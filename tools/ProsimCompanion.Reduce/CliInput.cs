namespace ProsimCompanion.Reduce;

/// <summary>
/// The command line is outside input: on the support host the pipeline builds it from what a
/// user sent. A path from it is used only after it is made absolute and found below an allowed
/// folder, and text from it reaches the console only as one line — so an argument can neither
/// point the tool at an arbitrary file nor write a false line into the pipeline's log.
/// </summary>
public static class CliInput
{
    /// <summary>The absolute path, or null when it is not below the working folder or the
    /// system temp folder (where the pipeline and the tests keep their files).</summary>
    public static string? ResolvePath(string path) => ResolvePath(path, DefaultRoots());

    /// <summary>The absolute path, or null when it is below none of <paramref name="allowedRoots"/>
    /// (a root itself counts as below). Exposed for tests.</summary>
    public static string? ResolvePath(string path, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        return Locate(path, allowedRoots)?.Full;
    }

    /// <summary>The existing file or folder the path names, or null when it is refused or not
    /// there.</summary>
    public static string? FindExisting(string path) => FindExisting(path, DefaultRoots());

    /// <summary>
    /// The existing file or folder the path names, or null. The answer is never the text of the
    /// argument: it is built from the allowed root down, one real directory entry at a time,
    /// and the argument only chooses which entry to take at each level. A directory listing
    /// has no ".." entry, so the result cannot leave the root, and nothing the caller typed
    /// reaches a file API or a log line (code-scanner rule, 2026-10-04: an argument that flows
    /// into a file open is a path-traversal alert whatever check ran before it).
    /// </summary>
    public static string? FindExisting(string path, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        if (Locate(path, allowedRoots) is not { } located)
        {
            return null;
        }

        var comparison = PathComparison();
        var current = located.Root;
        var remainder = located.Full.Length > located.Root.Length ? located.Full[located.Root.Length..] : string.Empty;
        foreach (var segment in remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            string? next = null;
            try
            {
                if (Directory.Exists(current))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        if (string.Equals(Path.GetFileName(entry), segment, comparison))
                        {
                            next = entry;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (next is null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Where an output file goes: its folder must exist below an allowed root; the
    /// file itself may be new. Null when the folder is refused or not there.</summary>
    public static string? OutputFile(string path) => OutputFile(path, DefaultRoots());

    /// <inheritdoc cref="OutputFile(string)"/>
    public static string? OutputFile(string path, IReadOnlyList<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        if (Locate(path, allowedRoots) is not { } located
            || Path.GetDirectoryName(located.Full) is not { Length: > 0 } parent
            || Path.GetFileName(located.Full) is not { Length: > 0 } file)
        {
            return null;
        }

        return FindExisting(parent, allowedRoots) is { } folder && Directory.Exists(folder)
            ? Path.Combine(folder, file)
            : null;
    }

    /// <summary>The text with every carriage return and line feed removed.</summary>
    public static string SingleLine(string? text)
        => (text ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

    private static string[] DefaultRoots() => [Environment.CurrentDirectory, Path.GetTempPath()];

    // Windows paths differ only by case for the same folder; Linux paths do not.
    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>The absolute path and the allowed root it is below (no trailing separator).</summary>
    private static (string Full, string Root)? Locate(string path, IReadOnlyList<string> allowedRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var comparison = PathComparison();
        var candidate = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
        foreach (var allowed in allowedRoots)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowed));
            if (candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            {
                return (full, root);
            }
        }

        return null;
    }
}
