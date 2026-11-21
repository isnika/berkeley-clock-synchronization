using Berkeley.Client.Clocks;
using Berkeley.Contracts;
using Berkeley.Desktop.Configuration;
using Berkeley.Desktop.Persistence;
using Berkeley.Desktop.Services;
using Berkeley.Engine.Runtime;
using Berkeley.Networking;

namespace Berkeley.Client;

/// <summary>Runtime for the primary Windows Avalonia client.</summary>
public sealed class ClientSession : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly Func<bool, ClientClock> _clockFactory;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private ClientClock _clock;
    private NodeApplication? _node;
    private INetworkTransport? _transport;
    private ILeadershipService? _leadership;
    private CancellationTokenSource? _runCts;
    private bool _closed;
    public SqliteAuditSink Audit { get; }
    public bool IsRunning => _node?.IsRunning == true;
    public bool IsElecting => (_leadership as BullyLeadershipService)?.IsElecting == true;
    public bool IsSimulated => _config.UseSimulatedCluster;
    public long UtcMs => _clock.ReadUtcMs();
    public bool HasPermission => _clock.HasSetTimePermission();
    public event Action<RoundSummary>? RoundCompleted;

    public ClientSession(AppConfig config, string dataDirectory, Func<bool, ClientClock>? clockFactory = null)
    {
        ConfigValidator.Validate(config);
        _config = config;
        _clockFactory = clockFactory ?? ClientClock.Create;
        _clock = _clockFactory(config.UseSimulatedCluster);
        Audit = new(Path.Combine(dataDirectory, config.UseSimulatedCluster ? "demo.db" : "network.db"));
    }

    public async Task StartAsync()
    {
        await _operation.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (IsRunning) return;
            _clock = _clockFactory(_config.UseSimulatedCluster);
            _clock.AllowAdjustments = true;
            _runCts = new();
            try
            {
                if (_config.UseSimulatedCluster)
                {
                    _transport = new SimulatedClusterTransport(_clock, Audit, _config.Node);
                    var sim = new SimulatedLeadershipService(_config.Node.NodeId, _clock, Audit);
                    sim.SetLeader(_config.Node.NodeId, 1);
                    _leadership = sim;
                }
                else
                {
                    var tcp = new TcpNetworkTransport(_config.Node, _clock, Audit, autoStart: false);
                    _transport = tcp;
                    await tcp.StartAsync(_runCts.Token);
                    _leadership = new BullyLeadershipService(_config.Node, tcp, _clock, Audit);
                }
                _node = new(_clock, _transport, _leadership, Audit);
                _node.RoundCompleted += OnRoundCompleted;
                await _node.StartAsync(_config.Node, _runCts.Token);
            }
            catch { await StopCoreAsync(); throw; }
        }
        finally { _operation.Release(); }
    }

    private void OnRoundCompleted(RoundSummary summary)
    {
        Audit.RecordRoundSummary(summary);
        RoundCompleted?.Invoke(summary);
    }
    public NodeSnapshot? Snapshot() => _node?.GetSnapshot();

    public async Task<bool> SyncAsync()
    {
        await _operation.WaitAsync();
        try { return _node != null && await _node.TriggerSyncNowAsync(_runCts!.Token); }
        finally { _operation.Release(); }
    }
    public async Task ElectAsync(Action<string> progress)
    {
        await _operation.WaitAsync();
        try
        {
            if (!IsRunning) return;
            if (_leadership is BullyLeadershipService real) await real.StartElectionAsync(_runCts!.Token);
            else if (_leadership is SimulatedLeadershipService sim)
                await sim.TriggerBullyElectionSimulationAsync(progress, _runCts!.Token);
        }
        finally { _operation.Release(); }
    }
    public async Task<AdjustmentResult> DriftAsync(double minutes)
    {
        await _operation.WaitAsync();
        try
        {
            var result = _clock.ApplyStep(_config.Node.NodeId, "MANUAL_DRIFT", minutes * 60_000);
            Audit.Record(new(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(), _config.Node.NodeId,
                result.Status == AdjustmentStatus.Applied ? AuditEventKind.ClockAdjusted : AuditEventKind.ClockAdjustFailed,
                $"Manual drift {minutes:+0.##;-0.##} min: {result.Status}", result));
            return result;
        }
        finally { _operation.Release(); }
    }
    public async Task StopAsync()
    {
        // Block incoming Worker steps immediately, then cancel/wait for active work.
        _clock.AllowAdjustments = false;
        _runCts?.Cancel();
        await _operation.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _operation.Release(); }
    }
    private async Task StopCoreAsync()
    {
        _clock.AllowAdjustments = false;
        if (_node != null)
        {
            _node.RoundCompleted -= OnRoundCompleted;
            await _node.StopAsync();
            _node.Dispose(); _node = null;
        }
        if (_leadership is IAsyncDisposable al) await al.DisposeAsync();
        else if (_leadership is IDisposable dl) dl.Dispose();
        _leadership = null;
        if (_transport is IAsyncDisposable at) await at.DisposeAsync();
        else if (_transport is IDisposable dt) dt.Dispose();
        _transport = null;
        _runCts?.Dispose(); _runCts = null;
        await Audit.FlushAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        try { await StopAsync(); }
        finally { await Audit.DisposeAsync(); }
    }
}
