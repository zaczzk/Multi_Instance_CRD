namespace CRDLightweightARM;

public enum ShortcutAction
{
    Session1,
    Session2,
    Session3,
    NextSession,
    ToggleSplit,
    ToggleFullscreen,
    ToggleBar,
}

/// <summary>
/// The single list of in-app shortcuts. All use Ctrl+Alt so they rarely collide with remote apps;
/// they are only captured while this app is the foreground window.
/// The global "show / hide" hotkey (Ctrl+Alt+R) is registered separately because it works from anywhere.
/// </summary>
public static class Shortcuts
{
    public record Entry(uint VirtualKey, uint AltVirtualKey, ShortcutAction Action, string Keys, string Description);

    public static readonly IReadOnlyList<Entry> All = new Entry[]
    {
        new(0x31, 0x61, ShortcutAction.Session1,         "Ctrl + Alt + 1", "Switch to session 1"),
        new(0x32, 0x62, ShortcutAction.Session2,         "Ctrl + Alt + 2", "Switch to session 2"),
        new(0x33, 0x63, ShortcutAction.Session3,         "Ctrl + Alt + 3", "Switch to session 3"),
        new(0x09, 0,    ShortcutAction.NextSession,      "Ctrl + Alt + Tab", "Next session"),
        new(0x43, 0,    ShortcutAction.ToggleSplit,      "Ctrl + Alt + C", "Split view on / off"),
        new(0x46, 0,    ShortcutAction.ToggleFullscreen, "Ctrl + Alt + F", "Fullscreen on / off"),
        new(0x42, 0,    ShortcutAction.ToggleBar,        "Ctrl + Alt + B", "Show / hide the top bar"),
    };

    public const string GlobalShowHideKeys = "Ctrl + Alt + R";

    public static Entry? Find(uint vk) => All.FirstOrDefault(e => e.VirtualKey == vk || (e.AltVirtualKey != 0 && e.AltVirtualKey == vk));

    public static string KeysFor(ShortcutAction action) => All.First(e => e.Action == action).Keys;
}
