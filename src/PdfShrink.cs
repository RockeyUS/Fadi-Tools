using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    /// Makes PDFs smaller without touching page size or picture resolution:
    /// photos inside are re-encoded more efficiently, and leftovers from earlier edits are dropped.
    public static class PdfShrink
    {
        class Obj
        {
            public int Num, Gen; public int Start, End;
            public int DictStart = -1, DictEnd = -1;
            public int DataStart = -1, DataLen = -1;
            public byte[] NewData; public string NewDict;
            public bool Drop;
        }

        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        static bool Ws(char c) { return c == ' ' || c == '\r' || c == '\n' || c == '\t' || c == '\f' || c == '\0'; }

        public static void Shrink(string input, string output, int quality)
        {
            byte[] b = File.ReadAllBytes(input);
            string s = Latin1.GetString(b);
            if (!s.StartsWith("%PDF")) throw new UserError(L.T("cmp_pdf_bad"));
            Regex hdr = new Regex(@"(\d+)\s+(\d+)\s+obj\b");

            // every object, in file order (a later copy of the same number replaces an earlier one)
            Dictionary<int, Obj> objs = new Dictionary<int, Obj>();
            List<Obj> all = new List<Obj>();
            int pos = 0;
            while (pos < s.Length)
            {
                Match m = hdr.Match(s, pos);
                if (!m.Success) break;
                if (m.Index > 0 && !Ws(s[m.Index - 1]) && s[m.Index - 1] != '>' && s[m.Index - 1] != ']' && s[m.Index - 1] != ')') { pos = m.Index + 1; continue; }
                Obj o = new Obj { Num = int.Parse(m.Groups[1].Value), Gen = int.Parse(m.Groups[2].Value), Start = m.Index };
                int i = m.Index + m.Length;
                while (i < s.Length && Ws(s[i])) i++;
                if (i + 1 < s.Length && s[i] == '<' && s[i + 1] == '<')
                {
                    int de = DictEnd(s, i);
                    if (de < 0) break;
                    o.DictStart = i; o.DictEnd = de;
                    int j = de;
                    while (j < s.Length && Ws(s[j])) j++;
                    if (string.CompareOrdinal(s, j, "stream", 0, 6) == 0)
                    {
                        j += 6;
                        if (j < s.Length && s[j] == '\r') j++;
                        if (j < s.Length && s[j] == '\n') j++;
                        o.DataStart = j;
                        int len = Length(s, s.Substring(i, de - i));
                        int k = len >= 0 ? j + len : -1;
                        bool ok = false;
                        if (k >= 0 && k <= s.Length)
                        {
                            int q = k; while (q < s.Length && Ws(s[q])) q++;
                            ok = string.CompareOrdinal(s, q, "endstream", 0, 9) == 0;
                        }
                        if (ok) o.DataLen = len;
                        else
                        {
                            int es = s.IndexOf("endstream", j, StringComparison.Ordinal);
                            if (es < 0) break;
                            int q = es;
                            if (q > j && s[q - 1] == '\n') q--;
                            if (q > j && s[q - 1] == '\r') q--;
                            o.DataLen = q - j;
                        }
                        i = s.IndexOf("endstream", o.DataStart + o.DataLen, StringComparison.Ordinal) + 9;
                    }
                    else i = de;
                }
                int eo = s.IndexOf("endobj", i, StringComparison.Ordinal);
                if (eo < 0) break;
                o.End = eo + 6;
                all.Add(o);
                objs[o.Num] = o;
                pos = o.End;
            }
            if (all.Count == 0) throw new UserError(L.T("cmp_pdf_bad"));

            // trailer: each value from the latest classic trailer or cross-reference stream that has it
            List<KeyValuePair<int, string>> trailers = new List<KeyValuePair<int, string>>();
            foreach (Match m in Regex.Matches(s, @"trailer\s*<<"))
            {
                int ds = s.IndexOf("<<", m.Index, StringComparison.Ordinal);
                int de = DictEnd(s, ds);
                if (de > 0) trailers.Add(new KeyValuePair<int, string>(m.Index, s.Substring(ds, de - ds)));
            }
            foreach (Obj o in all)
            {
                if (o.DictStart < 0) continue;
                string d = s.Substring(o.DictStart, o.DictEnd - o.DictStart);
                if (Regex.IsMatch(d, @"/Type\s*/XRef\b")) { o.Drop = true; trailers.Add(new KeyValuePair<int, string>(o.Start, d)); }
                else if (d.Contains("/Linearized")) o.Drop = true;   // its numbers would be wrong after rewriting
            }
            trailers.Sort((x, y) => x.Key.CompareTo(y.Key));
            string root = null, info = null, id = null;
            foreach (KeyValuePair<int, string> t in trailers)
            {
                if (t.Value.Contains("/Encrypt")) throw new UserError(L.T("cmp_pdf_locked"));
                root = Val(t.Value, "Root") ?? root; info = Val(t.Value, "Info") ?? info; id = Arr(t.Value, "ID") ?? id;
            }
            if (root == null) throw new UserError(L.T("cmp_pdf_bad"));

            // objects packed inside object streams
            Dictionary<int, int[]> packed = new Dictionary<int, int[]>();   // num -> {stream num, index}
            Dictionary<int, int> packedAt = new Dictionary<int, int>();
            foreach (Obj o in all)
            {
                if (o.DictStart < 0 || o.DataLen <= 0 || objs[o.Num] != o) continue;
                string d = s.Substring(o.DictStart, o.DictEnd - o.DictStart);
                if (!Regex.IsMatch(d, @"/Type\s*/ObjStm\b")) continue;
                byte[] data = Inflate(b, o.DataStart, o.DataLen, d);
                if (data == null) throw new UserError(L.T("cmp_pdf_complex"));
                int n = IntVal(d, "N"), first = IntVal(d, "First");
                string head = Latin1.GetString(data, 0, Math.Max(0, Math.Min(first, data.Length)));
                string[] nums = head.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < n && k * 2 < nums.Length; k++)
                {
                    int num;
                    if (!int.TryParse(nums[k * 2], out num)) continue;
                    // whichever copy comes later in the file is the current one
                    if (objs.ContainsKey(num) && objs[num].Start > o.Start) continue;
                    int at;
                    if (packedAt.TryGetValue(num, out at) && at > o.Start) continue;
                    packed[num] = new[] { o.Num, k }; packedAt[num] = o.Start;
                }
            }
            foreach (KeyValuePair<int, int> kv in packedAt)
                if (objs.ContainsKey(kv.Key) && objs[kv.Key].Start < kv.Value) objs[kv.Key].Drop = true;

            // re-encode photos
            foreach (Obj o in objs.Values)
            {
                if (o.Drop || o.DictStart < 0 || o.DataLen < 30000) continue;
                string d = s.Substring(o.DictStart, o.DictEnd - o.DictStart);
                if (!Regex.IsMatch(d, @"/Subtype\s*/Image\b")) continue;
                Match f = Regex.Match(d, @"/Filter\s*(\[\s*)?/DCTDecode\s*(\])?");
                if (!f.Success || d.Contains("/DecodeParms") || d.Contains("/Decode ") || d.Contains("/Decode[") || d.Contains("/DeviceCMYK") || d.Contains("/DeviceGray") || d.Contains("/Indexed")) continue;
                try
                {
                    using (MemoryStream ms = new MemoryStream(b, o.DataStart, o.DataLen))
                    using (Image im = Image.FromStream(ms))
                    {
                        if (im.PixelFormat != PixelFormat.Format24bppRgb && im.PixelFormat != PixelFormat.Format32bppRgb) continue;
                        using (MemoryStream outp = new MemoryStream())
                        {
                            ImageCodecInfo jpg = null;
                            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
                            using (EncoderParameters ep = new EncoderParameters(1))
                            {
                                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                                im.Save(outp, jpg, ep);
                            }
                            if (outp.Length < o.DataLen * 0.85)
                            {
                                o.NewData = outp.ToArray();
                                o.NewDict = Regex.Replace(d, @"/Length\s+\d+(\s+\d+\s+R)?", "/Length " + o.NewData.Length);
                            }
                        }
                    }
                }
                catch { }
            }

            // write the new file
            List<Obj> keep = new List<Obj>();
            foreach (Obj o in objs.Values) if (!o.Drop) keep.Add(o);
            keep.Sort((x, y) => x.Start.CompareTo(y.Start));
            int max = 0;
            foreach (Obj o in keep) max = Math.Max(max, o.Num);
            foreach (int k in packed.Keys) max = Math.Max(max, k);
            int xnum = max + 1;
            Dictionary<int, long> off = new Dictionary<int, long>();
            using (FileStream fs = new FileStream(output, FileMode.Create, FileAccess.Write))
            {
                Action<string> w = t => { byte[] bb = Latin1.GetBytes(t); fs.Write(bb, 0, bb.Length); };
                Match ver = Regex.Match(s, @"^%PDF-(\d)\.(\d)");
                int vmaj = ver.Success ? int.Parse(ver.Groups[1].Value) : 1, vmin = ver.Success ? int.Parse(ver.Groups[2].Value) : 4;
                if (vmaj == 1 && vmin < 5) vmin = 5;   // cross-reference streams need PDF 1.5
                w("%PDF-" + vmaj + "." + vmin + "\n%âãÏÓ\n");
                foreach (Obj o in keep)
                {
                    off[o.Num] = fs.Position;
                    if (o.NewData == null) { fs.Write(b, o.Start, o.End - o.Start); w("\n"); continue; }
                    w(o.Num + " " + o.Gen + " obj\n" + o.NewDict + "\nstream\n");
                    fs.Write(o.NewData, 0, o.NewData.Length);
                    w("\nendstream\nendobj\n");
                }
                long xoff = fs.Position;
                using (MemoryStream xs = new MemoryStream())
                {
                    for (int n = 0; n <= xnum; n++)
                    {
                        int type = 0; long f2 = 0; int f3 = 0;
                        long o2; int[] pk;
                        if (n == xnum) { type = 1; f2 = xoff; }
                        else if (off.TryGetValue(n, out o2)) { type = 1; f2 = o2; f3 = objs[n].Gen; }
                        else if (packed.TryGetValue(n, out pk) && off.ContainsKey(pk[0])) { type = 2; f2 = pk[0]; f3 = pk[1]; }
                        else if (n == 0) { f3 = 65535; }
                        xs.WriteByte((byte)type);
                        xs.WriteByte((byte)(f2 >> 24)); xs.WriteByte((byte)(f2 >> 16)); xs.WriteByte((byte)(f2 >> 8)); xs.WriteByte((byte)f2);
                        xs.WriteByte((byte)(f3 >> 8)); xs.WriteByte((byte)f3);
                    }
                    byte[] xd = xs.ToArray();
                    w(xnum + " 0 obj\n<< /Type /XRef /Size " + (xnum + 1) + " /W [1 4 2] /Root " + root + (info != null ? " /Info " + info : "") + (id != null ? " /ID " + id : "") + " /Length " + xd.Length + " >>\nstream\n");
                    fs.Write(xd, 0, xd.Length);
                    w("\nendstream\nendobj\nstartxref\n" + xoff + "\n%%EOF\n");
                }
            }
        }

        static int DictEnd(string s, int i)
        {
            int depth = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c == '(')
                {
                    int d = 0;
                    for (; i < n; i++)
                    {
                        if (s[i] == '\\') { i++; continue; }
                        if (s[i] == '(') d++;
                        else if (s[i] == ')') { d--; if (d == 0) { i++; break; } }
                    }
                    continue;
                }
                if (c == '<')
                {
                    if (i + 1 < n && s[i + 1] == '<') { depth++; i += 2; continue; }
                    int j = s.IndexOf('>', i + 1); i = j < 0 ? n : j + 1; continue;
                }
                if (c == '>' && i + 1 < n && s[i + 1] == '>') { depth--; i += 2; if (depth == 0) return i; continue; }
                if (c == '%') { while (i < n && s[i] != '\n' && s[i] != '\r') i++; continue; }
                i++;
            }
            return -1;
        }

        /// A stream's /Length (following an indirect reference when needed), or -1.
        static int Length(string s, string dict)
        {
            Match m = Regex.Match(dict, @"/Length\s+(\d+)(\s+(\d+)\s+R)?");
            if (!m.Success) return -1;
            if (!m.Groups[2].Success) return int.Parse(m.Groups[1].Value);
            Match r = Regex.Match(s, @"(?<![0-9])" + m.Groups[1].Value + @"\s+" + m.Groups[3].Value + @"\s+obj\s*(\d+)\s*endobj", RegexOptions.RightToLeft);
            return r.Success ? int.Parse(r.Groups[1].Value) : -1;
        }

        static string Val(string dict, string key)
        {
            Match m = Regex.Match(dict, "/" + key + @"\s+(\d+\s+\d+\s+R)");
            return m.Success ? m.Groups[1].Value : null;
        }
        static string Arr(string dict, string key)
        {
            Match m = Regex.Match(dict, "/" + key + @"\s*(\[[^\]]*\])");
            return m.Success ? m.Groups[1].Value : null;
        }
        static int IntVal(string dict, string key)
        {
            Match m = Regex.Match(dict, "/" + key + @"\s+(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        /// zlib (FlateDecode) data without predictors; null when it isn't that.
        static byte[] Inflate(byte[] b, int start, int len, string dict)
        {
            if (!Regex.IsMatch(dict, @"/Filter\s*(\[\s*)?/FlateDecode\s*\]?") || dict.Contains("/Predictor")) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(b, start + 2, len - 2))
                using (DeflateStream z = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream o = new MemoryStream())
                {
                    z.CopyTo(o);
                    return o.ToArray();
                }
            }
            catch { return null; }
        }
    }
}
