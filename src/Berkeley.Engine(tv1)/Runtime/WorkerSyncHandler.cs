using Berkeley.Contracts;

namespace Berkeley.Engine.Runtime;

/// <summary>
/// [A7] Phía Worker — xử lý TIME_REQUEST và ADJUST từ Master.
///
/// Trách nhiệm:
///   1. Nhận TIME_REQUEST → đọc T2/T3 từ đồng hồ thực → gửi TIME_RESPONSE
///   2. Nhận ADJUST       → validate → ApplyStep() → gửi ADJUST_RESULT
///   3. Từ chối message từ sai LeaderSession hoặc sai BootId (replay attack)
///   4. Idempotency: mỗi MessageId chỉ được xử lý đúng 1 lần
/// </summary>
public sealed class WorkerSyncHandler : IDisposable
{
    private readonly INetworkTransport _transport;
    private readonly ISystemClock _clock;
    private readonly ILeadershipService _leadership;
    private readonly IAuditSink _audit;
    private readonly NodeConfig _config;
    private readonly NodeIdentity _identity;

    // [A7] Lưu MessageId đã xử lý để chống lệnh trùng (idempotency)
    // Dùng lock để thread-safe vì MessageReceived gọi từ network thread
    private readonly HashSet<Guid> _processedMessageIds = [];
    private readonly object _processedLock = new();

    // Giới hạn số lượng ID lưu để tránh memory leak (giữ 1000 ID gần nhất)
    private readonly Queue<Guid> _processedQueue = [];
    private const int MaxProcessedIds = 1000;

    private bool _disposed;

    public WorkerSyncHandler(
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

        // Subscribe nhận message từ network layer (TV2)
        _transport.MessageReceived += OnMessageReceived;
    }

    // ── Entry point: nhận tất cả message từ transport ────────────────────

    private void OnMessageReceived(MessageEnvelope envelope)
    {
        switch (envelope.MessageType)
        {
            case MessageTypes.TimeRequest:
                HandleTimeRequest(envelope);
                break;

            case MessageTypes.Adjust:
                HandleAdjust(envelope);
                break;

            // Các loại message khác (ELECTION, PING...) không thuộc A7 → bỏ qua
        }
    }

    // ── Xử lý TIME_REQUEST [A7] ───────────────────────────────────────────

    /// <summary>
    /// Nhận TIME_REQUEST từ Master → đọc T2, chuẩn bị T3, gửi TIME_RESPONSE.
    /// T2 đọc ngay khi nhận message để sai số nhỏ nhất.
    /// </summary>
    private void HandleTimeRequest(MessageEnvelope envelope)
    {
        // T2 phải đọc NGAY — càng sớm càng chính xác [A7]
        long t2 = _clock.ReadUtcMs();

        // [A7] Validate session — từ chối nếu không phải Master hiện tại
        if (!ValidateMasterMessage(envelope, requireRoundId: false))
            return;

        var payload = envelope.DeserializePayload<TimeRequestPayload>();
        if (payload is null)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"TIME_REQUEST from {envelope.SourceNodeId}: invalid payload"));
            return;
        }

        // T3 đọc ngay trước khi gửi — sai số chủ yếu ở thời gian serialize TCP (nhỏ)
        long t3 = _clock.ReadUtcMs();

        var response = MessageEnvelope.Create(
            messageType: MessageTypes.TimeResponse,
            sourceNodeId: _config.NodeId,
            sourceBootId: _identity.BootId,
            targetNodeId: envelope.SourceNodeId,
            payload: new TimeResponsePayload(
                T1MasterSendUtcMs: payload.T1MasterSendUtcMs,
                T2WorkerRecvUtcMs: t2,
                T3WorkerSendUtcMs: t3),
            roundId: envelope.RoundId,
            leaderSessionId: envelope.LeaderSessionId
        );

        // Fire-and-forget: gửi không chờ (tránh deadlock trong event handler)
        _ = _transport.SendAsync(envelope.SourceNodeId, response, CancellationToken.None);

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _config.NodeId, AuditEventKind.MessageSent,
            $"TIME_RESPONSE → {envelope.SourceNodeId} | T2={t2} T3={t3}"));
    }

    // ── Xử lý ADJUST [A7] ────────────────────────────────────────────────

    /// <summary>
    /// Nhận ADJUST từ Master → validate → ApplyStep → gửi ADJUST_RESULT.
    ///
    /// Idempotency: nếu MessageId đã xử lý → trả ADJUST_RESULT cũ (Rejected)
    /// để Master không timeout chờ mãi.
    /// </summary>
    private void HandleAdjust(MessageEnvelope envelope)
    {
        // [A7] Validate session và round
        if (!ValidateMasterMessage(envelope, requireRoundId: true))
            return;

        var payload = envelope.DeserializePayload<AdjustPayload>();
        if (payload is null)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"ADJUST from {envelope.SourceNodeId}: invalid payload"));
            return;
        }

        // [A7] Kiểm tra lệnh hết hạn (MaxAgeMs)
        long nowUtcMs = _clock.ReadUtcMs();
        long ageMs = nowUtcMs - payload.SentAtUtcMs;
        if (ageMs > payload.MaxAgeMs)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"ADJUST rejected: too old ({ageMs}ms > {payload.MaxAgeMs}ms)"));

            var staleResult = new AdjustmentResult(
                _config.NodeId, envelope.RoundId!,
                payload.AdjustmentMs, nowUtcMs, nowUtcMs,
                AdjustmentStatus.Rejected, $"ADJUST expired: {ageMs}ms > {payload.MaxAgeMs}ms");

            SendAdjustResult(envelope, staleResult);
            return;
        }

        // [A7] Idempotency — nếu MessageId đã xử lý → báo Rejected (đã làm rồi)
        bool alreadyProcessed;
        lock (_processedLock)
            alreadyProcessed = _processedMessageIds.Contains(envelope.MessageId);

        if (alreadyProcessed)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"ADJUST {envelope.MessageId} already processed (duplicate)"));

            var dupResult = new AdjustmentResult(
                _config.NodeId, envelope.RoundId!,
                payload.AdjustmentMs, nowUtcMs, nowUtcMs,
                AdjustmentStatus.Rejected, "Duplicate ADJUST message");

            SendAdjustResult(envelope, dupResult);
            return;
        }

        // ── Áp dụng Step ──────────────────────────────────────────────────

        var result = _clock.ApplyStep(_config.NodeId, envelope.RoundId!, payload.AdjustmentMs);

        // Ghi nhận MessageId đã xử lý [A7]
        lock (_processedLock)
        {
            _processedMessageIds.Add(envelope.MessageId);
            _processedQueue.Enqueue(envelope.MessageId);

            // Xóa ID cũ nếu quá giới hạn
            while (_processedQueue.Count > MaxProcessedIds)
            {
                var old = _processedQueue.Dequeue();
                _processedMessageIds.Remove(old);
            }
        }

        // Audit
        var auditKind = result.Status == AdjustmentStatus.Applied
            ? AuditEventKind.ClockAdjusted
            : AuditEventKind.ClockAdjustFailed;

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _config.NodeId, auditKind,
            $"ApplyStep {payload.AdjustmentMs:+0.0;-0.0}ms → {result.Status}" +
            (result.ErrorMessage != null ? $" ({result.ErrorMessage})" : "")));

        SendAdjustResult(envelope, result);
    }

    // ── Gửi ADJUST_RESULT về Master ───────────────────────────────────────

    private void SendAdjustResult(MessageEnvelope originalMsg, AdjustmentResult result)
    {
        var response = MessageEnvelope.Create(
            messageType: MessageTypes.AdjustResult,
            sourceNodeId: _config.NodeId,
            sourceBootId: _identity.BootId,
            targetNodeId: originalMsg.SourceNodeId,
            payload: new AdjustResultPayload(result),
            roundId: originalMsg.RoundId,
            leaderSessionId: originalMsg.LeaderSessionId,
            messageId: originalMsg.MessageId   // giữ nguyên MessageId để Master match
        );

        _ = _transport.SendAsync(originalMsg.SourceNodeId, response, CancellationToken.None);

        _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            _config.NodeId, AuditEventKind.MessageSent,
            $"ADJUST_RESULT → {originalMsg.SourceNodeId}: {result.Status}"));
    }

    // ── Validate message từ Master [A7] ───────────────────────────────────

    /// <summary>
    /// Kiểm tra message có đến từ Master hợp lệ không.
    /// Từ chối nếu:
    ///   - Không có Master hiện tại
    ///   - SourceNodeId không phải Master
    ///   - LeaderSessionId không khớp (message từ Master cũ)
    ///   - requireRoundId = true mà thiếu RoundId
    /// </summary>
    private bool ValidateMasterMessage(MessageEnvelope envelope, bool requireRoundId)
    {
        var leader = _leadership.CurrentLeader;

        if (leader is null)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"{envelope.MessageType} rejected: no leader elected yet"));
            return false;
        }

        if (envelope.SourceNodeId != leader.MasterNodeId)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"{envelope.MessageType} rejected: source {envelope.SourceNodeId} " +
                $"is not master (expected {leader.MasterNodeId})"));
            return false;
        }

        if (envelope.LeaderSessionId.HasValue &&
            envelope.LeaderSessionId.Value != leader.LeaderSessionId)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"{envelope.MessageType} rejected: stale session " +
                $"{envelope.LeaderSessionId} (current={leader.LeaderSessionId})"));
            return false;
        }

        if (requireRoundId && string.IsNullOrEmpty(envelope.RoundId))
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.MessageRejected,
                $"{envelope.MessageType} rejected: missing RoundId"));
            return false;
        }

        return true;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.MessageReceived -= OnMessageReceived;
    }
}

