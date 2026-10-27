using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CRDLightweightARM;

public partial class MainWindow : Window
{
    private AppConfig _config;
    private CoreWebView2Environment? _webViewEnvironment;
    private int _currentSessionIndex = 1;
    private readonly DispatcherTimer _dwellTimer;
    private readonly DispatcherTimer _collapseDebounceTimer;
    private bool _isNotchVisible = true;
    private bool _isPinned = true;
    private readonly string _mediatorScript;

    // QoL Features State
    private readonly SnapdragonPowerGovernor _powerGovernor = new();
    private readonly Dictionary<int, ImageSource> _sessionThumbnails = new();
    private readonly string[] _displayModes = { "contain", "fill", "cover" };
    private int _displayModeIndex = 0; // 0 = Fit, 1 = Stretch, 2 = Zoom
    private bool _isCockpitMode = false;
    private int _cockpitSecondaryIndex = 2;
    private bool _isRelativeMouseActive = false;
    private DateTime _lastSwipeTime = DateTime.MinValue;

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();

        var mediatorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crd-mediator.js");
        if (File.Exists(mediatorPath))
        {
            _mediatorScript = File.ReadAllText(mediatorPath);
        }
        else
        {
            _mediatorScript = @"
                window.__crdMediaStream = null;
                window.__crdPauseSession = function() {
                    const v = document.querySelector('video');
                    if (v && v.srcObject) { window.__crdMediaStream = v.srcObject; v.srcObject = null; }
                };
                window.__crdResumeSession = async function() {
                    const v = document.querySelector('video');
                    if (window.__crdMediaStream && v) {
                        v.srcObject = window.__crdMediaStream;
                        try { await v.play(); } catch(e){}
                    }
                    window.dispatchEvent(new Event('resize'));
                };
            ";
        }

        _dwellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_config.NotchDwellMs) };
        _dwellTimer.Tick += DwellTimer_Tick;

        _collapseDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _collapseDebounceTimer.Tick += CollapseDebounceTimer_Tick;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _powerGovernor.Initialize(this);
        _powerGovernor.PowerSourceChanged += PowerGovernor_PowerSourceChanged;

        UpdateLabelsFromConfig();
        await InitializeWebViewEnvironmentAsync();
        await InitializeSessionAsync(1, WebViewSession1);
        await InitializeSessionAsync(2, WebViewSession2);
        await InitializeSessionAsync(3, WebViewSession3);

        SwitchToSession(1);
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ReleaseCursor();
        _powerGovernor.Dispose();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // Fail-safe: Always release Win32 cursor confinement if focus leaves window
        if (_isRelativeMouseActive)
        {
            ReleaseCursor();
        }
    }

    private void PowerGovernor_PowerSourceChanged(PowerSource source)
    {
        Dispatcher.Invoke(() =>
        {
            if (source == PowerSource.Battery)
            {
                // Snapdragon battery saving: enforce Eco-Warm on inactive
                for (int i = 1; i <= 3; i++)
                {
                    if (i != _currentSessionIndex)
                    {
                        var wv = GetWebViewByIndex(i);
                        if (wv?.CoreWebView2 != null)
                        {
                            wv.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                        }
                    }
                }
            }
            else
            {
                for (int i = 1; i <= 3; i++)
                {
                    var wv = GetWebViewByIndex(i);
                    if (wv?.CoreWebView2 != null)
                    {
                        wv.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                    }
                }
            }
        });
    }

    private async Task InitializeWebViewEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CRDLightweightARM",
            "User Data"
        );

        var browserArgs = string.Join(" ", new[]
        {
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--disable-features=IntensiveWakeUpThrottling,CalculateNativeWinOcclusion,FreezingOnBatterySaver,TabDiscarding",
            "--disable-user-gesture-requirement-for-presentation",
            "--autoplay-policy=no-user-gesture-required",
            "--enable-features=PlatformHEVCDecoderSupport,WebRtcHideLocalIpsWithMdns",
            "--enable-gpu-rasterization",
            "--enable-zero-copy"
        });

        var options = new CoreWebView2EnvironmentOptions(browserArgs);
        _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }

    private async Task InitializeSessionAsync(int index, WebView2 webView)
    {
        if (_webViewEnvironment == null) return;

        await webView.EnsureCoreWebView2Async(_webViewEnvironment);

        // Inject Warm Swap mediator script into every frame and page
        await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(_mediatorScript);

        // 1. Silent Scoped Clipboard Auto-Grant
        webView.CoreWebView2.PermissionRequested += (s, args) =>
        {
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
            {
                bool isCRD = uri.Scheme == Uri.UriSchemeHttps &&
                             (uri.Host.Equals("remotedesktop.google.com", StringComparison.OrdinalIgnoreCase) ||
                              uri.Host.Equals("remotedesktop.corp.google.com", StringComparison.OrdinalIgnoreCase));

                if (isCRD && args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                {
                    args.State = (_currentSessionIndex == index && IsActive)
                        ? CoreWebView2PermissionState.Allow
                        : CoreWebView2PermissionState.Deny;
                    args.Handled = true;
                    return;
                }
            }

            if (args.PermissionKind != CoreWebView2PermissionKind.Notifications)
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
            }
        };

        // 2. Direct Session Reconnect Persistence (Save session ID URL on connect)
        webView.CoreWebView2.SourceChanged += (s, args) =>
        {
            try
            {
                var source = webView.CoreWebView2.Source;
                if (!string.IsNullOrEmpty(source) && source.Contains("/access/session/"))
                {
                    var session = _config.Sessions.FirstOrDefault(item => item.Index == index);
                    if (session != null && session.Url != source)
                    {
                        session.Url = source;
                        _config.Save();
                    }
                }
            }
            catch {}
        };

        // 3. Auto-detect connected machine name from document title
        webView.CoreWebView2.DocumentTitleChanged += (s, args) =>
        {
            var title = webView.CoreWebView2.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var cleaned = title.Replace("- Chrome Remote Desktop", "")
                                   .Replace("Chrome Remote Desktop", "")
                                   .Trim();
                if (!string.IsNullOrEmpty(cleaned) && cleaned != "Access" && cleaned != "Computers")
                {
                    Dispatcher.Invoke(() => UpdateSessionName(index, cleaned));
                }
            }
        };

        // 4. WebMessage bridge for DOM-level events & Telemetry
        webView.WebMessageReceived += (s, args) =>
        {
            try
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == "SWITCH_1") SwitchToSession(1);
                else if (msg == "SWITCH_2") SwitchToSession(2);
                else if (msg == "SWITCH_3") SwitchToSession(3);
                else if (msg == "CYCLE") CycleNextSession();
                else if (msg == "TOGGLE_NOTCH") ToggleNotch();
                else if (msg == "CYCLE_DISPLAY_MODE") CycleDisplayMode();
                else if (msg == "TOGGLE_COCKPIT") ToggleCockpitMode();
                else if (msg == "TOGGLE_RELATIVE_MOUSE") ToggleRelativeMouseMode();
                else if (msg == "RELEASE_CURSOR") ReleaseCursor();
                else if (msg != null && msg.StartsWith("HOST_NAME:"))
                {
                    var name = msg.Substring("HOST_NAME:".Length).Trim();
                    if (!string.IsNullOrEmpty(name))
                    {
                        Dispatcher.Invoke(() => UpdateSessionName(index, name));
                    }
                }
                else if (msg != null && msg.StartsWith("{\"type\":\"STATS\""))
                {
                    if (_currentSessionIndex == index)
                    {
                        UpdateTelemetryBadge(msg);
                    }
                }
            }
            catch {}
        };

        // 5. WPF PreviewKeyDown fallback
        webView.PreviewKeyDown += (s, args) =>
        {
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            if (isCtrl && isAlt)
            {
                if (args.Key == Key.D1 || args.Key == Key.NumPad1) { SwitchToSession(1); args.Handled = true; }
                else if (args.Key == Key.D2 || args.Key == Key.NumPad2) { SwitchToSession(2); args.Handled = true; }
                else if (args.Key == Key.D3 || args.Key == Key.NumPad3) { SwitchToSession(3); args.Handled = true; }
                else if (args.Key == Key.Tab) { CycleNextSession(); args.Handled = true; }
                else if (args.Key == Key.S) { ToggleNotch(); args.Handled = true; }
                else if (args.Key == Key.D) { CycleDisplayMode(); args.Handled = true; }
                else if (args.Key == Key.C) { ToggleCockpitMode(); args.Handled = true; }
                else if (args.Key == Key.M) { ToggleRelativeMouseMode(); args.Handled = true; }
                else if (args.Key == Key.Home) { ReleaseCursor(); args.Handled = true; }
            }
        };

        // Navigate to session URL
        var currentSession = _config.Sessions.FirstOrDefault(s => s.Index == index);
        var url = currentSession?.Url ?? "https://remotedesktop.google.com/access";
        webView.CoreWebView2.Navigate(url);
    }

    private void UpdateTelemetryBadge(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int rtt = root.GetProperty("rtt").GetInt32();
            int fps = root.GetProperty("fps").GetInt32();

            Dispatcher.Invoke(() =>
            {
                TxtTelemetry.Text = $"{rtt}ms {fps}f";

                if (rtt < 35 && fps >= 30)
                {
                    TelemetryHealthDot.Fill = (Brush)FindResource("AccentGreenBrush");
                }
                else if (rtt < 80)
                {
                    TelemetryHealthDot.Fill = (Brush)FindResource("AccentAmberBrush");
                }
                else
                {
                    TelemetryHealthDot.Fill = new SolidColorBrush(Color.FromRgb(255, 107, 107));
                }
            });
        }
        catch {}
    }

    private async Task CaptureSessionThumbnailAsync(int sessionIndex, WebView2 webView)
    {
        try
        {
            using var memStream = new MemoryStream();
            await webView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Jpeg, memStream);
            memStream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 320;
            bitmap.StreamSource = memStream;
            bitmap.EndInit();
            bitmap.Freeze();

            _sessionThumbnails[sessionIndex] = bitmap;
        }
        catch {}
    }

    public async void SwitchToSession(int targetIndex)
    {
        if (targetIndex < 1 || targetIndex > 3) return;

        var currentView = GetWebViewByIndex(_currentSessionIndex);
        var targetView = GetWebViewByIndex(targetIndex);

        if (_isCockpitMode)
        {
            // In cockpit mode, simply set focus and audio solo
            _currentSessionIndex = targetIndex;
            targetView?.Focus();
            UpdateAudioMuteMatrix();
            UpdateSlotStatusPills();
            return;
        }

        if (currentView?.CoreWebView2 != null && targetIndex != _currentSessionIndex)
        {
            await CaptureSessionThumbnailAsync(_currentSessionIndex, currentView);

            if (_config.EfficiencyMode != WarmMode.LiveHot)
            {
                await currentView.CoreWebView2.ExecuteScriptAsync("window.__crdPauseSession && window.__crdPauseSession();");
                if (_config.EfficiencyMode == WarmMode.EcoWarm || _powerGovernor.CurrentPowerSource == PowerSource.Battery)
                {
                    currentView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                }
            }
            currentView.Visibility = Visibility.Collapsed;
        }

        if (targetView?.CoreWebView2 != null)
        {
            targetView.Visibility = Visibility.Visible;
            targetView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;

            if (_config.EfficiencyMode != WarmMode.LiveHot)
            {
                await targetView.CoreWebView2.ExecuteScriptAsync("window.__crdResumeSession && window.__crdResumeSession();");
            }
            targetView.Focus();
        }

        _currentSessionIndex = targetIndex;
        UpdateAudioMuteMatrix();
        UpdateSlotStatusPills();
    }

    private void UpdateAudioMuteMatrix()
    {
        for (int i = 1; i <= 3; i++)
        {
            var wv = GetWebViewByIndex(i);
            if (wv?.CoreWebView2 == null) continue;

            // Only currently active focused session outputs audio
            wv.CoreWebView2.IsMuted = (i != _currentSessionIndex);
        }
    }

    private void CycleNextSession()
    {
        var next = _currentSessionIndex + 1;
        if (next > 3) next = 1;
        SwitchToSession(next);
    }

    private WebView2 GetWebViewByIndex(int index) => index switch
    {
        1 => WebViewSession1,
        2 => WebViewSession2,
        3 => WebViewSession3,
        _ => WebViewSession1
    };

    private void UpdateSlotStatusPills()
    {
        DotSession1.Fill = _currentSessionIndex == 1 ? (Brush)FindResource("AccentGreenBrush") : (Brush)FindResource("AccentAmberBrush");
        DotSession2.Fill = _currentSessionIndex == 2 ? (Brush)FindResource("AccentGreenBrush") : (Brush)FindResource("AccentAmberBrush");
        DotSession3.Fill = _currentSessionIndex == 3 ? (Brush)FindResource("AccentGreenBrush") : (Brush)FindResource("TextSecondaryBrush");
    }

    private void UpdateLabelsFromConfig()
    {
        LabelSession1.Text = _config.Sessions.FirstOrDefault(s => s.Index == 1)?.Name ?? "Session 1";
        LabelSession2.Text = _config.Sessions.FirstOrDefault(s => s.Index == 2)?.Name ?? "Session 2";
        LabelSession3.Text = _config.Sessions.FirstOrDefault(s => s.Index == 3)?.Name ?? "Session 3";
    }

    private void UpdateSessionName(int index, string name)
    {
        var session = _config.Sessions.FirstOrDefault(s => s.Index == index);
        if (session != null)
        {
            session.Name = name;
            _config.Save();
            UpdateLabelsFromConfig();
        }
    }

    #region Precision Touchpad Gestures

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point pos = e.GetPosition(this);
        // Only trigger if cursor is within top 120px gesture strip
        if (pos.Y > 120) return;

        if (Keyboard.Modifiers == ModifierKeys.Shift && Math.Abs(e.Delta) >= 400)
        {
            if ((DateTime.Now - _lastSwipeTime).TotalMilliseconds > 600)
            {
                _lastSwipeTime = DateTime.Now;
                if (e.Delta < 0) CycleNextSession();
                else SwitchToSession(_currentSessionIndex == 1 ? 3 : _currentSessionIndex - 1);
                e.Handled = true;
            }
        }
    }

    #endregion

    #region Cockpit Split & Relative Mouse Modes

    public async void ToggleCockpitMode()
    {
        _isCockpitMode = !_isCockpitMode;

        if (_isCockpitMode)
        {
            ColDivider.Width = new GridLength(1);
            ColRight.Width = new GridLength(1, GridUnitType.Star);
            SplitDivider.Visibility = Visibility.Visible;
            TxtSplitMode.Foreground = (Brush)FindResource("AccentGreenBrush");

            // Left Column = Current Session
            Grid.SetColumn(GetWebViewByIndex(_currentSessionIndex), 0);
            GetWebViewByIndex(_currentSessionIndex).Visibility = Visibility.Visible;

            // Right Column = Secondary Session
            _cockpitSecondaryIndex = _currentSessionIndex == 1 ? 2 : 1;
            var secondaryView = GetWebViewByIndex(_cockpitSecondaryIndex);
            Grid.SetColumn(secondaryView, 2);
            secondaryView.Visibility = Visibility.Visible;

            if (secondaryView.CoreWebView2 != null)
            {
                // Secondary view in low memory target to preserve battery
                secondaryView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await secondaryView.CoreWebView2.ExecuteScriptAsync("window.__crdResumeSession && window.__crdResumeSession();");
                await secondaryView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
            }

            var primaryView = GetWebViewByIndex(_currentSessionIndex);
            if (primaryView.CoreWebView2 != null)
            {
                await primaryView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
            }
        }
        else
        {
            ColDivider.Width = new GridLength(0);
            ColRight.Width = new GridLength(0);
            SplitDivider.Visibility = Visibility.Collapsed;
            TxtSplitMode.Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 221));

            for (int i = 1; i <= 3; i++)
            {
                var wv = GetWebViewByIndex(i);
                Grid.SetColumn(wv, 0);
                wv.Visibility = (i == _currentSessionIndex) ? Visibility.Visible : Visibility.Collapsed;
            }

            var primaryView = GetWebViewByIndex(_currentSessionIndex);
            if (primaryView.CoreWebView2 != null)
            {
                primaryView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                await primaryView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
            }
        }

        UpdateAudioMuteMatrix();
    }

    private void BtnSplitMode_Click(object sender, RoutedEventArgs e) => ToggleCockpitMode();

    public async void ToggleRelativeMouseMode()
    {
        _isRelativeMouseActive = !_isRelativeMouseActive;
        var wv = GetWebViewByIndex(_currentSessionIndex);

        if (_isRelativeMouseActive)
        {
            TxtRelativeMouse.Foreground = (Brush)FindResource("AccentGreenBrush");
            if (wv?.CoreWebView2 != null)
            {
                // Dispatch synthetic click to ensure user gesture context, then request PointerLock
                string cdpClick = "{\"type\":\"mousePressed\",\"x\":10,\"y\":10,\"button\":\"left\",\"clickCount\":1}";
                await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", cdpClick);
                cdpClick = "{\"type\":\"mouseReleased\",\"x\":10,\"y\":10,\"button\":\"left\",\"clickCount\":1}";
                await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", cdpClick);

                await wv.CoreWebView2.ExecuteScriptAsync("window.__crdEnterPointerLock && window.__crdEnterPointerLock();");
            }

            // Lock cursor within client window
            var p1 = PointToScreen(new Point(0, 0));
            var p2 = PointToScreen(new Point(ActualWidth, ActualHeight));
            RECT rect = new RECT { Left = (int)p1.X, Top = (int)p1.Y, Right = (int)p2.X, Bottom = (int)p2.Y };
            ClipCursor(ref rect);
        }
        else
        {
            ReleaseCursor();
        }
    }

    public async void ReleaseCursor()
    {
        _isRelativeMouseActive = false;
        TxtRelativeMouse.Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 221));
        ClipCursor(IntPtr.Zero);

        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            await wv.CoreWebView2.ExecuteScriptAsync("window.__crdExitPointerLock && window.__crdExitPointerLock();");
        }
        ShowNotch();
    }

    private void BtnRelativeMouse_Click(object sender, RoutedEventArgs e) => ToggleRelativeMouseMode();

    #endregion

    #region Thumbnail Peek Hover

    private void SessionPill_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int idx))
        {
            if (idx != _currentSessionIndex && _sessionThumbnails.TryGetValue(idx, out var thumb))
            {
                ImgThumbnailPeek.Source = thumb;
                var sessionName = _config.Sessions.FirstOrDefault(s => s.Index == idx)?.Name ?? $"Session {idx}";
                TxtThumbnailStatus.Text = $"{sessionName} • Standby";
                ThumbnailPeekPopup.IsOpen = true;
            }
        }
    }

    private void SessionPill_MouseLeave(object sender, MouseEventArgs e)
    {
        ThumbnailPeekPopup.IsOpen = false;
    }

    #endregion

    #region QoL Controls: Display Mode & System Keys

    public async void CycleDisplayMode()
    {
        _displayModeIndex = (_displayModeIndex + 1) % _displayModes.Length;
        var mode = _displayModes[_displayModeIndex];

        TxtDisplayMode.Text = mode switch
        {
            "contain" => "⛶ Fit",
            "fill" => "↔ Stretch",
            "cover" => "🗖 Zoom",
            _ => "⛶ Fit"
        };

        var currentView = GetWebViewByIndex(_currentSessionIndex);
        if (currentView?.CoreWebView2 != null)
        {
            await currentView.CoreWebView2.ExecuteScriptAsync($"window.__crdScreenFit && window.__crdScreenFit.setMode('{mode}');");
        }
    }

    private void BtnDisplayMode_Click(object sender, RoutedEventArgs e) => CycleDisplayMode();

    private void BtnSystemKeys_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = !SystemKeysPopup.IsOpen;
    }

    private async void BtnSendCtrlAltDel_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            // First try CRD internal button
            await wv.CoreWebView2.ExecuteScriptAsync("window.__crdSendCtrlAltDel && window.__crdSendCtrlAltDel();");
            // Also send CDP key chord with guaranteed modifier release
            await SendCdpKeyChordAsync(wv, 0x2E, "Delete", "Delete", 3); // 1=Alt, 2=Ctrl -> 3
        }
    }

    private async void BtnSendWinL_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            await wv.CoreWebView2.ExecuteScriptAsync("window.__crdSendLockWorkstation && window.__crdSendLockWorkstation();");
        }
    }

    private async void BtnSendWinD_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv != null)
        {
            // Inject Win+D via CDP with guaranteed release block
            await SendCdpKeyChordAsync(wv, 68, "d", "KeyD", 4); // 4 = Meta/Win
        }
    }

    private async void BtnSendTaskMgr_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv != null)
        {
            // Inject Ctrl+Shift+Esc via CDP (modifiers: 2=Ctrl, 8=Shift -> 10)
            await SendCdpKeyChordAsync(wv, 27, "Escape", "Escape", 10);
        }
    }

    private async Task SendCdpKeyChordAsync(WebView2 wv, int virtualKeyCode, string key, string code, int modifiers)
    {
        if (wv?.CoreWebView2 == null) return;
        try
        {
            await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", 
                $"{{\"type\":\"rawKeyDown\",\"windowsVirtualKeyCode\":{virtualKeyCode},\"key\":\"{key}\",\"code\":\"{code}\",\"modifiers\":{modifiers}}}");
            await Task.Delay(40);
        }
        finally
        {
            // Unconditionally dispatch keyUp and flush modifiers
            await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", 
                $"{{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":{virtualKeyCode},\"key\":\"{key}\",\"code\":\"{code}\",\"modifiers\":0}}");
        }
    }

    private void BtnReleaseMouse_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        ReleaseCursor();
    }

    #endregion

    #region Top Notch Hover & Animation

    private void TopHoverTrigger_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDebounceTimer.Stop();
        _dwellTimer.Start();
    }

    private void TopHoverTrigger_MouseLeave(object sender, MouseEventArgs e)
    {
        _dwellTimer.Stop();
    }

    private void DwellTimer_Tick(object? sender, EventArgs e)
    {
        _dwellTimer.Stop();
        ShowNotch();
    }

    private void TopNotch_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseDebounceTimer.Stop();
    }

    private void TopNotch_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isPinned)
        {
            _collapseDebounceTimer.Start();
        }
    }

    private void CollapseDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _collapseDebounceTimer.Stop();
        HideNotch();
    }

    private void ShowNotch()
    {
        if (_isNotchVisible) return;
        _isNotchVisible = true;

        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        NotchTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void HideNotch()
    {
        if (!_isNotchVisible) return;
        _isNotchVisible = false;

        var anim = new DoubleAnimation(-46, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        NotchTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void ToggleNotch()
    {
        if (_isNotchVisible) HideNotch();
        else ShowNotch();
    }

    #endregion

    #region Hotkeys & Input

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (isCtrl && isAlt)
        {
            switch (e.Key)
            {
                case Key.D1:
                case Key.NumPad1:
                    SwitchToSession(1);
                    e.Handled = true;
                    break;
                case Key.D2:
                case Key.NumPad2:
                    SwitchToSession(2);
                    e.Handled = true;
                    break;
                case Key.D3:
                case Key.NumPad3:
                    SwitchToSession(3);
                    e.Handled = true;
                    break;
                case Key.Tab:
                    CycleNextSession();
                    e.Handled = true;
                    break;
                case Key.S:
                    ToggleNotch();
                    e.Handled = true;
                    break;
                case Key.D:
                    CycleDisplayMode();
                    e.Handled = true;
                    break;
                case Key.C:
                    ToggleCockpitMode();
                    e.Handled = true;
                    break;
                case Key.M:
                    ToggleRelativeMouseMode();
                    e.Handled = true;
                    break;
                case Key.Home:
                    ReleaseCursor();
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.F11)
        {
            BtnFullscreen_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    #endregion

    #region Button Handlers & Settings

    private void BtnSession1_Click(object sender, RoutedEventArgs e) => SwitchToSession(1);
    private void BtnSession2_Click(object sender, RoutedEventArgs e) => SwitchToSession(2);
    private void BtnSession3_Click(object sender, RoutedEventArgs e) => SwitchToSession(3);

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        TxtPin.Text = _isPinned ? "📌" : "▲";
        if (!_isPinned) HideNotch();
        else ShowNotch();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        TxtName1.Text = _config.Sessions.FirstOrDefault(s => s.Index == 1)?.Name ?? "Session 1";
        TxtUrl1.Text = _config.Sessions.FirstOrDefault(s => s.Index == 1)?.Url ?? "";
        TxtName2.Text = _config.Sessions.FirstOrDefault(s => s.Index == 2)?.Name ?? "Session 2";
        TxtUrl2.Text = _config.Sessions.FirstOrDefault(s => s.Index == 2)?.Url ?? "";
        TxtName3.Text = _config.Sessions.FirstOrDefault(s => s.Index == 3)?.Name ?? "Session 3";
        TxtUrl3.Text = _config.Sessions.FirstOrDefault(s => s.Index == 3)?.Url ?? "";

        RadioUltraWarm.IsChecked = _config.EfficiencyMode == WarmMode.UltraWarm;
        RadioEcoWarm.IsChecked = _config.EfficiencyMode == WarmMode.EcoWarm;
        RadioLiveHot.IsChecked = _config.EfficiencyMode == WarmMode.LiveHot;

        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void BtnSettingsSave_Click(object sender, RoutedEventArgs e)
    {
        _config.Sessions[0].Name = TxtName1.Text;
        _config.Sessions[0].Url = TxtUrl1.Text;
        _config.Sessions[1].Name = TxtName2.Text;
        _config.Sessions[1].Url = TxtUrl2.Text;
        _config.Sessions[2].Name = TxtName3.Text;
        _config.Sessions[2].Url = TxtUrl3.Text;

        if (RadioUltraWarm.IsChecked == true) _config.EfficiencyMode = WarmMode.UltraWarm;
        else if (RadioEcoWarm.IsChecked == true) _config.EfficiencyMode = WarmMode.EcoWarm;
        else if (RadioLiveHot.IsChecked == true) _config.EfficiencyMode = WarmMode.LiveHot;

        _config.Save();
        UpdateLabelsFromConfig();

        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnSettingsCancel_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized && WindowStyle == WindowStyle.None)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
        }
        else
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion
}
