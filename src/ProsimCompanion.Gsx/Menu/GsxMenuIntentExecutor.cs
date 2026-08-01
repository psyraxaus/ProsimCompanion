using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Gsx.Mirror;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>
/// Executes menu intents through the safe-fail pipeline (docs/integrations/gsx-remote-api.md §5):
/// readiness gate → parent navigation / menu.open (skipped when the target menu is already shown
/// — re-opening toggles it closed) → wait on <c>menuShown &amp;&amp; title</c> (never title alone:
/// the title stays stale for a beat after menu.open) → title check → resolve by text →
/// TOCTOU re-resolve → disabled guard → pick → verify against the mirror. Every failure mode
/// degrades to "menu left open for the user" — never a wrong click.
/// </summary>
public sealed class GsxMenuIntentExecutor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGsxRemoteApi _api;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ILogger<GsxMenuIntentExecutor> _logger;

    public GsxMenuIntentExecutor(
        IGsxRemoteApi api,
        IOptionsMonitor<GsxOptions> options,
        ILogger<GsxMenuIntentExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _options = options;
        _logger = logger;
    }

    public async Task<GsxIntentResult> ExecuteAsync(GsxMenuIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var result = await ExecuteCoreAsync(intent, cancellationToken).ConfigureAwait(false);
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

    private async Task<GsxIntentResult> ExecuteCoreAsync(GsxMenuIntent intent, CancellationToken cancellationToken)
    {
        if (_api.Readiness != GsxReadiness.Ready)
        {
            return new(GsxIntentOutcome.GsxNoResponse, $"Remote API not ready ({_api.Readiness})");
        }

        var mirror = _api.Mirror;

        // Reach the target menu.
        if (!(mirror.MenuShown && intent.TitleMatches(mirror.Menu?.Title)))
        {
            if (intent.ParentMenu is not null)
            {
                // The parent's pick opens this submenu.
                var parentResult = await ExecuteCoreAsync(intent.ParentMenu, cancellationToken).ConfigureAwait(false);
                if (!parentResult.Succeeded)
                {
                    return parentResult;
                }
            }
            else
            {
                var openResult = await _api.SendCommandAsync("menu.open", null, cancellationToken).ConfigureAwait(false);
                if (!openResult.Ok)
                {
                    return new(GsxIntentOutcome.GsxNoResponse, $"menu.open failed ({openResult.Code})");
                }
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

        // Navigation-only intents are done once the target menu is up.
        if (intent.EntryPattern is null)
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

    private static (bool Succeeded, int Index, GsxIntentResult? Result) Resolve(GsxMenuIntent intent, GsxMenuInfo menu)
    {
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
