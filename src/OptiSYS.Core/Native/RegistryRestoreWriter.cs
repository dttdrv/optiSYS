using Microsoft.Win32;
using OptiSYS.Core.Interfaces;

namespace OptiSYS.Core.Native;

/// <summary>Production <see cref="IRegistryRestoreWriter"/> over <c>Microsoft.Win32.Registry</c>.</summary>
public sealed class RegistryRestoreWriter : IRegistryRestoreWriter
{
    private static RegistryKey Hive(RegistryRoot root) =>
        root == RegistryRoot.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;

    public bool SetDword(RegistryRoot root, string subKey, string name, int value)
    {
        try
        {
            using var key = OpenWritable(root, subKey);
            if (key == null) return false;
            key.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch { return false; }
    }

    public bool SetString(RegistryRoot root, string subKey, string name, string value)
    {
        try
        {
            using var key = OpenWritable(root, subKey);
            if (key == null) return false;
            key.SetValue(name, value, RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }

    public bool DeleteValue(RegistryRoot root, string subKey, string name)
    {
        try
        {
            using var key = OpenWritable(root, subKey);
            if (key == null) return false;
            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    private static RegistryKey? OpenWritable(RegistryRoot root, string subKey) =>
        root == RegistryRoot.CurrentUser
            ? Hive(root).CreateSubKey(subKey)                 // HKCU keys are created if absent (matches prior CreateSubKey use)
            : Hive(root).OpenSubKey(subKey, writable: true);  // HKLM adapter keys must already exist
}
