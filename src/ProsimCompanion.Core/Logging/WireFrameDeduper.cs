namespace ProsimCompanion.Core.Logging;

/// <summary>
/// Drops a wire frame that repeats the previous frame on the same link byte for byte
/// (2026-10-17). GSX pushes its "startup" model event again and again — 10 376 frames in the
/// 2026-10-10 wire trace, most of them identical — and the trace is for reading a protocol
/// exchange, not for counting heartbeats. The next different frame carries how many repeats
/// were skipped, so nothing is hidden, only folded. Thread-safe: frames arrive on the
/// transports' own threads.
/// </summary>
public sealed class WireFrameDeduper
{
    private readonly Dictionary<string, (string Payload, int Repeats)> _last = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// True when the frame should be written. <paramref name="suppressed"/> is how many
    /// identical frames were folded on this link since the last written one (reported on the
    /// first different frame; 0 otherwise).
    /// </summary>
    public bool Accept(string channel, string direction, string payload, out int suppressed)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(direction);
        ArgumentNullException.ThrowIfNull(payload);

        var link = channel + "|" + direction;
        lock (_gate)
        {
            if (_last.TryGetValue(link, out var previous))
            {
                if (string.Equals(previous.Payload, payload, StringComparison.Ordinal))
                {
                    _last[link] = (payload, previous.Repeats + 1);
                    suppressed = 0;
                    return false;
                }

                suppressed = previous.Repeats;
            }
            else
            {
                suppressed = 0;
            }

            _last[link] = (payload, 0);
            return true;
        }
    }

    /// <summary>Forgets every link — used when the trace is switched on again, so the first
    /// frame of the new capture is always written.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _last.Clear();
        }
    }
}
