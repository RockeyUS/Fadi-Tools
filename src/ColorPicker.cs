using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// App-styled color picker: saturation/brightness square, hue bar, hex field, recent colors.
    public static class ColorPicker
    {
        /// Shows the picker. live (optional) is called while the color changes; returns false when cancelled.
        public static bool Pick(IWin32Window owner, Color initial, Action<Color> live, out Color result)
        {
            result = initial;
            Form ownerForm = owner as Form;
            if (ownerForm == null && owner is Control) ownerForm = ((Control)owner).FindForm();
            if (ownerForm != null && ownerForm.Owner != null && ownerForm is PopupList) ownerForm = ownerForm.Owner;
            Form dim = null;
            try
            {
                if (ownerForm != null && ownerForm.Visible && ownerForm.WindowState != FormWindowState.Minimized)
                {
                    dim = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                        BackColor = Color.Black, Opacity = Theme.Dark ? 0.35 : 0.22, Bounds = ownerForm.RectangleToScreen(ownerForm.ClientRectangle)
                    };
                    dim.Show(ownerForm);
                }
                using (PickerForm f = new PickerForm(initial, live))
                {
                    IWin32Window o = (IWin32Window)dim ?? ownerForm;
                    f.StartPosition = o != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                    bool ok = (o != null ? f.ShowDialog(o) : f.ShowDialog()) == DialogResult.OK;
                    if (ok) { result = f.Value; Remember(f.Value); }
                    else if (live != null) live(initial);
                    return ok;
                }
            }
            finally
            {
                if (dim != null) { dim.Close(); dim.Dispose(); }
                if (ownerForm != null) try { ownerForm.Activate(); } catch { }
            }
        }

        public static List<Color> Recent()
        {
            List<Color> l = new List<Color>();
            foreach (string s in Settings.Get("RecentColors", "").Split(','))
            {
                Color c;
                if (TryHex(s, out c)) l.Add(c);
            }
            return l;
        }

        static void Remember(Color c)
        {
            List<Color> l = Recent();
            l.RemoveAll(x => x.ToArgb() == c.ToArgb());
            l.Insert(0, c);
            if (l.Count > 9) l.RemoveRange(9, l.Count - 9);
            List<string> s = new List<string>();
            foreach (Color x in l) s.Add(Hex(x));
            Settings.Set("RecentColors", string.Join(",", s.ToArray()));
        }

        public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        public static bool TryHex(string s, out Color c)
        {
            c = Color.Empty;
            s = (s ?? "").Trim().TrimStart('#');
            if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
            int v;
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return false;
            c = Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
            return true;
        }

        public static void ToHsv(Color c, out float h, out float s, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }
            if (h < 0) h += 360;
            s = max <= 0 ? 0 : d / max;
            v = max;
        }

        public static Color FromHsv(float h, float s, float v)
        {
            h = ((h % 360) + 360) % 360;
            float c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            float r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb(255, (int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }
    }

    class PickerForm : Form
    {
        public Color Value { get { return ColorPicker.FromHsv(h, s, v); } }
        readonly Color initial;
        readonly Action<Color> live;
        float h, s, v;
        readonly PickerSurface surface;
        readonly TextField hex;
        readonly Btn bOk, bCancel;
        bool settingHex;
        DateTime lastLive = DateTime.MinValue;
        readonly Timer liveTimer = new Timer { Interval = 90 };

        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public PickerForm(Color start, Action<Color> live)
        {
            initial = start.IsEmpty ? Color.White : Color.FromArgb(255, start);
            this.live = live;
            ColorPicker.ToHsv(initial, out h, out s, out v);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Theme.Card;
            Font = Theme.F(10f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = L.T("cp_title");
            int W = Theme.S(372), pad = Theme.S(22);
            surface = new PickerSurface(this) { Surface = Surface.Card };
            surface.Bounds = new Rectangle(pad, pad + Theme.S(36), W - pad * 2, Theme.S(ColorPicker.Recent().Count > 0 ? 298 : 242));
            hex = new TextField(() => "#RRGGBB", null) { ForceLtr = true, Surface = Surface.Card };
            hex.Bounds = new Rectangle(pad + Theme.S(96), surface.Bottom + Theme.S(12), W - pad * 2 - Theme.S(96), Theme.S(40));
            hex.Box.TextChanged += (s2, e) =>
            {
                if (settingHex) return;
                Color c;
                if (ColorPicker.TryHex(hex.Box.Text, out c)) { ColorPicker.ToHsv(c, out h, out this.s, out v); surface.Invalidate(); Invalidate(); Changed(false); }
            };
            hex.Box.KeyDown += (s2, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DialogResult = DialogResult.OK; Close(); } };
            int by = hex.Bottom + Theme.S(20), bh = Theme.S(40);
            ClientSize = new Size(W, by + bh + pad);
            bOk = new Btn(() => L.T("cp_use"), Ico.Check, BtnKind.Primary) { Surface = Surface.Card };
            bOk.Click += (s2, e) => { DialogResult = DialogResult.OK; Close(); };
            bCancel = new Btn(() => L.T("cancel"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s2, e) => { DialogResult = DialogResult.Cancel; Close(); };
            int bw = (W - pad * 2 - Theme.S(10)) / 2;
            bCancel.Bounds = Theme.Mirror(new Rectangle(pad, by, bw, bh), W);
            bOk.Bounds = Theme.Mirror(new Rectangle(pad + bw + Theme.S(10), by, W - pad * 2 - bw - Theme.S(10), bh), W);
            hex.Bounds = Theme.Mirror(hex.Bounds, W);
            Controls.AddRange(new Control[] { surface, hex, bOk, bCancel });
            SetHex();
            liveTimer.Tick += (s2, e) => { liveTimer.Stop(); if (live != null) live(Value); };
            Shown += (s2, e) => { hex.Restyle(); surface.Focus(); };
        }

        public float H { get { return h; } }
        public float S2 { get { return s; } }
        public float V { get { return v; } }
        public Color Initial { get { return initial; } }

        public void Set(float nh, float ns, float nv)
        {
            h = nh; s = Math.Max(0, Math.Min(1, ns)); v = Math.Max(0, Math.Min(1, nv));
            SetHex();
            surface.Invalidate(); Invalidate();
            Changed(false);
        }

        public void SetColor(Color c) { ColorPicker.ToHsv(c, out h, out s, out v); SetHex(); surface.Invalidate(); Invalidate(); Changed(true); }

        void SetHex() { settingHex = true; hex.Box.Text = ColorPicker.Hex(Value); settingHex = false; }

        void Changed(bool now)
        {
            if (live == null) return;
            if (now) { liveTimer.Stop(); live(Value); return; }
            if (!liveTimer.Enabled) liveTimer.Start();   // at most ~11 updates a second while dragging
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }
        protected override void Dispose(bool disposing) { if (disposing) liveTimer.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            using (LinearGradientBrush b = new LinearGradientBrush(ClientRectangle, Theme.Card, Theme.Mix(Theme.Card, Theme.BgBottom, 0.35f), LinearGradientMode.Vertical))
                g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.CardBorder)) g.DrawPath(p, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));
            int pad = Theme.S(22);
            Theme.Text(g, L.T("cp_title"), Theme.F(12.5f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(pad, pad - Theme.S(2), Width - pad * 2, Theme.S(28)), Width), Align.Start);
            // before | after
            Rectangle pr = Theme.Mirror(new Rectangle(pad, hex.Top, Theme.S(84), hex.Height), Width);
            using (GraphicsPath gp = Theme.Round(pr, Theme.S(10)))
            {
                GraphicsState st = g.Save();
                g.SetClip(gp);
                using (SolidBrush a = new SolidBrush(initial)) g.FillRectangle(a, pr.X, pr.Y, pr.Width / 2, pr.Height);
                using (SolidBrush b = new SolidBrush(Value)) g.FillRectangle(b, pr.X + pr.Width / 2, pr.Y, pr.Width - pr.Width / 2, pr.Height);
                g.Restore(st);
                using (Pen p = new Pen(Theme.CardBorder)) g.DrawPath(p, gp);
            }
        }
    }

    /// The interactive part of the picker: SV square, hue bar, quick and recent swatches.
    class PickerSurface : UiControl
    {
        readonly PickerForm f;
        int drag;   // 1 square, 2 hue
        static readonly Color[] Quick = {
            Color.White, Color.FromArgb(156, 163, 175), Color.Black, Color.FromArgb(239, 68, 68), Color.FromArgb(249, 115, 22), Color.FromArgb(250, 204, 21),
            Color.FromArgb(34, 197, 94), Color.FromArgb(20, 184, 166), Color.FromArgb(14, 165, 233), Color.FromArgb(59, 130, 246), Color.FromArgb(168, 85, 247), Color.FromArgb(236, 72, 153) };
        readonly List<Color> recent;
        Bitmap sq; float sqHue = -1;
        int hoverSw = -1;

        public PickerSurface(PickerForm owner) { f = owner; Cursor = Cursors.Cross; DoubleBuffered = true; recent = ColorPicker.Recent(); }

        Rectangle Square { get { return new Rectangle(0, 0, Width, Theme.S(170)); } }
        Rectangle Hue { get { return new Rectangle(0, Theme.S(184), Width, Theme.S(16)); } }
        Rectangle Sw(int i)
        {
            int n = Quick.Length, s = Theme.S(22), gap = (Width - n * s) / (n - 1);
            int row = i / n, col = i % n;
            return Theme.Mirror(new Rectangle(col * (s + gap), Theme.S(214) + row * Theme.S(54), s, s), Width);
        }
        int SwCount { get { return Quick.Length + recent.Count; } }
        Color SwColor(int i) { return i < Quick.Length ? Quick[i] : recent[i - Quick.Length]; }

        void Rebuild()
        {
            Rectangle r = Square;
            if (sq != null && sqHue == f.H && sq.Width == r.Width && sq.Height == r.Height) return;
            if (sq != null) sq.Dispose();
            sq = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height));
            sqHue = f.H;
            using (Graphics g = Graphics.FromImage(sq))
            {
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, sq.Width, sq.Height), Color.White, ColorPicker.FromHsv(f.H, 1, 1), LinearGradientMode.Horizontal))
                    g.FillRectangle(b, 0, 0, sq.Width, sq.Height);
                using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, sq.Width, sq.Height), Color.FromArgb(0, 0, 0, 0), Color.Black, LinearGradientMode.Vertical))
                    g.FillRectangle(b, 0, 0, sq.Width, sq.Height + 1);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (Square.Contains(e.Location)) { drag = 1; Capture = true; Move(e.Location); }
            else if (Rectangle.Inflate(Hue, 0, Theme.S(6)).Contains(e.Location)) { drag = 2; Capture = true; Move(e.Location); }
            else for (int i = 0; i < SwCount; i++) if (Sw(i).Contains(e.Location)) { f.SetColor(SwColor(i)); return; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag != 0) { Move(e.Location); return; }
            int hs = -1; for (int i = 0; i < SwCount; i++) if (Sw(i).Contains(e.Location)) hs = i;
            if (hs != hoverSw) { hoverSw = hs; Invalidate(); }
            Cursor = hs >= 0 ? Cursors.Hand : Square.Contains(e.Location) || Rectangle.Inflate(Hue, 0, Theme.S(6)).Contains(e.Location) ? Cursors.Cross : Cursors.Default;
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = 0; Capture = false; }
        protected override void OnMouseLeave(EventArgs e) { hoverSw = -1; base.OnMouseLeave(e); }

        void Move(Point p)
        {
            if (drag == 1)
            {
                Rectangle r = Square;
                f.Set(f.H, (p.X - r.X) / (float)Math.Max(1, r.Width - 1), 1 - (p.Y - r.Y) / (float)Math.Max(1, r.Height - 1));
            }
            else if (drag == 2)
            {
                Rectangle r = Hue;
                float t = Math.Max(0, Math.Min(1, (p.X - r.X) / (float)Math.Max(1, r.Width)));
                f.Set(t * 359.9f, f.S2, f.V);
            }
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            float st = e.Shift ? 0.1f : 0.02f;
            if (e.KeyCode == Keys.Left) f.Set(f.H, f.S2 - st, f.V);
            else if (e.KeyCode == Keys.Right) f.Set(f.H, f.S2 + st, f.V);
            else if (e.KeyCode == Keys.Up) f.Set(f.H, f.S2, f.V + st);
            else if (e.KeyCode == Keys.Down) f.Set(f.H, f.S2, f.V - st);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Rebuild();
            Rectangle r = Square;
            using (GraphicsPath gp = Theme.Round(r, Theme.S(12)))
            {
                GraphicsState st = g.Save();
                g.SetClip(gp);
                g.DrawImageUnscaled(sq, r.X, r.Y);
                g.Restore(st);
                using (Pen p = new Pen(Color.FromArgb(40, 0, 0, 0))) g.DrawPath(p, gp);
            }
            // marker
            float mx = r.X + f.S2 * (r.Width - 1), my = r.Y + (1 - f.V) * (r.Height - 1);
            float ms = Theme.S(16);
            using (SolidBrush b = new SolidBrush(f.Value)) g.FillEllipse(b, mx - ms / 2, my - ms / 2, ms, ms);
            using (Pen p = new Pen(Color.White, Theme.S(2.5f))) g.DrawEllipse(p, mx - ms / 2, my - ms / 2, ms, ms);
            using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0), 1f)) g.DrawEllipse(p, mx - ms / 2 - 1.5f, my - ms / 2 - 1.5f, ms + 3, ms + 3);

            // hue bar
            Rectangle hr = Hue;
            using (GraphicsPath gp = Theme.Round(hr, hr.Height / 2f))
            using (LinearGradientBrush lb = new LinearGradientBrush(hr, Color.Red, Color.Red, LinearGradientMode.Horizontal))
            {
                ColorBlend cb = new ColorBlend();
                cb.Colors = new Color[7]; cb.Positions = new float[7];
                for (int i = 0; i < 7; i++) { cb.Colors[i] = ColorPicker.FromHsv(i * 60, 1, 1); cb.Positions[i] = i / 6f; }
                lb.InterpolationColors = cb;
                g.FillPath(lb, gp);
            }
            float hx = hr.X + f.H / 360f * hr.Width;
            float hs2 = Theme.S(20);
            using (SolidBrush b = new SolidBrush(ColorPicker.FromHsv(f.H, 1, 1))) g.FillEllipse(b, hx - hs2 / 2, hr.Y + hr.Height / 2f - hs2 / 2, hs2, hs2);
            using (Pen p = new Pen(Color.White, Theme.S(2.5f))) g.DrawEllipse(p, hx - hs2 / 2, hr.Y + hr.Height / 2f - hs2 / 2, hs2, hs2);
            using (Pen p = new Pen(Color.FromArgb(90, 0, 0, 0), 1f)) g.DrawEllipse(p, hx - hs2 / 2 - 1.5f, hr.Y + hr.Height / 2f - hs2 / 2 - 1.5f, hs2 + 3, hs2 + 3);

            // swatches: quick colors, then recently used
            if (recent.Count > 0)
                Theme.Text(g, L.T("cp_recent"), Theme.F(8f, FontStyle.Bold), Theme.InkFaint, Theme.Mirror(new Rectangle(0, Theme.S(246), Width, Theme.S(18)), Width), Align.Start);
            int cur = f.Value.ToArgb();
            for (int i = 0; i < SwCount; i++)
            {
                Rectangle c = Sw(i);
                Color col = SwColor(i);
                if (col.ToArgb() == cur) using (Pen p = new Pen(Theme.Ink, Theme.S(2))) g.DrawEllipse(p, Rectangle.Inflate(c, Theme.S(3), Theme.S(3)));
                else if (i == hoverSw) using (Pen p = new Pen(Color.FromArgb(110, Theme.Ink), 1.5f)) g.DrawEllipse(p, Rectangle.Inflate(c, Theme.S(2), Theme.S(2)));
                using (SolidBrush b = new SolidBrush(col)) g.FillEllipse(b, c);
                using (Pen p = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawEllipse(p, c);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing && sq != null) sq.Dispose(); base.Dispose(disposing); }
    }
}
