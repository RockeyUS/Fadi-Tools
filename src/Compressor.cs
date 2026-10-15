using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace FileConverter
{
    /// Finds a video encoder on the graphics card (NVIDIA, Intel or AMD): many times faster than the CPU.
    public static class HwEnc
    {
        static string found; static bool done;
        static readonly object lk = new object();

        /// "h264_nvenc", "h264_qsv", "h264_amf" or "" (none). Tested once with a tiny encode, then remembered.
        public static string Detect()
        {
            lock (lk)
            {
                if (done) return found;
                found = "";
                string ff = Tools.FFmpeg;
                if (ff == null || Environment.GetEnvironmentVariable("FADI_NO_GPU") != null) { done = true; return found; }
                string key = ff + "|" + new FileInfo(ff).Length;
                string saved = Settings.Get("HwEnc", "");
                if (saved.StartsWith(key + "=")) { found = saved.Substring(key.Length + 1); done = true; return found; }
                foreach (string enc in new[] { "h264_nvenc", "h264_qsv", "h264_amf" })
                {
                    string o; int code = -1;
                    try { code = Tools.RunHidden(ff, "-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=30 -frames:v 10 -c:v " + enc + " -f null -", 20000, out o); } catch { }
                    if (code == 0) { found = enc; break; }
                }
                Settings.Set("HwEnc", key + "=" + found);
                done = true;
                return found;
            }
        }

        /// Called when the graphics card failed mid-way: use the CPU from now on (this session).
        public static void Disable() { lock (lk) { found = ""; done = true; } }

        public static string Friendly(string enc)
        {
            if (enc.Contains("nvenc")) return "NVIDIA";
            if (enc.Contains("qsv")) return "Intel";
            if (enc.Contains("amf")) return "AMD";
            return "";
        }
    }

    /// Makes files smaller while keeping their resolution (and, where possible, their format).
    public static class Compressor
    {
        public enum Level { Light, Balanced, Strong }

        static readonly string[] JpegExt = { "jpg", "jpeg", "jfif", "jpe" };
        static readonly string[] OfficeExt = { "docx", "xlsx", "pptx", "odt", "ods", "odp", "epub" };
        static readonly string[] Packed = { "zip", "rar", "7z", "gz", "xz", "bz2", "cab", "msi", "apk", "jar" };

        static string Q(string s) { return Tools.Quote(s); }
        static bool In(string[] a, string v) { return Array.IndexOf(a, v) >= 0; }
        static int JpegQ(Level l) { return l == Level.Light ? 85 : l == Level.Balanced ? 75 : 62; }

        public static string OutName(string input, string ext) { return Path.GetFileNameWithoutExtension(input) + " " + L.T("cmp_suffix") + "." + ext; }

        /// The extension the smaller file will have (usually the same as before).
        public static string OutExt(string input, double targetMb = 0)
        {
            string e = Formats.Ext(input);
            // lossless audio can't aim for a size: with a target it becomes MP3
            if (targetMb > 0 && Formats.CategoryOf(e) == Category.Audio && (e == "wav" || e == "aiff" || e == "aif" || e == "flac")) return "mp3";
            Category c = Formats.CategoryOf(e);
            if (In(JpegExt, e) || e == "png" || e == "webp" || e == "gif" || e == "heic" || e == "heif" || e == "avif" || e == "tif" || e == "tiff") return e;
            if (c == Category.Video) return e == "mov" || e == "mkv" || e == "mp4" ? e : "mp4";
            if (c == Category.Audio) return e == "wav" || e == "aiff" || e == "aif" ? "flac" : (e == "mp3" || e == "m4a" || e == "aac" || e == "ogg" || e == "opus" || e == "flac" || e == "wma" ? e : "mp3");
            if (e == "pdf" || In(OfficeExt, e)) return e;
            return "zip";
        }

        /// Builds the steps that write a smaller copy of input into outDir. targetMb > 0 = make videos fit that size.
        public static Job Build(string input, string outDir, Level level, double targetMb) { return Build(input, outDir, level, targetMb, true); }

        public static Job Build(string input, string outDir, Level level, double targetMb, bool gpu) { return Build(input, outDir, level, targetMb, gpu, 0); }

        /// For a size limit: the biggest standard picture height that still gets enough bits per pixel to look like
        /// something (0 = full resolution is fine). Below ~0.009 bits per pixel the encoder can't hit the size anyway.
        public static int SuggestHeight(MediaInfo mi, double targetMb)
        {
            if (mi == null || !mi.HasVideo || mi.Duration <= 0 || targetMb <= 0 || mi.Width <= 0 || mi.Height <= 0) return 0;
            double per = targetMb * 8192 * 0.95 / mi.Duration;
            int aK = per >= 1200 ? 128 : per >= 600 ? 96 : per >= 300 ? 64 : 48;
            double vbps = Math.Max(30, per - (mi.HasAudio ? aK : 0)) * 1000;
            double fps = mi.Fps > 1 ? Math.Min(60, mi.Fps) : 30;
            const double minBpp = 0.009;
            int shortSide = Math.Min(mi.Width, mi.Height), longSide = Math.Max(mi.Width, mi.Height);
            if (vbps / (mi.Width * (double)mi.Height * fps) >= minBpp) return 0;
            foreach (int h in new[] { 1080, 720, 540, 480, 360, 240 })
            {
                if (h >= shortSide) continue;
                double w = longSide * h / (double)shortSide;
                if (vbps / (w * h * fps) >= minBpp) return h;
            }
            return 240;
        }

        /// maxHeight > 0: a smaller picture (only used when a size limit can't be reached at full resolution).
        public static Job Build(string input, string outDir, Level level, double targetMb, bool gpu, int maxHeight)
        {
            string e = Formats.Ext(input);
            Category cat = Formats.CategoryOf(e);
            if (In(Packed, e)) throw new UserError(L.T("cmp_packed"));
            string ext = OutExt(input, targetMb);
            Job job = new Job();
            job.Output = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(OutName(input, ext)), ext);
            string outp = job.Output;

            // ---------- pictures
            if (cat == Category.Image && ext != "zip")
            {
                string im = Tools.Magick;
                if (targetMb > 0)
                {
                    // a size limit (an avatar under 1 MB…): it wins over the level
                    long want = (long)(CapBytes(input, targetMb) * 0.98);
                    if (im != null) { job.Steps.Add(new JobStep { Native = () => job.Note = ImageToSize(im, input, outp, e, want) }); return job; }
                    if (In(JpegExt, e)) { job.Steps.Add(new JobStep { Native = () => JpegToSize(input, outp, want, JpegQ(level)) }); return job; }
                }
                if (In(JpegExt, e))
                {
                    if (im != null)
                        job.Steps.Add(new JobStep { Exe = im, Args = Q(input) + " -auto-orient -strip -sampling-factor 4:2:0 -interlace JPEG -quality " + JpegQ(level) + " " + Q(outp) });
                    else job.Steps.Add(new JobStep { Native = () => JpegNative(input, outp, JpegQ(level)) });
                    return job;
                }
                if (im == null) throw new UserError(L.T("err_need_im"));
                string ops;
                switch (e)
                {
                    case "png":
                        // light: lossless squeeze · balanced/strong: fewer colors, like TinyPNG
                        ops = "-strip -define png:compression-level=9 -define png:compression-filter=5 -define png:compression-strategy=1";
                        if (level == Level.Balanced) ops = "-strip -dither FloydSteinberg -colors 256 -define png:compression-level=9";
                        if (level == Level.Strong) ops = "-strip -dither FloydSteinberg -colors 128 -define png:compression-level=9";
                        break;
                    case "webp": ops = "-strip -quality " + (level == Level.Light ? 80 : level == Level.Balanced ? 70 : 55) + " -define webp:method=6"; break;
                    case "gif": ops = level == Level.Light ? "-layers Optimize" : level == Level.Balanced ? "-fuzz 3% -layers Optimize" : "-fuzz 6% -colors 128 -layers Optimize"; break;
                    case "heic": case "heif": case "avif": ops = "-strip -quality " + (level == Level.Light ? 70 : level == Level.Balanced ? 55 : 42); break;
                    default: ops = "-strip -compress zip"; break;   // tiff
                }
                string src = e == "gif" ? Q(input) : Q(input + "[0]");
                if (e == "gif") job.Steps.Add(new JobStep { Exe = im, Args = src + " -coalesce " + ops + " " + Q(outp) });
                else job.Steps.Add(new JobStep { Exe = im, Args = src + " -auto-orient " + ops + " " + Q(outp) });
                return job;
            }

            // ---------- video: same size and frame rate; the graphics card does the work when it can
            if (cat == Category.Video)
            {
                string ff = Tools.FFmpeg; if (ff == null) throw new UserError(L.T("err_need_ff"));
                MediaInfo mi = null; try { mi = Media.Probe(input); } catch { }
                string ab = level == Level.Light ? "160k" : level == Level.Balanced ? "128k" : "96k";
                string fast = ext == "mp4" || ext == "mov" ? " -movflags +faststart" : "";
                string enc = gpu ? HwEnc.Detect() : "";
                int li = (int)level;
                string v;
                bool target = targetMb > 0 && mi != null && mi.Duration > 0;
                if (target)
                {
                    double totalKbit = targetMb * 8192 * 0.95;
                    // tight budgets: less for the sound, more for the picture
                    double per = totalKbit / mi.Duration;
                    int aK = per >= 1200 ? 128 : per >= 600 ? 96 : per >= 300 ? 64 : 48;
                    double aKbit = mi.HasAudio ? aK : 0;
                    int vb = (int)(totalKbit / mi.Duration - aKbit);
                    if (vb < 60) throw new UserError(L.T("cmp_too_small", Theme.HumanSize((long)((60 + aKbit) * mi.Duration / 8 * 1024))));
                    ab = aK + "k";
                    string rate = " -b:v " + vb + "k -maxrate " + (vb * 3 / 2) + "k -bufsize " + (vb * 2) + "k";
                    if (enc == "h264_nvenc") v = "-c:v h264_nvenc -preset p5 -rc vbr" + rate + " -spatial-aq 1 -pix_fmt yuv420p";
                    else if (enc == "h264_qsv") v = "-c:v h264_qsv -preset medium" + rate + " -pix_fmt nv12";
                    else if (enc == "h264_amf") v = "-c:v h264_amf -quality balanced -rc vbr_peak" + rate + " -pix_fmt yuv420p";
                    else v = "-c:v libx264 -preset faster -b:v " + vb + "k -maxrate " + (vb * 7 / 5) + "k -bufsize " + (vb * 2) + "k -pix_fmt yuv420p";
                }
                else
                {
                    int q = new[] { 24, 28, 32 }[li];
                    if (enc == "h264_nvenc") v = "-c:v h264_nvenc -preset p5 -rc vbr -cq " + q + " -b:v 0 -spatial-aq 1 -pix_fmt yuv420p";
                    else if (enc == "h264_qsv") v = "-c:v h264_qsv -preset medium -global_quality " + q + " -pix_fmt nv12";
                    else if (enc == "h264_amf") v = "-c:v h264_amf -quality balanced -rc cqp -qp_i " + (q - 2) + " -qp_p " + q + " -qp_b " + (q + 2) + " -pix_fmt yuv420p";
                    else v = "-c:v libx264 -preset veryfast -crf " + (q - 1) + " -pix_fmt yuv420p";
                }
                string audio = mi != null && !mi.HasAudio ? "-an" : "-c:a aac -b:a " + ab;
                string hwIn = enc != "" ? "-hwaccel auto " : "";
                string scale = maxHeight > 0 && mi != null && Math.Min(mi.Width, mi.Height) > maxHeight
                    ? " -vf \"scale='if(gt(iw,ih),-2," + maxHeight + ")':'if(gt(iw,ih)," + maxHeight + ",-2)':flags=lanczos\"" : "";
                job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = "-hide_banner -y " + hwIn + "-i " + Q(input) + " -map 0:v:0 -map 0:a:0?" + scale + " " + v + " " + audio + fast + " " + Q(outp) });
                if (enc != "")
                {
                    job.Encoder = HwEnc.Friendly(enc);
                    job.Fallback = () => { HwEnc.Disable(); return Build(input, outDir, level, targetMb, false, maxHeight); };
                }
                return job;
            }

            // ---------- audio
            if (cat == Category.Audio)
            {
                string ff = Tools.FFmpeg; if (ff == null) throw new UserError(L.T("err_need_ff"));
                string codec;
                int lv = (int)level;
                MediaInfo ami = null;
                if (targetMb > 0) try { ami = Media.Probe(input); } catch { }
                if (ami != null && ami.Duration > 0)
                {
                    int kb = (int)Math.Max(32, Math.Min(320, targetMb * 8192 * 0.96 / ami.Duration));
                    string br = kb + "k";
                    switch (ext)
                    {
                        case "m4a": case "aac": codec = "-c:a aac -b:a " + br; break;
                        case "ogg": codec = "-c:a libvorbis -b:a " + br; break;
                        case "opus": codec = "-c:a libopus -b:a " + br; break;
                        case "wma": codec = "-c:a wmav2 -b:a " + br; break;
                        default: codec = "-c:a libmp3lame -b:a " + br; break;
                    }
                    job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = "-hide_banner -y -i " + Q(input) + " -vn -map 0:a:0 -map_metadata 0 " + codec + " " + Q(outp) });
                    return job;
                }
                switch (ext)
                {
                    case "flac": codec = "-c:a flac -compression_level 12"; break;
                    case "m4a": case "aac": codec = "-c:a aac -b:a " + new[] { "160k", "128k", "96k" }[lv]; break;
                    case "ogg": codec = "-c:a libvorbis -q:a " + new[] { "5", "3", "1" }[lv]; break;
                    case "opus": codec = "-c:a libopus -b:a " + new[] { "128k", "96k", "64k" }[lv]; break;
                    case "wma": codec = "-c:a wmav2 -b:a " + new[] { "160k", "128k", "96k" }[lv]; break;
                    default: codec = "-c:a libmp3lame -b:a " + new[] { "192k", "128k", "96k" }[lv]; break;
                }
                job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = "-hide_banner -y -i " + Q(input) + " -vn -map 0:a:0 -map_metadata 0 " + codec + " " + Q(outp) });
                return job;
            }

            // ---------- documents
            if (e == "pdf")
            {
                long want = targetMb > 0 ? (long)(targetMb * 1024 * 1024) : 0;
                job.Steps.Add(new JobStep
                {
                    Native = () =>
                    {
                        PdfShrink.Shrink(input, outp, JpegQ(level));
                        if (want <= 0) return;
                        // too big still: lower the picture quality step by step until it fits
                        foreach (int q in new[] { 60, 48, 36, 26 })
                        {
                            if (new FileInfo(outp).Length <= want) return;
                            if (q < JpegQ(level)) PdfShrink.Shrink(input, outp, q);
                        }
                    }
                });
                return job;
            }
            if (In(OfficeExt, e)) { job.Steps.Add(new JobStep { Native = () => Office(input, outp, level) }); return job; }

            // ---------- anything else: a .zip
            job.Steps.Add(new JobStep
            {
                Native = () =>
                {
                    using (FileStream fs = new FileStream(outp, FileMode.Create))
                    using (ZipArchive z = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        ZipArchiveEntry en = z.CreateEntry(Path.GetFileName(input), CompressionLevel.Optimal);
                        using (Stream w = en.Open()) using (FileStream r = File.OpenRead(input)) r.CopyTo(w);
                    }
                }
            });
            return job;
        }

        /// The size limit in bytes. For pictures "1 MB" counts as 1,000,000 bytes: websites differ on what a megabyte is,
        /// and this fits both meanings (GitHub, Discord… avatars).
        public static long CapBytes(string path, double mb)
        {
            return Formats.CategoryOf(Formats.Ext(path)) == Category.Image ? (long)(mb * 1000 * 1000) : (long)(mb * 1024 * 1024);
        }

        /// A picture under a size limit, in the same format: the best quality that fits; when even a low quality is too big,
        /// a smaller picture (step by step). Returns the new width × height when it had to shrink, otherwise null.
        public static string ImageToSize(string im, string input, string outp, string e, long want)
        {
            bool jpeg = In(JpegExt, e), gif = e == "gif", png = e == "png";
            bool lossy = jpeg || e == "webp" || e == "heic" || e == "heif" || e == "avif";
            int topQ = jpeg ? 92 : e == "webp" ? 90 : 80, lowQ = jpeg ? 45 : 40;
            string src = gif ? Q(input) : Q(input + "[0]");
            int w0 = 0, h0 = 0;
            try
            {
                string o;
                if (Tools.RunHidden(im, Q(input + "[0]") + " -auto-orient -format \"%w %h\" info:", 60000, out o) == 0)
                {
                    System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(o, @"(\d+) (\d+)");
                    if (m.Success) { w0 = int.Parse(m.Groups[1].Value); h0 = int.Parse(m.Groups[2].Value); }
                }
            }
            catch { }
            string tmp = Path.Combine(Path.GetDirectoryName(outp), "fadi_fit_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "." + e);
            // mode: png 0 = lossless, 1 = 256 colors · gif 0 = light, 1 = fewer colors
            Func<double, int, int, long> enc = (sc, q, mode) =>
            {
                string rs = sc < 0.999 ? " -resize " + (sc * 100).ToString("0.###", CultureInfo.InvariantCulture) + "%" : "";
                string ops;
                if (jpeg) ops = "-sampling-factor 4:2:0 -interlace JPEG -quality " + q;
                else if (e == "webp") ops = "-quality " + q + " -define webp:method=4";
                else if (lossy) ops = "-quality " + q;
                else if (png) ops = mode == 0 ? "-define png:compression-level=9 -define png:compression-filter=5" : "-dither FloydSteinberg -colors 256 -define png:compression-level=9";
                else if (gif) ops = mode == 0 ? "-fuzz 2% -layers Optimize" : "-fuzz 4% -colors 128 -layers Optimize";
                else ops = "-compress zip";
                string args = gif ? src + " -coalesce" + rs + " " + ops + " " + Q(tmp) : src + " -auto-orient -strip" + rs + " " + ops + " " + Q(tmp);
                string o;
                if (Tools.RunHidden(im, args, 10 * 60 * 1000, out o) != 0 || !File.Exists(tmp)) throw new Exception(Tools.LastLines(o, 1));
                return new FileInfo(tmp).Length;
            };
            Action keep = () => { if (File.Exists(outp)) File.Delete(outp); File.Move(tmp, outp); };
            Func<double, string> note = sc => sc < 0.999 && w0 > 0 ? Math.Max(1, (int)Math.Round(w0 * sc)) + " × " + Math.Max(1, (int)Math.Round(h0 * sc)) : null;
            double scale = 1;
            long smallest = long.MaxValue;
            try
            {
                for (int round = 0; round < 8; round++)
                {
                    long last;
                    if (lossy)
                    {
                        if (enc(scale, topQ, 0) <= want) { keep(); return note(scale); }
                        last = enc(scale, lowQ, 0);
                        if (last <= want)
                        {
                            // the best quality in between that still fits
                            int lo = lowQ + 1, hi = topQ - 1, best = lowQ;
                            while (lo <= hi) { int mid = (lo + hi) / 2; if (enc(scale, mid, 0) <= want) { best = mid; lo = mid + 1; } else hi = mid - 1; }
                            enc(scale, best, 0); keep(); return note(scale);
                        }
                    }
                    else
                    {
                        if (round == 0 && !gif)
                        {
                            long full = enc(1, 0, 0);
                            if (full <= want) { keep(); return null; }   // a lossless squeeze is enough
                            // a little over: a slightly smaller picture keeps every color (looks better than 256 colors)
                            double ls = 1;
                            for (int t = 0; t < 2 && full * 0.55 <= want; t++)
                            {
                                ls *= Math.Max(0.6, Math.Min(0.97, Math.Sqrt(want / (double)full) * 0.97));
                                full = enc(ls, 0, 0);
                                if (full <= want) { keep(); return note(ls); }
                            }
                        }
                        last = enc(scale, 0, 1);
                        if (last <= want) { keep(); return note(scale); }
                    }
                    if (last < smallest) { smallest = last; keep(); }   // the closest so far, in case nothing fits
                    // too big even at low quality: a smaller picture, in proportion to how far over it is
                    scale *= Math.Max(0.3, Math.Min(0.92, Math.Sqrt(want / (double)last) * 0.97));
                    if (w0 > 0 && Math.Max(w0, h0) * scale < 48) break;
                }
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            return note(scale);
        }

        /// JPEG without ImageMagick: turns the picture upright, drops camera data, re-encodes.
        public static void JpegNative(string input, string outp, int quality)
        {
            using (Image src = Image.FromFile(input))
            using (Bitmap b = new Bitmap(src))
            {
                try
                {
                    if (Array.IndexOf(src.PropertyIdList, 0x0112) >= 0)
                    {
                        int o = src.GetPropertyItem(0x0112).Value[0];
                        RotateFlipType[] map = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone,
                                                 RotateFlipType.RotateNoneFlipY, RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
                        if (o >= 1 && o <= 8) b.RotateFlip(map[o]);
                    }
                }
                catch { }
                SaveJpeg(b, outp, quality);
            }
        }

        /// Finds the best JPEG quality that still fits under a size (binary search, in memory).
        public static void JpegToSize(string input, string outp, long want, int maxQ)
        {
            using (Image src = Image.FromFile(input))
            using (Bitmap b = new Bitmap(src))
            {
                try
                {
                    if (Array.IndexOf(src.PropertyIdList, 0x0112) >= 0)
                    {
                        int o = src.GetPropertyItem(0x0112).Value[0];
                        RotateFlipType[] map = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone,
                                                 RotateFlipType.RotateNoneFlipY, RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
                        if (o >= 1 && o <= 8) b.RotateFlip(map[o]);
                    }
                }
                catch { }
                ImageCodecInfo jpg = null;
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
                Func<int, byte[]> enc = q =>
                {
                    using (MemoryStream ms = new MemoryStream())
                    using (EncoderParameters ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)q);
                        b.Save(ms, jpg, ep);
                        return ms.ToArray();
                    }
                };
                int lo = 10, hi = Math.Max(lo, Math.Min(95, maxQ + 10));
                byte[] best = enc(lo);
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    byte[] d = enc(mid);
                    if (d.Length <= want) { best = d; lo = mid + 1; } else hi = mid - 1;
                }
                File.WriteAllBytes(outp, best);
            }
        }

        public static void SaveJpeg(Image b, string path, int quality)
        {
            ImageCodecInfo jpg = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                b.Save(path, jpg, ep);
            }
        }

        /// Word / Excel / PowerPoint / OpenDocument: smaller pictures inside, tighter zip.
        static void Office(string input, string outp, Level level)
        {
            int q = JpegQ(level);
            using (ZipArchive src = ZipFile.OpenRead(input))
            using (FileStream fs = new FileStream(outp, FileMode.Create))
            using (ZipArchive dst = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                List<ZipArchiveEntry> entries = new List<ZipArchiveEntry>(src.Entries);
                // OpenDocument wants "mimetype" first and uncompressed
                entries.Sort((a, b) => (a.FullName == "mimetype" ? 0 : 1).CompareTo(b.FullName == "mimetype" ? 0 : 1));
                foreach (ZipArchiveEntry en in entries)
                {
                    byte[] data;
                    using (Stream s = en.Open()) using (MemoryStream ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                    string lower = en.FullName.ToLowerInvariant();
                    bool media = lower.Contains("media/") || lower.StartsWith("pictures/") || lower.Contains("/images/");
                    if (media && (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg")) && data.Length > 20000)
                    {
                        try
                        {
                            using (MemoryStream ms = new MemoryStream(data))
                            using (Image im = Image.FromStream(ms))
                            using (MemoryStream o = new MemoryStream())
                            {
                                if ((im.PixelFormat & PixelFormat.Indexed) == 0 && (int)im.PixelFormat != 0x200F)
                                {
                                    ImageCodecInfo jpg = null;
                                    foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
                                    using (EncoderParameters ep = new EncoderParameters(1))
                                    {
                                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)q);
                                        im.Save(o, jpg, ep);
                                    }
                                    if (o.Length < data.Length * 0.9) data = o.ToArray();
                                }
                            }
                        }
                        catch { }
                    }
                    else if (media && lower.EndsWith(".png") && data.Length > 50000 && Tools.Magick != null && level != Level.Light)
                    {
                        try
                        {
                            string t1 = Path.Combine(Path.GetTempPath(), "fadi_cmp_" + Guid.NewGuid().ToString("N") + ".png"), t2 = t1 + ".out.png";
                            File.WriteAllBytes(t1, data);
                            string o;
                            if (Tools.RunHidden(Tools.Magick, Q(t1) + " -strip -dither FloydSteinberg -colors 256 -define png:compression-level=9 " + Q(t2), 60000, out o) == 0 && File.Exists(t2))
                            {
                                byte[] nd = File.ReadAllBytes(t2);
                                if (nd.Length < data.Length * 0.9) data = nd;
                            }
                            try { File.Delete(t1); File.Delete(t2); } catch { }
                        }
                        catch { }
                    }
                    ZipArchiveEntry ne = dst.CreateEntry(en.FullName, en.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    ne.LastWriteTime = en.LastWriteTime;
                    using (Stream w = ne.Open()) w.Write(data, 0, data.Length);
                }
            }
        }
    }
}
