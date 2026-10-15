using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    public enum EditorMode { Photo, Video, Audio }
    enum EdTab { Trim, Text, Resize, Background, Effects }

    /// Side panel with the editing tools. Photos: Text, Resize, Background. Videos: Trim, Text, Resize.
    public class EditorPanel : UiControl
    {
        public readonly EditSession S = new EditSession();
        ImageCanvas canvas;
        public ImageCanvas Canvas
        {
            get { return canvas; }
            set
            {
                canvas = value;
                if (canvas != null)
                {
                    canvas.Stroke += OnStroke; canvas.WandClick += OnWand;
                    canvas.LayerResized += () =>
                    {
                        if (S.Current == null) return;
                        S.Dirty = true;
                        if (S.Current.SizePx > size.Max) size.Max = S.Current.SizePx;
                        loading = true; size.SetValue(S.Current.SizePx, false); loading = false;
                        if (!S.Current.IsPicture) style.SizePx = S.Current.SizePx;
                        if (Changed != null) Changed();
                    };
                }
            }
        }
        public EditorMode Mode = EditorMode.Photo;
        public event Action Changed;                 // layers or size changed (page refreshes its info line)
        public event Action CloseRequested;
        public Action<string, bool, string> Notify;   // message, ok, saved file

        // video pages put their trim controls and footer in here
        public Func<int, int, int> TrimLayout;       // (y, contentWidth) -> y after the trim rows
        public Action<Graphics> TrimPaint;
        public Func<int, int, int> EffectsLayout;
        public Action<Graphics> EffectsPaint;
        readonly List<Control> fxCtl = new List<Control>();
        public void AddToEffects(params Control[] cs) { body.Controls.AddRange(cs); fxCtl.AddRange(cs); }
        public event Action TabChanged;
        public Func<int> FooterLayout;               // returns the top of the footer area
        public Action<Graphics> FooterPaint;
        // video: when the selected text shows (placed under the text box)
        public Func<int, int, int> TextTimingLayout;
        public Action<Graphics> TextTimingPaint;
        readonly List<Control> timingCtl = new List<Control>();
        public void AddToTiming(params Control[] cs) { body.Controls.AddRange(cs); timingCtl.AddRange(cs); }
        public event Action<int> LayerSelected;
        public Action<TextLayer> PrepareNewText;     // video: new text starts at the playhead

        readonly PanelBody body = new PanelBody();
        readonly Segments tabs, fmt, bgMode;
        readonly Btn bAdd, bDel, bApply, bSave, bDone, bBgRun, bUndo, bRedo;
        readonly MultiField text = new MultiField();
        readonly DropDown font = new DropDown { PreviewFonts = true };
        readonly Slider size = new Slider(), opacity = new Slider { Min = 10, Max = 100, Value = 100 }, blur = new Slider { Min = 0, Max = 100, Value = 40 };
        readonly Swatches colors = new Swatches(), bgColors = new Swatches();
        readonly ChipToggles styles;
        readonly PositionPad position = new PositionPad();
        readonly TextField fw, fh;
        readonly Toggle keep = new Toggle();
        readonly ChipButtons presets = new ChipButtons("25%", "50%", "75%", "100%", "200%", "480p", "720p", "1080p", "1440p", "4K");
        readonly Progress bgProgress = new Progress();
        readonly Swatches boxColors = new Swatches();
        // outline / photo frame, turning, photos on top
        readonly Swatches outColors = new Swatches();
        readonly Slider outWidth = new Slider { Min = 0.3f, Max = 3f, Value = 1f }, rotate = new Slider { Min = -180, Max = 180, Value = 0 };
        readonly ChipToggles picStyles;
        readonly Btn bPic;
        System.Windows.Forms.Timer crisp;
        // fixing the background by hand
        readonly Segments bgTool;
        readonly Slider brush = new Slider { Min = 6, Max = 200, Value = 44 }, tolerance = new Slider { Min = 1, Max = 100, Value = 22 };
        readonly Toggle everywhere = new Toggle();
        Rectangle bgManualR, bgToolHintR, everywhereR;
        readonly ChipButtons cropPresets = new ChipButtons("Free", "1:1", "4:5", "16:9", "9:16", "3:2") { Columns = 6 };
        readonly Btn bCropApply = new Btn(() => L.T("ed_crop_apply"), Ico.Check, BtnKind.Primary), bCropCancel = new Btn(() => L.T("cancel"), Ico.Close, BtnKind.Secondary),
                     bCropReset = new Btn(() => L.T("ed_crop_reset"), Ico.Refresh, BtnKind.Secondary), bFonts = new Btn(() => L.T("fonts_get_arabic"), Ico.Download, BtnKind.Ghost);
        Rectangle cropLine;
        bool fontsBusy;
        readonly ToolTip tips = new ToolTip();
        TextLayer style = new TextLayer();           // style used for the next new text
        bool loading, saving, bgBusy, previewingFont, aiMask;
        string bgStatus = "";
        Rectangle footerLine, saveLabel, infoRow, hintRow, bgInfoRow, bgHintRow;
        EdTab[] tabList = { EdTab.Text, EdTab.Resize, EdTab.Background };

        public static readonly string[] FontChoices = {
            "Segoe UI", "Segoe UI Black", "Segoe UI Semibold", "Segoe UI Light", "Arial", "Arial Black", "Impact", "Bahnschrift",
            "Calibri", "Cambria", "Candara", "Comic Sans MS", "Consolas", "Constantia", "Corbel", "Courier New", "Franklin Gothic Medium",
            "Gabriola", "Georgia", "Ink Free", "Lucida Console", "Palatino Linotype", "Segoe Print", "Segoe Script", "Sitka Text",
            "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana",
            "Sakkal Majalla", "Traditional Arabic", "Simplified Arabic", "Arabic Typesetting", "Aldhabi", "Andalus", "Urdu Typesetting", "Microsoft Uighur" };

        public EditorPanel()
        {
            Surface = Surface.Card;
            tabs = new Segments(() => L.T("ed_layers"), () => L.T("ed_resize"), () => L.T("ed_bg"));
            tabs.Glyphs = tabGlyphs = new[] { Ico.FontGlyph, Ico.Resize, Ico.Photo };
            tabs.FontSize = 9f;
            tabs.SelectedChanged += (s, e) => { if (CurrentTab != EdTab.Resize) CancelCrop(); SyncBrush(); body.ResetScroll(); DoLayout(); body.Invalidate(); if (TabChanged != null) TabChanged(); };
            bUndo = new Btn(() => "", "\uE7A7", BtnKind.Secondary) { Enabled = false };
            bUndo.Click += (s, e) => Undo();
            bUndo.MouseEnter += (s, e) => tips.SetToolTip(bUndo, L.T("undo_tip"));
            bRedo = new Btn(() => "", "\uE7A6", BtnKind.Secondary) { Enabled = false };
            bRedo.Click += (s, e) => Redo();
            bRedo.MouseEnter += (s, e) => tips.SetToolTip(bRedo, L.T("redo_tip"));
            bAdd = new Btn(() => L.T("ed_add"), Ico.Add, BtnKind.Primary); bAdd.FontSize = 9.5f;
            bAdd.Click += (s, e) => AddText();
            bDel = new Btn(() => "", Ico.Delete, BtnKind.Secondary);
            bDel.Click += (s, e) => DeleteSelected();
            bDel.MouseEnter += (s, e) => tips.SetToolTip(bDel, L.T("ed_delete"));

            text.Box.TextChanged += (s, e) =>
            {
                if (loading || S.Current == null) return;
                string t = text.Box.Text;
                string fixedT = Crlf(t);
                if (fixedT != t)
                {
                    // pasted text with bare line feeds: show them as real line breaks
                    int caret = text.Box.SelectionStart + (fixedT.Length - t.Length);
                    loading = true; text.Box.Text = fixedT; loading = false;
                    text.Box.SelectionStart = Math.Max(0, Math.Min(fixedT.Length, caret));
                }
                S.Current.Text = fixedT; Touch(true);
            };

            font.Items = AllFonts(out font.Favorites);
            font.Selected = font.Items.Contains("Segoe UI") ? "Segoe UI" : (font.Items.Count > 0 ? font.Items[0] : "Segoe UI");
            style.FontName = font.Selected;
            font.SelectedChanged += (s, e) => { style.FontName = font.Selected; Apply(l => l.FontName = font.Selected, true); };
            // hovering a font in the list shows it on the picture right away; leaving the list puts the chosen one back
            font.Previewing += v =>
            {
                TextLayer l = S.Current;
                if (l == null || loading) return;
                previewingFont = v != null;
                string want = v ?? font.Selected;
                if (l.FontName == want) return;
                l.FontName = want; l.Touch();
                if (Canvas != null) Canvas.Invalidate();
            };

            size.Format = v => ((int)Math.Round(v)) + " px";
            size.Curved = true;
            // dragging: the text is stretched on screen right away, and drawn sharp once the hand stops
            size.ValueChanged += (s, e) =>
            {
                if (S.Current == null || !S.Current.IsPicture) style.SizePx = size.Value;
                if (loading || S.Current == null) return;
                S.Current.SizePx = size.Value; S.Current.SizeChanging();
                Touch(false); Crisp();
            };
            size.Committed += (s, e) => Sharpen();
            bPic = new Btn(() => L.T("ed_add_photo"), Ico.Photo, BtnKind.Secondary); bPic.FontSize = 9.5f;
            bPic.Click += (s, e) => AddPicture();
            bPic.MouseEnter += (s, e) => tips.SetToolTip(bPic, L.T("ed_add_photo_tip"));
            outColors.Selected = Color.Black;
            outColors.SelectedChanged += (s, e) => { if (S.Current == null || !S.Current.IsPicture) style.OutlineColor = outColors.Selected; Apply(l => l.OutlineColor = outColors.Selected, true); };
            outWidth.Format = v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "×";
            outWidth.ValueChanged += (s, e) => { if (S.Current == null || !S.Current.IsPicture) style.OutlineWidth = outWidth.Value; Apply(l => l.OutlineWidth = outWidth.Value, true); };
            rotate.Format = v => ((int)Math.Round(v)) + "°";
            rotate.ValueChanged += (s, e) => { float a = (float)Math.Round(rotate.Value); if (Math.Abs(a) < 2) a = 0; Apply(l => l.Angle = a, true); };
            picStyles = new ChipToggles(new Func<string>[] { () => L.T("ed_border"), () => L.T("ed_shadow") }, new string[] { null, null });
            picStyles.Changed += (s, e) => { bool[] o = picStyles.On; Apply(l => { l.Outline = o[0]; l.Shadow = o[1]; }, true); DoLayout(); };
            opacity.Format = v => ((int)Math.Round(v)) + "%";
            opacity.ValueChanged += (s, e) => { style.Opacity = opacity.Value / 100f; Apply(l => l.Opacity = opacity.Value / 100f, false); };
            colors.Selected = Color.White;
            colors.SelectedChanged += (s, e) => { style.Color = colors.Selected; Apply(l => l.Color = colors.Selected, true); };

            styles = new ChipToggles(new Func<string>[] { () => "", () => "", () => L.T("ed_outline"), () => L.T("ed_shadow"), () => L.T("ed_box") },
                                     new[] { Ico.Bold, Ico.Italic, null, null, null });
            styles.On = new[] { true, false, true, true, false };
            styles.Changed += (s, e) =>
            {
                bool[] o = styles.On;
                style.Bold = o[0]; style.Italic = o[1]; style.Outline = o[2]; style.Shadow = o[3]; style.Box = o[4];
                Apply(l => { l.Bold = o[0]; l.Italic = o[1]; l.Outline = o[2]; l.Shadow = o[3]; l.Box = o[4]; }, true);
            };

            position.Picked += MoveTo;
            boxColors.Selected = Color.Black;
            boxColors.SelectedChanged += (s, e) => { style.BoxColor = boxColors.Selected; Apply(l => l.BoxColor = boxColors.Selected, true); };
            styles.Changed += (s, e) => DoLayout();   // show/hide the box color row
            cropPresets.Picked += i => StartCrop(new[] { 0f, 1f, 4f / 5f, 16f / 9f, 9f / 16f, 3f / 2f }[i]);
            bCropApply.FontSize = 9.5f;
            bCropApply.Click += (s, e) => ApplyCrop();
            bCropCancel.Click += (s, e) => CancelCrop();
            bCropReset.Click += (s, e) => { S.ResetCrop(); AfterGeometry(); };
            bFonts.FontSize = 9f;
            bFonts.Click += (s, e) => GetArabicFonts();
            font.ActionLabel = () => FontPack.Ready ? null : L.T("fonts_get_arabic");
            font.Action = GetArabicFonts;
            font.Extra = FontPack.Families;
            font.ExtraHeader = () => L.T("fonts_arabic");
            position.MouseEnter += (s, e) => tips.SetToolTip(position, L.T("ed_position_tip"));

            fw = new TextField(() => "", null) { ForceLtr = true };
            fh = new TextField(() => "", null) { ForceLtr = true };
            fw.Box.TextChanged += (s, e) => SizeTyped(true);
            fh.Box.TextChanged += (s, e) => SizeTyped(false);
            foreach (TextField f in new[] { fw, fh })
            {
                f.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
                f.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplySize(); } };
            }
            keep.SetSilently(true);
            presets.Picked += Preset;
            bApply = new Btn(() => L.T("ed_apply_size"), Ico.Check, BtnKind.Secondary);
            bApply.Click += (s, e) => ApplySize();

            bBgRun = new Btn(() => BgRemover.Ready ? (aiMask ? L.T("bg_again") : L.T("bg_remove")) : L.T("bg_download"), Ico.Photo, BtnKind.Primary);
            bBgRun.FontSize = 9.5f;
            bBgRun.Click += (s, e) => RemoveBackground();
            bgMode = new Segments(() => L.T("bg_original"), () => L.T("bg_transparent"), () => L.T("bg_color"), () => L.T("bg_blur"));
            bgMode.FontSize = 8.5f;
            bgMode.SelectedChanged += (s, e) => SetBgMode(bgMode.SelectedIndex);
            bgColors.Selected = Color.White;
            bgColors.SelectedChanged += (s, e) => { S.BgColor = bgColors.Selected; if (S.BgMode == 2) RefreshBackground(); };
            blur.Format = v => ((int)Math.Round(v)) + "%";
            blur.Committed += (s, e) => { S.BgBlur = blur.Value; if (S.BgMode == 3) RefreshBackground(); };
            bgProgress.Visible = false;
            bgTool = new Segments(() => L.T("bg_t_off"), () => L.T("bg_t_erase"), () => L.T("bg_t_keep"), () => L.T("bg_t_wand"));
            bgTool.FontSize = 8.5f;
            bgTool.SelectedChanged += (s, e) => SetBgTool(bgTool.SelectedIndex);
            brush.Format = v => ((int)Math.Round(v)) + " px";
            brush.ValueChanged += (s, e) => { if (Canvas != null) { Canvas.BrushPx = Theme.S(brush.Value); Canvas.Invalidate(); } };
            tolerance.Format = v => ((int)Math.Round(v)) + "%";

            fmt = new Segments(() => "JPG", () => "JPEG", () => "PNG", () => "WEBP", () => "PDF");
            fmt.FontSize = 8.75f;
            int fi = Array.IndexOf(SaveExts, Settings.Get("EditFormat", "jpg"));
            fmt.SetSilently(Math.Max(0, fi));
            fmt.SelectedChanged += (s, e) => Settings.Set("EditFormat", SaveExts[fmt.SelectedIndex]);
            bSave = new Btn(() => L.T("ed_save"), Ico.Save, BtnKind.Primary); bSave.FontSize = 9.5f;
            bSave.Click += (s, e) => Save();
            bDone = new Btn(() => L.T("ed_done"), Ico.Close, BtnKind.Secondary);
            bDone.Click += (s, e) => { if (CloseRequested != null) CloseRequested(); };

            body.Controls.AddRange(new Control[] { boxColors, cropPresets, bCropApply, bCropCancel, bCropReset, bFonts, bgTool, brush, tolerance, everywhere });
            body.Controls.AddRange(new Control[] { bUndo, bRedo });
            body.Controls.AddRange(new Control[] { bPic, outColors, outWidth, rotate, picStyles });
            body.Controls.AddRange(new Control[] { tabs, bAdd, bDel, text, font, size, colors, styles, position, opacity, fw, fh, keep, presets, bApply, bBgRun, bgMode, bgColors, blur, bgProgress });
            body.Scrolled += DoLayout;
            body.ExtraPaint = PaintBodyExtras;
            Controls.AddRange(new Control[] { body, fmt, bSave, bDone });
        }

        static readonly string[] SaveExts = { "jpg", "jpeg", "png", "webp", "pdf" };
        bool KeepsAlpha { get { return fmt.SelectedIndex == 2 || fmt.SelectedIndex == 3; } }
        void PreferAlpha() { if (!KeepsAlpha) fmt.SelectedIndex = 2; }

        static string Crlf(string t) { return (t ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n"); }

        static List<string> fontCache, favCache;
        /// Every installed font (alphabetical) plus the nice ones to show first.
        public static List<string> AllFonts(out List<string> favorites)
        {
            if (fontCache == null)
            {
                List<string> all = new List<string>();
                try
                {
                    using (InstalledFontCollection ifc = new InstalledFontCollection())
                        foreach (FontFamily f in ifc.Families) if (f.IsStyleAvailable(FontStyle.Regular) || f.IsStyleAvailable(FontStyle.Bold)) all.Add(f.Name);
                }
                catch { }
                all.Sort(StringComparer.OrdinalIgnoreCase);
                HashSet<string> have = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
                favCache = new List<string>();
                foreach (string f in FontChoices) if (have.Contains(f)) favCache.Add(f);
                if (all.Count == 0) all.Add("Segoe UI");
                fontCache = all;
            }
            favorites = favCache;
            return fontCache;
        }

        /// The font the Text tab is using (subtitles borrow it).
        public string TextFont { get { return S.Current != null ? S.Current.FontName : style.FontName; } }

        public bool TextFocused { get { return text.Box.Focused || fw.Box.Focused || fh.Box.Focused || (TrimFocused != null && TrimFocused()); } }
        public Func<bool> TrimFocused;

        // ------------------------------------------------------------------ modes & session
        public void SetMode(EditorMode m)
        {
            Mode = m;
            if (m == EditorMode.Photo)
            {
                tabList = new[] { EdTab.Text, EdTab.Resize, EdTab.Background };
                tabs.Items = new Func<string>[] { () => L.T("ed_layers"), () => L.T("ed_resize"), () => L.T("ed_bg") };
                tabs.Glyphs = tabGlyphs = new[] { Ico.FontGlyph, Ico.Resize, Ico.Photo };
            }
            else if (m == EditorMode.Video)
            {
                tabList = new[] { EdTab.Trim, EdTab.Text, EdTab.Effects, EdTab.Resize };
                tabs.Items = new Func<string>[] { () => L.T("ed_trim"), () => L.T("ed_text"), () => L.T("ed_effects"), () => L.T("ed_resize") };
                tabs.Glyphs = tabGlyphs = new[] { Ico.Cut, Ico.FontGlyph, "\uE945", Ico.Resize };
            }
            else
            {
                tabList = new[] { EdTab.Trim };
                tabs.Items = new Func<string>[] { () => L.T("ed_trim") };
                tabs.Glyphs = tabGlyphs = new[] { Ico.Cut };
            }
            tabs.SetSilently(0);
            bool photo = m == EditorMode.Photo;
            fmt.Visible = bSave.Visible = bDone.Visible = photo;
            DoLayout();
        }

        EdTab CurrentTab { get { return tabList[Math.Max(0, Math.Min(tabList.Length - 1, tabs.SelectedIndex))]; } }

        public void SelectTab(EdTabPublic t)
        {
            EdTab want = t == EdTabPublic.Text ? EdTab.Text : t == EdTabPublic.Trim ? EdTab.Trim : t == EdTabPublic.Resize ? EdTab.Resize : t == EdTabPublic.Effects ? EdTab.Effects : EdTab.Background;
            int i = Array.IndexOf(tabList, want);
            if (i >= 0 && i != tabs.SelectedIndex) tabs.SelectedIndex = i;
        }

        public void Begin(Bitmap original, string path)
        {
            SetMode(EditorMode.Photo);
            S.BeginPhoto(original, path);
            bgMode.SetSilently(0); bgStatus = "";
            bgTool.SetSilently(0); SyncBrush(); aiMask = false;
            AfterBegin(true);
        }

        public void BeginVideo(int w, int h, string path, bool audioOnly)
        {
            SetMode(audioOnly ? EditorMode.Audio : EditorMode.Video);
            S.BeginVideo(Math.Max(2, w), Math.Max(2, h), path);
            AfterBegin(false);
        }

        void AfterBegin(bool addText)
        {
            ResetHistory();
            body.ResetScroll();
            style.SizePx = Math.Max(12, (int)(S.ShortSide * 0.08f));
            style.Opacity = opacity.Value / 100f;
            SyncCanvas();
            UpdateRanges();
            SetSizeFields(S.W, S.H);
            Select(-1);
            if (addText) AddText();
            DoLayout();
        }

        public void End()
        {
            S.End();
            if (Canvas != null) { Canvas.Layers = null; Canvas.SelectedLayer = -1; }
        }

        /// Video: show a new frame (the editor owns it afterwards).
        public void SetFrame(Bitmap frame)
        {
            S.SetFrame(frame);
            if (Canvas != null) { Canvas.Image = S.Preview; Canvas.Invalidate(); }
        }

        public void SyncCanvas()
        {
            if (Canvas == null) return;
            Canvas.Image = S.Preview;
            Canvas.Logical = new SizeF(S.W, S.H);
            Canvas.Layers = S.Layers;
            Canvas.SelectedLayer = S.Selected;
            Canvas.ImageChanged();
        }

        void UpdateRanges()
        {
            TextLayer c = S.Current;
            if (c != null && c.IsPicture)
            {
                // a photo: from small up to bigger than the picture underneath
                size.Min = Math.Max(8, S.ShortSide / 40f);
                size.Max = Math.Max(size.Min + 10, Math.Max(S.W, S.H) * 1.6f);
            }
            else
            {
                size.Min = Math.Max(6, S.ShortSide / 100);
                size.Max = Math.Max(size.Min + 10, S.ShortSide * 0.5f);
            }
            if (c != null && c.SizePx > size.Max) size.Max = c.SizePx;
            size.Invalidate();
        }

        /// Draws the selected layer sharp again a moment after its size stopped changing.
        void Crisp()
        {
            if (crisp == null) { crisp = new System.Windows.Forms.Timer { Interval = 160 }; crisp.Tick += (s, e) => { crisp.Stop(); Sharpen(); }; }
            crisp.Stop(); crisp.Start();
        }
        void Sharpen()
        {
            if (crisp != null) crisp.Stop();
            if (S.Current != null) S.Current.Touch();
            if (Canvas != null) Canvas.Invalidate();
        }

        /// Puts a photo on top of the picture (or video), like a sticker you can move, size and turn.
        void AddPicture()
        {
            if (!S.Active) return;
            string path;
            using (OpenFileDialog d = new OpenFileDialog { Title = L.T("ed_add_photo"), Filter = L.T("ed_photo_files") + "|*.jpg;*.jpeg;*.jfif;*.png;*.webp;*.heic;*.heif;*.avif;*.bmp;*.gif;*.tif;*.tiff|" + L.T("all_files") + "|*.*" })
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                path = d.FileName;
            }
            AddPicture(path);
        }

        public void AddPicture(string path)
        {
            if (!S.Active) return;
            Bitmap pic;
            Cursor was = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { pic = TextLayer.LoadPicture(path); }
            catch (Exception ex) { if (Notify != null) Notify(L.T("ed_photo_failed") + " " + ex.Message, false, null); return; }
            finally { Cursor.Current = was; }
            // half the picture underneath at most (never blown up)
            float k = Math.Min(1f, Math.Min(S.W * 0.5f / pic.Width, S.H * 0.5f / pic.Height));
            TextLayer t = new TextLayer
            {
                Picture = pic, PicturePath = Path.GetFullPath(path), Text = "",
                SizePx = Math.Max(16, Math.Max(pic.Width, pic.Height) * k),
                Outline = false, Shadow = false, Box = false, Opacity = 1f,
                Pos = new PointF(S.W / 2f, S.H / 2f), From = 0, To = -1
            };
            if (PrepareNewText != null) PrepareNewText(t);
            S.Layers.Add(t);
            S.Dirty = true;
            Select(S.Layers.Count - 1);
            SelectTab(EdTabPublic.Text);
            if (Changed != null) Changed();
        }

        static bool IsLightColor(Color c) { return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 150; }

        void Touch(bool look)
        {
            if (S.Current != null && look) S.Current.Touch();
            S.Dirty = true;
            if (Canvas != null) Canvas.Invalidate();
            if (Changed != null) Changed();
        }

        void Apply(Action<TextLayer> a, bool look)
        {
            if (loading || S.Current == null) return;
            a(S.Current);
            Touch(look);
        }

        void AddText()
        {
            if (!S.Active) return;
            TextLayer t = style.CloneStyle();
            t.Picture = null; t.PicturePath = null; t.Angle = 0;
            t.Text = L.T("ed_default");
            // stack new texts down the picture so they don't sit on top of each other
            float step = S.H * 0.14f;
            float y = S.H / 2f + (S.Layers.Count % 5) * step;
            if (y > S.H * 0.9f) y = S.H * 0.2f;
            t.Pos = new PointF(S.W / 2f, y);
            t.From = 0; t.To = -1;
            if (PrepareNewText != null) PrepareNewText(t);
            S.Layers.Add(t);
            S.Dirty = true;
            Select(S.Layers.Count - 1);
            SelectTab(EdTabPublic.Text);
            text.Box.Focus();
            text.Box.SelectAll();
            if (Changed != null) Changed();
        }

        void DeleteSelected()
        {
            if (S.Current == null) return;
            S.Current.Touch();
            S.Layers.RemoveAt(S.Selected);
            S.Dirty = true;
            Select(S.Layers.Count - 1);
            if (Changed != null) Changed();
        }

        public bool DeleteKey() { if (S.Current == null) return false; DeleteSelected(); return true; }

        public void Nudge(int dx, int dy)
        {
            if (S.Current == null) return;
            float step = Math.Max(1, S.ShortSide / 200f);
            S.Current.Pos = new PointF(S.Current.Pos.X + dx * step, S.Current.Pos.Y + dy * step);
            S.Dirty = true;
            if (Canvas != null) Canvas.Invalidate();
        }

        /// Moves the selected text to an edge or the middle of the picture (0-2 horizontal, 3-5 vertical).
        void MoveTo(int i)
        {
            TextLayer l = S.Current;
            if (l == null) return;
            if (i <= 2)
            {
                StringAlignment a = i == 0 ? StringAlignment.Near : i == 1 ? StringAlignment.Center : StringAlignment.Far;
                if (l.Align != a) { l.Align = a; style.Align = a; l.Touch(); }
            }
            RectangleF b = l.Bounds;
            float m = S.ShortSide * 0.04f;
            float x = l.Pos.X, y = l.Pos.Y;
            switch (i)
            {
                case 0: x += m - b.Left; break;
                case 1: x = S.W / 2f; break;
                case 2: x += (S.W - m) - b.Right; break;
                case 3: y += m - b.Top; break;
                case 4: y = S.H / 2f; break;
                case 5: y += (S.H - m) - b.Bottom; break;
            }
            if (Canvas != null) Canvas.AnimateLayerTo(l, new PointF(x, y));
            else l.Pos = new PointF(x, y);
            S.Dirty = true;
            if (Changed != null) Changed();
        }

        public bool Deselect() { if (S.Selected < 0) return false; Select(-1); return true; }

        public void FocusText() { SelectTab(EdTabPublic.Text); text.Box.Focus(); text.Box.SelectAll(); }

        /// Selects a layer (from the canvas or after add/delete) and loads its style into the controls.
        public void Select(int i)
        {
            S.Selected = i >= 0 && i < S.Layers.Count ? i : -1;
            if (Canvas != null) { Canvas.SelectedLayer = S.Selected; Canvas.Invalidate(); }
            if (LayerSelected != null) LayerSelected(S.Selected);
            TextLayer l = S.Current;
            loading = true;
            UpdateRanges();
            if (l != null)
            {
                bool rtl = TextLayer.HasRtl(l.Text) || Theme.RTL;
                RightToLeft want = rtl ? RightToLeft.Yes : RightToLeft.No;
                if (text.Box.RightToLeft != want) text.Box.RightToLeft = want;
                text.Box.Text = Crlf(l.Text);
                font.Selected = l.FontName; font.Invalidate();
                size.SetValue(l.SizePx, false);
                colors.Selected = l.Color; colors.Invalidate();
                styles.On = new[] { l.Bold, l.Italic, l.Outline, l.Shadow, l.Box }; styles.Invalidate();
                boxColors.Selected = l.BoxColor.IsEmpty ? Color.Black : l.BoxColor; boxColors.Invalidate();
                outColors.Selected = l.OutlineColor.IsEmpty ? (l.IsPicture || !IsLightColor(l.Color) ? Color.White : Color.Black) : l.OutlineColor; outColors.Invalidate();
                outWidth.SetValue(l.OutlineWidth, false);
                rotate.SetValue(l.Angle, false);
                picStyles.On = new[] { l.Outline, l.Shadow }; picStyles.Invalidate();
                opacity.SetValue(l.Opacity * 100, false);
                if (!l.IsPicture) { style = l.CloneStyle(); style.Angle = 0; }
            }
            else text.Box.Text = "";
            loading = false;
            bool on = l != null;
            foreach (Control c in new Control[] { bDel, text, font, size, colors, styles, position, opacity, boxColors, outColors, outWidth, rotate, picStyles }) c.Enabled = on;
            DoLayout();
            text.Box.Enabled = on;
            body.Invalidate();
        }

        // ------------------------------------------------------------------ undo / redo
        class Snap
        {
            public List<TextLayer> Layers; public int Selected, W, H; public Rectangle Crop;
            public byte[] Mask; public int BgMode; public Color BgColor; public float BgBlur;
            public object Extra; public string Key;
        }
        readonly List<Snap> undo = new List<Snap>(), redo = new List<Snap>();
        Snap committed;
        System.Windows.Forms.Timer snapTimer;
        /// Pages can add their own state to undo (e.g. the trim range).
        public Func<object> CaptureExtra; public Action<object> RestoreExtra; public Func<string> ExtraKey;
        public bool CanUndo { get { Poll(); return undo.Count > 0; } }
        /// Pages that show their own undo buttons (the video toolbar) turn these off.
        public bool ShowUndo = true;
        string[] tabGlyphs;
        void SyncUndoButtons()
        {
            bool u = undo.Count > 0, r = redo.Count > 0;
            if (bUndo.Enabled != u) { bUndo.Enabled = u; bUndo.Invalidate(); }
            if (bRedo.Enabled != r) { bRedo.Enabled = r; bRedo.Invalidate(); }
            if (HistoryChanged != null) HistoryChanged(u, r);
        }
        public event Action<bool, bool> HistoryChanged;

        void ResetHistory()
        {
            undo.Clear(); redo.Clear(); committed = null;
            SyncUndoButtons();
            if (snapTimer == null)
            {
                snapTimer = new System.Windows.Forms.Timer { Interval = 450 };
                snapTimer.Tick += (s, e) => Poll();
                snapTimer.Start();
            }
        }

        string KeyOf()
        {
            StringBuilder k = new StringBuilder();
            foreach (TextLayer l in S.Layers)
                k.Append(l.Text).Append('\u0001').Append(l.FontName).Append('|').Append(Math.Round(l.SizePx, 1)).Append('|').Append(l.Color.ToArgb())
                 .Append(l.Bold).Append(l.Italic).Append(l.Outline).Append(l.Shadow).Append(l.Box).Append(l.BoxColor.ToArgb()).Append('|')
                 .Append(l.OutlineColor.ToArgb()).Append(Math.Round(l.OutlineWidth, 2)).Append(Math.Round(l.Angle, 1)).Append(l.PicturePath).Append('|')
                 .Append(Math.Round(l.Opacity, 2)).Append((int)l.Align).Append('|').Append(Math.Round(l.Pos.X)).Append(',').Append(Math.Round(l.Pos.Y))
                 .Append('@').Append(Math.Round(l.From, 2)).Append('-').Append(Math.Round(l.To, 2)).Append(';');
            k.Append('#').Append(S.W).Append('x').Append(S.H).Append(S.Crop).Append(S.Mask == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(S.Mask))
             .Append(S.BgMode).Append(S.BgColor.ToArgb()).Append(Math.Round(S.BgBlur));
            if (ExtraKey != null) k.Append('#').Append(ExtraKey());
            return k.ToString();
        }

        Snap Capture(string key)
        {
            Snap n = new Snap { Layers = new List<TextLayer>(), Selected = S.Selected, W = S.W, H = S.H, Crop = S.Crop, Mask = S.Mask, BgMode = S.BgMode, BgColor = S.BgColor, BgBlur = S.BgBlur, Key = key };
            foreach (TextLayer l in S.Layers) n.Layers.Add(l.CloneStyle());
            if (CaptureExtra != null) n.Extra = CaptureExtra();
            return n;
        }

        /// Notes the current state; called often, it records a step only when something really changed.
        void Poll()
        {
            if (!S.Active || loading || previewingFont || saving || (Canvas != null && Canvas.CropMode)) return;
            string key = KeyOf();
            if (committed == null) { committed = Capture(key); return; }
            if (key == committed.Key) return;
            undo.Add(committed);
            if (undo.Count > 50) undo.RemoveAt(0);
            redo.Clear();
            committed = Capture(key);
            SyncUndoButtons();
        }

        public bool Undo()
        {
            Poll();
            if (undo.Count == 0) { if (Notify != null) Notify(L.T("undo_none"), true, null); SyncUndoButtons(); return false; }
            redo.Add(committed);
            committed = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            Restore(committed);
            SyncUndoButtons();
            return true;
        }

        public bool Redo()
        {
            Poll();
            if (redo.Count == 0) return false;
            undo.Add(committed);
            committed = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
            Restore(committed);
            SyncUndoButtons();
            return true;
        }

        void Restore(Snap n)
        {
            if (Canvas != null) Canvas.CropMode = false;
            foreach (TextLayer l in S.Layers) l.Touch();
            S.Layers.Clear();
            foreach (TextLayer l in n.Layers) S.Layers.Add(l.CloneStyle());
            bool geo = n.W != S.W || n.H != S.H || n.Crop != S.Crop;
            bool bg = n.Mask != S.Mask || n.BgMode != S.BgMode || n.BgColor != S.BgColor || Math.Abs(n.BgBlur - S.BgBlur) > 0.5f;
            try
            {
                if (geo) S.SetGeometry(n.W, n.H, n.Crop);
                if (bg) { S.Mask = n.Mask; S.BgMode = n.BgMode; S.BgColor = n.BgColor; S.BgBlur = n.BgBlur; if (!S.Video) S.ApplyBackground(); }
            }
            catch (Exception ex) { if (Notify != null) Notify(ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message, false, null); }
            if (RestoreExtra != null) RestoreExtra(n.Extra);
            bgMode.SetSilently(S.BgMode);
            bgColors.Selected = S.BgColor; blur.SetValue(S.BgBlur, false);
            S.Dirty = true;
            SyncCanvas();
            UpdateRanges();
            SetSizeFields(S.W, S.H);
            if (geo && Canvas != null) Canvas.Reset();
            Select(Math.Min(n.Selected, S.Layers.Count - 1));
            if (Changed != null) Changed();
            if (geo && CropChanged != null) CropChanged();
            DoLayout();
        }

        /// Puts back an edit saved earlier (recovery after a crash): size, crop, background and text.
        public void ApplyState(int w, int h, Rectangle crop, byte[] mask, int bgModeValue, Color bgColor, float bgBlur, List<TextLayer> layers)
        {
            if (!S.Active) return;
            try
            {
                if (mask != null && S.Source != null && mask.Length == S.Source.Width * S.Source.Height)
                {
                    S.Mask = mask; S.BgMode = bgModeValue; S.BgColor = bgColor; S.BgBlur = bgBlur; aiMask = true;
                    if (!S.Video) S.ApplyBackground();
                }
                if (w > 0 && h > 0) S.SetGeometry(w, h, crop);
            }
            catch (Exception ex) { if (Notify != null) Notify(ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message, false, null); }
            foreach (TextLayer l in S.Layers) l.Touch();
            S.Layers.Clear();
            if (layers != null) S.Layers.AddRange(layers);
            bgMode.SetSilently(S.BgMode); bgColors.Selected = S.BgColor; blur.SetValue(S.BgBlur, false);
            SyncCanvas(); UpdateRanges(); SetSizeFields(S.W, S.H);
            if (Canvas != null) Canvas.Reset();
            Select(S.Layers.Count > 0 ? 0 : -1);
            S.Dirty = true;
            if (Changed != null) Changed();
            DoLayout();
        }

        // ------------------------------------------------------------------ resize
        int ParseField(TextField f) { int v; return int.TryParse(f.Box.Text.Trim(), out v) ? v : 0; }
        bool settingSize;

        void SetSizeFields(int w, int h)
        {
            settingSize = true;
            fw.Box.Text = w.ToString(); fh.Box.Text = h.ToString();
            settingSize = false;
            body.Invalidate();
        }

        void SizeTyped(bool widthChanged)
        {
            body.Invalidate();
            if (settingSize || !keep.Checked || S.OW <= 0) return;
            float ar = S.BaseW / (float)S.BaseH;
            settingSize = true;
            if (widthChanged) { int w = ParseField(fw); if (w > 0) fh.Box.Text = Math.Max(1, (int)Math.Round(w / ar)).ToString(); }
            else { int h = ParseField(fh); if (h > 0) fw.Box.Text = Math.Max(1, (int)Math.Round(h * ar)).ToString(); }
            settingSize = false;
        }

        void Preset(int i)
        {
            if (S.OW <= 0) return;
            int ow = S.BaseW, oh = S.BaseH;
            float k;
            switch (i)
            {
                case 0: k = 0.25f; break;
                case 1: k = 0.5f; break;
                case 2: k = 0.75f; break;
                case 3: k = 1f; break;
                case 4: k = 2f; break;
                default:
                    int[] target = { 480, 720, 1080, 1440, 2160 };
                    k = target[i - 5] / (float)Math.Min(ow, oh);   // "1080p" = the short side becomes 1080
                    break;
            }
            int w = Math.Max(1, (int)Math.Round(ow * k)), h = Math.Max(1, (int)Math.Round(oh * k));
            if (S.Video) { w += w % 2; h += h % 2; }   // video encoders need even sizes
            SetSizeFields(w, h);
        }

        void ApplySize()
        {
            if (!S.Active) return;
            int w = ParseField(fw), h = ParseField(fh);
            if (S.Video) { w += w % 2; h += h % 2; }
            if (w < 8 || h < 8 || w > 16384 || h > 16384 || (long)w * h > 200000000L)
            {
                if (Notify != null) Notify(L.T("ed_bad_size"), false, null);
                return;
            }
            if (w == S.W && h == S.H) return;
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                S.Resize(w, h);
                SyncCanvas();
                UpdateRanges();
                if (S.Current != null) { loading = true; size.SetValue(S.Current.SizePx, false); loading = false; }
                style.SizePx = Math.Max(size.Min, Math.Min(size.Max, style.SizePx));
                if (Canvas != null) Canvas.Reset();
                if (Notify != null) Notify(L.T("ed_resized", w, h), true, null);
            }
            catch (Exception ex) { if (Notify != null) Notify(ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message, false, null); }
            finally { Cursor.Current = old; }
            if (Changed != null) Changed();
            body.Invalidate();
        }

        // ------------------------------------------------------------------ crop
        void StartCrop(float aspect)
        {
            if (Canvas == null || !S.Active) return;
            Canvas.StartCrop(aspect);
            DoLayout();
        }
        public void CancelCrop()
        {
            if (Canvas == null || !Canvas.CropMode) return;
            Canvas.CropMode = false; Canvas.Invalidate();
            DoLayout();
        }
        void ApplyCrop()
        {
            if (Canvas == null || !Canvas.CropMode) return;
            RectangleF r = Canvas.CropRect;
            Canvas.CropMode = false;
            try { S.ApplyCrop(r); }
            catch (Exception ex) { if (Notify != null) Notify(ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message, false, null); }
            AfterGeometry();
        }
        public event Action CropChanged;
        void AfterGeometry()
        {
            SyncCanvas();
            UpdateRanges();
            SetSizeFields(S.W, S.H);
            if (Canvas != null) Canvas.Reset();
            if (Changed != null) Changed();
            if (CropChanged != null) CropChanged();
            DoLayout();
        }

        // ------------------------------------------------------------------ Arabic fonts
        void GetArabicFonts()
        {
            if (fontsBusy) return;
            fontsBusy = true;
            if (Notify != null) Notify(L.T("fonts_downloading"), true, null);
            Thread t = new Thread(() =>
            {
                string err = null;
                try { FontPack.Download(null, () => IsDisposed || Tools.ShuttingDown); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    fontsBusy = false;
                    font.Extra = FontPack.Families;
                    if (FontPack.Ready)
                    {
                        string pick = FontPack.Families.Contains("Aref Ruqaa") ? "Aref Ruqaa" : FontPack.Families[0];
                        font.Selected = pick; style.FontName = pick; Apply(l => l.FontName = pick, true);
                        font.Invalidate();
                        if (Notify != null) Notify(L.T("fonts_ready", FontPack.Families.Count), true, null);
                    }
                    else if (Notify != null) Notify(L.T("fonts_failed") + (err != null ? " " + err : ""), false, null);
                    DoLayout();
                });
            });
            t.IsBackground = true; t.Start();
        }

        // ------------------------------------------------------------------ background removal
        void RemoveBackground()
        {
            if (bgBusy || S.Video || S.Source == null) return;
            bgBusy = true;
            bBgRun.Enabled = false;
            bgProgress.Visible = true;
            bgProgress.SetIndeterminate(true);
            bool install = !BgRemover.Ready;
            bgStatus = install ? L.T("bg_dl_engine") : L.T("bg_working");
            DoLayout();
            Bitmap copy = new Bitmap(S.Source);   // the worker gets its own copy (GDI+ images aren't thread-safe)
            Thread t = new Thread(() =>
            {
                string err = null; byte[] mask = null;
                try
                {
                    if (install)
                    {
                        DateTime last = DateTime.MinValue;
                        BgRemover.Install((f, msg) =>
                        {
                            if ((DateTime.Now - last).TotalMilliseconds < 150) return;
                            last = DateTime.Now;
                            Ui(() => { bgProgress.SetIndeterminate(false); bgProgress.Value = f; bgStatus = msg; body.Invalidate(); });
                        }, () => IsDisposed || Tools.ShuttingDown);
                        Ui(() => { bgProgress.SetIndeterminate(true); bgStatus = L.T("bg_working"); body.Invalidate(); });
                    }
                    mask = BgRemover.Mask(copy);
                }
                catch (Exception ex) { err = ex.Message; }
                copy.Dispose();
                Ui(() =>
                {
                    bgBusy = false; bBgRun.Enabled = true;
                    bgProgress.SetIndeterminate(false); bgProgress.Visible = false;
                    if (err != null || mask == null || S.Source == null) { bgStatus = err != null ? L.T("bg_failed", err) : ""; DoLayout(); return; }
                    S.Mask = mask; aiMask = true;
                    bgStatus = "";
                    bgMode.SetSilently(1);
                    SetBgMode(1);
                    PreferAlpha();   // keep the transparency
                    if (Notify != null) Notify(L.T("bg_done"), true, null);
                });
            });
            t.IsBackground = true; t.Start();
        }

        void SetBgTool(int t)
        {
            if (t > 0 && !S.Video && S.Source != null && S.Mask == null)
            {
                // starting by hand: everything is kept until you erase it
                S.Mask = MaskEdit.Full(S.Source.Width, S.Source.Height);
                bgMode.SetSilently(1);
                SetBgMode(1);
            }
            SyncBrush();
            DoLayout();
        }

        void SyncBrush()
        {
            if (Canvas == null) return;
            int t = Mode == EditorMode.Photo && CurrentTab == EdTab.Background ? bgTool.SelectedIndex - 1 : -1;
            Canvas.BrushTool = t;
            Canvas.BrushPx = Theme.S(brush.Value);
            Canvas.Invalidate();
        }

        void OnStroke(List<PointF> pts, float radius, int tool)
        {
            if (S.Video || S.Source == null || tool < 0 || tool > 1) return;
            Poll();   // each stroke is its own undo step
            if (S.Mask == null) S.Mask = MaskEdit.Full(S.Source.Width, S.Source.Height);
            List<PointF> sp = pts.ConvertAll(p => S.ToSource(p));
            Cursor old = Cursor.Current; Cursor.Current = Cursors.WaitCursor;
            try { S.Mask = MaskEdit.Brush(S.Mask, S.Source.Width, S.Source.Height, sp, radius * S.SourceScale, tool == 1); }
            finally { Cursor.Current = old; }
            AfterMaskEdit();
        }

        void OnWand(PointF at, bool shift)
        {
            if (S.Video || S.Source == null) return;
            Poll();
            if (S.Mask == null) S.Mask = MaskEdit.Full(S.Source.Width, S.Source.Height);
            PointF sp = S.ToSource(at);
            Cursor old = Cursor.Current; Cursor.Current = Cursors.WaitCursor;
            try { S.Mask = MaskEdit.Wand(S.Mask, S.Source, new Point((int)sp.X, (int)sp.Y), (int)tolerance.Value, everywhere.Checked, shift); }
            catch (Exception ex) { if (Notify != null) Notify(ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message, false, null); return; }
            finally { Cursor.Current = old; }
            AfterMaskEdit();
        }

        void AfterMaskEdit()
        {
            if (S.BgMode == 0) { bgMode.SetSilently(1); S.BgMode = 1; PreferAlpha(); }
            RefreshBackground();
            Poll();
            DoLayout();
        }

        void SetBgMode(int m)
        {
            if (S.Mask == null && m != 0) { bgMode.SetSilently(0); return; }
            S.BgMode = m;
            if (m == 1) PreferAlpha();
            RefreshBackground();
            DoLayout();
        }

        void RefreshBackground()
        {
            Cursor old = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;
            try { S.ApplyBackground(); SyncCanvas(); }
            catch (Exception ex) { if (Notify != null) Notify(ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message, false, null); }
            finally { Cursor.Current = old; }
            if (Changed != null) Changed();
        }

        // ------------------------------------------------------------------ save (photos)
        public void Save()
        {
            if (S.Work == null || saving || S.Video) return;
            string ext = SaveExts[fmt.SelectedIndex];
            string name = Path.GetFileNameWithoutExtension(S.SourcePath) + " " + L.T("ed_suffix");
            List<string> choices = new List<string> { ext };
            foreach (string e2 in SaveExts) if (e2 != ext) choices.Add(e2);
            string outp = SaveAsk.File(FindForm(), name + "." + ext, choices.ToArray());
            if (outp == null) return;
            string picked = Path.GetExtension(outp).TrimStart('.').ToLowerInvariant();
            if (Array.IndexOf(SaveExts, picked) >= 0 && picked != ext) { ext = picked; fmt.SetSilently(Array.IndexOf(SaveExts, picked)); }
            bool jpeg = ext == "jpg" || ext == "jpeg";
            bool pdf = ext == "pdf";
            if (ext == "webp" && Tools.Magick == null) { if (Notify != null) Notify(L.T("err_need_im"), false, null); return; }
            Bitmap final;
            try { final = S.Render(jpeg || pdf); }
            catch (Exception ex) { if (Notify != null) Notify(ex is OutOfMemoryException ? L.T("ed_too_big") : ex.Message, false, null); return; }
            saving = true;
            bSave.Enabled = false;
            Thread t = new Thread(() =>
            {
                string err = null;
                try
                {
                    if (pdf) ImagePdf.Write(new[] { final }, outp, 92);
                    else if (jpeg)
                    {
                        ImageCodecInfo jpg = null;
                        foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpg = c;
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                            final.Save(outp, jpg, ep);
                        }
                    }
                    else if (ext == "png") final.Save(outp, ImageFormat.Png);
                    else
                    {
                        string tmp = Path.Combine(Path.GetTempPath(), "fc_edit_" + Guid.NewGuid().ToString("N") + ".png");
                        final.Save(tmp, ImageFormat.Png);
                        string output;
                        int code = Tools.RunHidden(Tools.Magick, Tools.Quote(tmp) + " -quality 90 " + Tools.Quote(outp), 120000, out output);
                        try { File.Delete(tmp); } catch { }
                        if (code != 0 || !File.Exists(outp)) err = Tools.LastLines(output, 1);
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                final.Dispose();
                Ui(() =>
                {
                    saving = false; bSave.Enabled = true;
                    if (err == null) S.Dirty = false;
                    if (Notify != null) Notify(err == null ? L.T("v_saved", Path.GetFileName(outp)) : err, err == null, err == null ? outp : null);
                });
            });
            t.IsBackground = true; t.Start();
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        // ------------------------------------------------------------------ layout & paint
        public void Restyle()
        {
            text.Restyle(); fw.Restyle(); fh.Restyle();
            DoLayout();
            Invalidate(true);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        public int Pad { get { return Theme.S(16); } }
        public int RowH { get { return Theme.S(34); } }
        public int LabelW { get { return Theme.S(78); } }
        public PanelBody Body { get { return body; } }

        readonly List<Control> trimCtl = new List<Control>();
        /// Controls for the Trim tab (shown only on that tab).
        public void AddToBody(params Control[] cs) { body.Controls.AddRange(cs); trimCtl.AddRange(cs); }
        public void BodyPlace(Control c, Rectangle ltr) { c.Bounds = Theme.Mirror(ltr, body.Width); }
        public void PanelPlace(Control c, Rectangle ltr) { c.Bounds = Theme.Mirror(ltr, Width); }
        public void AddLabel(Func<string> t, Rectangle ltr) { body.Labels.Add(new KeyValuePair<Func<string>, Rectangle>(t, ltr)); }

        public void DoLayout()
        {
            if (Width <= 0 || Height <= 0) return;
            int p = Pad, gap = Theme.S(10), rh = RowH, lw = LabelW;
            int W = Width;
            int footerTop;
            if (FooterLayout != null && Mode != EditorMode.Photo) footerTop = FooterLayout();
            else
            {
                // photo footer: format + buttons, pinned to the bottom
                int fy = Height - p - Theme.S(40);
                int half = (W - p * 2 - gap) * 2 / 5;
                PanelPlace(bDone, new Rectangle(p, fy, half, Theme.S(40)));
                PanelPlace(bSave, new Rectangle(p + half + gap, fy, W - p * 2 - gap - half, Theme.S(40)));
                fy -= gap + rh;
                PanelPlace(fmt, new Rectangle(p + lw, fy, W - p * 2 - lw, rh));
                saveLabel = new Rectangle(p, fy, lw, rh);
                footerTop = fy - Theme.S(12);
            }
            footerLine = new Rectangle(p, footerTop, W - p * 2, 1);
            body.SetBounds(0, Theme.S(4), W, Math.Max(10, footerTop - Theme.S(8)));

            int bw = body.Width, cw = bw - p * 2;
            int y = p - body.Offset;
            body.Labels.Clear();
            bool showTabs = tabList.Length > 1;   // (reading .Visible would say false while the panel itself is hidden)
            tabs.Visible = showTabs;
            int ub = rh + Theme.S(2), uw = ShowUndo ? ub * 2 + Theme.S(12) : 0;
            bUndo.Visible = bRedo.Visible = ShowUndo;
            if (ShowUndo)
            {
                BodyPlace(bUndo, new Rectangle(p + cw - ub * 2 - Theme.S(4), y, ub, ub));
                BodyPlace(bRedo, new Rectangle(p + cw - ub, y, ub, ub));
            }
            if (showTabs)
            {
                // icons only when every tab still has room for its word
                int per = (cw - uw) / Math.Max(1, tabList.Length);
                tabs.Glyphs = per >= Theme.S(96) ? tabGlyphs : null;
                BodyPlace(tabs, new Rectangle(p, y, cw - uw, rh + Theme.S(2))); y += rh + gap + Theme.S(6);
            }
            else if (ShowUndo) y += ub + gap;
            EdTab tab = CurrentTab;
            Control[] textCtl = { bAdd, bPic, bDel, text, font, size, colors, styles, position, opacity, rotate };
            Control[] sizeCtl = { fw, fh, keep, presets, bApply };
            Control[] bgCtl = { bBgRun, bgMode, bgColors, blur, bgProgress, bgTool, brush, tolerance, everywhere };
            foreach (Control c in textCtl) c.Visible = tab == EdTab.Text;
            foreach (Control c in sizeCtl) c.Visible = tab == EdTab.Resize;
            foreach (Control c in bgCtl) c.Visible = false;
            foreach (Control c in trimCtl) c.Visible = false;
            foreach (Control c in fxCtl) c.Visible = false;
            foreach (Control c in timingCtl) c.Visible = false;
            foreach (Control c in new Control[] { boxColors, cropPresets, bCropApply, bCropCancel, bCropReset, bFonts, outColors, outWidth, picStyles }) c.Visible = false;
            if (tab == EdTab.Trim && TrimLayout != null) y = TrimLayout(y, cw);
            else if (tab == EdTab.Effects && EffectsLayout != null) y = EffectsLayout(y, cw);
            else if (tab == EdTab.Text)
            {
                int both = cw - rh - Theme.S(8), half = (both - gap) / 2;
                BodyPlace(bAdd, new Rectangle(p, y, half, rh));
                BodyPlace(bPic, new Rectangle(p + half + gap, y, both - half - gap, rh));
                BodyPlace(bDel, new Rectangle(p + cw - rh, y, rh, rh)); y += rh + gap;
                TextLayer cur = S.Current;
                if (cur != null && cur.IsPicture)
                {
                    // a photo on top: size, turn, frame, shadow, place, see-through
                    text.Visible = font.Visible = colors.Visible = styles.Visible = false;
                    if (Mode == EditorMode.Video && TextTimingLayout != null) y = TextTimingLayout(y, cw);
                    picStyles.Visible = true;
                    Row("ed_size", size, ref y);
                    Row("ed_rotate", rotate, ref y);
                    Row("ed_style", picStyles, ref y);
                    if (cur.Outline)
                    {
                        outColors.Visible = outWidth.Visible = true;
                        Row("ed_border_color", outColors, ref y);
                        Row("ed_thickness", outWidth, ref y);
                    }
                    Row("ed_position", position, ref y);
                    Row("ed_opacity", opacity, ref y);
                    goto textDone;
                }
                BodyPlace(text, new Rectangle(p, y, cw, Theme.S(64))); y += Theme.S(64) + gap + Theme.S(2);
                if (Mode == EditorMode.Video && TextTimingLayout != null && S.Current != null) y = TextTimingLayout(y, cw);
                Row("ed_font", font, ref y);
                if (!FontPack.Ready)
                {
                    bFonts.Visible = true;
                    BodyPlace(bFonts, new Rectangle(p + lw, y - Theme.S(6), cw - lw, Theme.S(28))); y += Theme.S(28);
                }
                Row("ed_size", size, ref y);
                Row("ed_color", colors, ref y);
                Row("ed_style", styles, ref y);
                bool boxOn = S.Current != null ? S.Current.Box : style.Box;
                if (boxOn) { boxColors.Visible = true; Row("ed_box_color", boxColors, ref y); }
                bool outOn = S.Current != null ? S.Current.Outline : style.Outline;
                if (outOn)
                {
                    outColors.Visible = outWidth.Visible = true;
                    Row("ed_outline_color", outColors, ref y);
                    Row("ed_thickness", outWidth, ref y);
                }
                Row("ed_rotate", rotate, ref y);
                Row("ed_position", position, ref y);
                Row("ed_opacity", opacity, ref y);
            textDone:;
            }
            else if (tab == EdTab.Resize)
            {
                infoRow = new Rectangle(p, y, cw, Theme.S(40)); y += Theme.S(40) + gap;
                Row("ed_width", fw, ref y);
                Row("ed_height", fh, ref y);
                AddLabel(() => L.T("ed_keep"), new Rectangle(p, y, cw - Theme.S(60), rh));
                BodyPlace(keep, new Rectangle(p + cw - Theme.S(46), y + (rh - Theme.S(24)) / 2, Theme.S(46), Theme.S(24))); y += rh + gap;
                AddLabel(() => L.T("ed_presets"), new Rectangle(p, y, cw, Theme.S(20))); y += Theme.S(24);
                int ph = presets.NeededHeight(Theme.S(32));
                BodyPlace(presets, new Rectangle(p, y, cw, ph)); y += ph + gap + Theme.S(4);
                BodyPlace(bApply, new Rectangle(p, y, cw, Theme.S(38))); y += Theme.S(38) + gap;
                hintRow = new Rectangle(p, y, cw, Theme.S(54)); y += Theme.S(54) + gap;
                cropLine = new Rectangle(p, y, cw, 1); y += Theme.S(12);
                AddLabel(() => L.T("ed_crop"), new Rectangle(p, y, cw, Theme.S(20))); y += Theme.S(24);
                cropPresets.Visible = true;
                int chh = cropPresets.NeededHeight(Theme.S(32));
                BodyPlace(cropPresets, new Rectangle(p, y, cw, chh)); y += chh + gap;
                if (Canvas != null && Canvas.CropMode)
                {
                    int half = (cw - gap) / 2;
                    bCropCancel.Visible = bCropApply.Visible = true;
                    BodyPlace(bCropCancel, new Rectangle(p, y, half, Theme.S(38)));
                    BodyPlace(bCropApply, new Rectangle(p + half + gap, y, cw - half - gap, Theme.S(38))); y += Theme.S(38) + gap;
                }
                else if (!S.Crop.IsEmpty)
                {
                    bCropReset.Visible = true;
                    BodyPlace(bCropReset, new Rectangle(p, y, cw, Theme.S(36))); y += Theme.S(36) + gap;
                }
            }
            else if (tab == EdTab.Background)
            {
                bgInfoRow = new Rectangle(p, y, cw, Theme.S(BgRemover.Ready ? 70 : 84)); y += bgInfoRow.Height + gap;
                bBgRun.Visible = true;
                BodyPlace(bBgRun, new Rectangle(p, y, cw, Theme.S(40))); y += Theme.S(40) + gap;
                if (bgBusy || bgStatus.Length > 0)
                {
                    bgProgress.Visible = bgBusy;
                    BodyPlace(bgProgress, new Rectangle(p, y, cw, Theme.S(6))); y += Theme.S(12);
                    bgHintRow = new Rectangle(p, y, cw, Theme.S(40)); y += Theme.S(44);
                }
                else bgHintRow = Rectangle.Empty;
                // fix by hand: erase / restore brush, magic wand
                bgManualR = new Rectangle(p, y, cw, Theme.S(20)); y += Theme.S(24);
                bgTool.Visible = true;
                BodyPlace(bgTool, new Rectangle(p, y, cw, rh)); y += rh + gap;
                int tool = bgTool.SelectedIndex;
                if (tool == 1 || tool == 2) { brush.Visible = true; Row("bg_brush", brush, ref y); }
                if (tool == 3)
                {
                    tolerance.Visible = true; Row("bg_tolerance", tolerance, ref y);
                    everywhereR = new Rectangle(p, y, cw - Theme.S(60), rh);
                    everywhere.Visible = true;
                    BodyPlace(everywhere, new Rectangle(p + cw - Theme.S(46), y + (rh - Theme.S(24)) / 2, Theme.S(46), Theme.S(24))); y += rh + gap;
                }
                else everywhereR = Rectangle.Empty;
                bgToolHintR = new Rectangle(p, y, cw, Theme.S(tool == 0 ? 36 : 54)); y += bgToolHintR.Height + gap;
                if (S.Mask != null)
                {
                    AddLabel(() => L.T("bg_background"), new Rectangle(p, y, cw, Theme.S(20))); y += Theme.S(24);
                    bgMode.Visible = true;
                    BodyPlace(bgMode, new Rectangle(p, y, cw, rh)); y += rh + gap;
                    if (S.BgMode == 2) { bgColors.Visible = true; Row("ed_color", bgColors, ref y); }
                    if (S.BgMode == 3) { blur.Visible = true; Row("bg_strength", blur, ref y); }
                }
            }
            body.ContentHeight = y + body.Offset + p;
            if (body.Offset > body.MaxOffset) { body.ClampTo(body.MaxOffset); DoLayout(); return; }
            body.Invalidate();
            Invalidate();
        }

        void Row(string key, Control c, ref int y)
        {
            int p = Pad, rh = RowH, lw = LabelW, cw = body.Width - p * 2;
            AddLabel(() => L.T(key), new Rectangle(p, y, lw, rh));
            BodyPlace(c, new Rectangle(p + lw, y, cw - lw, rh));
            y += rh + Theme.S(10);
        }

        void PaintBodyExtras(Graphics g)
        {
            EdTab tab = CurrentTab;
            if (tab == EdTab.Trim) { if (TrimPaint != null) TrimPaint(g); return; }
            if (tab == EdTab.Effects) { if (EffectsPaint != null) EffectsPaint(g); return; }
            if (tab == EdTab.Text && Mode == EditorMode.Video && TextTimingPaint != null && S.Current != null) TextTimingPaint(g);
            if (tab == EdTab.Resize && S.Active)
            {
                Rectangle r = Theme.Mirror(infoRow, body.Width);
                Theme.FillRound(g, r, Theme.S(10), Theme.CardInner);
                Rectangle inner = Rectangle.Inflate(r, -Theme.S(12), 0);
                string cur = L.T("ed_current", S.BaseW + " × " + S.BaseH);
                int nw = ParseField(fw), nh = ParseField(fh);
                string now = L.T("ed_new", (nw > 0 ? nw : 0) + " × " + (nh > 0 ? nh : 0));
                Theme.Text(g, cur, Theme.F(8.75f), Theme.InkSoft, inner, Align.Start);
                Theme.Text(g, now, Theme.F(8.75f, FontStyle.Bold), Theme.Ink, inner, Align.End);
                Theme.Text(g, L.T(S.Video ? "ed_resize_hint_video" : "ed_resize_hint"), Theme.F(8.5f), Theme.InkFaint, Theme.Mirror(hintRow, body.Width), Align.Start, false, true);
                using (SolidBrush lb = new SolidBrush(Theme.Line)) g.FillRectangle(lb, Theme.Mirror(cropLine, body.Width));
            }
            if (tab == EdTab.Background)
            {
                Rectangle r = Theme.Mirror(bgInfoRow, body.Width);
                Theme.FillRound(g, r, Theme.S(10), Theme.CardInner);
                Rectangle ic = Theme.Mirror(new Rectangle(bgInfoRow.X + Theme.S(12), bgInfoRow.Y + Theme.S(12), Theme.S(30), Theme.S(30)), body.Width);
                Theme.FillRoundGradient(g, ic, Theme.S(9), Theme.PrimaryTop, Theme.PrimaryBottom);
                Theme.Glyph(g, "", 10f, Theme.PrimaryInk, ic);
                Rectangle tr = Theme.Mirror(new Rectangle(bgInfoRow.X + Theme.S(52), bgInfoRow.Y + Theme.S(8), bgInfoRow.Width - Theme.S(62), bgInfoRow.Height - Theme.S(14)), body.Width);
                Theme.Text(g, L.T(BgRemover.Ready ? "bg_info_ready" : "bg_info"), Theme.F(8.5f), Theme.InkSoft, tr, Align.Start, true, true);
                Theme.Text(g, L.T("bg_manual"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(bgManualR, body.Width), Align.Start);
                int tool = bgTool.SelectedIndex;
                Theme.Text(g, L.T(tool == 0 ? "bg_hint_off" : tool == 3 ? "bg_hint_wand" : "bg_hint_brush"), Theme.F(8.5f), Theme.InkFaint, Theme.Mirror(bgToolHintR, body.Width), Align.Start, false, true);
                if (!everywhereR.IsEmpty) Theme.Text(g, L.T("bg_everywhere"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(everywhereR, body.Width), Align.Start);
                if (!bgHintRow.IsEmpty)
                    Theme.Text(g, bgStatus, Theme.F(8.5f), bgStatus.StartsWith(L.T("bg_failed", "").Trim()) ? Theme.Error : Theme.InkSoft, Theme.Mirror(bgHintRow, body.Width), Align.Start, false, true);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            using (SolidBrush b = new SolidBrush(Theme.Line)) g.FillRectangle(b, footerLine);
            if (Mode == EditorMode.Photo) Theme.Text(g, L.T("ed_save_as"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(saveLabel, Width), Align.Start);
            else if (FooterPaint != null) FooterPaint(g);
        }
    }

    public enum EdTabPublic { Trim, Text, Resize, Background, Effects }
}
