using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Export: the format, the quality and the size, with how big the file will be (worked out as you change them).
    public class ExportForm : FormDialog
    {
        readonly Bitmap flat;
        readonly bool alpha;
        readonly ChipGrid format = new ChipGrid { Columns = 4, RowH = 36 };
        readonly ChipGrid size = new ChipGrid { Columns = 4, RowH = 34 };
        readonly Slider quality = new Slider { Min = 30, Max = 100, Value = 90 };
        readonly System.Windows.Forms.Timer wait = new System.Windows.Forms.Timer { Interval = 260 };
        Rectangle estR, noteR;
        string estimate = "";
        int token;

        public ExportForm(Bitmap flat, bool alpha) : base(L.T("ps_export_title"), L.T("ps_export_sub"), L.T("ps_export"))
        {
            this.flat = flat; this.alpha = alpha;
            DialogWidth = 520;
            List<string> f = new List<string> { "jpg", "png" };
            if (Tools.Magick != null) f.Add("webp");
            f.Add("pdf");
            string last = Settings.Get("PsExport", alpha ? "png" : "jpg");
            if (!f.Contains(last)) last = alpha ? "png" : "jpg";
            format.LabelOf = k => k.ToUpperInvariant();
            format.SetItems(f, last);
            format.SelectedChanged += (s, e) => { Settings.Set("PsExport", format.Selected); Changed(); };
            quality.Format = v => ((int)Math.Round(v)).ToString();
            float q; if (float.TryParse(Settings.Get("PsQuality", "90"), out q)) quality.SetValue(Math.Max(30, Math.Min(100, q)), false);
            quality.ValueChanged += (s, e) => Changed();
            quality.Committed += (s, e) => Settings.Set("PsQuality", ((int)quality.Value).ToString());
            size.LabelOf = k => k;
            size.SetItems(new[] { "100%", "75%", "50%", "25%" }, "100%");
            size.SelectedChanged += (s, e) => Changed();
            Add(() => L.T("ps_format"), format, 36);
            Add(() => L.T("ps_quality"), quality, 30);
            Add(() => L.T("ps_out_size"), size, 34);
            wait.Tick += (s, e) => { wait.Stop(); Estimate(); };
            Changed();
        }

        public string Ext { get { return format.Selected; } }
        public int Quality { get { return (int)Math.Round(quality.Value); } }
        public float OutScale { get { return int.Parse(size.Selected.TrimEnd('%')) / 100f; } }

        void Changed()
        {
            quality.Enabled = Ext != "png";
            quality.Invalidate();
            estimate = L.T("ps_estimating");
            Invalidate();
            wait.Stop(); wait.Start();
        }

        void Estimate()
        {
            int my = ++token;
            string ext = Ext; int q = Quality; float sc = OutScale;
            Thread t = new Thread(() =>
            {
                long bytes = -1;
                try { bytes = Measure(flat, ext, q, sc); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (my != token) return;
                        int w = Math.Max(1, (int)Math.Round(flat.Width * sc)), h = Math.Max(1, (int)Math.Round(flat.Height * sc));
                        estimate = w + " × " + h + "   ·   " + (bytes < 0 ? "?" : L.T("ps_about", Theme.HumanSize(bytes)));
                        Invalidate();
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }

        static Bitmap Scaled(Bitmap b, float sc)
        {
            int w = Math.Max(1, (int)Math.Round(b.Width * sc)), h = Math.Max(1, (int)Math.Round(b.Height * sc));
            Bitmap o = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(o)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(b, 0, 0, w, h); }
            return o;
        }

        static Bitmap OnWhite(Bitmap b)
        {
            Bitmap o = new Bitmap(b.Width, b.Height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(o)) { g.Clear(Color.White); g.DrawImage(b, 0, 0, b.Width, b.Height); }
            return o;
        }

        static void Jpeg(Image b, Stream s, int q)
        {
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)q);
                b.Save(s, jpg, ep);
            }
        }

        /// How many bytes the export would be.
        static long Measure(Bitmap flat, string ext, int q, float sc)
        {
            Bitmap src;
            lock (flat) src = sc < 0.999f ? Scaled(flat, sc) : new Bitmap(flat);
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    if (ext == "png") { src.Save(ms, ImageFormat.Png); return ms.Length; }
                    using (Bitmap w = OnWhite(src)) Jpeg(w, ms, Math.Min(q, 100));
                    if (ext == "pdf") return ms.Length + 1200;
                    if (ext == "webp")
                    {
                        string dir = Path.Combine(Path.GetTempPath(), "FadiExport_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        try
                        {
                            Directory.CreateDirectory(dir);
                            string a = Path.Combine(dir, "a.png"), b = Path.Combine(dir, "b.webp");
                            src.Save(a, ImageFormat.Png);
                            string o;
                            if (Tools.RunHidden(Tools.Magick, Tools.Quote(a) + " -quality " + q + " " + Tools.Quote(b), 60000, out o) == 0 && File.Exists(b)) return new FileInfo(b).Length;
                        }
                        finally { try { Directory.Delete(dir, true); } catch { } }
                        return ms.Length * 7 / 10;
                    }
                    return ms.Length;
                }
            }
            finally { src.Dispose(); }
        }

        /// Writes the photo out.
        public static void Write(Bitmap flat, string path, string ext, int q, float sc)
        {
            Bitmap src;
            lock (flat) src = sc < 0.999f ? Scaled(flat, sc) : new Bitmap(flat);
            try
            {
                string tmp = path + ".part";
                if (ext == "png") src.Save(tmp, ImageFormat.Png);
                else if (ext == "jpg" || ext == "jpeg") using (Bitmap w = OnWhite(src)) using (FileStream fs = new FileStream(tmp, FileMode.Create)) Jpeg(w, fs, q);
                else if (ext == "pdf") { using (Bitmap w = OnWhite(src)) ImagePdf.Write(new List<Bitmap> { w }, tmp, q); }
                else if (ext == "webp")
                {
                    if (Tools.Magick == null) throw new UserError(L.T("err_need_im"));
                    string png = path + ".src.png";
                    src.Save(png, ImageFormat.Png);
                    string o;
                    int code = Tools.RunHidden(Tools.Magick, Tools.Quote(png) + " -quality " + q + " " + Tools.Quote("webp:" + tmp), 120000, out o);
                    try { File.Delete(png); } catch { }
                    if (code != 0 || !File.Exists(tmp)) throw new UserError(Tools.LastLines(o, 1));
                }
                else src.Save(tmp, ImageFormat.Png);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            finally { src.Dispose(); }
        }

        protected override void LayoutExtra(int x, ref int y, int w)
        {
            estR = new Rectangle(x, y, w, Theme.S(26)); y += Theme.S(28);
            noteR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(22);
        }
        protected override void PaintExtra(Graphics g)
        {
            int W = ClientSize.Width;
            Rectangle r = Theme.Mirror(estR, W);
            Theme.FillRound(g, r, Theme.S(8), Theme.CardInner);
            Theme.Text(g, estimate, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(12), r.Y, r.Width - Theme.S(24), r.Height), Align.Start, true, false, true);
            string note = alpha && Ext != "png" && Ext != "webp" ? L.T("ps_alpha_white") : alpha ? L.T("ps_alpha_kept") : "";
            if (note.Length > 0) Theme.Text(g, note, Theme.F(8.5f), Theme.InkSoft, Theme.Mirror(noteR, W), Align.Start);
        }
        protected override void Dispose(bool disposing) { if (disposing) { wait.Stop(); wait.Dispose(); token++; } base.Dispose(disposing); }
    }

    /// A blank canvas: a size (or a ready-made one) and what it starts filled with.
    public class NewCanvasForm : FormDialog
    {
        readonly ChipGrid preset = new ChipGrid { Columns = 3, RowH = 34 };
        readonly NumBox w = new NumBox(() => "W"), h = new NumBox(() => "H");
        readonly Panel sizeRow = new Panel();
        readonly Segments bg;
        readonly Color paint;
        public int W { get { return Math.Max(1, Math.Min(20000, (int)Math.Round(w.Value))); } }
        public int H { get { return Math.Max(1, Math.Min(20000, (int)Math.Round(h.Value))); } }
        public Color Background { get { return bg.SelectedIndex == 1 ? Color.Transparent : bg.SelectedIndex == 2 ? paint : Color.White; } }

        public NewCanvasForm(Color paint) : base(L.T("ps_new_canvas"), null, L.T("ps_create"))
        {
            this.paint = paint;
            List<string> keys = new List<string>();
            foreach (CanvasTiles.Preset p in CanvasTiles.Presets) keys.Add(p.Key);
            preset.LabelOf = k => L.T(k);
            preset.SetItems(keys, null);
            preset.ClearSelection();
            preset.SelectedChanged += (s, e) =>
            {
                foreach (CanvasTiles.Preset p in CanvasTiles.Presets)
                    if (p.Key == preset.Selected) { w.SetValue(p.W); h.SetValue(p.H); if (p.Clear) bg.SelectedIndex = 1; }
            };
            int lw = 1920, lh = 1080;
            int.TryParse(Settings.Get("PsNewW", "1920"), out lw); int.TryParse(Settings.Get("PsNewH", "1080"), out lh);
            w.SetValue(lw); h.SetValue(lh);
            w.Increment = h.Increment = 10;
            sizeRow.BackColor = Theme.Card;
            sizeRow.Controls.Add(w); sizeRow.Controls.Add(h);
            sizeRow.Resize += (s, e) =>
            {
                int gap = Theme.S(10), cw = (sizeRow.Width - gap) / 2;
                w.Bounds = Theme.Mirror(new Rectangle(0, 0, cw, sizeRow.Height), sizeRow.Width);
                h.Bounds = Theme.Mirror(new Rectangle(cw + gap, 0, sizeRow.Width - cw - gap, sizeRow.Height), sizeRow.Width);
            };
            w.Surface = h.Surface = Surface.Card;
            bg = new Segments(() => L.T("ps_bg_white"), () => L.T("ps_bg_clear"), () => L.T("ps_bg_paint")) { FontSize = 8.75f };
            Add(() => L.T("ps_presets"), preset, 34 * 2 + 8);
            Add(() => L.T("ps_size_px"), sizeRow, 34);
            Add(() => L.T("ps_fill_with"), bg, 34);
            Validate2 = () => { Settings.Set("PsNewW", W.ToString()); Settings.Set("PsNewH", H.ToString()); return true; };
        }
    }

    /// Image size: the whole photo bigger or smaller (everything on it with it).
    public class ImageSizeForm : FormDialog
    {
        readonly NumBox w = new NumBox(() => "W"), h = new NumBox(() => "H");
        readonly Panel sizeRow = new Panel();
        readonly ChipGrid pct = new ChipGrid { Columns = 5, RowH = 32 };
        readonly int w0, h0;
        bool busy;
        public int W { get { return Math.Max(1, Math.Min(20000, (int)Math.Round(w.Value))); } }
        public int H { get { return Math.Max(1, Math.Min(20000, (int)Math.Round(h.Value))); } }

        public ImageSizeForm(int width, int height) : base(L.T("ps_image_size"), L.T("ps_image_size_sub", width + " × " + height), L.T("ed_apply_size"))
        {
            w0 = width; h0 = height;
            w.SetValue(width); h.SetValue(height);
            w.Increment = h.Increment = 10;
            // the shape is kept: one side follows the other
            w.Committed += v => { if (busy) return; busy = true; h.SetValue((float)Math.Round(v * h0 / (double)w0)); busy = false; };
            h.Committed += v => { if (busy) return; busy = true; w.SetValue((float)Math.Round(v * w0 / (double)h0)); busy = false; };
            sizeRow.BackColor = Theme.Card;
            sizeRow.Controls.Add(w); sizeRow.Controls.Add(h);
            sizeRow.Resize += (s, e) =>
            {
                int gap = Theme.S(10), cw = (sizeRow.Width - gap) / 2;
                w.Bounds = Theme.Mirror(new Rectangle(0, 0, cw, sizeRow.Height), sizeRow.Width);
                h.Bounds = Theme.Mirror(new Rectangle(cw + gap, 0, sizeRow.Width - cw - gap, sizeRow.Height), sizeRow.Width);
            };
            w.Surface = h.Surface = Surface.Card;
            pct.LabelOf = k => k;
            pct.SetItems(new[] { "25%", "50%", "75%", "150%", "200%" }, null);
            pct.ClearSelection();
            pct.SelectedChanged += (s, e) =>
            {
                float k = int.Parse(pct.Selected.TrimEnd('%')) / 100f;
                busy = true; w.SetValue((float)Math.Round(w0 * k)); h.SetValue((float)Math.Round(h0 * k)); busy = false;
            };
            Add(() => L.T("ps_size_px"), sizeRow, 34);
            Add(() => L.T("ps_quick_sizes"), pct, 32);
        }
    }
}
