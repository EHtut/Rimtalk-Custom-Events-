using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimTalkCustomEvents.Util
{
    public enum JsonType
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// Thrown when an event file can't be parsed. Message carries line/column so the
    /// player can find the problem.
    /// </summary>
    public class JsonParseException : Exception
    {
        public JsonParseException(string message, int line, int column)
            : base($"line {line}, column {column}: {message}")
        {
            Line = line;
            Column = column;
        }

        public int Line { get; }
        public int Column { get; }
    }

    /// <summary>
    /// A parsed JSON node. Accessors are total — a missing or wrong-typed field returns
    /// the supplied default rather than throwing, so a partially-broken event file still
    /// loads with sensible values instead of failing outright.
    /// </summary>
    public class JsonValue
    {
        public JsonType Type = JsonType.Null;
        public bool Bool;
        public double Number;
        public string Str;
        public List<JsonValue> Array;
        public Dictionary<string, JsonValue> Object;

        public static JsonValue NewObject() =>
            new JsonValue { Type = JsonType.Object, Object = new Dictionary<string, JsonValue>() };

        public static JsonValue NewArray() =>
            new JsonValue { Type = JsonType.Array, Array = new List<JsonValue>() };

        public bool IsNull => Type == JsonType.Null;

        /// <summary>True if this is an object with the given key present and non-null.</summary>
        public bool Has(string key)
        {
            return Type == JsonType.Object
                   && Object.TryGetValue(key, out var v)
                   && v != null
                   && !v.IsNull;
        }

        /// <summary>Returns null when absent, so callers can distinguish "missing" from "empty".</summary>
        public JsonValue Get(string key)
        {
            if (Type != JsonType.Object) return null;
            return Object.TryGetValue(key, out var v) ? v : null;
        }

        public string AsString(string fallback = null)
        {
            switch (Type)
            {
                case JsonType.String: return Str;
                case JsonType.Number: return Number.ToString(CultureInfo.InvariantCulture);
                case JsonType.Bool: return Bool ? "true" : "false";
                default: return fallback;
            }
        }

        public double AsNumber(double fallback = 0)
        {
            if (Type == JsonType.Number) return Number;
            if (Type == JsonType.String && double.TryParse(Str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return d;
            return fallback;
        }

        public float AsFloat(float fallback = 0f) => (float)AsNumber(fallback);

        public int AsInt(int fallback = 0) => (int)Math.Round(AsNumber(fallback));

        public bool AsBool(bool fallback = false)
        {
            if (Type == JsonType.Bool) return Bool;
            if (Type == JsonType.Number) return Number != 0;
            if (Type == JsonType.String && bool.TryParse(Str, out var b)) return b;
            return fallback;
        }

        // ---- keyed convenience accessors ----

        public string GetString(string key, string fallback = null)
        {
            var v = Get(key);
            return v == null || v.IsNull ? fallback : v.AsString(fallback);
        }

        public float GetFloat(string key, float fallback = 0f)
        {
            var v = Get(key);
            return v == null || v.IsNull ? fallback : v.AsFloat(fallback);
        }

        public int GetInt(string key, int fallback = 0)
        {
            var v = Get(key);
            return v == null || v.IsNull ? fallback : v.AsInt(fallback);
        }

        public bool GetBool(string key, bool fallback = false)
        {
            var v = Get(key);
            return v == null || v.IsNull ? fallback : v.AsBool(fallback);
        }

        /// <summary>Always returns a list. Absent or wrong-typed yields an empty one.</summary>
        public List<JsonValue> GetArray(string key)
        {
            var v = Get(key);
            if (v == null || v.Type != JsonType.Array) return new List<JsonValue>();
            return v.Array;
        }

        /// <summary>
        /// Accepts either a real array of strings or a single bare string, which is treated
        /// as a one-element list. Lets an author write "continue": "..." instead of a list.
        /// </summary>
        public List<string> GetStringList(string key)
        {
            var result = new List<string>();
            var v = Get(key);
            if (v == null || v.IsNull) return result;

            if (v.Type == JsonType.String)
            {
                result.Add(v.Str);
                return result;
            }

            if (v.Type == JsonType.Array)
            {
                foreach (var item in v.Array)
                {
                    var s = item?.AsString();
                    if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }

            return result;
        }

        /// <summary>Object key/value pairs as (string, float). Empty when absent.</summary>
        public Dictionary<string, float> GetFloatMap(string key)
        {
            var result = new Dictionary<string, float>();
            var v = Get(key);
            if (v == null || v.Type != JsonType.Object) return result;

            foreach (var pair in v.Object)
            {
                result[pair.Key] = pair.Value.AsFloat();
            }

            return result;
        }
    }

    /// <summary>
    /// Small hand-rolled JSON reader. Deliberately lenient, because event files are prose
    /// written by hand: it allows // and /* */ comments, trailing commas, and raw newlines
    /// inside strings. Errors carry line and column.
    /// </summary>
    public static class Json
    {
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new JsonParseException("empty input", 1, 1);

            var p = new Parser(text);
            p.SkipTrivia();
            var value = p.ParseValue();
            p.SkipTrivia();

            if (!p.AtEnd)
            {
                throw p.Error($"unexpected trailing content '{p.Current}'");
            }

            return value;
        }

        private class Parser
        {
            private readonly string _s;
            private int _i;
            private int _line = 1;
            private int _col = 1;

            public Parser(string s)
            {
                _s = s;
                // Strip a UTF-8 BOM if the file was saved with one.
                if (_s.Length > 0 && _s[0] == '﻿') _i = 1;
            }

            public bool AtEnd => _i >= _s.Length;
            public char Current => _s[_i];

            public JsonParseException Error(string message) => new JsonParseException(message, _line, _col);

            private void Advance()
            {
                if (AtEnd) return;
                if (_s[_i] == '\n')
                {
                    _line++;
                    _col = 1;
                }
                else
                {
                    _col++;
                }

                _i++;
            }

            /// <summary>Skips whitespace and both comment styles.</summary>
            public void SkipTrivia()
            {
                while (!AtEnd)
                {
                    var c = Current;
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    {
                        Advance();
                    }
                    else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                    {
                        while (!AtEnd && Current != '\n') Advance();
                    }
                    else if (c == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                    {
                        Advance();
                        Advance();
                        while (!AtEnd && !(Current == '*' && _i + 1 < _s.Length && _s[_i + 1] == '/'))
                        {
                            Advance();
                        }

                        if (AtEnd) throw Error("unterminated block comment");
                        Advance();
                        Advance();
                    }
                    else
                    {
                        return;
                    }
                }
            }

            public JsonValue ParseValue()
            {
                if (AtEnd) throw Error("unexpected end of input");

                switch (Current)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"':
                    case '\'':
                        return new JsonValue { Type = JsonType.String, Str = ParseString() };
                    default:
                        return ParseLiteralOrNumber();
                }
            }

            private JsonValue ParseObject()
            {
                var result = JsonValue.NewObject();
                Advance(); // consume {
                SkipTrivia();

                if (!AtEnd && Current == '}')
                {
                    Advance();
                    return result;
                }

                while (true)
                {
                    SkipTrivia();
                    if (AtEnd) throw Error("unterminated object — missing '}'");

                    // Tolerate a trailing comma before the closing brace.
                    if (Current == '}')
                    {
                        Advance();
                        return result;
                    }

                    if (Current != '"' && Current != '\'')
                    {
                        throw Error($"expected a quoted key, found '{Current}'");
                    }

                    var key = ParseString();
                    SkipTrivia();

                    if (AtEnd || Current != ':') throw Error($"expected ':' after key \"{key}\"");
                    Advance();

                    SkipTrivia();
                    result.Object[key] = ParseValue();
                    SkipTrivia();

                    if (AtEnd) throw Error("unterminated object — missing '}'");

                    if (Current == ',')
                    {
                        Advance();
                        continue;
                    }

                    if (Current == '}')
                    {
                        Advance();
                        return result;
                    }

                    throw Error($"expected ',' or '}}' after value, found '{Current}'");
                }
            }

            private JsonValue ParseArray()
            {
                var result = JsonValue.NewArray();
                Advance(); // consume [
                SkipTrivia();

                if (!AtEnd && Current == ']')
                {
                    Advance();
                    return result;
                }

                while (true)
                {
                    SkipTrivia();
                    if (AtEnd) throw Error("unterminated array — missing ']'");

                    // Tolerate a trailing comma before the closing bracket.
                    if (Current == ']')
                    {
                        Advance();
                        return result;
                    }

                    result.Array.Add(ParseValue());
                    SkipTrivia();

                    if (AtEnd) throw Error("unterminated array — missing ']'");

                    if (Current == ',')
                    {
                        Advance();
                        continue;
                    }

                    if (Current == ']')
                    {
                        Advance();
                        return result;
                    }

                    throw Error($"expected ',' or ']' after value, found '{Current}'");
                }
            }

            private string ParseString()
            {
                var quote = Current;
                Advance();

                var sb = new StringBuilder();

                while (true)
                {
                    if (AtEnd) throw Error("unterminated string — missing closing quote");

                    var c = Current;

                    if (c == quote)
                    {
                        Advance();
                        return sb.ToString();
                    }

                    if (c == '\\')
                    {
                        Advance();
                        if (AtEnd) throw Error("unterminated escape sequence");
                        var e = Current;
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\'': sb.Append('\''); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (_i + 4 >= _s.Length) throw Error("truncated \\u escape");
                                var hex = _s.Substring(_i + 1, 4);
                                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                    throw Error($"invalid \\u escape '\\u{hex}'");
                                sb.Append((char)code);
                                for (var k = 0; k < 4; k++) Advance();
                                break;
                            default:
                                // Unknown escape: keep it literally rather than failing the file.
                                sb.Append('\\').Append(e);
                                break;
                        }

                        Advance();
                        continue;
                    }

                    // Raw newlines are illegal in strict JSON, but event prose is long and
                    // hand-written, so accept them as literal newlines.
                    sb.Append(c);
                    Advance();
                }
            }

            private JsonValue ParseLiteralOrNumber()
            {
                var start = _i;
                while (!AtEnd)
                {
                    var c = Current;
                    if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '/')
                        break;
                    Advance();
                }

                var token = _s.Substring(start, _i - start);

                if (token == "true") return new JsonValue { Type = JsonType.Bool, Bool = true };
                if (token == "false") return new JsonValue { Type = JsonType.Bool, Bool = false };
                if (token == "null") return new JsonValue { Type = JsonType.Null };

                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return new JsonValue { Type = JsonType.Number, Number = number };
                }

                if (token.Length == 0) throw Error("expected a value");
                throw Error($"'{token}' is not valid here — strings must be in double quotes");
            }
        }
    }
}
