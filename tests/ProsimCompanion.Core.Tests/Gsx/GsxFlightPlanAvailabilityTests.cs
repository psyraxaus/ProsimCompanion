using ProsimCompanion.Gsx.Automation;
using Xunit;

namespace ProsimCompanion.Core.Tests.Gsx;

/// <summary>
/// The pure flight-plan availability rule (issue #60): ProSim's efb.simbriefPlanImported
/// dataref survives ProSim sessions and can read a stale true, so it only counts when
/// corroborated by this-session evidence (the app's own OFP store) — a pilot-loaded MCDU plan
/// always counts on its own.
/// </summary>
public sealed class GsxFlightPlanAvailabilityTests
{
    [Fact]
    public void StaleDatarefAlone_NoAppOfp_NoFmsPlan_IsNotAvailable()
        // The 2026-08-15 flight: leftover imported=true let departure services run plan-less.
        => Assert.False(GsxFlightPlanMonitor.IsFlightPlanAvailable(
            ofpImportedDataref: true, fmsPlanPresent: false, ofpHeldThisSession: false));

    [Fact]
    public void DatarefCorroboratedByThisSessionsImport_IsAvailable()
        => Assert.True(GsxFlightPlanMonitor.IsFlightPlanAvailable(
            ofpImportedDataref: true, fmsPlanPresent: false, ofpHeldThisSession: true));

    [Fact]
    public void FmsPlanAlone_IsAvailable()
        // The pilot typed the plan into the MCDU — no SimBrief involvement required.
        => Assert.True(GsxFlightPlanMonitor.IsFlightPlanAvailable(
            ofpImportedDataref: false, fmsPlanPresent: true, ofpHeldThisSession: false));

    [Fact]
    public void StaleDatarefWithFmsPlan_IsAvailable()
        // The FMS evidence carries it regardless of what the import dataref claims.
        => Assert.True(GsxFlightPlanMonitor.IsFlightPlanAvailable(
            ofpImportedDataref: true, fmsPlanPresent: true, ofpHeldThisSession: false));

    [Fact]
    public void NothingAtAll_IsNotAvailable()
        => Assert.False(GsxFlightPlanMonitor.IsFlightPlanAvailable(
            ofpImportedDataref: false, fmsPlanPresent: false, ofpHeldThisSession: false));

    [Fact]
    public void AppOfpWithoutTheDataref_IsNotAvailable()
        // The dataref cleared after our import (user reset the EFB plan): the leftover app-side
        // OFP alone no longer proves a plan — conservative by design.
        => Assert.False(GsxFlightPlanMonitor.IsFlightPlanAvailable(
            ofpImportedDataref: false, fmsPlanPresent: false, ofpHeldThisSession: true));
}
