using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Rounded thumbnail that plays animated GIFs, with small badges.
    public class ThumbView : UiControl
    {
        Image image;
        /// Width ÷ height of the picture shown (0 = none).
        public float Aspect { get { return image == null || image.Height == 0 ? 0 : image.Width / (float)image.Height; } }
        public string Badge = "", Corner = "";
        bool animating;
        public ThumbView() { }
        public Image Image
        {
            get { return image; }
            set
            {
                if (animating && image != null) { try { ImageAnimator.StopAnimate(image, OnFrame); } catch { } animating = false; }
                image = value;
                if (image != null && ImageAnimator.CanAnimate(image)) { ImageAnimator.Animate(image, OnFrame); animating = true; }
                Invalidate();
            }
        }
        void OnFrame(object s, EventArgs e) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0, 0, Width - 1, Height - 1);
            using (GraphicsPath clip = Theme.Round(r, Theme.S(12)))
            {
                GraphicsState st = g.Save();
                g.SetClip(clip);
                g.Clear(Theme.Dark ? Color.FromArgb(24, 25, 28) : Color.FromArgb(226, 228, 232));
                if (image != null)
                {
                    try
                    {
                        if (animating) ImageAnimator.UpdateFrames(image);
                        float sc = Math.Max(r.Width / image.Width, r.Height / image.Height);
                        float w = image.Width * sc, h = image.Height * sc;
                        g.DrawImage(image, (r.Width - w) / 2, (r.Height - h) / 2, w, h);
                    }
                    catch { }
                }
                else Theme.Glyph(g, Ico.Photo, 22f, Theme.InkFaint, Rectangle.Round(r));
                g.Restore(st);
            }
            Theme.StrokeRound(g, r, Theme.S(12), Color.FromArgb(40, 0, 0, 0));
            Font f = Theme.F(8f, FontStyle.Bold);
            if (!string.IsNullOrEmpty(Badge))
            {
                int w = Theme.Measure(Badge, f) + Theme.S(16);
                Rectangle br = new Rectangle(Theme.S(8), Theme.S(8), w, Theme.S(22));
                Theme.FillRound(g, br, Theme.S(11), Color.FromArgb(190, 0, 0, 0));
                Theme.Text(g, Badge, f, Color.White, br, Align.Center, true, false, true);
            }
            if (!string.IsNullOrEmpty(Corner))
            {
                int w = Theme.Measure(Corner, f) + Theme.S(14);
                Rectangle cr = new Rectangle(Width - w - Theme.S(8), Height - Theme.S(30), w, Theme.S(22));
                Theme.FillRound(g, cr, Theme.S(6), Color.FromArgb(190, 0, 0, 0));
                Theme.Text(g, Corner, f, Color.White, cr, Align.Center, true, false, true);
            }
        }
    }

    public class DlItem
    {
        public YtInfo Info;
        public string Title;
        public Image Thumb;
        public YtOptions Options;
        public long Expected = -1;
        public string OutDir;
        public ItemState State = ItemState.Waiting;
        public YtProgress P = new YtProgress();
        public string Message = "";
        public List<string> Files = new List<string>();
        public bool Cancel;
    }

    public class DlQueue : ScrollList
    {
        public List<DlItem> Items = new List<DlItem>();
        int hoverAct;   // 0 none, 1 main action, 2 remove
        public event Action<DlItem> Act, Remove;
        public DlQueue() { RowHeight = 78; }
        protected override string RowText(int i) { return Items[i].Title ?? ""; }
        public override int Count { get { return Items.Count; } }
        protected override string[] DragFiles(int i) { return Items[i].Files.Count > 0 ? Items[i].Files.ToArray() : null; }

        Rectangle ActRect(Rectangle r)
        {
            int s = Theme.S(36);
            Rectangle a = new Rectangle(r.Right - s - Theme.S(14), r.Y + (r.Height - s) / 2, s, s);
            return Theme.RTL ? new Rectangle(r.X + Theme.S(14), a.Y, s, s) : a;
        }
        static bool Finished(DlItem it) { return it.State == ItemState.Done || it.State == ItemState.Failed || it.State == ItemState.Skipped; }
        Rectangle RemoveRect(Rectangle r)
        {
            Rectangle a = ActRect(r);
            int s = Theme.S(30), gap = Theme.S(8);
            return Theme.RTL ? new Rectangle(a.Right + gap, a.Y + (a.Height - s) / 2, s, s) : new Rectangle(a.X - gap - s, a.Y + (a.Height - s) / 2, s, s);
        }
        protected override void RowHover(int i, Point p, Rectangle r)
        {
            int h = ActRect(r).Contains(p) ? 1 : (Finished(Items[i]) && RemoveRect(r).Contains(p)) ? 2 : 0;
            if (h != hoverAct) { hoverAct = h; Invalidate(); }
            Cursor = h > 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void RowClick(int i, Point p, Rectangle r)
        {
            if (ActRect(r).Contains(p) && Act != null) Act(Items[i]);
            else if (Finished(Items[i]) && RemoveRect(r).Contains(p) && Remove != null) Remove(Items[i]);
        }
        protected override void RowDoubleClick(int i) { if (Items[i].State == ItemState.Done && Act != null) Act(Items[i]); }

        protected override void PaintEmpty(Graphics g)
        {
            Theme.Smooth(g);
            Rectangle c = new Rectangle(Width / 2 - Theme.S(26), Height / 2 - Theme.S(46), Theme.S(52), Theme.S(52));
            Theme.FillRound(g, c, Theme.S(26), Theme.Selected);
            Theme.Glyph(g, Ico.Download, 16f, Theme.InkFaint, c);
            Theme.Text(g, L.T("yt_empty_queue"), Theme.F(9.5f), Theme.InkFaint, new Rectangle(0, c.Bottom + Theme.S(10), Width, Theme.S(24)), Align.Center);
        }

        static Rectangle Mir(Rectangle r, Rectangle b) { return Theme.RTL ? new Rectangle(b.X + (b.Right - r.Right), r.Y, r.Width, r.Height) : r; }

        public static string Kind(YtOptions o)
        {
            switch (o.Mode)
            {
                case SaveAs.Audio: return o.AudioFormat.ToUpperInvariant();
                case SaveAs.Gif: return "GIF " + o.GifWidth + "px";
                case SaveAs.Original: return L.T("dl_original");
                default: return (o.MaxHeight > 0 ? o.MaxHeight + "p" : L.T("best")) + " MP4";
            }
        }

        protected override void DrawRow(Graphics g, int i, Rectangle b, bool hover)
        {
            DlItem it = Items[i];
            Theme.Smooth(g);
            if (hover) Theme.FillRound(g, new Rectangle(b.X + Theme.S(4), b.Y + Theme.S(3), b.Width - Theme.S(8), b.Height - Theme.S(6)), Theme.S(12), Theme.Hover);
            Rectangle th = Mir(new Rectangle(b.X + Theme.S(14), b.Y + Theme.S(11), Theme.S(100), Theme.S(56)), b);
            using (GraphicsPath clip = Theme.Round(th, Theme.S(9)))
            {
                GraphicsState st = g.Save(); g.SetClip(clip, CombineMode.Intersect);
                g.Clear(Theme.Track);
                if (it.Thumb != null)
                {
                    float sc = Math.Max(th.Width / (float)it.Thumb.Width, th.Height / (float)it.Thumb.Height);
                    float w = it.Thumb.Width * sc, h = it.Thumb.Height * sc;
                    g.DrawImage(it.Thumb, th.X + (th.Width - w) / 2, th.Y + (th.Height - h) / 2, w, h);
                }
                else Theme.Glyph(g, it.Options.Mode == SaveAs.Audio ? Ico.Music : Ico.Video, 14f, Theme.InkFaint, th);
                g.Restore(st);
            }
            int tx = Theme.S(14) + Theme.S(100) + Theme.S(16), aw = Theme.S(Finished(it) ? 104 : 66);
            Theme.Text(g, it.Title, Theme.F(10f, FontStyle.Bold), Theme.Ink, Mir(new Rectangle(b.X + tx, b.Y + Theme.S(11), b.Width - tx - aw - Theme.S(110), Theme.S(22)), b));
            // kind pill
            string kind = Kind(it.Options);
            if (it.Options.Mode == SaveAs.Original && it.Info != null && !string.IsNullOrEmpty(it.Info.DirectExt))
                kind = it.Info.DirectExt.ToUpperInvariant();   // e.g. "GIF", "JPG", "MP4"
            Font kf = Theme.F(7.75f, FontStyle.Bold);
            int kw = Theme.Measure(kind, kf) + Theme.S(14);
            Rectangle kr = Mir(new Rectangle(b.Right - aw - kw - Theme.S(6), b.Y + Theme.S(12), kw, Theme.S(20)), b);
            Theme.FillRound(g, kr, Theme.S(10), Theme.Selected);
            Theme.Text(g, kind, kf, Theme.InkSoft, kr, Align.Center, true, false, true);

            string sub; Color sc2 = Theme.InkSoft;
            YtProgress p = it.P;
            switch (it.State)
            {
                case ItemState.Working:
                    if (p.Finishing) sub = p.Stage == "gif" ? L.T("dl_making_gif") + (it.Info != null ? "  ·  " + DownloadPage.GifGuess(it.Options, it.Info.Duration) : "") : p.Stage == "convert" ? L.T("dl_converting") : L.T("yt_finishing");
                    else if (p.Done <= 0) sub = L.T("yt_starting");
                    else
                    {
                        sub = Theme.HumanSize(p.Done) + (p.Total > 0 ? " " + L.T("of") + " " + Theme.HumanSize(p.Total) : "");
                        if (p.Speed > 0) sub += "  ·  " + Theme.HumanSize((long)p.Speed) + "/s";
                        if (p.Eta > 0) sub += "  ·  " + L.T("eta", Theme.Time(p.Eta));
                    }
                    if (p.Items > 1) sub = L.T("yt_item_n", p.Item, p.Items) + "  ·  " + sub;
                    break;
                case ItemState.Done:
                    long size = 0; foreach (string f in it.Files) try { size += new FileInfo(f).Length; } catch { }
                    sub = (it.Files.Count > 1 ? L.T("n_files", it.Files.Count) : L.T("saved_as", it.Files.Count > 0 ? Path.GetFileName(it.Files[0]) : "")) + (size > 0 ? "  ·  " + Theme.HumanSize(size) : "");
                    sc2 = Theme.Success; break;
                case ItemState.Failed: sub = it.Message; sc2 = Theme.Error; break;
                case ItemState.Skipped: sub = L.T("cancelled"); break;
                default: sub = L.T("st_waiting") + (it.Expected > 0 ? "  ·  ≈ " + Theme.HumanSize(it.Expected) : ""); break;
            }
            Theme.Text(g, sub, Theme.F(8.75f), sc2, Mir(new Rectangle(b.X + tx, b.Y + Theme.S(36), b.Width - tx - aw, Theme.S(18)), b));
            if (it.State == ItemState.Working)
            {
                Rectangle pr = Mir(new Rectangle(b.X + tx, b.Y + Theme.S(58), b.Width - tx - aw, Theme.S(6)), b);
                Theme.FillRound(g, pr, pr.Height / 2f, Theme.Track);
                double pc = p.Finishing ? 1 : p.Percent;
                if (pc > 0)
                {
                    float fw = Math.Max(pr.Height, (float)(pr.Width * pc));
                    RectangleF f = Theme.RTL ? new RectangleF(pr.Right - fw, pr.Y, fw, pr.Height) : new RectangleF(pr.X, pr.Y, fw, pr.Height);
                    Theme.FillRound(g, f, pr.Height / 2f, Theme.Ink);
                }
            }
            Rectangle ar = ActRect(b);
            string glyph = it.State == ItemState.Working || it.State == ItemState.Waiting ? Ico.Close : it.State == ItemState.Done ? Ico.FolderOpen : Ico.Refresh;
            Theme.FillRound(g, ar, ar.Width / 2f, hover && hoverAct == 1 ? Theme.Pressed : Theme.Selected);
            Theme.Glyph(g, glyph, 10f, Theme.Ink, ar);
            if (Finished(it))
            {
                // remove from the list (the downloaded file stays)
                Rectangle rr = RemoveRect(b);
                bool hr = hover && hoverAct == 2;
                if (hr) Theme.FillRound(g, rr, rr.Width / 2f, Theme.ErrorBg);
                Theme.Glyph(g, Ico.Close, 8.5f, hr ? Theme.Error : Theme.InkFaint, rr);
            }
            if (i < Items.Count - 1) using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, b.X + Theme.S(16), b.Bottom - 1, b.Right - Theme.S(16), b.Bottom - 1);
        }
    }

    public class DownloadPage : Page
    {
        Rectangle top, queueCard, infoR, queueTitleR, noteR, statusR, sitesR, bottomR;
        TextField url;
        Btn btnPaste, btnGet, btnDownload, btnSetup;
        Segments saveAs, qualityV, qualityA, gifSize, gifLen;
        Toggle playlist;
        Notice notice;
        Progress busy;
        DlQueue queue;
        ThumbView thumbView;
        YtInfo info; Image thumb;
        bool fetching, settingUp, worker;
        string statusText = ""; Theme.LiveColor statusColor;
        List<int> heights = new List<int> { 0 };
        List<SaveAs> modes = new List<SaveAs>();
        int fetchToken;
        string lastFetched = "";

        static readonly string[] Sites = { "YouTube", "Pinterest", "GIPHY", "Tenor", "X / Twitter", "Reddit", "Imgur", "TikTok", "Instagram", "Vimeo" };

        public DownloadPage() : base("dl_title", "dl_sub")
        {
            url = new TextField(() => L.T("dl_paste_hint"), Ico.Link) { ForceLtr = true, Clearable = true };
            url.Cleared += ClearLink;
            url.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Fetch(); } };
            url.Box.TextChanged += (s, e) => { string t = url.Box.Text.Trim(); if (t.Length == 0) { ClearLink(); return; } if (t.Length > 12 && t != lastFetched && !fetching && t.StartsWith("http") && YtDlp.LooksLikeUrl(t)) Fetch(); };
            btnPaste = new Btn(() => L.T("paste"), Ico.Paste, BtnKind.Secondary);
            btnPaste.Click += (s, e) => { try { if (Clipboard.ContainsText()) { url.Box.Text = Clipboard.GetText().Trim(); Fetch(); } } catch { } };
            btnGet = new Btn(() => L.T("fetch"), Ico.Search, BtnKind.Primary);
            btnGet.Click += (s, e) => Fetch();
            btnDownload = new Btn(() => L.T("yt_download"), Ico.Download, BtnKind.Primary);
            btnDownload.Click += (s, e) => Enqueue();
            btnSetup = new Btn(() => L.T("yt_setup_btn"), Ico.Download, BtnKind.Primary);
            btnSetup.Click += (s, e) => Setup();

            saveAs = new Segments(() => "");
            saveAs.SelectedChanged += (s, e) => { Settings.Set("DlMode", ((int)CurrentMode).ToString()); UpdateView(); };
            qualityV = new Segments(() => L.T("best"));
            qualityV.SelectedChanged += (s, e) => UpdateSize();
            qualityA = new Segments(() => "MP3", () => "M4A", () => "OPUS", () => "WAV");
            gifSize = new Segments(() => L.T("gif_small"), () => L.T("gif_medium"), () => L.T("gif_large"));
            gifSize.SelectedChanged += (s, e) => { Settings.Set("GifSize", gifSize.SelectedIndex.ToString()); UpdateSize(); Invalidate(); };
            gifLen = new Segments(() => L.T("gif_5s"), () => L.T("gif_10s"), () => L.T("gif_30s"), () => L.T("gif_full"));
            gifLen.FontSize = 8.75f;
            gifLen.SelectedChanged += (s, e) => { Settings.Set("GifLen", gifLen.SelectedIndex.ToString()); UpdateSize(); Invalidate(); };
            int v;
            int.TryParse(Settings.Get("GifSize", "1"), out v); gifSize.SetSilently(Math.Max(0, Math.Min(2, v)));
            int.TryParse(Settings.Get("GifLen", "1"), out v); gifLen.SetSilently(Math.Max(0, Math.Min(3, v)));
            int.TryParse(Settings.Get("YtAudio", "0"), out v); qualityA.SetSilently(Math.Max(0, Math.Min(3, v)));
            playlist = new Toggle();
            notice = new Notice(() => L.T("dl_hint"));
            busy = new Progress { Visible = false };
            queue = new DlQueue();
            queue.Act += OnAct;
            queue.Remove += it => { lock (queue.Items) queue.Items.Remove(it); queue.ClampScroll(); queue.Invalidate(); UpdateClear(); };
            btnClear = new Btn(() => L.T("dl_clear_done"), Ico.Delete, BtnKind.Ghost);
            btnClear.FontSize = 9f;
            btnClear.Visible = false;
            btnClear.Click += (s, e) =>
            {
                Func<DlItem, bool> finished = x => x.State == ItemState.Done || x.State == ItemState.Failed || x.State == ItemState.Skipped;
                List<Rectangle> gone;
                lock (queue.Items) gone = queue.VisibleRows(i => i < queue.Items.Count && finished(queue.Items[i]));
                Fx.SweepAway(queue, gone, () =>
                {
                    lock (queue.Items) queue.Items.RemoveAll(x => finished(x));
                    queue.ClampScroll(); queue.Invalidate(); UpdateClear();
                });
            };
            thumbView = new ThumbView { Visible = false };
            Controls.AddRange(new Control[] { url, btnPaste, btnGet, btnDownload, btnSetup, saveAs, qualityV, qualityA, gifSize, gifLen, playlist, notice, busy, queue, thumbView, btnClear });
            UpdateView();
        }

        public override bool Busy { get { return worker; } }

        SaveAs CurrentMode { get { return modes.Count == 0 ? SaveAs.Video : modes[Math.Min(saveAs.SelectedIndex, modes.Count - 1)]; } }

        void UpdateView()
        {
            bool hasInfo = info != null;
            btnPaste.Enabled = btnGet.Enabled = !fetching;
            // once a link is found, Download is the one main button; looking up again is a plain one
            BtnKind gk = hasInfo ? BtnKind.Secondary : BtnKind.Primary;
            if (btnGet.Kind != gk) { btnGet.Kind = gk; btnGet.Invalidate(); }
            btnSetup.Visible = !YtDlp.Ready && !hasInfo;
            btnSetup.Enabled = !settingUp;
            notice.Visible = !hasInfo && !fetching;
            notice.Kind = 0; notice.Glyph = Ico.Info; notice.Invalidate();
            thumbView.Visible = hasInfo;
            SaveAs m = CurrentMode;
            saveAs.Visible = hasInfo && modes.Count > 1;
            qualityV.Visible = hasInfo && m == SaveAs.Video && !info.IsDirect && info.Heights.Count > 0;
            qualityA.Visible = hasInfo && m == SaveAs.Audio;
            gifSize.Visible = hasInfo && m == SaveAs.Gif && info.Kind == MediaKind.Video;
            gifLen.Visible = hasInfo && m == SaveAs.Gif && info.Kind == MediaKind.Video;
            btnDownload.Visible = hasInfo;
            playlist.Visible = hasInfo && YtDlp.HasPlaylist(info.Url) && !info.IsPlaylist && m != SaveAs.Gif;
            busy.Visible = fetching || settingUp;
            DoLayout();
            UpdateSize();
        }

        public override void Restyle() { url.Restyle(); UpdateView(); }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, ip = Theme.S(22);
            cards.Clear();
            // compact when idle: nothing below the "works with" row
            bool idle = info == null && !btnSetup.Visible && statusText.Length == 0;
            int topH = Theme.S(idle ? 202 : 270);
            top = new Rectangle(p, Top0, W - p * 2, topH);
            queueCard = new Rectangle(p, top.Bottom + Theme.S(18), W - p * 2, Math.Max(Theme.S(120), H - top.Bottom - Theme.S(18) - p - Theme.S(22)));
            cards.Add(top); cards.Add(queueCard);
            noteR = new Rectangle(p, queueCard.Bottom + Theme.S(4), W - p * 2, Theme.S(18));

            int x = top.X + ip, w = top.Width - ip * 2, y = top.Y + ip;
            int gw = Math.Max(Theme.S(130), btnGet.PreferredWidth), pw = Math.Max(Theme.S(100), btnPaste.PreferredWidth);
            Place(url, new Rectangle(x, y, w - gw - pw - Theme.S(20), Theme.S(46)));
            Place(btnPaste, new Rectangle(x + w - gw - pw - Theme.S(10), y, pw, Theme.S(46)));
            Place(btnGet, new Rectangle(x + w - gw, y, gw, Theme.S(46)));
            y += Theme.S(46) + Theme.S(18);
            infoR = new Rectangle(x, y, w, top.Bottom - ip - y);
            Place(busy, new Rectangle(x, y - Theme.S(10), w, Theme.S(4)));
            Place(notice, new Rectangle(x, y, w, Theme.S(48)));
            sitesR = new Rectangle(x, y + Theme.S(60), w, Theme.S(30));
            int sw = Math.Max(Theme.S(250), btnSetup.PreferredWidth);
            Place(btnSetup, new Rectangle(x, y + Theme.S(104), sw, Theme.S(40)));
            statusR = new Rectangle(x + (btnSetup.Wanted ? sw + Theme.S(14) : 0), y + Theme.S(104), w - (btnSetup.Wanted ? sw + Theme.S(14) : 0), Theme.S(40));

            int thW = Theme.S(288), thH = Theme.S(162);
            Place(thumbView, new Rectangle(x, y, thW, thH));
            int cx = x + thW + Theme.S(22), cw = w - thW - Theme.S(22);
            int sy = y + Theme.S(70);
            int kw = saveAs.Wanted ? Math.Min(Theme.S(300), cw / 2 - Theme.S(5)) : 0;
            Place(saveAs, new Rectangle(cx, sy, kw, Theme.S(34)));
            int qx = cx + (kw > 0 ? kw + Theme.S(10) : 0), qw = cw - (kw > 0 ? kw + Theme.S(10) : 0);
            Place(qualityV, new Rectangle(qx, sy, qw, Theme.S(34)));
            Place(qualityA, new Rectangle(qx, sy, qw, Theme.S(34)));
            Place(gifSize, new Rectangle(qx, sy, qw, Theme.S(34)));
            int dw = Math.Max(Theme.S(180), btnDownload.PreferredWidth);
            Place(btnDownload, new Rectangle(cx + cw - dw, y + thH - Theme.S(42), dw, Theme.S(42)));
            bottomR = new Rectangle(cx, y + thH - Theme.S(42), cw - dw - Theme.S(12), Theme.S(42));
            Place(gifLen, new Rectangle(cx, y + thH - Theme.S(32), Math.Min(Theme.S(320), cw - dw - Theme.S(14)), Theme.S(32)));
            Place(playlist, new Rectangle(cx, y + thH - Theme.S(80), Theme.S(50), Theme.S(30)));

            queueTitleR = new Rectangle(queueCard.X + ip, queueCard.Y + Theme.S(14), queueCard.Width - ip * 2, Theme.S(28));
            int cw2 = Math.Max(Theme.S(150), btnClear.PreferredWidth + Theme.S(10));
            Place(btnClear, new Rectangle(queueCard.Right - ip - cw2 + Theme.S(8), queueCard.Y + Theme.S(10), cw2, Theme.S(34)));
            Place(queue, new Rectangle(queueCard.X + Theme.S(6), queueCard.Y + Theme.S(50), queueCard.Width - Theme.S(12), queueCard.Height - Theme.S(56)));
            Invalidate();
        }

        string sizeText = "", gifSizeText = "";

        /// A rough size for the GIF that will be made (GIFs are big: about a tenth of a byte per pixel per frame).
        long GifEstimate()
        {
            if (info == null) return -1;
            double dur = info.Duration;
            int secs = new[] { 5, 10, 30, 0 }[Math.Max(0, Math.Min(3, gifLen.SelectedIndex))];
            if (secs > 0 && (dur <= 0 || secs < dur)) dur = secs;
            if (dur <= 0) return -1;
            int w = new[] { 320, 480, 720 }[Math.Max(0, Math.Min(2, gifSize.SelectedIndex))];
            float aspect = thumbView.Aspect > 0.2f ? thumbView.Aspect : 16f / 9f;
            double h = w / aspect;
            int fps = w >= 720 ? 15 : 12;
            return (long)(w * h * fps * dur * 0.085);
        }
        /// Shown on a GIF in the list while it's being made.
        public static string GifGuess(YtOptions o, double duration)
        {
            double dur = duration;
            if (o.GifSeconds > 0 && (dur <= 0 || o.GifSeconds < dur)) dur = o.GifSeconds;
            if (dur <= 0) return "";
            double h = o.GifWidth * 9.0 / 16;
            return "≈ " + Theme.HumanSize((long)(o.GifWidth * h * (o.GifWidth >= 720 ? 15 : 12) * dur * 0.085));
        }

        void UpdateSize()
        {
            if (info == null) { sizeText = ""; return; }
            long est = YtDlp.Estimate(info, Options());
            SaveAs m = CurrentMode;
            if (m == SaveAs.Gif && info.Kind == MediaKind.Video)
            {
                // how big the GIF itself will be (roughly): its size, length and frame rate decide it
                long gif = GifEstimate();
                gifSizeText = gif > 0 ? "≈ " + Theme.HumanSize(gif) : L.T("dl_size_unknown");
                sizeText = est > 0 ? L.T("dl_source_size", Theme.HumanSize(est)) : "";
            }
            else sizeText = est > 0 ? "≈ " + Theme.HumanSize(est) : L.T("dl_size_unknown");
            if (m == SaveAs.Video && !info.IsDirect && info.Heights.Count > 0)
            {
                int h = heights[Math.Min(qualityV.SelectedIndex, heights.Count - 1)];
                if (h == 0) h = info.Heights[info.Heights.Count - 1];
                sizeText += "  ·  " + h + "p";
            }
            Invalidate(M(bottomR));
        }

        protected override void PaintContent(Graphics g)
        {
            if (info != null)
            {
                int thW = Theme.S(288);
                int cx = infoR.X + thW + Theme.S(22), cw = infoR.Width - thW - Theme.S(22);
                Label(g, info.Title, Theme.F(12f, FontStyle.Bold), Theme.Ink, new Rectangle(cx, infoR.Y - Theme.S(2), cw, Theme.S(46)), Align.Start, false, true);
                string meta;
                if (info.IsPlaylist) meta = L.T("yt_playlist", info.Count);
                else
                {
                    List<string> parts = new List<string>();
                    if (info.Uploader.Length > 0) parts.Add(info.Uploader);
                    if (info.Duration > 0) parts.Add(Theme.Time(info.Duration));
                    if (info.Views > 0) parts.Add(L.T("yt_views", info.Views.ToString("N0")));
                    if (info.IsDirect) parts.Add(L.T(info.Kind == MediaKind.Gif ? "kind_gif" : info.Kind == MediaKind.Image ? "kind_image" : "kind_video"));
                    meta = string.Join("  ·  ", parts.ToArray());
                }
                Label(g, meta, Theme.F(9f), Theme.InkSoft, new Rectangle(cx, infoR.Y + Theme.S(44), cw, Theme.S(20)));
                if (playlist.Visible) Label(g, L.T("yt_whole_pl"), Theme.F(9f), Theme.Ink, new Rectangle(cx + Theme.S(58), infoR.Y + Theme.S(162) - Theme.S(80), cw - Theme.S(60), Theme.S(30)));
                if (!gifLen.Visible)
                {
                    Rectangle b = bottomR;
                    Label(g, sizeText, Theme.F(12f, FontStyle.Bold), Theme.Ink, new Rectangle(b.X, b.Y - Theme.S(2), b.Width, Theme.S(26)));
                    string dest = L.T("saves_to", Settings.OutputIsDefault ? L.T("downloads") : Settings.OutputDir);
                    Label(g, dest, Theme.F(8.5f), Theme.InkFaint, new Rectangle(b.X, b.Y + Theme.S(22), b.Width, Theme.S(20)));
                }
                else
                {
                    // GIF: its length on the left, the GIF's own (estimated) size in bold, then what's downloaded for it
                    Rectangle lr = new Rectangle(bottomR.X, infoR.Y + Theme.S(162) - Theme.S(54), bottomR.Width + Theme.S(200), Theme.S(20));
                    Font lf = Theme.F(8.5f), bf = Theme.F(10f, FontStyle.Bold);
                    string head = L.T("gif_length") + "   ·   " + L.T("gif_label");
                    int hw = Theme.Measure(head, lf) + Theme.S(8);
                    Label(g, head, lf, Theme.InkFaint, new Rectangle(lr.X, lr.Y, hw, lr.Height));
                    int sw2 = Theme.Measure(gifSizeText, bf) + Theme.S(10);
                    Label(g, gifSizeText, bf, Theme.Ink, new Rectangle(lr.X + hw, lr.Y, sw2, lr.Height));
                    Label(g, sizeText, lf, Theme.InkFaint, new Rectangle(lr.X + hw + sw2, lr.Y, Math.Max(10, lr.Width - hw - sw2), lr.Height));
                }
            }
            else if (!fetching)
            {
                // supported sites as small pills
                Font f = Theme.F(8.5f, FontStyle.Bold);
                int x = sitesR.X;
                Label(g, L.T("dl_works_with"), Theme.F(8.5f), Theme.InkFaint, new Rectangle(x, sitesR.Y, Theme.S(120), sitesR.Height));
                x += Theme.Measure(L.T("dl_works_with"), Theme.F(8.5f)) + Theme.S(10);
                foreach (string s in Sites)
                {
                    int w = Theme.Measure(s, f) + Theme.S(18);
                    if (x + w > sitesR.Right) break;
                    Rectangle r = M(new Rectangle(x, sitesR.Y + Theme.S(3), w, Theme.S(24)));
                    Theme.FillRound(g, r, Theme.S(12), Theme.Selected);
                    Theme.Text(g, s, f, Theme.InkSoft, r, Align.Center, true, false, true);
                    x += w + Theme.S(6);
                }
            }
            if (statusText.Length > 0)
            {
                Rectangle sr = info == null ? statusR : new Rectangle(infoR.X, infoR.Bottom - Theme.S(20), infoR.Width, Theme.S(20));
                if (fetching) sr = new Rectangle(infoR.X, infoR.Y + Theme.S(4), infoR.Width, Theme.S(40));
                Label(g, statusText, Theme.F(9.25f), statusColor, sr, Align.Start, true, true);
            }
            Label(g, L.T("yt_queue"), Theme.F(12.5f, FontStyle.Bold), Theme.Ink, queueTitleR);
            Label(g, L.T("yt_note"), Theme.F(8.25f), Theme.InkFaint, noteR, Align.Center);
        }

        void SetStatus(string t, Color c) { t = t ?? ""; bool relayout = (statusText.Length == 0) != (t.Length == 0); statusText = t; statusColor = c; if (relayout) DoLayout(); Invalidate(); }
        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        // ------------------------------------------------------------------ engine setup
        void Setup()
        {
            if (settingUp) return;
            settingUp = true;
            busy.SetIndeterminate(false); busy.Value = 0;
            UpdateView();
            Thread t = new Thread(() =>
            {
                string err = null;
                try { YtDlp.Install((label, frac) => Ui(() => { busy.Value = frac; SetStatus(label, Theme.InkSoft); }), () => IsDisposed); }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    settingUp = false;
                    if (err == null && YtDlp.Ready) { SetStatus("", Theme.InkSoft); ShowToast(L.T("yt_ready"), true, null, null); if (url.Box.Text.Trim().Length > 0) Fetch(); }
                    else SetStatus(L.T("yt_setup_failed", err ?? ""), Theme.Error);
                    UpdateView();
                });
            });
            t.IsBackground = true; t.Start();
        }

        /// A link from a site videos are downloaded from (for the "you copied a link" offer).
        public static bool IsVideoLink(string u)
        {
            if (Resolver.IsDataImage(u)) return true;
            if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !YtDlp.LooksLikeUrl(u)) return false;
            // an image result's link (Google, Bing…): what it points at counts
            string inner = Resolver.Unwrap(u);
            if (inner != u && (Resolver.IsDirectMedia(inner) || Resolver.IsImageResult(u))) return true;
            return System.Text.RegularExpressions.Regex.IsMatch(u, @"^https?://([\w-]+\.)*(youtube\.com|youtu\.be|tiktok\.com|instagram\.com|twitter\.com|x\.com|reddit\.com|redd\.it|pinterest\.[a-z.]+|pin\.it|vimeo\.com|giphy\.com|tenor\.com|klipy\.com|imgur\.com|facebook\.com|fb\.watch|twitch\.tv|dailymotion\.com|soundcloud\.com|streamable\.com|bilibili\.com)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                || Resolver.IsDirectMedia(u);
        }

        /// Puts a link in the box and gets its details (from the "you copied a link" offer).
        public void FetchLink(string u)
        {
            url.Box.Text = u.Trim();
            Fetch();
        }

        /// The link was removed (× or deleted): the video found for it goes too.
        void ClearLink()
        {
            if (info == null && !fetching && statusText.Length == 0 && lastFetched.Length == 0) return;
            fetchToken++;                       // a fetch still on its way is dropped
            fetching = false; busy.SetIndeterminate(false);
            info = null; thumb = null; thumbView.Image = null;
            lastFetched = "";
            SetStatus("", Theme.InkSoft);
            UpdateView();
        }

        // ------------------------------------------------------------------ fetch details
        void Fetch()
        {
            string u = url.Box.Text.Trim();
            if (fetching) return;
            bool data = Resolver.IsDataImage(u);
            if (!data && !YtDlp.LooksLikeUrl(u)) { SetStatus(L.T("yt_invalid"), Theme.Error); info = null; UpdateView(); return; }
            if (!data && !u.StartsWith("http")) u = "https://" + u;
            lastFetched = url.Box.Text.Trim();
            bool needsEngine = !YtDlp.Ready && Resolver.NeedsEngine(u);
            if (needsEngine) { SetStatus(L.T("dl_need_engine"), Theme.Warn); info = null; UpdateView(); return; }
            fetching = true; info = null;
            busy.SetIndeterminate(true);
            SetStatus(L.T("yt_fetching"), Theme.InkSoft);
            UpdateView();
            int token = ++fetchToken;
            Thread t = new Thread(() =>
            {
                YtInfo inf = null; string err = null; Image im = null;
                try
                {
                    inf = YtDlp.Fetch(u, () => token != fetchToken);
                    if (inf.Thumbnail.Length > 0) im = LoadImage(inf.Thumbnail);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    if (token != fetchToken) return;
                    fetching = false; busy.SetIndeterminate(false);
                    if (err != null) { SetStatus(L.T("yt_err", err), Theme.Error); UpdateView(); return; }
                    info = inf;
                    thumb = im;
                    thumbView.Image = im;
                    thumbView.Badge = inf.Site;
                    thumbView.Corner = inf.Duration > 0 ? Theme.Time(inf.Duration) : (inf.Kind == MediaKind.Gif ? "GIF" : "");
                    SetStatus("", Theme.InkSoft);
                    BuildOptions();
                    playlist.SetSilently(false);
                    UpdateView();
                });
            });
            t.IsBackground = true; t.Start();
        }

        void BuildOptions()
        {
            modes.Clear();
            List<Func<string>> labels = new List<Func<string>>();
            List<string> glyphs = new List<string>();
            Action<SaveAs, string, string> add = (m, key, gl) => { modes.Add(m); labels.Add(() => L.T(key)); glyphs.Add(gl); };
            switch (info.Kind)
            {
                case MediaKind.Gif: add(SaveAs.Original, "dl_as_gif", Ico.Photo); add(SaveAs.Video, "yt_video", Ico.Video); break;
                case MediaKind.Image: add(SaveAs.Original, "dl_as_image", Ico.Photo); break;
                default:
                    add(SaveAs.Video, "yt_video", Ico.Video);
                    add(SaveAs.Audio, "yt_audio", Ico.Music);
                    if (!info.IsPlaylist) add(SaveAs.Gif, "dl_as_gif", Ico.Photo);
                    break;
            }
            saveAs.Items = labels.ToArray();
            saveAs.Glyphs = glyphs.ToArray();
            int pref; int.TryParse(Settings.Get("DlMode", "0"), out pref);
            int idx = modes.IndexOf((SaveAs)pref);
            int gi = modes.IndexOf(SaveAs.Gif);
            if (info.PreferGif && gi >= 0) saveAs.SetSilently(gi);
            else saveAs.SetSilently(info.Kind == MediaKind.Video && idx >= 0 ? idx : 0);

            heights = new List<int> { 0 };
            List<Func<string>> q = new List<Func<string>> { () => L.T("best") };
            int max = info.Heights.Count > 0 ? info.Heights[info.Heights.Count - 1] : 0;
            foreach (int h in new[] { 2160, 1440, 1080, 720, 480, 360 })
            {
                if (h >= max || q.Count >= 5) continue;
                int hh = h; heights.Add(h); q.Add(() => hh + "p");
            }
            qualityV.Items = q.ToArray();
            int ph; int.TryParse(Settings.Get("YtHeight", "0"), out ph);
            int hi = heights.IndexOf(ph);
            qualityV.SetSilently(hi >= 0 ? hi : 0);
            if (info.Duration > 0 && (info.Duration <= 10 || (info.PreferGif && info.Duration <= 30)) && gifLen.SelectedIndex < 3) gifLen.SetSilently(3);
        }

        YtOptions Options()
        {
            return new YtOptions
            {
                Mode = CurrentMode,
                MaxHeight = heights[Math.Min(qualityV.SelectedIndex, heights.Count - 1)],
                AudioFormat = new[] { "mp3", "m4a", "opus", "wav" }[qualityA.SelectedIndex],
                GifWidth = new[] { 320, 480, 720 }[gifSize.SelectedIndex],
                GifSeconds = new[] { 5, 10, 30, 0 }[gifLen.SelectedIndex],
                Playlist = info != null && (info.IsPlaylist || (playlist.Visible && playlist.Checked))
            };
        }

        static Image LoadImage(string u)
        {
            try
            {
                if (u.Contains("/vi_webp/")) u = u.Replace("/vi_webp/", "/vi/").Replace(".webp", ".jpg");
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                    byte[] b = wc.DownloadData(u);
                    if (b.Length > 25 << 20) return null;
                    try { return Image.FromStream(new MemoryStream(b)); }
                    catch
                    {
                        // WEBP/AVIF thumbnails: let ImageMagick decode them
                        string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".webp");
                        File.WriteAllBytes(tmp, b);
                        try { return ViewerPage.Decode(tmp, 800); } finally { try { File.Delete(tmp); } catch { } }
                    }
                }
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ downloads
        void Enqueue()
        {
            if (info == null) return;
            string dir = SaveAsk.Folder(FindForm());
            if (dir == null) return;
            YtOptions o = Options();
            Settings.Set("YtHeight", o.MaxHeight.ToString());
            Settings.Set("YtAudio", qualityA.SelectedIndex.ToString());
            Image small = null;
            if (thumb != null) try { small = new Bitmap(thumb, new Size(Math.Max(1, thumb.Width * 112 / Math.Max(1, thumb.Height)), 112)); } catch { }
            DlItem it = new DlItem { Info = info, Title = info.Title, Thumb = small, Options = o, Expected = YtDlp.Estimate(info, o), OutDir = dir };
            lock (queue.Items) queue.Items.Insert(0, it);
            queue.Invalidate();
            ShowToast(L.T("yt_added"), true, null, null);
            if (!worker) StartWorker();
        }

        void StartWorker()
        {
            worker = true;
            Thread t = new Thread(() =>
            {
                while (true)
                {
                    DlItem it = null;
                    lock (queue.Items) { for (int i = queue.Items.Count - 1; i >= 0; i--) if (queue.Items[i].State == ItemState.Waiting) { it = queue.Items[i]; break; } }
                    if (it == null) break;
                    it.State = ItemState.Working;
                    Ui(() => { queue.Invalidate(); UpdateClear(); });
                    string outDir = it.OutDir ?? Settings.OutputDir;
                    DlItem me = it;
                    ActJob act = Activity.Begin(this, L.T("act_k_download"), it.Title, () => me.Cancel = true);
                    try
                    {
                        Directory.CreateDirectory(outDir);
                        DateTime last = DateTime.MinValue;
                        DlItem cur = it;
                        it.Files = YtDlp.Download(it.Info, it.Options, outDir, it.Expected, p =>
                        {
                            cur.P = p;
                            if ((DateTime.Now - last).TotalMilliseconds < 150) return;
                            last = DateTime.Now;
                            Activity.Update(act, p.Percent, (int)(p.Percent * 100) + "%" + (p.Speed > 0 ? "  ·  " + Theme.HumanSize((long)p.Speed) + "/s" : "") + (p.Items > 1 ? "  ·  " + p.Item + "/" + p.Items : ""));
                            Ui(() => { queue.Invalidate(); UpdateClear(); });
                        }, () => cur.Cancel);
                        it.State = ItemState.Done;
                        DlItem done = it;
                        long bytes = 0; foreach (string f in it.Files) try { bytes += new FileInfo(f).Length; } catch { }
                        Activity.Done(act, L.T("act_saved_size", Theme.HumanSize(bytes)), it.Files.Count == 1 ? it.Files[0] : it.Files.Count > 1 ? Path.GetDirectoryName(it.Files[0]) : outDir);
                        Ui(() =>
                        {
                            ShowToast(L.T("yt_saved", done.Title), true, () => L.T("show_file"), () => Reveal(done));
                            if (Settings.OpenAfter) Reveal(done);
                        });
                    }
                    catch (OperationCanceledException) { it.State = ItemState.Skipped; Activity.Cancelled(act); }
                    catch (Exception ex) { it.State = it.Cancel ? ItemState.Skipped : ItemState.Failed; it.Message = ex.Message; if (it.Cancel) Activity.Cancelled(act); else Activity.Fail(act, ex.Message); }
                    Ui(() => { queue.Invalidate(); UpdateClear(); });
                }
                worker = false;
            });
            t.IsBackground = true; t.Start();
        }

        void Reveal(DlItem it)
        {
            try
            {
                if (it.Files.Count == 1 && File.Exists(it.Files[0])) Process.Start("explorer.exe", "/select,\"" + it.Files[0] + "\"");
                else if (it.Files.Count > 1) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(it.Files[0]) + "\"");
                else Process.Start("explorer.exe", "\"" + (it.OutDir ?? Settings.OutputDir) + "\"");
            }
            catch { }
        }

        Btn btnClear;
        void UpdateClear()
        {
            bool any = false;
            lock (queue.Items) foreach (DlItem x in queue.Items) if (x.State == ItemState.Done || x.State == ItemState.Failed || x.State == ItemState.Skipped) any = true;
            if (btnClear.Visible != any) btnClear.Visible = any;
        }

        void OnAct(DlItem it)
        {
            if (it.State == ItemState.Working || it.State == ItemState.Waiting)
            {
                it.Cancel = true;
                if (it.State == ItemState.Waiting) { it.State = ItemState.Skipped; queue.Invalidate(); UpdateClear(); }
            }
            else if (it.State == ItemState.Done) Reveal(it);
            else
            {
                it.Cancel = false; it.State = ItemState.Waiting; it.Message = ""; it.P = new YtProgress();
                queue.Invalidate();
                if (!worker) StartWorker();
            }
        }

        public override void AcceptFiles(string[] paths) { }

        /// Downloads waiting or going on (the count on the side list).
        public int ActiveCount { get { int n = 0; foreach (DlItem it in queue.Items) if (it.State == ItemState.Working || it.State == ItemState.Waiting) n++; return n; } }

        /// Puts the cursor in the link box (and pastes a copied link, if there is one).
        public void FocusLink()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string t = Clipboard.GetText().Trim();
                    if (t.StartsWith("http") && YtDlp.LooksLikeUrl(t) && t != url.Box.Text.Trim()) url.Box.Text = t;
                }
            }
            catch { }
            url.Box.Focus(); url.Box.SelectAll();
        }
    }
}
