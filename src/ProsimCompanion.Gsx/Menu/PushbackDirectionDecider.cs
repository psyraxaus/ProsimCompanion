using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Menu;

public enum PushbackDecisionKind
{
    /// <summary>Pick <see cref="PushbackDecision.Entry"/>.</summary>
    Pick,

    /// <summary>Leave the menu open and have the FO ask the pilot.</summary>
    Ask,

    /// <summary>Leave the menu open, silently (reason logged).</summary>
    Leave,
}

/// <summary>What to do with GSX's "Select pushback direction" menu.</summary>
public sealed record PushbackDecision(PushbackDecisionKind Kind, string? Entry, string Reason, IReadOnlyList<PushbackOption> Options);

/// <summary>
/// Pure: the menu lines GSX shows + the flight's pushback state + the configured mode →
/// pick / ask / leave. Precedence (2026-10-04): the pilot's choice for this flight (voice,
/// OFP Korry buttons, API) → the fixed legacy preference when configured → the advisor's
/// confident suggestion (mode auto) → ask (mode ask, or auto when unsure and asking is on) →
/// leave. A menu with a single direction line is picked in every mode but "ask".
/// </summary>
public static class PushbackDirectionDecider
{
    public static PushbackDecision Decide(
        IReadOnlyList<string> menuEntries,
        string preference,
        bool askWhenUnsure,
        PushbackChoiceSnapshot? state)
    {
        ArgumentNullException.ThrowIfNull(menuEntries);
        var known = state?.Options ?? [];
        var options = LiveOptions(menuEntries, known);
        var turning = options.Where(o => o.Kind != PushbackOptionKind.Straight).ToList();
        var mode = (preference ?? "").Trim();

        // 1. The pilot said so.
        if (state?.Choice is { } choice)
        {
            var match = PushbackAdvisor.Match(options, choice);
            if (match is not null)
            {
                return new PushbackDecision(PushbackDecisionKind.Pick, match.Label, $"pilot chose {choice.Spoken} ({choice.Source}): '{match.Label}'", options);
            }

            var why = $"pilot chose {choice.Spoken} ({choice.Source}) but no menu line fits";
            return askWhenUnsure
                ? new PushbackDecision(PushbackDecisionKind.Ask, null, why, options)
                : new PushbackDecision(PushbackDecisionKind.Leave, null, why, options);
        }

        // 2. The old fixed answers.
        if (mode is "tailLeft" or "tailRight" or "straight")
        {
            var legacy = PushbackDirectionResolver.Resolve(menuEntries, mode);
            return legacy is null
                ? new PushbackDecision(PushbackDecisionKind.Leave, null, $"no entry matches preference '{mode}'", options)
                : new PushbackDecision(PushbackDecisionKind.Pick, legacy.Entry, $"preference {mode} ({legacy.Strategy})", options);
        }

        // 3. Ask, always.
        if (mode == "ask")
        {
            return new PushbackDecision(PushbackDecisionKind.Ask, null, "mode ask", options);
        }

        // 4. Auto.
        if (turning.Count == 1)
        {
            return new PushbackDecision(PushbackDecisionKind.Pick, turning[0].Label, "the only direction offered", options);
        }

        if (state?.Suggestion is { Confidence: PushbackConfidence.High } suggestion)
        {
            // The suggested line by its exact label first (custom-labelled stands), then by
            // the slot kind (GSX's default lines).
            var match = PushbackAdvisor.Match(options, PushbackChoice.NamedSlot(suggestion.Option.Label, "suggestion", suggestion.Reason))
                ?? PushbackAdvisor.Match(options, suggestion.AsChoice("suggestion"))
                ?? options.FirstOrDefault(o => o.Kind == suggestion.Option.Kind && o.Kind is PushbackOptionKind.Left or PushbackOptionKind.Right);
            if (match is not null)
            {
                return new PushbackDecision(PushbackDecisionKind.Pick, match.Label, $"suggested — {suggestion.Reason}", options);
            }
        }

        var reason = state?.Suggestion is { } low
            ? $"suggestion not confident — {low.Reason}"
            : "no suggestion (stand or departure runway unknown)";
        return askWhenUnsure
            ? new PushbackDecision(PushbackDecisionKind.Ask, null, reason, options)
            : new PushbackDecision(PushbackDecisionKind.Leave, null, reason, options);
    }

    /// <summary>The live menu lines as options, with the headings the advisor already knows
    /// for lines of the same label (profile routes) carried across.</summary>
    public static IReadOnlyList<PushbackOption> LiveOptions(IReadOnlyList<string> menuEntries, IReadOnlyList<PushbackOption> known)
    {
        ArgumentNullException.ThrowIfNull(menuEntries);
        ArgumentNullException.ThrowIfNull(known);
        var live = PushbackAdvisor.Options(stand: null, menuEntries);
        if (known.Count == 0)
        {
            return live;
        }

        return live.Select(option =>
        {
            var normalized = ParkingText.Normalize(option.Label);
            var byLabel = known.FirstOrDefault(k => ParkingText.Normalize(k.Label) == normalized);
            var byKind = option.Kind is PushbackOptionKind.Left or PushbackOptionKind.Right
                ? known.FirstOrDefault(k => k.Kind == option.Kind)
                : null;
            var source = byLabel ?? byKind;
            var enriched = option;
            if (byLabel is not null && option.Kind == PushbackOptionKind.Additional && byLabel.Kind is PushbackOptionKind.Left or PushbackOptionKind.Right)
            {
                // The profile's LEFT/RIGHT slot wears a custom label ("Facing SW on Taxi AV"):
                // the live line is that slot, so "tail left" can still select it.
                enriched = enriched with { Kind = byLabel.Kind };
            }

            return enriched.FinalHeadingDeg is null && source?.FinalHeadingDeg is not null
                ? enriched with { FinalHeadingDeg = source.FinalHeadingDeg, HeadingSource = source.HeadingSource }
                : enriched;
        }).ToList();
    }
}
