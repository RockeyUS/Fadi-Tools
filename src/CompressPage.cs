using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// One-click size limits ("Discord · 20 MB"): a row of small two-line chips, one can be picked.
    public class PresetChips : UiControl
    {
        public class Preset { public string Name; public double Limit, Value; }
        public readonly List<Preset> Items = new List<Preset>();
        public int Selected = -1;
        int hover = -1;
        public event Action<int> Picked;
        public PresetChips() { Cursor = Cursors.Hand; }

        Rectangle Chip(int i)
        {
            int n = Math.Max(1, Items.Count), gap = Theme.S(6);
            int w = (Width - gap * (n - 1)) / n;
            return Theme.Mirror(new Rectangle(i * (w + gap), 0, w, Height - 1), Width);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; for (int i = 0; i < Items.Count; i++) if (Chip(i).Contains(e.Location)) h = i;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (!Enabled) return;
            for (int i = 0; i < Items.Count; i++)
                if (Chip(i).Contains(e.Location)) { Selected = Selected == i ? -1 : i; Invalidate(); if (Picked != null) Picked(Selected); return; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            for (int i = 0; i < Items.Count; i++)
            {
                Rectangle c = Chip(i);
                RectangleF r = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1);
                bool on = i == Selected, hot = i == hover && Enabled;
                Theme.FillRound(g, r, Theme.S(9), on ? Theme.AccentSoft : hot ? Theme.Hover : Theme.CardInner);
                Theme.StrokeRound(g, r, Theme.S(9), on ? Theme.AccentColor : Theme.CardBorder, on ? 1.4f : 1f);
                Color ink = !Enabled ? Theme.InkFaint : on ? Theme.AccentColor : Theme.Ink;
                Theme.Text(g, Items[i].Name, Theme.F(8.75f, FontStyle.Bold), ink, new Rectangle(c.X + Theme.S(4), c.Y + Theme.S(5), c.Width - Theme.S(8), Theme.S(18)), Align.Center, true, false);
                Theme.Text(g, Items[i].Limit.ToString("0.#", CultureInfo.InvariantCulture) + " MB", Theme.F(8f), on ? Theme.Mix(Theme.AccentColor, Theme.InkSoft, 0.35f) : Theme.InkFaint,
                           new Rectangle(c.X + Theme.S(4), c.Y + Theme.S(23), c.Width - Theme.S(8), Theme.S(16)), Align.Center, true, false, true);
            }
        }
    }

    /// The compressor's file table: name · size (and what was saved) · type · status · action.
    public class CompressList : ScrollList
    {
        public List<FileItem> Items = new List<FileItem>();
        public bool Locked;
        public event Action Changed;
        public event Action<FileItem> Opened;
        float spin;
        int hoverAction = -1;

        public CompressList() { RowHeight = 66; Surface = Surface.Card; }
        /// Big rows with a picture of each file instead of an icon.
        public bool Previews { get { return previews; } set { previews = value; RowHeight = value ? 112 : 66; ClampScroll(); Invalidate(); } }
        bool previews;
        void Repaint() { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { } }
        public override int Count { get { return Items.Count; } }
        protected override string[] DragFiles(int i)
        {
            FileItem it = Items[i];
            return new[] { it.State == ItemState.Done && !string.IsNullOrEmpty(it.Output) && System.IO.File.Exists(it.Output) ? it.Output : it.Path };
        }

        /// Column layout shared with the header row (left-to-right coordinates inside a given width).
        public static void Cols(int w, out Rectangle name, out Rectangle size, out Rectangle type, out Rectangle status, out Rectangle act)
        {
            int actW = Theme.S(50), stW = Theme.S(62), tyW = Theme.S(62), szW = Theme.S(178);
            act = new Rectangle(w - actW - Theme.S(10), 0, actW, 0);
            status = new Rectangle(act.X - stW, 0, stW, 0);
            type = new Rectangle(status.X - tyW, 0, tyW, 0);
            size = new Rectangle(type.X - szW, 0, szW, 0);
            name = new Rectangle(Theme.S(14), 0, size.X - Theme.S(26), 0);
        }

        public void RefreshItem(FileItem it)
        {
            int i = Items.IndexOf(it);
            if (i >= 0) Invalidate(RowRect(i));
            if (it.State == ItemState.Working) Animator.Start(this);
        }

        public override bool Step()
        {
            bool more = base.Step();
            bool working = false;
            foreach (FileItem it in Items) if (it.State == ItemState.Working) working = true;
            if (working) { spin = (spin + 9) % 360; Invalidate(); return true; }
            return more;
        }

        Rectangle ActRect(Rectangle row)
        {
            Rectangle n, s, t, st, a;
            Cols(row.Width, out n, out s, out t, out st, out a);
            int b = Theme.S(36);
            return Theme.Mirror(new Rectangle(row.X + a.X + (a.Width - b) / 2, row.Y + (row.Height - b) / 2, b, b), Width);
        }

        protected override void RowHover(int index, Point local, Rectangle row)
        {
            int h = ActRect(row).Contains(local) ? index : -1;
            if (h != hoverAction) { hoverAction = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { hoverAction = -1; base.OnMouseLeave(e); }

        protected override void RowClick(int index, Point local, Rectangle row)
        {
            FileItem it = Items[index];
            if (!ActRect(row).Contains(local)) return;
            if (it.State == ItemState.Done && it.Output != null) { if (Opened != null) Opened(it); return; }
            if (!Locked && it.State != ItemState.Working)
            {
                Items.RemoveAt(index);
                hoverAction = -1;
                ClampScroll(); Invalidate();
                if (Changed != null) Changed();
            }
        }
        protected override void RowDoubleClick(int index) { if (Opened != null) Opened(Items[index]); }

        static string GlyphFor(FileItem it)
        {
            Category c = Formats.CategoryOf(it.Ext);
            if (c == Category.Image) return Ico.Photo;
            if (c == Category.Video) return Ico.Video;
            if (c == Category.Audio) return Ico.Music;
            if (c == Category.Document || c == Category.Data) return Ico.Doc;
            return Ico.Page;
        }

        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            FileItem it = Items[i];
            Theme.Smooth(g);
            Rectangle inner = new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(3), r.Width - Theme.S(12), r.Height - Theme.S(6));
            if (it.State == ItemState.Working)
            {
                Theme.FillRound(g, inner, Theme.S(10), Theme.AccentSoft);
                Theme.StrokeRound(g, inner, Theme.S(10), Theme.Mix(Theme.AccentSoft, Theme.AccentColor, 0.6f));
            }
            else if (hover) Theme.FillRound(g, inner, Theme.S(10), Theme.Hover);
            else if (i < Items.Count - 1)
                using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, r.X + Theme.S(16), r.Bottom - 1, r.Right - Theme.S(16), r.Bottom - 1);

            Rectangle cn, cs, ct, cst, ca;
            Cols(r.Width, out cn, out cs, out ct, out cst, out ca);
            Func<Rectangle, Rectangle> M = x => Theme.Mirror(new Rectangle(r.X + x.X, x.Y, x.Width, x.Height), Width);

            // icon tile (or a picture of the file) + name + detail line
            int tw = previews ? Theme.S(138) : Theme.S(40), th = previews ? r.Height - Theme.S(16) : Theme.S(40);
            Rectangle tile = M(new Rectangle(cn.X, r.Y + (r.Height - th) / 2, tw, th));
            if (previews && Thumbs.Draw(g, it.Path, tile, Theme.S(10), Repaint)) Theme.StrokeRound(g, tile, Theme.S(10), Theme.CardBorder);
            else
            {
                Theme.FillRound(g, tile, Theme.S(10), Theme.CardInner);
                Theme.StrokeRound(g, tile, Theme.S(10), Theme.CardBorder);
                Theme.Glyph(g, GlyphFor(it), previews ? 14f : 11f, Theme.InkSoft, tile);
            }
            // where the lines go: beside a picture they stack (name · detail · size), otherwise in columns
            Rectangle nameR, subR, sizeR;
            int top = r.Y + (r.Height - Theme.S(68)) / 2;
            int px = cn.X + tw + Theme.S(14), pw0 = ct.X - px - Theme.S(10);
            if (previews)
            {
                nameR = M(new Rectangle(px, top, pw0, Theme.S(20)));
                subR = M(new Rectangle(px, top + Theme.S(23), pw0, Theme.S(18)));
            }
            else
            {
                int nx = cn.X + Theme.S(54), nw = cn.Width - Theme.S(54);
                nameR = M(new Rectangle(nx, r.Y + Theme.S(13), nw, Theme.S(20)));
                subR = M(new Rectangle(nx, r.Y + Theme.S(34), nw, Theme.S(18)));
            }
            Theme.Text(g, it.Name, Theme.F(10f, FontStyle.Bold), Theme.Ink, nameR, Align.Start, true, false);
            string sub; Color subC = Theme.InkFaint;
            switch (it.State)
            {
                case ItemState.Working: sub = L.T("cmp_row_working", (int)(it.Progress * 100)); subC = Theme.Mix(Theme.InkSoft, Theme.AccentColor, 0.5f); break;
                case ItemState.Failed: sub = it.Message; subC = Theme.Error; break;
                case ItemState.Done:
                    sub = it.Output != null ? (it.DoneText != null ? it.DoneText + "  ·  " : "") + L.T("cmp_row_saved", Path.GetFileName(it.Output)) : (it.DoneText ?? "");
                    if (it.Output != null && it.DoneText != null) subC = Theme.Warn;
                    break;
                case ItemState.Skipped: sub = L.T("cancelled"); break;
                default: sub = Path.GetDirectoryName(it.Path); break;
            }
            Theme.Text(g, sub, Theme.F(8.5f), subC, subR, Align.Start, true, false);

            // size: before, and the savings badge after
            string sz = Theme.HumanSize(it.Size);
            if (it.State == ItemState.Done && it.Output != null && File.Exists(it.Output)) sz = Theme.HumanSize(it.Size) + "  →  " + Theme.HumanSize(new FileInfo(it.Output).Length);
            Font szf = Theme.F(9.25f);
            int szw = Theme.Measure(sz, szf) + Theme.S(4);
            if (previews) sizeR = M(new Rectangle(px, top + Theme.S(46), Math.Min(pw0, szw), Theme.S(20)));
            else sizeR = M(new Rectangle(cs.X, r.Y + Theme.S(12), cs.Width - Theme.S(12), Theme.S(20)));
            Theme.Text(g, sz, szf, Theme.InkSoft, sizeR, previews ? Align.Start : Align.End, true, false, true);
            if (it.State == ItemState.Done)
            {
                string badge; Color bg, fg;
                if (it.DoneQuiet || it.Output == null) { badge = L.T("cmp_badge_same"); bg = Theme.Selected; fg = Theme.InkSoft; }
                else
                {
                    long after = File.Exists(it.Output) ? new FileInfo(it.Output).Length : it.Size;
                    long saved = Math.Max(0, it.Size - after);
                    if (after > it.Size)
                    {
                        badge = "+" + Math.Min(9999, (after - it.Size) * 100 / Math.Max(1, it.Size)) + "%  ·  " + L.T("cmp_badge_bigger");
                        bg = Theme.Selected; fg = Theme.Ink;
                    }
                    else
                    {
                        badge = "−" + Math.Min(99, saved * 100 / Math.Max(1, it.Size)) + "%  ·  " + L.T("cmp_badge_saved", Theme.HumanSize(saved));
                        bg = Theme.SuccessBg; fg = Theme.Success;
                    }
                }
                Font bf = Theme.F(8f, FontStyle.Bold);
                int bw = Theme.Measure(badge, bf) + Theme.S(14);
                Rectangle br = previews ? new Rectangle(px + szw + Theme.S(10), top + Theme.S(47), bw, Theme.S(18))
                                        : new Rectangle(cs.Right - Theme.S(12) - bw, r.Y + Theme.S(36), bw, Theme.S(18));
                if (!previews || px + szw + Theme.S(10) + bw <= ct.X)
                {
                    br = M(br);
                    Theme.FillRound(g, br, Theme.S(5), bg);
                    Theme.Text(g, badge, bf, fg, br, Align.Center, true, false, !Theme.RTL);
                }
            }
            else if (it.State == ItemState.Working)
            {
                Rectangle tr = previews ? M(new Rectangle(px + szw + Theme.S(12), top + Theme.S(54), Math.Max(Theme.S(20), Math.Min(Theme.S(180), pw0 - szw - Theme.S(12))), Theme.S(4)))
                                        : M(new Rectangle(cs.X + Theme.S(40), r.Y + Theme.S(42), cs.Width - Theme.S(52), Theme.S(4)));
                Theme.FillRound(g, tr, Theme.S(2), Theme.Track);
                if (it.Progress > 0)
                {
                    int fw = Math.Max(tr.Height, (int)(tr.Width * it.Progress));
                    Rectangle fr = Theme.RTL ? new Rectangle(tr.Right - fw, tr.Y, fw, tr.Height) : new Rectangle(tr.X, tr.Y, fw, tr.Height);
                    Theme.FillRound(g, fr, Theme.S(2), Theme.AccentColor);
                }
            }

            // type pill
            string ext = (it.Ext ?? "").ToUpperInvariant();
            if (ext.Length > 5) ext = ext.Substring(0, 5);
            Font pf = Theme.F(8f, FontStyle.Bold);
            int pw = Math.Max(Theme.S(40), Theme.Measure(ext, pf) + Theme.S(14));
            Rectangle pill = M(new Rectangle(ct.X + (ct.Width - pw) / 2, r.Y + (r.Height - Theme.S(22)) / 2, pw, Theme.S(22)));
            Theme.FillRound(g, pill, Theme.S(6), Theme.Selected);
            Theme.Text(g, ext, pf, Theme.InkSoft, pill, Align.Center, true, false, true);

            // status
            int d = Theme.S(22);
            Rectangle sr = M(new Rectangle(cst.X + (cst.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d));
            switch (it.State)
            {
                case ItemState.Done:
                    using (SolidBrush b = new SolidBrush(it.DoneQuiet ? Theme.InkFaint : Theme.Success)) g.FillEllipse(b, sr);
                    Theme.Glyph(g, Ico.Check, 7f, Color.White, sr);
                    break;
                case ItemState.Failed:
                    using (SolidBrush b = new SolidBrush(Theme.Error)) g.FillEllipse(b, sr);
                    Theme.Glyph(g, Ico.Close, 7f, Color.White, sr);
                    break;
                case ItemState.Working:
                    using (Pen p0 = new Pen(Theme.Track, Theme.S(2.5f))) g.DrawEllipse(p0, Rectangle.Inflate(sr, -Theme.S(2), -Theme.S(2)));
                    using (Pen p1 = new Pen(Theme.AccentColor, Theme.S(2.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(p1, Rectangle.Inflate(sr, -Theme.S(2), -Theme.S(2)), spin, 100);
                    break;
                case ItemState.Skipped:
                    using (Pen p2 = new Pen(Theme.InkFaint, Theme.S(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLine(p2, sr.X + Theme.S(6), sr.Y + d / 2, sr.Right - Theme.S(6), sr.Y + d / 2);
                    break;
                default:
                    using (Pen p3 = new Pen(Theme.FieldBorder, Theme.S(1.5f))) g.DrawEllipse(p3, Rectangle.Inflate(sr, -Theme.S(3), -Theme.S(3)));
                    break;
            }

            // action: open the result (accent tile), or remove the row
            Rectangle ar = ActRect(r);
            bool ah = hoverAction == i;
            if (it.State == ItemState.Done && it.Output != null)
            {
                Theme.FillRound(g, ar, Theme.S(9), ah ? Theme.Mix(Theme.AccentColor, Color.Black, 0.1f) : Theme.AccentColor);
                Theme.Glyph(g, Ico.FolderOpen, 10f, Theme.PrimaryInk, ar);
            }
            else if (!Locked && it.State != ItemState.Working)
            {
                if (ah) Theme.FillRound(g, ar, Theme.S(9), Theme.Selected);
                Theme.Glyph(g, Ico.Close, 8f, ah ? Theme.Ink : Theme.InkFaint, ar);
            }
        }
    }

    /// Makes pictures, videos, songs, PDFs and Office files smaller — same resolution, same format where possible.
    public class CompressPage : Page
    {
        Rectangle left, opts, acts, filesTitleR, headR, footR, levelLblR, hintR, targetLblR, mbR, targetHintR, engineR, statusR, actsLblR;
        readonly Btn btnAdd, btnClear, btnGo, btnShow, btnMore, btnWipe, btnView;
        bool viewFits = true;
        // "Grow smaller files to this size": pictures, videos and songs under the size are made bigger instead of skipped
        readonly Toggle grow = new Toggle();
        Rectangle growR;
        readonly CompressList list = new CompressList();
        readonly DropZone drop;
        readonly Segments level;
        readonly TextField targetMb = new TextField(() => L.T("cmp_target_ph"), null) { ForceLtr = true };
        readonly PresetChips presets = new PresetChips();
        bool settingPreset;
        readonly Progress progress = new Progress();
        string statusText = ""; Color statusColor;
        bool running, cancel;
        string lastOutputFile, lastOutputDir; int doneCount;
        long totalBefore, totalAfter;
        string gpu = "";        // "NVIDIA", "Intel", "AMD" or "" (processor)
        bool gpuChecked, gpuChecking;

        public CompressPage() : base("cmp_title", "cmp_sub")
        {
            btnAdd = new Btn(() => L.T("add_files"), Ico.Add, BtnKind.Secondary);
            btnAdd.Click += (s, e) => Browse();
            list.Previews = Settings.Get("CompressPreviews", "1") == "1";
            // List / Previews: big rows with a picture of each file
            btnView = new Btn(() => L.T(list.Previews ? "view_list" : "view_previews"), list.Previews ? "\uE8FD" : "\uF0E2", BtnKind.Ghost);
            btnView.Click += (s, e) => { list.Previews = !list.Previews; Settings.Set("CompressPreviews", list.Previews ? "1" : "0"); btnView.Glyph = list.Previews ? "\uE8FD" : "\uF0E2"; DoLayout(); };
            btnClear = new Btn(() => L.T("clear"), Ico.Delete, BtnKind.Ghost);
            btnClear.Click += (s, e) => ClearList();
            list.Changed += () => UpdateState();
            list.Opened += it => { try { Process.Start("explorer.exe", "/select,\"" + (it.Output != null && File.Exists(it.Output) ? it.Output : it.Path) + "\""); } catch { } };
            drop = new DropZone(() => L.T("cmp_drop"), () => L.T("cmp_drop_sub")) { Tips = new[] { "tip_discord", "tip_grow", "tip_drag_out", "tip_paste" } };
            drop.Glyph = "";
            drop.Click += (s, e) => Browse();
            level = new Segments(() => L.T("cmp_light"), () => L.T("cmp_balanced"), () => L.T("cmp_strong"));
            int lv; int.TryParse(Settings.Get("CompressLevel", "1"), out lv);
            level.SetSilently(Math.Max(0, Math.Min(2, lv)));
            level.SelectedChanged += (s, e) => { Settings.Set("CompressLevel", level.SelectedIndex.ToString()); Invalidate(); };
            targetMb.Box.KeyPress += (s, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar) && e.KeyChar != '.' && e.KeyChar != ',') e.Handled = true; };
            targetMb.Box.TextChanged += (s, e) => { if (!settingPreset) SyncPreset(); UpdateState(); };
            // limits of popular apps; the value aims a little under so the file always goes through
            presets.Items.Add(new PresetChips.Preset { Name = "Discord", Limit = 20, Value = 19 });
            presets.Items.Add(new PresetChips.Preset { Name = L.T("cmp_p_email"), Limit = 25, Value = 24 });
            presets.Items.Add(new PresetChips.Preset { Name = L.T("cmp_p_avatar"), Limit = 1, Value = 1 });
            presets.Picked += i => UsePresetIndex(i);
            presets.Selected = -1;
            btnGo = new Btn(() => running ? L.T("cancel") : (list.Items.Count > 1 ? L.T("cmp_go_n", list.Items.Count) : L.T("cmp_go")), "", BtnKind.Primary);
            btnGo.FontSize = 11f; btnGo.Radius = 10;
            btnGo.Click += (s, e) => { if (running) { cancel = true; SetStatus(L.T("cancelling"), Theme.InkSoft); } else Start(); };
            btnShow = new Btn(() => doneCount == 1 ? L.T("show_file") : L.T("open_folder"), Ico.FolderOpen, BtnKind.Ghost) { AlignStart = true };
            btnShow.Click += (s, e) => OpenOutput();
            btnMore = new Btn(() => L.T("cmp_add_more"), Ico.Add, BtnKind.Ghost) { AlignStart = true };
            btnMore.Click += (s, e) => Browse();
            btnWipe = new Btn(() => L.T("cmp_clear_list"), Ico.Delete, BtnKind.Ghost) { AlignStart = true };
            btnWipe.Click += (s, e) => ClearList();
            progress.Visible = false;
            Controls.AddRange(new Control[] { btnAdd, btnClear, btnView, list, drop, level, targetMb, presets, progress, btnGo, btnShow, btnMore, btnWipe, grow });
            grow.SetSilently(Settings.Get("CompressGrow", "0") == "1");
            grow.CheckedChanged += (s, e) => Settings.Set("CompressGrow", grow.Checked ? "1" : "0");
            EnableDrop(list, drop);
            // the size preset picked last time (only once every control exists)
            int lastP; if (int.TryParse(Settings.Get("CompressPreset", ""), out lastP) && Settings.Get("CompressPresetV", "") != "2") { lastP = lastP == 3 ? 2 : lastP == 2 ? -1 : lastP; Settings.Set("CompressPresetV", "2"); Settings.Set("CompressPreset", lastP.ToString()); }
            if (int.TryParse(Settings.Get("CompressPreset", ""), out lastP) && lastP >= 0 && lastP < presets.Items.Count) UsePresetIndex(lastP);
            UpdateState();
        }

        public override bool Busy { get { return running; } }
        public override void Restyle() { targetMb.Restyle(); presets.Items[1].Name = L.T("cmp_p_email"); presets.Items[2].Name = L.T("cmp_p_avatar"); btnWipe.InkColor = Theme.Error; UpdateState(); }

        /// Picks a size preset (-1 = none) and fills the target field.
        void UsePresetIndex(int i)
        {
            settingPreset = true;
            presets.Selected = i;
            targetMb.Box.Text = i >= 0 ? presets.Items[i].Value.ToString(CultureInfo.InvariantCulture) : "";
            settingPreset = false;
            Settings.Set("CompressPreset", i.ToString());
            presets.Invalidate(); DoLayout(); UpdateState();
        }

        /// Typing a number by hand un-picks the preset (unless it's the same number).
        void SyncPreset()
        {
            int was = presets.Selected;
            presets.Selected = -1;
            string t = targetMb.Box.Text.Trim();
            for (int i = 0; i < presets.Items.Count; i++) if (t == presets.Items[i].Value.ToString(CultureInfo.InvariantCulture)) presets.Selected = i;
            if (presets.Selected != was) { Settings.Set("CompressPreset", presets.Selected.ToString()); presets.Invalidate(); DoLayout(); }
        }

        /// For the right-click menu and the quick search: "Compress for Discord" etc. (limit in MB, 0 = none).
        public void UsePreset(string name)
        {
            for (int i = 0; i < presets.Items.Count; i++) if (string.Equals(presets.Items[i].Name, name, StringComparison.OrdinalIgnoreCase) || (name == "email" && i == 1) || (name == "avatar" && i == 2)) { UsePresetIndex(i); return; }
            UsePresetIndex(-1);
        }

        public void Browse2() { Browse(); }

        /// Starts as soon as files are in the list (used by "Compress for Discord" from Explorer).
        public void StartNow() { if (!running && list.Items.Count > 0) Start(); }

        public override void OnShown()
        {
            // find out once whether the graphics card can help with videos
            if (gpuChecked || gpuChecking) return;
            gpuChecking = true;
            Thread t = new Thread(() =>
            {
                string g = "";
                try { g = HwEnc.Friendly(HwEnc.Detect()); } catch { }
                Post(() => { gpu = g; gpuChecked = true; gpuChecking = false; Invalidate(); });
            });
            t.IsBackground = true; t.Start();
        }

        void ClearList()
        {
            if (running) return;
            list.Items.Clear(); list.Invalidate(); progress.Value = 0; progress.Visible = false;
            doneCount = 0; totalBefore = totalAfter = 0;
            SetStatus("", Theme.InkSoft); UpdateState();
        }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, gap = Theme.S(18), rightW = Theme.S(320), ip = Theme.S(20);
            left = new Rectangle(p, Top0, W - p * 2 - rightW - gap, H - Top0 - p);
            int rx = left.Right + gap;
            cards.Clear(); cards.Add(left);

            // left: header · column titles · rows · footer
            filesTitleR = new Rectangle(left.X + ip, left.Y + Theme.S(14), left.Width - Theme.S(320), Theme.S(32));
            int bw = Math.Max(Theme.S(86), btnClear.PreferredWidth), aw = Math.Max(Theme.S(120), btnAdd.PreferredWidth);
            bool empty = list.Items.Count == 0;
            Place(btnClear, new Rectangle(left.Right - ip - bw, left.Y + Theme.S(12), bw, Theme.S(36)));
            Place(btnAdd, new Rectangle(left.Right - ip - aw - (empty ? 0 : bw + Theme.S(8)), left.Y + Theme.S(12), aw, Theme.S(36)));
            // with room: "Previews" / "List" in words, otherwise just the icon
            bool viewWords = left.Width > Theme.S(640);
            btnView.Label = viewWords ? (Func<string>)(() => L.T(list.Previews ? "view_list" : "view_previews")) : () => "";
            int vw = viewWords ? Math.Max(Theme.S(100), btnView.PreferredWidth) : Theme.S(36);
            btnView.Glyph = list.Previews ? "\uE8FD" : "\uF0E2";
            viewFits = left.Width > Theme.S(420);
            btnView.Visible = !empty && viewFits;
            Place(btnView, new Rectangle(left.Right - ip - aw - bw - vw - Theme.S(16), left.Y + Theme.S(12), vw, Theme.S(36)));
            headR = new Rectangle(left.X + Theme.S(6), left.Y + Theme.S(58), left.Width - Theme.S(12), Theme.S(30));
            footR = new Rectangle(left.X + ip, left.Bottom - Theme.S(44), left.Width - ip * 2, Theme.S(40));
            Place(list, new Rectangle(left.X + Theme.S(6), headR.Bottom + Theme.S(2), left.Width - Theme.S(12), footR.Y - headR.Bottom - Theme.S(6)));
            Place(drop, new Rectangle(left.X + ip, left.Y + Theme.S(62), left.Width - ip * 2, left.Height - Theme.S(62) - ip));

            // right: the big button, options, actions
            int y = Top0, w = rightW;
            Place(btnGo, new Rectangle(rx, y, w, Theme.S(52))); y += Theme.S(60);
            if (progress.Visible || statusText.Length > 0)
            {
                Place(progress, new Rectangle(rx + Theme.S(4), y + Theme.S(2), w - Theme.S(8), Theme.S(5)));
                statusR = new Rectangle(rx + Theme.S(4), y + Theme.S(10), w - Theme.S(8), Theme.S(36)); y += Theme.S(50);
            }
            else statusR = Rectangle.Empty;
            int x = rx + ip, cw = w - ip * 2;
            int oy = y + Theme.S(16);
            levelLblR = new Rectangle(x, oy, cw, Theme.S(18)); oy += Theme.S(24);
            Place(level, new Rectangle(x, oy, cw, Theme.S(36))); oy += Theme.S(42);
            hintR = new Rectangle(x, oy, cw, Theme.S(36)); oy += Theme.S(44);
            targetLblR = new Rectangle(x, oy, cw, Theme.S(18)); oy += Theme.S(24);
            Place(presets, new Rectangle(x, oy, cw, Theme.S(44))); oy += Theme.S(52);
            Place(targetMb, new Rectangle(x, oy, cw - Theme.S(52), Theme.S(40)));
            mbR = new Rectangle(x + cw - Theme.S(44), oy, Theme.S(44), Theme.S(40)); oy += Theme.S(46);
            targetHintR = new Rectangle(x, oy, cw, Theme.S(34)); oy += Theme.S(40);
            growR = new Rectangle(x, oy, cw - Theme.S(58), Theme.S(30));
            Place(grow, new Rectangle(x + cw - Theme.S(46), oy + Theme.S(3), Theme.S(46), Theme.S(24))); oy += Theme.S(38);
            engineR = new Rectangle(x, oy, cw, Theme.S(40)); oy += Theme.S(54);
            opts = new Rectangle(rx, y, w, oy - y);
            cards.Add(opts);
            int ay = opts.Bottom + gap;
            actsLblR = new Rectangle(x, ay + Theme.S(14), cw, Theme.S(18));
            int ry = ay + Theme.S(38);
            btnShow.Visible = doneCount > 0;
            if (btnShow.Visible) { Place(btnShow, new Rectangle(rx + Theme.S(8), ry, w - Theme.S(16), Theme.S(38))); ry += Theme.S(40); }
            Place(btnMore, new Rectangle(rx + Theme.S(8), ry, w - Theme.S(16), Theme.S(38))); ry += Theme.S(40);
            btnWipe.Visible = !empty;
            if (btnWipe.Visible) { Place(btnWipe, new Rectangle(rx + Theme.S(8), ry, w - Theme.S(16), Theme.S(38))); ry += Theme.S(40); }
            acts = new Rectangle(rx, ay, w, ry - ay + Theme.S(8));
            if (acts.Bottom < H - p) cards.Add(acts);
            Invalidate();
        }

        static void Caps(Graphics g, Page pg, string text, Rectangle r, Func<Rectangle, Rectangle> m)
        {
            Theme.Text(g, text.ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, m(r), Align.Start);
        }

        protected override void PaintContent(Graphics g)
        {
            bool empty = list.Items.Count == 0;
            long t = 0; foreach (FileItem it in list.Items) t += it.Size;
            Font tf = Theme.F(12f, FontStyle.Bold);
            Label(g, L.T("files"), tf, Theme.Ink, filesTitleR);
            if (!empty)
            {
                Rectangle cr = filesTitleR; cr.X += Theme.Measure(L.T("files"), tf) + Theme.S(6);
                Label(g, (list.Items.Count == 1 ? L.T("one_file") : L.T("n_files", list.Items.Count)) + "  ·  " + Theme.HumanSize(t), Theme.F(9f), Theme.InkFaint, cr);
                // column titles
                Rectangle cn, cs, ct, cst, ca;
                CompressList.Cols(headR.Width, out cn, out cs, out ct, out cst, out ca);
                Func<Rectangle, Rectangle> hm = r => M(new Rectangle(headR.X + r.X, headR.Y, r.Width, headR.Height));
                Font hf = Theme.F(7.75f, FontStyle.Bold);
                Theme.Text(g, L.T("cmp_col_name"), hf, Theme.InkFaint, hm(new Rectangle(cn.X + (list.Previews ? Theme.S(152) : Theme.S(54)), 0, cn.Width, 0)), Align.Start);
                if (!list.Previews) Theme.Text(g, L.T("cmp_col_size"), hf, Theme.InkFaint, hm(new Rectangle(cs.X, 0, cs.Width - Theme.S(12), 0)), Align.End);
                Theme.Text(g, L.T("cmp_col_type"), hf, Theme.InkFaint, hm(ct), Align.Center);
                Theme.Text(g, L.T("cmp_col_status"), hf, Theme.InkFaint, hm(cst), Align.Center);
                using (Pen lp = new Pen(Theme.Line))
                {
                    Rectangle l1 = M(new Rectangle(left.X + Theme.S(16), headR.Bottom, left.Width - Theme.S(32), 1));
                    g.DrawLine(lp, l1.X, l1.Y, l1.Right, l1.Y);
                    Rectangle l2 = M(new Rectangle(left.X + Theme.S(16), footR.Y, left.Width - Theme.S(32), 1));
                    g.DrawLine(lp, l2.X, l2.Y, l2.Right, l2.Y);
                }
                // footer: how many, and how much was saved
                Label(g, list.Items.Count == 1 ? L.T("one_file") : L.T("n_files", list.Items.Count), Theme.F(9f), Theme.InkSoft, footR);
                if (totalBefore > 0 && totalBefore > totalAfter)
                    Label(g, L.T("cmp_total_saved", Theme.HumanSize(totalBefore - totalAfter), Pct(totalBefore, totalAfter)), Theme.F(9f, FontStyle.Bold), Theme.Success, footR, Align.End);
            }

            // options card
            Func<Rectangle, Rectangle> m0 = r => M(r);
            Caps(g, this, L.T("cmp_how"), levelLblR, m0);
            Label(g, L.T(new[] { "cmp_hint_light", "cmp_hint_balanced", "cmp_hint_strong" }[level.SelectedIndex]), Theme.F(8.75f), Theme.InkSoft, hintR, Align.Start, false, true);
            Caps(g, this, L.T("cmp_fit"), targetLblR, m0);
            Label(g, "MB", Theme.F(10f, FontStyle.Bold), Theme.InkSoft, mbR, Align.Center);
            string th = presets.Selected >= 0 ? L.T("cmp_p_hint", presets.Items[presets.Selected].Name, presets.Items[presets.Selected].Limit) : L.T("cmp_target_hint");
            Label(g, th, Theme.F(8.5f), presets.Selected >= 0 ? Theme.InkSoft : Theme.InkFaint, targetHintR, Align.Start, false, true);
            Label(g, L.T("cmp_grow"), Theme.F(9f), Theme.Ink, growR, Align.Start, false, true);
            Rectangle er = M(engineR);
            Theme.FillRound(g, er, Theme.S(10), Theme.CardInner);
            Rectangle gi = M(new Rectangle(engineR.X + Theme.S(8), engineR.Y, Theme.S(26), engineR.Height));
            bool fast = gpuChecked && gpu.Length > 0;
            Theme.Glyph(g, fast ? "" : Ico.Info, 9.5f, fast ? Theme.AccentColor : Theme.InkFaint, gi);
            string et = !gpuChecked ? L.T("cmp_engine_check") : fast ? L.T("cmp_engine_gpu", gpu) : L.T("cmp_engine_cpu");
            Label(g, et, Theme.F(8.5f), Theme.InkSoft, new Rectangle(engineR.X + Theme.S(36), engineR.Y, engineR.Width - Theme.S(44), engineR.Height), Align.Start, true, true);

            // actions card
            if (cards.Contains(acts)) Caps(g, this, L.T("cmp_actions"), actsLblR, m0);
            if (!statusR.IsEmpty) Label(g, statusText, Theme.F(9f), statusColor, statusR, Align.Start, false, true);
        }

        void SetStatus(string t, Color c) { statusText = t; statusColor = c; DoLayout(); }

        /// How much smaller, in whole percent (never "100%" while something is left).
        static long Pct(long before, long after)
        {
            if (before <= 0) return 0;
            long p = (long)Math.Round((before - after) * 100.0 / before);
            return after > 0 ? Math.Min(99, p) : p;
        }

        void UpdateState()
        {
            bool empty = list.Items.Count == 0;
            drop.Visible = empty; list.Visible = !empty; btnClear.Visible = !empty; btnView.Visible = !empty && viewFits;
            btnClear.Enabled = btnAdd.Enabled = btnMore.Enabled = btnWipe.Enabled = level.Enabled = targetMb.Enabled = presets.Enabled = grow.Enabled = !running;
            btnWipe.InkColor = Theme.Error;
            list.Locked = running;
            btnGo.Enabled = running || !empty;
            btnGo.Kind = running ? BtnKind.Secondary : BtnKind.Primary;
            btnGo.Glyph = running ? Ico.Stop : "";
            DoLayout();
            btnGo.Invalidate();
        }

        void Browse()
        {
            if (running) return;
            using (OpenFileDialog d = new OpenFileDialog { Multiselect = true, Title = L.T("choose_files"), Filter = L.T("all_files") + " (*.*)|*.*" })
                if (LastDir.Show(d, FindForm(), "compress") == DialogResult.OK) AcceptFiles(d.FileNames);
        }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || running) return;
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileItem it in list.Items) have.Add(it.Path);
            foreach (string p0 in paths)
            {
                List<string> files = new List<string>();
                try { if (Directory.Exists(p0)) files.AddRange(Directory.GetFiles(p0, "*", SearchOption.AllDirectories)); else if (File.Exists(p0)) files.Add(Path.GetFullPath(p0)); } catch { }
                foreach (string f in files)
                {
                    if (have.Contains(f)) continue;
                    have.Add(f);
                    long size = 0; try { size = new FileInfo(f).Length; } catch { }
                    list.Items.Add(new FileItem { Path = f, Name = Path.GetFileName(f), Ext = Formats.Ext(f), Size = size });
                }
            }
            list.Invalidate();
            UpdateState();
        }

        void Start()
        {
            if (list.Items.Count == 0) return;
            double mb = 0;
            double.TryParse(targetMb.Box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out mb);
            string single = null, outDir;
            if (list.Items.Count == 1)
            {
                FileItem one = list.Items[0];
                string oe = Compressor.OutExt(one.Path, mb);
                single = SaveAsk.File(FindForm(), Compressor.OutName(one.Path, oe), oe);
                if (single == null) return;
                outDir = SaveAsk.Scratch();
            }
            else { outDir = SaveAsk.Folder(FindForm()); if (outDir == null) return; }
            Compressor.Level lv = (Compressor.Level)level.SelectedIndex;
            List<FileItem> items = new List<FileItem>(list.Items);
            foreach (FileItem it in items) { it.State = ItemState.Waiting; it.Message = ""; it.Progress = 0; it.Output = null; it.DoneText = null; it.DoneQuiet = false; }
            running = true; cancel = false; doneCount = 0; lastOutputFile = null; totalBefore = totalAfter = 0;
            lastOutputDir = single != null ? Path.GetDirectoryName(single) : outDir;
            progress.Visible = true; progress.Value = 0;
            statusText = L.T("cmp_starting"); statusColor = Theme.InkSoft;
            UpdateState();
            shownLimit = presets.Selected >= 0 ? presets.Items[presets.Selected].Limit : mb;
            act = Activity.Begin(this, L.T("act_k_compress"), items.Count == 1 ? items[0].Name : L.T("n_files", items.Count), () => { cancel = true; Ui(() => SetStatus(L.T("cancelling"), Theme.InkSoft)); });
            bool growOn = grow.Checked;
            Thread t = new Thread(() => Worker(items, outDir, single, lv, mb, growOn));
            t.IsBackground = true; t.Start();
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        ActJob act;
        double shownLimit;   // the limit as the person sees it (Discord's 20 MB, while aiming at 19)

        void Worker(List<FileItem> items, string outDir, string single, Compressor.Level lv, double mb, bool growOn)
        {
            long before = 0, after = 0; int ok = 0, fail = 0, fit = 0, grownN = 0;
            DateTime started = DateTime.Now;
            for (int i = 0; i < items.Count; i++)
            {
                FileItem it = items[i]; int index = i;
                if (cancel) { it.State = ItemState.Skipped; Ui(() => list.RefreshItem(it)); continue; }
                it.State = ItemState.Working;
                Ui(() => { list.RefreshItem(it); SetStatus(L.T("cmp_working", index + 1, items.Count) + "  ·  " + it.Name, Theme.InkSoft); });
                string error = null;
                long size0 = 0; try { size0 = new FileInfo(it.Path).Length; } catch { }
                long capPre = mb > 0 ? Compressor.CapBytes(it.Path, mb) : 0;
                bool growIt = growOn && mb > 0 && size0 > 0 && size0 < capPre * 0.9 && Compressor.CanGrow(it.Path);
                if (mb > 0 && size0 > 0 && size0 <= capPre && !growIt)
                {
                    // already under the size asked for: nothing to do (and nothing to wait for)
                    string lim = shownLimit.ToString("0.#", CultureInfo.InvariantCulture);
                    it.State = ItemState.Done; it.DoneText = !growOn && Compressor.CanGrow(it.Path) ? L.T("cmp_fits_grow", lim) : L.T("cmp_fits", lim); it.DoneQuiet = true;
                    before += size0; after += size0; ok++; fit++;
                    long fb = before, fa = after;
                    Ui(() => { list.RefreshItem(it); progress.Value = (index + 1) / (double)items.Count; totalBefore = fb; totalAfter = fa; Invalidate(); });
                    continue;
                }
                try
                {
                    // a size limit that full resolution can't reach: start straight at a smaller picture
                    int startH = 0;
                    if (mb > 0 && Formats.CategoryOf(Formats.Ext(it.Path)) == Category.Video)
                        try { startH = Compressor.SuggestHeight(Media.Probe(it.Path), mb); } catch { }
                    if (startH > 0) { int sh = startH; Ui(() => SetStatus(L.T("cmp_refit_res", it.Name, sh), Theme.InkSoft)); }
                    if (growIt) { startH = 0; Ui(() => SetStatus(L.T("cmp_growing", it.Name), Theme.InkSoft)); }
                    Job job = growIt && Formats.CategoryOf(Formats.Ext(it.Path)) == Category.Image ? Compressor.BuildGrow(it.Path, outDir, mb) : Compressor.Build(it.Path, outDir, lv, mb, true, startH, growIt);
                    error = RunOne(job, it, index, items.Count);
                    if (error != null && !cancel && job.Fallback != null)
                    {
                        // the graphics card couldn't do it: same job on the processor
                        try { if (File.Exists(job.Output)) File.Delete(job.Output); } catch { }
                        job = job.Fallback();
                        error = RunOne(job, it, index, items.Count);
                    }
                    if (error == null && mb > 0 && !cancel && File.Exists(job.Output) && Formats.CategoryOf(Formats.Ext(it.Path)) == Category.Video)
                    {
                        // the size limit wins: aim lower, and if the encoder can't go that low at this resolution, use a smaller picture
                        long cap = (long)(mb * 1024 * 1024), got = new FileInfo(job.Output).Length;
                        MediaInfo vi = null; try { vi = Media.Probe(it.Path); } catch { }
                        int shortSide = vi != null ? Math.Min(vi.Width, vi.Height) : 1080, maxH = startH;
                        double mbTry = mb, lastRatio = 0;
                        for (int attempt = 0; attempt < 4 && got > cap && error == null && !cancel; attempt++)
                        {
                            double r = got / (double)cap;
                            bool floor = r > 1.3 || (lastRatio > 0 && r > lastRatio * 0.85);   // far over, or aiming lower barely helped
                            if (floor)
                            {
                                int cur = maxH > 0 ? maxH : shortSide;
                                int next = 0;
                                foreach (int h in new[] { 1080, 720, 540, 480, 360, 240 })
                                    if (h < cur && (h / (double)cur) * (h / (double)cur) <= 1.15 / r) { next = h; break; }
                                if (next == 0) foreach (int h in new[] { 1080, 720, 540, 480, 360, 240 }) if (h < cur) { next = h; break; }
                                if (next == 0) break;
                                maxH = next;
                            }
                            else mbTry = mbTry * 0.96 / r;
                            lastRatio = r;
                            try { File.Delete(job.Output); } catch { }
                            int mh = maxH;
                            Ui(() => SetStatus(mh > 0 ? L.T("cmp_refit_res", it.Name, mh) : L.T("cmp_refit", it.Name), Theme.InkSoft));
                            job = Compressor.Build(it.Path, outDir, lv, mbTry, true, maxH, growIt);
                            error = RunOne(job, it, index, items.Count);
                            if (error != null && !cancel && job.Fallback != null)
                            {
                                try { if (File.Exists(job.Output)) File.Delete(job.Output); } catch { }
                                job = job.Fallback();
                                error = RunOne(job, it, index, items.Count);
                            }
                            if (error == null && File.Exists(job.Output)) got = new FileInfo(job.Output).Length;
                        }
                        if (error == null && maxH > 0) it.DoneText = L.T("cmp_made_res", maxH, shownLimit.ToString("0.#", CultureInfo.InvariantCulture));
                        if (error == null && got > cap) it.DoneText = L.T("cmp_still_big", Theme.HumanSize(got));
                    }
                    if (error == null && mb > 0 && !cancel && File.Exists(job.Output) && Formats.CategoryOf(Formats.Ext(it.Path)) == Category.Image)
                    {
                        long got = new FileInfo(job.Output).Length;
                        if (job.Note != null) it.DoneText = L.T(growIt ? "cmp_img_grow" : "cmp_img_fit", job.Note, shownLimit.ToString("0.#", CultureInfo.InvariantCulture));
                        if (got > Compressor.CapBytes(it.Path, mb)) it.DoneText = L.T("cmp_img_still", Theme.HumanSize(got));
                    }
                    if (error == null)
                    {
                        long b = new FileInfo(it.Path).Length, a = File.Exists(job.Output) ? new FileInfo(job.Output).Length : long.MaxValue;
                        long capB = mb > 0 ? Compressor.CapBytes(it.Path, mb) : 0;
                        bool nowFits = mb > 0 && b > capB && a <= capB;   // over the limit before, under it now: always worth keeping
                        bool grew = growIt && a > b && a <= capB;           // made bigger on purpose
                        if (grew) { grownN++; if (job.Note == null) it.DoneText = null; }
                        if (a >= b * 0.98 && !nowFits && !grew)
                        {
                            // no real gain: keep the original as it is
                            try { File.Delete(job.Output); } catch { }
                            it.DoneText = L.T("cmp_already"); it.DoneQuiet = true;
                            before += b; after += b;
                        }
                        else
                        {
                            string final = job.Output;
                            if (single != null)
                            {
                                if (File.Exists(single)) File.Delete(single);
                                File.Move(job.Output, single); final = single;
                                try { Directory.Delete(outDir, true); } catch { }
                            }
                            it.Output = final; lastOutputFile = final; doneCount++;
                            before += b; after += a;
                        }
                    }
                }
                catch (UserError ue) { error = ue.Message; }
                catch (Exception ex) { error = ex.Message; }
                if (error == null) { it.State = ItemState.Done; ok++; }
                else if (cancel) it.State = ItemState.Skipped;
                else { it.State = ItemState.Failed; it.Message = error; fail++; }
                long b2 = before, a2 = after;
                Ui(() => { list.RefreshItem(it); progress.Value = (index + 1) / (double)items.Count; totalBefore = b2; totalAfter = a2; Invalidate(); });
            }
            double secs = (DateTime.Now - started).TotalSeconds;
            Ui(() =>
            {
                running = false;
                progress.Value = cancel ? 0 : 1;
                long saved = before - after;
                totalBefore = before; totalAfter = after;
                string none = fit > 0 && fit == ok ? L.T("cmp_all_fit", shownLimit.ToString("0.#", CultureInfo.InvariantCulture)) : L.T("cmp_nothing");
                Activity.End(act, ok == 0 ? L.T("cmp_none") : null, cancel,
                    saved > 0 ? L.T("cmp_saved", Theme.HumanSize(saved), Pct(before, after)) + "  ·  " + L.T("cmp_took", Theme.Time(secs)) : grownN > 0 ? L.T("cmp_grew_n", grownN) : none,
                    doneCount == 1 ? lastOutputFile : lastOutputDir);
                if (cancel) SetStatus(L.T("cancelled"), Theme.InkSoft);
                else if (ok == 0) SetStatus(L.T("cmp_none"), Theme.Error);
                else if (saved <= 0 && grownN > 0) SetStatus(L.T("cmp_grew_n", grownN) + "  ·  " + L.T("cmp_took", Theme.Time(secs)), Theme.Success);
                else if (saved <= 0) SetStatus(none, fit > 0 && fit == ok ? Theme.Success : Theme.InkSoft);
                else SetStatus(L.T("cmp_saved", Theme.HumanSize(saved), Pct(before, after)) + "  ·  " + L.T("cmp_took", Theme.Time(secs)) + (fail > 0 ? "  ·  " + L.T("cmp_failed_n", fail) : ""), fail > 0 ? Theme.Warn : Theme.Success);
                progress.Visible = false;
                UpdateState();
                list.Invalidate();
                if (doneCount > 0 && (saved > 0 || grownN > 0))
                {
                    ShowToast(saved > 0 ? L.T("cmp_saved", Theme.HumanSize(saved), Pct(before, after)) : L.T("cmp_grew_n", grownN), true, () => doneCount == 1 ? L.T("show_file") : L.T("open_folder"), OpenOutput);
                    if (Settings.OpenAfter) OpenOutput();
                }
            });
        }

        string RunOne(Job job, FileItem it, int index, int count)
        {
            DateTime last = DateTime.MinValue;
            if (job.Encoder != null) Ui(() => SetStatus(L.T("cmp_working", index + 1, count) + "  ·  " + it.Name + "  ·  " + L.T("cmp_on_gpu", job.Encoder), Theme.InkSoft));
            return Engine.Run(job, f =>
            {
                it.Progress = f;
                if ((DateTime.Now - last).TotalMilliseconds < 100) return;
                last = DateTime.Now;
                Activity.Update(act, (index + f) / count, count > 1 ? (index + 1) + "/" + count + "  ·  " + (int)(f * 100) + "%" : (int)(f * 100) + "%");
                Ui(() => { list.RefreshItem(it); progress.Value = (index + f) / count; });
            }, () => cancel);
        }

        void OpenOutput()
        {
            try
            {
                if (doneCount == 1 && lastOutputFile != null && File.Exists(lastOutputFile)) Process.Start("explorer.exe", "/select,\"" + lastOutputFile + "\"");
                else if (lastOutputDir != null) Process.Start("explorer.exe", "\"" + lastOutputDir + "\"");
            }
            catch { }
        }
    }
}
