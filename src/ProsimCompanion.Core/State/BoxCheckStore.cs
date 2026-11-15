namespace ProsimCompanion.Core.State;

/// <summary>What the FO found on one page group of the MCDU check.</summary>
public enum BoxCheckItemState
{
    /// <summary>Not looked at (the check has not come to it, or the page was not the expected one).</summary>
    NotChecked,

    /// <summary>Read, and nothing to say.</summary>
    Ok,

    /// <summary>A field the crew fills later is still empty — not a fault at the gate.</summary>
    NotEntered,

    /// <summary>The page disagrees with the plan, the loadsheet or the computed figures.</summary>
    Difference,
}

/// <summary>One letter of the check: its page group, the verdict and the lines shown on the card
/// (what was read, then each finding). The lines are composed in English by the Speech pillar,
/// like every FO line.</summary>
public sealed record BoxCheckItem(string Id, string Letter, BoxCheckItemState State, IReadOnlyList<string> Lines);

/// <summary>The MCDU check as the web card shows it: running or not, the page the FO is on, and
/// the eight items of the last run.</summary>
public sealed record BoxCheckSnapshot(
    bool Running,
    string? CurrentItemId,
    DateTimeOffset? RanAtUtc,
    string? Source,
    IReadOnlyList<BoxCheckItem> Items)
{
    /// <summary>The eight page groups in the order the FO visits them (D-I-F-S-R-I-P-P).</summary>
    public static IReadOnlyList<(string Id, string Letter)> Order { get; } =
    [
        (BoxCheckItemIds.Data, "D"),
        (BoxCheckItemIds.InitA, "I"),
        (BoxCheckItemIds.FlightPlan, "F"),
        (BoxCheckItemIds.Secondary, "S"),
        (BoxCheckItemIds.RadNav, "R"),
        (BoxCheckItemIds.InitB, "I"),
        (BoxCheckItemIds.Perf, "P"),
        (BoxCheckItemIds.Prog, "P"),
    ];

    public static BoxCheckSnapshot Empty { get; } = new(false, null, null, null, Blank());

    /// <summary>All eight items, none checked.</summary>
    public static IReadOnlyList<BoxCheckItem> Blank()
        => [.. Order.Select(o => new BoxCheckItem(o.Id, o.Letter, BoxCheckItemState.NotChecked, []))];
}

/// <summary>The item ids — also the suffix of the card's text ids (<c>init.boxCheck.item.*</c>).</summary>
public static class BoxCheckItemIds
{
    public const string Data = "data";
    public const string InitA = "initA";
    public const string FlightPlan = "fpln";
    public const string Secondary = "sec";
    public const string RadNav = "radNav";
    public const string InitB = "initB";
    public const string Perf = "perf";
    public const string Prog = "prog";
}

/// <summary>
/// The FO's MCDU check ("check the box") for the web card: Speech runs the check and publishes
/// here, the page reads. Reset with the flight cycle — the last result belongs to its leg.
/// </summary>
public sealed class BoxCheckStore : SnapshotStore<BoxCheckSnapshot>, IDisposable
{
    private readonly GroundOpsSignals _signals;

    public BoxCheckStore(GroundOpsSignals signals)
        : base(BoxCheckSnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    public void Dispose() => _signals.FlightCycleReset -= OnFlightCycleReset;

    /// <summary>A run begins: all items blank, the first page current.</summary>
    public void Begin(string source)
        => Update(_ => new BoxCheckSnapshot(true, null, null, source, BoxCheckSnapshot.Blank()));

    /// <summary>The FO is now on this item's pages.</summary>
    public void SetCurrent(string itemId) => Update(s => s with { CurrentItemId = itemId });

    /// <summary>One item is read and judged.</summary>
    public void SetItem(BoxCheckItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Update(s => s with { Items = [.. s.Items.Select(i => i.Id == item.Id ? item : i)] });
    }

    /// <summary>The run is over (complete or cut short).</summary>
    public void End(DateTimeOffset atUtc) => Update(s => s with { Running = false, CurrentItemId = null, RanAtUtc = atUtc });

    // A run in progress keeps its card: the reset must not blank a check the FO is still doing.
    private void OnFlightCycleReset(FlightCycleResetKind _) => Update(s => s.Running ? s : BoxCheckSnapshot.Empty);
}

/// <summary>
/// "Shall I enter it?" — the FO's offer to correct one MCDU finding it knows the answer to.
/// One question at a time, per finding (the subject is the fix id); a new finding may be asked
/// after the last was answered. No answer within the minute counts as no: the FO never enters
/// a value the captain did not approve.
/// </summary>
public sealed class BoxCheckFixStore : CaptainsQuestionStore<CaptainsQuestionState>
{
    public static CaptainsQuestionDefinition QuestionDefinition { get; } = new(
        "boxCheckFix",
        CaptainsQuestionShape.YesNo,
        TimeSpan.FromSeconds(60),
        CaptainsQuestionExpiry.CountsAsNo,
        MaxAsks: 1,
        AnswerIsFinalForCycle: false);

    public BoxCheckFixStore(GroundOpsSignals signals)
        : base(QuestionDefinition, CaptainsQuestionState.Empty, signals)
    {
    }

    /// <summary>Raises the offer for one fix; false while another offer stands.</summary>
    public bool Ask(string prompt, string fixId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixId);
        if (Asking.QuestionStands)
        {
            return false;
        }

        return Raise(prompt, subject: fixId) is not null;
    }
}

/// <summary>How a request to run the MCDU check was taken.</summary>
public enum BoxCheckStart
{
    Started,

    AlreadyRunning,

    /// <summary>MCDU features off, or the display is not arriving from ProSim.</summary>
    Unavailable,
}

/// <summary>The seam the command registry and the web card start the check through; the Speech
/// pillar implements it.</summary>
public interface IBoxCheckControl
{
    BoxCheckStart Start(string source);
}
