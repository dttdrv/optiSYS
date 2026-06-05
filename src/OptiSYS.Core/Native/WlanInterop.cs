using System.Runtime.InteropServices;
using OptiSYS.Core.Interfaces;

namespace OptiSYS.Core.Native;

/// <summary>
/// Production <see cref="IWlanInterop"/> over <see cref="WlanNativeMethods"/>. Holds one client
/// handle open while active; all operations no-op safely when the handle is closed or the WLAN
/// service is unavailable.
///
/// <para>
/// Thread-safe on its own: a private lock guards the handle/disposed lifecycle so a concurrent
/// <see cref="Close"/>/<see cref="Dispose"/> can't race an in-flight query/set onto a closed
/// handle, and the handle can't be double-closed. The wlanapi calls run under the lock because the
/// handle is their argument and must stay valid for the call's duration — this matches (and is now
/// independent of) the existing single-caller-with-lock model in WiFiOptimizerDomain.
/// </para>
/// </summary>
public sealed class WlanInterop : IWlanInterop
{
    private const uint ClientVersion = 2;
    private readonly object _lock = new();
    private IntPtr _handle = IntPtr.Zero;
    private bool _disposed;

    public bool IsOpen
    {
        get { lock (_lock) return _handle != IntPtr.Zero; }
    }

    public bool TryOpen()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle != IntPtr.Zero)
                return true;

            try
            {
                var result = WlanNativeMethods.WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle);
                if (result == 0 && handle != IntPtr.Zero)
                {
                    _handle = handle;
                    return true;
                }
            }
            catch (DllNotFoundException) { /* wlanapi absent (rare SKU) → unavailable */ }
            catch (EntryPointNotFoundException) { /* unavailable */ }

            return false;
        }
    }

    public IReadOnlyList<WlanInterfaceInfo> EnumerateInterfaces()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero)
                return [];

            var listPtr = IntPtr.Zero;
            try
            {
                if (WlanNativeMethods.WlanEnumInterfaces(_handle, IntPtr.Zero, out listPtr) != 0 || listPtr == IntPtr.Zero)
                    return [];

                int count = Marshal.ReadInt32(listPtr); // dwNumberOfItems is the first field
                if (count <= 0)
                    return [];

                var stride = Marshal.SizeOf<WlanNativeMethods.WLAN_INTERFACE_INFO>();
                var result = new List<WlanInterfaceInfo>(count);
                for (int i = 0; i < count; i++)
                {
                    var itemPtr = listPtr + WlanNativeMethods.InterfaceListHeaderBytes + i * stride;
                    var info = Marshal.PtrToStructure<WlanNativeMethods.WLAN_INTERFACE_INFO>(itemPtr);
                    result.Add(new WlanInterfaceInfo(
                        info.InterfaceGuid,
                        info.isState == WlanNativeMethods.WLAN_INTERFACE_STATE_CONNECTED,
                        info.strInterfaceDescription ?? string.Empty));
                }
                return result;
            }
            catch
            {
                return [];
            }
            finally
            {
                if (listPtr != IntPtr.Zero)
                    WlanNativeMethods.WlanFreeMemory(listPtr);
            }
        }
    }

    public bool? QueryBool(Guid interfaceGuid, WlanOpcode opcode)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero)
                return null;

            var dataPtr = IntPtr.Zero;
            try
            {
                var result = WlanNativeMethods.WlanQueryInterface(
                    _handle, interfaceGuid, ToOpcode(opcode), IntPtr.Zero, out var size, out dataPtr, IntPtr.Zero);
                if (result != 0 || dataPtr == IntPtr.Zero || size < sizeof(int))
                    return null;

                return Marshal.ReadInt32(dataPtr) != 0;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (dataPtr != IntPtr.Zero)
                    WlanNativeMethods.WlanFreeMemory(dataPtr);
            }
        }
    }

    public bool SetBool(Guid interfaceGuid, WlanOpcode opcode, bool value)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero)
                return false;

            try
            {
                int data = value ? 1 : 0;
                return WlanNativeMethods.WlanSetInterface(
                    _handle, interfaceGuid, ToOpcode(opcode), sizeof(int), in data, IntPtr.Zero) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero)
                return;

            try { WlanNativeMethods.WlanCloseHandle(_handle, IntPtr.Zero); }
            catch { /* best-effort */ }
            finally { _handle = IntPtr.Zero; }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Close();
        }
    }

    private static int ToOpcode(WlanOpcode opcode) => opcode switch
    {
        WlanOpcode.BackgroundScan => WlanNativeMethods.WLAN_INTF_OPCODE_BACKGROUND_SCAN_ENABLED,
        WlanOpcode.MediaStreaming => WlanNativeMethods.WLAN_INTF_OPCODE_MEDIA_STREAMING_MODE,
        _ => throw new ArgumentOutOfRangeException(nameof(opcode)),
    };
}
