using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.App.Hosting;

/// <summary>One damage-related SimVar as served over the debug API. <see cref="Received"/>
/// distinguishes "the sim pushed this value" from the catalog fallback — essential here
/// because the WEAR AND TEAR family does not exist on MSFS 2020 and a fallback 1.0 must not
/// read as a live "tyres healthy".</summary>
public sealed record SimDamageEntry(
    string Key,
    string SimVar,
    double Value,
    bool Received,
    bool Stale,
    DateTimeOffset? LastUpdatedUtc);

/// <summary>Answer to <c>GET /api/debug/damage</c>.</summary>
public sealed record SimDamageResponse(
    bool MsfsConnected,
    string Note,
    IReadOnlyList<SimDamageEntry> Values);

/// <summary>
/// Holds the airframe-damage SimVar subscriptions behind the debug endpoint. Subscribes
/// lazily on the first snapshot rather than at startup: users who never open the debug
/// surface should not pay the 2024-only registrations (which MSFS 2020 refuses with a logged
/// SimConnect exception on every connect). Once opened, the handles stay for the app's
/// lifetime so later polls read warm cached values.
/// </summary>
public sealed class SimDamageProbe : IDisposable
{
    private readonly ISimVars _simVars;
    private readonly object _gate = new();
    private List<DamageVar>? _subscriptions;

    /// <summary>One subscribed variable plus its read policy: <see cref="Read"/> goes through
    /// the typed handle so a value that never arrived shows the catalog's benign fallback
    /// (tyre wear 1.0 = healthy) — never a raw-coercion 0 that would read as "failed".</summary>
    private sealed record DamageVar(string Key, IDataRefSubscription Handle, Func<double> Read);

    public SimDamageProbe(ISimVars simVars)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        _simVars = simVars;
    }

    /// <summary>Current cached values, subscribing on first use. The first call therefore
    /// usually reports received=false everywhere — the data arrives within the Infrequent
    /// cadence and the next poll reads it.</summary>
    public IReadOnlyList<SimDamageEntry> Snapshot()
    {
        lock (_gate)
        {
            _subscriptions ??=
            [
                Flag("gearDamageBySpeed", _simVars.Subscribe(ProsimDataRefNames.SimVars.GearDamageBySpeed)),
                Flag("gearSpeedExceeded", _simVars.Subscribe(ProsimDataRefNames.SimVars.GearSpeedExceeded)),
                Flag("flapDamageBySpeed", _simVars.Subscribe(ProsimDataRefNames.SimVars.FlapDamageBySpeed)),
                Flag("flapSpeedExceeded", _simVars.Subscribe(ProsimDataRefNames.SimVars.FlapSpeedExceeded)),
                Level("tireWear", _simVars.Subscribe(ProsimDataRefNames.SimVars.TireWearLevel)),
                Flag("tireFailed", _simVars.Subscribe(ProsimDataRefNames.SimVars.TireFailed)),
                Level("tirePressureWear", _simVars.Subscribe(ProsimDataRefNames.SimVars.TirePressureWearLevel)),
                Level("exposedPartsWear", _simVars.Subscribe(ProsimDataRefNames.SimVars.ExposedPartsWearLevel)),
                Level("exposedPartsLowestWear", _simVars.Subscribe(ProsimDataRefNames.SimVars.ExposedPartsLowestWearLevel)),
            ];

            return [.. _subscriptions.Select(entry => ToEntry(entry.Key, entry.Handle, entry.Read()))];
        }
    }

    private static DamageVar Flag(string key, IDataRefSubscription<bool> handle)
        => new(key, handle, () => handle.Value ? 1.0 : 0.0);

    private static DamageVar Level(string key, IDataRefSubscription<double> handle)
        => new(key, handle, () => handle.Value);

    /// <summary>Pure view assembly — exposed for tests. Value is always a double (Bool vars
    /// read 0/1) so the wire shape stays uniform for curl/browser reading.</summary>
    public static SimDamageEntry ToEntry(string key, IDataRefSubscription subscription, double value)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new SimDamageEntry(
            key,
            subscription.Name,
            value,
            Received: subscription.RawValue is not null,
            Stale: subscription.IsStale,
            subscription.LastUpdatedUtc);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_subscriptions is null)
            {
                return;
            }

            foreach (var entry in _subscriptions)
            {
                entry.Handle.Dispose();
            }
            _subscriptions = null;
        }
    }
}

/// <summary>
/// Read-only <c>GET /api/debug/damage</c>: the airframe-damage SimVars (blown-tyre wear
/// state, gear/flap overspeed damage) as one JSON snapshot, so damage picked up in flight —
/// e.g. tyres shredded by broken scenery on takeoff — can be inspected over the LAN without
/// sim-side tooling. Deliberately API-only (no web page). Rides the telemetry API's gate and
/// auth: <c>telemetryApi.enabled</c> answers 404 while off, loopback passes without a token
/// by default, LAN callers need the web access token.
/// </summary>
public static class SimDamageEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Served with every payload — the semantics are not guessable from numbers
    /// alone, and this surface is read raw in a browser, not through a UI.</summary>
    public const string Note =
        "Read-only. Wear levels: 1 = undamaged, 0 = failed. WEAR AND TEAR vars are MSFS 2024 "
        + "only; received=false means the sim never pushed the value and the shown value is "
        + "the benign fallback. The first request starts the subscriptions — poll again after "
        + "a few seconds.";

    public static void MapSimDamageApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/debug/damage", GetDamage);
    }

    private static IResult GetDamage(
        HttpContext context,
        SimDamageProbe probe,
        ConnectionStatusStore status,
        IOptionsMonitor<TelemetryApiOptions> apiOptions,
        IOptionsMonitor<WebUiOptions> webUiOptions)
    {
        // The sibling-API gate order: a disabled surface 404s before any auth probing.
        if (!apiOptions.CurrentValue.Enabled)
        {
            return Results.NotFound();
        }

        if (!ApiTokenAuth.IsAuthorized(
                context, apiOptions.CurrentValue.RequireTokenOnLoopback, webUiOptions.CurrentValue))
        {
            return Results.Json(
                new { reason = "Access token required (Authorization: Bearer <token>, or the web UI cookie)." },
                Json,
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Json(BuildResponse(IsMsfsConnected(status.Snapshot()), probe.Snapshot()), Json);
    }

    /// <summary>Pure over the store snapshot and probe values — exposed for tests.</summary>
    public static SimDamageResponse BuildResponse(bool msfsConnected, IReadOnlyList<SimDamageEntry> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new SimDamageResponse(msfsConnected, Note, values);
    }

    private static bool IsMsfsConnected(IReadOnlyList<KeyValuePair<string, ConnectionState>> states)
        => states.Any(pair =>
            string.Equals(pair.Key, Subsystems.SimConnect, StringComparison.OrdinalIgnoreCase)
            && pair.Value == ConnectionState.Connected);
}
