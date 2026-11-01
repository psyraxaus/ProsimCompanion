using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// <see cref="IMicOwnership"/> over the shared <see cref="IRecognitionWindow"/>. Chosen seam:
/// suppression happens at the router's event handlers (they check <see cref="IsBorrowed"/>
/// before doing anything), NOT by detaching them — so the checklist engine's pending answer
/// survives a borrow untouched, and the window replay on release drops it straight back into
/// its item. Restoration lives in the scope's Dispose and is therefore unconditional; a
/// throwing dialogue still gives the mic back.
/// </summary>
public sealed class MicOwnership : IMicOwnership
{
    /// <summary>How often the listen deadline is re-checked against the FO's own speech.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(250);

    /// <summary>Longest the FO's own speech may hold a listen past its timeout. NowPlaying is
    /// cleared by the arbiter when a line ends; a line that never ends (a cancelled run, a
    /// provider that hangs) must not hold the microphone for the rest of the flight.</summary>
    internal static readonly TimeSpan MaxSpeechFreeze = TimeSpan.FromSeconds(60);

    private int _listening;

    private readonly IRecognitionWindow _window;
    private readonly ILogger<MicOwnership> _logger;
    private readonly Core.State.SpeechStatusStore? _speech;
    private readonly object _gate = new();

    private BorrowScope? _active;
    private volatile IReadOnlyList<string>? _listenGrammar;

    /// <param name="expectedReplies">Where the self-echo guard reads what the pilot is expected
    /// to say (issue #172): while <see cref="ListenAsync"/> runs, its grammar is the expected
    /// reply — the minima confirm's "confirm, two seven niner feet" (EGLL 2026-10-31) echoes the
    /// FO's own read-back by design. Null (tests) registers nothing.</param>
    /// <param name="speech">The arbiter's status (what is playing now): the listen window does
    /// not count time while the FO is talking (issue #201 — a radio read-back and a cabin
    /// question ate the minima confirm's fifteen seconds). Null (tests) counts wall time.</param>
    public MicOwnership(IRecognitionWindow window, ILogger<MicOwnership> logger, ExpectedReplyStore? expectedReplies = null, Core.State.SpeechStatusStore? speech = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(logger);

        _window = window;
        _logger = logger;
        _speech = speech;
        expectedReplies?.RegisterListenSource(() => _listenGrammar ?? []);
    }

    public event Action? Released;

    public bool IsBorrowed
    {
        get
        {
            lock (_gate)
            {
                return _active is not null;
            }
        }
    }

    /// <inheritdoc />
    public bool IsListening => Volatile.Read(ref _listening) == 1;

    public IDisposable Borrow(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        lock (_gate)
        {
            if (_active is not null)
            {
                throw new InvalidOperationException(
                    $"Microphone is already borrowed by '{_active.Owner}' — dialogue runners must serialize.");
            }

            var scope = new BorrowScope(this, owner, _window.WindowOpen, _window.CurrentGrammar);
            _active = scope;
            _logger.LogDebug("Mic borrowed by {Owner}", owner);
            return scope;
        }
    }

    public async Task<string?> ListenAsync(
        IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grammar);

        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnAccepted(object? sender, RecognizedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Text))
            {
                heard.TrySetResult(e.Text.Trim());
            }
        }

        _window.Accepted += OnAccepted;
        _listenGrammar = grammar;
        try
        {
            _window.OpenListeningWindow(grammar);
            Volatile.Write(ref _listening, 1);
            // The deadline moves on while the FO is speaking: the pilot cannot answer over
            // the FO's own voice, so that time is not the pilot's — up to a hard cap.
            var remaining = timeout;
            var last = DateTimeOffset.UtcNow;
            var hardDeadline = last + timeout + MaxSpeechFreeze;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wait = remaining < Slice ? remaining : Slice;
                if (wait <= TimeSpan.Zero)
                {
                    return null; // timeout — the dialogue speaks its fallback line
                }

                if (DateTimeOffset.UtcNow >= hardDeadline)
                {
                    _logger.LogWarning("Listening window closed by the hard cap — the FO's speech status never cleared ({Timeout} s timeout + {Cap} s)", (int)timeout.TotalSeconds, (int)MaxSpeechFreeze.TotalSeconds);
                    return null;
                }

                var done = await Task.WhenAny(heard.Task, Task.Delay(wait, cancellationToken)).ConfigureAwait(false);
                if (done == heard.Task)
                {
                    return await heard.Task.ConfigureAwait(false);
                }

                var now = DateTimeOffset.UtcNow;
                if (_speech?.Snapshot().NowPlaying is null)
                {
                    remaining -= now - last;
                }

                last = now;
            }
        }
        finally
        {
            Volatile.Write(ref _listening, 0);
            _listenGrammar = null;
            _window.Accepted -= OnAccepted;
            _window.CloseListeningWindow();
        }
    }

    private void Release(BorrowScope scope)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_active, scope))
            {
                return; // stale double-dispose — the mic already moved on
            }

            _active = null;
        }

        // Replay the pre-borrow window AFTER routing is re-enabled so its recognitions route
        // normally again. Never throws — a failed replay must not undo the release itself.
        try
        {
            if (scope.WindowWasOpen)
            {
                _window.OpenListeningWindow(scope.SavedGrammar);
            }
            else
            {
                _window.CloseListeningWindow();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restoring the listening window after {Owner}'s borrow failed", scope.Owner);
        }

        // AFTER the replay, so a subscriber re-asserting its current window (idle grammar)
        // wins over the possibly-stale captured state. Never allowed to undo the release.
        try
        {
            Released?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A mic-release subscriber threw after {Owner}'s borrow", scope.Owner);
        }

        _logger.LogDebug("Mic returned by {Owner}", scope.Owner);
    }

    private sealed class BorrowScope : IDisposable
    {
        private readonly MicOwnership _parent;
        private int _disposed;

        public BorrowScope(MicOwnership parent, string owner, bool windowWasOpen, IReadOnlyList<string> savedGrammar)
        {
            _parent = parent;
            Owner = owner;
            WindowWasOpen = windowWasOpen;
            SavedGrammar = savedGrammar;
        }

        public string Owner { get; }

        public bool WindowWasOpen { get; }

        public IReadOnlyList<string> SavedGrammar { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _parent.Release(this);
            }
        }
    }
}
