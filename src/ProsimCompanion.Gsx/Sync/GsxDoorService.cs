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

    private readonly IDataRefSubscription<bool> _door1L;
    private readonly IDataRefSubscription<bool> _door4L;
    private readonly IDataRefSubscription<bool> _door1R;
    private readonly IDataRefSubscription<bool> _door4R;
    private readonly IDataRefSubscription<bool> _cargoFwd;
    private readonly IDataRefSubscription<bool> _cargoAft;
    private readonly IDataRefSubscription<bool> _cargoBulk;

    private readonly ISimVars _simVars;
    private readonly IDataRefSubscription<double> _disableDoorsMsg;
    private readonly IDataRefSubscription<double> _toggleCargo1;
    private readonly IDataRefSubscription<double> _toggleCargo2;
    private readonly IDataRefSubscription<double> _toggleService1;
    private readonly IDataRefSubscription<double> _toggleService2;
    private readonly IDataRefSubscription<double> _cargoExit0;
    private readonly IDataRefSubscription<double> _cargoExit1;
    private readonly IDataRefSubscription<double> _stairsState;
    private readonly IDataRefSubscription<double> _jetwayLvar;

    private readonly Timer _reassertTimer;
    private double _lastToggleCargo1;
    private double _lastToggleCargo2;
    private double _lastToggleService1;
    private double _lastToggleService2;
    private double _lastCargoExit0;
    private double _lastCargoExit1;
    private bool _stairsWereActive;
    private bool _stairsOpenedL1;

    private enum Loading { None, Boarding, Deboarding }

    private volatile Loading _loading = Loading.None;

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

        _door1L = prosim.Subscribe(ProsimDataRefNames.Door1L);
        _door4L = prosim.Subscribe(ProsimDataRefNames.Door4L);
        _door1R = prosim.Subscribe(ProsimDataRefNames.Door1R);
        _door4R = prosim.Subscribe(ProsimDataRefNames.Door4R);
        _cargoFwd = prosim.Subscribe(ProsimDataRefNames.DoorCargoForward);
        _cargoAft = prosim.Subscribe(ProsimDataRefNames.DoorCargoAft);
        _cargoBulk = prosim.Subscribe(ProsimDataRefNames.DoorCargoBulk);

        _disableDoorsMsg = simVars.Subscribe(GsxLvarNames.DisableDoorsMsg);
        _toggleCargo1 = simVars.Subscribe(GsxLvarNames.DoorToggleCargo1);
        _toggleCargo2 = simVars.Subscribe(GsxLvarNames.DoorToggleCargo2);
        _toggleService1 = simVars.Subscribe(GsxLvarNames.DoorToggleService1);
        _toggleService2 = simVars.Subscribe(GsxLvarNames.DoorToggleService2);
        _cargoExit0 = simVars.Subscribe(GsxLvarNames.BoardingCargoExit0);
        _cargoExit1 = simVars.Subscribe(GsxLvarNames.BoardingCargoExit1);
        _stairsState = simVars.Subscribe(GsxLvarNames.Stairs);
        _jetwayLvar = simVars.Subscribe(GsxLvarNames.Jetway);

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
        _door1L.Value || _door4L.Value
        || _door1R.Value || _door4R.Value
        || _cargoFwd.Value || _cargoAft.Value || _cargoBulk.Value;

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
            // GetValueOr(desired): compare against the intended value, so "no data yet" never
            // triggers a write — the catalog's static 0 fallback is meaningless here (#83).
            if (Math.Abs(_disableDoorsMsg.GetValueOr(desired) - desired) > 0.5)
            {
                _ = _simVars.WriteAsync(GsxLvarNames.DisableDoorsMsg.Name, desired);
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

        var options = _options.CurrentValue;
        switch (lifecycleEvent)
        {
            case GsxServiceLifecycleEvent.Active:
                _loading = isBoarding ? Loading.Boarding : Loading.Deboarding;
                if (options.DoorCargoHandling && options.DoorOpenOnBoardingActive)
                {
                    _ = OpenCargoDoorsDelayedAsync(isBoarding ? "boarding" : "deboarding");
                }
                break;

            case GsxServiceLifecycleEvent.Completed:
                _loading = Loading.None;
                if (!options.DoorCargoHandling)
                {
                    break;
                }
                if (isDeboarding && options.KeepCargoDoorsOpenAfterUnload)
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

            // Per-door-class gates (predecessor Gate & Doors sub-tab): each class of rule can
            // be turned off independently under the master door-automation switch.
            var options = _options.CurrentValue;
            if (options.DoorCargoHandling)
            {
                HandleToggle(_toggleCargo1, ref _lastToggleCargo1, () =>
                    OpenIfClosedAsync(_cargoFwd, ProsimDataRefNames.DoorCargoForward, "cargo fwd", "GSX cargo-1 toggle"));
                HandleToggle(_toggleCargo2, ref _lastToggleCargo2, () =>
                    OpenIfClosedAsync(_cargoAft, ProsimDataRefNames.DoorCargoAft, "cargo aft", "GSX cargo-2 toggle"));
                HandleCargoExit(_cargoExit0, ref _lastCargoExit0, _cargoFwd, ProsimDataRefNames.DoorCargoForward, "cargo fwd");
                HandleCargoExit(_cargoExit1, ref _lastCargoExit1, _cargoAft, ProsimDataRefNames.DoorCargoAft, "cargo aft");
            }

            if (options.DoorCateringHandling)
            {
                HandleToggle(_toggleService1, ref _lastToggleService1, () =>
                    ToggleDoorAsync(_door1R, ProsimDataRefNames.Door1R, "1R", "GSX service-1 toggle"));
                HandleToggle(_toggleService2, ref _lastToggleService2, () =>
                    ToggleDoorAsync(_door4R, ProsimDataRefNames.Door4R, "4R", "GSX service-2 toggle"));
            }

            if (options.DoorStairHandling)
            {
                HandleStairs();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Door LVAR handling failed");
        }
    }

    private static void HandleToggle(IDataRefSubscription<double> toggle, ref double last, Func<Task> action)
    {
        var value = toggle.Value;
        var rising = value != 0 && last == 0;
        last = value;
        if (rising)
        {
            _ = action();
        }
    }

    /// <summary>A loader LVAR dropping back to 0 after working means that hold is done — close
    /// its door after the crew-realism delay (predecessor: 16 s). The keep-open options skip
    /// this per direction (boarding = KeepCargoDoorsOpenAfterLoad, deboarding =
    /// KeepCargoDoorsOpenAfterUnload); the service-completed close still applies.</summary>
    private void HandleCargoExit(
        IDataRefSubscription<double> exit,
        ref double last,
        IDataRefSubscription<bool> door,
        DataRef<bool> doorRef,
        string label)
    {
        var value = exit.Value;
        var finished = value == 0 && last != 0;
        last = value;
        var keepOpen = _loading switch
        {
            Loading.Boarding => _options.CurrentValue.KeepCargoDoorsOpenAfterLoad,
            Loading.Deboarding => _options.CurrentValue.KeepCargoDoorsOpenAfterUnload,
            _ => false,
        };
        if (finished && keepOpen)
        {
            RecordDecision("doors", $"{label} loader finished — door kept open (configured)");
            return;
        }
        if (finished && _loading != Loading.None && door.Value)
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
        var active = (int)_stairsState.Value == StairsActiveState;
        if (active == _stairsWereActive)
        {
            return;
        }
        _stairsWereActive = active;

        var noJetway = (int)_jetwayLvar.Value == JetwayLvarNotPresent;
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

    private Task OpenIfClosedAsync(IDataRefSubscription<bool> door, DataRef<bool> doorRef, string label, string reason)
    {
        if (door.Value)
        {
            return Task.CompletedTask;
        }
        RecordDecision("doors", $"{reason} — opening {label}");
        return SetDoorAsync(door, doorRef, true, label);
    }

    private Task ToggleDoorAsync(IDataRefSubscription<bool> door, DataRef<bool> doorRef, string label, string reason)
    {
        var newState = !door.Value;
        RecordDecision("doors", $"{reason} — {(newState ? "opening" : "closing")} {label}");
        return SetDoorAsync(door, doorRef, newState, label);
    }

    private async Task SetDoorAsync(IDataRefSubscription<bool> door, DataRef<bool> doorRef, bool open, string label)
    {
        if (door.Value == open)
        {
            return;
        }
        var ok = await _writer.WriteAsync(doorRef.Name, open).ConfigureAwait(false);
        _logger.LogDebug("Door {Door} -> {State} (written {Ok})", label, open ? "open" : "closed", ok);
    }

    private void RecordDecision(string action, string reason)
    {
        _logger.LogInformation("GSX {Action}: {Reason}", action, reason);
        _diagnostics.RecordDecision(new GsxDecisionView(DateTimeOffset.UtcNow, action, reason));
    }
}
