using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FileConverter
{
    /// A row of page pictures under a document in Studio: click one to go to that page.
    /// Edited pages carry a dot; the current page is outlined and kept in view.
    public class PageStrip : UiControl
    {
        public Func<int, string> PathOf;     // the picture to show for a page (the edited one when there is one)
        public Func<int, bool> Edited;
        public int Count, Current;
        public event Action<int> PagePicked;
        float scroll, target;
        int hover = -1;

        public PageStrip() { Surface = Surface.Background; Cursor = Cursors.Hand; }

        int TileW { get { return Theme.S(64); } }
        int TileH { get { return Height - Theme.S(30); } }
        int Gap { get { return Theme.S(10); } }
        int ContentW { get { return Count * (TileW + Gap) - Gap + Theme.S(24); } }
        float MaxScroll { get { return Math.Max(0, ContentW - Width); } }

        Rectangle Tile(int i)
        {
            int x = Theme.S(12) + i * (TileW + Gap) - (int)Math.Round(scroll);
            if (ContentW < Width) x += (Width - ContentW) / 2;   // few pages: centered
            Rectangle r = new Rectangle(x, Theme.S(8), TileW, TileH);
            return Theme.Mirror(r, Width);
        }

        int At(Point p) { for (int i = 0; i < Count; i++) if (Tile(i).Contains(p)) return i; return -1; }

        /// Brings a page into view (smoothly).
        public void Reveal(int i)
        {
            Current = i;
            float left = Theme.S(12) + i * (TileW + Gap);
            if (left - Gap < target) target = left - Gap;
            else if (left + TileW + Gap > target + Width) target = left + TileW + Gap - Width;
            target = Math.Max(0, Math.Min(MaxScroll, target));
            Animator.Start(this);
            Invalidate();
        }

        public override bool Step()
        {
            bool more = base.Step();
            if (Math.Abs(target - scroll) > 0.5f) { scroll += (target - scroll) * Animator.Rate(0.3f); Invalidate(); return true; }
            if (scroll != target) { scroll = target; Invalidate(); }
            return more;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            target = Math.Max(0, Math.Min(MaxScroll, target - e.Delta / 120f * (TileW + Gap) * 2));
            Animator.Start(this);
            base.OnMouseWheel(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = At(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i = At(e.Location);
            if (i >= 0 && i != Current && PagePicked != null) PagePicked(i);
        }

        void Repaint() { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            for (int i = 0; i < Count; i++)
            {
                Rectangle r = Tile(i);
                if (r.Right < 0 || r.X > Width) continue;
                bool cur = i == Current, hot = i == hover;
                if (hot && !cur) r.Offset(0, -Theme.S(2));   // a little lift under the mouse
                string p = PathOf != null ? PathOf(i) : null;
                if (p == null || !Thumbs.Draw(g, p, r, Theme.S(6), Repaint)) Theme.FillRound(g, r, Theme.S(6), Theme.CardInner);
                Theme.StrokeRound(g, Rectangle.Inflate(r, cur ? Theme.S(2) : 0, cur ? Theme.S(2) : 0), Theme.S(cur ? 8 : 6),
                                  cur ? Theme.AccentColor : hot ? Theme.Mix(Theme.CardBorder, Theme.Ink, 0.35f) : Theme.CardBorder, cur ? 2f : 1f);
                if (Edited != null && Edited(i))
                {
                    int d = Theme.S(10);
                    Rectangle dot = Theme.Mirror(new Rectangle(r.Right - d - Theme.S(4), r.Y + Theme.S(4), d, d), Width);
                    if (Theme.RTL) dot = new Rectangle(r.X + Theme.S(4), r.Y + Theme.S(4), d, d);
                    using (SolidBrush b = new SolidBrush(Theme.AccentColor)) g.FillEllipse(b, dot);
                    using (Pen pn = new Pen(Color.White, 1.5f)) g.DrawEllipse(pn, dot);
                }
                Theme.Text(g, (i + 1).ToString(), Theme.F(8.5f, cur ? FontStyle.Bold : FontStyle.Regular), cur ? Theme.Ink : Theme.InkSoft,
                           new Rectangle(r.X, r.Bottom + Theme.S(3), r.Width, Theme.S(18)), Align.Center, true, false, true);
            }
        }
    }
}
