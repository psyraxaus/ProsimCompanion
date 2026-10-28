using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Core.Display;

/// <summary>What <see cref="CamerasCfgEditor.Apply"/> produced.</summary>
/// <param name="Content">The new file text (unchanged when nothing qualified).</param>
/// <param name="BlocksChanged">How many cockpit camera blocks got a new zoom. Zero means the
/// file holds no saved pilot view yet — the page must not report success.</param>
public sealed record CamerasCfgEdit(string Content, int BlocksChanged);

/// <summary>
/// Surgical edits to an MSFS 2024 custom-camera <c>cameras.cfg</c> — the file the sim writes
/// under <c>LocalCache\SimObjects\Airplanes\&lt;aircraft&gt;\…</c> when the pilot saves a view
/// (Ctrl+F10). Only the <c>InitialZoom=</c> line, and on request the pitch of <c>InitialPbh=</c>,
/// of the <c>[CAMERADEFINITION.N]</c> blocks that are cockpit pilot views change; every other
/// byte, the line endings and a BOM survive. The write helper refuses any path that is not
/// under a LocalCache <c>SimObjects\Airplanes</c> folder, so a package's own file under
/// <c>Official\</c> or <c>Community\</c>, and ChasePlane's work folder, can never be touched.
/// Design: <c>claude/fov-tool-design.md</c>.
/// </summary>
public static partial class CamerasCfgEditor
{
    /// <summary>The <c>SubCategoryItem</c> values of the views a saved pilot view can be.</summary>
    public static readonly IReadOnlyList<string> PilotViewItems = ["VFRPilot", "IFRPilot", "LandingPilot", "ClosePilot"];

    /// <summary>The number of backups kept beside the file.</summary>
    public const int BackupsKept = 5;

    private const string FileName = "cameras.cfg";
    private const string BackupPrefix = FileName + ".bak-";
    private const char Bom = '﻿';

    // UTF-8 without a BOM of its own: the BOM, when the file has one, is kept as the first
    // character of the text so it round-trips byte for byte.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Rewrites the zoom (and optionally the pitch) of every qualifying cockpit
    /// camera block. A block qualifies when its <c>Category</c> is <c>Cockpit</c> and its
    /// <c>SubCategoryItem</c> is one of <see cref="PilotViewItems"/> — quotes optional, case
    /// ignored, spaces around <c>=</c> tolerated, in any key order.</summary>
    /// <param name="content">The file text, as <see cref="ReadRaw"/> returns it.</param>
    /// <param name="initialZoom">The new <c>InitialZoom</c>, written with up to four decimals.</param>
    /// <param name="pitchDegrees">When given, the new first component of <c>InitialPbh</c>;
    /// the bank and heading components keep their text and separators.</param>
    public static CamerasCfgEdit Apply(string content, double initialZoom, double? pitchDegrees)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!double.IsFinite(initialZoom) || initialZoom <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialZoom), "InitialZoom must be a positive number.");
        }

        var lines = SplitLines(content);
        var qualifying = QualifyingBlocks(lines);
        if (qualifying.Count == 0)
        {
            return new CamerasCfgEdit(content, 0);
        }

        var zoomText = initialZoom.ToString("0.####", CultureInfo.InvariantCulture);
        var pitchText = pitchDegrees?.ToString("0.####", CultureInfo.InvariantCulture);
        var changed = 0;
        var output = new StringBuilder(content.Length + 16);
        foreach (var (index, line) in lines.Index())
        {
            var text = line.Text;
            if (qualifying.TryGetValue(index, out var isHeader) && !isHeader)
            {
                var zoom = ZoomLine().Match(text);
                if (zoom.Success)
                {
                    text = zoom.Groups["lead"].Value + zoomText + zoom.Groups["rest"].Value;
                    changed++;
                }
                else if (pitchText is not null)
                {
                    var pbh = PbhLine().Match(text);
                    if (pbh.Success)
                    {
                        text = pbh.Groups["lead"].Value + pitchText + pbh.Groups["rest"].Value;
                    }
                }
            }

            output.Append(text).Append(line.Ending);
        }

        return new CamerasCfgEdit(output.ToString(), changed);
    }

    /// <summary>Every <c>cameras.cfg</c> under the LocalCache's <c>SimObjects\Airplanes</c>,
    /// newest first. With an aircraft hint (the sim's aircraft title) the search is narrowed
    /// to the aircraft folders whose name and the hint contain one another, case ignored;
    /// when no folder matches, every aircraft is listed so the pilot can pick.</summary>
    public static IReadOnlyList<string> FindCandidateFiles(string localCacheRoot, string? aircraftFolderHint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localCacheRoot);
        var airplanes = Path.Combine(localCacheRoot, "SimObjects", "Airplanes");
        if (!SafeDirectoryExists(airplanes))
        {
            return [];
        }

        try
        {
            var folders = Directory.EnumerateDirectories(airplanes).ToList();
            var hint = aircraftFolderHint?.Trim();
            if (!string.IsNullOrEmpty(hint))
            {
                var matching = folders.Where(f => FolderMatches(Path.GetFileName(f), hint)).ToList();
                if (matching.Count > 0)
                {
                    folders = matching;
                }
            }

            return folders
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cfg", SearchOption.AllDirectories))
                .Where(file => string.Equals(Path.GetFileName(file), FileName, StringComparison.OrdinalIgnoreCase))
                .Select(file => (Path: file, Written: SafeLastWrite(file)))
                .OrderByDescending(f => f.Written)
                .Select(f => f.Path)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>True when the path is one this feature may write: a <c>cameras.cfg</c> under a
    /// <c>LocalCache\SimObjects\Airplanes</c> folder, and never under <c>Official</c>,
    /// <c>Community</c> or ChasePlane's work folder.</summary>
    public static bool IsWritablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(full), FileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = full.Replace('/', '\\');
        return normalized.Contains(@"\LocalCache\SimObjects\Airplanes\", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains(@"\Official\", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains(@"\Community\", StringComparison.OrdinalIgnoreCase)
               && !normalized.Contains("p42-util-chaseplane", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The file's text with its BOM, if any, kept as the first character (a plain
    /// <c>File.ReadAllText</c> would strip it and the rewrite would drop it).</summary>
    public static string ReadRaw(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Utf8.GetString(File.ReadAllBytes(path));
    }

    /// <summary>Backs the file up beside itself as <c>cameras.cfg.bak-yyyyMMdd-HHmmss</c>,
    /// prunes older backups beyond <see cref="BackupsKept"/>, and replaces the file through a
    /// temporary file so a crash mid-write leaves the original intact.</summary>
    /// <returns>The backup's full path.</returns>
    /// <exception cref="InvalidOperationException">The path is not one this feature may write.</exception>
    public static string WriteWithBackup(string path, string newContent, DateTime? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(newContent);
        if (!IsWritablePath(path))
        {
            throw new InvalidOperationException(
                $"Refusing to write '{path}': only a cameras.cfg under the MSFS LocalCache SimObjects\\Airplanes folder may be edited.");
        }

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        var stamp = (timestamp ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var backup = Path.Combine(folder, BackupPrefix + stamp);
        File.Copy(full, backup, overwrite: true);
        PruneBackups(folder);

        var temp = full + ".tmp";
        File.WriteAllBytes(temp, Utf8.GetBytes(newContent));
        File.Move(temp, full, overwrite: true);
        return backup;
    }

    private static void PruneBackups(string folder)
    {
        var stale = Directory.EnumerateFiles(folder, BackupPrefix + "*")
            .Where(f => Path.GetFileName(f).StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Skip(BackupsKept);
        foreach (var file in stale)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A backup that will not go is not worth failing the write for.
            }
        }
    }

    /// <summary>Line index → true for the header line, false for a body line, of every
    /// qualifying block. Two passes because the keys that qualify a block may follow the
    /// zoom line.</summary>
    private static Dictionary<int, bool> QualifyingBlocks(IReadOnlyList<Line> lines)
    {
        var result = new Dictionary<int, bool>();
        var blockStart = -1;
        string? category = null;
        string? item = null;

        for (var i = 0; i <= lines.Count; i++)
        {
            var isEnd = i == lines.Count;
            var text = isEnd ? "" : lines[i].Text;
            var header = !isEnd && SectionHeader().IsMatch(text);
            if (isEnd || header)
            {
                if (blockStart >= 0 && Qualifies(category, item))
                {
                    result[blockStart] = true;
                    for (var j = blockStart + 1; j < i; j++)
                    {
                        result[j] = false;
                    }
                }

                blockStart = header && CameraHeader().IsMatch(text) ? i : -1;
                category = null;
                item = null;
                continue;
            }

            if (blockStart < 0)
            {
                continue;
            }

            var pair = KeyValue().Match(text);
            if (!pair.Success)
            {
                continue;
            }

            var name = pair.Groups["name"].Value;
            var value = Unquote(pair.Groups["value"].Value);
            if (name.Equals("Category", StringComparison.OrdinalIgnoreCase))
            {
                category = value;
            }
            else if (name.Equals("SubCategoryItem", StringComparison.OrdinalIgnoreCase))
            {
                item = value;
            }
        }

        return result;
    }

    private static bool Qualifies(string? category, string? item)
        => string.Equals(category, "Cockpit", StringComparison.OrdinalIgnoreCase)
           && item is not null
           && PilotViewItems.Contains(item, StringComparer.OrdinalIgnoreCase);

    private static string Unquote(string value)
    {
        var text = value.Trim();
        var comment = text.IndexOfAny([';', '/']);
        if (comment > 0 && (text[comment] == ';' || (comment + 1 < text.Length && text[comment + 1] == '/')))
        {
            text = text[..comment].Trim();
        }

        return text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
    }

    private static bool FolderMatches(string folderName, string hint)
        => folderName.Contains(hint, StringComparison.OrdinalIgnoreCase)
           || hint.Contains(folderName, StringComparison.OrdinalIgnoreCase);

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static DateTime SafeLastWrite(string file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>One line and the exact ending that followed it ("" for the last line without one).</summary>
    private readonly record struct Line(string Text, string Ending);

    private static List<Line> SplitLines(string content)
    {
        var lines = new List<Line>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\r')
            {
                var crlf = i + 1 < content.Length && content[i + 1] == '\n';
                lines.Add(new Line(content[start..i], crlf ? "\r\n" : "\r"));
                if (crlf)
                {
                    i++;
                }

                start = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(new Line(content[start..i], "\n"));
                start = i + 1;
            }
        }

        if (start < content.Length)
        {
            lines.Add(new Line(content[start..], ""));
        }

        return lines;
    }

    [GeneratedRegex(@"^\s*﻿?\s*\[[^\]]*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"^\s*﻿?\s*\[\s*CAMERADEFINITION\.\d+\s*\]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CameraHeader();

    [GeneratedRegex(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"^(?<lead>\s*InitialZoom\s*=\s*""?)(?<value>[-+0-9.eE]+)(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZoomLine();

    [GeneratedRegex(@"^(?<lead>\s*InitialPbh\s*=\s*""?)(?<value>[-+0-9.eE]+)(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PbhLine();
}
