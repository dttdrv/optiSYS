using OptiSYS.Services;
using Xunit;

namespace OptiSYS.Tests.Services;

public class StartupRegistrationServiceTests
{
    [Fact]
    public void Apply_WhenEnabled_WritesQuotedBackgroundLaunchCommand()
    {
        var store = new FakeStartupRegistrationStore();
        var service = new StartupRegistrationService(
            store,
            new FakeExecutablePathProvider(@"C:\Apps\OptiSYS\OptiSYS.exe"));

        service.Apply(enabled: true);

        Assert.Equal("\"C:\\Apps\\OptiSYS\\OptiSYS.exe\" --background", store.Value);
    }

    [Fact]
    public void Apply_WhenDisabled_RemovesExistingRunEntry()
    {
        var store = new FakeStartupRegistrationStore
        {
            Value = "\"C:\\Apps\\OptiSYS\\OptiSYS.exe\" --background"
        };
        var service = new StartupRegistrationService(
            store,
            new FakeExecutablePathProvider(@"C:\Apps\OptiSYS\OptiSYS.exe"));

        service.Apply(enabled: false);

        Assert.Null(store.Value);
    }

    private sealed class FakeStartupRegistrationStore : IStartupRegistrationStore
    {
        public string? Value { get; set; }

        public string? Read() => Value;

        public void Write(string command) => Value = command;

        public void Remove() => Value = null;
    }

    private sealed class FakeExecutablePathProvider : IExecutablePathProvider
    {
        private readonly string _path;

        public FakeExecutablePathProvider(string path) => _path = path;

        public string? GetExecutablePath() => _path;
    }
}
