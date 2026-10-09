using ProsimCompanion.Core.Configuration;

namespace ProsimCompanion.Audio.Mixer;

/// <summary>
/// The pure maths of a mapping: input range → output dB range for a level, threshold → 1/0
/// for a toggle, either flipped by <c>invert</c>. No clock, no socket — the unit tests cover
/// every branch. Level output is rounded to 0.1 dB: an ACP knob step is ~0.07 dB across the
/// default span, so rounding halves the writes without a hearable difference.
/// </summary>
public static class MixerMappingMath
{
    /// <summary>Linear scale with clamping. A degenerate input range (min == max) returns
    /// the output minimum rather than dividing by zero.</summary>
    public static double Level(double input, double inputMin, double inputMax, double outputMin, double outputMax, bool invert)
    {
        var span = inputMax - inputMin;
        if (span == 0 || double.IsNaN(span) || double.IsNaN(input))
        {
            return outputMin;
        }

        var t = Math.Clamp((input - inputMin) / span, 0.0, 1.0);
        if (invert)
        {
            t = 1.0 - t;
        }

        return Math.Round(outputMin + (t * (outputMax - outputMin)), 1);
    }

    /// <summary>1 when the input is at or above the threshold, else 0; <paramref name="invert"/>
    /// swaps them (a REC latch reads 1 = unmuted, a Mute parameter wants 1 = muted).</summary>
    public static double Toggle(double input, double threshold, bool invert)
    {
        var on = !double.IsNaN(input) && input >= threshold;
        return on ^ invert ? 1 : 0;
    }

    /// <summary>Applies one configured mapping to a ProSim value.</summary>
    public static double Apply(MixerMapping mapping, double input)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return mapping.Kind == MixerMappingKind.Toggle
            ? Toggle(input, mapping.Threshold, mapping.Invert)
            : Level(input, mapping.InputMin, mapping.InputMax, mapping.OutputMinDb, mapping.OutputMaxDb, mapping.Invert);
    }
}
