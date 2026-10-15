using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FileConverter
{
    public class Sniff
    {
        public string Name = "";        // human readable type
        public string Ext = "";         // suggested real extension ("" if unknown)
        public Category Category = Category.Unknown;
        public bool IsText;
        public string Encoding = "";
        public double Entropy;
        public long Size;
        public bool Encrypted;
    }

    /// Identifies a file from its content (magic bytes), not its extension.
    public static class Sniffer
    {
        public static Sniff Detect(string path)
        {
            Sniff s = new Sniff();
            FileInfo fi = new FileInfo(path);
            s.Size = fi.Length;
            byte[] h = new byte[Math.Min(65536, (int)Math.Min(int.MaxValue, fi.Length))];
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n = 0, r;
                while (n < h.Length && (r = fs.Read(h, n, h.Length - n)) > 0) n += r;
            }
            s.Entropy = EntropyOf(path);
            if (h.Length == 0) { s.Name = L.T("empty_file"); return s; }
            if (Magic(h, s, path)) return s;
            TextCheck(h, s);
            if (s.IsText) { ClassifyText(h, s); return s; }
            if (s.Entropy > 7.6) { s.Name = L.T("high_entropy"); s.Encrypted = true; return s; }
            s.Name = L.T("unknown_type");
            return s;
        }

        static bool At(byte[] b, int off, params byte[] sig)
        {
            if (b.Length < off + sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (b[off + i] != sig[i]) return false;
            return true;
        }
        static bool AtS(byte[] b, int off, string ascii) { return At(b, off, Encoding.ASCII.GetBytes(ascii)); }
        static void Set(Sniff s, string name, string ext, Category c) { s.Name = name; s.Ext = ext; s.Category = c; }

        static bool Magic(byte[] b, Sniff s, string path)
        {
            if (At(b, 0, 0x89, 0x50, 0x4E, 0x47)) { Set(s, "PNG image", "png", Category.Image); return true; }
            if (At(b, 0, 0xFF, 0xD8, 0xFF)) { Set(s, "JPEG image", "jpg", Category.Image); return true; }
            if (AtS(b, 0, "GIF8")) { Set(s, "GIF image", "gif", Category.Image); return true; }
            if (AtS(b, 0, "RIFF") && AtS(b, 8, "WEBP")) { Set(s, "WebP image", "webp", Category.Image); return true; }
            if (AtS(b, 0, "RIFF") && AtS(b, 8, "WAVE")) { Set(s, "WAV audio", "wav", Category.Audio); return true; }
            if (AtS(b, 0, "RIFF") && AtS(b, 8, "AVI ")) { Set(s, "AVI video", "avi", Category.Video); return true; }
            if (At(b, 0, 0x49, 0x49, 0x2A, 0x00) || At(b, 0, 0x4D, 0x4D, 0x00, 0x2A))
            {
                string ext = Formats.Ext(path);
                if (ext == "cr2" || ext == "nef" || ext == "arw" || ext == "dng" || ext == "orf" || ext == "rw2" || ext == "pef") { Set(s, "Camera RAW photo", ext, Category.Image); return true; }
                Set(s, "TIFF image", "tiff", Category.Image); return true;
            }
            if (AtS(b, 0, "BM") && b.Length > 30 && b[14] >= 12 && b[14] <= 124 && b[15] == 0) { Set(s, "Bitmap image", "bmp", Category.Image); return true; }
            if (At(b, 0, 0x00, 0x00, 0x01, 0x00) && b.Length > 6 && b[4] > 0) { Set(s, "Windows icon", "ico", Category.Image); return true; }
            if (AtS(b, 0, "8BPS")) { Set(s, "Photoshop document", "psd", Category.Image); return true; }
            if (AtS(b, 0, "DDS ")) { Set(s, "DirectDraw texture", "dds", Category.Image); return true; }
            if (At(b, 0, 0xFF, 0x0A) || At(b, 0, 0, 0, 0, 0x0C, 0x4A, 0x58, 0x4C, 0x20)) { Set(s, "JPEG XL image", "jxl", Category.Image); return true; }
            if (AtS(b, 4, "ftyp"))
            {
                string brand = Encoding.ASCII.GetString(b, 8, 4);
                switch (brand)
                {
                    case "heic": case "heix": case "heim": case "heis": case "mif1": case "msf1": case "hevc": case "hevx": Set(s, "HEIC image", "heic", Category.Image); return true;
                    case "avif": case "avis": Set(s, "AVIF image", "avif", Category.Image); return true;
                    case "qt  ": Set(s, "QuickTime video", "mov", Category.Video); return true;
                    case "M4A ": case "M4B ": Set(s, "M4A audio", "m4a", Category.Audio); return true;
                    case "3gp4": case "3gp5": case "3gp6": case "3g2a": Set(s, "3GP video", "3gp", Category.Video); return true;
                    case "crx ": Set(s, "Canon RAW photo", "cr3", Category.Image); return true;
                    case "M4V ": Set(s, "M4V video", "m4v", Category.Video); return true;
                    default: Set(s, "MP4 video", "mp4", Category.Video); return true;
                }
            }
            if (At(b, 0, 0x1A, 0x45, 0xDF, 0xA3))
            {
                bool webm = IndexOf(b, Encoding.ASCII.GetBytes("webm"), 64) >= 0;
                if (webm) Set(s, "WebM video", "webm", Category.Video); else Set(s, "Matroska video", "mkv", Category.Video);
                return true;
            }
            if (At(b, 0, 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11)) { Set(s, "Windows Media (WMV/WMA)", "wmv", Category.Video); return true; }
            if (AtS(b, 0, "FLV")) { Set(s, "Flash video", "flv", Category.Video); return true; }
            if (At(b, 0, 0x00, 0x00, 0x01, 0xBA)) { Set(s, "MPEG video", "mpg", Category.Video); return true; }
            if (b.Length > 376 && b[0] == 0x47 && b[188] == 0x47 && b[376] == 0x47) { Set(s, "MPEG transport stream", "ts", Category.Video); return true; }
            if (AtS(b, 0, "ID3") || (b.Length > 2 && b[0] == 0xFF && (b[1] & 0xE6) == 0xE2)) { Set(s, "MP3 audio", "mp3", Category.Audio); return true; }
            if (AtS(b, 0, "fLaC")) { Set(s, "FLAC audio", "flac", Category.Audio); return true; }
            if (AtS(b, 0, "OggS"))
            {
                if (IndexOf(b, Encoding.ASCII.GetBytes("OpusHead"), 128) >= 0) Set(s, "Opus audio", "opus", Category.Audio);
                else if (IndexOf(b, Encoding.ASCII.GetBytes("theora"), 128) >= 0) Set(s, "Ogg video", "ogv", Category.Video);
                else Set(s, "Ogg Vorbis audio", "ogg", Category.Audio);
                return true;
            }
            if (AtS(b, 0, "FORM") && (AtS(b, 8, "AIFF") || AtS(b, 8, "AIFC"))) { Set(s, "AIFF audio", "aiff", Category.Audio); return true; }
            if (AtS(b, 0, "#!AMR")) { Set(s, "AMR audio", "amr", Category.Audio); return true; }
            if (AtS(b, 0, "MThd")) { Set(s, "MIDI music", "mid", Category.Audio); return true; }
            if (b.Length > 2 && b[0] == 0xFF && (b[1] & 0xF6) == 0xF0) { Set(s, "AAC audio", "aac", Category.Audio); return true; }
            if (AtS(b, 0, "%PDF")) { Set(s, "PDF document", "pdf", Category.Document); return true; }
            if (AtS(b, 0, "{\\rtf")) { Set(s, "Rich Text document", "rtf", Category.Document); return true; }
            if (At(b, 0, 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1))
            {
                if (IndexOf(b, Encoding.Unicode.GetBytes("WordDocument"), b.Length) >= 0) Set(s, "Word 97-2003 document", "doc", Category.Document);
                else if (IndexOf(b, Encoding.Unicode.GetBytes("Workbook"), b.Length) >= 0 || IndexOf(b, Encoding.Unicode.GetBytes("Book"), b.Length) >= 0) Set(s, "Excel 97-2003 workbook", "xls", Category.Data);
                else if (IndexOf(b, Encoding.Unicode.GetBytes("PowerPoint"), b.Length) >= 0) Set(s, "PowerPoint 97-2003 presentation", "ppt", Category.Document);
                else Set(s, "Microsoft compound file (Office/MSI)", "", Category.Unknown);
                return true;
            }
            if (AtS(b, 0, "PK\x03\x04") || AtS(b, 0, "PK\x05\x06")) { Zip(path, s); return true; }
            if (At(b, 0, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) { Set(s, "7-Zip archive", "7z", Category.Unknown); return true; }
            if (AtS(b, 0, "Rar!")) { Set(s, "RAR archive", "rar", Category.Unknown); return true; }
            if (b.Length > 6 && b[0] == 0x78 && (b[1] == 0x01 || b[1] == 0x5E || b[1] == 0x9C || b[1] == 0xDA) && (b[0] * 256 + b[1]) % 31 == 0) { Set(s, "Compressed data (zlib)", "", Category.Unknown); return true; }
            if (At(b, 0, 0x1F, 0x8B)) { Set(s, "GZip compressed file", "gz", Category.Unknown); return true; }
            if (AtS(b, 0, "BZh")) { Set(s, "BZip2 compressed file", "bz2", Category.Unknown); return true; }
            if (At(b, 0, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) { Set(s, "XZ compressed file", "xz", Category.Unknown); return true; }
            if (At(b, 0, 0x28, 0xB5, 0x2F, 0xFD)) { Set(s, "Zstandard compressed file", "zst", Category.Unknown); return true; }
            if (AtS(b, 0, "MSCF")) { Set(s, "Windows cabinet archive", "cab", Category.Unknown); return true; }
            if (b.Length > 0x8006 && AtS(b, 0x8001, "CD001")) { Set(s, "Disc image (ISO)", "iso", Category.Unknown); return true; }
            if (AtS(b, 0, "MZ"))
            {
                int pe = b.Length > 0x40 ? BitConverter.ToInt32(b, 0x3C) : -1;
                bool isPe = pe > 0 && pe + 24 < b.Length && AtS(b, pe, "PE\0\0");
                bool dll = isPe && (BitConverter.ToUInt16(b, pe + 22) & 0x2000) != 0;
                Set(s, dll ? "Windows library (DLL)" : "Windows program (EXE)", dll ? "dll" : "exe", Category.Unknown); return true;
            }
            if (At(b, 0, 0x7F, 0x45, 0x4C, 0x46)) { Set(s, "Linux/Android executable (ELF)", "", Category.Unknown); return true; }
            if (At(b, 0, 0xCA, 0xFE, 0xBA, 0xBE)) { Set(s, "Java class / Mac universal binary", "class", Category.Unknown); return true; }
            if (AtS(b, 0, "SQLite format 3")) { Set(s, "SQLite database", "sqlite", Category.Data); return true; }
            if (AtS(b, 0, "regf")) { Set(s, "Windows registry hive", "", Category.Unknown); return true; }
            if (At(b, 0, 0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00)) { Set(s, "Windows shortcut", "lnk", Category.Unknown); return true; }
            if (AtS(b, 0, "wOFF") || AtS(b, 0, "wOF2")) { Set(s, "Web font (WOFF)", AtS(b, 0, "wOF2") ? "woff2" : "woff", Category.Unknown); return true; }
            if (At(b, 0, 0x00, 0x01, 0x00, 0x00, 0x00) || AtS(b, 0, "OTTO") || AtS(b, 0, "true")) { Set(s, "Font file", AtS(b, 0, "OTTO") ? "otf" : "ttf", Category.Unknown); return true; }
            // FromSoftware game archives (Elden Ring, Dark Souls...)
            if (AtS(b, 0, "DCX\0")) { Set(s, "FromSoftware DCX compressed archive", "dcx", Category.Unknown); return true; }
            if (AtS(b, 0, "BND4") || AtS(b, 0, "BND3")) { Set(s, "FromSoftware BND archive", "bnd", Category.Unknown); return true; }
            if (AtS(b, 0, "BHF4") || AtS(b, 0, "BDF4") || AtS(b, 0, "BHF3")) { Set(s, "FromSoftware split archive (BHD/BDT)", "", Category.Unknown); return true; }
            if (AtS(b, 0, "UnityFS")) { Set(s, "Unity asset bundle", "", Category.Unknown); return true; }
            if (AtS(b, 0, "NES\x1A")) { Set(s, "NES game ROM", "nes", Category.Unknown); return true; }
            return false;
        }

        static void Zip(string path, Sniff s)
        {
            Set(s, "ZIP archive", "zip", Category.Unknown);
            try
            {
                using (ZipArchive za = ZipFile.OpenRead(path))
                {
                    bool word = false, xl = false, ppt = false, manifest = false, android = false;
                    string mimetype = null;
                    foreach (ZipArchiveEntry e in za.Entries)
                    {
                        string n = e.FullName;
                        if (n.StartsWith("word/")) word = true;
                        if (n.StartsWith("xl/")) xl = true;
                        if (n.StartsWith("ppt/")) ppt = true;
                        if (n == "META-INF/MANIFEST.MF") manifest = true;
                        if (n == "AndroidManifest.xml") android = true;
                        if (n == "mimetype" && e.Length < 200)
                            using (StreamReader r = new StreamReader(e.Open())) mimetype = r.ReadToEnd().Trim();
                    }
                    if (word) Set(s, "Word document", "docx", Category.Document);
                    else if (xl) Set(s, "Excel workbook", "xlsx", Category.Data);
                    else if (ppt) Set(s, "PowerPoint presentation", "pptx", Category.Document);
                    else if (mimetype == "application/epub+zip") Set(s, "EPUB e-book", "epub", Category.Document);
                    else if (mimetype == "application/vnd.oasis.opendocument.text") Set(s, "OpenDocument text", "odt", Category.Document);
                    else if (mimetype == "application/vnd.oasis.opendocument.spreadsheet") Set(s, "OpenDocument spreadsheet", "ods", Category.Data);
                    else if (mimetype == "application/vnd.oasis.opendocument.presentation") Set(s, "OpenDocument presentation", "odp", Category.Document);
                    else if (android) Set(s, "Android app (APK)", "apk", Category.Unknown);
                    else if (manifest) Set(s, "Java archive (JAR)", "jar", Category.Unknown);
                }
            }
            catch { }
        }

        static int IndexOf(byte[] hay, byte[] needle, int limit)
        {
            int end = Math.Min(hay.Length, limit) - needle.Length;
            for (int i = 0; i <= end; i++)
            {
                int k = 0;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        static void TextCheck(byte[] b, Sniff s)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { s.IsText = true; s.Encoding = "UTF-8 (BOM)"; return; }
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { s.IsText = true; s.Encoding = "UTF-16 LE"; return; }
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { s.IsText = true; s.Encoding = "UTF-16 BE"; return; }
            int nul = 0, ctrl = 0;
            foreach (byte x in b) { if (x == 0) nul++; else if (x < 9 || (x > 13 && x < 32)) ctrl++; }
            if (nul > b.Length / 4 && b.Length > 16)
            {
                int evenNul = 0, oddNul = 0;
                for (int i = 0; i < b.Length; i++) if (b[i] == 0) { if (i % 2 == 0) evenNul++; else oddNul++; }
                if (oddNul > b.Length * 0.35 && evenNul < b.Length * 0.05) { s.IsText = true; s.Encoding = "UTF-16 LE"; return; }
                if (evenNul > b.Length * 0.35 && oddNul < b.Length * 0.05) { s.IsText = true; s.Encoding = "UTF-16 BE"; return; }
            }
            if (nul > 0 || ctrl > b.Length / 50) return;
            try
            {
                int len = b.Length;
                while (len > 0 && len > b.Length - 4 && (b[len - 1] & 0xC0) == 0x80) len--;
                if (len > 0 && b[len - 1] >= 0xC0) len--;
                new UTF8Encoding(false, true).GetString(b, 0, len);
                bool ascii = true; foreach (byte x in b) if (x > 127) { ascii = false; break; }
                s.IsText = true; s.Encoding = ascii ? "ASCII" : "UTF-8";
            }
            catch { s.IsText = true; s.Encoding = "ANSI (" + Encoding.Default.WebName + ")"; }
        }

        public static Encoding EncodingFor(Sniff s)
        {
            if (s.Encoding.StartsWith("UTF-16 LE")) return Encoding.Unicode;
            if (s.Encoding.StartsWith("UTF-16 BE")) return Encoding.BigEndianUnicode;
            if (s.Encoding.StartsWith("ANSI")) return Encoding.Default;
            return new UTF8Encoding(false);
        }

        static void ClassifyText(byte[] b, Sniff s)
        {
            string t;
            try { t = EncodingFor(s).GetString(b).TrimStart('﻿', ' ', '\r', '\n', '\t'); } catch { t = ""; }
            string low = t.Length > 2000 ? t.Substring(0, 2000).ToLowerInvariant() : t.ToLowerInvariant();
            if (low.StartsWith("<?xml") && low.Contains("<svg")) Set(s, "SVG image", "svg", Category.Image);
            else if (low.StartsWith("<svg")) Set(s, "SVG image", "svg", Category.Image);
            else if (low.StartsWith("<!doctype html") || low.StartsWith("<html") || (low.Contains("<head") && low.Contains("<body"))) Set(s, "HTML web page", "html", Category.Document);
            else if (low.StartsWith("<?xml") || (low.StartsWith("<") && low.Contains("</"))) Set(s, "XML data", "xml", Category.Data);
            else if ((low.StartsWith("{") || low.StartsWith("[")) && LooksJson(t)) Set(s, "JSON data", "json", Category.Data);
            else if (low.StartsWith("#!")) Set(s, "Script", "", Category.Document);
            else if (low.StartsWith("{\\rtf")) Set(s, "Rich Text document", "rtf", Category.Document);
            else if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(^|\n)(#{1,6} |\* |- |\d+\. |```|\[.+\]\(.+\))") && s.Size < 5 << 20) Set(s, "Markdown / text", "md", Category.Document);
            else if (LooksCsv(t)) Set(s, "CSV table", "csv", Category.Data);
            else Set(s, L.T("text_file"), "txt", Category.Document);
            s.IsText = true;
        }

        static bool LooksJson(string t)
        {
            try { new JsonParser(t.Length > 200000 ? t.Substring(0, 200000) : t).Parse(); return true; }
            catch { return t.Length > 200000; }
        }

        static bool LooksCsv(string t)
        {
            string[] lines = t.Split('\n');
            if (lines.Length < 3) return false;
            int c0 = lines[0].Split(',').Length;
            if (c0 < 2) return false;
            int same = 0;
            for (int i = 1; i < Math.Min(lines.Length, 20); i++) if (lines[i].Split(',').Length == c0) same++;
            return same >= Math.Max(1, Math.Min(lines.Length - 2, 5) / 2);
        }

        public static double EntropyOf(string path)
        {
            long[] counts = new long[256];
            long total = 0;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[1 << 16];
                int n;
                while (total < (4 << 20) && (n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++) counts[buf[i]]++;
                    total += n;
                }
            }
            if (total == 0) return 0;
            double e = 0;
            foreach (long c in counts) if (c > 0) { double p = c / (double)total; e -= p * Math.Log(p, 2); }
            return e;
        }

        public static string[] Hashes(string path, Action<double> progress, Func<bool> cancelled)
        {
            using (MD5 md5 = MD5.Create())
            using (SHA1 sha1 = SHA1.Create())
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
            {
                byte[] buf = new byte[1 << 20];
                long len = fs.Length, done = 0;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled()) return null;
                    md5.TransformBlock(buf, 0, n, null, 0);
                    sha1.TransformBlock(buf, 0, n, null, 0);
                    sha256.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if (progress != null && len > 0) progress(done / (double)len);
                }
                md5.TransformFinalBlock(buf, 0, 0); sha1.TransformFinalBlock(buf, 0, 0); sha256.TransformFinalBlock(buf, 0, 0);
                return new[] { Hex(md5.Hash), Hex(sha1.Hash), Hex(sha256.Hash) };
            }
        }

        static string Hex(byte[] b) { StringBuilder sb = new StringBuilder(); foreach (byte x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }

        /// Readable ASCII/UTF-16 strings found in the file (like the Unix "strings" tool).
        public static List<KeyValuePair<long, string>> Strings(string path, int minLen, int max)
        {
            List<KeyValuePair<long, string>> list = new List<KeyValuePair<long, string>>();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[1 << 16];
                StringBuilder cur = new StringBuilder();
                long start = 0, pos = 0;
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0 && list.Count < max && pos < (64L << 20))
                {
                    for (int i = 0; i < n; i++, pos++)
                    {
                        byte x = buf[i];
                        if (x >= 32 && x < 127) { if (cur.Length == 0) start = pos; cur.Append((char)x); }
                        else if (x == 0 && cur.Length > 0 && i + 1 < n && buf[i + 1] >= 32 && buf[i + 1] < 127 && cur.Length < 2) { }
                        else
                        {
                            if (cur.Length >= minLen) list.Add(new KeyValuePair<long, string>(start, cur.ToString()));
                            cur.Clear();
                        }
                    }
                }
                if (cur.Length >= minLen) list.Add(new KeyValuePair<long, string>(start, cur.ToString()));
            }
            // UTF-16LE pass over the first 8 MB
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[Math.Min(8 << 20, (int)Math.Min(int.MaxValue, fs.Length))];
                int n = fs.Read(buf, 0, buf.Length);
                StringBuilder cur = new StringBuilder(); long start = 0;
                for (int i = 0; i + 1 < n && list.Count < max; i += 2)
                {
                    char c = (char)(buf[i] | (buf[i + 1] << 8));
                    bool ok = buf[i + 1] == 0 ? (c >= 32 && c < 127) : (c >= 0x0600 && c <= 0x06FF);
                    if (ok) { if (cur.Length == 0) start = i; cur.Append(c); }
                    else { if (cur.Length >= minLen) list.Add(new KeyValuePair<long, string>(start, cur.ToString() + "  (UTF-16)")); cur.Clear(); }
                }
            }
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }
    }
}
