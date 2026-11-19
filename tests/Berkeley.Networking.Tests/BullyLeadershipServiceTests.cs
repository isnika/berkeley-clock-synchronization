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
}
