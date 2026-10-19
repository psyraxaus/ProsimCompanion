using System.Collections.Concurrent;
using System.Reflection;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// An <see cref="OptionFieldAttribute"/> resolved against its property: the attribute's data
/// plus what the type system adds (the value type, whether it is an enum, the parsed
/// dependency rule, the paired range property) and the checks the attribute itself cannot
/// make (a Range needs an upper bound, a Select a choice list, a Number a numeric property).
/// Built once per property by <see cref="OptionFieldCatalog"/> and shared by the renderer,
/// the normaliser and the completeness test.
/// </summary>
public sealed class OptionFieldDescription
{
    internal OptionFieldDescription(PropertyInfo property, OptionFieldAttribute spec)
    {
        Property = property;
        Spec = spec;
        ValueType = property.PropertyType;
        Dependencies = FieldDependency.ParseAll(spec.DependsOn);
        Validate();
    }

    /// <summary>The described property.</summary>
    public PropertyInfo Property { get; }

    /// <summary>The declaration as written on the property.</summary>
    public OptionFieldAttribute Spec { get; }

    /// <summary>The property's CLR type (<c>string</c> for a nullable string too).</summary>
    public Type ValueType { get; }

    /// <summary>The parsed disabled-when rule; empty = always enabled.</summary>
    public IReadOnlyList<FieldDependency> Dependencies { get; }

    /// <summary>Shorthand for <see cref="OptionFieldAttribute.Kind"/>.</summary>
    public FieldKind Kind => Spec.Kind;

    /// <summary>Shorthand for <see cref="OptionFieldAttribute.TextId"/>.</summary>
    public string TextId => Spec.TextId;

    /// <summary>True for an enum-typed property (rendered as a Select over the camelCase names).</summary>
    public bool IsEnum => ValueType.IsEnum;

    /// <summary>The sibling property holding a Range's upper bound; null for other kinds.</summary>
    public PropertyInfo? RangeEndProperty
        => Kind == FieldKind.Range ? Property.DeclaringType!.GetProperty(Spec.RangeEnd!, BindingFlags.Public | BindingFlags.Instance) : null;

    /// <summary>"GsxOptions.ChockDelayMinSec" — for messages and the completeness test.</summary>
    public string Name => $"{Property.DeclaringType?.Name}.{Property.Name}";

    private void Validate()
    {
        var owner = Property.DeclaringType?.Name ?? "?";
        if (Kind != FieldKind.RangeUpper && string.IsNullOrWhiteSpace(TextId))
        {
            throw Invalid("needs a text id root");
        }

        if (!Property.CanWrite || Property.SetMethod?.IsPublic != true)
        {
            throw Invalid("must have a public setter");
        }

        switch (Kind)
        {
            case FieldKind.Bool when ValueType != typeof(bool):
                throw Invalid("is a Bool field but not a bool");
            case FieldKind.Number or FieldKind.Range or FieldKind.RangeUpper when ValueType != typeof(int) && ValueType != typeof(double):
                throw Invalid("is a numeric field but neither int nor double");
            case FieldKind.Text or FieldKind.VoicePicker when ValueType != typeof(string):
                throw Invalid("is a text field but not a string");
            case FieldKind.Select when ValueType != typeof(string) && ValueType != typeof(int) && !IsEnum:
                throw Invalid("is a Select but neither string, int nor enum");
            case FieldKind.Select when string.IsNullOrWhiteSpace(Spec.Choices):
                throw Invalid("is a Select without a Choices list name");
            case FieldKind.Range when string.IsNullOrWhiteSpace(Spec.RangeEnd):
                throw Invalid("is a Range without RangeEnd");
            case FieldKind.Range when RangeEndProperty is null || RangeEndProperty.PropertyType != ValueType:
                throw Invalid($"names RangeEnd '{Spec.RangeEnd}', which is not a sibling of the same type");
            case FieldKind.RangeUpper when string.IsNullOrWhiteSpace(Spec.RangeStart)
                || Property.DeclaringType!.GetProperty(Spec.RangeStart!)?.GetCustomAttribute<OptionFieldAttribute>()?.RangeEnd != Property.Name:
                throw Invalid("is a RangeUpper whose RangeStart does not name it back as RangeEnd");
            case FieldKind.Text when Spec.EmptyAsNull && !IsNullableString():
                throw Invalid("is EmptyAsNull but not a nullable string");
        }

        InvalidOperationException Invalid(string what)
            => new($"Option field {owner}.{Property.Name} {what}.");
    }

    private bool IsNullableString()
        => ValueType == typeof(string) && new NullabilityInfoContext().Create(Property).WriteState == NullabilityState.Nullable;
}

/// <summary>
/// Finds the field descriptions of an options type (candidate C3). Reflection once per type,
/// cached — the renderer asks on every render, the normaliser on every save.
/// </summary>
public static class OptionFieldCatalog
{
    private static readonly ConcurrentDictionary<PropertyInfo, OptionFieldDescription?> ByProperty = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<OptionFieldDescription>> ByType = new();

    /// <summary>The description of one property, or null when the property carries none.</summary>
    public static OptionFieldDescription? Describe(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ByProperty.GetOrAdd(property, static p =>
            p.GetCustomAttribute<OptionFieldAttribute>() is { } spec ? new OptionFieldDescription(p, spec) : null);
    }

    /// <summary>The description of a property named on a type; throws for a property without one,
    /// because a page asked to render it.</summary>
    public static OptionFieldDescription Require(PropertyInfo property)
        => Describe(property) ?? throw new InvalidOperationException(
            $"{property?.DeclaringType?.Name}.{property?.Name} has no [OptionField] description — "
            + "declare one beside the property before binding it to a settings page.");

    /// <summary>Every described property of a type, in declaration order.</summary>
    public static IReadOnlyList<OptionFieldDescription> Describe(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ByType.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(Describe)
            .Where(d => d is not null)
            .Select(d => d!)
            .ToArray());
    }
}
