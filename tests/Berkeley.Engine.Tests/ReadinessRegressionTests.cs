using Berkeley.Contracts;
using Berkeley.Engine.Clocks;
using Berkeley.Engine.Runtime;
using Berkeley.Engine.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Engine.Tests;

[TestClass]
public class ReadinessRegressionTests
{
    private readonly FakeClock _clock = new(1_000_000);
    private readonly FakeLeadershipService _leader = new() { CurrentNodeId = "Master" };
    private readonly FakeAuditSink _audit = new();
    private readonly NodeConfig _config = new("Master", "Master", "127.0.0.1", 8000, [],
        new NodeOperationParams(SampleTimeoutMs: 30, SampleMaxAgeMs: 5000, RoundTimeoutMs: 1000));

    private SyncRoundOrchestrator Engine(INetworkTransport transport)
    {
        _leader.SetLeader("Master", 1);
        return new(transport, _clock, _leader, _audit, _config, new("Master", Guid.NewGuid()));
    }

    [TestMethod]
    public async Task NoSamples_ResetsStateAndNextRoundSucceeds()
    {
        var transport = new ScriptedTransport(_clock) { DropAll = true };
        using var engine = Engine(transport);
        Assert.AreEqual(RoundOutcome.Failed, (await engine.RunRoundAsync())!.Outcome);
        Assert.AreEqual(RoundState.Idle, engine.CurrentState);
        transport.DropAll = false;
        Assert.AreEqual(RoundOutcome.Success, (await engine.RunRoundAsync())!.Outcome);
    }

    [DataTestMethod]
    [DataRow(120_000L)]
    [DataRow(-180_000L)]
    public async Task SingleWorkerResidual_SpreadIncludesMaster(long residual)
    {
        var transport = new ScriptedTransport(_clock) { Residual = residual };
        using var engine = Engine(transport);
        var summary = await engine.RunRoundAsync();
        Assert.AreEqual((double)Math.Abs(residual), summary!.RemeasuredSpreadMs);
    }

    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("invalid")]
    [DataRow("disconnected")]
    public async Task IncompleteVerification_DoesNotReportSuccess(string failure)
    {
        var transport = new ScriptedTransport(_clock) { Peers = ["Worker1", "Worker2"], VerificationFailure = failure };
        using var engine = Engine(transport);
        var summary = await engine.RunRoundAsync();
        Assert.AreEqual(2, summary!.AcceptedSamples.Count);
        Assert.IsNull(summary.RemeasuredSpreadMs);
        Assert.AreEqual(RoundOutcome.PartialSuccess, summary.Outcome);
    }

    [TestMethod]
    public async Task MissingInitialSample_DoesNotReportWholeRoundSuccess()
    {
        var transport = new ScriptedTransport(_clock) { Peers = ["Worker1", "Worker2"], MissingInitialWorker = true };
        using var engine = Engine(transport);
        var summary = await engine.RunRoundAsync();
        Assert.AreEqual(1, summary!.RejectedSamples.Count);
        Assert.AreEqual(RoundOutcome.PartialSuccess, summary.Outcome);
    }

    [DataTestMethod]
    [DataRow("Adjust")]
    [DataRow("Verifying")]
    public async Task CancelDuringLaterPhase_ResetsAndAllowsRetry(string phase)
    {
        var transport = new ScriptedTransport(_clock);
        using var engine = Engine(transport);
        using var cts = new CancellationTokenSource();
        transport.BeforeReply = (m, verification) =>
        {
            if ((phase == "Adjust" && m.MessageType == MessageTypes.Adjust) ||
                (phase == "Verifying" && verification)) cts.Cancel();
        };
        var summary = await engine.RunRoundAsync(cts.Token);
        Assert.AreEqual(RoundOutcome.Cancelled, summary!.Outcome);
        Assert.AreEqual(RoundState.Idle, engine.CurrentState);
        if (phase == "Adjust") Assert.AreEqual(0, _clock.ApplyHistory.Count);
        transport.BeforeReply = null;
        Assert.AreEqual(RoundOutcome.Success, (await engine.RunRoundAsync())!.Outcome);
    }

    [TestMethod]
    public async Task TransportException_ResetsAndAllowsRetry()
    {
        var transport = new ScriptedTransport(_clock);
        using var engine = Engine(transport);
        transport.BeforeReply = (_, _) => throw new IOException("test transport failure");
        await Assert.ThrowsExceptionAsync<IOException>(() => engine.RunRoundAsync());
        Assert.AreEqual(RoundState.Idle, engine.CurrentState);
        transport.BeforeReply = null;
        Assert.AreEqual(RoundOutcome.Success, (await engine.RunRoundAsync())!.Outcome);
    }

    // Replies are delivered inline for deterministic phase-boundary tests.
    // Applied replies deliberately leave a residual to test verification independently.
    private sealed class ScriptedTransport(FakeClock clock) : INetworkTransport
    {
        private readonly Dictionary<(string?, string), int> _requests = [];
        public List<string> Peers = ["Worker1"];
        public bool DropAll, MissingInitialWorker;
        public long Residual;
        public string? VerificationFailure;
        public Action<MessageEnvelope, bool>? BeforeReply;
        public event Action<MessageEnvelope>? MessageReceived;
        public IReadOnlyList<string> GetConnectedNodeIds() => Peers.ToList();
        public Task BroadcastAsync(MessageEnvelope m, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SendAsync(string id, MessageEnvelope m, CancellationToken ct = default)
        {
            if (m.MessageType == MessageTypes.TimeRequest)
            {
                var key = (m.RoundId, id);
                _requests.TryGetValue(key, out int count);
                _requests[key] = count + 1;
                bool verification = count > 0;
                BeforeReply?.Invoke(m, verification);
                if (DropAll || (MissingInitialWorker && id == "Worker2") ||
                    (verification && id == "Worker2" && VerificationFailure is "missing" or "disconnected"))
                    return Task.FromResult(VerificationFailure != "disconnected");
                long t = clock.ReadUtcMs();
                long t1 = m.DeserializePayload<TimeRequestPayload>()!.T1MasterSendUtcMs;
                long workerTime = t + (verification ? Residual : 0);
                long t3 = verification && id == "Worker2" && VerificationFailure == "invalid" ? workerTime + 10000 : workerTime;
                MessageReceived?.Invoke(MessageEnvelope.Create(MessageTypes.TimeResponse, id, Guid.NewGuid(), "Master",
                    new TimeResponsePayload(t1, workerTime, t3), m.RoundId, m.LeaderSessionId));
            }
            if (m.MessageType == MessageTypes.Adjust)
            {
                BeforeReply?.Invoke(m, false);
                if (VerificationFailure == "disconnected") Peers.Remove("Worker2");
                MessageReceived?.Invoke(MessageEnvelope.Create(MessageTypes.AdjustResult, id, Guid.NewGuid(), "Master",
                    new AdjustResultPayload(new(id, m.RoundId!, m.DeserializePayload<AdjustPayload>()!.AdjustmentMs,
                        0, 0, AdjustmentStatus.Applied)), m.RoundId, m.LeaderSessionId, messageId: m.MessageId));
            }
            return Task.FromResult(true);
        }
    }
}
