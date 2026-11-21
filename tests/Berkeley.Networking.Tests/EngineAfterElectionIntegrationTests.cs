using Berkeley.Contracts;
using Berkeley.Engine.Runtime;
using Berkeley.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Networking.Tests;

[TestClass]
public sealed class EngineAfterElectionIntegrationTests
{
    [TestMethod]
    public async Task ElectedNode2_CanRunEngineRoundThroughManualApi()
    {
        var ports = new HashSet<int>();
        while (ports.Count < 3) ports.Add(NetworkingTestHelpers.FreePort());
        var portArray = ports.ToArray();
        int node1Port = portArray[0], node2Port = portArray[1], node3Port = portArray[2];
        var peers = new[]
        {
            ("Node1", node1Port),
            ("Node2", node2Port),
            ("Node3", node3Port)
        };

        var config1 = Config("Node1", node1Port);
        var config2 = Config("Node2", node2Port);
        var config3 = Config("Node3", node3Port);

        NodeConfig Config(string id, int port)
        {
            // Allow scheduling jitter when all solution test assemblies run together.
            var config = NetworkingTestHelpers.Config(id, port, peers.Where(p => p.Item1 != id), 100, 1500, 400);
            return config with { OperationParams = config.OperationParams with
                { SampleTimeoutMs = 1000, SampleMaxAgeMs = 5000, RoundTimeoutMs = 10000 } };
        }
        var clock1 = new TestClock(1_700_000_120_000);
        var clock2 = new TestClock();
        var clock3 = new TestClock();
        var audit1 = new TestAuditSink();
        var audit2 = new TestAuditSink();
        var audit3 = new TestAuditSink();

        await using var transport1 = new TcpNetworkTransport(config1, clock1, audit1, autoStart: false);
        await using var transport2 = new TcpNetworkTransport(config2, clock2, audit2, autoStart: false);
        await using var transport3 = new TcpNetworkTransport(config3, clock3, audit3, autoStart: false);
        await transport1.StartAsync();
        await transport2.StartAsync();
        await transport3.StartAsync();

        await using var leadership1 = new BullyLeadershipService(config1, transport1, clock1, audit1);
        await using var leadership2 = new BullyLeadershipService(config2, transport2, clock2, audit2);
        await using var leadership3 = new BullyLeadershipService(config3, transport3, clock3, audit3);

        await NetworkingTestHelpers.WaitUntilAsync(
            () => transport1.GetConnectedNodeIds().Count == 2 &&
                  transport2.GetConnectedNodeIds().Count == 2 &&
                  transport3.GetConnectedNodeIds().Count == 2,
            TimeSpan.FromSeconds(3),
            "The three TCP transports did not become connected.");

        await leadership3.DisposeAsync();
        await transport3.StopAsync();

        await NetworkingTestHelpers.WaitUntilAsync(
            () => leadership1.CurrentLeader?.MasterNodeId == "Node2" &&
                  leadership2.CurrentLeader?.MasterNodeId == "Node2" &&
                  leadership1.CurrentLeader?.LeaderSessionId == leadership2.CurrentLeader?.LeaderSessionId &&
                  leadership1.CurrentLeader?.LeaderSessionId is > 1 &&
                  !leadership1.IsElecting && !leadership2.IsElecting &&
                  transport1.GetConnectedNodeIds().SequenceEqual(new[] { "Node2" }) &&
                  transport2.GetConnectedNodeIds().SequenceEqual(new[] { "Node1" }),
            TimeSpan.FromSeconds(5),
            "Node1 and Node2 did not elect Node2.");

        using var node1 = new NodeApplication(clock1, transport1, leadership1, audit1);
        using var node2 = new NodeApplication(clock2, transport2, leadership2, audit2);
        await node1.StartAsync(config1);
        await node2.StartAsync(config2);

        RoundSummary? summary = null;
        node2.RoundCompleted += result => summary = result;
        bool completed = await node2.TriggerSyncNowAsync();

        Assert.IsTrue(completed,
            "The elected coordinator could not complete a Berkeley round through the existing manual API.");
        Assert.IsNotNull(summary);
        Assert.AreEqual(RoundOutcome.Success, summary.Outcome, string.Join("\n", audit2.Events.Select(e => e.Summary)));
        Assert.AreEqual(2, summary.AdjustmentResults.Count);
        Assert.IsTrue(summary.AdjustmentResults.All(r => r.Status == AdjustmentStatus.Applied));
        Assert.IsTrue(summary.RemeasuredSpreadMs is >= 0 and < 100);
        Assert.IsTrue(Math.Abs(clock1.ReadUtcMs() - clock2.ReadUtcMs()) < 100);

        await node2.StopAsync();
        await node1.StopAsync();
    }
}
