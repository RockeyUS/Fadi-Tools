using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    // ====================================================================== Crop (videos)

    /// Donut Wheel → Crop on a video: a frame from the video with the crop frame on top (the same handles, ratios and
    /// size boxes as cropping a picture), a slider to look at any moment, Play (or Space) to watch it with the frame
    /// on top, then Crop makes "video Cropped.mp4" next to the original. The sound is kept as it is, or made quieter or louder with the volume slider (which also sets how loud it plays here).
    public class VideoCropWindow : MiniWindow
    {
        readonly string file;
        readonly CropCanvas canvas = new CropCanvas();
        readonly Slider time = new Slider();
        readonly Slider volume = new Slider();
        readonly Segments ratio;
        readonly TextField wBox, hBox;
        readonly Btn bPlay, bReset, bGo, bCancel, bShow;
        readonly Progress bar = new Progress { Visible = false };
        readonly Control activitySource;
        readonly ClipPlayer player = new ClipPlayer();
        MediaInfo info;
        Size full;                     // the video's picture as it's shown (turned the right way)
        Rectangle volLbl, hintR, ratioLbl, sizeLbl, xLbl, pxLbl, statusR;
        string status; Color statusColor;
        string result;
        bool busy, cancel, done, typing;
        int frameAsk; double frameAt = -1;
        static readonly float[] Ratios = { 0, -1, 1f, 16f / 9, 9f / 16, 4f / 3, 3f / 4 };   // 0 = free, -1 = the video's own

        public static void Open(string file, Point at, Control activitySource)
        {
            VideoCropWindow w = new VideoCropWindow(file, activitySource);
            w.ShowNear(at);
            w.LoadVideo();
        }

        VideoCropWindow(string file, Control activitySource) : base(() => L.T("wh_crop"), Ico.Crop)
        {
            this.file = file; this.activitySource = activitySource;
            canvas.Loading = true;
            time.Min = 0; time.Max = 1; time.Value = 0; time.Enabled = false;
            time.Format = v => Clock(v);
            time.ValueChanged += (s, e) => { if (player.Playing) Pause(); AskFrame(time.Value); };
            // how loud it plays here, and how loud the cropped copy will be (100% = as it is)
            volume.Min = 0; volume.Max = 200; volume.Value = 100; volume.Enabled = false;
            volume.Format = v => Math.Round(v) <= 0 ? L.T("vc_muted") : ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%";
            volume.ValueChanged += (s, e) => player.Volume = (float)(Vol());
            ratio = new Segments(() => L.T("crop_free"), () => L.T("crop_orig"), () => "1:1", () => "16:9", () => "9:16", () => "4:3", () => "3:4") { Surface = Surface.Card, FontSize = 8.75f };
            ratio.SelectedChanged += (s, e) => canvas.SetRatio(RatioNow());
            wBox = new TextField(() => "", null) { Surface = Surface.Card, ForceLtr = true };
            hBox = new TextField(() => "", null) { Surface = Surface.Card, ForceLtr = true };
            foreach (TextField tf in new[] { wBox, hBox })
            {
                TextField me = tf;
                me.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TypedSize(); } };
                me.Box.Leave += (s, e) => TypedSize();
            }
            bPlay = new Btn(() => L.T(player.Playing ? "tr_pause" : "vc_play"), Ico.Play, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f, Enabled = false, QuickClicks = true };
            bPlay.Click += (s, e) => PlayPause();
            player.Tick += t => Ui(() => { if (!player.Playing) return; time.SetValue((float)t, false); });
            player.Frame += (b, t) => Ui(() => { if (IsDisposed || !player.Playing) { b.Dispose(); return; } canvas.SetShown(b); });
            player.Finished += () => Ui(PlayState);
            bReset = new Btn(() => L.T("crop_reset"), Ico.Refresh, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 9f };
            bReset.Click += (s, e) => { ratio.SelectedIndex = 0; canvas.ResetCrop(); };
            bGo = new Btn(() => L.T(done ? "wn_ok" : "vc_go"), Ico.Crop, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bGo.Click += (s, e) => { if (done) Dismiss(); else Go(); };
            bCancel = new Btn(() => L.T(busy ? "cancel" : "close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { if (busy) cancel = true; else Dismiss(); };
            bShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, Visible = false, FontSize = 9f };
            bShow.Click += (s, e) => { if (result != null) ShowInFolder(result); };
            canvas.Changed += ShowSize;
            Controls.AddRange(new Control[] { canvas, bPlay, time, volume, ratio, wBox, hBox, bar, bReset, bGo, bCancel, bShow });
            ClientSize = new Size(Theme.S(600), Theme.S(700));
        }

        float RatioNow()
        {
            float r = Ratios[Math.Max(0, ratio.SelectedIndex)];
            if (r < 0) r = full.Height > 0 ? full.Width / (float)full.Height : 0;
            return r;
        }

        /// The volume chosen: 1 = as it is, 0 = silent, 2 = twice as loud (whole percents).
        double Vol() { return Math.Round(volume.Value) / 100.0; }

        static string Clock(double s)
        {
            s = Math.Max(0, s);
            int m = (int)(s / 60); double r = s - m * 60;
            return m + ":" + r.ToString("00.0", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ loading

        /// Reads the video's length and size and a frame from it, then fits the window to the picture's shape.
        void LoadVideo()
        {
            status = L.T("loading"); statusColor = Theme.InkSoft;
            Thread th = new Thread(() =>
            {
                MediaInfo mi = null; Image first = null; Size sz = Size.Empty; string err = null; double at = 0;
                try
                {
                    if (Tools.FFmpeg == null) throw new UserError(L.T("err_need_ff"));
                    mi = Media.Probe(file);
                    if (mi == null || !mi.HasVideo) throw new UserError(L.T("vc_no_video"));
                    at = mi.Duration > 0 ? Math.Min(1, mi.Duration / 2) : 0;
                    first = Media.Frame(file, at, Math.Max(90, Math.Min(mi.Height > 0 ? Math.Max(mi.Width, mi.Height) : 720, Theme.S(720))));
                    if (first == null) throw new UserError(L.T("vc_no_video"));
                    sz = ShownSize(file, mi, first);
                }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    if (err != null) { status = err; statusColor = Theme.Error; canvas.Loading = false; canvas.Invalidate(); Invalidate(); if (first != null) first.Dispose(); return; }
                    info = mi; full = sz;
                    canvas.SetImage(new Bitmap(first), sz);
                    first.Dispose();
                    frameAt = at;
                    if (mi.Duration > 0) { time.Max = (float)mi.Duration; time.SetValue((float)at, false); time.Enabled = true; bPlay.Enabled = true; }
                    volume.Enabled = mi.HasAudio; volume.Visible = mi.HasAudio;
                    status = null;
                    bGo.Enabled = true;
                    FitToVideo();
                    ShowSize();
                    Invalidate();
                });
            });
            th.IsBackground = true; th.Start();
        }

        /// The size of the picture as it plays: the size in the file, turned when the video is stored sideways
        /// (phone videos), which the frame's own shape tells.
        static Size ShownSize(string file, MediaInfo mi, Image frame)
        {
            int w = mi.Width, h = mi.Height;
            if (w <= 0 || h <= 0)
            {
                // the file didn't say: read one frame at its real size
                string ff = Tools.FFmpeg;
                byte[] png = ff == null ? null : Media.Capture(ff, "-hide_banner -loglevel error -i " + Tools.Quote(file) + " -frames:v 1 -f image2pipe -vcodec png -", 60000);
                if (png != null) try { using (Image big = Image.FromStream(new MemoryStream(png))) return big.Size; } catch { }
                return frame.Size;
            }
            bool frameWide = frame.Width >= frame.Height, fileWide = w >= h;
            if (frameWide != fileWide && Math.Abs(w - h) > 2) { int t = w; w = h; h = t; }
            return new Size(w, h);
        }

        /// The window takes the video's shape (tall phone videos get a taller window), inside the screen.
        void FitToVideo()
        {
            if (full.Width <= 0) return;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int below = Theme.S(358);
            int maxW = Math.Min(Theme.S(820), wa.Width - Theme.S(60)), maxH = Math.Min(Theme.S(560), wa.Height - Head - below - Theme.S(40));
            float k = Math.Min(maxW / (float)full.Width, Math.Max(Theme.S(160), maxH) / (float)full.Height);
            int cw = Math.Max(Theme.S(200), (int)(full.Width * k)), ch = Math.Max(Theme.S(160), (int)(full.Height * k));
            int W = Math.Max(Theme.S(560), cw + Pad * 2 + Theme.S(16));
            int H = Head + ch + below;
            Point c = new Point(Left + Width / 2, Top + Height / 2);
            Bounds = new Rectangle(Math.Max(wa.Left + 8, Math.Min(wa.Right - W - 8, c.X - W / 2)), Math.Max(wa.Top + 8, Math.Min(wa.Bottom - H - 8, c.Y - H / 2)), W, H);
        }

        /// The frame at a time (only the newest ask counts, so dragging the slider stays smooth).
        void AskFrame(double t)
        {
            if (info == null || Math.Abs(t - frameAt) < 0.04) return;
            frameAt = t;
            int ask = ++frameAsk;
            int h = FrameHeight();
            Thread th = new Thread(() =>
            {
                Thread.Sleep(70);
                if (ask != frameAsk) return;
                Image im = null;
                try { im = Media.Frame(file, t, h); } catch { }
                Ui(() => { if (ask == frameAsk && im != null && !player.Playing) canvas.SetShown(new Bitmap(im)); if (im != null) im.Dispose(); });
            });
            th.IsBackground = true; th.Start();
        }

        /// Pictures the size they're shown, not bigger.
        int FrameHeight()
        {
            if (full.Height <= 0) return Theme.S(480);
            float k = Math.Min(canvas.Width / (float)full.Width, canvas.Height / (float)full.Height);
            return Math.Max(90, Math.Min(full.Height, (int)(full.Height * k) + 2));
        }

        // ------------------------------------------------------------------ playing

        void PlayPause()
        {
            if (info == null || busy) return;
            if (player.Playing) { Pause(); return; }
            double from = time.Value >= info.Duration - 0.1 ? 0 : time.Value;
            int h = Math.Min(FrameHeight(), Theme.S(540));
            int w = (int)Math.Round(h * full.Width / (float)Math.Max(1, full.Height));
            try { player.Play(file, from, info.Duration, info.HasAudio, true, Math.Max(2, w), Math.Max(2, h)); } catch { }
            PlayState();
        }

        void Pause() { player.Stop(); frameAt = -1; PlayState(); }

        void PlayState()
        {
            bPlay.Glyph = player.Playing ? Ico.Pause : Ico.Play;
            DoLayout(); bPlay.Invalidate(); Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            // Space plays or pauses (not while typing a size)
            if (k == Keys.Space && info != null && !busy && !wBox.Box.Focused && !hBox.Box.Focused) { PlayPause(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { try { player.Dispose(); } catch { } base.OnFormClosed(e); }

        // ------------------------------------------------------------------ layout

        protected override void DoLayout()
        {
            base.DoLayout();
            if (canvas == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            int below = Theme.S(358);
            canvas.Bounds = M(new Rectangle(p, Head, W - p * 2, Math.Max(Theme.S(140), H - Head - below)));
            int y = canvas.Bottom + Theme.S(12);
            int pw = Math.Max(Theme.S(96), bPlay.PreferredWidth);
            bPlay.Bounds = M(new Rectangle(p, y, pw, Theme.S(34)));
            time.Bounds = M(new Rectangle(p + pw + Theme.S(10), y, W - p * 2 - pw - Theme.S(10), Theme.S(34)));
            y += Theme.S(40);
            volLbl = new Rectangle(p, y, Theme.S(96), Theme.S(34));
            volume.Bounds = M(new Rectangle(p + Theme.S(96) + Theme.S(10), y, W - p * 2 - Theme.S(106), Theme.S(34)));
            y += Theme.S(38);
            hintR = new Rectangle(p, y, W - p * 2, Theme.S(20));
            y += Theme.S(28);
            ratioLbl = new Rectangle(p, y, W - p * 2, Theme.S(20));
            ratio.Bounds = M(new Rectangle(p, y + Theme.S(24), W - p * 2, Theme.S(36)));
            y += Theme.S(72);
            int bw = Theme.S(96);
            sizeLbl = new Rectangle(p, y, Theme.S(50), Theme.S(36));
            wBox.Bounds = M(new Rectangle(p + Theme.S(50), y, bw, Theme.S(36)));
            xLbl = new Rectangle(p + Theme.S(50) + bw, y, Theme.S(28), Theme.S(36));
            hBox.Bounds = M(new Rectangle(xLbl.Right, y, bw, Theme.S(36)));
            pxLbl = new Rectangle(xLbl.Right + bw + Theme.S(8), y, Theme.S(30), Theme.S(36));
            y += Theme.S(44);
            bar.Bounds = M(new Rectangle(p, y + Theme.S(2), W - p * 2, Theme.S(6)));
            statusR = new Rectangle(p, y + Theme.S(10), W - p * 2, Theme.S(30));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(120), bGo.PreferredWidth + Theme.S(12)), cw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - cw, by, cw, Theme.S(40)));
            Rectangle left = new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(110), Math.Max(bReset.PreferredWidth, bShow.PreferredWidth)), Theme.S(34));
            bReset.Bounds = M(left);
            bShow.Bounds = M(left);
        }

        protected override void PaintBody(Graphics g)
        {
            if (info != null)
            {
                Theme.Text(g, L.T("vc_volume"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(volLbl), Align.Start);
                if (!info.HasAudio) Theme.Text(g, L.T("vc_no_sound"), Theme.F(8.75f), Theme.InkFaint, M(new Rectangle(volLbl.Right + Theme.S(10), volLbl.Y, ClientSize.Width - Pad - volLbl.Right - Theme.S(10), volLbl.Height)), Align.Start, true, true);
            }
            if (info != null) Theme.Text(g, L.T("vc_hint"), Theme.F(8.25f), Theme.InkFaint, M(hintR), Align.Start, true, false, true);
            Theme.Text(g, L.T("crop_ratio"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(ratioLbl), Align.Start);
            Theme.Text(g, L.T("crop_size"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(sizeLbl), Align.Start);
            Theme.Text(g, "×", Theme.F(10f), Theme.InkSoft, M(xLbl), Align.Center);
            Theme.Text(g, "px", Theme.F(9f), Theme.InkSoft, M(pxLbl), Align.Start);
            if (status != null) Theme.Text(g, status, Theme.F(9f, done ? FontStyle.Bold : FontStyle.Regular), statusColor, M(statusR), Align.Start, true, true);
        }

        // ------------------------------------------------------------------ size boxes

        void ShowSize()
        {
            if (typing) return;
            Rectangle c = Even(canvas.CropPx);
            if (!wBox.Box.Focused) wBox.Box.Text = c.Width.ToString(CultureInfo.InvariantCulture);
            if (!hBox.Box.Focused) hBox.Box.Text = c.Height.ToString(CultureInfo.InvariantCulture);
        }

        void TypedSize()
        {
            int w, h;
            if (!int.TryParse(wBox.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out w) ||
                !int.TryParse(hBox.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) { ShowSize(); return; }
            typing = true;
            if (ratio.SelectedIndex != 0) ratio.SelectedIndex = 0;
            canvas.SetCropSize(w, h);
            typing = false;
            ShowSize();
        }

        /// Videos need an even width and height (most players and encoders insist on it).
        Rectangle Even(Rectangle c)
        {
            int w = Math.Max(2, c.Width & ~1), h = Math.Max(2, c.Height & ~1);
            int x = Math.Max(0, Math.Min(full.Width - w, c.X)), y = Math.Max(0, Math.Min(full.Height - h, c.Y));
            return new Rectangle(x, y, w, h);
        }

        // ------------------------------------------------------------------ cropping

        protected override bool CanClose() { if (busy) { cancel = true; return false; } return true; }

        void Go()
        {
            if (busy || done || info == null || full.Width <= 0) return;
            Rectangle c = Even(canvas.CropPx);
            if (c.Width < 2 || c.Height < 2) return;
            if (player.Playing) Pause();
            busy = true; cancel = false;
            bGo.Enabled = false; bReset.Enabled = false; canvas.Enabled = false; ratio.Enabled = false; volume.Enabled = false; wBox.Enabled = hBox.Enabled = false;
            bar.Visible = true; bar.Value = 0;
            status = L.T("vc_working"); statusColor = Theme.InkSoft; Invalidate(); bCancel.Invalidate();
            ActJob act = Activity.Begin(activitySource, L.T("wh_crop"), Path.GetFileName(file), () => cancel = true);
            MediaInfo mi = info; Size whole = full; double vol = Vol();
            Thread th = new Thread(() =>
            {
                string err = null, outp = null;
                try
                {
                    Job job = Build(file, mi, c, whole, DragWheel.OutDirFor(file), vol);
                    outp = job.Output;
                    Action<double> prog = pr => { Activity.Update(act, pr, (int)(pr * 100) + "%"); Ui(() => bar.Value = pr); };
                    err = Engine.Run(job, prog, () => cancel);
                    if (err != null && !cancel && job.Fallback != null)
                    {
                        try { if (File.Exists(outp)) File.Delete(outp); } catch { }
                        job = job.Fallback();
                        err = Engine.Run(job, prog, () => cancel);
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                if ((cancel || err != null) && outp != null) try { if (File.Exists(outp)) File.Delete(outp); } catch { }
                Activity.End(act, cancel ? null : err, cancel, err == null && !cancel ? L.T("crop_done", c.Width, c.Height) : null, err == null && !cancel ? outp : null);
                Ui(() =>
                {
                    busy = false; bar.Visible = false;
                    canvas.Enabled = true; ratio.Enabled = true; volume.Enabled = info.HasAudio; wBox.Enabled = hBox.Enabled = true; bReset.Enabled = true;
                    if (cancel) { status = L.T("cancelled"); statusColor = Theme.InkSoft; bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); return; }
                    if (err != null) { status = err; statusColor = Theme.Error; bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); return; }
                    done = true; result = outp;
                    long sz = 0; try { sz = new FileInfo(outp).Length; } catch { }
                    status = L.T("up_saved", Path.GetFileName(outp), Theme.HumanSize(sz)); statusColor = Theme.Success;
                    bGo.Enabled = true; bGo.Glyph = Ico.Check; bCancel.Visible = false; bReset.Visible = false; bShow.Visible = true;
                    DoLayout(); Invalidate(); bGo.Invalidate();
                    PicLoad.Done(outp);
                });
            });
            th.IsBackground = true; th.Start();
        }

        /// ffmpeg: the picture through the crop filter (re-encoded in the same format), the sound copied as it is.
        /// If the sound can't be copied into the new file, it tries again with the sound re-encoded.
        public static Job Build(string input, MediaInfo mi, Rectangle c, Size whole, string outDir, double vol = 1)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            string ext = Formats.Ext(input);
            if (ext == "" || Formats.CategoryOf(ext) != Category.Video) ext = "mp4";
            string outp = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(input) + " " + L.T("vc_suffix"), ext);
            bool all = c.X == 0 && c.Y == 0 && c.Width >= (whole.Width & ~1) && c.Height >= (whole.Height & ~1);
            string vf = all ? "" : " -vf \"crop=" + c.Width + ":" + c.Height + ":" + c.X + ":" + c.Y + ",setsar=1\"";
            MediaInfo pictureOnly = new MediaInfo { HasVideo = true, HasAudio = false, Width = mi.Width, Height = mi.Height };
            Func<string, Job> make = audio =>
            {
                Job j = new Job { Output = outp };
                string args = "-hide_banner -y -i " + Tools.Quote(input) + " -map 0:v:0 -map 0:a? -map_metadata 0" + vf + " " + Media.CodecsFor(ext, pictureOnly) + " " + audio + " " + Tools.Quote(outp);
                j.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = args, Duration = mi.Duration });
                return j;
            };
            bool same = Math.Abs(vol - 1) < 0.005;
            if (!mi.HasAudio || vol < 0.005) return make("-an");                 // no sound, or turned all the way down
            if (!same) return make("-af \"volume=" + vol.ToString("0.##", CultureInfo.InvariantCulture) + "\" " + Media.AudioCodecFor(ext));
            Job job = make("-c:a copy");
            job.Fallback = () => make(Media.AudioCodecFor(ext));
            return job;
        }
    }
}
