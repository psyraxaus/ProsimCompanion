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
    private readonly ILogger<SimConnectService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, RegisteredVar> _registered = new(StringComparer.OrdinalIgnoreCase);
    private SimConnect? _simConnect;
    private EventWaitHandle? _messageSignal;
    private uint _nextDefinitionId = 1;
    private volatile bool _connected;

    public SimConnectService(
        SimVarService simVars,
        ConnectionStatusStore status,
        ILogger<SimConnectService> logger)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(logger);

        _simVars = simVars;
        _status = status;
        _logger = logger;
    }

    /// <summary>Definition/request id namespace — SimConnect wants enum types.</summary>
    private enum DefinitionId : uint
    {
    }

    private enum RequestId : uint
    {
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "SimConnect session failed; retrying");
            }
            finally
            {
                TearDownSession();
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

        lock (_gate)
        {
            foreach (var entry in _registered.Values)
            {
                RegisterWithSim(entry);
            }
        }

        _simVars.AttachBackend(this);
    }

    private void OnRecvQuit(SimConnect sender, SIMCONNECT_RECV data)
    {
        _logger.LogInformation("MSFS is shutting down; SimConnect disconnected");
        // Signal the pump loop to end the session; the outer loop retries.
        _simConnect = null;
    }

    private void OnRecvException(SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
        => _logger.LogWarning("SimConnect exception {Exception} (sendId {SendId})", (SIMCONNECT_EXCEPTION)data.dwException, data.dwSendID);

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
        }
        catch (COMException ex)
        {
            _logger.LogError(ex, "Failed to register SimVar {SimVar} ({Unit})", entry.Name, entry.Unit);
        }
    }

    private void TearDownSession()
    {
        var wasConnected = _connected;
        _connected = false;
        _simVars.DetachBackend();

        var simConnect = _simConnect;
        _simConnect = null;
        if (simConnect is not null)
        {
            simConnect.OnRecvOpen -= OnRecvOpen;
            simConnect.OnRecvQuit -= OnRecvQuit;
            simConnect.OnRecvException -= OnRecvException;
            simConnect.OnRecvSimobjectData -= OnRecvSimobjectData;
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
