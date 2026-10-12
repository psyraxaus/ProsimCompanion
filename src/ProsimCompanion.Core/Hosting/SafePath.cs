using System.Diagnostics.CodeAnalysis;

namespace ProsimCompanion.Core.Hosting;

/// <summary>
/// The one place a user-typed path is checked before a file API sees it (Snyk path rule):
/// strip Explorer's "Copy as path" quotes, refuse invalid characters, and never let
/// <c>Exists</c> or <c>GetFullPath</c> throw over a value from a settings field. The field
/// validation (Web) and the Browse… start-folder rule (Core) used to carry their own copies
/// of this shape; a fix in one never reached the others (review 2026-10-13).
/// </summary>
public static class SafePath
{
    /// <summary>Trims and strips the surrounding quotes Explorer's "Copy as path" adds.</summary>
    public static string Unquote(string? value) => (value ?? "").Trim().Trim('"');

    /// <summary>Non-blank and free of characters Windows refuses in a path. The gate before
    /// any file API call. Tells the compiler the path is non-null when true, so callers hand
    /// it straight to a file API without a null-forgiving bang.</summary>
    public static bool IsWellFormed([NotNullWhen(true)] string? path)
        => !string.IsNullOrWhiteSpace(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    /// <summary>The absolute form of <paramref name="path"/> (relative paths resolve under
    /// <paramref name="baseDirectory"/>, else the working directory), or null when the path
    /// is malformed — bad characters, a bad drive letter, too long.</summary>
    public static string? TryGetFullPath(string? path, string? baseDirectory = null)
    {
        if (!IsWellFormed(path))
        {
            return null;
        }

        try
        {
            return Path.IsPathRooted(path) || baseDirectory is null
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, baseDirectory);
        }
        catch (Exception ex) when (IsPathFailure(ex))
        {
            return null;
        }
    }

    /// <summary>True when the folder exists; false for a malformed, missing or unreadable one.</summary>
    public static bool DirectoryExists(string? path) => Probe(path, Directory.Exists);

    /// <summary>True when the file exists; false for a malformed, missing or unreadable one.</summary>
    public static bool FileExists(string? path) => Probe(path, File.Exists);

    private static bool Probe(string? path, Func<string, bool> exists)
    {
        if (!IsWellFormed(path))
        {
            return false;
        }

        try
        {
            return exists(path);
        }
        catch (Exception ex) when (IsPathFailure(ex))
        {
            return false;
        }
    }

    private static bool IsPathFailure(Exception ex)
        => ex is ArgumentException or NotSupportedException or PathTooLongException or IOException
            or UnauthorizedAccessException or System.Security.SecurityException;
}
