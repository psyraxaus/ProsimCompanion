using ProsimCompanion.Core.State;
using Xunit;

namespace ProsimCompanion.Core.Tests.State;

/// <summary>2026-09-20: the TTS router re-published "Connected" after every utterance and
/// every call re-rendered the web layout and the WPF window — Set must be quiet when nothing
/// changed.</summary>
public sealed class ConnectionStatusStoreTests
{
    [Fact]
    public void Set_RaisesChanged_OnlyWhenTheValueChanges()
    {
        var store = new ConnectionStatusStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Set(Subsystems.Tts, ConnectionState.Connecting);
        store.Set(Subsystems.Tts, ConnectionState.Connected);
        store.Set(Subsystems.Tts, ConnectionState.Connected);
        store.Set(Subsystems.Tts, ConnectionState.Connected);
        store.Set(Subsystems.Asr, ConnectionState.Connected);
        store.Set(Subsystems.Tts, ConnectionState.Disconnected);

        Assert.Equal(4, raised);
        Assert.Equal(ConnectionState.Disconnected, store.Snapshot().Single(p => p.Key == Subsystems.Tts).Value);
    }

    /// <summary>Issue #158: a subsystem that is off says why, and the web shows it.</summary>
    [Fact]
    public void Set_WithReason_KeepsIt_UntilTheNextSet()
    {
        var store = new ConnectionStatusStore();

        store.Set(Subsystems.Prosim, ConnectionState.Disabled, "  The ProSim SDK does not match.  ");
        Assert.Equal("The ProSim SDK does not match.", store.ReasonOf(Subsystems.Prosim));
        Assert.Equal(ConnectionState.Disabled, store.Snapshot().Single(p => p.Key == Subsystems.Prosim).Value);

        store.Set(Subsystems.Prosim, ConnectionState.Connected);
        Assert.Null(store.ReasonOf(Subsystems.Prosim));
        Assert.Null(store.ReasonOf(Subsystems.Gsx));
    }

    [Fact]
    public void Set_RaisesChanged_WhenOnlyTheReasonChanges()
    {
        var store = new ConnectionStatusStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Set(Subsystems.Prosim, ConnectionState.Disabled, "No ProSim SDK folder is set.");
        store.Set(Subsystems.Prosim, ConnectionState.Disabled, "No ProSim SDK folder is set.");
        store.Set(Subsystems.Prosim, ConnectionState.Disabled, "ProSimSDK.dll was not found.");
        store.Set(Subsystems.Prosim, ConnectionState.Disabled, " ");

        Assert.Equal(3, raised);
        Assert.Null(store.ReasonOf(Subsystems.Prosim));
    }

    [Fact]
    public void Set_FirstValue_AlwaysRaises()
    {
        var store = new ConnectionStatusStore();
        var raised = 0;
        store.Changed += (_, _) => raised++;

        store.Set(Subsystems.Gsx, ConnectionState.Disabled);

        Assert.Equal(1, raised);
    }
}
