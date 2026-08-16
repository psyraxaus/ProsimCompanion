using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Menu;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Repositions the aircraft via GSX once per gate session at startup: navigates the gate menu →
/// "Reposition Aircraft" → picks the first entry of the "Select Position at …" list
/// (positional — those lines carry no stable keyword). Runs only while stationary in
/// preparation with engines off, through the safe-fail intent pipeline, with a generous verify
/// budget (the reposition submenu is known to exceed 5 s).
/// </summary>
public sealed class GsxRepositionService : IDisposable
{
    private readonly IGsxRemoteApi _api;
    private readonly GsxMenuIntentExecutor _executor;
    private readonly IFlightPhaseSource _flightState;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxRepositionService> _logger;
    private string? _handledGateKey;
    private int _running;

    public GsxRepositionService(
        IGsxRemoteApi api,
        GsxMenuIntentExecutor executor,
        IFlightPhaseSource flightState,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxRepositionService> logger)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _api = api;
        _executor = executor;
        _flightState = flightState;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _api.Mirror.SidChanged += OnSidChanged;
    }

    public void Dispose() => _api.Mirror.SidChanged -= OnSidChanged;

    private void OnSidChanged(string? oldSid, string? newSid) => _handledGateKey = null;

    /// <summary>One coordinator-driven attempt. Returns Done when repositioned, disabled, or
    /// already handled for this gate; Waiting while preconditions (stationary, engines off)
    /// are not met. Driven by <see cref="GsxGroundPrepCoordinator"/> — step 1 of ground prep.</summary>
    public async Task<GsxPrepStatus> RunStepAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return GsxPrepStatus.Pending;
        }

        try
        {
            var options = _options.CurrentValue;
            var gateKey = _api.Mirror.GateContextKey;
            var snapshot = _flightState.Snapshot().Data;

            if (!options.AutoReposition || string.Equals(gateKey, _handledGateKey, StringComparison.Ordinal))
            {
                return GsxPrepStatus.Done;
            }

            if (gateKey is null
                || snapshot is null
                || snapshot.AnyEngineRunning
                || snapshot.GroundSpeedKt > 1)
            {
                return GsxPrepStatus.Waiting;
            }

            // Latch first: even a failed attempt must not loop the reposition menu forever —
            // the safe-fail leaves the menu for the user instead.
            _handledGateKey = gateKey;
            RecordDecision("reposition", $"repositioning at {gateKey}");

            var gateMenu = new GsxMenuIntent
            {
                Name = "gate menu (reposition)",
                TitlePrefixes = ["Activate Services at"],
                EntryPattern = new Regex("^reposition aircraft", RegexOptions.IgnoreCase),
                Verify = mirror => mirror.MenuShown
                    && mirror.Menu?.Title.StartsWith("Select Position", StringComparison.OrdinalIgnoreCase) == true,
                VerifyTimeout = TimeSpan.FromSeconds(20),
            };
            var positionPick = new GsxMenuIntent
            {
                Name = "reposition position pick",
                TitlePrefixes = ["Select Position at"],
                EntryIndex = 0,
                ParentMenu = gateMenu,
                VerifyTimeout = TimeSpan.FromSeconds(20),
            };

            var result = await _executor.ExecuteAsync(positionPick).ConfigureAwait(false);
            RecordDecision(
                "reposition",
                result.Succeeded ? $"completed: {result.Detail}" : $"{result.Outcome}: {result.Detail} — menu left for the user");
            return GsxPrepStatus.Done;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reposition step failed");
            return GsxPrepStatus.Done; // never block the rest of the prep chain
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
