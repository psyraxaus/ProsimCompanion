using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

// Nose-wheel-steering probe (2026-11-14) — see the csproj comment. Read-only until a number
// key is pressed; every value change and every action is written to the console and to
// nwsprobe.log beside the exe, so the file can be read from another PC afterwards.

var logPath = Path.Combine(AppContext.BaseDirectory, "nwsprobe.log");
void Log(string text)
{
    var line = $"{DateTime.Now:HH:mm:ss.fff} {text}";
    Console.WriteLine(line);
    try
    {
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
    catch (IOException)
    {
        // the console still has it
    }
}

// What ProSim could be mirroring: the simulator's pushback, freeze and steering state, and
// GSX's own pushback LVARs for the timeline.
(string Name, string Unit)[] vars =
[
    ("PUSHBACK STATE", "Enum"),
    ("PUSHBACK ATTACHED", "Bool"),
    ("PUSHBACK AVAILABLE", "Bool"),
    ("PUSHBACK WAIT", "Bool"),
    ("PUSHBACK ANGLE", "Degrees"),
    ("IS LATITUDE LONGITUDE FREEZE ON", "Bool"),
    ("IS ALTITUDE FREEZE ON", "Bool"),
    ("IS ATTITUDE FREEZE ON", "Bool"),
    ("IS SLEW ACTIVE", "Bool"),
    ("SIM DISABLED", "Bool"),
    ("NOSEWHEEL LOCK ON", "Bool"),
    ("TOW CONNECTION", "Bool"),
    ("STEER INPUT CONTROL", "Percent Over 100"),
    ("GEAR STEER ANGLE:0", "Degrees"),
    ("RUDDER PEDAL POSITION", "Position"),
    ("BRAKE PARKING POSITION", "Bool"),
    ("GROUND VELOCITY", "Knots"),
    ("L:FSDT_GSX_PUSHBACK_STATUS", "Number"),
    ("L:FSDT_GSX_VEHICLE_PUSHBACK_STATE", "Number"),
    ("L:FSDT_GSX_BYPASS_PIN", "Number"),
    // GSX's "the aircraft is frozen / held by us" family — the likeliest thing ProSim's own
    // "GSX pushback support" (1.47) watches. An LVAR that does not exist reads 0.
    ("L:FSDT_VAR_Frozen", "Number"),
    ("L:FSDT_VAR_EnginesStopped", "Number"),
    ("L:FSDT_GSX_STATE", "Number"),
    ("L:FSDT_GSX_DEPARTURE_STATE", "Number"),
    ("L:FSDT_GSX_COUATL_STARTED", "Number"),
];

// The candidate release actions. Key events first (they go through the sim's own handling),
// then the direct variable writes.
(char Key, string Label, string? Event, uint Data, string? SetVar, string? SetUnit, double SetValue)[] actions =
[
    ('1', "event TUG_DISABLE (stop/detach the sim's tug)", "TUG_DISABLE", 0, null, null, 0),
    ('2', "set PUSHBACK WAIT = 0", null, 0, "PUSHBACK WAIT", "Bool", 0),
    ('3', "event FREEZE_LATITUDE_LONGITUDE_SET 0", "FREEZE_LATITUDE_LONGITUDE_SET", 0, null, null, 0),
    ('4', "event FREEZE_ALTITUDE_SET 0", "FREEZE_ALTITUDE_SET", 0, null, null, 0),
    ('5', "event FREEZE_ATTITUDE_SET 0", "FREEZE_ATTITUDE_SET", 0, null, null, 0),
    ('6', "set PUSHBACK ATTACHED = 0", null, 0, "PUSHBACK ATTACHED", "Bool", 0),
    ('7', "set PUSHBACK STATE = 3 (no pushback)", null, 0, "PUSHBACK STATE", "Enum", 3),
    ('8', "event TOGGLE_PUSHBACK (CAUTION: the aircraft can move — press again to stop)", "TOGGLE_PUSHBACK", 0, null, null, 0),
    ('9', "event TOW_PLANE_RELEASE", "TOW_PLANE_RELEASE", 0, null, null, 0),
    ('0', "event SLEW_OFF", "SLEW_OFF", 0, null, null, 0),
    ('F', "set L:FSDT_VAR_Frozen = 0", null, 0, "L:FSDT_VAR_Frozen", "Number", 0),
    ('G', "set L:FSDT_VAR_Frozen = 1 (to see ProSim react — then press F)", null, 0, "L:FSDT_VAR_Frozen", "Number", 1),
    ('P', "set L:FSDT_GSX_PUSHBACK_STATUS = 0", null, 0, "L:FSDT_GSX_PUSHBACK_STATUS", "Number", 0),
    ('V', "set L:FSDT_GSX_VEHICLE_PUSHBACK_STATE = 0", null, 0, "L:FSDT_GSX_VEHICLE_PUSHBACK_STATE", "Number", 0),
];

const uint WriteDefBase = 1000;
const uint EventBase = 2000;
var last = new double?[vars.Length];
using var signal = new AutoResetEvent(false);
SimConnect sim;
try
{
    sim = new SimConnect("NwsProbe", IntPtr.Zero, 0, signal, 0);
}
catch (COMException ex)
{
    Log($"MSFS is not running or SimConnect refused the connection: {ex.Message}");
    return 1;
}

var quit = false;
sim.OnRecvOpen += (_, open) => Log($"connected to {open.szApplicationName} {open.dwApplicationVersionMajor}.{open.dwApplicationVersionMinor} build {open.dwApplicationBuildMajor}.{open.dwApplicationBuildMinor}");
sim.OnRecvQuit += (_, _) =>
{
    Log("the simulator quit");
    quit = true;
};
sim.OnRecvException += (_, ex) =>
    Log($"SimConnect exception {(SIMCONNECT_EXCEPTION)ex.dwException} (send {ex.dwSendID}, index {ex.dwIndex}) — a name the sim does not know, or a variable that cannot be set");
sim.OnRecvSimobjectData += (_, data) =>
{
    var index = (int)data.dwRequestID;
    if (index < 0 || index >= vars.Length)
    {
        return;
    }

    var value = ((Value)data.dwData[0]).Number;
    if (last[index] is { } before && Math.Abs(before - value) < 0.0005)
    {
        return;
    }

    Log(last[index] is null
        ? $"  {vars[index].Name,-36} = {Format(value)}"
        : $"  {vars[index].Name,-36} {Format(last[index]!.Value)} -> {Format(value)}   <== CHANGED");
    last[index] = value;
};

// One definition and one request per variable: a name the sim refuses fails alone.
for (var i = 0; i < vars.Length; i++)
{
    sim.AddToDataDefinition((Id)i, vars[i].Name, vars[i].Unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
    sim.RegisterDataDefineStruct<Value>((Id)i);
    sim.RequestDataOnSimObject((Id)i, (Id)i, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
}

for (var i = 0; i < actions.Length; i++)
{
    var action = actions[i];
    if (action.Event is not null)
    {
        sim.MapClientEventToSimEvent((Id)(EventBase + i), action.Event);
    }
    else
    {
        sim.AddToDataDefinition((Id)(WriteDefBase + i), action.SetVar!, action.SetUnit!, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
        sim.RegisterDataDefineStruct<Value>((Id)(WriteDefBase + i));
    }
}

Log("NwsProbe started. First block = the values now. Keys:");
foreach (var action in actions)
{
    Log($"   [{action.Key}] {action.Label}");
}

Log("   [M] write a marker line   [Q] quit");

while (!quit)
{
    if (signal.WaitOne(100))
    {
        try
        {
            sim.ReceiveMessage();
        }
        catch (COMException ex)
        {
            Log($"SimConnect receive failed: {ex.Message}");
            break;
        }
    }

    while (Console.KeyAvailable)
    {
        var pressed = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
        if (pressed == 'Q')
        {
            quit = true;
            break;
        }

        if (pressed == 'M')
        {
            Log("---- MARKER (pilot) ----");
            continue;
        }

        var index = Array.FindIndex(actions, a => a.Key == pressed);
        if (index < 0)
        {
            continue;
        }

        var action = actions[index];
        Log($">>>> ACTION [{action.Key}] {action.Label}");
        try
        {
            if (action.Event is not null)
            {
                sim.TransmitClientEvent(
                    SimConnect.SIMCONNECT_OBJECT_ID_USER, (Id)(EventBase + index), action.Data,
                    Group.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
            else
            {
                sim.SetDataOnSimObject(
                    (Id)(WriteDefBase + index), SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                    new Value { Number = action.SetValue });
            }
        }
        catch (COMException ex)
        {
            Log($"action failed: {ex.Message}");
        }
    }
}

sim.Dispose();
Log("NwsProbe stopped.");
return 0;

static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

internal enum Id : uint
{
}

internal enum Group : uint
{
    Highest = 1, // SIMCONNECT_GROUP_PRIORITY_HIGHEST
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct Value
{
    public double Number;
}
