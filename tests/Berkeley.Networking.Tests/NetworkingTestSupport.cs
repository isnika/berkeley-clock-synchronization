using System.Collections.Concurrent;
using System.Diagnostics;
using Berkeley.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Networking.Tests;

internal sealed class TestClock : IClockReader, ISystemClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly long _initialUtcMs;
    private long _adjustmentMs;

    public TestClock(long initialUtcMs = 1_700_000_000_000)
    {
        _initialUtcMs = initialUtcMs;
    }

    public long ReadUtcMs() => _initialUtcMs + _stopwatch.ElapsedMilliseconds + Interlocked.Read(ref _adjustmentMs);

    public long ReadMonotonicMs() => _stopwatch.ElapsedMilliseconds;

    public AdjustmentResult ApplyStep(string nodeId, string roundId, double adjustmentMs)
    {
        long before = ReadUtcMs();
        Interlocked.Add(ref _adjustmentMs, (long)Math.Round(adjustmentMs));
        long after = ReadUtcMs();
        return new AdjustmentResult(
            nodeId,
            roundId,
            adjustmentMs,
            before,
            after,
            AdjustmentStatus.Applied);
    }

    public bool HasSetTimePermission() => true;
}

internal sealed class TestAuditSink : IAuditSink
{
    public ConcurrentQueue<AuditEvent> Events { get; } = new();

    public void Record(AuditEvent auditEvent) => Events.Enqueue(auditEvent);
}

internal sealed class RecordingTransport : INetworkTransport
{
    public List<(string TargetNodeId, MessageEnvelope Message)> Sent { get; } = [];
    public List<MessageEnvelope> Broadcasts { get; } = [];
    public IReadOnlyList<string> ConnectedNodeIds { get; set; } = [];
    public bool SendResult { get; set; } = true;

    public event Action<MessageEnvelope>? MessageReceived;

    public Task<bool> SendAsync(
        string targetNodeId,
        MessageEnvelope message,
        CancellationToken ct = default)
    {
        Sent.Add((targetNodeId, message));
        return Task.FromResult(SendResult);
    }

    public Task BroadcastAsync(MessageEnvelope message, CancellationToken ct = default)
    {
        Broadcasts.Add(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetConnectedNodeIds() => ConnectedNodeIds;

    public void Raise(MessageEnvelope message) => MessageReceived?.Invoke(message);
}

internal static class NetworkingTestHelpers
{
    public static NodeConfig Config(
        string nodeId,
        int port,
        IEnumerable<(string NodeId, int Port)> peers,
        int heartbeatIntervalMs = 40,
        int heartbeatTimeoutMs = 180,
        int electionTimeoutMs = 90) =>
        new(
            NodeId: nodeId,
            DisplayName: nodeId,
            ListenAddress: "127.0.0.1",
            ListenPort: port,
            Peers: peers.Select(peer => new PeerConfig(peer.NodeId, "127.0.0.1", peer.Port)).ToList(),
            OperationParams: new NodeOperationParams(
                HeartbeatIntervalMs: heartbeatIntervalMs,
                HeartbeatTimeoutMs: heartbeatTimeoutMs,
                ElectionTimeoutMs: electionTimeoutMs,
                SampleTimeoutMs: 100,
                SampleMaxAgeMs: 1_000,
                RoundTimeoutMs: 2_000));

    public static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            port: 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var deadline = Stopwatch.GetTimestamp() +
                       (long)(timeout.TotalSeconds * Stopwatch.Frequency);

        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                Assert.Fail(failureMessage);

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    public static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
