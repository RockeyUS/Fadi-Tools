using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileConverter
{
    /// AI background removal: runs the IS-Net (DIS) segmentation model through ONNX Runtime's C API.
    /// Both the runtime and the model are downloaded once, on first use.
    public static class BgRemover
    {
        public const string OrtVersion = "1.20.1";
        public static string Dir { get { return Path.Combine(Paths.ToolsDir, "bg"); } }
        public static string ModelPath { get { return Path.Combine(Dir, "isnet-general-use.onnx"); } }
        public static string RuntimePath { get { return Path.Combine(Dir, IsWindows ? "onnxruntime.dll" : "libonnxruntime.so"); } }
        public const string ModelUrl = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx";
        public static string RuntimeZipUrl { get { return "https://github.com/microsoft/onnxruntime/releases/download/v" + OrtVersion + "/onnxruntime-win-x64-" + OrtVersion + ".zip"; } }
        const long ModelBytes = 178648008;

        public static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static bool Ready { get { return File.Exists(RuntimePath) && File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 1000000; } }

        /// Downloads the runtime (~7 MB) and the model (~170 MB). progress: 0..1
        public static void Install(Action<double, string> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(RuntimePath))
            {
                string zip = Path.Combine(Dir, "ort.zip");
                Net.Download(RuntimeZipUrl, zip, (d, t) => { if (progress != null) progress(t > 0 ? 0.04 * d / t : 0, L.T("bg_dl_engine")); }, cancelled);
                using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(zip))
                    foreach (System.IO.Compression.ZipArchiveEntry e in z.Entries)
                        if (e.Name.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
                            using (Stream s = e.Open()) using (FileStream f = File.Create(RuntimePath)) s.CopyTo(f);
                try { File.Delete(zip); } catch { }
                if (!File.Exists(RuntimePath)) throw new Exception("onnxruntime.dll not found in the download");
            }
            if (!File.Exists(ModelPath) || new FileInfo(ModelPath).Length < 1000000)
            {
                string tmp = ModelPath + ".part";
                Net.Download(ModelUrl, tmp, (d, t) =>
                {
                    if (progress != null) progress(0.04 + 0.96 * d / (double)(t > 0 ? t : ModelBytes), L.T("bg_dl_model", Theme.HumanSize(d), Theme.HumanSize(t > 0 ? t : ModelBytes)));
                }, cancelled);
                if (File.Exists(ModelPath)) File.Delete(ModelPath);
                File.Move(tmp, ModelPath);
            }
        }

        // ------------------------------------------------------------------ native loading
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
        [DllImport("libdl.so.2")] static extern IntPtr dlopen(string path, int flags);
        [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr h, string name);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetApiBaseFn();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetApiFn(uint version);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetErrorMessageFn(IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateEnvFn(int level, string logid, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateSessionOptionsFn(out IntPtr opts);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr SetIntFn(IntPtr opts, int v);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateSessionFn(IntPtr env, IntPtr path, IntPtr opts, out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetAllocatorFn(out IntPtr alloc);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetNameFn(IntPtr session, UIntPtr index, IntPtr alloc, out IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr AllocatorFreeFn(IntPtr alloc, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateCpuMemoryInfoFn(int type, int memType, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateTensorFn(IntPtr info, IntPtr data, UIntPtr bytes, long[] shape, UIntPtr shapeLen, int type, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr RunFn(IntPtr session, IntPtr runOpts, IntPtr[] inNames, IntPtr[] inputs, UIntPtr inLen, IntPtr[] outNames, UIntPtr outLen, IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetDataFn(IntPtr value, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetShapeInfoFn(IntPtr value, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetDimCountFn(IntPtr info, out UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetDimsFn(IntPtr info, [Out] long[] dims, UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ReleaseFn(IntPtr p);

        // OrtApi function table positions (append-only across versions; checked against the 1.20 header)
        const int I_GetErrorMessage = 2, I_CreateEnv = 3, I_CreateSession = 7, I_Run = 9, I_CreateSessionOptions = 10,
            I_SetGraphOpt = 23, I_SetIntraOp = 24, I_GetInputName = 36, I_GetOutputName = 37, I_CreateTensor = 49,
            I_GetTensorData = 51, I_GetDimCount = 61, I_GetDims = 62, I_GetTypeShape = 65, I_CreateCpuMemInfo = 69,
            I_AllocatorFree = 76, I_GetAllocator = 78, I_ReleaseEnv = 92, I_ReleaseStatus = 93, I_ReleaseMemInfo = 94,
            I_ReleaseValue = 96, I_ReleaseShapeInfo = 99, I_ReleaseSessionOptions = 100;

        static IntPtr api, env, session, memInfo;
        static string inputName, outputName;
        static readonly object gate = new object();

        static T Fn<T>(int index) where T : class
        {
            IntPtr p = Marshal.ReadIntPtr(api, index * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static void Check(IntPtr status)
        {
            if (status == IntPtr.Zero) return;
            string msg = Marshal.PtrToStringAnsi(Fn<GetErrorMessageFn>(I_GetErrorMessage)(status));
            Fn<ReleaseFn>(I_ReleaseStatus)(status);
            throw new Exception("ONNX Runtime: " + msg);
        }

        static void EnsureLoaded()
        {
            if (session != IntPtr.Zero) return;
            IntPtr lib, sym;
            if (IsWindows)
            {
                SetDllDirectoryW(Dir);
                lib = LoadLibraryW(RuntimePath);
                if (lib == IntPtr.Zero) throw new Exception(L.T("bg_err_load", Marshal.GetLastWin32Error()));
                sym = GetProcAddress(lib, "OrtGetApiBase");
            }
            else
            {
                lib = dlopen(RuntimePath, 2);
                if (lib == IntPtr.Zero) throw new Exception("can't load " + RuntimePath);
                sym = dlsym(lib, "OrtGetApiBase");
            }
            if (sym == IntPtr.Zero) throw new Exception("OrtGetApiBase missing");
            IntPtr baseApi = ((GetApiBaseFn)Marshal.GetDelegateForFunctionPointer(sym, typeof(GetApiBaseFn)))();
            GetApiFn getApi = (GetApiFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(baseApi, 0), typeof(GetApiFn));
            api = getApi(20);
            if (api == IntPtr.Zero) api = getApi(17);
            if (api == IntPtr.Zero) throw new Exception("ONNX Runtime API version not supported");

            Check(Fn<CreateEnvFn>(I_CreateEnv)(3 /* error */, "FadiTools", out env));
            IntPtr opts;
            Check(Fn<CreateSessionOptionsFn>(I_CreateSessionOptions)(out opts));
            Check(Fn<SetIntFn>(I_SetIntraOp)(opts, Math.Max(1, Math.Min(8, Environment.ProcessorCount))));
            Check(Fn<SetIntFn>(I_SetGraphOpt)(opts, 99));
            IntPtr pathPtr = IsWindows ? Marshal.StringToHGlobalUni(ModelPath) : Marshal.StringToHGlobalAnsi(ModelPath);
            try { Check(Fn<CreateSessionFn>(I_CreateSession)(env, pathPtr, opts, out session)); }
            finally { Marshal.FreeHGlobal(pathPtr); Fn<ReleaseFn>(I_ReleaseSessionOptions)(opts); }

            IntPtr alloc, name;
            Check(Fn<GetAllocatorFn>(I_GetAllocator)(out alloc));
            Check(Fn<GetNameFn>(I_GetInputName)(session, UIntPtr.Zero, alloc, out name));
            inputName = Marshal.PtrToStringAnsi(name); Fn<AllocatorFreeFn>(I_AllocatorFree)(alloc, name);
            Check(Fn<GetNameFn>(I_GetOutputName)(session, UIntPtr.Zero, alloc, out name));
            outputName = Marshal.PtrToStringAnsi(name); Fn<AllocatorFreeFn>(I_AllocatorFree)(alloc, name);
            Check(Fn<CreateCpuMemoryInfoFn>(I_CreateCpuMemInfo)(1 /* arena */, 0 /* default */, out memInfo));
        }

        // ------------------------------------------------------------------ inference
        const int S = 1024;

        /// Returns a soft mask (0..255 per pixel, same size as the image).
        public static byte[] Mask(Bitmap img)
        {
            lock (gate)
            {
                EnsureLoaded();
                float[] input = new float[3 * S * S];
                using (Bitmap small = new Bitmap(S, S, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.Clear(Color.Black);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(img, new Rectangle(0, 0, S, S));
                    }
                    BitmapData bd = small.LockBits(new Rectangle(0, 0, S, S), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    byte[] px = new byte[S * S * 4];
                    Marshal.Copy(bd.Scan0, px, 0, px.Length);
                    small.UnlockBits(bd);
                    int max = 1;
                    for (int i = 0; i < S * S; i++) { int o = i * 4; max = Math.Max(max, Math.Max(px[o], Math.Max(px[o + 1], px[o + 2]))); }
                    float inv = 1f / max;
                    for (int i = 0; i < S * S; i++)
                    {
                        int o = i * 4;   // BGRA
                        input[i] = px[o + 2] * inv - 0.485f;
                        input[S * S + i] = px[o + 1] * inv - 0.456f;
                        input[2 * S * S + i] = px[o] * inv - 0.406f;
                    }
                }

                float[] pred = new float[S * S];
                GCHandle h = GCHandle.Alloc(input, GCHandleType.Pinned);
                IntPtr tensor = IntPtr.Zero;
                IntPtr[] outputs = new IntPtr[1];
                IntPtr inName = Marshal.StringToHGlobalAnsi(inputName), outName = Marshal.StringToHGlobalAnsi(outputName);
                try
                {
                    Check(Fn<CreateTensorFn>(I_CreateTensor)(memInfo, h.AddrOfPinnedObject(), (UIntPtr)(input.Length * 4), new long[] { 1, 3, S, S }, (UIntPtr)4, 1 /* float */, out tensor));
                    Check(Fn<RunFn>(I_Run)(session, IntPtr.Zero, new[] { inName }, new[] { tensor }, (UIntPtr)1, new[] { outName }, (UIntPtr)1, outputs));
                    IntPtr data;
                    Check(Fn<GetDataFn>(I_GetTensorData)(outputs[0], out data));
                    IntPtr shapeInfo; UIntPtr dc;
                    Check(Fn<GetShapeInfoFn>(I_GetTypeShape)(outputs[0], out shapeInfo));
                    Check(Fn<GetDimCountFn>(I_GetDimCount)(shapeInfo, out dc));
                    long[] dims = new long[(int)dc];
                    Check(Fn<GetDimsFn>(I_GetDims)(shapeInfo, dims, dc));
                    Fn<ReleaseFn>(I_ReleaseShapeInfo)(shapeInfo);
                    long n = 1; foreach (long d in dims) n *= d;
                    if (n < S * S) throw new Exception("unexpected model output");
                    Marshal.Copy(data, pred, 0, S * S);   // first channel of the first output
                }
                finally
                {
                    h.Free();
                    Marshal.FreeHGlobal(inName); Marshal.FreeHGlobal(outName);
                    if (tensor != IntPtr.Zero) Fn<ReleaseFn>(I_ReleaseValue)(tensor);
                    if (outputs[0] != IntPtr.Zero) Fn<ReleaseFn>(I_ReleaseValue)(outputs[0]);
                }

                // normalise to 0..1 like rembg does, then scale back up to the photo size
                float mn = float.MaxValue, mx = float.MinValue;
                foreach (float v in pred) { if (v < mn) mn = v; if (v > mx) mx = v; }
                float range = Math.Max(1e-6f, mx - mn);
                byte[] m8 = new byte[S * S * 4];
                for (int i = 0; i < S * S; i++)
                {
                    int a = (int)Math.Round((pred[i] - mn) / range * 255);
                    m8[i * 4] = m8[i * 4 + 1] = m8[i * 4 + 2] = (byte)Math.Max(0, Math.Min(255, a));
                    m8[i * 4 + 3] = 255;
                }
                int W = img.Width, H = img.Height;
                byte[] mask = new byte[W * H];
                using (Bitmap mb = new Bitmap(S, S, PixelFormat.Format32bppArgb))
                {
                    BitmapData bd = mb.LockBits(new Rectangle(0, 0, S, S), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    Marshal.Copy(m8, 0, bd.Scan0, m8.Length);
                    mb.UnlockBits(bd);
                    using (Bitmap big = EditSession.Scale(mb, W, H))
                    {
                        BitmapData bb = big.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        byte[] row = new byte[W * 4];
                        for (int y = 0; y < H; y++)
                        {
                            Marshal.Copy(new IntPtr(bb.Scan0.ToInt64() + (long)y * bb.Stride), row, 0, row.Length);
                            for (int x = 0; x < W; x++) mask[y * W + x] = row[x * 4];
                        }
                        big.UnlockBits(bb);
                    }
                }
                return mask;
            }
        }

        /// Applies a mask as transparency. A light curve cleans faint haze while keeping soft hair edges.
        public static Bitmap Cutout(Bitmap img, byte[] mask)
        {
            int W = img.Width, H = img.Height;
            Bitmap outp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(outp)) g.DrawImage(img, new Rectangle(0, 0, W, H));
            BitmapData bd = outp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            byte[] row = new byte[W * 4];
            for (int y = 0; y < H; y++)
            {
                IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride);
                Marshal.Copy(p, row, 0, row.Length);
                for (int x = 0; x < W; x++)
                {
                    float a = mask[y * W + x] / 255f;
                    a = Math.Max(0, Math.Min(1, (a - 0.08f) / 0.62f));
                    row[x * 4 + 3] = (byte)(row[x * 4 + 3] * a);
                }
                Marshal.Copy(row, 0, p, row.Length);
            }
            outp.UnlockBits(bd);
            return outp;
        }
    }
}
