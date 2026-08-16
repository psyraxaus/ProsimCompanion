using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// The config binder APPENDS array items to a list the options class already initialized
/// (2026-08 round-4 smoke test: the departure order arrived doubled and every service triggered
/// twice). The fix — clear list defaults before the file binds, restore them after when the file
/// omitted the key entirely — used to be hand-patched per section (gsx, audio, sop) and is
/// declared once here so every section registered through <c>AddOptionSection&lt;T&gt;</c> gets it
/// (campaign #84). Top-level lists only: a guard test asserts no registered options type carries
/// a non-empty default list or dictionary below its top level.
/// </summary>
internal static class OptionsListBinding
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ListProperties = new();

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

    private static PropertyInfo[] ListPropertiesOf(Type type)
        => ListProperties.GetOrAdd(type, static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && typeof(IList).IsAssignableFrom(p.PropertyType))
            .ToArray());
}
