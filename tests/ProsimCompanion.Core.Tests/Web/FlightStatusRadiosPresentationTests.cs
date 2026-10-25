using ProsimCompanion.Core.Radios;
using ProsimCompanion.Web.Components;
using Xunit;

namespace ProsimCompanion.Core.Tests.Web;

/// <summary>Radios tile (owner request 2026-10-25): the verdict never says WRONG without both
/// sides known, and the station chips mark active and assigned.</summary>
public sealed class FlightStatusRadiosPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 16, 35, 0, TimeSpan.Zero);

    private static RadioSnapshot Live(int activeKhz, RadioAssignment? assigned, bool siOn = true)
        => RadioSnapshot.Empty with
        {
            Com1ActiveKhz = activeKhz,
            Com1StandbyKhz = 121_800,
            Com1Live = true,
            Assigned = assigned,
            SayIntentionsOn = siOn,
            Airports =
            [
                new AirportStations("EFHK",
                [
                    new AtcStation("GROUND", "Helsinki Ground", 118_125),
                    new AtcStation("TOWER", "", 119_700),
                    new AtcStation("DELIVERY", "", 118_125),
                ], Now),
            ],
        };

    private static readonly RadioAssignment Ground = new("GROUND", 118_125, RadioAssignmentSource.CorrectFrequency, Now);

    [Fact]
    public void OnFrequency_IsMatch_WithTheTuneButtonHidden()
    {
        var view = FlightStatusPresentation.Radios(Live(118_125, Ground), "EFHK");

        Assert.Equal("match", view.StateClass);
        Assert.Equal("flightStatus.radios.match", view.StateTextId);
        Assert.Equal("118.125", view.Active);
        Assert.Equal("121.800", view.Standby);
        Assert.Equal("Ground", view.AssignedStation);
        Assert.Equal("118.125", view.AssignedFrequency);
        Assert.False(view.CanTuneAssigned);
    }

    [Fact]
    public void WrongFrequency_IsMismatch_WithTheTuneButton()
    {
        var view = FlightStatusPresentation.Radios(Live(121_800, Ground), "EFHK");

        Assert.Equal("mismatch", view.StateClass);
        Assert.True(view.CanTuneAssigned);
        Assert.Equal(118_125, view.AssignedKhz);
    }

    [Fact]
    public void NoAssignment_IsUnknown_NeverWrong()
    {
        var view = FlightStatusPresentation.Radios(Live(121_800, null), "EFHK");

        Assert.Equal("unknown", view.StateClass);
        Assert.Equal("flightStatus.radios.unknown", view.StateTextId);
        Assert.Equal("flightStatus.radios.noAssignment", view.HintTextId);
        Assert.False(view.CanTuneAssigned);
    }

    [Fact]
    public void SayIntentionsOff_SaysSo_WithTheSettingsHint()
    {
        var view = FlightStatusPresentation.Radios(Live(121_800, null, siOn: false), "EFHK");

        Assert.Equal("off", view.StateClass);
        Assert.Equal("flightStatus.radios.offHint", view.HintTextId);
    }

    [Fact]
    public void ProsimGone_WithAnAssignment_IsUnknownAndShowsDashes()
    {
        var view = FlightStatusPresentation.Radios(Live(121_800, Ground) with { Com1Live = false }, "EFHK");

        Assert.Equal("unknown", view.StateClass);
        Assert.Equal("flightStatus.radios.noProsim", view.StateTextId);
        Assert.Equal("---.---", view.Active);
        Assert.False(view.CanTuneAssigned);
    }

    [Fact]
    public void Stations_DedupeByFrequency_MarkActiveAndAssigned_UseCallsignOrTypeWord()
    {
        var view = FlightStatusPresentation.Radios(Live(119_700, Ground), "EFHK");

        Assert.Equal("EFHK", view.StationsIcao);
        Assert.Equal(2, view.Stations.Count);
        var ground = view.Stations[0];
        Assert.Equal("Helsinki Ground", ground.Label);
        Assert.True(ground.IsAssigned);
        Assert.False(ground.IsActive);
        var tower = view.Stations[1];
        Assert.Equal("Tower", tower.Label);
        Assert.Equal("119.700", tower.Frequency);
        Assert.True(tower.IsActive);
        Assert.False(tower.IsAssigned);
    }

    [Fact]
    public void Stations_ForAnotherAirport_AreEmpty()
    {
        var view = FlightStatusPresentation.Radios(Live(119_700, Ground), "EGLL");

        Assert.Empty(view.Stations);
        Assert.Equal("", view.StationsIcao);
    }
}
