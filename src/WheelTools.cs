using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    // The small windows the Donut Wheel opens for pictures and videos: AI Scale, Resize, Rotate and Trim.
    // Each saves a new copy next to the original and shows in Activity; Fadi Tools itself stays where it is.

    /// A picture fitted in a box. It can turn and flip with a glide (Rotate), and show a before/after split
    /// with a handle to drag (AI Scale).
    public class PicView : UiControl
    {
        Bitmap pic, after;
        public bool Loading;
        public string Message;
        // turning and flipping (what it's going to, and what it shows now)
        public int Angle; public bool FlipX, FlipY;
        float angleNow, sxNow = 1, syNow = 1;
        // before / after
        float split = 0.5f; bool dragging;
        public string BeforeLabel = "", AfterLabel = "";

        public PicView() { Surface = Surface.Card; }

        public void SetPicture(Bitmap b) { if (pic != null) pic.Dispose(); pic = b; Loading = false; Invalidate(); }
        /// Before / after of the whole picture, and (optional) of a close look at its middle.
        public void SetCompare(Bitmap before, Bitmap aft, Bitmap closeBefore, Bitmap closeAfter)
        {
            if (pic != null) pic.Dispose();
            if (after != null) after.Dispose();
            pic = before; after = aft; split = 0.5f;
            cBefore = closeBefore; cAfter = closeAfter; closeUp = false;
            Cursor = Cursors.SizeWE;
            Invalidate();
        }
        public bool Comparing { get { return after != null; } }
        Bitmap cBefore, cAfter;
        bool closeUp;
        public string WholeLabel = "", CloseLabel = "";
        Rectangle wholeR, closeR;   // the "Whole picture | Close-up" switch

        public void Turn(int deg) { Angle += deg; Animator.Start(this); }
        public void Flip(bool horizontal)
        {
            // a flip of a turned picture swaps which way it goes on the screen
            bool sideways = ((Angle % 180) + 180) % 180 == 90;
            if (horizontal ^ sideways) FlipX = !FlipX; else FlipY = !FlipY;
            Animator.Start(this);
        }
        public void ResetTurn() { Angle = 0; FlipX = FlipY = false; Animator.Start(this); }

        public override bool Step()
        {
            bool more = base.Step();
            if (!Fx.Motion) { angleNow = Angle; sxNow = FlipX ? -1 : 1; syNow = FlipY ? -1 : 1; Invalidate(); return more; }
            more |= Fx.Ease(ref angleNow, Angle, 0.22f);
            more |= Fx.Ease(ref sxNow, FlipX ? -1 : 1, 0.22f);
            more |= Fx.Ease(ref syNow, FlipY ? -1 : 1, 0.22f);
            Invalidate();
            return more;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Comparing || e.Button != MouseButtons.Left) return;
            if (cBefore != null && (wholeR.Contains(e.Location) || closeR.Contains(e.Location)))
            {
                bool c = closeR.Contains(e.Location);
                if (c != closeUp) { if (Fx.Motion) Fx.Crossfade(this, 160); closeUp = c; Invalidate(); }
                return;
            }
            dragging = true; Capture = true; SplitAt(e.X);
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SplitAt(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }
        void SplitAt(int x) { split = Math.Max(0.02f, Math.Min(0.98f, x / (float)Math.Max(1, Width))); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF box = new RectangleF(0, 0, Width - 1, Height - 1);
            if (pic == null)
            {
                Theme.FillRound(g, box, Theme.S(12), Theme.CardInner);
                Theme.Text(g, Message ?? (Loading ? L.T("loading") : ""), Theme.F(9f), Theme.InkSoft, ClientRectangle, Align.Center, true, true);
                return;
            }
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (Comparing)
            {
                // the whole picture (or a close look), before and after, across a handle
                Bitmap b0 = closeUp && cBefore != null ? cBefore : pic, b1 = closeUp && cAfter != null ? cAfter : after;
                Theme.FillRound(g, box, Theme.S(12), Theme.Mix(Theme.CardInner, Theme.Ink, 0.06f));
                float fk = Math.Min(box.Width / b0.Width, box.Height / b0.Height);
                RectangleF fit = new RectangleF(box.X + (box.Width - b0.Width * fk) / 2, box.Y + (box.Height - b0.Height * fk) / 2, b0.Width * fk, b0.Height * fk);
                using (GraphicsPath clip = Theme.Round(box, Theme.S(12))) g.SetClip(clip);
                g.DrawImage(b0, fit);
                float x = box.Width * split;
                g.SetClip(new RectangleF(x, 0, box.Width - x, box.Height), CombineMode.Intersect);
                g.DrawImage(b1, fit);
                g.ResetClip();
                using (Pen p = new Pen(Color.White, Math.Max(2f, Theme.S(2)))) g.DrawLine(p, x, 0, x, box.Height);
                float k = Theme.S(30);
                RectangleF knob = new RectangleF(x - k / 2, box.Height / 2 - k / 2, k, k);
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(sh, knob.X, knob.Y + 2, k, k);
                using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, knob);
                VectorIcons.Draw(g, "", 9f, Color.FromArgb(40, 36, 32), Rectangle.Round(knob));
                Badge(g, BeforeLabel, new PointF(Theme.S(10), Theme.S(10)), false);
                Badge(g, AfterLabel, new PointF(box.Width - Theme.S(10), Theme.S(10)), true);
                if (cBefore != null)
                {
                    // Whole picture | Close-up, at the bottom
                    Font sf = Theme.F(8f, FontStyle.Bold);
                    int w0 = TextRenderer.MeasureText(WholeLabel, sf).Width + Theme.S(20), w1 = TextRenderer.MeasureText(CloseLabel, sf).Width + Theme.S(20), hh = Theme.S(26);
                    int sx = (int)(box.Width - w0 - w1) / 2, sy = (int)box.Height - hh - Theme.S(10);
                    Theme.FillRound(g, new Rectangle(sx - 3, sy - 3, w0 + w1 + 6, hh + 6), (hh + 6) / 2f, Color.FromArgb(185, 20, 20, 22));
                    wholeR = new Rectangle(sx, sy, w0, hh); closeR = new Rectangle(sx + w0, sy, w1, hh);
                    Theme.FillRound(g, closeUp ? closeR : wholeR, hh / 2f, Color.White);
                    Theme.Text(g, WholeLabel, sf, closeUp ? Color.White : Color.FromArgb(30, 30, 32), wholeR, Align.Center);
                    Theme.Text(g, CloseLabel, sf, closeUp ? Color.FromArgb(30, 30, 32) : Color.White, closeR, Align.Center);
                }
                return;
            }
            // fitted for the way it will end up (a turned picture swaps width and height)
            bool sideways = Math.Abs(Math.Sin(angleNow * Math.PI / 180)) > 0.5;
            float pw = pic.Width, ph = pic.Height;
            float bw = sideways ? ph : pw, bh = sideways ? pw : ph;
            float k2 = Math.Min((Width - Theme.S(16)) / bw, (Height - Theme.S(16)) / bh);
            // while it turns, it shrinks a little so the corners stay inside
            double frac = Math.Abs(Math.Sin(2 * angleNow * Math.PI / 180));
            k2 *= (float)(1 - 0.18 * frac);
            GraphicsState st = g.Save();
            g.TranslateTransform(Width / 2f, Height / 2f);
            g.RotateTransform(angleNow);
            g.ScaleTransform(sxNow, syNow);
            float w = pw * k2, h = ph * k2;
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(Theme.Dark ? 60 : 30, 0, 0, 0))) g.FillRectangle(sh, -w / 2 + 2, -h / 2 + 3, w, h);
            g.DrawImage(pic, -w / 2, -h / 2, w, h);
            g.Restore(st);
        }

        static void Badge(Graphics g, string text, PointF at, bool right)
        {
            if (string.IsNullOrEmpty(text)) return;
            Font f = Theme.F(8f, FontStyle.Bold);
            SizeF sz = g.MeasureString(text, f);
            RectangleF r = new RectangleF(right ? at.X - sz.Width - Theme.S(14) : at.X, at.Y, sz.Width + Theme.S(14), Theme.S(22));
            Theme.FillRound(g, r, Theme.S(11), Color.FromArgb(170, 20, 20, 22));
            Theme.Text(g, text, f, Color.White, Rectangle.Round(r), Align.Center);
        }

        protected override void Dispose(bool disposing) { if (disposing) { foreach (Bitmap b in new[] { pic, after, cBefore, cAfter }) if (b != null) b.Dispose(); } base.Dispose(disposing); }
    }

    /// Before and after (AI Scale): the two pictures on top of each other with a handle to slide across.
    /// Scroll to zoom in where the mouse is, drag to look around, double-click to see it all again.
    public class CompareView : UiControl
    {
        Bitmap before, after;
        float split = 0.5f;
        float zoom = 1;     // 1 = the whole picture fits
        PointF center;                  // the point of the picture in the middle of the view (picture pixels)
        int drag;                       // 0 none, 1 the handle, 2 looking around
        Point last;
        bool used;                      // zoomed or dragged once: the hint goes
        public string BeforeLabel = "", AfterLabel = "", Hint = "";

        public CompareView() { Surface = Surface.Card; TabStop = true; }

        public void Set(Bitmap b, Bitmap a)
        {
            if (before != null) before.Dispose();
            if (after != null) after.Dispose();
            before = b; after = a; split = 0.5f; zoom = 1; used = false;
            center = new PointF(a.Width / 2f, a.Height / 2f);
            Invalidate();
        }

        float Fit { get { return after == null ? 1 : Math.Min((Width - 2f) / after.Width, (Height - 2f) / after.Height); } }
        float Zm { get { return Fit * zoom; } }
        /// The most it zooms: each pixel of the result 4× bigger.
        float MaxZoom { get { return after == null ? 1 : Math.Max(1, 4f / Fit); } }

        PointF ToPic(PointF p) { float k = Zm; return new PointF(center.X + (p.X - Width / 2f) / k, center.Y + (p.Y - Height / 2f) / k); }

        void Clamp()
        {
            if (after == null) return;
            float k = Zm, hw = Width / 2f / k, hh = Height / 2f / k;
            float w = after.Width, h = after.Height;
            center.X = hw * 2 >= w ? w / 2 : Math.Max(hw, Math.Min(w - hw, center.X));
            center.Y = hh * 2 >= h ? h / 2 : Math.Max(hh, Math.Min(h - hh, center.Y));
        }

        float SplitX { get { return Width * split; } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (after == null) return;
            // the spot under the mouse stays under the mouse
            PointF before0 = ToPic(e.Location);
            float nz = Math.Max(1, Math.Min(MaxZoom, zoom * (float)Math.Pow(1.25, e.Delta / 120.0)));
            if (Math.Abs(nz - zoom) < 0.0001f) return;
            zoom = nz;
            PointF after0 = ToPic(e.Location);
            center = new PointF(center.X + before0.X - after0.X, center.Y + before0.Y - after0.Y);
            Clamp(); used = true;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (after == null || e.Button != MouseButtons.Left) return;
            Focus();
            drag = Math.Abs(e.X - SplitX) <= Theme.S(14) || zoom <= 1.001f ? 1 : 2;
            last = e.Location; Capture = true;
            if (drag == 1) { split = Math.Max(0.02f, Math.Min(0.98f, e.X / (float)Math.Max(1, Width))); Invalidate(); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (after == null) return;
            if (drag == 1) { split = Math.Max(0.02f, Math.Min(0.98f, e.X / (float)Math.Max(1, Width))); Invalidate(); return; }
            if (drag == 2)
            {
                float k = Zm;
                center = new PointF(center.X - (e.X - last.X) / k, center.Y - (e.Y - last.Y) / k);
                last = e.Location; Clamp(); used = true; Invalidate();
                return;
            }
            Cursor = Math.Abs(e.X - SplitX) <= Theme.S(14) || zoom <= 1.001f ? Cursors.SizeWE : Cursors.SizeAll;
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = 0; Capture = false; }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (after == null) return;
            // back to the whole picture (or, from there, a close look at that spot)
            if (zoom > 1.001f) { zoom = 1; center = new PointF(after.Width / 2f, after.Height / 2f); }
            else { PointF p = ToPic(e.Location); zoom = Math.Min(MaxZoom, 1 / Fit); center = p; Clamp(); used = true; }
            Invalidate();
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Clamp(); }

        /// Draws the part of a picture that's in view (b is drawn as if it were the result's size).
        void DrawPart(Graphics g, Bitmap b, RectangleF clip)
        {
            float k = Zm, rx = b.Width / (float)after.Width, ry = b.Height / (float)after.Height;
            PointF tl = ToPic(new PointF(clip.X, clip.Y)), br = ToPic(new PointF(clip.Right, clip.Bottom));
            RectangleF src = RectangleF.FromLTRB(Math.Max(0, tl.X), Math.Max(0, tl.Y), Math.Min(after.Width, br.X), Math.Min(after.Height, br.Y));
            if (src.Width <= 0 || src.Height <= 0) return;
            RectangleF dst = new RectangleF(Width / 2f + (src.X - center.X) * k, Height / 2f + (src.Y - center.Y) * k, src.Width * k, src.Height * k);
            RectangleF s2 = new RectangleF(src.X * rx, src.Y * ry, src.Width * rx, src.Height * ry);
            g.DrawImage(b, dst, s2, GraphicsUnit.Pixel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF box = new RectangleF(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, box, Theme.S(12), Theme.Mix(Theme.CardInner, Theme.Ink, 0.06f));
            if (after == null) return;
            using (GraphicsPath clip = Theme.Round(box, Theme.S(12))) g.SetClip(clip);
            // sharp pixels when looking very close, smooth otherwise (and while moving)
            bool close = Zm * after.Width / Math.Max(1, before.Width) > 3;
            g.InterpolationMode = drag != 0 ? InterpolationMode.Bilinear : close ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            float x = SplitX;
            g.SetClip(new RectangleF(0, 0, x, Height), CombineMode.Intersect);
            DrawPart(g, before, new RectangleF(0, 0, x, Height));
            using (GraphicsPath clip = Theme.Round(box, Theme.S(12))) g.SetClip(clip);
            g.SetClip(new RectangleF(x, 0, Width - x, Height), CombineMode.Intersect);
            g.InterpolationMode = drag != 0 ? InterpolationMode.Bilinear : Zm > 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            DrawPart(g, after, new RectangleF(x, 0, Width - x, Height));
            g.ResetClip();
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (Pen p = new Pen(Color.White, Math.Max(2f, Theme.S(2)))) g.DrawLine(p, x, 0, x, box.Height);
            float kk = Theme.S(30);
            RectangleF knob = new RectangleF(x - kk / 2, box.Height / 2 - kk / 2, kk, kk);
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) g.FillEllipse(sh, knob.X, knob.Y + 2, kk, kk);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, knob);
            VectorIcons.Draw(g, Ico.FlipH, 9f, Color.FromArgb(40, 36, 32), Rectangle.Round(knob));
            Pill(g, BeforeLabel, new PointF(Theme.S(10), Theme.S(10)), false);
            Pill(g, AfterLabel + (zoom > 1.001f ? "  ·  " + (int)Math.Round(Zm * 100) + "%" : ""), new PointF(box.Width - Theme.S(10), Theme.S(10)), true);
            if (!used && Hint.Length > 0)
            {
                Font f = Theme.F(8f, FontStyle.Bold);
                int tw = TextRenderer.MeasureText(Hint, f).Width + Theme.S(24);
                Rectangle hr = new Rectangle((int)(box.Width - tw) / 2, (int)box.Height - Theme.S(36), tw, Theme.S(26));
                Theme.FillRound(g, hr, hr.Height / 2f, Color.FromArgb(185, 20, 20, 22));
                Theme.Text(g, Hint, f, Color.White, hr, Align.Center);
            }
        }

        static void Pill(Graphics g, string text, PointF at, bool right)
        {
            if (string.IsNullOrEmpty(text)) return;
            Font f = Theme.F(8f, FontStyle.Bold);
            SizeF sz = g.MeasureString(text, f);
            RectangleF r = new RectangleF(right ? at.X - sz.Width - Theme.S(14) : at.X, at.Y, sz.Width + Theme.S(14), Theme.S(22));
            Theme.FillRound(g, r, Theme.S(11), Color.FromArgb(170, 20, 20, 22));
            Theme.Text(g, text, f, Color.White, Rectangle.Round(r), Align.Center);
        }

        protected override void Dispose(bool disposing) { if (disposing) { if (before != null) before.Dispose(); if (after != null) after.Dispose(); } base.Dispose(disposing); }
    }

    /// Reading a picture for the small windows (slow formats go through the image engine).
    static class PicLoad
    {
        /// Calls done(shown, fullSize, error) on the window's thread.
        public static void Start(MiniWindow w, string file, int maxSide, Action<Bitmap, Size, string> done)
        {
            Thread th = new Thread(() =>
            {
                Bitmap shown = null; Size full = Size.Empty; string err = null;
                try
                {
                    using (Image im = ViewerPage.Decode(file, 16000))
                    {
                        full = im.Size;
                        float k = Math.Min(1f, maxSide / (float)Math.Max(im.Width, im.Height));
                        shown = new Bitmap(Math.Max(1, (int)(im.Width * k)), Math.Max(1, (int)(im.Height * k)), PixelFormat.Format32bppPArgb);
                        using (Graphics g = Graphics.FromImage(shown)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(im, 0, 0, shown.Width, shown.Height); }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try { if (!w.IsDisposed) w.BeginInvoke((Action)(() => done(shown, full, err))); } catch { }
            });
            th.IsBackground = true; th.Start();
        }

        /// The format a new copy is saved in: the picture's own when Fadi Tools can write it, otherwise PNG.
        public static string OutExt(string file)
        {
            string e = Formats.Ext(file);
            if (e == "jpeg" || e == "jfif") return "jpg";
            if (e == "jpg" || e == "png" || e == "bmp" || e == "gif") return e;
            if (e == "tif") e = "tiff";
            return Tools.Magick != null && (e == "webp" || e == "avif" || e == "heic" || e == "tiff") ? e : "png";
        }

        /// A new copy made with the image engine (ops between input and output), or with Windows' own pictures.
        public static string Save(string file, string nameSuffix, string magickOps, Func<Image, Bitmap> gdi)
        {
            string ext = OutExt(file);
            string dst = Paths.UniquePath(DragWheel.OutDirFor(file), Path.GetFileNameWithoutExtension(file) + " " + nameSuffix, ext);
            string magick = Tools.Magick;
            if (magick != null)
            {
                string q = ext == "jpg" ? " -quality 95" : ext == "webp" || ext == "avif" || ext == "heic" ? " -quality 92" : "";
                string o;
                int code = Tools.RunHidden(magick, Tools.Quote(file + "[0]") + " -auto-orient " + magickOps + q + " " + Tools.Quote(dst), 300000, out o);
                if (code == 0 && File.Exists(dst)) return dst;
                if (ext != "jpg" && ext != "png" && ext != "bmp" && ext != "gif") throw new Exception(Tools.LastLines(o, 1));
            }
            using (Image im = ViewerPage.Decode(file, 16000))
            using (Bitmap b = gdi(im))
            {
                if (ext == "jpg") Compressor.SaveJpeg(b, dst, 95);
                else b.Save(dst, ext == "bmp" ? ImageFormat.Bmp : ext == "gif" ? ImageFormat.Gif : ImageFormat.Png);
            }
            return dst;
        }

        public static void Done(string dst)
        {
            if (Settings.OpenAfter && dst != null) try { Process.Start("explorer.exe", "/select,\"" + dst + "\""); } catch { }
        }
    }

    // ====================================================================== AI Scale

    /// Donut Wheel → AI Scale: 2×, 3× or 4× bigger, redrawn by AI (Real-ESRGAN) instead of stretched. Photo or Drawing.
    /// The first time, its engine is downloaded (one click). When it's done, the same spot is shown before and after.
    public class UpscaleWindow : MiniWindow
    {
        readonly string file;
        readonly PicView view = new PicView();
        readonly CompareView compare = new CompareView { Visible = false };
        readonly Segments scale, kind;
        readonly Progress bar = new Progress { Visible = false };
        readonly Btn bGo, bCancel, bShow;
        readonly Control activitySource;
        Size full;
        Rectangle scaleLbl, kindLbl, sizeR, statusR;
        string status; Color statusColor;
        bool busy, cancel, done;
        string result;

        public static void Open(string file, Point at, Control activitySource)
        {
            UpscaleWindow w = new UpscaleWindow(file, activitySource);
            w.ShowNear(at);
            w.view.Loading = true;
            PicLoad.Start(w, file, Theme.S(900), (b, sz, err) =>
            {
                if (err != null) { w.view.Message = err; w.view.Loading = false; w.view.Invalidate(); return; }
                w.full = sz; w.view.SetPicture(b); w.Refresh2();
            });
        }

        UpscaleWindow(string file, Control activitySource) : base(() => L.T("wh_upscale"), Ico.Upscale)
        {
            this.file = file; this.activitySource = activitySource;
            scale = new Segments(() => "2×", () => "3×", () => "4×") { Surface = Surface.Card };
            int si; scale.SelectedIndex = int.TryParse(Settings.Get("UpScale", "0"), out si) ? Math.Max(0, Math.Min(2, si)) : 0;
            scale.SelectedChanged += (s, e) => { Settings.Set("UpScale", scale.SelectedIndex.ToString()); Refresh2(); };
            kind = new Segments(() => L.T("up_photo"), () => L.T("up_drawing")) { Surface = Surface.Card, Glyphs = new[] { Ico.Photo, Ico.Edit } };
            kind.SelectedIndex = Settings.Get("UpKind", "0") == "1" ? 1 : 0;
            kind.SelectedChanged += (s, e) => Settings.Set("UpKind", kind.SelectedIndex.ToString());
            bGo = new Btn(() => L.T(done ? "wn_ok" : Upscaler.Ready ? "up_go" : "up_get_go"), Ico.Upscale, BtnKind.Primary) { Surface = Surface.Card };
            bGo.Click += (s, e) => { if (done) Dismiss(); else Go(); };
            bCancel = new Btn(() => L.T(busy ? "cancel" : "close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { if (busy) cancel = true; else Dismiss(); };
            bShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, Visible = false, FontSize = 9f };
            bShow.Click += (s, e) => { if (result != null) ShowInFolder(result); };
            Controls.AddRange(new Control[] { view, compare, scale, kind, bar, bGo, bCancel, bShow });
            ClientSize = new Size(Theme.S(520), Theme.S(640));
        }

        int Factor { get { return scale.SelectedIndex + 2; } }
        const int MaxSide = 16384;

        void Refresh2()
        {
            if (full.Width > 0 && Math.Max(full.Width, full.Height) * Factor > MaxSide)
            {
                // too big a picture for that: the biggest that fits
                for (int i = 2; i >= 0; i--) if (Math.Max(full.Width, full.Height) * (i + 2) <= MaxSide) { scale.SelectedIndex = i; break; }
            }
            DoLayout(); Invalidate();
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (view == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            int bottom = Theme.S(250);
            view.Bounds = M(new Rectangle(p, Head, W - p * 2, H - Head - bottom));
            compare.Bounds = view.Bounds;
            int y = view.Bottom + Theme.S(16);
            int half = (W - p * 2 - Theme.S(12)) / 2;
            scaleLbl = new Rectangle(p, y, half, Theme.S(20));
            kindLbl = new Rectangle(p + half + Theme.S(12), y, half, Theme.S(20));
            scale.Bounds = M(new Rectangle(p, y + Theme.S(24), half, Theme.S(36)));
            kind.Bounds = M(new Rectangle(p + half + Theme.S(12), y + Theme.S(24), half, Theme.S(36)));
            y += Theme.S(70);
            sizeR = new Rectangle(p, y, W - p * 2, Theme.S(26));
            bar.Bounds = M(new Rectangle(p, y + Theme.S(34), W - p * 2, Theme.S(6)));
            statusR = new Rectangle(p, y + Theme.S(42), W - p * 2, Theme.S(40));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(130), bGo.PreferredWidth + Theme.S(12)), cw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - cw, by, cw, Theme.S(40)));
            bShow.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(110), bShow.PreferredWidth), Theme.S(34)));
        }

        protected override void PaintBody(Graphics g)
        {
            Theme.Text(g, L.T("up_scale"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(scaleLbl), Align.Start);
            Theme.Text(g, L.T("up_kind"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(kindLbl), Align.Start);
            if (full.Width > 0)
                Theme.Text(g, full.Width + " × " + full.Height + "   →   " + full.Width * Factor + " × " + full.Height * Factor + " px", Theme.F(10f, FontStyle.Bold), Theme.Ink, M(sizeR), Align.Start, true, false, true);
            string s = status ?? (Upscaler.Ready ? L.T("up_note") : L.T("up_note_get"));
            Theme.Text(g, s, Theme.F(8.75f), status != null ? statusColor : Theme.InkSoft, M(statusR), Align.Start, false, true);
        }

        protected override bool CanClose() { if (busy) { cancel = true; return false; } return true; }

        void Go()
        {
            if (busy || done || full.Width <= 0) return;
            busy = true; cancel = false;
            scale.Enabled = kind.Enabled = bGo.Enabled = false; bCancel.Invalidate();
            bar.Visible = true; bar.Value = 0;
            int factor = Factor; bool drawing = kind.SelectedIndex == 1;
            string dst = Paths.UniquePath(DragWheel.OutDirFor(file), Path.GetFileNameWithoutExtension(file) + " " + L.T("up_suffix", factor), PicLoad.OutExt(file));
            ActJob act = Activity.Begin(activitySource, L.T("wh_upscale"), Path.GetFileName(file), () => cancel = true);
            Thread th = new Thread(() =>
            {
                string err = null;
                try
                {
                    if (!Upscaler.Ready)
                    {
                        // the engine first (one time)
                        Ui(() => { status = L.T("up_getting"); statusColor = Theme.InkSoft; Invalidate(); });
                        Upscaler.Install((got, total) => { double v = total > 0 ? (double)got / total : 0; Ui(() => { bar.Value = v * 0.3; status = L.T("up_getting_n", Theme.HumanSize(got), total > 0 ? Theme.HumanSize(total) : "?"); Invalidate(); }); }, () => cancel);
                    }
                    if (!cancel)
                    {
                        Ui(() => { status = L.T("up_working"); Invalidate(); bGo.Invalidate(); });
                        double from = bar.Value;
                        err = Upscaler.Run(file, dst, factor, drawing, v =>
                        {
                            double all = from + (1 - from) * v;
                            Activity.Update(act, all, (int)(v * 100) + "%");
                            Ui(() => { bar.Value = all; });
                        }, () => cancel);
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                if (cancel) { try { if (File.Exists(dst)) File.Delete(dst); } catch { } }
                Bitmap[] cmp = null;
                if (err == null && !cancel) MakeComparison(file, dst, out cmp);
                Activity.End(act, cancel ? null : err, cancel, err == null ? L.T("up_done", factor) : null, err == null ? dst : null);
                Ui(() =>
                {
                    busy = false; bar.Visible = false;
                    if (cancel) { status = L.T("cancelled"); statusColor = Theme.InkSoft; scale.Enabled = kind.Enabled = bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); return; }
                    if (err != null) { status = err; statusColor = Theme.Error; scale.Enabled = kind.Enabled = bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); bGo.Invalidate(); return; }
                    done = true; result = dst;
                    long sz = 0; try { sz = new FileInfo(dst).Length; } catch { }
                    status = L.T("up_saved", Path.GetFileName(dst), Theme.HumanSize(sz)); statusColor = Theme.Success;
                    if (cmp != null)
                    {
                        compare.BeforeLabel = L.T("up_before"); compare.AfterLabel = L.T("up_after"); compare.Hint = L.T("up_zoom_hint");
                        compare.Set(cmp[0], cmp[1]);
                        compare.Bounds = view.Bounds; compare.Visible = true; view.Visible = false;
                        // more room to look at it: the window grows (inside the screen), around its middle
                        Rectangle wa = Screen.FromControl(this).WorkingArea;
                        int nw = Math.Min(wa.Width - Theme.S(40), Theme.S(820)), nh = Math.Min(wa.Height - Theme.S(40), Theme.S(860));
                        if (nw > Width || nh > Height)
                        {
                            Point c = new Point(Left + Width / 2, Top + Height / 2);
                            Bounds = new Rectangle(Math.Max(wa.Left + 10, Math.Min(wa.Right - nw - 10, c.X - nw / 2)), Math.Max(wa.Top + 10, Math.Min(wa.Bottom - nh - 10, c.Y - nh / 2)), nw, nh);
                        }
                    }
                    bGo.Enabled = true; bGo.Glyph = Ico.Check; bCancel.Visible = false; bShow.Visible = true;
                    DoLayout(); Invalidate(); bGo.Invalidate();
                    PicLoad.Done(dst);
                });
            });
            th.IsBackground = true; th.Start();
        }

        /// The original as it is, and the result (up to 4096 px on its long side), for looking at closely.
        static void MakeComparison(string src, string dst, out Bitmap[] pair)
        {
            pair = null;
            Bitmap b0 = null, b1 = null;
            try
            {
                using (Image a = ViewerPage.Decode(src, 16000)) b0 = new Bitmap(a);
                using (Image b = ViewerPage.Decode(dst, 4096))
                {
                    float k = Math.Min(1f, 4096f / Math.Max(b.Width, b.Height));
                    int w = Math.Max(1, (int)(b.Width * k)), h = Math.Max(1, (int)(b.Height * k));
                    b1 = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(b1)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(b, 0, 0, w, h); }
                }
                pair = new[] { b0, b1 };
            }
            catch { if (b0 != null) b0.Dispose(); if (b1 != null) b1.Dispose(); pair = null; }
        }
    }

    // ====================================================================== Resize

    /// Donut Wheel → Resize: width and height (kept in shape unless the chain is undone), or a quick percentage.
    public class ResizeWindow : MiniWindow
    {
        readonly string file;
        readonly PicView view = new PicView();
        readonly TextField wBox, hBox;
        readonly Btn bLock, bGo, bCancel;
        readonly Segments quick;
        readonly Control activitySource;
        Size full;
        bool locked = true, typing, busy;
        Rectangle sizeLbl, quickLbl, newR, statusR;
        string status; Color statusColor;
        static readonly int[] Pcts = { 25, 50, 75, 150, 200 };

        public static void Open(string file, Point at, Control activitySource)
        {
            ResizeWindow w = new ResizeWindow(file, activitySource);
            w.ShowNear(at);
            w.view.Loading = true;
            PicLoad.Start(w, file, Theme.S(700), (b, sz, err) =>
            {
                if (err != null) { w.view.Message = err; w.view.Loading = false; w.view.Invalidate(); return; }
                w.full = sz; w.view.SetPicture(b);
                w.SetSize(sz.Width / 2, sz.Height / 2);
                w.quick.SelectedIndex = 1;
                w.bGo.Enabled = true;
            });
        }

        ResizeWindow(string file, Control activitySource) : base(() => L.T("wh_resize"), Ico.Scale)
        {
            this.file = file; this.activitySource = activitySource;
            wBox = new TextField(() => "", null) { Surface = Surface.Card, ForceLtr = true };
            hBox = new TextField(() => "", null) { Surface = Surface.Card, ForceLtr = true };
            wBox.Box.TextChanged += (s, e) => Typed(true);
            hBox.Box.TextChanged += (s, e) => Typed(false);
            bLock = new Btn(() => "", Ico.Link, BtnKind.Ghost) { Surface = Surface.Card, Active = true, AccessibleName = L.T("rs_keep_shape") };
            bLock.Click += (s, e) => { locked = !locked; bLock.Active = locked; bLock.Invalidate(); if (locked) Typed(true); };
            Func<string>[] q = new Func<string>[Pcts.Length];
            for (int i = 0; i < Pcts.Length; i++) { int v = Pcts[i]; q[i] = () => v + "%"; }
            quick = new Segments(q) { Surface = Surface.Card };
            quick.SelectedChanged += (s, e) =>
            {
                if (typing || full.Width <= 0 || quick.SelectedIndex < 0) return;
                int pc = Pcts[quick.SelectedIndex];
                SetSize((int)Math.Round(full.Width * pc / 100.0), (int)Math.Round(full.Height * pc / 100.0));
            };
            bGo = new Btn(() => L.T("rs_go"), Ico.Scale, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bGo.Click += (s, e) => Go();
            bCancel = new Btn(() => L.T("close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => Dismiss();
            wBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(); } };
            hBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Go(); } };
            Controls.AddRange(new Control[] { view, wBox, hBox, bLock, quick, bGo, bCancel });
            ClientSize = new Size(Theme.S(480), Theme.S(560));
        }

        void SetSize(int w, int h)
        {
            typing = true;
            wBox.Box.Text = Math.Max(1, w).ToString(CultureInfo.InvariantCulture);
            hBox.Box.Text = Math.Max(1, h).ToString(CultureInfo.InvariantCulture);
            typing = false;
            Invalidate();
        }

        bool Read(out int w, out int h)
        {
            bool a = int.TryParse(wBox.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out w);
            bool b = int.TryParse(hBox.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out h);
            return a && b && w > 0 && h > 0 && w <= 30000 && h <= 30000;
        }

        /// A number typed in one box: the other follows when the shape is kept.
        void Typed(bool width)
        {
            if (typing || full.Width <= 0) return;
            typing = true;
            if (quick.SelectedIndex >= 0) quick.SetSilently(-1);
            int v;
            if (locked && int.TryParse((width ? wBox : hBox).Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0)
            {
                if (width) hBox.Box.Text = Math.Max(1, (int)Math.Round(v * (double)full.Height / full.Width)).ToString(CultureInfo.InvariantCulture);
                else wBox.Box.Text = Math.Max(1, (int)Math.Round(v * (double)full.Width / full.Height)).ToString(CultureInfo.InvariantCulture);
            }
            typing = false;
            Invalidate();
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (view == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            view.Bounds = M(new Rectangle(p, Head, W - p * 2, H - Head - Theme.S(276)));
            int y = view.Bottom + Theme.S(16);
            quickLbl = new Rectangle(p, y, W - p * 2, Theme.S(20));
            quick.Bounds = M(new Rectangle(p, y + Theme.S(24), W - p * 2, Theme.S(36)));
            y += Theme.S(72);
            int bw = Theme.S(104);
            sizeLbl = new Rectangle(p, y, Theme.S(50), Theme.S(36));
            wBox.Bounds = M(new Rectangle(p + Theme.S(50), y, bw, Theme.S(36)));
            bLock.Bounds = M(new Rectangle(p + Theme.S(50) + bw + Theme.S(4), y + Theme.S(2), Theme.S(32), Theme.S(32)));
            hBox.Bounds = M(new Rectangle(p + Theme.S(50) + bw + Theme.S(40), y, bw, Theme.S(36)));
            y += Theme.S(46);
            newR = new Rectangle(p, y, W - p * 2, Theme.S(24));
            statusR = new Rectangle(p, y + Theme.S(24), W - p * 2, Theme.S(36));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(120), bGo.PreferredWidth + Theme.S(12)), cw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - cw, by, cw, Theme.S(40)));
        }

        protected override void PaintBody(Graphics g)
        {
            Theme.Text(g, L.T("rs_quick"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(quickLbl), Align.Start);
            Theme.Text(g, L.T("crop_size"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(sizeLbl), Align.Start);
            int w, h;
            if (full.Width > 0 && Read(out w, out h))
            {
                int pc = (int)Math.Round(100.0 * w / full.Width);
                Theme.Text(g, full.Width + " × " + full.Height + "   →   " + w + " × " + h + " px   (" + pc + "%)", Theme.F(9.5f, FontStyle.Bold), Theme.Ink, M(newR), Align.Start, true, false, true);
            }
            if (status != null) Theme.Text(g, status, Theme.F(8.75f), statusColor, M(statusR), Align.Start, true, true);
        }

        protected override bool CanClose() { return !busy; }

        void Go()
        {
            int w, h;
            if (busy || full.Width <= 0) return;
            if (!Read(out w, out h)) { status = L.T("rs_bad"); statusColor = Theme.Error; Invalidate(); return; }
            busy = true; bGo.Enabled = false;
            status = L.T("saving"); statusColor = Theme.InkSoft; Invalidate();
            ActJob act = Activity.Begin(activitySource, L.T("wh_resize"), Path.GetFileName(file), null);
            Thread th = new Thread(() =>
            {
                string dst = null, err = null;
                try
                {
                    dst = PicLoad.Save(file, w + "x" + h, "-filter Lanczos -resize " + w + "x" + h + "!", im =>
                    {
                        Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(im, 0, 0, w, h); }
                        return b;
                    });
                }
                catch (Exception ex) { err = ex.Message; }
                Activity.End(act, err, false, err == null ? L.T("rs_done", w, h) : null, dst);
                Ui(() =>
                {
                    busy = false;
                    if (err != null) { status = err; statusColor = Theme.Error; bGo.Enabled = true; Invalidate(); return; }
                    PicLoad.Done(dst);
                    Dismiss();
                });
            });
            th.IsBackground = true; th.Start();
        }
    }

    // ====================================================================== Rotate

    /// Donut Wheel → Rotate: turn left or right and flip; the picture turns as you click, then Save.
    public class RotateWindow : MiniWindow
    {
        readonly string file;
        readonly PicView view = new PicView();
        readonly Btn bLeft, bRight, bFlipH, bFlipV, bReset, bGo, bCancel;
        readonly Control activitySource;
        Size full;
        bool busy;
        Rectangle statusR;
        string status; Color statusColor;

        public static void Open(string file, Point at, Control activitySource)
        {
            RotateWindow w = new RotateWindow(file, activitySource);
            w.ShowNear(at);
            w.view.Loading = true;
            PicLoad.Start(w, file, Theme.S(800), (b, sz, err) =>
            {
                if (err != null) { w.view.Message = err; w.view.Loading = false; w.view.Invalidate(); return; }
                w.full = sz; w.view.SetPicture(b); w.Sync();
            });
        }

        RotateWindow(string file, Control activitySource) : base(() => L.T("wh_rotate"), Ico.Rotate)
        {
            this.file = file; this.activitySource = activitySource;
            bLeft = Tool("rt_left", "", () => view.Turn(-90));
            bRight = Tool("rt_right", Ico.Rotate, () => view.Turn(90));
            bFlipH = Tool("rt_flip_h", Ico.FlipH, () => view.Flip(true));
            bFlipV = Tool("rt_flip_v", Ico.FlipV, () => view.Flip(false));
            bReset = new Btn(() => L.T("crop_reset"), Ico.Refresh, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 9f };
            bReset.Click += (s, e) => { view.ResetTurn(); Sync(); };
            bGo = new Btn(() => L.T("rt_go"), Ico.Save, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bGo.Click += (s, e) => Go();
            bCancel = new Btn(() => L.T("close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => Dismiss();
            Controls.AddRange(new Control[] { view, bLeft, bRight, bFlipH, bFlipV, bReset, bGo, bCancel });
            ClientSize = new Size(Theme.S(500), Theme.S(560));
        }

        Btn Tool(string key, string glyph, Action a)
        {
            Btn b = new Btn(() => L.T(key), glyph, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 8.75f };
            b.Click += (s, e) => { a(); Sync(); };
            return b;
        }

        int Turned { get { return ((view.Angle % 360) + 360) % 360; } }
        bool Changed { get { return Turned != 0 || view.FlipX || view.FlipY; } }
        void Sync() { bGo.Enabled = !busy && full.Width > 0 && Changed; bReset.Visible = Changed; bGo.Invalidate(); Invalidate(); }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (view == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            view.Bounds = M(new Rectangle(p, Head, W - p * 2, H - Head - Theme.S(170)));
            int y = view.Bottom + Theme.S(16);
            int bw = (W - p * 2 - Theme.S(24)) / 4;
            Btn[] row = { bLeft, bRight, bFlipH, bFlipV };
            for (int i = 0; i < 4; i++) row[i].Bounds = M(new Rectangle(p + i * (bw + Theme.S(8)), y, bw, Theme.S(38)));
            statusR = new Rectangle(p, y + Theme.S(44), W - p * 2, Theme.S(28));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(110), bGo.PreferredWidth + Theme.S(12)), cw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - cw, by, cw, Theme.S(40)));
            bReset.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(100), bReset.PreferredWidth), Theme.S(34)));
        }

        protected override void PaintBody(Graphics g)
        {
            string s = status;
            if (s == null && full.Width > 0)
            {
                bool side = Turned % 180 == 90;
                s = (side ? full.Height : full.Width) + " × " + (side ? full.Width : full.Height) + " px";
            }
            if (s != null) Theme.Text(g, s, Theme.F(8.75f), status != null ? statusColor : Theme.InkSoft, M(statusR), Align.Center, true, true);
        }

        protected override bool CanClose() { return !busy; }

        void Go()
        {
            if (busy || !Changed) return;
            int deg = Turned; bool fx = view.FlipX, fy = view.FlipY;
            busy = true; Sync();
            status = L.T("saving"); statusColor = Theme.InkSoft; Invalidate();
            ActJob act = Activity.Begin(activitySource, L.T("wh_rotate"), Path.GetFileName(file), null);
            Thread th = new Thread(() =>
            {
                string dst = null, err = null;
                try
                {
                    // the picture is turned first, then mirrored (the way it looked in the window)
                    string ops = (deg != 0 ? "-rotate " + deg + " " : "") + (fx ? "-flop " : "") + (fy ? "-flip" : "");
                    dst = PicLoad.Save(file, L.T("rt_suffix"), ops.Trim(), im =>
                    {
                        Bitmap b = new Bitmap(im);
                        RotateFlipType t = deg == 90 ? RotateFlipType.Rotate90FlipNone : deg == 180 ? RotateFlipType.Rotate180FlipNone : deg == 270 ? RotateFlipType.Rotate270FlipNone : RotateFlipType.RotateNoneFlipNone;
                        b.RotateFlip(t);
                        if (fx) b.RotateFlip(RotateFlipType.RotateNoneFlipX);
                        if (fy) b.RotateFlip(RotateFlipType.RotateNoneFlipY);
                        return b;
                    });
                }
                catch (Exception ex) { err = ex.Message; }
                Activity.End(act, err, false, err == null ? L.T("rt_done") : null, dst);
                Ui(() =>
                {
                    busy = false;
                    if (err != null) { status = err; statusColor = Theme.Error; Sync(); return; }
                    PicLoad.Done(dst);
                    Dismiss();
                });
            });
            th.IsBackground = true; th.Start();
        }
    }

    // ====================================================================== Trim

    /// Donut Wheel → Trim (videos and songs): a strip of frames (or the sound's waveform) with a start and an end
    /// to drag, a look at the frame where you are, and Play to hear or see the part you kept.
    public class TrimWindow : MiniWindow
    {
        readonly string file;
        readonly PicView frame = new PicView();
        readonly TrimStrip strip = new TrimStrip();
        readonly Toggle quickCut = new Toggle();
        readonly Progress bar = new Progress { Visible = false };
        readonly Btn bPlay, bGo, bCancel, bShow;
        readonly Control activitySource;
        MediaInfo info;
        bool busy, cancel, done, video;
        Rectangle timesR, quickR, statusR;
        string status; Color statusColor;
        string result;
        int frameAsk; double frameAt = -1;

        public static void Open(string file, Point at, Control activitySource)
        {
            TrimWindow w = new TrimWindow(file, activitySource);
            w.ShowNear(at);
            w.Load2();
        }

        TrimWindow(string file, Control activitySource) : base(() => L.T("wh_trim"), Ico.Trim)
        {
            this.file = file; this.activitySource = activitySource;
            video = Formats.CategoryOf(Formats.Ext(file)) == Category.Video;
            frame.Loading = true;
            strip.Changed += (which) => { Invalidate(); bGo.Enabled = !busy && info != null && strip.End - strip.Start >= 0.1; if (video) AskFrame(which == 1 ? strip.End : strip.Start); };
            bPlay = new Btn(() => L.T("tr_play"), Ico.Play, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f, Enabled = false };
            bPlay.Click += (s, e) => { if (info != null) try { Media.Play(file, strip.Start, strip.End - strip.Start, info.HasVideo); } catch { } };
            quickCut.SetSilently(Settings.Get("TrimQuick", "0") == "1");
            quickCut.CheckedChanged += (s, e) => Settings.Set("TrimQuick", quickCut.Checked ? "1" : "0");
            bGo = new Btn(() => L.T(done ? "wn_ok" : "tr_go"), Ico.Trim, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bGo.Click += (s, e) => { if (done) Dismiss(); else Go(); };
            bCancel = new Btn(() => L.T(busy ? "cancel" : "close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { if (busy) cancel = true; else Dismiss(); };
            bShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, Visible = false, FontSize = 9f };
            bShow.Click += (s, e) => { if (result != null) ShowInFolder(result); };
            Controls.AddRange(new Control[] { frame, strip, bPlay, quickCut, bar, bGo, bCancel, bShow });
            if (!video) frame.Visible = false;
            ClientSize = new Size(Theme.S(620), video ? Theme.S(640) : Theme.S(380));
        }

        void Load2()
        {
            Thread th = new Thread(() =>
            {
                MediaInfo mi = null; Image strp = null, first = null; string err = null;
                try
                {
                    mi = Media.Probe(file);
                    if (mi == null || mi.Duration <= 0) throw new Exception(L.T("tr_no_length"));
                    if (video)
                    {
                        strp = Strip(file, mi.Duration, 14, Theme.S(54));
                        first = Media.Frame(file, Math.Min(1, mi.Duration / 2), Theme.S(360));
                    }
                    else strp = Media.Waveform(file, Theme.S(560), Theme.S(54), Theme.AccentColor);
                }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    if (err != null) { status = err; statusColor = Theme.Error; frame.Message = err; frame.Loading = false; frame.Invalidate(); Invalidate(); return; }
                    info = mi;
                    strip.SetMedia(strp, mi.Duration, video);
                    if (first != null) frame.SetPicture(new Bitmap(first));
                    if (first != null) first.Dispose();
                    bPlay.Enabled = true; bGo.Enabled = true;
                    Invalidate();
                });
            });
            th.IsBackground = true; th.Start();
        }

        /// A row of small frames across the whole video, made in one go.
        static Image Strip(string f, double dur, int n, int h)
        {
            string ff = Tools.FFmpeg; if (ff == null) return null;
            string rate = (n / Math.Max(0.1, dur)).ToString("0.######", CultureInfo.InvariantCulture);
            byte[] png = Media.Capture(ff, "-hide_banner -loglevel error -i " + Tools.Quote(f) + " -vf \"fps=" + rate + ",scale=-2:" + h + ",tile=" + n + "x1\" -frames:v 1 -f image2pipe -vcodec png -", 120000);
            if (png == null) return null;
            try { return Image.FromStream(new MemoryStream(png)); } catch { return null; }
        }

        /// The frame at a time, shown above (only the newest ask counts, so dragging stays smooth).
        void AskFrame(double t)
        {
            if (Math.Abs(t - frameAt) < 0.04) return;
            frameAt = t;
            int ask = ++frameAsk;
            Thread th = new Thread(() =>
            {
                Thread.Sleep(90);
                if (ask != frameAsk) return;
                Image im = null;
                try { im = Media.Frame(file, t, Theme.S(360)); } catch { }
                Ui(() => { if (ask == frameAsk && im != null) frame.SetPicture(new Bitmap(im)); if (im != null) im.Dispose(); });
            });
            th.IsBackground = true; th.Start();
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (strip == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            int y = Head;
            if (video) { frame.Bounds = M(new Rectangle(p, y, W - p * 2, H - Head - Theme.S(300))); y = frame.Bottom + Theme.S(14); }
            strip.Bounds = M(new Rectangle(p, y, W - p * 2, Theme.S(84)));
            y += Theme.S(90);
            timesR = new Rectangle(p, y, W - p * 2 - Theme.S(140), Theme.S(36));
            int pw = Math.Max(Theme.S(120), bPlay.PreferredWidth);
            bPlay.Bounds = M(new Rectangle(W - p - pw, y, pw, Theme.S(36)));
            y += Theme.S(44);
            quickR = new Rectangle(p, y, W - p * 2 - Theme.S(60), Theme.S(34));
            quickCut.Bounds = M(new Rectangle(W - p - Theme.S(52), y, Theme.S(52), Theme.S(34)));
            y += Theme.S(40);
            bar.Bounds = M(new Rectangle(p, y + Theme.S(2), W - p * 2, Theme.S(6)));
            statusR = new Rectangle(p, y + Theme.S(10), W - p * 2, Theme.S(30));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(120), bGo.PreferredWidth + Theme.S(12)), cw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - cw, by, cw, Theme.S(40)));
            bShow.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(110), bShow.PreferredWidth), Theme.S(34)));
        }

        static string Clock(double s)
        {
            s = Math.Max(0, s);
            int m = (int)(s / 60); double r = s - m * 60;
            return m + ":" + r.ToString("00.0", CultureInfo.InvariantCulture);
        }

        protected override void PaintBody(Graphics g)
        {
            if (info != null)
                Theme.Text(g, Clock(strip.Start) + "  →  " + Clock(strip.End) + "     ·     " + L.T("tr_length", Clock(strip.End - strip.Start)), Theme.F(9.5f, FontStyle.Bold), Theme.Ink, M(timesR), Align.Start, true, false, true);
            Theme.Text(g, L.T("tr_quick"), Theme.F(9f), Theme.Ink, M(quickR), Align.Start, true, true);
            if (status != null) Theme.Text(g, status, Theme.F(9f, done ? FontStyle.Bold : FontStyle.Regular), statusColor, M(statusR), Align.Start, true, true);
        }

        protected override bool CanClose() { if (busy) { cancel = true; return false; } return true; }

        void Go()
        {
            if (busy || done || info == null) return;
            double s0 = strip.Start, s1 = strip.End;
            if (s1 - s0 < 0.1) return;
            busy = true; cancel = false;
            bGo.Enabled = false; strip.Enabled = false; bCancel.Invalidate();
            bar.Visible = true; bar.Value = 0;
            status = L.T("tr_working"); statusColor = Theme.InkSoft; Invalidate();
            bool precise = !quickCut.Checked;
            ActJob act = Activity.Begin(activitySource, L.T("wh_trim"), Path.GetFileName(file), () => cancel = true);
            Thread th = new Thread(() =>
            {
                string err = null, outp = null;
                try
                {
                    Job job = Media.BuildTrim(file, info, s0, s1, TrimMode.Keep, precise, DragWheel.OutDirFor(file));
                    outp = job.Output;
                    err = Engine.Run(job, p => { Activity.Update(act, p, (int)(p * 100) + "%"); Ui(() => bar.Value = p); }, () => cancel);
                }
                catch (Exception ex) { err = ex.Message; }
                if (cancel && outp != null) try { if (File.Exists(outp)) File.Delete(outp); } catch { }
                Activity.End(act, cancel ? null : err, cancel, err == null ? L.T("tr_done", Clock(s1 - s0)) : null, err == null ? outp : null);
                Ui(() =>
                {
                    busy = false; bar.Visible = false; strip.Enabled = true;
                    if (cancel) { status = L.T("cancelled"); statusColor = Theme.InkSoft; bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); return; }
                    if (err != null) { status = err; statusColor = Theme.Error; bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); return; }
                    done = true; result = outp;
                    long sz = 0; try { sz = new FileInfo(outp).Length; } catch { }
                    status = L.T("up_saved", Path.GetFileName(outp), Theme.HumanSize(sz)); statusColor = Theme.Success;
                    bGo.Enabled = true; bGo.Glyph = Ico.Check; bCancel.Visible = false; bShow.Visible = true;
                    DoLayout(); Invalidate(); bGo.Invalidate();
                    PicLoad.Done(outp);
                });
            });
            th.IsBackground = true; th.Start();
        }
    }

    /// The strip under a trimmed video: frames (or the waveform), the part kept in the clear between two handles,
    /// the rest dimmed. Drag a handle, or the kept part to slide it.
    public class TrimStrip : UiControl
    {
        Image pic;
        double dur;
        bool video;
        public double Start, End;
        int grab = -1; double grabT, grabS, grabE;
        float hotHandle = -1;
        public event Action<int> Changed;   // 0 start, 1 end, 2 both

        public TrimStrip() { Surface = Surface.Card; Cursor = Cursors.Hand; }

        public void SetMedia(Image strip, double duration, bool isVideo)
        {
            pic = strip; dur = duration; video = isVideo;
            Start = 0; End = duration;
            Invalidate();
            if (Changed != null) Changed(2);
        }

        Rectangle Track { get { return new Rectangle(Theme.S(10), Theme.S(22), Width - Theme.S(20), Height - Theme.S(30)); } }
        float X(double t) { Rectangle r = Track; float x = (float)(r.X + r.Width * (dur > 0 ? t / dur : 0)); return Theme.RTL ? Width - x : x; }
        double T(int x) { Rectangle r = Track; if (Theme.RTL) x = Width - x; return Math.Max(0, Math.Min(dur, (x - r.X) / (double)Math.Max(1, r.Width) * dur)); }

        int Hit(Point p)
        {
            float tol = Theme.S(12);
            if (Math.Abs(p.X - X(Start)) <= tol) return 0;
            if (Math.Abs(p.X - X(End)) <= tol) return 1;
            float a = Math.Min(X(Start), X(End)), b = Math.Max(X(Start), X(End));
            if (p.X > a && p.X < b) return 2;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (dur <= 0 || !Enabled || e.Button != MouseButtons.Left) return;
            grab = Hit(e.Location);
            if (grab < 0)
            {
                // a click outside the kept part moves the nearer end there
                double t = T(e.X);
                grab = Math.Abs(t - Start) < Math.Abs(t - End) ? 0 : 1;
                Move2(e.X);
            }
            grabT = T(e.X); grabS = Start; grabE = End;
            Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (grab >= 0) { Move2(e.X); return; }
            int h = Hit(e.Location);
            Cursor = h == 0 || h == 1 ? Cursors.SizeWE : h == 2 ? Cursors.SizeAll : Cursors.Hand;
            float hh = h == 0 || h == 1 ? h : -1;
            if (hh != hotHandle) { hotHandle = hh; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); grab = -1; Capture = false; Invalidate(); }

        void Move2(int x)
        {
            double t = T(x), min = Math.Min(0.1, dur / 2);
            if (grab == 0) Start = Math.Max(0, Math.Min(End - min, t));
            else if (grab == 1) End = Math.Min(dur, Math.Max(Start + min, t));
            else if (grab == 2)
            {
                double d = t - grabT, len = grabE - grabS;
                Start = Math.Max(0, Math.Min(dur - len, grabS + d)); End = Start + len;
            }
            Invalidate();
            if (Changed != null) Changed(grab);
        }

        static string Clock(double s) { int m = (int)(s / 60); return m + ":" + (s - m * 60).ToString("00.0", CultureInfo.InvariantCulture); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Rectangle r = Track;
            Theme.FillRound(g, r, Theme.S(8), Theme.CardInner);
            if (pic != null)
            {
                using (GraphicsPath clip = Theme.Round(r, Theme.S(8)))
                {
                    g.SetClip(clip);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    if (video) g.DrawImage(pic, r);
                    else g.DrawImage(pic, new Rectangle(r.X, r.Y + Theme.S(4), r.Width, r.Height - Theme.S(8)));
                    g.ResetClip();
                }
            }
            if (dur <= 0) return;
            float a = Math.Min(X(Start), X(End)), b = Math.Max(X(Start), X(End));
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(Theme.Dark ? 170 : 150, Theme.Dark ? 10 : 250, Theme.Dark ? 10 : 248, Theme.Dark ? 12 : 245)))
            {
                g.FillRectangle(dim, r.X, r.Y, a - r.X, r.Height);
                g.FillRectangle(dim, b, r.Y, r.Right - b, r.Height);
            }
            Color acc = Theme.AccentColor;
            using (Pen fp = new Pen(acc, Math.Max(2f, Theme.S(3)))) { g.DrawLine(fp, a, r.Y + 1, b, r.Y + 1); g.DrawLine(fp, a, r.Bottom - 1, b, r.Bottom - 1); }
            float hw = Theme.S(10);
            for (int i = 0; i < 2; i++)
            {
                float x = i == 0 ? a : b;
                RectangleF hr = new RectangleF(i == 0 ? x - hw : x, r.Y - Theme.S(2), hw, r.Height + Theme.S(4));
                Theme.FillRound(g, hr, Theme.S(4), hotHandle == i || grab == i ? Theme.Mix(acc, Color.White, 0.15f) : acc);
                using (Pen gp = new Pen(Theme.PrimaryInk, 1.5f)) g.DrawLine(gp, hr.X + hw / 2, hr.Y + hr.Height / 2 - Theme.S(6), hr.X + hw / 2, hr.Y + hr.Height / 2 + Theme.S(6));
                // the time over each handle
                string tt = Clock(i == 0 ? Start : End);
                Rectangle tr = new Rectangle(Math.Max(0, Math.Min(Width - Theme.S(80), (int)x - Theme.S(40))), 0, Theme.S(80), Theme.S(20));
                Theme.Text(g, tt, Theme.F(8f, FontStyle.Bold), Theme.InkSoft, tr, Align.Center);
            }
        }
    }
}
