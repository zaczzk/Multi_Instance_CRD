using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CRDLightweightARM.Hardware;
using CRDLightweightARM.NativeProtocol;
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

    // Owned Layered HUD Window that floats above WebView2's Win32 HwndHost (fixes WPF Airspace issue)
    private Window? _hudWindow;

    // Scoped Win32 Low-Level Keyboard Hook (prevents CRD navigator.keyboard.lock from swallowing Ctrl+Alt+*)
    private IntPtr _hKeyboardHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _keyboardHookDelegate;

    // QoL & Snapdragon Hardware Subsystems
    private readonly WindowsPowerGovernor _powerGovernor = new();
    private readonly SnapdragonOryonGovernor _oryonGovernor = SnapdragonOryonGovernor.Instance;
    private readonly RawInputMouseEngine _rawMouseEngine = new();
    private readonly TrayIconHelper _trayIcon = new();
    private readonly Dictionary<int, ImageSource> _sessionThumbnails = new();
    private int _hoveredSessionIndex = 1;
    private readonly DispatcherTimer _thumbnailCloseTimer;
    private readonly DispatcherTimer _activeThumbnailTimer;
    private int _remoteMonitorIndex = 0; // 0 = Mon 1, 1 = Mon 2, 2 = All
    private readonly string[] _displayModes = { "contain", "fill", "cover" };
    private int _displayModeIndex = 0; // 0 = Fit, 1 = Stretch, 2 = Zoom
    private bool _isCockpitMode = false;
    private int _cockpitSecondaryIndex = 2;
    private bool _isRelativeMouseActive = false;
    private DateTime _lastSwipeTime = DateTime.MinValue;
    private DateTime _lastRawDeltaDispatch = DateTime.MinValue;
    private int _accumulatedRawDx = 0;
    private int _accumulatedRawDy = 0;
    private bool _isExplicitExit = false;
    private HwndSource? _hwndSource;
    private PhysicalDisplayTopology _displayTopology;

    private const int HOTKEY_SUMMON_ID = 9001;
    private const int HOTKEY_SUMMON_FALLBACK_ID = 9002;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_R = 0x52;
    private const uint VK_F12 = 0x7B;
    private const int WM_HOTKEY = 0x0312;

    // Win32 Low-Level Keyboard Hook Constants
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_SHIFT = 0x10;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
        _mediatorScript = LoadMediatorScript();

        _dwellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_config.NotchDwellMs) };
        _dwellTimer.Tick += DwellTimer_Tick;

        _collapseDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _collapseDebounceTimer.Tick += CollapseDebounceTimer_Tick;

        _thumbnailCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _thumbnailCloseTimer.Tick += (s, e) =>
        {
            _thumbnailCloseTimer.Stop();
            ThumbnailPeekPopup.IsOpen = false;
        };

        _activeThumbnailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _activeThumbnailTimer.Tick += async (s, e) =>
        {
            var wv = GetWebViewByIndex(_currentSessionIndex);
            if (wv?.CoreWebView2 != null && !_isCockpitMode && Visibility == Visibility.Visible)
            {
                await CaptureSessionThumbnailAsync(_currentSessionIndex, wv);
            }
        };
        _activeThumbnailTimer.Start();

        Loaded += MainWindow_Loaded;
        LocationChanged += (_, _) => SyncHudWindowBounds();
        SizeChanged += (_, _) => SyncHudWindowBounds();
        StateChanged += (_, _) => SyncHudWindowBounds();
        DpiChanged += (_, _) => SyncHudWindowBounds();
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Loads crd-mediator.js from EmbeddedResource first (guaranteeing single-file standalone .exe works),
    /// with external file override support.
    /// </summary>
    private static string LoadMediatorScript()
    {
        try
        {
            var mediatorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crd-mediator.js");
            if (File.Exists(mediatorPath))
            {
                return File.ReadAllText(mediatorPath);
            }
        }
        catch {}

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("crd-mediator.js", StringComparison.OrdinalIgnoreCase));
            if (resourceName != null)
            {
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }
        }
        catch {}

        return string.Empty;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 1. Pin UI/Input/SwapChain thread to Snapdragon Oryon performance cores via MMCSS ("Games")
        _oryonGovernor.PromoteMainThreadForLowLatency();

        var handle = new WindowInteropHelper(this).EnsureHandle();
        _displayTopology = DisplayTopologyDetector.DetectTopology(handle);

        // 2. Mount HudOverlayRoot inside an Owned Layered Window so WebView2's Win32 HWND
        //    can NEVER cover the Top Notch Pill, Hover Trigger, Popups, or Settings Modal!
        InitializeHudOverlayWindow();

        // Update Top Notch & Settings Banners with real hardware probe metrics
        TxtNativeRes.Text = _displayTopology.AspectRatio;
        BtnNativeRes.ToolTip = $"Negotiate Native {_displayTopology.PhysicalWidth}×{_displayTopology.PhysicalHeight} ({_displayTopology.AspectRatio} @ {_displayTopology.DpiX} DPI) via VideoLayout Protobuf (Ctrl+Alt+N)";
        TxtHardwareBanner.Text = _oryonGovernor.HardwareBadge;
        TxtDisplayBanner.Text = $"Native Panel: {_displayTopology.PhysicalWidth}×{_displayTopology.PhysicalHeight} ({_displayTopology.AspectRatio}) @ {_displayTopology.RefreshRateHz}Hz • {_displayTopology.DpiX} DPI • MMCSS High-Priority + EcoQoS Active";

        // 3. Initialize Power Governor, Win32 WM_INPUT Raw Mouse Engine, & Low-Level Keyboard Hook
        _powerGovernor.Initialize(this);
        _powerGovernor.PowerSourceChanged += PowerGovernor_PowerSourceChanged;

        _rawMouseEngine.RawMouseDeltaCallback = OnRawHardwareMouseDelta;
        _rawMouseEngine.Attach(handle, enableInputSink: false);

        InstallLowLevelKeyboardHook();

        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(HwndMessageHook);

        _trayIcon.Initialize(this, $"CRD ({_oryonGovernor.HardwareBadge})");
        _trayIcon.LeftClick += ToggleWindowVisibility;
        _trayIcon.RightClick += ShowTrayContextMenu;
        _trayIcon.DoubleClick += ToggleWindowVisibility;

        if (_config.GlobalHotkeyEnabled)
        {
            bool ok = RegisterHotKey(handle, HOTKEY_SUMMON_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_R);
            if (!ok)
            {
                RegisterHotKey(handle, HOTKEY_SUMMON_FALLBACK_ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_F12);
            }
        }

        UpdateLabelsFromConfig();
        await InitializeWebViewEnvironmentAsync();
        await InitializeSessionAsync(1, WebViewSession1);
        await InitializeSessionAsync(2, WebViewSession2);
        await InitializeSessionAsync(3, WebViewSession3);

        SwitchToSession(1);
    }

    #region Owned Layered HUD Window (Solves WPF WebView2 HwndHost Airspace)

    private void InitializeHudOverlayWindow()
    {
        if (_hudWindow != null) return;

        // Detach HudOverlayRoot from MainWindow's visual tree and host it in a transparent Owned Window
        RootGrid.Children.Remove(HudOverlayRoot);

        _hudWindow = new Window
        {
            Title = "CRDLightweightARM_HudOverlay",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = null, // Alpha=0 pixels pass mouse clicks straight through to WebView2!
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Owner = this,
            Content = HudOverlayRoot
        };

        _hudWindow.KeyDown += Window_KeyDown;
        _hudWindow.PreviewMouseWheel += Window_PreviewMouseWheel;

        SyncHudWindowBounds();
        _hudWindow.Show();
    }

    private void SyncHudWindowBounds()
    {
        if (_hudWindow == null) return;

        if (Visibility != Visibility.Visible || WindowState == WindowState.Minimized)
        {
            if (_hudWindow.Visibility == Visibility.Visible)
            {
                _hudWindow.Hide();
            }
            return;
        }

        if (_hudWindow.Visibility != Visibility.Visible)
        {
            _hudWindow.Show();
        }

        if (WindowState == WindowState.Maximized)
        {
            try
            {
                var pZero = PointToScreen(new Point(0, 0));
                var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
                if (transform.HasValue)
                {
                    var dipPoint = transform.Value.Transform(pZero);
                    _hudWindow.Left = dipPoint.X;
                    _hudWindow.Top = dipPoint.Y;
                }
                else
                {
                    _hudWindow.Left = Left;
                    _hudWindow.Top = Top;
                }
            }
            catch
            {
                _hudWindow.Left = Left;
                _hudWindow.Top = Top;
            }
            _hudWindow.Width = ActualWidth > 0 ? ActualWidth : (Width > 0 ? Width : SystemParameters.PrimaryScreenWidth);
            _hudWindow.Height = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : SystemParameters.PrimaryScreenHeight);
        }
        else
        {
            _hudWindow.Left = Left;
            _hudWindow.Top = Top;
            _hudWindow.Width = ActualWidth > 0 ? ActualWidth : Width;
            _hudWindow.Height = ActualHeight > 0 ? ActualHeight : Height;
        }
    }

    #endregion

    #region Scoped Win32 Low-Level Keyboard Hook (Prevents CRD from Hijacking Shortcuts)

    private void InstallLowLevelKeyboardHook()
    {
        if (_hKeyboardHook != IntPtr.Zero) return;
        _keyboardHookDelegate = LowLevelKeyboardCallback;
        IntPtr hMod = GetModuleHandle(null);
        _hKeyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardHookDelegate, hMod, 0);
    }

    private void UninstallLowLevelKeyboardHook()
    {
        if (_hKeyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hKeyboardHook);
            _hKeyboardHook = IntPtr.Zero;
        }
    }

    private bool IsOurWindowOrWebViewActive()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var myHandle = new WindowInteropHelper(this).Handle;
        var hudHandle = _hudWindow != null ? new WindowInteropHelper(_hudWindow).Handle : IntPtr.Zero;

        if (fg == myHandle || fg == hudHandle) return true;

        IntPtr root = GetAncestor(fg, GA_ROOT);
        IntPtr rootOwner = GetAncestor(fg, GA_ROOTOWNER);
        return root == myHandle || root == hudHandle || rootOwner == myHandle || rootOwner == hudHandle;
    }

    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            if (IsOurWindowOrWebViewActive())
            {
                var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;

                if (ctrlDown && altDown)
                {
                    switch (kb.vkCode)
                    {
                        case 0x31: // '1'
                        case 0x61: // NumPad 1
                            Dispatcher.BeginInvoke(() => SwitchToSession(1));
                            return (IntPtr)1;
                        case 0x32: // '2'
                        case 0x62: // NumPad 2
                            Dispatcher.BeginInvoke(() => SwitchToSession(2));
                            return (IntPtr)1;
                        case 0x33: // '3'
                        case 0x63: // NumPad 3
                            Dispatcher.BeginInvoke(() => SwitchToSession(3));
                            return (IntPtr)1;
                        case 0x09: // Tab
                            Dispatcher.BeginInvoke(CycleNextSession);
                            return (IntPtr)1;
                        case 0x53: // 'S' -> Toggle Notch
                            Dispatcher.BeginInvoke(ToggleNotch);
                            return (IntPtr)1;
                        case 0x44: // 'D' -> Cycle Display Mode (Fit / Stretch 16:10 / Zoom)
                            Dispatcher.BeginInvoke(CycleDisplayMode);
                            return (IntPtr)1;
                        case 0x43: // 'C' -> Toggle Cockpit 50/50 Split
                            Dispatcher.BeginInvoke(ToggleCockpitMode);
                            return (IntPtr)1;
                        case 0x4D: // 'M' -> Toggle Relative Mouse
                            Dispatcher.BeginInvoke(ToggleRelativeMouseMode);
                            return (IntPtr)1;
                        case 0x4E: // 'N' -> Sync Native 16:10 Resolution
                            Dispatcher.BeginInvoke(async () => await SyncSessionHostResolutionAsync(_currentSessionIndex));
                            return (IntPtr)1;
                        case 0x4F: // 'O' -> Cycle Remote Host Monitor (1 / 2 / All)
                            Dispatcher.BeginInvoke(CycleRemoteMonitor);
                            return (IntPtr)1;
                        case 0x24: // Home -> Release Cursor
                            Dispatcher.BeginInvoke(ReleaseCursor);
                            return (IntPtr)1;
                    }
                }
                else if (kb.vkCode == 0x7A) // F11 -> Toggle Fullscreen
                {
                    Dispatcher.BeginInvoke(() => BtnFullscreen_Click(this, new RoutedEventArgs()));
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hKeyboardHook, nCode, wParam, lParam);
    }

    #endregion

    private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_SUMMON_ID || id == HOTKEY_SUMMON_FALLBACK_ID)
            {
                ToggleWindowVisibility();
                handled = true;
            }
        }
        else
        {
            _trayIcon.HandleHwndMessage(msg, wParam, lParam);
        }
        return IntPtr.Zero;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit && _config.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
            _trayIcon.ShowNotification("CRD Standby", "Minimized to System Tray (0% Adreno GPU, EcoQoS Active). Press Ctrl+Alt+R to summon.");
        }
        else
        {
            UninstallLowLevelKeyboardHook();
            _hudWindow?.Close();
            _trayIcon.Dispose();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        ReleaseCursor();
        UninstallLowLevelKeyboardHook();
        _activeThumbnailTimer?.Stop();
        _thumbnailCloseTimer?.Stop();
        _dwellTimer?.Stop();
        _collapseDebounceTimer?.Stop();
        _rawMouseEngine.Dispose();
        _oryonGovernor.Dispose();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            UnregisterHotKey(handle, HOTKEY_SUMMON_ID);
            UnregisterHotKey(handle, HOTKEY_SUMMON_FALLBACK_ID);
        }
        _trayIcon.Dispose();
        _powerGovernor.Dispose();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_isRelativeMouseActive && !IsOurWindowOrWebViewActive())
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
                for (int i = 1; i <= 3; i++)
                {
                    if (i != _currentSessionIndex)
                    {
                        var wv = GetWebViewByIndex(i);
                        if (wv?.CoreWebView2 != null)
                        {
                            wv.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                            _oryonGovernor.ApplySessionProcessQoS(wv.CoreWebView2.BrowserProcessId, isEcoStandby: true);
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
                        bool isActive = (i == _currentSessionIndex) || (_isCockpitMode && i == _cockpitSecondaryIndex);
                        wv.CoreWebView2.MemoryUsageTargetLevel = isActive
                            ? CoreWebView2MemoryUsageTargetLevel.Normal
                            : CoreWebView2MemoryUsageTargetLevel.Low;
                        _oryonGovernor.ApplySessionProcessQoS(wv.CoreWebView2.BrowserProcessId, isEcoStandby: !isActive);
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
            "--enable-features=PlatformHEVCDecoderSupport,D3D11VideoDecoder,MediaFoundationD3D11VideoCapture,WebRtcHideLocalIpsWithMdns",
            "--enable-direct-composition-video-overlays",
            "--enable-hardware-overlays=single-fullscreen,single-on-top,underlay",
            "--enable-gpu-rasterization",
            "--enable-zero-copy",
            "--use-angle=d3d11"
        });

        var options = new CoreWebView2EnvironmentOptions(browserArgs);
        _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }

    private async Task InitializeSessionAsync(int index, WebView2 webView)
    {
        if (_webViewEnvironment == null) return;

        webView.DefaultBackgroundColor = System.Drawing.Color.Black;
        await webView.EnsureCoreWebView2Async(_webViewEnvironment);

        // Inject Warm Swap + WebRTC SCTP DataChannel Protobuf Mediator into every frame
        if (!string.IsNullOrEmpty(_mediatorScript))
        {
            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(_mediatorScript);
        }

        // Re-apply mediator script & current display mode on NavigationCompleted as well
        webView.CoreWebView2.NavigationCompleted += async (s, args) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(_mediatorScript))
                {
                    await webView.CoreWebView2.ExecuteScriptAsync(_mediatorScript);
                }
                var mode = _displayModes[_displayModeIndex];
                await webView.CoreWebView2.ExecuteScriptAsync($"window.__crdScreenFit && window.__crdScreenFit.setMode('{mode}');");
            }
            catch {}
        };

        // Keep HUD Overlay visible if CRD tries to request HTML5 Fullscreen
        webView.CoreWebView2.ContainsFullScreenElementChanged += (s, args) =>
        {
            SyncHudWindowBounds();
        };

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
                    args.State = (_currentSessionIndex == index)
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

        // 4. WebMessage bridge for DOM-level events, SCTP DataChannel Ready, & Telemetry
        webView.WebMessageReceived += async (s, args) =>
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
                else if (msg == "SYNC_NATIVE_RESOLUTION") await SyncSessionHostResolutionAsync(index);
                else if (msg == "RELEASE_CURSOR") ReleaseCursor();
                else if (msg == "DATACHANNEL_READY:control")
                {
                    if (_config.AutoSyncNativeResolution)
                    {
                        await SyncSessionHostResolutionAsync(index);
                    }
                }
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

        // Navigate to session URL
        var currentSession = _config.Sessions.FirstOrDefault(s => s.Index == index);
        var url = currentSession?.Url ?? "https://remotedesktop.google.com/access";
        webView.CoreWebView2.Navigate(url);
    }

    #region Native Chromoting Protobuf Resolution & Input Engine

    public async Task SyncSessionHostResolutionAsync(int sessionIndex, DisplayLayoutMode? overrideMode = null)
    {
        var wv = GetWebViewByIndex(sessionIndex);
        if (wv?.CoreWebView2 == null) return;

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var mode = overrideMode ?? (_isCockpitMode
                ? DisplayLayoutMode.CockpitDualSplit5050
                : DisplayLayoutMode.FullBleedNative16By10);

            var profile = DisplayTopologyDetector.GetOptimalHostResolution(mode, handle);
            var syncPayloads = ChromotingProtobuf.BuildNativeResolutionSyncMessages(
                profile.Width,
                profile.Height,
                (int)profile.Dpi,
                screenId: 0,
                targetFps: _displayTopology.RefreshRateHz > 0 ? _displayTopology.RefreshRateHz : 60,
                losslessColor: _config.LosslessColorMode
            );

            string script = $"window.__crdSetHostResolution && window.__crdSetHostResolution({syncPayloads.AlignedWidth}, {syncPayloads.AlignedHeight}, {syncPayloads.Dpi}, ['{syncPayloads.ClientResolutionBase64}', '{syncPayloads.VideoLayoutBase64}', '{syncPayloads.VideoControlBase64}']);";
            await wv.CoreWebView2.ExecuteScriptAsync(script);

            TxtNativeRes.Text = mode == DisplayLayoutMode.CockpitDualSplit5050
                ? "8:10"
                : profile.AspectRatio;
            TxtNativeRes.Foreground = new SolidColorBrush(Color.FromRgb(124, 227, 139));
        }
        catch {}
    }

    private void OnRawHardwareMouseDelta(int deltaX, int deltaY, ushort buttonFlags, short wheelDelta)
    {
        if (!_isRelativeMouseActive || (deltaX == 0 && deltaY == 0 && wheelDelta == 0)) return;

        _accumulatedRawDx += deltaX;
        _accumulatedRawDy += deltaY;

        var now = DateTime.UtcNow;
        if ((now - _lastRawDeltaDispatch).TotalMilliseconds < 8 && wheelDelta == 0) return;

        int dx = _accumulatedRawDx;
        int dy = _accumulatedRawDy;
        _accumulatedRawDx = 0;
        _accumulatedRawDy = 0;
        _lastRawDeltaDispatch = now;

        var evMsg = new EventMessage
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MouseEvent = new CRDLightweightARM.NativeProtocol.MouseEvent
            {
                DeltaX = dx,
                DeltaY = dy,
                WheelDeltaY = wheelDelta != 0 ? (float?)wheelDelta : null
            }
        };

        string protoB64 = Convert.ToBase64String(evMsg.Encode(framed: false));
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            _ = wv.CoreWebView2.ExecuteScriptAsync($"window.__crdDispatchRawMouseDelta && window.__crdDispatchRawMouseDelta({dx}, {dy}, '{protoB64}');");
        }
    }

    #endregion

    private void UpdateTelemetryBadge(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int rtt = root.GetProperty("rtt").GetInt32();
            int fps = root.GetProperty("fps").GetInt32();
            int width = root.TryGetProperty("width", out var wProp) ? wProp.GetInt32() : 0;
            int height = root.TryGetProperty("height", out var hProp) ? hProp.GetInt32() : 0;

            Dispatcher.Invoke(() =>
            {
                TxtTelemetry.Text = $"{rtt}ms {fps}f";
                if (width > 0 && height > 0)
                {
                    TxtTelemetry.ToolTip = $"WebRTC Stream: {width}×{height} @ {fps} FPS • RTT: {rtt} ms • {_oryonGovernor.HardwareBadge}";
                }

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

    public void ForceForegroundActivate()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        IntPtr foregroundHwnd = GetForegroundWindow();
        uint foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero);
        uint currentThreadId = GetCurrentThreadId();

        if (foregroundThreadId != currentThreadId && foregroundThreadId != 0)
        {
            AttachThreadInput(currentThreadId, foregroundThreadId, true);
            ShowWindow(handle, 9); // SW_RESTORE
            SetForegroundWindow(handle);
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
        else
        {
            ShowWindow(handle, 9);
            SetForegroundWindow(handle);
        }

        Activate();
        Focus();
        SyncHudWindowBounds();
    }

    public void ToggleWindowVisibility()
    {
        if (Visibility == Visibility.Visible && WindowState != WindowState.Minimized && IsOurWindowOrWebViewActive())
        {
            HideToTray();
        }
        else
        {
            RestoreFromTray();
        }
    }

    public async void HideToTray()
    {
        ReleaseCursor();
        UninstallLowLevelKeyboardHook();
        _activeThumbnailTimer?.Stop();
        _oryonGovernor.RevertMainThreadPromotion();

        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            await wv.CoreWebView2.ExecuteScriptAsync("window.__crdPauseSession && window.__crdPauseSession();");
        }

        for (int i = 1; i <= 3; i++)
        {
            var sessionWv = GetWebViewByIndex(i);
            if (sessionWv?.CoreWebView2 != null)
            {
                sessionWv.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                _oryonGovernor.ApplySessionProcessQoS(sessionWv.CoreWebView2.BrowserProcessId, isEcoStandby: true);
            }
        }

        _hudWindow?.Hide();
        Visibility = Visibility.Hidden;
        ShowInTaskbar = false;
        _trayIcon.UpdateTooltip($"CRD (EcoQoS Standby) - {GetSessionName(_currentSessionIndex)}");
    }

    public async void RestoreFromTray()
    {
        InstallLowLevelKeyboardHook();
        _oryonGovernor.PromoteMainThreadForLowLatency();
        _activeThumbnailTimer?.Start();

        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
        WindowState = WindowState.Maximized;

        ForceForegroundActivate();

        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            wv.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
            _oryonGovernor.ApplySessionProcessQoS(wv.CoreWebView2.BrowserProcessId, isEcoStandby: false);
            await wv.CoreWebView2.ExecuteScriptAsync("window.__crdResumeSession && window.__crdResumeSession();");
        }

        _trayIcon.UpdateTooltip($"CRD (Active) - {GetSessionName(_currentSessionIndex)}");
    }

    private void ShowTrayContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = (Brush)FindResource("NotchBackgroundBrush"),
            BorderBrush = (Brush)FindResource("NotchBorderBrush"),
            BorderThickness = new Thickness(1),
            Foreground = (Brush)FindResource("TextPrimaryBrush")
        };

        var headerItem = new MenuItem
        {
            Header = _oryonGovernor.HardwareBadge,
            IsEnabled = false,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            FontWeight = FontWeights.Bold
        };
        menu.Items.Add(headerItem);
        menu.Items.Add(new Separator { Background = (Brush)FindResource("NotchBorderBrush") });

        var itemShow = new MenuItem { Header = "🗖 Show / Restore Client (Ctrl+Alt+R)", Foreground = Brushes.White };
        itemShow.Click += (s, e) => RestoreFromTray();
        menu.Items.Add(itemShow);

        var itemSyncRes = new MenuItem
        {
            Header = $"📐 Sync Native {_displayTopology.PhysicalWidth}×{_displayTopology.PhysicalHeight} ({_displayTopology.AspectRatio})",
            Foreground = new SolidColorBrush(Color.FromRgb(124, 227, 139))
        };
        itemSyncRes.Click += async (s, e) =>
        {
            RestoreFromTray();
            await SyncSessionHostResolutionAsync(_currentSessionIndex);
        };
        menu.Items.Add(itemSyncRes);

        menu.Items.Add(new Separator { Background = (Brush)FindResource("NotchBorderBrush") });

        for (int i = 1; i <= 3; i++)
        {
            int slot = i;
            var name = GetSessionName(slot);
            var itemSlot = new MenuItem
            {
                Header = $"{(slot == _currentSessionIndex ? "●" : "○")} Slot {slot}: {name}",
                Foreground = (slot == _currentSessionIndex) ? (Brush)FindResource("AccentGreenBrush") : Brushes.White
            };
            itemSlot.Click += (s, e) =>
            {
                RestoreFromTray();
                SwitchToSession(slot);
            };
            menu.Items.Add(itemSlot);
        }

        menu.Items.Add(new Separator { Background = (Brush)FindResource("NotchBorderBrush") });

        var itemSettings = new MenuItem { Header = "⚙ Settings...", Foreground = Brushes.White };
        itemSettings.Click += (s, e) =>
        {
            RestoreFromTray();
            BtnSettings_Click(this, new RoutedEventArgs());
        };
        menu.Items.Add(itemSettings);

        var itemExit = new MenuItem { Header = "✕ Disconnect All & Exit", Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100)) };
        itemExit.Click += (s, e) =>
        {
            _isExplicitExit = true;
            _trayIcon.Dispose();
            Application.Current.Shutdown();
        };
        menu.Items.Add(itemExit);

        menu.IsOpen = true;
    }

    private string GetSessionName(int index)
    {
        return _config.Sessions.FirstOrDefault(s => s.Index == index)?.Name ?? $"Session {index}";
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

    public void SwitchToSession(int targetIndex)
    {
        if (targetIndex < 1 || targetIndex > 3) return;

        ThumbnailPeekPopup.IsOpen = false;
        _thumbnailCloseTimer.Stop();

        int prevIndex = _currentSessionIndex;
        var prevView = GetWebViewByIndex(prevIndex);
        var targetView = GetWebViewByIndex(targetIndex);

        _currentSessionIndex = targetIndex;
        UpdateSlotStatusPills();
        UpdateAudioMuteMatrix();

        if (_isCockpitMode)
        {
            targetView?.Focus();
            return;
        }

        // 1. Instantaneous visual swap: 0ms UI latency
        if (targetView != null)
        {
            Panel.SetZIndex(targetView, 10);
            targetView.Visibility = Visibility.Visible;
            targetView.Focus();
        }

        if (prevView != null && prevView != targetView)
        {
            Panel.SetZIndex(prevView, 0);
            prevView.Visibility = Visibility.Collapsed;
        }

        // 2. Non-blocking asynchronous background warm-swap & thumbnail capture
        _ = Task.Run(async () =>
        {
            // Capture thumbnail of the session we just left
            if (prevView != null)
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    await CaptureSessionThumbnailAsync(prevIndex, prevView);
                });
            }

            // Power-throttle previous session
            if (prevView?.CoreWebView2 != null && _config.EfficiencyMode != WarmMode.LiveHot)
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await prevView.CoreWebView2.ExecuteScriptAsync("window.__crdPauseSession && window.__crdPauseSession();");
                        if (_config.EfficiencyMode == WarmMode.EcoWarm || _powerGovernor.CurrentPowerSource == PowerSource.Battery)
                        {
                            prevView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                            _oryonGovernor.ApplySessionProcessQoS(prevView.CoreWebView2.BrowserProcessId, isEcoStandby: true);
                        }
                    }
                    catch {}
                });
            }

            // Wake & resume target session
            if (targetView?.CoreWebView2 != null)
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        targetView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                        _oryonGovernor.ApplySessionProcessQoS(targetView.CoreWebView2.BrowserProcessId, isEcoStandby: false);

                        if (_config.EfficiencyMode != WarmMode.LiveHot)
                        {
                            await targetView.CoreWebView2.ExecuteScriptAsync("window.__crdResumeSession && window.__crdResumeSession();");
                        }
                    }
                    catch {}
                });
            }
        });
    }

    private void UpdateAudioMuteMatrix()
    {
        for (int i = 1; i <= 3; i++)
        {
            var wv = GetWebViewByIndex(i);
            if (wv?.CoreWebView2 == null) continue;

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

            Grid.SetColumn(GetWebViewByIndex(_currentSessionIndex), 0);
            GetWebViewByIndex(_currentSessionIndex).Visibility = Visibility.Visible;

            _cockpitSecondaryIndex = _currentSessionIndex == 1 ? 2 : 1;
            var secondaryView = GetWebViewByIndex(_cockpitSecondaryIndex);
            Grid.SetColumn(secondaryView, 2);
            secondaryView.Visibility = Visibility.Visible;

            if (secondaryView.CoreWebView2 != null)
            {
                secondaryView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                await secondaryView.CoreWebView2.ExecuteScriptAsync("window.__crdResumeSession && window.__crdResumeSession();");
                if (_config.AutoSyncNativeResolution)
                {
                    await SyncSessionHostResolutionAsync(_cockpitSecondaryIndex, DisplayLayoutMode.CockpitDualSplit5050);
                }
                else
                {
                    await secondaryView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
                }
            }

            var primaryView = GetWebViewByIndex(_currentSessionIndex);
            if (primaryView.CoreWebView2 != null)
            {
                if (_config.AutoSyncNativeResolution)
                {
                    await SyncSessionHostResolutionAsync(_currentSessionIndex, DisplayLayoutMode.CockpitDualSplit5050);
                }
                else
                {
                    await primaryView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
                }
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
                if (_config.AutoSyncNativeResolution)
                {
                    await SyncSessionHostResolutionAsync(_currentSessionIndex, DisplayLayoutMode.FullBleedNative16By10);
                }
                else
                {
                    await primaryView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new Event('resize'));");
                }
            }
        }

        UpdateAudioMuteMatrix();
    }

    private void BtnSplitMode_Click(object sender, RoutedEventArgs e) => ToggleCockpitMode();

    public async void ToggleRelativeMouseMode()
    {
        _isRelativeMouseActive = !_isRelativeMouseActive;
        _rawMouseEngine.IsRelativeModeActive = _isRelativeMouseActive;
        var wv = GetWebViewByIndex(_currentSessionIndex);

        if (_isRelativeMouseActive)
        {
            TxtRelativeMouse.Foreground = (Brush)FindResource("AccentGreenBrush");
            if (wv?.CoreWebView2 != null)
            {
                string cdpClick = "{\"type\":\"mousePressed\",\"x\":10,\"y\":10,\"button\":\"left\",\"clickCount\":1}";
                await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", cdpClick);
                cdpClick = "{\"type\":\"mouseReleased\",\"x\":10,\"y\":10,\"button\":\"left\",\"clickCount\":1}";
                await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", cdpClick);

                await wv.CoreWebView2.ExecuteScriptAsync("window.__crdEnterPointerLock && window.__crdEnterPointerLock();");
            }

            _rawMouseEngine.SetCursorConfinement(true);
        }
        else
        {
            ReleaseCursor();
        }
    }

    public async void ReleaseCursor()
    {
        _isRelativeMouseActive = false;
        _rawMouseEngine.IsRelativeModeActive = false;
        _rawMouseEngine.SetCursorConfinement(false);
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

    #region Thumbnail Peek Hover & Direct Session Switching

    private void SessionPill_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int idx))
        {
            _thumbnailCloseTimer.Stop();
            _hoveredSessionIndex = idx;

            if (idx != _currentSessionIndex && _sessionThumbnails.TryGetValue(idx, out var thumb))
            {
                ImgThumbnailPeek.Source = thumb;
                var sessionName = _config.Sessions.FirstOrDefault(s => s.Index == idx)?.Name ?? $"Session {idx}";
                TxtThumbnailStatus.Text = $"{sessionName} • Click to Switch";
                ThumbnailPeekPopup.IsOpen = true;
            }
        }
    }

    private void SessionPill_MouseLeave(object sender, MouseEventArgs e)
    {
        _thumbnailCloseTimer.Start();
    }

    private void ThumbnailBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        _thumbnailCloseTimer.Stop();
    }

    private void ThumbnailBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        _thumbnailCloseTimer.Start();
    }

    private void ThumbnailPeek_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ThumbnailPeekPopup.IsOpen = false;
        _thumbnailCloseTimer.Stop();
        if (_hoveredSessionIndex >= 1 && _hoveredSessionIndex <= 3)
        {
            SwitchToSession(_hoveredSessionIndex);
            e.Handled = true;
        }
    }

    private void BtnSession1_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SwitchToSession(1);
        e.Handled = true;
    }

    private void BtnSession2_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SwitchToSession(2);
        e.Handled = true;
    }

    private void BtnSession3_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        SwitchToSession(3);
        e.Handled = true;
    }

    #endregion

    #region QoL Controls: Display Mode, Native 16:10 Sync & System Keys

    public async void CycleRemoteMonitor()
    {
        _remoteMonitorIndex = (_remoteMonitorIndex + 1) % 3;
        string displayId = _remoteMonitorIndex switch
        {
            0 => "0",
            1 => "1",
            _ => "all"
        };

        TxtSwitchMonitor.Text = _remoteMonitorIndex switch
        {
            0 => "🖥 Mon 1",
            1 => "🖥 Mon 2",
            _ => "🖥 All"
        };
        TxtSwitchMonitor.Foreground = new SolidColorBrush(Color.FromRgb(124, 227, 139));

        var (_, b64) = ChromotingProtobuf.BuildSelectDisplayMessage(displayId);
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            await wv.CoreWebView2.ExecuteScriptAsync($"window.__crdSelectDisplay && window.__crdSelectDisplay('{displayId}', '{b64}');");
        }
    }

    private void BtnSwitchMonitor_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        CycleRemoteMonitor();
    }

    public async void CycleDisplayMode()
    {
        _displayModeIndex = (_displayModeIndex + 1) % _displayModes.Length;
        var mode = _displayModes[_displayModeIndex];

        TxtDisplayMode.Text = mode switch
        {
            "contain" => "⛶ Fit to Screen",
            "fill" => "↔ Stretch",
            "cover" => "🗖 Zoom",
            _ => "⛶ Fit to Screen"
        };

        var currentView = GetWebViewByIndex(_currentSessionIndex);
        if (currentView?.CoreWebView2 != null)
        {
            await currentView.CoreWebView2.ExecuteScriptAsync($"window.__crdScreenFit && window.__crdScreenFit.setMode('{mode}');");
        }
    }

    private void BtnDisplayMode_Click(object sender, RoutedEventArgs e) => CycleDisplayMode();

    private async void BtnNativeRes_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        await SyncSessionHostResolutionAsync(_currentSessionIndex);
        if (_isCockpitMode)
        {
            await SyncSessionHostResolutionAsync(_cockpitSecondaryIndex);
        }
    }

    private void BtnSystemKeys_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = !SystemKeysPopup.IsOpen;
    }

    private async Task DispatchUsbHidChordProtoAsync(WebView2 wv, UsbHidKeycodeMapper.SystemChord chord)
    {
        if (wv?.CoreWebView2 == null) return;
        try
        {
            var events = UsbHidKeycodeMapper.BuildChordEventMessages(chord, framed: false);
            var b64List = events.Select(ev => $"'{Convert.ToBase64String(ev.Encode(framed: false))}'");
            string jsArray = "[" + string.Join(",", b64List) + "]";
            await wv.CoreWebView2.ExecuteScriptAsync($"window.__crdInjectEventProtoBase64 && window.__crdInjectEventProtoBase64({jsArray});");
        }
        catch {}
    }

    private async void BtnSendCtrlAltDel_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            await DispatchUsbHidChordProtoAsync(wv, UsbHidKeycodeMapper.SystemChord.CtrlAltDel);
            await wv.CoreWebView2.ExecuteScriptAsync("window.__crdSendCtrlAltDel && window.__crdSendCtrlAltDel();");
            await SendCdpKeyChordAsync(wv, 0x2E, "Delete", "Delete", 3);
        }
    }

    private async void BtnSendWinL_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv?.CoreWebView2 != null)
        {
            await DispatchUsbHidChordProtoAsync(wv, UsbHidKeycodeMapper.SystemChord.WinL);
            await wv.CoreWebView2.ExecuteScriptAsync("window.__crdSendLockWorkstation && window.__crdSendLockWorkstation();");
        }
    }

    private async void BtnSendWinD_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv != null)
        {
            await DispatchUsbHidChordProtoAsync(wv, UsbHidKeycodeMapper.SystemChord.WinD);
            await SendCdpKeyChordAsync(wv, 68, "d", "KeyD", 4);
        }
    }

    private async void BtnSendTaskMgr_Click(object sender, RoutedEventArgs e)
    {
        SystemKeysPopup.IsOpen = false;
        var wv = GetWebViewByIndex(_currentSessionIndex);
        if (wv != null)
        {
            await DispatchUsbHidChordProtoAsync(wv, UsbHidKeycodeMapper.SystemChord.CtrlShiftEsc);
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

    private void TopNotch_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        TopHoverTrigger.Width = Math.Max(420, e.NewSize.Width + 30);
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

    private async void Window_KeyDown(object sender, KeyEventArgs e)
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
                case Key.N:
                    await SyncSessionHostResolutionAsync(_currentSessionIndex);
                    e.Handled = true;
                    break;
                case Key.O:
                    CycleRemoteMonitor();
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

        ChkAutoSyncResolution.IsChecked = _config.AutoSyncNativeResolution;
        ChkLosslessColor.IsChecked = _config.LosslessColorMode;

        SettingsOverlay.Visibility = Visibility.Visible;
        if (_hudWindow != null)
        {
            _hudWindow.Focusable = true;
            _hudWindow.Activate();
        }
    }

    private async void BtnSettingsSave_Click(object sender, RoutedEventArgs e)
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

        _config.AutoSyncNativeResolution = ChkAutoSyncResolution.IsChecked == true;
        _config.LosslessColorMode = ChkLosslessColor.IsChecked == true;

        _config.Save();
        UpdateLabelsFromConfig();

        SettingsOverlay.Visibility = Visibility.Collapsed;
        Activate();

        if (_config.AutoSyncNativeResolution)
        {
            await SyncSessionHostResolutionAsync(_currentSessionIndex);
        }
    }

    private void BtnSettingsCancel_Click(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        Activate();
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
        SyncHudWindowBounds();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion
}
