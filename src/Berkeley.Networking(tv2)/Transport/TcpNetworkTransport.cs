using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Berkeley.Contracts;

namespace Berkeley.Networking;

/// <summary>
/// TCP transport for the Berkeley protocol.
///
/// Every frame is encoded as a four-byte big-endian length followed by a
/// UTF-8 JSON MessageEnvelope. The transport owns its listener, heartbeat
/// loop and accepted-client tasks and can be stopped and started again.
/// </summary>
public sealed class TcpNetworkTransport : INetworkTransport, IPeerLiveness, IDisposable, IAsyncDisposable
{
    private const int MaxFrameBytes = 10 * 1024 * 1024;

    private readonly NodeConfig _config;
    private readonly IClockReader _clock;
    private readonly IAuditSink _audit;
    private readonly Dictionary<string, PeerConfig> _peerConfigs;
    private readonly ConcurrentDictionary<string, long> _lastSeenMonotonicMs = new();
    private readonly Dictionary<string, PeerConnectionState> _peerStates;
    private readonly object _lifecycleLock = new();
    private readonly object _membershipLock = new();
    private readonly Guid _bootId = Guid.NewGuid();

    private CancellationTokenSource? _lifetimeCts;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private Task? _heartbeatTask;
    private readonly ConcurrentDictionary<Task, byte> _clientTasks = new();
    private bool _started;
    private bool _disposed;
    private long _startedAtMonotonicMs;
    private int _localPort;

    public event Action<MessageEnvelope>? MessageReceived;

    public event Action<string, PeerConnectionState>? PeerConnectionChanged;

    public TcpNetworkTransport(
        NodeConfig config,
        IClockReader clock,
        IAuditSink audit,
        bool autoStart = true)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));

        _peerConfigs = config.Peers
            .GroupBy(peer => peer.NodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        _peerStates = _peerConfigs.Keys.ToDictionary(
            nodeId => nodeId,
            _ => PeerConnectionState.Unknown,
            StringComparer.OrdinalIgnoreCase);

        if (autoStart)
        {
            Start();
        }
    }

    public bool IsStarted
    {
        get
        {
            lock (_lifecycleLock)
                return _started;
        }
    }

    public int LocalPort
    {
        get
        {
            lock (_lifecycleLock)
                return _localPort;
        }
    }

    public IReadOnlyDictionary<string, PeerConnectionState> GetPeerStates()
    {
        lock (_membershipLock)
            return new Dictionary<string, PeerConnectionState>(_peerStates, StringComparer.OrdinalIgnoreCase);
    }

    public long? GetLastSeenMonotonicMs(string nodeId)
    {
        return _lastSeenMonotonicMs.TryGetValue(nodeId, out var value) ? value : null;
    }

    public PeerConnectionState GetPeerConnectionState(string nodeId)
    {
        RefreshMembership();
        lock (_membershipLock)
        {
            return _peerStates.TryGetValue(nodeId, out var state)
                ? state
                : PeerConnectionState.Unknown;
        }
    }

    public void Start()
    {
        TcpListener listener;
        CancellationTokenSource lifetimeCts;
        int localPort;

        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (_started)
                return;

            listener = new TcpListener(ResolveListenAddress(_config.ListenAddress), _config.ListenPort);
            try
            {
                listener.Start();
            }
            catch
            {
                listener.Stop();
                throw;
            }

            lifetimeCts = new CancellationTokenSource();
            _listener = listener;
            _lifetimeCts = lifetimeCts;
            _startedAtMonotonicMs = _clock.ReadMonotonicMs();
            localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _localPort = localPort;
            _started = true;

            lock (_membershipLock)
            {
                _lastSeenMonotonicMs.Clear();
                foreach (var nodeId in _peerStates.Keys.ToList())
                    _peerStates[nodeId] = PeerConnectionState.Unknown;
            }

            _acceptTask = Task.Run(() => AcceptLoopAsync(listener, lifetimeCts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(lifetimeCts.Token));
        }

        RecordAudit(AuditEventKind.PeerConnected,
            $"[TCP] Listening on {_config.ListenAddress}:{localPort}");
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? lifetimeCts;
        TcpListener? listener;
        Task? acceptTask;
        Task? heartbeatTask;

        lock (_lifecycleLock)
        {
            if (!_started)
                return;

            _started = false;
            lifetimeCts = _lifetimeCts;
            listener = _listener;
            acceptTask = _acceptTask;
            heartbeatTask = _heartbeatTask;
            _lifetimeCts = null;
            _listener = null;
            _acceptTask = null;
            _heartbeatTask = null;
            _localPort = 0;
        }

        lifetimeCts?.Cancel();
        listener?.Stop();

        await AwaitQuietlyAsync(acceptTask, ct).ConfigureAwait(false);
        await AwaitQuietlyAsync(heartbeatTask, ct).ConfigureAwait(false);

        var clientTasks = _clientTasks.Keys.ToArray();
        if (clientTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(clientTasks).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A client closing while the listener is stopping is expected.
            }
        }

        lifetimeCts?.Dispose();
        RefreshMembership();
        RecordAudit(AuditEventKind.PeerDisconnected, "[TCP] Transport stopped");
    }

    public async Task<bool> SendAsync(
        string targetNodeId,
        MessageEnvelope message,
        CancellationToken ct = default)
    {
        if (message is null || !IsStarted ||
            !_peerConfigs.TryGetValue(targetNodeId, out var peer))
        {
            return false;
        }

        CancellationToken lifetimeToken;
        lock (_lifecycleLock)
        {
            if (!_started || _lifetimeCts is null)
                return false;
            lifetimeToken = _lifetimeCts.Token;
        }

        try
        {
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
            ioCts.CancelAfter(GetIoTimeoutMs());

            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(peer.Address, peer.Port, ioCts.Token).ConfigureAwait(false);

            await using var stream = client.GetStream();
            await WriteFrameAsync(stream, message, ioCts.Token).ConfigureAwait(false);

            RecordAudit(AuditEventKind.MessageSent,
                $"SEND [{message.MessageType}] -> {targetNodeId} ({peer.Address}:{peer.Port})",
                message.SourceNodeId);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RecordAudit(AuditEventKind.SendError,
                $"SEND timeout [{message.MessageType}] -> {targetNodeId}",
                message.SourceNodeId);
            return false;
        }
        catch (Exception ex)
        {
            RecordAudit(AuditEventKind.SendError,
                $"SEND failed [{message.MessageType}] -> {targetNodeId}: {ex.Message}",
                message.SourceNodeId);
            return false;
        }
    }

    public async Task BroadcastAsync(MessageEnvelope message, CancellationToken ct = default)
    {
        var sends = _peerConfigs.Keys
            .Select(nodeId => SendAsync(nodeId, message, ct))
            .ToArray();

        if (sends.Length > 0)
            await Task.WhenAll(sends).ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetConnectedNodeIds()
    {
        RefreshMembership();
        lock (_membershipLock)
        {
            return _peerStates
                .Where(pair => pair.Value == PeerConnectionState.Connected)
                .Select(pair => pair.Key)
                .ToList();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;

                var task = Task.Run(() => HandleClientAsync(client, ct));
                _clientTasks.TryAdd(task, 0);
                _ = task.ContinueWith(
                    completed => _clientTasks.TryRemove(completed, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordAudit(AuditEventKind.SendError, $"TCP accept failed: {ex.Message}");
                if (!ct.IsCancellationRequested)
                    await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            await using var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                MessageEnvelope? envelope;
                try
                {
                    var frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
                    if (frame is null)
                        break;

                    envelope = JsonSerializer.Deserialize<MessageEnvelope>(frame);
                    if (envelope is null)
                        break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    RecordAudit(AuditEventKind.MessageRejected, "TCP frame read timeout");
                    break;
                }
                catch (JsonException ex)
                {
                    RecordAudit(AuditEventKind.MessageRejected, $"Invalid JSON frame: {ex.Message}");
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                if (!IsAcceptedEnvelope(envelope))
                {
                    RecordAudit(AuditEventKind.MessageRejected,
                        $"Rejected message from {envelope.SourceNodeId}");
                    continue;
                }

                MarkPeerSeen(envelope.SourceNodeId);
                RecordAudit(AuditEventKind.MessageReceived,
                    $"RECV [{envelope.MessageType}] <- {envelope.SourceNodeId}",
                    envelope.SourceNodeId);

                if (envelope.MessageType == MessageTypes.Ping)
                    await SendPongAsync(envelope, ct).ConfigureAwait(false);

                PublishMessage(envelope);
            }
        }
    }

    private async Task SendPongAsync(MessageEnvelope ping, CancellationToken ct)
    {
        var pong = MessageEnvelope.Create(
            messageType: MessageTypes.Pong,
            sourceNodeId: _config.NodeId,
            sourceBootId: _bootId,
            targetNodeId: ping.SourceNodeId,
            payload: new HeartbeatPayload(_clock.ReadUtcMs(), _clock.ReadMonotonicMs()),
            leaderSessionId: ping.LeaderSessionId);

        await SendAsync(ping.SourceNodeId, pong, ct).ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, _config.OperationParams.HeartbeatIntervalMs));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                RefreshMembership();

                var ping = MessageEnvelope.Create(
                    messageType: MessageTypes.Ping,
                    sourceNodeId: _config.NodeId,
                    sourceBootId: _bootId,
                    targetNodeId: "*",
                    payload: new HeartbeatPayload(_clock.ReadUtcMs(), _clock.ReadMonotonicMs()));

                await BroadcastAsync(ping, ct).ConfigureAwait(false);
                RefreshMembership();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordAudit(AuditEventKind.SendError, $"Heartbeat loop failed: {ex.Message}");
            }
        }
    }

    private void RefreshMembership()
    {
        long now = _clock.ReadMonotonicMs();
        long timeout = Math.Max(1, _config.OperationParams.HeartbeatTimeoutMs);
        long startedAt;

        lock (_lifecycleLock)
        {
            if (!_started)
                return;
            startedAt = _startedAtMonotonicMs;
        }

        foreach (var nodeId in _peerConfigs.Keys)
        {
            PeerConnectionState state;
            if (_lastSeenMonotonicMs.TryGetValue(nodeId, out var lastSeen))
            {
                state = now - lastSeen < timeout
                    ? PeerConnectionState.Connected
                    : PeerConnectionState.Disconnected;
            }
            else
            {
                state = now - startedAt >= timeout
                    ? PeerConnectionState.Disconnected
                    : PeerConnectionState.Unknown;
            }

            SetPeerState(nodeId, state);
        }
    }

    private void MarkPeerSeen(string nodeId)
    {
        if (!_peerConfigs.ContainsKey(nodeId))
            return;

        _lastSeenMonotonicMs[nodeId] = _clock.ReadMonotonicMs();
        SetPeerState(nodeId, PeerConnectionState.Connected);
    }

    private void SetPeerState(string nodeId, PeerConnectionState state)
    {
        PeerConnectionState previous;
        lock (_membershipLock)
        {
            if (!_peerStates.TryGetValue(nodeId, out previous) || previous == state)
                return;
            _peerStates[nodeId] = state;
        }

        try
        {
            PeerConnectionChanged?.Invoke(nodeId, state);
        }
        catch (Exception ex)
        {
            RecordAudit(AuditEventKind.SendError,
                $"PeerConnectionChanged handler failed for {nodeId}: {ex.Message}");
        }
    }

    private bool IsAcceptedEnvelope(MessageEnvelope envelope)
    {
        if (string.Equals(envelope.SourceNodeId, _config.NodeId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!_peerConfigs.ContainsKey(envelope.SourceNodeId))
            return false;

        return envelope.TargetNodeId == "*" ||
               string.Equals(envelope.TargetNodeId, _config.NodeId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        frameCts.CancelAfter(GetFrameReadTimeoutMs());

        var header = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, header, frameCts.Token).ConfigureAwait(false))
            return null;

        int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(header, 0));
        if (length <= 0 || length > MaxFrameBytes)
            return null;

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, frameCts.Token).ConfigureAwait(false))
            return null;

        return payload;
    }

    private static async Task<bool> ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                return false;
            total += read;
        }

        return true;
    }

    private static async Task WriteFrameAsync(
        NetworkStream stream,
        MessageEnvelope message,
        CancellationToken ct)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private int GetFrameReadTimeoutMs() => Math.Max(1, _config.OperationParams.HeartbeatTimeoutMs);

    private int GetIoTimeoutMs() => Math.Max(
        100,
        Math.Max(_config.OperationParams.HeartbeatTimeoutMs,
            _config.OperationParams.ElectionTimeoutMs));

    private static IPAddress ResolveListenAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address == "0.0.0.0")
            return IPAddress.Any;

        return IPAddress.TryParse(address, out var parsed)
            ? parsed
            : IPAddress.Any;
    }

    private void PublishMessage(MessageEnvelope envelope)
    {
        try
        {
            MessageReceived?.Invoke(envelope);
        }
        catch (Exception ex)
        {
            RecordAudit(AuditEventKind.MessageRejected,
                $"MessageReceived handler failed: {ex.Message}",
                envelope.SourceNodeId);
        }
    }

    private void RecordAudit(
        AuditEventKind kind,
        string summary,
        string? sourceNodeId = null)
    {
        try
        {
            _audit.Record(new AuditEvent(
                Sequence: 0,
                OccurredAt: DateTimeOffset.UtcNow,
                MonotonicMs: _clock.ReadMonotonicMs(),
                SourceNodeId: sourceNodeId ?? _config.NodeId,
                Kind: kind,
                Summary: summary));
        }
        catch
        {
            // Audit must not terminate a network task.
        }
    }

    private static async Task AwaitQuietlyAsync(Task? task, CancellationToken ct)
    {
        if (task is null)
            return;

        try
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Listener shutdown exceptions are not allowed to block cleanup.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TcpNetworkTransport));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Synchronous dispose has no caller cancellation token.
        }

        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
