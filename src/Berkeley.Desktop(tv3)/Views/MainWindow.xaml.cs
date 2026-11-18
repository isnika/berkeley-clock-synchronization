using System.Windows;
using Berkeley.Desktop.ViewModels;

namespace Berkeley.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
