using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Berkeley.Windows;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try { desktop.MainWindow = new MainWindow(); }
            catch (Exception ex)
            {
                desktop.MainWindow = new Window
                {
                    Title = "Berkeley Time Sync — lỗi khởi động", Width = 680, Height = 300,
                    Content = new TextBox { Text = $"Không thể khởi động. Cấu hình lỗi được giữ nguyên.\n\n{ex.Message}\n\nDữ liệu: {Program.DataDirectory}",
                        IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(24) }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
