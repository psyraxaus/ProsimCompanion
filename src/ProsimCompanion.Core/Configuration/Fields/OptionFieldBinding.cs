using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// One option property as a settings page addresses it: the property chain from the section
/// root (<c>o =&gt; o.Humanize.Tempo</c>), its description, and the read/write/default/enabled
/// operations the renderer needs. Built from the page's selector expression and cached per
/// chain, so a page re-rendering a hundred fields pays reflection once. The expression is the
/// same shape <see cref="SettingsWriter.Set{TOptions, TValue}"/> accepts — a property chain on
/// the parameter, nothing else — so a rename breaks the build, never the page.
/// </summary>
public sealed class OptionFieldBinding
{
    private static readonly ConcurrentDictionary<string, OptionFieldBinding> Cache = new(StringComparer.Ordinal);

    private readonly IReadOnlyList<PropertyInfo> _chain;

    private OptionFieldBinding(Type rootType, IReadOnlyList<PropertyInfo> chain)
    {
        RootType = rootType;
        _chain = chain;
        Description = OptionFieldCatalog.Require(chain[^1]);
    }

    /// <summary>The type the selector starts from (the section class, or a list item's class).</summary>
    public Type RootType { get; }

    /// <summary>The described property at the end of the chain.</summary>
    public OptionFieldDescription Description { get; }

    /// <summary>The property's CLR type.</summary>
    public Type ValueType => Description.ValueType;

    /// <summary>The field's description from a selector such as <c>o =&gt; o.Volume</c> or
    /// <c>o =&gt; o.Humanize.Tempo</c>. A boxing conversion the compiler adds for a value-typed
    /// member is seen through.</summary>
    public static OptionFieldBinding For(LambdaExpression selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var rootType = selector.Parameters.Count == 1
            ? selector.Parameters[0].Type
            : throw new ArgumentException("The selector must take the options object as its one parameter.", nameof(selector));

        var chain = new List<PropertyInfo>();
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert)
        {
            body = convert.Operand;
        }

        while (body is MemberExpression { Member: PropertyInfo property } member)
        {
            chain.Insert(0, property);
            body = member.Expression!;
        }

        if (body is not ParameterExpression || chain.Count == 0)
        {
            throw new ArgumentException(
                "The selector must be a property chain on the options parameter, e.g. o => o.Volume or o => o.Humanize.Tempo.",
                nameof(selector));
        }

        var id = rootType.FullName + ":" + string.Join('.', chain.Select(p => p.Name));
        return Cache.GetOrAdd(id, _ => new OptionFieldBinding(rootType, chain));
    }

    /// <summary>The objects from the root down to the one that owns the property.</summary>
    public IReadOnlyList<object> OwnerChain(object root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var owners = new List<object>(_chain.Count) { root };
        var current = root;
        for (var i = 0; i < _chain.Count - 1; i++)
        {
            current = _chain[i].GetValue(current)
                ?? throw new InvalidOperationException($"{_chain[i].DeclaringType?.Name}.{_chain[i].Name} is null; nested option blocks must be initialised.");
            owners.Add(current);
        }

        return owners;
    }

    /// <summary>The owning object (the last of <see cref="OwnerChain"/>).</summary>
    public object Owner(object root) => OwnerChain(root)[^1];

    /// <summary>The property's current value on <paramref name="root"/>.</summary>
    public object? Get(object root) => Description.Property.GetValue(Owner(root));

    /// <summary>Writes the property on <paramref name="root"/>, converting a dropdown's text
    /// to the property's type where needed (enum names, int selects).</summary>
    public void Set(object root, object? value)
        => Description.Property.SetValue(Owner(root), Coerce(value, Description.Property));

    /// <summary>The upper bound of a Range field, read like <see cref="Get"/>.</summary>
    public object? GetRangeEnd(object root)
        => Description.RangeEndProperty?.GetValue(Owner(root));

    /// <summary>The upper bound of a Range field, written like <see cref="Set"/>.</summary>
    public void SetRangeEnd(object root, object? value)
    {
        var property = Description.RangeEndProperty
            ?? throw new InvalidOperationException($"{Description.Name} is not a Range field.");
        property.SetValue(Owner(root), Coerce(value, property));
    }

    /// <summary>True unless a <see cref="OptionFieldAttribute.DependsOn"/> condition fails.</summary>
    public bool IsEnabled(object root)
        => Description.Dependencies.Count == 0 || FieldDependency.AllHold(Description.Dependencies, OwnerChain(root));

    /// <summary>The value's text for a dropdown: enums by camelCase JSON name, numbers
    /// invariant, strings as they are.</summary>
    public string SelectText(object root) => FieldCondition.ValueText(Get(root));

    private static object? Coerce(object? value, PropertyInfo property)
    {
        var type = property.PropertyType;
        if (value is null || type.IsInstanceOfType(value))
        {
            return value;
        }

        if (value is string text)
        {
            if (type.IsEnum)
            {
                // A name the enum lacks falls back to the property's own default (what the
                // hand-written page bridges did: OpenAi, FasterWhisper, Auto, Allow) rather
                // than throwing out of a dropdown handler.
                return EnumText.Parse(type, text, DefaultOf(property) ?? Enum.GetValues(type).GetValue(0)!);
            }

            if (type == typeof(int))
            {
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? i
                    : throw new FormatException($"'{text}' is not a whole number for {property.Name}.");
            }

            if (type == typeof(double))
            {
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                    ? d
                    : throw new FormatException($"'{text}' is not a number for {property.Name}.");
            }
        }

        return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    private static readonly ConcurrentDictionary<PropertyInfo, object?> Defaults = new();

    /// <summary>The property's out-of-the-box value: read off a fresh instance of its declaring
    /// type (every option class carries its defaults as initialisers), never duplicated here.</summary>
    public static object? DefaultOf(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Defaults.GetOrAdd(property, static p =>
        {
            var type = p.DeclaringType;
            if (type is null || type.GetConstructor(Type.EmptyTypes) is null)
            {
                return null;
            }

            return p.GetValue(Activator.CreateInstance(type));
        });
    }
}
