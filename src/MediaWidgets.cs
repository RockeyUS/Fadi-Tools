using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace FileConverter
{
    /// A video or song file, shared by every clip cut from it (strips are loaded once).
    public class MediaSource
    {
        public string File; public MediaInfo Info;
        public int W, H;
        public Image[] Strip; public Image Wave;
        public void Dispose()
        {
            if (Strip != null) foreach (Image i in Strip) if (i != null) i.Dispose();
            Strip = null;
            if (Wave != null) Wave.Dispose();
            Wave = null;
        }
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

    /// "Recent video projects" chips shown in the empty Studio.
    public class RecentStrip : UiControl
    {
        public List<string> Paths = new List<string>();
        public event Action<string> Open;
        public event Action Browse;
        int hover = -1;
        public RecentStrip() { Surface = Surface.Background; Cursor = Cursors.Hand; }

        int Count { get { return Math.Min(Paths.Count, Math.Max(0, (Width - Theme.S(150)) / Theme.S(206))); } }
        Rectangle Chip(int i)   // i == Count → "Open project…"
        {
            int w = i == Count ? Theme.S(150) : Theme.S(198), x = i * Theme.S(206);
            return Theme.Mirror(new Rectangle(x, Theme.S(26), w, Height - Theme.S(28)), Width);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; for (int i = 0; i <= Count; i++) if (Chip(i).Contains(e.Location)) h = i;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i <= Count; i++)
                if (Chip(i).Contains(e.Location)) { if (i == Count) { if (Browse != null) Browse(); } else if (Open != null) Open(Paths[i]); return; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Theme.Text(g, L.T("proj_recent_videos").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, Theme.Mirror(new Rectangle(Theme.S(2), 0, Width, Theme.S(20)), Width), Align.Start);
            for (int i = 0; i <= Count; i++)
            {
                Rectangle c = Chip(i);
                bool hot = i == hover;
                RectangleF r = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1);
                Theme.FillRound(g, r, Theme.S(10), hot ? Theme.Hover : Theme.Card);
                Theme.StrokeRound(g, r, Theme.S(10), hot ? Theme.InkFaint : Theme.CardBorder);
                Rectangle ic = Theme.Mirror(new Rectangle((Theme.RTL ? Width - c.Right : c.X) + Theme.S(10), c.Y + (c.Height - Theme.S(32)) / 2, Theme.S(32), Theme.S(32)), Width);
                Theme.FillRound(g, ic, Theme.S(8), i == Count ? Theme.CardInner : Theme.AccentSoft);
                Theme.Glyph(g, i == Count ? Ico.FolderOpen : "", 9.5f, i == Count ? Theme.InkSoft : Theme.AccentColor, ic);
                int tx = (Theme.RTL ? Width - c.Right : c.X) + Theme.S(50), tw = c.Width - Theme.S(58);
                if (i == Count)
                {
                    Theme.Text(g, L.T("proj_open_short"), Theme.F(9f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(tx, c.Y, tw, c.Height), Width), Align.Start, true, false);
                    continue;
                }
                DateTime t = DateTime.Now; try { t = File.GetLastWriteTime(Paths[i]); } catch { }
                Theme.Text(g, Path.GetFileNameWithoutExtension(Paths[i]), Theme.F(9f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(tx, c.Y + Theme.S(7), tw, Theme.S(20)), Width), Align.Start, true, false);
                Theme.Text(g, VideoStudio.Ago(t), Theme.F(8f), Theme.InkFaint, Theme.Mirror(new Rectangle(tx, c.Y + Theme.S(26), tw, Theme.S(17)), Width), Align.Start, true, false);
            }
        }
    }
}

namespace FileConverter
{
    /// The preview's own volume (it doesn't change the video): the speaker mutes, the bar sets how loud.
    public class VolumeBar : UiControl
    {
        public float Value = 1;
        public bool Muted;
        public event Action Changed;
        bool drag;

        public VolumeBar() { Surface = Surface.Card; Cursor = System.Windows.Forms.Cursors.Hand; TabStop = true; }

        Rectangle IconR { get { return Theme.Mirror(new Rectangle(0, 0, Height, Height), Width); } }
        Rectangle Bar { get { int x0 = Height + Theme.S(2), x1 = Width - Theme.S(8); return Theme.Mirror(new Rectangle(x0, Height / 2 - Theme.S(2), Math.Max(10, x1 - x0), Theme.S(4)), Width); } }

        void Set(float v, bool unmute)
        {
            v = Math.Max(0, Math.Min(1, v));
            bool ch = Math.Abs(v - Value) > 0.0001f || (unmute && Muted);
            Value = v; if (unmute) Muted = false;
            Invalidate();
            if (ch && Changed != null) Changed();
        }

        float At(int x) { Rectangle b = Bar; float f = (x - b.X) / (float)Math.Max(1, b.Width); return Theme.RTL ? 1 - f : f; }

        protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Rectangle ir = IconR;
            if (hovering) Theme.FillRound(g, RectangleF.Inflate(ir, -Theme.S(2), -Theme.S(2)), Theme.S(8), Theme.Hover);
            bool off = Muted || Value < 0.005f;
            Theme.Glyph(g, off ? "" : Ico.Volume, 10.5f, off ? Theme.AccentColor : Theme.InkSoft, ir);
            Rectangle b = Bar;
            Theme.FillRound(g, b, b.Height / 2f, Theme.Track);
            float f = Muted ? 0 : Value;
            int fw = (int)Math.Round(b.Width * f);
            Rectangle fill = Theme.RTL ? new Rectangle(b.Right - fw, b.Y, fw, b.Height) : new Rectangle(b.X, b.Y, fw, b.Height);
            if (fw > 0) Theme.FillRound(g, fill, b.Height / 2f, Muted ? Theme.InkFaint : Theme.AccentColor);
            float kx = Theme.RTL ? b.Right - fw : b.X + fw, r = Theme.S(6);
            using (SolidBrush br = new SolidBrush(Color.White)) g.FillEllipse(br, kx - r, b.Y + b.Height / 2f - r, r * 2, r * 2);
            using (Pen p = new Pen(Muted ? Theme.InkFaint : Theme.AccentColor, Theme.S(1.5f))) g.DrawEllipse(p, kx - r, b.Y + b.Height / 2f - r, r * 2, r * 2);
        }

        protected override void OnMouseDown(System.Windows.Forms.MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
            if (IconR.Contains(e.Location))
            {
                if (Value < 0.005f) { Value = 0.6f; Muted = false; } else Muted = !Muted;
                Invalidate();
                if (Changed != null) Changed();
                return;
            }
            drag = true; Set(At(e.X), true);
        }
        protected override void OnMouseMove(System.Windows.Forms.MouseEventArgs e) { base.OnMouseMove(e); if (drag) Set(At(e.X), true); }
        protected override void OnMouseUp(System.Windows.Forms.MouseEventArgs e) { base.OnMouseUp(e); drag = false; }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
        protected override void OnMouseWheel(System.Windows.Forms.MouseEventArgs e) { base.OnMouseWheel(e); Set(Value + e.Delta / 120f * 0.05f, true); }
        protected override bool IsInputKey(System.Windows.Forms.Keys k) { return k == System.Windows.Forms.Keys.Left || k == System.Windows.Forms.Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(System.Windows.Forms.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            bool up = e.KeyCode == System.Windows.Forms.Keys.Up || e.KeyCode == (Theme.RTL ? System.Windows.Forms.Keys.Left : System.Windows.Forms.Keys.Right);
            bool down = e.KeyCode == System.Windows.Forms.Keys.Down || e.KeyCode == (Theme.RTL ? System.Windows.Forms.Keys.Right : System.Windows.Forms.Keys.Left);
            if (up) { Set(Value + 0.05f, true); e.Handled = true; }
            else if (down) { Set(Value - 0.05f, true); e.Handled = true; }
            else if (e.KeyCode == System.Windows.Forms.Keys.M) { Muted = !Muted; Invalidate(); if (Changed != null) Changed(); e.Handled = true; }
        }
        protected override string AccName { get { return L.T("vs_preview_vol"); } }
        protected override System.Windows.Forms.AccessibleRole AccRole { get { return System.Windows.Forms.AccessibleRole.Slider; } }
        protected override string AccValue { get { return Muted ? L.T("vs_muted") : ((int)Math.Round(Value * 100)) + "%"; } }
    }
}
