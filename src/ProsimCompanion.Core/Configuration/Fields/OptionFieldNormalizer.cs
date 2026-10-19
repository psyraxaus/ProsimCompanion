using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// Applies every described property's <see cref="FieldNormalization"/> to a draft before it is
/// saved (candidate C3). Walks the section object, its nested option blocks and the items of
/// its lists (a wake-on-LAN target, an approach gate), touching ONLY properties that carry an
/// <see cref="OptionFieldAttribute"/> — an undescribed string (the app-maintained keys, the
/// power-user lists) is never rewritten, so the diff save keeps leaving them alone.
/// </summary>
public static class OptionFieldNormalizer
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Walkable = new();

    /// <summary>Normalises the described text properties of <paramref name="instance"/> and
    /// everything reachable below it.</summary>
    public static void Apply(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Apply(instance, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    /// <summary>The pure rule: trim, then strip the quotes, then fold the case.</summary>
    public static string Normalize(string value, FieldNormalization rule)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = value;
        if (rule.HasFlag(FieldNormalization.Trim))
        {
            result = result.Trim();
        }

        if (rule.HasFlag(FieldNormalization.StripQuotes))
        {
            result = result.Trim('"');
        }

        if (rule.HasFlag(FieldNormalization.LowerCase))
        {
            result = result.ToLowerInvariant();
        }

        if (rule.HasFlag(FieldNormalization.UpperCase))
        {
            result = result.ToUpperInvariant();
        }

        return result;
    }

    private static void Apply(object instance, HashSet<object> visited)
    {
        if (!visited.Add(instance))
        {
            return;
        }

        foreach (var description in OptionFieldCatalog.Describe(instance.GetType()))
        {
            if (description.ValueType != typeof(string) || description.Spec.Normalize == FieldNormalization.None)
            {
                continue;
            }

            if (description.Property.GetValue(instance) is string text)
            {
                var normalized = Normalize(text, description.Spec.Normalize);
                if (!string.Equals(normalized, text, StringComparison.Ordinal))
                {
                    description.Property.SetValue(instance, normalized);
                }
            }
        }

        foreach (var property in Walkable.GetOrAdd(instance.GetType(), WalkableProperties))
        {
            switch (property.GetValue(instance))
            {
                case null or string:
                    break;
                case IDictionary:
                    break; // JSON-edited power-user structures; never described, never walked.
                case IEnumerable items:
                    foreach (var item in items)
                    {
                        if (item is not null && IsOptionObject(item.GetType()))
                        {
                            Apply(item, visited);
                        }
                    }

                    break;
                case var nested when IsOptionObject(nested.GetType()):
                    Apply(nested, visited);
                    break;
            }
        }
    }

    private static PropertyInfo[] WalkableProperties(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.PropertyType != typeof(string) && !p.PropertyType.IsValueType)
            .ToArray();

    /// <summary>Only this solution's own option blocks and list items are walked — a framework
    /// type below an option class (there are none today) is left alone.</summary>
    private static bool IsOptionObject(Type type)
        => type.Namespace?.StartsWith("ProsimCompanion", StringComparison.Ordinal) is true && !type.IsValueType;
}
