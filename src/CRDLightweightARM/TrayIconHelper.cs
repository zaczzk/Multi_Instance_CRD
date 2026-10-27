using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CRDLightweightARM;

public class TrayIconHelper : IDisposable
{
    private const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 1024;

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
    private const int WM_LBUTTONDBLCLK = 0x0203;

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

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? DoubleClick;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _isCreated = false;
    private uint _wmTaskbarCreated = 0;
    private string _currentTip = "Chrome Remote Desktop (Lightweight)";

    public void Initialize(Window window, string tooltip)
    {
        _currentTip = tooltip;
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();

        _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        // Load default app icon or system application icon
        try
        {
            var hModule = GetModuleHandle(null);
            _hIcon = LoadIcon(hModule, (IntPtr)32512); // IDI_APPLICATION
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
            if (mouseMsg == WM_LBUTTONUP)
            {
                LeftClick?.Invoke();
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                RightClick?.Invoke();
            }
            else if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                DoubleClick?.Invoke();
            }
        }
        else if (_wmTaskbarCreated != 0 && msg == _wmTaskbarCreated)
        {
            // Explorer restarted: recreate tray icon
            CreateIcon();
        }
    }

    public void UpdateTooltip(string tip)
    {
        _currentTip = tip;
        if (!_isCreated || _hwnd == IntPtr.Zero) return;

        var nid = CreateNotifyData();
        nid.uFlags = NIF_TIP;
        nid.szTip = tip;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public void ShowNotification(string title, string text)
    {
        if (!_isCreated || _hwnd == IntPtr.Zero) return;

        var nid = CreateNotifyData();
        nid.uFlags = NIF_INFO;
        nid.szInfoTitle = title;
        nid.szInfo = text;
        nid.dwInfoFlags = NIIF_INFO;
        nid.uTimeoutOrVersion = 3000;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private void CreateIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        var nid = CreateNotifyData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAYICON;
        nid.hIcon = _hIcon;
        nid.szTip = _currentTip;

        _isCreated = Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private NOTIFYICONDATA CreateNotifyData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _hwnd,
            uID = 1001,
            szTip = _currentTip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    public void Dispose()
    {
        if (_isCreated && _hwnd != IntPtr.Zero)
        {
            var nid = CreateNotifyData();
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isCreated = false;
        }
    }
}
