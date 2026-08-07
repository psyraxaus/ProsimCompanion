using Microsoft.Extensions.Logging.Abstractions;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Tests.TechLog;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Company;
using Xunit;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>The <see cref="ICompanyChannel.DeliverMessage"/> push seam company day mode uses.</summary>
public sealed class CompanyDeliverMessageTests
{
    private readonly FakeArbiter _arbiter = new();
    private readonly CompanyOptions _options = new();

    private CompanyChannelService Create() => new(
        new FakeDataRefs(),
        new FlightStateEngine(new FakeFlightSource(), NullLogger<FlightStateEngine>.Instance),
        _arbiter,
        new OfpStore(),
        OptionsSupport.Monitor(_options),
        SpeechTestSupport.TempEventLog(),
        NullLogger<CompanyChannelService>.Instance);

    [Fact]
    public void DeliverMessage_SpeaksLowWithCompanyChimeAndDayTag()
    {
        var service = Create();
        service.DeliverMessage("Next sector, EGCC to EGLL, flight BA124.");

        var request = Assert.Single(_arbiter.Requests);
        Assert.Equal("Next sector, EGCC to EGLL, flight BA124.", request.Text);
        Assert.Equal(SpeechPriority.Low, request.Priority);
        Assert.Equal("company.day", request.Tag);
        Assert.Equal("company", request.Chime);
    }

    [Fact]
    public void DeliverMessage_RespectsTheChimeOption()
    {
        _options.Chime = false;
        var service = Create();
        service.DeliverMessage("Gate will be advised on arrival.");
        Assert.Null(Assert.Single(_arbiter.Requests).Chime);
    }

    [Fact]
    public void DeliverMessage_HasNoEnabledGate_AndIgnoresBlankText()
    {
        _options.Enabled = false; // a pushed message is an explicit request — no gate
        var service = Create();
        service.DeliverMessage("   ");
        Assert.Empty(_arbiter.Requests);

        service.DeliverMessage("Company message.");
        Assert.Single(_arbiter.Requests);
    }

    [Fact]
    public void DeliverMessage_SetsTheRepeatLastMessage()
    {
        var service = Create();
        service.DeliverMessage("Next sector, EGCC to EGLL.");
        Assert.True(service.TryHandle("read last company message"));

        Assert.Equal(2, _arbiter.Requests.Count);
        Assert.Equal("Next sector, EGCC to EGLL.", _arbiter.Requests[1].Text);
        Assert.Equal("company.repeat", _arbiter.Requests[1].Tag);
    }
}
