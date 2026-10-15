using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FileConverter
{
    /// Photo adjustments in the Studio: light, contrast, color, warmth, a look (filter), blur and sharpness.
    public class Adjust
    {
        public float Bright, Contrast, Sat, Warmth;   // -100..100
        public float Blur, Sharpen;                   // 0..100
        public int Filter;                            // index into FilterKeys

        public static readonly string[] FilterKeys = { "fl_none", "fl_vivid", "fl_warm", "fl_cool", "fl_bw", "fl_sepia", "fl_vintage", "fl_fade", "fl_drama" };

        public bool None { get { return Bright == 0 && Contrast == 0 && Sat == 0 && Warmth == 0 && Blur == 0 && Sharpen == 0 && Filter == 0; } }
        public Adjust Clone() { return (Adjust)MemberwiseClone(); }
        public string Key()
        {
            return string.Join(",", new[] { Bright, Contrast, Sat, Warmth, Blur, Sharpen }.Select2(v => Math.Round(v).ToString(CultureInfo.InvariantCulture))) + "," + Filter;
        }
        public bool Same(Adjust o) { return o != null && Key() == o.Key(); }

        // ------------------------------------------------------------------ color (one 5×5 matrix)
        static float[][] I()
        {
            return new[] { new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } };
        }
        static float[][] Mul(float[][] a, float[][] b)
        {
            float[][] r = new float[5][];
            for (int i = 0; i < 5; i++) { r[i] = new float[5]; for (int j = 0; j < 5; j++) { float s = 0; for (int k = 0; k < 5; k++) s += a[i][k] * b[k][j]; r[i][j] = s; } }
            return r;
        }
        static float[][] Saturation(float s)
        {
            const float lr = 0.3086f, lg = 0.6094f, lb = 0.0820f;
            float[][] m = I();
            m[0][0] = (1 - s) * lr + s; m[0][1] = (1 - s) * lr; m[0][2] = (1 - s) * lr;
            m[1][0] = (1 - s) * lg; m[1][1] = (1 - s) * lg + s; m[1][2] = (1 - s) * lg;
            m[2][0] = (1 - s) * lb; m[2][1] = (1 - s) * lb; m[2][2] = (1 - s) * lb + s;
            return m;
        }
        static float[][] Contrast2(float k)
        {
            float[][] m = I();
            m[0][0] = m[1][1] = m[2][2] = k;
            float t = 0.5f * (1 - k);
            m[4][0] = m[4][1] = m[4][2] = t;
            return m;
        }
        static float[][] Lift(float v) { float[][] m = I(); m[4][0] = m[4][1] = m[4][2] = v; return m; }
        static float[][] Warm(float w)
        {
            float[][] m = I();
            m[0][0] = 1 + 0.12f * w; m[2][2] = 1 - 0.12f * w;
            m[4][0] = 0.03f * w; m[4][2] = -0.03f * w;
            return m;
        }
        static float[][] Sepia()
        {
            return new[] {
                new float[] { 0.393f, 0.349f, 0.272f, 0, 0 },
                new float[] { 0.769f, 0.686f, 0.534f, 0, 0 },
                new float[] { 0.189f, 0.168f, 0.131f, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 0, 0, 1 } };
        }
        static float[][] Mix(float[][] a, float[][] b, float t)
        {
            float[][] r = new float[5][];
            for (int i = 0; i < 5; i++) { r[i] = new float[5]; for (int j = 0; j < 5; j++) r[i][j] = a[i][j] * (1 - t) + b[i][j] * t; }
            return r;
        }

        float[][] Look()
        {
            switch (Filter)
            {
                case 1: return Mul(Saturation(1.35f), Contrast2(1.08f));                               // vivid
                case 2: return Mul(Warm(0.7f), Saturation(1.08f));                                     // warm
                case 3: return Mul(Warm(-0.7f), Saturation(0.95f));                                    // cool
                case 4: return Mul(Saturation(0), Contrast2(1.12f));                                   // black & white
                case 5: return Sepia();                                                                // sepia
                case 6: return Mul(Mul(Mix(I(), Sepia(), 0.45f), Contrast2(0.88f)), Lift(0.05f));      // vintage
                case 7: return Mul(Mul(Saturation(0.75f), Contrast2(0.82f)), Lift(0.07f));             // faded
                case 8: return Mul(Mul(Contrast2(1.35f), Saturation(0.85f)), Lift(-0.04f));            // dramatic
                default: return I();
            }
        }

        public float[][] Matrix()
        {
            float[][] m = Look();
            if (Warmth != 0) m = Mul(m, Warm(Warmth / 100f));
            if (Sat != 0) m = Mul(m, Saturation(1 + Sat / 100f * (Sat > 0 ? 0.9f : 1f)));
            if (Contrast != 0) m = Mul(m, Contrast2(Contrast > 0 ? 1 + Contrast / 100f * 0.8f : 1 + Contrast / 100f * 0.6f));
            if (Bright != 0) m = Mul(m, Lift(Bright / 100f * 0.3f));
            return m;
        }

        /// A new picture with the adjustments (src stays as it is).
        public Bitmap Apply(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            using (ImageAttributes ia = new ImageAttributes())
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                ia.SetColorMatrix(new ColorMatrix(Matrix()));
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }
            int shortSide = Math.Min(w, h);
            if (Blur > 0 || Sharpen > 0)
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = d.Stride;
                    byte[] px = new byte[stride * h];
                    Marshal.Copy(d.Scan0, px, 0, px.Length);
                    if (Blur > 0)
                    {
                        int r = Math.Max(1, (int)Math.Round(Blur / 100f * 0.025f * shortSide));
                        for (int pass = 0; pass < 3; pass++) BoxBlur(px, w, h, stride, r);
                    }
                    if (Sharpen > 0)
                    {
                        byte[] soft = (byte[])px.Clone();
                        int r = Math.Max(1, shortSide / 500);
                        BoxBlur(soft, w, h, stride, r);
                        float amt = Sharpen / 100f * 1.1f;
                        for (int i = 0; i < px.Length; i += 4)
                            for (int c = 0; c < 3; c++)
                            {
                                int v = px[i + c] + (int)(amt * (px[i + c] - soft[i + c]));
                                px[i + c] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                            }
                    }
                    Marshal.Copy(px, 0, d.Scan0, px.Length);
                }
                finally { b.UnlockBits(d); }
            }
            return b;
        }

        /// One box-blur pass (rows, then columns); three passes look like a smooth (Gaussian) blur.
        static void BoxBlur(byte[] px, int w, int h, int stride, int r)
        {
            int[] acc = new int[4];
            byte[] line = new byte[Math.Max(w, h) * 4];
            int win = r * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int o = y * stride;
                Array.Copy(px, o, line, 0, w * 4);
                Run(line, w, r, win, acc, px, o, 4);
            }
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++) { int s = y * stride + x * 4; line[y * 4] = px[s]; line[y * 4 + 1] = px[s + 1]; line[y * 4 + 2] = px[s + 2]; line[y * 4 + 3] = px[s + 3]; }
                Run(line, h, r, win, acc, px, x * 4, stride);
            }
        }

        static void Run(byte[] line, int n, int r, int win, int[] acc, byte[] dst, int start, int step)
        {
            for (int c = 0; c < 4; c++) acc[c] = 0;
            for (int k = -r; k <= r; k++) { int i = Math.Max(0, Math.Min(n - 1, k)) * 4; for (int c = 0; c < 4; c++) acc[c] += line[i + c]; }
            for (int i = 0; i < n; i++)
            {
                int o = start + i * step;
                for (int c = 0; c < 4; c++) dst[o + c] = (byte)(acc[c] / win);
                int add = Math.Min(n - 1, i + r + 1) * 4, rem = Math.Max(0, i - r) * 4;
                for (int c = 0; c < 4; c++) acc[c] += line[add + c] - line[rem + c];
            }
        }
    }

    static class SelectExt
    {
        public static string[] Select2(this float[] a, Func<float, string> f) { string[] r = new string[a.Length]; for (int i = 0; i < a.Length; i++) r[i] = f(a[i]); return r; }
    }
}
