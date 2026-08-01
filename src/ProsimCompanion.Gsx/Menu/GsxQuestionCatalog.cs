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

        dispatcher.Register("Do you want to board crew", ct => AnswerIfAsync(
            () => _options.CurrentValue.AnswerCrewQuestions,
            "board-crew question",
            "Do you want to board crew",
            "^yes",
            closeAfter: true,
            ct));

        dispatcher.Register("Do you want to deboard crew", ct => AnswerIfAsync(
            () => _options.CurrentValue.AnswerCrewQuestions,
            "deboard-crew question",
            "Do you want to deboard crew",
            "^yes",
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

        dispatcher.Register("Select de-icing type", HandleDeIceTypeAsync);
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
