namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The de-icing auto-request policy (<c>gsx.deice.*</c>, 2026-10-09). Until now only GSX could
/// start a de-ice (its "Ice warning" offer, answered per <see cref="GsxOptions.AutoDeIce"/>) or
/// the crew (voice / <c>gsx.requestDeice</c>). This policy lets the app decide from the
/// outside air temperature and the departure METAR whether THIS departure needs it, and either
/// put de-icing on the departure sequence itself or ask the captain first. Pure decision in
/// <c>DeiceRequestPolicy</c>; every verdict is decision-logged.
/// </summary>
public sealed class GsxDeiceOptions
{
    /// <summary><c>"off"</c> (default) never requests; <c>"ask"</c> raises a crew question
    /// ("Captain, conditions call for de-icing — request it?") the FO voices and the Status
    /// board shows; <c>"auto"</c> puts the DeIce step on the departure sequence for this cycle
    /// without asking. Unknown values read as off.</summary>
    public string AutoRequest { get; set; } = AutoRequestOff;

    /// <summary>De-icing is considered at or below this outside air temperature (°C). The
    /// ProSim OAT dataref is used when ProSim reports one, else the METAR temperature.</summary>
    public double OatThresholdC { get; set; } = 3;

    /// <summary>With this on (default), a cold OAT alone is not enough: the departure METAR
    /// must also show precipitation (snow, freezing precipitation, ice pellets, rain at or
    /// below the threshold), freezing fog, or a frost-likely dew-point spread (≤ 3 °C). Off
    /// requests on the temperature alone — the "cold-soaked wing" rule some operators use.
    /// With no METAR for the departure the policy makes no request at all (no data, no call).</summary>
    public bool RequirePrecipitation { get; set; } = true;

    /// <summary>When the policy runs and the captain is asked (issue #166, EFHK 2026-10-10:
    /// the question fired one millisecond after "departure sequence started" — before ground
    /// prep, before the OFP, sixteen minutes before boarding — and nobody was ready for it).
    /// <c>"whenStepIsNext"</c> (default): when the DeIce step's turn comes on the departure
    /// sequence — every earlier service done, the OFP loaded, the weather known.
    /// <c>"afterBoardingComplete"</c>: on the boarding-completed edge, so the captain decides
    /// while the last services finish; the question is re-asked once when the step's turn
    /// comes if it is still unanswered.</summary>
    public string AskWhen { get; set; } = AskWhenStepIsNext;

    /// <summary>How long an unanswered question stands before it counts as declined (source
    /// <c>timeout</c>). Silence from the FO's listen never declines — only this expiry does;
    /// the Status board buttons and "request de-icing" / "no de-icing" answer it meanwhile.</summary>
    public int QuestionTimeoutMinutes { get; set; } = 5;

    /// <summary>Off by default. On, the FO runs the cockpit side of the treatment the way
    /// real crews do: at the de-icing crew's Requested/Active edge "De-icing starting — packs
    /// off, ditching on", twenty seconds later a read-only check of the pack OFF lights and
    /// the DITCHING ON light with one reminder if they do not agree; at the Completed edge
    /// "De-icing complete — packs on, ditching off" with the same check. The FO never moves
    /// those switches.</summary>
    public bool PacksProcedure { get; set; }

    public const string AutoRequestOff = "off";
    public const string AutoRequestAsk = "ask";
    public const string AutoRequestAuto = "auto";

    public const string AskWhenStepIsNext = "whenStepIsNext";
    public const string AskWhenAfterBoardingComplete = "afterBoardingComplete";

    /// <summary>True for <see cref="AskWhenAfterBoardingComplete"/>; every other value (the
    /// default, a typo) reads as <see cref="AskWhenStepIsNext"/>.</summary>
    public static bool AsksAfterBoarding(string? askWhen)
        => string.Equals(askWhen, AskWhenAfterBoardingComplete, StringComparison.OrdinalIgnoreCase);

    /// <summary>The question TTL the options describe, floored at one minute so a zero in the
    /// file can never decline a question the moment it is asked.</summary>
    public TimeSpan QuestionTtl => TimeSpan.FromMinutes(Math.Max(1, QuestionTimeoutMinutes));
}
