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
    public const int CurrentVersion = 1;

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
            // safe to run on a partial file. Example shape for the future:
            //
            // if (version < 2)
            // {
            //     RenameSection(root, "oldName", "newName");
            //     version = 2;
            // }

            _ = version;
            root[VersionKey] = CurrentVersion;
        });

        return foundVersion;
    }

    private static int ReadVersion(JsonObject root)
        => root[VersionKey] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;
}
