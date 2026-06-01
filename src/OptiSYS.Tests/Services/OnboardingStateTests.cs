using OptiSYS.Core.Models;
using OptiSYS.Core.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class OnboardingStateTests
{
    [Fact]
    public void Steps_GoWelcomeToDone_InOrder()
    {
        var s = new OnboardingState();
        Assert.Equal(OnboardingStep.Welcome, s.Current);

        s.Next(); Assert.Equal(OnboardingStep.WiFi, s.Current);
        s.Next(); Assert.Equal(OnboardingStep.Battery, s.Current);
        s.Next(); Assert.Equal(OnboardingStep.Memory, s.Current);
        s.Next(); Assert.Equal(OnboardingStep.Done, s.Current);
    }

    [Fact]
    public void Next_AtDone_DoesNotOverrun()
    {
        var s = new OnboardingState();
        for (int i = 0; i < 10; i++) s.Next();
        Assert.Equal(OnboardingStep.Done, s.Current);
    }

    [Fact]
    public void Back_AtWelcome_DoesNotUnderrun()
    {
        var s = new OnboardingState();
        s.Back();
        Assert.Equal(OnboardingStep.Welcome, s.Current);
    }

    [Fact]
    public void Back_StepsReturnThroughPriorScreens()
    {
        var s = new OnboardingState();
        s.Next(); s.Next();                 // Battery
        s.Back();
        Assert.Equal(OnboardingStep.WiFi, s.Current);
    }

    [Fact]
    public void IsFirstStep_AndIsLastStep_Flags()
    {
        var s = new OnboardingState();
        Assert.True(s.IsFirstStep);
        Assert.False(s.IsLastStep);

        while (!s.IsLastStep) s.Next();
        Assert.True(s.IsLastStep);
        Assert.False(s.IsFirstStep);
        Assert.Equal(OnboardingStep.Done, s.Current);
    }

    [Fact]
    public void Defaults_AllFeaturesOn_MemoryBalanced()
    {
        var s = new OnboardingState();
        Assert.True(s.WiFiEnabled);
        Assert.True(s.BatteryEnabled);
        Assert.True(s.MemoryEnabled);
        Assert.Equal(OptimizationLevel.Balanced, s.MemoryLevel);
    }

    [Fact]
    public void ApplyTo_WritesChoices_AndMarksOnboardingComplete()
    {
        var settings = new Settings();
        var s = new OnboardingState
        {
            WiFiEnabled = false,
            BatteryEnabled = false,
            MemoryEnabled = true,
            MemoryLevel = OptimizationLevel.Aggressive,
        };

        s.ApplyTo(settings);

        Assert.False(settings.WiFiOptimizerEnabled);
        Assert.False(settings.CpuParkingEnabled);
        Assert.True(settings.AutoOptimizeMemoryEnabled);
        Assert.Equal(OptimizationLevel.Aggressive, settings.OptimizationLevel);
        Assert.True(settings.HasCompletedOnboarding);
    }

    [Fact]
    public void ApplyTo_AllOn_EnablesEverything()
    {
        var settings = new Settings();
        new OnboardingState().ApplyTo(settings);

        Assert.True(settings.WiFiOptimizerEnabled);
        Assert.True(settings.CpuParkingEnabled);
        Assert.True(settings.AutoOptimizeMemoryEnabled);
        Assert.Equal(OptimizationLevel.Balanced, settings.OptimizationLevel);
        Assert.True(settings.HasCompletedOnboarding);
    }
}
