using System.Text;

namespace MineOS.Infrastructure.Services.Mods;

/// <summary>
/// Just enough TOML to read a mods.toml: tables, arrays of tables, and string /
/// boolean values. Other values (numbers, arrays, inline tables, multi-line
/// strings) are parsed far enough to be skipped correctly. Malformed input ends the
/// parse quietly with whatever was read so far - this feeds warnings, not decisions
/// that must be exact.
/// </summary>
internal sealed class MiniToml
{
    private readonly string _text;
    private int _pos;
    private readonly Dictionary<string, List<TomlTable>> _arrayTables = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TomlTable> _tables = new(StringComparer.Ordinal);

    public TomlTable Root { get; } = new();

    private MiniToml(string text) => _text = text;

    public static MiniToml Parse(string text)
    {
        var toml = new MiniToml(text);
        try
        {
            toml.ParseDocument();
        }
        catch (FormatException)
        {
            // Keep what was parsed.
        }
        catch (IndexOutOfRangeException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        return toml;
    }

    public IReadOnlyList<TomlTable> ArrayTables(string name) =>
        _arrayTables.TryGetValue(name, out var list) ? list : Array.Empty<TomlTable>();

    private void ParseDocument()
    {
        var current = Root;
        while (true)
        {
            SkipTrivia(newlines: true);
            if (_pos >= _text.Length)
            {
                return;
            }

            if (_text[_pos] == '[')
            {
                var isArray = Peek(1) == '[';
                _pos += isArray ? 2 : 1;
                var name = ReadHeaderName(isArray ? "]]" : "]");
                if (isArray)
                {
                    if (!_arrayTables.TryGetValue(name, out var list))
                    {
                        _arrayTables[name] = list = new List<TomlTable>();
                    }
                    current = new TomlTable();
                    list.Add(current);
                }
                else
                {
                    if (!_tables.TryGetValue(name, out current!))
                    {
                        _tables[name] = current = new TomlTable();
                    }
                }
                continue;
            }

            var key = ReadKey();
            SkipTrivia(newlines: false);
            Expect('=');
            SkipTrivia(newlines: false);
            current.Values[key] = ReadValue();
        }
    }

    private string ReadHeaderName(string terminator)
    {
        var end = _text.IndexOf(terminator, _pos, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException("Unterminated table header");
        }
        var raw = _text[_pos..end];
        _pos = end + terminator.Length;
        // dependencies."my-mod" -> dependencies.my-mod
        var parts = raw.Split('.').Select(p => p.Trim().Trim('"', '\''));
        return string.Join('.', parts);
    }

    private string ReadKey()
    {
        var sb = new StringBuilder();
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '"' || c == '\'')
            {
                sb.Append(ReadSingleLineString(c));
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            {
                sb.Append(c);
                _pos++;
            }
            else
            {
                break;
            }
        }
        if (sb.Length == 0)
        {
            throw new FormatException($"Expected a key at {_pos}");
        }
        return sb.ToString();
    }

    private object? ReadValue()
    {
        var c = _text[_pos];
        switch (c)
        {
            case '"' when Starts("\"\"\""):
                return ReadMultiLine("\"\"\"", escapes: true);
            case '\'' when Starts("'''"):
                return ReadMultiLine("'''", escapes: false);
            case '"':
            case '\'':
                return ReadSingleLineString(c);
            case '[':
                SkipBracketed('[', ']');
                return null;
            case '{':
                SkipBracketed('{', '}');
                return null;
            default:
                var start = _pos;
                while (_pos < _text.Length && _text[_pos] is not ('\n' or '\r' or '#' or ',' or ']' or '}'))
                {
                    _pos++;
                }
                var token = _text[start.._pos].Trim();
                return token switch
                {
                    "true" => true,
                    "false" => false,
                    _ => token
                };
        }
    }

    private string ReadSingleLineString(char quote)
    {
        _pos++; // opening quote
        var sb = new StringBuilder();
        while (_pos < _text.Length && _text[_pos] != quote)
        {
            var c = _text[_pos];
            if (c == '\n')
            {
                throw new FormatException("Newline in string");
            }
            if (c == '\\' && quote == '"' && _pos + 1 < _text.Length)
            {
                sb.Append(Unescape(_text[_pos + 1]));
                _pos += 2;
                continue;
            }
            sb.Append(c);
            _pos++;
        }
        _pos++; // closing quote
        return sb.ToString();
    }

    private string ReadMultiLine(string delimiter, bool escapes)
    {
        _pos += delimiter.Length;
        var sb = new StringBuilder();
        while (_pos < _text.Length && !Starts(delimiter))
        {
            if (escapes && _text[_pos] == '\\' && _pos + 1 < _text.Length)
            {
                sb.Append(Unescape(_text[_pos + 1]));
                _pos += 2;
                continue;
            }
            sb.Append(_text[_pos]);
            _pos++;
        }
        _pos += delimiter.Length;
        return sb.ToString();
    }

    /// <summary>Skips an array or inline table, minding strings and comments inside it.</summary>
    private void SkipBracketed(char open, char close)
    {
        var depth = 0;
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == '"' && Starts("\"\"\"")) { ReadMultiLine("\"\"\"", true); continue; }
            if (c == '\'' && Starts("'''")) { ReadMultiLine("'''", false); continue; }
            if (c is '"' or '\'') { ReadSingleLineString(c); continue; }
            if (c == '#') { SkipComment(); continue; }
            if (c == '[' || c == '{') depth++;
            if (c == ']' || c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    _pos++;
                    return;
                }
            }
            _pos++;
        }
    }

    private void SkipTrivia(bool newlines)
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c is ' ' or '\t' || (newlines && c is '\n' or '\r'))
            {
                _pos++;
            }
            else if (c == '#')
            {
                SkipComment();
            }
            else
            {
                return;
            }
        }
    }

    private void SkipComment()
    {
        while (_pos < _text.Length && _text[_pos] != '\n')
        {
            _pos++;
        }
    }

    private void Expect(char c)
    {
        if (_pos >= _text.Length || _text[_pos] != c)
        {
            throw new FormatException($"Expected '{c}' at {_pos}");
        }
        _pos++;
    }

    private char Peek(int offset) => _pos + offset < _text.Length ? _text[_pos + offset] : '\0';

    private bool Starts(string s) => string.CompareOrdinal(_text, _pos, s, 0, s.Length) == 0;

    private static char Unescape(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        _ => c
    };
}

internal sealed class TomlTable
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    public string? GetString(string key) => Values.TryGetValue(key, out var v) ? v as string : null;

    public bool? GetBool(string key) => Values.TryGetValue(key, out var v) && v is bool b ? b : null;
}
