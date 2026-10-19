using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.Core.State;

/// <summary>
/// The one clock behind every captain's question (issue #166): every 15 s it asks each store
/// to settle a question whose TTL passed — a yes/no question counts as no (source
/// <c>timeout</c>), a pick-one question is withdrawn. This is the ONLY path that settles a
/// question on silence; the FO's listen window hands the decision back and leaves the
/// question open. Before C2 the de-icing policy service kept this timer for its own question
/// alone (2026-10-11); the other three questions had none. Startup module: the host calls
/// <see cref="Start"/>; <see cref="Sweep"/> is public for tests.
/// </summary>
public sealed class CaptainsQuestionTtlSweeper : IStartupModule, IDisposable
{
    /// <summary>The de-icing service's cadence, kept: a 5-minute TTL settled within 15 s of
    /// passing is what the probes and the Status board ("counts as no at HH:mm") assume.</summary>
    public static TimeSpan Cadence { get; } = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<ICaptainsQuestionStore> _stores;
    private readonly ILogger<CaptainsQuestionTtlSweeper> _logger;
    private readonly Timer _timer;

    public CaptainsQuestionTtlSweeper(IEnumerable<ICaptainsQuestionStore> stores, ILogger<CaptainsQuestionTtlSweeper> logger)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(logger);
        _stores = [.. stores];
        _logger = logger;
        _timer = new Timer(_ => Sweep(DateTimeOffset.UtcNow), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start() => _timer.Change(Cadence, Cadence);

    public void Dispose() => _timer.Dispose();

    /// <summary>One pass over every store. Each store is swept behind its own guard — a
    /// subscriber that throws on the Answered edge must not stop the other questions' clocks.</summary>
    public void Sweep(DateTimeOffset nowUtc)
    {
        foreach (var store in _stores)
        {
            try
            {
                var outcome = store.ExpireOverdue(nowUtc);
                if (outcome != CaptainsQuestionExpiry.Never)
                {
                    _logger.LogInformation(
                        "Captain's question {Topic} expired unanswered — {Outcome}",
                        store.Definition.Topic,
                        outcome == CaptainsQuestionExpiry.CountsAsNo ? "counts as no (source timeout)" : "withdrawn; the raiser's default decides");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Captain's question {Topic} TTL sweep failed", store.Definition.Topic);
            }
        }
    }
}
