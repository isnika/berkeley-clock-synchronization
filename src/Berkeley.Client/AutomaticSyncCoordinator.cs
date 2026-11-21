using Berkeley.Contracts;

namespace Berkeley.Client;

// Readiness is reported by every client, including clients with AutoSync off.
// Only the Master schedules rounds. No UTC subtraction is used for leases/scheduling.
internal sealed class AutomaticSyncCoordinator : IAsyncDisposable
{
    private const string StatusType = "SYNC_STATUS";
    public sealed record Status(string? Master, long? Session, bool Electing);
    private readonly INetworkTransport _transport;
    private readonly NodeApplicationAdapter _local;
    private readonly Dictionary<string, (Guid Boot, Status Status, long Seen)> _reports = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts;
    private readonly Task _task;

    internal sealed record NodeApplicationAdapter(NodeConfig Config, Guid Boot, bool Enabled, int IntervalSeconds,
        Func<NodeSnapshot?> Snapshot, Func<bool> Electing, Func<long> Now,
        Func<LeaderSession, CancellationToken, Task<bool>> Sync, Action<Exception> Error);

    public AutomaticSyncCoordinator(INetworkTransport transport, NodeApplicationAdapter local, CancellationToken ct)
    {
        _transport = transport; _local = local;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _transport.MessageReceived += Receive;
        _task = RunAsync(_cts.Token);
    }

    private void Receive(MessageEnvelope message)
    {
        if (message.MessageType != StatusType || !_local.Config.Peers.Any(p =>
            string.Equals(p.NodeId, message.SourceNodeId, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            if (message.DeserializePayload<Status>() is { } status)
                lock (_lock) _reports[message.SourceNodeId] = (message.SourceBootId, status, _local.Now());
        }
        catch (System.Text.Json.JsonException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string? candidate = null, completed = null;
        long stableSince = 0, nextDue = 0;
        int tick = Math.Clamp(_local.Config.OperationParams.HeartbeatIntervalMs, 100, 1000);
        long settle = Math.Max(250L, 2L * tick);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = _local.Snapshot();
                if (snapshot is null) { await Task.Delay(tick, ct); continue; }
                await _transport.BroadcastAsync(MessageEnvelope.Create(StatusType, _local.Config.NodeId,
                    _local.Boot, "*", new Status(snapshot.CurrentMasterId, snapshot.LeaderSessionId, _local.Electing())), ct);
                long now = _local.Now();
                var peers = _transport.GetConnectedNodeIds().Order(StringComparer.OrdinalIgnoreCase).ToArray();
                bool ready = !_local.Electing() && snapshot.CurrentMasterId == _local.Config.NodeId && peers.Length > 0;
                string fingerprint = $"{snapshot.CurrentMasterId}/{snapshot.LeaderSessionId}";
                lock (_lock)
                    foreach (string peer in peers)
                    {
                        if (!_reports.TryGetValue(peer, out var r) || r.Status.Electing ||
                            r.Status.Master != snapshot.CurrentMasterId || r.Status.Session != snapshot.LeaderSessionId ||
                            now - r.Seen > _local.Config.OperationParams.HeartbeatTimeoutMs) ready = false;
                        else fingerprint += $"|{peer}/{r.Boot}";
                    }
                if (!ready) { candidate = null; completed = null; }
                else if (candidate != fingerprint) { candidate = fingerprint; stableSince = now; }
                else if (_local.Enabled && now - stableSince >= settle &&
                    (completed != fingerprint || now >= nextDue))
                {
                    if (await _local.Sync(new(snapshot.CurrentMasterId!, snapshot.LeaderSessionId!.Value), ct)) completed = fingerprint;
                    // No busy loop on failures or missing permission. A changed membership
                    // still triggers promptly; periodic retries wait a full interval.
                    nextDue = _local.Now() + (long)_local.IntervalSeconds * 1000;
                    if (completed != fingerprint) stableSince = nextDue;
                }
                await Task.Delay(tick, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _local.Error(ex);
                await Task.Delay(tick, ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.MessageReceived -= Receive;
        _cts.Cancel();
        try { await _task.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
