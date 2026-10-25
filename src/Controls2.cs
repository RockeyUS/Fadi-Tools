using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FileConverter
{
    /// Horizontal slider. The knob glides to its target, so dragging feels smooth.
    public class Slider : UiControl
    {
        public float Min = 0, Max = 100, Value = 50;
        public Func<float, string> Format = v => ((int)Math.Round(v)).ToString();
        public event EventHandler ValueChanged;
        public event EventHandler Committed;          // mouse released / wheel: good moment for expensive work
        bool dragging;
        float shown = float.NaN, press;
        public Slider() { Cursor = Cursors.Hand; TabStop = true; }
        // keyboard: ← → (and Page Up / Down for bigger steps)
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            int d = e.KeyCode == Keys.Right || e.KeyCode == Keys.Up ? 1 : e.KeyCode == Keys.Left || e.KeyCode == Keys.Down ? -1 : e.KeyCode == Keys.PageUp ? 5 : e.KeyCode == Keys.PageDown ? -5 : 0;
            if (Theme.RTL && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)) d = -d;
            if (d == 0) return;
            e.Handled = true;
            float f = Math.Max(0, Math.Min(1, FracOf(Value) + d / 50f));
            SetValue(ValueAt(f), true);
            if (Committed != null) Committed(this, EventArgs.Empty);
        }
        protected override AccessibleRole AccRole { get { return AccessibleRole.Slider; } }
        protected override string AccValue { get { return Format(Value); } }

        /// Sizes: equal steps feel the same at 20 px and at 800 px (each step is a share of the value, not a fixed amount).
        public bool Curved;
        bool LogScale { get { return Curved && Min > 0 && Max > Min * 1.01f; } }
        Rectangle Track { get { int lw = Theme.S(50); return new Rectangle(Theme.S(10), Height / 2 - Theme.S(2), Width - Theme.S(22) - lw, Theme.S(4)); } }
        float FracOf(float v)
        {
            if (!(Max > Min)) return 0;
            if (LogScale) return (float)Math.Max(0, Math.Min(1, Math.Log(Math.Max(Min, v) / Min) / Math.Log(Max / Min)));
            return Math.Max(0, Math.Min(1, (v - Min) / (Max - Min)));
        }
        float ValueAt(float f) { return LogScale ? (float)(Min * Math.Pow(Max / Min, f)) : Min + f * (Max - Min); }

        public void SetValue(float v, bool notify)
        {
            v = Math.Max(Min, Math.Min(Max, v));
            if (Math.Abs(v - Value) < 0.0001f) return;
            Value = v;
            if (float.IsNaN(shown) || !Visible) shown = v;
            Animator.Start(this);
            Invalidate();
            if (notify && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        void FromMouse(int x)
        {
            Rectangle t = Track;
            float f = (x - t.X) / (float)Math.Max(1, t.Width);
            if (Theme.RTL) f = 1 - (x - (Width - t.Right)) / (float)Math.Max(1, t.Width);
            SetValue(ValueAt(Math.Max(0, Math.Min(1, f))), true);
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left && Enabled) { dragging = true; Capture = true; FromMouse(e.X); Animator.Start(this); } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) FromMouse(e.X); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging) { dragging = false; Capture = false; Animator.Start(this); if (Committed != null) Committed(this, EventArgs.Empty); }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!Enabled) return;
            SetValue(LogScale ? Value * (float)Math.Pow(1.04, Math.Sign(e.Delta)) : Value + (Max - Min) / 50f * Math.Sign(e.Delta), true);
            if (Committed != null) Committed(this, EventArgs.Empty);
        }

        public override bool Step()
        {
            bool more = base.Step();
            if (float.IsNaN(shown)) shown = Value;
            float k = Animator.Rate(dragging ? 0.55f : 0.28f);
            if (LogScale) { float fs = FracOf(shown), fv = FracOf(Value); fs += (fv - fs) * k; shown = Math.Abs(fv - fs) < 0.0005f ? Value : ValueAt(fs); }
            else shown += (Value - shown) * k;
            if (Math.Abs(Value - shown) < (Max - Min) / 2000f) shown = Value;
            float pt = dragging ? 1 : 0;
            press += (pt - press) * Animator.Rate(0.35f);
            if (Math.Abs(pt - press) < 0.02f) press = pt;
            Invalidate();
            return more || shown != Value || press != pt;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            if (float.IsNaN(shown)) shown = Value;
            Rectangle t = Theme.Mirror(Track, Width);
            Theme.FillRound(g, t, t.Height / 2f, Theme.Track);
            float fw = t.Width * FracOf(shown);
            RectangleF fill = Theme.RTL ? new RectangleF(t.Right - fw, t.Y, fw, t.Height) : new RectangleF(t.X, t.Y, fw, t.Height);
            if (fw > 0.5f)
            {
                Theme.FillRound(g, fill, t.Height / 2f, Enabled ? Theme.AccentColor : Theme.InkFaint);
            }
            float kx = Theme.RTL ? t.Right - fw : t.X + fw;
            float k = Theme.S(16) + hot * Theme.S(2) + press * Theme.S(3);
            RectangleF kr = new RectangleF(kx - k / 2, Height / 2f - k / 2, k, k);
            if (press > 0.01f || hot > 0.01f)
                using (SolidBrush halo = new SolidBrush(Color.FromArgb((int)(28 * Math.Max(press, hot * 0.6f)), Theme.Ink)))
                    g.FillEllipse(halo, RectangleF.Inflate(kr, Theme.S(6), Theme.S(6)));
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(55, 0, 0, 0))) g.FillEllipse(sh, kr.X, kr.Y + 1.5f, kr.Width, kr.Height);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, kr);
            using (Pen p = new Pen(Theme.Mix(Theme.ChipBorder, Theme.InkSoft, 0.5f))) g.DrawEllipse(p, kr);
            Rectangle lr = Theme.Mirror(new Rectangle(Width - Theme.S(50), 0, Theme.S(50), Height), Width);
            Theme.Text(g, Format(Value), Theme.F(8.75f, FontStyle.Bold), dragging ? Theme.Ink : Theme.InkSoft, lr, Theme.RTL ? Align.Start : Align.End, true, false, true);
        }
    }

    /// Row of color circles plus a "more colors" button.
    public class Swatches : UiControl
    {
        public static readonly Color[] Colors = {
            Color.White, Color.Black, Color.FromArgb(239, 68, 68), Color.FromArgb(249, 115, 22), Color.FromArgb(250, 204, 21),
            Color.FromArgb(34, 197, 94), Color.FromArgb(14, 165, 233), Color.FromArgb(168, 85, 247) };
        public Color Selected = Color.White;
        public Color[] Palette = Colors;
        public bool LivePreview = true;      // the custom picker updates the picture while you drag
        public event EventHandler SelectedChanged;
        int hoverIndex = -1;
        public Swatches() { Cursor = Cursors.Hand; TabStop = true; }
        // keyboard: ← → pick the colour beside it
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled || Palette.Length == 0) return;
            int d = e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Left ? -1 : 0;
            if (Theme.RTL) d = -d;
            if (d == 0) return;
            e.Handled = true;
            int i = Math.Max(0, Array.FindIndex(Palette, c => c.ToArgb() == Selected.ToArgb()));
            Selected = Palette[Math.Max(0, Math.Min(Palette.Length - 1, i + d))];
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
        }
        protected override AccessibleRole AccRole { get { return AccessibleRole.List; } }
        protected override string AccValue { get { return "#" + Selected.R.ToString("X2") + Selected.G.ToString("X2") + Selected.B.ToString("X2"); } }

        Rectangle Cell(int i)
        {
            int n = Palette.Length + 1, inset = Theme.S(4), avail = Width - inset * 2;
            int s = Math.Min(Theme.S(24), avail / n - Theme.S(5));
            int gap = (avail - n * s) / Math.Max(1, n - 1);
            Rectangle r = new Rectangle(inset + i * (s + gap), (Height - s) / 2, s, s);
            return Theme.Mirror(r, Width);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; for (int i = 0; i <= Palette.Length; i++) if (Cell(i).Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int hit = -1; for (int i = 0; i <= Palette.Length; i++) if (Cell(i).Contains(e.Location)) { hit = i; break; }
            if (hit < 0) return;
            if (hit == Palette.Length)
            {
                Color before = Selected, picked;
                Action<Color> live = null;
                if (LivePreview) live = c => { Selected = c; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); };
                if (!ColorPicker.Pick(FindForm(), Selected, live, out picked)) { Selected = before; Invalidate(); return; }
                Selected = picked;
            }
            else Selected = Palette[hit];
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            bool matched = false;
            for (int i = 0; i <= Palette.Length; i++)
            {
                Rectangle r = Cell(i);
                if (i < Palette.Length)
                {
                    bool sel = Palette[i].ToArgb() == Selected.ToArgb();
                    matched |= sel;
                    if (sel) using (Pen p = new Pen(Theme.Ink, Theme.S(2))) g.DrawEllipse(p, Rectangle.Inflate(r, Theme.S(3), Theme.S(3)));
                    using (SolidBrush b = new SolidBrush(Palette[i])) g.FillEllipse(b, r);
                    using (Pen p = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawEllipse(p, r);
                }
                else
                {
                    // rainbow "custom color" button
                    using (LinearGradientBrush lb = new LinearGradientBrush(r, Color.Red, Color.Blue, 45f))
                    {
                        ColorBlend cb = new ColorBlend();
                        cb.Colors = new[] { Color.FromArgb(239, 68, 68), Color.FromArgb(250, 204, 21), Color.FromArgb(34, 197, 94), Color.FromArgb(14, 165, 233), Color.FromArgb(168, 85, 247) };
                        cb.Positions = new[] { 0f, 0.25f, 0.5f, 0.75f, 1f };
                        lb.InterpolationColors = cb;
                        g.FillEllipse(lb, r);
                    }
                    if (!matched)
                    {
                        using (Pen p = new Pen(Theme.Ink, Theme.S(2))) g.DrawEllipse(p, Rectangle.Inflate(r, Theme.S(3), Theme.S(3)));
                        Rectangle inner = Rectangle.Inflate(r, -Theme.S(6), -Theme.S(6));
                        using (SolidBrush b = new SolidBrush(Selected)) g.FillEllipse(b, inner);
                    }
                    else Theme.Glyph(g, Ico.Add, 8f, Color.White, r);
                }
                if (i == hoverIndex) using (Pen p = new Pen(Color.FromArgb(90, Theme.Ink), 1.5f)) g.DrawEllipse(p, Rectangle.Inflate(r, Theme.S(2), Theme.S(2)));
            }
        }
    }

    /// Multi-select chips (Bold, Italic, Outline…).
    public class ChipToggles : UiControl
    {
        public Func<string>[] Labels;
        public string[] Glyphs;
        public bool[] On;
        int hoverIndex = -1;
        public event EventHandler Changed;
        public ChipToggles(Func<string>[] labels, string[] glyphs) { Labels = labels; Glyphs = glyphs; On = new bool[labels.Length]; Cursor = Cursors.Hand; }

        // glyph-only chips (B, I) are narrower than worded ones
        float Weight(int i) { return Glyphs != null && Glyphs[i] != null && Labels[i]().Length == 0 ? 0.62f : 1f; }
        Rectangle Cell(int i)
        {
            int gap = Theme.S(6), n = Labels.Length;
            float total = 0; for (int k = 0; k < n; k++) total += Weight(k);
            float unit = (Width - gap * (n - 1)) / total, x = 0;
            for (int k = 0; k < i; k++) x += Weight(k) * unit + gap;
            return Theme.Mirror(new Rectangle((int)x, 0, (int)(Weight(i) * unit), Height), Width);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; for (int i = 0; i < Labels.Length; i++) if (Cell(i).Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            for (int i = 0; i < Labels.Length; i++)
                if (Cell(i).Contains(e.Location)) { On[i] = !On[i]; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); break; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            for (int i = 0; i < Labels.Length; i++)
            {
                Rectangle c = Cell(i);
                RectangleF r = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1);
                Color fg;
                if (On[i]) { Theme.FillRound(g, r, Theme.S(8), Theme.AccentSoft); Theme.StrokeRound(g, r, Theme.S(8), Theme.Mix(Theme.AccentSoft, Theme.AccentColor, 0.5f)); fg = Theme.Dark ? Theme.Ink : Theme.Mix(Theme.AccentColor, Color.Black, 0.25f); }
                else
                {
                    bool h = i == hoverIndex;
                    Theme.FillRound(g, r, Theme.S(8), h ? Theme.Hover : Theme.ChipTop);
                    Theme.StrokeRound(g, r, Theme.S(8), Theme.ChipBorder);
                    fg = Theme.Ink;
                }
                string label = Labels[i]();
                if (Glyphs != null && Glyphs[i] != null && label.Length == 0) Theme.Glyph(g, Glyphs[i], 10f, fg, c);
                else Theme.Text(g, label, Theme.F(8.75f, FontStyle.Bold), fg, c, Align.Center);
            }
        }
    }

    /// A button that opens a themed, scrollable, searchable list (fonts are previewed in their own typeface).
    public class DropDown : UiControl
    {
        public List<string> Items = new List<string>();
        public List<string> Favorites;               // shown first, under their own heading
        public List<string> Extra;                   // another group shown first (e.g. Arabic fonts)
        public Func<string> ExtraHeader;
        public Func<string> ActionLabel;             // optional first row that runs an action (e.g. download fonts)
        public Action Action;
        public string Selected;
        public bool PreviewFonts;
        public event EventHandler SelectedChanged;
        public event Action<string> Previewing;       // hovered item while the list is open; null = preview over
        PopupList open; DateTime closedAt;
        public DropDown() { Cursor = Cursors.Hand; }
        /// Chooses an item as if it was picked from the list.
        public void Pick(string v) { Selected = v; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (open != null || (DateTime.Now - closedAt).TotalMilliseconds < 250) return;   // a click on the button while open just closes it
            open = new PopupList(Items, Favorites, Selected, PreviewFonts);
            open.Extra = Extra; open.ExtraHeader = ExtraHeader != null ? ExtraHeader() : "";
            open.ActionText = ActionLabel != null ? ActionLabel() : null;
            open.ActionClicked += () => { if (Action != null) Action(); };
            open.Rebuild();
            bool previewed = false;
            open.Hovered += v => { if (Previewing != null) { previewed = true; Previewing(v); } };
            open.Picked += v => { if (previewed && Previewing != null) { previewed = false; Previewing(null); } Selected = v; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); };
            open.FormClosed += (s, e2) => { open = null; closedAt = DateTime.Now; if (previewed && Previewing != null) Previewing(null); Invalidate(); };
            open.ShowBelow(this);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, r, Theme.S(9), Theme.Mix(Theme.Field, Theme.Hover, hot));
            Theme.StrokeRound(g, r, Theme.S(9), open != null ? Theme.InkSoft : Theme.Mix(Theme.FieldBorder, Theme.InkFaint, hot * 0.6f), open != null ? 1.5f : 1f);
            Font f = Theme.F(10f);
            if (PreviewFonts && Selected != null) f = PopupList.FontFor(Selected, 10.5f) ?? f;
            Theme.Text(g, Selected ?? "", f, Enabled ? Theme.Ink : Theme.InkFaint, Theme.Mirror(new Rectangle(Theme.S(12), 0, Width - Theme.S(44), Height), Width), Align.Start, true, false, PreviewFonts);
            Theme.Glyph(g, open != null ? "\uE70E" : "\uE70D", 8f, Theme.InkSoft, Theme.Mirror(new Rectangle(Width - Theme.S(32), 0, Theme.S(24), Height), Width));
        }
    }

    /// Floating list used by DropDown: search box, smooth scrolling, keyboard navigation.
    public class PopupList : Form
    {
        public event Action<string> Picked;
        public event Action ActionClicked;
        public event Action<string> Hovered;
        public List<string> Extra; public string ExtraHeader = ""; public string ActionText;
        readonly List<string> all, favorites;
        readonly bool fonts;
        readonly TextField search;
        readonly PopupRows rows;
        static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public static Font FontFor(string name, float size)
        {
            string key = name + "|" + size + "@" + Theme.Scale;
            Font f;
            if (fontCache.TryGetValue(key, out f)) return f;
            FontFamily pf = FontPack.Find(name);
            if (pf != null)
            {
                FontStyle st = pf.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
                try { f = new Font(pf, Theme.Px(size), st, GraphicsUnit.Pixel); } catch { f = null; }
                fontCache[key] = f;
                return f;
            }
            try { f = new Font(name, Theme.Px(size), FontStyle.Regular, GraphicsUnit.Pixel); if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) { f.Dispose(); f = null; } } catch { f = null; }
            if (fontCache.Count > 600) fontCache.Clear();
            fontCache[key] = f;
            return f;
        }

        public PopupList(List<string> items, List<string> favs, string selected, bool fonts)
        {
            all = items; favorites = favs ?? new List<string>(); this.fonts = fonts;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Card;
            Font = Theme.F(10f);
            search = new TextField(() => L.T(fonts ? "find_font" : "search"), Ico.Search);
            search.Surface = Surface.Card;
            search.Box.TextChanged += (s, e) => Rebuild();
            search.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down) { rows.Move(1); e.Handled = true; }
                else if (e.KeyCode == Keys.Up) { rows.Move(-1); e.Handled = true; }
                else if (e.KeyCode == Keys.PageDown) { rows.Move(8); e.Handled = true; }
                else if (e.KeyCode == Keys.PageUp) { rows.Move(-8); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; rows.PickCurrent(); }
            };
            rows = new PopupRows(fonts);
            rows.Selected = selected;
            rows.Hovered += v => { if (Hovered != null) Hovered(v); };
            rows.Picked += v =>
            {
                if (v == "\u0001action") { if (ActionClicked != null) ActionClicked(); Close(); return; }
                if (Picked != null) Picked(v); Close();
            };
            Controls.Add(search); Controls.Add(rows);
            Deactivate += (s, e) => Close();
            Rebuild();
            rows.ScrollToSelected();
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // drop shadow
        }

        public void Rebuild()
        {
            string q = search.Box.Text.Trim();
            List<PopupRows.Row> list = new List<PopupRows.Row>();
            if (q.Length == 0 && !string.IsNullOrEmpty(ActionText)) list.Add(new PopupRows.Row { Text = "\u0001action", Label = ActionText });
            if (Extra != null && Extra.Count > 0)
            {
                List<string> hits = Extra.FindAll(f => q.Length == 0 || f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hits.Count > 0)
                {
                    list.Add(new PopupRows.Row { Text = ExtraHeader, Header = true });
                    foreach (string f in hits) list.Add(new PopupRows.Row { Text = f, Sample = "خط عربي" });
                }
            }
            if (q.Length == 0 && favorites.Count > 0 && all.Count > favorites.Count)
            {
                list.Add(new PopupRows.Row { Text = L.T("fonts_popular"), Header = true });
                foreach (string f in favorites) list.Add(new PopupRows.Row { Text = f });
                list.Add(new PopupRows.Row { Text = L.T("fonts_all", all.Count), Header = true });
            }
            foreach (string f in all)
                if (q.Length == 0 || f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) list.Add(new PopupRows.Row { Text = f });
            rows.SetRows(list);
        }

        public void ShowBelow(Control anchor)
        {
            Rectangle a = anchor.RectangleToScreen(anchor.ClientRectangle);
            Rectangle wa = Screen.FromControl(anchor).WorkingArea;
            int w = Math.Max(a.Width, Theme.S(fonts ? 320 : 220));
            int h = Math.Min(Theme.S(440), Theme.S(58) + Math.Max(3, rows.Count) * rows.RowPx + Theme.S(8));
            int x = Theme.RTL ? a.Right - w : a.X;
            int y = a.Bottom + Theme.S(4);
            if (y + h > wa.Bottom) y = Math.Max(wa.Top, a.Top - h - Theme.S(4));
            x = Math.Max(wa.Left, Math.Min(wa.Right - w, x));
            Bounds = new Rectangle(x, y, w, h);
            int pad = Theme.S(8);
            search.SetBounds(pad, pad, w - pad * 2, Theme.S(38));
            rows.SetBounds(Theme.S(4), pad + Theme.S(44), w - Theme.S(8), h - pad - Theme.S(44) - Theme.S(4));
            Show(anchor.FindForm());
            search.Restyle();
            search.Box.Focus();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(16);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) { int r = Theme.S(16); try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { } }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Card);
            Theme.Smooth(e.Graphics);
            using (System.Drawing.Drawing2D.GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(8)))
            using (Pen pen = new Pen(Theme.CardBorder)) e.Graphics.DrawPath(pen, p);
        }
    }

    public class PopupRows : ScrollList
    {
        public class Row { public string Text, Label, Sample; public bool Header; }
        List<Row> rows = new List<Row>();
        public string Selected;
        int cursor = -1;
        readonly bool fonts;
        public event Action<string> Picked;
        public event Action<string> Hovered;          // the row under the mouse (or keyboard cursor)
        void Hover() { if (Hovered != null && cursor >= 0 && cursor < rows.Count && !rows[cursor].Header && rows[cursor].Label == null) Hovered(rows[cursor].Text); }
        public PopupRows(bool fonts) { this.fonts = fonts; RowHeight = fonts ? 40 : 34; Surface = Surface.Card; Cursor = Cursors.Hand; }
        public int RowPx { get { return RH; } }
        public override int Count { get { return rows.Count; } }
        public void SetRows(List<Row> r)
        {
            rows = r;
            cursor = -1;
            for (int i = 0; i < rows.Count; i++) if (!rows[i].Header) { cursor = i; break; }
            for (int i = 0; i < rows.Count; i++) if (!rows[i].Header && rows[i].Text == Selected) { cursor = i; break; }
            ScrollTo(0);
            Invalidate();
        }
        public void ScrollToSelected() { if (cursor >= 0) EnsureVisible(cursor); }
        public void Move(int d)
        {
            if (rows.Count == 0) return;
            int i = cursor;
            int step = Math.Sign(d), n = Math.Abs(d);
            while (n > 0)
            {
                int j = i + step;
                while (j >= 0 && j < rows.Count && rows[j].Header) j += step;
                if (j < 0 || j >= rows.Count) break;
                i = j; n--;
            }
            cursor = i; EnsureVisible(cursor); Invalidate(); Hover();
        }
        public void PickCurrent() { if (cursor >= 0 && cursor < rows.Count && !rows[cursor].Header && Picked != null) Picked(rows[cursor].Text); }
        protected override void RowClick(int index, Point local, Rectangle row)
        {
            if (rows[index].Header) return;
            if (Picked != null) Picked(rows[index].Text);
        }
        protected override void RowHover(int index, Point local, Rectangle row) { if (!rows[index].Header && cursor != index) { cursor = index; Invalidate(); Hover(); } }
        protected override void PaintEmpty(Graphics g)
        {
            Theme.Text(g, L.T("no_matches"), Theme.F(9.5f), Theme.InkFaint, new Rectangle(0, Theme.S(10), Width, Theme.S(30)), Align.Center);
        }
        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            Row row = rows[i];
            Theme.Smooth(g);
            if (row.Header)
            {
                Theme.Text(g, row.Text.ToUpperInvariant(), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint,
                    Theme.Mirror(new Rectangle(r.X + Theme.S(12), r.Y + Theme.S(10), r.Width - Theme.S(24), r.Height - Theme.S(10)), Width), Align.Start, true, false);
                return;
            }
            Rectangle inner = new Rectangle(r.X + Theme.S(4), r.Y + Theme.S(2), r.Width - Theme.S(8), r.Height - Theme.S(4));
            bool sel = row.Text == Selected;
            if (i == cursor) Theme.FillRound(g, inner, Theme.S(8), Theme.Hover);
            if (sel) Theme.Glyph(g, Ico.Check, 8f, Theme.Ink, Theme.Mirror(new Rectangle(inner.Right - Theme.S(30), inner.Y, Theme.S(24), inner.Height), Width));
            if (row.Label != null)
            {
                // action row (e.g. "Get free Arabic fonts")
                Theme.FillRound(g, inner, Theme.S(8), Theme.Selected);
                Theme.Glyph(g, Ico.Download, 10f, Theme.Ink, Theme.Mirror(new Rectangle(inner.X + Theme.S(8), inner.Y, Theme.S(24), inner.Height), Width));
                Rectangle ar = Theme.Mirror(new Rectangle(inner.X + Theme.S(38), inner.Y, inner.Width - Theme.S(46), inner.Height), Width);
                Theme.Text(g, row.Label, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, ar, Align.Start, true, false);
                return;
            }
            Font f = fonts ? (PopupList.FontFor(row.Text, 12f) ?? Theme.F(10.5f)) : Theme.F(10f, sel ? FontStyle.Bold : FontStyle.Regular);
            Rectangle tr = Theme.Mirror(new Rectangle(inner.X + Theme.S(10), inner.Y, inner.Width - Theme.S(46), inner.Height), Width);
            Theme.Text(g, row.Text, f, Theme.Ink, tr, Align.Start, true, false, fonts);
            if (row.Sample != null && fonts)
            {
                Rectangle sr = Theme.Mirror(new Rectangle(inner.Right - Theme.S(120), inner.Y, Theme.S(86), inner.Height), Width);
                TextRenderer.DrawText(g, row.Sample, f, sr, Theme.InkSoft, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.RightToLeft | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }
    }

    /// Menu colors that follow the light/dark theme.
    public class ThemedRenderer : ToolStripProfessionalRenderer
    {
        public ThemedRenderer() : base(new ThemedColors()) { RoundedEdges = true; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = Theme.Ink; base.OnRenderItemText(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle r = new Rectangle(e.ImageRectangle.X, e.Item.ContentRectangle.Y, e.ImageRectangle.Width, e.Item.ContentRectangle.Height);
            Theme.Glyph(e.Graphics, Ico.Check, 8f, Theme.Ink, r);
        }
    }

    public class ThemedColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.CardBorder; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color CheckBackground { get { return Theme.Card; } }
        public override Color CheckSelectedBackground { get { return Theme.Hover; } }
        public override Color CheckPressedBackground { get { return Theme.Pressed; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
    }
}
