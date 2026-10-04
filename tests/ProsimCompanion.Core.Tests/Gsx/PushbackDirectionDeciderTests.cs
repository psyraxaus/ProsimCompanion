using ProsimCompanion.Core.Airports.Parking;
using ProsimCompanion.Core.State;
using ProsimCompanion.Gsx.Menu;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>What answers GSX's "Select pushback direction" menu (2026-10-04): the pilot's
/// per-flight choice, the fixed legacy preference, the confident suggestion, or a question.</summary>
public sealed class PushbackDirectionDeciderTests
{
    private static readonly string[] DefaultMenu = ["Nose Right/Tail Left (LEFT)", "Nose Left/Tail Right (RIGHT)", "Straight pushback", "QuickEdit Pushback"];
    private static readonly string[] EfhkMenu = ["Facing SW on Taxi AV", "Facing NE on Taxi AT", "Facing South (V2)", "Straight pushback"];

    private static PushbackChoiceSnapshot State(PushbackChoice? choice = null, PushbackSuggestion? suggestion = null, IReadOnlyList<PushbackOption>? options = null)
        => PushbackChoiceSnapshot.Empty with { Choice = choice, Suggestion = suggestion, Options = options ?? [] };

    private static PushbackSuggestion Suggest(PushbackOptionKind kind, PushbackConfidence confidence)
        => new(new PushbackOption(kind == PushbackOptionKind.Left ? "Facing SW on Taxi AV" : "Facing NE on Taxi AT", kind, kind == PushbackOptionKind.Left ? 227 : 47, "profile"), confidence, "test", 50);

    [Fact]
    public void PilotChoice_Wins_OverEverything()
    {
        var decision = PushbackDirectionDecider.Decide(DefaultMenu, "tailRight", true,
            State(PushbackChoice.TailLeft("voice", "said"), Suggest(PushbackOptionKind.Right, PushbackConfidence.High)));

        Assert.Equal(PushbackDecisionKind.Pick, decision.Kind);
        Assert.Equal("Nose Right/Tail Left (LEFT)", decision.Entry);
        Assert.Contains("pilot chose tail left", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PilotCompassChoice_MapsToTheRouteOnCustomLines()
    {
        var known = new[]
        {
            new PushbackOption("Facing SW on Taxi AV", PushbackOptionKind.Left, 227, "profile"),
            new PushbackOption("Facing NE on Taxi AT", PushbackOptionKind.Right, 47, "profile"),
        };
        var decision = PushbackDirectionDecider.Decide(EfhkMenu, "auto", true,
            State(PushbackChoice.Facing(45, "voice", "facing north east"), options: known));

        Assert.Equal(PushbackDecisionKind.Pick, decision.Kind);
        Assert.Equal("Facing NE on Taxi AT", decision.Entry);
    }

    [Fact]
    public void PilotCompassChoice_NoRouteFits_Asks()
    {
        var decision = PushbackDirectionDecider.Decide(DefaultMenu, "auto", true,
            State(PushbackChoice.Facing(90, "voice", "facing east")));

        Assert.Equal(PushbackDecisionKind.Ask, decision.Kind);
    }

    [Theory]
    [InlineData("tailLeft", "Nose Right/Tail Left (LEFT)")]
    [InlineData("tailRight", "Nose Left/Tail Right (RIGHT)")]
    [InlineData("straight", "Straight pushback")]
    public void LegacyFixedPreference_StillPicks(string preference, string expected)
    {
        var decision = PushbackDirectionDecider.Decide(DefaultMenu, preference, true, State());
        Assert.Equal(PushbackDecisionKind.Pick, decision.Kind);
        Assert.Equal(expected, decision.Entry);
    }

    [Fact]
    public void Ask_Mode_AlwaysAsks()
    {
        var decision = PushbackDirectionDecider.Decide(DefaultMenu, "ask", false, State(suggestion: Suggest(PushbackOptionKind.Right, PushbackConfidence.High)));
        Assert.Equal(PushbackDecisionKind.Ask, decision.Kind);
    }

    [Fact]
    public void Auto_ConfidentSuggestion_Picks_ByKindOnDefaultLines()
    {
        var decision = PushbackDirectionDecider.Decide(DefaultMenu, "auto", true, State(suggestion: Suggest(PushbackOptionKind.Right, PushbackConfidence.High)));
        Assert.Equal(PushbackDecisionKind.Pick, decision.Kind);
        Assert.Equal("Nose Left/Tail Right (RIGHT)", decision.Entry);
        Assert.StartsWith("suggested", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_ConfidentSuggestion_Picks_ByLabelOnCustomLines()
    {
        var decision = PushbackDirectionDecider.Decide(EfhkMenu, "auto", true, State(suggestion: Suggest(PushbackOptionKind.Right, PushbackConfidence.High)));
        Assert.Equal("Facing NE on Taxi AT", decision.Entry);
    }

    [Fact]
    public void Auto_LowSuggestion_AsksOrLeaves()
    {
        var low = State(suggestion: Suggest(PushbackOptionKind.Right, PushbackConfidence.Low));
        Assert.Equal(PushbackDecisionKind.Ask, PushbackDirectionDecider.Decide(DefaultMenu, "auto", true, low).Kind);
        Assert.Equal(PushbackDecisionKind.Leave, PushbackDirectionDecider.Decide(DefaultMenu, "auto", false, low).Kind);
    }

    [Fact]
    public void Auto_NoSuggestion_Asks()
    {
        var decision = PushbackDirectionDecider.Decide(DefaultMenu, "auto", true, State());
        Assert.Equal(PushbackDecisionKind.Ask, decision.Kind);
        Assert.Contains("no suggestion", decision.Reason, StringComparison.Ordinal);
        Assert.Equal(3, decision.Options.Count); // QuickEdit excluded
    }

    [Fact]
    public void Auto_SingleDirectionMenu_PicksIt()
    {
        var decision = PushbackDirectionDecider.Decide(["Nose Left/Tail Right (RIGHT)", "Straight pushback"], "auto", true, State());
        Assert.Equal(PushbackDecisionKind.Pick, decision.Kind);
        Assert.Equal("Nose Left/Tail Right (RIGHT)", decision.Entry);
    }

    [Fact]
    public void NoState_Auto_Asks_NotGuesses()
    {
        var decision = PushbackDirectionDecider.Decide(DefaultMenu, "auto", true, null);
        Assert.Equal(PushbackDecisionKind.Ask, decision.Kind);
    }
}
