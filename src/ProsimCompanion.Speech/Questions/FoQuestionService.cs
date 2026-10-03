using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Speech;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Questions;

/// <summary>What one question came to — for the session event and the tests.</summary>
public enum FoAnswerOutcome
{
    /// <summary>The model's answer was spoken (streamed, verified sentence by sentence).</summary>
    Answered,

    /// <summary>The streamed answer failed verification before a word was spoken; the strict re-ask verified and was spoken.</summary>
    AnsweredOnRetry,

    /// <summary>Nothing verifiable arrived: the fixed "no verified answer" line was spoken.</summary>
    Unverified,

    /// <summary>The time budget ran out before the first spoken word: the fixed line was spoken.</summary>
    TimedOut,

    /// <summary>Sterile cockpit: the question was recorded and dropped without a word.</summary>
    Sterile,

    /// <summary>The LLM is known-unhealthy or not configured: the offline advisory was spoken.</summary>
    LlmOffline,

    /// <summary>Another question is still being answered.</summary>
    Busy,

    /// <summary>A Critical callout cut the answer short; nothing more was said.</summary>
    Preempted,
}

/// <summary>
/// Free-form questions to the FO (issue #149): the <see cref="IFreeFormQuestionHandler"/> the
/// router consults last. A question is answered by the LLM from the live
/// <see cref="FoFactSheet"/> through the streaming narrator, every number verified against the
/// sheet; a failed verification before any speech earns ONE strict re-ask (non-streamed), and
/// after that the fixed line. The answer is speech and a session event — it is never parsed,
/// never routed and never reaches any aircraft, radio, MCDU or GSX path.
/// <para>
/// Sterile cockpit (below the sterile ceiling in climb / descent / approach, and the
/// takeoff-roll / initial-climb / rollout phases): the question is dropped WITHOUT a reply and
/// recorded as such — the FO staying silent is the SOP, and a "stand by, after ten thousand"
/// would itself be the chatter the rule forbids. Owner decision 2026-10-03.
/// </para>
/// </summary>
public sealed class FoQuestionService : IFreeFormQuestionHandler, IDisposable
{
    public const string QueryEvent = "fo.query";
    public const string AnswerEvent = "fo.answer";
    public const string Tag = "fo.answer";

    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<BriefingOptions> _briefing;
    private readonly OpenAiChatClient _llm;
    private readonly StreamingNarrator _narrator;
    private readonly ISpeechArbiter _arbiter;
    private readonly IFlightPhaseSource _flight;
    private readonly FoFactSource _facts;
    private readonly LlmHealthStore _llmHealth;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FoQuestionService> _logger;
    private readonly Persona.PersonaService? _persona;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private Task? _inFlight;

    public FoQuestionService(
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<BriefingOptions> briefing,
        OpenAiChatClient llm,
        StreamingNarrator narrator,
        ISpeechArbiter arbiter,
        IFlightPhaseSource flight,
        FoFactSource facts,
        LlmHealthStore llmHealth,
        JsonlEventLog eventLog,
        ILogger<FoQuestionService> logger,
        Persona.PersonaService? persona = null)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(briefing);
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(narrator);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(llmHealth);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _speech = speech;
        _briefing = briefing;
        _llm = llm;
        _narrator = narrator;
        _arbiter = arbiter;
        _flight = flight;
        _facts = facts;
        _llmHealth = llmHealth;
        _eventLog = eventLog;
        _logger = logger;
        _persona = persona;
    }

    private FoQuestionOptions Options => _speech.CurrentValue.FoQuestions;

    public bool Enabled => Options.Enabled;

    /// <summary>The answer in progress, for tests to await. Null when idle.</summary>
    internal Task? InFlight => _inFlight;

    /// <summary>Clock for the budget timers; tests shorten it.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public bool TryAsk(string rawUtterance)
    {
        var options = Options;
        if (!options.Enabled || !FoQuestionCore.IsQuestion(rawUtterance, options))
        {
            return false;
        }

        var question = FoQuestionCore.StripWakeWord(rawUtterance);
        var context = Context();
        var mode = Mode(question);
        _eventLog.Record(QueryEvent, new { question, mode, phase = context.Phase.ToString(), sterile = IsSterile(context) });

        if (IsSterile(context))
        {
            // Dropped on purpose — see the class summary. Recorded so the flight review can
            // see the question was heard.
            _logger.LogInformation("FO question dropped in the sterile cockpit: \"{Question}\"", question);
            Record(question, FoAnswerOutcome.Sterile, "", null, null, 0);
            return true;
        }

        if (!_llm.IsConfigured || !_briefing.CurrentValue.LlmEnabled || _llmHealth.Snapshot().IsUnhealthy)
        {
            Speak(FoQuestionCore.LlmOffline);
            Record(question, FoAnswerOutcome.LlmOffline, FoQuestionCore.LlmOffline, null, null, 0);
            return true;
        }

        if (!_oneAtATime.Wait(0))
        {
            Speak(FoQuestionCore.StandBy);
            Record(question, FoAnswerOutcome.Busy, FoQuestionCore.StandBy, null, null, 0);
            return true;
        }

        _inFlight = Task.Run(async () =>
        {
            try
            {
                await AnswerAsync(question).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FO question failed: \"{Question}\"", question);
                Speak(FoQuestionCore.NoVerifiedAnswer);
                Record(question, FoAnswerOutcome.Unverified, FoQuestionCore.NoVerifiedAnswer, null, null, 0);
            }
            finally
            {
                _oneAtATime.Release();
            }
        });
        return true;
    }

    /// <summary>"flight" (strict, fact sheet, verified) or "chat" (small talk, issue #152) —
    /// chat only with the switch on and no flight word in the question.</summary>
    private string Mode(string question)
        => Options.SmallTalk && !FoQuestionCore.IsFlightQuestion(question) ? "chat" : "flight";

    private async Task AnswerAsync(string question)
    {
        if (Mode(question) == "chat")
        {
            var checkedInstead = await AnswerChatAsync(question).ConfigureAwait(false);
            if (!checkedInstead)
            {
                return;
            }

            // The model said "Let me check." — the question was about the flight after all.
        }

        await AnswerFlightAsync(question).ConfigureAwait(false);
    }

    /// <summary>The small-talk path (issue #152): general knowledge allowed, numbers not
    /// verified (trivia, not flight data), but every sentence passes the chat guard — a line
    /// about THIS flight with a figure in it is refused. Returns true when the model answered
    /// "Let me check." so the caller re-runs the strict path.</summary>
    private async Task<bool> AnswerChatAsync(string question)
    {
        var options = Options;
        var clock = Stopwatch.StartNew();
        var personaFragment = _persona?.SystemPromptFragment(Persona.PersonaStyleCategory.Advisory) ?? "";

        using var spoken = new CancellationTokenSource();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, options.TimeBudgetSeconds)));
        var standBy = StandByAsync(options, spoken.Token);

        var plan = new NarrationPlan(
            "fo.chat", FoQuestionCore.ChatSystemPrompt(personaFragment), FoQuestionCore.ChatUserPrompt(question), [], [],
            new SpeechRequest("FO chat", SpeechPriority.Normal, TimeSpan.FromSeconds(30), Tag: Tag), [])
        {
            VerifyNumbers = false,
            Guard = FoQuestionCore.ChatSentenceAllowed,
            OnFirstSpeech = () =>
            {
                spoken.Cancel();
                try
                {
                    budget.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Already finished.
                }
            },
        };

        NarrationResult? result = null;
        var timedOut = false;
        try
        {
            result = await _narrator.RunAsync(plan, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            spoken.Cancel();
            await standBy.ConfigureAwait(false);
        }

        if (timedOut)
        {
            Speak(FoQuestionCore.NoVerifiedAnswer);
            Record(question, FoAnswerOutcome.TimedOut, FoQuestionCore.NoVerifiedAnswer, null, null, clock.ElapsedMilliseconds, "chat");
            return false;
        }

        if (result!.Preempted)
        {
            Record(question, FoAnswerOutcome.Preempted, result.Text, result.FirstTokenMs, result.FirstAudioMs, clock.ElapsedMilliseconds, "chat");
            return false;
        }

        if (result.LlmSentences > 0 && result.Takeover == TakeoverReason.None)
        {
            var escaped = result.Text.Trim().TrimEnd('.', '!').Equals(FoQuestionCore.LetMeCheck.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
            Record(question, FoAnswerOutcome.Answered, result.Text, result.FirstTokenMs, result.FirstAudioMs, clock.ElapsedMilliseconds, escaped ? "chat→flight" : "chat");
            return escaped;
        }

        // Refused by the guard, or nothing said: the fixed line (no strict re-ask for chat —
        // there is no fact to re-ask about).
        Speak(FoQuestionCore.NoVerifiedAnswer);
        Record(question, FoAnswerOutcome.Unverified, (result.Text + " " + FoQuestionCore.NoVerifiedAnswer).Trim(),
            result.FirstTokenMs, result.FirstAudioMs, clock.ElapsedMilliseconds, "chat");
        return false;
    }

    private async Task AnswerFlightAsync(string question)
    {
        var options = Options;
        var clock = Stopwatch.StartNew();
        var sheet = _facts.Build(DateTimeOffset.UtcNow);
        var personaFragment = _persona?.SystemPromptFragment(Persona.PersonaStyleCategory.Advisory) ?? "";
        var system = FoQuestionCore.SystemPrompt(personaFragment);

        // The budget runs until the first spoken word, then the answer plays out. The
        // "stand by" line fills the gap when the model is slow but still inside the budget.
        using var spoken = new CancellationTokenSource();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, options.TimeBudgetSeconds)));
        var standBy = StandByAsync(options, spoken.Token);

        var plan = new NarrationPlan(
            "fo.answer", system, FoQuestionCore.UserPrompt(sheet, question), sheet.AllowedNumbers, [],
            new SpeechRequest("FO answer", SpeechPriority.Normal, TimeSpan.FromSeconds(30), Tag: Tag), [])
        {
            OnFirstSpeech = () =>
            {
                // The first word is out: the "stand by" timer AND the time budget stand down.
                // The budget token is the one the arbiter item was enqueued with — left armed,
                // it cut every answer at exactly 6.0 s mid-sentence (owner's gate test
                // 2026-10-03: three answers, totalMs 6012–6020, outcome preempted).
                spoken.Cancel();
                try
                {
                    budget.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // The answer already finished.
                }
            },
        };

        NarrationResult? result = null;
        var timedOut = false;
        try
        {
            result = await _narrator.RunAsync(plan, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            spoken.Cancel();
            await standBy.ConfigureAwait(false);
        }

        if (timedOut)
        {
            Speak(FoQuestionCore.NoVerifiedAnswer);
            Record(question, FoAnswerOutcome.TimedOut, FoQuestionCore.NoVerifiedAnswer, null, null, clock.ElapsedMilliseconds, "flight");
            return;
        }

        if (result!.Preempted)
        {
            Record(question, FoAnswerOutcome.Preempted, result.Text, result.FirstTokenMs, result.FirstAudioMs, clock.ElapsedMilliseconds, "flight");
            return;
        }

        if (result.LlmSentences > 0 && result.Takeover == TakeoverReason.None)
        {
            Record(question, FoAnswerOutcome.Answered, result.Text, result.FirstTokenMs, result.FirstAudioMs, clock.ElapsedMilliseconds, "flight");
            return;
        }

        if (result.LlmSentences > 0)
        {
            // Part of the answer was heard and the rest failed: finish honestly, no re-ask —
            // a second answer after half of a first one would be worse than the fixed line.
            Speak(FoQuestionCore.NoVerifiedAnswer);
            Record(question, FoAnswerOutcome.Unverified, result.Text + " " + FoQuestionCore.NoVerifiedAnswer,
                result.FirstTokenMs, result.FirstAudioMs, clock.ElapsedMilliseconds, "flight");
            return;
        }

        // Nothing was spoken (verification failed, the model said nothing, or errored before
        // speech): ONE strict re-ask inside what is left of the budget, non-streamed.
        if (result.Takeover == TakeoverReason.VerifyFailed && !budget.IsCancellationRequested)
        {
            var retry = await StrictRetryAsync(system, sheet, question, budget.Token).ConfigureAwait(false);
            if (retry is not null)
            {
                Speak(retry);
                Record(question, FoAnswerOutcome.AnsweredOnRetry, retry, result.FirstTokenMs, null, clock.ElapsedMilliseconds, "flight");
                return;
            }
        }

        Speak(FoQuestionCore.NoVerifiedAnswer);
        Record(question, budget.IsCancellationRequested ? FoAnswerOutcome.TimedOut : FoAnswerOutcome.Unverified,
            FoQuestionCore.NoVerifiedAnswer, result.FirstTokenMs, null, clock.ElapsedMilliseconds, "flight");
    }

    private async Task<string?> StrictRetryAsync(string system, FoFactSheet sheet, string question, CancellationToken budget)
    {
        try
        {
            var text = await _llm.CompleteAsync(system, FoQuestionCore.StrictUserPrompt(sheet, question), budget).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var check = NumberVerifier.Check(SpokenNumberText.ToDigits(text), sheet.AllowedNumbers);
            if (check.Ok)
            {
                return text.Trim();
            }

            _logger.LogWarning("FO answer strict re-ask still unverified: {Tokens}", string.Join(", ", check.Offending));
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FO answer strict re-ask failed");
            return null;
        }
    }

    private async Task StandByAsync(FoQuestionOptions options, CancellationToken spoken)
    {
        if (options.StandBySeconds <= 0)
        {
            return;
        }

        try
        {
            await Delay(TimeSpan.FromSeconds(options.StandBySeconds), spoken).ConfigureAwait(false);
            Speak(FoQuestionCore.StandBy);
        }
        catch (OperationCanceledException)
        {
            // The answer began (or ended) first.
        }
    }

    private SpeechContext Context()
    {
        var view = _flight.Snapshot();
        return new SpeechContext(view.Phase, view.Data?.AltitudeFt ?? 0, view.Data?.IsValid ?? false);
    }

    private bool IsSterile(SpeechContext context)
        => context.Phase is FlightPhase.TakeoffRoll or FlightPhase.InitialClimb or FlightPhase.LandingRollout
            || SterileCockpitRule.IsSterile(_speech.CurrentValue, context);

    /// <summary>The ONLY way an answer leaves this class: text to the arbiter. Nothing here
    /// ever calls a feature, a dataref or a router.</summary>
    private void Speak(string text)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, TimeSpan.FromSeconds(30), Tag: Tag));

    private void Record(string question, FoAnswerOutcome outcome, string text, double? firstTokenMs, double? firstAudioMs, long totalMs, string? mode = null)
    {
        _logger.LogInformation("FO question \"{Question}\" -> {Outcome} in {TotalMs} ms: \"{Answer}\"", question, outcome, totalMs, text);
        _eventLog.Record(AnswerEvent, new
        {
            question,
            mode = mode ?? Mode(question),
            outcome = outcome switch
            {
                FoAnswerOutcome.Answered => "answered",
                FoAnswerOutcome.AnsweredOnRetry => "answered-on-retry",
                FoAnswerOutcome.Unverified => "unverified",
                FoAnswerOutcome.TimedOut => "timed-out",
                FoAnswerOutcome.Sterile => "sterile",
                FoAnswerOutcome.LlmOffline => "llm-offline",
                FoAnswerOutcome.Busy => "busy",
                _ => "preempted",
            },
            text,
            firstTokenMs = firstTokenMs is { } t ? Math.Round(t) : (double?)null,
            firstAudioMs = firstAudioMs is { } a ? Math.Round(a) : (double?)null,
            totalMs,
            model = _briefing.CurrentValue.LlmModel,
        });
    }

    public void Dispose() => _oneAtATime.Dispose();
}
