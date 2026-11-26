using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace FileConverter
{
    /// A tensor of floats: its numbers and its shape (e.g. 1×3×H×W).
    public class Tensor
    {
        public float[] Data;
        public long[] Shape;
        public Tensor(float[] data, params long[] shape) { Data = data; Shape = shape; }
        public static Tensor Zeros(params long[] shape) { long n = 1; foreach (long d in shape) n *= d; return new Tensor(new float[n], shape); }
    }

    /// Any ONNX model, run on this PC with the ONNX Runtime the background remover already downloads
    /// (tools\bg\onnxruntime.dll). Several inputs and outputs, all float tensors. Used by the video background
    /// remover (Robust Video Matting) and the click-to-pick cut-out (MobileSAM).
    public sealed class OnnxNet : IDisposable
    {
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
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetCountFn(IntPtr session, out UIntPtr count);
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
            I_SetGraphOpt = 23, I_SetIntraOp = 24, I_GetInputCount = 30, I_GetOutputCount = 31, I_GetInputName = 36, I_GetOutputName = 37,
            I_CreateTensor = 49, I_GetTensorData = 51, I_GetDimCount = 61, I_GetDims = 62, I_GetTypeShape = 65, I_CreateCpuMemInfo = 69,
            I_AllocatorFree = 76, I_GetAllocator = 78, I_ReleaseStatus = 93, I_ReleaseValue = 96, I_ReleaseShapeInfo = 99,
            I_ReleaseSessionOptions = 100, I_ReleaseSession = 95;

        static IntPtr api, env, memInfo;
        static readonly object loadGate = new object();
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        IntPtr session;
        public readonly string[] InputNames, OutputNames;
        readonly object runGate = new object();

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

        /// The runtime (tools\bg\onnxruntime.dll; on Linux libonnxruntime.so there, or FADI_ORT for tests).
        public static string RuntimePath
        {
            get
            {
                string env2 = Environment.GetEnvironmentVariable("FADI_ORT");
                return !string.IsNullOrEmpty(env2) && File.Exists(env2) ? env2 : BgRemover.RuntimePath;
            }
        }
        public static bool RuntimeReady { get { return File.Exists(RuntimePath); } }

        static void LoadRuntime()
        {
            lock (loadGate)
            {
                if (api != IntPtr.Zero) return;
                IntPtr lib, sym;
                if (IsWindows)
                {
                    SetDllDirectoryW(Path.GetDirectoryName(RuntimePath));
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
                IntPtr a = getApi(20);
                if (a == IntPtr.Zero) a = getApi(17);
                if (a == IntPtr.Zero) throw new Exception("ONNX Runtime API version not supported");
                api = a;
                Check(Fn<CreateEnvFn>(I_CreateEnv)(3 /* error */, "FadiToolsNet", out env));
                Check(Fn<CreateCpuMemoryInfoFn>(I_CreateCpuMemInfo)(1 /* arena */, 0, out memInfo));
            }
        }

        public OnnxNet(string modelPath, int threads = 0)
        {
            LoadRuntime();
            IntPtr opts;
            Check(Fn<CreateSessionOptionsFn>(I_CreateSessionOptions)(out opts));
            Check(Fn<SetIntFn>(I_SetIntraOp)(opts, threads > 0 ? threads : Math.Max(1, Math.Min(8, Environment.ProcessorCount))));
            Check(Fn<SetIntFn>(I_SetGraphOpt)(opts, 99));
            IntPtr pathPtr = IsWindows ? Marshal.StringToHGlobalUni(modelPath) : Marshal.StringToHGlobalAnsi(modelPath);
            try { Check(Fn<CreateSessionFn>(I_CreateSession)(env, pathPtr, opts, out session)); }
            finally { Marshal.FreeHGlobal(pathPtr); Fn<ReleaseFn>(I_ReleaseSessionOptions)(opts); }
            IntPtr alloc;
            Check(Fn<GetAllocatorFn>(I_GetAllocator)(out alloc));
            UIntPtr ni, no;
            Check(Fn<GetCountFn>(I_GetInputCount)(session, out ni));
            Check(Fn<GetCountFn>(I_GetOutputCount)(session, out no));
            InputNames = new string[(int)ni]; OutputNames = new string[(int)no];
            for (int i = 0; i < InputNames.Length; i++) { IntPtr name; Check(Fn<GetNameFn>(I_GetInputName)(session, (UIntPtr)i, alloc, out name)); InputNames[i] = Marshal.PtrToStringAnsi(name); Fn<AllocatorFreeFn>(I_AllocatorFree)(alloc, name); }
            for (int i = 0; i < OutputNames.Length; i++) { IntPtr name; Check(Fn<GetNameFn>(I_GetOutputName)(session, (UIntPtr)i, alloc, out name)); OutputNames[i] = Marshal.PtrToStringAnsi(name); Fn<AllocatorFreeFn>(I_AllocatorFree)(alloc, name); }
        }

        /// Runs the model. inputs by name (every input the model has); returns every output by name.
        public Dictionary<string, Tensor> Run(Dictionary<string, Tensor> inputs)
        {
            lock (runGate)
            {
                int n = InputNames.Length;
                IntPtr[] inNames = new IntPtr[n], values = new IntPtr[n];
                GCHandle[] pins = new GCHandle[n];
                IntPtr[] outNames = new IntPtr[OutputNames.Length], outputs = new IntPtr[OutputNames.Length];
                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        Tensor t;
                        if (!inputs.TryGetValue(InputNames[i], out t)) throw new Exception("model input missing: " + InputNames[i]);
                        inNames[i] = Marshal.StringToHGlobalAnsi(InputNames[i]);
                        pins[i] = GCHandle.Alloc(t.Data, GCHandleType.Pinned);
                        Check(Fn<CreateTensorFn>(I_CreateTensor)(memInfo, pins[i].AddrOfPinnedObject(), (UIntPtr)(t.Data.Length * 4), t.Shape, (UIntPtr)t.Shape.Length, 1 /* float */, out values[i]));
                    }
                    for (int i = 0; i < outNames.Length; i++) outNames[i] = Marshal.StringToHGlobalAnsi(OutputNames[i]);
                    Check(Fn<RunFn>(I_Run)(session, IntPtr.Zero, inNames, values, (UIntPtr)n, outNames, (UIntPtr)outNames.Length, outputs));
                    Dictionary<string, Tensor> r = new Dictionary<string, Tensor>();
                    for (int i = 0; i < outputs.Length; i++)
                    {
                        IntPtr shapeInfo; UIntPtr dc;
                        Check(Fn<GetShapeInfoFn>(I_GetTypeShape)(outputs[i], out shapeInfo));
                        Check(Fn<GetDimCountFn>(I_GetDimCount)(shapeInfo, out dc));
                        long[] dims = new long[(int)dc];
                        Check(Fn<GetDimsFn>(I_GetDims)(shapeInfo, dims, dc));
                        Fn<ReleaseFn>(I_ReleaseShapeInfo)(shapeInfo);
                        long count = 1; foreach (long d in dims) count *= d;
                        float[] data = new float[count];
                        IntPtr p;
                        Check(Fn<GetDataFn>(I_GetTensorData)(outputs[i], out p));
                        if (count > 0) Marshal.Copy(p, data, 0, (int)count);
                        r[OutputNames[i]] = new Tensor(data, dims);
                    }
                    return r;
                }
                finally
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (inNames[i] != IntPtr.Zero) Marshal.FreeHGlobal(inNames[i]);
                        if (values[i] != IntPtr.Zero) Fn<ReleaseFn>(I_ReleaseValue)(values[i]);
                        if (pins[i].IsAllocated) pins[i].Free();
                    }
                    for (int i = 0; i < outNames.Length; i++)
                    {
                        if (outNames[i] != IntPtr.Zero) Marshal.FreeHGlobal(outNames[i]);
                        if (outputs[i] != IntPtr.Zero) Fn<ReleaseFn>(I_ReleaseValue)(outputs[i]);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (session != IntPtr.Zero) { try { Fn<ReleaseFn>(I_ReleaseSession)(session); } catch { } session = IntPtr.Zero; }
        }
    }
}
