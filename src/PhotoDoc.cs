using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace FileConverter
{
    public enum LayerKind { Image, Text, Paint, Adjust, Group }
    public enum BlendMode { Normal, Multiply, Screen, Overlay, SoftLight, Darken, Lighten, Add, Difference, Color }
    public enum AdjKind { Light, Levels, Curves, HueSat }

    /// Levels, curves and hue / saturation: what an adjustment layer does beyond the Light & color sliders.
    public class Tone
    {
        // levels
        public int InBlack = 0, InWhite = 255, OutBlack = 0, OutWhite = 255;
        public float Gamma = 1f;
        // curves: points (input, output) 0..255, sorted, always including the two ends
        public List<Point> Curve = new List<Point> { new Point(0, 0), new Point(255, 255) };
        // hue / saturation / lightness: [0] everything, then reds, yellows, greens, cyans, blues, magentas
        public float[] Hue = new float[7], Sat = new float[7], Light = new float[7];

        public Tone Clone()
        {
            Tone t = (Tone)MemberwiseClone();
            t.Curve = new List<Point>(Curve);
            t.Hue = (float[])Hue.Clone(); t.Sat = (float[])Sat.Clone(); t.Light = (float[])Light.Clone();
            return t;
        }

        public string Key()
        {
            StringBuilder b = new StringBuilder();
            b.Append(InBlack).Append(',').Append(InWhite).Append(',').Append(OutBlack).Append(',').Append(OutWhite).Append(',').Append(Gamma.ToString("0.###", CultureInfo.InvariantCulture)).Append('|');
            foreach (Point p in Curve) b.Append(p.X).Append(':').Append(p.Y).Append(';');
            b.Append('|');
            for (int i = 0; i < 7; i++) b.Append(F(Hue[i])).Append(':').Append(F(Sat[i])).Append(':').Append(F(Light[i])).Append(';');
            return b.ToString();
        }
        static string F(float v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        public static Tone FromKey(string k)
        {
            Tone t = new Tone();
            if (string.IsNullOrEmpty(k)) return t;
            try
            {
                string[] parts = k.Split('|');
                string[] lv = parts[0].Split(',');
                t.InBlack = int.Parse(lv[0]); t.InWhite = int.Parse(lv[1]); t.OutBlack = int.Parse(lv[2]); t.OutWhite = int.Parse(lv[3]);
                t.Gamma = float.Parse(lv[4], CultureInfo.InvariantCulture);
                t.Curve.Clear();
                foreach (string p in parts[1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) { string[] xy = p.Split(':'); t.Curve.Add(new Point(int.Parse(xy[0]), int.Parse(xy[1]))); }
                if (t.Curve.Count < 2) t.Curve = new List<Point> { new Point(0, 0), new Point(255, 255) };
                string[] hs = parts[2].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < 7 && i < hs.Length; i++)
                {
                    string[] v = hs[i].Split(':');
                    t.Hue[i] = float.Parse(v[0], CultureInfo.InvariantCulture); t.Sat[i] = float.Parse(v[1], CultureInfo.InvariantCulture); t.Light[i] = float.Parse(v[2], CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return t;
        }

        /// The 256-step table for levels then curves (one table for all three colors).
        public byte[] Table(AdjKind kind)
        {
            byte[] t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double v = i;
                if (kind == AdjKind.Levels)
                {
                    double x = (v - InBlack) / Math.Max(1, InWhite - InBlack);
                    x = Math.Max(0, Math.Min(1, x));
                    x = Math.Pow(x, 1.0 / Math.Max(0.1, Gamma));
                    v = OutBlack + x * (OutWhite - OutBlack);
                }
                else if (kind == AdjKind.Curves) v = CurveAt(i);
                t[i] = (byte)Math.Max(0, Math.Min(255, Math.Round(v)));
            }
            return t;
        }

        /// A smooth curve through the points (monotone cubic, so it never wobbles past them).
        public double CurveAt(double x)
        {
            List<Point> p = Curve;
            if (p.Count == 0) return x;
            if (x <= p[0].X) return p[0].Y;
            if (x >= p[p.Count - 1].X) return p[p.Count - 1].Y;
            int n = p.Count;
            double[] d = new double[n - 1], m = new double[n];
            for (int i = 0; i < n - 1; i++) d[i] = (p[i + 1].Y - p[i].Y) / (double)Math.Max(1, p[i + 1].X - p[i].X);
            m[0] = d[0]; m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
            for (int i = 0; i < n - 1; i++)
            {
                if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
                double a = m[i] / d[i], b = m[i + 1] / d[i], s = a * a + b * b;
                if (s > 9) { double tau = 3 / Math.Sqrt(s); m[i] = tau * a * d[i]; m[i + 1] = tau * b * d[i]; }
            }
            int k = 0;
            while (k < n - 2 && x > p[k + 1].X) k++;
            double h = Math.Max(1, p[k + 1].X - p[k].X), t = (x - p[k].X) / h, t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * p[k].Y + (t3 - 2 * t2 + t) * h * m[k] + (-2 * t3 + 3 * t2) * p[k + 1].Y + (t3 - t2) * h * m[k + 1];
        }

        public bool None(AdjKind kind)
        {
            if (kind == AdjKind.Levels) return InBlack == 0 && InWhite == 255 && OutBlack == 0 && OutWhite == 255 && Math.Abs(Gamma - 1) < 0.001f;
            if (kind == AdjKind.Curves) { foreach (Point q in Curve) if (q.X != q.Y) return false; return true; }
            if (kind == AdjKind.HueSat) { for (int i = 0; i < 7; i++) if (Hue[i] != 0 || Sat[i] != 0 || Light[i] != 0) return false; return true; }
            return true;
        }
    }

    /// One layer of a photo: a picture, text, a painting, an adjustment, or a group (a folder of layers).
    public class DocLayer
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 10);
        public string Name = "";
        public LayerKind Kind;
        public bool Hidden, Locked, Collapsed;
        public float Opacity = 1f;
        public BlendMode Blend = BlendMode.Normal;
        /// The group (folder) it's in (null = none).
        public string Group;

        // a picture (Image) or a painting (Paint, the whole canvas' size): its own pixels
        public Bitmap Pixels;
        /// What shows of it: 0 hidden … 255 shown, one per pixel of Pixels (null = all of it).
        public byte[] Mask;
        // a picture's place: its middle on the canvas, its size, turn and mirror
        public float X, Y, Scale = 1f, Angle;
        public bool FlipX, FlipY;
        public string SourcePath;

        // text
        public TextLayer Text;

        // an adjustment
        public AdjKind AdjType;
        public Adjust Adj;
        public Tone Tone;

        /// Bumped when the look changes (pixels, mask, adjustment…): cached pictures of it are dropped.
        public int Version;

        public bool HasPixels { get { return Kind == LayerKind.Image || Kind == LayerKind.Paint; } }

        /// A copy that shares the (unchanged) pixels: changing pixels always makes new ones, so this is safe.
        public DocLayer Copy()
        {
            DocLayer c = (DocLayer)MemberwiseClone();
            if (Text != null) c.Text = Text.CloneStyle();
            if (Adj != null) c.Adj = Adj.Clone();
            if (Tone != null) c.Tone = Tone.Clone();
            return c;
        }

        /// A brand-new layer with copies of everything (Duplicate).
        public DocLayer Duplicate()
        {
            DocLayer c = Copy();
            c.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
            if (Pixels != null) c.Pixels = new Bitmap(Pixels);
            if (Mask != null) c.Mask = (byte[])Mask.Clone();
            return c;
        }

        /// Where a picture's corners are on the canvas (Image layers).
        public PointF[] Corners()
        {
            if (Pixels == null) return new PointF[0];
            float w = Pixels.Width * Scale / 2, h = Pixels.Height * Scale / 2;
            PointF[] p = { new PointF(-w, -h), new PointF(w, -h), new PointF(w, h), new PointF(-w, h) };
            double a = Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            for (int i = 0; i < 4; i++) p[i] = new PointF((float)(X + p[i].X * c - p[i].Y * s), (float)(Y + p[i].X * s + p[i].Y * c));
            return p;
        }

        /// The canvas point → the picture's own pixel (Image layers; may fall outside it).
        public PointF ToLayer(PointF doc)
        {
            double a = -Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            double dx = doc.X - X, dy = doc.Y - Y;
            double lx = (dx * c - dy * s) / Scale, ly = (dx * s + dy * c) / Scale;
            if (FlipX) lx = -lx;
            if (FlipY) ly = -ly;
            return new PointF((float)(lx + Pixels.Width / 2.0), (float)(ly + Pixels.Height / 2.0));
        }

        /// Its outline on the canvas (for handles, snapping and alignment).
        public RectangleF Bounds(PhotoDoc d)
        {
            switch (Kind)
            {
                case LayerKind.Image:
                    {
                        PointF[] p = Corners();
                        if (p.Length == 0) return RectangleF.Empty;
                        float x0 = p[0].X, y0 = p[0].Y, x1 = x0, y1 = y0;
                        foreach (PointF q in p) { x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); }
                        return RectangleF.FromLTRB(x0, y0, x1, y1);
                    }
                case LayerKind.Text: return Text != null && Text.Visible ? Text.Bounds : RectangleF.Empty;
                case LayerKind.Paint: return PaintBounds();
                case LayerKind.Group:
                    {
                        RectangleF r = RectangleF.Empty;
                        foreach (DocLayer l in d.Layers) if (l.Group == Id) { RectangleF b = l.Bounds(d); if (!b.IsEmpty) r = r.IsEmpty ? b : RectangleF.Union(r, b); }
                        return r;
                    }
            }
            return new RectangleF(0, 0, d.W, d.H);
        }

        Rectangle paintBounds; int paintBoundsV = -1;
        RectangleF PaintBounds()
        {
            if (Pixels == null) return RectangleF.Empty;
            if (paintBoundsV == Version) return paintBounds;
            int[] px = Pix.Get(Pixels);
            int w = Pixels.Width, h = Pixels.Height, x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if ((px[y * w + x] >> 24) != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            paintBounds = x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            paintBoundsV = Version;
            return paintBounds;
        }
    }

    /// Fast pixel access: a bitmap as an int per pixel (ARGB, not premultiplied) and back.
    public static class Pix
    {
        public static int[] Get(Bitmap b)
        {
            int[] a = new int[b.Width * b.Height];
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == b.Width * 4) Marshal.Copy(d.Scan0, a, 0, a.Length);
                else for (int y = 0; y < b.Height; y++) Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), a, y * b.Width, b.Width);
            }
            finally { b.UnlockBits(d); }
            return a;
        }

        public static void Put(Bitmap b, int[] a)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                if (d.Stride == b.Width * 4) Marshal.Copy(a, 0, d.Scan0, a.Length);
                else for (int y = 0; y < b.Height; y++) Marshal.Copy(a, y * b.Width, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), b.Width);
            }
            finally { b.UnlockBits(d); }
        }

        public static Bitmap Make(int w, int h, int[] a)
        {
            Bitmap b = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppArgb);
            if (a != null) Put(b, a);
            return b;
        }

        /// A plain ARGB copy (any format in, 32-bit out).
        public static Bitmap Argb(Image src)
        {
            Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(src, 0, 0, src.Width, src.Height); }
            return b;
        }
    }

    /// A photo being edited: its size and its layers, bottom first. Drawing it, its history (undo), and its
    /// project file (.fphoto: the layers kept apart, to carry on later).
    public class PhotoDoc
    {
        public const string ProjectExt = "fphoto";
        public int W, H;
        public List<DocLayer> Layers = new List<DocLayer>();
        /// What's selected: one byte per canvas pixel (0 out … 255 in); null = nothing.
        public byte[] Sel;
        public int Current = -1;
        public string SourcePath;     // the photo it started from
        public string ProjectPath;    // where it was saved as a project
        public bool Dirty;
        public string Title = "";

        public DocLayer Cur { get { return Current >= 0 && Current < Layers.Count ? Layers[Current] : null; } }

        // ------------------------------------------------------------------ making one
        public static PhotoDoc FromPhoto(Bitmap photo, string path)
        {
            PhotoDoc d = new PhotoDoc { W = photo.Width, H = photo.Height, SourcePath = path, Title = path != null ? Path.GetFileName(path) : "" };
            DocLayer l = new DocLayer { Kind = LayerKind.Image, Name = L.T("ps_l_photo"), Pixels = Pix.Argb(photo), X = photo.Width / 2f, Y = photo.Height / 2f, SourcePath = path };
            d.Layers.Add(l);
            d.Current = 0;
            return d;
        }

        public static PhotoDoc Blank(int w, int h, Color bg, string title)
        {
            PhotoDoc d = new PhotoDoc { W = w, H = h, Title = title };
            if (bg.A > 0)
            {
                Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b)) g.Clear(bg);
                d.Layers.Add(new DocLayer { Kind = LayerKind.Image, Name = L.T("ps_l_background"), Pixels = b, X = w / 2f, Y = h / 2f });
                d.Current = 0;
            }
            else
            {
                d.Layers.Add(NewPaint(w, h, L.T("ps_l_layer", 1)));
                d.Current = 0;
            }
            return d;
        }

        public static DocLayer NewPaint(int w, int h, string name)
        {
            return new DocLayer { Kind = LayerKind.Paint, Name = name, Pixels = new Bitmap(w, h, PixelFormat.Format32bppArgb), X = w / 2f, Y = h / 2f };
        }

        /// A name not used yet: "Layer 3".
        public string NextName(string key)
        {
            for (int i = 1; ; i++)
            {
                string n = L.T(key, i);
                bool used = false;
                foreach (DocLayer l in Layers) if (l.Name == n) used = true;
                if (!used) return n;
            }
        }

        // ------------------------------------------------------------------ what shows
        /// Hidden itself, or inside a hidden group.
        public bool Shown(DocLayer l)
        {
            if (l.Hidden) return false;
            string g = l.Group;
            int guard = 0;
            while (g != null && guard++ < 20)
            {
                DocLayer p = Find(g);
                if (p == null) break;
                if (p.Hidden) return false;
                g = p.Group;
            }
            return true;
        }

        /// Its opacity times its groups' opacity.
        public float EffOpacity(DocLayer l)
        {
            float o = l.Opacity;
            string g = l.Group; int guard = 0;
            while (g != null && guard++ < 20) { DocLayer p = Find(g); if (p == null) break; o *= p.Opacity; g = p.Group; }
            return o;
        }

        public DocLayer Find(string id) { foreach (DocLayer l in Layers) if (l.Id == id) return l; return null; }
        public int IndexOf(string id) { for (int i = 0; i < Layers.Count; i++) if (Layers[i].Id == id) return i; return -1; }

        /// Locked itself or inside a locked group.
        public bool IsLocked(DocLayer l)
        {
            if (l.Locked) return true;
            string g = l.Group; int guard = 0;
            while (g != null && guard++ < 20) { DocLayer p = Find(g); if (p == null) break; if (p.Locked) return true; g = p.Group; }
            return false;
        }

        // ------------------------------------------------------------------ drawing it
        readonly Dictionary<string, KeyValuePair<string, Bitmap>> masked = new Dictionary<string, KeyValuePair<string, Bitmap>>();

        /// A picture with its mask applied (cached until either changes).
        Bitmap MaskedPixels(DocLayer l)
        {
            if (l.Mask == null || l.Pixels == null) return l.Pixels;
            string key = l.Version + "/" + l.Pixels.GetHashCode() + "/" + l.Mask.GetHashCode();
            KeyValuePair<string, Bitmap> c;
            if (masked.TryGetValue(l.Id, out c) && c.Key == key) return c.Value;
            if (c.Value != null) c.Value.Dispose();
            int[] px = Pix.Get(l.Pixels);
            byte[] m = l.Mask;
            for (int i = 0; i < px.Length && i < m.Length; i++)
            {
                int a = (int)((uint)px[i] >> 24) * m[i] / 255;
                px[i] = (px[i] & 0xFFFFFF) | (a << 24);
            }
            Bitmap b = Pix.Make(l.Pixels.Width, l.Pixels.Height, px);
            masked[l.Id] = new KeyValuePair<string, Bitmap>(key, b);
            return b;
        }

        /// Draws one layer (pixels, text…) by itself onto g: canvas pixel (0,0) at (0,0), `scale` screen pixels per canvas pixel.
        public void DrawLayer(Graphics g, DocLayer l, float scale, float opacity)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (l.Kind == LayerKind.Image && l.Pixels != null)
            {
                Bitmap px = MaskedPixels(l);
                GraphicsState st = g.Save();
                g.InterpolationMode = scale * l.Scale < 0.99f ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.ScaleTransform(scale, scale);
                g.TranslateTransform(l.X, l.Y);
                g.RotateTransform(l.Angle);
                g.ScaleTransform(l.Scale * (l.FlipX ? -1 : 1), l.Scale * (l.FlipY ? -1 : 1));
                using (ImageAttributes ia = Alpha(opacity))
                    g.DrawImage(px, new Rectangle(-px.Width / 2, -px.Height / 2, px.Width, px.Height), 0, 0, px.Width, px.Height, GraphicsUnit.Pixel, ia);
                g.Restore(st);
            }
            else if (l.Kind == LayerKind.Paint && l.Pixels != null)
            {
                Bitmap px = MaskedPixels(l);
                GraphicsState st = g.Save();
                g.InterpolationMode = scale < 0.99f ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (ImageAttributes ia = Alpha(opacity))
                    g.DrawImage(px, new Rectangle(0, 0, (int)Math.Round(W * scale), (int)Math.Round(H * scale)), 0, 0, px.Width, px.Height, GraphicsUnit.Pixel, ia);
                g.Restore(st);
            }
            else if (l.Kind == LayerKind.Text && l.Text != null)
            {
                float was = l.Text.Opacity;
                l.Text.Opacity = opacity;
                try { l.Text.DrawScreen(g, 0, 0, scale); } finally { l.Text.Opacity = was; }
            }
        }

        static ImageAttributes Alpha(float a)
        {
            ImageAttributes ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            if (a < 0.999f)
            {
                ColorMatrix cm = new ColorMatrix { Matrix33 = Math.Max(0, a) };
                ia.SetColorMatrix(cm);
            }
            return ia;
        }

        /// The whole photo at a scale (1 = full size): every shown layer, bottom first, with its opacity and blend.
        /// `upTo` stops before that layer (-1 = all); `only` draws just from that layer up (for quick redraws).
        public Bitmap Compose(float scale, int upTo = -1, Bitmap below = null, int from = 0)
        {
            int w = Math.Max(1, (int)Math.Round(W * scale)), h = Math.Max(1, (int)Math.Round(H * scale));
            Bitmap acc = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(acc))
            {
                g.Clear(Color.Transparent);
                if (below != null) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(below, 0, 0, w, h); g.CompositingMode = CompositingMode.SourceOver; }
            }
            int end = upTo < 0 ? Layers.Count : Math.Min(upTo, Layers.Count);
            for (int i = from; i < end; i++)
            {
                DocLayer l = Layers[i];
                if (l.Kind == LayerKind.Group || !Shown(l)) continue;
                float op = EffOpacity(l);
                if (op <= 0.001f) continue;
                if (l.Kind == LayerKind.Adjust) { ApplyAdjustment(acc, l, op, scale); continue; }
                if (l.Blend == BlendMode.Normal)
                {
                    using (Graphics g = Graphics.FromImage(acc)) DrawLayer(g, l, scale, op);
                    continue;
                }
                using (Bitmap top = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(top)) DrawLayer(g, l, scale, 1f);
                    BlendInto(acc, top, l.Blend, op);
                }
            }
            return acc;
        }

        /// The whole photo, flattened, at full size (for saving).
        public Bitmap Flatten() { return Compose(1f); }

        // ------------------------------------------------------------------ blend modes
        static float B(BlendMode m, float cb, float cs)
        {
            switch (m)
            {
                case BlendMode.Multiply: return cb * cs;
                case BlendMode.Screen: return cb + cs - cb * cs;
                case BlendMode.Overlay: return cb <= 0.5f ? 2 * cb * cs : 1 - 2 * (1 - cb) * (1 - cs);
                case BlendMode.SoftLight:
                    {
                        if (cs <= 0.5f) return cb - (1 - 2 * cs) * cb * (1 - cb);
                        float d = cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : (float)Math.Sqrt(cb);
                        return cb + (2 * cs - 1) * (d - cb);
                    }
                case BlendMode.Darken: return Math.Min(cb, cs);
                case BlendMode.Lighten: return Math.Max(cb, cs);
                case BlendMode.Add: return Math.Min(1, cb + cs);
                case BlendMode.Difference: return Math.Abs(cb - cs);
            }
            return cs;
        }

        /// Puts `top` over `acc` with a blend mode (the W3C compositing formula, colors not premultiplied).
        public static void BlendInto(Bitmap acc, Bitmap top, BlendMode mode, float opacity)
        {
            int[] a = Pix.Get(acc), t = Pix.Get(top);
            for (int i = 0; i < a.Length; i++)
            {
                int ts = t[i];
                float As = ((uint)ts >> 24) / 255f * opacity;
                if (As <= 0) continue;
                int bs = a[i];
                float Ab = ((uint)bs >> 24) / 255f;
                float sr = ((ts >> 16) & 255) / 255f, sg = ((ts >> 8) & 255) / 255f, sb = (ts & 255) / 255f;
                float br = ((bs >> 16) & 255) / 255f, bg = ((bs >> 8) & 255) / 255f, bb = (bs & 255) / 255f;
                float rr, rg, rb;
                if (mode == BlendMode.Color)
                {
                    // the top's hue and saturation with the backdrop's lightness
                    float lb = 0.3f * br + 0.59f * bg + 0.11f * bb, ls = 0.3f * sr + 0.59f * sg + 0.11f * sb, dl = lb - ls;
                    rr = sr + dl; rg = sg + dl; rb = sb + dl;
                    float l = 0.3f * rr + 0.59f * rg + 0.11f * rb, n = Math.Min(rr, Math.Min(rg, rb)), x = Math.Max(rr, Math.Max(rg, rb));
                    if (n < 0) { rr = l + (rr - l) * l / (l - n); rg = l + (rg - l) * l / (l - n); rb = l + (rb - l) * l / (l - n); }
                    if (x > 1) { rr = l + (rr - l) * (1 - l) / (x - l); rg = l + (rg - l) * (1 - l) / (x - l); rb = l + (rb - l) * (1 - l) / (x - l); }
                }
                else { rr = B(mode, br, sr); rg = B(mode, bg, sg); rb = B(mode, bb, sb); }
                float Ao = As + Ab * (1 - As);
                if (Ao <= 0) { a[i] = 0; continue; }
                float or = (As * (1 - Ab) * sr + As * Ab * rr + (1 - As) * Ab * br) / Ao;
                float og = (As * (1 - Ab) * sg + As * Ab * rg + (1 - As) * Ab * bg) / Ao;
                float ob = (As * (1 - Ab) * sb + As * Ab * rb + (1 - As) * Ab * bb) / Ao;
                a[i] = (Clamp(Ao) << 24) | (Clamp(or) << 16) | (Clamp(og) << 8) | Clamp(ob);
            }
            Pix.Put(acc, a);
        }
        static int Clamp(float v) { int x = (int)(v * 255 + 0.5f); return x < 0 ? 0 : x > 255 ? 255 : x; }

        // ------------------------------------------------------------------ adjustment layers
        /// Changes everything under it (at the layer's opacity, and only where its mask shows).
        void ApplyAdjustment(Bitmap acc, DocLayer l, float op, float scale)
        {
            Bitmap after = null;
            try
            {
                if (l.AdjType == AdjKind.Light) { if (l.Adj == null || l.Adj.None) return; after = l.Adj.Apply(acc); }
                else
                {
                    if (l.Tone == null || l.Tone.None(l.AdjType)) return;
                    after = ApplyTone(acc, l.Tone, l.AdjType);
                }
                int[] a = Pix.Get(acc), b = Pix.Get(after);
                byte[] m = l.Mask;
                int w = acc.Width, h = acc.Height;
                for (int y = 0; y < h; y++)
                {
                    int my = m == null ? 0 : Math.Min(H - 1, (int)(y / scale));
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        float k = op;
                        if (m != null) k *= m[my * W + Math.Min(W - 1, (int)(x / scale))] / 255f;
                        if (k <= 0) continue;
                        int p = a[i], q = b[i];
                        if (k >= 0.999f) { a[i] = (p & unchecked((int)0xFF000000)) | (q & 0xFFFFFF); continue; }
                        int r = (int)(((p >> 16) & 255) * (1 - k) + ((q >> 16) & 255) * k), g = (int)(((p >> 8) & 255) * (1 - k) + ((q >> 8) & 255) * k), bl = (int)((p & 255) * (1 - k) + (q & 255) * k);
                        a[i] = (p & unchecked((int)0xFF000000)) | (r << 16) | (g << 8) | bl;
                    }
                }
                Pix.Put(acc, a);
            }
            catch { }
            finally { if (after != null) after.Dispose(); }
        }

        public static Bitmap ApplyTone(Bitmap src, Tone t, AdjKind kind)
        {
            int[] a = Pix.Get(src);
            if (kind == AdjKind.Levels || kind == AdjKind.Curves)
            {
                byte[] tb = t.Table(kind);
                for (int i = 0; i < a.Length; i++)
                {
                    int p = a[i];
                    a[i] = (p & unchecked((int)0xFF000000)) | (tb[(p >> 16) & 255] << 16) | (tb[(p >> 8) & 255] << 8) | tb[p & 255];
                }
            }
            else
            {
                for (int i = 0; i < a.Length; i++)
                {
                    int p = a[i];
                    float r = ((p >> 16) & 255) / 255f, g = ((p >> 8) & 255) / 255f, b = (p & 255) / 255f;
                    float hh, ss, ll; ToHsl(r, g, b, out hh, out ss, out ll);
                    // how much of each color range this pixel is (soft, so the edges blend)
                    float dh = 0, ds = 0, dl = 0;
                    dh += t.Hue[0]; ds += t.Sat[0]; dl += t.Light[0];
                    for (int c = 0; c < 6; c++)
                    {
                        float center = c * 60f, d = Math.Abs(hh - center); if (d > 180) d = 360 - d;
                        float wgt = Math.Max(0, 1 - d / 45f) * Math.Min(1, ss * 4);
                        if (wgt <= 0) continue;
                        dh += t.Hue[c + 1] * wgt; ds += t.Sat[c + 1] * wgt; dl += t.Light[c + 1] * wgt;
                    }
                    hh = (hh + dh * 1.8f + 360) % 360;
                    ss = ds >= 0 ? ss + (1 - ss) * ds / 100f * ss * 1.2f + ss * ds / 100f * 0.3f : ss * (1 + ds / 100f);
                    ss = Math.Max(0, Math.Min(1, ss));
                    ll = dl >= 0 ? ll + (1 - ll) * dl / 100f : ll * (1 + dl / 100f);
                    FromHsl(hh, ss, Math.Max(0, Math.Min(1, ll)), out r, out g, out b);
                    a[i] = (p & unchecked((int)0xFF000000)) | (Clamp(r) << 16) | (Clamp(g) << 8) | Clamp(b);
                }
            }
            return Pix.Make(src.Width, src.Height, a);
        }

        static void ToHsl(float r, float g, float b, out float h, out float s, out float l)
        {
            float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            l = (mx + mn) / 2;
            if (mx == mn) { h = 0; s = 0; return; }
            float d = mx - mn;
            s = l > 0.5f ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        static void FromHsl(float h, float s, float l, out float r, out float g, out float b)
        {
            if (s <= 0) { r = g = b = l; return; }
            float q = l < 0.5f ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            r = Hue2(p, q, h / 360f + 1f / 3); g = Hue2(p, q, h / 360f); b = Hue2(p, q, h / 360f - 1f / 3);
        }
        static float Hue2(float p, float q, float t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1f / 6) return p + (q - p) * 6 * t;
            if (t < 0.5f) return q;
            if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6;
            return p;
        }

        // ------------------------------------------------------------------ history (undo)
        /// What the photo was like before a change: its size, its layers (sharing unchanged pixels) and selection.
        public class State
        {
            public string Name;
            public int W, H, Current;
            public List<DocLayer> Layers;
            public byte[] Sel;
        }
        public readonly List<State> Undo = new List<State>(), Redo = new List<State>();
        public const int MaxHistory = 40;
        /// Every change made, oldest first (what History shows), and how many of them are applied.
        public State Snapshot(string name)
        {
            State s = new State { Name = name, W = W, H = H, Current = Current, Sel = Sel, Layers = new List<DocLayer>() };
            foreach (DocLayer l in Layers) s.Layers.Add(l.Copy());
            return s;
        }

        /// Call before changing anything: the photo as it is now goes into the history under this name.
        public void Push(string name)
        {
            Undo.Add(Snapshot(name));
            if (Undo.Count > MaxHistory) Undo.RemoveAt(0);
            Redo.Clear();
            Dirty = true;
            // big photos: the oldest steps go first so the history never holds more than about 1.5 GB of pictures
            while (Undo.Count > 3 && HistoryBytes() > 1500L << 20) Undo.RemoveAt(0);
        }

        void Restore(State s)
        {
            W = s.W; H = s.H; Sel = s.Sel;
            Layers = new List<DocLayer>();
            foreach (DocLayer l in s.Layers) { DocLayer c = l.Copy(); c.Version++; Layers.Add(c); }
            Current = Math.Min(s.Current, Layers.Count - 1);
            if (Current < 0 && Layers.Count > 0) Current = Layers.Count - 1;
        }

        /// How much memory the pictures kept for undo take (each picture counted once, however many steps share it).
        long HistoryBytes()
        {
            HashSet<Bitmap> seen = new HashSet<Bitmap>();
            foreach (DocLayer l in Layers) if (l.Pixels != null) seen.Add(l.Pixels);
            long total = 0;
            foreach (State st in Undo)
                foreach (DocLayer l in st.Layers)
                    if (l.Pixels != null && seen.Add(l.Pixels)) { try { total += (long)l.Pixels.Width * l.Pixels.Height * 4; } catch { } }
            return total;
        }

        public bool StepBack()
        {
            if (Undo.Count == 0) return false;
            State s = Undo[Undo.Count - 1]; Undo.RemoveAt(Undo.Count - 1);
            State now = Snapshot(s.Name);
            Redo.Add(now);
            Restore(s);
            Dirty = true;
            return true;
        }

        public bool StepForward()
        {
            if (Redo.Count == 0) return false;
            State s = Redo[Redo.Count - 1]; Redo.RemoveAt(Redo.Count - 1);
            Undo.Add(Snapshot(s.Name));
            Restore(s);
            Dirty = true;
            return true;
        }

        // ------------------------------------------------------------------ changes to the whole photo
        /// Crops the canvas to a rectangle (canvas pixels); layers keep their place on the photo.
        public void Crop(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, W, H));
            if (r.Width < 1 || r.Height < 1) return;
            foreach (DocLayer l in Layers)
            {
                if (l.Kind == LayerKind.Image) { l.X -= r.X; l.Y -= r.Y; }
                else if (l.Kind == LayerKind.Text && l.Text != null) { l.Text.Pos = new PointF(l.Text.Pos.X - r.X, l.Text.Pos.Y - r.Y); }
                else if (l.Kind == LayerKind.Paint && l.Pixels != null)
                {
                    Bitmap b = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(b)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(l.Pixels, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel); }
                    l.Pixels = b;
                    if (l.Mask != null) l.Mask = CropMask(l.Mask, W, r);
                    l.X = r.Width / 2f; l.Y = r.Height / 2f;
                }
                else if (l.Kind == LayerKind.Adjust && l.Mask != null) l.Mask = CropMask(l.Mask, W, r);
                l.Version++;
            }
            if (Sel != null) Sel = CropMask(Sel, W, r);
            W = r.Width; H = r.Height;
        }

        static byte[] CropMask(byte[] m, int w, Rectangle r)
        {
            byte[] o = new byte[r.Width * r.Height];
            for (int y = 0; y < r.Height; y++) Buffer.BlockCopy(m, (r.Y + y) * w + r.X, o, y * r.Width, r.Width);
            return o;
        }

        /// A new canvas size (everything grows or shrinks with it).
        public void Resize(int nw, int nh)
        {
            if (nw < 1 || nh < 1) return;
            float kx = nw / (float)W, ky = nh / (float)H, k = (kx + ky) / 2;
            foreach (DocLayer l in Layers)
            {
                if (l.Kind == LayerKind.Image) { l.X *= kx; l.Y *= ky; l.Scale *= k; }
                else if (l.Kind == LayerKind.Text && l.Text != null) { l.Text.Pos = new PointF(l.Text.Pos.X * kx, l.Text.Pos.Y * ky); l.Text.SizePx *= k; l.Text.Touch(); }
                else if (l.Kind == LayerKind.Paint && l.Pixels != null)
                {
                    Bitmap b = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(l.Pixels, 0, 0, nw, nh); }
                    l.Pixels = b; l.X = nw / 2f; l.Y = nh / 2f;
                    if (l.Mask != null) l.Mask = ScaleMask(l.Mask, W, H, nw, nh);
                }
                else if (l.Kind == LayerKind.Adjust && l.Mask != null) l.Mask = ScaleMask(l.Mask, W, H, nw, nh);
                l.Version++;
            }
            if (Sel != null) Sel = ScaleMask(Sel, W, H, nw, nh);
            W = nw; H = nh;
        }

        public static byte[] ScaleMask(byte[] m, int w, int h, int nw, int nh)
        {
            byte[] o = new byte[nw * nh];
            for (int y = 0; y < nh; y++)
            {
                int sy = Math.Min(h - 1, y * h / nh);
                for (int x = 0; x < nw; x++) o[y * nw + x] = m[sy * w + Math.Min(w - 1, x * w / nw)];
            }
            return o;
        }

        /// Turns the whole photo a quarter (or half) way round, or flips it.
        public void Turn(int quarters, bool flipH, bool flipV)
        {
            Bitmap flat = Flatten();
            RotateFlipType t = RotateFlipType.RotateNoneFlipNone;
            int q = ((quarters % 4) + 4) % 4;
            if (q == 1) t = RotateFlipType.Rotate90FlipNone; else if (q == 2) t = RotateFlipType.Rotate180FlipNone; else if (q == 3) t = RotateFlipType.Rotate270FlipNone;
            flat.RotateFlip(t);
            if (flipH) flat.RotateFlip(RotateFlipType.RotateNoneFlipX);
            if (flipV) flat.RotateFlip(RotateFlipType.RotateNoneFlipY);
            Layers.Clear();
            W = flat.Width; H = flat.Height;
            Layers.Add(new DocLayer { Kind = LayerKind.Image, Name = L.T("ps_l_photo"), Pixels = flat, X = W / 2f, Y = H / 2f });
            Current = 0; Sel = null;
        }

        /// Merges a layer into the one under it (both become one picture the canvas' size).
        public bool MergeDown(int i)
        {
            if (i <= 0 || i >= Layers.Count) return false;
            DocLayer top = Layers[i], bot = Layers[i - 1];
            if (top.Kind == LayerKind.Group || bot.Kind == LayerKind.Group || bot.Kind == LayerKind.Adjust) return false;
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) DrawLayer(g, bot, 1f, bot.Opacity);
            if (top.Kind == LayerKind.Adjust) { ApplyAdjustment(b, top, top.Opacity, 1f); }
            else if (top.Blend == BlendMode.Normal) using (Graphics g = Graphics.FromImage(b)) DrawLayer(g, top, 1f, top.Opacity);
            else using (Bitmap t = new Bitmap(W, H, PixelFormat.Format32bppArgb)) { using (Graphics g = Graphics.FromImage(t)) DrawLayer(g, top, 1f, 1f); BlendInto(b, t, top.Blend, top.Opacity); }
            DocLayer m = new DocLayer { Kind = LayerKind.Paint, Name = bot.Name, Pixels = b, X = W / 2f, Y = H / 2f, Group = bot.Group, Blend = BlendMode.Normal };
            Layers[i - 1] = m;
            Layers.RemoveAt(i);
            Current = i - 1;
            return true;
        }

        /// Everything in one layer.
        public void FlattenAll()
        {
            Bitmap flat = Flatten();
            Layers.Clear();
            Layers.Add(new DocLayer { Kind = LayerKind.Image, Name = L.T("ps_l_photo"), Pixels = flat, X = W / 2f, Y = H / 2f });
            Current = 0;
        }

        /// A layer turned into plain pixels (text, a turned photo…) the canvas' size.
        public void Rasterize(int i)
        {
            DocLayer l = Layers[i];
            if (l.Kind != LayerKind.Text && l.Kind != LayerKind.Image) return;
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) DrawLayer(g, l, 1f, 1f);
            DocLayer m = new DocLayer { Id = l.Id, Kind = LayerKind.Paint, Name = l.Name, Pixels = b, X = W / 2f, Y = H / 2f, Group = l.Group, Opacity = l.Opacity, Blend = l.Blend, Hidden = l.Hidden };
            Layers[i] = m;
        }

        /// A painting (canvas-sized) becomes a picture just the size of what's painted, so it can be moved and sized.
        public void PaintToImage(DocLayer l)
        {
            if (l.Kind != LayerKind.Paint || l.Pixels == null) return;
            RectangleF bf = l.Bounds(this);
            if (bf.IsEmpty) return;
            Rectangle b = Rectangle.Round(bf);
            b.Intersect(new Rectangle(0, 0, l.Pixels.Width, l.Pixels.Height));
            if (b.Width < 1 || b.Height < 1) return;
            Bitmap px = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(px)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImage(l.Pixels, new Rectangle(0, 0, b.Width, b.Height), b, GraphicsUnit.Pixel); }
            if (l.Mask != null) l.Mask = CropMask(l.Mask, l.Pixels.Width, b);
            l.Pixels = px;
            l.Kind = LayerKind.Image;
            l.X = b.X + b.Width / 2f; l.Y = b.Y + b.Height / 2f; l.Scale = 1; l.Angle = 0; l.FlipX = l.FlipY = false;
            l.Version++;
        }

        /// Turns the photo a quarter at a time (clockwise), every layer with it — nothing is flattened.
        public void TurnLayers(int quarters)
        {
            int q = ((quarters % 4) + 4) % 4;
            if (q == 0) return;
            int W0 = W, H0 = H;
            int nw = q == 2 ? W0 : H0, nh = q == 2 ? H0 : W0;
            Func<PointF, PointF> map = p => q == 1 ? new PointF(H0 - p.Y, p.X) : q == 2 ? new PointF(W0 - p.X, H0 - p.Y) : new PointF(p.Y, W0 - p.X);
            RotateFlipType rf = q == 1 ? RotateFlipType.Rotate90FlipNone : q == 2 ? RotateFlipType.Rotate180FlipNone : RotateFlipType.Rotate270FlipNone;
            foreach (DocLayer l in Layers)
            {
                if (l.Kind == LayerKind.Image) { PointF c = map(new PointF(l.X, l.Y)); l.X = c.X; l.Y = c.Y; l.Angle = Norm(l.Angle + 90 * q); }
                else if (l.Kind == LayerKind.Text && l.Text != null) { l.Text.Pos = map(l.Text.Pos); l.Text.Angle = Norm(l.Text.Angle + 90 * q); l.Text.Touch(); }
                else if (l.Kind == LayerKind.Paint && l.Pixels != null)
                {
                    Bitmap b = new Bitmap(l.Pixels); b.RotateFlip(rf); l.Pixels = b; l.X = nw / 2f; l.Y = nh / 2f;
                    if (l.Mask != null) l.Mask = TurnMask(l.Mask, W0, H0, q);
                }
                else if (l.Kind == LayerKind.Adjust && l.Mask != null) l.Mask = TurnMask(l.Mask, W0, H0, q);
                l.Version++;
            }
            if (Sel != null) Sel = TurnMask(Sel, W0, H0, q);
            W = nw; H = nh;
        }
        static float Norm(float a) { a = ((a % 360) + 360) % 360; return a > 180 ? a - 360 : a; }

        static byte[] TurnMask(byte[] m, int w, int h, int q)
        {
            int nw = q == 2 ? w : h;
            byte[] o = new byte[m.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int nx, ny;
                    if (q == 1) { nx = h - 1 - y; ny = x; } else if (q == 2) { nx = w - 1 - x; ny = h - 1 - y; } else { nx = y; ny = w - 1 - x; }
                    o[ny * nw + nx] = m[y * w + x];
                }
            return o;
        }

        /// Mirrors the whole photo (left ↔ right, or upside down), every layer with it.
        public void FlipAll(bool horizontal)
        {
            foreach (DocLayer l in Layers)
            {
                if (l.Kind == LayerKind.Image)
                {
                    if (horizontal) { l.X = W - l.X; l.FlipX = !l.FlipX; } else { l.Y = H - l.Y; l.FlipY = !l.FlipY; }
                    l.Angle = Norm(-l.Angle);
                }
                else if (l.Kind == LayerKind.Text && l.Text != null)
                {
                    l.Text.Pos = horizontal ? new PointF(W - l.Text.Pos.X, l.Text.Pos.Y) : new PointF(l.Text.Pos.X, H - l.Text.Pos.Y);
                    l.Text.Angle = Norm(-l.Text.Angle); l.Text.Touch();
                }
                else if (l.Kind == LayerKind.Paint && l.Pixels != null)
                {
                    Bitmap b = new Bitmap(l.Pixels); b.RotateFlip(horizontal ? RotateFlipType.RotateNoneFlipX : RotateFlipType.RotateNoneFlipY); l.Pixels = b;
                    if (l.Mask != null) l.Mask = FlipMask(l.Mask, W, H, horizontal);
                }
                else if (l.Kind == LayerKind.Adjust && l.Mask != null) l.Mask = FlipMask(l.Mask, W, H, horizontal);
                l.Version++;
            }
            if (Sel != null) Sel = FlipMask(Sel, W, H, horizontal);
        }

        static byte[] FlipMask(byte[] m, int w, int h, bool horizontal)
        {
            byte[] o = new byte[m.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    o[y * w + x] = horizontal ? m[y * w + (w - 1 - x)] : m[(h - 1 - y) * w + x];
            return o;
        }

        /// A copy with pictures of its own (for writing the project on another thread while editing carries on).
        public PhotoDoc CopyForSave()
        {
            PhotoDoc d = new PhotoDoc { W = W, H = H, Current = Current, SourcePath = SourcePath, Title = Title };
            foreach (DocLayer l in Layers)
            {
                DocLayer c = l.Copy();
                if (l.Pixels != null) c.Pixels = Pix.Make(l.Pixels.Width, l.Pixels.Height, Pix.Get(l.Pixels));
                d.Layers.Add(c);
            }
            return d;
        }

        public void DisposePixels() { foreach (DocLayer l in Layers) if (l.Pixels != null) { l.Pixels.Dispose(); l.Pixels = null; } }

        // ------------------------------------------------------------------ the project file
        /// Saves everything as it is (layers apart) so it can be opened again later: a zip with the layers as PNG.
        public void SaveProject(string path)
        {
            string tmp = path + ".saving";
            using (FileStream fs = new FileStream(tmp, FileMode.Create))
            using (ZipArchive z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                StringBuilder s = new StringBuilder();
                s.Append("fphoto 1\n");
                s.Append("size ").Append(W).Append(' ').Append(H).Append('\n');
                s.Append("source ").Append(SourcePath ?? "").Append('\n');
                s.Append("current ").Append(Current).Append('\n');
                int n = 0;
                foreach (DocLayer l in Layers)
                {
                    s.Append("layer\n");
                    Kv(s, "id", l.Id); Kv(s, "name", l.Name); Kv(s, "kind", l.Kind.ToString()); Kv(s, "hidden", l.Hidden ? "1" : "0");
                    Kv(s, "locked", l.Locked ? "1" : "0"); Kv(s, "collapsed", l.Collapsed ? "1" : "0"); Kv(s, "opacity", Fs(l.Opacity)); Kv(s, "blend", l.Blend.ToString());
                    Kv(s, "group", l.Group ?? "");
                    Kv(s, "x", Fs(l.X)); Kv(s, "y", Fs(l.Y)); Kv(s, "scale", Fs(l.Scale)); Kv(s, "angle", Fs(l.Angle));
                    Kv(s, "flip", (l.FlipX ? "1" : "0") + (l.FlipY ? "1" : "0")); Kv(s, "path", l.SourcePath ?? "");
                    if (l.Pixels != null)
                    {
                        string e = "px" + n + ".png";
                        using (Stream o = z.CreateEntry(e, CompressionLevel.Fastest).Open()) l.Pixels.Save(o, ImageFormat.Png);
                        Kv(s, "pixels", e);
                    }
                    if (l.Mask != null)
                    {
                        string e = "mask" + n + ".bin";
                        using (Stream o = z.CreateEntry(e, CompressionLevel.Optimal).Open()) { WriteInt(o, l.Mask.Length); o.Write(l.Mask, 0, l.Mask.Length); }
                        Kv(s, "mask", e);
                    }
                    if (l.Text != null) Kv(s, "text", TextKey(l.Text));
                    if (l.Kind == LayerKind.Adjust)
                    {
                        Kv(s, "adjtype", l.AdjType.ToString());
                        if (l.Adj != null) Kv(s, "adj", l.Adj.Key());
                        if (l.Tone != null) Kv(s, "tone", l.Tone.Key());
                    }
                    n++;
                }
                using (StreamWriter w = new StreamWriter(z.CreateEntry("photo.txt").Open(), new UTF8Encoding(false))) w.Write(s.ToString());
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            ProjectPath = path; Dirty = false;
        }

        static void WriteInt(Stream o, int v) { o.Write(BitConverter.GetBytes(v), 0, 4); }
        static string Fs(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        static void Kv(StringBuilder s, string k, string v) { s.Append(k).Append('=').Append((v ?? "").Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n'); }
        static string Un(string v) { StringBuilder b = new StringBuilder(); for (int i = 0; i < v.Length; i++) { if (v[i] == '\\' && i + 1 < v.Length) { i++; b.Append(v[i] == 'n' ? '\n' : v[i]); } else b.Append(v[i]); } return b.ToString(); }

        static string TextKey(TextLayer t)
        {
            string[] f = { t.Text, t.FontName, Fs(t.SizePx), t.Color.ToArgb().ToString(), t.Bold ? "1" : "0", t.Italic ? "1" : "0", t.Outline ? "1" : "0", t.Shadow ? "1" : "0", t.Box ? "1" : "0",
                           t.BoxColor.ToArgb().ToString(), t.OutlineColor.ToArgb().ToString(), Fs(t.OutlineWidth), Fs(t.Angle), ((int)t.Align).ToString(), Fs(t.Pos.X), Fs(t.Pos.Y), Fs(t.Opacity) };
            StringBuilder b = new StringBuilder();
            foreach (string x in f) b.Append(Uri.EscapeDataString(x ?? "")).Append('|');
            return b.ToString();
        }
        static TextLayer TextFrom(string k)
        {
            string[] f = k.Split('|');
            for (int i = 0; i < f.Length; i++) f[i] = Uri.UnescapeDataString(f[i]);
            Func<int, float> F = i => { float v; float.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v); return v; };
            Func<int, int> I = i => { int v; int.TryParse(f[i], out v); return v; };
            TextLayer t = new TextLayer
            {
                Text = f[0], FontName = f[1], SizePx = F(2), Color = Color.FromArgb(I(3)), Bold = f[4] == "1", Italic = f[5] == "1", Outline = f[6] == "1", Shadow = f[7] == "1", Box = f[8] == "1",
                BoxColor = I(9) == 0 ? Color.Empty : Color.FromArgb(I(9)), OutlineColor = I(10) == 0 ? Color.Empty : Color.FromArgb(I(10)), OutlineWidth = F(11), Angle = F(12),
                Align = (StringAlignment)I(13), Pos = new PointF(F(14), F(15)), Opacity = f.Length > 16 ? F(16) : 1f
            };
            return t;
        }

        public static PhotoDoc OpenProject(string path)
        {
            PhotoDoc d = new PhotoDoc { ProjectPath = path, Title = Path.GetFileNameWithoutExtension(path) };
            using (ZipArchive z = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry meta = z.GetEntry("photo.txt");
                if (meta == null) throw new UserError(L.T("ps_proj_bad"));
                string text;
                using (StreamReader r = new StreamReader(meta.Open(), Encoding.UTF8)) text = r.ReadToEnd();
                DocLayer l = null;
                foreach (string line0 in text.Split('\n'))
                {
                    string line = line0.TrimEnd('\r');
                    if (line.StartsWith("size ")) { string[] p = line.Split(' '); d.W = int.Parse(p[1]); d.H = int.Parse(p[2]); continue; }
                    if (line.StartsWith("source ")) { d.SourcePath = line.Substring(7); continue; }
                    if (line.StartsWith("current ")) { int.TryParse(line.Substring(8), out d.Current); continue; }
                    if (line == "layer") { l = new DocLayer(); d.Layers.Add(l); continue; }
                    int eq = line.IndexOf('=');
                    if (l == null || eq < 0) continue;
                    string k = line.Substring(0, eq), v = Un(line.Substring(eq + 1));
                    float f; float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f);
                    switch (k)
                    {
                        case "id": l.Id = v; break;
                        case "name": l.Name = v; break;
                        case "kind": l.Kind = (LayerKind)Enum.Parse(typeof(LayerKind), v); break;
                        case "hidden": l.Hidden = v == "1"; break;
                        case "locked": l.Locked = v == "1"; break;
                        case "collapsed": l.Collapsed = v == "1"; break;
                        case "opacity": l.Opacity = f; break;
                        case "blend": l.Blend = (BlendMode)Enum.Parse(typeof(BlendMode), v); break;
                        case "group": l.Group = v.Length > 0 ? v : null; break;
                        case "x": l.X = f; break;
                        case "y": l.Y = f; break;
                        case "scale": l.Scale = f; break;
                        case "angle": l.Angle = f; break;
                        case "flip": l.FlipX = v.Length > 0 && v[0] == '1'; l.FlipY = v.Length > 1 && v[1] == '1'; break;
                        case "path": l.SourcePath = v.Length > 0 ? v : null; break;
                        case "pixels":
                            {
                                ZipArchiveEntry e = z.GetEntry(v);
                                if (e != null)
                                    using (Stream s = e.Open()) using (MemoryStream ms = new MemoryStream()) { s.CopyTo(ms); ms.Position = 0; using (Image im = Image.FromStream(ms)) l.Pixels = Pix.Argb(im); }
                                break;
                            }
                        case "mask":
                            {
                                ZipArchiveEntry e = z.GetEntry(v);
                                if (e != null)
                                    using (Stream s = e.Open()) using (MemoryStream ms = new MemoryStream())
                                    {
                                        s.CopyTo(ms); byte[] all = ms.ToArray();
                                        int len = BitConverter.ToInt32(all, 0);
                                        l.Mask = new byte[len]; Buffer.BlockCopy(all, 4, l.Mask, 0, Math.Min(len, all.Length - 4));
                                    }
                                break;
                            }
                        case "text": l.Text = TextFrom(v); break;
                        case "adjtype": l.AdjType = (AdjKind)Enum.Parse(typeof(AdjKind), v); break;
                        case "adj": l.Adj = Adjust.FromKey(v) ?? new Adjust(); break;
                        case "tone": l.Tone = Tone.FromKey(v); break;
                    }
                }
            }
            if (d.W < 1 || d.H < 1 || d.Layers.Count == 0) throw new UserError(L.T("ps_proj_bad"));
            if (d.Current < 0 || d.Current >= d.Layers.Count) d.Current = d.Layers.Count - 1;
            return d;
        }
    }
}
