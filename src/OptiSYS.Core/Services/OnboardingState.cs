using OptiSYS.Core.Models;

namespace OptiSYS.Core.Services;

/// <summary>The first-run onboarding steps, in order.</summary>
public enum OnboardingStep { Welcome, WiFi, Battery, Memory, Done }

/// <summary>
/// Pure state for the stepped first-run onboarding: which step is showing and the user's feature
/// choices. UI-free so it is fully unit-testable; the WinUI panels are thin glue over this.
/// <see cref="ApplyTo"/> maps the choices onto <see cref="Settings"/> and marks onboarding complete.
/// </summary>
public sealed class OnboardingState
{
    private static readonly OnboardingStep[] Order =
        [OnboardingStep.Welcome, OnboardingStep.WiFi, OnboardingStep.Battery, OnboardingStep.Memory, OnboardingStep.Done];

    private int _index;

    public OnboardingStep Current => Order[_index];
    public bool IsFirstStep => _index == 0;
    public bool IsLastStep => _index == Order.Length - 1;

    // Feature choices — default ON (the recommended path); memory defaults to Balanced.
    public bool WiFiEnabled { get; set; } = true;
    public bool BatteryEnabled { get; set; } = true;
    public bool MemoryEnabled { get; set; } = true;
    public OptimizationLevel MemoryLevel { get; set; } = OptimizationLevel.Balanced;

    public void Next()
    {
        if (_index < Order.Length - 1) _index++;
    }

    public void Back()
    {
        if (_index > 0) _index--;
    }

    /// <summary>Write the chosen features onto settings and mark onboarding complete. Does not save.</summary>
    public void ApplyTo(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.WiFiOptimizerEnabled = WiFiEnabled;
        settings.CpuParkingEnabled = BatteryEnabled;
        settings.AutoOptimizeMemoryEnabled = MemoryEnabled;
        settings.OptimizationLevel = MemoryLevel;
        settings.HasCompletedOnboarding = true;
    }
}
