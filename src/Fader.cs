using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace FileConverter
{
    /// Smooth change: the old picture fades out while the new one fades in (and settles into place, or grows
    /// out of a point as a circle — used for the light/dark switch).
    public class PageFader : Control, IAnimated
    {
        Bitmap from, to;
        float t = 1;
        int dir = 1;
        float frame;
        /// Length of the change in frames (one frame ≈ 15 ms).
        public float Frames = 13;
        /// Removes itself from the window when done (for one-off fades made by Fx).
        public bool OneShot;
        /// Set = the new look grows as a circle from this point instead of fading in.
        public Point? RevealFrom;

        public PageFader()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            Visible = false;
        }

        /// Picture-based fades work on Windows only (and can be turned off in Settings).
        public static bool Enabled2 { get { return Environment.OSVersion.Platform == PlatformID.Win32NT && Fx.Motion; } }

        static bool printing;   // while taking a picture, faders inside it draw nothing (they're not part of the look)

        /// Takes a picture of a control as it looks now (null if that isn't possible).
        public static Bitmap Snap(Control c)
        {
            if (!Enabled2 || c == null || c.Width <= 0 || c.Height <= 0 || !c.IsHandleCreated) return null;
            bool was = printing;
            try
            {
                printing = true;
                Bitmap b = new Bitmap(c.Width, c.Height, PixelFormat.Format32bppPArgb);
                c.DrawToBitmap(b, new Rectangle(0, 0, c.Width, c.Height));
                return b;
            }
            catch { return null; }
            finally { printing = was; }
        }

        /// Shows a still picture (the look before a change) until Run starts the fade.
        public void Hold(Bitmap a)
        {
            Clear();
            if (a == null) { Done(); return; }
            from = a; to = null; t = 0;
            Visible = true; BringToFront();
            Invalidate(); Update();
        }

        public void Run(Bitmap a, Bitmap b, int direction)
        {
            Bitmap oldA = from, oldB = to;
            from = null; to = null;
            if (oldA != null && oldA != a && oldA != b) oldA.Dispose();
            if (oldB != null && oldB != a && oldB != b) oldB.Dispose();
            if (a == null || b == null) { if (a != null) a.Dispose(); if (b != null) b.Dispose(); Done(); return; }
            from = a; to = b; dir = direction; frame = 0; t = 0;
            Visible = true; BringToFront();
            Invalidate(); Update();
            Animator.Start(this);
        }

        void Clear()
        {
            if (from != null) { from.Dispose(); from = null; }
            if (to != null) { to.Dispose(); to = null; }
        }

        void Done()
        {
            Clear();
            Visible = false;
            if (OneShot && Parent != null)
            {
                Control p = Parent;
                try { p.BeginInvoke((Action)(() => { p.Controls.Remove(this); Dispose(); })); } catch { }
            }
        }

        public bool Step()
        {
            if (to == null) return false;
            frame++;
            t = Math.Min(1, frame / Math.Max(1, Frames));
            if (t >= 1) { Done(); return false; }
            Invalidate();
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (printing) return;
            Graphics g = e.Graphics;
            if (from == null) { g.Clear(Theme.Bg); return; }
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImageUnscaled(from, 0, 0);
            if (to == null) return;
            if (RevealFrom.HasValue)
            {
                // a circle of the new look grows from the button until it covers everything
                float ease = t < 0.5f ? 4 * t * t * t : 1 - (float)Math.Pow(-2 * t + 2, 3) / 2;
                Point c = RevealFrom.Value;
                float far = 0;
                foreach (Point k in new[] { new Point(0, 0), new Point(Width, 0), new Point(0, Height), new Point(Width, Height) })
                    far = Math.Max(far, (float)Math.Sqrt((k.X - c.X) * (k.X - c.X) + (k.Y - c.Y) * (k.Y - c.Y)));
                float r = Math.Max(1, ease * (far + 2));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (TextureBrush tb = new TextureBrush(to, WrapMode.Clamp))
                    g.FillEllipse(tb, c.X - r, c.Y - r, r * 2, r * 2);
                return;
            }
            float ez = 1 - (float)Math.Pow(1 - t, 3);
            float shift = (1 - ez) * Theme.S(14) * dir;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix { Matrix33 = ez };
                ia.SetColorMatrix(cm);
                g.DrawImage(to, new Rectangle(0, (int)shift, to.Width, to.Height), 0, 0, to.Width, to.Height, GraphicsUnit.Pixel, ia);
            }
        }
    }

    /// Small one-line animations for any part of the app.
    public static class Fx
    {
        static readonly bool noAnim = Environment.GetEnvironmentVariable("FADI_NO_ANIM") != null;
        /// Movement on (Settings > Animations; off = everything jumps straight to where it ends).
        public static bool Motion { get { return !noAnim && Settings.Get("Animations", "1") != "0"; } }

        /// Moves a value a step closer to its target; true while it's still moving.
        public static bool Ease(ref float v, float target, float speed)
        {
            if (!Motion) { v = target; return false; }
            v += (target - v) * speed;
            if (Math.Abs(target - v) < 0.004f) { v = target; return false; }
            return true;
        }

        /// Cross-fades a control from how it looks now to how it looks after the current change
        /// (call it just before changing what the control shows).
        public static void Crossfade(Control c, int ms) { Crossfade(c, ms, 0); }

        public static void Crossfade(Control c, int ms, int dir)
        {
            if (!PageFader.Enabled2 || c == null || c.Parent == null || !c.Visible || !c.IsHandleCreated) return;
            Form f = c.FindForm();
            if (f == null || f.WindowState == FormWindowState.Minimized) return;
            Bitmap before = PageFader.Snap(c);
            if (before == null) return;
            Control parent = c.Parent;
            PageFader fd = new PageFader { Frames = Math.Max(4, ms / 15f), OneShot = true, Bounds = c.Bounds };
            parent.Controls.Add(fd);
            fd.Hold(before);
            try
            {
                parent.BeginInvoke((Action)(() =>
                {
                    if (fd.IsDisposed) return;
                    if (c.IsDisposed || !c.Visible) { fd.Run(null, null, 0); return; }
                    fd.Bounds = c.Bounds;
                    fd.Run(before, PageFader.Snap(c), dir);
                }));
            }
            catch { fd.Run(null, null, 0); }
        }

        /// Cross-fades one part of a control (area in its own coordinates), e.g. the content under a row of tabs.
        public static void Crossfade(Control host, Func<Rectangle> area, int ms)
        {
            if (area == null) { Crossfade(host, ms, 0); return; }
            if (!PageFader.Enabled2 || host == null || !host.Visible || !host.IsHandleCreated) return;
            Form f = host.FindForm();
            if (f == null || f.WindowState == FormWindowState.Minimized) return;
            Rectangle r = area();
            r.Intersect(host.ClientRectangle);
            if (r.Width < 4 || r.Height < 4) return;
            Bitmap before = Crop(PageFader.Snap(host), r);
            if (before == null) return;
            PageFader fd = new PageFader { Frames = Math.Max(4, ms / 15f), OneShot = true, Bounds = r };
            host.Controls.Add(fd);
            fd.Hold(before);
            try
            {
                host.BeginInvoke((Action)(() =>
                {
                    if (fd.IsDisposed) return;
                    if (host.IsDisposed || !host.Visible) { fd.Run(null, null, 0); return; }
                    fd.Run(before, Crop(PageFader.Snap(host), r), 0);
                }));
            }
            catch { fd.Run(null, null, 0); }
        }

        static Bitmap Crop(Bitmap full, Rectangle r)
        {
            if (full == null) return null;
            try
            {
                if (r.X == 0 && r.Y == 0 && r.Width == full.Width && r.Height == full.Height) return full;
                Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b)) g.DrawImage(full, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
                full.Dispose();
                return b;
            }
            catch { full.Dispose(); return null; }
        }

        /// A picture of a whole window's inside (the bar, the side list and the page), as it looks now.
        public static Bitmap SnapWindow(Control root)
        {
            if (!PageFader.Enabled2 || root.ClientSize.Width <= 0 || root.ClientSize.Height <= 0) return null;
            try
            {
                Bitmap b = new Bitmap(root.ClientSize.Width, root.ClientSize.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(root.BackColor);
                    for (int i = root.Controls.Count - 1; i >= 0; i--)   // bottom first
                    {
                        Control c = root.Controls[i];
                        if (!c.Visible || c is PageFader || c.Width <= 0 || c.Height <= 0) continue;
                        using (Bitmap one = PageFader.Snap(c))
                            if (one != null) g.DrawImageUnscaled(one, c.Left, c.Top);
                    }
                }
                return b;
            }
            catch { return null; }
        }
    }

    /// A soft circle that spreads from where you clicked and fades (buttons, side list rows).
    public class Ripple
    {
        Point at; Rectangle box; float t = 1;
        public bool Running { get { return t < 1; } }

        public void Start(Point p, Rectangle r)
        {
            if (!Fx.Motion) return;
            at = p; box = r; t = 0;
        }

        /// One frame; true while it's still spreading.
        public bool Step()
        {
            if (t >= 1) return false;
            t = Math.Min(1, t + 0.055f);
            return true;
        }

        public void Paint(Graphics g, float radius, Color c)
        {
            if (t >= 1 || box.Width <= 0) return;
            float ease = 1 - (float)Math.Pow(1 - t, 3);
            float far = 0;
            foreach (Point k in new[] { new Point(box.Left, box.Top), new Point(box.Right, box.Top), new Point(box.Left, box.Bottom), new Point(box.Right, box.Bottom) })
                far = Math.Max(far, (float)Math.Sqrt((k.X - at.X) * (k.X - at.X) + (k.Y - at.Y) * (k.Y - at.Y)));
            float r = Math.Max(2, (0.15f + 0.85f * ease) * far);
            int a = (int)(c.A * (1 - t) * (1 - t));
            if (a <= 0) return;
            GraphicsState st = g.Save();
            using (GraphicsPath clip = Theme.Round(box, radius))
            {
                g.SetClip(clip, CombineMode.Intersect);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, c)))
                    g.FillEllipse(b, at.X - r, at.Y - r, r * 2, r * 2);
            }
            g.Restore(st);
        }
    }

    /// Drag files out of the app (into Explorer, Discord, WhatsApp, an email...). A list arms it on mouse down;
    /// the drag starts once the mouse has moved a little with the button held.
    public static class DragOut
    {
        static Control armed; static Point at; static Func<string[]> what;
        /// True while one of our own drags is going on (pages ignore it, so a file isn't added to itself).
        public static bool Active;

        public static void Arm(Control c, Point p, Func<string[]> files)
        {
            Disarm();
            armed = c; at = p; what = files;
            c.MouseMove += Move; c.MouseUp += Up; c.MouseLeave += Leave;
        }

        static void Leave(object s, EventArgs e) { if (Control.MouseButtons != MouseButtons.Left) Disarm(); }
        static void Up(object s, MouseEventArgs e) { Disarm(); }

        static void Move(object s, MouseEventArgs e)
        {
            if (armed == null) return;
            if (e.Button != MouseButtons.Left) { Disarm(); return; }
            Size d = SystemInformation.DragSize;
            if (Math.Abs(e.X - at.X) <= d.Width * 2 && Math.Abs(e.Y - at.Y) <= d.Height * 2) return;
            Control c = armed; Func<string[]> f = what;
            Disarm();
            string[] paths = null;
            try { paths = f(); } catch { }
            if (paths == null) return;
            System.Collections.Generic.List<string> ok = new System.Collections.Generic.List<string>();
            foreach (string p in paths) if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) ok.Add(p);
            if (ok.Count == 0) return;
            DataObject data = new DataObject();
            data.SetData(DataFormats.FileDrop, ok.ToArray());
            Active = true;
            try { c.DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Link); }
            catch { }
            finally { Active = false; c.Capture = false; }
        }

        static void Disarm()
        {
            if (armed != null) { armed.MouseMove -= Move; armed.MouseUp -= Up; armed.MouseLeave -= Leave; }
            armed = null; what = null;
        }
    }
}
