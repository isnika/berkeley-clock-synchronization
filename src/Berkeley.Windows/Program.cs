using Avalonia;
using Berkeley.Client;
using Berkeley.Client.Clocks;

namespace Berkeley.Windows;

internal static class Program
{
    public static string DataDirectory { get; private set; } = ClientPaths.DefaultDataDirectory;
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Berkeley Time Sync supports Windows only.");
        if (args is ["--diagnose"])
        {
            var clock = ClientClock.Create(simulated: false);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(clock.ReadUtcMs()),
                HasSetTimePermission = clock.HasSetTimePermission(),
                DataDirectory,
                Note = "Read-only Windows diagnostic. No clock change was attempted."
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        DataDirectory = ClientPaths.GetDataDirectory(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
