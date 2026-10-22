using System.Text.Json;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Core.Tests.Mixer;

/// <summary>An options monitor the mixer tests drive by hand: <see cref="Update"/> deep-clones
/// the current value, applies the change and notifies every listener, the way a saved
/// settings.json reaches the services.</summary>
internal sealed class FakeMixerOptionsMonitor(MixerOptions value) : IOptionsMonitor<MixerOptions>
{
    private readonly List<Action<MixerOptions, string?>> _listeners = [];

    public MixerOptions CurrentValue { get; private set; } = value;

    public MixerOptions Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<MixerOptions, string?> listener)
    {
        lock (_listeners)
        {
            _listeners.Add(listener);
        }

        return new Unsubscribe(() =>
        {
            lock (_listeners)
            {
                _listeners.Remove(listener);
            }
        });
    }

    public void Update(Action<MixerOptions> mutate)
    {
        var next = JsonSerializer.Deserialize<MixerOptions>(JsonSerializer.Serialize(CurrentValue))!;
        mutate(next);
        CurrentValue = next;
        Action<MixerOptions, string?>[] listeners;
        lock (_listeners)
        {
            listeners = [.. _listeners];
        }

        foreach (var listener in listeners)
        {
            listener(next, null);
        }
    }

    private sealed class Unsubscribe(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
