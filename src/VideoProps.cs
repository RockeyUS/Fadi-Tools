using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace FileConverter
{
    /// A keyframe switch beside a setting: filled when there's a keyframe at the playhead, outlined when the setting
    /// moves somewhere else in the clip. Click to add or remove one here.
    public class KeyBtn : UiControl
    {
        public Func<int> State;   // 0 none, 1 keys elsewhere, 2 a key here
        public KeyBtn() { Cursor = Cursors.Hand; Surface = Surface.Card; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            int st = State == null ? 0 : State();
            float cx = Width / 2f, cy = Height / 2f, s = Math.Min(Width, Height) * 0.3f;
            PointF[] d = { new PointF(cx, cy - s), new PointF(cx + s, cy), new PointF(cx, cy + s), new PointF(cx - s, cy) };
            if (hovering) Theme.FillRound(g, new RectangleF(2, 2, Width - 4, Height - 4), Theme.S(6), Theme.Hover);
            if (st == 2) using (SolidBrush b = new SolidBrush(Theme.AccentColor)) g.FillPolygon(b, d);
            using (Pen p = new Pen(st == 0 ? Theme.InkFaint : Theme.AccentColor, Theme.S(1.5f))) g.DrawPolygon(p, d);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
        protected override string AccName { get { return L.T("vs_keyframe"); } }
        protected override AccessibleRole AccRole { get { return AccessibleRole.PushButton; } }
    }

    public partial class VideoStudio
    {
        int tab;
        Segments tabs;
        // basic
        readonly Slider speed = new Slider { Min = 0.1f, Max = 10, Value = 1, Curved = true }, volume = new Slider { Min = 0, Max = 200, Value = 100 },
                        fadeIn = new Slider { Min = 0, Max = 3, Value = 0 }, fadeOut = new Slider { Min = 0, Max = 3, Value = 0 }, length = new Slider { Min = 0.5f, Max = 60, Value = 4, Curved = true };
        readonly Toggle denoise = new Toggle(), mute = new Toggle();
        readonly KeyBtn kVol = new KeyBtn();
        Btn bDetach2, bBg, bReplace;
        // adjust
        readonly Slider aBright = A(), aContrast = A(), aSat = A(), aWarm = A(), aBlur = new Slider { Min = 0, Max = 100 }, aSharp = new Slider { Min = 0, Max = 100 };
        readonly ChipGrid aFilter = new ChipGrid { Columns = 3, RowH = 32 };
        Btn bAdjReset;
        // transition
        readonly ChipGrid trans = new ChipGrid { Columns = 3, RowH = 34 };
        readonly Slider transDur = new Slider { Min = 0.2f, Max = 2, Value = 0.6f };
        // motion
        readonly Slider mX = new Slider { Min = -50, Max = 150, Value = 50 }, mY = new Slider { Min = -50, Max = 150, Value = 50 }, mScale = new Slider { Min = 5, Max = 400, Value = 100, Curved = true },
                        mRot = new Slider { Min = -180, Max = 180 }, mOpacity = new Slider { Min = 0, Max = 100, Value = 100 }, animInDur = new Slider { Min = 0.1f, Max = 3, Value = 0.5f }, animOutDur = new Slider { Min = 0.1f, Max = 3, Value = 0.5f };
        readonly KeyBtn kX = new KeyBtn(), kY = new KeyBtn(), kScale = new KeyBtn(), kRot = new KeyBtn(), kOpacity = new KeyBtn();
        readonly ChipGrid animIn = new ChipGrid { Columns = 3, RowH = 32 }, animOut = new ChipGrid { Columns = 3, RowH = 32 };
        // text
        readonly MultiField textBox = new MultiField();
        readonly DropDown font = new DropDown { PreviewFonts = true }, capFont = new DropDown { PreviewFonts = true };
        readonly Slider textSize = new Slider { Min = 10, Max = 400, Value = 72, Curved = true };
        readonly Swatches textColor = new Swatches();
        ChipToggles textStyles;
        Segments textAlign;
        // captions
        readonly ChipGrid capStyle = new ChipGrid { Columns = 2, RowH = 36 };
        readonly Slider capSize = new Slider { Min = 50, Max = 200, Value = 100 }, capY = new Slider { Min = 8, Max = 92, Value = 82 };
        readonly Swatches capColor = new Swatches(), capHigh = new Swatches();
        readonly MultiField capWords = new MultiField();
        Btn bCapSrt;
        // project
        readonly ChipGrid aspect = new ChipGrid { Columns = 3, RowH = 34 }, fps = new ChipGrid { Columns = 4, RowH = 32 };
        readonly Segments fill = new Segments(() => L.T("vs_fill_blur"), () => L.T("vs_fill_black"), () => L.T("vs_fill_color")) { FontSize = 8.75f };
        readonly Swatches fillColor = new Swatches { LivePreview = false };

        static Slider A() { return new Slider { Min = -100, Max = 100, Value = 0, Format = v => (v > 0 ? "+" : "") + ((int)Math.Round(v)) }; }

        readonly List<Control> propCtl = new List<Control>();
        readonly HashSet<Control> placed = new HashSet<Control>();
        readonly List<KeyValuePair<string, Rectangle>> hints = new List<KeyValuePair<string, Rectangle>>();
        readonly List<KeyValuePair<Func<string>, Rectangle>> heads = new List<KeyValuePair<Func<string>, Rectangle>>();
        int py, pw;
        bool filling;

        static readonly string[] TransKeys = { "none", "fade", "fadeblack", "fadewhite", "dissolve", "slideleft", "wipeleft", "circleopen", "zoomin", "smoothleft", "radial", "hblur" };
        static readonly string[] AnimKeys = { "none", "fade", "slideup", "slideleft", "pop" };

        VClip Sel { get { return timeline.Selected; } }
        VTrackKind? SelTrack { get { VClip c = Sel; if (c == null) return null; VTrack t = P.TrackOf(c); return t == null ? (VTrackKind?)null : t.Kind; } }
        bool IsOverlay(VClip c) { VTrack t = c == null ? null : P.TrackOf(c); return t != null && t.Kind == VTrackKind.Overlay && (c.Kind == VKind.Video || c.Kind == VKind.Image); }

        string[] TabNames(VClip c)
        {
            if (c == null) return new string[0];
            if (c.Kind == VKind.Text) return new[] { "vs_tab_text", "vs_tab_motion" };
            if (c.Kind == VKind.Captions) return new[] { "vs_tab_style", "vs_tab_words" };
            if (c.Kind == VKind.Audio) return new[] { "vs_tab_basic" };
            if (IsOverlay(c)) return new[] { "vs_tab_basic", "vs_tab_adjust", "vs_tab_motion" };
            return new[] { "vs_tab_basic", "vs_tab_adjust", "vs_tab_trans" };
        }
        int MaxTab(VClip c) { return Math.Max(0, TabNames(c).Length - 1); }

        string PanelTitle()
        {
            VClip c = Sel;
            if (c == null) return L.T("vs_project");
            switch (c.Kind)
            {
                case VKind.Text: return L.T("vs_k_text");
                case VKind.Captions: return L.T("vs_captions");
                case VKind.Audio: return L.T("vs_k_audio");
                case VKind.Image: return L.T(IsOverlay(c) ? "vs_k_overlay" : "vs_k_photo");
                default: return L.T(IsOverlay(c) ? "vs_k_overlay" : "vs_k_video");
            }
        }

        Btn Small(Func<string> label, string glyph, Action a)
        {
            Btn b = new Btn(label, glyph, BtnKind.Secondary) { FontSize = 8.75f, GlyphSize = 9.5f };
            b.Click += (s, e) => a();
            return b;
        }

        void InitProps()
        {
            props.Surface = Surface.Card;
            tabs = new Segments(() => "") { FontSize = 8.5f };
            tabs.SelectedChanged += (s, e) => { tab = tabs.SelectedIndex; props.ResetScroll(); LayoutProps(); };
            // ---- basic
            speed.Format = v => v.ToString(v < 1 ? "0.0#" : "0.0#", CultureInfo.InvariantCulture) + "×";
            speed.ValueChanged += (s, e) => Edit("vs_h_speed", speed, c =>
            {
                // the clip keeps its place in the source; it just plays faster or slower (and gets shorter or longer)
                double keep = c.Length;
                c.Speed = Math.Round(speed.Value, 2);
                double k = keep / c.Length;
                foreach (List<VKey> l in c.Keys.Values) foreach (VKey key in l) key.T /= k;
            }, false);
            speed.Committed += (s, e) => { EndChange(); AfterChange(false); };
            volume.Format = v => ((int)Math.Round(v)) + "%";
            volume.ValueChanged += (s, e) => Edit("vs_h_volume", volume, c => { float v = volume.Value / 100f; if (c.HasKeys("vol")) c.SetKey("vol", Playhead, v); else c.Volume = v; kVol.Invalidate(); }, false);
            volume.Committed += (s, e) => EndChange();
            kVol.State = () => KeyState("vol");
            kVol.Click += (s, e) => ToggleKey("vol", volume.Value / 100f);
            kVol.MouseEnter += (s, e) => tips.SetToolTip(kVol, L.T("vs_key_tip"));
            fadeIn.Format = fadeOut.Format = v => v < 0.05 ? L.T("vs_off") : v.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            fadeIn.ValueChanged += (s, e) => Edit("vs_h_fade", fadeIn, c => c.FadeIn = fadeIn.Value < 0.05 ? 0 : Math.Min(fadeIn.Value, c.Length / 2), false);
            fadeOut.ValueChanged += (s, e) => Edit("vs_h_fade", fadeOut, c => c.FadeOut = fadeOut.Value < 0.05 ? 0 : Math.Min(fadeOut.Value, c.Length / 2), false);
            fadeIn.Committed += (s, e) => EndChange(); fadeOut.Committed += (s, e) => EndChange();
            length.Format = v => v.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            length.ValueChanged += (s, e) => Edit("vs_h_trim", length, c => { c.In = 0; c.Out = Math.Round(length.Value, 1); }, false);
            length.Committed += (s, e) => { EndChange(); AfterChange(false); };
            denoise.CheckedChanged += (s, e) => Edit("vs_h_denoise", null, c => c.Denoise = denoise.Checked, false);
            mute.CheckedChanged += (s, e) => Edit("vs_h_mute", null, c => c.Muted = mute.Checked, false);
            bDetach2 = Small(() => L.T("vs_detach"), "", DetachAudio);
            bBg = Small(() => L.T("vs_remove_bg"), Ps.Subject, RemoveVideoBg);
            bReplace = Small(() => L.T("vs_replace"), Ico.Refresh, ReplaceMedia);
            // ---- adjust
            foreach (Slider sl in new[] { aBright, aContrast, aSat, aWarm, aBlur, aSharp })
            {
                Slider me = sl;
                me.ValueChanged += (s, e) => Edit("vs_h_adjust", me, c =>
                {
                    Adjust a = c.Adj ?? new Adjust();
                    if (me == aBright) a.Bright = me.Value; else if (me == aContrast) a.Contrast = me.Value; else if (me == aSat) a.Sat = me.Value;
                    else if (me == aWarm) a.Warmth = me.Value; else if (me == aBlur) a.Blur = me.Value; else a.Sharpen = me.Value;
                    c.Adj = a;
                }, false);
                me.Committed += (s, e) => EndChange();
            }
            aFilter.LabelOf = k => L.T(k);
            aFilter.SetItems(Adjust.FilterKeys, Adjust.FilterKeys[0]);
            aFilter.SelectedChanged += (s, e) => Edit("vs_h_adjust", null, c => { if (c.Adj == null) c.Adj = new Adjust(); c.Adj.Filter = Math.Max(0, Array.IndexOf(Adjust.FilterKeys, aFilter.Selected)); }, false);
            bAdjReset = Small(() => L.T("ps_reset"), Ico.Refresh, () => Edit("vs_h_adjust", null, c => c.Adj = new Adjust(), true));
            // ---- transition
            trans.LabelOf = k => L.T("vs_tr_" + k);
            trans.SetItems(TransKeys, "none");
            trans.SelectedChanged += (s, e) => Edit("vs_h_trans", null, c => c.Trans = trans.Selected, true);
            transDur.Format = v => v.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            transDur.ValueChanged += (s, e) => Edit("vs_h_trans", transDur, c => c.TransDur = Math.Round(transDur.Value, 1), false);
            transDur.Committed += (s, e) => { EndChange(); AfterChange(false); };
            // ---- motion
            mX.Format = mY.Format = v => ((int)Math.Round(v)) + "%";
            mScale.Format = v => ((int)Math.Round(v)) + "%";
            mRot.Format = v => ((int)Math.Round(v)) + "°";
            mOpacity.Format = v => ((int)Math.Round(v)) + "%";
            Bind(mX, kX, "x", v => v / 100f, c => c.X, (c, v) => c.X = v);
            Bind(mY, kY, "y", v => v / 100f, c => c.Y, (c, v) => c.Y = v);
            Bind(mScale, kScale, "scale", v => v / 100f, c => c.Scale, (c, v) => c.Scale = v);
            Bind(mRot, kRot, "rot", v => v, c => c.Rot, (c, v) => c.Rot = v);
            Bind(mOpacity, kOpacity, "opacity", v => v / 100f, c => c.Opacity, (c, v) => c.Opacity = v);
            animIn.LabelOf = k => L.T("vs_an_" + k); animOut.LabelOf = k => L.T("vs_an_" + k);
            animIn.SetItems(new[] { "none", "fade", "slideup", "slideleft", "pop", "type" }, "none");
            animOut.SetItems(AnimKeys, "none");
            animIn.SelectedChanged += (s, e) => Edit("vs_h_anim", null, c => c.AnimIn = animIn.Selected, true);
            animOut.SelectedChanged += (s, e) => Edit("vs_h_anim", null, c => c.AnimOut = animOut.Selected, true);
            animInDur.Format = animOutDur.Format = v => v.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            animInDur.ValueChanged += (s, e) => Edit("vs_h_anim", animInDur, c => c.AnimInDur = Math.Round(animInDur.Value, 1), false);
            animOutDur.ValueChanged += (s, e) => Edit("vs_h_anim", animOutDur, c => c.AnimOutDur = Math.Round(animOutDur.Value, 1), false);
            animInDur.Committed += (s, e) => EndChange(); animOutDur.Committed += (s, e) => EndChange();
            // ---- text
            textBox.Box.TextChanged += (s, e) => Edit("vs_h_text", textBox, c => { if (c.Text == null) return; c.Text.Text = textBox.Box.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"); c.Text.Touch(); }, false);
            textBox.Box.LostFocus += (s, e) => EndChange();
            List<string> favs;
            font.Items = EditorPanel.AllFonts(out favs); font.Favorites = favs;
            font.Extra = FontPack.Families; font.ExtraHeader = () => L.T("fonts_arabic");
            font.SelectedChanged += (s, e) => Edit("vs_h_font", null, c => { if (c.Text != null) { c.Text.FontName = font.Selected; c.Text.Touch(); } }, false);
            capFont.Items = font.Items; capFont.Favorites = favs; capFont.Extra = FontPack.Families; capFont.ExtraHeader = () => L.T("fonts_arabic");
            capFont.SelectedChanged += (s, e) => Edit("vs_h_caps", null, c => c.CapFont = capFont.Selected, false);
            textSize.Format = v => ((int)Math.Round(v)) + " px";
            textSize.ValueChanged += (s, e) => Edit("vs_h_text", textSize, c => { if (c.Text != null) { c.Text.SizePx = textSize.Value; c.Text.Touch(); } }, false);
            textSize.Committed += (s, e) => EndChange();
            textColor.Selected = Color.White;
            textColor.SelectedChanged += (s, e) => Edit("vs_h_text", textColor, c => { if (c.Text != null) { c.Text.Color = textColor.Selected; c.Text.Touch(); } }, false);
            textStyles = new ChipToggles(new Func<string>[] { () => "", () => "", () => L.T("ed_outline"), () => L.T("ed_shadow"), () => L.T("ed_box") }, new[] { Ico.Bold, Ico.Italic, null, null, null });
            textStyles.Changed += (s, e) => { bool[] o = textStyles.On; Edit("vs_h_text", null, c => { if (c.Text == null) return; c.Text.Bold = o[0]; c.Text.Italic = o[1]; c.Text.Outline = o[2]; c.Text.Shadow = o[3]; c.Text.Box = o[4]; c.Text.Touch(); }, false); };
            textAlign = new Segments(() => "", () => "", () => "") { FontSize = 8.5f };
            textAlign.Glyphs = new[] { Ico.AlignLeft, Ico.AlignCenter, Ico.AlignRight };
            textAlign.SelectedChanged += (s, e) => Edit("vs_h_text", null, c => { if (c.Text == null) return; c.Text.Align = textAlign.SelectedIndex == 0 ? StringAlignment.Near : textAlign.SelectedIndex == 2 ? StringAlignment.Far : StringAlignment.Center; c.Text.Touch(); }, false);
            // ---- captions
            capStyle.LabelOf = k => L.T("vs_cs_" + k);
            capStyle.SetItems(new[] { "outline", "box", "highlight", "bold" }, "outline");
            capStyle.SelectedChanged += (s, e) => Edit("vs_h_caps", null, c => c.CapStyle = capStyle.Selected, false);
            capSize.Format = v => ((int)Math.Round(v)) + "%";
            capSize.ValueChanged += (s, e) => Edit("vs_h_caps", capSize, c => c.CapSize = capSize.Value / 100f, false);
            capY.Format = v => v < 30 ? L.T("vs_pos_top") : v > 70 ? L.T("vs_pos_bottom") : L.T("vs_pos_middle");
            capY.ValueChanged += (s, e) => Edit("vs_h_caps", capY, c => c.CapY = capY.Value / 100f, false);
            capSize.Committed += (s, e) => EndChange(); capY.Committed += (s, e) => EndChange();
            capColor.Selected = Color.White; capHigh.Selected = Color.FromArgb(250, 204, 21);
            capColor.SelectedChanged += (s, e) => Edit("vs_h_caps", capColor, c => c.CapColor = capColor.Selected, false);
            capHigh.SelectedChanged += (s, e) => Edit("vs_h_caps", capHigh, c => c.CapHigh = capHigh.Selected, false);
            capWords.Box.TextChanged += (s, e) => Edit("vs_h_caps_text", capWords, c =>
            {
                if (c.Cues == null) return;
                string[] lines = capWords.Box.Text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < c.Cues.Count && i < lines.Length; i++) c.Cues[i].Text = lines[i].Replace(" / ", "\n");
            }, false);
            capWords.Box.LostFocus += (s, e) => EndChange();
            capWords.Box.WordWrap = false; capWords.Box.ScrollBars = ScrollBars.Vertical;
            bCapSrt = Small(() => L.T("vs_caps_srt"), Ico.Save, ExportSrt);
            // ---- project
            aspect.LabelOf = k => k == "orig" ? L.T("vs_ar_orig") : k;
            aspect.SetItems(new[] { "orig", "16:9", "9:16", "1:1", "4:5", "4:3" }, "orig");
            aspect.SelectedChanged += (s, e) => { if (!filling) SetAspect(aspect.Selected); };
            fps.LabelOf = k => k + " fps";
            fps.SetItems(new[] { "24", "25", "30", "60" }, "30");
            fps.SelectedChanged += (s, e) => { if (filling) return; Change("vs_h_project"); P.Fps = double.Parse(fps.Selected, CultureInfo.InvariantCulture); AfterChange(false); };
            fill.SelectedChanged += (s, e) => { if (filling) return; Change("vs_h_project"); P.Fill = fill.SelectedIndex == 0 ? "blur" : fill.SelectedIndex == 1 ? "black" : "color"; AfterChange(true); };
            fillColor.Selected = Color.Black;
            fillColor.SelectedChanged += (s, e) => { if (filling) return; Change("vs_h_project", fillColor); P.FillColor = fillColor.Selected; AfterChange(false); };
            propCtl.AddRange(new Control[] { tabs, speed, volume, kVol, fadeIn, fadeOut, length, denoise, mute, bDetach2, bBg, bReplace, aBright, aContrast, aSat, aWarm, aBlur, aSharp, aFilter, bAdjReset,
                trans, transDur, mX, mY, mScale, mRot, mOpacity, kX, kY, kScale, kRot, kOpacity, animIn, animOut, animInDur, animOutDur, textBox, font, textSize, textColor, textStyles, textAlign,
                capStyle, capFont, capSize, capY, capColor, capHigh, capWords, bCapSrt, aspect, fps, fill, fillColor });
            foreach (Control c in propCtl) { if (c is UiControl) ((UiControl)c).Surface = Surface.Card; c.Visible = false; }
            props.Controls.AddRange(propCtl.ToArray());
        }

        /// A motion setting: its slider, its keyframe switch, how it maps to the clip.
        void Bind(Slider s, KeyBtn k, string prop, Func<float, float> map, Func<VClip, float> get, Action<VClip, float> set)
        {
            s.ValueChanged += (o, e) => Edit("vs_h_motion", s, c => { float v = map(s.Value); if (c.HasKeys(prop)) c.SetKey(prop, Playhead, v); else set(c, v); k.Invalidate(); }, false);
            s.Committed += (o, e) => EndChange();
            k.State = () => KeyState(prop);
            k.Click += (o, e) => ToggleKey(prop, map(s.Value));
            k.MouseEnter += (o, e) => tips.SetToolTip(k, L.T("vs_key_tip"));
        }

        int KeyState(string prop)
        {
            VClip c = Sel;
            if (c == null || !c.HasKeys(prop)) return 0;
            return c.KeyAt(prop, Playhead) != null ? 2 : 1;
        }

        void ToggleKey(string prop, float v)
        {
            VClip c = Sel;
            if (c == null || Playhead < c.At - 0.01 || Playhead > c.End + 0.01) { ShowToast(L.T("vs_key_outside"), false, null, null); return; }
            Change("vs_h_key");
            if (c.KeyAt(prop, Playhead) != null) c.RemoveKey(prop, Playhead);
            else
            {
                // the first keyframe also fixes the value it had before
                c.SetKey(prop, Playhead, v);
            }
            AfterChange(false);
            foreach (KeyBtn kb in new[] { kX, kY, kScale, kRot, kOpacity, kVol }) kb.Invalidate();
        }

        /// The keyframe button in the timeline's tools: keyframes for every motion setting at the playhead (or none).
        void ToggleAllKeys()
        {
            VClip c = Sel;
            if (c == null) return;
            if (c.Kind == VKind.Audio) { ToggleKey("vol", c.Eval("vol", Playhead, c.Volume)); return; }
            if (Playhead < c.At - 0.01 || Playhead > c.End + 0.01) { ShowToast(L.T("vs_key_outside"), false, null, null); return; }
            Change("vs_h_key");
            string[] ps = { "x", "y", "scale", "rot", "opacity" };
            bool any = false; foreach (string p in ps) if (c.KeyAt(p, Playhead) != null) any = true;
            foreach (string p in ps)
            {
                float v = p == "x" ? c.Eval(p, Playhead, c.X) : p == "y" ? c.Eval(p, Playhead, c.Y) : p == "scale" ? c.Eval(p, Playhead, c.Scale) : p == "rot" ? c.Eval(p, Playhead, c.Rot) : c.Eval(p, Playhead, c.Opacity);
                if (any) c.RemoveKey(p, Playhead); else c.SetKey(p, Playhead, v);
            }
            if (!any) { tab = MaxTab(c); }
            AfterChange(true);
            if (!any) ShowToast(L.T("vs_key_added"), true, null, null);
        }

        /// A change from a setting: into the history first (one step per drag), then the picture catches up.
        void Edit(string hist, object key, Action<VClip> set, bool structure)
        {
            if (loadingProps) return;
            VClip c = Sel;
            if (c == null) return;
            Change(hist, key);
            set(c);
            P.Layout();
            timeline.Invalidate();
            if (structure) { LoadProps(); LayoutProps(); }
            InvalidateBar();
            Recompose();
            UpdateButtons();
        }
        bool loadingProps;

        void SetAspect(string a)
        {
            if (a == P.Aspect) return;
            Change("vs_h_project");
            P.Aspect = a;
            VClip first = P.Main.Clips.Find(x => x.Src != null && x.Kind != VKind.Audio);
            if (first != null) P.SizeFor(first.Src.W, first.Src.H); else P.SizeFor(P.W, P.H);
            bAspect.Invalidate();
            AfterChange(true);
            DoLayout();
        }

        // ------------------------------------------------------------------ filling the settings from the clip
        void LoadProps()
        {
            loadingProps = true; filling = true;
            try
            {
                VClip c = Sel;
                string[] tn = TabNames(c);
                if (tn.Length > 0)
                {
                    Func<string>[] items = new Func<string>[tn.Length];
                    for (int i = 0; i < tn.Length; i++) { string k = tn[i]; items[i] = () => L.T(k); }
                    tabs.Items = items;
                    tab = Math.Min(tab, tn.Length - 1);
                    tabs.SetSilently(tab);
                }
                aspect.Selected = P.Aspect; fps.Selected = ((int)Math.Round(P.Fps)).ToString();
                fill.SetSilently(P.Fill == "blur" ? 0 : P.Fill == "black" ? 1 : 2);
                fillColor.Selected = P.FillColor; fillColor.Invalidate();
                if (c == null) return;
                speed.SetValue((float)c.Speed, false);
                volume.SetValue(c.Eval("vol", Playhead, c.Volume) * 100, false);
                fadeIn.SetValue((float)c.FadeIn, false); fadeOut.SetValue((float)c.FadeOut, false);
                if (c.Kind == VKind.Image || c.Kind == VKind.Text) { if (c.Length > length.Max) length.Max = (float)c.Length; length.SetValue((float)c.Length, false); }
                denoise.SetSilently(c.Denoise); mute.SetSilently(c.Muted);
                Adjust a = c.Adj ?? new Adjust();
                aBright.SetValue(a.Bright, false); aContrast.SetValue(a.Contrast, false); aSat.SetValue(a.Sat, false); aWarm.SetValue(a.Warmth, false); aBlur.SetValue(a.Blur, false); aSharp.SetValue(a.Sharpen, false);
                aFilter.Selected = Adjust.FilterKeys[Math.Max(0, Math.Min(Adjust.FilterKeys.Length - 1, a.Filter))];
                trans.Selected = Array.IndexOf(TransKeys, c.Trans) >= 0 ? c.Trans : "fade";
                transDur.SetValue((float)c.TransDur, false);
                animIn.Selected = c.AnimIn; animOut.Selected = c.AnimOut;
                animInDur.SetValue((float)c.AnimInDur, false); animOutDur.SetValue((float)c.AnimOutDur, false);
                SyncPlacement();
                if (c.Text != null)
                {
                    string tt = (c.Text.Text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                    if (textBox.Box.Text != tt) textBox.Box.Text = tt;
                    font.Selected = c.Text.FontName; font.Invalidate();
                    if (c.Text.SizePx > textSize.Max) textSize.Max = c.Text.SizePx;
                    textSize.SetValue(c.Text.SizePx, false);
                    textColor.Selected = c.Text.Color; textColor.Invalidate();
                    textStyles.On = new[] { c.Text.Bold, c.Text.Italic, c.Text.Outline, c.Text.Shadow, c.Text.Box }; textStyles.Invalidate();
                    textAlign.SetSilently(c.Text.Align == StringAlignment.Near ? 0 : c.Text.Align == StringAlignment.Far ? 2 : 1);
                }
                if (c.Cues != null)
                {
                    capStyle.Selected = c.CapStyle;
                    capFont.Selected = string.IsNullOrEmpty(c.CapFont) ? "Segoe UI" : c.CapFont; capFont.Invalidate();
                    capSize.SetValue(c.CapSize * 100, false); capY.SetValue(c.CapY * 100, false);
                    capColor.Selected = c.CapColor; capHigh.Selected = c.CapHigh; capColor.Invalidate(); capHigh.Invalidate();
                    List<string> lines = new List<string>(); foreach (Cue q in c.Cues) lines.Add(q.Text.Replace("\r\n", " / ").Replace("\n", " / "));
                    string all = string.Join("\r\n", lines.ToArray());
                    if (capWords.Box.Text != all) capWords.Box.Text = all;
                }
            }
            finally { loadingProps = false; filling = false; }
        }

        /// The motion sliders show the values at the playhead.
        void SyncPlacement()
        {
            VClip c = Sel;
            if (c == null) return;
            bool was = loadingProps; loadingProps = true;
            try
            {
                mX.SetValue(c.Eval("x", Playhead, c.X) * 100, false); mY.SetValue(c.Eval("y", Playhead, c.Y) * 100, false);
                mScale.SetValue(c.Eval("scale", Playhead, c.Scale) * 100, false); mRot.SetValue(c.Eval("rot", Playhead, c.Rot), false);
                mOpacity.SetValue(c.Eval("opacity", Playhead, c.Opacity) * 100, false);
                if (c.HasKeys("vol")) volume.SetValue(c.Eval("vol", Playhead, c.Volume) * 100, false);
                foreach (KeyBtn kb in new[] { kX, kY, kScale, kRot, kOpacity, kVol }) kb.Invalidate();
            }
            finally { loadingProps = was; }
        }

        // ------------------------------------------------------------------ laying out the settings
        void Head(string key) { heads.Add(new KeyValuePair<Func<string>, Rectangle>(() => L.T(key), new Rectangle(0, py - props.Offset, pw, Theme.S(20)))); py += Theme.S(24); }
        void Put(Control c, int h) { c.Bounds = Theme.Mirror(new Rectangle(0, py - props.Offset, pw, h), props.Width); placed.Add(c); py += h + Theme.S(10); }
        void PutRow(int h, params Control[] cs)
        {
            int gap = Theme.S(6), w = (pw - gap * (cs.Length - 1)) / cs.Length;
            for (int i = 0; i < cs.Length; i++) { cs[i].Bounds = Theme.Mirror(new Rectangle(i * (w + gap), py - props.Offset, i == cs.Length - 1 ? pw - i * (w + gap) : w, h), props.Width); placed.Add(cs[i]); }
            py += h + Theme.S(10);
        }
        /// A slider with a keyframe switch at its end.
        void PutKeyed(string head, Slider s, KeyBtn k)
        {
            Head(head);
            int kw = Theme.S(30), h = Theme.S(30);
            s.Bounds = Theme.Mirror(new Rectangle(0, py - props.Offset, pw - kw - Theme.S(4), h), props.Width);
            k.Bounds = Theme.Mirror(new Rectangle(pw - kw, py - props.Offset, kw, h), props.Width);
            placed.Add(s); placed.Add(k);
            py += h + Theme.S(8);
        }
        void PutToggle(Toggle t, string key)
        {
            int h = Theme.S(28), tw = Theme.S(42);
            t.Bounds = Theme.Mirror(new Rectangle(pw - tw, py - props.Offset + (h - Theme.S(24)) / 2, tw, Theme.S(24)), props.Width);
            placed.Add(t);
            hints.Add(new KeyValuePair<string, Rectangle>("\u0001" + L.T(key), new Rectangle(0, py - props.Offset, pw - tw - Theme.S(8), h)));
            py += h + Theme.S(8);
        }
        void Hint(string text)
        {
            Font f = Theme.F(8.5f);
            int h = TextRenderer.MeasureText(text, f, new Size(pw, 999), TextFormatFlags.WordBreak).Height + Theme.S(2);
            hints.Add(new KeyValuePair<string, Rectangle>(text, new Rectangle(0, py - props.Offset, pw, h)));
            py += h + Theme.S(10);
        }

        void LayoutProps()
        {
            if (props.Width <= 0 || propCtl.Count == 0) return;
            placed.Clear(); hints.Clear(); heads.Clear();
            py = Theme.S(2); pw = props.Width - Theme.S(10);
            int sh = Theme.S(28), bh = Theme.S(32);
            VClip c = Sel;
            if (c == null)
            {
                Head("vs_aspect"); Put(aspect, aspect.NeededHeight(6));
                Head("vs_fill"); Put(fill, Theme.S(32));
                if (P.Fill == "color") Put(fillColor, Theme.S(34));
                Head("vs_fps"); Put(fps, fps.NeededHeight(4));
                Hint(L.T("vs_project_hint"));
            }
            else
            {
                string[] tn = TabNames(c);
                if (tn.Length > 1) Put(tabs, Theme.S(32));
                string t = tn.Length > 0 ? tn[Math.Min(tab, tn.Length - 1)] : "";
                switch (t)
                {
                    case "vs_tab_basic":
                        if (c.Kind == VKind.Image) { Head("vs_length"); Put(length, sh); }
                        else { Head("vs_speed"); Put(speed, sh); }
                        if (c.HasSound || c.Kind == VKind.Audio)
                        {
                            PutKeyed("vs_volume", volume, kVol);
                            Head("vs_fade_in"); Put(fadeIn, sh); Head("vs_fade_out"); Put(fadeOut, sh);
                            PutToggle(denoise, "vs_denoise");
                            PutToggle(mute, "vs_mute");
                        }
                        else if (c.Kind == VKind.Image || c.Kind == VKind.Video) { Head("vs_fade_in"); Put(fadeIn, sh); Head("vs_fade_out"); Put(fadeOut, sh); }
                        if (c.Kind == VKind.Video && c.HasSound) Put(bDetach2, bh);
                        if (c.Kind == VKind.Video) { Put(bBg, bh); Hint(L.T("vs_bg_hint")); }
                        if (c.Kind != VKind.Audio && SelTrack == VTrackKind.Main && c.Kind == VKind.Video && Math.Abs(c.Speed - 1) > 0.001) Hint(L.T("vs_speed_hint"));
                        break;
                    case "vs_tab_adjust":
                        Head("adj_bright"); Put(aBright, sh); Head("adj_contrast"); Put(aContrast, sh); Head("adj_sat"); Put(aSat, sh);
                        Head("adj_warm"); Put(aWarm, sh); Head("adj_blur"); Put(aBlur, sh); Head("adj_sharp"); Put(aSharp, sh);
                        Head("ps_filter"); Put(aFilter, aFilter.NeededHeight(Adjust.FilterKeys.Length));
                        Put(bAdjReset, bh);
                        break;
                    case "vs_tab_trans":
                        int i = P.Main.Clips.IndexOf(c);
                        if (i < 0 || i >= P.Main.Clips.Count - 1) { Hint(L.T("vs_trans_last")); break; }
                        Head("vs_trans_to_next"); Put(trans, trans.NeededHeight(TransKeys.Length));
                        if (c.Trans != "none") { Head("vs_trans_len"); Put(transDur, sh); }
                        break;
                    case "vs_tab_motion":
                        PutKeyed("vs_m_x", mX, kX); PutKeyed("vs_m_y", mY, kY); PutKeyed("vs_m_scale", mScale, kScale); PutKeyed("vs_m_rot", mRot, kRot); PutKeyed("vs_m_opacity", mOpacity, kOpacity);
                        Hint(L.T("vs_motion_hint"));
                        Head("vs_anim_in"); Put(animIn, animIn.NeededHeight(6)); if (c.AnimIn != "none") Put(animInDur, sh);
                        Head("vs_anim_out"); Put(animOut, animOut.NeededHeight(5)); if (c.AnimOut != "none") Put(animOutDur, sh);
                        break;
                    case "vs_tab_text":
                        Put(textBox, Theme.S(70));
                        Head("ps_font"); Put(font, Theme.S(34));
                        Head("ps_size"); Put(textSize, sh);
                        Head("ps_color"); Put(textColor, Theme.S(34));
                        Head("ps_style"); Put(textStyles, Theme.S(32));
                        Put(textAlign, Theme.S(32));
                        Head("vs_length"); Put(length, sh);
                        break;
                    case "vs_tab_style":
                        Head("vs_cap_style"); Put(capStyle, capStyle.NeededHeight(4));
                        Head("ps_font"); Put(capFont, Theme.S(34));
                        Head("ps_size"); Put(capSize, sh);
                        Head("vs_cap_place"); Put(capY, sh);
                        Head("ps_color"); Put(capColor, Theme.S(34));
                        if (c.CapStyle == "highlight" || c.CapStyle == "bold") { Head("vs_cap_high"); Put(capHigh, Theme.S(34)); }
                        break;
                    case "vs_tab_words":
                        Hint(L.T("vs_caps_words_hint"));
                        Put(capWords, Math.Max(Theme.S(160), props.Height - py - Theme.S(60)));
                        Put(bCapSrt, bh);
                        break;
                }
            }
            props.ContentHeight = py;
            props.ClampTo(props.MaxOffset);
            foreach (Control ctl in propCtl) { bool v = placed.Contains(ctl); if (ctl.Visible != v) ctl.Visible = v; }
            props.Labels.Clear(); props.Labels.AddRange(heads);
            props.Invalidate();
            InvalidateHead();
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
    }
}
