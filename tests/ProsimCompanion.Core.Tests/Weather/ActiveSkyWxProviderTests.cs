using ProsimCompanion.Core.Weather;
using Xunit;

namespace ProsimCompanion.Core.Tests.Weather;

public sealed class ActiveSkyWxProviderTests
{
    private const string Metar = "YSSY 070800Z 27012KT CAVOK 22/10 Q1013";

    // ---- snapshot line scan (ICAO::METAR::TAF::windsAloft) ----

    [Fact]
    public void Snapshot_MatchingStation_ReturnsMetarField()
    {
        string[] lines =
        [
            $"YMML::YMML 070800Z 35010KT 9999 SCT030 18/09 Q1017::TAF YMML...::winds",
            $"YSSY::{Metar}::TAF YSSY...::winds",
        ];

        Assert.Equal(Metar, ActiveSkyWxProvider.FindMetarInSnapshot(lines, "YSSY"));
    }

    [Fact]
    public void Snapshot_StarSentinel_IsNoData()
        => Assert.Null(ActiveSkyWxProvider.FindMetarInSnapshot(["YSSY::*::*::*"], "YSSY"));

    [Fact]
    public void Snapshot_MatchedStationWithoutMetar_StopsScanning()
    {
        // One line per station is the file's contract — a later duplicate would be stale, so
        // the first match is authoritative even when it carries no METAR.
        string[] lines =
        [
            "YSSY::*::*::*",
            $"YSSY::{Metar}::TAF::winds",
        ];

        Assert.Null(ActiveSkyWxProvider.FindMetarInSnapshot(lines, "YSSY"));
    }

    [Fact]
    public void Snapshot_PrefixMatch_IsCaseInsensitive()
        => Assert.Equal(Metar, ActiveSkyWxProvider.FindMetarInSnapshot([$"yssy::{Metar}::::"], "YSSY"));

    [Fact]
    public void Snapshot_NoLineForStation_ReturnsNull()
        => Assert.Null(ActiveSkyWxProvider.FindMetarInSnapshot([$"YMML::{Metar}::::"], "YSSY"));

    [Fact]
    public void Snapshot_PrefixMustBeExactStation()
        // "YSS" must not match the "YSSY::" line (the "::" is part of the prefix).
        => Assert.Null(ActiveSkyWxProvider.FindMetarInSnapshot([$"YSSY::{Metar}::::"], "YSS"));

    // ---- API body extraction (live-verified: raw METAR text with empty Content-Type) ----

    [Fact]
    public void ApiBody_RawMetarText_ReturnsMetarLine()
        => Assert.Equal(Metar, ActiveSkyWxProvider.ExtractMetarFromApiBody($"{Metar}\n"));

    [Fact]
    public void ApiBody_JsonMetarField_ReturnsValue()
        => Assert.Equal(Metar, ActiveSkyWxProvider.ExtractMetarFromApiBody(
            "{\"metar\":\"" + Metar + "\"}"));

    [Fact]
    public void ApiBody_NestedJsonField_ReturnsValue()
        => Assert.Equal(Metar, ActiveSkyWxProvider.ExtractMetarFromApiBody(
            "{\"data\":{\"station\":\"YSSY\",\"MetarString\":\"" + Metar + "\"}}"));

    [Fact]
    public void ApiBody_WindOnlyBody_FallsBackToWholeBody()
        => Assert.Equal("27012KT", ActiveSkyWxProvider.ExtractMetarFromApiBody("27012KT"));

    [Fact]
    public void ApiBody_NoMetarAnywhere_ReturnsNull()
        => Assert.Null(ActiveSkyWxProvider.ExtractMetarFromApiBody("No weather available"));
}
