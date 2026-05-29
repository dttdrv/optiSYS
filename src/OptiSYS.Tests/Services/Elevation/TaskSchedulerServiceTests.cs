using System.ComponentModel;
using System.Diagnostics;
using System.Xml.Linq;
using OptiSYS.Services.Elevation;
using Xunit;

namespace OptiSYS.Tests.Services.Elevation;

/// <summary>
/// Unit tests for the pure, OS-independent parts of the elevation mechanism. The
/// schtasks-invoking members (TaskExists/CreateOrUpdateTask/DeleteTask) touch the real Task
/// Scheduler and are verified by running the app, not in CI — same scope the optiRAM sibling
/// chose. What we CAN pin without elevation is the authored task XML and the decision logic.
/// </summary>
public class TaskSchedulerServiceTests
{
    private const string ExePath = @"C:\Program Files\optiSYS\OptiSYS.exe";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void BuildTaskXml_StartAtLogon_HasLogonTriggerHighestRunLevelAndInteractiveToken()
    {
        var doc = XDocument.Parse(TaskSchedulerService.BuildTaskXml(ExePath, "--background", startAtLogon: true));
        var principal = doc.Root?.Element(Ns + "Principals")?.Element(Ns + "Principal");

        Assert.NotNull(doc.Root?.Element(Ns + "Triggers")?.Element(Ns + "LogonTrigger"));
        Assert.Equal("HighestAvailable", principal?.Element(Ns + "RunLevel")?.Value);
        Assert.Equal("InteractiveToken", principal?.Element(Ns + "LogonType")?.Value);
        Assert.Equal("PT15S", doc.Root?.Element(Ns + "Triggers")?.Element(Ns + "LogonTrigger")?.Element(Ns + "Delay")?.Value);
    }

    [Fact]
    public void BuildTaskXml_EmbedsCommandArgumentsWorkingDirectoryAndAuthor()
    {
        var doc = XDocument.Parse(TaskSchedulerService.BuildTaskXml(ExePath, "--background", startAtLogon: true));
        var exec = doc.Root?.Element(Ns + "Actions")?.Element(Ns + "Exec");

        Assert.Equal(ExePath, exec?.Element(Ns + "Command")?.Value);
        Assert.Equal("--background", exec?.Element(Ns + "Arguments")?.Value);
        Assert.Equal(@"C:\Program Files\optiSYS", exec?.Element(Ns + "WorkingDirectory")?.Value);
        Assert.Equal("optiSYS", doc.Root?.Element(Ns + "RegistrationInfo")?.Element(Ns + "Author")?.Value);
    }

    [Fact]
    public void BuildTaskXml_UsesCurrentUserSidForUserId()
    {
        var doc = XDocument.Parse(TaskSchedulerService.BuildTaskXml(ExePath, "--background", startAtLogon: true));
        var userId = doc.Root?.Element(Ns + "Principals")?.Element(Ns + "Principal")?.Element(Ns + "UserId")?.Value;

        Assert.StartsWith("S-1-", userId); // SID, not a name → survives domain/profile renames
    }

    [Fact]
    public void BuildTaskXml_NoLogon_OmitsTriggers_KeepsHighestRunLevel()
    {
        var doc = XDocument.Parse(TaskSchedulerService.BuildTaskXml(ExePath, "--background", startAtLogon: false));

        Assert.Null(doc.Root?.Element(Ns + "Triggers"));
        Assert.Equal("HighestAvailable",
            doc.Root?.Element(Ns + "Principals")?.Element(Ns + "Principal")?.Element(Ns + "RunLevel")?.Value);
    }

    [Fact]
    public void BuildTaskXml_EmptyArguments_OmitsArgumentsElement()
    {
        var doc = XDocument.Parse(TaskSchedulerService.BuildTaskXml(ExePath, "", startAtLogon: true));
        var exec = doc.Root?.Element(Ns + "Actions")?.Element(Ns + "Exec");

        Assert.Null(exec?.Element(Ns + "Arguments"));
    }

    [Fact]
    public void ParseTaskCommand_ReturnsCommand_FromBuiltXml()
    {
        var xml = TaskSchedulerService.BuildTaskXml(ExePath, "--background", startAtLogon: true);
        Assert.Equal(ExePath, TaskSchedulerService.ParseTaskCommand(xml));
    }

    [Fact]
    public void ParseTaskCommand_ReturnsNull_ForGarbage()
    {
        Assert.Null(TaskSchedulerService.ParseTaskCommand("not xml at all <<<"));
    }

    [Theory]
    [InlineData(null, false)]                                          // couldn't introspect → not stale (never nag)
    [InlineData("", false)]
    [InlineData(@"C:\Program Files\optiSYS\OptiSYS.exe", false)]       // same path
    [InlineData(@"c:\program files\optisys\optisys.exe", false)]       // case-insensitive same
    [InlineData("\"C:\\Program Files\\optiSYS\\OptiSYS.exe\"", false)] // quoted same
    [InlineData(@"C:\Old\OptiSYS.exe", true)]                          // moved → stale
    public void IsStale_OnlyTrueWhenRegisteredPathProvablyDiffers(string? registeredCommand, bool expectedStale)
    {
        Assert.Equal(expectedStale, TaskSchedulerService.IsStale(registeredCommand, ExePath));
    }
}

public class ElevationHelperTests
{
    [Fact]
    public void BuildProvisionPsi_RequestsRunasWithProvisionArgument()
    {
        var psi = ElevationHelper.BuildProvisionPsi(@"C:\app\OptiSYS.exe");

        Assert.Equal(@"C:\app\OptiSYS.exe", psi.FileName);
        Assert.Equal(ElevationHelper.ProvisionArgument, psi.Arguments);
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute);
    }

    [Fact]
    public void RequestProvisioning_UserDeclinesUac_ReturnsFalse_WithoutRethrowing()
    {
        var result = ElevationHelper.RequestProvisioning(
            @"C:\app\OptiSYS.exe",
            starter: _ => throw new Win32Exception(1223)); // ERROR_CANCELLED

        Assert.False(result);
    }

    [Fact]
    public void RequestProvisioning_NoExecutablePath_ReturnsFalse()
    {
        Assert.False(ElevationHelper.RequestProvisioning("", starter: _ => null));
    }
}
