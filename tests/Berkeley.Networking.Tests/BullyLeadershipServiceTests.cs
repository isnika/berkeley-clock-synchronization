using Berkeley.Contracts;
using Berkeley.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Berkeley.Networking.Tests;

[TestClass]
public sealed class BullyLeadershipServiceTests
{
    [TestMethod]
    public async Task Election_SentSuccessfullyWithoutOk_WaitsThenDeclaresCoordinator()
    {
        var config = NetworkingTestHelpers.Config(
            "Node1",
            5001,
            [("Node2", 5002)],
            electionTimeoutMs: 70);
        var transport = new RecordingTransport { SendResult = true };
        var audit = new TestAuditSink();
        await using var leadership = new BullyLeadershipService(
            config,
            transport,
            new TestClock(),
            audit,
            autoStart: false);

        var changed = new TaskCompletionSource<LeaderSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        leadership.LeaderChanged += leader =>
        {
            if (leader is not null)
                changed.TrySetResult(leader);
        };

        await leadership.StartElectionAsync();
        var result = await changed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual("Node1", result.MasterNodeId);
        Assert.AreEqual(2L, result.LeaderSessionId);

        var election = transport.Sent.Single(message =>
            message.Message.MessageType == MessageTypes.Election).Message;
        Assert.AreEqual(2L, election.LeaderSessionId);
        Assert.AreEqual(1, transport.Broadcasts.Count);
        Assert.AreEqual(MessageTypes.Coordinator, transport.Broadcasts[0].MessageType);
        Assert.AreEqual(2L, transport.Broadcasts[0].LeaderSessionId);
    }

    [TestMethod]
    public async Task CoordinatorMessage_UsesTopLevelSessionAndFiresOnlyOnRealChange()
    {
        var config = NetworkingTestHelpers.Config(
            "Node1",
            5001,
            [("Node2", 5002)],
            electionTimeoutMs: 100);
        var transport = new RecordingTransport();
        await using var leadership = new BullyLeadershipService(
            config,
            transport,
            new TestClock(),
            new TestAuditSink(),
            autoStart: false);

        int changes = 0;
        leadership.LeaderChanged += _ => changes++;

        var coordinator = MessageEnvelope.Create(
            messageType: MessageTypes.Coordinator,
            sourceNodeId: "Node2",
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "*",
            payload: new ElectionPayload("Node2", 7),
            leaderSessionId: 7);

        transport.Raise(coordinator);
        transport.Raise(coordinator);

        Assert.AreEqual("Node2", leadership.CurrentLeader?.MasterNodeId);
        Assert.AreEqual(7L, leadership.CurrentLeader?.LeaderSessionId);
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public async Task RecoveringHigherNode_ObservesElectionTermBeforeDeclaring()
    {
        var transport = new RecordingTransport();
        await using var leadership = new BullyLeadershipService(
            NetworkingTestHelpers.Config("Node2", 5002, [("Node1", 5001)]),
            transport, new TestClock(), new TestAuditSink(), autoStart: false);
        transport.Raise(Control(MessageTypes.Election, "Node1", 41));
        await NetworkingTestHelpers.WaitUntilAsync(() => !leadership.IsElecting,
            TimeSpan.FromSeconds(1), "Election did not finish.");
        Assert.AreEqual(new LeaderSession("Node2", 42), leadership.CurrentLeader);
        Assert.AreEqual(42L, transport.Broadcasts.Single().LeaderSessionId);
        Assert.AreEqual(41L, transport.Sent.Single().Message.LeaderSessionId);
    }

    [TestMethod]
    public async Task RecoveringHigherNode_LearnsSurvivorThenReclaimsInNewTerm()
    {
        var transport = new RecordingTransport();
        await using var leadership = new BullyLeadershipService(
            NetworkingTestHelpers.Config("Node2", 5002, [("Node1", 5001)]),
            transport, new TestClock(), new TestAuditSink(), autoStart: false);
        transport.Raise(Control(MessageTypes.Coordinator, "Node1", 25));
        await NetworkingTestHelpers.WaitUntilAsync(() => !leadership.IsElecting,
            TimeSpan.FromSeconds(1), "Reclaim did not finish.");
        Assert.AreEqual(new LeaderSession("Node2", 26), leadership.CurrentLeader);
        transport.Raise(Control(MessageTypes.Coordinator, "Node1", 25));
        transport.Raise(Control(MessageTypes.Coordinator, "Node1", 1));
        Assert.AreEqual(new LeaderSession("Node2", 26), leadership.CurrentLeader);
        Assert.AreEqual(1, transport.Broadcasts.Count);
    }

    [TestMethod]
    public async Task NewCoordinatorDuringElection_PreventsObsoleteSelfDeclaration()
    {
        var transport = new RecordingTransport();
        await using var leadership = new BullyLeadershipService(
            NetworkingTestHelpers.Config("Node1", 5001, [("Node2", 5002)]),
            transport, new TestClock(), new TestAuditSink(), autoStart: false);
        var election = leadership.StartElectionAsync();
        transport.Raise(Control(MessageTypes.Coordinator, "Node2", 50));
        await election.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(new LeaderSession("Node2", 50), leadership.CurrentLeader);
        Assert.AreEqual(0, transport.Broadcasts.Count);
        transport.Raise(Control(MessageTypes.Coordinator, "Node2", 2));
        Assert.AreEqual(50L, leadership.CurrentLeader!.LeaderSessionId);
    }

    [TestMethod]
    public async Task CoordinatorReplay_RepairsLostAnnouncementWithoutOpeningNewTerm()
    {
        var transport = new RecordingTransport();
        var followerTransport = new RecordingTransport();
        await using var follower = new BullyLeadershipService(
            NetworkingTestHelpers.Config("Node1", 5001, [("Node2", 5002)]),
            followerTransport, new TestClock(), new TestAuditSink(), autoStart: false);
        var repaired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        follower.LeaderChanged += _ => repaired.TrySetResult(true);
        int announcements = 0;
        transport.OnBroadcast = message =>
        {
            // Drop the first declaration, as when the peer is still offline.
            if (Interlocked.Increment(ref announcements) > 1)
                followerTransport.Raise(message);
        };
        var leadership = new BullyLeadershipService(
            NetworkingTestHelpers.Config("Node2", 5002, [("Node1", 5001)]),
            transport, new TestClock(), new TestAuditSink());
        int changes = 0;
        leadership.LeaderChanged += _ => changes++;
        try
        {
            await leadership.StartElectionAsync();
            await repaired.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await leadership.DisposeAsync(); }
        Assert.IsTrue(transport.Broadcasts.Count >= 2, "An offline peer needs repeated announcements.");
        Assert.IsTrue(transport.Broadcasts.All(message => message.MessageType == MessageTypes.Coordinator &&
            message.SourceNodeId == "Node2" && message.LeaderSessionId == 2));
        Assert.AreEqual(leadership.CurrentLeader, follower.CurrentLeader);
        Assert.AreEqual(1, changes, "Only the election changes leadership; replay must not churn sessions.");
    }

    [TestMethod]
    public async Task StopDuringElection_CancelsBeforeDisposingGate()
    {
        var leadership = new BullyLeadershipService(
            NetworkingTestHelpers.Config("Node1", 5001, [("Node2", 5002)], electionTimeoutMs: 5000),
            new RecordingTransport(), new TestClock(), new TestAuditSink(), autoStart: false);
        var election = leadership.StartElectionAsync();
        await leadership.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        await election;
        Assert.IsFalse(leadership.IsElecting);
    }

    private static MessageEnvelope Control(string type, string source, long session) =>
        MessageEnvelope.Create(type, source, Guid.NewGuid(), "*", new ElectionPayload(source, session),
            leaderSessionId: session);
}
