using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    /// Turns any file into something a person can read: decoded text, decompressed data,
    /// document text, program information, or the readable text hidden inside binary data.
    public static class Readable
    {
        public const int MaxBytes = 4 << 20;   // read at most 4 MB for text views

        public static Encoding EncodingByIndex(int i)
        {
            switch (i)
            {
                case 1: return new UTF8Encoding(false);
                case 2: return Encoding.Unicode;
                case 3: try { return Encoding.GetEncoding(1256); } catch { return Encoding.Default; }
                case 4: try { return Encoding.GetEncoding(1252); } catch { return Encoding.Default; }
                default: return null;
            }
        }

        static byte[] ReadHead(string path, int max)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] b = new byte[(int)Math.Min(max, fs.Length)];
                int n = 0, r;
                while (n < b.Length && (r = fs.Read(b, n, b.Length - n)) > 0) n += r;
                return b;
            }
        }

        /// Picks the most likely encoding for text bytes (BOM, UTF-8, UTF-16, Arabic or Western ANSI).
        public static Encoding Guess(byte[] b, out string name)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { name = "UTF-8"; return new UTF8Encoding(false); }
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { name = "UTF-16 LE"; return Encoding.Unicode; }
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { name = "UTF-16 BE"; return Encoding.BigEndianUnicode; }
            int sample = Math.Min(b.Length, 65536);
            int oddNul = 0, evenNul = 0;
            for (int i = 0; i < sample; i++) if (b[i] == 0) { if (i % 2 == 1) oddNul++; else evenNul++; }
            if (sample > 8 && oddNul > sample * 0.3 && evenNul < sample * 0.05) { name = "UTF-16 LE"; return Encoding.Unicode; }
            if (sample > 8 && evenNul > sample * 0.3 && oddNul < sample * 0.05) { name = "UTF-16 BE"; return Encoding.BigEndianUnicode; }
            try
            {
                int len = sample;
                while (len > 0 && len > sample - 4 && (b[len - 1] & 0xC0) == 0x80) len--;
                if (len > 0 && b[len - 1] >= 0xC0) len--;
                new UTF8Encoding(false, true).GetString(b, 0, len);
                name = "UTF-8"; return new UTF8Encoding(false);
            }
            catch { }
            // Legacy ANSI: Arabic (1256) if decoding yields mostly Arabic letters, else Western (1252)
            try
            {
                Encoding ar = Encoding.GetEncoding(1256);
                string s = ar.GetString(b, 0, sample);
                int arabic = 0, high = 0;
                foreach (char c in s) { if (c >= 0x0600 && c <= 0x06FF) arabic++; if (c > 127) high++; }
                if (high > 0 && arabic > high * 0.6) { name = "Arabic (Windows-1256)"; return ar; }
                name = "Western (Windows-1252)"; return Encoding.GetEncoding(1252);
            }
            catch { name = "ANSI"; return Encoding.Default; }
        }

        /// readable = friendly view; otherwise the raw characters.
        public static string Build(string path, Sniff s, bool readable, int encIndex, out bool rtl)
        {
            rtl = false;
            byte[] b = ReadHead(path, MaxBytes);
            bool cut = s.Size > b.Length;
            string note = cut ? L.T("text_too_big", Theme.HumanSize(b.Length)) + "\r\n\r\n" : "";

            if (s.IsText || encIndex > 0)
            {
                string encName;
                Encoding enc = EncodingByIndex(encIndex) ?? Guess(b, out encName);
                string t = enc.GetString(b).TrimStart('﻿');
                if (readable && s.Ext == "json" && b.Length < (2 << 20)) try { t = PrettyJson(t); } catch { }
                rtl = MostlyArabic(t);
                return note + Lines(t);
            }

            if (!readable) return note + RawChars(b);

            StringBuilder sb = new StringBuilder();
            try
            {
                if (TryCompressed(b, sb, out rtl)) return note + sb;
                if (s.Ext == "exe" || s.Ext == "dll") { Program(path, b, sb); AppendStrings(path, sb, 400, true); return sb.ToString(); }
                if (TryZip(path, sb, out rtl)) return sb.ToString();
                if (s.Ext == "pdf") { Pdf(b, sb, out rtl); return note + sb; }
                if (s.Ext == "sqlite") { Sqlite(path, sb); return sb.ToString(); }
                if (s.Ext == "dcx" && Dcx(b, sb)) return sb.ToString();
            }
            catch (Exception ex) { sb.AppendLine("(" + ex.Message + ")"); }

            if (s.Entropy > 7.5)
            {
                sb.AppendLine(L.T("rd_encrypted"));
                sb.AppendLine();
            }
            AppendStrings(path, sb, 3000, s.Entropy > 7.5);
            string res = sb.ToString();
            rtl = MostlyArabic(res);
            return res;
        }

        static bool MostlyArabic(string t)
        {
            int ar = 0, lat = 0, n = Math.Min(t.Length, 20000);
            for (int i = 0; i < n; i++) { char c = t[i]; if (c >= 0x0600 && c <= 0x06FF) ar++; else if ((c | 32) >= 'a' && (c | 32) <= 'z') lat++; }
            return ar > lat && ar > 20;
        }

        static string Lines(string t) { return t.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"); }

        static string RawChars(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length + b.Length / 64 * 2);
            for (int i = 0; i < b.Length; i++)
            {
                byte x = b[i];
                sb.Append(x >= 32 && x < 127 ? (char)x : '.');
                if (i % 64 == 63) sb.Append("\r\n");
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- compressed data
        static bool TryCompressed(byte[] b, StringBuilder sb, out bool rtl)
        {
            rtl = false;
            byte[] data = null; string kind = null;
            if (b.Length > 18 && b[0] == 0x1F && b[1] == 0x8B) { data = Inflate(new GZipStream(new MemoryStream(b), CompressionMode.Decompress)); kind = "GZip"; }
            else if (b.Length > 6 && b[0] == 0x78 && (b[1] == 0x01 || b[1] == 0x5E || b[1] == 0x9C || b[1] == 0xDA)) { data = Inflate(new DeflateStream(new MemoryStream(b, 2, b.Length - 2), CompressionMode.Decompress)); kind = "zlib"; }
            if (data == null || data.Length == 0) return false;
            sb.AppendLine(L.T("rd_decompressed", kind, Theme.HumanSize(data.Length)));
            sb.AppendLine(new string('─', 60));
            DescribeBytes(data, sb, out rtl);
            return true;
        }

        static byte[] Inflate(Stream z)
        {
            try
            {
                using (z)
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[65536]; int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0 && ms.Length < MaxBytes) ms.Write(buf, 0, n);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        static void DescribeBytes(byte[] data, StringBuilder sb, out bool rtl)
        {
            rtl = false;
            int nul = 0, ctrl = 0, sample = Math.Min(data.Length, 65536);
            for (int i = 0; i < sample; i++) { byte x = data[i]; if (x == 0) nul++; else if (x < 9 || (x > 13 && x < 32)) ctrl++; }
            string encName;
            if (nul <= sample / 100 && ctrl <= sample / 50)
            {
                string t = Guess(data, out encName).GetString(data).TrimStart('﻿');
                rtl = MostlyArabic(t);
                sb.Append(Lines(t));
            }
            else
            {
                if (data.Length >= 4 && Encoding.ASCII.GetString(data, 0, 4) == "BND4") Bnd4(data, sb);
                foreach (string s in StringsOf(data, 5, 3000)) sb.AppendLine(s);
            }
        }

        // ---------------------------------------------------------------- FromSoftware DCX / BND4
        static bool Dcx(byte[] b, StringBuilder sb)
        {
            // DCX header: compression type at 0x28 ("DFLT" = zlib), compressed data starts at 0x4C
            if (b.Length < 0x50) return false;
            string type = Encoding.ASCII.GetString(b, 0x28, 4);
            sb.AppendLine(L.T("rd_dcx", type));
            if (type != "DFLT") { sb.AppendLine(L.T("rd_dcx_other")); return true; }
            byte[] data = Inflate(new DeflateStream(new MemoryStream(b, 0x4E, b.Length - 0x4E), CompressionMode.Decompress));
            if (data == null) return false;
            sb.AppendLine(L.T("rd_decompressed", "zlib", Theme.HumanSize(data.Length)));
            sb.AppendLine(new string('─', 60));
            bool rtl; DescribeBytes(data, sb, out rtl);
            return true;
        }

        static void Bnd4(byte[] d, StringBuilder sb)
        {
            try
            {
                int count = BitConverter.ToInt32(d, 0x0C);
                bool unicode = d[0x30] == 1;
                sb.AppendLine(L.T("rd_bnd", count));
                // file headers start at 0x40; each header holds a name offset; names may be UTF-16
                int hdrSize = (int)BitConverter.ToInt64(d, 0x20);
                List<string> names = new List<string>();
                for (int i = 0; i < count && i < 5000; i++)
                {
                    int h = 0x40 + i * hdrSize;
                    if (h + hdrSize > d.Length || hdrSize < 0x18) break;
                    int nameOff = BitConverter.ToInt32(d, h + hdrSize - 4);
                    if (nameOff <= 0 || nameOff >= d.Length) continue;
                    int end = nameOff;
                    if (unicode) { while (end + 1 < d.Length && (d[end] != 0 || d[end + 1] != 0)) end += 2; names.Add(Encoding.Unicode.GetString(d, nameOff, end - nameOff)); }
                    else { while (end < d.Length && d[end] != 0) end++; names.Add(Encoding.GetEncoding(932).GetString(d, nameOff, end - nameOff)); }
                }
                foreach (string n in names) sb.AppendLine("  • " + n);
                sb.AppendLine();
            }
            catch { }
        }

        // ---------------------------------------------------------------- programs (EXE / DLL)
        static void Program(string path, byte[] b, StringBuilder sb)
        {
            sb.AppendLine(L.T("rd_program"));
            sb.AppendLine(new string('─', 60));
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
                Row(sb, L.T("rd_desc"), v.FileDescription);
                Row(sb, L.T("rd_product"), v.ProductName);
                Row(sb, L.T("rd_company"), v.CompanyName);
                Row(sb, L.T("rd_version"), v.FileVersion);
                Row(sb, L.T("rd_original"), v.OriginalFilename);
                Row(sb, L.T("rd_copyright"), v.LegalCopyright);
                Row(sb, L.T("rd_comments"), v.Comments);
            }
            catch { }
            int pe = b.Length > 0x40 ? BitConverter.ToInt32(b, 0x3C) : -1;
            if (pe > 0 && pe + 0x78 < b.Length && b[pe] == 'P' && b[pe + 1] == 'E')
            {
                ushort machine = BitConverter.ToUInt16(b, pe + 4);
                string arch = machine == 0x8664 ? "64-bit (x64)" : machine == 0x14C ? "32-bit (x86)" : machine == 0xAA64 ? "ARM64" : "0x" + machine.ToString("X");
                {
                    // .NET "Any CPU" libraries are stamped as x86 but run as 64-bit too
                    int o2 = pe + 24, d2 = o2 + (BitConverter.ToUInt16(b, o2) == 0x20B ? 112 : 96);
                    if (machine == 0x14C && d2 + 8 * 15 <= b.Length && BitConverter.ToUInt32(b, d2 + 8 * 14) != 0) arch = "Any CPU (.NET)";
                }
                Row(sb, L.T("rd_arch"), arch);
                uint ts = BitConverter.ToUInt32(b, pe + 8);
                // reproducible builds store a hash here instead of a date, so only show believable dates
                DateTime built = new DateTime(1970, 1, 1).AddSeconds(ts);
                if (ts > 0x30000000 && built <= DateTime.UtcNow.AddDays(1)) Row(sb, L.T("rd_built"), built.ToString("yyyy-MM-dd"));
                ushort ch = BitConverter.ToUInt16(b, pe + 22);
                Row(sb, L.T("rd_kind"), (ch & 0x2000) != 0 ? L.T("rd_library") : L.T("rd_app"));
                int opt = pe + 24;
                bool pe32plus = BitConverter.ToUInt16(b, opt) == 0x20B;
                int dirs = opt + (pe32plus ? 112 : 96);
                int sections = BitConverter.ToUInt16(b, pe + 6);
                int secTable = opt + BitConverter.ToUInt16(b, pe + 20);
                if (dirs + 8 * 15 <= b.Length)
                {
                    uint clr = BitConverter.ToUInt32(b, dirs + 8 * 14);
                    Row(sb, L.T("rd_dotnet"), clr != 0 ? L.T("rd_yes") : L.T("rd_no"));
                    if (clr != 0) try { Row(sb, L.T("rd_assembly"), System.Reflection.AssemblyName.GetAssemblyName(path).FullName); } catch { }
                    List<string> exports = Exports(b, BitConverter.ToUInt32(b, dirs), sections, secTable);
                    List<string> imports = Imports(b, BitConverter.ToUInt32(b, dirs + 8), sections, secTable, pe32plus);
                    if (imports.Count > 0) { sb.AppendLine(); sb.AppendLine(L.T("rd_imports", imports.Count)); foreach (string i in imports) sb.AppendLine("  • " + i); }
                    if (exports.Count > 0) { sb.AppendLine(); sb.AppendLine(L.T("rd_exports", exports.Count)); for (int i = 0; i < exports.Count && i < 500; i++) sb.AppendLine("  • " + exports[i]); }
                }
            }
            sb.AppendLine();
        }

        static void Row(StringBuilder sb, string k, string v) { if (!string.IsNullOrEmpty(v) && v.Trim().Length > 0) sb.AppendLine(k.PadRight(22) + v.Trim()); }

        static int RvaToOffset(byte[] b, uint rva, int sections, int table)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = table + i * 40;
                if (s + 40 > b.Length) break;
                uint va = BitConverter.ToUInt32(b, s + 12), size = Math.Max(BitConverter.ToUInt32(b, s + 8), BitConverter.ToUInt32(b, s + 16));
                uint raw = BitConverter.ToUInt32(b, s + 20);
                if (rva >= va && rva < va + size) return (int)(rva - va + raw);
            }
            return -1;
        }

        static string CStr(byte[] b, int off)
        {
            if (off < 0 || off >= b.Length) return "";
            int e = off; while (e < b.Length && b[e] != 0 && e - off < 512) e++;
            return Encoding.ASCII.GetString(b, off, e - off);
        }

        static List<string> Exports(byte[] b, uint rva, int sections, int table)
        {
            List<string> list = new List<string>();
            if (rva == 0) return list;
            int d = RvaToOffset(b, rva, sections, table);
            if (d < 0 || d + 40 > b.Length) return list;
            int n = BitConverter.ToInt32(b, d + 24);
            int names = RvaToOffset(b, BitConverter.ToUInt32(b, d + 32), sections, table);
            for (int i = 0; i < n && i < 5000 && names >= 0 && names + i * 4 + 4 <= b.Length; i++)
                list.Add(CStr(b, RvaToOffset(b, BitConverter.ToUInt32(b, names + i * 4), sections, table)));
            return list;
        }

        static List<string> Imports(byte[] b, uint rva, int sections, int table, bool plus)
        {
            List<string> list = new List<string>();
            if (rva == 0) return list;
            int d = RvaToOffset(b, rva, sections, table);
            for (int i = 0; d >= 0 && d + i * 20 + 20 <= b.Length && i < 500; i++)
            {
                uint nameRva = BitConverter.ToUInt32(b, d + i * 20 + 12);
                if (nameRva == 0) break;
                string n = CStr(b, RvaToOffset(b, nameRva, sections, table));
                if (n.Length > 0) list.Add(n);
            }
            return list;
        }

        // ---------------------------------------------------------------- ZIP based (docx, xlsx, pptx, odt, epub, zip)
        static bool TryZip(string path, StringBuilder sb, out bool rtl)
        {
            rtl = false;
            ZipArchive za;
            try { za = ZipFile.OpenRead(path); } catch { return false; }
            using (za)
            {
                Dictionary<string, ZipArchiveEntry> map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry e in za.Entries) map[e.FullName] = e;
                StringBuilder text = new StringBuilder();
                ZipArchiveEntry en;
                if (map.TryGetValue("word/document.xml", out en)) WordText(Xml(en), text);
                else if (map.TryGetValue("xl/sharedStrings.xml", out en))
                {
                    foreach (Match m in Regex.Matches(Xml(en), "<t[^>]*>(.*?)</t>", RegexOptions.Singleline)) text.AppendLine(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
                }
                else if (map.ContainsKey("ppt/presentation.xml"))
                {
                    List<string> slides = new List<string>();
                    foreach (string k in map.Keys) if (Regex.IsMatch(k, @"^ppt/slides/slide\d+\.xml$")) slides.Add(k);
                    slides.Sort((a, c) => int.Parse(Regex.Match(a, @"\d+").Value).CompareTo(int.Parse(Regex.Match(c, @"\d+").Value)));
                    int idx = 1;
                    foreach (string k in slides)
                    {
                        text.AppendLine("── " + L.T("rd_slide", idx++) + " ──");
                        foreach (Match m in Regex.Matches(Xml(map[k]), "<a:t>(.*?)</a:t>", RegexOptions.Singleline)) text.AppendLine(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value));
                        text.AppendLine();
                    }
                }
                else if (map.TryGetValue("content.xml", out en))
                {
                    string x = Xml(en);
                    x = Regex.Replace(x, "</text:(p|h)>", "\n");
                    x = Regex.Replace(x, "<text:tab/>", "\t");
                    text.Append(System.Net.WebUtility.HtmlDecode(Regex.Replace(x, "<[^>]+>", "")));
                }
                else if (map.ContainsKey("META-INF/container.xml"))
                {
                    foreach (ZipArchiveEntry e in za.Entries)
                        if (Regex.IsMatch(e.FullName, @"\.(x?html?)$", RegexOptions.IgnoreCase) && text.Length < MaxBytes)
                            text.AppendLine(Markdown.HtmlToText(Xml(e)));
                }
                if (text.Length > 0)
                {
                    sb.AppendLine(L.T("rd_doc_text"));
                    sb.AppendLine(new string('─', 60));
                    string t = Lines(text.ToString().Trim());
                    rtl = MostlyArabic(t);
                    sb.Append(t);
                    return true;
                }
                sb.AppendLine(L.T("rd_zip", za.Entries.Count));
                sb.AppendLine(new string('─', 60));
                int shown = 0;
                foreach (ZipArchiveEntry e in za.Entries)
                {
                    if (shown++ > 3000) break;
                    sb.AppendLine((e.Length > 0 ? Theme.HumanSize(e.Length) : "").PadLeft(10) + "   " + e.FullName);
                }
                return true;
            }
        }

        static string Xml(ZipArchiveEntry e)
        {
            using (StreamReader r = new StreamReader(e.Open(), Encoding.UTF8))
            {
                char[] buf = new char[MaxBytes];
                int n = r.ReadBlock(buf, 0, buf.Length);
                return new string(buf, 0, n);
            }
        }

        static void WordText(string xml, StringBuilder sb)
        {
            string x = Regex.Replace(xml, "</w:p>", "\n");
            x = Regex.Replace(x, "<w:tab/>", "\t");
            x = Regex.Replace(x, "<w:br/>", "\n");
            foreach (Match m in Regex.Matches(x, "<w:t[^>]*>(.*?)</w:t>|(\n)|(\t)", RegexOptions.Singleline))
                sb.Append(m.Groups[1].Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : m.Value);
        }

        // ---------------------------------------------------------------- PDF (basic text extraction)
        static void Pdf(byte[] b, StringBuilder sb, out bool rtl)
        {
            rtl = false;
            StringBuilder text = new StringBuilder();
            string raw = Encoding.GetEncoding(28591).GetString(b);
            foreach (Match m in Regex.Matches(raw, @"stream\r?\n"))
            {
                int start = m.Index + m.Length;
                int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
                if (end < 0) break;
                byte[] chunk = new byte[end - start];
                Array.Copy(b, start, chunk, 0, chunk.Length);
                byte[] data = chunk.Length > 2 && chunk[0] == 0x78 ? Inflate(new DeflateStream(new MemoryStream(chunk, 2, chunk.Length - 2), CompressionMode.Decompress)) : chunk;
                if (data == null) continue;
                string s = Encoding.GetEncoding(28591).GetString(data);
                if (!s.Contains("Tj") && !s.Contains("TJ")) continue;
                foreach (Match t in Regex.Matches(s, @"\((?:\\.|[^\\)])*\)\s*Tj|\[(.*?)\]\s*TJ|(T\*|Td|TD|')"))
                {
                    if (t.Groups[2].Success) { text.Append(' '); continue; }
                    string part = t.Groups[1].Success ? Joined(Regex.Matches(t.Groups[1].Value, @"\((?:\\.|[^\\)])*\)")) : t.Value.Substring(0, t.Value.LastIndexOf(')') + 1);
                    text.Append(Regex.Replace(Regex.Replace(part, @"^\(|\)$", ""), @"\)\s*-?\d*\.?\d*\s*\(", "").Replace("\\(", "(").Replace("\\)", ")").Replace("\\n", "\n"));
                    if (t.Value.EndsWith("Tj")) text.Append(' ');
                }
                text.Append('\n');
                if (text.Length > MaxBytes) break;
            }
            string res = Regex.Replace(text.ToString(), @"[ \t]{2,}", " ").Trim();
            if (res.Length > 20)
            {
                sb.AppendLine(L.T("rd_pdf"));
                sb.AppendLine(new string('─', 60));
                sb.Append(Lines(res));
            }
            else sb.AppendLine(L.T("rd_pdf_none"));
        }

        static string Joined(MatchCollection mc) { StringBuilder sb = new StringBuilder(); foreach (Match m in mc) sb.Append(m.Value); return sb.ToString(); }

        // ---------------------------------------------------------------- SQLite
        static void Sqlite(string path, StringBuilder sb)
        {
            sb.AppendLine(L.T("rd_sqlite"));
            sb.AppendLine(new string('─', 60));
            foreach (KeyValuePair<long, string> kv in Sniffer.Strings(path, 8, 20000))
                if (kv.Value.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) || kv.Value.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine(kv.Value);
            sb.AppendLine();
            AppendStrings(path, sb, 1500);
        }

        // ---------------------------------------------------------------- strings
        static void AppendStrings(string path, StringBuilder sb, int max, bool strict = false)
        {
            List<KeyValuePair<long, string>> list = Sniffer.Strings(path, 5, max * 3);
            List<string> good = new List<string>();
            string last = null;
            foreach (KeyValuePair<long, string> kv in list)
            {
                string v = kv.Value.Replace("  (UTF-16)", "");
                if (!LooksLikeWords(v) || v == last) continue;
                if (strict && (v.Length < 8 || (v.Length < 10 && v.IndexOfAny(new[] { ' ', '_', '.', '/', '\\' }) < 0))) continue;
                good.Add(v); last = v;
                if (good.Count >= max) break;
            }
            if (good.Count == 0) { sb.AppendLine(L.T("rd_no_text")); return; }
            sb.AppendLine(L.T("rd_found", good.Count));
            sb.AppendLine(new string('─', 60));
            foreach (string g in good) sb.AppendLine(g);
        }

        public static List<string> StringsOf(byte[] d, int minLen, int max)
        {
            List<string> res = new List<string>();
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < d.Length && res.Count < max; i++)
            {
                byte x = d[i];
                if (x >= 32 && x < 127) cur.Append((char)x);
                else { if (cur.Length >= minLen && LooksLikeWords(cur.ToString())) res.Add(cur.ToString()); cur.Clear(); }
            }
            // UTF-16 pass
            cur.Clear();
            for (int i = 0; i + 1 < d.Length && res.Count < max; i += 2)
            {
                char c = (char)(d[i] | (d[i + 1] << 8));
                if ((c >= 32 && c < 127) || (c >= 0x0600 && c <= 0x06FF) || (c >= 0x3040 && c <= 0x9FFF)) cur.Append(c);
                else { if (cur.Length >= minLen && LooksLikeWords(cur.ToString())) res.Add(cur.ToString()); cur.Clear(); }
            }
            return res;
        }

        /// Filters out random byte noise: keeps strings that are mostly letters/digits/spaces.
        public static bool IsWords(string s) { return LooksLikeWords(s); }

        static bool LooksLikeWords(string s)
        {
            s = s.Trim();
            if (s.Length < 5) return false;
            int good = 0, letters = 0, vowels = 0, caseFlips = 0, runSame = 1, maxRun = 1, seps = 0, nonLatin = 0;
            bool prevUpper = false, prevLetter = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetterOrDigit(c) || " ._-/\\:,()'".IndexOf(c) >= 0) good++;
                if (c == ' ' || c == '_' || c == '.' || c == '/' || c == '\\' || c == '-') seps++;
                if (char.IsLetter(c))
                {
                    letters++;
                    if (c > 127) nonLatin++;
                    if ("aeiouyAEIOUY".IndexOf(c) >= 0) vowels++;
                    bool up = char.IsUpper(c);
                    if (prevLetter && up != prevUpper && up) caseFlips++;
                    prevUpper = up; prevLetter = true;
                }
                else prevLetter = false;
                if (i > 0 && c == s[i - 1]) { runSame++; maxRun = Math.Max(maxRun, runSame); } else runSame = 1;
            }
            if (good < s.Length * 0.9 || letters < s.Length * 0.5 || maxRun >= 4) return false;
            if (nonLatin > letters / 2) return true;                       // Arabic and other scripts
            if (vowels < letters * 0.22) return false;                       // random consonant soup
            if (caseFlips > Math.Max(1, letters / 5)) return false;          // rAnDoM cAsE noise
            if (seps == 0 && s.Length < 6) return false;
            return true;
        }

        public static string PrettyJson(string s)
        {
            StringBuilder sb = new StringBuilder(); int ind = 0; bool q = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) q = !q;
                if (q) { sb.Append(c); continue; }
                if (char.IsWhiteSpace(c)) continue;
                if (c == '{' || c == '[') { sb.Append(c).Append('\n').Append(' ', (++ind) * 2); }
                else if (c == '}' || c == ']') { sb.Append('\n').Append(' ', Math.Max(0, --ind) * 2).Append(c); }
                else if (c == ',') { sb.Append(c).Append('\n').Append(' ', ind * 2); }
                else if (c == ':') sb.Append(": ");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
