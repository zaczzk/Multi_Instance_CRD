using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CRDLightweightARM.Hardware;

/// <summary>
/// Hardware governor and probe specifically optimized for Qualcomm Snapdragon X Plus
/// (X1P42100 8-core Oryon CPU) and Qualcomm Adreno X1-45 GPU running on Windows 11 ARM64.
/// Manages MMCSS real-time thread promotion, power throttling policies (EcoQoS vs High QoS),
/// and working-set memory compaction for multi-session Chrome Remote Desktop workloads.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SnapdragonOryonGovernor : IDisposable
{
    private static readonly Lazy<SnapdragonOryonGovernor> _lazyInstance =
        new(() => new SnapdragonOryonGovernor());

    public static SnapdragonOryonGovernor Instance => _lazyInstance.Value;

    private IntPtr _hAvrt = IntPtr.Zero;
    private uint _mmcssTaskIndex = 0;
    private bool _isDisposed = false;

    // --- Hardware Probe Properties ---
    public string CpuName { get; }
    public Architecture ProcessArchitecture { get; }
    public Architecture OsArchitecture { get; }
    public int CoreCount { get; }
    public string GpuName { get; }
    public string HardwareBadge { get; }
    public bool IsSnapdragonOryon { get; }
    public bool IsAdrenoGpu { get; }
    public bool IsMainThreadPromoted => _hAvrt != IntPtr.Zero;

    public SnapdragonOryonGovernor()
    {
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture;
        OsArchitecture = RuntimeInformation.OSArchitecture;
        CoreCount = Environment.ProcessorCount;

        CpuName = DetectCpuName();
        GpuName = DetectGpuName();

        IsSnapdragonOryon = CheckIfSnapdragonOryon(CpuName, ProcessArchitecture);
        IsAdrenoGpu = GpuName.Contains("Adreno", StringComparison.OrdinalIgnoreCase);

        HardwareBadge = BuildHardwareBadge(CpuName, CoreCount, GpuName, IsSnapdragonOryon, IsAdrenoGpu);
    }

    #region Hardware Probing

    private static string DetectCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }
        catch
        {
            // Fallback to environment probe
        }

        var envIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        if (!string.IsNullOrWhiteSpace(envIdentifier))
        {
            return envIdentifier.Trim();
        }

        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "Qualcomm Oryon ARM64 Processor"
            : "Generic Processor";
    }

    private static string DetectGpuName()
    {
        try
        {
            // Probe Windows Display Adapter registry class
            const string displayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var classKey = Registry.LocalMachine.OpenSubKey(displayClassPath);
            if (classKey != null)
            {
                foreach (var subKeyName in classKey.GetSubKeyNames())
                {
                    if (subKeyName.Length == 4 && char.IsDigit(subKeyName[0]))
                    {
                        using var adapterKey = classKey.OpenSubKey(subKeyName);
                        var desc = adapterKey?.GetValue("DriverDesc") as string;
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            if (desc.Contains("Adreno", StringComparison.OrdinalIgnoreCase))
                            {
                                return desc.Trim();
                            }
                        }
                    }
                }

                // If not specifically Adreno, try the first primary adapter (0000)
                using var defaultAdapter = classKey.OpenSubKey("0000");
                var defaultDesc = defaultAdapter?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrWhiteSpace(defaultDesc))
                {
                    return defaultDesc.Trim();
                }
            }
        }
        catch
        {
            // Fallback to EnumDisplayDevices
        }

        try
        {
            var displayDevice = new DISPLAY_DEVICE();
            displayDevice.cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>();
            if (EnumDisplayDevices(null, 0, ref displayDevice, 0))
            {
                if (!string.IsNullOrWhiteSpace(displayDevice.DeviceString))
                {
                    return displayDevice.DeviceString.Trim();
                }
            }
        }
        catch
        {
            // Fallback
        }

        return "Qualcomm Adreno Graphics";
    }

    private static bool CheckIfSnapdragonOryon(string cpu, Architecture arch)
    {
        if (arch != Architecture.Arm64)
        {
            return false;
        }

        return cpu.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase) ||
               cpu.Contains("Oryon", StringComparison.OrdinalIgnoreCase) ||
               cpu.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildHardwareBadge(string cpu, int cores, string gpu, bool isOryon, bool isAdreno)
    {
        string cpuLabel;
        if (cpu.Contains("X Plus", StringComparison.OrdinalIgnoreCase) || cpu.Contains("X1P", StringComparison.OrdinalIgnoreCase))
        {
            cpuLabel = $"Snapdragon X Plus ({cores}C Oryon)";
        }
        else if (cpu.Contains("X Elite", StringComparison.OrdinalIgnoreCase) || cpu.Contains("X1E", StringComparison.OrdinalIgnoreCase))
        {
            cpuLabel = $"Snapdragon X Elite ({cores}C Oryon)";
        }
        else if (isOryon)
        {
            cpuLabel = $"Snapdragon Oryon ({cores}C)";
        }
        else
        {
            var cleanedCpu = CleanHardwareString(cpu);
            cpuLabel = $"{cleanedCpu} ({cores}C)";
        }

        string gpuLabel;
        var adrenoMatch = Regex.Match(gpu, @"Adreno(?:\(TM\))?\s+(X\d+-\d+|\d+)", RegexOptions.IgnoreCase);
        if (adrenoMatch.Success)
        {
            gpuLabel = $"Adreno {adrenoMatch.Groups[1].Value}";
        }
        else if (isAdreno)
        {
            gpuLabel = "Adreno GPU";
        }
        else
        {
            gpuLabel = CleanHardwareString(gpu);
        }

        return $"{cpuLabel} • {gpuLabel}";
    }

    private static string CleanHardwareString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var cleaned = raw.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("Qualcomm", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("CPU", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("GPU", "", StringComparison.OrdinalIgnoreCase)
                         .Trim();
        return Regex.Replace(cleaned, @"\s+", " ");
    }

    #endregion

    #region Thread Latency Promotion (MMCSS & Win11 Execution Speed)

    /// <summary>
    /// Promotes the current thread (typically the UI/input/swapchain thread) to low-latency real-time
    /// status via Win32 AvSetMmThreadCharacteristicsW("Games") and AvSetMmThreadPriority(AVRT_PRIORITY_HIGH),
    /// and explicitly disables execution speed power throttling on the calling thread.
    /// This prevents the Windows 11 ARM64 scheduler from frequency down-clocking or migrating the thread
    /// off the performance Oryon cores during mouse delta capture and 60 FPS swapchain presentation.
    /// </summary>
    /// <returns>True if thread characteristics and power throttling override were successfully applied.</returns>
    public bool PromoteMainThreadForLowLatency()
    {
        bool mmcssSuccess = false;
        bool powerSuccess = false;

        try
        {
            if (_hAvrt == IntPtr.Zero)
            {
                _mmcssTaskIndex = 0;
                _hAvrt = AvSetMmThreadCharacteristicsW("Games", ref _mmcssTaskIndex);
                if (_hAvrt != IntPtr.Zero)
                {
                    mmcssSuccess = AvSetMmThreadPriority(_hAvrt, AVRT_PRIORITY.AVRT_PRIORITY_HIGH);
                }
            }
            else
            {
                mmcssSuccess = true;
            }
        }
        catch
        {
            mmcssSuccess = false;
        }

        try
        {
            // Explicitly disable execution speed throttling on current thread
            var threadThrottlingState = new THREAD_POWER_THROTTLING_STATE
            {
                Version = THREAD_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = THREAD_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0 // 0 = Do NOT throttle execution speed (High performance)
            };

            powerSuccess = SetThreadInformation(
                GetCurrentThread(),
                THREAD_INFORMATION_CLASS.ThreadPowerThrottling,
                ref threadThrottlingState,
                (uint)Marshal.SizeOf<THREAD_POWER_THROTTLING_STATE>());
        }
        catch
        {
            powerSuccess = false;
        }

        return mmcssSuccess || powerSuccess;
    }

    /// <summary>
    /// Reverts low-latency MMCSS promotion on the calling thread.
    /// </summary>
    public void RevertMainThreadPromotion()
    {
        if (_hAvrt != IntPtr.Zero)
        {
            try
            {
                AvRevertMmThreadCharacteristics(_hAvrt);
            }
            catch
            {
                // Revert best effort
            }
            finally
            {
                _hAvrt = IntPtr.Zero;
                _mmcssTaskIndex = 0;
            }
        }
    }

    #endregion

    #region Session Process QoS & Memory Working Set Compaction

    /// <summary>
    /// Applies Windows 11 Quality of Service (QoS) and memory trimming policies across a browser session
    /// and all of its associated child processes (such as msedgewebview2.exe renderer, GPU, and utility processes).
    /// </summary>
    /// <param name="browserProcessId">The root browser process ID associated with the session.</param>
    /// <param name="isEcoStandby">
    /// If true, applies Windows 11 EcoQoS (PROCESS_POWER_THROTTLING_EXECUTION_SPEED enabled), lowers process priority,
    /// and purges unreferenced memory pages via EmptyWorkingSet() so background sessions consume 0% Adreno GPU
    /// and minimal LPDDR5x RAM.
    /// If false, disables execution speed throttling (High QoS) and restores normal priority so active sessions
    /// achieve full Oryon boost frequency.
    /// </param>
    /// <returns>The total number of processes whose QoS and working set was configured.</returns>
    public int ApplySessionProcessQoS(uint browserProcessId, bool isEcoStandby)
    {
        if (browserProcessId == 0) return 0;

        var targetPids = EnumerateSessionProcessTree(browserProcessId);
        int successfulCount = 0;

        foreach (var pid in targetPids)
        {
            // PROCESS_SET_INFORMATION (0x0200) | PROCESS_SET_QUOTA (0x0100) | PROCESS_QUERY_LIMITED_INFORMATION (0x1000)
            const uint desiredAccess = 0x0200 | 0x0100 | 0x1000;
            IntPtr hProcess = OpenProcess(desiredAccess, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var throttlingState = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = isEcoStandby ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
                };

                bool qosApplied = SetProcessInformation(
                    hProcess,
                    PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                    ref throttlingState,
                    (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());

                if (isEcoStandby)
                {
                    // Set priority to Idle/Background
                    SetPriorityClass(hProcess, IDLE_PRIORITY_CLASS);

                    // Evict idle pages from LPDDR5x RAM
                    EmptyWorkingSet(hProcess);
                }
                else
                {
                    // Restore priority to Normal
                    SetPriorityClass(hProcess, NORMAL_PRIORITY_CLASS);
                }

                if (qosApplied)
                {
                    successfulCount++;
                }
            }
            catch
            {
                // Continue with next process
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        return successfulCount;
    }

    /// <summary>
    /// Enumerates the root process and all child msedgewebview2.exe / edge descendant processes using Win32 Toolhelp snapshot.
    /// </summary>
    private static HashSet<uint> EnumerateSessionProcessTree(uint rootPid)
    {
        var processIds = new HashSet<uint> { rootPid };

        IntPtr hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnapshot == IntPtr.Zero || hSnapshot == new IntPtr(-1))
        {
            return processIds;
        }

        try
        {
            var pe = new PROCESSENTRY32W();
            pe.dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>();

            var parentChildMap = new List<(uint ParentId, uint ProcessId, string ExeName)>();

            if (Process32FirstW(hSnapshot, ref pe))
            {
                do
                {
                    parentChildMap.Add((pe.th32ParentProcessID, pe.th32ProcessID, pe.szExeFile));
                }
                while (Process32NextW(hSnapshot, ref pe));
            }

            // Recursively collect all descendants
            bool addedNew;
            do
            {
                addedNew = false;
                foreach (var entry in parentChildMap)
                {
                    if (processIds.Contains(entry.ParentId) && !processIds.Contains(entry.ProcessId))
                    {
                        // Match children or specifically WebView2 processes
                        if (entry.ExeName.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase) ||
                            processIds.Contains(entry.ParentId))
                        {
                            processIds.Add(entry.ProcessId);
                            addedNew = true;
                        }
                    }
                }
            }
            while (addedNew);
        }
        catch
        {
            // Snapshot traversal failed, fallback to rootPid only
        }
        finally
        {
            CloseHandle(hSnapshot);
        }

        return processIds;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (!_isDisposed)
        {
            RevertMainThreadPromotion();
            _isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~SnapdragonOryonGovernor()
    {
        Dispose();
    }

    #endregion

    #region Win32 P/Invoke & Structures

    private const uint THREAD_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint THREAD_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    private const uint IDLE_PRIORITY_CLASS = 0x00000040;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    private enum AVRT_PRIORITY
    {
        AVRT_PRIORITY_VERYLOW = -2,
        AVRT_PRIORITY_LOW = -1,
        AVRT_PRIORITY_NORMAL = 0,
        AVRT_PRIORITY_HIGH = 1,
        AVRT_PRIORITY_CRITICAL = 2
    }

    private enum THREAD_INFORMATION_CLASS
    {
        ThreadMemoryPriority = 0,
        ThreadAbsoluteCpuPriority = 1,
        ThreadDynamicCodePolicy = 2,
        ThreadPowerThrottling = 3,
        ThreadInformationClassMax = 4
    }

    private enum PROCESS_INFORMATION_CLASS
    {
        ProcessMemoryPriority = 0,
        ProcessMemoryExhaustionInfo = 1,
        ProcessAppMemoryInfo = 2,
        ProcessInPrivateInfo = 3,
        ProcessPowerThrottling = 4,
        ProcessReservedValue1 = 5,
        ProcessTelemetryCoverageInfo = 6,
        ProcessProtectionLevelInfo = 7,
        ProcessLeapSecondInfo = 8,
        ProcessMachineTypeInfo = 9,
        ProcessOverrideSubsequentPrefetchParameter = 10,
        ProcessMaxPriorityHint = 11,
        ProcessInformationClassMax = 12
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREAD_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string TaskName, ref uint TaskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvSetMmThreadPriority(IntPtr AvrtHandle, AVRT_PRIORITY Priority);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr AvrtHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadInformation(
        IntPtr hThread,
        THREAD_INFORMATION_CLASS ThreadInformationClass,
        ref THREAD_POWER_THROTTLING_STATE ThreadInformation,
        uint ThreadInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess,
        PROCESS_INFORMATION_CLASS ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    #endregion
}
