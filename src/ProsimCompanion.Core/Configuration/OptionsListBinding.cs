using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The config binder APPENDS array items to a list the options class already initialized
/// (2026-08 round-4 smoke test: the departure order arrived doubled and every service triggered
/// twice). The fix — clear list defaults before the file binds, restore them after when the file
/// omitted the key entirely — used to be hand-patched per section (gsx, audio, sop) and is
/// declared once here so every section registered through <c>AddOptionSection&lt;T&gt;</c> gets it
/// (campaign #84).
///
/// Nested option objects (an aircraft profile's stored <c>gsxSettings</c> block is a whole
/// <c>GsxOptions</c>) are created BY the binder, so nothing can clear their lists first: the
/// profile's five departure services arrived behind the six defaults and the profile applier
/// wrote eleven into the file (EHAM 2026-11-01, the duplicated Departure Service Order).
/// <see cref="StripAppendedDefaults"/> undoes that after the bind — a list that begins with
/// exactly its type's default items, followed by more, had the defaults appended to — and
/// <see cref="NormalizeGraph"/> walks the object graph so every nested options object is
/// treated the same way and gets its <see cref="IOptionNormalize"/> pass.
/// </summary>
internal static class OptionsListBinding
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ListProperties = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ObjectProperties = new();
    private static readonly JsonSerializerOptions Compare = new() { WriteIndented = false };

    /// <summary>Empties every top-level list so the binder appends into a clean slate.</summary>
    internal static void ClearLists(object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var property in ListPropertiesOf(options.GetType()))
        {
            (property.GetValue(options) as IList)?.Clear();
        }
    }

    /// <summary>Puts the property defaults back into any list the file left empty. An explicit
    /// empty array in the file is indistinguishable from an omitted key — restoring defaults for
    /// both is the pre-#84 behaviour, preserved deliberately.</summary>
    internal static void RestoreEmptyLists(object options, object defaults)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(defaults);
        foreach (var property in ListPropertiesOf(options.GetType()))
        {
            if (property.GetValue(options) is not IList list || list.Count > 0)
            {
                continue;
            }

            if (property.GetValue(defaults) is not IList defaultItems || defaultItems.Count == 0)
            {
                continue;
            }

            foreach (var item in defaultItems)
            {
                list.Add(item);
            }
        }
    }

    /// <summary>Removes a leading run of the type's default items from every list that holds
    /// MORE than the defaults — the binder's append signature on an object it created itself.
    /// A list that is exactly the defaults, or starts differently, is left alone. Returns the
    /// number of lists repaired.</summary>
    internal static int StripAppendedDefaults(object options, object defaults)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(defaults);
        var repaired = 0;
        foreach (var property in ListPropertiesOf(options.GetType()))
        {
            if (property.GetValue(options) is not IList list
                || property.GetValue(defaults) is not IList defaultItems
                || defaultItems.Count == 0
                || list.Count <= defaultItems.Count)
            {
                continue;
            }

            var isPrefix = true;
            for (var i = 0; i < defaultItems.Count && isPrefix; i++)
            {
                isPrefix = SameItem(list[i], defaultItems[i]);
            }

            if (!isPrefix)
            {
                continue;
            }

            for (var i = 0; i < defaultItems.Count; i++)
            {
                list.RemoveAt(0);
            }

            repaired++;
        }

        return repaired;
    }

    /// <summary>The whole post-bind pass for a section: strip appended defaults and run the
    /// type's own normalisation on the section and on every nested options object (a class
    /// from this assembly, directly or inside a list) up to a small depth.</summary>
    internal static void NormalizeGraph(object options, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(options);
        var type = options.GetType();
        if (depth > 0 && TryCreateDefaults(type) is { } defaults)
        {
            StripAppendedDefaults(options, defaults);
        }

        (options as IOptionNormalize)?.Normalize();

        if (depth >= 3)
        {
            return;
        }

        foreach (var property in ObjectPropertiesOf(type))
        {
            var value = property.GetValue(options);
            switch (value)
            {
                case null:
                    break;
                case IList list:
                    foreach (var item in list)
                    {
                        if (item is not null && IsOurs(item.GetType()))
                        {
                            NormalizeGraph(item, depth + 1);
                        }
                    }

                    break;
                default:
                    if (IsOurs(value.GetType()))
                    {
                        NormalizeGraph(value, depth + 1);
                    }

                    break;
            }
        }
    }

    private static object? TryCreateDefaults(Type type)
    {
        try
        {
            return type.GetConstructor(Type.EmptyTypes) is not null ? Activator.CreateInstance(type) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameItem(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.GetType() != b.GetType())
        {
            return false;
        }

        if (a.GetType().IsPrimitive || a is string || a is Enum)
        {
            return a.Equals(b);
        }

        try
        {
            return string.Equals(
                JsonSerializer.Serialize(a, a.GetType(), Compare),
                JsonSerializer.Serialize(b, b.GetType(), Compare),
                StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsOurs(Type type)
        => type.IsClass && !type.IsAbstract && type != typeof(string) && type.Assembly == typeof(OptionsListBinding).Assembly;

    private static PropertyInfo[] ListPropertiesOf(Type type)
        => ListProperties.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && typeof(IList).IsAssignableFrom(p.PropertyType))
            .ToArray());

    private static PropertyInfo[] ObjectPropertiesOf(Type type)
        => ObjectProperties.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                && (typeof(IList).IsAssignableFrom(p.PropertyType) || (p.PropertyType.IsClass && p.PropertyType != typeof(string))))
            .ToArray());
}
