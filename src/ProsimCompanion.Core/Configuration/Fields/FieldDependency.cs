using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;

namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// One condition of a field's disabled-when rule: <c>[!]path[=value]</c> (see
/// <see cref="OptionFieldAttribute.DependsOn"/>). The path is read on the object that owns the
/// described property; each leading <c>../</c> steps up to that object's owner.
/// </summary>
/// <param name="Path">Property chain, dot-separated, after the <c>../</c> prefixes.</param>
/// <param name="Ups">How many owners to step up before walking the path.</param>
/// <param name="Negated">True for <c>!path</c>: the condition holds when the value does NOT.</param>
/// <param name="EqualTo">The text the value must equal; null = the value must be truthy
/// (a true bool, a non-blank string, a non-null object).</param>
public sealed record FieldCondition(string Path, int Ups, bool Negated, string? EqualTo)
{
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Properties = new();

    /// <summary>Parses one <c>[!]../path[=value]</c> condition.</summary>
    public static FieldCondition Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        var negated = trimmed.StartsWith('!');
        if (negated)
        {
            trimmed = trimmed[1..].TrimStart();
        }

        string? equalTo = null;
        var equals = trimmed.IndexOf('=', StringComparison.Ordinal);
        if (equals >= 0)
        {
            equalTo = trimmed[(equals + 1)..].Trim();
            trimmed = trimmed[..equals].Trim();
        }

        var ups = 0;
        while (trimmed.StartsWith("../", StringComparison.Ordinal))
        {
            ups++;
            trimmed = trimmed[3..];
        }

        if (trimmed.Length == 0)
        {
            throw new FormatException($"A field dependency needs a property path: '{text}'.");
        }

        return new FieldCondition(trimmed, ups, negated, equalTo);
    }

    /// <summary>Evaluates the condition against the owner chain (<c>[root, …, owner]</c>).</summary>
    public bool Holds(IReadOnlyList<object> ownerChain)
    {
        ArgumentNullException.ThrowIfNull(ownerChain);
        if (ownerChain.Count == 0)
        {
            throw new ArgumentException("The owner chain must hold at least the owning object.", nameof(ownerChain));
        }

        var index = ownerChain.Count - 1 - Ups;
        if (index < 0)
        {
            throw new InvalidOperationException(
                $"Field dependency '{Path}' steps up {Ups} owner(s) but the field has only {ownerChain.Count - 1}.");
        }

        object? current = ownerChain[index];
        foreach (var segment in Path.Split('.'))
        {
            if (current is null)
            {
                break;
            }

            var property = Properties.GetOrAdd((current.GetType(), segment), static pair =>
                pair.Item1.GetProperty(pair.Item2, BindingFlags.Public | BindingFlags.Instance));
            current = property is null
                ? throw new InvalidOperationException(
                    $"Field dependency '{Path}': {ownerChain[index].GetType().Name} has no property '{segment}'.")
                : property.GetValue(current);
        }

        var holds = EqualTo is null ? IsTruthy(current) : string.Equals(ValueText(current), EqualTo, StringComparison.OrdinalIgnoreCase);
        return Negated ? !holds : holds;
    }

    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrWhiteSpace(s),
        _ => true,
    };

    /// <summary>The settings-file spelling of a value: enums as their camelCase JSON name,
    /// numbers invariant — what the pages compared against ("silero", "manual", "ollama").</summary>
    public static string ValueText(object? value) => value switch
    {
        null => "",
        Enum e => EnumText.ToJsonName(e),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}

/// <summary>
/// One entry of a field's <see cref="OptionFieldAttribute.DependsOn"/> rule: alternatives
/// separated by <c>|</c>, one of which must hold. Entries themselves are separated by <c>;</c>
/// and all must hold — so <c>"Enabled; !CommsHandover.Enabled"</c> reads "enabled, and the
/// hand-over block off", and <c>"AutoDeIce | Deice.AutoRequest=ask | Deice.AutoRequest=auto"</c>
/// reads "any of these".
/// </summary>
public sealed record FieldDependency(IReadOnlyList<FieldCondition> Alternatives)
{
    /// <summary>Parses a whole <see cref="OptionFieldAttribute.DependsOn"/> value; null or
    /// blank = no dependencies (always enabled).</summary>
    public static IReadOnlyList<FieldDependency> ParseAll(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return [];
        }

        return spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => new FieldDependency(
                entry.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(FieldCondition.Parse)
                    .ToArray()))
            .ToArray();
    }

    /// <summary>True when every dependency holds (one alternative each).</summary>
    public static bool AllHold(IReadOnlyList<FieldDependency> dependencies, IReadOnlyList<object> ownerChain)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        foreach (var dependency in dependencies)
        {
            if (!dependency.Alternatives.Any(condition => condition.Holds(ownerChain)))
            {
                return false;
            }
        }

        return true;
    }
}
