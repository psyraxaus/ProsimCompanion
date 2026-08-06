using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Audio.Backends.VoiceMeeter;

/// <summary>
/// Runtime-loaded wrapper around VoicemeeterRemote64.dll (never redistributed; the path is
/// user-configured). Loaded via NativeLibrary + GetDelegateForFunctionPointer — not DllImport —
/// so a wrong configured path degrades instead of crashing at process start. Once loaded the
/// DLL stays loaded: a changed VoiceMeeterDllPath takes effect on the next app restart.
/// Login is idempotent per process: repeated
/// VBVMR_Login on the same process is flaky on some VoiceMeeter versions, so backend switches
/// suspend writes instead of logging out; VBVMR_Logout happens only at service shutdown.
/// </summary>
public sealed class VoiceMeeterRemote : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LoginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int LogoutDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetParameterFloatDelegate([MarshalAs(UnmanagedType.LPStr)] string name, float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetParameterFloatDelegate([MarshalAs(UnmanagedType.LPStr)] string name, out float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVoicemeeterTypeDelegate(out int type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetParameterStringDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string name, StringBuilder value);

    private readonly ILogger<VoiceMeeterRemote> _logger;
    private readonly object _gate = new();

    private IntPtr _module;
    private LoginDelegate? _login;
    private LogoutDelegate? _logout;
    private SetParameterFloatDelegate? _setFloat;
    private GetParameterFloatDelegate? _getFloat;
    private GetVoicemeeterTypeDelegate? _getType;
    private GetParameterStringDelegate? _getString;
    private bool _loginCalled;
    private bool _writesSuspended;

    public VoiceMeeterRemote(ILogger<VoiceMeeterRemote> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Module loaded and login performed — read-only calls are valid.</summary>
    public bool IsLoaded
    {
        get
        {
            lock (_gate)
            {
                return _loginCalled;
            }
        }
    }

    /// <summary>Loaded and writes not suspended — every write gates on this.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _loginCalled && !_writesSuspended;
            }
        }
    }

    /// <summary>Loads the DLL (if needed) and logs in. Idempotent: when already logged in it
    /// just lifts a write suspension. Returns false when the DLL or any export is missing, or
    /// VBVMR_Login reports an error (0 = OK; 1 = OK but VoiceMeeter app not running yet —
    /// still usable, parameters apply when it starts).</summary>
    public bool Login(string dllPath)
    {
        lock (_gate)
        {
            if (_loginCalled)
            {
                _writesSuspended = false;
                return true;
            }

            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
            {
                return false;
            }

            try
            {
                _module = NativeLibrary.Load(dllPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                _logger.LogWarning(ex, "VoicemeeterRemote64.dll failed to load from {Path}", dllPath);
                return false;
            }

            _login = GetExport<LoginDelegate>("VBVMR_Login");
            _logout = GetExport<LogoutDelegate>("VBVMR_Logout");
            _setFloat = GetExport<SetParameterFloatDelegate>("VBVMR_SetParameterFloat");
            _getFloat = GetExport<GetParameterFloatDelegate>("VBVMR_GetParameterFloat");
            _getType = GetExport<GetVoicemeeterTypeDelegate>("VBVMR_GetVoicemeeterType");
            _getString = GetExport<GetParameterStringDelegate>("VBVMR_GetParameterStringA");

            if (_login is null || _logout is null || _setFloat is null || _getFloat is null
                || _getType is null || _getString is null)
            {
                _logger.LogWarning("VoicemeeterRemote64.dll at {Path} is missing expected exports", dllPath);
                UnloadLocked();
                return false;
            }

            var result = _login();
            if (result is not (0 or 1))
            {
                _logger.LogWarning("VBVMR_Login failed with code {Code}", result);
                UnloadLocked();
                return false;
            }

            _loginCalled = true;
            _writesSuspended = false;
            _logger.LogInformation("VoiceMeeter remote logged in (code {Code})", result);
            return true;
        }
    }

    /// <summary>Blocks writes without logging out (used when switching to CoreAudio).</summary>
    public void SuspendWrites()
    {
        lock (_gate)
        {
            _writesSuspended = true;
        }
    }

    /// <summary>Full logout + module unload — service shutdown only.</summary>
    public void Logout()
    {
        lock (_gate)
        {
            if (_loginCalled)
            {
                try
                {
                    _logout?.Invoke();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "VBVMR_Logout failed");
                }
            }

            UnloadLocked();
        }
    }

    public void Dispose() => Logout();

    /// <summary>Strip/bus counts for the installed edition: VoiceMeeter (3,2), Banana (5,5),
    /// Potato (8,8). Falls back to the basic edition when the query fails.</summary>
    public (int Strips, int Buses) GetCounts()
    {
        lock (_gate)
        {
            if (_getType is null || _getType(out var type) != 0)
            {
                return (3, 2);
            }

            return type switch
            {
                2 => (5, 5),
                3 => (8, 8),
                _ => (3, 2),
            };
        }
    }

    public void SetGainDb(int index, bool isBus, float gainDb)
    {
        lock (_gate)
        {
            if (_loginCalled && !_writesSuspended)
            {
                _setFloat!($"{Target(isBus)}[{index}].Gain", gainDb);
            }
        }
    }

    public float? GetGainDb(int index, bool isBus)
    {
        lock (_gate)
        {
            if (_loginCalled && _getFloat!($"{Target(isBus)}[{index}].Gain", out var value) == 0)
            {
                return value;
            }

            return null;
        }
    }

    public void SetMute(int index, bool isBus, bool mute)
    {
        lock (_gate)
        {
            if (_loginCalled && !_writesSuspended)
            {
                _setFloat!($"{Target(isBus)}[{index}].Mute", mute ? 1f : 0f);
            }
        }
    }

    public bool? GetMute(int index, bool isBus)
    {
        lock (_gate)
        {
            if (_loginCalled && _getFloat!($"{Target(isBus)}[{index}].Mute", out var value) == 0)
            {
                return value >= 0.5f;
            }

            return null;
        }
    }

    public string? GetLabel(int index, bool isBus)
    {
        lock (_gate)
        {
            if (_getString is null)
            {
                return null;
            }

            var buffer = new StringBuilder(512);
            return _getString($"{Target(isBus)}[{index}].Label", buffer) == 0 ? buffer.ToString() : null;
        }
    }

    private static string Target(bool isBus) => isBus ? "Bus" : "Strip";

    private T? GetExport<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_module, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    private void UnloadLocked()
    {
        if (_module != IntPtr.Zero)
        {
            NativeLibrary.Free(_module);
        }

        _module = IntPtr.Zero;
        _login = null;
        _logout = null;
        _setFloat = null;
        _getFloat = null;
        _getType = null;
        _getString = null;
        _loginCalled = false;
        _writesSuspended = false;
    }
}
