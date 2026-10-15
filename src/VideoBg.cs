using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FileConverter
{
    /// Removes the background from a video, frame by frame, with the same AI as photos (it runs on this PC).
    /// mode: 0 transparent (WebM), 1 a color, 2 the video's own background blurred, 3 a picture.
    public static class VideoBg
    {
        public const int MaxSide = 1280;   // bigger videos are made smaller first (the AI works at 1024 px anyway)

        public static Size OutSize(int w, int h)
        {
            double k = Math.Min(1.0, MaxSide / (double)Math.Max(w, h));
            int ow = Math.Max(2, (int)Math.Round(w * k)), oh = Math.Max(2, (int)Math.Round(h * k));
            return new Size(ow - ow % 2, oh - oh % 2);
        }

        public static double Fps(MediaInfo mi) { return Math.Max(1, Math.Min(24, mi != null && mi.Fps > 0 ? mi.Fps : 24)); }

        public static void Run(string input, double start, double length, int mode, Color color, string picture, string output,
                               Action<int, int> progress, Func<bool> cancelled)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            if (!BgRemover.Ready) throw new UserError(L.T("vbg_need_ai"));
            MediaInfo mi = Media.Probe(input);
            if (mi == null || !mi.HasVideo) throw new UserError(L.T("vbg_no_video"));
            Size sz = OutSize(mi.Width, mi.Height);
            int W = sz.Width, H = sz.Height;
            double fps = Fps(mi);
            if (length <= 0) length = Math.Max(0.1, mi.Duration - start);
            int total = Math.Max(1, (int)Math.Ceiling(length * fps));
            Func<double, string> F = v => v.ToString("0.###", CultureInfo.InvariantCulture);

            Bitmap pic = null;
            if (mode == 3 && picture != null)
                using (Image src = ViewerPage.Decode(picture, 4000)) pic = Cover(src, W, H);

            // decoder: raw BGRA frames on its output
            string dArgs = "-hide_banner -loglevel error -ss " + F(start) + " -t " + F(length) + " -i " + Tools.Quote(input) +
                           " -vf \"fps=" + F(fps) + ",scale=" + W + ":" + H + ":flags=bicubic\" -f rawvideo -pix_fmt bgra -";
            // encoder: frames in, the sound from the original clip
            bool alpha = mode == 0;
            string venc = alpha
                ? "-c:v libvpx-vp9 -pix_fmt yuva420p -b:v 0 -crf 30 -deadline good -row-mt 1 -auto-alt-ref 0"
                : "-c:v libx264 -pix_fmt yuv420p -crf 20 -preset medium -movflags +faststart";
            string aenc = mi.HasAudio ? (alpha ? " -c:a libopus -b:a 128k" : " -c:a aac -b:a 160k") : "";
            string eArgs = "-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s " + W + "x" + H + " -r " + F(fps) + " -i - " +
                           "-ss " + F(start) + " -t " + F(length) + " -i " + Tools.Quote(input) + " -map 0:v:0 " + (mi.HasAudio ? "-map 1:a:0? " : "") +
                           venc + aenc + " -shortest " + Tools.Quote(output);

            Process dec = Start(ff, dArgs, false), enc = null;
            StringBuilder decErr = new StringBuilder(), encErr = new StringBuilder();
            dec.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (decErr) decErr.AppendLine(e.Data); };
            dec.BeginErrorReadLine();
            try
            {
                enc = Start(ff, eArgs, true);
                enc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (encErr) encErr.AppendLine(e.Data); };
                enc.BeginErrorReadLine();
                Stream inS = dec.StandardOutput.BaseStream, outS = enc.StandardInput.BaseStream;
                int frameBytes = W * H * 4;
                byte[] buf = new byte[frameBytes];
                float[] prev = null;
                int n = 0;
                using (Bitmap frame = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    while (true)
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        int got = 0, r;
                        while (got < frameBytes && (r = inS.Read(buf, got, frameBytes - got)) > 0) got += r;
                        if (got < frameBytes) break;
                        Put(frame, buf);
                        byte[] mask = BgRemover.Mask(frame);
                        // a little of the last frame's mask: edges flicker less
                        float[] m = new float[mask.Length];
                        for (int i = 0; i < m.Length; i++)
                        {
                            float a = mask[i] / 255f;
                            a = Math.Max(0, Math.Min(1, (a - 0.08f) / 0.62f));
                            m[i] = prev == null ? a : a * 0.7f + prev[i] * 0.3f;
                        }
                        prev = m;
                        Compose(buf, W, H, m, mode, color, pic);
                        outS.Write(buf, 0, frameBytes);
                        n++;
                        if (progress != null) progress(n, total);
                    }
                }
                outS.Close();
                enc.WaitForExit();
                dec.WaitForExit(5000);
                if (n == 0) throw new UserError(L.T("vbg_failed", Tools.LastLines(decErr.ToString(), 2)));
                if (enc.ExitCode != 0) throw new UserError(L.T("vbg_failed", Tools.LastLines(encErr.ToString(), 2)));
            }
            catch
            {
                try { if (!dec.HasExited) dec.Kill(); } catch { }
                try { if (enc != null && !enc.HasExited) enc.Kill(); } catch { }
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                throw;
            }
            finally
            {
                if (pic != null) pic.Dispose();
                dec.Dispose(); if (enc != null) enc.Dispose();
            }
        }

        static Process Start(string exe, string args, bool input)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = !input, RedirectStandardInput = input, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            return Process.Start(psi);
        }

        static void Put(Bitmap b, byte[] buf)
        {
            BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int row = b.Width * 4;
                for (int y = 0; y < b.Height; y++) Marshal.Copy(buf, y * row, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), row);
            }
            finally { b.UnlockBits(d); }
        }

        /// The picture filling the frame (cropped, not stretched).
        static Bitmap Cover(Image src, int W, int H)
        {
            Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float k = Math.Max(W / (float)src.Width, H / (float)src.Height);
                float w = src.Width * k, h = src.Height * k;
                g.DrawImage(src, (W - w) / 2, (H - h) / 2, w, h);
            }
            return b;
        }

        static byte[] blurBuf;

        /// Mixes each pixel with the new background by how much it belongs to the person / subject.
        static void Compose(byte[] px, int W, int H, float[] m, int mode, Color color, Bitmap pic)
        {
            if (mode == 0)
            {
                for (int i = 0; i < m.Length; i++) px[i * 4 + 3] = (byte)(m[i] * 255);
                return;
            }
            byte[] bg = null;
            if (mode == 2)
            {
                // the frame itself, heavily blurred, behind the subject
                if (blurBuf == null || blurBuf.Length != px.Length) blurBuf = new byte[px.Length];
                Buffer.BlockCopy(px, 0, blurBuf, 0, px.Length);
                using (Bitmap fb = new Bitmap(W, H, PixelFormat.Format32bppArgb))
                {
                    Put(fb, blurBuf);
                    using (Bitmap small = EditSession.Scale(fb, Math.Max(2, W / 8), Math.Max(2, H / 8)))
                    using (Bitmap back = EditSession.Scale(small, W, H))
                    {
                        BitmapData d = back.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        try { for (int y = 0; y < H; y++) Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), blurBuf, y * W * 4, W * 4); }
                        finally { back.UnlockBits(d); }
                    }
                }
                bg = blurBuf;
            }
            else if (mode == 3 && pic != null)
            {
                if (blurBuf == null || blurBuf.Length != px.Length) blurBuf = new byte[px.Length];
                BitmapData d = pic.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try { for (int y = 0; y < H; y++) Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), blurBuf, y * W * 4, W * 4); }
                finally { pic.UnlockBits(d); }
                bg = blurBuf;
            }
            for (int i = 0; i < m.Length; i++)
            {
                int o = i * 4; float a = m[i], ia = 1 - a;
                if (bg != null)
                {
                    px[o] = (byte)(px[o] * a + bg[o] * ia);
                    px[o + 1] = (byte)(px[o + 1] * a + bg[o + 1] * ia);
                    px[o + 2] = (byte)(px[o + 2] * a + bg[o + 2] * ia);
                }
                else
                {
                    px[o] = (byte)(px[o] * a + color.B * ia);
                    px[o + 1] = (byte)(px[o + 1] * a + color.G * ia);
                    px[o + 2] = (byte)(px[o + 2] * a + color.R * ia);
                }
                px[o + 3] = 255;
            }
        }
    }
}
