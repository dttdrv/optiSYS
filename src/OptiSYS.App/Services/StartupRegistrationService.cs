using Microsoft.Win32;

namespace OptiSYS.Services;

public interface IStartupRegistrationService
{
    void Apply(bool enabled);
}

public interface IStartupRegistrationStore
{
    string? Read();
    void Write(string command);
    void Remove();
}

public interface IExecutablePathProvider
{
    string? GetExecutablePath();
}

internal sealed class CurrentUserStartupRegistrationStore : IStartupRegistrationStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "optiSYS";

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(RunValueName, command, RegistryValueKind.String);
    }

    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}

internal sealed class ProcessExecutablePathProvider : IExecutablePathProvider
{
    public string? GetExecutablePath() => Environment.ProcessPath;
}

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private readonly IStartupRegistrationStore _store;
    private readonly IExecutablePathProvider _pathProvider;

    public StartupRegistrationService(
        IStartupRegistrationStore store,
        IExecutablePathProvider pathProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pathProvider = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
    }

    public void Apply(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                _store.Remove();
                return;
            }

            var executablePath = _pathProvider.GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                StartupLog.Write("StartupRegistrationService: skipped because executable path is unavailable");
                return;
            }

            var command = BuildCommand(executablePath);
            if (string.Equals(_store.Read(), command, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _store.Write(command);
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("StartupRegistrationService.Apply", ex);
        }
    }

    private static string BuildCommand(string executablePath) => $"\"{executablePath}\" --background";
}
