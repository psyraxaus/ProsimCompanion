using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>The per-flight gate suffix question (issue #156).</summary>
public sealed class GateChoiceStoreTests : IDisposable
{
    private readonly GroundOpsSignals _signals = new();
    private readonly GateChoiceStore _store;
    private readonly List<GateChoiceQuestion> _raised = [];

    private static readonly IReadOnlyList<GateCandidate> Variants =
        [new("36A", "Gate W 36A", null), new("36B", "Gate W 36B", "Safedock")];

    public GateChoiceStoreTests()
    {
        _store = new GateChoiceStore(_signals);
        _store.QuestionRaised += (_, q) => _raised.Add(q);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Ask_RaisesOnce_PerGate()
    {
        _store.Ask("W36", Variants, "W36 is 36A or 36B in this scenery — which?");
        _store.Ask("W36", Variants, "W36 is 36A or 36B in this scenery — which?");

        Assert.Single(_raised);
        Assert.Equal("W36", _store.Snapshot().Question?.Gate);

        _store.Ask("W40", Variants, "W40 is …");
        Assert.Equal(2, _raised.Count);
        Assert.Equal("W40", _store.Snapshot().Question?.Gate);
    }

    [Theory]
    [InlineData("36B", "36B")]
    [InlineData("36 b", "36B")]
    [InlineData("gate w 36a", "36A")]
    [InlineData("Gate W 36B", "36B")]
    public void Choose_MatchesLabelOrToken(string answer, string expectedLabel)
    {
        _store.Ask("W36", Variants, "which?");

        Assert.True(_store.Choose(answer, "web"));

        var snapshot = _store.Snapshot();
        Assert.Null(snapshot.Question);
        Assert.Equal(expectedLabel, snapshot.Choice?.Candidate.Label);
        Assert.Equal("web", snapshot.Choice?.Source);
        Assert.Equal("W36", snapshot.Choice?.Gate);
    }

    [Fact]
    public void Choose_UnknownOrNoQuestion_IsFalse()
    {
        Assert.False(_store.Choose("36A", "web"));

        _store.Ask("W36", Variants, "which?");
        Assert.False(_store.Choose("36C", "web"));
        Assert.NotNull(_store.Snapshot().Question);
    }

    [Fact]
    public void FlightCycleReset_ClearsEverything()
    {
        _store.Ask("W36", Variants, "which?");
        _store.Choose("36A", "voice");

        _signals.RaiseFlightCycleReset();

        Assert.Equal(GateChoiceSnapshot.Empty, _store.Snapshot());
    }

    [Fact]
    public void ClearQuestion_WithdrawsIt()
    {
        _store.Ask("W36", Variants, "which?");
        _store.ClearQuestion();
        Assert.Null(_store.Snapshot().Question);
    }
}
