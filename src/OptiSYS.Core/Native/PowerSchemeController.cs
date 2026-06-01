using OptiSYS.Core.Interfaces;

namespace OptiSYS.Core.Native;

/// <summary>Production <see cref="IPowerSchemeController"/> over <see cref="NativeMethods"/> (powrprof.dll).</summary>
public sealed class PowerSchemeController : IPowerSchemeController
{
    public Guid GetActiveScheme() => NativeMethods.GetActiveScheme();

    public uint? ReadDcValue(Guid scheme, Guid subgroup, Guid setting) =>
        NativeMethods.ReadDCValue(scheme, subgroup, setting);

    public bool WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value) =>
        NativeMethods.WriteDCValue(scheme, subgroup, setting, value);

    public void SetActiveScheme(Guid scheme) =>
        NativeMethods.PowerSetActiveScheme(IntPtr.Zero, scheme);
}
