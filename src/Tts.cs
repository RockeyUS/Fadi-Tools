using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileConverter
{
    /// Text to speech on this PC: natural Piper voices (English and Arabic) run by sherpa-onnx (Apache-2.0).
    /// The engine (about 25 MB) and each voice (about 65 MB) are downloaded the first time they're used, asked first.
    /// The text goes to the engine as UTF-8 in memory, so Arabic is read correctly.
    public static class Tts
    {
        public class Voice
        {
            public string Id, NameKey, Lang, Archive;
            public long Bytes;
        }
        const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
        const string EngineArchive = "sherpa-onnx-v1.13.8-win-x64-shared-MT-Release";
        const long EngineBytes = 24805859;
        public static readonly Voice[] Voices =
        {
            new Voice { Id = "en_US-lessac-medium", NameKey = "tts_v_en_f", Lang = "en", Archive = "vits-piper-en_US-lessac-medium", Bytes = 67230653 },
            new Voice { Id = "en_US-ryan-medium", NameKey = "tts_v_en_m", Lang = "en", Archive = "vits-piper-en_US-ryan-medium", Bytes = 67213100 },
            new Voice { Id = "ar_JO-kareem-medium", NameKey = "tts_v_ar_m", Lang = "ar", Archive = "vits-piper-ar_JO-kareem-medium", Bytes = 67177830 },
        };

        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static string Dir
        {
            get
            {
                string t = Environment.GetEnvironmentVariable("FADI_TTS");   // tests: a folder with engine\ and voices\
                return !string.IsNullOrEmpty(t) ? t : Path.Combine(Paths.ToolsDir, "tts");
            }
        }
        static string EngineDir { get { return Path.Combine(Dir, "engine"); } }
        static string Lib { get { return Path.Combine(EngineDir, IsWindows ? "sherpa-onnx-c-api.dll" : "libsherpa-onnx-c-api.so"); } }
        public static string VoiceDir(Voice v) { return Path.Combine(Dir, "voices", v.Id); }
        static string ModelOf(Voice v) { return Path.Combine(VoiceDir(v), v.Id + ".onnx"); }

        public static bool EngineReady { get { return File.Exists(Lib); } }
        public static bool VoiceReady(Voice v) { return File.Exists(ModelOf(v)) && File.Exists(Path.Combine(VoiceDir(v), "tokens.txt")) && Directory.Exists(Path.Combine(VoiceDir(v), "espeak-ng-data")); }
        public static Voice Find(string id) { foreach (Voice v in Voices) if (v.Id == id) return v; return Voices[0]; }
        /// How much a voice still needs (engine included), for "downloads about … MB first".
        public static long Needed(Voice v) { return (EngineReady ? 0 : EngineBytes) + (VoiceReady(v) ? 0 : v.Bytes); }

        /// Gets the engine and the voice when they aren't here. progress: 0..1 and what's happening.
        public static void Install(Voice v, Action<double, string> progress, Func<bool> cancelled)
        {
            long total = Math.Max(1, Needed(v)), done = 0;
            if (!EngineReady)
            {
                string tmp = Path.Combine(Dir, "engine.tar.bz2");
                Directory.CreateDirectory(Dir);
                Net.Download(Release + "v1.13.8/" + EngineArchive + ".tar.bz2", tmp, (d, t) => { if (progress != null) progress(d / (double)total, L.T("tts_dl_engine", Theme.HumanSize(d))); }, cancelled);
                string x = Path.Combine(Dir, "x_engine");
                Untar(tmp, x);
                Directory.CreateDirectory(EngineDir);
                string lib = Path.Combine(x, EngineArchive, "lib");
                foreach (string f in Directory.GetFiles(lib))
                {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.EndsWith(".dll") || n.EndsWith(".so")) File.Copy(f, Path.Combine(EngineDir, Path.GetFileName(f)), true);
                }
                try { Directory.Delete(x, true); File.Delete(tmp); } catch { }
                if (!EngineReady) throw new UserError(L.T("tts_bad_dl"));
                done += EngineBytes;
            }
            if (!VoiceReady(v))
            {
                string tmp = Path.Combine(Dir, v.Id + ".tar.bz2");
                long before = done;
                Net.Download(Release + "tts-models/" + v.Archive + ".tar.bz2", tmp, (d, t) => { if (progress != null) progress((before + d) / (double)total, L.T("tts_dl_voice", Theme.HumanSize(d), Theme.HumanSize(t > 0 ? t : v.Bytes))); }, cancelled);
                string x = Path.Combine(Dir, "x_" + v.Id);
                Untar(tmp, x);
                string vd = VoiceDir(v);
                if (Directory.Exists(vd)) Directory.Delete(vd, true);
                Directory.CreateDirectory(Path.GetDirectoryName(vd));
                Directory.Move(Path.Combine(x, v.Archive), vd);
                try { Directory.Delete(x, true); File.Delete(tmp); } catch { }
                if (!VoiceReady(v)) throw new UserError(L.T("tts_bad_dl"));
            }
        }

        /// .tar.bz2 with Windows' own tar (built into Windows 10 and 11), or tar on Linux.
        static void Untar(string archive, string into)
        {
            if (Directory.Exists(into)) Directory.Delete(into, true);
            Directory.CreateDirectory(into);
            string tar = IsWindows ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe") : "tar";
            if (IsWindows && !File.Exists(tar)) throw new UserError(L.T("tts_no_tar"));
            string o;
            int code = Tools.RunHidden(tar, "-xjf " + Tools.Quote(archive) + " -C " + Tools.Quote(into), 600000, out o);
            if (code != 0) throw new UserError(L.T("tts_bad_dl") + " " + Tools.LastLines(o, 1));
        }

        // ------------------------------------------------------------------ the engine (sherpa-onnx C API)
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string path, int flags);
        [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr h, string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateFn(IntPtr config);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyFn(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GenerateFn(IntPtr tts, IntPtr text, int sid, float speed);

        static IntPtr lib;
        static CreateFn create; static DestroyFn destroy, destroyAudio; static GenerateFn generate;
        static readonly Dictionary<string, IntPtr> loaded = new Dictionary<string, IntPtr>();
        static readonly object gate = new object();

        static IntPtr Sym(string name)
        {
            IntPtr p = IsWindows ? GetProcAddress(lib, name) : dlsym(lib, name);
            if (p == IntPtr.Zero) throw new Exception("text to speech engine: " + name + " missing");
            return p;
        }

        static void Load()
        {
            if (lib != IntPtr.Zero) return;
            if (IsWindows)
            {
                SetDllDirectoryW(EngineDir);   // its own onnxruntime.dll, next to it
                lib = LoadLibraryW(Lib);
                SetDllDirectoryW(null);
            }
            else lib = dlopen(Lib, 2);
            if (lib == IntPtr.Zero) throw new UserError(L.T("tts_load_failed"));
            create = (CreateFn)Marshal.GetDelegateForFunctionPointer(Sym("SherpaOnnxCreateOfflineTts"), typeof(CreateFn));
            destroy = (DestroyFn)Marshal.GetDelegateForFunctionPointer(Sym("SherpaOnnxDestroyOfflineTts"), typeof(DestroyFn));
            generate = (GenerateFn)Marshal.GetDelegateForFunctionPointer(Sym("SherpaOnnxOfflineTtsGenerate"), typeof(GenerateFn));
            destroyAudio = (DestroyFn)Marshal.GetDelegateForFunctionPointer(Sym("SherpaOnnxDestroyOfflineTtsGeneratedAudio"), typeof(DestroyFn));
        }

        /// A UTF-8 string in unmanaged memory (paths with Arabic letters and the text itself).
        static IntPtr U8(string s, List<IntPtr> keep)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            IntPtr p = Marshal.AllocHGlobal(b.Length + 1);
            Marshal.Copy(b, 0, p, b.Length);
            Marshal.WriteByte(p, b.Length, 0);
            keep.Add(p);
            return p;
        }

        // SherpaOnnxOfflineTtsConfig (sherpa-onnx v1.13.8 c-api.h, x64): measured with offsetof
        const int ConfigSize = 448, O_Model = 0, O_Tokens = 16, O_DataDir = 24, O_Noise = 32, O_NoiseW = 36, O_Length = 40,
            O_Threads = 56, O_Provider = 64, O_MaxSentences = 424, O_Silence = 440;

        static IntPtr Engine(Voice v)
        {
            IntPtr t;
            if (loaded.TryGetValue(v.Id, out t)) return t;
            List<IntPtr> keep = new List<IntPtr>();
            IntPtr cfg = Marshal.AllocHGlobal(ConfigSize);
            try
            {
                for (int i = 0; i < ConfigSize; i++) Marshal.WriteByte(cfg, i, 0);
                Marshal.WriteIntPtr(cfg, O_Model, U8(ModelOf(v), keep));
                Marshal.WriteIntPtr(cfg, O_Tokens, U8(Path.Combine(VoiceDir(v), "tokens.txt"), keep));
                Marshal.WriteIntPtr(cfg, O_DataDir, U8(Path.Combine(VoiceDir(v), "espeak-ng-data"), keep));
                WriteFloat(cfg, O_Noise, 0.667f); WriteFloat(cfg, O_NoiseW, 0.8f); WriteFloat(cfg, O_Length, 1f);
                Marshal.WriteInt32(cfg, O_Threads, Math.Max(1, Math.Min(4, Environment.ProcessorCount)));
                Marshal.WriteIntPtr(cfg, O_Provider, U8("cpu", keep));
                Marshal.WriteInt32(cfg, O_MaxSentences, 2);
                WriteFloat(cfg, O_Silence, 0.2f);
                t = create(cfg);
            }
            finally { Marshal.FreeHGlobal(cfg); foreach (IntPtr p in keep) Marshal.FreeHGlobal(p); }
            if (t == IntPtr.Zero) throw new UserError(L.T("tts_load_failed"));
            loaded[v.Id] = t;
            return t;
        }
        static void WriteFloat(IntPtr p, int off, float f) { Marshal.WriteInt32(p, off, BitConverter.ToInt32(BitConverter.GetBytes(f), 0)); }

        /// Reads the text aloud into a WAV file. speed: 1 = normal (0.5 … 2). Returns how long it is (seconds).
        public static double Speak(string text, Voice v, float speed, string wavPath)
        {
            if (string.IsNullOrEmpty((text ?? "").Trim())) throw new UserError(L.T("tts_empty"));
            if (!EngineReady || !VoiceReady(v)) throw new UserError(L.T("tts_not_ready"));
            float[] samples; int rate;
            lock (gate)
            {
                Load();
                IntPtr tts = Engine(v);
                List<IntPtr> keep = new List<IntPtr>();
                IntPtr audio = IntPtr.Zero;
                try
                {
                    audio = generate(tts, U8(text.Trim(), keep), 0, Math.Max(0.5f, Math.Min(2f, speed)));
                    if (audio == IntPtr.Zero) throw new UserError(L.T("tts_failed"));
                    // SherpaOnnxGeneratedAudio: const float *samples; int32 n; int32 sample_rate
                    IntPtr sp = Marshal.ReadIntPtr(audio, 0);
                    int n = Marshal.ReadInt32(audio, 8);
                    rate = Marshal.ReadInt32(audio, 12);
                    if (n <= 0 || sp == IntPtr.Zero) throw new UserError(L.T("tts_failed"));
                    samples = new float[n];
                    Marshal.Copy(sp, samples, 0, n);
                }
                finally
                {
                    if (audio != IntPtr.Zero) destroyAudio(audio);
                    foreach (IntPtr p in keep) Marshal.FreeHGlobal(p);
                }
            }
            WriteWav(wavPath, samples, rate);
            return samples.Length / (double)Math.Max(1, rate);
        }

        static void WriteWav(string path, float[] s, int rate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream f = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(f))
            {
                int bytes = s.Length * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
                foreach (float x in s) w.Write((short)Math.Max(-32768, Math.Min(32767, (int)Math.Round(x * 32767))));
            }
        }

        public static long SizeOnDisk
        {
            get { long n = 0; try { if (Directory.Exists(Dir)) foreach (string f in Directory.GetFiles(Dir, "*", SearchOption.AllDirectories)) try { n += new FileInfo(f).Length; } catch { } } catch { } return n; }
        }
    }
}
