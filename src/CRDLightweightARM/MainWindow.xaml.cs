using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private bool _isNotchVisible = false;
    private readonly string _mediatorScript;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();

        // Load the injected warm-swap mediator script
        var mediatorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crd-mediator.js");
        if (File.Exists(mediatorPath))
        {
            _mediatorScript = File.ReadAllText(mediatorPath);
        }
        else
        {
            // Inline fallback
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

        // Top notch hover dwell timer (prevents accidental drop on fling)
        _dwellTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_config.NotchDwellMs)
        };
        _dwellTimer.Tick += DwellTimer_Tick;

        // Auto-collapse debounce timer
        _collapseDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _collapseDebounceTimer.Tick += CollapseDebounceTimer_Tick;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLabelsFromConfig();
        await InitializeWebViewEnvironmentAsync();
        await InitializeSessionAsync(1, WebViewSession1);
        await InitializeSessionAsync(2, WebViewSession2);
        await InitializeSessionAsync(3, WebViewSession3);

        SwitchToSession(1);
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

        // 1. WebMessage bridge for DOM-level shortcut events
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
            }
            catch
            {
                // Ignore parse errors
            }
        };

        // 2. WPF PreviewKeyDown fallback
        webView.PreviewKeyDown += (s, args) =>
        {
            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            if (isCtrl && isAlt)
            {
                if (args.Key == Key.D1 || args.Key == Key.NumPad1)
                {
                    SwitchToSession(1);
                    args.Handled = true;
                }
                else if (args.Key == Key.D2 || args.Key == Key.NumPad2)
                {
                    SwitchToSession(2);
                    args.Handled = true;
                }
                else if (args.Key == Key.D3 || args.Key == Key.NumPad3)
                {
                    SwitchToSession(3);
                    args.Handled = true;
                }
                else if (args.Key == Key.Tab)
                {
                    CycleNextSession();
                    args.Handled = true;
                }
                else if (args.Key == Key.S)
                {
                    ToggleNotch();
                    args.Handled = true;
                }
            }
        };

        // Navigate to session URL
        var session = _config.Sessions.FirstOrDefault(s => s.Index == index);
        var url = session?.Url ?? "https://remotedesktop.google.com/access";
        webView.CoreWebView2.Navigate(url);
    }

    public async void SwitchToSession(int targetIndex)
    {
        if (targetIndex < 1 || targetIndex > 3) return;

        var currentView = GetWebViewByIndex(_currentSessionIndex);
        var targetView = GetWebViewByIndex(targetIndex);

        // 1. Put current session into Warm Standby (Power-gate GPU decoder)
        if (currentView?.CoreWebView2 != null && targetIndex != _currentSessionIndex)
        {
            if (_config.EfficiencyMode != WarmMode.LiveHot)
            {
                await currentView.CoreWebView2.ExecuteScriptAsync("window.__crdPauseSession && window.__crdPauseSession();");
                if (_config.EfficiencyMode == WarmMode.EcoWarm)
                {
                    currentView.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                }
            }
            currentView.Visibility = Visibility.Collapsed;
        }

        // 2. Wake target session (<35ms instant resume)
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
        UpdateSlotStatusPills();
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
        DotSession1.Fill = _currentSessionIndex == 1 ? (System.Windows.Media.Brush)FindResource("AccentGreenBrush") : (System.Windows.Media.Brush)FindResource("AccentAmberBrush");
        DotSession2.Fill = _currentSessionIndex == 2 ? (System.Windows.Media.Brush)FindResource("AccentGreenBrush") : (System.Windows.Media.Brush)FindResource("AccentAmberBrush");
        DotSession3.Fill = _currentSessionIndex == 3 ? (System.Windows.Media.Brush)FindResource("AccentGreenBrush") : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }

    private void UpdateLabelsFromConfig()
    {
        LabelSession1.Text = _config.Sessions.FirstOrDefault(s => s.Index == 1)?.Name ?? "Session 1";
        LabelSession2.Text = _config.Sessions.FirstOrDefault(s => s.Index == 2)?.Name ?? "Session 2";
        LabelSession3.Text = _config.Sessions.FirstOrDefault(s => s.Index == 3)?.Name ?? "Session 3";
    }

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
        _collapseDebounceTimer.Start();
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

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        // Load into settings UI
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
