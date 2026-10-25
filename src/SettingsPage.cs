using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    public class SettingsPage : Page
    {
        Rectangle appearance, saving, about, engines, integration, wheelCard, addons;
        readonly WheelEntry wheelEntry = new WheelEntry();
        readonly WatchEntry watchEntry = new WatchEntry();
        WheelPage wheelPage;
        WatchPage watchPage;
        Rectangle watchCard;
        int aboutLogoY, aboutTextW;
        bool aboutTallNow;
        Rectangle themeLbl, langLbl, openAfterLbl, askSaveLbl, aboutR, animLbl;
        readonly Toggle askSave = new Toggle(), anim = new Toggle();
        float scrollTo, scrollNow; int maxScroll;
        Rectangle[] engineRows = new Rectangle[4], integRows = new Rectangle[11];
        Segments theme, lang, logoSeg;
        Swatches accent;
        Rectangle accentLbl, logoLbl;
        static readonly Color[] AccentColors = {
            Color.FromArgb(238, 98, 46), Color.FromArgb(108, 112, 120), Color.FromArgb(59, 130, 246), Color.FromArgb(124, 58, 237),
            Color.FromArgb(219, 39, 119), Color.FromArgb(220, 38, 38), Color.FromArgb(22, 163, 74), Color.FromArgb(13, 148, 136) };
        FolderButton folder;
        Btn btnReset, btnUninstall, btnLo, btnYt, btnYtUpdate;
        Toggle openAfter;
        Toggle[] integ = new Toggle[11];
        // Windows integration, in three groups: -1, -2, -3 are the group headings
        static readonly int[] IntegOrder = { -1, 0, 1, 2, 7, -2, 3, 9, 10, -3, 4, 6, 8, 5 };
        static readonly string[] IntegGroups = { "int_g_explorer", "int_g_notify", "int_g_background" };
        readonly Rectangle[] integHeads = new Rectangle[3];
        Progress ytProgress;
        string ytStatus = "";
        bool ytBusy;

        public SettingsPage() : base("set_title", "set_sub")
        {
            theme = new Segments(() => L.T("th_light"), () => L.T("th_dark"), () => L.T("th_system"));
            theme.Glyphs = new[] { Ico.Sun, Ico.Moon, Ico.Contrast };
            theme.SelectedChanged += (s, e) =>
            {
                Settings.Set("Theme", new[] { "light", "dark", "system" }[theme.SelectedIndex]);
                Theme.ApplySettings(); Theme.RaiseChanged();
            };
            lang = new Segments(() => "English", () => "العربية");
            lang.SelectedChanged += (s, e) =>
            {
                Settings.Set("Language", lang.SelectedIndex == 1 ? "ar" : "en");
                Theme.ApplySettings(); Theme.RaiseChanged();
            };
            accent = new Swatches { Palette = AccentColors, LivePreview = false };
            accent.Selected = Theme.Accent.IsEmpty ? AccentColors[0] : Theme.Accent;
            accent.SelectedChanged += (s, e) =>
            {
                // the first (clay) swatch is the default Claude-like look
                Settings.Set("Accent", accent.Selected.ToArgb() == AccentColors[0].ToArgb() ? "" : ColorTranslator.ToHtml(Color.FromArgb(accent.Selected.R, accent.Selected.G, accent.Selected.B)));
                Theme.ApplySettings(); Theme.RaiseChanged();
            };
            logoSeg = new Segments(() => L.T("logo_classic"), () => L.T("logo_night"));
            string lc = Settings.Get("Logo", "");
            logoSeg.SetSilently(Theme.LogoDark ? 1 : 0);
            logoSeg.SelectedChanged += (s, e) =>
            {
                Settings.Set("Logo", logoSeg.SelectedIndex == 1 ? "dark" : "light");
                Theme.ResetLogo(); Theme.RaiseChanged();
            };
            folder = new FolderButton();
            folder.Picked += (s, e) => Refresh2();
            btnReset = new Btn(() => L.T("reset_downloads"), Ico.Download, BtnKind.Ghost);
            btnReset.Click += (s, e) => { Settings.OutputDir = ""; Refresh2(); };
            askSave.CheckedChanged += (s, e) => SaveAsk.Enabled = askSave.Checked;
            openAfter = new Toggle();
            openAfter.CheckedChanged += (s, e) => Settings.OpenAfter = openAfter.Checked;
            btnUpdate = new Btn(() => checking ? L.T("upd_checking") : L.T("upd_check"), Ico.Refresh, BtnKind.Secondary);
            btnUpdate.Click += (s, e) => CheckUpdates();
            btnTips = new Btn(() => L.T("tour_again"), Ico.Info, BtnKind.Ghost);
            btnTips.FontSize = 8.75f;
            btnTips.Click += (s, e) => TourForm.ShowTour(FindForm());
            btnNew = new Btn(() => L.T("wn_btn"), "\uE789", BtnKind.Ghost) { FontSize = 8.75f };
            btnNew.Click += (s, e) => WhatsNew.Show(FindForm());
            btnUninstall = new Btn(() => L.T("uninstall"), Ico.Delete, BtnKind.Danger);
            btnUninstall.Click += (s, e) => { if (Shell.Uninstall(FindForm())) Application.Exit(); };
            btnUninstall.Visible = !Paths.Portable;
            btnLo = new Btn(() => LibreOffice.Busy ? L.T("installing") : L.T("install"), Ico.Download, BtnKind.Secondary);
            btnLo.Click += (s, e) => { LibreOffice.Enable(this, ok => Refresh2()); Refresh2(); };
            btnYt = new Btn(() => L.T("install"), Ico.Download, BtnKind.Secondary);
            btnYt.Click += (s, e) => InstallYt();
            btnYtUpdate = new Btn(() => L.T("yt_update"), Ico.Refresh, BtnKind.Ghost);
            btnYtUpdate.Click += (s, e) => UpdateYt();
            ytProgress = new Progress { Visible = false };
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                integ[i] = new Toggle();
                integ[i].CheckedChanged += (s, e) =>
                {
                    Settings.Set(new[] { "IntDesktop", "IntMenu", "IntViewer" }[k], integ[k].Checked ? "1" : "0");
                    try { Shell.Integrate(Settings.Get("IntDesktop", "1") == "1", Settings.Get("IntMenu", "1") == "1", Settings.Get("IntViewer", "1") == "1"); }
                    catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
                };
            }
            integ[3] = new Toggle();
            integ[3].CheckedChanged += (s, e) => { Notifier.Enabled = integ[3].Checked; integ[9].Enabled = integ[10].Enabled = integ[3].Checked; Invalidate(); };
            integ[9] = new Toggle();
            integ[9].CheckedChanged += (s, e) => Notifier.LongOnly = integ[9].Checked;
            integ[10] = new Toggle();
            integ[10].CheckedChanged += (s, e) => Notifier.Sound = integ[10].Checked;
            integ[4] = new Toggle();
            integ[4].CheckedChanged += (s, e) => { Settings.Set("KeepInTray", integ[4].Checked ? "1" : "0"); Notifier.ShowResident(FindForm(), integ[4].Checked); };
            integ[5] = new Toggle();
            integ[5].CheckedChanged += (s, e) => Settings.Set("WatchLinks", integ[5].Checked ? "1" : "0");
            integ[6] = new Toggle();
            integ[6].CheckedChanged += (s, e) => { Settings.Set("CloseToTray", integ[6].Checked ? "1" : "0"); DragWheel.NotifyChanged(); };
            integ[8] = new Toggle();
            integ[8].CheckedChanged += (s, e) => Shell.SetStartsWithWindows(integ[8].Checked);
            integ[7] = new Toggle();
            integ[7].CheckedChanged += (s, e) =>
            {
                DragWheel.Switch(integ[7].Checked, FindForm());
                // the wheel only works while Fadi Tools runs: offer to keep it running when the window closes
                if (integ[7].Checked && !integ[6].Checked)
                    ShowToast(L.T("wheel_needs_tray"), true, () => L.T("wheel_keep_running"), () => integ[6].Checked = true);
            };
            anim.CheckedChanged += (s, e) => Settings.Set("Animations", anim.Checked ? "1" : "0");
            btnZip = new Btn(() => L.T("zip_make"), "\uE8B7", BtnKind.Secondary);
            btnZip.Click += (s, e) => MakeZip();
            // everything sits on one body that slides up and down as a whole when the page scrolls
            // (moving each button on its own made the page wobble while scrolling fast)
            body = new Body(this);
            thumb = new Thumb(this);
            Controls.Add(thumb); Controls.Add(body);
            body.Controls.AddRange(new Control[] { btnUpdate, btnTips, btnNew, askSave, btnZip, anim, wheelEntry, watchEntry });
            wheelEntry.Click += (s, e) => OpenWheel();
            watchEntry.Click += (s, e) => OpenWatch();
            // switched from the tray panel or the wheel's own page: the switches here follow
            DragWheel.Changed += () => { if (IsHandleCreated && !IsDisposed) { LoadValues(); wheelEntry.Invalidate(); } };
            body.Controls.AddRange(new Control[] { theme, lang, accent, logoSeg, folder, btnReset, openAfter, btnUninstall, btnLo, btnYt, btnYtUpdate, ytProgress });
            body.Controls.AddRange(integ);
            LoadValues();
        }

        Btn btnUpdate, btnTips, btnZip, btnNew;

        // ---- add-ons: what Fadi Tools downloaded, where it is, how big, and a button to remove it
        List<AddOn> aoList = new List<AddOn>();
        readonly List<string> aoPaths = new List<string>();
        readonly List<Rectangle> aoRows = new List<Rectangle>();
        readonly List<Btn> aoOpen = new List<Btn>(), aoDel = new List<Btn>();
        readonly HashSet<string> aoBusy = new HashSet<string>();
        bool aoLoading;

        /// Looks again at what's installed (on a background thread: sizes take a moment).
        void RefreshAddOns()
        {
            if (aoLoading) return;
            aoLoading = true;
            Thread t = new Thread(() =>
            {
                List<AddOn> found = new List<AddOn>();
                List<string> paths = new List<string>();
                foreach (AddOn a in AddOns.All())
                {
                    string d = a.Path2;
                    if (d == null) continue;
                    // sizes only for Fadi Tools' own folders (an FFmpeg found elsewhere may sit in a big shared folder)
                    a.Size = !a.Machine && d.StartsWith(Paths.ToolsDir, StringComparison.OrdinalIgnoreCase) ? AddOns.SizeOf(d) : -1;
                    found.Add(a); paths.Add(d);
                }
                Ui(() =>
                {
                    aoLoading = false;
                    aoList = found; aoPaths.Clear(); aoPaths.AddRange(paths);
                    foreach (Btn b in aoOpen) { body.Controls.Remove(b); b.Dispose(); }
                    foreach (Btn b in aoDel) { body.Controls.Remove(b); b.Dispose(); }
                    aoOpen.Clear(); aoDel.Clear();
                    for (int i = 0; i < aoList.Count; i++)
                    {
                        AddOn a = aoList[i]; string dir = aoPaths[i];
                        Btn o = new Btn(() => "", Ico.Folder, BtnKind.Ghost) { GlyphSize = 10f };
                        o.Click += (s, e) => AddOns.OpenFolder(dir);
                        o.AccessibleName = L.T("ao_open");
                        o.MouseEnter += (s, e) => aoTip.SetToolTip(o, L.T("ao_open"));
                        Btn d = new Btn(() => aoBusy.Contains(a.Key) ? L.T("ao_removing") : L.T("uninstall"), Ico.Delete, BtnKind.Secondary) { FontSize = 8.75f, GlyphSize = 9f, Visible = !a.Required };
                        d.Click += (s, e) => RemoveAddOn(a);
                        aoOpen.Add(o); aoDel.Add(d);
                        body.Controls.Add(o); body.Controls.Add(d);
                    }
                    DoLayout();
                });
            }) { IsBackground = true };
            t.Start();
        }
        readonly ToolTip aoTip = new ToolTip();

        void RemoveAddOn(AddOn a)
        {
            if (aoBusy.Contains(a.Key)) return;
            string where = a.Path2;
            if (where == null) { RefreshAddOns(); return; }
            string name = L.T(a.NameKey);
            if (!Ask.Confirm(FindForm(), L.T("ao_q", name), L.T(a.Machine ? "ao_q_machine" : "ao_q_d", name, where), L.T("uninstall"), L.T("cancel"), AskKind.Warning)) return;
            aoBusy.Add(a.Key);
            body.Invalidate(true);
            Thread t = new Thread(() =>
            {
                string msg;
                try { msg = AddOns.Remove(a); } catch (Exception ex) { msg = ex.Message; }
                Ui(() =>
                {
                    aoBusy.Remove(a.Key);
                    if (a.Key == "fonts") FontPack.Forget();
                    Tools.Reset();
                    Refresh2();
                    RefreshAddOns();
                    if (msg != null) ShowToast(msg, true, null, null);
                });
            }) { IsBackground = true };
            t.Start();
        }

        int AddOnsHeight { get { return Theme.S(54) + Math.Max(1, aoList.Count) * Theme.S(64) + Theme.S(8); } }
        Rectangle zipRow; bool zipping; string zipStatus;

        void MakeZip()
        {
            if (zipping) return;
            string path = SaveAsk.File(FindForm(), "Fadi Tools.zip", "zip");
            if (path == null) return;
            zipping = true; btnZip.Enabled = false; btnZip.Invalidate();
            Thread t = new Thread(() =>
            {
                string err = null;
                DateTime last = DateTime.MinValue;
                try
                {
                    PortableZip.Make(path, (name, f) =>
                    {
                        if ((DateTime.Now - last).TotalMilliseconds < 250) return;
                        last = DateTime.Now;
                        try { BeginInvoke((Action)(() => { zipStatus = L.T("zip_working", (int)(f * 100)); body.Invalidate(); })); } catch { }
                    }, () => IsDisposed);
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        zipping = false; btnZip.Enabled = true; btnZip.Invalidate(); zipStatus = null; body.Invalidate();
                        if (err != null) ShowToast(err, false, null, null);
                        else ShowToast(L.T("zip_done", Path.GetFileName(path), Theme.HumanSize(new FileInfo(path).Length)), true, () => L.T("show_file"), () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } });
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }
        bool checking;

        void CheckUpdates()
        {
            if (checking) return;
            checking = true; btnUpdate.Enabled = false; btnUpdate.Invalidate();
            Thread t = new Thread(() =>
            {
                Updater.Release r = null; string err = null;
                try { r = Updater.Latest(); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        checking = false; btnUpdate.Enabled = true; btnUpdate.Invalidate();
                        if (err != null) ShowToast(L.T("upd_offline"), false, null, null);
                        else if (r == null || !Updater.IsNewer(r.Version)) ShowToast(L.T("upd_latest", Shell.Version), true, null, null);
                        else Updater.Run(FindForm(), r);
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }

        void LoadValues()
        {
            string t = Settings.Get("Theme", "system");
            theme.SetSilently(t == "light" ? 0 : t == "dark" ? 1 : 2);
            lang.SetSilently(Theme.RTL ? 1 : 0);
            openAfter.SetSilently(Settings.OpenAfter);
            askSave.SetSilently(SaveAsk.Enabled);
            anim.SetSilently(Settings.Get("Animations", "1") != "0");
            integ[0].SetSilently(Settings.Get("IntDesktop", "1") == "1");
            integ[1].SetSilently(Settings.Get("IntMenu", "1") == "1");
            integ[2].SetSilently(Settings.Get("IntViewer", "1") == "1");
            integ[3].SetSilently(Notifier.Enabled);
            integ[9].SetSilently(Notifier.LongOnly);
            integ[10].SetSilently(Notifier.Sound);
            integ[9].Enabled = integ[10].Enabled = Notifier.Enabled;
            integ[4].SetSilently(Settings.Get("KeepInTray", "1") == "1");
            integ[5].SetSilently(Settings.Get("WatchLinks", "1") == "1");
            integ[6].SetSilently(Settings.Get("CloseToTray", "0") == "1");
            integ[7].SetSilently(DragWheel.On);
            integ[8].SetSilently(Shell.StartsWithWindows);
        }

        public override void OnShown() { Tools.Reset(); LoadValues(); Refresh2(); RefreshAddOns(); if (!openingWheel) CloseWheel(false); }
        bool openingWheel;

        // ------------------------------------------------------------------ pages of their own on top: Donut Wheel, Watch folders
        Page sub;   // the one showing, if any

        T Make<T>(ref T page) where T : Page, new()
        {
            if (page == null)
            {
                page = new T { Toast = Toast, Owner = Owner, Glyph = Glyph, Visible = false };
                Controls.Add(page);
            }
            return page;
        }

        void OpenSub(Page p)
        {
            if (sub == p && p.Visible) return;
            if (sub != null && sub != p) sub.Visible = false;
            if (Visible && Fx.Motion) Fx.Crossfade(this, 220, 1);
            sub = p;
            p.Bounds = ClientRectangle;
            p.Visible = true; p.BringToFront();
            body.Visible = thumb.Visible = false;
            p.OnShown(); p.DoLayout();
            p.Focus();
        }

        void CloseSub(bool animate)
        {
            if (sub == null || !sub.Visible) { sub = null; return; }
            if (animate && Visible && Fx.Motion) Fx.Crossfade(this, 220, -1);
            sub.Visible = false; sub = null;
            body.Visible = true;
            DoLayout();
            wheelEntry.Invalidate(); watchEntry.Invalidate();
        }

        /// Shows the Donut Wheel page over Settings.
        public void OpenWheel()
        {
            bool first = wheelPage == null;
            Make(ref wheelPage);
            if (first) wheelPage.BackRequested += () => CloseSub(true);
            OpenSub(wheelPage);
        }

        /// Shows Watch folders over Settings.
        public void OpenWatch()
        {
            bool first = watchPage == null;
            Make(ref watchPage);
            if (first) watchPage.BackRequested += () => CloseSub(true);
            OpenSub(watchPage);
        }

        /// Back to Settings.
        public void CloseWheel(bool animate) { CloseSub(animate); }
        public bool WheelOpen { get { return sub != null && sub.Visible; } }

        /// From the tray: open Settings right at the Donut Wheel page.
        public void PrepareWheel() { openingWheel = true; }
        public void AfterShownWheel() { openingWheel = false; OpenWheel(); }
        public override void Restyle() { LoadValues(); Refresh2(); body.Invalidate(true); thumb.Invalidate(); }

        void Refresh2()
        {
            btnReset.Visible = !Settings.OutputIsDefault;
            btnLo.Visible = Tools.Soffice == null;
            btnLo.Enabled = !LibreOffice.Busy;
            btnYt.Visible = !YtDlp.Ready && !ytBusy;
            btnYtUpdate.Visible = YtDlp.Ready && !ytBusy;
            ytProgress.Visible = ytBusy;
            folder.Invalidate();
            DoLayout();
        }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, gap = Theme.S(18), ip = Theme.S(20);
            cards.Clear();
            // narrow window: one column (two squeezed columns made the buttons pile on top of the text)
            bool one = (W - p * 2 - gap) / 2 < Theme.S(430);
            int colW = one ? W - p * 2 : (W - p * 2 - gap) / 2;
            int lx = p, rx = one ? p : p + colW + gap, y = Top0;
            appearance = new Rectangle(lx, y, colW, Theme.S(292));
            saving = new Rectangle(lx, appearance.Bottom + gap, colW, Theme.S(236));
            // the about card: when "Show tips" and "What's new" don't fit beside the buttons, they get a row of their own
            int uw = Math.Max(Theme.S(150), Math.Max(btnUninstall.PreferredWidth, btnUpdate.PreferredWidth));
            int tw = Math.Max(Theme.S(120), btnTips.PreferredWidth), nw2 = Math.Max(Theme.S(110), btnNew.PreferredWidth);
            bool aboutTall = tw + Theme.S(4) + nw2 > colW - ip * 2 - Theme.S(50) - uw - Theme.S(12);
            if (one)
            {
                engines = new Rectangle(lx, saving.Bottom + gap, colW, Theme.S(296));
                addons = new Rectangle(lx, engines.Bottom + gap, colW, AddOnsHeight);
                integration = new Rectangle(lx, addons.Bottom + gap, colW, Theme.S(790));
                wheelCard = new Rectangle(lx, integration.Bottom + gap, colW, Theme.S(92));
                watchCard = new Rectangle(lx, wheelCard.Bottom + gap, colW, Theme.S(92));
                about = new Rectangle(lx, watchCard.Bottom + gap, colW, aboutTall ? Theme.S(152) : Theme.S(112));
            }
            else
            {
                wheelCard = new Rectangle(lx, saving.Bottom + gap, colW, Theme.S(92));
                watchCard = new Rectangle(lx, wheelCard.Bottom + gap, colW, Theme.S(92));
                about = new Rectangle(lx, watchCard.Bottom + gap, colW, aboutTall ? Theme.S(152) : Math.Max(Theme.S(96), Math.Min(Theme.S(120), H - watchCard.Bottom - gap - p)));
                engines = new Rectangle(rx, y, colW, Theme.S(296));
                addons = new Rectangle(rx, engines.Bottom + gap, colW, AddOnsHeight);
                integration = new Rectangle(rx, addons.Bottom + gap, colW, Theme.S(790));
            }
            cards.AddRange(new[] { appearance, saving, about, engines, addons, integration });
            Place(wheelEntry, wheelCard);   // a card that is a button: it paints itself
            Place(watchEntry, watchCard);

            int x = appearance.X + ip, w = colW - ip * 2;
            int half = (w - Theme.S(12)) * 3 / 5;
            themeLbl = new Rectangle(x, appearance.Y + Theme.S(50), half, Theme.S(20));
            Place(theme, new Rectangle(x, appearance.Y + Theme.S(72), half, Theme.S(34)));
            langLbl = new Rectangle(x + half + Theme.S(12), appearance.Y + Theme.S(50), w - half - Theme.S(12), Theme.S(20));
            Place(lang, new Rectangle(x + half + Theme.S(12), appearance.Y + Theme.S(72), w - half - Theme.S(12), Theme.S(34)));
            accentLbl = new Rectangle(x, appearance.Y + Theme.S(114), w, Theme.S(20));
            Place(accent, new Rectangle(x, appearance.Y + Theme.S(134), w, Theme.S(34)));
            logoLbl = new Rectangle(x, appearance.Y + Theme.S(174), w, Theme.S(20));
            Place(logoSeg, new Rectangle(x, appearance.Y + Theme.S(196), w, Theme.S(34)));
            animLbl = new Rectangle(x, appearance.Y + Theme.S(242), w - Theme.S(60), Theme.S(34));
            Place(anim, new Rectangle(x + w - Theme.S(52), appearance.Y + Theme.S(242), Theme.S(52), Theme.S(34)));

            Place(folder, new Rectangle(x, saving.Y + Theme.S(52), w, Theme.S(58)));
            int rw = Math.Max(Theme.S(150), btnReset.PreferredWidth);
            Place(btnReset, new Rectangle(x, saving.Y + Theme.S(114), rw, Theme.S(30)));
            askSaveLbl = new Rectangle(x, saving.Y + Theme.S(150), w - Theme.S(60), Theme.S(34));
            Place(askSave, new Rectangle(x + w - Theme.S(52), saving.Y + Theme.S(150), Theme.S(52), Theme.S(34)));
            openAfterLbl = new Rectangle(x, saving.Y + Theme.S(190), w - Theme.S(60), Theme.S(34));
            Place(openAfter, new Rectangle(x + w - Theme.S(52), saving.Y + Theme.S(190), Theme.S(52), Theme.S(34)));

            aboutR = new Rectangle(about.X + ip, about.Y, colW - ip * 2, about.Height);
            int bt = aboutTall ? about.Y + Theme.S(20) : about.Y + (about.Height - Theme.S(76)) / 2;
            Place(btnUpdate, new Rectangle(about.Right - ip - uw, bt, uw, Theme.S(34)));
            Place(btnUninstall, new Rectangle(about.Right - ip - uw, bt + Theme.S(42), uw, Theme.S(34)));
            aboutTallNow = aboutTall;
            aboutTextW = Math.Max(Theme.S(80), aboutR.Width - Theme.S(56) - uw - Theme.S(10));
            aboutLogoY = aboutTall ? about.Y + Theme.S(22) : about.Y + (about.Height - Theme.S(44)) / 2 - Theme.S(14);
            int ty = aboutTall ? about.Y + Theme.S(106) : about.Y + about.Height / 2 + Theme.S(16);
            Place(btnTips, new Rectangle(aboutR.X + Theme.S(50), ty, tw, Theme.S(28)));
            Place(btnNew, new Rectangle(aboutR.X + Theme.S(50) + tw + Theme.S(4), ty, nw2, Theme.S(28)));

            int ex = engines.X + ip, ew = colW - ip * 2;
            for (int i = 0; i < 4; i++) engineRows[i] = new Rectangle(ex, engines.Y + Theme.S(50) + i * Theme.S(58), ew, Theme.S(54));
            int bw = Theme.S(120);
            Place(btnLo, new Rectangle(ex + ew - bw, engineRows[2].Y + Theme.S(10), bw, Theme.S(34)));
            Place(btnYt, new Rectangle(ex + ew - bw, engineRows[3].Y + Theme.S(10), bw, Theme.S(34)));
            int uw2 = Math.Max(Theme.S(120), btnYtUpdate.PreferredWidth);
            Place(btnYtUpdate, new Rectangle(ex + ew - uw2, engineRows[3].Y + Theme.S(10), uw2, Theme.S(34)));
            Place(ytProgress, new Rectangle(ex + ew - bw, engineRows[3].Y + Theme.S(24), bw, Theme.S(5)));

            aoRows.Clear();
            for (int i = 0; i < aoList.Count; i++)
            {
                Rectangle r = new Rectangle(ex, addons.Y + Theme.S(50) + i * Theme.S(64), ew, Theme.S(60));
                aoRows.Add(r);
                int dw = Math.Max(Theme.S(112), aoDel[i].PreferredWidth + Theme.S(6)), bh = Theme.S(32), by = r.Y + (r.Height - bh) / 2;
                Place(aoDel[i], new Rectangle(r.Right - dw, by, dw, bh));
                Place(aoOpen[i], new Rectangle(r.Right - (aoDel[i].Visible ? dw + Theme.S(6) : 0) - bh, by, bh, bh));
            }

            // the two notification choices sit right under "Windows notification…", a little indented
            int iy = integration.Y + Theme.S(50);
            foreach (int i in IntegOrder)
            {
                if (i < 0) { integHeads[-i - 1] = new Rectangle(ex, iy + (i == -1 ? 0 : Theme.S(8)), ew, Theme.S(26)); iy += Theme.S(i == -1 ? 28 : 36); continue; }
                bool indented = i == 9 || i == 10;
                int ind = indented ? Theme.S(22) : 0;
                integRows[i] = new Rectangle(ex + ind, iy, ew - ind, Theme.S(48));
                Place(integ[i], new Rectangle(ex + ew - Theme.S(52), integRows[i].Y + Theme.S(7), Theme.S(52), Theme.S(34)));
                iy += Theme.S(52);
            }
            zipRow = new Rectangle(ex, iy + Theme.S(6), ew, Theme.S(48));
            integration.Height = zipRow.Bottom + Theme.S(14) - integration.Y;
            for (int c = 0; c < cards.Count; c++) if (cards[c].Location == integration.Location) cards[c] = integration;
            int zw = Math.Max(Theme.S(130), btnZip.PreferredWidth);
            Place(btnZip, new Rectangle(ex + ew - zw, zipRow.Y + Theme.S(7), zw, Theme.S(34)));
            // long page: scroll with the mouse wheel when the window is short
            int bottom = Math.Max(about.Bottom, integration.Bottom) + p;
            maxScroll = Math.Max(0, bottom - H);
            if (scrollTo > maxScroll) scrollTo = maxScroll;
            if (scrollNow > maxScroll) scrollNow = maxScroll;
            body.Bounds = new Rectangle(0, -(int)Math.Round(scrollNow), W, Math.Max(H, bottom));
            thumb.Bounds = Theme.Mirror(new Rectangle(W - Theme.S(10), 0, Theme.S(10), H), W);
            thumb.Visible = maxScroll > 0 && !WheelOpen;
            if (WheelOpen) sub.Bounds = ClientRectangle;
            thumb.BringToFront();
            body.Invalidate();
            thumb.Invalidate();
        }

        void Head(Graphics g, Rectangle card, string glyph, string key)
        {
            Rectangle gr = new Rectangle(card.X + Theme.S(18), card.Y + Theme.S(14), Theme.S(26), Theme.S(28));
            Theme.Glyph(g, glyph, 12f, Theme.Ink, M(gr));
            Label(g, L.T(key), Theme.F(11.5f, FontStyle.Bold), Theme.Ink, new Rectangle(card.X + Theme.S(50), card.Y + Theme.S(14), card.Width - Theme.S(70), Theme.S(28)));
        }

        protected override void PaintContent(Graphics g)
        {
            Head(g, appearance, Ico.Contrast, "appearance");
            Label(g, L.T("theme"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, themeLbl);
            Label(g, L.T("language"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, langLbl);
            Label(g, L.T("accent_color"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, accentLbl);
            Label(g, L.T("app_logo"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, logoLbl);
            Label(g, L.T("set_anim"), Theme.F(9.5f), Theme.Ink, animLbl);
            Head(g, saving, Ico.Save, "saving");
            Label(g, L.T("open_after"), Theme.F(9.5f), Theme.Ink, openAfterLbl);
            Label(g, L.T("ask_save"), Theme.F(9.5f), Theme.Ink, askSaveLbl);
            // about
            Rectangle logo = new Rectangle(aboutR.X, aboutLogoY, Theme.S(44), Theme.S(44));
            Theme.DrawLogo(g, M(logo));
            Label(g, L.T("app_name"), Theme.F(11f, FontStyle.Bold), Theme.Ink, new Rectangle(aboutR.X + Theme.S(56), logo.Y, aboutTextW, Theme.S(24)));
            Label(g, L.T("version", Shell.Version), Theme.F(9f), Theme.InkSoft, new Rectangle(aboutR.X + Theme.S(56), logo.Y + Theme.S(24), aboutTextW, aboutTallNow ? Theme.S(40) : Theme.S(20)), Align.Start, !aboutTallNow, aboutTallNow);

            Head(g, engines, Ico.Settings, "engines");
            string[] names = { L.T("eng_ff"), L.T("eng_im"), L.T("eng_lo"), L.T("eng_yt") };
            bool[] ok = { Tools.FFmpeg != null, Tools.Magick != null, Tools.Soffice != null, YtDlp.Ready };
            string[] sub = { L.T("eng_ff_sub"), L.T("eng_im_sub"), L.T("eng_lo_sub"), ytBusy ? ytStatus : L.T("eng_yt_sub") };
            for (int i = 0; i < 4; i++)
            {
                Rectangle r = engineRows[i];
                Rectangle dot = new Rectangle(r.X, r.Y + Theme.S(13), Theme.S(28), Theme.S(28));
                Theme.FillRound(g, M(dot), Theme.S(14), ok[i] ? Theme.SuccessBg : Theme.Selected);
                Theme.Glyph(g, ok[i] ? Ico.Check : Ico.Download, 8.5f, ok[i] ? Theme.Success : Theme.InkFaint, M(dot));
                bool hasBtn = (i == 2 && btnLo.Visible) || (i == 3);
                int tw = r.Width - Theme.S(40) - (hasBtn ? Theme.S(130) : Theme.S(90));
                Label(g, names[i], Theme.F(9.75f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(40), r.Y + Theme.S(8), tw, Theme.S(22)));
                Label(g, sub[i], Theme.F(8.5f), Theme.InkSoft, new Rectangle(r.X + Theme.S(40), r.Y + Theme.S(29), tw, Theme.S(20)));
                if (!hasBtn)
                    Label(g, ok[i] ? L.T("installed") : L.T("not_installed"), Theme.F(8.75f, FontStyle.Bold), ok[i] ? Theme.Success : Theme.InkFaint, new Rectangle(r.Right - Theme.S(110), r.Y, Theme.S(110), r.Height), Align.End);
                if (i < 3) using (System.Drawing.Pen lp = new System.Drawing.Pen(Theme.Line)) { Rectangle l = M(new Rectangle(r.X, r.Bottom + Theme.S(2), r.Width, 1)); g.DrawLine(lp, l.X, l.Y, l.Right, l.Y); }
            }

            Head(g, addons, Ico.Download, "ao_title");
            if (aoList.Count == 0)
                Label(g, aoLoading ? L.T("ao_checking") : L.T("ao_none"), Theme.F(8.75f), Theme.InkSoft, new Rectangle(addons.X + Theme.S(20), addons.Y + Theme.S(50), addons.Width - Theme.S(40), Theme.S(56)), Align.Start, false, true);
            for (int i = 0; i < aoList.Count && i < aoRows.Count; i++)
            {
                AddOn a = aoList[i];
                Rectangle r = aoRows[i];
                Rectangle dot = new Rectangle(r.X, r.Y + (r.Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
                Theme.FillRound(g, M(dot), Theme.S(9), Theme.CardInner);
                Theme.Glyph(g, a.Glyph, 9.5f, Theme.InkSoft, M(dot));
                int right = aoOpen[i].Left;
                int tw = Math.Max(Theme.S(40), (Theme.RTL ? r.Right - right : right - r.X) - Theme.S(52));
                string title = L.T(a.NameKey) + (a.Size > 0 ? "  ·  " + Theme.HumanSize(a.Size) : "") + (a.Required ? "  ·  " + L.T("ao_required") : "");
                Label(g, title, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(42), r.Y + Theme.S(9), tw, Theme.S(22)));
                Label(g, L.T("re_node_where", Theme.RTL ? "\u202A" + aoPaths[i] + "\u202C" : aoPaths[i]), Theme.F(8.25f), Theme.InkSoft, new Rectangle(r.X + Theme.S(42), r.Y + Theme.S(31), tw, Theme.S(20)), Align.Start, true, false);
                if (i < aoList.Count - 1) using (Pen lp = new Pen(Theme.Line)) { Rectangle l = M(new Rectangle(r.X, r.Bottom + Theme.S(2), r.Width, 1)); g.DrawLine(lp, l.X, l.Y, l.Right, l.Y); }
            }

            Head(g, integration, Ico.Settings, "integration");
            for (int h = 0; h < 3; h++)
                Label(g, L.T(IntegGroups[h]).ToUpper(System.Globalization.CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, integHeads[h], Align.Start);
            using (Pen lp = new Pen(Theme.Line)) { Rectangle l = M(new Rectangle(zipRow.X, zipRow.Y - Theme.S(4), zipRow.Width, 1)); g.DrawLine(lp, l.X, l.Y, l.Right, l.Y); }
            Label(g, zipStatus ?? L.T(Paths.Portable ? "zip_label_portable" : "zip_label"), Theme.F(9.5f), zipStatus != null ? Theme.InkSoft : Theme.Ink, new Rectangle(zipRow.X, zipRow.Y, zipRow.Width - Theme.S(150), zipRow.Height), Align.Start, true, true);
            string[] ik = { "int_desktop", "int_menu", "int_viewer", "int_notify", "int_tray", "int_links", "int_close_tray", "int_wheel", "int_startup", "int_notify_long", "int_notify_sound" };
            for (int i = 0; i < 11; i++)
            {
                bool subRow = i == 9 || i == 10;
                Label(g, L.T(ik[i]), Theme.F(subRow ? 9f : 9.5f), subRow ? (integ[3].Checked ? Theme.InkSoft : Theme.InkFaint) : Theme.Ink, new Rectangle(integRows[i].X, integRows[i].Y, integRows[i].Width - Theme.S(64), integRows[i].Height), Align.Start, true, true);
            }
        }

        /// Lets you choose any picture as the app logo (kept as a 256 px PNG next to the settings).
        bool PickCustomLogo()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("logo_pick");
                d.Filter = L.T("images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.webp|" + L.T("all_files") + "|*.*";
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return false;
                try
                {
                    using (Image src = ViewerPage.Decode(d.FileName, 1024))
                    using (Bitmap b = new Bitmap(256, 256, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(b))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            float k = Math.Min(256f / src.Width, 256f / src.Height);
                            float w = src.Width * k, h = src.Height * k;
                            g.DrawImage(src, (256 - w) / 2, (256 - h) / 2, w, h);
                        }
                        System.IO.Directory.CreateDirectory(Paths.AppDir);
                        b.Save(Theme.CustomLogoPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    return true;
                }
                catch (Exception ex) { ShowToast(ex.Message, false, null, null); return false; }
            }
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Wheel(e.Delta); }

        void Wheel(int delta)
        {
            if (maxScroll <= 0) return;
            scrollTo = Math.Max(0, Math.Min(maxScroll, scrollTo - delta / 120f * Theme.S(90)));
            Animator.Start(this);
        }

        /// Scrolls straight to a spot (0 = top), without the glide.
        public void ScrollToNow(int y)
        {
            scrollTo = scrollNow = Math.Max(0, Math.Min(maxScroll, y));
            body.Top = -(int)Math.Round(scrollNow); thumb.Invalidate();
        }

        public override bool Step()
        {
            bool more = base.Step();
            if (Math.Abs(scrollTo - scrollNow) > 0.01f)
            {
                bool moving = Fx.Ease(ref scrollNow, scrollTo, 0.3f);
                int n = -(int)Math.Round(scrollNow);
                if (n != body.Top) { body.Top = n; thumb.Invalidate(); thumb.Update(); }
                more |= moving;
            }
            return more;
        }

        protected override void OnPaint(PaintEventArgs e) { PaintBg(e.Graphics); }

        // ---- the scrolling body and its scroll bar
        readonly Body body;
        readonly Thumb thumb;

        void PaintBody(Graphics g, Control on)
        {
            Theme.PaintSurface(g, on, Surface.Background);
            Theme.Smooth(g);
            int tx = Pad;
            Label(g, L.T(TitleKey), Theme.Display(20f), Theme.Ink, new Rectangle(tx, Theme.S(18), Width - Pad - tx - HeaderReserve, Theme.S(40)), Align.Start, false);
            Label(g, L.T(SubKey), Theme.F(9.75f), Theme.InkSoft, new Rectangle(tx, Theme.S(58), Width - Pad - tx - HeaderReserve, Theme.S(24)), Align.Start, false);
            foreach (Rectangle c in cards) Theme.PaintCard(g, M(c));
            PaintContent(g);
        }

        class Body : Control
        {
            readonly SettingsPage page;
            public Body(SettingsPage p)
            {
                page = p;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaint(PaintEventArgs e) { page.PaintBody(e.Graphics, this); }
            protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); page.Wheel(e.Delta); }
        }

        class Thumb : Control
        {
            readonly SettingsPage page;
            public Thumb(SettingsPage p)
            {
                page = p;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Bg);
                if (page.maxScroll <= 0) return;
                Theme.Smooth(g);
                int H = page.Height;
                int th = Math.Max(Theme.S(40), H * H / (H + page.maxScroll));
                int ty = (int)((H - th) * (page.scrollNow / page.maxScroll));
                Theme.FillRound(g, new Rectangle((Width - Theme.S(4)) / 2, ty, Theme.S(4), th), Theme.S(2), Color.FromArgb(90, Theme.InkFaint));
            }
            protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); page.Wheel(e.Delta); }
        }

        void InstallYt()
        {
            ytBusy = true; ytStatus = L.T("starting"); Refresh2();
            Thread t = new Thread(() =>
            {
                string err = null;
                try { YtDlp.Install((label, f) => Ui(() => { ytStatus = label; ytProgress.Value = f; body.Invalidate(); }), () => IsDisposed); }
                catch (Exception ex) { err = ex.Message; }
                Ui(() => { ytBusy = false; Refresh2(); ShowToast(err == null ? L.T("yt_ready") : L.T("yt_setup_failed", err), err == null, null, null); });
            });
            t.IsBackground = true; t.Start();
        }

        void UpdateYt()
        {
            ytBusy = true; ytStatus = L.T("checking"); ytProgress.SetIndeterminate(true); Refresh2();
            Thread t = new Thread(() =>
            {
                string r = YtDlp.Update();
                Ui(() => { ytBusy = false; ytProgress.SetIndeterminate(false); Refresh2(); ShowToast(r, true, null, null); });
            });
            t.IsBackground = true; t.Start();
        }
    }
}
