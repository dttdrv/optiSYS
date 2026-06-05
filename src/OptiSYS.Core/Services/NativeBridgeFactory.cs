using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Factory for the Windows-native bridge implementation.
/// </summary>
public static class NativeBridgeFactory
{
    public static INativeBridge Create(IDiagnosticLog? log = null) => new WindowsNativeBridge(log);
}
