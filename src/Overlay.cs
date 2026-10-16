using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// A see-through window floating just above the app. Windows blends it on the graphics card, so fades,
    /// pops and fly-outs drawn on it stay smooth even when the app fills a big screen (changing how see-through
    /// it is, or moving it, costs next to nothing; only redrawing what's on it takes time).
    public sealed class Overlay : Form
    {
        [StructLayout(LayoutKind.Sequential)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; public PT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; public SZ(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential)]
        struct BMIH { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst, ref SZ psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int dwFlags);
        [DllImport("user32.dll", EntryPoint = "UpdateLayeredWindow")] static extern bool UpdateLayeredWindowLight(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BLEND pblend, int dwFlags);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BMIH bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

        IntPtr dc, dib, oldBmp;
        Bitmap canvas;
        readonly int w, h;
        byte alpha = 255;
        /// Where it is on the screen (top-left).
        public Point At;
        /// A picture it keeps until it closes (e.g. the look before a change).
        public Bitmap Picture;
        /// Left out of pictures of the screen (pop-ups and fly-outs; a fading page picture is kept in).
        public bool Ghost;
        public int W { get { return w; } }
        public int H { get { return h; } }

        public Overlay(Rectangle screen)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = Size.Empty;
            w = Math.Max(1, screen.Width); h = Math.Max(1, screen.Height);
            At = screen.Location;
            Bounds = new Rectangle(At, new Size(w, h));
            BMIH bi = new BMIH { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr bits;
            dib = CreateDIBSection(dc, ref bi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) { DeleteDC(dc); dc = IntPtr.Zero; throw new OutOfMemoryException(); }
            oldBmp = SelectObject(dc, dib);
            canvas = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000;   // layered, click-through, no taskbar button, never takes the focus
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; }   // the mouse goes straight through
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }      // WM_MOUSEACTIVATE: never activate
            base.WndProc(ref m);
        }

        /// A cleared (see-through) canvas to draw the next frame on; call Present() when done.
        public Graphics Draw()
        {
            Graphics g = Graphics.FromImage(canvas);
            g.Clear(Color.Transparent);
            return g;
        }

        /// Puts what was drawn on the screen (a = how solid: 0 see-through … 255 solid).
        public void Present(byte a) { alpha = a; Present(); }

        /// Puts what was drawn on the screen.
        public void Present()
        {
            if (IsDisposed || dc == IntPtr.Zero) return;
            PT dst = new PT(At.X, At.Y), src = new PT(0, 0);
            SZ sz = new SZ(w, h);
            BLEND b = new BLEND { Op = 0, Flags = 0, Alpha = alpha, Format = 1 };
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref sz, dc, ref src, 0, ref b, 2);
        }

        /// Moves it and/or changes how see-through it is, without redrawing (cheap: done by the graphics card).
        public void MoveTo(Point at, byte a)
        {
            if (IsDisposed) return;
            At = at; alpha = a;
            PT dst = new PT(At.X, At.Y);
            BLEND b = new BLEND { Op = 0, Flags = 0, Alpha = alpha, Format = 1 };
            UpdateLayeredWindowLight(Handle, IntPtr.Zero, ref dst, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref b, 2);
        }

        public byte Alpha { get { return alpha; } set { MoveTo(At, value); } }

        /// Shows it above the app's window (after the first Present, so it never flashes empty).
        public void ShowOn(Form owner)
        {
            Present();
            Show(owner);
        }

        protected override void Dispose(bool disposing)
        {
            if (canvas != null) { canvas.Dispose(); canvas = null; }
            if (dc != IntPtr.Zero) { SelectObject(dc, oldBmp); DeleteObject(dib); DeleteDC(dc); dc = IntPtr.Zero; }
            if (Picture != null) { Picture.Dispose(); Picture = null; }
            base.Dispose(disposing);
        }
    }

    public static partial class Fx
    {
        /// Overlay animations run on Windows only, and follow Settings › Animations.
        public static bool Overlays { get { return PageFader.Enabled2; } }

        static float Smooth01(float t) { t = Math.Max(0, Math.Min(1, t)); return t * t * (3 - 2 * t); }
        static float OutCubic(float t) { t = Math.Max(0, Math.Min(1, t)); return 1 - (1 - t) * (1 - t) * (1 - t); }
        static float OutQuint(float t) { t = Math.Max(0, Math.Min(1, t)); float u = 1 - t; return 1 - u * u * u * u * u; }
        static float OutBack(float t, float k) { t = Math.Max(0, Math.Min(1, t)); float u = t - 1; return 1 + (k + 1) * u * u * u + k * u * u; }

        /// A picture of part of a control (area in its own coordinates) as it is on the screen right now.
        /// Copied straight off the screen when the window is in front (fast, even full screen); drawn otherwise.
        public static Bitmap Grab(Control c, Rectangle area)
        {
            if (area.Width <= 0 || area.Height <= 0) return null;
            Rectangle scr = c.RectangleToScreen(area);
            Form f = c as Form ?? c.FindForm();
            bool direct = f != null && Form.ActiveForm == f && Screen.FromRectangle(scr).Bounds.Contains(scr);
            // another window of ours on top of it (a pop-up, a dialog) would end up in the picture: draw it instead
            if (direct)
                foreach (Form o in Application.OpenForms)
                {
                    if (o == f || !o.Visible || o.IsDisposed) continue;
                    Overlay ov = o as Overlay;
                    if (ov != null && !ov.Ghost) continue;
                    Rectangle ob = ov != null ? new Rectangle(ov.At, new Size(ov.W, ov.H)) : o.Bounds;
                    if (ob.IntersectsWith(scr)) { direct = false; break; }
                }
            Bitmap b = null;
            try
            {
                b = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
                if (direct)
                {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(scr.Location, Point.Empty, scr.Size, CopyPixelOperation.SourceCopy);
                    return b;
                }
                using (Bitmap full = c is Form ? SnapWindow(c) : PageFader.Snap(c))
                {
                    if (full == null) { b.Dispose(); return null; }
                    using (Graphics g = Graphics.FromImage(b)) g.DrawImage(full, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
                }
                return b;
            }
            catch { if (b != null) b.Dispose(); return null; }
        }

        /// Covers part of a control with a still picture of how it looks now, until the overlay is faded or closed.
        /// Use it just before changing what the control shows.
        public static Overlay Hold(Control c, Rectangle area)
        {
            if (!Overlays || c == null || c.IsDisposed || !c.Visible || !c.IsHandleCreated) return null;
            Form f = c as Form ?? c.FindForm();
            if (f == null || !f.Visible || f.WindowState == FormWindowState.Minimized) return null;
            if (Form.ActiveForm == null) return null;   // another program is in front: no picture floating over it
            area.Intersect(c.ClientRectangle);
            if (area.Width < 4 || area.Height < 4) return null;
            Bitmap pic = Grab(c, area);
            if (pic == null) return null;
            Overlay ov = null;
            try
            {
                ov = new Overlay(c.RectangleToScreen(area)) { Picture = pic };
                using (Graphics g = ov.Draw()) g.DrawImageUnscaled(pic, 0, 0);
                ov.ShowOn(f);
                return ov;
            }
            catch { if (ov != null) ov.Dispose(); else pic.Dispose(); return null; }
        }

        /// Fades a held picture away while it drifts by (dx, dy), so the new look underneath shows through.
        public static void FadeAway(Overlay ov, int ms, int dx, int dy)
        {
            if (ov == null) return;
            Point p0 = ov.At;
            TweenLinear(ms, t =>
            {
                if (ov.IsDisposed) return;
                float m = OutCubic(t);
                ov.MoveTo(new Point(p0.X + (int)Math.Round(dx * m), p0.Y + (int)Math.Round(dy * m)), (byte)Math.Round(255 * (1 - Smooth01(t))));
            }, () => { if (!ov.IsDisposed) ov.Close(); });
        }

        /// The new look grows as a circle from a point (screen coordinates) through the held picture of the old one.
        public static void RevealAway(Overlay ov, Point screenCenter, int ms)
        {
            if (ov == null) return;
            if (ov.Picture == null) { FadeAway(ov, ms, 0, 0); return; }
            Point at = ov.At;
            PointF c = new PointF(screenCenter.X - at.X, screenCenter.Y - at.Y);
            float far = 0;
            foreach (PointF k in new[] { new PointF(0, 0), new PointF(ov.W, 0), new PointF(0, ov.H), new PointF(ov.W, ov.H) })
                far = Math.Max(far, (float)Math.Sqrt((k.X - c.X) * (k.X - c.X) + (k.Y - c.Y) * (k.Y - c.Y)));
            TweenLinear(ms, t =>
            {
                if (ov.IsDisposed) return;
                float e = t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
                float r = Math.Max(1, e * (far + 2));
                using (Graphics g = ov.Draw())
                using (GraphicsPath path = new GraphicsPath(FillMode.Alternate))
                using (TextureBrush tb = new TextureBrush(ov.Picture, WrapMode.Clamp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.CompositingQuality = CompositingQuality.HighSpeed;
                    path.AddRectangle(new Rectangle(0, 0, ov.W, ov.H));
                    path.AddEllipse(c.X - r, c.Y - r, r * 2, r * 2);
                    g.FillPath(tb, path);
                }
                ov.Present();
            }, () => { if (!ov.IsDisposed) ov.Close(); });
        }

        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);
        /// Paints a window and everything in it right now (so it never shows up blank for a frame).
        public static void PaintNow(Control c)
        {
            try { if (c.IsHandleCreated) RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x80 | 0x100); } catch { }   // invalidate, erase, all children, update now
        }

        /// Closes an overlay a couple of frames from now (after what's under it has been drawn).
        static void CloseSoon(Overlay ov)
        {
            if (ov == null) return;
            TweenLinear(40, t => { }, () => { if (!ov.IsDisposed) ov.Close(); });
        }

        /// A picture of a window (including the parts covered by its own children), for the pops.
        static Bitmap Picture(Form f)
        {
            try
            {
                if (!f.IsHandleCreated) { IntPtr h = f.Handle; }
                Bitmap b = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb);
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                return b;
            }
            catch { return null; }
        }

        /// Draws a window's picture with rounded corners and a soft shadow at the given place and size.
        static void DrawCard(Graphics g, Bitmap pic, RectangleF dst, float radius, float shadow)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            // the shadow: a few soft layers under the card
            for (int i = 4; i >= 1; i--)
            {
                float grow = i * shadow / 4f;
                RectangleF sr = new RectangleF(dst.X - grow, dst.Y - grow + shadow * 0.35f, dst.Width + grow * 2, dst.Height + grow * 2);
                using (GraphicsPath sp = Theme.Round(sr, radius + grow))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(Theme.Dark ? 18 : 10, 0, 0, 0))) g.FillPath(sb, sp);
            }
            using (GraphicsPath p = Theme.Round(dst, radius))
            using (TextureBrush tb = new TextureBrush(pic, WrapMode.Clamp))
            {
                tb.TranslateTransform(dst.X, dst.Y);
                tb.ScaleTransform(dst.Width / pic.Width, dst.Height / pic.Height);
                g.FillPath(tb, p);
            }
        }

        /// Opens a pop-up window (already sized and placed) by growing it out of a spot on the screen (e.g. the
        /// button that opened it): its picture scales up and fades in, then the real window takes its place.
        /// show() must show the window; it's called at once when animations are off.
        public static void PopIn(Form f, Form owner, Rectangle from, float radius, int ms, Action show)
        {
            if (!Overlays || owner == null || !owner.Visible) { show(); return; }
            Bitmap pic = Picture(f);
            if (pic == null) { show(); return; }
            Rectangle fin = f.Bounds;
            int m = Theme.S(28);
            Overlay ov;
            try { ov = new Overlay(Rectangle.Inflate(fin, m, m)) { Picture = pic, Ghost = true }; }
            catch { pic.Dispose(); show(); return; }
            // grows from the side nearest the spot it came from
            PointF anchor = new PointF(Math.Max(fin.Left, Math.Min(fin.Right, from.X + from.Width / 2f)), Math.Max(fin.Top, Math.Min(fin.Bottom, from.Y + from.Height / 2f)));
            PointF local = new PointF(anchor.X - fin.X, anchor.Y - fin.Y);
            Action<float> frame = t =>
            {
                if (ov.IsDisposed) return;
                float s = 0.86f + 0.14f * OutBack(t, 1.15f);
                float wv = fin.Width * s, hv = fin.Height * s;
                RectangleF dst = new RectangleF(m + local.X - local.X * s, m + local.Y - local.Y * s, wv, hv);
                using (Graphics g = ov.Draw()) DrawCard(g, pic, dst, radius * s, Theme.S(10) * Math.Min(1, t * 1.4f));
                ov.Present((byte)Math.Round(255 * Math.Min(1, t * 1.8f)));
            };
            frame(0);
            ov.Show(owner);
            TweenLinear(ms, frame, () =>
            {
                // the real window appears exactly where its picture ended (fully drawn), then the picture goes
                try { show(); PaintNow(f); } catch { }
                CloseSoon(ov);
            });
        }

        /// Closes a pop-up window by shrinking it towards a spot and fading it out; close() really closes it.
        public static void PopOut(Form f, Form owner, Rectangle to, float radius, int ms, Action close)
        {
            if (!Overlays || owner == null || !f.Visible) { close(); return; }
            Bitmap pic = Picture(f);
            if (pic == null) { close(); return; }
            Rectangle fin = f.Bounds;
            int m = Theme.S(28);
            Overlay ov;
            try { ov = new Overlay(Rectangle.Inflate(fin, m, m)) { Picture = pic, Ghost = true }; }
            catch { pic.Dispose(); close(); return; }
            PointF anchor = new PointF(Math.Max(fin.Left, Math.Min(fin.Right, to.X + to.Width / 2f)), Math.Max(fin.Top, Math.Min(fin.Bottom, to.Y + to.Height / 2f)));
            PointF local = new PointF(anchor.X - fin.X, anchor.Y - fin.Y);
            Action<float> frame = t =>
            {
                if (ov.IsDisposed) return;
                float s = 1 - 0.08f * t * t;
                RectangleF dst = new RectangleF(m + local.X - local.X * s, m + local.Y - local.Y * s, fin.Width * s, fin.Height * s);
                using (Graphics g = ov.Draw()) DrawCard(g, pic, dst, radius * s, Theme.S(10) * (1 - t));
                ov.Present((byte)Math.Round(255 * (1 - Smooth01(t))));
            };
            frame(0);
            ov.Show(owner);
            try { close(); } catch { }
            TweenLinear(ms, frame, () => { if (!ov.IsDisposed) ov.Close(); });
        }

        /// "Clear": the pieces (rows, pages; in the control's coordinates) are swept away one after another
        /// while the emptied control shows underneath. clear() empties it; called at once when animations are off.
        public static void SweepAway(Control c, IList<Rectangle> pieces, Action clear)
        {
            Bitmap pic = null;
            Form f = c == null ? null : c.FindForm();
            if (Overlays && c != null && c.Visible && c.IsHandleCreated && f != null && f.Visible && pieces != null && pieces.Count > 0)
                pic = Grab(c, c.ClientRectangle);
            clear();
            if (pic == null) return;
            List<Rectangle> ps = new List<Rectangle>();
            foreach (Rectangle r in pieces) { Rectangle k = Rectangle.Intersect(r, c.ClientRectangle); if (k.Width > 2 && k.Height > 2) ps.Add(k); }
            if (ps.Count == 0) { pic.Dispose(); return; }
            Overlay ov;
            try { ov = new Overlay(c.RectangleToScreen(c.ClientRectangle)) { Picture = pic, Ghost = true }; }
            catch { pic.Dispose(); return; }
            int n = ps.Count;
            float gap = Math.Min(0.08f, 0.45f / Math.Max(1, n - 1));   // each piece leaves a moment after the one before
            float life = 1 - gap * (n - 1);
            float dist = Theme.S(70) * (Theme.RTL ? -1 : 1);
            ImageAttributes ia = new ImageAttributes();
            Action<float> frame = t =>
            {
                if (ov.IsDisposed) return;
                using (Graphics g = ov.Draw())
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                    for (int i = 0; i < n; i++)
                    {
                        float p = Math.Max(0, Math.Min(1, (t - i * gap) / life));
                        if (p >= 1) continue;
                        Rectangle r = ps[i];
                        if (p <= 0) { g.DrawImage(pic, r, r, GraphicsUnit.Pixel); continue; }
                        float e = p * p;                         // speeds up as it goes
                        float s = 1 - 0.1f * e;                  // and shrinks a little
                        float a = 1 - Smooth01(p);
                        RectangleF dst = new RectangleF(r.X + dist * e + r.Width * (1 - s) / 2, r.Y + r.Height * (1 - s) / 2, r.Width * s, r.Height * s);
                        ia.SetColorMatrix(new ColorMatrix { Matrix33 = a });
                        g.DrawImage(pic, new[] { dst.Location, new PointF(dst.Right, dst.Y), new PointF(dst.X, dst.Bottom) }, r, GraphicsUnit.Pixel, ia);
                    }
                }
                ov.Present();
            };
            frame(0);
            ov.ShowOn(f);
            TweenLinear(300 + (int)(gap * (n - 1) * 300), frame, () => { ia.Dispose(); if (!ov.IsDisposed) ov.Close(); });
        }
    }
}
