using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.Roles;

namespace ProsimCompanion.Speech.Fcu;

/// <summary>
/// Executes voice FCU instructions with Prosim2FO's gate sequence: FO-is-PF → announce the
/// readback → a cancel-window pause (3 s; "negative"/"disregard"/"cancel"/"belay that"
/// aborts) → back off if the pilot moved that knob during the pause ("your aircraft on the
/// heading") → write + verify with ONE retry → on second failure the field goes
/// advisory-only until the next handover. Values are written directly to the analogs and the
/// knob pulled (selected); altitude sets the value only — the vertical mode stays an explicit
/// command. Fixed predecessor bug: the V/S managed verify no longer reads the heading
/// indicator (V/S has no managed indicator — pulse is unverified like Expedite).
/// </summary>
public sealed class FcuExecutor : IVoiceFeature, IDisposable
{
    private static readonly string[] CancelWords = ["negative", "disregard", "cancel", "belay that"];
    private static readonly TimeSpan VerifyPause = TimeSpan.FromSeconds(3);

    private readonly IProsimDataRefs _dataRefs;
    private readonly RoleManager _roles;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FcuExecutor> _logger;
    private readonly SemaphoreSlim _execGate = new(1, 1);
    private readonly Dictionary<string, IDataRefSubscription<double>> _reads = new(StringComparer.Ordinal);
    private readonly HashSet<FcuField> _inhibited = [];
    private readonly object _gate = new();

    private CancellationTokenSource? _pending;

    public FcuExecutor(
        IProsimDataRefs dataRefs,
        RoleManager roles,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<FcuExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _roles = roles;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        // A handover cancels anything pending and clears the advisory-only inhibits.
        _roles.Changed += () =>
        {
            CancelPending();
            lock (_gate)
            {
                _inhibited.Clear();
            }
        };
    }

    public bool Enabled => true;

    public IEnumerable<string> Phrases =>
    [
        .. CancelWords,
        "engage autopilot", "engage autopilot one", "engage autopilot two", "engage autothrust",
        "arm approach", "arm localizer", "expedite",
        "resume own navigation", "managed speed", "selected speed", "open descent", "managed descent",
        "set heading", "turn left heading", "turn right heading", "fly heading",
        "descend flight level", "climb flight level", "maintain flight level",
        "reduce speed", "increase speed", "maintain speed", "vertical speed",
    ];

    public bool ValueParse => true;

    public bool TryHandle(string utterance)
    {
        // A checklist request is never an FCU instruction — "after takeoff climb checklist"
        // otherwise classifies as a conditional via its condition word + "climb" (issue #47);
        // let it fall through to the checklist start matching.
        if (utterance.Contains("checklist", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = CommandMatcher.Normalize(utterance);
        if (CancelWords.Contains(normalized))
        {
            if (CancelPending())
            {
                _ = _arbiter.SpeakAsync("Disregard.", SpeechPriority.High);
                _eventLog.Record("fcu.cancelled", new { });
                return true;
            }

            return false; // nothing pending — let other features see the cancel word
        }

        var instruction = AtcInstructionParser.Parse(utterance);
        switch (instruction.Type)
        {
            case FcuInstructionType.Unknown:
                return false;

            case FcuInstructionType.Conditional:
                _ = _arbiter.SpeakAsync("Copied — conditional. Call it when it applies.", SpeechPriority.Normal);
                return true;

            case FcuInstructionType.Query:
                // Tagged "clarifier" (issue #66): these lines were the only untagged entries
                // in the session jsonl, which is exactly what made the misroute hard to spot.
                _ = _arbiter.EnqueueAsync(new SpeechRequest(
                    $"Say again — {instruction.Reason}.", SpeechPriority.Normal, Tag: "clarifier"));
                return true;
        }

        if (!_roles.IsFoPilotFlying)
        {
            _ = _arbiter.SpeakAsync("You're pilot flying — the FCU is yours.", SpeechPriority.Normal);
            return true;
        }

        if (instruction.Field is { } field && IsInhibited(field))
        {
            _ = _arbiter.SpeakAsync(
                $"{FieldLabel(field)} is advisory only — your aircraft on the {FieldLabel(field).ToLowerInvariant()}.",
                SpeechPriority.Normal);
            _eventLog.Record("fcu.inhibited", new { field = field.ToString() });
            return true;
        }

        _ = ExecuteAsync(instruction);
        return true;
    }

    public void Dispose()
    {
        CancelPending();
        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }
    }

    private async Task ExecuteAsync(FcuInstruction instruction)
    {
        // Supersede any pending action.
        CancelPending();
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _pending = cts;
        }

        try
        {
            var readback = instruction.Readback ?? DescribeEngagement(instruction);
            var preValue = instruction is { Type: FcuInstructionType.SetValue, Field: { } f }
                ? Read(ValueRef(f))
                : (double?)null;

            _eventLog.Record("fcu.announce", new { text = readback });
            await _arbiter.EnqueueAsync(new SpeechRequest(
                $"Setting {readback}.", SpeechPriority.High, Tag: "fcu")).ConfigureAwait(false);
            await Task.Delay(VerifyPause, cts.Token).ConfigureAwait(false);

            // Back off if the pilot dialled that knob during the pause.
            if (instruction is { Type: FcuInstructionType.SetValue, Field: { } fieldSet }
                && preValue is { } pre && Math.Abs(Read(ValueRef(fieldSet)) - pre) > 0.5)
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    $"You're on the {FieldLabel(fieldSet).ToLowerInvariant()} — your {FieldLabel(fieldSet).ToLowerInvariant()}.",
                    SpeechPriority.High, Tag: "fcu")).ConfigureAwait(false);
                _eventLog.Record("fcu.backoff", new { field = fieldSet.ToString() });
                return;
            }

            await _execGate.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (!_roles.IsFoPilotFlying)
                {
                    _eventLog.Record("fcu.aborted", new { reason = "role-changed" });
                    return;
                }

                var ok = await ApplyAsync(instruction, cts.Token).ConfigureAwait(false)
                    || await ApplyAsync(instruction, cts.Token).ConfigureAwait(false);
                if (ok)
                {
                    await _arbiter.EnqueueAsync(new SpeechRequest(
                        $"{Capitalize(readback)} set.", SpeechPriority.High, Tag: "fcu")).ConfigureAwait(false);
                    _eventLog.Record("fcu.set", new { text = readback });
                }
                else
                {
                    var label = instruction.Field is { } failed ? FieldLabel(failed).ToLowerInvariant() : "FCU";
                    await _arbiter.EnqueueAsync(new SpeechRequest(
                        $"Unable — your aircraft on the {label}.", SpeechPriority.High, Tag: "fcu"))
                        .ConfigureAwait(false);
                    _eventLog.Record("fcu.unable", new { text = readback });
                    if (instruction.Field is { } inhibit)
                    {
                        lock (_gate)
                        {
                            _inhibited.Add(inhibit);
                        }
                    }
                }
            }
            finally
            {
                _execGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled by "disregard", supersession, or handover.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FCU execution failed");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, cts))
                {
                    _pending = null;
                }
            }

            cts.Dispose();
        }
    }

    private async Task<bool> ApplyAsync(FcuInstruction instruction, CancellationToken ct)
    {
        switch (instruction.Type)
        {
            case FcuInstructionType.SetValue when instruction is { Field: { } field, Value: { } value }:
                if (field == FcuField.Altitude)
                {
                    // Value only — the vertical mode stays an explicit command.
                    await Write(ValueRef(field).Name, (int)value, ct).ConfigureAwait(false);
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    return Math.Abs(Read(ValueRef(field)) - value) <= 1;
                }

                await Write(ValueRef(field).Name, (int)value, ct).ConfigureAwait(false);
                await Task.Delay(200, ct).ConfigureAwait(false);
                await Pulse(KnobRef(field), 2, ct).ConfigureAwait(false); // pull = selected
                await Task.Delay(150, ct).ConfigureAwait(false);
                var tolerance = field == FcuField.VerticalSpeed ? 50 : 1;
                return Math.Abs(Read(ValueRef(field)) - value) <= tolerance;

            case FcuInstructionType.Managed when instruction.Field is { } managedField:
                return await PulseVerify(KnobRef(managedField), 1,
                    ManagedIndicator(managedField), expect: 1, ct).ConfigureAwait(false);

            case FcuInstructionType.Selected when instruction.Field is { } selectedField:
                return await PulseVerify(KnobRef(selectedField), 2,
                    ManagedIndicator(selectedField), expect: 0, ct).ConfigureAwait(false);

            case FcuInstructionType.Autopilot:
                var two = instruction.RawText.Contains("two", StringComparison.OrdinalIgnoreCase)
                    || instruction.RawText.Contains(" 2", StringComparison.Ordinal);
                return await PulseVerify(
                    two ? FcuControls.Ap2 : FcuControls.Ap1, 1,
                    two ? ProsimDataRefNames.FcuAp2Indicator : ProsimDataRefNames.FcuAp1Indicator,
                    expect: 1, ct).ConfigureAwait(false);

            case FcuInstructionType.AutoThrust:
                return await PulseVerify(FcuControls.Athr, 1, ProsimDataRefNames.FcuAthrIndicator, 1, ct).ConfigureAwait(false);

            case FcuInstructionType.Approach:
                return await PulseVerify(FcuControls.Appr, 1, ProsimDataRefNames.FcuApprIndicator, 1, ct).ConfigureAwait(false);

            case FcuInstructionType.Localizer:
                return await PulseVerify(FcuControls.Loc, 1, ProsimDataRefNames.FcuLocIndicator, 1, ct).ConfigureAwait(false);

            case FcuInstructionType.Expedite:
                await Pulse(FcuControls.Exped, 1, ct).ConfigureAwait(false);
                return true; // no persistent indicator — unverified by design

            case FcuInstructionType.SpeedMachToggle:
                await Pulse(FcuControls.SpdMach, 1, ct).ConfigureAwait(false);
                return true;

            default:
                return false;
        }
    }

    private async Task<bool> PulseVerify(string knob, int press, DataRef<double>? indicator, int expect, CancellationToken ct)
    {
        await Pulse(knob, press, ct).ConfigureAwait(false);
        await Task.Delay(250, ct).ConfigureAwait(false);
        if (indicator is not { } indicatorRef)
        {
            return true; // V/S has no managed indicator — pulse unverified (predecessor bug fixed)
        }

        return Math.Abs(Read(indicatorRef) - expect) < 0.5;
    }

    private async Task Pulse(string dataref, int press, CancellationToken ct)
    {
        await Write(dataref, press, ct).ConfigureAwait(false);
        await Task.Delay(150, ct).ConfigureAwait(false);
        await Write(dataref, 0, ct).ConfigureAwait(false);
    }

    private Task Write(string dataref, int value, CancellationToken ct)
        => _dataRefs.WriteAsync(dataref, value, ct);

    private double Read(DataRef<double> dataref)
    {
        if (!_reads.TryGetValue(dataref.Name, out var read))
        {
            read = _dataRefs.Subscribe(dataref);
            _reads[dataref.Name] = read;
        }

        return read.Value;
    }

    private bool CancelPending()
    {
        lock (_gate)
        {
            if (_pending is null)
            {
                return false;
            }

            _pending.Cancel();
            _pending = null;
            return true;
        }
    }

    private bool IsInhibited(FcuField field)
    {
        lock (_gate)
        {
            return _inhibited.Contains(field);
        }
    }

    private static DataRef<double> ValueRef(FcuField field) => field switch
    {
        FcuField.Heading => ProsimDataRefNames.FcuHeadingValue,
        FcuField.Altitude => ProsimDataRefNames.FcuAltitudeValue,
        FcuField.Speed => ProsimDataRefNames.FcuSpeedValue,
        _ => ProsimDataRefNames.FcuVsValue,
    };

    private static string KnobRef(FcuField field) => field switch
    {
        FcuField.Heading => FcuControls.HeadingKnob,
        FcuField.Altitude => FcuControls.AltitudeKnob,
        FcuField.Speed => FcuControls.SpeedKnob,
        _ => FcuControls.VsKnob,
    };

    private static DataRef<double>? ManagedIndicator(FcuField field) => field switch
    {
        FcuField.Heading => ProsimDataRefNames.FcuHeadingManaged,
        FcuField.Altitude => ProsimDataRefNames.FcuAltitudeManaged,
        FcuField.Speed => ProsimDataRefNames.FcuSpeedManaged,
        _ => null, // V/S: no managed indicator exists
    };

    private static string FieldLabel(FcuField field) => field switch
    {
        FcuField.Heading => "Heading",
        FcuField.Altitude => "Altitude",
        FcuField.Speed => "Speed",
        _ => "Vertical speed",
    };

    private static string DescribeEngagement(FcuInstruction instruction) => instruction.Type switch
    {
        FcuInstructionType.Autopilot => "autopilot",
        FcuInstructionType.AutoThrust => "autothrust",
        FcuInstructionType.Approach => "approach mode",
        FcuInstructionType.Localizer => "localizer",
        FcuInstructionType.Expedite => "expedite",
        FcuInstructionType.SpeedMachToggle => "speed mach toggle",
        FcuInstructionType.Managed => $"managed {instruction.Field?.ToString().ToLowerInvariant()}",
        FcuInstructionType.Selected => $"selected {instruction.Field?.ToString().ToLowerInvariant()}",
        _ => "that",
    };

    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
