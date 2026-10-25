using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// The video studio, CapCut style: add things on the left, the picture in the middle, the selected clip's settings
    /// on the right, and the timeline (texts, overlays, the main video, sounds, captions) along the bottom.
    public partial class VideoStudio : Page
    {
        VProject P = new VProject();
        readonly VideoTimeline timeline = new VideoTimeline();
        readonly VideoPreview preview = new VideoPreview();
        readonly FrameCache frames = new FrameCache();
        readonly PanelBody props = new PanelBody();
        readonly AddRail addRail = new AddRail();
        readonly PreviewPlayer player = new PreviewPlayer();
        readonly DropZone drop;
        readonly CanvasShapes shapes = new CanvasShapes();
        readonly RecentStrip recents = new RecentStrip();
        readonly ToolTip tips = new ToolTip();
        readonly Btn bUndo, bRedo, bAspect, bMore, bExport, bOpen, bPlay, bBack, bFwd, bSplit, bDel, bDup, bDetach, bKey, bSnap, bZoomIn, bZoomOut, bFitTl;
        Rectangle bar, railR, previewR, panelR, tlR, transportR, timeR, headR, emptyR, nameR, infoR;
        string projectPath;
        bool loading;
        public event Action<string> OpenImage;

        public const string ProjectExt = "fadiproj";
        public static string ProjectsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fadi Tools", "Projects"); } }
        public string ProjectPath { get { return projectPath; } }
        public bool HasFile { get { return !P.Empty; } }
        public override bool Busy { get { return exporting || busyJob != null; } }
        public bool HasUnsavedText { get { return HasFile && changes != savedAt; } }
        int changes, savedAt;
        string busyJob;
        bool exporting;

        public VideoStudio() : base("studio_title", "studio_sub")
        {
            Glyph = Ico.Video;
            timeline.P = P; preview.P = P;
            timeline.BeforeChange = n => { Change(n); return true; };
            timeline.Changed += () => AfterChange(false);
            timeline.Seek += (t, final) => { StopPlay(); Playhead = t; Recompose(); if (final) { } };
            timeline.Picked += c => { Select(c); };
            timeline.TransitionPicked += i => { tab = 2; LayoutProps(); };
            timeline.Menu += ClipMenu;
            timeline.FilesDropped += DroppedOnTimeline;
            timeline.Opened += c => { if (c.Kind == VKind.Text) BeginInvoke((Action)(() => { textBox.Box.Focus(); textBox.Box.SelectAll(); })); };
            timeline.ZoomChanged += () => { };
            preview.BeforeChange = n => { Change(n, preview); return true; };
            preview.Changed += () => { Recompose(); SyncPlacement(); timeline.Invalidate(); };
            preview.Picked += c => { timeline.Selected = c; Select(c); };
            preview.Clicked += TogglePlay;
            frames.Arrived += () => Ui(() => { if (!playing) Recompose(); });
            props.Scrolled += LayoutProps;
            props.ExtraPaint = PaintPropsExtra;
            addRail.Picked += AddPicked;
            // ---- the top bar
            bUndo = Icon("", "undo_tip", Undo);
            bRedo = Icon("", "redo_tip", Redo);
            bAspect = new Btn(() => AspectName(P.Aspect), "", BtnKind.Ghost) { FontSize = 9f, GlyphSize = 10f };
            bAspect.Click += (s, e) => AspectMenu();
            bAspect.MouseEnter += (s, e) => tips.SetToolTip(bAspect, L.T("vs_aspect"));
            bMore = Icon(Ico.More, "ps_more", MoreMenu);
            bExport = new Btn(() => L.T("ps_export"), Ico.Upload, BtnKind.Primary) { FontSize = 9.5f };
            bExport.Click += (s, e) => Export();
            bExport.MouseEnter += (s, e) => tips.SetToolTip(bExport, L.T("vs_export_tip"));
            bOpen = new Btn(() => L.T("vs_open_project"), "", BtnKind.Secondary) { FontSize = 9.5f };
            bOpen.Click += (s, e) => { string f = PickProject(FindForm()); if (f != null) OpenProject(f, false); };
            // ---- under the picture: playing
            bBack = Icon("", "vs_back5", () => { StopPlay(); Playhead = Math.Max(0, Playhead - 5); Recompose(); });
            bPlay = new Btn(() => "", Ico.Play, BtnKind.Ghost) { GlyphSize = 12f };
            bPlay.Click += (s, e) => TogglePlay();
            bPlay.MouseEnter += (s, e) => tips.SetToolTip(bPlay, L.T("vs_play_tip"));
            bFwd = Icon("", "vs_fwd5", () => { StopPlay(); Playhead = Math.Min(P.Duration, Playhead + 5); Recompose(); });
            // ---- the timeline's tools
            bSplit = Icon(Ico.Cut, "vs_split", Split);
            bDel = Icon(Ico.Delete, "vs_delete", DeleteSelected);
            bDup = Icon(Ico.Copy, "vs_duplicate", DuplicateSelected);
            bDetach = Icon("", "vs_detach", DetachAudio);
            bKey = Icon("", "vs_keyframe", ToggleAllKeys);
            bSnap = Icon("", "vs_snap", () => { timeline.Snap = !timeline.Snap; bSnap.Active = timeline.Snap; bSnap.Invalidate(); });
            bSnap.Active = true;
            bZoomOut = Icon(Ico.ZoomOut, "vs_zoom_out", () => timeline.ZoomBy(0.75, timeline.Width / 2));
            bZoomIn = Icon(Ico.ZoomIn, "vs_zoom_in", () => timeline.ZoomBy(1.33, timeline.Width / 2));
            bFitTl = Icon(Ps.Fit, "vs_zoom_fit", () => timeline.FitAll());
            // ---- nothing open
            drop = new DropZone(() => L.T("vs_drop"), () => L.T("vs_drop_sub")) { Tips = new[] { "tip_vs_tracks", "tip_vs_keys", "tip_vs_captions", "tip_ctrl_tab" } };
            drop.Glyph = Ico.Video;
            drop.Click += (s, e) => Browse(false);
            shapes.Picked += a => { P.Aspect = a; Browse(false); };
            recents.Open += f => OpenProject(f, false);
            recents.Browse += () => { string f = PickProject(FindForm()); if (f != null) OpenProject(f, false); };
            InitProps();
            Controls.AddRange(new Control[] { timeline, preview, props, addRail, bUndo, bRedo, bAspect, bMore, bExport, bOpen, bPlay, bBack, bFwd, bSplit, bDel, bDup, bDetach, bKey, bSnap, bZoomIn, bZoomOut, bFitTl, drop, shapes, recents });
            EnableDrop(drop, preview, shapes);
            autoTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            autoTimer.Tick += (s, e) => AutoSave();
            autoTimer.Start();
            saveNow = () => { try { AutoSave(); } catch { } };
            Recovery.SaveNowRequested += saveNow;
            UpdateButtons();
        }

        System.Windows.Forms.Timer autoTimer;
        Action saveNow;

        Btn Icon(string glyph, string tipKey, Action a)
        {
            Btn b = new Btn(() => "", glyph, BtnKind.Ghost) { GlyphSize = 10.5f };
            b.Click += (s, e) => a();
            b.MouseEnter += (s, e) => tips.SetToolTip(b, L.T(tipKey));
            b.AccessibleName = L.T(tipKey);
            return b;
        }

        static string AspectName(string a) { return a == "orig" ? L.T("vs_ar_orig") : a; }

        // ------------------------------------------------------------------ the playhead
        double playhead;
        double Playhead
        {
            get { return playhead; }
            set { playhead = Math.Max(0, Math.Min(Math.Max(0, P.Duration - 0.001), value)); timeline.Playhead = playhead; preview.Time = playhead; timeline.Invalidate(); InvalidateTime(); }
        }
        void InvalidateTime() { if (!timeR.IsEmpty) Invalidate(M(timeR)); }

        int composeToken;
        /// Draws the picture at the playhead (off the screen's thread; the newest wish wins).
        void Recompose()
        {
            if (playing || P.Empty) { preview.Invalidate(); return; }
            int my = ++composeToken;
            VProject snap = P.Copy();
            double t = playhead;
            RectangleF s = preview.Screen;
            int w = Math.Max(16, Math.Min(1280, (int)s.Width)), h = Math.Max(16, (int)Math.Round(w * P.H / (double)P.W));
            frames.Height = Math.Max(240, Math.Min(720, h));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (my != composeToken) return;
                bool waiting;
                Bitmap b = null;
                lock (frames.Gate)
                {
                    if (my != composeToken) return;
                    try { b = Composer.Compose(snap, t, w, h, frames, out waiting); } catch { }
                }
                Ui(() => { if (my != composeToken || playing) { if (b != null) b.Dispose(); return; } preview.SetFrame(b); });
            });
            preview.Invalidate();
        }

        // ------------------------------------------------------------------ playing (a quick render of the whole project, then played)
        bool playing;
        string proxyFile, proxyKey;
        bool proxyRendering; volatile bool proxyCancel;
        int proxyW, proxyH;

        void TogglePlay() { if (playing || proxyRendering) StopPlay(); else Play(); }

        void Play()
        {
            if (P.Empty || exporting) return;
            if (Playhead >= P.Duration - 0.05) Playhead = 0;
            string key = P.ToJson(null).GetHashCode() + "/" + P.Duration;
            if (proxyFile != null && proxyKey == key && File.Exists(proxyFile)) { StartPlayer(); return; }
            // build the preview: small and fast, with everything in it
            proxyRendering = true; proxyCancel = false;
            preview.Note = L.T("vs_preparing", 0);
            bPlay.Glyph = Ico.Stop; bPlay.Invalidate(); preview.Invalidate();
            int w = Math.Min(854, P.W), h = (int)Math.Round(w * P.H / (double)P.W);
            if (h > 854) { h = 854; w = (int)Math.Round(h * P.W / (double)P.H); }
            w -= w % 2; h -= h % 2;
            string outp = Path.Combine(Path.GetTempPath(), "fadi_vs_preview_" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".mp4");
            Job job;
            try { job = VideoRender.Build(P.Copy(), outp, new RenderOptions { W = w, H = h, Fps = 25, Preview = true }); }
            catch (Exception ex) { proxyRendering = false; preview.Note = null; bPlay.Glyph = Ico.Play; ShowToast(ex.Message, false, null, null); return; }
            Thread t = new Thread(() =>
            {
                string err = Engine.Run(job, f => Ui(() => { if (proxyRendering) { preview.Note = L.T("vs_preparing", (int)(f * 100)); preview.Invalidate(); } }), () => proxyCancel || Tools.ShuttingDown);
                Ui(() =>
                {
                    proxyRendering = false; preview.Note = null;
                    if (proxyCancel || err != null || !File.Exists(outp)) { bPlay.Glyph = Ico.Play; bPlay.Invalidate(); preview.Invalidate(); if (err != null && !proxyCancel) ShowToast(err, false, null, null); return; }
                    if (proxyFile != null) try { File.Delete(proxyFile); } catch { }
                    proxyFile = outp; proxyKey = key; proxyW = w; proxyH = h;
                    StartPlayer();
                });
            });
            t.IsBackground = true; t.Start();
        }

        void StartPlayer()
        {
            playing = true; preview.Playing = true;
            bPlay.Glyph = Ico.Pause; bPlay.Invalidate();
            double from = Playhead;
            player.Fps = 25;
            player.Start(proxyFile, from, P.Duration, proxyW > 0 ? proxyW : 640, proxyH > 0 ? proxyH : 360, true, 1,
                (b, t) => Ui(() => { if (!playing) { b.Dispose(); return; } preview.SetFrame(b); Playhead = t; timeline.Follow(); }),
                t => Ui(() => { if (playing) { Playhead = t; timeline.Follow(); } }),
                () => Ui(() => { StopPlay(); }));
        }

        void StopPlay()
        {
            if (proxyRendering) { proxyCancel = true; proxyRendering = false; preview.Note = null; }
            if (!playing && bPlay.Glyph == Ico.Play) return;
            playing = false; preview.Playing = false;
            player.Stop();
            bPlay.Glyph = Ico.Play; bPlay.Invalidate();
            Recompose();
        }

        // ------------------------------------------------------------------ history
        readonly List<VProject> undo = new List<VProject>(), redo = new List<VProject>();
        readonly List<string> undoNames = new List<string>();
        object editKey; DateTime editAt;
        void Change(string name, object key = null)
        {
            if (key == null || key != editKey || (DateTime.Now - editAt).TotalSeconds > 2.5)
            {
                undo.Add(P.Copy()); undoNames.Add(name);
                if (undo.Count > 60) { undo.RemoveAt(0); undoNames.RemoveAt(0); }
                redo.Clear();
            }
            editKey = key; editAt = DateTime.Now;
            changes++;
        }
        void EndChange() { editKey = null; }

        void AfterChange(bool structure)
        {
            P.Layout();
            if (Playhead > P.Duration) Playhead = P.Duration;
            timeline.Invalidate();
            UpdateButtons();
            InvalidateBar();
            if (structure) { LoadProps(); LayoutProps(); } else SyncPlacement();
            Recompose();
        }

        void Restore(VProject s)
        {
            string sel = timeline.Selected != null ? timeline.Selected.Id : null;
            P = s; timeline.P = P; preview.P = P;
            VClip c = sel != null ? P.Find(sel) : null;
            timeline.Selected = c; preview.Selected = c;
            changes++;
            AfterChange(true);
        }
        public void Undo() { if (undo.Count == 0 || exporting) return; StopPlay(); EndChange(); redo.Add(P.Copy()); VProject s = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1); undoNames.RemoveAt(undoNames.Count - 1); Restore(s); }
        public void Redo() { if (redo.Count == 0 || exporting) return; StopPlay(); EndChange(); undo.Add(P.Copy()); undoNames.Add("redo"); VProject s = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1); Restore(s); }

        void UpdateButtons()
        {
            bUndo.Enabled = undo.Count > 0; bRedo.Enabled = redo.Count > 0;
            VClip c = timeline.Selected;
            bSplit.Enabled = c != null && Playhead > c.At + 0.05 && Playhead < c.End - 0.05;
            bDel.Enabled = bDup.Enabled = c != null;
            bDetach.Enabled = c != null && c.Kind == VKind.Video && c.HasSound;
            bKey.Enabled = c != null && (c.Kind == VKind.Text || ((c.Kind == VKind.Video || c.Kind == VKind.Image) && P.TrackOf(c) != null && P.TrackOf(c).Kind == VTrackKind.Overlay) || c.Kind == VKind.Audio) && Playhead >= c.At && Playhead <= c.End;
            foreach (Btn b in new[] { bUndo, bRedo, bSplit, bDel, bDup, bDetach, bKey }) b.Invalidate();
        }

        void Select(VClip c)
        {
            timeline.Selected = c; preview.Selected = c;
            EndChange();
            if (c == null || tab > MaxTab(c)) tab = 0;
            props.ResetScroll();
            LoadProps(); LayoutProps();
            UpdateButtons();
            timeline.Invalidate(); preview.Invalidate();
            InvalidateHead();
        }

        // ------------------------------------------------------------------ layout
        int PanelW { get { return Math.Max(Theme.S(280), Math.Min(Theme.S(330), Width / 4)); } }

        public override void DoLayout()
        {
            if (bExport == null) return;
            int W = Width, H = Height, p = Pad;
            cards.Clear();
            bool open = HasFile;
            bar = new Rectangle(p, Top0, W - p * 2, Theme.S(58));
            cards.Add(bar);
            int y = bar.Y + (bar.Height - Theme.S(36)) / 2, h = Theme.S(36), x = bar.Right - Theme.S(12);
            foreach (Control c in new Control[] { bUndo, bRedo, bAspect, bMore, bExport }) c.Visible = open;
            bOpen.Visible = !open;
            if (open)
            {
                int ew = Math.Max(Theme.S(104), bExport.PreferredWidth + Theme.S(8));
                x -= ew; Place(bExport, new Rectangle(x, y, ew, h)); x -= Theme.S(6);
                x -= h; Place(bMore, new Rectangle(x, y, h, h)); x -= Theme.S(12);
                int aw = Math.Max(Theme.S(86), bAspect.PreferredWidth + Theme.S(6)); x -= aw; Place(bAspect, new Rectangle(x, y, aw, h)); x -= Theme.S(12);
                x -= h; Place(bRedo, new Rectangle(x, y, h, h)); x -= Theme.S(2);
                x -= h; Place(bUndo, new Rectangle(x, y, h, h)); x -= Theme.S(10);
            }
            else { int ow = Math.Max(Theme.S(120), bOpen.PreferredWidth + Theme.S(8)); x -= ow; Place(bOpen, new Rectangle(x, y, ow, h)); x -= Theme.S(10); }
            nameR = new Rectangle(bar.X + Theme.S(18), bar.Y + Theme.S(9), Math.Max(10, x - bar.X - Theme.S(26)), Theme.S(22));
            infoR = new Rectangle(nameR.X, bar.Y + Theme.S(31), nameR.Width, Theme.S(18));
            int top = bar.Bottom + Theme.S(12), bottom = H - Theme.S(16);
            foreach (Control c in new Control[] { timeline, preview, props, addRail, bPlay, bBack, bFwd, bSplit, bDel, bDup, bDetach, bKey, bSnap, bZoomIn, bZoomOut, bFitTl }) c.Visible = open;
            drop.Visible = shapes.Visible = !open;
            if (open)
            {
                int tlH = Math.Max(Theme.S(200), Math.Min(Theme.S(380), (bottom - top) * 40 / 100));
                tlR = new Rectangle(p, bottom - tlH, W - p * 2, tlH);
                cards.Add(tlR);
                int upper = tlR.Y - Theme.S(12) - top;
                railR = new Rectangle(p, top, Theme.S(70), upper);
                cards.Add(railR);
                Place(addRail, Rectangle.Inflate(railR, -Theme.S(4), -Theme.S(6)));
                panelR = new Rectangle(W - p - PanelW, top, PanelW, upper);
                cards.Add(panelR);
                headR = new Rectangle(panelR.X + Theme.S(14), panelR.Y + Theme.S(10), panelR.Width - Theme.S(28), Theme.S(26));
                Place(props, new Rectangle(panelR.X + Theme.S(8), headR.Bottom + Theme.S(4), panelR.Width - Theme.S(16), panelR.Bottom - headR.Bottom - Theme.S(12)));
                // the picture, with the play controls under it
                Rectangle center = new Rectangle(railR.Right + Theme.S(10), top, panelR.X - Theme.S(10) - railR.Right - Theme.S(10), upper);
                cards.Add(center);
                transportR = new Rectangle(center.X, center.Bottom - Theme.S(48), center.Width, Theme.S(48));
                previewR = new Rectangle(center.X + Theme.S(8), center.Y + Theme.S(8), center.Width - Theme.S(16), transportR.Y - center.Y - Theme.S(8));
                Place(preview, previewR);
                int bs = Theme.S(36), cy = transportR.Y + (transportR.Height - bs) / 2, mid = transportR.X + transportR.Width / 2;
                Place(bBack, new Rectangle(mid - bs / 2 - bs - Theme.S(6), cy, bs, bs));
                Place(bPlay, new Rectangle(mid - bs / 2, cy, bs, bs));
                Place(bFwd, new Rectangle(mid + bs / 2 + Theme.S(6), cy, bs, bs));
                timeR = new Rectangle(transportR.X + Theme.S(16), transportR.Y, mid - bs * 2 - transportR.X - Theme.S(24), transportR.Height);
                // the timeline: its tools on top
                int ty = tlR.Y + Theme.S(8), tb = Theme.S(32), tx = tlR.X + Theme.S(12);
                foreach (Btn b in new[] { bSplit, bDel, bDup, bDetach, bKey }) { Place(b, new Rectangle(tx, ty, tb, tb)); tx += tb + Theme.S(4); }
                int rx = tlR.Right - Theme.S(12);
                foreach (Btn b in new[] { bFitTl, bZoomIn, bZoomOut }) { rx -= tb; Place(b, new Rectangle(rx, ty, tb, tb)); rx -= Theme.S(4); }
                rx -= Theme.S(10); rx -= tb; Place(bSnap, new Rectangle(rx, ty, tb, tb));
                Place(timeline, new Rectangle(tlR.X + Theme.S(4), ty + tb + Theme.S(6), tlR.Width - Theme.S(8), tlR.Bottom - ty - tb - Theme.S(12)));
                recents.Visible = false;
                LayoutProps();
                Recompose();
            }
            else
            {
                bool rec = RecentProjects.Count > 0;
                emptyR = new Rectangle(p, top, W - p * 2, bottom - top - (rec ? Theme.S(96) : 0));
                cards.Add(emptyR);
                int th = Theme.S(150);
                Place(drop, new Rectangle(emptyR.X + Theme.S(20), emptyR.Y + Theme.S(20), emptyR.Width - Theme.S(40), Math.Max(Theme.S(120), emptyR.Height - th - Theme.S(40))));
                Place(shapes, new Rectangle(emptyR.X + Theme.S(20), emptyR.Bottom - th - Theme.S(12), emptyR.Width - Theme.S(40), th));
                List<string> rp = RecentProjects;
                recents.Paths = rp; recents.Visible = rp.Count > 0;
                if (recents.Visible) { Place(recents, new Rectangle(p, H - Theme.S(96), W - p * 2, Theme.S(82))); recents.Invalidate(); }
            }
            Invalidate();
        }

        void InvalidateBar() { if (!nameR.IsEmpty) Invalidate(Rectangle.Union(M(nameR), M(infoR))); }
        void InvalidateHead() { if (!headR.IsEmpty) Invalidate(M(headR)); }
        protected override void DragHighlight(bool on) { drop.DragHot = on; drop.Invalidate(); }

        protected override void PaintContent(Graphics g)
        {
            Font nf = Theme.F(10.5f, FontStyle.Bold), inf = Theme.F(8.75f);
            if (!HasFile)
            {
                Label(g, loading ? L.T("v_loading") : L.T("vs_none"), nf, Theme.Ink, nameR);
                Label(g, L.T("vs_none_sub"), inf, Theme.InkSoft, infoR);
                return;
            }
            string name = projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : FirstName();
            Label(g, name + (HasUnsavedText ? "  •" : ""), nf, Theme.Ink, nameR);
            string info = busyJob ?? L.T("vs_info", P.W + " × " + P.H, Math.Round(P.Fps).ToString(), Theme.Time(P.Duration, false));
            Theme.Text(g, info, inf, Theme.InkSoft, M(infoR), Theme.RTL ? Align.End : Align.Start, true, false, true);
            // the time under the picture
            Theme.Text(g, Theme.Time(Playhead, true) + "  /  " + Theme.Time(P.Duration, true), Theme.F(9.5f, FontStyle.Bold), Theme.InkSoft, M(timeR), Align.Start, true, false, true);
            Theme.Text(g, PanelTitle(), Theme.F(10f, FontStyle.Bold), Theme.Ink, M(headR), Align.Start);
        }

        string FirstName()
        {
            foreach (VClip c in P.Main.Clips) if (c.File != null) return Path.GetFileNameWithoutExtension(c.File);
            foreach (VTrack t in P.Tracks) foreach (VClip c in t.Clips) if (c.File != null) return Path.GetFileNameWithoutExtension(c.File);
            return L.T("ps_untitled");
        }

        public override void Restyle() { timeline.Invalidate(); preview.Invalidate(); props.Invalidate(); addRail.Invalidate(); Invalidate(); }
        public override void OnShown() { if (HasFile) { timeline.Focus(); Recompose(); } }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        // ------------------------------------------------------------------ media
        readonly Dictionary<string, MediaSource> sources = new Dictionary<string, MediaSource>(StringComparer.OrdinalIgnoreCase);

        /// What the file is (probed once), with its size the way it shows (turned phone videos the right way).
        MediaSource Source(string file, VKind kind)
        {
            MediaSource s;
            lock (sources) if (sources.TryGetValue(file, out s)) return s;
            string ext = Formats.Ext(file);
            if (Formats.CategoryOf(ext) == Category.Image)
            {
                int w = 1280, h = 720;
                try { using (Image im = ViewerPage.Decode(file, 1600)) { w = im.Width; h = im.Height; } } catch { return null; }
                s = new MediaSource { File = file, Info = new MediaInfo { Duration = 5 }, W = w, H = h, Strip = new Image[1] };
            }
            else
            {
                MediaInfo mi = null; try { mi = Media.Probe(file); } catch { }
                if (mi == null || (!mi.HasVideo && !mi.HasAudio) || mi.Duration <= 0) return null;
                int w = mi.Width, h = mi.Height;
                if (mi.HasVideo)
                {
                    using (Image first = Media.Frame(file, Math.Min(1, mi.Duration / 10), 180))
                    {
                        if (first != null && w > 0 && h > 0) { float ar = first.Width / (float)first.Height; if (Math.Abs(ar - h / (float)w) < Math.Abs(ar - w / (float)h)) { int tmp = w; w = h; h = tmp; } }
                        else if (first != null) { w = first.Width * 4; h = first.Height * 4; }
                    }
                }
                if (w <= 0) { w = 1280; h = 720; }
                s = new MediaSource { File = file, Info = mi, W = w + w % 2, H = h + h % 2, Strip = mi.HasVideo ? new Image[16] : null };
            }
            lock (sources) sources[file] = s;
            LoadPictures(s);
            return s;
        }

        /// The little pictures along a clip (and its sound wave), made once per file in the background.
        void LoadPictures(MediaSource s)
        {
            Thread t = new Thread(() =>
            {
                try
                {
                    if (Formats.CategoryOf(Formats.Ext(s.File)) == Category.Image)
                    {
                        Image im = null; try { using (Image big = ViewerPage.Decode(s.File, 400)) im = new Bitmap(big); } catch { }
                        Ui(() => { if (s.Strip != null) s.Strip[0] = im; timeline.Invalidate(); });
                        return;
                    }
                    if (s.Info.HasVideo && s.Strip != null)
                    {
                        // one run of ffmpeg for all of them: evenly spaced frames, side by side
                        int n = s.Strip.Length;
                        double dur = Math.Max(0.5, s.Info.Duration);
                        string ff = Tools.FFmpeg;
                        byte[] png = ff == null ? null : Media.Capture(ff, "-hide_banner -loglevel error -i " + Tools.Quote(s.File) + " -vf \"fps=" + (n / dur).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + ",scale=-2:90,tile=" + n + "x1\" -frames:v 1 -f image2pipe -vcodec png -", 120000);
                        if (png != null)
                        {
                            using (Image tile = Image.FromStream(new MemoryStream(png)))
                            {
                                int fw = tile.Width / n;
                                Image[] parts = new Image[n];
                                for (int i = 0; i < n; i++)
                                {
                                    Bitmap b = new Bitmap(Math.Max(1, fw), tile.Height);
                                    using (Graphics g = Graphics.FromImage(b)) g.DrawImage(tile, new Rectangle(0, 0, fw, tile.Height), new Rectangle(i * fw, 0, fw, tile.Height), GraphicsUnit.Pixel);
                                    parts[i] = b;
                                }
                                Ui(() => { if (s.Strip != null) for (int i = 0; i < n && i < s.Strip.Length; i++) s.Strip[i] = parts[i]; timeline.Invalidate(); });
                            }
                        }
                    }
                    if (s.Info.HasAudio)
                    {
                        Color c = Color.FromArgb(240, 244, 248);
                        Image w = Media.Waveform(s.File, 1600, 120, c);
                        Ui(() => { s.Wave = w; timeline.Invalidate(); });
                    }
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }

        public void Browse(bool append)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("vs_add_media");
                List<string> pats = new List<string>();
                foreach (string e in Formats.ImageExt) pats.Add("*." + e);
                d.Filter = L.T("photos_videos") + "|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.m4v;*.mpg;*.3gp;*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;" + string.Join(";", pats.ToArray()) + ";*." + ProjectExt + "|" + L.T("all_files") + "|*.*";
                d.Multiselect = true;
                if (LastDir.Show(d, FindForm(), "video") == DialogResult.OK) { if (append || HasFile) AddMedia(d.FileNames, null, -1); else AcceptFiles(d.FileNames); }
            }
        }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            if (paths.Length == 1 && Formats.Ext(paths[0]) == ProjectExt) { OpenProject(paths[0], false); return; }
            // a photo by itself goes to the photo studio (when the video studio is empty)
            if (!HasFile && paths.Length == 1 && Formats.CategoryOf(Formats.Ext(paths[0])) == Category.Image && OpenImage != null) { OpenImage(paths[0]); return; }
            AddMedia(paths, null, -1);
        }
        public void AppendFiles(string[] paths) { AddMedia(paths, null, -1); }

        /// Photos laid over the video (when something is open).
        public bool AddPictures(string[] paths)
        {
            if (!HasFile) return false;
            AddMedia(paths, VTrackKind.Overlay, Playhead);
            return true;
        }

        void DroppedOnTimeline(string[] files, VTrack onto, double at)
        {
            VTrackKind? kind = null;
            if (onto != null) kind = onto.Kind == VTrackKind.Text || onto.Kind == VTrackKind.Captions ? VTrackKind.Overlay : onto.Kind;
            AddMedia(files, kind, kind == VTrackKind.Main || kind == null ? -1 : at);
        }

        /// Adds files: videos and photos to the main track (or as overlays), sounds to an audio track.
        void AddMedia(string[] paths, VTrackKind? where, double at)
        {
            List<string> files = new List<string>();
            foreach (string f in paths) if (File.Exists(f)) files.Add(Path.GetFullPath(f));
            if (files.Count == 0) return;
            StopPlay();
            loading = true; busyJob = L.T("loading_media"); InvalidateBar(); Invalidate();
            bool first = !HasFile;
            Thread t = new Thread(() =>
            {
                List<KeyValuePair<string, MediaSource>> got = new List<KeyValuePair<string, MediaSource>>();
                foreach (string f in files)
                {
                    VKind k = Formats.CategoryOf(Formats.Ext(f)) == Category.Image ? VKind.Image : VKind.Video;
                    got.Add(new KeyValuePair<string, MediaSource>(f, Source(f, k)));
                }
                Ui(() =>
                {
                    loading = false; busyJob = null;
                    List<string> bad = new List<string>();
                    bool any = false;
                    foreach (KeyValuePair<string, MediaSource> kv in got)
                    {
                        MediaSource s = kv.Value;
                        if (s == null) { bad.Add(Path.GetFileName(kv.Key)); continue; }
                        bool image = Formats.CategoryOf(Formats.Ext(kv.Key)) == Category.Image;
                        bool sound = !image && !s.Info.HasVideo;
                        if (!any) { Change("vs_h_add"); any = true; }
                        VClip c = new VClip { Kind = image ? VKind.Image : sound ? VKind.Audio : VKind.Video, Src = s, In = 0, Out = image ? 4 : s.Info.Duration };
                        VTrackKind tk = where ?? (sound && !P.Main.Clips.Exists(x => x.Kind != VKind.Audio) && P.Main.Clips.Count == 0 && files.Count == 1 ? VTrackKind.Main : sound ? VTrackKind.Audio : VTrackKind.Main);
                        if (sound && tk == VTrackKind.Overlay) tk = VTrackKind.Audio;
                        if (tk == VTrackKind.Audio) { if (!s.Info.HasAudio) { bad.Add(Path.GetFileName(kv.Key)); continue; } c.Kind = VKind.Audio; }
                        if (tk == VTrackKind.Main) P.Main.Clips.Add(c);
                        else
                        {
                            c.At = at >= 0 ? at : Playhead;
                            if (tk == VTrackKind.Overlay) { c.Scale = 0.4f; c.X = 0.5f; c.Y = 0.5f; }
                            P.TrackFor(tk, c.At, c.Length).Clips.Add(c);
                        }
                        if (first && P.Main.Clips.Count == 1 && c.Kind != VKind.Audio) { P.SizeFor(s.W, s.H); }
                        timeline.Selected = c;
                    }
                    if (bad.Count > 0) ShowToast(L.T("not_media") + ": " + string.Join(", ", bad.ToArray()), false, null, null);
                    if (!any) { InvalidateBar(); Invalidate(); return; }
                    P.Layout();
                    if (first) { DoLayout(); timeline.FitAll(); }
                    Select(timeline.Selected);
                    AfterChange(true);
                    foreach (string f in files) RecentFiles.Add("studio", f);
                });
            });
            t.IsBackground = true; t.Start();
        }

        // ------------------------------------------------------------------ closing
        public bool CloseFile()
        {
            if (exporting) return false;
            if (HasUnsavedText && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("vs_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return false;
            Unload();
            return true;
        }

        public void Unload()
        {
            StopPlay();
            P = new VProject(); timeline.P = P; preview.P = P;
            timeline.Selected = null; preview.Selected = null;
            preview.SetFrame(null);
            projectPath = null;
            undo.Clear(); redo.Clear(); undoNames.Clear();
            changes = savedAt = 0;
            playhead = 0; timeline.Playhead = 0;
            frames.Clear();
            Recovery.Clear(Recovery.VideoFile);
            DoLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                player.Stop();
                if (saveNow != null) Recovery.SaveNowRequested -= saveNow;
                if (autoTimer != null) { autoTimer.Stop(); autoTimer.Dispose(); }
                frames.Clear();
                if (proxyFile != null) try { File.Delete(proxyFile); } catch { }
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ keys
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        bool Typing
        {
            get
            {
                Control f = null;
                try { f = Control.FromHandle(GetFocus()); }
                catch { Form fm = FindForm(); f = fm == null ? null : fm.ActiveControl; while (f is ContainerControl && ((ContainerControl)f).ActiveControl != null) f = ((ContainerControl)f).ActiveControl; }
                return f is TextBox;
            }
        }

        public bool HandleKey(Keys k)
        {
            if (!HasFile) { if (k == (Keys.Control | Keys.O)) { Browse(false); return true; } return false; }
            bool typing = Typing;
            double frame = 1 / Math.Max(1, P.Fps);
            switch (k)
            {
                case Keys.Space: if (typing) return false; TogglePlay(); return true;
                case Keys.K: if (typing) return false; StopPlay(); return true;
                case Keys.L: if (typing) return false; if (!playing) Play(); return true;
                case Keys.J: if (typing) return false; StopPlay(); Playhead = Math.Max(0, Playhead - 2); Recompose(); return true;
                case Keys.Left: if (typing) return false; StopPlay(); Playhead -= frame; Recompose(); return true;
                case Keys.Right: if (typing) return false; StopPlay(); Playhead += frame; Recompose(); return true;
                case Keys.Shift | Keys.Left: if (typing) return false; StopPlay(); Playhead -= 1; Recompose(); return true;
                case Keys.Shift | Keys.Right: if (typing) return false; StopPlay(); Playhead += 1; Recompose(); return true;
                case Keys.Home: if (typing) return false; StopPlay(); Playhead = 0; Recompose(); return true;
                case Keys.End: if (typing) return false; StopPlay(); Playhead = P.Duration; Recompose(); return true;
                case Keys.S: case Keys.Control | Keys.B: if (typing) return false; Split(); return true;
                case Keys.I: if (typing) return false; TrimToPlayhead(true); return true;
                case Keys.O: if (typing) return false; TrimToPlayhead(false); return true;
                case Keys.Delete: case Keys.Back: if (typing) return false; DeleteSelected(); return true;
                case Keys.Escape: if (playing || proxyRendering) { StopPlay(); return true; } if (timeline.Selected != null) { Select(null); return true; } return false;
                case Keys.Control | Keys.Z: if (typing) return false; Undo(); return true;
                case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: if (typing) return false; Redo(); return true;
                case Keys.Control | Keys.D: if (typing) return false; DuplicateSelected(); return true;
                case Keys.Control | Keys.S: SaveProject(false); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveProject(true); return true;
                case Keys.Control | Keys.E: case Keys.Control | Keys.Shift | Keys.E: Export(); return true;
                case Keys.Control | Keys.O: { string f = PickProject(FindForm()); if (f != null) OpenProject(f, false); return true; }
                case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: timeline.ZoomBy(1.33, timeline.Width / 2); return true;
                case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: timeline.ZoomBy(0.75, timeline.Width / 2); return true;
                case Keys.Control | Keys.D0: timeline.FitAll(); return true;
            }
            return false;
        }
    }

    /// The "add" column: media, text, sound, captions, overlay.
    public class AddRail : UiControl
    {
        public static readonly string[][] Items =
        {
            new[] { "media", Ico.Video, "vs_a_media" },
            new[] { "overlay", Ico.Photo, "vs_a_overlay" },
            new[] { "text", Ico.Text, "vs_a_text" },
            new[] { "audio", Ico.Music, "vs_a_audio" },
            new[] { "captions", "", "vs_a_captions" },
            new[] { "subs", Ico.Doc, "vs_a_subs" },
        };
        public event Action<string> Picked;
        int hover = -1;
        public AddRail() { Cursor = Cursors.Hand; Surface = Surface.Card; }
        int ItemH { get { return Math.Max(Theme.S(30), Math.Min(Theme.S(66), Height / Items.Length)); } }
        bool Labels { get { return ItemH >= Theme.S(50); } }
        Rectangle Cell(int i) { return new Rectangle(0, i * ItemH, Width, ItemH); }
        readonly ToolTip tip = new ToolTip();
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = e.Y / Math.Max(1, ItemH); if (h >= Items.Length) h = -1; if (h != hover) { hover = h; Invalidate(); tip.SetToolTip(this, h >= 0 ? L.T(Items[h][2]) : ""); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); int i = e.Y / Math.Max(1, ItemH); if (i >= 0 && i < Items.Length && Picked != null) Picked(Items[i][0]); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            for (int i = 0; i < Items.Length; i++)
            {
                Rectangle r = Cell(i);
                if (i == hover) Theme.FillRound(g, RectangleF.Inflate(r, -Theme.S(3), -Theme.S(2)), Theme.S(8), Theme.Hover);
                if (!Labels) { Theme.Glyph(g, Items[i][1], 12f, i == hover ? Theme.AccentColor : Theme.InkSoft, r); continue; }
                Theme.Glyph(g, Items[i][1], 12f, i == hover ? Theme.AccentColor : Theme.InkSoft, new Rectangle(r.X, r.Y + Theme.S(6), r.Width, r.Height / 2));
                Theme.Text(g, L.T(Items[i][2]), Theme.F(7.75f, FontStyle.Bold), Theme.InkSoft, new Rectangle(r.X + 2, r.Y + r.Height / 2 + Theme.S(2), r.Width - 4, Theme.S(18)), Align.Center);
            }
        }
        protected override string AccName { get { return L.T("vs_add"); } }
    }

    /// Shapes to start a new video in: wide, tall (Reels / TikTok), square, portrait.
    public class CanvasShapes : UiControl
    {
        static readonly string[][] Items = { new[] { "16:9", "vs_s_wide" }, new[] { "9:16", "vs_s_tall" }, new[] { "1:1", "vs_s_square" }, new[] { "4:5", "vs_s_portrait" }, new[] { "orig", "vs_s_orig" } };
        public event Action<string> Picked;
        int hover = -1;
        public CanvasShapes() { Cursor = Cursors.Hand; Surface = Surface.Card; }
        Rectangle Tile(int i) { int top = Theme.S(28), gap = Theme.S(10), w = (Width - gap * (Items.Length - 1)) / Items.Length; return Theme.Mirror(new Rectangle(i * (w + gap), top, w, Height - top), Width); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = -1; for (int i = 0; i < Items.Length; i++) if (Tile(i).Contains(e.Location)) h = i; if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); for (int i = 0; i < Items.Length; i++) if (Tile(i).Contains(e.Location) && Picked != null) Picked(Items[i][0]); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Theme.Text(g, L.T("vs_start_shape"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(new Rectangle(0, 0, Width, Theme.S(22)), Width), Align.Start);
            for (int i = 0; i < Items.Length; i++)
            {
                Rectangle r = Tile(i);
                bool h = i == hover;
                Theme.FillRound(g, r, Theme.S(10), h ? Theme.Hover : Theme.CardInner);
                Theme.StrokeRound(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(10), h ? Theme.AccentColor : Theme.CardBorder);
                Rectangle art = new Rectangle(r.X + Theme.S(10), r.Y + Theme.S(10), r.Width - Theme.S(20), r.Height - Theme.S(58));
                float aw = 16, ah = 9;
                switch (Items[i][0]) { case "9:16": aw = 9; ah = 16; break; case "1:1": aw = ah = 1; break; case "4:5": aw = 4; ah = 5; break; case "orig": aw = 3; ah = 2; break; }
                float k = Math.Min(art.Width / aw, art.Height / ah) * 0.9f;
                RectangleF s = new RectangleF(art.X + (art.Width - aw * k) / 2, art.Y + (art.Height - ah * k) / 2, aw * k, ah * k);
                Theme.FillRound(g, s, Theme.S(3), Items[i][0] == "orig" ? Theme.Card : Theme.Mix(Theme.Card, Theme.AccentColor, 0.12f));
                Theme.StrokeRound(g, s, Theme.S(3), Items[i][0] == "orig" ? Theme.FieldBorder : Theme.AccentColor);
                if (Items[i][0] == "orig") using (Pen p = new Pen(Theme.FieldBorder) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawRectangle(p, s.X + 3, s.Y + 3, s.Width - 6, s.Height - 6);
                Theme.Text(g, L.T(Items[i][1]), Theme.F(8.75f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(8), r.Bottom - Theme.S(44), r.Width - Theme.S(16), Theme.S(20)), Align.Center);
                Theme.Text(g, Items[i][0] == "orig" ? L.T("vs_s_orig_sub") : Items[i][0], Theme.F(8f), Theme.InkFaint, new Rectangle(r.X + Theme.S(8), r.Bottom - Theme.S(24), r.Width - Theme.S(16), Theme.S(18)), Align.Center, true, false, true);
            }
        }
    }
}
