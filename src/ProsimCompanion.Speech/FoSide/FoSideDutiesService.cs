using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.FoSide;

/// <summary>
/// The FO's own altimeter and EFIS (issue #188): baro follow (<see cref="BaroFollowCore"/> on a
/// 1 Hz tick), "QNH one zero one three" / "your side standard" by voice, and "your range forty" /
/// "ND to arc". Every write goes through <see cref="FoSideDutiesGate"/> (four FO-side names,
/// seat-mapped) and the FCU flow — announce, a 3 s cancel window, write, verify once more on a
/// miss, "set" or "unable" — except the ND selectors, which are instant and harmless to redo.
/// The bare "set standard" / "set QNH" stay the file commands.
/// </summary>
public sealed class FoSideDutiesService : IVoiceFeature, Core.Hosting.IStartupModule, IDisposable
{
    public const string UnableBaroText = "Unable — your altimeter.";
    public const string UnableNdText = "Unable — your ND.";
    public const string InchesText = "My altimeter is in inches — your call, captain.";

    private static readonly string[] CancelWords = ["negative", "disregard", "cancel", "belay that"];
    private static readonly TimeSpan CancelWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VerifyWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PressHold = TimeSpan.FromMilliseconds(300);

    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IProsimDataRefs _dataRefs;
    private readonly IFlightPhaseSource _flight;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FoSideDutiesService> _logger;
    private readonly GroundOpsSignals _signals;
    private readonly BaroFollowCore _follow = new();
    private readonly SemaphoreSlim _oneWrite = new(1, 1);
    private readonly object _gate = new();
    private readonly bool _rightSeat;
    private readonly IDataRefSubscription<bool> _captainStd;
    private readonly IDataRefSubscription<double> _captainHpa;
    private readonly IDataRefSubscription<bool> _foStd;
    private readonly IDataRefSubscription<double> _foHpa;
    private readonly IDataRefSubscription<int> _foMode;
    private readonly IDataRefSubscription<int> _foNdMode;
    private readonly IDataRefSubscription<int> _foNdZoom;
    private CancellationTokenSource? _pending;
    private Timer? _timer;
    private int _ticking;

    public FoSideDutiesService(
        IOptionsMonitor<SpeechOptions> speech,
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<FoSideDutiesService> logger,
        GroundOpsSignals signals)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(signals);
        _speech = speech;
        _dataRefs = dataRefs;
        _flight = flight;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _signals = signals;

        // The seat is read once: the FO's side is fixed for the session (a seat change needs a restart, as for the FCU).
        _rightSeat = PilotSeatMap.HumanIsRightSeat(speech.CurrentValue);
        _captainStd = dataRefs.Subscribe(Side(ProsimDataRefNames.Efis1BaroStdGate));
        _captainHpa = dataRefs.Subscribe(Side(ProsimDataRefNames.Efis1BaroHpa));
        _foStd = dataRefs.Subscribe(Side(ProsimDataRefNames.Efis2BaroStdGate));
        _foHpa = dataRefs.Subscribe(Side(ProsimDataRefNames.Efis2BaroHpa));
        _foMode = dataRefs.Subscribe(Side(ProsimDataRefNames.Efis2BaroMode));
        // The ND selectors have string constants only (one catalogue symbol per wire name): typed here for the verify-read.
        _foNdMode = dataRefs.Subscribe(Side(new DataRef<int>(ProsimDataRefNames.Efis2NdMode, DataRefTier.Normal, 2)));
        _foNdZoom = dataRefs.Subscribe(Side(new DataRef<int>(ProsimDataRefNames.Efis2NdZoom, DataRefTier.Normal, 2)));
    }

    /// <summary>Roster declaration: right after the FCU (the same flow), before the file commands.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 65,
        Gate = VoiceFeatureGate.ByOption("speech.foSide.baroFollow", "voiceReference.off.foSide"),
        Description = new DescribedVoiceReference("foSide", "voiceReference.group.foSide.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["qnh one zero one three", "set qnh one zero one three"], "voiceReference.group.foSide.qnh", "Setting QNH one zero one three.", [VoiceBadges.Value], "‹one zero one three›"),
            new(["your side standard", "standard on your side"], "voiceReference.group.foSide.standard", "Standard."),
            new(["your range forty", "range forty on your side"], "voiceReference.group.foSide.range", "Range forty.", [VoiceBadges.Value], "‹forty›"),
            new(["nd to arc", "your nd rose nav", "plan on your side"], "voiceReference.group.foSide.mode", "Arc."),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _speech.CurrentValue.FoSide.BaroFollow || _speech.CurrentValue.FoSide.EfisCommands;

    public IEnumerable<string> Phrases =>
    [
        .. CancelWords, "qnh", "set qnh", "on your side", "your side standard", "standard on your side",
        "your range", "range", "nd to arc", "your nd", "rose nav", "rose vor", "rose ils", "arc", "plan",
        "ten", "twenty", "forty", "eighty", "one sixty", "three twenty",
        .. NumberExtractor.GrammarWords,
    ];

    public bool ValueParse => true;

    public void Start()
    {
        _signals.FlightCycleReset += OnReset;
        _flight.SessionReset += OnReset;
        _timer = new Timer(_ => Tick(), null, 2000, 1000);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        CancelPending();
        _signals.FlightCycleReset -= OnReset;
        _flight.SessionReset -= OnReset;
        foreach (var read in new IDataRefSubscription[] { _captainStd, _captainHpa, _foStd, _foHpa, _foMode, _foNdMode, _foNdZoom })
        {
            read.Dispose();
        }
    }

    private void OnReset()
    {
        lock (_gate)
        {
            _follow.Reset();
        }
    }

    // ---- voice ----

    public bool TryHandle(string utterance)
    {
        var options = _speech.CurrentValue.FoSide;
        var normalized = CommandMatcher.Normalize(utterance);
        if (CancelWords.Contains(normalized))
        {
            if (CancelPending())
            {
                _ = _arbiter.SpeakAsync("Disregard.", SpeechPriority.High);
                _eventLog.Record("fo.cancelled", new { });
                return true;
            }

            return false;
        }

        if (options.BaroFollow)
        {
            if (FoSidePhrases.TryParseQnh(utterance, out var hpa))
            {
                _ = RunBaroAsync(new BaroAction(_foStd.Value ? BaroActionKind.SetQnh : BaroActionKind.SetValue, hpa), "voice");
                return true;
            }

            if (FoSidePhrases.IsYourSideStandard(utterance))
            {
                _ = RunBaroAsync(new BaroAction(BaroActionKind.SetStd, 0), "voice");
                return true;
            }
        }

        if (options.EfisCommands)
        {
            if (FoSidePhrases.TryParseRange(utterance, out var zoom, out var unknownRange))
            {
                _ = RunNdAsync(Side(ProsimDataRefNames.Efis2NdZoom), _foNdZoom, zoom, "range", $"Range {RangeWord(zoom)}.");
                return true;
            }

            if (unknownRange)
            {
                _ = _arbiter.SpeakAsync("Range ten, twenty, forty, eighty, one sixty or three twenty — which one?", SpeechPriority.High);
                return true;
            }

            if (FoSidePhrases.TryParseMode(utterance, out var mode))
            {
                _ = RunNdAsync(Side(ProsimDataRefNames.Efis2NdMode), _foNdMode, mode, "mode", FoSidePhrases.ModeNames[mode] + ".");
                return true;
            }
        }

        return false;
    }

    // ---- follow ----

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_speech.CurrentValue.FoSide.BaroFollow)
            {
                return;
            }

            var action = Evaluate(Sample(DateTimeOffset.UtcNow));
            if (action is not null)
            {
                _ = RunBaroAsync(action, "follow");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FO baro follow tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>One follow evaluation — public for tests.</summary>
    public BaroAction? Evaluate(BaroSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            return _follow.Evaluate(sample);
        }
    }

    private BaroSample Sample(DateTimeOffset nowUtc)
    {
        var live = _flight.IsLive && !_captainStd.IsStale && !_foStd.IsStale && _captainStd.RawValue is not null && _foStd.RawValue is not null;
        return new BaroSample(live, _captainStd.Value, _captainHpa.Value, _foStd.Value, _foHpa.Value, _foMode.Value != 0, _flight.CurrentPhase, nowUtc);
    }

    // ---- the gate flow ----

    private async Task RunBaroAsync(BaroAction action, string source)
    {
        if (action.Kind == BaroActionKind.InchesNotice)
        {
            _eventLog.Record("fo.baroSet", new { kind = "inchesNotice", source, ok = false });
            await _arbiter.SpeakAsync(InchesText, SpeechPriority.High).ConfigureAwait(false);
            return;
        }

        if (_foMode.Value == 0 && action.Kind != BaroActionKind.SetStd)
        {
            _eventLog.Record("fo.baroSet", new { kind = action.Kind.ToString(), requested = action.Hpa, source, ok = false, reason = "inches" });
            await _arbiter.SpeakAsync(InchesText, SpeechPriority.High).ConfigureAwait(false);
            return;
        }

        CancelPending();
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _pending = cts;
        }

        var announce = action.Kind == BaroActionKind.SetStd ? "Standard." : $"QNH {Aviation.ToDigits(action.Hpa.ToString(CultureInfo.InvariantCulture))}.";
        var foStdBefore = _foStd.Value;
        var foHpaBefore = Math.Round(_foHpa.Value);
        try
        {
            _eventLog.Record("fo.baroAnnounce", new { kind = action.Kind.ToString(), requested = action.Hpa, source });
            await _arbiter.EnqueueAsync(new SpeechRequest(announce, SpeechPriority.High, Tag: "fo.baro")).ConfigureAwait(false);
            await Task.Delay(CancelWindow, cts.Token).ConfigureAwait(false);

            // The pilot touched the FO knob during the window: their knob, back off.
            if (_foStd.Value != foStdBefore || Math.Abs(Math.Round(_foHpa.Value) - foHpaBefore) >= 1)
            {
                _eventLog.Record("fo.baroSet", new { kind = action.Kind.ToString(), requested = action.Hpa, source, ok = false, reason = "backoff" });
                await _arbiter.EnqueueAsync(new SpeechRequest("You're on my altimeter — your knob.", SpeechPriority.High, Tag: "fo.baro")).ConfigureAwait(false);
                return;
            }

            await _oneWrite.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                var ok = await ApplyBaroAsync(action, cts.Token).ConfigureAwait(false)
                    || await ApplyBaroAsync(action, cts.Token).ConfigureAwait(false);
                var readBack = action.Kind == BaroActionKind.SetStd ? (double)(_foStd.Value ? 1 : 0) : Math.Round(_foHpa.Value);
                _eventLog.Record("fo.baroSet", new { kind = action.Kind.ToString(), requested = action.Hpa, readBack, foStd = _foStd.Value, source, ok });
                if (ok)
                {
                    _logger.LogInformation("FO altimeter {Kind} {Hpa} set ({Source})", action.Kind, action.Hpa, source);
                    await _arbiter.EnqueueAsync(new SpeechRequest(
                        action.Kind == BaroActionKind.SetStd ? "Standard set." : $"QNH {Aviation.ToDigits(action.Hpa.ToString(CultureInfo.InvariantCulture))} set.",
                        SpeechPriority.High, Tag: "fo.baro")).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning("FO altimeter {Kind} {Hpa} did not verify twice — inhibited for the cycle", action.Kind, action.Hpa);
                    lock (_gate)
                    {
                        _follow.Inhibit();
                    }

                    await _arbiter.EnqueueAsync(new SpeechRequest(UnableBaroText, SpeechPriority.High, Tag: "fo.baro")).ConfigureAwait(false);
                }
            }
            finally
            {
                _oneWrite.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // "negative" or a newer action.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FO altimeter action {Kind} failed", action.Kind);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, cts))
                {
                    _pending = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task<bool> ApplyBaroAsync(BaroAction action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case BaroActionKind.SetStd:
                // The F/O knob PUSH: value 2 (the catalogue legend is inverted, settled live 2026-08-29, #109).
                await PressAsync(Side(ProsimDataRefNames.Efis2BaroStd), 2, ct).ConfigureAwait(false);
                return await WaitForAsync(() => _foStd.Value, ct).ConfigureAwait(false);

            case BaroActionKind.SetQnh:
                await WriteAsync(Side(ProsimDataRefNames.Efis2BaroHpaInput), action.Hpa, ct).ConfigureAwait(false);
                await PressAsync(Side(ProsimDataRefNames.Efis2BaroStd), 1, ct).ConfigureAwait(false); // PULL = QNH
                return await WaitForAsync(() => !_foStd.Value && Math.Abs(_foHpa.Value - action.Hpa) <= 1, ct).ConfigureAwait(false);

            default:
                await WriteAsync(Side(ProsimDataRefNames.Efis2BaroHpaInput), action.Hpa, ct).ConfigureAwait(false);
                return await WaitForAsync(() => Math.Abs(_foHpa.Value - action.Hpa) <= 1, ct).ConfigureAwait(false);
        }
    }

    private async Task RunNdAsync(string dataref, IDataRefSubscription<int> readBack, int value, string field, string spoken)
    {
        try
        {
            await _oneWrite.WaitAsync().ConfigureAwait(false);
            try
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(spoken, SpeechPriority.High, Tag: "fo.efis")).ConfigureAwait(false);
                await WriteAsync(dataref, value, CancellationToken.None).ConfigureAwait(false);
                var ok = await WaitForAsync(() => readBack.Value == value, CancellationToken.None, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                _eventLog.Record("fo.efisSet", new { field, value, readBack = readBack.Value, ok });
                _logger.LogInformation("FO ND {Field} → {Value}: {Outcome}", field, value, ok ? "set" : "did not read back");
                if (!ok)
                {
                    await _arbiter.EnqueueAsync(new SpeechRequest(UnableNdText, SpeechPriority.High, Tag: "fo.efis")).ConfigureAwait(false);
                }
            }
            finally
            {
                _oneWrite.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FO ND {Field} failed", field);
        }
    }

    private async Task PressAsync(string dataref, int press, CancellationToken ct)
    {
        await WriteAsync(dataref, press, ct).ConfigureAwait(false);
        await Task.Delay(PressHold, ct).ConfigureAwait(false);
        await WriteAsync(dataref, 0, ct).ConfigureAwait(false);
    }

    private Task WriteAsync(string dataref, int value, CancellationToken ct)
    {
        FoSideDutiesGate.EnsureAllowed(dataref, _rightSeat);
        return _dataRefs.WriteAsync(dataref, value, ct);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken ct, TimeSpan? limit = null)
    {
        var deadline = DateTimeOffset.UtcNow + (limit ?? VerifyWait);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return condition();
    }

    private bool CancelPending()
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                return false;
            }

            _pending.Cancel();
            _pending = null;
            return true;
        }
    }

    private string Side(string dataref) => PilotSeatMap.Map(dataref, _rightSeat);

    private DataRef<T> Side<T>(DataRef<T> dataref) => PilotSeatMap.Map(dataref, _rightSeat);

    private static string RangeWord(int index) => index switch
    {
        0 => "ten", 1 => "twenty", 2 => "forty", 3 => "eighty", 4 => "one sixty", _ => "three twenty",
    };
}
