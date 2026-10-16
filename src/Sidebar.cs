using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FileConverter
{
    /// The side list: a rounded card floating on the window, with the tools in small groups (a heading and a
    /// thin line between them), counts as little orange pills, and Activity + Settings at the bottom.
    /// It folds into a slim rail of icons (counts become dots) and opens again with the arrow button.
    public class Sidebar : UiControl
    {
        public List<NavItem> Items = new List<NavItem>();
        int selected;
        float pillY = -1;                      // the selection glides between rows
        int hoverIndex = -1, hoverFoot = -1; bool hoverSearch, hoverToggle;
        readonly float[] footHot = new float[2]; float searchHot, toggleHot;
        readonly Ripple ripple = new Ripple();
        readonly ToolTip tips = new ToolTip { InitialDelay = 300 };
        string tipShown;
        public event Action<int> Navigate;
        public event Action OpenSearch, ToggleCollapse;
        public event Action<Rectangle> OpenActivity;
        /// what the Activity row shows: jobs running, and finished ones not looked at yet
        public int ActivityRunning, ActivityNew;
        /// True while the Activity panel is open (the row stays lit).
        public bool ActivityOpen;
        float actOpenT, actPulse = 1;
        /// Folded into icons only (the window animates the width).
        public bool Collapsed;
        float spin;

        public Sidebar() { Surface = Surface.Background; Cursor = Cursors.Hand; }

        public int Selected
        {
            get { return selected; }
            set { selected = value; if (pillY < 0 || !Visible || !Fx.Motion) pillY = SelRect(value).Y; Animator.Start(this); Invalidate(); }
        }
        public int SettingsIndex { get { return Items.Count; } }

        // ------------------------------------------------------------------ geometry (left-to-right; mirrored when painting)
        /// Folded when the control itself is narrow (also while the width animates).
        bool Narrow { get { return Width < Theme.S(150); } }
        Rectangle Card { get { return new Rectangle(Theme.S(10), Theme.S(6), Math.Max(10, Width - Theme.S(16)), Math.Max(10, Height - Theme.S(16))); } }
        int RowH { get { return rowH; } }
        int rowH = 36;
        int SecH { get { return Narrow ? Theme.S(18) : Theme.S(36); } }   // gap + line + heading before a group
        Rectangle LogoRect { get { Rectangle c = Card; int s = Theme.S(34); return Narrow ? new Rectangle(c.X + (c.Width - s) / 2, c.Y + Theme.S(14), s, s) : new Rectangle(c.X + Theme.S(14), c.Y + Theme.S(14), s, s); } }
        public Rectangle ToggleRect
        {
            get
            {
                Rectangle c = Card; int s = Theme.S(28);
                return Narrow ? new Rectangle(c.X + (c.Width - s) / 2, LogoRect.Bottom + Theme.S(8), s, s) : new Rectangle(c.Right - s - Theme.S(10), c.Y + Theme.S(17), s, s);
            }
        }
        /// The search box on the screen (the quick search grows out of it).
        public Rectangle SearchScreenRect { get { return RectangleToScreen(M(SearchRect)); } }

        public Rectangle SearchRect
        {
            get
            {
                Rectangle c = Card;
                int y = (Narrow ? ToggleRect.Bottom : LogoRect.Bottom) + Theme.S(12);
                return Narrow ? new Rectangle(c.X + (c.Width - Theme.S(40)) / 2, y, Theme.S(40), Theme.S(36)) : new Rectangle(c.X + Theme.S(10), y, c.Width - Theme.S(20), Theme.S(36));
            }
        }
        int NavTop { get { return SearchRect.Bottom + Theme.S(6); } }
        int FootTop { get { return Card.Bottom - Theme.S(10) - RowH * 2 - Theme.S(4) - (Narrow ? Theme.S(10) : Theme.S(28)); } }

        void Measure()
        {
            // rows tighten on short windows so the groups never run into Activity / Settings
            int groups = 0; foreach (NavItem n in Items) if (n.Section != null) groups++;
            for (int h = 38; h >= 30; h -= 2)
            {
                rowH = Theme.S(h);
                int need = NavTop + groups * SecH + Items.Count * rowH;
                if (need <= FootTop - Theme.S(8)) return;
            }
        }

        Rectangle ItemRect(int i)
        {
            int y = NavTop;
            for (int k = 0; k <= i && k < Items.Count; k++)
            {
                if (Items[k].Section != null) y += SecH;
                if (k < i) y += RowH;
            }
            Rectangle c = Card;
            return Narrow ? new Rectangle(c.X + (c.Width - Theme.S(44)) / 2, y, Theme.S(44), RowH - Theme.S(4)) : new Rectangle(c.X + Theme.S(8), y, c.Width - Theme.S(16), RowH - Theme.S(4));
        }
        /// 0 = Activity, 1 = Settings (the group at the bottom)
        public Rectangle FootRect(int i)
        {
            Rectangle c = Card;
            int y = FootTop + (Narrow ? Theme.S(10) : Theme.S(28)) + i * RowH;
            return Narrow ? new Rectangle(c.X + (c.Width - Theme.S(44)) / 2, y, Theme.S(44), RowH - Theme.S(4)) : new Rectangle(c.X + Theme.S(8), y, c.Width - Theme.S(16), RowH - Theme.S(4));
        }
        Rectangle SelRect(int i) { return i >= 0 && i < Items.Count ? ItemRect(i) : FootRect(1); }
        Rectangle M(Rectangle r) { return Theme.Mirror(r, Width); }
        Point MP(Point p) { return Theme.RTL ? new Point(Width - p.X, p.Y) : p; }   // mouse → layout coordinates

        public override bool Step()
        {
            bool more = false;
            for (int i = 0; i < Items.Count; i++) if (Fx.Ease(ref Items[i].Hot, i == hoverIndex ? 1 : 0, 0.28f)) more = true;
            for (int i = 0; i < 2; i++) if (Fx.Ease(ref footHot[i], i == hoverFoot ? 1 : 0, 0.28f)) more = true;
            if (Fx.Ease(ref searchHot, hoverSearch ? 1 : 0, 0.28f)) more = true;
            if (Fx.Ease(ref toggleHot, hoverToggle ? 1 : 0, 0.28f)) more = true;
            if (ripple.Step()) more = true;
            if (Fx.Ease(ref actOpenT, ActivityOpen ? 1 : 0, 0.25f)) more = true;
            if (actPulse < 1) { actPulse = Math.Min(1, actPulse + 0.028f * Animator.K); more = true; }
            float ty = SelRect(selected).Y;
            if (pillY < 0 || !Fx.Motion) pillY = ty;
            pillY += (ty - pillY) * Animator.Rate(0.22f);
            if (Math.Abs(ty - pillY) < 0.5f) pillY = ty; else more = true;
            if (ActivityRunning > 0) { spin = (spin + (int)Math.Round(6 * Animator.K)) % 360; more = true; }
            Invalidate();
            return more;
        }

        protected override void OnResize(EventArgs e) { Measure(); pillY = SelRect(selected).Y; base.OnResize(e); Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point p = MP(e.Location);
            int h = -1, f = -1;
            for (int i = 0; i < Items.Count; i++) if (ItemRect(i).Contains(p)) h = i;
            for (int i = 0; i < 2; i++) if (FootRect(i).Contains(p)) f = i;
            bool s = SearchRect.Contains(p), t = ToggleRect.Contains(p);
            if (h != hoverIndex || f != hoverFoot || s != hoverSearch || t != hoverToggle) { hoverIndex = h; hoverFoot = f; hoverSearch = s; hoverToggle = t; Animator.Start(this); }
            Cursor = (h >= 0 || f >= 0 || s || t) ? Cursors.Hand : Cursors.Default;
            // folded: the names show as tooltips
            string tip = !Narrow ? null : h >= 0 ? L.T(Items[h].Key) : f == 0 ? L.T("act_title") : f == 1 ? L.T("nav_settings") : s ? L.T("search_ph") : null;
            if (t) tip = L.T(Collapsed ? "nav_expand" : "nav_collapse");
            if (tip != tipShown) { tipShown = tip; tips.SetToolTip(this, tip ?? ""); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; hoverFoot = -1; hoverSearch = hoverToggle = false; Animator.Start(this); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            Point p = MP(e.Location);
            int nav = -1;
            for (int i = 0; i < Items.Count; i++) if (ItemRect(i).Contains(p)) nav = i;
            bool act = FootRect(0).Contains(p), set = FootRect(1).Contains(p), srch = SearchRect.Contains(p), tog = ToggleRect.Contains(p);
            base.OnMouseClick(e);
            if (set) nav = SettingsIndex;
            if (nav >= 0) { ripple.Start(e.Location, M(SelRect(nav))); Animator.Start(this); }
            else if (srch) { ripple.Start(e.Location, M(SearchRect)); Animator.Start(this); }
            if (nav >= 0 && Navigate != null) Navigate(nav);
            else if (tog && ToggleCollapse != null) ToggleCollapse();
            else if (srch && OpenSearch != null) OpenSearch();
            else if (act && OpenActivity != null)
            {
                // Activity's own click: the icon sends out two rings, like a pulse
                if (Fx.Motion && !ActivityOpen) { actPulse = 0; Animator.Start(this); }
                OpenActivity(M(FootRect(0)));
            }
        }

        // ------------------------------------------------------------------ painting
        Color CardFill { get { return Theme.Side; } }

        void Row(Graphics g, Rectangle r, string glyph, string text, Color fg, bool bold, int badge, bool dot)
        {
            if (Narrow)
            {
                Theme.Glyph(g, glyph, 11.5f, fg, M(r));
                // a count becomes a small orange dot on the icon
                if (badge > 0 || dot)
                {
                    Rectangle d = new Rectangle(r.X + r.Width / 2 + Theme.S(6), r.Y + Theme.S(5), Theme.S(7), Theme.S(7));
                    using (SolidBrush b = new SolidBrush(badge > 0 ? Theme.AccentColor : Theme.Success)) g.FillEllipse(b, M(d));
                }
                return;
            }
            Theme.Glyph(g, glyph, 10.5f, fg, M(new Rectangle(r.X + Theme.S(8), r.Y, Theme.S(22), r.Height)));
            int bw = 0;
            if (badge > 0)
            {
                string t = badge > 99 ? "99+" : badge.ToString();
                Font bf = Theme.F(7.75f, FontStyle.Bold);
                bw = Math.Max(Theme.S(22), Theme.Measure(t, bf) + Theme.S(12));
                Rectangle pr = new Rectangle(r.Right - bw - Theme.S(8), r.Y + (r.Height - Theme.S(20)) / 2, bw, Theme.S(20));
                Theme.FillRound(g, M(pr), Theme.S(10), Theme.AccentColor);
                Theme.Text(g, t, bf, Theme.PrimaryInk, M(pr), Align.Center, true, false, true);
                bw += Theme.S(12);
            }
            else if (dot)
            {
                Rectangle d = new Rectangle(r.Right - Theme.S(20), r.Y + r.Height / 2 - Theme.S(4), Theme.S(8), Theme.S(8));
                using (SolidBrush b = new SolidBrush(Theme.Success)) g.FillEllipse(b, M(d));
                bw = Theme.S(24);
            }
            Rectangle tr = new Rectangle(r.X + Theme.S(38), r.Y, r.Width - Theme.S(42) - bw, r.Height);
            Theme.Text(g, text, Theme.F(9.5f, bold ? FontStyle.Bold : FontStyle.Regular), fg, M(tr), Align.Start);
        }

        void Heading(Graphics g, int y, string key, bool line)
        {
            Rectangle c = Card;
            if (line) using (Pen p = new Pen(Theme.Line)) { Rectangle l = M(new Rectangle(c.X + Theme.S(16), y + Theme.S(6), c.Width - Theme.S(32), 1)); g.DrawLine(p, l.X, l.Y, l.Right, l.Y); }
            if (Narrow || key == null) return;
            Rectangle hr = new Rectangle(c.X + Theme.S(18), y + Theme.S(14), c.Width - Theme.S(36), Theme.S(18));
            Theme.Text(g, L.T(key).ToUpper(System.Globalization.CultureInfo.CurrentCulture), Theme.F(7.5f, FontStyle.Bold), Theme.InkFaint, M(hr), Align.Start);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Rectangle card = M(Card);
            using (GraphicsPath cp = Theme.Round(new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1, card.Height - 1), Theme.S(16)))
            {
                if (!Theme.Dark) using (SolidBrush sh = new SolidBrush(Color.FromArgb(10, 60, 40, 20))) { g.TranslateTransform(0, Theme.S(2)); g.FillPath(sh, cp); g.ResetTransform(); }
                using (SolidBrush b = new SolidBrush(CardFill)) g.FillPath(b, cp);
                using (Pen p = new Pen(Theme.Dark ? Color.FromArgb(18, 255, 255, 255) : Theme.Line)) g.DrawPath(p, cp);
            }

            // logo (and the name, when open)
            Rectangle logo = M(LogoRect);
            if (Theme.Logo != null)
                using (GraphicsPath lp = Theme.Round(logo, Theme.S(10)))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(lp); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(Theme.Logo, logo);
                    g.Restore(st);
                }
            if (!Narrow)
            {
                Rectangle name = new Rectangle(LogoRect.Right + Theme.S(10), LogoRect.Y - Theme.S(2), Card.Right - LogoRect.Right - Theme.S(50), Theme.S(22));
                Theme.Text(g, L.T("app_name"), Theme.Serif(13f), Theme.Ink, M(name), Align.Start);
                Rectangle tag = new Rectangle(name.X, name.Bottom, name.Width, Theme.S(16));
                Theme.Text(g, L.T("app_short"), Theme.F(7.75f), Theme.InkFaint, M(tag), Align.Start);
            }
            // fold / unfold
            Rectangle tg = M(ToggleRect);
            if (toggleHot > 0) Theme.FillRound(g, tg, Theme.S(8), Color.FromArgb((int)(255 * toggleHot), Theme.Hover));
            Theme.Glyph(g, Collapsed ? "" : "", 8f, Theme.Mix(Theme.InkFaint, Theme.Ink, toggleHot), tg);

            // search (opens the Ctrl+K palette)
            Rectangle sr = M(SearchRect);
            RectangleF srf = new RectangleF(sr.X + 0.5f, sr.Y + 0.5f, sr.Width - 1, sr.Height - 1);
            Theme.FillRound(g, srf, Theme.S(10), Theme.Mix(Theme.Dark ? Theme.Mix(CardFill, Color.White, 0.04f) : Theme.CardInner, Theme.Hover, searchHot));
            if (Narrow) Theme.Glyph(g, Ico.Search, 10f, Theme.Mix(Theme.InkFaint, Theme.Ink, searchHot), sr);
            else
            {
                Rectangle s0 = SearchRect;
                Theme.Glyph(g, Ico.Search, 9.5f, Theme.InkFaint, M(new Rectangle(s0.X + Theme.S(8), s0.Y, Theme.S(22), s0.Height)));
                Theme.Text(g, L.T("search_ph"), Theme.F(9f), Theme.Mix(Theme.InkFaint, Theme.InkSoft, searchHot), M(new Rectangle(s0.X + Theme.S(36), s0.Y, s0.Width - Theme.S(90), s0.Height)), Align.Start);
                Font kf = Theme.F(7.5f, FontStyle.Bold);
                int kw = Theme.Measure("Ctrl K", kf) + Theme.S(10);
                Rectangle kr = new Rectangle(s0.Right - kw - Theme.S(8), s0.Y + (s0.Height - Theme.S(20)) / 2, kw, Theme.S(20));
                Theme.FillRound(g, M(kr), Theme.S(6), Theme.Dark ? Theme.Mix(CardFill, Color.White, 0.08f) : Color.White);
                Theme.Text(g, "Ctrl K", kf, Theme.InkFaint, M(kr), Align.Center, true, false, true);
            }

            // groups: a thin line and a small heading before each
            bool first = true;
            for (int i = 0; i < Items.Count; i++)
                if (Items[i].Section != null)
                {
                    Rectangle r0 = ItemRect(i);
                    Heading(g, r0.Y - SecH, Items[i].Section, !first);
                    first = false;
                }
            Heading(g, FootTop, "nav_sec_app", true);

            // the selection, gliding between rows
            if (selected >= 0 && selected <= Items.Count)
            {
                Rectangle r0 = SelRect(selected);
                Rectangle pr = new Rectangle(r0.X, (int)(pillY < 0 ? r0.Y : pillY), r0.Width, r0.Height);
                Theme.FillRound(g, M(pr), Theme.S(9), Theme.Dark ? Theme.Mix(CardFill, Color.White, 0.09f) : Theme.Mix(Color.White, Theme.AccentColor, 0.09f));
            }

            for (int i = 0; i < Items.Count; i++)
            {
                Rectangle r = ItemRect(i);
                bool sel = i == selected;
                if (!sel && Items[i].Hot > 0) Theme.FillRound(g, M(r), Theme.S(9), Color.FromArgb((int)(255 * Items[i].Hot), Theme.Hover));
                Color fg = sel ? (Theme.Dark ? Theme.Ink : Theme.Mix(Theme.Ink, Theme.AccentColor, 0.35f)) : Theme.Mix(Theme.InkSoft, Theme.Ink, Items[i].Hot);
                int badge = Items[i].Badge != null ? Items[i].Badge() : 0;
                Row(g, r, Items[i].Glyph, L.T(Items[i].Key), fg, sel, badge, false);
                if (sel && Math.Abs(pillY - r.Y) < 2) Mark(g, r);
            }
            for (int i = 0; i < 2; i++)
            {
                Rectangle r = FootRect(i);
                bool sel = i == 1 && selected == SettingsIndex;
                if (!sel && footHot[i] > 0) Theme.FillRound(g, M(r), Theme.S(9), Color.FromArgb((int)(255 * footHot[i]), Theme.Hover));
                Color fg = sel ? (Theme.Dark ? Theme.Ink : Theme.Mix(Theme.Ink, Theme.AccentColor, 0.35f)) : Theme.Mix(Theme.InkSoft, Theme.Ink, footHot[i]);
                if (i == 1) { Row(g, r, Ico.Settings, L.T("nav_settings"), fg, sel, 0, false); if (sel && Math.Abs(pillY - r.Y) < 2) Mark(g, r); continue; }
                if (actOpenT > 0) Theme.FillRound(g, M(r), Theme.S(9), Color.FromArgb((int)(255 * actOpenT), Theme.Dark ? Theme.Selected : Theme.Mix(Theme.Selected, Theme.AccentColor, 0.08f)));
                Row(g, r, "", L.T("act_title"), ActivityRunning > 0 || actOpenT > 0.5f ? Theme.Ink : fg, false, ActivityRunning, ActivityNew > 0);
                if (actPulse < 1) Pulse(g, r);
                if (ActivityRunning > 0 && Narrow)
                {
                    // folded: a turning ring around the icon while something runs
                    Rectangle ring = new Rectangle(r.X + (r.Width - Theme.S(30)) / 2, r.Y + (r.Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
                    using (Pen ap = new Pen(Theme.AccentColor, Theme.S(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(ap, M(ring), spin, 90);
                }
            }
            if (ripple.Running) ripple.Paint(g, Theme.S(9), Theme.Dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        }

        void Pulse(Graphics g, Rectangle r)
        {
            Rectangle icon = Narrow ? r : new Rectangle(r.X + Theme.S(8), r.Y, Theme.S(22), r.Height);
            icon = M(icon);
            PointF c = new PointF(icon.X + icon.Width / 2f, icon.Y + icon.Height / 2f);
            for (int k = 0; k < 2; k++)
            {
                float p = (actPulse - k * 0.25f) / 0.75f;
                if (p <= 0 || p >= 1) continue;
                float e = 1 - (1 - p) * (1 - p) * (1 - p);
                float rad = Theme.S(6) + e * Theme.S(15);
                int a = (int)(170 * (1 - p) * (1 - p));
                using (Pen pen = new Pen(Color.FromArgb(a, Theme.AccentColor), Theme.S(1.5f)))
                    g.DrawEllipse(pen, c.X - rad, c.Y - rad, rad * 2, rad * 2);
            }
        }

        void Mark(Graphics g, Rectangle r)
        {
            if (Narrow) return;
            Rectangle mk = new Rectangle(r.X + Theme.S(2), r.Y + Theme.S(9), Theme.S(3), r.Height - Theme.S(18));
            Theme.FillRound(g, M(mk), Theme.S(1.5f), Theme.AccentColor);
        }
    }
}
