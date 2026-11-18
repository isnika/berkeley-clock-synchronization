using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using Berkeley.Contracts;

namespace Berkeley.Desktop.ViewModels;

public partial class LogItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _timeString = string.Empty;

    [ObservableProperty]
    private string _directionBadge = string.Empty;

    [ObservableProperty]
    private Brush _badgeBrush = (Brush)new BrushConverter().ConvertFrom("#64748B")!;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private AuditEventKind _kind;

    [ObservableProperty]
    private string _category = "Khác";

    public static LogItemViewModel FromAuditEvent(AuditEvent evt)
    {
        var (badge, brush, category) = evt.Kind switch
        {
            AuditEventKind.MessageSent => ("➡️ GỬI", (Brush)new BrushConverter().ConvertFrom("#38BDF8")!, "GỬI"),
            AuditEventKind.MessageReceived => ("⬅️ NHẬN", (Brush)new BrushConverter().ConvertFrom("#818CF8")!, "NHẬN"),
            AuditEventKind.ClockAdjusted => ("⚡ STEP", (Brush)new BrushConverter().ConvertFrom("#10B981")!, "STEP"),
            AuditEventKind.ClockAdjustFailed => ("❌ LỖI STEP", (Brush)new BrushConverter().ConvertFrom("#EF4444")!, "LỖI"),
            AuditEventKind.LeaderChanged => ("👑 BULLY", (Brush)new BrushConverter().ConvertFrom("#F59E0B")!, "BULLY"),
            AuditEventKind.RoundStarted => ("⏱️ VÒNG ĐO", (Brush)new BrushConverter().ConvertFrom("#EAB308")!, "VÒNG"),
            AuditEventKind.RoundEnded => ("✅ KẾT QUẢ", (Brush)new BrushConverter().ConvertFrom("#22C55E")!, "VÒNG"),
            AuditEventKind.PermissionError => ("⚠️ QUYỀN", (Brush)new BrushConverter().ConvertFrom("#F43F5E")!, "LỖI"),
            AuditEventKind.PeerConnected => ("🟢 KẾT NỐI", (Brush)new BrushConverter().ConvertFrom("#10B981")!, "MẠNG"),
            AuditEventKind.PeerDisconnected => ("🔴 NGẮT KẾT NỐI", (Brush)new BrushConverter().ConvertFrom("#EF4444")!, "MẠNG"),
            AuditEventKind.SendError => ("❌ LỖI GỬI", (Brush)new BrushConverter().ConvertFrom("#EF4444")!, "LỖI"),
            _ => ("ℹ️ INFO", (Brush)new BrushConverter().ConvertFrom("#64748B")!, "Khác")
        };

        return new LogItemViewModel
        {
            TimeString = evt.OccurredAt.ToLocalTime().ToString("HH:mm:ss.fff"),
            DirectionBadge = badge,
            BadgeBrush = brush,
            Source = evt.SourceNodeId,
            Summary = evt.Summary,
            Kind = evt.Kind,
            Category = category
        };
    }
}
