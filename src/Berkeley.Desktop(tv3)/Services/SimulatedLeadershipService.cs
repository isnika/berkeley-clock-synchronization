using Berkeley.Contracts;

namespace Berkeley.Desktop.Services;

/// <summary>
/// Dịch vụ quản lý vai trò lãnh đạo và mô phỏng thuật toán bầu chọn Bully khi Master bị sự cố.
/// </summary>
public sealed class SimulatedLeadershipService : ILeadershipService
{
    private readonly string _currentNodeId;
    private readonly IAuditSink _audit;
    private readonly IClockReader _clock;
    private LeaderSession? _currentLeader;

    public LeaderSession? CurrentLeader => _currentLeader;
    public bool IAmMaster => _currentLeader?.MasterNodeId == _currentNodeId;

    public event Action<LeaderSession?>? LeaderChanged;

    public SimulatedLeadershipService(string currentNodeId, IClockReader clock, IAuditSink audit)
    {
        _currentNodeId = currentNodeId;
        _clock = clock;
        _audit = audit;

        // Mặc định Node 1 khởi đầu là Master phiên #1
        _currentLeader = new LeaderSession("Node1", 1);
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

    /// <summary>
    /// Giả lập kịch bản Master bị Crash -> Kích hoạt bầu cử Bully (Lớn trị bé)
    /// </summary>
    public async Task TriggerBullyElectionSimulationAsync(Action<string> stepReporter)
    {
        long newSessionId = (_currentLeader?.LeaderSessionId ?? 0) + 1;
        string oldMaster = _currentLeader?.MasterNodeId ?? "Node1";

        stepReporter($"[CẢNH BÁO] Phát hiện mất Heartbeat từ Master cũ {oldMaster}!");
        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _currentNodeId, AuditEventKind.LeaderChanged,
            $"Heartbeat timeout từ Master {oldMaster}. Bắt đầu thuật toán Bully..."));

        await Task.Delay(1000);

        stepReporter("Node2 phát hiện và gửi thông điệp [ELECTION] tới các Node có ID cao hơn (Node3..Node6)");
        await Task.Delay(1200);

        stepReporter("Node3, Node4, Node5, Node6 trả lời [OK] - Node có ID cao hơn tiếp tục tranh cử");
        await Task.Delay(1200);

        stepReporter("Node6 (ID cao nhất cụm còn sống) không nhận được phản hồi từ ai cao hơn");
        await Task.Delay(1000);

        stepReporter($"👑 Node6 gửi BROADCAST [COORDINATOR]! Tuyên bố trở thành MASTER mới (Phiên #{newSessionId})");
        await Task.Delay(800);

        SetLeader("Node6", newSessionId);
    }
}
