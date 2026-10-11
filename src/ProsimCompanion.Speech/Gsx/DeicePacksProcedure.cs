using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// The pure half of the FO's de-icing packs/ditching procedure: what to say on each edge and
/// what the overhead lights must show afterwards. No I/O, no clock.
/// </summary>
public static class DeicePacksProcedureCore
{
    /// <summary>How long the crew gets between the call and the read-back check.</summary>
    public static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(20);

    public const string StartingCall = "De-icing starting — packs off, ditching on.";
    public const string CompleteCall = "De-icing complete — packs on, ditching off.";

    /// <summary>The lights, as read: PACK 1/2 OFF legends and the DITCHING ON legend.</summary>
    public sealed record Lights(bool Pack1Off, bool Pack2Off, bool DitchingOn);

    /// <summary>What the FO says at the edge; null for an edge that says nothing.</summary>
    public static string? CallFor(DeiceStage stage) => stage switch
    {
        DeiceStage.Requested or DeiceStage.Active => StartingCall,
        DeiceStage.Completed => CompleteCall,
        _ => null,
    };

    /// <summary>The treatment configuration: packs off, ditching on (true while the crew
    /// sprays); the normal configuration afterwards.</summary>
    public static bool WantsTreatmentConfiguration(DeiceStage stage)
        => stage is DeiceStage.Requested or DeiceStage.Active;

    /// <summary>The items that disagree with the wanted configuration, in cockpit order;
    /// empty when the lights match.</summary>
    public static IReadOnlyList<string> Discrepancies(Lights lights, bool treatment)
    {
        ArgumentNullException.ThrowIfNull(lights);
        var missing = new List<string>(3);
        if (lights.Pack1Off != treatment)
        {
            missing.Add(treatment ? "pack 1 still on" : "pack 1 still off");
        }

        if (lights.Pack2Off != treatment)
        {
            missing.Add(treatment ? "pack 2 still on" : "pack 2 still off");
        }

        if (lights.DitchingOn != treatment)
        {
            missing.Add(treatment ? "ditching not on" : "ditching still on");
        }

        return missing;
    }

    /// <summary>The verify line: a confirmation, or the one reminder.</summary>
    public static string VerifyPhrase(IReadOnlyList<string> discrepancies, bool treatment)
    {
        ArgumentNullException.ThrowIfNull(discrepancies);
        if (discrepancies.Count == 0)
        {
            return treatment ? "Packs off, ditching on — checked." : "Packs on, ditching off — checked.";
        }

        var wanted = treatment ? "packs off, ditching on" : "packs on, ditching off";
        return $"Reminder — {string.Join(", ", discrepancies)}. For de-icing: {wanted}.";
    }
}

/// <summary>
/// The FO's cockpit side of a GSX de-icing (issue #166, opt-in <c>gsx.deice.packsProcedure</c>):
/// real crews switch the packs OFF and DITCHING ON before the trucks spray (no fluid into the
/// cabin air, outflow valve closed) and reverse it afterwards. On the crew's Requested/Active
/// edge the FO says "De-icing starting — packs off, ditching on", twenty seconds later reads
/// the overhead OFF/ON lights and reminds once if they disagree; on Completed "De-icing
/// complete — packs on, ditching off" with the same check. Detect-and-report only: the FO
/// never writes a pneumatic switch (no allow-list entry exists for them, on purpose).
/// Edges come from the Core <see cref="DeiceActivityStore"/>; each is spoken once per cycle.
/// </summary>
public sealed class DeicePacksProcedure : IStartupModule, IDisposable
{
    private readonly DeiceActivityStore _activity;
    private readonly IProsimDataRefs _dataRefs;
    private readonly IFlightPhaseSource _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<DeicePacksProcedure> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private IDisposable? _observer;
    private IDataRefSubscription<int>? _pack1Off;
    private IDataRefSubscription<int>? _pack2Off;
    private IDataRefSubscription<int>? _ditchingOn;
    private bool _startingSpoken;
    private bool _completeSpoken;

    public DeicePacksProcedure(
        DeiceActivityStore activity,
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        JsonlEventLog eventLog,
        ILogger<DeicePacksProcedure> logger)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _activity = activity;
        _dataRefs = dataRefs;
        _flight = flight;
        _arbiter = arbiter;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        _pack1Off = _dataRefs.Subscribe(ProsimDataRefNames.PneumaticPack1Indicator);
        _pack2Off = _dataRefs.Subscribe(ProsimDataRefNames.PneumaticPack2Indicator);
        _ditchingOn = _dataRefs.Subscribe(ProsimDataRefNames.PneumaticDitchingIndicator);
        _observer = _activity.Observe(OnActivity);
    }

    public void Dispose()
    {
        _observer?.Dispose();
        _shutdown.Cancel();
        _shutdown.Dispose();
        _pack1Off?.Dispose();
        _pack2Off?.Dispose();
        _ditchingOn?.Dispose();
    }

    private void OnActivity(DeiceActivitySnapshot snapshot)
    {
        try
        {
            if (snapshot.Stage == DeiceStage.None)
            {
                lock (_gate)
                {
                    _startingSpoken = false;
                    _completeSpoken = false;
                }

                return;
            }

            if (!_options.CurrentValue.Deice.PacksProcedure || !_flight.IsLive)
            {
                return;
            }

            var treatment = DeicePacksProcedureCore.WantsTreatmentConfiguration(snapshot.Stage);
            lock (_gate)
            {
                if (treatment ? _startingSpoken : _completeSpoken)
                {
                    return;
                }

                if (treatment)
                {
                    _startingSpoken = true;
                }
                else
                {
                    _completeSpoken = true;
                }
            }

            _ = RunAsync(snapshot.Stage, treatment, _shutdown.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "De-icing packs procedure failed to start");
        }
    }

    /// <summary>One edge: the call, the wait, the read-only check. Public so tests can drive
    /// it with a zero delay.</summary>
    public async Task RunAsync(DeiceStage stage, bool treatment, CancellationToken cancellationToken, TimeSpan? verifyDelay = null)
    {
        try
        {
            var call = DeicePacksProcedureCore.CallFor(stage);
            if (call is null)
            {
                return;
            }

            _eventLog.Record("deice-procedure", new { stage = stage.ToString(), spoken = call });
            await _arbiter.SpeakAsync(call, SpeechPriority.High, cancellationToken).ConfigureAwait(false);

            await Task.Delay(verifyDelay ?? DeicePacksProcedureCore.VerifyDelay, cancellationToken).ConfigureAwait(false);

            var lights = ReadLights();
            if (lights is null)
            {
                _logger.LogInformation("De-icing packs procedure: overhead lights not reported by ProSim — check skipped");
                _eventLog.Record("deice-procedure", new { stage = stage.ToString(), verified = (bool?)null, reason = "lights not reported" });
                return;
            }

            var discrepancies = DeicePacksProcedureCore.Discrepancies(lights, treatment);
            var phrase = DeicePacksProcedureCore.VerifyPhrase(discrepancies, treatment);
            _eventLog.Record("deice-procedure", new
            {
                stage = stage.ToString(),
                verified = discrepancies.Count == 0,
                missing = discrepancies,
                pack1Off = lights.Pack1Off,
                pack2Off = lights.Pack2Off,
                ditchingOn = lights.DitchingOn,
                spoken = phrase,
            });
            await _arbiter.SpeakAsync(phrase, SpeechPriority.Normal, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "De-icing packs procedure failed");
        }
    }

    /// <summary>Null until ProSim has pushed all three lights (no guessing on a cold cache).</summary>
    private DeicePacksProcedureCore.Lights? ReadLights()
    {
        if (_pack1Off is null || _pack2Off is null || _ditchingOn is null
            || _pack1Off.RawValue is null || _pack2Off.RawValue is null || _ditchingOn.RawValue is null)
        {
            return null;
        }

        return new DeicePacksProcedureCore.Lights(_pack1Off.Value != 0, _pack2Off.Value != 0, _ditchingOn.Value != 0);
    }
}
