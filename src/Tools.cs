using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    public static class Paths
    {
        public const string PortableMarker = "FadiTools.portable";
        static int portable = -1;
        /// A copy unzipped anywhere with the marker file beside it: settings and engines live in its own "data" folder.
        public static bool Portable
        {
            get
            {
                if (portable < 0)
                {
                    try { portable = File.Exists(Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), PortableMarker)) ? 1 : 0; }
                    catch { portable = 0; }
                }
                return portable == 1;
            }
        }
        public static string ExeDir { get { return Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath); } }
        public static string AppDir { get { return Portable ? Path.Combine(ExeDir, "data") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileConverter"); } }
        public static string ToolsDir { get { return Path.Combine(AppDir, "tools"); } }
        public static string InstalledExe { get { return Path.Combine(AppDir, "Fadi Tools.exe"); } }
        /// The installed copy's name before 1.0.1 (removed once the new one is in place).
        public static string OldInstalledExe { get { return Path.Combine(AppDir, "FileConverter.exe"); } }
        public static string SettingsFile { get { return Path.Combine(AppDir, "settings.ini"); } }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        public static string Downloads
        {
            get
            {
                try
                {
                    IntPtr p;
                    if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out p) == 0)
                    {
                        string s = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        public static string UniquePath(string dir, string baseName, string ext)
        {
            string p = Path.Combine(dir, baseName + "." + ext);
            int i = 1;
            while (File.Exists(p)) { p = Path.Combine(dir, baseName + " (" + i + ")." + ext); i++; }
            return p;
        }
    }

    public static class Settings
    {
        static Dictionary<string, string> values;

        static void Load()
        {
            if (values != null) return;
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(Paths.SettingsFile))
                    foreach (string line in File.ReadAllLines(Paths.SettingsFile, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch { }
        }

        public static string Get(string key, string def)
        {
            Load();
            string v;
            return values.TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            Load();
            values[key] = value ?? "";
            try
            {
                Directory.CreateDirectory(Paths.AppDir);
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(Paths.SettingsFile, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        /// Where converted files go. Empty setting = the Downloads folder.
        public static string OutputDir
        {
            get
            {
                string d = Get("OutputDir", "");
                if (d == "" || !Directory.Exists(d)) return Paths.Downloads;
                return d;
            }
            set { Set("OutputDir", value); }
        }
        public static bool OpenAfter { get { return Get("OpenAfter", "0") == "1"; } set { Set("OpenAfter", value ? "1" : "0"); } }
        public static bool OutputIsDefault { get { string d = Get("OutputDir", ""); return d == "" || !Directory.Exists(d); } }
    }

    public static class Tools
    {
        static string ffmpeg, magick, soffice;

        public static void Reset() { ffmpeg = magick = soffice = null; }

        static string FindIn(string dir, string exe)
        {
            try
            {
                if (!Directory.Exists(dir)) return null;
                string[] hits = Directory.GetFiles(dir, exe, SearchOption.AllDirectories);
                return hits.Length > 0 ? hits[0] : null;
            }
            catch { return null; }
        }

        public static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(Path.PathSeparator))
            {
                try
                {
                    if (d.Trim() == "") continue;
                    string p = Path.Combine(d.Trim(), exe);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        public static string FFmpeg
        {
            get
            {
                if (ffmpeg == null || !File.Exists(ffmpeg))
                    ffmpeg = FindIn(Path.Combine(Paths.ToolsDir, "ffmpeg"), "ffmpeg.exe") ?? FindIn(Paths.ToolsDir, "ffmpeg.exe") ?? FindOnPath("ffmpeg.exe");
                return ffmpeg;
            }
        }

        public static string Magick
        {
            get
            {
                if (magick == null || !File.Exists(magick))
                {
                    magick = FindIn(Path.Combine(Paths.ToolsDir, "imagemagick"), "magick.exe") ?? FindIn(Paths.ToolsDir, "magick.exe");
                    if (magick == null)
                    {
                        try
                        {
                            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                            foreach (string d in Directory.GetDirectories(pf, "ImageMagick*"))
                            {
                                string p = Path.Combine(d, "magick.exe");
                                if (File.Exists(p)) { magick = p; break; }
                            }
                        }
                        catch { }
                    }
                }
                return magick;
            }
        }

        public static string Soffice
        {
            get
            {
                if (soffice == null || !File.Exists(soffice))
                {
                    soffice = null;
                    foreach (string root in new[] {
                        Environment.GetEnvironmentVariable("ProgramFiles"),
                        Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                        Environment.GetEnvironmentVariable("ProgramW6432") })
                    {
                        if (string.IsNullOrEmpty(root)) continue;
                        string p = Path.Combine(root, @"LibreOffice\program\soffice.com");
                        if (File.Exists(p)) { soffice = p; break; }
                    }
                }
                return soffice;
            }
        }

        public static bool EnginesReady { get { return FFmpeg != null && Magick != null; } }

        /// ImageMagick ships 8 copies of the same 32 MB program and FFmpeg a 107 MB player: only magick.exe,
        /// ffmpeg.exe and ffprobe.exe are used, so the rest is removed (new installs never unpack them).
        public static readonly string[] UnusedMagick = { "compare", "composite", "conjure", "identify", "mogrify", "montage", "stream" };
        public static void Slim()
        {
            try
            {
                string im = Path.Combine(Paths.ToolsDir, "imagemagick");
                if (File.Exists(Path.Combine(im, "magick.exe")))
                    foreach (string x in UnusedMagick) try { File.Delete(Path.Combine(im, x + ".exe")); } catch { }
                string ff = Path.Combine(Paths.ToolsDir, "ffmpeg");
                if (File.Exists(Path.Combine(ff, "ffmpeg.exe"))) try { File.Delete(Path.Combine(ff, "ffplay.exe")); } catch { }
            }
            catch { }
        }

        static string Sibling(string exe, string name)
        {
            if (exe == null) return null;
            string p = Path.Combine(Path.GetDirectoryName(exe), name);
            return File.Exists(p) ? p : null;
        }
        public static string FFprobe { get { return Sibling(FFmpeg, "ffprobe.exe"); } }
        public static string FFplay { get { return Sibling(FFmpeg, "ffplay.exe"); } }

        public static string YtDir { get { return Path.Combine(Paths.ToolsDir, "yt"); } }
        public static string YtDlp { get { string p = Path.Combine(YtDir, "yt-dlp.exe"); return File.Exists(p) ? p : null; } }
        public static string Deno { get { string p = Path.Combine(YtDir, "deno.exe"); return File.Exists(p) ? p : null; } }

        public static string Edge
        {
            get
            {
                foreach (string root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    string p = Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe");
                    if (File.Exists(p)) return p;
                }
                return null;
            }
        }

        public static string WinGet
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
                return File.Exists(p) ? p : null;
            }
        }

        /// Runs a tool hidden and returns exit code; output collected into 'output'.
        public static int RunHidden(string exe, string args, int timeoutMs, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            StringBuilder sb = new StringBuilder();
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    output = "Timed out";
                    return -1;
                }
                p.WaitForExit();
                lock (sb) output = sb.ToString();
                return p.ExitCode;
            }
        }

        public static volatile bool ShuttingDown;

        /// Runs a tool hidden, streaming output lines to callbacks. Returns exit code (-2 if cancelled).
        public static int Stream(string exe, string args, Action<string> onOut, Action<string> onErr, Func<bool> cancelled,
                                 Dictionary<string, string> env = null, Encoding enc = null)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = enc ?? Encoding.UTF8;
            psi.StandardErrorEncoding = enc ?? Encoding.UTF8;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            if (env != null) foreach (KeyValuePair<string, string> kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null && onOut != null) onOut(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null && onErr != null) onErr(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                while (!p.WaitForExit(150))
                {
                    if (ShuttingDown || (cancelled != null && cancelled()))
                    {
                        KillTree(p);
                        p.WaitForExit(3000);
                        return -2;
                    }
                }
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        public static void KillTree(Process p)
        {
            try
            {
                ProcessStartInfo k = new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F");
                k.CreateNoWindow = true; k.UseShellExecute = false;
                using (Process kp = Process.Start(k)) kp.WaitForExit(3000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public static string Quote(string s)
        {
            if (s.EndsWith("\\")) s += "\\";
            return "\"" + s + "\"";
        }

        public static string LastLines(string text, int n)
        {
            if (string.IsNullOrEmpty(text)) return "unknown error";
            List<string> lines = new List<string>();
            foreach (string l in text.Split('\n')) if (l.Trim() != "") lines.Add(l.Trim());
            if (lines.Count == 0) return "unknown error";
            int start = Math.Max(0, lines.Count - n);
            return string.Join("  ", lines.GetRange(start, lines.Count - start).ToArray());
        }
    }

    /// Downloads with progress, following redirects. Runs on a worker thread.
    public static class Net
    {
        public static void Init()
        {
            try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288; }
            catch { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
            // Windows apps may only open 2 connections per server by default: too few for split downloads
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 32);
            ServicePointManager.Expect100Continue = false;
        }

        static HttpWebRequest Req(string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FadiTools/1.0";
            req.AllowAutoRedirect = true;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            return req;
        }

        public static string GetString(string url)
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "FadiTools/1.0";
                wc.Encoding = Encoding.UTF8;
                return wc.DownloadString(url);
            }
        }

        /// Big files come down over several connections at once (much faster from far-away servers);
        /// servers that can't do that get the plain one-connection download.
        public static void Download(string url, string dest, Action<long, long> progress, Func<bool> cancelled)
        {
            try { if (DownloadParts(url, dest, progress, cancelled)) return; }
            catch (OperationCanceledException) { throw; }
            catch { if (cancelled != null && cancelled()) throw new OperationCanceledException(); }
            DownloadOne(url, dest, progress, cancelled);
        }

        const long SplitAbove = 6L << 20;

        static bool DownloadParts(string url, string dest, Action<long, long> progress, Func<bool> cancelled)
        {
            // one byte first: the size, whether parts are allowed, and the real address after redirects
            long total; string final;
            HttpWebRequest probe = Req(url);
            probe.AddRange(0L, 0L);
            using (HttpWebResponse r = (HttpWebResponse)probe.GetResponse())
            {
                if (r.StatusCode != HttpStatusCode.PartialContent) return false;
                string cr = r.Headers["Content-Range"];
                int slash = cr == null ? -1 : cr.LastIndexOf('/');
                if (slash < 0 || !long.TryParse(cr.Substring(slash + 1), out total)) return false;
                final = r.ResponseUri.AbsoluteUri;
            }
            if (total < SplitAbove) return false;
            int n = total > (64L << 20) ? 8 : total > (24L << 20) ? 6 : 4;
            long size = total / n;
            long[] got = new long[n];
            Exception[] error = new Exception[1];
            bool[] stop = new bool[1];
            using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) fs.SetLength(total);
            System.Threading.Thread[] parts = new System.Threading.Thread[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                long from = k * size, to = k == n - 1 ? total - 1 : (k + 1) * size - 1;
                parts[k] = new System.Threading.Thread(() =>
                {
                    int tries = 0;
                    using (FileStream fs = new FileStream(dest, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                        while (true)
                        {
                            long pos = from + System.Threading.Interlocked.Read(ref got[k]);
                            if (pos > to || System.Threading.Volatile.Read(ref stop[0])) return;
                            try
                            {
                                HttpWebRequest q = Req(final);
                                q.AddRange(pos, to);
                                using (HttpWebResponse r = (HttpWebResponse)q.GetResponse())
                                {
                                    if (r.StatusCode != HttpStatusCode.PartialContent) throw new IOException("The server sent the whole file instead of a part");
                                    using (Stream s = r.GetResponseStream())
                                    {
                                        fs.Position = pos;
                                        byte[] buf = new byte[1 << 16];
                                        int c;
                                        while ((c = s.Read(buf, 0, buf.Length)) > 0)
                                        {
                                            if (System.Threading.Volatile.Read(ref stop[0])) return;
                                            long room = to - fs.Position + 1;
                                            if (c > room) c = (int)room;
                                            fs.Write(buf, 0, c);
                                            System.Threading.Interlocked.Add(ref got[k], c);
                                            if (fs.Position > to) break;
                                        }
                                    }
                                }
                                if (from + System.Threading.Interlocked.Read(ref got[k]) <= to) throw new IOException("Download was interrupted");
                                return;
                            }
                            catch (Exception ex)
                            {
                                // a dropped connection carries on from where it stopped (a few times)
                                if (++tries >= 4) { lock (error) if (error[0] == null) error[0] = ex; System.Threading.Volatile.Write(ref stop[0], true); return; }
                                System.Threading.Thread.Sleep(400 * tries);
                            }
                        }
                }) { IsBackground = true };
                parts[k].Start();
            }
            DateTime last = DateTime.MinValue;
            while (true)
            {
                bool alive = false;
                foreach (System.Threading.Thread t in parts) if (t.IsAlive) alive = true;
                long sum = 0;
                for (int i = 0; i < n; i++) sum += System.Threading.Interlocked.Read(ref got[i]);
                if (progress != null && (!alive || (DateTime.Now - last).TotalMilliseconds > 120)) { last = DateTime.Now; progress(sum, total); }
                if (!alive) break;
                if (cancelled != null && cancelled())
                {
                    System.Threading.Volatile.Write(ref stop[0], true);
                    foreach (System.Threading.Thread t in parts) t.Join(5000);
                    throw new OperationCanceledException();
                }
                System.Threading.Thread.Sleep(100);
            }
            if (error[0] != null) throw error[0];
            long all = 0;
            for (int i = 0; i < n; i++) all += got[i];
            if (all != total) throw new IOException("Download was interrupted");
            return true;
        }

        static void DownloadOne(string url, string dest, Action<long, long> progress, Func<bool> cancelled)
        {
            HttpWebRequest req = Req(url);
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                long total = resp.ContentLength;
                long got = 0;
                byte[] buf = new byte[1 << 16];
                int n;
                DateTime last = DateTime.MinValue;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    fs.Write(buf, 0, n);
                    got += n;
                    if ((DateTime.Now - last).TotalMilliseconds > 120) { last = DateTime.Now; if (progress != null) progress(got, total); }
                }
                if (progress != null) progress(got, total);
                if (total > 0 && got < total) throw new IOException("Download was interrupted");
            }
        }

        /// Finds a release asset URL on GitHub matching a pattern (null if not reachable).
        public static string GitHubAsset(string repo, string pattern)
        {
            try
            {
                string json = GetString("https://api.github.com/repos/" + repo + "/releases/latest");
                foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\""))
                    if (Regex.IsMatch(m.Groups[1].Value, pattern, RegexOptions.IgnoreCase)) return m.Groups[1].Value;
            }
            catch { }
            return null;
        }
    }
}
