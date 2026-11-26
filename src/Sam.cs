using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileConverter
{
    /// One click in the pick dialog: where (0..1 of the picture's width and height) and whether it is to keep.
    public struct PickPoint
    {
        public float U, V;
        public bool Keep;
        public PickPoint(float u, float v, bool keep) { U = u; V = v; Keep = keep; }
    }

    /// Click-to-pick cut-out: MobileSAM looks at the picture once (the "embedding"), then every click is answered
    /// at once by SAM's small prompt decoder. Videos: the pick follows the thing from frame to frame (each frame is
    /// asked with the last frame's box and outline). About 37 MB, downloaded the first time (asked first), next to the
    /// background remover's AI runtime. Apache-2.0 models: MobileSAM (Chaoning Zhang et al.) and Segment Anything's
    /// decoder (Meta), ONNX exports by vietanhdev (AnyLabeling).
    public static class Sam
    {
        public const string ZipUrl = "https://huggingface.co/vietanhdev/segment-anything-onnx-models/resolve/main/mobile_sam_20230629.zip";
        const long ZipBytes = 36700000;
        const int Side = 1024;     // the encoder looks at the picture with its long side this size

        static string Folder
        {
            get
            {
                string t = Environment.GetEnvironmentVariable("FADI_SAM");
                return !string.IsNullOrEmpty(t) && Directory.Exists(t) ? t : BgRemover.Dir;
            }
        }
        public static string EncoderPath { get { return Path.Combine(Folder, "mobile_sam.encoder.onnx"); } }
        public static string DecoderPath { get { return Path.Combine(Folder, "sam.decoder.onnx"); } }
        static bool Big(string f, long min) { try { return File.Exists(f) && new FileInfo(f).Length > min; } catch { return false; } }
        public static bool Ready { get { return OnnxNet.RuntimeReady && Big(EncoderPath, 5000000) && Big(DecoderPath, 1000000); } }
        /// About how much is still to download (bytes), 0 when ready.
        public static long Needed { get { return Ready ? 0 : ZipBytes + (OnnxNet.RuntimeReady ? 0 : 7 << 20); } }

        /// The runtime (when it isn't here yet) and the two models (one zip). progress: 0..1
        public static void Install(Action<double, string> progress, Func<bool> cancelled)
        {
            BgRemover.InstallRuntime(f => { if (progress != null) progress(0.15 * f, L.T("bg_dl_engine")); }, cancelled);
            if (Ready) return;
            string zip = Path.Combine(BgRemover.Dir, "sam.zip.part");
            Net.Download(ZipUrl, zip, (d, t) =>
            {
                long all = t > 0 ? t : ZipBytes;
                if (progress != null) progress(0.15 + 0.85 * d / (double)all, L.T("pick_dl", Theme.HumanSize(d), Theme.HumanSize(all)));
            }, cancelled);
            try
            {
                using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(zip))
                    foreach (System.IO.Compression.ZipArchiveEntry e in z.Entries)
                    {
                        string n = e.Name.ToLowerInvariant(), to = null;
                        if (n.EndsWith(".encoder.onnx")) to = EncoderPath;
                        else if (n.EndsWith(".decoder.onnx")) to = DecoderPath;
                        if (to == null) continue;
                        using (Stream s = e.Open()) using (FileStream f = File.Create(to + ".part")) s.CopyTo(f);
                        if (File.Exists(to)) File.Delete(to);
                        File.Move(to + ".part", to);
                    }
            }
            finally { try { File.Delete(zip); } catch { } }
            if (!Ready) throw new UserError(L.T("pick_bad_dl"));
        }

        public static void Uninstall()
        {
            foreach (string f in new[] { EncoderPath, DecoderPath }) try { if (File.Exists(f)) File.Delete(f); } catch { }
        }

        static OnnxNet enc, dec;
        static readonly object gate = new object();
        static void Load()
        {
            lock (gate)
            {
                if (enc == null) enc = new OnnxNet(EncoderPath);
                if (dec == null) dec = new OnnxNet(DecoderPath);
            }
        }

        /// What the encoder made of one picture (aw×ah: the picture at the encoder's size).
        public sealed class Look
        {
            public Tensor Emb;
            public int AW, AH;
        }

        /// The encoder's answer for a picture (any size; it is made 1024 px on the long side first).
        public static Look See(Bitmap img)
        {
            double s = Side / (double)Math.Max(img.Width, img.Height);
            int aw = Math.Max(1, (int)Math.Round(img.Width * s)), ah = Math.Max(1, (int)Math.Round(img.Height * s));
            using (Bitmap b = new Bitmap(aw, ah, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(img, new Rectangle(0, 0, aw, ah), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                byte[] px = new byte[aw * ah * 4];
                BitmapData d = b.LockBits(new Rectangle(0, 0, aw, ah), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try { for (int y = 0; y < ah; y++) Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), px, y * aw * 4, aw * 4); }
                finally { b.UnlockBits(d); }
                return SeeBgra(px, aw, ah);
            }
        }

        /// px: BGRA bytes, already at the encoder's size (1024 on the long side).
        static Look SeeBgra(byte[] px, int aw, int ah)
        {
            Load();
            int n = aw * ah;
            float[] f = new float[n * 3];
            for (int i = 0, j = 0; i < n; i++) { int o = i * 4; f[j++] = px[o + 2]; f[j++] = px[o + 1]; f[j++] = px[o]; }
            Dictionary<string, Tensor> r = enc.Run(new Dictionary<string, Tensor> { { enc.InputNames[0], new Tensor(f, ah, aw, 3) } });
            return new Look { Emb = r[enc.OutputNames[0]], AW = aw, AH = ah };
        }

        /// SAM's answer: its outline as 256×256 numbers (above 0 = picked), and the box around it (encoder pixels).
        public sealed class Answer
        {
            public float[] Low;
            public int Area;
            public RectangleF Box;
        }

        /// pts: the clicks; box (encoder pixels) and prev (the last frame's answer) are for following a thing in a video.
        public static Answer Ask(Look E, IList<PickPoint> pts, RectangleF? box, Answer prev)
        {
            Load();
            List<float> P = new List<float>(), Lb = new List<float>();
            if (pts != null) foreach (PickPoint p in pts) { P.Add(p.U * E.AW); P.Add(p.V * E.AH); Lb.Add(p.Keep ? 1 : 0); }
            if (box.HasValue) { RectangleF b = box.Value; P.Add(b.Left); P.Add(b.Top); P.Add(b.Right); P.Add(b.Bottom); Lb.Add(2); Lb.Add(3); }
            else { P.Add(0); P.Add(0); Lb.Add(-1); }
            int n = Lb.Count;
            Dictionary<string, Tensor> inp = new Dictionary<string, Tensor>
            {
                { "image_embeddings", E.Emb },
                { "point_coords", new Tensor(P.ToArray(), 1, n, 2) },
                { "point_labels", new Tensor(Lb.ToArray(), 1, n) },
                { "mask_input", new Tensor(prev != null ? prev.Low : new float[256 * 256], 1, 1, 256, 256) },
                { "has_mask_input", new Tensor(new[] { prev != null ? 1f : 0f }, 1) },
                // the decoder also makes a full-size outline; a small one is enough (the outline is drawn from Low)
                { "orig_im_size", new Tensor(new[] { (float)Math.Max(1, E.AH / 4), (float)Math.Max(1, E.AW / 4) }, 2) }
            };
            Dictionary<string, Tensor> r = dec.Run(inp);
            float[] low = new float[256 * 256];
            Array.Copy(r["low_res_masks"].Data, low, low.Length);
            return Measure(low, E.AW, E.AH);
        }

        static Answer Measure(float[] low, int aw, int ah)
        {
            // the 256×256 outline covers the picture padded to 1024×1024 (4 encoder pixels a cell)
            int cw = Math.Min(256, (aw + 3) / 4), ch = Math.Min(256, (ah + 3) / 4), area = 0, x0 = cw, y0 = ch, x1 = -1, y1 = -1;
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    if (low[y * 256 + x] > 0) { area++; if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            RectangleF box = area > 0 ? RectangleF.FromLTRB(x0 * 4, y0 * 4, Math.Min(aw - 1, x1 * 4 + 4), Math.Min(ah - 1, y1 * 4 + 4)) : RectangleF.Empty;
            return new Answer { Low = low, Area = area * 16, Box = box };
        }

        /// The outline at any size (w×h, the same shape as the picture): how much of each pixel is picked, 0..1.
        public static float[] Alpha(float[] low, int aw, int ah, int w, int h)
        {
            float[] a = new float[w * h];
            double kx = aw / (double)w / 4.0, ky = ah / (double)h / 4.0;
            int[] xi = new int[w]; float[] xf = new float[w];
            for (int x = 0; x < w; x++)
            {
                double gx = (x + 0.5) * kx - 0.5;
                if (gx < 0) gx = 0; if (gx > 255) gx = 255;
                int i = Math.Min(254, (int)gx); xi[x] = i; xf[x] = (float)(gx - i);
            }
            for (int y = 0; y < h; y++)
            {
                double gy = (y + 0.5) * ky - 0.5;
                if (gy < 0) gy = 0; if (gy > 255) gy = 255;
                int j = Math.Min(254, (int)gy); float fy = (float)(gy - j);
                int r0 = j * 256, r1 = r0 + 256, o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = xi[x]; float fx = xf[x];
                    float top = low[r0 + i] + (low[r0 + i + 1] - low[r0 + i]) * fx;
                    float bot = low[r1 + i] + (low[r1 + i + 1] - low[r1 + i]) * fx;
                    float v = top + (bot - top) * fy;
                    a[o + x] = 1f / (1f + (float)Math.Exp(-v * 2));
                }
            }
            return a;
        }

        /// The outline as a mask of bytes (255 = keep), w×h.
        public static byte[] Mask(float[] low, int aw, int ah, int w, int h)
        {
            float[] a = Alpha(low, aw, ah, w, h);
            byte[] m = new byte[a.Length];
            for (int i = 0; i < a.Length; i++) m[i] = (byte)(Math.Max(0, Math.Min(1, (a[i] - 0.1f) / 0.8f)) * 255 + 0.5f);
            return m;
        }

        // ------------------------------------------------------------------ videos: follow the pick through the clip

        /// The pick, followed through a clip: one outline a frame, kept in a temporary file (256 KB a frame).
        public sealed class Track : IDisposable
        {
            readonly string tmp;
            readonly FileStream fs;
            public readonly int Count, AW, AH;
            readonly bool[] have;
            const int Bytes = 256 * 256 * 4;

            Track(int count, int aw, int ah)
            {
                Count = count; AW = aw; AH = ah; have = new bool[count];
                tmp = Path.Combine(Path.GetTempPath(), "fadi_pick_" + Guid.NewGuid().ToString("N") + ".bin");
                fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            }

            void Put(int i, float[] low)
            {
                byte[] b = new byte[Bytes];
                Buffer.BlockCopy(low, 0, b, 0, Bytes);
                lock (fs) { fs.Position = (long)i * Bytes; fs.Write(b, 0, Bytes); }
                have[i] = true;
            }

            float[] Get(int i)
            {
                // a frame that was never reached (the last one or two can differ by rounding) uses its neighbour's
                int k = Math.Max(0, Math.Min(Count - 1, i));
                int d = 0;
                while (!have[k] && d < Count) { d++; if (k - d >= 0 && have[k - d]) { k -= d; break; } if (k + d < Count && have[k + d]) { k += d; break; } }
                float[] low = new float[256 * 256];
                if (!have[k]) { for (int j = 0; j < low.Length; j++) low[j] = -20; return low; }
                byte[] b = new byte[Bytes];
                lock (fs) { fs.Position = (long)k * Bytes; int got = 0, r; while (got < Bytes && (r = fs.Read(b, got, Bytes - got)) > 0) got += r; }
                Buffer.BlockCopy(b, 0, low, 0, Bytes);
                return low;
            }

            /// Frame i's outline at w×h (0..1 a pixel).
            public float[] Alpha(int i, int w, int h) { return Sam.Alpha(Get(i), AW, AH, w, h); }

            public void Dispose() { try { fs.Dispose(); } catch { } try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }

            /// Looks at every frame: the clicked one with the clicks, then forwards to the end and backwards to the
            /// start, each frame asked with the last one's box (a little bigger) and outline.
            /// at: seconds from start where the clicks were made. progress(done, total)
            public static Track Build(string input, double start, double length, double fps, int total, int w, int h,
                                      IList<PickPoint> pts, double at, Action<int, int> progress, Func<bool> cancelled)
            {
                string ff = Tools.FFmpeg;
                if (ff == null) throw new UserError(L.T("err_need_ff"));
                double s = Side / (double)Math.Max(w, h);
                int aw = Math.Max(2, (int)Math.Round(w * s)), ah = Math.Max(2, (int)Math.Round(h * s));
                Track t = new Track(total, aw, ah);
                try
                {
                    int k0 = Math.Max(0, Math.Min(total - 1, (int)Math.Round(at * fps)));
                    int done = 0;
                    Answer first = null, prev = null;
                    Action<int, byte[]> look = (i, px) =>
                    {
                        Look E = SeeBgra(px, aw, ah);
                        Answer r;
                        if (i == k0 || prev == null || prev.Area == 0) r = Ask(E, pts, null, null);
                        else
                        {
                            RectangleF b = prev.Box;
                            float mx = b.Width * 0.08f + 4, my = b.Height * 0.08f + 4;
                            RectangleF big = RectangleF.FromLTRB(Math.Max(0, b.Left - mx), Math.Max(0, b.Top - my), Math.Min(aw - 1, b.Right + mx), Math.Min(ah - 1, b.Bottom + my));
                            r = Ask(E, null, big, prev);
                            if (r.Area < 16) r = prev;   // lost it for a frame (covered, blurred): keep the last outline
                        }
                        if (i == k0) first = r;
                        prev = r;
                        t.Put(i, r.Low);
                        done++;
                        if (progress != null) progress(done, total);
                    };
                    // forwards: from the clicked frame to the end
                    Frames(ff, input, start + k0 / fps, length - k0 / fps, fps, aw, ah, total - k0, (j, px) =>
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        look(k0 + j, px);
                    });
                    // backwards: a second at a time, newest first
                    prev = first;
                    int chunk = Math.Max(1, (int)Math.Round(fps));
                    for (int hi = k0 - 1; hi >= 0; hi -= chunk)
                    {
                        int lo = Math.Max(0, hi - chunk + 1), cnt = hi - lo + 1;
                        List<byte[]> got = new List<byte[]>();
                        Frames(ff, input, start + lo / fps, cnt / fps + 0.5 / fps, fps, aw, ah, cnt, (j, px) => got.Add((byte[])px.Clone()));
                        for (int j = got.Count - 1; j >= 0; j--)
                        {
                            if (cancelled != null && cancelled()) throw new OperationCanceledException();
                            look(lo + j, got[j]);
                        }
                    }
                    return t;
                }
                catch { t.Dispose(); throw; }
            }

            /// Decodes up to max frames (BGRA, aw×ah) from start, length seconds, at fps.
            static void Frames(string ff, string input, double start, double length, double fps, int aw, int ah, int max, Action<int, byte[]> each)
            {
                if (max <= 0 || length <= 0) return;
                Func<double, string> F = v => v.ToString("0.###", CultureInfo.InvariantCulture);
                string args = "-hide_banner -loglevel error -ss " + F(Math.Max(0, start)) + " -t " + F(length) + " -i " + Tools.Quote(input) +
                              " -vf \"fps=" + F(fps) + ",scale=" + aw + ":" + ah + ":flags=bicubic\" -f rawvideo -pix_fmt bgra -";
                ProcessStartInfo psi = new ProcessStartInfo(ff, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(ff)
                };
                using (Process p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s2, e2) => { };
                    p.BeginErrorReadLine();
                    try
                    {
                        Stream inS = p.StandardOutput.BaseStream;
                        int fb = aw * ah * 4;
                        byte[] buf = new byte[fb];
                        for (int j = 0; j < max; j++)
                        {
                            int got = 0, r;
                            while (got < fb && (r = inS.Read(buf, got, fb - got)) > 0) got += r;
                            if (got < fb) break;
                            each(j, buf);
                        }
                    }
                    finally { try { if (!p.HasExited) p.Kill(); } catch { } }
                }
            }
        }
    }
}
