namespace OptiSYS.Core.Interfaces;

/// <summary>
/// Testable seam over the active power scheme's DC (battery) values. Lets domains that tune
/// processor/power-scheme settings capture → write → restore exact prior values without taking a
/// hard dependency on the static <c>NativeMethods</c> P/Invokes. Production wraps powrprof.dll;
/// tests use an in-memory fake.
/// </summary>
public interface IPowerSchemeController
{
    /// <summary>The active power scheme GUID, or <see cref="Guid.Empty"/> if it can't be read.</summary>
    Guid GetActiveScheme();

    /// <summary>Read a DC value index for a setting; null on failure.</summary>
    uint? ReadDcValue(Guid scheme, Guid subgroup, Guid setting);

    /// <summary>Write a DC value index for a setting; false on failure.</summary>
    bool WriteDcValue(Guid scheme, Guid subgroup, Guid setting, uint value);

    /// <summary>Re-assert the active scheme so written DC values take effect.</summary>
    void SetActiveScheme(Guid scheme);
}
