using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// Executes menu intents through the safe-fail pipeline (docs/integrations/gsx-remote-api.md §5):
/// readiness gate → parent navigation / menu open through <see cref="GsxMenuOpener"/> (skipped
/// when the target menu is already shown — re-opening toggles it closed; a parent whose open
/// lands on the child's own page picks nothing) → wait on <c>menuShown &amp;&amp; title</c> (never title alone:
/// the title stays stale for a beat after menu.open) → title check → resolve by text →
/// TOCTOU re-resolve → disabled guard → pick → verify against the mirror. Every failure mode
/// degrades to "menu left open for the user" — never a wrong click.
/// </summary>
public sealed class GsxMenuIntentExecutor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a title read right after our own menu.open must stay the same before
    /// it is trusted (the title stays stale for a beat after menu.open).</summary>
    private static readonly TimeSpan OpenSettle = TimeSpan.FromMilliseconds(300);

    /// <summary>How long a finished intent still counts as "driving" its menus. The question
    /// dispatcher runs off the receive thread and can reach a handler a beat after the pick
    /// verified; without this grace the last mirror update of our own submenu would read as a
    /// GSX-raised question.</summary>
    internal static readonly TimeSpan RecentGrace = TimeSpan.FromSeconds(5);

    private readonly IGsxRemoteApi _api;
    private readonly GsxMenuOpener _opener;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ILogger<GsxMenuIntentExecutor> _logger;
    private readonly object _drivenGate = new();
    private readonly List<(GsxMenuIntent Intent, DateTimeOffset? FinishedAtUtc)> _driven = [];

    public GsxMenuIntentExecutor(
        IGsxRemoteApi api,
        GsxMenuOpener opener,
        IOptionsMonitor<GsxOptions> options,
        ILogger<GsxMenuIntentExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _opener = opener;
        _options = options;
        _logger = logger;
    }

    public async Task<GsxIntentResult> ExecuteAsync(GsxMenuIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        GsxIntentResult result;
        MarkDriving(intent);
        try
        {
            result = await ExecuteCoreAsync(intent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            MarkFinished(intent);
        }

        if (result.Succeeded)
        {
            _logger.LogInformation("Intent {Intent} succeeded: {Detail}", intent.Name, result.Detail);
        }
        else
        {
            // Safe-fail: the menu stays for the user; the log line is the diagnostic surface.
            _logger.LogWarning(
                "Intent {Intent} not executed ({Outcome}): {Detail} — menu left for the user",
                intent.Name,
                result.Outcome,
                result.Detail);
        }

        return result;
    }

    /// <summary>
    /// True when a menu with this title is one THIS executor opened: an intent in flight, any
    /// parent in its chain, or one that finished within <see cref="RecentGrace"/>. The question
    /// catalogue asks before treating a menu as a GSX-raised question. The reposition step
    /// opens GSX's own "Select Position at …" list, and on the 2026-09-13 and 2026-09-20
    /// flights the dispatcher took that submenu for the unknown-parking prompt — the FO then
    /// spoke "GSX doesn't recognise our parking position" at a stand GSX had already armed.
    /// </summary>
    public bool IsDriving(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        lock (_drivenGate)
        {
            _driven.RemoveAll(entry => entry.FinishedAtUtc is { } finished && now - finished > RecentGrace);
            foreach (var (intent, _) in _driven)
            {
                for (var current = intent; current is not null; current = current.ParentMenu)
                {
                    if (current.TitleMatches(title))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private void MarkDriving(GsxMenuIntent intent)
    {
        lock (_drivenGate)
        {
            _driven.Add((intent, null));
        }
    }

    private void MarkFinished(GsxMenuIntent intent)
    {
        lock (_drivenGate)
        {
            var index = _driven.FindIndex(entry => ReferenceEquals(entry.Intent, intent) && entry.FinishedAtUtc is null);
            if (index >= 0)
            {
                _driven[index] = (intent, DateTimeOffset.UtcNow);
            }
        }
    }

    /// <param name="openedFor">The child intent this one is the parent of, when it runs only to
    /// open that child's page.</param>
    private async Task<GsxIntentResult> ExecuteCoreAsync(
        GsxMenuIntent intent,
        CancellationToken cancellationToken,
        GsxMenuIntent? openedFor = null)
    {
        if (_api.Readiness != GsxReadiness.Ready)
        {
            return new(GsxIntentOutcome.GsxNoResponse, $"Remote API not ready ({_api.Readiness})");
        }

        var mirror = _api.Mirror;
        var opened = false;

        // Reach the target menu.
        if (!(mirror.MenuShown && intent.TitleMatches(mirror.Menu?.Title)))
        {
            if (intent.ParentMenu is not null)
            {
                // The parent's pick opens this submenu.
                var parentResult = await ExecuteCoreAsync(intent.ParentMenu, cancellationToken, openedFor: intent).ConfigureAwait(false);
                if (!parentResult.Succeeded)
                {
                    return parentResult;
                }
            }
            else
            {
                // "A menu is shown" is all the opener waits for — whether it is the right one
                // is the title wait's job below. The opener falls back to the legacy menu
                // LVAR when menu.open is acknowledged and nothing appears (issue #141).
                var openResult = await _opener.OpenAsync(
                    () => mirror.MenuShown,
                    TimeSpan.FromMilliseconds(_options.CurrentValue.MenuOpenTimeoutMs),
                    cancellationToken).ConfigureAwait(false);
                if (!openResult.Opened)
                {
                    return new(GsxIntentOutcome.GsxNoResponse, $"menu did not appear ({openResult.Detail})");
                }
                opened = true;
            }

            // Gate on menuShown AND title — the cached title is stale for a beat after open,
            // and a pick against a not-shown menu is silently dropped.
            var appeared = await WaitForAsync(
                () => mirror.MenuShown && intent.TitleMatches(mirror.Menu?.Title),
                TimeSpan.FromMilliseconds(_options.CurrentValue.MenuOpenTimeoutMs),
                cancellationToken).ConfigureAwait(false);
            if (!appeared)
            {
                return mirror.MenuShown
                    ? new(GsxIntentOutcome.MenuTitleMismatch, $"shown menu is '{mirror.Menu?.Title}', expected {string.Join("|", intent.TitlePrefixes)}")
                    : new(GsxIntentOutcome.GsxNoResponse, "menu did not appear");
            }
        }

        // GSX opened straight onto the child's page — this parent has nothing to pick (issue
        // #157, EFHK→LKPR 2026-10-04: in flight the GSX root menu IS the "Select airport"
        // page, so the parent's "^select airport" entry never existed and the airport pick
        // failed ItemNotAvailable three times with the right page on screen).
        if (openedFor is not null)
        {
            // The title lags menuShown by a beat after our own open (2026-10-10 EGCC, attempt
            // 1: title '' while the rows were already the airport list) — give it the open
            // timeout before deciding whose page this is.
            await WaitForAsync(
                () => !mirror.MenuShown || !string.IsNullOrEmpty(mirror.Menu?.Title),
                TimeSpan.FromMilliseconds(_options.CurrentValue.MenuOpenTimeoutMs),
                cancellationToken).ConfigureAwait(false);

            if (await IsOnChildPageAsync(openedFor, settle: opened, cancellationToken).ConfigureAwait(false))
            {
                return new(GsxIntentOutcome.Success, $"'{mirror.Menu?.Title}' is already the page for {openedFor.Name}");
            }
        }

        // Navigation-only intents are done once the target menu is up.
        if (intent.EntryPattern is null && intent.EntryIndex is null)
        {
            return new(GsxIntentOutcome.Success, "navigated");
        }

        var menu = mirror.Menu;
        if (menu is null)
        {
            return new(GsxIntentOutcome.GsxNoResponse, "menu vanished before resolve");
        }

        var resolved = Resolve(intent, menu);
        if (!resolved.Succeeded)
        {
            return resolved.Result!;
        }
        var index = resolved.Index;

        // TOCTOU: the menu may have changed between resolve and pick — re-resolve on any change.
        var latest = mirror.Menu;
        if (latest is null || !mirror.MenuShown)
        {
            return new(GsxIntentOutcome.GsxNoResponse, "menu closed before pick");
        }
        if (!MenusEqual(menu, latest))
        {
            if (!intent.TitleMatches(latest.Title))
            {
                return new(GsxIntentOutcome.MenuTitleMismatch, $"menu changed to '{latest.Title}' before pick");
            }

            resolved = Resolve(intent, latest);
            if (!resolved.Succeeded)
            {
                return resolved.Result!;
            }
            index = resolved.Index;
            menu = latest;
        }

        _logger.LogDebug(
            "Intent {Intent}: picking index {Index} ('{Entry}') on '{Title}'",
            intent.Name,
            index,
            menu.Entries[index],
            menu.Title);
        var pickResult = await _api.SendCommandAsync(
            "menu.pick",
            new JsonObject { ["index"] = index },
            cancellationToken).ConfigureAwait(false);
        if (!pickResult.Ok)
        {
            // "disabled" = greyed entry — unavailable by definition; never retried.
            return pickResult.Code.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                ? new(GsxIntentOutcome.ItemNotAvailable, "server refused pick: entry disabled")
                : new(GsxIntentOutcome.GsxNoResponse, $"menu.pick failed ({pickResult.Code})");
        }

        // Verify the observable effect against the same source the resolve used: the mirror.
        var verify = intent.Verify ?? (m => !m.MenuShown || !intent.TitleMatches(m.Menu?.Title));
        var verifyTimeout = intent.VerifyTimeout
            ?? TimeSpan.FromMilliseconds(_options.CurrentValue.IntentVerifyTimeoutMs);
        var verified = await WaitForAsync(() => verify(mirror), verifyTimeout, cancellationToken).ConfigureAwait(false);

        return verified
            ? new(GsxIntentOutcome.Success, $"picked '{menu.Entries[index]}'")
            : new(GsxIntentOutcome.GsxNoResponse, "pick sent but the expected effect was not observed");
    }

    /// <summary>True when the shown menu is the child's own page. After our own menu.open the
    /// cached title can be the previous menu's for a beat, so the answer must still hold
    /// after <see cref="OpenSettle"/> — a stale title must never send the child's pick to a
    /// different menu.</summary>
    private async Task<bool> IsOnChildPageAsync(GsxMenuIntent child, bool settle, CancellationToken cancellationToken)
    {
        var mirror = _api.Mirror;
        if (!(mirror.MenuShown && child.TitleMatches(mirror.Menu?.Title)))
        {
            return false;
        }

        if (settle)
        {
            await Task.Delay(OpenSettle, cancellationToken).ConfigureAwait(false);
        }

        return mirror.MenuShown && child.TitleMatches(mirror.Menu?.Title);
    }

    private static (bool Succeeded, int Index, GsxIntentResult? Result) Resolve(GsxMenuIntent intent, GsxMenuInfo menu)
    {
        // Positional intent: bounds + disabled checks; TOCTOU still re-validates before pick.
        if (intent.EntryPattern is null && intent.EntryIndex is { } fixedIndex)
        {
            if (fixedIndex < 0 || fixedIndex >= menu.Entries.Count)
            {
                return (false, -1, new(GsxIntentOutcome.ItemNotAvailable, $"index {fixedIndex} out of range on '{menu.Title}' ({menu.Entries.Count} entries)"));
            }

            if (fixedIndex < menu.Disabled.Count && menu.Disabled[fixedIndex])
            {
                return (false, -1, new(GsxIntentOutcome.ItemNotAvailable, $"entry '{menu.Entries[fixedIndex]}' is disabled"));
            }

            return (true, fixedIndex, null);
        }

        var matches = new List<int>();
        for (var i = 0; i < menu.Entries.Count; i++)
        {
            if (intent.EntryPattern!.IsMatch(menu.Entries[i]))
            {
                matches.Add(i);
            }
        }

        if (matches.Count == 0)
        {
            return (false, -1, new(GsxIntentOutcome.ItemNotAvailable, $"no entry matches {intent.EntryPattern} on '{menu.Title}'"));
        }

        if (matches.Count > 1)
        {
            return (false, -1, new(GsxIntentOutcome.AmbiguousMatch, $"{matches.Count} entries match {intent.EntryPattern} on '{menu.Title}'"));
        }

        var index = matches[0];
        if (index < menu.Disabled.Count && menu.Disabled[index])
        {
            return (false, -1, new(GsxIntentOutcome.ItemNotAvailable, $"entry '{menu.Entries[index]}' is disabled"));
        }

        return (true, index, null);
    }

    private static bool MenusEqual(GsxMenuInfo a, GsxMenuInfo b)
        => string.Equals(a.Title, b.Title, StringComparison.Ordinal)
            && a.Entries.SequenceEqual(b.Entries, StringComparer.Ordinal);

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
