using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace FileConverter
{
    /// A live Donut Wheel to try out: point at slices like the real one, and drag a slice onto another to move it there.
    public class WheelPreview : UiControl
    {
        WheelArt art;
        Bitmap frame;
        float pop = 1;              // 0 → 1 after a change: the wheel springs back to size
        int downO = -1, downI = -1; // the slice the mouse went down on
        bool dragging;
        Point downAt;
        List<WheelItem> outer = new List<WheelItem>(), inner = new List<WheelItem>();
        string subject = "";
        /// The wheel's real size on the screen (Settings → Size): the preview eases to it, never bigger than its box.
        public int TargetPx = WheelConfig.WheelPx;
        float shownPx = -1;
        float Fit { get { return Math.Min(1f, (shownPx < 0 ? TargetPx : shownPx) / Side); } }

        /// A slice was dragged to a new place: (outer ring?, from, to).
        public event Action<bool, int, int> Reordered;

        public WheelPreview() { Surface = Surface.Card; Cursor = Cursors.Hand; }

        public void Show(List<WheelItem> o, List<WheelItem> i, string sampleName, bool animate)
        {
            outer = o; inner = i; subject = sampleName;
            Build();
            if (animate && Fx.Motion) { pop = 0; Animator.Start(this); }
            Invalidate();
        }

        int Side { get { return Math.Max(Theme.S(200), Math.Min(Width, Height)); } }

        void Build()
        {
            if (art == null || art.D != Side) art = new WheelArt(Side);
            art.SetItems(outer, inner);
            art.Subject = subject; art.Hint = L.T("wh_hint");
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Width > 0 && Height > 0) { art = null; Build(); Invalidate(); } }

        /// A point of the control in the wheel's own box (the wheel is drawn smaller than the box when it's set smaller).
        PointF ToArt(Point p)
        {
            float k = Math.Max(0.05f, Fit);
            return new PointF(Side / 2f + (p.X - Width / 2f) / k, Side / 2f + (p.Y - Height / 2f) / k);
        }
        public void SetSize(int px) { TargetPx = px; Animator.Start(this); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (art == null) return;
            int o, i; art.HitTest(ToArt(e.Location), out o, out i);
            if (downO >= 0 || downI >= 0)
            {
                if (!dragging && (Math.Abs(e.X - downAt.X) > 4 || Math.Abs(e.Y - downAt.Y) > 4)) dragging = true;
                // while moving a slice, only its own ring lights up
                if (downO >= 0) { i = -1; } else { o = -1; }
            }
            if (o != art.HotOuter || i != art.HotInner) { art.HotOuter = o; art.HotInner = i; Animator.Start(this); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (art == null || e.Button != MouseButtons.Left) return;
            art.HitTest(ToArt(e.Location), out downO, out downI);
            downAt = e.Location; dragging = false;
            art.LiftOuter = downO; art.LiftInner = downI;
            Animator.Start(this);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (art == null) return;
            int o, i; art.HitTest(ToArt(e.Location), out o, out i);
            int fo = downO, fi = downI;
            downO = downI = -1; art.LiftOuter = art.LiftInner = -1;
            if (dragging && Reordered != null)
            {
                if (fo >= 0 && o >= 0 && o != fo) Reordered(true, fo, o);
                else if (fi >= 0 && i >= 0 && i != fi) Reordered(false, fi, i);
            }
            dragging = false;
            Animator.Start(this);
        }

        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); WheelPage wp = Parent as WheelPage; if (wp != null) wp.ScrollBy(e.Delta); }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (art != null && downO < 0 && downI < 0) { art.HotOuter = art.HotInner = -1; Animator.Start(this); }
        }

        public override bool Step()
        {
            bool b = base.Step();
            bool a = art != null && art.Step();
            bool p = Fx.Ease(ref pop, 1, 0.12f);
            bool z = false;
            if (shownPx < 0 || !Fx.Motion) shownPx = TargetPx;
            else if (Math.Abs(shownPx - TargetPx) > 0.3f) { shownPx += (TargetPx - shownPx) * Animator.Rate(0.22f); z = true; }
            else shownPx = TargetPx;
            Invalidate();
            return a || b || p || z;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            if (art == null) Build();
            int s = Side;
            if (frame == null || frame.Width != s) { if (frame != null) frame.Dispose(); frame = new Bitmap(s, s, PixelFormat.Format32bppPArgb); }
            float scale = (Fx.Motion ? 0.9f + 0.1f * WheelArt.EaseOutBack(Math.Min(1, pop)) : 1) * Fit;
            using (Graphics fg = Graphics.FromImage(frame)) art.Draw(fg, scale, 1, true, true);
            g.DrawImage(frame, (Width - s) / 2, (Height - s) / 2, s, s);
        }

        protected override void Dispose(bool disposing) { if (frame != null) frame.Dispose(); base.Dispose(disposing); }
    }

    /// Settings → Donut Wheel: for each kind of file, which formats go on the outer ring and which actions in the middle,
    /// in what order. Changes are saved right away and show on the live wheel beside them.
    public class WheelPage : Page
    {
        readonly Btn back, reset;
        readonly Segments kinds;
        readonly Toggle onOff;
        readonly WheelPreview preview = new WheelPreview();
        public event Action BackRequested;

        Rectangle previewCard, pickCard, onRow, fmtHead, actHead, hintR, captionR, sizeLbl;
        readonly Slider size;
        // recipes for this kind of file: listed under the choices, each opens its editor
        readonly Btn bNewRecipe;
        List<Recipe> recList = new List<Recipe>();
        Rectangle recHead, recEmpty;
        readonly List<Rectangle> recR = new List<Rectangle>();
        int hotRecipe = -1;
        bool RecipesHere { get { return Array.IndexOf(Recipes.Kinds, Kind) >= 0; } }
        readonly List<Rectangle> fmtR = new List<Rectangle>(), actR = new List<Rectangle>();
        string[] pool = new string[0], actPool = new string[0];
        List<string> formats = new List<string>(), actions = new List<string>();
        readonly Dictionary<string, float> lit = new Dictionary<string, float>();   // how "on" each chip looks (eased)
        int hotChip = -1;   // 0.. = formats, 100.. = actions
        int maxScroll;

        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ScrollBy(e.Delta); }
        public void ScrollBy(int delta)
        {
            if (maxScroll <= 0) return;
            int n = Math.Max(0, Math.Min(maxScroll, ScrollY - delta / 120 * Theme.S(70)));
            if (n != ScrollY) { ScrollY = n; DoLayout(); Update(); }
        }

        string Kind { get { return WheelConfig.Kinds[Math.Max(0, kinds.SelectedIndex)]; } }

        public WheelPage() : base("whp_title", "whp_sub")
        {
            back = new Btn(() => L.T("studio_back"), Ico.Back, BtnKind.Secondary);
            back.Click += (s, e) => { if (BackRequested != null) BackRequested(); };
            reset = new Btn(() => L.T("whp_reset"), Ico.Refresh, BtnKind.Ghost) { FontSize = 9f };
            reset.Click += (s, e) => { WheelConfig.Reset(Kind); Load(true); };
            Func<string>[] names = new Func<string>[WheelConfig.Kinds.Length];
            for (int i = 0; i < names.Length; i++) { string k = WheelConfig.KindKeys[i]; names[i] = () => L.T(k); }
            kinds = new Segments(names) { Glyphs = WheelConfig.KindGlyphs };
            kinds.SelectedChanged += (s, e) => Load(true);
            onOff = new Toggle();
            onOff.CheckedChanged += (s, e) =>
            {
                DragWheel.Switch(onOff.Checked, FindForm());
                if (onOff.Checked && Settings.Get("CloseToTray", "0") != "1")
                    ShowToast(L.T("wheel_needs_tray"), true, () => L.T("wheel_keep_running"), () => { Settings.Set("CloseToTray", "1"); DragWheel.NotifyChanged(); });
            };
            DragWheel.Changed += () => { if (IsHandleCreated) onOff.SetSilently(DragWheel.On); };
            preview.Reordered += MoveSlice;
            // how big the wheel opens on the desktop: the preview grows and shrinks with it, at its real size
            size = new Slider { Min = WheelConfig.MinSize, Max = WheelConfig.MaxSize, Value = WheelConfig.SizePct, Format = v => ((int)Math.Round(v)) + "%" };
            size.ValueChanged += (s, e) => preview.SetSize((int)Math.Round(360f * Theme.Scale * size.Value / 100f));
            size.Committed += (s, e) => WheelConfig.SizePct = (int)Math.Round(size.Value);
            bNewRecipe = new Btn(() => L.T("rc_new_short"), Ico.Add, BtnKind.Secondary) { FontSize = 9f };
            bNewRecipe.Click += (s, e) => EditRecipe(null);
            Controls.AddRange(new Control[] { back, reset, kinds, onOff, preview, size, bNewRecipe });
            Cursor = Cursors.Default;
        }

        public override void OnShown()
        {
            onOff.SetSilently(DragWheel.On);
            size.SetValue(WheelConfig.SizePct, false);
            preview.SetSize(WheelConfig.WheelPx);
            Load(false);
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape && BackRequested != null) { BackRequested(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        void Load(bool animate)
        {
            pool = WheelConfig.Pool(Kind);
            actPool = WheelConfig.ActionPool(Kind);
            formats = WheelConfig.FormatsFor(Kind);
            actions = WheelConfig.ActionsFor(Kind);
            recList = Recipes.All().FindAll(x => x.For == Kind);
            foreach (string f in pool) lit["f:" + f] = formats.Contains(f) ? 1 : 0;
            foreach (string a in actPool) lit["a:" + a] = actions.Contains(a) ? 1 : 0;
            Refresh2(animate);
        }

        void Refresh2(bool animate)
        {
            List<WheelItem> o = new List<WheelItem>(), i = new List<WheelItem>();
            foreach (string f in formats) o.Add(WheelConfig.Format(f));
            foreach (string a in actions) i.Add(WheelConfig.ActionItem(a));
            preview.Show(o, i, WheelConfig.Samples[Math.Max(0, kinds.SelectedIndex)], animate);
            reset.Visible = !WheelConfig.IsDefault(Kind);
            DoLayout();
        }

        void Save() { WheelConfig.Save(Kind, formats, actions); Refresh2(true); Animator.Start(this); }

        /// A new recipe (null) or one to change; a new one goes on the wheel straight away when there's room.
        void EditRecipe(Recipe existing)
        {
            Recipe done = RecipeDialog.Edit(FindForm(), existing, Kind);
            if (done == null) return;
            if (existing == null && Array.IndexOf(Recipes.Kinds, done.For) >= 0)
            {
                List<string> acts = WheelConfig.ActionsFor(done.For);
                if (acts.Count < WheelConfig.MaxActions && !acts.Contains(done.Id)) { acts.Add(done.Id); WheelConfig.Save(done.For, WheelConfig.FormatsFor(done.For), acts); }
                int ki = Array.IndexOf(WheelConfig.Kinds, done.For);
                if (ki >= 0 && ki != kinds.SelectedIndex) { kinds.SelectedIndex = ki; return; }
            }
            Load(true);
        }

        void MoveSlice(bool outerRing, int from, int to)
        {
            List<string> l = outerRing ? formats : actions;
            if (from < 0 || from >= l.Count || to < 0 || to >= l.Count) return;
            string x = l[from]; l.RemoveAt(from); l.Insert(to, x);
            Save();
        }

        void Toggle(int chip)
        {
            if (chip >= 100)
            {
                string a = actPool[chip - 100];
                if (actions.Contains(a)) actions.Remove(a);
                else if (actions.Count < WheelConfig.MaxActions) actions.Add(a);
                else { ShowToast(L.T("whp_full_actions", WheelConfig.MaxActions), false, null, null); return; }
            }
            else
            {
                string f = pool[chip];
                if (formats.Contains(f)) formats.Remove(f);
                else if (formats.Count < WheelConfig.MaxFormats) formats.Add(f);
                else { ShowToast(L.T("whp_full", WheelConfig.MaxFormats), false, null, null); return; }
            }
            Save();
        }

        public override bool Step()
        {
            bool more = base.Step();
            foreach (string f in pool) { float v = lit.ContainsKey("f:" + f) ? lit["f:" + f] : 0; if (Fx.Ease(ref v, formats.Contains(f) ? 1 : 0, 0.3f)) more = true; lit["f:" + f] = v; }
            foreach (string a in actPool) { float v = lit.ContainsKey("a:" + a) ? lit["a:" + a] : 0; if (Fx.Ease(ref v, actions.Contains(a) ? 1 : 0, 0.3f)) more = true; lit["a:" + a] = v; }
            Invalidate();
            return more;
        }

        // ------------------------------------------------------------------ layout

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, ip = Theme.S(20), gap = Theme.S(18);
            cards.Clear();
            int bw = Math.Max(Theme.S(110), back.PreferredWidth);
            HeaderReserve = bw + Theme.S(12);
            Place(back, new Rectangle(W - p - bw, Theme.S(24) - ScrollY, bw, Theme.S(38)));
            Place(kinds, new Rectangle(p, Top0, Math.Min(W - p * 2, Theme.S(820)), Theme.S(42)));
            int y = Top0 + Theme.S(42) + gap;
            int avail = H - (y + ScrollY) - p;   // the room under the tabs when the page isn't scrolled
            bool side = W - p * 2 >= Theme.S(860);
            int pw = side ? Math.Min(Theme.S(440), (W - p * 2 - gap) * 9 / 20) : W - p * 2;
            int ph = side ? Math.Max(Theme.S(360), avail) : Math.Min(Theme.S(460), Math.Max(Theme.S(380), avail / 2));
            previewCard = new Rectangle(p, y, pw, ph);
            pickCard = side ? new Rectangle(p + pw + gap, y, W - p * 2 - pw - gap, ph)
                            : new Rectangle(p, previewCard.Bottom + gap, W - p * 2, Math.Max(Theme.S(200), avail - ph - gap));
            cards.Add(previewCard); cards.Add(pickCard);

            // the live wheel, and under it what it shows and a way back to the usual set
            int rw = Math.Max(Theme.S(150), reset.PreferredWidth);
            captionR = new Rectangle(previewCard.X + ip, previewCard.Bottom - Theme.S(50), previewCard.Width - ip * 2 - rw - Theme.S(8), Theme.S(36));
            Place(reset, new Rectangle(previewCard.Right - ip - rw, previewCard.Bottom - Theme.S(48), rw, Theme.S(34)));
            sizeLbl = new Rectangle(previewCard.X + ip, previewCard.Bottom - Theme.S(96), Theme.S(70), Theme.S(36));
            Place(size, new Rectangle(sizeLbl.Right + Theme.S(4), sizeLbl.Y, previewCard.Width - ip * 2 - sizeLbl.Width - Theme.S(4), Theme.S(36)));
            int room = previewCard.Height - Theme.S(112);
            int sz = Math.Min(previewCard.Width - ip * 2, room);
            Place(preview, new Rectangle(previewCard.X + (previewCard.Width - sz) / 2, previewCard.Y + Theme.S(8) + (room - sz) / 2, sz, sz));

            // the choices
            int x = pickCard.X + ip, w = pickCard.Width - ip * 2, cy = pickCard.Y + Theme.S(16);
            onRow = new Rectangle(x, cy, w - Theme.S(64), Theme.S(44));
            Place(onOff, new Rectangle(x + w - Theme.S(52), cy + Theme.S(5), Theme.S(52), Theme.S(34)));
            cy += Theme.S(60);
            fmtHead = new Rectangle(x, cy, w, Theme.S(22)); cy += Theme.S(30);
            fmtR.Clear();
            int chipW = Theme.S(78), chipH = Theme.S(40), cg = Theme.S(8);
            int cols = Math.Max(1, (w + cg) / (chipW + cg));
            chipW = (w - (cols - 1) * cg) / cols;
            for (int i = 0; i < pool.Length; i++) fmtR.Add(new Rectangle(x + (i % cols) * (chipW + cg), cy + (i / cols) * (chipH + cg), chipW, chipH));
            cy += ((pool.Length + cols - 1) / cols) * (chipH + cg) + Theme.S(14);
            actHead = new Rectangle(x, cy, w, Theme.S(22)); cy += Theme.S(30);
            actR.Clear();
            int aw = Theme.S(170), acols = Math.Max(1, (w + cg) / (aw + cg));
            aw = (w - (acols - 1) * cg) / acols;
            for (int i = 0; i < actPool.Length; i++) actR.Add(new Rectangle(x + (i % acols) * (aw + cg), cy + (i / acols) * (chipH + cg), aw, chipH));
            cy += ((actPool.Length + acols - 1) / acols) * (chipH + cg) + Theme.S(8);
            hintR = new Rectangle(x, cy, w, Theme.S(40));
            cy += Theme.S(48);
            recR.Clear();
            bNewRecipe.Visible = RecipesHere;
            if (RecipesHere)
            {
                recHead = new Rectangle(x, cy, w, Theme.S(34));
                int nw = Math.Max(Theme.S(140), bNewRecipe.PreferredWidth);
                Place(bNewRecipe, new Rectangle(x + w - nw, cy, nw, Theme.S(34)));
                cy += Theme.S(42);
                if (recList.Count == 0) { recEmpty = new Rectangle(x, cy, w, Theme.S(40)); cy += Theme.S(44); }
                foreach (Recipe rc in recList) { recR.Add(new Rectangle(x, cy, w, Theme.S(52))); cy += Theme.S(58); }
                hintR.Height = Theme.S(40);
            }
            Rectangle lastR = recR.Count > 0 ? recR[recR.Count - 1] : RecipesHere ? (recList.Count == 0 ? recEmpty : recHead) : hintR;
            // a short window: the page scrolls (the narrow layout stacks the wheel over the choices)
            int need = lastR.Bottom + Theme.S(16) - pickCard.Y;
            if (pickCard.Height < need) { pickCard.Height = need; cards[1] = pickCard; }
            int bottom = Math.Max(previewCard.Bottom, pickCard.Bottom) + p + ScrollY;
            maxScroll = Math.Max(0, bottom - H);
            if (ScrollY > maxScroll) { ScrollY = maxScroll; DoLayout(); return; }
            Invalidate();
        }

        // ------------------------------------------------------------------ chips (drawn here, clicked here)

        int ChipAt(Point screenLtr)
        {
            for (int i = 0; i < fmtR.Count; i++) if (fmtR[i].Contains(screenLtr)) return i;
            for (int i = 0; i < actR.Count; i++) if (actR[i].Contains(screenLtr)) return 100 + i;
            return -1;
        }
        Point Ltr(Point p) { return Theme.RTL ? new Point(Width - p.X, p.Y) : p; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int c = ChipAt(Ltr(e.Location));
            int hr = -1; Point pt = Ltr(e.Location);
            for (int i = 0; i < recR.Count; i++) if (recR[i].Contains(pt)) hr = i;
            if (c != hotChip || hr != hotRecipe) { hotChip = c; hotRecipe = hr; Cursor = c >= 0 || hr >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hotChip != -1) { hotChip = -1; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int c = ChipAt(Ltr(e.Location));
            if (c >= 0) Toggle(c);
            else
            {
                Point pt = Ltr(e.Location);
                for (int i = 0; i < recR.Count; i++) if (recR[i].Contains(pt)) { EditRecipe(recList[i]); return; }
            }
        }

        protected override void PaintContent(Graphics g)
        {
            // under the wheel
            Label(g, L.T("whp_size"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, sizeLbl, Align.Start);
            Label(g, L.T("whp_caption"), Theme.F(8.75f), Theme.InkSoft, reset.Visible ? captionR : new Rectangle(captionR.X, captionR.Y, previewCard.Width - Theme.S(40), captionR.Height), Align.Start, true, true);

            // on / off
            Label(g, L.T("whp_on"), Theme.F(10.5f, FontStyle.Bold), Theme.Ink, new Rectangle(onRow.X, onRow.Y, onRow.Width, Theme.S(22)), Align.Start);
            Label(g, L.T("whp_on_d"), Theme.F(8.75f), Theme.InkSoft, new Rectangle(onRow.X, onRow.Y + Theme.S(22), onRow.Width, Theme.S(20)), Align.Start);
            using (Pen lp = new Pen(Theme.Line)) { Rectangle l = M(new Rectangle(onRow.X, onRow.Bottom + Theme.S(7), pickCard.Width - Theme.S(40), 1)); g.DrawLine(lp, l.X, l.Y, l.Right, l.Y); }

            Head(g, fmtHead, L.T("whp_formats"), formats.Count + " / " + WheelConfig.MaxFormats);
            for (int i = 0; i < fmtR.Count; i++)
            {
                string f = pool[i];
                int order = formats.IndexOf(f);
                Chip(g, fmtR[i], f.ToUpperInvariant(), null, lit.ContainsKey("f:" + f) ? lit["f:" + f] : 0, order, hotChip == i,
                     order < 0 && formats.Count >= WheelConfig.MaxFormats);
            }
            Head(g, actHead, L.T("whp_actions"), actions.Count + " / " + WheelConfig.MaxActions);
            for (int i = 0; i < actR.Count; i++)
            {
                WheelItem it = WheelConfig.ActionItem(actPool[i]);
                int order = actions.IndexOf(actPool[i]);
                Chip(g, actR[i], it.Label(), it.Glyph, lit.ContainsKey("a:" + actPool[i]) ? lit["a:" + actPool[i]] : 0, order, hotChip == 100 + i, false);
            }
            Label(g, L.T("whp_hint"), Theme.F(8.5f), Theme.InkFaint, hintR, Align.Start, false, true);
            if (RecipesHere)
            {
                using (Pen lp = new Pen(Theme.Line)) { Rectangle l = M(new Rectangle(recHead.X, recHead.Y - Theme.S(8), recHead.Width, 1)); g.DrawLine(lp, l.X, l.Y, l.Right, l.Y); }
                Label(g, L.T("rc_title").ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.InkSoft, new Rectangle(recHead.X, recHead.Y, recHead.Width - Theme.S(160), recHead.Height), Align.Start);
                if (recList.Count == 0) Label(g, L.T("rc_none"), Theme.F(8.75f), Theme.InkFaint, recEmpty, Align.Start, true, true);
                for (int i = 0; i < recR.Count && i < recList.Count; i++)
                {
                    Rectangle r = recR[i];
                    Theme.FillRound(g, M(r), Theme.S(12), hotRecipe == i ? Theme.Hover : Theme.CardInner);
                    Rectangle ic = new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - Theme.S(32)) / 2, Theme.S(32), Theme.S(32));
                    Theme.FillRound(g, M(ic), Theme.S(9), Theme.AccentSoft);
                    Theme.Glyph(g, Ico.Recipe, 9.5f, Theme.AccentColor, M(ic));
                    bool on = actions.Contains(recList[i].Id);
                    Label(g, recList[i].Name, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, new Rectangle(ic.Right + Theme.S(10), r.Y + Theme.S(7), r.Width - Theme.S(220), Theme.S(20)), Align.Start);
                    Label(g, Recipes.Summary(recList[i]), Theme.F(8.25f), Theme.InkSoft, new Rectangle(ic.Right + Theme.S(10), r.Y + Theme.S(27), r.Width - Theme.S(100), Theme.S(18)), Align.Start);
                    Label(g, L.T(on ? "rc_on_wheel" : "rc_off_wheel"), Theme.F(8f, FontStyle.Bold), on ? Theme.AccentColor : Theme.InkFaint, new Rectangle(r.Right - Theme.S(150), r.Y + Theme.S(7), Theme.S(118), Theme.S(20)), Align.End);
                    Theme.Glyph(g, Ico.Edit, 8.5f, Theme.InkSoft, M(new Rectangle(r.Right - Theme.S(30), r.Y, Theme.S(22), r.Height)));
                }
            }
        }

        void Head(Graphics g, Rectangle r, string text, string count)
        {
            Label(g, text.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.InkSoft, r, Align.Start);
            Label(g, count, Theme.F(8.25f, FontStyle.Bold), Theme.InkFaint, r, Align.End);
        }

        /// A chip: on = filled with the accent and numbered by its place on the wheel; off = a quiet outline.
        void Chip(Graphics g, Rectangle ltr, string label, string glyph, float on, int order, bool hover, bool full)
        {
            Rectangle r = M(ltr);
            Color off = hover && !full ? Theme.Hover : Theme.CardInner;
            Color fill = Theme.Mix(off, Theme.AccentFill, on);
            Theme.FillRound(g, r, Theme.S(10), fill);
            if (on < 0.5f) Theme.StrokeRound(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(10), hover && !full ? Theme.Mix(Theme.ChipBorder, Theme.AccentColor, 0.5f) : Theme.ChipBorder);
            Color ink = Theme.Mix(full ? Theme.InkFaint : Theme.Ink, Theme.PrimaryInk, on);
            int lead = 0;
            if (order >= 0 && on > 0.5f)
            {
                // its place on the wheel (1 = at the top, then clockwise)
                Rectangle nb = new Rectangle(ltr.X + Theme.S(7), ltr.Y + (ltr.Height - Theme.S(18)) / 2, Theme.S(18), Theme.S(18));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillEllipse(b, M(nb));
                Theme.Text(g, (order + 1).ToString(), Theme.F(7.5f, FontStyle.Bold), ink, M(nb), Align.Center, true, false, true);
                lead = Theme.S(22);
            }
            if (glyph != null)
            {
                Rectangle gr = new Rectangle(ltr.X + lead + Theme.S(10), ltr.Y, Theme.S(22), ltr.Height);
                Theme.Glyph(g, glyph, 10f, ink, M(gr));
                Theme.Text(g, label, Theme.F(9f, FontStyle.Bold), ink, M(new Rectangle(gr.Right + Theme.S(6), ltr.Y, ltr.Right - gr.Right - Theme.S(12), ltr.Height)), Align.Start);
            }
            else
                Theme.Text(g, label, Theme.F(9f, FontStyle.Bold), ink, M(new Rectangle(ltr.X + lead, ltr.Y, ltr.Width - lead, ltr.Height)), Align.Center, true, false, true);
        }
    }

    /// Settings: the card that opens the Donut Wheel page. A little wheel turns when you point at it.
    public class WheelEntry : UiControl
    {
        public WheelEntry() { Surface = Surface.Background; Cursor = Cursors.Hand; TabStop = true; }
        protected override AccessibleRole AccRole { get { return AccessibleRole.PushButton; } }
        protected virtual string TitleKey { get { return "whp_open"; } }
        protected virtual string SubKey { get { return "whp_open_d"; } }
        protected virtual bool IsOn { get { return DragWheel.On; } }
        protected virtual string StateText { get { return L.T(IsOn ? "whp_state_on" : "whp_state_off"); } }
        protected virtual void DrawArt(Graphics g, Rectangle r, float hot) { DrawMini(g, r, hot * 45f); }
        protected override string AccName { get { return L.T(TitleKey); } }
        protected override string AccAction { get { return L.T(TitleKey); } }
        protected override void AccDo() { OnClick(EventArgs.Empty); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Rectangle card = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.PaintCard(g, card);
            if (hot > 0.01f) Theme.FillRound(g, new RectangleF(1, 1, Width - 3, Height - 3), Theme.S(13), Color.FromArgb((int)(hot * 255), Theme.Mix(Theme.Card, Theme.AccentColor, 0.05f)));
            bool rtl = Theme.RTL;
            Func<Rectangle, Rectangle> M = r => Theme.Mirror(r, Width);
            int ip = Theme.S(20), d = Height - Theme.S(28);
            Rectangle wr = M(new Rectangle(ip, (Height - d) / 2, d, d));
            DrawArt(g, wr, hot);
            int tx = ip + d + Theme.S(16), ar = Theme.S(34), pillW = Theme.S(56);
            int tw = Width - tx - ip - ar - pillW - Theme.S(12);
            Theme.Text(g, L.T(TitleKey), Theme.F(11f, FontStyle.Bold), Theme.Ink, M(new Rectangle(tx, Theme.S(18), tw, Theme.S(26))), Align.Start, true, false, true);
            Theme.Text(g, L.T(SubKey), Theme.F(8.75f), Theme.InkSoft, M(new Rectangle(tx, Theme.S(44), tw, Theme.S(36))), Align.Start, false, true);
            // on / off
            bool on = IsOn;
            Rectangle pill = M(new Rectangle(Width - ip - ar - Theme.S(8) - pillW, (Height - Theme.S(24)) / 2, pillW, Theme.S(24)));
            Theme.FillRound(g, pill, Theme.S(12), on ? Theme.SuccessBg : Theme.Selected);
            Theme.Text(g, StateText, Theme.F(8f, FontStyle.Bold), on ? Theme.Success : Theme.InkFaint, pill, Align.Center, true, false, true);
            // the arrow nudges forward when pointed at
            int nudge = (int)(hot * Theme.S(4)) * (rtl ? -1 : 1);
            Rectangle a = M(new Rectangle(Width - ip - ar, (Height - ar) / 2, ar, ar));
            a.Offset(nudge, 0);
            Theme.FillRound(g, a, ar / 2f, Theme.Mix(Theme.CardInner, Theme.AccentFill, hot * 0.9f));
            Theme.Glyph(g, rtl ? Ico.Back : Ico.Forward, 9f, Theme.Mix(Theme.Ink, Theme.PrimaryInk, hot), a);
            PaintFocus(g, card, Theme.S(14));
        }

        /// A small donut: eight formats round the outside, four actions inside, the logo's colour in the middle.
        public static void DrawMini(Graphics g, Rectangle r, float turn)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, R = r.Width / 2f;
            Color acc = Theme.AccentColor;
            for (int ring = 0; ring < 2; ring++)
            {
                int n = ring == 0 ? 8 : 4;
                float ro = ring == 0 ? R : R * 0.6f, ri = ring == 0 ? R * 0.64f : R * 0.34f;
                float step = 360f / n, gapDeg = ring == 0 ? 5f : 8f;
                for (int i = 0; i < n; i++)
                {
                    float a0 = -90 - step / 2 + i * step + gapDeg / 2 + (ring == 0 ? turn : -turn);
                    using (GraphicsPath p = new GraphicsPath())
                    {
                        p.AddArc(cx - ro, cy - ro, ro * 2, ro * 2, a0, step - gapDeg);
                        p.AddArc(cx - ri, cy - ri, ri * 2, ri * 2, a0 + step - gapDeg, -(step - gapDeg));
                        p.CloseFigure();
                        float k = ring == 0 ? (i % 2 == 0 ? 0.9f : 0.6f) : 0.35f;
                        using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.CardInner, acc, k))) g.FillPath(b, p);
                    }
                }
            }
            float h = R * 0.26f;
            using (SolidBrush b = new SolidBrush(acc)) g.FillEllipse(b, cx - h, cy - h, h * 2, h * 2);
        }
    }
}
