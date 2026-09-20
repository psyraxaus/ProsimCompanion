using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// The question/answer catalogue (docs/integrations/gsx-remote-api.md §5): registers handlers on
/// the rising-edge dispatcher for the GSX-raised menus that have configured answers. Every
/// handler checks its option live (hot-reload friendly), answers through the safe-fail intent
/// executor, and records a decision — including "left for the user" — so a smoke test shows
/// exactly what was answered and why.
/// </summary>
public sealed class GsxQuestionCatalog
{
    private readonly IGsxRemoteApi _api;
    private readonly GsxMenuIntentExecutor _executor;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly IFlightPhaseSource _flightState;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxQuestionCatalog> _logger;
    private volatile bool _directionAutoSelected;
    private volatile bool _selectPositionSeenWhileMoving;

    public GsxQuestionCatalog(
        IGsxRemoteApi api,
        GsxMenuIntentExecutor executor,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        IFlightPhaseSource flightState,
        JsonlEventLog eventLog,
        ILogger<GsxQuestionCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _executor = executor;
        _options = options;
        _diagnostics = diagnostics;
        _flightState = flightState;
        _eventLog = eventLog;
        _logger = logger;

        // App-lifetime singleton — no unsubscribe needed. A Couatl engine restart starts a new
        // GSX session, so the once-per-session direction latch re-arms.
        _api.Mirror.SidChanged += (_, _) =>
        {
            _directionAutoSelected = false;
            _selectPositionSeenWhileMoving = false;
        };
        _flightState.PhaseChanged += OnPhaseChanged;
    }

    /// <summary>Registers all catalogued questions on the dispatcher.</summary>
    public void RegisterAll(GsxQuestionDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.Register("Request FollowMe", ct => AnswerIfAsync(
            () => _options.CurrentValue.SkipFollowMe,
            "FollowMe question",
            "Request FollowMe",
            "^no",
            closeAfter: true,
            ct));

        // GSX 4's crew menus offer Nobody/Crew/Pilots/Both — the configured entry is picked
        // exactly (pattern built at dispatch time so setting changes hot-apply).
        dispatcher.Register("Do you want to board crew", ct => AnswerIfAsync(
            () => _options.CurrentValue.AnswerCrewQuestions,
            "board-crew question",
            "Do you want to board crew",
            $"^{Regex.Escape(_options.CurrentValue.CrewBoardingAnswer)}$",
            closeAfter: true,
            ct));

        dispatcher.Register("Do you want to deboard crew", ct => AnswerIfAsync(
            () => _options.CurrentValue.AnswerCrewQuestions,
            "deboard-crew question",
            "Do you want to deboard crew",
            $"^{Regex.Escape(_options.CurrentValue.CrewBoardingAnswer)}$",
            closeAfter: true,
            ct));

        dispatcher.Register("Do you want to request", ct => AnswerIfAsync(
            () => _options.CurrentValue.ConfirmPushbackRequest,
            "pushback confirmation",
            "Do you want to request",
            "^yes",
            closeAfter: true,
            ct));

        dispatcher.Register("Ice warning", ct => AnswerIfAsync(
            () => _options.CurrentValue.AutoDeIce,
            "de-icing offer",
            "Ice warning",
            "^yes",
            closeAfter: false,
            ct));

        // Menu title from Prosim2GSX GsxConstants.MenuTugAttach — verified constant, not guessed.
        dispatcher.Register("Attach Pushback Tug", HandleTugQuestionAsync);

        dispatcher.Register("Select pushback direction", HandlePushbackDirectionAsync);
        dispatcher.Register("Select de-icing type", HandleDeIceTypeAsync);

        // Facility/parking-conflict surfaces (issue #44): GSX raises these when its remembered
        // parking disagrees with the aircraft's stand (or a parking change is attempted while
        // services run). No safe automated answer is known yet — the value is the decision-log
        // visibility, which names the gate GSX is anchored on instead of "no question handler".
        dispatcher.Register("Change parking or service", HandleParkingConflictAsync);

        // The unknown-parking spawn (issue #44, 2026-08-23 LGAV): GSX raises "Select
        // Position at <airport>" when it does not recognize the aircraft's stand. The menu
        // stays with the user (picking a stand is a crew decision) — registering it turns
        // the anonymous "no question handler" line into a named decision; the pilot-facing
        // conflict itself is published by the ground-prep coordinator's hold.
        dispatcher.Register("Select Position", ct =>
        {
            HandleSelectPosition();
            return Task.CompletedTask;
        });
        dispatcher.Register("This will revoke all active services", ct =>
        {
            RecordDecision(
                "revoke-services confirmation",
                "left for the user (never auto-answered — revoking active services is a crew decision)");
            return Task.CompletedTask;
        });
        dispatcher.Register("Select handling operator", ct => HandleOperatorMenuAsync("handling operator", ct));
        dispatcher.Register("Select catering operator", ct => HandleOperatorMenuAsync("catering operator", ct));
    }

    private async Task AnswerIfAsync(
        Func<bool> enabled,
        string questionName,
        string titlePrefix,
        string answerPattern,
        bool closeAfter,
        CancellationToken cancellationToken)
    {
        if (!enabled())
        {
            RecordDecision(questionName, "left for the user (disabled in settings)");
            return;
        }

        var result = await _executor.ExecuteAsync(
            new GsxMenuIntent
            {
                Name = questionName,
                TitlePrefixes = [titlePrefix],
                EntryPattern = new Regex(answerPattern, RegexOptions.IgnoreCase),
            },
            cancellationToken).ConfigureAwait(false);

        RecordDecision(questionName, result.Succeeded ? $"answered: {result.Detail}" : $"{result.Outcome}: {result.Detail}");

        if (result.Succeeded && closeAfter)
        {
            // GSX does not reliably dismiss question prompts after a pick — close explicitly.
            _ = await _api.SendCommandAsync("menu.close", null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>GSX's "Attach Pushback Tug" question raised during boarding. The predecessor
    /// answered POSITIONALLY — yes = entry 1, no = entry 2 (its DispatchTug; the entries are
    /// not literal yes/no text) — so the entry text is taken from the live menu at that index
    /// and re-found exactly (TOCTOU-safe, same pattern as the operator menus).</summary>
    private async Task HandleTugQuestionAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var answer = options.TugQuestionAnswer;
        if (!options.AutomationEnabled
            || string.Equals(answer, "ignore", StringComparison.OrdinalIgnoreCase))
        {
            RecordDecision("tug question", "left for the user (gsx.tugQuestionAnswer)");
            return;
        }

        var menu = _api.Mirror.Menu;
        if (menu is null || !_api.Mirror.MenuShown)
        {
            return;
        }

        var index = string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        if (index >= menu.Entries.Count)
        {
            RecordDecision(
                "tug question",
                $"left for the user (menu shows {menu.Entries.Count} entries — expected at least {index + 1})");
            return;
        }

        var entry = menu.Entries[index];
        var result = await _executor.ExecuteAsync(
            new GsxMenuIntent
            {
                Name = "tug question answer",
                TitlePrefixes = ["Attach Pushback Tug"],
                EntryPattern = new Regex($"^{Regex.Escape(entry)}$"),
            },
            cancellationToken).ConfigureAwait(false);

        RecordDecision(
            "tug question",
            result.Succeeded ? $"answered '{answer}' — picked '{entry}'" : $"{result.Outcome}: {result.Detail}");

        // Predecessor force-closed this prompt only when crew questions are skipped (the crew
        // question follows it in the same menu flow — closing early would eat that prompt).
        if (result.Succeeded && options.SkipCrewBoardingQuestion)
        {
            _ = await _api.SendCommandAsync("menu.close", null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Issue #44 diagnostics: the "Change parking or service" menu's first entry names
    /// the facility GSX is anchored on ("Change Facility [… Gate D5 …]") — logging it makes a
    /// stale-facility conflict visible the moment it happens, next to the gate session the
    /// mirror reports. When the session gate is UNKNOWN (EGLL Stand 547, 2026-08-22: GSX did
    /// not recognize the spawn position and the pilot restarted the app four times at a state
    /// no restart can fix) the conflict is also published to the diagnostics store, which
    /// drives the Flight Status row and the FO's spoken guidance. The menu itself stays with
    /// the user (its semantics are unverified) — the app advises, never clicks.</summary>
    private Task HandleParkingConflictAsync(CancellationToken cancellationToken)
    {
        var facilityEntry = _api.Mirror.MenuShown
            ? _api.Mirror.Menu?.Entries.FirstOrDefault(e => e.StartsWith("Change Facility", StringComparison.OrdinalIgnoreCase))
            : null;
        var gateKey = _api.Mirror.GateContextKey;
        RecordDecision(
            "parking-change menu",
            facilityEntry is null
                ? "left for the user"
                : $"left for the user — GSX is anchored on '{facilityEntry}' while the session gate is "
                    + $"'{gateKey ?? "unknown"}' (facility/stand conflict? see issue #44)");

        if (facilityEntry is not null && gateKey is null)
        {
            _diagnostics.UpdateParkingConflict(new GsxParkingConflictView(
                DateTimeOffset.UtcNow, ExtractFacility(facilityEntry)));
        }
        else if (gateKey is not null)
        {
            // The session gate is known again — whatever conflict stood is over.
            _diagnostics.UpdateParkingConflict(null);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The "Select Position at …" menu has three different meanings, and only one of them is
    /// a parking conflict (2026-09-21 review of the 09-13 and 09-20 flights):
    /// <list type="bullet">
    /// <item>Our own reposition step opened it (Reposition Aircraft → Select Position). GSX
    /// already knows the stand — the intent executor is driving this menu. Not a conflict;
    /// the FO spoke "GSX doesn't recognise our parking position" on every departure because
    /// this case was not distinguished.</item>
    /// <item>GSX asks for the arrival parking while the aircraft is still rolling (EGLL
    /// 2026-09-20: 31 s after touchdown). Normal GSX behaviour with no gate pre-selected —
    /// the pilot picks, or GSX identifies the stand on parking. Held: it becomes a conflict
    /// only if the menu is still up, with no parking identified, once parked with engines
    /// off (checked on the phase change, see <see cref="OnPhaseChanged"/>).</item>
    /// <item>The aircraft is parked, engines off, and GSX still does not know the stand —
    /// the genuine #44 case. Published (the Flight Status row + the FO's guidance).</item>
    /// </list>
    /// </summary>
    private void HandleSelectPosition()
    {
        var title = _api.Mirror.Menu?.Title ?? "Select Position at";
        if (_executor.IsDriving(title))
        {
            RecordDecision(
                "position-select menu",
                "opened by our own reposition step — GSX knows the stand; not a parking conflict");
            return;
        }

        var data = _flightState.Snapshot().Data;
        if (!ParkingConflictGate.IsParkedEnginesOff(data))
        {
            _selectPositionSeenWhileMoving = true;
            RecordDecision(
                "position-select menu",
                "left for the user — GSX is asking for the parking while the aircraft is moving or running; "
                + "a conflict only if it is still unanswered once parked");
            return;
        }

        PublishSelectPositionConflict();
    }

    private void PublishSelectPositionConflict()
    {
        RecordDecision(
            "position-select menu",
            "left for the user — GSX does not recognize the parking; pick the stand or reposition (issue #44)");

        // Publish the conflict from HERE too (issue #121, 2026-08-30 EGLL arrival): on
        // arrival there is no prep hold to publish it, so the menu appeared and the
        // pilot heard nothing. Once per standing conflict — the advisory speaks each
        // timestamp once and re-publishing would re-speak it.
        if (_diagnostics.Snapshot().ParkingConflict is null)
        {
            _diagnostics.UpdateParkingConflict(new GsxParkingConflictView(DateTimeOffset.UtcNow, ""));
        }
    }

    /// <summary>The held arrival case: the position menu appeared while rolling, the aircraft
    /// has now parked (Shutdown), and GSX still names no parking with the menu still up —
    /// that is the genuine conflict, published now rather than during the landing roll.</summary>
    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        try
        {
            if (!_selectPositionSeenWhileMoving || !e.Current.IsAtGate())
            {
                return;
            }

            _selectPositionSeenWhileMoving = false;
            var menuStillUp = _api.Mirror.MenuShown
                && _api.Mirror.Menu?.Title.StartsWith("Select Position", StringComparison.OrdinalIgnoreCase) == true;
            if (menuStillUp && _api.Mirror.GateContextKey is null)
            {
                PublishSelectPositionConflict();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Select Position phase follow-up failed");
        }
    }

    /// <summary>"Change Facility [Terminal 5B (531-548) Stand 547 with Safedock©]" → the
    /// bracketed facility; falls back to the whole entry when the brackets are absent.</summary>
    internal static string ExtractFacility(string changeFacilityEntry)
    {
        var open = changeFacilityEntry.IndexOf('[', StringComparison.Ordinal);
        var close = changeFacilityEntry.LastIndexOf(']');
        return open >= 0 && close > open + 1
            ? changeFacilityEntry[(open + 1)..close].Trim()
            : changeFacilityEntry.Trim();
    }

    private async Task HandlePushbackDirectionAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.AutomationEnabled)
        {
            RecordDecision("pushback direction menu", "left for the user (automation off)");
            return;
        }

        // GSX has been seen to re-open the direction menu mid-push (predecessor archaeology,
        // 2026-08: re-selecting then is never right) — apply the preference once per GSX
        // session; the SidChanged reset re-arms it for the next engine restart/turnaround.
        if (_directionAutoSelected)
        {
            RecordDecision("pushback direction menu", "left for the user (preference already applied this session)");
            return;
        }

        var menu = _api.Mirror.Menu;
        if (menu is null || !_api.Mirror.MenuShown)
        {
            return;
        }

        var pick = PushbackDirectionResolver.Resolve(menu.Entries, options.PushbackPreference);
        if (pick is null)
        {
            RecordDecision(
                "pushback direction menu",
                $"left for the user (no entry matches preference '{options.PushbackPreference}')");
            return;
        }

        // Re-match the resolved entry text exactly in the live menu (predecessor semantics): a
        // menu that changed between resolution and pick fails safe instead of picking blind.
        var result = await _executor.ExecuteAsync(
            new GsxMenuIntent
            {
                Name = "pushback direction selection",
                TitlePrefixes = ["Select pushback direction"],
                EntryPattern = new Regex($"^{Regex.Escape(pick.Entry)}$"),
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            _directionAutoSelected = true;
        }

        RecordDecision(
            "pushback direction menu",
            result.Succeeded
                ? $"picked '{pick.Entry}' ({pick.Strategy}, preference {options.PushbackPreference})"
                : $"{result.Outcome}: {result.Detail}");
    }

    private async Task HandleDeIceTypeAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.AutoDeIce)
        {
            RecordDecision("de-icing type menu", "left for the user (AutoDeIce off)");
            return;
        }

        // The entry must carry both the fluid-type token and the concentration token.
        var pattern = $"(?=.*{Regex.Escape(options.DeIceFluidType)})(?=.*{Regex.Escape(options.DeIceConcentration)})";
        var result = await _executor.ExecuteAsync(
            new GsxMenuIntent
            {
                Name = "de-icing type selection",
                TitlePrefixes = ["Select de-icing type"],
                EntryPattern = new Regex(pattern, RegexOptions.IgnoreCase),
            },
            cancellationToken).ConfigureAwait(false);

        RecordDecision(
            "de-icing type menu",
            result.Succeeded
                ? $"selected {options.DeIceFluidType} {options.DeIceConcentration}%: {result.Detail}"
                : $"{result.Outcome}: {result.Detail}");
    }

    private async Task HandleOperatorMenuAsync(string menuKind, CancellationToken cancellationToken)
    {
        var menu = _api.Mirror.Menu;
        if (menu is null || !_api.Mirror.MenuShown)
        {
            return;
        }

        var index = GsxOperatorMatcher.PickOperator(menu.Entries, _options.CurrentValue.OperatorPreferences);
        if (index is null)
        {
            RecordDecision($"{menuKind} menu", "left for the user (no preference matched)");
            return;
        }

        // Re-find by exact entry text (TOCTOU-safe: the intent resolves against the live menu).
        var result = await _executor.ExecuteAsync(
            new GsxMenuIntent
            {
                Name = $"{menuKind} selection",
                TitlePrefixes = [menu.Title],
                EntryPattern = new Regex($"^{Regex.Escape(menu.Entries[index.Value])}$"),
            },
            cancellationToken).ConfigureAwait(false);

        RecordDecision(
            $"{menuKind} menu",
            result.Succeeded ? $"picked '{menu.Entries[index.Value]}'" : $"{result.Outcome}: {result.Detail}");
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX question {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
        _eventLog.Record("gsx-question", new { action, reason });
    }
}
