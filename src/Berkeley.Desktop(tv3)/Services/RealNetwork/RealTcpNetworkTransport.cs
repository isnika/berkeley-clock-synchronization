using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Berkeley.Contracts;

namespace Berkeley.Desktop.Services.RealNetwork;

/// <summary>
/// Trình vận chuyển mạng TCP Socket thực tế giữa các máy tính (laptop) trong mạng LAN/Wi-Fi.
/// Sử dụng quy tắc phân khung: 4-byte Big-Endian độ dài + JSON UTF-8 của MessageEnvelope.
/// Hỗ trợ kiểm tra heartbeat và tự động kết nối lại khi mất mạng.
/// </summary>
public sealed class RealTcpNetworkTransport : INetworkTransport, IAsyncDisposable, IDisposable
{
    private readonly NodeConfig _config;
    private readonly IClockReader _clock;
    private readonly IAuditSink _audit;
    private readonly ConcurrentDictionary<string, PeerConfig> _peerConfigs = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeenPeers = new();

    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;
    private Task? _heartbeatTask;
    private bool _disposed;

    public event Action<MessageEnvelope>? MessageReceived;

    public RealTcpNetworkTransport(NodeConfig config, IClockReader clock, IAuditSink audit)
    {
        _config = config;
        _clock = clock;
        _audit = audit;

        foreach (var peer in _config.Peers)
        {
            _peerConfigs[peer.NodeId] = peer;
        }

        StartListener();
        _heartbeatTask = Task.Run(HeartbeatLoopAsync);
    }

    private void StartListener()
    {
        try
        {
            IPAddress bindAddress = IPAddress.Any;
            if (!string.IsNullOrWhiteSpace(_config.ListenAddress) &&
                _config.ListenAddress != "0.0.0.0" &&
                IPAddress.TryParse(_config.ListenAddress, out var parsed))
            {
                bindAddress = parsed;
            }

            _listener = new TcpListener(bindAddress, _config.ListenPort);
            _listener.Start();

            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.PeerConnected,
                $"[TCP Server] Đang lắng nghe trên {bindAddress}:{_config.ListenPort}"));

            _listenTask = Task.Run(AcceptConnectionsLoopAsync);
        }
        catch (Exception ex)
        {
            _audit.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                _config.NodeId, AuditEventKind.SendError,
                $"[TCP Server LỖI] Không thể mở cổng {_config.ListenPort}: {ex.Message}"));
        }
    }

    private async Task AcceptConnectionsLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Thử lại nếu có lỗi tạm thời
                await Task.Delay(200);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                try
                {
                    // 1. Đọc tiền tố độ dài 4 byte
                    byte[] lengthBuffer = new byte[4];
                    int bytesRead = await ReadExactAsync(stream, lengthBuffer, 0, 4, ct);
                    if (bytesRead < 4) break; // Client đã ngắt kết nối

                    int messageLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));
                    if (messageLength <= 0 || messageLength > 10 * 1024 * 1024) // Giới hạn max 10MB
                    {
                        break;
                    }

                    // 2. Đọc payload JSON
                    byte[] payloadBuffer = new byte[messageLength];
                    int payloadRead = await ReadExactAsync(stream, payloadBuffer, 0, messageLength, ct);
                    if (payloadRead < messageLength) break;

                    string json = Encoding.UTF8.GetString(payloadBuffer);
                    var envelope = JsonSerializer.Deserialize<MessageEnvelope>(json);

                    if (envelope != null)
                    {
                        _lastSeenPeers[envelope.SourceNodeId] = DateTimeOffset.UtcNow;

                        // Ghi log
                        _audit.Record(new AuditEvent(
                            Sequence: 0,
                            OccurredAt: DateTimeOffset.UtcNow,
                            MonotonicMs: _clock.ReadMonotonicMs(),
                            SourceNodeId: envelope.SourceNodeId,
                            Kind: AuditEventKind.MessageReceived,
                            Summary: $"NHẬN [{envelope.MessageType}] từ {envelope.SourceNodeId} (Round: {envelope.RoundId ?? "N/A"})"
                        ));

                        MessageReceived?.Invoke(envelope);
                    }
                }
                catch
                {
                    break;
                }
            }
        }
    }

    public async Task<bool> SendAsync(string targetNodeId, MessageEnvelope message, CancellationToken ct = default)
    {
        if (!_peerConfigs.TryGetValue(targetNodeId, out var peer))
            return false;

        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(3000); // 3 giây timeout kết nối

            await client.ConnectAsync(peer.Address, peer.Port, connectCts.Token);

            await using var stream = client.GetStream();
            string json = JsonSerializer.Serialize(message);
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] lengthHeader = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));

            await stream.WriteAsync(lengthHeader, ct);
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);

            _audit.Record(new AuditEvent(
                Sequence: 0,
                OccurredAt: DateTimeOffset.UtcNow,
                MonotonicMs: _clock.ReadMonotonicMs(),
                SourceNodeId: message.SourceNodeId,
                Kind: AuditEventKind.MessageSent,
                Summary: $"GỬI [{message.MessageType}] tới {targetNodeId} ({peer.Address}:{peer.Port})"
            ));

            return true;
        }
        catch (Exception ex)
        {
            _audit.Record(new AuditEvent(
                Sequence: 0,
                OccurredAt: DateTimeOffset.UtcNow,
                MonotonicMs: _clock.ReadMonotonicMs(),
                SourceNodeId: message.SourceNodeId,
                Kind: AuditEventKind.SendError,
                Summary: $"LỖI GỬI [{message.MessageType}] tới {targetNodeId}: {ex.Message}"
            ));
            return false;
        }
    }

    public async Task BroadcastAsync(MessageEnvelope message, CancellationToken ct = default)
    {
        var tasks = _peerConfigs.Keys.Select(peerId => SendAsync(peerId, message, ct));
        await Task.WhenAll(tasks);
    }

    public IReadOnlyList<string> GetConnectedNodeIds()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMilliseconds(-_config.OperationParams.HeartbeatTimeoutMs);
        return _lastSeenPeers
            .Where(kvp => kvp.Value >= cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    private async Task HeartbeatLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.OperationParams.HeartbeatIntervalMs, _cts.Token);

                // Gửi Ping tới các peers
                var pingEnvelope = MessageEnvelope.Create(
                    messageType: MessageTypes.Ping,
                    sourceNodeId: _config.NodeId,
                    sourceBootId: Guid.NewGuid(),
                    targetNodeId: "*",
                    payload: new { Time = _clock.ReadUtcMs() }
                );

                await BroadcastAsync(pingEnvelope, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Bỏ qua lỗi vòng lặp heartbeat
            }
        }
    }

    private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0) return totalRead;
            totalRead += read;
        }
        return totalRead;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _listener?.Stop();
        if (_listenTask != null) await _listenTask;
        if (_heartbeatTask != null) await _heartbeatTask;
        _cts.Dispose();
    }
}
