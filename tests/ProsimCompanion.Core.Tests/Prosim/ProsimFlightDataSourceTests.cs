using ProsimCompanion.Core.Tests.Speech;
using ProsimCompanion.Prosim.Flight;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

public sealed class ProsimFlightDataSourceTests
{
    private const string Ias = "aircraft.speed.ias";
    private const string Engine1State = "aircraft.systems.engines.1.state";
    private const string Engine2State = "aircraft.systems.engines.2.state";
    private const string Engine1Running = "aircraft.engines.1.running";
    private const string Engine2Running = "aircraft.engines.2.running";
    private const string Pushback = "groundservice.pushback";

    /// <summary>Pushes a first value into every phase-critical ref, engines off at the gate.</summary>
    private static void MakeReady(FakeDataRefs refs)
    {
        foreach (var name in ProsimFlightDataSource.PhaseCriticalRefs)
        {
            refs.Values[name] = name switch
            {
                Engine1State or Engine2State => "off",
                Engine1Running or Engine2Running => false,
                "system.gates.B_GROUND" => true,
                "system.gates.B_ELEC_BUS_POWER_DC_BAT" => true,
                "aircraft.gearDown" => true,
                // Idle enum value (issue #104) — 0 would read as "push in progress".
                Pushback => 3,
                _ => 0.0,
            };
        }
    }

    [Fact]
    public void Snapshot_NotReady_UntilEveryPhaseCriticalRefHasAValue()
    {
        // Issue #59 (flight test 2026-08-16): ProSim registers the subscription set over
        // several seconds at startup; a snapshot with only the IAS ref live is "valid" but
        // must not be "ready" — the half-populated defaults read as airborne garbage.
        var refs = new FakeDataRefs();
        using var source = new ProsimFlightDataSource(refs);

        Assert.False(source.Sample().IsValid);

        refs.Values[Ias] = 0.0;
        var partial = source.Sample();
        Assert.True(partial.IsValid);
        Assert.False(partial.IsReady);

        MakeReady(refs);
        var ready = source.Sample();
        Assert.True(ready.IsValid);
        Assert.True(ready.IsReady);
    }

    [Fact]
    public void AnyEngineRunning_SurvivesAnUnexpectedStateString()
    {
        // Issue #59: the state string is descriptive and any unexpected value maps to Off
        // (documented hazard) — the raw running boolean must keep the engines "running" so a
        // transient string glitch cannot read as both-engines-off mid-taxi.
        var refs = new FakeDataRefs();
        using var source = new ProsimFlightDataSource(refs);
        MakeReady(refs);

        refs.Values[Engine1State] = "avail??";
        refs.Values[Engine2State] = "avail??";
        refs.Values[Engine1Running] = true;

        Assert.True(source.Sample().AnyEngineRunning);
    }

    [Fact]
    public void AnyEngineRunning_StillDerivedFromTheStateString()
    {
        var refs = new FakeDataRefs();
        using var source = new ProsimFlightDataSource(refs);
        MakeReady(refs);

        refs.Values[Engine2State] = "RUNNING";

        Assert.True(source.Sample().AnyEngineRunning);
    }

    [Fact]
    public void AnyEngineRunning_FalseWhenBothSourcesAgreeOff()
    {
        var refs = new FakeDataRefs();
        using var source = new ProsimFlightDataSource(refs);
        MakeReady(refs);

        Assert.False(source.Sample().AnyEngineRunning);
    }

    [Theory]
    [InlineData(3, false)] // idle — observed parked, taxiing, and airborne (2026-08-23)
    [InlineData(0, true)]  // push in progress — the only active value observed
    [InlineData(1, true)]  // presumed direction variants count as active
    [InlineData(2, true)]
    public void PushbackActive_UsesTheSettledEnum(int raw, bool expected)
    {
        // Issue #104: the pre-fix ">0 = active" mapping was inverted — it latched the flag
        // true for entire flights and blocked the PushbackAndStart→TaxiOut exit.
        var refs = new FakeDataRefs();
        using var source = new ProsimFlightDataSource(refs);
        MakeReady(refs);
        refs.Values[Pushback] = raw;

        Assert.Equal(expected, source.Sample().PushbackActive);
    }

    [Fact]
    public void PushbackActive_MissingRef_ReadsIdle()
    {
        // The descriptor fallback is the IDLE value: a missing dataref must never read as
        // "pushback running" (issue #104).
        var refs = new FakeDataRefs();
        using var source = new ProsimFlightDataSource(refs);
        MakeReady(refs);
        refs.Values.Remove(Pushback);

        Assert.False(source.Sample().PushbackActive);
    }

    [Fact]
    public void TakeoffThrust_RequiresBeingOnTheGround()
    {
        // Issue #101: climb/cruise N1 routinely exceeds the 75% threshold, which kept the
        // flag true for entire flights in telemetry — it must mean thrust set ON THE GROUND.
        var refs = new FakeDataRefs();
        using var source = new ProsimFlightDataSource(refs);
        MakeReady(refs);
        refs.Values[Engine1Running] = true;
        refs.Values["aircraft.engines.1.n1"] = 85.0;

        Assert.True(source.Sample().TakeoffThrustSet);

        refs.Values["system.gates.B_GROUND"] = false;
        refs.Values[Ias] = 250.0;
        Assert.False(source.Sample().TakeoffThrustSet);
    }
}
