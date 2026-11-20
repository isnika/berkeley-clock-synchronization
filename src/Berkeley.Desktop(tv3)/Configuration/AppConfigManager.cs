using System.IO;
using System.Text.Json;
namespace Berkeley.Desktop.Configuration;
public static class AppConfigManager
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BerkeleyTimeSync");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static AppConfig Load(string? path = null)
    {
        path ??= Path.Combine(DataDirectory, "node_config.json");
        if (!File.Exists(path)) return new AppConfig();
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("Cấu hình rỗng.");
        ConfigValidator.Validate(config);
        return config;
    }
    public static void Save(AppConfig config, string? path = null)
    {
        ConfigValidator.Validate(config);
        path = Path.GetFullPath(path ?? Path.Combine(DataDirectory, "node_config.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(config, Options)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
