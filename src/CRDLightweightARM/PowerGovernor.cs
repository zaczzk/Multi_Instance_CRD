using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CRDLightweightARM;

public enum PowerSource { AC, Battery }

public class SnapdragonPowerGovernor : IDisposable
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
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public void Initialize(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(HwndHook);

            _hPowerNotifyACDC = RegisterPowerSettingNotification(handle, in GUID_ACDC_POWER_SOURCE, 0);

            // Read initial state via Kernel32
            if (GetSystemPowerStatus(out var status))
            {
                CurrentPowerSource = status.ACLineStatus == 1 ? PowerSource.AC : PowerSource.Battery;
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
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_ACDC_POWER_SOURCE)
            {
                int acdc = Marshal.ReadInt32(lParam, Marshal.OffsetOf<POWERBROADCAST_SETTING>("Data").ToInt32());
                CurrentPowerSource = acdc == 0 ? PowerSource.AC : PowerSource.Battery;
                PowerSourceChanged?.Invoke(CurrentPowerSource);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid PowerSettingGuid, int Flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public int DataLength;
        public byte Data;
    }

    public void Dispose()
    {
        if (_hPowerNotifyACDC != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_hPowerNotifyACDC);
            _hPowerNotifyACDC = IntPtr.Zero;
        }
    }
}
