using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// The ground crew's "we want to talk" cue before a spoken report (review 2026-11-05: the
/// upcalls and the walkaround report re-typed it line for line): the MECH call press that
/// flashes the ACP lamp (ProSim's own pipeline does lamp and audio), then the INT latch or the
/// grace — a report is never lost to an unmonitored panel.
/// </summary>
internal static class InterphoneRing
{
    /// <param name="dataRefs">For the MECH press; null = no press.</param>
    /// <param name="acp">For the INT latch; null = no wait.</param>
    /// <param name="mechCall"><c>groundCrew.mechCall</c>.</param>
    /// <param name="requireIntChannel"><c>groundCrew.requireIntChannel</c>.</param>
    /// <param name="graceSeconds"><c>groundCrew.intChannelGraceSeconds</c>.</param>
    /// <param name="what">What follows the ring, for the log lines ("upcall", "walkaround report").</param>
    public static async Task RingAsync(
        IProsimDataRefs? dataRefs,
        IAcpChannel? acp,
        bool mechCall,
        bool requireIntChannel,
        int graceSeconds,
        ILogger logger,
        string what,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (mechCall && dataRefs is not null)
        {
            try
            {
                await dataRefs.PressMomentaryAsync(ProsimDataRefNames.OhCallsMech, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "MECH call press failed — speaking the {What} anyway", what);
            }
        }

        if (requireIntChannel && acp is not null)
        {
            var latched = await acp.AwaitReceiveAsync(
                AcpChannelKind.Intercom, TimeSpan.FromSeconds(Math.Max(0, graceSeconds)), cancellationToken).ConfigureAwait(false);
            if (!latched)
            {
                logger.LogInformation("INT channel not selected within grace — playing the {What} anyway", what);
            }
        }
    }
}
