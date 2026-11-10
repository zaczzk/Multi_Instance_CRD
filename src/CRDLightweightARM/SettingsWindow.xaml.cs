using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace CRDLightweightARM;

public partial class SettingsWindow : Window
{
    /// <summary>Editable copy of one session so Cancel leaves the real config untouched.</summary>
    private sealed class SessionDraft : INotifyPropertyChanged
    {
        private string _name = "";
        private string _url = "";

        public required SessionItem Source { get; init; }
        public required string OriginalUrl { get; init; }
        public string Title => $"Session {Source.Index}";

        public string NamePlaceholder => string.IsNullOrWhiteSpace(Source.Name)
            ? "Blank = the remote computer's name once connected"
            : $"Blank = use \"{Source.Name}\" (detected)";

        public string Name
        {
            get => _name;
            set { _name = value; OnChanged(); }
        }

        public string Url
        {
            get => _url;
            set { _url = value; OnChanged(); OnChanged(nameof(UrlError)); }
        }

        public string UrlError => AppConfig.IsAllowedUrl(Url.Trim())
            ? ""
            : "Must be an https://remotedesktop.google.com address.";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly AppConfig _config;
    private readonly List<SessionDraft> _drafts;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        // Never taller than the screen, so Save / Cancel stay reachable at high display scaling.
        MaxHeight = SystemParameters.WorkArea.Height;

        _drafts = config.Sessions.Select(s => new SessionDraft
        {
            Source = s,
            OriginalUrl = s.Url,
            Name = s.CustomName ?? "",
            Url = s.Url,
        }).ToList();
        SessionList.ItemsSource = _drafts;

        ShortcutList.ItemsSource = Shortcuts.All
            .Select(s => new { s.Keys, s.Description })
            .Append(new { Keys = Shortcuts.GlobalShowHideKeys, Description = "Show / hide the app (from anywhere)" })
            .ToList();

        ChkPause.IsChecked = config.PauseBackgroundSessions;
        ChkPin.IsChecked = config.PinBar;
        ChkFullscreen.IsChecked = config.StartFullscreen;
        ChkTray.IsChecked = config.MinimizeToTrayOnClose;
        ChkHotkey.Content = $"Global shortcut {Shortcuts.GlobalShowHideKeys} to show / hide";
        ChkHotkey.IsChecked = config.GlobalHotkeyEnabled;
        TxtDataPath.Text = $"Settings and sign-in data: {AppConfig.DataDirectory}";
        TxtDataPath.ToolTip = AppConfig.DataDirectory;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_drafts.Any(d => d.UrlError.Length > 0)) return;

        foreach (var draft in _drafts)
        {
            var name = draft.Name.Trim();
            draft.Source.CustomName = name.Length == 0 ? null : name;
            // Only overwrite the start page if it was edited: it may have been updated by a
            // reconnect while this dialog was open.
            var url = draft.Url.Trim();
            if (url != draft.OriginalUrl) draft.Source.Url = url;
        }

        _config.PauseBackgroundSessions = ChkPause.IsChecked == true;
        _config.PinBar = ChkPin.IsChecked == true;
        _config.StartFullscreen = ChkFullscreen.IsChecked == true;
        _config.MinimizeToTrayOnClose = ChkTray.IsChecked == true;
        _config.GlobalHotkeyEnabled = ChkHotkey.IsChecked == true;

        DialogResult = true;
    }
}
