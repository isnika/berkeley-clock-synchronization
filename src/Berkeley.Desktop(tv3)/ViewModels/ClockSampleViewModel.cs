using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace Berkeley.Desktop.ViewModels;

public partial class ClockSampleViewModel : ObservableObject
{
    [ObservableProperty]
    private string _nodeId = string.Empty;

    [ObservableProperty]
    private string _t1 = string.Empty;

    [ObservableProperty]
    private string _t2 = string.Empty;

    [ObservableProperty]
    private string _t3 = string.Empty;

    [ObservableProperty]
    private string _t4 = string.Empty;

    [ObservableProperty]
    private string _rttText = string.Empty;

    [ObservableProperty]
    private string _offsetText = string.Empty;

    [ObservableProperty]
    private string _adjustmentText = string.Empty;

    [ObservableProperty]
    private bool _isAccepted = true;

    [ObservableProperty]
    private string _status = "Chấp nhận";

    [ObservableProperty]
    private Brush _statusBrush = (Brush)new BrushConverter().ConvertFrom("#10B981")!;

    [ObservableProperty]
    private string _stepResultText = "--";

    [ObservableProperty]
    private Brush _stepResultBrush = (Brush)new BrushConverter().ConvertFrom("#94A3B8")!;

    [ObservableProperty]
    private string _note = string.Empty;
}
