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
    public const int CurrentVersion = 3;

    private const string VersionKey = "configVersion";

    /// <summary>
    /// Upgrades the settings file to <see cref="CurrentVersion"/> if it is older. Missing or
    /// unversioned files are treated as version 0. No write occurs when already current.
    /// </summary>
    /// <returns>The version the file was at before migration.</returns>
    public static int Migrate(JsonSettingsFile file)
        => Migrate(file, Path.Combine(AppContext.BaseDirectory, LegacyRotationsFolderName), Directory.Exists);

    /// <summary>Testable core of <see cref="Migrate(JsonSettingsFile)"/>: the v3 step must
    /// know whether the pre-2026-10-13 rotations folder under the exe is really there.</summary>
    /// <param name="legacyRotationsFolder">Full path of the old "rotations" folder under the exe.</param>
    /// <param name="directoryExists">The probe for it (Directory.Exists in production).</param>
    public static int Migrate(JsonSettingsFile file, string legacyRotationsFolder, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(legacyRotationsFolder);
        ArgumentNullException.ThrowIfNull(directoryExists);

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

            if (version < 3)
            {
                MigrateRotationsFolderDefault(root, keepLegacy: directoryExists(legacyRotationsFolder));
                version = 3;
            }

            _ = version;
            root[VersionKey] = CurrentVersion;
        });

        return foundVersion;
    }

    private const string LegacyRotationsFolderName = "rotations";

    /// <summary>v2 → v3 (2026-10-13): <c>day.rotationsFolder</c> "rotations" — the old default,
    /// a folder under the exe nothing ever created — becomes "" = the per-user
    /// <c>%LOCALAPPDATA%\ProsimCompanion\config\rotations</c> the app now seeds. A custom
    /// folder is kept. So is "rotations" while that folder really exists under the exe
    /// (a pilot who made it and put plans there must not lose them to the empty seeded
    /// folder; review 2026-10-13) — <see cref="Day.RotationLoader.ResolveFolder(string?)"/>
    /// then keeps honouring it until it is gone.</summary>
    private static void MigrateRotationsFolderDefault(JsonObject root, bool keepLegacy)
    {
        if (!keepLegacy
            && root["day"] is JsonObject day
            && day["rotationsFolder"] is JsonValue value
            && value.TryGetValue<string>(out var folder)
            && string.Equals(folder.Trim().Trim('"'), LegacyRotationsFolderName, StringComparison.OrdinalIgnoreCase))
        {
            day["rotationsFolder"] = "";
        }
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
