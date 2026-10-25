using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace FileConverter
{
    /// The video timeline: one row per track (texts and overlays above the main video, sounds under it), a ruler,
    /// the red playhead. Click to jump, drag clips to move them (or between tracks), pull their ends to trim,
    /// Ctrl + wheel to zoom. Clips snap to each other and to the playhead.
    public class VideoTimeline : UiControl
    {
        public VProject P;
        public double Playhead;
        public VClip Selected;
        public bool Snap = true;
        public double PxPerSec = 60;
        double scroll;   // seconds at the left edge
        int scrollY;
        public Func<string, bool> BeforeChange;
        public event Action Changed, ZoomChanged;
        public event Action<double, bool> Seek;
        public event Action<VClip> Picked;
        public event Action<int> TransitionPicked;
        public event Action<VClip, Point> Menu;
        public event Action<string[], VTrack, double> FilesDropped;
        public event Action<VClip> Opened;

        public VideoTimeline() { Surface = Surface.Card; TabStop = true; AllowDrop = true; DoubleBuffered = true; }

        int HeadW { get { return Theme.S(138); } }
        int RulerH { get { return Theme.S(26); } }
        int BarH { get { return Theme.S(10); } }
        public static int TrackH(VTrackKind k)
        {
            switch (k)
            {
                case VTrackKind.Main: return Theme.S(64);
                case VTrackKind.Overlay: return Theme.S(48);
                case VTrackKind.Audio: return Theme.S(44);
                default: return Theme.S(34);
            }
        }
        int Gap { get { return Theme.S(4); } }
        public int ContentH { get { int h = RulerH + Theme.S(6); if (P != null) foreach (VTrack t in P.Tracks) h += TrackH(t.Kind) + Gap; return h + BarH + Theme.S(4); } }

        float X(double t) { return (float)(HeadW + (t - scroll) * PxPerSec); }
        double T(float x) { return scroll + (x - HeadW) / PxPerSec; }
        int TrackY(int i) { int y = RulerH + Theme.S(6) - scrollY; for (int k = 0; k < i; k++) y += TrackH(P.Tracks[k].Kind) + Gap; return y; }
        Rectangle TrackR(int i) { return new Rectangle(HeadW, TrackY(i), Width - HeadW, TrackH(P.Tracks[i].Kind)); }
        int TrackAt(int y) { if (P == null) return -1; for (int i = 0; i < P.Tracks.Count; i++) { int ty = TrackY(i); if (y >= ty - Gap / 2 && y < ty + TrackH(P.Tracks[i].Kind) + Gap / 2) return i; } return -1; }
        RectangleF ClipR(int ti, VClip c) { Rectangle tr = TrackR(ti); return new RectangleF(X(c.At), tr.Y, (float)(c.Length * PxPerSec), tr.Height); }

        double Total { get { return P == null ? 1 : Math.Max(5, P.Duration); } }
        double MaxScroll { get { return Math.Max(0, Total + 10 - (Width - HeadW) / PxPerSec); } }

        /// Zoom so the whole project fits.
        public void FitAll()
        {
            if (P == null || Width <= HeadW) return;
            PxPerSec = Math.Max(2, Math.Min(400, (Width - HeadW - Theme.S(40)) / Math.Max(1, Total)));
            scroll = 0;
            Invalidate();
            if (ZoomChanged != null) ZoomChanged();
        }
        public void ZoomBy(double f, float atX)
        {
            double tAt = T(atX);
            PxPerSec = Math.Max(2, Math.Min(600, PxPerSec * f));
            scroll = Math.Max(0, Math.Min(MaxScroll, tAt - (atX - HeadW) / PxPerSec));
            Invalidate();
            if (ZoomChanged != null) ZoomChanged();
        }
        /// Keeps the playhead in view while playing.
        public void Follow()
        {
            float x = X(Playhead);
            if (x < HeadW || x > Width - Theme.S(30)) { scroll = Math.Max(0, Math.Min(MaxScroll, Playhead - (Width - HeadW) * 0.15 / PxPerSec)); }
            Invalidate();
        }

        // ------------------------------------------------------------------ painting
        static readonly Color CVideo = Color.FromArgb(58, 112, 196), CImage = Color.FromArgb(86, 132, 168), CText = Color.FromArgb(150, 92, 200), CCap = Color.FromArgb(214, 150, 40), CAudio = Color.FromArgb(46, 150, 108);
        static Color ColorOf(VClip c) { switch (c.Kind) { case VKind.Image: return CImage; case VKind.Text: return CText; case VKind.Captions: return CCap; case VKind.Audio: return CAudio; default: return CVideo; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            if (P == null) return;
            P.Layout();
            Font small = Theme.F(7.75f);
            // ruler
            Rectangle ruler = new Rectangle(HeadW, 0, Width - HeadW, RulerH);
            double[] steps = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            double step = 600; foreach (double s in steps) if (s * PxPerSec >= Theme.S(70)) { step = s; break; }
            using (Pen tp = new Pen(Theme.InkFaint))
            {
                g.SetClip(ruler);
                for (double t = Math.Floor(scroll / step) * step; ; t += step)
                {
                    float x = X(t); if (x > Width) break;
                    if (x < HeadW - 60) continue;
                    g.DrawLine(tp, x, RulerH - Theme.S(8), x, RulerH);
                    for (int k = 1; k < 4; k++) { float mx = x + (float)(step * PxPerSec * k / 4); g.DrawLine(tp, mx, RulerH - Theme.S(4), mx, RulerH); }
                    TextRenderer.DrawText(g, Theme.Time(t, step < 1), small, new Point((int)x + 3, Theme.S(4)), Theme.InkFaint, TextFormatFlags.NoPadding);
                }
                g.ResetClip();
            }
            // tracks
            Rectangle area = new Rectangle(0, RulerH + Theme.S(2), Width, Height - RulerH - BarH - Theme.S(4));
            g.SetClip(area);
            for (int i = 0; i < P.Tracks.Count; i++)
            {
                VTrack t = P.Tracks[i];
                Rectangle tr = TrackR(i);
                if (tr.Bottom < area.Y || tr.Y > area.Bottom) continue;
                Theme.FillRound(g, new RectangleF(HeadW, tr.Y, Width - HeadW - Theme.S(2), tr.Height), Theme.S(6), Theme.Mix(Theme.CardInner, Theme.Track, t.Kind == VTrackKind.Main ? 0.45f : 0.2f));
                PaintHead(g, i, t);
                g.SetClip(Rectangle.Intersect(area, new Rectangle(HeadW, tr.Y, Width - HeadW, tr.Height)));
                for (int ci = 0; ci < t.Clips.Count; ci++) PaintClip(g, i, t, ci);
                if (t.Kind == VTrackKind.Main) for (int ci = 0; ci + 1 < t.Clips.Count; ci++) PaintTrans(g, i, ci);
                g.SetClip(area);
            }
            // where a dragged clip would land
            if (dropMark >= 0) using (Pen p = new Pen(Theme.AccentColor, Theme.S(3))) { Rectangle tr = TrackR(P.MainIndex); g.DrawLine(p, dropMark, tr.Y - 2, dropMark, tr.Bottom + 2); }
            if (snapX >= 0) using (Pen p = new Pen(Color.FromArgb(236, 64, 160), 1f) { DashStyle = DashStyle.Dash }) g.DrawLine(p, snapX, area.Y, snapX, area.Bottom);
            g.ResetClip();
            // the playhead
            float px = X(Playhead);
            if (px >= HeadW - 1 && px <= Width)
            {
                Color red = Color.FromArgb(232, 59, 47);
                using (Pen p = new Pen(red, Theme.S(1.6f))) g.DrawLine(p, px, Theme.S(4), px, Height - BarH - Theme.S(2));
                PointF[] head = { new PointF(px - Theme.S(6), Theme.S(2)), new PointF(px + Theme.S(6), Theme.S(2)), new PointF(px + Theme.S(6), Theme.S(10)), new PointF(px, Theme.S(16)), new PointF(px - Theme.S(6), Theme.S(10)) };
                using (SolidBrush b = new SolidBrush(red)) g.FillPolygon(b, head);
            }
            // the scrollbar
            Rectangle bar = new Rectangle(HeadW, Height - BarH, Width - HeadW - Theme.S(4), Theme.S(6));
            double vis = (Width - HeadW) / PxPerSec, all = Total + 10;
            if (vis < all)
            {
                Theme.FillRound(g, bar, Theme.S(3), Theme.Track);
                float tw = Math.Max(Theme.S(30), (float)(bar.Width * vis / all)), tx = bar.X + (float)((bar.Width - tw) * scroll / Math.Max(0.001, MaxScroll));
                Theme.FillRound(g, new RectangleF(tx, bar.Y, tw, bar.Height), Theme.S(3), Color.FromArgb(140, Theme.InkFaint));
            }
            if (ContentH > Height)
            {
                int vh = Height - RulerH - BarH;
                float th = Math.Max(Theme.S(24), vh * vh / (float)ContentH), ty = RulerH + (vh - th) * scrollY / Math.Max(1, ContentH - Height);
                Theme.FillRound(g, new RectangleF(Width - Theme.S(5), ty, Theme.S(3), th), Theme.S(1.5f), Color.FromArgb(120, Theme.InkFaint));
            }
            if (P.Empty) Theme.Text(g, L.T("vs_tl_empty"), Theme.F(9f), Theme.InkFaint, new Rectangle(HeadW, RulerH, Width - HeadW, Height - RulerH - BarH), Align.Center);
        }

        void PaintHead(Graphics g, int i, VTrack t)
        {
            Rectangle tr = TrackR(i);
            Rectangle hr = new Rectangle(Theme.S(4), tr.Y, HeadW - Theme.S(10), tr.Height);
            Theme.FillRound(g, hr, Theme.S(6), Theme.CardInner);
            string glyph = t.Kind == VTrackKind.Main ? Ico.Video : t.Kind == VTrackKind.Overlay ? Ico.Photo : t.Kind == VTrackKind.Text ? Ico.Text : t.Kind == VTrackKind.Captions ? "\uE8BE" : Ico.Music;
            Theme.Glyph(g, glyph, 9f, Theme.InkSoft, new Rectangle(hr.X + Theme.S(4), hr.Y, Theme.S(22), hr.Height));
            string name = L.T("vs_tr_" + t.Kind.ToString().ToLowerInvariant());
            // hide (pictures) and mute (sound)
            Rectangle a = HeadBtn(i, 0), b = HeadBtn(i, 1);
            bool canHide = t.Visual, canMute = t.Kind == VTrackKind.Main || t.Kind == VTrackKind.Audio || t.Kind == VTrackKind.Overlay;
            if (canHide) Theme.Glyph(g, t.Hidden ? Ps.EyeOff : Ps.Eye, 8f, t.Hidden ? Theme.AccentColor : Theme.InkFaint, a);
            if (canMute) Theme.Glyph(g, t.Muted ? "\uE74F" : Ico.Volume, 8f, t.Muted ? Theme.AccentColor : Theme.InkFaint, b);
            int nameEnd = canHide ? a.X : canMute ? b.X : hr.Right;
            Theme.Text(g, name, Theme.F(7.75f, FontStyle.Bold), Theme.InkSoft, new Rectangle(hr.X + Theme.S(26), hr.Y, Math.Max(4, nameEnd - hr.X - Theme.S(28)), hr.Height), Align.Start);
        }
        Rectangle HeadBtn(int i, int which) { Rectangle tr = TrackR(i); int s = Theme.S(22); return new Rectangle(HeadW - Theme.S(10) - s * (2 - which), tr.Y + (tr.Height - s) / 2, s, s); }

        void PaintClip(Graphics g, int ti, VTrack t, int ci)
        {
            VClip c = t.Clips[ci];
            RectangleF r = ClipR(ti, c);
            if (r.Right < HeadW || r.X > Width) return;
            r.Inflate(-0.5f, -1);
            bool sel = c == Selected;
            Color col = ColorOf(c);
            if (t.Hidden || t.Muted && !c.Visual) col = Theme.Mix(col, Theme.Track, 0.6f);
            using (GraphicsPath gp = Theme.Round(r, Theme.S(6)))
            {
                using (SolidBrush b = new SolidBrush(Theme.Mix(col, Theme.Dark ? Color.Black : Color.White, Theme.Dark ? 0.35f : 0.55f))) g.FillPath(b, gp);
                Region keep = g.Clip;
                g.SetClip(gp, CombineMode.Intersect);
                // what's in it
                if ((c.Kind == VKind.Video || c.Kind == VKind.Image) && c.Src != null) PaintThumbs(g, c, r);
                if (c.Kind == VKind.Audio || (c.Kind == VKind.Video && t.Kind == VTrackKind.Main && r.Height > Theme.S(50)))
                    PaintWave(g, c, c.Kind == VKind.Audio ? r : new RectangleF(r.X, r.Bottom - Theme.S(14), r.Width, Theme.S(13)), c.Kind == VKind.Audio ? col : Color.FromArgb(200, 255, 255, 255));
                if (c.Kind == VKind.Captions && c.Cues != null)
                    foreach (Cue q in c.Cues)
                    {
                        float x0 = X(q.Start), x1 = X(q.End);
                        if (x1 < r.X || x0 > r.Right) continue;
                        Theme.FillRound(g, new RectangleF(x0 + 1, r.Y + Theme.S(5), Math.Max(2, x1 - x0 - 2), r.Height - Theme.S(10)), Theme.S(3), Color.FromArgb(Theme.Dark ? 120 : 90, col));
                    }
                // fades
                using (SolidBrush fb = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                {
                    if (c.FadeIn > 0.01) g.FillPolygon(fb, new[] { new PointF(r.X, r.Y), new PointF(r.X + (float)(c.FadeIn * PxPerSec), r.Y), new PointF(r.X, r.Bottom) });
                    if (c.FadeOut > 0.01) g.FillPolygon(fb, new[] { new PointF(r.Right, r.Y), new PointF(r.Right - (float)(c.FadeOut * PxPerSec), r.Y), new PointF(r.Right, r.Bottom) });
                }
                // the name, on a soft dark band so it reads over any picture
                string label = c.Kind == VKind.Text ? c.Name : c.Kind == VKind.Captions ? L.T("vs_captions") + " · " + c.Cues.Count : c.Name;
                if (Math.Abs(c.Speed - 1) > 0.001) label = c.Speed.ToString("0.##") + "×  " + label;
                if (c.Muted) label = "🔇 " + label;
                Font f = Theme.F(7.75f, FontStyle.Bold);
                bool onPic = c.Kind == VKind.Video || c.Kind == VKind.Image;
                Rectangle lr = new Rectangle((int)Math.Max(r.X, HeadW) + Theme.S(6), (int)r.Y + Theme.S(2), Math.Max(10, (int)(r.Right - Math.Max(r.X, HeadW)) - Theme.S(10)), Theme.S(16));
                if (onPic) { int tw = Math.Min(lr.Width, Theme.Measure(label, f) + Theme.S(8)); Theme.FillRound(g, new Rectangle(lr.X - Theme.S(3), lr.Y + 1, tw, lr.Height - 1), Theme.S(4), Color.FromArgb(150, 0, 0, 0)); }
                Theme.Text(g, label, f, onPic || Theme.Dark ? Color.White : Theme.Mix(col, Color.Black, 0.55f), lr, Align.Start, true, false, true);
                g.Clip = keep;
                using (Pen p = new Pen(sel ? Theme.AccentColor : Theme.Mix(col, Color.Black, 0.15f), sel ? Theme.S(2.2f) : 1f)) g.DrawPath(p, gp);
            }
            if (sel)
            {
                // trim handles, and the keyframes as little diamonds
                foreach (float hx in new[] { r.X, r.Right - Theme.S(6) })
                    Theme.FillRound(g, new RectangleF(hx, r.Y + 1, Theme.S(6), r.Height - 2), Theme.S(3), Theme.AccentColor);
                foreach (double kt in c.KeyTimes())
                {
                    float kx = X(c.At + kt), ky = r.Bottom - Theme.S(7), s = Theme.S(5);
                    PointF[] d = { new PointF(kx, ky - s), new PointF(kx + s, ky), new PointF(kx, ky + s), new PointF(kx - s, ky) };
                    using (SolidBrush b = new SolidBrush(Color.White)) g.FillPolygon(b, d);
                    using (Pen p = new Pen(Theme.AccentColor, 1.5f)) g.DrawPolygon(p, d);
                }
            }
        }

        void PaintThumbs(Graphics g, VClip c, RectangleF r)
        {
            Image[] strip = c.Src.Strip;
            if (strip == null || strip.Length == 0) return;
            float th = r.Height, tw = th * Math.Max(0.5f, Math.Min(2.2f, c.Src.W / (float)Math.Max(1, c.Src.H)));
            double dur = c.Src.Info != null && c.Src.Info.Duration > 0 ? c.Src.Info.Duration : Math.Max(c.Out, 1);
            float start = Math.Max(r.X, HeadW - tw);
            for (float x = r.X; x < r.Right; x += tw)
            {
                if (x + tw < start || x > Width) continue;
                double f = (x + tw / 2 - r.X) / Math.Max(1, r.Width);
                double st = c.Kind == VKind.Image ? 0 : c.In + f * (c.Out - c.In);
                int i = c.Kind == VKind.Image ? 0 : Math.Max(0, Math.Min(strip.Length - 1, (int)(st / dur * strip.Length)));
                Image im = strip[i];
                if (im == null) for (int k = 0; k < strip.Length && im == null; k++) im = strip[k];
                if (im == null) continue;
                g.InterpolationMode = InterpolationMode.Bilinear;
                // cover the slot (cropped), never stretched
                float ia = im.Width / (float)im.Height, sa = tw / th;
                RectangleF src = ia > sa ? new RectangleF((im.Width - im.Height * sa) / 2, 0, im.Height * sa, im.Height) : new RectangleF(0, (im.Height - im.Width / sa) / 2, im.Width, im.Width / sa);
                g.DrawImage(im, new RectangleF(x, r.Y, tw, th), src, GraphicsUnit.Pixel);
            }
        }

        void PaintWave(Graphics g, VClip c, RectangleF r, Color col)
        {
            Image w = c.Src == null ? null : c.Src.Wave;
            if (w == null) return;
            double dur = c.Src.Info != null && c.Src.Info.Duration > 0 ? c.Src.Info.Duration : Math.Max(c.Out, 1);
            float sx = (float)(c.In / dur * w.Width), sw = (float)((c.Out - c.In) / dur * w.Width);
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(w, r, new RectangleF(sx, 0, Math.Max(1, sw), w.Height), GraphicsUnit.Pixel);
        }

        RectangleF TransR(int ti, int ci)
        {
            VClip a = P.Tracks[ti].Clips[ci + 1];
            Rectangle tr = TrackR(ti);
            float x = X(a.At + P.TransLen(ci) / 2), s = Theme.S(20);
            return new RectangleF(x - s / 2, tr.Y + (tr.Height - s) / 2, s, s);
        }

        void PaintTrans(Graphics g, int ti, int ci)
        {
            RectangleF r = TransR(ti, ci);
            bool on = P.Tracks[ti].Clips[ci].Trans != "none";
            Theme.FillRound(g, r, Theme.S(5), on ? Theme.AccentColor : Theme.Card);
            Theme.StrokeRound(g, r, Theme.S(5), on ? Theme.AccentColor : Theme.FieldBorder);
            Color ink = on ? Color.White : Theme.InkSoft;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, s = r.Width * 0.22f;
            using (SolidBrush b = new SolidBrush(ink))
            {
                g.FillPolygon(b, new[] { new PointF(cx - s * 1.6f, cy - s), new PointF(cx - 1, cy), new PointF(cx - s * 1.6f, cy + s) });
                g.FillPolygon(b, new[] { new PointF(cx + s * 1.6f, cy - s), new PointF(cx + 1, cy), new PointF(cx + s * 1.6f, cy + s) });
            }
        }

        // ------------------------------------------------------------------ mouse
        enum Drag { None, Seek, Move, TrimL, TrimR, Scroll, Bar }
        Drag drag;
        VClip dragClip; int dragTrack;
        Point downAt; double downT, origAt, origIn, origOut, origLen;
        bool changed;
        int dropMark = -1; float snapX = -1;
        int mainFrom = -1;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if ((ModifierKeys & Keys.Control) != 0) { ZoomBy(e.Delta > 0 ? 1.25 : 0.8, Math.Max(HeadW, e.X)); return; }
            if (((ModifierKeys & Keys.Shift) != 0 || e.X < HeadW) && ContentH > Height) { scrollY = Math.Max(0, Math.Min(ContentH - Height, scrollY - e.Delta / 3)); Invalidate(); return; }
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120.0 * 80 / PxPerSec));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (P == null) return;
            downAt = e.Location; downT = T(e.X); changed = false;
            if (e.Button == MouseButtons.Middle) { drag = Drag.Scroll; origAt = scroll; return; }
            if (e.Y >= Height - BarH - 2 && e.X > HeadW) { drag = Drag.Bar; origAt = scroll; return; }
            if (e.Y < RulerH && e.X > HeadW) { drag = Drag.Seek; SeekTo(T(e.X), false); return; }
            int ti = TrackAt(e.Y);
            if (ti < 0) { if (e.X > HeadW) { drag = Drag.Seek; SeekTo(T(e.X), false); } return; }
            VTrack t = P.Tracks[ti];
            if (e.X < HeadW)
            {
                // track buttons
                if (HeadBtn(ti, 0).Contains(e.Location) && t.Visual) { if (Begin("vs_h_track")) { t.Hidden = !t.Hidden; Done(); } }
                else if (HeadBtn(ti, 1).Contains(e.Location) && (t.Kind == VTrackKind.Main || t.Kind == VTrackKind.Audio || t.Kind == VTrackKind.Overlay)) { if (Begin("vs_h_track")) { t.Muted = !t.Muted; Done(); } }
                return;
            }
            // a transition badge
            if (t.Kind == VTrackKind.Main)
                for (int ci = 0; ci + 1 < t.Clips.Count; ci++)
                    if (TransR(ti, ci).Contains(e.Location)) { Select(t.Clips[ci]); if (TransitionPicked != null) TransitionPicked(ci); return; }
            for (int ci = t.Clips.Count - 1; ci >= 0; ci--)
            {
                VClip c = t.Clips[ci];
                RectangleF r = ClipR(ti, c);
                if (!r.Contains(e.Location)) continue;
                Select(c);
                if (e.Button == MouseButtons.Right) { if (Menu != null) Menu(c, PointToScreen(e.Location)); return; }
                dragClip = c; dragTrack = ti;
                origAt = c.At; origIn = c.In; origOut = c.Out; origLen = c.Length;
                float edge = Theme.S(8);
                if (e.X - r.X <= edge && r.Width > edge * 2.5f) drag = Drag.TrimL;
                else if (r.Right - e.X <= edge && r.Width > edge * 2.5f) drag = Drag.TrimR;
                else { drag = Drag.Move; mainFrom = t.Kind == VTrackKind.Main ? ci : -1; }
                return;
            }
            // empty space: jump there (and nothing selected)
            Select(null);
            drag = Drag.Seek; SeekTo(T(e.X), false);
        }

        void Select(VClip c) { if (Selected == c) return; Selected = c; Invalidate(); if (Picked != null) Picked(c); }
        bool Begin(string name) { if (changed) return true; if (BeforeChange != null && !BeforeChange(name)) return false; changed = true; return true; }
        void Done() { P.Layout(); Invalidate(); if (Changed != null) Changed(); }

        void SeekTo(double t, bool final)
        {
            Playhead = Math.Max(0, Math.Min(Math.Max(0, P.Duration - 0.01), t));
            Invalidate();
            if (Seek != null) Seek(Playhead, final);
        }

        /// The nearest place to snap to (the playhead, the start, other clips' ends), within 8 pixels.
        double SnapTime(double t, VClip self, out bool hit)
        {
            hit = false; snapX = -1;
            if (!Snap) return t;
            double best = Theme.S(8) / PxPerSec, outp = t;
            List<double> pts = new List<double> { 0, Playhead };
            foreach (VTrack tr in P.Tracks) foreach (VClip c in tr.Clips) if (c != self) { pts.Add(c.At); pts.Add(c.End); }
            foreach (double p in pts) if (Math.Abs(p - t) < best) { best = Math.Abs(p - t); outp = p; hit = true; }
            if (hit) snapX = X(outp);
            return outp;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (P == null) return;
            double t = T(e.X), dt = t - downT;
            switch (drag)
            {
                case Drag.Seek: SeekTo(t, false); return;
                case Drag.Scroll: scroll = Math.Max(0, Math.Min(MaxScroll, origAt - (e.X - downAt.X) / PxPerSec)); Invalidate(); return;
                case Drag.Bar:
                    {
                        double all = Total + 10;
                        scroll = Math.Max(0, Math.Min(MaxScroll, origAt + (e.X - downAt.X) / (double)Math.Max(1, Width - HeadW) * all));
                        Invalidate(); return;
                    }
                case Drag.TrimL: case Drag.TrimR: TrimDrag(dt); return;
                case Drag.Move:
                    if (Math.Abs(e.X - downAt.X) < Theme.S(4) && Math.Abs(e.Y - downAt.Y) < Theme.S(4) && !changed) return;
                    MoveDrag(e, dt);
                    return;
            }
            // the cursor says what a drag would do
            int ti = TrackAt(e.Y);
            Cursor cur = Cursors.Default;
            if (ti >= 0 && e.X > HeadW)
                foreach (VClip c in P.Tracks[ti].Clips)
                {
                    RectangleF r = ClipR(ti, c);
                    if (!r.Contains(e.Location)) continue;
                    float edge = Theme.S(8);
                    cur = (e.X - r.X <= edge || r.Right - e.X <= edge) && r.Width > edge * 2.5f ? Cursors.SizeWE : Cursors.Hand;
                }
            if (Cursor != cur) Cursor = cur;
        }

        void TrimDrag(double dt)
        {
            VClip c = dragClip;
            if (!Begin("vs_h_trim")) return;
            bool media = c.Kind == VKind.Video || c.Kind == VKind.Audio;
            double srcDur = media && c.Src != null && c.Src.Info != null ? c.Src.Info.Duration : 1e9;
            bool mainT = P.Tracks[dragTrack].Kind == VTrackKind.Main;
            bool hit;
            if (drag == Drag.TrimR)
            {
                double end = SnapTime(origAt + origLen + dt, c, out hit);
                double len = Math.Max(0.1, end - origAt);
                c.Out = Math.Min(srcDur, origIn + len * c.Speed);
                if (!media) c.Out = c.In + len;
            }
            else
            {
                double start = mainT ? origAt + dt : SnapTime(origAt + dt, c, out hit);
                double d = start - origAt;
                if (media) d = Math.Max(-origIn / c.Speed, d);
                if (!mainT) d = Math.Max(-origAt, d);
                d = Math.Min(d, origLen - 0.1);
                if (media || c.Kind == VKind.Image) c.In = origIn + d * c.Speed;
                if (c.Kind == VKind.Image || c.Kind == VKind.Text || c.Kind == VKind.Captions) { c.In = 0; c.Out = Math.Max(0.1, origOut - origIn - d); }
                if (!mainT) c.At = origAt + d;
            }
            P.Layout(); Invalidate();
            if (Changed != null) Changed();
        }

        void MoveDrag(MouseEventArgs e, double dt)
        {
            VClip c = dragClip;
            VTrack from = P.Tracks[dragTrack];
            if (from.Kind == VTrackKind.Main)
            {
                // reorder: where between the main clips would it go
                List<VClip> m = from.Clips;
                int at = m.Count;
                for (int i = 0; i < m.Count; i++) if (T(e.X) < m[i].At + m[i].Length / 2) { at = i; break; }
                dropMark = (int)(at < m.Count ? X(m[at].At) : X(m[m.Count - 1].End));
                Invalidate();
                return;
            }
            if (!Begin("vs_h_move")) return;
            bool hit;
            double nat = Math.Max(0, origAt + dt);
            double s1 = SnapTime(nat, c, out hit);
            if (hit) nat = s1;
            else { double s2 = SnapTime(nat + c.Length, c, out hit); if (hit) nat = s2 - c.Length; }
            c.At = Math.Max(0, nat);
            // another track of the same kind under the mouse
            int ti = TrackAt(e.Y);
            if (ti >= 0 && ti != dragTrack && P.Tracks[ti].Kind == from.Kind)
            {
                from.Clips.Remove(c); P.Tracks[ti].Clips.Add(c); dragTrack = ti;
            }
            P.Layout(); Invalidate();
            if (Changed != null) Changed();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (P == null) return;
            Drag was = drag; drag = Drag.None; snapX = -1;
            if (was == Drag.Seek) { SeekTo(T(e.X), true); return; }
            if (was == Drag.Move && dropMark >= 0 && dragClip != null && mainFrom >= 0)
            {
                List<VClip> m = P.Main.Clips;
                int at = m.Count;
                for (int i = 0; i < m.Count; i++) if (T(e.X) < m[i].At + m[i].Length / 2) { at = i; break; }
                if (at != mainFrom && at != mainFrom + 1 && Begin("vs_h_move"))
                {
                    m.RemoveAt(mainFrom);
                    if (at > mainFrom) at--;
                    m.Insert(at, dragClip);
                    Done();
                }
            }
            dropMark = -1;
            if (changed) { P.Tidy(); Done(); }
            changed = false; dragClip = null;
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (Selected != null && Opened != null) Opened(Selected);
        }

        // ------------------------------------------------------------------ files dropped on a track
        protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && !DragOut.Active ? DragDropEffects.Copy : DragDropEffects.None; }
        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            if (DragOut.Active) return;
            string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (f == null || P == null || FilesDropped == null) return;
            Point p = PointToClient(new Point(e.X, e.Y));
            int ti = TrackAt(p.Y);
            FilesDropped(f, ti >= 0 ? P.Tracks[ti] : null, Math.Max(0, T(p.X)));
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override string AccName { get { return L.T("vs_timeline"); } }
    }
}
