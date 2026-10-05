using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Gsx.Menu;

/// <summary>How a <see cref="GsxMenuOpener"/> request ended.</summary>
public enum GsxMenuOpenOutcome
{
    /// <summary>The Remote API's <c>menu.open</c> produced the awaited effect.</summary>
    OpenedByApi,

    /// <summary>The API request showed nothing; the legacy menu LVAR produced the effect.</summary>
    OpenedByLvar,

    /// <summary>Neither path produced the awaited effect inside its wait.</summary>
    NotOpened,
}

/// <summary>Result of one open request. <see cref="Detail"/> is log/diagnostics text.</summary>
public sealed record GsxMenuOpenResult(GsxMenuOpenOutcome Outcome, string Detail)
{
    public bool Opened => Outcome != GsxMenuOpenOutcome.NotOpened;
}

/// <summary>
/// Asks GSX to show its menu, by the Remote API first and the legacy menu LVAR second.
/// <para>Issue #141 (2026-09-27 EKCH→EGLL, three in-flight airport picks): <c>menu.open</c>
/// answered ok and no menu ever appeared — <c>menuShown</c> stayed false for the whole wait —
/// in a session where the in-sim GSX panel had not been opened yet. The LVAR
/// <c>L:FSDT_GSX_MENU_OPEN</c> is the request the toolbar itself makes and was the
/// predecessor's only menu path for years, from fresh sessions included, so it is the second
/// rung. Never <c>menu.toggle</c>: both rungs are plain open requests.</para>
/// </summary>
public sealed class GsxMenuOpener
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGsxRemoteApi _api;
    private readonly ISimVars _simVars;
    private readonly ILogger<GsxMenuOpener> _logger;

    public GsxMenuOpener(IGsxRemoteApi api, ISimVars simVars, ILogger<GsxMenuOpener> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _simVars = simVars;
        _logger = logger;
    }

    /// <summary>
    /// Requests the menu and waits for <paramref name="effect"/> — "a menu is shown" for a
    /// menu driver, "the parking is named" for the parking wake. Each rung gets its own
    /// <paramref name="rungWait"/>; the LVAR rung runs only when the API rung produced nothing.
    /// Callers must not call this while a menu is already shown.
    /// </summary>
    /// <param name="effect">The observable outcome the caller needs, read against the mirror.</param>
    /// <param name="rungWait">How long each rung may take to produce the effect.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    public async Task<GsxMenuOpenResult> OpenAsync(
        Func<bool> effect,
        TimeSpan rungWait,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effect);

        var open = await _api.SendCommandAsync("menu.open", null, cancellationToken).ConfigureAwait(false);
        if (open.Ok && await WaitForAsync(effect, rungWait, cancellationToken).ConfigureAwait(false))
        {
            return new(GsxMenuOpenOutcome.OpenedByApi, "menu.open");
        }

        var apiDetail = open.Ok ? "menu.open acknowledged but nothing appeared" : $"menu.open failed ({open.Code})";
        try
        {
            await _simVars.WriteAsync(GsxLvarNames.MenuOpen, 1, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // MSFS not connected — there is no second rung to try.
            _logger.LogDebug(ex, "GSX menu LVAR could not be written");
            return new(GsxMenuOpenOutcome.NotOpened, $"{apiDetail}; menu LVAR not writable (MSFS not connected)");
        }

        _logger.LogInformation("GSX menu: {ApiDetail} — requested through the menu LVAR", apiDetail);
        return await WaitForAsync(effect, rungWait, cancellationToken).ConfigureAwait(false)
            ? new(GsxMenuOpenOutcome.OpenedByLvar, $"{apiDetail}; opened through the menu LVAR")
            : new(GsxMenuOpenOutcome.NotOpened, $"{apiDetail}; the menu LVAR showed nothing either");
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
