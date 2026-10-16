using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace FileConverter
{
    /// One thing the quick search can do: open a page, start an action, flip a setting.
    public class PaletteItem
    {
        public string Title, Sub, Glyph, Group, Words = "";
        public string Shortcut;
        public Action Run;
    }

    /// Ctrl+K: type a few letters and jump anywhere, Claude-style. Arrow keys move, Enter opens, Esc closes.
    public class CommandPalette : Form
    {
        readonly TextField search;
        readonly PaletteRows rows = new PaletteRows();
        readonly List<PaletteItem> all;
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        static CommandPalette open;
        public static bool IsOpen { get { return open != null; } }

        public static void Show(Form owner, List<PaletteItem> items) { Show(owner, items, Rectangle.Empty); }

        /// from = where it was opened from on the screen (the search box): it grows out of there.
        public static void Show(Form owner, List<PaletteItem> items, Rectangle from)
        {
            if (open != null) { open.Dismiss(); return; }
            CommandPalette p = new CommandPalette(items);
            open = p;
            p.FormClosed += (s, e) => open = null;
            Rectangle o = owner.Bounds;
            int w = Math.Min(Theme.S(600), o.Width - Theme.S(80));
            int h = Theme.S(470);
            p.Bounds = new Rectangle(o.X + (o.Width - w) / 2, o.Y + Math.Min(Theme.S(110), Math.Max(Theme.S(40), (o.Height - h) / 3)), w, h);
            p.LayoutInside();
            p.search.Restyle();
            p.owner = owner;
            p.from = from.IsEmpty ? new Rectangle(p.Left + w / 2, p.Top, 1, 1) : from;
            IntPtr hh = p.Handle;   // made now, so its picture can be taken before it shows
            Fx.PopIn(p, owner, p.from, Theme.S(10), 160, () =>
            {
                if (p.IsDisposed) return;
                p.Show(owner);
                p.search.Box.Focus();
            });
        }

        Form owner; Rectangle from; bool closing;

        /// Shrinks back towards the search box and fades out.
        public void Dismiss()
        {
            if (closing || IsDisposed) return;
            closing = true;
            Fx.PopOut(this, owner, from, Theme.S(10), 110, () => { if (!IsDisposed) Close(); });
        }

        CommandPalette(List<PaletteItem> items)
        {
            all = items;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Card;
            Font = Theme.F(10f);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            search = new TextField(() => L.T("pal_ph"), Ico.Search) { Surface = Surface.Card };
            search.Box.TextChanged += (s, e) => Rebuild();
            search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == System.Windows.Forms.Keys.Down) { rows.Move(1); e.Handled = true; }
                else if (e.KeyCode == System.Windows.Forms.Keys.Up) { rows.Move(-1); e.Handled = true; }
                else if (e.KeyCode == System.Windows.Forms.Keys.PageDown) { rows.Move(6); e.Handled = true; }
                else if (e.KeyCode == System.Windows.Forms.Keys.PageUp) { rows.Move(-6); e.Handled = true; }
                else if (e.KeyCode == System.Windows.Forms.Keys.Enter) { e.SuppressKeyPress = true; Pick(rows.Current); }
            };
            rows.Picked += Pick;
            Controls.Add(search); Controls.Add(rows);
            Deactivate += (s, e) => Dismiss();
            Rebuild();
        }

        void LayoutInside()
        {
            int pad = Theme.S(10);
            search.SetBounds(pad, pad, Width - pad * 2, Theme.S(44));
            rows.SetBounds(Theme.S(6), pad + Theme.S(52), Width - Theme.S(12), Height - pad - Theme.S(52) - Theme.S(34));
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // drop shadow
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(20);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape || k == (Keys.Control | Keys.K)) { Dismiss(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        void Pick(PaletteItem it)
        {
            if (it == null) return;
            Form owner = Owner ?? this.owner;
            Dismiss();
            // run after the palette is gone, so dialogs it opens get the focus
            try { if (owner != null && owner.IsHandleCreated) owner.BeginInvoke(it.Run); else it.Run(); } catch { }
        }

        /// Letters typed in order anywhere in the text ("cmpdis" finds "Compress for Discord").
        static int Score(PaletteItem it, string q)
        {
            if (q.Length == 0) return 1;
            CompareInfo ci = CultureInfo.InvariantCulture.CompareInfo;
            CompareOptions o = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            string t = it.Title ?? "";
            if (ci.IsPrefix(t, q, o)) return 100;
            foreach (string w in t.Split(' ', '·', '(', '-')) if (w.Length > 0 && ci.IsPrefix(w, q, o)) return 80;
            if (ci.IndexOf(t, q, o) >= 0) return 60;
            if (ci.IndexOf(it.Words ?? "", q, o) >= 0) return 45;
            if (ci.IndexOf(it.Sub ?? "", q, o) >= 0) return 30;
            // letters typed in order in the title, close together ("cmpdis" → "Compress for Discord")
            if (q.Length < 3) return 0;
            string hay = t.ToLowerInvariant(); int k = 0, first = -1, last = -1;
            for (int i = 0; i < hay.Length && k < q.Length; i++)
                if (hay[i] == char.ToLowerInvariant(q[k])) { if (first < 0) first = i; last = i; k++; }
            return k == q.Length && last - first <= q.Length * 4 ? 10 : 0;
        }

        void Rebuild()
        {
            string q = search.Box.Text.Trim();
            List<PaletteRows.Row> list = new List<PaletteRows.Row>();
            if (q.Length == 0)
            {
                string group = null;
                foreach (PaletteItem it in all)
                {
                    if (it.Group != group) { group = it.Group; list.Add(new PaletteRows.Row { Header = group }); }
                    list.Add(new PaletteRows.Row { Item = it });
                }
            }
            else
            {
                List<KeyValuePair<int, PaletteItem>> hits = new List<KeyValuePair<int, PaletteItem>>();
                for (int i = 0; i < all.Count; i++)
                {
                    int sc = Score(all[i], q);
                    if (sc > 0) hits.Add(new KeyValuePair<int, PaletteItem>(sc * 1000 - i, all[i]));
                }
                hits.Sort((a, b) => b.Key.CompareTo(a.Key));
                foreach (var h in hits) list.Add(new PaletteRows.Row { Item = h.Value });
            }
            rows.SetRows(list);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(10)))
            using (Pen pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, p);
            // footer: how to use it
            int fy = Height - Theme.S(32);
            using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, Theme.S(12), fy, Width - Theme.S(12), fy);
            Font kf = Theme.F(7.75f, FontStyle.Bold), tf = Theme.F(8.25f);
            int x = Theme.S(16);
            foreach (string[] k in new[] { new[] { "↑↓", L.T("pal_move") }, new[] { "Enter", L.T("pal_open") }, new[] { "Esc", L.T("pal_close") } })
            {
                int kw = Theme.Measure(k[0], kf) + Theme.S(10);
                Rectangle kr = Theme.Mirror(new Rectangle(x, fy + Theme.S(7), kw, Theme.S(19)), Width);
                Theme.FillRound(g, kr, Theme.S(5), Theme.Selected);
                Theme.Text(g, k[0], kf, Theme.InkSoft, kr, Align.Center, true, false, true);
                x += kw + Theme.S(6);
                int tw = Theme.Measure(k[1], tf) + Theme.S(4);
                Theme.Text(g, k[1], tf, Theme.InkFaint, Theme.Mirror(new Rectangle(x, fy + Theme.S(6), tw, Theme.S(21)), Width), Align.Start);
                x += tw + Theme.S(16);
            }
        }
    }

    public class PaletteRows : ScrollList
    {
        public class Row { public PaletteItem Item; public string Header; }
        List<Row> rows = new List<Row>();
        int cursor = -1;
        public event Action<PaletteItem> Picked;
        public PaletteRows() { RowHeight = 46; Surface = Surface.Card; Cursor = Cursors.Hand; }
        public override int Count { get { return rows.Count; } }
        public PaletteItem Current { get { return cursor >= 0 && cursor < rows.Count ? rows[cursor].Item : null; } }

        public void SetRows(List<Row> r)
        {
            rows = r; cursor = -1;
            for (int i = 0; i < rows.Count; i++) if (rows[i].Item != null) { cursor = i; break; }
            ScrollTo(0); Invalidate();
        }

        public void Move(int d)
        {
            if (rows.Count == 0) return;
            int i = cursor, step = Math.Sign(d), n = Math.Abs(d);
            while (n > 0)
            {
                int j = i + step;
                while (j >= 0 && j < rows.Count && rows[j].Item == null) j += step;
                if (j < 0 || j >= rows.Count) break;
                i = j; n--;
            }
            cursor = i;
            // keep a header in view when moving to the first item of a group
            EnsureVisible(cursor > 0 && rows[cursor - 1].Item == null && step < 0 ? cursor - 1 : cursor);
            Invalidate();
        }

        protected override void RowClick(int index, Point local, Rectangle row) { if (rows[index].Item != null && Picked != null) Picked(rows[index].Item); }
        protected override void RowHover(int index, Point local, Rectangle row) { if (rows[index].Item != null && cursor != index) { cursor = index; Invalidate(); } }

        protected override void PaintEmpty(Graphics g)
        {
            Theme.Text(g, L.T("no_matches"), Theme.F(9.5f), Theme.InkFaint, new Rectangle(0, Theme.S(24), Width, Theme.S(30)), Align.Center);
        }

        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            Row row = rows[i];
            Theme.Smooth(g);
            if (row.Item == null)
            {
                Theme.Text(g, row.Header.ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint,
                    Theme.Mirror(new Rectangle(r.X + Theme.S(14), r.Y + Theme.S(16), r.Width - Theme.S(28), r.Height - Theme.S(16)), Width), Align.Start, true, false);
                return;
            }
            PaletteItem it = row.Item;
            Rectangle inner = new Rectangle(r.X + Theme.S(4), r.Y + Theme.S(2), r.Width - Theme.S(8), r.Height - Theme.S(4));
            bool cur = i == cursor;
            if (cur) Theme.FillRound(g, inner, Theme.S(10), Theme.Hover);
            Rectangle tile = Theme.Mirror(new Rectangle(inner.X + Theme.S(8), inner.Y + (inner.Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30)), Width);
            Theme.FillRound(g, tile, Theme.S(8), cur ? Theme.AccentSoft : Theme.CardInner);
            Theme.Glyph(g, it.Glyph ?? Ico.Forward, 10f, cur ? Theme.AccentColor : Theme.InkSoft, tile);
            int tx = inner.X + Theme.S(48), tw = inner.Width - Theme.S(48) - Theme.S(110);
            Font tf = Theme.F(9.75f, FontStyle.Bold);
            int titleW = Math.Min(tw, Theme.Measure(it.Title, tf) + Theme.S(6));
            Theme.Text(g, it.Title, tf, Theme.Ink, Theme.Mirror(new Rectangle(tx, inner.Y, titleW, inner.Height), Width), Align.Start, true, false);
            if (!string.IsNullOrEmpty(it.Sub) && tw - titleW > Theme.S(60))
                Theme.Text(g, it.Sub, Theme.F(8.75f), Theme.InkFaint, Theme.Mirror(new Rectangle(tx + titleW + Theme.S(6), inner.Y, tw - titleW - Theme.S(6), inner.Height), Width), Align.Start, true, false);
            if (!string.IsNullOrEmpty(it.Shortcut))
            {
                Font kf = Theme.F(7.75f, FontStyle.Bold);
                int kw = Theme.Measure(it.Shortcut, kf) + Theme.S(10);
                Rectangle kr = Theme.Mirror(new Rectangle(inner.Right - kw - Theme.S(10), inner.Y + (inner.Height - Theme.S(20)) / 2, kw, Theme.S(20)), Width);
                Theme.FillRound(g, kr, Theme.S(5), Theme.Selected);
                Theme.Text(g, it.Shortcut, kf, Theme.InkFaint, kr, Align.Center, true, false, true);
            }
            else if (cur)
                Theme.Glyph(g, Theme.RTL ? Ico.Back : Ico.Forward, 8.5f, Theme.InkFaint, Theme.Mirror(new Rectangle(inner.Right - Theme.S(34), inner.Y, Theme.S(24), inner.Height), Width));
        }
    }
}

namespace FileConverter
{
    /// A small floating menu (Claude-style): icon + text rows, optional section titles, dividers and shortcuts.
    /// It grows out of the spot it was opened from and shrinks back when it closes; the row under the mouse
    /// lights up softly. Arrow keys move, Enter picks, Esc closes.
    public class MenuPopup : Form
    {
        public class Item { public string Text, Sub, Glyph, Header, Shortcut; public Action Run; public bool Divider, Disabled, Danger; }
        readonly List<Item> items;
        readonly MenuRows rows;
        Rectangle from;
        Form owner;
        bool closing;
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        static MenuPopup open;

        /// Under (or above) a button.
        public static void Show(Control anchor, List<Item> items, int width = 300)
        {
            Rectangle a = anchor.RectangleToScreen(anchor.ClientRectangle);
            Open(anchor, items, width, a, false);
        }

        /// At the mouse (a right click).
        public static void ShowAt(Control over, Point screen, List<Item> items, int width = 270)
        {
            Open(over, items, width, new Rectangle(screen, new Size(1, 1)), true);
        }

        static void Open(Control anchor, List<Item> items, int width, Rectangle a, bool atPoint)
        {
            if (open != null && !open.IsDisposed) { try { open.Close(); } catch { } }
            MenuPopup m = new MenuPopup(items);
            Rectangle wa = Screen.FromPoint(a.Location).WorkingArea;
            int w = Theme.S(width), h = m.rows.ContentHeight + Theme.S(12);
            h = Math.Min(h, Theme.S(520));
            int x, y;
            if (atPoint)
            {
                x = Theme.RTL ? a.X - w : a.X; y = a.Y;
                if (x + w > wa.Right) x = a.X - w;
                if (x < wa.Left) x = a.X;
                if (y + h > wa.Bottom) y = a.Y - h;
            }
            else
            {
                x = Theme.RTL ? a.Right - w : a.X; y = a.Bottom + Theme.S(4);
                if (y + h > wa.Bottom) y = Math.Max(wa.Top, a.Top - h - Theme.S(4));
            }
            x = Math.Max(wa.Left, Math.Min(wa.Right - w, x));
            y = Math.Max(wa.Top, Math.Min(wa.Bottom - h, y));
            m.Bounds = new Rectangle(x, y, w, h);
            m.rows.SetBounds(Theme.S(6), Theme.S(6), w - Theme.S(12), h - Theme.S(12));
            m.from = a; m.owner = anchor.FindForm();
            open = m;
            IntPtr hh = m.Handle;   // made now, so its picture can be taken before it shows
            Fx.PopIn(m, m.owner, a, Theme.S(12), 140, () => { if (!m.IsDisposed) { m.Show(m.owner); m.rows.Focus(); } });
        }

        MenuPopup(List<Item> items)
        {
            this.items = items;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Card;
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            rows = new MenuRows(items);
            rows.Picked += it =>
            {
                Form o = owner;
                Dismiss(true);
                try { if (o != null && o.IsHandleCreated) o.BeginInvoke(it.Run); else it.Run(); } catch { }
            };
            Controls.Add(rows);
            Deactivate += (s, e) => Dismiss(false);
        }

        /// Shrinks back towards where it came from and closes (quickly when something was picked).
        void Dismiss(bool picked)
        {
            if (closing || IsDisposed) return;
            closing = true;
            if (open == this) open = null;
            Fx.PopOut(this, owner, from, Theme.S(12), picked ? 80 : 110, () => { if (!IsDisposed) Close(); });
        }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(16);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Dismiss(false); return true; }
            if (k == Keys.Down || k == Keys.Up) { rows.MoveCursor(k == Keys.Down ? 1 : -1); return true; }
            if (k == Keys.Enter || k == Keys.Space) { rows.PickCursor(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Card);
            Theme.Smooth(e.Graphics);
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(8)))
            using (Pen pen = new Pen(Theme.CardBorder)) e.Graphics.DrawPath(pen, p);
        }

        class MenuRows : UiControl
        {
            readonly List<Item> items;
            readonly float[] rowHot;
            int hover = -1;
            public event Action<Item> Picked;
            public MenuRows(List<Item> items) { this.items = items; rowHot = new float[items.Count]; Surface = Surface.Card; Cursor = Cursors.Hand; TabStop = true; }
            int H(Item it) { return it.Divider ? Theme.S(11) : it.Header != null ? Theme.S(28) : it.Sub != null ? Theme.S(48) : Theme.S(36); }
            public int ContentHeight { get { int h = 0; foreach (Item it in items) h += H(it); return h; } }
            Rectangle R(int i) { int y = 0; for (int k = 0; k < i; k++) y += H(items[k]); return new Rectangle(0, y, Width, H(items[i])); }
            int At(Point p) { for (int i = 0; i < items.Count; i++) if (R(i).Contains(p)) return i; return -1; }
            bool Pickable(int i) { return i >= 0 && i < items.Count && !items[i].Divider && items[i].Header == null && !items[i].Disabled; }
            void SetHover(int h) { if (h != hover) { hover = h; Animator.Start(this); } }
            public void MoveCursor(int d)
            {
                int i = hover;
                for (int n = 0; n < items.Count; n++)
                {
                    i = i < 0 ? (d > 0 ? 0 : items.Count - 1) : (i + d + items.Count) % items.Count;
                    if (Pickable(i)) { SetHover(i); return; }
                }
            }
            public void PickCursor() { if (Pickable(hover) && Picked != null) Picked(items[hover]); }
            public override bool Step()
            {
                bool more = false;
                for (int i = 0; i < rowHot.Length; i++) if (Fx.Ease(ref rowHot[i], i == hover ? 1 : 0, 0.35f)) more = true;
                Invalidate();
                return more;
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                int h = At(e.Location);
                SetHover(Pickable(h) ? h : -1);
                base.OnMouseMove(e);
            }
            protected override void OnMouseLeave(EventArgs e) { SetHover(-1); base.OnMouseLeave(e); }
            protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); int i = At(e.Location); if (Pickable(i) && Picked != null) Picked(items[i]); }
            protected override AccessibleRole AccRole { get { return AccessibleRole.MenuPopup; } }
            protected override string AccValue { get { return Pickable(hover) ? items[hover].Text : ""; } }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                PaintBg(g);
                Theme.Smooth(g);
                for (int i = 0; i < items.Count; i++)
                {
                    Item it = items[i]; Rectangle r = R(i);
                    if (it.Divider) { using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, r.X + Theme.S(8), r.Y + r.Height / 2, r.Right - Theme.S(8), r.Y + r.Height / 2); continue; }
                    if (it.Header != null)
                    {
                        Theme.Text(g, it.Header.ToUpper(CultureInfo.CurrentCulture), Theme.F(7.5f, FontStyle.Bold), Theme.InkFaint, Theme.Mirror(new Rectangle(r.X + Theme.S(10), r.Y + Theme.S(8), r.Width - Theme.S(20), r.Height - Theme.S(8)), Width), Align.Start);
                        continue;
                    }
                    if (rowHot[i] > 0.01f)
                    {
                        Color hc = it.Danger ? Theme.ErrorBg : Theme.Hover;
                        Theme.FillRound(g, r, Theme.S(8), Color.FromArgb((int)(255 * rowHot[i]), hc));
                    }
                    Color ink = it.Disabled ? Theme.InkFaint : it.Danger ? Theme.Error : Theme.Ink;
                    Color gl = it.Disabled ? Theme.InkFaint : it.Danger ? Theme.Error : Theme.Mix(Theme.InkSoft, Theme.Ink, rowHot[i]);
                    // the icon nudges a little to the side as the row lights up
                    int nudge = (int)Math.Round(Theme.S(2) * rowHot[i]);
                    Theme.Glyph(g, it.Glyph ?? "", 10f, gl, Theme.Mirror(new Rectangle(r.X + Theme.S(8) + nudge, r.Y, Theme.S(24), it.Sub != null ? Theme.S(30) + Theme.S(4) : r.Height), Width));
                    int sw = 0;
                    if (!string.IsNullOrEmpty(it.Shortcut))
                    {
                        Font kf = Theme.F(7.75f, FontStyle.Bold);
                        sw = Theme.Measure(it.Shortcut, kf) + Theme.S(12);
                        Rectangle kr = Theme.Mirror(new Rectangle(r.Right - sw - Theme.S(8), r.Y + (r.Height - Theme.S(20)) / 2, sw, Theme.S(20)), Width);
                        Theme.FillRound(g, kr, Theme.S(5), Theme.Selected);
                        Theme.Text(g, it.Shortcut, kf, Theme.InkFaint, kr, Align.Center, true, false, true);
                        sw += Theme.S(12);
                    }
                    int tx = r.X + Theme.S(40), tw = r.Width - Theme.S(48) - sw;
                    if (it.Sub != null)
                    {
                        Theme.Text(g, it.Text, Theme.F(9.5f), ink, Theme.Mirror(new Rectangle(tx, r.Y + Theme.S(6), tw, Theme.S(20)), Width), Align.Start, true, false);
                        Theme.Text(g, it.Sub, Theme.F(8f), Theme.InkFaint, Theme.Mirror(new Rectangle(tx, r.Y + Theme.S(25), tw, Theme.S(17)), Width), Align.Start, true, false);
                    }
                    else Theme.Text(g, it.Text, Theme.F(9.5f), ink, Theme.Mirror(new Rectangle(tx, r.Y, tw, r.Height), Width), Align.Start, true, false);
                }
            }
        }
    }
}
