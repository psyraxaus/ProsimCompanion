using ProsimCompanion.Core.Hardware.Skalarki;

namespace ProsimCompanion.Skalarki;

/// <summary>What kind of control sent an input event (the SDK's <c>HardwareSource</c>).</summary>
public enum SkalarkiSourceKind
{
    Switch,
    Encoder,
    Axis,
    Unknown,
}

/// <summary>One input event from a board, already translated out of SDK types.</summary>
/// <param name="Device">The board.</param>
/// <param name="Group">The SDK group name (FCU, ELEC, MCDUCS, …).</param>
/// <param name="Source">Switch, encoder or axis.</param>
/// <param name="Control">The SDK event name (FCU_SPD_PUSH, …) — the <c>Event</c> enum member,
/// which is what the SDK's own diagnostics tool shows as the control name.</param>
/// <param name="Value">The value as text: <c>ON</c>/<c>OFF</c> for a switch, the integer for
/// an encoder or axis.</param>
/// <param name="AtUtc">When the bridge received it.</param>
public sealed record SkalarkiHardwareEvent(
    SkalarkiDeviceId Device,
    string Group,
    SkalarkiSourceKind Source,
    string Control,
    string Value,
    DateTimeOffset AtUtc);

/// <summary>
/// The thin seam over the SkalarkiIO SDK's <c>EventClient</c>. Core and Web never see SDK
/// types; the one implementation that does (<c>SdkSkalarkiClient</c>) lives in the
/// conditionally built ProsimCompanion.Skalarki.Sdk assembly and is loaded at runtime by
/// <see cref="SdkLoader"/>. Every member may throw an SDK exception; the connection service
/// wraps every call, so an implementation need not.
/// </summary>
public interface ISkalarkiClient : IDisposable
{
    /// <summary>Profiler link up or down (the SDK's <c>ConnectionStateChanged</c>).</summary>
    event Action<bool>? LinkChanged;

    /// <summary>A board's USB presence as the Profiler reports it (the SDK's
    /// <c>ConnectedDevicesChanged</c>, a full 26-board snapshot right after connect).</summary>
    event Action<SkalarkiDeviceId, bool>? DeviceChanged;

    /// <summary>An input event from a registered control.</summary>
    event Action<SkalarkiHardwareEvent>? HardwareEvent;

    /// <summary>The SDK assembly's informational version, for the page's memo cell.</summary>
    string SdkVersion { get; }

    /// <summary>Opens the TCP link to the Profiler's SDK server. The host may be a name or an
    /// address; the SDK itself only takes an <c>IPAddress</c>.</summary>
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken);

    /// <summary>Closes the link; the SDK raises <see cref="LinkChanged"/>(false) synchronously.</summary>
    Task DisconnectAsync();

    /// <summary>Registers every switch, encoder and axis of the given boards from the chosen
    /// catalogue, with <c>scanState</c> false (the spike showed true replays nothing on
    /// Profiler 5.1.920 and a burst would falsely satisfy a hardware check).</summary>
    void RegisterAll(IReadOnlyCollection<SkalarkiDeviceId> devices, SkalarkiCatalogue catalogue);

    /// <summary>Drives every output descriptor of a board on or off (LAMP TEST only).</summary>
    void SetDeviceOutputs(SkalarkiDeviceId device, bool lit);

    /// <summary>Resets a board's controller (HW RESET only, behind an explicit confirm).</summary>
    void HardwareReset(SkalarkiDeviceId device, bool hard);

    /// <summary>The Profiler's licence reply (<c>GetLicenseTypeAsync</c> returns an int; it is
    /// rendered as text because its meaning is undocumented).</summary>
    Task<string?> GetLicenseAsync(CancellationToken cancellationToken);
}

/// <summary>Where a client comes from: the SDK bridge when the user's SDK folder validates,
/// else a reason the page can print. A fake factory stands in for tests.</summary>
public interface ISkalarkiClientFactory
{
    /// <summary>Checks the SDK folder and the bridge assembly without loading anything. Null
    /// when a client can be created; otherwise one sentence a pilot can act on.</summary>
    string? Validate(string sdkPath);

    /// <summary>Creates a client over the SDK in <paramref name="sdkPath"/>. Throws when the
    /// folder does not validate or the bridge cannot load; the service turns that into the
    /// Disabled state with the message.</summary>
    ISkalarkiClient Create(string sdkPath);
}
