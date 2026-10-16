using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FileConverter
{
    /// Windows integration: install location, shortcuts, right-click menu, file associations, uninstall.
    public static class Shell
    {
        public const string Version = "1.5.1";

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

        public static bool IsInstalledCopy
        {
            get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase); }
        }

        /// Any version was installed before (so engines/settings exist).
        public static bool WasInstalled
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileConverter"))
                    return k != null && (File.Exists(Paths.InstalledExe) || File.Exists(Paths.OldInstalledExe));
            }
        }

        public static bool IsRegistered
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileConverter"))
                    return k != null && File.Exists(Paths.InstalledExe) && string.Equals((k.GetValue("DisplayVersion") as string), Version);
            }
        }

        /// Copies this exe into %LOCALAPPDATA%\Fadi Tools (if needed) and removes leftovers of version 1.
        public static void CopySelf()
        {
            Directory.CreateDirectory(Paths.AppDir);
            if (!IsInstalledCopy)
            {
                // an older installed copy that's still open (under its old or new name) gives way
                foreach (string name in new[] { "FileConverter", "Fadi Tools" })
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            string f = p.MainModule.FileName;
                            if (p.Id != Process.GetCurrentProcess().Id && (string.Equals(f, Paths.InstalledExe, StringComparison.OrdinalIgnoreCase) || string.Equals(f, Paths.OldInstalledExe, StringComparison.OrdinalIgnoreCase)))
                            { p.Kill(); p.WaitForExit(3000); }
                        }
                        catch { }
                    }
                File.Copy(Application.ExecutablePath, Paths.InstalledExe, true);
            }
            RemoveOldCopy();
            foreach (string old in new[] { "Common.ps1", "Converter.ps1", "Viewer.ps1", "Launcher.cs", "Uninstall.ps1", "FileConverter.ico" })
                try { File.Delete(Path.Combine(Paths.AppDir, old)); } catch { }
            try { File.Delete(Path.Combine(StartMenuDir, "Uninstall File Converter.lnk")); } catch { }
        }

        /// Before 1.0.1 the installed copy was called FileConverter.exe: only "Fadi Tools.exe" is kept.
        public static void RemoveOldCopy()
        {
            try
            {
                string old = Paths.OldInstalledExe;
                if (File.Exists(old) && !string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(old), StringComparison.OrdinalIgnoreCase) && File.Exists(Paths.InstalledExe))
                    File.Delete(old);
            }
            catch { }
        }

        const string AppKey = @"Software\Classes\Applications\Fadi Tools.exe", OldAppKey = @"Software\Classes\Applications\FileConverter.exe";

        static string StartMenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Fadi Tools"); } }
        static string OldStartMenuDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "File Converter"); } }

        static void Shortcut(string lnk, string target, string args, string desc)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            try
            {
                object s = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                Type st = s.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, s, new object[] { target });
                st.InvokeMember("Arguments", BindingFlags.SetProperty, null, s, new object[] { args ?? "" });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, s, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, s, new object[] { target + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, s, new object[] { desc });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
                Marshal.FinalReleaseComObject(s);
            }
            finally { Marshal.FinalReleaseComObject(sh); }
        }

        static void SetValue(string key, string name, object value)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                k.SetValue(name ?? "", value);
        }

        public static void Integrate(bool desktop, bool contextMenu, bool viewer)
        {
            string exe = Paths.InstalledExe;
            string q = "\"" + exe + "\"";

            // Shortcuts (the app was called "File Converter" before 4.0: remove the old ones)
            try { if (Directory.Exists(OldStartMenuDir)) Directory.Delete(OldStartMenuDir, true); } catch { }
            foreach (string old in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.SendTo) })
                try { File.Delete(Path.Combine(old, "File Converter.lnk")); } catch { }
            Directory.CreateDirectory(StartMenuDir);
            Shortcut(Path.Combine(StartMenuDir, "Fadi Tools.lnk"), exe, "", "Convert, edit and download files");
            Shortcut(Path.Combine(StartMenuDir, "Image Viewer.lnk"), exe, "/view", "View HEIC, AVIF, WEBP and other photos");
            try { File.Delete(Path.Combine(StartMenuDir, "Image Viewer (HEIC, AVIF, WEBP).lnk")); } catch { }
            string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Fadi Tools.lnk");
            if (desktop) Shortcut(desk, exe, "", "Convert images, audio, video and documents");
            else try { File.Delete(desk); } catch { }
            Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Fadi Tools.lnk"), exe, "", "Convert with Fadi Tools");

            // Right-click "Fadi Tools ▸ Convert / Compress / Compress for Discord / Edit in Studio / Inspect"
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\*\shell\FileConverter.Convert", false);   // the single item of older versions
            Registry.CurrentUser.DeleteSubKeyTree(MenuKey, false);
            if (contextMenu)
            {
                SetValue(MenuKey, "MUIVerb", L.T("app_name"));
                SetValue(MenuKey, "Icon", exe + ",0");
                SetValue(MenuKey, "SubCommands", "");
                string[][] verbs = {
                    new[] { "a_convert", "menu_convert", "convert" },
                    new[] { "b_compress", "menu_compress", "compress" },
                    new[] { "c_discord", "menu_discord", "discord" },
                    new[] { "d_studio", "menu_studio", "studio" },
                    new[] { "e_inspect", "menu_inspect", "inspect" } };
                foreach (string[] v in verbs)
                {
                    string k = MenuKey + @"\shell\" + v[0];
                    SetValue(k, "MUIVerb", L.T(v[1]));
                    SetValue(k, "MultiSelectModel", "Player");
                    SetValue(k + @"\command", null, q + " /do " + v[2] + " \"%1\"");
                }
            }
            // pictures only: "Copy text (Fadi Tools)"
            Registry.CurrentUser.DeleteSubKeyTree(TextKey, false);
            if (contextMenu)
            {
                SetValue(TextKey, "MUIVerb", L.T("menu_text"));
                SetValue(TextKey, "Icon", exe + ",0");
                SetValue(TextKey + @"\command", null, q + " /do text \"%1\"");
            }

            // video projects open in the Studio when double-clicked
            SetValue(@"Software\Classes\." + TrimPage.ProjectExt, null, "FadiTools.Project");
            SetValue(@"Software\Classes\FadiTools.Project", null, L.T("proj_files"));
            SetValue(@"Software\Classes\FadiTools.Project\DefaultIcon", null, exe + ",0");
            SetValue(@"Software\Classes\FadiTools.Project\shell\open\command", null, q + " /do project \"%1\"");

            // Viewer for HEIC/AVIF/WEBP...
            string viewCmd = q + " /view \"%1\"";
            SetValue(@"Software\Classes\FileConverter.Image", null, "Image");
            SetValue(@"Software\Classes\FileConverter.Image\DefaultIcon", null, exe + ",0");
            SetValue(@"Software\Classes\FileConverter.Image\shell\open\command", null, viewCmd);
            Registry.CurrentUser.DeleteSubKeyTree(OldAppKey, false);
            SetValue(AppKey, "FriendlyAppName", L.T("viewer_app_name"));
            SetValue(AppKey + @"\shell\open\command", null, viewCmd);
            foreach (string e in Formats.ViewerExt)
            {
                SetValue(@"Software\Classes\." + e + @"\OpenWithProgids", "FileConverter.Image", "");
                SetValue(AppKey + @"\SupportedTypes", "." + e, "");
                string v = @"Software\Classes\SystemFileAssociations\." + e + @"\shell\FileConverter.View";
                if (viewer)
                {
                    SetValue(v, "MUIVerb", L.T("menu_view"));
                    SetValue(v, "Icon", exe + ",0");
                    SetValue(v + @"\command", null, viewCmd);
                }
                else Registry.CurrentUser.DeleteSubKeyTree(v, false);
            }
            if (viewer)
            {
                // Double-click opens HEIC/HEIF/AVIF in the viewer when no other app was chosen
                foreach (string e in new[] { "heic", "heif", "avif" })
                {
                    using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\." + e))
                    {
                        string cur = k.GetValue("") as string;
                        if (string.IsNullOrEmpty(cur)) k.SetValue("", "FileConverter.Image");
                    }
                }
            }

            // Apps & features entry
            const string un = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileConverter";
            SetValue(un, "DisplayName", "Fadi Tools");
            SetValue(un, "DisplayVersion", Version);
            SetValue(un, "Publisher", "Fadi");
            SetValue(un, "DisplayIcon", exe + ",0");
            SetValue(un, "InstallLocation", Paths.AppDir);
            SetValue(un, "UninstallString", q + " /uninstall");
            SetValue(un, "NoModify", 1);
            SetValue(un, "NoRepair", 1);
            try
            {
                long size = 0;
                foreach (string f in Directory.GetFiles(Paths.AppDir, "*", SearchOption.AllDirectories)) size += new FileInfo(f).Length;
                SetValue(un, "EstimatedSize", (int)(size / 1024));
            }
            catch { }

            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        /// The right-click menu's words in the current language. Only text changes, so Windows needs no refresh.
        public static void UpdateMenuText()
        {
            Action<string, string, string> set = (key, name, value) =>
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key, true))
                    if (k != null) k.SetValue(name ?? "", value);
            };
            set(MenuKey, "MUIVerb", L.T("app_name"));
            foreach (string[] v in new[] { new[] { "a_convert", "menu_convert" }, new[] { "b_compress", "menu_compress" }, new[] { "c_discord", "menu_discord" },
                                           new[] { "d_studio", "menu_studio" }, new[] { "e_inspect", "menu_inspect" } })
                set(MenuKey + @"\shell\" + v[0], "MUIVerb", L.T(v[1]));
            set(TextKey, "MUIVerb", L.T("menu_text"));
            set(@"Software\Classes\FadiTools.Project", null, L.T("proj_files"));
            set(AppKey, "FriendlyAppName", L.T("viewer_app_name"));
            foreach (string e in Formats.ViewerExt) set(@"Software\Classes\SystemFileAssociations\." + e + @"\shell\FileConverter.View", "MUIVerb", L.T("menu_view"));
        }

        const string MenuKey = @"Software\Classes\*\shell\FadiTools";
        const string TextKey = @"Software\Classes\SystemFileAssociations\image\shell\FadiTools.Text";

        static MessageBoxOptions Rtl { get { return Theme.RTL ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0; } }

        public static bool Uninstall(IWin32Window owner)
        {
            if (!Ask.Confirm(owner, L.T("uninstall_title"), L.T("uninstall_q"), L.T("uninstall_btn"), L.T("cancel"), AskKind.Danger)) return false;

            RegistryKey cu = Registry.CurrentUser;
            foreach (string k in new[] { @"Software\Classes\*\shell\FileConverter.Convert", MenuKey, TextKey, @"Software\Classes\FadiTools.Project", @"Software\Classes\." + TrimPage.ProjectExt, @"Software\Classes\FileConverter.Image",
                                         AppKey, OldAppKey, @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileConverter" })
                try { cu.DeleteSubKeyTree(k, false); } catch { }
            foreach (string e in Formats.ViewerExt)
            {
                try { cu.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\." + e + @"\shell\FileConverter.View", false); } catch { }
                using (RegistryKey k = cu.OpenSubKey(@"Software\Classes\." + e + @"\OpenWithProgids", true))
                    if (k != null) try { k.DeleteValue("FileConverter.Image", false); } catch { }
                using (RegistryKey k = cu.OpenSubKey(@"Software\Classes\." + e, true))
                    if (k != null && (k.GetValue("") as string) == "FileConverter.Image") try { k.DeleteValue("", false); } catch { }
            }
            foreach (string f in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "File Converter.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "File Converter.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Fadi Tools.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Fadi Tools.lnk") })
                try { File.Delete(f); } catch { }
            try { Directory.Delete(StartMenuDir, true); } catch { }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);

            // The running exe can't delete itself: let a hidden cmd remove the folder after we exit.
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 3 /nobreak >nul & rd /s /q \"" + Paths.AppDir + "\" & rd /s /q \"" + Paths.OldAppDir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WorkingDirectory = Path.GetTempPath();
            Process.Start(psi);
            Ask.Info(owner, L.T("uninstall_title"), L.T("uninstalled"));
            return true;
        }

        /// Sizes a window, shrinking it to fit small or highly scaled screens.
        public static void FitToScreen(Form f, Size preferred, Size minimum)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int chromeW = f.Width - f.ClientSize.Width, chromeH = f.Height - f.ClientSize.Height;
            int maxW = wa.Width - chromeW - Theme.S(8), maxH = wa.Height - chromeH - Theme.S(8);
            Size c = new Size(Math.Min(preferred.Width, maxW), Math.Min(preferred.Height, maxH));
            f.MinimumSize = new Size(Math.Min(minimum.Width, c.Width) + chromeW, Math.Min(minimum.Height, c.Height) + chromeH);
            f.ClientSize = c;
        }

        /// Makes the built-in web preview use the modern IE11 engine instead of IE7 mode.
        public static void SetBrowserEmulation()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION"))
                    k.SetValue(Path.GetFileName(Application.ExecutablePath), 11001, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool IsInstalled { get { return File.Exists(Paths.InstalledExe); } }
    }
}
