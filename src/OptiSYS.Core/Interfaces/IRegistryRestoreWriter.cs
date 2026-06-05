namespace OptiSYS.Core.Interfaces;

/// <summary>Registry hive a restore write targets.</summary>
public enum RegistryRoot
{
    LocalMachine,
    CurrentUser,
}

/// <summary>
/// Testable seam over the registry writes a battery domain performs during revert. Mirrors
/// <see cref="IPowerSchemeController"/>: each write returns false on failure so a domain's
/// <see cref="IVerifiableRevert.TryRevert"/> can surface a partial restore failure instead of
/// swallowing it — letting the engine retain the crash snapshot (the only copy of the originals).
/// Production wraps <c>Microsoft.Win32.Registry</c>; tests use an in-memory fake.
/// </summary>
public interface IRegistryRestoreWriter
{
    /// <summary>Write a DWORD value; false on failure.</summary>
    bool SetDword(RegistryRoot root, string subKey, string name, int value);

    /// <summary>Write a string value; false on failure.</summary>
    bool SetString(RegistryRoot root, string subKey, string name, string value);

    /// <summary>Delete a value (treating "already absent" as success); false on failure.</summary>
    bool DeleteValue(RegistryRoot root, string subKey, string name);
}
