using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;

namespace ProsimCompanion.Core.Notifications;

/// <summary>What one send came to, for the Notifications page's "Send test" line and the store.</summary>
public sealed record NotificationSendResult(bool Ok, string Summary, int? StatusCode, DateTimeOffset AtUtc);

/// <summary>
/// Delivers notifications (issue #151) without ever making a raiser wait: <see cref="Enqueue"/>
/// drops the message into a bounded channel and returns; ONE background sender posts to each
/// enabled target in turn with a short timeout. A target that fails goes into a cooldown and
/// is skipped until it ends; a full queue drops the newest message. Secrets never reach the
/// log: lines carry the target NAME and the HTTP status only.
/// </summary>
public sealed class NotificationDispatcher : IDisposable
{
    public const int QueueCapacity = 32;
    public const string SentEvent = "notify.sent";
    public const string FailedEvent = "notify.failed";

    private readonly IOptionsMonitor<NotificationOptions> _options;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<NotificationDispatcher> _logger;
    private readonly HttpMessageHandler _handler;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Channel<NotificationMessage> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NotificationSendResult> _lastResult = new(StringComparer.Ordinal);
    private Task? _sender;
    private int _dropped;

    public NotificationDispatcher(
        IOptionsMonitor<NotificationOptions> options,
        JsonlEventLog eventLog,
        ILogger<NotificationDispatcher> logger,
        HttpMessageHandler? handler = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _eventLog = eventLog;
        _logger = logger;
        _handler = handler ?? new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _queue = Channel.CreateBounded<NotificationMessage>(new BoundedChannelOptions(QueueCapacity)
        {
            // Wait mode + TryWrite: a full queue makes TryWrite return FALSE at once (DropWrite
            // would return true and drop silently), so the raiser never waits AND the drop is
            // counted; the messages already queued (older milestones) still go out in order.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Messages dropped because the queue was full (tests and the status line).</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>The last outcome per target name, for the Notifications page.</summary>
    public IReadOnlyDictionary<string, NotificationSendResult> LastResults
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, NotificationSendResult>(_lastResult, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Raised after every send attempt (the page refreshes its result lines).</summary>
    public event Action? Changed;

    /// <summary>Starts the single background sender. Idempotent.</summary>
    public void Start()
    {
        _sender ??= Task.Run(() => SendLoopAsync(_stopping.Token));
    }

    /// <summary>Queues a message for every enabled target that wants it. Returns at once:
    /// true when queued, false when notifications are off, no target wants it, or the queue
    /// was full (counted in <see cref="Dropped"/>). Never throws.</summary>
    public bool Enqueue(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var options = _options.CurrentValue;
        if (!options.Enabled || !options.Targets.Any(t => NotificationMessage.Wants(t.Events, message.Event) && t.Url.Length > 0))
        {
            return false;
        }

        if (_queue.Writer.TryWrite(message))
        {
            return true;
        }

        Interlocked.Increment(ref _dropped);
        _logger.LogWarning("Notification queue full ({Capacity}) — dropped {Event}", QueueCapacity, message.EventName);
        _eventLog.Record(FailedEvent, new { @event = message.EventName, target = (string?)null, reason = "queue-full" });
        return false;
    }

    /// <summary>"Send test": one message to ONE target, now, ignoring the master switch and the
    /// cooldown (the pilot pressed the button). Returns the one-line result.</summary>
    public async Task<NotificationSendResult> SendTestAsync(NotificationTarget target, string? flightNumber, string? route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var now = _clock();
        var message = new NotificationMessage(
            FlightEvent.Test, "Test notification",
            $"ProsimCompanion can reach this target. Sent {now:HH:mm:ss} zulu.",
            flightNumber ?? "", route ?? "", now);
        var result = await SendOneAsync(target, message, cancellationToken).ConfigureAwait(false);
        Remember(target, result);
        return result;
    }

    private async Task SendLoopAsync(CancellationToken stopping)
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                var options = _options.CurrentValue;
                foreach (var target in options.Targets)
                {
                    if (!NotificationMessage.Wants(target.Events, message.Event) || target.Url.Length == 0)
                    {
                        continue;
                    }

                    if (InCooldown(target, out var until))
                    {
                        _eventLog.Record(FailedEvent, new { @event = message.EventName, target = target.Name, reason = "cooldown", until });
                        continue;
                    }

                    var result = await SendOneAsync(target, message, stopping).ConfigureAwait(false);
                    Remember(target, result);
                    if (!result.Ok)
                    {
                        lock (_gate)
                        {
                            _cooldownUntil[Key(target)] = _clock().AddSeconds(Math.Max(1, options.FailureCooldownSeconds));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            // The loop must survive everything else — a dead sender would silently end the feature.
            _logger.LogError(ex, "Notification sender stopped unexpectedly");
        }
    }

    private async Task<NotificationSendResult> SendOneAsync(NotificationTarget target, NotificationMessage message, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var started = _clock();
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return Fail(target, message, null, "the URL is not a valid http(s) address", started);
        }

        try
        {
            var wire = NotificationFormatters.Format(target.Kind, message);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new StringContent(wire.Body, Encoding.UTF8, wire.ContentType),
            };
            foreach (var (name, value) in wire.Headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            if (!string.IsNullOrWhiteSpace(target.Token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token.Trim());
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.SendTimeoutSeconds)));
            // One client per send over the shared handler: cheap, and no shared default headers
            // can ever leak one target's token to another.
            using var client = new HttpClient(_handler, disposeHandler: false);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var ms = (int)(_clock() - started).TotalMilliseconds;
                _logger.LogInformation("Notification {Event} sent to {Target}: HTTP {Status} in {Ms} ms", message.EventName, target.Name, status, ms);
                _eventLog.Record(SentEvent, new { @event = message.EventName, target = target.Name, kind = target.Kind.ToString().ToLowerInvariant(), status, ms });
                return new NotificationSendResult(true, $"Sent · {status} {response.ReasonPhrase} · {_clock():HH:mm:ss}", status, _clock());
            }

            return Fail(target, message, status, $"HTTP {status} {response.ReasonPhrase}", started);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(target, message, null, $"timed out after {options.SendTimeoutSeconds} s", started);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            // The message names the host at most (DNS / refused); never the path or query.
            return Fail(target, message, null, ex.StatusCode is { } code ? $"HTTP {(int)code}" : "could not connect", started);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Notification send to {Target} threw", target.Name);
            return Fail(target, message, null, ex.GetType().Name, started);
        }
    }

    private NotificationSendResult Fail(NotificationTarget target, NotificationMessage message, int? status, string reason, DateTimeOffset started)
    {
        var ms = (int)(_clock() - started).TotalMilliseconds;
        _logger.LogWarning("Notification {Event} to {Target} failed: {Reason} ({Ms} ms)", message.EventName, target.Name, reason, ms);
        _eventLog.Record(FailedEvent, new { @event = message.EventName, target = target.Name, kind = target.Kind.ToString().ToLowerInvariant(), status, reason, ms });
        return new NotificationSendResult(false, $"Failed · {reason} · {_clock():HH:mm:ss}", status, _clock());
    }

    private bool InCooldown(NotificationTarget target, out DateTimeOffset until)
    {
        lock (_gate)
        {
            return _cooldownUntil.TryGetValue(Key(target), out until) && until > _clock();
        }
    }

    private void Remember(NotificationTarget target, NotificationSendResult result)
    {
        lock (_gate)
        {
            _lastResult[Key(target)] = result;
            if (result.Ok)
            {
                _cooldownUntil.Remove(Key(target));
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Targets are keyed by name; two targets with one name share a result line.</summary>
    private static string Key(NotificationTarget target) => target.Name.Trim().Length > 0 ? target.Name.Trim() : target.Kind.ToString();

    public void Dispose()
    {
        _stopping.Cancel();
        _queue.Writer.TryComplete();
        _stopping.Dispose();
    }
}
