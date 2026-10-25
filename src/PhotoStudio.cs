using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// What the Studio's file tabs need from what's open in them (a photo being edited, or a document's pages).
    public interface IStudioTab
    {
        string TabTitle { get; }
        string TabGlyph { get; }
        bool HasUnsavedEdits { get; }
        bool HasFile { get; }
        bool Editing { get; }
        bool CloseAll();
        bool ConfirmDiscard();
        bool HandleKey(Keys k);
    }

    /// The photo studio: tools down the left, the photo in the middle, its properties, layers and history on the right.
    /// Opening a photo starts editing it; everything stays in layers until it's exported (or saved as a project).
    public partial class PhotoStudio : Page, IStudioTab
    {
        PhotoDoc doc;
        readonly PhotoCanvas canvas = new PhotoCanvas();
        readonly ToolRail rail = new ToolRail();
        readonly PanelBody props = new PanelBody();
        readonly Segments panelTabs;
        readonly LayersList layers = new LayersList();
        readonly HistoryList history = new HistoryList();
        readonly DropDown blend = new DropDown();
        readonly Slider opacity = new Slider { Min = 0, Max = 100, Value = 100 };
        readonly Btn bUndo, bRedo, bZoomOut, bZoomIn, bZoom, bFit, bMore, bExport, bOpen, bNew;
        readonly Btn lNew, lAdj, lGroup, lDup, lMerge, lDel, lPhoto;
        readonly DropZone emptyDrop;
        readonly CanvasTiles tiles = new CanvasTiles();
        readonly RecentStrip recents = new RecentStrip();
        readonly ToolTip tips = new ToolTip();
        Rectangle bar, railR, panelR, canvasR, headR, layersR, emptyR;
        bool loading;        // filling the controls from the layer: their change events are ignored
        bool opening; string openingName;
        string busyJob;      // a long job is running (remove object, select subject…)

        public event Action<string> OpenProjectFile, SendToConverter;
        public event Action BrowseProjects;
        /// Files dropped or opened here go to the Studio, which decides where they open (a new tab…).
        public Action<string[]> DropTo;

        public PhotoStudio() : base("studio_title", "studio_sub")
        {
            Glyph = Ico.Photo;
            // ---- the canvas
            canvas.BeforeChange = n => { Change(n); return true; };
            canvas.Changed += () => AfterChange(false);
            canvas.ZoomChanged += () => { bZoom.Invalidate(); InvalidateBar(); };
            canvas.TextAt += AddTextAt;
            canvas.Picked += c => { SetFore(c); };
            canvas.RemoveStroke += RemoveObject;
            canvas.ContextMenuAt += CanvasMenu;
            // ---- tools
            rail.Picked += t => SetTool(t);
            rail.ForeClicked += () => { Color c; if (ColorPicker.Pick(FindForm(), canvas.Fore, null, out c)) SetFore(c); };
            rail.BackClicked += () => { Color c; if (ColorPicker.Pick(FindForm(), canvas.Back, null, out c)) { canvas.Back = c; rail.Back = c; rail.Invalidate(); } };
            rail.SwapClicked += SwapColors;
            rail.Fore = canvas.Fore; rail.Back = canvas.Back;
            // ---- the right side: properties on top, layers / history under
            props.Scrolled += LayoutProps;
            props.ExtraPaint = PaintPropsExtra;
            panelTabs = new Segments(() => L.T("ps_layers"), () => L.T("ps_history")) { FontSize = 8.75f };
            panelTabs.Glyphs = new[] { "\uE7C4", "\uE81C" };
            panelTabs.SelectedChanged += (s, e) => { DoLayout(); if (panelTabs.SelectedIndex == 1) history.ShowNow(); };
            layers.Picked += l => PickLayer(l);
            layers.ToggleShown += l => { Change("ps_h_visibility"); l.Hidden = !l.Hidden; l.Version++; AfterChange(true); };
            layers.ToggleLock += l => { Change("ps_h_lock"); l.Locked = !l.Locked; AfterChange(true); };
            layers.ToggleOpen += l => { l.Collapsed = !l.Collapsed; layers.Refresh2(); };
            layers.Renamed += (l, n) => { Change("ps_h_rename"); l.Name = n; AfterChange(true); };
            layers.Moved += (l, parent, at) => { Change("ps_h_order"); LayerTree.Move(doc, l, parent, at); AfterChange(true); };
            layers.Menu += LayerMenu;
            history.Jump += n => { Jump(n); };
            blend.Items = new List<string>();
            foreach (BlendMode m in Enum.GetValues(typeof(BlendMode))) blend.Items.Add(L.T("ps_bl_" + m.ToString().ToLowerInvariant()));
            blend.Selected = blend.Items[0];
            blend.Previewing += v =>
            {
                DocLayer l = doc == null ? null : doc.Cur;
                if (l == null || loading) return;
                BlendMode want = v == null ? (BlendMode)Math.Max(0, blend.Items.IndexOf(blend.Selected)) : (BlendMode)Math.Max(0, blend.Items.IndexOf(v));
                if (l.Blend != want) { l.Blend = want; l.Version++; canvas.Redraw(); }
            };
            blend.SelectedChanged += (s, e) =>
            {
                DocLayer l = doc == null ? null : doc.Cur;
                if (l == null || loading) return;
                BlendMode want = (BlendMode)Math.Max(0, blend.Items.IndexOf(blend.Selected));
                BlendMode was = l.Blend; l.Blend = was;
                Change("ps_h_blend"); l.Blend = want; l.Version++; AfterChange(true);
            };
            opacity.Format = v => ((int)Math.Round(v)) + "%";
            opacity.ValueChanged += (s, e) =>
            {
                DocLayer l = doc == null ? null : doc.Cur;
                if (l == null || loading) return;
                Change("ps_h_opacity", opacity);
                l.Opacity = opacity.Value / 100f; l.Version++;
                canvas.Redraw(); layers.Invalidate();
            };
            opacity.Committed += (s, e) => EndChange();
            lNew = Icon(Ps.NewLayer, "ps_new_layer", () => NewLayer());
            lAdj = Icon(Ps.AdjLayer, "ps_new_adj", () => AdjMenu(lAdj));
            lGroup = Icon(Ico.Folder, "ps_group", () => GroupLayer());
            lDup = Icon(Ico.Copy, "ps_duplicate", () => DuplicateLayer());
            lMerge = Icon(Ps.MergeDown, "ps_merge", () => MergeDown());
            lDel = Icon(Ico.Delete, "ps_delete_layer", () => DeleteLayer());
            lPhoto = Icon(Ico.Photo, "ps_add_photo", () => PickPictureLayer());
            // ---- the top bar
            bUndo = Icon("", "undo_tip", () => Undo());
            bRedo = Icon("", "redo_tip", () => Redo());
            bZoomOut = Icon(Ico.ZoomOut, "ps_zoom_out", () => canvas.ZoomCenter(canvas.Zoom / 1.25f));
            bZoomIn = Icon(Ico.ZoomIn, "ps_zoom_in", () => canvas.ZoomCenter(canvas.Zoom * 1.25f));
            bFit = Icon(Ps.Fit, "ps_fit", () => canvas.Fit());
            bZoom = new Btn(() => doc == null ? "" : (int)Math.Round(canvas.Zoom * 100) + "%", null, BtnKind.Ghost) { FontSize = 9f };
            bZoom.Click += (s, e) => ZoomMenu();
            bZoom.MouseEnter += (s, e) => tips.SetToolTip(bZoom, L.T("ps_zoom"));
            bMore = Icon(Ico.More, "ps_more", () => MoreMenu());
            bExport = new Btn(() => L.T("ps_export"), Ico.Upload, BtnKind.Primary) { FontSize = 9.5f };
            bExport.Click += (s, e) => Export();
            bExport.MouseEnter += (s, e) => tips.SetToolTip(bExport, L.T("ps_export_tip"));
            bOpen = new Btn(() => L.T("v_open"), Ico.FolderOpen, BtnKind.Secondary) { FontSize = 9.5f };
            bOpen.Click += (s, e) => PickFile();
            bNew = new Btn(() => L.T("ps_new_canvas"), Ps.NewCanvas, BtnKind.Secondary) { FontSize = 9.5f };
            bNew.Click += (s, e) => NewCanvasDialog();
            // ---- nothing open: drop something, or start from a blank canvas
            emptyDrop = new DropZone(() => L.T("studio_pdrop"), () => L.T("ps_drop_sub")) { Tips = new[] { "tip_ps_layers", "tip_ps_remove", "tip_studio_pdf", "tip_ocr" } };
            emptyDrop.Glyph = Ico.Photo;
            emptyDrop.Click += (s, e) => PickFile();
            emptyDrop.ExtrasTitle = () => L.T("recent");
            emptyDrop.Extras = () => RecentFiles.Extras("studio", 4, f => AcceptFiles(new[] { f }));
            tiles.Picked += i => { if (i >= CanvasTiles.Presets.Length) NewCanvasDialog(); else { CanvasTiles.Preset p = CanvasTiles.Presets[i]; NewCanvas(p.W, p.H, p.Clear ? Color.Transparent : Color.White, L.T(p.Key)); } };
            recents.Open += f => { if (OpenProjectFile != null) OpenProjectFile(f); };
            recents.Browse += () => { if (BrowseProjects != null) BrowseProjects(); };
            InitProps();
            Controls.AddRange(new Control[] { canvas, rail, props, panelTabs, layers, history, blend, opacity, lNew, lAdj, lGroup, lDup, lMerge, lDel, lPhoto,
                                              bUndo, bRedo, bZoomOut, bZoomIn, bZoom, bFit, bMore, bExport, bOpen, bNew, emptyDrop, tiles, recents });
            EnableDrop(canvas, emptyDrop, layers, tiles);
            // the edit is backed up every minute, and once more if the app crashes
            autoTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            autoTimer.Tick += (s, e) => AutoSave();
            autoTimer.Start();
            saveNow = () => { try { AutoSave(); } catch { } };
            Recovery.SaveNowRequested += saveNow;
            spaceTimer.Tick += (s, e) => { if ((GetAsyncKeyState(0x20) & 0x8000) == 0) { canvas.SpaceHeld = false; spaceTimer.Stop(); } };
            SetTool(PhotoTool.Move);
            UpdateState();
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        readonly System.Windows.Forms.Timer spaceTimer = new System.Windows.Forms.Timer { Interval = 60 };
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

        // ------------------------------------------------------------------ the tab
        public PhotoDoc Doc { get { return doc; } }
        public bool HasFile { get { return doc != null || opening; } }
        public bool Editing { get { return doc != null; } }
        int changes, savedAt;
        public bool HasUnsavedEdits { get { return doc != null && doc.Undo.Count + doc.Redo.Count > 0 && changes != savedAt; } }
        public string TabTitle { get { return doc != null ? doc.Title : opening ? openingName : L.T("tabs_empty"); } }
        public string TabGlyph { get { return doc != null || opening ? Ico.Photo : Ico.Add; } }
        public override bool Busy { get { return opening || busyJob != null; } }

        public bool ConfirmDiscard()
        {
            if (!HasUnsavedEdits) return true;
            return Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("ps_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning);
        }

        public bool CloseAll()
        {
            if (busyJob != null) return false;
            if (!ConfirmDiscard()) return false;
            CloseDoc();
            return true;
        }

        void CloseDoc()
        {
            if (doc != null && Wanted) Recovery.Clear(Recovery.PhotoProject);
            doc = null;
            canvas.Doc = null; layers.Doc = null; history.Doc = null;
            canvas.CropRect = RectangleF.Empty;
            changes = savedAt = 0;
            UpdateState();
            DoLayout();
        }

        void SetDoc(PhotoDoc d)
        {
            doc = d;
            canvas.Doc = d; layers.Doc = d; history.Doc = d;
            canvas.CropRect = RectangleF.Empty;
            changes = savedAt = 0;
            UpdateState();
            DoLayout();
            canvas.Fit();
            Sync();
        }

        // ------------------------------------------------------------------ changes and history
        object editKey; DateTime editAt;
        /// Call before changing the photo: the way it is now goes into the history (a run of changes from one
        /// control — a slider being dragged, a word being typed — is one step).
        void Change(string name, object key = null)
        {
            if (doc == null) return;
            if (key == null || key != editKey || (DateTime.Now - editAt).TotalSeconds > 2.5) doc.Push(name);
            editKey = key; editAt = DateTime.Now;
            changes++;
        }
        void EndChange() { editKey = null; }

        /// Something changed: the picture, the layers list, the history and the buttons catch up.
        int lastLH = -1;
        /// How tall the layers / history part is: the room its rows need (up to about half the panel), the rest is for the settings.
        int LayersH()
        {
            if (doc == null || panelR.Height <= 0) return 0;
            int rows = panelTabs.SelectedIndex == 1 ? (doc.Undo.Count + doc.Redo.Count + 1) * Theme.S(34) + Theme.S(38 + 16) : doc.Layers.Count * Theme.S(44) + Theme.S(38 + 38 + 44 + 16);
            return Math.Max(Theme.S(236), Math.Min(Math.Min(Theme.S(440), (panelR.Height * 48) / 100), rows));
        }
        void AfterChange(bool structure)
        {
            if (doc == null) return;
            if (LayersH() != lastLH) DoLayout();
            canvas.Redraw();
            layers.Refresh2();
            history.Invalidate();
            UpdateUndo();
            InvalidateBar();
            if (structure) { LoadProps(); LayoutProps(); }
            else SyncTransformFields();
        }

        void Sync() { if (doc == null) return; if (LayersH() != lastLH) DoLayout(); layers.Refresh2(); layers.Reveal(); history.Invalidate(); UpdateUndo(); LoadProps(); LayoutProps(); canvas.Redraw(); InvalidateBar(); }

        void UpdateUndo()
        {
            bool u = doc != null && doc.Undo.Count > 0, r = doc != null && doc.Redo.Count > 0;
            if (bUndo.Enabled != u) { bUndo.Enabled = u; bUndo.Invalidate(); }
            if (bRedo.Enabled != r) { bRedo.Enabled = r; bRedo.Invalidate(); }
            DocLayer l = doc == null ? null : doc.Cur;
            lDup.Enabled = lDel.Enabled = l != null;
            lMerge.Enabled = l != null && doc.Current > 0 && l.Kind != LayerKind.Group && doc.Layers[doc.Current - 1].Kind != LayerKind.Group && doc.Layers[doc.Current - 1].Kind != LayerKind.Adjust;
            lGroup.Enabled = l != null;
            foreach (Btn b in new[] { lDup, lDel, lMerge, lGroup }) b.Invalidate();
        }

        public void Undo()
        {
            if (doc == null || busyJob != null) return;
            EndChange();
            if (canvas.Tool == PhotoTool.Crop) canvas.CropRect = RectangleF.Empty;
            if (doc.StepBack()) { changes++; Sync(); }
        }
        public void Redo()
        {
            if (doc == null || busyJob != null) return;
            EndChange();
            if (doc.StepForward()) { changes++; Sync(); }
        }
        void Jump(int n)
        {
            if (doc == null || busyJob != null) return;
            EndChange();
            for (int i = 0; i < Math.Abs(n); i++) if (!(n < 0 ? doc.StepBack() : doc.StepForward())) break;
            changes++;
            Sync();
        }

        void PickLayer(DocLayer l)
        {
            if (doc == null) return;
            int i = doc.Layers.IndexOf(l);
            if (i < 0) return;
            EndChange();
            doc.Current = i;
            layers.Invalidate();
            UpdateUndo();
            LoadProps(); LayoutProps();
            canvas.Invalidate();
        }

        // ------------------------------------------------------------------ tools
        public void SetTool(PhotoTool t)
        {
            if (canvas.Tool == PhotoTool.Crop && t != PhotoTool.Crop) canvas.CropRect = RectangleF.Empty;
            canvas.Tool = t; rail.Tool = t; rail.Invalidate();
            if (t == PhotoTool.Crop && doc != null) canvas.StartCrop();
            canvas.Cursor = Cursors.Default;
            canvas.Invalidate();
            if (props.Width > 0) { props.ResetScroll(); LoadProps(); LayoutProps(); }
            InvalidatePanelHead();
        }

        void SetFore(Color c)
        {
            canvas.Fore = c; rail.Fore = c; rail.Invalidate();
            if (brushColors.Selected != c) { brushColors.Selected = c; brushColors.Invalidate(); }
        }
        void SwapColors() { Color f = canvas.Fore; SetFore(canvas.Back); canvas.Back = f; rail.Back = f; rail.Invalidate(); }

        // ------------------------------------------------------------------ layout
        int RailW { get { return Theme.S(54); } }
        int PanelW { get { return Math.Max(Theme.S(286), Math.Min(Theme.S(340), Width / 4)); } }

        public override void DoLayout()
        {
            if (panelTabs == null) return;
            int W = Width, H = Height, p = Pad;
            cards.Clear();
            bool open = doc != null;
            bar = new Rectangle(p, Top0, W - p * 2, Theme.S(58));
            cards.Add(bar);
            int y = bar.Y + (bar.Height - Theme.S(36)) / 2, h = Theme.S(36), x = bar.Right - Theme.S(12);
            foreach (Control c in new Control[] { bUndo, bRedo, bZoomOut, bZoomIn, bZoom, bFit, bMore, bExport }) c.Visible = open;
            bOpen.Visible = bNew.Visible = !open;
            if (open)
            {
                int ew = Math.Max(Theme.S(104), bExport.PreferredWidth + Theme.S(8));
                x -= ew; Place(bExport, new Rectangle(x, y, ew, h)); x -= Theme.S(6);
                x -= h; Place(bMore, new Rectangle(x, y, h, h)); x -= Theme.S(14);
                x -= h; Place(bFit, new Rectangle(x, y, h, h)); x -= Theme.S(2);
                x -= h; Place(bZoomIn, new Rectangle(x, y, h, h));
                int zw = Theme.S(58); x -= zw; Place(bZoom, new Rectangle(x, y, zw, h));
                x -= h; Place(bZoomOut, new Rectangle(x, y, h, h)); x -= Theme.S(14);
                x -= h; Place(bRedo, new Rectangle(x, y, h, h)); x -= Theme.S(2);
                x -= h; Place(bUndo, new Rectangle(x, y, h, h)); x -= Theme.S(10);
            }
            else
            {
                int nw = Math.Max(Theme.S(120), bNew.PreferredWidth + Theme.S(8)), ow = Math.Max(Theme.S(90), bOpen.PreferredWidth + Theme.S(8));
                x -= nw; Place(bNew, new Rectangle(x, y, nw, h)); x -= Theme.S(8);
                x -= ow; Place(bOpen, new Rectangle(x, y, ow, h)); x -= Theme.S(10);
            }
            nameR = new Rectangle(bar.X + Theme.S(18), bar.Y + Theme.S(9), Math.Max(10, x - bar.X - Theme.S(26)), Theme.S(22));
            infoR = new Rectangle(nameR.X, bar.Y + Theme.S(31), nameR.Width, Theme.S(18));
            int top = bar.Bottom + Theme.S(12), bottom = H - Theme.S(18);
            foreach (Control c in new Control[] { canvas, rail, props, panelTabs, layers, history, blend, opacity, lNew, lAdj, lGroup, lDup, lMerge, lDel, lPhoto }) c.Visible = open;
            emptyDrop.Visible = tiles.Visible = !open;
            if (open)
            {
                railR = new Rectangle(p, top, RailW, bottom - top);
                cards.Add(railR);
                Place(rail, Rectangle.Inflate(railR, -Theme.S(3), -Theme.S(4)));
                panelR = new Rectangle(W - p - PanelW, top, PanelW, bottom - top);
                cards.Add(panelR);
                canvasR = new Rectangle(railR.Right + Theme.S(10), top, panelR.X - Theme.S(10) - railR.Right - Theme.S(10), bottom - top);
                Place(canvas, canvasR);
                // the panel: what's chosen (on top), then layers / history
                int inner = panelR.Width - Theme.S(16), px = panelR.X + Theme.S(8);
                int lh = LayersH(); lastLH = lh;
                headR = new Rectangle(px + Theme.S(6), panelR.Y + Theme.S(10), inner - Theme.S(12), Theme.S(26));
                int propsTop = headR.Bottom + Theme.S(6), propsBottom = panelR.Bottom - lh - Theme.S(6);
                Place(props, new Rectangle(px, propsTop, inner, Math.Max(Theme.S(60), propsBottom - propsTop)));
                layersR = new Rectangle(px, panelR.Bottom - lh, inner, lh - Theme.S(8));
                int ly = layersR.Y;
                Place(panelTabs, new Rectangle(px, ly, inner, Theme.S(32))); ly += Theme.S(38);
                bool showLayers = panelTabs.SelectedIndex == 0;
                layers.Visible = blend.Visible = opacity.Visible = showLayers;
                foreach (Btn b in new[] { lNew, lAdj, lGroup, lDup, lMerge, lDel, lPhoto }) b.Visible = showLayers;
                history.Visible = !showLayers;
                int fb = Theme.S(34);
                if (showLayers)
                {
                    int bw = (inner * 44) / 100;
                    Place(blend, new Rectangle(px, ly, bw, Theme.S(32)));
                    Place(opacity, new Rectangle(px + bw + Theme.S(6), ly, inner - bw - Theme.S(6), Theme.S(32)));
                    ly += Theme.S(38);
                    Place(layers, new Rectangle(px, ly, inner, layersR.Bottom - fb - Theme.S(4) - ly));
                    Btn[] fbs = { lPhoto, lNew, lAdj, lGroup, lDup, lMerge, lDel };
                    int gap = Math.Max(Theme.S(2), (inner - fbs.Length * fb) / (fbs.Length - 1));
                    for (int i = 0; i < fbs.Length; i++) Place(fbs[i], new Rectangle(px + i * (fb + gap), layersR.Bottom - fb, fb, fb));
                    layers.Refresh2();
                }
                else Place(history, new Rectangle(px, ly, inner, layersR.Bottom - ly));
                LayoutProps();
                recents.Visible = false;
            }
            else
            {
                bool rec = VideoStudio.RecentProjects.Count > 0;
                emptyR = new Rectangle(p, top, W - p * 2, bottom - top - (rec ? Theme.S(96) : 0));
                cards.Add(emptyR);
                int th = Theme.S(150);
                Place(emptyDrop, new Rectangle(emptyR.X + Theme.S(20), emptyR.Y + Theme.S(20), emptyR.Width - Theme.S(40), Math.Max(Theme.S(120), emptyR.Height - th - Theme.S(40))));
                Place(tiles, new Rectangle(emptyR.X + Theme.S(20), emptyR.Bottom - th - Theme.S(12), emptyR.Width - Theme.S(40), th));
                List<string> rp = VideoStudio.RecentProjects;
                recents.Paths = rp;
                recents.Visible = rp.Count > 0;
                if (recents.Visible) { Place(recents, new Rectangle(p, H - Theme.S(96), W - p * 2, Theme.S(82))); recents.Invalidate(); }
            }
            Invalidate();
        }

        Rectangle nameR, infoR;
        void InvalidateBar() { if (!nameR.IsEmpty) Invalidate(Rectangle.Union(M(nameR), M(infoR))); }
        void InvalidatePanelHead() { if (!headR.IsEmpty) Invalidate(M(headR)); }

        protected override void DragHighlight(bool on) { emptyDrop.DragHot = on; emptyDrop.Invalidate(); }

        protected override void PaintContent(Graphics g)
        {
            Font nf = Theme.F(10.5f, FontStyle.Bold), inf = Theme.F(8.75f);
            if (doc == null)
            {
                Label(g, opening ? openingName : L.T("ps_none"), nf, Theme.Ink, nameR);
                Label(g, opening ? L.T("v_loading") : L.T("ps_none_sub"), inf, Theme.InkSoft, infoR);
                return;
            }
            Label(g, doc.Title + (HasUnsavedEdits ? "  •" : ""), nf, Theme.Ink, nameR);
            string info = L.T("ps_info", doc.W + " × " + doc.H, doc.Layers.Count);
            if (doc.Sel != null) { Rectangle sb = Selection.Bounds(doc.Sel, doc.W, doc.H); if (!sb.IsEmpty) info += "   ·   " + L.T("ps_info_sel", sb.Width + " × " + sb.Height); }
            if (busyJob != null) info = busyJob;
            Theme.Text(g, info, inf, Theme.InkSoft, M(infoR), Theme.RTL ? Align.End : Align.Start, true, false, true);
            // the panel's heading: the tool, or the layer it's showing
            Theme.Text(g, PanelTitle(), Theme.F(10f, FontStyle.Bold), Theme.Ink, M(headR), Align.Start);
            using (Pen pen = new Pen(Theme.Line)) { Rectangle lr = M(new Rectangle(layersR.X, layersR.Y - Theme.S(6), layersR.Width, 1)); g.DrawLine(pen, lr.X, lr.Y, lr.Right, lr.Y); }
        }

        string PanelTitle()
        {
            PhotoTool t = canvas.Tool;
            DocLayer l = doc == null ? null : doc.Cur;
            bool layerShown = t == PhotoTool.Move || (t == PhotoTool.Text && l != null && l.Kind == LayerKind.Text);
            if (layerShown && l != null) return l.Kind == LayerKind.Adjust ? L.T("ps_adj_" + l.AdjType.ToString().ToLowerInvariant()) : L.T("ps_kind_" + l.Kind.ToString().ToLowerInvariant());
            foreach (ToolRail.Item i in rail.Items) if (i.Tool == t) return L.T(i.NameKey);
            return "";
        }

        public override void Restyle() { canvas.Invalidate(); rail.Invalidate(); layers.Invalidate(); props.Invalidate(); Invalidate(); }
        public override void OnShown() { if (doc != null) canvas.Focus(); }

        // ------------------------------------------------------------------ files
        public override void AcceptFiles(string[] paths) { if (DropTo != null) DropTo(paths); else OpenFiles(paths); }

        /// Opens a photo or a photo project here (this tab must be empty); pictures dropped while editing become layers.
        public void OpenFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            if (doc != null) { AddPictures(paths); return; }
            string f = paths[0];
            if (!File.Exists(f)) return;
            RecentFiles.Add("studio", f);
            if (Formats.Ext(f) == PhotoDoc.ProjectExt) { OpenProject(f, false); return; }
            opening = true; openingName = Path.GetFileName(f);
            UpdateState(); Invalidate();
            Thread t = new Thread(() =>
            {
                Bitmap b = null; string err = null;
                try { using (Image im = ViewerPage.Decode(f, 12000)) b = Pix.Argb(im); }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    opening = false;
                    if (b == null) { UpdateState(); Invalidate(); ShowToast(L.T("v_cant") + (err != null ? " " + err : ""), false, null, null); return; }
                    SetDoc(PhotoDoc.FromPhoto(b, f));
                    b.Dispose();
                });
            });
            t.IsBackground = true; t.Start();
        }

        public void OpenProject(string path, bool restored)
        {
            if (doc != null && !CloseAll()) return;
            try
            {
                PhotoDoc d = PhotoDoc.OpenProject(path);
                if (restored) { d.ProjectPath = null; d.Title = string.IsNullOrEmpty(d.SourcePath) ? L.T("ps_untitled") : Path.GetFileName(d.SourcePath); }
                SetDoc(d);
                if (restored) { changes = 1; savedAt = 0; d.Push("ps_h_restored"); d.Undo.Clear(); d.Undo.Add(d.Snapshot("ps_h_restored")); UpdateUndo(); }
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        public void NewCanvas(int w, int h, Color bg, string title)
        {
            if (doc != null)
            {
                // a new canvas over an open photo goes to a tab of its own
                if (NewCanvasElsewhere != null) { NewCanvasElsewhere(w, h, bg, title); return; }
                if (!CloseAll()) return;
            }
            SetDoc(PhotoDoc.Blank(w, h, bg, title));
        }
        public event Action<int, int, Color, string> NewCanvasElsewhere;

        public void PickFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("v_open");
                List<string> pats = new List<string>();
                foreach (string e in Formats.ImageExt) pats.Add("*." + e);
                d.Filter = L.T("photos_videos") + "|" + string.Join(";", pats.ToArray()) + ";*." + PhotoDoc.ProjectExt + ";*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.m4v;*.mpg;*.3gp;*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.pdf;*.doc;*.docx;*.odt;*.rtf;*.ppt;*.pptx;*.odp|" +
                           L.T("ps_projects") + "|*." + PhotoDoc.ProjectExt + "|" +
                           L.T("documents") + "|*.pdf;*.doc;*.docx;*.odt;*.rtf;*.ppt;*.pptx;*.odp|" +
                           L.T("images") + "|" + string.Join(";", pats.ToArray()) + "|" + L.T("all_files") + "|*.*";
                if (LastDir.Show(d, FindForm(), "studio") == DialogResult.OK) AcceptFiles(new[] { d.FileName });
            }
        }

        // ------------------------------------------------------------------ keys
        bool Typing
        {
            get
            {
                Control f = null;
                try { f = Control.FromHandle(GetFocus()); }
                catch { Form fm = FindForm(); f = fm == null ? null : fm.ActiveControl; while (f is ContainerControl && ((ContainerControl)f).ActiveControl != null) f = ((ContainerControl)f).ActiveControl; }
                return f is TextBox || (layers.Renaming);
            }
        }

        public bool HandleKey(Keys k)
        {
            if (doc == null)
            {
                if (k == (Keys.Control | Keys.O)) { PickFile(); return true; }
                if (k == (Keys.Control | Keys.N)) { NewCanvasDialog(); return true; }
                if (k == (Keys.Control | Keys.V)) { Paste(); return true; }
                return false;
            }
            bool typing = Typing;
            if (busyJob != null) return k == Keys.Escape;
            Keys code = k & Keys.KeyCode, mods = k & Keys.Modifiers;
            if (!typing && mods == Keys.None)
            {
                switch (code)
                {
                    case Keys.V: SetTool(PhotoTool.Move); return true;
                    case Keys.M: SetTool(canvas.Tool == PhotoTool.Marquee ? PhotoTool.Ellipse : PhotoTool.Marquee); return true;
                    case Keys.L: SetTool(PhotoTool.Lasso); return true;
                    case Keys.W: SetTool(PhotoTool.Wand); return true;
                    case Keys.B: SetTool(PhotoTool.Brush); return true;
                    case Keys.E: SetTool(PhotoTool.Eraser); return true;
                    case Keys.J: SetTool(PhotoTool.Remove); return true;
                    case Keys.T: SetTool(PhotoTool.Text); return true;
                    case Keys.C: SetTool(PhotoTool.Crop); return true;
                    case Keys.I: SetTool(PhotoTool.Eyedropper); return true;
                    case Keys.H: SetTool(PhotoTool.Hand); return true;
                    case Keys.Z: SetTool(PhotoTool.Zoom); return true;
                    case Keys.X: SwapColors(); return true;
                    case Keys.D: SetFore(Color.Black); canvas.Back = Color.White; rail.Back = Color.White; rail.Invalidate(); return true;
                    case Keys.OemOpenBrackets: SetBrush(canvas.BrushSize / 1.2f); return true;
                    case Keys.OemCloseBrackets: SetBrush(canvas.BrushSize * 1.2f); return true;
                    case Keys.Space: canvas.SpaceHeld = true; spaceTimer.Start(); return true;
                    case Keys.Enter: if (canvas.Tool == PhotoTool.Crop) { ApplyCrop(); return true; } return false;
                    case Keys.Escape:
                        if (canvas.Tool == PhotoTool.Crop) { SetTool(PhotoTool.Move); return true; }
                        if (doc.Sel != null) { canvas.ApplySel(null, SelOp.New, "ps_h_deselect"); return true; }
                        return false;
                    case Keys.Delete: case Keys.Back: DeleteKey(); return true;
                    case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return canvas.Nudge(k);
                }
            }
            if (!typing && mods == Keys.Shift && (code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down)) return canvas.Nudge(k);
            switch (k)
            {
                case Keys.Control | Keys.Z: if (typing) return false; Undo(); return true;
                case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: if (typing) return false; Redo(); return true;
                case Keys.Control | Keys.A: if (typing) return false; SelectAll(); return true;
                case Keys.Control | Keys.D: if (typing) return false; if (doc.Sel != null) canvas.ApplySel(null, SelOp.New, "ps_h_deselect"); return true;
                case Keys.Control | Keys.Shift | Keys.I: InvertSel(); return true;
                case Keys.Control | Keys.J: LayerViaCopy(); return true;
                case Keys.Control | Keys.E: MergeDown(); return true;
                case Keys.Control | Keys.G: GroupLayer(); return true;
                case Keys.Control | Keys.Shift | Keys.G: Ungroup(); return true;
                case Keys.Control | Keys.D0: canvas.Fit(); return true;
                case Keys.Control | Keys.D1: canvas.ZoomCenter(1f); return true;
                case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: canvas.ZoomCenter(canvas.Zoom * 1.25f); return true;
                case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: canvas.ZoomCenter(canvas.Zoom / 1.25f); return true;
                case Keys.Control | Keys.S: SaveProject(false); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveProject(true); return true;
                case Keys.Control | Keys.Shift | Keys.E: Export(); return true;
                case Keys.Control | Keys.O: PickFile(); return true;
                case Keys.Control | Keys.N: NewCanvasDialog(); return true;
                case Keys.Control | Keys.C: if (typing) return false; CopyPicture(); return true;
                case Keys.Control | Keys.V: if (typing) return false; Paste(); return true;
                case Keys.Shift | Keys.F5: FillSel(); return true;
                case Keys.F2: if (doc.Cur != null) layers.StartRename(doc.Cur); return true;
            }
            return false;
        }

        void SetBrush(float px)
        {
            canvas.BrushSize = Math.Max(1, Math.Min(1000, px));
            loading = true; brushSize.SetValue(canvas.BrushSize, false); loading = false;
            canvas.Invalidate();
        }

        // ------------------------------------------------------------------ backups
        int autoSavedAt = -1;
        void AutoSave()
        {
            if (doc == null || !Wanted || busyJob != null || changes == autoSavedAt || !HasUnsavedEdits) return;
            autoSavedAt = changes;
            SaveInBackground(Recovery.PhotoProject, null);
        }

        /// Writes the project from a copy (so editing carries on while it's written).
        void SaveInBackground(string path, Action<string> done)
        {
            PhotoDoc copy;
            try { copy = doc.CopyForSave(); } catch (Exception ex) { if (done != null) done(ex.Message); return; }
            Thread t = new Thread(() =>
            {
                string err = null;
                try { Directory.CreateDirectory(Path.GetDirectoryName(path)); copy.SaveProject(path); }
                catch (Exception ex) { err = ex.Message; }
                copy.DisposePixels();
                if (done != null) Ui(() => done(err));
            });
            t.IsBackground = true; t.Start();
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (saveNow != null) Recovery.SaveNowRequested -= saveNow;
                if (autoTimer != null) { autoTimer.Stop(); autoTimer.Dispose(); }
                spaceTimer.Stop(); spaceTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        void UpdateState()
        {
            bool open = doc != null;
            if (!open) { emptyDrop.Visible = true; tiles.Visible = true; }
            UpdateUndo();
        }
    }

    /// Blank canvases to start from (sizes for common places), plus "Custom size…".
    public class CanvasTiles : UiControl
    {
        public class Preset { public string Key; public int W, H; public bool Clear; }
        public static readonly Preset[] Presets =
        {
            new Preset { Key = "ps_c_post", W = 1080, H = 1080 },
            new Preset { Key = "ps_c_story", W = 1080, H = 1920 },
            new Preset { Key = "ps_c_thumb", W = 1280, H = 720 },
            new Preset { Key = "ps_c_wall", W = 1920, H = 1080 },
            new Preset { Key = "ps_c_sticker", W = 512, H = 512, Clear = true },
            new Preset { Key = "ps_c_a4", W = 2480, H = 3508 },
        };
        public event Action<int> Picked;
        int hover = -1;
        public CanvasTiles() { Cursor = Cursors.Hand; Surface = Surface.Card; TabStop = true; }
        int N { get { return Presets.Length + 1; } }
        Rectangle Tile(int i)
        {
            int top = Theme.S(28), gap = Theme.S(10);
            int w = Math.Max(Theme.S(90), (Width - gap * (N - 1)) / N);
            return Theme.Mirror(new Rectangle(i * (w + gap), top, w, Height - top), Width);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1; for (int i = 0; i < N; i++) if (Tile(i).Contains(e.Location)) h = i;
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < N; i++) if (Tile(i).Contains(e.Location) && Picked != null) { Picked(i); return; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Theme.Text(g, L.T("ps_start_blank"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(new Rectangle(0, 0, Width, Theme.S(22)), Width), Align.Start);
            for (int i = 0; i < N; i++)
            {
                Rectangle r = Tile(i);
                bool h = i == hover;
                Theme.FillRound(g, r, Theme.S(10), h ? Theme.Hover : Theme.CardInner);
                Theme.StrokeRound(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(10), h ? Theme.AccentColor : Theme.CardBorder);
                // the shape of the canvas, to scale
                Rectangle art = new Rectangle(r.X + Theme.S(10), r.Y + Theme.S(10), r.Width - Theme.S(20), r.Height - Theme.S(58));
                if (i < Presets.Length)
                {
                    Preset p = Presets[i];
                    float k = Math.Min(art.Width / (float)p.W, art.Height / (float)p.H);
                    RectangleF s = new RectangleF(art.X + (art.Width - p.W * k) / 2, art.Y + (art.Height - p.H * k) / 2, p.W * k, p.H * k);
                    Theme.FillRound(g, s, Theme.S(3), p.Clear ? Theme.Track : Theme.Card);
                    Theme.StrokeRound(g, s, Theme.S(3), Theme.FieldBorder);
                    Theme.Text(g, L.T(p.Key), Theme.F(8.75f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(8), r.Bottom - Theme.S(44), r.Width - Theme.S(16), Theme.S(20)), Align.Center);
                    Theme.Text(g, p.W + " × " + p.H, Theme.F(8f), Theme.InkFaint, new Rectangle(r.X + Theme.S(8), r.Bottom - Theme.S(24), r.Width - Theme.S(16), Theme.S(18)), Align.Center, true, false, true);
                }
                else
                {
                    Theme.Glyph(g, Ico.Add, 14f, h ? Theme.AccentColor : Theme.InkSoft, art);
                    Theme.Text(g, L.T("ps_c_custom"), Theme.F(8.75f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(8), r.Bottom - Theme.S(44), r.Width - Theme.S(16), Theme.S(20)), Align.Center);
                    Theme.Text(g, L.T("ps_c_custom_sub"), Theme.F(8f), Theme.InkFaint, new Rectangle(r.X + Theme.S(8), r.Bottom - Theme.S(24), r.Width - Theme.S(16), Theme.S(18)), Align.Center);
                }
            }
        }
    }
}
