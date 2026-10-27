using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Hardware.Skalarki;

namespace ProsimCompanion.Web.Components.Diagnostics;

/// <summary>One row of the PANELS plan view: <c>bar</c> (one full-width board), <c>row</c>
/// (up to three boards side by side), <c>pair</c> (one board per pedestal column) or
/// <c>span</c> (one board across both columns). Boards are named as the SDK names them.</summary>
public sealed record SkalarkiLayoutRow(
    string Kind,
    string? Device = null,
    string? Label = null,
    IReadOnlyList<string>? Devices = null,
    IReadOnlyList<string>? Labels = null,
    string? Left = null,
    string? Right = null);

/// <summary>The plan view and the hardware-check prompts for one catalogue.</summary>
public sealed record SkalarkiLayout(IReadOnlyList<SkalarkiLayoutRow> Rows, IReadOnlyDictionary<string, string> Prompts)
{
    public static SkalarkiLayout Empty { get; } = new([], new Dictionary<string, string>());

    /// <summary>Every board the plan view draws, in reading order (bars, then the row, then
    /// the pedestal left-to-right per pair), with the layout's own label when it has one.</summary>
    public IReadOnlyList<(SkalarkiDeviceId Device, string? Label)> Boards()
    {
        var boards = new List<(SkalarkiDeviceId, string?)>();
        foreach (var row in Rows)
        {
            switch (row.Kind.ToLowerInvariant())
            {
                case "bar":
                case "span":
                    Add(row.Device, row.Label);
                    break;
                case "row":
                    for (var i = 0; row.Devices is not null && i < row.Devices.Count; i++)
                    {
                        Add(row.Devices[i], row.Labels is not null && i < row.Labels.Count ? row.Labels[i] : null);
                    }

                    break;
                case "pair":
                    Add(row.Left, null);
                    Add(row.Right, null);
                    break;
            }
        }

        return boards;

        void Add(string? name, string? label)
        {
            if (SkalarkiDevices.TryParse(name, out var device) && !boards.Any(b => b.Item1 == device))
            {
                boards.Add((device, label));
            }
        }
    }

    /// <summary>The prompts keyed by board id (unknown names dropped).</summary>
    public IReadOnlyDictionary<SkalarkiDeviceId, string> PromptsById()
    {
        var result = new Dictionary<SkalarkiDeviceId, string>();
        foreach (var (name, prompt) in Prompts)
        {
            if (SkalarkiDevices.TryParse(name, out var device))
            {
                result[device] = prompt;
            }
        }

        return result;
    }
}

/// <summary>
/// Loads <c>wwwroot/data/skalarki-layout.json</c>: from the web root when the app serves one
/// (so another deck is a JSON edit on the installed app), else from the copy embedded in this
/// assembly (tests, prerender without a web root). A broken file logs once and falls back to
/// the embedded copy; a catalogue the file lacks renders as an empty plan view.
/// </summary>
public sealed class SkalarkiLayoutCatalog
{
    public const string FileName = "skalarki-layout.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lazy<IReadOnlyDictionary<string, SkalarkiLayout>> _layouts;

    public SkalarkiLayoutCatalog(string? webRootPath, ILogger<SkalarkiLayoutCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _layouts = new Lazy<IReadOnlyDictionary<string, SkalarkiLayout>>(() => Load(webRootPath, logger));
    }

    /// <summary>The layout for a catalogue, or an empty one.</summary>
    public SkalarkiLayout For(SkalarkiCatalogue catalogue)
        => _layouts.Value.TryGetValue(catalogue.ToString(), out var layout) ? layout : SkalarkiLayout.Empty;

    /// <summary>Parses a layout file's text (public so a test can round-trip the shipped file).</summary>
    public static IReadOnlyDictionary<string, SkalarkiLayout> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<LayoutFile>(json, Json) ?? new LayoutFile(null);
        var result = new Dictionary<string, SkalarkiLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, layout) in file.Layouts ?? new Dictionary<string, LayoutEntry>())
        {
            result[name] = new SkalarkiLayout(layout.Rows ?? [], layout.Prompts ?? new Dictionary<string, string>());
        }

        return result;
    }

    /// <summary>The copy embedded in this assembly.</summary>
    public static string EmbeddedJson()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException($"{FileName} is not embedded in the web assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyDictionary<string, SkalarkiLayout> Load(string? webRootPath, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(webRootPath))
        {
            var path = Path.Combine(webRootPath, "data", FileName);
            if (File.Exists(path))
            {
                try
                {
                    return Parse(File.ReadAllText(path));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "{File} could not be read; using the built-in PANELS layout", path);
                }
            }
        }

        return Parse(EmbeddedJson());
    }

    private sealed record LayoutFile(Dictionary<string, LayoutEntry>? Layouts);

    private sealed record LayoutEntry(List<SkalarkiLayoutRow>? Rows, Dictionary<string, string>? Prompts);
}
