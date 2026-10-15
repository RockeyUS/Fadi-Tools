using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FileConverter
{
    /// Where results go: ask every time (default), or the saved folder when the user turned asking off.
    public static class SaveAsk
    {
        public static bool Enabled { get { return Settings.Get("AskSave", "1") == "1"; } set { Settings.Set("AskSave", value ? "1" : "0"); } }

        static string StartDir
        {
            get
            {
                string d = Settings.Get("LastSaveDir", "");
                return d.Length > 0 && Directory.Exists(d) ? d : Settings.OutputDir;
            }
        }

        static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>
        {
            { "jpg", "JPG" }, { "jpeg", "JPEG" }, { "png", "PNG" }, { "webp", "WEBP" }, { "pdf", "PDF" }, { "gif", "GIF" }, { "mp4", "MP4" }, { "mp3", "MP3" },
            { "zip", "ZIP" }, { "json", "JSON" }, { "ico", "ICO" }
        };

        /// Asks for a file name (or picks one in the saved folder when asking is off). Returns null when cancelled.
        /// choices: other extensions the user may switch to in the dialog (the first one is the default).
        public static string File(IWin32Window owner, string suggestedName, params string[] choices)
        {
            string ext = choices.Length > 0 ? choices[0] : Path.GetExtension(suggestedName).TrimStart('.');
            string baseName = Path.GetFileNameWithoutExtension(suggestedName);
            if (!Enabled)
            {
                string dir = Settings.OutputDir;
                Directory.CreateDirectory(dir);
                return Paths.UniquePath(dir, baseName, ext);
            }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = L.T("save_where");
                List<string> f = new List<string>();
                foreach (string c in choices.Length > 0 ? choices : new[] { ext })
                {
                    string label; if (!Kinds.TryGetValue(c, out label)) label = c.ToUpperInvariant();
                    f.Add(label + " (*." + c + ")|*." + c);
                }
                f.Add(L.T("all_files") + "|*.*");
                d.Filter = string.Join("|", f.ToArray());
                d.FilterIndex = 1;
                d.DefaultExt = ext;
                d.AddExtension = true;
                d.OverwritePrompt = true;
                d.InitialDirectory = StartDir;
                d.FileName = baseName + "." + ext;
                Form o = owner as Form ?? (owner is Control ? ((Control)owner).FindForm() : null);
                if ((o != null ? d.ShowDialog(o) : d.ShowDialog()) != DialogResult.OK) return null;
                string path = d.FileName;
                // the type picked in the dialog wins when the name has no extension of its own
                if (choices.Length > 1 && d.FilterIndex >= 1 && d.FilterIndex <= choices.Length && Path.GetExtension(path).Length == 0)
                    path += "." + choices[d.FilterIndex - 1];
                try { Settings.Set("LastSaveDir", Path.GetDirectoryName(path)); } catch { }
                return path;
            }
        }

        /// Asks for a folder for several results. Returns null when cancelled.
        public static string Folder(IWin32Window owner)
        {
            if (!Enabled) { string dir = Settings.OutputDir; Directory.CreateDirectory(dir); return dir; }
            string picked = FolderPicker.Pick(owner, L.T("save_where_folder"), StartDir);
            if (picked != null) Settings.Set("LastSaveDir", picked);
            return picked;
        }

        /// A private scratch folder: jobs are built there, then moved to the chosen place.
        public static string Scratch()
        {
            string d = Path.Combine(Path.GetTempPath(), "FadiOut_" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(d);
            return d;
        }

        /// Makes a finished job end up at target (replacing whatever the user agreed to overwrite).
        public static void Redirect(Job job, string target)
        {
            string src = job.Output;
            if (src == null || string.Equals(Path.GetFullPath(src), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            string scratch = Path.GetDirectoryName(src);
            job.Steps.Add(new JobStep
            {
                Native = () =>
                {
                    if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                    System.IO.File.Move(src, target);
                    try { if (Path.GetFileName(scratch).StartsWith("FadiOut_")) Directory.Delete(scratch, true); } catch { }
                },
                Weight = 0.01
            });
            job.Output = target;
        }
    }

    /// Writes pictures as a PDF (one picture per page) — no extra tools needed.
    public static class ImagePdf
    {
        public enum PageSize { Photo, A4, Letter }

        public static void Write(IList<Bitmap> pages, string path, int quality = 90)
        {
            Write(pages.Count, i => pages[i], false, path, quality, PageSize.Photo, null);
        }

        static byte[] Jpeg(Bitmap b, int quality, int maxSide, out Size size)
        {
            ImageCodecInfo codec = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) codec = c;
            int w = b.Width, h = b.Height;
            if (maxSide > 0 && Math.Max(w, h) > maxSide) { double k = maxSide / (double)Math.Max(w, h); w = Math.Max(1, (int)(w * k)); h = Math.Max(1, (int)(h * k)); }
            size = new Size(w, h);
            using (Bitmap flat = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(flat))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(b, 0, 0, w, h);
                }
                using (MemoryStream ms = new MemoryStream())
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    flat.Save(ms, codec, ep);
                    return ms.ToArray();
                }
            }
        }

        /// One picture at a time (so big photo sets don't fill the memory). get(i) returns page i;
        /// dispose = the writer frees each picture after use. A4/Letter: the photo is fitted on the page with a margin.
        public static void Write(int count, Func<int, Bitmap> get, bool dispose, string path, int quality, PageSize size, Action<int, int> progress)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                List<long> offsets = new List<long>();
                Action<string> w = s => { byte[] bb = Encoding.ASCII.GetBytes(s); fs.Write(bb, 0, bb.Length); };
                w("%PDF-1.4\n");
                fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);   // marks the file as binary
                // objects: 1 catalog, 2 pages, then per page: page, image, content
                offsets.Add(fs.Position); w("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                StringBuilder kids = new StringBuilder();
                for (int i = 0; i < count; i++) kids.Append(3 + i * 3).Append(" 0 R ");
                offsets.Add(fs.Position); w("2 0 obj\n<< /Type /Pages /Count " + count + " /Kids [" + kids + "] >>\nendobj\n");
                for (int i = 0; i < count; i++)
                {
                    Bitmap b = get(i);
                    Size px; byte[] jpg;
                    try { jpg = Jpeg(b, quality, size == PageSize.Photo ? 0 : 3508, out px); }
                    finally { if (dispose && b != null) b.Dispose(); }
                    float pw, ph, ix, iy, iw, ih;
                    if (size == PageSize.Photo)
                    {
                        // 96 dpi pictures → points (1/72 inch), but never a page bigger than about 200 inches
                        float k = Math.Min(0.75f, 14400f / Math.Max(px.Width, px.Height));
                        pw = iw = px.Width * k; ph = ih = px.Height * k; ix = iy = 0;
                    }
                    else
                    {
                        float a = size == PageSize.A4 ? 595.28f : 612f, bb = size == PageSize.A4 ? 841.89f : 792f;
                        bool land = px.Width > px.Height;
                        pw = land ? bb : a; ph = land ? a : bb;
                        float m = 28.35f;   // 1 cm margin
                        float k = Math.Min((pw - 2 * m) / px.Width, (ph - 2 * m) / px.Height);
                        iw = px.Width * k; ih = px.Height * k; ix = (pw - iw) / 2; iy = (ph - ih) / 2;
                    }
                    string W = pw.ToString("0.##", ci), H = ph.ToString("0.##", ci);
                    int po = 3 + i * 3, io = po + 1, co = po + 2;
                    offsets.Add(fs.Position);
                    w(po + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + W + " " + H + "] /Resources << /XObject << /Im0 " + io + " 0 R >> >> /Contents " + co + " 0 R >>\nendobj\n");
                    offsets.Add(fs.Position);
                    w(io + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + px.Width + " /Height " + px.Height + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpg.Length + " >>\nstream\n");
                    fs.Write(jpg, 0, jpg.Length);
                    w("\nendstream\nendobj\n");
                    string content = "q " + iw.ToString("0.##", ci) + " 0 0 " + ih.ToString("0.##", ci) + " " + ix.ToString("0.##", ci) + " " + iy.ToString("0.##", ci) + " cm /Im0 Do Q";
                    offsets.Add(fs.Position);
                    w(co + " 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
                    if (progress != null) progress(i + 1, count);
                }
                long xref = fs.Position;
                w("xref\n0 " + (offsets.Count + 1) + "\n0000000000 65535 f \n");
                foreach (long o in offsets) w(o.ToString("D10") + " 00000 n \n");
                w("trailer\n<< /Size " + (offsets.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            }
        }
    }

    /// Open dialogs start in the folder last used on the same page (Convert, Compress, Studio...).
    public static class LastDir
    {
        public static DialogResult Show(FileDialog d, IWin32Window owner, string key)
        {
            string dir = Settings.Get("Dir_" + key, "");
            try { if (dir.Length > 0 && Directory.Exists(dir)) d.InitialDirectory = dir; } catch { }
            DialogResult r = owner != null ? d.ShowDialog(owner) : d.ShowDialog();
            if (r == DialogResult.OK) try { Settings.Set("Dir_" + key, Path.GetDirectoryName(d.FileName)); } catch { }
            return r;
        }
    }
}
