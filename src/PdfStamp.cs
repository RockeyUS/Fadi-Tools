using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FileConverter
{
    /// Puts pictures (page numbers, a watermark, a signature) on top of the pages of a PDF without touching
    /// its text: a small see-through PDF is made with the pictures, and qpdf lays it over the real pages.
    public static class PdfStamp
    {
        public class Item { public Bitmap Image; public RectangleF Box; }   // Box: 0..1 of the page, from the top-left
        public class PageSpec { public float W = 612, H = 792; public List<Item> Items = new List<Item>(); }

        // ------------------------------------------------------------------ the overlay PDF
        public static void WriteOverlay(string path, IList<PageSpec> pages)
        {
            Dictionary<Bitmap, int> imageObj = new Dictionary<Bitmap, int>();
            List<Bitmap> images = new List<Bitmap>();
            foreach (PageSpec p in pages)
                foreach (Item it in p.Items)
                    if (!imageObj.ContainsKey(it.Image)) { imageObj[it.Image] = 0; images.Add(it.Image); }
            // numbering: 1 catalog, 2 pages, then page + content for each page, then image + mask for each picture
            int next = 3;
            int[] pageNo = new int[pages.Count], contentNo = new int[pages.Count];
            for (int i = 0; i < pages.Count; i++) { pageNo[i] = next++; contentNo[i] = next++; }
            foreach (Bitmap b in images) { imageObj[b] = next; next += 2; }

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                long[] offsets = new long[next];
                Action<string> w = s => { byte[] d = Encoding.ASCII.GetBytes(s); fs.Write(d, 0, d.Length); };
                w("%PDF-1.4\n%âã\n");
                offsets[1] = fs.Position; w("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                StringBuilder kids = new StringBuilder();
                foreach (int n in pageNo) kids.Append(n).Append(" 0 R ");
                offsets[2] = fs.Position; w("2 0 obj\n<< /Type /Pages /Kids [ " + kids + "] /Count " + pages.Count + " >>\nendobj\n");
                for (int i = 0; i < pages.Count; i++)
                {
                    PageSpec p = pages[i];
                    // qpdf shrinks an overlay page to fit but never enlarges it: make it very big (same shape)
                    float K = Math.Min(10f, 14000f / Math.Max(1f, Math.Max(p.W, p.H)));
                    float PW = p.W * K, PH = p.H * K;
                    StringBuilder res = new StringBuilder(), content = new StringBuilder();
                    HashSet<int> used = new HashSet<int>();
                    foreach (Item it in p.Items)
                    {
                        int o = imageObj[it.Image];
                        if (used.Add(o)) res.Append("/Im").Append(o).Append(' ').Append(o).Append(" 0 R ");
                        float x = it.Box.X * PW, y = (1 - it.Box.Y - it.Box.Height) * PH, bw = it.Box.Width * PW, bh = it.Box.Height * PH;
                        content.Append("q ").Append(F(bw)).Append(" 0 0 ").Append(F(bh)).Append(' ').Append(F(x)).Append(' ').Append(F(y)).Append(" cm /Im").Append(o).Append(" Do Q\n");
                    }
                    offsets[pageNo[i]] = fs.Position;
                    w(pageNo[i] + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(PW) + " " + F(PH) + "] /Resources << /XObject << " + res + ">> >> /Contents " + contentNo[i] + " 0 R >>\nendobj\n");
                    byte[] cs = Encoding.ASCII.GetBytes(content.ToString());
                    offsets[contentNo[i]] = fs.Position;
                    w(contentNo[i] + " 0 obj\n<< /Length " + cs.Length + " >>\nstream\n");
                    fs.Write(cs, 0, cs.Length);
                    w("\nendstream\nendobj\n");
                }
                foreach (Bitmap b in images)
                {
                    int o = imageObj[b];
                    byte[] rgb, alpha;
                    Split(b, out rgb, out alpha);
                    byte[] zr = Zlib(rgb), za = Zlib(alpha);
                    offsets[o] = fs.Position;
                    w(o + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + b.Width + " /Height " + b.Height + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /SMask " + (o + 1) + " 0 R /Length " + zr.Length + " >>\nstream\n");
                    fs.Write(zr, 0, zr.Length); w("\nendstream\nendobj\n");
                    offsets[o + 1] = fs.Position;
                    w((o + 1) + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + b.Width + " /Height " + b.Height + " /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length " + za.Length + " >>\nstream\n");
                    fs.Write(za, 0, za.Length); w("\nendstream\nendobj\n");
                }
                long xref = fs.Position;
                StringBuilder xr = new StringBuilder("xref\n0 " + next + "\n0000000000 65535 f \n");
                for (int i = 1; i < next; i++) xr.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
                xr.Append("trailer\n<< /Size " + next + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                w(xr.ToString());
            }
        }

        static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static void Split(Bitmap b, out byte[] rgb, out byte[] alpha)
        {
            int w = b.Width, h = b.Height;
            rgb = new byte[w * h * 3]; alpha = new byte[w * h];
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), row, 0, row.Length);
                    for (int x = 0; x < w; x++)
                    {
                        int s = x * 4, t = (y * w + x) * 3;
                        rgb[t] = row[s + 2]; rgb[t + 1] = row[s + 1]; rgb[t + 2] = row[s];
                        alpha[y * w + x] = row[s + 3];
                    }
                }
            }
            finally { b.UnlockBits(d); }
        }

        /// zlib wrapper around .NET's deflate (what PDF's FlateDecode expects).
        static byte[] Zlib(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (DeflateStream ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
                uint a = 1, bb = 0;
                foreach (byte v in data) { a = (a + v) % 65521; bb = (bb + a) % 65521; }
                uint ad = (bb << 16) | a;
                ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
                return ms.ToArray();
            }
        }

        /// Lays the pictures over the PDF's pages (one PageSpec per page, in order). Returns an error or null.
        public static string Apply(string inPdf, string outPdf, IList<PageSpec> pages)
        {
            string ov = Path.Combine(Path.GetTempPath(), "fadi_overlay_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".pdf");
            try
            {
                WriteOverlay(ov, pages);
                // turned pages are made really upright first, so the overlay lines up with what you see
                string flat = Path.Combine(Path.GetTempPath(), "fadi_flat_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".pdf");
                try
                {
                    string src = inPdf;
                    if (Qpdf.Run("--flatten-rotation " + Tools.Quote(inPdf) + " " + Tools.Quote(flat)) == null && File.Exists(flat)) src = flat;
                    return Qpdf.Run(Tools.Quote(src) + " --overlay " + Tools.Quote(ov) + " -- " + Tools.Quote(outPdf));
                }
                finally { try { File.Delete(flat); } catch { } }
            }
            finally { try { File.Delete(ov); } catch { } }
        }

        // ------------------------------------------------------------------ the pictures
        const float Dpi = 200f;

        static Bitmap Canvas(float wPt, float hPt, out Graphics g)
        {
            Bitmap b = new Bitmap(Math.Max(1, (int)(wPt / 72f * Dpi)), Math.Max(1, (int)(hPt / 72f * Dpi)), PixelFormat.Format32bppArgb);
            g = Graphics.FromImage(b);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            return b;
        }

        static FontFamily Family()
        {
            foreach (string n in new[] { "Segoe UI", "Arial", "Noto Sans Arabic", "DejaVu Sans" })
                try { return new FontFamily(n); } catch { }
            return FontFamily.GenericSansSerif;
        }

        /// 0 = "3", 1 = "3 / 12", 2 = "Page 3 of 12". where: 0 bottom middle, 1 bottom corner, 2 top corner.
        public static Item PageNumber(PageSpec page, int n, int total, int style, int where)
        {
            string t = style == 0 ? n.ToString(CultureInfo.InvariantCulture) : style == 1 ? n + " / " + total : L.T("pdf_num_text", n, total);
            float boxW = 0.36f, boxH = 0.045f, marginY = 0.025f, marginX = 0.05f;
            bool corner = where != 0, top = where == 2;
            // the corner away from the binding: right in English, left in Arabic
            bool rightSide = !Theme.RTL;
            RectangleF box = new RectangleF(corner ? (rightSide ? 1 - marginX - boxW : marginX) : (1 - boxW) / 2, top ? marginY : 1 - marginY - boxH, boxW, boxH);
            Graphics g;
            Bitmap b = Canvas(page.W * boxW, page.H * boxH, out g);
            using (g)
            using (Font f = new Font(Family(), b.Height * 0.5f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush br = new SolidBrush(Color.FromArgb(235, 55, 55, 60)))
            using (StringFormat sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = corner ? (rightSide ? StringAlignment.Far : StringAlignment.Near) : StringAlignment.Center })
            {
                if (Theme.RTL) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                if (Theme.RTL && corner) sf.Alignment = StringAlignment.Far;   // right-to-left flips near/far
                g.DrawString(t, f, br, new RectangleF(0, 0, b.Width, b.Height), sf);
            }
            return new Item { Image = b, Box = box };
        }

        /// where: 0 across the page (corner to corner), 1 big in the middle, 2 a line at the top. strength 0..2.
        public static Item Watermark(PageSpec page, string text, Color color, int where, int strength)
        {
            int alpha = new[] { 40, 70, 110 }[Math.Max(0, Math.Min(2, strength))];
            if (where == 2)
            {
                RectangleF box = new RectangleF(0.06f, 0.02f, 0.88f, 0.05f);
                Graphics g;
                Bitmap b = Canvas(page.W * box.Width, page.H * box.Height, out g);
                using (g)
                using (Font f = new Font(Family(), b.Height * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (SolidBrush br = new SolidBrush(Color.FromArgb(Math.Min(255, alpha * 2), color)))
                using (StringFormat sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center })
                    g.DrawString(text, f, br, new RectangleF(0, 0, b.Width, b.Height), sf);
                return new Item { Image = b, Box = box };
            }
            {
                // a lighter picture for the big ones (they cover the whole page)
                Graphics g;
                Bitmap b = Canvas(page.W * 0.5f, page.H * 0.5f, out g);
                using (g)
                using (SolidBrush br = new SolidBrush(Color.FromArgb(alpha, color)))
                {
                    float W = b.Width, H = b.Height;
                    float angle = where == 0 ? -(float)(Math.Atan2(H, W) * 180 / Math.PI) : 0;
                    float room = where == 0 ? (float)Math.Sqrt(W * W + H * H) * 0.8f : W * 0.85f;
                    // the largest text that fits
                    float size = H / 4;
                    using (StringFormat sf = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center })
                    {
                        for (int i = 0; i < 30; i++)
                        {
                            using (Font f = new Font(Family(), size, FontStyle.Bold, GraphicsUnit.Pixel))
                            {
                                SizeF m = g.MeasureString(text, f);
                                if (m.Width <= room && m.Height <= (where == 0 ? H * 0.35f : H * 0.3f)) break;
                            }
                            size *= 0.9f;
                        }
                        g.TranslateTransform(W / 2, H / 2);
                        g.RotateTransform(angle);
                        using (Font f = new Font(Family(), size, FontStyle.Bold, GraphicsUnit.Pixel))
                            g.DrawString(text, f, br, new RectangleF(-room / 2, -H / 2, room, H), sf);
                    }
                }
                return new Item { Image = b, Box = new RectangleF(0, 0, 1, 1) };
            }
        }
    }
}
