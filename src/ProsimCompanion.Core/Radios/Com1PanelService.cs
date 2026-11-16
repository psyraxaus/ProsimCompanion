using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.Core.Radios;

/// <summary>
/// Mirrors ProSim's COM1 active/standby into the <see cref="RadioStore"/> and tunes COM1 for
/// the Radios tile (owner request 2026-10-25). Reads come from the push read model (one
/// subscription each, never a polled ReadDataRef); the tune is standby → verify → swap, the
/// same order the FO's radio executor uses, so a failed write never leaves the active
/// frequency half-changed. Degrades: without ProSim the store says "not live" and every tune
/// returns <see cref="RadioTuneOutcome.NotConnected"/>.
/// <para>The active frequency is read by one rule (<see cref="Com1Reading"/>, 2026-11-16): the
/// radio's own <c>N_FREQ_COM1</c> when it is live and above 0, else the R_ input. The store,
/// <see cref="ActiveKhz"/> and the tune's own verification carry that value; nothing else in
/// the app reads COM1 active for a judgement. Writes are unchanged — the R_ pair only.</para>
/// </summary>
public sealed class Com1PanelService : IStartupModule, IRadioTuner, IDisposable
{
    private static readonly TimeSpan VerifyPause = TimeSpan.FromMilliseconds(250);

    /// <summary>Reads of COM1 active after the swap before the tune is "unverified": the
    /// radio's own frequency follows the R_ write, not necessarily within one pause.</summary>
    private const int SwapVerifyReads = 8;

    private readonly IProsimDataRefs _dataRefs;
    private readonly RadioStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<Com1PanelService> _logger;
    private readonly SemaphoreSlim _tuneGate = new(1, 1);
    private IDataRefSubscription<double>? _active;
    private IDataRefSubscription<double>? _standby;
    private IDataRefSubscription<double>? _radio;

    public Com1PanelService(
        IProsimDataRefs dataRefs,
        RadioStore store,
        JsonlEventLog eventLog,
        ILogger<Com1PanelService> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _dataRefs = dataRefs;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        _active = _dataRefs.Subscribe(ProsimDataRefNames.RadioCom1Active);
        _standby = _dataRefs.Subscribe(ProsimDataRefNames.RadioCom1Standby);
        _radio = _dataRefs.Subscribe(ProsimDataRefNames.RadioCom1Frequency);
        _active.ValueChanged += OnCom1Changed;
        _standby.ValueChanged += OnCom1Changed;
        _radio.ValueChanged += OnCom1Changed;
        Publish();
    }

    public void Dispose()
    {
        if (_active is not null)
        {
            _active.ValueChanged -= OnCom1Changed;
            _active.Dispose();
        }

        if (_standby is not null)
        {
            _standby.ValueChanged -= OnCom1Changed;
            _standby.Dispose();
        }

        if (_radio is not null)
        {
            _radio.ValueChanged -= OnCom1Changed;
            _radio.Dispose();
        }

        _tuneGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<RadioTuneOutcome> TuneCom1Async(int khz, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (!RadioFrequency.InBand(khz))
        {
            return Record(RadioTuneOutcome.OutOfBand, khz, 0, reason);
        }

        // ProSim pushes the radio when either the R_ pair or the radio's own frequency is live
        // (2026-11-16: the R_ pair read 0 with the radio on 124.850 — a tune must still work).
        if (_active is null || _standby is null || !Reading().Live)
        {
            return Record(RadioTuneOutcome.NotConnected, khz, 0, reason);
        }

        await _tuneGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fromKhz = Reading().ActiveKhz;
            if (fromKhz == khz)
            {
                return Record(RadioTuneOutcome.Tuned, khz, fromKhz, reason);
            }

            var standbyOk = await WriteVerify(_standby, khz, cancellationToken).ConfigureAwait(false)
                || await WriteVerify(_standby, khz, cancellationToken).ConfigureAwait(false);
            if (!standbyOk)
            {
                return Record(RadioTuneOutcome.Failed, khz, fromKhz, reason);
            }

            // ARCHAEOLOGY (owner's rig, 2026-11-16 01:09Z, EGLL): R_COM1_ACTIVE keeps the last
            // value software wrote. It held 121705 from a tile tune at 00:24Z; the pilot then
            // turned the radio to 119.000 by hand (N_FREQ_COM1 = 119000, R_COM1_ACTIVE still
            // 121705). The FO's tune back to 121.705 wrote 121705 over 121705 — no change for
            // ProSim, so the radio stayed and the tune ended Unverified ("Unable to tune COM1").
            // The tune at 00:24Z had worked because the input went 0 -> 121705. So the input is
            // first set to the frequency the radio is on, and only then to the new one: the
            // second write is always a change.
            if (Khz(_active) == khz)
            {
                await _dataRefs.WriteAsync(ProsimDataRefNames.RadioCom1Active.Name, fromKhz, cancellationToken).ConfigureAwait(false);
                await Task.Delay(VerifyPause, cancellationToken).ConfigureAwait(false);
            }

            var tuned = await SwapAndVerifyAsync(khz, fromKhz, cancellationToken).ConfigureAwait(false);
            if (!tuned)
            {
                // One more try the same way: input to the radio's own frequency, then the swap.
                await _dataRefs.WriteAsync(ProsimDataRefNames.RadioCom1Active.Name, Reading().ActiveKhz, cancellationToken).ConfigureAwait(false);
                await Task.Delay(VerifyPause, cancellationToken).ConfigureAwait(false);
                tuned = await SwapAndVerifyAsync(khz, fromKhz, cancellationToken).ConfigureAwait(false);
            }

            Publish();
            return Record(tuned ? RadioTuneOutcome.Tuned : RadioTuneOutcome.Unverified, khz, fromKhz, reason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "COM1 tune to {Khz} kHz failed", khz);
            return Record(RadioTuneOutcome.Failed, khz, 0, reason);
        }
        finally
        {
            _tuneGate.Release();
        }
    }

    /// <summary>The swap: the new frequency to the active input, what was active to standby (so
    /// the pilot can swap straight back), then the reading that follows the real radio
    /// (N_FREQ_COM1 first) must show the new frequency.</summary>
    private async Task<bool> SwapAndVerifyAsync(int khz, int fromKhz, CancellationToken cancellationToken)
    {
        await _dataRefs.WriteAsync(ProsimDataRefNames.RadioCom1Active.Name, khz, cancellationToken).ConfigureAwait(false);
        await _dataRefs.WriteAsync(ProsimDataRefNames.RadioCom1Standby.Name, fromKhz, cancellationToken).ConfigureAwait(false);
        for (var read = 0; read < SwapVerifyReads; read++)
        {
            await Task.Delay(VerifyPause, cancellationToken).ConfigureAwait(false);
            if (Reading().ActiveKhz == khz)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> WriteVerify(IDataRefSubscription<double> dataref, int khz, CancellationToken cancellationToken)
    {
        await _dataRefs.WriteAsync(dataref.Name, khz, cancellationToken).ConfigureAwait(false);
        await Task.Delay(VerifyPause, cancellationToken).ConfigureAwait(false);
        return Khz(dataref) == khz;
    }

    private RadioTuneOutcome Record(RadioTuneOutcome outcome, int khz, int fromKhz, string reason)
    {
        _logger.LogInformation("Radios tile: COM1 tune to {Khz} kHz ({Reason}) -> {Outcome}", khz, reason, outcome);
        _eventLog.Record("radio.tile-tune", new { khz, fromKhz, reason, outcome = outcome.ToString() });
        return outcome;
    }

    private void OnCom1Changed(object? sender, EventArgs e) => Publish();

    private void Publish()
    {
        var reading = Reading();
        var standby = _standby;
        var standbyKhz = standby is null ? 0 : Khz(standby);
        _store.Update(s => s with { Com1ActiveKhz = reading.ActiveKhz, Com1StandbyKhz = standbyKhz, Com1Live = reading.Live });
    }

    private Com1Reading Reading()
    {
        var active = _active;
        var standby = _standby;
        var radio = _radio;
        return Com1Reading.From(
            inputsLive: active is not null && standby is not null && IsLive(active) && IsLive(standby),
            inputActiveKhz: active is null ? 0 : Khz(active),
            radioLive: radio is not null && IsLive(radio),
            radioKhz: radio is null ? 0 : Khz(radio));
    }

    private static bool IsLive(IDataRefSubscription<double> dataref) => dataref is { IsStale: false, RawValue: not null };

    private static int Khz(IDataRefSubscription<double> dataref) => (int)Math.Round(dataref.Value);

    /// <inheritdoc />
    public int? ActiveKhz => Reading() is { Live: true } reading ? reading.ActiveKhz : null;
}

/// <summary>
/// COM1 active as the app reads it — the one rule. Two recordings on the owner's rig,
/// 2026-11-16 (EGLL): at the gate <c>R_COM1_ACTIVE</c> read 0 while the radio was on 124.850
/// (<c>N_FREQ_COM1</c> = 124850); later, after the app had written 121705 through the Radios
/// tile, the pilot turned the hardware knob to 119.000 and <c>N_FREQ_COM1</c> read 119000 while
/// <c>R_COM1_ACTIVE</c> still read 121705 — the app saw a match with the assigned Ground
/// frequency and the FO said nothing. So the R_ input is the last value software wrote (or 0),
/// not the radio; <c>N_FREQ_COM1</c> is the radio and follows the knob and a software write
/// alike. The radio's own frequency counts when it is live and above 0; the R_ input is only
/// the fallback. Pure for tests.
/// </summary>
/// <param name="ActiveKhz">COM1 active in kHz; 0 when neither source has a frequency.</param>
/// <param name="Live">ProSim is pushing COM1 (the radio's own frequency, or the R_ pair).</param>
/// <param name="FromRadio">The value came from <c>N_FREQ_COM1</c>, not from the R_ input.</param>
public readonly record struct Com1Reading(int ActiveKhz, bool Live, bool FromRadio)
{
    public static Com1Reading From(bool inputsLive, int inputActiveKhz, bool radioLive, int radioKhz)
    {
        if (radioLive && radioKhz > 0)
        {
            return new Com1Reading(radioKhz, true, true);
        }

        if (inputsLive && inputActiveKhz > 0)
        {
            return new Com1Reading(inputActiveKhz, true, false);
        }

        return new Com1Reading(0, inputsLive || radioLive, false);
    }
}
