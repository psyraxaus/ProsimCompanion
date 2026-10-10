using ProsimCompanion.Core.Aircraft;

namespace ProsimCompanion.Speech.Recognition;

/// <summary>
/// The ProSim push-to-talk switches a settings key can name (2026-10-10): the sidestick and
/// hand-mic PTT datarefs, 0 normal / 1 pushed. Shared by the SayIntentions PTT relay
/// (<c>sayIntentions.pttSource</c>) and the ATC mute (<c>speech.atcMuteSource</c>) so one
/// cockpit button can key SayIntentions and silence the FO through the same dataref.
/// </summary>
public static class ProsimPttSwitches
{
    public const string None = "none";

    /// <summary>Settings keys with their labels, in page order.</summary>
    public static IReadOnlyList<(string Key, string Label)> Sources { get; } =
    [
        (None, "Off"),
        ("captainSidestick", "Captain sidestick PTT"),
        ("foSidestick", "First Officer sidestick PTT"),
        ("captainHandMic", "Captain hand mic PTT"),
        ("foHandMic", "First Officer hand mic PTT"),
        ("observerHandMic", "Observer hand mic PTT"),
    ];

    /// <summary>The switch behind a key; null for <c>none</c>, blank or unknown.</summary>
    public static DataRef<int>? SourceRef(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "captainsidestick" => ProsimDataRefNames.SidestickPttCapt,
        "fosidestick" => ProsimDataRefNames.SidestickPttFo,
        "captainhandmic" => ProsimDataRefNames.HandMicPttCapt,
        "fohandmic" => ProsimDataRefNames.HandMicPttFo,
        "observerhandmic" => ProsimDataRefNames.HandMicPttObs,
        _ => null,
    };

    public static bool IsOff(string? source) =>
        string.IsNullOrWhiteSpace(source) || source.Trim().Equals(None, StringComparison.OrdinalIgnoreCase);
}
