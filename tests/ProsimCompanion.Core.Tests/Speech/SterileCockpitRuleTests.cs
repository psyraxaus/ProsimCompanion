using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

public sealed class SterileCockpitRuleTests
{
    private readonly SpeechOptions _options = new();
    private SterileCockpitRule Rule => new(() => _options);

    private static SpeechContext Ctx(FlightPhase phase, double altitude, bool valid = true)
        => new(phase, altitude, valid);

    private static SpeechRequest Req(SpeechPriority priority, string? tag = null)
        => new("text", priority, Tag: tag);

    [Theory]
    [InlineData(FlightPhase.InitialClimb)]
    [InlineData(FlightPhase.Climb)]
    [InlineData(FlightPhase.Descent)]
    [InlineData(FlightPhase.Approach)]
    public void SterilePhasesBelowCeiling_LowIsSuppressed(FlightPhase phase)
        => Assert.Equal(SpeechSuppressionVerdict.Suppress,
            Rule.Evaluate(Req(SpeechPriority.Low), Ctx(phase, 5000)));

    [Theory]
    [InlineData(FlightPhase.Cruise)]          // cruise deliberately excluded
    [InlineData(FlightPhase.TaxiOut)]         // ground phases are never sterile
    [InlineData(FlightPhase.TakeoffRoll)]
    [InlineData(FlightPhase.LandingRollout)]
    [InlineData(FlightPhase.Preflight)]
    public void NonSterilePhases_LowIsAllowed(FlightPhase phase)
        => Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Low), Ctx(phase, 5000)));

    [Fact]
    public void AboveCeiling_Allowed()
        => Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Low), Ctx(FlightPhase.Climb, 12_000)));

    [Fact]
    public void InvalidSnapshot_Allowed()
        => Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Low), Ctx(FlightPhase.Climb, 5000, valid: false)));

    [Fact]
    public void ZeroAltitude_MeansNoData_Allowed()
        => Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Low), Ctx(FlightPhase.Climb, 0)));

    [Theory]
    [InlineData(SpeechPriority.High)]
    [InlineData(SpeechPriority.Critical)]
    public void HighAndCritical_NeverGated(SpeechPriority priority)
        => Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(priority), Ctx(FlightPhase.Approach, 3000)));

    [Fact]
    public void CabinTaggedReports_ExemptByDefault()
        => Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Low, tag: "cabin.ready"), Ctx(FlightPhase.Approach, 3000)));

    [Fact]
    public void NormalPolicy_DefaultAllow()
        // Normal carries checklists/briefings — required during a low-altitude approach.
        => Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Normal), Ctx(FlightPhase.Approach, 3000)));

    [Fact]
    public void NormalPolicy_Defer()
    {
        _options.SterileNormalPolicy = SterileNormalPolicy.Defer;
        Assert.Equal(SpeechSuppressionVerdict.Defer,
            Rule.Evaluate(Req(SpeechPriority.Normal), Ctx(FlightPhase.Approach, 3000)));
    }

    [Fact]
    public void NormalPolicy_Suppress()
    {
        _options.SterileNormalPolicy = SterileNormalPolicy.Suppress;
        Assert.Equal(SpeechSuppressionVerdict.Suppress,
            Rule.Evaluate(Req(SpeechPriority.Normal), Ctx(FlightPhase.Approach, 3000)));
    }

    [Fact]
    public void SuppressLowOff_LowAllowed()
    {
        _options.SterileSuppressLow = false;
        Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Low), Ctx(FlightPhase.Approach, 3000)));
    }

    [Fact]
    public void SterileDisabled_EverythingAllowed()
    {
        _options.SterileEnabled = false;
        Assert.Equal(SpeechSuppressionVerdict.Allow,
            Rule.Evaluate(Req(SpeechPriority.Low), Ctx(FlightPhase.Approach, 3000)));
    }
}
