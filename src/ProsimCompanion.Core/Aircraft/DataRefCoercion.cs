using System.Globalization;

namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// Converts raw dataref values (whose runtime type varies per variable — bool, int, double,
/// string, …) to the type a consumer asked for. Coercion is deliberately forgiving: simulator
/// data feeds are not a place to throw over a double arriving where an int was expected.
/// </summary>
public static class DataRefCoercion
{
    /// <summary>
    /// Coerces <paramref name="raw"/> to <typeparamref name="T"/>, returning
    /// <paramref name="fallback"/> when the value is null or not convertible.
    /// Numeric → bool follows the dataref convention "non-zero is true".
    /// </summary>
    public static T Coerce<T>(object? raw, T fallback)
    {
        if (raw is null)
        {
            return fallback;
        }

        if (raw is T typed)
        {
            return typed;
        }

        try
        {
            var target = typeof(T);

            if (target == typeof(bool))
            {
                return (T)(object)CoerceToBool(raw, fallback is bool b && b);
            }

            if (target == typeof(string))
            {
                return (T)(object)(Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty);
            }

            return (T)Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return fallback;
        }
    }

    private static bool CoerceToBool(object raw, bool fallback)
    {
        return raw switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => n != 0,
            string => fallback,
            IConvertible c => Math.Abs(c.ToDouble(CultureInfo.InvariantCulture)) > double.Epsilon,
            _ => fallback,
        };
    }
}
