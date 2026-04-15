using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Factory that creates the appropriate INativeBridge implementation.
/// Tries Zig first, falls back to managed P/Invoke.
/// </summary>
public static class NativeBridgeFactory
{
    private static bool _zigAvailableChecked;
    private static bool _zigAvailable;

    /// <summary>
    /// Creates the best available native bridge.
    /// Tries ZigNativeBridge (optisys_core.dll) first, falls back to ManagedNativeBridge.
    /// </summary>
    public static INativeBridge Create()
    {
        if (IsZigAvailable())
            return new ZigNativeBridge();

        return new ManagedNativeBridge();
    }

    private static bool IsZigAvailable()
    {
        if (_zigAvailableChecked)
            return _zigAvailable;

        try
        {
            // Try to load the Zig DLL
            var dll = System.Runtime.InteropServices.NativeLibrary.TryLoad("optisys_core.dll", out _);
            _zigAvailable = dll;
        }
        catch
        {
            _zigAvailable = false;
        }

        _zigAvailableChecked = true;
        return _zigAvailable;
    }
}
