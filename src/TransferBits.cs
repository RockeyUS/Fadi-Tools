using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace FileConverter
{
    /// File Transfer's "Recent" list: what was sent and received lately, kept in the settings (the last 40).
    public static class FtHistory
    {
        public class Entry
        {
            public bool Sent;
            public long When;              // UTC ticks
            public string Name, Who;       // what, and to or from whom ("" when it went out with a code)
            public long Size;
            public int Count;
            /// Sent: the files and folders that were sent. Received: where it was saved (one path).
            public List<string> Paths = new List<string>();
            public bool Exists { get { foreach (string p in Paths) if (File.Exists(p) || Directory.Exists(p)) return true; return false; } }
        }
        public static event Action Changed;
        const int Max = 40;
        static string B(string s) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? "")); }
        static string U(string s) { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }

        /// The newest first.
        public static List<Entry> All()
        {
            List<Entry> l = new List<Entry>();
            foreach (string row in Settings.Get("TransferHistory", "").Split(';'))
            {
                string[] p = row.Split('|');
                if (p.Length < 7) continue;
                try
                {
                    Entry e = new Entry { Sent = p[0] == "s", When = long.Parse(p[1], CultureInfo.InvariantCulture), Name = U(p[2]), Size = long.Parse(p[3], CultureInfo.InvariantCulture), Count = int.Parse(p[4], CultureInfo.InvariantCulture), Who = U(p[5]) };
                    foreach (string x in U(p[6]).Split('\n')) if (x.Length > 0) e.Paths.Add(x);
                    l.Add(e);
                }
                catch { }
            }
            l.Sort((a, b) => b.When.CompareTo(a.When));
            return l;
        }

        static void Save(List<Entry> l)
        {
            List<string> rows = new List<string>();
            foreach (Entry e in l)
                rows.Add((e.Sent ? "s" : "r") + "|" + e.When.ToString(CultureInfo.InvariantCulture) + "|" + B(e.Name) + "|" + e.Size.ToString(CultureInfo.InvariantCulture) + "|" +
                         e.Count.ToString(CultureInfo.InvariantCulture) + "|" + B(e.Who) + "|" + B(string.Join("\n", e.Paths.ToArray())));
            Settings.Set("TransferHistory", string.Join(";", rows.ToArray()));
            if (Changed != null) try { Changed(); } catch { }
        }

        public static void Add(Entry e)
        {
            if (SelfTest.Active) return;
            e.When = DateTime.UtcNow.Ticks;
            List<Entry> l = All();
            l.Insert(0, e);
            if (l.Count > Max) l.RemoveRange(Max, l.Count - Max);
            Save(l);
        }

        public static void Clear() { Save(new List<Entry>()); }

        /// "5 min ago", "2 h ago", "3 days ago", or the date.
        public static string Ago(long ticks)
        {
            if (ticks <= 0) return L.T("ft_never");
            TimeSpan t = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            return t.TotalMinutes < 1 ? L.T("ft_now") : t.TotalHours < 1 ? L.T("ft_min_ago", (int)t.TotalMinutes) : t.TotalDays < 1 ? L.T("ft_h_ago", (int)t.TotalHours) :
                   t.TotalDays < 2 ? L.T("ft_yesterday") : t.TotalDays < 30 ? L.T("ft_d_ago", (int)t.TotalDays) : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    /// The picture of what's being sent or received: the file's own thumbnail when there is one (a photo, a video's
    /// frame, a PDF's first page), otherwise a tile with the kind's icon. Also makes the small JPEG that travels to
    /// the receiver so they see the same picture before accepting.
    public static class FtArt
    {
        /// The icon for a name: a folder, or by the kind of file.
        public static string GlyphFor(string name, bool folder)
        {
            if (folder) return Ico.Folder;
            switch (Formats.CategoryOf(Formats.Ext(name ?? "")))
            {
                case Category.Image: return Ico.Photo;
                case Category.Video: return Ico.Video;
                case Category.Audio: return Ico.Music;
                default: return Ico.Page;
            }
        }

        /// Draws the picture in a rounded box. path: a file on this PC (its thumbnail is looked up), or null;
        /// picture: one already in hand (the one the sender made); count > 1 draws a second tile peeking out behind.
        public static void Draw(Graphics g, Rectangle box, string path, Image picture, string name, bool folder, int count, Action repaint)
        {
            float rad = Math.Max(6, box.Width / 6f);
            if (count > 1)
            {
                // a stack: a second tile peeking out behind
                Rectangle back = new Rectangle(box.X + box.Width / 9, box.Y - box.Height / 12, box.Width - box.Width * 2 / 9, box.Height);
                Theme.FillRound(g, back, rad, Theme.Mix(Theme.AccentSoft, Theme.Card, 0.45f));
                Theme.StrokeRound(g, new RectangleF(back.X + 0.5f, back.Y + 0.5f, back.Width - 1, back.Height - 1), rad, Theme.Line);
            }
            bool drawn = false;
            // a photo fills the tile; an icon (a folder, a zip, a program: it has see-through parts) sits inside it
            if (picture != null) drawn = Image.IsAlphaPixelFormat(picture.PixelFormat) ? Contain(g, picture, box, rad) : Fill(g, picture, box, rad);
            else if (path != null)
            {
                if (!folder) drawn = Thumbs.Draw(g, path, box, rad, repaint);
                // no thumbnail of its own: the picture Windows shows for it (the folder with what's in it, the program's icon)
                if (!drawn && (folder || Thumbs.None(path))) { Image ic = IconFor(path, repaint); if (ic != null) drawn = Contain(g, ic, box, rad); }
            }
            if (!drawn)
            {
                Theme.FillRound(g, box, rad, Theme.AccentSoft);
                Theme.Glyph(g, GlyphFor(name, folder), Math.Max(9f, box.Width / (float)Theme.S(100) * 26f), Theme.AccentColor, box);
            }
            Theme.StrokeRound(g, new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), rad, Theme.Line);
            if (count > 1)
            {
                // how many, in a badge on the corner
                string n = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
                Font f = Theme.F(8f, FontStyle.Bold);
                int bw = Math.Max(Theme.S(22), Theme.Measure(n, f) + Theme.S(12)), bh = Theme.S(22);
                Rectangle badge = new Rectangle(box.Right - bw + Theme.S(6), box.Bottom - bh + Theme.S(6), bw, bh);
                Theme.FillRound(g, badge, bh / 2f, Theme.AccentFill);
                Theme.Text(g, n, f, Theme.PrimaryInk, badge, Align.Center, true, false, true);
            }
        }

        // ---- Windows' own picture of a file or folder (kept in memory; made on a thread of its own)
        static readonly Dictionary<string, Image> icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> noIcon = new HashSet<string>(StringComparer.OrdinalIgnoreCase), asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// The picture, or null while it's being fetched (ready is called when it's there) or when there's none.
        public static Image IconFor(string path, Action ready)
        {
            if (string.IsNullOrEmpty(path) || Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            lock (icons)
            {
                Image im;
                if (icons.TryGetValue(path, out im)) return im;
                if (noIcon.Contains(path) || !asked.Add(path)) return null;
            }
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                Image got = null;
                try { got = Thumbs.ShellPicture(path, 256); } catch { }
                lock (icons) { if (got != null) icons[path] = got; else noIcon.Add(path); asked.Remove(path); if (icons.Count > 200) icons.Clear(); }
                if (got != null && ready != null) try { ready(); } catch { }
            }) { IsBackground = true };
            try { t.SetApartmentState(System.Threading.ApartmentState.STA); } catch { }
            t.Start();
            return null;
        }

        /// An icon inside the tile (whole, not cropped), on a soft background.
        static bool Contain(Graphics g, Image im, Rectangle box, float radius)
        {
            try
            {
                Theme.FillRound(g, box, radius, Theme.CardInner);
                int pad = Math.Max(2, box.Width / 10);
                float sc = Math.Min((box.Width - pad * 2) / (float)im.Width, (box.Height - pad * 2) / (float)im.Height);
                float w = im.Width * sc, h = im.Height * sc;
                System.Drawing.Drawing2D.InterpolationMode keep = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(im, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
                g.InterpolationMode = keep;
                return true;
            }
            catch { return false; }
        }

        static bool Fill(Graphics g, Image im, Rectangle box, float radius)
        {
            try
            {
                GraphicsState st = g.Save();
                using (GraphicsPath gp = Theme.Round(box, radius))
                {
                    g.SetClip(gp, CombineMode.Intersect);
                    float sc = Math.Max(box.Width / (float)im.Width, box.Height / (float)im.Height);
                    float w = im.Width * sc, h = im.Height * sc;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(im, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
                }
                g.Restore(st);
                return true;
            }
            catch { return false; }
        }

        /// The small JPEG that goes to the receiver (about 10 KB), from a file's thumbnail; null when it has none yet.
        public static byte[] Small(string path)
        {
            try
            {
                if (path == null) return null;
                bool dir = Directory.Exists(path);
                Image im = dir ? null : Thumbs.Get(path, null);
                if (im == null)
                {
                    // no thumbnail of its own: Windows' picture of it (a folder, a program), as a small PNG so its
                    // see-through parts survive
                    Image ic = dir || Thumbs.None(path) ? IconFor(path, null) : null;
                    if (ic == null) return null;
                    foreach (int px in new[] { 128, 96, 64 })
                    {
                        float ki = Math.Min(1f, px / (float)Math.Max(ic.Width, ic.Height));
                        using (Bitmap b2 = new Bitmap(Math.Max(1, (int)(ic.Width * ki)), Math.Max(1, (int)(ic.Height * ki)), PixelFormat.Format32bppArgb))
                        {
                            using (Graphics g2 = Graphics.FromImage(b2)) { g2.Clear(Color.Transparent); g2.InterpolationMode = InterpolationMode.HighQualityBicubic; g2.DrawImage(ic, 0, 0, b2.Width, b2.Height); }
                            using (MemoryStream ms2 = new MemoryStream())
                            {
                                b2.Save(ms2, ImageFormat.Png);
                                if (ms2.Length > 100 && ms2.Length < 60000) return ms2.ToArray();
                            }
                        }
                    }
                    return null;
                }
                int side = 200;
                float k = Math.Min(1f, side / (float)Math.Max(im.Width, im.Height));
                using (Bitmap b = new Bitmap(Math.Max(1, (int)(im.Width * k)), Math.Max(1, (int)(im.Height * k)), PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(im, 0, 0, b.Width, b.Height); }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        ImageCodecInfo jpeg = null;
                        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpeg = c;
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
                            if (jpeg != null) b.Save(ms, jpeg, ep); else b.Save(ms, ImageFormat.Jpeg);
                        }
                        return ms.Length > 100 && ms.Length < 60000 ? ms.ToArray() : null;
                    }
                }
            }
            catch { return null; }
        }

        /// A picture from the bytes the sender sent (never trusted to be more than a small picture).
        public static Image FromBytes(byte[] b)
        {
            try
            {
                if (b == null || b.Length < 100 || b.Length > 200000) return null;
                using (MemoryStream ms = new MemoryStream(b))
                using (Image im = Image.FromStream(ms))
                {
                    if (im.Width > 2000 || im.Height > 2000) return null;
                    // an icon keeps its see-through parts (it's drawn whole, inside the tile); a photo is solid (it fills the tile)
                    bool alpha = Image.IsAlphaPixelFormat(im.PixelFormat);
                    Bitmap copy = new Bitmap(im.Width, im.Height, alpha ? PixelFormat.Format32bppPArgb : PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(copy)) { if (!alpha) g.Clear(Color.White); g.DrawImage(im, 0, 0, im.Width, im.Height); }
                    return copy;
                }
            }
            catch { return null; }
        }
    }
}
