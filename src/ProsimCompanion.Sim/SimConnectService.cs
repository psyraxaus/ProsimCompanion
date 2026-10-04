using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Sim;

/// <summary>
/// Hosted service owning the MSFS SimConnect session: headless event-handle message pump on a
/// background thread, retry loop while the simulator is not running, SimVar registrations
/// (double-typed, requested with the CHANGED flag) feeding <see cref="SimVarService"/>.
/// Self-degrading: a missing native SimConnect.dll disables the subsystem without affecting
/// anything else. LVAR access is a separate transport, decided at the start of Phase 2.
/// </summary>
public sealed class SimConnectService : BackgroundService, ISimVarBackend
{
    private const string AppName = "ProsimCompanion";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PumpWaitTimeout = TimeSpan.FromMilliseconds(500);

    private readonly SimVarService _simVars;
    private readonly ConnectionStatusStore _status;
    private readonly SimSessionSignals _sessionSignals;
    private readonly Facilities.AirportFacilityService? _facilities;
    private readonly ILogger<SimConnectService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, RegisteredVar> _registered = new(StringComparer.OrdinalIgnoreCase);
    private SimConnect? _simConnect;
    private EventWaitHandle? _messageSignal;
    private uint _nextDefinitionId = 1;
    private volatile bool _connected;
    private uint _lastFacilitySendId;
    private uint _lastFacilityRequestId;

    public SimConnectService(
        SimVarService simVars,
        ConnectionStatusStore status,
        SimSessionSignals sessionSignals,
        ILogger<SimConnectService> logger,
        Facilities.AirportFacilityService? facilities = null)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(sessionSignals);
        ArgumentNullException.ThrowIfNull(logger);

        _simVars = simVars;
        _status = status;
        _sessionSignals = sessionSignals;
        _logger = logger;
        _facilities = facilities;
    }

    /// <summary>Definition/request id namespace — SimConnect wants enum types.</summary>
    private enum DefinitionId : uint
    {
        /// <summary>The one facility definition (airport → parkings + jetways). Facility
        /// definitions are a separate SimConnect namespace from data definitions, but the
        /// id sits above the SimVar counter so logs never confuse the two.</summary>
        AirportFacilities = Facilities.AirportFacilityService.RequestIdBase,
    }

    private enum RequestId : uint
    {
    }

    /// <summary>System-event ids — a separate id space from data definitions/requests.</summary>
    private enum SystemEventId : uint
    {
        SimState = 1,
        PauseEx1 = 2,
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _status.Set(Subsystems.SimConnect, ConnectionState.Disconnected);
        var loggedWaiting = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunSession(stoppingToken, ref loggedWaiting);
            }
            catch (COMException)
            {
                // Simulator not running (or connection refused) — quiet retry.
                if (!loggedWaiting)
                {
                    loggedWaiting = true;
                    _logger.LogInformation("MSFS is not running; SimConnect will keep retrying quietly");
                }
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogError(ex, "Native SimConnect.dll could not be loaded; SimConnect subsystem disabled");
                _status.Set(Subsystems.SimConnect, ConnectionState.Disabled);
                return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or TypeLoadException)
            {
                // The managed wrapper (Microsoft.FlightSimulator.SimConnect.dll) is mixed-mode
                // C++/CLI: without the VC++ 2019+ runtime (vcruntime140_1.dll) the loader
                // reports the WRAPPER as "module not found" — a FileNotFoundException, not the
                // DllNotFoundException above. Observed 2026-09-19 on a fresh dev workstation;
                // it used to escape the loop and stop the whole host (StopHost behaviour).
                _logger.LogError(ex,
                    "SimConnect wrapper could not be loaded (install the Microsoft Visual C++ 2015-2022 x64 runtime); SimConnect subsystem disabled");
                _status.Set(Subsystems.SimConnect, ConnectionState.Disabled);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SimConnect session failed; retrying");
            }
            finally
            {
                TearDownSessionSafely();
            }

            try
            {
                await Task.Delay(RetryInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    void ISimVarBackend.EnsureRegistered(string simVarName, string unit, int intervalMs)
    {
        lock (_gate)
        {
            if (_registered.ContainsKey(simVarName))
            {
                return;
            }

            var entry = new RegisteredVar(_nextDefinitionId++, simVarName, unit, intervalMs);
            _registered[simVarName] = entry;

            if (_connected && _simConnect is not null)
            {
                RegisterWithSim(entry);
            }
        }
    }

    Task ISimVarBackend.WriteValueAsync(string simVarName, double value, CancellationToken cancellationToken)
    {
        // SetDataOnSimObject needs an existing data definition; writes register on demand at a
        // relaxed cadence (the write echo arrives through the subscription stream regardless).
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            RegisteredVar entry;
            SimConnect? simConnect;
            lock (_gate)
            {
                if (!_registered.TryGetValue(simVarName, out entry!))
                {
                    entry = new RegisteredVar(_nextDefinitionId++, simVarName, "number", 2000);
                    _registered[simVarName] = entry;
                    if (_connected && _simConnect is not null)
                    {
                        RegisterWithSim(entry);
                    }
                }

                simConnect = _connected ? _simConnect : null;
            }

            if (simConnect is null)
            {
                throw new InvalidOperationException("MSFS is not connected — the SimVar write cannot be delivered.");
            }

            simConnect.SetDataOnSimObject(
                (DefinitionId)entry.Id,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                new DoubleValue { Value = value });
            _logger.LogDebug("Wrote {Value} to SimVar {SimVar}", value, simVarName);
        }, cancellationToken);
    }

    void ISimVarBackend.Unregister(string simVarName)
    {
        lock (_gate)
        {
            if (!_registered.Remove(simVarName, out var entry))
            {
                return;
            }

            if (_connected && _simConnect is not null)
            {
                try
                {
                    _simConnect.ClearDataDefinition((DefinitionId)entry.Id);
                }
                catch (COMException ex)
                {
                    _logger.LogWarning(ex, "Failed to clear SimConnect definition for {SimVar}", simVarName);
                }
            }
        }
    }

    /// <summary>Connects and pumps messages until the sim quits or shutdown is requested.
    /// Throws COMException when the simulator is unavailable.</summary>
    private void RunSession(CancellationToken stoppingToken, ref bool loggedWaiting)
    {
        _messageSignal = new EventWaitHandle(false, EventResetMode.AutoReset);
        var simConnect = new SimConnect(AppName, IntPtr.Zero, 0, _messageSignal, 0);
        _simConnect = simConnect;

        simConnect.OnRecvOpen += OnRecvOpen;
        simConnect.OnRecvQuit += OnRecvQuit;
        simConnect.OnRecvException += OnRecvException;
        simConnect.OnRecvSimobjectData += OnRecvSimobjectData;
        simConnect.OnRecvEvent += OnRecvEvent;
        simConnect.OnRecvFacilityData += OnRecvFacilityData;
        simConnect.OnRecvFacilityDataEnd += OnRecvFacilityDataEnd;

        loggedWaiting = false;

        // Pump until quit/disconnect. ReceiveMessage dispatches the events above on this thread.
        while (!stoppingToken.IsCancellationRequested && _simConnect is not null)
        {
            if (_messageSignal.WaitOne(PumpWaitTimeout))
            {
                _simConnect?.ReceiveMessage();
            }
        }
    }

    private void OnRecvOpen(SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        _connected = true;
        _logger.LogInformation(
            "Connected to {Sim} {Major}.{Minor}",
            data.szApplicationName,
            data.dwApplicationVersionMajor,
            data.dwApplicationVersionMinor);
        _status.Set(Subsystems.SimConnect, ConnectionState.Connected);

        // MSFS 2020 identifies as KittyHawk 11.x; 2024 as a later major. The version decides
        // whether the session monitor may subscribe the 2024-only avatar SimVars.
        _sessionSignals.SetConnected(
            $"{data.szApplicationName} {data.dwApplicationVersionMajor}.{data.dwApplicationVersionMinor}",
            isMsfs2024: data.dwApplicationVersionMajor >= 12);

        try
        {
            // The handshake succeeds from the main menu, so "connected" says nothing about a
            // flight session. These two events (plus CAMERA STATE, sampled by the session
            // monitor) carry the actual session lifecycle; both transmit their current state
            // immediately on subscribe.
            sender.SubscribeToSystemEvent(SystemEventId.SimState, "Sim");
            sender.SubscribeToSystemEvent(SystemEventId.PauseEx1, "Pause_EX1");
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "System-event subscription failed; sim-session detection degraded");
        }

        lock (_gate)
        {
            foreach (var entry in _registered.Values)
            {
                RegisterWithSim(entry);
            }
        }

        _simVars.AttachBackend(this);
        RegisterFacilityDefinition(sender);
    }

    /// <summary>Airport → TAXI_PARKING + JETWAY facility definition (Option B, 2026-10-04),
    /// registered per connection. A field name the running sim rejects raises a SimConnect
    /// exception asynchronously; the definition is then marked failed on the first facility
    /// exception so the tier stays quiet instead of timing out every lookup.</summary>
    private void RegisterFacilityDefinition(SimConnect sender)
    {
        if (_facilities is null)
        {
            return;
        }

        try
        {
            foreach (var field in Facilities.FacilityDefinition.Fields)
            {
                sender.AddToFacilityDefinition(DefinitionId.AirportFacilities, field);
            }

            sender.RegisterFacilityDataDefineStruct<Facilities.FacilityAirportRow>(SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
            sender.RegisterFacilityDataDefineStruct<Facilities.FacilityParkingRow>(SIMCONNECT_FACILITY_DATA_TYPE.TAXI_PARKING);
            sender.RegisterFacilityDataDefineStruct<Facilities.FacilityJetwayRow>(SIMCONNECT_FACILITY_DATA_TYPE.JETWAY);
            _facilities.Attach(RequestAirportFacility);
            _logger.LogInformation("Facility definition registered ({Fields} fields): airport parkings available", Facilities.FacilityDefinition.Fields.Length);
        }
        catch (COMException ex)
        {
            _logger.LogWarning(ex, "Facility definition failed; airport parking data from the simulator disabled this session");
            _facilities.MarkDefinitionFailed();
        }
    }

    /// <summary>Sends one airport facility request (from any thread, under the gate).</summary>
    private bool RequestAirportFacility(string icao, uint requestId)
    {
        lock (_gate)
        {
            if (!_connected || _simConnect is null)
            {
                return false;
            }

            try
            {
                _simConnect.RequestFacilityData(DefinitionId.AirportFacilities, (RequestId)requestId, icao, "");
                _lastFacilitySendId = _simConnect.GetLastSentPacketID();
                _lastFacilityRequestId = requestId;
                _logger.LogDebug("Requested facility data for {Icao} (request {RequestId}, send {SendId})", icao, requestId, _lastFacilitySendId);
                return true;
            }
            catch (COMException ex)
            {
                _logger.LogWarning(ex, "Facility request for {Icao} could not be sent", icao);
                return false;
            }
        }
    }

    private void OnRecvFacilityData(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (_facilities is null || data.Data is not [{ } row, ..])
        {
            return;
        }

        _facilities.OnRow(data.UserRequestId, row);
    }

    private void OnRecvFacilityDataEnd(SimConnect sender, SIMCONNECT_RECV_FACILITY_DATA_END data)
        => _facilities?.OnEnd(data.RequestId);

    private void OnRecvEvent(SimConnect sender, SIMCONNECT_RECV_EVENT data)
    {
        switch ((SystemEventId)data.uEventID)
        {
            case SystemEventId.SimState:
                _sessionSignals.SetSimRunning(data.dwData != 0);
                _logger.LogDebug("Sim state event: running={Running}", data.dwData != 0);
                break;

            case SystemEventId.PauseEx1:
                // Any Pause_EX1 flag (full / active / sim pause) counts as paused — the
                // "Ready to Fly" hold arrives as a pause flag on a valid cockpit camera.
                _sessionSignals.SetPaused(data.dwData != 0);
                _logger.LogDebug("Pause event: flags={Flags}", data.dwData);
                break;
        }
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        _logger.LogInformation("MSFS is shutting down; SimConnect disconnected");
        // Signal the pump loop to end the session; the outer loop retries.
        _simConnect = null;
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var exception = (SIMCONNECT_EXCEPTION)data.dwException;
        _logger.LogWarning("SimConnect exception {Exception} (sendId {SendId})", exception, data.dwSendID);

        // A facility request that the sim rejects (unknown ICAO, a definition field the running
        // sim does not have) comes back only as this exception — fail the pending lookup now
        // rather than letting it time out.
        if (_facilities is not null && data.dwSendID != 0 && data.dwSendID == _lastFacilitySendId)
        {
            _facilities.OnFailed(_lastFacilityRequestId, exception.ToString());
            if (exception is SIMCONNECT_EXCEPTION.DATA_ERROR or SIMCONNECT_EXCEPTION.INVALID_DATA_TYPE or SIMCONNECT_EXCEPTION.INVALID_DATA_SIZE)
            {
                _facilities.MarkDefinitionFailed();
            }
        }
    }

    private void OnRecvSimobjectData(SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        string? name = null;
        lock (_gate)
        {
            foreach (var entry in _registered.Values)
            {
                if (entry.Id == data.dwRequestID)
                {
                    name = entry.Name;
                    break;
                }
            }
        }

        if (name is not null && data.dwData is [DoubleValue value, ..])
        {
            _simVars.UpdateFromSim(name, value.Value);
        }
    }

    private void RegisterWithSim(RegisteredVar entry)
    {
        try
        {
            _simConnect!.AddToDataDefinition(
                (DefinitionId)entry.Id,
                entry.Name,
                entry.Unit,
                SIMCONNECT_DATATYPE.FLOAT64,
                0f,
                SimConnect.SIMCONNECT_UNUSED);
            _simConnect.RegisterDataDefineStruct<DoubleValue>((DefinitionId)entry.Id);

            // SECOND for relaxed tiers; SIM_FRAME (with CHANGED) for the fast ones.
            var period = entry.IntervalMs >= 500 ? SIMCONNECT_PERIOD.SECOND : SIMCONNECT_PERIOD.SIM_FRAME;
            _simConnect.RequestDataOnSimObject(
                (RequestId)entry.Id,
                (DefinitionId)entry.Id,
                SimConnect.SIMCONNECT_OBJECT_ID_USER,
                period,
                SIMCONNECT_DATA_REQUEST_FLAG.CHANGED,
                0,
                0,
                0);
            _logger.LogDebug(
                "Registered SimVar {SimVar} ({Unit}) at {Period}",
                entry.Name,
                entry.Unit,
                period);
        }
        catch (COMException ex)
        {
            _logger.LogError(ex, "Failed to register SimVar {SimVar} ({Unit})", entry.Name, entry.Unit);
        }
    }

    /// <summary>
    /// <see cref="TearDownSession"/> references SimConnect types, so merely JIT-compiling it
    /// throws when the wrapper assembly cannot load — inside a <c>finally</c> that exception
    /// escaped ExecuteAsync and took the host down. A teardown that cannot run has nothing to
    /// tear down; the subsystem is already flagged Disabled by the catch above.
    /// </summary>
    private void TearDownSessionSafely()
    {
        try
        {
            TearDownSession();
        }
        catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException or TypeLoadException)
        {
            _connected = false;
            _simVars.DetachBackend();
            _sessionSignals.SetDisconnected();
            _facilities?.Detach();
            _logger.LogDebug(ex, "SimConnect teardown skipped: wrapper assembly unavailable");
        }
    }

    private void TearDownSession()
    {
        var wasConnected = _connected;
        _connected = false;
        _simVars.DetachBackend();
        _sessionSignals.SetDisconnected();
        _facilities?.Detach();

        var simConnect = _simConnect;
        _simConnect = null;
        if (simConnect is not null)
        {
            simConnect.OnRecvOpen -= OnRecvOpen;
            simConnect.OnRecvQuit -= OnRecvQuit;
            simConnect.OnRecvException -= OnRecvException;
            simConnect.OnRecvSimobjectData -= OnRecvSimobjectData;
            simConnect.OnRecvEvent -= OnRecvEvent;
            simConnect.OnRecvFacilityData -= OnRecvFacilityData;
            simConnect.OnRecvFacilityDataEnd -= OnRecvFacilityDataEnd;
            simConnect.Dispose();
        }

        _messageSignal?.Dispose();
        _messageSignal = null;

        if (wasConnected)
        {
            _status.Set(Subsystems.SimConnect, ConnectionState.Disconnected);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DoubleValue
    {
        public double Value;
    }

    private sealed record RegisteredVar(uint Id, string Name, string Unit, int IntervalMs);
}
