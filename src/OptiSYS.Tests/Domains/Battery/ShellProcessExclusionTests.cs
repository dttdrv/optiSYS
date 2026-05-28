using OptiSYS.Core.Domains.Battery;
using Xunit;

namespace OptiSYS.Tests.Domains.Battery;

public class ShellProcessExclusionTests
{
    [Theory]
    [InlineData("explorer")]
    [InlineData("ShellExperienceHost")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("SearchHost")]
    [InlineData("TextInputHost")]
    public void EcoQos_DoesNotThrottleWindowsShellProcesses(string processName)
    {
        Assert.True(EcoQosDomain.IsShellProcess(processName));
    }

    [Theory]
    [InlineData("explorer")]
    [InlineData("ShellExperienceHost")]
    [InlineData("StartMenuExperienceHost")]
    [InlineData("SearchHost")]
    [InlineData("TextInputHost")]
    public void TimerResolution_DoesNotClampWindowsShellProcesses(string processName)
    {
        Assert.True(TimerResolutionDomain.IsShellProcess(processName));
    }
}
