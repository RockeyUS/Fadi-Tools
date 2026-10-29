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
        public string Name { get { return Path.GetFileName(File); } }
    }

    /// The list of clips in the merge window: their order (drag a row, or the arrows), a play button and a remove button
    /// on each.
    public class MergeList : UiControl
    {
        public List<MergeItem> Items = new List<MergeItem>();
        public event Action Changed;
        public event Action<MergeItem> PlayAsked;
        int scroll, hover = -1, hoverBtn = -1, dragFrom = -1, dragTo = -1;
        Point downAt; bool dragging;

        public MergeList() { Surface = Surface.Card; DoubleBuffered = true; Cursor = Cursors.Default; }

        public int RowH { get { return Theme.S(58); } }
        int ContentH { get { return Items.Count * RowH; } }
        int MaxScroll { get { return Math.Max(0, ContentH - Height); } }
        Rectangle Row(int i) { return new Rectangle(0, i * RowH - scroll, Width, RowH); }
        /// The buttons at the end of a row: 0 play, 1 up, 2 down, 3 remove.
        Rectangle Btn(int i, int b) { Rectangle r = Row(i); int s = Theme.S(30), gap = Theme.S(2), right = r.Right - Theme.S(8); return Theme.Mirror(new Rectangle(right - (4 - b) * (s + gap), r.Y + (r.Height - s) / 2, s, s), Width); }
        static readonly string[] Glyphs = { Ico.Play, "\uE70E", "\uE70D", Ico.Close };

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Theme.FillRound(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(10), Theme.CardInner);
            g.SetClip(new Rectangle(1, 1, Width - 2, Height - 2));
            Font nf = Theme.F(9.25f, FontStyle.Bold), sf = Theme.F(8.25f);
            for (int i = 0; i < Items.Count; i++)
            {
                Rectangle r = Row(i);
                if (r.Bottom < 0 || r.Y > Height) continue;
                MergeItem it = Items[i];
                bool lifted = dragging && i == dragFrom;
                if (i == hover || lifted) Theme.FillRound(g, Rectangle.Inflate(r, -Theme.S(4), -Theme.S(3)), Theme.S(8), lifted ? Theme.Selected : Theme.Hover);
                // its place in the order, then its picture
                Rectangle num = Theme.Mirror(new Rectangle(r.X + Theme.S(10), r.Y, Theme.S(22), r.Height), Width);
                Theme.Text(g, (i + 1).ToString(CultureInfo.InvariantCulture), Theme.F(9.5f, FontStyle.Bold), Theme.InkFaint, num, Align.Center);
                Rectangle th = Theme.Mirror(new Rectangle(r.X + Theme.S(36), r.Y + Theme.S(7), Theme.S(78), r.Height - Theme.S(14)), Width);
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
                int tx = Theme.S(126), tw = Math.Max(Theme.S(40), Width - tx - Theme.S(150));
                Theme.Text(g, it.Name, nf, it.Bad ? Theme.Error : Theme.Ink, Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(9), tw, Theme.S(20)), Width), Align.Start, true, false, true);
                string sub = !it.Ready ? L.T("mg_reading") : it.Bad ? L.T("mg_bad") : Theme.Time(it.Info.Duration) + (it.Info.HasVideo ? "  ·  " + it.W + "×" + it.H : "") + "  ·  " + it.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Theme.Text(g, sub, sf, it.Bad ? Theme.Error : Theme.InkSoft, Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(29), tw, Theme.S(18)), Width), Align.Start, true, false, true);
                if (i == hover && !dragging)
                    for (int b = 0; b < 4; b++)
                    {
                        if ((b == 1 && i == 0) || (b == 2 && i == Items.Count - 1) || (b == 0 && it.Bad)) continue;
                        Rectangle br = Btn(i, b);
                        if (hoverBtn == b) Theme.FillRound(g, br, Theme.S(7), Theme.Mix(Theme.Hover, Theme.InkFaint, 0.25f));
                        Theme.Glyph(g, Glyphs[b], b == 0 ? 8.5f : 8f, b == 3 ? Theme.Error : Theme.InkSoft, br);
                    }
                if (i < Items.Count - 1) using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, Theme.S(12), r.Bottom - 1, Width - Theme.S(12), r.Bottom - 1);
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
            Cursor = hb >= 0 ? Cursors.Hand : h >= 0 && Items.Count > 1 ? Cursors.SizeNS : Cursors.Default;
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
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && dragFrom >= 0 && dragTo >= 0)
            {
                int to = dragTo > dragFrom ? dragTo - 1 : dragTo;
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
            list.PlayAsked += it => { try { Media.Play(it.File, 0, 0, it.Info != null && it.Info.HasVideo); } catch { } };
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
            bPreview = new Btn(() => L.T("mg_preview"), Ico.Play, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f, Enabled = false };
            bPreview.Click += (s, e) => Preview();
            bGo = new Btn(() => L.T(done ? "wn_ok" : audio ? "mg_go_audio" : "mg_go"), Ico.Combine, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bGo.Click += (s, e) => { if (done) Dismiss(); else Go(); };
            bCancel = new Btn(() => L.T(busy ? "cancel" : "close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { if (busy) cancel = true; else Dismiss(); };
            bShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, Visible = false, FontSize = 9f };
            bShow.Click += (s, e) => { if (result != null) ShowInFolder(result); };
            Controls.AddRange(new Control[] { list, order, bReverse, name, trans, shape, sound, quick, bar, bPreview, bGo, bCancel, bShow });
            if (audio) shape.Visible = false;
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

        /// Plays the clips one after another in the chosen order (no fades: just to check the order).
        void Preview()
        {
            List<MergeItem> l = Good;
            if (l.Count == 0) return;
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
