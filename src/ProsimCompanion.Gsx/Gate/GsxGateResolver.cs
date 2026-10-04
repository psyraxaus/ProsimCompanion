using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ProsimCompanion.Gsx.Mirror;
using ProsimCompanion.Gsx.Protocol;

namespace ProsimCompanion.Gsx.Gate;

/// <summary>A gate reference as it appears in gate.select payloads/candidates.</summary>
public sealed record GsxGateRef(string? UiName, string? Gate, int? Number, string? BglName)
{
    /// <summary>The token to resend on the disambiguation retry (bglName preferred).</summary>
    public string? ResendToken => BglName ?? UiName ?? Gate;

    public static GsxGateRef Parse(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new GsxGateRef(
            GsxFrame.ReadString(node["uiName"]),
            GsxFrame.ReadString(node["gate"]),
            GsxFrame.ReadInt(node["number"]),
            GsxFrame.ReadString(node["bglName"]));
    }
}

/// <summary>The arrival gate GSX accepted (gate.select ok, or the pick in its own position
/// menu) and that still stands. <see cref="GsxName"/> is GSX's own name for it from the
/// gate.select payload ("Gate C 29"), when it sent one.</summary>
public sealed record GsxAssignedGate(string Requested, string? GsxName, int? Number);

/// <summary>Read-only view of the standing arrival-gate assignment — the seam the question
/// catalogue asks before it takes GSX's parking-change menu for an unknown parking.</summary>
public interface IGsxAssignedGateSource
{
    /// <summary>Null when no assignment stands (idle, armed, failed, cancelled).</summary>
    GsxAssignedGate? AssignedGate { get; }
}

/// <summary>
/// Pure gate-selection logic: normalization, disambiguation candidate picking, the SetGate_*
/// readback letter map, and nearest-name suggestions. See docs/integrations/gsx-remote-api.md §6.
/// </summary>
public static class GsxGateResolver
{
    /// <summary>Strip non-alphanumerics, uppercase — the comparison form for all gate tokens.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// Picks the single unambiguous candidate for a disambiguation retry: exact normalized match
    /// on uiName or gate first; else a unique normalized suffix match on gate (falling back to
    /// uiName). Null when no unique candidate exists (fail with the candidate list).
    /// </summary>
    public static GsxGateRef? PickUniqueCandidate(IReadOnlyList<GsxGateRef> candidates, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return null;
        }

        var exact = candidates
            .Where(c => Normalize(c.UiName) == requested || Normalize(c.Gate) == requested)
            .ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }
        if (exact.Count > 1)
        {
            return null;
        }

        var suffix = candidates
            .Where(c =>
            {
                var token = Normalize(c.Gate);
                if (token.Length == 0)
                {
                    token = Normalize(c.UiName);
                }
                return token.Length > 0 && token.EndsWith(requested, StringComparison.Ordinal);
            })
            .ToList();
        return suffix.Count == 1 ? suffix[0] : null;
    }

    /// <summary>
    /// Formats the SetGate_* readback LVARs into a display gate, or null when unassigned.
    /// Letter map: Name 0 = NONE; 10 = "Gate {n}"; 12..37 = A..Z → "{Letter}{n}";
    /// Suffix −1 = unassigned sentinel; anything else unassigned.
    /// </summary>
    public static string? FormatReadback(int name, int number, int suffix)
    {
        if (suffix == -1 || name == 0)
        {
            return null;
        }

        if (name == 10)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Gate {number}");
        }

        if (name is >= 12 and <= 37)
        {
            var letter = (char)('A' + (name - 12));
            return string.Create(CultureInfo.InvariantCulture, $"{letter}{number}");
        }

        return null;
    }

    /// <summary>
    /// Resolves a user token ("D5") to GSX's own parking display name ("Gate D5" at EHAM —
    /// facility prefix included, whitespace TRIMMED; see <see cref="TrimToken"/> for the
    /// 2026-08-16 trim evidence). gate.select matches against those names, so a bare token
    /// fails not_found even when the gate exists (issue #36). Returns the canonical name on a
    /// normalized-exact match, or on a UNIQUE normalized-suffix match; null otherwise
    /// (unknown gate, or ambiguous — e.g. " Gate D5" vs "Stand D5" both ending in D5).
    /// </summary>
    public static string? ResolveCanonical(IReadOnlyList<GsxParking> parkings, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return null;
        }

        var names = parkings
            .Select(p => p.UiGateName ?? p.UiName ?? p.BglName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var exact = names.Where(n => Normalize(n) == requested).ToList();
        if (exact.Count == 1)
        {
            return TrimToken(exact[0]);
        }
        if (exact.Count > 1)
        {
            return null;
        }

        var suffix = names
            .Where(n => Normalize(n).EndsWith(requested, StringComparison.Ordinal))
            .ToList();
        return suffix.Count == 1 ? TrimToken(suffix[0]) : null;
    }

    /// <summary>
    /// gate.select tokens are TRIMMED before sending. 2026-08-15 flight evidence (issue #75):
    /// the mirrored parking name carried a leading space (" Gate D57") and gate.select refused
    /// exactly that name with not_found — GSX trims its own side of the comparison, so the
    /// issue-#36 "send the display name verbatim" rule over-corrected. Inner spacing stays
    /// untouched ("Gate D57" ≠ "GateD57").
    /// </summary>
    public static string TrimToken(string name) => name.Trim();

    /// <summary>Facility-word prefixes GSX prepends to bare stand numbers in its parking
    /// display names ("Stand 313" for the requested "313"). Normalized form, longest first so
    /// PARKING never half-matches.</summary>
    private static readonly string[] KnownFacilityPrefixes =
        ["PARKING", "STAND", "RAMP", "DOCK", "GATE"];

    /// <summary>
    /// Whether a not_found <c>nearest</c> suggestion is safe to substitute for the requested
    /// gate (issue #75): the two must be equal after normalization (whitespace/case only —
    /// " Gate D57" vs "Gate D57"), or the nearest must reduce to the requested token once a
    /// single known facility prefix is stripped ("Stand 313" for "313"). Anything looser
    /// ("31" vs "Stand 313", "D5" vs "Gate D57") is rejected — a nearest-name substitution
    /// that guesses sends the pilot's aircraft services to the wrong stand.
    /// </summary>
    public static bool IsUnambiguousNearestMatch(string requestedGate, string nearestName)
    {
        var requested = Normalize(requestedGate);
        var nearest = Normalize(nearestName);
        if (requested.Length == 0 || nearest.Length == 0)
        {
            return false;
        }

        if (requested == nearest || StripFacilityPrefix(nearest) == requested)
        {
            return true;
        }

        // Decorated stand names (issue #75, EGLL "Stand 547 with Safedock©"): compare the
        // DESIGNATOR — the first word after an optional facility word — so the decoration
        // never blocks the match, while the word boundary keeps "Stand 313" from ever
        // answering a requested "31" (normalization alone erases that boundary).
        var designator = LeadingDesignator(nearestName);
        return designator is not null
            && (designator == requested || designator == StripFacilityPrefix(requested));
    }

    /// <summary>Normalized first word of a display name after an optional facility word:
    /// "Stand 547 with Safedock©" → "547", " Gate D57" → "D57". Null for empty input.</summary>
    private static string? LeadingDesignator(string rawName)
    {
        var words = rawName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(word => word.Length > 0)
            .ToList();
        if (words.Count == 0)
        {
            return null;
        }

        var start = words.Count > 1 && KnownFacilityPrefixes.Contains(words[0]) ? 1 : 0;
        return words[start];
    }

    /// <summary>
    /// Whether the facility GSX names in its "Change Facility [...]" menu entry is the gate an
    /// arrival request assigned (issue #157, LKPR 2026-10-04: gate.select 29 answered
    /// "Gate C 29", the menu then read "Change Facility [Gate C 29]" with the mirror's parking
    /// still empty, and the FO warned of an unknown parking at the gate GSX had prepared).
    /// With GSX's own name for the gate the two texts are compared as whole words: the name
    /// inside the facility ("Stand 547" in "Terminal 5B (531-548) Stand 547 with Safedock©"),
    /// or the facility as the tail of the name ("Gate C 29" for "Pier C | Gate C 29"). Without
    /// it the typed token decides: it must be a run of whole facility words that follows a
    /// facility word or ends the text ("C29" in "Gate C 29"; never "2" in "Gate C 29").
    /// </summary>
    public static bool FacilityNamesGate(string? facility, string? requestedGate, string? gsxName)
    {
        var facilityWords = Words(facility);
        if (facilityWords.Count == 0)
        {
            return false;
        }

        var nameWords = Words(gsxName);
        if (nameWords.Count > 0)
        {
            return IndexOfRun(facilityWords, nameWords) >= 0
                || (facilityWords.Count <= nameWords.Count
                    && facilityWords.Any(word => word.Any(char.IsDigit))
                    && nameWords.Skip(nameWords.Count - facilityWords.Count).SequenceEqual(facilityWords, StringComparer.Ordinal));
        }

        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return false;
        }

        var bare = StripFacilityPrefix(requested);
        for (var start = 0; start < facilityWords.Count; start++)
        {
            var afterFacilityWord = start > 0 && KnownFacilityPrefixes.Contains(facilityWords[start - 1]);
            var run = new StringBuilder();
            for (var end = start; end < facilityWords.Count && run.Length < requested.Length; end++)
            {
                run.Append(facilityWords[end]);
                var text = run.ToString();
                var endsTheText = end == facilityWords.Count - 1;
                if ((text == bare && (afterFacilityWord || endsTheText))
                    || (text == requested && (start == 0 || afterFacilityWord || endsTheText)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The alphanumeric words of a display name, uppercased: "Pier C | Gate C 29" →
    /// PIER, C, GATE, C, 29. Word boundaries are what keep "Gate 2" out of "Gate 29".</summary>
    private static List<string> Words(string? text)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return words;
        }

        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToUpperInvariant(c));
            }
            else if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static int IndexOfRun(List<string> words, List<string> run)
    {
        for (var start = 0; start + run.Count <= words.Count; start++)
        {
            if (words.Skip(start).Take(run.Count).SequenceEqual(run, StringComparer.Ordinal))
            {
                return start;
            }
        }

        return -1;
    }

    private static string StripFacilityPrefix(string normalized)
    {
        foreach (var prefix in KnownFacilityPrefixes)
        {
            if (normalized.Length > prefix.Length && normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return normalized[prefix.Length..];
            }
        }
        return normalized;
    }

    /// <summary>
    /// Resolves the CURRENT gate-context key (the parking uiName GSX pushes, e.g.
    /// "D-Pier =&lt; Medium | Gate D27") to the token <c>gate.select</c> accepts — the parking's
    /// display gate name. Used to re-anchor GSX's remembered facility to the stand the aircraft
    /// actually occupies at departure prep (issue #44: GSX kept the previous session's gate and
    /// silently dropped every service trigger). Null when the mirror doesn't know the parking.
    /// </summary>
    public static string? ResolveAnchorToken(IReadOnlyList<GsxParking> parkings, string gateContextKey)
        => AnchorTokenLadder(parkings, gateContextKey).FirstOrDefault();

    /// <summary>
    /// Every identity a <c>gate.select</c> re-anchor may try, in order (issue #75): which
    /// field GSX matches on is still empirically open — the display gate name worked at some
    /// stands but was refused not_found at EGLL Stand 313 (2026-08-22) while GSX's own menus
    /// carried the full "facility|gate" key. The caller walks the ladder until GSX accepts:
    /// display gate name → full uiName key → bglName → the bare designator ("313"). Whichever
    /// rung succeeds is logged, so the field teaches us the real answer.
    /// </summary>
    public static IReadOnlyList<string> AnchorTokenLadder(IReadOnlyList<GsxParking> parkings, string gateContextKey)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        var key = Normalize(gateContextKey);
        if (key.Length == 0)
        {
            return [];
        }

        var parking = parkings.FirstOrDefault(p =>
            Normalize(p.UiName) == key || Normalize(p.UiGateName) == key || Normalize(p.BglName) == key);
        if (parking is null)
        {
            return [];
        }

        // Trimmed (issue #75): the untrimmed " Gate D57" anchor token was refused not_found.
        List<string?> candidates =
        [
            parking.UiGateName,
            parking.UiName,
            parking.BglName,
            BareDesignator(parking.UiGateName),
        ];
        return [.. candidates
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => TrimToken(token!))
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>"Stand 313" → "313": the display name minus a single leading facility word —
    /// the docs' "as typed in GSX's gate search, no prefix" reading of the token contract.
    /// Null when there is no facility word to strip (nothing new to try).</summary>
    private static string? BareDesignator(string? uiGateName)
    {
        if (string.IsNullOrWhiteSpace(uiGateName))
        {
            return null;
        }

        var words = uiGateName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && KnownFacilityPrefixes.Contains(Normalize(words[0]))
            ? string.Join(' ', words[1..])
            : null;
    }

    /// <summary>
    /// The parking NUMBER to retry with after a not_found (2026-09-21): the Remote API accepts
    /// an integer parking number as a fourth identity, and it is the only one never sent in
    /// six weeks of refusals. Only a UNIQUE parking whose display/BGL name equals or ends with
    /// the requested token (normalized) qualifies — a guessed number sends the services to the
    /// wrong stand. Null when none, several, or the parking carries no number.
    /// </summary>
    public static int? NumberFallback(IReadOnlyList<GsxParking> parkings, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return null;
        }

        var matches = parkings
            .Where(p => p.Number is not null)
            .Where(p => new[] { p.UiGateName, p.UiName, p.BglName }
                .Select(Normalize)
                .Any(name => name.Length > 0 && (name == requested || name.EndsWith(requested, StringComparison.Ordinal))))
            .Select(p => p.Number!.Value)
            .Distinct()
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Nearest-name suggestions for a not_found failure: exact → suffix → contains,
    /// max 3, drawn from the mirrored parkings.</summary>
    /// <summary>The first <paramref name="count"/> distinct parking names, for the refusal
    /// diagnostics (issue #156) — enough to see the scenery's naming shape.</summary>
    public static IReadOnlyList<string> SampleNames(IReadOnlyList<GsxParking> parkings, int count)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        return parkings
            .Select(p => p.UiGateName ?? p.UiName ?? p.BglName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, count))
            .ToList();
    }

    public static IReadOnlyList<string> NearestNames(IReadOnlyList<GsxParking> parkings, string requestedGate)
    {
        ArgumentNullException.ThrowIfNull(parkings);
        var requested = Normalize(requestedGate);
        if (requested.Length == 0)
        {
            return [];
        }

        var names = parkings
            .Select(p => p.UiGateName ?? p.UiName ?? p.BglName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> ranked =
        [
            .. names.Where(n => Normalize(n) == requested),
            .. names.Where(n => Normalize(n).EndsWith(requested, StringComparison.Ordinal) && Normalize(n) != requested),
            .. names.Where(n => Normalize(n).Contains(requested, StringComparison.Ordinal)
                && !Normalize(n).EndsWith(requested, StringComparison.Ordinal)
                && Normalize(n) != requested),
        ];

        return [.. ranked.Take(3)];
    }
}
