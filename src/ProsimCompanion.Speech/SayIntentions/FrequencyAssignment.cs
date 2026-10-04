using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>Where an assigned frequency came from — carried into the session event so a
/// flight review can tell the two sources apart.</summary>
internal enum AssignedFrequencySource
{
    /// <summary>ATC's last "Contact … on …" instruction in the comms history.</summary>
    Handoff,

    /// <summary><c>correct_frequency</c> of getCurrentFrequencies — only before the first
    /// hand-off of the flight.</summary>
    CorrectFrequency,
}

/// <summary>The frequency SayIntentions ATC wants COM1 on, in integer kHz (the unit of
/// ProSim's COM analogs), with the station name as ATC said it.</summary>
internal sealed record AssignedFrequency(int Khz, string Station, AssignedFrequencySource Source);

/// <summary>One getCommsHistory entry, reduced to what the frequency watch reads.</summary>
internal sealed record CommEntry(long Id, string Channel, string? Outgoing, string? OutgoingEnglish);

/// <summary>
/// Reads ATC hand-off instructions out of SayIntentions comms-history text. Live capture
/// 2026-10-05 (EFHK→LKPR): every hand-off was "Contact &lt;station&gt; on &lt;freq&gt;" —
/// "Contact Tower on 119.7. Have a Good morning", "Contact Tallinn Control on 134.325.".
/// Deliberately strict: a line it cannot read yields nothing, and the caller then stays
/// silent — a wrong "we should be on …" is worse than none.
/// </summary>
internal static partial class HandoffParser
{
    /// <summary>VHF COM band in kHz.</summary>
    private const int BandLowKhz = 118_000;
    private const int BandHighKhz = 136_990;

    // "Contact Tower on 119.7" / FAA "contact departure 124.5" ("on" optional). The station
    // stops at punctuation so a later clause's frequency is never glued to this verb.
    [GeneratedRegex(
        @"\b(?:contact|monitor)\s+(?<station>[^,.;:\d]+?)\s+(?:on\s+)?(?<freq>1[1-3]\d\.\d{1,3})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HandoffPattern();

    // A clause that OPENS with the verb is an instruction even when no frequency follows
    // (FAA "Contact departure."). "Radar contact" — said on every FAA check-in — is not:
    // there the word follows "radar", not a clause start.
    [GeneratedRegex(
        @"(?:^|[.,;:!?]\s*)(?:contact|monitor)\s+\p{L}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstructionPattern();

    /// <summary>The hand-off in <paramref name="message"/>, or null when there is none it
    /// can read in full (station AND an in-band frequency).</summary>
    public static AssignedFrequency? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var match = HandoffPattern().Match(message);
        if (!match.Success || ToKhz(match.Groups["freq"].Value) is not { } khz)
        {
            return null;
        }

        return new AssignedFrequency(khz, match.Groups["station"].Value.Trim(), AssignedFrequencySource.Handoff);
    }

    /// <summary>True when <paramref name="message"/> tells the pilot to change station,
    /// whether or not <see cref="Parse"/> can read the frequency.</summary>
    public static bool IsInstruction(string? message)
        => !string.IsNullOrWhiteSpace(message) && InstructionPattern().IsMatch(message);

    /// <summary>"119.7" → 119700, "134.325" → 134325. A two-decimal reading ending in 2 or 7
    /// is the spoken short form of a 25 kHz channel ("118.12" = 118.125). Null when the text
    /// is not a number or is outside the VHF COM band.</summary>
    public static int? ToKhz(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz)
            || mhz is < 100m or > 200m)
        {
            return null;
        }

        var khz = (int)Math.Round(mhz * 1000m);
        var dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0 && trimmed.Length - dot - 1 == 2 && trimmed[^1] is '2' or '7')
        {
            khz += 5;
        }

        return khz is >= BandLowKhz and <= BandHighKhz ? khz : null;
    }
}

/// <summary>Tolerant reader for the getCommsHistory body —
/// <c>{"flight_id":…,"comm_history":[{id,channel,outgoing_message,outgoing_message_english,…}]}</c>.
/// A malformed body yields no entries rather than an exception. Pure so it is testable
/// without HTTP.</summary>
internal static class CommHistory
{
    public static (long? FlightId, IReadOnlyList<CommEntry> Entries) Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            var root = JsonNode.Parse(body);
            var flightId = Long(root?["flight_id"]);
            if (root?["comm_history"] is not JsonArray history)
            {
                return (flightId, []);
            }

            var entries = new List<CommEntry>(history.Count);
            foreach (var node in history)
            {
                if (node is null || Long(node["id"]) is not { } id)
                {
                    continue;
                }

                entries.Add(new CommEntry(
                    id,
                    Str(node["channel"]) ?? "",
                    Str(node["outgoing_message"]),
                    Str(node["outgoing_message_english"])));
            }

            return (flightId, entries);
        }
        catch (Exception)
        {
            return (null, []);
        }
    }

    /// <summary>The root <c>correct_frequency</c> / <c>correct_position</c> pair of a
    /// getCurrentFrequencies body; null when either is absent (both are JSON null in cruise —
    /// live capture 2026-10-05) or the frequency is out of band.</summary>
    public static AssignedFrequency? ParseCorrectFrequency(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            var root = JsonNode.Parse(body);
            var position = Str(root?["correct_position"]);
            if (string.IsNullOrWhiteSpace(position) || HandoffParser.ToKhz(Str(root?["correct_frequency"])) is not { } khz)
            {
                return null;
            }

            return new AssignedFrequency(khz, position.Trim(), AssignedFrequencySource.CorrectFrequency);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (Exception)
        {
            return node.ToString();
        }
    }

    private static long? Long(JsonNode? node)
        => long.TryParse(Str(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>
/// Keeps the frequency SayIntentions ATC last assigned on COM1. Rules, all from the
/// 2026-10-05 live captures:
/// <list type="bullet">
/// <item>The assignment is the LAST hand-off instruction — never "the frequency of the last
/// exchange": SI lets the pilot check in with any station (the owner tuned Helsinki Radar
/// while assigned Tallinn Control and Radar answered "Identified").</item>
/// <item>A hand-off it cannot read ("Contact departure.") clears the assignment — unknown,
/// so the watch is silent until the next readable one.</item>
/// <item><c>correct_frequency</c> counts only before the first hand-off of the flight; it is
/// null in cruise whatever COM1 is on.</item>
/// <item>Only COM1 entries count — INTERCOM is the GSX ramp crew, ACARS the clearance.</item>
/// </list>
/// Not thread-safe; the monitor drives it from one tick at a time.
/// </summary>
internal sealed class AssignedFrequencyTracker
{
    private long? _flightId;
    private bool _handoffSeen;
    private AssignedFrequency? _handoff;
    private AssignedFrequency? _correct;

    /// <summary>Highest comms-history id applied — the next poll's <c>since_id</c>.</summary>
    public long LastId { get; private set; }

    /// <summary>True until the flight's first hand-off instruction: the window in which
    /// <c>correct_frequency</c> is worth asking for.</summary>
    public bool NeedsCorrectFrequency => !_handoffSeen;

    /// <summary>The assigned frequency, or null when it is not known.</summary>
    public AssignedFrequency? Current => _handoffSeen ? _handoff : _correct;

    /// <summary>Applies a comms-history poll. A changed <paramref name="flightId"/> is a new
    /// SayIntentions flight: everything known about the old one is dropped first.</summary>
    public void ApplyHistory(long? flightId, IEnumerable<CommEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (flightId is not null && _flightId is not null && flightId != _flightId)
        {
            Reset();
        }

        _flightId = flightId ?? _flightId;

        foreach (var entry in entries.OrderBy(e => e.Id))
        {
            // since_id semantics are undocumented — tolerate a body that repeats old entries.
            if (entry.Id <= LastId)
            {
                continue;
            }

            LastId = entry.Id;
            if (!entry.Channel.Equals("COM1", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var handoff = HandoffParser.Parse(entry.Outgoing) ?? HandoffParser.Parse(entry.OutgoingEnglish);
            if (handoff is not null)
            {
                _handoffSeen = true;
                _handoff = handoff;
            }
            else if (HandoffParser.IsInstruction(entry.Outgoing) || HandoffParser.IsInstruction(entry.OutgoingEnglish))
            {
                _handoffSeen = true;
                _handoff = null;
            }
        }
    }

    /// <summary>Applies a getCurrentFrequencies poll (null = SI names no station).</summary>
    public void ApplyCorrectFrequency(AssignedFrequency? correct) => _correct = correct;

    public void Reset()
    {
        _flightId = null;
        _handoffSeen = false;
        _handoff = null;
        _correct = null;
        LastId = 0;
    }
}

/// <summary>What the watch decided to say: the assignment, what COM1 was on, how long.</summary>
internal sealed record WrongFrequencyAdvisory(AssignedFrequency Assigned, int Com1Khz, TimeSpan Waited);

/// <summary>
/// The timing half of the wrong-frequency report, pure for tests: COM1 must stay off the
/// assigned frequency — on ONE other frequency — for the whole wait before the FO speaks,
/// so a hand-off in progress or the pilot dialling through channels never triggers it. One
/// report per wrong frequency; tuning the assigned frequency re-arms it.
/// </summary>
internal sealed class FrequencyWatchCore
{
    /// <summary>COM1 within this of the assignment is "on it": a 25 kHz channel and its
    /// 8.33 kHz name (118.000 / 118.005) are the same carrier.</summary>
    public const int ToleranceKhz = 5;

    private DateTimeOffset? _mismatchSince;
    private int _mismatchCom1Khz;
    private int _mismatchAssignedKhz;
    private bool _reported;

    /// <summary>One evaluation. <paramref name="com1Khz"/> outside the VHF COM band (ProSim
    /// absent, radio off) and an unknown assignment both reset the watch and return null.</summary>
    public WrongFrequencyAdvisory? Step(DateTimeOffset nowUtc, AssignedFrequency? assigned, int com1Khz, TimeSpan wait)
    {
        if (assigned is null || com1Khz is < 118_000 or > 137_000
            || Math.Abs(com1Khz - assigned.Khz) <= ToleranceKhz)
        {
            Reset();
            return null;
        }

        if (_mismatchSince is null || com1Khz != _mismatchCom1Khz || assigned.Khz != _mismatchAssignedKhz)
        {
            _mismatchSince = nowUtc;
            _mismatchCom1Khz = com1Khz;
            _mismatchAssignedKhz = assigned.Khz;
            _reported = false;
        }

        var waited = nowUtc - _mismatchSince.Value;
        if (_reported || waited < wait)
        {
            return null;
        }

        _reported = true;
        return new WrongFrequencyAdvisory(assigned, com1Khz, waited);
    }

    public void Reset()
    {
        _mismatchSince = null;
        _reported = false;
    }
}
