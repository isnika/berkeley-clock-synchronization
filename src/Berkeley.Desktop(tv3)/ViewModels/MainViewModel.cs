using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Berkeley.Contracts;
using Berkeley.Desktop.Configuration;
using Berkeley.Desktop.Persistence;
using Berkeley.Desktop.Services;
using Berkeley.Networking;
using Berkeley.Engine.Clocks;
using Berkeley.Engine.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Berkeley.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _clockTimer;
    private AppConfig _config;
    private readonly SqliteAuditSink _auditSink;
    private ISystemClock _clock;
    private CancellationTokenSource? _runCts;
    private Task? _syncTask;
    private Task? _electionTask;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveConfigAndRestartCommand))]
    private bool _hasInputErrors;
    private bool CanSaveConfig() => !HasInputErrors;
    private bool _closed;
    private bool _running;
    private int _snapshotTicks;
    private bool _stopping;
    private Task? _stopTask;
    private int _generation;

    // Services có thể tái khởi tạo khi đổi chế độ Giả lập / Mạng thật
    private INodeApplication? _nodeApp;
    private INetworkTransport? _transport;
    private ILeadershipService? _leadership;
    private SimulatedLeadershipService? _simLeadership; // Dùng khi giả lập để test crash Bully

    // ── Quản lý Điều Hướng Tabs ─────────────────────────────────────────────
    [ObservableProperty] private int _selectedTabIndex = 0; // 0: Dashboard, 1: Config, 2: History, 3: Theory

    // ── Đồng hồ Windows thật (SetSystemTime) ────────────────────────────────
    [ObservableProperty] private string _localTimeDisplay = "--:--:--.---";
    [ObservableProperty] private string _localUtcDisplay = "--:--:--.--- UTC";
    [ObservableProperty] private string _currentDateDisplay = string.Empty;
    [ObservableProperty] private bool _hasAdminPermission;
    [ObservableProperty] private string _adminPermissionText = "Kiểm tra quyền...";
    [ObservableProperty] private Brush _adminPermissionBrush = (Brush)new BrushConverter().ConvertFrom("#EF4444")!;

    // ── Danh tính & Vai trò & Chế độ mạng ──────────────────────────────────
    [ObservableProperty] private string _nodeId = "Node1";
    [ObservableProperty] private string _displayName = "Trạm Node 1";
    [ObservableProperty] private string _listenEndpoint = "127.0.0.1:5001";
    [ObservableProperty] private bool _isMaster = true;
    [ObservableProperty] private string _roleBadge = "👑 MASTER";
    [ObservableProperty] private Brush _roleBrush = (Brush)new BrushConverter().ConvertFrom("#F59E0B")!;
    [ObservableProperty] private string _currentMasterId = "Node1";
    [ObservableProperty] private long _leaderSessionId = 1;
    [ObservableProperty] private bool _isElecting = false;
    [ObservableProperty] private string _electionStatusMessage = string.Empty;
    [ObservableProperty] private string _networkModeBadge = "🧪 CHẾ ĐỘ GIẢ LẬP";
    [ObservableProperty] private Brush _networkModeBrush = (Brush)new BrushConverter().ConvertFrom("#A855F7")!;
    [ObservableProperty] private string _connectedNodesSummary = "0/0 Máy kết nối";
    [ObservableProperty] private string _syncButtonTooltip = "Kích hoạt vòng đồng bộ Berkeley ngay lập tức";

    // ── Trạng thái Vòng Berkeley ──────────────────────────────────────────
    [ObservableProperty] private string _activeRoundId = "--";
    [ObservableProperty] private string _roundPhaseText = "IDLE (Sẵn sàng)";
    [ObservableProperty] private int _phaseStepIndex = 0; // 0..4
    [ObservableProperty] private bool _isSyncInProgress;
    [ObservableProperty] private string _averageOffsetText = "-- ms";
    [ObservableProperty] private string _preSyncSpreadText = "-- ms";
    [ObservableProperty] private string _postSyncSpreadText = "-- ms";
    [ObservableProperty] private string _lastRoundOutcomeText = "Chưa có vòng chạy";
    [ObservableProperty] private Brush _outcomeBrush = (Brush)new BrushConverter().ConvertFrom("#94A3B8")!;

    // ── Vùng Kiểm Chứng Đồ Án (Verification Verdict) ────────────────────────
    [ObservableProperty] private string _verificationVerdictText = "Chờ đo kiểm chứng...";
    [ObservableProperty] private Brush _verificationVerdictBrush = (Brush)new BrushConverter().ConvertFrom("#94A3B8")!;
    [ObservableProperty] private string _acceptanceThresholdText = "Ngưỡng sai số: < 60.0s";

    // ── Notification Banner kết quả vòng ───────────────────────────────────
    [ObservableProperty] private bool _isResultBannerVisible = false;
    [ObservableProperty] private string _resultBannerTitle = string.Empty;
    [ObservableProperty] private string _resultBannerSummary = string.Empty;
    [ObservableProperty] private Brush _resultBannerBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!;
    private DispatcherTimer? _bannerDismissTimer;

    // ── Bộ sưu tập hiển thị ───────────────────────────────────────────────
    public ObservableCollection<NodeCardViewModel> Peers { get; } = [];
    public ObservableCollection<ClockSampleViewModel> CurrentRoundSamples { get; } = [];
    public ObservableCollection<LogItemViewModel> AuditLogs { get; } = [];
    public ObservableCollection<RoundSummaryRecord> RecentRounds { get; } = [];

    // ── Bộ lọc Log ────────────────────────────────────────────────────────
    [ObservableProperty] private string _selectedLogFilter = "Tất cả";
    [ObservableProperty] private string _logSearchKeyword = string.Empty;
    private readonly List<AuditEvent> _allLoggedEvents = [];
    private readonly object _logLock = new();

    // ── Properties cho Màn hình Cấu Hình (Tab 2) ──────────────────────────
    [ObservableProperty] private string _configNodeId = "Node1";
    [ObservableProperty] private string _configDisplayName = "Trạm Node 1";
    [ObservableProperty] private string _configListenAddress = "0.0.0.0";
    [ObservableProperty] private int _configListenPort = 5001;
    [ObservableProperty] private bool _configUseSimulatedCluster = true;
    [ObservableProperty] private double _configAcceptanceThresholdSeconds = 60.0;
    [ObservableProperty] private int _configHeartbeatIntervalMs = 2000;
    [ObservableProperty] private int _configHeartbeatTimeoutMs = 6000;
    [ObservableProperty] private int _configElectionTimeoutMs = 5000;
    [ObservableProperty] private int _configSampleTimeoutMs = 3000;
    [ObservableProperty] private int _configRoundTimeoutMs = 30000;
    public ObservableCollection<string> AvailableIpAddresses { get; } = [];
    public ObservableCollection<ConfigPeerItem> ConfigPeers { get; } = [];

    public MainViewModel() : this(AppConfigManager.Load(), new SqliteAuditSink()) { }

    public MainViewModel(AppConfig config, SqliteAuditSink auditSink)
    {
        _config = config;
        _auditSink = auditSink;
        _clock = new DesktopClock(_config.UseSimulatedCluster);
        _auditSink.AuditEventLogged += OnAuditEventLogged;
        _auditSink.StorageFailed += message => Application.Current?.Dispatcher.BeginInvoke(new Action(() => { VerificationVerdictText = "Lỗi lưu trữ: " + message; }));

        CheckAdminPermissions();

        // Nạp danh sách IP trên card mạng máy này
        foreach (var ip in AppConfig.GetLocalIPv4Addresses())
        {
            AvailableIpAddresses.Add(ip);
        }

        // Tải cấu hình vào Tab Settings
        LoadConfigFields();

        // Khởi động hệ thống mạng & Node
        if (_config.AutoStartOnLaunch) InitializeNodeApplication();
        else { IsMaster = false; RoleBadge = "ĐÃ DỪNG"; }

        // Đồng hồ tick 50ms cập nhật UI
        _clockTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _clockTimer.Tick += OnClockTimerTick;
        _clockTimer.Start();

        // Nạp lịch sử ban đầu từ SQLite
        _ = RefreshHistoryAsync();
    }

    private void CheckAdminPermissions()
    {
        HasAdminPermission = _clock.HasSetTimePermission();
        if (_config.UseSimulatedCluster) { AdminPermissionText = "Đồng hồ mô phỏng — không chỉnh giờ Windows"; return; }
        if (HasAdminPermission)
        {
            AdminPermissionText = "🛡️ Administrator: Token có quyền chỉnh giờ (kết quả thực tế theo từng Step)";
            AdminPermissionBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!;
        }
        else
        {
            AdminPermissionText = "⚠️ Thiếu quyền Administrator (Hãy chạy Run as Administrator để Step thật)";
            AdminPermissionBrush = (Brush)new BrushConverter().ConvertFrom("#EF4444")!;
        }
    }

    private void LoadConfigFields()
    {
        ConfigNodeId = _config.Node.NodeId;
        ConfigDisplayName = _config.Node.DisplayName;
        ConfigListenAddress = _config.Node.ListenAddress;
        ConfigListenPort = _config.Node.ListenPort;
        ConfigUseSimulatedCluster = _config.UseSimulatedCluster;
        ConfigAcceptanceThresholdSeconds = _config.AcceptanceThresholdSeconds;
        ConfigHeartbeatIntervalMs = _config.Node.OperationParams.HeartbeatIntervalMs;
        ConfigHeartbeatTimeoutMs = _config.Node.OperationParams.HeartbeatTimeoutMs;
        ConfigElectionTimeoutMs = _config.Node.OperationParams.ElectionTimeoutMs;
        ConfigSampleTimeoutMs = _config.Node.OperationParams.SampleTimeoutMs;
        ConfigRoundTimeoutMs = _config.Node.OperationParams.RoundTimeoutMs;

        AcceptanceThresholdText = $"Ngưỡng cấu hình: ≤ {_config.AcceptanceThresholdSeconds:F1}s";

        ConfigPeers.Clear();
        foreach (var p in _config.Node.Peers)
        {
            ConfigPeers.Add(new ConfigPeerItem(p.NodeId, p.Address, p.Port));
        }
    }

    private void InitializeNodeApplication()
    {
        ConfigValidator.Validate(_config);
        _clock = new DesktopClock(_config.UseSimulatedCluster);
        _generation++;
        _runCts = new CancellationTokenSource();
        CheckAdminPermissions();
        NodeId = _config.Node.NodeId;
        DisplayName = _config.Node.DisplayName;
        ListenEndpoint = $"{_config.Node.ListenAddress}:{_config.Node.ListenPort}";

        if (_config.UseSimulatedCluster)
        {
            NetworkModeBadge = "🧪 CHẾ ĐỘ GIẢ LẬP (1 MÁY)";
            NetworkModeBrush = (Brush)new BrushConverter().ConvertFrom("#A855F7")!;

            var simTransport = new SimulatedClusterTransport((IClockReader)_clock, _auditSink, _config.Node);
            _simLeadership = new SimulatedLeadershipService(NodeId, (IClockReader)_clock, _auditSink);

            _simLeadership.SetLeader(NodeId, 1);
            _transport = simTransport;
            _leadership = _simLeadership;
        }
        else
        {
            NetworkModeBadge = "🟢 MẠNG THẬT TCP (6 LAPTOP LAN/Wi-Fi)";
            NetworkModeBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!;

            _transport = new TcpNetworkTransport(_config.Node, (IClockReader)_clock, _auditSink);
            _leadership = new BullyLeadershipService(_config.Node, _transport, (IClockReader)_clock, _auditSink);
            _simLeadership = null;
        }

        _nodeApp = new NodeApplication(_clock, _transport, _leadership, _auditSink);
        _nodeApp.SnapshotChanged += OnSnapshotChanged;
        _nodeApp.RoundCompleted += OnRoundCompleted;
        _running = true;

        // Khởi động Node
        _ = _nodeApp.StartAsync(_config.Node);

        // Khởi tạo thẻ các peers
        RefreshPeerCards();
    }

    private void RefreshPeerCards()
    {
        Peers.Clear();

        // 1. Thêm chính máy này (TÔI)
        var selfCard = new NodeCardViewModel
        {
            NodeId = NodeId,
            DisplayName = DisplayName,
            AddressPort = ListenEndpoint,
            IsSelf = true,
            IsMaster = _leadership?.IAmMaster ?? false
        };
        selfCard.UpdateMetrics(0, 0, 0, _leadership?.IAmMaster ?? false, true);
        Peers.Add(selfCard);

        // 2. Thêm các peers từ cấu hình
        foreach (var peer in _config.Node.Peers)
        {
            var card = new NodeCardViewModel
            {
                NodeId = peer.NodeId,
                DisplayName = $"Trạm {peer.NodeId}",
                AddressPort = $"{peer.Address}:{peer.Port}",
                IsSelf = false,
                IsMaster = false
            };
            card.UpdateMetrics(null, null, null, false, false);
            Peers.Add(card);
        }
    }

    private void OnClockTimerTick(object? sender, EventArgs e)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(_clock.ReadUtcMs());
        var now = utc.ToLocalTime();
        if (_running && _nodeApp != null && ++_snapshotTicks % 10 == 0) OnSnapshotChanged(_nodeApp.GetSnapshot());
        LocalTimeDisplay = now.ToString("HH:mm:ss.fff");
        LocalUtcDisplay = utc.ToString("HH:mm:ss.fff UTC");
        CurrentDateDisplay = now.ToString("dddd, dd/MM/yyyy");
    }

    private void OnSnapshotChanged(NodeSnapshot snapshot)
    {
        var generation = _generation;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_closed || !_running || generation != _generation) return;
            IsMaster = snapshot.Role == NodeRole.Master;
            CurrentMasterId = snapshot.CurrentMasterId ?? "--";
            LeaderSessionId = snapshot.LeaderSessionId ?? 0;
            ActiveRoundId = snapshot.ActiveRoundId ?? "--";

            // Cập nhật tooltip nút đồng bộ
            SyncButtonTooltip = IsMaster
                ? "Kích hoạt vòng đồng bộ Berkeley ngay lập tức (Bạn là Master)"
                : $"Chỉ Master ({CurrentMasterId}) mới có quyền kích hoạt vòng đồng bộ";

            if (snapshot.Role == NodeRole.Master)
            {
                RoleBadge = "👑 MASTER";
                RoleBrush = (Brush)new BrushConverter().ConvertFrom("#F59E0B")!;
            }
            else if (snapshot.Role == NodeRole.Worker)
            {
                RoleBadge = "👷 WORKER";
                RoleBrush = (Brush)new BrushConverter().ConvertFrom("#38BDF8")!;
            }
            else
            {
                RoleBadge = snapshot.Role == NodeRole.Standalone ? "ĐÃ DỪNG" : "⚡ BẦU CỬ";
                RoleBrush = (Brush)new BrushConverter().ConvertFrom("#A855F7")!;
            }

            RoundPhaseText = snapshot.RoundPhase switch
            {
                RoundPhase.Measuring => "1/4: Master gửi TIME_REQUEST, thu 4 mốc (t1..t4)...",
                RoundPhase.Adjusting => "2/4: Tính trung bình Berkeley & gửi lệnh Step hiệu chỉnh...",
                RoundPhase.Verifying => "3/4: Đo lại để kiểm chứng sai số sau khi Step...",
                RoundPhase.Done => "4/4: Hoàn thành vòng đồng bộ!",
                _ => "IDLE (Sẵn sàng kích hoạt)"
            };

            PhaseStepIndex = snapshot.RoundPhase switch
            {
                RoundPhase.Measuring => 1,
                RoundPhase.Adjusting => 2,
                RoundPhase.Verifying => 3,
                RoundPhase.Done => 4,
                _ => 0
            };

            // Cập nhật trạng thái từng máy từ KnownPeers
            int connectedCount = 1; // Tính cả chính tôi
            int totalCount = snapshot.KnownPeers.Count + 1;

            foreach (var peerStatus in snapshot.KnownPeers)
            {
                var card = Peers.FirstOrDefault(p => p.NodeId == peerStatus.NodeId);
                if (card != null)
                {
                    card.UpdateFromPeerStatus(peerStatus, CurrentMasterId, NodeId);
                    if (peerStatus.ConnectionState == PeerConnectionState.Connected)
                    {
                        connectedCount++;
                    }
                }
            }

            // Cập nhật lại chính máy tôi
            var selfCard = Peers.FirstOrDefault(p => p.IsSelf);
            if (selfCard != null)
            {
                selfCard.UpdateMetrics(0, 0, null, IsMaster, true);
            }

            ConnectedNodesSummary = $"{connectedCount}/{totalCount} Máy kết nối";
        });
    }

    private void OnRoundCompleted(RoundSummary summary)
    {
        _auditSink.RecordRoundSummary(summary);

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_closed) return;
            IsSyncInProgress = false;
            ActiveRoundId = summary.RoundId;
            AverageOffsetText = $"{summary.AverageOffsetMs:+0.00;-0.00} ms ({summary.AverageOffsetMs / 1000.0:+0.00;-0.00}s)";

            // Sai số đo lại thực tế (RemeasuredSpreadMs)
            double remeasuredSpreadSec = (summary.RemeasuredSpreadMs ?? double.NaN) / 1000.0;
            PostSyncSpreadText = Verification.HasMeasurement(summary.RemeasuredSpreadMs)
                ? $"{summary.RemeasuredSpreadMs.GetValueOrDefault():F1} ms ({remeasuredSpreadSec:F3}s)"
                : "Chưa có số đo hợp lệ";

            LastRoundOutcomeText = summary.Outcome switch
            {
                RoundOutcome.Success => "✅ ĐỒNG BỘ THÀNH CÔNG RỰC RỠ",
                RoundOutcome.PartialSuccess => "⚠️ HOÀN THÀNH MỘT PHẦN (Một số node lỗi)",
                _ => "❌ VÒNG BỊ HỦY HOẶC THẤT BẠI"
            };

            OutcomeBrush = (Brush)new BrushConverter().ConvertFrom(
                summary.Outcome == RoundOutcome.Success ? "#10B981" : "#EF4444")!;

            // Đánh giá tiêu chí nghiệm thu (Verification Verdict)
            double thresholdSec = _config.AcceptanceThresholdSeconds;
            bool isAcceptable = Verification.MeetsThreshold(summary, thresholdSec);

            if (isAcceptable)
            {
                VerificationVerdictText = $"✅ ĐẠT NGƯỠNG TRÊN SỐ ĐO NHẬN ĐƯỢC (Sai số {remeasuredSpreadSec:F3}s ≤ {thresholdSec:F1}s)";
                VerificationVerdictBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!;
            }
            else
            {
                VerificationVerdictText = $"CHƯA XÁC NHẬN ĐẠT (thiếu số đo, lỗi vòng hoặc vượt ngưỡng {thresholdSec:F1}s)";
                VerificationVerdictBrush = (Brush)new BrushConverter().ConvertFrom("#EF4444")!;
            }

            // Cập nhật bảng mẫu đo 4 mốc (bao gồm CẢ mẫu hợp lệ VÀ mẫu bị loại)
            CurrentRoundSamples.Clear();
            double maxOffset = 0, minOffset = 0;

            // 1. Các mẫu hợp lệ
            foreach (var s in summary.AcceptedSamples)
            {
                if (s.OffsetMs > maxOffset) maxOffset = s.OffsetMs;
                if (s.OffsetMs < minOffset) minOffset = s.OffsetMs;

                var adj = summary.AdjustmentResults.FirstOrDefault(a => a.NodeId == s.WorkerNodeId);
                var (stepText, stepBrush) = GetStepResultVisuals(adj?.Status);

                CurrentRoundSamples.Add(new ClockSampleViewModel
                {
                    NodeId = s.WorkerNodeId,
                    T1 = DateTimeOffset.FromUnixTimeMilliseconds(s.T1MasterSendUtcMs).ToString("HH:mm:ss.fff"),
                    T2 = DateTimeOffset.FromUnixTimeMilliseconds(s.T2WorkerRecvUtcMs).ToString("HH:mm:ss.fff"),
                    T3 = DateTimeOffset.FromUnixTimeMilliseconds(s.T3WorkerSendUtcMs).ToString("HH:mm:ss.fff"),
                    T4 = DateTimeOffset.FromUnixTimeMilliseconds(s.T4MasterRecvUtcMs).ToString("HH:mm:ss.fff"),
                    RttText = $"{s.RttMs:F1} ms",
                    OffsetText = $"{s.OffsetMs / 1000.0:+0.00;-0.00} s",
                    AdjustmentText = adj != null ? $"{adj.RequestedAdjustmentMs / 1000.0:+0.00;-0.00} s" : "--",
                    IsAccepted = true,
                    Status = "Hợp lệ",
                    StatusBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!,
                    StepResultText = stepText,
                    StepResultBrush = stepBrush,
                    Note = "Đã tính vào trung bình d_avg"
                });
            }

            // 2. Các mẫu bị loại (Rejected Samples) - Yêu cầu quan trọng của thầy
            foreach (var r in summary.RejectedSamples)
            {
                CurrentRoundSamples.Add(new ClockSampleViewModel
                {
                    NodeId = r.WorkerNodeId,
                    T1 = r.T1MasterSendUtcMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.T1MasterSendUtcMs).ToString("HH:mm:ss.fff") : "--",
                    T2 = r.T2WorkerRecvUtcMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.T2WorkerRecvUtcMs).ToString("HH:mm:ss.fff") : "--",
                    T3 = r.T3WorkerSendUtcMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.T3WorkerSendUtcMs).ToString("HH:mm:ss.fff") : "--",
                    T4 = r.T4MasterRecvUtcMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(r.T4MasterRecvUtcMs).ToString("HH:mm:ss.fff") : "--",
                    RttText = $"{r.RttMs:F1} ms",
                    OffsetText = $"{r.OffsetMs / 1000.0:+0.00;-0.00} s",
                    AdjustmentText = "BỎ QUA",
                    IsAccepted = false,
                    Status = $"BỊ LOẠI ({r.Status})",
                    StatusBrush = (Brush)new BrushConverter().ConvertFrom("#EF4444")!,
                    StepResultText = "Không chỉnh",
                    StepResultBrush = (Brush)new BrushConverter().ConvertFrom("#64748B")!,
                    Note = r.RejectionReason ?? "Mẫu không thỏa điều kiện"
                });
            }

            PreSyncSpreadText = $"{Math.Abs(maxOffset - minOffset) / 1000.0:F2} s ({Math.Abs(maxOffset - minOffset):F0} ms)";

            // Cập nhật thẻ các nốt
            foreach (var card in Peers)
            {
                var s = summary.AcceptedSamples.FirstOrDefault(x => x.WorkerNodeId == card.NodeId);
                var a = summary.AdjustmentResults.FirstOrDefault(x => x.NodeId == card.NodeId);
                if (s != null && a != null)
                {
                    card.UpdateMetrics(s.RttMs, s.OffsetMs, a.RequestedAdjustmentMs, card.NodeId == CurrentMasterId, card.IsSelf);
                    card.SetAdjustmentResult(a.Status, a.RequestedAdjustmentMs);
                }
            }

            // Hiển thị Notification Banner nổi bật
            ShowNotificationBanner(
                title: $"KẾT QUẢ VÒNG {summary.RoundId}: {LastRoundOutcomeText}",
                summary: $"Kết quả số đo: Sai số trước {PreSyncSpreadText} ➔ sau {PostSyncSpreadText} | Lệch trung bình: {AverageOffsetText}",
                isSuccess: summary.Outcome == RoundOutcome.Success
            );

            // Nạp lại danh sách lịch sử
            _ = RefreshHistoryAsync();
        });
    }

    private static (string Text, Brush Brush) GetStepResultVisuals(AdjustmentStatus? status)
    {
        return status switch
        {
            AdjustmentStatus.Applied => ("✅ ĐÃ ÁP DỤNG", (Brush)new BrushConverter().ConvertFrom("#10B981")!),
            AdjustmentStatus.Failed => ("❌ LỖI API", (Brush)new BrushConverter().ConvertFrom("#EF4444")!),
            AdjustmentStatus.PermissionDenied => ("🔒 THIẾU QUYỀN", (Brush)new BrushConverter().ConvertFrom("#F43F5E")!),
            AdjustmentStatus.Rejected => ("⏭️ BỎ QUA", (Brush)new BrushConverter().ConvertFrom("#64748B")!),
            _ => ("--", (Brush)new BrushConverter().ConvertFrom("#94A3B8")!)
        };
    }

    private void ShowNotificationBanner(string title, string summary, bool isSuccess)
    {
        ResultBannerTitle = title;
        ResultBannerSummary = summary;
        ResultBannerBrush = (Brush)new BrushConverter().ConvertFrom(isSuccess ? "#065F46" : "#7F1D1D")!;
        IsResultBannerVisible = true;

        _bannerDismissTimer?.Stop();
        _bannerDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _bannerDismissTimer.Tick += (s, e) =>
        {
            IsResultBannerVisible = false;
            _bannerDismissTimer.Stop();
        };
        _bannerDismissTimer.Start();
    }

    [RelayCommand]
    private void CloseResultBanner()
    {
        IsResultBannerVisible = false;
        _bannerDismissTimer?.Stop();
    }

    private void OnAuditEventLogged(AuditEvent evt)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_closed) return;
            lock (_logLock)
            {
                _allLoggedEvents.Add(evt);
                if (_allLoggedEvents.Count > 1000)
                    _allLoggedEvents.RemoveAt(0);
            }

            var item = LogItemViewModel.FromAuditEvent(evt);

            // Áp dụng bộ lọc hiện tại
            if (MatchesFilter(item))
            {
                AuditLogs.Insert(0, item);
                if (AuditLogs.Count > 300)
                    AuditLogs.RemoveAt(AuditLogs.Count - 1);
            }
        });
    }

    private bool MatchesFilter(LogItemViewModel item)
    {
        if (SelectedLogFilter != "Tất cả" && item.Category != SelectedLogFilter)
            return false;

        if (!string.IsNullOrWhiteSpace(LogSearchKeyword) &&
            !item.Summary.Contains(LogSearchKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void ReapplyLogFilter()
    {
        AuditLogs.Clear();
        lock (_logLock)
        {
            var filtered = _allLoggedEvents
                .Select(LogItemViewModel.FromAuditEvent)
                .Where(MatchesFilter)
                .TakeLast(300)
                .Reverse();

            foreach (var item in filtered)
            {
                AuditLogs.Add(item);
            }
        }
    }

    // ── CÁC LỆNH ĐIỀU KHIỂN (COMMANDS) ───────────────────────────────────

    [RelayCommand]
    private void SelectTab(string tabIndexStr)
    {
        if (int.TryParse(tabIndexStr, out int idx))
        {
            SelectedTabIndex = idx;
            if (idx == 2)
            {
                _ = RefreshHistoryAsync();
            }
        }
    }

    [RelayCommand]
    private async Task TriggerSyncNowAsync()
    {
        if (IsSyncInProgress || !_running) return;
        if (!_leadership?.IAmMaster ?? false)
        {
            MessageBox.Show($"Chỉ máy Master ({CurrentMasterId}) mới có quyền kích hoạt vòng đồng bộ Berkeley!",
                "Thông Báo Quyền Điều Phối", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsSyncInProgress = true;
        _auditSink.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
            NodeId, AuditEventKind.RoundStarted,
            $"[NGƯỜI DÙNG KÍCH HOẠT] Khởi động vòng đồng bộ Berkeley mới..."));

        if (_nodeApp != null)
        {
            var task = _nodeApp.TriggerSyncNowAsync(_runCts?.Token ?? default);
            _syncTask = task;
            bool ok;
            try { ok = await task; }
            catch (Exception ex) { ok = false; VerificationVerdictText = "Vòng đồng bộ lỗi: " + ex.Message; }
            finally { IsSyncInProgress = false; _syncTask = null; }
            if (!ok)
            {
                IsSyncInProgress = false;
            }
        }
    }

    [RelayCommand]
    private Task SimulateMasterCrashAsync() => _electionTask = RunElectionAsync();

    private async Task RunElectionAsync()
    {
        if (IsElecting || !_running || IsSyncInProgress) return;
        IsElecting = true;
        try
        {

        if (_simLeadership != null)
        {
            // Trong chế độ giả lập
            await _simLeadership.TriggerBullyElectionSimulationAsync(step =>
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    ElectionStatusMessage = step;
                });
            }, _runCts?.Token ?? default);
        }
        else if (_leadership is BullyLeadershipService realBully)
        {
            // Trong chế độ mạng thật TCP
            ElectionStatusMessage = $"[CẢNH BÁO] Giả lập sự cố Master {CurrentMasterId} -> Bắt đầu thuật toán Bully...";
            await realBully.StartElectionAsync(_runCts?.Token ?? default);
        }

        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ElectionStatusMessage = "Bầu chọn lỗi: " + ex.Message; }
        finally { IsElecting = false; }
    }

    [RelayCommand]
    private void QuickDrift(string minutesStr)
    {
        if (!_running || IsSyncInProgress || !double.TryParse(minutesStr, out double minutes)) return;
        double offsetMs = minutes * 60 * 1000;

        var res = _clock.ApplyStep(NodeId, "MANUAL_DRIFT", offsetMs);
        if (res.Status == AdjustmentStatus.Applied)
        {
            _auditSink.Record(new AuditEvent(0, DateTimeOffset.UtcNow, _clock.ReadMonotonicMs(),
                NodeId, AuditEventKind.ClockAdjusted,
                $"[DEMO CHỈNH LỆCH] Đã cố tình chỉnh giờ Windows {minutes:+0;-0} phút"));

            MessageBox.Show($"Đã chỉnh đồng hồ đang chọn lệch {minutes:+0;-0} phút thành công!\nChế độ demo chỉ thay đổi đồng hồ mô phỏng.",
                "Chỉnh đồng hồ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Không thể chỉnh giờ Windows: {res.ErrorMessage}\n(Hãy đảm bảo bạn đã mở ứng dụng với quyền Run as Administrator)",
                "Lỗi Quyền Windows", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void SetLogFilter(string filter)
    {
        SelectedLogFilter = filter;
        ReapplyLogFilter();
    }

    partial void OnLogSearchKeywordChanged(string value)
    {
        ReapplyLogFilter();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        AuditLogs.Clear();
        lock (_logLock)
        {
            _allLoggedEvents.Clear();
        }
    }

    [RelayCommand]
    private async Task ExportAuditLogAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV File (*.csv)|*.csv",
            FileName = $"berkeley_audit_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            try { await _auditSink.ExportAuditToCsvAsync(dialog.FileName); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Không xuất được nhật ký"); return; }
            MessageBox.Show($"Đã xuất toàn bộ nhật ký gói tin và audit ra file:\n{dialog.FileName}",
                "Xuất File CSV Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ── CÁC LỆNH TAB CẤU HÌNH (SETTINGS) ─────────────────────────────────

    [RelayCommand]
    private void AddConfigPeer()
    {
        int nextId = ConfigPeers.Count + 1;
        ConfigPeers.Add(new ConfigPeerItem($"Node{nextId}", "192.168.1.100", 5000 + nextId));
    }

    [RelayCommand]
    private void RemoveConfigPeer(ConfigPeerItem peer)
    {
        ConfigPeers.Remove(peer);
    }

    [RelayCommand]
    private void Reset6LaptopConfig()
    {
        var result = MessageBox.Show(
            "Bạn có chắc muốn nạp cấu hình mẫu cho mạng 6 máy laptop Wi-Fi/LAN (192.168.1.101 -> 192.168.1.106)?",
            "Xác Nhận Nạp Cấu Hình Mẫu", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            var sampleConfig = AppConfig.Create6LaptopNetworkConfig(ConfigNodeId, ConfigListenAddress);
            ConfigPeers.Clear();
            foreach (var p in sampleConfig.Peers)
            {
                ConfigPeers.Add(new ConfigPeerItem(p.NodeId, p.Address, p.Port));
            }
            ConfigUseSimulatedCluster = false; // Chuyển sang mạng thật
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveConfig))]
    private async Task SaveConfigAndRestartAsync()
    {
        try
        {
            if (_stopping || IsSyncInProgress || IsElecting) throw new InvalidOperationException("Chờ thao tác hiện tại hoàn tất trước khi thay cấu hình.");
            // Cập nhật đối tượng cấu hình
            var peersList = ConfigPeers.Select(p => new PeerConfig(p.NodeId, p.Address, p.Port)).ToList();

            var newConfig = new AppConfig
            {
                Node = new NodeConfig(
                    NodeId: ConfigNodeId,
                    DisplayName: ConfigDisplayName,
                    ListenAddress: ConfigListenAddress,
                    ListenPort: ConfigListenPort,
                    Peers: peersList,
                    OperationParams: new NodeOperationParams(
                        HeartbeatIntervalMs: ConfigHeartbeatIntervalMs,
                        HeartbeatTimeoutMs: ConfigHeartbeatTimeoutMs,
                        ElectionTimeoutMs: ConfigElectionTimeoutMs,
                        SampleTimeoutMs: ConfigSampleTimeoutMs,
                        SampleMaxAgeMs: 10000,
                        RoundTimeoutMs: ConfigRoundTimeoutMs
                    )
                ),
                UseSimulatedCluster = ConfigUseSimulatedCluster,
                AcceptanceThresholdSeconds = ConfigAcceptanceThresholdSeconds
            };

            newConfig.AutoStartOnLaunch = _config.AutoStartOnLaunch;
            ConfigValidator.Validate(newConfig);
            // Lưu vào file JSON
            AppConfigManager.Save(newConfig);
            _config = newConfig;
            AcceptanceThresholdText = $"Ngưỡng cấu hình: ≤ {newConfig.AcceptanceThresholdSeconds:F1}s";

            await StopNodeAsync();

            // Khởi động lại dịch vụ theo cấu hình mới
            InitializeNodeApplication();

            SelectedTabIndex = 0; // Quay về Dashboard

            MessageBox.Show("Đã lưu cấu hình và khởi động lại Node thành công!",
                "Cập Nhật Cấu Hình", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi lưu cấu hình: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── CÁC LỆNH TAB LỊCH SỬ (HISTORY) ───────────────────────────────────

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        try
        {
            await _auditSink.FlushAsync();
            var rounds = await _auditSink.GetRecentRoundsAsync(50);
            RecentRounds.Clear();
            foreach (var r in rounds)
            {
                RecentRounds.Add(r);
            }
        }
        catch (Exception ex) { VerificationVerdictText = "Lỗi đọc lịch sử: " + ex.Message; }
    }

    [RelayCommand]
    private async Task ExportHistoryCsvAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV File (*.csv)|*.csv",
            FileName = $"berkeley_history_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                await _auditSink.ExportRoundsToCsvAsync(dialog.FileName);

                MessageBox.Show($"Đã xuất lịch sử ra file: {dialog.FileName}",
                    "Xuất File Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi xuất file: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
    [RelayCommand]
    private void StartNode()
    {
        if (_running || _closed || _stopping) return;
        try { InitializeNodeApplication(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Không khởi động được Node"); }
    }

    [RelayCommand]
    private Task StopNodeAsync() => _stopTask is { IsCompleted: false } ? _stopTask : (_stopTask = StopCoreAsync());

    private async Task StopCoreAsync()
    {
        _stopping = true;
        try
        {
        _generation++;
        _running = false;
        if (_clock is DesktopClock clock) clock.AllowAdjustments = false;
        _runCts?.Cancel();
        if (_syncTask != null) { try { await _syncTask; } catch (Exception ex) { VerificationVerdictText = "Vòng đã dừng: " + ex.Message; } }
        if (_electionTask != null) await _electionTask;
        if (_nodeApp != null)
        {
            _nodeApp.SnapshotChanged -= OnSnapshotChanged;
            _nodeApp.RoundCompleted -= OnRoundCompleted;
            await _nodeApp.StopAsync();
            _nodeApp = null;
        }
        if (_leadership is IAsyncDisposable al) await al.DisposeAsync();
        else if (_leadership is IDisposable dl) dl.Dispose();
        if (_transport is IAsyncDisposable at) await at.DisposeAsync();
        else if (_transport is IDisposable dt) dt.Dispose();
        _leadership = null; _transport = null; _simLeadership = null;
        _runCts?.Dispose(); _runCts = null;
        IsMaster = false; IsSyncInProgress = false; RoleBadge = "ĐÃ DỪNG";
        }
        finally { _stopping = false; }
    }

    public async Task CloseAsync()
    {
        _closed = true;
        _clockTimer.Stop(); _bannerDismissTimer?.Stop();
        try { await StopNodeAsync(); }
        finally { _auditSink.AuditEventLogged -= OnAuditEventLogged; await _auditSink.DisposeAsync(); }
    }

}
