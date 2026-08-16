namespace ProsimCompanion.Core.Configuration;

/// <summary>One registered option section: its JSON name, CLR type, and a factory producing a
/// fresh instance carrying every property default.</summary>
public sealed record OptionSectionDescriptor(
    string SectionName,
    Type OptionsType,
    Func<object> CreateDefaults);

/// <summary>
/// Every option section registered through <c>AddOptionSection&lt;T&gt;</c>, in registration order.
/// <see cref="SettingsDefaultsWriter"/> enumerates this instead of a hand-maintained list, so a
/// section that binds is a section whose defaults reach the file — the Persona/UpdateCheck drift
/// (campaign #84) cannot recur.
/// </summary>
public sealed class OptionSectionRegistry
{
    private readonly List<OptionSectionDescriptor> _sections = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    /// <summary>Registered sections in registration order (registration order is also the order
    /// sections first appear in a freshly written settings file).</summary>
    public IReadOnlyList<OptionSectionDescriptor> Sections => _sections;

    /// <summary>Adds a section; a duplicate section name is a composition bug and throws.</summary>
    public void Add(OptionSectionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_names.Add(descriptor.SectionName))
        {
            throw new InvalidOperationException(
                $"Option section '{descriptor.SectionName}' is registered twice "
                + $"(second registration: {descriptor.OptionsType.Name}).");
        }

        _sections.Add(descriptor);
    }
}
