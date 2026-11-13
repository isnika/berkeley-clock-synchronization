using Berkeley.Contracts;
using Berkeley.Engine.Clocks;
using Berkeley.Engine.Runtime;
using Berkeley.Engine.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Engine.Tests;

[TestClass]
public class SyncRoundOrchestratorTests
{
    private FakeNetworkTransport _transport = null!;
    private FakeClock _masterClock = null!;
    private FakeLeadershipService _leadership = null!;
    private FakeAuditSink _audit = null!;
    private NodeConfig _config = null!;
    private NodeIdentity _identity = null!;
    private SyncRoundOrchestrator _orchestrator = null!;

    [TestInitialize]
    public void Setup()
    {
        _transport = new FakeNetworkTransport();
        _masterClock = new FakeClock(initialUtcMs: 10_000, hasPermission: true);
        _leadership = new FakeLeadershipService { CurrentNodeId = "Master" };
        _leadership.SetLeader("Master", sessionId: 1);
        _audit = new FakeAuditSink();
        _identity = new NodeIdentity("Master", Guid.NewGuid());
        _config = new NodeConfig(
            NodeId: "Master",
            DisplayName: "Master Node",
            ListenAddress: "127.0.0.1",
            ListenPort: 8000,
            Peers: [new PeerConfig("Worker1", "127.0.0.1", 8001)],
            OperationParams: new NodeOperationParams(
                SampleTimeoutMs: 50,  // Ngắn để test chạy nhanh
                SampleMaxAgeMs: 5000,
                RoundTimeoutMs: 1000
            )
        );

        _orchestrator = new SyncRoundOrchestrator(
            _transport, _masterClock, _leadership, _audit, _config, _identity);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _orchestrator.Dispose();
    }

    [TestMethod]
    [Description("Không phải Master -> RunRoundAsync trả về null ngay")]
    public async Task RunRoundAsync_NotMaster_ReturnsNull()
    {
        _leadership.SetLeader("OtherNode", sessionId: 1);

        var result = await _orchestrator.RunRoundAsync();

        Assert.IsNull(result);
    }

    [TestMethod]
    [Description("Đổi Master giữa vòng -> Vòng bị hủy với kết quả Cancelled")]
    public async Task RunRoundAsync_LeaderChangedMidRound_ReturnsCancelled()
    {
        _transport.ConnectedNodes = ["Worker1"];

        // Chạy vòng bất đồng bộ, sau đó kích hoạt đổi Leader
        var roundTask = _orchestrator.RunRoundAsync();

        await Task.Delay(10);
        _leadership.SetLeader("OtherMaster", sessionId: 2);

        var summary = await roundTask;

        Assert.IsNotNull(summary);
        Assert.AreEqual(RoundOutcome.Cancelled, summary.Outcome);
    }

    [TestMethod]
    [Description("Một vòng hoàn chỉnh giữa Master và 1 Worker -> Thành công Success")]
    public async Task RunRoundAsync_WithWorker_CompletesSuccessfully()
    {
        _transport.ConnectedNodes = ["Worker1"];

        // Setup Worker với WorkerSyncHandler
        var workerClock = new FakeClock(initialUtcMs: 10_100); // Worker nhanh hơn 100ms
        var workerIdentity = new NodeIdentity("Worker1", Guid.NewGuid());
        var workerConfig = new NodeConfig(
            NodeId: "Worker1",
            DisplayName: "Worker 1",
            ListenAddress: "127.0.0.1",
            ListenPort: 8001,
            Peers: [],
            OperationParams: _config.OperationParams
        );

        // Nối transport giữa Master và Worker
        using var workerHandler = new WorkerSyncHandler(
            _transport, workerClock, _leadership, _audit, workerConfig, workerIdentity);

        // Khi Master gửi message tới Worker1, transport chuyển tiếp tới workerHandler
        // Khi Worker gửi message tới Master, transport chuyển tiếp tới orchestrator
        var origSent = _transport.SentMessages;
        int lastHandledIndex = 0;

        var pumpTask = Task.Run(async () =>
        {
            while (_orchestrator.CurrentState != RoundState.Idle || lastHandledIndex == 0)
            {
                while (lastHandledIndex < origSent.Count)
                {
                    var (target, msg) = origSent[lastHandledIndex++];
                    _transport.SimulateReceive(msg);
                }
                await Task.Delay(5);
            }
        });

        var summary = await _orchestrator.RunRoundAsync();
        await pumpTask;

        Assert.IsNotNull(summary);
        Assert.AreEqual(RoundOutcome.Success, summary.Outcome);
        Assert.AreEqual(1, summary.AcceptedSamples.Count);
        Assert.AreEqual(2, summary.AdjustmentResults.Count); // Master + Worker
    }

    [TestMethod]
    [Description("Worker không phản hồi -> Ghi timeout và kết thúc vòng")]
    public async Task RunRoundAsync_WorkerTimeout_RecordsTimeoutSample()
    {
        _transport.ConnectedNodes = ["WorkerUnresponsive"];

        // Không có ai trả lời message
        var summary = await _orchestrator.RunRoundAsync();

        Assert.IsNotNull(summary);
        // Vì không có mẫu hợp lệ nào -> Failed
        Assert.AreEqual(RoundOutcome.Failed, summary.Outcome);
        Assert.AreEqual(1, summary.RejectedSamples.Count);
        Assert.AreEqual(SampleStatus.RejectedTimeout, summary.RejectedSamples[0].Status);
    }
}

