namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>One box on a page. <see cref="Value"/> is what sits in the box, <see cref="Detail"/>
/// the line under it, <see cref="Host"/> the small label above it (the machine that hop runs
/// on). The page looks nodes up by <see cref="Id"/> and never computes health itself.</summary>
public sealed record DiagNode(
    string Id,
    DiagState State,
    string Value,
    string? Detail = null,
    string? Host = null)
{
    /// <summary>The node a page draws when a collector has not reported it: amber
    /// <see cref="EcamTokens.NoData"/>, so the page itself says what is not instrumented.</summary>
    public static DiagNode NoData(string id) => new(id, DiagState.NoData, EcamTokens.NoData);

    /// <summary>A node that is intentionally not in use: white <see cref="EcamTokens.NotApplicable"/>.</summary>
    public static DiagNode NotApplicable(string id, string? detail = null)
        => new(id, DiagState.NotApplicable, EcamTokens.NotApplicable, detail);
}

/// <summary>A dashed link between two node ids; the state is the worse of the two endpoints
/// unless the collector overrode it.</summary>
public sealed record DiagLink(string From, string To, DiagState State);

/// <summary>A dial (or a bar on the GSX page). The red arc begins at <see cref="WarningAt"/>;
/// the value text turns amber from <see cref="CautionAt"/>. A null <see cref="Value"/> draws
/// the amber <see cref="EcamTokens.NoData"/> in place of the number.</summary>
public sealed record DiagGauge(
    string Id,
    double? Value,
    double Min,
    double Max,
    double CautionAt,
    double WarningAt,
    string Unit)
{
    /// <summary>The text colour state the value earns against the thresholds.</summary>
    public DiagState ValueState => Value switch
    {
        null => DiagState.NoData,
        var v when v >= WarningAt => DiagState.Warning,
        var v when v >= CautionAt => DiagState.Caution,
        _ => DiagState.Normal,
    };
}

/// <summary>One cyan dotted action line under a STATUS caution ("WOL MAGIC PKT ...... SENT 00:42").</summary>
public sealed record DiagAction(string Label, string Value);

/// <summary>One STATUS-page entry: an amber (or red) title under its system, with the actions
/// the pilot can take or the app has taken. A <see cref="DiagState.Normal"/> severity is an
/// informational green note (an active model trial, say).</summary>
public sealed record DiagCaution(
    string System,
    string Title,
    DiagState Severity,
    IReadOnlyList<DiagAction> Actions,
    DateTimeOffset RaisedUtc);

/// <summary>One RECENT line on the STATUS page: a state change, never a heartbeat.</summary>
public sealed record DiagEvent(DateTimeOffset AtUtc, string Text, DiagState State);

/// <summary>The four-cell strip at the bottom of every page.</summary>
public sealed record DiagMemo(
    string Version,
    string Model,
    TimeSpan Uptime,
    string Phase,
    int CautionCount,
    int WarningCount,
    bool BundleReady);

/// <summary>One page's worth of drawing data, keyed by the ids the Razor component expects.</summary>
public sealed record DiagPage(
    string Id,
    IReadOnlyDictionary<string, DiagNode> Nodes,
    IReadOnlyList<DiagLink> Links,
    IReadOnlyDictionary<string, DiagGauge> Gauges,
    IReadOnlyDictionary<string, string> Readouts,
    string? CautionLine)
{
    /// <summary>A page with nothing on it: every node the component asks for renders as
    /// <see cref="DiagNode.NoData"/>.</summary>
    public static DiagPage Empty(string id) => new(
        id,
        new Dictionary<string, DiagNode>(StringComparer.Ordinal),
        [],
        new Dictionary<string, DiagGauge>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        null);

    /// <summary>The node, or the amber placeholder when no collector reported it.</summary>
    public DiagNode NodeOrNoData(string id)
        => Nodes.TryGetValue(id, out var node) ? node : DiagNode.NoData(id);

    /// <summary>The readout text, or null when nothing reported it.</summary>
    public string? Readout(string id) => Readouts.TryGetValue(id, out var text) ? text : null;

    /// <summary>The gauge, or a gauge with no value (amber XX) when nothing reported it.</summary>
    public DiagGauge GaugeOrNoData(string id, double max = 100, string unit = "")
        => Gauges.TryGetValue(id, out var gauge) ? gauge : new DiagGauge(id, null, 0, max, max, max, unit);

    /// <summary>The link between two nodes as the collector drew it, or the worse of the two
    /// endpoints when it drew none (so a page may draw its fixed wiring from nodes alone).</summary>
    public DiagState LinkState(string from, string to)
    {
        foreach (var link in Links)
        {
            if (link.From == from && link.To == to)
            {
                return link.State;
            }
        }

        return DiagStates.Worse(NodeOrNoData(from).State, NodeOrNoData(to).State);
    }
}

/// <summary>Everything the five pages draw, published once per tick by the snapshot service.</summary>
public sealed record DiagnosticsSnapshot(
    DateTimeOffset AtUtc,
    DiagMemo Memo,
    DiagPage Voice,
    DiagPage Sim,
    DiagPage Remote,
    DiagPage Gsx,
    IReadOnlyList<DiagCaution> Cautions,
    IReadOnlyList<string> InopSystems,
    IReadOnlyList<string> Degraded,
    IReadOnlyList<DiagEvent> Recent)
{
    /// <summary>Before the first tick: every node NoData, no cautions, an empty memo.</summary>
    public static DiagnosticsSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        new DiagMemo("", "", TimeSpan.Zero, "", 0, 0, false),
        DiagPage.Empty(DiagPageIds.Voice),
        DiagPage.Empty(DiagPageIds.Sim),
        DiagPage.Empty(DiagPageIds.Remote),
        DiagPage.Empty(DiagPageIds.Gsx),
        [],
        [],
        [],
        []);

    /// <summary>The page by id, or null for an id that is not a drawing page (STATUS is composed
    /// from the snapshot's lists, not a page).</summary>
    public DiagPage? Page(string id) => id switch
    {
        DiagPageIds.Voice => Voice,
        DiagPageIds.Sim => Sim,
        DiagPageIds.Remote => Remote,
        DiagPageIds.Gsx => Gsx,
        _ => null,
    };
}

/// <summary>The page ids a collector names in <see cref="IDiagnosticsCollector.Page"/> and the
/// route segments under <c>/diagnostics/</c>.</summary>
public static class DiagPageIds
{
    public const string Voice = "voice";
    public const string Sim = "sim";
    public const string Remote = "remote";
    public const string Gsx = "gsx";

    /// <summary>Not a collector page: composed from the cautions, inop and recent lists.</summary>
    public const string Status = "status";

    /// <summary>The four pages collectors may contribute to, in display order.</summary>
    public static IReadOnlyList<string> Drawing { get; } = [Voice, Sim, Remote, Gsx];

    /// <summary>Every page in selector order.</summary>
    public static IReadOnlyList<string> All { get; } = [Voice, Sim, Remote, Gsx, Status];

    /// <summary>True for an id a collector may name.</summary>
    public static bool IsDrawing(string id) => id is Voice or Sim or Remote or Gsx;
}
