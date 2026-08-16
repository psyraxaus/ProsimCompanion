using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Non-generic view of a page draft so <see cref="SettingsWriter"/> can save a mixed set of
/// sections in one atomic file write.
/// </summary>
public abstract class OptionsDraft
{
    /// <summary>True when the draft differs from the values it was loaded from.</summary>
    public abstract bool IsDirty { get; }

    /// <summary>Discards edits: re-clones the draft from the live options and re-baselines.</summary>
    public abstract void Reload();

    internal abstract string Section { get; }

    internal abstract JsonObject SerializeDraft();

    internal abstract JsonObject Baseline { get; }

    internal abstract void Rebaseline(JsonObject serialized);
}

/// <summary>
/// An editable clone of one option section for a settings page (campaign #84). The page binds
/// its inputs straight to <see cref="Value"/>'s properties; dirty tracking and persistence are
/// JSON diffs against the load-time baseline, so a save writes exactly the keys the user
/// changed — keys co-owned by services (e.g. <c>gsx.fuelFobSaved</c>) and hand edits made while
/// the page was open are never clobbered, and there are no hand-written key strings anywhere.
/// </summary>
public sealed class OptionsDraft<TOptions> : OptionsDraft
    where TOptions : class, IOptionSection
{
    private readonly Func<TOptions> _current;
    private JsonObject _baseline = [];

    public OptionsDraft(Func<TOptions> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
        Value = Clone(out _baseline);
    }

    /// <summary>The editable clone. Mutating it never touches the live bound options; only
    /// <see cref="SettingsWriter.Save"/> persists.</summary>
    public TOptions Value { get; private set; }

    public override bool IsDirty => !JsonNode.DeepEquals(SerializeDraft(), _baseline);

    public override void Reload() => Value = Clone(out _baseline);

    internal override string Section => TOptions.SectionName;

    internal override JsonObject SerializeDraft()
        => JsonSerializer.SerializeToNode(Value, SettingsJson.DraftOptions) as JsonObject ?? [];

    internal override JsonObject Baseline => _baseline;

    internal override void Rebaseline(JsonObject serialized) => _baseline = serialized;

    private TOptions Clone(out JsonObject baseline)
    {
        baseline = JsonSerializer.SerializeToNode(_current(), SettingsJson.DraftOptions) as JsonObject ?? [];
        return baseline.Deserialize<TOptions>(SettingsJson.DraftOptions)
            ?? throw new InvalidOperationException(
                $"Options type {typeof(TOptions).Name} did not survive a JSON round-trip.");
    }
}
