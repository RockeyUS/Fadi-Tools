using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Virtualized hex dump: offset | 16 bytes | ASCII. Always left-to-right.
    public class HexView : ScrollList
    {
        FileStream fs; long length;
        byte[] cache = new byte[0]; long cacheStart = -1;
        public HexView() { RowHeight = 22; }
        public override int Count { get { return (int)Math.Min(int.MaxValue / 2, (length + 15) / 16); } }

        public void Open(string path)
        {
            Close();
            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); length = fs.Length; } catch { fs = null; length = 0; }
            ScrollTo(0); cacheStart = -1; Invalidate();
        }
        public void Close() { if (fs != null) fs.Dispose(); fs = null; length = 0; cacheStart = -1; }

        byte[] Row(long offset, out int n)
        {
            n = 0;
            if (fs == null) return null;
            if (cacheStart < 0 || offset < cacheStart || offset + 16 > cacheStart + cache.Length)
            {
                cacheStart = Math.Max(0, offset - 32768);
                cache = new byte[(int)Math.Min(65536, length - cacheStart)];
                fs.Position = cacheStart;
                int got = 0, r;
                while (got < cache.Length && (r = fs.Read(cache, got, cache.Length - got)) > 0) got += r;
            }
            int start = (int)(offset - cacheStart);
            n = (int)Math.Min(16, cache.Length - start);
            byte[] b = new byte[Math.Max(0, n)];
            if (n > 0) Array.Copy(cache, start, b, 0, n);
            return b;
        }

        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            if (hover) using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillRectangle(hb, r);
            long off = (long)i * 16;
            int n; byte[] b = Row(off, out n);
            if (b == null) return;
            Font f = Theme.Mono(9.5f);
            int cw = TextRenderer.MeasureText("0", f, new Size(100, 100), TextFormatFlags.NoPadding).Width;
            int x = (Theme.RTL ? BarW : 0) + Theme.S(14);
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, off.ToString("X8"), f, new Rectangle(x, r.Y, cw * 9, r.Height), Theme.InkFaint, fl);
            x += cw * 10;
            StringBuilder hex = new StringBuilder(), asc = new StringBuilder();
            for (int k = 0; k < 16; k++)
            {
                if (k < n) { hex.Append(b[k].ToString("X2")).Append(' '); asc.Append(b[k] >= 32 && b[k] < 127 ? (char)b[k] : '·'); }
                else hex.Append("   ");
                if (k == 7) hex.Append(' ');
            }
            TextRenderer.DrawText(g, hex.ToString(), f, new Rectangle(x, r.Y, cw * 50, r.Height), Theme.Ink, fl);
            x += cw * 51;
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, x - cw / 2, r.Y, x - cw / 2, r.Bottom);
            TextRenderer.DrawText(g, asc.ToString(), f, new Rectangle(x + cw / 2, r.Y, cw * 18, r.Height), Theme.InkSoft, fl);
        }
        protected override void Dispose(bool disposing) { Close(); base.Dispose(disposing); }
    }

    public class StringsView : ScrollList
    {
        public List<KeyValuePair<long, string>> Items = new List<KeyValuePair<long, string>>();
        public StringsView() { RowHeight = 24; }
        public override int Count { get { return Items.Count; } }
        protected override void PaintEmpty(Graphics g) { Theme.Text(g, L.T("no_strings"), Theme.F(9.5f), Theme.InkFaint, ClientRectangle, Align.Center); }
        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            if (hover) using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillRectangle(hb, r);
            Font f = Theme.Mono(9.25f);
            TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            int x = r.X + Theme.S(14);
            TextRenderer.DrawText(g, Items[i].Key.ToString("X8"), f, new Rectangle(x, r.Y, Theme.S(80), r.Height), Theme.InkFaint, fl);
            TextRenderer.DrawText(g, Items[i].Value, f, new Rectangle(x + Theme.S(90), r.Y, r.Width - Theme.S(110), r.Height), Theme.Ink, fl);
        }
        protected override void RowDoubleClick(int index) { try { Clipboard.SetText(Items[index].Value); } catch { } }
    }

    /// Key/value rows with optional copy buttons (file details and checksums).
    public class DetailsView : ScrollList
    {
        public List<string[]> Rows = new List<string[]>();   // label, value, copyable("1")
        int hoverCopy = -1;
        public event Action<string> Copied;
        public DetailsView() { RowHeight = 40; }
        public override int Count { get { return Rows.Count; } }
        Rectangle CopyRect(Rectangle r)
        {
            Rectangle c = new Rectangle(r.Right - Theme.S(44), r.Y + Theme.S(5), Theme.S(30), Theme.S(30));
            return Theme.RTL ? new Rectangle(r.X + Theme.S(14), c.Y, c.Width, c.Height) : c;
        }
        protected override void RowHover(int i, Point p, Rectangle r)
        {
            int h = Rows[i].Length > 2 && CopyRect(r).Contains(p) ? i : -1;
            if (h != hoverCopy) { hoverCopy = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void RowClick(int i, Point p, Rectangle r)
        {
            if (Rows[i].Length > 2 && CopyRect(r).Contains(p)) { try { Clipboard.SetText(Rows[i][1]); if (Copied != null) Copied(Rows[i][0]); } catch { } }
        }
        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            string[] row = Rows[i];
            Theme.Smooth(g);
            int lw = Theme.S(150);
            Rectangle lr = new Rectangle(r.X + Theme.S(16), r.Y, lw, r.Height);
            Rectangle vr = new Rectangle(r.X + Theme.S(16) + lw, r.Y, r.Width - lw - Theme.S(70), r.Height);
            if (Theme.RTL) { lr.X = r.Right - lr.Right + r.X; vr.X = r.Right - vr.Right + r.X; }
            Theme.Text(g, row[0], Theme.F(9f, FontStyle.Bold), Theme.InkSoft, lr);
            bool mono = row.Length > 2;
            if (mono) TextRenderer.DrawText(g, row[1], Theme.Mono(9f), vr, Theme.Ink, (Theme.RTL ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            else Theme.Text(g, row[1], Theme.F(9.5f), Theme.Ink, vr);
            if (row.Length > 2)
            {
                Rectangle c = CopyRect(r);
                if (i == hoverCopy) Theme.FillRound(g, c, Theme.S(8), Theme.Pressed);
                Theme.Glyph(g, Ico.Copy, 9.5f, Theme.InkSoft, c);
            }
            using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, r.X + Theme.S(16), r.Bottom - 1, r.Right - Theme.S(16), r.Bottom - 1);
        }
    }

    public class InspectPage : Page
    {
        Rectangle head, body, emptyCard, nameR, typeR;
        DropZone drop;
        Btn btnConvert, btnWindows, btnOther, btnSaveAs, btnPlay, btnRe;
        Notice mismatch;

        Segments tabs;
        DetailsView details;
        HexView hex;
        TextBox text;
        Segments textMode, encSeg;
        StringsView strings;
        FrameView preview;
        WebBrowser web;
        Label noPreview;
        string file; Sniff sniff;
        int token;
        public event Action<string> SendToConverter;
        public event Action<string> SendToRe;
        bool ReFits { get { if (file == null) return false; string e = Formats.Ext(file); return Rea.IsPe(file) || e == "apk" || e == "asar" || e == "har"; } }
        string[] hashes;
        MediaInfo media;

        public InspectPage() : base("insp_title", "insp_sub")
        {
            drop = new DropZone(() => L.T("insp_drop"), () => L.T("insp_drop_sub")) { Tips = new[] { "tip_fake_ext", "tip_paste" } };
            drop.Glyph = Ico.Search;
            drop.Click += (s, e) => Browse();
            btnConvert = new Btn(() => L.T("send_convert"), Ico.Sync, BtnKind.Primary);
            btnConvert.FontSize = 9.5f;
            btnConvert.Click += (s, e) => { if (file != null && SendToConverter != null) SendToConverter(file); };
            btnWindows = new Btn(() => L.T("open_windows"), Ico.Open, BtnKind.Secondary);
            btnWindows.Click += (s, e) => { if (file != null) try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { try { Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + file); } catch { } } };
            btnRe = new Btn(() => L.T("re_open_in"), Ico.Code, BtnKind.Secondary);
            btnRe.Click += (s, e) => { if (file != null && SendToRe != null) SendToRe(file); };
            btnOther = new Btn(() => L.T("open_other"), Ico.FolderOpen, BtnKind.Ghost);
            btnOther.Click += (s, e) => Browse();
            btnSaveAs = new Btn(() => sniff != null ? L.T("save_as_real", sniff.Ext) : "", Ico.Save, BtnKind.Secondary);
            btnSaveAs.Click += (s, e) => SaveAsReal();
            btnPlay = new Btn(() => L.T("play"), Ico.Play, BtnKind.Secondary);
            btnPlay.Click += (s, e) => { if (file != null && media != null) Media.Play(file, 0, 0, media.HasVideo); };
            mismatch = new Notice(() => sniff == null ? "" : L.T("ext_mismatch", Formats.Ext(file).ToUpperInvariant(), sniff.Name)) { Kind = 1, Glyph = Ico.Warning };
            tabs = new Segments(() => L.T("tab_info"), () => L.T("tab_preview"), () => L.T("tab_hex"), () => L.T("tab_text"), () => L.T("tab_strings"));
            tabs.FadeTarget = this;
            tabs.FadeArea = () => new Rectangle(0, tabs.Bottom + Theme.S(4), Width, Math.Max(0, Height - tabs.Bottom - Theme.S(4)));
            tabs.SelectedChanged += (s, e) => ShowTab();
            details = new DetailsView();
            details.Copied += lbl => ShowToast(L.T("copied", lbl), true, null, null);
            hex = new HexView();
            text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None, Font = Theme.Mono(10f) };
            strings = new StringsView();
            textMode = new Segments(() => L.T("rd_readable"), () => L.T("rd_raw"));
            textMode.SelectedChanged += (s2, e2) => { text.Tag = null; ShowTab(); };
            encSeg = new Segments(() => L.T("enc_auto"), () => "UTF-8", () => "UTF-16", () => L.T("enc_arabic"), () => L.T("enc_western"));
            encSeg.FontSize = 8.75f;
            encSeg.SelectedChanged += (s2, e2) => { text.Tag = null; ShowTab(); };
            preview = new FrameView();
            noPreview = new Label { TextAlign = ContentAlignment.MiddleCenter, Font = Theme.F(10f) };
            Controls.AddRange(new Control[] { drop, btnConvert, btnWindows, btnOther, btnRe, btnSaveAs, btnPlay, mismatch, tabs, details, hex, text, strings, preview, noPreview, textMode, encSeg });
            EnableDrop(drop, details, hex, strings, preview);
            Restyle();
            ShowState();
        }

        void EnsureWeb()
        {
            if (web != null) return;
            try
            {
                web = new WebBrowser { ScriptErrorsSuppressed = true, AllowWebBrowserDrop = false, IsWebBrowserContextMenuEnabled = true, Visible = false };
                Controls.Add(web);
            }
            catch { web = null; }
        }

        public override void Restyle()
        {
            text.BackColor = Theme.Card; text.ForeColor = Theme.Ink;
            noPreview.BackColor = Theme.Card; noPreview.ForeColor = Theme.InkFaint;
            if (text.IsHandleCreated) Theme.ApplyNativeTheme(text);
            else text.HandleCreated += (s, e) => Theme.ApplyNativeTheme(text);
        }

        void ShowState()
        {
            bool has = file != null;
            drop.Visible = !has;
            foreach (Control c in new Control[] { btnConvert, btnWindows, btnOther, tabs }) c.Visible = has;
            btnRe.Visible = has && ReFits;
            bool mis = has && sniff != null && sniff.Ext.Length > 0 && !SameExt(Formats.Ext(file), sniff.Ext);
            mismatch.Visible = mis; btnSaveAs.Visible = mis;
            ShowTab();
            DoLayout();
        }

        static bool SameExt(string a, string b)
        {
            if (a == b) return true;
            string[][] eq = { new[] { "jpg", "jpeg", "jfif", "jpe" }, new[] { "tif", "tiff" }, new[] { "htm", "html", "xhtml" }, new[] { "md", "markdown", "mdown" },
                new[] { "mp4", "m4v", "mov", "3gp", "m4a", "m4b", "f4v" }, new[] { "mkv", "webm", "mka" }, new[] { "txt", "log", "ini", "cfg", "csv", "tsv", "md", "json", "xml", "svg", "srt", "bat", "ps1", "py", "js", "cs", "css", "yml", "yaml" },
                new[] { "heic", "heif" }, new[] { "ogg", "oga", "opus", "ogv" }, new[] { "zip", "docx", "xlsx", "pptx", "jar", "apk", "epub", "odt", "ods", "odp", "nupkg", "vsix" },
                new[] { "exe", "dll", "sys", "scr", "ocx", "cpl", "msi" }, new[] { "wmv", "wma", "asf" }, new[] { "mpg", "mpeg", "vob" }, new[] { "ts", "mts", "m2ts" }, new[] { "ttf", "otf", "ttc" },
                new[] { "cr2", "nef", "arw", "dng", "orf", "rw2", "pef", "tiff", "tif" }, new[] { "doc", "xls", "ppt", "msi", "msg" }, new[] { "mp3", "mp2" }, new[] { "xml", "svg", "xaml", "csproj", "plist", "config" } };
            foreach (string[] g in eq) if (Array.IndexOf(g, a) >= 0 && Array.IndexOf(g, b) >= 0) return true;
            return false;
        }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, ip = Theme.S(20);
            cards.Clear();
            if (file == null)
            {
                emptyCard = new Rectangle(p, Top0, W - p * 2, H - Top0 - p);
                cards.Add(emptyCard);
                Place(drop, new Rectangle(emptyCard.X + ip, emptyCard.Y + ip, emptyCard.Width - ip * 2, emptyCard.Height - ip * 2));
                Invalidate(); return;
            }
            int headH = mismatch.Visible ? Theme.S(176) : Theme.S(124);
            head = new Rectangle(p, Top0, W - p * 2, headH);
            body = new Rectangle(p, head.Bottom + Theme.S(16), W - p * 2, H - head.Bottom - Theme.S(16) - p);
            cards.Add(head); cards.Add(body);
            int x = head.X + ip, w = head.Width - ip * 2;
            nameR = new Rectangle(x + Theme.S(66), head.Y + Theme.S(18), w - Theme.S(66), Theme.S(26));
            typeR = new Rectangle(x + Theme.S(66), head.Y + Theme.S(44), w - Theme.S(66), Theme.S(20));
            int by = head.Y + Theme.S(74);
            int cw = Math.Max(Theme.S(160), btnConvert.PreferredWidth), ww = Math.Max(Theme.S(160), btnWindows.PreferredWidth), ow = Math.Max(Theme.S(140), btnOther.PreferredWidth);
            Place(btnConvert, new Rectangle(x, by, cw, Theme.S(36)));
            Place(btnWindows, new Rectangle(x + cw + Theme.S(10), by, ww, Theme.S(36)));
            int rw2 = Math.Max(Theme.S(170), btnRe.PreferredWidth);
            Place(btnRe, new Rectangle(x + cw + ww + Theme.S(20), by, rw2, Theme.S(36)));
            Place(btnOther, new Rectangle(x + w - ow, by, ow, Theme.S(36)));
            if (mismatch.Visible)
            {
                int sw = Math.Max(Theme.S(170), btnSaveAs.PreferredWidth);
                Place(mismatch, new Rectangle(x, by + Theme.S(48), w - sw - Theme.S(10), Theme.S(42)));
                Place(btnSaveAs, new Rectangle(x + w - sw, by + Theme.S(51), sw, Theme.S(36)));
            }
            Place(tabs, new Rectangle(body.X + ip, body.Y + Theme.S(14), Math.Min(Theme.S(560), body.Width - ip * 2), Theme.S(38)));
            Rectangle content = new Rectangle(body.X + Theme.S(6), body.Y + Theme.S(62), body.Width - Theme.S(12), body.Height - Theme.S(70));
            foreach (Control c in new Control[] { details, hex, strings }) Place(c, content);
            Place(textMode, new Rectangle(content.X + Theme.S(14), content.Y, Theme.S(220), Theme.S(32)));
            Place(encSeg, new Rectangle(content.X + Theme.S(244), content.Y, Math.Min(Theme.S(430), content.Width - Theme.S(260)), Theme.S(32)));
            Place(text, new Rectangle(content.X + Theme.S(12), content.Y + Theme.S(42), content.Width - Theme.S(16), content.Height - Theme.S(48)));
            Rectangle pv = new Rectangle(content.X + Theme.S(14), content.Y, content.Width - Theme.S(28), content.Height - Theme.S(60));
            Place(preview, pv);
            Place(btnPlay, new Rectangle(pv.X, pv.Bottom + Theme.S(12), Math.Max(Theme.S(120), btnPlay.PreferredWidth), Theme.S(36)));
            Place(noPreview, content);
            if (web != null) Place(web, new Rectangle(content.X + Theme.S(8), content.Y, content.Width - Theme.S(16), content.Height - Theme.S(8)));
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            if (file == null || sniff == null) return;
            Rectangle badge = new Rectangle(head.X + Theme.S(20), head.Y + Theme.S(18), Theme.S(50), Theme.S(50));
            Theme.FillRoundGradient(g, M(badge), Theme.S(12), Theme.PrimaryTop, Theme.PrimaryBottom);
            Theme.Glyph(g, Formats.GlyphFor(sniff.Category), 16f, Theme.PrimaryInk, M(badge));
            Label(g, Path.GetFileName(file), Theme.F(12f, FontStyle.Bold), Theme.Ink, nameR);
            string t = sniff.Name + "  ·  " + Theme.HumanSize(sniff.Size) + (sniff.Encoding.Length > 0 ? "  ·  " + sniff.Encoding : "");
            Label(g, t, Theme.F(9.25f), Theme.InkSoft, typeR);
        }

        void ShowTab()
        {
            bool has = file != null;
            int i = tabs.SelectedIndex;
            details.Visible = has && i == 0;
            hex.Visible = has && i == 2;
            text.Visible = has && i == 3;
            textMode.Visible = has && i == 3;
            encSeg.Visible = has && i == 3 && (sniff != null && sniff.IsText || textMode.SelectedIndex == 1 || encSeg.SelectedIndex > 0);
            strings.Visible = has && i == 4;
            bool pv = has && i == 1;
            bool webPv = pv && sniff != null && (Formats.IsMarkdown(Formats.Ext(file)) || sniff.Ext == "html" || sniff.Ext == "md" || sniff.Ext == "svg");
            bool imgPv = pv && sniff != null && !webPv && (sniff.Category == Category.Image || (media != null && (media.HasVideo || media.HasAudio)));
            if (webPv) EnsureWeb();
            if (web != null) web.Visible = webPv;
            preview.Visible = imgPv;
            btnPlay.Visible = imgPv && media != null && (media.HasVideo || media.HasAudio);
            noPreview.Visible = pv && !imgPv && !(webPv && web != null);
            noPreview.Text = L.T("no_preview");
            if (i == 3 && has && text.Tag as string != file) LoadText();
            if (i == 4 && has && strings.Tag as string != file) LoadStrings();
            if (webPv && web != null && web.Tag as string != file) LoadWeb();
            DoLayoutLight();
        }

        void DoLayoutLight() { if (Width > 0) DoLayout(); }

        public void Browse()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("insp_drop"); d.Filter = L.T("all_files") + "|*.*";
                if (LastDir.Show(d, FindForm(), "inspect") == DialogResult.OK) AcceptFiles(new[] { d.FileName });
            }
        }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0 || !File.Exists(paths[0])) return;
            Inspect(paths[0]);
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        void Inspect(string path)
        {
            int tk = ++token;
            Thread t = new Thread(() =>
            {
                Sniff s; try { s = Sniffer.Detect(path); } catch (Exception ex) { Ui(() => ShowToast(ex.Message, false, null, null)); return; }
                MediaInfo mi = null;
                if (s.Category == Category.Video || s.Category == Category.Audio) try { mi = Media.Probe(path); } catch { }
                Ui(() =>
                {
                    if (tk != token) return;
                    file = path; sniff = s; media = mi; hashes = null;
                    text.Tag = null; strings.Tag = null; if (web != null) web.Tag = null;
                    textMode.SetSilently(0); encSeg.SetSilently(0);
                    if (preview.Frame != null) { preview.Frame.Dispose(); preview.Frame = null; }
                    preview.IsAudio = mi != null && !mi.HasVideo; preview.Caption = "";
                    hex.Open(path);
                    BuildDetails();
                    tabs.SetSilently(0);
                    ShowState();
                    LoadPreview(tk);
                    ComputeHashes(tk);
                });
            });
            t.IsBackground = true; t.Start();
        }

        void BuildDetails()
        {
            FileInfo fi = new FileInfo(file);
            details.Rows.Clear();
            details.Rows.Add(new[] { L.T("detected"), sniff.Name + (sniff.Ext.Length > 0 ? "  (." + sniff.Ext + ")" : "") });
            details.Rows.Add(new[] { L.T("location"), fi.DirectoryName });
            details.Rows.Add(new[] { L.T("size"), Theme.HumanSize(fi.Length) + "  (" + fi.Length.ToString("N0") + " " + L.T("bytes") + ")" });
            details.Rows.Add(new[] { L.T("modified"), fi.LastWriteTime.ToString("yyyy-MM-dd  HH:mm") });
            details.Rows.Add(new[] { L.T("created"), fi.CreationTime.ToString("yyyy-MM-dd  HH:mm") });
            if (sniff.Encoding.Length > 0) details.Rows.Add(new[] { L.T("encoding"), sniff.Encoding });
            string ent = sniff.Entropy.ToString("0.00") + " / 8  —  " + (sniff.Entropy > 7.5 ? L.T("ent_high") : sniff.Entropy > 5 ? L.T("ent_mid") : L.T("ent_low"));
            details.Rows.Add(new[] { L.T("entropy"), ent });
            if (media != null)
            {
                details.Rows.Add(new[] { L.T("length"), Theme.Time(media.Duration) });
                if (media.HasVideo) details.Rows.Add(new[] { L.T("video"), media.Width + "×" + media.Height + "  ·  " + media.VCodec.ToUpperInvariant() + (media.Fps > 0 ? "  ·  " + media.Fps.ToString("0.##") + " fps" : "") });
                if (media.HasAudio) details.Rows.Add(new[] { L.T("audio"), media.ACodec.ToUpperInvariant() + (media.SampleRate > 0 ? "  ·  " + media.SampleRate + " Hz" : "") + (media.Channels > 0 ? "  ·  " + media.Channels + " ch" : "") });
            }
            string wait = L.T("computing");
            details.Rows.Add(new[] { "MD5", hashes != null ? hashes[0] : wait, "1" });
            details.Rows.Add(new[] { "SHA-1", hashes != null ? hashes[1] : wait, "1" });
            details.Rows.Add(new[] { "SHA-256", hashes != null ? hashes[2] : wait, "1" });
            details.Invalidate();
        }

        void ComputeHashes(int tk)
        {
            string f = file;
            Thread t = new Thread(() =>
            {
                string[] h = null;
                try { h = Sniffer.Hashes(f, null, () => tk != token); } catch { }
                Ui(() => { if (tk == token && h != null) { hashes = h; BuildDetails(); } });
            });
            t.IsBackground = true; t.Start();
        }

        void LoadPreview(int tk)
        {
            string f = file; Sniff s = sniff; MediaInfo mi = media;
            if (!(s.Category == Category.Image || (mi != null && mi.HasVideo))) { preview.Invalidate(); return; }
            Thread t = new Thread(() =>
            {
                Image im = null;
                try
                {
                    if (mi != null && mi.HasVideo) im = Media.Frame(f, Math.Min(mi.Duration * 0.1, 5), 720);
                    else im = ViewerPage.Decode(f, 2400);
                }
                catch { }
                Ui(() => { if (tk == token) { preview.Frame = im; preview.Invalidate(); } else if (im != null) im.Dispose(); });
            });
            t.IsBackground = true; t.Start();
        }

        void LoadText()
        {
            text.Tag = file;
            string f = file; Sniff sn = sniff; int tk = token;
            bool readable = textMode.SelectedIndex == 0; int enc = encSeg.SelectedIndex;
            text.Text = L.T("v_loading");
            Thread t = new Thread(() =>
            {
                string s; bool rtl = false;
                try { s = Readable.Build(f, sn, readable, enc, out rtl); } catch (Exception ex) { s = ex.Message; }
                Ui(() =>
                {
                    if (tk != token) return;
                    text.WordWrap = readable && !(sn.Ext == "json" || sn.Ext == "xml" || sn.Ext == "csv");
                    text.RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
                    text.Text = s;
                    text.Select(0, 0);
                });
            });
            t.IsBackground = true; t.Start();
        }

        void LoadStrings()
        {
            strings.Tag = file;
            string f = file; int tk = token;
            Thread t = new Thread(() =>
            {
                List<KeyValuePair<long, string>> list = null;
                try { list = Sniffer.Strings(f, 5, 60000).FindAll(kv => Readable.IsWords(kv.Value.Replace("  (UTF-16)", ""))); } catch { }
                Ui(() => { if (tk == token && list != null) { strings.Items = list; strings.Invalidate(); } });
            });
            t.IsBackground = true; t.Start();
        }

        void LoadWeb()
        {
            if (web == null) return;
            web.Tag = file;
            try
            {
                string src = DataConv.ReadText(file);
                string html = Formats.IsMarkdown(Formats.Ext(file)) || sniff.Ext == "md" ? Markdown.ToHtmlDocument(src, Path.GetFileName(file)) : src;
                if (Theme.Dark && html.Contains("</style>") && sniff.Ext != "html")
                    html = html.Replace("</style>", "body{background:#1e1f22;color:#e6e7e9}h1,h2{border-color:#3a3d42}a{color:#7cb4ff}code{background:#2c2e33}pre{background:#26282c;border-color:#3a3d42}th{background:#2a2c30}th,td{border-color:#3a3d42}tr:nth-child(2n) td{background:#232427}blockquote{border-color:#4a4d54;color:#a9adb5}</style>");
                if (!html.Contains("X-UA-Compatible")) html = html.Replace("<head>", "<head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">");
                if (sniff.Ext == "html") web.Navigate(new Uri(file));
                else web.DocumentText = html;
            }
            catch { }
        }

        void SaveAsReal()
        {
            if (file == null || sniff == null || sniff.Ext.Length == 0) return;
            try
            {
                string dest = SaveAsk.Enabled ? SaveAsk.File(FindForm(), Path.GetFileNameWithoutExtension(file) + "." + sniff.Ext, sniff.Ext)
                                              : Paths.UniquePath(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file), sniff.Ext);
                if (dest == null) return;
                File.Copy(file, dest, true);
                ShowToast(L.T("saved_copy", Path.GetFileName(dest)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + dest + "\""); } catch { } });
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        protected override void DragHighlight(bool on) { drop.DragHot = on; drop.Invalidate(); }
    }
}
