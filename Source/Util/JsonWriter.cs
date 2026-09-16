using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RimTalkCustomEvents.Util
{
    /// <summary>
    /// Writes indented JSON. Tracks comma placement itself so callers just open
    /// containers and write values.
    ///
    /// Output is meant to be read and hand-edited afterwards, so it is pretty-printed
    /// and callers are expected to omit values that match their default rather than
    /// emitting every field.
    /// </summary>
    public class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();

        /// <summary>One entry per open container; true once it holds something.</summary>
        private readonly Stack<bool> _hasEntries = new Stack<bool>();

        private int _depth;

        public void BeginObject(string key = null)
        {
            OpenContainer(key, '{');
        }

        public void EndObject()
        {
            CloseContainer('}');
        }

        public void BeginArray(string key = null)
        {
            OpenContainer(key, '[');
        }

        public void EndArray()
        {
            CloseContainer(']');
        }

        public void Write(string key, string value)
        {
            if (value == null) return;
            WritePrefix(key);
            _sb.Append(Quote(value));
        }

        public void Write(string key, bool value)
        {
            WritePrefix(key);
            _sb.Append(value ? "true" : "false");
        }

        public void Write(string key, int value)
        {
            WritePrefix(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public void Write(string key, float value)
        {
            WritePrefix(key);
            _sb.Append(Number(value));
        }

        /// <summary>Appends a bare string into the current array.</summary>
        public void WriteValue(string value)
        {
            Write(null, value);
        }

        private void OpenContainer(string key, char open)
        {
            WritePrefix(key);
            _sb.Append(open);
            _hasEntries.Push(false);
            _depth++;
        }

        private void CloseContainer(char close)
        {
            var had = _hasEntries.Count > 0 && _hasEntries.Pop();
            _depth--;

            // An empty container stays on one line: {} rather than a blank block.
            if (had)
            {
                _sb.Append('\n').Append(Indent(_depth));
            }

            _sb.Append(close);
        }

        /// <summary>Emits the separator, newline, indent and key before a value.</summary>
        private void WritePrefix(string key)
        {
            if (_hasEntries.Count > 0)
            {
                if (_hasEntries.Pop()) _sb.Append(',');
                _hasEntries.Push(true);
                _sb.Append('\n').Append(Indent(_depth));
            }

            if (key != null)
            {
                _sb.Append(Quote(key)).Append(": ");
            }
        }

        private static string Indent(int depth) => new string(' ', depth * 2);

        /// <summary>
        /// Trims trailing zeros so 12.0 writes as 12, and never uses exponent notation,
        /// which this parser doesn't need to handle on the way back in.
        /// </summary>
        private static string Number(float value)
        {
            if (value == (int)value && value > -1e9f && value < 1e9f)
            {
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        public static string Quote(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        public override string ToString() => _sb.ToString() + "\n";
    }
}
