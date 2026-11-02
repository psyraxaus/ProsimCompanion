using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.Tests.Diagnostics.Ecam;
using Xunit;

namespace ProsimCompanion.Core.Tests.Airports;

/// <summary>Issue #231: the suggestion loop's stored task caught only cancellation, so the
/// first other exception faulted it unobserved and the suggestion stopped for the session.
/// An iteration that throws is logged at Warning and the next one still runs; only
/// cancellation (or the timer closing) ends the loop.</summary>
public sealed class PushbackSuggestionLoopTests
{
    [Fact]
    public async Task IterationThatThrows_IsLogged_AndTheNextIterationStillRuns()
    {
        using var stopping = new CancellationTokenSource();
        var logger = new CountingLogger();
        var ticks = 0;
        var iterations = 0;

        ValueTask<bool> WaitForTick(CancellationToken token)
        {
            if (++ticks > 3)
            {
                stopping.Cancel();
                return ValueTask.FromCanceled<bool>(token);
            }

            return ValueTask.FromResult(true);
        }

        Task Iteration()
        {
            iterations++;
            return iterations == 1
                ? Task.FromException(new InvalidOperationException("gateway gone"))
                : Task.CompletedTask;
        }

        await PushbackSuggestionService.RunLoopAsync(WaitForTick, Iteration, TimeSpan.Zero, logger, stopping.Token);

        Assert.Equal(3, iterations);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("iteration failed", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDuringTheBackoff_EndsTheLoop()
    {
        using var stopping = new CancellationTokenSource();
        var logger = new CountingLogger();
        var ticks = 0;

        ValueTask<bool> WaitForTick(CancellationToken token)
        {
            ticks++;
            return ValueTask.FromResult(true);
        }

        Task Iteration()
        {
            stopping.Cancel(); // shutdown arrives while this iteration is failing
            throw new InvalidOperationException("boom");
        }

        await PushbackSuggestionService.RunLoopAsync(WaitForTick, Iteration, TimeSpan.FromMinutes(1), logger, stopping.Token);

        Assert.Equal(1, ticks);
        Assert.Equal(1, logger.Warnings);
    }

    [Fact]
    public async Task TimerClosing_EndsTheLoop_Quietly()
    {
        var logger = new CountingLogger();
        var iterations = 0;

        await PushbackSuggestionService.RunLoopAsync(
            _ => ValueTask.FromResult(false),
            () => { iterations++; return Task.CompletedTask; },
            TimeSpan.Zero, logger, CancellationToken.None);

        Assert.Equal(0, iterations);
        Assert.Equal(0, logger.Warnings);
    }
}
