namespace ProsimCompanion.Core.Theming;

/// <summary>
/// The user's airline logos, one file per theme, under the user config tree
/// (<c>%LOCALAPPDATA%\ProsimCompanion\config\themes\logos\&lt;slug&gt;.&lt;ext&gt;</c>).
/// Owner decision 2026-09-20: real airline marks are trademarks, so the app never ships one —
/// the pilot uploads their own from the Appearance page and the header shows it. The seed
/// folder <c>{app}\config</c> must never hold a logo (the installer build fails if it does).
/// </summary>
public sealed class ThemeLogoStore
{
    /// <summary>Raster and vector formats a browser renders in an <c>&lt;img&gt;</c>.</summary>
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".svg", ".jpg", ".jpeg", ".webp" };

    /// <summary>A header logo is a small mark; anything bigger is a mistake (a wallpaper) and
    /// would slow every page load on a tablet.</summary>
    public const long MaxBytes = 512 * 1024;

    private readonly object _gate = new();

    public ThemeLogoStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
    }

    /// <summary>Where the files live. Created on the first save.</summary>
    public string Directory { get; }

    /// <summary>Bumped on every save/remove — the pages append it to the image URL so a
    /// replaced logo is never served from the browser cache.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after a save or remove, on the caller's thread.</summary>
    public event Action? Changed;

    /// <summary>Stable file-name form of a theme name: lower-case, runs of anything but a-z /
    /// 0-9 collapse to one dash ("KLM Royal Dutch" → "klm-royal-dutch").</summary>
    public static string Slug(string themeName)
    {
        ArgumentNullException.ThrowIfNull(themeName);
        var chars = new List<char>(themeName.Length);
        var pendingDash = false;
        foreach (var ch in themeName.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingDash && chars.Count > 0)
                {
                    chars.Add('-');
                }

                pendingDash = false;
                chars.Add(ch);
            }
            else
            {
                pendingDash = true;
            }
        }

        return chars.Count == 0 ? "theme" : new string([.. chars]);
    }

    /// <summary>The MIME type a file's extension implies (the endpoint's Content-Type).</summary>
    public static string ContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };

    /// <summary>Full path of the theme's logo, or null when none is uploaded.</summary>
    public string? FindFile(string themeName) => FindFileBySlug(Slug(themeName));

    /// <summary>Every slug that has a logo file, sorted — only files with an allowed
    /// extension count, so a stray file in the folder is invisible.</summary>
    public IReadOnlyList<string> ListSlugs()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return [.. System.IO.Directory.EnumerateFiles(Directory)
            .Where(path => AllowedExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null && name == Slug(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>Full path of the logo stored under <paramref name="slug"/>, or null. Only
    /// files with an allowed extension count, so a stray file in the folder is invisible.</summary>
    public string? FindFileBySlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (slug != Slug(slug) || !System.IO.Directory.Exists(Directory))
        {
            return null; // not a slug we would ever write — never let a path fragment through
        }

        foreach (var extension in AllowedExtensions)
        {
            var candidate = Path.Combine(Directory, slug + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Stores <paramref name="content"/> as the theme's logo, replacing any previous
    /// file for that theme (whatever its extension). Written to a temp file first so a failed
    /// upload never leaves a torn logo behind.</summary>
    /// <exception cref="ArgumentException">The file's extension is not an image the header can show.</exception>
    /// <exception cref="InvalidOperationException">The file is larger than <see cref="MaxBytes"/>.</exception>
    public async Task<string> SaveAsync(string themeName, string originalFileName, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var extension = Path.GetExtension(originalFileName ?? "").ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException(
                $"'{Path.GetFileName(originalFileName)}' is not a PNG, SVG, JPG or WEBP file.", nameof(originalFileName));
        }

        System.IO.Directory.CreateDirectory(Directory);
        var slug = Slug(themeName);
        var target = Path.Combine(Directory, slug + extension);
        var temp = Path.Combine(Directory, $"{slug}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        throw new InvalidOperationException(
                            $"The logo is larger than {MaxBytes / 1024} KB — export a smaller header-sized mark.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            lock (_gate)
            {
                RemoveFilesLocked(slug);
                File.Move(temp, target, overwrite: true);
                Version++;
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        Changed?.Invoke();
        return target;
    }

    /// <summary>Deletes the theme's logo. True when a file was removed.</summary>
    public bool Remove(string themeName)
    {
        bool removed;
        lock (_gate)
        {
            removed = RemoveFilesLocked(Slug(themeName));
            if (removed)
            {
                Version++;
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    private bool RemoveFilesLocked(string slug)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return false;
        }

        var removed = false;
        foreach (var extension in AllowedExtensions)
        {
            var candidate = Path.Combine(Directory, slug + extension);
            if (File.Exists(candidate))
            {
                File.Delete(candidate);
                removed = true;
            }
        }

        return removed;
    }
}
