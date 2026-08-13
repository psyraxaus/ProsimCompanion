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
/// The phrase table lives in <see cref="GsxVoicePhrases"/>, shared with the hail dialogues.
/// Since ADR-0006 "cockpit to ground" is the crew hail (<see cref="Crew.CrewHailService"/>);
/// "commence ground services" / "start ground services" start the departure sequence here.
/// Gated by <see cref="GsxOptions.VoiceControlEnabled"/>.
/// </summary>
public sealed class GsxVoiceService : IVoiceFeature
{
    private const string VoiceTag = "gsx.voice";

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

    public IEnumerable<string> Phrases
        => GsxVoicePhrases.StartGroundServicesPhrases.Concat(GsxVoicePhrases.Bindings.Keys);

    public bool ValueParse => false;

    public bool TryHandle(string utterance)
    {
        if (!_options.CurrentValue.VoiceControlEnabled || string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        if (GsxVoicePhrases.StartGroundServicesPhrases.Any(
                p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            _ = HandleGroundServicesAsync();
            return true;
        }

        if (!GsxVoicePhrases.Bindings.TryGetValue(text, out var binding))
        {
            return false;
        }

        _ = DispatchAsync(binding);
        return true;
    }

    /// <summary>"Commence ground services": start the departure sequence, or advance it when
    /// it is already running. When the departure seam is absent the start command is fired
    /// anyway — with an AlreadySatisfied answer falling back to force-next.</summary>
    private async Task HandleGroundServicesAsync()
    {
        try
        {
            if (_departureControl is { Started: true })
            {
                await DispatchCoreAsync(GsxVoicePhrases.Bindings["next service"]).ConfigureAwait(false);
                return;
            }

            var result = await ExecuteAsync("gsx.startDepartureServices").ConfigureAwait(false);
            if (result.Outcome == CommandOutcome.AlreadySatisfied)
            {
                await DispatchCoreAsync(GsxVoicePhrases.Bindings["next service"]).ConfigureAwait(false);
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

    private async Task DispatchAsync(GsxVoiceBinding binding)
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

    private async Task DispatchCoreAsync(GsxVoiceBinding binding)
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
                Chime: "cabin",
                Role: SpeechRole.Purser));
        }
    }

    /// <summary>Shared with the hail dialogues so both paths execute identically.</summary>
    internal Task<CommandResult> ExecuteAsync(string command)
        => _registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(command, new EmptyCommandRequest());

    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(
            text,
            SpeechPriority.Normal,
            Ttl: TimeSpan.FromMinutes(1),
            Tag: VoiceTag));
}
