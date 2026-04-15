using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptiSYS.Core.Models;

public enum OptimizationLevel { Conservative, Balanced, Aggressive }
public enum PowerSourceAction { Activate, Deactivate, DoNothing }

/// <summary>
/// Unified settings for all optimization domains (battery + memory).
/// Persisted as JSON in %APPDATA%\optiSYS\settings.json.
/// </summary>
public sealed class Settings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "optiSYS");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");
    private static readonly string SnapshotFile = Path.Combine(SettingsDir, "snapshots.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private CancellationTokenSource? _debounceCts;

    // ── Battery optimization settings ────────────────────────────────
    public bool AutoOptimizeOnBattery { get; set; } = true;
    public int DebouncePowerChangeSeconds { get; set; } = 2;

    // Battery domain toggles
    public bool EcoQosEnabled { get; set; } = true;
    public bool TimerResolutionEnabled { get; set; } = true;
    public bool BackgroundServicesEnabled { get; set; } = true;
    public bool UsbSuspendEnabled { get; set; } = true;
    public bool NetworkPowerEnabled { get; set; } = true;
    public bool GpuPowerEnabled { get; set; } = true;
    public bool CpuParkingEnabled { get; set; } = true;
    public bool DiskCoalescingEnabled { get; set; } = true;

    // Battery domain-specific settings
    public List<string> EcoQosExcludedProcesses { get; set; } =
    [
        "System", "Idle", "smss", "csrss", "wininit", "services",
        "lsass", "svchost", "dwm", "winlogon", "fontdrvhost", "conhost",
        "Memory Compression", "Registry"
    ];

    public List<string> ServicesToThrottle { get; set; } =
    [
        "WSearch", "SysMain", "DiagTrack", "BITS",
        "wuauserv", "DoSvc", "DPS", "WdiServiceHost"
    ];

    public int CpuParkingMinProcessorDC { get; set; } = 5;
    public int DiskIdleTimeoutSeconds { get; set; } = 30;

    public List<string> TimerResolutionExcludedProcesses { get; set; } =
    [
        "System", "Idle", "smss", "csrss", "wininit", "services",
        "lsass", "svchost", "dwm", "winlogon", "fontdrvhost", "conhost",
        "audiodg", "NVIDIA Display Container"
    ];

    // Memory optimization settings
    public bool AutoOptimizeMemoryEnabled { get; set; } = false;
    public int MemoryCheckIntervalSeconds { get; set; } = 5;
    public int MemoryThresholdPercent { get; set; } = 80;
    public int MemoryCooldownSeconds { get; set; } = 30;
    public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Balanced;
    // NOTE: SelfWorkingSetCapMB must be >= 100 for UI apps.
    // optiRAM defaulted to 25MB which caused System.OutOfMemoryException
    // in WPF's MediaContext.CommitChannel (insufficient memory for rendering).
    public int SelfWorkingSetCapMB { get; set; } = 100;
    public int CacheMaxPercent { get; set; } = 0; // 0 = disabled
    public int HysteresisGap { get; set; } = 10;
    public int TrendWindowSize { get; set; } = 10;
    public int PredictiveLeadSeconds { get; set; } = 15;
    public int AccessedBitsDelayMs { get; set; } = 2000;
    public bool EffectivenessTrackingEnabled { get; set; } = true;
    public bool ScheduledOptimizeEnabled { get; set; } = false;
    public int ScheduledOptimizeIntervalMinutes { get; set; } = 30;

    public List<string> MemoryExcludedProcesses { get; set; } =
    [
        "System", "Idle", "smss", "csrss", "wininit", "services",
        "lsass", "svchost", "dwm", "winlogon", "Memory Compression",
        "Registry", "fontdrvhost", "conhost"
    ];

    // ── Common UI settings ────────────────────────────────────────────
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 720;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string ThemeMode { get; set; } = "System"; // "System", "Light", "Dark"
    public int HistoryMaxItems { get; set; } = 50;
    public string SelectedNavItem { get; set; } = "Dashboard";

    // ── Paths ────────────────────────────────────────────────────────
    public static string GetSettingsDir() => SettingsDir;
    public static string GetSnapshotPath() => SnapshotFile;

    // ── Load / Save ──────────────────────────────────────────────────
    public static Settings Load()
    {
        try
        {
            MigrateOldSettings();
            if (!File.Exists(SettingsFile))
                return new Settings();

            var json = File.ReadAllText(SettingsFile);
            var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
            settings.Validate();
            return settings;
        }
        catch
        {
            return new Settings();
        }
    }

    /// <summary>One-time migration from optiBAT and optiRAM settings dirs.</summary>
    private static void MigrateOldSettings()
    {
        if (File.Exists(SettingsFile)) return;

        // Try optiRAM first (more settings to preserve)
        var ramDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "optiRAM");
        var ramFile = Path.Combine(ramDir, "settings.json");
        if (File.Exists(ramFile))
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.Copy(ramFile, SettingsFile, overwrite: false);
                return;
            }
            catch { /* Migration failure is non-critical */ }
        }

        // Try RAMSpeed (legacy name)
        var oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RAMSpeed");
        var oldFile = Path.Combine(oldDir, "settings.json");
        if (File.Exists(oldFile))
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.Copy(oldFile, SettingsFile, overwrite: false);
                return;
            }
            catch { }
        }

        // Try optiBAT
        var batDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "optiBAT");
        var batFile = Path.Combine(batDir, "settings.json");
        if (File.Exists(batFile))
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.Copy(batFile, SettingsFile, overwrite: false);
            }
            catch { }
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    public async void SaveDebounced()
    {
        var old = Interlocked.Exchange(ref _debounceCts, new CancellationTokenSource());
        old?.Cancel();
        old?.Dispose();
        var token = _debounceCts!.Token;
        var json = JsonSerializer.Serialize(this, JsonOptions);

        try
        {
            await Task.Delay(500, token);
            Directory.CreateDirectory(SettingsDir);
            await File.WriteAllTextAsync(SettingsFile, json, token);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Validate()
    {
        // Battery
        DebouncePowerChangeSeconds = Math.Clamp(DebouncePowerChangeSeconds, 1, 10);
        CpuParkingMinProcessorDC = Math.Clamp(CpuParkingMinProcessorDC, 0, 100);
        DiskIdleTimeoutSeconds = Math.Clamp(DiskIdleTimeoutSeconds, 10, 300);

        // Memory
        MemoryCheckIntervalSeconds = Math.Clamp(MemoryCheckIntervalSeconds, 1, 60);
        MemoryThresholdPercent = Math.Clamp(MemoryThresholdPercent, 10, 95);
        MemoryCooldownSeconds = Math.Clamp(MemoryCooldownSeconds, 5, 300);
        CacheMaxPercent = Math.Clamp(CacheMaxPercent, 0, 75);
        SelfWorkingSetCapMB = Math.Clamp(SelfWorkingSetCapMB, 0, 512);
        HysteresisGap = Math.Clamp(HysteresisGap, 5, 30);
        TrendWindowSize = Math.Clamp(TrendWindowSize, 5, 60);
        PredictiveLeadSeconds = Math.Clamp(PredictiveLeadSeconds, 5, 120);
        AccessedBitsDelayMs = Math.Clamp(AccessedBitsDelayMs, 500, 5000);
        ScheduledOptimizeIntervalMinutes = Math.Clamp(ScheduledOptimizeIntervalMinutes, 1, 1440);
        HistoryMaxItems = Math.Clamp(HistoryMaxItems, 1, 500);

        // UI
        WindowWidth = double.IsFinite(WindowWidth) ? Math.Clamp(WindowWidth, 400, 4000) : 1100;
        WindowHeight = double.IsFinite(WindowHeight) ? Math.Clamp(WindowHeight, 300, 3000) : 720;
        if (!double.IsFinite(WindowLeft)) WindowLeft = double.NaN;
        if (!double.IsFinite(WindowTop)) WindowTop = double.NaN;

        ThemeMode ??= "System";
        EcoQosExcludedProcesses ??= [];
        ServicesToThrottle ??= [];
        MemoryExcludedProcesses ??= [];
        TimerResolutionExcludedProcesses ??= [];
    }
}
