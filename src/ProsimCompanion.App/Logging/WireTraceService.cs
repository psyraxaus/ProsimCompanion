using System.IO;
using ProsimCompanion.Core.Logging;
using Serilog;

namespace ProsimCompanion.App.Logging;

/// <summary>
/// Writes raw protocol frames to a dedicated CMTrace-format rolling file
/// (<c>ProsimCompanion-wire-*.log</c>), gated by the live wire-trace flag. Kept separate from
/// the main log so protocol dumps never drown normal diagnostics.
/// </summary>
public sealed class WireTraceService : IWireTrace, IDisposable
{
    private readonly LoggingLevels _levels;
    private readonly Serilog.Core.Logger _logger;

    public WireTraceService(LoggingLevels levels, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        _levels = levels;
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                new CmTraceTextFormatter(),
                Path.Combine(logDirectory, "ProsimCompanion-wire-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();
    }

    /// <inheritdoc />
    public bool Enabled => _levels.WireTraceEnabled;

    /// <inheritdoc />
    public void Trace(string channel, string direction, string payload)
    {
        if (!Enabled)
        {
            return;
        }

        _logger
            .ForContext("SourceContext", $"Wire.{channel}")
            .Information("{Direction} {Payload}", direction, payload);
    }

    public void Dispose() => _logger.Dispose();
}
