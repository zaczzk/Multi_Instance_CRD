using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

namespace CRDLightweightARM.Hardware;

/// <summary>
/// High-performance hardware-level Win32 WM_INPUT Raw Input mouse engine optimized for Windows 11 ARM64.
/// Captures un-accelerated physical mouse deltas via RegisterRawInputDevices and 64-bit aligned RAWINPUT parsing.
/// Designed for 3D CAD, Blender, Unreal Engine, and gaming viewports on the native 1920x1200 60Hz display,
/// providing continuous infinite mouse movement without hitting display boundaries.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RawInputMouseEngine : IDisposable
{
    // Raw Input Constants
    public const int WM_INPUT = 0x00FF;
    public const uint RID_INPUT = 0x10000003;
    public const uint RID_HEADER = 0x10000005;

    public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;

    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RIDEV_CAPTUREMOUSE = 0x00000200;
    public const uint RIDEV_REMOVE = 0x00000001;
    public const uint RIDEV_NOLEGACY = 0x00000030;

    public const uint RIM_TYPEMOUSE = 0;
    public const ushort MOUSE_MOVE_RELATIVE = 0x00;
    public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;
    public const ushort MOUSE_VIRTUAL_DESKTOP = 0x02;

    // Mouse Button Flags
    public const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    public const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    public const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    public const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    public const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    public const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    public const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
    public const ushort RI_MOUSE_BUTTON_4_UP = 0x0080;
    public const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100;
    public const ushort RI_MOUSE_BUTTON_5_UP = 0x0200;
    public const ushort RI_MOUSE_WHEEL = 0x0400;
    public const ushort RI_MOUSE_HWHEEL = 0x0800;

    // 64-bit Win32 RAWINPUT Layout Constants
    // Header: dwType (4) + dwSize (4) + hDevice (8) + wParam (8) = 24 bytes
    private const uint RAWINPUTHEADER_SIZE = 24;
    // Buffer allocation size (Header 24 + RAWMOUSE 24 = 48 bytes; 64 bytes allocated for alignment safety)
    private const int RAW_BUFFER_CAPACITY = 64;

    private readonly IntPtr _pRawBuffer;
    private HwndSource? _attachedHwndSource;
    private bool _isDisposed = false;
    private int _prevAbsoluteX = 0;
    private int _prevAbsoluteY = 0;
    private bool _hasPrevAbsolute = false;

    /// <summary>
    /// Event fired whenever a raw mouse delta packet is processed while Relative Mode is active.
    /// Signature: (int deltaX, int deltaY, ushort buttonFlags, short wheelDelta)
    /// </summary>
    public event Action<int, int, ushort, short>? RawMouseDelta;

    /// <summary>
    /// Optional direct fast callback delegate (can be set directly to avoid multicast event overhead).
    /// </summary>
    public Action<int, int, ushort, short>? RawMouseDeltaCallback { get; set; }

    /// <summary>
    /// Gets or sets whether relative mouse mode (lock mode) is active.
    /// When true, physical mouse deltas are dispatched to the callback and cursor is re-centered.
    /// </summary>
    public bool IsRelativeModeActive { get; set; } = false;

    /// <summary>
    /// Gets or sets whether the cursor should automatically re-center to window middle on movement,
    /// preventing CAD/Blender/gaming viewports from hitting the edge of the 1920x1200 screen.
    /// </summary>
    public bool RecenterCursorEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the cursor should also be confined (clipped) to the window rectangle.
    /// </summary>
    public bool ClipCursorToWindow { get; set; } = false;

    public bool IsRegistered { get; private set; } = false;
    public IntPtr TargetHwnd { get; private set; } = IntPtr.Zero;

    // Telemetry & diagnostics
    public long TotalPacketsCaptured { get; private set; } = 0;
    public long TotalAccumulatedDeltaX { get; private set; } = 0;
    public long TotalAccumulatedDeltaY { get; private set; } = 0;

    public RawInputMouseEngine()
    {
        // Allocate zero-garbage pinned native memory buffer for high-frequency WM_INPUT polling
        _pRawBuffer = Marshal.AllocHGlobal(RAW_BUFFER_CAPACITY);
    }

    /// <summary>
    /// Registers raw input for mouse devices targeting the specified window handle.
    /// </summary>
    /// <param name="hwndTarget">Target window HWND.</param>
    /// <param name="enableInputSink">If true, captures raw input even when the client window doesn't have focus.</param>
    /// <returns>True if registration succeeded.</returns>
    public bool Register(IntPtr hwndTarget, bool enableInputSink = true)
    {
        TargetHwnd = hwndTarget;

        var rid = new RAWINPUTDEVICE[1];
        rid[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
        rid[0].usUsage = HID_USAGE_GENERIC_MOUSE;
        rid[0].dwFlags = (enableInputSink && hwndTarget != IntPtr.Zero) ? RIDEV_INPUTSINK : 0;
        rid[0].hwndTarget = hwndTarget;

        bool success = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        IsRegistered = success;
        return success;
    }

    /// <summary>
    /// Unregisters the raw mouse input device.
    /// </summary>
    public bool Unregister()
    {
        var rid = new RAWINPUTDEVICE[1];
        rid[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
        rid[0].usUsage = HID_USAGE_GENERIC_MOUSE;
        rid[0].dwFlags = RIDEV_REMOVE;
        rid[0].hwndTarget = IntPtr.Zero;

        bool success = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (success)
        {
            IsRegistered = false;
        }
        return success;
    }

    /// <summary>
    /// Attaches this raw input engine directly to a WPF Window's message loop via HwndSource hook.
    /// </summary>
    public bool Attach(Window window, bool enableInputSink = true)
    {
        var helper = new WindowInteropHelper(window);
        var hwnd = helper.EnsureHandle();
        return Attach(hwnd, enableInputSink);
    }

    /// <summary>
    /// Attaches this raw input engine directly to a native HWND message loop.
    /// </summary>
    public bool Attach(IntPtr hwnd, bool enableInputSink = true)
    {
        TargetHwnd = hwnd;
        _attachedHwndSource = HwndSource.FromHwnd(hwnd);
        _attachedHwndSource?.AddHook(ProcessWindowMessage);

        return Register(hwnd, enableInputSink);
    }

    /// <summary>
    /// Detaches from the window message loop, unregisters raw input, and releases cursor clips.
    /// </summary>
    public void Detach()
    {
        SetCursorConfinement(false);
        Unregister();

        if (_attachedHwndSource != null)
        {
            try
            {
                _attachedHwndSource.RemoveHook(ProcessWindowMessage);
            }
            catch
            {
                // Best effort
            }
            _attachedHwndSource = null;
        }
        TargetHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// Win32 window message hook method (compatible with WPF HwndSourceHook).
    /// Extracts raw mouse deltas, evaluates relative lock mode, triggers callbacks, and recenters cursor.
    /// </summary>
    public IntPtr ProcessWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_isDisposed) return IntPtr.Zero;

        if (msg == WM_INPUT)
        {
            if (ProcessRawInputMessage(lParam))
            {
                if (IsRelativeModeActive)
                {
                    // Suppress further legacy mouse processing when in relative capture mode
                    handled = true;
                }
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Parses the raw input data from an HRAWINPUT handle.
    /// </summary>
    private bool ProcessRawInputMessage(IntPtr hRawInput)
    {
        if (hRawInput == IntPtr.Zero || _pRawBuffer == IntPtr.Zero) return false;

        uint dwSize = RAW_BUFFER_CAPACITY;
        uint bytesRead = GetRawInputData(
            hRawInput,
            RID_INPUT,
            _pRawBuffer,
            ref dwSize,
            RAWINPUTHEADER_SIZE);

        if (bytesRead == uint.MaxValue || bytesRead == 0)
        {
            return false;
        }

        // Check header dwType (offset 0): 0 = RIM_TYPEMOUSE
        int dwType = Marshal.ReadInt32(_pRawBuffer, 0);
        if (dwType != (int)RIM_TYPEMOUSE)
        {
            return false;
        }

        // RAWMOUSE starts immediately after RAWINPUTHEADER (offset 24 on 64-bit)
        // Offset 24: usFlags (ushort, 2 bytes)
        // Offset 26: 2 bytes alignment padding
        // Offset 28: usButtonFlags (ushort, 2 bytes)
        // Offset 30: usButtonData (ushort / short, 2 bytes)
        // Offset 32: ulRawButtons (uint, 4 bytes)
        // Offset 36: lLastX (int, 4 bytes)
        // Offset 40: lLastY (int, 4 bytes)
        // Offset 44: ulExtraInformation (uint, 4 bytes)

        ushort usFlags = (ushort)Marshal.ReadInt16(_pRawBuffer, 24);
        ushort buttonFlags = (ushort)Marshal.ReadInt16(_pRawBuffer, 28);
        short buttonData = Marshal.ReadInt16(_pRawBuffer, 30);
        int lastX = Marshal.ReadInt32(_pRawBuffer, 36);
        int lastY = Marshal.ReadInt32(_pRawBuffer, 40);

        short wheelDelta = 0;
        if ((buttonFlags & RI_MOUSE_WHEEL) != 0 || (buttonFlags & RI_MOUSE_HWHEEL) != 0)
        {
            wheelDelta = buttonData;
        }

        int deltaX = 0;
        int deltaY = 0;

        if ((usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
        {
            // Absolute coordinates (digitizer, tablet, or virtual injection)
            if (_hasPrevAbsolute)
            {
                deltaX = lastX - _prevAbsoluteX;
                deltaY = lastY - _prevAbsoluteY;
            }
            _prevAbsoluteX = lastX;
            _prevAbsoluteY = lastY;
            _hasPrevAbsolute = true;
        }
        else
        {
            // Standard relative hardware deltas
            deltaX = lastX;
            deltaY = lastY;
            _hasPrevAbsolute = false;
        }

        TotalPacketsCaptured++;
        TotalAccumulatedDeltaX += deltaX;
        TotalAccumulatedDeltaY += deltaY;

        if (IsRelativeModeActive)
        {
            // Fire high-performance callbacks
            RawMouseDelta?.Invoke(deltaX, deltaY, buttonFlags, wheelDelta);
            RawMouseDeltaCallback?.Invoke(deltaX, deltaY, buttonFlags, wheelDelta);

            // Re-center cursor inside window client rectangle to prevent boundary clipping
            if (RecenterCursorEnabled && (deltaX != 0 || deltaY != 0))
            {
                RecenterCursor();
            }

            if (ClipCursorToWindow)
            {
                SetCursorConfinement(true);
            }
        }

        return true;
    }

    /// <summary>
    /// Re-centers the mouse cursor to the geometric center of the target window.
    /// This allows infinite delta streaming for 3D modeling and gaming viewports.
    /// </summary>
    public void RecenterCursor()
    {
        if (TargetHwnd == IntPtr.Zero) return;

        if (GetClientRect(TargetHwnd, out RECT clientRect))
        {
            POINT center = new POINT
            {
                X = (clientRect.Left + clientRect.Right) / 2,
                Y = (clientRect.Top + clientRect.Bottom) / 2
            };

            if (ClientToScreen(TargetHwnd, ref center))
            {
                SetCursorPos(center.X, center.Y);
            }
        }
    }

    /// <summary>
    /// Restricts or releases cursor clipping within the target window bounds.
    /// </summary>
    public void SetCursorConfinement(bool confine)
    {
        if (confine && TargetHwnd != IntPtr.Zero)
        {
            if (GetClientRect(TargetHwnd, out RECT clientRect))
            {
                POINT p1 = new POINT { X = clientRect.Left, Y = clientRect.Top };
                POINT p2 = new POINT { X = clientRect.Right, Y = clientRect.Bottom };

                if (ClientToScreen(TargetHwnd, ref p1) && ClientToScreen(TargetHwnd, ref p2))
                {
                    RECT clipRect = new RECT
                    {
                        Left = Math.Min(p1.X, p2.X),
                        Top = Math.Min(p1.Y, p2.Y),
                        Right = Math.Max(p1.X, p2.X),
                        Bottom = Math.Max(p1.Y, p2.Y)
                    };
                    ClipCursor(ref clipRect);
                    return;
                }
            }
        }

        // Release confinement
        ClipCursor(IntPtr.Zero);
    }

    /// <summary>
    /// Resets accumulated deltas.
    /// </summary>
    public void ResetAccumulators()
    {
        TotalPacketsCaptured = 0;
        TotalAccumulatedDeltaX = 0;
        TotalAccumulatedDeltaY = 0;
        _hasPrevAbsolute = false;
    }

    #region Button Flag Query Helpers

    public static bool IsLeftDown(ushort flags) => (flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0;
    public static bool IsLeftUp(ushort flags) => (flags & RI_MOUSE_LEFT_BUTTON_UP) != 0;
    public static bool IsRightDown(ushort flags) => (flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0;
    public static bool IsRightUp(ushort flags) => (flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0;
    public static bool IsMiddleDown(ushort flags) => (flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0;
    public static bool IsMiddleUp(ushort flags) => (flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0;
    public static bool HasWheel(ushort flags) => (flags & (RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL)) != 0;

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (!_isDisposed)
        {
            Detach();

            if (_pRawBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_pRawBuffer);
            }

            _isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~RawInputMouseEngine()
    {
        Dispose();
    }

    #endregion

    #region Win32 P/Invoke & Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ClipCursor(IntPtr lpRect);

    #endregion
}
