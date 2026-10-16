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

        public Btn(Func<string> label, string glyph, BtnKind kind)
        {
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
                            bg = Theme.Mix(bg, Theme.Dark ? Color.White : Color.Black, hot * 0.08f);
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

        public Segments(params Func<string>[] items) { Items = items; Cursor = Cursors.Hand; }

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
                Theme.FillRound(g, pr, Theme.S(7), Theme.Dark ? Theme.Mix(Theme.Card, Color.White, 0.06f) : Color.White);
                Theme.StrokeRound(g, pr, Theme.S(7), Theme.Dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(26, 60, 50, 30));
            }
            for (int i = 0; i < Items.Length; i++)
            {
                RectangleF r = Seg(i);
                Color fg = Theme.InkSoft;
                if (i == selected) fg = Theme.Ink;
                else if (i == hoverIndex && Enabled && Math.Abs(pos - i) > 0.5f) { Theme.FillRound(g, r, Theme.S(7), Color.FromArgb(Theme.Dark ? 16 : 70, Color.White)); fg = Theme.Ink; }
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

        public ChipGrid() { Cursor = Cursors.Hand; }

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
                // the orange chip flows over to the one clicked (its front edge leads, its back edge catches up)
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
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle c = Cell(i);
                RectangleF r = CellF(i);
                bool sel = items[i] == selected && !moving;
                Color fg;
                if (sel)
                {
                    Theme.FillRound(g, r, Theme.S(8), Enabled ? Theme.PrimaryTop : Theme.InkFaint);
                    fg = Theme.PrimaryInk;
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
            Theme.FillRound(g, pill, Theme.S(8), Enabled ? Theme.PrimaryTop : Theme.InkFaint);
            // the labels under the moving chip turn white where it covers them
            GraphicsState st = g.Save();
            using (System.Drawing.Drawing2D.GraphicsPath clip = Theme.Round(pill, Theme.S(8)))
            {
                g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
                for (int i = 0; i < items.Count; i++)
                    if (CellF(i).IntersectsWith(pill)) Theme.Text(g, ChipLabel(i), f, Theme.PrimaryInk, Cell(i), Align.Center, true, false, LabelOf == null);
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
        public Toggle() { Cursor = Cursors.Hand; }
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public TextField(Func<string> placeholder, string glyph)
        {
            Placeholder = placeholder; Glyph = glyph;
            Box = new TextBox();
            Box.BorderStyle = BorderStyle.None;
            Box.Font = Theme.F(10.5f);
            Controls.Add(Box);
            Box.GotFocus += (s, e) => { focused = true; Invalidate(); };
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

        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

        void LayoutBox()
        {
            int pad = Theme.S(14), gw = string.IsNullOrEmpty(Glyph) ? 0 : Theme.S(26);
            int h = Box.PreferredHeight;
            int x = Theme.RTL && !ForceLtr ? pad : pad + gw;
            Box.SetBounds(x, (Height - h) / 2, Math.Max(10, Width - pad * 2 - gw), h);
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
        }
    }

    /// Big dashed drop area with an icon, title and subtitle.
    public class DropZone : UiControl
    {
        public Func<string> Title, Sub;
        public string Glyph = Ico.Upload;
        public bool DragHot;
        /// Small tips shown at the bottom (keys); a different one each time the page is opened.
        public string[] Tips;
        static int tipSeed = Environment.TickCount & 0xFFFF;
        int tip = -1;
        protected override void OnVisibleChanged(EventArgs e)
        {
            if (Visible && Tips != null && Tips.Length > 0) tip = (tipSeed++) % Tips.Length;
            base.OnVisibleChanged(e);
        }

        public DropZone(Func<string> title, Func<string> sub)
        {
            Title = title; Sub = sub; Cursor = Cursors.Hand; AllowDrop = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            float t = DragHot ? 1 : hot;
            RectangleF r = new RectangleF(Theme.S(2), Theme.S(2), Width - Theme.S(4) - 1, Height - Theme.S(4) - 1);
            Theme.FillRound(g, r, Theme.S(14), DragHot ? Theme.AccentSoft : Theme.Mix(Theme.CardInner, Theme.Hover, t * 0.6f));
            using (GraphicsPath p = Theme.Round(r, Theme.S(14)))
            using (Pen pen = new Pen(DragHot ? Theme.AccentColor : Theme.Mix(Theme.FieldBorder, Theme.InkFaint, t * 0.6f), Theme.S(1.4f)))
            {
                pen.DashPattern = new float[] { 4f, 3f };
                g.DrawPath(pen, p);
            }
            float cx = Width / 2f, cy = Height / 2f - Theme.S(30);
            float s = Theme.S(56);
            RectangleF circle = new RectangleF(cx - s / 2, cy - s / 2 - t * Theme.S(3), s, s);
            Theme.FillRound(g, circle, Theme.S(16), Theme.Mix(Theme.Card, Theme.AccentSoft, t));
            Theme.StrokeRound(g, circle, Theme.S(16), Theme.CardBorder);
            Theme.Glyph(g, Glyph, 15f, Theme.Mix(Theme.InkSoft, Theme.AccentColor, t), Rectangle.Round(circle));
            Rectangle t1 = new Rectangle(Theme.S(10), (int)(cy + s / 2 + Theme.S(16)), Width - Theme.S(20), Theme.S(30));
            Theme.Text(g, Title(), Theme.Serif(14.5f), Theme.Ink, t1, Align.Center);
            Rectangle t2 = new Rectangle(Theme.S(10), t1.Bottom + Theme.S(2), Width - Theme.S(20), Theme.S(24));
            Theme.Text(g, Sub(), Theme.F(9.5f), Theme.InkSoft, t2, Align.Center);
            if (Tips != null && Tips.Length > 0 && Height > Theme.S(300))
            {
                if (tip < 0) tip = (tipSeed++) % Tips.Length;
                string tt = L.T(Tips[tip % Tips.Length]);
                Font tf = Theme.F(9f);
                int tw = Math.Min(Width - Theme.S(80), Theme.Measure(tt, tf)), gw = Theme.S(18);
                Rectangle row = new Rectangle((Width - tw - gw - Theme.S(8)) / 2, Height - Theme.S(54), tw + gw + Theme.S(8), Theme.S(30));
                Rectangle pill = new Rectangle(row.X - Theme.S(12), row.Y, row.Width + Theme.S(24), row.Height);
                Theme.FillRound(g, pill, Theme.S(15), Theme.Mix(Theme.CardInner, Theme.Card, 0.5f));
                Rectangle gr = Theme.Mirror(new Rectangle(row.X, row.Y, gw, row.Height), Width);
                Rectangle tr = Theme.Mirror(new Rectangle(row.X + gw + Theme.S(8), row.Y, tw + 2, row.Height), Width);
                Theme.Glyph(g, "\uEA80", 9f, Theme.AccentColor, gr);
                Theme.Text(g, tt, tf, Theme.InkSoft, tr, Align.Start);
            }
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

        protected Page(string titleKey, string subKey)
        {
            TitleKey = titleKey; SubKey = subKey;
            Surface = Surface.Background;
            AllowDrop = true;
        }

        /// Top of the content area below the title (moves up when a long page is scrolled).
        protected int Top0 { get { return Theme.S(96) - ScrollY; } }
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
            Label(g, L.T(TitleKey), Theme.Serif(20f), Theme.Ink, new Rectangle(tx, Theme.S(18) - ScrollY, Width - Pad - tx - HeaderReserve, Theme.S(40)), Align.Start, false);
            Label(g, L.T(SubKey), Theme.F(9.75f), Theme.InkSoft, new Rectangle(tx, Theme.S(58) - ScrollY, Width - Pad - tx - HeaderReserve, Theme.S(24)), Align.Start, false);
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
