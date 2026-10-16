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
$langs = [Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages
if ($langs.Count -eq 0) { throw 'NOLANG' }
$engines = @()
foreach ($lang in $langs) { $e = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang); if ($e -ne $null) { $engines += ,@($lang.LanguageTag, $e) } }
$max = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
$sb = New-Object System.Text.StringBuilder
$n = 0
foreach ($path in $In.Split('|')) {
  $null = $sb.AppendLine('@@IMG ' + $n); $n++
  $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($path)) ([Windows.Storage.StorageFile])
  $stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
  $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
  $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
  if ($bitmap.PixelWidth -gt $max -or $bitmap.PixelHeight -gt $max) { $stream.Dispose(); continue }
  foreach ($pair in $engines) {
    $result = Await ($pair[1].RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $null = $sb.AppendLine('@@LANG ' + $pair[0])
    foreach ($line in $result.Lines) {
      $null = $sb.AppendLine('@@LINE')
      foreach ($w in $line.Words) { $r = $w.BoundingRect; $null = $sb.AppendLine([string]([int]$r.X) + [char]9 + [string]([int]$r.Y) + [char]9 + [string]([int]$r.Width) + [char]9 + [string]([int]$r.Height) + [char]9 + $w.Text) }
    }
  }
  $stream.Dispose()
}
[System.IO.File]::WriteAllText($Out, $sb.ToString(), [System.Text.Encoding]::UTF8)
";

        /// One word found in a picture, with its box (pixels of the picture that was read).
        public class Word { public int X, Y, W, H; public string Text; }
        /// One reader's (language's) result for one picture.
        public class Read { public string Lang = ""; public List<List<Word>> Lines = new List<List<Word>>(); public string Text = ""; public int Score; }

        /// Words come in the order they sit on the page (left to right). Arabic reads right to left, with any
        /// English or numbers inside it still left to right — this puts them back in reading order.
        static string LogicalLine(List<KeyValuePair<int, string>> words) { return string.Join(" ", LogicalOrder(words).ToArray()); }

        /// The words of a line in reading order (see LogicalLine).
        public static List<Word> LogicalWords(List<Word> line)
        {
            List<KeyValuePair<int, string>> kv = new List<KeyValuePair<int, string>>();
            List<Word> sorted = new List<Word>(line);
            sorted.Sort((a, b) => a.X.CompareTo(b.X));
            foreach (Word w in sorted) kv.Add(new KeyValuePair<int, string>(w.X, w.Text));
            List<string> order = LogicalOrder(kv);
            List<Word> outp = new List<Word>(); List<Word> left = new List<Word>(sorted);
            foreach (string t in order) { int k = left.FindIndex(w => w.Text == t); if (k >= 0) { outp.Add(left[k]); left.RemoveAt(k); } }
            outp.AddRange(left);
            return outp;
        }

        static List<string> LogicalOrder(List<KeyValuePair<int, string>> words)
        {
            List<KeyValuePair<int, string>> w = new List<KeyValuePair<int, string>>(words);
            if (w.Count == 0) return new List<string>();
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
            return outp;
        }
        static bool IsArabicWord(string s) { foreach (char c in s) if (Arabic(c)) return true; return false; }
        public static bool IsArabic(string s) { return IsArabicWord(s); }
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

        static string PowerShell { get { return Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"); } }

        /// Reads several pictures at once (one start of the reader for all of them): for each picture, every
        /// installed reading language's result.
        public static List<List<Read>> ReadPictures(IList<string> pngs, string workDir)
        {
            if (!IsWindows) throw new UserError(L.T("ocr_windows"));
            string ps1 = Path.Combine(workDir, "ocr.ps1"), outp = Path.Combine(workDir, "out_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".txt");
            File.WriteAllText(ps1, Script, new UTF8Encoding(true));
            string o;
            Tools.RunHidden(PowerShell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Tools.Quote(ps1) + " -In " + Tools.Quote(string.Join("|", new List<string>(pngs).ToArray())) + " -Out " + Tools.Quote(outp), 600000, out o);
            if (!File.Exists(outp))
            {
                if ((o ?? "").Contains("NOLANG")) throw new UserError(L.T("ocr_no_lang"));
                throw new UserError(L.T("ocr_failed") + " " + Tools.LastLines(o ?? "", 1));
            }
            List<List<Read>> all = new List<List<Read>>();
            for (int i = 0; i < pngs.Count; i++) all.Add(new List<Read>());
            List<Read> cur = null; Read rd = null; List<Word> line = null;
            foreach (string ln in File.ReadAllLines(outp, Encoding.UTF8))
            {
                if (ln.StartsWith("@@IMG ")) { int k; cur = int.TryParse(ln.Substring(6).Trim(), out k) && k < all.Count ? all[k] : null; rd = null; line = null; continue; }
                if (ln.StartsWith("@@LANG ")) { if (cur == null) continue; rd = new Read { Lang = ln.Substring(7).Trim().ToLowerInvariant() }; cur.Add(rd); line = null; continue; }
                if (rd == null) continue;
                if (ln == "@@LINE") { line = new List<Word>(); rd.Lines.Add(line); continue; }
                string[] f = ln.Split(new[] { '\t' }, 5);
                int x, y, w, h;
                if (line != null && f.Length == 5 && int.TryParse(f[0], out x) && int.TryParse(f[1], out y) && int.TryParse(f[2], out w) && int.TryParse(f[3], out h))
                    line.Add(new Word { X = x, Y = y, W = w, H = h, Text = f[4] });
            }
            try { File.Delete(outp); } catch { }
            foreach (List<Read> reads in all)
                foreach (Read r in reads)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (List<Word> l in r.Lines) sb.AppendLine(FixBrackets(LogicalLine(ToKv(l))));
                    r.Text = sb.ToString().Trim();
                    r.Score = Score(r.Lang, r.Text);
                }
            return all;
        }

        static List<KeyValuePair<int, string>> ToKv(List<Word> l) { List<KeyValuePair<int, string>> kv = new List<KeyValuePair<int, string>>(); foreach (Word w in l) kv.Add(new KeyValuePair<int, string>(w.X, w.Text)); return kv; }

        /// The reader to trust for one picture: the Arabic one when there's real Arabic in it (it reads English too),
        /// otherwise the one that read the most.
        public static Read Best(List<Read> reads)
        {
            Read best = null, arabic = null; int arabicLetters = 0, arabicAll = 0;
            foreach (Read r in reads)
            {
                if (r.Lang.StartsWith("ar") || r.Lang.StartsWith("fa") || r.Lang.StartsWith("ur"))
                {
                    int ar = 0, all = 0;
                    foreach (char c in r.Text) { if (Arabic(c)) ar++; if (char.IsLetter(c)) all++; }
                    if (ar > arabicLetters) { arabicLetters = ar; arabicAll = all; arabic = r; }
                }
                if (best == null || r.Score > best.Score) best = r;
            }
            if (arabic != null && arabicLetters >= 3 && arabicLetters * 100 >= arabicAll * 12) return arabic;
            return best;
        }

        /// A closing bracket at the end of a line is easily read as "1" or "l" ("...is 8]" became "...is 81").
        /// When the line opened a bracket and never closed it, that last character was the bracket.
        public static string FixBrackets(string line)
        {
            if (line.Length < 3) return line;
            // (only square brackets: they rarely run on to the next line, unlike round ones in sentences)
            foreach (char[] pair in new[] { new[] { '[', ']' } })
            {
                int open = 0, close = 0;
                foreach (char c in line) { if (c == pair[0]) open++; else if (c == pair[1]) close++; }
                if (open <= close) continue;
                char last = line[line.Length - 1];
                int at = line.LastIndexOf(pair[0]);
                if ((last == '1' || last == 'l' || last == 'I' || last == '|' || last == 'J' || last == 'j') && at >= 0 && at < line.Length - 2 && !char.IsWhiteSpace(line[line.Length - 2]))
                    line = line.Substring(0, line.Length - 1) + pair[1];
            }
            return line;
        }

        static int MedianHeight(Read r)
        {
            List<int> h = new List<int>();
            if (r != null) foreach (List<Word> l in r.Lines) foreach (Word w in l) if (w.Text.Length > 0) h.Add(w.H);
            if (h.Count == 0) return 0;
            h.Sort();
            return h[h.Count / 2];
        }

        /// area: the part to read, 0..1 of the picture as it's shown (after turning).
        public static string Recognize(string imagePath, int rotation, RectangleF area)
        {
            if (!IsWindows) throw new UserError(L.T("ocr_windows"));
            string dir = Path.Combine(Path.GetTempPath(), "FadiOcr_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(dir);
            try
            {
                // any format (HEIC, WEBP...) → PNGs Windows can read: the chosen part as it's shown, and the same
                // part twice as big (small text, like a "]" at the end of a line, is read much better bigger)
                string p1 = Path.Combine(dir, "in.png"), p2 = Path.Combine(dir, "in2.png");
                Bitmap part;
                using (Image im = ViewerPage.Decode(imagePath, 4000))
                using (Bitmap b = new Bitmap(im))
                {
                    if (rotation == 90) b.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    else if (rotation == 180) b.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    else if (rotation == 270) b.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    Rectangle crop = Rectangle.FromLTRB((int)Math.Floor(Math.Max(0, area.Left) * b.Width), (int)Math.Floor(Math.Max(0, area.Top) * b.Height),
                                                        (int)Math.Ceiling(Math.Min(1, area.Right) * b.Width), (int)Math.Ceiling(Math.Min(1, area.Bottom) * b.Height));
                    if (crop.Width < 4 || crop.Height < 4) crop = new Rectangle(0, 0, b.Width, b.Height);
                    part = crop.Width == b.Width && crop.Height == b.Height ? new Bitmap(b) : b.Clone(crop, b.PixelFormat);
                }
                List<string> pics = new List<string>();
                using (part)
                {
                    // a little white border helps the reader with text that touches the edge of the chosen part
                    float k1 = Math.Max(part.Width, part.Height) < 900 ? Math.Min(4f, 900f / Math.Max(part.Width, part.Height)) : 1f;
                    SavePadded(part, k1, p1); pics.Add(p1);
                    float k2 = k1 * 2;
                    if (Math.Max(part.Width, part.Height) * k2 <= 7000) { SavePadded(part, k2, p2); pics.Add(p2); }
                    List<List<Read>> res = ReadPictures(pics, dir);
                    Read r1 = Best(res[0]), r2 = res.Count > 1 && res[1].Count > 0 ? Best(res[1]) : null;
                    // small text: trust the bigger copy (as long as it found about as much)
                    int mh = MedianHeight(r1);
                    Read best = r1;
                    if (r2 != null && (r1 == null || (mh > 0 && mh < 30 && r2.Score * 10 >= r1.Score * 8) || r2.Score > r1.Score * 1.25)) best = r2;
                    // hardly anything: the text may be turned (a page turned in the viewer, a photo taken sideways)
                    if (best == null || best.Score < 12)
                    {
                        List<string> turned = new List<string>();
                        RotateFlipType[] turns = { RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate270FlipNone };
                        for (int i = 0; i < 3; i++)
                        {
                            string pt = Path.Combine(dir, "turn" + i + ".png");
                            using (Bitmap tb = new Bitmap(part)) { tb.RotateFlip(turns[i]); SavePadded(tb, Math.Max(k1, Math.Max(tb.Width, tb.Height) * 1.6f <= 7000 ? 1.6f * k1 : k1), pt); }
                            turned.Add(pt);
                        }
                        foreach (List<Read> rr in ReadPictures(turned, dir))
                        {
                            Read c = Best(rr);
                            if (c != null && (best == null || c.Score > best.Score * 1.5 + 4)) best = c;
                        }
                    }
                    return best == null ? "" : best.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                }
            }
            finally { ThreadPool.QueueUserWorkItem(_ => { try { Directory.Delete(dir, true); } catch { } }); }
        }

        static void SavePadded(Bitmap src, float k, string path)
        {
            int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k)), pad = Math.Max(8, (int)(Math.Min(w, h) * 0.02f));
            using (Bitmap outb = new Bitmap(w + pad * 2, h + pad * 2, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(outb))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = k > 1.01f ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new Rectangle(pad, pad, w, h));
                }
                outb.Save(path, ImageFormat.Png);
            }
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
            Movable.Make(this, () => Theme.S(70));   // drag it by the top
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
