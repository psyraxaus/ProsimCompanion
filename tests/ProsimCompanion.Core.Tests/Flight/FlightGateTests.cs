using ProsimCompanion.Core.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Flight;

/// <summary>One spelling of "live enough to act on the data" (review 2026-11-05): the session
/// gate AND a valid sample.</summary>
public sealed class FlightGateTests
{
    [Fact]
    public void LiveNeedsTheSessionGateAndAValidSample()
    {
        var valid = new FlightDataSnapshot { IsValid = true };
        Assert.True(FlightGate.IsLiveFor(new FlightStateView(FlightPhase.Cruise, valid, true, IsLive: true)));
        Assert.False(FlightGate.IsLiveFor(new FlightStateView(FlightPhase.Cruise, valid, true, IsLive: false)));
        Assert.False(FlightGate.IsLiveFor(new FlightStateView(FlightPhase.Cruise, null, true, IsLive: true)));
        Assert.False(FlightGate.IsLiveFor(new FlightStateView(FlightPhase.Cruise, new FlightDataSnapshot { IsValid = false }, true, IsLive: true)));
        Assert.True(FlightGate.IsLiveFor(live: true, valid));
        Assert.False(FlightGate.IsLiveFor(live: true, null));
    }
}
