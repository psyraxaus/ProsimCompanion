using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Company;

/// <summary>The push seam other features (company day mode's next-sector and deviation
/// messages) use to deliver a message over the company channel without owning chime/repeat
/// mechanics themselves.</summary>
public interface ICompanyChannel
{
    /// <summary>Delivers <paramref name="text"/> as an inbound company message: spoken at Low
    /// with the company chime (per options), and set as the repeat-last message. No busy or
    /// enabled gate — a pushed message is an explicit request; blank text no-ops.</summary>
    void DeliverMessage(string text);
}

/// <summary>
/// Company / ACARS-style channel (Prosim2FO semantics): a spoken loadsheet from real
/// weight/pax/CG datarefs — numbers locked, invariant-culture formatted, delivered in the FO
/// voice with the ACARS double-beep and optionally persisted beside the session log — plus an
/// optional deterministic mid-cruise company message (the predecessor was LLM-only with a
/// silence fallback; the deterministic template floor follows this repo's briefing philosophy).
/// "Read last company message" repeats the most recent message (the loadsheet deliberately
/// does not set it — predecessor parity). PDC/clearance readout stays out of scope.
/// </summary>
public sealed class CompanyChannelService : IVoiceFeature, ICompanyChannel, Core.Hosting.IStartupModule, IDisposable
{
    private static readonly string[] LoadsheetPhrases =
        ["request loadsheet", "loadsheet please", "read the loadsheet", "loadsheet"];

    private static readonly string[] RepeatPhrases =
        ["read last company message", "repeat company message", "last company message", "say again company"];

    private readonly IProsimDataRefs _dataRefs;
    private readonly IFlightPhaseSource _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly OfpStore _ofp;
    private readonly IOptionsMonitor<CompanyOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<CompanyChannelService> _logger;

    // Optional: with no name source the destination stays spelled ("E G L L") — issue #70.
    private readonly Core.Speech.ISpokenText _spokenText;
    private readonly List<IDataRefSubscription> _reads = [];
    private readonly object _gate = new();

    private IDataRefSubscription<double>? _zfw;
    private IDataRefSubscription<double>? _gross;
    private IDataRefSubscription<double>? _fobKg; // the explicitly-kg fuel ref — aircraft.fuel.total.amount is unit-ambiguous
    private IDataRefSubscription<double>? _cg;
    private IDataRefSubscription<int>? _zone1;
    private IDataRefSubscription<int>? _zone2;
    private IDataRefSubscription<int>? _zone3;
    private IDataRefSubscription<int>? _zone4;
    private IDataRefSubscription<string?>? _finalLoadsheet;
    private Timer? _timer;
    private bool _busy;
    private bool _loadsheetDone;
    private bool _cruiseRolled;
    private string? _lastMessage;

    public CompanyChannelService(
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        OfpStore ofp,
        IOptionsMonitor<CompanyOptions> options,
        JsonlEventLog eventLog,
        ILogger<CompanyChannelService> logger,
        Core.Speech.ISpokenText spokenText)
    {
        ArgumentNullException.ThrowIfNull(spokenText);
        _spokenText = spokenText;
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _flight = flight;
        _arbiter = arbiter;
        _ofp = ofp;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public bool Enabled => true;

    public IEnumerable<string> Phrases => LoadsheetPhrases.Concat(RepeatPhrases);

    public bool ValueParse => false;

    public void Start()
    {
        _zfw = Track(_dataRefs.Subscribe(ProsimDataRefNames.WeightZfw));
        _gross = Track(_dataRefs.Subscribe(ProsimDataRefNames.WeightGross));
        _fobKg = Track(_dataRefs.Subscribe(ProsimDataRefNames.FuelTotal));
        _cg = Track(_dataRefs.Subscribe(ProsimDataRefNames.CenterOfGravity));
        _zone1 = Track(_dataRefs.Subscribe(ProsimDataRefNames.PaxZone1Amount));
        _zone2 = Track(_dataRefs.Subscribe(ProsimDataRefNames.PaxZone2Amount));
        _zone3 = Track(_dataRefs.Subscribe(ProsimDataRefNames.PaxZone3Amount));
        _zone4 = Track(_dataRefs.Subscribe(ProsimDataRefNames.PaxZone4Amount));
        _finalLoadsheet = Track(_dataRefs.Subscribe(ProsimDataRefNames.EfbFinalLoadsheet));
        _flight.PhaseChanged += OnPhaseChanged;
        _timer = new Timer(_ => Tick(), null, 2000, 2000);
    }

    public void Dispose()
    {
        _flight.PhaseChanged -= OnPhaseChanged;
        _timer?.Dispose();
        foreach (var read in _reads)
        {
            read.Dispose();
        }
    }

    private IDataRefSubscription<T> Track<T>(IDataRefSubscription<T> subscription)
    {
        _reads.Add(subscription);
        return subscription;
    }

    public bool TryHandle(string utterance)
    {
        var text = utterance.Trim();
        if (LoadsheetPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            _ = DeliverLoadsheetAsync(manual: true);
            return true;
        }

        if (RepeatPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            _ = RepeatLastAsync();
            return true;
        }

        return false;
    }

    /// <summary>The web page's "request loadsheet" button — same path as the voice command.</summary>
    public void RequestLoadsheet() => _ = DeliverLoadsheetAsync(manual: true);

    /// <inheritdoc />
    public void DeliverMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_gate)
        {
            _lastMessage = text; // "read last company message" repeats it — a pushed message counts
        }

        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Low, Tag: "company.day",
            Chime: _options.CurrentValue.Chime ? "company" : null,
            Role: SpeechRole.Company));
        _eventLog.Record("company.message", new { text, pushed = true });
    }

    /// <summary>Composes the spoken loadsheet. Tonnes to 1 dp, invariant culture — a European
    /// locale must never emit "62,3". Pure and static for tests.</summary>
    public static string BuildLoadsheet(double zfwKg, double towKg, double fobKg, double cgPercent, int pax)
    {
        var parts = new List<string> { "Loadsheet." };
        if (zfwKg > 0)
        {
            parts.Add($"Zero fuel weight {Tonnes(zfwKg)} tonnes.");
        }

        if (towKg > 0)
        {
            parts.Add($"Take-off weight {Tonnes(towKg)} tonnes.");
        }

        if (fobKg > 0)
        {
            parts.Add($"Fuel on board {Tonnes(fobKg)} tonnes.");
        }

        if (pax > 0)
        {
            parts.Add($"Passengers, {pax.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (cgPercent > 0)
        {
            parts.Add($"Center of gravity {cgPercent.ToString("0.0", CultureInfo.InvariantCulture)} percent.");
        }

        return string.Join(" ", parts);
    }

    private static string Tonnes(double kg) => (kg / 1000.0).ToString("0.0", CultureInfo.InvariantCulture);

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        if (e.Current == FlightPhase.ColdAndDark
            || (e.Current == FlightPhase.Preflight && e.Previous is FlightPhase.Shutdown or FlightPhase.TaxiIn))
        {
            lock (_gate)
            {
                _loadsheetDone = false;
                _cruiseRolled = false;
                _lastMessage = null;
            }
        }
    }

    private void Tick()
    {
        try
        {
            var options = _options.CurrentValue;
            // Flight-live gate (issue #114): ProSim pushes plausible data with no MSFS session.
            if (!options.Enabled || !_flight.IsLive)
            {
                return;
            }

            lock (_gate)
            {
                if (_busy)
                {
                    return;
                }
            }

            if (_reads.Any(r => r.IsStale))
            {
                return;
            }

            var phase = _flight.CurrentPhase;
            if (options.AutoLoadsheet && !_loadsheetDone
                && phase is FlightPhase.Preflight or FlightPhase.Departure or FlightPhase.PushbackAndStart
                && LoadsheetReady())
            {
                _loadsheetDone = true;
                _ = DeliverLoadsheetAsync(manual: false);
                return;
            }

            if (options.CruiseMessages && !_cruiseRolled && phase == FlightPhase.Cruise)
            {
                _cruiseRolled = true;
                if (Random.Shared.NextDouble() < Math.Clamp(options.CruiseMessageProbability, 0, 1))
                {
                    _ = DeliverCruiseMessageAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Company tick failed");
        }
    }

    /// <summary>Ready once the final loadsheet has landed in the EFB, else once the weights
    /// are plausibly populated (sanity floors, not real limits).</summary>
    private bool LoadsheetReady()
        => !string.IsNullOrWhiteSpace(_finalLoadsheet?.Value)
            || ((_zfw?.Value ?? 0.0) > 1000 && (_fobKg?.Value ?? 0.0) > 100);

    private async Task DeliverLoadsheetAsync(bool manual)
    {
        lock (_gate)
        {
            if (_busy)
            {
                return;
            }

            _busy = true;
        }

        try
        {
            if (!LoadsheetReady())
            {
                if (manual)
                {
                    await _arbiter.EnqueueAsync(new SpeechRequest(
                        "The loadsheet is not available yet.", SpeechPriority.Normal,
                        Tag: "company.loadsheet")).ConfigureAwait(false);
                }

                return;
            }

            var pax = (_zone1?.Value ?? 0) + (_zone2?.Value ?? 0)
                + (_zone3?.Value ?? 0) + (_zone4?.Value ?? 0);
            var spoken = BuildLoadsheet(
                _zfw?.Value ?? 0.0, _gross?.Value ?? 0.0,
                _fobKg?.Value ?? 0.0, _cg?.Value ?? 0.0, pax);

            Persist(spoken);
            var options = _options.CurrentValue;
            await _arbiter.EnqueueAsync(new SpeechRequest(
                spoken, SpeechPriority.Normal, Tag: "company.loadsheet",
                Chime: options.Chime ? "company" : null,
                Role: SpeechRole.Company)).ConfigureAwait(false);
            _eventLog.Record("company.loadsheet", new { text = spoken, manual });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Loadsheet delivery failed");
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
            }
        }
    }

    private async Task DeliverCruiseMessageAsync()
    {
        try
        {
            var destination = _ofp.Current?.DestinationIcao;
            var sb = new StringBuilder("Company. ");
            // Spoken name when known ("into Heathrow"), spelled ICAO otherwise (issue #70).
            sb.Append(string.IsNullOrWhiteSpace(destination)
                ? "No significant updates for the arrival. "
                : $"No significant updates for the arrival into {_spokenText.Airport(destination)}. ");
            sb.Append("Gate will be advised on arrival.");
            var text = sb.ToString();

            lock (_gate)
            {
                _lastMessage = text;
            }

            await _arbiter.EnqueueAsync(new SpeechRequest(
                text, SpeechPriority.Low, Ttl: TimeSpan.FromSeconds(60),
                IsStillValid: () => _flight.CurrentPhase == FlightPhase.Cruise,
                Tag: "company.message",
                Chime: _options.CurrentValue.Chime ? "company" : null,
                Role: SpeechRole.Company)).ConfigureAwait(false);
            _eventLog.Record("company.message", new { text });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cruise company message failed");
        }
    }

    private async Task RepeatLastAsync()
    {
        string? last;
        lock (_gate)
        {
            last = _lastMessage;
        }

        if (last is null)
        {
            await _arbiter.EnqueueAsync(new SpeechRequest(
                "No company messages received.", SpeechPriority.Normal, Tag: "company.repeat"))
                .ConfigureAwait(false);
            return;
        }

        // Replaying company content keeps the company voice; the "no messages" line above
        // stays role-less (the FO answering).
        await _arbiter.EnqueueAsync(new SpeechRequest(
            last, SpeechPriority.Normal, Tag: "company.repeat",
            Chime: _options.CurrentValue.Chime ? "company" : null,
            Role: SpeechRole.Company)).ConfigureAwait(false);
    }

    /// <summary>Writes the spoken loadsheet (and the raw EFB loadsheet when present) beside
    /// the JSONL session log. Best-effort — a failed write never blocks the delivery.</summary>
    private void Persist(string spoken)
    {
        try
        {
            if (!_options.CurrentValue.PersistLoadsheet)
            {
                return;
            }

            var sessionPath = _eventLog.Path;
            if (string.IsNullOrWhiteSpace(sessionPath))
            {
                return;
            }

            var sb = new StringBuilder(spoken);
            var raw = _finalLoadsheet?.Value;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                sb.Append("\n\n--- EFB final loadsheet ---\n").Append(raw);
            }

            File.WriteAllText(System.IO.Path.ChangeExtension(sessionPath, ".loadsheet.txt"), sb.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist loadsheet");
        }
    }
}
