using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Radios;

/// <summary>COM radio dataref allow-list (integer kHz). HF/NAV/ADF/transponder deliberately
/// excluded. THE safety rule: standby is freely tunable; ACTIVE is only ever written during a
/// swap, with a value already vetted in standby — there is no tune-active-directly path.</summary>
public static class RadioControls
{
    public const string Com1Standby = "system.analog.R_COM1_STANDBY";
    public const string Com2Standby = "system.analog.R_COM2_STANDBY";
    public const string Com1Active = "system.analog.R_COM1_ACTIVE";
    public const string Com2Active = "system.analog.R_COM2_ACTIVE";
}

/// <summary>
/// Deterministic VHF COM frequency parser — never guesses: requires a radio context (a
/// decimal, an explicit box, or a radio verb), band 118.000–136.990, and a legal 25 kHz /
/// 8.33 kHz channel ending. Homophone-tolerant digits.
/// </summary>
public static class FrequencyParser
{
    private static readonly HashSet<int> ValidEndings =
        [0, 5, 10, 15, 25, 30, 35, 40, 50, 55, 60, 65, 75, 80, 85, 90];

    /// <summary>Parses a frequency in kHz from an utterance; null when nothing valid.</summary>
    public static int? Parse(string utterance)
    {
        ArgumentNullException.ThrowIfNull(utterance);
        if (!NumberExtractor.TryExtract(utterance, out var value))
        {
            return null;
        }

        // "121.5" → 121500; "118" alone is ambiguous without a decimal — require one, or a
        // 5/6-digit kHz form ("121500").
        int khz;
        if (value is >= 118 and < 137 && value != Math.Floor(value))
        {
            khz = (int)Math.Round(value * 1000);
        }
        else if (value is >= 118_000 and <= 136_990)
        {
            khz = (int)value;
        }
        else
        {
            return null;
        }

        if (khz is < 118_000 or > 136_990)
        {
            return null;
        }

        var ending = khz % 100;
        return ValidEndings.Contains(ending) ? khz : null;
    }
}

/// <summary>
/// Voice radio management (Prosim2FO semantics): tune standby → announce → 3 s cancel window
/// → back off if the pilot dialled the standby during the pause → write + exact read-back
/// verify with one retry → "unable" puts that box advisory-only. Swap writes
/// active←standby / standby←old-active and verifies the active landed.
/// </summary>
public sealed class RadioExecutor : IVoiceFeature, IDisposable
{
    private static readonly string[] CancelWords = ["negative", "disregard", "cancel", "belay that"];
    private static readonly TimeSpan VerifyPause = TimeSpan.FromSeconds(3);

    private readonly IProsimDataRefs _dataRefs;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<RadioExecutor> _logger;
    private readonly Dictionary<string, IDataRefSubscription> _reads = new(StringComparer.Ordinal);
    private readonly HashSet<int> _inhibitedBoxes = [];
    private readonly object _gate = new();

    private CancellationTokenSource? _pending;

    public RadioExecutor(
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<RadioExecutor> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public IEnumerable<string> Phrases =>
    [
        "set standby", "set box one standby", "set box two standby", "tune box one", "tune box two",
        "tune standby", "standby box one", "standby box two",
        "swap", "swap box one", "swap box two", "flip", "flip box one", "flip box two",
    ];

    public bool ValueParse => true;

    public bool TryHandle(string utterance)
    {
        var text = " " + CommandMatcher.Normalize(utterance) + " ";

        if (CancelWords.Contains(text.Trim()))
        {
            lock (_gate)
            {
                if (_pending is null)
                {
                    return false;
                }

                _pending.Cancel();
                _pending = null;
            }

            _ = _arbiter.SpeakAsync("Disregard.", SpeechPriority.High);
            _eventLog.Record("radio.cancelled", new { });
            return true;
        }

        var box = text.Contains(" box two ", StringComparison.Ordinal)
            || text.Contains(" two ", StringComparison.Ordinal) && text.Contains("box", StringComparison.Ordinal)
            ? 2 : 1;

        if (text.Contains(" swap", StringComparison.Ordinal) || text.Contains(" flip", StringComparison.Ordinal))
        {
            _ = SwapAsync(box);
            return true;
        }

        // A radio VERB is required — a bare "box" mention must not claim the utterance
        // ("check the box" is an MCDU command; it used to land here and answer "Say again
        // the frequency"). "set" only counts together with "box" so FCU "set heading two
        // seven zero" is never grabbed either.
        var hasRadioVerb = text.Contains(" standby ", StringComparison.Ordinal)
            || text.Contains(" tune ", StringComparison.Ordinal)
            || (text.Contains(" set ", StringComparison.Ordinal)
                && text.Contains(" box ", StringComparison.Ordinal));
        if (!hasRadioVerb)
        {
            return false;
        }

        // The box specifier must not reach the number extractor — in "set box one standby one
        // one eight decimal one zero" the first digit-word run would otherwise start (and end)
        // at the box digit.
        var frequencyText = text
            .Replace(" box one ", " box ", StringComparison.Ordinal)
            .Replace(" box two ", " box ", StringComparison.Ordinal)
            .Replace(" box 1 ", " box ", StringComparison.Ordinal)
            .Replace(" box 2 ", " box ", StringComparison.Ordinal);
        var khz = FrequencyParser.Parse(frequencyText);
        if (khz is null)
        {
            // Tagged "clarifier" so unreadable-value asks are attributable in the session
            // jsonl (issue #66 — untagged spoken lines hid a misroute for a whole flight).
            _ = _arbiter.EnqueueAsync(new SpeechRequest(
                "Say again the frequency.", SpeechPriority.Normal, Tag: "clarifier"));
            _eventLog.Record("radio.unreadable", new { text = utterance });
            return true;
        }

        _ = TuneStandbyAsync(box, khz.Value);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pending?.Cancel();
        }

        foreach (var read in _reads.Values)
        {
            read.Dispose();
        }
    }

    private async Task TuneStandbyAsync(int box, int khz)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_inhibitedBoxes.Contains(box))
            {
                _ = _arbiter.SpeakAsync("Radios are yours.", SpeechPriority.Normal);
                return;
            }

            _pending?.Cancel();
            // Captured under the lock — reading _pending afterwards could hand this task a
            // concurrent second tune's CTS and cross their cancellation semantics.
            cts = new CancellationTokenSource();
            _pending = cts;
        }

        var standbyRef = box == 2 ? RadioControls.Com2Standby : RadioControls.Com1Standby;
        var spoken = SpeakFrequency(khz);
        try
        {
            var pre = Read(standbyRef);
            _eventLog.Record("radio.announce", new { box, khz });
            await _arbiter.EnqueueAsync(new SpeechRequest(
                $"Setting {spoken} standby, VHF {(box == 2 ? "two" : "one")}.",
                SpeechPriority.High, Tag: "radio")).ConfigureAwait(false);
            await Task.Delay(VerifyPause, cts.Token).ConfigureAwait(false);

            if (Math.Abs(Read(standbyRef) - pre) > 0.5)
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    "You're on the standby — your radios.", SpeechPriority.High, Tag: "radio"))
                    .ConfigureAwait(false);
                _eventLog.Record("radio.backoff", new { box });
                return;
            }

            var ok = await WriteVerify(standbyRef, khz, cts.Token).ConfigureAwait(false)
                || await WriteVerify(standbyRef, khz, cts.Token).ConfigureAwait(false);
            if (ok)
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    $"{spoken} standby.", SpeechPriority.High, Tag: "radio")).ConfigureAwait(false);
                _eventLog.Record("radio.set", new { box, khz });
            }
            else
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    "Unable — radios are yours.", SpeechPriority.High, Tag: "radio")).ConfigureAwait(false);
                _eventLog.Record("radio.unable", new { box, khz });
                lock (_gate)
                {
                    _inhibitedBoxes.Add(box);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio tune failed");
        }
    }

    private async Task SwapAsync(int box)
    {
        lock (_gate)
        {
            // "Unable"/backoff put the whole box advisory-only — a swap moves the ACTIVE
            // frequency, so it must respect the inhibit at least as much as a standby tune.
            if (_inhibitedBoxes.Contains(box))
            {
                _ = _arbiter.SpeakAsync("Radios are yours.", SpeechPriority.Normal);
                return;
            }
        }

        var activeRef = box == 2 ? RadioControls.Com2Active : RadioControls.Com1Active;
        var standbyRef = box == 2 ? RadioControls.Com2Standby : RadioControls.Com1Standby;
        try
        {
            var active = (int)Read(activeRef);
            var standby = (int)Read(standbyRef);
            if (standby < 118_000)
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    "No standby set.", SpeechPriority.Normal, Tag: "radio")).ConfigureAwait(false);
                return;
            }

            await _dataRefs.WriteAsync(activeRef, standby).ConfigureAwait(false);
            await _dataRefs.WriteAsync(standbyRef, active).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            if ((int)Read(activeRef) == standby)
            {
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    $"{SpeakFrequency(standby)} active.", SpeechPriority.High, Tag: "radio")).ConfigureAwait(false);
                _eventLog.Record("radio.swapped", new { box, khz = standby });
            }
            else
            {
                // Silence here would leave the pilot believing the swap took.
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    $"Check VHF {(box == 2 ? "two" : "one")} — the swap may not have taken.",
                    SpeechPriority.High, Tag: "radio")).ConfigureAwait(false);
                _eventLog.Record("radio.swapUnverified", new { box, khz = standby });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Radio swap failed");
        }
    }

    private async Task<bool> WriteVerify(string dataref, int khz, CancellationToken ct)
    {
        await _dataRefs.WriteAsync(dataref, khz, ct).ConfigureAwait(false);
        await Task.Delay(200, ct).ConfigureAwait(false);
        return (int)Read(dataref) == khz;
    }

    private double Read(string dataref)
    {
        if (!_reads.TryGetValue(dataref, out var read))
        {
            read = _dataRefs.Subscribe(dataref, DataRefTier.Normal);
            _reads[dataref] = read;
        }

        return read.GetValue(0.0);
    }

    private static string SpeakFrequency(int khz)
        => Callouts.Aviation.ToDigits((khz / 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            .TrimEnd('0').TrimEnd('.'));
}
