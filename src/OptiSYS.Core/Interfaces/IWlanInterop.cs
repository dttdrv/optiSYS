namespace OptiSYS.Core.Interfaces;

/// <summary>The two latency-relevant per-interface WLAN settings optiSYS toggles.</summary>
public enum WlanOpcode
{
    /// <summary>Periodic background scan for other networks while connected — disabling it removes 100ms+ latency spikes.</summary>
    BackgroundScan,

    /// <summary>Media-streaming mode — prioritizes the active connection for low-latency streaming.</summary>
    MediaStreaming,
}

/// <summary>A WLAN adapter as seen by the Native Wifi API.</summary>
public sealed record WlanInterfaceInfo(Guid Guid, bool IsConnected, string Description);

/// <summary>
/// Testable seam over <c>wlanapi.dll</c>. <b>Stateful by necessity:</b> the toggled opcodes are
/// scoped to the open client handle and revert when it closes, so the interop holds the handle
/// open between <see cref="TryOpen"/> and <see cref="Close"/>/<see cref="IDisposable.Dispose"/>.
/// Production wraps the P/Invokes; tests use an in-memory fake adapter table.
/// </summary>
public interface IWlanInterop : IDisposable
{
    /// <summary>True once a client handle is held open.</summary>
    bool IsOpen { get; }

    /// <summary>Open a client handle (starts holding the settings). False when WLAN/Wlansvc is unavailable.</summary>
    bool TryOpen();

    /// <summary>Enumerate adapters. Empty when not open or none present.</summary>
    IReadOnlyList<WlanInterfaceInfo> EnumerateInterfaces();

    /// <summary>Read a bool opcode for an interface; null on failure (driver/permission).</summary>
    bool? QueryBool(Guid interfaceGuid, WlanOpcode opcode);

    /// <summary>Write a bool opcode for an interface; false on failure.</summary>
    bool SetBool(Guid interfaceGuid, WlanOpcode opcode, bool value);

    /// <summary>Close the client handle (reverts the session-scoped opcodes).</summary>
    void Close();
}
