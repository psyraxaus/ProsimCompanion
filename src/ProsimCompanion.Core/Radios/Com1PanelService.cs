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
/// </summary>
public sealed class Com1PanelService : IStartupModule, IRadioTuner, IDisposable
{
    private static readonly TimeSpan VerifyPause = TimeSpan.FromMilliseconds(250);

    private readonly IProsimDataRefs _dataRefs;
    private readonly RadioStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<Com1PanelService> _logger;
    private readonly SemaphoreSlim _tuneGate = new(1, 1);
    private IDataRefSubscription<double>? _active;
    private IDataRefSubscription<double>? _standby;

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
        _active.ValueChanged += OnCom1Changed;
        _standby.ValueChanged += OnCom1Changed;
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

        if (_active is null || _standby is null || !IsLive(_active) || !IsLive(_standby))
        {
            return Record(RadioTuneOutcome.NotConnected, khz, 0, reason);
        }

        await _tuneGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fromKhz = Khz(_active);
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

            // The swap: what was active goes to standby, so the pilot can swap straight back.
            await _dataRefs.WriteAsync(ProsimDataRefNames.RadioCom1Active.Name, khz, cancellationToken).ConfigureAwait(false);
            await _dataRefs.WriteAsync(ProsimDataRefNames.RadioCom1Standby.Name, fromKhz, cancellationToken).ConfigureAwait(false);
            await Task.Delay(VerifyPause, cancellationToken).ConfigureAwait(false);
            Publish();
            return Record(Khz(_active) == khz ? RadioTuneOutcome.Tuned : RadioTuneOutcome.Unverified, khz, fromKhz, reason);
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
        var active = _active;
        var standby = _standby;
        var live = active is not null && standby is not null && IsLive(active) && IsLive(standby);
        var activeKhz = active is null ? 0 : Khz(active);
        var standbyKhz = standby is null ? 0 : Khz(standby);
        _store.Update(s => s with { Com1ActiveKhz = activeKhz, Com1StandbyKhz = standbyKhz, Com1Live = live });
    }

    private static bool IsLive(IDataRefSubscription<double> dataref) => dataref is { IsStale: false, RawValue: not null };

    private static int Khz(IDataRefSubscription<double> dataref) => (int)Math.Round(dataref.Value);
}
