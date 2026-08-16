using ProsimCompanion.Speech.Crew;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>Spoken-line composition for the cold-and-dark mismatch advisory (issue #63):
/// at most three discrepancies read out, the rest summarized as a count.</summary>
public sealed class AircraftStateAdvisoryTests
{
    [Fact]
    public void OneMismatch_ReadsPlainly()
        => Assert.Equal(
            "Captain, the aircraft is not in the expected cold and dark state — battery 1 is on.",
            AircraftStateAdvisoryService.ComposeAdvisory(["battery 1 is on"]));

    [Fact]
    public void TwoMismatches_JoinWithAnd()
        => Assert.Equal(
            "Captain, the aircraft is not in the expected cold and dark state — battery 1 is on, "
            + "and the parking brake is off.",
            AircraftStateAdvisoryService.ComposeAdvisory(
                ["battery 1 is on", "the parking brake is off"]));

    [Fact]
    public void ThreeMismatches_OxfordJoin()
        => Assert.Equal(
            "Captain, the aircraft is not in the expected cold and dark state — battery 1 is on, "
            + "the beacon is on, and the parking brake is off.",
            AircraftStateAdvisoryService.ComposeAdvisory(
                ["battery 1 is on", "the beacon is on", "the parking brake is off"]));

    [Fact]
    public void MoreThanThree_SummarizesTheRemainder()
        => Assert.Equal(
            "Captain, the aircraft is not in the expected cold and dark state — a, b, and c, "
            + "and 2 more items.",
            AircraftStateAdvisoryService.ComposeAdvisory(["a", "b", "c", "d", "e"]));

    [Fact]
    public void ExactlyFour_SingularRemainder()
        => Assert.EndsWith("and 1 more item.", AircraftStateAdvisoryService.ComposeAdvisory(
            ["a", "b", "c", "d"]));
}
