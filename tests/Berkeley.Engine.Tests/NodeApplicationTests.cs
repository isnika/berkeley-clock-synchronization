using Berkeley.Contracts;
using Berkeley.Engine.Clocks;
using Berkeley.Engine.Runtime;
using Berkeley.Engine.Tests.Fakes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Engine.Tests;

[TestClass]
public class NodeApplicationTests
{
    private FakeClock _clock = null!;
    private FakeNetworkTransport _transport = null!;
    private FakeLeadershipService _leadership = null!;
    private FakeAuditSink _audit = null!;
    private NodeApplication _app = null!;
    private NodeConfig _config = null!;

    [TestInitialize]
    public void Setup()
    {
        _clock = new FakeClock(initialUtcMs: 100_000, hasPermission: true);
        _transport = new FakeNetworkTransport();
        _leadership = new FakeLeadershipService { CurrentNodeId = "Node1" };
        _audit = new FakeAuditSink();
        _app = new NodeApplication(_clock, _transport, _leadership, _audit);

        _config = new NodeConfig(
            NodeId: "Node1",
            DisplayName: "Node 1",
            ListenAddress: "127.0.0.1",
            ListenPort: 8000,
            Peers: [new PeerConfig("Node2", "127.0.0.1", 8001)],
            OperationParams: new NodeOperationParams()
        );
    }

    [TestCleanup]
    public void Cleanup()
    {
        _app.Dispose();
    }

    [TestMethod]
    [Description("GetSnapshot trước khi StartAsync trả về Role = Standalone")]
    public void GetSnapshot_BeforeStart_ReturnsStandalone()
    {
        var snapshot = _app.GetSnapshot();

        Assert.AreEqual(NodeRole.Standalone, snapshot.Role);
        Assert.AreEqual(100_000L, snapshot.LocalClockUtcMs);
        Assert.IsTrue(snapshot.HasSetTimePermission);
    }

    [TestMethod]
    [Description("StartAsync cập nhật Role theo LeadershipService và kích hoạt SnapshotChanged")]
    public async Task StartAsync_UpdatesRoleAndFiresSnapshotChanged()
    {
        bool snapshotFired = false;
        _app.SnapshotChanged += snap => snapshotFired = true;

        _leadership.SetLeader("Node1", sessionId: 1); // Node1 là Master
        await _app.StartAsync(_config);

        Assert.IsTrue(_app.IsRunning);
        Assert.IsTrue(snapshotFired);

        var snapshot = _app.GetSnapshot();
        Assert.AreEqual(NodeRole.Master, snapshot.Role);
        Assert.AreEqual("Node1", snapshot.NodeId);
        Assert.AreEqual(1, snapshot.KnownPeers.Count);
        Assert.AreEqual(PeerConnectionState.Disconnected, snapshot.KnownPeers[0].ConnectionState);
    }

    [TestMethod]
    [Description("TriggerSyncNowAsync trả về false nếu Node không phải là Master")]
    public async Task TriggerSyncNowAsync_NotMaster_ReturnsFalse()
    {
        _leadership.SetLeader("OtherNode", sessionId: 1);
        await _app.StartAsync(_config);

        bool success = await _app.TriggerSyncNowAsync();

        Assert.IsFalse(success);
    }

    [TestMethod]
    [Description("StopAsync đưa Node về Standalone và giải phóng tài nguyên")]
    public async Task StopAsync_StopsNodeAndResetsRole()
    {
        await _app.StartAsync(_config);
        Assert.IsTrue(_app.IsRunning);

        await _app.StopAsync();
        Assert.IsFalse(_app.IsRunning);

        var snapshot = _app.GetSnapshot();
        Assert.AreEqual(NodeRole.Standalone, snapshot.Role);
    }
}

