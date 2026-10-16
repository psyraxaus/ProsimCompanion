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
    private readonly WireFrameDeduper _deduper = new();
    private bool _wasEnabled;

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
                // 50 MB per file, three files kept (2026-10-17): the 2026-10-10 trace was
                // 49 MB in one day, mostly GSX repeating its startup event — folded by the
                // deduper now, but a trace left on can still never fill the disk.
                fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 3)
            .CreateLogger();
    }

    /// <inheritdoc />
    public bool Enabled => _levels.WireTraceEnabled;

    /// <inheritdoc />
    public void Trace(string channel, string direction, string payload)
    {
        if (!Enabled)
        {
            _wasEnabled = false;
            return;
        }

        if (!_wasEnabled)
        {
            // A capture that was just switched on starts with a written frame, whatever
            // the previous capture ended on.
            _wasEnabled = true;
            _deduper.Reset();
        }

        if (!_deduper.Accept(channel, direction, payload, out var suppressed))
        {
            return;
        }

        var logger = _logger.ForContext("SourceContext", $"Wire.{channel}");
        if (suppressed > 0)
        {
            logger.Information("{Direction} (previous frame repeated {Count} more times)", direction, suppressed);
        }

        logger.Information("{Direction} {Payload}", direction, payload);
    }

    public void Dispose() => _logger.Dispose();
}
