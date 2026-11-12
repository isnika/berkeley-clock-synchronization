using Berkeley.Contracts;
using Berkeley.Engine.Synchronization;

namespace Berkeley.Engine.Runtime;

/// <summary>
/// [A6, A7, A8] State machine điều phối một vòng đồng bộ Berkeley.
/// Master chạy SyncRoundOrchestrator; Worker chỉ nhận lệnh và trả kết quả.
///
/// Vòng đời: Idle → Measuring → Adjusting → Verifying → Done/Cancelled
/// </summary>
public sealed class SyncRoundOrchestrator
{
    private readonly INetworkTransport _transport;
    private readonly ISystemClock _clock;
    private readonly ILeadershipService _leadership;
    private readonly IAuditSink _audit;
    private readonly NodeConfig _config;

    // Trạng thái vòng hiện tại
    private RoundState _state = RoundState.Idle;
    private string? _currentRoundId;
    private long _currentLeaderSessionId;
    private readonly Dictionary<string, ClockSample> _collectedSamples = [];
    private readonly Dictionary<string, long> _sampleTakenMonotonicMs = [];
    private readonly HashSet<Guid> _appliedAdjustmentIds = []; // chống lệnh trùng

    private CancellationTokenSource? _roundCts;

    public SyncRoundOrchestrator(
        INetworkTransport transport,
        ISystemClock clock,
        ILeadershipService leadership,
        IAuditSink audit,
        NodeConfig config)
    {
        _transport = transport;
        _clock = clock;
        _leadership = leadership;
        _audit = audit;
        _config = config;

        // Hủy vòng ngay khi Master đổi [A8]
        _leadership.LeaderChanged += OnLeaderChanged;
    }

    public RoundState CurrentState => _state;
    public string? CurrentRoundId => _currentRoundId;

    // ── Khởi động vòng (chỉ Master gọi) ──────────────────────────────────

    /// <summary>[A6] Chạy một vòng đồng bộ hoàn chỉnh. Trả về RoundSummary khi xong.</summary>
    public async Task<RoundSummary?> RunRoundAsync(CancellationToken ct = default)
    {
        if (!_leadership.IAmMaster)
            return null;

        if (_state != RoundState.Idle)
            return null; // vòng khác đang chạy

        var leader = _leadership.CurrentLeader!;
        _currentRoundId = Guid.NewGuid().ToString("N");
        _currentLeaderSessionId = leader.LeaderSessionId;
        _roundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var roundStarted = DateTimeOffset.UtcNow;
        _collectedSamples.Clear();
        _sampleTakenMonotonicMs.Clear();

        _audit.Record(new AuditEvent(
            Sequence: 0,
            OccurredAt: DateTimeOffset.UtcNow,
            MonotonicMs: _clock.ReadMonotonicMs(),
            SourceNodeId: _config.NodeId,
            Kind: AuditEventKind.RoundStarted,
            Summary: $"Round {_currentRoundId} started"
        ));

        try
        {
            // BƯỚC 1: ĐO
            _state = RoundState.Measuring;
            await MeasurePhaseAsync(_roundCts.Token);

            if (_roundCts.Token.IsCancellationRequested || _state == RoundState.Cancelled)
                return BuildSummary(roundStarted, [], RoundOutcome.Cancelled);

            // BƯỚC 2: TÍNH TRUNG BÌNH
            var validSamples = _collectedSamples.Values
                .Where(s => s.Status == SampleStatus.Valid)
                .ToList();

            var rejectedSamples = _collectedSamples.Values
                .Where(s => s.Status != SampleStatus.Valid)
                .ToList();

            var result = BerkeleyCalculator.ComputeAdjustments(
                _config.NodeId, validSamples);

            if (result is null)
                return BuildSummary(roundStarted, [], RoundOutcome.Failed,
                    validSamples, rejectedSamples);

            // BƯỚC 3: GỬI LỆNH HIỆU CHỈNH
            _state = RoundState.Adjusting;
            var adjustResults = await AdjustPhaseAsync(result, _roundCts.Token);

            // Master tự chỉnh giờ của mình
            var masterResult = _clock.ApplyStep(
                _config.NodeId, _currentRoundId!, result.MasterAdjustment.AdjustmentMs);
            adjustResults.Add(masterResult);

            // BƯỚC 4: ĐO LẠI
            _state = RoundState.Verifying;
            double? spread = await RemeasurePhaseAsync(_roundCts.Token);

            _state = RoundState.Idle;

            var outcome = adjustResults.All(r => r.Status == AdjustmentStatus.Applied)
                ? RoundOutcome.Success
                : RoundOutcome.PartialSuccess;

            return BuildSummary(roundStarted, adjustResults, outcome,
                validSamples, rejectedSamples, result.AverageOffsetMs, spread);
        }
        catch (OperationCanceledException)
        {
            _state = RoundState.Idle;
            return BuildSummary(roundStarted, [], RoundOutcome.Cancelled);
        }
        finally
        {
            _roundCts?.Dispose();
            _roundCts = null;
        }
    }

    // ── Phase: Đo thời gian ───────────────────────────────────────────────

    private async Task MeasurePhaseAsync(CancellationToken ct)
    {
        var peers = _transport.GetConnectedNodeIds();
        var tasks = peers.Select(peerId => SendTimeRequestAsync(peerId, ct));
        await Task.WhenAll(tasks);

        // Chờ thêm chút để nhận phản hồi chậm (trong giới hạn timeout)
        await Task.Delay(
            (int)(_config.OperationParams.SampleTimeoutMs * 0.1), ct);
    }

    private async Task SendTimeRequestAsync(string targetNodeId, CancellationToken ct)
    {
        long t1 = _clock.ReadUtcMs();
        long monoAtSend = _clock.ReadMonotonicMs();

        var msg = MessageEnvelope.Create(
            messageType: MessageTypes.TimeRequest,
            sourceNodeId: _config.NodeId,
            sourceBootId: Guid.NewGuid(), // thay bằng BootId thực từ NodeIdentity
            targetNodeId: targetNodeId,
            payload: new TimeRequestPayload(t1),
            roundId: _currentRoundId,
            leaderSessionId: _currentLeaderSessionId
        );

        bool sent = await _transport.SendAsync(targetNodeId, msg, ct);
        if (!sent)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.SendError,
                $"Failed to send TIME_REQUEST to {targetNodeId}"));
        }
    }

    /// <summary>Được gọi khi nhận TIME_RESPONSE từ Worker.</summary>
    public void HandleTimeResponse(MessageEnvelope envelope, long t4MasterRecvUtcMs)
    {
        // [A7] Kiểm tra phiên và vòng
        if (envelope.RoundId != _currentRoundId) return;
        if (envelope.LeaderSessionId != _currentLeaderSessionId) return;
        if (_state != RoundState.Measuring) return;

        var payload = envelope.DeserializePayload<TimeResponsePayload>();
        if (payload is null) return;

        long t1 = payload.T1MasterSendUtcMs;
        long t2 = payload.T2WorkerRecvUtcMs;
        long t3 = payload.T3WorkerSendUtcMs;

        var (offsetMs, rttMs) = BerkeleyCalculator.ComputeOffset(t1, t2, t3, t4MasterRecvUtcMs);

        long monoNow = _clock.ReadMonotonicMs();
        bool rttOk = rttMs <= _config.OperationParams.SampleTimeoutMs;

        var sample = new ClockSample(
            RoundId: _currentRoundId!,
            WorkerNodeId: envelope.SourceNodeId,
            T1MasterSendUtcMs: t1,
            T2WorkerRecvUtcMs: t2,
            T3WorkerSendUtcMs: t3,
            T4MasterRecvUtcMs: t4MasterRecvUtcMs,
            OffsetMs: offsetMs,
            RttMs: rttMs,
            Status: rttOk ? SampleStatus.Valid : SampleStatus.RejectedHighRtt,
            RejectionReason: rttOk ? null : $"RTT {rttMs}ms quá cao"
        );

        _collectedSamples[envelope.SourceNodeId] = sample;
        _sampleTakenMonotonicMs[envelope.SourceNodeId] = monoNow;
    }

    // ── Phase: Hiệu chỉnh ─────────────────────────────────────────────────

    private async Task<List<AdjustmentResult>> AdjustPhaseAsync(
        BerkeleyResult result, CancellationToken ct)
    {
        var tasks = result.WorkerAdjustments.Select(adj =>
            SendAdjustAsync(adj.NodeId, adj.AdjustmentMs, ct));
        var results = await Task.WhenAll(tasks);
        return results.OfType<AdjustmentResult>().ToList();
    }

    private async Task<AdjustmentResult?> SendAdjustAsync(
        string targetNodeId, double adjustmentMs, CancellationToken ct)
    {
        var msgId = Guid.NewGuid();

        // [A7] Chống lệnh trùng: lưu msgId khi gửi
        _appliedAdjustmentIds.Add(msgId);

        var msg = MessageEnvelope.Create(
            messageType: MessageTypes.Adjust,
            sourceNodeId: _config.NodeId,
            sourceBootId: Guid.Empty,
            targetNodeId: targetNodeId,
            payload: new AdjustPayload(
                AdjustmentMs: adjustmentMs,
                SentAtUtcMs: _clock.ReadUtcMs(),
                MaxAgeMs: _config.OperationParams.SampleMaxAgeMs),
            roundId: _currentRoundId,
            leaderSessionId: _currentLeaderSessionId,
            messageId: msgId
        );

        await _transport.SendAsync(targetNodeId, msg, ct);

        // Thực tế cần chờ ADJUST_RESULT — đây là skeleton, sẽ hoàn thiện ở A6
        return null;
    }

    // ── Phase: Đo lại ─────────────────────────────────────────────────────

    private async Task<double?> RemeasurePhaseAsync(CancellationToken ct)
    {
        // Gửi TIME_REQUEST lần 2 để đo spread sau hiệu chỉnh
        // Skeleton — sẽ hoàn thiện khi tích hợp với TV2
        await Task.Delay(500, ct);
        return null;
    }

    // ── Hủy vòng khi Master đổi ───────────────────────────────────────────

    private void OnLeaderChanged(LeaderSession? newLeader)
    {
        if (_state != RoundState.Idle)
        {
            _state = RoundState.Cancelled;
            _roundCts?.Cancel();

            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow,
                _clock.ReadMonotonicMs(), _config.NodeId,
                AuditEventKind.LeaderChanged,
                $"Round {_currentRoundId} cancelled: leader changed to {newLeader?.MasterNodeId ?? "none"}"));
        }
    }

    // ── Build summary ─────────────────────────────────────────────────────

    private RoundSummary BuildSummary(
        DateTimeOffset startedAt,
        List<AdjustmentResult> adjustResults,
        RoundOutcome outcome,
        List<ClockSample>? accepted = null,
        List<ClockSample>? rejected = null,
        double avgOffset = 0,
        double? spread = null)
    {
        return new RoundSummary(
            RoundId: _currentRoundId ?? "unknown",
            MasterNodeId: _config.NodeId,
            LeaderSessionId: _currentLeaderSessionId,
            RoundStartedAt: startedAt,
            RoundEndedAt: DateTimeOffset.UtcNow,
            AcceptedSamples: accepted ?? [],
            RejectedSamples: rejected ?? [],
            AverageOffsetMs: avgOffset,
            AdjustmentResults: adjustResults,
            RemeasuredSpreadMs: spread,
            Outcome: outcome
        );
    }
}

public enum RoundState { Idle, Measuring, Adjusting, Verifying, Cancelled }

