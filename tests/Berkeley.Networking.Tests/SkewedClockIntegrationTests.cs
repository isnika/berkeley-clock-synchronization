using Berkeley.Contracts;
using Berkeley.Engine.Runtime;
using Berkeley.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Networking.Tests;

[TestClass]
public sealed class SkewedClockIntegrationTests
{
    [DataTestMethod]
    [DataRow(120_000L)]
    [DataRow(-180_000L)]
    public async Task RealTcp_LargeClockSkew_ConvergesAndCanSyncAgain(long skewMs)
    {
        int port1 = NetworkingTestHelpers.FreePort();
        int port2;
        do { port2 = NetworkingTestHelpers.FreePort(); } while (port2 == port1);
        var config1 = NetworkingTestHelpers.Config("Node1", port1, [("Node2", port2)], 100, 2000, 500);
        var config2 = NetworkingTestHelpers.Config("Node2", port2, [("Node1", port1)], 100, 2000, 500);
        var clock1 = new TestClock(1_700_000_000_000 + skewMs);
        var clock2 = new TestClock();
        var audit1 = new TestAuditSink();
        var audit2 = new TestAuditSink();
        await using var transport1 = new TcpNetworkTransport(config1, clock1, audit1, autoStart: false);
        await using var transport2 = new TcpNetworkTransport(config2, clock2, audit2, autoStart: false);
        await transport1.StartAsync();
        await transport2.StartAsync();
        await using var leadership1 = new BullyLeadershipService(config1, transport1, clock1, audit1);
        await using var leadership2 = new BullyLeadershipService(config2, transport2, clock2, audit2);
        await NetworkingTestHelpers.WaitUntilAsync(() => transport1.GetConnectedNodeIds().Count == 1 &&
            transport2.GetConnectedNodeIds().Count == 1 && leadership1.CurrentLeader?.MasterNodeId == "Node2" &&
            leadership1.CurrentLeader?.LeaderSessionId == leadership2.CurrentLeader?.LeaderSessionId,
            TimeSpan.FromSeconds(5), "Two nodes did not agree on the coordinator.");

        using var node1 = new NodeApplication(clock1, transport1, leadership1, audit1);
        using var node2 = new NodeApplication(clock2, transport2, leadership2, audit2);
        await node1.StartAsync(config1);
        await node2.StartAsync(config2);
        var rounds = new List<RoundSummary>();
        node2.RoundCompleted += rounds.Add;
        try
        {
            Assert.IsTrue(Math.Abs(clock1.ReadUtcMs() - clock2.ReadUtcMs()) > 100_000);
            for (int i = 0; i < 2; i++)
            {
                Assert.IsTrue(await node2.TriggerSyncNowAsync());
                var summary = rounds[i];
                Assert.AreEqual(RoundOutcome.Success, summary.Outcome);
                Assert.AreEqual(1, summary.AcceptedSamples.Count);
                Assert.AreEqual(2, summary.AdjustmentResults.Count);
                Assert.IsTrue(summary.AdjustmentResults.All(r => r.Status == AdjustmentStatus.Applied));
                Assert.IsTrue(summary.RemeasuredSpreadMs is >= 0 and < 100);
                Assert.IsTrue(Math.Abs(clock1.ReadUtcMs() - clock2.ReadUtcMs()) < 100);
            }
        }
        finally
        {
            await node2.StopAsync();
            await node1.StopAsync();
        }
    }
}
