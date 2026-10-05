using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Menu;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>Where the parking wake stands for the current unknown-parking hold.</summary>
public enum GsxParkingWakeStatus
{
    /// <summary>Nothing conclusive yet — the first attempt has not run.</summary>
    Waiting,

    /// <summary>GSX named the parking; the hold releases on the next prep cycle.</summary>
    Named,

    /// <summary>The wake cannot help: a GSX menu is up and GSX still names no parking (the
    /// genuine issue #44 conflict — the pilot picks the stand), or the aircraft is not
    /// parked with engines off.</summary>
    Unresolved,

    /// <summary>No rung made GSX show its menu — only the pilot's toolbar click can.</summary>
    MenuUnreachable,
}

/// <summary>What one wake step should do. Pure — see <see cref="ParkingWakePlan"/>.</summary>
internal enum ParkingWakeAction
{
    /// <summary>Too early in the hold, or between two attempts.</summary>
    Wait,

    /// <summary>Ask GSX for its menu and watch for the parking.</summary>
    OpenMenu,

    /// <summary>A menu is already up or the aircraft is not parked — nothing to send.</summary>
    Stand,

    /// <summary>Every attempt is spent.</summary>
    GiveUp,
}

/// <summary>The wake's decision function, pure for table tests.</summary>
internal static class ParkingWakePlan
{
    /// <summary>GSX gets one prep cycle to name the parking by itself before the menu is
    /// requested — at session start it reports Ready a few seconds before it has the airport.</summary>
    internal static readonly TimeSpan FirstAttemptDelay = TimeSpan.FromSeconds(3);

    /// <summary>Spacing of the re-tries after an attempt that showed no menu.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    internal const int MaxAttempts = 3;

    internal static ParkingWakeAction Next(
        TimeSpan holdAge,
        bool parkedEnginesOff,
        bool menuShown,
        int attempts,
        TimeSpan? sinceLastAttempt)
    {
        // A menu on screen is the pilot's or GSX's own question — never opened over, never
        // closed. Opening the menu under a moving or running aircraft is not ours to do.
        if (menuShown || !parkedEnginesOff)
        {
            return ParkingWakeAction.Stand;
        }

        if (attempts >= MaxAttempts)
        {
            return ParkingWakeAction.GiveUp;
        }

        if (attempts == 0)
        {
            return holdAge >= FirstAttemptDelay ? ParkingWakeAction.OpenMenu : ParkingWakeAction.Wait;
        }

        return sinceLastAttempt is { } since && since >= RetryInterval
            ? ParkingWakeAction.OpenMenu
            : ParkingWakeAction.Wait;
    }
}

/// <summary>
/// Makes GSX look for the stand the aircraft is parked on, by opening its menu (issue #141).
/// <para>GSX does not evaluate the parking until its menu is requested. 2026-10-05 LKPR,
/// 0.6.0-rc.17, wire trace: after the session loaded GSX reported state 3 "Our airplane is
/// taxing on ground" at 01:02:40Z and then sent nothing for 2 min 10 s — no <c>/parking</c>,
/// no gate. The pilot said "start ground services" at 01:04:10Z, the prep chain held on the
/// unknown parking, and the FO spoke "GSX doesn't recognise our parking position" at
/// 01:04:38Z. The pilot clicked the GSX toolbar menu at 01:04:49Z; 0.7 s later GSX patched
/// state 5 "Our airplane is Parked" and <c>/parking</c> "Gate C 28", and the whole chain ran.
/// Same edge on the arrival the flight before (menu shown 21:44:05.98Z, "Gate C 29"
/// 21:44:06.37Z). The prep chain waited for a parking name before any step that opens the
/// menu, and GSX waited for the menu before naming the parking.</para>
/// <para>So the unknown-parking hold now asks for the menu itself — through
/// <see cref="GsxMenuOpener"/> (Remote API, then the legacy LVAR) — watches for the parking,
/// and closes the root services menu it opened. A menu that comes up while GSX still names
/// no parking is GSX's own parking question: it stays for the pilot and the conflict
/// guidance applies unchanged. The app never picks a stand here.</para>
/// </summary>
public sealed class GsxParkingWakeService
{
    /// <summary>How long each rung of the opener may take to name the parking or show a menu.
    /// The 2026-10-05 toolbar click named the parking after 0.7 s and showed the menu after 3 s.</summary>
    internal TimeSpan RungWait { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How long a shown menu may take to be followed by the parking name (0.4 s on
    /// the 2026-10-04 arrival), and a named parking by its menu, before the step concludes.</summary>
    internal TimeSpan SettleWait { get; init; } = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGsxRemoteApi _api;
    private readonly GsxMenuOpener _opener;
    private readonly IFlightPhaseSource _flightState;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxParkingWakeService> _logger;
    private readonly object _gate = new();
    private int _attempts;
    private DateTimeOffset? _lastAttemptAt;
    private GsxParkingWakeStatus _status = GsxParkingWakeStatus.Waiting;

    public GsxParkingWakeService(
        IGsxRemoteApi api,
        GsxMenuOpener opener,
        IFlightPhaseSource flightState,
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        ILogger<GsxParkingWakeService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _opener = opener;
        _flightState = flightState;
        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Forgets the attempts of a finished hold — the next unknown-parking hold starts
    /// from the first rung.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _attempts = 0;
            _lastAttemptAt = null;
            _status = GsxParkingWakeStatus.Waiting;
        }
    }

    /// <summary>One coordinator-driven step while the unknown-parking hold stands. May take
    /// up to two rung waits; never throws, never picks a menu entry.</summary>
    /// <param name="holdSince">When the unknown-parking hold began.</param>
    /// <param name="now">The prep cycle's clock.</param>
    /// <param name="cancellationToken">Cancels the waits.</param>
    public async Task<GsxParkingWakeStatus> RunStepAsync(
        DateTimeOffset holdSince,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var mirror = _api.Mirror;
        if (mirror.GateContextKey is not null)
        {
            return SetStatus(GsxParkingWakeStatus.Named);
        }

        int attempts;
        DateTimeOffset? lastAttemptAt;
        lock (_gate)
        {
            attempts = _attempts;
            lastAttemptAt = _lastAttemptAt;
        }

        var action = ParkingWakePlan.Next(
            now - holdSince,
            ParkingConflictGate.IsParkedEnginesOff(_flightState.Snapshot().Data),
            mirror.MenuShown,
            attempts,
            lastAttemptAt is { } last ? now - last : null);

        switch (action)
        {
            case ParkingWakeAction.Stand:
                return SetStatus(GsxParkingWakeStatus.Unresolved);

            case ParkingWakeAction.GiveUp:
                return SetStatus(GsxParkingWakeStatus.MenuUnreachable);

            case ParkingWakeAction.Wait:
                lock (_gate)
                {
                    return _status;
                }
        }

        lock (_gate)
        {
            _attempts = ++attempts;
            _lastAttemptAt = now;
        }

        try
        {
            return SetStatus(await OpenAndWatchAsync(attempts, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parking wake attempt failed");
            return SetStatus(GsxParkingWakeStatus.MenuUnreachable);
        }
    }

    private async Task<GsxParkingWakeStatus> OpenAndWatchAsync(int attempt, CancellationToken cancellationToken)
    {
        var mirror = _api.Mirror;
        Record(attempt, "opening", "GSX names no parking — opening its menu so it looks for the stand");

        var open = await _opener.OpenAsync(
            () => mirror.GateContextKey is not null || mirror.MenuShown,
            RungWait,
            cancellationToken).ConfigureAwait(false);
        if (!open.Opened)
        {
            Record(attempt, "menu-unreachable", $"no menu and no parking ({open.Detail})");
            return GsxParkingWakeStatus.MenuUnreachable;
        }

        // The two edges arrive in either order: menu then parking, or parking then menu.
        if (!await WaitForAsync(() => mirror.GateContextKey is not null, SettleWait, cancellationToken).ConfigureAwait(false))
        {
            Record(
                attempt,
                "menu-without-parking",
                $"GSX shows '{mirror.Menu?.Title}' and names no parking — left for the pilot ({open.Detail})");
            return GsxParkingWakeStatus.Unresolved;
        }

        var gate = mirror.GateContextKey;
        await WaitForAsync(() => mirror.MenuShown, SettleWait, cancellationToken).ConfigureAwait(false);
        var closed = await CloseRootMenuAsync(cancellationToken).ConfigureAwait(false);
        Record(attempt, "named", $"GSX named the parking '{gate}' ({open.Detail}{(closed ? "; menu closed" : "")})");
        return GsxParkingWakeStatus.Named;
    }

    /// <summary>Closes the menu this step opened, and only the root services menu: anything
    /// else GSX raised on the way is a question for the dispatcher or the pilot.</summary>
    private async Task<bool> CloseRootMenuAsync(CancellationToken cancellationToken)
    {
        var mirror = _api.Mirror;
        if (!mirror.MenuShown
            || mirror.Menu?.Title.StartsWith("Activate Services", StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        var close = await _api.SendCommandAsync("menu.close", null, cancellationToken).ConfigureAwait(false);
        return close.Ok;
    }

    private GsxParkingWakeStatus SetStatus(GsxParkingWakeStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        return status;
    }

    private void Record(int attempt, string outcome, string detail)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", "parking wake", detail);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, "parking wake", detail));
        _eventLog.Record("gsx-parking-wake", new { attempt, outcome, detail });
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
