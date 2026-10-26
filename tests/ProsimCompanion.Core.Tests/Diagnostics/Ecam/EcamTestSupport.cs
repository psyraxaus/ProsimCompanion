using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Diagnostics.Ecam;

namespace ProsimCompanion.Core.Tests.Diagnostics.Ecam;

/// <summary>A logger that remembers every entry at Warning or above, for "logged once" assertions.</summary>
internal sealed class CountingLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    public int Warnings => Entries.Count(e => e.Level >= LogLevel.Warning);
}

/// <summary>A collector driven by a delegate, so a test states exactly what each tick says.</summary>
internal sealed class DelegateCollector(string page, Action<DiagPageBuilder, DiagCautionSink> contribute) : IDiagnosticsCollector
{
    public string Page { get; } = page;

    public void Contribute(DiagPageBuilder page, DiagCautionSink cautions) => contribute(page, cautions);
}

internal sealed class FixedMemoSource : IDiagMemoSource
{
    public DiagMemo Read(DateTimeOffset now) => new("0.8.0-test", "qwen3", TimeSpan.FromMinutes(5), "PREFLIGHT", 0, 0, true);
}

internal static class EcamTestSupport
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 27, 6, 44, 0, TimeSpan.Zero);

    public static DiagPageBuilder Builder(string page = DiagPageIds.Voice, DiagEventLog? log = null)
        => new(page, new DiagEventSink(log ?? new DiagEventLog(), T0));
}
