using System.ComponentModel;
using OptiSYS.Core.Models;
using OptiSYS.ViewModels;
using Xunit;

namespace OptiSYS.Tests.ViewModels;

/// <summary>
/// Behavior contract for <see cref="SettingsViewModel"/>:
/// <list type="bullet">
///   <item>All bindable properties read/write through to the underlying <see cref="Settings"/>
///         instance so changes outlive the VM.</item>
///   <item>Initial <c>IsDirty</c> is false; any setter flips it true.</item>
///   <item><c>SaveCommand</c> clears <c>IsDirty</c> (actual disk write is best-effort and not
///         verified here — <see cref="Settings.Save"/> swallows IO failures).</item>
///   <item><c>ResetCommand</c> restores every exposed field to the <see cref="Settings"/>
///         type defaults, clears <c>IsDirty</c>, and notifies bindings.</item>
///   <item>Null-arg guard.</item>
/// </list>
/// </summary>
public class SettingsViewModelTests
{
    // ── Null-arg guard ───────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullSettings_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SettingsViewModel(null!));
    }

    // ── Initial state mirrors underlying Settings ────────────────────────────

    [Fact]
    public void Ctor_ReflectsInitialSettingsValues()
    {
        var settings = new Settings
        {
            ThemeMode = "Dark",
            StartWithWindows = true,
            MemoryThresholdPercent = 72,
            AutoOptimizeOnBattery = false,
            EcoQosEnabled = false,
            OptimizationLevel = OptimizationLevel.Aggressive,
        };

        var vm = new SettingsViewModel(settings);

        Assert.Equal("Dark", vm.ThemeMode);
        Assert.True(vm.StartWithWindows);
        Assert.Equal(72, vm.MemoryThresholdPercent);
        Assert.False(vm.AutoOptimizeOnBattery);
        Assert.False(vm.EcoQosEnabled);
        Assert.Equal(OptimizationLevel.Aggressive, vm.OptimizationLevel);
    }

    [Fact]
    public void Ctor_IsNotDirty()
    {
        var vm = new SettingsViewModel(new Settings());
        Assert.False(vm.IsDirty);
    }

    // ── Setters write through to Settings ────────────────────────────────────

    [Fact]
    public void Setter_WritesThroughToUnderlyingSettings()
    {
        var settings = new Settings();
        var vm = new SettingsViewModel(settings);

        vm.ThemeMode = "Light";
        vm.MemoryThresholdPercent = 55;
        vm.StartWithWindows = true;
        vm.EcoQosEnabled = false;
        vm.OptimizationLevel = OptimizationLevel.Conservative;

        Assert.Equal("Light", settings.ThemeMode);
        Assert.Equal(55, settings.MemoryThresholdPercent);
        Assert.True(settings.StartWithWindows);
        Assert.False(settings.EcoQosEnabled);
        Assert.Equal(OptimizationLevel.Conservative, settings.OptimizationLevel);
    }

    // ── IsDirty tracking ─────────────────────────────────────────────────────

    [Fact]
    public void Setter_SetsIsDirtyTrue()
    {
        var vm = new SettingsViewModel(new Settings());
        Assert.False(vm.IsDirty);

        vm.ThemeMode = "Light";

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void MultipleSetters_StayDirty()
    {
        var vm = new SettingsViewModel(new Settings());

        vm.StartWithWindows       = true;
        vm.MinimizeToTray         = false;
        vm.MemoryThresholdPercent = 70;

        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void Setter_RaisesPropertyChanged()
    {
        var vm = new SettingsViewModel(new Settings());
        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ThemeMode = "Light";

        Assert.Contains(nameof(vm.ThemeMode), changed);
        Assert.Contains(nameof(vm.IsDirty), changed);
    }

    // ── SaveCommand ──────────────────────────────────────────────────────────

    [Fact]
    public void SaveCommand_ClearsIsDirty()
    {
        var vm = new SettingsViewModel(new Settings());
        vm.ThemeMode = "Light"; // makes it dirty
        Assert.True(vm.IsDirty);

        vm.SaveCommand.Execute(null);

        Assert.False(vm.IsDirty);
    }

    // ── ResetCommand ─────────────────────────────────────────────────────────

    [Fact]
    public void ResetCommand_RestoresDefaultsOnUnderlyingSettings()
    {
        var defaults = new Settings(); // snapshot the type defaults
        var settings = new Settings
        {
            ThemeMode              = "Dark",
            StartWithWindows       = true,
            MemoryThresholdPercent = 50,
            EcoQosEnabled          = false,
            OptimizationLevel      = OptimizationLevel.Aggressive,
        };
        var vm = new SettingsViewModel(settings);

        vm.ResetCommand.Execute(null);

        Assert.Equal(defaults.ThemeMode,              vm.ThemeMode);
        Assert.Equal(defaults.StartWithWindows,       vm.StartWithWindows);
        Assert.Equal(defaults.MemoryThresholdPercent, vm.MemoryThresholdPercent);
        Assert.Equal(defaults.EcoQosEnabled,          vm.EcoQosEnabled);
        Assert.Equal(defaults.OptimizationLevel,      vm.OptimizationLevel);

        // Verify the underlying Settings object was also mutated (same reference, fresh values).
        Assert.Equal(defaults.ThemeMode, settings.ThemeMode);
        Assert.Equal(defaults.EcoQosEnabled, settings.EcoQosEnabled);
    }

    [Fact]
    public void ResetCommand_ClearsIsDirty()
    {
        var vm = new SettingsViewModel(new Settings());
        vm.ThemeMode = "Light";
        Assert.True(vm.IsDirty);

        vm.ResetCommand.Execute(null);

        Assert.False(vm.IsDirty);
    }
}
