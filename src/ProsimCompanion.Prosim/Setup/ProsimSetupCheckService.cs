using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Gateway;
using ProsimCompanion.Core.Aircraft.Setup;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Prosim.Setup;

/// <summary>
/// The ProSim setup check (owner request 2026-10-06). Once per ProSim connection it reads the
/// IOS options this app has an opinion about (<see cref="ProsimSetupRecommendations"/>) through
/// the port-5000 gateway and publishes the comparison to <see cref="ProsimSetupStore"/>; the
/// Setup page card shows amber rows and the pilot decides. Writes happen only through
/// <see cref="ApplyRecommendedAsync"/> — a click — never from the connection event, because
/// changing a pilot's ProSim configuration behind their back is exactly what the old native
/// guard was criticised for. Every write is read back and logged.
/// </summary>
public sealed class ProsimSetupCheckService : IProsimSetupCheck, IDisposable
{
    private readonly IProsimGateway _gateway;
    private readonly ConnectionStatusStore _status;
    private readonly ProsimSetupStore _store;
    private readonly IOptionsMonitor<ProsimOptions> _prosimOptions;
    private readonly IOptionsMonitor<GsxOptions> _gsxOptions;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<ProsimSetupCheckService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _readThisConnection;

    public ProsimSetupCheckService(
        IProsimGateway gateway,
        ConnectionStatusStore status,
        ProsimSetupStore store,
        IOptionsMonitor<ProsimOptions> prosimOptions,
        IOptionsMonitor<GsxOptions> gsxOptions,
        JsonlEventLog eventLog,
        ILogger<ProsimSetupCheckService> logger)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(prosimOptions);
        ArgumentNullException.ThrowIfNull(gsxOptions);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _gateway = gateway;
        _status = status;
        _store = store;
        _prosimOptions = prosimOptions;
        _gsxOptions = gsxOptions;
        _eventLog = eventLog;
        _logger = logger;

        _status.Changed += OnStatusChanged;
    }

    public void Dispose()
    {
        _status.Changed -= OnStatusChanged;
        _gate.Dispose();
    }

    private bool ProsimConnected => _status.Snapshot()
        .FirstOrDefault(pair => pair.Key == Subsystems.Prosim).Value == ConnectionState.Connected;

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        if (!ProsimConnected)
        {
            // Re-read on the next connection: a ProSim restart may come with changed options.
            _readThisConnection = false;
            _store.Update(_ => ProsimSetupSnapshot.Empty);
            return;
        }

        if (_readThisConnection || !_prosimOptions.CurrentValue.SetupCheckEnabled)
        {
            return;
        }

        _readThisConnection = true;
        _ = ReadAfterGatewayAsync();
    }

    /// <summary>Connection-time read. ProSim raises the SDK connection before its gateway
    /// listens (issue #76), so wait for the listener like the native guard does.</summary>
    private async Task ReadAfterGatewayAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (await _gateway.IsReachableAsync().ConfigureAwait(false))
                {
                    await ReadAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }

            _logger.LogWarning("ProSim setup check skipped: the ProSim gateway (port 5000) did not answer within 60 s");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProSim setup check failed");
        }
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!ProsimConnected)
        {
            return false;
        }

        return await ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProsimSetupApplyResult> ApplyRecommendedAsync(CancellationToken cancellationToken = default)
    {
        if (!ProsimConnected)
        {
            return new ProsimSetupApplyResult([], ["ProSim is not connected"]);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Update(snapshot => snapshot with { Busy = true });
            var toWrite = _store.Snapshot().Items.Where(item => item.NeedsWrite).ToList();
            var rejected = new List<string>();

            foreach (var item in toWrite)
            {
                object value = item.IsBoolean ? bool.Parse(item.Recommended) : item.Recommended;
                var accepted = await _gateway.WriteDataRefAsync(item.DataRef, value, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "ProSim setup: {Label} ({DataRef}) {Outcome} = {Value} (was {Previous})",
                    item.Label, item.DataRef, accepted ? "written" : "REJECTED", item.Recommended, OneLine(item.Actual) ?? "unknown");
                if (!accepted)
                {
                    rejected.Add(item.Label);
                }
            }

            // Read everything back: the verdict is what ProSim now reports, not what we sent.
            var items = await ReadRowsAsync(cancellationToken).ConfigureAwait(false);
            var written = new List<string>();
            var failed = new List<string>(rejected);
            foreach (var item in toWrite)
            {
                var after = items?.FirstOrDefault(row => row.DataRef == item.DataRef);
                if (after?.Status == ProsimSetupStatus.Ok)
                {
                    written.Add(item.Label);
                }
                else if (!failed.Contains(item.Label))
                {
                    failed.Add(item.Label);
                }
            }

            var result = new ProsimSetupApplyResult(written, failed);
            _eventLog.Record("prosim-setup-apply", new
            {
                written,
                failed,
                attempted = toWrite.Select(item => new { item.DataRef, item.Recommended, previous = item.Actual }).ToList(),
            });
            _logger.LogInformation("ProSim setup apply: {Summary}", result.Summary);
            return result;
        }
        finally
        {
            _store.Update(snapshot => snapshot with { Busy = false });
            _gate.Release();
        }
    }

    private async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _store.Update(snapshot => snapshot with { Busy = true });
            return await ReadRowsAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        finally
        {
            _store.Update(snapshot => snapshot with { Busy = false });
            _gate.Release();
        }
    }

    /// <summary>Reads the version and every row, publishes them, and logs one summary line.
    /// Returns null (store untouched) when the gateway gave no version — that is a transport
    /// failure, not five missing options.</summary>
    private async Task<IReadOnlyList<ProsimSetupItem>?> ReadRowsAsync(CancellationToken cancellationToken)
    {
        var version = await _gateway.QueryDataRefAsync(ProsimSetupRecommendations.ProsimVersion, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(version))
        {
            _logger.LogWarning("ProSim setup check: the gateway gave no system.version — read skipped (gateway down?)");
            return null;
        }

        var ignore = _prosimOptions.CurrentValue.SetupCheckIgnore;
        var rows = ProsimSetupRecommendations.For(_gsxOptions.CurrentValue);
        var items = new List<ProsimSetupItem>(rows.Count);
        foreach (var row in rows)
        {
            var actual = await _gateway.QueryDataRefAsync(row.DataRef, cancellationToken).ConfigureAwait(false);
            var ignored = ignore.Contains(row.DataRef, StringComparer.OrdinalIgnoreCase);
            items.Add(ProsimSetupRecommendations.Evaluate(row, actual, ignored));
        }

        _store.Update(snapshot => snapshot with
        {
            Items = items,
            ProsimVersion = version,
            CheckedAtUtc = DateTimeOffset.UtcNow,
        });

        var mismatches = items.Where(item => item.Status == ProsimSetupStatus.Mismatch)
            .Select(item => $"{item.Label}={OneLine(item.Actual)} (want {item.Recommended})")
            .ToList();
        _logger.LogInformation(
            "ProSim setup check (ProSim {Version}): {Ok} ok, {Mismatch} to change, {NotPresent} not on this build, {Ignored} ignored{Detail}",
            OneLine(version),
            items.Count(item => item.Status == ProsimSetupStatus.Ok),
            mismatches.Count,
            items.Count(item => item.Status == ProsimSetupStatus.NotPresent),
            items.Count(item => item.Status == ProsimSetupStatus.Ignored),
            mismatches.Count == 0 ? "" : " — " + string.Join(", ", mismatches));

        _eventLog.Record("prosim-setup-check", new
        {
            prosimVersion = version,
            items = items.Select(item => new
            {
                item.DataRef,
                item.Label,
                item.Recommended,
                item.Actual,
                status = item.Status.ToString(),
            }).ToList(),
        });

        return items;
    }

    /// <summary>Gateway text goes into the log: no line breaks from the wire (log-line
    /// integrity, the Snyk rule).</summary>
    private static string? OneLine(string? text)
        => text?.Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
