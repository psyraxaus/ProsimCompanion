using System.Globalization;

namespace ProsimCompanion.Core.State;

/// <summary>Lifecycle of the link to the VoicemeeterBridge agent.</summary>
public enum MixerConnectionState
{
    /// <summary>The mixer feature is off, or no host is configured.</summary>
    Disabled,

    /// <summary>Not connected; the client keeps retrying with backoff.</summary>
    Disconnected,

    /// <summary>Socket open, waiting for the agent's <c>welcome</c>.</summary>
    Connecting,

    /// <summary>Hello accepted — sets and watches work (Voicemeeter itself may still be closed:
    /// see <see cref="MixerVoicemeeterStatus.Connected"/>).</summary>
    Connected,
}

/// <summary>What the agent reports about Voicemeeter on its PC (<c>welcome</c>/<c>status</c>).
/// <paramref name="Kind"/> and <paramref name="Version"/> are null while it is closed.</summary>
public sealed record MixerVoicemeeterStatus(bool Connected, string? Kind, string? Version)
{
    public static MixerVoicemeeterStatus Unknown { get; } = new(false, null, null);
}

/// <summary>A cached parameter value in its natural type: gains/mutes are numbers, labels and
/// device names are text. Exactly one of the two is set.</summary>
public readonly record struct MixerValue(double? Number, string? Text)
{
    public static MixerValue FromNumber(double value) => new(value, null);

    public static MixerValue FromText(string value) => new(null, value);

    /// <summary>The numeric value, or <paramref name="fallback"/> for a text parameter.</summary>
    public double AsDouble(double fallback = 0) => Number ?? fallback;

    public override string ToString() => Number is { } n ? n.ToString("0.##", CultureInfo.InvariantCulture) : Text ?? "";
}

/// <summary>Outcome of one <c>set</c>. Never thrown: <see cref="Code"/> is <c>ok</c>, a
/// client-synthesised <c>not_connected</c> / <c>timeout</c>, or the agent's error string
/// (<c>unknown parameter</c>, <c>voicemeeter not running</c>, …).</summary>
public sealed record MixerSetResult(bool Ok, string Code)
{
    public static MixerSetResult Success { get; } = new(true, "ok");

    public static MixerSetResult Failed(string code) => new(false, code);
}

/// <summary>Outcome of a Test-button probe against a host/port/token that may differ from the
/// saved ones.</summary>
public sealed record MixerProbeResult(bool Ok, string Message, MixerVoicemeeterStatus? Voicemeeter);

/// <summary>One strip or bus of the remote Voicemeeter, as read after each connect
/// (<c>Strip[n].Label</c> / <c>Bus[n].Label</c> via <c>get</c>; names the agent reports as
/// unknown are left out, so the list matches the edition that is running).</summary>
public sealed record MixerChannel(bool IsBus, int Index, string Label)
{
    /// <summary>"Strip 3" or "Bus A1" — Voicemeeter's own numbering for the edition.</summary>
    public string Name(string? kind) => MixerChannelNames.Name(IsBus, Index, kind);

    /// <summary>Name plus the label the pilot gave the channel, when there is one.</summary>
    public string DisplayName(string? kind) =>
        string.IsNullOrWhiteSpace(Label) ? Name(kind) : $"{Name(kind)} — {Label}";

    public string GainParameter => MixerChannelNames.Parameter(IsBus, Index, "Gain");

    public string MuteParameter => MixerChannelNames.Parameter(IsBus, Index, "Mute");

    public string LabelParameter => MixerChannelNames.Parameter(IsBus, Index, "Label");
}

/// <summary>Voicemeeter's channel numbering per edition: strips count from 1; buses are
/// A1…An then B1…Bm (Standard 1+1, Banana 3+2, Potato 5+3). Unknown edition → "Bus n".</summary>
public static class MixerChannelNames
{
    /// <summary>Highest index this client probes — Potato has 8 strips and 8 buses.</summary>
    public const int MaxIndex = 8;

    public static string Parameter(bool isBus, int index, string property) =>
        $"{(isBus ? "Bus" : "Strip")}[{index}].{property}";

    public static string Name(bool isBus, int index, string? kind)
    {
        if (!isBus)
        {
            return $"Strip {index + 1}";
        }

        var physical = kind?.ToLowerInvariant() switch
        {
            "standard" => 1,
            "banana" => 3,
            "potato" => 5,
            _ => -1,
        };
        if (physical < 0)
        {
            return $"Bus {index + 1}";
        }

        return index < physical ? $"Bus A{index + 1}" : $"Bus B{index - physical + 1}";
    }

    /// <summary>Parses "Strip[2].Gain" / "Bus[0].Mute" back into its parts; false for any
    /// other shape (a hand-typed parameter the editor shows as free text).</summary>
    public static bool TryParse(string? parameter, out bool isBus, out int index, out string property)
    {
        isBus = false;
        index = 0;
        property = "";
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return false;
        }

        var open = parameter.IndexOf('[', StringComparison.Ordinal);
        var close = parameter.IndexOf("].", StringComparison.Ordinal);
        if (open <= 0 || close < open || close + 2 >= parameter.Length)
        {
            return false;
        }

        var kind = parameter[..open];
        if (kind.Equals("Strip", StringComparison.OrdinalIgnoreCase))
        {
            isBus = false;
        }
        else if (kind.Equals("Bus", StringComparison.OrdinalIgnoreCase))
        {
            isBus = true;
        }
        else
        {
            return false;
        }

        if (!int.TryParse(parameter[(open + 1)..close], System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        property = parameter[(close + 2)..];
        return property.Length > 0 && !property.Contains('.', StringComparison.Ordinal) && !property.Contains('[', StringComparison.Ordinal);
    }
}

public sealed class MixerParameterChangedEventArgs(string parameter, MixerValue value) : EventArgs
{
    public string Parameter { get; } = parameter;

    public MixerValue Value { get; } = value;
}

/// <summary>
/// The remote mixer link, as the rest of the app sees it (implemented in ProsimCompanion.Audio).
/// Every member is safe to call while the agent is unreachable: sets complete with a failure
/// code, watches queue up for the next session, the cache keeps its last values. Events fire
/// on the receive thread — consumers marshal to their own context.
/// </summary>
public interface IMixerClient
{
    MixerConnectionState State { get; }

    MixerVoicemeeterStatus Voicemeeter { get; }

    /// <summary>Raised on a <see cref="State"/> or <see cref="Voicemeeter"/> change.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised for every <c>snapshot</c>/<c>changed</c> value from the agent.</summary>
    event EventHandler<MixerParameterChangedEventArgs>? ParameterChanged;

    /// <summary>The remote Voicemeeter's strips and buses with their labels, read after each
    /// connect; empty until the first successful read (the editors then fall back to generic
    /// numbers). Kept across a drop so a page still shows names while reconnecting.</summary>
    IReadOnlyList<MixerChannel> Channels { get; }

    /// <summary>Raised after <see cref="Channels"/> was (re)read.</summary>
    event EventHandler? ChannelsChanged;

    /// <summary>Reads the labels again (the pilot renamed a strip in Voicemeeter, or picked
    /// another edition). Returns the fresh list, or the cached one when not connected.</summary>
    Task<IReadOnlyList<MixerChannel>> RefreshChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Last known value of a parameter this client has seen.</summary>
    bool TryGetValue(string parameter, out MixerValue value);

    /// <summary>Copy of the whole value cache.</summary>
    IReadOnlyDictionary<string, MixerValue> Values();

    /// <summary>Adds names to the watch list. Sent now when connected, and replayed after every
    /// reconnect. Watching a name twice is harmless.</summary>
    void Watch(IEnumerable<string> parameters);

    void Unwatch(IEnumerable<string> parameters);

    /// <summary>Writes one numeric parameter and completes on the matching <c>result</c> (or a
    /// timeout / disconnect). Booleans go as 1/0.</summary>
    Task<MixerSetResult> SetAsync(string parameter, double value, CancellationToken cancellationToken = default);

    /// <summary>Opens a throwaway socket, sends <c>hello</c> and reports the <c>welcome</c> —
    /// the Test button on the settings page, working off unsaved values.</summary>
    Task<MixerProbeResult> ProbeAsync(string host, int port, string token, CancellationToken cancellationToken = default);
}
