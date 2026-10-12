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
    /// <summary>The folder the rotations are read from: blank = the seeded per-user folder
    /// (<see cref="Configuration.UserConfigPaths.Rotations"/>); the pre-2026-10-13 default
    /// "rotations" under the exe is honoured only while that folder exists (nothing ever
    /// created it, so a stale value must not hide the seeded one); any other relative path
    /// resolves under the exe.</summary>
    public static string ResolveFolder(string? configured)
        => ResolveFolder(configured, AppContext.BaseDirectory, Configuration.UserConfigPaths.Rotations, Directory.Exists);

    /// <summary>Testable core of <see cref="ResolveFolder(string?)"/>.</summary>
    public static string ResolveFolder(string? configured, string baseDirectory, string userFolder, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(userFolder);
        ArgumentNullException.ThrowIfNull(directoryExists);
        var folder = (configured ?? "").Trim().Trim('"');
        if (folder.Length == 0)
        {
            return userFolder;
        }

        var resolved = Path.IsPathRooted(folder) ? folder : Path.Combine(baseDirectory, folder);
        return string.Equals(folder, "rotations", StringComparison.OrdinalIgnoreCase) && !directoryExists(resolved)
            ? userFolder
            : resolved;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Resolves the folder (see <see cref="ResolveFolder(string?)"/>; relative → against
    /// <see cref="AppContext.BaseDirectory"/>) and returns the first usable rotation, or null.</summary>
    public static RotationFile? TryLoad(string? rotationsFolder)
    {
        try
        {
            var folder = ResolveFolder(rotationsFolder);

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
