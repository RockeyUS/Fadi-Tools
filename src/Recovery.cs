using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FileConverter
{
    /// Tiny JSON writer + readers for the parser's objects (projects and autosaves).
    public static class Js
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        public static string Q(string s)
        {
            if (s == null) return "null";
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break;
                }
            }
            return b.Append('"').ToString();
        }
        public static string N(double d) { return double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("0.####", CI); }
        public static string B(bool v) { return v ? "true" : "false"; }

        public static object Get(object o, string key)
        {
            JsonObject j = o as JsonObject;
            if (j == null) return null;
            foreach (KeyValuePair<string, object> kv in j) if (kv.Key == key) return kv.Value;
            return null;
        }
        public static string S(object o, string key, string def = null) { object v = Get(o, key); return v as string ?? def; }
        public static double D(object o, string key, double def = 0)
        {
            object v = Get(o, key);
            if (v is long) return (long)v;
            if (v is double) return (double)v;
            return def;
        }
        public static int I(object o, string key, int def = 0) { return (int)Math.Round(D(o, key, def)); }
        public static bool Bo(object o, string key, bool def = false) { object v = Get(o, key); return v is bool ? (bool)v : def; }
        public static List<object> A(object o, string key) { return Get(o, key) as List<object> ?? new List<object>(); }

        // text layers (shared by the photo and the video editor)
        public static string Layer(TextLayer l)
        {
            return "{\"text\":" + Q(l.Text) + ",\"font\":" + Q(l.FontName) + ",\"size\":" + N(l.SizePx) + ",\"color\":" + l.Color.ToArgb() +
                   ",\"bold\":" + B(l.Bold) + ",\"italic\":" + B(l.Italic) + ",\"outline\":" + B(l.Outline) + ",\"shadow\":" + B(l.Shadow) + ",\"box\":" + B(l.Box) +
                   ",\"boxColor\":" + (l.BoxColor.IsEmpty ? "null" : l.BoxColor.ToArgb().ToString()) + ",\"opacity\":" + N(l.Opacity) + ",\"align\":" + (int)l.Align +
                   ",\"x\":" + N(l.Pos.X) + ",\"y\":" + N(l.Pos.Y) + ",\"from\":" + N(l.From) + ",\"to\":" + N(l.To) +
                   ",\"angle\":" + N(l.Angle) + ",\"outlineColor\":" + (l.OutlineColor.IsEmpty ? "null" : l.OutlineColor.ToArgb().ToString()) + ",\"outlineWidth\":" + N(l.OutlineWidth) +
                   (l.IsPicture ? ",\"picture\":" + Q(l.PicturePath ?? "") + (Math.Abs(l.Stretch - 1) > 0.001f ? ",\"stretch\":" + N(l.Stretch) : "") : "") + "}";
        }
        public static TextLayer Layer(object o)
        {
            TextLayer l = new TextLayer
            {
                Text = S(o, "text", ""), FontName = S(o, "font", "Segoe UI"), SizePx = (float)D(o, "size", 64), Color = Color.FromArgb((int)D(o, "color", -1)),
                Bold = Bo(o, "bold", true), Italic = Bo(o, "italic"), Outline = Bo(o, "outline", true), Shadow = Bo(o, "shadow", true), Box = Bo(o, "box"),
                Opacity = (float)D(o, "opacity", 1), Align = (StringAlignment)I(o, "align", 1), Pos = new PointF((float)D(o, "x"), (float)D(o, "y")),
                From = D(o, "from", 0), To = D(o, "to", -1)
            };
            object bc = Get(o, "boxColor");
            l.BoxColor = bc is long ? Color.FromArgb((int)(long)bc) : Color.Empty;
            object oc = Get(o, "outlineColor");
            l.OutlineColor = oc is long ? Color.FromArgb((int)(long)oc) : Color.Empty;
            l.Angle = (float)D(o, "angle", 0);
            l.Stretch = (float)D(o, "stretch", 1);
            l.OutlineWidth = (float)D(o, "outlineWidth", 1);
            string pic = S(o, "picture");
            if (pic != null)
            {
                // a photo on top: it comes back if the file is still there, otherwise it's left out
                if (!System.IO.File.Exists(pic)) return null;
                try { l.Picture = TextLayer.LoadPicture(pic); l.PicturePath = pic; } catch { return null; }
            }
            return l;
        }
    }

    /// Unsaved Studio work is written here every minute, so a crash or power cut doesn't lose it.
    public static class Recovery
    {
        public static string Dir { get { return Path.Combine(Paths.AppDir, "autosave"); } }
        public static string VideoFile { get { return Path.Combine(Dir, "video.fadiproj"); } }
        public static string PhotoFile { get { return Path.Combine(Dir, "photo.json"); } }
        /// Pages hook in here so a crash can save one last time.
        public static event Action SaveNowRequested;

        public static void Write(string file, string json)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = file + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch { }
        }
        public static void Clear(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
        public static void SaveNow() { try { if (SaveNowRequested != null) SaveNowRequested(); } catch { } }
        public static bool Has(string file) { try { return File.Exists(file) && new FileInfo(file).Length > 10; } catch { return false; } }
    }

    /// "Something went wrong": what happened, that the work was kept, and the details to copy.
    public static class Crash
    {
        static bool showing;
        public static string LogDir { get { return Path.Combine(Paths.AppDir, "logs"); } }

        public static void Report(Exception ex, bool fatal)
        {
            if (showing) return;
            showing = true;
            try
            {
                Recovery.SaveNow();
                string details = Details(ex);
                string log = null;
                try
                {
                    Directory.CreateDirectory(LogDir);
                    log = Path.Combine(LogDir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                    File.WriteAllText(log, details, Encoding.UTF8);
                    // keep the 20 newest
                    List<string> all = new List<string>(Directory.GetFiles(LogDir, "crash-*.txt"));
                    all.Sort(StringComparer.Ordinal);
                    for (int i = 0; i < all.Count - 20; i++) try { File.Delete(all[i]); } catch { }
                }
                catch { }
                bool kept = Recovery.Has(Recovery.VideoFile) || Recovery.Has(Recovery.PhotoFile);
                using (CrashForm f = new CrashForm(ex != null ? ex.Message : "?", details, kept, fatal, log))
                {
                    f.StartPosition = FormStartPosition.CenterScreen;
                    f.ShowDialog();
                }
            }
            catch { }
            finally { showing = false; }
        }

        public static string Details(Exception ex)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("Fadi Tools " + Shell.Version);
            b.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            b.AppendLine(Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " (64-bit)" : "") + "  ·  .NET " + Environment.Version);
            b.AppendLine();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                b.AppendLine(e.GetType().FullName + ": " + e.Message);
                b.AppendLine(e.StackTrace);
                b.AppendLine();
            }
            return b.ToString();
        }
    }

    class CrashForm : Form
    {
        readonly string message; readonly bool kept, fatal;
        readonly Btn bCopy, bClose, bLog;
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public CrashForm(string message, string details, bool kept, bool fatal, string logFile)
        {
            this.message = message; this.kept = kept; this.fatal = fatal;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Font = Theme.F(10f);
            BackColor = Theme.Card;
            Text = L.T("crash_title");
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            int W = Theme.S(560), H = Theme.S(kept ? 306 : 256), pad = Theme.S(26);
            ClientSize = new Size(W, H);
            bClose = new Btn(() => L.T(fatal ? "crash_close_app" : "crash_keep"), null, BtnKind.Primary);
            bClose.Click += (s, e) => Close();
            bCopy = new Btn(() => L.T("crash_copy"), Ico.Copy, BtnKind.Secondary);
            bCopy.Click += (s, e) => { try { Clipboard.SetText(details); bCopy.Label = () => L.T("ocr_copied"); bCopy.Glyph = Ico.Check; bCopy.Invalidate(); } catch { } };
            bLog = new Btn(() => L.T("crash_logs"), Ico.FolderOpen, BtnKind.Ghost);
            bLog.Click += (s, e) => { try { if (logFile != null) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + logFile + "\""); } catch { } };
            bLog.Visible = logFile != null;
            int by = H - pad - Theme.S(40);
            int cw = Math.Max(Theme.S(130), bClose.PreferredWidth + Theme.S(20)), pw = Math.Max(Theme.S(150), bCopy.PreferredWidth + Theme.S(20)), lw = Math.Max(Theme.S(110), bLog.PreferredWidth + Theme.S(10));
            bClose.Bounds = Theme.Mirror(new Rectangle(W - pad - cw, by, cw, Theme.S(40)), W);
            bCopy.Bounds = Theme.Mirror(new Rectangle(W - pad - cw - Theme.S(10) - pw, by, pw, Theme.S(40)), W);
            bLog.Bounds = Theme.Mirror(new Rectangle(pad - Theme.S(8), by, lw, Theme.S(40)), W);
            if (pad - Theme.S(8) + lw > W - pad - cw - Theme.S(10) - pw - Theme.S(6)) bLog.Visible = false;   // no room: the details button is enough
            foreach (Btn b in new[] { bClose, bCopy, bLog }) { b.Surface = Surface.Card; Controls.Add(b); }
        }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k) { if (k == Keys.Escape) { Close(); return true; } return base.ProcessCmdKey(ref msg, k); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            using (Pen p = new Pen(Theme.CardBorder)) using (GraphicsPath gp = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9))) g.DrawPath(p, gp);
            int pad = Theme.S(26), tx = pad + Theme.S(58), tw = Width - tx - pad;
            Rectangle ic = Theme.Mirror(new Rectangle(pad, pad, Theme.S(44), Theme.S(44)), Width);
            using (SolidBrush b = new SolidBrush(Theme.WarnBg)) g.FillEllipse(b, ic);
            Theme.Glyph(g, Ico.Warning, 14f, Theme.Warn, ic);
            Theme.Text(g, L.T("crash_title"), Theme.Serif(15f), Theme.Ink, Theme.Mirror(new Rectangle(tx, pad - Theme.S(2), tw, Theme.S(30)), Width), Align.Start, false);
            Theme.Text(g, L.T(fatal ? "crash_fatal" : "crash_body"), Theme.F(9.75f), Theme.InkSoft, Theme.Mirror(new Rectangle(tx, pad + Theme.S(32), tw, Theme.S(44)), Width), Align.Start, false, true);
            int y = pad + Theme.S(82);
            Rectangle mr = Theme.Mirror(new Rectangle(tx, y, tw, Theme.S(48)), Width);
            Theme.FillRound(g, mr, Theme.S(8), Theme.CardInner);
            Theme.Text(g, message, Theme.F(8.75f), Theme.InkSoft, Rectangle.Inflate(mr, -Theme.S(10), -Theme.S(6)), Align.Start, true, true, true);
            if (kept)
            {
                Theme.Glyph(g, Ico.Check, 8.5f, Theme.Success, Theme.Mirror(new Rectangle(tx, y + Theme.S(58), Theme.S(20), Theme.S(22)), Width));
                Theme.Text(g, L.T("crash_kept"), Theme.F(9f, FontStyle.Bold), Theme.Success, Theme.Mirror(new Rectangle(tx + Theme.S(24), y + Theme.S(59), tw - Theme.S(24), Theme.S(40)), Width), Align.Start, false, true);
            }
        }
    }
}
