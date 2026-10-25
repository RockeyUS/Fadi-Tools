using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// whisper.cpp (free, open source, runs on the PC): listens to a video's sound and writes subtitles.
    /// The program (~9 MB) and a speech model (60 or 190 MB) are downloaded the first time.
    public static class Whisper
    {
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static string Dir { get { return Path.Combine(Paths.ToolsDir, "whisper"); } }
        public static string ModelsDir { get { return Path.Combine(Dir, "models"); } }
        // Fast = "small" (it can also translate into English); Accurate = "large-v3-turbo", far better with Arabic
        public static readonly string[] Models = { "ggml-small-q5_1.bin", "ggml-large-v3-turbo-q5_0.bin" };
        public static readonly int[] ModelMb = { 190, 574 };
        const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
        const string FallbackZip = "https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip";

        public static string Exe
        {
            get
            {
                string name = IsWindows ? "whisper-cli.exe" : "whisper-cli";
                try
                {
                    string p = Path.Combine(Dir, name);
                    if (File.Exists(p)) return p;
                    if (Directory.Exists(Dir)) foreach (string f in Directory.GetFiles(Dir, name, SearchOption.AllDirectories)) return f;
                }
                catch { }
                return null;
            }
        }
        public static string ModelPath(int q) { return Path.Combine(ModelsDir, Models[Math.Max(0, Math.Min(1, q))]); }
        public static bool HasModel(int q) { try { FileInfo f = new FileInfo(ModelPath(q)); return f.Exists && f.Length > 10 << 20; } catch { return false; } }
        public static bool Ready(int q) { return Exe != null && HasModel(q); }

        /// The newest release that has the Windows program (the latest one is often source code only).
        static string ProgramUrl()
        {
            try
            {
                string json = Net.GetString("https://api.github.com/repos/ggml-org/whisper.cpp/releases?per_page=15");
                foreach (Match m in Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/whisper-bin-x64\\.zip)\""))
                    return m.Groups[1].Value;
            }
            catch { }
            return FallbackZip;
        }

        /// Downloads whatever is missing. progress(what, done, total).
        public static void Install(int q, Action<string, long, long> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(Dir);
            if (Exe == null)
            {
                string zip = Path.Combine(Path.GetTempPath(), "fadi_whisper_" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    Net.Download(ProgramUrl(), zip, (a, b) => progress("program", a, b), cancelled);
                    using (ZipArchive za = ZipFile.OpenRead(zip))
                        foreach (ZipArchiveEntry e in za.Entries)
                        {
                            if (e.Name.Length == 0) continue;
                            string n = e.Name.ToLowerInvariant();
                            // only what whisper-cli needs: itself and the DLLs
                            if (!(n == "whisper-cli.exe" || n.EndsWith(".dll"))) continue;
                            e.ExtractToFile(Path.Combine(Dir, e.Name), true);
                        }
                }
                finally { try { File.Delete(zip); } catch { } }
                if (Exe == null) throw new UserError(L.T("cap_dl_bad"));
            }
            if (!HasModel(q))
            {
                Directory.CreateDirectory(ModelsDir);
                string part = ModelPath(q) + ".part";
                Net.Download(ModelUrl + Models[q], part, (a, b) => progress("model", a, b), cancelled);
                if (File.Exists(ModelPath(q))) File.Delete(ModelPath(q));
                File.Move(part, ModelPath(q));
            }
        }

        /// Writes subtitles for a 16 kHz WAV. lang = "auto", "ar", "en"... Returns the .srt path.
        public static string Transcribe(string wav, int q, string lang, Action<double> progress, Func<bool> cancelled) { return Transcribe(wav, q, lang, false, progress, cancelled); }

        /// english: subtitles in English whatever language is spoken (Whisper's translation goes into English only).
        /// The .srt it returns has one word per entry (with when it's said); Captions.FromWords groups them into lines.
        public static string Transcribe(string wav, int q, string lang, bool english, Action<double> progress, Func<bool> cancelled)
        {
            string exe = Exe;
            if (exe == null || !HasModel(q)) throw new UserError(L.T("cap_not_ready"));
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            // -ml 1 -sow: one word at a time, so each word gets its own time (for word highlight).
            // -mc 0: no carrying earlier text into the next 30 seconds — that's what made it stop after a few words.
            // -nth: only skip a stretch as "no speech" when Whisper is quite sure (Arabic over music was being skipped).
            string common = "-m " + Tools.Quote(ModelPath(q)) + " -f " + Tools.Quote(wav) + " -l " + lang + " -t " + threads + " -osrt -pp -np" + (english && lang != "en" ? " -tr" : "");
            string[] tries = { " -ml 1 -sow -mc 0 -nth 0.8 -bs 5 -bo 5 -sns", " -ml 1 -sow -mc 0 -bs 5 -bo 5", " -ml 1 -sow" };
            string lastErr = "";
            foreach (string extra in tries)
            {
                string outBase = Path.Combine(Path.GetTempPath(), "fadi_cap_" + Guid.NewGuid().ToString("N"));
                StringBuilder err = new StringBuilder();
                Regex pr = new Regex(@"progress\s*=\s*(\d+)\s*%");
                Action<string> on = line =>
                {
                    if (line == null) return;
                    Match m = pr.Match(line);
                    if (m.Success) { if (progress != null) progress(int.Parse(m.Groups[1].Value) / 100.0); }
                    else if (err.Length < 6000) err.AppendLine(line);
                };
                int code = Tools.Stream(exe, common + extra + " -of " + Tools.Quote(outBase), on, on, cancelled);
                string srt = outBase + ".srt";
                if (code == -2) throw new OperationCanceledException();
                if (File.Exists(srt) && new FileInfo(srt).Length > 0) return srt;
                lastErr = err.ToString();
                // an older whisper-cli that doesn't know an option: try again with fewer
                if (lastErr.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) < 0 && lastErr.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) < 0) break;
            }
            throw new UserError(L.T("cap_failed") + " " + Tools.LastLines(lastErr, 2));
        }
    }

    /// "Auto captions": which language is spoken and how careful to be (Fast = small download).
    public class CaptionsForm : Form
    {
        readonly Segments lang, quality, subLang;
        readonly Btn bGo, bCancel;
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        public string Language { get { return new[] { "auto", "ar", "en" }[lang.SelectedIndex]; } }
        public int Quality { get { return quality.SelectedIndex; } }
        /// Subtitles in English (translated) instead of the spoken language.
        public bool English { get { return subLang.SelectedIndex == 1 && Language != "en"; } }
        Rectangle langLbl, subLbl, subHint, qLbl, qHint, note;

        public CaptionsForm()
        {
            Movable.Make(this, () => Theme.S(64));   // drag it by the top
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Font = Theme.F(10f);
            BackColor = Theme.Card;
            Text = L.T("cap_title");
            int W = Theme.S(500), pad = Theme.S(26), cw = W - pad * 2;
            lang = new Segments(() => L.T("cap_lang_auto"), () => "العربية", () => "English") { FontSize = 9f };
            int li; int.TryParse(Settings.Get("CapLang", "0"), out li); lang.SetSilently(Math.Max(0, Math.Min(2, li)));
            quality = new Segments(() => L.T("cap_fast") + " · " + Whisper.ModelMb[0] + " MB", () => L.T("cap_accurate") + " · " + Whisper.ModelMb[1] + " MB") { FontSize = 9f };
            int qi; int.TryParse(Settings.Get("CapQuality", Whisper.HasModel(0) && !Whisper.HasModel(1) ? "0" : "1"), out qi); quality.SetSilently(Math.Max(0, Math.Min(1, qi)));
            quality.SelectedChanged += (s, e) => Invalidate();
            subLang = new Segments(() => L.T("cap_sub_same"), () => "English") { FontSize = 9f };
            subLang.SetSilently(Settings.Get("CapSubLang", "0") == "1" ? 1 : 0);
            subLang.SelectedChanged += (s, e) => Invalidate();
            lang.SelectedChanged += (s, e) => Invalidate();
            int y = Theme.S(84);
            langLbl = new Rectangle(pad, y, cw, Theme.S(20)); y += Theme.S(24);
            lang.Bounds = Theme.Mirror(new Rectangle(pad, y, cw, Theme.S(36)), W); y += Theme.S(50);
            subLbl = new Rectangle(pad, y, cw, Theme.S(20)); y += Theme.S(24);
            subLang.Bounds = Theme.Mirror(new Rectangle(pad, y, cw, Theme.S(36)), W); y += Theme.S(40);
            subHint = new Rectangle(pad, y, cw, Theme.S(34)); y += Theme.S(44);
            qLbl = new Rectangle(pad, y, cw, Theme.S(20)); y += Theme.S(24);
            quality.Bounds = Theme.Mirror(new Rectangle(pad, y, cw, Theme.S(36)), W); y += Theme.S(42);
            qHint = new Rectangle(pad, y, cw, Theme.S(38)); y += Theme.S(44);
            note = new Rectangle(pad, y, cw, Theme.S(34)); y += Theme.S(48);
            bGo = new Btn(() => L.T("cap_go"), "", BtnKind.Primary);
            bGo.Click += (s, e) => { Settings.Set("CapLang", lang.SelectedIndex.ToString()); Settings.Set("CapQuality", quality.SelectedIndex.ToString()); Settings.Set("CapSubLang", subLang.SelectedIndex.ToString()); DialogResult = DialogResult.OK; Close(); };
            bCancel = new Btn(() => L.T("cancel"), null, BtnKind.Secondary);
            bCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            int gw = Math.Max(Theme.S(150), bGo.PreferredWidth + Theme.S(20)), xw = Math.Max(Theme.S(100), bCancel.PreferredWidth + Theme.S(20));
            bGo.Bounds = Theme.Mirror(new Rectangle(W - pad - gw, y, gw, Theme.S(40)), W);
            bCancel.Bounds = Theme.Mirror(new Rectangle(W - pad - gw - Theme.S(10) - xw, y, xw, Theme.S(40)), W);
            ClientSize = new Size(W, y + Theme.S(40) + pad);
            foreach (UiControl c in new UiControl[] { lang, subLang, quality, bGo, bCancel }) { c.Surface = Surface.Card; Controls.Add(c); }
            AcceptButton = null;
            Shown += (s, e) => bGo.Focus();
        }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (k == Keys.Enter) { bGo.PerformClickSafe(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            using (Pen p = new Pen(Theme.CardBorder)) using (GraphicsPath gp = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9))) g.DrawPath(p, gp);
            int pad = Theme.S(26), W = Width;
            Rectangle ic = Theme.Mirror(new Rectangle(pad, Theme.S(22), Theme.S(42), Theme.S(42)), W);
            Theme.FillRound(g, ic, Theme.S(11), Theme.AccentSoft);
            Theme.Glyph(g, "", 13f, Theme.AccentColor, ic);
            Theme.Text(g, L.T("cap_title"), Theme.Display(15f), Theme.Ink, Theme.Mirror(new Rectangle(pad + Theme.S(56), Theme.S(18), W - pad * 2 - Theme.S(56), Theme.S(28)), W), Align.Start);
            Theme.Text(g, L.T("cap_sub"), Theme.F(9f), Theme.InkSoft, Theme.Mirror(new Rectangle(pad + Theme.S(56), Theme.S(46), W - pad * 2 - Theme.S(56), Theme.S(20)), W), Align.Start);
            Func<Rectangle, Rectangle> m = r => Theme.Mirror(r, W);
            Theme.Text(g, L.T("cap_lang").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, m(langLbl), Align.Start);
            Theme.Text(g, L.T("cap_quality").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, m(qLbl), Align.Start);
            Theme.Text(g, L.T("cap_sub_lang").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, m(subLbl), Align.Start);
            Theme.Text(g, L.T(English ? "cap_sub_hint_en" : "cap_sub_hint_same"), Theme.F(8.5f), Theme.InkSoft, m(subHint), Align.Start, false, true);
            int q = quality.SelectedIndex;
            string hint = L.T(q == 0 ? "cap_fast_hint" : "cap_accurate_hint") + "  " + (Whisper.Ready(q) ? L.T("cap_have") : L.T("cap_once", Whisper.ModelMb[q] + (Whisper.Exe == null ? 9 : 0)));
            Theme.Text(g, hint, Theme.F(8.5f), Theme.InkSoft, m(qHint), Align.Start, false, true);
            Rectangle nr = m(note);
            Theme.FillRound(g, nr, Theme.S(9), Theme.CardInner);
            Theme.Glyph(g, Ico.Check, 8f, Theme.Success, Theme.Mirror(new Rectangle(note.X + Theme.S(8), note.Y, Theme.S(20), note.Height), W));
            Theme.Text(g, L.T("cap_private"), Theme.F(8.5f), Theme.InkSoft, Theme.Mirror(new Rectangle(note.X + Theme.S(32), note.Y, note.Width - Theme.S(40), note.Height), W), Align.Start, true, true);
        }
    }

    public static class BtnExt
    {
        public static void PerformClickSafe(this Btn b) { if (b.Enabled && b.Visible) b.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(b, new object[] { EventArgs.Empty }); }
    }
}
