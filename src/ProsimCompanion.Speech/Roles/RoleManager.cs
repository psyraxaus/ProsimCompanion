using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Roles;

/// <summary>
/// PF/PM role state. The FO acts on the FCU/MCDU/radios only while it is pilot flying.
/// Handover by voice: "you have control"/"your aircraft" gives the FO control; taking it back
/// ("my aircraft"/"i have control"/…) is instant and ungated (predecessor rule — the human
/// can always reclaim the aircraft). The predecessor's airborne confirm handshake is deferred
/// with the recognition leftovers. Defaults to the USER flying.
/// </summary>
public sealed class RoleManager : IVoiceFeature
{
    private static readonly string[] GiveControl = ["you have control", "your aircraft", "your controls"];
    private static readonly string[] TakeControl =
        ["my aircraft", "i have control", "i have the aircraft", "i have the controls"];

    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<RoleManager> _logger;

    public RoleManager(ISpeechArbiter arbiter, JsonlEventLog eventLog, ILogger<RoleManager> logger)
    {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    /// <summary>Raised on every handover (any thread).</summary>
    public event Action? Changed;

    public bool IsFoPilotFlying { get; private set; }

    public IEnumerable<string> Phrases => [.. GiveControl, .. TakeControl];

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (GiveControl.Contains(text))
        {
            SetFoFlying(true);
            _ = _arbiter.SpeakAsync("I have control.", SpeechPriority.High);
            return true;
        }

        if (TakeControl.Contains(text))
        {
            SetFoFlying(false);
            _ = _arbiter.SpeakAsync("You have control.", SpeechPriority.High);
            return true;
        }

        return false;
    }

    private void SetFoFlying(bool foFlying)
    {
        if (IsFoPilotFlying == foFlying)
        {
            return;
        }

        IsFoPilotFlying = foFlying;
        _logger.LogInformation("Pilot flying: {Who}", foFlying ? "FO" : "user");
        _eventLog.Record("roles.handover", new { foFlying });
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Role change handler threw");
        }
    }
}
