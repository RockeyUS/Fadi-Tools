using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// A small window of its own, opened from the Donut Wheel, that does one job without bringing up Fadi Tools
    /// (Crop, Compress). It rises in near where the file was dropped, can be dragged by its top, and closes with Esc.
    public class MiniWindow : Form
    {
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        protected readonly Btn closeBtn;
        readonly Func<string> title;
        readonly string glyph;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        float t; int fromY, toY; bool closing;
        protected int Head { get { return Theme.S(52); } }
        protected int Pad { get { return Theme.S(20); } }

        public MiniWindow(Func<string> title, string glyph)
        {
            this.title = title; this.glyph = glyph;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true; TopMost = true; KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Theme.Card; Font = Theme.F(10f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = title() + " · " + L.T("app_name");
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            Movable.Make(this, () => Head);
            closeBtn = new Btn(() => "", Ico.Close, BtnKind.Ghost) { Surface = Surface.Card, GlyphSize = 9f, AccessibleName = L.T("close") };
            closeBtn.Click += (s, e) => Dismiss();
            Controls.Add(closeBtn);
            anim.Tick += (s, e) => Step();
        }

        /// Lays the window out for its size (the close button sits in the top corner).
        protected virtual void DoLayout()
        {
            int b = Theme.S(32);
            closeBtn.Bounds = Theme.Mirror(new Rectangle(ClientSize.Width - Theme.S(12) - b, (Head - b) / 2, b, b), ClientSize.Width);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (IsHandleCreated) SetRegion();
            DoLayout();
            Invalidate();
        }

        void SetRegion()
        {
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SetRegion(); }
        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }   // a soft shadow

        /// Opens centred on a point of the screen (the drop), kept inside the screen.
        public void ShowNear(Point at)
        {
            Rectangle wa = Screen.FromPoint(at).WorkingArea;
            int m = Theme.S(12);
            int x = Math.Max(wa.Left + m, Math.Min(wa.Right - Width - m, at.X - Width / 2));
            int y = Math.Max(wa.Top + m, Math.Min(wa.Bottom - Height - m, at.Y - Height / 2));
            toY = y; fromY = y + (Fx.Motion ? Theme.S(16) : 0);
            Location = new Point(x, fromY);
            Opacity = Fx.Motion ? 0 : 1;
            Show();
            try { SetForegroundWindow(Handle); } catch { }
            Activate();
            if (Fx.Motion) { t = 0; anim.Start(); }
        }

        void Step()
        {
            if (IsDisposed) { anim.Stop(); return; }
            t = Math.Min(1, t + 0.085f * Animator.K);
            float e = 1 - (float)Math.Pow(1 - t, 3);
            try
            {
                if (closing) { Opacity = Math.Max(0, 1 - e); Top = (int)Math.Round(toY + Theme.S(10) * e); }
                else { Opacity = Math.Min(1, e * 1.25); Top = (int)Math.Round(fromY + (toY - fromY) * e); }
            }
            catch { }
            if (t >= 1) { anim.Stop(); if (closing) Close(); }
        }

        /// Fades away and closes.
        public void Dismiss()
        {
            if (closing || IsDisposed) return;
            if (!CanClose()) return;
            closing = true;
            if (!Fx.Motion || !Visible) { Close(); return; }
            toY = Top; t = 0; anim.Start();
        }
        protected virtual bool CanClose() { return true; }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Dismiss(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }
        protected override void OnFormClosed(FormClosedEventArgs e) { anim.Stop(); anim.Dispose(); base.OnFormClosed(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width;
            Theme.Smooth(g);
            using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.CardBorder)) g.DrawPath(p, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));
            Rectangle ic = new Rectangle(Pad, (Head - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
            Theme.FillRound(g, Theme.Mirror(ic, W), Theme.S(9), Theme.AccentSoft);
            VectorIcons.Draw(g, glyph, 10f, Theme.AccentColor, Theme.Mirror(ic, W));
            Theme.Text(g, title(), Theme.F(11f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(ic.Right + Theme.S(10), 0, W - ic.Right - Theme.S(60), Head), W), Align.Start, true, false, true);
            PaintBody(g);
        }
        protected virtual void PaintBody(Graphics g) { }

        protected Rectangle M(Rectangle r) { return Theme.Mirror(r, ClientSize.Width); }
        protected void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        protected static void ShowInFolder(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }
    }

    // ====================================================================== Crop

    /// Donut Wheel → Crop: the picture with a frame to drag, ratios to snap to, the size in pixels, and Apply.
    /// The cropped copy is saved next to the original ("photo Cropped.jpg").
    public class CropWindow : MiniWindow
    {
        readonly string file;
        readonly CropCanvas canvas = new CropCanvas();
        readonly Segments ratio;
        readonly TextField wBox, hBox;
        readonly Btn bReset, bApply;
        readonly Control activitySource;
        Rectangle ratioLbl, sizeLbl, xLbl, pxLbl, statusR;
        string status;
        bool busy;
        static readonly float[] Ratios = { 0, -1, 1f, 16f / 9, 9f / 16, 4f / 3, 3f / 4 };   // 0 = free, -1 = the picture's own

        public static void Open(string file, Point at, Control activitySource)
        {
            CropWindow w = new CropWindow(file, activitySource);
            w.ShowNear(at);
            w.LoadPicture();
        }

        CropWindow(string file, Control activitySource) : base(() => L.T("wh_crop"), Ico.Crop)
        {
            this.file = file; this.activitySource = activitySource;
            ratio = new Segments(() => L.T("crop_free"), () => L.T("crop_orig"), () => "1:1", () => "16:9", () => "9:16", () => "4:3", () => "3:4") { Surface = Surface.Card, FontSize = 8.75f };
            ratio.SelectedChanged += (s, e) => { canvas.SetRatio(RatioNow()); };
            wBox = new TextField(() => "", null) { Surface = Surface.Card, ForceLtr = true };
            hBox = new TextField(() => "", null) { Surface = Surface.Card, ForceLtr = true };
            foreach (TextField tf in new[] { wBox, hBox })
            {
                TextField me = tf;
                me.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TypedSize(); } };
                me.Box.Leave += (s, e) => TypedSize();
            }
            bReset = new Btn(() => L.T("crop_reset"), Ico.Refresh, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 9f };
            bReset.Click += (s, e) => { ratio.SelectedIndex = 0; canvas.ResetCrop(); };
            bApply = new Btn(() => L.T("crop_apply"), Ico.Check, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bApply.Click += (s, e) => Apply();
            canvas.Changed += ShowSize;
            Controls.AddRange(new Control[] { canvas, ratio, wBox, hBox, bReset, bApply });
            ClientSize = new Size(Theme.S(560), Theme.S(620));
        }

        float RatioNow()
        {
            float r = Ratios[Math.Max(0, ratio.SelectedIndex)];
            if (r < 0) r = canvas.ImageSize.Height > 0 ? canvas.ImageSize.Width / (float)canvas.ImageSize.Height : 0;
            return r;
        }

        /// Reads the picture (slow formats go through the image engine) and fits the window to it.
        void LoadPicture()
        {
            status = L.T("loading");
            canvas.Loading = true;
            Thread th = new Thread(() =>
            {
                Bitmap full = null, shown = null; string err = null;
                try
                {
                    using (Image im = ViewerPage.Decode(file, 16000))
                    {
                        full = new Bitmap(im.Width, im.Height, PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(full)) g.DrawImage(im, 0, 0, im.Width, im.Height);
                    }
                    int maxSide = Theme.S(1100);
                    float k = Math.Min(1f, maxSide / (float)Math.Max(full.Width, full.Height));
                    shown = new Bitmap(Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)), PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(shown)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(full, 0, 0, shown.Width, shown.Height); }
                }
                catch (Exception ex) { err = ex.Message; }
                Size sz = full != null ? full.Size : Size.Empty;
                if (full != null) full.Dispose();
                Ui(() =>
                {
                    if (err != null) { status = err; canvas.Loading = false; Invalidate(); return; }
                    canvas.SetImage(shown, sz);
                    status = null;
                    bApply.Enabled = true;
                    FitToPicture();
                    ShowSize();
                });
            });
            th.IsBackground = true; th.Start();
        }

        /// The window takes the picture's shape (tall pictures get a taller window), inside the screen.
        void FitToPicture()
        {
            Size img = canvas.ImageSize;
            if (img.Width <= 0) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int maxW = Math.Min(Theme.S(760), wa.Width - Theme.S(60)), maxH = Math.Min(Theme.S(560), wa.Height - Theme.S(260) - Theme.S(60));
            float k = Math.Min(maxW / (float)img.Width, maxH / (float)img.Height);
            int cw = Math.Max(Theme.S(200), (int)(img.Width * k)), ch = Math.Max(Theme.S(160), (int)(img.Height * k));
            int W = Math.Max(Theme.S(520), cw + Pad * 2 + Theme.S(16));
            int H = Head + ch + Theme.S(16) + Theme.S(200);
            Point c = new Point(Left + Width / 2, Top + Height / 2);
            Bounds = new Rectangle(Math.Max(wa.Left + 8, Math.Min(wa.Right - W - 8, c.X - W / 2)), Math.Max(wa.Top + 8, Math.Min(wa.Bottom - H - 8, c.Y - H / 2)), W, H);
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (canvas == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            int bottom = Theme.S(196);
            canvas.Bounds = M(new Rectangle(p, Head, W - p * 2, Math.Max(Theme.S(120), H - Head - bottom)));
            int y = canvas.Bottom + Theme.S(18);
            ratioLbl = new Rectangle(p, y, W - p * 2, Theme.S(20));
            ratio.Bounds = M(new Rectangle(p, y + Theme.S(24), W - p * 2, Theme.S(36)));
            y += Theme.S(74);
            int bw = Theme.S(96);
            sizeLbl = new Rectangle(p, y, Theme.S(50), Theme.S(36));
            wBox.Bounds = M(new Rectangle(p + Theme.S(50), y, bw, Theme.S(36)));
            xLbl = new Rectangle(p + Theme.S(50) + bw, y, Theme.S(28), Theme.S(36));
            hBox.Bounds = M(new Rectangle(xLbl.Right, y, bw, Theme.S(36)));
            pxLbl = new Rectangle(xLbl.Right + bw + Theme.S(8), y, Theme.S(30), Theme.S(36));
            statusR = new Rectangle(pxLbl.Right + Theme.S(8), y, W - p - pxLbl.Right - Theme.S(8), Theme.S(36));
            y = H - Theme.S(20) - Theme.S(40);
            int rw = Math.Max(Theme.S(110), bReset.PreferredWidth), aw = Math.Max(Theme.S(120), bApply.PreferredWidth + Theme.S(16));
            bReset.Bounds = M(new Rectangle(p - Theme.S(8), y + Theme.S(3), rw, Theme.S(34)));
            bApply.Bounds = M(new Rectangle(W - p - aw, y, aw, Theme.S(40)));
        }

        protected override void PaintBody(Graphics g)
        {
            Theme.Text(g, L.T("crop_ratio"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(ratioLbl), Align.Start);
            Theme.Text(g, L.T("crop_size"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(sizeLbl), Align.Start);
            Theme.Text(g, "×", Theme.F(10f), Theme.InkSoft, M(xLbl), Align.Center);
            Theme.Text(g, "px", Theme.F(9f), Theme.InkSoft, M(pxLbl), Align.Start);
            if (status != null) Theme.Text(g, status, Theme.F(8.75f), Theme.InkSoft, M(statusR), Align.End, true, true);
        }

        bool typing;
        void ShowSize()
        {
            if (typing) return;
            Rectangle c = canvas.CropPx;
            if (!wBox.Box.Focused) wBox.Box.Text = c.Width.ToString(CultureInfo.InvariantCulture);
            if (!hBox.Box.Focused) hBox.Box.Text = c.Height.ToString(CultureInfo.InvariantCulture);
        }

        /// A size typed in the boxes: the frame takes it (around the same middle), the ratio becomes Free.
        void TypedSize()
        {
            int w, h;
            if (!int.TryParse(wBox.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out w) ||
                !int.TryParse(hBox.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) { ShowSize(); return; }
            typing = true;
            if (ratio.SelectedIndex != 0) ratio.SelectedIndex = 0;
            canvas.SetCropSize(w, h);
            typing = false;
            ShowSize();
        }

        protected override bool CanClose() { return !busy; }

        void Apply()
        {
            if (busy || canvas.ImageSize.Width <= 0) return;
            Rectangle c = canvas.CropPx;
            if (c.Width < 1 || c.Height < 1) return;
            busy = true; bApply.Enabled = false; bReset.Enabled = false;
            status = L.T("saving"); Invalidate();
            string ext = Formats.Ext(file);
            string outDir = DragWheel.OutDirFor(file);
            ActJob job = Activity.Begin(activitySource, L.T("wh_crop"), Path.GetFileName(file), null);
            Thread th = new Thread(() =>
            {
                string dst = null, err = null;
                try { dst = Crop(file, c, ext, outDir); }
                catch (Exception ex) { err = ex.Message; }
                Activity.End(job, err, false, err == null ? L.T("crop_done", c.Width, c.Height) : null, dst);
                Ui(() =>
                {
                    busy = false;
                    if (err != null) { status = err; bApply.Enabled = bReset.Enabled = true; Invalidate(); return; }
                    if (Settings.OpenAfter) ShowInFolder(dst);
                    Dismiss();
                });
            });
            th.IsBackground = true; th.Start();
        }

        /// Writes the cropped copy: with the image engine when it's there (same format, same quality), otherwise
        /// with Windows' own pictures (JPEG, PNG, BMP, GIF, TIFF; anything else becomes PNG).
        static string Crop(string file, Rectangle c, string ext, string outDir)
        {
            string name = Path.GetFileNameWithoutExtension(file) + " " + L.T("crop_suffix");
            string magick = Tools.Magick;
            bool gdiOk = ext == "jpg" || ext == "jpeg" || ext == "png" || ext == "bmp" || ext == "gif" || ext == "tif" || ext == "tiff";
            if (magick != null)
            {
                string outExt = ext == "jfif" ? "jpg" : ext;
                string dst = Paths.UniquePath(outDir, name, outExt);
                string q = outExt == "jpg" || outExt == "jpeg" ? " -quality 95" : "";
                string output;
                int code = Tools.RunHidden(magick, Tools.Quote(file + "[0]") + " -auto-orient -crop " + c.Width + "x" + c.Height + "+" + c.X + "+" + c.Y + " +repage" + q + " " + Tools.Quote(dst), 180000, out output);
                if (code == 0 && File.Exists(dst)) return dst;
                if (!gdiOk) throw new Exception(Tools.LastLines(output, 1));
            }
            string ext2 = gdiOk ? (ext == "jpeg" ? "jpg" : ext == "tif" ? "tiff" : ext) : "png";
            string d2 = Paths.UniquePath(outDir, name, ext2);
            using (Image im = ViewerPage.Decode(file, 16000))
            using (Bitmap b = new Bitmap(c.Width, c.Height, ext2 == "jpg" || ext2 == "bmp" ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    if (ext2 == "jpg" || ext2 == "bmp") g.Clear(Color.White);
                    g.DrawImage(im, new Rectangle(0, 0, c.Width, c.Height), c, GraphicsUnit.Pixel);
                }
                if (ext2 == "jpg") Compressor.SaveJpeg(b, d2, 95);
                else b.Save(d2, ext2 == "png" ? ImageFormat.Png : ext2 == "bmp" ? ImageFormat.Bmp : ext2 == "gif" ? ImageFormat.Gif : ImageFormat.Tiff);
            }
            return d2;
        }
    }

    /// The picture with the crop frame: drag the frame or its handles, or draw a new one; the rest is dimmed.
    /// Changes of ratio and Reset glide to their new frame.
    public class CropCanvas : UiControl
    {
        Bitmap shown;
        public Size ImageSize { get; private set; }
        public bool Loading;
        RectangleF crop, drawn;        // in picture pixels: where the frame is, and where it's drawn (easing towards it)
        float ratio;                   // width / height, 0 = free
        int grab = -1;                 // 0..7 handles (corners then edges), 8 = move, 9 = new frame
        PointF grabAt; RectangleF grabCrop;
        public event Action Changed;

        public CropCanvas() { Surface = Surface.Card; Cursor = Cursors.Cross; }

        public Rectangle CropPx
        {
            get
            {
                Rectangle r = Rectangle.Round(crop);
                r.Intersect(new Rectangle(Point.Empty, ImageSize));
                return r;
            }
        }

        public void SetImage(Bitmap b, Size full)
        {
            if (shown != null) shown.Dispose();
            shown = b; ImageSize = full; Loading = false;
            crop = drawn = new RectangleF(0, 0, full.Width, full.Height);
            Invalidate();
        }

        public void ResetCrop() { ratio = 0; crop = new RectangleF(0, 0, ImageSize.Width, ImageSize.Height); Moved(true); }

        /// Snaps the frame to a ratio: the biggest frame of that shape inside the current one's middle area.
        public void SetRatio(float r)
        {
            ratio = r;
            if (r <= 0 || ImageSize.Width <= 0) { Moved(true); return; }
            float cx = crop.X + crop.Width / 2, cy = crop.Y + crop.Height / 2;
            float w = crop.Width, h = w / r;
            if (h > crop.Height) { h = crop.Height; w = h * r; }
            if (w < 8 || h < 8) { w = ImageSize.Width; h = w / r; if (h > ImageSize.Height) { h = ImageSize.Height; w = h * r; } }
            crop = Clamp(new RectangleF(cx - w / 2, cy - h / 2, w, h));
            Moved(true);
        }

        public void SetCropSize(int w, int h)
        {
            w = Math.Max(1, Math.Min(ImageSize.Width, w)); h = Math.Max(1, Math.Min(ImageSize.Height, h));
            ratio = 0;
            float cx = crop.X + crop.Width / 2, cy = crop.Y + crop.Height / 2;
            crop = Clamp(new RectangleF(cx - w / 2f, cy - h / 2f, w, h));
            Moved(true);
        }

        RectangleF Clamp(RectangleF r)
        {
            float W = ImageSize.Width, H = ImageSize.Height;
            r.Width = Math.Min(r.Width, W); r.Height = Math.Min(r.Height, H);
            r.X = Math.Max(0, Math.Min(W - r.Width, r.X)); r.Y = Math.Max(0, Math.Min(H - r.Height, r.Y));
            return r;
        }

        void Moved(bool glide)
        {
            if (!glide || !Fx.Motion) drawn = crop;
            Animator.Start(this);
            Invalidate();
            if (Changed != null) Changed();
        }

        // ---- where the picture sits in the control
        RectangleF Fit
        {
            get
            {
                if (ImageSize.Width <= 0) return RectangleF.Empty;
                float k = Math.Min((Width - 2f) / ImageSize.Width, (Height - 2f) / ImageSize.Height);
                float w = ImageSize.Width * k, h = ImageSize.Height * k;
                return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
            }
        }
        float K { get { RectangleF f = Fit; return ImageSize.Width > 0 ? f.Width / ImageSize.Width : 1; } }
        RectangleF ToScreen(RectangleF r) { RectangleF f = Fit; float k = K; return new RectangleF(f.X + r.X * k, f.Y + r.Y * k, r.Width * k, r.Height * k); }
        PointF ToImage(Point p) { RectangleF f = Fit; float k = K; return new PointF((p.X - f.X) / k, (p.Y - f.Y) / k); }

        PointF[] Handles(RectangleF s)
        {
            float cx = s.X + s.Width / 2, cy = s.Y + s.Height / 2;
            return new[] { new PointF(s.Left, s.Top), new PointF(s.Right, s.Top), new PointF(s.Right, s.Bottom), new PointF(s.Left, s.Bottom),
                           new PointF(cx, s.Top), new PointF(s.Right, cy), new PointF(cx, s.Bottom), new PointF(s.Left, cy) };
        }

        int HitHandle(Point p)
        {
            RectangleF s = ToScreen(crop);
            PointF[] h = Handles(s);
            float tol = Theme.S(12);
            for (int i = 0; i < 8; i++) if (Math.Abs(p.X - h[i].X) <= tol && Math.Abs(p.Y - h[i].Y) <= tol) return i;
            if (s.Contains(p)) return 8;
            return -1;
        }

        static readonly Cursor[] HandleCursors = { Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNWSE, Cursors.SizeNESW, Cursors.SizeNS, Cursors.SizeWE, Cursors.SizeNS, Cursors.SizeWE };

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || ImageSize.Width <= 0) return;
            grab = HitHandle(e.Location);
            if (grab < 0) grab = 9;
            grabAt = ToImage(e.Location); grabCrop = crop;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (ImageSize.Width <= 0) return;
            if (grab < 0)
            {
                int h = HitHandle(e.Location);
                Cursor = h >= 0 && h < 8 ? HandleCursors[h] : h == 8 ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            PointF p = ToImage(e.Location);
            float W = ImageSize.Width, H = ImageSize.Height;
            p.X = Math.Max(0, Math.Min(W, p.X)); p.Y = Math.Max(0, Math.Min(H, p.Y));
            float dx = p.X - grabAt.X, dy = p.Y - grabAt.Y;
            RectangleF r = grabCrop;
            float min = Math.Max(4, 24 / Math.Max(0.01f, K));
            if (grab == 8) { r.X += dx; r.Y += dy; crop = Clamp(r); Moved(false); return; }
            float l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            if (grab == 9) { l = Math.Min(grabAt.X, p.X); rt = Math.Max(grabAt.X, p.X); t = Math.Min(grabAt.Y, p.Y); b = Math.Max(grabAt.Y, p.Y); }
            else
            {
                if (grab == 0 || grab == 3 || grab == 7) l = Math.Min(rt - min, p.X);
                if (grab == 1 || grab == 2 || grab == 5) rt = Math.Max(l + min, p.X);
                if (grab == 0 || grab == 1 || grab == 4) t = Math.Min(b - min, p.Y);
                if (grab == 2 || grab == 3 || grab == 6) b = Math.Max(t + min, p.Y);
            }
            RectangleF n = RectangleF.FromLTRB(l, t, rt, b);
            if (ratio > 0)
            {
                // keep the shape: the side being pulled leads, the other follows (anchored at the opposite corner or edge)
                bool horiz = grab == 5 || grab == 7 || (grab != 4 && grab != 6 && Math.Abs(dx) >= Math.Abs(dy));
                float w = n.Width, h = n.Height;
                if (horiz) h = w / ratio; else w = h * ratio;
                if (w > W) { w = W; h = w / ratio; }
                if (h > H) { h = H; w = h * ratio; }
                bool fromRight = grab == 0 || grab == 3 || grab == 7 || (grab == 9 && p.X < grabAt.X);
                bool fromBottom = grab == 0 || grab == 1 || grab == 4 || (grab == 9 && p.Y < grabAt.Y);
                float ax = fromRight ? (grab == 9 ? grabAt.X : grabCrop.Right) : (grab == 9 ? grabAt.X : grabCrop.Left);
                float ay = fromBottom ? (grab == 9 ? grabAt.Y : grabCrop.Bottom) : (grab == 9 ? grabAt.Y : grabCrop.Top);
                if (grab == 4 || grab == 6) ax = grabCrop.X + grabCrop.Width / 2 + (fromRight ? w / 2 : -w / 2);
                if (grab == 5 || grab == 7) ay = grabCrop.Y + grabCrop.Height / 2 + (fromBottom ? h / 2 : -h / 2);
                n = new RectangleF(fromRight ? ax - w : ax, fromBottom ? ay - h : ay, w, h);
            }
            if (n.Width >= 1 && n.Height >= 1) { crop = Clamp(n); Moved(false); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (grab == 9 && (crop.Width < 4 || crop.Height < 4)) ResetCrop();
            grab = -1; Capture = false;
            Animator.Start(this); Invalidate();
        }

        public override bool Step()
        {
            bool more = base.Step();
            float k = Animator.Rate(0.25f);
            RectangleF d = drawn;
            if (Math.Abs(d.X - crop.X) + Math.Abs(d.Y - crop.Y) + Math.Abs(d.Width - crop.Width) + Math.Abs(d.Height - crop.Height) > 0.5f / Math.Max(0.01f, K))
            {
                drawn = new RectangleF(d.X + (crop.X - d.X) * k, d.Y + (crop.Y - d.Y) * k, d.Width + (crop.Width - d.Width) * k, d.Height + (crop.Height - d.Height) * k);
                more = true;
            }
            else drawn = crop;
            Invalidate();
            return more;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            if (shown == null)
            {
                Theme.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(12), Theme.CardInner);
                Theme.Text(g, Loading ? L.T("loading") : "", Theme.F(9f), Theme.InkSoft, ClientRectangle, Align.Center);
                return;
            }
            RectangleF f = Fit;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(shown, f);
            RectangleF s = ToScreen(drawn);
            // everything outside the frame is dimmed
            using (Region outside = new Region(f))
            {
                outside.Exclude(s);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(140, 10, 10, 12))) g.FillRegion(dim, outside);
            }
            // thirds while dragging
            if (grab >= 0)
                using (Pen tp = new Pen(Color.FromArgb(110, 255, 255, 255), 1f))
                    for (int i = 1; i < 3; i++)
                    {
                        g.DrawLine(tp, s.X + s.Width * i / 3, s.Y, s.X + s.Width * i / 3, s.Bottom);
                        g.DrawLine(tp, s.X, s.Y + s.Height * i / 3, s.Right, s.Y + s.Height * i / 3);
                    }
            using (Pen bp = new Pen(Theme.AccentColor, Math.Max(1.5f, Theme.S(2)))) g.DrawRectangle(bp, s.X, s.Y, s.Width, s.Height);
            // handles: L-shaped corners and short bars on the edges
            float hl = Theme.S(14), th = Theme.S(4);
            using (SolidBrush hb = new SolidBrush(Theme.AccentColor))
            {
                PointF[] h = Handles(s);
                for (int i = 0; i < 4; i++)
                {
                    float sx = i == 0 || i == 3 ? 1 : -1, sy = i < 2 ? 1 : -1;
                    g.FillRectangle(hb, sx > 0 ? h[i].X - th / 2 : h[i].X - hl + th / 2, h[i].Y - th / 2, hl, th);
                    g.FillRectangle(hb, h[i].X - th / 2, sy > 0 ? h[i].Y - th / 2 : h[i].Y - hl + th / 2, th, hl);
                }
                for (int i = 4; i < 8; i++)
                {
                    bool horizontal = i == 4 || i == 6;
                    float w = horizontal ? hl * 1.4f : th, hh = horizontal ? th : hl * 1.4f;
                    g.FillRectangle(hb, h[i].X - w / 2, h[i].Y - hh / 2, w, hh);
                }
            }
        }

        protected override void Dispose(bool disposing) { if (disposing && shown != null) { shown.Dispose(); shown = null; } base.Dispose(disposing); }
    }

    // ====================================================================== Compress

    /// Donut Wheel → Compress: how much smaller (and an optional size to aim for), then it compresses right here,
    /// showing how much was saved. The smaller copy is saved next to the original.
    public class CompressWindow : MiniWindow
    {
        readonly string[] files;
        readonly Segments level;
        readonly TextField target;
        readonly Progress bar = new Progress { Visible = false };
        readonly Btn bGo, bCancel, bShow;
        readonly Control activitySource;
        Rectangle fileR, levelLbl, hintR, targetLbl, statusR;
        string status; Color statusColor;
        bool busy, cancel, done;
        string lastOut;
        readonly long before;
        readonly string kindGlyph;

        public static void Open(string[] files, Point at, Control activitySource)
        {
            new CompressWindow(files, activitySource).ShowNear(at);
        }

        CompressWindow(string[] files, Control activitySource) : base(() => L.T("wh_compress"), "")
        {
            this.files = files; this.activitySource = activitySource;
            foreach (string f in files) try { before += new FileInfo(f).Length; } catch { }
            kindGlyph = Formats.GlyphFor(Formats.CategoryOf(Formats.Ext(files[0])));
            level = new Segments(() => L.T("cmp_light"), () => L.T("cmp_balanced"), () => L.T("cmp_strong")) { Surface = Surface.Card };
            int lv; level.SelectedIndex = int.TryParse(Settings.Get("WheelCmpLevel", "1"), out lv) ? Math.Max(0, Math.Min(2, lv)) : 1;
            level.SelectedChanged += (s, e) => { Settings.Set("WheelCmpLevel", level.SelectedIndex.ToString()); Invalidate(); };
            target = new TextField(() => L.T("cmp_fit_hint"), null) { Surface = Surface.Card, ForceLtr = true };
            target.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(); } };
            bGo = new Btn(() => L.T("cmp_go"), "", BtnKind.Primary) { Surface = Surface.Card };
            bGo.Click += (s, e) => { if (done) Dismiss(); else Go(); };
            bCancel = new Btn(() => L.T(busy ? "cancel" : "close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { if (busy) cancel = true; else Dismiss(); };
            bShow = new Btn(() => L.T(files.Length == 1 ? "show_file" : "open_folder"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, Visible = false, FontSize = 9f };
            bShow.Click += (s, e) => { if (lastOut != null) { if (files.Length == 1) ShowInFolder(lastOut); else try { Process.Start(Path.GetDirectoryName(lastOut)); } catch { } } };
            Controls.AddRange(new Control[] { level, target, bar, bGo, bCancel, bShow });
            ClientSize = new Size(Theme.S(440), Theme.S(404));
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (level == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            fileR = new Rectangle(p, Head + Theme.S(4), W - p * 2, Theme.S(56));
            int y = fileR.Bottom + Theme.S(16);
            levelLbl = new Rectangle(p, y, W - p * 2, Theme.S(20));
            level.Bounds = M(new Rectangle(p, y + Theme.S(24), W - p * 2, Theme.S(38)));
            hintR = new Rectangle(p, y + Theme.S(66), W - p * 2, Theme.S(38));
            y += Theme.S(112);
            targetLbl = new Rectangle(p, y, W - p * 2 - Theme.S(130), Theme.S(38));
            target.Bounds = M(new Rectangle(W - p - Theme.S(120), y, Theme.S(120), Theme.S(38)));
            y += Theme.S(52);
            bar.Bounds = M(new Rectangle(p, y + Theme.S(4), W - p * 2, Theme.S(6)));
            statusR = new Rectangle(p, y + Theme.S(12), W - p * 2, Theme.S(36));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(120), bGo.PreferredWidth + Theme.S(12)), cw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - cw, by, cw, Theme.S(40)));
            bShow.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(110), bShow.PreferredWidth), Theme.S(34)));
        }

        protected override void PaintBody(Graphics g)
        {
            // the file: its kind, its name and how big it is now
            Rectangle ic = new Rectangle(fileR.X, fileR.Y + (fileR.Height - Theme.S(44)) / 2, Theme.S(44), Theme.S(44));
            Theme.FillRound(g, M(ic), Theme.S(12), Theme.CardInner);
            VectorIcons.Draw(g, kindGlyph, 13f, Theme.InkSoft, M(ic));
            string name = files.Length == 1 ? Path.GetFileName(files[0]) : L.T("n_files", files.Length);
            Theme.Text(g, name, Theme.F(10f, FontStyle.Bold), Theme.Ink, M(new Rectangle(ic.Right + Theme.S(12), fileR.Y + Theme.S(6), fileR.Right - ic.Right - Theme.S(12), Theme.S(24))), Align.Start, true, false, true);
            Theme.Text(g, Theme.HumanSize(before), Theme.F(9f), Theme.InkSoft, M(new Rectangle(ic.Right + Theme.S(12), fileR.Y + Theme.S(28), fileR.Right - ic.Right - Theme.S(12), Theme.S(22))), Align.Start);
            Theme.Text(g, L.T("cmp_how"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(levelLbl), Align.Start);
            string hk = new[] { "cmp_hint_light", "cmp_hint_balanced", "cmp_hint_strong" }[Math.Max(0, level.SelectedIndex)];
            Theme.Text(g, L.T(hk), Theme.F(8.75f), Theme.InkSoft, M(hintR), Align.Start, false, true);
            Theme.Text(g, L.T("cmp_fit"), Theme.F(9.25f), Theme.Ink, M(targetLbl), Align.Start, true, true);
            if (status != null) Theme.Text(g, status, Theme.F(9.25f, done ? FontStyle.Bold : FontStyle.Regular), statusColor, M(statusR), Align.Start, true, true);
        }

        protected override bool CanClose() { if (busy) { cancel = true; return false; } return true; }

        void Go()
        {
            if (busy || done) return;
            double mb = 0;
            string tt = target.Box.Text.Trim().Replace(',', '.');
            if (tt.Length > 0 && (!double.TryParse(tt, NumberStyles.Float, CultureInfo.InvariantCulture, out mb) || mb <= 0)) { status = L.T("crop_bad_number"); statusColor = Theme.Error; Invalidate(); return; }
            Compressor.Level lv = level.SelectedIndex == 0 ? Compressor.Level.Light : level.SelectedIndex == 2 ? Compressor.Level.Strong : Compressor.Level.Balanced;
            busy = true; cancel = false;
            level.Enabled = target.Enabled = bGo.Enabled = false;
            bCancel.Invalidate();
            bar.Visible = true; bar.Value = 0;
            status = L.T("cmp_working", 1, files.Length); statusColor = Theme.InkSoft; Invalidate();
            string title = files.Length == 1 ? Path.GetFileName(files[0]) : L.T("n_files", files.Length);
            ActJob act = Activity.Begin(activitySource, L.T("act_k_compress"), title, () => cancel = true);
            Thread th = new Thread(() =>
            {
                long after = 0, saved = 0; int ok = 0; string err = null;
                for (int i = 0; i < files.Length && !cancel; i++)
                {
                    string f = files[i]; int index = i;
                    long b = 0; try { b = new FileInfo(f).Length; } catch { }
                    Ui(() => { status = L.T("cmp_working", index + 1, files.Length); Invalidate(); });
                    try
                    {
                        string outDir = DragWheel.OutDirFor(f);
                        Job job = Compressor.Build(f, outDir, lv, mb);
                        Action<double> prog = p =>
                        {
                            double all = (index + p) / files.Length;
                            Activity.Update(act, all, (int)(all * 100) + "%");
                            Ui(() => { bar.Value = all; });
                        };
                        string e = Engine.Run(job, prog, () => cancel);
                        if (e != null && !cancel && job.Fallback != null)
                        {
                            // the graphics card couldn't do it: the same on the processor
                            try { if (File.Exists(job.Output)) File.Delete(job.Output); } catch { }
                            job = job.Fallback();
                            e = Engine.Run(job, prog, () => cancel);
                        }
                        if (e != null) throw new Exception(e);
                        long got = File.Exists(job.Output) ? new FileInfo(job.Output).Length : b;
                        if (got >= b * 0.98 && mb <= 0)
                        {
                            // no real gain: the original stays as it is
                            try { File.Delete(job.Output); } catch { }
                            after += b;
                        }
                        else { after += got; saved += Math.Max(0, b - got); lastOut = job.Output; }
                        ok++;
                    }
                    catch (Exception ex) { if (!cancel) err = ex.Message; }
                }
                int pct = before > 0 ? (int)Math.Round(100.0 * saved / before) : 0;
                string detail = saved > 0 ? L.T("cmp_saved", Theme.HumanSize(saved), pct) : ok > 0 ? L.T("cmp_nothing") : null;
                Activity.End(act, ok == 0 && !cancel ? (err ?? L.T("cmp_none")) : null, cancel, detail, files.Length == 1 ? lastOut : (lastOut != null ? Path.GetDirectoryName(lastOut) : null));
                Ui(() =>
                {
                    busy = false; bar.Visible = false;
                    if (cancel) { status = L.T("cancelled"); statusColor = Theme.InkSoft; level.Enabled = target.Enabled = bGo.Enabled = true; bCancel.Invalidate(); Invalidate(); return; }
                    done = ok > 0;
                    if (saved > 0) { status = Theme.HumanSize(before) + "  →  " + Theme.HumanSize(after) + "   ·   " + L.T("cmp_saved", Theme.HumanSize(saved), pct); statusColor = Theme.Success; }
                    else if (ok > 0) { status = files.Length == 1 ? L.T("cmp_already") : L.T("cmp_nothing"); statusColor = Theme.InkSoft; }
                    else { status = err ?? L.T("cmp_none"); statusColor = Theme.Error; }
                    bGo.Label = () => L.T(done ? "wn_ok" : "cmp_go");
                    bGo.Glyph = done ? Ico.Check : "";
                    bGo.Enabled = true; level.Enabled = target.Enabled = !done;
                    bShow.Visible = lastOut != null;
                    if (done) bCancel.Visible = false;
                    DoLayout(); Invalidate(); bGo.Invalidate();
                    if (done && lastOut != null && Settings.OpenAfter) { if (files.Length == 1) ShowInFolder(lastOut); else try { Process.Start(Path.GetDirectoryName(lastOut)); } catch { } }
                });
            });
            th.IsBackground = true; th.Start();
        }
    }
}
