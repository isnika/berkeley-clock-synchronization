using Berkeley.Contracts;
using Berkeley.Engine.Synchronization;

namespace Berkeley.Engine.Runtime;

/// <summary>
/// [A6, A7, A8] State machine điều phối một vòng đồng bộ Berkeley.
/// Master chạy SyncRoundOrchestrator; Worker chỉ nhận lệnh và trả kết quả.
///
/// Vòng đời: Idle → Measuring → Adjusting → Verifying → Done/Cancelled
/// </summary>
public sealed class SyncRoundOrchestrator : IDisposable
{
    private readonly INetworkTransport _transport;
    private readonly ISystemClock _clock;
    private readonly ILeadershipService _leadership;
    private readonly IAuditSink _audit;
    private readonly NodeConfig _config;
    private readonly NodeIdentity _identity;
    private bool _disposed;

    // Trạng thái vòng hiện tại
    private RoundState _state = RoundState.Idle;
    private string? _currentRoundId;
    private long _currentLeaderSessionId;

    // Thread-safety: tất cả write từ network event handler đều qua lock này [A8]
    private readonly object _samplesLock = new();
    private readonly Dictionary<string, ClockSample> _collectedSamples = [];
    private readonly Dictionary<string, long> _sampleTakenMonotonicMs = [];

    private readonly object _adjustLock = new();
    private readonly HashSet<Guid> _appliedAdjustmentIds = [];

    // TaskCompletionSource để chờ ADJUST_RESULT từng Worker [A6]
    private readonly Dictionary<string, TaskCompletionSource<AdjustmentResult>> _pendingAdjusts = [];
    private readonly object _pendingAdjustsLock = new();

    // TaskCompletionSource để chờ TIME_RESPONSE (đo lại) [A6]
    private readonly Dictionary<string, TaskCompletionSource<ClockSample>> _pendingRemeasure = [];
    private readonly object _pendingRemeasureLock = new();

    private CancellationTokenSource? _roundCts;

    public SyncRoundOrchestrator(
        INetworkTransport transport,
        ISystemClock clock,
        ILeadershipService leadership,
        IAuditSink audit,
        NodeConfig config,
        NodeIdentity identity)
    {
        _transport = transport;
        _clock = clock;
        _leadership = leadership;
        _audit = audit;
        _config = config;
        _identity = identity;

        // Nhận phản hồi từ network layer
        _transport.MessageReceived += OnMessageReceived;

        // Hủy vòng ngay khi Master đổi [A8]
        _leadership.LeaderChanged += OnLeaderChanged;
    }

    private void OnMessageReceived(MessageEnvelope envelope)
    {
        if (envelope.MessageType == MessageTypes.TimeResponse)
        {
            long t4 = _clock.ReadUtcMs();
            HandleTimeResponse(envelope, t4);
        }
        else if (envelope.MessageType == MessageTypes.AdjustResult)
        {
            HandleAdjustResult(envelope);
        }
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

        // Round timeout tổng thể [A8]
        _roundCts.CancelAfter(_config.OperationParams.RoundTimeoutMs);

        var roundStarted = DateTimeOffset.UtcNow;

        lock (_samplesLock)
        {
            _collectedSamples.Clear();
            _sampleTakenMonotonicMs.Clear();
        }

        _audit.Record(new AuditEvent(
            Sequence: 0,
            OccurredAt: DateTimeOffset.UtcNow,
            MonotonicMs: _clock.ReadMonotonicMs(),
            SourceNodeId: _config.NodeId,
            Kind: AuditEventKind.RoundStarted,
            Summary: $"Round {_currentRoundId} started (session={_currentLeaderSessionId})"
        ));

        try
        {
            // BƯỚC 1: ĐO thời gian tất cả Worker
            _state = RoundState.Measuring;
            await MeasurePhaseAsync(_roundCts.Token);

            if (_roundCts.Token.IsCancellationRequested || _state == RoundState.Cancelled)
                return BuildSummary(roundStarted, [], RoundOutcome.Cancelled);

            // BƯỚC 2: TÍNH TRUNG BÌNH
            List<ClockSample> validSamples, rejectedSamples;
            lock (_samplesLock)
            {
                validSamples   = _collectedSamples.Values.Where(s => s.Status == SampleStatus.Valid).ToList();
                rejectedSamples = _collectedSamples.Values.Where(s => s.Status != SampleStatus.Valid).ToList();
            }

            var result = BerkeleyCalculator.ComputeAdjustments(_config.NodeId, validSamples);

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

            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.ClockAdjusted,
                $"Master self-adjusted {result.MasterAdjustment.AdjustmentMs:+0.0;-0.0}ms"));

            // BƯỚC 4: ĐO LẠI để kiểm tra spread sau hiệu chỉnh
            _state = RoundState.Verifying;
            double? spread = await RemeasurePhaseAsync(_roundCts.Token);

            _state = RoundState.Idle;

            var outcome = adjustResults.All(r => r.Status == AdjustmentStatus.Applied)
                ? RoundOutcome.Success
                : RoundOutcome.PartialSuccess;

            var summary = BuildSummary(roundStarted, adjustResults, outcome,
                validSamples, rejectedSamples, result.AverageOffsetMs, spread);

            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.RoundEnded,
                $"Round {_currentRoundId} ended: {outcome}, spread={spread:F1}ms"));

            return summary;
        }
        catch (OperationCanceledException)
        {
            _state = RoundState.Idle;
            return BuildSummary(roundStarted, [], RoundOutcome.Cancelled);
        }
        finally
        {
            // Dọn dẹp các TCS còn treo
            CancelPendingAdjusts();
            CancelPendingRemeasure();
            _roundCts?.Dispose();
            _roundCts = null;
        }
    }

    // ── Phase 1: Đo thời gian (per-Worker timeout) [A6] ───────────────────

    private async Task MeasurePhaseAsync(CancellationToken ct)
    {
        var peers = _transport.GetConnectedNodeIds();
        if (peers.Count == 0) return;

        int timeoutMs = _config.OperationParams.SampleTimeoutMs;

        // Mỗi Worker có timeout riêng — không bị kéo chờ nhau [A6/A8]
        var tasks = peers.Select(peerId => SendTimeRequestWithTimeoutAsync(peerId, timeoutMs, ct));
        await Task.WhenAll(tasks);
    }

    private async Task SendTimeRequestWithTimeoutAsync(
        string targetNodeId, int timeoutMs, CancellationToken ct)
    {
        long t1 = _clock.ReadUtcMs();
        long monoAtSend = _clock.ReadMonotonicMs();

        var msg = MessageEnvelope.Create(
            messageType: MessageTypes.TimeRequest,
            sourceNodeId: _config.NodeId,
            sourceBootId: _identity.BootId,
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

            // Ghi mẫu Timeout ngay nếu gửi thất bại
            RecordTimeoutSample(targetNodeId, t1);
            return;
        }

        // Chờ tối đa timeoutMs để nhận TIME_RESPONSE thông qua HandleTimeResponse()
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await Task.Delay(timeoutMs, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Vòng bị hủy từ bên ngoài — không làm gì
            return;
        }
        catch (OperationCanceledException)
        {
            // timeout hết → không sao, HandleTimeResponse() đã cập nhật nếu có
        }

        // Nếu sau timeout vẫn chưa có sample → ghi Timeout
        bool hasResponse;
        lock (_samplesLock)
            hasResponse = _collectedSamples.ContainsKey(targetNodeId);

        if (!hasResponse)
            RecordTimeoutSample(targetNodeId, t1);
    }

    private void RecordTimeoutSample(string targetNodeId, long t1)
    {
        var sample = new ClockSample(
            RoundId: _currentRoundId!,
            WorkerNodeId: targetNodeId,
            T1MasterSendUtcMs: t1,
            T2WorkerRecvUtcMs: 0,
            T3WorkerSendUtcMs: 0,
            T4MasterRecvUtcMs: 0,
            OffsetMs: 0,
            RttMs: -1,
            Status: SampleStatus.RejectedTimeout,
            RejectionReason: "No TIME_RESPONSE within timeout"
        );

        lock (_samplesLock)
            _collectedSamples[targetNodeId] = sample;

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _config.NodeId, AuditEventKind.MessageRejected,
            $"Worker {targetNodeId} timeout — no TIME_RESPONSE"));
    }

    // ── Nhận TIME_RESPONSE từ Worker ──────────────────────────────────────

    /// <summary>Được gọi khi nhận TIME_RESPONSE từ Worker. Thread-safe.</summary>
    public void HandleTimeResponse(MessageEnvelope envelope, long t4MasterRecvUtcMs)
    {
        // [A7] Kiểm tra phiên và vòng — bỏ qua message lạc/lỗi thời
        if (envelope.RoundId != _currentRoundId) return;
        if (envelope.LeaderSessionId != _currentLeaderSessionId) return;
        if (_state != RoundState.Measuring && _state != RoundState.Verifying) return;

        var payload = envelope.DeserializePayload<TimeResponsePayload>();
        if (payload is null) return;

        long t1 = payload.T1MasterSendUtcMs;
        long t2 = payload.T2WorkerRecvUtcMs;
        long t3 = payload.T3WorkerSendUtcMs;

        var (offsetMs, rttMs) = BerkeleyCalculator.ComputeOffset(t1, t2, t3, t4MasterRecvUtcMs);
        long monoNow = _clock.ReadMonotonicMs();

        // Validate đầy đủ [A6: sửa vấn đề ValidateSample không được gọi]
        var dummySample = new ClockSample(
            RoundId: envelope.RoundId!,
            WorkerNodeId: envelope.SourceNodeId,
            T1MasterSendUtcMs: t1,
            T2WorkerRecvUtcMs: t2,
            T3WorkerSendUtcMs: t3,
            T4MasterRecvUtcMs: t4MasterRecvUtcMs,
            OffsetMs: offsetMs,
            RttMs: rttMs,
            Status: SampleStatus.Valid
        );

        long monoAtSend;
        lock (_samplesLock)
            _sampleTakenMonotonicMs.TryGetValue(envelope.SourceNodeId, out monoAtSend);

        // Lấy monoAtSend từ lúc gửi TIME_REQUEST (nếu chưa có thì dùng monoNow)
        // monoAtSend = 0 nếu là lần đo lại (remeasure)
        var (isValid, reason) = BerkeleyCalculator.ValidateSample(
            sample: dummySample,
            expectedRoundId: _currentRoundId!,
            nowMonotonicMs: monoNow,
            sampleMaxAgeMs: _config.OperationParams.SampleMaxAgeMs,
            sampleTakenMonotonicMs: monoAtSend == 0 ? monoNow : monoAtSend,
            maxRttMs: _config.OperationParams.SampleTimeoutMs
        );

        SampleStatus status = isValid ? SampleStatus.Valid
            : (rttMs < 0 ? SampleStatus.RejectedHighRtt
            : reason?.Contains("old") == true ? SampleStatus.RejectedStaleRound
            : SampleStatus.RejectedHighRtt);

        var sample = dummySample with { Status = status, RejectionReason = isValid ? null : reason };

        // Phase đo chính → lưu vào _collectedSamples
        if (_state == RoundState.Measuring)
        {
            lock (_samplesLock)
                _collectedSamples[envelope.SourceNodeId] = sample;
        }
        // Phase đo lại → báo cho RemeasurePhaseAsync biết
        else if (_state == RoundState.Verifying)
        {
            lock (_pendingRemeasureLock)
            {
                if (_pendingRemeasure.TryGetValue(envelope.SourceNodeId, out var tcs))
                    tcs.TrySetResult(sample);
            }
        }
    }

    // ── Nhận ADJUST_RESULT từ Worker [A6] ────────────────────────────────

    /// <summary>Được gọi khi nhận ADJUST_RESULT từ Worker. Thread-safe.</summary>
    public void HandleAdjustResult(MessageEnvelope envelope)
    {
        if (envelope.RoundId != _currentRoundId) return;
        if (envelope.LeaderSessionId != _currentLeaderSessionId) return;
        if (_state != RoundState.Adjusting) return;

        var payload = envelope.DeserializePayload<AdjustResultPayload>();
        if (payload is null) return;

        lock (_pendingAdjustsLock)
        {
            if (_pendingAdjusts.TryGetValue(envelope.SourceNodeId, out var tcs))
                tcs.TrySetResult(payload.Result);
        }
    }

    // ── Phase 2: Hiệu chỉnh (chờ ADJUST_RESULT từng Worker) [A6] ─────────

    private async Task<List<AdjustmentResult>> AdjustPhaseAsync(
        BerkeleyResult result, CancellationToken ct)
    {
        int adjustTimeoutMs = _config.OperationParams.SampleTimeoutMs * 2; // 2x timeout
        var tasks = result.WorkerAdjustments.Select(adj =>
            SendAdjustAsync(adj.NodeId, adj.AdjustmentMs, adjustTimeoutMs, ct));
        var results = await Task.WhenAll(tasks);
        return results.OfType<AdjustmentResult>().ToList();
    }

    private async Task<AdjustmentResult?> SendAdjustAsync(
        string targetNodeId, double adjustmentMs, int timeoutMs, CancellationToken ct)
    {
        var msgId = Guid.NewGuid();

        // [A7] Chống lệnh trùng: lưu msgId khi gửi
        lock (_adjustLock)
            _appliedAdjustmentIds.Add(msgId);

        // Đăng ký TaskCompletionSource để chờ ADJUST_RESULT
        var tcs = new TaskCompletionSource<AdjustmentResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pendingAdjustsLock)
            _pendingAdjusts[targetNodeId] = tcs;

        var msg = MessageEnvelope.Create(
            messageType: MessageTypes.Adjust,
            sourceNodeId: _config.NodeId,
            sourceBootId: _identity.BootId,
            targetNodeId: targetNodeId,
            payload: new AdjustPayload(
                AdjustmentMs: adjustmentMs,
                SentAtUtcMs: _clock.ReadUtcMs(),
                MaxAgeMs: _config.OperationParams.SampleMaxAgeMs),
            roundId: _currentRoundId,
            leaderSessionId: _currentLeaderSessionId,
            messageId: msgId
        );

        bool sent = await _transport.SendAsync(targetNodeId, msg, ct);
        if (!sent)
        {
            lock (_pendingAdjustsLock)
                _pendingAdjusts.Remove(targetNodeId);

            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.SendError,
                $"Failed to send ADJUST to {targetNodeId}"));

            return new AdjustmentResult(targetNodeId, _currentRoundId!,
                adjustmentMs, 0, 0, AdjustmentStatus.Failed, "Send failed");
        }

        // Chờ ADJUST_RESULT hoặc timeout [A6]
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            var waitTask = tcs.Task;
            var delayTask = Task.Delay(timeoutMs, timeoutCts.Token);
            var completed = await Task.WhenAny(waitTask, delayTask);

            if (completed == waitTask && waitTask.IsCompletedSuccessfully)
            {
                lock (_pendingAdjustsLock)
                    _pendingAdjusts.Remove(targetNodeId);

                return waitTask.Result;
            }
        }
        catch (OperationCanceledException) { /* vòng bị hủy */ }

        // Timeout hoặc bị hủy — không nhận được ADJUST_RESULT
        lock (_pendingAdjustsLock)
            _pendingAdjusts.Remove(targetNodeId);

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _config.NodeId, AuditEventKind.MessageRejected,
            $"No ADJUST_RESULT from {targetNodeId} within {timeoutMs}ms"));

        return new AdjustmentResult(targetNodeId, _currentRoundId!,
            adjustmentMs, 0, 0, AdjustmentStatus.Failed, "ADJUST_RESULT timeout");
    }

    // ── Phase 3: Đo lại spread sau hiệu chỉnh [A6] ───────────────────────

    private async Task<double?> RemeasurePhaseAsync(CancellationToken ct)
    {
        var peers = _transport.GetConnectedNodeIds();
        if (peers.Count == 0) return null;

        int timeoutMs = _config.OperationParams.SampleTimeoutMs;

        // Tạo TCS cho từng Worker đo lại
        lock (_pendingRemeasureLock)
        {
            _pendingRemeasure.Clear();
            foreach (var peerId in peers)
                _pendingRemeasure[peerId] = new TaskCompletionSource<ClockSample>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Gửi TIME_REQUEST lần 2 (không lưu monoAtSend vì dùng riêng cho remeasure)
        var sendTasks = peers.Select(peerId => SendRemeasureRequestAsync(peerId, ct));
        await Task.WhenAll(sendTasks);

        // Chờ tất cả Worker trả lời hoặc timeout
        List<TaskCompletionSource<ClockSample>> tcsList;
        lock (_pendingRemeasureLock)
            tcsList = _pendingRemeasure.Values.ToList();

        var waitTasks = tcsList.Select(tcs => tcs.Task).ToList();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        try
        {
            await Task.WhenAll(waitTasks.Select(t =>
                t.WaitAsync(timeoutCts.Token).ContinueWith(_ => { }, TaskContinuationOptions.None)));
        }
        catch (OperationCanceledException) { /* timeout — thu kết quả có được */ }

        // Thu các offset đo lại được (chỉ Valid)
        var offsets = new List<double>();
        lock (_pendingRemeasureLock)
        {
            foreach (var tcs in _pendingRemeasure.Values)
            {
                if (tcs.Task.IsCompletedSuccessfully && tcs.Task.Result.Status == SampleStatus.Valid)
                    offsets.Add(tcs.Task.Result.OffsetMs);
            }
            _pendingRemeasure.Clear();
        }

        if (offsets.Count == 0) return null;

        double spread = BerkeleyCalculator.ComputeSpread(offsets);
        return spread;
    }

    private async Task SendRemeasureRequestAsync(string targetNodeId, CancellationToken ct)
    {
        long t1 = _clock.ReadUtcMs();

        var msg = MessageEnvelope.Create(
            messageType: MessageTypes.TimeRequest,
            sourceNodeId: _config.NodeId,
            sourceBootId: _identity.BootId,
            targetNodeId: targetNodeId,
            payload: new TimeRequestPayload(t1),
            roundId: _currentRoundId,
            leaderSessionId: _currentLeaderSessionId
        );

        bool sent = await _transport.SendAsync(targetNodeId, msg, ct);
        if (!sent)
        {
            // Báo timeout luôn cho TCS của worker này
            lock (_pendingRemeasureLock)
            {
                if (_pendingRemeasure.TryGetValue(targetNodeId, out var tcs))
                    tcs.TrySetCanceled();
            }
        }
    }

    // ── Hủy vòng khi Master đổi [A8] ─────────────────────────────────────

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

    // ── Dọn dẹp TCS còn treo khi vòng kết thúc/hủy ───────────────────────

    private void CancelPendingAdjusts()
    {
        lock (_pendingAdjustsLock)
        {
            foreach (var tcs in _pendingAdjusts.Values)
                tcs.TrySetCanceled();
            _pendingAdjusts.Clear();
        }
    }

    private void CancelPendingRemeasure()
    {
        lock (_pendingRemeasureLock)
        {
            foreach (var tcs in _pendingRemeasure.Values)
                tcs.TrySetCanceled();
            _pendingRemeasure.Clear();
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.MessageReceived -= OnMessageReceived;
        _leadership.LeaderChanged -= OnLeaderChanged;
        _roundCts?.Dispose();
    }
}

public enum RoundState { Idle, Measuring, Adjusting, Verifying, Cancelled }
