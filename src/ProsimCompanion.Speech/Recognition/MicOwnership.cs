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
    private readonly IRecognitionWindow _window;
    private readonly ILogger<MicOwnership> _logger;
    private readonly object _gate = new();

    private BorrowScope? _active;

    public MicOwnership(IRecognitionWindow window, ILogger<MicOwnership> logger)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(logger);

        _window = window;
        _logger = logger;
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
        try
        {
            _window.OpenListeningWindow(grammar);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                return await heard.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null; // timeout — the dialogue speaks its fallback line
            }
        }
        finally
        {
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
