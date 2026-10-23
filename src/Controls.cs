using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// Base for all custom-painted controls: double buffered, themed surface, hover animation.
    public class UiControl : Control, IAnimated
    {
        public Surface Surface = Surface.Card;
        protected float hot;
        protected bool hovering, pressing;

        public UiControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, true);
            TabStop = false;   // only things you can press or change take part in Tab (they switch it on)
        }

        bool wanted = true;
        /// Whether the page shows this control. (Visible also reads false while the window is hidden — in the
        /// tray — or the page is hidden, so layouts measure with this instead.)
        public bool Wanted { get { return wanted; } }
        protected override void SetVisibleCore(bool value) { wanted = value; base.SetVisibleCore(value); }

        // ---- keyboard focus: a clear ring when you move around with Tab (not when you click)
        protected bool KeyFocus { get { return Focused && ShowFocusCues; } }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected void PaintFocus(Graphics g, RectangleF r, float radius)
        {
            if (!KeyFocus) return;
            RectangleF f = RectangleF.Inflate(r, -1, -1);
            Theme.StrokeRound(g, f, radius, Theme.AccentColor, Math.Max(2f, Theme.S(2)));
        }

        // ---- what screen readers (Narrator, NVDA) say about it
        /// Its name (e.g. a button's words).
        protected virtual string AccName { get { return AccessibleName ?? ""; } }
        protected virtual AccessibleRole AccRole { get { return AccessibleRole.Client; } }
        protected virtual AccessibleStates AccState { get { return AccessibleStates.None; } }
        protected virtual string AccValue { get { return null; } }
        /// What "do it" does (press, switch...), or null.
        protected virtual void AccDo() { }
        protected virtual string AccAction { get { return null; } }
        protected override AccessibleObject CreateAccessibilityInstance() { return new UiAccessible(this); }

        public class UiAccessible : ControlAccessibleObject
        {
            readonly UiControl c;
            public UiAccessible(UiControl owner) : base(owner) { c = owner; }
            public override string Name { get { string n = c.AccName; return string.IsNullOrEmpty(n) ? base.Name : n; } set { } }
            public override AccessibleRole Role { get { return c.AccRole; } }
            public override AccessibleStates State { get { return base.State | c.AccState | (c.Enabled ? AccessibleStates.None : AccessibleStates.Unavailable); } }
            public override string Value { get { return c.AccValue ?? base.Value; } set { } }
            public override string DefaultAction { get { return c.AccAction ?? base.DefaultAction; } }
            public override void DoDefaultAction() { if (c.AccAction != null && c.Enabled) c.AccDo(); else base.DoDefaultAction(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Animator.Start(this); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressing = false; Animator.Start(this); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressing = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressing = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        public virtual bool Step()
        {
            float target = hovering && Enabled ? 1f : 0f;
            hot += (target - hot) * Animator.Rate(0.3f);
            if (Math.Abs(target - hot) < 0.03f) hot = target;
            Invalidate();
            return hot != target;
        }

        protected void PaintBg(Graphics g) { Theme.PaintSurface(g, this, Surface); }
    }

    public enum BtnKind { Primary, Secondary, Ghost, Danger }

    public class Btn : UiControl
    {
        public Func<string> Label;
        public string Glyph;
        public BtnKind Kind;
        public float Radius = 8;
        public float FontSize = 9.75f;
        public bool Bold;
        public bool Active;          // for toggle-like ghost buttons
        public bool AlignStart;      // icon + text at the start (menu-style rows)
        public Color InkColor = Color.Empty;   // optional text/icon color (e.g. red for "Clear list")
        public float GlyphSize = 11f;
        readonly Ripple ripple = new Ripple();

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Enabled) { ripple.Start(e.Location, ClientRectangle); Animator.Start(this); }
            base.OnMouseDown(e);
        }
        public override bool Step()
        {
            bool r = ripple.Step();
            bool b = base.Step();
            return r || b;
        }

        protected override string AccName { get { string t = Caption; return t.Length > 0 ? t : AccessibleName ?? ""; } }
        protected override AccessibleRole AccRole { get { return AccessibleRole.PushButton; } }
        protected override string AccAction { get { return L.T("acc_press"); } }
        protected override void AccDo() { OnClick(EventArgs.Empty); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && e.Modifiers == Keys.None)
            {
                e.Handled = true;
                ripple.Start(new Point(Width / 2, Height / 2), ClientRectangle); Animator.Start(this);
                OnClick(EventArgs.Empty);
            }
        }

        public Btn(Func<string> label, string glyph, BtnKind kind)
        {
            TabStop = true;
            Label = label; Glyph = glyph; Kind = kind;
            Cursor = Cursors.Hand;
            Bold = kind == BtnKind.Primary;
            if (kind == BtnKind.Primary) FontSize = 10f;
        }

        public string Caption { get { return Label == null ? "" : Label(); } }

        public int PreferredWidth
        {
            get
            {
                string t = Caption;
                int w = Theme.S(18) * 2;
                if (!string.IsNullOrEmpty(Glyph)) w += Theme.S(18) + (t.Length > 0 ? Theme.S(8) : 0);
                w += Theme.Measure(t, Theme.F(FontSize, Bold ? FontStyle.Bold : FontStyle.Regular));
                if (t.Length == 0) w = Height;
                return w;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.S(Radius);
            Color fg;
            bool en = Enabled;
            switch (Kind)
            {
                case BtnKind.Primary:
                    {
                        Color bg = Theme.PrimaryTop;
                        if (!en) bg = Theme.Track;
                        else
                        {
                            bg = Theme.Mix(bg, Theme.Dark ? Color.White : Color.Black, hot * 0.05f);
                            if (pressing) bg = Theme.Mix(bg, Color.Black, 0.12f);
                        }
                        Theme.FillRound(g, r, rad, bg);
                        fg = en ? Theme.PrimaryInk : Theme.InkFaint;
                        break;
                    }
                case BtnKind.Secondary:
                    {
                        Color bg = Theme.Mix(Theme.ChipTop, Theme.Hover, hot);
                        if (pressing) bg = Theme.Pressed;
                        Theme.FillRound(g, r, rad, bg);
                        Theme.StrokeRound(g, r, rad, Theme.Mix(Theme.ChipBorder, Theme.FieldBorder, hot));
                        fg = en ? Theme.Ink : Theme.InkFaint;
                        break;
                    }
                case BtnKind.Danger:
                    {
                        Color bg = Theme.Mix(Theme.ErrorBg, Theme.Error, hot * 0.12f);
                        Theme.FillRound(g, r, rad, bg);
                        fg = Theme.Error;
                        break;
                    }
                default:
                    {
                        if (Active) Theme.FillRound(g, r, rad, Theme.Selected);
                        if (hot > 0 && en) Theme.FillRound(g, r, rad, Color.FromArgb((int)(255 * hot), pressing ? Theme.Pressed : Theme.Hover));
                        fg = en ? (Active ? Theme.Ink : Theme.Mix(Theme.InkSoft, Theme.Ink, hot)) : Theme.InkFaint;
                        break;
                    }
            }

            if (ripple.Running)
                ripple.Paint(g, rad, Kind == BtnKind.Primary ? Color.FromArgb(90, 255, 255, 255) : Theme.Dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0));
            PaintFocus(g, r, rad);

            string text = Caption;
            Font f = Theme.F(FontSize, Bold ? FontStyle.Bold : FontStyle.Regular);
            int tw = Theme.Measure(text, f);
            bool hasG = !string.IsNullOrEmpty(Glyph);
            int gw = hasG ? Theme.S(18) : 0;
            int gap = hasG && tw > 0 ? Theme.S(8) : 0;
            int total = gw + gap + tw;
            int x0 = AlignStart ? Theme.S(12) : (Width - total) / 2;
            if (x0 < Theme.S(8)) x0 = Theme.S(8);
            if (!InkColor.IsEmpty && en) fg = InkColor;
            bool rtl = Theme.RTL;
            Rectangle gr, tr;
            if (!rtl) { gr = new Rectangle(x0, 0, gw, Height); tr = new Rectangle(x0 + gw + gap, 0, Width - (x0 + gw + gap) - Theme.S(6), Height); }
            else { tr = new Rectangle(Theme.S(6), 0, Width - x0 - gw - gap - Theme.S(6), Height); gr = new Rectangle(Width - x0 - gw, 0, gw, Height); tr.X = Width - x0 - gw - gap - tw; tr.Width = tw + 2; if (tr.X < Theme.S(6)) { tr.Width -= Theme.S(6) - tr.X; tr.X = Theme.S(6); } }
            if (hasG) Theme.Glyph(g, Glyph, GlyphSize, fg, gr);
            if (tw > 0)
            {
                TextFormatFlags fl = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left;
                if (rtl) fl |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(g, text, f, tr, fg, fl);
            }
        }
    }

    /// Segmented control (e.g. Image | Audio | Video).
    public class Segments : UiControl
    {
        public Func<string>[] Items;
        public string[] Glyphs;
        int selected, hoverIndex = -1;
        public event EventHandler SelectedChanged;
        public float FontSize = 9.25f;

        float pos = -1;   // where the selection pill is now: it slides to the chosen item
        /// Optional: the part of the page this switch changes — it cross-fades to the new content.
        public Control FadeTarget;
        /// Which part of FadeTarget (null = all of it).
        public Func<Rectangle> FadeArea;

        public Segments(params Func<string>[] items) { Items = items; Cursor = Cursors.Hand; TabStop = true; }

        // keyboard: ← → pick the one beside it
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled || Items.Length == 0) return;
            int d = e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Left ? -1 : 0;
            if (Theme.RTL) d = -d;
            if (d != 0) { e.Handled = true; SelectedIndex = Math.Max(0, Math.Min(Items.Length - 1, selected + d)); Invalidate(); }
        }
        protected override string AccName { get { return AccessibleName ?? ""; } }
        protected override AccessibleRole AccRole { get { return AccessibleRole.PageTabList; } }
        protected override string AccValue { get { return selected >= 0 && selected < Items.Length ? Items[selected]() : ""; } }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value != selected && value >= 0 && value < Items.Length)
                {
                    if (FadeTarget != null && Visible) Fx.Crossfade(FadeTarget, FadeArea, 170);
                    selected = value; Slide();
                    if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
                }
            }
        }
        public void SetSilently(int i) { selected = i; pos = i; Invalidate(); }
        /// Moves the selection (sliding) without raising SelectedChanged.
        public void SetQuietly(int i) { if (i < 0 || i >= Items.Length || i == selected) return; selected = i; Slide(); }

        void Slide()
        {
            if (pos < 0 || !Visible || !Fx.Motion) pos = selected;
            Animator.Start(this);
            Invalidate();
        }

        public override bool Step()
        {
            bool b = base.Step();
            if (pos < 0) pos = selected;
            bool m = Fx.Ease(ref pos, selected, 0.24f);
            if (m) Invalidate();
            return b || m;
        }
        protected override void OnVisibleChanged(EventArgs e) { pos = selected; base.OnVisibleChanged(e); }

        RectangleF Seg(int i)
        {
            float pad = Theme.S(3);
            float w = (Width - pad * 2) / Items.Length;
            int vis = Theme.RTL ? Items.Length - 1 - i : i;
            return new RectangleF(pad + vis * w, pad, w, Height - pad * 2);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < Items.Length; i++) if (Seg(i).Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (!Enabled) return;
            int hit = -1;
            for (int i = 0; i < Items.Length; i++) if (Seg(i).Contains(e.Location)) { hit = i; break; }
            base.OnMouseClick(e);
            if (hit >= 0) SelectedIndex = hit;   // after the hit test: changing language can flip the layout
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Color track = Theme.Dark ? Theme.Mix(Theme.Card, Color.Black, 0.28f) : Theme.Mix(Theme.Bg, Theme.Selected, 0.75f);
            Theme.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(9), track);
            if (selected >= 0 && selected < Items.Length)
            {
                // the white pill, on its way to (or at) the chosen item
                float p = pos < 0 ? selected : Math.Max(0, Math.Min(Items.Length - 1, pos));
                int a = (int)Math.Floor(p), b = Math.Min(Items.Length - 1, a + 1);
                float f0 = p - a;
                RectangleF ra = Seg(a), rb = Seg(b);
                RectangleF pr = new RectangleF(ra.X + (rb.X - ra.X) * f0, ra.Y, ra.Width + (rb.Width - ra.Width) * f0, ra.Height);
                pr.Inflate(-Theme.S(1.5f), 0);   // the same small gap around every pill, so the tabs look evenly spaced
                Theme.FillRound(g, pr, Theme.S(7), Theme.Dark ? Theme.Mix(Theme.Card, Color.White, 0.06f) : Color.White);
                Theme.StrokeRound(g, pr, Theme.S(7), Theme.Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(26, 60, 50, 30));
            }
            for (int i = 0; i < Items.Length; i++)
            {
                RectangleF r = Seg(i);
                Color fg = Theme.InkSoft;
                if (i == selected) fg = Theme.Ink;
                else if (i == hoverIndex && Enabled && Math.Abs(pos - i) > 0.5f)
                {
                    // hover: a softer pill of exactly the same size and spacing as the chosen one
                    RectangleF hr = r; hr.Inflate(-Theme.S(1.5f), 0);
                    Theme.FillRound(g, hr, Theme.S(7), Theme.Dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(140, 255, 255, 255));
                    fg = Theme.Ink;
                }
                else if (i == hoverIndex && Enabled) fg = Theme.Ink;
                if (!Enabled) fg = Theme.InkFaint;
                Font f = Theme.F(FontSize, i == selected ? FontStyle.Bold : FontStyle.Regular);
                Rectangle rr = Rectangle.Round(r);
                // long words in a narrow tab: a slightly smaller font before cutting the word off
                if (Glyphs == null || i >= Glyphs.Length || Glyphs[i] == null)
                    for (float fs = FontSize - 0.5f; fs >= 7.5f && Theme.Measure(Items[i](), f) > rr.Width - Theme.S(8); fs -= 0.5f)
                        f = Theme.F(fs, i == selected ? FontStyle.Bold : FontStyle.Regular);
                if (Glyphs != null && i < Glyphs.Length && Glyphs[i] != null)
                {
                    string t = Items[i]();
                    int tw = Theme.Measure(t, f), gw = Theme.S(16), gap = Theme.S(6);
                    int x0 = rr.X + (rr.Width - tw - gw - gap) / 2;
                    if (!Theme.RTL)
                    {
                        Theme.Glyph(g, Glyphs[i], 10f, fg, new Rectangle(x0, rr.Y, gw, rr.Height));
                        TextRenderer.DrawText(g, t, f, new Rectangle(x0 + gw + gap, rr.Y, tw + 4, rr.Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    }
                    else
                    {
                        Theme.Glyph(g, Glyphs[i], 10f, fg, new Rectangle(x0 + tw + gap, rr.Y, gw, rr.Height));
                        TextRenderer.DrawText(g, t, f, new Rectangle(x0, rr.Y, tw + 4, rr.Height), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.RightToLeft);
                    }
                }
                else Theme.Text(g, Items[i](), f, fg, rr, Align.Center);
            }
            PaintFocus(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(9));
        }
    }

    /// Grid of format chips (JPG, PNG, ...).
    public class ChipGrid : UiControl
    {
        List<string> items = new List<string>();
        string selected;
        int hoverIndex = -1;
        public int Columns = 4;
        public int RowH = 36;
        public event EventHandler SelectedChanged;
        /// What a chip says (default: the item in capitals, like "PNG").
        public Func<string, string> LabelOf;

        public ChipGrid() { Cursor = Cursors.Hand; TabStop = true; }

        // keyboard: arrows move to the chip beside / above / below
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int i = items.IndexOf(selected);
            if (!Enabled || items.Count == 0 || i < 0) return;
            int d = e.KeyCode == Keys.Right ? 1 : e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Down ? Columns : e.KeyCode == Keys.Up ? -Columns : 0;
            if (Theme.RTL && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)) d = -d;
            if (d == 0) return;
            e.Handled = true;
            int j = Math.Max(0, Math.Min(items.Count - 1, i + d));
            if (j != i && Fx.Motion) { morphFrom = morph < 1 ? PillAt(morph) : CellF(i); morph = 0; Animator.Start(this); }
            Selected = items[j];
        }
        protected override AccessibleRole AccRole { get { return AccessibleRole.List; } }
        protected override string AccValue { get { return selected == null ? "" : LabelOf != null ? LabelOf(selected) : selected.ToUpperInvariant(); } }

        public void SetItems(IEnumerable<string> list, string select)
        {
            items = new List<string>(list);
            morph = 1;
            selected = items.Contains(select) ? select : (items.Count > 0 ? items[0] : null);
            Invalidate();
            if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
        }
        public string Selected
        {
            get { return selected; }
            set { if (items.Contains(value) && value != selected) { selected = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); } }
        }
        public int RowHeight { get { return Theme.S(RowH); } }
        public int Gap { get { return Theme.S(8); } }
        public int NeededHeight(int count) { int rows = (count + Columns - 1) / Columns; return rows * RowHeight + Math.Max(0, rows - 1) * Gap; }

        Rectangle Cell(int i)
        {
            int w = (Width - Gap * (Columns - 1)) / Columns;
            int row = i / Columns, col = i % Columns;
            if (Theme.RTL) col = Columns - 1 - col;
            return new Rectangle(col * (w + Gap), row * (RowHeight + Gap), w, RowHeight);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < items.Count; i++) if (Cell(i).Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (!Enabled) return;
            string hit = null;
            for (int i = 0; i < items.Count; i++) if (Cell(i).Contains(e.Location)) { hit = items[i]; break; }
            base.OnMouseClick(e);
            if (hit != null && hit != selected)
            {
                // the selection flows over to the one clicked (its front edge leads, its back edge catches up)
                int was = items.IndexOf(selected);
                if (was >= 0 && Fx.Motion) { morphFrom = morph < 1 ? PillAt(morph) : CellF(was); morph = 0; Animator.Start(this); }
                Selected = hit;
            }
        }

        // ---- the moving selection
        float morph = 1;
        RectangleF morphFrom;
        RectangleF CellF(int i) { Rectangle c = Cell(i); return new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1); }
        static float Out3(float t) { t = Math.Max(0, Math.Min(1, t)); return 1 - (float)Math.Pow(1 - t, 3); }
        static float OutBack(float t) { t = Math.Max(0, Math.Min(1, t)); const float k = 1.4f; float u = t - 1; return 1 + (k + 1) * u * u * u + k * u * u; }

        RectangleF PillAt(float t)
        {
            RectangleF a = morphFrom, b = CellF(Math.Max(0, items.IndexOf(selected)));
            float lead = Out3(t / 0.62f), tail = OutBack((t - 0.16f) / 0.84f);
            Func<float, float, bool, float> mix = (x, y, front) => x + (y - x) * (front ? lead : tail);
            bool right = b.X > a.X, down = b.Y > a.Y;
            float l = mix(a.Left, b.Left, !right), r = mix(a.Right, b.Right, right);
            float tp = mix(a.Top, b.Top, !down), bt = mix(a.Bottom, b.Bottom, down);
            if (r - l < Theme.S(10)) { float m = (l + r) / 2; l = m - Theme.S(5); r = m + Theme.S(5); }
            return RectangleF.FromLTRB(l, tp, r, Math.Max(tp + Theme.S(10), bt));
        }

        public override bool Step()
        {
            bool more = base.Step();
            if (morph < 1) { morph = Math.Min(1, morph + 0.034f * Animator.K); Invalidate(); return true; }
            return more;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Font f = Theme.F(9.25f, FontStyle.Bold);
            int si = items.IndexOf(selected);
            bool moving = morph < 1 && si >= 0;
            RectangleF pill = moving ? PillAt(morph) : RectangleF.Empty;
            if (!moving && si >= 0) PaintFocus(g, RectangleF.Inflate(CellF(si), Theme.S(3), Theme.S(3)), Theme.S(10));
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle c = Cell(i);
                RectangleF r = CellF(i);
                bool sel = items[i] == selected && !moving;
                Color fg;
                if (sel)
                {
                    // chosen: a soft orange tint with an orange edge (solid orange is kept for the Convert button)
                    Theme.FillRound(g, r, Theme.S(8), Enabled ? Theme.AccentSoft : Theme.Selected);
                    Theme.StrokeRound(g, r, Theme.S(8), Enabled ? Theme.AccentColor : Theme.InkFaint, Theme.S(1.6f));
                    fg = Enabled ? Theme.Ink : Theme.InkFaint;
                }
                else
                {
                    bool h = i == hoverIndex && Enabled;
                    Theme.FillRound(g, r, Theme.S(8), h ? Theme.Hover : Theme.ChipTop);
                    Theme.StrokeRound(g, r, Theme.S(8), h ? Theme.FieldBorder : Theme.ChipBorder);
                    fg = Enabled ? Theme.Ink : Theme.InkFaint;
                }
                Theme.Text(g, ChipLabel(i), f, fg, c, Align.Center, true, false, LabelOf == null);
            }
            if (!moving) return;
            // a soft ring spreads from the chip as the selection lands on it
            if (morph > 0.42f)
            {
                float p = (morph - 0.42f) / 0.58f;
                RectangleF ring = CellF(si);
                float grow = Theme.S(1) + p * Theme.S(6);
                ring.Inflate(grow, grow);
                Theme.StrokeRound(g, ring, Theme.S(8) + grow, Color.FromArgb((int)(150 * (1 - p) * (1 - p)), Theme.AccentColor), Theme.S(1.6f));
            }
            Theme.FillRound(g, pill, Theme.S(8), Enabled ? Theme.AccentSoft : Theme.Selected);
            Theme.StrokeRound(g, pill, Theme.S(8), Enabled ? Theme.AccentColor : Theme.InkFaint, Theme.S(1.6f));
            PaintFocus(g, RectangleF.Inflate(pill, Theme.S(3), Theme.S(3)), Theme.S(10));
            // the labels under the moving chip are redrawn on top of it
            GraphicsState st = g.Save();
            using (System.Drawing.Drawing2D.GraphicsPath clip = Theme.Round(pill, Theme.S(8)))
            {
                g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                for (int i = 0; i < items.Count; i++)
                    if (CellF(i).IntersectsWith(pill)) Theme.Text(g, ChipLabel(i), f, Theme.Ink, Cell(i), Align.Center, true, false, LabelOf == null);
            }
            g.Restore(st);
        }

        string ChipLabel(int i) { return LabelOf != null ? LabelOf(items[i]) : items[i].ToUpperInvariant(); }
    }

    /// On/off switch.
    public class Toggle : UiControl
    {
        bool on;
        float pos;
        public event EventHandler CheckedChanged;
        public Toggle() { Cursor = Cursors.Hand; TabStop = true; }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || k == Keys.Enter || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { e.Handled = true; Checked = !Checked; }
        }
        protected override AccessibleRole AccRole { get { return AccessibleRole.CheckButton; } }
        protected override AccessibleStates AccState { get { return on ? AccessibleStates.Checked : AccessibleStates.None; } }
        protected override string AccAction { get { return L.T(on ? "acc_turn_off" : "acc_turn_on"); } }
        protected override void AccDo() { Checked = !Checked; }
        public bool Checked
        {
            get { return on; }
            set { if (value != on) { on = value; Animator.Start(this); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }
        public void SetSilently(bool v) { on = v; pos = v ? 1 : 0; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { if (Enabled) Checked = !Checked; base.OnMouseClick(e); }
        public override bool Step()
        {
            bool more = base.Step();
            float t = on ? 1 : 0;
            pos += (t - pos) * Animator.Rate(0.3f);
            if (Math.Abs(t - pos) < 0.02f) pos = t;
            Invalidate();
            return more || pos != t;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            int w = Theme.S(42), h = Theme.S(24);
            RectangleF tr = new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
            Color off = Theme.Dark ? Theme.Mix(Theme.Track, Color.White, 0.06f) : Theme.Mix(Theme.Track, Color.Black, 0.06f), onc = Theme.AccentColor;
            Theme.FillRound(g, tr, h / 2f, Theme.Mix(off, onc, pos));
            float p = Theme.RTL ? 1 - pos : pos;
            float k = h - Theme.S(6);
            RectangleF kr = new RectangleF(tr.X + Theme.S(3) + p * (w - k - Theme.S(6)), tr.Y + Theme.S(3), k, k);
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillEllipse(sb, kr.X, kr.Y + 1, kr.Width, kr.Height);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, kr);
            PaintFocus(g, RectangleF.Inflate(tr, Theme.S(2), Theme.S(2)), h / 2f + Theme.S(2));
        }
    }

    public class Progress : UiControl, IAnimated
    {
        double value;
        public bool Indeterminate;
        float phase;
        public Progress() { }
        public double Value { get { return value; } set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public void SetIndeterminate(bool v) { Indeterminate = v; if (v) Animator.Start(this); Invalidate(); }
        public override bool Step()
        {
            if (!Indeterminate || !Visible) return false;
            phase += 0.012f * Animator.K; if (phase > 1) phase -= 1;
            Invalidate();
            return true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, r.Height / 2, Theme.Track);
            Color b = Theme.AccentColor;
            if (Indeterminate)
            {
                float w = r.Width * 0.3f, x = -w + (r.Width + w) * phase;
                RectangleF f = RectangleF.Intersect(new RectangleF(x, 0, w, r.Height), r);
                if (f.Width > 0) Theme.FillRound(g, f, r.Height / 2, b);
            }
            else if (value > 0)
            {
                float fw = Math.Max(r.Height, (float)(r.Width * value));
                RectangleF f = Theme.RTL ? new RectangleF(r.Right - fw, 0, fw, r.Height) : new RectangleF(0, 0, fw, r.Height);
                Theme.FillRound(g, f, r.Height / 2, b);
            }
        }
    }

    /// Rounded text field wrapping a borderless TextBox.
    public class TextField : UiControl
    {
        public readonly TextBox Box;
        public Func<string> Placeholder;
        public string Glyph;
        public bool ForceLtr;
        bool focused;
        /// An × at the end of the box that empties it (shown while there's text).
        public bool Clearable;
        public event Action Cleared;
        float xShown, xHot; bool overX;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public TextField(Func<string> placeholder, string glyph)
        {
            Placeholder = placeholder; Glyph = glyph;
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Theme.F(10.5f);
            if (placeholder != null) Box.AccessibleName = placeholder();
            Controls.Add(Box);
            Box.GotFocus += (s, e) => { focused = true; Invalidate(); };
            Box.TextChanged += (s, e) => { if (Clearable) Animator.Start(this); };
            Box.LostFocus += (s, e) => { focused = false; Invalidate(); };
            // the direction is set before Windows creates the box: changing it while the box is being created
            // makes Windows rebuild it mid-way, which fails ("Error creating window handle") — Arabic search window
            Box.RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Box.HandleCreated += (s, e) =>
            {
                ApplyCue(); LayoutBox();
                if (Box.RightToLeft != WantRtl) try { Box.BeginInvoke((Action)Restyle); } catch { }
            };
            Box.BackColor = Theme.Field; Box.ForeColor = Theme.Ink;
            Cursor = Cursors.IBeam;
            Surface = Surface.Card;
        }

        RightToLeft WantRtl { get { return (Theme.RTL && !ForceLtr) ? RightToLeft.Yes : RightToLeft.No; } }

        public void ApplyCue()
        {
            try { if (Box.IsHandleCreated && Placeholder != null) SendMessage(Box.Handle, 0x1501, (IntPtr)1, Placeholder()); } catch { }
            if (Placeholder != null && string.IsNullOrEmpty(AccessibleName)) Box.AccessibleName = Placeholder();
            else if (!string.IsNullOrEmpty(AccessibleName)) Box.AccessibleName = AccessibleName;
        }

        public void Restyle()
        {
            Box.BackColor = Theme.Field;
            Box.ForeColor = Theme.Ink;
            if (Box.RightToLeft != WantRtl) Box.RightToLeft = WantRtl;
            ApplyCue();
            LayoutBox();
            Invalidate();
        }

        protected override void OnClick(EventArgs e) { if (!overX) Box.Focus(); base.OnClick(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

        bool Rtl { get { return Theme.RTL && !ForceLtr; } }
        Rectangle XRect { get { int s = Theme.S(26); return new Rectangle(Rtl ? Theme.S(10) : Width - Theme.S(10) - s, (Height - s) / 2, s, s); } }
        public override bool Step()
        {
            bool more = base.Step();
            if (Fx.Ease(ref xShown, Clearable && Box.Text.Length > 0 ? 1 : 0, 0.3f)) more = true;
            if (Fx.Ease(ref xHot, overX ? 1 : 0, 0.3f)) more = true;
            Invalidate();
            return more;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool o = Clearable && Box.Text.Length > 0 && XRect.Contains(e.Location);
            if (o != overX) { overX = o; Cursor = o ? Cursors.Hand : Cursors.IBeam; Animator.Start(this); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { if (overX) { overX = false; Animator.Start(this); } base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (overX && e.Button == MouseButtons.Left)
            {
                // × : empty the box (and whatever was found for it)
                Box.Text = "";
                Box.Focus();
                if (Cleared != null) Cleared();
                overX = false; Animator.Start(this);
                return;
            }
            base.OnMouseDown(e);
        }

        void LayoutBox()
        {
            int pad = Theme.S(14), gw = string.IsNullOrEmpty(Glyph) ? 0 : Theme.S(26), xw = Clearable ? Theme.S(30) : 0;
            int h = Box.PreferredHeight;
            int x = Rtl ? pad + xw : pad + gw;
            Box.SetBounds(x, (Height - h) / 2, Math.Max(10, Width - pad * 2 - gw - xw), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, r, Theme.S(10), Theme.Field);
            Theme.StrokeRound(g, r, Theme.S(10), focused ? Theme.Mix(Theme.FieldBorder, Theme.Ink, 0.45f) : Theme.Mix(Theme.FieldBorder, Theme.InkFaint, hot * 0.4f), 1f);
            if (!string.IsNullOrEmpty(Glyph))
            {
                int gx = Theme.RTL && !ForceLtr ? Width - Theme.S(12) - Theme.S(20) : Theme.S(12);
                Theme.Glyph(g, Glyph, 11f, Theme.InkFaint, new Rectangle(gx, 0, Theme.S(20), Height));
            }
            if (Clearable && xShown > 0.02f)
            {
                // the × grows in when there's text, and gets a soft circle under the mouse
                RectangleF xr = XRect;
                float k = 0.6f + 0.4f * xShown;
                RectangleF xs = new RectangleF(xr.X + xr.Width * (1 - k) / 2, xr.Y + xr.Height * (1 - k) / 2, xr.Width * k, xr.Height * k);
                if (xHot > 0) Theme.FillRound(g, xs, xs.Width / 2, Color.FromArgb((int)(255 * xHot * xShown), Theme.Hover));
                Theme.Glyph(g, Ico.Close, 7.5f, Color.FromArgb((int)(255 * xShown), Theme.Mix(Theme.InkFaint, Theme.Ink, xHot)), Rectangle.Round(xs));
            }
        }
    }

    /// A shortcut shown in an empty page's drop area: a recent file, or "Scan Downloads".
    public class DropExtra
    {
        public string Text, Glyph;
        public Action Run;
    }

    /// Files opened lately, per page ("inspect", "studio"…), newest first; only ones that still exist are given.
    public static class RecentFiles
    {
        const int Keep = 8;
        public static void Add(string kind, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { path = System.IO.Path.GetFullPath(path); } catch { return; }
            if (path.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;   // not the app's own scratch copies
            List<string> l = new List<string>(Settings.Get("Recent." + kind, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries));
            l.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
            l.Insert(0, path);
            if (l.Count > Keep) l.RemoveRange(Keep, l.Count - Keep);
            Settings.Set("Recent." + kind, string.Join("|", l.ToArray()));
        }
        public static List<string> Get(string kind, int max)
        {
            List<string> r = new List<string>();
            foreach (string f in Settings.Get("Recent." + kind, "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (r.Count >= max) break;
                try { if (System.IO.File.Exists(f)) r.Add(f); } catch { }
            }
            return r;
        }
        /// The recent files as drop-area shortcuts that open them.
        public static List<DropExtra> Extras(string kind, int max, Action<string> open)
        {
            List<DropExtra> l = new List<DropExtra>();
            foreach (string f in Get(kind, max))
            {
                string f0 = f;
                l.Add(new DropExtra { Text = System.IO.Path.GetFileName(f), Glyph = Formats.GlyphFor(Formats.CategoryOf(Formats.Ext(f))), Run = () => open(f0) });
            }
            return l;
        }
    }

    /// The drop area of an empty page: a ring with the page's icon (the Donut Wheel's shape), what to drop, and
    /// what the page does — a live example or a few shortcuts (recent files, quick scans). The dashed frame sits in
    /// the middle; the whole area takes drops.
    public class DropZone : UiControl
    {
        public Func<string> Title, Sub;
        public string Glyph = Ico.Upload;
        public bool DragHot;
        /// Small tips shown at the bottom (keys); a different one each time the page is opened.
        public string[] Tips;
        /// Shortcuts under the words (asked for each time it's drawn), with a small heading.
        public Func<List<DropExtra>> Extras;
        public Func<string> ExtrasTitle;
        /// A live example, as pairs: before, after (Rename).
        public Func<string[]> Example;
        static int tipSeed = Environment.TickCount & 0xFFFF;
        int tip = -1;
        readonly List<Rectangle> chipRects = new List<Rectangle>();
        List<DropExtra> shown = new List<DropExtra>();
        int hoverChip = -1;
        protected override void OnVisibleChanged(EventArgs e)
        {
            if (Visible && Tips != null && Tips.Length > 0) tip = (tipSeed++) % Tips.Length;
            base.OnVisibleChanged(e);
        }

        public DropZone(Func<string> title, Func<string> sub)
        {
            Title = title; Sub = sub; Cursor = Cursors.Hand; AllowDrop = true; TabStop = true;
        }
        int keyChip = -1;   // a shortcut picked with the arrow keys (-1 = the drop area itself)
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            if ((e.KeyCode == Keys.Right || e.KeyCode == Keys.Left) && shown.Count > 0)
            {
                bool fwd = (e.KeyCode == Keys.Right) != Theme.RTL;
                keyChip = fwd ? (keyChip + 1 >= shown.Count ? -1 : keyChip + 1) : (keyChip <= -1 ? shown.Count - 1 : keyChip - 1);
                e.Handled = true; Invalidate(); return;
            }
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                if (keyChip >= 0 && keyChip < shown.Count) { try { shown[keyChip].Run(); } catch { } return; }
                OnClick(EventArgs.Empty);
            }
        }
        protected override void OnLostFocus(EventArgs e) { keyChip = -1; base.OnLostFocus(e); }
        protected override string AccName { get { return Title != null ? Title() : ""; } }
        protected override AccessibleRole AccRole { get { return AccessibleRole.PushButton; } }
        protected override string AccAction { get { return L.T("acc_press"); } }
        protected override void AccDo() { OnClick(EventArgs.Empty); }

        int ChipAt(Point p) { for (int i = 0; i < chipRects.Count; i++) if (chipRects[i].Contains(p)) return i; return -1; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = ChipAt(e.Location);
            if (h != hoverChip) { hoverChip = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { if (hoverChip >= 0) { hoverChip = -1; Invalidate(); } base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            int c = ChipAt(PointToClient(Cursor.Position));
            if (c >= 0 && c < shown.Count && !(e is KeyEventArgs)) { try { shown[c].Run(); } catch { } return; }
            base.OnClick(e);
        }

        /// The page's ring: a quiet track with an orange arc that sweeps further as the mouse comes near.
        public static void DrawRing(Graphics g, RectangleF r, float t, string glyph)
        {
            float w = Math.Max(3f, r.Width * 0.085f);
            RectangleF rr = RectangleF.Inflate(r, -w / 2, -w / 2);
            using (Pen tp = new Pen(Theme.Track, w)) g.DrawEllipse(tp, rr);
            using (Pen ap = new Pen(Theme.AccentColor, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(ap, rr, -90, Theme.RTL ? -(110 + 250 * t) : 110 + 250 * t);
            Theme.Glyph(g, glyph, r.Width * 0.22f / Theme.Scale, Theme.Mix(Theme.InkSoft, Theme.AccentColor, t), Rectangle.Round(r));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            float t = DragHot ? 1 : hot;
            string title = Title(), sub = Sub();
            shown = Extras != null ? (Extras() ?? new List<DropExtra>()) : new List<DropExtra>();
            string[] ex = Example != null ? Example() : null;
            bool roomy = Height > Theme.S(380);
            if (!roomy) { shown = new List<DropExtra>(); ex = null; }

            // how tall the middle is: ring, title, subtitle, then the example or the shortcuts
            int ring = Theme.S(roomy ? 72 : 56);
            int blockH = ring + Theme.S(16) + Theme.S(30) + Theme.S(26);
            int extraH = shown.Count > 0 ? Theme.S(26 + 40) : ex != null ? Theme.S(26) + (ex.Length / 2) * Theme.S(38) : 0;
            if (extraH > 0) blockH += Theme.S(24) + extraH;
            int panelW = Math.Min(Width - Theme.S(4), Math.Max(Theme.S(560), Math.Min(Theme.S(760), (int)(Width * 0.7f))));
            int panelH = Math.Min(Height - Theme.S(4), blockH + Theme.S(96));
            if (DragHot || Height < Theme.S(420) || Width < Theme.S(600)) { panelW = Width - Theme.S(4); panelH = Height - Theme.S(4); }
            int tipRoom = Tips != null && Tips.Length > 0 && Height > Theme.S(300) ? Theme.S(30) : 0;
            RectangleF r = new RectangleF((Width - panelW) / 2f, Math.Max(Theme.S(2), (Height - panelH - tipRoom) / 2f), panelW - 1, panelH - 1);
            Theme.FillRound(g, r, Theme.S(18), DragHot ? Theme.AccentSoft : Theme.Mix(Theme.CardInner, Theme.Hover, t * 0.6f));
            using (GraphicsPath p = Theme.Round(r, Theme.S(18)))
            using (Pen pen = new Pen(DragHot ? Theme.AccentColor : Theme.Mix(Theme.FieldBorder, Theme.InkFaint, t * 0.6f), Theme.S(1.4f)))
            {
                pen.DashPattern = new float[] { 4f, 3f };
                g.DrawPath(pen, p);
            }

            float cx = Width / 2f, top = r.Y + (r.Height - blockH) / 2f;
            RectangleF circle = new RectangleF(cx - ring / 2f, top - t * Theme.S(3), ring, ring);
            DrawRing(g, circle, t, Glyph);
            int inner = (int)r.Width - Theme.S(40);
            Rectangle t1 = new Rectangle((int)r.X + Theme.S(20), (int)(top + ring + Theme.S(16)), inner, Theme.S(30));
            float ts = 15f, ss = 9.5f;
            while (ts > 10f && Theme.Measure(title, Theme.Display(ts)) > t1.Width) ts -= 0.5f;
            while (ss > 7.5f && Theme.Measure(sub, Theme.F(ss)) > t1.Width) ss -= 0.25f;
            Theme.Text(g, title, Theme.Display(ts), Theme.Ink, t1, Align.Center);
            Rectangle t2 = new Rectangle(t1.X, t1.Bottom + Theme.S(2), inner, Theme.S(24));
            Theme.Text(g, sub, Theme.F(ss), Theme.InkSoft, t2, Align.Center, true);

            chipRects.Clear();
            int y = t2.Bottom + Theme.S(24);
            if (shown.Count > 0)
            {
                string head = ExtrasTitle != null ? ExtrasTitle() : "";
                Theme.Text(g, head.ToUpper(System.Globalization.CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, new Rectangle(t1.X, y, inner, Theme.S(18)), Align.Center);
                y += Theme.S(26);
                Font cf = Theme.F(9f);
                int gap = Theme.S(8), ch = Theme.S(36), maxW = Theme.S(220);
                List<int> widths = new List<int>();
                int total = 0;
                foreach (DropExtra d in shown)
                {
                    int w = Math.Min(maxW, Theme.Measure(d.Text, cf) + Theme.S(46));
                    if (total + w + (widths.Count > 0 ? gap : 0) > inner) break;
                    total += w + (widths.Count > 0 ? gap : 0); widths.Add(w);
                }
                int x = (int)(cx - total / 2f);
                for (int i = 0; i < widths.Count; i++)
                {
                    Rectangle cr = Theme.Mirror(new Rectangle(x, y, widths[i], ch), Width);
                    chipRects.Add(cr);
                    bool hv = i == hoverChip || (KeyFocus && i == keyChip);
                    Theme.FillRound(g, cr, ch / 2f, hv ? Theme.Hover : Theme.Card);
                    Theme.StrokeRound(g, cr, ch / 2f, hv ? Theme.Mix(Theme.ChipBorder, Theme.AccentColor, 0.6f) : Theme.ChipBorder);
                    Rectangle gr = Theme.RTL ? new Rectangle(cr.Right - Theme.S(34), cr.Y, Theme.S(22), ch) : new Rectangle(cr.X + Theme.S(12), cr.Y, Theme.S(22), ch);
                    Theme.Glyph(g, shown[i].Glyph, 9f, hv ? Theme.AccentColor : Theme.InkSoft, gr);
                    Rectangle tr = Theme.RTL ? new Rectangle(cr.X + Theme.S(12), cr.Y, cr.Width - Theme.S(46), ch) : new Rectangle(cr.X + Theme.S(36), cr.Y, cr.Width - Theme.S(48), ch);
                    Theme.Text(g, shown[i].Text, cf, Theme.Ink, tr, Align.Start, true, false, true);
                    x += widths[i] + gap;
                }
                if (widths.Count < shown.Count) shown = shown.GetRange(0, widths.Count);
            }
            else if (ex != null && ex.Length >= 2)
            {
                string head = ExtrasTitle != null ? ExtrasTitle() : "";
                Theme.Text(g, head.ToUpper(System.Globalization.CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, new Rectangle(t1.X, y, inner, Theme.S(18)), Align.Center);
                y += Theme.S(26);
                Font ef = Theme.F(9f);
                int pw = Theme.S(170), aw = Theme.S(36), ph = Theme.S(30);
                for (int k = 0; k + 1 < ex.Length; k += 2)
                {
                    int x0 = (int)(cx - (pw * 2 + aw) / 2f);
                    Rectangle a = Theme.Mirror(new Rectangle(x0, y, pw, ph), Width), b = Theme.Mirror(new Rectangle(x0 + pw + aw, y, pw, ph), Width);
                    Rectangle arrow = Theme.Mirror(new Rectangle(x0 + pw, y, aw, ph), Width);
                    Theme.FillRound(g, a, Theme.S(8), Theme.Card); Theme.StrokeRound(g, a, Theme.S(8), Theme.ChipBorder);
                    Theme.FillRound(g, b, Theme.S(8), Theme.AccentSoft);
                    Theme.Text(g, ex[k], ef, Theme.InkSoft, a, Align.Center, true, false, true);
                    Theme.Text(g, ex[k + 1], Theme.F(9f, FontStyle.Bold), Theme.Ink, b, Align.Center, true, false, true);
                    Theme.Glyph(g, Theme.RTL ? Ico.Back : Ico.Forward, 8f, Theme.AccentColor, arrow);
                    y += Theme.S(38);
                }
            }

            if (tipRoom > 0)
            {
                if (tip < 0) tip = (tipSeed++) % Tips.Length;
                string tt = L.T(Tips[tip % Tips.Length]);
                float tsz = 9f;
                while (tsz > 7.5f && Theme.Measure(tt, Theme.F(tsz)) > Width - Theme.S(80)) tsz -= 0.25f;
                Font tf = Theme.F(tsz);
                int tw = Math.Min(Width - Theme.S(80), Theme.Measure(tt, tf)), gw = Theme.S(18);
                int ty = Math.Min(Height - Theme.S(44), (int)r.Bottom + Theme.S(14));
                if (r.Bottom > Height - Theme.S(60)) ty = Height - Theme.S(54);
                Rectangle row = new Rectangle((Width - tw - gw - Theme.S(8)) / 2, ty, tw + gw + Theme.S(8), Theme.S(30));
                Rectangle pill = new Rectangle(row.X - Theme.S(12), row.Y, row.Width + Theme.S(24), row.Height);
                Theme.FillRound(g, pill, Theme.S(15), Theme.Mix(Theme.CardInner, Theme.Card, 0.5f));
                Rectangle gr = Theme.Mirror(new Rectangle(row.X, row.Y, gw, row.Height), Width);
                Rectangle tr = Theme.Mirror(new Rectangle(row.X + gw + Theme.S(8), row.Y, tw + 2, row.Height), Width);
                Theme.Glyph(g, "\uEA80", 9f, Theme.AccentColor, gr);
                Theme.Text(g, tt, tf, Theme.InkSoft, tr, Align.Start, true);
            }
            if (KeyFocus && keyChip < 0) PaintFocus(g, r, Theme.S(18));
            else if (KeyFocus && keyChip < chipRects.Count) PaintFocus(g, RectangleF.Inflate(chipRects[keyChip], Theme.S(3), Theme.S(3)), Theme.S(21));
        }
    }

    /// Scrollable, virtualized list with a slim custom scrollbar (looks right in dark mode too).
    public abstract class ScrollList : UiControl
    {
        protected int scroll;
        protected int hoverIndex = -1;
        public int RowHeight = 60;
        bool dragThumb; int dragStartY, dragStartScroll;
        float barHot;

        public abstract int Count { get; }
        protected abstract void DrawRow(Graphics g, int index, Rectangle r, bool hover);
        protected virtual void RowClick(int index, Point local, Rectangle row) { }
        protected virtual void RowDoubleClick(int index) { }
        protected virtual void RowHover(int index, Point local, Rectangle row) { }
        protected virtual void PaintEmpty(Graphics g) { }
        /// Files a row can be dragged out of the app as (null = it can't).
        protected virtual string[] DragFiles(int index) { return null; }

        protected int RH { get { return Theme.S(RowHeight); } }
        int ContentH { get { return Count * RH; } }

        // ------------------------------------------------------------------ keyboard & screen readers
        protected ScrollList() { TabStop = true; }
        /// The row the keyboard is on (-1 = none yet).
        protected int keyRow = -1;
        /// What a screen reader says for a row (null = nothing to say).
        protected virtual string RowText(int index) { return null; }
        protected override AccessibleRole AccRole { get { return AccessibleRole.List; } }
        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || k == Keys.Home || k == Keys.End || k == Keys.Enter || base.IsInputKey(k);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Count == 0) { base.OnKeyDown(e); return; }
            int page = Math.Max(1, Height / Math.Max(1, RH) - 1), to = keyRow < 0 ? Math.Max(0, scroll / Math.Max(1, RH)) : keyRow;
            switch (e.KeyCode)
            {
                case Keys.Up: to = keyRow < 0 ? to : to - 1; break;
                case Keys.Down: to = keyRow < 0 ? to : to + 1; break;
                case Keys.PageUp: to -= page; break;
                case Keys.PageDown: to += page; break;
                case Keys.Home: to = 0; break;
                case Keys.End: to = Count - 1; break;
                case Keys.Enter: if (keyRow >= 0 && keyRow < Count) RowDoubleClick(keyRow); e.Handled = true; return;
                default: base.OnKeyDown(e); return;
            }
            keyRow = Math.Max(0, Math.Min(Count - 1, to));
            hoverIndex = keyRow;
            EnsureVisible(keyRow);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Focus, keyRow);
            e.Handled = true;
        }
        protected override void OnLostFocus(EventArgs e) { if (keyRow >= 0 && hoverIndex == keyRow) hoverIndex = -1; keyRow = -1; base.OnLostFocus(e); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new ListAccessible(this); }

        class ListAccessible : UiAccessible
        {
            readonly ScrollList l;
            public ListAccessible(ScrollList owner) : base(owner) { l = owner; }
            public override int GetChildCount() { return l.Count; }
            public override AccessibleObject GetChild(int i) { return i >= 0 && i < l.Count ? new RowAccessible(l, this, i) : null; }
            public override AccessibleObject GetFocused() { return l.Focused && l.keyRow >= 0 && l.keyRow < l.Count ? GetChild(l.keyRow) : base.GetFocused(); }
        }
        class RowAccessible : AccessibleObject
        {
            readonly ScrollList l; readonly AccessibleObject parent; readonly int row;
            public RowAccessible(ScrollList s, AccessibleObject p, int i) { l = s; parent = p; row = i; }
            public override string Name { get { return row < l.Count ? l.RowText(row) ?? "" : ""; } set { } }
            public override AccessibleObject Parent { get { return parent; } }
            public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
            public override Rectangle Bounds { get { return l.RectangleToScreen(Rectangle.Intersect(l.RowRect(row), l.ClientRectangle)); } }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates st = AccessibleStates.Focusable | AccessibleStates.Selectable;
                    Rectangle r = l.RowRect(row);
                    if (r.Bottom <= 0 || r.Y >= l.Height) st |= AccessibleStates.Offscreen;
                    if (l.Focused && l.keyRow == row) st |= AccessibleStates.Focused | AccessibleStates.Selected;
                    return st;
                }
            }
            public override void Select(AccessibleSelection flags) { if ((flags & AccessibleSelection.TakeFocus) != 0) { l.Focus(); l.keyRow = row; l.hoverIndex = row; l.EnsureVisible(row); l.Invalidate(); } }
        }
        int MaxScroll { get { return Math.Max(0, ContentH - Height); } }
        protected int BarW { get { return MaxScroll > 0 ? Theme.S(12) : 0; } }

        // smooth scrolling: the wheel moves a target and the view glides to it
        float scrollF; int target;
        public void ClampScroll()
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll));
            target = Math.Max(0, Math.Min(MaxScroll, target));
        }
        void Jump(int v) { scroll = target = v; ClampScroll(); scrollF = scroll; }
        public void ScrollTo(int v) { Jump(v); Invalidate(); }
        public void ScrollToEnd() { Jump(MaxScroll); Invalidate(); }
        public void EnsureVisible(int i)
        {
            int top = i * RH, t = target;
            if (top < t) t = top;
            else if (top + RH > t + Height) t = top + RH - Height;
            target = Math.Max(0, Math.Min(MaxScroll, t));
            scrollF = scroll;
            Animator.Start(this);
        }
        public override bool Step()
        {
            bool more = base.Step();
            if (scroll != target)
            {
                scrollF += (target - scrollF) * Animator.Rate(0.32f);
                if (Math.Abs(target - scrollF) < 0.6f) scrollF = target;
                scroll = (int)Math.Round(scrollF);
                Invalidate();
                return true;
            }
            return more;
        }

        /// Where the rows on screen are (for "clear" animations); which = the rows to include (all when null).
        public List<Rectangle> VisibleRows(Func<int, bool> which)
        {
            List<Rectangle> l = new List<Rectangle>();
            if (Count == 0 || RH <= 0) return l;
            int first = Math.Max(0, scroll / RH), last = Math.Min(Count - 1, (scroll + Height) / RH);
            for (int i = first; i <= last; i++) if (which == null || which(i)) l.Add(RowRect(i));
            return l;
        }

        protected Rectangle RowRect(int i)
        {
            int x = Theme.RTL ? BarW : 0;
            return new Rectangle(x, i * RH - scroll, Width - BarW, RH);
        }

        Rectangle ThumbRect()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            int h = Math.Max(Theme.S(36), Height * Height / Math.Max(1, ContentH));
            int y = (int)((Height - h) * (scroll / (double)MaxScroll));
            int x = Theme.RTL ? Theme.S(3) : Width - Theme.S(9);
            return new Rectangle(x, y, Theme.S(6), h);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (scroll == target) scrollF = scroll;
            target -= e.Delta / 120 * Math.Max(RH, Theme.S(48));
            target = Math.Max(0, Math.Min(MaxScroll, target));
            Animator.Start(this);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Rectangle th = ThumbRect();
            if (!th.IsEmpty && new Rectangle(th.X - Theme.S(4), 0, th.Width + Theme.S(8), Height).Contains(e.Location))
            {
                dragThumb = true; dragStartY = e.Y; Jump(scroll); dragStartScroll = scroll; if (!th.Contains(e.Location)) { Jump((int)((e.Y / (double)Height) * ContentH - Height / 2)); dragStartScroll = scroll; } Capture = true; Invalidate(); return;
            }
            Focus();
            int row = (e.Y + scroll) / RH;
            if (e.Button == MouseButtons.Left && row >= 0 && row < Count && DragFiles(row) != null)
                DragOut.Arm(this, e.Location, () => row < Count ? DragFiles(row) : null);
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragThumb = false; Capture = false; base.OnMouseUp(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragThumb)
            {
                Rectangle th = ThumbRect();
                int track = Height - th.Height;
                if (track > 0) { Jump(dragStartScroll + (int)((e.Y - dragStartY) * (MaxScroll / (double)track))); Invalidate(); }
                return;
            }
            int i = (e.Y + scroll) / RH;
            if (i < 0 || i >= Count || (BarW > 0 && (Theme.RTL ? e.X < BarW : e.X > Width - BarW))) i = -1;
            if (i != hoverIndex) { hoverIndex = i; Invalidate(); }
            if (i >= 0) RowHover(i, e.Location, RowRect(i));
            float bh = (BarW > 0 && (Theme.RTL ? e.X < BarW + Theme.S(4) : e.X > Width - BarW - Theme.S(4))) ? 1 : 0;
            if (bh != barHot) { barHot = bh; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; barHot = 0; base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = (e.Y + scroll) / RH;
            if (i >= 0 && i < Count && e.Button == MouseButtons.Left) RowClick(i, e.Location, RowRect(i));
            base.OnMouseClick(e);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int i = (e.Y + scroll) / RH;
            if (i >= 0 && i < Count) RowDoubleClick(i);
            base.OnMouseDoubleClick(e);
        }
        protected override void OnResize(EventArgs e) { ClampScroll(); base.OnResize(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            ClampScroll();
            if (Count == 0) { PaintEmpty(g); return; }
            int first = Math.Max(0, scroll / RH), last = Math.Min(Count - 1, (scroll + Height) / RH);
            for (int i = first; i <= last; i++)
            {
                Rectangle r = RowRect(i);
                GraphicsState st = g.Save();
                g.SetClip(r);
                DrawRow(g, i, r, i == hoverIndex);
                g.Restore(st);
            }
            if (keyRow >= first && keyRow <= last) { Theme.Smooth(g); Rectangle kr = RowRect(keyRow); kr.Inflate(-Theme.S(2), -Theme.S(2)); PaintFocus(g, kr, Theme.S(8)); }
            Rectangle th = ThumbRect();
            if (!th.IsEmpty)
            {
                Theme.Smooth(g);
                if (barHot > 0 || dragThumb) { th.Inflate(Theme.S(1), 0); }
                Theme.FillRound(g, th, th.Width / 2f, Color.FromArgb(barHot > 0 || dragThumb ? 160 : 90, Theme.InkFaint));
            }
        }
    }

    /// Small notification that slides up from the bottom of the window.
    public class Toast : UiControl
    {
        string message; string glyph; Color glyphColor;
        Func<string> actionLabel; Action action;
        Timer hideTimer;
        float shown; bool wantShown;
        Rectangle actionRect;
        public Toast()
        {
            Visible = false;
            hideTimer = new Timer(); hideTimer.Interval = 6000;
            hideTimer.Tick += (s, e) => { hideTimer.Stop(); wantShown = false; Animator.Start(this); };
            Surface = Surface.Background;
        }

        public void Show(string msg, bool ok, Func<string> actLabel, Action act)
        {
            message = msg; glyph = ok ? Ico.Check : Ico.Error; glyphColor = ok ? Theme.Success : Theme.Error;
            actionLabel = actLabel; action = act;
            Width = Math.Min(Parent != null ? Parent.Width - Theme.S(40) : Theme.S(520), Theme.Measure(msg, Theme.F(10f)) + Theme.S(90) + (actLabel != null ? Theme.Measure(actLabel(), Theme.F(10f, FontStyle.Bold)) + Theme.S(30) : 0));
            Height = Theme.S(52);
            wantShown = true;
            Visible = true; BringToFront();
            Place();
            Animator.Start(this);
            hideTimer.Stop(); hideTimer.Start();
            Invalidate();
        }

        public Rectangle Host;
        void Place()
        {
            int bottom = Host.Bottom - Theme.S(24);
            int y = (int)(bottom - Height * shown + (1 - shown) * Theme.S(30));
            Location = new Point(Host.X + (Host.Width - Width) / 2, Math.Min(y, bottom - Theme.S(4)));
        }

        public override bool Step()
        {
            float t = wantShown ? 1 : 0;
            shown += (t - shown) * Animator.Rate(0.25f);
            if (Math.Abs(t - shown) < 0.02f) shown = t;
            if (shown <= 0) Visible = false;
            Place();
            Invalidate();
            return shown != t;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (action != null && actionRect.Contains(e.Location)) { action(); wantShown = false; Animator.Start(this); }
            else { wantShown = false; Animator.Start(this); }
        }
        protected override void OnMouseMove(MouseEventArgs e) { Cursor = actionRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color bg = Theme.Dark ? Color.FromArgb(240, 238, 230) : Color.FromArgb(38, 38, 36);
            Color fg = Theme.Dark ? Color.FromArgb(24, 24, 22) : Color.FromArgb(250, 249, 245);
            Theme.FillRound(g, r, Theme.S(12), bg);
            int gx = Theme.RTL ? Width - Theme.S(44) : Theme.S(14);
            Theme.Glyph(g, glyph, 12f, glyphColor, new Rectangle(gx, 0, Theme.S(30), Height));
            int aw = actionLabel != null ? Theme.Measure(actionLabel(), Theme.F(10f, FontStyle.Bold)) + Theme.S(24) : 0;
            Rectangle tr = new Rectangle(Theme.S(52), 0, Width - Theme.S(66) - aw, Height);
            if (Theme.RTL) tr.X = Width - tr.Right;
            Theme.Text(g, message, Theme.F(10f), fg, tr);
            if (actionLabel != null)
            {
                actionRect = new Rectangle(Theme.RTL ? Theme.S(8) : Width - aw - Theme.S(8), Theme.S(10), aw, Height - Theme.S(20));
                Theme.FillRound(g, actionRect, Theme.S(8), Color.FromArgb(Theme.Dark ? 30 : 40, Theme.Dark ? 0 : 255, Theme.Dark ? 0 : 255, Theme.Dark ? 0 : 255));
                Theme.Text(g, actionLabel(), Theme.F(10f, FontStyle.Bold), fg, actionRect, Align.Center);
            }
            else actionRect = Rectangle.Empty;
        }
    }

    public class NavItem
    {
        public string Key; public string Glyph; public float Hot;
        /// Starts a new group in the side list (a small heading key, e.g. "nav_sec_files").
        public string Section;
        /// A count shown as a small pill (0 = none), e.g. downloads going on.
        public Func<int> Badge;
        public NavItem(string key, string glyph) { Key = key; Glyph = glyph; }
    }

    /// Base for a page: themed background, page title, cards, and RTL-aware placement helpers.
    public abstract class Page : UiControl
    {
        public string TitleKey, SubKey;
        public string Glyph;          // icon shown next to the page title
        protected List<Rectangle> cards = new List<Rectangle>();
        public Toast Toast;   // set by the main window
        public Form Owner;

        protected override string AccName { get { return TitleKey != null ? L.T(TitleKey) : ""; } }
        protected override AccessibleRole AccRole { get { return AccessibleRole.Grouping; } }

        protected Page(string titleKey, string subKey)
        {
            TitleKey = titleKey; SubKey = subKey;
            Surface = Surface.Background;
            AllowDrop = true;
        }

        /// Top of the content area below the title (moves up when a long page is scrolled).
        protected int Top0 { get { return Theme.S(96) + TopExtra - ScrollY; } }
        /// Room taken above the content by something the page sits under (the Studio's file tabs).
        public int TopExtra;
        /// How far a long page (Settings) has been scrolled down.
        protected int ScrollY;
        /// Room kept free at the end of the title row (for the tabs of a page that holds several tools).
        public int HeaderReserve;
        protected int Pad { get { return Theme.S(28); } }

        public abstract void DoLayout();
        public virtual void Restyle() { }
        public virtual void OnShown() { }

        /// Run on the UI thread from a worker. Waits (briefly) for the window to exist first,
        /// so results that arrive while the app is still opening aren't lost.
        protected void Post(Action a)
        {
            if (System.Threading.Thread.CurrentThread.IsBackground)
                for (int i = 0; i < 200 && !IsHandleCreated && !IsDisposed; i++) System.Threading.Thread.Sleep(50);
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }
        public virtual bool Busy { get { return false; } }
        public virtual void AcceptFiles(string[] paths) { }

        protected Rectangle M(Rectangle r) { return Theme.Mirror(r, Width); }
        protected void Place(Control c, Rectangle ltr) { c.Bounds = M(ltr); }

        protected void Label(Graphics g, string text, Font f, Color c, Rectangle ltr, Align a = Align.Start, bool vcenter = true, bool wrap = false)
        {
            Theme.Text(g, text, f, c, M(ltr), a, vcenter, wrap);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Width > 0 && Height > 0) DoLayout(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            int tx = Pad;
            Label(g, L.T(TitleKey), Theme.Display(20f), Theme.Ink, new Rectangle(tx, Theme.S(18) - ScrollY, Width - Pad - tx - HeaderReserve, Theme.S(40)), Align.Start, false);
            // the whole line under the title: a little smaller when the window is narrow, never cut off
            string sub = L.T(SubKey); int subW = Width - Pad - tx - HeaderReserve;
            float ss = 9.75f;
            while (ss > 8f && Theme.Measure(sub, Theme.F(ss)) > subW) ss -= 0.25f;
            Label(g, sub, Theme.F(ss), Theme.InkSoft, new Rectangle(tx, Theme.S(58) - ScrollY, subW, Theme.S(24)), Align.Start, false);
            foreach (Rectangle c in cards) Theme.PaintCard(g, M(c));
            PaintContent(g);
        }

        protected virtual void PaintContent(Graphics g) { }

        protected void ShowToast(string msg, bool ok, Func<string> act, Action a)
        {
            if (Toast != null) Toast.Show(msg, ok, act, a);
        }

        /// Hooks drag & drop for this page and the given child controls.
        protected void EnableDrop(params Control[] extra)
        {
            List<Control> all = new List<Control>(extra); all.Add(this);
            foreach (Control c in all)
            {
                c.AllowDrop = true;
                c.DragEnter += (s, e) => { if (DragOut.Active) { e.Effect = DragDropEffects.None; return; } e.Effect = !Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; DragHighlight(true); };
                c.DragLeave += (s, e) => DragHighlight(false);
                c.DragDrop += (s, e) => { DragHighlight(false); if (DragOut.Active) return; string[] p = e.Data.GetData(DataFormats.FileDrop) as string[]; if (p != null && !Busy) AcceptFiles(p); };
            }
        }
        protected virtual void DragHighlight(bool on) { }
    }
}
