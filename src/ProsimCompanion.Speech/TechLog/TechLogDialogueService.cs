using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Sessions;
using ProsimCompanion.Core.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.TechLog;

/// <summary>
/// Voice entry points for the guided tech-log dialogues: the raise/rectify command phrases
/// (<see cref="IVoiceFeature"/>, exact match — phrases carried verbatim from Prosim2FO) and the
/// post-abnormal shutdown offer (<see cref="ISessionFinalizationStep"/>, Order 40 — after the
/// tech-log fold at 30 so offers see this session's folded store). Dialogues run on a
/// background task under an exclusive <see cref="IMicOwnership"/> borrow whose using-disposal
/// restores normal routing on every path, including exceptions. Every prompt goes through the
/// arbiter (Normal, tag "techlog") and is awaited to its terminal outcome before the next
/// listen window opens. Gated on <c>techLog.enabled</c>; needs no Start — dispatch and
/// finalization both arrive via DI collections.
/// </summary>
public sealed class TechLogDialogueService : IVoiceFeature, ISessionFinalizationStep, ITechLogDialogueIo, IDisposable
{
    private static readonly string[] RaisePhrases =
        ["log a defect", "raise a defect", "enter a defect", "log a snag"];

    private static readonly string[] RectifyPhrases =
    [
        "maintenance complete", "maintenance performed", "defect rectified",
        "clear the tech log", "rectify the defect",
    ];

    private static readonly string[] AllPhrases = [.. RaisePhrases, .. RectifyPhrases];

    private readonly TechLogVoiceService _voice;
    private readonly ISpeechArbiter _arbiter;
    private readonly IMicOwnership _mic;
    private readonly IOptionsMonitor<TechLogOptions> _options;
    private readonly ILogger<TechLogDialogueService> _logger;
    private readonly TechLogDialogueCore _core;
    private readonly SemaphoreSlim _oneDialogue = new(1, 1);

    public TechLogDialogueService(
        ITechLogService techLog,
        TechLogVoiceService voice,
        ISpeechArbiter arbiter,
        IMicOwnership mic,
        IOptionsMonitor<TechLogOptions> options,
        JsonlEventLog eventLog,
        ILogger<TechLogDialogueService> logger)
    {
        ArgumentNullException.ThrowIfNull(techLog);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _voice = voice;
        _arbiter = arbiter;
        _mic = mic;
        _options = options;
        _logger = logger;
        _core = new TechLogDialogueCore(techLog, this, eventLog);
    }

    public IEnumerable<string> Phrases => AllPhrases;

    public bool ValueParse => false;

    string ISessionFinalizationStep.Name => "techlog-offer";

    int ISessionFinalizationStep.Order => 40;

    public bool TryHandle(string utterance)
    {
        if (!_options.CurrentValue.Enabled || string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        if (RaisePhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(() => RunDialogueAsync("raise", _core.RunRaiseAsync));
            return true;
        }

        if (RectifyPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(() => RunDialogueAsync("rectify", _core.RunRectifyAsync));
            return true;
        }

        return false;
    }

    /// <summary>Launches (never awaits) the post-abnormal offers, so session finalization
    /// records its completion without waiting on a voice exchange.</summary>
    Task ISessionFinalizationStep.RunAsync(SessionFinalizationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = _options.CurrentValue;
        if (!options.Enabled || !options.OfferFromAbnormalAtShutdown)
        {
            return Task.CompletedTask;
        }

        var abnormals = _voice.FiredAbnormals.ToList();
        if (abnormals.Count == 0)
        {
            return Task.CompletedTask;
        }

        // Deliberately NOT the finalizer's token: the offer outlives the finalization pass.
        _ = Task.Run(
            () => RunDialogueAsync(
                "offer", ct => _core.RunAbnormalOffersAsync(abnormals, context.SessionId, ct)),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public void Dispose() => _oneDialogue.Dispose();

    /// <summary>One dialogue at a time (raise/rectify/offer share the mic); the borrow's
    /// using-disposal restores routing before the failure path logs, so an exception can never
    /// leave the mic held.</summary>
    private async Task RunDialogueAsync(string name, Func<CancellationToken, Task> dialogue)
    {
        await _oneDialogue.WaitAsync().ConfigureAwait(false);
        try
        {
            using (_mic.Borrow("techlog:" + name))
            {
                await dialogue(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tech-log {Dialogue} dialogue failed", name);
        }
        finally
        {
            _oneDialogue.Release();
        }
    }

    // ---- ITechLogDialogueIo (the core's speak/listen seam, adapted onto arbiter + mic) ----

    async Task ITechLogDialogueIo.SpeakAsync(string text, CancellationToken cancellationToken)
        => await _arbiter.EnqueueAsync(
                new SpeechRequest(text, SpeechPriority.Normal, Tag: "techlog"), cancellationToken)
            .ConfigureAwait(false);

    Task<string?> ITechLogDialogueIo.ListenAsync(
        IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken)
        => _mic.ListenAsync(grammar, timeout, cancellationToken);
}
