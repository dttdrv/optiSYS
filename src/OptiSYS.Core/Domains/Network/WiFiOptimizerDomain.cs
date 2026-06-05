using System.Diagnostics;
using System.Threading;
using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Models;

namespace OptiSYS.Core.Domains.Network;

/// <summary>Captured pre-optimization opcode values for one WLAN interface (null = couldn't read).</summary>
public sealed class WiFiInterfaceBaseline
{
    public bool? BackgroundScan { get; set; }
    public bool? MediaStreaming { get; set; }
}

/// <summary>
/// Toggles per-interface Native WiFi opcodes on the active adapter(s) — the WLAN Optimizer
/// technique. Fully reversible and session-scoped (no system mutation, no admin). Ships OFF
/// (opt-in): the effect is adapter-dependent. Disabling the background scan can remove latency
/// spikes, but media-streaming mode (its driver contract lives in the deprecated Native 802.11
/// model) has been observed to ADD latency on some WDI/WiFiCx adapters, so it defaults off.
///
/// <para>
/// The toggled opcodes revert when the WLAN client handle closes, so the handle is held open for
/// the active lifetime, and a reapply timer re-asserts the settings (Wi-Fi reconnects reset them).
/// The interval is deliberately longer than the <c>catid/WLANOptimizer</c> reference's 11s: re-issuing
/// WlanSetInterface on a live connection is undocumented and chatty (WDI guidance favors minimizing
/// host↔IHV traffic), and a slightly later re-assert after a reconnect is harmless.
/// </para>
/// </summary>
public sealed class WiFiOptimizerDomain : IOptimizationDomain
{
    private static readonly TimeSpan ReapplyInterval = TimeSpan.FromSeconds(45);

    private readonly Settings _settings;
    private readonly IWlanInterop _wlan;
    private readonly object _gate = new();

    private bool _isActive;
    private bool? _available;
    private int _driversIgnored;
    private Timer? _reapplyTimer;

    public string Id => "wifi-optimizer";
    public string DisplayName => "Wi-Fi Latency Optimizer";
    public string Category => "Network";
    public bool IsSupported => _available ??= DetectAvailability();
    public bool IsActive => _isActive;

    public bool IsEnabled(Settings settings) => settings.WiFiOptimizerEnabled;

    public WiFiOptimizerDomain(Settings settings, IWlanInterop wlan)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _wlan = wlan ?? throw new ArgumentNullException(nameof(wlan));
    }

    public DomainSnapshot CaptureBaseline()
    {
        var snapshot = new DomainSnapshot { DomainId = Id };
        var baselines = new Dictionary<string, WiFiInterfaceBaseline>(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            // Transient open only if we aren't already holding the handle — capturing reads
            // current values without changing anything, so closing it reverts nothing.
            bool openedHere = !_wlan.IsOpen && _wlan.TryOpen();
            try
            {
                foreach (var iface in _wlan.EnumerateInterfaces())
                {
                    if (!iface.IsConnected) continue;
                    baselines[iface.Guid.ToString()] = new WiFiInterfaceBaseline
                    {
                        BackgroundScan = _wlan.QueryBool(iface.Guid, WlanOpcode.BackgroundScan),
                        MediaStreaming = _wlan.QueryBool(iface.Guid, WlanOpcode.MediaStreaming),
                    };
                }
            }
            finally
            {
                if (openedHere) _wlan.Close();
            }
        }

        snapshot.Set("interfaces", baselines);
        return snapshot;
    }

    public ApplyResult Apply(DomainSnapshot baseline)
    {
        var sw = Stopwatch.StartNew();

        lock (_gate)
        {
            if (!_wlan.TryOpen())
                return ApplyResult.Fail(Id, "Wi-Fi service unavailable", sw.Elapsed);

            var connected = ConnectedInterfacesLocked();
            if (connected.Count == 0)
            {
                _wlan.Close(); // nothing to hold open
                return ApplyResult.Ok(Id, "No connected Wi-Fi adapter — nothing to optimize", skipped: 1, duration: sw.Elapsed);
            }

            var (applied, ignored) = ApplyToConnectedLocked(connected);
            _driversIgnored = ignored;
            _isActive = true;       // keep the handle OPEN — closing it would revert the opcodes
            StartReapplyTimerLocked();

            sw.Stop();
            var message = ignored > 0
                ? $"Optimized {applied} adapter(s); {ignored} ignored by driver"
                : $"Optimized {applied} Wi-Fi adapter(s): background scan off, streaming on";
            return ApplyResult.Ok(Id, message, optimized: applied, skipped: ignored, duration: sw.Elapsed);
        }
    }

    public void Revert(DomainSnapshot baseline)
    {
        lock (_gate)
        {
            StopReapplyTimerLocked();

            var baselines = baseline.Get<Dictionary<string, WiFiInterfaceBaseline>>("interfaces")
                            ?? new Dictionary<string, WiFiInterfaceBaseline>();

            // Restore captured values explicitly (belt-and-suspenders — closing the handle below
            // also reverts the session-scoped opcodes).
            if (_wlan.IsOpen || _wlan.TryOpen())
            {
                foreach (var (guidStr, b) in baselines)
                {
                    if (!Guid.TryParse(guidStr, out var guid)) continue;
                    if (b.BackgroundScan is bool bs) _wlan.SetBool(guid, WlanOpcode.BackgroundScan, bs);
                    if (b.MediaStreaming is bool ms) _wlan.SetBool(guid, WlanOpcode.MediaStreaming, ms);
                }
            }

            _wlan.Close();
            _isActive = false;
            _driversIgnored = 0;
        }
    }

    public DomainStatus GetStatus() => new()
    {
        DomainId = Id,
        DisplayName = DisplayName,
        Category = Category,
        IsSupported = IsSupported,
        IsActive = _isActive,
        Summary = _isActive
            ? (_driversIgnored > 0
                ? $"Active — {_driversIgnored} adapter(s) ignored by driver"
                : "Background scan off, streaming on")
            : "Inactive",
    };

    /// <summary>
    /// Re-assert the optimization on currently-connected interfaces. Called on the reapply timer
    /// so a Wi-Fi reconnect (which resets the opcodes) doesn't silently lose the optimization.
    /// </summary>
    internal void Reapply()
    {
        lock (_gate)
        {
            if (!_isActive || !_wlan.IsOpen) return;
            var connected = ConnectedInterfacesLocked();
            if (connected.Count > 0)
                _driversIgnored = ApplyToConnectedLocked(connected).ignored;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopReapplyTimerLocked();
            _wlan.Dispose();
        }
    }

    private bool DetectAvailability()
    {
        lock (_gate)
        {
            if (_wlan.IsOpen) return true; // already holding a handle → available
            if (!_wlan.TryOpen()) return false;
            try { return _wlan.EnumerateInterfaces().Count > 0; }
            finally { _wlan.Close(); }
        }
    }

    private List<WlanInterfaceInfo> ConnectedInterfacesLocked() =>
        _wlan.EnumerateInterfaces().Where(i => i.IsConnected).ToList();

    private (int applied, int ignored) ApplyToConnectedLocked(IReadOnlyList<WlanInterfaceInfo> connected)
    {
        int applied = 0, ignored = 0;
        foreach (var iface in connected)
        {
            bool changed = false, ifaceIgnored = false;

            if (_settings.WiFiDisableBackgroundScan)
                ApplyAndVerify(iface.Guid, WlanOpcode.BackgroundScan, false, ref changed, ref ifaceIgnored);
            // Media-streaming mode is permanently disabled: it net-degraded latency on real adapters
            // (Qualcomm WCN685x) and is never enabled, regardless of any setting. The capture/revert
            // paths remain so a value left set by an older build is still restored to its baseline.

            if (ifaceIgnored) ignored++;
            else if (changed) applied++;
        }
        return (applied, ignored);
    }

    /// <summary>
    /// Set an opcode then read it back. Some Intel/MediaTek drivers report success but silently
    /// ignore the write — verify-after-write detects that so we surface "ignored" rather than
    /// claiming a change we didn't make. A null readback (can't verify) trusts the set result.
    /// </summary>
    private void ApplyAndVerify(Guid guid, WlanOpcode opcode, bool desired, ref bool changed, ref bool ignored)
    {
        if (!_wlan.SetBool(guid, opcode, desired))
            return;

        var readback = _wlan.QueryBool(guid, opcode);
        if (readback is null || readback.Value == desired)
            changed = true;
        else
            ignored = true;
    }

    private void StartReapplyTimerLocked()
    {
        _reapplyTimer ??= new Timer(_ => Reapply(), null, ReapplyInterval, ReapplyInterval);
    }

    private void StopReapplyTimerLocked()
    {
        _reapplyTimer?.Dispose();
        _reapplyTimer = null;
    }
}
