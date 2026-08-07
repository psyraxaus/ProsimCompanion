using Microsoft.Extensions.Options;
using Moq;

namespace ProsimCompanion.Core.Tests.TechLog;

/// <summary>Fixed-now clock so due-date math is deterministic in tests.</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class OptionsSupport
{
    /// <summary>An options monitor over a live instance — tests mutate the instance to
    /// simulate a hot-reload.</summary>
    public static IOptionsMonitor<T> Monitor<T>(T value)
        where T : class
    {
        var monitor = new Mock<IOptionsMonitor<T>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => value);
        return monitor.Object;
    }
}
