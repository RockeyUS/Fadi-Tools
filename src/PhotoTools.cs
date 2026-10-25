using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FileConverter
{
    public enum SelOp { New, Add, Subtract, Intersect }

    /// Selections: shapes → masks (one byte per canvas pixel), combined, inverted, softened, and their outline.
    public static class Selection
    {
        /// A shape (canvas pixels) as a mask the canvas' size.
        public static byte[] FromPath(GraphicsPath p, int w, int h)
        {
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using (SolidBrush br = new SolidBrush(Color.White)) g.FillPath(br, p);
                }
                int[] px = Pix.Get(b);
                byte[] m = new byte[w * h];
                for (int i = 0; i < m.Length; i++) m[i] = (byte)((uint)px[i] >> 24);
                return m;
            }
        }

        public static byte[] Rect(RectangleF r, int w, int h, bool ellipse)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                if (ellipse) p.AddEllipse(r); else p.AddRectangle(r);
                return FromPath(p, w, h);
            }
        }

        public static byte[] Lasso(List<PointF> pts, int w, int h)
        {
            if (pts.Count < 3) return null;
            using (GraphicsPath p = new GraphicsPath()) { p.AddPolygon(pts.ToArray()); return FromPath(p, w, h); }
        }

        /// Combines a new shape with what's selected.
        public static byte[] Combine(byte[] cur, byte[] add, SelOp op)
        {
            if (add == null) return cur;
            if (cur == null || op == SelOp.New) return op == SelOp.Subtract || op == SelOp.Intersect ? (cur == null ? null : Combine(cur, add, op)) : add;
            byte[] o = new byte[cur.Length];
            for (int i = 0; i < o.Length; i++)
            {
                int a = cur[i], b = add[i];
                switch (op)
                {
                    case SelOp.Add: o[i] = (byte)Math.Max(a, b); break;
                    case SelOp.Subtract: o[i] = (byte)Math.Max(0, a - b); break;
                    default: o[i] = (byte)(a * b / 255); break;
                }
            }
            return Empty(o) ? null : o;
        }

        public static bool Empty(byte[] m) { if (m == null) return true; for (int i = 0; i < m.Length; i++) if (m[i] > 8) return false; return true; }

        public static byte[] Invert(byte[] m, int w, int h)
        {
            byte[] o = new byte[w * h];
            for (int i = 0; i < o.Length; i++) o[i] = (byte)(255 - (m == null ? 0 : m[i]));
            return o;
        }

        /// Softens the edge (a blur of the mask, `r` canvas pixels).
        public static byte[] Feather(byte[] m, int w, int h, int r)
        {
            if (m == null || r < 1) return m;
            float[] a = new float[m.Length], b = new float[m.Length];
            for (int i = 0; i < m.Length; i++) a[i] = m[i];
            for (int pass = 0; pass < 3; pass++)
            {
                // a box blur, three times ≈ a gaussian
                for (int y = 0; y < h; y++)
                {
                    float sum = 0; int row = y * w;
                    for (int x = -r; x <= r; x++) sum += a[row + Math.Max(0, Math.Min(w - 1, x))];
                    for (int x = 0; x < w; x++)
                    {
                        b[row + x] = sum / (2 * r + 1);
                        sum += a[row + Math.Min(w - 1, x + r + 1)] - a[row + Math.Max(0, x - r)];
                    }
                }
                for (int x = 0; x < w; x++)
                {
                    float sum = 0;
                    for (int y = -r; y <= r; y++) sum += b[Math.Max(0, Math.Min(h - 1, y)) * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        a[y * w + x] = sum / (2 * r + 1);
                        sum += b[Math.Min(h - 1, y + r + 1) * w + x] - b[Math.Max(0, y - r) * w + x];
                    }
                }
            }
            byte[] o = new byte[m.Length];
            for (int i = 0; i < o.Length; i++) o[i] = (byte)Math.Max(0, Math.Min(255, a[i] + 0.5f));
            return o;
        }

        public static Rectangle Bounds(byte[] m, int w, int h)
        {
            if (m == null) return Rectangle.Empty;
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++) { int row = y * w; for (int x = 0; x < w; x++) if (m[row + x] > 127) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; } }
            return x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
        }

        /// The edge of the selection as short lines, at a scale (for the "marching ants").
        public static List<PointF[]> Outline(byte[] m, int w, int h, float scale)
        {
            List<PointF[]> segs = new List<PointF[]>();
            if (m == null) return segs;
            // work on a grid no finer than the screen shows it
            int step = Math.Max(1, (int)Math.Floor(1 / Math.Max(0.0001f, scale)));
            int gw = (w + step - 1) / step, gh = (h + step - 1) / step;
            Func<int, int, bool> In = (gx, gy) => gx >= 0 && gy >= 0 && gx < gw && gy < gh && m[Math.Min(h - 1, gy * step) * w + Math.Min(w - 1, gx * step)] > 127;
            float s = step * scale;
            for (int gy = 0; gy <= gh; gy++)
            {
                int runStart = -1;
                for (int gx = 0; gx <= gw; gx++)
                {
                    bool edge = gx < gw && In(gx, gy) != In(gx, gy - 1);
                    if (edge && runStart < 0) runStart = gx;
                    if (!edge && runStart >= 0) { segs.Add(new[] { new PointF(runStart * s, gy * s), new PointF(gx * s, gy * s) }); runStart = -1; }
                }
            }
            for (int gx = 0; gx <= gw; gx++)
            {
                int runStart = -1;
                for (int gy = 0; gy <= gh; gy++)
                {
                    bool edge = gy < gh && In(gx, gy) != In(gx - 1, gy);
                    if (edge && runStart < 0) runStart = gy;
                    if (!edge && runStart >= 0) { segs.Add(new[] { new PointF(gx * s, runStart * s), new PointF(gx * s, gy * s) }); runStart = -1; }
                }
            }
            return segs;
        }

        /// Magic wand on the photo: pixels that look like the clicked one, touching it.
        public static byte[] Wand(Bitmap flat, Point at, int tolerance, bool contiguous)
        {
            int w = flat.Width, h = flat.Height;
            if (at.X < 0 || at.Y < 0 || at.X >= w || at.Y >= h) return null;
            int[] px = Pix.Get(flat);
            int c = px[at.Y * w + at.X];
            int cr = (c >> 16) & 255, cg = (c >> 8) & 255, cb = c & 255, ca = (int)((uint)c >> 24);
            Func<int, bool> Like = i =>
            {
                int p = px[i];
                return Math.Abs(((p >> 16) & 255) - cr) <= tolerance && Math.Abs(((p >> 8) & 255) - cg) <= tolerance && Math.Abs((p & 255) - cb) <= tolerance && Math.Abs((int)((uint)p >> 24) - ca) <= tolerance;
            };
            byte[] m = new byte[w * h];
            if (!contiguous) { for (int i = 0; i < m.Length; i++) if (Like(i)) m[i] = 255; return m; }
            Stack<int> st = new Stack<int>();
            st.Push(at.Y * w + at.X);
            while (st.Count > 0)
            {
                int i = st.Pop();
                if (m[i] != 0 || !Like(i)) continue;
                m[i] = 255;
                int x = i % w, y = i / w;
                if (x > 0) st.Push(i - 1); if (x < w - 1) st.Push(i + 1);
                if (y > 0) st.Push(i - w); if (y < h - 1) st.Push(i + w);
            }
            return m;
        }
    }

    /// Painting: a soft round brush on a layer's pixels (or erasing them), only inside the selection when there is one.
    public static class Brush
    {
        /// The dabs along a stroke, spaced evenly.
        public static List<PointF> Dabs(List<PointF> pts, float radius)
        {
            List<PointF> d = new List<PointF>();
            if (pts.Count == 0) return d;
            float step = Math.Max(0.75f, radius / 4);
            d.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++)
            {
                PointF a = pts[i - 1], b = pts[i];
                float dx = b.X - a.X, dy = b.Y - a.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
                int n = Math.Max(1, (int)(len / step));
                for (int k = 1; k <= n; k++) d.Add(new PointF(a.X + dx * k / n, a.Y + dy * k / n));
            }
            return d;
        }

        /// How much of the brush covers each pixel of a w×h picture (0..1), the strongest dab wins.
        public static float[] Cover(List<PointF> dabs, float radius, float hardness, int w, int h, out Rectangle box)
        {
            float[] c = new float[w * h];
            float r = Math.Max(0.5f, radius), inner = r * Math.Max(0, Math.Min(0.99f, hardness));
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            foreach (PointF p in dabs)
            {
                int ax = Math.Max(0, (int)(p.X - r)), bx = Math.Min(w - 1, (int)(p.X + r) + 1);
                int ay = Math.Max(0, (int)(p.Y - r)), by = Math.Min(h - 1, (int)(p.Y + r) + 1);
                if (ax > bx || ay > by) continue;
                x0 = Math.Min(x0, ax); y0 = Math.Min(y0, ay); x1 = Math.Max(x1, bx); y1 = Math.Max(y1, by);
                for (int y = ay; y <= by; y++)
                {
                    float fy = y + 0.5f - p.Y;
                    for (int x = ax; x <= bx; x++)
                    {
                        float fx = x + 0.5f - p.X, d = (float)Math.Sqrt(fx * fx + fy * fy);
                        if (d >= r) continue;
                        float a = d <= inner ? 1 : 1 - (d - inner) / Math.Max(0.5f, r - inner);
                        a = a * a * (3 - 2 * a);
                        int i = y * w + x;
                        if (a > c[i]) c[i] = a;
                    }
                }
            }
            box = x1 < 0 ? Rectangle.Empty : Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            return c;
        }

        /// Paints (or erases) a stroke onto a canvas-sized painting; returns new pixels (the old ones stay for undo).
        public static Bitmap Paint(Bitmap layer, List<PointF> pts, float radius, float hardness, float opacity, Color color, bool erase, byte[] sel)
        {
            int w = layer.Width, h = layer.Height;
            Rectangle box;
            float[] cov = Cover(Dabs(pts, radius), radius, hardness, w, h, out box);
            int[] px = Pix.Get(layer);
            float cr = color.R / 255f, cg = color.G / 255f, cb = color.B / 255f;
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    int i = y * w + x;
                    float a = cov[i] * opacity;
                    if (sel != null && i < sel.Length) a *= sel[i] / 255f;
                    if (a <= 0) continue;
                    int p = px[i];
                    float pa = ((uint)p >> 24) / 255f;
                    if (erase) { int na = (int)(pa * (1 - a) * 255 + 0.5f); px[i] = (p & 0xFFFFFF) | (na << 24); continue; }
                    float oa = a + pa * (1 - a);
                    float pr = ((p >> 16) & 255) / 255f, pg = ((p >> 8) & 255) / 255f, pb = (p & 255) / 255f;
                    float nr = (cr * a + pr * pa * (1 - a)) / oa, ng = (cg * a + pg * pa * (1 - a)) / oa, nb = (cb * a + pb * pa * (1 - a)) / oa;
                    px[i] = (C(oa) << 24) | (C(nr) << 16) | (C(ng) << 8) | C(nb);
                }
            return Pix.Make(w, h, px);
        }

        /// Erases (or brings back) part of a picture layer through its mask: the stroke is in canvas pixels.
        public static byte[] EraseMask(DocLayer l, List<PointF> docPts, float radius, float hardness, float opacity, bool restore, byte[] sel, int docW)
        {
            int w = l.Pixels.Width, h = l.Pixels.Height;
            List<PointF> local = new List<PointF>();
            foreach (PointF p in docPts) local.Add(l.ToLayer(p));
            float r = radius / Math.Max(0.01f, l.Scale);
            Rectangle box;
            float[] cov = Cover(Dabs(local, r), r, hardness, w, h, out box);
            byte[] m = l.Mask != null ? (byte[])l.Mask.Clone() : MaskEdit.Full(w, h);
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    int i = y * w + x;
                    float a = cov[i] * opacity;
                    if (a <= 0) continue;
                    if (sel != null)
                    {
                        // the selection is on the canvas: look up where this pixel lands
                        PointF d = FromLayer(l, x + 0.5f, y + 0.5f);
                        int sx = (int)d.X, sy = (int)d.Y;
                        if (sx < 0 || sy < 0 || sx >= docW || sy * docW + sx >= sel.Length) continue;
                        a *= sel[sy * docW + sx] / 255f;
                    }
                    m[i] = restore ? (byte)Math.Min(255, m[i] + a * (255 - m[i])) : (byte)(m[i] * (1 - a));
                }
            return m;
        }

        /// Hides (or brings back) part of a canvas-sized mask (an adjustment's, or a painting's).
        public static byte[] EraseCanvasMask(byte[] mask, int w, int h, List<PointF> pts, float radius, float hardness, float opacity, bool restore, byte[] sel)
        {
            Rectangle box;
            float[] cov = Cover(Dabs(pts, radius), radius, hardness, w, h, out box);
            byte[] m = mask != null ? (byte[])mask.Clone() : MaskEdit.Full(w, h);
            for (int y = box.Top; y < box.Bottom; y++)
                for (int x = box.Left; x < box.Right; x++)
                {
                    int i = y * w + x;
                    float a = cov[i] * opacity;
                    if (sel != null) a *= sel[i] / 255f;
                    if (a <= 0) continue;
                    m[i] = restore ? (byte)Math.Min(255, m[i] + a * (255 - m[i])) : (byte)(m[i] * (1 - a));
                }
            return m;
        }

        public static PointF FromLayer(DocLayer l, float lx, float ly)
        {
            double x = lx - l.Pixels.Width / 2.0, y = ly - l.Pixels.Height / 2.0;
            if (l.FlipX) x = -x;
            if (l.FlipY) y = -y;
            x *= l.Scale; y *= l.Scale;
            double a = l.Angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return new PointF((float)(l.X + x * c - y * s), (float)(l.Y + x * s + y * c));
        }

        static int C(float v) { int x = (int)(v * 255 + 0.5f); return x < 0 ? 0 : x > 255 ? 255 : x; }

        /// Fills the selection (or everything) with a color, on a canvas-sized painting.
        public static Bitmap Fill(Bitmap layer, Color color, byte[] sel)
        {
            int[] px = Pix.Get(layer);
            float cr = color.R / 255f, cg = color.G / 255f, cb = color.B / 255f;
            for (int i = 0; i < px.Length; i++)
            {
                float a = sel == null ? 1 : sel[i] / 255f;
                if (a <= 0) continue;
                int p = px[i];
                float pa = ((uint)p >> 24) / 255f, oa = a + pa * (1 - a);
                float pr = ((p >> 16) & 255) / 255f, pg = ((p >> 8) & 255) / 255f, pb = (p & 255) / 255f;
                px[i] = (C(oa) << 24) | (C((cr * a + pr * pa * (1 - a)) / oa) << 16) | (C((cg * a + pg * pa * (1 - a)) / oa) << 8) | C((cb * a + pb * pa * (1 - a)) / oa);
            }
            return Pix.Make(layer.Width, layer.Height, px);
        }

        /// Clears the selection on a canvas-sized painting.
        public static Bitmap Clear(Bitmap layer, byte[] sel)
        {
            int[] px = Pix.Get(layer);
            for (int i = 0; i < px.Length && i < sel.Length; i++)
            {
                if (sel[i] == 0) continue;
                int p = px[i];
                int na = (int)((uint)p >> 24) * (255 - sel[i]) / 255;
                px[i] = (p & 0xFFFFFF) | (na << 24);
            }
            return Pix.Make(layer.Width, layer.Height, px);
        }
    }

    /// "Remove object": fills a hole from the picture around it, so what was there is gone and the background
    /// carries on (patch matching, like content-aware fill). Works on the device, nothing to download.
    public static class Inpaint
    {
        const int P = 3;   // patch radius (7×7 patches)

        /// `hole`: one byte per pixel of `src` (non-zero = to fill). Returns a new picture.
        public static Bitmap Fill(Bitmap src, byte[] hole, Func<bool> cancelled)
        {
            int W = src.Width, H = src.Height;
            Rectangle hb = Rectangle.Empty;
            {
                int x0 = W, y0 = H, x1 = -1, y1 = -1;
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (hole[y * W + x] != 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                if (x1 < 0) return new Bitmap(src);
                hb = Rectangle.FromLTRB(x0, y0, x1 + 1, y1 + 1);
            }
            // work on the hole and a ring of picture around it (where the fill is taken from)
            int margin = Math.Max(40, Math.Max(hb.Width, hb.Height) * 2);
            Rectangle work = Rectangle.Inflate(hb, margin, margin);
            work.Intersect(new Rectangle(0, 0, W, H));
            int ww = work.Width, wh = work.Height;
            int[] all = Pix.Get(src);
            int[] img = new int[ww * wh];
            bool[] mask = new bool[ww * wh];
            for (int y = 0; y < wh; y++)
                for (int x = 0; x < ww; x++)
                {
                    int si = (work.Y + y) * W + work.X + x;
                    img[y * ww + x] = all[si];
                    mask[y * ww + x] = hole[si] != 0;
                }
            // grow the hole a little: the edge of an object is often a halo
            mask = Dilate(mask, ww, wh, 2);
            int[] filled = Complete(img, mask, ww, wh, cancelled);
            Seamless(filled, img, mask, ww, wh);
            for (int y = 0; y < wh; y++)
                for (int x = 0; x < ww; x++)
                    if (mask[y * ww + x])
                    {
                        int si = (work.Y + y) * W + work.X + x;
                        all[si] = (all[si] & unchecked((int)0xFF000000)) | (filled[y * ww + x] & 0xFFFFFF);
                    }
            return Pix.Make(W, H, all);
        }

        /// Keeps the fill's detail but makes its colors meet the picture around it without a seam: the difference
        /// at the hole's edge is spread smoothly over the hole (a harmonic membrane, solved coarse to fine).
        static void Seamless(int[] fill, int[] orig, bool[] m, int w, int h)
        {
            bool[] fixd = new bool[m.Length];
            bool any = false;
            for (int i = 0; i < m.Length; i++)
            {
                if (!m[i]) continue;
                int x = i % w, y = i / w;
                if ((x > 0 && !m[i - 1]) || (x < w - 1 && !m[i + 1]) || (y > 0 && !m[i - w]) || (y < h - 1 && !m[i + w])) { fixd[i] = true; any = true; }
            }
            if (!any) return;
            for (int ch = 0; ch < 3; ch++)
            {
                int sh = 16 - ch * 8;
                float[] c = new float[m.Length];
                for (int i = 0; i < m.Length; i++)
                {
                    if (!fixd[i]) continue;
                    int x = i % w, y = i / w; float sum = 0, n = 0;
                    if (x > 0 && !m[i - 1]) { sum += (orig[i - 1] >> sh) & 255; n++; }
                    if (x < w - 1 && !m[i + 1]) { sum += (orig[i + 1] >> sh) & 255; n++; }
                    if (y > 0 && !m[i - w]) { sum += (orig[i - w] >> sh) & 255; n++; }
                    if (y < h - 1 && !m[i + w]) { sum += (orig[i + w] >> sh) & 255; n++; }
                    c[i] = sum / n - ((fill[i] >> sh) & 255);
                }
                Harmonic(c, fixd, m, w, h);
                for (int i = 0; i < m.Length; i++)
                {
                    if (!m[i]) continue;
                    int v = (int)Math.Max(0, Math.Min(255, ((fill[i] >> sh) & 255) + c[i] + 0.5f));
                    fill[i] = (fill[i] & ~(255 << sh)) | (v << sh);
                }
            }
        }

        /// Fills the free cells of `active` so they vary smoothly between the fixed ones (coarse grid first).
        static void Harmonic(float[] v, bool[] fixd, bool[] active, int w, int h)
        {
            if (w > 24 && h > 24)
            {
                int cw = (w + 1) / 2, chh = (h + 1) / 2;
                float[] cv = new float[cw * chh]; bool[] cf = new bool[cw * chh], ca = new bool[cw * chh];
                float[] cs = new float[cw * chh];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x, j = (y / 2) * cw + x / 2;
                        if (active[i]) ca[j] = true;
                        if (fixd[i]) { cf[j] = true; cv[j] += v[i]; cs[j]++; }
                    }
                for (int j = 0; j < cv.Length; j++) if (cf[j]) cv[j] /= cs[j];
                Harmonic(cv, cf, ca, cw, chh);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (active[i] && !fixd[i]) v[i] = cv[(y / 2) * cw + x / 2];
                    }
            }
            List<int> free = new List<int>();
            for (int i = 0; i < v.Length; i++) if (active[i] && !fixd[i]) free.Add(i);
            int iters = w > 24 && h > 24 ? 40 : 400;
            for (int it = 0; it < iters; it++)
                foreach (int i in free)
                {
                    int x = i % w, y = i / w; float sum = 0, n = 0;
                    if (x > 0 && active[i - 1]) { sum += v[i - 1]; n++; }
                    if (x < w - 1 && active[i + 1]) { sum += v[i + 1]; n++; }
                    if (y > 0 && active[i - w]) { sum += v[i - w]; n++; }
                    if (y < h - 1 && active[i + w]) { sum += v[i + w]; n++; }
                    if (n > 0) v[i] += 1.8f * (sum / n - v[i]);
                }
        }

        static bool[] Dilate(bool[] m, int w, int h, int r)
        {
            bool[] o = (bool[])m.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (m[y * w + x])
                        for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx >= 0 && ny >= 0 && nx < w && ny < h) o[ny * w + nx] = true;
                            }
            return o;
        }

        /// Multi-scale completion: fill coarse first, then refine at each finer level (Wexler et al. with PatchMatch).
        static int[] Complete(int[] img, bool[] mask, int w, int h, Func<bool> cancelled)
        {
            // the pyramid
            List<int[]> imgs = new List<int[]> { img };
            List<bool[]> masks = new List<bool[]> { mask };
            List<Size> sizes = new List<Size> { new Size(w, h) };
            while (sizes[sizes.Count - 1].Width > 48 && sizes[sizes.Count - 1].Height > 48)
            {
                Size s = sizes[sizes.Count - 1];
                int nw = s.Width / 2, nh = s.Height / 2;
                int[] pi = imgs[imgs.Count - 1]; bool[] pm = masks[masks.Count - 1];
                int[] ni = new int[nw * nh]; bool[] nm = new bool[nw * nh];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int r = 0, g = 0, b = 0, n = 0; bool any = false;
                        for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                            {
                                int i = (y * 2 + dy) * s.Width + x * 2 + dx;
                                if (pm[i]) { any = true; continue; }
                                int p = pi[i]; r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++;
                            }
                        nm[y * nw + x] = any;
                        ni[y * nw + x] = n > 0 ? (255 << 24) | ((r / n) << 16) | ((g / n) << 8) | (b / n) : 0;
                    }
                imgs.Add(ni); masks.Add(nm); sizes.Add(new Size(nw, nh));
            }
            int levels = imgs.Count;
            int[] cur = null;
            int[] nnfX = null, nnfY = null;
            Random rnd = new Random(7);
            for (int lv = levels - 1; lv >= 0; lv--)
            {
                int lw = sizes[lv].Width, lh = sizes[lv].Height;
                int[] src = imgs[lv]; bool[] m = masks[lv];
                int[] work = (int[])src.Clone();
                if (cur == null)
                {
                    // coarsest: a first guess by smearing the edge colors inward
                    SmearFill(work, m, lw, lh);
                }
                else
                {
                    // upscale the coarser answer into the hole
                    int pw = sizes[lv + 1].Width, ph = sizes[lv + 1].Height;
                    for (int y = 0; y < lh; y++) for (int x = 0; x < lw; x++)
                            if (m[y * lw + x]) work[y * lw + x] = cur[Math.Min(ph - 1, y / 2) * pw + Math.Min(pw - 1, x / 2)];
                }
                // the nearest-neighbor field: for each patch in the hole, the best patch from outside it
                int[] fx = new int[lw * lh], fy = new int[lw * lh];
                bool[] valid = ValidSources(m, lw, lh);
                List<int> validList = new List<int>();
                for (int i = 0; i < valid.Length; i++) if (valid[i]) validList.Add(i);
                if (validList.Count == 0) { cur = work; continue; }
                for (int y = 0; y < lh; y++)
                    for (int x = 0; x < lw; x++)
                    {
                        int i = y * lw + x;
                        int pick;
                        if (nnfX != null && lv < levels - 1)
                        {
                            int pw = sizes[lv + 1].Width, ph = sizes[lv + 1].Height;
                            int ci = Math.Min(ph - 1, y / 2) * pw + Math.Min(pw - 1, x / 2);
                            int cx = Math.Min(lw - 1, nnfX[ci] * 2 + (x & 1)), cy = Math.Min(lh - 1, nnfY[ci] * 2 + (y & 1));
                            pick = valid[cy * lw + cx] ? cy * lw + cx : validList[rnd.Next(validList.Count)];
                        }
                        else pick = validList[rnd.Next(validList.Count)];
                        fx[i] = pick % lw; fy[i] = pick / lw;
                    }
                int iters = lv == 0 ? 4 : 6;
                for (int it = 0; it < iters; it++)
                {
                    if (cancelled != null && cancelled()) return work;
                    PatchMatch(work, m, valid, validList, fx, fy, lw, lh, rnd, it);
                    Vote(work, m, fx, fy, lw, lh);
                }
                cur = work; nnfX = fx; nnfY = fy;
            }
            return cur;
        }

        /// Patches entirely outside the hole (and inside the picture) can be copied from.
        static bool[] ValidSources(bool[] m, int w, int h)
        {
            bool[] v = new bool[w * h];
            for (int y = P; y < h - P; y++)
                for (int x = P; x < w - P; x++)
                {
                    bool ok = true;
                    for (int dy = -P; dy <= P && ok; dy++) for (int dx = -P; dx <= P; dx++) if (m[(y + dy) * w + x + dx]) { ok = false; break; }
                    v[y * w + x] = ok;
                }
            return v;
        }

        static void SmearFill(int[] img, bool[] m, int w, int h)
        {
            bool[] known = new bool[m.Length];
            for (int i = 0; i < m.Length; i++) known[i] = !m[i];
            bool changed = true; int guard = 0;
            while (changed && guard++ < Math.Max(w, h))
            {
                changed = false;
                bool[] next = (bool[])known.Clone();
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (known[i]) continue;
                        int r = 0, g = 0, b = 0, n = 0;
                        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                                int j = ny * w + nx;
                                if (!known[j]) continue;
                                int p = img[j]; r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++;
                            }
                        if (n > 0) { img[i] = (255 << 24) | ((r / n) << 16) | ((g / n) << 8) | (b / n); next[i] = true; changed = true; }
                    }
                known = next;
            }
        }

        static long Dist(int[] img, int w, int h, int ax, int ay, int bx, int by, long cap)
        {
            long d = 0;
            for (int dy = -P; dy <= P; dy++)
            {
                int ya = ay + dy, yb = by + dy;
                if (ya < 0 || ya >= h) continue;
                for (int dx = -P; dx <= P; dx++)
                {
                    int xa = ax + dx, xb = bx + dx;
                    if (xa < 0 || xa >= w) continue;
                    int p = img[ya * w + xa], q = img[yb * w + xb];
                    int dr = ((p >> 16) & 255) - ((q >> 16) & 255), dg = ((p >> 8) & 255) - ((q >> 8) & 255), db = (p & 255) - (q & 255);
                    d += dr * dr + dg * dg + db * db;
                    if (d > cap) return d;
                }
            }
            return d;
        }

        static void PatchMatch(int[] img, bool[] m, bool[] valid, List<int> validList, int[] fx, int[] fy, int w, int h, Random rnd, int iter)
        {
            bool fwd = iter % 2 == 0;
            int ys = fwd ? 0 : h - 1, ye = fwd ? h : -1, st = fwd ? 1 : -1;
            for (int y = ys; y != ye; y += st)
                for (int x = fwd ? 0 : w - 1; x != (fwd ? w : -1); x += st)
                {
                    int i = y * w + x;
                    // only patches touching the hole matter
                    if (!Near(m, w, h, x, y)) continue;
                    int bx = fx[i], by = fy[i];
                    long best = Dist(img, w, h, x, y, bx, by, long.MaxValue);
                    // propagation from the neighbors already visited
                    int nx = x - st, ny = y - st;
                    if (nx >= 0 && nx < w)
                    {
                        int j = y * w + nx, cx = fx[j] + st, cy = fy[j];
                        if (cx >= 0 && cx < w && valid[cy * w + cx]) { long d = Dist(img, w, h, x, y, cx, cy, best); if (d < best) { best = d; bx = cx; by = cy; } }
                    }
                    if (ny >= 0 && ny < h)
                    {
                        int j = ny * w + x, cx = fx[j], cy = fy[j] + st;
                        if (cy >= 0 && cy < h && valid[cy * w + cx]) { long d = Dist(img, w, h, x, y, cx, cy, best); if (d < best) { best = d; bx = cx; by = cy; } }
                    }
                    // random search around the best, shrinking
                    for (int r = Math.Max(w, h); r >= 1; r /= 2)
                    {
                        int cx = bx + rnd.Next(-r, r + 1), cy = by + rnd.Next(-r, r + 1);
                        if (cx < 0 || cy < 0 || cx >= w || cy >= h || !valid[cy * w + cx]) continue;
                        long d = Dist(img, w, h, x, y, cx, cy, best);
                        if (d < best) { best = d; bx = cx; by = cy; }
                    }
                    fx[i] = bx; fy[i] = by;
                }
        }

        static bool Near(bool[] m, int w, int h, int x, int y)
        {
            for (int dy = -P; dy <= P; dy++)
            {
                int yy = y + dy; if (yy < 0 || yy >= h) continue;
                for (int dx = -P; dx <= P; dx++) { int xx = x + dx; if (xx >= 0 && xx < w && m[yy * w + xx]) return true; }
            }
            return false;
        }

        /// Each hole pixel becomes the average of what the patches covering it say it should be.
        static void Vote(int[] img, bool[] m, int[] fx, int[] fy, int w, int h)
        {
            int[] outp = (int[])img.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!m[i]) continue;
                    float r = 0, g = 0, b = 0, n = 0;
                    for (int dy = -P; dy <= P; dy++)
                    {
                        int py = y + dy; if (py < 0 || py >= h) continue;
                        for (int dx = -P; dx <= P; dx++)
                        {
                            int px = x + dx; if (px < 0 || px >= w) continue;
                            int j = py * w + px;
                            int sx = fx[j] - dx, sy = fy[j] - dy;
                            if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                            int q = img[sy * w + sx];
                            float wt = 1f;
                            r += ((q >> 16) & 255) * wt; g += ((q >> 8) & 255) * wt; b += (q & 255) * wt; n += wt;
                        }
                    }
                    if (n > 0) outp[i] = (img[i] & unchecked((int)0xFF000000)) | ((int)(r / n) << 16) | ((int)(g / n) << 8) | (int)(b / n);
                }
            Array.Copy(outp, img, img.Length);
        }
    }
}
