using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using static CRDLightweightARM.NativeMethods;

namespace CRDLightweightARM;

public partial class MainWindow : Window
{
    private enum ConnState { Idle, Connecting, Connected, Failed }

    /// <summary>Everything the window tracks for one session slot.</summary>
    private sealed class Slot
    {
        public required int Index { get; init; }
        public required WebView2 View { get; init; }
        public Button Pill { get; set; } = null!;
        public Ellipse Dot { get; set; } = null!;
        public TextBlock Label { get; set; } = null!;
        public ConnState State { get; set; } = ConnState.Idle;
        public string? Stats { get; set; }
        public ImageSource? Thumbnail { get; set; }
        public DateTime LastExternalOpen { get; set; }
        public DateTime? UnresponsiveSince { get; set; }
        public List<DateTime> RecentCrashes { get; } = new();
        public CoreWebView2? Core => View.CoreWebView2;
    }

    private sealed record KeyDef(string Key, string Code, int VirtualKey, int ModifierBit, int Location = 0);

    private static readonly KeyDef KeyCtrl = new("Control", "ControlLeft", 0x11, 2, 1);
    private static readonly KeyDef KeyAlt = new("Alt", "AltLeft", 0x12, 1, 1);
    private static readonly KeyDef KeyShift = new("Shift", "ShiftLeft", 0x10, 8, 1);
    private static readonly KeyDef KeyMeta = new("Meta", "MetaLeft", 0x5B, 4, 1);
    private static readonly KeyDef KeyDelete = new("Delete", "Delete", 0x2E, 0);
    private static readonly KeyDef KeyEscape = new("Escape", "Escape", 0x1B, 0);
    private static readonly KeyDef KeyD = new("d", "KeyD", 0x44, 0);

    private const int HotkeyShowHideId = 9001;
    private const uint VK_R = 0x52;

    private readonly AppConfig _config;
    private readonly string _mediatorScript;
    private readonly Slot[] _slots;
    private readonly TrayIconHelper _trayIcon = new();
    private CoreWebView2Environment? _webViewEnvironment;

    private int _current = 1;
    private int _lastBackground = 2;
    private bool _split;
    private int _splitLeft = 1;
    private int _splitRight = 2;
    private bool _isFullscreen;
    private bool _exiting;
    private bool _trayHintShown;
    private WindowState _lastWindowState = WindowState.Normal;

    // Top bar
    private Window? _barWindow;
    private bool _barVisible = true;
    private readonly DispatcherTimer _revealTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _thumbnailCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    // Previews of on-screen sessions, so hovering a tab later shows a recent picture. Infrequent on purpose.
    private readonly DispatcherTimer _thumbnailRefreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime? _revealDwellStart;
    private int _hoveredSlot;

    // Shortcuts
    private IntPtr _keyboardHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _keyboardHookProc;
    private bool _globalHotkeyRegistered;
    private SettingsWindow? _settingsDialog;

    private IntPtr Handle => new WindowInteropHelper(this).Handle;
    private Slot CurrentSlot => _slots[_current - 1];
    private Slot SlotAt(int index) => _slots[index - 1];

    public MainWindow()
    {
        InitializeComponent();
        TrySetWindowIcon();

        _config = AppConfig.Load();
        _mediatorScript = LoadMediatorScript();

        _slots = new[]
        {
            new Slot { Index = 1, View = WebViewSession1 },
            new Slot { Index = 2, View = WebViewSession2 },
            new Slot { Index = 3, View = WebViewSession3 },
        };
        BuildSessionPills();

        _revealTimer.Tick += RevealTimer_Tick;
        _hideTimer.Tick += HideTimer_Tick;
        _thumbnailCloseTimer.Tick += (_, _) => { _thumbnailCloseTimer.Stop(); ThumbnailPopup.IsOpen = false; };
        _thumbnailRefreshTimer.Tick += (_, _) => CaptureShownSessions();

        ApplyWindowMode(_config.StartFullscreen);

        SourceInitialized += (_, _) => UseDarkTitleBar(Handle);
        Loaded += MainWindow_Loaded;
        LocationChanged += (_, _) => PositionBar();
        SizeChanged += (_, _) => PositionBar();
        StateChanged += MainWindow_StateChanged;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    #region Startup

    private static string LoadMediatorScript()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("crd-mediator.js", StringComparison.OrdinalIgnoreCase));
        if (name == null) return string.Empty;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/CRDLightweightARM;component/app.ico"));
            if (info?.Stream != null)
            {
                using var s = info.Stream;
                Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
        }
        catch { }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        HwndSource.FromHwnd(Handle)?.AddHook(WndProc);

        CreateBarWindow();
        InstallKeyboardHook();

        _trayIcon.Initialize(this, "Multi-Instance CRD");
        UpdateGlobalHotkey(); // after the tray icon exists, so a clash can be reported there
        _trayIcon.LeftClick += ToggleWindowVisibility;
        _trayIcon.RightClick += ShowTrayMenu;

        RefreshPills();
        UpdateBarButtons();

        try
        {
            var userDataFolder = Path.Combine(AppConfig.DataDirectory, "User Data");
            // Background sessions are hidden WebViews; keep Chromium from throttling them into a disconnect.
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-background-timer-throttling --disable-backgrounding-occluded-windows " +
                "--disable-renderer-backgrounding --autoplay-policy=no-user-gesture-required");
            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Multi-Instance CRD needs the Microsoft Edge WebView2 Runtime, which is missing on this PC.\n\n" +
                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start the app again.",
                "WebView2 Runtime required", MessageBoxButton.OK, MessageBoxImage.Warning);
            ExitApp();
            return;
        }
        catch (Exception ex)
        {
            CrashLog.Write("WebView2 environment failed to start", ex);
            MessageBox.Show(
                $"The browser engine could not start:\n\n{ex.Message}\n\nDetails: {CrashLog.FilePath}",
                "Multi-Instance CRD", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp();
            return;
        }

        foreach (var slot in _slots)
        {
            try
            {
                await InitializeSessionAsync(slot);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"Session {slot.Index} failed to start", ex);
                slot.State = ConnState.Failed;
            }
        }

        // Older builds could leave a clipboard decision saved in the shared profile, which would bypass
        // the per-session check in PermissionRequested. Reset it so every request is asked again.
        try
        {
            if (_slots[0].Core is { } core)
            {
                foreach (var origin in new[] { "https://remotedesktop.google.com", "https://remotedesktop.corp.google.com" })
                {
                    await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.ClipboardRead, origin, CoreWebView2PermissionState.Default);
                }
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("Could not reset saved clipboard permission", ex.Message);
        }

        ApplyLayout();
        ApplySessionActivity();
        RefreshPills();
        CurrentSlot.View.Focus();
        _thumbnailRefreshTimer.Start();

        // Show the bar for a few seconds on launch so people know it exists, then let it auto-hide.
        await Task.Delay(3000);
        if (!_config.PinBar && _barVisible) _hideTimer.Start();
    }

    private async Task InitializeSessionAsync(Slot slot)
    {
        var view = slot.View;
        view.DefaultBackgroundColor = System.Drawing.Color.Black;
        await view.EnsureCoreWebView2Async(_webViewEnvironment);
        var core = view.CoreWebView2;

        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        // F5 / Ctrl+R / Ctrl+W etc. go to the remote computer instead of reloading the session.
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;

        if (!string.IsNullOrEmpty(_mediatorScript))
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(_mediatorScript);
        }

        view.GotFocus += (_, _) => MarkActive(slot.Index);

        core.NavigationStarting += (_, _) =>
        {
            slot.State = ConnState.Idle;
            slot.Stats = null;
            RefreshPills();
        };

        core.NavigationCompleted += (_, _) =>
        {
            slot.UnresponsiveSince = null;
            ApplySessionActivity(slot);
        };

        core.PermissionRequested += (_, args) =>
        {
            bool isCrd = Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && AppConfig.IsCrdUri(uri);
            // Clipboard sync only for the session you are working in; everything else is declined.
            // Not saved to the (shared) profile, so the decision is made per request and per session.
            args.SavesInProfile = false;
            args.State = isCrd && args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead && slot.Index == _current
                ? CoreWebView2PermissionState.Allow
                : CoreWebView2PermissionState.Deny;
            args.Handled = true;
        };

        core.NewWindowRequested += (_, args) =>
        {
            // Links that would open a popup go to the default browser instead, but only when the user
            // clicked something or the request comes from the CRD page itself, and at most once a second.
            args.Handled = true;
            bool fromCrd = Uri.TryCreate(core.Source, UriKind.Absolute, out var source) && AppConfig.IsCrdUri(source);
            if (!(args.IsUserInitiated || fromCrd) || (DateTime.UtcNow - slot.LastExternalOpen).TotalSeconds < 1) return;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                slot.LastExternalOpen = DateTime.UtcNow;
                OpenInDefaultBrowser(uri);
            }
        };

        core.SourceChanged += (_, _) =>
        {
            if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !AppConfig.IsCrdUri(uri)) return;

            if (uri.AbsolutePath.StartsWith("/access/session/", StringComparison.OrdinalIgnoreCase))
            {
                // Remember the last computer this slot connected to so it reconnects directly next time.
                var session = _config.GetSession(slot.Index);
                if (session.Url != core.Source)
                {
                    session.Url = core.Source;
                    _config.Save();
                }
                UpdateHostName(slot, core);
            }
            else if (slot.State != ConnState.Idle)
            {
                // Back on the computer list (in-page navigation): the session is over even if no event said so.
                slot.State = ConnState.Idle;
                slot.Stats = null;
                RefreshPills();
            }
        };

        core.DocumentTitleChanged += (_, _) => UpdateHostName(slot, core);
        core.WebMessageReceived += (_, args) => OnWebMessage(slot, args);
        core.ProcessFailed += (_, args) => OnProcessFailed(slot, args);

        core.Navigate(_config.GetSession(slot.Index).Url);
    }

    private bool _browserFailureHandled;

    private async void OnProcessFailed(Slot slot, CoreWebView2ProcessFailedEventArgs args)
    {
        var kind = args.ProcessFailedKind;
        CrashLog.Write($"Session {slot.Index} WebView process failed", kind);

        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // Every session shares one browser process, so this is reported once per session: handle it once.
            if (_browserFailureHandled) return;
            _browserFailureHandled = true;
            foreach (var s in _slots) s.State = ConnState.Failed;
            RefreshPills();
            MessageBox.Show(this,
                "The browser engine stopped unexpectedly, so all sessions were disconnected.\n\nMulti-Instance CRD will close now. Please start it again.",
                "Multi-Instance CRD", MessageBoxButton.OK, MessageBoxImage.Warning);
            ExitApp();
            return;
        }

        var now = DateTime.UtcNow;
        if (kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // Often recovers on its own. Only reload if it is still hung well after the first report.
            // A report long after the previous one is a new hang, not the same one continuing.
            if (slot.UnresponsiveSince == null || (now - slot.UnresponsiveSince.Value).TotalSeconds > 45)
            {
                slot.UnresponsiveSince = now;
                return;
            }
            if ((now - slot.UnresponsiveSince.Value).TotalSeconds < 12) return;
        }
        else if (kind != CoreWebView2ProcessFailedKind.RenderProcessExited)
        {
            return; // GPU / utility processes are restarted by WebView2 itself.
        }

        slot.UnresponsiveSince = null;
        slot.RecentCrashes.RemoveAll(t => (now - t).TotalSeconds > 60);
        slot.RecentCrashes.Add(now);
        slot.State = ConnState.Failed;
        RefreshPills();
        if (slot.RecentCrashes.Count > 3) return; // keeps crashing: leave it in the failed state

        await Task.Delay(1000 * slot.RecentCrashes.Count);
        try { slot.Core?.Reload(); } catch { }
    }

    private static void OpenInDefaultBrowser(Uri uri)
    {
        // Started outside the app's kill-on-exit job, so quitting the app never closes the user's browser.
        if (NativeMethods.TryStartOutsideJob($"\"{Environment.SystemDirectory}\\rundll32.exe\" url.dll,FileProtocolHandler {uri.AbsoluteUri}")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    #endregion

    #region Page messages

    private void OnWebMessage(Slot slot, CoreWebView2WebMessageReceivedEventArgs args)
    {
        string? msg;
        try { msg = args.TryGetWebMessageAsString(); }
        catch { return; }
        if (string.IsNullOrEmpty(msg)) return;
        slot.UnresponsiveSince = null; // the page is talking, so its renderer is alive

        if (msg == "FOCUSED")
        {
            MarkActive(slot.Index);
        }
        else if (msg.StartsWith("CONN_STATE:", StringComparison.Ordinal))
        {
            slot.State = msg["CONN_STATE:".Length..] switch
            {
                "connected" => ConnState.Connected,
                "new" or "connecting" => ConnState.Connecting,
                "failed" => ConnState.Failed,
                _ => ConnState.Idle,
            };
            if (slot.State != ConnState.Connected) slot.Stats = null;
            else ApplySessionActivity(slot); // a background session that just connected should start paused
            RefreshPills();
        }
        else if (msg.StartsWith("{\"type\":\"STATS\"", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(msg);
                var root = doc.RootElement;
                int rtt = root.GetProperty("rtt").GetInt32();
                int fps = root.GetProperty("fps").GetInt32();
                int w = root.GetProperty("width").GetInt32();
                int h = root.GetProperty("height").GetInt32();
                slot.Stats = w > 0 ? $"{rtt} ms · {fps} fps · {w}×{h}" : $"{rtt} ms · {fps} fps";
                if (ThumbnailPopup.IsOpen && _hoveredSlot == slot.Index) UpdatePeekText(slot);
            }
            catch { }
        }
    }

    /// <summary>Names the slot after the connected computer. Only titles of CRD session pages count.</summary>
    private void UpdateHostName(Slot slot, CoreWebView2 core)
    {
        if (!Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) || !AppConfig.IsCrdUri(uri) ||
            !uri.AbsolutePath.StartsWith("/access/session/", StringComparison.OrdinalIgnoreCase)) return;

        var title = core.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title)) return;
        var cleaned = title.Replace("- Chrome Remote Desktop", "").Replace("Chrome Remote Desktop", "").Trim(' ', '-', '–');
        if (AppConfig.IsGenericPageTitle(cleaned)) return;

        var session = _config.GetSession(slot.Index);
        if (session.Name == cleaned) return;
        session.Name = cleaned;
        _config.Save();
        RefreshPills();
    }

    #endregion

    #region Sessions, split view and background pausing

    public void SwitchToSession(int target)
    {
        if (target < 1 || target > AppConfig.SessionCount) return;
        ThumbnailPopup.IsOpen = false;

        int previous = _current;
        if (target == previous)
        {
            CurrentSlot.View.Focus();
            return;
        }

        if (!IsShown(target)) _lastBackground = previous;

        if (_split)
        {
            // A session that is already on screen just gets focus; otherwise it replaces the focused pane.
            if (target != _splitLeft && target != _splitRight)
            {
                if (_current == _splitLeft) _splitLeft = target; else _splitRight = target;
            }
            _current = target;
            ApplyLayout();
        }
        else
        {
            // Swap in one step: WebView2 is a native child window, so WPF z-order can't stack two of them.
            _current = target;
            ApplyVisibility();
        }

        CurrentSlot.View.Focus();
        OnCurrentSessionChanged();
    }

    private void OnCurrentSessionChanged()
    {
        RefreshPills();
        ApplySessionActivity();
    }

    /// <summary>In split view, clicking into a pane makes it the current session (sound, clipboard, Send keys).</summary>
    private void MarkActive(int index)
    {
        if (!_split || index == _current || !IsShown(index)) return;
        _current = index;
        OnCurrentSessionChanged();
    }

    private void CycleNextSession() => SwitchToSession(_current % AppConfig.SessionCount + 1);

    private bool IsShown(int index) => _split ? index == _splitLeft || index == _splitRight : index == _current;

    private void ToggleSplit()
    {
        _split = !_split;
        if (_split)
        {
            _splitLeft = _current;
            // Second pane: the last session you were in, unless another one is connected and it isn't.
            var others = _slots.Where(s => s.Index != _current).ToList();
            var last = others.FirstOrDefault(s => s.Index == _lastBackground);
            _splitRight = (last != null && last.State == ConnState.Connected ? last
                : others.FirstOrDefault(s => s.State == ConnState.Connected) ?? last ?? others[0]).Index;
            ResetSplitRatio();
        }
        ApplyLayout();
        RefreshPills();
        ApplySessionActivity();
        UpdateBarButtons();
    }

    private void ApplyLayout()
    {
        if (_split)
        {
            ColDivider.Width = GridLength.Auto;
            if (ColRight.Width.Value == 0) ResetSplitRatio();
            SplitDivider.Visibility = Visibility.Visible;
            foreach (var slot in _slots)
            {
                Grid.SetColumn(slot.View, slot.Index == _splitRight ? 2 : 0);
            }
        }
        else
        {
            ColLeft.Width = new GridLength(1, GridUnitType.Star);
            ColDivider.Width = new GridLength(0);
            ColRight.Width = new GridLength(0);
            SplitDivider.Visibility = Visibility.Collapsed;
            foreach (var slot in _slots) Grid.SetColumn(slot.View, 0);
        }
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        foreach (var slot in _slots)
        {
            slot.View.Visibility = IsShown(slot.Index) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ResetSplitRatio()
    {
        ColLeft.Width = new GridLength(1, GridUnitType.Star);
        ColRight.Width = new GridLength(1, GridUnitType.Star);
    }

    private void SplitDivider_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ResetSplitRatio();

    /// <summary>Sound only from the focused session; video paused for sessions that are off screen (if enabled).</summary>
    private void ApplySessionActivity()
    {
        foreach (var slot in _slots) ApplySessionActivity(slot);
    }

    private void ApplySessionActivity(Slot slot)
    {
        var core = slot.Core;
        if (core == null) return;
        try
        {
            core.IsMuted = slot.Index != _current;
            bool keepRunning = (IsVisible && IsShown(slot.Index)) || !_config.PauseBackgroundSessions;
            core.MemoryUsageTargetLevel = keepRunning ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
            RunScript(core, keepRunning
                ? "window.__crdResumeSession && window.__crdResumeSession();"
                : "window.__crdPauseSession && window.__crdPauseSession();");
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Session {slot.Index} activity update failed", ex.Message);
        }
    }

    private static async void RunScript(CoreWebView2? core, string script)
    {
        if (core == null) return;
        try { await core.ExecuteScriptAsync(script); }
        catch (Exception ex) { Debug.WriteLine($"Script failed: {ex.Message}"); }
    }

    private void CaptureShownSessions()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        foreach (var slot in _slots.Where(s => IsShown(s.Index))) _ = CaptureThumbnailAsync(slot);
    }

    private static async Task CaptureThumbnailAsync(Slot slot)
    {
        var core = slot.Core;
        if (core == null || slot.State != ConnState.Connected) return;
        try
        {
            using var stream = new MemoryStream();
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, stream);
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 512;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            slot.Thumbnail = bitmap;
        }
        catch { }
    }

    #endregion

    #region Top bar

    private void CreateBarWindow()
    {
        RootGrid.Children.Remove(BarRoot);
        _barWindow = new Window
        {
            Title = "Multi-Instance CRD bar",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent, // transparent pixels pass clicks through to the session
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Owner = this,
            Content = BarRoot,
        };
        _barWindow.SourceInitialized += (_, _) => MakeNonActivating(new WindowInteropHelper(_barWindow).Handle);
        _barWindow.SizeChanged += (_, _) => PositionBar();
        _barVisible = false;
        ShowBar();
    }

    private void PositionBar()
    {
        if (_barWindow == null || !_barWindow.IsVisible || !IsVisible) return;
        var bar = new WindowInteropHelper(_barWindow).Handle;
        if (bar == IntPtr.Zero || !GetClientRect(Handle, out var client) || !GetWindowRect(bar, out var barRect)) return;

        var origin = new POINT();
        ClientToScreen(Handle, ref origin);
        int barWidth = barRect.Right - barRect.Left;
        int x = origin.X + (client.Right - client.Left - barWidth) / 2;
        SetWindowPos(bar, IntPtr.Zero, x, origin.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void ShowBar()
    {
        _revealTimer.Stop();
        _revealDwellStart = null;
        if (_barWindow == null || _barVisible || !IsVisible || WindowState == WindowState.Minimized) return;

        _barVisible = true;
        _barWindow.Show();
        PositionBar();

        // Refresh previews of what's on screen now: the next hover over a tab will want them.
        CaptureShownSessions();
        var from = -Math.Max(Bar.ActualHeight, 40);
        BarTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void HideBar(bool animate = true)
    {
        _hideTimer.Stop();
        if (_barWindow == null || !_barVisible) return;

        _barVisible = false;
        KeysPopup.IsOpen = false;
        ThumbnailPopup.IsOpen = false;

        if (!animate)
        {
            BarTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _barWindow.Hide();
        }
        else
        {
            var anim = new DoubleAnimation(-(Bar.ActualHeight + 4), TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            anim.Completed += (_, _) => { if (!_barVisible) _barWindow.Hide(); };
            BarTransform.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        if (IsVisible) _revealTimer.Start();
    }

    private void ToggleBar()
    {
        if (_barVisible)
        {
            if (_config.PinBar) SetPinned(false);
            HideBar();
        }
        else
        {
            ShowBar();
        }
    }

    private void SetPinned(bool pinned)
    {
        _config.PinBar = pinned;
        _config.Save();
        UpdateBarButtons();
        if (pinned) ShowBar();
        else if (!IsCursorOverBar()) _hideTimer.Start();
    }

    /// <summary>Polls the cursor while the bar is hidden: resting at the top edge of the window reveals it.</summary>
    private void RevealTimer_Tick(object? sender, EventArgs e)
    {
        if (_barVisible || !IsVisible || WindowState == WindowState.Minimized || !IsOurWindowActive() || !IsCursorInRevealZone())
        {
            _revealDwellStart = null;
            return;
        }

        if (_revealDwellStart == null)
        {
            _revealDwellStart = DateTime.UtcNow;
        }
        else if ((DateTime.UtcNow - _revealDwellStart.Value).TotalMilliseconds >= _config.BarRevealDelayMs)
        {
            ShowBar();
            if (!_config.PinBar) _hideTimer.Start();
        }
    }

    /// <summary>
    /// Keeps polling until the bar can hide: no MouseLeave is guaranteed after the pointer
    /// leaves a popup or the bar window's transparent margin.
    /// </summary>
    private void HideTimer_Tick(object? sender, EventArgs e)
    {
        if (_config.PinBar || !_barVisible)
        {
            _hideTimer.Stop();
            return;
        }
        if (KeysPopup.IsOpen || IsCursorOverBar() || ThumbnailPopup.IsMouseOver || IsCursorInRevealZone()) return;
        HideBar();
    }

    /// <summary>The thin strip along the top edge of the window, as wide as the bar (plus some slack).</summary>
    private bool IsCursorInRevealZone()
    {
        if (!GetCursorPos(out var cursor) || !GetClientRect(Handle, out var client)) return false;

        var origin = new POINT();
        ClientToScreen(Handle, ref origin);
        int width = client.Right - client.Left;
        int center = origin.X + width / 2;
        int half = 220;
        if (_barWindow != null && GetWindowRect(new WindowInteropHelper(_barWindow).Handle, out var bar) && bar.Right > bar.Left)
        {
            half = Math.Max(half, (bar.Right - bar.Left) / 2);
        }

        return cursor.Y >= origin.Y - 1 && cursor.Y <= origin.Y + 2 &&
               cursor.X >= center - half && cursor.X <= center + half;
    }

    /// <summary>True when the pointer is over the visible bar itself (not its transparent shadow margin).</summary>
    private bool IsCursorOverBar()
    {
        if (!_barVisible || !GetCursorPos(out var p) || PresentationSource.FromVisual(Bar) == null) return false;
        var topLeft = Bar.PointToScreen(new Point(0, 0));
        var bottomRight = Bar.PointToScreen(new Point(Bar.ActualWidth, Bar.ActualHeight));
        return p.X >= topLeft.X && p.X < bottomRight.X && p.Y >= topLeft.Y - 2 && p.Y < bottomRight.Y;
    }

    private void Bar_MouseEnter(object sender, MouseEventArgs e) => _hideTimer.Stop();

    private void Bar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_config.PinBar) _hideTimer.Start();
    }

    private void KeysPopup_Closed(object? sender, EventArgs e)
    {
        if (!_config.PinBar && _barVisible) _hideTimer.Start();
    }

    private void BuildSessionPills()
    {
        foreach (var slot in _slots)
        {
            slot.Dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            slot.Label = new TextBlock { MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(slot.Dot);
            content.Children.Add(slot.Label);

            var pill = new Button
            {
                Style = (Style)FindResource("SessionPillStyle"),
                Content = content,
                Margin = new Thickness(1, 0, 1, 0),
            };
            int index = slot.Index;
            pill.Click += (_, _) => SwitchToSession(index);
            pill.MouseEnter += (_, _) => ShowPeek(index);
            pill.MouseLeave += (_, _) => _thumbnailCloseTimer.Start();
            slot.Pill = pill;
            SessionPills.Children.Add(pill);
        }
    }

    private void RefreshPills()
    {
        foreach (var slot in _slots)
        {
            bool focused = slot.Index == _current;
            bool onScreen = IsShown(slot.Index);
            slot.Label.Text = _config.GetSession(slot.Index).DisplayName;
            slot.Pill.Background = focused ? (Brush)FindResource("SelectedBrush")
                : onScreen ? (Brush)FindResource("PressedBrush")
                : Brushes.Transparent;
            slot.Pill.Foreground = (Brush)FindResource(onScreen ? "TextPrimaryBrush" : "TextSecondaryBrush");
            slot.Label.FontWeight = focused ? FontWeights.SemiBold : FontWeights.Normal;
            slot.Dot.Fill = (Brush)FindResource(slot.State switch
            {
                ConnState.Connected => "StatusConnectedBrush",
                ConnState.Connecting => "StatusConnectingBrush",
                ConnState.Failed => "StatusFailedBrush",
                _ => "StatusIdleBrush",
            });
            // What a screen reader announces for the tab, e.g. "Session 1: DESKTOP-LLM, connected, current".
            AutomationProperties.SetName(slot.Pill,
                $"Session {slot.Index}: {slot.Label.Text}, {StatusText(slot.State).ToLowerInvariant()}{(focused ? ", current" : "")}");
        }
        if (ThumbnailPopup.IsOpen && _hoveredSlot > 0) UpdatePeekText(SlotAt(_hoveredSlot));
        _trayIcon.UpdateTooltip($"Multi-Instance CRD — {_config.GetSession(_current).DisplayName}");
    }

    private static string StatusText(ConnState state) => state switch
    {
        ConnState.Connected => "Connected",
        ConnState.Connecting => "Connecting…",
        ConnState.Failed => "Connection failed",
        _ => "Not connected",
    };

    private void UpdateBarButtons()
    {
        BtnSplit.Foreground = (Brush)FindResource(_split ? "AccentBrush" : "TextPrimaryBrush");
        BtnSplit.ToolTip = $"{(_split ? "Turn off split view" : "Split view: two sessions side by side")}  ({Shortcuts.KeysFor(ShortcutAction.ToggleSplit)})";
        BtnFullscreen.Content = _isFullscreen ? "" : "";
        BtnFullscreen.ToolTip = $"{(_isFullscreen ? "Exit fullscreen" : "Fullscreen")}  ({Shortcuts.KeysFor(ShortcutAction.ToggleFullscreen)})";
        BtnPin.Content = _config.PinBar ? "" : "";
        BtnPin.ToolTip = _config.PinBar ? "Unpin: auto-hide this bar" : $"Pin this bar open  (reveal it any time by resting the pointer at the top edge, or {Shortcuts.KeysFor(ShortcutAction.ToggleBar)})";
        BtnHide.ToolTip = $"Hide to the system tray  ({Shortcuts.GlobalShowHideKeys} brings it back)";
    }

    private void ShowPeek(int index)
    {
        _thumbnailCloseTimer.Stop();
        _hideTimer.Stop();
        _hoveredSlot = index;
        var slot = SlotAt(index);
        bool showImage = !IsShown(index) && slot.Thumbnail != null;
        ImgThumbnail.Source = showImage ? slot.Thumbnail : null;
        ((FrameworkElement)ImgThumbnail.Parent).Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        UpdatePeekText(slot);
        ThumbnailPopup.PlacementTarget = slot.Pill;
        ThumbnailPopup.IsOpen = true;
    }

    private void UpdatePeekText(Slot slot)
    {
        var lines = new List<string> { $"{_config.GetSession(slot.Index).DisplayName} · {StatusText(slot.State)}" };
        if (slot.Stats != null && slot.State == ConnState.Connected) lines.Add(slot.Stats);
        lines.Add(slot.Index == _current ? "Current session" : $"Click to switch  ({Shortcuts.KeysFor((ShortcutAction)(slot.Index - 1))})");
        TxtThumbnail.Text = string.Join("\n", lines);
    }

    private void ThumbnailPopup_MouseEnter(object sender, MouseEventArgs e) => _thumbnailCloseTimer.Stop();

    private void ThumbnailPopup_MouseLeave(object sender, MouseEventArgs e)
    {
        _thumbnailCloseTimer.Start();
        if (!_config.PinBar && _barVisible) _hideTimer.Start();
    }

    private void ThumbnailPopup_Click(object sender, MouseButtonEventArgs e)
    {
        ThumbnailPopup.IsOpen = false;
        if (_hoveredSlot > 0) SwitchToSession(_hoveredSlot);
        if (!_config.PinBar && _barVisible) _hideTimer.Start();
        e.Handled = true;
    }

    private void BtnSplit_Click(object sender, RoutedEventArgs e) => ToggleSplit();
    private void BtnKeys_Click(object sender, RoutedEventArgs e) => KeysPopup.IsOpen = !KeysPopup.IsOpen;
    private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void BtnPin_Click(object sender, RoutedEventArgs e) => SetPinned(!_config.PinBar);
    private void BtnHide_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    #endregion

    #region Send keys to the remote computer

    private async void BtnSendCtrlAltDel_Click(object sender, RoutedEventArgs e) => await SendKeyChordAsync(KeyCtrl, KeyAlt, KeyDelete);
    private async void BtnSendTaskManager_Click(object sender, RoutedEventArgs e) => await SendKeyChordAsync(KeyCtrl, KeyShift, KeyEscape);
    private async void BtnSendWin_Click(object sender, RoutedEventArgs e) => await SendKeyChordAsync(KeyMeta);
    private async void BtnSendWinD_Click(object sender, RoutedEventArgs e) => await SendKeyChordAsync(KeyMeta, KeyD);

    /// <summary>
    /// Presses the keys in order and releases them in reverse as real key events in the page,
    /// so Chrome Remote Desktop forwards them to the remote computer exactly once.
    /// </summary>
    private async Task SendKeyChordAsync(params KeyDef[] keys)
    {
        KeysPopup.IsOpen = false;
        var core = CurrentSlot.Core;
        if (core == null) return;
        CurrentSlot.View.Focus();

        int modifiers = 0;
        int pressed = 0;
        try
        {
            foreach (var key in keys)
            {
                modifiers |= key.ModifierBit;
                await DispatchKeyAsync(core, "rawKeyDown", key, modifiers);
                pressed++;
            }
            await Task.Delay(60);
        }
        catch (Exception ex)
        {
            CrashLog.Write("Sending keys failed", ex.Message);
        }
        finally
        {
            for (int i = pressed - 1; i >= 0; i--)
            {
                modifiers &= ~keys[i].ModifierBit;
                try { await DispatchKeyAsync(core, "keyUp", keys[i], modifiers); } catch { }
            }
        }
    }

    private static Task DispatchKeyAsync(CoreWebView2 core, string type, KeyDef key, int modifiers)
    {
        var json = JsonSerializer.Serialize(new
        {
            type,
            key = key.Key,
            code = key.Code,
            windowsVirtualKeyCode = key.VirtualKey,
            nativeVirtualKeyCode = key.VirtualKey,
            location = key.Location,
            modifiers,
        });
        return core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", json);
    }

    #endregion

    #region Keyboard shortcuts

    private void InstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _keyboardHookProc = KeyboardHookCallback;
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookProc, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero) CrashLog.Write("Keyboard hook failed", Marshal.GetLastWin32Error());
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
    }

    /// <summary>
    /// Ctrl+Alt shortcuts are caught before the remote session sees them, but only while this app is in front.
    /// AltGr (reported by Windows as Left Ctrl + Right Alt) is ignored so it still types characters.
    /// </summary>
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) && IsOurWindowActive())
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool ctrl = IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
            bool alt = IsKeyDown(VK_LMENU) || (IsKeyDown(VK_RMENU) && IsKeyDown(VK_RCONTROL) && !IsKeyDown(VK_LCONTROL));
            bool other = IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);

            if (ctrl && alt && !other && Shortcuts.Find(kb.vkCode) is { } shortcut)
            {
                Dispatcher.BeginInvoke(() => RunShortcut(shortcut.Action));
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void RunShortcut(ShortcutAction action)
    {
        if (_settingsDialog != null) return; // modal Settings is open
        switch (action)
        {
            case ShortcutAction.Session1: SwitchToSession(1); break;
            case ShortcutAction.Session2: SwitchToSession(2); break;
            case ShortcutAction.Session3: SwitchToSession(3); break;
            case ShortcutAction.NextSession: CycleNextSession(); break;
            case ShortcutAction.ToggleSplit: ToggleSplit(); break;
            case ShortcutAction.ToggleFullscreen: ToggleFullscreen(); break;
            case ShortcutAction.ToggleBar: ToggleBar(); break;
        }
    }

    private bool IsOurWindowActive()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var main = Handle;
        var root = GetAncestor(fg, GA_ROOT);
        var owner = GetAncestor(fg, GA_ROOTOWNER);
        return fg == main || root == main || owner == main;
    }

    private void UpdateGlobalHotkey()
    {
        var hwnd = Handle;
        if (_config.GlobalHotkeyEnabled && !_globalHotkeyRegistered)
        {
            _globalHotkeyRegistered = RegisterHotKey(hwnd, HotkeyShowHideId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_R);
            if (!_globalHotkeyRegistered)
            {
                CrashLog.Write("Ctrl+Alt+R is already used by another app", Marshal.GetLastWin32Error());
                _trayIcon.ShowNotification("Shortcut unavailable",
                    $"{Shortcuts.GlobalShowHideKeys} is already used by another app. Use the tray icon to show Multi-Instance CRD.");
            }
        }
        else if (!_config.GlobalHotkeyEnabled && _globalHotkeyRegistered)
        {
            UnregisterHotKey(hwnd, HotkeyShowHideId);
            _globalHotkeyRegistered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyShowHideId)
        {
            ToggleWindowVisibility();
            handled = true;
        }
        else
        {
            _trayIcon.HandleHwndMessage(msg, wParam, lParam);
        }
        return IntPtr.Zero;
    }

    #endregion

    #region Window, fullscreen and tray

    private void ApplyWindowMode(bool fullscreen)
    {
        _isFullscreen = fullscreen;
        if (fullscreen)
        {
            // Leaving Maximized first makes WPF re-maximize over the taskbar with the borderless style.
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
        }
    }

    private void ToggleFullscreen()
    {
        ApplyWindowMode(!_isFullscreen);
        UpdateBarButtons();
        PositionBar();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            _lastWindowState = WindowState;
            if (_config.PinBar) ShowBar(); else if (!_barVisible) _revealTimer.Start();
            PositionBar();
        }
        else
        {
            HideBar(animate: false);
            _revealTimer.Stop();
        }
    }

    public void ToggleWindowVisibility()
    {
        if (_settingsDialog != null)
        {
            _settingsDialog.Activate();
            return;
        }
        if (IsVisible && WindowState != WindowState.Minimized && IsOurWindowActive()) HideToTray();
        else RestoreFromTray();
    }

    public void HideToTray()
    {
        HideBar(animate: false);
        _revealTimer.Stop();
        UninstallKeyboardHook();

        Hide();
        ApplySessionActivity(); // IsVisible is now false: every session pauses (if enabled)

        if (!_trayHintShown && _config.MinimizeToTrayOnClose)
        {
            _trayHintShown = true;
            _trayIcon.ShowNotification("Still running in the tray",
                _globalHotkeyRegistered ? $"Press {Shortcuts.GlobalShowHideKeys} or click the tray icon to bring it back." : "Click the tray icon to bring it back.");
        }
    }

    /// <summary>Called when the app is launched again while already running.</summary>
    public void ShowFromOtherInstance()
    {
        if (_settingsDialog != null)
        {
            _settingsDialog.Activate();
            return;
        }
        RestoreFromTray();
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastWindowState;
        Activate();
        SetForegroundWindow(Handle);

        InstallKeyboardHook();
        ApplySessionActivity();
        if (_config.PinBar) ShowBar(); else _revealTimer.Start();
        PositionBar();
        CurrentSlot.View.Focus();
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        var show = new MenuItem { Header = "Show", FontWeight = FontWeights.SemiBold };
        show.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(show);
        menu.Items.Add(new Separator());

        foreach (var slot in _slots)
        {
            int index = slot.Index;
            var item = new MenuItem
            {
                Header = $"{index}. {_config.GetSession(index).DisplayName}",
                IsCheckable = false,
                IsChecked = index == _current,
            };
            item.Click += (_, _) => { RestoreFromTray(); SwitchToSession(index); };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Settings…" };
        settings.Click += (_, _) => { RestoreFromTray(); OpenSettings(); };
        menu.Items.Add(settings);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) =>
        {
            _settingsDialog?.Close();
            ExitApp();
        };
        menu.Items.Add(exit);

        // While Settings is open, everything except Exit just brings the dialog back to the front.
        if (_settingsDialog != null)
        {
            foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i != exit)) item.IsEnabled = false;
        }

        SetForegroundWindow(Handle);
        menu.IsOpen = true;
    }

    private void OpenSettings()
    {
        if (_settingsDialog != null)
        {
            _settingsDialog.Activate();
            return;
        }

        KeysPopup.IsOpen = false;
        var oldUrls = _config.Sessions.ToDictionary(s => s.Index, s => s.Url);
        bool saved;
        try
        {
            _settingsDialog = new SettingsWindow(_config) { Owner = this };
            saved = _settingsDialog.ShowDialog() == true;
        }
        finally
        {
            _settingsDialog = null;
        }
        if (!saved) return;

        _config.Save();
        foreach (var slot in _slots)
        {
            var url = _config.GetSession(slot.Index).Url;
            // Only reload a slot whose start page was edited; a reconnect while the dialog was open
            // also changes the URL, but the page is already showing it.
            if (url != oldUrls[slot.Index] && slot.Core != null && !string.Equals(url, slot.Core.Source, StringComparison.Ordinal))
            {
                slot.Core.Navigate(url);
            }
        }
        UpdateGlobalHotkey();
        UpdateBarButtons();
        RefreshPills();
        ApplySessionActivity();
        if (_config.PinBar) ShowBar(); else if (_barVisible) _hideTimer.Start();
        CurrentSlot.View.Focus();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _config.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _revealTimer.Stop();
        _hideTimer.Stop();
        _thumbnailCloseTimer.Stop();
        _thumbnailRefreshTimer.Stop();
        UninstallKeyboardHook();
        if (_globalHotkeyRegistered) UnregisterHotKey(Handle, HotkeyShowHideId);
        _trayIcon.Dispose();
    }

    #endregion
}
