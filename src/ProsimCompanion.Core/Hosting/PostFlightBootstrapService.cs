using Microsoft.Extensions.Hosting;
using ProsimCompanion.Core.Logbook;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.TechLog;

namespace ProsimCompanion.Core.Hosting;

/// <summary>
/// Activates the post-flight bookkeeping pillar (tech log, logbook, session finalizer) at
/// startup. A separate hosted service — rather than more constructor parameters on
/// <see cref="CoreBootstrapService"/> — so the pillar stays one registration line and the app
/// still starts cleanly in builds that omit it (degrade, not fail).
/// </summary>
public sealed class PostFlightBootstrapService : IHostedService
{
    private readonly TechLogService _techLog;
    private readonly LogbookService _logbook;
    private readonly SessionFinalizer _finalizer;

    public PostFlightBootstrapService(
        TechLogService techLog,
        LogbookService logbook,
        SessionFinalizer finalizer)
    {
        ArgumentNullException.ThrowIfNull(techLog);
        ArgumentNullException.ThrowIfNull(logbook);
        ArgumentNullException.ThrowIfNull(finalizer);

        _techLog = techLog;
        _logbook = logbook;
        _finalizer = finalizer;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Stores load before the finalizer subscribes, so a Shutdown edge racing startup can
        // never fold into an unloaded store.
        _techLog.Start();
        _logbook.Start();
        _finalizer.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _finalizer.Dispose();
        _techLog.Dispose();
        return Task.CompletedTask;
    }
}
