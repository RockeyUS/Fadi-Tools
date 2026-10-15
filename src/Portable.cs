using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

namespace FileConverter
{
    /// Packs the app and its engines into a .zip that runs anywhere after unzipping — nothing to install.
    public static class PortableZip
    {
        public static void Make(string zipPath, Action<string, double> progress, Func<bool> cancelled)
        {
            string root = "Fadi Tools/";
            string tmp = zipPath + ".part";
            using (FileStream fs = new FileStream(tmp, FileMode.Create))
            using (ZipArchive z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Add(z, Application.ExecutablePath, root + "Fadi Tools.exe");
                Text(z, root + Paths.PortableMarker, "This file makes Fadi Tools portable: settings and engines stay in the \"data\" folder next to it.\r\nDelete it if you'd rather install Fadi Tools on this PC.\r\n");
                Text(z, root + "Read me.txt",
                    "Fadi Tools — ready to use\r\n\r\n1. Unzip this folder anywhere (Desktop, USB stick…).\r\n2. Open \"Fadi Tools.exe\".\r\n\r\nNothing is installed: everything stays in this folder. Delete the folder to remove it.\r\n" +
                    "Windows may ask \"Run anyway?\" the first time — click More info → Run anyway.\r\n\r\n" +
                    "أدوات فادي — جاهزة للاستخدام\r\n\r\n١. فك ضغط هذا المجلد في أي مكان.\r\n٢. افتح \"Fadi Tools.exe\".\r\n\r\nلا يتم تثبيت أي شيء: كل شيء يبقى داخل هذا المجلد. احذف المجلد لإزالته.\r\n");
                // the engines (FFmpeg, ImageMagick, yt-dlp, Arabic fonts) so nothing needs downloading
                string tools = Paths.ToolsDir;
                if (Directory.Exists(tools))
                {
                    string[] files = Directory.GetFiles(tools, "*", SearchOption.AllDirectories);
                    long total = 0, done = 0;
                    foreach (string f in files) if (Wanted(tools, f)) total += new FileInfo(f).Length;
                    foreach (string f in files)
                    {
                        if (cancelled != null && cancelled()) throw new OperationCanceledException();
                        if (!Wanted(tools, f)) continue;
                        string rel = f.Substring(tools.Length).TrimStart('\\', '/').Replace('\\', '/');
                        if (progress != null) progress(Path.GetFileName(f), total > 0 ? done / (double)total : 0);
                        Add(z, f, root + "data/tools/" + rel);
                        done += new FileInfo(f).Length;
                    }
                }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
        }

        /// Everything except the large AI model (it downloads by itself when first used).
        static bool Wanted(string tools, string f)
        {
            string rel = f.Substring(tools.Length).TrimStart('\\', '/').ToLowerInvariant().Replace('\\', '/');
            if (rel.StartsWith("bg") || rel.StartsWith("whisper/models") || rel.EndsWith(".part") || rel.EndsWith(".tmp")) return false;
            // programs the app never runs, and long release notes
            if (rel == "ffmpeg/ffplay.exe" || rel == "imagemagick/changelog.md") return false;
            foreach (string x in Tools.UnusedMagick) if (rel == "imagemagick/" + x + ".exe") return false;
            return true;
        }

        static void Add(ZipArchive z, string file, string name)
        {
            ZipArchiveEntry e = z.CreateEntry(name, CompressionLevel.Optimal);
            e.LastWriteTime = File.GetLastWriteTime(file);
            using (Stream w = e.Open()) using (FileStream r = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) r.CopyTo(w);
        }

        static void Text(ZipArchive z, string name, string text)
        {
            ZipArchiveEntry e = z.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream w = e.Open()) { byte[] b = new UTF8Encoding(true).GetBytes(text); w.Write(b, 0, b.Length); }
        }
    }
}
