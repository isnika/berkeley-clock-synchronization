using Berkeley.Contracts;

namespace Berkeley.Desktop.Services.RealNetwork;

/// <summary>
/// Dịch vụ quản lý vai trò lãnh đạo và giải thuật bầu cử Bully (Lớn trị bé)
/// khi chạy thực tế trên cụm máy tính trong mạng LAN/Wi-Fi.
/// </summary>
public sealed class RealBullyLeadershipService : ILeadershipService, IDisposable
{
    private readonly string _currentNodeId;
    private readonly INetworkTransport _transport;
    private readonly IClockReader _clock;
    private readonly IAuditSink _audit;
    private readonly NodeConfig _config;

    private LeaderSession? _currentLeader;
    private readonly CancellationTokenSource _cts = new();
    private bool _isElecting;
    private bool _disposed;

    public LeaderSession? CurrentLeader => _currentLeader;
    public bool IAmMaster => _currentLeader?.MasterNodeId == _currentNodeId;

    public event Action<LeaderSession?>? LeaderChanged;

    public RealBullyLeadershipService(
        NodeConfig config,
        INetworkTransport transport,
        IClockReader clock,
        IAuditSink audit)
    {
        _config = config;
        _currentNodeId = config.NodeId;
        _transport = transport;
        _clock = clock;
        _audit = audit;

        // Đăng ký nhận tin nhắn bầu cử từ mạng
        _transport.MessageReceived += OnMessageReceived;

        // Mặc định ban đầu node có ID cao nhất trong cấu hình là Master
        var highestNode = config.Peers.Select(p => p.NodeId)
            .Append(_currentNodeId)
            .OrderByDescending(id => id)
            .FirstOrDefault() ?? "Node6";

        _currentLeader = new LeaderSession(highestNode, 1);
    }

    public void SetLeader(string masterNodeId, long sessionId)
    {
        _currentLeader = new LeaderSession(masterNodeId, sessionId);
        _audit.Record(new AuditEvent(
            Sequence: 0,
            OccurredAt: DateTimeOffset.UtcNow,
            MonotonicMs: _clock.ReadMonotonicMs(),
            SourceNodeId: _currentNodeId,
            Kind: AuditEventKind.LeaderChanged,
            Summary: $"LÃNH ĐẠO ĐỔI: Master mới là {masterNodeId} (Phiên #{sessionId})"
        ));

        LeaderChanged?.Invoke(_currentLeader);
    }

    public async Task StartElectionAsync()
    {
        if (_isElecting) return;
        _isElecting = true;

        long newSessionId = (_currentLeader?.LeaderSessionId ?? 0) + 1;

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _currentNodeId, AuditEventKind.LeaderChanged,
            $"[BULLY] {_currentNodeId} khởi xướng bầu cử Master mới (Phiên #{newSessionId})"));

        // Tìm các node có ID lớn hơn
        var higherNodes = _config.Peers
            .Where(p => string.Compare(p.NodeId, _currentNodeId, StringComparison.OrdinalIgnoreCase) > 0)
            .Select(p => p.NodeId)
            .ToList();

        if (higherNodes.Count == 0)
        {
            // Không có ai cao hơn tôi -> Tôi làm Master!
            await DeclareCoordinatorAsync(newSessionId);
            _isElecting = false;
            return;
        }

        // Gửi ELECTION tới các node cao hơn
        bool receivedOk = false;
        var electionMsg = MessageEnvelope.Create(
            messageType: MessageTypes.Election,
            sourceNodeId: _currentNodeId,
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "*",
            payload: new { Session = newSessionId }
        );

        foreach (var node in higherNodes)
        {
            bool sent = await _transport.SendAsync(node, electionMsg, _cts.Token);
            if (sent) receivedOk = true;
        }

        if (!receivedOk)
        {
            // Các node cao hơn không phản hồi -> Tôi làm Master
            await DeclareCoordinatorAsync(newSessionId);
        }

        _isElecting = false;
    }

    private async Task DeclareCoordinatorAsync(long sessionId)
    {
        SetLeader(_currentNodeId, sessionId);

        var coordMsg = MessageEnvelope.Create(
            messageType: MessageTypes.Coordinator,
            sourceNodeId: _currentNodeId,
            sourceBootId: Guid.NewGuid(),
            targetNodeId: "*",
            payload: new { Master = _currentNodeId, Session = sessionId }
        );

        await _transport.BroadcastAsync(coordMsg, _cts.Token);

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _currentNodeId, AuditEventKind.LeaderChanged,
            $"👑 [BULLY] {_currentNodeId} đã tuyên bố trở thành MASTER mới (Phiên #{sessionId})"));
    }

    private void OnMessageReceived(MessageEnvelope msg)
    {
        if (msg.MessageType == MessageTypes.Coordinator)
        {
            long session = msg.LeaderSessionId ?? ((_currentLeader?.LeaderSessionId ?? 0) + 1);
            SetLeader(msg.SourceNodeId, session);
        }
        else if (msg.MessageType == MessageTypes.Election)
        {
            // Nếu ID của tôi cao hơn người gửi -> Trả lời OK
            if (string.Compare(_currentNodeId, msg.SourceNodeId, StringComparison.OrdinalIgnoreCase) > 0)
            {
                var okMsg = MessageEnvelope.Create(
                    messageType: MessageTypes.Ok,
                    sourceNodeId: _currentNodeId,
                    sourceBootId: Guid.NewGuid(),
                    targetNodeId: msg.SourceNodeId,
                    payload: new { Status = "OK" }
                );
                _ = _transport.SendAsync(msg.SourceNodeId, okMsg, _cts.Token);

                // Khởi xướng tiếp bầu cử của mình
                _ = StartElectionAsync();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.MessageReceived -= OnMessageReceived;
        _cts.Cancel();
        _cts.Dispose();
    }
}
