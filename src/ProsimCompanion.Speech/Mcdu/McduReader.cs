using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>Read side of the MCDU features: the parsed CDU2 page and whether the display is
/// currently being received at all. The actuation flows compose on top of this.</summary>
public interface IMcduReader
{
    /// <summary>Parses the current CDU2 page from the cached display value
    /// (<see cref="McduPage.Empty"/> when disconnected or unavailable).</summary>
    McduPage Read();

    /// <summary>True while ProSim is pushing display values (a value has arrived and the
    /// connection has not dropped since) — the "can I even see the box" gate every MCDU
    /// feature checks before speaking or pressing anything.</summary>
    bool IsDisplayAvailable { get; }
}

/// <summary>
/// Read-only MCDU reader (Prosim2FO semantics): on "read the MCDU" the FO reads back what is
/// on the FO's CDU2 screen; "read the scratchpad" reads just the scratchpad. No writes — this
/// is the safe foundation the actuation flows build on. Numbers and identifiers are read
/// deterministically (never persona-styled). Degrades to a spoken explanation when ProSim is
/// absent or the display dataref carries nothing.
/// </summary>
public sealed class McduReader : IMcduReader, IVoiceFeature, IDisposable
{
    private static readonly string[] ReadPhrases =
    [
        "read the mcdu", "read the m c d u", "read the box", "read me the mcdu",
        "what's on the mcdu", "read the fms",
    ];

    private static readonly string[] ScratchpadPhrases =
        ["read the scratchpad", "read the scratch pad", "what's in the scratchpad"];

    // Matching is against normalized text ("what's" → "what s"), so normalize once here.
    private static readonly HashSet<string> ReadNormalized =
        [.. ReadPhrases.Select(CommandMatcher.Normalize)];
    private static readonly HashSet<string> ScratchpadNormalized =
        [.. ScratchpadPhrases.Select(CommandMatcher.Normalize)];

    private readonly IProsimDataRefs _dataRefs;
    private readonly IOptionsMonitor<McduOptions> _options;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<McduReader> _logger;
    private readonly object _gate = new();

    private IDataRefSubscription? _display;

    public McduReader(
        IProsimDataRefs dataRefs,
        IOptionsMonitor<McduOptions> options,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<McduReader> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _options = options;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public IEnumerable<string> Phrases => [.. ReadPhrases, .. ScratchpadPhrases];

    public bool ValueParse => false;

    public bool IsDisplayAvailable
    {
        get
        {
            var display = Display();
            return display.RawValue is not null && !display.IsStale;
        }
    }

    public McduPage Read()
    {
        // Cached-subscription read (registered once; never a per-read network round-trip).
        // ARCHAEOLOGY (Prosim2FO, 2025, empirical): ProSim's aircraft.mcdu2.display
        // double-buffers — after the F/O page changes it TEARS between the new and previous
        // page until a key event flushes it, so consecutive reads can strictly alternate even
        // though the real gauge is steady. Callers must de-flicker: the actuator's
        // WaitSettledAsync waits for two identical consecutive reads, and its own keypress is
        // what flushes the buffer.
        try
        {
            return McduDisplayParser.Parse(Display().GetValue<string?>(null));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MCDU display read failed");
            return McduPage.Empty;
        }
    }

    public bool TryHandle(string utterance)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return false;
        }

        var text = CommandMatcher.Normalize(utterance);
        var scratchpadOnly = ScratchpadNormalized.Contains(text);
        if (!scratchpadOnly && !ReadNormalized.Contains(text))
        {
            return false;
        }

        // Degraded mode: explain instead of going silent when ProSim isn't feeding us.
        if (!IsDisplayAvailable)
        {
            Speak("Unable — I can't see the M C D U; ProSim isn't connected.");
            _eventLog.Record("mcdu.read.unavailable", new { scope = scratchpadOnly ? "scratchpad" : "page" });
            return true;
        }

        var page = Read();
        if (scratchpadOnly)
        {
            var scratch = McduDisplayParser.Speakable(page.Scratchpad);
            Speak(scratch.Length == 0 ? "The scratchpad is empty." : $"Scratchpad, {scratch}.");
            _eventLog.Record("mcdu.read", new { scope = "scratchpad", page.Title });
        }
        else
        {
            Speak(BuildReadback(page));
            _eventLog.Record("mcdu.read", new { scope = "page", page.Title, tmpy = page.IsTemporary });
        }

        return true;
    }

    /// <summary>Deterministic spoken form of a page: title, the meaningful data lines, then
    /// the scratchpad; prefixed with a temporary-plan note when one is staged. Pure — the
    /// testable core of the read-back.</summary>
    public static string BuildReadback(McduPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (!page.HasData)
        {
            return "The M C D U is blank or unavailable.";
        }

        var parts = new List<string>();
        if (page.IsTemporary)
        {
            parts.Add("Temporary flight plan");
        }

        var title = McduDisplayParser.Collapse(page.Title);
        if (title.Length > 0)
        {
            parts.Add(title);
        }

        foreach (var row in page.Rows)
        {
            var spoken = McduDisplayParser.Speakable(row.Data);
            if (spoken.Length > 0)
            {
                parts.Add(spoken);
            }
        }

        var scratch = McduDisplayParser.Speakable(page.Scratchpad);
        if (scratch.Length > 0)
        {
            parts.Add($"scratchpad, {scratch}");
        }

        return string.Join(". ", parts) + ".";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _display?.Dispose();
            _display = null;
        }
    }

    private IDataRefSubscription Display()
    {
        lock (_gate)
        {
            // Frequent (250 ms) so the actuator's two-identical-reads settle check converges
            // inside its timeout. Registered once and cached — never re-read per call.
            return _display ??= _dataRefs.Subscribe(McduControls.Display, DataRefTier.Frequent);
        }
    }

    private void Speak(string text)
    {
        _logger.LogInformation("MCDU read-back: {Text}", text);
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text, SpeechPriority.Normal, Ttl: TimeSpan.FromMinutes(1), Tag: "mcdu"));
    }
}
