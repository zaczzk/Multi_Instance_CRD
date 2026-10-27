using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRDLightweightARM;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WarmMode
{
    UltraWarm,  // Instant wake <35ms, 0% GPU decode, normal memory (Recommended)
    EcoWarm,    // Low-RAM standby ~200ms wake, 0% GPU decode, low memory target
    LiveHot     // Continuous 60fps decode in background, 0ms wake
}

public class SessionItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "Session";
    public string Url { get; set; } = "https://remotedesktop.google.com/access";
    public bool AutoConnect { get; set; } = true;
}

public class AppConfig
{
    public WarmMode EfficiencyMode { get; set; } = WarmMode.UltraWarm;
    public int NotchDwellMs { get; set; } = 220;
    public bool EnforceNativeDpi { get; set; } = true;
    public bool StartFullscreen { get; set; } = true;

    public List<SessionItem> Sessions { get; set; } = new()
    {
        new SessionItem { Index = 1, Name = "Session 1", Url = "https://remotedesktop.google.com/access" },
        new SessionItem { Index = 2, Name = "Session 2", Url = "https://remotedesktop.google.com/access" },
        new SessionItem { Index = 3, Name = "Session 3", Url = "https://remotedesktop.google.com/access" }
    };

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CRDLightweightARM",
        "config.json"
    );

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null && config.Sessions.Count > 0)
                {
                    return config;
                }
            }
        }
        catch
        {
            // Ignore error and fallback to default
        }

        var defaultConfig = new AppConfig();
        defaultConfig.Save();
        return defaultConfig;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }
}
