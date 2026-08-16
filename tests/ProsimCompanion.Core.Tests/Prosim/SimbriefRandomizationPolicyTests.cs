using ProsimCompanion.Prosim.Simbrief;
using Xunit;

namespace ProsimCompanion.Core.Tests.Prosim;

/// <summary>
/// The pure per-OFP randomization latch decision (issue #64: import-with-randomization ran six
/// times during one boarding and re-rolled the pax count every time — re-imports of the SAME
/// OFP must be idempotent, while a new OFP or an explicit user re-import re-randomizes).
/// </summary>
public sealed class SimbriefRandomizationPolicyTests
{
    private static SimbriefRandomizationLatch Latch(string key = "ABCD1234")
        => new(key, [true, false, true], PaxCount: 89, CargoKg: 2500);

    [Fact]
    public void SameOfp_NotForced_Reuses()
        => Assert.True(SimbriefRandomizationPolicy.ShouldReuse(Latch(), "ABCD1234", force: false));

    [Fact]
    public void SameOfp_ForcedByTheUser_Rerandomizes()
        => Assert.False(SimbriefRandomizationPolicy.ShouldReuse(Latch(), "ABCD1234", force: true));

    [Fact]
    public void DifferentOfp_Rerandomizes()
        => Assert.False(SimbriefRandomizationPolicy.ShouldReuse(Latch(), "WXYZ9999", force: false));

    [Fact]
    public void NoLatchYet_Rerandomizes()
        => Assert.False(SimbriefRandomizationPolicy.ShouldReuse(null, "ABCD1234", force: false));

    [Fact]
    public void OfpWithoutIdentity_NeverReuses()
        => Assert.False(SimbriefRandomizationPolicy.ShouldReuse(Latch(""), "", force: false));

    [Fact]
    public void OfpKeyIsCaseSensitive_RequestIdsAreOpaqueTokens()
        => Assert.False(SimbriefRandomizationPolicy.ShouldReuse(Latch("abcd1234"), "ABCD1234", force: false));

    // ---- OfpKey: request id first, flight number + date fallback ----

    [Fact]
    public void OfpKey_PrefersRequestId()
        => Assert.Equal("ABCD1234", SimbriefRandomizationPolicy.OfpKey(
            " ABCD1234 ", "BAW123", new DateTimeOffset(2026, 8, 15, 7, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void OfpKey_FallsBackToFlightNumberAndDate()
        => Assert.Equal("BAW123|2026-08-15", SimbriefRandomizationPolicy.OfpKey(
            "", "baw123", new DateTimeOffset(2026, 8, 15, 7, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void OfpKey_NoIdentityAtAll_IsEmpty()
    {
        Assert.Equal("", SimbriefRandomizationPolicy.OfpKey(null, null, null));
        Assert.Equal("", SimbriefRandomizationPolicy.OfpKey("", "BAW123", null));
        Assert.Equal("", SimbriefRandomizationPolicy.OfpKey("", "", new DateTimeOffset(2026, 8, 15, 7, 0, 0, TimeSpan.Zero)));
    }
}
