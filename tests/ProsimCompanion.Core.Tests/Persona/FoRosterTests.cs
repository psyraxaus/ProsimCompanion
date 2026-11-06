using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Persona;
using Xunit;

namespace ProsimCompanion.Core.Tests.Persona;

/// <summary>The FO roster (issue #194): the pure draw, the file parser, the service's pin /
/// restart rules and the trigger core.</summary>
public sealed class FoRosterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pc-roster-").FullName;
    private readonly PersonaOptions _options = new() { Enabled = true, Roster = { Mode = PersonaRosterOptions.ModeDaily } };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static FoRosterEntry Officer(string id, string? voice = null, string confidence = "steady", int hours = 1000)
        => new(id, char.ToUpperInvariant(id[0]) + id[1..], "standard", "standard", 1, confidence, hours, "EGLL", voice);

    private FoRosterService Service(Func<int, int>? next = null)
    {
        var monitor = new Mock<IOptionsMonitor<PersonaOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(() => _options);
        return new FoRosterService(
            monitor.Object,
            new FoRosterStateFile(NullLogger<FoRosterStateFile>.Instance, Path.Combine(_dir, "state.json")),
            new JsonlEventLog(Path.Combine(_dir, "sessions"), NullLogger<JsonlEventLog>.Instance),
            NullLogger<FoRosterService>.Instance,
            configDirectory: _dir)
        {
            Next = next,
        };
    }

    private void WriteRoster(params string[] ids)
        => File.WriteAllText(Path.Combine(_dir, FoRosterService.FileName),
            "{ \"officers\": [" + string.Join(",", ids.Select(id => $"{{ \"id\": \"{id}\", \"name\": \"{id}\", \"hoursOnType\": 1250, \"confidence\": \"nervous\" }}")) + "] }");

    // ---- the draw ----

    [Fact]
    public void Pick_NeverRepeatsThePreviousId_WhenThePoolHasMoreThanOne()
    {
        var pool = new[] { Officer("a"), Officer("b"), Officer("c") };
        for (var seed = 0; seed < 50; seed++)
        {
            var dice = new Random(seed);
            var picked = FoRosterDraw.Pick(pool, "b", bound => dice.Next(bound));
            Assert.NotNull(picked);
            Assert.NotEqual("b", picked!.Id);
        }
    }

    [Fact]
    public void Pick_FallsBackToTheFullPool_WhenOnlyOneEntry()
    {
        var only = Officer("solo");
        Assert.Same(only, FoRosterDraw.Pick([only], "solo", _ => 0));
        Assert.Null(FoRosterDraw.Pick([], null, _ => 0));
    }

    // ---- the file ----

    [Fact]
    public void Parse_NormalisesAxes_AndSkipsBrokenEntries()
    {
        var result = FoRosterFile.Parse("""
            {
              "officers": [
                { "id": "a", "name": "Alex", "experience": "SENIOR", "formality": "odd", "chattiness": 9, "confidence": "Assured", "hoursOnType": 5649, "base": "egll", "voice": " kokoro:af_sky " },
                { "name": "no id" },
                { "id": "a", "name": "dup" }
              ]
            }
            """);

        var alex = Assert.Single(result.Officers);
        Assert.Equal(("senior", "standard", 3, "assured", "EGLL", "kokoro:af_sky"), (alex.Experience, alex.Formality, alex.Chattiness, alex.Confidence, alex.Base, alex.Voice));
        Assert.Equal(5600, alex.SpokenHours);
        Assert.Equal(2, result.Problems.Count);
    }

    // ---- the service ----

    [Fact]
    public void Draw_RecordsTheDraw_AndTheVoiceIsNullWithoutOne()
    {
        WriteRoster("a", "b");
        var service = Service(_ => 0);

        var drawn = service.Draw("test", DateTimeOffset.UtcNow, "sim:2026-11-01");

        Assert.NotNull(drawn);
        Assert.Null(drawn!.Voice);
        Assert.Equal(drawn.Id, service.Current!.Id);
        Assert.Equal("sim:2026-11-01", service.State.DrawnFor);
        Assert.False(service.State.IntroDone);
    }

    [Fact]
    public void Draw_NeverRepeatsThePreviousDraw()
    {
        WriteRoster("a", "b", "c");
        var service = Service();
        var first = service.Draw("test", DateTimeOffset.UtcNow)!.Id;
        for (var i = 0; i < 20; i++)
        {
            var next = service.Draw("test", DateTimeOffset.UtcNow)!.Id;
            Assert.NotEqual(first, next);
            first = next;
        }
    }

    [Fact]
    public void Restart_KeepsTheDraw_FromTheStateFile()
    {
        WriteRoster("a", "b");
        var first = Service(_ => 1);
        var drawn = first.Draw("test", DateTimeOffset.UtcNow, "day:d1")!;
        first.MarkIntroSpoken();

        var restarted = Service();

        Assert.Equal(drawn.Id, restarted.Current!.Id);
        Assert.Equal("day:d1", restarted.State.DrawnFor);
        Assert.True(restarted.State.IntroDone);
    }

    [Fact]
    public void PinnedMode_CurrentIsThePinnedOfficer_AndNothingElseCounts()
    {
        WriteRoster("a", "b");
        var service = Service(_ => 0);
        Assert.True(service.Pin("b"));
        _options.Roster.Mode = PersonaRosterOptions.ModePinned; // the writer is absent in tests; the page would see the flip

        Assert.Equal("b", service.Current!.Id);
        Assert.False(service.Pin("zz"));
        // The trigger core never asks for a draw in pinned mode (below); even a forced draw
        // leaves the pinned FO on duty.
        service.Draw("test", DateTimeOffset.UtcNow);
        Assert.Equal("b", service.Current!.Id);
    }

    [Fact]
    public void RosterOff_CurrentIsNull_EvenAfterADraw()
    {
        WriteRoster("a");
        var service = Service();
        service.Draw("test", DateTimeOffset.UtcNow);
        _options.Roster.Mode = PersonaRosterOptions.ModeOff;

        Assert.Null(service.Current);
        Assert.False(service.IsActive);
    }

    [Fact]
    public void FileEdits_AreReloaded_OnTheNextAccess()
    {
        WriteRoster("a");
        var service = Service();
        Assert.Single(service.Pool);

        Thread.Sleep(20);
        WriteRoster("a", "b", "c");
        File.SetLastWriteTimeUtc(Path.Combine(_dir, FoRosterService.FileName), DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(3, service.Pool.Count);
    }

    // ---- the trigger core ----

    private static FoRosterTriggerInputs Inputs(
        bool active = true, bool pinned = false, int? legsCompleted = null, bool legBegan = false,
        bool legPlanned = false, bool inPreflight = false, DateOnly? simDate = null, string? drawnFor = null,
        bool hasCurrent = true, bool introDone = false, bool introEnabled = true, string? openDayId = null, string? drawnDayId = null)
        => new(active, pinned, legsCompleted, openDayId ?? (legsCompleted is not null ? "day-open" : null), drawnDayId,
            legBegan, legPlanned, inPreflight, simDate, new DateOnly(2026, 11, 1), drawnFor, hasCurrent, introDone, introEnabled);

    // Review 2026-11-05: an open day pins its FO from the moment the FO is that day's — not only
    // after a flown leg (an open leg-1 day restarted after a sim-date rollover used to redraw).
    [Fact]
    public void Trigger_OpenDayWithItsOwnFo_PinsBeforeTheFirstLegIsFlown()
    {
        var restarted = FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true, legPlanned: true, legsCompleted: 0, openDayId: "d1", drawnDayId: "d1",
            simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01", introDone: true));
        Assert.Null(restarted.NewStamp);
        Assert.Null(FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, legsCompleted: 0, openDayId: "d1", drawnDayId: "d1",
            simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01", introDone: true)).NewStamp);
    }

    [Fact]
    public void Trigger_OpenDayWithYesterdaysFo_DrawsOnANewDate_KeepsOnTheSameDate()
    {
        // The day opened (auto-start on the Preflight edge) with yesterday's FO still on file:
        // not that FO's day — a new sim date draws, the same date keeps (ADR-0019 §8).
        Assert.Equal("sim:2026-11-02", FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, legsCompleted: 0, openDayId: "d2", drawnDayId: "d1",
            simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01")).NewStamp);
        Assert.Null(FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, legsCompleted: 0, openDayId: "d2", drawnDayId: "d1",
            simDate: new DateOnly(2026, 11, 1), drawnFor: "sim:2026-11-01", introDone: true)).NewStamp);
        // No day open at all: the date alone decides, as before.
        Assert.Equal("sim:2026-11-02", FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, drawnDayId: "d1",
            simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01")).NewStamp);
    }

    [Fact]
    public void Trigger_RosterOff_DoesNothing()
        => Assert.Equal(FoRosterTriggerDecision.Nothing, FoRosterTriggerCore.Evaluate(Inputs(active: false, legBegan: true, inPreflight: true)));

    // ADR-0019 §8 / issue #219: one FO per sim calendar day, drawn when a leg begins.
    [Fact]
    public void Trigger_NewLeg_DrawsOnANewSimDate_NotTheSameDate()
    {
        var newDay = FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, inPreflight: true, simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01"));
        Assert.Equal(("sim:2026-11-02", "new-leg", true), (newDay.NewStamp, newDay.Reason, newDay.Intro));

        var sameDay = FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, inPreflight: true, simDate: new DateOnly(2026, 11, 1), drawnFor: "sim:2026-11-01", introDone: true));
        Assert.Equal(FoRosterTriggerDecision.Nothing, sameDay);

        // No sim date: the wall date stands in.
        var fallback = FoRosterTriggerCore.Evaluate(Inputs(legBegan: true));
        Assert.Equal("sim:2026-11-01", fallback.NewStamp);
        Assert.False(fallback.Intro); // cold and dark: the intro waits for Preflight

        // The old "day:" stamp of a pre-1.0 state file is simply a different stamp.
        Assert.Equal("sim:2026-11-01", FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, drawnFor: "day:day-20261101-002148")).NewStamp);
    }

    [Fact]
    public void Trigger_PreflightWithThePlan_IsAnOpportunity_ABarePreflightOrTheArrivalsIsNot()
    {
        // The app started after the plan: Preflight + the leg planned draws.
        var late = FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true, legPlanned: true));
        Assert.Equal(("sim:2026-11-01", "preflight"), (late.NewStamp, late.Reason));

        // Powered with no plan: nothing to draw for.
        Assert.Null(FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true)).NewStamp);

        // The arrival's Preflight edge (boundary Turnaround → legPlanned false) on a new sim
        // date: no draw — EHAM 2026-11-01's Hanna → Sam.
        Assert.Null(FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true, simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01", introDone: true)).NewStamp);

        // A plan loaded cold and dark: not yet (the leg-began edge itself draws; this is the re-evaluation).
        Assert.Null(FoRosterTriggerCore.Evaluate(Inputs(legPlanned: true)).NewStamp);
    }

    [Fact]
    public void Trigger_OpenDutyDayPastItsFirstLeg_PinsTheFo_UntilEndOfDuty()
    {
        // Mid-day sim-date rollover with an open day and an FO on duty: no draw.
        var pinned = FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, legsCompleted: 1, inPreflight: true, simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01", introDone: true));
        Assert.Equal(FoRosterTriggerDecision.Nothing, pinned);

        // The day's first leg: the day is open but has flown nothing — a draw is allowed.
        Assert.Equal("sim:2026-11-02", FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, legsCompleted: 0, simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01")).NewStamp);

        // An open day with nobody drawn yet pins nobody.
        Assert.Equal("sim:2026-11-02", FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, legsCompleted: 1, simDate: new DateOnly(2026, 11, 2), hasCurrent: false)).NewStamp);

        // End of duty (no open day), a new plan the same sim date: same FO.
        Assert.Null(FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, simDate: new DateOnly(2026, 11, 1), drawnFor: "sim:2026-11-01", introDone: true)).NewStamp);

        // End of duty, a new plan on a new sim date: a draw.
        Assert.Equal("sim:2026-11-02", FoRosterTriggerCore.Evaluate(Inputs(legBegan: true, simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01")).NewStamp);
    }

    [Fact]
    public void Trigger_IntroOncePerStamp_InPreflightOnly_AndNeverWhenOff()
    {
        var due = FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true, drawnFor: "sim:2026-11-01", introDone: false));
        Assert.True(due.Intro);

        Assert.False(FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true, drawnFor: "sim:2026-11-01", introDone: true)).Intro);
        Assert.False(FoRosterTriggerCore.Evaluate(Inputs(inPreflight: false, drawnFor: "sim:2026-11-01")).Intro);
        Assert.False(FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true, drawnFor: "sim:2026-11-01", introEnabled: false)).Intro);
        Assert.False(FoRosterTriggerCore.Evaluate(Inputs(inPreflight: true, drawnFor: "sim:2026-11-01", hasCurrent: false)).Intro);
    }

    [Fact]
    public void Trigger_Pinned_AdoptsTheNewSimDay_SoTheIntroRepeatsPerDay_ButNobodyIsDrawn()
    {
        var decision = FoRosterTriggerCore.Evaluate(Inputs(pinned: true, legBegan: true, simDate: new DateOnly(2026, 11, 2), drawnFor: "sim:2026-11-01", introDone: true, inPreflight: true));
        Assert.Equal("sim:2026-11-02", decision.NewStamp); // the caller adopts instead of drawing in pinned mode
        Assert.True(decision.Intro);
    }
}
