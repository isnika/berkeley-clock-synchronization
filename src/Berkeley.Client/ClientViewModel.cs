using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Berkeley.Contracts;
using Berkeley.Desktop.Configuration;
using Berkeley.Desktop.Persistence;
using Berkeley.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Berkeley.Client;

public interface IClientDialogs
{
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> SaveCsvAsync(string suggestedName);
}

public partial class PeerEditor : ObservableObject
{
    [ObservableProperty] private string _nodeId = "Node2";
    [ObservableProperty] private string _address = "192.168.1.102";
    [ObservableProperty] private string _port = "5000";
}
public record NodeRow(string NodeId, string Name, string Role, string Connection, string Endpoint,
    string Rtt, string Offset, string Adjustment, string Step, string LastSeen, string Note = "")
{
    public string ConnectionColor => Connection is "ĐÃ KẾT NỐI" or "ONLINE" ? "#6EE7B7" : "#FBBF24";
    public string StepColor => Step == "Applied" ? "#6EE7B7" : Step == "--" ? "#A5B4CB" : "#FCA5A5";
}
public record SampleRow(string NodeId, string T1, string T2, string T3, string T4, string Rtt,
    string Offset, string Adjustment, string Status, string Step, string Note);
public record AuditRow(string Time, string NodeId, string Kind, string Category, string Summary)
{
    public static AuditRow From(AuditEvent e) => new(e.OccurredAt.ToLocalTime().ToString("HH:mm:ss.fff"),
        e.SourceNodeId, e.Kind.ToString(), e.Kind switch
        {
            AuditEventKind.MessageSent => "Gửi", AuditEventKind.MessageReceived => "Nhận",
            AuditEventKind.ClockAdjusted => "Step", AuditEventKind.LeaderChanged => "Bully",
            AuditEventKind.SendError or AuditEventKind.ClockAdjustFailed or AuditEventKind.MessageRejected or AuditEventKind.PermissionError => "Lỗi",
            _ => "Khác"
        }, e.Summary);
}

/// <summary>UI-independent view model; no WPF/Avalonia or native window dependency.</summary>
public partial class ClientViewModel : ObservableObject, IAsyncDisposable
{
    private AppConfig _config;
    private ClientSession _session;
    private readonly IClientDialogs _dialogs;
    private readonly string _dataDirectory, _configPath;
    private readonly ConcurrentQueue<AuditEvent> _incoming = new();
    private readonly ConcurrentQueue<RoundSummary> _rounds = new();
    private readonly List<AuditRow> _allLogs = [];
    private readonly Dictionary<string, AdjustmentResult> _adjustments = [];
    private string? _storageError;
    private bool _disposed;
    private bool _replacingSession;
    private bool _manualElection;
    private Task? _activeOperation;
    private readonly Func<bool, Clocks.ClientClock>? _clockFactory;
    private int _ticks;

    public ClientViewModel(string dataDirectory, IClientDialogs dialogs, Func<bool, Clocks.ClientClock>? clockFactory = null)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _configPath = Path.Combine(_dataDirectory, "node_config.json");
        _dialogs = dialogs;
        _clockFactory = clockFactory;
        _config = AppConfigManager.Load(_configPath); // Invalid files are preserved, never silently reset.
        _session = CreateSession();
        LoadForm();
        DataLocation = _dataDirectory;
    }
    private ClientSession CreateSession()
    {
        var session = new ClientSession(_config, _dataDirectory, _clockFactory);
        session.Audit.AuditEventLogged += e => { if (_incoming.Count < 4096) _incoming.Enqueue(e); };
        session.Audit.StorageFailed += error => _storageError = error;
        session.RoundCompleted += summary => _rounds.Enqueue(summary);
        return session;
    }

    public ObservableCollection<NodeRow> Nodes { get; } = [];
    public ObservableCollection<SampleRow> Samples { get; } = [];
    public ObservableCollection<AuditRow> Logs { get; } = [];
    public ObservableCollection<RoundSummaryRecord> History { get; } = [];
    public ObservableCollection<PeerEditor> ConfigPeers { get; } = [];
    public ObservableCollection<string> AvailableIps { get; } = new(AppConfig.GetLocalIPv4Addresses());
    public string[] LogFilters { get; } = ["Tất cả", "Gửi", "Nhận", "Step", "Bully", "Lỗi", "Khác"];
    [ObservableProperty] private string _localTime = "--:--:--";
    [ObservableProperty] private string _utcTime = "-- UTC";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _identity = "";
    [ObservableProperty] private string _role = "ĐÃ DỪNG";
    [ObservableProperty] private string _master = "--";
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private string _permission = "";
    [ObservableProperty] private string _connected = "";
    [ObservableProperty] private string _phase = "Idle";
    [ObservableProperty] private string _phaseDescription = "Sẵn sàng";
    [ObservableProperty] private int _phaseProgress;
    [ObservableProperty] private bool _isElecting;
    [ObservableProperty] private string _electionStatus = "";
    [ObservableProperty] private string _operatorHint = "";
    [ObservableProperty] private string _threshold = "";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _verdictColor = "#7DD3FC";
    [ObservableProperty] private bool _resultVisible;
    [ObservableProperty] private string _roundId = "--";
    [ObservableProperty] private string _average = "--";
    [ObservableProperty] private string _before = "--";
    [ObservableProperty] private string _after = "--";
    [ObservableProperty] private string _verdict = "Chưa có vòng chạy";
    [ObservableProperty] private string _status = "Sẵn sàng";
    [ObservableProperty] private string _dataLocation = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isMaster;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private int _monitorTab;
    [ObservableProperty] private string _logFilter = "Tất cả";
    [ObservableProperty] private string _logSearch = "";
    [ObservableProperty] private string _driftMinutes = "2";
    [ObservableProperty] private string _configNodeId = "Node1";
    [ObservableProperty] private string _configName = "Trạm Node1";
    [ObservableProperty] private string _configAddress = "0.0.0.0";
    [ObservableProperty] private string _configPort = "5001";
    [ObservableProperty] private bool _configDemo = true;
    [ObservableProperty] private bool _configAutoStart = true;
    [ObservableProperty] private bool _configAutoSync = true;
    [ObservableProperty] private string _configAutoSyncInterval = "30";
    [ObservableProperty] private string _configThreshold = "60";
    [ObservableProperty] private string _configHeartbeat = "2000";
    [ObservableProperty] private string _configHeartbeatTimeout = "6000";
    [ObservableProperty] private string _configElectionTimeout = "5000";
    [ObservableProperty] private string _configSampleTimeout = "3000";
    [ObservableProperty] private string _configMaxAge = "10000";
    [ObservableProperty] private string _configRoundTimeout = "30000";

    public async Task InitializeAsync()
    {
        if (_config.AutoStartOnLaunch) await StartAsync();
        if (_disposed) return;
        await RefreshHistoryAsync();
        Tick();
    }
    public void Tick()
    {
        if (_disposed) return;
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(_session.UtcMs);
        LocalTime = utc.ToLocalTime().ToString("HH:mm:ss.fff");
        UtcTime = utc.ToString("HH:mm:ss.fff 'UTC'");
        Date = utc.ToLocalTime().ToString("dddd, dd/MM/yyyy");
        IsRunning = _session.IsRunning;
        if (++_ticks % 4 == 1)
        {
            var snapshot = _session.Snapshot();
            IsMaster = IsRunning && snapshot?.Role == NodeRole.Master;
            Role = IsRunning ? snapshot?.Role.ToString() ?? "--" : "ĐÃ DỪNG";
            Master = $"{snapshot?.CurrentMasterId ?? "--"} / phiên {snapshot?.LeaderSessionId?.ToString() ?? "--"}";
            Phase = snapshot?.RoundPhase.ToString() ?? "Idle";
            PhaseDescription = snapshot?.RoundPhase switch
            {
                RoundPhase.Measuring => "1/3 · Đang đo t1–t4",
                RoundPhase.Adjusting => "2/3 · Đang chỉnh giờ",
                RoundPhase.Verifying => "3/3 · Đang đo lại",
                _ => IsRunning ? "Sẵn sàng" : "Node đã dừng"
            };
            PhaseProgress = snapshot?.RoundPhase switch
            {
                RoundPhase.Measuring => 1, RoundPhase.Adjusting => 2, RoundPhase.Verifying => 3, _ => 0
            };
            if (snapshot?.ActiveRoundId is { } activeId) RoundId = activeId;
            IsElecting = _manualElection || _session.IsElecting || snapshot?.Role == NodeRole.Electing;
            Threshold = $"Ngưỡng spread cấu hình: ≤ {_config.AcceptanceThresholdSeconds:F3} giây";
            Database = Path.Combine(_dataDirectory, _config.UseSimulatedCluster ? "demo.db" : "network.db");
            Identity = $"{_config.Node.DisplayName} · {_config.Node.NodeId} · {_config.Node.ListenAddress}:{_config.Node.ListenPort}";
            Mode = _config.UseSimulatedCluster ? "DEMO · không đổi giờ hệ điều hành" : "TCP THẬT · đồng bộ giữa các máy Windows";
            Permission = _session.IsSimulated ? "Đồng hồ mô phỏng độc lập" : _session.HasPermission
                ? "Có quyền chỉnh giờ · xem kết quả từng Step"
                : "Thiếu quyền chỉnh giờ · chạy với quyền Administrator";
            var peers = snapshot?.KnownPeers ?? [];
            int online = (IsRunning ? 1 : 0) + peers.Count(p => p.ConnectionState == PeerConnectionState.Connected);
            OperatorHint = !IsRunning ? "Nhấn Khởi động để kết nối cụm máy."
                : IsElecting ? "Đang bầu chọn Master; chờ các máy thống nhất Master và phiên."
                : IsBusy || _session.IsSyncing ? "Đang xử lý thao tác; chờ hoàn tất hoặc nhấn Dừng Node để hủy vòng đang chạy."
                : !IsMaster ? "Máy này là Worker, tự nhận lệnh từ Master qua AutoSync hoặc nút ĐỒNG BỘ NGAY trên Master."
                : online < _config.Node.Peers.Count + 1 ? "Còn máy chưa kết nối. Kiểm tra IP/cổng và firewall trước khi xác nhận đồng bộ toàn cụm."
                : _config.AutoSyncEnabled && !_session.IsSimulated ? $"AutoSync mỗi {_config.AutoSyncIntervalSeconds} giây và sau tái gia nhập khi thống nhất Master/phiên. Có thể Đồng bộ ngay."
                : "AutoSync đang tắt hoặc đang Demo. Nhấn ĐỒNG BỘ NGAY; bật AutoSync trong cấu hình để chạy TCP tự động.";
            Connected = $"{(IsRunning ? 1 : 0) + peers.Count(p => p.ConnectionState == PeerConnectionState.Connected)}/{_config.Node.Peers.Count + 1} máy online";
            var nodes = new List<NodeRow>();
            _adjustments.TryGetValue(_config.Node.NodeId, out var ownStep);
            nodes.Add(new(_config.Node.NodeId, _config.Node.DisplayName, IsMaster ? "TÔI · MASTER" : "TÔI",
                IsRunning ? "ONLINE" : "STOPPED", $"{_config.Node.ListenAddress}:{_config.Node.ListenPort}",
                "--", IsMaster ? "0 ms (mốc)" : "--", Ms(ownStep?.RequestedAdjustmentMs), ownStep?.Status.ToString() ?? "--", "Máy này", ownStep?.ErrorMessage ?? ""));
            foreach (var p in _config.Node.Peers)
            {
                var peer = peers.FirstOrDefault(x => x.NodeId == p.NodeId);
                _adjustments.TryGetValue(p.NodeId, out var step);
                nodes.Add(new(p.NodeId, peer?.DisplayName ?? p.NodeId, p.NodeId == snapshot?.CurrentMasterId ? "MASTER" : "WORKER",
                    peer?.ConnectionState == PeerConnectionState.Connected ? "ĐÃ KẾT NỐI" : "CHƯA KẾT NỐI", $"{p.Address}:{p.Port}", Ms(peer?.LastRttMs),
                    Ms(peer?.LastOffsetMs), Ms(step?.RequestedAdjustmentMs ?? peer?.LastAdjustmentMs), step?.Status.ToString() ?? "--", peer?.LastSeenAt?.ToLocalTime().ToString("HH:mm:ss") ?? "--", step?.ErrorMessage ?? ""));
            }
            // Preserve the grid's selection, scroll and sort state between timer ticks.
            for (int i = 0; i < nodes.Count; i++)
            {
                if (i >= Nodes.Count) Nodes.Add(nodes[i]);
                else if (Nodes[i] != nodes[i]) Nodes[i] = nodes[i];
            }
            while (Nodes.Count > nodes.Count) Nodes.RemoveAt(Nodes.Count - 1);
        }
        bool logsChanged = false;
        for (int i = 0; i < 200 && _incoming.TryDequeue(out var evt); i++)
        {
            _allLogs.Add(AuditRow.From(evt)); logsChanged = true;
            if (evt.Detail is AdjustmentResult step) _adjustments[step.NodeId] = step;
            if (evt.Kind == AuditEventKind.LeaderChanged) ElectionStatus = evt.Summary;
        }
        if (_allLogs.Count > 1000) _allLogs.RemoveRange(0, _allLogs.Count - 1000);
        if (logsChanged) FilterLogs();
        while (_rounds.TryDequeue(out var summary)) ShowRound(summary);
        if (_storageError is { } error) { Status = "Lỗi lưu trữ: " + error; _storageError = null; }
        UpdateCommands();
    }
    private static string Ms(double? value) => value is { } v && double.IsFinite(v) ? $"{v:F1} ms" : "--";
    private static string Time(long value) => value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("HH:mm:ss.fff") : "--";
    private void ShowRound(RoundSummary summary)
    {
        RoundId = summary.RoundId;
        Average = Ms(summary.AverageOffsetMs);
        var offsets = summary.AcceptedSamples.Select(s => s.OffsetMs).Append(0).ToList();
        Before = Ms(offsets.Max() - offsets.Min());
        After = Ms(summary.RemeasuredSpreadMs);
        bool pass = Verification.MeetsThreshold(summary, _config.AcceptanceThresholdSeconds);
        VerdictColor = pass ? "#6EE7B7" : "#FBBF24";
        ResultVisible = true;
        Verdict = $"{summary.Outcome} · {(pass ? "ĐẠT NGƯỠNG TRÊN SỐ ĐO NHẬN ĐƯỢC" : "CHƯA XÁC NHẬN ĐẠT NGƯỠNG")} ≤ {_config.AcceptanceThresholdSeconds:F3}s · {summary.AcceptedSamples.Count} mẫu Worker hợp lệ / {summary.AdjustmentResults.Count} kết quả Step";
        Samples.Clear(); _adjustments.Clear();
        foreach (var a in summary.AdjustmentResults) _adjustments[a.NodeId] = a;
        foreach (var s in summary.AcceptedSamples.Concat(summary.RejectedSamples).OrderBy(s => s.WorkerNodeId, StringComparer.Ordinal))
        {
            _adjustments.TryGetValue(s.WorkerNodeId, out var a);
            Samples.Add(new(s.WorkerNodeId, Time(s.T1MasterSendUtcMs), Time(s.T2WorkerRecvUtcMs), Time(s.T3WorkerSendUtcMs), Time(s.T4MasterRecvUtcMs),
                Ms(s.RttMs), Ms(s.OffsetMs), Ms(a?.RequestedAdjustmentMs), s.Status.ToString(), a?.Status.ToString() ?? "--", a?.ErrorMessage ?? s.RejectionReason ?? ""));
        }
        foreach (var a in summary.AdjustmentResults.Where(a => a.NodeId == _config.Node.NodeId))
            Samples.Add(new(a.NodeId + " (Master)", "--", "--", "--", "--", "--", "0 ms", Ms(a.RequestedAdjustmentMs), "Master", a.Status.ToString(), a.ErrorMessage ?? ""));
        History.Insert(0, new(summary.RoundId, summary.MasterNodeId, summary.LeaderSessionId, summary.RoundStartedAt, summary.RoundEndedAt,
            summary.AverageOffsetMs, summary.RemeasuredSpreadMs, summary.Outcome.ToString(), summary.AcceptedSamples.Count, summary.RejectedSamples.Count));
        if (History.Count > 50) History.RemoveAt(History.Count - 1);
    }

    private bool CanStart() => !IsRunning && !IsBusy && !_disposed;
    private bool CanSync() => IsRunning && IsMaster && !IsBusy && !_session.IsSyncing && !_session.IsElecting;
    private bool CanAct() => IsRunning && !IsBusy && !_session.IsSyncing && !_session.IsElecting;
    private bool CanEdit() => !IsBusy && !_session.IsSyncing && !_session.IsElecting && !_disposed;
    private bool CanStop() => IsRunning && !_replacingSession && !_disposed;
    private void UpdateCommands()
    {
        StartCommand.NotifyCanExecuteChanged(); SyncCommand.NotifyCanExecuteChanged();
        ElectCommand.NotifyCanExecuteChanged(); DriftCommand.NotifyCanExecuteChanged(); SaveConfigCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged(); RefreshHistoryCommand.NotifyCanExecuteChanged();
        QuickDriftCommand.NotifyCanExecuteChanged();
        ExportHistoryCommand.NotifyCanExecuteChanged(); ExportAuditCommand.NotifyCanExecuteChanged();
        AddPeerCommand.NotifyCanExecuteChanged(); RemovePeerCommand.NotifyCanExecuteChanged(); LoadLanPresetCommand.NotifyCanExecuteChanged();
    }
    private Task RunAsync(Func<Task> action, string success)
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        return _activeOperation = RunCoreAsync(action, success);
    }
    private async Task RunCoreAsync(Func<Task> action, string success)
    {
        IsBusy = true; UpdateCommands();
        try { await action(); Status = success; }
        catch (OperationCanceledException) { Status = "Đã hủy thao tác"; }
        catch (Exception ex) { Status = "Lỗi: " + ex.Message; }
        finally { IsBusy = false; _ticks = 0; Tick(); }
    }
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync(_session.StartAsync, "Node đã khởi động");
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try { await _session.StopAsync(); Status = "Node đã dừng"; }
        catch (Exception ex) { Status = "Lỗi khi dừng: " + ex.Message; }
        _ticks = 0; Tick();
    }
    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task SyncAsync() => RunAsync(async () =>
    {
        if (!await _session.SyncAsync()) throw new InvalidOperationException("Chỉ Master đang hoạt động mới được đồng bộ.");
    }, "Vòng đã kết thúc · xem kết quả Step và số đo");
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task ElectAsync() => RunAsync(async () =>
    {
        _manualElection = true; _ticks = 0;
        ElectionStatus = "Đang bắt đầu bầu chọn Master...";
        Tick();
        try
        {
            await _session.ElectAsync(message => _incoming.Enqueue(
                new(0, DateTimeOffset.UtcNow, 0, _config.Node.NodeId, AuditEventKind.LeaderChanged, message)));
        }
        finally { _manualElection = false; }
    }, "Đã thực hiện bầu chọn");
    [RelayCommand] private void DismissResult() => ResultVisible = false;
    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task QuickDriftAsync(string minutes)
    {
        DriftMinutes = minutes;
        return DriftAsync();
    }
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DriftAsync()
    {
        if (!double.TryParse(DriftMinutes, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || !double.IsFinite(minutes))
        { Status = "Số phút không hợp lệ; dùng dấu chấm thập phân."; return; }
        if (!_session.IsSimulated && !await _dialogs.ConfirmAsync("Chỉnh giờ hệ thống thật",
            $"Dịch giờ máy này {minutes:+0.##;-0.##} phút? Dùng trên máy thử và phục hồi nguồn giờ chuẩn sau thử.")) return;
        await RunAsync(async () =>
        {
            var result = await _session.DriftAsync(minutes);
            if (result.Status != AdjustmentStatus.Applied) throw new InvalidOperationException($"{result.Status}: {result.ErrorMessage}");
        }, "Đã chỉnh lệch đồng hồ đang chọn");
    }
    private void FilterLogs()
    {
        Logs.Clear();
        foreach (var row in _allLogs.Where(x => (LogFilter == "Tất cả" || x.Category == LogFilter) &&
            (string.IsNullOrWhiteSpace(LogSearch) || (x.Summary + x.NodeId + x.Kind).Contains(LogSearch, StringComparison.OrdinalIgnoreCase))).TakeLast(300).Reverse()) Logs.Add(row);
    }
    partial void OnLogFilterChanged(string value) => FilterLogs();
    partial void OnLogSearchChanged(string value) => FilterLogs();
    [RelayCommand] private void ClearLogView() { _allLogs.Clear(); Logs.Clear(); } // SQLite remains available for export.
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task RefreshHistoryAsync() => RunAsync(async () =>
    {
        await _session.Audit.FlushAsync();
        var records = await _session.Audit.GetRecentRoundsAsync();
        History.Clear(); foreach (var record in records) History.Add(record);
    }, "Đã tải lịch sử");
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task ExportHistoryAsync() => ExportAsync(false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task ExportAuditAsync() => ExportAsync(true);
    private async Task ExportAsync(bool audit)
    {
        var path = await _dialogs.SaveCsvAsync($"berkeley_{(audit ? "audit" : "history")}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        if (path is null) return;
        await RunAsync(() => audit ? _session.Audit.ExportAuditToCsvAsync(path) : _session.Audit.ExportRoundsToCsvAsync(path), "Đã xuất CSV: " + path);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private void AddPeer() => ConfigPeers.Add(new() { NodeId = "Node" + (ConfigPeers.Count + 2) });
    [RelayCommand(CanExecute = nameof(CanEdit))] private void RemovePeer(PeerEditor peer) => ConfigPeers.Remove(peer);
    [RelayCommand(CanExecute = nameof(CanEdit))] private void LoadLanPreset()
    {
        var node = AppConfig.Create6LaptopNetworkConfig(ConfigNodeId, ConfigAddress);
        ConfigPort = "5000"; ConfigDemo = false;
        SetPeers(node.Peers);
        Status = "Địa chỉ mẫu 192.168.1.101–106: sửa theo IP thực tế rồi Lưu.";
    }
    private void SetPeers(IEnumerable<PeerConfig> peers)
    {
        ConfigPeers.Clear();
        foreach (var p in peers) ConfigPeers.Add(new() { NodeId = p.NodeId, Address = p.Address, Port = p.Port.ToString(CultureInfo.InvariantCulture) });
    }
    private void LoadForm()
    {
        var n = _config.Node; var p = n.OperationParams;
        ConfigNodeId = n.NodeId; ConfigName = n.DisplayName; ConfigAddress = n.ListenAddress;
        ConfigPort = n.ListenPort.ToString(CultureInfo.InvariantCulture); ConfigDemo = _config.UseSimulatedCluster;
        ConfigAutoStart = _config.AutoStartOnLaunch; ConfigThreshold = _config.AcceptanceThresholdSeconds.ToString(CultureInfo.InvariantCulture);
        ConfigAutoSync = _config.AutoSyncEnabled; ConfigAutoSyncInterval = _config.AutoSyncIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        ConfigHeartbeat = p.HeartbeatIntervalMs.ToString(); ConfigHeartbeatTimeout = p.HeartbeatTimeoutMs.ToString();
        ConfigElectionTimeout = p.ElectionTimeoutMs.ToString(); ConfigSampleTimeout = p.SampleTimeoutMs.ToString();
        ConfigMaxAge = p.SampleMaxAgeMs.ToString(); ConfigRoundTimeout = p.RoundTimeoutMs.ToString(); SetPeers(n.Peers);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveConfigAsync() => RunAsync(async () =>
    {
        int Number(string text, string name) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw new ArgumentException(name + " phải là số nguyên.");
        if (!double.TryParse(ConfigThreshold, NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold))
            throw new ArgumentException("Ngưỡng phải là số; dùng dấu chấm thập phân.");
        var config = new AppConfig
        {
            Node = new(ConfigNodeId, ConfigName, ConfigAddress, Number(ConfigPort, "Cổng"),
                ConfigPeers.Select(p => new PeerConfig(p.NodeId, p.Address, Number(p.Port, "Cổng peer"))).ToList(),
                new(Number(ConfigHeartbeat, "Heartbeat"), Number(ConfigHeartbeatTimeout, "Heartbeat timeout"),
                    Number(ConfigElectionTimeout, "Election timeout"), Number(ConfigSampleTimeout, "Sample timeout"),
                    Number(ConfigMaxAge, "Sample max age"), Number(ConfigRoundTimeout, "Round timeout"))),
            UseSimulatedCluster = ConfigDemo, AutoStartOnLaunch = ConfigAutoStart, AcceptanceThresholdSeconds = threshold,
            AutoSyncEnabled = ConfigAutoSync, AutoSyncIntervalSeconds = Number(ConfigAutoSyncInterval, "Chu kỳ AutoSync")
        };
        ConfigValidator.Validate(config); // Validate before touching disk or stopping a working node.
        AppConfigManager.Save(config, _configPath);
        _replacingSession = true; UpdateCommands();
        try
        {
            await _session.DisposeAsync();
            if (_disposed) return;
            _config = config; _session = CreateSession();
            _incoming.Clear(); _rounds.Clear(); _allLogs.Clear(); Logs.Clear(); Samples.Clear(); _adjustments.Clear();
            RoundId = "--"; Average = Before = After = "--"; Verdict = "Chưa có vòng chạy trong cấu hình mới";
            ResultVisible = false; VerdictColor = "#7DD3FC"; ElectionStatus = "";
            await _session.StartAsync();
            await _session.Audit.FlushAsync();
            History.Clear(); foreach (var r in await _session.Audit.GetRecentRoundsAsync()) History.Add(r);
            SelectedTab = 0;
        }
        finally { _replacingSession = false; }
    }, "Đã lưu cấu hình và khởi động lại Node");

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _session.StopAsync();
            if (_activeOperation != null) await _activeOperation;
        }
        finally
        {
            await _session.DisposeAsync();
            IsRunning = false; IsMaster = false;
        }
    }
}
