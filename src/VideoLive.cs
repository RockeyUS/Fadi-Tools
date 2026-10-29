using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FileConverter
{
    /// Where the preview's pictures come from: photos, and a video's frame at a moment of its file.
    public interface IFrames
    {
        Bitmap Video(VClip c, double srcT, out bool exact);
        Bitmap Still(string file);
    }

    // ====================================================================== the sound of files, decoded once
    /// Each file's sound as plain 48 kHz stereo samples in a temporary file (decoded once, in the background),
    /// so playing can start at any moment and mix any number of clips without starting FFmpeg again.
    public static class PcmCache
    {
        public const int Rate = 48000;
        public class Entry
        {
            public string File, Pcm;
            public long Bytes;          // written so far
            public volatile bool Done, Failed;
        }
        static readonly Dictionary<string, Entry> map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static bool cleaned;

        static string Dir { get { return Path.Combine(Path.GetTempPath(), "fadi_pcm"); } }

        /// The decoded sound of a file (decoding starts the first time it's asked for).
        public static Entry Get(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            Entry e;
            lock (map)
            {
                if (map.TryGetValue(file, out e)) return e;
                e = new Entry { File = file };
                map[file] = e;
            }
            Thread t = new Thread(() => Decode(e)) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.Start();
            return e;
        }

        static string Key(string file)
        {
            FileInfo fi = new FileInfo(file);
            string s = file.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            using (MD5 md = MD5.Create())
            {
                byte[] h = md.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder b = new StringBuilder();
                for (int i = 0; i < 10; i++) b.Append(h[i].ToString("x2"));
                return b.ToString();
            }
        }

        static void Clean()
        {
            if (cleaned) return;
            cleaned = true;
            try
            {
                if (!Directory.Exists(Dir)) return;
                foreach (string f in Directory.GetFiles(Dir))
                    try { if ((DateTime.Now - File.GetLastAccessTime(f)).TotalDays > 3 && (DateTime.Now - File.GetLastWriteTime(f)).TotalDays > 3) File.Delete(f); } catch { }
            }
            catch { }
        }

        static void Decode(Entry e)
        {
            try
            {
                Clean();
                Directory.CreateDirectory(Dir);
                string k = Key(e.File);
                string pcm = Path.Combine(Dir, k + ".pcm"), ok = Path.Combine(Dir, k + ".ok");
                if (File.Exists(pcm) && File.Exists(ok))
                {
                    e.Pcm = pcm; Interlocked.Exchange(ref e.Bytes, new FileInfo(pcm).Length); e.Done = true;
                    try { File.SetLastAccessTime(pcm, DateTime.Now); File.SetLastWriteTime(ok, DateTime.Now); } catch { }
                    return;
                }
                string ff = Tools.FFmpeg;
                if (ff == null) { e.Failed = true; return; }
                ProcessStartInfo psi = new ProcessStartInfo(ff, "-hide_banner -loglevel error -i " + Tools.Quote(e.File) + " -vn -sn -dn -ac 2 -ar " + Rate + " -f s16le pipe:1")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (Process p = Process.Start(psi))
                using (FileStream fs = new FileStream(pcm, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    p.ErrorDataReceived += (s, a) => { };
                    p.BeginErrorReadLine();
                    e.Pcm = pcm;
                    byte[] buf = new byte[1 << 16];
                    Stream o = p.StandardOutput.BaseStream;
                    int r;
                    while ((r = o.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, r);
                        fs.Flush();
                        Interlocked.Add(ref e.Bytes, r);
                        if (Tools.ShuttingDown) { try { p.Kill(); } catch { } e.Failed = true; return; }
                    }
                    p.WaitForExit(10000);
                }
                File.WriteAllText(ok, "1");
                e.Done = true;
            }
            catch { e.Failed = true; }
        }
    }

    /// Reads a file's decoded sound (stereo floats), with a window kept in memory.
    sealed class PcmReader : IDisposable
    {
        readonly PcmCache.Entry e;
        FileStream fs;
        readonly float[] win = new float[PcmCache.Rate * 2];   // one second, stereo
        long winStart = -1; int winLen;
        byte[] raw = new byte[PcmCache.Rate * 4];

        public PcmReader(PcmCache.Entry e) { this.e = e; }

        bool Open()
        {
            if (fs != null) return true;
            if (e == null || e.Pcm == null) return false;
            try { fs = new FileStream(e.Pcm, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return true; } catch { return false; }
        }

        /// count frames starting at frame `at` into dst (stereo) at dstOff; silence where there's nothing (yet).
        public void Read(long at, int count, float[] dst, int dstOff)
        {
            int i = 0;
            while (i < count)
            {
                long f = at + i;
                if (f < 0) { dst[dstOff + i * 2] = dst[dstOff + i * 2 + 1] = 0; i++; continue; }
                if (winStart < 0 || f < winStart || f >= winStart + winLen) Fill(Math.Max(0, f - 2048));
                if (f < winStart || f >= winStart + winLen)
                {
                    // not decoded yet (or past the end): silence for the rest
                    for (; i < count; i++) dst[dstOff + i * 2] = dst[dstOff + i * 2 + 1] = 0;
                    return;
                }
                int n = (int)Math.Min(count - i, winStart + winLen - f);
                Array.Copy(win, (int)(f - winStart) * 2, dst, dstOff + i * 2, n * 2);
                i += n;
            }
        }

        void Fill(long from)
        {
            winStart = from; winLen = 0;
            if (!Open()) return;
            long have = Interlocked.Read(ref e.Bytes) / 4;
            int n = (int)Math.Max(0, Math.Min(PcmCache.Rate, have - from));
            if (n <= 0) return;
            try
            {
                fs.Seek(from * 4, SeekOrigin.Begin);
                int got = 0, r;
                while (got < n * 4 && (r = fs.Read(raw, got, n * 4 - got)) > 0) got += r;
                n = got / 4;
                for (int k = 0; k < n * 2; k++) win[k] = (short)(raw[k * 2] | (raw[k * 2 + 1] << 8)) / 32768f;
                winLen = n;
            }
            catch { winLen = 0; }
        }

        public void Dispose() { if (fs != null) try { fs.Dispose(); } catch { } fs = null; }
    }

    /// One clip's sound while playing: straight from the file at 1×, or stretched without changing the pitch
    /// (WSOLA: overlapping slices of the sound, each lined up with the last) when it's faster or slower.
    sealed class Voice : IDisposable
    {
        const int N = 1536, Hs = N / 2, Tol = 480;
        static readonly float[] Win = MakeWin();
        static float[] MakeWin() { float[] w = new float[N]; for (int i = 0; i < N; i++) w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N)); return w; }

        public readonly string File;
        readonly PcmReader rd;
        public double Pos;          // source seconds of the next sample handed out
        public double Speed = 1;
        public long Touched;
        // stretching
        double ana; long prev = -1;
        readonly float[] ola = new float[N * 2], seg = new float[N * 2], refm = new float[N], cand = new float[N + Tol * 2];
        readonly float[] region = new float[(N + Tol * 2) * 2], outq = new float[N * 4];
        int outCount;

        public Voice(string file) { File = file; rd = new PcmReader(PcmCache.Get(file)); }

        public void Reset(double src, double speed)
        {
            Pos = src; Speed = speed;
            ana = src * PcmCache.Rate; prev = -1; outCount = 0;
            Array.Clear(ola, 0, ola.Length);
        }

        /// The next `count` frames of this clip's sound (stereo) into dst.
        public void Pull(int count, float[] dst)
        {
            if (Math.Abs(Speed - 1) < 0.005)
            {
                rd.Read((long)Math.Round(Pos * PcmCache.Rate), count, dst, 0);
                Pos += count / (double)PcmCache.Rate;
                return;
            }
            int done = 0;
            while (done < count)
            {
                if (outCount == 0) Hop();
                int n = Math.Min(outCount, count - done);
                Array.Copy(outq, 0, dst, done * 2, n * 2);
                Array.Copy(outq, n * 2, outq, 0, (outCount - n) * 2);
                outCount -= n; done += n;
            }
            Pos += count * Speed / PcmCache.Rate;
        }

        void Hop()
        {
            long nominal = (long)Math.Round(ana), best = nominal;
            if (prev >= 0)
            {
                // the slice that best continues the last one, within ±10 ms of where it should be
                rd.Read(prev + Hs, N, seg, 0);
                for (int i = 0; i < N; i++) refm[i] = seg[i * 2] + seg[i * 2 + 1];
                rd.Read(nominal - Tol, N + Tol * 2, region, 0);
                for (int i = 0; i < N + Tol * 2; i++) cand[i] = region[i * 2] + region[i * 2 + 1];
                double bestC = double.MinValue;
                for (int d = -Tol; d <= Tol; d += 8)
                {
                    double c = 0, en = 1e-9;
                    int o = d + Tol;
                    for (int i = 0; i < N; i += 4) { float v = cand[o + i]; c += refm[i] * v; en += v * v; }
                    c /= Math.Sqrt(en);
                    if (c > bestC) { bestC = c; best = nominal + d; }
                }
            }
            rd.Read(best, N, seg, 0);
            for (int i = 0; i < N; i++) { ola[i * 2] += seg[i * 2] * Win[i]; ola[i * 2 + 1] += seg[i * 2 + 1] * Win[i]; }
            Array.Copy(ola, 0, outq, outCount * 2, Hs * 2);
            outCount += Hs;
            Array.Copy(ola, Hs * 2, ola, 0, (N - Hs) * 2);
            Array.Clear(ola, (N - Hs) * 2, Hs * 2);
            prev = best;
            ana += Hs * Speed;
        }

        public void Dispose() { rd.Dispose(); }
    }

    // ====================================================================== sound out (Windows' waveOut)
    sealed class WaveOut : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public short wFormatTag, nChannels; public int nSamplesPerSec, nAvgBytesPerSec; public short nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [StructLayout(LayoutKind.Explicit, Size = 12)] struct MMTIME { [FieldOffset(0)] public int wType; [FieldOffset(4)] public int u; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr h, ref MMTIME t, int size);
        const int WHDR_DONE = 1, WHDR_PREPARED = 2, TIME_BYTES = 4, TIME_SAMPLES = 2;

        IntPtr h;
        readonly IntPtr[] hdr, buf;
        readonly bool[] queued;
        readonly int frames, hsize = Marshal.SizeOf(typeof(WAVEHDR));
        readonly int flagsAt = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
        public int Next;

        WaveOut(int frames, int count) { this.frames = frames; hdr = new IntPtr[count]; buf = new IntPtr[count]; queued = new bool[count]; }

        public int Count { get { return hdr.Length; } }

        public static WaveOut Open(int frames, int count)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            WaveOut w = new WaveOut(frames, count);
            try
            {
                WAVEFORMATEX f = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = PcmCache.Rate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = PcmCache.Rate * 4 };
                if (waveOutOpen(out w.h, -1, ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) return null;
                for (int i = 0; i < count; i++)
                {
                    w.buf[i] = Marshal.AllocHGlobal(frames * 4);
                    w.hdr[i] = Marshal.AllocHGlobal(w.hsize);
                    WAVEHDR z = new WAVEHDR { lpData = w.buf[i], dwBufferLength = frames * 4 };
                    Marshal.StructureToPtr(z, w.hdr[i], false);
                }
                return w;
            }
            catch { try { w.Dispose(); } catch { } return null; }
        }

        public bool Free(int i) { return !queued[i] || (Marshal.ReadInt32(hdr[i], flagsAt) & WHDR_DONE) != 0; }

        public void Write(int i, short[] data)
        {
            if (queued[i]) { waveOutUnprepareHeader(h, hdr[i], hsize); queued[i] = false; }
            Marshal.Copy(data, 0, buf[i], frames * 2);
            WAVEHDR z = new WAVEHDR { lpData = buf[i], dwBufferLength = frames * 4 };
            Marshal.StructureToPtr(z, hdr[i], false);
            waveOutPrepareHeader(h, hdr[i], hsize);
            waveOutWrite(h, hdr[i], hsize);
            queued[i] = true;
        }

        /// Frames played since it was opened or last reset.
        public long Played()
        {
            MMTIME t = new MMTIME { wType = TIME_SAMPLES };
            if (waveOutGetPosition(h, ref t, 12) != 0) return -1;
            uint u = (uint)t.u;
            return t.wType == TIME_BYTES ? u / 4 : u;
        }

        /// Stops at once and forgets what was queued (the position starts again at 0).
        public void Reset()
        {
            waveOutReset(h);
            for (int i = 0; i < hdr.Length; i++) if (queued[i]) { waveOutUnprepareHeader(h, hdr[i], hsize); queued[i] = false; }
            Next = 0;
        }

        public void Dispose()
        {
            if (h != IntPtr.Zero) { try { Reset(); waveOutClose(h); } catch { } h = IntPtr.Zero; }
            for (int i = 0; i < hdr.Length; i++)
            {
                if (buf[i] != IntPtr.Zero) Marshal.FreeHGlobal(buf[i]);
                if (hdr[i] != IntPtr.Zero) Marshal.FreeHGlobal(hdr[i]);
                buf[i] = hdr[i] = IntPtr.Zero;
            }
        }
    }

    // ====================================================================== pictures while playing
    /// Live frames of the clips being played: one FFmpeg per visible video clip, decoding just ahead of the playhead.
    public sealed class LiveFrames : IFrames
    {
        sealed class Dec
        {
            public string ClipId, File; public double Start, Rate; public int W, H; public bool Alpha;
            Process p; Thread th; volatile bool stop;
            readonly Queue<KeyValuePair<double, Bitmap>> q = new Queue<KeyValuePair<double, Bitmap>>();
            public Bitmap Cur; public double CurT = -1;
            public double Newest = -1;
            public long Touched;
            public readonly DateTime Began = DateTime.UtcNow;

            public void Begin()
            {
                th = new Thread(Run) { IsBackground = true };
                th.Start();
            }

            void Run()
            {
                try
                {
                    string ff = Tools.FFmpeg;
                    if (ff == null) return;
                    CultureInfo ci = CultureInfo.InvariantCulture;
                    string args = "-hide_banner -loglevel error " + (Alpha ? "-c:v libvpx-vp9 " : "") + "-ss " + Math.Max(0, Start).ToString("0.###", ci) + " -i " + Tools.Quote(File) +
                                  " -an -sn -dn -vf \"fps=" + Rate.ToString("0.###", ci) + ",scale=" + W + ":" + H + ",setsar=1\" -f rawvideo -pix_fmt bgra pipe:1";
                    p = Process.Start(new ProcessStartInfo(ff, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                    p.ErrorDataReceived += (s, a) => { };
                    p.BeginErrorReadLine();
                    Stream s2 = p.StandardOutput.BaseStream;
                    byte[] b = new byte[W * H * 4];
                    for (int k = 0; !stop; k++)
                    {
                        int got = 0, r;
                        while (got < b.Length && (r = s2.Read(b, got, b.Length - got)) > 0) got += r;
                        if (got < b.Length) break;
                        Bitmap bm = new Bitmap(W, H, Alpha ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppPArgb);
                        BitmapData d = bm.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, bm.PixelFormat);
                        if (d.Stride == W * 4) Marshal.Copy(b, 0, d.Scan0, b.Length);
                        else for (int y = 0; y < H; y++) Marshal.Copy(b, y * W * 4, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), W * 4);
                        bm.UnlockBits(d);
                        double t = Start + k / Rate;
                        lock (q) { q.Enqueue(new KeyValuePair<double, Bitmap>(t, bm)); Newest = t; }
                        // keep just a little ahead
                        while (!stop) { int n; lock (q) n = q.Count; if (n < 6) break; Thread.Sleep(3); }
                    }
                }
                catch { }
                finally { Kill(); }
            }

            /// The newest frame at or before srcT (older ones are let go).
            public Bitmap At(double srcT)
            {
                lock (q)
                {
                    while (q.Count > 0 && q.Peek().Key <= srcT + 0.5 / Rate)
                    {
                        KeyValuePair<double, Bitmap> f = q.Dequeue();
                        if (Cur != null) Cur.Dispose();
                        Cur = f.Value; CurT = f.Key;
                    }
                }
                return Cur;
            }

            void Kill() { if (p != null) { try { if (!p.HasExited) p.Kill(); } catch { } try { p.Dispose(); } catch { } p = null; } }

            public void Stop()
            {
                stop = true;
                Kill();
                lock (q) { while (q.Count > 0) q.Dequeue().Value.Dispose(); }
                if (Cur != null) { Cur.Dispose(); Cur = null; }
            }
        }

        readonly Dictionary<string, Dec> decs = new Dictionary<string, Dec>();
        readonly IFrames stills;
        public int BoxW = 960, BoxH = 540;
        public double Fps = 30;
        long now;

        public LiveFrames(IFrames stills) { this.stills = stills; }

        public Bitmap Still(string file) { return stills.Still(file); }

        double RateFor(double speed) { return Math.Max(5, Math.Min(60, Fps * Math.Max(0.1, speed))); }

        void Size(VClip c, out int w, out int h)
        {
            int sw = c.Src != null && c.Src.W > 0 ? c.Src.W : 1280, sh = c.Src != null && c.Src.H > 0 ? c.Src.H : 720;
            double k = Math.Min(1, Math.Min(BoxW / (double)sw, BoxH / (double)sh));
            w = Math.Max(2, (int)Math.Round(sw * k)); h = Math.Max(2, (int)Math.Round(sh * k));
            w -= w % 2; h -= h % 2;
        }

        Dec Ensure(VClip c, double srcT)
        {
            Dec d;
            decs.TryGetValue(c.Id, out d);
            double rate = RateFor(c.Speed);
            int w, h; Size(c, out w, out h);
            // a decoder that's still starting (FFmpeg can take a moment, more so while the file is still being read
            // for its thumbnails and sound) is given time; one that's a little behind catches up by skipping frames.
            // Starting it again in those cases is what froze the picture for the first seconds.
            bool warming = d != null && d.Newest < 0 && (DateTime.UtcNow - d.Began).TotalSeconds < 5;
            bool stale = d == null || d.File != c.File || Math.Abs(d.Rate - rate) > 0.01 || d.W != w || d.H != h || d.Alpha != c.Alpha
                         || srcT < d.Start - 0.05 || (d.CurT >= 0 && srcT < d.CurT - 0.25)
                         || (!warming && srcT > Math.Max(d.Newest, d.Start) + 3.0);
            if (stale)
            {
                if (d != null) d.Stop();
                d = new Dec { ClipId = c.Id, File = c.File, Start = Math.Max(0, srcT), Rate = rate, W = w, H = h, Alpha = c.Alpha };
                decs[c.Id] = d;
                d.Begin();
            }
            d.Touched = now;
            return d;
        }

        public Bitmap Video(VClip c, double srcT, out bool exact)
        {
            exact = true;
            if (c.File == null) return null;
            Dec d = Ensure(c, srcT);
            Bitmap b = d.At(srcT);
            if (b == null)
            {
                // its decoder is just starting: the nearest frame already at hand meanwhile (no black flash)
                bool ex;
                b = stills.Video(c, srcT, out ex);
                exact = false;
            }
            return b;
        }

        /// Starts a clip's decoder before it shows (so it's ready on time).
        public void Prepare(VClip c, double srcT) { if (c.File != null && c.Kind == VKind.Video) Ensure(c, srcT); }

        /// One frame done: decoders of clips that aren't showing any more stop.
        public void Sweep()
        {
            List<string> gone = null;
            foreach (KeyValuePair<string, Dec> kv in decs) if (kv.Value.Touched < now) { if (gone == null) gone = new List<string>(); gone.Add(kv.Key); }
            if (gone != null) foreach (string k in gone) { decs[k].Stop(); decs.Remove(k); }
            now++;
        }

        public void Clear() { foreach (Dec d in decs.Values) d.Stop(); decs.Clear(); }
        public bool Fits(int w, int h, double fps) { return BoxW == w && BoxH == h && Math.Abs(Fps - fps) < 0.01; }
    }

    // ====================================================================== the player
    /// Plays a project as it is: the picture composed at the playhead from live frames, the sound mixed from every
    /// clip as it plays. Edits (volume, speed, anything) take effect at once — there's nothing to prepare first.
    public sealed class LivePlayer
    {
        const int Block = 1024, Buffers = 6;
        volatile VProject project;
        readonly FrameCache stills;
        LiveFrames live;
        WaveOut wave;
        readonly object mixLock = new object();
        double baseT, mixT;
        readonly Stopwatch sw = new Stopwatch();
        readonly Dictionary<string, Voice> voices = new Dictionary<string, Voice>();
        long mixCount;
        // every Start and Stop begins a new "generation": threads of an older one finish on their own (nothing waits for them)
        volatile int gen;
        volatile bool running;
        Timer idle;
        public volatile float Master = 1f;
        public bool Running { get { return running; } }
        /// A composed picture and its time (on a background thread; the receiver owns the picture).
        public event Action<Bitmap, double> Frame;
        public event Action Ended;
        int posting;

        public LivePlayer(FrameCache stills) { this.stills = stills; }

        public void Update(VProject p) { project = p; }

        /// Seconds on the timeline being heard now.
        public double Now
        {
            get
            {
                lock (mixLock)
                {
                    if (wave != null) { long f = wave.Played(); if (f >= 0) return baseT + f / (double)PcmCache.Rate; }
                    return baseT + sw.Elapsed.TotalSeconds;
                }
            }
        }

        /// Plays from a moment. The sound device and the decoders of the last play are reused when they still fit,
        /// so pausing and playing again (even quickly, many times) starts at once.
        public void Start(VProject p, double from, int w, int h, double fps)
        {
            w = Math.Max(64, w); h = Math.Max(64, h); fps = Math.Max(15, Math.Min(30, fps));
            int my;
            LiveFrames old = null;
            lock (mixLock)
            {
                my = ++gen;
                running = true;
                project = p;
                if (live == null || !live.Fits(w, h, fps)) { old = live; live = new LiveFrames(stills) { BoxW = w, BoxH = h, Fps = fps }; }
                if (wave == null) wave = WaveOut.Open(Block, Buffers); else wave.Reset();
                baseT = mixT = from; sw.Restart();
                foreach (Voice v in voices.Values) v.Dispose();
                voices.Clear();
                Interlocked.Exchange(ref posting, 0);
            }
            if (old != null) ThreadPool.QueueUserWorkItem(_ => { lock (stills.Gate) old.Clear(); });
            // make sure every sound is being decoded
            foreach (VTrack t in p.Tracks) foreach (VClip c in t.Clips) if (c.HasSound && c.File != null) PcmCache.Get(c.File);
            LiveFrames lf = live;
            if (wave != null) new Thread(() => MixLoop(my)) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start();
            new Thread(() => RenderLoop(my, w, h, lf)) { IsBackground = true }.Start();
        }

        /// Jumps while playing (sound and picture carry on from there).
        public void Seek(double t)
        {
            lock (mixLock)
            {
                if (wave != null) wave.Reset();
                baseT = mixT = Math.Max(0, t);
                sw.Restart();
                foreach (Voice v in voices.Values) v.Dispose();
                voices.Clear();
            }
        }

        /// Pauses at once (no waiting for anything). The decoders and the sound device stay ready for a few
        /// seconds in case it plays again; then they're let go.
        public void Stop()
        {
            lock (mixLock)
            {
                gen++;
                running = false;
                if (wave != null) wave.Reset();
                foreach (Voice v in voices.Values) v.Dispose();
                voices.Clear();
            }
            int g = gen;
            Timer old = idle;
            idle = new Timer(_ => Release(g), null, 20000, Timeout.Infinite);
            if (old != null) old.Dispose();
        }

        void Release(int g)
        {
            LiveFrames l;
            lock (mixLock)
            {
                if (gen != g || running) return;
                if (wave != null) { wave.Dispose(); wave = null; }
                l = live; live = null;
            }
            if (l != null) lock (stills.Gate) l.Clear();
        }

        /// Everything let go now (closing the video or the app).
        public void Shutdown()
        {
            Stop();
            Release(gen);
        }

        /// The picture's receiver says it's ready for the next one.
        public void Delivered() { Interlocked.Exchange(ref posting, 0); }

        void RenderLoop(int my, int w, int h, LiveFrames lf)
        {
            Stopwatch pace = Stopwatch.StartNew();
            double step = 1 / lf.Fps;
            try
            {
                while (gen == my)
                {
                    double next = pace.Elapsed.TotalSeconds + step;
                    VProject p = project;
                    double t = Now;
                    double dur = p.Duration;
                    if (t >= dur - 0.001)
                    {
                        if (gen == my && Ended != null) Ended();
                        break;
                    }
                    Bitmap b = null;
                    bool waiting;
                    lock (stills.Gate)
                    {
                        if (gen != my) break;
                        try { b = Composer.Compose(p, t, w, h, lf, out waiting); } catch { }
                        // clips about to start: their decoders begin now, so they're ready on time
                        foreach (VTrack tr in p.Tracks)
                            if (!tr.Hidden && tr.Visual)
                                foreach (VClip c in tr.Clips)
                                    if (c.Kind == VKind.Video && c.At > t && c.At < t + 1.0) lf.Prepare(c, c.In);
                        lf.Sweep();
                    }
                    if (b != null)
                    {
                        if (gen == my && Frame != null && Interlocked.CompareExchange(ref posting, 1, 0) == 0) Frame(b, t);
                        else b.Dispose();
                    }
                    double wait = next - pace.Elapsed.TotalSeconds;
                    if (wait > 0.001) Thread.Sleep((int)(wait * 1000));
                }
            }
            catch { }
        }

        void MixLoop(int my)
        {
            short[] outb = new short[Block * 2];
            float[] acc = new float[Block * 2], tmp = new float[Block * 2];
            try
            {
                while (gen == my)
                {
                    bool wrote = false;
                    lock (mixLock)
                    {
                        if (gen != my) break;
                        if (wave != null && wave.Free(wave.Next))
                        {
                            Mix(mixT, acc, tmp, outb);
                            wave.Write(wave.Next, outb);
                            wave.Next = (wave.Next + 1) % wave.Count;
                            mixT += Block / (double)PcmCache.Rate;
                            wrote = true;
                        }
                    }
                    if (!wrote) Thread.Sleep(3);
                }
            }
            catch { }
        }

        /// One block of sound at timeline time t0: every clip that sounds then, at its volume (with keyframes),
        /// fades and cross-fades, then the preview volume.
        void Mix(double t0, float[] acc, float[] tmp, short[] outb)
        {
            Array.Clear(acc, 0, acc.Length);
            VProject p = project;
            mixCount++;
            double rate = PcmCache.Rate, t1 = t0 + Block / rate;
            if (p != null)
            {
                VTrack main = p.Main;
                for (int ti = 0; ti < p.Tracks.Count; ti++)
                {
                    VTrack tr = p.Tracks[ti];
                    if (tr.Muted) continue;
                    for (int ci = 0; ci < tr.Clips.Count; ci++)
                    {
                        VClip c = tr.Clips[ci];
                        if (!c.HasSound || c.Muted || c.File == null || c.End <= t0 || c.At >= t1) continue;
                        int i0 = Math.Max(0, (int)Math.Ceiling((c.At - t0) * rate)), i1 = Math.Min(Block, (int)Math.Floor((c.End - t0) * rate));
                        int n = i1 - i0;
                        if (n <= 0) continue;
                        double ts = t0 + i0 / rate;
                        double src = c.In + (ts - c.At) * c.Speed;
                        Voice v;
                        if (!voices.TryGetValue(c.Id, out v) || v.File != c.File) { if (v != null) v.Dispose(); v = new Voice(c.File); voices[c.Id] = v; v.Reset(src, c.Speed); }
                        else if (Math.Abs(v.Pos - src) > 0.04 || Math.Abs(v.Speed - c.Speed) > 0.001) v.Reset(src, c.Speed);
                        v.Touched = mixCount;
                        v.Pull(n, tmp);
                        // cross-fades between main clips (like the export's acrossfade)
                        double tin = 0, tout = 0;
                        if (tr == main)
                        {
                            if (ci > 0) tin = p.TransLen(ci - 1);
                            if (ci < tr.Clips.Count - 1) tout = p.TransLen(ci);
                        }
                        float g0 = c.Eval("vol", ts, c.Volume), g1 = c.Eval("vol", ts + n / rate, c.Volume);
                        for (int k = 0; k < n; k++)
                        {
                            double t = ts + k / rate;
                            double g = g0 + (g1 - g0) * k / n;
                            double fromStart = t - c.At, toEnd = c.End - t;
                            if (c.FadeIn > 0.01 && fromStart < c.FadeIn) g *= fromStart / c.FadeIn;
                            if (c.FadeOut > 0.01 && toEnd < c.FadeOut) g *= toEnd / c.FadeOut;
                            if (tin > 0 && fromStart < tin) g *= fromStart / tin;
                            if (tout > 0 && toEnd < tout) g *= toEnd / tout;
                            int o = (i0 + k) * 2;
                            acc[o] += (float)(tmp[k * 2] * g);
                            acc[o + 1] += (float)(tmp[k * 2 + 1] * g);
                        }
                    }
                }
            }
            // sounds that stopped playing let go of their files
            List<string> gone = null;
            foreach (KeyValuePair<string, Voice> kv in voices) if (kv.Value.Touched < mixCount) { if (gone == null) gone = new List<string>(); gone.Add(kv.Key); }
            if (gone != null) foreach (string k in gone) { voices[k].Dispose(); voices.Remove(k); }
            float m = Master;
            for (int i = 0; i < acc.Length; i++)
            {
                float x = acc[i] * m;
                if (x > 0.98f) x = 0.98f; else if (x < -0.98f) x = -0.98f;
                outb[i] = (short)(x * 32767);
            }
        }

        /// For tests: the mix of a stretch of the timeline as 16-bit stereo (no sound card needed).
        public static short[] RenderSound(VProject p, double from, double seconds)
        {
            LivePlayer lp = new LivePlayer(new FrameCache());
            lp.project = p;
            int blocks = (int)Math.Ceiling(seconds * PcmCache.Rate / Block);
            short[] all = new short[blocks * Block * 2], outb = new short[Block * 2];
            float[] acc = new float[Block * 2], tmp = new float[Block * 2];
            double t = from;
            for (int b = 0; b < blocks; b++)
            {
                lp.Mix(t, acc, tmp, outb);
                Array.Copy(outb, 0, all, b * Block * 2, outb.Length);
                t += Block / (double)PcmCache.Rate;
            }
            foreach (Voice v in lp.voices.Values) v.Dispose();
            return all;
        }
    }
}
