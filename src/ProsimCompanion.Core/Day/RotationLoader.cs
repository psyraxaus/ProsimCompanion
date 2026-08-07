using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProsimCompanion.Core.Day;

/// <summary>
/// Loads the planned rotation when a day starts: the first <c>*.json</c> (ordinal file-name
/// order) in the rotations folder that parses and has at least one leg. The predecessor took
/// the first file blindly and silently fell back to progressive mode when it happened to be
/// empty or malformed; skipping to the next candidate fixes that. Never throws — any failure
/// means "no plan" (progressive mode).
/// </summary>
public static class RotationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Resolves the folder (blank → "rotations"; relative → against
    /// <see cref="AppContext.BaseDirectory"/>) and returns the first usable rotation, or null.</summary>
    public static RotationFile? TryLoad(string? rotationsFolder)
    {
        try
        {
            var folder = string.IsNullOrWhiteSpace(rotationsFolder) ? "rotations" : rotationsFolder!;
            if (!Path.IsPathRooted(folder))
            {
                folder = Path.Combine(AppContext.BaseDirectory, folder);
            }

            if (!Directory.Exists(folder))
            {
                return null;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                try
                {
                    var rotation = JsonSerializer.Deserialize<RotationFile>(File.ReadAllText(file), JsonOptions);
                    if (rotation is { Legs.Count: > 0 })
                    {
                        return rotation;
                    }
                }
                catch (JsonException)
                {
                    // Malformed candidate — try the next file rather than losing the plan.
                }
                catch (IOException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Folder unreadable — progressive mode is the safe degrade.
        }

        return null;
    }
}
