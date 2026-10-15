using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FileConverter
{
    /// Small pictures of files for the lists: Windows' own thumbnails first (photos, videos, Office files…),
    /// then our engines (any picture format, a frame from a video, the first page of a PDF).
    /// Made one at a time in the background and kept in memory.
    public static class Thumbs
    {
        public const int Size = 320;
        static readonly Dictionary<string, Image> made = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly List<KeyValuePair<string, Action>> queue = new List<KeyValuePair<string, Action>>();
        static readonly object lk = new object();
        static Thread worker;

        static string Key(string path)
        {
            try { return path + "|" + File.GetLastWriteTimeUtc(path).Ticks; } catch { return path; }
        }

        /// The picture, or null while it's being made (ready is called on the worker thread when it is) or when there's none.
        public static Image Get(string path, Action ready)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string k = Key(path);
            lock (lk)
            {
                Image im;
                if (made.TryGetValue(k, out im)) return im;
                if (failed.Contains(k)) return null;
                foreach (KeyValuePair<string, Action> q in queue) if (q.Key == path) return null;
                queue.Add(new KeyValuePair<string, Action>(path, ready));
                if (worker == null || !worker.IsAlive)
                {
                    worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    try { worker.SetApartmentState(ApartmentState.STA); } catch { }
                    worker.Start();
                }
            }
            return null;
        }

        /// True once we know a file has no picture (draw its icon instead of waiting).
        public static bool None(string path) { lock (lk) return failed.Contains(Key(path)); }

        static void Work()
        {
            while (true)
            {
                KeyValuePair<string, Action> job;
                lock (lk)
                {
                    if (queue.Count == 0) { worker = null; return; }
                    job = queue[queue.Count - 1];   // newest first: what was just added is what's on screen
                    queue.RemoveAt(queue.Count - 1);
                }
                if (Tools.ShuttingDown) return;
                Image im = null;
                try { im = Make(job.Key); } catch { im = null; }
                lock (lk)
                {
                    string k = Key(job.Key);
                    if (im != null) made[k] = im; else failed.Add(k);
                    // keep memory in check on very long lists
                    if (made.Count > 400) made.Clear();   // (not disposed: the screen may be drawing one right now)
                }
                try { if (job.Value != null) job.Value(); } catch { }
            }
        }

        static Image Make(string path)
        {
            if (!File.Exists(path)) return null;
            string ext = Formats.Ext(path);
            Category cat = Formats.CategoryOf(ext);
            // pictures that may be see-through: our own reader keeps the transparency
            bool clear = ext == "png" || ext == "gif" || ext == "bmp" || ext == "ico" || ext == "webp" || ext == "svg";
            if (!clear) { Image s = Shell(path); if (s != null) return s; }
            if (cat == Category.Image)
                using (Image im = ViewerPage.Decode(path, Size)) return Fit(im);
            if (cat == Category.Video) return VideoFrame(path);
            if (ext == "pdf") return PdfPage(path);
            return null;
        }

        /// A copy no bigger than Size on its longest side.
        static Image Fit(Image im)
        {
            float k = Math.Min(1f, Size / (float)Math.Max(im.Width, im.Height));
            int w = Math.Max(1, (int)(im.Width * k)), h = Math.Max(1, (int)(im.Height * k));
            Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(im, 0, 0, w, h);
            }
            return b;
        }

        static Image FromFile(string f)
        {
            if (!File.Exists(f)) return null;
            byte[] bytes = File.ReadAllBytes(f);
            using (Image im = Image.FromStream(new MemoryStream(bytes))) return Fit(im);
        }

        static Image VideoFrame(string path)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) return null;
            double at = 1;
            try { MediaInfo mi = Media.Probe(path); if (mi != null && mi.Duration > 0) at = Math.Min(mi.Duration * 0.15, 30); if (mi != null && !mi.HasVideo) return null; } catch { }
            string png = Path.Combine(Path.GetTempPath(), "fadi_thumb_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".png");
            try
            {
                string o;
                Tools.RunHidden(ff, "-hide_banner -loglevel error -y -ss " + at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " -i " + Tools.Quote(path) +
                                    " -frames:v 1 -vf \"scale='min(" + Size + ",iw)':-2\" " + Tools.Quote(png), 30000, out o);
                return FromFile(png);
            }
            finally { try { File.Delete(png); } catch { } }
        }

        static Image PdfPage(string pdf)
        {
            string dir = Path.Combine(Path.GetTempPath(), "fadi_thumb_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            try
            {
                int got = 0;
                DocPages.RenderTo(pdf, dir, Size, (done, total) => got = Math.Max(got, done), () => got >= 1);
                foreach (string f in Directory.GetFiles(dir, "*.png")) return FromFile(f);
                return null;
            }
            catch { return null; }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ------------------------------------------------------------------ Windows' own thumbnails
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        static Image Shell(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            IShellItemImageFactory f = null;
            IntPtr h = IntPtr.Zero;
            try
            {
                if (SHCreateItemFromParsingName(Path.GetFullPath(path), IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f) != 0 || f == null) return null;
                // 0x8 = a real thumbnail only (no generic icon), 0x1 = a bigger one is fine
                if (f.GetImage(new SIZE { cx = Size, cy = Size }, 0x8 | 0x1, out h) != 0 || h == IntPtr.Zero) return null;
                using (Bitmap b = Image.FromHbitmap(h)) return Fit(b);
            }
            catch { return null; }
            finally
            {
                if (h != IntPtr.Zero) DeleteObject(h);
                if (f != null) try { Marshal.ReleaseComObject(f); } catch { }
            }
        }

        /// Draws a file's picture filling a rounded box (cropped to fit), or returns false when there isn't one (yet).
        public static bool Draw(Graphics g, string path, Rectangle box, float radius, Action repaint)
        {
            Image im = Get(path, repaint);
            if (im == null) return false;
            GraphicsState st = g.Save();
            using (GraphicsPath gp = Theme.Round(box, radius))
            {
                g.SetClip(gp, CombineMode.Intersect);
                float sc = Math.Max(box.Width / (float)im.Width, box.Height / (float)im.Height);
                float w = im.Width * sc, h = im.Height * sc;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(im, box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            }
            g.Restore(st);
            return true;
        }
    }
}
