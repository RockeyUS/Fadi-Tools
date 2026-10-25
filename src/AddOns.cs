using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Everything Fadi Tools downloaded or installed after setup (fonts, AI models, helper programs): where each one
    /// is, how big it is, and how to remove it. Settings → Add-ons lists them.
    public class AddOn
    {
        public string Key, NameKey, SubKey, Glyph;
        public Func<string> Where;          // its folder (null when it isn't there)
        public bool Required;               // the app can't work without it: shown, never removed
        public bool Machine;                // installed for the whole PC (LibreOffice, Node.js): removed through Windows
        public long Size = -1;
        public string Path2 { get { try { return Where(); } catch { return null; } } }
    }

    public static class AddOns
    {
        static string Tools2 { get { return Paths.ToolsDir; } }
        static string Mark(string key) { return Path.Combine(Paths.ToolsDir, key + ".remove"); }
        static bool Marked(string key) { try { return File.Exists(Mark(key)); } catch { return false; } }

        static string IfDir(string d, string key) { return d != null && !Marked(key) && Directory.Exists(d) && HasFiles(d) ? d : null; }
        static bool HasFiles(string d) { try { return Directory.GetFiles(d, "*", SearchOption.AllDirectories).Length > 0; } catch { return false; } }
        static string DirOf(string exe) { return exe == null ? null : System.IO.Path.GetDirectoryName(exe); }

        public static List<AddOn> All()
        {
            return new List<AddOn>
            {
                new AddOn { Key = "ffmpeg", NameKey = "ao_ff", SubKey = "ao_required", Glyph = Ico.Video, Required = true, Where = () => DirOf(Tools.FFmpeg) },
                new AddOn { Key = "imagemagick", NameKey = "ao_im", SubKey = "ao_required", Glyph = Ico.Photo, Required = true, Where = () => DirOf(Tools.Magick) },
                new AddOn { Key = "fonts", NameKey = "ao_fonts", Glyph = Ico.Text, Where = () => IfDir(FontPack.Dir, "fonts") },
                new AddOn { Key = "whisper", NameKey = "ao_whisper", Glyph = "", Where = () => IfDir(Whisper.Dir, "whisper") },
                new AddOn { Key = "bg", NameKey = "ao_bg", Glyph = Ps.Subject, Where = () => BgRemover.Ready ? BgRemover.Dir : null },
                new AddOn { Key = "realesrgan", NameKey = "ao_upscale", Glyph = Ico.Upscale, Where = () => IfDir(Upscaler.Dir, "realesrgan") },
                new AddOn { Key = "qpdf", NameKey = "ao_qpdf", Glyph = Ico.Doc, Where = () => IfDir(Qpdf.Dir, "qpdf") },
                new AddOn { Key = "yt", NameKey = "ao_yt", Glyph = Ico.Download, Where = () => IfDir(Tools.YtDir, "yt") },
                new AddOn { Key = "libreoffice", NameKey = "ao_lo", Glyph = Ico.Doc, Machine = true, Where = () => DirOf(Tools.Soffice) },
                new AddOn { Key = "node", NameKey = "ao_node", Glyph = Ico.Code, Machine = true, Where = () => DirOf(Rea.NodeExe) },
            };
        }

        public static long SizeOf(string dir)
        {
            long n = 0;
            try { foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) try { n += new FileInfo(f).Length; } catch { } } catch { }
            return n;
        }

        /// Removes an add-on (on a background thread). Returns a message for the user, or null when nothing happened.
        public static string Remove(AddOn a)
        {
            string dir = a.Path2;
            if (dir == null || a.Required) return null;
            switch (a.Key)
            {
                case "bg":
                    BgRemover.Uninstall();
                    return BgRemover.Ready ? L.T("ao_failed", L.T(a.NameKey)) : L.T("ao_removed", L.T(a.NameKey));
                case "libreoffice":
                    {
                        string winget = Tools.WinGet, o;
                        if (winget != null) try { Tools.RunHidden(winget, "uninstall -e --id TheDocumentFoundation.LibreOffice --silent --accept-source-agreements", 15 * 60 * 1000, out o); } catch { }
                        Tools.Reset();
                        if (Tools.Soffice == null) return L.T("ao_removed", L.T(a.NameKey));
                        OpenAppsSettings();
                        return L.T("ao_by_hand", L.T(a.NameKey));
                    }
                case "node":
                    {
                        bool declined = !NodeRemover.ByInstaller(dir);
                        string winget = Tools.WinGet, o;
                        if (!declined && Rea.NodeExe != null && winget != null)
                        {
                            try { Tools.RunHidden(winget, "uninstall -e --id OpenJS.NodeJS.LTS --silent --accept-source-agreements", 10 * 60 * 1000, out o); } catch { }
                            if (Rea.NodeExe != null) try { Tools.RunHidden(winget, "uninstall -e --id OpenJS.NodeJS --silent --accept-source-agreements", 10 * 60 * 1000, out o); } catch { }
                        }
                        if (!declined && Rea.NodeExe != null) NodeRemover.DeleteFolder(dir);
                        if (Rea.NodeExe == null) return L.T("ao_removed", L.T(a.NameKey));
                        return declined ? L.T("re_node_declined") : L.T("ao_failed", L.T(a.NameKey));
                    }
            }
            // a folder of Fadi Tools' own: deleted (what's in use goes when the app starts next time)
            try { Directory.Delete(dir, true); }
            catch
            {
                try { foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) try { File.Delete(f); } catch { } } catch { }
                if (Directory.Exists(dir) && HasFiles(dir)) { try { File.WriteAllText(Mark(a.Key), dir); } catch { } return L.T("ao_restart", L.T(a.NameKey)); }
                try { Directory.Delete(dir, true); } catch { }
            }
            return L.T("ao_removed", L.T(a.NameKey));
        }

        /// Finishes removals that had to wait for the app to close (call when it starts, before fonts are loaded).
        public static void FinishRemovals()
        {
            try
            {
                if (!Directory.Exists(Paths.ToolsDir)) return;
                foreach (string m in Directory.GetFiles(Paths.ToolsDir, "*.remove"))
                {
                    string key = System.IO.Path.GetFileNameWithoutExtension(m);
                    if (key == "bg") continue;   // the background remover finishes its own
                    string dir = System.IO.Path.Combine(Paths.ToolsDir, key);
                    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); File.Delete(m); } catch { }
                }
            }
            catch { }
        }

        public static void OpenFolder(string dir) { try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { } }
        static void OpenAppsSettings() { try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); } catch { } }
    }
}
