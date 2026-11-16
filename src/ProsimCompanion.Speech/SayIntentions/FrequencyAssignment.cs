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

/// <summary>One getCommsHistory entry, reduced to what the frequency watch reads.
/// <paramref name="Incoming"/> is the pilot's message (<c>incoming_message</c>), null when blank;
/// <paramref name="StampUtc"/> the entry's <c>stamp_zulu</c>, null when unreadable.</summary>
internal sealed record CommEntry(
    long Id,
    string Channel,
    string? Outgoing,
    string? OutgoingEnglish,
    string? StationName = null,
    string? Incoming = null,
    DateTimeOffset? StampUtc = null,
    int? FrequencyKhz = null)
{
    /// <summary>A COM1 entry — the only channel ATC speaks to the flight deck on
    /// (INTERCOM is the ramp crew, ACARS the clearance).</summary>
    public bool IsCom1 => Channel.Equals("COM1", StringComparison.OrdinalIgnoreCase);

    /// <summary>ATC's text (the English form first), or null when the entry carries none.</summary>
    public string? AtcText
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(OutgoingEnglish) ? Outgoing : OutgoingEnglish;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
    }

    /// <summary>
    /// A COM1 entry with the pilot's message and no ATC text yet. SayIntentions writes ONE
    /// record per exchange: it is created when the pilot transmits (the stamp is the pilot's
    /// time) and ATC's reply is added to the same record two to three seconds later (EGLL
    /// 2026-11-07: push approval and taxi clearance both lost — a poll landed in that gap,
    /// <c>since_id</c> moved past the record, and the reply was never fetched). A read-back the
    /// pilot made looks the same and stays pending for good, hence the grace in the tracker.
    /// </summary>
    public bool IsPendingReply => IsCom1 && !string.IsNullOrWhiteSpace(Incoming) && AtcText is null;
}

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

                var incoming = Str(node["incoming_message"]);
                entries.Add(new CommEntry(
                    id,
                    Str(node["channel"]) ?? "",
                    Str(node["outgoing_message"]),
                    Str(node["outgoing_message_english"]),
                    Str(node["station_name"]),
                    string.IsNullOrWhiteSpace(incoming) ? null : incoming,
                    Stamp(node["stamp_zulu"]),
                    HandoffParser.ToKhz(Str(node["frequency"]))));
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

    /// <summary>"2026-11-06T23:55:45Z" → UTC; null when absent or unreadable.</summary>
    private static DateTimeOffset? Stamp(JsonNode? node)
        => DateTimeOffset.TryParse(Str(node), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp) ? stamp : null;
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
/// <item>The <c>since_id</c> marker never passes a record that is still waiting for ATC's
/// text (<see cref="CommEntry.IsPendingReply"/>): SayIntentions fills the pilot's record with
/// the reply a few seconds after it is created, and a marker moved past it loses the reply
/// for good (EGLL 2026-11-07). A record pending longer than <see cref="PendingGrace"/> — a
/// read-back ATC never answers — releases the marker. Entries behind a held marker are
/// fetched again each poll and handed out once (<see cref="ApplyHistory"/>).</item>
/// </list>
/// Not thread-safe; the monitor drives it from one tick at a time.
/// </summary>
internal sealed class AssignedFrequencyTracker
{
    /// <summary>How long a pilot-only record may hold the marker. ATC's text lands in two to
    /// three seconds (the reply audio's own stamp, 2026-11-07); a pilot read-back gets no
    /// text at all and must not hold the marker for the rest of the flight.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromSeconds(30);

    /// <summary>Polls a pending record without a readable stamp may hold the marker for.</summary>
    public const int PendingGracePolls = 8;

    private readonly HashSet<long> _seen = [];
    private readonly Dictionary<long, int> _pendingPolls = [];
    private long? _flightId;
    private bool _handoffSeen;
    private AssignedFrequency? _handoff;
    private AssignedFrequency? _correct;

    /// <summary>The next poll's <c>since_id</c>: the highest id below which every record is
    /// complete — held back while a record is still waiting for ATC's text.</summary>
    public long LastId { get; private set; }

    /// <summary>The id of the record holding the marker back, or null when none is pending.</summary>
    public long? PendingId { get; private set; }

    /// <summary>True when the last <see cref="ApplyHistory"/> began a new SayIntentions flight —
    /// what it returned is that flight's backlog, to seed on rather than to speak.</summary>
    public bool LastApplyReset { get; private set; }

    /// <summary>True until the flight's first hand-off instruction: the window in which
    /// <c>correct_frequency</c> is worth asking for.</summary>
    public bool NeedsCorrectFrequency => !_handoffSeen;

    /// <summary>The assigned frequency, or null when it is not known.</summary>
    public AssignedFrequency? Current => _handoffSeen ? _handoff : _correct;

    /// <summary>Applies a comms-history poll and returns the COM1 ATC lines not handed out
    /// before, in id order — the first poll's whole backlog included (the caller seeds on it).
    /// A changed <paramref name="flightId"/> is a new SayIntentions flight: everything known
    /// about the old one is dropped first.</summary>
    public IReadOnlyList<CommEntry> ApplyHistory(long? flightId, IEnumerable<CommEntry> entries, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        LastApplyReset = flightId is not null && _flightId is not null && flightId != _flightId;
        if (LastApplyReset)
        {
            Reset();
        }

        _flightId = flightId ?? _flightId;
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var fresh = new List<CommEntry>();
        var markerHeld = false;
        PendingId = null;

        foreach (var entry in entries.OrderBy(e => e.Id))
        {
            // since_id semantics are undocumented — tolerate a body that repeats old entries.
            if (entry.Id <= LastId)
            {
                continue;
            }

            if (entry.IsPendingReply && !GraceExpired(entry, now))
            {
                if (!markerHeld)
                {
                    markerHeld = true;
                    PendingId = entry.Id;
                }

                continue; // asked for again next poll; handed out once ATC's text is in it
            }

            if (!markerHeld)
            {
                LastId = entry.Id;
            }

            _pendingPolls.Remove(entry.Id);
            if (!_seen.Add(entry.Id) || !entry.IsCom1)
            {
                continue;
            }

            if (entry.AtcText is not null)
            {
                fresh.Add(entry);
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

        // Ids at or below the marker are never fetched again — nothing to remember about them.
        _seen.RemoveWhere(id => id <= LastId);
        foreach (var id in _pendingPolls.Keys.Where(id => id <= LastId).ToList())
        {
            _pendingPolls.Remove(id);
        }

        return fresh;
    }

    /// <summary>A pending record is given up on by its stamp when it has one, else by polls.</summary>
    private bool GraceExpired(CommEntry entry, DateTimeOffset now)
    {
        if (entry.StampUtc is { } stamp)
        {
            return now - stamp > PendingGrace;
        }

        var polls = _pendingPolls.GetValueOrDefault(entry.Id) + 1;
        _pendingPolls[entry.Id] = polls;
        return polls > PendingGracePolls;
    }

    /// <summary>Applies a getCurrentFrequencies poll (null = SI names no station).</summary>
    public void ApplyCorrectFrequency(AssignedFrequency? correct) => _correct = correct;

    public void Reset()
    {
        _seen.Clear();
        _pendingPolls.Clear();
        PendingId = null;
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

/// <summary>Which rule the wrong-frequency report judges COM1 by.</summary>
internal enum FrequencyWatchRule
{
    /// <summary>Not judged: no live flight, or a phase outside the watch.</summary>
    None,

    /// <summary>COM1 must be on the frequency ATC assigned (pushback to taxi-in).</summary>
    Assigned,

    /// <summary>At the gate: COM1 on any station of the local airport is correct.</summary>
    AirportStations,
}

/// <summary>The judgement of COM1 against what ATC expects.</summary>
internal enum Com1Verdict
{
    /// <summary>One side cannot be read — the FO says nothing.</summary>
    Unknown,

    Correct,

    Wrong,
}

/// <summary>
/// The judgement half of the wrong-frequency report, pure for tests (owner flight 2026-11-16,
/// EGLL stand 313: the report was quiet at the gate by design and the pilot sat on a wrong
/// frequency unnoticed).
/// <list type="bullet">
/// <item>At the gate (cold and dark, preflight, departure) SayIntentions names Ground from the
/// moment the flight is filed, while the crew is rightly on ATIS or Delivery. So there COM1 on
/// ANY station of the local airport's list is correct, and only a frequency that belongs to no
/// station of the airport is wrong. No station list — no judgement, never a false alarm.</item>
/// <item>From pushback to taxi-in COM1 must be on the assigned frequency (the 2026-10-05 rule).</item>
/// <item>The same rules with the FO on the radios (owner's rig, 2026-11-16: he flies that way
/// and wants the advice there too). The FO's own hand-off tune is kept out of the judgement by
/// <see cref="FoHandoffTuneWindow"/>.</item>
/// </list>
/// </summary>
internal static class FrequencyJudgement
{
    public static FrequencyWatchRule Rule(bool flightLive, Core.Flight.FlightPhase phase)
    {
        if (!flightLive)
        {
            return FrequencyWatchRule.None;
        }

        if (Core.Flight.FlightPhaseExtensions.IsAtGate(phase))
        {
            return FrequencyWatchRule.AirportStations;
        }

        return SayIntentionsFrequencyMonitor.IsWatchedPhase(phase) ? FrequencyWatchRule.Assigned : FrequencyWatchRule.None;
    }

    /// <param name="com1Khz">COM1 active by the app's one read rule; 0 = not read.</param>
    /// <param name="airportStationsKhz">The local airport's station frequencies; null or empty
    /// when the list is not known. Read only under <see cref="FrequencyWatchRule.AirportStations"/>.</param>
    public static Com1Verdict Judge(
        FrequencyWatchRule rule, AssignedFrequency? assigned, int com1Khz, IReadOnlyCollection<int>? airportStationsKhz)
    {
        if (rule == FrequencyWatchRule.None || assigned is null || com1Khz is < 118_000 or > 137_000)
        {
            return Com1Verdict.Unknown;
        }

        if (SameChannel(com1Khz, assigned.Khz))
        {
            return Com1Verdict.Correct;
        }

        if (rule == FrequencyWatchRule.Assigned)
        {
            return Com1Verdict.Wrong;
        }

        if (airportStationsKhz is null || airportStationsKhz.Count == 0)
        {
            return Com1Verdict.Unknown;
        }

        return airportStationsKhz.Any(khz => SameChannel(com1Khz, khz)) ? Com1Verdict.Correct : Com1Verdict.Wrong;
    }

    private static bool SameChannel(int a, int b) => Math.Abs(a - b) <= FrequencyWatchCore.ToleranceKhz;
}

/// <summary>
/// One question per assignment and COM1 frequency: the pair the FO last asked about is not
/// asked about again — whether the captain said no, did not answer, or the wait and the
/// push-to-talk both fired for it. Any other pair re-arms. Not thread-safe; the owner locks.
/// </summary>
internal sealed class TuneOfferMemory
{
    private (int AssignedKhz, int Com1Khz)? _last;

    /// <summary>True when this pair was not the last one asked about — it is now.</summary>
    public bool TryClaim(int assignedKhz, int com1Khz)
    {
        if (_last == (assignedKhz, com1Khz))
        {
            return false;
        }

        _last = (assignedKhz, com1Khz);
        return true;
    }

    /// <summary>Gives the pair back when the question could not be raised after all.</summary>
    public void Release(int assignedKhz, int com1Khz)
    {
        if (_last == (assignedKhz, com1Khz))
        {
            _last = null;
        }
    }

    public void Reset() => _last = null;
}

/// <summary>One firing of the wrong-frequency report: after the wait, or at once on the ATC
/// push-to-talk.</summary>
/// <param name="Trigger"><c>wait</c> or <c>ptt</c>.</param>
/// <param name="Com1Station">The station that owns the frequency COM1 is on, when known
/// (<see cref="Com1StationNames"/>); null otherwise.</param>
internal sealed record WrongFrequencyReport(AssignedFrequency Assigned, int Com1Khz, TimeSpan Waited, string Trigger, bool AtGate, string? Com1Station = null)
{
    public const string WaitTrigger = "wait";
    public const string PttTrigger = "ptt";
}

/// <summary>
/// "The FO is changing COM1 itself": open from the moment the FO takes a hand-off to read back
/// until its tune and check-in wait are over, and for <see cref="Grace"/> after. While it is
/// open the wrong-frequency report judges nothing — neither the wait nor the push-to-talk may
/// ask "Shall I tune it?" about a frequency change the FO is in the middle of making
/// (owner request 2026-11-16). Thread-safe; the clock is passed in.
/// </summary>
public sealed class FoHandoffTuneWindow
{
    /// <summary>After the hand-off is done: the radio's own frequency and the store settle.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private int _pending;
    private DateTimeOffset _closedUtc = DateTimeOffset.MinValue;

    public void Begin()
    {
        lock (_gate)
        {
            _pending++;
        }
    }

    public void End(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_pending > 0)
            {
                _pending--;
            }

            _closedUtc = nowUtc;
        }
    }

    public bool IsOpen(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return _pending > 0 || nowUtc - _closedUtc < Grace;
        }
    }
}

/// <summary>
/// The name of the station COM1 is really on (owner's rig 2026-11-16: on 119.000 at Heathrow
/// SayIntentions answered as "Scottish Control" — it never objects to a frequency, it connects
/// the pilot to whoever owns it; the FO should say who that is). Only names the evidence
/// supports, in this order:
/// <list type="number">
/// <item>SayIntentions' own: every getCommsHistory COM1 record carries <c>frequency</c> and
/// <c>station_name</c> (capture 2026-10-05: <c>"frequency":"118.125","station_name":"Helsinki
/// Ground"</c>). The newest record on a frequency names it.</item>
/// <item>The local airport's station list (getWX comms, the Radios tile's chips): the callsign,
/// else the station type as a word.</item>
/// </list>
/// No match — no name; nothing is guessed. The name is looked up by the frequency COM1 is on
/// at that moment, so it cannot outlive a frequency change. Not used: the
/// getCurrentFrequencies <c>frequencies[]</c> list (it is the nearest airport's, see the
/// integration doc) and <c>L:SIAI_COM1_POSITION</c> (its values are not documented beyond
/// "0 = unknown"). Thread-safe: the tick writes, the push-to-talk reads.
/// </summary>
internal sealed class Com1StationNames
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _heard = new();

    /// <summary>Takes the frequency → station pairs out of a history poll (in id order, so the
    /// newest record on a frequency wins).</summary>
    public void Note(IEnumerable<CommEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries.OrderBy(e => e.Id))
        {
            // SayIntentions writes the station of a frequency nobody owns as "Unknown" (owner's
            // rig 2026-11-16 01:21Z on 118.000: the FO said "COM one is on Unknown, …"). That is
            // not a name; the line then gives the frequency alone.
            if (entry.IsCom1 && entry.FrequencyKhz is { } khz && IsARealName(entry.StationName))
            {
                _heard[khz] = entry.StationName.Trim();
            }
        }
    }

    /// <summary>A new SayIntentions flight: the old flight's stations are forgotten.</summary>
    public void Reset() => _heard.Clear();

    /// <summary>False for an empty name and for SayIntentions' placeholders for "no station".</summary>
    internal static bool IsARealName(string? stationName)
    {
        var name = stationName?.Trim();
        return !string.IsNullOrEmpty(name)
            && !name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("N/A", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The station on <paramref name="com1Khz"/>, or null when no source names it.</summary>
    public string? Resolve(int com1Khz, Core.Radios.AirportStations? localAirport)
        => Resolve(com1Khz, _heard, localAirport);

    /// <summary>Pure, for tests.</summary>
    public static string? Resolve(int com1Khz, IEnumerable<KeyValuePair<int, string>> heard, Core.Radios.AirportStations? localAirport)
    {
        ArgumentNullException.ThrowIfNull(heard);
        if (com1Khz <= 0)
        {
            return null;
        }

        foreach (var (khz, name) in heard.OrderBy(h => Math.Abs(h.Key - com1Khz)))
        {
            if (Math.Abs(khz - com1Khz) <= FrequencyWatchCore.ToleranceKhz)
            {
                return name;
            }

            break; // the nearest is not this channel: none is
        }

        if (localAirport is not null)
        {
            foreach (var station in localAirport.Stations)
            {
                if (Math.Abs(station.Khz - com1Khz) <= FrequencyWatchCore.ToleranceKhz)
                {
                    var name = station.Callsign.Trim().Length > 0 ? station.Callsign.Trim() : station.Type.Trim();
                    return name.Length > 0 ? name : null;
                }
            }
        }

        return null;
    }
}
