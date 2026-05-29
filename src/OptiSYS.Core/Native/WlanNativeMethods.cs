using System.Runtime.InteropServices;

namespace OptiSYS.Core.Native;

/// <summary>
/// P/Invoke surface for the Native Wifi API (<c>wlanapi.dll</c>), used to disable the
/// connection-killing background scan and enable media-streaming mode on the active adapter —
/// the same two opcodes the WLAN Optimizer (Majowski) tool and the open-source
/// <c>catid/WLANOptimizer</c> reference toggle.
///
/// <para>
/// <b>Handle lifetime is load-bearing.</b> These two opcodes are scoped to the open client
/// handle: closing the handle reverts them. So the handle must stay open for as long as the
/// optimization is meant to hold (reference: catid keeps it open for the app's lifetime).
/// </para>
/// </summary>
internal static partial class WlanNativeMethods
{
    // WLAN_INTF_OPCODE ordinals (wlanapi.h). autoconf_start=0, autoconf_enabled=1, then:
    internal const int WLAN_INTF_OPCODE_BACKGROUND_SCAN_ENABLED = 2;
    internal const int WLAN_INTF_OPCODE_MEDIA_STREAMING_MODE = 3;

    // WLAN_INTERFACE_STATE
    internal const int WLAN_INTERFACE_STATE_CONNECTED = 1;

    // Header layout of WLAN_INTERFACE_INFO_LIST: { DWORD dwNumberOfItems; DWORD dwIndex; INFO[]; }
    internal const int InterfaceListHeaderBytes = 8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;

        public int isState; // WLAN_INTERFACE_STATE
    }

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanOpenHandle(
        uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanEnumInterfaces(
        IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    // pData / ppData for the two BOOL opcodes is a 4-byte int (0 / 1).
    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanSetInterface(
        IntPtr hClientHandle, in Guid pInterfaceGuid, int OpCode, uint dwDataSize, in int pData, IntPtr pReserved);

    [LibraryImport("wlanapi.dll")]
    internal static partial uint WlanQueryInterface(
        IntPtr hClientHandle, in Guid pInterfaceGuid, int OpCode, IntPtr pReserved,
        out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [LibraryImport("wlanapi.dll")]
    internal static partial void WlanFreeMemory(IntPtr pMemory);
}
