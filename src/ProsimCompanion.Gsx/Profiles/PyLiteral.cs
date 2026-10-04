using System.Globalization;
using System.Text;

namespace ProsimCompanion.Gsx.Profiles;

/// <summary>
/// A tolerant reader for the Python literals GSX writes into its profiles: the
/// <c>pushbackaddpos = [{'snap': False, 'pos': (lat, lon, hdg), 'label': u'Facing South'}, …]</c>
/// ini value and the <c>parkings = { GATE_W : { 40 : (Apron1W, stop), '34B' : (…) } }</c> dict
/// of a <c>.py</c> customisation. Not a Python interpreter: identifiers and calls become
/// <see cref="PyIdentifier"/> / <see cref="PyCall"/> nodes for the caller to interpret, and any
/// syntax it does not understand ends the literal rather than throwing.
/// </summary>
public static class PyLiteral
{
    /// <summary>Parses one literal from the start of <paramref name="text"/>; null on nothing
    /// parseable. Dicts become <see cref="Dictionary{TKey,TValue}"/> (string keys — non-string
    /// keys are rendered with <c>ToString</c>), lists/tuples <see cref="List{T}"/>, strings,
    /// doubles, bools, <see cref="PyNone"/>, identifiers and calls.</summary>
    public static object? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(text);
        return reader.ReadValue();
    }

    /// <summary>Parses, requiring the whole trimmed text to be consumed; null otherwise.</summary>
    public static object? ParseWhole(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(text);
        var value = reader.ReadValue();
        reader.SkipWs();
        return reader.AtEnd ? value : null;
    }

    public sealed record PyNone
    {
        public static readonly PyNone Instance = new();
    }

    public sealed record PyIdentifier(string Name);

    /// <summary>A call such as <c>CustomizedName("Terminal 1|Gate #§", 1)</c>.</summary>
    public sealed record PyCall(string Name, IReadOnlyList<object?> Args);

    private sealed class Reader(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public void SkipWs()
        {
            while (!AtEnd)
            {
                var c = text[_pos];
                if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '#')
                {
                    while (!AtEnd && text[_pos] != '\n')
                    {
                        _pos++;
                    }
                }
                else if (c == '\\' && _pos + 1 < text.Length && (text[_pos + 1] == '\n' || text[_pos + 1] == '\r'))
                {
                    _pos += 2;
                }
                else
                {
                    break;
                }
            }
        }

        public object? ReadValue()
        {
            SkipWs();
            if (AtEnd)
            {
                return null;
            }

            var c = text[_pos];
            switch (c)
            {
                case '{':
                    return ReadDict();
                case '[':
                    return ReadSequence(']');
                case '(':
                    return ReadSequence(')');
                case '"':
                case '\'':
                    return ReadString();
            }

            if ((c == 'u' || c == 'r' || c == 'b') && _pos + 1 < text.Length && (text[_pos + 1] == '"' || text[_pos + 1] == '\''))
            {
                _pos++;
                return ReadString();
            }

            if (char.IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                return ReadNumber();
            }

            if (char.IsLetter(c) || c == '_')
            {
                var name = ReadIdentifier();
                switch (name)
                {
                    case "None":
                        return PyNone.Instance;
                    case "True":
                        return true;
                    case "False":
                        return false;
                }

                SkipWs();
                if (!AtEnd && text[_pos] == '(')
                {
                    var args = ReadSequence(')') ?? [];
                    return new PyCall(name, args);
                }

                return new PyIdentifier(name);
            }

            return null;
        }

        private Dictionary<string, object?>? ReadDict()
        {
            _pos++; // {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipWs();
                if (AtEnd)
                {
                    return dict;
                }

                if (text[_pos] == '}')
                {
                    _pos++;
                    return dict;
                }

                var key = ReadValue();
                SkipWs();
                if (AtEnd || text[_pos] != ':')
                {
                    return dict;
                }

                _pos++;
                var value = ReadValue();
                dict[KeyText(key)] = value;
                SkipWs();
                if (!AtEnd && text[_pos] == ',')
                {
                    _pos++;
                }
            }
        }

        private List<object?>? ReadSequence(char close)
        {
            _pos++; // [ or (
            var list = new List<object?>();
            while (true)
            {
                SkipWs();
                if (AtEnd)
                {
                    return list;
                }

                if (text[_pos] == close)
                {
                    _pos++;
                    return list;
                }

                var before = _pos;
                var value = ReadValue();
                if (_pos == before)
                {
                    // Unparseable token: stop here, keep what we have.
                    return list;
                }

                list.Add(value);
                SkipWs();
                if (!AtEnd && text[_pos] == ',')
                {
                    _pos++;
                }
            }
        }

        private string ReadString()
        {
            var quote = text[_pos];
            var triple = _pos + 2 < text.Length && text[_pos + 1] == quote && text[_pos + 2] == quote;
            _pos += triple ? 3 : 1;
            var builder = new StringBuilder();
            while (!AtEnd)
            {
                var c = text[_pos];
                if (c == '\\' && _pos + 1 < text.Length)
                {
                    var next = text[_pos + 1];
                    builder.Append(next switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        _ => next,
                    });
                    _pos += 2;
                    continue;
                }

                if (c == quote)
                {
                    if (!triple)
                    {
                        _pos++;
                        return builder.ToString();
                    }

                    if (_pos + 2 < text.Length && text[_pos + 1] == quote && text[_pos + 2] == quote)
                    {
                        _pos += 3;
                        return builder.ToString();
                    }
                }

                builder.Append(c);
                _pos++;
            }

            return builder.ToString();
        }

        private object? ReadNumber()
        {
            var start = _pos;
            while (!AtEnd && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '.' or '-' or '+'))
            {
                _pos++;
            }

            var token = text[start.._pos];
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return (double)l;
            }

            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        private string ReadIdentifier()
        {
            var start = _pos;
            while (!AtEnd && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_' || text[_pos] == '.'))
            {
                _pos++;
            }

            return text[start.._pos];
        }

        private static string KeyText(object? key) => key switch
        {
            string s => s,
            double d => d.ToString(CultureInfo.InvariantCulture),
            PyNone => "None",
            PyIdentifier id => id.Name,
            null => "",
            _ => key.ToString() ?? "",
        };
    }
}
