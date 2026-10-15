using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// "Copy text from a picture": the text recognition built into Windows 10/11 (works offline).
    /// Every reading language installed in Windows is tried, and the one that reads the most is kept.
    public static class Ocr
    {
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        const string Script = @"param([string]$In, [string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$null = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
$ext = [System.WindowsRuntimeSystemExtensions].GetMethods()
$asTaskOp = $ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$type) { $t = $asTaskOp.MakeGenericMethod($type).Invoke($null, @($op)); $null = $t.Wait(-1); $t.Result }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($In)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$langs = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages
if ($langs.Count -eq 0) { throw 'NOLANG' }
$sb = New-Object System.Text.StringBuilder
foreach ($lang in $langs) {
  $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
  if ($engine -eq $null) { continue }
  $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
  $null = $sb.AppendLine('@@LANG ' + $lang.LanguageTag)
  foreach ($line in $result.Lines) {
    $null = $sb.AppendLine('@@LINE')
    foreach ($w in $line.Words) { $null = $sb.AppendLine([string]([int]$w.BoundingRect.X) + [char]9 + $w.Text) }
  }
}
[System.IO.File]::WriteAllText($Out, $sb.ToString(), [System.Text.Encoding]::UTF8)
";

        /// Words come in the order they sit on the page (left to right). Arabic reads right to left, with any
        /// English or numbers inside it still left to right — this puts them back in reading order.
        static string LogicalLine(List<KeyValuePair<int, string>> words)
        {
            List<KeyValuePair<int, string>> w = new List<KeyValuePair<int, string>>(words);
            w.Sort((a, b) => a.Key.CompareTo(b.Key));
            int ar = 0, lat = 0;
            foreach (KeyValuePair<int, string> k in w) { if (IsArabicWord(k.Value)) ar++; else if (HasLetter(k.Value)) lat++; }
            // mostly Arabic words = right to left; on a tie, the word at the right edge decides (where an Arabic line starts)
            bool rtl = ar > lat || (ar > 0 && ar == lat && IsArabicWord(w[w.Count - 1].Value));
            if (rtl) w.Reverse();
            // runs written the other way round go back to their own order
            List<string> outp = new List<string>();
            int i = 0;
            while (i < w.Count)
            {
                bool other = rtl ? !IsArabicWord(w[i].Value) : IsArabicWord(w[i].Value);
                if (!other) { outp.Add(w[i].Value); i++; continue; }
                int j = i;
                while (j < w.Count && (rtl ? !IsArabicWord(w[j].Value) : IsArabicWord(w[j].Value))) j++;
                for (int k = j - 1; k >= i; k--) outp.Add(w[k].Value);
                i = j;
            }
            return string.Join(" ", outp.ToArray());
        }
        static bool IsArabicWord(string s) { foreach (char c in s) if (Arabic(c)) return true; return false; }
        static bool HasLetter(string s) { foreach (char c in s) if (char.IsLetterOrDigit(c)) return true; return false; }

        static bool Arabic(char c) { return (c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) || (c >= 0xFB50 && c <= 0xFEFF); }

        /// How much of the text is in the script the reader was made for (a reader for the wrong language reads garbage).
        static int Score(string lang, string text)
        {
            bool rtl = lang.StartsWith("ar") || lang.StartsWith("fa") || lang.StartsWith("ur") || lang.StartsWith("he");
            int n = 0;
            foreach (char c in text)
            {
                if (rtl) { if (Arabic(c) || (c >= 0x0590 && c <= 0x05FF)) n++; }
                else if (char.IsLetter(c) && !Arabic(c)) n++;
                if (char.IsDigit(c)) n++;
            }
            return n;
        }

        /// Reads the text in a picture. rotation = how the picture is turned on screen (0/90/180/270).
        public static string Recognize(string imagePath, int rotation) { return Recognize(imagePath, rotation, new RectangleF(0, 0, 1, 1)); }

        /// area: the part to read, 0..1 of the picture as it's shown (after turning).
        public static string Recognize(string imagePath, int rotation, RectangleF area)
        {
            if (!IsWindows) throw new UserError(L.T("ocr_windows"));
            string dir = Path.Combine(Path.GetTempPath(), "FadiOcr_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(dir);
            try
            {
                // any format (HEIC, WEBP...) → a PNG Windows can read, not bigger than its reader allows
                string png = Path.Combine(dir, "in.png");
                using (Image im = ViewerPage.Decode(imagePath, 4000))
                using (Bitmap b = new Bitmap(im))
                {
                    if (rotation == 90) b.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    else if (rotation == 180) b.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    else if (rotation == 270) b.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    // just the part that was chosen
                    Rectangle crop = Rectangle.FromLTRB((int)Math.Floor(Math.Max(0, area.Left) * b.Width), (int)Math.Floor(Math.Max(0, area.Top) * b.Height),
                                                        (int)Math.Ceiling(Math.Min(1, area.Right) * b.Width), (int)Math.Ceiling(Math.Min(1, area.Bottom) * b.Height));
                    if (crop.Width < 4 || crop.Height < 4) crop = new Rectangle(0, 0, b.Width, b.Height);
                    using (Bitmap part = crop.Width == b.Width && crop.Height == b.Height ? new Bitmap(b) : b.Clone(crop, b.PixelFormat))
                    {
                        // small pictures (and small chosen parts) read better a little bigger
                        if (Math.Max(part.Width, part.Height) < 900)
                        {
                            float k = Math.Min(4f, 900f / Math.Max(part.Width, part.Height));
                            using (Bitmap big = EditSession.Scale(part, (int)(part.Width * k), (int)(part.Height * k))) big.Save(png, ImageFormat.Png);
                        }
                        else part.Save(png, ImageFormat.Png);
                    }
                }
                string ps1 = Path.Combine(dir, "ocr.ps1"), outp = Path.Combine(dir, "out.txt");
                File.WriteAllText(ps1, Script, new UTF8Encoding(true));
                string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                string o;
                int code = Tools.RunHidden(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Tools.Quote(ps1) + " -In " + Tools.Quote(png) + " -Out " + Tools.Quote(outp), 120000, out o);
                if (!File.Exists(outp))
                {
                    if ((o ?? "").Contains("NOLANG")) throw new UserError(L.T("ocr_no_lang"));
                    throw new UserError(L.T("ocr_failed") + " " + Tools.LastLines(o ?? "", 1));
                }
                // each reader's lines, with every word's place on the line
                List<string> langs = new List<string>();
                List<List<List<KeyValuePair<int, string>>>> reads = new List<List<List<KeyValuePair<int, string>>>>();
                List<List<KeyValuePair<int, string>>> curRead = null; List<KeyValuePair<int, string>> curLine = null;
                foreach (string line in File.ReadAllLines(outp, Encoding.UTF8))
                {
                    if (line.StartsWith("@@LANG ")) { langs.Add(line.Substring(7).Trim().ToLowerInvariant()); curRead = new List<List<KeyValuePair<int, string>>>(); reads.Add(curRead); curLine = null; continue; }
                    if (curRead == null) continue;
                    if (line == "@@LINE") { curLine = new List<KeyValuePair<int, string>>(); curRead.Add(curLine); continue; }
                    int tab = line.IndexOf('\t'); int x;
                    if (curLine != null && tab > 0 && int.TryParse(line.Substring(0, tab), out x)) curLine.Add(new KeyValuePair<int, string>(x, line.Substring(tab + 1)));
                }
                string best = ""; int bestScore = -1; string arabicRead = null; int arabicLetters = 0, arabicAll = 0;
                for (int i = 0; i < reads.Count; i++)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (List<KeyValuePair<int, string>> ln in reads[i]) sb.AppendLine(LogicalLine(ln));
                    string t = sb.ToString().Trim();
                    bool rtl = langs[i].StartsWith("ar") || langs[i].StartsWith("fa") || langs[i].StartsWith("ur");
                    if (rtl)
                    {
                        int ar = 0, all = 0;
                        foreach (char c in t) { if (Arabic(c)) ar++; if (char.IsLetter(c)) all++; }
                        if (ar > arabicLetters) { arabicLetters = ar; arabicAll = all; arabicRead = t; }
                    }
                    int sc = Score(langs[i], t);
                    if (sc > bestScore) { bestScore = sc; best = t; }
                }
                // any real Arabic in it: the Arabic reader (it reads English words too); otherwise the best reader
                if (arabicRead != null && arabicLetters >= 3 && arabicLetters * 100 >= arabicAll * 12) best = arabicRead;
                return best.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }
            finally { ThreadPool.QueueUserWorkItem(_ => { try { Directory.Delete(dir, true); } catch { } }); }
        }

        /// Reads a picture in the background and shows the text (already copied) in a small window.
        public static void Run(Control page, string imagePath, int rotation, Action<string, bool> toast) { Run(page, imagePath, rotation, new RectangleF(0, 0, 1, 1), toast, null); }

        /// again: "Read another part" in the result window.
        public static void Run(Control page, string imagePath, int rotation, RectangleF area, Action<string, bool> toast, Action again)
        {
            Form owner = page.FindForm();
            ActJob act = Activity.Begin(page, L.T("act_k_ocr"), Path.GetFileName(imagePath), null);
            if (toast != null) toast(L.T("ocr_reading"), true);
            Thread t = new Thread(() =>
            {
                string text = null, err = null;
                try { text = Recognize(imagePath, rotation, area); }
                catch (Exception ex) { err = ex.Message; }
                if (err == null && string.IsNullOrWhiteSpace(text)) err = L.T("ocr_nothing");
                Activity.End(act, err, false, err == null ? L.T("ocr_found", text.Length) : null, null);
                try
                {
                    page.BeginInvoke((Action)(() =>
                    {
                        if (err != null) { if (toast != null) toast(err, false); return; }
                        try { Clipboard.SetText(text); } catch { }
                        bool more = false;
                        using (TextResultForm f = new TextResultForm(L.T("ocr_title"), Path.GetFileName(imagePath), text))
                        {
                            f.StartPosition = FormStartPosition.CenterParent;
                            if (again != null) f.Again = () => more = true;
                            f.ShowDialog(owner);
                        }
                        if (more && again != null) again();
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }
    }

    /// Shows text you can read, fix and copy (used for text found in pictures).
    public class TextResultForm : Form
    {
        readonly string title, sub;
        readonly MultiField box = new MultiField();
        readonly Btn bCopy, bClose, bAgain;
        public Action Again;   // "Read another part"
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        bool copied = true;
        string sub2;

        public TextResultForm(string title, string sub, string text)
        {
            this.title = title; this.sub = sub;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Font = Theme.F(10f);
            BackColor = Theme.Card;
            Text = title;
            int W = Theme.S(560), H = Theme.S(460), pad = Theme.S(24);
            ClientSize = new Size(W, H);
            box.Surface = Surface.Card;
            box.Box.Text = text;
            box.Box.ScrollBars = ScrollBars.Vertical;
            box.Box.Font = Theme.F(10.5f);
            bool rtl = TextLayer.HasRtl(text);
            box.Box.RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
            box.Box.TextChanged += (s, e) => { copied = false; bCopy.Invalidate(); };
            box.Bounds = new Rectangle(pad, Theme.S(78), W - pad * 2, H - Theme.S(78) - Theme.S(76));
            // select words or lines in the box to copy only those; nothing selected = everything
            bCopy = new Btn(() => box.Box.SelectionLength > 0 ? L.T("ocr_copy_sel") : copied ? L.T("ocr_copied") : L.T("ocr_copy"), copied ? Ico.Check : Ico.Copy, BtnKind.Primary);
            bCopy.Click += (s, e) =>
            {
                try
                {
                    string pick = box.Box.SelectionLength > 0 ? box.Box.SelectedText : box.Box.Text;
                    Clipboard.SetText(pick.Length > 0 ? pick : " ");
                    copied = true; bCopy.Glyph = Ico.Check;
                    box.Box.SelectionLength = 0;
                    bCopy.Invalidate();
                    sub2 = box.Box.Text.Length > 0 && pick.Length < box.Box.Text.Length ? L.T("ocr_copied_part", pick.Length) : null; Invalidate();
                }
                catch { }
            };
            EventHandler sel = (s, e) => { if (box.Box.SelectionLength > 0) { copied = false; bCopy.Glyph = Ico.Copy; } bCopy.Invalidate(); };
            box.Box.MouseUp += (s, e) => sel(s, e);
            box.Box.KeyUp += (s, e) => sel(s, e);
            bAgain = new Btn(() => L.T("ocr_again"), "\uE8EF", BtnKind.Ghost);
            bAgain.Click += (s, e) => { if (Again != null) { Again(); Close(); } };
            bClose = new Btn(() => L.T("close"), null, BtnKind.Secondary);
            bClose.Click += (s, e) => Close();
            int bw = Math.Max(Theme.S(130), bCopy.PreferredWidth + Theme.S(24)), cw = Math.Max(Theme.S(100), bClose.PreferredWidth + Theme.S(20));
            bCopy.Bounds = Theme.Mirror(new Rectangle(W - pad - bw, H - Theme.S(58), bw, Theme.S(40)), W);
            bClose.Bounds = Theme.Mirror(new Rectangle(W - pad - bw - Theme.S(10) - cw, H - Theme.S(58), cw, Theme.S(40)), W);
            int aw = Math.Min(W - pad * 2 - bw - cw - Theme.S(20), bAgain.PreferredWidth + Theme.S(20));
            bAgain.Bounds = Theme.Mirror(new Rectangle(pad - Theme.S(8), H - Theme.S(58), aw, Theme.S(40)), W);
            foreach (Btn b in new[] { bCopy, bClose, bAgain }) b.Surface = Surface.Card;
            Controls.Add(box); Controls.Add(bCopy); Controls.Add(bClose); Controls.Add(bAgain);
            Shown += (s, e) => bAgain.Visible = Again != null && aw > Theme.S(60);
            Shown += (s, e) => { bCopy.Focus(); box.Box.SelectionStart = 0; box.Box.SelectionLength = 0; };
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
            if (k == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            using (Pen p = new Pen(Theme.CardBorder)) using (GraphicsPath gp = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9))) g.DrawPath(p, gp);
            int pad = Theme.S(24);
            Rectangle ic = Theme.Mirror(new Rectangle(pad, Theme.S(20), Theme.S(40), Theme.S(40)), Width);
            Theme.FillRound(g, ic, Theme.S(10), Theme.AccentSoft);
            Theme.Glyph(g, "", 12f, Theme.AccentColor, ic);
            Theme.Text(g, title, Theme.Serif(14f), Theme.Ink, Theme.Mirror(new Rectangle(pad + Theme.S(54), Theme.S(16), Width - pad * 2 - Theme.S(54), Theme.S(26)), Width));
            Theme.Text(g, sub + "  ·  " + (sub2 ?? L.T("ocr_hint")), Theme.F(8.75f), Theme.InkSoft, Theme.Mirror(new Rectangle(pad + Theme.S(54), Theme.S(42), Width - pad * 2 - Theme.S(54), Theme.S(20)), Width));
        }
    }
}
