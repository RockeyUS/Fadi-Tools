using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FileConverter
{
    /// A small floating card for one photo placed on a page: its light, contrast, color and warmth, applied as the
    /// sliders move. Done (or a click elsewhere) keeps it; Esc puts it back as it was.
    public class PicAdjustPopup : Form
    {
        readonly Slider[] sliders;
        readonly string[] keys = { "adj_bright", "adj_contrast", "adj_sat", "adj_warm" };
        readonly Btn bReset, bDone;
        readonly Action<Adjust> apply;
        readonly Adjust before;
        Rectangle from;
        Form owner;
        bool closing;
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public static void ShowAt(Control over, Point screen, Adjust current, Action<Adjust> apply)
        {
            PicAdjustPopup p = new PicAdjustPopup(current, apply);
            Rectangle wa = Screen.FromPoint(screen).WorkingArea;
            int w = p.Width, h = p.Height;
            int x = Theme.RTL ? screen.X - w : screen.X, y = screen.Y;
            if (x + w > wa.Right) x = screen.X - w;
            if (x < wa.Left) x = screen.X;
            if (y + h > wa.Bottom) y = screen.Y - h;
            p.Location = new Point(Math.Max(wa.Left, Math.Min(wa.Right - w, x)), Math.Max(wa.Top, Math.Min(wa.Bottom - h, y)));
            p.from = new Rectangle(screen, new Size(1, 1));
            p.owner = over.FindForm();
            IntPtr hh = p.Handle;
            Fx.PopIn(p, p.owner, p.from, Theme.S(14), 150, () => { if (!p.IsDisposed) p.Show(p.owner); });
        }

        PicAdjustPopup(Adjust current, Action<Adjust> apply)
        {
            this.apply = apply;
            before = current != null ? current.Clone() : new Adjust();
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            KeyPreview = true; AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Theme.Card; Font = Theme.F(9.5f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            int W = Theme.S(320), pad = Theme.S(18), rowH = Theme.S(44), top = Theme.S(54);
            Func<float, string> signed = v => { int i = (int)Math.Round(v); return i > 0 ? "+" + i : i.ToString(); };
            float[] vals = { before.Bright, before.Contrast, before.Sat, before.Warmth };
            sliders = new Slider[4];
            for (int i = 0; i < 4; i++)
            {
                Slider s = new Slider { Min = -100, Max = 100, Value = vals[i], Format = signed, Surface = Surface.Card };
                s.ValueChanged += (o, e) => Push();
                s.Bounds = Theme.Mirror(new Rectangle(pad + Theme.S(86), top + i * rowH + Theme.S(6), W - pad * 2 - Theme.S(86), Theme.S(32)), W);
                sliders[i] = s;
                Controls.Add(s);
            }
            int by = top + 4 * rowH + Theme.S(10);
            bReset = new Btn(() => L.T("adj_reset"), Ico.Refresh, BtnKind.Ghost) { FontSize = 9f, Surface = Surface.Card };
            bReset.Click += (s, e) => { foreach (Slider sl in sliders) sl.SetValue(0, false); Push(); };
            bDone = new Btn(() => L.T("lm_done"), Ico.Check, BtnKind.Primary) { FontSize = 9f, Surface = Surface.Card };
            bDone.Click += (s, e) => Dismiss(false);
            int dw = Math.Max(Theme.S(96), bDone.PreferredWidth + Theme.S(12)), rw = bReset.PreferredWidth + Theme.S(8);
            bDone.Bounds = Theme.Mirror(new Rectangle(W - pad - dw, by, dw, Theme.S(36)), W);
            bReset.Bounds = Theme.Mirror(new Rectangle(pad - Theme.S(8), by + Theme.S(2), rw, Theme.S(32)), W);
            Controls.Add(bReset); Controls.Add(bDone);
            ClientSize = new Size(W, by + Theme.S(36) + pad);
            Deactivate += (s, e) => Dismiss(false);
            Movable.Make(this, () => Theme.S(46));
        }

        void Push()
        {
            Adjust a = new Adjust { Bright = sliders[0].Value, Contrast = sliders[1].Value, Sat = sliders[2].Value, Warmth = sliders[3].Value, Filter = before.Filter };
            bReset.Enabled = !a.None;
            apply(a);
        }

        /// Shrinks back into the spot it came from. cancel = put the photo back as it was.
        void Dismiss(bool cancel)
        {
            if (closing || IsDisposed) return;
            closing = true;
            if (cancel) apply(before);
            Fx.PopOut(this, owner, from, Theme.S(14), 110, () => { if (!IsDisposed) Close(); });
        }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Dismiss(true); return true; }
            if (k == Keys.Enter) { Dismiss(false); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); bReset.Enabled = !before.None; sliders[0].Focus(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            int W = ClientSize.Width, pad = Theme.S(18), rowH = Theme.S(44), top = Theme.S(54);
            Rectangle ic = Theme.Mirror(new Rectangle(pad, Theme.S(14), Theme.S(28), Theme.S(28)), W);
            Theme.FillRound(g, ic, Theme.S(8), Theme.AccentSoft);
            Theme.Glyph(g, "", 10f, Theme.AccentColor, ic);
            Theme.Text(g, L.T("lm_adjust_title"), Theme.F(10.5f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(pad + Theme.S(38), Theme.S(12), W - pad * 2 - Theme.S(38), Theme.S(32)), W), Align.Start);
            for (int i = 0; i < keys.Length; i++)
                Theme.Text(g, L.T(keys[i]), Theme.F(9f), Theme.InkSoft, Theme.Mirror(new Rectangle(pad, top + i * rowH, Theme.S(84), rowH), W), Align.Start);
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)))
            using (Pen pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, p);
        }
    }
}
