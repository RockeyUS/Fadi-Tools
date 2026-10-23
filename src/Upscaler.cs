using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace FileConverter
{
    /// AI Scale: makes a picture 2×, 3× or 4× bigger with Real-ESRGAN, which redraws the detail instead of just
    /// stretching the pixels. Its engine (about 45 MB, it uses the graphics card) is downloaded the first time.
    public static class Upscaler
    {
        const string Url = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip";
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static string Dir { get { return Path.Combine(Paths.ToolsDir, "realesrgan"); } }
        static string ExeName { get { return IsWindows ? "realesrgan-ncnn-vulkan.exe" : "realesrgan-ncnn-vulkan"; } }

        public static string Exe
        {
            get
            {
                try
                {
                    if (Directory.Exists(Dir))
                        foreach (string f in Directory.GetFiles(Dir, ExeName, SearchOption.AllDirectories)) return f;
                }
                catch { }
                return Tools.FindOnPath(ExeName);
            }
        }
        public static bool Ready { get { return Exe != null; } }

        /// Downloads and unpacks the engine (the program and its models).
        public static void Install(Action<long, long> progress, Func<bool> cancelled)
        {
            string zip = Path.Combine(Path.GetTempPath(), "fadi_esrgan_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Net.Download(Url, zip, progress, cancelled);
                string tmp = Dir + ".new";
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                Directory.CreateDirectory(tmp);
                using (ZipArchive za = ZipFile.OpenRead(zip))
                    foreach (ZipArchiveEntry e in za.Entries)
                    {
                        if (e.Name.Length == 0) continue;
                        // the program next to a "models" folder, whatever folder the zip wraps them in
                        string n = e.FullName.Replace('\\', '/');
                        int mi = n.IndexOf("models/", StringComparison.OrdinalIgnoreCase);
                        string rel = mi >= 0 ? n.Substring(mi) : e.Name;
                        if (rel.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) continue;   // the sample pictures
                        string dest = Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                if (!File.Exists(Path.Combine(tmp, "realesrgan-ncnn-vulkan.exe"))) throw new UserError(L.T("up_dl_bad"));
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
                Directory.Move(tmp, Dir);
            }
            finally { try { File.Delete(zip); } catch { } }
        }

        public static long SizeOnDisk
        {
            get
            {
                long n = 0;
                try { if (Directory.Exists(Dir)) foreach (string f in Directory.GetFiles(Dir, "*", SearchOption.AllDirectories)) n += new FileInfo(f).Length; } catch { }
                return n;
            }
        }

        public static void Uninstall() { try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { } }

        /// Upscales a picture into dst (scale 2, 3 or 4). drawing: the model made for drawings and anime.
        /// Returns null when it worked, otherwise a readable reason.
        public static string Run(string input, string dst, int scale, bool drawing, Action<double> progress, Func<bool> cancelled)
        {
            string exe = Exe;
            if (exe == null) return L.T("up_need");
            string work = Path.Combine(Path.GetTempPath(), "fadi_up_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                // the engine reads JPEG, PNG and WebP: anything else becomes a PNG first
                string e = Formats.Ext(input), src = input;
                if (e != "png" && e != "jpg" && e != "jpeg" && e != "webp")
                {
                    src = Path.Combine(work, "in.png");
                    using (Image im = ViewerPage.Decode(input, 16000)) im.Save(src, ImageFormat.Png);
                }
                string raw = Path.Combine(work, "out.png");
                // the photo model only works at 4×; 2× and 3× are made from it, scaled down smoothly
                string model = drawing ? "realesr-animevideov3" : "realesrgan-x4plus";
                int s = drawing ? scale : 4;
                string args = "-i " + Tools.Quote(src) + " -o " + Tools.Quote(raw) + " -n " + model + " -s " + s + " -f png";
                string err = Exec(exe, args, progress, cancelled);
                if (err != null && cancelled != null && cancelled()) return L.T("cancelled");
                if (err != null || !File.Exists(raw))
                {
                    // no graphics card it can use: try the processor (slow, but it works)
                    string err2 = Exec(exe, args + " -g -1", progress, cancelled);
                    if (err2 != null || !File.Exists(raw)) return Friendly(err ?? err2);
                }
                Finish(raw, input, dst, scale, s);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        static string Exec(string exe, string args, Action<double> progress, Func<bool> cancelled)
        {
            System.Text.StringBuilder log = new System.Text.StringBuilder();
            Action<string> line = l =>
            {
                lock (log) log.AppendLine(l);
                Match m = Regex.Match(l, @"(\d+(?:[.,]\d+)?)%");
                double v;
                if (m.Success && progress != null && double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) progress(Math.Min(1, v / 100.0));
            };
            int code = Tools.Stream(exe, args, line, line, cancelled);
            if (code == 0) return null;
            return code == -2 ? L.T("cancelled") : Tools.LastLines(log.ToString(), 2);
        }

        static string Friendly(string err)
        {
            if (err != null && (err.IndexOf("vkCreateInstance", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("gpu", StringComparison.OrdinalIgnoreCase) >= 0 || err.IndexOf("vulkan", StringComparison.OrdinalIgnoreCase) >= 0))
                return L.T("up_no_gpu");
            return string.IsNullOrEmpty(err) ? L.T("up_failed") : err;
        }

        /// From the engine's PNG to the file you get: scaled down when 2× or 3× was made at 4×, and in the
        /// picture's own format (JPEG stays JPEG; formats Windows can't write go through the image engine, or become PNG).
        static void Finish(string raw, string input, string dst, int want, int made)
        {
            string outExt = Formats.Ext(dst);
            string magick = Tools.Magick;
            if (magick != null)
            {
                string resize = want != made ? " -filter Lanczos -resize " + (100.0 * want / made).ToString("0.####", CultureInfo.InvariantCulture) + "%" : "";
                string q = outExt == "jpg" || outExt == "jpeg" ? " -quality 95" : outExt == "webp" || outExt == "avif" || outExt == "heic" ? " -quality 92" : "";
                string o;
                int code = Tools.RunHidden(magick, Tools.Quote(raw) + resize + q + " " + Tools.Quote(dst), 300000, out o);
                if (code == 0 && File.Exists(dst)) return;
            }
            using (Image im = Image.FromFile(raw))
            {
                Bitmap b = null;
                try
                {
                    Image use = im;
                    if (want != made)
                    {
                        int w = Math.Max(1, im.Width * want / made), h = Math.Max(1, im.Height * want / made);
                        b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(im, 0, 0, w, h); }
                        use = b;
                    }
                    if (outExt == "jpg" || outExt == "jpeg") Compressor.SaveJpeg(use, dst, 95);
                    else use.Save(dst, ImageFormat.Png);
                }
                finally { if (b != null) b.Dispose(); }
            }
        }
    }
}
