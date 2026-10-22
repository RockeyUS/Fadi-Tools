using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// One slice of the Donut Wheel: a format to convert to (outer ring) or a quick action (inner ring).
    public class WheelItem
    {
        public string Target;    // a format ("png", "mp3"…), or null for an action
        public string Action;    // compress, nobg, edit, text, info
        public string Glyph;
        public Func<string> Label;
        public bool IsFormat { get { return Target != null; } }
    }

    /// What the Donut Wheel offers for each kind of file (Settings → Donut Wheel): the formats of the outer ring and
    /// the actions of the middle ring, in their order. Kept in the settings as "Wheel.image=png,jpg,…".
    public static class WheelConfig
    {
        public static readonly string[] Kinds = { "image", "video", "audio", "pdf", "doc", "data" };
        public static readonly string[] KindKeys = { "wk_image", "wk_video", "wk_audio", "wk_pdf", "wk_doc", "wk_data" };
        public static readonly string[] KindGlyphs = { Ico.Photo, Ico.Video, Ico.Music, Ico.Page, Ico.Doc, Ico.Code };
        public static readonly string[] Samples = { "photo.jpg", "clip.mp4", "song.mp3", "report.pdf", "letter.docx", "table.csv" };
        public const int MaxFormats = 8, MaxActions = 6;

        /// The wheel's size, in % of 360 px (Settings → Donut Wheel → Size).
        public const int MinSize = 55, MaxSize = 130, DefaultSize = 80;
        public static int SizePct
        {
            get { int v; return int.TryParse(Settings.Get("WheelSize", DefaultSize.ToString()), out v) ? Math.Max(MinSize, Math.Min(MaxSize, v)) : DefaultSize; }
            set { Settings.Set("WheelSize", Math.Max(MinSize, Math.Min(MaxSize, value)).ToString()); }
        }
        /// The wheel's width on the screen, in pixels.
        public static int WheelPx { get { return (int)Math.Round(360f * Theme.Scale * SizePct / 100f); } }

        /// The kind of file an extension is (null when the wheel has nothing for it).
        public static string KindOf(string ext)
        {
            if (ext == "pdf") return "pdf";
            switch (Formats.CategoryOf(ext))
            {
                case Category.Image: return "image";
                case Category.Video: return "video";
                case Category.Audio: return "audio";
                case Category.Document: return "doc";
                case Category.Data: return "data";
            }
            return null;
        }

        /// Every format Fadi Tools can turn this kind of file into (what can be put on the wheel).
        public static string[] Pool(string kind)
        {
            switch (kind)
            {
                case "image": return new[] { "png", "jpg", "webp", "avif", "heic", "gif", "pdf", "ico", "bmp", "tiff", "jpeg" };
                case "video": return new[] { "mp4", "mov", "mkv", "webm", "avi", "wmv", "m4v", "gif", "mp3", "m4a", "wav", "jpg", "png" };
                case "audio": return new[] { "mp3", "wav", "m4a", "flac", "ogg", "opus", "aac", "wma", "aiff", "ac3" };
                case "pdf": return new[] { "docx", "pptx", "txt", "png", "jpg", "html", "md", "odt", "rtf", "epub", "odp" };
                case "doc": return new[] { "pdf", "docx", "txt", "html", "md", "odt", "rtf", "epub", "pptx", "odp", "png", "jpg" };
                case "data": return new[] { "xlsx", "csv", "json", "xml", "tsv", "ods", "html", "md", "pdf" };
            }
            return new string[0];
        }

        /// The actions that work for this kind of file.
        public static string[] ActionPool(string kind)
        {
            switch (kind)
            {
                case "image": return new[] { "compress", "crop", "nobg", "edit", "text", "info" };
                case "video": return new[] { "compress", "edit", "info" };
                case "audio": return new[] { "compress", "info" };
                case "pdf": return new[] { "compress", "edit", "text", "info" };
                case "doc": return new[] { "compress", "edit", "info" };
                default: return new[] { "info" };
            }
        }

        static string DefaultFormats(string kind)
        {
            switch (kind)
            {
                case "image": return "png,jpg,webp,avif,heic,pdf,ico,gif";
                case "video": return "mp4,mov,mkv,webm,gif,mp3";
                case "audio": return "mp3,wav,m4a,flac,ogg,opus";
                case "pdf": return "docx,pptx,txt,png,html";
                case "doc": return "pdf,docx,txt,html,md,odt";
                case "data": return "xlsx,csv,json,xml,tsv,ods";
            }
            return "";
        }

        static List<string> Read(string key, string def, string[] pool, int max)
        {
            List<string> l = new List<string>();
            foreach (string x in Settings.Get(key, def).Split(','))
            {
                string t = x.Trim().ToLowerInvariant();
                if (t.Length > 0 && Array.IndexOf(pool, t) >= 0 && !l.Contains(t) && l.Count < max) l.Add(t);
            }
            return l;
        }

        public static List<string> FormatsFor(string kind) { return Read("Wheel." + kind, DefaultFormats(kind), Pool(kind), MaxFormats); }
        public static List<string> ActionsFor(string kind)
        {
            // 1.6.2 added Crop for pictures: a wheel set up before then gets it once (after Compress), if there's room
            if (kind == "image" && Settings.Get("WheelCropAdded", "0") != "1")
            {
                Settings.Set("WheelCropAdded", "1");
                string saved = Settings.Get("WheelAct.image", "");
                if (saved.Length > 0 && saved != "-" && Array.IndexOf(saved.Split(','), "crop") < 0)
                {
                    List<string> l = Read("WheelAct.image", "", ActionPool(kind), MaxActions);
                    if (l.Count < MaxActions) { int at = l.IndexOf("compress"); l.Insert(at >= 0 ? at + 1 : 0, "crop"); Settings.Set("WheelAct.image", string.Join(",", l.ToArray())); }
                }
            }
            return Read("WheelAct." + kind, string.Join(",", ActionPool(kind)), ActionPool(kind), MaxActions);
        }

        public static void Save(string kind, List<string> formats, List<string> actions)
        {
            // an empty list is kept as "-" (so "nothing" isn't read back as "the defaults")
            Settings.Set("Wheel." + kind, formats.Count > 0 ? string.Join(",", formats.ToArray()) : "-");
            Settings.Set("WheelAct." + kind, actions.Count > 0 ? string.Join(",", actions.ToArray()) : "-");
        }

        public static void Reset(string kind)
        {
            Settings.Set("Wheel." + kind, DefaultFormats(kind));
            Settings.Set("WheelAct." + kind, string.Join(",", ActionPool(kind)));
        }

        public static bool IsDefault(string kind)
        {
            return string.Join(",", FormatsFor(kind).ToArray()) == DefaultFormats(kind) && string.Join(",", ActionsFor(kind).ToArray()) == string.Join(",", ActionPool(kind));
        }

        public static WheelItem Format(string t) { return new WheelItem { Target = t, Label = () => t.ToUpperInvariant() }; }

        public static WheelItem ActionItem(string a)
        {
            switch (a)
            {
                case "compress": return new WheelItem { Action = a, Glyph = "\uE73F", Label = () => L.T("wh_compress") };
                case "crop": return new WheelItem { Action = a, Glyph = Ico.Crop, Label = () => L.T("wh_crop") };
                case "nobg": return new WheelItem { Action = a, Glyph = Ico.Cut, Label = () => L.T("wh_nobg") };
                case "edit": return new WheelItem { Action = a, Glyph = Ico.Edit, Label = () => L.T("wh_edit") };
                case "text": return new WheelItem { Action = a, Glyph = Ico.Text, Label = () => L.T("wh_text") };
                default: return new WheelItem { Action = "info", Glyph = Ico.Info, Label = () => L.T("wh_info") };
            }
        }
    }

    /// The Donut Wheel. Drag + Shift: while a file is dragged from File Explorer with Shift held, the wheel opens under
    /// the mouse. Drop the file on a format to convert it, or on an action in the middle ring; the result is saved next to it.
    /// Nothing listens to the keyboard: a light timer reads the mouse button and Shift state (like a game would),
    /// and the wheel learns what is dragged when the drag enters it (Windows hands it the file list then).
    public static class DragWheel
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        public static bool On { get { return Settings.Get("DragWheel", "1") == "1"; } }
        /// The wheel was switched on or off (from Settings, its own page or the tray panel), so every switch can follow.
        public static event Action Changed;

        /// Turns the wheel on or off, saves it, and tells the other switches.
        public static void Switch(bool on, Form owner)
        {
            if (on == On) return;
            Settings.Set("DragWheel", on ? "1" : "0");
            Apply(owner);
            if (Changed != null) Changed();
        }
        /// Something the switches show changed elsewhere (like "keep running by the clock").
        public static void NotifyChanged() { if (Changed != null) Changed(); }
        /// A file (or several) was dropped on a slice.
        public static event Action<string[], WheelItem> Dropped;

        static Timer poll;
        static bool wasDown, armed, shown;
        static Point downAt;
        static WheelWindow win;
        static readonly int ownPid = Process.GetCurrentProcess().Id;

        /// Starts or stops watching for the gesture (after the setting changes, and when the app starts).
        public static void Apply(Form owner)
        {
            bool want = On && Environment.OSVersion.Platform == PlatformID.Win32NT && !SelfTest.Active;
            if (want && poll == null)
            {
                poll = new Timer { Interval = 30 };
                poll.Tick += (s, e) => { try { Tick(); } catch { } };
                poll.Start();
            }
            else if (!want && poll != null) { poll.Stop(); poll.Dispose(); poll = null; CloseWheel(); }
        }

        static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        static void Tick()
        {
            // the main button (the right one when the buttons are swapped)
            bool down = Down(GetSystemMetrics(23) != 0 ? 0x02 : 0x01);
            Point p = Cursor.Position;
            if (down && !wasDown)
            {
                downAt = p; shown = false;
                armed = StartsOverFileManager(p);
            }
            if (down && armed && !shown && Down(0x10))
            {
                // a real drag: past the distance Windows itself waits for before a drag starts
                int tx = Math.Max(4, GetSystemMetrics(68)), ty = Math.Max(4, GetSystemMetrics(69));
                if (Math.Abs(p.X - downAt.X) > tx || Math.Abs(p.Y - downAt.Y) > ty)
                {
                    shown = true;
                    OpenWheel(p);
                }
            }
            if (!down && wasDown)
            {
                armed = false;
                if (win != null && !win.IsDisposed) win.ButtonReleased();
            }
            wasDown = down;
        }

        /// File Explorer, the desktop, or another file manager (never Fadi Tools' own windows).
        static bool StartsOverFileManager(Point p)
        {
            try
            {
                IntPtr h = WindowFromPoint(p);
                if (h == IntPtr.Zero) return false;
                IntPtr root = GetAncestor(h, 2);
                uint pid; GetWindowThreadProcessId(root != IntPtr.Zero ? root : h, out pid);
                if (pid == 0 || pid == ownPid) return false;
                string name;
                using (Process pr = Process.GetProcessById((int)pid)) name = pr.ProcessName.ToLowerInvariant();
                foreach (string fm in new[] { "explorer", "files", "totalcmd", "totalcmd64", "dopus", "onecommander", "xyplorer", "xyplorer64", "q-dir", "freecommander", "multicommander" })
                    if (name == fm) return true;
                return false;
            }
            catch { return false; }
        }

        static void OpenWheel(Point at)
        {
            CloseWheel();
            try
            {
                win = new WheelWindow(at);
                win.Picked += (files, item) => { if (Dropped != null) Dropped(files, item); };
                win.Open();
            }
            catch { win = null; }
        }

        static void CloseWheel()
        {
            if (win != null && !win.IsDisposed) win.Close();
            win = null;
        }

        // ------------------------------------------------------------------ what the wheel offers for a file

        /// The formats (outer ring) and actions (inner ring) for the dragged files (Settings → Donut Wheel); empty when nothing fits.
        public static void ItemsFor(string[] files, out List<WheelItem> formats, out List<WheelItem> actions)
        {
            formats = new List<WheelItem>(); actions = new List<WheelItem>();
            if (files == null || files.Length == 0) return;
            string ext = Formats.Ext(files[0]);
            string kind = WheelConfig.KindOf(ext);
            if (kind == null) return;
            foreach (string t in WheelConfig.FormatsFor(kind))
                if (!Same(ext, t) && Possible(kind, ext, t)) formats.Add(WheelConfig.Format(t));
            foreach (string a in WheelConfig.ActionsFor(kind))
            {
                // Office files compress only as Word, Excel and PowerPoint documents
                if (a == "compress" && kind == "doc" && ext != "docx" && ext != "pptx" && ext != "xlsx") continue;
                actions.Add(WheelConfig.ActionItem(a));
            }
        }

        /// The few pairs the converter can't make (slides from plain text, Markdown from a spreadsheet).
        static bool Possible(string kind, string ext, string t)
        {
            bool textLike = ext == "txt" || ext == "log" || ext == "ini" || ext == "cfg" || ext == "html" || ext == "htm" || Formats.IsMarkdown(ext);
            if (kind == "doc" && textLike && (t == "pptx" || t == "odp")) return false;
            bool sheet = ext == "xlsx" || ext == "xls" || ext == "ods" || ext == "numbers";
            if (kind == "data" && sheet && t == "md") return false;
            return true;
        }

        static bool Same(string ext, string t)
        {
            if (ext == t) return true;
            string[][] eq = { new[] { "jpg", "jpeg", "jfif" }, new[] { "tif", "tiff" }, new[] { "heic", "heif" }, new[] { "htm", "html" }, new[] { "md", "markdown", "mdown" } };
            foreach (string[] g in eq) if (Array.IndexOf(g, ext) >= 0 && Array.IndexOf(g, t) >= 0) return true;
            return false;
        }

        /// Where results go: next to the file, or Downloads when that folder can't be written to.
        public static string OutDirFor(string file)
        {
            string dir = Path.GetDirectoryName(file);
            try
            {
                string probe = Path.Combine(dir, ".fadi-write-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, ""); File.Delete(probe);
                return dir;
            }
            catch { return Settings.OutputDir; }
        }
    }

    /// Draws the Donut Wheel (the floating one and the preview in Settings): rings of slices, the middle with what is
    /// pointed at, and the logo while it waits for the files. Works in a box D×D, its centre in the middle.
    public sealed class WheelArt
    {
        public readonly int D;
        public readonly float R, RMid, RHub;   // outer edge, between the rings, the middle
        public List<WheelItem> Outer = new List<WheelItem>(), Inner = new List<WheelItem>();
        public float[] HotO = new float[0], HotI = new float[0];
        public int HotOuter = -1, HotInner = -1;
        public WheelItem Picked;
        /// The middle when nothing is pointed at: the file's name (or "3 files") over a hint.
        public string Subject = "", Hint = "";
        /// Settings preview: a slice being dragged to a new place (drawn lifted), and where it would go.
        public int LiftOuter = -1, LiftInner = -1;

        /// How big it is next to the usual wheel (360 px at 100 %): everything inside grows and shrinks with it.
        public readonly float K;
        float S(float v) { return v * Theme.Scale * K; }

        public WheelArt(int d)
        {
            D = d;
            K = Math.Max(0.4f, d / (360f * Theme.Scale));
            R = D / 2f - S(14); RMid = R * 0.64f; RHub = R * 0.30f;
        }

        public void SetItems(List<WheelItem> outer, List<WheelItem> inner)
        {
            Outer = outer; Inner = inner;
            HotO = new float[outer.Count]; HotI = new float[inner.Count];
            HotOuter = HotInner = -1;
        }

        /// Eases the highlights towards the slice under the mouse; true while something still moves.
        public bool Step()
        {
            bool more = false;
            for (int i = 0; i < HotO.Length; i++) more |= Fx.Ease(ref HotO[i], i == HotOuter || i == LiftOuter ? 1 : 0, 0.35f);
            for (int i = 0; i < HotI.Length; i++) more |= Fx.Ease(ref HotI[i], i == HotInner || i == LiftInner ? 1 : 0, 0.35f);
            return more;
        }

        /// Which slice is at a point of the D×D box (-1 = none; the middle is never a slice).
        public void HitTest(PointF p, out int o, out int i)
        {
            o = -1; i = -1;
            float dx = p.X - D / 2f, dy = p.Y - D / 2f, r = (float)Math.Sqrt(dx * dx + dy * dy);
            if (r < RHub || r > R + S(10)) return;
            double a = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (r >= RMid) { if (Outer.Count > 0) o = Index(a, Outer.Count); }
            else if (Inner.Count > 0) i = Index(a, Inner.Count);
        }

        /// Slice under an angle; slice 0 is centred at the top, the rest follow clockwise.
        static int Index(double a, int n)
        {
            double span = 360.0 / n;
            double rel = a - (-90 - span / 2);
            rel = ((rel % 360) + 360) % 360;
            return Math.Min(n - 1, (int)(rel / span));
        }

        public static float EaseOutBack(float t) { float c1 = 1.4f, c3 = c1 + 1; return 1 + c3 * (float)Math.Pow(t - 1, 3) + c1 * (float)Math.Pow(t - 1, 2); }

        /// tFull: 0 = the small waiting ring with the logo, 1 = the whole wheel. clear: start from a see-through canvas.
        public void Draw(Graphics g, float scale, float tFull, bool hasItems, bool clear)
        {
            if (clear) g.Clear(Color.Transparent);
            GraphicsState st0 = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.TranslateTransform(D / 2f, D / 2f);
            g.ScaleTransform(scale, scale);
            float grow = 0.42f + 0.58f * EaseOutBack(tFull);
            float r = R * grow;

            // soft shadow
            for (int i = 8; i >= 1; i--)
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(Theme.Dark ? 14 : 7, 0, 0, 0)))
                    g.FillEllipse(sb, -r - i, -r - i + S(4), (r + i) * 2, (r + i) * 2);
            Color disc = Theme.Dark ? Color.FromArgb(246, 30, 29, 27) : Color.FromArgb(246, 252, 250, 246);
            using (SolidBrush b = new SolidBrush(disc)) g.FillEllipse(b, -r, -r, r * 2, r * 2);
            using (Pen p = new Pen(Theme.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 60, 40, 20), 1f)) g.DrawEllipse(p, -r, -r, r * 2, r * 2);

            if (tFull > 0.05f && hasItems)
            {
                float rm = RMid * grow, rh = RHub * grow;
                float gapPx = S(3);
                DrawRing(g, Outer, HotO, rm + gapPx, r - S(8), true, tFull);
                DrawRing(g, Inner, HotI, rh + gapPx, rm - gapPx, false, tFull);
                DrawHub(g, rh, tFull);
            }
            else DrawHint(g, r);
            g.Restore(st0);
        }

        void DrawRing(Graphics g, List<WheelItem> items, float[] hot, float r0, float r1, bool formats, float t)
        {
            int n = items.Count;
            if (n == 0)
            {
                // an empty ring: a dashed outline, so it's clear there's room for more
                using (Pen p = new Pen(Color.FromArgb((int)(90 * t), Theme.InkFaint), 1.2f) { DashPattern = new float[] { 3f, 3f } })
                {
                    float rr = (r0 + r1) / 2;
                    g.DrawEllipse(p, -rr, -rr, rr * 2, rr * 2);
                }
                return;
            }
            float span = 360f / n, gapDeg = n > 1 ? Math.Min(4f, span * 0.08f) : 0;
            for (int i = 0; i < n; i++)
            {
                float a0 = -90 - span / 2 + i * span + gapDeg / 2, sweep = span - gapDeg;
                float h = i < hot.Length ? hot[i] : 0;
                bool picking = Picked == items[i];
                bool lifted = formats ? i == LiftOuter : i == LiftInner;
                using (GraphicsPath p = Wedge(r0, r1 + h * S(4), a0, n == 1 ? 359.9f : sweep))
                {
                    Color baseC = Theme.Dark ? Color.FromArgb(255, 44, 42, 39) : Color.FromArgb(255, 242, 236, 228);
                    Color fill = Theme.Mix(baseC, Theme.AccentColor, Math.Max(h, picking ? 1 : 0));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(255 * t * (lifted ? 0.55f : 1)), fill))) g.FillPath(b, p);
                }
                double mid = (a0 + sweep / 2) * Math.PI / 180;
                float rc = (r0 + r1) / 2;
                PointF c = new PointF((float)(Math.Cos(mid) * rc), (float)(Math.Sin(mid) * rc));
                Color ink = Theme.Mix(Theme.Dark ? Color.FromArgb(235, 232, 226) : Color.FromArgb(40, 36, 32), Color.White, h);
                ink = Color.FromArgb((int)(255 * t), ink);
                // the room a label has across: the arc at the top and bottom of the wheel, the ring's depth at its sides
                float arc = (float)(rc * Math.Min(sweep, 170) * Math.PI / 180), depth = r1 - r0;
                float room = (float)(arc * Math.Abs(Math.Sin(mid)) + depth * Math.Abs(Math.Cos(mid))) - S(12);
                if (formats) DrawLabel(g, items[i].Label(), S(12.5f), FontStyle.Bold, ink, c, room);
                else
                {
                    float gs = S(22);
                    Rectangle gr = Rectangle.Round(new RectangleF(c.X - gs / 2, c.Y - gs / 2 - S(7), gs, gs));
                    VectorIcons.Draw(g, items[i].Glyph, 11f, ink, gr);
                    DrawLabel(g, items[i].Label(), S(8.5f), FontStyle.Regular, ink, new PointF(c.X, c.Y + S(11)), room);
                }
            }
        }

        void DrawHub(Graphics g, float rh, float t)
        {
            WheelItem hi = HotOuter >= 0 && HotOuter < Outer.Count ? Outer[HotOuter] : HotInner >= 0 && HotInner < Inner.Count ? Inner[HotInner] : null;
            Color hubC = Theme.Dark ? Color.FromArgb(255, 36, 35, 33) : Color.White;
            using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(255 * t), hubC))) g.FillEllipse(b, -rh, -rh, rh * 2, rh * 2);
            Color ink = Color.FromArgb((int)(255 * t), Theme.Dark ? Color.FromArgb(235, 232, 226) : Color.FromArgb(40, 36, 32));
            Color soft = Color.FromArgb((int)(200 * t), Theme.Dark ? Color.FromArgb(170, 165, 158) : Color.FromArgb(120, 112, 104));
            string top, bottom;
            if (Outer.Count == 0 && Inner.Count == 0) { top = L.T("wh_none"); bottom = ""; }
            else if (hi != null && hi.IsFormat) { top = L.T("wh_to"); bottom = hi.Label(); }
            else if (hi != null) { top = hi.Label(); bottom = ""; }
            else { top = Short(Subject, 18); bottom = Hint; }
            if (bottom.Length > 0)
            {
                DrawLabel(g, top, S(8f), FontStyle.Regular, soft, new PointF(0, -S(9)), rh * 1.7f);
                DrawLabel(g, bottom, S(hi != null ? 13f : 8.5f), hi != null ? FontStyle.Bold : FontStyle.Regular, hi != null ? ink : soft, new PointF(0, S(8)), rh * 1.7f);
            }
            else DrawLabel(g, top, S(9.5f), FontStyle.Bold, ink, PointF.Empty, rh * 1.8f);
        }

        void DrawHint(Graphics g, float r)
        {
            Image logo = Theme.Logo;
            float s = r * 0.9f;
            if (logo == null) return;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(logo, new RectangleF(-s / 2, -s / 2, s, s));   // its corners are already rounded (and soft)
        }

        public static string Short(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        static GraphicsPath Wedge(float r0, float r1, float a0, float sweep)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(-r1, -r1, r1 * 2, r1 * 2, a0, sweep);
            p.AddArc(-r0, -r0, r0 * 2, r0 * 2, a0 + sweep, -sweep);
            p.CloseFigure();
            return p;
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// Text drawn as shapes, so it stays smooth on the see-through canvas (Arabic is shaped as usual).
        static void DrawLabel(Graphics g, string text, float px, FontStyle style, Color c, PointF center, float maxW = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            Font f = Theme.F(9f, style);   // shared: not disposed here
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Center;
                if (Theme.RTL) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                GraphicsPath p = new GraphicsPath();
                p.AddString(text, f.FontFamily, (int)style, px, center, sf);
                if (maxW > 0)
                {
                    float w = p.GetBounds().Width;
                    int sp = text.IndexOf(' ');
                    if (w > maxW && sp > 0)
                    {
                        // two words that don't fit: one above the other
                        int mid = text.Length / 2, best = sp;
                        for (int k = 0; k < text.Length; k++) if (text[k] == ' ' && Math.Abs(k - mid) < Math.Abs(best - mid)) best = k;
                        string a = text.Substring(0, best), b2 = text.Substring(best + 1);
                        p.Dispose(); p = new GraphicsPath();
                        p.AddString(a, f.FontFamily, (int)style, px, new PointF(center.X, center.Y - px * 0.55f), sf);
                        p.AddString(b2, f.FontFamily, (int)style, px, new PointF(center.X, center.Y + px * 0.55f), sf);
                        w = p.GetBounds().Width;
                    }
                    if (w > maxW)
                    {
                        // still too wide: a little smaller, around the same middle
                        float k2 = Math.Max(0.6f, maxW / w);
                        using (Matrix m = new Matrix()) { m.Translate(center.X, center.Y); m.Scale(k2, k2); m.Translate(-center.X, -center.Y); p.Transform(m); }
                    }
                }
                using (p)
                using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
            }
        }

    }

    /// The wheel itself: a see-through window that never takes the focus (so Explorer's drag carries on) and
    /// accepts the drop. Drawn on a transparent canvas the graphics card blends, so it pops open smoothly.
    sealed class WheelWindow : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; public PT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; public SZ(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential)]
        struct BMIH { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst, ref SZ psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int dwFlags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BMIH bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        public event Action<string[], WheelItem> Picked;

        readonly int D;
        readonly WheelArt art;
        IntPtr dc, dib, oldBmp;
        Bitmap canvas;
        readonly Timer anim = new Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double openedAt, closingAt = -1, enteredBy = -1, releasedAt = -1;
        string[] files;
        bool entered, dropped;

        public WheelWindow(Point mouse)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            AllowDrop = true;
            D = WheelConfig.WheelPx;
            art = new WheelArt(D) { Hint = L.T("wh_hint") };
            // centred on the mouse, but always whole on the screen (by a corner or an edge it moves in)
            Rectangle wa = Screen.FromPoint(mouse).WorkingArea;
            int m = (int)(art.K * Theme.Scale * 6);   // the shadow may go a little past the edge
            int x = Math.Max(wa.Left - m, Math.Min(wa.Right - D + m, mouse.X - D / 2));
            int y = Math.Max(wa.Top - m, Math.Min(wa.Bottom - D + m, mouse.Y - D / 2));
            Bounds = new Rectangle(x, y, D, D);
            BMIH bi = new BMIH { biSize = 40, biWidth = D, biHeight = -D, biPlanes = 1, biBitCount = 32 };
            dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr bits;
            dib = CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) { DeleteDC(dc); dc = IntPtr.Zero; throw new OutOfMemoryException(); }
            oldBmp = SelectObject(dc, dib);
            canvas = new Bitmap(D, D, D * 4, PixelFormat.Format32bppPArgb, bits);
            anim.Tick += (s, e) => Frame();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8;   // layered, no taskbar button, never takes the focus, on top
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE: never activate
            base.WndProc(ref m);
        }

        double Now { get { return clock.Elapsed.TotalMilliseconds; } }

        public void Open()
        {
            openedAt = Now;
            Render();
            Show();
            anim.Start();
        }

        /// The mouse button came up: the drop (if any) arrives about now; close shortly after if nothing was dropped.
        public void ButtonReleased() { if (releasedAt < 0) releasedAt = Now; }

        void BeginClose() { if (closingAt < 0) closingAt = Now; }

        void Frame()
        {
            if (IsDisposed) return;
            double now = Now;
            // never entered (a Shift-drag that isn't moving files, like a selection box): go away quietly
            if (!entered && closingAt < 0 && now - openedAt > 1400) BeginClose();
            if (releasedAt >= 0 && !dropped && closingAt < 0 && now - releasedAt > 350) BeginClose();
            art.Step();
            Render();
            if (closingAt >= 0 && now - closingAt > (Fx.Motion ? 200 : 0)) { anim.Stop(); Close(); }
        }

        // ------------------------------------------------------------------ drag and drop

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            if (!entered)
            {
                entered = true; enteredBy = Now;
                string[] f = null;
                try { if (e.Data.GetDataPresent(DataFormats.FileDrop)) f = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
                if (f != null) { List<string> ok = new List<string>(); foreach (string p in f) if (File.Exists(p)) ok.Add(p); f = ok.ToArray(); }
                files = f != null && f.Length > 0 ? f : null;
                if (files != null)
                {
                    List<WheelItem> o, i;
                    DragWheel.ItemsFor(files, out o, out i);
                    art.SetItems(o, i);
                    art.Subject = files.Length == 1 ? Path.GetFileName(files[0]) : L.T("n_files", files.Length);
                }
                else BeginClose();   // not files (text, a picture from a browser…): nothing to offer
            }
            Over(e);
        }

        protected override void OnDragOver(DragEventArgs e) { base.OnDragOver(e); Over(e); }

        protected override void OnDragLeave(EventArgs e) { base.OnDragLeave(e); art.HotOuter = art.HotInner = -1; }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            Over(e);
            WheelItem it = art.HotOuter >= 0 ? art.Outer[art.HotOuter] : art.HotInner >= 0 ? art.Inner[art.HotInner] : null;
            dropped = true;
            e.Effect = it != null ? DragDropEffects.Copy : DragDropEffects.None;
            art.Picked = it;
            BeginClose();
            if (it != null && files != null && Picked != null)
            {
                string[] f = files;
                // let Explorer finish its drag first, then start the work
                BeginInvoke((Action)(() => Picked(f, it)));
            }
        }

        void Over(DragEventArgs e)
        {
            Point p = PointToClient(new Point(e.X, e.Y));
            art.HitTest(p, out art.HotOuter, out art.HotInner);
            e.Effect = files != null && (art.HotOuter >= 0 || art.HotInner >= 0) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void Render()
        {
            if (canvas == null || dc == IntPtr.Zero) return;
            double now = Now;
            bool motion = Fx.Motion;
            float tOpen = motion ? (float)Math.Min(1, (now - openedAt) / 220.0) : 1;
            float tFull = entered ? (motion ? (float)Math.Min(1, (now - enteredBy) / 200.0) : 1) : 0;   // grows from the small hint to the whole wheel
            float tClose = closingAt >= 0 ? (motion ? (float)Math.Min(1, (now - closingAt) / 200.0) : 1) : 0;
            float scale = (0.55f + 0.45f * WheelArt.EaseOutBack(tOpen)) * (1 - 0.18f * tClose);
            byte alpha = (byte)Math.Max(0, Math.Min(255, 255 * Math.Min(1, tOpen * 1.6f) * (1 - tClose)));
            using (Graphics g = Graphics.FromImage(canvas)) art.Draw(g, scale, tFull, files != null, true);
            PT dst = new PT(Left, Top), src = new PT(0, 0);
            SZ sz = new SZ(D, D);
            BLEND bl = new BLEND { Op = 0, Flags = 0, Alpha = alpha, Format = 1 };
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref sz, dc, ref src, 0, ref bl, 2);
        }

        protected override void Dispose(bool disposing)
        {
            anim.Stop(); anim.Dispose();
            if (canvas != null) { canvas.Dispose(); canvas = null; }
            if (dc != IntPtr.Zero) { SelectObject(dc, oldBmp); DeleteObject(dib); DeleteDC(dc); dc = IntPtr.Zero; }
            base.Dispose(disposing);
        }
    }
}
