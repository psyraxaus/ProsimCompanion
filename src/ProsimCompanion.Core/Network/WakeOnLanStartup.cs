using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Hosting;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Network;

/// <summary>
/// Sends the startup magic packets — one per enabled <c>wakeOnLan.targets</c> row, plus the
/// legacy <c>briefing.llmWakeOnLan</c> block while a config still carries it enabled (the
/// Setup page moves it into the list on its first visit). Fire-and-forget: WoL is
/// connectionless UDP, so each feature's own readiness probe says when its PC is really up.
/// Also the <see cref="IWakeOnLanControl"/> seam behind the page's "Send now" buttons.
/// Replaced the Speech project's <c>LlmWakeOnLanStartup</c> on 2026-10-08.
/// </summary>
public sealed class WakeOnLanStartup : IStartupModule, IWakeOnLanControl
{
    private readonly IOptionsMonitor<WakeTargetsOptions> _targets;
    private readonly IOptionsMonitor<BriefingOptions> _briefing;
    private readonly ILogger<WakeOnLanStartup> _logger;

    public WakeOnLanStartup(
        IOptionsMonitor<WakeTargetsOptions> targets,
        IOptionsMonitor<BriefingOptions> briefing,
        ILogger<WakeOnLanStartup> logger)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(logger);
        _targets = targets;
        _briefing = briefing;
        _logger = logger;
    }

    public void Start()
    {
        foreach (var target in _targets.CurrentValue.Targets.Where(t => t.Enabled))
        {
            WakeOnLan.Send(target.MacAddress, target.BroadcastAddress, target.Port, _logger);
        }

        var legacy = _briefing.CurrentValue.LlmWakeOnLan;
        if (legacy.Enabled)
        {
            _logger.LogInformation(
                "briefing.llmWakeOnLan is the old single-PC form — open Settings → Setup → Wake-on-LAN once to move it into the list");
            WakeOnLan.Send(legacy.MacAddress, legacy.BroadcastAddress, legacy.Port, _logger);
        }
    }

    /// <inheritdoc/>
    public string Send(WakeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WakeOnLan.Send(target.MacAddress, target.BroadcastAddress, target.Port, _logger);
    }

    /// <inheritdoc/>
    public string SendAll()
    {
        var sent = 0;
        var problems = new List<string>();
        foreach (var target in _targets.CurrentValue.Targets.Where(t => t.Enabled))
        {
            var outcome = Send(target);
            if (outcome.StartsWith("Magic packet sent", StringComparison.Ordinal))
            {
                sent++;
            }
            else
            {
                problems.Add($"{(target.Name.Length > 0 ? target.Name : target.MacAddress)}: {outcome}");
            }
        }

        var legacy = _briefing.CurrentValue.LlmWakeOnLan;
        if (legacy.Enabled)
        {
            var outcome = WakeOnLan.Send(legacy.MacAddress, legacy.BroadcastAddress, legacy.Port, _logger);
            if (outcome.StartsWith("Magic packet sent", StringComparison.Ordinal))
            {
                sent++;
            }
            else
            {
                problems.Add("language-model PC (old setting): " + outcome);
            }
        }

        if (sent == 0 && problems.Count == 0)
        {
            return "No PC is set up for Wake-on-LAN — add one on Settings → Setup.";
        }

        var line = sent == 1 ? "Magic packet sent to 1 PC" : $"Magic packets sent to {sent} PCs";
        return problems.Count == 0
            ? line + " — give them a minute to boot."
            : line + "; " + string.Join("; ", problems);
    }
}
