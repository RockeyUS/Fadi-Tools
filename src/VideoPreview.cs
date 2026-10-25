using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Frames of the project's files, fetched in the background (one at a time, newest wish first) and kept for a while.
    public class FrameCache : IFrames
    {
        /// Held while frames are drawn from (GDI+ pictures can't be used by two threads at once).
        public readonly object Gate = new object();
        readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        readonly LinkedList<string> order = new LinkedList<string>();
        readonly Dictionary<string, Bitmap> stills = new Dictionary<string, Bitmap>();
        readonly object lk = new object();
        readonly Stack<KeyValuePair<string, double>> wants = new Stack<KeyValuePair<string, double>>();
        readonly HashSet<string> asked = new HashSet<string>();
        Thread worker;
        public int Height = 360;
        /// Files with see-through parts (cut-outs).
        public readonly HashSet<string> Alpha = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public event Action Arrived;

        static string Key(string file, double t, int h) { return file + "|" + Math.Round(t * 12) + "|" + h; }

        /// A video's frame (or the nearest one already here); asks for the exact one when it isn't.
        public Bitmap Get(string file, double t, out bool exact)
        {
            string k = Key(file, t, Height);
            lock (lk)
            {
                Bitmap b;
                if (cache.TryGetValue(k, out b)) { exact = true; return b; }
                exact = false;
                if (!asked.Contains(k)) { asked.Add(k); wants.Push(new KeyValuePair<string, double>(file, t)); Start(); }
                // something close while waiting: the nearest frame of this file
                Bitmap near = null; double best = 1e9;
                string pre = file + "|";
                foreach (KeyValuePair<string, Bitmap> kv in cache)
                {
                    if (!kv.Key.StartsWith(pre)) continue;
                    string[] p = kv.Key.Substring(pre.Length).Split('|');
                    double d = Math.Abs(double.Parse(p[0], CultureInfo.InvariantCulture) / 12 - t);
                    if (d < best) { best = d; near = kv.Value; }
                }
                return near;
            }
        }

        /// A clip's frame at a moment of its file (cut-outs are decoded with their see-through parts).
        public Bitmap Video(VClip c, double srcT, out bool exact)
        {
            if (c.Alpha) lock (Alpha) Alpha.Add(c.File);
            return Get(c.File, srcT, out exact);
        }

        /// A photo, decoded once (at most 1600 pixels).
        public Bitmap Still(string file)
        {
            lock (lk)
            {
                Bitmap b;
                if (stills.TryGetValue(file, out b)) return b;
            }
            Bitmap nb = null;
            try { using (Image im = ViewerPage.Decode(file, 1600)) nb = Pix.Argb(im); } catch { }
            lock (lk) stills[file] = nb;
            return nb;
        }

        void Start()
        {
            if (worker != null && worker.IsAlive) return;
            worker = new Thread(() =>
            {
                while (true)
                {
                    KeyValuePair<string, double> w;
                    lock (lk)
                    {
                        if (wants.Count == 0) return;
                        w = wants.Pop();
                        // scrubbing fast: only the newest wishes matter
                        if (wants.Count > 8) { foreach (KeyValuePair<string, double> o in wants) asked.Remove(Key(o.Key, o.Value, Height)); wants.Clear(); }
                    }
                    Image im = null;
                    try
                    {
                        bool alpha; lock (Alpha) alpha = Alpha.Contains(w.Key);
                        if (alpha)
                        {
                            // a cut-out video: decoded so its see-through parts stay see-through
                            byte[] png = Tools.FFmpeg == null ? null : Media.Capture(Tools.FFmpeg, "-hide_banner -loglevel error -c:v libvpx-vp9 -ss " + Math.Max(0, w.Value).ToString("0.###", CultureInfo.InvariantCulture) + " -i " + Tools.Quote(w.Key) + " -frames:v 1 -vf \"scale=-2:" + Height + "\" -f image2pipe -vcodec png -", 30000);
                            if (png != null) im = Image.FromStream(new MemoryStream(png));
                        }
                        if (im == null) im = Media.Frame(w.Key, w.Value, Height);
                    }
                    catch { }
                    Bitmap b = null;
                    if (im != null) { try { b = Pix.Argb(im); } catch { } im.Dispose(); }
                    string k = Key(w.Key, w.Value, Height);
                    lock (Gate) lock (lk)
                    {
                        asked.Remove(k);
                        if (b != null)
                        {
                            cache[k] = b; order.AddLast(k);
                            while (order.Count > 160) { string old = order.First.Value; order.RemoveFirst(); Bitmap ob; if (cache.TryGetValue(old, out ob)) { cache.Remove(old); ob.Dispose(); } }
                        }
                    }
                    if (b != null && Arrived != null) Arrived();
                }
            });
            worker.IsBackground = true; worker.Start();
        }

        public void Clear()
        {
            lock (Gate) lock (lk)
            {
                foreach (Bitmap b in cache.Values) b.Dispose();
                foreach (Bitmap b in stills.Values) if (b != null) b.Dispose();
                cache.Clear(); stills.Clear(); order.Clear(); wants.Clear(); asked.Clear();
            }
        }
    }

    /// Draws the project at one moment (for scrubbing and editing): the same layers the export makes, close enough to judge.
    public static class Composer
    {
        public static Bitmap Compose(VProject p, double t, int w, int h, IFrames fc, out bool waiting)
        {
            waiting = false;
            p.Layout();
            Bitmap b = new Bitmap(Math.Max(2, w), Math.Max(2, h), PixelFormat.Format32bppArgb);
            float k = w / (float)p.W;
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(p.Fill == "color" ? p.FillColor : Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                VTrack main = p.Main;
                if (!main.Hidden)
                {
                    // the clip(s) at this moment: two of them during a transition
                    for (int i = 0; i < main.Clips.Count; i++)
                    {
                        VClip c = main.Clips[i];
                        if (t < c.At - 0.0001 || t >= c.End || c.Kind == VKind.Audio) continue;
                        float alpha = 1;
                        double tin = i > 0 ? p.TransLen(i - 1) : 0;
                        if (tin > 0 && t < c.At + tin) alpha = (float)((t - c.At) / tin);
                        bool w2;
                        Bitmap fr = FrameOf(c, t, fc, out w2);
                        waiting |= w2;
                        if (fr == null) continue;
                        float fade = FadeK(c, t);
                        using (Bitmap adj = Adjusted(c, fr))
                            DrawMain(g, p, adj ?? fr, w, h, alpha * fade, i > 0 && tin > 0 && alpha < 1 ? p.Main.Clips[i - 1].Trans : null, alpha);
                    }
                }
                // overlays and texts, from just above the main track up to the top
                for (int ti = p.MainIndex - 1; ti >= 0; ti--)
                {
                    VTrack tr = p.Tracks[ti];
                    if (tr.Hidden) continue;
                    foreach (VClip c in tr.Clips)
                    {
                        if (t < c.At || t >= c.End) continue;
                        if (c.Kind == VKind.Text) DrawText(g, p, c, t, k);
                        else if (c.Kind == VKind.Video || c.Kind == VKind.Image)
                        {
                            bool w2;
                            Bitmap fr = FrameOf(c, t, fc, out w2);
                            waiting |= w2;
                            if (fr != null) using (Bitmap adj = Adjusted(c, fr)) DrawOverlay(g, p, c, adj ?? fr, t, k);
                        }
                    }
                }
                foreach (VTrack tr in p.Tracks)
                {
                    if (tr.Hidden) continue;
                    foreach (VClip c in tr.Clips) if (c.Kind == VKind.Captions && c.Cues != null) DrawCaption(g, c, t, w, h);
                }
            }
            return b;
        }

        static Bitmap FrameOf(VClip c, double t, IFrames fc, out bool waiting)
        {
            waiting = false;
            if (c.Kind == VKind.Image) return fc.Still(c.File);
            double st = c.In + (t - c.At) * c.Speed;
            bool exact;
            Bitmap b = fc.Video(c, Math.Max(0, Math.Min(c.Out - 0.04, st)), out exact);
            waiting = !exact;
            return b;
        }

        static Bitmap Adjusted(VClip c, Bitmap fr)
        {
            if (c.Adj == null || c.Adj.None) return null;
            try { return c.Adj.Apply(fr); } catch { return null; }
        }

        static float FadeK(VClip c, double t)
        {
            float a = 1;
            if (c.FadeIn > 0.01 && t - c.At < c.FadeIn) a *= (float)((t - c.At) / c.FadeIn);
            if (c.FadeOut > 0.01 && c.End - t < c.FadeOut) a *= (float)((c.End - t) / c.FadeOut);
            return Math.Max(0, Math.Min(1, a));
        }

        static void DrawMain(Graphics g, VProject p, Bitmap fr, int w, int h, float alpha, string trans, float progress)
        {
            float fit = Math.Min(w / (float)fr.Width, h / (float)fr.Height);
            RectangleF dst = new RectangleF((w - fr.Width * fit) / 2, (h - fr.Height * fit) / 2, fr.Width * fit, fr.Height * fit);
            if (trans != null && (trans == "slideleft" || trans == "smoothleft")) { dst.X += (1 - progress) * w; alpha = 1; }
            if (p.Fill == "blur" && (dst.Width < w - 1 || dst.Height < h - 1))
            {
                // a blurred, darker copy filling the frame behind it
                float cover = Math.Max(w / (float)fr.Width, h / (float)fr.Height);
                using (Bitmap small = new Bitmap(Math.Max(2, w / 18), Math.Max(2, h / 18)))
                {
                    using (Graphics sg = Graphics.FromImage(small))
                    {
                        sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        float sw = fr.Width * cover / 18, sh = fr.Height * cover / 18;
                        sg.DrawImage(fr, (small.Width - sw) / 2, (small.Height - sh) / 2, sw, sh);
                    }
                    Draw(g, small, new RectangleF(-w * 0.02f, -h * 0.02f, w * 1.04f, h * 1.04f), alpha * 0.92f);
                }
            }
            Draw(g, fr, dst, alpha);
        }

        static void Draw(Graphics g, Image im, RectangleF dst, float alpha)
        {
            if (alpha >= 0.999f) { g.DrawImage(im, dst); return; }
            if (alpha <= 0.001f) return;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                g.DrawImage(im, new[] { dst.Location, new PointF(dst.Right, dst.Y), new PointF(dst.X, dst.Bottom) }, new RectangleF(0, 0, im.Width, im.Height), GraphicsUnit.Pixel, ia);
            }
        }

        /// How an overlay or text is placed now: middle, size factor, turn, see-through (with its animations).
        public static void Placement(VClip c, double t, int W, int H, out PointF mid, out float scale, out float rot, out float alpha)
        {
            mid = new PointF(c.Eval("x", t, c.X) * W, c.Eval("y", t, c.Y) * H);
            scale = c.Eval("scale", t, c.Scale);
            rot = c.Eval("rot", t, c.Rot);
            alpha = c.Eval("opacity", t, c.Opacity);
            double lin = Math.Max(0, Math.Min(1, (t - c.At) / Math.Max(0.05, c.AnimIn != "none" ? c.AnimInDur : Math.Max(0.05, c.FadeIn))));
            double lout = Math.Max(0, Math.Min(1, (c.End - t) / Math.Max(0.05, c.AnimOut != "none" ? c.AnimOutDur : Math.Max(0.05, c.FadeOut))));
            float ein = (float)(1 - (1 - lin) * (1 - lin)), eout = (float)(1 - (1 - lout) * (1 - lout));
            if (c.AnimIn != "none" || c.FadeIn > 0.01) alpha *= (float)lin;
            if (c.AnimOut != "none" || c.FadeOut > 0.01) alpha *= (float)lout;
            if (c.AnimIn == "slideup") mid.Y += H * 0.08f * (1 - ein);
            if (c.AnimOut == "slideup") mid.Y -= H * 0.08f * (1 - eout);
            if (c.AnimIn == "slideleft") mid.X += W * 0.1f * (1 - ein);
            if (c.AnimOut == "slideleft") mid.X -= W * 0.1f * (1 - eout);
            if (c.AnimIn == "pop") scale *= 0.6f + 0.4f * ein;
            if (c.AnimOut == "pop") scale *= 0.6f + 0.4f * eout;
        }

        static void DrawOverlay(Graphics g, VProject p, VClip c, Bitmap fr, double t, float k)
        {
            PointF mid; float scale, rot, alpha;
            Placement(c, t, p.W, p.H, out mid, out scale, out rot, out alpha);
            float ww = scale * p.W * k, hh = ww * fr.Height / Math.Max(1, fr.Width);
            GraphicsState st = g.Save();
            g.TranslateTransform(mid.X * k, mid.Y * k);
            g.RotateTransform(rot);
            Draw(g, fr, new RectangleF(-ww / 2, -hh / 2, ww, hh), alpha);
            g.Restore(st);
        }

        static void DrawText(Graphics g, VProject p, VClip c, double t, float k)
        {
            if (c.Text == null) return;
            PointF mid; float scale, rot, alpha;
            Placement(c, t, p.W, p.H, out mid, out scale, out rot, out alpha);
            if (alpha <= 0.01f) return;
            TextLayer tl = c.Text.CloneStyle();
            if (c.AnimIn == "type")
            {
                double f = Math.Min(1, (t - c.At) / Math.Max(0.1, c.AnimInDur));
                string s = tl.Text ?? "";
                tl.Text = s.Substring(0, (int)Math.Round(s.Length * f));
                tl.Align = StringAlignment.Near;
                alpha = c.Eval("opacity", t, c.Opacity);
            }
            tl.Pos = PointF.Empty; tl.Opacity = alpha; tl.Angle = rot; tl.Touch();
            GraphicsState st = g.Save();
            g.TranslateTransform(mid.X * k, mid.Y * k);
            tl.DrawScreen(g, 0, 0, k * scale);
            g.Restore(st);
        }

        /// The captions' font: the one picked, else Cairo when the Arabic fonts are here (it has Latin letters too).
        public static string CapFontName(VClip c) { return string.IsNullOrEmpty(c.CapFont) ? (FontPack.Ready && FontPack.Find("Cairo") != null ? "Cairo" : "Segoe UI") : c.CapFont; }

        static FontFamily Family(string name)
        {
            FontFamily f = FontPack.Find(name);
            if (f != null) return f;
            try { return new FontFamily(name); } catch { return FontFamily.GenericSansSerif; }
        }

        /// The caption showing now, in its style. Arabic (and other right-to-left words) go from right to left.
        public static void DrawCaption(Graphics g, VClip c, double t, int w, int h)
        {
            Cue q = c.Cues.Find(x => t >= x.Start && t < x.End);
            if (q == null) return;
            string text = c.CapStyle == "bold" ? q.Text.ToUpper(CultureInfo.CurrentCulture) : q.Text;
            float size = Math.Min(w, h) * 0.062f * c.CapSize * (c.CapStyle == "bold" ? 1.25f : 1);
            if (size < 3) return;
            FontFamily ff = Family(CapFontName(c));
            bool bold = c.CapStyle == "bold" || c.CapStyle == "highlight" || c.CapStyle == "outline";
            FontStyle fs = bold ? FontStyle.Bold : FontStyle.Regular;
            if (!ff.IsStyleAvailable(fs)) fs = ff.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
            bool rtl = Cue.IsRtl(text);
            // the words (with when each is said), laid out in lines across most of the width
            Cue up = q.Copy(); up.Text = text;
            List<KeyValuePair<string, double>> words = up.Words();
            int cur = -1;
            for (int i = 0; i < words.Count; i++) if (t >= words[i].Value) cur = i;
            float emPx = size, space = size * 0.3f, maxW = w * 0.86f;
            List<List<int>> lines = new List<List<int>> { new List<int>() };
            List<float> lineW = new List<float> { 0 };
            float[] ww = new float[words.Count];
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                if (rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                for (int i = 0; i < words.Count; i++)
                {
                    using (GraphicsPath gp = new GraphicsPath()) { gp.AddString(words[i].Key, ff, (int)fs, emPx, PointF.Empty, sf); ww[i] = gp.GetBounds().Width + size * 0.05f; }
                    float lw = lineW[lineW.Count - 1];
                    if (lw > 0 && lw + space + ww[i] > maxW) { lines.Add(new List<int>()); lineW.Add(0); lw = 0; }
                    lines[lines.Count - 1].Add(i);
                    lineW[lineW.Count - 1] = lw + (lw > 0 ? space : 0) + ww[i];
                }
                float lh = size * 1.25f, y0 = c.CapY * h - lines.Count * lh / 2;
                for (int li = 0; li < lines.Count; li++)
                {
                    float left = (w - lineW[li]) / 2, y = y0 + li * lh;
                    if (c.CapStyle == "box")
                        Theme.FillRound(g, new RectangleF(left - size * 0.3f, y - size * 0.08f, lineW[li] + size * 0.6f, lh), size * 0.18f, Color.FromArgb(190, 0, 0, 0));
                    // left to right, or (Arabic) from the right edge of the line to the left
                    float x = rtl ? left + lineW[li] : left;
                    foreach (int i in lines[li])
                    {
                        float wx = rtl ? x - ww[i] : x;
                        bool hi = (c.CapStyle == "highlight" || c.CapStyle == "bold") && i == cur;
                        using (GraphicsPath gp = new GraphicsPath())
                        {
                            gp.AddString(words[i].Key, ff, (int)fs, emPx * (hi && c.CapStyle == "bold" ? 1.1f : 1), PointF.Empty, sf);
                            RectangleF bb = gp.GetBounds();
                            using (Matrix m = new Matrix()) { m.Translate(wx - bb.X, y); gp.Transform(m); }
                            if (c.CapStyle != "box")
                                using (Pen o = new Pen(Color.Black, Math.Max(1.5f, size / (c.CapStyle == "bold" ? 3.5f : 6f))) { LineJoin = LineJoin.Round }) g.DrawPath(o, gp);
                            using (SolidBrush b = new SolidBrush(hi ? c.CapHigh : c.CapColor)) g.FillPath(b, gp);
                        }
                        x = rtl ? x - ww[i] - space : x + ww[i] + space;
                    }
                }
            }
        }
    }

    /// The preview: the project's picture (letterboxed in the dark), and handles to move and size the selected
    /// overlay or text right on it.
    public class VideoPreview : UiControl
    {
        public VProject P;
        public Bitmap Frame;            // the picture now (owned here)
        public VClip Selected;
        public double Time;
        public bool Playing;
        public string Note;             // "Getting the preview ready… 40%"
        public Func<string, bool> BeforeChange;
        public event Action Changed, Clicked;
        public event Action<VClip> Picked;

        public VideoPreview() { Surface = Surface.Card; DoubleBuffered = true; }

        public RectangleF Screen
        {
            get
            {
                if (P == null) return RectangleF.Empty;
                float m = Theme.S(10), k = Math.Min((Width - m * 2) / P.W, (Height - m * 2) / P.H);
                return new RectangleF((Width - P.W * k) / 2, (Height - P.H * k) / 2, P.W * k, P.H * k);
            }
        }

        public void SetFrame(Bitmap b) { Bitmap old = Frame; Frame = b; if (old != null && old != b) old.Dispose(); Invalidate(); }

        bool Movable(VClip c) { return c != null && (c.Kind == VKind.Text || ((c.Kind == VKind.Video || c.Kind == VKind.Image) && P != null && P.TrackOf(c) != null && P.TrackOf(c).Kind == VTrackKind.Overlay)) && Time >= c.At && Time < c.End; }

        /// The selected overlay's corners on screen.
        PointF[] Frame4(VClip c)
        {
            RectangleF s = Screen; float k = s.Width / P.W;
            PointF mid; float scale, rot, alpha;
            Composer.Placement(c, Time, P.W, P.H, out mid, out scale, out rot, out alpha);
            float w, h;
            if (c.Kind == VKind.Text)
            {
                TextLayer tl = c.Text.CloneStyle(); tl.Pos = PointF.Empty; tl.Angle = 0; tl.Touch();
                RectangleF b = tl.Bounds; w = b.Width * scale * k; h = b.Height * scale * k;
            }
            else { w = scale * P.W * k; h = w * Math.Max(1, c.Src.H) / Math.Max(1, c.Src.W); }
            PointF ctr = new PointF(s.X + mid.X * k, s.Y + mid.Y * k);
            double a = rot * Math.PI / 180, co = Math.Cos(a), si = Math.Sin(a);
            PointF[] p = { new PointF(-w / 2, -h / 2), new PointF(w / 2, -h / 2), new PointF(w / 2, h / 2), new PointF(-w / 2, h / 2) };
            for (int i = 0; i < 4; i++) p[i] = new PointF((float)(ctr.X + p[i].X * co - p[i].Y * si), (float)(ctr.Y + p[i].X * si + p[i].Y * co));
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Dark ? Color.FromArgb(18, 18, 20) : Color.FromArgb(28, 28, 31));
            if (P == null) return;
            RectangleF s = Screen;
            if (Frame != null) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(Frame, s); }
            else using (SolidBrush b = new SolidBrush(Color.Black)) g.FillRectangle(b, s);
            Theme.Smooth(g);
            if (!Playing && Movable(Selected))
            {
                PointF[] f = Frame4(Selected);
                using (Pen p = new Pen(Color.White, Theme.S(1.5f))) g.DrawPolygon(p, f);
                foreach (PointF c in f)
                {
                    RectangleF r = new RectangleF(c.X - Theme.S(5), c.Y - Theme.S(5), Theme.S(10), Theme.S(10));
                    using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, r);
                    using (Pen p = new Pen(Theme.AccentColor, 1.5f)) g.DrawEllipse(p, r);
                }
            }
            if (Note != null)
            {
                Font f = Theme.F(9.5f, FontStyle.Bold);
                int w = Theme.Measure(Note, f) + Theme.S(36), h = Theme.S(36);
                Rectangle r = new Rectangle((int)(s.X + (s.Width - w) / 2), (int)(s.Y + (s.Height - h) / 2), w, h);
                Theme.FillRound(g, r, h / 2f, Color.FromArgb(210, 20, 20, 22));
                Theme.Text(g, Note, f, Color.White, r, Align.Center);
            }
        }

        // ------------------------------------------------------------------ moving and sizing on the picture
        int grab; PointF down; float downX, downY, downScale, downDist;
        bool moved;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            grab = 0; moved = false;
            if (P == null || e.Button != MouseButtons.Left) return;
            // while playing, a click on the picture pauses
            if (Playing) { if (Clicked != null) Clicked(); return; }
            VClip c = Selected;
            if (Movable(c))
            {
                PointF[] f = Frame4(c);
                PointF ctr = new PointF((f[0].X + f[2].X) / 2, (f[0].Y + f[2].Y) / 2);
                foreach (PointF q in f) if (Math.Abs(q.X - e.X) < Theme.S(10) && Math.Abs(q.Y - e.Y) < Theme.S(10)) { grab = 2; break; }
                if (grab == 0 && Inside(f, e.Location)) grab = 1;
                if (grab != 0)
                {
                    down = e.Location;
                    downX = c.Eval("x", Time, c.X); downY = c.Eval("y", Time, c.Y); downScale = c.Eval("scale", Time, c.Scale);
                    downDist = Math.Max(1, (float)Math.Sqrt((e.X - ctr.X) * (e.X - ctr.X) + (e.Y - ctr.Y) * (e.Y - ctr.Y)));
                    return;
                }
            }
            // clicked another overlay or text on the picture: pick it
            for (int ti = 0; ti < P.MainIndex; ti++)
                foreach (VClip o in P.Tracks[ti].Clips)
                    if (Movable(o) && Inside(Frame4(o), e.Location)) { if (Picked != null) Picked(o); return; }
            if (Clicked != null) Clicked();
        }

        static bool Inside(PointF[] poly, PointF p)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) && (p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)) c = !c;
            return c;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (grab == 0 || Selected == null) { Cursor = Movable(Selected) && Inside(Frame4(Selected), e.Location) ? Cursors.SizeAll : Cursors.Default; return; }
            if (!moved) { if (BeforeChange != null && !BeforeChange(grab == 1 ? "vs_h_move_pic" : "vs_h_size_pic")) { grab = 0; return; } moved = true; }
            RectangleF s = Screen; float k = s.Width / P.W;
            VClip c = Selected;
            if (grab == 1)
            {
                float nx = downX + (e.X - down.X) / k / P.W, ny = downY + (e.Y - down.Y) / k / P.H;
                // the middle and the edges of the picture pull a little
                if (Math.Abs(nx - 0.5f) < 0.012f) nx = 0.5f;
                if (Math.Abs(ny - 0.5f) < 0.012f) ny = 0.5f;
                Set(c, "x", nx); Set(c, "y", ny);
            }
            else
            {
                PointF[] f = Frame4(c);
                PointF ctr = new PointF((f[0].X + f[2].X) / 2, (f[0].Y + f[2].Y) / 2);
                float d = (float)Math.Sqrt((e.X - ctr.X) * (e.X - ctr.X) + (e.Y - ctr.Y) * (e.Y - ctr.Y));
                Set(c, "scale", Math.Max(0.02f, downScale * d / downDist));
            }
            if (Changed != null) Changed();
        }

        /// Sets a placement value: on a keyframe when the property is animated (at the playhead), else the plain value.
        void Set(VClip c, string prop, float v)
        {
            if (c.HasKeys(prop)) { c.SetKey(prop, Time, v); return; }
            if (prop == "x") c.X = v; else if (prop == "y") c.Y = v; else if (prop == "scale") c.Scale = v;
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); grab = 0; if (moved && Changed != null) Changed(); moved = false; }
        protected override string AccName { get { return L.T("vs_preview"); } }
    }
}
