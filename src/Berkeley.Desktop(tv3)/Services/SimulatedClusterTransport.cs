using System.Text.Json;
using Berkeley.Contracts;

namespace Berkeley.Desktop.Services;

/// <summary>
/// Trình vận chuyển mạng giả lập cụm 6 máy cho phép chạy thử nghiệm và demo đầy đủ
/// thuật toán Berkeley và thuật toán Bully ngay trên 1 máy tính mà không cần cắm dây LAN.
/// </summary>
public sealed class SimulatedClusterTransport : INetworkTransport, IDisposable
{
    private readonly IClockReader _clock;
    private readonly long _originUtc;
    private readonly long _originMono;
    private bool _disposed;
    private readonly IAuditSink _audit;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _simulatedDriftMs = new()
    {
        ["Node2"] = +120_000,  // +2 phút
        ["Node3"] = -45_000,   // -45 giây
        ["Node4"] = +210_000,  // +3.5 phút
        ["Node5"] = -90_000,   // -1.5 phút
        ["Node6"] = +15_000    // +15 giây
    };

    public event Action<MessageEnvelope>? MessageReceived;

    public SimulatedClusterTransport(IClockReader clock, IAuditSink audit, NodeConfig? config = null)
    {
        _clock = clock;
        _originUtc = clock.ReadUtcMs();
        _originMono = clock.ReadMonotonicMs();
        if (config != null)
        {
            var defaults = _simulatedDriftMs.ToDictionary(x => x.Key, x => x.Value);
            _simulatedDriftMs.Clear();
            foreach (var peer in config.Peers) _simulatedDriftMs[peer.NodeId] = defaults.GetValueOrDefault(peer.NodeId);
        }
        _audit = audit;
    }

    public IReadOnlyList<string> GetConnectedNodeIds()
    {
        return _simulatedDriftMs.Keys.ToList();
    }

    public Task<bool> SendAsync(string targetNodeId, MessageEnvelope message, CancellationToken ct = default)
    {
        _audit.Record(new AuditEvent(
            Sequence: 0,
            OccurredAt: DateTimeOffset.UtcNow,
            MonotonicMs: _clock.ReadMonotonicMs(),
            SourceNodeId: message.SourceNodeId,
            Kind: AuditEventKind.MessageSent,
            Summary: $"GỬI [{message.MessageType}] tới {targetNodeId} (Round: {message.RoundId ?? "N/A"})"
        ));

        _ = HandlePeerSimulatedResponseAsync(targetNodeId, message);
        return Task.FromResult(true);
    }

    public Task BroadcastAsync(MessageEnvelope message, CancellationToken ct = default)
    {
        _audit.Record(new AuditEvent(
            Sequence: 0,
            OccurredAt: DateTimeOffset.UtcNow,
            MonotonicMs: _clock.ReadMonotonicMs(),
            SourceNodeId: message.SourceNodeId,
            Kind: AuditEventKind.MessageSent,
            Summary: $"BROADCAST [{message.MessageType}] tới {_simulatedDriftMs.Count} máy Worker (Round: {message.RoundId ?? "N/A"})"
        ));

        foreach (var peerId in _simulatedDriftMs.Keys)
        {
            _ = HandlePeerSimulatedResponseAsync(peerId, message);
        }

        return Task.CompletedTask;
    }

    private async Task HandlePeerSimulatedResponseAsync(string peerId, MessageEnvelope msg)
    {
        // Giả lập trễ mạng ngẫu nhiên 5-15ms
        int delay = Random.Shared.Next(8, 20);
        await Task.Delay(delay);

        if (_disposed) return;
        long nowUtc = _originUtc + _clock.ReadMonotonicMs() - _originMono;
        double drift = _simulatedDriftMs.GetValueOrDefault(peerId, 0);

        if (msg.MessageType == MessageTypes.TimeRequest)
        {
            var req = msg.DeserializePayload<TimeRequestPayload>();
            long t1 = req?.T1MasterSendUtcMs ?? nowUtc;
            long t2 = (long)(nowUtc + drift);
            long t3 = t2 + Random.Shared.Next(1, 3); // Worker mất 1-3ms xử lý

            var responsePayload = new TimeResponsePayload(t1, t2, t3);
            var respEnv = MessageEnvelope.Create(
                messageType: MessageTypes.TimeResponse,
                sourceNodeId: peerId,
                sourceBootId: Guid.NewGuid(),
                targetNodeId: msg.SourceNodeId,
                payload: responsePayload,
                roundId: msg.RoundId,
                leaderSessionId: msg.LeaderSessionId
            );

            _audit.Record(new AuditEvent(
                Sequence: 0,
                OccurredAt: DateTimeOffset.UtcNow,
                MonotonicMs: _clock.ReadMonotonicMs(),
                SourceNodeId: peerId,
                Kind: AuditEventKind.MessageReceived,
                Summary: $"NHẬN [TIME_RESPONSE] từ {peerId}: t2={t2}, t3={t3} (Drift: {drift / 1000:F1}s)"
            ));

            MessageReceived?.Invoke(respEnv);
        }
        else if (msg.MessageType == MessageTypes.Adjust)
        {
            var adj = msg.DeserializePayload<AdjustPayload>();
            if (adj != null)
            {
                // Giả lập áp dụng Step: cập nhật độ lệch drift của Worker đó
                double adjustment = adj.AdjustmentMs;
                _simulatedDriftMs[peerId] = drift + adjustment;

                var result = new AdjustmentResult(
                    NodeId: peerId,
                    RoundId: msg.RoundId ?? "R-0",
                    RequestedAdjustmentMs: adjustment,
                    ClockBeforeUtcMs: (long)(nowUtc + drift),
                    ClockAfterUtcMs: (long)(nowUtc + _simulatedDriftMs[peerId]),
                    Status: AdjustmentStatus.Applied
                );

                var respEnv = MessageEnvelope.Create(
                    messageType: MessageTypes.AdjustResult,
                    sourceNodeId: peerId,
                    sourceBootId: Guid.NewGuid(),
                    targetNodeId: msg.SourceNodeId,
                    payload: new AdjustResultPayload(result),
                    roundId: msg.RoundId,
                    leaderSessionId: msg.LeaderSessionId
                );

                _audit.Record(new AuditEvent(
                    Sequence: 0,
                    OccurredAt: DateTimeOffset.UtcNow,
                    MonotonicMs: _clock.ReadMonotonicMs(),
                    SourceNodeId: peerId,
                    Kind: AuditEventKind.ClockAdjusted,
                    Summary: $"Worker {peerId} ĐÃ STEP: {adjustment:+0.0;-0.0}ms -> Lệch mới: {_simulatedDriftMs[peerId]:+0.0;-0.0}ms"
                ));

                MessageReceived?.Invoke(respEnv);
            }
        }
    }

    public void InjectPeerDrift(string peerId, double deltaMs)
    {
        if (_simulatedDriftMs.ContainsKey(peerId))
        {
            _simulatedDriftMs[peerId] += deltaMs;
        }
    }

    public void Dispose() { _disposed = true; MessageReceived = null; }

    public IReadOnlyDictionary<string, double> GetDrifts() => _simulatedDriftMs;
}
