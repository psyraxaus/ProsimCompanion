namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Connection settings for the ProSim system. The SDK itself is loaded at runtime from the user's
/// ProSim installation and is never redistributed (see docs/integrations/prosim.md).
/// </summary>
public sealed class ProsimOptions
{
    public const string SectionName = "prosim";

    /// <summary>Hostname or IP of the machine running ProSim System.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Directory containing ProSimSDK.dll. Defaults to the standard ProSim install location.
    /// </summary>
    public string SdkPath { get; set; } = @"C:\prosim\prosim-system";

    /// <summary>Delay between reconnect attempts when the connection is lost.</summary>
    public int ReconnectIntervalMs { get; set; } = 2000;
}
