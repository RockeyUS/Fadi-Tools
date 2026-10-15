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
        ConvertPage convert; StudioPage studio; DownloadPage youtube; InspectPage inspect; RePage re; CompressPage compress; PdfPage pdf; RenamePage rename; SettingsPage settings;
        Toast toast;
        int current = -1;

        public MainForm(string[] files) : this(files, null) { }

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
            sidebar.Items.Add(new NavItem("nav_convert", Ico.Sync));
            sidebar.Items.Add(new NavItem("nav_studio", Ico.Photo));
            sidebar.Items.Add(new NavItem("nav_compress", "\uE73F"));
            sidebar.Items.Add(new NavItem("nav_pdf", "\uEA90"));
            sidebar.Items.Add(new NavItem("nav_rename", "\uE8AC"));
            sidebar.Items.Add(new NavItem("nav_download", Ico.Download));
            sidebar.Items.Add(new NavItem("nav_inspect", Ico.Search));
            sidebar.Items.Add(new NavItem("nav_re", Ico.Code));
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
            sidebar.OpenSearch += ShowPalette;
            actTimer.Tick += (s, e) => PollActivity();
            actTimer.Start();

            toast = new Toast();
            convert = new ConvertPage(); studio = new StudioPage(); youtube = new DownloadPage();
            inspect = new InspectPage(); re = new RePage(); compress = new CompressPage(); pdf = new PdfPage(); rename = new RenamePage(); settings = new SettingsPage();
            pages.AddRange(new Page[] { convert, studio, compress, pdf, rename, youtube, inspect, re, settings });
            for (int i = 0; i < pages.Count; i++) pages[i].Glyph = nav[i].Glyph;
            inspect.SendToConverter += f => { Go(0); convert.AcceptFiles(new[] { f }); };
            inspect.SendToRe += f => { Go(pages.IndexOf(re)); re.AcceptFiles(new[] { f }); };
            studio.Photos.SendToConverter += f => { Go(0); convert.AcceptFiles(new[] { f }); };

            Controls.Add(toast);
            Controls.Add(fader);
            foreach (Page p in pages) { p.Toast = toast; p.Owner = this; p.Visible = false; Controls.Add(p); }
            Controls.Add(sidebar);
            Controls.Add(Bar);

            Theme.Changed += OnThemeChanged;
            Shown += (s, e) =>
            {
                BeginInvoke((Action)(() =>
                {
                    bool tour = false;
                    if (Settings.Get("TourDone", "") != TourForm.Version && Environment.GetEnvironmentVariable("FADI_NO_TOUR") == null) { TourForm.ShowTour(this); tour = true; }
                    WhatsNew.ShowIfUpdated(this, tour);
                    OfferRestore();
                    Updater.CheckInBackground(toast, this);
                }));
            };
            Resize += (s, e) => DoLayout();
            HandleCreated += (s, e) => Theme.ApplyTitleBar(this);

            int start = 0;
            int.TryParse(Settings.Get("LastPage", "0"), out start);
            if (Settings.Get("PagesV", "") != "7") { start = 0; Settings.Set("PagesV", "7"); }   // page order changed in 1.3 (Rename)
            if (start < 0 || start >= pages.Count || start == SettingsIndex) start = 0;
            bool hasFiles = files != null && files.Length > 0;
            if (!hasFiles) Go(start);
            DoLayout();
            if (hasFiles) Route(files, verb);

            SingleInstance.FilesReceived += (paths, v) => BeginInvoke((Action)(() =>
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                if (paths.Length == 0) return;
                Route(paths, v);
            }));
        }

        int SettingsIndex { get { return pages.Count - 1; } }
        readonly List<NavItem> nav = new List<NavItem>();

        // ------------------------------------------------------------------ light/dark and language changes, animated
        PageFader lookFader;
        Point? revealAt;

        /// Called just before the theme or language changes: holds a picture of the window as it looks now,
        /// then (once everything has been redrawn the new way) fades — or, for the light/dark button, grows — into the new look.
        void BeginLookChange()
        {
            Point? from = revealAt; revealAt = null;
            if (!Visible || WindowState == FormWindowState.Minimized || !IsHandleCreated || (lookFader != null && lookFader.Visible)) return;
            Bitmap before = Fx.SnapWindow(this);
            if (before == null) return;
            if (lookFader == null) { lookFader = new PageFader(); Controls.Add(lookFader); }
            lookFader.Bounds = ClientRectangle;
            lookFader.RevealFrom = from;
            lookFader.Frames = from.HasValue ? 30 : 18;
            lookFader.Hold(before);
            BeginInvoke((Action)(() =>
            {
                lookFader.Bounds = ClientRectangle;
                lookFader.Run(before, Fx.SnapWindow(this), 0);
            }));
        }

        /// The app closed before some Studio work was saved (crash, power cut): offer to bring it back.
        void OfferRestore()
        {
            if (Environment.GetEnvironmentVariable("FADI_NO_RESTORE") != null) return;
            foreach (string f in new[] { Recovery.VideoFile, Recovery.PhotoFile })
            {
                if (!Recovery.Has(f)) continue;
                bool video = f == Recovery.VideoFile;
                DateTime t = DateTime.Now; try { t = System.IO.File.GetLastWriteTime(f); } catch { }
                if (Ask.Confirm(this, L.T("rec_title"), L.T(video ? "rec_video" : "rec_photo", TrimPage.Ago(t)), L.T("rec_restore"), L.T("rec_discard"), AskKind.Info))
                {
                    Go(pages.IndexOf(studio));
                    if (video) { studio.Show(studio.Videos); studio.Videos.OpenProject(f, true); }
                    else { studio.Show(studio.Photos); studio.Photos.RestoreEdit(f); }
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
                case "inspect": Go(pages.IndexOf(inspect)); inspect.AcceptFiles(paths); break;
                default:
                    Go(pages.IndexOf(convert));
                    if (!convert.Busy) convert.AcceptFiles(paths);
                    break;
            }
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
        void GoTo(Control c)
        {
            int i = PageIndexOf(c);
            if (i < 0) return;
            Go(i);
            if (pages[i] == studio && (c == studio.Videos || c == studio.Photos)) studio.Show((Page)c);
        }

        void PollActivity()
        {
            if (Activity.Version == actSeen) return;
            actSeen = Activity.Version;
            int run = Activity.RunningCount, fresh = 0;
            foreach (ActJob j in Activity.Snapshot()) if (!j.Running && j.State != ActState.Cancelled && j.Ended > actSeenAt) fresh++;
            bool changed = sidebar.ActivityRunning != run || sidebar.ActivityNew != fresh;
            sidebar.ActivityRunning = run; sidebar.ActivityNew = actPanel != null ? 0 : fresh;
            if (run > 0) Animator.Start(sidebar);
            else if (changed) sidebar.Invalidate();
            Taskbar.Set(this, Activity.Overall);
            foreach (ActJob j in Activity.TakeFinished()) Announce(j);
        }

        /// A job finished: when the app is in the background, say so with a Windows notification.
        void Announce(ActJob j)
        {
            if (j.State == ActState.Cancelled) return;
            bool background = Form.ActiveForm == null || WindowState == FormWindowState.Minimized;
            if (!background) return;
            bool ok = j.State == ActState.Done;
            string title = ok ? L.T("act_n_done", j.Title) : L.T("act_n_failed", j.Title);
            Control src = j.Source;
            Notifier.Show(this, title, j.Detail, ok, () => { GoTo(src); });
            Taskbar.Flash(this);
        }

        // ------------------------------------------------------------------ quick search (Ctrl+K)
        void ShowPalette() { CommandPalette.Show(this, PaletteItems()); }

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
            add("pal_studio_open", Ico.Photo, "edit photo video open studio تعديل صورة فيديو", () => { Go(pages.IndexOf(studio)); studio.Photos.PickFile(); });
            add("pal_proj_open", "\uE7C4", "open video project capcut timeline فتح مشروع فيديو", () => { string f = TrimPage.PickProject(this); if (f != null) { Go(pages.IndexOf(studio)); studio.Show(studio.Videos); studio.Videos.OpenProject(f, false); } });
            foreach (string rp in TrimPage.RecentProjects)
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
            add("pal_pdf_combine", "\uE8AB", "merge combine join pdf photos images to pdf دمج ملفات pdf صور", () => { Go(pages.IndexOf(pdf)); pdf.StartMode(0, true); });
            add("pal_pdf_pages", "\uEA90", "pdf pages rotate delete reorder organize صفحات تدوير حذف ترتيب", () => { Go(pages.IndexOf(pdf)); pdf.StartMode(1, true); });
            add("pal_pdf_split", "\uE8C9", "split pdf extract pages separate تقسيم استخراج صفحات", () => { Go(pages.IndexOf(pdf)); pdf.StartMode(1, true); });
            add("pal_pdf_images", Ico.Photo, "pdf to jpg png images pictures export صور", () => { Go(pages.IndexOf(pdf)); pdf.StartMode(1, true); });
            add("pal_ocr", "\uE8EF", "ocr copy text from picture image screenshot read scan نسخ نص من صورة لقطة شاشة", () => { Go(pages.IndexOf(studio)); studio.Show(studio.Photos); studio.Photos.CopyText(); });
            add("pal_rename", "\uE8AC", "rename batch many files names numbers numbered replace إعادة تسمية ملفات أسماء ترقيم", () => { Go(pages.IndexOf(rename)); rename.Browse(); });
            add("pal_vbg", "\uE77B", "remove video background green screen cut out person transparent إزالة خلفية الفيديو شاشة خضراء", () => { Go(pages.IndexOf(studio)); studio.Show(studio.Videos); if (studio.Videos.HasFile) studio.Videos.RemoveVideoBg(); else studio.Videos.Browse(false); });
            add("pal_sticker", "\uE899", "sticker whatsapp telegram discord emoji ملصق واتساب تيليجرام ديسكورد", () => { Go(pages.IndexOf(convert)); convert.Browse(); });
            add("pal_pdf_sign", "\uEC87", "sign pdf signature watermark page numbers password lock unlock توقيع علامة مائية ترقيم كلمة مرور قفل", () => { Go(pages.IndexOf(pdf)); pdf.StartMode(1, true); });
            add("pal_whatsnew", "\uE789", "what's new changes update version changelog الجديد التغييرات تحديث", () => WhatsNew.Show(this));
            add("pal_download", Ico.Download, "download youtube tiktok instagram link url video تنزيل يوتيوب رابط", () => { Go(pages.IndexOf(youtube)); youtube.FocusLink(); });
            add("pal_inspect", Ico.Search, "inspect file type hex checksum فحص ملف", () => { Go(pages.IndexOf(inspect)); inspect.Browse(); });
            add("pal_re", Ico.Code, "reverse engineer exe dll analyse program هندسة عكسية برنامج", () => Go(pages.IndexOf(re)));
            add("pal_activity", "\uE9D9", "activity running jobs progress tasks النشاط المهام", () => ShowActivity(sidebar.FootRect(0)));
            Action<string, string, string, Action> set = (key, glyph, words, run) => l.Add(new PaletteItem { Group = gs, Title = L.T(key), Glyph = glyph, Words = L.Both(key) + " " + (words ?? ""), Run = run });
            set(Theme.Dark ? "pal_light" : "pal_dark", Theme.Dark ? Ico.Sun : Ico.Moon, "theme dark light mode appearance مظهر داكن فاتح", () => { Settings.Set("Theme", Theme.Dark ? "light" : "dark"); Theme.ApplySettings(); Theme.RaiseChanged(); });
            set(Theme.RTL ? "pal_english" : "pal_arabic", Ico.Globe, "language arabic english لغة عربي انجليزي", () => { Settings.Set("Language", Theme.RTL ? "en" : "ar"); Theme.ApplySettings(); Theme.RaiseChanged(); });
            set("pal_settings", Ico.Settings, "settings accent colour color save folder updates notifications الإعدادات لون", () => Go(SettingsIndex));
            return l;
        }

        void ShowActivity(Rectangle row)
        {
            if (actPanel != null) { actPanel.Close(); return; }
            if ((DateTime.Now - actPanelClosed).TotalMilliseconds < 250) return;   // the click that closed it
            actPanel = new ActivityPanel();
            actPanel.GlyphOf = GlyphFor;
            actPanel.Navigate += GoTo;
            actPanel.FormClosed += (s, e) => { actPanel = null; actPanelClosed = DateTime.Now; actSeenAt = DateTime.Now; sidebar.ActivityNew = 0; sidebar.Invalidate(); };
            actSeenAt = DateTime.Now; sidebar.ActivityNew = 0; sidebar.Invalidate();
            actPanel.ShowBeside(sidebar, row);
        }
        public void ShowStudio(string[] files) { Go(1); studio.AcceptFiles(files); }

        readonly PageFader fader = new PageFader();

        void Go(int i)
        {
            if (i == current) return;
            int was = current;
            // picture of the page we're leaving, for a soft cross-fade
            Bitmap before = was >= 0 && Visible && WindowState != FormWindowState.Minimized ? PageFader.Snap(pages[was]) : null;
            current = i;
            sidebar.Selected = i;
            for (int k = 0; k < pages.Count; k++) pages[k].Visible = k == i;
            pages[i].OnShown();
            pages[i].DoLayout();
            sidebar.Invalidate();
            if (before != null)
            {
                fader.Bounds = pages[i].Bounds;
                fader.Run(before, PageFader.Snap(pages[i]), i > was ? 1 : -1);
            }
            if (i != SettingsIndex) Settings.Set("LastPage", i.ToString());
        }

        void DoLayout()
        {
            if (ClientSize.Width <= 0) return;
            int sw = Theme.S(236), bh = BarHeight;
            Bar.Bounds = new Rectangle(0, 0, ClientSize.Width, Bar.Height);
            Bar.SideWidth = sw; Bar.SideRight = Theme.RTL;
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
            if (keyData == (Keys.Control | Keys.Tab)) { Go((current + 1) % pages.Count); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { Go((current + pages.Count - 1) % pages.Count); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool system = e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing;
            bool busy = false;
            foreach (Page p in pages) if (p.Busy) busy = true;
            if (system) Recovery.SaveNow();   // Windows is shutting down: keep the unsaved work for next time
            else
            {
                if (!studio.ConfirmClose()) { e.Cancel = true; return; }
                if (busy && !Ask.Confirm(this, L.T("close_busy_title"), L.T("close_busy"), L.T("close_anyway"), L.T("cancel"), AskKind.Warning)) { e.Cancel = true; return; }
                // closed on purpose: nothing to bring back next time
                Recovery.Clear(Recovery.VideoFile); Recovery.Clear(Recovery.PhotoFile);
            }
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
