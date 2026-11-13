using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Facet3D
{
    /// <summary>Minimal JSON reader/writer. Objects = Dictionary&lt;string,object&gt;, arrays = List&lt;object&gt;, numbers = double.</summary>
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            if (s.Length > 0 && s[0] == '﻿') i = 1;
            return ParseValue(s, ref i);
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    string key = ParseString(s, ref i);
                    Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("Expected ':' in JSON");
                    i++;
                    d[key] = ParseValue(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' in JSON");
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ParseValue(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("Expected ',' or ']' in JSON");
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { i += 4; return true; }
            if (c == 'f') { i += 5; return false; }
            if (c == 'n') { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st) throw new FormatException("Bad JSON value at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string in JSON");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        // ---- access helpers ----
        public static Dictionary<string, object> Obj(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        public static List<object> Arr(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as List<object>;
            return null;
        }

        public static double Num(object o, string key, double def)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is double) return (double)v;
            return def;
        }

        public static int Int(object o, string key, int def) { return (int)Num(o, key, def); }

        public static bool Has(object o, string key)
        {
            var d = o as Dictionary<string, object>;
            return d != null && d.ContainsKey(key) && d[key] != null;
        }

        public static string Str(object o, string key, string def)
        {
            var d = o as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue(key, out v) && v is string) return (string)v;
            return def;
        }

        public static float[] Floats(object o, string key)
        {
            var a = Arr(o, key);
            if (a == null) return null;
            var r = new float[a.Count];
            for (int i = 0; i < a.Count; i++) r[i] = a[i] is double ? (float)(double)a[i] : 0f;
            return r;
        }

        // ---- writer ----
        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteValue(sb, v);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            var str = v as string;
            if (str != null) { WriteString(sb, str); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is uint || v is short || v is byte)
            {
                sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (v is float || v is double)
            {
                double d = Convert.ToDouble(v);
                if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
                if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as Dictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var en = v as IEnumerable;
            if (en != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var x in en)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, x);
                }
                sb.Append(']');
                return;
            }
            WriteString(sb, v.ToString());
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
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
