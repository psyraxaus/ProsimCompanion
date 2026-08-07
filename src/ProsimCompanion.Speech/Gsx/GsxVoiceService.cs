using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Gsx;

/// <summary>
/// Voice control for GSX ground services (community request): exact-match phrases dispatched
/// into the <see cref="CommandRegistry"/> — the same named commands the web UI, HTTP API and
/// Stream Deck use, so voice inherits every guard of the serialized trigger path and never
/// grows a second write path. Each outcome is spoken briefly through the arbiter
/// (tag <c>gsx.voice</c>); the cabin-flavored boarding phrases additionally acknowledge as the
/// cabin crew (tag <c>cabin.boarding.ack</c>) when boarding is actually underway.
/// Gated by <see cref="GsxOptions.VoiceControlEnabled"/>.
/// </summary>
public sealed class GsxVoiceService : IVoiceFeature
{
    private const string VoiceTag = "gsx.voice";

    /// <summary>One phrase → command mapping. <see cref="SuccessPhrase"/> is spoken on
    /// Success; every other outcome speaks the command's reason.</summary>
    private sealed record PhraseBinding(string Command, string SuccessPhrase, bool CabinAck = false);

    private static readonly Dictionary<string, PhraseBinding> Bindings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["call the next service"] = new("gsx.forceNextService", "Calling the next service."),
        ["next service"] = new("gsx.forceNextService", "Calling the next service."),
        ["request boarding"] = new("gsx.requestBoarding", "Boarding requested."),
        ["start boarding"] = new("gsx.requestBoarding", "Boarding requested.", CabinAck: true),
        ["cabin crew start boarding"] = new("gsx.requestBoarding", "Boarding requested.", CabinAck: true),
        ["request refueling"] = new("gsx.requestRefuel", "Refueling requested."),
        ["call the fuel truck"] = new("gsx.requestRefuel", "Refueling requested."),
        ["request catering"] = new("gsx.requestCatering", "Catering requested."),
        ["request pushback"] = new("gsx.requestPushback", "Pushback requested."),
        ["request de-icing"] = new("gsx.requestDeice", "De-icing requested."),
    };

    /// <summary>Phrases that resolve to start-or-advance at dispatch time
    /// (<see cref="HandleGroundServicesAsync"/>) rather than to a single fixed command.</summary>
    private static readonly string[] GroundServicesPhrases = ["cockpit to ground", "start ground services"];

    private readonly CommandRegistry _registry;
    private readonly ISpeechArbiter _arbiter;
    private readonly IGsxDepartureControl? _departureControl;
    private readonly IOptionsMonitor<GsxOptions> _options;
    private readonly ILogger<GsxVoiceService> _logger;

    public GsxVoiceService(
        CommandRegistry registry,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GsxOptions> options,
        ILogger<GsxVoiceService> logger,
        IGsxDepartureControl? departureControl = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _arbiter = arbiter;
        _options = options;
        _logger = logger;
        _departureControl = departureControl;
    }

    public IEnumerable<string> Phrases => GroundServicesPhrases.Concat(Bindings.Keys);

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        if (!_options.CurrentValue.VoiceControlEnabled || string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        if (GroundServicesPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            _ = HandleGroundServicesAsync();
            return true;
        }

        if (!Bindings.TryGetValue(text, out var binding))
        {
            return false;
        }

        _ = DispatchAsync(binding);
        return true;
    }

    /// <summary>"Cockpit to ground": start the departure sequence, or advance it when it is
    /// already running. When the departure seam is absent the start command is fired anyway —
    /// with an AlreadySatisfied answer falling back to force-next.</summary>
    private async Task HandleGroundServicesAsync()
    {
        try
        {
            if (_departureControl is { Started: true })
            {
                await DispatchCoreAsync(new("gsx.forceNextService", "Calling the next service.")).ConfigureAwait(false);
                return;
            }

            var result = await ExecuteAsync("gsx.startDepartureServices").ConfigureAwait(false);
            if (result.Outcome == CommandOutcome.AlreadySatisfied)
            {
                await DispatchCoreAsync(new("gsx.forceNextService", "Calling the next service.")).ConfigureAwait(false);
                return;
            }

            Speak(result.Outcome == CommandOutcome.Success ? "Ground services started." : result.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice ground-services dispatch failed");
            Speak("That did not work — see the log.");
        }
    }

    private async Task DispatchAsync(PhraseBinding binding)
    {
        try
        {
            await DispatchCoreAsync(binding).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice command {Command} dispatch failed", binding.Command);
            Speak("That did not work — see the log.");
        }
    }

    private async Task DispatchCoreAsync(PhraseBinding binding)
    {
        var result = await ExecuteAsync(binding.Command).ConfigureAwait(false);
        _logger.LogInformation(
            "Voice GSX command {Command} → {Outcome}: {Reason}",
            binding.Command,
            result.Outcome,
            result.Reason);

        Speak(result.Outcome == CommandOutcome.Success ? binding.SuccessPhrase : result.Reason);

        // Cabin-flavored phrases: the crew acknowledges only when boarding is really on
        // (freshly requested, or already running) — never after a refusal.
        if (binding.CabinAck
            && result.Outcome is CommandOutcome.Success or CommandOutcome.AlreadySatisfied)
        {
            _ = _arbiter.EnqueueAsync(new SpeechRequest(
                "Boarding underway.",
                SpeechPriority.Normal,
                Ttl: TimeSpan.FromMinutes(1),
                Tag: "cabin.boarding.ack",
                Chime: "cabin"));
        }
    }

    private Task<CommandResult> ExecuteAsync(string command)
        => _registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(command, new EmptyCommandRequest());

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text,
            SpeechPriority.Normal,
            Ttl: TimeSpan.FromMinutes(1),
            Tag: VoiceTag));
}
