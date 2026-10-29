using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CRDLightweightARM;

public enum PowerSource { AC, Battery }

/// <summary>
/// Universal Windows Power Governor supporting Qualcomm Snapdragon X (ARM64),
/// Intel Core / Core Ultra (x64), and AMD Ryzen / Ryzen AI (x64).
/// </summary>
public class WindowsPowerGovernor : IDisposable
{
    private static readonly Guid GUID_ACDC_POWER_SOURCE = new("5d3e0268-fff8-4382-a55b-300e84b90161");

    private IntPtr _hPowerNotifyACDC = IntPtr.Zero;
    private HwndSource? _hwndSource;

    public event Action<PowerSource>? PowerSourceChanged;
    public PowerSource CurrentPowerSource { get; private set; } = PowerSource.AC;

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus; // 0 = Battery, 1 = AC, 255 = Unknown (Desktop without battery)
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public byte Data; // First byte of data payload
    }

    public void Initialize(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(HwndHook);

            _hPowerNotifyACDC = RegisterPowerSettingNotification(handle, in GUID_ACDC_POWER_SOURCE, 0);

            // Read initial state
            if (GetSystemPowerStatus(out var status))
            {
                // 0 = Battery, 1 = AC, 255 = Unknown/Desktop (treat as AC)
                CurrentPowerSource = (status.ACLineStatus == 0) ? PowerSource.Battery : PowerSource.AC;
            }
        }
        catch
        {
            CurrentPowerSource = PowerSource.AC;
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_POWERBROADCAST = 0x0218;
        const int PBT_POWERSETTINGCHANGE = 0x8013;

        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE)
        {
            try
            {
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                if (setting.PowerSetting == GUID_ACDC_POWER_SOURCE)
                {
                    // Data is 32-bit integer: 0 = AC, 1 = Battery, 2 = Short-term UPS
                    IntPtr pData = IntPtr.Add(lParam, 20); // 16 bytes Guid + 4 bytes DataLength
                    int acdc = Marshal.ReadInt32(pData);
                    CurrentPowerSource = (acdc == 1) ? PowerSource.Battery : PowerSource.AC;
                    PowerSourceChanged?.Invoke(CurrentPowerSource);
                    handled = true;
                }
            }
            catch {}
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    public void Dispose()
    {
        if (_hwndSource != null)
        {
            try
            {
                _hwndSource.RemoveHook(HwndHook);
            }
            catch {}
            _hwndSource = null;
        }

        if (_hPowerNotifyACDC != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_hPowerNotifyACDC);
            _hPowerNotifyACDC = IntPtr.Zero;
        }
    }
}
