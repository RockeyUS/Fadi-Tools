using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// A piece of styled text — or a photo — placed on a photo or video. Coordinates are in output pixels.
    /// It renders itself into a cached bitmap, so moving it or changing its opacity costs almost nothing.
    public class TextLayer
    {
        public string Text = "";
        public string FontName = "Segoe UI";
        public float SizePx = 64;        // text: font size · photo: its longest side
        public Color Color = Color.White;
        public bool Bold = true, Italic, Outline = true, Shadow = true, Box;
        public Color BoxColor = Color.Empty;       // empty = automatic (dark behind light text, light behind dark text)
        public Color OutlineColor = Color.Empty;   // empty = automatic (black around light text, white around dark text / white frame)
        public float OutlineWidth = 1f;            // 1 = normal thickness
        public float Angle;                        // degrees, clockwise
        public float Opacity = 1f;
        public StringAlignment Align = StringAlignment.Center;
        public PointF Pos;               // center of the text block
        public double From = 0, To = -1; // video: seconds on the timeline when it shows (To < 0 = until the end)
        public bool ShownAt(double t) { return t >= From - 0.001 && (To < 0 || t < To); }

        /// A photo placed on top instead of text (kept at a sensible size in memory).
        public Bitmap Picture;
        public string PicturePath;
        public bool IsPicture { get { return Picture != null; } }
        /// Has something to show (a photo, or text that isn't empty).
        public bool Visible { get { return Picture != null || (!string.IsNullOrEmpty(Text) && Text.Trim().Length > 0); } }
        /// What the timeline and lists call it.
        public string Label { get { return IsPicture ? System.IO.Path.GetFileName(PicturePath ?? "") : (Text ?? "").Replace("\r", " ").Replace("\n", " "); } }

        GraphicsPath path;
        RectangleF glyphs;
        Bitmap cache; float cacheScale = -1, cacheSize; PointF cacheOrigin;
        bool live;   // size being dragged: the last picture is stretched until the drag settles

        public TextLayer CloneStyle()
        {
            TextLayer t = (TextLayer)MemberwiseClone();
            t.path = null; t.cache = null; t.cacheScale = -1; t.live = false;
            return t;
        }

        /// Call after changing any look-related field (not needed for Pos or Opacity).
        public void Touch()
        {
            if (path != null) { path.Dispose(); path = null; }
            live = false;
            DropCache();
        }
        /// The size is being dragged: shapes are measured again, but the screen keeps stretching the last picture
        /// (instant) until Touch() draws it sharp again.
        public void SizeChanging()
        {
            if (path != null) { path.Dispose(); path = null; }
            live = cache != null;
        }
        public void DropCache() { if (cache != null) { cache.Dispose(); cache = null; } cacheScale = -1; live = false; }

        public static bool HasRtl(string s)
        {
            if (s == null) return false;
            foreach (char c in s) if ((c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFEFC)) return true;
            return false;
        }

        static FontFamily Family(string name)
        {
            FontFamily pf = FontPack.Find(name);
            if (pf != null) return pf;
            try { return new FontFamily(name); } catch { }
            try { return new FontFamily("Segoe UI"); } catch { }
            return FontFamily.GenericSansSerif;
        }

        /// Text outline (or the photo's rectangle) centered on (0,0), in output pixels, before turning.
        GraphicsPath LocalPath
        {
            get
            {
                if (path != null) return path;
                if (IsPicture)
                {
                    float big = Math.Max(1, SizePx), pw = Math.Max(1, Picture.Width), ph = Math.Max(1, Picture.Height);
                    float w = pw >= ph ? big : big * pw / ph, h = pw >= ph ? big * ph / pw : big;
                    glyphs = new RectangleF(-w / 2, -h / 2, w, h);
                    path = new GraphicsPath();
                    path.AddRectangle(glyphs);
                    return path;
                }
                FontFamily fam = Family(FontName);
                FontStyle st = (Bold ? FontStyle.Bold : 0) | (Italic ? FontStyle.Italic : 0);
                if (!fam.IsStyleAvailable(st)) st = Bold && fam.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                if (!fam.IsStyleAvailable(st)) foreach (FontStyle s in new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic }) if (fam.IsStyleAvailable(s)) { st = s; break; }
                float em = Math.Max(1, SizePx);
                float lh = em * fam.GetLineSpacing(st) / Math.Max(1, fam.GetEmHeight(st));
                string[] lines = (Text ?? "").Replace("\r", "").Split('\n');
                List<GraphicsPath> parts = new List<GraphicsPath>();
                List<RectangleF> bounds = new List<RectangleF>();
                float maxW = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    GraphicsPath p = new GraphicsPath(FillMode.Winding);
                    if (lines[i].Trim().Length > 0)
                        using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                        {
                            sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                            if (HasRtl(lines[i])) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                            try { p.AddString(lines[i], fam, (int)st, em, new PointF(0, i * lh), sf); } catch { }
                        }
                    RectangleF b = p.PointCount > 0 ? p.GetBounds() : RectangleF.Empty;
                    parts.Add(p); bounds.Add(b);
                    maxW = Math.Max(maxW, b.Width);
                }
                float totalH = lines.Length * lh;
                GraphicsPath all = new GraphicsPath(FillMode.Winding);
                for (int i = 0; i < parts.Count; i++)
                {
                    if (parts[i].PointCount > 0)
                    {
                        float w = bounds[i].Width;
                        float left = Align == StringAlignment.Near ? 0 : Align == StringAlignment.Center ? (maxW - w) / 2 : maxW - w;
                        using (Matrix m = new Matrix())
                        {
                            m.Translate(left - bounds[i].X - maxW / 2, -totalH / 2);
                            parts[i].Transform(m);
                        }
                        all.AddPath(parts[i], false);
                    }
                    parts[i].Dispose();
                }
                glyphs = all.PointCount > 0 ? all.GetBounds() : new RectangleF(-em / 2, -em / 2, em, em);
                path = all;
                return path;
            }
        }

        RectangleF BoxRect
        {
            get
            {
                GraphicsPath p = LocalPath;
                if (IsPicture) return glyphs;
                float em = Math.Max(1, SizePx);
                RectangleF r = glyphs;
                r.Inflate(em * 0.3f, em * 0.2f);
                return r;
            }
        }

        /// The box around r after turning it by the layer's angle.
        RectangleF Turned(RectangleF r)
        {
            if (Math.Abs(Angle) < 0.01f) return r;
            double a = Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (PointF p in new[] { new PointF(r.Left, r.Top), new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom), new PointF(r.Right, r.Bottom) })
            {
                float x = (float)(p.X * c - p.Y * s), y = (float)(p.X * s + p.Y * c);
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            return RectangleF.FromLTRB(x0, y0, x1, y1);
        }

        /// Selection/hit area in output coordinates.
        public RectangleF Bounds { get { RectangleF r = Turned(BoxRect); r.Offset(Pos); return r; } }

        static bool IsLight(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 150; }

        float Em { get { return Math.Max(1, SizePx); } }
        /// How big shadows and frames are: the font size for text, a share of a photo's size.
        float FxEm { get { return IsPicture ? Math.Max(1, SizePx) * 0.28f : Em; } }
        PointF ShadowOffset { get { return new PointF(FxEm * 0.05f, FxEm * 0.07f); } }
        float FrameW { get { return Math.Max(1.5f, SizePx * 0.014f * OutlineWidth); } }

        /// Everything the layer paints, in local coordinates.
        RectangleF ContentRect
        {
            get
            {
                GraphicsPath p = LocalPath;
                RectangleF r;
                if (IsPicture) r = RectangleF.Inflate(glyphs, (Outline ? FrameW : 0) + 1, (Outline ? FrameW : 0) + 1);
                else if (Box) r = BoxRect;
                else { float o = Em * 0.08f + (Outline ? Em * 0.055f * Math.Max(0, OutlineWidth - 1) : 0); r = RectangleF.Inflate(glyphs, o, o); }
                r = Turned(r);
                if (Shadow)
                {
                    RectangleF s = r; s.Offset(ShadowOffset); s.Inflate(FxEm * 0.14f, FxEm * 0.14f);
                    r = RectangleF.Union(r, s);
                }
                return r;
            }
        }

        void Paint(Graphics g, bool silhouette, Color sil)
        {
            GraphicsPath p = LocalPath;
            float em = Em;
            GraphicsState st = g.Save();
            if (Math.Abs(Angle) >= 0.01f) g.RotateTransform(Angle);
            if (IsPicture)
            {
                RectangleF frame = Outline ? RectangleF.Inflate(glyphs, FrameW, FrameW) : glyphs;
                if (silhouette) { using (SolidBrush b = new SolidBrush(sil)) g.FillRectangle(b, frame); }
                else
                {
                    if (Outline) using (SolidBrush b = new SolidBrush(OutlineColor.IsEmpty ? Color.White : OutlineColor)) g.FillRectangle(b, frame);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);   // no grey seam at the edges
                        g.DrawImage(Picture, Rectangle.Round(glyphs), 0, 0, Picture.Width, Picture.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                g.Restore(st);
                return;
            }
            Color contrast = IsLight(Color) ? Color.Black : Color.White;
            if (Box)
            {
                Color bc = silhouette ? sil : !BoxColor.IsEmpty ? Color.FromArgb(235, BoxColor) : (IsLight(Color) ? Color.FromArgb(158, 0, 0, 0) : Color.FromArgb(218, 255, 255, 255));
                using (GraphicsPath rp = Theme.Round(BoxRect, em * 0.25f)) using (SolidBrush b = new SolidBrush(bc)) g.FillPath(b, rp);
                if (silhouette) { g.Restore(st); return; }
            }
            if (Outline)
                using (Pen pen = new Pen(silhouette ? sil : OutlineColor.IsEmpty ? contrast : OutlineColor, em * 0.11f * Math.Max(0.2f, OutlineWidth)) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, p);
            using (SolidBrush b = new SolidBrush(silhouette ? sil : Color)) g.FillPath(b, p);
            g.Restore(st);
        }

        /// Renders the layer at a given scale into a premultiplied bitmap.
        Bitmap RenderAt(float scale, out PointF origin)
        {
            RectangleF c = ContentRect;
            const int pad = 2;
            int bw = Math.Max(1, (int)Math.Ceiling(c.Width * scale) + pad * 2), bh = Math.Max(1, (int)Math.Ceiling(c.Height * scale) + pad * 2);
            origin = new PointF(c.X - pad / scale, c.Y - pad / scale);
            Bitmap b = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                if (Shadow)
                {
                    // soft drop shadow: paint a silhouette small, then stretch it back up (cheap blur)
                    float f = Math.Max(2f, Math.Min(24f, FxEm * scale * 0.09f));
                    int sw = Math.Max(1, (int)(bw / f)), sh = Math.Max(1, (int)(bh / f));
                    using (Bitmap s = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb))
                    {
                        using (Graphics gs = Graphics.FromImage(s))
                        {
                            gs.SmoothingMode = SmoothingMode.AntiAlias;
                            gs.ScaleTransform(sw / (float)bw, sh / (float)bh);
                            gs.TranslateTransform(pad + (ShadowOffset.X - c.X) * scale, pad + (ShadowOffset.Y - c.Y) * scale);
                            gs.ScaleTransform(scale, scale);
                            Paint(gs, true, Color.FromArgb(Box || IsPicture ? 120 : 170, 0, 0, 0));
                        }
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        using (ImageAttributes ia = new ImageAttributes())
                        {
                            ia.SetWrapMode(WrapMode.TileFlipXY);
                            g.DrawImage(s, new Rectangle(0, 0, bw, bh), 0, 0, sw, sh, GraphicsUnit.Pixel, ia);
                        }
                    }
                }
                g.TranslateTransform(pad - c.X * scale, pad - c.Y * scale);
                g.ScaleTransform(scale, scale);
                Paint(g, false, Color.Empty);
            }
            return b;
        }

        static void Blit(Graphics g, Bitmap b, RectangleF dest, float opacity)
        {
            if (opacity >= 0.999f) { g.DrawImage(b, dest); return; }
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix { Matrix33 = Math.Max(0, opacity) };
                ia.SetColorMatrix(cm);
                g.DrawImage(b, Rectangle.Round(dest), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
            }
        }
        static void Blit(Graphics g, Bitmap b, float x, float y, float opacity)
        {
            Blit(g, b, new RectangleF((float)Math.Round(x), (float)Math.Round(y), b.Width, b.Height), opacity);
        }

        /// Draws the layer on screen: (ox, oy) is where output pixel (0,0) lands, scale = screen pixels per output pixel.
        public void DrawScreen(Graphics g, float ox, float oy, float scale)
        {
            if (!Visible) return;
            GraphicsState st = g.Save();
            float k = cacheSize > 0 ? SizePx / cacheSize : 1;
            if (live && cache != null && Math.Abs(cacheScale - scale) <= 0.0005f && k > 0.4f && k < 2.5f)
            {
                // being resized: stretch the last picture (smooth), it's drawn sharp again when the drag settles
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                Blit(g, cache, new RectangleF(ox + (Pos.X + cacheOrigin.X * k) * scale, oy + (Pos.Y + cacheOrigin.Y * k) * scale, cache.Width * k, cache.Height * k), Opacity);
                g.Restore(st);
                return;
            }
            if (cache == null || Math.Abs(cacheScale - scale) > 0.0005f || Math.Abs(cacheSize - SizePx) > 0.001f)
            {
                DropCache();
                try { cache = RenderAt(scale, out cacheOrigin); cacheScale = scale; cacheSize = SizePx; } catch { g.Restore(st); return; }
            }
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Blit(g, cache, ox + (Pos.X + cacheOrigin.X) * scale, oy + (Pos.Y + cacheOrigin.Y) * scale, Opacity);
            g.Restore(st);
        }

        /// Draws the layer at full size (for saving).
        public void Draw(Graphics g)
        {
            if (!Visible) return;
            PointF o;
            using (Bitmap b = RenderAt(1f, out o))
            {
                GraphicsState st = g.Save();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                Blit(g, b, Pos.X + o.X, Pos.Y + o.Y, Opacity);
                g.Restore(st);
            }
        }

        /// Loads a photo for placing on top: any format the viewer opens, kept to a sensible size.
        public static Bitmap LoadPicture(string path)
        {
            using (Image im = ViewerPage.Decode(path, 3000))
            {
                Bitmap b = new Bitmap(im.Width, im.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(b)) g.DrawImage(im, 0, 0, im.Width, im.Height);
                return b;
            }
        }
    }

    /// What is being edited: a photo (with optional background edit) or the frame size of a video.
    public class EditSession
    {
        public Bitmap Source;            // photo as loaded (photo mode)
        public Bitmap Original;          // Source with the background edit applied
        public Bitmap Work;              // Original at the output size
        public Bitmap Preview;           // what the canvas shows (smaller copy, or the current video frame)
        public int W, H;                 // output size
        public int OW, OH;               // original size
        public Rectangle Crop;           // in original pixels; empty = no crop
        public int BaseW { get { return Crop.IsEmpty ? OW : Crop.Width; } }
        public int BaseH { get { return Crop.IsEmpty ? OH : Crop.Height; } }
        public bool Video;
        public List<TextLayer> Layers = new List<TextLayer>();
        public string SourcePath;
        public bool Dirty;
        public int Selected = -1;

        // background removal
        public byte[] Mask;
        public int BgMode;               // 0 original, 1 transparent, 2 color, 3 blur
        public Color BgColor = Color.White;
        public float BgBlur = 40;        // 0..100
        /// Light, color, look, blur and sharpness (photos).
        public Adjust Adj = new Adjust();
        Bitmap adjPreview, adjBase; Adjust adjFor;

        /// What the canvas shows: the preview with the adjustments.
        public Bitmap Shown
        {
            get
            {
                if (Video || Adj.None || Preview == null) return Preview;
                if (adjPreview != null && adjBase == Preview && Adj.Same(adjFor)) return adjPreview;
                Bitmap old = adjPreview;
                adjPreview = Adj.Apply(Preview); adjBase = Preview; adjFor = Adj.Clone();
                if (old != null) old.Dispose();
                return adjPreview;
            }
        }

        public TextLayer Current { get { return Selected >= 0 && Selected < Layers.Count ? Layers[Selected] : null; } }
        public int ShortSide { get { return W <= 0 ? 1000 : Math.Min(W, H); } }
        public bool Active { get { return W > 0; } }

        public void BeginPhoto(Bitmap source, string path)
        {
            End();
            Video = false;
            Source = Original = Work = source; SourcePath = path;
            Adj = new Adjust();
            OW = W = source.Width; OH = H = source.Height;
            MakePreview();
            Dirty = false;
        }

        public void BeginVideo(int w, int h, string path)
        {
            End();
            Video = true; SourcePath = path;
            OW = W = w; OH = H = h;
            Dirty = false;
        }

        /// Video: show a new frame under the text.
        public void SetFrame(Bitmap frame)
        {
            if (!Crop.IsEmpty && frame != null && OW > 0)
            {
                // frames arrive uncropped (and smaller): cut the same region out of them
                float k = frame.Width / (float)OW;
                Rectangle r = Rectangle.Intersect(new Rectangle((int)(Crop.X * k), (int)(Crop.Y * k), Math.Max(1, (int)(Crop.Width * k)), Math.Max(1, (int)(Crop.Height * k))), new Rectangle(0, 0, frame.Width, frame.Height));
                if (r.Width > 0 && r.Height > 0) { Bitmap c = frame.Clone(r, frame.PixelFormat); frame.Dispose(); frame = c; }
            }
            if (Preview != null && Preview != frame) Preview.Dispose();
            Preview = frame;
        }

        /// Crops to a rectangle given in output coordinates. Text keeps its place on the picture.
        public void ApplyCrop(RectangleF r)
        {
            float k = BaseW / (float)W;   // base pixels per output pixel
            int bx = (Crop.IsEmpty ? 0 : Crop.X) + (int)Math.Round(r.X * k), by = (Crop.IsEmpty ? 0 : Crop.Y) + (int)Math.Round(r.Y * k);
            int bw = Math.Max(8, (int)Math.Round(r.Width * k)), bh = Math.Max(8, (int)Math.Round(r.Height * k));
            if (Video) { bw -= bw % 2; bh -= bh % 2; }
            Crop = Rectangle.Intersect(new Rectangle(bx, by, bw, bh), new Rectangle(0, 0, OW, OH));
            int nw = Math.Max(8, (int)Math.Round(r.Width)), nh = Math.Max(8, (int)Math.Round(r.Height));
            if (Video) { nw += nw % 2; nh += nh % 2; }
            foreach (TextLayer l in Layers) { l.Pos = new PointF(l.Pos.X - r.X, l.Pos.Y - r.Y); l.DropCache(); }
            W = nw; H = nh;
            if (!Video) RebuildWork();
            Dirty = true;
        }

        public void ResetCrop()
        {
            if (Crop.IsEmpty) return;
            float k = W / (float)BaseW;
            foreach (TextLayer l in Layers) { l.Pos = new PointF(l.Pos.X + Crop.X * k, l.Pos.Y + Crop.Y * k); l.DropCache(); }
            W = Math.Max(8, (int)Math.Round(OW * k)); H = Math.Max(8, (int)Math.Round(OH * k));
            if (Video) { W += W % 2; H += H % 2; }
            Crop = Rectangle.Empty;
            if (!Video) RebuildWork();
            Dirty = true;
        }

        void DisposeImages()
        {
            if (adjPreview != null) { adjPreview.Dispose(); adjPreview = null; adjBase = null; }
            HashSet<Bitmap> all = new HashSet<Bitmap>();
            foreach (Bitmap b in new[] { Preview, Work, Original, Source }) if (b != null) all.Add(b);
            foreach (Bitmap b in all) b.Dispose();
            Preview = Work = Original = Source = null;
        }

        public void End()
        {
            DisposeImages();
            foreach (TextLayer l in Layers) l.Touch();
            Layers.Clear(); Selected = -1;
            Mask = null; BgMode = 0; W = H = OW = OH = 0; Crop = Rectangle.Empty;
        }

        void MakePreview()
        {
            if (Preview != null && Preview != Work && Preview != Original && Preview != Source) Preview.Dispose();
            const int max = 2200;
            if (Work.Width <= max && Work.Height <= max) { Preview = Work; return; }
            float k = Math.Min(max / (float)Work.Width, max / (float)Work.Height);
            Preview = Scale(Work, Math.Max(1, (int)(Work.Width * k)), Math.Max(1, (int)(Work.Height * k)));
        }

        public static Bitmap Scale(Image src, int w, int h) { return Scale(src, w, h, Rectangle.Empty); }

        public static Bitmap Scale(Image src, int w, int h, Rectangle from)
        {
            if (from.IsEmpty) from = new Rectangle(0, 0, src.Width, src.Height);
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe at the edges
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h), from.X, from.Y, from.Width, from.Height, GraphicsUnit.Pixel, ia);
            }
            return b;
        }

        /// Changes the output size; photos are always re-scaled from the original. Text keeps its place.
        public void Resize(int w, int h)
        {
            float fx = w / (float)W, fy = h / (float)H;
            W = w; H = h;
            foreach (TextLayer l in Layers)
            {
                l.Pos = new PointF(l.Pos.X * fx, l.Pos.Y * fy);
                l.SizePx *= (fx + fy) / 2;
                l.Touch();
            }
            if (!Video) RebuildWork();
            Dirty = true;
        }

        /// Puts back a size and crop (undo).
        public void SetGeometry(int w, int h, Rectangle crop)
        {
            if (w == W && h == H && crop == Crop) return;
            W = w; H = h; Crop = crop;
            foreach (TextLayer l in Layers) l.DropCache();
            if (!Video && Original != null) RebuildWork();
        }

        /// Output (layer) coordinates → source-photo pixels.
        public PointF ToSource(PointF p)
        {
            float kx = BaseW / (float)Math.Max(1, W), ky = BaseH / (float)Math.Max(1, H);
            return new PointF((Crop.IsEmpty ? 0 : Crop.X) + p.X * kx, (Crop.IsEmpty ? 0 : Crop.Y) + p.Y * ky);
        }
        public float SourceScale { get { return BaseW / (float)Math.Max(1, W); } }

        void RebuildWork()
        {
            Bitmap old = Work;
            Work = (Crop.IsEmpty && W == Original.Width && H == Original.Height) ? Original : Scale(Original, W, H, Crop);
            if (old != null && old != Work && old != Original && old != Source) { if (Preview == old) Preview = null; old.Dispose(); }
            MakePreview();
        }

        /// Rebuilds the photo from the source + mask using the current background mode.
        public void ApplyBackground()
        {
            if (Video || Source == null) return;
            Bitmap old = Original;
            if (Mask == null || BgMode == 0) Original = Source;
            else
            {
                Bitmap cut = BgRemover.Cutout(Source, Mask);
                if (BgMode == 1) Original = cut;
                else
                {
                    Bitmap b = new Bitmap(Source.Width, Source.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        if (BgMode == 2) g.Clear(BgColor);
                        else
                        {
                            // portrait-style blurred background
                            float k = 6 + BgBlur * 0.5f;
                            using (Bitmap small = Scale(Source, Math.Max(2, (int)(Source.Width / k)), Math.Max(2, (int)(Source.Height / k))))
                            using (Bitmap mid = Scale(small, Math.Max(2, Source.Width / 4), Math.Max(2, Source.Height / 4)))
                            {
                                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                                using (ImageAttributes ia = new ImageAttributes())
                                {
                                    ia.SetWrapMode(WrapMode.TileFlipXY);
                                    g.DrawImage(mid, new Rectangle(0, 0, Source.Width, Source.Height), 0, 0, mid.Width, mid.Height, GraphicsUnit.Pixel, ia);
                                }
                            }
                        }
                        g.DrawImage(cut, new Rectangle(0, 0, cut.Width, cut.Height));
                    }
                    cut.Dispose();
                    Original = b;
                }
            }
            if (old != null && old != Original && old != Source)
            {
                if (Work == old) Work = null;
                if (Preview == old) Preview = null;
                old.Dispose();
            }
            if (Work != null && Work != Original && Work != Source) { if (Preview == Work) Preview = null; Work.Dispose(); }
            Work = null;
            RebuildWork();
            Dirty = true;
        }

        public Bitmap Render(bool flatten)
        {
            Bitmap b = new Bitmap(W, H, flatten ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                if (flatten) g.Clear(Color.White);
                if (Video || Adj.None) g.DrawImage(Work, new Rectangle(0, 0, W, H));
                else using (Bitmap a = Adj.Apply(Work)) g.DrawImage(a, new Rectangle(0, 0, W, H));
                g.CompositingQuality = CompositingQuality.HighQuality;
                foreach (TextLayer l in Layers) l.Draw(g);
            }
            return b;
        }

        /// Video: only the text, on a transparent canvas the size of the output.
        public Bitmap RenderOverlay()
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.CompositingQuality = CompositingQuality.HighQuality;
                foreach (TextLayer l in Layers) l.Draw(g);
            }
            return b;
        }

        public bool HasText { get { foreach (TextLayer l in Layers) if (l.Visible) return true; return false; } }
    }

    /// Multi-line text box with the app's rounded field look.
    public class MultiField : UiControl
    {
        public readonly TextBox Box;
        bool focused;
        public MultiField()
        {
            Box = new TextBox { Multiline = true, BorderStyle = BorderStyle.None, AcceptsReturn = true, WordWrap = true, ScrollBars = ScrollBars.None };
            Box.Font = Theme.F(11f);
            Controls.Add(Box);
            Box.GotFocus += (s, e) => { focused = true; Invalidate(); };
            Box.LostFocus += (s, e) => { focused = false; Invalidate(); };
            Box.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.A) { Box.SelectAll(); e.SuppressKeyPress = true; } };
            Cursor = Cursors.IBeam;
            Restyle();
        }
        public void Restyle() { Box.BackColor = Theme.Field; Box.ForeColor = Theme.Ink; Invalidate(); }
        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Box.SetBounds(Theme.S(12), Theme.S(9), Math.Max(10, Width - Theme.S(24)), Math.Max(10, Height - Theme.S(18))); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, r, Theme.S(10), Theme.Field);
            Theme.StrokeRound(g, r, Theme.S(10), focused ? Theme.InkSoft : Theme.Mix(Theme.FieldBorder, Theme.InkFaint, hot * 0.5f), focused ? 1.5f : 1f);
        }
    }

    /// A grid of one-shot chip buttons (resize presets).
    public class ChipButtons : UiControl
    {
        public string[] Items;
        public int Columns = 5;
        int hoverIndex = -1;
        public event Action<int> Picked;
        public ChipButtons(params string[] items) { Items = items; Cursor = Cursors.Hand; }
        int Rows { get { return (Items.Length + Columns - 1) / Columns; } }
        public int NeededHeight(int rowH) { return Rows * rowH + (Rows - 1) * Theme.S(6); }
        Rectangle Cell(int i)
        {
            int gap = Theme.S(6);
            int w = (Width - gap * (Columns - 1)) / Columns, h = (Height - gap * (Rows - 1)) / Rows;
            int col = i % Columns, row = i / Columns;
            return Theme.Mirror(new Rectangle(col * (w + gap), row * (h + gap), w, h), Width);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; for (int i = 0; i < Items.Length; i++) if (Cell(i).Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            for (int i = 0; i < Items.Length; i++) if (Cell(i).Contains(e.Location)) { if (Picked != null) Picked(i); break; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            for (int i = 0; i < Items.Length; i++)
            {
                Rectangle c = Cell(i);
                RectangleF r = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1);
                bool h = i == hoverIndex && Enabled;
                Theme.FillRoundGradient(g, r, Theme.S(8), h ? (pressing ? Theme.Pressed : Theme.Hover) : Theme.ChipTop, h ? Theme.Pressed : Theme.ChipBottom);
                Theme.StrokeRound(g, r, Theme.S(8), h ? Theme.InkFaint : Theme.ChipBorder);
                Theme.Text(g, Items[i], Theme.F(8.75f, FontStyle.Bold), Enabled ? Theme.Ink : Theme.InkFaint, c, Align.Center, true, false, true);
            }
        }
    }

    /// Six buttons that move text to the left/center/right and top/middle/bottom of the picture.
    public class PositionPad : UiControl
    {
        int hoverIndex = -1, flash = -1; float flashT;
        public event Action<int> Picked;
        public PositionPad() { Cursor = Cursors.Hand; }

        Rectangle Cell(int i)
        {
            int gap = Theme.S(5), group = Theme.S(10);
            int w = (Width - gap * 4 - group) / 6;
            int gi = i < 3 ? 0 : 1, pos = i % 3;
            if (Theme.RTL) gi = 1 - gi;   // the left/center/right group sits next to the label
            int x = gi * (3 * (w + gap) - gap + group) + pos * (w + gap);
            return new Rectangle(x, 0, w, Height);   // drawn mirrored only for the horizontal group meaning
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; for (int i = 0; i < 6; i++) if (Cell(i).Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            for (int i = 0; i < 6; i++)
                if (Cell(i).Contains(e.Location)) { flash = i; flashT = 1; Animator.Start(this); if (Picked != null) Picked(i); break; }
        }
        public override bool Step()
        {
            bool more = base.Step();
            if (flashT > 0) { flashT = Math.Max(0, flashT - 0.08f * Animator.K); Invalidate(); more |= flashT > 0; }
            return more;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            for (int i = 0; i < 6; i++)
            {
                Rectangle c = Cell(i);
                RectangleF r = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1);
                bool h = i == hoverIndex && Enabled;
                Color top = h ? Theme.Hover : Theme.ChipTop, bot = h ? Theme.Pressed : Theme.ChipBottom;
                if (i == flash && flashT > 0) { top = Theme.Mix(top, Theme.PrimaryTop, flashT); bot = Theme.Mix(bot, Theme.PrimaryBottom, flashT); }
                Theme.FillRoundGradient(g, r, Theme.S(8), top, bot);
                Theme.StrokeRound(g, r, Theme.S(8), Theme.ChipBorder);
                Color ink = i == flash && flashT > 0.5f ? Theme.PrimaryInk : (Enabled ? Theme.Ink : Theme.InkFaint);
                // mini picture: a frame with a bar where the text will go
                int fw = Theme.S(18), fh = Theme.S(14);
                Rectangle f = new Rectangle(c.X + (c.Width - fw) / 2, c.Y + (c.Height - fh) / 2, fw, fh);
                using (Pen p = new Pen(Color.FromArgb(150, ink), 1.2f)) using (System.Drawing.Drawing2D.GraphicsPath fp = Theme.Round(f, Theme.S(3))) g.DrawPath(p, fp);
                RectangleF bar;
                float t = Theme.S(3), m = Theme.S(3);
                switch (i)
                {
                    case 0: bar = new RectangleF(f.X + m, f.Y + (fh - t) / 2f, fw * 0.45f, t); break;
                    case 1: bar = new RectangleF(f.X + (fw - fw * 0.5f) / 2, f.Y + (fh - t) / 2f, fw * 0.5f, t); break;
                    case 2: bar = new RectangleF(f.Right - m - fw * 0.45f, f.Y + (fh - t) / 2f, fw * 0.45f, t); break;
                    case 3: bar = new RectangleF(f.X + (fw - fw * 0.6f) / 2, f.Y + m - 0.5f, fw * 0.6f, t); break;
                    case 4: bar = new RectangleF(f.X + (fw - fw * 0.6f) / 2, f.Y + (fh - t) / 2f, fw * 0.6f, t); break;
                    default: bar = new RectangleF(f.X + (fw - fw * 0.6f) / 2, f.Bottom - m - t + 0.5f, fw * 0.6f, t); break;
                }
                Theme.FillRound(g, bar, t / 2, ink);
            }
        }
    }

    /// Scrollable area inside the editor panel; paints the row labels. Scrolls smoothly.
    public class PanelBody : UiControl
    {
        public int Offset, ContentHeight;
        float target, current;
        public readonly List<KeyValuePair<Func<string>, Rectangle>> Labels = new List<KeyValuePair<Func<string>, Rectangle>>();
        public Action<Graphics> ExtraPaint;
        public event Action Scrolled;
        public PanelBody() { Surface = Surface.Card; }
        public int MaxOffset { get { return Math.Max(0, ContentHeight - Height); } }
        public void ResetScroll() { Offset = 0; target = current = 0; }
        public void ScrollBy(int d)
        {
            target = Math.Max(0, Math.Min(MaxOffset, target + d));
            Animator.Start(this);
        }
        public override bool Step()
        {
            bool more = base.Step();
            current += (target - current) * Animator.Rate(0.3f);
            if (Math.Abs(target - current) < 0.5f) current = target;
            int n = (int)Math.Round(current);
            if (n != Offset) { Offset = n; if (Scrolled != null) Scrolled(); Invalidate(); }
            return more || current != target;
        }
        public void ClampTo(int max) { if (target > max) target = max; if (current > max) current = max; if (Offset > max) Offset = max; }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollBy(-e.Delta / 120 * Theme.S(60)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            foreach (KeyValuePair<Func<string>, Rectangle> kv in Labels)
                Theme.Text(g, kv.Key(), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(kv.Value, Width), Align.Start);
            if (ExtraPaint != null) ExtraPaint(g);
            if (MaxOffset > 0)
            {
                float frac = Height / (float)ContentHeight;
                int th = Math.Max(Theme.S(30), (int)(Height * frac));
                int ty = (int)((Height - th) * (Offset / (float)MaxOffset));
                Rectangle bar = Theme.Mirror(new Rectangle(Width - Theme.S(5), ty, Theme.S(3), th), Width);
                Theme.FillRound(g, bar, Theme.S(1.5f), Color.FromArgb(90, Theme.InkFaint));
            }
        }
    }

    /// Hand edits to a background mask (255 = keep, 0 = removed). Every edit returns a new array, so undo can keep the old one.
    public static class MaskEdit
    {
        public static byte[] Full(int w, int h) { byte[] m = new byte[w * h]; for (int i = 0; i < m.Length; i++) m[i] = 255; return m; }

        /// Paints along a line of points with a soft round brush. keep = true restores, false erases.
        public static byte[] Brush(byte[] mask, int W, int H, List<PointF> pts, float radius, bool keep)
        {
            byte[] m = (byte[])mask.Clone();
            if (pts.Count == 0) return m;
            float r = Math.Max(1, radius), step = Math.Max(1, r / 3);
            List<PointF> stamps = new List<PointF> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                PointF a = pts[i - 1], b = pts[i];
                float dx = b.X - a.X, dy = b.Y - a.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
                int n = Math.Max(1, (int)(len / step));
                for (int k = 1; k <= n; k++) stamps.Add(new PointF(a.X + dx * k / n, a.Y + dy * k / n));
            }
            float soft = Math.Max(1, r * 0.3f);
            foreach (PointF c in stamps)
            {
                int x0 = Math.Max(0, (int)(c.X - r)), x1 = Math.Min(W - 1, (int)(c.X + r) + 1);
                int y0 = Math.Max(0, (int)(c.Y - r)), y1 = Math.Min(H - 1, (int)(c.Y + r) + 1);
                for (int y = y0; y <= y1; y++)
                {
                    float fy = y + 0.5f - c.Y;
                    int row = y * W;
                    for (int x = x0; x <= x1; x++)
                    {
                        float fx = x + 0.5f - c.X;
                        float d = (float)Math.Sqrt(fx * fx + fy * fy);
                        if (d > r) continue;
                        float a = Math.Min(1, (r - d) / soft);   // 1 in the middle, fading at the rim
                        int i = row + x;
                        if (keep) { int v = (int)(a * 255); if (v > m[i]) m[i] = (byte)v; }
                        else { int v = (int)((1 - a) * 255); if (v < m[i]) m[i] = (byte)v; }
                    }
                }
            }
            return m;
        }

        /// Magic wand: every pixel that looks like the clicked one (touching it, or anywhere when global) is removed or kept.
        public static byte[] Wand(byte[] mask, Bitmap src, Point at, int tolerance, bool global, bool keep)
        {
            int W = src.Width, H = src.Height;
            byte[] m = (byte[])mask.Clone();
            if (at.X < 0 || at.Y < 0 || at.X >= W || at.Y >= H) return m;
            int[] px = new int[W * H];
            BitmapData bd = src.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { for (int y = 0; y < H; y++) Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), px, y * W, W); }
            finally { src.UnlockBits(bd); }
            int seed = px[at.Y * W + at.X];
            int sr = (seed >> 16) & 255, sg = (seed >> 8) & 255, sb = seed & 255;
            int tol = Math.Max(1, tolerance) * 255 / 100;
            Func<int, bool> like = c =>
            {
                int dr = ((c >> 16) & 255) - sr, dg = ((c >> 8) & 255) - sg, db = (c & 255) - sb;
                return Math.Abs(dr) <= tol && Math.Abs(dg) <= tol && Math.Abs(db) <= tol;
            };
            byte val = keep ? (byte)255 : (byte)0;
            if (global)
            {
                for (int i = 0; i < px.Length; i++) if (like(px[i])) m[i] = val;
                return m;
            }
            bool[] seen = new bool[W * H];
            Stack<int> st = new Stack<int>();
            st.Push(at.Y * W + at.X); seen[at.Y * W + at.X] = true;
            while (st.Count > 0)
            {
                int i = st.Pop();
                m[i] = val;
                int x = i % W, y = i / W;
                if (x > 0 && !seen[i - 1] && like(px[i - 1])) { seen[i - 1] = true; st.Push(i - 1); }
                if (x < W - 1 && !seen[i + 1] && like(px[i + 1])) { seen[i + 1] = true; st.Push(i + 1); }
                if (y > 0 && !seen[i - W] && like(px[i - W])) { seen[i - W] = true; st.Push(i - W); }
                if (y < H - 1 && !seen[i + W] && like(px[i + W])) { seen[i + W] = true; st.Push(i + W); }
            }
            return m;
        }
    }
}
