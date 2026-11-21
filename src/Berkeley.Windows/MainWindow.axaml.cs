using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Berkeley.Client;

namespace Berkeley.Windows;

public partial class MainWindow : Window, IClientDialogs
{
    private readonly ClientViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    private bool _closed, _closing;
    public MainWindow() : this(Program.DataDirectory) { }
    public MainWindow(string dataDirectory)
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new ClientViewModel(dataDirectory, this);
        DataContext = _vm;
        _timer.Tick += (_, _) => _vm.Tick();
        Opened += OnOpened;
        Closing += OnClosing;
    }
    private async void OnOpened(object? sender, EventArgs args)
    {
        _timer.Start();
        try { await _vm.InitializeAsync(); }
        catch (Exception ex) { _vm.Status = "Lỗi khởi động: " + ex.Message; }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_closed) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true; _timer.Stop();
        try { await _vm.DisposeAsync(); }
        catch (Exception ex) { await ShowNoticeAsync("Không lưu được toàn bộ dữ liệu", ex.Message); }
        finally { _closed = true; Close(); }
    }
    public async Task<string?> SaveCsvAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất CSV", SuggestedFileName = suggestedName, DefaultExtension = "csv", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        return file?.TryGetLocalPath();
    }
    public Task<bool> ConfirmAsync(string title, string message) => DialogAsync(title, message, true);
    private async Task ShowNoticeAsync(string title, string message) => await DialogAsync(title, message, false);
    private Task<bool> DialogAsync(string title, string message, bool confirmation)
    {
        var dialog = new Window { Title = title, Width = 560, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        if (confirmation)
        {
            var cancel = new Button { Content = "Hủy" }; cancel.Click += (_, _) => dialog.Close(false); buttons.Children.Add(cancel);
        }
        var accept = new Button { Content = confirmation ? "Xác nhận chỉnh giờ" : "Đóng" };
        accept.Click += (_, _) => dialog.Close(true); buttons.Children.Add(accept);
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 22, Children =
        {
            new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons
        }};
        return dialog.ShowDialog<bool>(this);
    }
}
