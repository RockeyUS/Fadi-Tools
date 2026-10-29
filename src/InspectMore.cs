using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    public class ShortcutInfo { public string Target, Arguments, Folder, Icon, Description; }

    /// Files that are about something else: shortcuts (where they point), web links, certificates, fonts.
    public static class Special
    {
        public static ShortcutInfo Shortcut(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                object sh = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { path });
                Func<string, string> get = n => { try { return lnk.GetType().InvokeMember(n, BindingFlags.GetProperty, null, lnk, null) as string; } catch { return null; } };
                return new ShortcutInfo { Target = get("TargetPath"), Arguments = get("Arguments"), Folder = get("WorkingDirectory"), Icon = get("IconLocation"), Description = get("Description") };
            }
            catch { return null; }
        }

        public static string WebLink(string path)
        {
            try { foreach (string l in File.ReadAllLines(path)) if (l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return l.Substring(4).Trim(); } catch { }
            return null;
        }

        public static List<string[]> Certificate(string path)
        {
            List<string[]> r = new List<string[]>();
            X509Certificate2 c;
            try { c = new X509Certificate2(path); } catch { return r; }
            r.Add(new[] { L.T("ce_for"), Simple(c.Subject) });
            r.Add(new[] { L.T("ce_by"), Simple(c.Issuer) + (c.Subject == c.Issuer ? "  ·  " + L.T("ce_self") : "") });
            bool expired = c.NotAfter < DateTime.Now, early = c.NotBefore > DateTime.Now;
            r.Add(new[] { L.T("ce_valid"), c.NotBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "  →  " + c.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "  ·  " + L.T(expired ? "ce_expired" : early ? "ce_not_yet" : "ce_ok") });
            r.Add(new[] { L.T("ce_alg"), c.SignatureAlgorithm.FriendlyName ?? c.SignatureAlgorithm.Value });
            r.Add(new[] { L.T("ce_thumb"), c.Thumbprint, "1" });
            r.Add(new[] { L.T("ce_serial"), c.SerialNumber, "1" });
            try { foreach (X509Extension e in c.Extensions) if (e.Oid.Value == "2.5.29.17") r.Add(new[] { L.T("ce_names"), e.Format(false) }); } catch { }
            return r;
        }

        static string Simple(string dn)
        {
            Match m = Regex.Match(dn ?? "", @"CN=([^,]+)");
            Match o = Regex.Match(dn ?? "", @"O=([^,]+)");
            return m.Success ? m.Groups[1].Value.Trim('"') + (o.Success ? "  (" + o.Groups[1].Value.Trim('"') + ")" : "") : dn;
        }

        /// A font's names, and a picture of it in English and Arabic.
        public static Bitmap FontSample(string path, int width, out string family)
        {
            family = null;
            using (PrivateFontCollection pfc = new PrivateFontCollection())
            {
                pfc.AddFontFile(path);
                if (pfc.Families.Length == 0) return null;
                FontFamily ff = pfc.Families[0];
                family = ff.Name;
                FontStyle st = ff.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : ff.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Italic;
                string[] lines = { ff.Name, "The quick brown fox jumps over the lazy dog", "أبجد هوز حطي كلمن سعفص قرشت", "ABCDEFGHIJKLMNOPQRSTUVWXYZ", "abcdefghijklmnopqrstuvwxyz  0123456789  !?&@" };
                float[] sizes = { 40, 26, 26, 20, 20 };
                int h = 40;
                foreach (float s in sizes) h += (int)(s * 1.7f);
                Bitmap b = new Bitmap(width, h);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    float y = 20;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        using (Font f = new Font(ff, sizes[i], st, GraphicsUnit.Pixel))
                        using (StringFormat sf = new StringFormat(i == 2 ? StringFormatFlags.DirectionRightToLeft : 0))
                            g.DrawString(lines[i], f, Brushes.Black, new RectangleF(24, y, width - 48, sizes[i] * 1.6f), sf);
                        y += sizes[i] * 1.7f;
                    }
                }
                return b;
            }
        }
    }

    /// Everything about a video or song's insides, from FFprobe: each video, sound and subtitle track, chapters, HDR.
    public static class MediaDetails
    {
        public static List<string[]> Read(string path)
        {
            List<string[]> r = new List<string[]>();
            string fp = Tools.FFprobe;
            if (fp == null) return r;
            byte[] o = Media.Capture(fp, "-v error -show_streams -show_chapters -show_format -of json " + Tools.Quote(path), 30000);
            if (o == null) return r;
            object root = new JsonParser(Encoding.UTF8.GetString(o)).Parse();
            List<object> streams = Js.Get(root, "streams") as List<object> ?? new List<object>();
            int nv = 0, na = 0, ns = 0;
            foreach (object st in streams)
            {
                string type = Js.S(st, "codec_type"), codec = (Js.S(st, "codec_name") ?? "").ToUpperInvariant();
                object tags = Js.Get(st, "tags");
                string lang = Js.S(tags, "language"), title = Js.S(tags, "title");
                string extra = (lang != null && lang != "und" ? "  ·  " + Lang(lang) : "") + (title != null ? "  ·  “" + title + "”" : "");
                if (type == "video")
                {
                    if (Js.I(Js.Get(st, "disposition"), "attached_pic", 0) == 1) { r.Add(new[] { L.T("md_cover"), codec + "  ·  " + Js.I(st, "width", 0) + "×" + Js.I(st, "height", 0) }); continue; }
                    nv++;
                    List<string> parts = new List<string> { codec };
                    parts.Add(Js.I(st, "width", 0) + "×" + Js.I(st, "height", 0));
                    double fps = Rate(Js.S(st, "avg_frame_rate") ?? Js.S(st, "r_frame_rate"));
                    if (fps > 0) parts.Add(fps.ToString("0.##", CultureInfo.InvariantCulture) + " fps");
                    long br = Js.I(st, "bit_rate", 0); long brs; if (br == 0 && long.TryParse(Js.S(st, "bit_rate"), out brs)) br = brs;
                    if (br > 0) parts.Add(Kbps(br));
                    string tr = Js.S(st, "color_transfer");
                    if (tr == "smpte2084") parts.Add("HDR10"); else if (tr == "arib-std-b67") parts.Add("HLG HDR");
                    string pix = Js.S(st, "pix_fmt"); if (pix != null && pix.Contains("10")) parts.Add("10-bit");
                    int rot = Rotation(st); if (rot != 0) parts.Add(L.T("md_turned", rot));
                    r.Add(new[] { L.T("md_video", nv), string.Join("  ·  ", parts.ToArray()) + extra });
                }
                else if (type == "audio")
                {
                    na++;
                    List<string> parts = new List<string> { codec };
                    string layout = Js.S(st, "channel_layout"); int ch = Js.I(st, "channels", 0);
                    parts.Add(layout ?? (ch + " ch"));
                    string sr = Js.S(st, "sample_rate"); if (sr != null) parts.Add(sr + " Hz");
                    long brs; if (long.TryParse(Js.S(st, "bit_rate"), out brs) && brs > 0) parts.Add(Kbps(brs));
                    r.Add(new[] { L.T("md_audio", na), string.Join("  ·  ", parts.ToArray()) + extra });
                }
                else if (type == "subtitle") { ns++; r.Add(new[] { L.T("md_subs", ns), codec + extra }); }
            }
            List<object> chapters = Js.Get(root, "chapters") as List<object>;
            if (chapters != null && chapters.Count > 0) r.Add(new[] { L.T("md_chapters"), chapters.Count.ToString(CultureInfo.InvariantCulture) });
            object fmt = Js.Get(root, "format");
            long total; if (long.TryParse(Js.S(fmt, "bit_rate"), out total) && total > 0) r.Add(new[] { L.T("md_bitrate"), Kbps(total) });
            string cont = Js.S(fmt, "format_long_name"); if (cont != null) r.Add(new[] { L.T("md_container"), cont });
            return r;
        }

        static int Rotation(object st)
        {
            object tags = Js.Get(st, "tags");
            int r; if (int.TryParse(Js.S(tags, "rotate"), out r)) return ((r % 360) + 360) % 360;
            List<object> sd = Js.Get(st, "side_data_list") as List<object>;
            if (sd != null) foreach (object d in sd) { double v = Js.D(d, "rotation", 0); if (Math.Abs(v) > 0.5) return (((int)Math.Round(-v) % 360) + 360) % 360; }
            return 0;
        }

        static double Rate(string s)
        {
            if (s == null) return 0;
            string[] p = s.Split('/');
            double a, b;
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b > 0) return a / b;
            return 0;
        }

        static string Kbps(long bps) { return bps >= 1000000 ? (bps / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + " Mb/s" : (bps / 1000) + " kb/s"; }

        static string Lang(string code)
        {
            try { foreach (CultureInfo c in CultureInfo.GetCultures(CultureTypes.NeutralCultures)) if (c.ThreeLetterISOLanguageName == code || c.TwoLetterISOLanguageName == code) return c.EnglishName; } catch { }
            return code;
        }

        /// A picture's colour profile, bit depth, DPI, see-through and frame count (ImageMagick reads them all).
        public static List<string[]> Picture(string path)
        {
            List<string[]> r = new List<string[]>();
            string mg = Tools.Magick;
            if (mg == null) return r;
            byte[] o = Media.Capture(mg, "identify -format \"%w|%h|%z|%[channels]|%x|%y|%U|%[colorspace]\\n\" " + Tools.Quote(path), 30000);
            if (o == null) return r;
            string[] lines = Encoding.UTF8.GetString(o).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return r;
            string[] f = lines[0].Split('|');
            if (f.Length >= 8)
            {
                r.Add(new[] { L.T("pi_size"), f[0] + " × " + f[1] + " px" });
                r.Add(new[] { L.T("pi_depth"), f[2] + "-bit  ·  " + f[7] + "  ·  " + f[3] });
                double dx; if (double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out dx) && dx > 1) r.Add(new[] { L.T("pi_dpi"), Math.Round(dx) + (f[6].Contains("Centimeter") ? " px/cm" : " DPI") });
                r.Add(new[] { L.T("pi_alpha"), L.T(f[3].Contains("a") ? "yes" : "no") });
            }
            if (lines.Length > 1) r.Add(new[] { L.T("pi_frames"), lines.Length.ToString(CultureInfo.InvariantCulture) });
            byte[] icc = Media.Capture(mg, "identify -format \"%[profile:icc]\" " + Tools.Quote(path) + "[0]", 20000);
            string iccName = icc == null ? "" : Encoding.UTF8.GetString(icc).Trim();
            r.Add(new[] { L.T("pi_profile"), iccName.Length > 0 ? iccName : L.T("pi_no_profile") });
            return r;
        }
    }

    public class FolderReport
    {
        public int Files, Folders; public long Bytes;
        public Dictionary<Category, long[]> ByKind = new Dictionary<Category, long[]>();   // count, bytes
        public List<KeyValuePair<string, long>> Largest = new List<KeyValuePair<string, long>>();
        public List<string[]> Wrong = new List<string[]>();   // path, its extension, the real one, the real type's name
        public bool Truncated;
    }

    /// A whole folder: what takes the room, and files whose extension lies about what they are.
    public static class FolderScan
    {
        public static FolderReport Run(string dir, Func<bool> cancelled, Action<int> progress)
        {
            FolderReport r = new FolderReport();
            Stack<string> todo = new Stack<string>();
            todo.Push(dir);
            List<KeyValuePair<string, long>> all = new List<KeyValuePair<string, long>>();
            while (todo.Count > 0)
            {
                if (cancelled()) break;
                string d = todo.Pop();
                string[] files, subs;
                try { files = Directory.GetFiles(d); subs = Directory.GetDirectories(d); } catch { continue; }
                foreach (string s in subs) { try { if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; } todo.Push(s); r.Folders++; }
                foreach (string f in files)
                {
                    if (r.Files >= 50000) { r.Truncated = true; break; }
                    long len; try { len = new FileInfo(f).Length; } catch { continue; }
                    r.Files++; r.Bytes += len;
                    Category c = Formats.CategoryOf(Formats.Ext(f));
                    long[] k; if (!r.ByKind.TryGetValue(c, out k)) { k = new long[2]; r.ByKind[c] = k; }
                    k[0]++; k[1] += len;
                    all.Add(new KeyValuePair<string, long>(f, len));
                    if (r.Wrong.Count < 500 && len > 0)
                    {
                        Sniff s = null;
                        try { s = Sniffer.Quick(f); } catch { }
                        string e = Formats.Ext(f);
                        if (s != null && s.Ext.Length > 0 && e.Length > 0 && !InspectPage.SameExt(e, s.Ext)) r.Wrong.Add(new[] { f, e, s.Ext, s.Name });
                    }
                    if (r.Files % 200 == 0 && progress != null) progress(r.Files);
                }
                if (r.Truncated) break;
            }
            all.Sort((a, b) => b.Value.CompareTo(a.Value));
            r.Largest = all.GetRange(0, Math.Min(15, all.Count));
            return r;
        }

        /// Gives each listed file its real extension. Returns how many were renamed.
        public static int Fix(List<string[]> wrong)
        {
            int n = 0;
            foreach (string[] w in wrong)
            {
                try
                {
                    string to = Paths.UniquePath(Path.GetDirectoryName(w[0]), Path.GetFileNameWithoutExtension(w[0]), w[2]);
                    File.Move(w[0], to); n++;
                }
                catch { }
            }
            return n;
        }
    }

    public class CompareResult
    {
        public bool Same; public long SizeA, SizeB; public string HashA, HashB;
        public long FirstDiff = -1, DiffBytes; public bool BothText;
        public List<string> Lines = new List<string>();   // "+ ", "- ", "  " and "@@" lines
        public int Added, Removed; public bool TooLong;
    }

    /// Two files side by side: the same or not, where they first differ, and for text, which lines changed.
    public static class Comparer2
    {
        public static CompareResult Run(string a, string b, Func<bool> cancelled)
        {
            CompareResult r = new CompareResult();
            r.SizeA = new FileInfo(a).Length; r.SizeB = new FileInfo(b).Length;
            using (FileStream fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (FileStream fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 ha = SHA256.Create()) using (SHA256 hb = SHA256.Create())
            {
                byte[] ba = new byte[1 << 20], bb = new byte[1 << 20];
                long pos = 0;
                while (true)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    int na = Fill(fa, ba), nb = Fill(fb, bb);
                    if (na == 0 && nb == 0) break;
                    ha.TransformBlock(ba, 0, na, null, 0); hb.TransformBlock(bb, 0, nb, null, 0);
                    int m = Math.Min(na, nb);
                    for (int i = 0; i < m; i++) if (ba[i] != bb[i]) { r.DiffBytes++; if (r.FirstDiff < 0) r.FirstDiff = pos + i; }
                    if (na != nb && r.FirstDiff < 0) r.FirstDiff = pos + m;
                    pos += Math.Max(na, nb);
                }
                ha.TransformFinalBlock(new byte[0], 0, 0); hb.TransformFinalBlock(new byte[0], 0, 0);
                r.HashA = Hex(ha.Hash); r.HashB = Hex(hb.Hash);
            }
            r.Same = r.HashA == r.HashB;
            Sniff sa = null, sb = null;
            try { sa = Sniffer.Quick(a); sb = Sniffer.Quick(b); } catch { }
            r.BothText = sa != null && sb != null && sa.IsText && sb.IsText;
            if (!r.Same && r.BothText)
            {
                if (r.SizeA > 4 << 20 || r.SizeB > 4 << 20) r.TooLong = true;
                else Diff(File.ReadAllLines(a), File.ReadAllLines(b), r);
            }
            return r;
        }

        static int Fill(Stream s, byte[] b) { int got = 0, n; while (got < b.Length && (n = s.Read(b, got, b.Length - got)) > 0) got += n; return got; }
        static string Hex(byte[] h) { StringBuilder b = new StringBuilder(); foreach (byte x in h) b.Append(x.ToString("x2")); return b.ToString(); }

        /// Line by line (longest common part), shown as the changed parts with two lines around each.
        static void Diff(string[] a, string[] b, CompareResult r)
        {
            int pre = 0;
            while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
            int suf = 0;
            while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
            int n = a.Length - pre - suf, m = b.Length - pre - suf;
            if ((long)n * m > 9000000) { r.TooLong = true; return; }
            int[,] lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[pre + i] == b[pre + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            List<KeyValuePair<char, int>> ops = new List<KeyValuePair<char, int>>();   // ' ', '-', '+' with a line index
            for (int i = 0; i < pre; i++) ops.Add(new KeyValuePair<char, int>(' ', i));
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[pre + x] == b[pre + y]) { ops.Add(new KeyValuePair<char, int>(' ', pre + x)); x++; y++; }
                else if (x < n && (y >= m || lcs[x + 1, y] >= lcs[x, y + 1])) { ops.Add(new KeyValuePair<char, int>('-', pre + x)); x++; r.Removed++; }
                else { ops.Add(new KeyValuePair<char, int>('+', pre + y)); y++; r.Added++; }
            }
            for (int i = 0; i < suf; i++) ops.Add(new KeyValuePair<char, int>(' ', a.Length - suf + i));
            // only the changes, with two lines of context
            bool[] keep = new bool[ops.Count];
            for (int i = 0; i < ops.Count; i++) if (ops[i].Key != ' ') for (int k = Math.Max(0, i - 2); k <= Math.Min(ops.Count - 1, i + 2); k++) keep[k] = true;
            bool gap = false;
            int lineA = 0, lineB = 0;
            for (int i = 0; i < ops.Count; i++)
            {
                char k = ops[i].Key;
                string text = k == '+' ? b[ops[i].Value] : a[ops[i].Value];
                if (keep[i])
                {
                    if (gap || r.Lines.Count == 0) r.Lines.Add("@@ " + L.T("cmp_line", lineA + 1, lineB + 1));
                    gap = false;
                    r.Lines.Add((k == ' ' ? "  " : k + " ") + text);
                    if (r.Lines.Count > 5000) { r.Lines.Add("…"); return; }
                }
                else gap = true;
                if (k != '+') lineA++;
                if (k != '-') lineB++;
            }
        }
    }

    /// How random each part of a file is (packed or encrypted parts look random; text and empty parts don't).
    public static class EntropyMap
    {
        public static float[] Of(string path, int blocks)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                blocks = (int)Math.Max(1, Math.Min(blocks, len / 256));
                float[] r = new float[blocks];
                byte[] buf = new byte[8192];
                int[] counts = new int[256];
                for (int i = 0; i < blocks; i++)
                {
                    long start = len * i / blocks, end = len * (i + 1) / blocks;
                    fs.Position = start;
                    int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, end - start));
                    Array.Clear(counts, 0, 256);
                    for (int k = 0; k < n; k++) counts[buf[k]]++;
                    double e = 0;
                    for (int k = 0; k < 256; k++) if (counts[k] > 0) { double p = counts[k] / (double)n; e -= p * Math.Log(p, 2); }
                    r[i] = (float)(e / 8);
                }
                return r;
            }
        }

        /// The next place (from `from`) where these bytes appear, or -1.
        public static long Find(string path, byte[] what, long from, Func<bool> cancelled)
        {
            if (what == null || what.Length == 0) return -1;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[(1 << 20) + what.Length];
                long pos = Math.Max(0, from);
                while (pos < fs.Length)
                {
                    if (cancelled != null && cancelled()) return -1;
                    fs.Position = pos;
                    int n = 0, r;
                    while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                    for (int i = 0; i + what.Length <= n; i++)
                    {
                        if (buf[i] != what[0]) continue;
                        int k = 1;
                        while (k < what.Length && buf[i + k] == what[k]) k++;
                        if (k == what.Length) return pos + i;
                    }
                    if (n < buf.Length) break;
                    pos += n - what.Length + 1;
                }
                return -1;
            }
        }

        /// What was typed in the search box: hex bytes ("4D 5A 90" or "0x4D5A90") or text (UTF-8, or UTF-16).
        public static byte[] Pattern(string s, bool utf16)
        {
            s = (s ?? "").Trim();
            string h = null;
            if (Regex.IsMatch(s, @"^([0-9A-Fa-f]{2}\s+)+[0-9A-Fa-f]{2}$")) h = Regex.Replace(s, @"\s", "");
            else if (Regex.IsMatch(s, "^0x([0-9A-Fa-f]{2})+$")) h = s.Substring(2);
            if (h != null)
            {
                byte[] b = new byte[h.Length / 2];
                for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16);
                return b;
            }
            return utf16 ? Encoding.Unicode.GetBytes(s) : Encoding.UTF8.GetBytes(s);
        }
    }
}
