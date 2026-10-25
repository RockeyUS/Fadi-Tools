using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace FileConverter
{
    /// A small number box with its name in front ("X 120"): type, or ↑ ↓ (Shift: ×10), or scroll over it.
    public class NumBox : UiControl
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None };
        public Func<string> Caption;
        public float Value, Increment = 1;
        public string Unit = "";
        public event Action<float> Committed;
        bool focused;

        public NumBox(Func<string> caption)
        {
            Caption = caption;
            Box.Font = Theme.F(9.5f);
            Box.RightToLeft = RightToLeft.No;
            Controls.Add(Box);
            Box.GotFocus += (s, e) => { focused = true; Invalidate(); BeginInvoke((Action)(() => Box.SelectAll())); };
            Box.LostFocus += (s, e) => { focused = false; Commit(); Invalidate(); };
            Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); Box.SelectAll(); }
                else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    e.SuppressKeyPress = true;
                    float v; if (!Parse(out v)) v = Value;
                    v += (e.KeyCode == Keys.Up ? 1 : -1) * Increment * (e.Shift ? 10 : 1);
                    SetValue(v); if (Committed != null) Committed(v); Box.SelectAll();
                }
            };
            Box.MouseWheel += (s, e) =>
            {
                float v; if (!Parse(out v)) v = Value;
                v += Math.Sign(e.Delta) * Increment;
                SetValue(v); if (Committed != null) Committed(v);
            };
            Cursor = Cursors.IBeam;
        }

        public bool Editing { get { return focused; } }
        bool Parse(out float v) { return float.TryParse(Box.Text.Replace(Unit, "").Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v); }
        void Commit() { float v; if (Parse(out v) && Math.Abs(v - Value) > 0.0001f) { Value = v; if (Committed != null) Committed(v); } SetValue(Value); }

        public void SetValue(float v)
        {
            Value = v;
            string t = (Math.Abs(v - Math.Round(v)) < 0.05 ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture)) + Unit;
            if (Box.Text != t && !(focused && Box.Text.Length > 0 && Box.Focused && Box.SelectionLength == 0)) Box.Text = t;
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); Box.Focus(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int cw = Theme.S(22), h = Box.PreferredHeight;
            Box.Bounds = Theme.Mirror(new Rectangle(cw + Theme.S(4), (Height - h) / 2, Width - cw - Theme.S(10), h), Width);
            Box.BackColor = Theme.Field; Box.ForeColor = Theme.Ink;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.S(7), Theme.Field);
            Theme.StrokeRound(g, r, Theme.S(7), focused ? Theme.AccentColor : Theme.FieldBorder, focused ? 1.6f : 1f);
            Theme.Text(g, Caption(), Theme.F(8.25f, FontStyle.Bold), Theme.InkFaint, Theme.Mirror(new Rectangle(Theme.S(4), 0, Theme.S(20), Height), Width), Align.Center);
            if (Box.BackColor != Theme.Field) { Box.BackColor = Theme.Field; Box.ForeColor = Theme.Ink; }
        }
        protected override string AccName { get { return Caption(); } }
    }

    public partial class PhotoStudio
    {
        // selection
        Segments selMode;
        readonly Slider feather = new Slider { Min = 0, Max = 100, Value = 0 }, tolerance = new Slider { Min = 0, Max = 120, Value = 32 };
        readonly Toggle contiguous = new Toggle();
        Btn bSubject, bSelAll, bInvert, bDeselect, bSelCopy, bSelFill, bSelClear, bSelCrop;
        // brush, eraser, remove
        readonly Slider brushSize = new Slider { Min = 1, Max = 600, Value = 30, Curved = true }, brushHard = new Slider { Min = 0, Max = 100, Value = 60 }, brushOpac = new Slider { Min = 1, Max = 100, Value = 100 };
        readonly Swatches brushColors = new Swatches();
        // crop
        readonly ChipGrid cropRatio = new ChipGrid { Columns = 4, RowH = 32 };
        Btn bCropApply, bCropCancel;
        // place & size
        readonly NumBox nX = new NumBox(() => "X"), nY = new NumBox(() => "Y"), nW = new NumBox(() => "W"), nH = new NumBox(() => "H"), nAngle = new NumBox(() => "°");
        Btn bFlipH, bFlipV, bAL, bAC, bAR, bAT, bAM, bAB;
        Btn bRemoveBg, bShowAll;
        // text
        readonly MultiField textBox = new MultiField();
        readonly DropDown font = new DropDown { PreviewFonts = true };
        readonly Slider textSize = new Slider { Min = 6, Max = 1200, Value = 64, Curved = true };
        readonly Swatches textColors = new Swatches(), boxColors = new Swatches();
        ChipToggles textStyles;
        Segments textAlign;
        readonly TextLayer textStyle = new TextLayer { Color = Color.White };
        // adjustments
        readonly Slider aBright = Adj(), aContrast = Adj(), aSat = Adj(), aWarm = Adj(), aBlur = new Slider { Min = 0, Max = 100, Value = 0 }, aSharp = new Slider { Min = 0, Max = 100, Value = 0 };
        readonly ChipGrid aFilter = new ChipGrid { Columns = 3, RowH = 32 };
        readonly LevelsBar levels = new LevelsBar();
        readonly CurveEditor curves = new CurveEditor();
        readonly DropDown hueRange = new DropDown();
        readonly Slider hHue = Adj(), hSat = Adj(), hLight = Adj();
        Btn bAdjReset, bAuto;
        static Slider Adj() { return new Slider { Min = -100, Max = 100, Value = 0, Format = v => (v > 0 ? "+" : "") + ((int)Math.Round(v)) }; }

        readonly List<KeyValuePair<string, Rectangle>> hints = new List<KeyValuePair<string, Rectangle>>();
        readonly List<KeyValuePair<Func<string>, Rectangle>> rowLabels = new List<KeyValuePair<Func<string>, Rectangle>>();
        readonly HashSet<Control> placed = new HashSet<Control>();
        readonly List<Control> propCtl = new List<Control>();
        int py, pw;

        Btn Small(Func<string> label, string glyph, Action a)
        {
            Btn b = new Btn(label, glyph, BtnKind.Secondary) { FontSize = 8.75f, GlyphSize = 9.5f };
            b.Click += (s, e) => a();
            return b;
        }
        Btn Ico2(string glyph, string tipKey, Action a)
        {
            Btn b = new Btn(() => "", glyph, BtnKind.Secondary) { GlyphSize = 10f };
            b.Click += (s, e) => a();
            b.MouseEnter += (s, e) => tips.SetToolTip(b, L.T(tipKey));
            b.AccessibleName = L.T(tipKey);
            return b;
        }

        void InitProps()
        {
            props.Surface = Surface.Card;
            // ---- selection
            selMode = new Segments(() => L.T("ps_sel_new"), () => L.T("ps_sel_add"), () => L.T("ps_sel_sub"), () => L.T("ps_sel_int")) { FontSize = 8.25f };
            selMode.SelectedChanged += (s, e) => canvas.SelMode = (SelOp)selMode.SelectedIndex;
            feather.Format = v => ((int)Math.Round(v)) + " px";
            feather.ValueChanged += (s, e) => canvas.Feather = (int)Math.Round(feather.Value);
            tolerance.ValueChanged += (s, e) => canvas.WandTolerance = (int)Math.Round(tolerance.Value);
            contiguous.SetSilently(true);
            contiguous.CheckedChanged += (s, e) => canvas.WandContiguous = contiguous.Checked;
            bSubject = new Btn(() => L.T("ps_subject"), Ps.Subject, BtnKind.Secondary) { FontSize = 9f };
            bSubject.Click += (s, e) => SelectSubject();
            bSelAll = Small(() => L.T("ps_sel_all"), null, SelectAll);
            bInvert = Small(() => L.T("ps_sel_invert"), Ps.Invert, InvertSel);
            bDeselect = Small(() => L.T("ps_sel_none"), Ico.Close, () => { if (doc != null && doc.Sel != null) canvas.ApplySel(null, SelOp.New, "ps_h_deselect"); });
            bSelCopy = Small(() => L.T("ps_sel_copy"), Ico.Copy, LayerViaCopy);
            bSelFill = Small(() => L.T("ps_sel_fill"), Ps.Fill, FillSel);
            bSelClear = Small(() => L.T("ps_sel_clear"), Ico.Delete, DeleteKey);
            bSelCrop = Small(() => L.T("ps_sel_crop"), Ico.Crop, CropToSel);
            // ---- brush
            brushSize.Format = v => ((int)Math.Round(v)) + " px";
            brushSize.SetValue(canvas.BrushSize, false);
            brushSize.ValueChanged += (s, e) => { if (!loading) { canvas.BrushSize = brushSize.Value; canvas.Invalidate(); } };
            brushHard.Format = v => ((int)Math.Round(v)) + "%";
            brushHard.SetValue(canvas.BrushHardness * 100, false);
            brushHard.ValueChanged += (s, e) => canvas.BrushHardness = brushHard.Value / 100f;
            brushOpac.Format = v => ((int)Math.Round(v)) + "%";
            brushOpac.ValueChanged += (s, e) => canvas.BrushOpacity = brushOpac.Value / 100f;
            brushColors.Selected = canvas.Fore;
            brushColors.LivePreview = false;
            brushColors.SelectedChanged += (s, e) => SetFore(brushColors.Selected);
            // ---- crop
            cropRatio.LabelOf = k => k == "free" ? L.T("ps_crop_free") : k == "orig" ? L.T("ps_crop_orig") : k;
            cropRatio.SetItems(new[] { "free", "orig", "1:1", "4:5", "16:9", "9:16", "3:2", "2:3" }, "free");
            cropRatio.SelectedChanged += (s, e) =>
            {
                string k = cropRatio.Selected;
                canvas.CropRatio = k == "free" ? 0 : k == "orig" ? (doc == null ? 0 : doc.W / (float)doc.H) : Ratio(k);
                canvas.FitCropRatio();
            };
            bCropApply = new Btn(() => L.T("ed_crop_apply"), Ico.Check, BtnKind.Primary) { FontSize = 9f };
            bCropApply.Click += (s, e) => ApplyCrop();
            bCropCancel = Small(() => L.T("cancel"), Ico.Close, () => SetTool(PhotoTool.Move));
            // ---- place & size
            nAngle.Unit = "";
            nX.Committed += v => Transform(TrX, v); nY.Committed += v => Transform(TrY, v);
            nW.Committed += v => Transform(TrW, v); nH.Committed += v => Transform(TrH, v);
            nAngle.Committed += v => Transform(TrA, v);
            bFlipH = Ico2(Ico.FlipH, "ps_flip_h", () => FlipLayer(true));
            bFlipV = Ico2(Ico.FlipV, "ps_flip_v", () => FlipLayer(false));
            bAL = Ico2(Ps.AlignL, "ps_al_l", () => Align2(0)); bAC = Ico2(Ps.AlignC, "ps_al_c", () => Align2(1)); bAR = Ico2(Ps.AlignR, "ps_al_r", () => Align2(2));
            bAT = Ico2(Ps.AlignT, "ps_al_t", () => Align2(3)); bAM = Ico2(Ps.AlignM, "ps_al_m", () => Align2(4)); bAB = Ico2(Ps.AlignB, "ps_al_b", () => Align2(5));
            bRemoveBg = new Btn(() => L.T(BgRemover.Ready ? "ps_remove_bg" : "bg_download"), Ps.Subject, BtnKind.Secondary) { FontSize = 9f };
            bRemoveBg.Click += (s, e) => RemoveLayerBackground();
            bShowAll = Small(() => L.T("ps_show_all"), Ico.Refresh, () => { DocLayer l = doc == null ? null : doc.Cur; if (l == null || l.Mask == null) return; Change("ps_h_mask"); l.Mask = null; l.Version++; AfterChange(true); });
            // ---- text
            textBox.Box.TextChanged += (s, e) =>
            {
                TextLayer t = CurText;
                if (loading || t == null) return;
                Change("ps_h_text", textBox);
                t.Text = textBox.Box.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"); t.Touch();
                doc.Cur.Version++;
                canvas.Redraw(); layers.Invalidate();
            };
            textBox.Box.LostFocus += (s, e) => EndChange();
            List<string> favs;
            font.Items = EditorPanel.AllFonts(out favs); font.Favorites = favs;
            font.Selected = font.Items.Contains("Segoe UI") ? "Segoe UI" : font.Items[0];
            font.Extra = FontPack.Families; font.ExtraHeader = () => L.T("fonts_arabic");
            textStyle.FontName = font.Selected;
            font.Previewing += v =>
            {
                TextLayer t = CurText;
                if (t == null || loading) return;
                string want = v ?? font.Selected;
                if (t.FontName != want) { t.FontName = want; t.Touch(); canvas.Redraw(); }
            };
            font.SelectedChanged += (s, e) => TextSet("ps_h_font", t => t.FontName = font.Selected, null);
            textSize.Format = v => ((int)Math.Round(v)) + " px";
            textSize.ValueChanged += (s, e) => TextSet("ps_h_text_size", t => { t.SizePx = textSize.Value; t.SizeChanging(); }, textSize, false);
            textSize.Committed += (s, e) => { TextLayer t = CurText; if (t != null) { t.Touch(); canvas.Redraw(); } EndChange(); };
            textColors.Selected = Color.White;
            textColors.SelectedChanged += (s, e) => TextSet("ps_h_text_color", t => t.Color = textColors.Selected, textColors);
            boxColors.Selected = Color.Black;
            boxColors.SelectedChanged += (s, e) => TextSet("ps_h_text_style", t => t.BoxColor = boxColors.Selected, boxColors);
            textStyles = new ChipToggles(new Func<string>[] { () => "", () => "", () => L.T("ed_outline"), () => L.T("ed_shadow"), () => L.T("ed_box") }, new[] { Ico.Bold, Ico.Italic, null, null, null });
            textStyles.On = new[] { true, false, true, true, false };
            textStyles.Changed += (s, e) => { bool[] o = textStyles.On; TextSet("ps_h_text_style", t => { t.Bold = o[0]; t.Italic = o[1]; t.Outline = o[2]; t.Shadow = o[3]; t.Box = o[4]; }, null); LayoutProps(); };
            textAlign = new Segments(() => "", () => "", () => "") { FontSize = 8.5f };
            textAlign.Glyphs = new[] { Ico.AlignLeft, Ico.AlignCenter, Ico.AlignRight };
            textAlign.SetSilently(1);
            textAlign.SelectedChanged += (s, e) =>
            {
                StringAlignment a = textAlign.SelectedIndex == 0 ? StringAlignment.Near : textAlign.SelectedIndex == 2 ? StringAlignment.Far : StringAlignment.Center;
                TextSet("ps_h_text_style", t => t.Align = a, null);
            };
            // ---- adjustments
            foreach (Slider sl in new[] { aBright, aContrast, aSat, aWarm, aBlur, aSharp })
            {
                Slider me = sl;
                me.ValueChanged += (s, e) => AdjSet(a =>
                {
                    if (me == aBright) a.Bright = me.Value; else if (me == aContrast) a.Contrast = me.Value; else if (me == aSat) a.Sat = me.Value;
                    else if (me == aWarm) a.Warmth = me.Value; else if (me == aBlur) a.Blur = me.Value; else a.Sharpen = me.Value;
                }, me);
                me.Committed += (s, e) => EndChange();
            }
            aBlur.Format = aSharp.Format = v => ((int)Math.Round(v)).ToString();
            aFilter.LabelOf = k => L.T(k);
            aFilter.SetItems(Adjust.FilterKeys, Adjust.FilterKeys[0]);
            aFilter.SelectedChanged += (s, e) => { int i = Array.IndexOf(Adjust.FilterKeys, aFilter.Selected); AdjSet(a => a.Filter = Math.Max(0, i), null); };
            levels.Changed += () => ToneChanged(levels);
            levels.Committed += () => EndChange();
            curves.Changed += () => ToneChanged(curves);
            curves.Committed += () => EndChange();
            hueRange.Items = new List<string>();
            foreach (string k in new[] { "ps_hr_all", "ps_hr_red", "ps_hr_yellow", "ps_hr_green", "ps_hr_cyan", "ps_hr_blue", "ps_hr_magenta" }) hueRange.Items.Add(L.T(k));
            hueRange.Selected = hueRange.Items[0];
            hueRange.SelectedChanged += (s, e) => { LoadProps(); };
            foreach (Slider sl in new[] { hHue, hSat, hLight })
            {
                Slider me = sl;
                me.ValueChanged += (s, e) =>
                {
                    DocLayer l = doc == null ? null : doc.Cur;
                    if (loading || l == null || l.Kind != LayerKind.Adjust || l.Tone == null) return;
                    Change("ps_h_adjust", me);
                    int r = Math.Max(0, hueRange.Items.IndexOf(hueRange.Selected));
                    if (me == hHue) l.Tone.Hue[r] = me.Value; else if (me == hSat) l.Tone.Sat[r] = me.Value; else l.Tone.Light[r] = me.Value;
                    l.Version++; canvas.Redraw(); layers.Invalidate();
                };
                me.Committed += (s, e) => EndChange();
            }
            bAdjReset = Small(() => L.T("ps_reset"), Ico.Refresh, ResetAdjust);
            bAuto = Small(() => L.T("ps_auto"), "\uE945", AutoLevels);
            propCtl.AddRange(new Control[] { selMode, feather, tolerance, contiguous, bSubject, bSelAll, bInvert, bDeselect, bSelCopy, bSelFill, bSelClear, bSelCrop,
                brushSize, brushHard, brushOpac, brushColors, cropRatio, bCropApply, bCropCancel, nX, nY, nW, nH, nAngle, bFlipH, bFlipV, bAL, bAC, bAR, bAT, bAM, bAB,
                bRemoveBg, bShowAll, textBox, font, textSize, textColors, boxColors, textStyles, textAlign, aBright, aContrast, aSat, aWarm, aBlur, aSharp, aFilter,
                levels, curves, hueRange, hHue, hSat, hLight, bAdjReset, bAuto });
            foreach (Control c in propCtl) { if (c is UiControl) ((UiControl)c).Surface = Surface.Card; c.Visible = false; }
            props.Controls.AddRange(propCtl.ToArray());
        }

        static float Ratio(string k) { string[] p = k.Split(':'); return float.Parse(p[0], CultureInfo.InvariantCulture) / float.Parse(p[1], CultureInfo.InvariantCulture); }

        TextLayer CurText
        {
            get
            {
                DocLayer l = doc == null ? null : doc.Cur;
                return l != null && l.Kind == LayerKind.Text ? l.Text : null;
            }
        }

        /// Changes the selected text (or, with none, the style the next text gets).
        void TextSet(string hist, Action<TextLayer> set, object key, bool sharp = true)
        {
            if (loading) return;
            set(textStyle);
            TextLayer t = CurText;
            if (t == null) return;
            Change(hist, key);
            set(t);
            if (sharp) t.Touch();
            doc.Cur.Version++;
            canvas.Redraw(); layers.Invalidate();
            SyncTransformFields();
        }

        void AdjSet(Action<Adjust> set, object key)
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (loading || l == null || l.Kind != LayerKind.Adjust) return;
            Change("ps_h_adjust", key);
            if (l.Adj == null) l.Adj = new Adjust();
            set(l.Adj);
            l.Version++;
            canvas.Redraw(); layers.Invalidate();
        }

        void ToneChanged(object key)
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (loading || l == null || l.Kind != LayerKind.Adjust) return;
            // the control already changed the curve: put the old one in the history first
            Tone now = l.Tone.Clone();
            if (key != editKey || (DateTime.Now - editAt).TotalSeconds > 2.5)
            {
                l.Tone = toneBefore ?? now;
                Change("ps_h_adjust", key);
                l.Tone = now;
                if (key == levels) levels.Tone = now; else curves.Tone = now;
            }
            else { editAt = DateTime.Now; }
            toneBefore = now.Clone();
            l.Version++;
            canvas.Redraw(); layers.Invalidate();
        }
        Tone toneBefore;

        void ResetAdjust()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null || l.Kind != LayerKind.Adjust) return;
            Change("ps_h_adjust");
            if (l.AdjType == AdjKind.Light) l.Adj = new Adjust();
            else l.Tone = new Tone();
            l.Version++;
            AfterChange(true);
        }

        /// Levels: the darkest and lightest 0.5% become black and white.
        void AutoLevels()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null || l.Kind != LayerKind.Adjust || l.Tone == null) return;
            int[] h = HistBelow(l);
            long total = 0; foreach (int c in h) total += c;
            if (total == 0) return;
            long cut = total / 200, acc = 0; int lo = 0, hi = 255;
            for (int i = 0; i < 256; i++) { acc += h[i]; if (acc > cut) { lo = i; break; } }
            acc = 0;
            for (int i = 255; i >= 0; i--) { acc += h[i]; if (acc > cut) { hi = i; break; } }
            if (hi - lo < 8) return;
            Change("ps_h_adjust");
            l.Tone.InBlack = lo; l.Tone.InWhite = hi; l.Tone.Gamma = 1;
            l.Version++;
            AfterChange(true);
        }

        int[] histCache; string histKey;
        /// The light of everything under an adjustment (what it changes).
        int[] HistBelow(DocLayer l)
        {
            int i = doc.Layers.IndexOf(l);
            string key = l.Id + "/" + doc.Undo.Count + "/" + changes;
            if (histKey == key && histCache != null) return histCache;
            float k = Math.Min(1f, 600f / Math.Max(doc.W, doc.H));
            using (Bitmap b = doc.Compose(k, i)) histCache = Hist.Of(b);
            histKey = key;
            return histCache;
        }

        // ------------------------------------------------------------------ filling the controls from the layer
        void LoadProps()
        {
            if (doc == null) return;
            loading = true;
            try
            {
                DocLayer l = doc.Cur;
                // layer area
                if (l != null)
                {
                    blend.Selected = blend.Items[(int)l.Blend]; blend.Invalidate();
                    opacity.SetValue(l.Opacity * 100, false);
                }
                blend.Enabled = opacity.Enabled = l != null && l.Kind != LayerKind.Group;
                TextLayer t = CurText ?? textStyle;
                if (textBox.Box.Text != (t.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n")) textBox.Box.Text = (t.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                font.Selected = t.FontName; font.Invalidate();
                if (t.SizePx > textSize.Max) textSize.Max = t.SizePx;
                textSize.SetValue(t.SizePx, false);
                textColors.Selected = t.Color; textColors.Invalidate();
                boxColors.Selected = t.BoxColor.IsEmpty ? Color.Black : t.BoxColor; boxColors.Invalidate();
                textStyles.On = new[] { t.Bold, t.Italic, t.Outline, t.Shadow, t.Box }; textStyles.Invalidate();
                textAlign.SetSilently(t.Align == StringAlignment.Near ? 0 : t.Align == StringAlignment.Far ? 2 : 1);
                if (l != null && l.Kind == LayerKind.Adjust)
                {
                    Adjust a = l.Adj ?? new Adjust();
                    aBright.SetValue(a.Bright, false); aContrast.SetValue(a.Contrast, false); aSat.SetValue(a.Sat, false);
                    aWarm.SetValue(a.Warmth, false); aBlur.SetValue(a.Blur, false); aSharp.SetValue(a.Sharpen, false);
                    aFilter.Selected = Adjust.FilterKeys[Math.Max(0, Math.Min(Adjust.FilterKeys.Length - 1, a.Filter))]; aFilter.Invalidate();
                    if (l.Tone == null) l.Tone = new Tone();
                    toneBefore = l.Tone.Clone();
                    levels.Tone = curves.Tone = l.Tone;
                    if (l.AdjType == AdjKind.Levels || l.AdjType == AdjKind.Curves) { int[] h = HistBelow(l); levels.Histogram = curves.Histogram = h; }
                    levels.Invalidate(); curves.Invalidate();
                    int r = Math.Max(0, hueRange.Items.IndexOf(hueRange.Selected));
                    hHue.SetValue(l.Tone.Hue[r], false); hSat.SetValue(l.Tone.Sat[r], false); hLight.SetValue(l.Tone.Light[r], false);
                }
                SyncTransformFields();
                bRemoveBg.Invalidate();
            }
            finally { loading = false; }
        }

        void SyncTransformFields()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null) return;
            RectangleF b = l.Bounds(doc);
            if (!nX.Editing) nX.SetValue((float)Math.Round(b.X));
            if (!nY.Editing) nY.SetValue((float)Math.Round(b.Y));
            if (l.Kind == LayerKind.Image && l.Pixels != null)
            {
                if (!nW.Editing) nW.SetValue((float)Math.Round(l.Pixels.Width * l.Scale));
                if (!nH.Editing) nH.SetValue((float)Math.Round(l.Pixels.Height * l.Scale));
                if (!nAngle.Editing) nAngle.SetValue((float)Math.Round(l.Angle, 1));
            }
            else
            {
                if (!nW.Editing) nW.SetValue((float)Math.Round(b.Width));
                if (!nH.Editing) nH.SetValue((float)Math.Round(b.Height));
                if (!nAngle.Editing) nAngle.SetValue(l.Kind == LayerKind.Text ? (float)Math.Round(l.Text.Angle, 1) : 0);
            }
            if (l.Kind == LayerKind.Text && !loading && Math.Abs(textSize.Value - l.Text.SizePx) > 0.5f)
            {
                bool was = loading; loading = true;
                if (l.Text.SizePx > textSize.Max) textSize.Max = l.Text.SizePx;
                textSize.SetValue(l.Text.SizePx, false); loading = was;
            }
        }

        // ------------------------------------------------------------------ laying out the panel for what's chosen
        void Head(string key) { rowLabels.Add(new KeyValuePair<Func<string>, Rectangle>(() => L.T(key), new Rectangle(0, py - props.Offset, pw, Theme.S(20)))); py += Theme.S(24); }
        void Put(Control c, int h) { c.Bounds = Theme.Mirror(new Rectangle(0, py - props.Offset, pw, h), props.Width); placed.Add(c); py += h + Theme.S(10); }
        void PutRow(int h, params Control[] cs)
        {
            int gap = Theme.S(6), w = (pw - gap * (cs.Length - 1)) / cs.Length;
            for (int i = 0; i < cs.Length; i++) { cs[i].Bounds = Theme.Mirror(new Rectangle(i * (w + gap), py - props.Offset, i == cs.Length - 1 ? pw - i * (w + gap) : w, h), props.Width); placed.Add(cs[i]); }
            py += h + Theme.S(10);
        }
        void Hint(string text)
        {
            Font f = Theme.F(8.5f);
            int h = TextRenderer.MeasureText(text, f, new Size(pw, 999), TextFormatFlags.WordBreak).Height + Theme.S(2);
            hints.Add(new KeyValuePair<string, Rectangle>(text, new Rectangle(0, py - props.Offset, pw, h)));
            py += h + Theme.S(10);
        }
        /// A switch with its words in front.
        void PutToggle(Toggle t, string key)
        {
            int h = Theme.S(28), tw = Theme.S(42);
            t.Bounds = Theme.Mirror(new Rectangle(pw - tw, py - props.Offset + (h - Theme.S(24)) / 2, tw, Theme.S(24)), props.Width);
            placed.Add(t);
            hints.Add(new KeyValuePair<string, Rectangle>("\u0001" + L.T(key), new Rectangle(0, py - props.Offset, pw - tw - Theme.S(8), h)));
            py += h + Theme.S(8);
        }
        void Gap() { py += Theme.S(6); }

        void LayoutProps()
        {
            if (doc == null || props.Width <= 0 || propCtl.Count == 0) return;
            placed.Clear(); hints.Clear(); rowLabels.Clear();
            py = Theme.S(2); pw = props.Width - Theme.S(10);
            int sh = Theme.S(30), bh = Theme.S(32);
            DocLayer l = doc.Cur;
            switch (canvas.Tool)
            {
                case PhotoTool.Marquee: case PhotoTool.Ellipse: case PhotoTool.Lasso: case PhotoTool.Wand:
                    Head("ps_sel_mode"); Put(selMode, Theme.S(32));
                    if (canvas.Tool == PhotoTool.Wand) { Head("ps_tolerance"); Put(tolerance, sh); PutToggle(contiguous, "ps_contiguous"); }
                    Head("ps_feather"); Put(feather, sh);
                    Gap();
                    Put(bSubject, Theme.S(36));
                    if (!BgRemover.Ready) Hint(L.T("ps_subject_dl"));
                    PutRow(bh, bSelAll, bInvert);
                    if (doc.Sel != null)
                    {
                        Head("ps_with_sel");
                        PutRow(bh, bSelCopy, bSelFill);
                        PutRow(bh, bSelClear, bSelCrop);
                        Put(bDeselect, bh);
                    }
                    Hint(L.T("ps_sel_hint"));
                    break;
                case PhotoTool.Brush: case PhotoTool.Eraser: case PhotoTool.Remove:
                    Head("ps_size"); Put(brushSize, sh);
                    if (canvas.Tool != PhotoTool.Remove)
                    {
                        Head("ps_hardness"); Put(brushHard, sh);
                        Head("ps_opacity"); Put(brushOpac, sh);
                    }
                    if (canvas.Tool == PhotoTool.Brush) { Head("ps_color"); Put(brushColors, Theme.S(34)); Hint(L.T("ps_brush_hint")); }
                    else if (canvas.Tool == PhotoTool.Eraser) Hint(L.T(l != null && (l.Kind == LayerKind.Image || l.Kind == LayerKind.Adjust) ? "ps_eraser_mask" : l != null && l.Kind == LayerKind.Paint ? "ps_eraser_paint" : "ps_eraser_none"));
                    else Hint(L.T("ps_remove_hint"));
                    if (doc.Sel != null && canvas.Tool != PhotoTool.Remove) Hint(L.T("ps_inside_sel"));
                    break;
                case PhotoTool.Crop:
                    Head("ps_crop_shape"); Put(cropRatio, cropRatio.NeededHeight(8));
                    PutRow(Theme.S(36), bCropCancel, bCropApply);
                    Hint(L.T("ps_crop_hint"));
                    break;
                case PhotoTool.Text:
                    if (l != null && l.Kind == LayerKind.Text) LayerProps(l);
                    else { Hint(L.T("ps_text_hint")); TextProps(false); }
                    break;
                case PhotoTool.Eyedropper: Hint(L.T("ps_dropper_hint")); Head("ps_color"); Put(brushColors, Theme.S(34)); break;
                case PhotoTool.Hand: Hint(L.T("ps_hand_hint")); break;
                case PhotoTool.Zoom: Hint(L.T("ps_zoom_hint")); break;
                default:
                    if (l == null) Hint(L.T("ps_no_layer"));
                    else LayerProps(l);
                    break;
            }
            props.ContentHeight = py;
            props.ClampTo(props.MaxOffset);
            foreach (Control c in propCtl) { bool v = placed.Contains(c); if (c.Visible != v) c.Visible = v; }
            props.Labels.Clear(); props.Labels.AddRange(rowLabels);
            props.Invalidate();
            InvalidatePanelHead();
        }

        void TextProps(bool layer)
        {
            if (layer) { Head("ps_text"); Put(textBox, Theme.S(70)); }
            Head("ps_font"); Put(font, Theme.S(34));
            Head("ps_size"); Put(textSize, Theme.S(30));
            Head("ps_color"); Put(textColors, Theme.S(34));
            Head("ps_style"); Put(textStyles, Theme.S(32));
            if (textStyles.On[4]) { Head("ed_box"); Put(boxColors, Theme.S(34)); }
            Put(textAlign, Theme.S(32));
        }

        void LayerProps(DocLayer l)
        {
            int bh = Theme.S(32);
            switch (l.Kind)
            {
                case LayerKind.Text:
                    TextProps(true);
                    Head("ps_place"); PutRow(bh, nX, nY); PutRow(bh, nAngle);
                    AlignRows();
                    break;
                case LayerKind.Image:
                    Head("ps_place"); PutRow(bh, nX, nY); PutRow(bh, nW, nH); PutRow(bh, nAngle, bFlipH, bFlipV);
                    AlignRows();
                    Head("ps_background");
                    Put(bRemoveBg, Theme.S(36));
                    if (!BgRemover.Ready) Hint(L.T("ps_subject_dl"));
                    if (l.Mask != null) Put(bShowAll, bh);
                    break;
                case LayerKind.Paint:
                    Hint(L.T("ps_paint_hint"));
                    if (!l.Bounds(doc).IsEmpty) { Head("ps_place"); PutRow(bh, nX, nY); AlignRows(); }
                    if (l.Mask != null) Put(bShowAll, bh);
                    break;
                case LayerKind.Group:
                    Hint(L.T("ps_group_hint", LayerTree.Children(doc, l.Id).Count));
                    if (!l.Bounds(doc).IsEmpty) { Head("ps_place"); PutRow(bh, nX, nY); AlignRows(); }
                    break;
                case LayerKind.Adjust:
                    switch (l.AdjType)
                    {
                        case AdjKind.Light:
                            Head("adj_bright"); Put(aBright, Theme.S(28)); Head("adj_contrast"); Put(aContrast, Theme.S(28));
                            Head("adj_sat"); Put(aSat, Theme.S(28)); Head("adj_warm"); Put(aWarm, Theme.S(28));
                            Head("adj_blur"); Put(aBlur, Theme.S(28)); Head("adj_sharp"); Put(aSharp, Theme.S(28));
                            Head("ps_filter"); Put(aFilter, aFilter.NeededHeight(Adjust.FilterKeys.Length));
                            break;
                        case AdjKind.Levels:
                            Put(levels, Theme.S(190)); PutRow(bh, bAuto, bAdjReset); Hint(L.T("ps_levels_hint"));
                            break;
                        case AdjKind.Curves:
                            Put(curves, Math.Min(pw, Theme.S(260))); Put(bAdjReset, bh); Hint(L.T("ps_curves_hint"));
                            break;
                        case AdjKind.HueSat:
                            Head("ps_hue_range"); Put(hueRange, Theme.S(34));
                            Head("ps_hue"); Put(hHue, Theme.S(28)); Head("ps_saturation"); Put(hSat, Theme.S(28)); Head("ps_lightness"); Put(hLight, Theme.S(28));
                            Put(bAdjReset, bh);
                            break;
                    }
                    if (l.AdjType == AdjKind.Light) Put(bAdjReset, bh);
                    Hint(L.T("ps_adj_mask_hint"));
                    if (l.Mask != null) Put(bShowAll, bh);
                    break;
            }
        }

        void AlignRows()
        {
            Head(doc.Sel != null ? "ps_align_sel" : "ps_align");
            PutRow(Theme.S(32), bAL, bAC, bAR, bAT, bAM, bAB);
        }

        void PaintPropsExtra(Graphics g)
        {
            Font f = Theme.F(8.5f), tf = Theme.F(9f);
            foreach (KeyValuePair<string, Rectangle> h in hints)
            {
                Rectangle r = Theme.Mirror(h.Value, props.Width);
                if (h.Key.Length > 0 && h.Key[0] == '\u0001') Theme.Text(g, h.Key.Substring(1), tf, Theme.Ink, r, Align.Start);
                else Theme.Text(g, h.Key, f, Theme.InkSoft, r, Align.Start, false, true);
            }
        }

        // ------------------------------------------------------------------ place & size
        const int TrX = 0, TrY = 1, TrW = 2, TrH = 3, TrA = 4;
        void Transform(int what, float v)
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null || loading || doc.IsLocked(l)) { SyncTransformFields(); return; }
            RectangleF b = l.Bounds(doc);
            if (b.IsEmpty && l.Kind != LayerKind.Image) return;
            if (what == TrX || what == TrY)
            {
                float dx = what == TrX ? v - b.X : 0, dy = what == TrY ? v - b.Y : 0;
                if (Math.Abs(dx) < 0.01f && Math.Abs(dy) < 0.01f) return;
                Change("ps_h_move");
                MoveLayer(l, dx, dy);
            }
            else if (what == TrA)
            {
                Change("ps_h_turn");
                v = ((v % 360) + 360) % 360; if (v > 180) v -= 360;
                if (l.Kind == LayerKind.Image) l.Angle = v;
                else if (l.Kind == LayerKind.Text) { l.Text.Angle = v; l.Text.Touch(); }
            }
            else if (l.Kind == LayerKind.Image && l.Pixels != null && v >= 1)
            {
                Change("ps_h_resize");
                l.Scale = what == TrW ? v / l.Pixels.Width : v / l.Pixels.Height;
            }
            l.Version++;
            AfterChange(false);
        }

        /// Moves a layer (a painting, a group with everything in it) by an amount.
        void MoveLayer(DocLayer l, float dx, float dy)
        {
            if (l.Kind == LayerKind.Image) { l.X += dx; l.Y += dy; }
            else if (l.Kind == LayerKind.Text) l.Text.Pos = new PointF(l.Text.Pos.X + dx, l.Text.Pos.Y + dy);
            else if (l.Kind == LayerKind.Paint) { doc.PaintToImage(l); l.X += dx; l.Y += dy; }
            else if (l.Kind == LayerKind.Group)
                foreach (DocLayer c in doc.Layers.ToArray()) if (c.Group == l.Id) MoveLayer(c, dx, dy);
            l.Version++;
        }

        /// Lines a layer up with the photo's edges or middle (or the selection's, when there is one).
        void Align2(int how)
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null || doc.IsLocked(l)) return;
            RectangleF b = l.Bounds(doc);
            if (b.IsEmpty) return;
            RectangleF to = new RectangleF(0, 0, doc.W, doc.H);
            if (doc.Sel != null) { Rectangle sb = Selection.Bounds(doc.Sel, doc.W, doc.H); if (!sb.IsEmpty) to = sb; }
            float dx = 0, dy = 0;
            switch (how)
            {
                case 0: dx = to.Left - b.Left; break;
                case 1: dx = to.X + to.Width / 2 - (b.X + b.Width / 2); break;
                case 2: dx = to.Right - b.Right; break;
                case 3: dy = to.Top - b.Top; break;
                case 4: dy = to.Y + to.Height / 2 - (b.Y + b.Height / 2); break;
                case 5: dy = to.Bottom - b.Bottom; break;
            }
            if (Math.Abs(dx) < 0.01f && Math.Abs(dy) < 0.01f) return;
            Change("ps_h_align");
            MoveLayer(l, dx, dy);
            AfterChange(false);
        }

        void FlipLayer(bool h)
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null || l.Kind != LayerKind.Image || doc.IsLocked(l)) return;
            Change("ps_h_flip");
            if (h) l.FlipX = !l.FlipX; else l.FlipY = !l.FlipY;
            l.Version++;
            AfterChange(false);
        }
    }
}
