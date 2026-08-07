using Moq;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Commands;
using ProsimCompanion.Core.Commands.Handlers;
using Xunit;

namespace ProsimCompanion.Core.Tests.Commands;

public sealed class OfpCommandHandlerTests
{
    [Theory]
    [InlineData(SimbriefImportOutcome.Imported, CommandOutcome.Success)]
    [InlineData(SimbriefImportOutcome.AlreadyImported, CommandOutcome.AlreadySatisfied)]
    [InlineData(SimbriefImportOutcome.NoPilotId, CommandOutcome.PreconditionFailed)]
    [InlineData(SimbriefImportOutcome.FetchFailed, CommandOutcome.Failed)]
    [InlineData(SimbriefImportOutcome.ImportFailed, CommandOutcome.Failed)]
    public void FromImportOutcome_MapsEveryImporterOutcome(
        SimbriefImportOutcome importOutcome, CommandOutcome expected)
    {
        var result = OfpCommandHandlers.FromImportOutcome(importOutcome);

        Assert.Equal(expected, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason)); // outcome + reason, never a bare ack
    }

    [Fact]
    public async Task Fetch_ForcesReimport()
    {
        var importer = new Mock<ISimbriefImporter>(MockBehavior.Strict);
        importer
            .Setup(i => i.TryImportAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SimbriefImportOutcome.Imported);

        var registry = new CommandRegistry();
        OfpCommandHandlers.Register(registry, importer.Object);

        var result = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "ofp.fetch", new EmptyCommandRequest());

        Assert.Equal(CommandOutcome.Success, result.Outcome);
        importer.VerifyAll();
    }

    [Fact]
    public async Task Fetch_WithAbsentImporter_IsUnavailable()
    {
        var registry = new CommandRegistry();
        OfpCommandHandlers.Register(registry, importer: null);

        var result = await registry.ExecuteAsync<EmptyCommandRequest, CommandResult>(
            "ofp.fetch", new EmptyCommandRequest());

        Assert.Equal(CommandOutcome.Unavailable, result.Outcome);
    }
}
