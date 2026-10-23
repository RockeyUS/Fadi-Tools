using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    public class CheckRow : UiControl
    {
        public bool Checked = true;
        public Func<string> Title, Sub;
        public CheckRow(Func<string> title, Func<string> sub) { Title = title; Sub = sub; Cursor = Cursors.Hand; }
        protected override void OnClick(EventArgs e) { if (Enabled) { Checked = !Checked; Invalidate(); } base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            int s = Theme.S(22);
            Rectangle box = Theme.Mirror(new Rectangle(1, Theme.S(3), s, s), Width);
            if (Checked)
            {
                Theme.FillRoundGradient(g, box, Theme.S(6), Enabled ? Theme.PrimaryTop : Theme.InkFaint, Enabled ? Theme.PrimaryBottom : Theme.InkFaint);
                Theme.Glyph(g, Ico.Check, 8.5f, Theme.PrimaryInk, box);
            }
            else
            {
                Theme.FillRound(g, box, Theme.S(6), hot > 0 ? Theme.Hover : Theme.Field);
                Theme.StrokeRound(g, box, Theme.S(6), Theme.FieldBorder, 1.2f);
            }
            int tx = s + Theme.S(14);
            Theme.Text(g, Title(), Theme.F(10f, FontStyle.Bold), Enabled ? Theme.Ink : Theme.InkFaint, Theme.Mirror(new Rectangle(tx, 0, Width - tx, Theme.S(28)), Width));
            Theme.Text(g, Sub(), Theme.F(8.75f), Theme.InkSoft, Theme.Mirror(new Rectangle(tx, Theme.S(26), Width - tx, Theme.S(20)), Width));
        }
    }

    /// Paints the themed gradient behind a whole window.
    public class SetupCanvas : UiControl
    {
        public Rectangle Card;
        public Action<Graphics> Extra;
        public SetupCanvas() { Surface = Surface.Background; }
        protected override void OnPaint(PaintEventArgs e)
        {
            PaintBg(e.Graphics);
            Theme.PaintCard(e.Graphics, Theme.Mirror(Card, Width));
            if (Extra != null) Extra(e.Graphics);
        }
    }

    public class SetupForm : Form
    {
        SetupCanvas canvas;
        Rectangle card, logoR, titleR, subR, stepR, noteR;
        CheckRow cDesktop, cMenu, cViewer;
        Btn bInstall, bCancel;
        Segments lang;
        Progress bar;
        string step = "";
        Color stepColor;
        bool working, cancelled;
        public bool Completed;
        bool needEngines, portable;

        public SetupForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.F(9.75f);
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Shell.FitToScreen(this, new Size(Theme.S(620), Theme.S(560)), new Size(Theme.S(620), Theme.S(560)));
            needEngines = !Tools.EnginesReady;

            canvas = new SetupCanvas { Dock = DockStyle.Fill };
            canvas.Extra = Paint2;
            portable = Paths.Portable;
            cDesktop = new CheckRow(() => L.T("setup_desktop"), () => L.T("setup_desktop_sub"));
            cMenu = new CheckRow(() => L.T("setup_menu"), () => L.T("setup_menu_sub"));
            cViewer = new CheckRow(() => L.T("setup_viewer"), () => L.T("setup_viewer_sub"));
            bInstall = new Btn(() => Completed ? L.T("setup_open") : working ? L.T(portable ? "setup_getting" : "installing") : step.Length > 0 && stepColor == Theme.Error ? L.T("try_again") : portable ? L.T("setup_get_engines") : (needEngines ? L.T("setup_install_dl") : L.T("install")), Ico.Download, BtnKind.Primary);
            bInstall.Click += (s, e) => { if (Completed) { DialogResult = DialogResult.OK; Close(); } else Begin(); };
            bCancel = new Btn(() => L.T("cancel"), null, BtnKind.Ghost);
            bCancel.Click += (s, e) => { if (working) cancelled = true; else Close(); };
            lang = new Segments(() => "English", () => "العربية");
            lang.SetSilently(Theme.RTL ? 1 : 0);
            lang.SelectedChanged += (s, e) => { Settings.Set("Language", lang.SelectedIndex == 1 ? "ar" : "en"); Theme.ApplySettings(); Relayout(); };
            bar = new Progress { Visible = false };
            canvas.Controls.AddRange(new Control[] { cDesktop, cMenu, cViewer, bInstall, bCancel, lang, bar });
            Controls.Add(canvas);
            HandleCreated += (s, e) => Theme.ApplyTitleBar(this);
            Relayout();
        }

        void Relayout()
        {
            Text = L.T(Paths.Portable ? "setup_title_portable" : "setup_title");
            int m = Theme.S(22);
            card = new Rectangle(m, m, ClientSize.Width - m * 2, ClientSize.Height - m * 2);
            canvas.Card = card;
            int p = Theme.S(30), x = card.X + p, w = card.Width - p * 2;
            Func<Rectangle, Rectangle> M = r => Theme.Mirror(r, ClientSize.Width);
            logoR = new Rectangle(x, card.Y + Theme.S(30), Theme.S(64), Theme.S(64));
            titleR = new Rectangle(logoR.Right + Theme.S(18), logoR.Y + Theme.S(4), w - Theme.S(82), Theme.S(32));
            subR = new Rectangle(logoR.Right + Theme.S(19), logoR.Y + Theme.S(36), w - Theme.S(82), Theme.S(22));
            lang.Bounds = M(new Rectangle(card.Right - p - Theme.S(170), card.Y + Theme.S(14), Theme.S(170), Theme.S(30)));
            noteR = new Rectangle(x, logoR.Bottom + Theme.S(18), w, Theme.S(46));
            int y = noteR.Bottom + Theme.S(12);
            cDesktop.Visible = cMenu.Visible = cViewer.Visible = !portable;
            if (!portable)
            {
                cDesktop.Bounds = M(new Rectangle(x, y, w, Theme.S(48))); y += Theme.S(54);
                cMenu.Bounds = M(new Rectangle(x, y, w, Theme.S(48))); y += Theme.S(54);
                cViewer.Bounds = M(new Rectangle(x, y, w, Theme.S(48))); y += Theme.S(58);
            }
            else y += Theme.S(20);
            bar.Bounds = M(new Rectangle(x, y, w, Theme.S(6)));
            stepR = new Rectangle(x, y + Theme.S(12), w, Theme.S(42));
            int bw = Math.Max(Theme.S(220), bInstall.PreferredWidth);
            bInstall.Bounds = M(new Rectangle(card.Right - p - bw, card.Bottom - p - Theme.S(48), bw, Theme.S(48)));
            bCancel.Bounds = M(new Rectangle(card.Right - p - bw - Theme.S(120), card.Bottom - p - Theme.S(42), Theme.S(110), Theme.S(36)));
            foreach (Control c in canvas.Controls) c.Invalidate();
            canvas.Invalidate();
        }

        void Paint2(Graphics g)
        {
            Func<Rectangle, Rectangle> M = r => Theme.Mirror(r, ClientSize.Width);
            Theme.Smooth(g);
            Theme.DrawLogo(g, M(logoR));
            Theme.Text(g, L.T("setup_welcome"), Theme.F(16f, FontStyle.Bold), Theme.Ink, M(titleR), Align.Start, false);
            Theme.Text(g, L.T("setup_tag"), Theme.F(9.75f), Theme.InkSoft, M(subR), Align.Start, false);
            Theme.Text(g, portable ? L.T("setup_note_portable") : needEngines ? L.T("setup_note_dl") : L.T("setup_note_ready"), Theme.F(9.5f), Theme.InkSoft, M(noteR), Align.Start, false, true);
            Theme.Text(g, step, Theme.F(9.25f), stepColor, M(stepR), Align.Start, false, true);
        }

        void Ui(Action a) { try { if (!IsDisposed) BeginInvoke(a); } catch { } }
        void Step(string text, double? progress)
        {
            Ui(() => { step = text; stepColor = Theme.InkSoft; if (progress.HasValue) bar.Value = progress.Value; canvas.Invalidate(); });
        }

        void Begin()
        {
            working = true; cancelled = false;
            cDesktop.Enabled = cMenu.Enabled = cViewer.Enabled = lang.Enabled = false;
            bInstall.Enabled = false; bInstall.Invalidate();
            bar.Visible = true; bar.Value = 0;
            bool desk = cDesktop.Checked, menu = cMenu.Checked, view = cViewer.Checked;
            Settings.Set("IntDesktop", desk ? "1" : "0"); Settings.Set("IntMenu", menu ? "1" : "0"); Settings.Set("IntViewer", view ? "1" : "0");
            Thread t = new Thread(() =>
            {
                string error = null;
                try { DoInstall(desk, menu, view); }
                catch (OperationCanceledException) { error = L.T("setup_cancelled"); }
                catch (Exception ex) { error = ex.Message; }
                Ui(() => Done(error));
            });
            t.IsBackground = true;
            t.Start();
        }

        void Done(string error)
        {
            working = false;
            if (error == null)
            {
                Completed = true; bar.Value = 1;
                step = L.T("setup_done"); stepColor = Theme.Success;
                bCancel.Visible = false;
            }
            else
            {
                step = L.T("setup_failed", error); stepColor = Theme.Error;
                cDesktop.Enabled = cMenu.Enabled = cViewer.Enabled = lang.Enabled = true;
            }
            bInstall.Enabled = true;
            Relayout();
        }

        string temp;

        void DoInstall(bool desktop, bool menu, bool viewer)
        {
            temp = Path.Combine(Path.GetTempPath(), "FadiToolsSetup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                Step(L.T("preparing"), 0.01);
                if (!portable) Shell.CopySelf();
                Directory.CreateDirectory(Paths.ToolsDir);
                bool ff = Tools.FFmpeg == null, im = Tools.Magick == null;
                if (ff || im || !YtDlp.Ready || !Qpdf.Ready) GetEngines(ff, im);
                CheckMagickRuns();
                if (!portable)
                {
                    Step(L.T("adding_shortcuts"), 0.95);
                    Shell.Integrate(desktop, menu, viewer);
                }
                Shell.SetBrowserEmulation();
                Tools.Reset();
                if (!Tools.EnginesReady) throw new Exception(L.T("err_engines_missing"));
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }

        /// Tries each address in turn (the fastest first).
        void Fetch(string label, string[] urls, string dest, Action<long, long> progress)
        {
            Exception last = null;
            foreach (string url in urls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                if (cancelled) throw new OperationCanceledException();
                try { Net.Download(url, dest, progress, () => cancelled); return; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; }
            }
            throw new Exception(L.T("dl_failed", label) + (last != null ? " (" + last.Message + ")" : ""));
        }

        /// FFmpeg and ImageMagick come down at the same time, as small .7z archives from GitHub's fast servers,
        /// and only the 3 programs Fadi Tools uses are unpacked (not 10 copies of them).
        void GetEngines(bool ff, bool im)
        {
            string sevenZ = Path.Combine(temp, "7zr.exe");
            Step(L.T("preparing"), 0.02);
            Fetch(L.T("eng_unpacker"), new[] { "https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe", "https://www.7-zip.org/a/7zr.exe" }, sevenZ, null);

            // everything at once: video/audio, pictures, the download engine (yt-dlp) and the PDF engine (qpdf)
            bool yt = !YtDlp.Ready, pdf = !Qpdf.Ready;
            long[] got = new long[4], size = { ff ? 36L << 20 : 0, im ? 12L << 20 : 0, yt ? 60L << 20 : 0, pdf ? 27L << 20 : 0 };   // guesses until the servers answer
            Exception[] errors = new Exception[4];
            int[] unpacking = new int[1];
            Thread a = null, b = null;
            if (ff) a = Run(() =>
            {
                string dir = Path.Combine(Paths.ToolsDir, "ffmpeg");
                Directory.CreateDirectory(dir);
                string arc = Path.Combine(temp, "ffmpeg.7z");
                Action<long, long> prog = (g, t) => { got[0] = g; if (t > 0) size[0] = t; };
                bool sevenZip = true;
                try
                {
                    Fetch(L.T("eng_av"), new[] { Net.GitHubAsset("GyanD/codexffmpeg", @"essentials_build\.7z$"),
                        "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.7z" }, arc, prog);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // the bigger .zip as a last resort
                    sevenZip = false; arc = Path.Combine(temp, "ffmpeg.zip");
                    Fetch(L.T("eng_av"), new[] { Net.GitHubAsset("GyanD/codexffmpeg", @"essentials_build\.zip$"),
                        "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip",
                        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip" }, arc, prog);
                }
                Interlocked.Increment(ref unpacking[0]);
                if (sevenZip) Unpack(sevenZ, "e " + Tools.Quote(arc) + " -o" + Tools.Quote(dir) + " ffmpeg.exe ffprobe.exe -r -y");
                else
                    using (ZipArchive za = ZipFile.OpenRead(arc))
                        foreach (ZipArchiveEntry e in za.Entries)
                            if (e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                                e.ExtractToFile(Path.Combine(dir, e.Name), true);
                try { File.Delete(arc); } catch { }
            }, errors, 0);
            if (im) b = Run(() =>
            {
                string dir = Path.Combine(Paths.ToolsDir, "imagemagick");
                Directory.CreateDirectory(dir);
                string arc = Path.Combine(temp, "imagemagick.7z");
                Fetch(L.T("eng_img"), new[] { Net.GitHubAsset("ImageMagick/ImageMagick", @"portable-Q16-HDRI-x64\.7z$"),
                    "https://github.com/ImageMagick/ImageMagick/releases/download/7.1.2-33/ImageMagick-7.1.2-33-portable-Q16-HDRI-x64.7z" }, arc,
                    (g, t) => { got[1] = g; if (t > 0) size[1] = t; });
                Interlocked.Increment(ref unpacking[0]);
                // magick.exe and its settings files only
                Unpack(sevenZ, "x " + Tools.Quote(arc) + " -o" + Tools.Quote(dir) + " magick.exe *.xml *.icc *.txt -y");
                try { File.Delete(arc); } catch { }
            }, errors, 1);
            // these two are welcome but not essential: if one fails, the app gets it later by itself
            Exception[] extra = new Exception[2];
            Thread c = yt ? Run(() => YtDlp.Install((label, f) => got[2] = (long)(Math.Max(0, f) * size[2]), () => cancelled), extra, 0) : null;
            Thread d = pdf ? Run(() => Qpdf.Install((g, t) => { got[3] = g; if (t > 0) size[3] = t; }, () => cancelled), extra, 1) : null;

            // one progress bar for all of them, with the speed
            DateTime t0 = DateTime.Now, lastT = t0; long lastSum = 0; double speed = 0;
            while ((a != null && a.IsAlive) || (b != null && b.IsAlive) || (c != null && c.IsAlive) || (d != null && d.IsAlive))
            {
                Thread.Sleep(200);
                long sum = got[0] + got[1] + got[2] + got[3], total = Math.Max(1, size[0] + size[1] + size[2] + size[3]);
                double dt = (DateTime.Now - lastT).TotalSeconds;
                if (dt >= 0.5) { double now = (sum - lastSum) / dt; speed = speed <= 0 ? now : speed * 0.6 + now * 0.4; lastSum = sum; lastT = DateTime.Now; }
                double frac = Math.Min(1, sum / (double)total);
                if (unpacking[0] >= (ff ? 1 : 0) + (im ? 1 : 0) || frac >= 0.999) Step(L.T("unpacking", L.T("eng_both")), 0.9);
                else Step(L.T("dl_engines", Theme.HumanSize(sum), Theme.HumanSize(total)) + (speed > 1024 ? "  ·  " + Theme.HumanSize((long)speed) + "/s" : ""), 0.03 + 0.85 * frac);
            }
            if (cancelled) throw new OperationCanceledException();
            foreach (Exception e in errors) if (e != null) throw e;
            Tools.Reset();
            if (Tools.FFmpeg == null || Tools.Magick == null) throw new Exception(L.T("err_engines_missing"));
        }

        static Thread Run(Action work, Exception[] errors, int slot)
        {
            Thread t = new Thread(() => { try { work(); } catch (Exception ex) { errors[slot] = ex; } }) { IsBackground = true };
            t.Start();
            return t;
        }

        static void Unpack(string sevenZ, string args)
        {
            string output;
            int code = Tools.RunHidden(sevenZ, args, 10 * 60 * 1000, out output);
            if (code != 0 && code != 1) throw new Exception(L.T("err_engines_missing") + " (" + Tools.LastLines(output, 1) + ")");
        }

        void CheckMagickRuns()
        {
            string magick = Tools.Magick;
            if (magick == null) return;
            string output;
            if (Tools.RunHidden(magick, "-version", 30000, out output) == 0) return;
            Step(L.T("installing_vc"), 0.91);
            string vc = Path.Combine(temp, "vc_redist.x64.exe");
            Fetch(L.T("eng_vc"), new[] { "https://aka.ms/vs/17/release/vc_redist.x64.exe" }, vc, null);
            ProcessStartInfo psi = new ProcessStartInfo(vc, "/install /passive /norestart") { UseShellExecute = true };
            using (Process p = Process.Start(psi)) p.WaitForExit();
            if (Tools.RunHidden(magick, "-version", 30000, out output) != 0) throw new Exception(L.T("err_engines_missing") + " (" + Tools.LastLines(output, 1) + ")");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (working) { cancelled = true; e.Cancel = true; step = L.T("cancelling"); canvas.Invalidate(); return; }
            if (Completed && DialogResult != DialogResult.OK) DialogResult = DialogResult.OK;
            base.OnFormClosing(e);
        }
    }
}
