using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Filmstrip / waveform with a draggable start-end selection and a playhead. Always left-to-right (time flows that way).
    /// Click anywhere to jump there (and play from there); drag the white handles to trim.
    public class Timeline : UiControl
    {
        public double Duration = 1, Start, End = 1, Playhead = -1;
        public Image[] Strip;
        public Image Wave;
        public bool IsAudio, ReadOnly;
        int drag; // 0 none, 1 start, 2 end, 4 playhead
        int hoverHandle;
        public event Action<int> RangeChanged;   // 1 start moved, 2 end moved
        public event Action<double, bool> Seek;  // time, finished (mouse released)
        Bitmap stripCache; int cacheW, cacheH; object cacheKey;

        public Timeline() { Cursor = Cursors.Hand; }

        Rectangle Track { get { return new Rectangle(Theme.S(14), Theme.S(6), Width - Theme.S(28), Height - Theme.S(30)); } }
        float X(double t) { Rectangle r = Track; return r.X + (float)(r.Width * (Duration > 0 ? t / Duration : 0)); }
        double Tm(int x) { Rectangle r = Track; return Math.Max(0, Math.Min(Duration, (x - r.X) / (double)Math.Max(1, r.Width) * Duration)); }
        public void StripChanged() { if (stripCache != null) { stripCache.Dispose(); stripCache = null; } Invalidate(); }

        int HandleAt(int x)
        {
            if (ReadOnly) return 0;
            float xs = X(Start), xe = X(End);
            int grab = Theme.S(10);
            if (Math.Abs(x - xs) <= grab && Math.Abs(x - xs) <= Math.Abs(x - xe)) return 1;
            if (Math.Abs(x - xe) <= grab) return 2;
            return 0;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            drag = HandleAt(e.X);
            if (drag == 0)
            {
                drag = 4;
                Playhead = Tm(e.X);
                if (Seek != null) Seek(Playhead, false);
            }
            Capture = true;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = drag == 0 ? HandleAt(e.X) : (drag == 4 ? 0 : drag);
            if (h != hoverHandle) { hoverHandle = h; Invalidate(); }
            Cursor = h != 0 ? Cursors.SizeWE : Cursors.Hand;
            if (drag == 0) return;
            double t = Tm(e.X);
            double minLen = Math.Min(0.1, Duration / 100);
            if (drag == 1) { Start = Math.Min(t, End - minLen); if (RangeChanged != null) RangeChanged(1); }
            else if (drag == 2) { End = Math.Max(t, Start + minLen); if (RangeChanged != null) RangeChanged(2); }
            else if (drag == 4) { Playhead = t; if (Seek != null) Seek(t, false); }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int was = drag;
            drag = 0; Capture = false;
            if (was == 4 && Seek != null) Seek(Playhead, true);
            else if ((was == 1 || was == 2) && RangeChanged != null) RangeChanged(was + 10);   // 11/12: drag finished
            Invalidate();
        }

        /// Film strip / waveform drawn once into a bitmap, so dragging only repaints the handles.
        Bitmap StripImage(Rectangle r)
        {
            object key = IsAudio ? (object)Wave : new object[] { Strip, Wave };
            int filled = 0; if (Strip != null) foreach (Image i in Strip) if (i != null) filled++;
            key = new object[] { key, filled, Theme.Dark };
            if (stripCache != null && cacheW == r.Width && cacheH == r.Height && Equals2(cacheKey, key)) return stripCache;
            if (stripCache != null) stripCache.Dispose();
            stripCache = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            cacheW = r.Width; cacheH = r.Height; cacheKey = key;
            using (Graphics g = Graphics.FromImage(stripCache))
            {
                g.Clear(Theme.Dark ? Color.FromArgb(24, 25, 28) : Color.FromArgb(222, 225, 230));
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                if (Strip != null && !IsAudio)
                {
                    float cw = r.Width / (float)Strip.Length;
                    for (int i = 0; i < Strip.Length; i++)
                    {
                        Image im = Strip[i];
                        if (im == null) continue;
                        RectangleF cell = new RectangleF(i * cw, 0, cw + 1, r.Height);
                        float sc = Math.Max(cell.Width / im.Width, cell.Height / im.Height);
                        float w = im.Width * sc, h = im.Height * sc;
                        GraphicsState st = g.Save(); g.SetClip(cell);
                        g.DrawImage(im, cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h);
                        g.Restore(st);
                    }
                }
                if (Wave != null && IsAudio) g.DrawImage(Wave, new Rectangle(0, 0, r.Width, r.Height));
                if (Wave != null && !IsAudio)
                {
                    // the sound, as a band along the bottom of the film strip
                    int bh = Math.Max(8, r.Height * 34 / 100);
                    Rectangle band = new Rectangle(0, r.Height - bh, r.Width, bh);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 10, 11, 13))) g.FillRectangle(b, band);
                    g.DrawImage(Wave, new Rectangle(band.X, band.Y + 2, band.Width, band.Height - 4));
                }
            }
            return stripCache;
        }
        static bool Equals2(object a, object b)
        {
            object[] x = a as object[], y = b as object[];
            if (x == null || y == null || x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (!Equals(x[i], y[i])) return false;
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Rectangle r = Track;
            using (GraphicsPath clip = Theme.Round(r, Theme.S(10)))
            {
                GraphicsState st = g.Save();
                g.SetClip(clip);
                g.DrawImageUnscaled(StripImage(r), r.X, r.Y);
                if (!ReadOnly)
                    using (SolidBrush dim = new SolidBrush(Color.FromArgb(Theme.Dark ? 170 : 150, Theme.Dark ? 10 : 245, Theme.Dark ? 11 : 246, Theme.Dark ? 13 : 248)))
                    {
                        g.FillRectangle(dim, r.X, r.Y, X(Start) - r.X, r.Height);
                        g.FillRectangle(dim, X(End), r.Y, r.Right - X(End), r.Height);
                    }
                g.Restore(st);
            }
            Theme.StrokeRound(g, r, Theme.S(10), Theme.CardBorder);

            Color sel = Theme.Dark ? Color.FromArgb(240, 241, 243) : Color.FromArgb(30, 33, 38);
            if (!ReadOnly)
            {
                float xs = X(Start), xe = X(End);
                using (Pen p = new Pen(sel, Theme.S(3)))
                {
                    g.DrawLine(p, xs, r.Y + 1, xe, r.Y + 1);
                    g.DrawLine(p, xs, r.Bottom - 1, xe, r.Bottom - 1);
                }
                for (int k = 0; k < 2; k++)
                {
                    float hx = k == 0 ? xs : xe;
                    bool hot2 = hoverHandle == k + 1 || drag == k + 1;
                    float hw = Theme.S(hot2 ? 14 : 12);
                    RectangleF h = new RectangleF(hx - hw / 2, r.Y - Theme.S(2), hw, r.Height + Theme.S(4));
                    Theme.FillRound(g, h, Theme.S(4), sel);
                    using (Pen grip = new Pen(Theme.Dark ? Color.FromArgb(40, 42, 46) : Color.White, Theme.S(1.5f)))
                    {
                        float cy = r.Y + r.Height / 2f;
                        g.DrawLine(grip, hx - Theme.S(1.5f), cy - Theme.S(8), hx - Theme.S(1.5f), cy + Theme.S(8));
                        g.DrawLine(grip, hx + Theme.S(1.5f), cy - Theme.S(8), hx + Theme.S(1.5f), cy + Theme.S(8));
                    }
                }
            }
            // ticks
            Font f = Theme.F(8f);
            float phx = Playhead >= 0 ? X(Playhead) : -9999;
            for (int i = 0; i <= 4; i++)
            {
                double t = Duration * i / 4;
                float x = X(t);
                if (Math.Abs(x - phx) < Theme.S(48)) continue;   // the playhead label sits here
                Rectangle tr = new Rectangle((int)x - Theme.S(40), r.Bottom + Theme.S(6), Theme.S(80), Theme.S(16));
                if (i == 0) tr.X = (int)x - Theme.S(4); if (i == 4) tr.X = (int)x - Theme.S(76);
                TextRenderer.DrawText(g, Theme.Time(t), f, tr, Theme.InkFaint, (i == 0 ? TextFormatFlags.Left : i == 4 ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter) | TextFormatFlags.NoPrefix);
            }
            // playhead: where you are in the video
            if (Playhead >= 0)
            {
                Color ph = Color.FromArgb(239, 68, 68);
                using (Pen p = new Pen(Color.FromArgb(120, 0, 0, 0), Theme.S(4))) g.DrawLine(p, phx, r.Y - Theme.S(2), phx, r.Bottom + Theme.S(2));
                using (Pen p = new Pen(ph, Theme.S(2))) g.DrawLine(p, phx, r.Y - Theme.S(2), phx, r.Bottom + Theme.S(2));
                string tl = Theme.Time(Playhead, true);
                Font bf = Theme.F(8f, FontStyle.Bold);
                int tw = Theme.Measure(tl, bf) + Theme.S(12);
                Rectangle pill = new Rectangle((int)phx - tw / 2, r.Bottom + Theme.S(4), tw, Theme.S(18));
                pill.X = Math.Max(0, Math.Min(Width - tw, pill.X));
                Theme.FillRound(g, pill, Theme.S(9), ph);
                Theme.Text(g, tl, bf, Color.White, pill, Align.Center, true, false, true);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing && stripCache != null) stripCache.Dispose(); base.Dispose(disposing); }
    }

    public class FrameView : UiControl
    {
        public Image Frame;
        public string Caption = "";
        public bool Loading;
        public bool IsAudio;
        public FrameView() { }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath clip = Theme.Round(r, Theme.S(12)))
            {
                GraphicsState st = g.Save();
                g.SetClip(clip);
                g.Clear(Color.FromArgb(14, 15, 17));
                if (Frame != null && !IsAudio)
                {
                    float sc = Math.Min(r.Width / Frame.Width, r.Height / Frame.Height);
                    float w = Frame.Width * sc, h = Frame.Height * sc;
                    g.DrawImage(Frame, (r.Width - w) / 2, (r.Height - h) / 2, w, h);
                }
                else if (IsAudio)
                {
                    using (LinearGradientBrush b = new LinearGradientBrush(r, Color.FromArgb(52, 56, 62), Color.FromArgb(20, 21, 24), LinearGradientMode.Vertical)) g.FillRectangle(b, r);
                    Theme.Glyph(g, Ico.Music, 40f, Color.FromArgb(200, 204, 210), Rectangle.Round(r));
                }
                g.Restore(st);
            }
            if (!string.IsNullOrEmpty(Caption))
            {
                Font f = Theme.F(10f, FontStyle.Bold);
                int w = Theme.Measure(Caption, f) + Theme.S(20);
                Rectangle cr = new Rectangle(Theme.S(12), Height - Theme.S(42), w, Theme.S(30));
                Theme.FillRound(g, cr, Theme.S(8), Color.FromArgb(170, 0, 0, 0));
                Theme.Text(g, Caption, f, Color.White, cr, Align.Center, true, false, true);
            }
        }
    }

    /// One piece of a source file on the Studio's timeline (several clips can share a file after splitting).
    public class Clip
    {
        public MediaSource Src;
        public double Start, End;
        public string File { get { return Src.File; } }
        public MediaInfo Info { get { return Src.Info; } }
        public int W { get { return Src.W; } }
        public int H { get { return Src.H; } }
        public Image[] Strip { get { return Src.Strip; } set { Src.Strip = value; } }
        public Image Wave { get { return Src.Wave; } set { Src.Wave = value; } }
        public double Length { get { return Math.Max(0, End - Start); } }
        public Image Thumb { get { if (Strip != null) foreach (Image i in Strip) if (i != null) return i; return null; } }
        public Clip Copy() { return new Clip { Src = Src, Start = Start, End = End }; }
        public void Dispose() { }   // the source's pictures are freed when the project closes
    }

    /// Storyboard under the timeline: one chip per clip plus "Add clip".
    public class ClipStrip : UiControl
    {
        public List<Clip> Clips;
        public int Selected;
        public bool HasTransition;
        int hover = -1; bool hoverX;
        public event Action<int> Pick, Remove;
        public event Action Add, PickAll;
        public bool ShowAll { get { return Count > 1; } }   // a "Full video" chip first, to watch everything joined
        public ClipStrip() { Cursor = Cursors.Hand; }
        int Lead { get { return ShowAll ? 1 : 0; } }
        int Gap { get { return Theme.S(14); } }
        int AddW { get { return Theme.S(Count > 1 ? 58 : 150); } }
        int ChipW { get { int n = Count + Lead; return Math.Max(Theme.S(120), Math.Min(Theme.S(210), (Width - AddW - n * Gap) / Math.Max(1, n))); } }
        Rectangle Chip(int i)
        {
            int x = (i + Lead) * (ChipW + Gap);
            int w = i == Count ? AddW : ChipW;
            return Theme.Mirror(new Rectangle(x, Theme.S(2), w, Height - Theme.S(4)), Width);
        }
        public string HoverName { get { return hover >= 0 && hover < Count ? Path.GetFileName(Clips[hover].File) : null; } }
        Rectangle XRect(Rectangle c) { int s = Theme.S(20); return Theme.RTL ? new Rectangle(c.X + Theme.S(4), c.Y + Theme.S(4), s, s) : new Rectangle(c.Right - s - Theme.S(4), c.Y + Theme.S(4), s, s); }
        int Count { get { return Clips == null ? 0 : Clips.Count; } }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; bool hx = false;
            if (ShowAll && Chip(-1).Contains(e.Location)) h = -2;
            for (int i = 0; i <= Count; i++) if (Chip(i).Contains(e.Location)) { h = i; hx = i < Count && Count > 1 && XRect(Chip(i)).Contains(e.Location); }
            if (h != hover || hx != hoverX) { hover = h; hoverX = hx; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            if (ShowAll && Chip(-1).Contains(e.Location)) { if (PickAll != null) PickAll(); return; }
            for (int i = 0; i <= Count; i++)
                if (Chip(i).Contains(e.Location))
                {
                    if (i == Count) { if (Add != null) Add(); }
                    else if (Count > 1 && XRect(Chip(i)).Contains(e.Location)) { if (Remove != null) Remove(i); }
                    else if (Pick != null) Pick(i);
                    return;
                }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            if (ShowAll)
            {
                Rectangle c0 = Chip(-1);
                RectangleF r0 = new RectangleF(c0.X + 0.5f, c0.Y + 0.5f, c0.Width - 1, c0.Height - 1);
                bool sel0 = Selected < 0;
                if (sel0) Theme.FillRoundGradient(g, r0, Theme.S(10), Theme.PrimaryTop, Theme.PrimaryBottom);
                else { Theme.FillRound(g, r0, Theme.S(10), hover == -2 ? Theme.Hover : Theme.CardInner); Theme.StrokeRound(g, r0, Theme.S(10), Theme.CardBorder); }
                Color ink = sel0 ? Theme.PrimaryInk : Theme.Ink;
                Rectangle gl = Theme.Mirror(new Rectangle(c0.X + Theme.S(10), c0.Y, Theme.S(22), c0.Height), Width);
                if (Theme.RTL) gl = new Rectangle(c0.Right - Theme.S(32), c0.Y, Theme.S(22), c0.Height); else gl = new Rectangle(c0.X + Theme.S(10), c0.Y, Theme.S(22), c0.Height);
                Theme.Glyph(g, Ico.Play, 10f, ink, gl);
                Rectangle tr0 = Theme.RTL ? new Rectangle(c0.X + Theme.S(6), c0.Y + Theme.S(8), c0.Width - Theme.S(44), Theme.S(18)) : new Rectangle(c0.X + Theme.S(38), c0.Y + Theme.S(8), c0.Width - Theme.S(44), Theme.S(18));
                Theme.Text(g, L.T("clip_all"), Theme.F(8.5f, FontStyle.Bold), ink, tr0, Align.Start);
                double tot = 0; foreach (Clip cc in Clips) tot += cc.Length;
                Theme.Text(g, L.T("clip_all_sub", Count) + "  ·  " + Theme.Time(tot), Theme.F(8f), sel0 ? ink : Theme.InkSoft, new Rectangle(tr0.X, c0.Y + Theme.S(28), tr0.Width, Theme.S(16)), Align.Start);
            }
            for (int i = 0; i <= Count; i++)
            {
                Rectangle c = Chip(i);
                RectangleF r = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1);
                if (i == Count)
                {
                    // "add a clip"
                    using (Pen p = new Pen(hover == i ? Theme.InkSoft : Theme.InkFaint, 1.3f) { DashStyle = DashStyle.Dash })
                    using (GraphicsPath gp = Theme.Round(r, Theme.S(10))) g.DrawPath(p, gp);
                    Theme.Text(g, Count > 1 ? "+" : "+  " + L.T("clip_add"), Theme.F(Count > 1 ? 13f : 9f, FontStyle.Bold), hover == i ? Theme.Ink : Theme.InkSoft, c, Align.Center);
                    continue;
                }
                Clip clip = Clips[i];
                bool sel = i == Selected;
                Theme.FillRound(g, r, Theme.S(10), sel ? Theme.Selected : (hover == i ? Theme.Hover : Theme.CardInner));
                Theme.StrokeRound(g, r, Theme.S(10), sel ? Theme.Ink : Theme.CardBorder, sel ? 1.6f : 1f);
                int tw0 = Math.Max(Theme.S(40), Math.Min(Theme.S(64), c.Width * 30 / 100));
                Rectangle th = Theme.RTL ? new Rectangle(c.Right - tw0 - Theme.S(6), c.Y + Theme.S(6), tw0, c.Height - Theme.S(12)) : new Rectangle(c.X + Theme.S(6), c.Y + Theme.S(6), tw0, c.Height - Theme.S(12));
                using (GraphicsPath clip2 = Theme.Round(th, Theme.S(6)))
                {
                    GraphicsState st = g.Save(); g.SetClip(clip2, CombineMode.Intersect);
                    g.Clear(Color.FromArgb(20, 21, 24));
                    Image im = clip.Thumb;
                    if (im != null) { float sc = Math.Max(th.Width / (float)im.Width, th.Height / (float)im.Height); g.DrawImage(im, th.X + (th.Width - im.Width * sc) / 2, th.Y + (th.Height - im.Height * sc) / 2, im.Width * sc, im.Height * sc); }
                    else Theme.Glyph(g, clip.Info.HasVideo ? Ico.Video : Ico.Music, 11f, Color.FromArgb(180, 255, 255, 255), th);
                    g.Restore(st);
                }
                Rectangle badge = new Rectangle(th.X + Theme.S(3), th.Y + Theme.S(3), Theme.S(18), Theme.S(16));
                Theme.FillRound(g, badge, Theme.S(5), Color.FromArgb(190, 0, 0, 0));
                Theme.Text(g, (i + 1).ToString(), Theme.F(7.5f, FontStyle.Bold), Color.White, badge, Align.Center, true, false, true);
                int tx = Theme.RTL ? c.X + Theme.S(8) : th.Right + Theme.S(8), tw = c.Width - th.Width - Theme.S(22) - (Count > 1 ? Theme.S(18) : 0);
                if (Theme.RTL && Count > 1) tx += Theme.S(18);
                Theme.Text(g, Path.GetFileName(clip.File), Theme.F(8.5f, FontStyle.Bold), Theme.Ink, new Rectangle(tx, c.Y + Theme.S(8), tw, Theme.S(18)), Align.Start);
                Theme.Text(g, Theme.Time(clip.Length, true), Theme.F(8f), Theme.InkSoft, new Rectangle(tx, c.Y + Theme.S(28), tw, Theme.S(16)), Align.Start, true, false, true);
                if (Count > 1)
                {
                    Rectangle xr = XRect(c);
                    if (hover == i && hoverX) Theme.FillRound(g, xr, xr.Width / 2f, Theme.ErrorBg);
                    Theme.Glyph(g, Ico.Close, 7f, hover == i && hoverX ? Theme.Error : Theme.InkFaint, xr);
                }
                if (i < Count - 1)
                {
                    // the join between clips: a small "transition" dot when one is chosen
                    int jx = Theme.RTL ? c.X - Gap / 2 : c.Right + Gap / 2;
                    Rectangle j = new Rectangle(jx - Theme.S(7), c.Y + c.Height / 2 - Theme.S(7), Theme.S(14), Theme.S(14));
                    if (HasTransition) { Theme.FillRound(g, j, Theme.S(7), Theme.Ink); Theme.Glyph(g, "", 6f, Theme.Card, j); }
                    else using (SolidBrush b = new SolidBrush(Theme.InkFaint)) g.FillEllipse(b, j.X + Theme.S(4), j.Y + Theme.S(4), Theme.S(6), Theme.S(6));
                }
            }
        }
    }

    /// Video & audio side of the Studio: clips, trim, text on top, transitions, crop and resize.
    public partial class TrimPage : Page
    {
        Rectangle main, side, emptyCard;
        Rectangle infoR, fieldsLblR, modeLblR, accLblR, accHintR, statusR, fxLblR, fxDurR, fxHintR, fxInR, fxOutR;
        DropZone drop;
        ImageCanvas view;
        Track timeline;
        Btn tbAdd, tbSplit, tbDelete, tbDup, tbUndo, tbRedo, tbPreview, tbProject, tbCaptions;
        bool subsAuto;   // the subtitles were made by auto-captions
        readonly ToolTip tbTips = new ToolTip();
        readonly List<MediaSource> sources = new List<MediaSource>();
        int playToken;
        bool tbCompact;
        const int StripFrames = 24;
        EditorPanel editor;
        TextField fStart, fEnd;
        Segments mode, accuracy;
        Btn btnPlay, btnSave, btnOther, btnShow;
        ChipGrid transition;
        Slider fxDur = new Slider { Min = 0.3f, Max = 2f, Value = 0.8f };
        Toggle fadeIn = new Toggle(), fadeOut = new Toggle();
        Slider volume = new Slider { Min = 0, Max = 200, Value = 100 };
        Rectangle fxVolR;
        // speed, direction, music, subtitles, GIF
        static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };
        readonly Segments speedSeg = new Segments(() => "0.5×", () => "0.75×", () => "1×", () => "1.25×", () => "1.5×", () => "2×");
        readonly Toggle reverse = new Toggle();
        readonly Segments musicMode = new Segments(() => L.T("fx_music_mix"), () => L.T("fx_music_replace"));
        readonly Segments subsSize = new Segments(() => L.T("fx_small"), () => L.T("fx_medium"), () => L.T("fx_large"));
        readonly Segments saveAs = new Segments(() => L.T("fx_as_video"), () => L.T("fx_as_gif"));
        readonly Slider musicVol = new Slider { Min = 0, Max = 200, Value = 80 };
        Btn btnMusic, btnMusicX, btnSubs, btnSubsX, btnFxPreview;
        string musicFile, subsFile;
        Rectangle fxSpeedR, fxRevR, fxMusicLblR, fxMusicNameR, fxMusVolR, fxSubsLblR, fxSubsNameR, fxSubsHintR, fxSaveAsR, fxLineR;
        // "Full video": the clips joined (with transitions) as a quick preview you can play
        bool seqMode; string seqFile, seqKey; double seqDur; bool seqRendering;
        DateTime lastFrameReq = DateTime.MinValue;
        Progress progress;
        readonly List<Clip> clips = new List<Clip>();
        int cur;
        bool running, cancel;
        string statusText = ""; Theme.LiveColor statusColor;
        string lastOutput;
        readonly PreviewPlayer player = new PreviewPlayer();
        bool framePending;
        public event Action<string> OpenImage;      // a photo was dropped here: the Studio shows it in the photo editor

        static readonly string[] TransKeys = { "none", "fade", "fadeblack", "slideleft", "wipeleft", "circleopen" };
        string[] TransNames { get { return new[] { L.T("fx_none"), L.T("fx_fade"), L.T("fx_black"), L.T("fx_slide"), L.T("fx_wipe"), L.T("fx_circle") }; } }

        readonly object frameLock = new object();
        double frameReq = -1; bool frameThread;
        int loadToken;

        Clip C { get { return clips.Count > 0 ? clips[Math.Min(cur, clips.Count - 1)] : null; } }
        string file { get { return C != null ? C.File : null; } }
        MediaInfo info { get { return C != null ? C.Info : null; } }

        public TrimPage() : base("studio_title", "studio_sub")
        {
            drop = new DropZone(() => L.T("studio_drop"), () => L.T("studio_drop_sub")) { Tips = new[] { "tip_studio_layer", "tip_studio_pdf", "tip_ocr", "tip_ctrl_tab" } };
            drop.Glyph = Ico.Video;
            drop.Click += (s, e) => Browse(false);
            view = new ImageCanvas { Dark = true };
            view.BackgroundClick += TogglePlay;
            timeline = new Track { Clips = clips };
            timeline.Seek += (t, finished) =>
            {
                StopPlay();
                if (finished) { PlayFrom(t); return; }
                view.Caption = Theme.Time(t, true) + "  /  " + Theme.Time(timeline.Total, true);
                if (!seqMode)
                {
                    double local; int i = timeline.ClipAt(t, out local);
                    if (i >= 0 && i != cur) { cur = i; SyncFields(); editor.DoLayout(); UpdateToolbar(); }
                    if ((DateTime.Now - lastFrameReq).TotalMilliseconds > 120) { lastFrameReq = DateTime.Now; RequestFrame(local, false, false); }
                }
                view.Invalidate();
            };
            timeline.Picked += i => { if (seqMode || i < 0 || i >= clips.Count) return; cur = i; SyncFields(); editor.DoLayout(); UpdateToolbar(); };
            timeline.Trimmed += (i, edge, final) =>
            {
                StopPlay();
                cur = i;
                // while dragging, fetch a new picture at most ~8 times a second so the handle stays smooth
                if (final || (DateTime.Now - lastFrameReq).TotalMilliseconds > 120)
                {
                    lastFrameReq = DateTime.Now;
                    SyncFields();
                    RequestFrame(edge == 2 ? C.End : C.Start, edge == 2);
                }
                if (final) { editor.S.Dirty = true; timeline.Playhead = Math.Min(timeline.Playhead, timeline.Total); timeline.Changed(); UpdateToolbar(); }
            };
            timeline.Moved += (from, to) =>
            {
                StopPlay();
                Clip c = clips[from]; clips.RemoveAt(from); clips.Insert(Math.Min(to, clips.Count), c);
                cur = clips.IndexOf(c); timeline.Selected = cur;
                editor.S.Dirty = true;
                timeline.Changed(); SyncFields(); UpdateToolbar();
                ShowToast(L.T("tl_moved"), true, null, null);
            };
            tbAdd = new Btn(() => tbCompact ? "" : L.T("clip_add"), Ico.Add, BtnKind.Secondary);
            tbAdd.MouseEnter += (s, e) => tbTips.SetToolTip(tbAdd, L.T("clip_add"));
            tbAdd.Click += (s, e) => Browse(true);
            tbSplit = new Btn(() => tbCompact ? "" : L.T("tl_split"), Ico.Cut, BtnKind.Secondary);
            tbSplit.Click += (s, e) => SplitAtPlayhead();
            tbSplit.MouseEnter += (s, e) => tbTips.SetToolTip(tbSplit, L.T("tl_split_tip"));
            tbDelete = new Btn(() => tbCompact ? "" : L.T("tl_delete"), Ico.Delete, BtnKind.Secondary);
            tbDelete.Click += (s, e) => DeleteClip(cur);
            tbDelete.MouseEnter += (s, e) => tbTips.SetToolTip(tbDelete, L.T("tl_delete_tip"));
            tbDup = new Btn(() => "", Ico.Copy, BtnKind.Secondary);
            tbDup.Click += (s, e) => DuplicateClip();
            tbDup.MouseEnter += (s, e) => tbTips.SetToolTip(tbDup, L.T("tl_duplicate_tip"));
            tbUndo = new Btn(() => "", "\uE7A7", BtnKind.Secondary) { Enabled = false };
            tbUndo.Click += (s, e) => { StopPlay(); editor.Undo(); };
            tbUndo.MouseEnter += (s, e) => tbTips.SetToolTip(tbUndo, L.T("undo_tip"));
            tbRedo = new Btn(() => "", "\uE7A6", BtnKind.Secondary) { Enabled = false };
            tbRedo.Click += (s, e) => { StopPlay(); editor.Redo(); };
            tbRedo.MouseEnter += (s, e) => tbTips.SetToolTip(tbRedo, L.T("redo_tip"));
            tbPreview = new Btn(() => seqMode ? L.T("tl_back") : L.T(tbCompact ? "tl_preview_short" : "tl_preview"), Ico.Play, BtnKind.Ghost);
            tbPreview.Click += (s, e) => { if (seqMode) ExitSequence(); else EnterSequence(true); };
            tbProject = new Btn(() => tbCompact ? "" : L.T("proj_btn"), "\uE7C4", BtnKind.Secondary);
            tbProject.MouseEnter += (s, e) => tbTips.SetToolTip(tbProject, L.T("proj_tip"));
            tbProject.Click += (s, e) => ShowProjectMenu(tbProject);
            tbCaptions = new Btn(() => "", "\uE8BE", BtnKind.Secondary);
            tbCaptions.MouseEnter += (s, e) => tbTips.SetToolTip(tbCaptions, L.T(captioning ? "cap_stop_tip" : "cap_tip"));
            tbCaptions.Click += (s, e) => MakeCaptions();
            foreach (Btn tb in new[] { tbAdd, tbSplit, tbDelete, tbDup, tbUndo, tbRedo, tbPreview, tbProject, tbCaptions }) tb.FontSize = 9f;
            fStart = new TextField(() => "0:00.0", Ico.Clock) { ForceLtr = true };
            fEnd = new TextField(() => "0:00.0", Ico.Clock) { ForceLtr = true };
            foreach (TextField tf in new[] { fStart, fEnd })
            {
                TextField f = tf;
                f.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyField(f); } };
                f.Box.Leave += (s, e) => ApplyField(f);
            }
            mode = new Segments(() => L.T("keep_sel"), () => L.T("remove_sel"));
            mode.SelectedChanged += (s, e) => { if (mode.SelectedIndex == 1) accuracy.SetSilently(0); accuracy.Enabled = mode.SelectedIndex == 0 && !running; editor.Body.Invalidate(); };
            accuracy = new Segments(() => L.T("acc_precise"), () => L.T("acc_fast"));
            accuracy.SelectedChanged += (s, e) => editor.Body.Invalidate();
            btnPlay = new Btn(() => player.Playing ? L.T("pause") : L.T("play_sel"), Ico.Play, BtnKind.Secondary);
            btnPlay.Click += (s, e) => TogglePlay();
            btnSave = new Btn(() => running ? L.T("cancel") : L.T(info != null && !info.HasVideo ? "save_clip" : saveAs.SelectedIndex == 1 ? "save_gif" : "save_video"), Ico.Save, BtnKind.Primary);
            btnSave.Click += (s, e) => { if (running) cancel = true; else Save(); };
            btnOther = new Btn(() => L.T("open_other"), Ico.FolderOpen, BtnKind.Ghost);
            btnOther.Click += (s, e) => Browse(false);
            btnShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Secondary);
            btnShow.Visible = false;
            btnShow.Click += (s, e) => { if (lastOutput != null) try { Process.Start("explorer.exe", "/select,\"" + lastOutput + "\""); } catch { } };
            progress = new Progress();
            progress.Visible = false;

            transition = new ChipGrid { Columns = 2, RowH = 34 };
            transition.SetItems(TransNames, TransNames[0]);
            transition.SelectedChanged += (s, e) => editor.Body.Invalidate();
            fxDur.Format = v => v.ToString("0.0") + " s";
            volume.Format = v => ((int)Math.Round(v)) + "%";
            volume.Committed += (s, e) => { editor.S.Dirty = true; editor.Body.Invalidate(); };

            speedSeg.SetSilently(2); speedSeg.FontSize = 8.5f;
            speedSeg.SelectedChanged += (s, e) => FxChanged();
            reverse.CheckedChanged += (s, e) => FxChanged();
            musicMode.FontSize = 8.75f; musicMode.SelectedChanged += (s, e) => FxChanged();
            subsSize.FontSize = 8.75f; subsSize.SetSilently(1); subsSize.SelectedChanged += (s, e) => FxChanged();
            saveAs.FontSize = 8.75f; saveAs.SelectedChanged += (s, e) => { FxChanged(); btnSave.Invalidate(); };
            musicVol.Format = v => ((int)Math.Round(v)) + "%";
            musicVol.Committed += (s, e) => FxChanged();
            btnMusic = new Btn(() => L.T("fx_add_music"), Ico.Music, BtnKind.Secondary);
            btnMusic.Click += (s, e) => PickMusic();
            btnMusicX = new Btn(() => "", Ico.Close, BtnKind.Ghost);
            btnMusicX.Click += (s, e) => { musicFile = null; FxChanged(); editor.DoLayout(); };
            btnSubs = new Btn(() => L.T("fx_add_subs"), Ico.Text, BtnKind.Secondary);
            btnSubs.Click += (s, e) => PickSubs();
            btnSubsX = new Btn(() => "", Ico.Close, BtnKind.Ghost);
            btnSubsX.Click += (s, e) => { subsFile = null; subsAuto = false; FxChanged(); editor.DoLayout(); };
            btnFxPreview = new Btn(() => L.T("fx_preview"), Ico.Play, BtnKind.Secondary);
            btnFxPreview.Click += (s, e) => EnterSequence(true);

            editor = new EditorPanel { Canvas = view, ShowUndo = false };
            editor.HistoryChanged += (u, r) => { tbUndo.Enabled = u; tbRedo.Enabled = r; tbUndo.Invalidate(); tbRedo.Invalidate(); };
            editor.AddToBody(fStart, fEnd, mode, accuracy, btnPlay, btnOther);
            editor.AddToEffects(transition, fxDur, fadeIn, fadeOut, volume, speedSeg, reverse, musicMode, subsSize, saveAs, musicVol, btnMusic, btnMusicX, btnSubs, btnSubsX, btnFxPreview);
            editor.Controls.AddRange(new Control[] { btnSave, btnShow, progress });
            editor.TrimLayout = TrimLayout;
            editor.TrimPaint = TrimPaint;
            editor.EffectsLayout = EffectsLayout;
            editor.EffectsPaint = EffectsPaint;
            editor.FooterLayout = FooterLayout;
            editor.FooterPaint = g => Theme.Text(g, statusText, Theme.F(9f), statusColor, Theme.Mirror(statusR, editor.Width), Align.Start, false, true);
            editor.TrimFocused = () => fStart.Box.Focused || fEnd.Box.Focused;
            editor.Notify = (msg, ok, f) => ShowToast(msg, ok, null, null);
            editor.CropChanged += () => { if (C != null) { double local; timeline.ClipAt(timeline.Playhead, out local); RequestFrame(local, false, false); } };
            editor.TabChanged += () => { if (editor.Visible) view.ShowPlay = !player.Playing && info != null && info.HasVideo; view.Invalidate(); };
            // undo also covers the clips: their order, splits, deletions and trims
            editor.ExtraKey = () =>
            {
                StringBuilder k = new StringBuilder();
                foreach (Clip c in clips) k.Append(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c.Src)).Append(':').Append(Math.Round(c.Start, 2)).Append('-').Append(Math.Round(c.End, 2)).Append(';');
                return k.ToString();
            };
            editor.CaptureExtra = () =>
            {
                List<Clip> l = new List<Clip>(); foreach (Clip c in clips) l.Add(c.Copy());
                return new object[] { l, cur };
            };
            editor.RestoreExtra = o =>
            {
                object[] a = o as object[];
                if (a == null) return;
                List<Clip> l = (List<Clip>)a[0];
                if (l.Count == 0) return;
                if (seqMode) { seqMode = false; timeline.Sequence = false; }
                clips.Clear(); foreach (Clip c in l) clips.Add(c.Copy());
                cur = Math.Max(0, Math.Min((int)a[1], clips.Count - 1));
                RefreshTrack();
                RequestFrame(C.Start, false);
            };
            view.LayerPicked += i => editor.Select(i);
            view.LayerEdit += () => editor.FocusText();
            view.LayerMoved += () => editor.S.Dirty = true;

            Controls.AddRange(new Control[] { drop, view, timeline, editor, tbAdd, tbSplit, tbDelete, tbDup, tbUndo, tbRedo, tbPreview, tbProject, tbCaptions });
            EnableDrop(drop, view, timeline);
            ShowEmpty(true);
            InitProjects();
            InitTimedText(); InitVideoBg();
            InitCaptions();
        }

        int TransIndex { get { return Math.Max(0, Array.IndexOf(TransNames, transition.Selected)); } }

        public override bool Busy { get { return running; } }
        public bool HasFile { get { return clips.Count > 0; } }
        public bool HasUnsavedText { get { return clips.Count > 0 && editor.S.Dirty && (editor.S.HasText || clips.Count > 1); } }
        public bool TextFocused { get { return editor.TextFocused; } }

        void ShowEmpty(bool empty)
        {
            drop.Visible = empty;
            foreach (Control c in new Control[] { view, timeline, editor, tbAdd, tbSplit, tbDelete, tbDup, tbUndo, tbRedo, tbPreview, tbProject, tbCaptions }) c.Visible = !empty;
            if (empty) { btnShow.Visible = false; progress.Visible = false; }
            DoLayout();
        }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, gap = Theme.S(20), sideW = Math.Min(Theme.S(372), W / 2);
            cards.Clear();
            if (clips.Count == 0)
            {
                emptyCard = new Rectangle(p, Top0, W - p * 2, H - Top0 - p);
                cards.Add(emptyCard);
                Place(drop, new Rectangle(emptyCard.X + Theme.S(20), emptyCard.Y + Theme.S(20), emptyCard.Width - Theme.S(40), emptyCard.Height - Theme.S(40)));
                Invalidate();
                return;
            }
            main = new Rectangle(p, Top0, W - p * 2 - sideW - gap, H - Top0 - p);
            side = new Rectangle(main.Right + gap, Top0, sideW, H - Top0 - p);
            cards.Add(main); cards.Add(side);
            int ip = Theme.S(16);
            int tlH = Theme.S(timeline.HasTexts ? 126 : 100), tbH = Theme.S(36);
            hadTexts = timeline.HasTexts;
            Place(view, new Rectangle(main.X + ip, main.Y + ip, main.Width - ip * 2, main.Height - ip * 2 - tlH - tbH - Theme.S(24)));
            // toolbar: add · split · delete · duplicate · undo · redo ... preview
            int ty = main.Bottom - ip - tlH - tbH - Theme.S(4), tx = main.X + ip, gapb = Theme.S(6);
            // not enough room for words: icons only (the tooltips still say what each one does)
            tbCompact = false;
            int need = Math.Max(Theme.S(96), tbAdd.PreferredWidth) + Math.Max(Theme.S(84), tbSplit.PreferredWidth) + Math.Max(Theme.S(84), tbDelete.PreferredWidth) + tbH * 4 + gapb * 8 + Theme.S(8) + Math.Max(Theme.S(150), tbPreview.PreferredWidth) + Math.Max(Theme.S(100), tbProject.PreferredWidth);
            if (need > main.Width - ip * 2) tbCompact = true;
            Func<Btn, int, int> put = (b, w) => { Place(b, new Rectangle(tx, ty, w, tbH)); tx += w + gapb; return w; };
            put(tbAdd, tbCompact ? tbH : Math.Max(Theme.S(96), tbAdd.PreferredWidth));
            put(tbSplit, tbCompact ? tbH : Math.Max(Theme.S(84), tbSplit.PreferredWidth));
            put(tbDelete, tbCompact ? tbH : Math.Max(Theme.S(84), tbDelete.PreferredWidth));
            put(tbDup, tbH);
            tx += Theme.S(8);
            put(tbUndo, tbH); put(tbRedo, tbH);
            int pw = Math.Max(Theme.S(tbCompact ? 100 : 150), tbPreview.PreferredWidth);
            Place(tbPreview, new Rectangle(main.Right - ip - pw, ty, pw, tbH));
            int jw = tbCompact ? tbH : Math.Max(Theme.S(100), tbProject.PreferredWidth);
            Place(tbProject, new Rectangle(main.Right - ip - pw - gapb - jw, ty, jw, tbH));
            tbCaptions.Visible = clips.Count > 0 && clips[0].Info.HasVideo;
            Place(tbCaptions, new Rectangle(main.Right - ip - pw - gapb - jw - gapb - tbH, ty, tbH, tbH));
            Place(timeline, new Rectangle(main.X + ip - Theme.S(14), main.Bottom - ip - tlH, main.Width - ip * 2 + Theme.S(28), tlH));
            Place(editor, Rectangle.Inflate(side, -Theme.S(4), -Theme.S(6)));
            editor.DoLayout();
            Invalidate();
        }

        int TrimLayout(int y, int w)
        {
            int x = editor.Pad;
            infoR = new Rectangle(x, y, w, Theme.S(48)); y += Theme.S(58);
            fieldsLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
            int fw = (w - Theme.S(10)) / 2;
            editor.BodyPlace(fStart, new Rectangle(x, y, fw, Theme.S(40)));
            editor.BodyPlace(fEnd, new Rectangle(x + fw + Theme.S(10), y, fw, Theme.S(40)));
            y += Theme.S(52);
            bool one = clips.Count <= 1;
            foreach (Control c in new Control[] { fStart, fEnd, btnPlay, btnOther }) c.Visible = true;
            mode.Visible = accuracy.Visible = one;
            if (one)
            {
                modeLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
                editor.BodyPlace(mode, new Rectangle(x, y, w, Theme.S(36))); y += Theme.S(46);
                accLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
                editor.BodyPlace(accuracy, new Rectangle(x, y, w, Theme.S(36))); y += Theme.S(40);
            }
            else modeLblR = accLblR = Rectangle.Empty;
            accHintR = new Rectangle(x, y, w, Theme.S(40)); y += Theme.S(46);
            editor.BodyPlace(btnPlay, new Rectangle(x, y, w, Theme.S(40))); y += Theme.S(48);
            editor.BodyPlace(btnOther, new Rectangle(x, y, w, Theme.S(36))); y += Theme.S(40);
            return y;
        }

        Rectangle fxBgLblR;
        int EffectsLayout(int y, int w)
        {
            int x = editor.Pad, lw = Theme.S(78), rh = Theme.S(34);
            bool vid = info != null && info.HasVideo;
            foreach (Control c in new Control[] { transition, fxDur, fadeIn, fadeOut, volume, speedSeg, reverse, btnFxPreview }) c.Visible = true;
            fxVolR = new Rectangle(x, y, lw, rh);
            editor.BodyPlace(volume, new Rectangle(x + lw, y, w - lw, rh)); y += Theme.S(44);
            fxSpeedR = new Rectangle(x, y, lw, rh);
            editor.BodyPlace(speedSeg, new Rectangle(x + lw, y, w - lw, rh)); y += Theme.S(44);
            fxRevR = new Rectangle(x, y, w - Theme.S(60), rh);
            editor.BodyPlace(reverse, new Rectangle(x + w - Theme.S(46), y + Theme.S(5), Theme.S(46), Theme.S(24))); y += Theme.S(44);
            fxLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
            int th = transition.NeededHeight(6);
            editor.BodyPlace(transition, new Rectangle(x, y, w, th)); y += th + Theme.S(12);
            fxDurR = new Rectangle(x, y, lw, rh);
            editor.BodyPlace(fxDur, new Rectangle(x + lw, y, w - lw, rh)); y += Theme.S(44);
            fxHintR = new Rectangle(x, y, w, Theme.S(36)); y += Theme.S(44);
            fxInR = new Rectangle(x, y, w - Theme.S(60), rh);
            editor.BodyPlace(fadeIn, new Rectangle(x + w - Theme.S(46), y + Theme.S(5), Theme.S(46), Theme.S(24))); y += Theme.S(42);
            fxOutR = new Rectangle(x, y, w - Theme.S(60), rh);
            editor.BodyPlace(fadeOut, new Rectangle(x + w - Theme.S(46), y + Theme.S(5), Theme.S(46), Theme.S(24))); y += Theme.S(46);
            fxDur.Enabled = TransIndex > 0;

            // background music
            fxLineR = new Rectangle(x, y, w, 1); y += Theme.S(14);
            fxMusicLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(26);
            bool hasMusic = musicFile != null;
            btnMusic.Visible = !hasMusic;
            btnMusicX.Visible = musicMode.Visible = musicVol.Visible = hasMusic;
            if (!hasMusic) { editor.BodyPlace(btnMusic, new Rectangle(x, y, w, Theme.S(38))); y += Theme.S(48); fxMusicNameR = fxMusVolR = Rectangle.Empty; }
            else
            {
                fxMusicNameR = new Rectangle(x, y, w - Theme.S(40), Theme.S(32));
                editor.BodyPlace(btnMusicX, new Rectangle(x + w - Theme.S(34), y, Theme.S(34), Theme.S(32))); y += Theme.S(40);
                editor.BodyPlace(musicMode, new Rectangle(x, y, w, rh)); y += Theme.S(44);
                fxMusVolR = new Rectangle(x, y, lw, rh);
                editor.BodyPlace(musicVol, new Rectangle(x + lw, y, w - lw, rh)); y += Theme.S(46);
            }
            // subtitles and GIF (video only)
            btnSubs.Visible = btnSubsX.Visible = subsSize.Visible = saveAs.Visible = btnCaptions.Visible = btnCapEdit.Visible = false;
            fxSubsLblR = fxSubsNameR = fxSubsHintR = fxSaveAsR = Rectangle.Empty;
            if (vid)
            {
                fxSubsLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(26);
                if (subsFile == null || captioning)
                {
                    // make them automatically (whisper, on this PC), or pick a subtitle file
                    btnCaptions.Visible = true; btnCaptions.Kind = captioning ? BtnKind.Secondary : BtnKind.Primary; btnCaptions.Glyph = captioning ? Ico.Stop : "\uE8BE";
                    editor.BodyPlace(btnCaptions, new Rectangle(x, y, w, Theme.S(40))); y += Theme.S(46);
                    if (!captioning) { btnSubs.Visible = true; editor.BodyPlace(btnSubs, new Rectangle(x, y, w, Theme.S(36))); y += Theme.S(46); }
                }
                else
                {
                    fxSubsNameR = new Rectangle(x, y, w - Theme.S(40), Theme.S(32));
                    btnSubsX.Visible = true;
                    editor.BodyPlace(btnSubsX, new Rectangle(x + w - Theme.S(34), y, Theme.S(34), Theme.S(32))); y += Theme.S(40);
                    if (subsAuto && File.Exists(subsFile))
                    {
                        btnCapEdit.Visible = true;
                        editor.BodyPlace(btnCapEdit, new Rectangle(x, y, w, Theme.S(32))); y += Theme.S(38);
                    }
                    subsSize.Visible = true;
                    editor.BodyPlace(subsSize, new Rectangle(x, y, w, rh)); y += Theme.S(42);
                    fxSubsHintR = new Rectangle(x, y, w, Theme.S(34)); y += Theme.S(40);
                }
                fxSaveAsR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(26);
                saveAs.Visible = true;
                editor.BodyPlace(saveAs, new Rectangle(x, y, w, rh)); y += Theme.S(48);
                // the background, taken out frame by frame
                fxBgLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(26);
                btnVbg.Visible = true; btnVbg.Enabled = !running;
                editor.BodyPlace(btnVbg, new Rectangle(x, y, w, Theme.S(38))); y += Theme.S(48);
            }
            else fxBgLblR = Rectangle.Empty;
            editor.BodyPlace(btnFxPreview, new Rectangle(x, y, w, Theme.S(40))); y += Theme.S(48);
            return y;
        }

        void EffectsPaint(Graphics g)
        {
            int bw = editor.Body.Width;
            Func<Rectangle, Rectangle> m = r => Theme.Mirror(r, bw);
            Font lf = Theme.F(9f, FontStyle.Bold);
            Theme.Text(g, L.T("fx_volume"), lf, Theme.InkSoft, m(fxVolR), Align.Start);
            Theme.Text(g, L.T("fx_speed"), lf, Theme.InkSoft, m(fxSpeedR), Align.Start);
            Theme.Text(g, L.T("fx_reverse"), lf, Theme.InkSoft, m(fxRevR), Align.Start);
            Theme.Text(g, L.T("fx_transition"), lf, Theme.InkSoft, m(fxLblR), Align.Start);
            Theme.Text(g, L.T("fx_duration"), lf, Theme.InkSoft, m(fxDurR), Align.Start);
            Theme.Text(g, L.T(clips.Count > 1 ? "fx_hint" : "fx_hint_one"), Theme.F(8.5f), Theme.InkFaint, m(fxHintR), Align.Start, false, true);
            Theme.Text(g, L.T("fx_fade_in"), lf, Theme.InkSoft, m(fxInR), Align.Start);
            Theme.Text(g, L.T("fx_fade_out"), lf, Theme.InkSoft, m(fxOutR), Align.Start);
            using (SolidBrush lb = new SolidBrush(Theme.Line)) g.FillRectangle(lb, m(fxLineR));
            Theme.Text(g, L.T("fx_music"), lf, Theme.InkSoft, m(fxMusicLblR), Align.Start);
            if (!fxMusicNameR.IsEmpty) NameChip(g, m(fxMusicNameR), Ico.Music, Path.GetFileName(musicFile));
            if (!fxMusVolR.IsEmpty) Theme.Text(g, L.T("fx_music_vol"), lf, Theme.InkSoft, m(fxMusVolR), Align.Start);
            if (!fxSubsLblR.IsEmpty) Theme.Text(g, L.T("fx_subs"), lf, Theme.InkSoft, m(fxSubsLblR), Align.Start);
            if (!fxSubsNameR.IsEmpty) NameChip(g, m(fxSubsNameR), subsAuto ? "\uE8BE" : Ico.Text, subsAuto ? L.T("cap_chip") : Path.GetFileName(subsFile));
            if (!fxSubsHintR.IsEmpty) Theme.Text(g, L.T("fx_subs_hint", editor.TextFont), Theme.F(8.5f), Theme.InkFaint, m(fxSubsHintR), Align.Start, false, true);
            if (!fxSaveAsR.IsEmpty) Theme.Text(g, L.T("fx_save_as"), lf, Theme.InkSoft, m(fxSaveAsR), Align.Start);
            if (!fxBgLblR.IsEmpty) Theme.Text(g, L.T("vbg_lbl"), lf, Theme.InkSoft, m(fxBgLblR), Align.Start);
        }

        static void NameChip(Graphics g, Rectangle r, string glyph, string name)
        {
            Theme.FillRound(g, r, Theme.S(8), Theme.CardInner);
            Rectangle gr = Theme.RTL ? new Rectangle(r.Right - Theme.S(30), r.Y, Theme.S(24), r.Height) : new Rectangle(r.X + Theme.S(6), r.Y, Theme.S(24), r.Height);
            Theme.Glyph(g, glyph, 9f, Theme.InkSoft, gr);
            Rectangle tr = Theme.RTL ? new Rectangle(r.X + Theme.S(8), r.Y, r.Width - Theme.S(40), r.Height) : new Rectangle(r.X + Theme.S(34), r.Y, r.Width - Theme.S(40), r.Height);
            Theme.Text(g, name, Theme.F(9f), Theme.Ink, tr, Align.Start);
        }

        Media.Fx MakeFx(bool preview)
        {
            bool vid = clips.Count > 0 && clips[0].Info.HasVideo;
            return new Media.Fx
            {
                Volume = volume.Value / 100.0, Speed = Speeds[Math.Max(0, Math.Min(Speeds.Length - 1, speedSeg.SelectedIndex))], Reverse = reverse.Checked,
                Music = musicFile, MusicVolume = musicVol.Value / 100.0, MusicReplace = musicMode.SelectedIndex == 1,
                Subs = vid ? subsFile : null, SubsFont = editor.TextFont, SubsSize = new[] { 16, 22, 30 }[Math.Max(0, Math.Min(2, subsSize.SelectedIndex))],
                Gif = !preview && vid && saveAs.SelectedIndex == 1,
                GifWidth = Math.Min(640, Math.Max(160, editor.S.W))
            };
        }

        void FxChanged() { editor.S.Dirty = true; editor.Body.Invalidate(); }

        void PickMusic()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("fx_add_music");
                d.Filter = L.T("media_files") + "|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.wma;*.aiff;*.mp4;*.mkv;*.mov;*.webm|" + L.T("all_files") + "|*.*";
                if (LastDir.Show(d, FindForm(), "video") != DialogResult.OK) return;
                musicFile = d.FileName;
            }
            FxChanged(); editor.DoLayout();
        }

        void PickSubs()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("fx_add_subs");
                d.Filter = L.T("fx_subs_files") + "|*.srt;*.ass;*.ssa;*.vtt|" + L.T("all_files") + "|*.*";
                if (LastDir.Show(d, FindForm(), "video") != DialogResult.OK) return;
                subsFile = d.FileName;
            }
            FxChanged(); editor.DoLayout();
        }

        int FooterLayout()
        {
            int p = editor.Pad, w = editor.Width - p * 2;
            int bottom = editor.Height - p, bh = Theme.S(46);
            if (btnShow.Visible)
            {
                int ow = (w - Theme.S(10)) * 9 / 20;
                editor.PanelPlace(btnShow, new Rectangle(p, bottom - bh, ow, bh));
                editor.PanelPlace(btnSave, new Rectangle(p + ow + Theme.S(10), bottom - bh, w - ow - Theme.S(10), bh));
            }
            else editor.PanelPlace(btnSave, new Rectangle(p, bottom - bh, w, bh));
            statusR = new Rectangle(p, bottom - bh - Theme.S(44), w, Theme.S(38));
            editor.PanelPlace(progress, new Rectangle(p, bottom - bh - Theme.S(52), w, Theme.S(6)));
            return bottom - bh - Theme.S(64);
        }

        void TrimPaint(Graphics g)
        {
            if (C == null) return;
            int bw = editor.Body.Width;
            Func<Rectangle, Rectangle> m = r => Theme.Mirror(r, bw);
            string name = (clips.Count > 1 ? L.T("clip_n", cur + 1, clips.Count) + "  ·  " : "") + Path.GetFileName(file);
            Theme.Text(g, name, Theme.F(11f, FontStyle.Bold), Theme.Ink, m(new Rectangle(infoR.X, infoR.Y, infoR.Width, Theme.S(24))), Align.Start);
            string meta = Theme.Time(info.Duration) + (info.HasVideo ? "  ·  " + C.W + "×" + C.H : "") +
                          (info.VCodec != "" ? "  ·  " + info.VCodec.ToUpperInvariant() : "") + (info.ACodec != "" ? "  ·  " + info.ACodec.ToUpperInvariant() : "");
            Theme.Text(g, meta, Theme.F(9f), Theme.InkSoft, m(new Rectangle(infoR.X, infoR.Y + Theme.S(24), infoR.Width, Theme.S(22))), Align.Start);
            int fw = (fieldsLblR.Width - Theme.S(10)) / 2;
            Theme.Text(g, L.T("start"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, m(new Rectangle(fieldsLblR.X, fieldsLblR.Y, fw, fieldsLblR.Height)), Align.Start);
            Theme.Text(g, L.T("end") + "   ·   " + L.T("length") + " " + Theme.Time(C.Length, true), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, m(new Rectangle(fieldsLblR.X + fw + Theme.S(10), fieldsLblR.Y, fw + Theme.S(4), fieldsLblR.Height)), Align.Start);
            if (!modeLblR.IsEmpty) Theme.Text(g, L.T("mode"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, m(modeLblR), Align.Start);
            if (!accLblR.IsEmpty) Theme.Text(g, L.T("accuracy"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, m(accLblR), Align.Start);
            string hint = clips.Count > 1 ? L.T("clips_hint") : mode.SelectedIndex == 1 ? L.T("remove_hint") : NeedsReencode ? L.T("acc_text_hint") : accuracy.SelectedIndex == 0 ? L.T("acc_precise_hint") : L.T("acc_fast_hint");
            Theme.Text(g, hint, Theme.F(8.5f), Theme.InkFaint, m(accHintR), Align.Start, false, true);
        }

        bool NeedsReencode { get { EditSession s = editor.S; return Math.Abs(volume.Value - 100) > 0.5 || MakeFx(false).Special || s.HasText || s.W != s.BaseW || s.H != s.BaseH || !s.Crop.IsEmpty || fadeIn.Checked || fadeOut.Checked; } }

        public override void Restyle()
        {
            fStart.Restyle(); fEnd.Restyle();
            transition.SetItems(TransNames, TransNames[Math.Min(TransIndex, TransNames.Length - 1)]);
            editor.Restyle();
            view.ImageChanged();
            if (C != null && !info.HasVideo) LoadWave(C);
        }

        void SetStatus(string t, Color c) { statusText = t; statusColor = c; editor.Invalidate(); }

        protected override void DragHighlight(bool on) { drop.DragHot = on; drop.Invalidate(); }

        public void Browse(bool append)
        {
            if (running) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T(append ? "clip_add" : "studio_drop");
                List<string> img = new List<string>();
                foreach (string e in Formats.ImageExt) img.Add("*." + e);
                string media = "*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.m4v;*.mpg;*.mpeg;*.3gp;*.flv;*.ts;*.mts;*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.wma;*.aiff";
                d.Filter = L.T("media_files") + "|" + media + (append ? "" : "|" + L.T("images") + "|" + string.Join(";", img.ToArray())) + "|" + L.T("all_files") + "|*.*";
                d.Multiselect = append;
                if (LastDir.Show(d, FindForm(), "video") != DialogResult.OK) return;
                if (append) foreach (string f in d.FileNames) Load(f, true);
                else AcceptFiles(new[] { d.FileName });
            }
        }

        public override void AcceptFiles(string[] paths)
        {
            if (running || paths == null || paths.Length == 0 || !File.Exists(paths[0])) return;
            if (Formats.Ext(paths[0]) == ProjectExt) { OpenProject(paths[0], false); return; }
            if ((Formats.CategoryOf(Formats.Ext(paths[0])) == Category.Image || DocPages.IsDoc(paths[0])) && OpenImage != null) { OpenImage(paths[0]); return; }
            // dropping more videos while one is open adds them as clips
            bool append = clips.Count > 0 && (ModifierKeys & Keys.Shift) == 0 && paths.Length >= 1 && dropAppends;
            for (int i = 0; i < paths.Length; i++) Load(paths[i], append || i > 0);
        }
        bool dropAppends;
        /// Photos placed on top of the video (they show for the whole video, or from the red line when chosen so).
        public bool AddPictures(string[] paths)
        {
            if (clips.Count == 0 || running || !clips[0].Info.HasVideo) return false;
            foreach (string p in paths) if (Formats.CategoryOf(Formats.Ext(p)) != Category.Image) return false;
            foreach (string p in paths) editor.AddPicture(p);
            return true;
        }

        public void AppendFiles(string[] paths) { dropAppends = true; try { AcceptFiles(paths); } finally { dropAppends = false; } }

        static Bitmap AudioArt()
        {
            Bitmap b = new Bitmap(1280, 720);
            using (Graphics g = Graphics.FromImage(b))
            {
                Theme.Smooth(g);
                using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, 1280, 720), Color.FromArgb(58, 62, 70), Color.FromArgb(18, 19, 22), LinearGradientMode.Vertical)) g.FillRectangle(br, 0, 0, 1280, 720);
                using (Font f = new Font(Theme.IconFontName, 120f, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(205, 209, 216)))
                    g.DrawString(Ico.Music, f, ink, new RectangleF(0, 0, 1280, 720), sf);
            }
            return b;
        }

        void Load(string path, bool append)
        {
            if (!append && HasUnsavedText && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("ed_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return;
            StopPlay();
            int token = append ? loadToken : ++loadToken;
            SetStatus(L.T("loading_media"), Theme.InkSoft);
            Thread t = new Thread(() =>
            {
                MediaInfo mi = null; Image first = null;
                try { mi = Media.Probe(path); } catch { }
                if (mi != null && mi.HasVideo) first = Media.Frame(path, Math.Min(1, mi.Duration / 10), 540);
                Ui(() =>
                {
                    if (token != loadToken) { if (first != null) first.Dispose(); return; }
                    SetStatus("", Theme.InkSoft);
                    if (mi == null || (!mi.HasVideo && !mi.HasAudio) || mi.Duration <= 0)
                    {
                        if (first != null) first.Dispose();
                        ShowToast(L.T("not_media"), false, null, null);
                        return;
                    }
                    if (append && clips.Count > 0 && mi.HasVideo != clips[0].Info.HasVideo)
                    {
                        if (first != null) first.Dispose();
                        ShowToast(L.T(clips[0].Info.HasVideo ? "clip_need_video" : "clip_need_audio"), false, null, null);
                        return;
                    }
                    int w = mi.Width, h = mi.Height;
                    if (first != null && w > 0 && h > 0)
                    {
                        float ar = first.Width / (float)first.Height;
                        if (Math.Abs(ar - h / (float)w) < Math.Abs(ar - w / (float)h)) { int tmp = w; w = h; h = tmp; }
                    }
                    else if (first != null) { w = first.Width; h = first.Height; }
                    if (w <= 0) { w = 1280; h = 720; }
                    w += w % 2; h += h % 2;
                    MediaSource src = new MediaSource { File = path, Info = mi, W = w, H = h, Strip = new Image[StripFrames] };
                    Clip clip = new Clip { Src = src, Start = 0, End = mi.Duration };
                    if (!append || clips.Count == 0)
                    {
                        projectPath = null;
                        foreach (MediaSource ms in sources) ms.Dispose();
                        sources.Clear();
                        clips.Clear();
                        seqMode = false; timeline.Sequence = false;
                        clips.Add(clip);
                        cur = 0;
                        editor.BeginVideo(w, h, path, !mi.HasVideo);
                        view.Layers = mi.HasVideo ? editor.S.Layers : null;
                        lastOutput = null; btnShow.Visible = false; progress.Visible = false;
                        mode.SetSilently(0); accuracy.SetSilently(0); accuracy.Enabled = true;
                    }
                    else
                    {
                        if (seqMode) ExitSequence();
                        clips.Add(clip);
                        cur = clips.Count - 1;
                        editor.S.Dirty = true;
                    }
                    sources.Add(src);
                    editor.SetFrame(first != null ? new Bitmap(first) : AudioArt());
                    if (first != null) first.Dispose();
                    view.Reset();
                    timeline.Playhead = timeline.OffsetOf(cur);
                    ShowClip();
                    ShowEmpty(false);
                    if (mi.HasVideo) { LoadStrip(clip, loadToken); if (mi.HasAudio) LoadWave(clip); } else LoadWave(clip);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        /// Shows the clips on the timeline and the selected one in the Trim tab.
        void ShowClip() { RefreshTrack(); }

        void RefreshTrack()
        {
            if (clips.Count == 0) return;
            cur = Math.Max(0, Math.Min(cur, clips.Count - 1));
            timeline.Sequence = seqMode;
            timeline.Selected = seqMode ? -1 : cur;
            timeline.Playhead = Math.Max(0, Math.Min(timeline.Playhead, timeline.Total));
            view.ShowPlay = C.Info.HasVideo && !player.Playing;
            SyncFields();
            timeline.Changed();
            UpdateToolbar();
            editor.DoLayout();
        }

        void UpdateToolbar()
        {
            bool edit = !seqMode && !running && clips.Count > 0;
            tbSplit.Enabled = edit; tbDup.Enabled = edit; tbAdd.Enabled = !running;
            tbDelete.Enabled = edit && clips.Count > 1;
            tbPreview.Enabled = !running && clips.Count > 0;
            tbProject.Enabled = !running;
            tbCaptions.Enabled = !running;
            foreach (Btn b in new[] { tbAdd, tbSplit, tbDelete, tbDup, tbPreview, tbProject }) b.Invalidate();
        }

        void SelectClip(int i)
        {
            if (i < 0 || i >= clips.Count) return;
            StopPlay();
            if (seqMode) ExitSequence();
            cur = i;
            timeline.Playhead = timeline.OffsetOf(i);
            RefreshTrack();
            RequestFrame(C.Start, false);
        }

        /// Cuts the clip under the red line in two (CapCut's "Split").
        void SplitAtPlayhead()
        {
            if (seqMode || running || clips.Count == 0) return;
            StopPlay();
            double local; int i = timeline.ClipAt(timeline.Playhead, out local);
            if (i < 0) return;
            Clip c = clips[i];
            if (local <= c.Start + 0.1 || local >= c.End - 0.1) { ShowToast(L.T("tl_split_where"), false, null, null); return; }
            Clip right = new Clip { Src = c.Src, Start = local, End = c.End };
            c.End = local;
            clips.Insert(i + 1, right);
            cur = i + 1;
            editor.S.Dirty = true;
            RefreshTrack();
            ShowToast(L.T("tl_split_done", Theme.Time(timeline.Playhead, true)), true, null, null);
        }

        void DeleteClip(int i)
        {
            if (seqMode || running || i < 0 || i >= clips.Count) return;
            if (clips.Count <= 1) { ShowToast(L.T("tl_last_clip"), false, null, null); return; }
            StopPlay();
            clips.RemoveAt(i);
            cur = Math.Min(i, clips.Count - 1);
            editor.S.Dirty = true;
            timeline.Playhead = timeline.OffsetOf(cur);
            RefreshTrack();
            RequestFrame(C.Start, false);
        }

        void DuplicateClip()
        {
            if (seqMode || running || C == null) return;
            StopPlay();
            clips.Insert(cur + 1, C.Copy());
            cur++;
            editor.S.Dirty = true;
            RefreshTrack();
        }

        void RemoveClip(int i) { DeleteClip(i); }

        public void Unload()
        {
            loadToken++;
            StopPlay();
            Media.StopPreview();
            editor.End();
            view.Image = null; view.Layers = null; view.ShowPlay = false;
            clips.Clear();
            foreach (MediaSource ms in sources) ms.Dispose();
            sources.Clear();
            seqMode = false; timeline.Sequence = false; timeline.SeqStrip = null; timeline.Playhead = 0;
            projectPath = null; musicFile = null; subsFile = null; subsAuto = false;
            ForgetAutoSave();
            ShowEmpty(true);
        }

        public bool CloseFile()
        {
            if (running) return false;
            if (HasUnsavedText && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("ed_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return false;
            Unload();
            return true;
        }

        void LoadStrip(Clip clip, int token)
        {
            string f = clip.File; double dur = clip.Info.Duration;
            Thread t = new Thread(() =>
            {
                // a quick first pass every 4th frame, then the rest, so the timeline fills in fast
                int n = StripFrames;
                List<int> order = new List<int>();
                for (int i = 0; i < n; i += 4) order.Add(i);
                for (int i = 0; i < n; i++) if (i % 4 != 0) order.Add(i);
                foreach (int i in order)
                {
                    if (token != loadToken || clip.Strip == null) return;
                    Image im = Media.Frame(f, dur * (i + 0.5) / n, 90);
                    int idx = i;
                    Ui(() => { if (token == loadToken && clip.Strip != null) { clip.Strip[idx] = im; timeline.Changed(); } else if (im != null) im.Dispose(); });
                }
            });
            t.IsBackground = true; t.Start();
        }

        void LoadWave(Clip clip)
        {
            int token = loadToken;
            Color c = clip.Info.HasVideo ? Color.FromArgb(235, 238, 242) : Theme.Dark ? Color.FromArgb(170, 176, 186) : Color.FromArgb(90, 96, 106);
            Thread t = new Thread(() =>
            {
                Image w = Media.Waveform(clip.File, 1600, clip.Info.HasVideo ? 120 : 200, c);
                Ui(() =>
                {
                    if (token != loadToken) { if (w != null) w.Dispose(); return; }
                    if (clip.Wave != null) clip.Wave.Dispose();
                    clip.Wave = w;
                    timeline.Changed();
                });
            });
            t.IsBackground = true; t.Start();
        }

        // ------------------------------------------------------------------ playback
        int PreviewH { get { return 540; } }

        void TogglePlay()
        {
            if (C == null || running) return;
            if (player.Playing) { StopPlay(); return; }
            double ph = timeline.Playhead;
            PlayFrom(ph < timeline.Total - 0.15 ? ph : 0);
        }

        /// Plays the project from a time; when one clip ends the next one starts.
        void PlayFrom(double from)
        {
            if (C == null || running) return;
            StopPlay();
            if (seqMode && seqFile == null) return;
            double total = timeline.Total;
            if (from >= total - 0.05) from = 0;
            string f; double local, to; Clip clip = null; int index = -1;
            if (seqMode) { f = seqFile; local = from; to = seqDur; }
            else
            {
                index = timeline.ClipAt(from, out local);
                if (index < 0) return;
                clip = clips[index];
                f = clip.File; to = clip.End;
                if (index != cur) { cur = index; timeline.Selected = cur; SyncFields(); editor.DoLayout(); }
            }
            double offset = seqMode ? 0 : timeline.OffsetOf(index) - (clip != null ? clip.Start : 0);
            bool hasVideo = clips[0].Info.HasVideo;
            int h = PreviewH, w = seqMode ? (int)Math.Round(h * editor.S.OW / (double)editor.S.OH) : (int)Math.Round(h * clip.W / (double)clip.H);
            int token = loadToken, ptoken = ++playToken;
            bool seq = seqMode;
            view.ShowPlay = false; view.Loading = true; view.Invalidate();
            timeline.Playhead = from; timeline.Invalidate();
            player.Start(f, local, to, w, h, hasVideo, seq ? 1.0 : volume.Value / 100.0,
                (bmp, t) =>
                {
                    if (framePending) { bmp.Dispose(); return; }   // the screen is busy: drop this frame
                    framePending = true;
                    Ui(() =>
                    {
                        framePending = false;
                        if (token != loadToken || ptoken != playToken || seq != seqMode || !player.Playing) { bmp.Dispose(); return; }
                        view.Loading = false;
                        editor.SetFrame(bmp);
                        timeline.Playhead = offset + t; timeline.Invalidate();
                        view.Caption = Theme.Time(offset + t, true) + "  /  " + Theme.Time(total, true);
                    });
                },
                t => Ui(() => { if (ptoken != playToken) return; view.Loading = false; timeline.Playhead = offset + t; timeline.Invalidate(); view.Caption = Theme.Time(offset + t, true) + "  /  " + Theme.Time(total, true); view.Invalidate(); }),
                () => Ui(() =>
                {
                    if (ptoken != playToken || token != loadToken) return;
                    // keep going with the next clip
                    if (!seq && index >= 0 && index + 1 < clips.Count) { PlayFrom(timeline.OffsetOf(index + 1)); return; }
                    view.ShowPlay = hasVideo; view.Loading = false; view.Invalidate(); btnPlay.Glyph = Ico.Play; btnPlay.Invalidate();
                }));
            btnPlay.Glyph = Ico.Pause; btnPlay.Invalidate();
        }

        // ------------------------------------------------------------------ full video (all clips joined)
        string SequenceKey()
        {
            StringBuilder k = new StringBuilder();
            foreach (Clip c in clips) k.Append(c.File).Append('|').Append(c.Start.ToString("0.00")).Append('|').Append(c.End.ToString("0.00")).Append(';');
            k.Append(TransIndex).Append(fxDur.Value.ToString("0.0")).Append(fadeIn.Checked).Append(fadeOut.Checked).Append(MakeFx(true).Key).Append(TextKey());
            return k.ToString();
        }

        void EnterSequence(bool withEffects)
        {
            if ((clips.Count < 2 && !withEffects) || running) return;
            StopPlay();
            seqMode = true;
            double total = 0; foreach (Clip c in clips) total += c.Length;
            int ti = TransIndex;
            double d = ti > 0 && clips.Count > 1 ? fxDur.Value : 0;
            seqDur = Math.Max(0.5, (total - d * (clips.Count - 1)) / Speeds[Math.Max(0, Math.Min(Speeds.Length - 1, speedSeg.SelectedIndex))]);
            // a film strip of the whole thing, borrowed from each clip's own strip
            Image[] st = new Image[StripFrames];
            for (int i = 0; i < StripFrames; i++)
            {
                double t = total * (i + 0.5) / StripFrames, acc = 0;
                foreach (Clip c in clips)
                {
                    if (t <= acc + c.Length || c == clips[clips.Count - 1])
                    {
                        if (c.Strip != null && c.Info.Duration > 0)
                        {
                            int k = (int)Math.Min(StripFrames - 1, Math.Max(0, (c.Start + (t - acc)) / c.Info.Duration * StripFrames));
                            st[i] = c.Strip[k];
                        }
                        break;
                    }
                    acc += c.Length;
                }
            }
            if (reverse.Checked) Array.Reverse(st);
            timeline.SeqStrip = st; timeline.SeqDur = seqDur; timeline.Sequence = true; timeline.Selected = -1; timeline.Playhead = 0;
            timeline.Changed(); UpdateToolbar();
            editor.DoLayout();
            string key = SequenceKey();
            if (seqFile != null && seqKey == key && File.Exists(seqFile)) { PlayFrom(0); return; }
            RenderSequence(key);
        }

        void RenderSequence(string key)
        {
            if (seqRendering) return;
            seqRendering = true;
            EditSession s = editor.S;
            List<Media.MergeClip> mc = new List<Media.MergeClip>();
            foreach (Clip c in clips) mc.Add(new Media.MergeClip { File = c.File, Info = c.Info, Start = c.Start, End = c.End });
            int h = Math.Min(540, s.OH), w = (int)Math.Round(h * s.OW / (double)s.OH);
            w += w % 2; h += h % 2;
            Job job;
            List<Media.Overlay> ovs = null;
            if (clips[0].Info.HasVideo && s.HasText && s.Crop.IsEmpty)
            {
                // text in the preview too (drawn at the preview's size, switched on and off at its times)
                try { ovs = MakeOverlays(MapTime, w, h); } catch { ovs = null; }
            }
            try { job = Media.BuildMerge(mc, s.OW, s.OH, Rectangle.Empty, w, h, ovs, clips.Count > 1 ? TransKeys[TransIndex] : "none", fxDur.Value, fadeIn.Checked, fadeOut.Checked, Path.GetTempPath(), MakeFx(true), true); }
            catch (Exception ex) { seqRendering = false; ShowToast(ex.Message, false, null, null); return; }
            view.Loading = true; view.ShowPlay = false;
            view.Caption = L.T("seq_preparing", 0); view.Invalidate();
            int token = loadToken;
            if (ovs != null) foreach (Media.Overlay o in ovs) job.Temps.Add(o.Png);
            Thread t = new Thread(() =>
            {
                DateTime last = DateTime.MinValue;
                string err = Engine.Run(job, f =>
                {
                    if ((DateTime.Now - last).TotalMilliseconds < 200) return;
                    last = DateTime.Now;
                    Ui(() => { if (seqMode) { view.Caption = L.T("seq_preparing", (int)(f * 100)); view.Invalidate(); } });
                }, () => token != loadToken || Tools.ShuttingDown);
                Ui(() =>
                {
                    seqRendering = false;
                    view.Loading = false;
                    if (err != null || token != loadToken) { if (err != null) ShowToast(err, false, null, null); return; }
                    if (seqFile != null) try { File.Delete(seqFile); } catch { }
                    seqFile = job.Output; seqKey = key;
                    if (seqMode) PlayFrom(0);
                });
            });
            t.IsBackground = true; t.Start();
        }

        void ExitSequence()
        {
            StopPlay();
            seqMode = false;
            timeline.Sequence = false;
            timeline.Playhead = timeline.OffsetOf(cur);
            RefreshTrack();
            if (C != null) RequestFrame(C.Start, false);
        }

        void StopPlay()
        {
            playToken++;
            if (!player.Playing) return;
            player.Stop();
            framePending = false;
            view.ShowPlay = info != null && info.HasVideo;
            btnPlay.Glyph = Ico.Play; btnPlay.Invalidate();
            view.Invalidate();
        }

        void RequestFrame(double t, bool isEnd, bool caption = true)
        {
            if (caption) view.Caption = (isEnd ? L.T("end") : L.T("start")) + "  " + Theme.Time(t, true);
            view.Invalidate();
            if (info == null || !info.HasVideo) return;
            lock (frameLock)
            {
                frameReq = t;
                if (frameThread) return;
                frameThread = true;
            }
            view.Loading = true;
            string f = file; int token = loadToken; double dur = info.Duration;
            Thread th = new Thread(() =>
            {
                while (true)
                {
                    double req;
                    lock (frameLock) { req = frameReq; frameReq = -1; if (req < 0) { frameThread = false; break; } }
                    Image im = Media.Frame(f, Math.Max(0, Math.Min(req, dur - 0.05)), 540);
                    Ui(() =>
                    {
                        if (token != loadToken || f != file) { if (im != null) im.Dispose(); return; }
                        if (im != null) { editor.SetFrame(new Bitmap(im)); im.Dispose(); }
                        lock (frameLock) view.Loading = frameThread;
                        view.Invalidate();
                    });
                }
                Ui(() => { view.Loading = false; view.Invalidate(); });
            });
            th.IsBackground = true; th.Start();
        }

        void SyncFields()
        {
            if (C == null) return;
            fStart.Box.Text = Theme.Time(C.Start, true);
            fEnd.Box.Text = Theme.Time(C.End, true);
            editor.Body.Invalidate();
        }

        static double ParseTime(string s, double fallback)
        {
            s = s.Trim().Replace(',', '.');
            if (s.Length == 0) return fallback;
            string[] parts = s.Split(':');
            double total = 0;
            try
            {
                foreach (string p in parts) total = total * 60 + double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture);
                return total;
            }
            catch { return fallback; }
        }

        void ApplyField(TextField f)
        {
            if (info == null || seqMode) return;
            if (f == fStart)
            {
                double t = ParseTime(f.Box.Text, C.Start);
                if (Math.Abs(t - C.Start) < 0.01) return;
                C.Start = Math.Max(0, Math.Min(t, C.End - 0.1));
                RequestFrame(C.Start, false);
            }
            else
            {
                double t = ParseTime(f.Box.Text, C.End);
                if (Math.Abs(t - C.End) < 0.01) return;
                C.End = Math.Min(info.Duration, Math.Max(t, C.Start + 0.1));
                RequestFrame(C.End, true);
            }
            SyncFields();
            timeline.Changed();
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        public void Save()
        {
            if (C == null || running) return;
            StopPlay();
            ApplyField(fStart); ApplyField(fEnd);
            Job job;
            string overlay = null;
            List<Media.Overlay> overlays = null;
            try
            {
                string outDir = SaveAsk.Scratch();
                EditSession s = editor.S;
                bool video = clips[0].Info.HasVideo;
                bool geometry = video && (s.W != s.OW || s.H != s.OH || !s.Crop.IsEmpty);
                Media.Fx fx = MakeFx(false);
                if (clips.Count == 1 && !fadeIn.Checked && !fadeOut.Checked && !fx.Special && !(video && HasTimedText))
                {
                    if (video && s.HasText)
                    {
                        overlay = Path.Combine(Path.GetTempPath(), "fadi_overlay_" + Guid.NewGuid().ToString("N") + ".png");
                        using (Bitmap ov = s.RenderOverlay()) ov.Save(overlay, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    job = Media.BuildTrim(file, info, C.Start, C.End, mode.SelectedIndex == 0 ? TrimMode.Keep : TrimMode.Remove, accuracy.SelectedIndex == 0, outDir,
                                          overlay, (overlay != null || geometry) ? s.W : 0, (overlay != null || geometry) ? s.H : 0, s.Crop, volume.Value / 100.0);
                }
                else
                {
                    List<Media.MergeClip> mc = new List<Media.MergeClip>();
                    string trans = TransKeys[TransIndex];
                    if (clips.Count == 1 && mode.SelectedIndex == 1)
                    {
                        // "cut it out" with effects: join what comes before and after the selection
                        if (C.Start > 0.05) mc.Add(new Media.MergeClip { File = file, Info = info, Start = 0, End = C.Start });
                        if (C.End < info.Duration - 0.05) mc.Add(new Media.MergeClip { File = file, Info = info, Start = C.End, End = info.Duration });
                        if (mc.Count == 0) throw new UserError(L.T("err_nothing_left"));
                        trans = "none";
                    }
                    else foreach (Clip c in clips) mc.Add(new Media.MergeClip { File = c.File, Info = c.Info, Start = c.Start, End = c.End });
                    // text: one picture per group shown at the same times, switched on and off in the saved video
                    if (video && s.HasText) overlays = MakeOverlays(clips.Count == 1 && mode.SelectedIndex == 1 ? (Func<double, double>)(x => x) : MapTime, 0, 0);
                    job = Media.BuildMerge(mc, s.OW, s.OH, s.Crop, s.W, s.H, overlays, trans, fxDur.Value, fadeIn.Checked, fadeOut.Checked, outDir, fx);
                    foreach (Media.Overlay o in overlays ?? new List<Media.Overlay>()) job.Temps.Add(o.Png);
                }
            }
            catch (Exception ex) { SetStatus(ex.Message, Theme.Error); return; }
            // where should it go?
            string ext0 = Path.GetExtension(job.Output).TrimStart('.');
            string chosen = SaveAsk.File(FindForm(), Path.GetFileName(job.Output), ext0);
            if (chosen == null)
            {
                if (overlay != null) try { File.Delete(overlay); } catch { }
                if (overlays != null) foreach (Media.Overlay o in overlays) try { File.Delete(o.Png); } catch { }
                try { Directory.Delete(Path.GetDirectoryName(job.Output), true); } catch { }
                return;
            }
            SaveAsk.Redirect(job, chosen);
            running = true; cancel = false;
            Media.StopPreview();
            btnShow.Visible = false; progress.Visible = true; progress.Value = 0;
            foreach (Control c in new Control[] { fStart, fEnd, mode, accuracy, btnPlay, btnOther, timeline }) c.Enabled = false;
            UpdateToolbar();
            editor.DoLayout(); btnSave.Kind = BtnKind.Secondary; btnSave.Glyph = Ico.Stop; btnSave.Invalidate();
            SetStatus(L.T("trimming", 0), Theme.InkSoft);
            string ov2 = overlay;
            act = Activity.Begin(this, L.T("act_k_export"), Path.GetFileName(chosen), () => cancel = true);
            Thread t = new Thread(() =>
            {
                DateTime last = DateTime.MinValue;
                string err = Engine.Run(job, f =>
                {
                    if ((DateTime.Now - last).TotalMilliseconds < 120) return;
                    last = DateTime.Now;
                    Activity.Update(act, f, (int)(f * 100) + "%");
                    Ui(() => { progress.Value = f; SetStatus(L.T("trimming", (int)(f * 100)), Theme.InkSoft); });
                }, () => cancel);
                if (ov2 != null) try { File.Delete(ov2); } catch { }
                Ui(() => Done(err, job.Output));
            });
            t.IsBackground = true; t.Start();
        }

        ActJob act;

        void Done(string err, string output)
        {
            running = false;
            long sz = 0; try { if (err == null) sz = new FileInfo(output).Length; } catch { }
            Activity.End(act, err, cancel, L.T("act_saved_size", Theme.HumanSize(sz)), output);
            UpdateToolbar();
            foreach (Control c in new Control[] { fStart, fEnd, mode, btnPlay, btnOther, timeline }) c.Enabled = true;
            accuracy.Enabled = mode.SelectedIndex == 0;
            btnSave.Kind = BtnKind.Primary; btnSave.Glyph = Ico.Save;
            if (err == null)
            {
                lastOutput = output;
                editor.S.Dirty = false;
                if (projectPath == null) ForgetAutoSave();
                progress.Value = 1;
                btnShow.Visible = true;
                SetStatus(L.T("trim_done", Path.GetFileName(output)), Theme.Success);
                ShowToast(L.T("trim_done", Path.GetFileName(output)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + output + "\""); } catch { } });
                if (Settings.OpenAfter) try { Process.Start("explorer.exe", "/select,\"" + output + "\""); } catch { }
            }
            else
            {
                progress.Visible = false;
                SetStatus(cancel ? L.T("cancelled") : err, cancel ? Theme.InkSoft : Theme.Error);
            }
            editor.DoLayout();
            btnSave.Invalidate();
        }

        public bool HandleKey(Keys k)
        {
            if (C == null) return false;
            bool typing = editor.TextFocused;
            switch (k)
            {
                case Keys.Space: if (typing) return false; TogglePlay(); return true;
                case Keys.Control | Keys.S: Save(); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveProject(false); return true;
                case Keys.Control | Keys.Z: if (typing || running) return false; StopPlay(); editor.Undo(); return true;
                case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: if (typing || running) return false; StopPlay(); editor.Redo(); return true;
                case Keys.Escape: if (typing) return false; if (view.CropMode) { editor.CancelCrop(); return true; } if (player.Playing) { StopPlay(); return true; } return editor.Deselect();
                case Keys.Delete: if (typing) return false; if (editor.DeleteKey()) return true; DeleteClip(cur); return true;
                case Keys.S: case Keys.Control | Keys.B: if (typing) return false; SplitAtPlayhead(); return true;
                case Keys.Control | Keys.D: if (typing) return false; DuplicateClip(); return true;
                case Keys.Left: if (typing || editor.S.Current == null) return false; editor.Nudge(-1, 0); return true;
                case Keys.Right: if (typing || editor.S.Current == null) return false; editor.Nudge(1, 0); return true;
                case Keys.Up: if (typing || editor.S.Current == null) return false; editor.Nudge(0, -1); return true;
                case Keys.Down: if (typing || editor.S.Current == null) return false; editor.Nudge(0, 1); return true;
            }
            return false;
        }

        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) StopPlay(); }
    }
}
