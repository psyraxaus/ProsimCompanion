using ProsimCompanion.Core.Flight;
using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

public sealed class GsxAutomationPhaseMapperTests
{
    [Theory]
    [InlineData(FlightPhase.Unknown, GsxAutomationPhase.SessionStart)]
    [InlineData(FlightPhase.ColdAndDark, GsxAutomationPhase.Preparation)]
    [InlineData(FlightPhase.Preflight, GsxAutomationPhase.Preparation)]
    [InlineData(FlightPhase.PushbackAndStart, GsxAutomationPhase.PushBack)]
    [InlineData(FlightPhase.TaxiOut, GsxAutomationPhase.TaxiOut)]
    [InlineData(FlightPhase.TakeoffRoll, GsxAutomationPhase.TaxiOut)]
    [InlineData(FlightPhase.InitialClimb, GsxAutomationPhase.Flight)]
    [InlineData(FlightPhase.Cruise, GsxAutomationPhase.Flight)]
    [InlineData(FlightPhase.Approach, GsxAutomationPhase.Flight)]
    [InlineData(FlightPhase.LandingRollout, GsxAutomationPhase.TaxiIn)]
    [InlineData(FlightPhase.TaxiIn, GsxAutomationPhase.TaxiIn)]
    [InlineData(FlightPhase.Shutdown, GsxAutomationPhase.Arrival)]
    public void Map_CoversTheFlightModel(FlightPhase flight, GsxAutomationPhase expected)
        => Assert.Equal(expected, GsxAutomationPhaseMapper.Map(flight));
}
