using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Image surface with fit-to-window, Ctrl+wheel zoom, drag to pan and rotation.
    /// In edit mode it also draws text layers and lets you drag them around.
    public class ImageCanvas : UiControl
    {
        public Image Image;
        public string Message;
        public int Rotation;
        public float Zoom;              // 0 = fit
        public bool Dark;               // video look: dark stage
        public string Caption;          // small label in the corner (e.g. "Start 0:01.2")
        public bool Loading;
        PointF pan; Point dragFrom; PointF panFrom; bool dragging;
        public event Action Clicked;
        public event Action<int> Navigate;

        // edit mode
        public List<TextLayer> Layers;   // non-null while editing
        public Func<TextLayer, bool> LayerShown;   // video: only the texts on screen at the current time
        bool Shown(int i) { return LayerShown == null || LayerShown(Layers[i]); }
        public SizeF Logical;            // output size (layer coordinates)
        public int SelectedLayer = -1;
        public event Action<int> LayerPicked;
        public event Action LayerEdit, LayerMoved, LayerResized;
        int resizeCorner = -1; float resizeStartDist, resizeStartSize; PointF resizeCenter;
        int resizeSide = -1;   // a photo's side handle being dragged: 0 left, 1 right, 2 top, 3 bottom
        SizeF resizeStartBox;
        bool layerDrag; PointF layerFrom, layerPosFrom; bool snapX, snapY;

        // crop mode: a rectangle in output coordinates with corner handles
        public bool CropMode;
        public RectangleF CropRect;
        float cropAspect; int cropDrag = -1; PointF cropFrom; RectangleF cropStart;
        // video: click on the picture plays/pauses; a play badge shows while paused
        public event Action BackgroundClick;
        public bool ShowPlay;
        bool clickMoved; Point downAt;
        // background brush: 0 erase, 1 keep (paint strokes), 2 magic wand (click); -1 off
        public int BrushTool = -1;
        public float BrushPx = 40;                       // brush diameter on screen
        public event Action<List<PointF>, float, int> Stroke;   // points and radius in output coordinates
        public event Action<PointF, bool> WandClick;     // point in output coordinates, shift held
        List<PointF> stroke; Point mouseAt = new Point(-1, -1);
        public bool Brushing { get { return BrushTool >= 0 && Layers != null && Image != null; } }
        // "Copy text": drag a box over the part to read (0..1 of the picture as it's shown, turned)
        public bool AreaMode;
        public event Action<RectangleF> AreaPicked;
        public event Action AreaCancelled;
        bool areaDrag; PointF areaFrom, areaTo;
        public void StartArea() { AreaMode = true; areaDrag = false; Cursor = Cursors.Cross; Focus(); Invalidate(); }
        public void EndArea() { AreaMode = false; areaDrag = false; Cursor = Cursors.Default; Invalidate(); }
        RectangleF AreaScreen { get { return RectangleF.FromLTRB(Math.Min(areaFrom.X, areaTo.X), Math.Min(areaFrom.Y, areaTo.Y), Math.Max(areaFrom.X, areaTo.X), Math.Max(areaFrom.Y, areaTo.Y)); } }
        PointF ClampToImage(Point p)
        {
            float sc; RectangleF r = ImageRect(out sc);
            return new PointF(Math.Max(r.Left, Math.Min(r.Right, p.X)), Math.Max(r.Top, Math.Min(r.Bottom, p.Y)));
        }
        void FinishArea(RectangleF norm)
        {
            EndArea();
            if (AreaPicked != null) AreaPicked(norm);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (AreaMode && keyData == Keys.Escape) { EndArea(); if (AreaCancelled != null) AreaCancelled(); return true; }
            if (AreaMode && keyData == Keys.Enter) { FinishArea(new RectangleF(0, 0, 1, 1)); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void PaintArea(Graphics g, RectangleF img)
        {
            RectangleF sel = areaDrag ? AreaScreen : RectangleF.Empty;
            using (Region outside = new Region(img))
            {
                if (sel.Width > 0 && sel.Height > 0) outside.Exclude(sel);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(sel.Width > 0 ? 140 : 90, 0, 0, 0))) g.FillRegion(dim, outside);
            }
            if (sel.Width > 0 && sel.Height > 0)
            {
                using (Pen p = new Pen(Color.White, Theme.S(1.6f)) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, sel.X, sel.Y, sel.Width, sel.Height);
                using (Pen p = new Pen(Theme.AccentColor, Theme.S(1.6f)) { DashStyle = DashStyle.Dash, DashOffset = 3 }) g.DrawRectangle(p, sel.X, sel.Y, sel.Width, sel.Height);
            }
            // the hint, at the top of the picture
            string hint = L.T("ocr_area_hint");
            Font f = Theme.F(9.75f, FontStyle.Bold);
            int w = Math.Min(Width - Theme.S(24), Theme.Measure(hint, f) + Theme.S(56));
            Rectangle hr = new Rectangle((Width - w) / 2, (int)Math.Max(Theme.S(10), img.Y + Theme.S(10)), w, Theme.S(38));
            Theme.FillRound(g, hr, Theme.S(19), Color.FromArgb(215, 20, 20, 22));
            Theme.Glyph(g, "\uE8EF", 10f, Color.White, Theme.Mirror(new Rectangle(hr.X + Theme.S(12), hr.Y, Theme.S(24), hr.Height), Width));
            Theme.Text(g, hint, f, Color.White, new Rectangle(hr.X + Theme.S(40), hr.Y, hr.Width - Theme.S(52), hr.Height), Align.Start, true, false, true);
        }

        public void StartCrop(float aspect)
        {
            cropAspect = aspect;
            float W = Logical.Width, H = Logical.Height;
            float w = W, h = H;
            if (aspect > 0) { if (W / H > aspect) w = H * aspect; else h = W / aspect; }
            if (aspect <= 0) { w = W * 0.9f; h = H * 0.9f; }
            CropRect = new RectangleF((W - w) / 2, (H - h) / 2, w, h);
            CropMode = true;
            Invalidate();
        }

        RectangleF ScreenRect(RectangleF r, RectangleF img, float k) { return new RectangleF(img.X + r.X * k, img.Y + r.Y * k, r.Width * k, r.Height * k); }

        int CropHit(Point p)
        {
            float sc; RectangleF img = ImageRect(out sc);
            float k = img.Width / Logical.Width;
            RectangleF sr = ScreenRect(CropRect, img, k);
            float hs = Theme.S(14);
            PointF[] c = { new PointF(sr.X, sr.Y), new PointF(sr.Right, sr.Y), new PointF(sr.X, sr.Bottom), new PointF(sr.Right, sr.Bottom) };
            for (int i = 0; i < 4; i++) if (Math.Abs(p.X - c[i].X) < hs && Math.Abs(p.Y - c[i].Y) < hs) return i;
            return sr.Contains(p) ? 4 : -1;
        }

        void CropMove(Point p)
        {
            PointF ip = ToImage(p);
            float dx = ip.X - cropFrom.X, dy = ip.Y - cropFrom.Y;
            float W = Logical.Width, H = Logical.Height, min = Math.Max(16, Math.Min(W, H) * 0.05f);
            RectangleF r = cropStart;
            if (cropDrag == 4)
            {
                r.X = Math.Max(0, Math.Min(W - r.Width, r.X + dx));
                r.Y = Math.Max(0, Math.Min(H - r.Height, r.Y + dy));
            }
            else
            {
                // the opposite corner stays put
                float ax = cropDrag == 0 || cropDrag == 2 ? r.Right : r.X, ay = cropDrag == 0 || cropDrag == 1 ? r.Bottom : r.Y;
                float x = Math.Max(0, Math.Min(W, ip.X)), y = Math.Max(0, Math.Min(H, ip.Y));
                float w = Math.Max(min, Math.Abs(x - ax)), h = Math.Max(min, Math.Abs(y - ay));
                if (cropAspect > 0) { if (w / h > cropAspect) w = h * cropAspect; else h = w / cropAspect; }
                float nx = x < ax ? ax - w : ax, ny = y < ay ? ay - h : ay;
                if (nx < 0 || ny < 0 || nx + w > W || ny + h > H) return;
                r = new RectangleF(nx, ny, w, h);
            }
            CropRect = r;
            Invalidate();
        }

        void PaintCrop(Graphics g, RectangleF img)
        {
            float k = img.Width / Logical.Width;
            RectangleF sr = ScreenRect(CropRect, img, k);
            using (Region outside = new Region(img))
            {
                outside.Exclude(sr);
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRegion(dim, outside);
            }
            using (Pen thirds = new Pen(Color.FromArgb(110, 255, 255, 255)))
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(thirds, sr.X + sr.Width * i / 3, sr.Y, sr.X + sr.Width * i / 3, sr.Bottom);
                    g.DrawLine(thirds, sr.X, sr.Y + sr.Height * i / 3, sr.Right, sr.Y + sr.Height * i / 3);
                }
            using (Pen border = new Pen(Color.White, 2f)) g.DrawRectangle(border, sr.X, sr.Y, sr.Width, sr.Height);
            float L = Theme.S(16), t = Theme.S(4);
            using (SolidBrush hb = new SolidBrush(Color.White))
                foreach (PointF c in new[] { new PointF(sr.X, sr.Y), new PointF(sr.Right, sr.Y), new PointF(sr.X, sr.Bottom), new PointF(sr.Right, sr.Bottom) })
                {
                    float sx = c.X == sr.X ? 1 : -1, sy = c.Y == sr.Y ? 1 : -1;
                    g.FillRectangle(hb, sx > 0 ? c.X - t / 2 : c.X - L + t / 2, c.Y - t / 2, L, t);
                    g.FillRectangle(hb, c.X - t / 2, sy > 0 ? c.Y - t / 2 : c.Y - L + t / 2, t, L);
                }
            string dim2 = (int)Math.Round(CropRect.Width) + " × " + (int)Math.Round(CropRect.Height);
            Font f = Theme.F(9f, FontStyle.Bold);
            int tw = Theme.Measure(dim2, f) + Theme.S(16);
            Rectangle lr = new Rectangle((int)(sr.X + (sr.Width - tw) / 2), (int)sr.Bottom - Theme.S(34), tw, Theme.S(24));
            Theme.FillRound(g, lr, Theme.S(8), Color.FromArgb(170, 0, 0, 0));
            Theme.Text(g, dim2, f, Color.White, lr, Align.Center, true, false, true);
        }

        // cached background (photo scaled to the screen) so repaints stay fast
        Bitmap bg; Image bgImage; RectangleF bgRect; int bgRot; bool bgDark, bgChecker;

        // glide animation for "move text to the left/right/top..."
        TextLayer animLayer; PointF animFrom, animTo; float animT = 1;

        public ImageCanvas() { Surface = Surface.Background; }

        public void Reset() { Zoom = 0; pan = PointF.Empty; Rotation = 0; ImageChanged(); }
        public void ImageChanged() { DropBg(); Invalidate(); }
        void DropBg() { if (bg != null) { bg.Dispose(); bg = null; } }
        protected override void OnResize(EventArgs e) { DropBg(); base.OnResize(e); }
        protected override void Dispose(bool disposing) { if (disposing) DropBg(); base.Dispose(disposing); }

        public void AnimateLayerTo(TextLayer l, PointF to)
        {
            animLayer = l; animFrom = l.Pos; animTo = to; animT = 0;
            Animator.Start(this);
        }

        public override bool Step()
        {
            bool more = base.Step();
            if (animLayer != null && animT < 1)
            {
                animT = Math.Min(1, animT + 0.09f * Animator.K);
                float e = 1 - (float)Math.Pow(1 - animT, 3);   // ease-out
                animLayer.Pos = new PointF(animFrom.X + (animTo.X - animFrom.X) * e, animFrom.Y + (animTo.Y - animFrom.Y) * e);
                Invalidate();
                if (animT >= 1) { animLayer = null; if (LayerMoved != null) LayerMoved(); }
                return true;
            }
            return more;
        }

        float FitScale(float iw, float ih)
        {
            float pad = Theme.S(Dark ? 0 : 16);
            float s = Math.Min((Width - pad * 2) / iw, (Height - pad * 2) / ih);
            return Layers != null ? s : Math.Min(1f, s);
        }

        /// Where the (rotated) image lands on screen.
        RectangleF ImageRect(out float scale)
        {
            bool side = Rotation == 90 || Rotation == 270;
            float iw = side ? Image.Height : Image.Width, ih = side ? Image.Width : Image.Height;
            if (Layers != null && Logical.Width > 0) { iw = Logical.Width; ih = Logical.Height; }
            scale = Zoom > 0 ? Zoom : FitScale(iw, ih);
            float w = iw * scale, h = ih * scale;
            return new RectangleF((Width - w) / 2 + (Zoom > 0 ? pan.X : 0), (Height - h) / 2 + (Zoom > 0 ? pan.Y : 0), w, h);
        }

        PointF ToImage(Point p)
        {
            float sc; RectangleF r = ImageRect(out sc);
            float k = Logical.Width / r.Width;
            return new PointF((p.X - r.X) * k, (p.Y - r.Y) * k);
        }

        /// Corner handle of the selected text under the mouse (0 TL, 1 TR, 2 BL, 3 BR), or -1.
        int CornerAt(Point p)
        {
            if (Layers == null || Image == null || SelectedLayer < 0 || SelectedLayer >= Layers.Count || CropMode || Brushing || !Shown(SelectedLayer)) return -1;
            float sc; RectangleF r = ImageRect(out sc);
            float k = r.Width / Math.Max(1, Logical.Width);
            RectangleF b = Layers[SelectedLayer].Bounds;
            RectangleF sr = new RectangleF(r.X + b.X * k, r.Y + b.Y * k, b.Width * k, b.Height * k);
            PointF[] c = { new PointF(sr.X, sr.Y), new PointF(sr.Right, sr.Y), new PointF(sr.X, sr.Bottom), new PointF(sr.Right, sr.Bottom) };
            float grab = Theme.S(9);
            for (int i = 0; i < 4; i++) if (Math.Abs(p.X - c[i].X) <= grab && Math.Abs(p.Y - c[i].Y) <= grab) return i;
            return -1;
        }

        /// The side handle of the selected photo under the mouse (0 left, 1 right, 2 top, 3 bottom), or -1.
        /// (Sides stretch a photo wider or taller; corners keep its shape.)
        int SideAt(Point p)
        {
            if (Layers == null || Image == null || SelectedLayer < 0 || SelectedLayer >= Layers.Count || CropMode || Brushing || !Shown(SelectedLayer)) return -1;
            TextLayer l = Layers[SelectedLayer];
            if (!l.IsPicture || Math.Abs(l.Angle) > 0.5f) return -1;
            RectangleF sr = SelScreen();
            PointF[] c = { new PointF(sr.X, sr.Y + sr.Height / 2), new PointF(sr.Right, sr.Y + sr.Height / 2), new PointF(sr.X + sr.Width / 2, sr.Y), new PointF(sr.X + sr.Width / 2, sr.Bottom) };
            float grab = Theme.S(9);
            for (int i = 0; i < 4; i++) if (Math.Abs(p.X - c[i].X) <= grab && Math.Abs(p.Y - c[i].Y) <= grab) return i;
            return -1;
        }
        RectangleF SelScreen()
        {
            float sc; RectangleF r = ImageRect(out sc);
            float k = r.Width / Math.Max(1, Logical.Width);
            RectangleF b = Layers[SelectedLayer].Bounds;
            return new RectangleF(r.X + b.X * k, r.Y + b.Y * k, b.Width * k, b.Height * k);
        }

        int HitLayer(Point p)
        {
            if (Layers == null || Image == null) return -1;
            PointF ip = ToImage(p);
            if (SelectedLayer >= 0 && SelectedLayer < Layers.Count && Shown(SelectedLayer) && Layers[SelectedLayer].Bounds.Contains(ip)) return SelectedLayer;
            for (int i = Layers.Count - 1; i >= 0; i--) if (Shown(i) && Layers[i].Bounds.Contains(ip)) return i;
            return -1;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Image == null) return;
            if ((ModifierKeys & Keys.Control) != 0 || Layers != null)
            {
                float cur; ImageRect(out cur);
                float nz = Math.Max(0.05f, Math.Min(16f, cur * (e.Delta > 0 ? 1.2f : 1 / 1.2f)));
                PointF c = new PointF(Width / 2f + (Zoom > 0 ? pan.X : 0), Height / 2f + (Zoom > 0 ? pan.Y : 0));
                if (Zoom <= 0) pan = PointF.Empty;
                pan.X += (e.X - c.X) * (1 - nz / cur);
                pan.Y += (e.Y - c.Y) * (1 - nz / cur);
                Zoom = nz;
                Invalidate();
            }
            else if (Navigate != null) Navigate(e.Delta < 0 ? 1 : -1);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            downAt = e.Location; clickMoved = false;
            if (AreaMode && Image != null)
            {
                if (e.Button == MouseButtons.Left) { areaDrag = true; areaFrom = areaTo = ClampToImage(e.Location); Capture = true; Invalidate(); }
                else if (e.Button == MouseButtons.Right) { EndArea(); if (AreaCancelled != null) AreaCancelled(); }
                return;
            }
            if (Brushing && !CropMode && e.Button == MouseButtons.Left)
            {
                PointF ip = ToImage(e.Location);
                if (BrushTool == 2) { if (WandClick != null && ip.X >= 0 && ip.Y >= 0 && ip.X < Logical.Width && ip.Y < Logical.Height) WandClick(ip, (ModifierKeys & Keys.Shift) != 0); return; }
                stroke = new List<PointF> { ip };
                Capture = true; Invalidate();
                return;
            }
            if (Brushing && (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) && Zoom > 0) { dragging = true; dragFrom = e.Location; panFrom = pan; Cursor = Cursors.SizeAll; return; }
            if (CropMode && Image != null && e.Button == MouseButtons.Left)
            {
                cropDrag = CropHit(e.Location);
                if (cropDrag >= 0) { cropFrom = ToImage(e.Location); cropStart = CropRect; Capture = true; return; }
            }
            if (e.Button == MouseButtons.Left && SideAt(e.Location) >= 0)
            {
                // drag a side: the photo gets wider or narrower (taller or shorter) around its middle
                TextLayer l = Layers[SelectedLayer];
                resizeSide = SideAt(e.Location);
                RectangleF b = l.Bounds;
                resizeCenter = new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
                resizeStartBox = l.PictureSize;
                animLayer = null;
                Capture = true;
                return;
            }
            if (e.Button == MouseButtons.Left && CornerAt(e.Location) >= 0)
            {
                // drag a corner: the text grows or shrinks around its middle
                TextLayer l = Layers[SelectedLayer];
                resizeCorner = CornerAt(e.Location);
                RectangleF b = l.Bounds;
                resizeCenter = new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
                PointF ip = ToImage(e.Location);
                resizeStartDist = Math.Max(1, (float)Math.Sqrt((ip.X - resizeCenter.X) * (ip.X - resizeCenter.X) + (ip.Y - resizeCenter.Y) * (ip.Y - resizeCenter.Y)));
                resizeStartSize = l.SizePx;
                animLayer = null;
                Capture = true;
                return;
            }
            if (Layers != null && Image != null && e.Button == MouseButtons.Left && !CropMode)
            {
                int hit = HitLayer(e.Location);
                if (hit != SelectedLayer) { SelectedLayer = hit; if (LayerPicked != null) LayerPicked(hit); Invalidate(); }
                if (hit >= 0)
                {
                    animLayer = null;
                    layerDrag = true; layerFrom = ToImage(e.Location); layerPosFrom = Layers[hit].Pos;
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }
            if (Image != null && Zoom > 0) { dragging = true; dragFrom = e.Location; panFrom = pan; Cursor = Cursors.SizeAll; }
            else if (Image != null && DragFile != null && e.Button == MouseButtons.Left && !CropMode && !Brushing)
                DragOut.Arm(this, e.Location, () => { string f = DragFile(); return f != null ? new[] { f } : null; });   // drag the picture out of the app
        }
        /// The file the picture on screen can be dragged out as (null = it can't, e.g. while editing).
        public Func<string> DragFile;
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Math.Abs(e.X - downAt.X) + Math.Abs(e.Y - downAt.Y) > 4) clickMoved = true;
            if (AreaMode) { Cursor = Cursors.Cross; if (areaDrag) { areaTo = ClampToImage(e.Location); Invalidate(); } return; }
            if (Brushing && !CropMode && !dragging)
            {
                mouseAt = e.Location;
                if (stroke != null) stroke.Add(ToImage(e.Location));
                Cursor = BrushTool == 2 ? Cursors.Cross : Cursors.Default;
                Invalidate();
                return;
            }
            if (CropMode)
            {
                if (cropDrag >= 0) { CropMove(e.Location); return; }
                int h = CropHit(e.Location);
                Cursor = h == 0 || h == 3 ? Cursors.SizeNWSE : h == 1 || h == 2 ? Cursors.SizeNESW : h == 4 ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            if (resizeSide >= 0 && SelectedLayer >= 0 && SelectedLayer < Layers.Count)
            {
                PointF ip = ToImage(e.Location);
                TextLayer l = Layers[SelectedLayer];
                float w = resizeStartBox.Width, h = resizeStartBox.Height;
                if (resizeSide <= 1) w = Math.Max(8, Math.Min(Logical.Width * 4, 2 * Math.Abs(ip.X - resizeCenter.X)));
                else h = Math.Max(8, Math.Min(Logical.Height * 4, 2 * Math.Abs(ip.Y - resizeCenter.Y)));
                l.SetPictureSize(w, h);
                if (LayerResized != null) LayerResized();
                Invalidate();
                return;
            }
            if (resizeCorner >= 0 && SelectedLayer >= 0 && SelectedLayer < Layers.Count)
            {
                PointF ip = ToImage(e.Location);
                float d = (float)Math.Sqrt((ip.X - resizeCenter.X) * (ip.X - resizeCenter.X) + (ip.Y - resizeCenter.Y) * (ip.Y - resizeCenter.Y));
                TextLayer l = Layers[SelectedLayer];
                float ns = Math.Max(4, Math.Min(4000, resizeStartSize * d / resizeStartDist));
                if (Math.Abs(ns - l.SizePx) >= 0.5f)
                {
                    l.SizePx = ns; l.SizeChanging();
                    if (LayerResized != null) LayerResized();
                    Invalidate();
                }
                return;
            }
            if (layerDrag && SelectedLayer >= 0 && SelectedLayer < Layers.Count)
            {
                PointF ip = ToImage(e.Location);
                float nx = layerPosFrom.X + ip.X - layerFrom.X, ny = layerPosFrom.Y + ip.Y - layerFrom.Y;
                nx = Math.Max(0, Math.Min(Logical.Width, nx)); ny = Math.Max(0, Math.Min(Logical.Height, ny));
                float sc; RectangleF r = ImageRect(out sc);
                float tol = Theme.S(8) * Logical.Width / r.Width;
                snapX = Math.Abs(nx - Logical.Width / 2) < tol; if (snapX) nx = Logical.Width / 2;
                snapY = Math.Abs(ny - Logical.Height / 2) < tol; if (snapY) ny = Logical.Height / 2;
                Layers[SelectedLayer].Pos = new PointF(nx, ny);
                if (LayerMoved != null) LayerMoved();
                Invalidate();
                return;
            }
            if (dragging) { pan = new PointF(panFrom.X + e.X - dragFrom.X, panFrom.Y + e.Y - dragFrom.Y); DropBg(); Invalidate(); return; }
            int corner = CornerAt(e.Location);
            if (corner >= 0) { Cursor = corner == 0 || corner == 3 ? Cursors.SizeNWSE : Cursors.SizeNESW; return; }
            int side = SideAt(e.Location);
            if (side >= 0) { Cursor = side <= 1 ? Cursors.SizeWE : Cursors.SizeNS; return; }
            if (Layers != null) Cursor = HitLayer(e.Location) >= 0 ? Cursors.SizeAll : (Zoom > 0 ? Cursors.Hand : Cursors.Default);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (AreaMode && areaDrag)
            {
                areaDrag = false; Capture = false;
                RectangleF sel = AreaScreen;
                float sc; RectangleF r = ImageRect(out sc);
                if (sel.Width < Theme.S(8) || sel.Height < Theme.S(8)) { Invalidate(); return; }   // just a click: keep waiting for a box
                FinishArea(new RectangleF((sel.X - r.X) / r.Width, (sel.Y - r.Y) / r.Height, sel.Width / r.Width, sel.Height / r.Height));
                return;
            }
            dragging = false;
            if (stroke != null)
            {
                List<PointF> pts = stroke; stroke = null; Capture = false;
                float sc; RectangleF ir = ImageRect(out sc);
                float radius = BrushPx / 2f * Logical.Width / Math.Max(1, ir.Width);
                if (Stroke != null) Stroke(pts, radius, BrushTool);
                Invalidate();
                return;
            }
            if (cropDrag >= 0) { cropDrag = -1; Capture = false; }
            if (resizeSide >= 0)
            {
                resizeSide = -1; Capture = false;
                if (SelectedLayer >= 0 && SelectedLayer < Layers.Count) Layers[SelectedLayer].Touch();
                Invalidate();
            }
            if (resizeCorner >= 0)
            {
                resizeCorner = -1; Capture = false;
                if (SelectedLayer >= 0 && SelectedLayer < Layers.Count) Layers[SelectedLayer].Touch();   // sharp again
                Invalidate();
            }
            if (layerDrag) { layerDrag = false; snapX = snapY = false; Invalidate(); }
            Cursor = Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (mouseAt.X >= 0) { mouseAt = new Point(-1, -1); Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (Image == null) { if (Clicked != null) Clicked(); return; }
            if (Brushing) return;
            if (!CropMode && !clickMoved && e.Button == MouseButtons.Left && BackgroundClick != null && HitLayer(e.Location) < 0) BackgroundClick();
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Image == null || Brushing) return;
            if (Layers != null && HitLayer(e.Location) >= 0) { if (LayerEdit != null) LayerEdit(); return; }
            if (Zoom > 0) { Zoom = 0; pan = PointF.Empty; } else Zoom = 1;
            DropBg();
            Invalidate();
        }

        void PaintEmpty(Graphics g)
        {
            if (string.IsNullOrEmpty(Message)) return;
            Rectangle c = new Rectangle(Width / 2 - Theme.S(40), Height / 2 - Theme.S(80), Theme.S(80), Theme.S(80));
            for (int i = 1; i <= 6; i++)
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(Theme.Dark ? 16 : 8, 0, 0, 0))) g.FillEllipse(sb, c.X - i, c.Y - i + 4, c.Width + 2 * i, c.Height + 2 * i);
            using (LinearGradientBrush b = new LinearGradientBrush(c, Theme.PrimaryTop, Theme.PrimaryBottom, LinearGradientMode.Vertical)) g.FillEllipse(b, c);
            Theme.Glyph(g, Ico.Photo, 22f, Theme.PrimaryInk, c);
            Theme.Text(g, Message, Theme.F(11f), Theme.InkSoft, new Rectangle(Theme.S(20), c.Bottom + Theme.S(16), Width - Theme.S(40), Theme.S(60)), Align.Center, false, true);
        }

        /// Draws the photo (drop shadow, checkerboard for transparency, rotation) into a screen-sized cache.
        void EnsureBg(RectangleF r)
        {
            if (bg != null && bgImage == Image && bgRect == r && bgRot == Rotation && bgDark == Theme.Dark && bgChecker == (Layers != null) && bg.Width == Width && bg.Height == Height) return;
            DropBg();
            if (Width <= 0 || Height <= 0) return;
            bg = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            bgImage = Image; bgRect = r; bgRot = Rotation; bgDark = Theme.Dark; bgChecker = Layers != null;
            using (Graphics g = Graphics.FromImage(bg))
            {
                if (Dark) g.Clear(Color.FromArgb(14, 15, 17)); else Theme.PaintSurface(g, this, Surface);
                Theme.Smooth(g);
                if (!Dark)
                    for (int i = 1; i <= 8; i++)
                        using (SolidBrush sb = new SolidBrush(Color.FromArgb(Theme.Dark ? 14 : 7, 0, 0, 0)))
                            g.FillRectangle(sb, r.X - i + 2, r.Y - i + 5, r.Width + 2 * i - 4, r.Height + 2 * i - 4);
                if (Layers != null && !Dark) PaintChecker(g, r);
                bool side = Rotation == 90 || Rotation == 270;
                g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
                g.RotateTransform(Rotation);
                float dw = side ? r.Height : r.Width, dh = side ? r.Width : r.Height;
                float zoomFactor = r.Width / (side ? Image.Height : Image.Width);
                g.InterpolationMode = zoomFactor > 2 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(Image, new RectangleF(-dw / 2, -dh / 2, dw, dh));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (Image == null)
            {
                if (Dark) g.Clear(Color.FromArgb(14, 15, 17)); else PaintBg(g);
                Theme.Smooth(g);
                PaintEmpty(g);
                return;
            }
            float scale;
            RectangleF r = ImageRect(out scale);
            // a huge zoom would make a gigantic cache: draw directly then
            if (r.Width * r.Height > 40000000f)
            {
                PaintBg(g);
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(Image, r);
            }
            else
            {
                EnsureBg(r);
                if (bg != null) g.DrawImageUnscaled(bg, 0, 0); else PaintBg(g);
            }
            Theme.Smooth(g);
            if (Layers != null) PaintLayers(g, r);
            if (CropMode && Logical.Width > 0) PaintCrop(g, r);
            if (AreaMode) PaintArea(g, r);
            if (Brushing && !CropMode) PaintBrush(g, r);
            if (ShowPlay && !CropMode)
            {
                int d = Theme.S(64);
                Rectangle pc = new Rectangle((int)(r.X + r.Width / 2 - d / 2), (int)(r.Y + r.Height / 2 - d / 2), d, d);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(140, 0, 0, 0))) g.FillEllipse(b, pc);
                using (Pen p = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f)) g.DrawEllipse(p, pc);
                Theme.Glyph(g, Ico.Play, 18f, Color.White, new Rectangle(pc.X + Theme.S(3), pc.Y, pc.Width, pc.Height));
            }
            if (!string.IsNullOrEmpty(Caption))
            {
                Font f = Theme.F(10f, FontStyle.Bold);
                int w = Theme.Measure(Caption, f) + Theme.S(20);
                Rectangle cr = new Rectangle(Theme.S(12), Height - Theme.S(42), w, Theme.S(30));
                Theme.FillRound(g, cr, Theme.S(8), Color.FromArgb(170, 0, 0, 0));
                Theme.Text(g, Caption, f, Color.White, cr, Align.Center, true, false, true);
            }
            if (Loading)
            {
                Rectangle lr = new Rectangle(Width - Theme.S(44), Theme.S(12), Theme.S(32), Theme.S(32));
                Theme.FillRound(g, lr, Theme.S(8), Color.FromArgb(150, 0, 0, 0));
                Theme.Glyph(g, Ico.Refresh, 11f, Color.White, lr);
            }
            if (Zoom > 0)
            {
                string z = ((int)(Zoom * 100)) + "%";
                Rectangle zr = new Rectangle(Width - Theme.S(80), Height - Theme.S(44), Theme.S(64), Theme.S(28));
                Theme.FillRound(g, zr, Theme.S(8), Color.FromArgb(160, 0, 0, 0));
                Theme.Text(g, z, Theme.F(9f, FontStyle.Bold), Color.White, zr, Align.Center, true, false, true);
            }
        }

        void PaintBrush(Graphics g, RectangleF r)
        {
            float k = r.Width / Math.Max(1, Logical.Width);
            Color tint = BrushTool == 1 ? Color.FromArgb(120, 34, 197, 94) : Color.FromArgb(120, 239, 68, 68);
            if (stroke != null && stroke.Count > 0)
            {
                GraphicsState st = g.Save();
                g.SetClip(r);
                PointF[] sp = new PointF[stroke.Count];
                for (int i = 0; i < sp.Length; i++) sp[i] = new PointF(r.X + stroke[i].X * k, r.Y + stroke[i].Y * k);
                using (Pen p = new Pen(tint, BrushPx) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (sp.Length == 1) using (SolidBrush b = new SolidBrush(tint)) g.FillEllipse(b, sp[0].X - BrushPx / 2, sp[0].Y - BrushPx / 2, BrushPx, BrushPx);
                    else g.DrawLines(p, sp);
                }
                g.Restore(st);
            }
            if (mouseAt.X >= 0 && BrushTool != 2)
            {
                float d = BrushPx;
                using (Pen p = new Pen(Color.FromArgb(150, 0, 0, 0), 3f)) g.DrawEllipse(p, mouseAt.X - d / 2, mouseAt.Y - d / 2, d, d);
                using (Pen p = new Pen(Color.White, 1.5f)) g.DrawEllipse(p, mouseAt.X - d / 2, mouseAt.Y - d / 2, d, d);
                using (SolidBrush b = new SolidBrush(tint)) g.FillEllipse(b, mouseAt.X - 2, mouseAt.Y - 2, 4, 4);
            }
        }

        void PaintChecker(Graphics g, RectangleF r)
        {
            int s = Theme.S(10);
            GraphicsState st = g.Save();
            g.SetClip(r);
            g.FillRectangle(Brushes.White, r);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(226, 228, 232)))
                for (float y = r.Y; y < r.Bottom; y += s)
                    for (float x = r.X + ((int)((y - r.Y) / s) % 2) * s; x < r.Right; x += s * 2)
                        g.FillRectangle(b, x, y, s, s);
            g.Restore(st);
        }

        void PaintLayers(Graphics g, RectangleF r)
        {
            float k = r.Width / Logical.Width;
            GraphicsState st = g.Save();
            g.SetClip(r);
            for (int i = 0; i < Layers.Count; i++) if (Shown(i)) Layers[i].DrawScreen(g, r.X, r.Y, k);
            g.Restore(st);
            if (layerDrag && (snapX || snapY))
                using (Pen guide = new Pen(Color.FromArgb(230, 255, 255, 255), 1f) { DashStyle = DashStyle.Dash })
                using (Pen under = new Pen(Color.FromArgb(120, 0, 0, 0), 3f))
                {
                    if (snapX) { g.DrawLine(under, r.X + r.Width / 2, r.Y, r.X + r.Width / 2, r.Bottom); g.DrawLine(guide, r.X + r.Width / 2, r.Y, r.X + r.Width / 2, r.Bottom); }
                    if (snapY) { g.DrawLine(under, r.X, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2); g.DrawLine(guide, r.X, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2); }
                }
            if (SelectedLayer >= 0 && SelectedLayer < Layers.Count && !CropMode && Shown(SelectedLayer))
            {
                RectangleF b = Layers[SelectedLayer].Bounds;
                RectangleF sr = new RectangleF(r.X + b.X * k, r.Y + b.Y * k, b.Width * k, b.Height * k);
                using (GraphicsPath sp = Theme.Round(sr, Theme.S(6)))
                {
                    using (Pen dark = new Pen(Color.FromArgb(130, 0, 0, 0), 3f)) g.DrawPath(dark, sp);
                    using (Pen p = new Pen(Color.White, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawPath(p, sp);
                }
                float hs = Theme.S(10);
                foreach (PointF c in new[] { new PointF(sr.X, sr.Y), new PointF(sr.Right, sr.Y), new PointF(sr.X, sr.Bottom), new PointF(sr.Right, sr.Bottom) })
                {
                    RectangleF hr = new RectangleF(c.X - hs / 2, c.Y - hs / 2, hs, hs);
                    g.FillEllipse(Brushes.White, hr);
                    using (Pen p = new Pen(Color.FromArgb(160, 0, 0, 0))) g.DrawEllipse(p, hr);
                }
                // a photo also has a handle on each side: stretch it wider or taller
                TextLayer sel = Layers[SelectedLayer];
                if (sel.IsPicture && Math.Abs(sel.Angle) <= 0.5f && sr.Width > hs * 4 && sr.Height > hs * 4)
                {
                    float lw = Theme.S(16), lt = Theme.S(6);
                    RectangleF[] bars = {
                        new RectangleF(sr.X - lt / 2, sr.Y + sr.Height / 2 - lw / 2, lt, lw), new RectangleF(sr.Right - lt / 2, sr.Y + sr.Height / 2 - lw / 2, lt, lw),
                        new RectangleF(sr.X + sr.Width / 2 - lw / 2, sr.Y - lt / 2, lw, lt), new RectangleF(sr.X + sr.Width / 2 - lw / 2, sr.Bottom - lt / 2, lw, lt) };
                    foreach (RectangleF br in bars)
                    {
                        Theme.FillRound(g, br, lt / 2, Color.White);
                        Theme.StrokeRound(g, br, lt / 2, Color.FromArgb(160, 0, 0, 0));
                    }
                }
            }
        }
    }

    public class ViewerPage : Page
    {
        Rectangle bar, nameR, infoR;
        Btn bPrev, bNext, bRotL, bRotR, bOpen, bSave, bConvert, bFolder, bEdit, bUndoH, bRedoH;
        ImageCanvas canvas;
        EditorPanel editor;
        bool editing, preparing;
        Image viewImage;              // the normal view image, kept while editing
        Rectangle panelCard;
        List<string> files = new List<string>();
        int index, loadToken;
        string title = "", info = "";
        public event Action<string> SendToConverter;
        static readonly string TempDir = Path.Combine(Path.GetTempPath(), "FileConverterView");
        readonly ToolTip tips = new ToolTip();

        public readonly bool InStudio;
        public event Action<string> OpenMedia;     // studio: a video/audio file was chosen
        public event Action<string> OpenProjectFile;   // studio: a recent video project was picked
        public event Action BrowseProjects;
        readonly RecentStrip recents = new RecentStrip();
        string DropText { get { return L.T(InStudio ? "studio_drop_any" : "v_drop"); } }

        /// Studio: "PDF tools" / "Pages" was clicked: the PDF page tools, with this PDF (null = nothing open).
        public event Action<string> OrganizePages;
        /// The PDF (or other document) open now, or null.
        public string DocPath { get { return docSource; } }
        public int PageIndex { get { return index; } }
        readonly Btn bPages;
        bool DocIsPdf { get { return Doc && Formats.Ext(docSource) == "pdf"; } }

        public ViewerPage() : this(false) { }
        public ViewerPage(bool studio) : base(studio ? "studio_title" : "viewer_title", studio ? "studio_sub" : "viewer_sub")
        {
            InStudio = studio;
            // Studio: "PDF tools" when nothing is open (combine PDFs, ...), "Pages" with a PDF open (reorder, sign... it)
            bPages = new Btn(() => L.T(DocIsPdf ? "v_pages" : "v_pdf_tools"), "", BtnKind.Secondary) { Visible = false };
            bPages.MouseEnter += (s, e) => tips.SetToolTip(bPages, L.T(DocIsPdf ? "v_pages_tip" : "v_pdf_tools_tip"));
            bPages.Click += (s, e) =>
            {
                if (OrganizePages == null) return;
                if (DocIsPdf) { if (ConfirmDiscard()) OrganizePages(docSource); }
                else if (files.Count == 0) OrganizePages(null);
            };
            bPrev = IconBtn(Ico.Left, "v_prev", () => Go(-1));
            bNext = IconBtn(Ico.Right, "v_next", () => Go(1));
            bRotL = IconBtn("\uE7A8", "v_rotate_l", () => Rotate(270));
            bRotR = IconBtn(Ico.Rotate, "v_rotate_r", () => Rotate(90));
            bOpen = new Btn(() => L.T("v_open"), Ico.FolderOpen, BtnKind.Secondary); bOpen.Click += (s, e) => PickFile();
            bOpen.MouseEnter += (s, e) => tips.SetToolTip(bOpen, L.T("v_open"));
            bSave = new Btn(() => L.T(Doc ? "doc_save_pdf" : "v_save_jpg"), Ico.Save, BtnKind.Primary); bSave.FontSize = 9.5f; bSave.Click += (s, e) => { if (Doc) SaveDoc(); else SaveJpg(); };
            bConvert = new Btn(() => L.T("v_convert"), Ico.Sync, BtnKind.Secondary);
            bConvert.MouseEnter += (s, e) => tips.SetToolTip(bConvert, L.T("v_convert")); bConvert.Click += (s, e) => { if (Current != null && SendToConverter != null) SendToConverter(Current); };
            bFolder = new Btn(() => L.T("v_folder"), Ico.FolderOpen, BtnKind.Ghost);
            bFolder.MouseEnter += (s, e) => tips.SetToolTip(bFolder, L.T("v_folder")); bFolder.Click += (s, e) => { if (Current != null) try { Process.Start("explorer.exe", "/select,\"" + Current + "\""); } catch { } };
            bEdit = new Btn(() => L.T("ed_edit"), Ico.Edit, BtnKind.Secondary); bEdit.Click += (s, e) => EnterEdit();
            bText = new Btn(() => L.T("ocr_btn"), "\uE8EF", BtnKind.Secondary);
            bText.MouseEnter += (s, e) => tips.SetToolTip(bText, L.T("ocr_tip"));
            bText.Click += (s, e) => CopyText();
            canvas = new ImageCanvas();
            canvas.DragFile = () => editing || Current == null ? null : (Doc && pageRender.ContainsKey(index) ? pageRender[index] : Current);
            canvas.Message = DropText;
            canvas.Clicked += PickFile;
            canvas.Navigate += d => Go(d);
            canvas.AreaPicked += ReadText;
            editor = new EditorPanel { Canvas = canvas, Visible = false, ShowUndo = false };
            // undo / redo live in the top bar while editing (the panel's tabs need the room)
            bUndoH = IconBtn("\uE7A7", "undo_tip", () => editor.Undo());
            bRedoH = IconBtn("\uE7A6", "redo_tip", () => editor.Redo());
            bUndoH.Enabled = bRedoH.Enabled = false;
            editor.HistoryChanged += (u, r) => { bUndoH.Enabled = u; bRedoH.Enabled = r; bUndoH.Invalidate(); bRedoH.Invalidate(); };
            Controls.Add(bUndoH); Controls.Add(bRedoH);
            editor.SaveAllPages = () => { if (!Doc) return false; SaveDoc(); return true; };
            editor.Notify = (msg, ok, file) =>
            {
                if (file != null) { if (InStudio) Recovery.Clear(Recovery.PhotoFile); ShowToast(msg, true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } }); }
                else ShowToast(msg, ok, null, null);
            };
            if (studio)
            {
                // the photo edit is backed up every half minute, and once more if the app crashes
                autoTimer = new System.Windows.Forms.Timer { Interval = 30000 };
                autoTimer.Tick += (s, e) => AutoSave();
                autoTimer.Start();
                Recovery.SaveNowRequested += () => { try { AutoSave(); } catch { } };
            }
            editor.Changed += () => { UpdateEditInfo(); Invalidate(nameR.IsEmpty ? bar : Rectangle.Union(M(nameR), M(infoR))); };
            editor.CloseRequested += () => ExitEdit(true);
            canvas.LayerPicked += i => editor.Select(i);
            canvas.LayerEdit += () => editor.FocusText();
            canvas.LayerMoved += () => editor.S.Dirty = true;
            Controls.AddRange(new Control[] { bPrev, bNext, bRotL, bRotR, bOpen, bEdit, bText, bSave, bConvert, bFolder, bPages, canvas, editor, strip });
            strip.PathOf = i => pageRender.ContainsKey(i) ? pageRender[i] : (i < files.Count ? files[i] : null);
            strip.Edited = i => pageState.ContainsKey(i);
            strip.PagePicked += GoTo;
            recents.Visible = false;
            recents.Open += f => { if (OpenProjectFile != null) OpenProjectFile(f); };
            recents.Browse += () => { if (BrowseProjects != null) BrowseProjects(); };
            if (studio) { Controls.Add(recents); recents.BringToFront(); }
            EnableDrop(canvas);
            try
            {
                Directory.CreateDirectory(TempDir);
                foreach (string f in Directory.GetFiles(TempDir)) if (File.GetLastWriteTime(f) < DateTime.Now.AddHours(-1)) File.Delete(f);
            }
            catch { }
            UpdateButtons();
        }

        Btn IconBtn(string glyph, string tipKey, Action a)
        {
            Btn b = new Btn(() => "", glyph, BtnKind.Secondary);
            b.Click += (s, e) => a();
            b.MouseEnter += (s, e) => tips.SetToolTip(b, L.T(tipKey));
            return b;
        }

        string Current { get { return files.Count > 0 ? files[index] : null; } }
        int StripH { get { return Theme.S(112); } }

        // ------------------------------------------------------------------ documents (PDF / Word): every page can be edited
        // Moving to another page keeps the edits of this one (they can be changed again later);
        // saving as PDF writes all pages, edited or not, into one file.
        string docSource, docEditDir;
        bool docLoading, docSaved = true, quietRestore;
        readonly Dictionary<int, string> pageState = new Dictionary<int, string>();   // page → its edit (texts, photos, size…)
        readonly Dictionary<int, string> pageRender = new Dictionary<int, string>();  // page → the edited page as a picture
        bool Doc { get { return docSource != null; } }
        readonly PageStrip strip = new PageStrip();
        bool ShowStrip { get { return Doc && files.Count > 1; } }

        /// Goes to a page. While editing, the editor stays open: this page is kept and the other one swapped in.
        void GoTo(int i)
        {
            if (!Doc || files.Count == 0 || preparing) return;
            i = Math.Max(0, Math.Min(files.Count - 1, i));
            if (i == index) return;
            if (!editing) { index = i; strip.Reveal(i); Show(i); return; }
            CommitPage();
            Bitmap full;
            try { using (Image img = Decode(files[i], 12000)) full = new Bitmap(img); }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); return; }
            Fx.Crossfade(canvas, 160);
            index = i;
            editor.Begin(full, files[i], false);
            editor.UsePdf();
            if (pageState.ContainsKey(i))
            {
                try { pendingRestore = new JsonParser(pageState[i]).Parse(); quietRestore = true; ApplyPendingRestore(); } catch { }
            }
            editor.Deselect();         // nothing picked: the arrow keys keep turning pages
            editor.S.Dirty = false;    // bringing back earlier edits isn't a new change
            canvas.Reset();
            UpdateEditInfo();
            strip.Reveal(i);
            Invalidate();
        }
        bool DocChanged { get { return Doc && !docSaved && pageState.Count > 0; } }

        void ResetDoc()
        {
            docSource = null; docSaved = true;
            pageState.Clear(); pageRender.Clear();
            if (docEditDir != null) { string d = docEditDir; ThreadPool.QueueUserWorkItem(_ => { try { Directory.Delete(d, true); } catch { } }); docEditDir = null; }
        }

        /// Keeps the page being edited (its layers, and a picture of the result).
        void CommitPage()
        {
            if (!editing || !Doc || !editor.S.Active || editor.S.Video) return;
            if (!editor.S.Dirty && !pageState.ContainsKey(index)) return;
            EditSession es = editor.S;
            if (es.Layers.Count == 0 && es.W == es.BaseW && es.H == es.BaseH && es.Crop.IsEmpty && es.Mask == null && es.BgMode == 0 && es.Adj.None)
            {
                // nothing left on this page (everything removed): it's the original page again
                if (pageState.Remove(index)) { pageRender.Remove(index); docSaved = false; }
                es.Dirty = false; strip.Invalidate();
                return;
            }
            pageState[index] = EditJson();
            if (docEditDir == null) { docEditDir = Path.Combine(Path.GetTempPath(), "FadiDocEdit_" + Guid.NewGuid().ToString("N").Substring(0, 10)); Directory.CreateDirectory(docEditDir); }
            string f = Path.Combine(docEditDir, "page" + (index + 1).ToString("D4") + "_" + DateTime.Now.Ticks + ".png");
            using (Bitmap b = editor.S.Render(true)) b.Save(f, ImageFormat.Png);
            pageRender[index] = f;
            docSaved = false;
            editor.S.Dirty = false;
            strip.Invalidate();
        }

        /// All pages into one PDF (the edited ones as edited).
        void SaveDoc()
        {
            if (!Doc || files.Count == 0) return;
            if (editing) CommitPage();
            string outp = SaveAsk.File(FindForm(), Path.GetFileNameWithoutExtension(docSource) + " " + L.T("ed_suffix") + ".pdf", "pdf");
            if (outp == null) return;
            List<string> pages = new List<string>();
            for (int i = 0; i < files.Count; i++) pages.Add(pageRender.ContainsKey(i) ? pageRender[i] : files[i]);
            ShowToast(L.T("doc_saving", pages.Count), true, null, null);
            ActJob act = Activity.Begin(this, L.T("act_k_pdf"), Path.GetFileName(outp), null);
            Thread t = new Thread(() =>
            {
                string err = null;
                try
                {
                    ImagePdf.Write(pages.Count, i => { using (Image im = Image.FromStream(new MemoryStream(File.ReadAllBytes(pages[i])))) return new Bitmap(im); }, true, outp, 92, ImagePdf.PageSize.Photo,
                                   (done, total) => Activity.Update(act, done / (double)Math.Max(1, total), null));
                }
                catch (Exception ex) { err = ex.Message; }
                Activity.End(act, err, false, err == null ? L.T("doc_saved_n", pages.Count) : null, err == null ? outp : null);
                Ui(() =>
                {
                    if (err != null) { ShowToast(err, false, null, null); return; }
                    docSaved = true;
                    ShowToast(L.T("v_saved", Path.GetFileName(outp)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + outp + "\""); } catch { } });
                    UpdateButtons(); Invalidate();
                });
            });
            t.IsBackground = true; t.Start();
        }

        /// Whether the buttons were last laid out for a PDF (Pages button, "Save as PDF").
        int layoutDoc;
        int DocState { get { return (Doc ? 1 : 0) | (DocIsPdf ? 2 : 0); } }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad;
            layoutDoc = DocState;
            bSave.Invalidate(); bPages.Invalidate();
            cards.Clear();
            bar = new Rectangle(p, Top0, W - p * 2, Theme.S(68));
            cards.Add(bar);
            int y = bar.Y + Theme.S(16), h = Theme.S(36), x = bar.Right - Theme.S(16);
            foreach (Control c in new Control[] { bPrev, bNext, bRotL, bRotR, bOpen, bEdit, bText, bSave, bConvert, bFolder }) c.Visible = !editing;
            bool showPages = InStudio && !editing && (DocIsPdf || files.Count == 0);
            bPages.Visible = showPages;
            bUndoH.Visible = bRedoH.Visible = editing && !editor.S.Video;
            if (editing && !editor.S.Video)
            {
                x -= h; Place(bRedoH, new Rectangle(x, y, h, h)); x -= Theme.S(6);
                x -= h; Place(bUndoH, new Rectangle(x, y, h, h)); x -= Theme.S(14);
            }
            if (editing && Doc && files.Count > 1)
            {
                // editing a document: ‹ › go to the other pages (this page's edits are kept)
                bPrev.Visible = bNext.Visible = true;
                x -= h; Place(bNext, new Rectangle(x, y, h, h)); x -= Theme.S(6);
                x -= h; Place(bPrev, new Rectangle(x, y, h, h)); x -= Theme.S(8);
            }
            if (!editing)
            {
                // narrow windows: drop the text from the less important buttons
                bool compact = W < Theme.S(1120), tiny = W < Theme.S(900);
                bFolder.Label = compact ? (Func<string>)(() => "") : () => L.T("v_folder");
                bConvert.Label = compact ? (Func<string>)(() => "") : () => L.T("v_convert");
                bText.Label = tiny ? (Func<string>)(() => "") : () => L.T("ocr_btn");
                bool pagesIcon = compact && files.Count > 0;   // nothing open: "PDF tools" keeps its name, so it's easy to find
                bPages.Label = pagesIcon ? (Func<string>)(() => "") : () => L.T(DocIsPdf ? "v_pages" : "v_pdf_tools");
                bool openIcon = compact && showPages;   // one more button: make room for the file name
                bOpen.Label = openIcon ? (Func<string>)(() => "") : () => L.T("v_open");
                List<Btn> right = new List<Btn> { bFolder, bConvert, bSave, bText, bEdit };
                if (showPages) right.Add(bPages);
                right.Add(bOpen);
                foreach (Btn b in right)
                {
                    int w = Math.Max(Theme.S(70), b.PreferredWidth);
                    if ((compact && (b == bFolder || b == bConvert)) || (pagesIcon && b == bPages) || (tiny && b == bText) || (openIcon && b == bOpen)) w = h;
                    x -= w; Place(b, new Rectangle(x, y, w, h)); x -= Theme.S(8);
                }
                x -= Theme.S(8);
                foreach (Btn b in new[] { bRotR, bRotL, bNext, bPrev })
                {
                    x -= h; Place(b, new Rectangle(x, y, h, h)); x -= Theme.S(6);
                    if (b == bRotL) x -= Theme.S(8);
                }
            }
            int tx = bar.X + Theme.S(18);
            nameR = new Rectangle(tx, bar.Y + Theme.S(14), Math.Max(10, x - tx - Theme.S(10)), Theme.S(22));
            infoR = new Rectangle(tx, bar.Y + Theme.S(36), Math.Max(10, x - tx - Theme.S(10)), Theme.S(18));
            int top = bar.Bottom + Theme.S(6);
            if (editing)
            {
                int pw = Math.Min(Theme.S(372), W / 2);
                panelCard = new Rectangle(W - p - pw, bar.Bottom + Theme.S(14), pw, H - bar.Bottom - Theme.S(14) - Theme.S(20));
                cards.Add(panelCard);
                Place(editor, Rectangle.Inflate(panelCard, -Theme.S(4), -Theme.S(6)));
                int ch = H - top - (ShowStrip ? StripH : 0);
                Place(canvas, new Rectangle(0, top, panelCard.X - Theme.S(6), ch));
                if (ShowStrip) Place(strip, new Rectangle(0, top + ch, panelCard.X - Theme.S(6), StripH));
            }
            else
            {
                int ch = H - top - (ShowStrip ? StripH : 0);
                Place(canvas, new Rectangle(0, top, W, ch));
                if (ShowStrip) Place(strip, new Rectangle(0, top + ch, W, StripH));
            }
            strip.Visible = ShowStrip;
            if (ShowStrip) { strip.Count = files.Count; strip.Current = index; }
            editor.Visible = editing;
            // the empty Studio: recent video projects along the bottom
            if (InStudio)
            {
                List<string> rp = files.Count == 0 && !editing ? TrimPage.RecentProjects : new List<string>();
                recents.Paths = rp;
                recents.Visible = rp.Count > 0;
                if (recents.Visible) { Place(recents, new Rectangle(p, H - Theme.S(96), W - p * 2, Theme.S(82))); recents.Invalidate(); }
            }
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            if (editing)
            {
                // a small "editing" pill in front of the name
                string tag = L.T("ed_editing");
                Font tf = Theme.F(8.25f, FontStyle.Bold);
                int tw = Theme.Measure(tag, tf) + Theme.S(18);
                Rectangle pill = new Rectangle(nameR.X, nameR.Y + Theme.S(1), tw, Theme.S(20));
                Theme.FillRoundGradient(g, M(pill), Theme.S(10), Theme.PrimaryTop, Theme.PrimaryBottom);
                Theme.Text(g, tag, tf, Theme.PrimaryInk, M(pill), Align.Center);
                Label(g, title, Theme.F(10.5f, FontStyle.Bold), Theme.Ink, new Rectangle(pill.Right + Theme.S(8), nameR.Y, nameR.Width - tw - Theme.S(8), nameR.Height));
                Theme.Text(g, info, Theme.F(8.75f), Theme.InkSoft, M(infoR), Theme.RTL ? Align.End : Align.Start, true, false, true);
                return;
            }
            Label(g, files.Count == 0 ? L.T("viewer_none") : title, Theme.F(10.5f, FontStyle.Bold), Theme.Ink, nameR);
            Theme.Text(g, info, Theme.F(8.75f), Theme.InkSoft, M(infoR), Theme.RTL ? Align.End : Align.Start, true, false, true);
        }

        public override void Restyle()
        {
            canvas.Message = editing ? null : DropText;
            editor.Restyle();
            if (editing) UpdateEditInfo();
            canvas.Invalidate();
        }

        void UpdateButtons()
        {
            bPrev.Enabled = bNext.Enabled = files.Count > 1;
            bRotL.Enabled = bRotR.Enabled = bSave.Enabled = bConvert.Enabled = bFolder.Enabled = bEdit.Enabled = bText.Enabled = canvas.Image != null && !preparing;
        }

        public bool HasFile { get { return files.Count > 0 || editing; } }

        /// Closes the photo (asks first when there are unsaved edits).
        public bool CloseAll()
        {
            if (editing && !Doc) { if (!ConfirmDiscard()) return false; ExitEdit(false); }
            if (editing) ExitEdit(false);
            if (DocChanged && !ConfirmDiscard()) return false;
            ResetDoc();
            loadToken++;
            files.Clear();
            Image old = canvas.Image;
            canvas.Image = null;
            canvas.Reset();
            canvas.Message = DropText;
            if (old != null) old.Dispose();
            title = ""; info = "";
            Form f = FindForm(); if (f is ViewerForm) f.Text = L.T("viewer_title");
            strip.Count = 0;
            UpdateButtons();
            DoLayout();   // the page strip goes too (it used to stay behind, its pages no longer clickable)
            return true;
        }

        // ------------------------------------------------------------------ editing
        public bool Editing { get { return editing; } }
        public bool HasUnsavedEdits { get { return (editing && editor.S.Dirty) || DocChanged; } }

        /// Asks before throwing away unsaved edits. Returns true when it's fine to continue.
        public bool ConfirmDiscard()
        {
            if (!HasUnsavedEdits) return true;
            return Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("ed_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning);
        }

        System.Windows.Forms.Timer autoTimer;
        int editRot;
        object pendingRestore;

        void AutoSave()
        {
            if (!editing || !editor.S.Dirty || !editor.S.Active || editor.S.Video) return;
            Recovery.Write(Recovery.PhotoFile, EditJson());
        }

        /// The edit as text: size, crop, background and every text / photo layer.
        string EditJson()
        {
            EditSession s = editor.S;
            StringBuilder b = new StringBuilder();
            b.Append("{\"app\": \"Fadi Tools\", \"kind\": \"photo\", \"version\": 1, \"saved\": ").Append(Js.Q(DateTime.Now.ToString("o")));
            b.Append(", \"source\": ").Append(Js.Q(s.SourcePath)).Append(", \"rot\": ").Append(editRot);
            b.Append(", \"w\": ").Append(s.W).Append(", \"h\": ").Append(s.H).Append(", \"crop\": [").Append(s.Crop.X).Append(',').Append(s.Crop.Y).Append(',').Append(s.Crop.Width).Append(',').Append(s.Crop.Height).Append(']');
            b.Append(", \"bgMode\": ").Append(s.BgMode).Append(", \"bgColor\": ").Append(s.BgColor.ToArgb()).Append(", \"bgBlur\": ").Append(Js.N(s.BgBlur));
            string mask = null;
            if (s.Mask != null)
                using (MemoryStream ms = new MemoryStream())
                {
                    using (System.IO.Compression.GZipStream gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true)) gz.Write(s.Mask, 0, s.Mask.Length);
                    mask = Convert.ToBase64String(ms.ToArray());
                }
            Adjust a = s.Adj;
            b.Append(", \"adjust\": [").Append(Js.N(a.Bright)).Append(',').Append(Js.N(a.Contrast)).Append(',').Append(Js.N(a.Sat)).Append(',').Append(Js.N(a.Warmth))
             .Append(',').Append(Js.N(a.Blur)).Append(',').Append(Js.N(a.Sharpen)).Append(',').Append(a.Filter).Append(']');
            b.Append(", \"mask\": ").Append(Js.Q(mask)).Append(", \"texts\": [");
            for (int i = 0; i < s.Layers.Count; i++) b.Append(i > 0 ? "," : "").Append(Js.Layer(s.Layers[i]));
            b.Append("]}");
            return b.ToString();
        }

        /// Brings back a photo edit that wasn't saved when the app closed.
        public void RestoreEdit(string file)
        {
            object root;
            try { root = new JsonParser(File.ReadAllText(file, Encoding.UTF8)).Parse(); } catch { return; }
            string src = Js.S(root, "source");
            if (src == null || !File.Exists(src)) { ShowToast(L.T("proj_all_missing"), false, null, null); return; }
            pendingRestore = root;
            afterLoad = () => { canvas.Rotation = Js.I(root, "rot"); EnterEdit(); };
            LoadFiles(new[] { src });
        }

        void ApplyPendingRestore()
        {
            object root = pendingRestore; pendingRestore = null;
            if (root == null) return;
            byte[] mask = null;
            string m = Js.S(root, "mask");
            if (m != null)
                try
                {
                    using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(m)))
                    using (System.IO.Compression.GZipStream gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
                    using (MemoryStream o = new MemoryStream()) { gz.CopyTo(o); mask = o.ToArray(); }
                }
                catch { mask = null; }
            List<object> cr = Js.A(root, "crop");
            Func<object, int> num = o => o is long ? (int)(long)o : o is double ? (int)(double)o : 0;
            Rectangle crop = cr.Count == 4 ? new Rectangle(num(cr[0]), num(cr[1]), num(cr[2]), num(cr[3])) : Rectangle.Empty;
            List<TextLayer> layers = new List<TextLayer>();
            foreach (object o in Js.A(root, "texts")) { TextLayer tl = Js.Layer(o); if (tl != null) layers.Add(tl); }
            editor.ApplyState(Js.I(root, "w"), Js.I(root, "h"), crop, mask, Js.I(root, "bgMode"), Color.FromArgb(Js.I(root, "bgColor", -1)), (float)Js.D(root, "bgBlur", 40), layers);
            List<object> adj = Js.A(root, "adjust");
            if (adj.Count >= 7)
            {
                Func<object, float> f = o => o is long ? (long)o : o is double ? (float)(double)o : 0f;
                editor.SetAdjust(new Adjust { Bright = f(adj[0]), Contrast = f(adj[1]), Sat = f(adj[2]), Warmth = f(adj[3]), Blur = f(adj[4]), Sharpen = f(adj[5]), Filter = (int)f(adj[6]) });
            }
            UpdateEditInfo();
            if (!quietRestore) ShowToast(L.T("proj_restored"), true, null, null);
            quietRestore = false;
        }

        void EnterEdit()
        {
            if (canvas.AreaMode) canvas.EndArea();
            if (editing || preparing || Current == null || canvas.Image == null) return;
            string path = Current;   // a document page is always edited from its original picture
            if (Doc && pageState.ContainsKey(index) && pendingRestore == null)
            {
                try { pendingRestore = new JsonParser(pageState[index]).Parse(); quietRestore = true; } catch { }
                canvas.Rotation = 0;
            }
            int rot = canvas.Rotation;
            editRot = rot;
            int token = loadToken;
            preparing = true;
            UpdateButtons();
            info = L.T("ed_preparing"); Invalidate();
            Thread t = new Thread(() =>
            {
                Bitmap full = null; string err = null;
                try
                {
                    using (Image img = Decode(path, 12000)) full = new Bitmap(img);
                    if (rot == 90) full.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    else if (rot == 180) full.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    else if (rot == 270) full.RotateFlip(RotateFlipType.Rotate270FlipNone);
                }
                catch (Exception ex) { err = ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message; }
                Ui(() =>
                {
                    preparing = false;
                    if (token != loadToken || err != null)
                    {
                        if (full != null) full.Dispose();
                        if (err != null) ShowToast(err, false, null, null);
                        UpdateButtons(); Show(index);
                        return;
                    }
                    viewImage = canvas.Image;
                    canvas.Rotation = 0; canvas.Zoom = 0;
                    editing = true;
                    editor.Begin(full, path, !Doc);
                    if (Doc) editor.UsePdf();
                    canvas.Reset();
                    UpdateEditInfo();
                    DoLayout();
                    if (pendingRestore != null) ApplyPendingRestore();
                    else editor.FocusText();
                });
            });
            t.IsBackground = true; t.Start();
        }

        void ExitEdit(bool ask)
        {
            if (!editing) return;
            if (Doc)
            {
                CommitPage(); ask = false;
                try
                {
                    string shown = pageRender.ContainsKey(index) ? pageRender[index] : files[index];
                    Image fresh = Decode(shown, 3840);
                    if (viewImage != null && viewImage != fresh) viewImage.Dispose();
                    viewImage = fresh;
                }
                catch { }
            }
            if (ask && !ConfirmDiscard()) return;
            if (InStudio) Recovery.Clear(Recovery.PhotoFile);
            editing = false;
            editor.End();
            canvas.Image = viewImage;
            viewImage = null;
            canvas.Reset();
            DoLayout();
            UpdateButtons();
            if (files.Count > 0) Show(index);
        }

        void UpdateEditInfo()
        {
            if (!editing || !editor.S.Active) return;
            int n = editor.S.Layers.Count;
            info = (Doc ? L.T("doc_page_info", index + 1, files.Count) + "   ·   " : "") + L.T("ed_info", editor.S.W + " × " + editor.S.H, n);
        }

        public void PickFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("v_open");
                List<string> pats = new List<string>();
                foreach (string e in Formats.ImageExt) pats.Add("*." + e);
                d.Filter = L.T("images") + "|" + string.Join(";", pats.ToArray()) + "|" + L.T("all_files") + "|*.*";
                if (InStudio)
                    d.Filter = L.T("photos_videos") + "|" + string.Join(";", pats.ToArray()) + ";*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.m4v;*.mpg;*.3gp;*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.pdf;*.doc;*.docx;*.odt;*.rtf;*.ppt;*.pptx;*.odp|" +
                               L.T("documents") + "|*.pdf;*.doc;*.docx;*.odt;*.rtf;*.ppt;*.pptx;*.odp|" + d.Filter;
                if (LastDir.Show(d, FindForm(), "studio") == DialogResult.OK) LoadFiles(new[] { d.FileName });
            }
        }

        public override void AcceptFiles(string[] paths) { LoadFiles(paths); }

        Btn bText;
        /// Reads the text in the photo on screen and copies it.
        /// Copy text: drag a box over the part you want (Enter = the whole picture), then it's read.
        public void CopyText()
        {
            if (Current == null || canvas.Image == null) { PickFileThen(CopyText); return; }
            if (canvas.AreaMode) { canvas.EndArea(); ReadText(new RectangleF(0, 0, 1, 1)); return; }   // second click: the whole picture
            canvas.StartArea();
            ShowToast(L.T("ocr_area_toast"), true, () => L.T("ocr_whole"), () => { if (canvas.AreaMode) { canvas.EndArea(); ReadText(new RectangleF(0, 0, 1, 1)); } });
        }

        void ReadText(RectangleF area)
        {
            if (Current == null) return;
            Ocr.Run(this, Current, canvas.Rotation, area, (msg, ok) => ShowToast(msg, ok, null, null), CopyText);
        }

        /// Asks for a picture, then does something with it once it's on screen.
        Action afterLoad;
        public void PickFileThen(Action a)
        {
            afterLoad = a;
            PickFile();
            if (Current == null) afterLoad = null;
        }

        bool opening;
        /// PDF / Word / PowerPoint: every page becomes a picture you can flip through, edit and save.
        public void OpenDocument(string path) { OpenDocument(path, 0); }

        /// Opens a document's pages; startPage = the page to show first.
        public void OpenDocument(string path, int startPage)
        {
            if (opening) return;
            if (editing) { if (!ConfirmDiscard()) return; ExitEdit(false); }
            else if (DocChanged && !ConfirmDiscard()) return;
            opening = true;
            title = Path.GetFileName(path); info = L.T("doc_opening"); Invalidate();
            ShowToast(L.T("doc_opening"), true, null, null);
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                List<string> pages = null; string err = null;
                try
                {
                    pages = DocPages.Render(path, (done, total) =>
                    {
                        try { BeginInvoke((Action)(() => { info = L.T("doc_rendering", done, Math.Max(done, total)); Invalidate(); })); } catch { }
                    }, () => IsDisposed || Tools.ShuttingDown);
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        opening = false;
                        if (err != null || pages == null || pages.Count == 0) { info = ""; Invalidate(); ShowToast(err ?? L.T("doc_failed"), false, null, null); return; }
                        ResetDoc();
                        docSource = path;
                        docLoading = true;
                        try { LoadFiles(pages.ToArray()); } finally { docLoading = false; }
                        if (startPage > 0 && startPage < files.Count) GoTo(startPage);
                        if (pages.Count > 1) ShowToast(L.T("doc_pages", pages.Count), true, null, null);
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }

        public void LoadFiles(string[] paths)
        {
            if (InStudio && OpenMedia != null && paths != null && paths.Length > 0)
            {
                Category c = Formats.CategoryOf(Formats.Ext(paths[0]));
                if (c == Category.Video || c == Category.Audio) { OpenMedia(paths[0]); return; }
            }
            if (InStudio && paths != null && paths.Length == 1 && DocPages.IsDoc(paths[0]) && File.Exists(paths[0])) { OpenDocument(paths[0]); return; }
            if (!docLoading && !editing && DocChanged && !ConfirmDiscard()) return;
            if (editing)
            {
                // a picture dropped while editing goes on top of this one
                List<string> pics = new List<string>();
                foreach (string p in paths) if (File.Exists(p) && Formats.CategoryOf(Formats.Ext(p)) == Category.Image) pics.Add(p);
                if (pics.Count > 0 && pics.Count == paths.Length) { foreach (string p in pics) editor.AddPicture(p); return; }
                if (!ConfirmDiscard()) return; ExitEdit(false);
            }
            if (!docLoading) ResetDoc();
            List<string> valid = new List<string>();
            foreach (string p in paths) if (File.Exists(p)) valid.Add(Path.GetFullPath(p));
            if (valid.Count == 0) return;
            files.Clear();
            if (valid.Count > 1) { files.AddRange(valid); index = 0; }
            else
            {
                string first = valid[0];
                try
                {
                    foreach (string f in Directory.GetFiles(Path.GetDirectoryName(first)))
                        if (Formats.CategoryOf(Formats.Ext(f)) == Category.Image) files.Add(f);
                    files.Sort(StringComparer.OrdinalIgnoreCase);
                }
                catch { }
                if (!files.Contains(first)) files.Insert(0, first);
                index = files.IndexOf(first);
            }
            Show(index);
        }

        void Go(int d)
        {
            if (canvas.AreaMode) canvas.EndArea();
            if (files.Count < 2 || preparing) return;
            if (Doc) { GoTo(Math.Max(0, Math.Min(files.Count - 1, index + d))); return; }
            if (editing) return;
            index = (index + d + files.Count) % files.Count;
            Show(index);
        }

        void Rotate(int deg)
        {
            if (canvas.Image == null || editing) return;
            canvas.Rotation = (canvas.Rotation + deg) % 360;
            canvas.Invalidate();
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        void Show(int i)
        {
            string path = files[i];
            bool edited = Doc && pageRender.ContainsKey(i);
            strip.Count = files.Count; strip.Reveal(i);
            if (strip.Wanted != ShowStrip || layoutDoc != DocState) DoLayout();   // a document just opened: room for its pages, the Pages button
            string shown = edited ? pageRender[i] : path;
            int token = ++loadToken;
            title = Doc ? Path.GetFileName(docSource) : Path.GetFileName(path); info = L.T("v_loading");
            Form f = FindForm(); if (f is ViewerForm) f.Text = title + " — " + L.T("viewer_title");
            Invalidate();
            Thread t = new Thread(() =>
            {
                Image img = null; string err = null;
                try { img = Decode(shown, 3840); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    if (token != loadToken || editing) { if (img != null) img.Dispose(); return; }
                    Image old = canvas.Image;
                    canvas.Image = img;
                    canvas.Reset();
                    canvas.Message = err == null ? null : L.T("v_cant") + "\n" + err;
                    if (old != null) old.Dispose();
                    long size = 0; try { size = new FileInfo(path).Length; } catch { }
                    string ext = Formats.Ext(path).ToUpperInvariant();
                    info = Doc ? L.T("doc_page_info", index + 1, files.Count) + (edited ? "   ·   " + L.T("doc_edited") : "") + (pageState.Count > 0 ? "   ·   " + L.T("doc_edited_n", pageState.Count) : "")
                         : img != null ? string.Format("{0} × {1}   ·   {2}   ·   {3}   ·   {4} / {5}", img.Width, img.Height, ext, Theme.HumanSize(size), index + 1, files.Count)
                                       : ext + "   ·   " + (index + 1) + " / " + files.Count;
                    UpdateButtons();
                    Invalidate();
                    if (img != null && afterLoad != null) { Action a = afterLoad; afterLoad = null; a(); }
                });
            });
            t.IsBackground = true; t.Start();
        }

        /// Decodes any image (HEIC/AVIF/PSD/RAW via ImageMagick) into a GDI+ image.
        public static Image Decode(string path, int max)
        {
            string ext = Formats.Ext(path);
            string src = path; bool temp = false;
            if (ext != "png" && ext != "bmp" && ext != "gif")
            {
                string magick = Tools.Magick;
                if (magick == null) { if (ext != "jpg" && ext != "jpeg") throw new Exception(L.T("err_need_im")); }
                else
                {
                    Directory.CreateDirectory(TempDir);
                    src = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + ".png");
                    temp = true;
                    string output;
                    int code = Tools.RunHidden(magick, Tools.Quote(path + "[0]") + " -auto-orient -resize \"" + max + "x" + max + ">\" " + Tools.Quote(src), 120000, out output);
                    if (code != 0 || !File.Exists(src)) throw new Exception(Tools.LastLines(output, 1));
                }
            }
            byte[] bytes = File.ReadAllBytes(src);
            if (temp) try { File.Delete(src); } catch { }
            return Image.FromStream(new MemoryStream(bytes));
        }

        void SaveJpg()
        {
            string p = Current;
            if (p == null || Tools.Magick == null) return;
            string outp = SaveAsk.File(FindForm(), Path.GetFileNameWithoutExtension(p) + ".jpg", "jpg");
            if (outp == null) return;
            string rot = canvas.Rotation != 0 ? " -rotate " + canvas.Rotation : "";
            info = L.T("saving"); Invalidate();
            Thread t = new Thread(() =>
            {
                string output;
                int code = Tools.RunHidden(Tools.Magick, Tools.Quote(p + "[0]") + " -auto-orient" + rot + " -background white -alpha remove -alpha off -quality 92 " + Tools.Quote(outp), 120000, out output);
                Ui(() =>
                {
                    Show(index);
                    if (code == 0 && File.Exists(outp))
                        ShowToast(L.T("v_saved", Path.GetFileName(outp)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + outp + "\""); } catch { } });
                    else ShowToast(Tools.LastLines(output, 1), false, null, null);
                });
            });
            t.IsBackground = true; t.Start();
        }

        public bool HandleKey(Keys k)
        {
            if (editing)
            {
                bool typing = editor.TextFocused;
                switch (k)
                {
                    case Keys.Control | Keys.S: editor.Save(); return true;
                    case Keys.Control | Keys.Z: if (typing) return false; editor.Undo(); return true;
                    case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: if (typing) return false; editor.Redo(); return true;
                    case Keys.Escape: if (!typing && editor.Deselect()) return true; ExitEdit(true); return true;
                    case Keys.Delete: return !typing && editor.DeleteKey();
                    case Keys.Left: if (typing) return false; if (Doc && editor.S.Current == null) { Go(Theme.RTL ? 1 : -1); return true; } editor.Nudge(-1, 0); return true;
                    case Keys.Right: if (typing) return false; if (Doc && editor.S.Current == null) { Go(Theme.RTL ? -1 : 1); return true; } editor.Nudge(1, 0); return true;
                    case Keys.PageUp: if (!Doc) return false; Go(-1); return true;
                    case Keys.PageDown: if (!Doc) return false; Go(1); return true;
                    case Keys.Up: if (typing) return false; editor.Nudge(0, -1); return true;
                    case Keys.Down: if (typing) return false; editor.Nudge(0, 1); return true;
                    case Keys.Control | Keys.D0: canvas.Zoom = 0; canvas.Invalidate(); return true;
                }
                return false;
            }
            switch (k)
            {
                case Keys.Right: Go(Theme.RTL ? -1 : 1); return true;
                case Keys.Left: Go(Theme.RTL ? 1 : -1); return true;
                case Keys.Control | Keys.R: Rotate(90); return true;
                case Keys.Control | Keys.S: SaveJpg(); return true;
                case Keys.Control | Keys.O: PickFile(); return true;
                case Keys.Control | Keys.E: EnterEdit(); return true;
                case Keys.Control | Keys.D0: canvas.Reset(); return true;
            }
            return false;
        }
    }

    /// Stand-alone viewer window (used when double-clicking a HEIC etc.).
    public class ViewerForm : ChromeForm
    {
        public ViewerPage Page;
        Toast toast;
        public ViewerForm(string[] files)
        {
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.F(9.75f);
            Text = L.T("viewer_title") + " — " + L.T("app_name");
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            Shell.FitToScreen(this, new Size(Theme.S(1180), Theme.S(800)), new Size(Theme.S(900), Theme.S(520)));
            KeyPreview = true;
            BackColor = Theme.BgBottom;
            Page = new ViewerPage { Dock = DockStyle.Fill, Owner = this, Glyph = Ico.Photo };
            toast = new Toast();
            Page.Toast = toast;
            Controls.Add(toast);
            Controls.Add(Page);
            Bar.Dock = DockStyle.Top;
            Bar.Title = () => L.T("viewer_title") + "  ·  " + L.T("app_name");
            Controls.Add(Bar);
            Page.SendToConverter += f =>
            {
                Thread t = new Thread(() =>
                {
                    bool sent = SingleInstance.MainRunning && SingleInstance.Send(new[] { f });
                    if (!sent) try { Process.Start(Application.ExecutablePath, Tools.Quote(f)); } catch { }
                });
                t.IsBackground = true; t.Start();
            };
            Resize += (s, e) => toast.Host = ClientRectangle;
            toast.Host = ClientRectangle;
            HandleCreated += (s, e) => Theme.ApplyTitleBar(this);
            if (files != null && files.Length > 0) Page.LoadFiles(files);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Page.ConfirmDiscard()) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Page.Editing && Page.HandleKey(keyData)) return true;
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.F11)
            {
                if (FormBorderStyle == FormBorderStyle.None) { FormBorderStyle = FormBorderStyle.Sizable; WindowState = FormWindowState.Normal; BarShown = true; }
                else { BarShown = false; FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Maximized; }
                return true;
            }
            if (Page.HandleKey(keyData)) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
