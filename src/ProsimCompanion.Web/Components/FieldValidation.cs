using System.Net;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.Network;

namespace ProsimCompanion.Web.Components;

/// <summary>One finding about a field's value, as a pack text id (ADR-0014) plus the one
/// value the sentence names (<see cref="Detail"/>: the missing folders). The table is static,
/// so the English lives in <c>fields.json</c> and <see cref="TextField"/> resolves the id where
/// a localizer is at hand. <see cref="Blocking"/> findings disable Save (the value can never
/// work); the rest are advisory (a path that is not there yet).</summary>
public sealed record FieldProblem(string TextId, bool Blocking, string? Detail = null);

/// <summary>Everything the field needs to know about one <see cref="TextFieldKind"/>, in one
/// row: the rule, the Windows dialog Browse… opens (null for a kind that is not a path) and
/// the dialog's title. One table instead of four switches on the enum across two files, which
/// drifted apart (review 2026-10-13).</summary>
/// <param name="Check">The rule for a non-empty, trimmed value.</param>
/// <param name="Dialog">The dialog Browse… opens, or null when the kind is not a path.</param>
/// <param name="BrowseTitleTextId">The pack id of the Browse… button's tooltip; empty for a kind that is not a path.</param>
/// <param name="IsList">';'-separated folders: Browse… opens at the last one and appends.</param>
public sealed record TextFieldKindInfo(Func<string, FieldProblem?> Check, NativePickKind? Dialog, string BrowseTitleTextId, bool IsList = false)
{
    /// <summary>True for the kinds that get a Browse… button.</summary>
    public bool IsPath => Dialog is not null;
}

/// <summary>
/// The pure rules behind <see cref="TextFieldKind"/>. Empty is never a problem — every page
/// hint already says what empty means — and the messages (text ids under
/// <c>fields.validation</c> in the language packs) name the expected shape with an example,
/// so the line under the field is the "what do I type here" the owner asked for.
/// Path kinds touch the file system (Exists only, through <see cref="SafePath"/>) because
/// the web server runs on the PC the paths refer to.
/// </summary>
public static partial class FieldValidation
{
    private const string HostMessage = "fields.validation.host";
    private const string HostIpv6Message = "fields.validation.hostIpv6";
    private const string UrlMessage = "fields.validation.url";
    private const string IpMessage = "fields.validation.ip";
    private const string MacMessage = "fields.validation.mac";
    private const string BadPathMessage = "fields.validation.badPath";
    private const string IcaoMessage = "fields.validation.icao";
    private const string RunwayMessage = "fields.validation.runway";
    private const string IntListMessage = "fields.validation.intList";

    [GeneratedRegex("^[A-Z0-9]{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex IcaoPattern();

    [GeneratedRegex("^(0?[1-9]|[12][0-9]|3[0-6])[LRC]?$", RegexOptions.IgnoreCase)]
    private static partial Regex RunwayPattern();

    private const string ChooseFile = "fields.validation.chooseFile";
    private const string ChooseFolder = "fields.validation.chooseFolder";

    private static readonly IReadOnlyDictionary<TextFieldKind, TextFieldKindInfo> Kinds = new Dictionary<TextFieldKind, TextFieldKindInfo>
    {
        [TextFieldKind.Text] = new(_ => null, null, ""),
        [TextFieldKind.Host] = new(CheckHost, null, ""),
        [TextFieldKind.Url] = new(CheckUrl, null, ""),
        [TextFieldKind.IpAddress] = new(CheckIp, null, ""),
        [TextFieldKind.Mac] = new(text => WakeOnLan.TryParseMac(text, out _) ? null : Block(MacMessage), null, ""),
        [TextFieldKind.FilePath] = new(text => CheckPath(text, PathTarget.File), NativePickKind.OpenFile, ChooseFile),
        [TextFieldKind.FolderPath] = new(text => CheckPath(text, PathTarget.Folder), NativePickKind.Folder, ChooseFolder),
        [TextFieldKind.FileOrFolderPath] = new(text => CheckPath(text, PathTarget.FileOrFolder), NativePickKind.OpenFile, ChooseFile),
        [TextFieldKind.OutputFilePath] = new(text => CheckPath(text, PathTarget.ParentFolder), NativePickKind.SaveFile, "fields.validation.chooseOutputFile"),
        [TextFieldKind.FolderList] = new(CheckFolderList, NativePickKind.Folder, "fields.validation.addFolder", IsList: true),
        [TextFieldKind.Icao] = new(text => IcaoPattern().IsMatch(text) ? null : Block(IcaoMessage), null, ""),
        [TextFieldKind.Runway] = new(text => RunwayPattern().IsMatch(text) ? null : Block(RunwayMessage), null, ""),
        [TextFieldKind.IntList] = new(CheckIntList, null, ""),
    };

    /// <summary>The one row for <paramref name="kind"/>; an unknown value reads as
    /// <see cref="TextFieldKind.Text"/> (unchecked, no Browse…).</summary>
    public static TextFieldKindInfo Describe(TextFieldKind kind)
        => Kinds.TryGetValue(kind, out var info) ? info : Kinds[TextFieldKind.Text];

    /// <summary>Returns the problem with <paramref name="value"/> for <paramref name="kind"/>,
    /// or null when it is empty or fine.</summary>
    public static FieldProblem? Check(TextFieldKind kind, string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 ? null : Describe(kind).Check(text);
    }

    private static FieldProblem Block(string textId) => new(textId, Blocking: true);

    private static FieldProblem Warn(string textId, string? detail = null) => new(textId, Blocking: false, detail);

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
        var path = SafePath.Unquote(text);
        if (path.Length == 0)
        {
            return null;
        }

        var full = SafePath.TryGetFullPath(path, AppContext.BaseDirectory);
        if (full is null)
        {
            return Block(BadPathMessage);
        }

        return target switch
        {
            PathTarget.File => SafePath.FileExists(full) ? null : Warn("fields.validation.fileNotFound"),
            PathTarget.Folder => SafePath.DirectoryExists(full) ? null : Warn("fields.validation.folderNotFound"),
            PathTarget.FileOrFolder => SafePath.FileExists(full) || SafePath.DirectoryExists(full) ? null : Warn("fields.validation.notFound"),
            PathTarget.ParentFolder => SafePath.DirectoryExists(Path.GetDirectoryName(full) ?? full)
                ? null
                : Warn("fields.validation.parentFolderMissing"),
            _ => null,
        };
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
                missing.Add(SafePath.Unquote(entry));
            }
        }

        return missing.Count == 0 ? null : Warn("fields.validation.foldersNotFound", string.Join("; ", missing));
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
