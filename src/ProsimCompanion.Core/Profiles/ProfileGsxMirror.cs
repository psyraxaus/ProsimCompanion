using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Profiles;

/// <summary>
/// The write half of the per-profile GSX settings model (Prosim2GSX behaviour): after the GSX
/// Settings page saves, the live <c>gsx</c> section is captured into the active aircraft
/// profile's stored block, so the settings travel with the aircraft and
/// <see cref="ProfileGsxApplier"/> re-applies them on the next profile match.
/// <c>fuelFobSaved</c> is app-maintained state, not configuration — it is stripped from the
/// mirrored block here and preserved across profile switches by the applier.
/// </summary>
public sealed class ProfileGsxMirror
{
    private readonly AircraftProfileService _profiles;
    private readonly JsonSettingsFile _settings;
    private readonly ILogger<ProfileGsxMirror> _logger;

    public ProfileGsxMirror(
        AircraftProfileService profiles,
        JsonSettingsFile settings,
        ILogger<ProfileGsxMirror> logger)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _profiles = profiles;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Mirrors the settings file's current <c>gsx</c> section into the active profile's
    /// <c>gsxSettings</c> block. A no-op when no profile is active or the file has no
    /// <c>gsx</c> section yet — a profile only captures a block once one exists to capture.
    /// </summary>
    public void MirrorActiveProfile()
    {
        if (_profiles.ActiveProfile is not { } activeProfile)
        {
            return;
        }

        _settings.Update(root =>
        {
            if (root[GsxOptions.SectionName] is not JsonObject gsx)
            {
                return;
            }

            if (JsonSettingsFile.GetOrCreateSection(root, AircraftProfilesOptions.SectionName)
                    ["profiles"] is not JsonArray profileArray)
            {
                return;
            }

            foreach (var entry in profileArray.OfType<JsonObject>())
            {
                if (entry["id"]?.GetValue<string>() == activeProfile.Id)
                {
                    var mirrored = gsx.DeepClone().AsObject();
                    mirrored.Remove("fuelFobSaved");
                    entry["gsxSettings"] = mirrored;
                    _logger.LogDebug("Mirrored gsx section into profile {Profile}", activeProfile.Name);
                    break;
                }
            }
        });
    }
}
