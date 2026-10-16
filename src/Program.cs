using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Keeps one main window: files opened later (right-click, Send to) are handed to it.
    public static class SingleInstance
    {
        static Mutex mutex;
        /// Portable copies keep to themselves (each folder is its own app).
        static string Tag { get { return Paths.Portable ? "FadiTools_" + (uint)Paths.ExeDir.ToLowerInvariant().GetHashCode() : "FileConverter"; } }
        static string PipeName { get { return Tag + "_" + Environment.UserName; } }
        /// files, and what to do with them (null = convert; "compress", "discord", "studio", "inspect"...)
        public static event Action<string[], string> FilesReceived;

        /// True when a main File Converter window is already open.
        public static bool MainRunning
        {
            get
            {
                Mutex m;
                if (Mutex.TryOpenExisting(@"Local\" + Tag + "_Main", out m)) { m.Dispose(); return true; }
                return false;
            }
        }

        public static void Release() { try { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); mutex = null; } } catch { } }

        public static bool TryBecomeFirst()
        {
            bool created;
            mutex = new Mutex(true, @"Local\" + Tag + "_Main", out created);
            return created;
        }

        public static void Listen()
        {
            Thread t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (NamedPipeServerStream server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (StreamReader r = new StreamReader(server, Encoding.UTF8))
                            {
                                List<string> files = new List<string>();
                                string line, verb = null;
                                while ((line = r.ReadLine()) != null)
                                {
                                    if (line.StartsWith("@do:")) { verb = line.Substring(4).Trim(); continue; }
                                    if (line.Trim() != "") files.Add(line);
                                }
                                if (FilesReceived != null) FilesReceived(files.ToArray(), verb);
                            }
                        }
                    }
                    catch { Thread.Sleep(200); }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public static bool Send(string[] files) { return Send(files, null); }

        public static bool Send(string[] files, string verb)
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        c.Connect(500);
                        using (StreamWriter w = new StreamWriter(c, new UTF8Encoding(false)))
                        {
                            if (!string.IsNullOrEmpty(verb)) w.WriteLine("@do:" + verb);
                            foreach (string f in files) w.WriteLine(Path.GetFullPath(f));
                            w.Flush();
                        }
                        return true;
                    }
                }
                catch { Thread.Sleep(150); }
            }
            return false;
        }
    }

    public static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);

        /// Sharp on every screen: the app follows each screen's own size setting (per-monitor, Windows 10 1703+),
        /// and falls back to the older ways on older Windows.
        static void DpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }   // per monitor (v2)
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }                  // per monitor (8.1)
            try { SetProcessDPIAware(); } catch { }                                         // whole system (7)
        }

        [STAThread]
        public static int Main(string[] args)
        {
            DpiAware();
            // an update: let the old window finish closing before its folder is moved
            if (args.Length > 0 && args[0].Equals("/update", StringComparison.OrdinalIgnoreCase))
                for (int i = 0; i < 100 && SingleInstance.MainRunning; i++) Thread.Sleep(150);
            // "/selftest folder" (the build check) and "/screenshots folder" (pictures for the project page)
            bool test = args.Length > 0 && (args[0].Equals("/selftest", StringComparison.OrdinalIgnoreCase) || args[0].Equals("/screenshots", StringComparison.OrdinalIgnoreCase));
            if (!test) Paths.MoveOldFolder();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();
            Net.Init();
            FontPack.Load();
            if (test)
            {
                bool shots = args[0].Equals("/screenshots", StringComparison.OrdinalIgnoreCase);
                string dir = args.Length > 1 ? args[1] : Path.Combine(shots ? Paths.Downloads : Path.GetTempPath(), shots ? "Fadi Tools screenshots" : "Fadi Tools self-test");
                return SelfTest.Run(Path.GetFullPath(dir), shots);
            }
            Application.ThreadException += (s, e) => Crash.Report(e.Exception, false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash.Report(e.ExceptionObject as Exception, e.IsTerminating);

            if (args.Length > 0 && args[0].Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Shell.Uninstall(null);
                return 0;
            }

            if (args.Length > 1 && args[0].Equals("/replace", StringComparison.OrdinalIgnoreCase))
            {
                // portable update: put this new version in place of the old exe, then start it
                for (int i = 0; i < 100 && SingleInstance.MainRunning; i++) Thread.Sleep(150);
                for (int i = 0; i < 20; i++) { try { File.Copy(Application.ExecutablePath, args[1], true); break; } catch { Thread.Sleep(300); } }
                try { System.Diagnostics.Process.Start(args[1]); } catch { }
                return 0;
            }
            if (args.Length > 0 && args[0].Equals("/update", StringComparison.OrdinalIgnoreCase))
            {
                // started by the updater: give the old window a moment to close
                for (int i = 0; i < 100 && SingleInstance.MainRunning; i++) Thread.Sleep(150);
                string[] rest = new string[args.Length - 1]; Array.Copy(args, 1, rest, 0, rest.Length); args = rest;
            }
            // "/do compress file..." from the right-click menu: what to do with the files
            string verb = null;
            if (args.Length > 1 && args[0].Equals("/do", StringComparison.OrdinalIgnoreCase))
            {
                verb = args[1].ToLowerInvariant();
                string[] rest = new string[args.Length - 2]; Array.Copy(args, 2, rest, 0, rest.Length); args = rest;
            }
            bool view = args.Length > 0 && args[0].Equals("/view", StringComparison.OrdinalIgnoreCase);
            List<string> files = new List<string>();
            for (int i = view ? 1 : 0; i < args.Length; i++) if (args[i].Trim() != "") files.Add(args[i]);

            if (!view && !SingleInstance.TryBecomeFirst())
            {
                if (SingleInstance.Send(files.ToArray(), verb)) return 0;
            }

            if (Paths.Portable)
            {
                // first run of an unzipped copy: no shortcuts or menus unless switched on in Settings
                foreach (string k in new[] { "IntDesktop", "IntMenu", "IntViewer" }) if (Settings.Get(k, "") == "") Settings.Set(k, "0");
                if (!Tools.EnginesReady)
                    using (SetupForm setup = new SetupForm())
                    {
                        Application.Run(setup);
                        if (!setup.Completed) return 1;
                        Tools.Reset(); Theme.ApplySettings();
                    }
            }
            else if (!Tools.EnginesReady || !Shell.WasInstalled)
            {
                using (SetupForm setup = new SetupForm())
                {
                    Application.Run(setup);
                    if (!setup.Completed) return 1;
                }
                Tools.Reset();
                Theme.ApplySettings();
            }
            else if (!Shell.IsRegistered && Updater.Ver(Shell.Version) >= InstalledVersion())
            {
                // newer version: update the installed copy and menus silently
                try
                {
                    Shell.CopySelf();
                    Shell.Integrate(Settings.Get("IntDesktop", "1") == "1", Settings.Get("IntMenu", "1") == "1", Settings.Get("IntViewer", "1") == "1");
                }
                catch { }
            }
            Shell.SetBrowserEmulation();
            // tidy up quietly: the old FileConverter.exe, and engine programs Fadi Tools never uses (about 330 MB)
            new Thread(() => { Shell.RemoveOldCopy(); Tools.Slim(); }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();

            // one copy: a copy started from Downloads (or anywhere else) hands over to the installed app
            if (!Paths.Portable && !Shell.IsInstalledCopy && Shell.IsInstalled && Settings.Get("RunAnyCopy", "0") != "1")
            {
                try
                {
                    StringBuilder a = new StringBuilder(view ? "/view" : verb != null ? "/do " + verb : "");
                    foreach (string f in files) a.Append(a.Length > 0 ? " " : "").Append(Tools.Quote(Path.GetFullPath(f)));
                    SingleInstance.Release();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.InstalledExe, a.ToString()) { UseShellExecute = false, WorkingDirectory = Paths.AppDir });
                    return 0;
                }
                catch { if (!view) SingleInstance.TryBecomeFirst(); }
            }

            if (view)
            {
                Application.Run(new ViewerForm(files.ToArray()));
                return 0;
            }

            SingleInstance.Listen();
            Application.Run(new MainForm(files.ToArray(), verb));
            return 0;
        }

        static Version InstalledVersion()
        {
            try { return Updater.Ver(System.Diagnostics.FileVersionInfo.GetVersionInfo(Paths.InstalledExe).FileVersion); } catch { return new Version(0, 0); }
        }


    }
}
