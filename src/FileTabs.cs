using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FileConverter
{
    /// The files open on the Studio's Photos / PDF side, as tabs (like a browser): click one to switch to it,
    /// × (or a middle click) to close it, + to start another. New tabs grow in, closed ones shrink away, and the
    /// white pill glides to the tab that's showing.
    public class FileTabs : UiControl
    {
        public class Tab
        {
            public Func<string> Title;
            public Func<string> Glyph;
            /// Unsaved changes: a dot where the × goes (the × shows when the mouse is on the tab).
            public Func<bool> Changed;
            public object Tag;
            internal float Grow;          // 0 → 1 as it appears, back to 0 as it closes
            public bool Closing { get; internal set; }
            internal float Hot, XHot;
        }

        public readonly List<Tab> Tabs = new List<Tab>();
        int selected;
        float pillX = -1, pillW;
        int hover = -1; bool hoverX, hoverPlus; float plusHot;
        readonly ToolTip tips = new ToolTip { InitialDelay = 400 };
        string tipShown;

        public event Action<Tab> Picked, CloseClicked;
        public event Action AddClicked;

        public FileTabs() { Cursor = Cursors.Hand; TabStop = true; Surface = Surface.Background; }

        public Tab Selected { get { return selected >= 0 && selected < Tabs.Count ? Tabs[selected] : null; } }

        public void Add(Tab t, bool select)
        {
            t.Grow = Visible && Fx.Motion ? 0 : 1;
            Tabs.Add(t);
            if (select) Select(t);
            Animator.Start(this); Invalidate();
        }

        /// Shrinks the tab away, then drops it.
        public void Remove(Tab t)
        {
            int i = Tabs.IndexOf(t);
            if (i < 0) return;
            if (!Visible || !Fx.Motion) { Tabs.RemoveAt(i); if (selected >= Tabs.Count) selected = Tabs.Count - 1; Invalidate(); return; }
            t.Closing = true;
            Animator.Start(this);
        }

        public void Select(Tab t)
        {
            int i = Tabs.IndexOf(t);
            if (i < 0) return;
            selected = i;
            if (pillX < 0 || !Visible || !Fx.Motion) { RectangleF r = TabRect(i); pillX = r.X; pillW = r.Width; }
            Animator.Start(this); Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Focus, -1);
        }

        // ------------------------------------------------------------------ geometry (left to right; mirrored when drawn)
        int H { get { return Height; } }
        int PlusW { get { return Theme.S(34); } }
        float Natural(Tab t)
        {
            Font f = Theme.F(9f, FontStyle.Bold);
            return Math.Max(Theme.S(110), Math.Min(Theme.S(230), Theme.Measure(t.Title(), f) + Theme.S(70)));
        }
        RectangleF[] Rects()
        {
            float avail = Width - PlusW - Theme.S(8), want = 0;
            foreach (Tab t in Tabs) want += Natural(t) * t.Grow + Theme.S(4) * t.Grow;
            float k = want > avail && want > 0 ? Math.Max(0.35f, avail / want) : 1f;
            RectangleF[] r = new RectangleF[Tabs.Count];
            float x = 0;
            for (int i = 0; i < Tabs.Count; i++)
            {
                float w = Natural(Tabs[i]) * Tabs[i].Grow * k;
                r[i] = new RectangleF(x, Theme.S(3), w, H - Theme.S(6));
                x += w + Theme.S(4) * Tabs[i].Grow;
            }
            return r;
        }
        RectangleF TabRect(int i) { RectangleF[] r = Rects(); return i >= 0 && i < r.Length ? r[i] : RectangleF.Empty; }
        RectangleF PlusRect
        {
            get
            {
                RectangleF[] r = Rects();
                float x = r.Length > 0 ? r[r.Length - 1].Right + Theme.S(6) : 0;
                return new RectangleF(x, (H - Theme.S(30)) / 2f, Theme.S(30), Theme.S(30));
            }
        }
        RectangleF XRect(RectangleF tab) { float s = Theme.S(20); return new RectangleF(tab.Right - s - Theme.S(8), tab.Y + (tab.Height - s) / 2, s, s); }
        RectangleF M(RectangleF r) { return Theme.RTL ? new RectangleF(Width - r.Right, r.Y, r.Width, r.Height) : r; }
        PointF MP(Point p) { return Theme.RTL ? new PointF(Width - p.X, p.Y) : p; }

        public override bool Step()
        {
            bool more = false;
            for (int i = Tabs.Count - 1; i >= 0; i--)
            {
                Tab t = Tabs[i];
                if (Fx.Ease(ref t.Grow, t.Closing ? 0 : 1, 0.3f)) more = true;
                if (Fx.Ease(ref t.Hot, i == hover ? 1 : 0, 0.3f)) more = true;
                if (Fx.Ease(ref t.XHot, i == hover && hoverX ? 1 : 0, 0.3f)) more = true;
                if (t.Closing && t.Grow <= 0.02f)
                {
                    Tabs.RemoveAt(i);
                    if (selected > i || selected >= Tabs.Count) selected = Math.Max(0, Math.Min(Tabs.Count - 1, selected - (selected > i ? 1 : 0)));
                    more = true;
                }
            }
            if (Fx.Ease(ref plusHot, hoverPlus ? 1 : 0, 0.3f)) more = true;
            RectangleF to = TabRect(selected);
            if (pillX < 0) { pillX = to.X; pillW = to.Width; }
            float r = Animator.Rate(0.25f);
            pillX += (to.X - pillX) * r; pillW += (to.Width - pillW) * r;
            if (Math.Abs(to.X - pillX) > 0.5f || Math.Abs(to.Width - pillW) > 0.5f) more = true; else { pillX = to.X; pillW = to.Width; }
            Invalidate();
            return more;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointF p = MP(e.Location);
            RectangleF[] r = Rects();
            int h = -1; bool hx = false;
            for (int i = 0; i < r.Length; i++) if (!Tabs[i].Closing && r[i].Contains(p)) { h = i; hx = XRect(r[i]).Contains(p); }
            bool hp = PlusRect.Contains(p);
            if (h != hover || hx != hoverX || hp != hoverPlus) { hover = h; hoverX = hx; hoverPlus = hp; Animator.Start(this); }
            string tip = hp ? L.T("tabs_new") : hx ? L.T("tabs_close") : h >= 0 ? Tabs[h].Title() : null;
            if (tip != tipShown) { tipShown = tip; tips.SetToolTip(this, tip ?? ""); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; hoverX = hoverPlus = false; Animator.Start(this); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            PointF p = MP(e.Location);
            if (PlusRect.Contains(p)) { if (e.Button == MouseButtons.Left && AddClicked != null) AddClicked(); return; }
            RectangleF[] r = Rects();
            for (int i = 0; i < r.Length; i++)
            {
                if (Tabs[i].Closing || !r[i].Contains(p)) continue;
                if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && XRect(r[i]).Contains(p))) { if (CloseClicked != null) CloseClicked(Tabs[i]); return; }
                if (e.Button == MouseButtons.Left && i != selected && Picked != null) Picked(Tabs[i]);
                return;
            }
        }

        // keyboard: ← → move between the tabs, Delete closes one
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Delete || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int d = e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Left ? -1 : 0;
            if (Theme.RTL) d = -d;
            if (d != 0 && Tabs.Count > 1) { int i = (selected + d + Tabs.Count) % Tabs.Count; if (Picked != null) Picked(Tabs[i]); e.Handled = true; }
            if (e.KeyCode == Keys.Delete && Selected != null && CloseClicked != null) { CloseClicked(Selected); e.Handled = true; }
        }
        protected override AccessibleRole AccRole { get { return AccessibleRole.PageTabList; } }
        protected override string AccValue { get { return Selected != null ? Selected.Title() : ""; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF[] r = Rects();
            // the pill under the tab that's showing
            if (selected >= 0 && selected < Tabs.Count && pillW > 1)
            {
                RectangleF pr = M(new RectangleF(pillX, Theme.S(3), pillW, H - Theme.S(6)));
                if (!Theme.Dark) using (SolidBrush sh = new SolidBrush(Color.FromArgb(12, 60, 40, 20))) using (System.Drawing.Drawing2D.GraphicsPath gp = Theme.Round(new RectangleF(pr.X, pr.Y + Theme.S(1.5f), pr.Width, pr.Height), Theme.S(9))) g.FillPath(sh, gp);
                Theme.FillRound(g, pr, Theme.S(9), Theme.Dark ? Theme.Mix(Theme.Card, Color.White, 0.06f) : Color.White);
                Theme.StrokeRound(g, pr, Theme.S(9), Theme.Dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(28, 60, 50, 30));
            }
            for (int i = 0; i < Tabs.Count; i++)
            {
                Tab t = Tabs[i];
                RectangleF tr = r[i];
                if (tr.Width < Theme.S(24)) continue;
                bool sel = i == selected;
                int alpha = (int)(255 * Math.Max(0, Math.Min(1, t.Grow * 1.4f - 0.4f)));
                if (!sel && t.Hot > 0) Theme.FillRound(g, M(tr), Theme.S(9), Color.FromArgb((int)(t.Hot * (Theme.Dark ? 20 : 120)), Theme.Dark ? Color.White : Color.White));
                Color fg = Color.FromArgb(alpha, sel ? Theme.Ink : Theme.Mix(Theme.InkSoft, Theme.Ink, t.Hot));
                RectangleF gr = new RectangleF(tr.X + Theme.S(10), tr.Y, Theme.S(18), tr.Height);
                Theme.Glyph(g, t.Glyph != null ? t.Glyph() : Ico.Photo, 9.5f, sel ? Color.FromArgb(alpha, Theme.AccentColor) : fg, Rectangle.Round(M(gr)));
                RectangleF xr = XRect(tr);
                RectangleF txt = new RectangleF(gr.Right + Theme.S(6), tr.Y, Math.Max(4, xr.X - gr.Right - Theme.S(10)), tr.Height);
                Theme.Text(g, t.Title(), Theme.F(9f, sel ? FontStyle.Bold : FontStyle.Regular), fg, Rectangle.Round(M(txt)), Align.Start);
                // × on the tab under the mouse (and the one showing); a dot for unsaved changes
                bool changed = t.Changed != null && t.Changed();
                if (i == hover || (sel && !changed))
                {
                    if (t.XHot > 0) Theme.FillRound(g, M(xr), xr.Width / 2, Color.FromArgb((int)(t.XHot * 255), Theme.Pressed));
                    Theme.Glyph(g, Ico.Close, 7f, Color.FromArgb(alpha, i == hover ? Theme.Ink : Theme.InkFaint), Rectangle.Round(M(xr)));
                }
                else if (changed)
                {
                    RectangleF d = new RectangleF(xr.X + xr.Width / 2 - Theme.S(4), xr.Y + xr.Height / 2 - Theme.S(4), Theme.S(8), Theme.S(8));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, Theme.AccentColor))) g.FillEllipse(b, M(d));
                }
            }
            // + : another tab
            RectangleF pl = PlusRect;
            if (plusHot > 0) Theme.FillRound(g, M(pl), Theme.S(8), Color.FromArgb((int)(plusHot * 255), Theme.Hover));
            Theme.Glyph(g, Ico.Add, 9f, Theme.Mix(Theme.InkSoft, Theme.Ink, plusHot), Rectangle.Round(M(pl)));
            if (KeyFocus && selected >= 0 && selected < r.Length) PaintFocus(g, M(r[selected]), Theme.S(9));
        }
    }
}
