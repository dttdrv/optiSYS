using System.Runtime.InteropServices;

namespace OptiSYS.Core.Native;

/// <summary>
/// Audio session enumeration (WASAPI / MMDevice COM): which processes currently have an ACTIVE
/// audio session on the default render endpoint. Used to exempt audible processes from EcoQoS —
/// an explicit EXECUTION_SPEED throttle overrides the OS's own audio-gets-full-QoS heuristic, so
/// throttling a playing process risks audible glitches.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// Best-effort: process ids with an active audio session on the default render endpoint.
    /// Empty on any failure (no audio device, COM error) — callers degrade to the static
    /// exclusion lists, which already cover browsers and conferencing apps.
    /// </summary>
    internal static IReadOnlyCollection<int> GetAudibleProcessIds()
    {
        var audible = new HashSet<int>();
        try
        {
            var deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                deviceEnumerator.GetDefaultAudioEndpoint(EDataFlowRender, ERoleMultimedia, out var device);
                try
                {
                    var iid = IID_IAudioSessionManager2;
                    device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var managerObject);
                    var manager = (IAudioSessionManager2)managerObject;
                    try
                    {
                        manager.GetSessionEnumerator(out var sessions);
                        try
                        {
                            sessions.GetCount(out var count);
                            for (var i = 0; i < count; i++)
                            {
                                sessions.GetSession(i, out var session);
                                try
                                {
                                    if (session is IAudioSessionControl2 control)
                                    {
                                        control.GetState(out var state);
                                        if (state == AudioSessionStateActive &&
                                            control.GetProcessId(out var pid) >= 0 && pid > 0)
                                        {
                                            audible.Add((int)pid);
                                        }
                                    }
                                }
                                finally { Marshal.ReleaseComObject(session); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(sessions); }
                    }
                    finally { Marshal.ReleaseComObject(manager); }
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(deviceEnumerator); }
        }
        catch
        {
            // No render endpoint / COM unavailable — report nothing rather than fail the sweep.
        }

        return audible;
    }

    private const int EDataFlowRender = 0;
    private const int ERoleMultimedia = 1;
    private const int AudioSessionStateActive = 1;
    private const uint CLSCTX_ALL = 23;
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints_Unused();   // vtable slot placeholder — never call
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        // Later slots unused.
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        // Later slots unused.
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager base slots.
        int GetAudioSessionControl_Unused();
        int GetSimpleAudioVolume_Unused();
        // IAudioSessionManager2.
        void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        // Later slots unused.
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl base slots, in vtable order.
        void GetState(out int state);
        int GetDisplayName_Unused();
        int SetDisplayName_Unused();
        int GetIconPath_Unused();
        int SetIconPath_Unused();
        int GetGroupingParam_Unused();
        int SetGroupingParam_Unused();
        int RegisterAudioSessionNotification_Unused();
        int UnregisterAudioSessionNotification_Unused();
        // IAudioSessionControl2.
        int GetSessionIdentifier_Unused();
        int GetSessionInstanceIdentifier_Unused();
        [PreserveSig]
        int GetProcessId(out uint processId);
        // Later slots unused.
    }
}
