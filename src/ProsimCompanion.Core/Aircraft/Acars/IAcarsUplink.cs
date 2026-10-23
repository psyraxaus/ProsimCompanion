namespace ProsimCompanion.Core.Aircraft.Acars;

/// <summary>
/// Sends one ACARS uplink to the cockpit RCVD MSGS list (issue #184: the Speech pillar's
/// company channel sends the diversion message through it). Implemented by the Prosim pillar;
/// absent when ProSim is not composed in — callers degrade to the spoken channel alone.
/// </summary>
public interface IAcarsUplink
{
    /// <summary>True when the gateway accepted the write (dispatch is asynchronous in ProSim).</summary>
    Task<bool> SendAsync(AcarsMessage message, CancellationToken cancellationToken = default);
}
