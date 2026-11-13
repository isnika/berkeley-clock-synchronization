using Berkeley.Contracts;

namespace Berkeley.Engine.Runtime;

/// <summary>
/// [A9] Triển khai API điều khiển Node trung tâm (INodeApplication) cho GUI và toàn bộ hệ thống.
///
/// Quản lý vòng đời Node:
///   - Khởi động, dừng
///   - Khởi tạo WorkerSyncHandler (phía Worker) và SyncRoundOrchestrator (phía Master)
///   - Kích hoạt vòng đồng bộ thủ công hoặc theo lịch
///   - Xuất NodeSnapshot cho GUI cập nhật trạng thái hiển thị
/// </summary>
public sealed class NodeApplication : INodeApplication, IDisposable
{
    private readonly ISystemClock _clock;
    private readonly INetworkTransport _transport;
    private readonly ILeadershipService _leadership;
    private readonly IAuditSink _audit;

    private NodeConfig? _config;
    private NodeIdentity? _identity;
    private SyncRoundOrchestrator? _orchestrator;
    private WorkerSyncHandler? _workerHandler;

    private bool _isRunning;
    private bool _disposed;

    // Lưu thông số gần nhất của các peers để hiển thị lên snapshot
    private readonly Dictionary<string, (double? Rtt, double? Offset, double? Adjustment, DateTimeOffset? LastSeen)> _peerMetrics = [];
    private readonly object _metricsLock = new();

    public event Action<NodeSnapshot>? SnapshotChanged;
    public event Action<RoundSummary>? RoundCompleted;

    public NodeApplication(
        ISystemClock clock,
        INetworkTransport transport,
        ILeadershipService leadership,
        IAuditSink audit)
    {
        _clock = clock;
        _transport = transport;
        _leadership = leadership;
        _audit = audit;
    }

    public bool IsRunning => _isRunning;
    public NodeIdentity? Identity => _identity;
    public NodeConfig? Config => _config;

    // ── Khởi động Node [A9] ───────────────────────────────────────────────

    public Task StartAsync(NodeConfig config, CancellationToken ct = default)
    {
        if (_isRunning)
            return Task.CompletedTask;

        _config = config;
        _identity = new NodeIdentity(config.NodeId, Guid.NewGuid());

        // Khởi tạo các module con của Engine TV1
        _orchestrator = new SyncRoundOrchestrator(
            _transport, _clock, _leadership, _audit, _config, _identity);

        _workerHandler = new WorkerSyncHandler(
            _transport, _clock, _leadership, _audit, _config, _identity);

        _leadership.LeaderChanged += OnLeaderChanged;

        _isRunning = true;

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _identity.NodeId, AuditEventKind.RoundEnded,
            $"Node {_identity.NodeId} started (bootId={_identity.BootId})"));

        NotifySnapshotChanged();
        return Task.CompletedTask;
    }

    // ── Dừng Node [A9] ───────────────────────────────────────────────────

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning)
            return Task.CompletedTask;

        _isRunning = false;

        if (_leadership != null)
            _leadership.LeaderChanged -= OnLeaderChanged;

        _workerHandler?.Dispose();
        _workerHandler = null;

        _orchestrator?.Dispose();
        _orchestrator = null;

        NotifySnapshotChanged();
        return Task.CompletedTask;
    }

    // ── Kích hoạt đồng bộ ngay (chỉ Master) [A9] ──────────────────────────

    public async Task<bool> TriggerSyncNowAsync(CancellationToken ct = default)
    {
        if (!_isRunning || _orchestrator is null || !_leadership.IAmMaster)
            return false;

        if (_orchestrator.CurrentState != RoundState.Idle)
            return false; // Đang có vòng chạy dở

        NotifySnapshotChanged();

        var summary = await _orchestrator.RunRoundAsync(ct);

        if (summary != null)
        {
            // Cập nhật metrics đo được từ vòng vừa xong vào snapshot cache
            lock (_metricsLock)
            {
                foreach (var sample in summary.AcceptedSamples)
                {
                    _peerMetrics[sample.WorkerNodeId] = (
                        sample.RttMs,
                        sample.OffsetMs,
                        null,
                        DateTimeOffset.UtcNow
                    );
                }

                foreach (var adj in summary.AdjustmentResults)
                {
                    if (_peerMetrics.TryGetValue(adj.NodeId, out var existing))
                    {
                        _peerMetrics[adj.NodeId] = (
                            existing.Rtt,
                            existing.Offset,
                            adj.RequestedAdjustmentMs,
                            DateTimeOffset.UtcNow
                        );
                    }
                }
            }

            RoundCompleted?.Invoke(summary);
        }

        NotifySnapshotChanged();
        return summary != null;
    }

    // ── Lấy NodeSnapshot hiện tại [A9] ────────────────────────────────────

    public NodeSnapshot GetSnapshot()
    {
        string nodeId = _config?.NodeId ?? "Unknown";
        string displayName = _config?.DisplayName ?? nodeId;

        NodeRole role;
        if (!_isRunning)
        {
            role = NodeRole.Standalone;
        }
        else if (_leadership.IAmMaster)
        {
            role = NodeRole.Master;
        }
        else if (_leadership.CurrentLeader != null)
        {
            role = NodeRole.Worker;
        }
        else
        {
            role = NodeRole.Electing;
        }

        RoundPhase phase = _orchestrator?.CurrentState switch
        {
            RoundState.Measuring => RoundPhase.Measuring,
            RoundState.Adjusting => RoundPhase.Adjusting,
            RoundState.Verifying => RoundPhase.Verifying,
            _ => RoundPhase.Idle
        };

        var connectedNodes = _isRunning ? _transport.GetConnectedNodeIds() : [];
        var peerStatuses = new List<PeerStatus>();

        if (_config?.Peers != null)
        {
            foreach (var peer in _config.Peers)
            {
                bool isConnected = connectedNodes.Contains(peer.NodeId);
                var connState = isConnected
                    ? PeerConnectionState.Connected
                    : PeerConnectionState.Disconnected;

                double? lastRtt = null, lastOffset = null, lastAdj = null;
                DateTimeOffset? lastSeen = null;

                lock (_metricsLock)
                {
                    if (_peerMetrics.TryGetValue(peer.NodeId, out var m))
                    {
                        lastRtt = m.Rtt;
                        lastOffset = m.Offset;
                        lastAdj = m.Adjustment;
                        lastSeen = m.LastSeen;
                    }
                }

                peerStatuses.Add(new PeerStatus(
                    NodeId: peer.NodeId,
                    DisplayName: peer.NodeId,
                    ConnectionState: connState,
                    LastRttMs: lastRtt,
                    LastOffsetMs: lastOffset,
                    LastAdjustmentMs: lastAdj,
                    LastSeenAt: lastSeen
                ));
            }
        }

        return new NodeSnapshot(
            NodeId: nodeId,
            DisplayName: displayName,
            Role: role,
            CurrentMasterId: _leadership?.CurrentLeader?.MasterNodeId,
            LeaderSessionId: _leadership?.CurrentLeader?.LeaderSessionId,
            LocalClockUtcMs: _clock.ReadUtcMs(),
            HasSetTimePermission: _clock.HasSetTimePermission(),
            ActiveRoundId: _orchestrator?.CurrentRoundId,
            RoundPhase: phase,
            KnownPeers: peerStatuses,
            SnapshotTakenAt: DateTimeOffset.UtcNow
        );
    }

    private void OnLeaderChanged(LeaderSession? newLeader)
    {
        NotifySnapshotChanged();
    }

    private void NotifySnapshotChanged()
    {
        SnapshotChanged?.Invoke(GetSnapshot());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = StopAsync();
    }
}

