using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// Implements the Core <see cref="IGsxMenuControl"/> seam (issue #135): manual picks from the
/// web menu card go through the SAME <see cref="GsxMenuIntentExecutor"/> pipeline the
/// automation uses — readiness gate, title check, bounds/disabled guards, TOCTOU re-validation,
/// verify against the mirror — so a pilot's click can never land on a line that moved.
/// <para>
/// The pick is positional by necessity (addon-airport direction lines carry no keyword, and
/// two lines can read alike), but it is guarded by the entry text the pilot saw: if the line
/// at that index no longer reads the same when the pick is sent, nothing goes out and the
/// card says "menu changed — pick again". One pick in flight at a time; the automation's
/// own intents keep priority (<see cref="AutomationDriving"/>).
/// </para>
/// </summary>
public sealed class GsxMenuControl : IGsxMenuControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGsxRemoteApi _api;
    private readonly GsxMenuIntentExecutor _executor;
    private readonly GsxMenuOpener _opener;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ILogger<GsxMenuControl> _logger;
    private readonly SemaphoreSlim _inFlight = new(1, 1);
    private readonly object _gate = new();
    private bool _ownIntentInFlight;
    private DateTimeOffset? _ownIntentFinishedUtc;

    public GsxMenuControl(
        IGsxRemoteApi api,
        GsxMenuIntentExecutor executor,
        GsxMenuOpener opener,
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        IOptionsMonitor<GsxOptions> options,
        ILogger<GsxMenuControl> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _executor = executor;
        _opener = opener;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>The executor counts our own pick as "driving" for its grace window, so the
    /// card would lock itself out for five seconds after every click — our own intents are
    /// excluded by the in-flight / recently-finished latch.</remarks>
    public bool AutomationDriving
    {
        get
        {
            var mirror = _api.Mirror;
            var title = mirror.MenuShown ? mirror.Menu?.Title : null;
            if (string.IsNullOrEmpty(title))
            {
                return false;
            }

            lock (_gate)
            {
                if (_ownIntentInFlight
                    || (_ownIntentFinishedUtc is { } finished
                        && DateTimeOffset.UtcNow - finished <= GsxMenuIntentExecutor.RecentGrace))
                {
                    return false;
                }
            }

            return _executor.IsDriving(title);
        }
    }

    public async Task<GsxMenuActionOutcome> PickAsync(int index, string expectedEntry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedEntry);

        if (_api.Readiness != GsxReadiness.Ready)
        {
            return new(GsxMenuActionStatus.Unavailable, $"GSX Remote API not ready ({_api.Readiness})");
        }

        var mirror = _api.Mirror;
        var menu = mirror.Menu;
        if (!mirror.MenuShown || menu is null)
        {
            return new(GsxMenuActionStatus.NotAvailable, "no GSX menu is open");
        }

        if (index < 0 || index >= menu.Entries.Count
            || !string.Equals(menu.Entries[index], expectedEntry, StringComparison.Ordinal))
        {
            return new(GsxMenuActionStatus.NotAvailable, "the menu changed — pick again from the current lines");
        }

        if (index < menu.Disabled.Count && menu.Disabled[index])
        {
            return new(GsxMenuActionStatus.NotAvailable, $"'{expectedEntry}' is greyed out in GSX");
        }

        if (AutomationDriving)
        {
            return new(GsxMenuActionStatus.NotAvailable, "the automation is answering this menu — wait for it");
        }

        if (!await _inFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new(GsxMenuActionStatus.NotAvailable, "a pick is already in flight");
        }

        try
        {
            MarkOwnIntent(inFlight: true);
            var before = menu;
            var intent = new GsxMenuIntent
            {
                Name = "web menu pick",
                TitlePrefixes = [menu.Title],
                EntryIndex = index,
                // Any change is the effect: the menu closes (a direction line), a submenu
                // opens (Customize, operator lists) or the same title re-renders with other
                // lines (toggles). Same title + same lines = GSX ignored the click.
                Verify = m => !m.MenuShown || m.Menu is null || !SameMenu(before, m.Menu),
            };

            var result = await _executor.ExecuteAsync(intent, cancellationToken).ConfigureAwait(false);
            var outcome = Map(result, expectedEntry);
            Record(
                "web menu pick",
                outcome.Status == GsxMenuActionStatus.Done
                    ? $"picked '{expectedEntry}' on '{menu.Title}' ({outcome.Detail})"
                    : $"{result.Outcome} picking '{expectedEntry}' on '{menu.Title}': {result.Detail}",
                new { title = menu.Title, index, entry = expectedEntry, outcome = result.Outcome.ToString(), detail = result.Detail });
            return outcome;
        }
        finally
        {
            MarkOwnIntent(inFlight: false);
            _inFlight.Release();
        }
    }

    public async Task<GsxMenuActionOutcome> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_api.Readiness != GsxReadiness.Ready)
        {
            return new(GsxMenuActionStatus.Unavailable, $"GSX Remote API not ready ({_api.Readiness})");
        }

        var mirror = _api.Mirror;
        if (mirror.MenuShown)
        {
            // Never menu.toggle / re-open a shown menu — that closes it and races the
            // re-raise (docs/integrations/gsx-remote-api.md §4).
            return new(GsxMenuActionStatus.Done, "the GSX menu is already open");
        }

        // Same two rungs as the automation (issue #141): the card was as blind as the
        // intents in a session where the in-sim GSX panel had not been opened yet.
        var open = await _opener.OpenAsync(
            () => mirror.MenuShown,
            TimeSpan.FromMilliseconds(_options.CurrentValue.MenuOpenTimeoutMs),
            cancellationToken).ConfigureAwait(false);
        Record("web menu open", open.Opened ? $"opened '{mirror.Menu?.Title}' ({open.Detail})" : open.Detail,
            new { outcome = open.Opened ? "ok" : "no-menu", via = open.Outcome.ToString() });
        return open.Opened
            ? new(GsxMenuActionStatus.Done, $"opened '{mirror.Menu?.Title}'")
            : new(GsxMenuActionStatus.Failed, "GSX showed no menu");
    }

    public async Task<GsxMenuActionOutcome> CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_api.Readiness != GsxReadiness.Ready)
        {
            return new(GsxMenuActionStatus.Unavailable, $"GSX Remote API not ready ({_api.Readiness})");
        }

        var mirror = _api.Mirror;
        if (!mirror.MenuShown)
        {
            return new(GsxMenuActionStatus.Done, "no GSX menu is open");
        }

        var title = mirror.Menu?.Title;
        var close = await _api.SendCommandAsync("menu.close", null, cancellationToken).ConfigureAwait(false);
        Record("web menu close", close.Ok ? $"closed '{title}'" : $"menu.close failed ({close.Code})",
            new { title, outcome = close.Ok ? "ok" : close.Code });
        return close.Ok
            ? new(GsxMenuActionStatus.Done, $"closed '{title}'")
            : new(GsxMenuActionStatus.Failed, $"menu.close failed ({close.Code})");
    }

    private static GsxMenuActionOutcome Map(GsxIntentResult result, string entry) => result.Outcome switch
    {
        GsxIntentOutcome.Success => new(GsxMenuActionStatus.Done, result.Detail),
        // menu.pick was accepted but the mirror never changed — the click reached GSX; the
        // line simply had no visible effect (a toggle GSX re-renders identically).
        GsxIntentOutcome.GsxNoResponse when result.Detail.Contains("expected effect", StringComparison.Ordinal)
            => new(GsxMenuActionStatus.Done, $"'{entry}' sent — GSX showed no change"),
        GsxIntentOutcome.GsxNoResponse => new(GsxMenuActionStatus.Failed, result.Detail),
        _ => new(GsxMenuActionStatus.NotAvailable, result.Detail),
    };

    private static bool SameMenu(GsxMenuInfo a, GsxMenuInfo b)
        => string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && a.Entries.SequenceEqual(b.Entries, StringComparer.Ordinal);

    private void MarkOwnIntent(bool inFlight)
    {
        lock (_gate)
        {
            _ownIntentInFlight = inFlight;
            if (!inFlight)
            {
                _ownIntentFinishedUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private void Record(string action, string reason, object payload)
    {
        _logger.LogInformation("GSX menu card {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
        _eventLog.Record("gsx-menu-card", new { action, reason, payload });
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return condition();
    }
}
