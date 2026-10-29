using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    public class MainForm : ChromeForm
    {
        Sidebar sidebar;
        List<Page> pages = new List<Page>();
        ConvertPage convert; StudioPage studio; DownloadPage youtube; InspectorHost inspector; CompressPage compress; RenamePage rename; SettingsPage settings; DuplicatesPage dupes;
        InspectPage inspect { get { return inspector.Inspect; } }
        PdfPage pdf { get { return studio.Pdf; } }
        Toast toast;
        int current = -1;

        public MainForm(string[] files) : this(files, null) { }
        /// Started with Windows: open hidden, by the clock.
        public static bool StartHidden;

        public MainForm(string[] files, string verb)
        {
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Font = Theme.F(9.75f);
            Text = L.T("app_name");
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            // roomy by default: about 80% of the screen, within sensible limits
            Size pref = new Size(Math.Max(Theme.S(1220), Math.Min(Theme.S(1600), (int)(wa.Width * 0.8))),
                                 Math.Max(Theme.S(780), Math.Min(Theme.S(1000), (int)(wa.Height * 0.86))));
            Shell.FitToScreen(this, pref, new Size(Theme.S(1000), Theme.S(600)));
            BackColor = Theme.BgBottom;
            KeyPreview = true;

            sidebar = new Sidebar();
            // grouped like the side list shows them: making things, files, the internet
            sidebar.Items.Add(new NavItem("nav_convert", Ico.Sync) { Section = "nav_sec_create" });
            sidebar.Items.Add(new NavItem("nav_studio", Ico.Photo));
            sidebar.Items.Add(new NavItem("nav_compress", "\uE73F"));
            sidebar.Items.Add(new NavItem("nav_rename", "\uE8AC") { Section = "nav_sec_files" });
            sidebar.Items.Add(new NavItem("nav_inspect", Ico.Search));
            sidebar.Items.Add(new NavItem("nav_dupes", Ico.Copy));
            sidebar.Items.Add(new NavItem("nav_download", Ico.Download) { Section = "nav_sec_web", Badge = () => youtube != null ? youtube.ActiveCount : 0 });
            nav.AddRange(sidebar.Items);
            nav.Add(new NavItem("nav_settings", Ico.Settings));   // at the bottom of the side list, under Activity
            sidebar.Navigate += Go;
            Bar.ThemeClicked += () =>
            {
                // the new look grows out of the sun/moon button
                revealAt = Bar.ThemeCenter;
                Settings.Set("Theme", Theme.Dark ? "light" : "dark");
                Theme.ApplySettings(); Theme.RaiseChanged();
            };
            Bar.LangClicked += () =>
            {
                Settings.Set("Language", Theme.RTL ? "en" : "ar");
                Theme.ApplySettings(); Theme.RaiseChanged();
            };
            Theme.Changing += BeginLookChange;
            sidebar.OpenActivity += ShowActivity;
            sidebar.Collapsed = Settings.Get("SideCollapsed", "0") == "1";
            sideW = sidebar.Collapsed ? SideNarrow : SideWide;
            sidebar.ToggleCollapse += () =>
            {
                // fold into icons / open again, sliding
                if (sliding) return;
                sidebar.Collapsed = !sidebar.Collapsed;
                Settings.Set("SideCollapsed", sidebar.Collapsed ? "1" : "0");
                float from = sideW, to = sidebar.Collapsed ? SideNarrow : SideWide;
                SlideSidebar(from, to);
            };
            sidebar.OpenSearch += ShowPalette;
            Notifier.ExitRequested += ExitFromTray;
            TrayPanel.ExitRequested += ExitFromTray;
            TrayPanel.OpenRequested += ComeBack;
            TrayPanel.SettingsRequested += () => { ComeBack(); Go(SettingsIndex); };
            TrayPanel.CustomizeRequested += ShowWheelSettings;
            actTimer.Tick += (s, e) => PollActivity();
            actTimer.Start();

            toast = new Toast();
            convert = new ConvertPage(); studio = new StudioPage(); youtube = new DownloadPage();
            inspector = new InspectorHost(); compress = new CompressPage(); rename = new RenamePage(); settings = new SettingsPage();
            dupes = new DuplicatesPage();
            pages.AddRange(new Page[] { convert, studio, compress, rename, inspector, dupes, youtube, settings });
            for (int i = 0; i < pages.Count; i++) pages[i].Glyph = nav[i].Glyph;
            inspect.SendToConverter += f => { Go(0); convert.AcceptFiles(new[] { f }); };
            studio.SendToConverter += f => { Go(0); convert.AcceptFiles(new[] { f }); };

            Controls.Add(toast);
            foreach (Page p in pages) { p.Toast = toast; p.Owner = this; p.Visible = false; Controls.Add(p); }
            Controls.Add(sidebar);
            Controls.Add(Bar);

            Theme.Changed += OnThemeChanged;
            if (StartHidden) Opacity = 0;   // started with Windows: it goes straight to the clock without showing
            Shown += (s, e) =>
            {
                if (SelfTest.Active) return;   // a test run: no tour, questions or update check
                // the icon by the clock, for as long as the app is open
                if (Settings.Get("KeepInTray", "1") == "1" || StartHidden) Notifier.ShowResident(this, true);
                DragWheel.Dropped += OnWheelDrop;
                DragWheel.Apply(this);
                Watchers.Start(this);
                if (StartHidden)
                {
                    StartHidden = false;
                    inTray = true;
                    Hide();
                    Opacity = 1;
                    return;   // the tour, "what's new" and the update check wait for the first time it's opened
                }
                BeginInvoke((Action)(() =>
                {
                    bool tour = false;
                    if (Settings.Get("TourDone", "") != TourForm.Version && Environment.GetEnvironmentVariable("FADI_NO_TOUR") == null) { TourForm.ShowTour(this); tour = true; }
                    WhatsNew.ShowIfUpdated(this, tour);
                    GetMissingEngines();
                    OfferRestore();
                    Updater.CheckInBackground(toast, this);
                }));
            };
            Resize += (s, e) => DoLayout();
            HandleCreated += (s, e) => Theme.ApplyTitleBar(this);

            int start = 0;
            int.TryParse(Settings.Get("LastPage", "0"), out start);
            if (Settings.Get("PagesV", "") != "9") { start = 0; Settings.Set("PagesV", "9"); }   // page order changed in 1.5 (Duplicates)
            if (start < 0 || start >= pages.Count || start == SettingsIndex) start = 0;
            bool hasFiles = files != null && files.Length > 0;
            if (!hasFiles) Go(start);
            DoLayout();
            if (hasFiles) Route(files, verb);

            SingleInstance.FilesReceived += (paths, v) => BeginInvoke((Action)(() =>
            {
                if (!Visible) Show();   // it was working in the tray
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                if (paths.Length == 0) return;
                Route(paths, v);
            }));
        }

        int SettingsIndex { get { return pages.Count - 1; } }

        /// Everything comes installed: engines an older version left out (the download engine, the PDF engine)
        /// are fetched quietly in the background, shown in Activity.
        void GetMissingEngines()
        {
            if (Environment.GetEnvironmentVariable("FADI_NO_TOUR") != null) return;
            bool yt = !YtDlp.Ready, pdf = !Qpdf.Ready;
            if (!yt && !pdf) return;
            ActJob job = Activity.Begin(this, L.T("eng_getting"), yt && pdf ? "yt-dlp · qpdf" : yt ? "yt-dlp" : "qpdf", null);
            Thread t = new Thread(() =>
            {
                string err = null;
                try { if (yt) YtDlp.Install((label, f) => Activity.Update(job, (pdf ? 0.7 : 1) * Math.Max(0, f), label), () => Tools.ShuttingDown); }
                catch (Exception ex) { err = ex.Message; }
                try { if (pdf) Qpdf.Install((g, total) => Activity.Update(job, (yt ? 0.7 : 0) + (yt ? 0.3 : 1) * (total > 0 ? g / (double)total : 0), "qpdf"), () => Tools.ShuttingDown); }
                catch (Exception ex) { if (err == null) err = ex.Message; }
                Activity.End(job, err, false, L.T("eng_ready"), null);
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.Start();
        }
        readonly List<NavItem> nav = new List<NavItem>();

        // ------------------------------------------------------------------ light/dark and language changes, animated
        Overlay lookOv;
        Point? revealAt;

        /// Called just before the theme or language changes: holds a picture of the window as it looks now over it,
        /// then fades it away — or, for the light/dark button, lets the new look grow through it as a circle.
        void BeginLookChange()
        {
            Point? from = revealAt; revealAt = null;
            if (!Visible || WindowState == FormWindowState.Minimized || !IsHandleCreated || (lookOv != null && !lookOv.IsDisposed)) return;
            Overlay ov = Fx.Hold(this, ClientRectangle);
            if (ov == null) return;
            lookOv = ov;
            ov.FormClosed += (s, e) => { if (lookOv == ov) lookOv = null; };
            if (from.HasValue) Fx.RevealAway(ov, PointToScreen(from.Value), 480);
            else Fx.FadeAway(ov, 280, 0, 0);
        }

        // ------------------------------------------------------------------ folding the side list
        bool sliding;

        /// The side list folds / opens while the page slides along with it. The page is laid out once (not on
        /// every frame, which was slow on a big screen): it keeps its wider size during the slide and only moves.
        void SlideSidebar(float from, float to)
        {
            Page pg = current >= 0 && current < pages.Count ? pages[current] : null;
            if (!Fx.Motion || pg == null || !Visible || WindowState == FormWindowState.Minimized) { sideW = to; DoLayout(); return; }
            int W = ClientSize.Width, bh = BarHeight, H = ClientSize.Height - bh;
            int wFrom = W - (int)Math.Round(from), wTo = W - (int)Math.Round(to);
            int pw = Math.Max(wFrom, wTo);
            sliding = true;
            Func<float, int> pageX = sw => Theme.RTL ? W - (int)Math.Round(sw) - pw : (int)Math.Round(sw);
            pg.SetBounds(pageX(from), bh, pw, H);
            Fx.Tween(260, t =>
            {
                sideW = from + (to - from) * t;
                int sw = (int)Math.Round(sideW);
                sidebar.Bounds = new Rectangle(Theme.RTL ? W - sw : 0, bh, sw, H);
                pg.Location = new Point(pageX(sideW), bh);
                sidebar.Update(); pg.Update();
            }, () =>
            {
                sideW = to;
                sliding = false;
                // opening: the page was kept wide while it slid; it now fits again behind a quick fade
                Overlay ov = pw != wTo ? Fx.Hold(this, new Rectangle(Theme.RTL ? 0 : (int)Math.Round(to), bh, wTo, H)) : null;
                DoLayout();
                Fx.FadeAway(ov, 160, 0, 0);
            });
        }

        /// The app closed before some Studio work was saved (crash, power cut): offer to bring it back.
        void OfferRestore()
        {
            if (Environment.GetEnvironmentVariable("FADI_NO_RESTORE") != null) return;
            foreach (string f in new[] { Recovery.VideoFile, Recovery.PhotoProject, Recovery.PhotoFile })
            {
                if (!Recovery.Has(f)) continue;
                bool video = f == Recovery.VideoFile;
                DateTime t = DateTime.Now; try { t = System.IO.File.GetLastWriteTime(f); } catch { }
                if (Ask.Confirm(this, L.T("rec_title"), L.T(video ? "rec_video" : "rec_photo", VideoStudio.Ago(t)), L.T("rec_restore"), L.T("rec_discard"), AskKind.Info))
                {
                    Go(pages.IndexOf(studio));
                    if (video) { studio.Show(studio.Videos); studio.Videos.OpenProject(f, true); }
                    else studio.RestorePhotoEdit(f);
                    return;   // one at a time: the other one is offered next time
                }
                Recovery.Clear(f);
            }
        }

        // ------------------------------------------------------------------ files from Explorer's right-click menu
        System.Windows.Forms.Timer autoStart;

        /// Opens files on the right page: verb = convert (default), compress, discord, studio, inspect.
        void Route(string[] paths, string verb)
        {
            switch (verb)
            {
                case "compress":
                case "discord":
                    Go(pages.IndexOf(compress));
                    if (compress.Busy) { toast.Show(L.T("menu_busy"), false, null, null); return; }
                    compress.UsePreset(verb == "discord" ? "Discord" : "");
                    compress.AcceptFiles(paths);
                    if (verb == "discord")
                    {
                        // several files arrive one by one (Explorer starts us once per file): start once they've all come in
                        if (autoStart == null) { autoStart = new System.Windows.Forms.Timer { Interval = 1200 }; autoStart.Tick += (s, e) => { autoStart.Stop(); compress.StartNow(); }; }
                        autoStart.Stop(); autoStart.Start();
                    }
                    break;
                case "studio": Go(pages.IndexOf(studio)); studio.AcceptFiles(paths); break;
                case "project": Go(pages.IndexOf(studio)); studio.Show(studio.Videos); studio.Videos.OpenProject(paths[0], false); break;
                case "text":
                    Go(pages.IndexOf(studio)); studio.AcceptFiles(new[] { paths[0] });
                    Ocr.Run(studio.Photos, paths[0], 0, (m, ok) => toast.Show(m, ok, null, null));
                    break;
                case "inspect": Go(pages.IndexOf(inspector)); inspector.Show(inspect); inspect.AcceptFiles(paths); break;
                default:
                    Go(pages.IndexOf(convert));
                    if (!convert.Busy) convert.AcceptFiles(paths);
                    break;
            }
        }

        // ------------------------------------------------------------------ the drag + Shift wheel
        /// Settings → Donut Wheel (from the tray panel).
        public void ShowWheelSettings()
        {
            ComeBack();
            if (current == SettingsIndex) { settings.OpenWheel(); return; }
            settings.PrepareWheel();
            Go(SettingsIndex);
            settings.AfterShownWheel();
        }

        /// Brings the window back (from the tray too) for actions that need it.
        void ComeBack()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        /// A file was dropped on a slice of the wheel: convert / compress / cut out right away (shown in Activity,
        /// saved next to the file), or open the right page.
        void OnWheelDrop(string[] files, WheelItem it)
        {
            if (IsDisposed || files == null || files.Length == 0) return;
            if (Recipes.IsRecipe(it.Action)) { Recipes.Run(it.Action, files, convert); return; }
            switch (it.Action)
            {
                // small windows of their own: Fadi Tools itself stays where it is
                case "crop": CropWindow.Open(files[0], Cursor.Position, studio); return;
                case "resize": ResizeWindow.Open(files[0], Cursor.Position, studio); return;
                case "rotate": RotateWindow.Open(files[0], Cursor.Position, studio); return;
                case "upscale": UpscaleWindow.Open(files[0], Cursor.Position, studio); return;
                case "trim": TrimWindow.Open(files[0], Cursor.Position, studio); return;
                case "combine": case "merge": QuickPdf.CombineInBackground(files, studio); return;
                case "joinv": case "joina": MergeWindow.Open(files, Cursor.Position, studio); return;
                case "compress": CompressWindow.Open(files, Cursor.Position, compress); return;
                case "edit": ComeBack(); Route(files, "studio"); return;
                case "text": ComeBack(); Route(new[] { files[0] }, "text"); return;
                case "info": ComeBack(); Route(new[] { files[0] }, "inspect"); return;
                // a folder dragged onto the wheel
                case "f_convert": ComeBack(); Route(files, "convert"); return;
                case "f_compress": ComeBack(); Route(files, "compress"); return;
                case "f_rename": ComeBack(); Go(pages.IndexOf(rename)); rename.AcceptFiles(files); return;
                case "f_dupes": ComeBack(); Go(pages.IndexOf(dupes)); dupes.AcceptFiles(files); dupes.Scan(); return;
                case "nobg":
                    if (!BgRemover.Ready) { ComeBack(); Route(new[] { files[0] }, "studio"); toast.Show(L.T("wh_nobg_get"), true, null, null); return; }
                    break;
            }
            bool cancel = false;
            string title = files.Length == 1 ? System.IO.Path.GetFileName(files[0]) : L.T("n_files", files.Length);
            Control source = it.IsFormat ? (Control)convert : it.Action == "compress" ? (Control)compress : studio;
            string kind = it.IsFormat ? L.T("act_k_convert", it.Target.ToUpperInvariant()) : it.Action == "compress" ? L.T("act_k_compress") : L.T("wh_nobg");
            ActJob job = Activity.Begin(source, kind, title, () => cancel = true);
            Thread t = new Thread(() =>
            {
                int ok = 0; string lastOut = null, lastErr = null;
                for (int i = 0; i < files.Length && !cancel; i++)
                {
                    string f = files[i];
                    int index = i;
                    try
                    {
                        string outDir = DragWheel.OutDirFor(f);
                        if (it.Action == "nobg")
                        {
                            Activity.Update(job, (index + 0.2) / files.Length, null);
                            using (Image im = ViewerPage.Decode(f, 4096))
                            using (Bitmap b = new Bitmap(im))
                            {
                                byte[] mask = BgRemover.Mask(b);
                                using (Bitmap cut = BgRemover.Cutout(b, mask))
                                {
                                    string dst = Paths.UniquePath(outDir, System.IO.Path.GetFileNameWithoutExtension(f) + " " + L.T("wh_nobg_suffix"), "png");
                                    cut.Save(dst, System.Drawing.Imaging.ImageFormat.Png);
                                    lastOut = dst;
                                }
                            }
                        }
                        else
                        {
                            Job j = it.IsFormat ? Engine.Build(f, it.Target, outDir, Quality.Balanced) : Compressor.Build(f, outDir, Compressor.Level.Balanced, 0);
                            string err = Engine.Run(j, p => Activity.Update(job, (index + p) / files.Length, files.Length > 1 ? (index + 1) + "/" + files.Length + "  ·  " + (int)(p * 100) + "%" : (int)(p * 100) + "%"), () => cancel);
                            if (err != null) throw new Exception(err);
                            lastOut = j.Output;
                        }
                        ok++;
                    }
                    catch (Exception ex) { lastErr = ex.Message; }
                }
                string where = files.Length > 0 ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(lastOut ?? files[0])) : "";
                string detail = ok == files.Length ? L.T("all_done", ok == 1 ? L.T("one_file") : L.T("n_files", ok), where) : L.T("some_failed", ok, files.Length - ok);
                Activity.End(job, ok == 0 ? (lastErr ?? L.T("none_converted")) : null, cancel, detail, ok == 1 ? lastOut : (lastOut != null ? System.IO.Path.GetDirectoryName(lastOut) : null));
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------------ activity (jobs across all pages)
        readonly System.Windows.Forms.Timer actTimer = new System.Windows.Forms.Timer { Interval = 200 };
        ActivityPanel actPanel; DateTime actPanelClosed = DateTime.MinValue, actSeenAt = DateTime.Now;
        int actSeen = -1;

        /// The top-level page a control belongs to (walks up from nested pages like the Studio's video editor).
        int PageIndexOf(Control c)
        {
            for (Control x = c; x != null; x = x.Parent) { int i = pages.IndexOf(x as Page); if (i >= 0) return i; }
            return -1;
        }
        string GlyphFor(Control c) { int i = PageIndexOf(c); return i >= 0 ? pages[i].Glyph : null; }

        /// Shows the page (and, in the Studio, the photo or video side) a job came from.
        void GoPdf() { Go(pages.IndexOf(studio)); studio.Show(studio.Pdf); }

        void GoTo(Control c)
        {
            int i = PageIndexOf(c);
            if (i < 0) return;
            Go(i);
            if (pages[i] == studio) studio.ShowPart(c);
            if (pages[i] == inspector && (c == inspector.Inspect || c == inspector.Re)) inspector.Show((Page)c);
        }

        void PollActivity()
        {
            if (Activity.Version == actSeen) return;
            actSeen = Activity.Version;
            int run = Activity.RunningCount, fresh = 0;
            foreach (ActJob j in Activity.Snapshot()) if (!j.Running && j.State != ActState.Cancelled && j.Ended > actSeenAt) fresh++;
            sidebar.ActivityRunning = run; sidebar.ActivityProgress = Activity.Overall; sidebar.ActivityNew = actPanel != null ? 0 : fresh;
            if (run > 0) Animator.Start(sidebar);
            sidebar.Invalidate();   // counts (downloads, jobs) may have changed
            Taskbar.Set(this, Activity.Overall);
            bool any = false, told = false, longOne = false;
            foreach (ActJob j in Activity.TakeFinished())
            {
                if (Announce(j)) told = true;
                any = true;
                if ((j.Ended - j.Started).TotalSeconds >= Notifier.LongSeconds) longOne = true;
            }
            // working in the tray and now everything's done: say so (the app stays by the clock until opened or exited),
            // unless a notice just said it, or it was all quick
            if (any && !told && inTray && run == 0 && !AnyBusy && (longOne || !Notifier.LongOnly)) Notifier.Show(this, L.T("tray_all_done"), L.T("tray_all_done_d"), true, null);
        }

        // ------------------------------------------------------------------ working on in the tray
        bool inTray, reallyExit;
        bool AnyBusy { get { foreach (Page p in pages) if (p.Busy) return true; return false; } }

        /// Closing the window while something runs: it hides and keeps working, with an icon by the clock.
        void GoToTray(bool idle)
        {
            inTray = true;
            Hide();
            Notifier.Tray(this, true);
            if (!idle) Notifier.Show(this, L.T("tray_still"), L.T("tray_still_d"), true, null);
            else if (Settings.Get("TrayNoteShown", "0") != "1")
            {
                // closed with "keep running" on: say where it went, once
                Settings.Set("TrayNoteShown", "1");
                Notifier.Show(this, L.T("tray_idle"), L.T("tray_idle_d"), true, null);
            }
        }

        /// "Exit" in the tray icon's menu: really close (still asking about unsaved work).
        void ExitFromTray()
        {
            if (IsDisposed) return;
            reallyExit = true;
            Close();
            reallyExit = false;   // a question was answered "keep editing": the window stays
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && inTray) { inTray = false; Notifier.Tray(this, false); }
        }

        // ------------------------------------------------------------------ links copied anywhere
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        string lastLink, pendingLink; DateTime pendingAt;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // opened on a screen whose size setting differs from the main screen's
            int dpi = Theme.DpiOf(Handle);
            if (!SelfTest.FixedScale && dpi > 0 && Math.Abs(dpi / 96f - Theme.Scale) > 0.001f) BeginInvoke((Action)(() => ApplyDpi(dpi, null)));
            try { if (Environment.OSVersion.Platform == PlatformID.Win32NT && !SelfTest.Active) AddClipboardFormatListener(Handle); } catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D) { try { BeginInvoke((Action)ClipboardChanged); } catch { } }   // WM_CLIPBOARDUPDATE
            if (m.Msg == 0x02E0)   // WM_DPICHANGED: moved to a screen with another size setting
            {
                int dpi = (int)((long)m.WParam & 0xFFFF);
                RECT r = (RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(RECT));
                if (SelfTest.FixedScale) { m.Result = IntPtr.Zero; return; }
                ApplyDpi(dpi, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        /// The window is now on a screen with another size setting (or opened on one): everything is measured
        /// again for it, so it's sharp and the right size there instead of stretched.
        public void ApplyDpi(int dpi, Rectangle? bounds)
        {
            float before = Theme.Scale;
            Theme.SetDpi(dpi);
            float k = Theme.Scale / before;
            if (Math.Abs(k - 1) < 0.001f) { if (bounds.HasValue) Bounds = bounds.Value; return; }
            SuspendLayout();
            try
            {
                Rectangle wa = Screen.FromHandle(Handle).WorkingArea;
                MinimumSize = new Size(Math.Min(wa.Width, (int)(MinimumSize.Width * k)), Math.Min(wa.Height, (int)(MinimumSize.Height * k)));
                if (bounds.HasValue) Bounds = bounds.Value;
                else if (WindowState == FormWindowState.Normal) Bounds = Rectangle.Intersect(wa, Bounds);
                sideW = sidebar.Collapsed ? SideNarrow : SideWide;
                // Windows' own text boxes keep the font they were given: make theirs follow too
                foreach (Form f in Application.OpenForms) RescaleFonts(f, k);
                OnThemeChanged();
            }
            finally { ResumeLayout(); }
        }

        static void RescaleFonts(Control c, float k)
        {
            foreach (Control ch in c.Controls)
            {
                if ((ch is TextBoxBase || ch is ComboBox || ch is ListBox || ch is WebBrowser) && ch.Font != null && ch.Font.Unit == GraphicsUnit.Pixel)
                    try { ch.Font = new Font(ch.Font.FontFamily, ch.Font.Size * k, ch.Font.Style, GraphicsUnit.Pixel); } catch { }
                RescaleFonts(ch, k);
            }
        }

        /// A video link was copied (in the browser, an app…): offer to download it.
        void ClipboardChanged()
        {
            if (Settings.Get("WatchLinks", "1") != "1") return;
            string t = null;
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { return; }
            if (t == null) return;
            t = t.Trim();
            // links can be long (an image result's link carries the picture's address); a picture inside the link longer still
            if (t.Length > (Resolver.IsDataImage(t) ? 8 << 20 : 4000) || t.IndexOfAny(new[] { ' ', '\n', '\r', '\t' }) >= 0 || !DownloadPage.IsVideoLink(t)) return;
            if (t == lastLink || (current == pages.IndexOf(youtube) && Form.ActiveForm == this)) { lastLink = t; return; }   // already there
            lastLink = t;
            if (Form.ActiveForm == this) OfferLink(t);
            else { pendingLink = t; pendingAt = DateTime.Now; }
        }

        void OfferLink(string url)
        {
            string host = Resolver.IsDataImage(url) ? L.T("dl_picture") : url;
            if (!Resolver.IsDataImage(url)) try { host = new Uri(Resolver.Unwrap(url)).Host.Replace("www.", ""); } catch { }
            toast.Show(L.T("link_copied") + "  (" + host + ")", true, () => L.T("link_get"), () =>
            {
                Go(pages.IndexOf(youtube));
                youtube.FetchLink(url);
            });
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // a link copied while the app was in the background: offered when you come back (if it's recent)
            if (pendingLink != null)
            {
                string l = pendingLink; pendingLink = null;
                if ((DateTime.Now - pendingAt).TotalMinutes < 3) BeginInvoke((Action)(() => OfferLink(l)));
            }
        }

        /// A job finished: when the app is in the background, say so with a Windows notification
        /// (by default only for work that took a while, or failed). True when a notice was shown.
        bool Announce(ActJob j)
        {
            if (j.State == ActState.Cancelled || !Notifier.Enabled) return false;
            bool background = Form.ActiveForm == null || WindowState == FormWindowState.Minimized;
            if (!background) return false;
            bool ok = j.State == ActState.Done;
            if (ok && Notifier.LongOnly && (j.Ended - j.Started).TotalSeconds < Notifier.LongSeconds) return false;
            string title = ok ? L.T("act_n_done", j.Title) : L.T("act_n_failed", j.Title);
            Control src = j.Source;
            Notifier.Show(this, title, j.Detail, ok, () => { GoTo(src); });
            Taskbar.Flash(this);
            return true;
        }

        // ------------------------------------------------------------------ quick search (Ctrl+K)
        void ShowPalette() { CommandPalette.Show(this, PaletteItems(), sidebar.Visible ? sidebar.SearchScreenRect : Rectangle.Empty); }

        /// Everything the quick search offers. Words hold extra search terms in both languages.
        List<PaletteItem> PaletteItems()
        {
            List<PaletteItem> l = new List<PaletteItem>();
            string gp = L.T("pal_g_pages"), ga = L.T("pal_g_actions"), gs = L.T("pal_g_settings");
            Func<string, string> both = k => L.T(k) + " " + L.Both(k);
            for (int i = 0; i < pages.Count; i++)
            {
                int k = i;
                l.Add(new PaletteItem { Group = gp, Title = L.T(nav[i].Key), Sub = L.T(pages[i].SubKey), Glyph = nav[i].Glyph, Words = L.Both(nav[i].Key) + " " + L.Both(pages[i].SubKey), Run = () => Go(k) });
            }
            Action<string, string, string, Action> add = (key, glyph, words, run) => l.Add(new PaletteItem { Group = ga, Title = L.T(key), Glyph = glyph, Words = L.Both(key) + " " + (words ?? ""), Run = run });
            add("pal_convert", Ico.Sync, "convert change format mp3 mp4 pdf jpg png تحويل صيغة", () => { Go(pages.IndexOf(convert)); convert.Browse(); });
            add("pal_compress", "\uE73F", "compress smaller reduce size shrink ضغط تصغير حجم", () => { Go(pages.IndexOf(compress)); compress.UsePreset(""); compress.Browse2(); });
            add("pal_discord", "\uE73F", "discord 20 mb compress ضغط ديسكورد", () => { Go(pages.IndexOf(compress)); compress.UsePreset("Discord"); compress.Browse2(); });
            add("pal_email", "\uE73F", "email mail gmail outlook 25 mb attach بريد ايميل", () => { Go(pages.IndexOf(compress)); compress.UsePreset("email"); compress.Browse2(); });
            add("pal_avatar", "\uE73F", "avatar profile picture photo pfp github 1 mb صورة شخصية بروفايل", () => { Go(pages.IndexOf(compress)); compress.UsePreset("avatar"); compress.Browse2(); });
            add("pal_studio_open", Ico.Photo, "edit photo video open studio تعديل صورة فيديو", () => { Go(pages.IndexOf(studio)); studio.PickPhoto(); });
            add("pal_proj_open", "\uE7C4", "open video project capcut timeline فتح مشروع فيديو", () => { string f = VideoStudio.PickProject(this); if (f != null) { Go(pages.IndexOf(studio)); studio.Show(studio.Videos); studio.Videos.OpenProject(f, false); } });
            foreach (string rp in VideoStudio.RecentProjects)
            {
                string p0 = rp;
                l.Add(new PaletteItem { Group = L.T("pal_g_recent"), Title = System.IO.Path.GetFileNameWithoutExtension(rp), Sub = L.T("pal_project"), Glyph = "\uE7C4", Words = "project recent مشروع " + rp, Run = () => { Go(pages.IndexOf(studio)); studio.Show(studio.Videos); studio.Videos.OpenProject(p0, false); } });
            }
            add("pal_captions", "\uE8BE", "captions subtitles auto transcribe whisper speech to text ترجمة نصوص تلقائية كلام", () =>
            {
                Go(pages.IndexOf(studio)); studio.Show(studio.Videos);
                if (studio.Videos.HasFile) studio.Videos.MakeCaptions(); else { studio.Videos.Browse(false); toast.Show(L.T("cap_open_first"), true, null, null); }
            });
            add("pal_video_new", Ico.Video, "video edit cut trim new project capcut فيديو قص مشروع", () => { Go(pages.IndexOf(studio)); studio.Show(studio.Videos); studio.Videos.Browse(false); });
            add("pal_pdf_combine", "\uE8AB", "merge combine join pdf photos images to pdf دمج ملفات pdf صور", () => { GoPdf(); pdf.StartMode(0, true); });
            add("pal_pdf_pages", "\uEA90", "pdf pages rotate delete reorder organize صفحات تدوير حذف ترتيب", () => { GoPdf(); pdf.StartMode(1, true); });
            add("pal_pdf_split", "\uE8C9", "split pdf extract pages separate تقسيم استخراج صفحات", () => { GoPdf(); pdf.StartMode(1, true); });
            add("pal_pdf_images", Ico.Photo, "pdf to jpg png images pictures export صور", () => { GoPdf(); pdf.StartMode(1, true); });
            add("pal_ocr", "\uE8EF", "ocr copy text from picture image screenshot read scan نسخ نص من صورة لقطة شاشة", () => { Go(pages.IndexOf(studio)); studio.CopyText(); });
            add("pal_rename", "\uE8AC", "rename batch many files names numbers numbered replace إعادة تسمية ملفات أسماء ترقيم", () => { Go(pages.IndexOf(rename)); rename.Browse(); });
            add("pal_vbg", "\uE77B", "remove video background green screen cut out person transparent إزالة خلفية الفيديو شاشة خضراء", () => { Go(pages.IndexOf(studio)); studio.Show(studio.Videos); if (studio.Videos.HasFile) studio.Videos.RemoveVideoBg(); else studio.Videos.Browse(false); });
            add("pal_sticker", "\uE899", "sticker whatsapp telegram discord emoji ملصق واتساب تيليجرام ديسكورد", () => { Go(pages.IndexOf(convert)); convert.Browse(); });
            add("pal_pdf_sign", "\uEC87", "sign pdf signature watermark page numbers password lock unlock توقيع علامة مائية ترقيم كلمة مرور قفل", () => { GoPdf(); pdf.StartMode(1, true); });
            add("pal_whatsnew", "\uE789", "what's new changes update version changelog الجديد التغييرات تحديث", () => WhatsNew.Show(this));
            add("pal_download", Ico.Download, "download youtube tiktok instagram link url video تنزيل يوتيوب رابط", () => { Go(pages.IndexOf(youtube)); youtube.FocusLink(); });
            add("pal_inspect", Ico.Search, "inspect file type hex checksum فحص ملف", () => { Go(pages.IndexOf(inspector)); inspector.Show(inspect); inspect.Browse(); });
            add("pal_re", Ico.Code, "reverse engineer exe dll analyse program هندسة عكسية برنامج", () => { Go(pages.IndexOf(inspector)); inspector.Show(inspector.Re); });
            add("pal_activity", "\uE9D9", "activity running jobs progress tasks النشاط المهام", () => ShowActivity(sidebar.FootRect(0)));
            Action<string, string, string, Action> set = (key, glyph, words, run) => l.Add(new PaletteItem { Group = gs, Title = L.T(key), Glyph = glyph, Words = L.Both(key) + " " + (words ?? ""), Run = run });
            set(Theme.Dark ? "pal_light" : "pal_dark", Theme.Dark ? Ico.Sun : Ico.Moon, "theme dark light mode appearance مظهر داكن فاتح", () => { Settings.Set("Theme", Theme.Dark ? "light" : "dark"); Theme.ApplySettings(); Theme.RaiseChanged(); });
            set(Theme.RTL ? "pal_english" : "pal_arabic", Ico.Globe, "language arabic english لغة عربي انجليزي", () => { Settings.Set("Language", Theme.RTL ? "en" : "ar"); Theme.ApplySettings(); Theme.RaiseChanged(); });
            set("pal_settings", Ico.Settings, "settings accent colour color save folder updates notifications الإعدادات لون", () => Go(SettingsIndex));
            return l;
        }

        void ShowActivity(Rectangle row)
        {
            if (actPanel != null) { actPanel.Dismiss(); return; }
            if ((DateTime.Now - actPanelClosed).TotalMilliseconds < 250) return;   // the click that closed it
            actPanel = new ActivityPanel();
            actPanel.GlyphOf = GlyphFor;
            actPanel.Navigate += GoTo;
            actPanel.FormClosed += (s, e) => { actPanel = null; actPanelClosed = DateTime.Now; actSeenAt = DateTime.Now; sidebar.ActivityNew = 0; sidebar.ActivityOpen = false; Animator.Start(sidebar); };
            actSeenAt = DateTime.Now; sidebar.ActivityNew = 0; sidebar.ActivityOpen = true; Animator.Start(sidebar);
            actPanel.ShowBeside(sidebar, row);
        }
        public void ShowStudio(string[] files) { Go(1); studio.AcceptFiles(files); }

        public int PageCount { get { return pages.Count; } }
        public Page PageAt(int i) { return pages[i]; }
        /// The self-test is done: close without questions.
        public void CloseForTest() { reallyExit = true; Close(); }

        public void Go(int i)
        {
            if (i == current) return;
            int was = current;
            // a picture of the page we're leaving floats above and fades away (on the graphics card) as the new one shows
            Overlay before = was >= 0 && !sliding && Visible && WindowState != FormWindowState.Minimized ? Fx.Hold(pages[was], pages[was].ClientRectangle) : null;
            current = i;
            sidebar.Selected = i;
            for (int k = 0; k < pages.Count; k++) pages[k].Visible = k == i;
            pages[i].OnShown();
            pages[i].DoLayout();
            sidebar.Invalidate();
            Fx.FadeAway(before, 230, 0, (i > was ? -1 : 1) * Theme.S(10));
            if (i != SettingsIndex) Settings.Set("LastPage", i.ToString());
        }

        float sideW = -1;
        float SideWide { get { return Theme.S(244); } }
        float SideNarrow { get { return Theme.S(78); } }

        void DoLayout()
        {
            if (ClientSize.Width <= 0) return;
            if (sideW < 0) sideW = sidebar.Collapsed ? SideNarrow : SideWide;
            int sw = (int)Math.Round(sideW), bh = BarHeight;
            Bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Bar.Height);
            Bar.SideWidth = 0; Bar.SideRight = Theme.RTL;   // the side list floats as its own card: the top bar is one plain strip
            Rectangle side = new Rectangle(0, bh, sw, ClientSize.Height - bh);
            Rectangle content = new Rectangle(sw, bh, ClientSize.Width - sw, ClientSize.Height - bh);
            if (Theme.RTL) { side.X = ClientSize.Width - sw; content.X = 0; }
            sidebar.Bounds = side;
            foreach (Page p in pages) p.Bounds = content;
            toast.Host = content;
            if (toast.Visible) toast.BringToFront();
        }

        bool menuRtl = Theme.RTL;
        void OnThemeChanged()
        {
            SuspendLayout();
            BackColor = Theme.BgBottom;
            Text = L.T("app_name");
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            Theme.ApplyTitleBar(this);
            Bar.Height = Theme.S(36);
            DoLayout();
            foreach (Page p in pages) { p.Restyle(); p.DoLayout(); }
            ResumeLayout();
            InvalidateAll(this);
            // keep the Explorer menu text in the chosen language — just the words: re-registering everything
            // made Windows reload every icon (the whole desktop flickered on each theme or language change)
            if (Theme.RTL != menuRtl && Shell.IsInstalled && Shell.IsRegistered)
            {
                menuRtl = Theme.RTL;
                Thread t = new Thread(() => { try { Shell.UpdateMenuText(); } catch { } });
                t.IsBackground = true; t.Start();
            }
        }

        static void InvalidateAll(Control c)
        {
            c.Invalidate();
            foreach (Control ch in c.Controls) InvalidateAll(ch);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (current == 1 && studio.HandleKey(keyData, ActiveControl is TextBox)) return true;
            if (keyData == (Keys.Control | Keys.V) && !(ActiveControl is TextBox) && !(ActiveControl is TextField))
            {
                try
                {
                    if (Clipboard.ContainsFileDropList())
                    {
                        StringCollection sc = Clipboard.GetFileDropList();
                        string[] a = new string[sc.Count]; sc.CopyTo(a, 0);
                        pages[current].AcceptFiles(a);
                        return true;
                    }
                    else if (Clipboard.ContainsText() && !Clipboard.ContainsImage())
                    {
                        string t = Clipboard.GetText().Trim();
                        // a copied link on the Download page; "Copy as path" from Explorer anywhere else
                        if (pages[current] == youtube) { youtube.FocusLink(); return true; }
                        List<string> found = new List<string>();
                        foreach (string line in t.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string f = line.Trim().Trim('"');
                            try { if (f.Length > 3 && f.Length < 400 && System.IO.File.Exists(f)) found.Add(f); } catch { }
                        }
                        if (found.Count > 0) { pages[current].AcceptFiles(found.ToArray()); return true; }
                    }
                    else if (Clipboard.ContainsImage())
                    {
                        // a copied picture (screenshot, image from a browser...): save it and open it here
                        using (Image img = Clipboard.GetImage())
                        {
                            if (img == null) return true;
                            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FadiTools");
                            System.IO.Directory.CreateDirectory(dir);
                            string path = System.IO.Path.Combine(dir, L.T("pasted_image") + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".png");
                            img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                            pages[current].AcceptFiles(new[] { path });
                        }
                        return true;
                    }
                }
                catch { }
            }
            if (keyData == (Keys.Control | Keys.K)) { ShowPalette(); return true; }
            // Ctrl+Alt+Shift+S: pictures of every page (with made-up example files) for the project page
            if (keyData == (Keys.Control | Keys.Alt | Keys.Shift | Keys.S))
            {
                string dir = System.IO.Path.Combine(Paths.Downloads, "Fadi Tools screenshots");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "/screenshots " + Tools.Quote(dir)) { UseShellExecute = false }); toast.Show(L.T("shots_started"), true, null, null); }
                catch (Exception ex) { toast.Show(ex.Message, false, null, null); }
                return true;
            }
            // Ctrl+1…8: the pages, in the order of the side list
            if ((keyData & Keys.Modifiers) == Keys.Control && (keyData & Keys.KeyCode) >= Keys.D1 && (keyData & Keys.KeyCode) <= Keys.D9)
            {
                int i = (keyData & Keys.KeyCode) - Keys.D1;
                if (i < pages.Count) { Go(i); return true; }
            }
            if (keyData == (Keys.Control | Keys.Tab)) { Go((current + 1) % pages.Count); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { Go((current + pages.Count - 1) % pages.Count); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool system = e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing;
            bool busy = false;
            foreach (Page p in pages) if (p.Busy) busy = true;
            // closing while something runs: keep working in the tray (unless that's turned off, or Exit was chosen there)
            bool working = busy || Activity.RunningCount > 0;
            if (!system && !reallyExit && e.CloseReason == CloseReason.UserClosing && Environment.OSVersion.Platform == PlatformID.Win32NT && !SelfTest.Active &&
                ((working && Settings.Get("KeepInTray", "1") == "1") || Settings.Get("CloseToTray", "0") == "1"))
            {
                e.Cancel = true;
                GoToTray(!working);
                return;
            }
            if (!Visible && !system) { Show(); Activate(); }   // Exit from the tray: questions need the window
            if (SelfTest.Active) { }   // a test run leaves the person's unsaved work alone
            else if (system) Recovery.SaveNow();   // Windows is shutting down: keep the unsaved work for next time
            else
            {
                if (!studio.ConfirmClose()) { e.Cancel = true; return; }
                if (busy && !Ask.Confirm(this, L.T("close_busy_title"), L.T("close_busy"), L.T("close_anyway"), L.T("cancel"), AskKind.Warning)) { e.Cancel = true; return; }
                // closed on purpose: nothing to bring back next time
                Recovery.Clear(Recovery.VideoFile); Recovery.Clear(Recovery.PhotoFile); Recovery.Clear(Recovery.PhotoProject);
            }
            try { RemoveClipboardFormatListener(Handle); } catch { }
            Notifier.ExitRequested -= ExitFromTray;
            TrayPanel.ExitRequested -= ExitFromTray;
            TrayPanel.OpenRequested -= ComeBack;
            TrayPanel.CustomizeRequested -= ShowWheelSettings;
            Media.StopPreview();
            actTimer.Stop();
            Notifier.Dispose();
            if (busy) { Tools.ShuttingDown = true; Thread.Sleep(600); }
            Theme.Changed -= OnThemeChanged;
            Theme.Changing -= BeginLookChange;
            base.OnFormClosing(e);
        }
    }
}
