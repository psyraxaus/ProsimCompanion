using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Checklists;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Abnormals;
using ProsimCompanion.Speech.Recognition;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>
/// The interactive ECAM dialogue (EcamDialogueCore) against a scripted speak/listen seam:
/// confirm-gated advance, standby/continue, skip, say again, silence handling, verification
/// discrepancies and branch gates — plus the FailureMonitor wiring (dialogue after detection,
/// announce-only without a mic, drills staying non-interactive).
/// </summary>
public sealed class EcamDialogueTests
{
    private const string Completion = "ECAM actions complete. Resuming normal duties.";
    private const string StandingBy = "Standing by. Say continue ECAM when ready.";
    private const string SilencePrompt = "Say again, or say standby.";

    // ---- scripted Io ----

    /// <summary>Scripted seam. An exhausted answer queue throws instead of returning null, so
    /// a dialogue that asks more than the test scripted fails loudly rather than looping.</summary>
    private sealed class ScriptedIo : IEcamDialogueIo
    {
        public List<string> Spoken { get; } = [];

        public List<IReadOnlyList<string>> Grammars { get; } = [];

        public Queue<string?> Answers { get; } = new();

        public Func<VerifyCondition, bool?> Evaluate { get; set; } = _ => true;

        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        {
            Spoken.Add(text);
            return Task.CompletedTask;
        }

        public Task<string?> ListenAsync(
            IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Grammars.Add(grammar);
            return Answers.Count > 0
                ? Task.FromResult(Answers.Dequeue())
                : throw new InvalidOperationException("listen script exhausted");
        }

        public bool? TryEvaluate(VerifyCondition condition) => Evaluate(condition);
    }

    private static EcamDialogueCore Core(ScriptedIo io)
        => new(io, SpeechTestSupport.TempEventLog());

    private static AbnormalDefinition Procedure(params AbnormalAction[] actions) => new()
    {
        Id = "eng-1-fire",
        Title = "ENG 1 FIRE",
        Severity = "warning",
        Announce = "ECAM, engine 1 fire.",
        Actions = [.. actions],
        Status = ["Status. Avoid icing conditions."],
    };

    // ---- confirm-gated advance ----

    [Fact]
    public async Task ConfirmPhrases_GateEachLine_ThenStatusAndCompletionReadStraight()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("thrust lever one idle");   // line 1: its own confirm phrase
        io.Answers.Enqueue("confirmed");               // line 2: generic affirmative
        var definition = Procedure(
            new AbnormalAction
            {
                Say = "Thrust lever 1, idle.",
                Confirm = ["thrust lever one idle"],
                Ack = "Idle set.",
            },
            new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["engine master one off"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(
            [
                "Thrust lever 1, idle.",
                "Idle set.",
                "Engine master 1, off.",
                "Status. Avoid icing conditions.",
                Completion,
            ],
            io.Spoken);
    }

    [Fact]
    public async Task LineGrammar_CarriesConfirmPhrasesAffirmativesAndGlobalWords()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("thrust lever one idle");
        var definition = Procedure(new AbnormalAction
        {
            Say = "Thrust lever 1, idle.",
            Confirm = ["thrust lever one idle"],
        });

        await Core(io).RunAsync(definition, CancellationToken.None);

        var grammar = io.Grammars[0];
        Assert.Contains("thrust lever one idle", grammar);
        Assert.Contains("affirm", grammar);
        Assert.Contains("standby", grammar);
        Assert.Contains("stand by", grammar);
        Assert.Contains("say again", grammar);
        Assert.Contains("skip", grammar);
        Assert.Contains("skip line", grammar);
        Assert.Contains("fault remains", grammar);
        Assert.Contains("unable", grammar);
    }

    // ---- fault remains / unable (issue #103) ----

    [Fact]
    public async Task FaultRemains_ClosesTheLineWithoutItsVerify_AndRunsTheCarryPath()
    {
        // The 2026-08-23 GEN fault run: the reset did not work and the pilot had no way to
        // say so — "fault remains" must accept the failed action (never demanding the
        // verify) and let the condition-gated carry-the-fault line run.
        var io = new ScriptedIo();
        io.Answers.Enqueue("fault remains");   // line 1: the reset did not work
        io.Answers.Enqueue("generator off");   // line 2: the carry path
        io.Evaluate = condition => condition.Dataref == "carry"; // verify false, branch true
        var definition = Procedure(
            new AbnormalAction
            {
                Say = "Generator 1, off, then on. Attempt a reset.",
                Confirm = ["reset"],
                Verify = new VerifyCondition { Dataref = "verify", Op = ComparisonOp.Equals, Value = 1 },
                Discrepancy = "Generator 1 has not come back on line.",
            },
            new AbnormalAction
            {
                Say = "Generator 1, off.",
                Confirm = ["generator off"],
                Condition = new VerifyCondition { Dataref = "carry", Op = ComparisonOp.Equals, Value = 0 },
            });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(
            [
                "Generator 1, off, then on. Attempt a reset.",
                "Understood — the fault remains.",
                "Generator 1, off.",
                "Status. Avoid icing conditions.",
                Completion,
            ],
            io.Spoken);
    }

    [Fact]
    public async Task Unable_SkipsRemainingActions_StillReadsStatus()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("unable");
        var definition = Procedure(
            new AbnormalAction { Say = "Generator 1, off, then on. Attempt a reset.", Confirm = ["reset"] },
            new AbnormalAction { Say = "Generator 1, off.", Confirm = ["generator off"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(
            [
                "Generator 1, off, then on. Attempt a reset.",
                "Understood.",
                "Status. Avoid icing conditions.",
                Completion,
            ],
            io.Spoken);
    }

    // ---- standby / continue ----

    [Fact]
    public async Task Standby_PausesSilently_UntilContinueEcam_ThenRespeaksTheLine()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("standby");       // pause
        io.Answers.Enqueue(null);            // one silent standby window — no re-prompt
        io.Answers.Enqueue("continue ecam"); // resume
        io.Answers.Enqueue("done");          // confirm on the re-spoken line
        var definition = Procedure(new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["done"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(
            [
                "Engine master 1, off.",
                StandingBy,
                "Continuing.",
                "Engine master 1, off.",
                "Status. Avoid icing conditions.",
                Completion,
            ],
            io.Spoken);

        // While standing by the FO listens for continue phrases only.
        Assert.Contains("continue ecam", io.Grammars[1]);
        Assert.Contains("proceed", io.Grammars[1]);
        Assert.DoesNotContain("done", io.Grammars[1]);
    }

    // ---- skip ----

    [Fact]
    public async Task SkipLine_AbandonsTheLineWithoutAck_AndMovesOn()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("skip line");
        io.Answers.Enqueue("done");
        var definition = Procedure(
            new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["master off"], Ack = "Master off." },
            new AbnormalAction { Say = "Agent 1, discharge.", Confirm = ["done"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.DoesNotContain("Master off.", io.Spoken);
        Assert.Equal(
            ["Engine master 1, off.", "Agent 1, discharge.", "Status. Avoid icing conditions.", Completion],
            io.Spoken);
    }

    // ---- say again ----

    [Fact]
    public async Task SayAgain_RepeatsTheLine_WithoutTheSilencePrompt()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("say again");
        io.Answers.Enqueue("done");
        var definition = Procedure(new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["done"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(2, io.Spoken.Count(s => s == "Engine master 1, off."));
        Assert.DoesNotContain(SilencePrompt, io.Spoken);
    }

    // ---- timeouts ----

    [Fact]
    public async Task Timeout_RepromptsTwice_ThenStandsByUntilContinue()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue(null);       // silence 1 → "Say again, or say standby."
        io.Answers.Enqueue(null);       // silence 2 → prompt again
        io.Answers.Enqueue(null);       // silence 3 → auto-standby, no third prompt
        io.Answers.Enqueue("continue"); // resumes from the (auto) standby
        io.Answers.Enqueue("done");
        var definition = Procedure(new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["done"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(2, io.Spoken.Count(s => s == SilencePrompt));
        Assert.Equal(1, io.Spoken.Count(s => s == StandingBy));
        Assert.Contains("Continuing.", io.Spoken);
        Assert.Contains(Completion, io.Spoken);
    }

    // ---- abort (issue #56) ----

    [Fact]
    public async Task CancelEcam_EndsTheWholeProcedure_WithoutStatusOrCompletion()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("cancel ecam");
        var definition = Procedure(
            new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["master off"] },
            new AbnormalAction { Say = "Agent 1, discharge.", Confirm = ["discharged"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Contains("ECAM cancelled. Resuming normal duties.", io.Spoken);
        Assert.DoesNotContain("Agent 1, discharge.", io.Spoken);
        Assert.DoesNotContain("Status. Avoid icing conditions.", io.Spoken);
        Assert.DoesNotContain(Completion, io.Spoken);
    }

    [Fact]
    public async Task CancelEcam_WorksFromStandbyToo()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("standby");
        io.Answers.Enqueue("cancel ecam");
        var definition = Procedure(new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["done"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Contains(StandingBy, io.Spoken);
        Assert.Contains("ECAM cancelled. Resuming normal duties.", io.Spoken);
        Assert.DoesNotContain(Completion, io.Spoken);
    }

    // ---- containment matching (issue #56) ----

    [Fact]
    public async Task NaturalPhrasing_AroundAConfirmPhrase_StillConfirms()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("okay engine master one off done"); // superset of the confirm phrase
        var definition = Procedure(new AbnormalAction
        {
            Say = "Engine master 1, off.",
            Confirm = ["engine master one off"],
            Ack = "Master one off.",
        });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Contains("Master one off.", io.Spoken);
        Assert.Contains(Completion, io.Spoken);
    }

    // ---- standby holds through chatter (issue #56) ----

    [Fact]
    public async Task Standby_IgnoresUnmatchedSpeech_OnlyAContinuePhraseResumes()
    {
        var io = new ScriptedIo();
        io.Answers.Enqueue("standby");
        io.Answers.Enqueue("tower golf uniform ready for pushback"); // ATC chatter — must hold
        io.Answers.Enqueue("cabin crew seats for departure");        // more chatter — must hold
        io.Answers.Enqueue("continue ecam");
        io.Answers.Enqueue("done");
        var definition = Procedure(new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["done"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(1, io.Spoken.Count(s => s == "Continuing."));
        Assert.Contains(Completion, io.Spoken);
    }

    // ---- already-satisfied lines (issue #57) ----

    [Fact]
    public async Task VerifyAlreadyTrue_AcksWithoutDemandingAReadback()
    {
        // No scripted answers at all: if the dialogue listens, ScriptedIo throws.
        var io = new ScriptedIo { Evaluate = _ => true };
        var definition = Procedure(new AbnormalAction
        {
            Say = "Electrical, generator 2, off, then on.",
            Confirm = ["reset"],
            Verify = new VerifyCondition
            {
                Dataref = "aircraft.electrical.relay.Gen2Line",
                Op = ComparisonOp.Equals,
                Value = 1,
            },
            Ack = "Generator 2 back on line.",
        });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(
            [
                "Electrical, generator 2, off, then on.",
                "Generator 2 back on line.",
                "Status. Avoid icing conditions.",
                Completion,
            ],
            io.Spoken);
        Assert.Empty(io.Grammars); // never opened a listening window
    }

    // ---- verification ----

    [Fact]
    public async Task VerifiedLine_SpeaksAck_AndAdvances()
    {
        var io = new ScriptedIo { Evaluate = _ => true };
        io.Answers.Enqueue("fire push button pushed");
        var definition = Procedure(new AbnormalAction
        {
            Say = "Engine 1 fire push button, push.",
            Confirm = ["fire push button pushed"],
            Verify = new VerifyCondition
            {
                Dataref = "system.switches.S_FIRE_ENG1",
                Op = ComparisonOp.Equals,
                Value = 1,
            },
            Ack = "Fire push button out.",
        });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Contains("Fire push button out.", io.Spoken);
        Assert.Contains(Completion, io.Spoken);
    }

    [Fact]
    public async Task VerifyMismatch_SpeaksDiscrepancyAndRetries_ThenOfferTimeoutContinuesUnverified()
    {
        var io = new ScriptedIo { Evaluate = _ => false }; // aircraft never agrees
        io.Answers.Enqueue("fire push button pushed"); // mismatch 1 → retry (MaxVerifyRetries = 1)
        io.Answers.Enqueue("fire push button pushed"); // mismatch 2 → offer continue/standby
        io.Answers.Enqueue(null);                      // offer timeout → proceed unverified
        var definition = Procedure(new AbnormalAction
        {
            Say = "Engine 1 fire push button, push.",
            Confirm = ["fire push button pushed"],
            Verify = new VerifyCondition
            {
                Dataref = "system.switches.S_FIRE_ENG1",
                Op = ComparisonOp.Equals,
                Value = 1,
            },
            Discrepancy = "The fire push button is not out.",
        });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.Equal(2, io.Spoken.Count(s => s == "The fire push button is not out."));
        Assert.Contains("Say continue to proceed, or standby.", io.Spoken);
        Assert.Contains(Completion, io.Spoken);
    }

    // ---- branch gates ----

    [Fact]
    public async Task BranchConditionFalse_SkipsTheLineSilently()
    {
        var io = new ScriptedIo { Evaluate = _ => false };
        io.Answers.Enqueue("done");
        var gated = new AbnormalAction
        {
            Say = "Agent 2, discharge.",
            Confirm = ["agent two discharged"],
            Condition = new VerifyCondition
            {
                Dataref = "system.indicators.I_ENG_FIRE_1",
                Op = ComparisonOp.GreaterThan,
                Value = 0,
            },
        };
        var definition = Procedure(gated, new AbnormalAction { Say = "Engine master 1, off.", Confirm = ["done"] });

        await Core(io).RunAsync(definition, CancellationToken.None);

        Assert.DoesNotContain("Agent 2, discharge.", io.Spoken);
        Assert.Contains("Engine master 1, off.", io.Spoken);
    }

    [Fact]
    public async Task BranchUnreadable_AsksThePilot_NegativeSkips_AffirmApplies()
    {
        var condition = new VerifyCondition
        {
            Dataref = "system.indicators.I_ENG_FIRE_1",
            Op = ComparisonOp.GreaterThan,
            Value = 0,
        };
        AbnormalAction Gated() => new()
        {
            Say = "Agent 2, discharge.",
            Confirm = ["agent two discharged"],
            Condition = condition,
        };

        var negative = new ScriptedIo { Evaluate = _ => null };
        negative.Answers.Enqueue("negative");
        await Core(negative).RunAsync(Procedure(Gated()), CancellationToken.None);
        Assert.Contains("Regarding: Agent 2, discharge.. Does this apply? Affirm or negative.", negative.Spoken);
        Assert.DoesNotContain("Agent 2, discharge.", negative.Spoken);

        var affirm = new ScriptedIo { Evaluate = _ => null };
        affirm.Answers.Enqueue("affirm");
        affirm.Answers.Enqueue("agent two discharged");
        await Core(affirm).RunAsync(Procedure(Gated()), CancellationToken.None);
        Assert.Contains("Agent 2, discharge.", affirm.Spoken);
    }

    // ---- FailureMonitor wiring ----

    /// <summary>Recording mic. Like the scripted Io, an exhausted answer queue throws so a
    /// dialogue that over-asks surfaces in the monitor's error log and fails the poll below.</summary>
    private sealed class FakeMic : IMicOwnership
    {
        public int Borrows;

        public event Action? Released { add { } remove { } }

        public Queue<string?> Answers { get; } = new();

        public List<IReadOnlyList<string>> Listens { get; } = [];

        public bool IsBorrowed { get; private set; }

        public IDisposable Borrow(string owner)
        {
            if (IsBorrowed)
            {
                throw new InvalidOperationException("already borrowed");
            }

            IsBorrowed = true;
            Interlocked.Increment(ref Borrows);
            return new Scope(this);
        }

        public Task<string?> ListenAsync(
            IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (Listens)
            {
                Listens.Add(grammar);
                return Answers.Count > 0
                    ? Task.FromResult(Answers.Dequeue())
                    : throw new InvalidOperationException("listen script exhausted");
            }
        }

        private sealed class Scope(FakeMic owner) : IDisposable
        {
            public void Dispose() => owner.IsBorrowed = false;
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static AbnormalDefinition DetectableEcam() => new()
    {
        Id = "eng-1-fire",
        Title = "ENG 1 FIRE",
        Severity = "warning",
        Announce = "ECAM, engine 1 fire.",
        Trigger = new AbnormalTrigger
        {
            Condition = new VerifyCondition
            {
                Dataref = "system.indicators.I_ENG_FIRE_1",
                Op = ComparisonOp.GreaterThan,
                Value = 0,
            },
            DebounceSeconds = 1.0,
        },
        Actions =
        [
            new AbnormalAction
            {
                Say = "Engine master 1, off.",
                Confirm = ["engine master one off"],
                Ack = "Master one off.",
            },
        ],
        Status = ["Status. Avoid icing conditions."],
    };

    private static async Task<bool> WaitForSpokenAsync(FakeArbiter arbiter, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (arbiter.Requests)
            {
                if (arbiter.Requests.Any(r => r.Text == text))
                {
                    return true;
                }
            }

            await Task.Delay(25);
        }

        return false;
    }

    [Fact]
    public async Task DetectedEcamProcedure_RunsInteractiveDialogueUnderMicBorrow()
    {
        var arbiter = new FakeArbiter();
        var phase = new FakePhaseSource();
        var dataRefs = new FakeDataRefs();
        var mic = new FakeMic();
        mic.Answers.Enqueue("engine master one off");
        using var monitor = new FailureMonitor(
            arbiter, dataRefs, phase, SpeechTestSupport.TempEventLog(),
            NullLogger<FailureMonitor>.Instance, mic);
        phase.SetPhase(FlightPhase.Cruise);
        monitor.Load([DetectableEcam()]);
        dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;

        monitor.ProcessTick(T0);
        monitor.ProcessTick(T0.AddSeconds(1.5)); // fires: announcement + background dialogue

        Assert.True(await WaitForSpokenAsync(arbiter, Completion), "dialogue never completed");
        lock (arbiter.Requests)
        {
            var texts = arbiter.Requests.Select(r => r.Text).ToList();
            Assert.Equal(
                [
                    "ECAM, engine 1 fire.",
                    "Engine master 1, off.",
                    "Master one off.",
                    "Status. Avoid icing conditions.",
                    Completion,
                ],
                texts);
        }

        Assert.Equal(1, mic.Borrows);
        Assert.False(mic.IsBorrowed); // borrow restored after the dialogue
    }

    /// <summary>Mic whose listens BLOCK until cancelled once the scripted answers run out —
    /// models a pilot who never says anything recognizable, which is exactly the state the
    /// cleared/cancel paths must be able to break out of.</summary>
    private sealed class BlockingMic : IMicOwnership
    {
        public event Action? Released { add { } remove { } }

        public bool IsBorrowed { get; private set; }

        public IDisposable Borrow(string owner)
        {
            IsBorrowed = true;
            return new Scope(this);
        }

        public async Task<string?> ListenAsync(
            IReadOnlyList<string> grammar, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null; // unreachable — the delay only ends by cancellation
        }

        private sealed class Scope(BlockingMic owner) : IDisposable
        {
            public void Dispose() => owner.IsBorrowed = false;
        }
    }

    [Fact]
    public async Task FailureClearingMidDialogue_EndsIt_AnnouncesAndReturnsTheMic()
    {
        var arbiter = new FakeArbiter();
        var phase = new FakePhaseSource();
        var dataRefs = new FakeDataRefs();
        var mic = new BlockingMic();
        using var monitor = new FailureMonitor(
            arbiter, dataRefs, phase, SpeechTestSupport.TempEventLog(),
            NullLogger<FailureMonitor>.Instance, mic);
        phase.SetPhase(FlightPhase.Cruise);
        monitor.Load([DetectableEcam()]);
        dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;

        monitor.ProcessTick(T0);
        monitor.ProcessTick(T0.AddSeconds(1.5));
        Assert.True(await WaitForSpokenAsync(arbiter, "Engine master 1, off."), "dialogue never started");

        // The fault clears while the dialogue is stuck listening (issue #57).
        dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 0.0;
        monitor.ProcessTick(T0.AddSeconds(3));

        Assert.True(
            await WaitForSpokenAsync(arbiter, "The ENG 1 FIRE has cleared. Resuming normal duties."),
            "cleared announcement never spoken");
        Assert.False(mic.IsBorrowed);
    }

    [Fact]
    public async Task WebCancel_EndsTheRunningDialogue_AndAcknowledges()
    {
        var arbiter = new FakeArbiter();
        var phase = new FakePhaseSource();
        var dataRefs = new FakeDataRefs();
        var mic = new BlockingMic();
        using var monitor = new FailureMonitor(
            arbiter, dataRefs, phase, SpeechTestSupport.TempEventLog(),
            NullLogger<FailureMonitor>.Instance, mic);
        phase.SetPhase(FlightPhase.Cruise);
        monitor.Load([DetectableEcam()]);
        dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;

        monitor.ProcessTick(T0);
        monitor.ProcessTick(T0.AddSeconds(1.5));
        Assert.True(await WaitForSpokenAsync(arbiter, "Engine master 1, off."), "dialogue never started");

        Assert.True(monitor.CancelActiveDialogue());

        Assert.True(
            await WaitForSpokenAsync(arbiter, "ECAM cancelled. Resuming normal duties."),
            "cancel acknowledgement never spoken");
        Assert.False(mic.IsBorrowed);
        Assert.False(monitor.CancelActiveDialogue()); // nothing left to cancel
    }

    [Fact]
    public async Task WithoutMicSeam_EcamProcedureStaysAnnounceOnly()
    {
        var arbiter = new FakeArbiter();
        var phase = new FakePhaseSource();
        var dataRefs = new FakeDataRefs();
        using var monitor = new FailureMonitor(
            arbiter, dataRefs, phase, SpeechTestSupport.TempEventLog(),
            NullLogger<FailureMonitor>.Instance);
        phase.SetPhase(FlightPhase.Cruise);
        monitor.Load([DetectableEcam()]);
        dataRefs.Values["system.indicators.I_ENG_FIRE_1"] = 1.0;

        monitor.ProcessTick(T0);
        monitor.ProcessTick(T0.AddSeconds(1.5));
        await Task.Delay(300); // give any (wrongly started) dialogue a chance to speak

        lock (arbiter.Requests)
        {
            var fire = Assert.Single(arbiter.Requests);
            Assert.Equal("ECAM, engine 1 fire.", fire.Text);
        }
    }

    [Fact]
    public async Task MemoryDrill_StaysNonInteractive_NeverTouchesTheMic()
    {
        var arbiter = new FakeArbiter();
        var phase = new FakePhaseSource();
        var dataRefs = new FakeDataRefs();
        var mic = new FakeMic();
        using var monitor = new FailureMonitor(
            arbiter, dataRefs, phase, SpeechTestSupport.TempEventLog(),
            NullLogger<FailureMonitor>.Instance, mic);
        monitor.Load(
        [
            new AbnormalDefinition
            {
                Id = "drill-stall-recovery",
                Class = "memoryDrill",
                Severity = "warning",
                Announce = "Stall!",
                VoiceTriggers = ["stall drill"],
                Actions =
                [
                    // Confirm phrases on a drill line must be ignored — drills never listen.
                    new AbnormalAction { Say = "Nose down.", Confirm = ["nose down set"] },
                    new AbnormalAction { Say = "Wings level.", Confirm = ["wings level set"] },
                ],
                Status = ["Recover smoothly."],
            },
        ]);

        Assert.True(monitor.TryRunDrillByPhrase("stall drill"));
        Assert.True(await WaitForSpokenAsync(arbiter, "Recover smoothly."), "drill never completed");

        lock (arbiter.Requests)
        {
            Assert.Equal(
                ["Stall!", "Nose down.", "Wings level.", "Recover smoothly."],
                arbiter.Requests.Select(r => r.Text).ToArray());
        }

        Assert.Equal(0, mic.Borrows);
        Assert.Empty(mic.Listens);
    }
}
