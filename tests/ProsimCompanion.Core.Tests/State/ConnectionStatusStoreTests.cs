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
