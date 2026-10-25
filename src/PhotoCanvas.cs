using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace FileConverter
{
    public enum PhotoTool { Move, Marquee, Ellipse, Lasso, Wand, Brush, Eraser, Remove, Text, Crop, Eyedropper, Hand, Zoom }

    /// The photo being edited, on screen: zoom and pan, rulers, the checkerboard under see-through parts,
    /// and every tool's mouse work (moving with handles and smart guides, selecting, painting, cropping…).
    public class PhotoCanvas : UiControl
    {
        public PhotoDoc Doc;
        public PhotoTool Tool = PhotoTool.Move;
        public bool ShowRulers = true;
        // tool settings (Properties sets them)
        public float BrushSize = 30, BrushHardness = 0.6f, BrushOpacity = 1f;
        public Color Fore = Color.FromArgb(240, 98, 46), Back = Color.White;
        public SelOp SelMode = SelOp.New;
        public int Feather = 0, WandTolerance = 32;
        public bool WandContiguous = true;
        public float CropRatio;            // 0 = free
        /// Something changed that the panels should know about (layer moved, selection made…).
        public event Action Changed;
        /// A change was made: (history name) — the studio pushes it into the history first.
        public Func<string, bool> BeforeChange;
        public event Action<PointF> TextAt;
        public event Action<Color> Picked;
        public event Action<List<PointF>> RemoveStroke;
        public event Action ZoomChanged;
        public event Action<Point> ContextMenuAt;

        public float Zoom = 1;         // screen pixels per photo pixel
        public PointF Pan;             // where photo pixel (0,0) is on screen
        Bitmap shown; float shownScale; int shownStamp = -1;
        int stamp;                     // bumped on every change: the picture is drawn again
        bool interacting;

        readonly Timer ants = new Timer { Interval = 120 };
        float antPhase;
        List<PointF[]> antSegs; float antScale = -1; byte[] antFor;

        public PhotoCanvas()
        {
            DoubleBuffered = true; TabStop = true;
            SetStyle(ControlStyles.Selectable, true);
            ants.Tick += (s, e) => { if (Doc != null && Doc.Sel != null && Visible) { antPhase = (antPhase + 1) % 8; Invalidate(); } };
            ants.Start();
        }

        protected override void Dispose(bool disposing) { if (disposing) { ants.Stop(); ants.Dispose(); if (shown != null) shown.Dispose(); } base.Dispose(disposing); }

        /// The picture changed: drawn again on the next paint.
        public void Redraw() { stamp++; Invalidate(); }

        int Ruler { get { return ShowRulers ? Theme.S(20) : 0; } }
        Rectangle View { get { return new Rectangle(Ruler, Ruler, Math.Max(1, Width - Ruler), Math.Max(1, Height - Ruler)); } }

        public void Fit()
        {
            if (Doc == null) return;
            Rectangle v = View;
            float m = Theme.S(28);
            Zoom = Math.Max(0.01f, Math.Min((v.Width - m * 2) / Doc.W, (v.Height - m * 2) / Doc.H));
            Zoom = Math.Min(Zoom, 4f);
            Pan = new PointF(v.X + (v.Width - Doc.W * Zoom) / 2, v.Y + (v.Height - Doc.H * Zoom) / 2);
            Invalidate();
            if (ZoomChanged != null) ZoomChanged();
        }

        public void ZoomAt(PointF screen, float z)
        {
            if (Doc == null) return;
            z = Math.Max(0.02f, Math.Min(32f, z));
            PointF d = ToDoc(screen);
            Zoom = z;
            Pan = new PointF(screen.X - d.X * Zoom, screen.Y - d.Y * Zoom);
            Invalidate();
            if (ZoomChanged != null) ZoomChanged();
        }
        public void ZoomCenter(float z) { Rectangle v = View; ZoomAt(new PointF(v.X + v.Width / 2f, v.Y + v.Height / 2f), z); }

        public PointF ToDoc(PointF s) { return new PointF((s.X - Pan.X) / Zoom, (s.Y - Pan.Y) / Zoom); }
        public PointF ToScreen(PointF d) { return new PointF(Pan.X + d.X * Zoom, Pan.Y + d.Y * Zoom); }
        RectangleF ToScreen(RectangleF r) { PointF a = ToScreen(r.Location); return new RectangleF(a.X, a.Y, r.Width * Zoom, r.Height * Zoom); }

        // ------------------------------------------------------------------ drawing
        /// The photo at a size good for the screen (smaller while something is being dragged, so it keeps up).
        Bitmap Picture()
        {
            float want = Math.Min(1f, Zoom);
            float maxSide = interacting ? 1100 : 2600;
            want = Math.Min(want, maxSide / Math.Max(Doc.W, Doc.H));
            want = Math.Max(want, 0.02f);
            if (shown != null && shownStamp == stamp && Math.Abs(shownScale - want) < 0.001f) return shown;
            if (shown != null && shownStamp == stamp && interacting && shownScale >= want * 0.5f && shownScale <= want * 2) return shown;
            Bitmap b;
            try { b = Doc.Compose(want); } catch { return shown; }
            if (shown != null) shown.Dispose();
            shown = b; shownScale = want; shownStamp = stamp;
            return shown;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Theme.Dark ? Color.FromArgb(24, 24, 26) : Color.FromArgb(226, 225, 222);
            g.Clear(bg);
            if (Doc == null) return;
            Rectangle v = View;
            g.SetClip(v);
            RectangleF page = new RectangleF(Pan.X, Pan.Y, Doc.W * Zoom, Doc.H * Zoom);
            // a soft shadow, then the checkerboard under see-through parts
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(Theme.Dark ? 90 : 40, 0, 0, 0))) g.FillRectangle(sh, page.X + 2, page.Y + 3, page.Width, page.Height);
            Checker(g, page);
            Bitmap pic = Picture();
            if (pic != null)
            {
                g.InterpolationMode = Zoom > shownScale * 1.5f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(pic, page);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSelection(g);
            DrawStroke(g);
            DrawGuides(g);
            DrawCrop(g);
            DrawHandles(g);
            DrawDrag(g);
            g.ResetClip();
            if (ShowRulers) DrawRulers(g, bg);
            DrawBrushCursor(g);
            if (Busy != null) DrawBusy(g, v);
        }

        static Bitmap checkTile;
        void Checker(Graphics g, RectangleF r)
        {
            if (checkTile == null)
            {
                checkTile = new Bitmap(16, 16);
                using (Graphics cg = Graphics.FromImage(checkTile))
                {
                    cg.Clear(Color.FromArgb(255, 255, 255));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(214, 214, 214))) { cg.FillRectangle(b, 0, 0, 8, 8); cg.FillRectangle(b, 8, 8, 8, 8); }
                }
            }
            using (TextureBrush tb = new TextureBrush(checkTile, WrapMode.Tile)) g.FillRectangle(tb, r);
        }

        /// A long job on the photo (remove object, select subject): a note over the canvas.
        public string Busy;
        /// Remove object: the stroke being filled in (shown until it's done).
        public List<PointF> PendingStroke;
        void DrawBusy(Graphics g, Rectangle v)
        {
            Font f = Theme.F(10f, FontStyle.Bold);
            int w = Theme.Measure(Busy, f) + Theme.S(56), h = Theme.S(44);
            Rectangle r = new Rectangle(v.X + (v.Width - w) / 2, v.Y + Theme.S(18), w, h);
            Theme.FillRound(g, r, h / 2f, Color.FromArgb(235, Theme.Dark ? Color.FromArgb(40, 40, 44) : Color.White));
            RectangleF ring = new RectangleF(r.X + Theme.S(14), r.Y + (h - Theme.S(18)) / 2f, Theme.S(18), Theme.S(18));
            using (Pen p = new Pen(Theme.Track, Theme.S(2.5f))) g.DrawEllipse(p, ring);
            using (Pen p = new Pen(Theme.AccentColor, Theme.S(2.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, ring, (Environment.TickCount / 4) % 360, 100);
            Theme.Text(g, Busy, f, Theme.Ink, new Rectangle(r.X + Theme.S(40), r.Y, r.Width - Theme.S(48), h), Align.Start);
            Invalidate(new Rectangle(r.X, r.Y, Theme.S(40), h));
        }

        void DrawSelection(Graphics g)
        {
            if (Doc.Sel == null) { antSegs = null; return; }
            if (antSegs == null || Math.Abs(antScale - Zoom) > 0.0001f || antFor != Doc.Sel)
            {
                antSegs = Selection.Outline(Doc.Sel, Doc.W, Doc.H, Zoom);
                antScale = Zoom; antFor = Doc.Sel;
            }
            using (Pen white = new Pen(Color.White, 1f))
            using (Pen black = new Pen(Color.Black, 1f) { DashPattern = new float[] { 4, 4 }, DashOffset = antPhase })
            {
                SmoothingMode sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                foreach (PointF[] s in antSegs)
                {
                    PointF a = new PointF(s[0].X + Pan.X, s[0].Y + Pan.Y), b = new PointF(s[1].X + Pan.X, s[1].Y + Pan.Y);
                    g.DrawLine(white, a, b); g.DrawLine(black, a, b);
                }
                g.SmoothingMode = sm;
            }
        }

        void DrawRulers(Graphics g, Color bg)
        {
            int r = Ruler;
            Color rc = Theme.Dark ? Color.FromArgb(32, 32, 35) : Color.FromArgb(244, 243, 241);
            using (SolidBrush b = new SolidBrush(rc)) { g.FillRectangle(b, 0, 0, Width, r); g.FillRectangle(b, 0, 0, r, Height); }
            using (Pen ln = new Pen(Theme.Line)) { g.DrawLine(ln, r, r - 1, Width, r - 1); g.DrawLine(ln, r - 1, r, r - 1, Height); }
            // a tick every "nice" number of photo pixels, at least 50 screen pixels apart
            double[] nice = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000, 10000 };
            double step = 10000;
            foreach (double n in nice) if (n * Zoom >= Theme.S(56)) { step = n; break; }
            Font f = Theme.F(6.75f);
            Color ink = Theme.InkFaint;
            using (Pen tp = new Pen(ink))
            {
                double start = Math.Floor(ToDoc(new PointF(r, 0)).X / step) * step;
                for (double x = start; ; x += step)
                {
                    float sx = Pan.X + (float)(x * Zoom);
                    if (sx > Width) break;
                    if (sx < r) continue;
                    g.DrawLine(tp, sx, r - Theme.S(7), sx, r - 1);
                    for (int k = 1; k < 5; k++) { float mx = sx + (float)(step * Zoom * k / 5); if (mx < Width) g.DrawLine(tp, mx, r - Theme.S(3), mx, r - 1); }
                    TextRenderer.DrawText(g, ((int)x).ToString(), f, new Point((int)sx + 2, 1), ink, TextFormatFlags.NoPadding);
                }
                start = Math.Floor(ToDoc(new PointF(0, r)).Y / step) * step;
                for (double y = start; ; y += step)
                {
                    float sy = Pan.Y + (float)(y * Zoom);
                    if (sy > Height) break;
                    if (sy < r) continue;
                    g.DrawLine(tp, r - Theme.S(7), sy, r - 1, sy);
                    for (int k = 1; k < 5; k++) { float my = sy + (float)(step * Zoom * k / 5); if (my < Height) g.DrawLine(tp, r - Theme.S(3), my, r - 1, my); }
                    GraphicsState st = g.Save();
                    g.TranslateTransform(1, sy + 2); g.RotateTransform(90);
                    TextRenderer.DrawText(g, ((int)y).ToString(), f, new Point(0, -r + 2), ink, TextFormatFlags.NoPadding);
                    g.Restore(st);
                }
                // where the mouse is
                if (mouseIn)
                {
                    using (Pen mp = new Pen(Theme.AccentColor)) { g.DrawLine(mp, mouse.X, 0, mouse.X, r); g.DrawLine(mp, 0, mouse.Y, r, mouse.Y); }
                }
            }
            using (SolidBrush b = new SolidBrush(rc)) g.FillRectangle(b, 0, 0, r - 1, r - 1);
        }

        // ------------------------------------------------------------------ handles (Move)
        enum Grab { None, Move, Scale, Rotate, Pan, Select, Lasso, Stroke, Crop, CropMove }
        Grab grab;
        int grabCorner;                // 0..3 corners, 4..7 sides (crop)
        PointF downDoc, downScreen;
        DocLayer grabLayer;
        float startX, startY, startScale, startAngle, startSize; PointF startPos;
        RectangleF startBounds;
        bool changedThisDrag;
        List<PointF> stroke = new List<PointF>();
        RectangleF dragRect; bool dragEllipse;
        List<RectangleF> guidesX = new List<RectangleF>();
        public RectangleF CropRect;     // in photo pixels (Crop tool)
        RectangleF cropStart;

        DocLayer Movable { get { DocLayer l = Doc == null ? null : Doc.Cur; return l != null && (l.Kind == LayerKind.Image || l.Kind == LayerKind.Text || l.Kind == LayerKind.Group || (l.Kind == LayerKind.Paint && !l.Bounds(Doc).IsEmpty)) ? l : null; } }

        /// The current layer's frame on screen: 4 corners (turned with the layer).
        PointF[] Frame(DocLayer l)
        {
            if (l.Kind == LayerKind.Image)
            {
                PointF[] c = l.Corners();
                for (int i = 0; i < c.Length; i++) c[i] = ToScreen(c[i]);
                return c;
            }
            RectangleF b = ToScreen(l.Bounds(Doc));
            if (b.IsEmpty) return null;
            return new[] { new PointF(b.Left, b.Top), new PointF(b.Right, b.Top), new PointF(b.Right, b.Bottom), new PointF(b.Left, b.Bottom) };
        }

        void DrawHandles(Graphics g)
        {
            if (Tool != PhotoTool.Move && Tool != PhotoTool.Text) return;
            DocLayer l = Movable;
            if (l == null || !Doc.Shown(l)) return;
            PointF[] f = Frame(l);
            if (f == null) return;
            using (Pen p = new Pen(Theme.AccentColor, Theme.S(1.25f))) g.DrawPolygon(p, f);
            if (Doc.IsLocked(l) || l.Kind == LayerKind.Group) return;
            float hs = Theme.S(9);
            foreach (PointF c in f) DrawHandle(g, c, hs);
            // the turn handle above the top edge
            PointF top = Mid(f[0], f[1]), rot = RotHandle(f);
            using (Pen p = new Pen(Theme.AccentColor, Theme.S(1.25f))) g.DrawLine(p, top, rot);
            RectangleF rr = new RectangleF(rot.X - hs / 2 - 1, rot.Y - hs / 2 - 1, hs + 2, hs + 2);
            using (SolidBrush b = new SolidBrush(Theme.AccentColor)) g.FillEllipse(b, rr);
            using (Pen p = new Pen(Color.White, Theme.S(1.5f))) g.DrawEllipse(p, RectangleF.Inflate(rr, -2, -2));
        }

        static PointF Mid(PointF a, PointF b) { return new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2); }
        PointF RotHandle(PointF[] f)
        {
            PointF top = Mid(f[0], f[1]), bot = Mid(f[3], f[2]);
            float dx = top.X - bot.X, dy = top.Y - bot.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.01f) return new PointF(top.X, top.Y - Theme.S(26));
            return new PointF(top.X + dx / len * Theme.S(26), top.Y + dy / len * Theme.S(26));
        }

        static void DrawHandle(Graphics g, PointF c, float s)
        {
            RectangleF r = new RectangleF(c.X - s / 2, c.Y - s / 2, s, s);
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillRectangle(b, r);
            using (Pen p = new Pen(Theme.AccentColor, 1.5f)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
        }

        void DrawGuides(Graphics g)
        {
            if (guidesX.Count == 0) return;
            using (Pen p = new Pen(Color.FromArgb(236, 64, 160), 1f))
                foreach (RectangleF gl in guidesX)
                {
                    PointF a = ToScreen(gl.Location), b = ToScreen(new PointF(gl.Right, gl.Bottom));
                    g.DrawLine(p, a, b);
                }
        }

        void DrawDrag(Graphics g)
        {
            if (grab == Grab.Select && !dragRect.IsEmpty)
            {
                RectangleF r = ToScreen(dragRect);
                using (Pen p = new Pen(Color.White, 1f) { DashPattern = new float[] { 4, 3 } })
                {
                    if (dragEllipse) g.DrawEllipse(p, r); else g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                }
                using (Pen p = new Pen(Color.FromArgb(160, 0, 0, 0), 1f) { DashPattern = new float[] { 4, 3 }, DashOffset = 3.5f })
                {
                    if (dragEllipse) g.DrawEllipse(p, r); else g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                }
            }
            if (grab == Grab.Lasso && stroke.Count > 1)
            {
                PointF[] pts = stroke.ConvertAll(ToScreen).ToArray();
                using (Pen p = new Pen(Color.White, 1.2f)) g.DrawLines(p, pts);
                using (Pen p = new Pen(Color.Black, 1.2f) { DashPattern = new float[] { 4, 3 } }) g.DrawLines(p, pts);
            }
        }

        /// While painting: the stroke so far, drawn as the brush would leave it.
        void DrawStroke(Graphics g)
        {
            if (PendingStroke != null && PendingStroke.Count > 0 && grab != Grab.Stroke)
            {
                PointF[] pp = PendingStroke.ConvertAll(ToScreen).ToArray();
                float pw = BrushSize * Zoom;
                using (Pen p = new Pen(Color.FromArgb(110, 236, 64, 64), Math.Max(1, pw)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (pp.Length == 1) using (SolidBrush b = new SolidBrush(p.Color)) g.FillEllipse(b, pp[0].X - pw / 2, pp[0].Y - pw / 2, pw, pw);
                    else g.DrawLines(p, pp);
                }
            }
            if (grab != Grab.Stroke || stroke.Count == 0) return;
            Color c = Tool == PhotoTool.Brush ? Color.FromArgb((int)(255 * BrushOpacity * 0.85f), Fore)
                    : Tool == PhotoTool.Remove ? Color.FromArgb(120, 236, 64, 64)
                    : Color.FromArgb(110, 255, 255, 255);
            float w = BrushSize * Zoom;
            PointF[] pts = stroke.ConvertAll(ToScreen).ToArray();
            using (Pen p = new Pen(c, Math.Max(1, w)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (pts.Length == 1) using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, pts[0].X - w / 2, pts[0].Y - w / 2, w, w);
                else g.DrawLines(p, pts);
            }
            if (Tool == PhotoTool.Eraser && pts.Length > 1)
                using (Pen p = new Pen(Color.FromArgb(160, 0, 0, 0), 1f) { DashPattern = new float[] { 3, 3 } }) g.DrawLines(p, pts);
        }

        void DrawBrushCursor(Graphics g)
        {
            if (!mouseIn || Doc == null) return;
            if (Tool != PhotoTool.Brush && Tool != PhotoTool.Eraser && Tool != PhotoTool.Remove) return;
            float r = Math.Max(2, BrushSize * Zoom / 2);
            using (Pen p = new Pen(Color.Black, 1f)) g.DrawEllipse(p, mouse.X - r, mouse.Y - r, r * 2, r * 2);
            using (Pen p = new Pen(Color.White, 1f)) g.DrawEllipse(p, mouse.X - r + 1, mouse.Y - r + 1, r * 2 - 2, r * 2 - 2);
        }

        void DrawCrop(Graphics g)
        {
            if (Tool != PhotoTool.Crop || CropRect.IsEmpty) return;
            RectangleF r = ToScreen(CropRect), page = ToScreen(new RectangleF(0, 0, Doc.W, Doc.H));
            using (Region out1 = new Region(page))
            {
                out1.Exclude(r);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRegion(b, out1);
            }
            using (Pen p = new Pen(Color.FromArgb(120, 255, 255, 255), 1f))
                for (int i = 1; i < 3; i++) { g.DrawLine(p, r.X + r.Width * i / 3, r.Y, r.X + r.Width * i / 3, r.Bottom); g.DrawLine(p, r.X, r.Y + r.Height * i / 3, r.Right, r.Y + r.Height * i / 3); }
            using (Pen p = new Pen(Color.White, Theme.S(1.5f))) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            float hs = Theme.S(9);
            foreach (PointF c in CropHandles(r)) DrawHandle(g, c, hs);
            string size = (int)Math.Round(CropRect.Width) + " × " + (int)Math.Round(CropRect.Height);
            Font f = Theme.F(8.5f, FontStyle.Bold);
            int tw = Theme.Measure(size, f) + Theme.S(16);
            Rectangle tr = new Rectangle((int)(r.X + r.Width / 2 - tw / 2), (int)r.Bottom + Theme.S(8), tw, Theme.S(24));
            Theme.FillRound(g, tr, Theme.S(12), Color.FromArgb(200, 0, 0, 0));
            Theme.Text(g, size, f, Color.White, tr, Align.Center);
        }

        static PointF[] CropHandles(RectangleF r)
        {
            return new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Bottom), new PointF(r.Left, r.Bottom),
                           new PointF(r.X + r.Width / 2, r.Top), new PointF(r.Right, r.Y + r.Height / 2), new PointF(r.X + r.Width / 2, r.Bottom), new PointF(r.Left, r.Y + r.Height / 2) };
        }

        public void StartCrop()
        {
            if (Doc == null) return;
            CropRect = new RectangleF(0, 0, Doc.W, Doc.H);
            if (CropRatio > 0) FitCropRatio();
            Invalidate();
        }

        /// The biggest frame of the chosen shape, in the middle of the photo.
        public void FitCropRatio()
        {
            if (Doc == null || CropRatio <= 0) { Invalidate(); return; }
            float w = Doc.W, h = w / CropRatio;
            if (h > Doc.H) { h = Doc.H; w = h * CropRatio; }
            CropRect = new RectangleF((Doc.W - w) / 2, (Doc.H - h) / 2, w, h);
            Invalidate();
        }

        // ------------------------------------------------------------------ mouse
        Point mouse; bool mouseIn;
        bool spaceDown;
        public bool SpaceHeld { set { spaceDown = value; Cursor = CursorFor(); } }

        protected override void OnMouseEnter(EventArgs e) { mouseIn = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { mouseIn = false; Invalidate(); base.OnMouseLeave(e); }

        Cursor CursorFor()
        {
            if (Doc == null) return Cursors.Default;
            if (spaceDown || Tool == PhotoTool.Hand) return grab == Grab.Pan ? Cursors.SizeAll : Cursors.Hand;
            switch (Tool)
            {
                case PhotoTool.Brush: case PhotoTool.Eraser: case PhotoTool.Remove: return Cursors.Cross;
                case PhotoTool.Marquee: case PhotoTool.Ellipse: case PhotoTool.Lasso: case PhotoTool.Wand: case PhotoTool.Crop: case PhotoTool.Eyedropper: return Cursors.Cross;
                case PhotoTool.Text: return Cursors.IBeam;
                case PhotoTool.Zoom: return Cursors.Hand;
            }
            return Cursors.Default;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Doc == null) return;
            if ((ModifierKeys & Keys.Shift) != 0) { Pan = new PointF(Pan.X + e.Delta, Pan.Y); Invalidate(); return; }
            if ((ModifierKeys & Keys.Control) != 0) { Pan = new PointF(Pan.X, Pan.Y + e.Delta); Invalidate(); return; }
            ZoomAt(e.Location, Zoom * (float)Math.Pow(1.2, e.Delta / 120.0));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (Doc == null || Busy != null) return;
            downScreen = e.Location; downDoc = ToDoc(e.Location);
            changedThisDrag = false;
            if (e.Button == MouseButtons.Middle || spaceDown || Tool == PhotoTool.Hand) { grab = Grab.Pan; Cursor = Cursors.SizeAll; return; }
            if (e.Button != MouseButtons.Left) return;
            Keys mods = ModifierKeys;
            switch (Tool)
            {
                case PhotoTool.Zoom: ZoomAt(e.Location, (mods & Keys.Alt) != 0 ? Zoom / 1.5f : Zoom * 1.5f); return;
                case PhotoTool.Eyedropper: PickColor(downDoc); return;
                case PhotoTool.Wand:
                    {
                        using (Bitmap flat = Doc.Flatten())
                        {
                            byte[] m = Selection.Wand(flat, Point.Truncate(downDoc), WandTolerance, WandContiguous);
                            ApplySel(m, OpFor(mods), "ps_h_select");
                        }
                        return;
                    }
                case PhotoTool.Marquee: case PhotoTool.Ellipse:
                    grab = Grab.Select; dragRect = RectangleF.Empty; dragEllipse = Tool == PhotoTool.Ellipse; return;
                case PhotoTool.Lasso: grab = Grab.Lasso; stroke.Clear(); stroke.Add(downDoc); return;
                case PhotoTool.Brush: case PhotoTool.Eraser: case PhotoTool.Remove:
                    grab = Grab.Stroke; stroke.Clear(); stroke.Add(downDoc); interacting = true; Invalidate(); return;
                case PhotoTool.Crop: CropDown(e.Location); return;
                case PhotoTool.Text:
                    {
                        // on a text layer: move it; elsewhere: new text there
                        DocLayer hit = HitLayer(downDoc);
                        if (hit != null && hit.Kind == LayerKind.Text) { Select(hit); BeginMove(e.Location, mods); return; }
                        if (TextAt != null) TextAt(downDoc);
                        return;
                    }
            }
            // Move: a handle of the current layer, else whatever is under the mouse
            BeginMove(e.Location, mods);
        }

        void BeginMove(Point at, Keys mods)
        {
            DocLayer l = Movable;
            if (l != null && !Doc.IsLocked(l) && l.Kind != LayerKind.Group && Doc.Shown(l))
            {
                PointF[] f = Frame(l);
                if (f != null)
                {
                    float hs = Theme.S(12);
                    PointF rot = RotHandle(f);
                    if (Dist(at, rot) <= hs) { StartGrab(l, Grab.Rotate, 0); return; }
                    for (int i = 0; i < 4; i++) if (Dist(at, f[i]) <= hs) { StartGrab(l, Grab.Scale, i); return; }
                    if (Inside(f, at)) { StartGrab(l, Grab.Move, 0); return; }
                }
            }
            DocLayer hit = HitLayer(downDoc);
            if (hit != null)
            {
                // a layer in a group moves the whole group when the group is selected
                DocLayer gp = l != null && l.Kind == LayerKind.Group && hit.Group == l.Id ? l : hit;
                Select(gp);
                if (!Doc.IsLocked(gp)) StartGrab(gp, Grab.Move, 0);
            }
        }

        static float Dist(PointF a, PointF b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }
        static bool Inside(PointF[] poly, PointF p)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y)) && (p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)) c = !c;
            return c;
        }

        /// The top layer that shows something at this point (pictures, text; not adjustments).
        public DocLayer HitLayer(PointF d)
        {
            for (int i = Doc.Layers.Count - 1; i >= 0; i--)
            {
                DocLayer l = Doc.Layers[i];
                if (!Doc.Shown(l) || l.Kind == LayerKind.Adjust || l.Kind == LayerKind.Group) continue;
                if (l.Kind == LayerKind.Text) { if (l.Text != null && l.Text.Visible && l.Text.Bounds.Contains(d)) return l; continue; }
                if (l.Pixels == null) continue;
                PointF lp = l.Kind == LayerKind.Image ? l.ToLayer(d) : d;
                int x = (int)lp.X, y = (int)lp.Y;
                if (x < 0 || y < 0 || x >= l.Pixels.Width || y >= l.Pixels.Height) continue;
                if (l.Pixels.GetPixel(x, y).A < 20) continue;
                if (l.Mask != null && l.Mask[y * l.Pixels.Width + x] < 20) continue;
                return l;
            }
            return null;
        }

        void Select(DocLayer l)
        {
            int i = Doc.Layers.IndexOf(l);
            if (i >= 0 && i != Doc.Current) { Doc.Current = i; if (Changed != null) Changed(); }
        }

        void StartGrab(DocLayer l, Grab kind, int corner)
        {
            // a painting is moved as a picture (just what's painted)
            if (l.Kind == LayerKind.Paint) { Doc.PaintToImage(l); if (l.Kind != LayerKind.Image) return; }
            grab = kind; grabLayer = l; grabCorner = corner;
            interacting = true;
            if (l.Kind == LayerKind.Image) { startX = l.X; startY = l.Y; startScale = l.Scale; startAngle = l.Angle; }
            else if (l.Kind == LayerKind.Text) { startPos = l.Text.Pos; startSize = l.Text.SizePx; startAngle = l.Text.Angle; }
            startBounds = l.Bounds(Doc);
            groupStart.Clear();
            if (l.Kind == LayerKind.Group)
                foreach (DocLayer c in Doc.Layers) if (c.Group == l.Id) groupStart[c] = c.Kind == LayerKind.Text ? c.Text.Pos : new PointF(c.X, c.Y);
        }
        readonly Dictionary<DocLayer, PointF> groupStart = new Dictionary<DocLayer, PointF>();

        /// The first change of a drag goes into the history.
        bool Begin(string name)
        {
            if (changedThisDrag) return true;
            if (BeforeChange != null && !BeforeChange(name)) return false;
            changedThisDrag = true;
            return true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location; mouseIn = true;
            if (Doc == null) return;
            PointF d = ToDoc(e.Location);
            if (Tool == PhotoTool.Brush || Tool == PhotoTool.Eraser || Tool == PhotoTool.Remove || ShowRulers) Invalidate();
            switch (grab)
            {
                case Grab.Pan:
                    Pan = new PointF(Pan.X + e.X - downScreen.X, Pan.Y + e.Y - downScreen.Y); downScreen = e.Location; Invalidate(); return;
                case Grab.Select:
                    {
                        float x0 = Math.Min(downDoc.X, d.X), y0 = Math.Min(downDoc.Y, d.Y), w = Math.Abs(d.X - downDoc.X), h = Math.Abs(d.Y - downDoc.Y);
                        if ((ModifierKeys & Keys.Shift) != 0 && SelMode == SelOp.New) { float s = Math.Max(w, h); w = h = s; if (d.X < downDoc.X) x0 = downDoc.X - s; if (d.Y < downDoc.Y) y0 = downDoc.Y - s; }
                        dragRect = new RectangleF(x0, y0, w, h); Invalidate(); return;
                    }
                case Grab.Lasso: case Grab.Stroke:
                    if (Dist(ToScreen(stroke[stroke.Count - 1]), e.Location) >= 2) { stroke.Add(d); Invalidate(); }
                    return;
                case Grab.Crop: case Grab.CropMove: CropDrag(d); return;
                case Grab.Move: case Grab.Scale: case Grab.Rotate:
                    DragLayer(d, e.Location);
                    return;
            }
            Cursor = HoverCursor(e.Location);
        }

        Cursor HoverCursor(Point at)
        {
            if (spaceDown || Tool == PhotoTool.Hand) return Cursors.Hand;
            if (Tool == PhotoTool.Move || Tool == PhotoTool.Text)
            {
                DocLayer l = Movable;
                if (l != null && !Doc.IsLocked(l) && Doc.Shown(l) && l.Kind != LayerKind.Group)
                {
                    PointF[] f = Frame(l);
                    if (f != null)
                    {
                        if (Dist(at, RotHandle(f)) <= Theme.S(12)) return Cursors.Hand;
                        for (int i = 0; i < 4; i++) if (Dist(at, f[i]) <= Theme.S(12)) return i % 2 == 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
                        if (Inside(f, at)) return Cursors.SizeAll;
                    }
                }
            }
            if (Tool == PhotoTool.Crop)
            {
                PointF[] h = CropHandles(ToScreen(CropRect));
                for (int i = 0; i < 8; i++) if (Dist(at, h[i]) <= Theme.S(12)) return i < 4 ? (i % 2 == 0 ? Cursors.SizeNWSE : Cursors.SizeNESW) : (i % 2 == 0 ? Cursors.SizeNS : Cursors.SizeWE);
                if (ToScreen(CropRect).Contains(at)) return Cursors.SizeAll;
            }
            return CursorFor();
        }

        void DragLayer(PointF d, Point screen)
        {
            DocLayer l = grabLayer;
            if (l == null) return;
            if (!Begin(grab == Grab.Move ? "ps_h_move" : grab == Grab.Scale ? "ps_h_resize" : "ps_h_turn")) return;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            guidesX.Clear();
            if (grab == Grab.Move)
            {
                float dx = d.X - downDoc.X, dy = d.Y - downDoc.Y;
                if (shift) { if (Math.Abs(dx) > Math.Abs(dy)) dy = 0; else dx = 0; }
                // smart guides: the layer's edges and middle snap to the photo's and other layers'
                RectangleF b = startBounds; b.Offset(dx, dy);
                float sx, sy;
                Snap(b, l, out sx, out sy);
                dx += sx; dy += sy;
                MoveBy(l, dx, dy);
            }
            else if (grab == Grab.Scale)
            {
                PointF c = l.Kind == LayerKind.Image ? new PointF(startX, startY) : new PointF(startBounds.X + startBounds.Width / 2, startBounds.Y + startBounds.Height / 2);
                float d0 = Dist(downDoc, c), d1 = Dist(d, c);
                float k = d0 < 0.5f ? 1 : Math.Max(0.02f, d1 / d0);
                if (l.Kind == LayerKind.Image) l.Scale = Math.Max(0.005f, startScale * k);
                else if (l.Kind == LayerKind.Text) { l.Text.SizePx = Math.Max(4, startSize * k); l.Text.SizeChanging(); }
            }
            else if (grab == Grab.Rotate)
            {
                PointF c = l.Kind == LayerKind.Image ? new PointF(l.X, l.Y) : new PointF(startBounds.X + startBounds.Width / 2, startBounds.Y + startBounds.Height / 2);
                double a0 = Math.Atan2(downDoc.Y - c.Y, downDoc.X - c.X), a1 = Math.Atan2(d.Y - c.Y, d.X - c.X);
                float ang = startAngle + (float)((a1 - a0) * 180 / Math.PI);
                if (shift) ang = (float)Math.Round(ang / 15) * 15;
                else { float near = (float)Math.Round(ang / 90) * 90; if (Math.Abs(ang - near) < 2.5f) ang = near; }
                ang = ((ang % 360) + 360) % 360; if (ang > 180) ang -= 360;
                if (l.Kind == LayerKind.Image) l.Angle = ang;
                else if (l.Kind == LayerKind.Text) { l.Text.Angle = ang; l.Text.Touch(); }
            }
            l.Version++;
            Redraw();
            if (Changed != null) Changed();
        }

        void MoveBy(DocLayer l, float dx, float dy)
        {
            if (l.Kind == LayerKind.Image) { l.X = startX + dx; l.Y = startY + dy; }
            else if (l.Kind == LayerKind.Text) l.Text.Pos = new PointF(startPos.X + dx, startPos.Y + dy);
            else if (l.Kind == LayerKind.Group)
                foreach (KeyValuePair<DocLayer, PointF> kv in groupStart)
                {
                    if (kv.Key.Kind == LayerKind.Text) kv.Key.Text.Pos = new PointF(kv.Value.X + dx, kv.Value.Y + dy);
                    else { kv.Key.X = kv.Value.X + dx; kv.Key.Y = kv.Value.Y + dy; }
                    kv.Key.Version++;
                }
        }

        /// Smart guides: how far to nudge so an edge or middle lines up (within 6 screen pixels); the lines to show.
        void Snap(RectangleF b, DocLayer self, out float sx, out float sy)
        {
            sx = 0; sy = 0;
            float tol = Theme.S(6) / Zoom;
            List<float> xs = new List<float> { 0, Doc.W / 2f, Doc.W }, ys = new List<float> { 0, Doc.H / 2f, Doc.H };
            foreach (DocLayer o in Doc.Layers)
            {
                if (o == self || o.Group == self.Id || !Doc.Shown(o) || o.Kind == LayerKind.Adjust || o.Kind == LayerKind.Group) continue;
                RectangleF r = o.Bounds(Doc);
                if (r.IsEmpty || (r.Width >= Doc.W - 1 && r.Height >= Doc.H - 1)) continue;
                xs.Add(r.Left); xs.Add(r.X + r.Width / 2); xs.Add(r.Right);
                ys.Add(r.Top); ys.Add(r.Y + r.Height / 2); ys.Add(r.Bottom);
            }
            float[] mine = { b.Left, b.X + b.Width / 2, b.Right };
            float best = tol + 1; float bx = float.NaN;
            foreach (float m in mine) foreach (float x in xs) { float dd = Math.Abs(x - m); if (dd <= tol && dd < best) { best = dd; sx = x - m; bx = x; } }
            float[] mineY = { b.Top, b.Y + b.Height / 2, b.Bottom };
            best = tol + 1; float by = float.NaN;
            foreach (float m in mineY) foreach (float y in ys) { float dd = Math.Abs(y - m); if (dd <= tol && dd < best) { best = dd; sy = y - m; by = y; } }
            if (!float.IsNaN(bx)) guidesX.Add(new RectangleF(bx, -10000, 0, 20000));
            if (!float.IsNaN(by)) guidesX.Add(new RectangleF(-10000, by, 20000, 0));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right && grab == Grab.None && Doc != null && Busy == null)
            {
                // right click: pick what's under the mouse first (when moving things)
                if (Tool == PhotoTool.Move) { DocLayer hit = HitLayer(ToDoc(e.Location)); if (hit != null) Select(hit); }
                if (ContextMenuAt != null) ContextMenuAt(e.Location);
                return;
            }
            Grab was = grab;
            grab = Grab.None;
            bool wasInteracting = interacting;
            interacting = false;
            guidesX.Clear();
            if (Doc == null) return;
            Keys mods = ModifierKeys;
            switch (was)
            {
                case Grab.Select:
                    {
                        if (dragRect.Width < 1 || dragRect.Height < 1)
                        {
                            // a click: deselect (New), or nothing
                            if (OpFor(mods) == SelOp.New && Doc.Sel != null) ApplySel(null, SelOp.New, "ps_h_deselect");
                        }
                        else ApplySel(Selection.Rect(dragRect, Doc.W, Doc.H, dragEllipse), OpFor(mods), "ps_h_select");
                        dragRect = RectangleF.Empty;
                        break;
                    }
                case Grab.Lasso:
                    if (stroke.Count > 2) ApplySel(Selection.Lasso(stroke, Doc.W, Doc.H), OpFor(mods), "ps_h_select");
                    stroke.Clear();
                    break;
                case Grab.Stroke: FinishStroke(); break;
                case Grab.Move: case Grab.Scale: case Grab.Rotate:
                    if (grabLayer != null && grabLayer.Kind == LayerKind.Text) { grabLayer.Text.Touch(); }
                    grabLayer = null;
                    break;
            }
            Cursor = HoverCursor(e.Location);
            if (wasInteracting) Redraw();
            Invalidate();
        }

        SelOp OpFor(Keys mods)
        {
            if ((mods & Keys.Shift) != 0 && (mods & Keys.Alt) != 0) return SelOp.Intersect;
            if ((mods & Keys.Shift) != 0) return SelOp.Add;
            if ((mods & Keys.Alt) != 0) return SelOp.Subtract;
            return SelMode;
        }

        public void ApplySel(byte[] m, SelOp op, string name)
        {
            if (BeforeChange != null && !BeforeChange(name)) return;
            if (m != null && Feather > 0) m = Selection.Feather(m, Doc.W, Doc.H, Feather);
            Doc.Sel = op == SelOp.New ? (Selection.Empty(m) ? null : m) : Selection.Combine(Doc.Sel, m, op);
            antSegs = null;
            Invalidate();
            if (Changed != null) Changed();
        }

        void PickColor(PointF d)
        {
            int x = (int)d.X, y = (int)d.Y;
            if (x < 0 || y < 0 || x >= Doc.W || y >= Doc.H) return;
            using (Bitmap one = Doc.Compose(1f))
            {
                Color c = one.GetPixel(x, y);
                Fore = Color.FromArgb(255, c.R, c.G, c.B);
            }
            if (Picked != null) Picked(Fore);
        }

        /// The brush let go: the stroke is put on the layer (painted, erased, or removed).
        void FinishStroke()
        {
            List<PointF> pts = new List<PointF>(stroke);
            stroke.Clear();
            if (pts.Count == 0) return;
            if (Tool == PhotoTool.Remove) { if (RemoveStroke != null) RemoveStroke(pts); return; }
            DocLayer l = Doc.Cur;
            if (Tool == PhotoTool.Brush)
            {
                // painting goes on a painting layer: the current one, or a new one just above
                if (l == null || l.Kind != LayerKind.Paint || Doc.IsLocked(l))
                {
                    if (BeforeChange != null && !BeforeChange("ps_h_paint")) return;
                    changedThisDrag = true;
                    DocLayer p = PhotoDoc.NewPaint(Doc.W, Doc.H, Doc.NextName("ps_l_layer"));
                    int at = Doc.Current < 0 ? Doc.Layers.Count : Doc.Current + 1;
                    if (l != null) p.Group = l.Kind == LayerKind.Group ? l.Id : l.Group;
                    Doc.Layers.Insert(at, p); Doc.Current = at; l = p;
                }
                else if (!Begin("ps_h_paint")) return;
                l.Pixels = Brush.Paint(l.Pixels, pts, BrushSize / 2, BrushHardness, BrushOpacity, Fore, false, Doc.Sel);
            }
            else if (Tool == PhotoTool.Eraser)
            {
                if (l == null || Doc.IsLocked(l) || !(l.HasPixels || l.Kind == LayerKind.Adjust)) { if (Changed != null) Changed(); return; }
                if (!Begin("ps_h_erase")) return;
                bool restore = (ModifierKeys & Keys.Alt) != 0;
                if (l.Kind == LayerKind.Paint && !restore) l.Pixels = Brush.Paint(l.Pixels, pts, BrushSize / 2, BrushHardness, BrushOpacity, Color.Black, true, Doc.Sel);
                else if (l.Kind == LayerKind.Image) l.Mask = Brush.EraseMask(l, pts, BrushSize / 2, BrushHardness, BrushOpacity, restore, Doc.Sel, Doc.W);
                else if (l.Kind == LayerKind.Adjust || (l.Kind == LayerKind.Paint && l.Mask != null)) l.Mask = Brush.EraseCanvasMask(l.Mask, Doc.W, Doc.H, pts, BrushSize / 2, BrushHardness, BrushOpacity, restore, Doc.Sel);
            }
            if (l != null) l.Version++;
            Redraw();
            if (Changed != null) Changed();
        }

        // ------------------------------------------------------------------ crop
        void CropDown(Point at)
        {
            if (CropRect.IsEmpty) StartCrop();
            PointF[] h = CropHandles(ToScreen(CropRect));
            cropStart = CropRect;
            for (int i = 0; i < 8; i++) if (Dist(at, h[i]) <= Theme.S(12)) { grab = Grab.Crop; grabCorner = i; return; }
            if (ToScreen(CropRect).Contains(at)) { grab = Grab.CropMove; return; }
            // drawn anew
            grab = Grab.Crop; grabCorner = 2;
            cropStart = new RectangleF(downDoc.X, downDoc.Y, 0, 0);
        }

        void CropDrag(PointF d)
        {
            RectangleF r = cropStart;
            float dx = d.X - downDoc.X, dy = d.Y - downDoc.Y;
            if (grab == Grab.CropMove)
            {
                r.X = Math.Max(0, Math.Min(Doc.W - r.Width, r.X + dx));
                r.Y = Math.Max(0, Math.Min(Doc.H - r.Height, r.Y + dy));
                CropRect = r; Invalidate(); return;
            }
            float l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            switch (grabCorner)
            {
                case 0: l += dx; t += dy; break;
                case 1: rt += dx; t += dy; break;
                case 2: rt += dx; b += dy; break;
                case 3: l += dx; b += dy; break;
                case 4: t += dy; break;
                case 5: rt += dx; break;
                case 6: b += dy; break;
                case 7: l += dx; break;
            }
            l = Math.Max(0, l); t = Math.Max(0, t); rt = Math.Min(Doc.W, rt); b = Math.Min(Doc.H, b);
            if (rt - l < 4) { if (grabCorner == 0 || grabCorner == 3 || grabCorner == 7) l = rt - 4; else rt = l + 4; }
            if (b - t < 4) { if (grabCorner == 0 || grabCorner == 1 || grabCorner == 4) t = b - 4; else b = t + 4; }
            RectangleF n = RectangleF.FromLTRB(l, t, rt, b);
            if (CropRatio > 0)
            {
                // keep the shape: the height follows the width (from the fixed corner)
                float w = n.Width, h = w / CropRatio;
                if (grabCorner == 4 || grabCorner == 6) { h = n.Height; w = h * CropRatio; }
                if (grabCorner == 0 || grabCorner == 1 || grabCorner == 4) n = RectangleF.FromLTRB(grabCorner == 0 ? n.Right - w : n.Left, n.Bottom - h, grabCorner == 0 ? n.Right : n.Left + w, n.Bottom);
                else n = new RectangleF(grabCorner == 3 || grabCorner == 7 ? n.Right - w : n.Left, n.Top, w, h);
                n.Intersect(new RectangleF(0, 0, Doc.W, Doc.H));
            }
            CropRect = n;
            Invalidate();
        }

        // ------------------------------------------------------------------ keys
        protected override bool IsInputKey(Keys k)
        {
            Keys c = k & Keys.KeyCode;
            return c == Keys.Left || c == Keys.Right || c == Keys.Up || c == Keys.Down || base.IsInputKey(k);
        }

        /// Arrow keys nudge the current layer (Shift: 10 pixels).
        public bool Nudge(Keys k)
        {
            DocLayer l = Movable;
            if (Doc == null || l == null || Doc.IsLocked(l)) return false;
            Keys c = k & Keys.KeyCode;
            float step = (k & Keys.Shift) != 0 ? 10 : 1, dx = 0, dy = 0;
            if (c == Keys.Left) dx = -step; else if (c == Keys.Right) dx = step; else if (c == Keys.Up) dy = -step; else if (c == Keys.Down) dy = step; else return false;
            if (BeforeChange != null && !BeforeChange("ps_h_move")) return true;
            startX = l.X; startY = l.Y; if (l.Text != null) startPos = l.Text.Pos;
            groupStart.Clear();
            if (l.Kind == LayerKind.Group) foreach (DocLayer ch in Doc.Layers) if (ch.Group == l.Id) groupStart[ch] = ch.Kind == LayerKind.Text ? ch.Text.Pos : new PointF(ch.X, ch.Y);
            MoveBy(l, dx, dy);
            l.Version++;
            Redraw();
            if (Changed != null) Changed();
            return true;
        }
    }
}
