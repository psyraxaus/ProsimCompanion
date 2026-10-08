using System.Globalization;
using System.Text.RegularExpressions;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.Weather;

namespace ProsimCompanion.Gsx.Automation;

/// <summary>
/// The de-icing auto-request decision, pure (2026-10-09): "does THIS departure call for
/// de-icing?" from the outside air temperature, the departure METAR and the
/// <c>gsx.deice.*</c> policy options — no I/O, no clock. The shell
/// (<see cref="GsxDeiceRequestService"/>) gathers the inputs, publishes the verdict on the
/// <see cref="DeiceRequestStore"/> and decision-logs it; the departure sequencer shapes its
/// step list with <see cref="EffectiveSteps"/>.
/// <para>
/// Rules (the common ground-operations shape, not any one operator's manual): the aircraft
/// is "cold" at or below <see cref="GsxDeiceOptions.OatThresholdC"/>; with
/// <see cref="GsxDeiceOptions.RequirePrecipitation"/> on, cold alone is not enough — the
/// METAR must show contamination: snow, freezing precipitation, ice pellets/hail, rain while
/// cold, freezing fog (an <c>FZ</c>-prefixed obscuration the precipitation classifier
/// deliberately reads as "none"), or a frost-likely dew-point spread of
/// <see cref="FrostSpreadMaxC"/> or less. Missing data never requests: no OAT at all, or
/// precipitation required and no METAR, is a <see cref="DeicePolicyVerdict.NoData"/>.
/// </para>
/// </summary>
public static class DeiceRequestPolicy
{
    /// <summary>Temperature/dew-point spread at or below which frost is likely on a cold
    /// airframe (the usual "active frost" rule of thumb).</summary>
    public const double FrostSpreadMaxC = 3;

    /// <summary>What the shell feeds the policy.</summary>
    /// <param name="Mode">The <c>gsx.deice.autoRequest</c> value (off | ask | auto).</param>
    /// <param name="OatThresholdC">De-icing is considered at or below this OAT.</param>
    /// <param name="RequirePrecipitation">Cold alone does not request when true.</param>
    /// <param name="ProsimOatC">ProSim's <c>aircraft.temperature.oat</c>, null when ProSim
    /// has not reported it (the subscription's raw value is null).</param>
    /// <param name="Weather">The departure airport's observation, null when none.</param>
    /// <param name="Icao">The departure airport, for the reason text.</param>
    public sealed record Inputs(
        string? Mode,
        double OatThresholdC,
        bool RequirePrecipitation,
        double? ProsimOatC,
        WxFacts? Weather,
        string? Icao);

    /// <summary>The verdict with its reason and the figures it used (session-event payload).</summary>
    public sealed record Result(
        DeicePolicyVerdict Verdict,
        string Reason,
        double? OatC,
        string Precip,
        string? Evidence);

    public static Result Evaluate(Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var mode = Mode(inputs.Mode);
        if (mode == GsxDeiceOptions.AutoRequestOff)
        {
            return new(DeicePolicyVerdict.Off, "auto-request is off (gsx.deice.autoRequest)", inputs.ProsimOatC, PrecipWord(inputs.Weather), null);
        }

        var icao = string.IsNullOrWhiteSpace(inputs.Icao) ? "the departure airport" : inputs.Icao.Trim().ToUpperInvariant();
        var precip = PrecipWord(inputs.Weather);

        // OAT: the sim's own air is the truth while ProSim reports it; the METAR is the
        // fallback (a METAR fetched for the OFP origin while the sim sits elsewhere is wrong
        // either way, but it is the best figure left).
        var (oat, oatSource) = inputs.ProsimOatC is { } prosim
            ? (prosim, "ProSim OAT")
            : inputs.Weather?.TemperatureC is { } metarTemp
                ? (metarTemp, $"{icao} METAR")
                : ((double?)null, "");
        if (oat is null)
        {
            return new(DeicePolicyVerdict.NoData, $"no outside air temperature yet (ProSim not reporting, no METAR for {icao})", null, precip, null);
        }

        // Whole degrees in the reason: the shell dedupes its log on the reason text, and a
        // live OAT drifting by tenths must not re-log the same verdict.
        var oatText = $"OAT {Math.Round(oat.Value).ToString("0", CultureInfo.InvariantCulture)} °C ({oatSource})";
        var threshold = inputs.OatThresholdC.ToString("0.#", CultureInfo.InvariantCulture);
        if (oat.Value > inputs.OatThresholdC)
        {
            return new(DeicePolicyVerdict.NotRequired, $"{oatText} is above the {threshold} °C threshold", oat, precip, null);
        }

        if (!inputs.RequirePrecipitation)
        {
            return Decide(mode, $"{oatText} is at or below the {threshold} °C threshold; precipitation not required by policy", oat, precip, "cold OAT");
        }

        if (inputs.Weather is null || string.IsNullOrWhiteSpace(inputs.Weather.RawMetar))
        {
            return new(DeicePolicyVerdict.NoData, $"{oatText} is at or below the {threshold} °C threshold but there is no METAR for {icao} — precipitation unknown, no request", oat, precip, null);
        }

        var evidence = Contamination(inputs.Weather, oat.Value, inputs.OatThresholdC);
        if (evidence is null)
        {
            return new(DeicePolicyVerdict.NotRequired, $"{oatText} is at or below the {threshold} °C threshold but {icao} reports no precipitation, freezing fog or frost-likely spread", oat, precip, null);
        }

        return Decide(mode, $"{oatText} with {evidence} at {icao}", oat, precip, evidence);
    }

    /// <summary>The captain's question for an <c>ask</c> verdict.</summary>
    public static string Prompt(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var oat = result.OatC is { } o ? $" OAT {Math.Round(o).ToString("0", CultureInfo.InvariantCulture)}" : "";
        var evidence = string.IsNullOrWhiteSpace(result.Evidence) ? "" : $", {result.Evidence}";
        return $"Captain, conditions call for de-icing —{oat}{evidence}. Request it?";
    }

    /// <summary>
    /// The departure step list for this cycle. With de-icing requested (an <c>auto</c>
    /// verdict or the captain's "yes"), or while the captain's question is still OPEN, the
    /// list carries a <c>DeIce</c> step as its LAST entry with
    /// <see cref="GsxServiceActivation.AfterAllCompleted"/>: a configured step with
    /// activation Skip is lifted out of its place and re-added at the end, and a list without
    /// one gets the step appended. Last on purpose — de-icing is the final thing before
    /// pushback, and an open question must only hold the departure's "all done", never a
    /// boarding configured behind a mid-list DeIce row (the shell's <c>preHold</c> keeps an
    /// unanswered step unsettled, exactly like the fuel confirmation). A step the pilot
    /// configured with any other activation stays where and as it is (their order wins).
    /// Every other state returns the configured list untouched, so a declined or
    /// not-required de-ice changes nothing. Fresh instances — the configured steps are never
    /// mutated.
    /// </summary>
    public static IReadOnlyList<DepartureServiceStep> EffectiveSteps(
        IReadOnlyList<DepartureServiceStep> configured,
        DeiceRequestSnapshot deice,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(deice);

        var place = deice.RequestThisCycle || deice.QuestionOpen(nowUtc);
        if (!place)
        {
            return configured;
        }

        var result = new List<DepartureServiceStep>(configured.Count + 1);
        var kept = false;
        foreach (var step in configured)
        {
            if (!step.Service.Equals(GsxServiceIds.DeIce, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(step);
                continue;
            }

            if (step.Activation != GsxServiceActivation.Skip)
            {
                result.Add(step);
                kept = true;
            }
        }

        if (!kept)
        {
            result.Add(new DepartureServiceStep(GsxServiceIds.DeIce, GsxServiceActivation.AfterAllCompleted));
        }

        return result;
    }

    /// <summary>The sequencer's hold reason for the DeIce step while the captain's question
    /// stands; null otherwise. Probed by <c>gsx-deice-policy</c> — keep the wording stable.</summary>
    public static string? HoldReason(DeiceRequestSnapshot deice, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(deice);
        return deice.QuestionOpen(nowUtc) && !deice.RequestThisCycle
            ? "waiting for the captain's answer — conditions call for de-icing (say 'request de-icing' / 'negative', or use the Status board)"
            : null;
    }

    /// <summary>The sequencer's skip reason for a DeIce step the captain declined; null
    /// otherwise. A declined step the pilot had configured as a normal step is skipped too —
    /// the question was asked precisely because the policy saw de-icing conditions.</summary>
    public static string? SkipReason(DeiceRequestSnapshot deice)
    {
        ArgumentNullException.ThrowIfNull(deice);
        return deice.Declined is { } declined && !deice.RequestThisCycle
            ? $"de-icing declined: {declined}"
            : null;
    }

    private static Result Decide(string mode, string reason, double? oat, string precip, string evidence)
        => mode == GsxDeiceOptions.AutoRequestAuto
            ? new(DeicePolicyVerdict.Request, reason + " — requesting de-icing (auto)", oat, precip, evidence)
            : new(DeicePolicyVerdict.Ask, reason + " — asking the captain", oat, precip, evidence);

    private static string Mode(string? mode)
        => string.Equals(mode, GsxDeiceOptions.AutoRequestAuto, StringComparison.OrdinalIgnoreCase) ? GsxDeiceOptions.AutoRequestAuto
            : string.Equals(mode, GsxDeiceOptions.AutoRequestAsk, StringComparison.OrdinalIgnoreCase) ? GsxDeiceOptions.AutoRequestAsk
            : GsxDeiceOptions.AutoRequestOff;

    private static string PrecipWord(WxFacts? weather) => weather is null || string.IsNullOrWhiteSpace(weather.RawMetar)
        ? "unknown"
        : weather.Precip switch
        {
            PrecipKind.None => FreezingFog(weather.RawMetar) ? "freezing fog" : "none",
            PrecipKind.Rain => "rain",
            PrecipKind.Snow => "snow",
            PrecipKind.Thunderstorm => "thunderstorm",
            PrecipKind.Freezing => "freezing precipitation",
            _ => "ice pellets / hail",
        };

    /// <summary>The contamination the METAR shows for a cold airframe, as a short phrase;
    /// null when none. Rain counts only because the OAT is already at or below the threshold
    /// (the caller checked) — rain on a cold-soaked wing is the classic clear-ice case.</summary>
    private static string? Contamination(WxFacts weather, double oat, double thresholdC)
    {
        switch (weather.Precip)
        {
            case PrecipKind.Snow:
                return "snow";
            case PrecipKind.Freezing:
                return "freezing precipitation";
            case PrecipKind.Other:
                return "ice pellets or hail";
            case PrecipKind.Thunderstorm:
            case PrecipKind.Rain:
                return $"rain at {Math.Round(oat).ToString("0", CultureInfo.InvariantCulture)} °C (at or below {thresholdC.ToString("0.#", CultureInfo.InvariantCulture)} °C)";
        }

        if (FreezingFog(weather.RawMetar))
        {
            return "freezing fog";
        }

        if (weather.TemperatureC is { } t && weather.DewPointC is { } dp && t - dp <= FrostSpreadMaxC)
        {
            return $"frost likely (temperature/dew point {t}/{dp}, spread {t - dp} °C)";
        }

        return null;
    }

    /// <summary>An <c>FZ</c>-prefixed obscuration (FZFG, FZBR) in the pre-remarks body —
    /// supercooled fog contaminates a cold wing without any precipitation group.</summary>
    private static bool FreezingFog(string? metar)
    {
        if (string.IsNullOrWhiteSpace(metar))
        {
            return false;
        }

        var body = metar;
        var rmk = body.IndexOf(" RMK ", StringComparison.OrdinalIgnoreCase);
        if (rmk >= 0)
        {
            body = body[..rmk];
        }

        return Regex.IsMatch(body, @"(?<![A-Z])[+-]?FZ(?:FG|BR)\b");
    }
}
