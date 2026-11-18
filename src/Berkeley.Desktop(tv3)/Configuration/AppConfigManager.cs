using System.IO;
using System.Text.Json;

namespace Berkeley.Desktop.Configuration;

/// <summary>
/// Quản lý đọc và ghi file cấu hình JSON của ứng dụng.
/// </summary>
public static class AppConfigManager
{
    private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "node_config.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // Fallback to default
        }

        var defaultConfig = new AppConfig();
        Save(defaultConfig);
        return defaultConfig;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Ignore
        }
    }
}
