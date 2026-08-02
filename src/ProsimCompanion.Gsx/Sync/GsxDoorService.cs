using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Services;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Drives the ProSim aircraft doors from GSX activity, the predecessor's proven model with the
/// ProSim door state authoritative throughout:
///
/// - GSX's door-request toggles (<c>AIRCRAFT_CARGO_n_TOGGLE</c> rising = open that cargo door,
///   <c>AIRCRAFT_SERVICE_n_TOGGLE</c> rising = toggle the right-side catering door);
/// - cargo doors open shortly after boarding/deboarding starts and close a crew-realistic
///   delay after each GSX loader finishes (<c>BOARDING_CARGO_EXIT_n</c> dropping to 0);
/// - entry doors open when GSX stairs dock (L1 only at jetway-less stands — with a jetway,
///   ProSim's own autoDoor handles L1) and close when the stairs leave;
/// - <c>DISABLE_DOORS_MSG</c> suppresses GSX's "waiting for your action" prompts while we
///   drive the doors — re-asserted periodically because it resets on every Couatl restart.
///
/// Door writes go through the gateway (EFB domain); every action is decision-logged.
/// </summary>
public sealed class GsxDoorService : IDisposable
{
    private static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(10);
    private const int StairsActiveState = 5;
    private const int JetwayLvarNotPresent = 2;

    private readonly GsxProsimWriter _writer;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly ILogger<GsxDoorService> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly IDataRefSubscription _door1L;
    private readonly IDataRefSubscription _door4L;
    private readonly IDataRefSubscription _door1R;
    private readonly IDataRefSubscription _door4R;
    private readonly IDataRefSubscription _cargoFwd;
    private readonly IDataRefSubscription _cargoAft;
    private readonly IDataRefSubscription _cargoBulk;

    private readonly ISimVars _simVars;
    private readonly IDataRefSubscription _disableDoorsMsg;
    private readonly IDataRefSubscription _toggleCargo1;
    private readonly IDataRefSubscription _toggleCargo2;
    private readonly IDataRefSubscription _toggleService1;
    private readonly IDataRefSubscription _toggleService2;
    private readonly IDataRefSubscription _cargoExit0;
    private readonly IDataRefSubscription _cargoExit1;
    private readonly IDataRefSubscription _stairsState;
    private readonly IDataRefSubscription _jetwayLvar;

    private readonly Timer _reassertTimer;
    private double _lastToggleCargo1;
    private double _lastToggleCargo2;
    private double _lastToggleService1;
    private double _lastToggleService2;
    private double _lastCargoExit0;
    private double _lastCargoExit1;
    private bool _stairsWereActive;
    private bool _stairsOpenedL1;
    private volatile bool _loadingActive; // boarding OR deboarding running

    public GsxDoorService(
        IProsimDataRefs prosim,
        ISimVars simVars,
        GsxProsimWriter writer,
        GsxServiceLifecycleTracker lifecycle,
        IOptionsMonitor<GsxOptions> options,
        GsxDiagnosticsStore diagnostics,
        ILogger<GsxDoorService> logger)
    {
        ArgumentNullException.ThrowIfNull(prosim);
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(logger);

        _simVars = simVars;
        _writer = writer;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;

        _door1L = prosim.Subscribe(ProsimDataRefNames.Door1L, DataRefTier.Normal);
        _door4L = prosim.Subscribe(ProsimDataRefNames.Door4L, DataRefTier.Normal);
        _door1R = prosim.Subscribe(ProsimDataRefNames.Door1R, DataRefTier.Normal);
        _door4R = prosim.Subscribe(ProsimDataRefNames.Door4R, DataRefTier.Normal);
        _cargoFwd = prosim.Subscribe(ProsimDataRefNames.DoorCargoForward, DataRefTier.Normal);
        _cargoAft = prosim.Subscribe(ProsimDataRefNames.DoorCargoAft, DataRefTier.Normal);
        _cargoBulk = prosim.Subscribe(ProsimDataRefNames.DoorCargoBulk, DataRefTier.Normal);

        _disableDoorsMsg = simVars.Subscribe(GsxLvarNames.DisableDoorsMsg, "number", DataRefTier.Infrequent);
        _toggleCargo1 = simVars.Subscribe(GsxLvarNames.DoorToggleCargo1, "number", DataRefTier.Normal);
        _toggleCargo2 = simVars.Subscribe(GsxLvarNames.DoorToggleCargo2, "number", DataRefTier.Normal);
        _toggleService1 = simVars.Subscribe(GsxLvarNames.DoorToggleService1, "number", DataRefTier.Normal);
        _toggleService2 = simVars.Subscribe(GsxLvarNames.DoorToggleService2, "number", DataRefTier.Normal);
        _cargoExit0 = simVars.Subscribe(GsxLvarNames.BoardingCargoExit0, "number", DataRefTier.Normal);
        _cargoExit1 = simVars.Subscribe(GsxLvarNames.BoardingCargoExit1, "number", DataRefTier.Normal);
        _stairsState = simVars.Subscribe(GsxLvarNames.Stairs, "number", DataRefTier.Normal);
        _jetwayLvar = simVars.Subscribe(GsxLvarNames.Jetway, "number", DataRefTier.Normal);

        _toggleCargo1.ValueChanged += OnLvarChanged;
        _toggleCargo2.ValueChanged += OnLvarChanged;
        _toggleService1.ValueChanged += OnLvarChanged;
        _toggleService2.ValueChanged += OnLvarChanged;
        _cargoExit0.ValueChanged += OnLvarChanged;
        _cargoExit1.ValueChanged += OnLvarChanged;
        _stairsState.ValueChanged += OnLvarChanged;
        lifecycle.ServiceEvent += OnServiceEvent;

        _reassertTimer = new Timer(_ => ReassertDoorsMsgSuppression(), null, ReassertInterval, ReassertInterval);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _reassertTimer.Dispose();
        _door1L.Dispose();
        _door4L.Dispose();
        _door1R.Dispose();
        _door4R.Dispose();
        _cargoFwd.Dispose();
        _cargoAft.Dispose();
        _cargoBulk.Dispose();
        _disableDoorsMsg.Dispose();
        _toggleCargo1.Dispose();
        _toggleCargo2.Dispose();
        _toggleService1.Dispose();
        _toggleService2.Dispose();
        _cargoExit0.Dispose();
        _cargoExit1.Dispose();
        _stairsState.Dispose();
        _jetwayLvar.Dispose();
    }

    private bool Enabled => _options.CurrentValue.AutomationEnabled && _options.CurrentValue.DoorAutomationEnabled;

    /// <summary>True while any tracked entry or cargo door reads open in ProSim — the
    /// pushback sequence's doors-closed gate.</summary>
    public bool AnyDoorOpen =>
        _door1L.GetValue(false) || _door4L.GetValue(false)
        || _door1R.GetValue(false) || _door4R.GetValue(false)
        || _cargoFwd.GetValue(false) || _cargoAft.GetValue(false) || _cargoBulk.GetValue(false);

    /// <summary>Closes every tracked door that reads open (the departure-sequence doors step).</summary>
    public async Task CloseAllDoorsAsync()
    {
        RecordDecision("doors", "closing all doors");
        await SetDoorAsync(_door1L, ProsimDataRefNames.Door1L, false, "1L").ConfigureAwait(false);
        await SetDoorAsync(_door4L, ProsimDataRefNames.Door4L, false, "4L").ConfigureAwait(false);
        await SetDoorAsync(_door1R, ProsimDataRefNames.Door1R, false, "1R").ConfigureAwait(false);
        await SetDoorAsync(_door4R, ProsimDataRefNames.Door4R, false, "4R").ConfigureAwait(false);
        await SetDoorAsync(_cargoFwd, ProsimDataRefNames.DoorCargoForward, false, "cargo fwd").ConfigureAwait(false);
        await SetDoorAsync(_cargoAft, ProsimDataRefNames.DoorCargoAft, false, "cargo aft").ConfigureAwait(false);
        await SetDoorAsync(_cargoBulk, ProsimDataRefNames.DoorCargoBulk, false, "cargo bulk").ConfigureAwait(false);
    }

    /// <summary>DISABLE_DOORS_MSG resets to 0 whenever Couatl restarts — this periodic
    /// compare-and-write keeps it asserted (or clears it when the option is turned off).</summary>
    private void ReassertDoorsMsgSuppression()
    {
        try
        {
            var desired = Enabled && _options.CurrentValue.SuppressGsxDoorMessages ? 1.0 : 0.0;
            if (Math.Abs(_disableDoorsMsg.GetValue(desired) - desired) > 0.5)
            {
                _ = _simVars.WriteAsync(GsxLvarNames.DisableDoorsMsg, desired);
                _logger.LogInformation("GSX door-message suppression LVAR set to {Value}", desired);
            }
        }
        catch (InvalidOperationException)
        {
            // MSFS not connected — nothing to suppress.
        }
    }

    private void OnServiceEvent(string serviceId, GsxServiceLifecycleEvent lifecycleEvent)
    {
        if (!Enabled)
        {
            return;
        }

        var isBoarding = serviceId.Equals("Boarding", StringComparison.OrdinalIgnoreCase);
        var isDeboarding = serviceId.Equals("Deboarding", StringComparison.OrdinalIgnoreCase);
        if (!isBoarding && !isDeboarding)
        {
            return;
        }

        switch (lifecycleEvent)
        {
            case GsxServiceLifecycleEvent.Active:
                _loadingActive = true;
                _ = OpenCargoDoorsDelayedAsync(isBoarding ? "boarding" : "deboarding");
                break;

            case GsxServiceLifecycleEvent.Completed:
                _loadingActive = false;
                if (isDeboarding && _options.CurrentValue.KeepCargoDoorsOpenAfterUnload)
                {
                    RecordDecision("doors", "deboarding complete — cargo doors kept open (configured)");
                    break;
                }
                _ = CloseCargoDoorsAsync(isBoarding ? "boarding complete" : "deboarding complete");
                break;
        }
    }

    private async Task OpenCargoDoorsDelayedAsync(string reason)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.CargoDoorOpenDelaySec)),
                _shutdown.Token).ConfigureAwait(false);
            RecordDecision("doors", $"{reason} started — opening cargo doors");
            await SetDoorAsync(_cargoFwd, ProsimDataRefNames.DoorCargoForward, true, "cargo fwd").ConfigureAwait(false);
            await SetDoorAsync(_cargoAft, ProsimDataRefNames.DoorCargoAft, true, "cargo aft").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cargo door open failed");
        }
    }

    private async Task CloseCargoDoorsAsync(string reason)
    {
        try
        {
            RecordDecision("doors", $"{reason} — closing cargo doors");
            await SetDoorAsync(_cargoFwd, ProsimDataRefNames.DoorCargoForward, false, "cargo fwd").ConfigureAwait(false);
            await SetDoorAsync(_cargoAft, ProsimDataRefNames.DoorCargoAft, false, "cargo aft").ConfigureAwait(false);
            await SetDoorAsync(_cargoBulk, ProsimDataRefNames.DoorCargoBulk, false, "cargo bulk").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cargo door close failed");
        }
    }

    private void OnLvarChanged(object? sender, EventArgs e)
    {
        try
        {
            if (!Enabled)
            {
                return;
            }

            HandleToggle(_toggleCargo1, ref _lastToggleCargo1, () =>
                OpenIfClosedAsync(_cargoFwd, ProsimDataRefNames.DoorCargoForward, "cargo fwd", "GSX cargo-1 toggle"));
            HandleToggle(_toggleCargo2, ref _lastToggleCargo2, () =>
                OpenIfClosedAsync(_cargoAft, ProsimDataRefNames.DoorCargoAft, "cargo aft", "GSX cargo-2 toggle"));
            HandleToggle(_toggleService1, ref _lastToggleService1, () =>
                ToggleDoorAsync(_door1R, ProsimDataRefNames.Door1R, "1R", "GSX service-1 toggle"));
            HandleToggle(_toggleService2, ref _lastToggleService2, () =>
                ToggleDoorAsync(_door4R, ProsimDataRefNames.Door4R, "4R", "GSX service-2 toggle"));

            HandleCargoExit(_cargoExit0, ref _lastCargoExit0, _cargoFwd, ProsimDataRefNames.DoorCargoForward, "cargo fwd");
            HandleCargoExit(_cargoExit1, ref _lastCargoExit1, _cargoAft, ProsimDataRefNames.DoorCargoAft, "cargo aft");

            HandleStairs();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Door LVAR handling failed");
        }
    }

    private static void HandleToggle(IDataRefSubscription toggle, ref double last, Func<Task> action)
    {
        var value = toggle.GetValue(0.0);
        var rising = value != 0 && last == 0;
        last = value;
        if (rising)
        {
            _ = action();
        }
    }

    /// <summary>A loader LVAR dropping back to 0 after working means that hold is done — close
    /// its door after the crew-realism delay (predecessor: 16 s).</summary>
    private void HandleCargoExit(
        IDataRefSubscription exit,
        ref double last,
        IDataRefSubscription door,
        string doorRef,
        string label)
    {
        var value = exit.GetValue(0.0);
        var finished = value == 0 && last != 0;
        last = value;
        if (finished && _loadingActive && door.GetValue(false))
        {
            var delay = Math.Max(0, _options.CurrentValue.CargoDoorCloseDelaySec);
            RecordDecision("doors", $"{label} loader finished — closing in {delay}s");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), _shutdown.Token).ConfigureAwait(false);
                    await SetDoorAsync(door, doorRef, false, label).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
    }

    /// <summary>Stairs docked (state 5) open L4 — and L1 too at jetway-less stands (with a
    /// jetway, ProSim's autoDoor owns L1). Stairs leaving close what we opened.</summary>
    private void HandleStairs()
    {
        var active = (int)_stairsState.GetValue(0.0) == StairsActiveState;
        if (active == _stairsWereActive)
        {
            return;
        }
        _stairsWereActive = active;

        var noJetway = (int)_jetwayLvar.GetValue(0.0) == JetwayLvarNotPresent;
        if (active)
        {
            _stairsOpenedL1 = noJetway;
            RecordDecision("doors", $"stairs docked — opening L4{(noJetway ? " + L1 (no jetway at stand)" : "")}");
            _ = OpenIfClosedAsync(_door4L, ProsimDataRefNames.Door4L, "4L", "stairs docked");
            if (noJetway)
            {
                _ = OpenIfClosedAsync(_door1L, ProsimDataRefNames.Door1L, "1L", "stairs docked");
            }
        }
        else
        {
            RecordDecision("doors", "stairs leaving — closing entry doors they served");
            _ = SetDoorAsync(_door4L, ProsimDataRefNames.Door4L, false, "4L");
            if (_stairsOpenedL1)
            {
                _stairsOpenedL1 = false;
                _ = SetDoorAsync(_door1L, ProsimDataRefNames.Door1L, false, "1L");
            }
        }
    }

    private Task OpenIfClosedAsync(IDataRefSubscription door, string doorRef, string label, string reason)
    {
        if (door.GetValue(false))
        {
            return Task.CompletedTask;
        }
        RecordDecision("doors", $"{reason} — opening {label}");
        return SetDoorAsync(door, doorRef, true, label);
    }

    private Task ToggleDoorAsync(IDataRefSubscription door, string doorRef, string label, string reason)
    {
        var newState = !door.GetValue(false);
        RecordDecision("doors", $"{reason} — {(newState ? "opening" : "closing")} {label}");
        return SetDoorAsync(door, doorRef, newState, label);
    }

    private async Task SetDoorAsync(IDataRefSubscription door, string doorRef, bool open, string label)
    {
        if (door.GetValue(false) == open)
        {
            return;
        }
        var ok = await _writer.WriteAsync(doorRef, open).ConfigureAwait(false);
        _logger.LogDebug("Door {Door} -> {State} (written {Ok})", label, open ? "open" : "closed", ok);
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
