using OptiSYS.Core.Interfaces;
using OptiSYS.Core.Native;

namespace OptiSYS.Core.Services;

/// <summary>
/// Native-backed <see cref="IEffectivePowerModeProvider"/> using
/// <c>PowerRegisterForEffectivePowerModeNotifications</c> (powrprof.dll). Pure read-only signal
/// consumption — it NEVER writes the power mode.
///
/// <para>Graceful degradation: if the OS API is unavailable (older Windows) or registration fails,
/// <see cref="Current"/> stays <see cref="EffectivePowerMode.Unknown"/> and the consuming
/// controller behaves exactly as if there were no provider. The callback delegate is rooted in a
/// field for the lifetime of the registration (so the GC can't collect it underneath the OS), and
/// the registration is released on <see cref="Stop"/>/<see cref="Dispose"/>.</para>
/// </summary>
public sealed class EffectivePowerModeProvider : IEffectivePowerModeProvider
{
    private readonly object _gate = new();

    // Rooted for the lifetime of the registration so the GC never collects the delegate the OS holds.
    private NativeMethods.EffectivePowerModeCallback? _callback;
    private IntPtr _registration = IntPtr.Zero;
    private volatile int _current = (int)EffectivePowerMode.Unknown;
    private bool _disposed;

    public EffectivePowerMode Current => (EffectivePowerMode)_current;

    public event Action? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _registration != IntPtr.Zero)
                return;

            // Keep one delegate instance alive in a field; the OS invokes it once immediately with
            // the current mode, then on every change.
            _callback = OnModeChanged;

            try
            {
                // Prefer V2 (adds GameMode / MixedReality); fall back to V1 on older Windows.
                if (NativeMethods.PowerRegisterForEffectivePowerModeNotifications(
                        NativeMethods.EFFECTIVE_POWER_MODE_V2, _callback, IntPtr.Zero, out var handle) == 0)
                {
                    _registration = handle;
                    return;
                }

                if (NativeMethods.PowerRegisterForEffectivePowerModeNotifications(
                        NativeMethods.EFFECTIVE_POWER_MODE_V1, _callback, IntPtr.Zero, out handle) == 0)
                {
                    _registration = handle;
                    return;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            // Registration failed or the API is missing: stay Unknown, drop the rooted delegate.
            _callback = null;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_registration != IntPtr.Zero)
            {
                try { NativeMethods.PowerUnregisterFromEffectivePowerModeNotifications(_registration); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                _registration = IntPtr.Zero;
            }

            _callback = null;
            _current = (int)EffectivePowerMode.Unknown;
        }
    }

    // Invoked by the OS on the registration thread. The raw int maps 1:1 onto EFFECTIVE_POWER_MODE;
    // an unrecognized value degrades to Unknown rather than mis-classifying.
    private void OnModeChanged(int mode, IntPtr context)
    {
        var newMode = Enum.IsDefined(typeof(EffectivePowerMode), mode) && mode >= 0
            ? mode
            : (int)EffectivePowerMode.Unknown;

        if (newMode == _current)
            return;

        _current = newMode;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
