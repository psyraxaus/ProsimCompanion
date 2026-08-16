using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.AircraftState;

/// <summary>
/// Loads an <see cref="AircraftStateDefinition"/> from the user config tree. Failures degrade:
/// a missing file simply means "no check" (the assessor reports Skipped), and a malformed file
/// is surfaced through <see cref="ConfigProblemStore"/> — a warning-only log line has already
/// cost a flight test its edited checklist (issue #74), so parse problems must reach the web UI.
/// </summary>
public static class AircraftStateDefinitionLoader
{
    /// <summary>Loads <paramref name="path"/>, or null when absent/unreadable/empty. Clears
    /// the aircraft-states problem area first so a fixed file drops off the banner.</summary>
    public static AircraftStateDefinition? TryLoad(
        string path, ILogger logger, ConfigProblemStore? problems = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        problems?.ClearArea(ConfigAreas.AircraftStates);

        if (!File.Exists(path))
        {
            logger.LogInformation(
                "No aircraft state definition at {Path} — the session-start check will be skipped", path);
            return null;
        }

        var fileName = Path.GetFileName(path);
        try
        {
            var definition = JsonSerializer.Deserialize<AircraftStateDefinition>(
                File.ReadAllText(path), ChecklistDefinition.JsonOptions);
            if (definition is null || !definition.AllItems().Any(item => item.Verify is not null))
            {
                var message = "The definition contains no items with a 'verify' condition — nothing to check.";
                logger.LogWarning("Aircraft state definition {File}: {Message}", fileName, message);
                problems?.Report(ConfigAreas.AircraftStates, fileName, message);
                return null;
            }

            logger.LogInformation(
                "Aircraft state definition '{Name}' loaded: {Items} expectation(s) in {Groups} group(s)",
                definition.Name, definition.AllItems().Count(item => item.Verify is not null),
                definition.Groups.Count);
            return definition;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // JSON parser messages carry line numbers — keep them for the banner.
            logger.LogWarning(ex, "Aircraft state definition {File} failed to load", fileName);
            problems?.Report(ConfigAreas.AircraftStates, fileName, ex.Message);
            return null;
        }
    }
}
