using System.Runtime.InteropServices;

namespace ProsimCompanion.Sim.Input;

/// <summary>
/// The handful of <c>vJoyInterface.dll</c> exports the relay needs, loaded with
/// <see cref="NativeLibrary"/> from the driver's folder at first use — never a DllImport (the
/// DLL is not ours to ship, and a missing driver must not fail startup). Signatures from the
/// vJoy SDK's <c>vjoyinterface.h</c>: every export is <c>__cdecl</c>, <c>BOOL</c> is a 32-bit
/// int, device ids are 1-based <c>UINT</c>, buttons 1-based <c>UCHAR</c>.
/// </summary>
internal sealed class VJoyInterface : IDisposable
{
    /// <summary><c>enum VjdStat</c> in the SDK: owned by us / free / owned by another app /
    /// missing (or driver down) / unknown.</summary>
    public enum DeviceStatus
    {
        Own = 0,
        Free = 1,
        Busy = 2,
        Missing = 3,
        Unknown = 4,
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnabledDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate short VersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StatusDelegate(uint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AcquireDelegate(uint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RelinquishDelegate(uint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetButtonDelegate(int value, uint device, byte button);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ButtonCountDelegate(uint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ResetButtonsDelegate(uint device);

    private readonly IntPtr _module;
    private readonly EnabledDelegate _enabled;
    private readonly VersionDelegate _version;
    private readonly StatusDelegate _status;
    private readonly AcquireDelegate _acquire;
    private readonly RelinquishDelegate _relinquish;
    private readonly SetButtonDelegate _setButton;
    private readonly ButtonCountDelegate _buttonCount;
    private readonly ResetButtonsDelegate? _resetButtons;

    private VJoyInterface(IntPtr module)
    {
        _module = module;
        _enabled = Export<EnabledDelegate>("vJoyEnabled") ?? throw Missing("vJoyEnabled");
        _version = Export<VersionDelegate>("GetvJoyVersion") ?? throw Missing("GetvJoyVersion");
        _status = Export<StatusDelegate>("GetVJDStatus") ?? throw Missing("GetVJDStatus");
        _acquire = Export<AcquireDelegate>("AcquireVJD") ?? throw Missing("AcquireVJD");
        _relinquish = Export<RelinquishDelegate>("RelinquishVJD") ?? throw Missing("RelinquishVJD");
        _setButton = Export<SetButtonDelegate>("SetBtn") ?? throw Missing("SetBtn");
        _buttonCount = Export<ButtonCountDelegate>("GetVJDButtonNumber") ?? throw Missing("GetVJDButtonNumber");
        _resetButtons = Export<ResetButtonsDelegate>("ResetButtons");
    }

    /// <summary>Loads the DLL at <paramref name="path"/>. Throws <see cref="DllNotFoundException"/>,
    /// <see cref="BadImageFormatException"/> (a 32-bit DLL in a 64-bit process) or
    /// <see cref="EntryPointNotFoundException"/> (not vJoy's DLL) — the relay turns each into words.</summary>
    public static VJoyInterface Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var module = NativeLibrary.Load(path);
        try
        {
            return new VJoyInterface(module);
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    /// <summary>True when the vJoy driver is installed and running.</summary>
    public bool DriverEnabled => _enabled() != 0;

    /// <summary>Driver/DLL version word (e.g. 0x0219 for 2.1.9).</summary>
    public short Version => _version();

    public DeviceStatus Status(uint device) => (DeviceStatus)_status(device);

    public bool Acquire(uint device) => _acquire(device) != 0;

    public void Relinquish(uint device) => _relinquish(device);

    public bool SetButton(uint device, byte button, bool pressed) => _setButton(pressed ? 1 : 0, device, button) != 0;

    public int ButtonCount(uint device) => _buttonCount(device);

    public void ResetButtons(uint device) => _resetButtons?.Invoke(device);

    private T? Export<T>(string name) where T : Delegate =>
        NativeLibrary.TryGetExport(_module, name, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    private static EntryPointNotFoundException Missing(string name) =>
        new($"vJoyInterface.dll has no export named {name} — not the vJoy SDK DLL?");

    public void Dispose()
    {
        if (_module != IntPtr.Zero)
        {
            NativeLibrary.Free(_module);
        }
    }
}
