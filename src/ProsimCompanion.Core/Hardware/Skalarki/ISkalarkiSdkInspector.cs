namespace ProsimCompanion.Core.Hardware.Skalarki;

/// <summary>What one probe of the Profiler found (the settings page's "Test connection").</summary>
/// <param name="Ok">The link came up.</param>
/// <param name="Message">One line for the pill: the refusal, or what was found.</param>
/// <param name="Reported">Boards the Profiler reported at all.</param>
/// <param name="Connected">Boards it reported as connected.</param>
/// <param name="License">The licence reply, when the link came up.</param>
public sealed record SkalarkiProbeResult(
    bool Ok,
    string Message,
    IReadOnlyList<SkalarkiDeviceId> Reported,
    IReadOnlyList<SkalarkiDeviceId> Connected,
    string? License);

/// <summary>
/// What the Skalarki settings page asks of the hardware pillar without referencing it: is the
/// SDK folder usable, which SDK is in it, and does the Profiler answer. Implemented by the
/// Skalarki project over its loader; a page test registers a stub.
/// </summary>
public interface ISkalarkiSdkInspector
{
    /// <summary>The SDK's file version in the folder ("5.1.919.1000"), or null when it is not there.</summary>
    string? DescribeSdk(string? sdkPath);

    /// <summary>Null when the folder (and the app build) can run the link; otherwise the reason.</summary>
    string? Validate(string? sdkPath);

    /// <summary>Connects once, listens for the presence snapshot and the licence, disconnects.
    /// Never throws: a failure comes back as <see cref="SkalarkiProbeResult.Ok"/> false.</summary>
    Task<SkalarkiProbeResult> ProbeAsync(string host, int port, string? sdkPath, CancellationToken cancellationToken);
}
