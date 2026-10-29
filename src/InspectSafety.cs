using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace FileConverter
{
    /// One thing worth knowing about a file's safety: 0 good to know, 1 be careful, 2 dangerous.
    public class Finding
    {
        public int Level;
        public string Title, Detail;
        public Finding(int level, string title, string detail) { Level = level; Title = title; Detail = detail; }
    }

    public class SafetyReport
    {
        public int Level;                       // the worst finding
        public List<Finding> Findings = new List<Finding>();
        public bool FromInternet;               // Windows marked it as downloaded ("Mark of the Web")
        public string HostUrl, ReferrerUrl;
        public bool RunsCode;
        public string Signer; public bool Signed, SignatureValid;
        public long HiddenAt = -1;              // data after the end of the file starts here
        public string HiddenType;
        public void Add(int level, string title, string detail) { Findings.Add(new Finding(level, title, detail)); if (level > Level) Level = level; }
    }

    /// Where a downloaded file came from: Windows writes it into the file's hidden "Zone.Identifier" part.
    public static class Zone
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool DeleteFileW(string name);
        const uint GENERIC_READ = 0x80000000, OPEN_EXISTING = 3, SHARE_ALL = 7;

        static bool Windows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        /// ZoneId (3 = from the internet), HostUrl, ReferrerUrl; null when there's no mark.
        public static Dictionary<string, string> Read(string path)
        {
            if (!Windows) return null;
            try
            {
                using (SafeFileHandle h = CreateFileW(path + ":Zone.Identifier", GENERIC_READ, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    using (FileStream fs = new FileStream(h, FileAccess.Read))
                    using (StreamReader sr = new StreamReader(fs, Encoding.Default))
                    {
                        Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                        return d;
                    }
                }
            }
            catch { return null; }
        }

        /// "Unblock": removes the downloaded mark (Windows stops warning about the file).
        public static bool Unblock(string path) { return Windows && DeleteFileW(path + ":Zone.Identifier"); }
    }

    /// Checks a file for the usual tricks, offline: what it is and whether it runs code, who signed it, where it was
    /// downloaded from, names that hide the real type, macros, scripts in PDFs, programs inside archives, data hidden
    /// after the end of the file.
    public static class Safety
    {
        static readonly string[] Runs = { "exe", "scr", "com", "pif", "bat", "cmd", "ps1", "psm1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "msi", "msp", "lnk", "jar", "reg", "cpl", "dll", "sys", "ocx", "appx", "msix", "application", "iso", "img", "vhd", "vhdx", "url", "scf", "chm", "msc", "xll", "appref-ms", "docm", "xlsm", "pptm", "dotm", "xltm" };
        static readonly string[] Programs = { "exe", "scr", "com", "pif", "bat", "cmd", "ps1", "vbs", "vbe", "js", "jse", "wsf", "hta", "msi", "lnk", "jar", "reg", "cpl", "dll" };
        static readonly string[] Harmless = { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "jpg", "jpeg", "png", "gif", "mp4", "mp3", "mov", "zip", "rar", "csv", "rtf", "wav", "avi", "webp", "heic" };

        public static bool RunsCode(string ext) { return Array.IndexOf(Runs, ext) >= 0; }

        public static SafetyReport Check(string path, Sniff sniff)
        {
            SafetyReport r = new SafetyReport();
            string name = Path.GetFileName(path), ext = Formats.Ext(path);
            string real = sniff != null && sniff.Ext.Length > 0 ? sniff.Ext : ext;
            bool pe = sniff != null && (sniff.Ext == "exe" || sniff.Ext == "dll") || Rea.IsPe(path);
            r.RunsCode = RunsCode(ext) || pe;

            // ---- the name
            if (name.IndexOf('‮') >= 0 || name.IndexOf('‭') >= 0 || name.IndexOf('‏') >= 0)
                r.Add(2, L.T("sf_rlo_t"), L.T("sf_rlo_d"));
            string[] parts = name.Split('.');
            if (parts.Length >= 3)
            {
                string inner = parts[parts.Length - 2].ToLowerInvariant().Trim();
                if (Array.IndexOf(Harmless, inner) >= 0 && Array.IndexOf(Programs, ext) >= 0)
                    r.Add(2, L.T("sf_double_t"), L.T("sf_double_d", "." + inner, "." + ext));
            }
            if (Regex.IsMatch(name, @"\s{6,}\.[A-Za-z0-9]+$")) r.Add(2, L.T("sf_spaces_t"), L.T("sf_spaces_d"));
            if (sniff != null && sniff.Ext.Length > 0 && ext != sniff.Ext && pe && !(ext == "exe" || ext == "dll" || ext == "sys" || ext == "scr" || ext == "ocx" || ext == "cpl" || ext == "msi" || ext == "efi" || ext == "mui" || ext == "node" || ext == "pyd"))
                r.Add(2, L.T("sf_hidden_prog_t"), L.T("sf_hidden_prog_d", ext.ToUpperInvariant()));

            // ---- downloaded from the internet
            Dictionary<string, string> z = Zone.Read(path);
            if (z != null)
            {
                string zid; z.TryGetValue("ZoneId", out zid);
                r.FromInternet = zid == "3" || zid == "4";
                string host, refr; z.TryGetValue("HostUrl", out host); z.TryGetValue("ReferrerUrl", out refr);
                r.HostUrl = host; r.ReferrerUrl = refr;
                if (r.FromInternet)
                {
                    string from = !string.IsNullOrEmpty(refr) && !refr.StartsWith("about:") ? refr : host;
                    string site = from; try { site = new Uri(from).Host; } catch { }
                    r.Add(r.RunsCode ? 1 : 0, L.T("sf_web_t"), string.IsNullOrEmpty(site) ? L.T("sf_web_d0") : L.T("sf_web_d", site));
                }
            }

            // ---- programs: who signed it
            if (pe || ext == "msi" || ext == "ps1" || ext == "cab" || ext == "appx" || ext == "msix" || ext == "sys" || ext == "cat")
            {
                string pub = null; bool valid = false;
                try { pub = Signature.Publisher(path); valid = pub != null && Signature.Valid(path); } catch { }
                r.Signed = pub != null; r.SignatureValid = valid; r.Signer = pub;
                if (r.Signed && valid) r.Add(0, L.T("sf_signed_t", pub), L.T("sf_signed_d"));
                else if (r.Signed) r.Add(2, L.T("sf_badsig_t"), L.T("sf_badsig_d", pub));
                else r.Add(1, L.T("sf_unsigned_t"), L.T("sf_unsigned_d"));
            }
            else if (r.RunsCode) r.Add(1, L.T("sf_runs_t"), L.T("sf_runs_d"));

            // ---- what's inside
            try { Contents(path, real, r); } catch { }

            // ---- shortcuts that start a command line
            if (ext == "lnk")
            {
                ShortcutInfo s = Special.Shortcut(path);
                if (s != null && s.Target != null)
                {
                    string t = Path.GetFileName(s.Target).ToLowerInvariant();
                    if (t == "powershell.exe" || t == "pwsh.exe" || t == "cmd.exe" || t == "mshta.exe" || t == "wscript.exe" || t == "cscript.exe" || t == "rundll32.exe" || t == "regsvr32.exe" || t == "bitsadmin.exe" || t == "certutil.exe")
                        r.Add(2, L.T("sf_lnk_t"), L.T("sf_lnk_d", Path.GetFileName(s.Target)));
                }
            }

            // ---- something added after the end of the file
            string hiddenType;
            long end = Hidden.EndOf(path, real, out hiddenType);
            long size = new FileInfo(path).Length;
            if (end > 0 && end < size && size - end > 16)
            {
                r.HiddenAt = end; r.HiddenType = hiddenType;
                bool pdf = real == "pdf";
                r.Add(pe || pdf ? 0 : 1, L.T("sf_extra_t", Theme.HumanSize(size - end)), L.T(hiddenType != null ? "sf_extra_d_kind" : "sf_extra_d", hiddenType ?? ""));
            }

            if (r.Findings.Count == 0 || r.Level == 0 && !r.RunsCode)
                r.Findings.Insert(0, new Finding(0, L.T("sf_nothing_t"), L.T(r.RunsCode ? "sf_nothing_prog_d" : "sf_nothing_d")));
            return r;
        }

        /// Macros, PDF scripts and actions, programs packed inside archives, encrypted archives.
        static void Contents(string path, string real, SafetyReport r)
        {
            long size = new FileInfo(path).Length;
            if (real == "pdf")
            {
                string s = Latin(path, 24 << 20);
                bool js = Regex.IsMatch(s, @"/(JavaScript|JS)\b"), launch = s.Contains("/Launch"), embedded = s.Contains("/EmbeddedFile"), open = Regex.IsMatch(s, @"/OpenAction|/AA\b");
                if (launch) r.Add(2, L.T("sf_pdf_launch_t"), L.T("sf_pdf_launch_d"));
                if (js) r.Add(open ? 2 : 1, L.T("sf_pdf_js_t"), L.T(open ? "sf_pdf_js_open_d" : "sf_pdf_js_d"));
                if (embedded) r.Add(1, L.T("sf_pdf_files_t"), L.T("sf_pdf_files_d"));
                return;
            }
            // old Office files: their macros sit in a "_VBA_PROJECT" stream
            if (real == "doc" || real == "xls" || real == "ppt" || real == "msi" || real == "msg")
            {
                string s = Latin(path, 24 << 20);
                if (s.Contains("_\0V\0B\0A\0_\0P\0R\0O\0J\0E\0C\0T") || s.Contains("VBA\0P\0R\0O\0J\0E\0C\0T"))
                    r.Add(2, L.T("sf_macros_t"), L.T("sf_macros_d"));
                return;
            }
            if (real == "zip" || real == "docx" || real == "xlsx" || real == "pptx" || real == "jar" || real == "apk" || real == "epub" || Formats.Ext(path) == "docm" || Formats.Ext(path) == "xlsm" || Formats.Ext(path) == "pptm")
            {
                List<ArchiveEntry> list = Inside.List(path);
                if (list == null) return;
                bool office = real == "docx" || real == "xlsx" || real == "pptx";
                long packed = 0, unpacked = 0; int progs = 0, enc = 0; string firstProg = null;
                foreach (ArchiveEntry e in list)
                {
                    packed += e.Packed; unpacked += e.Size;
                    if (e.Encrypted) enc++;
                    string en = e.Path.ToLowerInvariant();
                    if (office && en.EndsWith("vbaproject.bin")) { r.Add(2, L.T("sf_macros_t"), L.T("sf_macros_d")); office = false; }
                    if (en.EndsWith("/oleobject1.bin") || en.Contains("embeddings/oleobject")) r.Add(1, L.T("sf_ole_t"), L.T("sf_ole_d"));
                    string ee = Formats.Ext(e.Path);
                    if (!e.IsDir && Array.IndexOf(Programs, ee) >= 0 && real != "jar" && real != "apk") { progs++; if (firstProg == null) firstProg = Path.GetFileName(e.Path); }
                }
                if (progs > 0) r.Add(enc > 0 ? 2 : 1, L.T("sf_zip_progs_t", progs), L.T("sf_zip_progs_d", firstProg));
                if (enc > 0) r.Add(1, L.T("sf_zip_locked_t"), L.T("sf_zip_locked_d"));
                if (unpacked > (1L << 30) && packed > 0 && unpacked / Math.Max(1, packed) > 100) r.Add(2, L.T("sf_bomb_t"), L.T("sf_bomb_d", Theme.HumanSize(unpacked)));
            }
        }

        /// The start of a file as Latin-1 text (byte for byte), for quick searches.
        public static string Latin(string path, int max)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n = (int)Math.Min(max, fs.Length);
                byte[] b = new byte[n];
                int got = 0, rr;
                while (got < n && (rr = fs.Read(b, got, n - got)) > 0) got += rr;
                return Encoding.GetEncoding(28591).GetString(b, 0, got);
            }
        }
    }

    /// Data added after the real end of a file (a ZIP glued onto a picture, a program's appended payload).
    public static class Hidden
    {
        /// Where the file's own data ends, or -1 when that can't be told. kind: what the extra data looks like.
        public static long EndOf(string path, string ext, out string kind)
        {
            kind = null;
            long end = -1;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length;
                if (len < 16) return -1;
                if (ext == "jpg") end = JpegEnd(fs);
                else if (ext == "png") end = PngEnd(fs);
                else if (ext == "exe" || ext == "dll") end = Pe.OverlayStart(fs);
                else if (ext == "zip" || ext == "docx" || ext == "xlsx" || ext == "pptx" || ext == "apk" || ext == "jar") end = ZipEnd(fs);
                if (end > 0 && end < len)
                {
                    byte[] h = new byte[Math.Min(64, (int)(len - end))];
                    fs.Position = end; fs.Read(h, 0, h.Length);
                    kind = Guess(h);
                }
            }
            return end;
        }

        static string Guess(byte[] h)
        {
            if (h.Length >= 4 && h[0] == 0x50 && h[1] == 0x4B && h[2] == 3 && h[3] == 4) return "ZIP";
            if (h.Length >= 3 && h[0] == 0x52 && h[1] == 0x61 && h[2] == 0x72) return "RAR";
            if (h.Length >= 6 && h[0] == '7' && h[1] == 'z' && h[2] == 0xBC) return "7-Zip";
            if (h.Length >= 2 && h[0] == 'M' && h[1] == 'Z') return "EXE";
            if (h.Length >= 4 && h[0] == '%' && h[1] == 'P' && h[2] == 'D' && h[3] == 'F') return "PDF";
            if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "JPEG";
            if (h.Length >= 4 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return "PNG";
            return null;
        }

        static long JpegEnd(FileStream fs)
        {
            fs.Position = 2;
            byte[] b2 = new byte[2];
            // markers with lengths until the picture data starts
            while (true)
            {
                int m = fs.ReadByte();
                if (m < 0) return -1;
                if (m != 0xFF) return -1;
                int t = fs.ReadByte();
                while (t == 0xFF) t = fs.ReadByte();
                if (t < 0) return -1;
                if (t == 0xD9) return fs.Position;
                if (t >= 0xD0 && t <= 0xD7 || t == 0x01) continue;
                if (fs.Read(b2, 0, 2) < 2) return -1;
                int seg = (b2[0] << 8) | b2[1];
                if (t == 0xDA) { fs.Position += seg - 2; break; }
                fs.Position += seg - 2;
            }
            // the picture data: the end is the first FF D9 (an FF inside it is always followed by 00 or a restart marker)
            byte[] buf = new byte[1 << 16];
            int prev = -1;
            while (true)
            {
                long at = fs.Position;
                int n = fs.Read(buf, 0, buf.Length);
                if (n <= 0) return -1;
                for (int i = 0; i < n; i++)
                {
                    if (prev == 0xFF && buf[i] == 0xD9) return at + i + 1;
                    prev = buf[i];
                }
            }
        }

        static long PngEnd(FileStream fs)
        {
            fs.Position = 8;
            byte[] h = new byte[8];
            while (fs.Read(h, 0, 8) == 8)
            {
                long len = ((long)h[0] << 24) | ((long)h[1] << 16) | ((long)h[2] << 8) | h[3];
                string type = Encoding.ASCII.GetString(h, 4, 4);
                fs.Position += len + 4;
                if (type == "IEND") return fs.Position;
                if (fs.Position > fs.Length) return -1;
            }
            return -1;
        }

        static long ZipEnd(FileStream fs)
        {
            // the "end of central directory" record (and its comment) ends a ZIP
            long len = fs.Length;
            int back = (int)Math.Min(len, 65557);
            byte[] b = new byte[back];
            fs.Position = len - back; fs.Read(b, 0, back);
            for (int i = back - 22; i >= 0; i--)
                if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 5 && b[i + 3] == 6)
                {
                    int comment = b[i + 20] | (b[i + 21] << 8);
                    return len - back + i + 22 + comment;
                }
            return -1;
        }

        /// Saves the data after the end as a file of its own (next to the file).
        public static string Extract(string path, long from, string kind)
        {
            string ext = kind == null ? "bin" : kind == "7-Zip" ? "7z" : kind == "JPEG" ? "jpg" : kind.ToLowerInvariant();
            string outp = Paths.UniquePath(DragWheel.OutDirFor(path), Path.GetFileNameWithoutExtension(path) + " " + L.T("hd_part"), ext);
            using (FileStream src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (FileStream dst = File.Create(outp))
            {
                src.Position = from;
                src.CopyTo(dst);
            }
            return outp;
        }
    }
}
