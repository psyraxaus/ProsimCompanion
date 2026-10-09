namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// The pure maths of a remote mapping: knob travel → gain over the configured dB span, REC
/// push-button → Mute, latchless dial → Mute at the bottom of its travel. No clock, no
/// socket — the unit tests cover every branch. Gain is rounded to 0.1 dB: an ACP knob step
/// is ~0.07 dB across the default span, so rounding halves the writes without a hearable
/// difference.
/// </summary>
public static class MixerMappingMath
{
    /// <summary>Linear scale with clamping. A degenerate input range (min == max) returns
    /// the output minimum rather than dividing by zero.</summary>
    public static double Level(double input, double inputMin, double inputMax, double outputMin, double outputMax)
    {
        var span = inputMax - inputMin;
        if (span == 0 || double.IsNaN(span) || double.IsNaN(input))
        {
            return outputMin;
        }

        var t = Math.Clamp((input - inputMin) / span, 0.0, 1.0);
        return Math.Round(outputMin + (t * (outputMax - outputMin)), 1);
    }

    /// <summary>ACP knob (0–1024) → gain in dB over <paramref name="minDb"/>…<paramref name="maxDb"/>.</summary>
    public static double GainDb(double rawKnob, double minDb, double maxDb) =>
        Level(rawKnob, 0, VolumeMath.KnobMax, minDb, maxDb);

    /// <summary>REC latch 0 = muted → Mute 1; latch 1 = open → Mute 0.</summary>
    public static double MuteFromLatch(int latch) => latch == 0 ? 1 : 0;

    /// <summary>A dial with no push-button (the loudspeaker) mutes at the bottom of its travel.</summary>
    public static double MuteFromDial(double rawKnob) => VolumeMath.IsDialAtZero(rawKnob) ? 1 : 0;
}
