using Berkeley.Contracts;

namespace Berkeley.Networking;

/// <summary>
/// Bully leader election over INetworkTransport.
///
/// The election session is carried in MessageEnvelope.LeaderSessionId for
/// ELECTION, OK and COORDINATOR messages. A node accepts a newer session, or
/// the higher node ID when two nodes announce the same session.
/// </summary>
public sealed class BullyLeadershipService : ILeadershipService, IDisposable, IAsyncDisposable
{
    private readonly NodeConfig _config;
    private readonly string _currentNodeId;
    private readonly INetworkTransport _transport;
    private readonly IClockReader _clock;
    private readonly IAuditSink _audit;
    private readonly IPeerLiveness? _liveness;
    private readonly Guid _bootId = Guid.NewGuid();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _electionGate = new(1, 1);
    private readonly object _stateLock = new();

    private LeaderSession? _currentLeader;
    private long _lastSessionId;
    private long? _activeElectionSession;
    private TaskCompletionSource<bool>? _okReceived;
    private TaskCompletionSource<LeaderSession>? _coordinatorReceived;
    private Task? _watchTask;
    private bool _disposed;

    public LeaderSession? CurrentLeader
    {
        get
        {
            lock (_stateLock)
                return _currentLeader;
        }
    }

    public bool IAmMaster => CurrentLeader?.MasterNodeId == _currentNodeId;

    public bool IsElecting
    {
        get
        {
            lock (_stateLock)
                return _activeElectionSession.HasValue;
        }
    }

    public event Action<LeaderSession?>? LeaderChanged;

    public BullyLeadershipService(
        NodeConfig config,
        INetworkTransport transport,
        IClockReader clock,
        IAuditSink audit,
        bool autoStart = true)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _currentNodeId = config.NodeId;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _liveness = transport as IPeerLiveness;

        string initialLeader = config.Peers
            .Select(peer => peer.NodeId)
            .Append(_currentNodeId)
            .OrderByDescending(nodeId => nodeId, StringComparer.OrdinalIgnoreCase)
            .First();

        _currentLeader = new LeaderSession(initialLeader, 1);
        _lastSessionId = 1;

        _transport.MessageReceived += OnMessageReceived;
        if (_liveness is not null)
            _liveness.PeerConnectionChanged += OnPeerConnectionChanged;

        if (autoStart)
            _watchTask = Task.Run(WatchCoordinatorAsync);
    }

    /// <summary>
    /// Starts a Bully election if this node is not already running one.
    /// Concurrent requests on the same node are coalesced and return without
    /// starting a second election.
    /// </summary>
    public async Task StartElectionAsync(CancellationToken ct = default)
    {
        if (_disposed || !await _electionGate.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        try
        {
            await RunElectionAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_stateLock)
            {
                _activeElectionSession = null;
                _okReceived = null;
                _coordinatorReceived = null;
            }
            _electionGate.Release();
        }
    }

    private async Task RunElectionAsync(CancellationToken ct)
    {
        long sessionId = NextSessionId();
        var higherNodes = _config.Peers
            .Where(peer => IsHigherPriority(peer.NodeId, _currentNodeId))
            .Select(peer => peer.NodeId)
            .ToList();

        while (!_disposed && !ct.IsCancellationRequested)
        {
            BeginElectionWait(sessionId);

            var election = MessageEnvelope.Create(
                messageType: MessageTypes.Election,
                sourceNodeId: _currentNodeId,
                sourceBootId: _bootId,
                targetNodeId: "*",
                payload: new ElectionPayload(_currentNodeId, sessionId),
                leaderSessionId: sessionId);

            var sends = higherNodes
                .Select(nodeId => SendSafelyAsync(nodeId, election, ct))
                .ToArray();

            if (sends.Length > 0)
                await Task.WhenAll(sends).ConfigureAwait(false);

            if (higherNodes.Count == 0)
            {
                await DeclareCoordinatorAsync(sessionId, ct).ConfigureAwait(false);
                return;
            }

            bool receivedOk = await WaitForOkAsync(ct).ConfigureAwait(false);
            if (!receivedOk)
            {
                await DeclareCoordinatorAsync(sessionId, ct).ConfigureAwait(false);
                return;
            }

            bool receivedCoordinator = await WaitForCoordinatorAsync(ct).ConfigureAwait(false);
            if (receivedCoordinator)
                return;

            // An OK without a COORDINATOR means the higher node did not finish
            // its own election. Start a fresh session rather than remaining in
            // an indeterminate state.
            sessionId = NextSessionId();
        }
    }

    private void BeginElectionWait(long sessionId)
    {
        lock (_stateLock)
        {
            _activeElectionSession = sessionId;
            _okReceived = NewTcs<bool>();
            _coordinatorReceived = NewTcs<LeaderSession>();
            _lastSessionId = Math.Max(_lastSessionId, sessionId);
        }

        RecordAudit(AuditEventKind.LeaderChanged,
            $"[BULLY] {_currentNodeId} starts election session {sessionId}");
    }

    private async Task<bool> WaitForOkAsync(CancellationToken ct)
    {
        Task<bool>? waitTask;
        lock (_stateLock)
            waitTask = _okReceived?.Task;

        if (waitTask is null)
            return false;

        try
        {
            await waitTask.WaitAsync(GetElectionTimeout(), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task<bool> WaitForCoordinatorAsync(CancellationToken ct)
    {
        Task<LeaderSession>? waitTask;
        lock (_stateLock)
            waitTask = _coordinatorReceived?.Task;

        if (waitTask is null)
            return false;

        try
        {
            await waitTask.WaitAsync(GetElectionTimeout(), ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task DeclareCoordinatorAsync(long sessionId, CancellationToken ct)
    {
        var leader = new LeaderSession(_currentNodeId, sessionId);
        SetLeaderIfNewer(leader);

        var coordinator = MessageEnvelope.Create(
            messageType: MessageTypes.Coordinator,
            sourceNodeId: _currentNodeId,
            sourceBootId: _bootId,
            targetNodeId: "*",
            payload: new ElectionPayload(_currentNodeId, sessionId),
            leaderSessionId: sessionId);

        await _transport.BroadcastAsync(coordinator, ct).ConfigureAwait(false);
        RecordAudit(AuditEventKind.LeaderChanged,
            $"[BULLY] {_currentNodeId} declared COORDINATOR session {sessionId}");
    }

    private void OnMessageReceived(MessageEnvelope message)
    {
        if (_disposed || string.Equals(message.SourceNodeId, _currentNodeId, StringComparison.OrdinalIgnoreCase))
            return;

        switch (message.MessageType)
        {
            case MessageTypes.Election:
                HandleElection(message);
                break;
            case MessageTypes.Ok:
                HandleOk(message);
                break;
            case MessageTypes.Coordinator:
                HandleCoordinator(message);
                break;
        }
    }

    private void HandleElection(MessageEnvelope message)
    {
        if (!IsKnownPeer(message.SourceNodeId) ||
            !IsHigherPriority(_currentNodeId, message.SourceNodeId))
        {
            return;
        }

        var payload = message.DeserializePayload<ElectionPayload>();
        long? sessionId = message.LeaderSessionId ?? payload?.LeaderSessionId;
        if (!sessionId.HasValue)
            return;

        var ok = MessageEnvelope.Create(
            messageType: MessageTypes.Ok,
            sourceNodeId: _currentNodeId,
            sourceBootId: _bootId,
            targetNodeId: message.SourceNodeId,
            payload: new ElectionPayload(_currentNodeId, sessionId),
            leaderSessionId: sessionId);

        _ = SendSafelyAsync(message.SourceNodeId, ok, _cts.Token);
        _ = StartElectionAsync(_cts.Token);
    }

    private void HandleOk(MessageEnvelope message)
    {
        if (!IsKnownPeer(message.SourceNodeId) ||
            !IsHigherPriority(message.SourceNodeId, _currentNodeId))
        {
            return;
        }

        long? activeSession;
        lock (_stateLock)
            activeSession = _activeElectionSession;

        if (!activeSession.HasValue || message.LeaderSessionId != activeSession)
            return;

        var payload = message.DeserializePayload<ElectionPayload>();
        if (payload is null ||
            !string.Equals(payload.CandidateNodeId, message.SourceNodeId, StringComparison.OrdinalIgnoreCase) ||
            payload.LeaderSessionId != activeSession)
        {
            return;
        }

        lock (_stateLock)
            _okReceived?.TrySetResult(true);
    }

    private void HandleCoordinator(MessageEnvelope message)
    {
        if (!IsKnownPeer(message.SourceNodeId))
            return;

        var payload = message.DeserializePayload<ElectionPayload>();
        string leaderNodeId = payload?.CandidateNodeId ?? message.SourceNodeId;
        long? sessionId = message.LeaderSessionId ?? payload?.LeaderSessionId;

        if (!sessionId.HasValue ||
            !string.Equals(leaderNodeId, message.SourceNodeId, StringComparison.OrdinalIgnoreCase) ||
            !IsKnownPeer(leaderNodeId))
        {
            return;
        }

        var leader = new LeaderSession(leaderNodeId, sessionId.Value);
        if (SetLeaderIfNewer(leader))
        {
            lock (_stateLock)
            {
                _okReceived?.TrySetResult(true);
                _coordinatorReceived?.TrySetResult(leader);
            }
        }
        else
        {
            lock (_stateLock)
            {
                if (_currentLeader == leader)
                {
                    _okReceived?.TrySetResult(true);
                    _coordinatorReceived?.TrySetResult(leader);
                }
            }
        }
    }

    private bool SetLeaderIfNewer(LeaderSession leader)
    {
        LeaderSession? previous;
        lock (_stateLock)
        {
            previous = _currentLeader;
            if (previous is not null)
            {
                if (leader.LeaderSessionId < previous.LeaderSessionId)
                    return false;

                if (leader.LeaderSessionId == previous.LeaderSessionId)
                {
                    if (string.Equals(leader.MasterNodeId, previous.MasterNodeId, StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (!IsHigherPriority(leader.MasterNodeId, previous.MasterNodeId))
                        return false;
                }
            }

            _currentLeader = leader;
            _lastSessionId = Math.Max(_lastSessionId, leader.LeaderSessionId);
        }

        RecordAudit(AuditEventKind.LeaderChanged,
            $"Leader changed: {previous?.MasterNodeId ?? "none"} -> {leader.MasterNodeId} session {leader.LeaderSessionId}");

        try
        {
            LeaderChanged?.Invoke(leader);
        }
        catch (Exception ex)
        {
            RecordAudit(AuditEventKind.MessageRejected,
                $"LeaderChanged handler failed: {ex.Message}");
        }

        return true;
    }

    private void OnPeerConnectionChanged(string nodeId, PeerConnectionState state)
    {
        if (state != PeerConnectionState.Disconnected)
            return;

        var leader = CurrentLeader;
        if (leader is null ||
            string.Equals(leader.MasterNodeId, _currentNodeId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(leader.MasterNodeId, nodeId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = StartElectionAsync(_cts.Token);
    }

    private async Task WatchCoordinatorAsync()
    {
        var intervalMs = Math.Max(10, Math.Min(
            _config.OperationParams.HeartbeatIntervalMs,
            _config.OperationParams.ElectionTimeoutMs));

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, _cts.Token).ConfigureAwait(false);
                var leader = CurrentLeader;
                if (leader is null ||
                    string.Equals(leader.MasterNodeId, _currentNodeId, StringComparison.OrdinalIgnoreCase) ||
                    _liveness is null)
                {
                    continue;
                }

                if (_liveness.GetPeerConnectionState(leader.MasterNodeId) == PeerConnectionState.Disconnected)
                    _ = StartElectionAsync(_cts.Token);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private long NextSessionId()
    {
        lock (_stateLock)
            return ++_lastSessionId;
    }

    private bool IsKnownPeer(string nodeId) =>
        _config.Peers.Any(peer => string.Equals(peer.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));

    private static bool IsHigherPriority(string left, string right) =>
        string.Compare(left, right, StringComparison.OrdinalIgnoreCase) > 0;

    private TimeSpan GetElectionTimeout() => TimeSpan.FromMilliseconds(
        Math.Max(1, _config.OperationParams.ElectionTimeoutMs));

    private async Task<bool> SendSafelyAsync(
        string targetNodeId,
        MessageEnvelope message,
        CancellationToken ct)
    {
        try
        {
            return await _transport.SendAsync(targetNodeId, message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            RecordAudit(AuditEventKind.SendError,
                $"Election message failed to {targetNodeId}: {ex.Message}");
            return false;
        }
    }

    private static TaskCompletionSource<T> NewTcs<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void RecordAudit(AuditEventKind kind, string summary)
    {
        try
        {
            _audit.Record(new AuditEvent(
                Sequence: 0,
                OccurredAt: DateTimeOffset.UtcNow,
                MonotonicMs: _clock.ReadMonotonicMs(),
                SourceNodeId: _currentNodeId,
                Kind: kind,
                Summary: summary));
        }
        catch
        {
            // Audit must not break election progress.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _transport.MessageReceived -= OnMessageReceived;
        if (_liveness is not null)
            _liveness.PeerConnectionChanged -= OnPeerConnectionChanged;
        _cts.Cancel();
        _electionGate.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _transport.MessageReceived -= OnMessageReceived;
        if (_liveness is not null)
            _liveness.PeerConnectionChanged -= OnPeerConnectionChanged;
        _cts.Cancel();

        if (_watchTask is not null)
        {
            try
            {
                await _watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _electionGate.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }
}
