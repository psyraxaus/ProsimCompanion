namespace ProsimCompanion.Core.State;

/// <summary>Connection lifecycle of an external subsystem (ProSim, SimConnect, GSX, …).</summary>
public enum ConnectionState
{
    /// <summary>The subsystem is turned off in settings or unavailable on this machine.</summary>
    Disabled,

    /// <summary>Not currently connected; the subsystem will keep retrying.</summary>
    Disconnected,

    /// <summary>A connection attempt is in progress.</summary>
    Connecting,

    /// <summary>Connected and healthy.</summary>
    Connected,
}

/// <summary>Well-known subsystem names used as keys in the <see cref="ConnectionStatusStore"/>.</summary>
public static class Subsystems
{
    public const string Prosim = "ProSim";
    public const string SimConnect = "SimConnect";
    public const string Gsx = "GSX";
}
