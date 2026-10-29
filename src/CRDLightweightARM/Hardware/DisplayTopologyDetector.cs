using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace CRDLightweightARM.Hardware;

/// <summary>
/// Display layout modes supported by the Chrome Remote Desktop Lightweight ARM client.
/// </summary>
public enum DisplayLayoutMode
{
    /// <summary>
    /// Full-Bleed Native 16:10 Mode: 1920 x 1200 @ 96 DPI (or current physical screen bounds aligned to even macroblocks).
    /// </summary>
    FullBleedNative16By10,

    /// <summary>
    /// Cockpit 50/50 Dual Split Mode: 960 x 1200 @ 96 DPI per pane (half physical width x full physical height in native 8:10).
    /// </summary>
    CockpitDualSplit5050,

    /// <summary>
    /// Quad Grid 2x2 Split Mode: 960 x 600 @ 96 DPI per quadrant.
    /// </summary>
    QuadGrid2x2
}

/// <summary>
/// Immutable snapshot of physical display metrics detected via Win32 GDI and Per-Monitor V2 DPI APIs.
/// </summary>
public readonly record struct PhysicalDisplayTopology(
    int PhysicalWidth,
    int PhysicalHeight,
    int LogicalWidth,
    int LogicalHeight,
    int RefreshRateHz,
    uint DpiX,
    uint DpiY,
    double ScaleFactor,
    string AspectRatio,
    bool Is16By10,
    bool IsSnapdragonNative1200p
);

/// <summary>
/// Target host resolution profile computed for Chrome Remote Desktop rendering pipelines.
/// </summary>
public readonly record struct HostResolutionProfile(
    DisplayLayoutMode LayoutMode,
    string ModeName,
    int Width,
    int Height,
    uint Dpi,
    string AspectRatio,
    string Description
);

/// <summary>
/// Hardware display detector tuned for the 1920x1200 16:10 60Hz display of Snapdragon X Plus systems.
/// Queries physical hardware raster coordinates via GetDeviceCaps (bypassing OS display scaling virtualization)
/// and Per-Monitor V2 DPI, computing optimal codec-friendly macroblock-aligned resolutions for full-bleed and split cockpit modes.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DisplayTopologyDetector
{
    // Win32 GetDeviceCaps index constants
    public const int HORZRES = 8;
    public const int VERTRES = 10;
    public const int LOGPIXELSX = 88;
    public const int LOGPIXELSY = 90;
    public const int VREFRESH = 116;
    public const int DESKTOPVERTRES = 117;
    public const int DESKTOPHORZRES = 118;

    // Standard baseline DPI
    public const uint BASELINE_DPI = 96;

    /// <summary>
    /// Queries the exact physical display raster bounds, refresh rate, and DPI metrics.
    /// Uses DESKTOPHORZRES and DESKTOPVERTRES to acquire true physical panel resolution
    /// regardless of Windows display scaling (100%, 125%, 150%, etc.).
    /// </summary>
    /// <param name="hwnd">Optional window handle for Per-Monitor V2 DPI query. If IntPtr.Zero, uses system DPI.</param>
    /// <returns>PhysicalDisplayTopology record containing hardware display specifications.</returns>
    public static PhysicalDisplayTopology DetectTopology(IntPtr hwnd = default)
    {
        int physicalWidth = 1920;
        int physicalHeight = 1200;
        int logicalWidth = 1920;
        int logicalHeight = 1200;
        int refreshRate = 60;
        uint dpiX = BASELINE_DPI;
        uint dpiY = BASELINE_DPI;

        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc != IntPtr.Zero)
        {
            try
            {
                int dHorz = GetDeviceCaps(hdc, DESKTOPHORZRES);
                int dVert = GetDeviceCaps(hdc, DESKTOPVERTRES);
                int horz = GetDeviceCaps(hdc, HORZRES);
                int vert = GetDeviceCaps(hdc, VERTRES);
                int refresh = GetDeviceCaps(hdc, VREFRESH);
                int logX = GetDeviceCaps(hdc, LOGPIXELSX);
                int logY = GetDeviceCaps(hdc, LOGPIXELSY);

                if (dHorz > 0 && dVert > 0)
                {
                    physicalWidth = dHorz;
                    physicalHeight = dVert;
                }
                else if (horz > 0 && vert > 0)
                {
                    physicalWidth = horz;
                    physicalHeight = vert;
                }

                if (horz > 0 && vert > 0)
                {
                    logicalWidth = horz;
                    logicalHeight = vert;
                }
                else
                {
                    logicalWidth = physicalWidth;
                    logicalHeight = physicalHeight;
                }

                if (refresh > 0)
                {
                    refreshRate = refresh;
                }

                if (logX > 0) dpiX = (uint)logX;
                if (logY > 0) dpiY = (uint)logY;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        // Per-Monitor V2 DPI resolution
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                uint winDpi = GetDpiForWindow(hwnd);
                if (winDpi > 0)
                {
                    dpiX = winDpi;
                    dpiY = winDpi;
                }
            }
            catch
            {
                // Fallback to GetDpiForSystem
            }
        }
        else
        {
            try
            {
                uint sysDpi = GetDpiForSystem();
                if (sysDpi > 0)
                {
                    dpiX = sysDpi;
                    dpiY = sysDpi;
                }
            }
            catch
            {
                // Fallback to GDI LOGPIXELSX
            }
        }

        double scaleFactor = (double)dpiX / BASELINE_DPI;
        bool is16By10 = IsAspectRatio16By10(physicalWidth, physicalHeight);
        bool isSnapdragonNative1200p = (physicalWidth == 1920 && physicalHeight == 1200 && refreshRate == 60);

        string aspectRatioStr = ComputeAspectRatioString(physicalWidth, physicalHeight);

        return new PhysicalDisplayTopology(
            PhysicalWidth: physicalWidth,
            PhysicalHeight: physicalHeight,
            LogicalWidth: logicalWidth,
            LogicalHeight: logicalHeight,
            RefreshRateHz: refreshRate,
            DpiX: dpiX,
            DpiY: dpiY,
            ScaleFactor: scaleFactor,
            AspectRatio: aspectRatioStr,
            Is16By10: is16By10,
            IsSnapdragonNative1200p: isSnapdragonNative1200p
        );
    }

    /// <summary>
    /// Aligns a dimension to the specified macroblock boundary (default: 2 for even dimensions, required by VP9/H.264/AV1).
    /// </summary>
    public static int AlignToMacroblock(int dimension, int alignment = 2)
    {
        if (alignment <= 1) return dimension;
        return dimension & ~(alignment - 1);
    }

    /// <summary>
    /// Computes the optimal Full-Bleed Native 16:10 target resolution:
    /// 1920 x 1200 @ 96 DPI (aligned to even macroblock boundaries).
    /// Provides pixel-perfect 1:1 hardware canvas rendering without letterboxing or downsampling blur.
    /// </summary>
    /// <param name="hwnd">Optional window handle for DPI scaling.</param>
    /// <returns>HostResolutionProfile for full-screen native sessions.</returns>
    public static HostResolutionProfile GetFullBleedResolution(IntPtr hwnd = default)
    {
        var topology = DetectTopology(hwnd);

        int targetWidth = AlignToMacroblock(topology.PhysicalWidth, 2);
        int targetHeight = AlignToMacroblock(topology.PhysicalHeight, 2);

        return new HostResolutionProfile(
            LayoutMode: DisplayLayoutMode.FullBleedNative16By10,
            ModeName: "Full-Bleed Native 16:10",
            Width: targetWidth,
            Height: targetHeight,
            Dpi: topology.DpiX,
            AspectRatio: "16:10",
            Description: $"Pixel-perfect 1:1 hardware canvas mapping ({targetWidth}x{targetHeight} @ {topology.DpiX} DPI) without letterboxing or scaling blur"
        );
    }

    /// <summary>
    /// Computes the optimal Cockpit 50/50 Dual Split target resolution:
    /// 960 x 1200 @ 96 DPI per pane (half physical width x full physical height).
    /// Two side-by-side remote sessions each render in native 8:10 vertical split with zero horizontal scrollbars or letterbox bars.
    /// </summary>
    /// <param name="hwnd">Optional window handle for DPI scaling.</param>
    /// <returns>HostResolutionProfile for side-by-side dual sessions.</returns>
    public static HostResolutionProfile GetCockpitSplitResolution(IntPtr hwnd = default)
    {
        var topology = DetectTopology(hwnd);

        // Half width aligned to even macroblocks (e.g. 1920 / 2 = 960)
        int splitWidth = AlignToMacroblock(topology.PhysicalWidth / 2, 2);
        int splitHeight = AlignToMacroblock(topology.PhysicalHeight, 2);

        return new HostResolutionProfile(
            LayoutMode: DisplayLayoutMode.CockpitDualSplit5050,
            ModeName: "Cockpit 50/50 Dual Split",
            Width: splitWidth,
            Height: splitHeight,
            Dpi: topology.DpiX,
            AspectRatio: "8:10",
            Description: $"Side-by-side split viewport ({splitWidth}x{splitHeight} @ {topology.DpiX} DPI) per session with zero horizontal scrollbars or letterbox bars"
        );
    }

    /// <summary>
    /// Computes the optimal Quad Grid 2x2 Split target resolution:
    /// 960 x 600 @ 96 DPI per pane (half physical width x half physical height).
    /// </summary>
    public static HostResolutionProfile GetQuadGridResolution(IntPtr hwnd = default)
    {
        var topology = DetectTopology(hwnd);

        int quadWidth = AlignToMacroblock(topology.PhysicalWidth / 2, 2);
        int quadHeight = AlignToMacroblock(topology.PhysicalHeight / 2, 2);

        return new HostResolutionProfile(
            LayoutMode: DisplayLayoutMode.QuadGrid2x2,
            ModeName: "Quad Grid 2x2 Split",
            Width: quadWidth,
            Height: quadHeight,
            Dpi: topology.DpiX,
            AspectRatio: "16:10",
            Description: $"Quartered 2x2 grid viewport ({quadWidth}x{quadHeight} @ {topology.DpiX} DPI) per session"
        );
    }

    /// <summary>
    /// Returns the optimal resolution profile for the requested layout mode.
    /// </summary>
    public static HostResolutionProfile GetOptimalHostResolution(DisplayLayoutMode mode, IntPtr hwnd = default)
    {
        return mode switch
        {
            DisplayLayoutMode.FullBleedNative16By10 => GetFullBleedResolution(hwnd),
            DisplayLayoutMode.CockpitDualSplit5050 => GetCockpitSplitResolution(hwnd),
            DisplayLayoutMode.QuadGrid2x2 => GetQuadGridResolution(hwnd),
            _ => GetFullBleedResolution(hwnd)
        };
    }

    /// <summary>
    /// Computes custom fractional horizontal split dimensions (e.g., 0.5 for 50/50, 0.67 for 2/3 primary pane).
    /// All returned dimensions are aligned to even macroblocks (width &amp; ~1, height &amp; ~1).
    /// </summary>
    public static (int Width, int Height) ComputeCustomSplitDimensions(double horizontalFraction, IntPtr hwnd = default)
    {
        var topology = DetectTopology(hwnd);
        int width = (int)Math.Round(topology.PhysicalWidth * Math.Clamp(horizontalFraction, 0.1, 0.9));
        int alignedWidth = AlignToMacroblock(width, 2);
        int alignedHeight = AlignToMacroblock(topology.PhysicalHeight, 2);
        return (alignedWidth, alignedHeight);
    }

    /// <summary>
    /// Generates JavaScript to execute inside a WebView2 session to synchronize the remote host resolution.
    /// </summary>
    public static string GenerateCrdResizeScript(HostResolutionProfile profile)
    {
        return $"window.__crdSetHostResolution && window.__crdSetHostResolution({profile.Width}, {profile.Height}, {profile.Dpi});";
    }

    /// <summary>
    /// Serializes a host resolution profile into JSON format for IPC or telemetry.
    /// </summary>
    public static string ToJson(HostResolutionProfile profile)
    {
        return JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
    }

    #region Aspect Ratio Helpers

    private static bool IsAspectRatio16By10(int width, int height)
    {
        if (height <= 0) return false;
        double ratio = (double)width / height;
        return Math.Abs(ratio - 1.6) < 0.02; // 16/10 = 1.6
    }

    private static string ComputeAspectRatioString(int width, int height)
    {
        if (height <= 0) return "Unknown";
        int gcd = GreatestCommonDivisor(width, height);
        int simplifiedWidth = width / gcd;
        int simplifiedHeight = height / gcd;

        // Common normalized representations
        if (simplifiedWidth == 8 && simplifiedHeight == 5) return "16:10";
        if (simplifiedWidth == 16 && simplifiedHeight == 10) return "16:10";
        if (simplifiedWidth == 4 && simplifiedHeight == 5) return "8:10";
        if (simplifiedWidth == 16 && simplifiedHeight == 9) return "16:9";
        if (simplifiedWidth == 4 && simplifiedHeight == 3) return "4:3";

        return $"{simplifiedWidth}:{simplifiedHeight}";
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int temp = b;
            b = a % b;
            a = temp;
        }
        return Math.Abs(a);
    }

    #endregion

    #region Win32 P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForSystem();

    #endregion
}
