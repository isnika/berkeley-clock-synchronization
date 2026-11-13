using Berkeley.Contracts;

namespace Berkeley.Engine.Tests.Fakes;

public class FakeNetworkTransport : INetworkTransport
{
    public List<(string TargetNodeId, MessageEnvelope Message)> SentMessages { get; } = [];
    public List<MessageEnvelope> BroadcastMessages { get; } = [];
    public List<string> ConnectedNodes { get; set; } = [];

    public event Action<MessageEnvelope>? MessageReceived;

    public Task<bool> SendAsync(string targetNodeId, MessageEnvelope message, CancellationToken ct = default)
    {
        SentMessages.Add((targetNodeId, message));
        return Task.FromResult(true);
    }

    public Task BroadcastAsync(MessageEnvelope message, CancellationToken ct = default)
    {
        BroadcastMessages.Add(message);
        return Task.FromResult(true);
    }

    public IReadOnlyList<string> GetConnectedNodeIds() => ConnectedNodes;

    /// <summary>Giả lập nhận một message từ network.</summary>
    public void SimulateReceive(MessageEnvelope message)
    {
        MessageReceived?.Invoke(message);
    }
}

public class FakeLeadershipService : ILeadershipService
{
    public LeaderSession? CurrentLeader { get; set; }
    public bool IAmMaster => CurrentLeader?.MasterNodeId == CurrentNodeId;
    public string CurrentNodeId { get; set; } = "Node1";

    public event Action<LeaderSession?>? LeaderChanged;

    public void SetLeader(string masterNodeId, long sessionId)
    {
        CurrentLeader = new LeaderSession(masterNodeId, sessionId);
        LeaderChanged?.Invoke(CurrentLeader);
    }
}

public class FakeAuditSink : IAuditSink
{
    public List<AuditEvent> Events { get; } = [];

    public void Record(AuditEvent auditEvent)
    {
        Events.Add(auditEvent);
    }
}

