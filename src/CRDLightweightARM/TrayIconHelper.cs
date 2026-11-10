using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CRDLightweightARM;

/// <summary>Minimal Shell_NotifyIcon wrapper (WPF has no tray icon control).</summary>
public sealed class TrayIconHelper : IDisposable
{
    private const int WM_TRAYICON = 0x0400 + 1024;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIIF_INFO = 0x00000001;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public event Action? LeftClick;
    public event Action? RightClick;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _ownsIcon;
    private bool _isCreated;
    private uint _wmTaskbarCreated;
    private string _currentTip = "Multi-Instance CRD";

    public void Initialize(Window window, string tooltip)
    {
        _currentTip = tooltip;
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // The app icon is a Win32 resource in both the managed .dll and the .exe (ApplicationIcon).
        // Prefer the .dll: when started as `dotnet CRDLightweightARM.dll` (Run-CRD.cmd) the process is dotnet.exe.
        // Single-file builds have no assembly location, so they fall back to the .exe.
        try
        {
            var iconSource = typeof(TrayIconHelper).Assembly.Location;
            if (string.IsNullOrEmpty(iconSource) || !File.Exists(iconSource)) iconSource = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(iconSource) && File.Exists(iconSource))
            {
                ExtractIconEx(iconSource, 0, out var large, out _hIcon, 1);
                if (large != IntPtr.Zero) DestroyIcon(large);
                _ownsIcon = _hIcon != IntPtr.Zero;
            }
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION (shared, not destroyed)
            }
        }
        catch
        {
            _hIcon = IntPtr.Zero;
        }

        CreateIcon();
    }

    public void HandleHwndMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = lParam.ToInt32();
            if (mouseMsg == WM_LBUTTONUP) LeftClick?.Invoke();
            else if (mouseMsg == WM_RBUTTONUP) RightClick?.Invoke();
        }
        else if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated)
        {
            // Explorer restarted: the icon has to be added again.
            CreateIcon();
        }
    }

    public void UpdateTooltip(string tip)
    {
        _currentTip = tip.Length > 127 ? tip[..127] : tip;
        if (!_isCreated) return;

        var nid = CreateNotifyData();
        nid.uFlags = NIF_TIP;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public void ShowNotification(string title, string text)
    {
        if (!_isCreated) return;

        var nid = CreateNotifyData();
        nid.uFlags = NIF_INFO;
        nid.szInfoTitle = title;
        nid.szInfo = text;
        nid.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private void CreateIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        var nid = CreateNotifyData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = _hIcon;
        _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private NOTIFYICONDATA CreateNotifyData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1001,
        szTip = _currentTip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    public void Dispose()
    {
        if (_isCreated)
        {
            var nid = CreateNotifyData();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isCreated = false;
        }
        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
        }
        _hIcon = IntPtr.Zero;
    }
}
