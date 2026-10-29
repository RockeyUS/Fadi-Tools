using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    public class PrivacyReport
    {
        public List<string[]> Rows = new List<string[]>();      // label, value
        public double Lat = double.NaN, Lon = double.NaN;
        public bool CanClean;
        public int Orientation = 1;
        public bool Personal { get { return Rows.Count > 0; } }
        public void Add(string label, string value) { if (!string.IsNullOrEmpty(value) && value.Trim().Length > 0) Rows.Add(new[] { label, value.Trim() }); }
    }

    /// What a file says about whoever made it, hidden from view: where and with what a photo was taken, who wrote
    /// a document and with which company, a video's location. And a clean copy without it.
    public static class Privacy
    {
        public static PrivacyReport Read(string path, Sniff sniff)
        {
            PrivacyReport r = new PrivacyReport();
            string ext = sniff != null && sniff.Ext.Length > 0 ? sniff.Ext : Formats.Ext(path);
            try
            {
                if (ext == "jpg") { Jpeg(path, r); r.CanClean = true; }
                else if (ext == "png") { Png(path, r); r.CanClean = true; }
                else if (ext == "webp") { Webp(path, r); r.CanClean = true; }
                else if (ext == "tif" || ext == "tiff" || ext == "dng" || ext == "cr2" || ext == "nef" || ext == "arw") { byte[] all = Head(path, 32 << 20); Tiff(all, 0, r); r.CanClean = Tools.Magick != null; }
                else if (ext == "heic" || ext == "avif") { Media(path, r); r.CanClean = Tools.Magick != null; }
                else if (ext == "docx" || ext == "xlsx" || ext == "pptx") { Office(path, r); r.CanClean = true; }
                else if (ext == "pdf") { Pdf(path, r); r.CanClean = true; }
                else if (sniff != null && (sniff.Category == Category.Video || sniff.Category == Category.Audio)) { Media(path, r); r.CanClean = Tools.FFmpeg != null; }
            }
            catch { }
            if (!double.IsNaN(r.Lat)) r.Rows.Insert(0, new[] { L.T("pv_location"), "\u200E" + r.Lat.ToString("0.000000", CultureInfo.InvariantCulture) + ", " + r.Lon.ToString("0.000000", CultureInfo.InvariantCulture) });
            return r;
        }

        static byte[] Head(string path, int max)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] b = new byte[(int)Math.Min(max, fs.Length)];
                int got = 0, n;
                while (got < b.Length && (n = fs.Read(b, got, b.Length - got)) > 0) got += n;
                return b;
            }
        }

        // ------------------------------------------------------------------ photos: EXIF (a little TIFF inside the file)
        static void Jpeg(string path, PrivacyReport r)
        {
            byte[] b = Head(path, 4 << 20);
            int p = 2;
            while (p + 4 < b.Length && b[p] == 0xFF)
            {
                int m = b[p + 1];
                if (m == 0xDA || m == 0xD9) break;
                int len = (b[p + 2] << 8) | b[p + 3];
                if (m == 0xE1 && len > 8 && Encoding.ASCII.GetString(b, p + 4, 4) == "Exif") Tiff(b, p + 10, r);
                else if (m == 0xE1 && len > 30 && Encoding.ASCII.GetString(b, p + 4, Math.Min(28, len - 2)).StartsWith("http://ns.adobe.com/xap/1.0/")) Xmp(Encoding.UTF8.GetString(b, p + 4, Math.Min(len - 2, b.Length - p - 4)), r);
                else if (m == 0xFE && len > 2) r.Add(L.T("pv_comment"), Encoding.UTF8.GetString(b, p + 4, Math.Min(len - 2, 200)).Trim('\0'));
                else if (m == 0xED) r.Add(L.T("pv_iptc"), L.T("pv_present"));
                p += 2 + len;
            }
        }

        static void Png(string path, PrivacyReport r)
        {
            byte[] b = Head(path, 32 << 20);
            int p = 8;
            while (p + 12 <= b.Length)
            {
                int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
                string type = Encoding.ASCII.GetString(b, p + 4, 4);
                if (len < 0 || p + 12 + len > b.Length) break;
                if (type == "tEXt" || type == "iTXt" || type == "zTXt")
                {
                    int z = Array.IndexOf(b, (byte)0, p + 8, len);
                    if (z > 0)
                    {
                        string key = Encoding.ASCII.GetString(b, p + 8, z - p - 8);
                        string val = type == "tEXt" ? Encoding.GetEncoding(28591).GetString(b, z + 1, p + 8 + len - z - 1) : type == "iTXt" ? ITxt(b, z + 1, p + 8 + len) : L.T("pv_present");
                        if (key == "XML:com.adobe.xmp") Xmp(val, r);
                        else if (val.Length < 300) r.Add(key, val);
                    }
                }
                else if (type == "eXIf") Tiff(b, p + 8, r);
                else if (type == "tIME") r.Add(L.T("pv_changed"), (b[p + 8] << 8 | b[p + 9]) + "-" + b[p + 10].ToString("00") + "-" + b[p + 11].ToString("00"));
                if (type == "IEND") break;
                p += 12 + len;
            }
        }

        static string ITxt(byte[] b, int from, int end)
        {
            // flag, method, language\0, translated keyword\0, text
            int p = from + 2;
            for (int k = 0; k < 2 && p < end; k++) { int z = Array.IndexOf(b, (byte)0, p, end - p); if (z < 0) return ""; p = z + 1; }
            return p < end ? Encoding.UTF8.GetString(b, p, end - p) : "";
        }

        static void Webp(string path, PrivacyReport r)
        {
            byte[] b = Head(path, 32 << 20);
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                if (len < 0 || p + 8 + len > b.Length) break;
                if (id == "EXIF") Tiff(b, p + 8 + (Encoding.ASCII.GetString(b, p + 8, 4) == "Exif" ? 6 : 0), r);
                else if (id == "XMP ") Xmp(Encoding.UTF8.GetString(b, p + 8, len), r);
                p += 8 + len + (len & 1);
            }
        }

        /// A TIFF structure starting at `t` (EXIF): camera, phone, software, dates, owner, GPS.
        static void Tiff(byte[] b, int t, PrivacyReport r)
        {
            if (t + 8 > b.Length) return;
            bool le = b[t] == 'I';
            Func<int, int> u16 = i => i + 2 > b.Length ? 0 : le ? b[i] | (b[i + 1] << 8) : (b[i] << 8) | b[i + 1];
            Func<int, long> u32 = i => i + 4 > b.Length ? 0 : le ? (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)) : (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
            Dictionary<int, object> ifd0 = Ifd(b, t, (int)u32(t + 4), u16, u32);
            object o;
            Dictionary<int, object> exif = ifd0.TryGetValue(0x8769, out o) ? Ifd(b, t, (int)(long)o, u16, u32) : new Dictionary<int, object>();
            Dictionary<int, object> gps = ifd0.TryGetValue(0x8825, out o) ? Ifd(b, t, (int)(long)o, u16, u32) : new Dictionary<int, object>();
            Func<Dictionary<int, object>, int, string> s = (d, k) => { object v; return d.TryGetValue(k, out v) && v is string ? (string)v : null; };
            string make = s(ifd0, 0x010F), model = s(ifd0, 0x0110);
            if (model != null) r.Add(L.T("pv_camera"), make != null && !model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? make + " " + model : model);
            else r.Add(L.T("pv_camera"), make);
            r.Add(L.T("pv_lens"), s(exif, 0xA434));
            r.Add(L.T("pv_taken"), Date(s(exif, 0x9003) ?? s(ifd0, 0x0132)));
            r.Add(L.T("pv_software"), s(ifd0, 0x0131));
            r.Add(L.T("pv_owner"), s(exif, 0xA430) ?? s(ifd0, 0x013B));
            r.Add(L.T("pv_copyright"), s(ifd0, 0x8298));
            r.Add(L.T("pv_serial"), s(exif, 0xA431));
            r.Add(L.T("pv_description"), s(ifd0, 0x010E));
            if (ifd0.TryGetValue(0x0112, out o) && o is long) r.Orientation = (int)(long)o;
            double[] lat = gps.ContainsKey(2) ? gps[2] as double[] : null, lon = gps.ContainsKey(4) ? gps[4] as double[] : null;
            if (lat != null && lon != null && lat.Length >= 3 && lon.Length >= 3)
            {
                double la = lat[0] + lat[1] / 60 + lat[2] / 3600, lo = lon[0] + lon[1] / 60 + lon[2] / 3600;
                if (s(gps, 1) == "S") la = -la;
                if (s(gps, 3) == "W") lo = -lo;
                if (Math.Abs(la) > 0.0001 || Math.Abs(lo) > 0.0001) { r.Lat = la; r.Lon = lo; }
                double[] alt = gps.ContainsKey(6) ? gps[6] as double[] : null;
                if (alt != null && alt.Length > 0) r.Add(L.T("pv_altitude"), "\u200E" + Math.Round(alt[0]) + " m");
            }
        }

        static string Date(string exif)
        {
            if (exif == null) return null;
            DateTime d;
            return DateTime.TryParseExact(exif.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture) : exif;
        }

        /// One directory of tags: text (ASCII), numbers (SHORT/LONG) and fractions (RATIONAL, as double[]).
        static Dictionary<int, object> Ifd(byte[] b, int t, int off, Func<int, int> u16, Func<int, long> u32)
        {
            Dictionary<int, object> d = new Dictionary<int, object>();
            int p = t + off;
            if (off <= 0 || p + 2 > b.Length) return d;
            int n = u16(p);
            if (n > 500) return d;
            for (int i = 0; i < n; i++)
            {
                int e = p + 2 + i * 12;
                if (e + 12 > b.Length) break;
                int tag = u16(e), type = u16(e + 2);
                long count = u32(e + 4);
                int size = type == 2 || type == 1 || type == 7 ? 1 : type == 3 ? 2 : type == 4 || type == 9 ? 4 : type == 5 || type == 10 ? 8 : 0;
                if (size == 0 || count <= 0 || count > 65536) continue;
                int vp = size * count <= 4 ? e + 8 : t + (int)u32(e + 8);
                if (vp < 0 || vp + size * count > b.Length) continue;
                if (type == 2) d[tag] = Encoding.UTF8.GetString(b, vp, (int)count).TrimEnd('\0', ' ');
                else if (type == 7 && tag == 0x9286 && count > 8) d[tag] = Encoding.ASCII.GetString(b, vp + 8, (int)count - 8).TrimEnd('\0', ' ');
                else if (type == 3) d[tag] = (long)u16(vp);
                else if (type == 4) d[tag] = u32(vp);
                else if (type == 5 || type == 10)
                {
                    double[] v = new double[count];
                    for (int k = 0; k < count; k++) { long num = u32(vp + k * 8), den = u32(vp + k * 8 + 4); v[k] = den == 0 ? 0 : num / (double)den; }
                    d[tag] = v;
                }
            }
            return d;
        }

        static void Xmp(string x, PrivacyReport r)
        {
            r.Add(L.T("pv_tool"), Tag(x, "xmp:CreatorTool"));
            r.Add(L.T("pv_author"), Tag(x, "dc:creator"));
            if (x.Contains("photoshop:History") || x.Contains("xmpMM:History")) r.Add(L.T("pv_history"), L.T("pv_present"));
        }

        static string Tag(string x, string name)
        {
            Match m = Regex.Match(x, "<" + name + @"[^>]*>(.*?)</" + name + ">", RegexOptions.Singleline);
            if (m.Success) return Regex.Replace(m.Groups[1].Value, "<[^>]+>", " ").Trim();
            m = Regex.Match(x, name + "=\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ------------------------------------------------------------------ Word, Excel, PowerPoint
        static void Office(string path, PrivacyReport r)
        {
            using (ZipArchive z = ZipFile.OpenRead(path))
            {
                string core = Entry(z, "docProps/core.xml"), app = Entry(z, "docProps/app.xml");
                if (core != null)
                {
                    r.Add(L.T("pv_author"), Tag(core, "dc:creator"));
                    r.Add(L.T("pv_last_by"), Tag(core, "cp:lastModifiedBy"));
                    r.Add(L.T("pv_title"), Tag(core, "dc:title"));
                    r.Add(L.T("pv_revision"), Tag(core, "cp:revision"));
                    r.Add(L.T("pv_created"), IsoDate(Tag(core, "dcterms:created")));
                    r.Add(L.T("pv_changed"), IsoDate(Tag(core, "dcterms:modified")));
                    r.Add(L.T("pv_printed"), IsoDate(Tag(core, "cp:lastPrinted")));
                }
                if (app != null)
                {
                    r.Add(L.T("pv_company"), Tag(app, "Company"));
                    r.Add(L.T("pv_manager"), Tag(app, "Manager"));
                    r.Add(L.T("pv_software"), Tag(app, "Application"));
                    string tt = Tag(app, "TotalTime");
                    int mins; if (int.TryParse(tt, out mins) && mins > 0) r.Add(L.T("pv_edit_time"), L.T("pv_minutes", mins));
                    r.Add(L.T("pv_template"), Tag(app, "Template"));
                }
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string n = e.FullName.ToLowerInvariant();
                    if (n == "word/comments.xml" || n.StartsWith("xl/comments") || n.StartsWith("ppt/comments/")) { r.Add(L.T("pv_comments"), L.T("pv_present")); break; }
                }
                string doc = Entry(z, "word/document.xml");
                if (doc != null && (doc.Contains("<w:ins ") || doc.Contains("<w:del "))) r.Add(L.T("pv_tracked"), L.T("pv_tracked_d"));
            }
        }

        static string Entry(ZipArchive z, string name)
        {
            ZipArchiveEntry e = z.GetEntry(name);
            if (e == null || e.Length > 8 << 20) return null;
            using (StreamReader sr = new StreamReader(e.Open(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        static string IsoDate(string s)
        {
            DateTime d;
            return s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out d) ? d.ToLocalTime().ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture) : s;
        }

        // ------------------------------------------------------------------ PDF
        static void Pdf(string path, PrivacyReport r)
        {
            string s = Safety.Latin(path, 48 << 20);
            r.Add(L.T("pv_author"), PdfField(s, "Author"));
            r.Add(L.T("pv_title"), PdfField(s, "Title"));
            r.Add(L.T("pv_tool"), PdfField(s, "Creator"));
            r.Add(L.T("pv_software"), PdfField(s, "Producer"));
            r.Add(L.T("pv_created"), PdfDate(PdfField(s, "CreationDate")));
            r.Add(L.T("pv_changed"), PdfDate(PdfField(s, "ModDate")));
            if (r.Rows.Count == 0 && s.Contains("<x:xmpmeta")) Xmp(s, r);
        }

        static readonly Regex PdfStr = new Regex(@"/(Author|Title|Creator|Producer|Subject|Keywords|CreationDate|ModDate)\s*(\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>)", RegexOptions.Compiled);

        static string PdfField(string s, string name)
        {
            string last = null;
            foreach (Match m in PdfStr.Matches(s)) if (m.Groups[1].Value == name) last = m.Groups[2].Value;
            return last == null ? null : PdfText(last);
        }

        static string PdfText(string v)
        {
            byte[] bytes;
            if (v.StartsWith("<"))
            {
                string hex = Regex.Replace(v.Substring(1, v.Length - 2), @"\s", "");
                if (hex.Length % 2 == 1) hex += "0";
                bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            else
            {
                List<byte> l = new List<byte>();
                string t = v.Substring(1, v.Length - 2);
                for (int i = 0; i < t.Length; i++)
                {
                    char c = t[i];
                    if (c == '\\' && i + 1 < t.Length)
                    {
                        char n = t[++i];
                        if (n >= '0' && n <= '7') { int k = 0, val = 0; while (k < 3 && i < t.Length && t[i] >= '0' && t[i] <= '7') { val = val * 8 + (t[i] - '0'); i++; k++; } i--; l.Add((byte)val); }
                        else l.Add((byte)(n == 'n' ? '\n' : n == 'r' ? '\r' : n == 't' ? '\t' : n));
                    }
                    else l.Add((byte)c);
                }
                bytes = l.ToArray();
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).Trim('\0');
            return Encoding.GetEncoding(28591).GetString(bytes).Trim('\0');
        }

        static string PdfDate(string s)
        {
            if (s == null) return null;
            Match m = Regex.Match(s, @"D:(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?");
            if (!m.Success) return s;
            return m.Groups[1].Value + "-" + (m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : "01") + "-" + (m.Groups[3].Value.Length > 0 ? m.Groups[3].Value : "01") + (m.Groups[4].Value.Length > 0 ? "  " + m.Groups[4].Value + ":" + (m.Groups[5].Value.Length > 0 ? m.Groups[5].Value : "00") : "");
        }

        // ------------------------------------------------------------------ videos and songs (tags FFprobe reads)
        static void Media(string path, PrivacyReport r)
        {
            string fp = Tools.FFprobe;
            if (fp == null) return;
            byte[] o = FileConverter.Media.Capture(fp, "-v error -show_entries format_tags:stream_tags -of json " + Tools.Quote(path), 30000);
            if (o == null) return;
            object root = new JsonParser(Encoding.UTF8.GetString(o)).Parse();
            Dictionary<string, string> tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<object> take = obj =>
            {
                JsonObject to = Js.Get(obj, "tags") as JsonObject;
                if (to == null) return;
                foreach (KeyValuePair<string, object> kv in to) if (!tags.ContainsKey(kv.Key) && kv.Value is string) tags[kv.Key] = (string)kv.Value;
            };
            take(Js.Get(root, "format"));
            List<object> streams = Js.Get(root, "streams") as List<object>;
            if (streams != null) foreach (object st in streams) take(st);
            Func<string[], string> any = keys => { foreach (string k in keys) { string v; if (tags.TryGetValue(k, out v) && v.Trim().Length > 0) return v; } return null; };
            string loc = any(new[] { "com.apple.quicktime.location.ISO6709", "location", "location-eng" });
            if (loc != null)
            {
                Match m = Regex.Match(loc, @"([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)");
                if (m.Success) { r.Lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); r.Lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); }
            }
            string make = any(new[] { "com.apple.quicktime.make", "make", "com.android.manufacturer" }), model = any(new[] { "com.apple.quicktime.model", "model", "com.android.model" });
            r.Add(L.T("pv_camera"), model != null && make != null && !model.StartsWith(make) ? make + " " + model : model ?? make);
            r.Add(L.T("pv_software"), any(new[] { "com.apple.quicktime.software", "software", "com.android.version" }));
            r.Add(L.T("pv_tool"), any(new[] { "encoder", "encoded_by", "ENCODER" }));
            r.Add(L.T("pv_taken"), IsoDate(any(new[] { "com.apple.quicktime.creationdate", "creation_time", "date" })));
            r.Add(L.T("pv_author"), any(new[] { "artist", "author", "ARTIST" }));
            r.Add(L.T("pv_comment"), any(new[] { "comment", "description", "COMMENT" }));
            r.Add(L.T("pv_copyright"), any(new[] { "copyright" }));
        }

        // ------------------------------------------------------------------ the clean copy
        /// A copy without the hidden details (next to the file): pictures and videos as they are (nothing re-done),
        /// Office files with the author, company and edit history blanked, PDFs with their info blanked.
        public static string Clean(string path, Sniff sniff)
        {
            string ext = sniff != null && sniff.Ext.Length > 0 ? sniff.Ext : Formats.Ext(path);
            string fileExt = Formats.Ext(path).Length > 0 ? Formats.Ext(path) : ext;
            string outp = Paths.UniquePath(DragWheel.OutDirFor(path), Path.GetFileNameWithoutExtension(path) + " " + L.T("pv_clean_suffix"), fileExt);
            if (ext == "jpg") { PrivacyReport r = new PrivacyReport(); Jpeg(path, r); File.WriteAllBytes(outp, CleanJpeg(File.ReadAllBytes(path), r.Orientation)); }
            else if (ext == "png") File.WriteAllBytes(outp, CleanPng(File.ReadAllBytes(path)));
            else if (ext == "webp") File.WriteAllBytes(outp, CleanWebp(File.ReadAllBytes(path)));
            else if (ext == "docx" || ext == "xlsx" || ext == "pptx") CleanOffice(path, outp);
            else if (ext == "pdf") File.WriteAllBytes(outp, CleanPdf(File.ReadAllBytes(path)));
            else if (sniff != null && (sniff.Category == Category.Video || sniff.Category == Category.Audio))
            {
                string o;
                int code = Tools.RunHidden(Tools.FFmpeg, "-hide_banner -y -i " + Tools.Quote(path) + " -map 0 -map_metadata -1 -map_chapters -1 -c copy -fflags +bitexact -flags:v +bitexact -flags:a +bitexact " + Tools.Quote(outp), 30 * 60 * 1000, out o);
                if (code != 0 || !File.Exists(outp)) throw new UserError(L.T("pv_clean_failed") + " " + Tools.LastLines(o, 1));
            }
            else
            {
                string o;
                Tools.RunHidden(Tools.Magick, Tools.Quote(path) + " -auto-orient -strip " + Tools.Quote(outp), 5 * 60 * 1000, out o);
                if (!File.Exists(outp)) throw new UserError(L.T("pv_clean_failed"));
            }
            return outp;
        }

        static byte[] CleanJpeg(byte[] b, int orientation)
        {
            MemoryStream o = new MemoryStream();
            o.WriteByte(0xFF); o.WriteByte(0xD8);
            int p = 2;
            bool wroteOrientation = false;
            while (p + 4 <= b.Length && b[p] == 0xFF)
            {
                int m = b[p + 1];
                if (m == 0xDA) break;
                int len = (b[p + 2] << 8) | b[p + 3];
                bool drop = m == 0xE1 || m == 0xED || m == 0xFE || (m >= 0xE3 && m <= 0xEC) || m == 0xEF;
                // the photo's turn is kept (else it would show sideways): a tiny EXIF with only that, right after JFIF
                if (!wroteOrientation && orientation > 1 && orientation <= 8 && m != 0xE0) { WriteOrientation(o, orientation); wroteOrientation = true; }
                if (!drop) o.Write(b, p, 2 + len);
                p += 2 + len;
            }
            if (!wroteOrientation && orientation > 1 && orientation <= 8) WriteOrientation(o, orientation);
            o.Write(b, p, b.Length - p);
            return o.ToArray();
        }

        static void WriteOrientation(MemoryStream o, int orientation)
        {
            byte[] exif = { 0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
                (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
                0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00 };
            o.Write(exif, 0, exif.Length);
        }

        static byte[] CleanPng(byte[] b)
        {
            MemoryStream o = new MemoryStream();
            o.Write(b, 0, 8);
            int p = 8;
            while (p + 12 <= b.Length)
            {
                int len = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
                if (len < 0 || p + 12 + len > b.Length) break;
                string type = Encoding.ASCII.GetString(b, p + 4, 4);
                if (type != "tEXt" && type != "zTXt" && type != "iTXt" && type != "eXIf" && type != "tIME") o.Write(b, p, 12 + len);
                p += 12 + len;
                if (type == "IEND") break;
            }
            return o.ToArray();
        }

        static byte[] CleanWebp(byte[] b)
        {
            MemoryStream o = new MemoryStream();
            o.Write(b, 0, 12);
            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = Encoding.ASCII.GetString(b, p, 4);
                int len = BitConverter.ToInt32(b, p + 4);
                if (len < 0 || p + 8 + len > b.Length) break;
                int full = 8 + len + (len & 1);
                if (id == "VP8X") { byte[] c = new byte[Math.Min(full, b.Length - p)]; Array.Copy(b, p, c, 0, c.Length); c[8] = (byte)(c[8] & ~0x0C); o.Write(c, 0, c.Length); }
                else if (id != "EXIF" && id != "XMP ") o.Write(b, p, Math.Min(full, b.Length - p));
                p += full;
            }
            byte[] r = o.ToArray();
            byte[] size = BitConverter.GetBytes(r.Length - 8);
            Array.Copy(size, 0, r, 4, 4);
            return r;
        }

        static void CleanOffice(string path, string outp)
        {
            using (ZipArchive src = ZipFile.OpenRead(path))
            using (FileStream fs = File.Create(outp))
            using (ZipArchive dst = new ZipArchive(fs, ZipArchiveMode.Create))
                foreach (ZipArchiveEntry e in src.Entries)
                {
                    ZipArchiveEntry n = dst.CreateEntry(e.FullName, CompressionLevel.Optimal);
                    n.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    string name = e.FullName;
                    if (name == "docProps/core.xml" || name == "docProps/app.xml")
                    {
                        string x;
                        using (StreamReader sr = new StreamReader(e.Open(), Encoding.UTF8)) x = sr.ReadToEnd();
                        foreach (string t in new[] { "dc:creator", "cp:lastModifiedBy", "cp:lastPrinted", "Company", "Manager", "cp:keywords", "dc:description" })
                            x = Regex.Replace(x, "<" + t + @"(\s[^>]*)?>.*?</" + t + ">", "<" + t + "$1></" + t + ">", RegexOptions.Singleline);
                        x = Regex.Replace(x, @"<cp:revision>\d+</cp:revision>", "<cp:revision>1</cp:revision>");
                        x = Regex.Replace(x, @"<TotalTime>\d+</TotalTime>", "<TotalTime>0</TotalTime>");
                        using (StreamWriter sw = new StreamWriter(n.Open(), new UTF8Encoding(false))) sw.Write(x);
                        continue;
                    }
                    using (Stream a = e.Open()) using (Stream z = n.Open()) a.CopyTo(z);
                }
        }

        /// The PDF's info blanked in place (the same length, so nothing else in the file moves).
        static byte[] CleanPdf(byte[] b)
        {
            string s = Encoding.GetEncoding(28591).GetString(b);
            foreach (Match m in PdfStr.Matches(s))
            {
                string key = m.Groups[1].Value;
                if (key == "CreationDate" || key == "ModDate") continue;
                Group g = m.Groups[2];
                for (int i = g.Index + 1; i < g.Index + g.Length - 1; i++) b[i] = g.Value[0] == '<' ? (byte)'0' : (byte)' ';
            }
            foreach (string tag in new[] { "dc:creator", "xmp:CreatorTool", "pdf:Producer", "pdf:Author", "dc:title", "dc:description", "pdf:Keywords" })
                foreach (Match m in Regex.Matches(s, "<" + tag + @"[^>]*>(.*?)</" + tag + ">", RegexOptions.Singleline))
                {
                    Group g = m.Groups[1];
                    // keep the inner tags (rdf:Seq…), blank the words between them
                    bool inTag = false;
                    for (int i = g.Index; i < g.Index + g.Length; i++)
                    {
                        char c = s[i];
                        if (c == '<') inTag = true;
                        if (!inTag && c != '\n' && c != '\r') b[i] = (byte)' ';
                        if (c == '>') inTag = false;
                    }
                }
            return b;
        }
    }
}
