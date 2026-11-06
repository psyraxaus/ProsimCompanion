using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ProSimSDK;
using ProsimCompanion.Core.Scenarios;

namespace ProsimCompanion.Prosim.Failures;

/// <summary>
/// <see cref="IFailureInjector"/> over the ProSim SDK's failure objects (issue #197, ADR-0018):
/// <c>getFailures()</c> for the live list, <c>ArmedFailure.Create</c> to arm (immediately, or
/// on the SDK's own after-event condition for a takeoff scenario), <c>ArmedFailure.Reset()</c>
/// to clear, <c>getArmedFailures()</c> for what is armed. Every call runs on the live session
/// through <see cref="Sdk.SdkConnection.RunAsync"/>; no session = unavailable, never a throw
/// out. Handles are this process's own ids — a rebuilt session invalidates the SDK objects
/// behind them, so a clear re-reads the armed list instead of trusting a stale object. The
/// catalogue's verified names are installed on <see cref="ProsimFailureGate"/> at start and on
/// every reload; the gate refuses everything else.
/// </summary>
public sealed class SdkFailureInjector : IFailureInjector, IDisposable
{
    private readonly ProsimConnectionService _connection;
    private readonly ScenarioCatalogueBank _catalogue;
    private readonly ILogger<SdkFailureInjector> _logger;
    // Untyped on purpose: a field of an SDK type would load ProSimSDK.dll when this class
    // loads — i.e. when the container builds, with or without ProSim installed (the
    // composition test has no dll). SDK types appear only inside the call lambdas, which are
    // compiled when first invoked, on a live session.
    private readonly ConcurrentDictionary<string, object> _ours = new(StringComparer.Ordinal);
    private volatile bool _available;
    private bool _unavailableLogged;
    private bool _sdkMismatchLogged;

    public SdkFailureInjector(ProsimConnectionService connection, ScenarioCatalogueBank catalogue, ILogger<SdkFailureInjector> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(logger);
        _connection = connection;
        _catalogue = catalogue;
        _logger = logger;
    }

    public bool IsAvailable => _available;

    public void Dispose() => _ours.Clear();

    /// <summary>Installs the catalogue's verified names on the gate (called before every arm
    /// and by the service at start, so a reloaded catalogue is honoured).</summary>
    public void RefreshGate()
        => ProsimFailureGate.InstallCatalogue(_catalogue.Entries.Where(e => e.Verified).Select(e => e.FailureName));

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        var failures = await RunAsync(c => c.getFailures()?.Select(f => f.name ?? "").Where(n => n.Length > 0).ToList() ?? [], cancellationToken)
            .ConfigureAwait(false);
        return failures ?? [];
    }

    public async Task<string?> ArmAsync(string failureName, FailureArmCondition? condition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureName);
        RefreshGate();
        var name = failureName.Trim();
        var handle = Guid.NewGuid().ToString("N");
        var armed = await RunAsync(connection =>
        {
            var live = connection.getFailures()?.ToList() ?? [];
            var liveNames = live.Select(f => f.name ?? "").ToHashSet(StringComparer.Ordinal);
            ProsimFailureGate.EnsureAllowed(name, liveNames);
            var failure = live.First(f => string.Equals(f.name, name, StringComparison.Ordinal));
            return condition is null
                ? ArmedFailure.Create(failure)
                : ArmedFailure.Create(Condition(condition), failure);
        }, cancellationToken).ConfigureAwait(false);

        if (armed is null)
        {
            return null;
        }

        _ours[handle] = armed;
        _logger.LogInformation("ProSim failure armed: {Failure} ({Condition})", name, condition is null ? "now" : condition.AfterEvent.ToString());
        return handle;
    }

    public async Task<IReadOnlyList<ArmedFailureInfo>> ArmedAsync(CancellationToken cancellationToken = default)
    {
        var list = await RunAsync(connection =>
        {
            var result = new List<ArmedFailureInfo>();
            var armed = connection.getArmedFailures();
            if (armed is null)
            {
                return result;
            }

            // "Ours" is first the very object we created; when the SDK hands out fresh
            // wrappers per call (unverified on a rig — review 2026-11-05), the failure name
            // identifies it instead, each handle matched at most once.
            var oursByName = _ours.ToDictionary(pair => pair.Key, pair => FirstName((ArmedFailure)pair.Value), StringComparer.Ordinal);
            var unmatched = new HashSet<string>(oursByName.Keys, StringComparer.Ordinal);
            var index = 0;
            foreach (var item in armed)
            {
                index++;
                var name = FirstName(item);
                var handle = _ours.FirstOrDefault(pair => ReferenceEquals(pair.Value, item)).Key;
                if (handle is null && name.Length > 0)
                {
                    handle = unmatched.FirstOrDefault(h => string.Equals(oursByName[h], name, StringComparison.Ordinal));
                    if (handle is not null)
                    {
                        _logger.LogDebug("Armed failure {Failure} matched to our handle by name (the SDK returned a fresh wrapper)", name);
                    }
                }

                if (handle is not null)
                {
                    unmatched.Remove(handle);
                }

                result.Add(new ArmedFailureInfo(handle ?? ("ios-" + index), name, item.isActive));
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);
        return list ?? [];
    }

    public async Task<bool> ClearAsync(string handle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        if (!_ours.TryGetValue(handle, out var ours))
        {
            return false;
        }

        // The handle leaves the table only once ProSim has taken the reset: removed first, a
        // failed call (session gone, SDK threw) left the failure armed in ProSim with no handle
        // to clear it by — only clear-all could reach it (review 2026-11-05).
        var cleared = await RunAsync(_ =>
        {
            ((ArmedFailure)ours).Reset();
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (cleared)
        {
            _ours.TryRemove(handle, out _);
        }

        return cleared;
    }

    public async Task<int> ClearAllAsync(CancellationToken cancellationToken = default)
    {
        var count = await RunAsync(connection =>
        {
            var armed = connection.getArmedFailures()?.ToList() ?? [];
            var n = 0;
            foreach (var item in armed)
            {
                try
                {
                    item.Reset();
                    n++;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ArmedFailure.Reset threw");
                }
            }

            return n;
        }, cancellationToken, unavailable: -1).ConfigureAwait(false);
        if (count >= 0)
        {
            _ours.Clear();
        }

        return count;
    }

    private static FailureCondition Condition(FailureArmCondition condition)
        => new()
        {
            afterEvent = condition.AfterEvent switch
            {
                FailureAfterEvent.V1 => FailureCondition.EventTypeEnum.v1,
                FailureAfterEvent.Landing => FailureCondition.EventTypeEnum.Landing,
                _ => FailureCondition.EventTypeEnum.Takeoff,
            },
            afterEventTimer = condition.DelaySeconds > 0 ? TimeSpan.FromSeconds(condition.DelaySeconds) : null,
        };

    private static string FirstName(ArmedFailure armed)
    {
        try
        {
            var failures = armed.failures;
            if (failures is null)
            {
                return "";
            }

            foreach (var failure in failures)
            {
                return failure?.name ?? "";
            }
        }
        catch (Exception ex) when (ex is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            // An SDK build without the field, or a wrapper with nothing behind it: the name is
            // simply unknown (review 2026-11-05: narrowed from everything).
        }

        return "";
    }

    /// <summary>Runs SDK work on the live session; <paramref name="unavailable"/> is what a
    /// missing session / a refused gate / a thrown SDK call returns (default: the type's default).</summary>
    private async Task<T?> RunAsync<T>(Func<ProSimConnect, T> work, CancellationToken cancellationToken, T? unavailable = default)
    {
        var session = _connection.Current;
        if (session is null || !session.IsConnected)
        {
            _available = false;
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                _logger.LogInformation("ProSim failure API unavailable — no SDK session");
            }

            return unavailable;
        }

        try
        {
            var result = await session.RunAsync(work, cancellationToken).ConfigureAwait(false);
            _available = true;
            _unavailableLogged = false;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (Sdk.SdkIncompatibleException.IsMismatch(ex))
        {
            _available = false;
            if (!_sdkMismatchLogged)
            {
                _sdkMismatchLogged = true;
                _logger.LogWarning(ex, "This ProSimSDK.dll has no failure API this app binds — scenario mode is off until the SDK matches");
            }

            return unavailable;
        }
        catch (InvalidOperationException ex)
        {
            // The gate refused, or the session dropped mid-call.
            _logger.LogWarning("ProSim failure call refused: {Reason}", ex.Message);
            return unavailable;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProSim failure API call failed");
            return unavailable;
        }
    }
}
