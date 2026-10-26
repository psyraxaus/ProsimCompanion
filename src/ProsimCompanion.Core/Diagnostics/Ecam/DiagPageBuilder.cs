using System.Globalization;

namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// Fluent assembly of one <see cref="DiagPage"/>. A collector writes into a fresh builder each
/// tick; the snapshot service merges the builders of every collector on the same page
/// (<see cref="Merge"/>). A link with no explicit state resolves at <see cref="Build"/> time to
/// the worse of its endpoints, so the order of <see cref="Node"/> and <see cref="Link"/> calls
/// does not matter. The first caution line set wins: the page shows one line, and the
/// collector registered first owns it.
/// </summary>
public sealed class DiagPageBuilder
{
    private readonly Dictionary<string, DiagNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<(string From, string To, DiagState? State)> _links = [];
    private readonly Dictionary<string, DiagGauge> _gauges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _readouts = new(StringComparer.Ordinal);
    private string? _cautionLine;

    public DiagPageBuilder(string pageId, DiagEventSink events)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(events);
        PageId = pageId;
        Events = events;
    }

    /// <summary>Which page this builder fills.</summary>
    public string PageId { get; }

    /// <summary>Where a collector pushes RECENT lines on a state edge.</summary>
    public DiagEventSink Events { get; }

    /// <summary>The ids of the nodes added so far (the service marks them NoData when the
    /// collector throws half-way).</summary>
    public IReadOnlyCollection<string> NodeIds => _nodes.Keys;

    /// <summary>Adds or replaces a node.</summary>
    public DiagPageBuilder Node(string id, DiagState state, string value, string? detail = null, string? host = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(value);
        _nodes[id] = new DiagNode(id, state, value, detail, host);
        return this;
    }

    /// <summary>Adds or replaces a node from a ready record.</summary>
    public DiagPageBuilder Node(DiagNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes[node.Id] = node;
        return this;
    }

    /// <summary>A node whose source is missing (amber XX).</summary>
    public DiagPageBuilder NoData(string id, string? detail = null, string? host = null)
        => Node(DiagNode.NoData(id) with { Detail = detail, Host = host });

    /// <summary>A node intentionally not in use (white - - -).</summary>
    public DiagPageBuilder NotApplicable(string id, string? detail = null, string? host = null)
        => Node(DiagNode.NotApplicable(id, detail) with { Host = host });

    /// <summary>A link; null state means "the worse of the two endpoints at build time".</summary>
    public DiagPageBuilder Link(string from, string to, DiagState? state = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        _links.Add((from, to, state));
        return this;
    }

    /// <summary>A dial or bar.</summary>
    public DiagPageBuilder Gauge(string id, double? value, double min, double max, double cautionAt, double warningAt, string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(unit);
        _gauges[id] = new DiagGauge(id, value, min, max, cautionAt, warningAt, unit);
        return this;
    }

    /// <summary>Free-form green text keyed by id; null or blank removes nothing and adds nothing.</summary>
    public DiagPageBuilder Readout(string id, string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!string.IsNullOrWhiteSpace(text))
        {
            _readouts[id] = text;
        }

        return this;
    }

    /// <summary>A numeric readout formatted invariantly, or nothing when the value is null.</summary>
    public DiagPageBuilder Readout(string id, double? value, string format = "0")
        => Readout(id, value?.ToString(format, CultureInfo.InvariantCulture));

    /// <summary>The page's one centred amber line; the first one set wins.</summary>
    public DiagPageBuilder CautionLine(string? text)
    {
        if (_cautionLine is null && !string.IsNullOrWhiteSpace(text))
        {
            _cautionLine = text;
        }

        return this;
    }

    /// <summary>Folds another builder of the same page into this one: its nodes, gauges and
    /// readouts win on id clashes, its links are appended, its caution line fills an empty one.</summary>
    public DiagPageBuilder Merge(DiagPageBuilder other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (id, node) in other._nodes)
        {
            _nodes[id] = node;
        }

        _links.AddRange(other._links);
        foreach (var (id, gauge) in other._gauges)
        {
            _gauges[id] = gauge;
        }

        foreach (var (id, text) in other._readouts)
        {
            _readouts[id] = text;
        }

        CautionLine(other._cautionLine);
        return this;
    }

    /// <summary>Replaces every node added so far with its NoData twin, keeping the host
    /// label. Used when the collector threw: whatever it managed to say is no longer trusted.</summary>
    public DiagPageBuilder DowngradeToNoData()
    {
        foreach (var id in _nodes.Keys.ToArray())
        {
            _nodes[id] = DiagNode.NoData(id) with { Host = _nodes[id].Host };
        }

        foreach (var id in _gauges.Keys.ToArray())
        {
            _gauges[id] = _gauges[id] with { Value = null };
        }

        _readouts.Clear();
        _cautionLine = null;
        return this;
    }

    /// <summary>Freezes the page. Links to nodes nobody added resolve against NoData.</summary>
    public DiagPage Build()
    {
        var links = new List<DiagLink>(_links.Count);
        foreach (var (from, to, state) in _links)
        {
            var resolved = state ?? DiagStates.Worse(StateOf(from), StateOf(to));
            links.Add(new DiagLink(from, to, resolved));
        }

        return new DiagPage(
            PageId,
            new Dictionary<string, DiagNode>(_nodes, StringComparer.Ordinal),
            links,
            new Dictionary<string, DiagGauge>(_gauges, StringComparer.Ordinal),
            new Dictionary<string, string>(_readouts, StringComparer.Ordinal),
            _cautionLine);
    }

    private DiagState StateOf(string id) => _nodes.TryGetValue(id, out var node) ? node.State : DiagState.NoData;
}
