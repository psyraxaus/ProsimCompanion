using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Speech.Briefings;

namespace ProsimCompanion.Speech.Commands;

/// <summary>
/// The single source for spoken-value tokens ({altimeter}/{qnh}/{v1}/{vr}/{v2}/{flex}/{runway}),
/// shared by voice-command confirm callouts and checklist confirm callouts (issue #39: the
/// checklist path spoke the raw brace tokens because it had no expansion of its own). Owns the
/// cached dataref subscriptions; the formatting stays pure in <see cref="SpokenValueFormatting"/>.
/// </summary>
public sealed class SpokenTokenSource : IDisposable
{
    private readonly IProsimDataRefs _dataRefs;
    private readonly IOptionsMonitor<BriefingOptions> _briefingOptions;
    private readonly ILogger<SpokenTokenSource> _logger;
    private readonly IOptionsMonitor<SpeechOptions>? _speech;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);

    public SpokenTokenSource(
        IProsimDataRefs dataRefs,
        IOptionsMonitor<BriefingOptions> briefingOptions,
        ILogger<SpokenTokenSource> logger,
        IOptionsMonitor<SpeechOptions>? speech = null)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(briefingOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _briefingOptions = briefingOptions;
        _logger = logger;
        _speech = speech;
    }

    /// <summary>Expands the brace tokens in <paramref name="text"/> from the current cached
    /// values; pass-through when the text carries no tokens.</summary>
    public string Apply(string text)
        => string.IsNullOrEmpty(text) || !text.Contains('{')
            ? text
            : SpokenValueFormatting.ApplyTokens(text, Snapshot());

    /// <summary>Registers the token subscriptions ahead of first use so ProSim has pushed values
    /// before the first spoken query fires — a lazy first subscribe would answer "unavailable"
    /// once.</summary>
    public void Prime()
    {
        try
        {
            // The EFIS2 (F/O-side) baro refs are seat-relative — Sub flips them via
            // PilotSeatMap when the human flies the right seat, so "the FO's altimeter"
            // always means the virtual FO's side. The STD read is deliberately the effective
            // GATE, not the push-pull switch (see Efis2BaroStdGate, #83).
            Sub(ProsimDataRefNames.Efis2BaroStdGate);
            Sub(ProsimDataRefNames.Efis2BaroMode); // 0:inHg 1:hPa
            Sub(ProsimDataRefNames.Efis2BaroHpa);
            Sub(ProsimDataRefNames.Efis2BaroInch);
            Sub(ProsimDataRefNames.FmsPerfTakeoffV1);
            Sub(ProsimDataRefNames.FmsPerfTakeoffVr);
            Sub(ProsimDataRefNames.FmsPerfTakeoffV2);
            Sub(ProsimDataRefNames.FmsPerfTakeoffFlexTemp);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Priming token subscriptions failed");
        }
    }

    /// <summary>Snapshots the token values from the cached subscriptions (registered once on
    /// first use — never a per-read round-trip).</summary>
    public CommandTokenValues Snapshot()
    {
        try
        {
            var std = Sub(ProsimDataRefNames.Efis2BaroStdGate);
            bool? stdState = std.RawValue is null ? null : std.Value;
            var hpaMode = Sub(ProsimDataRefNames.Efis2BaroMode).Value == 1;
            var hpa = Sub(ProsimDataRefNames.Efis2BaroHpa).Value;
            var inches = Sub(ProsimDataRefNames.Efis2BaroInch).Value;

            var runway = FlightJsonRouteReader.Read().DepartureRunway;
            if (string.IsNullOrWhiteSpace(runway))
            {
                runway = _briefingOptions.CurrentValue.DepartureRunway;
            }

            return new CommandTokenValues(
                Altimeter: SpokenValueFormatting.Altimeter(stdState, hpaMode, hpa, inches),
                Qnh: SpokenValueFormatting.Altimeter(stdState, hpaMode: true, hpa, inches),
                V1: SpokenValueFormatting.Speed(Perf(ProsimDataRefNames.FmsPerfTakeoffV1)),
                Vr: SpokenValueFormatting.Speed(Perf(ProsimDataRefNames.FmsPerfTakeoffVr)),
                V2: SpokenValueFormatting.Speed(Perf(ProsimDataRefNames.FmsPerfTakeoffV2)),
                Flex: SpokenValueFormatting.Speed(Perf(ProsimDataRefNames.FmsPerfTakeoffFlexTemp)),
                Runway: SpokenValueFormatting.Runway(runway));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Token value snapshot failed");
            return CommandTokenValues.Unavailable;
        }
    }

    public void Dispose()
    {
        lock (_reads)
        {
            foreach (var read in _reads.Values)
            {
                read.Dispose();
            }
            _reads.Clear();
        }
    }

    private int? Perf(DataRef<int> dataref)
    {
        var sub = Sub(dataref);
        return sub.RawValue is null ? null : sub.Value;
    }

    private IDataRefSubscription<T> Sub<T>(DataRef<T> dataref)
    {
        lock (_reads)
        {
            // Seat-relative reads (EFIS baro etc.); cached under the MAPPED name so a seat
            // change picks up the other side on the next new subscription.
            var mapped = Recognition.PilotSeatMap.Map(dataref,
                _speech is not null && Recognition.PilotSeatMap.HumanIsRightSeat(_speech.CurrentValue));
            if (!_reads.TryGetValue(mapped.Name, out var read))
            {
                read = _dataRefs.Subscribe(mapped);
                _reads[mapped.Name] = read;
            }

            return (IDataRefSubscription<T>)read;
        }
    }
}
