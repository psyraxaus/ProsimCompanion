using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
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
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxQuestionCatalog> _logger;
    private volatile bool _directionAutoSelected;

    public GsxQuestionCatalog(
        IGsxRemoteApi api,
        GsxMenuIntentExecutor executor,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        ILogger<GsxQuestionCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _executor = executor;
        _options = options;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;

        // App-lifetime singleton — no unsubscribe needed. A Couatl engine restart starts a new
        // GSX session, so the once-per-session direction latch re-arms.
        _api.Mirror.SidChanged += (_, _) => _directionAutoSelected = false;
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
    /// mirror reports. The menu itself stays with the user (its semantics are unverified).</summary>
    private Task HandleParkingConflictAsync(CancellationToken cancellationToken)
    {
        var facilityEntry = _api.Mirror.MenuShown
            ? _api.Mirror.Menu?.Entries.FirstOrDefault(e => e.StartsWith("Change Facility", StringComparison.OrdinalIgnoreCase))
            : null;
        RecordDecision(
            "parking-change menu",
            facilityEntry is null
                ? "left for the user"
                : $"left for the user — GSX is anchored on '{facilityEntry}' while the session gate is "
                    + $"'{_api.Mirror.GateContextKey ?? "unknown"}' (facility/stand conflict? see issue #44)");
        return Task.CompletedTask;
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
