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
        Rectangle appearance, saving, about, engines, integration;
        Rectangle themeLbl, langLbl, openAfterLbl, askSaveLbl, aboutR;
        readonly Toggle askSave = new Toggle();
        Rectangle[] engineRows = new Rectangle[4], integRows = new Rectangle[4];
        Segments theme, lang, logoSeg;
        Swatches accent;
        Rectangle accentLbl, logoLbl;
        static readonly Color[] AccentColors = {
            Color.FromArgb(201, 100, 66), Color.FromArgb(108, 112, 120), Color.FromArgb(59, 130, 246), Color.FromArgb(124, 58, 237),
            Color.FromArgb(219, 39, 119), Color.FromArgb(220, 38, 38), Color.FromArgb(22, 163, 74), Color.FromArgb(13, 148, 136) };
        FolderButton folder;
        Btn btnReset, btnUninstall, btnLo, btnYt, btnYtUpdate;
        Toggle openAfter;
        Toggle[] integ = new Toggle[4];
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
            logoSeg = new Segments(() => L.T("logo_classic"), () => L.T("logo_silver"), () => L.T("logo_night"));
            string lc = Settings.Get("Logo", "");
            logoSeg.SetSilently(lc == "silver" ? 1 : lc == "dark" ? 2 : 0);
            logoSeg.SelectedChanged += (s, e) =>
            {
                Settings.Set("Logo", new[] { "", "silver", "dark" }[logoSeg.SelectedIndex]);
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
            integ[3].CheckedChanged += (s, e) => Notifier.Enabled = integ[3].Checked;
            btnZip = new Btn(() => L.T("zip_make"), "\uE8B7", BtnKind.Secondary);
            btnZip.Click += (s, e) => MakeZip();
            Controls.AddRange(new Control[] { btnUpdate, btnTips, askSave, btnZip });
            Controls.AddRange(new Control[] { theme, lang, accent, logoSeg, folder, btnReset, openAfter, btnUninstall, btnLo, btnYt, btnYtUpdate, ytProgress });
            Controls.AddRange(integ);
            LoadValues();
        }

        Btn btnUpdate, btnTips, btnZip;
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
                        try { BeginInvoke((Action)(() => { zipStatus = L.T("zip_working", (int)(f * 100)); Invalidate(); })); } catch { }
                    }, () => IsDisposed);
                }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        zipping = false; btnZip.Enabled = true; btnZip.Invalidate(); zipStatus = null; Invalidate();
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
            integ[0].SetSilently(Settings.Get("IntDesktop", "1") == "1");
            integ[1].SetSilently(Settings.Get("IntMenu", "1") == "1");
            integ[2].SetSilently(Settings.Get("IntViewer", "1") == "1");
            integ[3].SetSilently(Notifier.Enabled);
        }

        public override void OnShown() { Tools.Reset(); LoadValues(); Refresh2(); }
        public override void Restyle() { LoadValues(); Refresh2(); }

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
            int colW = (W - p * 2 - gap) / 2;
            int lx = p, rx = p + colW + gap, y = Top0;
            appearance = new Rectangle(lx, y, colW, Theme.S(252));
            saving = new Rectangle(lx, appearance.Bottom + gap, colW, Theme.S(236));
            about = new Rectangle(lx, saving.Bottom + gap, colW, Math.Max(Theme.S(96), Math.Min(Theme.S(120), H - saving.Bottom - gap - p)));
            engines = new Rectangle(rx, y, colW, Theme.S(296));
            integration = new Rectangle(rx, engines.Bottom + gap, colW, Theme.S(322));
            cards.AddRange(new[] { appearance, saving, about, engines, integration });

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

            Place(folder, new Rectangle(x, saving.Y + Theme.S(52), w, Theme.S(58)));
            int rw = Math.Max(Theme.S(150), btnReset.PreferredWidth);
            Place(btnReset, new Rectangle(x, saving.Y + Theme.S(114), rw, Theme.S(30)));
            askSaveLbl = new Rectangle(x, saving.Y + Theme.S(150), w - Theme.S(60), Theme.S(34));
            Place(askSave, new Rectangle(x + w - Theme.S(52), saving.Y + Theme.S(150), Theme.S(52), Theme.S(34)));
            openAfterLbl = new Rectangle(x, saving.Y + Theme.S(190), w - Theme.S(60), Theme.S(34));
            Place(openAfter, new Rectangle(x + w - Theme.S(52), saving.Y + Theme.S(190), Theme.S(52), Theme.S(34)));

            aboutR = new Rectangle(about.X + ip, about.Y, colW - ip * 2, about.Height);
            int uw = Math.Max(Theme.S(150), Math.Max(btnUninstall.PreferredWidth, btnUpdate.PreferredWidth));
            int bt = about.Y + (about.Height - Theme.S(76)) / 2;
            Place(btnUpdate, new Rectangle(about.Right - ip - uw, bt, uw, Theme.S(34)));
            Place(btnUninstall, new Rectangle(about.Right - ip - uw, bt + Theme.S(42), uw, Theme.S(34)));
            int tw = Math.Max(Theme.S(120), btnTips.PreferredWidth);
            Place(btnTips, new Rectangle(aboutR.X + Theme.S(50), about.Y + about.Height / 2 + Theme.S(16), tw, Theme.S(28)));

            int ex = engines.X + ip, ew = colW - ip * 2;
            for (int i = 0; i < 4; i++) engineRows[i] = new Rectangle(ex, engines.Y + Theme.S(50) + i * Theme.S(58), ew, Theme.S(54));
            int bw = Theme.S(120);
            Place(btnLo, new Rectangle(ex + ew - bw, engineRows[2].Y + Theme.S(10), bw, Theme.S(34)));
            Place(btnYt, new Rectangle(ex + ew - bw, engineRows[3].Y + Theme.S(10), bw, Theme.S(34)));
            int uw2 = Math.Max(Theme.S(120), btnYtUpdate.PreferredWidth);
            Place(btnYtUpdate, new Rectangle(ex + ew - uw2, engineRows[3].Y + Theme.S(10), uw2, Theme.S(34)));
            Place(ytProgress, new Rectangle(ex + ew - bw, engineRows[3].Y + Theme.S(24), bw, Theme.S(5)));

            for (int i = 0; i < 4; i++)
            {
                integRows[i] = new Rectangle(ex, integration.Y + Theme.S(50) + i * Theme.S(52), ew, Theme.S(48));
                Place(integ[i], new Rectangle(ex + ew - Theme.S(52), integRows[i].Y + Theme.S(7), Theme.S(52), Theme.S(34)));
            }
            zipRow = new Rectangle(ex, integration.Y + Theme.S(50) + 4 * Theme.S(52) + Theme.S(6), ew, Theme.S(48));
            int zw = Math.Max(Theme.S(130), btnZip.PreferredWidth);
            Place(btnZip, new Rectangle(ex + ew - zw, zipRow.Y + Theme.S(7), zw, Theme.S(34)));
            Invalidate();
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
            Head(g, saving, Ico.Save, "saving");
            Label(g, L.T("open_after"), Theme.F(9.5f), Theme.Ink, openAfterLbl);
            Label(g, L.T("ask_save"), Theme.F(9.5f), Theme.Ink, askSaveLbl);
            // about
            Rectangle logo = new Rectangle(aboutR.X, about.Y + (about.Height - Theme.S(44)) / 2 - Theme.S(14), Theme.S(44), Theme.S(44));
            if (Theme.Logo != null) g.DrawImage(Theme.Logo, M(logo));
            Label(g, L.T("app_name"), Theme.F(11f, FontStyle.Bold), Theme.Ink, new Rectangle(aboutR.X + Theme.S(56), logo.Y, aboutR.Width - Theme.S(200), Theme.S(24)));
            Label(g, L.T("version", Shell.Version), Theme.F(9f), Theme.InkSoft, new Rectangle(aboutR.X + Theme.S(56), logo.Y + Theme.S(22), aboutR.Width - Theme.S(200), Theme.S(22)));

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

            Head(g, integration, Ico.Settings, "integration");
            using (Pen lp = new Pen(Theme.Line)) { Rectangle l = M(new Rectangle(zipRow.X, zipRow.Y - Theme.S(4), zipRow.Width, 1)); g.DrawLine(lp, l.X, l.Y, l.Right, l.Y); }
            Label(g, zipStatus ?? L.T(Paths.Portable ? "zip_label_portable" : "zip_label"), Theme.F(9.5f), zipStatus != null ? Theme.InkSoft : Theme.Ink, new Rectangle(zipRow.X, zipRow.Y, zipRow.Width - Theme.S(150), zipRow.Height), Align.Start, true, true);
            string[] ik = { "int_desktop", "int_menu", "int_viewer", "int_notify" };
            for (int i = 0; i < 4; i++)
                Label(g, L.T(ik[i]), Theme.F(9.5f), Theme.Ink, new Rectangle(integRows[i].X, integRows[i].Y, integRows[i].Width - Theme.S(64), integRows[i].Height), Align.Start, true, true);
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

        void InstallYt()
        {
            ytBusy = true; ytStatus = L.T("starting"); Refresh2();
            Thread t = new Thread(() =>
            {
                string err = null;
                try { YtDlp.Install((label, f) => Ui(() => { ytStatus = label; ytProgress.Value = f; Invalidate(); }), () => IsDisposed); }
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
