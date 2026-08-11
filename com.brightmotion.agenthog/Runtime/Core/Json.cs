using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Brightmotion.AgentHog.Core
{
    /// <summary>
    /// Minimal JSON writer + parser (MiniJSON-style). Internal on purpose — the SDK's public
    /// surface never exposes JSON. Culture-invariant number formatting: the ingest contract is
    /// JSON, and "1,5" from a tr-TR/de-DE device would corrupt the batch.
    /// </summary>
    internal static class Json
    {
        // ---- writer ----

        public static string Serialize(object value)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, value);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case JsonObj obj:
                    WriteJsonObj(sb, obj);
                    break;
                case IDictionary dict:
                    WriteDict(sb, dict);
                    break;
                case IEnumerable list:
                    WriteArray(sb, list);
                    break;
                default:
                    WriteNumberOrFallback(sb, value);
                    break;
            }
        }

        static void WriteNumberOrFallback(StringBuilder sb, object value)
        {
            switch (value)
            {
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); break;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); break;
                case uint ui: sb.Append(ui.ToString(CultureInfo.InvariantCulture)); break;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); break;
                case float f: WriteDouble(sb, f); break;
                case double d: WriteDouble(sb, d); break;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
                case Enum e: WriteString(sb, e.ToString()); break;
                default:
                    // Unknown object type: stringify rather than throw — analytics must never
                    // crash the game over a weird prop value.
                    WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                    break;
            }
        }

        static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                sb.Append("null");
                return;
            }
            // Whole numbers print without a fractional part ("3", not "3.0") so ints that
            // arrived as floats look the same either way.
            if (d == Math.Floor(d) && Math.Abs(d) < 9.007199254740992e15)
            {
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        static void WriteJsonObj(StringBuilder sb, JsonObj obj)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in obj.Entries)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        static void WriteDict(StringBuilder sb, IDictionary dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                sb.Append(':');
                WriteValue(sb, entry.Value);
            }
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable list)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
        }

        // ---- parser ----
        // Only used to reload the persisted carry-over queue; tolerant of nothing.
        // Returns Dictionary<string, object> / List<object> / string / long / double / bool / null.

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int pos = 0;
            try
            {
                var value = ParseValue(json, ref pos);
                SkipWhitespace(json, ref pos);
                return pos == json.Length ? value : null;
            }
            catch (Exception)
            {
                return null; // corrupted persisted state → treated as absent
            }
        }

        static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("eof");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // {
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':') throw new FormatException("expected :");
                pos++;
                dict[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("eof in object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return dict; }
                throw new FormatException("expected , or }");
            }
        }

        static List<object> ParseArray(string s, ref int pos)
        {
            var list = new List<object>();
            pos++; // [
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("eof in array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return list; }
                throw new FormatException("expected , or ]");
            }
        }

        static string ParseString(string s, ref int pos)
        {
            if (s[pos] != '"') throw new FormatException("expected string");
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (pos >= s.Length) break;
                    char esc = s[pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 > s.Length) throw new FormatException("bad \\u");
                            sb.Append((char)ushort.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            pos += 4;
                            break;
                        default: throw new FormatException("bad escape");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("unterminated string");
        }

        static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && ("+-0123456789.eE".IndexOf(s[pos]) >= 0)) pos++;
            string num = s.Substring(start, pos - start);
            if (num.IndexOf('.') < 0 && num.IndexOf('e') < 0 && num.IndexOf('E') < 0
                && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            throw new FormatException("bad number");
        }

        static void Expect(string s, ref int pos, string literal)
        {
            if (pos + literal.Length > s.Length || s.Substring(pos, literal.Length) != literal)
                throw new FormatException("expected " + literal);
            pos += literal.Length;
        }

        static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r')) pos++;
        }
    }

    /// <summary>
    /// Insertion-ordered JSON object. Dictionary enumeration order is an implementation detail;
    /// batch serialization must be deterministic for the cross-repo golden-fixture test.
    /// </summary>
    internal sealed class JsonObj
    {
        public readonly List<KeyValuePair<string, object>> Entries = new List<KeyValuePair<string, object>>();

        public JsonObj Add(string key, object value)
        {
            Entries.Add(new KeyValuePair<string, object>(key, value));
            return this;
        }
    }
}
