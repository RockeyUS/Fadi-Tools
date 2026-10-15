using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace FileConverter
{
    /// A simple table: header + rows of strings.
    public class Table
    {
        public List<string> Headers = new List<string>();
        public List<List<string>> Rows = new List<List<string>>();

        public int Col(string name)
        {
            int i = Headers.IndexOf(name);
            if (i < 0) { Headers.Add(name); i = Headers.Count - 1; }
            return i;
        }
    }

    /// Reads and writes CSV, TSV, JSON and XML tables.
    public static class DataConv
    {
        public static string ReadText(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            try { return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
            catch { return Encoding.Default.GetString(b); }
        }

        public static Table Read(string path)
        {
            string ext = Formats.Ext(path);
            string text = ReadText(path);
            switch (ext)
            {
                case "tsv": return ReadDelimited(text, '\t');
                case "json": return ReadJson(text);
                case "jsonl": return ReadJsonLines(text);
                case "xml": return ReadXml(text);
                default: return ReadDelimited(text, GuessDelimiter(text));
            }
        }

        public static void Write(Table t, string path, string format)
        {
            string content;
            switch (format)
            {
                case "tsv": content = WriteDelimited(t, '\t'); break;
                case "json": content = WriteJson(t); break;
                case "xml": content = WriteXml(t); break;
                default: content = WriteDelimited(t, ','); break;
            }
            // BOM for CSV/TSV so Excel shows Arabic and other non-English text correctly
            bool bom = format == "csv" || format == "tsv";
            File.WriteAllText(path, content, new UTF8Encoding(bom));
        }

        // ---------------------------------------------------------------- CSV
        static char GuessDelimiter(string text)
        {
            string first = text.Split('\n')[0];
            int c = 0, s = 0, t = 0;
            foreach (char ch in first) { if (ch == ',') c++; else if (ch == ';') s++; else if (ch == '\t') t++; }
            if (t > c && t > s) return '\t';
            if (s > c) return ';';
            return ',';
        }

        public static Table ReadDelimited(string text, char d)
        {
            Table t = new Table();
            List<List<string>> rows = new List<List<string>>();
            List<string> row = new List<string>();
            StringBuilder cell = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(ch);
                }
                else if (ch == '"' && cell.Length == 0) quoted = true;
                else if (ch == d) { row.Add(cell.ToString()); cell.Clear(); }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    rows.Add(row); row = new List<string>();
                }
                else cell.Append(ch);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            rows.RemoveAll(r => r.Count == 1 && r[0].Length == 0);
            if (rows.Count == 0) return t;
            t.Headers = rows[0];
            for (int i = 0; i < t.Headers.Count; i++) if (t.Headers[i].Trim().Length == 0) t.Headers[i] = "column" + (i + 1);
            for (int r = 1; r < rows.Count; r++) t.Rows.Add(rows[r]);
            return t;
        }

        public static string WriteDelimited(Table t, char d)
        {
            StringBuilder sb = new StringBuilder();
            AppendRow(sb, t.Headers, d);
            foreach (List<string> r in t.Rows)
            {
                List<string> full = new List<string>();
                for (int i = 0; i < t.Headers.Count; i++) full.Add(i < r.Count ? r[i] : "");
                AppendRow(sb, full, d);
            }
            return sb.ToString();
        }

        static void AppendRow(StringBuilder sb, List<string> cells, char d)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append(d);
                string v = cells[i] ?? "";
                if (d == '\t') v = v.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
                else if (v.IndexOf(d) >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                    v = "\"" + v.Replace("\"", "\"\"") + "\"";
                sb.Append(v);
            }
            sb.Append("\r\n");
        }

        // ---------------------------------------------------------------- JSON
        public static Table ReadJson(string text)
        {
            object root = new JsonParser(text).Parse();
            List<object> items = null;
            if (root is List<object>) items = (List<object>)root;
            else if (root is JsonObject)
            {
                foreach (KeyValuePair<string, object> kv in (JsonObject)root)
                    if (kv.Value is List<object>) { items = (List<object>)kv.Value; break; }
                if (items == null) items = new List<object> { root };
            }
            else items = new List<object> { root };
            return FromObjects(items);
        }

        static Table ReadJsonLines(string text)
        {
            List<object> items = new List<object>();
            foreach (string line in text.Split('\n'))
                if (line.Trim().Length > 0) items.Add(new JsonParser(line).Parse());
            return FromObjects(items);
        }

        static Table FromObjects(List<object> items)
        {
            Table t = new Table();
            // array of arrays: first row is the header
            if (items.Count > 0 && items[0] is List<object>)
            {
                List<object> head = (List<object>)items[0];
                foreach (object h in head) t.Headers.Add(Scalar(h));
                for (int i = 1; i < items.Count; i++)
                {
                    List<string> r = new List<string>();
                    List<object> arr = items[i] as List<object>;
                    if (arr != null) foreach (object v in arr) r.Add(Scalar(v));
                    t.Rows.Add(r);
                }
                return t;
            }
            foreach (object it in items)
            {
                Dictionary<int, string> row = new Dictionary<int, string>();
                if (it is JsonObject) Flatten((JsonObject)it, "", t, row);
                else row[t.Col("value")] = Scalar(it);
                List<string> r = new List<string>();
                for (int i = 0; i < t.Headers.Count; i++) { string v; r.Add(row.TryGetValue(i, out v) ? v : ""); }
                t.Rows.Add(r);
            }
            return t;
        }

        static void Flatten(JsonObject o, string prefix, Table t, Dictionary<int, string> row)
        {
            foreach (KeyValuePair<string, object> kv in o)
            {
                string key = prefix.Length > 0 ? prefix + "." + kv.Key : kv.Key;
                if (kv.Value is JsonObject) Flatten((JsonObject)kv.Value, key, t, row);
                else row[t.Col(key)] = Scalar(kv.Value);
            }
        }

        static string Scalar(object v)
        {
            if (v == null) return "";
            if (v is string) return (string)v;
            if (v is bool) return (bool)v ? "true" : "false";
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is List<object> || v is JsonObject) { StringBuilder sb = new StringBuilder(); JsonWrite(sb, v, -1); return sb.ToString(); }
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static string WriteJson(Table t)
        {
            StringBuilder sb = new StringBuilder("[\n");
            for (int r = 0; r < t.Rows.Count; r++)
            {
                sb.Append("  {");
                for (int c = 0; c < t.Headers.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append("\n    ");
                    JsonString(sb, t.Headers[c]);
                    sb.Append(": ");
                    string v = c < t.Rows[r].Count ? t.Rows[r][c] : "";
                    JsonValue(sb, v);
                }
                sb.Append("\n  }");
                if (r < t.Rows.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            return sb.Append("]\n").ToString();
        }

        static void JsonValue(StringBuilder sb, string v)
        {
            double d;
            string tv = v.Trim();
            if (tv == "true" || tv == "false" || tv == "null") { sb.Append(tv); return; }
            bool leadingZero = tv.Length > 1 && tv[0] == '0' && tv[1] != '.';
            if (tv.Length > 0 && tv.Length < 17 && !leadingZero && !tv.StartsWith("+") && double.TryParse(tv, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !tv.EndsWith("."))
            { sb.Append(tv); return; }
            JsonString(sb, v);
        }

        static void JsonString(StringBuilder sb, string s)
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

        static void JsonWrite(StringBuilder sb, object v, int indent)
        {
            if (v is JsonObject)
            {
                sb.Append('{'); bool first = true;
                foreach (KeyValuePair<string, object> kv in (JsonObject)v)
                {
                    if (!first) sb.Append(','); first = false;
                    JsonString(sb, kv.Key); sb.Append(':'); JsonWrite(sb, kv.Value, indent);
                }
                sb.Append('}');
            }
            else if (v is List<object>)
            {
                sb.Append('['); bool first = true;
                foreach (object o in (List<object>)v) { if (!first) sb.Append(','); first = false; JsonWrite(sb, o, indent); }
                sb.Append(']');
            }
            else if (v is string) JsonString(sb, (string)v);
            else if (v == null) sb.Append("null");
            else sb.Append(Scalar(v));
        }

        // ---------------------------------------------------------------- XML
        public static Table ReadXml(string text)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(text);
            XmlElement root = doc.DocumentElement;
            // rows = the most common child element name under root
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (XmlNode n in root.ChildNodes) if (n is XmlElement) { int c; counts.TryGetValue(n.Name, out c); counts[n.Name] = c + 1; }
            string rowName = null; int best = 0;
            foreach (KeyValuePair<string, int> kv in counts) if (kv.Value > best) { best = kv.Value; rowName = kv.Key; }
            Table t = new Table();
            List<XmlElement> rows = new List<XmlElement>();
            if (rowName != null) foreach (XmlNode n in root.ChildNodes) if (n is XmlElement && n.Name == rowName) rows.Add((XmlElement)n);
            if (rows.Count == 0) rows.Add(root);
            foreach (XmlElement r in rows)
            {
                Dictionary<int, string> row = new Dictionary<int, string>();
                foreach (XmlAttribute a in r.Attributes) row[t.Col(a.Name)] = a.Value;
                bool hasChildEl = false;
                foreach (XmlNode c in r.ChildNodes)
                {
                    if (c is XmlElement) { hasChildEl = true; row[t.Col(c.Name)] = c.InnerText; }
                }
                if (!hasChildEl && r.InnerText.Trim().Length > 0) row[t.Col("value")] = r.InnerText.Trim();
                List<string> list = new List<string>();
                for (int i = 0; i < t.Headers.Count; i++) { string v; list.Add(row.TryGetValue(i, out v) ? v : ""); }
                t.Rows.Add(list);
            }
            return t;
        }

        public static string WriteXml(Table t)
        {
            StringBuilder sb = new StringBuilder();
            XmlWriterSettings s = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
            using (StringWriter sw = new Utf8StringWriter())
            using (XmlWriter w = XmlWriter.Create(sw, s))
            {
                w.WriteStartDocument();
                w.WriteStartElement("rows");
                List<string> names = new List<string>();
                foreach (string h in t.Headers) names.Add(XmlName(h));
                foreach (List<string> r in t.Rows)
                {
                    w.WriteStartElement("row");
                    for (int c = 0; c < names.Count; c++) w.WriteElementString(names[c], c < r.Count ? r[c] : "");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                w.WriteEndDocument();
                w.Flush();
                return sw.ToString() + "\n";
            }
        }

        class Utf8StringWriter : StringWriter { public override Encoding Encoding { get { return Encoding.UTF8; } } }

        static string XmlName(string h)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in h.Trim()) sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' ? c : '_');
            string n = sb.ToString();
            if (n.Length == 0 || !(char.IsLetter(n[0]) || n[0] == '_')) n = "_" + n;
            if (n.StartsWith("xml", StringComparison.OrdinalIgnoreCase)) n = "_" + n;
            return n;
        }
    }

    /// Ordered JSON object.
    public class JsonObject : List<KeyValuePair<string, object>> { }

    public class JsonParser
    {
        readonly string s; int i;
        public JsonParser(string text) { s = text; }

        public object Parse()
        {
            object v = Value();
            Ws();
            if (i < s.Length) throw new FormatException("Unexpected text after JSON at position " + i);
            return v;
        }

        void Ws() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        object Value()
        {
            Ws();
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{') return Obj();
            if (c == '[') return Arr();
            if (c == '"') return Str();
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("Invalid JSON at position " + i);
            string num = s.Substring(st, i - st);
            double d;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new FormatException("Invalid number " + num);
            long l;
            if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return d;
        }

        JsonObject Obj()
        {
            JsonObject o = new JsonObject();
            i++; Ws();
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                Ws();
                string k = Str();
                Ws();
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at position " + i);
                i++;
                o.Add(new KeyValuePair<string, object>(k, Value()));
                Ws();
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw new FormatException("Expected ',' or '}' at position " + i);
            }
        }

        List<object> Arr()
        {
            List<object> a = new List<object>();
            i++; Ws();
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value());
                Ws();
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw new FormatException("Expected ',' or ']' at position " + i);
            }
        }

        string Str()
        {
            if (s[i] != '"') throw new FormatException("Expected string at position " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            i++;
            return sb.ToString();
        }
    }
}
