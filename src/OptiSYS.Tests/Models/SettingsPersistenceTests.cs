using System.IO;
using OptiSYS.Core.Models;
using Xunit;

namespace OptiSYS.Tests.Models;

/// <summary>
/// Lever 4 — persistence hardening: atomic+serialized save with .bak fallback,
/// enum-by-name (backward-compatible with ordinals), SchemaVersion + migration scaffold,
/// validate-on-save, and ElevationPending no longer persisted.
/// All tests isolate to a temp dir; the real LocalAppData path is never touched.
/// </summary>
public class SettingsPersistenceTests
{
    private static string NewSettingsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "optiSYS-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, "settings.json");
    }

    // ── 1. Atomic + serialized save ──────────────────────────────────
    [Fact]
    public void SaveTo_WritesFileAndLeavesNoTempBehind()
    {
        var path = NewSettingsPath();
        var settings = new Settings();

        settings.SaveTo(path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void SaveTo_PreservesPreviousGoodFileAsBak()
    {
        var path = NewSettingsPath();
        var first = new Settings { MemoryThresholdPercent = 80 };
        first.SaveTo(path);
        var second = new Settings { MemoryThresholdPercent = 90 };
        second.SaveTo(path);   // overwrites; .bak should hold the prior good file

        Assert.True(File.Exists(path + ".bak"));
    }

    [Fact]
    public void Load_CorruptMainFile_RecoversFromBackup()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write a good .bak, then a torn main file (simulates a crash mid-write).
        var good = new Settings { MemoryThresholdPercent = 88 };
        good.SaveTo(path);                 // creates main file
        File.Copy(path, path + ".bak", overwrite: true);
        File.WriteAllText(path, "{ truncated json");

        var loaded = Settings.Load(path);

        Assert.Equal(88, loaded.MemoryThresholdPercent);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var path = NewSettingsPath();   // nothing on disk
        var loaded = Settings.Load(path);

        Assert.Equal(new Settings().MemoryThresholdPercent, loaded.MemoryThresholdPercent);
    }

    // ── 2. Enum-by-name + legacy ordinal read ────────────────────────
    [Fact]
    public void Enums_RoundTripByName()
    {
        var path = NewSettingsPath();
        var settings = new Settings
        {
            OptimizationLevel = OptimizationLevel.Aggressive,
            BatteryPreset = BatteryPreset.Saver,
        };
        settings.SaveTo(path);

        var json = File.ReadAllText(path);
        Assert.Contains("\"Aggressive\"", json);
        Assert.Contains("\"Saver\"", json);

        var loaded = Settings.Load(path);
        Assert.Equal(OptimizationLevel.Aggressive, loaded.OptimizationLevel);
        Assert.Equal(BatteryPreset.Saver, loaded.BatteryPreset);
    }

    [Fact]
    public void Enums_ReadLegacyOrdinalValues()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Legacy file: enums persisted as integer ordinals (Aggressive=2, Saver=1).
        File.WriteAllText(path,
            "{ \"OptimizationLevel\": 2, \"BatteryPreset\": 1 }");

        var loaded = Settings.Load(path);

        Assert.Equal(OptimizationLevel.Aggressive, loaded.OptimizationLevel);
        Assert.Equal(BatteryPreset.Saver, loaded.BatteryPreset);
    }

    [Fact]
    public void Enums_UnknownValue_ClampsToSafeDefault()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Out-of-range ordinal — must not produce an undefined enum value.
        File.WriteAllText(path, "{ \"OptimizationLevel\": 99, \"BatteryPreset\": 99 }");

        var loaded = Settings.Load(path);

        Assert.Equal(OptimizationLevel.Balanced, loaded.OptimizationLevel);
        Assert.Equal(BatteryPreset.Recommended, loaded.BatteryPreset);
    }

    // ── 3. SchemaVersion + migration scaffold ────────────────────────
    [Fact]
    public void SchemaVersion_IsWrittenToDisk()
    {
        var path = NewSettingsPath();
        new Settings().SaveTo(path);

        var json = File.ReadAllText(path);
        Assert.Contains("\"SchemaVersion\"", json);
    }

    [Fact]
    public void SchemaVersion_VersionlessLegacyBlob_UpgradesToCurrent()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"MemoryCooldownSeconds\": 42 }");   // no SchemaVersion

        var loaded = Settings.Load(path);

        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(42, loaded.MemoryCooldownSeconds);   // a non-migrated field survives the upgrade
    }

    [Fact]
    public void Migrate_PreV2Config_AdoptsNew50_75MemoryThresholds()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A v1 config carrying the old collapsed 75/75 thresholds (thresholds aren't user-configurable,
        // so a pre-v2 config adopts the current 50/75 defaults rather than self-healing to 75/85).
        File.WriteAllText(path, "{ \"SchemaVersion\": 1, \"MemoryThresholdPercent\": 75, \"MemoryCriticalThresholdPercent\": 75 }");

        var loaded = Settings.Load(path);

        Assert.Equal(50, loaded.MemoryThresholdPercent);
        Assert.Equal(75, loaded.MemoryCriticalThresholdPercent);
        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Migrate_PreV3Config_AdoptsDefaultOnDrainAwareEcoQos()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Pre-v3 configs persisted EcoQosEnabled=false as the OLD default (blanket throttling
        // was opt-in) and there is no UI toggle to change it — so the drain-aware default-on
        // must be adopted on upgrade rather than leaving the feature permanently dead.
        File.WriteAllText(path, "{ \"SchemaVersion\": 2, \"EcoQosEnabled\": false }");

        var loaded = Settings.Load(path);

        Assert.True(loaded.EcoQosEnabled);
        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Migrate_CurrentVersionOptOut_IsRespected()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // An opt-out saved at the CURRENT schema version is a deliberate user choice — never
        // overridden by the migration.
        File.WriteAllText(path,
            $"{{ \"SchemaVersion\": {Settings.CurrentSchemaVersion}, \"EcoQosEnabled\": false }}");

        var loaded = Settings.Load(path);

        Assert.False(loaded.EcoQosEnabled);
    }

    // ── 4. Validate-on-save ──────────────────────────────────────────
    [Fact]
    public void SaveTo_ClampsOutOfRangeRuntimeValues()
    {
        var path = NewSettingsPath();
        var settings = new Settings { MemoryThresholdPercent = 150 };

        settings.SaveTo(path);

        var json = File.ReadAllText(path);
        Assert.Contains("\"MemoryThresholdPercent\": 95", json);   // clamped to max
        Assert.Equal(95, settings.MemoryThresholdPercent);
    }

    // ── 5. ElevationPending not persisted ────────────────────────────
    [Fact]
    public void SaveTo_DoesNotPersistElevationPending()
    {
        var path = NewSettingsPath();
        var settings = new Settings { ElevationPending = true };

        settings.SaveTo(path);

        var json = File.ReadAllText(path);
        Assert.DoesNotContain("ElevationPending", json);
    }

    [Fact]
    public void Load_IgnoresStaleElevationPendingInFile()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"ElevationPending\": true }");

        var loaded = Settings.Load(path);

        Assert.False(loaded.ElevationPending);   // recomputed at runtime, never read from disk
    }

    // ── Backward compatibility: a full legacy blob from the current build ─
    [Fact]
    public void Load_LegacyBlob_OrdinalEnums_NoSchemaVersion_ElevationPending_LoadsEquivalent()
    {
        var path = NewSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Shape written by the current shipping build: ordinal enums, ElevationPending present,
        // no SchemaVersion. Must load without error and produce equivalent Settings.
        var legacy = """
        {
          "AutoOptimizeOnBattery": true,
          "BatteryPreset": 1,
          "DebouncePowerChangeSeconds": 3,
          "CpuParkingEnabled": true,
          "WiFiOptimizerEnabled": false,
          "AutoOptimizeMemoryEnabled": true,
          "MemoryThresholdPercent": 80,
          "OptimizationLevel": 2,
          "ElevationPending": true,
          "ThemeMode": "Dark",
          "WindowWidth": 1400,
          "HasCompletedOnboarding": true,
          "StartWithWindows": false
        }
        """;
        File.WriteAllText(path, legacy);

        var loaded = Settings.Load(path);

        Assert.True(loaded.AutoOptimizeOnBattery);
        Assert.Equal(BatteryPreset.Saver, loaded.BatteryPreset);
        Assert.Equal(3, loaded.DebouncePowerChangeSeconds);
        Assert.True(loaded.CpuParkingEnabled);
        Assert.False(loaded.WiFiOptimizerEnabled);
        Assert.True(loaded.AutoOptimizeMemoryEnabled);
        Assert.Equal(80, loaded.MemoryThresholdPercent);   // version-less blob reads as current schema (initializer), so thresholds are not migrated
        Assert.Equal(OptimizationLevel.Aggressive, loaded.OptimizationLevel);
        Assert.Equal("Dark", loaded.ThemeMode);
        Assert.Equal(1400, loaded.WindowWidth);
        Assert.True(loaded.HasCompletedOnboarding);
        Assert.False(loaded.StartWithWindows);
        // Transient + versioning are realized correctly regardless of legacy shape.
        Assert.False(loaded.ElevationPending);
        Assert.Equal(Settings.CurrentSchemaVersion, loaded.SchemaVersion);
    }
}
