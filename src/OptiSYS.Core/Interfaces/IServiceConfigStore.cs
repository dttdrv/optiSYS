namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Testable seam over the Service Control Manager for reading/writing a service's start type.
/// Production wraps the SCM P/Invokes; tests use an in-memory fake. Start-type values mirror the
/// Win32 constants: 2 = Automatic, 3 = Manual/Demand, 4 = Disabled.
/// </summary>
public interface IServiceConfigStore
{
    /// <summary>Current start type, or null if the service can't be opened/queried (absent / no rights).</summary>
    uint? GetStartType(string serviceName);

    /// <summary>Set the start type. False on failure (no admin, service absent). Never starts/stops the service.</summary>
    bool SetStartType(string serviceName, uint startType);
}
