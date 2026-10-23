using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// The first few times a file is dragged in File Explorer, a small note rides along with the mouse:
    /// "Hold Shift for the Donut Wheel". It never takes the focus and lets the mouse through; once the wheel has
    /// been opened (or after three times) it doesn't come back.
    sealed class DragHint : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; public PT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; public SZ(int w, int h) { W = w; H = h; } }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst, ref SZ psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public const int MaxTimes = 3;
        /// Whether the note should still be shown.
        public static bool Wanted { get { int n; int.TryParse(Settings.Get("WheelHintShown", "0"), out n); return n < MaxTimes; } }
        /// The wheel was opened: the note has done its job.
        public static void Learned() { Settings.Set("WheelHintShown", MaxTimes.ToString()); }

        Bitmap pic;
        readonly Timer anim = new Timer { Interval = 15 };
        float a; bool leaving;
        float px, py, tx, ty;           // where it is, and where it's going
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        double lastTick;
        static DragHint open;

        public static void ShowAt(Point mouse)
        {
            if (open != null && !open.IsDisposed) return;
            int n; int.TryParse(Settings.Get("WheelHintShown", "0"), out n);
            Settings.Set("WheelHintShown", (n + 1).ToString());
            try { open = new DragHint(); open.Follow(mouse); open.Show(); open.anim.Start(); } catch { open = null; }
        }

        public static void Follow2(Point mouse) { }   // it follows the mouse by itself, on its own clock
        public static void HideNow() { if (open != null && !open.IsDisposed) { open.leaving = true; open.anim.Start(); } open = null; }

        DragHint()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
            pic = Draw();
            Size = pic.Size;
            // its own steady clock (not the drag check's), and it glides after the mouse instead of jumping
            anim.Tick += (s, e) =>
            {
                float k = Math.Max(0.5f, Math.Min(3f, (float)(clock.Elapsed.TotalMilliseconds - lastTick) / 15f));
                lastTick = clock.Elapsed.TotalMilliseconds;
                a = Math.Max(0, Math.Min(1, a + (leaving ? -1 : 1) * 0.12f * k));
                if (a <= 0 && leaving) { anim.Stop(); Close(); return; }
                if (!leaving) Target(Cursor.Position);
                float f = 1 - (float)Math.Pow(1 - 0.38, k);
                px += (tx - px) * f; py += (ty - py) * f;
                Location = new Point((int)Math.Round(px), (int)Math.Round(py));
                Present();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x80 | 0x08000000 | 0x8 | 0x20;   // layered, no taskbar button, no focus, on top, the mouse goes through
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Present(); }

        /// Below and to the side of the pointer (the dragged file sits right on it).
        void Target(Point m)
        {
            Rectangle wa = Screen.FromPoint(m).WorkingArea;
            int x = Theme.RTL ? m.X - Width - Theme.S(22) : m.X + Theme.S(22), y = m.Y + Theme.S(26);
            if (x + Width > wa.Right) x = m.X - Width - Theme.S(22);
            if (x < wa.Left) x = m.X + Theme.S(22);
            if (y + Height > wa.Bottom) y = m.Y - Height - Theme.S(18);
            tx = x; ty = y;
        }

        void Follow(Point m)
        {
            bool first = px == 0 && py == 0;
            Target(m);
            if (first) { px = tx; py = ty; Location = new Point((int)tx, (int)ty); if (IsHandleCreated) Present(); }
        }

        Bitmap Draw()
        {
            string text = L.T("wh_hint_drag");
            Font f = Theme.F(9.5f, FontStyle.Bold);
            int tw;
            using (Bitmap probe = new Bitmap(1, 1)) using (Graphics pg = Graphics.FromImage(probe)) tw = (int)Math.Ceiling(pg.MeasureString(text, f).Width);
            int h = Theme.S(40), pad = Theme.S(12), donut = Theme.S(24), key = Theme.S(48);
            int w = pad + donut + Theme.S(10) + key + Theme.S(8) + tw + pad, m = Theme.S(8);
            Bitmap b = new Bitmap(w + m * 2, h + m * 2, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int i = 6; i >= 1; i--)
                    using (GraphicsPath sp = Theme.Round(new RectangleF(m - i / 2f, m - i / 2f + 2, w + i, h + i), h / 2f + i / 2f))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(9, 0, 0, 0))) g.FillPath(sb, sp);
                Color bg = Theme.Dark ? Color.FromArgb(250, 36, 35, 33) : Color.FromArgb(250, 255, 255, 255);
                using (GraphicsPath p = Theme.Round(new RectangleF(m, m, w, h), h / 2f))
                {
                    using (SolidBrush sb = new SolidBrush(bg)) g.FillPath(sb, p);
                    using (Pen pn = new Pen(Theme.Dark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0))) g.DrawPath(pn, p);
                }
                int x = m + pad;
                bool rtl = Theme.RTL;
                Func<Rectangle, Rectangle> M = r => rtl ? new Rectangle(b.Width - r.Right, r.Y, r.Width, r.Height) : r;
                WheelEntry.DrawMini(g, M(new Rectangle(x, m + (h - donut) / 2, donut, donut)), 0);
                x += donut + Theme.S(10);
                Rectangle k = M(new Rectangle(x, m + (h - Theme.S(24)) / 2, key, Theme.S(24)));
                Theme.FillRound(g, k, Theme.S(6), Theme.AccentFill);
                Theme.Text(g, "Shift", Theme.F(8.5f, FontStyle.Bold), Theme.PrimaryInk, k, Align.Center);
                x += key + Theme.S(8);
                Theme.Text(g, text, f, Theme.Dark ? Color.FromArgb(235, 232, 226) : Color.FromArgb(40, 36, 32), M(new Rectangle(x, m, tw + Theme.S(4), h)), Align.Start);
            }
            return b;
        }

        void Present()
        {
            if (pic == null || !IsHandleCreated) return;
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hb = pic.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hb);
            try
            {
                PT dst = new PT(Left, Top), src = new PT(0, 0);
                SZ sz = new SZ(pic.Width, pic.Height);
                float e = a * a * (3 - 2 * a);
                BLEND bl = new BLEND { Op = 0, Flags = 0, Alpha = (byte)(255 * e), Format = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref bl, 2);
            }
            finally { SelectObject(mem, old); DeleteObject(hb); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); }
        }

        protected override void Dispose(bool disposing) { anim.Stop(); anim.Dispose(); if (pic != null) { pic.Dispose(); pic = null; } base.Dispose(disposing); }
    }
}
