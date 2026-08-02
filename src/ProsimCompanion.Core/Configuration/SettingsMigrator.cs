using System.Text.Json.Nodes;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Stamps and upgrades the user settings file across application versions. Runs at startup
/// before the configuration system reads the file.
///
/// Note the division of labour: *additive* settings changes never need migration — every option
/// has a safe default and unknown JSON content is preserved on save. Migrations exist only for
/// *breaking* changes (renames, restructures, semantic changes), applied stepwise per version —
/// the same model that served Prosim2GSX through 33 config versions.
/// </summary>
public static class SettingsMigrator
{
    /// <summary>Version written by this build. Bump only alongside a new migration step.</summary>
    public const int CurrentVersion = 2;

    private const string VersionKey = "configVersion";

    /// <summary>
    /// Upgrades the settings file to <see cref="CurrentVersion"/> if it is older. Missing or
    /// unversioned files are treated as version 0. No write occurs when already current.
    /// </summary>
    /// <returns>The version the file was at before migration.</returns>
    public static int Migrate(JsonSettingsFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var foundVersion = ReadVersion(file.Read());
        if (foundVersion >= CurrentVersion)
        {
            return foundVersion;
        }

        file.Update(root =>
        {
            var version = ReadVersion(root);

            // Stepwise migration ladder. Each step upgrades exactly one version and must be
            // safe to run on a partial file.

            if (version < 2)
            {
                MigrateDepartureServicesToSteps(root);
                version = 2;
            }

            _ = version;
            root[VersionKey] = CurrentVersion;
        });

        return foundVersion;
    }

    private static int ReadVersion(JsonObject root)
        => root[VersionKey] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;

    /// <summary>
    /// v1 → v2: <c>gsx.departureServiceOrder</c> + <c>concurrentServices</c> + <c>boardingAfter</c>
    /// become the ordered <c>gsx.departureServices</c> step list (per-service activation, the
    /// Prosim2GSX model). Order is preserved; concurrent maps to AfterCalled, sequential to
    /// AfterPrevCompleted, and Boarding always becomes AfterAllCompleted — a non-empty
    /// boardingAfter list has no exact equivalent (the old semantics "board once just these
    /// finish") and maps to the conservative board-after-all.
    /// </summary>
    private static void MigrateDepartureServicesToSteps(JsonObject root)
    {
        if (root["gsx"] is not JsonObject gsx)
        {
            return;
        }

        var order = gsx["departureServiceOrder"] as JsonArray;
        var concurrent = gsx["concurrentServices"] is JsonValue c && c.TryGetValue<bool>(out var flag) ? flag : true;

        if (gsx["departureServices"] is null && order is { Count: > 0 })
        {
            var steps = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in order)
            {
                var id = (string?)entry;
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                var activation = id.Equals("Boarding", StringComparison.OrdinalIgnoreCase)
                    ? "afterAllCompleted"
                    : concurrent ? "afterCalled" : "afterPrevCompleted";
                steps.Add(new JsonObject
                {
                    ["service"] = id,
                    ["activation"] = activation,
                    ["constraint"] = "always",
                });
            }

            gsx["departureServices"] = steps;
        }

        gsx.Remove("departureServiceOrder");
        gsx.Remove("concurrentServices");
        gsx.Remove("boardingAfter");
    }
}
