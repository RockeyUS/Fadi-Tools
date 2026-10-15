using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Checks GitHub for a newer Fadi Tools and installs it (download → start the new copy → it replaces this one).
    public static class Updater
    {
        /// Where new versions are published: a GitHub repository ("owner/name") with releases that carry the .exe.
        public const string DefaultRepo = "RockeyUS/fadi-tools";
        public static string Repo { get { string r = Settings.Get("UpdateRepo", ""); return r.Length > 0 ? r : DefaultRepo; } }

        public class Release { public string Version, Url, Notes; public bool Zip; }

        /// The newest published release, or null when none could be found. Throws when the internet can't be reached.
        public static Release Latest()
        {
            string json;
            try { json = Net.GetString("https://api.github.com/repos/" + Repo + "/releases/latest"); }
            catch (System.Net.WebException ex)
            {
                System.Net.HttpWebResponse r = ex.Response as System.Net.HttpWebResponse;
                if (r != null && r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // nothing published yet
                throw;
            }
            Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][^\"]*)\"");
            if (!tag.Success) return null;
            string url = null;
            foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase)) { url = m.Groups[1].Value; break; }
            bool zip = false;
            if (url == null)   // only the portable .zip was attached: take the app out of it
                foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.zip)\"", RegexOptions.IgnoreCase)) { url = m.Groups[1].Value; zip = true; break; }
            if (url == null) return null;
            Match body = Regex.Match(json, "\"body\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            string notes = body.Success ? Regex.Unescape(body.Groups[1].Value).Trim() : "";
            if (notes.Length > 400) notes = notes.Substring(0, 397) + "…";
            return new Release { Version = tag.Groups[1].Value.Trim(), Url = url, Notes = notes, Zip = zip };
        }

        public static Version Ver(string s)
        {
            s = (s ?? "").Trim().TrimStart('v', 'V');
            Match m = Regex.Match(s, "^[0-9]+(\\.[0-9]+){0,3}");
            Version v;
            if (m.Success && Version.TryParse(m.Value.Contains(".") ? m.Value : m.Value + ".0", out v)) return v;
            return new Version(0, 0);
        }

        public static bool IsNewer(string other) { return Ver(other) > Ver(Shell.Version); }

        /// Downloads the new version and starts it; the caller should then close this window.
        public static void Install(Release r, Action<double> progress, Func<bool> cancelled)
        {
            string dir = Path.Combine(Path.GetTempPath(), "FadiToolsUpdate");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "Fadi Tools.exe");
            string part = exe + ".part";
            if (r.Zip)
            {
                string zp = Path.Combine(dir, "update.zip.part");
                Net.Download(r.Url, zp, (got, total) => { if (progress != null && total > 0) progress(got / (double)total); }, cancelled);
                try
                {
                    using (System.IO.Compression.ZipArchive z = System.IO.Compression.ZipFile.OpenRead(zp))
                    {
                        System.IO.Compression.ZipArchiveEntry best = null;
                        foreach (System.IO.Compression.ZipArchiveEntry e in z.Entries)
                        {
                            if (!e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || e.FullName.Replace('\\', '/').Contains("/data/")) continue;
                            if (best == null || e.Name.StartsWith("Fadi", StringComparison.OrdinalIgnoreCase)) best = e;
                        }
                        if (best == null) throw new IOException(L.T("upd_bad_file"));
                        using (Stream src = best.Open()) using (FileStream dst = File.Create(part)) src.CopyTo(dst);
                    }
                }
                finally { try { File.Delete(zp); } catch { } }
            }
            else Net.Download(r.Url, part, (got, total) => { if (progress != null && total > 0) progress(got / (double)total); }, cancelled);
            using (FileStream fs = File.OpenRead(part))
                if (fs.Length < 100000 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new IOException(L.T("upd_bad_file"));
            // once Fadi Tools is signed, an update must be signed by the same publisher (and valid)
            string mine = Signature.Publisher(Application.ExecutablePath);
            if (mine != null && (!Signature.Valid(part) || Signature.Publisher(part) != mine)) { try { File.Delete(part); } catch { } throw new IOException(L.T("upd_bad_sig")); }
            if (File.Exists(exe)) File.Delete(exe);
            File.Move(part, exe);
            if (Paths.Portable) Process.Start(new ProcessStartInfo(exe, "/replace " + Tools.Quote(Application.ExecutablePath)) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(exe, "/update") { UseShellExecute = false });
        }

        /// Quiet daily check from the main window: shows a toast when something newer is out.
        public static void CheckInBackground(Toast toast, Control ui)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (Settings.Get("UpdateChecked", "") == today || Settings.Get("AutoUpdate", "1") != "1") return;
            Settings.Set("UpdateChecked", today);
            Thread t = new Thread(() =>
            {
                Release r = null;
                try { r = Latest(); } catch { }
                if (r == null || !IsNewer(r.Version)) return;
                try { ui.BeginInvoke((Action)(() => toast.Show(L.T("upd_available", r.Version), true, () => L.T("upd_now"), () => Run(ui.FindForm(), r)))); } catch { }
            });
            t.IsBackground = true; t.Start();
        }

        /// Asks, downloads and switches to the new version.
        public static void Run(Form owner, Release r)
        {
            string msg = L.T("upd_ask", r.Version, Shell.Version) + (r.Notes.Length > 0 ? "\n\n" + r.Notes : "");
            if (!Ask.Confirm(owner, L.T("upd_title"), msg, L.T("upd_now"), L.T("upd_later"))) return;
            Cursor old = Cursor.Current; Cursor.Current = Cursors.WaitCursor;
            string err = null;
            Thread t = new Thread(() => { try { Install(r, null, () => false); } catch (Exception ex) { err = ex.Message; } });
            t.IsBackground = true; t.Start();
            while (t.IsAlive) { Application.DoEvents(); Thread.Sleep(30); }
            Cursor.Current = old;
            if (err != null) { Ask.Info(owner, L.T("upd_failed"), err, AskKind.Warning); return; }
            Application.Exit();
        }
    }

    /// Authenticode checks (Windows' own WinVerifyTrust): is a file properly signed, and by whom.
    public static class Signature
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        class FileInfo2 { public uint cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FileInfo2)); public string path; public IntPtr hFile = IntPtr.Zero; public IntPtr pgKnownSubject = IntPtr.Zero; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        class TrustData
        {
            public uint cbStruct = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(TrustData));
            public IntPtr pPolicyCallbackData = IntPtr.Zero, pSIPClientData = IntPtr.Zero;
            public uint dwUIChoice = 2;            // no UI
            public uint fdwRevocationChecks = 0;
            public uint dwUnionChoice = 1;         // a file
            public IntPtr pFile;
            public uint dwStateAction = 0;
            public IntPtr hWVTStateData = IntPtr.Zero;
            public IntPtr pwszURLReference = IntPtr.Zero;
            public uint dwProvFlags = 0x10;        // don't go online for revocation lists
            public uint dwUIContext = 0;
        }
        [System.Runtime.InteropServices.DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid action, TrustData data);

        public static bool Valid(string file)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            IntPtr fi = IntPtr.Zero;
            try
            {
                FileInfo2 f = new FileInfo2 { path = file };
                fi = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(f));
                System.Runtime.InteropServices.Marshal.StructureToPtr(f, fi, false);
                TrustData d = new TrustData { pFile = fi };
                return WinVerifyTrust(IntPtr.Zero, new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), d) == 0;
            }
            catch { return false; }
            finally { if (fi != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(fi); }
        }

        /// The signer's name, or null when the file isn't signed.
        public static string Publisher(string file)
        {
            try { return System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file).Subject; }
            catch { return null; }
        }
    }
}
