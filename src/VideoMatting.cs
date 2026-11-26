using System;
using System.Collections.Generic;
using System.IO;

namespace FileConverter
{
    /// Robust Video Matting (RVM, mobilenetv3): background removal made for video. It remembers the earlier frames,
    /// so hair and edges stay steady from frame to frame (the photo remover worked on each frame alone and flickered).
    /// About 15 MB, downloaded the first time (asked first), next to the background remover's AI runtime.
    /// GPL-3.0 model by Peter Lin et al. (github.com/PeterL1n/RobustVideoMatting), downloaded, not built in.
    public static class Rvm
    {
        public const string ModelUrl = "https://github.com/PeterL1n/RobustVideoMatting/releases/download/v1.0.0/rvm_mobilenetv3_fp32.onnx";
        const long ModelBytes = 14975696;
        public static string ModelPath
        {
            get
            {
                string t = Environment.GetEnvironmentVariable("FADI_RVM");
                return !string.IsNullOrEmpty(t) && File.Exists(t) ? t : Path.Combine(BgRemover.Dir, "rvm_mobilenetv3_fp32.onnx");
            }
        }
        public static bool Ready { get { return OnnxNet.RuntimeReady && File.Exists(ModelPath) && new FileInfo(ModelPath).Length > 1000000; } }

        /// The runtime (~7 MB, when it isn't here yet) and the model (~15 MB). progress: 0..1
        public static void Install(Action<double, string> progress, Func<bool> cancelled)
        {
            BgRemover.InstallRuntime(f => { if (progress != null) progress(0.3 * f, L.T("bg_dl_engine")); }, cancelled);
            if (Ready) return;
            string tmp = ModelPath + ".part";
            Net.Download(ModelUrl, tmp, (d, t) =>
            {
                long all = t > 0 ? t : ModelBytes;
                if (progress != null) progress(0.3 + 0.7 * d / (double)all, L.T("bg_dl_model", Theme.HumanSize(d), Theme.HumanSize(all)));
            }, cancelled);
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
            File.Move(tmp, ModelPath);
        }

        static OnnxNet net;
        static readonly object gate = new object();
        static OnnxNet Net0 { get { lock (gate) { if (net == null) net = new OnnxNet(ModelPath); return net; } } }

        /// Mattes one video, frame after frame (keep one per video: it carries what it learnt from the earlier frames).
        public sealed class Matter
        {
            readonly int W, H;
            readonly float ratio;
            Tensor r1, r2, r3, r4;
            readonly float[] src;

            public Matter(int w, int h)
            {
                W = w; H = h;
                // the model looks at a smaller copy (about 512 px on the long side) and refines the edges at full size
                ratio = (float)Math.Min(1.0, 512.0 / Math.Max(w, h));
                r1 = r2 = r3 = r4 = Tensor.Zeros(1, 1, 1, 1);
                src = new float[3 * w * h];
            }

            /// px: the frame as BGRA bytes (W×H). Returns how much of each pixel is the subject (0..1).
            public float[] Next(byte[] px)
            {
                int n = W * H;
                const float k = 1f / 255f;
                for (int i = 0; i < n; i++)
                {
                    int o = i * 4;
                    src[i] = px[o + 2] * k;
                    src[n + i] = px[o + 1] * k;
                    src[2 * n + i] = px[o] * k;
                }
                Dictionary<string, Tensor> inp = new Dictionary<string, Tensor>
                {
                    { "src", new Tensor(src, 1, 3, H, W) },
                    { "r1i", r1 }, { "r2i", r2 }, { "r3i", r3 }, { "r4i", r4 },
                    { "downsample_ratio", new Tensor(new[] { ratio }, 1) }
                };
                Dictionary<string, Tensor> o2 = Net0.Run(inp);
                r1 = o2["r1o"]; r2 = o2["r2o"]; r3 = o2["r3o"]; r4 = o2["r4o"];
                return o2["pha"].Data;
            }
        }
    }
}
