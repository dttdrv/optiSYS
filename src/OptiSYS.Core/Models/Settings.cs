using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OptiSYS.Core.Models;

public enum OptimizationLevel { Conservative, Balanced, Aggressive }
public enum BatteryPreset { Recommended, Saver }
public enum PowerSourceAction { Activate, Deactivate, DoNothing }

/// <summary>
/// Unified settings for all optimization domains (battery + memory).
/// Persisted as JSON in %APPDATA%\optiSYS\settings.json.
/// </summary>
public sealed class Settings
{
    public static IReadOnlyList<string> CriticalProcessExclusions { get; } =
    [
        "System", "Idle", "smss", "csrss", "wininit", "services",
        "lsass", "svchost", "dwm", "winlogon", "fontdrvhost", "conhost",
        "Memory Compression", "Registry",
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "SystemSettings",
        "Widgets"
    ];

    private static readonly string[] DefaultServicesToThrottle =
    [
        "WSearch", "SysMain", "DiagTrack", "BITS",
        "wuauserv", "DoSvc", "DPS", "WdiServiceHost"
    ];

    /// <summary>
    /// Curated allowlist of well-known background processes (indexers / updaters / sync
    /// daemons / telemetry) whose memory priority may be hinted to LOW/VERY_LOW. This is a
    /// pure page-eviction-order hint (zero disk IO, reversible to NORMAL) — it is NEVER
    /// applied to "all non-foreground" processes. Anything that also appears on the
    /// critical or protected lists is excluded at apply time (see
    /// <c>MemoryOptimizer.HintBackgroundMemoryPriority</c>).
    /// </summary>
    public static IReadOnlyList<string> BackgroundMemoryPriorityAllowlist { get; } =
    [
        "SearchIndexer", "SearchProtocolHost", "SearchFilterHost",
        "OneDrive", "OneDriveStandaloneUpdater",
        "Dropbox", "DropboxUpdate",
        "GoogleDriveFS", "googledrivesync",
        "Backup and Sync from Google",
        "MsMpEngCP",                       // Defender update content (not the engine)
        "CompatTelRunner", "CompatTelemetryRunner",
        "WaaSMedicAgent",
        "MoUsoCoreWorker", "usocoreworker",
        "TiWorker",                        // Windows Modules Installer worker
        "GoogleUpdate", "GoogleCrashHandler",
        "AdobeUpdateService", "Adobe Desktop Service", "AdobeIPCBroker",
        "EpicGamesLauncher", "EpicWebHelper",
        "SteamService"
    ];

    /// <summary>
    /// Conservative, hand-curated set of genuinely non-essential services optiSYS flips from
    /// Automatic → Manual start (it never STOPS them). Each still starts on demand, so the change
    /// is unnoticeable. Admin-gated. Kept deliberately small to honor "affects no workflow."
    /// </summary>
    public static IReadOnlyList<string> ServicesToSetManual { get; } =
    [
        "MapsBroker",    // Downloaded Maps Manager — only the Maps app uses it
        "Fax",           // Fax
        "lltdsvc",       // Link-Layer Topology Discovery Mapper — network map view
        "RetailDemo",    // Retail Demo Service — in-store demo mode
    ];

    /// <summary>
    /// Hard block-list: services whose start type must NEVER be changed, even if one were
    /// mistakenly added to <see cref="ServicesToSetManual"/>. Defensive belt-and-suspenders.
    /// </summary>
    public static IReadOnlyList<string> ServicesNeverManual { get; } =
    [
        "Audiosrv", "AudioEndpointBuilder", "RpcSs", "RpcEptMapper", "DcomLaunch",
        "BrokerInfrastructure", "SystemEventsBroker", "MpsSvc", "BFE", "WinDefend",
        "Dhcp", "Dnscache", "NlaSvc", "Wcmsvc", "WinHttpAutoProxySvc", "WlanSvc",
        "SamSs", "RasMan", "EapHost", "Schedule", "Power", "ProfSvc", "CryptSvc",
        "UserManager", "Themes",
    ];

    private static readonly HashSet<string> AllowedServicesToThrottle =
        new(DefaultServicesToThrottle, StringComparer.OrdinalIgnoreCase);

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
    public bool AutomationPaused { get; set; } = false;
    public BatteryPreset BatteryPreset { get; set; } = BatteryPreset.Recommended;
    public int DebouncePowerChangeSeconds { get; set; } = 2;

    // Battery domain toggles
    // EcoQoS and TimerResolution throttle ALL non-foreground processes, so they are
    // opt-in only (default OFF) per the overhaul design §3 — never enable silently.
    public bool EcoQosEnabled { get; set; } = false;
    public bool TimerResolutionEnabled { get; set; } = false;
    public bool BackgroundServicesEnabled { get; set; } = false;
    public bool UsbSuspendEnabled { get; set; } = false;
    public bool NetworkPowerEnabled { get; set; } = false;
    public bool GpuPowerEnabled { get; set; } = false;
    public bool CpuParkingEnabled { get; set; } = false;
    public bool DiskCoalescingEnabled { get; set; } = false;

    // Wi-Fi latency optimizer — part of the all-in-one automatic optimization (ON by default).
    // Unelevated + session-scoped + reversible. Disabling the background scan removes 100ms+
    // spikes on the active connection; streaming mode prioritizes it. Activated/reverted together
    // with the master "Automatic optimization" switch; a no-op on machines without a Wi-Fi adapter.
    public bool WiFiOptimizerEnabled { get; set; } = true;
    public bool WiFiDisableBackgroundScan { get; set; } = true;
    public bool WiFiStreamingMode { get; set; } = true;

    // Services-to-Manual — part of the AIO set, but ADMIN-GATED (only applies when the app runs
    // elevated, granted once via the installer checkbox). ON by default to match that checkbox;
    // a clean no-op when unelevated. Only flips Automatic→Manual start (never stops a service).
    public bool ServicesManualEnabled { get; set; } = true;

    // Battery domain-specific settings
    public List<string> EcoQosExcludedProcesses { get; set; } =
    [
        .. CriticalProcessExclusions
    ];

    public List<string> ServicesToThrottle { get; set; } = [.. DefaultServicesToThrottle];

    public int CpuParkingMinProcessorDC { get; set; } = 5;
    public int DiskIdleTimeoutSeconds { get; set; } = 30;

    public List<string> TimerResolutionExcludedProcesses { get; set; } =
    [
        .. CriticalProcessExclusions,
        "audiodg", "NVIDIA Display Container"
    ];

    // Memory optimization settings. Threshold + interval are baked-in defaults (no UI knobs):
    // 60% acts only under genuine pressure (typical systems idle at 40-60%) while the predictive
    // trigger catches climbs earlier; 30s keeps overhead low. Tuned with the AIO simplification.
    public bool AutoOptimizeMemoryEnabled { get; set; } = true;
    public int MemoryCheckIntervalSeconds { get; set; } = 30;
    public int MemoryThresholdPercent { get; set; } = 60;
    public int MemoryCooldownSeconds { get; set; } = 30;
    public int MemoryCleanupDurationSeconds { get; set; } = 15;
    public int MemoryRepeatPasses { get; set; } = 2;
    public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Conservative;
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
        .. CriticalProcessExclusions
    ];
    public List<string> ProtectedApplications { get; set; } =
    [
        "Code", "Cursor", "devenv", "rider64", "idea64", "clion64", "pycharm64", "webstorm64",
        "datagrip64", "dotnet", "msbuild", "node", "python", "pwsh", "powershell", "cmd", "wt",
        "chrome", "msedge", "firefox", "brave", "vivaldi", "obs64", "Teams", "ms-teams",
        "Zoom", "Discord", "slack"
    ];

    // ── Common UI settings ────────────────────────────────────────────
    public double WindowWidth { get; set; } = 640;
    public double WindowHeight { get; set; } = 420;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;

    /// <summary>
    /// Opt-in: run optiSYS elevated at logon via a self-provisioned Task Scheduler task
    /// (one-time UAC, then silent). When false, the plain HKCU Run-key autostart is used.
    /// </summary>
    public bool UseTaskScheduler { get; set; } = false;

    /// <summary>
    /// Transient UI state: set when <see cref="UseTaskScheduler"/> is on but the elevated
    /// logon task is missing/stale and we are not elevated — drives a non-nagging
    /// "Grant admin access" banner. Recomputed at startup; not a persisted user intent.
    /// </summary>
    public bool ElevationPending { get; set; } = false;

    public bool HasCompletedOnboarding { get; set; } = false;
    public string ThemeMode { get; set; } = "System"; // "System", "Light", "Dark"
    public bool UseWindowsAccentColor { get; set; } = true;
    public string BackdropType { get; set; } = "Acrylic";
    public int HistoryMaxItems { get; set; } = 50;
    public string SelectedNavItem { get; set; } = "Dashboard";

    // ── Paths ────────────────────────────────────────────────────────
    public static string GetSettingsDir() => SettingsDir;
    public static string GetSnapshotPath() => SnapshotFile;

    public static List<string> NormalizeServicesToThrottle(IEnumerable<string>? services) =>
        services?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Where(AllowedServicesToThrottle.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

    private static List<string> NormalizeProcessList(IEnumerable<string>? processes) =>
        processes?
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
        ?? [];

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

    public void Validate()
    {
        // Battery
        DebouncePowerChangeSeconds = Math.Clamp(DebouncePowerChangeSeconds, 1, 10);
        CpuParkingMinProcessorDC = Math.Clamp(CpuParkingMinProcessorDC, 0, 100);
        DiskIdleTimeoutSeconds = Math.Clamp(DiskIdleTimeoutSeconds, 10, 300);

        // Memory
        MemoryCheckIntervalSeconds = Math.Clamp(MemoryCheckIntervalSeconds, 1, 60);
        MemoryThresholdPercent = Math.Clamp(MemoryThresholdPercent, 10, 95);
        MemoryCooldownSeconds = Math.Clamp(MemoryCooldownSeconds, 5, 300);
        MemoryCleanupDurationSeconds = Math.Clamp(MemoryCleanupDurationSeconds, 5, 60);
        MemoryRepeatPasses = Math.Clamp(MemoryRepeatPasses, 1, 5);
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

        ThemeMode ??= "Dark";
        BackdropType = BackdropType switch
        {
            "Mica" => "Mica",
            "MicaAlt" => "MicaAlt",
            "Acrylic" => "Acrylic",
            "None" => "None",
            _ => "MicaAlt"
        };
        EcoQosExcludedProcesses = NormalizeProcessList(
            CriticalProcessExclusions.Concat(EcoQosExcludedProcesses ?? []));
        ServicesToThrottle = NormalizeServicesToThrottle(ServicesToThrottle);
        MemoryExcludedProcesses = NormalizeProcessList(
            CriticalProcessExclusions.Concat(MemoryExcludedProcesses ?? []));
        TimerResolutionExcludedProcesses = NormalizeProcessList(
            CriticalProcessExclusions.Concat(TimerResolutionExcludedProcesses ?? []));
        ProtectedApplications = NormalizeProcessList(ProtectedApplications);
    }
}
