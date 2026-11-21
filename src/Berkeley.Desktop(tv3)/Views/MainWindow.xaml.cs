using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Berkeley.Desktop.ViewModels;
namespace Berkeley.Desktop.Views;
public partial class MainWindow : Window
{
    private bool _closing;
    public MainWindow()
    {
        InitializeComponent();
        try { DataContext = new MainViewModel(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Không mở được ứng dụng — kiểm tra cấu hình/lưu trữ"); Application.Current.Shutdown(); }
        Closing += OnClosing;
        int validationErrors = 0;
        Validation.AddErrorHandler(this, (_, args) =>
        {
            validationErrors += args.Action == ValidationErrorEventAction.Added ? 1 : -1;
            if (DataContext is MainViewModel vm) vm.HasInputErrors = validationErrors > 0;
        });
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        try { if (DataContext is MainViewModel vm) await vm.CloseAsync(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Lỗi khi đóng ứng dụng"); }
        finally { _closing = true; Close(); }
    }
}
