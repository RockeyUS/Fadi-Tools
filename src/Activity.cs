using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    public enum ActState { Running, Done, Failed, Cancelled }

    /// One piece of work the app is doing (or did): compressing, converting, downloading, exporting...
    public class ActJob
    {
        public long Id;
        public string Title = "";        // what is being worked on, e.g. "Inner Morgott.mp4"
        public string Kind = "";         // what kind of work, e.g. "Compressing"
        public string Detail = "";       // progress or result line
        public Control Source;           // the page that started it (clicking the row goes there)
        public double Progress = -1;     // 0..1, or < 0 when unknown
        public ActState State;
        public DateTime Started = DateTime.Now, Ended;
        public string Output;            // file or folder to show when finished
        public Action Cancel;            // null when it can't be stopped
        public bool Announced;           // the "finished" notice was shown
        public bool Running { get { return State == ActState.Running; } }
    }

    /// Everything running in the app, in one place: the sidebar's Activity panel, the taskbar button
    /// and the Windows notifications all read from here. Safe to call from worker threads.
    public static class Activity
    {
        static readonly object lk = new object();
        static readonly List<ActJob> jobs = new List<ActJob>();
        static long nextId;
        static int version;
        public static int Version { get { return version; } }

        static void Bump() { System.Threading.Interlocked.Increment(ref version); }

        public static ActJob Begin(Control source, string kind, string title, Action cancel)
        {
            ActJob j = new ActJob { Source = source, Kind = kind ?? "", Title = title ?? "", Cancel = cancel };
            lock (lk)
            {
                j.Id = ++nextId;
                jobs.Insert(0, j);
                // keep the list short: drop the oldest finished ones
                int finished = 0;
                for (int i = 0; i < jobs.Count; i++)
                    if (!jobs[i].Running && ++finished > 30) { jobs.RemoveAt(i); i--; }
            }
            Bump();
            return j;
        }

        public static void Update(ActJob j, double progress, string detail)
        {
            if (j == null || !j.Running) return;
            if (progress >= 0) j.Progress = Math.Max(0, Math.Min(1, progress));
            if (detail != null) j.Detail = detail;
            Bump();
        }

        public static void Rename(ActJob j, string title) { if (j == null) return; j.Title = title ?? ""; Bump(); }

        public static void Done(ActJob j, string detail, string output)
        {
            if (j == null || !j.Running) return;
            j.Progress = 1; j.Detail = detail ?? ""; j.Output = output; j.Ended = DateTime.Now; j.State = ActState.Done;
            Bump();
        }

        public static void Fail(ActJob j, string why)
        {
            if (j == null || !j.Running) return;
            j.Detail = why ?? ""; j.Ended = DateTime.Now; j.State = ActState.Failed;
            Bump();
        }

        public static void Cancelled(ActJob j)
        {
            if (j == null || !j.Running) return;
            j.Detail = L.T("cancelled"); j.Ended = DateTime.Now; j.State = ActState.Cancelled; j.Announced = true;
            Bump();
        }

        /// Ends a job from an error string (null = success).
        public static void End(ActJob j, string error, bool cancelled, string doneDetail, string output)
        {
            if (cancelled) Cancelled(j);
            else if (error != null) Fail(j, error);
            else Done(j, doneDetail, output);
        }

        /// Running jobs first (newest on top), then finished ones (most recent first).
        public static List<ActJob> Snapshot()
        {
            List<ActJob> l; lock (lk) l = new List<ActJob>(jobs);
            List<ActJob> run = l.FindAll(j => j.Running), done = l.FindAll(j => !j.Running);
            done.Sort((a, b) => b.Ended.CompareTo(a.Ended));
            run.AddRange(done);
            return run;
        }

        public static int RunningCount { get { int n = 0; lock (lk) foreach (ActJob j in jobs) if (j.Running) n++; return n; } }
        public static int FinishedCount { get { int n = 0; lock (lk) foreach (ActJob j in jobs) if (!j.Running) n++; return n; } }

        /// Overall progress of what is running: 0..1, -1 = unknown, -2 = nothing running.
        public static double Overall
        {
            get
            {
                double sum = 0; int known = 0, running = 0;
                lock (lk)
                    foreach (ActJob j in jobs)
                        if (j.Running) { running++; if (j.Progress >= 0) { sum += j.Progress; known++; } }
                if (running == 0) return -2;
                if (known == 0) return -1;
                return (sum + (running - known) * 0.0) / running;
            }
        }

        public static void ClearFinished()
        {
            lock (lk) jobs.RemoveAll(j => !j.Running);
            Bump();
        }

        public static void Remove(ActJob j) { lock (lk) jobs.Remove(j); Bump(); }

        /// Jobs that finished and haven't been announced yet (marks them announced).
        public static List<ActJob> TakeFinished()
        {
            List<ActJob> r = new List<ActJob>();
            lock (lk)
                foreach (ActJob j in jobs)
                    if (!j.Running && !j.Announced) { j.Announced = true; r.Add(j); }
            return r;
        }
    }

    /// Progress on the app's taskbar button (Windows 7+), plus a flash when something finishes in the background.
    public static class Taskbar
    {
        [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ITaskbarList3
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
            void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
            void SetProgressState(IntPtr hwnd, int flags);
        }
        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        class TaskbarInstance { }

        [StructLayout(LayoutKind.Sequential)]
        struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        static ITaskbarList3 tb; static bool failed;
        static int lastState = -1; static ulong lastValue = ulong.MaxValue;
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        static ITaskbarList3 Get()
        {
            if (tb != null || failed || !IsWindows) return tb;
            try { tb = (ITaskbarList3)new TaskbarInstance(); tb.HrInit(); }
            catch { tb = null; failed = true; }
            return tb;
        }

        /// p: 0..1 = normal progress, -1 = busy without a known amount, -2 = clear. error = red bar.
        public static void Set(Form f, double p, bool error = false)
        {
            if (f == null || !f.IsHandleCreated) return;
            ITaskbarList3 t = Get();
            if (t == null) return;
            int state = p <= -2 ? 0 : p < 0 ? 1 : error ? 4 : 2;
            ulong value = p >= 0 ? (ulong)Math.Round(Math.Min(1, p) * 1000) : 0;
            try
            {
                if (state != lastState) { t.SetProgressState(f.Handle, state); lastState = state; lastValue = ulong.MaxValue; }
                if ((state == 2 || state == 4) && value != lastValue) { t.SetProgressValue(f.Handle, value, 1000); lastValue = value; }
            }
            catch { }
        }

        /// Flashes the taskbar button until the window is brought forward.
        public static void Flash(Form f)
        {
            if (f == null || !f.IsHandleCreated || !IsWindows) return;
            try
            {
                FLASHWINFO fi = new FLASHWINFO { hwnd = f.Handle, dwFlags = 2 | 12, uCount = 3, dwTimeout = 0 };   // tray, until foreground
                fi.cbSize = (uint)Marshal.SizeOf(fi);
                FlashWindowEx(ref fi);
            }
            catch { }
        }
    }

    /// Windows notifications ("Compressed 3 files") through a small tray icon. Clicking one brings the app back.
    public static class Notifier
    {
        static NotifyIcon icon;
        static Action onClick;
        static Form owner;
        public static bool Enabled { get { return Settings.Get("Notify", "1") == "1"; } set { Settings.Set("Notify", value ? "1" : "0"); } }
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        /// "Exit" in the tray icon's menu.
        public static event Action ExitRequested;

        static void Make(Form f)
        {
            if (icon != null) return;
            icon = new NotifyIcon { Icon = f.Icon ?? SystemIcons.Application, Text = L.T("app_name") };
            icon.BalloonTipClicked += (s, e) => { Bring(); Action a = onClick; if (a != null) try { a(); } catch { } };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Bring(); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(L.T("tray_open"), null, (s, e) => Bring());
            menu.Items.Add(L.T("tray_exit"), null, (s, e) => { if (ExitRequested != null) ExitRequested(); });
            menu.Opening += (s, e) => { menu.Items[0].Text = L.T("tray_open"); menu.Items[1].Text = L.T("tray_exit"); menu.RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No; };
            icon.ContextMenuStrip = menu;
        }

        /// The icon by the clock while the window is hidden and work goes on (on = show it, off = hide it).
        public static void Tray(Form f, bool on)
        {
            if (!IsWindows || f == null) return;
            try
            {
                owner = f;
                if (on) { Make(f); icon.Text = L.T("app_name") + " — " + L.T("tray_still"); icon.Visible = true; }
                else if (icon != null) { icon.Visible = false; icon.Text = L.T("app_name"); }
            }
            catch { }
        }

        public static void Show(Form f, string title, string text, bool ok, Action click)
        {
            if (!Enabled || !IsWindows || f == null) return;
            try
            {
                owner = f; onClick = click;
                Make(f);
                icon.Visible = true;
                icon.ShowBalloonTip(8000, title, string.IsNullOrEmpty(text) ? " " : (text.Length > 240 ? text.Substring(0, 237) + "…" : text), ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch { }
        }

        static void Bring()
        {
            Form f = owner;
            if (f == null || f.IsDisposed) return;
            try
            {
                if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                f.Show(); f.Activate();
            }
            catch { }
        }

        public static void Dispose()
        {
            try { if (icon != null) { icon.Visible = false; icon.Dispose(); } } catch { }
            icon = null;
        }
    }
}
