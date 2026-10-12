using System.Net;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Network;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// What shape a settings text field expects. <see cref="TextField"/> checks its value against
/// the kind on every keystroke and shows the mismatch under the field (owner request
/// 2026-10-13). Plain <see cref="Text"/> is never checked.
/// </summary>
public enum TextFieldKind
{
    /// <summary>Anything goes.</summary>
    Text,

    /// <summary>A machine name or IP address with no scheme, port or path — the consumers
    /// build the URL themselves (<c>http://{host}:5000/graphql</c>), so a pasted
    /// <c>http://…</c> or <c>host:5000</c> produces a URL that throws.</summary>
    Host,

    /// <summary>An absolute http(s) URL. Consumers string-concatenate a path onto it.</summary>
    Url,

    /// <summary>An IPv4 address (the Wake-on-LAN broadcast target).</summary>
    IpAddress,

    /// <summary>A MAC address: six hex pairs with ':' or '-' separators.</summary>
    Mac,

    /// <summary>A file that should already exist on this PC (a DLL, a certificate, a key).
    /// A missing file is a warning, not a block — it may be installed later.</summary>
    FilePath,

    /// <summary>A folder that should already exist on this PC. Relative paths resolve under
    /// the install folder. Missing is a warning.</summary>
    FolderPath,

    /// <summary>A file or a folder that should already exist (the DFD setting takes
    /// either). Missing is a warning.</summary>
    FileOrFolderPath,

    /// <summary>A file the app writes (day state, tech log, logbook). Only its parent
    /// folder needs to exist; a missing folder is a warning.</summary>
    OutputFilePath,

    /// <summary>Folders separated by ';'. Each missing one is a warning.</summary>
    FolderList,

    /// <summary>A four-character ICAO airport code.</summary>
    Icao,

    /// <summary>A runway designator: 01–36 with an optional L/R/C.</summary>
    Runway,

    /// <summary>Positive whole numbers separated by commas.</summary>
    IntList,
}

/// <summary>One finding about a field's value. <see cref="Blocking"/> findings disable Save
/// (the value can never work); the rest are advisory (a path that is not there yet).</summary>
public sealed record FieldProblem(string Message, bool Blocking);

/// <summary>
/// The pure rules behind <see cref="TextFieldKind"/>. Empty is never a problem — every page
/// hint already says what empty means — and the messages name the expected shape with an
/// example, so the line under the field is the "what do I type here" the owner asked for.
/// Path kinds touch the file system (Exists only, after the invalid-character gate; Snyk
/// path rule) because the web server runs on the PC the paths refer to.
/// </summary>
public static partial class FieldValidation
{
    private const string HostMessage = "Name or IP address only. No http:// and no port. Example: 192.168.1.10";
    private const string HostIpv6Message = "IPv6 addresses are not supported here. Use the PC's name or its IPv4 address. Example: 192.168.1.10";
    private const string UrlMessage = "Must start with http:// or https://. Example: http://192.168.1.50:8000";
    private const string IpMessage = "An IPv4 address like 192.168.1.255";
    private const string MacMessage = "Six hex pairs like AA:BB:CC:DD:EE:FF";
    private const string BadPathMessage = "Not a valid Windows path";
    private const string IcaoMessage = "A four-letter ICAO code like YSSY";
    private const string RunwayMessage = "A runway like 16R or 34";
    private const string IntListMessage = "Whole numbers separated by commas, like 1, 2, 3";

    [GeneratedRegex("^[A-Z0-9]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex IcaoPattern();

    [GeneratedRegex("^(0?[1-9]|[12][0-9]|3[0-6])[LRC]?$", RegexOptions.IgnoreCase)]
    private static partial Regex RunwayPattern();

    /// <summary>Returns the problem with <paramref name="value"/> for <paramref name="kind"/>,
    /// or null when it is empty or fine.</summary>
    public static FieldProblem? Check(TextFieldKind kind, string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0 || kind == TextFieldKind.Text)
        {
            return null;
        }

        return kind switch
        {
            TextFieldKind.Host => CheckHost(text),
            TextFieldKind.Url => CheckUrl(text),
            TextFieldKind.IpAddress => CheckIp(text),
            TextFieldKind.Mac => WakeOnLan.TryParseMac(text, out _) ? null : Block(MacMessage),
            TextFieldKind.FilePath => CheckPath(text, PathTarget.File),
            TextFieldKind.FolderPath => CheckPath(text, PathTarget.Folder),
            TextFieldKind.FileOrFolderPath => CheckPath(text, PathTarget.FileOrFolder),
            TextFieldKind.OutputFilePath => CheckPath(text, PathTarget.ParentFolder),
            TextFieldKind.FolderList => CheckFolderList(text),
            TextFieldKind.Icao => IcaoPattern().IsMatch(text) ? null : Block(IcaoMessage),
            TextFieldKind.Runway => RunwayPattern().IsMatch(text) ? null : Block(RunwayMessage),
            TextFieldKind.IntList => CheckIntList(text),
            _ => null,
        };
    }

    private static FieldProblem Block(string message) => new(message, Blocking: true);

    private static FieldProblem Warn(string message) => new(message, Blocking: false);

    private static FieldProblem? CheckHost(string text)
    {
        if (IPAddress.TryParse(text, out var address))
        {
            // An IPv6 literal would be pasted unbracketed into "http://{host}:5000" by the
            // consumers and throw — the very trap this kind exists to close (review 2026-10-13).
            return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? null
                : Block(HostIpv6Message);
        }

        // A scheme, a path, a port or whitespace: the consumers add their own.
        if (text.Contains("://", StringComparison.Ordinal) || text.Contains('/') || text.Contains('\\')
            || text.Contains(':') || text.Any(char.IsWhiteSpace))
        {
            return Block(HostMessage);
        }

        return Uri.CheckHostName(text) == UriHostNameType.Dns ? null : Block(HostMessage);
    }

    private static FieldProblem? CheckUrl(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
           && !string.IsNullOrEmpty(uri.Host)
            ? null
            : Block(UrlMessage);

    // TryParse accepts the legacy shorthand "192.168.1" (= 192.168.0.1); a broadcast address
    // typed that way is a typo, so four dotted parts are required.
    private static FieldProblem? CheckIp(string text)
        => text.Count(c => c == '.') == 3
           && IPAddress.TryParse(text, out var ip)
           && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? null
            : Block(IpMessage);

    private enum PathTarget { File, Folder, FileOrFolder, ParentFolder }

    private static FieldProblem? CheckPath(string text, PathTarget target)
    {
        // Explorer's "Copy as path" wraps the path in quotes; the pages strip them on save.
        var path = text.Trim('"');
        if (path.Length == 0)
        {
            return null;
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return Block(BadPathMessage);
        }

        string full;
        try
        {
            full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(path, AppContext.BaseDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Block(BadPathMessage);
        }

        try
        {
            return target switch
            {
                PathTarget.File => File.Exists(full) ? null : Warn("File not found on this PC"),
                PathTarget.Folder => Directory.Exists(full) ? null : Warn("Folder not found on this PC"),
                PathTarget.FileOrFolder => File.Exists(full) || Directory.Exists(full) ? null : Warn("Not found on this PC"),
                PathTarget.ParentFolder => Directory.Exists(Path.GetDirectoryName(full) ?? full)
                    ? null
                    : Warn("Its folder does not exist on this PC"),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Warn("Cannot read that path from this PC");
        }
    }

    private static FieldProblem? CheckFolderList(string text)
    {
        var missing = new List<string>();
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var problem = CheckPath(entry, PathTarget.Folder);
            if (problem is { Blocking: true })
            {
                return problem;
            }

            if (problem is not null)
            {
                missing.Add(entry.Trim('"'));
            }
        }

        return missing.Count == 0 ? null : Warn("Folder not found on this PC: " + string.Join("; ", missing));
    }

    private static FieldProblem? CheckIntList(string text)
    {
        foreach (var part in text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
            {
                return Block(IntListMessage);
            }
        }

        return null;
    }
}
