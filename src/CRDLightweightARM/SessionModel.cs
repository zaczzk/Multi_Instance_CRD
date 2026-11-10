using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CRDLightweightARM;

public class SessionItem
{
    public const string DefaultUrl = "https://remotedesktop.google.com/access";

    public int Index { get; set; }

    /// <summary>Name detected from the connected host (updated automatically).</summary>
    public string Name { get; set; } = "";

    /// <summary>Name chosen by the user in Settings. When set, it always wins over <see cref="Name"/>.</summary>
    public string? CustomName { get; set; }

    /// <summary>Start page for this slot. Updated to the last connected host link so the slot reconnects directly.</summary>
    public string Url { get; set; } = DefaultUrl;

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(CustomName) ? CustomName!.Trim()
        : !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : $"Session {Index}";
}

public class AppConfig
{
    public const int SessionCount = 3;

    /// <summary>Stop drawing video for sessions you are not looking at. The connection stays open.</summary>
    public bool PauseBackgroundSessions { get; set; } = true;

    /// <summary>Keep the top bar visible instead of auto-hiding it.</summary>
    public bool PinBar { get; set; } = false;

    public int BarRevealDelayMs { get; set; } = 220;
    public bool StartFullscreen { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool GlobalHotkeyEnabled { get; set; } = true;

    public List<SessionItem> Sessions { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CRDLightweightARM");

    private static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public SessionItem GetSession(int index) => Sessions.First(s => s.Index == index);

    public static AppConfig Load()
    {
        AppConfig? config = null;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null) MigrateLegacySettings(config, json);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Config load failed, using defaults (old file kept as config.json.bad)", ex);
            try { File.Copy(ConfigPath, ConfigPath + ".bad", overwrite: true); } catch { }
        }

        config ??= new AppConfig();
        config.Normalize();
        config.Save();
        return config;
    }

    /// <summary>Guarantees exactly one session per slot 1..SessionCount, sorted, with sane values.</summary>
    private void Normalize()
    {
        var normalized = new List<SessionItem>(SessionCount);
        for (int i = 1; i <= SessionCount; i++)
        {
            var existing = Sessions?.FirstOrDefault(s => s != null && s.Index == i);
            var item = existing ?? new SessionItem { Index = i };
            if (!IsAllowedUrl(item.Url)) item.Url = SessionItem.DefaultUrl;
            if (item.Name == $"Session {i}" || IsGenericPageTitle(item.Name)) item.Name = "";
            normalized.Add(item);
        }
        Sessions = normalized;
        BarRevealDelayMs = Math.Clamp(BarRevealDelayMs, 0, 2000);
    }

    /// <summary>Carries over settings from versions before the 2026 refactor that were renamed.</summary>
    private static void MigrateLegacySettings(AppConfig config, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty(nameof(BarRevealDelayMs), out _) &&
            root.TryGetProperty("NotchDwellMs", out var dwell) && dwell.TryGetInt32(out var ms))
        {
            config.BarRevealDelayMs = ms;
        }
    }

    private static readonly HashSet<string> GenericPageTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Access", "Computers", "Remote Access", "Remote Support", "Chrome Remote Desktop", "Sign in", "Sign in - Google Accounts",
    };

    /// <summary>True for CRD / Google page titles that are not the name of a remote computer.</summary>
    public static bool IsGenericPageTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) || GenericPageTitles.Contains(title.Trim());

    /// <summary>Only Chrome Remote Desktop pages over HTTPS may be loaded into a session.</summary>
    public static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsCrdUri(uri);

    public static bool IsCrdUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("remotedesktop.google.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("remotedesktop.corp.google.com", StringComparison.OrdinalIgnoreCase));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Config save failed", ex);
        }
    }
}

public static class CrashLog
{
    public static void Write(string context, object? detail)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            var path = Path.Combine(AppConfig.DataDirectory, "crash.log");
            if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}:\n{detail}\n\n");
        }
        catch { }
    }

    public static string FilePath => Path.Combine(AppConfig.DataDirectory, "crash.log");
}
