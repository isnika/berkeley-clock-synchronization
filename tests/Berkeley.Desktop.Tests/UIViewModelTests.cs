using Berkeley.Contracts;
using Berkeley.Desktop.Configuration;
using Berkeley.Desktop.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Desktop.Tests;

[TestClass]
public class UIViewModelTests
{
    [TestMethod]
    public void AppConfig_Create6LaptopNetworkConfig_GeneratesValidConfig()
    {
        var config = AppConfig.Create6LaptopNetworkConfig("Node3", "192.168.1.103");

        Assert.AreEqual("Node3", config.NodeId);
        Assert.AreEqual("192.168.1.103", config.ListenAddress);
        Assert.AreEqual(5000, config.ListenPort);
        Assert.AreEqual(5, config.Peers.Count);
        Assert.IsFalse(config.Peers.Any(p => p.NodeId == "Node3"));
        Assert.IsTrue(config.Peers.Any(p => p.NodeId == "Node1"));
        Assert.IsTrue(config.Peers.Any(p => p.NodeId == "Node6"));
    }

    [TestMethod]
    public void LogItemViewModel_FromAuditEvent_CorrectlyCategorizes()
    {
        var evtSent = new AuditEvent(1, DateTimeOffset.UtcNow, 10, "Node1", AuditEventKind.MessageSent, "Gửi test");
        var itemSent = LogItemViewModel.FromAuditEvent(evtSent);
        Assert.AreEqual("GỬI", itemSent.Category);
        Assert.IsTrue(itemSent.DirectionBadge.Contains("GỬI"));

        var evtStep = new AuditEvent(2, DateTimeOffset.UtcNow, 20, "Node2", AuditEventKind.ClockAdjusted, "Step test");
        var itemStep = LogItemViewModel.FromAuditEvent(evtStep);
        Assert.AreEqual("STEP", itemStep.Category);

        var evtBully = new AuditEvent(3, DateTimeOffset.UtcNow, 30, "Node6", AuditEventKind.LeaderChanged, "Bully test");
        var itemBully = LogItemViewModel.FromAuditEvent(evtBully);
        Assert.AreEqual("BULLY", itemBully.Category);
    }

    [TestMethod]
    public void NodeCardViewModel_UpdateFromPeerStatus_UpdatesPropertiesCorrectly()
    {
        var card = new NodeCardViewModel
        {
            NodeId = "Node2",
            DisplayName = "Trạm Node 2"
        };

        var peerStatus = new PeerStatus(
            NodeId: "Node2",
            DisplayName: "Laptop Node 2",
            ConnectionState: PeerConnectionState.Connected,
            LastRttMs: 12.5,
            LastOffsetMs: -1500.0, // -1.5s
            LastAdjustmentMs: 1500.0,
            LastSeenAt: DateTimeOffset.UtcNow
        );

        card.UpdateFromPeerStatus(peerStatus, currentMasterId: "Node1", currentNodeId: "Node1");

        Assert.IsTrue(card.IsConnected);
        Assert.AreEqual("ONLINE", card.ConnectionStateText);
        Assert.AreEqual("12.5 ms", card.LastRttText);
        Assert.AreEqual("-1.50 s", card.LastOffsetText);
        Assert.AreEqual("+1.50 s", card.LastAdjustmentText);
    }

    [TestMethod]
    public void NodeCardViewModel_SetAdjustmentResult_SetsVisualText()
    {
        var card = new NodeCardViewModel();

        card.SetAdjustmentResult(AdjustmentStatus.Applied, 2500);
        Assert.IsTrue(card.AdjustmentStatusText.Contains("Đã áp dụng"));

        card.SetAdjustmentResult(AdjustmentStatus.PermissionDenied, 1000);
        Assert.IsTrue(card.AdjustmentStatusText.Contains("Thiếu quyền"));

        card.SetAdjustmentResult(AdjustmentStatus.Failed, 1000);
        Assert.IsTrue(card.AdjustmentStatusText.Contains("Lỗi"));
    }
}
