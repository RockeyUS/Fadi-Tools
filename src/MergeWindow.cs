using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// One video (or song) to merge: what it is, a small picture of it, when it was taken.
    public class MergeItem
    {
        public string File;
        public MediaInfo Info;
        public Image Thumb;
        public int W, H;
        public DateTime When;
        public bool Ready, Bad;
        /// PDFs and pictures (the PDF merge window): page count (-1 unknown), size, and the glyph when there's no picture.
        public int Pages = -1;
        public long Bytes;
        public string Glyph;
        public string Name { get { return Path.GetFileName(File); } }
    }

    /// The list of clips in the merge window: their order (drag a row, or the arrows), a play button and a remove button
    /// on each.
    public class MergeList : UiControl
    {
        public List<MergeItem> Items = new List<MergeItem>();
        public event Action Changed;
        public event Action<MergeItem> PlayAsked;
        /// The line under each name (null: length, size and date of a video or song).
        public Func<MergeItem, string> SubText;
        /// The first button on a row (play, or open for documents) and its name.
        public string FirstGlyph = Ico.Play;
        /// Pictures come from Windows' own thumbnails (PDFs, photos), in a box this wide.
        public bool ShellThumbs;
        /// The row being played inside the window (or paused there), how far in, and whether it's playing now.
        public MergeItem PlayingItem;
        public double PlayingAt;
        public bool IsPlaying;
        public int ThumbW = 78;
        int scroll, hover = -1, hoverBtn = -1, dragFrom = -1, dragTo = -1;
        Point downAt; bool dragging;

        public MergeList() { Surface = Surface.Card; DoubleBuffered = true; Cursor = Cursors.Default; }

        public int RowH { get { return Theme.S(58); } }
        int ContentH { get { return Items.Count * RowH; } }
        int MaxScroll { get { return Math.Max(0, ContentH - Height); } }
        Rectangle Row(int i) { return new Rectangle(0, i * RowH - scroll, Width, RowH); }

        // ---- rows glide to their new place when the order changes (sorting, reversing, the arrows, a drop),
        // and the rows that moved glow for a moment, so the eye can follow them
        readonly Dictionary<MergeItem, float> shownY = new Dictionary<MergeItem, float>();
        readonly Dictionary<MergeItem, int> lastSlot = new Dictionary<MergeItem, int>();
        readonly Dictionary<MergeItem, float> glow = new Dictionary<MergeItem, float>();
        int dragY, grabDy;
        float YOf(MergeItem it, int i) { float y; return shownY.TryGetValue(it, out y) ? y : i * RowH; }
        float GlowOf(MergeItem it) { float v; return glow.TryGetValue(it, out v) ? v : 0; }
        /// Where a row is drawn now (the dragged row follows the mouse).
        Rectangle DrawRow(int i)
        {
            if (dragging && i == dragFrom) return new Rectangle(0, dragY - grabDy, Width, RowH);
            return new Rectangle(0, (int)Math.Round(YOf(Items[i], i)) - scroll, Width, RowH);
        }
        bool Moving(int i) { return (dragging && i == dragFrom) || Math.Abs(YOf(Items[i], i) - i * RowH) > 0.5f || GlowOf(Items[i]) > 0.02f; }

        /// Notices rows that changed place since the last look and starts them gliding.
        int lastCount = -1;
        void Track()
        {
            bool go = false;
            // a row taken out: the ones under it slide up, but nothing changed order, so nothing glows
            bool removal = lastCount >= 0 && Items.Count < lastCount;
            lastCount = Items.Count;
            for (int i = 0; i < Items.Count; i++)
            {
                MergeItem it = Items[i];
                int was;
                if (!lastSlot.TryGetValue(it, out was)) { lastSlot[it] = i; shownY[it] = i * RowH; continue; }
                if (was != i)
                {
                    lastSlot[it] = i;
                    if (Fx.Motion) { if (!removal) glow[it] = 1; go = true; } else shownY[it] = i * RowH;
                }
                if (Moving(i)) go = true;
            }
            if (go) Animator.Start(this);
        }

        public override bool Step()
        {
            bool more = base.Step();
            for (int i = 0; i < Items.Count; i++)
            {
                MergeItem it = Items[i];
                float y = YOf(it, i), t = i * RowH;
                if (Math.Abs(t - y) > 0.5f) { y += (t - y) * Animator.Rate(0.2f); more = true; } else y = t;
                shownY[it] = y;
                float gl = GlowOf(it);
                if (gl > 0) { if (Fx.Ease(ref gl, 0, 0.1f)) more = true; glow[it] = gl; }
            }
            Invalidate();
            return more;
        }

        /// The buttons at the end of a row: 0 play, 1 up, 2 down, 3 remove.
        Rectangle Btn(int i, int b) { Rectangle r = DrawRow(i); int s = Theme.S(30), gap = Theme.S(2), right = r.Right - Theme.S(8); return Theme.Mirror(new Rectangle(right - (4 - b) * (s + gap), r.Y + (r.Height - s) / 2, s, s), Width); }
        string[] Glyphs { get { return new[] { FirstGlyph, "\uE70E", "\uE70D", Ico.Close }; } }
        void Repaint() { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Theme.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(10), Theme.CardInner);
            g.SetClip(new Rectangle(1, 1, Width - 2, Height - 2));
            Font nf = Theme.F(9.25f, FontStyle.Bold), sf = Theme.F(8.25f);
            Track();
            // the lines between places stay put; rows that are still moving are drawn last, over the others
            for (int i = 0; i < Items.Count - 1; i++)
                using (Pen p = new Pen(Theme.Line)) { int ly = (i + 1) * RowH - scroll - 1; g.DrawLine(p, Theme.S(12), ly, Width - Theme.S(12), ly); }
            List<int> order = new List<int>();
            for (int i = 0; i < Items.Count; i++) if (!Moving(i)) order.Add(i);
            for (int i = 0; i < Items.Count; i++) if (Moving(i) && !(dragging && i == dragFrom)) order.Add(i);
            if (dragging && dragFrom >= 0 && dragFrom < Items.Count) order.Add(dragFrom);
            foreach (int i in order)
            {
                Rectangle r = DrawRow(i);
                if (r.Bottom < 0 || r.Y > Height) continue;
                MergeItem it = Items[i];
                bool lifted = dragging && i == dragFrom;
                bool moving = Moving(i);
                bool travelling = lifted || Math.Abs(YOf(it, i) - i * RowH) > 0.5f;
                if (moving)
                {
                    // a card lifted off the list: soft shadow, solid so the rows under it don't show through,
                    // and the accent glow fading out once it's in place
                    Rectangle card = Rectangle.Inflate(r, -Theme.S(4), -Theme.S(3));
                    if (travelling)
                    for (int k = 4; k >= 1; k--)
                        using (System.Drawing.Drawing2D.GraphicsPath sp = Theme.Round(new RectangleF(card.X - k / 2f, card.Y - k / 2f + 2, card.Width + k, card.Height + k), Theme.S(8) + k / 2f))
                        using (SolidBrush sb = new SolidBrush(Color.FromArgb(Theme.Dark ? 22 : 12, 0, 0, 0))) g.FillPath(sb, sp);
                    Color bgc = Theme.Mix(Theme.Card, Theme.AccentColor, 0.16f * Math.Min(1, GlowOf(it) * 1.4f));
                    // solid while travelling; once in place only the fading glow is left
                    if (travelling) Theme.FillRound(g, card, Theme.S(8), lifted ? Theme.Mix(Theme.Card, Theme.Selected, 0.6f) : bgc);
                    else Theme.FillRound(g, card, Theme.S(8), Color.FromArgb((int)(255 * Math.Min(1, GlowOf(it) * 1.4f)), bgc));
                    if (GlowOf(it) > 0.02f) Theme.StrokeRound(g, new RectangleF(card.X + 0.5f, card.Y + 0.5f, card.Width - 1, card.Height - 1), Theme.S(8), Color.FromArgb((int)(180 * Math.Min(1, GlowOf(it))), Theme.AccentColor));
                }
                else if (i == hover) Theme.FillRound(g, Rectangle.Inflate(r, -Theme.S(4), -Theme.S(3)), Theme.S(8), Theme.Hover);
                // its place in the order, then its picture
                Rectangle num = Theme.Mirror(new Rectangle(r.X + Theme.S(10), r.Y, Theme.S(22), r.Height), Width);
                Theme.Text(g, (i + 1).ToString(CultureInfo.InvariantCulture), Theme.F(9.5f, FontStyle.Bold), Theme.InkFaint, num, Align.Center);
                Rectangle th = Theme.Mirror(new Rectangle(r.X + Theme.S(36), r.Y + Theme.S(7), Theme.S(ThumbW), r.Height - Theme.S(14)), Width);
                if (ShellThumbs)
                {
                    // a page or photo from Windows' thumbnails, or the file's kind while there's none
                    Theme.FillRound(g, th, Theme.S(6), Theme.Card);
                    if (!Thumbs.Draw(g, it.File, th, Theme.S(6), Repaint)) Theme.Glyph(g, it.Glyph ?? Ico.Doc, 10f, Theme.InkFaint, th);
                    Theme.StrokeRound(g, new RectangleF(th.X + 0.5f, th.Y + 0.5f, th.Width - 1, th.Height - 1), Theme.S(6), Theme.Line);
                }
                else
                using (GraphicsPath gp = Theme.Round(th, Theme.S(6)))
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(30, 30, 33))) g.FillPath(b, gp);
                    if (it.Thumb != null)
                    {
                        Region keep = g.Clip; g.SetClip(gp, CombineMode.Intersect);
                        float k = Math.Max(th.Width / (float)it.Thumb.Width, th.Height / (float)it.Thumb.Height);
                        float w = it.Thumb.Width * k, h = it.Thumb.Height * k;
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.DrawImage(it.Thumb, th.X + (th.Width - w) / 2, th.Y + (th.Height - h) / 2, w, h);
                        g.Clip = keep;
                    }
                    else Theme.Glyph(g, it.Info != null && !it.Info.HasVideo ? Ico.Music : Ico.Video, 10f, Theme.InkFaint, th);
                }
                int tx = Theme.S(48 + ThumbW), tw = Math.Max(Theme.S(40), Width - tx - Theme.S(150));
                Theme.Text(g, it.Name, nf, it.Bad ? Theme.Error : Theme.Ink, Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(9), tw, Theme.S(20)), Width), Align.Start, true, false, true);
                string sub = SubText != null ? SubText(it) : !it.Ready ? L.T("mg_reading") : it.Bad ? L.T("mg_bad") : Theme.Time(it.Info.Duration) + (it.Info.HasVideo ? "  ·  " + it.W + "×" + it.H : "") + "  ·  " + it.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Theme.Text(g, sub, sf, it.Bad ? Theme.Error : Theme.InkSoft, Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(29), tw, Theme.S(18)), Width), Align.Start, true, false, true);
                bool mine = it == PlayingItem;
                if (i == hover && !dragging)
                    for (int b = 0; b < 4; b++)
                    {
                        if (b == 0 && it.Bad) continue;
                        // up and down always sit in the same places; the one that can't be used (up on the first
                        // row, down on the last) is shown faint instead of leaving a gap
                        bool off = (b == 1 && i == 0) || (b == 2 && i == Items.Count - 1);
                        Rectangle br = Btn(i, b);
                        if (hoverBtn == b && !off) Theme.FillRound(g, br, Theme.S(7), Theme.Mix(Theme.Hover, Theme.InkFaint, 0.25f));
                        string gl = b == 0 && mine && IsPlaying ? Ico.Pause : Glyphs[b];
                        Color ink = b == 3 ? Theme.Error : b == 0 && mine ? Theme.AccentColor : Theme.InkSoft;
                        Theme.Glyph(g, gl, b == 0 ? 8.5f : 8f, off ? Color.FromArgb(Theme.Dark ? 60 : 70, ink) : ink, br);
                    }
                else if (mine && !dragging)
                    Theme.Glyph(g, IsPlaying ? Ico.Pause : Ico.Play, 8.5f, Theme.AccentColor, Btn(i, 0));
                if (mine && it.Info != null && it.Info.Duration > 0)
                {
                    // how far it has played
                    float f = (float)Math.Max(0, Math.Min(1, PlayingAt / it.Info.Duration));
                    Rectangle tr = new Rectangle(r.X + tx, r.Bottom - Theme.S(7), Math.Max(Theme.S(40), Width - tx - Theme.S(150)), Theme.S(3));
                    Theme.FillRound(g, Theme.Mirror(tr, Width), Theme.S(1.5f), Theme.Line);
                    if (f > 0) Theme.FillRound(g, Theme.Mirror(new Rectangle(tr.X, tr.Y, Math.Max(Theme.S(3), (int)(tr.Width * f)), tr.Height), Width), Theme.S(1.5f), Theme.AccentColor);
                }
            }
            // where a dragged row would go
            if (dragging && dragTo >= 0)
            {
                int y = dragTo * RowH - scroll;
                using (Pen p = new Pen(Theme.AccentColor, Theme.S(2.5f))) g.DrawLine(p, Theme.S(8), y, Width - Theme.S(8), y);
            }
            g.ResetClip();
            if (MaxScroll > 0)
            {
                float vh = Height, thh = Math.Max(Theme.S(24), vh * vh / ContentH), ty = (vh - thh) * scroll / MaxScroll;
                Theme.FillRound(g, Theme.Mirror(new Rectangle(Width - Theme.S(6), (int)ty + 2, Theme.S(3), (int)thh - 4), Width), Theme.S(1.5f), Color.FromArgb(120, Theme.InkFaint));
            }
        }

        int RowAt(int y) { int i = (y + scroll) / Math.Max(1, RowH); return i >= 0 && i < Items.Count ? i : -1; }
        int BtnAt(int i, Point p) { if (i < 0) return -1; for (int b = 0; b < 4; b++) if (Btn(i, b).Contains(p)) return b; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragFrom >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && Math.Abs(e.Y - downAt.Y) > Theme.S(5)) dragging = true;
                if (dragging)
                {
                    dragY = e.Y;
                    dragTo = Math.Max(0, Math.Min(Items.Count, (e.Y + scroll + RowH / 2) / Math.Max(1, RowH)));
                    // near the edges: scroll
                    if (e.Y < Theme.S(16)) scroll = Math.Max(0, scroll - Theme.S(8));
                    else if (e.Y > Height - Theme.S(16)) scroll = Math.Min(MaxScroll, scroll + Theme.S(8));
                    Invalidate();
                    return;
                }
            }
            int h = RowAt(e.Y), hb = BtnAt(h, e.Location);
            if (h != hover || hb != hoverBtn) { hover = h; hoverBtn = hb; Invalidate(); }
            bool offBtn = (hb == 1 && h == 0) || (hb == 2 && h == Items.Count - 1);
            Cursor = hb >= 0 && !offBtn ? Cursors.Hand : hb >= 0 ? Cursors.Default : h >= 0 && Items.Count > 1 ? Cursors.SizeNS : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!dragging) { hover = hoverBtn = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            int i = RowAt(e.Y), b = BtnAt(i, e.Location);
            if (i < 0) return;
            if (b >= 0)
            {
                if (b == 0 && !Items[i].Bad) { if (PlayAsked != null) PlayAsked(Items[i]); return; }
                if (b == 1 && i > 0) MoveRow(i, i - 1);
                else if (b == 2 && i < Items.Count - 1) MoveRow(i, i + 1);
                else if (b == 3) { Items.RemoveAt(i); Done(); }
                return;
            }
            dragFrom = i; downAt = e.Location; dragging = false; dragTo = -1;
            dragY = e.Y; grabDy = e.Y - (i * RowH - scroll);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && dragFrom >= 0 && dragTo >= 0)
            {
                int to = dragTo > dragFrom ? dragTo - 1 : dragTo;
                // it glides from where it was let go into its place
                shownY[Items[dragFrom]] = dragY - grabDy + scroll;
                if (to != dragFrom) MoveRow(dragFrom, to);
            }
            dragging = false; dragFrom = dragTo = -1;
            Invalidate();
        }

        void MoveRow(int from, int to)
        {
            MergeItem it = Items[from];
            Items.RemoveAt(from);
            Items.Insert(Math.Max(0, Math.Min(Items.Count, to)), it);
            hover = to;
            Done();
        }

        void Done() { scroll = Math.Min(scroll, MaxScroll); Invalidate(); if (Changed != null) Changed(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * RowH));
            Invalidate();
        }

        protected override string AccName { get { return L.T("mg_order"); } }
    }

    /// The Donut Wheel's "Merge into one": several videos (or songs) joined in the order you choose, with the name you
    /// give it. Fades between them, a shape when they don't match, keep or mute the sound, and a quick join
    /// (no re-encoding, no quality lost) when the files allow it.
    public class MergeWindow : MiniWindow
    {
        readonly List<MergeItem> original = new List<MergeItem>();
        readonly MergeList list = new MergeList();
        readonly Segments order, trans, shape;
        readonly TextField name;
        readonly Toggle sound = new Toggle(), quick = new Toggle();
        readonly Progress bar = new Progress { Visible = false };
        readonly Btn bGo, bCancel, bShow, bPreview, bReverse;
        readonly Control activitySource;
        readonly bool audio;
        bool busy, cancel, done;
        string result, status; Color statusColor;
        Rectangle orderR, nameR, extR, transR, shapeR, soundR, quickR, summaryR, statusR;
        // songs play inside this window: one row, or all of them in order (Preview); Space pauses
        readonly ClipPlayer player = new ClipPlayer();
        MergeItem cur; double curPos; bool chain;

        public static void Open(string[] files, Point at, Control activitySource)
        {
            MergeWindow w = new MergeWindow(files, activitySource);
            w.ShowNear(at);
            w.Load2();
        }

        public static bool AllVideo(string[] files) { return All(files, Category.Video); }
        public static bool AllAudio(string[] files) { return All(files, Category.Audio); }
        static bool All(string[] files, Category c)
        {
            if (files == null || files.Length < 2) return false;
            foreach (string f in files) if (Directory.Exists(f) || Formats.CategoryOf(Formats.Ext(f)) != c) return false;
            return true;
        }

        MergeWindow(string[] files, Control activitySource) : base(() => L.T(AllAudio(files) ? "mg_title_audio" : "mg_title"), Ico.Combine)
        {
            this.activitySource = activitySource;
            audio = AllAudio(files);
            foreach (string f in files) { MergeItem it = new MergeItem { File = f }; try { it.When = File.GetLastWriteTime(f); } catch { } original.Add(it); list.Items.Add(it); }
            list.Changed += () => { CheckQuick(); Invalidate(); };
            list.PlayAsked += it =>
            {
                if (!audio) { try { Media.Play(it.File, 0, 0, it.Info != null && it.Info.HasVideo); } catch { } return; }
                if (it == cur && player.Playing) Pause();
                else if (it == cur) PlayItem(it, curPos, chain);
                else PlayItem(it, 0, false);
            };
            list.Changed += () => { if (cur != null && !list.Items.Contains(cur)) StopPlaying(); };
            player.Tick += t => Ui(() => { if (!player.Playing) return; curPos = t; list.PlayingAt = t; list.Invalidate(); });
            player.Finished += () => Ui(() =>
            {
                // Preview goes on to the next song; a single row stops at its end
                MergeItem next = null;
                if (chain && cur != null) { List<MergeItem> l = Good; int i = l.IndexOf(cur); if (i >= 0 && i + 1 < l.Count) next = l[i + 1]; }
                if (next != null) PlayItem(next, 0, true); else StopPlaying();
            });
            order = new Segments(() => L.T("mg_as_dragged"), () => L.T("mg_by_name"), () => L.T("mg_by_date")) { FontSize = 8.75f, Surface = Surface.Card };
            order.SelectedChanged += (s, e) => Sort();
            bReverse = new Btn(() => "", "\uE8CB", BtnKind.Ghost) { Surface = Surface.Card, GlyphSize = 10f, AccessibleName = L.T("mg_reverse") };
            bReverse.Click += (s, e) => { list.Items.Reverse(); list.Invalidate(); CheckQuick(); Invalidate(); };
            new ToolTip().SetToolTip(bReverse, L.T("mg_reverse"));
            name = new TextField(() => L.T("mg_name_hint"), null) { Surface = Surface.Card };
            name.Box.Text = SuggestName(files);
            name.Box.TextChanged += (s, e) => { if (done) Reset(); };
            trans = new Segments(() => L.T("mg_t_none"), () => L.T("mg_t_fade"), () => L.T("mg_t_black")) { FontSize = 8.75f, Surface = Surface.Card };
            int ti; int.TryParse(Settings.Get("MergeTrans", "0"), out ti); trans.SetSilently(Math.Max(0, Math.Min(2, ti)));
            trans.SelectedChanged += (s, e) => { Settings.Set("MergeTrans", trans.SelectedIndex.ToString(CultureInfo.InvariantCulture)); CheckQuick(); Invalidate(); };
            shape = new Segments(() => L.T("mg_s_first"), () => "16:9", () => "9:16", () => "1:1") { FontSize = 8.75f, Surface = Surface.Card };
            shape.SelectedChanged += (s, e) => { CheckQuick(); Invalidate(); };
            sound.SetSilently(true);
            sound.CheckedChanged += (s, e) => { CheckQuick(); Invalidate(); };
            quick.CheckedChanged += (s, e) => Invalidate();
            bPreview = new Btn(() => L.T(audio && player.Playing && chain ? "tr_pause" : "mg_preview"), Ico.Play, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f, Enabled = false };
            bPreview.Click += (s, e) => Preview();
            bGo = new Btn(() => L.T(done ? "wn_ok" : audio ? "mg_go_audio" : "mg_go"), Ico.Combine, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bGo.Click += (s, e) => { if (done) Dismiss(); else Go(); };
            bCancel = new Btn(() => L.T(busy ? "cancel" : "close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { if (busy) cancel = true; else Dismiss(); };
            bShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, Visible = false, FontSize = 9f };
            bShow.Click += (s, e) => { if (result != null) ShowInFolder(result); };
            Controls.AddRange(new Control[] { list, order, bReverse, name, trans, shape, sound, quick, bar, bPreview, bGo, bCancel, bShow });
            if (audio) shape.Visible = false;
            // the list starts with the focus (not the name box), so Space plays the songs straight away
            Shown += (s0, e0) => { try { list.Focus(); } catch { } };
            int rows = Math.Max(2, Math.Min(5, files.Length));
            ClientSize = new Size(Theme.S(640), Theme.S(audio ? 470 : 520) + rows * list.RowH);
        }

        static string SuggestName(string[] files)
        {
            string first = Path.GetFileNameWithoutExtension(files[0]);
            return L.T("mg_default_name", first.Length > 40 ? first.Substring(0, 40) : first);
        }

        /// Reads each file (length, size, a picture of it) in the background.
        void Load2()
        {
            List<MergeItem> todo = new List<MergeItem>(original);
            Thread th = new Thread(() =>
            {
                foreach (MergeItem it in todo)
                {
                    try
                    {
                        MediaInfo mi = Media.Probe(it.File);
                        if (mi == null || mi.Duration <= 0) throw new Exception();
                        int w = mi.Width, h = mi.Height;
                        Image thumb = null;
                        if (mi.HasVideo)
                        {
                            thumb = Media.Frame(it.File, Math.Min(1, mi.Duration / 10), Theme.S(90));
                            // phone videos filmed upright: the picture's shape tells which way round they are
                            if (thumb != null && w > 0 && h > 0) { float ar = thumb.Width / (float)thumb.Height; if (Math.Abs(ar - h / (float)w) < Math.Abs(ar - w / (float)h)) { int t = w; w = h; h = t; } }
                        }
                        Facts f = null; try { f = FileFacts.Read(it.File); } catch { }
                        Ui(() =>
                        {
                            it.Info = mi; it.Thumb = thumb; it.W = Math.Max(2, w + w % 2); it.H = Math.Max(2, h + h % 2);
                            if (f != null && f.Taken != DateTime.MinValue) it.When = f.Taken;
                            it.Ready = true;
                            AfterLoad();
                        });
                    }
                    catch { Ui(() => { it.Ready = true; it.Bad = true; AfterLoad(); }); }
                }
            }) { IsBackground = true };
            th.Start();
        }

        void AfterLoad()
        {
            if (order.SelectedIndex == 2) Sort();
            int good = 0; bool all = true;
            foreach (MergeItem it in list.Items) { if (!it.Ready) all = false; else if (!it.Bad) good++; }
            bGo.Enabled = bPreview.Enabled = all && good >= 2 && !busy;
            CheckQuick();
            list.Invalidate(); Invalidate();
        }

        void Sort()
        {
            List<MergeItem> l = new List<MergeItem>(original);
            l.RemoveAll(x => !list.Items.Contains(x));
            if (order.SelectedIndex == 1) l.Sort((a, b) => RenamePage.NaturalCompare(a.Name, b.Name));
            else if (order.SelectedIndex == 2) l.Sort((a, b) => a.When.CompareTo(b.When));
            list.Items = l;
            list.Invalidate();
            CheckQuick(); Invalidate();
        }

        List<MergeItem> Good { get { List<MergeItem> l = new List<MergeItem>(); foreach (MergeItem it in list.Items) if (it.Ready && !it.Bad) l.Add(it); return l; } }

        /// Whether the files can simply be put one after another (same kind of video and sound, nothing to redo).
        bool CanQuick()
        {
            List<MergeItem> l = Good;
            if (l.Count < 2 || trans.SelectedIndex != 0 || (!audio && shape.SelectedIndex != 0)) return false;
            MediaInfo a = l[0].Info;
            string ext = Formats.Ext(l[0].File);
            foreach (MergeItem it in l)
            {
                MediaInfo b = it.Info;
                string e = Formats.Ext(it.File);
                if (audio)
                {
                    if (e != ext || b.ACodec != a.ACodec || b.SampleRate != a.SampleRate || b.Channels != a.Channels) return false;
                    continue;
                }
                if (!(e == "mp4" || e == "m4v" || e == "mov")) return false;
                if (b.VCodec != a.VCodec || !(b.VCodec == "h264" || b.VCodec == "hevc") || it.W != l[0].W || it.H != l[0].H || Math.Abs(b.Fps - a.Fps) > 0.02) return false;
                if (b.HasAudio != a.HasAudio || (b.HasAudio && (b.ACodec != a.ACodec || b.SampleRate != a.SampleRate || b.Channels != a.Channels))) return false;
            }
            return true;
        }

        bool quickPossible;
        void CheckQuick()
        {
            bool can = CanQuick();
            if (can != quickPossible) { quickPossible = can; quick.SetSilently(can); }
            quick.Enabled = can && !busy;
            quick.Invalidate();
        }

        double Total
        {
            get
            {
                double t = 0; List<MergeItem> l = Good;
                foreach (MergeItem it in l) t += it.Info.Duration;
                if (trans.SelectedIndex != 0 && !quick.Checked) t -= Math.Max(0, l.Count - 1) * 0.6;
                return Math.Max(0, t);
            }
        }

        string Summary()
        {
            List<MergeItem> l = Good;
            if (l.Count == 0) return L.T("mg_reading");
            long bytes = 0;
            if (quick.Checked && quickPossible) { foreach (MergeItem it in l) try { bytes += new FileInfo(it.File).Length; } catch { } }
            else if (audio) bytes = (long)(Total * 192000 / 8);
            else
            {
                int w, h; OutSize(out w, out h);
                double fps = OutFps;
                bytes = (long)((w * (double)h * fps * 0.09 * Total + (sound.Checked ? 192000 * Total : 0)) / 8);
            }
            return L.T(audio ? "mg_summary_audio" : "mg_summary", l.Count, Theme.Time(Total), Theme.HumanSize(bytes));
        }

        double OutFps { get { List<MergeItem> l = Good; double f = l.Count > 0 && l[0].Info.Fps > 1 ? l[0].Info.Fps : 30; return Math.Max(24, Math.Min(60, Math.Round(f, 3))); } }

        VProject Build()
        {
            List<MergeItem> l = Good;
            VProject p = new VProject();
            p.Fill = "blur";
            string tr = trans.SelectedIndex == 1 ? "fade" : trans.SelectedIndex == 2 ? "fadeblack" : "none";
            foreach (MergeItem it in l)
            {
                MediaSource src = new MediaSource { File = it.File, Info = it.Info, W = it.W, H = it.H };
                VClip c = new VClip { Kind = audio ? VKind.Audio : VKind.Video, Src = src, In = 0, Out = it.Info.Duration, Trans = tr, TransDur = 0.6, Muted = !sound.Checked };
                p.Main.Clips.Add(c);
            }
            p.Aspect = audio ? "orig" : new[] { "orig", "16:9", "9:16", "1:1" }[shape.SelectedIndex];
            if (l.Count > 0) p.SizeFor(l[0].W, l[0].H);
            p.Fps = OutFps;
            p.Layout();
            return p;
        }

        void OutSize(out int w, out int h) { VProject p = Build(); w = p.W; h = p.H; }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (list == null || bGo == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad, cw = W - p * 2;
            int y = Head + Theme.S(2);
            orderR = new Rectangle(p, y, Theme.S(120), Theme.S(32));
            int ow = Math.Min(Theme.S(330), cw - Theme.S(170));
            order.Bounds = M(new Rectangle(W - p - Theme.S(38) - ow, y, ow, Theme.S(32)));
            bReverse.Bounds = M(new Rectangle(W - p - Theme.S(32), y, Theme.S(32), Theme.S(32)));
            y += Theme.S(40);
            int bottomBlock = (audio ? Theme.S(306) : Theme.S(356)) + Theme.S(40);
            int lh = Math.Max(list.RowH * 2, H - y - bottomBlock);
            list.Bounds = M(new Rectangle(p, y, cw, lh));
            y += lh + Theme.S(14);
            nameR = new Rectangle(p, y, Theme.S(120), Theme.S(34));
            extR = new Rectangle(W - p - Theme.S(56), y, Theme.S(56), Theme.S(34));
            name.Bounds = M(new Rectangle(p + Theme.S(124), y, cw - Theme.S(124) - Theme.S(60), Theme.S(34)));
            y += Theme.S(42);
            transR = new Rectangle(p, y, Theme.S(120), Theme.S(34));
            trans.Bounds = M(new Rectangle(p + Theme.S(124), y, cw - Theme.S(124), Theme.S(34)));
            y += Theme.S(42);
            if (!audio)
            {
                shapeR = new Rectangle(p, y, Theme.S(120), Theme.S(34));
                shape.Bounds = M(new Rectangle(p + Theme.S(124), y, cw - Theme.S(124), Theme.S(34)));
                y += Theme.S(42);
            }
            soundR = new Rectangle(p, y, cw - Theme.S(60), Theme.S(32));
            sound.Bounds = M(new Rectangle(W - p - Theme.S(52), y, Theme.S(52), Theme.S(32)));
            if (audio) { sound.Visible = false; soundR = Rectangle.Empty; } else y += Theme.S(38);
            quickR = new Rectangle(p, y, cw - Theme.S(60), Theme.S(44));
            quick.Bounds = M(new Rectangle(W - p - Theme.S(52), y + Theme.S(5), Theme.S(52), Theme.S(32)));
            y += Theme.S(50);
            summaryR = new Rectangle(p, y, cw, Theme.S(24));
            y += Theme.S(28);
            bar.Bounds = M(new Rectangle(p, y + Theme.S(2), cw, Theme.S(6)));
            statusR = new Rectangle(p, y + Theme.S(10), cw, Theme.S(30));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(130), bGo.PreferredWidth + Theme.S(12)), xw = Math.Max(Theme.S(96), bCancel.PreferredWidth), pw = Math.Max(Theme.S(110), bPreview.PreferredWidth + Theme.S(8));
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - xw, by, xw, Theme.S(40)));
            bPreview.Bounds = M(new Rectangle(p, by, pw, Theme.S(40)));
            bShow.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(110), bShow.PreferredWidth), Theme.S(34)));
        }

        protected override void PaintBody(Graphics g)
        {
            Font lf = Theme.F(9f, FontStyle.Bold);
            Theme.Text(g, L.T("mg_order"), lf, Theme.Ink, M(orderR), Align.Start);
            Theme.Text(g, L.T("mg_name"), lf, Theme.Ink, M(nameR), Align.Start);
            Theme.Text(g, audio ? (quick.Checked && quickPossible && list.Items.Count > 0 ? "." + Formats.Ext(list.Items[0].File) : ".mp3") : ".mp4", Theme.F(9.5f), Theme.InkSoft, M(extR), Align.Start, true, false, true);
            Theme.Text(g, L.T("mg_between"), lf, Theme.Ink, M(transR), Align.Start);
            if (!audio)
            {
                Theme.Text(g, L.T("mg_shape"), lf, Theme.Ink, M(shapeR), Align.Start);
                Theme.Text(g, L.T("mg_sound"), Theme.F(9.25f), Theme.Ink, M(soundR), Align.Start);
            }
            Theme.Text(g, L.T(quickPossible ? "mg_quick" : "mg_quick_no"), Theme.F(8.75f), quickPossible ? Theme.Ink : Theme.InkFaint, M(quickR), Align.Start, true, true);
            Theme.Text(g, Summary(), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, M(summaryR), Align.Start, true, false, true);
            if (status != null) Theme.Text(g, status, Theme.F(9f, done ? FontStyle.Bold : FontStyle.Regular), statusColor, M(statusR), Align.Start, true, true);
        }

        protected override bool CanClose() { if (busy) { cancel = true; return false; } return true; }

        void Reset()
        {
            done = false; result = null; status = null;
            bShow.Visible = false; bCancel.Visible = true; bGo.Glyph = Ico.Combine;
            bGo.Invalidate(); DoLayout(); Invalidate();
        }

        void PlayItem(MergeItem it, double from, bool all)
        {
            if (it == null || it.Bad || it.Info == null || busy) return;
            if (from >= it.Info.Duration - 0.05) from = 0;
            cur = it; curPos = from; chain = all;
            try { player.Play(it.File, from, it.Info.Duration, true, false, 0, 0); } catch { }
            PlayState();
        }
        void Pause() { player.Stop(); PlayState(); }
        void StopPlaying() { player.Stop(); cur = null; curPos = 0; chain = false; PlayState(); }
        void PlayState()
        {
            list.PlayingItem = cur; list.PlayingAt = curPos; list.IsPlaying = player.Playing;
            bPreview.Glyph = player.Playing && chain ? Ico.Pause : Ico.Play;
            list.Invalidate(); bPreview.Invalidate(); DoLayout();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            // Space plays or pauses the songs (not while typing the name)
            if (audio && k == Keys.Space && !busy && !name.Box.Focused)
            {
                if (player.Playing) Pause();
                else if (cur != null) PlayItem(cur, curPos, chain);
                else Preview();
                return true;
            }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { try { player.Dispose(); } catch { } base.OnFormClosed(e); }

        /// Plays the clips one after another in the chosen order (no fades: just to check the order).
        void Preview()
        {
            List<MergeItem> l = Good;
            if (l.Count == 0) return;
            if (audio)
            {
                // songs: right here, one after another; pressed again it pauses
                if (player.Playing && chain) Pause();
                else if (cur != null && chain) PlayItem(cur, curPos, true);
                else PlayItem(l[0], 0, true);
                return;
            }
            string ff = Tools.FFplay;
            if (ff == null) { try { Media.Play(l[0].File, 0, 0, !audio); } catch { } return; }
            try
            {
                string listFile = Path.Combine(Path.GetTempPath(), "fadi_merge_preview_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                File.WriteAllText(listFile, ConcatList(l), new UTF8Encoding(false));
                Media.StopPreview();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, "-hide_banner -loglevel error -autoexit -window_title " + Tools.Quote(L.T("preview_title")) + (audio ? " -showmode 1 -x 720 -y 240" : " -x 960 -y 540") + " -f concat -safe 0 -i " + Tools.Quote(listFile)) { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex) { status = ex.Message; statusColor = Theme.Error; Invalidate(); }
        }

        static string ConcatList(List<MergeItem> l)
        {
            StringBuilder b = new StringBuilder("ffconcat version 1.0\n");
            foreach (MergeItem it in l) b.Append("file '").Append(it.File.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
            return b.ToString();
        }

        void Go()
        {
            if (busy || done) return;
            List<MergeItem> l = Good;
            if (l.Count < 2) return;
            if (audio) StopPlaying();
            string nm = name.Box.Text.Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) nm = nm.Replace(ch, '_');
            nm = nm.Trim().TrimEnd('.');
            if (nm.Length == 0) nm = Path.GetFileNameWithoutExtension(SuggestName(new[] { l[0].File }));
            bool q = quick.Checked && quickPossible;
            string ext = audio ? (q ? Formats.Ext(l[0].File) : "mp3") : "mp4";
            string dir = DragWheel.OutDirFor(l[0].File);
            string outp = Paths.UniquePath(dir, nm, ext);
            busy = true; cancel = false; done = false;
            bGo.Enabled = bPreview.Enabled = false; list.Enabled = order.Enabled = trans.Enabled = shape.Enabled = sound.Enabled = quick.Enabled = name.Enabled = bReverse.Enabled = false;
            bCancel.Invalidate();
            bar.Visible = true; bar.Value = 0;
            status = L.T("mg_working"); statusColor = Theme.InkSoft; Invalidate();
            VProject proj = q ? null : Build();
            ActJob act = Activity.Begin(activitySource, L.T(audio ? "mg_title_audio" : "mg_title"), Path.GetFileName(outp), () => cancel = true);
            double total = Total;
            Thread th = new Thread(() =>
            {
                string err = null, listFile = null;
                Action<double> prog = f => { Activity.Update(act, f, (int)(f * 100) + "%"); Ui(() => bar.Value = f); };
                try
                {
                    if (q)
                    {
                        // the files one after another as they are: quick, and nothing is lost
                        listFile = Path.Combine(Path.GetTempPath(), "fadi_merge_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                        File.WriteAllText(listFile, ConcatList(l), new UTF8Encoding(false));
                        Job job = new Job { Output = outp };
                        job.Steps.Add(new JobStep { Exe = Tools.FFmpeg, Ffmpeg = true, Duration = total,
                            Args = "-hide_banner -y -f concat -safe 0 -i " + Tools.Quote(listFile) + " -map 0:v? -map 0:a? -c copy" + (audio ? "" : (sound.Checked ? "" : " -an") + " -movflags +faststart") + " " + Tools.Quote(outp) });
                        err = Engine.Run(job, prog, () => cancel);
                    }
                    else
                    {
                        RenderOptions o = new RenderOptions { W = proj.W, H = proj.H, Fps = proj.Fps, Crf = 20, Gpu = !audio, Format = audio ? "mp3" : "mp4" };
                        Job job = VideoRender.Build(proj, outp, o);
                        err = Engine.Run(job, prog, () => cancel);
                        if (err != null && !cancel && o.Gpu)
                        {
                            // the graphics card's encoder failed: once more on the processor
                            HwEnc.Disable(); o.Gpu = false;
                            job = VideoRender.Build(proj, outp, o);
                            err = Engine.Run(job, prog, () => cancel);
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                if (listFile != null) try { File.Delete(listFile); } catch { }
                if (cancel) try { if (File.Exists(outp)) File.Delete(outp); } catch { }
                if (err == null && !cancel && !File.Exists(outp)) err = L.T("mg_failed");
                Activity.End(act, cancel ? null : err, cancel, err == null ? L.T("mg_done", l.Count) : null, err == null ? outp : null);
                Ui(() =>
                {
                    busy = false; bar.Visible = false;
                    list.Enabled = order.Enabled = trans.Enabled = shape.Enabled = sound.Enabled = name.Enabled = bReverse.Enabled = true;
                    CheckQuick();
                    bPreview.Enabled = true;
                    if (cancel) { status = L.T("cancelled"); statusColor = Theme.InkSoft; bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); return; }
                    if (err != null) { status = err; statusColor = Theme.Error; bGo.Enabled = true; Invalidate(); bCancel.Invalidate(); return; }
                    done = true; result = outp;
                    long sz = 0; try { sz = new FileInfo(outp).Length; } catch { }
                    status = L.T("up_saved", Path.GetFileName(outp), Theme.HumanSize(sz)); statusColor = Theme.Success;
                    bGo.Enabled = true; bGo.Glyph = Ico.Check; bCancel.Visible = false; bShow.Visible = true; bPreview.Visible = false;
                    DoLayout(); Invalidate(); bGo.Invalidate();
                    if (Settings.OpenAfter) ShowInFolder(outp);
                });
            }) { IsBackground = true };
            th.Start();
        }
    }
}
