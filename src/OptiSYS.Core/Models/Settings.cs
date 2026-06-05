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
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Persist enums by NAME (reorder-safe). System.Text.Json still READS legacy numeric
        // ordinals, so existing settings.json files upgrade transparently. Unknown names/ordinals
        // are clamped to a safe default in Validate().
        Converters = { new JsonStringEnumConverter() }
    };

    // Serializes ALL writers (Save + the debounced path) so a synchronous Save can never race an
    // in-flight debounced write to the same file (Lever 4: single serialized writer).
    private static readonly object SaveLock = new();

    private CancellationTokenSource? _debounceCts;

    /// <summary>
    /// Persisted settings schema version. Bumped when a migration is added; a file with a lower
    /// (or missing → 0) version is upgraded on Load via <see cref="Migrate"/>.
    /// </summary>
    internal const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

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
    // CPU core parking + DC minimum processor state. ON by default and auto-applied on battery:
    // per the owner's explicit battery-category relaxation of the no-mutation rule, on DC it lowers
    // the DC "Minimum Processor State" to CpuParkingMinProcessorDC (0%, deeper idle) and parks cores
    // aggressively. Exact-captured and restored on AC/exit/crash. (A DC max-state cap was trialled
    // and removed: measurement showed thermal/TDP binds below it, so it was redundant under load.)
    public bool CpuParkingEnabled { get; set; } = true;
    public bool DiskCoalescingEnabled { get; set; } = false;

    // Wi-Fi latency optimizer — OFF by default. It net-degraded the connection on real hardware
    // (Qualcomm WCN685x), so it must not touch the adapter unless the user explicitly opts in.
    public bool WiFiOptimizerEnabled { get; set; } = false;
    public bool WiFiDisableBackgroundScan { get; set; } = true;
    // PERMANENTLY OFF and never written (see WiFiOptimizerDomain.ApplyToConnectedLocked): media-
    // streaming mode adds latency on some WiFiCx adapters. The field is retained only for settings
    // de/serialization compatibility and so revert can restore a value left set by an older build.
    public bool WiFiStreamingMode { get; set; } = false;

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

    public int CpuParkingMinProcessorDC { get; set; } = 0;
    public int DiskIdleTimeoutSeconds { get; set; } = 30;

    public List<string> TimerResolutionExcludedProcesses { get; set; } =
    [
        .. CriticalProcessExclusions,
        "audiodg", "NVIDIA Display Container"
    ];

    // Memory optimization settings. Baked-in (no UI knobs). DYNAMIC monitoring: a ~2s tick (like
    // optiRAM) watches continuously and reacts within seconds — but only SAMPLES memory each tick
    // (cheap); actual reclaim is gated by the 60% threshold + the predictive trend + the cooldown,
    // so the heavy work fires only under genuine pressure. Cooldown spaces consecutive reclaims.
    public bool AutoOptimizeMemoryEnabled { get; set; } = true;
    public int MemoryCheckIntervalSeconds { get; set; } = 5;
    public int MemoryThresholdPercent { get; set; } = 75;
    // OOM prevention: at/above this usage %, automatic cleanup escalates to a full (Aggressive)
    // reclaim immediately and bypasses the cooldown, so a fast allocation burst (e.g. many large
    // processes) can't blow through the free-RAM buffer between spaced-out cleanups.
    public int MemoryCriticalThresholdPercent { get; set; } = 75;
    public int MemoryCooldownSeconds { get; set; } = 30;
    public int MemoryCleanupDurationSeconds { get; set; } = 15;
    public int MemoryRepeatPasses { get; set; } = 2;
    // User-selectable memory mode: Balanced (default) or Aggressive (Max). Conservative is no
    // longer a user choice — the automatic path runs the full optiRAM-parity pipeline at this level.
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
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 720;
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
    /// "Grant admin access" banner. Recomputed at startup; not a persisted user intent — so it is
    /// never written to or read from disk (a stale value would muddy the persistence contract).
    /// </summary>
    [JsonIgnore]
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
    public static Settings Load() => Load(SettingsFile);

    internal static Settings Load(string file)
    {
        // Try the main file; if it is missing or torn (crash mid-write), fall back to the last-good
        // .bak so user intent / opt-ins are never silently reset by a corrupt file (Lever 4).
        var settings = TryLoadFrom(file) ?? TryLoadFrom(file + ".bak") ?? new Settings();
        settings.Migrate();
        settings.Validate();
        return settings;
    }

    private static Settings? TryLoadFrom(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<Settings>(json, JsonOptions);
        }
        catch
        {
            return null;   // corrupt — let the caller try the backup
        }
    }

    public void Save() => SaveTo(SettingsFile);

    internal void SaveTo(string file)
    {
        lock (SaveLock)   // serialize against any concurrent / debounced write to the same file
        {
            // Clamp runtime-set values before they reach disk, so what is persisted is always valid
            // (Validate previously ran only on Load, a launch too late). Inside the lock so a
            // concurrent SaveTo can't mutate `this` mid-serialization.
            Validate();

            try
            {
                var dir = Path.GetDirectoryName(file)!;
                Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, JsonOptions);

                // Atomic write: serialize to a temp file, then replace. A crash can leave the temp
                // file or the intact previous file, but never a half-written target. Preserve the
                // prior good file as .bak so Load can fall back if the replace is interrupted.
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, json);

                if (File.Exists(file))
                    File.Replace(tmp, file, file + ".bak");
                else
                    File.Move(tmp, file);
            }
            catch { }
        }
    }

    public async void SaveDebounced()
    {
        var old = Interlocked.Exchange(ref _debounceCts, new CancellationTokenSource());
        old?.Cancel();
        old?.Dispose();
        var token = _debounceCts!.Token;

        try
        {
            await Task.Delay(500, token);
            SaveTo(SettingsFile);   // same atomic + serialized writer as Save()
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    /// <summary>
    /// Ordered schema migration scaffold, run on Load between deserialize and Validate. A file with
    /// no <see cref="SchemaVersion"/> deserializes to 0 and is upgraded to <see cref="CurrentSchemaVersion"/>.
    /// No historical migrations exist yet; add future vN→vN+1 steps here, append-only.
    /// </summary>
    private void Migrate()
    {
        // (no historical migrations yet — when one is needed, gate it on the on-disk version, e.g.
        //  if (SchemaVersion < 1) { /* transform */ } )
        SchemaVersion = CurrentSchemaVersion;
    }

    public void Validate()
    {
        // Enums — a legacy/forward file can carry an undefined ordinal; realize to a safe default.
        if (!Enum.IsDefined(OptimizationLevel)) OptimizationLevel = OptimizationLevel.Balanced;
        if (!Enum.IsDefined(BatteryPreset)) BatteryPreset = BatteryPreset.Recommended;

        // Battery
        DebouncePowerChangeSeconds = Math.Clamp(DebouncePowerChangeSeconds, 1, 10);
        CpuParkingMinProcessorDC = Math.Clamp(CpuParkingMinProcessorDC, 0, 100);
        DiskIdleTimeoutSeconds = Math.Clamp(DiskIdleTimeoutSeconds, 10, 300);

        // Memory
        MemoryCheckIntervalSeconds = Math.Clamp(MemoryCheckIntervalSeconds, 1, 60);
        MemoryThresholdPercent = Math.Clamp(MemoryThresholdPercent, 10, 95);
        MemoryCriticalThresholdPercent = Math.Clamp(MemoryCriticalThresholdPercent, 70, 99);
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
