using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using Berkeley.Contracts;

namespace Berkeley.Desktop.ViewModels;

public partial class NodeCardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _nodeId = string.Empty;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _addressPort = string.Empty;

    [ObservableProperty]
    private bool _isMaster;

    [ObservableProperty]
    private bool _isSelf;

    [ObservableProperty]
    private bool _isConnected = true;

    [ObservableProperty]
    private PeerConnectionState _connectionState = PeerConnectionState.Connected;

    [ObservableProperty]
    private string _connectionStateText = "ONLINE";

    [ObservableProperty]
    private Brush _connectionStateBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!;

    [ObservableProperty]
    private string _lastRttText = "-- ms";

    [ObservableProperty]
    private string _lastOffsetText = "-- s";

    [ObservableProperty]
    private string _lastAdjustmentText = "-- s";

    [ObservableProperty]
    private string _adjustmentStatusText = "Chưa đồng bộ";

    [ObservableProperty]
    private Brush _adjustmentStatusBrush = (Brush)new BrushConverter().ConvertFrom("#94A3B8")!;

    [ObservableProperty]
    private string _lastSeenText = "Vừa xong";

    [ObservableProperty]
    private string _statusText = "Sẵn sàng";

    [ObservableProperty]
    private Brush _statusBrush = (Brush)new BrushConverter().ConvertFrom("#38BDF8")!;

    [ObservableProperty]
    private Brush _badgeBackground = (Brush)new BrushConverter().ConvertFrom("#1E293B")!;

    [ObservableProperty]
    private string _roleBadge = "👷 WORKER";

    /// <summary>
    /// Cập nhật card từ số liệu đo lường vòng đồng bộ
    /// </summary>
    public void UpdateMetrics(double? rttMs, double? offsetMs, double? adjMs, bool isMaster, bool isSelf)
    {
        IsMaster = isMaster;
        IsSelf = isSelf;

        if (rttMs.HasValue)
            LastRttText = $"{rttMs.Value:F1} ms";

        if (offsetMs.HasValue)
        {
            double sec = offsetMs.Value / 1000.0;
            LastOffsetText = $"{sec:+0.00;-0.00} s";
        }

        if (adjMs.HasValue)
        {
            double sec = adjMs.Value / 1000.0;
            LastAdjustmentText = $"{sec:+0.00;-0.00} s";
        }

        UpdateRoleVisuals(isMaster, isSelf);
    }

    /// <summary>
    /// Cập nhật card trực tiếp từ PeerStatus nhận từ NodeSnapshot (trạng thái mạng thật)
    /// </summary>
    public void UpdateFromPeerStatus(PeerStatus peer, string? currentMasterId, string currentNodeId)
    {
        DisplayName = peer.DisplayName;
        ConnectionState = peer.ConnectionState;

        bool isSelf = peer.NodeId == currentNodeId;
        bool isMaster = peer.NodeId == currentMasterId;

        IsConnected = peer.ConnectionState == PeerConnectionState.Connected;

        if (IsConnected)
        {
            ConnectionStateText = "ONLINE";
            ConnectionStateBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!; // Green
        }
        else
        {
            ConnectionStateText = "OFFLINE";
            ConnectionStateBrush = (Brush)new BrushConverter().ConvertFrom("#EF4444")!; // Red
        }

        if (peer.LastRttMs.HasValue)
            LastRttText = $"{peer.LastRttMs.Value:F1} ms";

        if (peer.LastOffsetMs.HasValue)
        {
            double sec = peer.LastOffsetMs.Value / 1000.0;
            LastOffsetText = $"{sec:+0.00;-0.00} s";
        }

        if (peer.LastAdjustmentMs.HasValue)
        {
            double sec = peer.LastAdjustmentMs.Value / 1000.0;
            LastAdjustmentText = $"{sec:+0.00;-0.00} s";
        }

        if (peer.LastSeenAt.HasValue)
        {
            var diff = DateTimeOffset.UtcNow - peer.LastSeenAt.Value;
            LastSeenText = diff.TotalSeconds < 5 ? "Vừa xong" : $"{diff.TotalSeconds:F0}s trước";
        }

        UpdateRoleVisuals(isMaster, isSelf);
    }

    public void SetAdjustmentResult(AdjustmentStatus status, double adjustmentMs)
    {
        double sec = adjustmentMs / 1000.0;
        LastAdjustmentText = $"{sec:+0.00;-0.00} s";

        switch (status)
        {
            case AdjustmentStatus.Applied:
                AdjustmentStatusText = "✅ Đã áp dụng Step";
                AdjustmentStatusBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!;
                break;
            case AdjustmentStatus.Failed:
                AdjustmentStatusText = "❌ Lỗi gọi API SetSystemTime";
                AdjustmentStatusBrush = (Brush)new BrushConverter().ConvertFrom("#EF4444")!;
                break;
            case AdjustmentStatus.PermissionDenied:
                AdjustmentStatusText = "🔒 Thiếu quyền Admin";
                AdjustmentStatusBrush = (Brush)new BrushConverter().ConvertFrom("#F43F5E")!;
                break;
            case AdjustmentStatus.Rejected:
                AdjustmentStatusText = "⏭️ Lệnh quá hạn / Bỏ qua";
                AdjustmentStatusBrush = (Brush)new BrushConverter().ConvertFrom("#64748B")!;
                break;
            default:
                AdjustmentStatusText = "Đang xử lý";
                AdjustmentStatusBrush = (Brush)new BrushConverter().ConvertFrom("#F59E0B")!;
                break;
        }
    }

    private void UpdateRoleVisuals(bool isMaster, bool isSelf)
    {
        IsMaster = isMaster;
        IsSelf = isSelf;

        if (isMaster)
        {
            RoleBadge = isSelf ? "👑 TÔI (MASTER)" : "👑 MASTER";
            StatusBrush = (Brush)new BrushConverter().ConvertFrom("#F59E0B")!; // Gold
            BadgeBackground = (Brush)new BrushConverter().ConvertFrom("#78350F")!;
            StatusText = "Điều phối cụm";
        }
        else
        {
            RoleBadge = isSelf ? "👷 TÔI (WORKER)" : "👷 WORKER";
            StatusBrush = (Brush)new BrushConverter().ConvertFrom("#38BDF8")!; // Cyan
            BadgeBackground = (Brush)new BrushConverter().ConvertFrom("#0C4A6E")!;
            StatusText = IsConnected ? "Đang hoạt động" : "Mất tín hiệu";
        }
    }
}
