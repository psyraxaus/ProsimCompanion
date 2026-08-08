using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;

namespace ProsimCompanion.Speech.Mcdu;

/// <summary>
/// Low-level, verified MCDU key-press primitives for the FO's CDU2. Every press goes through
/// the fixed <see cref="McduControls"/> allow-list and is gated on the box being armed
/// (<c>mcdu.enabled</c> + <c>mcdu.allowActuation</c> + display available) — nothing presses a
/// key otherwise. The higher primitives read the display back and verify the expected change
/// (never blind-press). These are the building blocks the RAD NAV and arrival flows compose.
/// </summary>
public interface IMcduActuator
{
    /// <summary>Enabled, actuation armed, and the display arriving — required before any key
    /// is pressed.</summary>
    bool IsArmed { get; }

    /// <summary>Presses one CDU2 key by suffix (e.g. "FPLN", "LSK1R", "1", "DOT"). No
    /// verification. False when refused (not armed / bad suffix).</summary>
    Task<bool> PressKeyAsync(string suffix, CancellationToken cancellationToken = default);

    /// <summary>Presses an LSK (row 1..6, left/right). No verification — the caller reads the
    /// page back and checks the result.</summary>
    Task<bool> PressLskAsync(int row, bool right, CancellationToken cancellationToken = default);

    /// <summary>Types text into the scratchpad, then verifies the scratchpad contains it.
    /// False on refusal, an untypeable character, or a scratchpad mismatch.</summary>
    Task<bool> TypeAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Presses a function key and verifies the page title then contains
    /// <paramref name="expectTitleContains"/>.</summary>
    Task<bool> GoToPageAsync(string funcKeySuffix, string expectTitleContains, CancellationToken cancellationToken = default);

    /// <summary>Polls the display until it stops changing (or a timeout), returning the
    /// settled page.</summary>
    Task<McduPage> WaitSettledAsync(CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IMcduActuator"/> over <see cref="IProsimDataRefs"/>. Presses
/// use <see cref="IProsimDataRefs.PressMomentaryAsync"/>, which serializes all presses
/// app-wide and owns the press/hold/gap timing, so MCDU keys can never interleave with FCU
/// pulses; a small extra inter-key pause keeps typed entries humanly paced.</summary>
public sealed class McduActuator : IMcduActuator, IDisposable
{
    // ARCHAEOLOGY (Prosim2FO, 2025, empirical): the display double-buffers and can tear
    // between the previous and current page after a change (see McduReader.Read), so
    // "settled" = two consecutive identical, non-blank reads. The display subscription
    // updates at the Frequent tier (250 ms), so the settle poll runs slightly slower than
    // that and the cap allows the page a few cycles to redraw (~3.5 s, matching the
    // predecessor's proven envelope).
    private const int SettleInitialMs = 300;
    private const int SettlePollMs = 280;
    private const int SettleReads = 12;

    private const int InterKeyPauseMs = 120;

    private readonly IProsimDataRefs _dataRefs;
    private readonly IMcduReader _reader;
    private readonly IOptionsMonitor<McduOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<McduActuator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1); // one key sequence at a time

    public McduActuator(
        IProsimDataRefs dataRefs,
        IMcduReader reader,
        IOptionsMonitor<McduOptions> options,
        JsonlEventLog eventLog,
        ILogger<McduActuator> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _dataRefs = dataRefs;
        _reader = reader;
        _options = options;
        _eventLog = eventLog;
        _logger = logger;
    }

    public bool IsArmed
    {
        get
        {
            var options = _options.CurrentValue;
            return options.Enabled && options.AllowActuation && _reader.IsDisplayAvailable;
        }
    }

    public async Task<bool> PressKeyAsync(string suffix, CancellationToken cancellationToken = default)
    {
        if (!Guard(suffix) || !McduControls.IsValidSuffix(suffix))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PressRawAsync(suffix, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<bool> PressLskAsync(int row, bool right, CancellationToken cancellationToken = default)
        => row is < 1 or > 6
            ? Task.FromResult(false)
            : PressKeyAsync($"LSK{row}{(right ? "R" : "L")}", cancellationToken);

    public async Task<bool> TypeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text) || !Guard("type"))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var c in text)
            {
                var key = McduControls.KeyForChar(c);
                if (key is null)
                {
                    _logger.LogWarning("MCDU type: unsupported character '{Char}' — refusing the entry", c);
                    return false;
                }

                await PressRawAsync(key, cancellationToken).ConfigureAwait(false);
                await Task.Delay(InterKeyPauseMs, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        var page = await WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        var want = text.Trim().ToUpperInvariant().Replace(" ", "");
        var ok = page.Scratchpad.Replace(" ", "").Contains(want, StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("MCDU type {Text}: scratchpad {Scratchpad} ({Result})",
            text, page.Scratchpad, ok ? "ok" : "MISMATCH");
        _eventLog.Record("mcdu.type", new { text, scratchpad = page.Scratchpad, ok });
        return ok;
    }

    public async Task<bool> GoToPageAsync(
        string funcKeySuffix, string expectTitleContains, CancellationToken cancellationToken = default)
    {
        if (!await PressKeyAsync(funcKeySuffix, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var page = await WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        var ok = page.Title.Contains(expectTitleContains, StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            _logger.LogWarning("MCDU goto {Key}: expected title containing {Expected}, got {Actual}",
                funcKeySuffix, expectTitleContains, page.Title);
        }

        _eventLog.Record("mcdu.goto", new { key = funcKeySuffix, expect = expectTitleContains, title = page.Title, ok });
        return ok;
    }

    public async Task<McduPage> WaitSettledAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(SettleInitialMs, cancellationToken).ConfigureAwait(false); // let the press land
        string? previous = null;
        var page = McduPage.Empty;
        for (var i = 0; i < SettleReads; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = _reader.Read();
            if (page.Raw == previous && page.HasData)
            {
                return page; // two consecutive identical, non-blank reads = settled
            }

            previous = page.Raw;
            await Task.Delay(SettlePollMs, cancellationToken).ConfigureAwait(false);
        }

        return page;
    }

    public void Dispose() => _gate.Dispose();

    private bool Guard(string what)
    {
        if (IsArmed)
        {
            return true;
        }

        var options = _options.CurrentValue;
        _logger.LogInformation(
            "MCDU actuation {What} refused — not armed (enabled={Enabled}, allowActuation={Allow}, display={Display})",
            what, options.Enabled, options.AllowActuation, _reader.IsDisplayAvailable);
        return false;
    }

    private Task PressRawAsync(string suffix, CancellationToken cancellationToken)
    {
        _logger.LogDebug("MCDU key {Suffix}", suffix);
        return _dataRefs.PressMomentaryAsync(McduControls.Key(suffix), cancellationToken);
    }
}
