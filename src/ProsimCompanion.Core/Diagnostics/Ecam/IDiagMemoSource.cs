using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Diagnostics.Metrics;
using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>The facts every page's memo strip shows that no collector owns: the build, the
/// LLM model in use, how long the app has been up and the flight phase. The counts and the
/// bundle flag are filled in by the snapshot core.</summary>
public interface IDiagMemoSource
{
    /// <summary>The memo with its counts at zero.</summary>
    DiagMemo Read(DateTimeOffset now);
}

/// <summary>Core's implementation over the build info, the uptime tracker, the flight-state
/// engine and the briefing options (where the LLM model id lives on this branch).</summary>
public sealed class DiagMemoSource : IDiagMemoSource
{
    private readonly IAppBuildInfo _build;
    private readonly UptimeTracker _uptime;
    private readonly IFlightPhaseSource _phase;
    private readonly IOptionsMonitor<BriefingOptions> _briefing;

    public DiagMemoSource(
        IAppBuildInfo build,
        UptimeTracker uptime,
        IFlightPhaseSource phase,
        IOptionsMonitor<BriefingOptions> briefing)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(uptime);
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(briefing);
        _build = build;
        _uptime = uptime;
        _phase = phase;
        _briefing = briefing;
    }

    public DiagMemo Read(DateTimeOffset now)
    {
        var options = _briefing.CurrentValue;
        var model = options.LlmEnabled && !string.IsNullOrWhiteSpace(options.LlmModel)
            ? options.LlmModel.Trim()
            : "";
        return new DiagMemo(
            _build.Version,
            model,
            _uptime.Elapsed(now),
            PhaseText(_phase.CurrentPhase),
            0,
            0,
            BundleReady: true);
    }

    /// <summary>The phase as the ECAM strip prints it: upper-case words, no camel case.</summary>
    public static string PhaseText(FlightPhase phase)
    {
        var name = phase.ToString();
        var text = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ');
            }

            text.Append(char.ToUpperInvariant(c));
        }

        return text.ToString();
    }
}
