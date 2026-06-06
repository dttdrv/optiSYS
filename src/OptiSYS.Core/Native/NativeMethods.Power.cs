using System.Runtime.InteropServices;

namespace OptiSYS.Core.Native;

/// <summary>
/// Power management: power-scheme GUIDs, per-scheme value read/write (powrprof.dll),
/// the read-only effective-power-mode notification signal, and timer-resolution query (ntdll).
/// </summary>
internal static partial class NativeMethods
{
    // ── Power setting GUIDs ──────────────────────────────────────────
    internal static readonly Guid GUID_PROCESSOR_SETTINGS_SUBGROUP = new("54533251-82be-4824-96c1-47b60b740d00");
    internal static readonly Guid GUID_PROCESSOR_THROTTLE_MINIMUM = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    internal static readonly Guid GUID_PROCESSOR_THROTTLE_MAXIMUM = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    // CPMINCORES: minimum percent of cores kept unparked (low = aggressive parking, 100 = parking disabled).
    internal static readonly Guid GUID_PROCESSOR_CORE_PARKING_MIN_CORES = new("0cc5b647-c1df-4637-891a-dec35c318583");
    internal static readonly Guid GUID_DISK_SUBGROUP = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    internal static readonly Guid GUID_DISK_IDLE_TIMEOUT = new("58e39ba8-b8e6-4ef6-90d0-89ae32b258d6");
    internal static readonly Guid GUID_DISK_AHCI_LINK_POWER = new("0b2d69d7-a2a1-449c-9680-f91c70521c60");

    // ── powrprof.dll ─────────────────────────────────────────────────

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadDCValueIndex(IntPtr RootPowerKey, in Guid SchemeGuid, in Guid SubGroupOfPowerSettingsGuid, in Guid PowerSettingGuid, out uint AcValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteDCValueIndex(IntPtr RootPowerKey, in Guid SchemeGuid, in Guid SubGroupOfPowerSettingsGuid, in Guid PowerSettingGuid, uint DcValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerSetActiveScheme(IntPtr UserRootPowerKey, in Guid SchemeGuid);

    // ── Effective power mode (read-only "follow, never fight" signal) ──
    // PowerRegisterForEffectivePowerModeNotifications was added in Windows 10 1809; V2 (which adds
    // GameMode / MixedReality) in a later release. Both return an HRESULT (0 == S_OK). The callback
    // delegate MUST be kept rooted by the caller for the lifetime of the registration, and the
    // registration handle unregistered on teardown. These are declared via DllImport (not
    // LibraryImport) because the delegate marshalling for the callback is simplest that way and this
    // is invoked rarely (registration once at start, on change thereafter).

    internal const uint EFFECTIVE_POWER_MODE_V1 = 1;
    internal const uint EFFECTIVE_POWER_MODE_V2 = 2;

    // EFFECTIVE_POWER_MODE_CALLBACK(EFFECTIVE_POWER_MODE Mode, VOID* Context)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void EffectivePowerModeCallback(int mode, IntPtr context);

    [DllImport("powrprof.dll")]
    internal static extern int PowerRegisterForEffectivePowerModeNotifications(
        uint version, EffectivePowerModeCallback callback, IntPtr context, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    internal static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr registrationHandle);

    // ── Battery rate (CallNtPowerInformation / SystemBatteryState) ─────
    // CallNtPowerInformation returns an NTSTATUS (0 == STATUS_SUCCESS), NOT a Win32 last-error.
    // SystemBatteryState (info level 5) fills SYSTEM_BATTERY_STATE whose Rate is the present drain
    // in milliwatts: negative while discharging, positive while charging on most adapters.

    private const uint POWER_INFORMATION_LEVEL_SYSTEM_BATTERY_STATE = 5;

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        uint InformationLevel,
        IntPtr lpInputBuffer,
        uint nInputBufferSize,
        out SYSTEM_BATTERY_STATE lpOutputBuffer,
        uint nOutputBufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_BATTERY_STATE
    {
        [MarshalAs(UnmanagedType.I1)] public bool AcOnLine;
        [MarshalAs(UnmanagedType.I1)] public bool BatteryPresent;
        [MarshalAs(UnmanagedType.I1)] public bool Charging;
        [MarshalAs(UnmanagedType.I1)] public bool Discharging;
        public byte Spare1_0;
        public byte Spare1_1;
        public byte Spare1_2;
        public byte Tag;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    /// <summary>
    /// Reads the present battery rate (milliwatts, signed: negative while discharging). Returns the
    /// signed <c>Rate</c> on success, or <see langword="null"/> when the NTSTATUS is non-zero so the
    /// caller can log the failure and degrade. <paramref name="status"/> carries the NTSTATUS for the
    /// caller's diagnostic log.
    /// </summary>
    internal static int? ReadBatteryRateMilliwatts(out uint status)
    {
        status = CallNtPowerInformation(
            POWER_INFORMATION_LEVEL_SYSTEM_BATTERY_STATE,
            IntPtr.Zero,
            0,
            out var state,
            (uint)Marshal.SizeOf<SYSTEM_BATTERY_STATE>());

        return status == 0 ? state.Rate : null;
    }

    // ── ntdll.dll ─────────────────────────────────────────────────────

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryTimerResolution(out uint MinimumResolution, out uint MaximumResolution, out uint CurrentResolution);

    // ── Helpers ───────────────────────────────────────────────────────

    internal static Guid GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0)
            return Guid.Empty;
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    internal static uint? ReadDCValue(Guid scheme, Guid subgroup, Guid setting)
    {
        var result = PowerReadDCValueIndex(IntPtr.Zero, scheme, subgroup, setting, out var value);
        return result == 0 ? value : null;
    }

    internal static bool WriteDCValue(Guid scheme, Guid subgroup, Guid setting, uint value)
    {
        return PowerWriteDCValueIndex(IntPtr.Zero, scheme, subgroup, setting, value) == 0;
    }
}
