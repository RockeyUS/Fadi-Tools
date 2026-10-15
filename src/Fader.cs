using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace FileConverter
{
    /// Smooth page change: the old page fades out while the new one fades in and settles into place.
    public class PageFader : Control, IAnimated
    {
        Bitmap from, to;
        float t = 1;
        int dir = 1;
        const float Duration = 13;   // frames (~200 ms)
        float frame;

        public PageFader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            Visible = false;
        }

        public static bool Enabled2 = Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.GetEnvironmentVariable("FADI_NO_ANIM") == null;

        /// Takes a picture of a control as it looks now (null if that isn't possible).
        public static Bitmap Snap(Control c)
        {
            if (!Enabled2 || c.Width <= 0 || c.Height <= 0) return null;
            try
            {
                Bitmap b = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppPArgb);
                c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height));
                return b;
            }
            catch { return null; }
        }

        public void Run(Bitmap a, Bitmap b, int direction)
        {
            Stop();
            if (a == null || b == null) { if (a != null) a.Dispose(); if (b != null) b.Dispose(); return; }
            from = a; to = b; dir = direction; frame = 0; t = 0;
            Visible = true; BringToFront();
            Invalidate(); Update();
            Animator.Start(this);
        }

        void Stop()
        {
            if (from != null) { from.Dispose(); from = null; }
            if (to != null) { to.Dispose(); to = null; }
            Visible = false;
        }

        public bool Step()
        {
            if (to == null) return false;
            frame++;
            t = Math.Min(1, frame / Duration);
            if (t >= 1) { Stop(); return false; }
            Invalidate();
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (from == null || to == null) { g.Clear(Theme.Bg); return; }
            float ease = 1 - (float)Math.Pow(1 - t, 3);
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(from, 0, 0);
            float shift = (1 - ease) * Theme.S(14) * dir;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix { Matrix33 = ease };
                ia.SetColorMatrix(cm);
                g.DrawImage(to, new Rectangle(0, (int)shift, to.Width, to.Height), 0, 0, to.Width, to.Height, GraphicsUnit.Pixel, ia);
            }
        }
    }
}
