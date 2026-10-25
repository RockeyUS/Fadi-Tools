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
