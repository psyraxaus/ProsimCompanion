using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>
/// "Check the box" — the FO's MCDU check in the D-I-F-S-R-I-P-P order (owner request
/// 2026-11-15). It replaces the predecessor's key macro, which turned eight pages in five
/// seconds, read nothing, never reached INIT B (its "arrow right" was pressed on RAD NAV, where
/// it does nothing) and answered "Checked" whatever the box held.
/// <para>The FO acknowledges, opens each page at a reading pace, reads the screen
/// (<see cref="BoxCheckPages"/>), compares it with the plan, the loadsheet and the computed
/// takeoff figures (<see cref="BoxCheckEvaluator"/>), reports the differences, and then offers
/// to enter what it knows the answer to — one question at a time, entered only on a yes.</para>
/// <para>Turning pages needs only <c>mcdu.enabled</c>: page keys, slew arrows and page links
/// checked against the screen text. Weights and takeoff speeds are written as datarefs
/// (dataref-first). An entry that needs MCDU keys (flight number, cost index, cruise level,
/// COPY ACTIVE) is offered only while <c>mcdu.allowActuation</c> is on. The route, the SID and
/// a discontinuity are reported and never changed.</para>
/// </summary>
public sealed class McduBoxCheckService : IVoiceFeature, IBoxCheckControl, IDisposable
{
    public const string EventName = "mcdu.boxCheck";
    public const string FixEventName = "mcdu.boxCheck.fix";

    private static readonly string[] CheckPhrases = ["check the box", "box check", "check the mcdu", "check the m c d u", "check the fms"];
    private static readonly string[] StopPhrases = ["stop the check", "stop the box check", "cancel the check", "cancel the box check"];
    private static readonly HashSet<string> CheckNormalized = [.. CheckPhrases.Select(CommandMatcher.Normalize)];
    private static readonly HashSet<string> StopNormalized = [.. StopPhrases.Select(CommandMatcher.Normalize)];

    /// <summary>Slew presses between two reads of the F-PLN: the screen holds five legs, so
    /// four presses keep one line of overlap for the join.</summary>
    private const int ScrollBurst = 4;

    /// <summary>Upper bound on F-PLN slew presses (the list wraps round to the origin).</summary>
    private const int MaxScrollPresses = 160;

    private readonly IMcduReader _reader;
    private readonly IMcduActuator _actuator;
    private readonly IOptionsMonitor<McduOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<McduBoxCheckService> _logger;
    private readonly BoxCheckStore _store;
    private readonly BoxCheckFixStore _fixes;
    private readonly OfpStore _ofp;
    private readonly LoadsheetStore _loadsheet;
    private readonly TakeoffPerfStore _perf;
    private readonly ISimClock _clock;
    private readonly ISpokenText _spokenText;
    private readonly IProsimDataRefs _dataRefs;
    private readonly IFmsInitSync? _initSync;
    private readonly object _gate = new();
    private CancellationTokenSource? _running;

    public McduBoxCheckService(
        IMcduReader reader,
        IMcduActuator actuator,
        IOptionsMonitor<McduOptions> options,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<McduBoxCheckService> logger,
        BoxCheckStore store,
        BoxCheckFixStore fixes,
        OfpStore ofp,
        LoadsheetStore loadsheet,
        TakeoffPerfStore perf,
        ISimClock clock,
        ISpokenText spokenText,
        IProsimDataRefs dataRefs,
        IFmsInitSync? initSync = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(actuator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fixes);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(loadsheet);
        ArgumentNullException.ThrowIfNull(perf);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(spokenText);
        ArgumentNullException.ThrowIfNull(dataRefs);
        _reader = reader;
        _actuator = actuator;
        _options = options;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _store = store;
        _fixes = fixes;
        _ofp = ofp;
        _loadsheet = loadsheet;
        _perf = perf;
        _clock = clock;
        _spokenText = spokenText;
        _dataRefs = dataRefs;
        _initSync = initSync;
    }

    /// <summary>Roster declaration: ahead of the configured key macros (70), so an edited
    /// commands.json that still carries the old "check the box" macro does not take the phrase.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 67,
        Gate = VoiceFeatureGate.ByOption("mcdu.enabled", VoiceReferenceTextIds.OffMcdu),
        Description = new DescribedVoiceReference("mcduBoxCheck", "voiceReference.group.mcduBoxCheck.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["check the box", "box check", "check the mcdu"], "voiceReference.group.mcduBoxCheck.check", "Checking the box. … Box checked, no differences."),
            new(["stop the check", "cancel the check"], "voiceReference.group.mcduBoxCheck.stop"),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _options.CurrentValue.Enabled;

    public IEnumerable<string> Phrases => [.. CheckPhrases, .. StopPhrases];

    public bool ValueParse => false;

    /// <summary>True while a check (pages, report or fix offers) is in progress.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running is not null;
            }
        }
    }

    public bool TryHandle(string utterance)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        if (StopNormalized.Contains(text))
        {
            return Stop(); // false with nothing running: the words are not ours then
        }

        if (!CheckNormalized.Contains(text))
        {
            return false;
        }

        switch (Start("voice"))
        {
            case BoxCheckStart.AlreadyRunning:
                Speak("Still checking.");
                break;
            case BoxCheckStart.Unavailable:
                Speak("Unable — I can't see the M C D U; ProSim isn't connected.");
                _eventLog.Record(EventName, new { source = "voice", ok = false, reason = "no-display" });
                break;
        }

        return true;
    }

    public BoxCheckStart Start(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!_actuator.CanNavigate)
        {
            return BoxCheckStart.Unavailable;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running is not null)
            {
                return BoxCheckStart.AlreadyRunning;
            }

            cts = new CancellationTokenSource();
            _running = cts;
        }

        _ = Task.Run(() => RunAsync(source, cts));
        return BoxCheckStart.Started;
    }

    /// <summary>Stops a running check; false when none runs.</summary>
    public bool Stop()
    {
        lock (_gate)
        {
            if (_running is null)
            {
                return false;
            }

            _running.Cancel();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running?.Cancel();
        }
    }

    /// <summary>The whole run: pages, report, fix offers — public for tests.</summary>
    public async Task RunAsync(string source, CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
        var ct = cts.Token;
        var started = DateTimeOffset.UtcNow;
        var findings = new List<BoxCheckFinding>();
        var notChecked = new List<string>();
        var completed = false;
        try
        {
            _store.Begin(source);
            _logger.LogInformation("MCDU check started ({Source})", source);
            Speak("Checking the box.");
            var reference = Reference();

            await ItemAsync(BoxCheckItemIds.Data, () => ReadDataAsync(reference, ct)).ConfigureAwait(false);
            await ItemAsync(BoxCheckItemIds.InitA, async () => BoxCheckEvaluator.InitA(BoxCheckPages.InitA(await OpenInitAsync(second: false, ct).ConfigureAwait(false)), reference)).ConfigureAwait(false);
            await ItemAsync(BoxCheckItemIds.FlightPlan, async () => BoxCheckEvaluator.FlightPlan(await ReadFlightPlanAsync(ct).ConfigureAwait(false), reference)).ConfigureAwait(false);
            await ItemAsync(BoxCheckItemIds.Secondary, async () => BoxCheckEvaluator.Secondary(await ReadSecondaryAsync(ct).ConfigureAwait(false))).ConfigureAwait(false);
            await ItemAsync(BoxCheckItemIds.RadNav, async () => BoxCheckEvaluator.RadNav(BoxCheckPages.RadNav(await GoAsync("RAD_NAV", ct).ConfigureAwait(false)))).ConfigureAwait(false);
            await ItemAsync(BoxCheckItemIds.InitB, async () => BoxCheckEvaluator.InitB(BoxCheckPages.InitB(await OpenInitAsync(second: true, ct).ConfigureAwait(false)), reference)).ConfigureAwait(false);
            await ItemAsync(BoxCheckItemIds.Perf, async () => BoxCheckEvaluator.Perf(BoxCheckPages.PerfTakeoff(await GoAsync("PERF", ct).ConfigureAwait(false)), reference)).ConfigureAwait(false);
            await ItemAsync(BoxCheckItemIds.Prog, async () => BoxCheckEvaluator.Prog(BoxCheckPages.Prog(await GoAsync("PROG", ct).ConfigureAwait(false)))).ConfigureAwait(false);

            await HomeAsync().ConfigureAwait(false);
            _store.End(DateTimeOffset.UtcNow);
            completed = true;

            var report = BoxCheckEvaluator.Report(findings, [.. notChecked.Select(BoxCheckEvaluator.SpokenItem)]);
            Record(source, started, findings, notChecked, report, outcome: "complete");
            _logger.LogInformation(
                "MCDU check ({Source}): {Differences} difference(s), {Missing} not entered, {NotChecked} page group(s) not read — {Report}",
                source, findings.Count(f => f.State == BoxCheckItemState.Difference), findings.Count(f => f.State == BoxCheckItemState.NotEntered), notChecked.Count, report);
            await _arbiter.EnqueueAsync(new SpeechRequest(report, SpeechPriority.Normal, TimeSpan.FromMinutes(2), Tag: "mcdu.boxCheck"), ct).ConfigureAwait(false);

            await OfferFixesAsync(findings, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Speak("Check stopped.");
            Record(source, started, findings, notChecked, report: null, outcome: "stopped");
        }
        catch (BoxCheckAbortedException ex)
        {
            _logger.LogWarning("MCDU check given up: {Reason}", ex.Message);
            Speak("Unable to finish the check — I lost the M C D U.");
            Record(source, started, findings, notChecked, report: null, outcome: "aborted", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCDU check failed");
            Speak("Unable to finish the check.");
            Record(source, started, findings, notChecked, report: null, outcome: "failed", ex.Message);
        }
        finally
        {
            if (!completed)
            {
                await HomeAsync().ConfigureAwait(false);
                _store.End(DateTimeOffset.UtcNow);
            }

            lock (_gate)
            {
                if (ReferenceEquals(_running, cts))
                {
                    _running = null;
                }
            }

            cts.Dispose();
        }

        async Task ItemAsync(string itemId, Func<Task<BoxCheckItemResult>> read)
        {
            ct.ThrowIfCancellationRequested();
            _store.SetCurrent(itemId);
            var result = await read().ConfigureAwait(false);
            _store.SetItem(result.Item);
            findings.AddRange(result.Findings);
            if (result.Item.State == BoxCheckItemState.NotChecked)
            {
                notChecked.Add(itemId);
            }
        }
    }

    // ---- the pages ----

    private async Task<BoxCheckItemResult> ReadDataAsync(BoxCheckReference reference, CancellationToken ct)
    {
        // Three sub-pages of DATA INDEX, each back through the DATA key (captured 2026-11-15:
        // A/C STATUS is the 4th link, the monitors are told apart by their small label).
        var status = BoxCheckPages.AircraftStatus(await DataSubPageAsync("A/C STATUS", null, ct).ConfigureAwait(false));
        var irs = BoxCheckPages.IrsMonitor(await DataSubPageAsync("MONITOR", "IRS", ct).ConfigureAwait(false));
        var gps = BoxCheckPages.GpsMonitor(await DataSubPageAsync("MONITOR", "GPS", ct).ConfigureAwait(false));
        return BoxCheckEvaluator.Data(status, irs, gps, reference);
    }

    private async Task<McduPage> DataSubPageAsync(string link, string? label, CancellationToken ct)
    {
        var index = await GoAsync("DATA", ct, dwell: 0.3).ConfigureAwait(false);
        if (!BoxCheckPages.IsDataIndex(index) || BoxCheckPages.LinkRow(index, link, label) is not { } row
            || !await _actuator.FollowLinkAsync(row, right: false, link, ct).ConfigureAwait(false))
        {
            return McduPage.Empty;
        }

        var page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        await DwellAsync(0.5, ct).ConfigureAwait(false);
        return page;
    }

    /// <summary>INIT A, or INIT B (the INIT key, then the right arrow — the old macro pressed
    /// the arrow on RAD NAV and so never saw the fuel page).</summary>
    private async Task<McduPage> OpenInitAsync(bool second, CancellationToken ct)
    {
        var page = await GoAsync("INIT", ct, dwell: second ? 0.3 : 1).ConfigureAwait(false);
        if (BoxCheckPages.IsInitB(page) != second && (BoxCheckPages.IsInitA(page) || BoxCheckPages.IsInitB(page)))
        {
            page = await GoAsync(second ? "ARROW_RIGHT" : "ARROW_LEFT", ct).ConfigureAwait(false);
        }

        return page;
    }

    private async Task<FlightPlanFacts> ReadFlightPlanAsync(CancellationToken ct)
    {
        var page = await GoAsync("FPLN", ct, dwell: 0.6).ConfigureAwait(false);
        var lines = new List<string>(BoxCheckPages.LegLines(page));
        var presses = 0;
        var idle = 0;

        // The slew key moves the list one line (captured 2026-11-15: 33 presses for EGLL-LPPT,
        // then it wraps to the origin). Read every few presses and join on the overlap; stop at
        // the end marker, or when two bursts in a row add nothing.
        while (!lines.Any(l => l.Contains(BoxCheckPages.EndOfPlan, StringComparison.Ordinal)) && presses < MaxScrollPresses && idle < 2)
        {
            for (var i = 0; i < ScrollBurst; i++)
            {
                if (!await _actuator.NavigateAsync("ARROW_UP", ct).ConfigureAwait(false))
                {
                    throw new BoxCheckAbortedException("the slew key was refused");
                }

                presses++;
            }

            page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
            idle = BoxCheckPages.Merge(lines, BoxCheckPages.LegLines(page)) == 0 ? idle + 1 : 0;
            await DwellAsync(0.15, ct).ConfigureAwait(false);
        }

        // Back to the top of the plan, as the captain would expect to find it.
        await GoAsync("FPLN", ct, dwell: 0).ConfigureAwait(false);
        return BoxCheckPages.FlightPlan(lines);
    }

    private async Task<bool?> ReadSecondaryAsync(CancellationToken ct)
    {
        var index = await GoAsync("SEC_FPLN", ct, dwell: 0.5).ConfigureAwait(false);
        if (!BoxCheckPages.IsSecIndex(index) || BoxCheckPages.LinkRow(index, "SEC F-PLN") is not { } row
            || !await _actuator.FollowLinkAsync(row, right: false, "SEC F-PLN", ct).ConfigureAwait(false))
        {
            return null;
        }

        var page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        await DwellAsync(0.6, ct).ConfigureAwait(false);
        return page.HasData ? BoxCheckPages.SecondaryIsEmpty(page) : null;
    }

    private async Task<McduPage> GoAsync(string pageSuffix, CancellationToken ct, double dwell = 1)
    {
        if (!await _actuator.NavigateAsync(pageSuffix, ct).ConfigureAwait(false))
        {
            throw new BoxCheckAbortedException($"the {pageSuffix} page was refused");
        }

        var page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        await DwellAsync(dwell, ct).ConfigureAwait(false);
        return page;
    }

    /// <summary>Leaves the box on the F-PLN page; never throws, never cancelled.</summary>
    private async Task HomeAsync()
    {
        try
        {
            if (_actuator.CanNavigate)
            {
                await _actuator.NavigateAsync("FPLN", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCDU check: return to F-PLN failed");
        }
    }

    /// <summary>The reading time on a page: the configured seconds times
    /// <paramref name="share"/>, a quarter either way.</summary>
    private Task DwellAsync(double share, CancellationToken ct)
    {
        var seconds = PageSeconds(_options.CurrentValue.BoxCheckPageSeconds, share, Random.Shared.NextDouble());
        return seconds <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }

    /// <summary>Pure dwell rule: seconds × share × (0.75 … 1.25); the setting is held to 0–20 s.</summary>
    public static double PageSeconds(double configuredSeconds, double share, double random01)
        => Math.Clamp(configuredSeconds, 0, 20) * Math.Max(0, share) * (0.75 + (0.5 * Math.Clamp(random01, 0, 1)));

    private BoxCheckReference Reference()
    {
        var sheets = _loadsheet.Snapshot();
        var (sheet, name) = sheets.Final.Status == LoadsheetSlotStatus.Sent ? (sheets.Final, "final")
            : sheets.Prelim.Status == LoadsheetSlotStatus.Sent ? (sheets.Prelim, "preliminary")
            : (null, "");
        return new BoxCheckReference(
            _ofp.Current, sheet, name, _perf.Snapshot(),
            DateOnly.FromDateTime(_clock.UtcNowOrReal.UtcDateTime), _spokenText.Airport);
    }

    // ---- the fixes ----

    private async Task OfferFixesAsync(IReadOnlyList<BoxCheckFinding> findings, CancellationToken ct)
    {
        var fixes = findings.Where(f => f.Fix is not null).Select(f => f.Fix!).DistinctBy(f => f.Kind).ToList();
        if (fixes.Count == 0 || !_options.CurrentValue.BoxCheckOfferFixes)
        {
            return;
        }

        var needKeys = 0;
        var entered = false;
        foreach (var fix in fixes)
        {
            ct.ThrowIfCancellationRequested();
            if (fix.NeedsKeys && !_actuator.IsArmed)
            {
                needKeys++;
                _eventLog.Record(FixEventName, new { fix = fix.Kind.ToString(), offered = false, reason = "actuation-off" });
                continue;
            }

            if (fix.Kind == BoxCheckFixKind.InitB && _initSync is null)
            {
                _eventLog.Record(FixEventName, new { fix = fix.Kind.ToString(), offered = false, reason = "no-init-sync" });
                continue;
            }

            var accepted = await AskAsync(fix, ct).ConfigureAwait(false);
            if (!accepted)
            {
                _eventLog.Record(FixEventName, new { fix = fix.Kind.ToString(), offered = true, accepted = false });
                continue;
            }

            var (ok, said) = await ApplyAsync(fix, ct).ConfigureAwait(false);
            entered |= fix.NeedsKeys;
            _eventLog.Record(FixEventName, new { fix = fix.Kind.ToString(), offered = true, accepted = true, ok, value = fix.Value });
            _logger.LogInformation("MCDU check fix {Fix} ({Value}): {Result} — {Said}", fix.Kind, fix.Value, ok ? "entered" : "NOT entered", said);
            Speak(said);
        }

        if (needKeys > 0)
        {
            Speak(needKeys == 1
                ? "One item needs M C D U actuation, which is off — that one is yours."
                : $"{needKeys} items need M C D U actuation, which is off — those are yours.");
        }

        if (entered)
        {
            await HomeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Raises the offer and waits for the answer (voice, web card or command); the
    /// question's own time limit counts as no.</summary>
    private async Task<bool> AskAsync(BoxCheckFix fix, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAnswered(object? sender, (bool Accepted, string Source) e) => answer.TrySetResult(e.Accepted);

        _fixes.Answered += OnAnswered;
        try
        {
            if (!_fixes.Ask(fix.Prompt, fix.Kind.ToString()))
            {
                return false;
            }

            var limit = (BoxCheckFixStore.QuestionDefinition.DefaultTtl ?? TimeSpan.FromSeconds(60)) + TimeSpan.FromSeconds(20);
            var first = await Task.WhenAny(answer.Task, Task.Delay(limit, ct)).ConfigureAwait(false);
            if (first != answer.Task)
            {
                _fixes.Withdraw();
                ct.ThrowIfCancellationRequested();
                return false;
            }

            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            _fixes.Answered -= OnAnswered;
        }
    }

    private async Task<(bool Ok, string Said)> ApplyAsync(BoxCheckFix fix, CancellationToken ct)
    {
        try
        {
            switch (fix.Kind)
            {
                case BoxCheckFixKind.InitB:
                    var synced = await _initSync!.SyncAsync(ct).ConfigureAwait(false);
                    return synced is null
                        ? (false, "Unable — I have no weights to enter.")
                        : (true, $"Weights entered from the {synced.Source} loadsheet.");

                case BoxCheckFixKind.PerfSpeeds:
                    return await EnterPerfAsync(ct).ConfigureAwait(false);

                case BoxCheckFixKind.FlightNumber:
                    return await EnterOnInitAsync("FLT NBR", fix.Value, init => string.Equals(init.FlightNumber, fix.Value, StringComparison.Ordinal), "Flight number entered.", ct).ConfigureAwait(false);

                case BoxCheckFixKind.CostIndex:
                    return await EnterOnInitAsync("COST INDEX", fix.Value, init => init.CostIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) == fix.Value, "Cost index entered.", ct).ConfigureAwait(false);

                case BoxCheckFixKind.CruiseLevel:
                    return await EnterOnInitAsync("CRZ FL", fix.Value, init => init.CruiseFlightLevel?.ToString(System.Globalization.CultureInfo.InvariantCulture) == fix.Value, "Cruise level entered.", ct).ConfigureAwait(false);

                case BoxCheckFixKind.CopyActive:
                    return await CopyActiveAsync(ct).ConfigureAwait(false);

                default:
                    return (false, "Unable.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCDU check fix {Fix} failed", fix.Kind);
            return (false, "Unable — the entry failed, please check the M C D U.");
        }
    }

    /// <summary>V speeds, flex and flaps through the PERF datarefs (the write gate lists them;
    /// the Takeoff page's uplink writes the same names). The trim is not written: the computed
    /// snapshot carries none.</summary>
    private async Task<(bool, string)> EnterPerfAsync(CancellationToken ct)
    {
        if (_perf.Snapshot() is not { } computed)
        {
            return (false, "Unable — the takeoff figures are gone.");
        }

        try
        {
            await _dataRefs.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffFlaps.Name, computed.FlapsConf, ct).ConfigureAwait(false);
            // FLEX rides the dataref as a double; 0 is the FMS's TOGA sentinel (see FmsPerfUplinkService).
            await _dataRefs.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffFlexTemp.Name, (double)(computed.FlexTempC ?? 0), ct).ConfigureAwait(false);
            await _dataRefs.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffV1, computed.V1, ct).ConfigureAwait(false);
            await _dataRefs.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffVr, computed.Vr, ct).ConfigureAwait(false);
            await _dataRefs.WriteAsync(ProsimDataRefNames.FmsPerfTakeoffV2, computed.V2, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("MCDU check: takeoff figures not written: {Message}", ex.Message);
            return (false, "Unable — the takeoff figures were refused.");
        }

        return (true, "Takeoff figures entered. The trim is yours.");
    }

    /// <summary>One INIT A entry by keys: clear the scratchpad, type, check the scratchpad is
    /// exactly the entry, press the field's LSK, read the page back and check the field.</summary>
    private async Task<(bool, string)> EnterOnInitAsync(string label, string value, Func<InitAFacts, bool> entered, string done, CancellationToken ct)
    {
        var page = await OpenInitAsync(second: false, ct).ConfigureAwait(false);
        if (!BoxCheckPages.IsInitA(page) || page.Rows.FirstOrDefault(r => r.Label.Contains(label, StringComparison.Ordinal)) is not { } row)
        {
            return (false, "Unable — I can't find the field on the init page.");
        }

        // A message ("WIND DATA UPLINK") or a leftover entry sits in the scratchpad: CLEAR
        // removes a message at once and an entry character by character.
        for (var i = 0; i < 12 && page.Scratchpad.Length > 0; i++)
        {
            await _actuator.PressKeyAsync("CLEAR", ct).ConfigureAwait(false);
            page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        }

        if (page.Scratchpad.Length > 0 || !await _actuator.TypeAsync(value, ct).ConfigureAwait(false)
            || !string.Equals(_reader.Read().Scratchpad.Replace(" ", "", StringComparison.Ordinal), value, StringComparison.Ordinal))
        {
            return (false, "Unable — the scratchpad is not clear, please check the M C D U.");
        }

        await _actuator.PressLskAsync(row.Row, right: false, ct).ConfigureAwait(false);
        page = await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        return BoxCheckPages.InitA(page) is { } init && entered(init)
            ? (true, done)
            : (false, "Unable — the M C D U did not take the entry, please check the scratchpad.");
    }

    private async Task<(bool, string)> CopyActiveAsync(CancellationToken ct)
    {
        var index = await GoAsync("SEC_FPLN", ct, dwell: 0.3).ConfigureAwait(false);
        if (!BoxCheckPages.IsSecIndex(index) || BoxCheckPages.LinkRow(index, "COPY ACTIVE") is not { } row)
        {
            return (false, "Unable — I can't find copy active.");
        }

        await _actuator.PressLskAsync(row, right: false, ct).ConfigureAwait(false);
        await _actuator.WaitSettledAsync(ct).ConfigureAwait(false);
        return await ReadSecondaryAsync(ct).ConfigureAwait(false) == false
            ? (true, "Active plan copied to the secondary.")
            : (false, "Unable — the secondary is still empty.");
    }

    private void Record(string source, DateTimeOffset started, List<BoxCheckFinding> findings, List<string> notChecked, string? report, string outcome, string? reason = null)
        => _eventLog.Record(EventName, new
        {
            source,
            ok = outcome == "complete",
            outcome,
            reason,
            seconds = Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds, 1),
            items = _store.Snapshot().Items.Select(i => new { i.Id, state = i.State.ToString(), i.Lines }).ToList(),
            differences = findings.Where(f => f.State == BoxCheckItemState.Difference).Select(f => f.Text).ToList(),
            notEntered = findings.Where(f => f.State == BoxCheckItemState.NotEntered).Select(f => f.Text).ToList(),
            notChecked,
            text = report,
        });

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, TimeSpan.FromMinutes(1), Tag: "mcdu.boxCheck"));

    /// <summary>The check cannot go on: a page key was refused (the display went away).</summary>
    private sealed class BoxCheckAbortedException(string message) : Exception(message);
}

/// <summary>
/// The phrase table for the FO's "Shall I enter it?" (<see cref="BoxCheckFixStore"/>): the shared
/// captain's-question dialogue voices the offer and takes yes / no.
/// </summary>
public static class BoxCheckFixPhrases
{
    private static readonly IReadOnlyList<string> Grammar =
    [
        .. ConfirmVocabulary.Affirm, .. ConfirmVocabulary.Negative, .. ConfirmVocabulary.SayAgain,
        "enter it", "go ahead", "please do", "leave it", "i'll do it", "i will do it",
    ];

    public static Gsx.CaptainsQuestionPhrases Table { get; } = new(
        Topic: BoxCheckFixStore.QuestionDefinition.Topic,
        LogName: "MCDU check fix offer",
        MicOwner: "boxCheckFix",
        EventName: "mcdu.boxCheck.offer",
        Grammar: _ => Grammar,
        SpokenPrompt: question => question.Prompt,
        Interpret: (heard, _) => Interpret(heard) switch
        {
            true => Gsx.QuestionReply.Yes,
            false => Gsx.QuestionReply.No,
            null => null,
        },
        UnclearPrompt: _ => "Say again — shall I enter it, yes or no?",
        UnansweredPhrase: (_, _) => "No answer — I leave it.",
        RaisedPayload: question => new { fix = question.Subject, prompt = question.Prompt },
        AnsweredPayload: (question, reply, heard) => new { fix = question.Subject, outcome = reply.Kind == Gsx.QuestionReplyKind.Yes ? "accepted" : "declined", heard },
        UnansweredPayload: (question, _) => new { fix = question.Subject, outcome = "unanswered" },
        YesReadback: "Entering.",
        NoReadback: "Left as it is.");

    /// <summary>True = enter, false = leave, null = not an answer. Pure, for tests.</summary>
    public static bool? Interpret(string heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        var text = CommandMatcher.Normalize(heard);
        if (text.Contains("leave it", StringComparison.Ordinal) || text.Contains("i ll do it", StringComparison.Ordinal)
            || text.Contains("i will do it", StringComparison.Ordinal)
            || text.Contains("don t", StringComparison.Ordinal) || text.Contains("do not", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("enter it", StringComparison.Ordinal) || text.Contains("go ahead", StringComparison.Ordinal)
            || text.Contains("please do", StringComparison.Ordinal))
        {
            return true;
        }

        if (ConfirmVocabulary.Negative.Any(n => text == n || text.StartsWith(n + " ", StringComparison.Ordinal)))
        {
            return false;
        }

        if (ConfirmVocabulary.Affirm.Any(a => text == a || text.StartsWith(a + " ", StringComparison.Ordinal)))
        {
            return true;
        }

        return null;
    }
}
