using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Checklists;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The taxi checklist's flap-config read-back parser (issue #125): digit and word
/// forms both arrive from the ASR ("Config 1 plus F." was the owner's exact phrasing), and a
/// generic acknowledgement carries no config.</summary>
public sealed class FlapConfigAnswerTests
{
    [Theory]
    [InlineData("config one plus f", 1)]
    [InlineData("Config 1 plus F.", 1)]
    [InlineData("one plus f", 1)]
    [InlineData("1 plus F", 1)]
    [InlineData("config one", 1)]
    [InlineData("flaps one", 1)]
    [InlineData("config two", 2)]
    [InlineData("config 2", 2)]
    [InlineData("flaps 3", 3)]
    [InlineData("config three", 3)]
    public void SpokenConfigs_ParseToTheHandleDetent(string answer, int expected)
        => Assert.Equal(expected, FlapConfigAnswer.TryParse(answer));

    [Theory]
    [InlineData("set")]
    [InlineData("checked")]
    [InlineData("as required")]
    [InlineData("")]
    public void GenericAnswers_CarryNoConfig(string answer)
        => Assert.Null(FlapConfigAnswer.TryParse(answer));

    [Fact]
    public void ConfigFull_DoesNotParse()
        // Takeoff never uses FULL — a "config full" answer must fail the read-back the way a
        // wrong number would, not sail through.
        => Assert.Null(FlapConfigAnswer.TryParse("config full"));

    [Theory]
    [InlineData(1, "config one plus F")]
    [InlineData(2, "config two")]
    [InlineData(3, "config three")]
    [InlineData(0, "not set")]
    [InlineData(null, "unavailable")]
    public void ReadBack_SpeaksTheDetent(int? detent, string expected)
        => Assert.Equal(expected, FlapConfigAnswer.Spoken(detent));

    [Theory]
    [InlineData(FlightPhase.TaxiOut, true)]
    [InlineData(FlightPhase.TakeoffRoll, true)]
    [InlineData(FlightPhase.Preflight, false)]
    [InlineData(FlightPhase.Cruise, false)]
    [InlineData(FlightPhase.LandingRollout, false)]
    public void ChronoCall_OnlyArmsOnTheRoll(FlightPhase phase, bool armed)
        // Issue #126: "takeoff" said anywhere else must fall through to normal routing.
        => Assert.Equal(armed, ChronoCallFeature.CallArmed(phase));
}
