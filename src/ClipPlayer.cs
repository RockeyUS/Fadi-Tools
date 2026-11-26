using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace FileConverter
{
    /// Plays part of a song or video inside a window (the Trim window): the sound goes straight to the speakers as
    /// it's decoded, so it starts at once wherever you click, and a video's pictures are timed to the sound.
    /// No other program opens.
    public sealed class ClipPlayer : IDisposable
    {
        const int Rate = 48000, Block = 2048, Buffers = 8;
        readonly object lk = new object();
        WaveOut wave;
        Process aproc, vproc;
        int gen;
        volatile bool playing;

        /// Where it's playing (worker thread, about 30 times a second).
        public event Action<double> Tick;
        /// A new picture of a video and its time (worker thread; the receiver keeps the bitmap).
        public event Action<Bitmap, double> Frame;
        /// It reached the end of the part (worker thread). Not called when stopped.
        public event Action Finished;
        public bool Playing { get { return playing; } }
        public int Fps = 25;
        /// How loud it plays: 1 = as it is, 0 = silent, up to 2 = twice (changes take effect within a moment).
        public volatile float Volume = 1f;

        /// Plays from one time to another. Pictures are made w × h when hasVideo.
        public void Play(string file, double from, double to, bool hasAudio, bool hasVideo, int w, int h)
        {
            Stop();
            int my;
            lock (lk)
            {
                my = ++gen;
                playing = true;
                if (hasAudio)
                {
                    if (wave == null) wave = WaveOut.Open(Block, Buffers);
                    else wave.Reset();
                }
            }
            double dur = Math.Max(0.05, to - from);
            bool sound = hasAudio && wave != null;
            Stopwatch sw = new Stopwatch();
            // the clock: the sound card's position when there's sound, otherwise a stopwatch
            long[] written = { 0 };
            bool[] audioEnded = { false };
            Func<double> clock = () =>
            {
                if (sound)
                {
                    lock (lk) { if (my != gen || wave == null) return 0; long p = wave.Played(); return p < 0 ? 0 : p / (double)Rate; }
                }
                return sw.Elapsed.TotalSeconds;
            };
            if (sound) new Thread(() => AudioLoop(my, file, from, dur, written, audioEnded)) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
            else sw.Start();
            if (hasVideo) new Thread(() => VideoLoop(my, file, from, dur, w, h, clock)) { IsBackground = true }.Start();
            new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(33);
                    if (my != gen) return;
                    double t = clock();
                    if (Tick != null) try { Tick(from + Math.Min(t, dur)); } catch { }
                    bool end = sound ? audioEnded[0] && t * Rate >= written[0] - Block / 2 : t >= dur;
                    if (sound && !audioEnded[0] && t >= dur + 0.5) end = true;   // the sound stalled: stop anyway
                    if (end)
                    {
                        lock (lk) { if (my != gen) return; gen++; playing = false; if (wave != null) wave.Reset(); }
                        KillProcs();
                        if (Finished != null) try { Finished(); } catch { }
                        return;
                    }
                }
            }) { IsBackground = true }.Start();
        }

        static Process Start(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Tools.FFmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WindowStyle = ProcessWindowStyle.Hidden };
            return Process.Start(psi);
        }
        static string N(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        void AudioLoop(int my, string file, double from, double dur, long[] written, bool[] ended)
        {
            Process p = null;
            try
            {
                if (Tools.FFmpeg == null) return;
                p = Start("-hide_banner -loglevel quiet -ss " + N(from) + " -i " + Tools.Quote(file) + " -t " + N(dur) + " -vn -ac 2 -ar " + Rate + " -f s16le pipe:1");
                lock (lk) { if (my != gen) { try { p.Kill(); } catch { } return; } aproc = p; }
                Stream s = p.StandardOutput.BaseStream;
                byte[] bytes = new byte[Block * 4];
                short[] pcm = new short[Block * 2];
                while (true)
                {
                    int got = 0, r;
                    while (got < bytes.Length && (r = s.Read(bytes, got, bytes.Length - got)) > 0) got += r;
                    if (got == 0) break;
                    if (got < bytes.Length) Array.Clear(bytes, got, bytes.Length - got);
                    Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
                    float vol = Volume;
                    if (Math.Abs(vol - 1f) > 0.001f)
                        for (int i = 0; i < pcm.Length; i++)
                        {
                            int v = (int)(pcm[i] * vol);
                            pcm[i] = (short)(v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : v);
                        }
                    // wait for a free buffer, then queue it
                    while (true)
                    {
                        lock (lk)
                        {
                            if (my != gen || wave == null) return;
                            if (wave.Free(wave.Next))
                            {
                                wave.Write(wave.Next, pcm);
                                wave.Next = (wave.Next + 1) % wave.Count;
                                written[0] += Block;
                                break;
                            }
                        }
                        Thread.Sleep(5);
                    }
                    if (got < bytes.Length) break;
                }
            }
            catch { }
            finally { ended[0] = true; }
        }

        void VideoLoop(int my, string file, double from, double dur, int w, int h, Func<double> clock)
        {
            try
            {
                if (Tools.FFmpeg == null) return;
                w = Math.Max(16, w + w % 2); h = Math.Max(16, h + h % 2);
                Process p = Start("-hide_banner -loglevel quiet -ss " + N(from) + " -i " + Tools.Quote(file) + " -t " + N(dur) + " -an -vf \"scale=" + w + ":" + h + "\" -r " + Fps + " -f rawvideo -pix_fmt bgra pipe:1");
                lock (lk) { if (my != gen) { try { p.Kill(); } catch { } return; } vproc = p; }
                Stream s = p.StandardOutput.BaseStream;
                byte[] buf = new byte[w * h * 4];
                for (int i = 0; my == gen; i++)
                {
                    int got = 0, r;
                    while (got < buf.Length && (r = s.Read(buf, got, buf.Length - got)) > 0) got += r;
                    if (got < buf.Length) break;
                    double t = i / (double)Fps;
                    // wait for the sound to get there; skip a picture that's already late
                    while (my == gen && clock() < t - 0.005) Thread.Sleep(4);
                    if (my != gen) break;
                    if (clock() - t > 0.15) continue;
                    // drawn opaque (some videos come out of the decoder with no alpha)
                    Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                    BitmapData bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    for (int y = 0; y < h; y++) Marshal.Copy(buf, y * w * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w * 4);
                    b.UnlockBits(bd);
                    if (my != gen || Frame == null) { b.Dispose(); break; }
                    try { Frame(b, from + t); } catch { b.Dispose(); }
                }
            }
            catch { }
        }

        void KillProcs()
        {
            Process a, v;
            lock (lk) { a = aproc; v = vproc; aproc = vproc = null; }
            if (a != null) try { if (!a.HasExited) a.Kill(); } catch { }
            if (v != null) try { if (!v.HasExited) v.Kill(); } catch { }
        }

        /// Stops at once (the place it got to stays where Tick last said).
        public void Stop()
        {
            lock (lk)
            {
                gen++;
                playing = false;
                if (wave != null) try { wave.Reset(); } catch { }
            }
            KillProcs();
        }

        public void Dispose()
        {
            Stop();
            lock (lk) { if (wave != null) { try { wave.Dispose(); } catch { } wave = null; } }
        }
    }
}
