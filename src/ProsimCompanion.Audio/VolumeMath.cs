namespace ProsimCompanion.Audio;

/// <summary>Knob-value scaling shared by both backends.</summary>
public static class VolumeMath
{
    /// <summary>ProSim ACP knob analogs run 0–1024.</summary>
    public const int KnobMax = 1024;

    public const float VoiceMeeterMinDb = -60f;
    public const float VoiceMeeterMaxDb = 12f;

    /// <summary>Raw knob value → 0..1 scalar (linear, no taper — Windows session volumes take
    /// the scalar directly).</summary>
    public static float Normalize(double rawKnob) =>
        Math.Clamp((float)(rawKnob / KnobMax), 0f, 1f);

    /// <summary>0..1 scalar → VoiceMeeter gain: −60 dB … +12 dB, deliberately past unity at
    /// full knob (predecessor-documented behaviour; 0 dB sits at ~83 %).</summary>
    public static float ToVoiceMeeterGainDb(float normalized) =>
        Math.Clamp(VoiceMeeterMinDb + (normalized * (VoiceMeeterMaxDb - VoiceMeeterMinDb)),
            VoiceMeeterMinDb, VoiceMeeterMaxDb);

    /// <summary>Inverse of <see cref="ToVoiceMeeterGainDb"/> for read-backs.</summary>
    public static float FromVoiceMeeterGainDb(float gainDb) =>
        Math.Clamp((gainDb - VoiceMeeterMinDb) / (VoiceMeeterMaxDb - VoiceMeeterMinDb), 0f, 1f);
}
