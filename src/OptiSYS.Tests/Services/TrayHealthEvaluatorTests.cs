using OptiSYS.Models;
using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class TrayHealthEvaluatorTests
{
    [Theory]
    [InlineData(OverallHealthState.Great, TrayDot.Green, "Good")]
    [InlineData(OverallHealthState.Good, TrayDot.Green, "Good")]
    [InlineData(OverallHealthState.Normal, TrayDot.Yellow, "Normal")]
    [InlineData(OverallHealthState.NotGood, TrayDot.Red, "Bad")]
    [InlineData(OverallHealthState.Bad, TrayDot.Red, "Bad")]
    public void Maps_HealthState_To_Dot_And_EfficiencyLabel(OverallHealthState state, TrayDot dot, string label)
    {
        Assert.Equal(dot, TrayHealthEvaluator.DotFor(state));
        Assert.Equal(label, TrayHealthEvaluator.EfficiencyLabel(state));
    }
}
