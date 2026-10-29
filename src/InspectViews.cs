using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace FileConverter
{
    static class Levels
    {
        public static Color Of(int level) { return level >= 2 ? Theme.Error : level == 1 ? Theme.Warn : Theme.Success; }
        public static string Glyph(int level) { return level >= 2 ? Ico.Warning : level == 1 ? Ico.Info : Ico.Check; }
        public static string Verdict(int level) { return L.T(level >= 2 ? "sf_v_danger" : level == 1 ? "sf_v_careful" : "sf_v_ok"); }
    }

    /// The verdict in the file's card ("Looks safe", "Be careful", "Dangerous"): click it for the reasons.
    public class SafetyPill : UiControl
    {
        public int Level = -1;
        public SafetyPill() { Cursor = Cursors.Hand; Surface = Surface.Card; }
        public int PreferredW { get { return Level < 0 ? Theme.S(120) : Theme.Measure(Levels.Verdict(Level), Theme.F(9f, FontStyle.Bold)) + Theme.S(44); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(1, 1, Width - 2, Height - 2);
            if (Level < 0)
            {
                Theme.FillRound(g, r, r.Height / 2, Theme.CardInner);
                Theme.Text(g, L.T("sf_checking"), Theme.F(8.75f), Theme.InkFaint, Rectangle.Round(r), Align.Center);
                return;
            }
            Color c = Levels.Of(Level);
            Theme.FillRound(g, r, r.Height / 2, Theme.Mix(Theme.Card, c, hovering ? 0.24f : 0.16f));
            Rectangle ic = Theme.Mirror(new Rectangle(Theme.S(8), 0, Theme.S(20), Height), Width);
            Theme.Glyph(g, Levels.Glyph(Level), 8.5f, c, ic);
            Theme.Text(g, Levels.Verdict(Level), Theme.F(9f, FontStyle.Bold), c, Theme.Mirror(new Rectangle(Theme.S(30), 0, Width - Theme.S(38), Height), Width), Align.Start);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
        protected override string AccName { get { return Level < 0 ? L.T("sf_checking") : Levels.Verdict(Level); } }
        protected override AccessibleRole AccRole { get { return AccessibleRole.PushButton; } }
    }

    /// The safety check: a big verdict, then each finding with its reason.
    public class SafetyView : ScrollList
    {
        public SafetyReport Report;
        public SafetyView() { RowHeight = 64; }
        public int TopH { get { return Theme.S(92); } }
        public override int Count { get { return Report == null ? 0 : Report.Findings.Count + 1; } }

        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            Theme.Smooth(g);
            if (i == 0)
            {
                // the verdict
                Color c = Levels.Of(Report.Level);
                Rectangle box = new Rectangle(r.X + Theme.S(14), r.Y + Theme.S(6), r.Width - Theme.S(28), r.Height - Theme.S(12));
                Theme.FillRound(g, box, Theme.S(12), Theme.Mix(Theme.Card, c, 0.12f));
                Rectangle ic = Theme.Mirror(new Rectangle(box.X + Theme.S(14), box.Y + (box.Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30)), Width);
                Theme.FillRound(g, ic, Theme.S(15), c);
                Theme.Glyph(g, Levels.Glyph(Report.Level), 9f, Color.White, ic);
                Rectangle t = new Rectangle(box.X + Theme.S(56), box.Y + Theme.S(4), box.Width - Theme.S(70), Theme.S(24));
                Theme.Text(g, Levels.Verdict(Report.Level), Theme.F(11f, FontStyle.Bold), Theme.Ink, Theme.Mirror(t, Width), Align.Start);
                string sub = L.T(Report.Level >= 2 ? "sf_v_danger_d" : Report.Level == 1 ? "sf_v_careful_d" : "sf_v_ok_d");
                Theme.Text(g, sub, Theme.F(8.75f), Theme.InkSoft, Theme.Mirror(new Rectangle(t.X, t.Bottom, t.Width, box.Height - Theme.S(32)), Width), Align.Start, false, true);
                return;
            }
            Finding f = Report.Findings[i - 1];
            Color fc = Levels.Of(f.Level);
            Rectangle dot = Theme.Mirror(new Rectangle(r.X + Theme.S(24), r.Y + Theme.S(12), Theme.S(22), Theme.S(22)), Width);
            Theme.FillRound(g, dot, Theme.S(11), Theme.Mix(Theme.Card, fc, 0.18f));
            Theme.Glyph(g, Levels.Glyph(f.Level), 7.5f, fc, dot);
            int tx = Theme.S(58), tw = r.Width - tx - Theme.S(20);
            Theme.Text(g, f.Title, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(8), tw, Theme.S(22)), Width), Align.Start);
            Theme.Text(g, f.Detail, Theme.F(8.75f), Theme.InkSoft, Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(30), tw, r.Height - Theme.S(32)), Width), Align.Start, false, true);
            using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, r.X + Theme.S(16), r.Bottom - 1, r.Right - Theme.S(16), r.Bottom - 1);
        }
        protected override string RowText(int i) { if (Report == null) return ""; if (i == 0) return Levels.Verdict(Report.Level); Finding f = Report.Findings[i - 1]; return f.Title + ". " + f.Detail; }
    }

    /// What's inside an archive: each file with its size; buttons to look at it or save it.
    public class EntriesView : ScrollList
    {
        public List<ArchiveEntry> Items = new List<ArchiveEntry>();
        public event Action<ArchiveEntry> Open, Save;
        int hoverBtn = -1;
        public EntriesView() { RowHeight = 40; }
        public override int Count { get { return Items.Count; } }
        Rectangle Btn(Rectangle r, int b) { int s = Theme.S(30); Rectangle x = new Rectangle(r.Right - Theme.S(14) - (2 - b) * (s + Theme.S(4)), r.Y + (r.Height - s) / 2, s, s); return Theme.RTL ? new Rectangle(r.X + (r.Right - x.Right), x.Y, s, s) : x; }
        protected override void PaintEmpty(Graphics g) { Theme.Text(g, L.T("in_empty"), Theme.F(9.5f), Theme.InkFaint, ClientRectangle, Align.Center); }
        protected override void RowHover(int i, Point p, Rectangle r)
        {
            int h = -1; if (!Items[i].IsDir) for (int b = 0; b < 2; b++) if (Btn(r, b).Contains(p)) h = i * 2 + b;
            if (h != hoverBtn) { hoverBtn = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void RowClick(int i, Point p, Rectangle r)
        {
            if (Items[i].IsDir) return;
            if (Btn(r, 0).Contains(p) && Open != null) Open(Items[i]);
            else if (Btn(r, 1).Contains(p) && Save != null) Save(Items[i]);
        }
        protected override void RowDoubleClick(int i) { if (!Items[i].IsDir && Open != null) Open(Items[i]); }
        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            ArchiveEntry e = Items[i];
            if (hover) using (SolidBrush hb = new SolidBrush(Theme.Hover)) g.FillRectangle(hb, r);
            Theme.Smooth(g);
            int depth = 0; foreach (char c in e.Path.TrimEnd('/')) if (c == '/') depth++;
            depth = Math.Min(depth, 8);
            int x = Theme.S(16) + depth * Theme.S(16);
            string name = Path.GetFileName(e.Path.TrimEnd('/'));
            string ext = Formats.Ext(name);
            bool risky = !e.IsDir && Safety.RunsCode(ext);
            Rectangle ic = Theme.Mirror(new Rectangle(r.X + x, r.Y, Theme.S(22), r.Height), Width);
            Theme.Glyph(g, e.IsDir ? Ico.Folder : Formats.GlyphFor(Formats.CategoryOf(ext)), 9f, risky ? Theme.Warn : Theme.InkSoft, ic);
            int tw = r.Width - x - Theme.S(30) - Theme.S(240);
            Theme.Text(g, name + (e.Encrypted ? "  🔒" : ""), Theme.F(9.25f, e.IsDir ? FontStyle.Bold : FontStyle.Regular), risky ? Theme.Warn : Theme.Ink, Theme.Mirror(new Rectangle(r.X + x + Theme.S(28), r.Y, Math.Max(Theme.S(40), tw), r.Height), Width), Align.Start, true, false, true);
            if (!e.IsDir)
            {
                Theme.Text(g, Theme.HumanSize(e.Size), Theme.F(8.75f), Theme.InkSoft, Theme.Mirror(new Rectangle(r.Right - Theme.S(200), r.Y, Theme.S(110), r.Height), Width), Align.End, true, false, true);
                if (hover)
                    for (int b = 0; b < 2; b++)
                    {
                        Rectangle br = Btn(r, b);
                        if (hoverBtn == i * 2 + b) Theme.FillRound(g, br, Theme.S(7), Theme.Pressed);
                        Theme.Glyph(g, b == 0 ? Ico.Search : Ico.Save, 8.5f, Theme.InkSoft, br);
                    }
            }
            using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, r.X + Theme.S(16), r.Bottom - 1, r.Right - Theme.S(16), r.Bottom - 1);
        }
        protected override string RowText(int i) { return Items[i].Path; }
    }

    /// A strip across the whole file: dark where it's random (packed, encrypted), light where it's text or empty.
    /// Click a spot to go there in the hex view.
    public class EntropyStrip : UiControl
    {
        public float[] Map;
        public long Length;
        public long Mark = -1;
        public event Action<long> Jump;
        public EntropyStrip() { Cursor = Cursors.Hand; Surface = Surface.Card; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            if (Map == null || Map.Length == 0) return;
            Rectangle r = new Rectangle(0, Theme.S(4), Width, Height - Theme.S(8));
            for (int i = 0; i < Map.Length; i++)
            {
                float x0 = r.X + r.Width * i / (float)Map.Length, x1 = r.X + r.Width * (i + 1) / (float)Map.Length;
                float v = Map[i];
                Color c = v > 0.94f ? Color.FromArgb(214, 64, 52) : v > 0.75f ? Color.FromArgb(232, 150, 52) : v > 0.45f ? Color.FromArgb(88, 150, 220) : Color.FromArgb(120, 190, 140);
                using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.CardInner, c, 0.25f + 0.75f * v))) g.FillRectangle(b, x0, r.Y, Math.Max(1, x1 - x0 + 0.5f), r.Height);
            }
            if (Mark >= 0 && Length > 0)
            {
                float mx = r.X + (float)(r.Width * (Mark / (double)Length));
                using (Pen p = new Pen(Theme.Ink, Theme.S(2))) g.DrawLine(p, mx, 0, mx, Height);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Length <= 0 || Jump == null) return;
            double f = Math.Max(0, Math.Min(1, e.X / (double)Math.Max(1, Width)));
            Jump((long)(f * Length));
        }
        protected override string AccName { get { return L.T("hx_map"); } }
    }

    /// Two files compared: the same or not, where they differ, and for text the changed lines.
    public class CompareWindow : MiniWindow
    {
        readonly string a, b;
        readonly RichTextBox box = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, WordWrap = false, DetectUrls = false };
        readonly Progress bar = new Progress();
        readonly Btn bClose;
        CompareResult res; string err;
        Rectangle headR;

        public static void Open(string a, string b, Form owner)
        {
            CompareWindow w = new CompareWindow(a, b);
            Point at = owner != null ? new Point(owner.Left + owner.Width / 2, owner.Top + owner.Height / 2) : Cursor.Position;
            w.ShowNear(at);
            w.Run();
        }

        CompareWindow(string a, string b) : base(() => L.T("fcmp_title"), Ico.Copy)
        {
            this.a = a; this.b = b;
            box.Font = Theme.Mono(9.5f); box.BackColor = Theme.CardInner; box.ForeColor = Theme.Ink; box.Visible = false;
            bar.SetIndeterminate(true);
            bClose = new Btn(() => L.T("close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bClose.Click += (s, e) => Dismiss();
            Controls.AddRange(new Control[] { box, bar, bClose });
            ClientSize = new Size(Theme.S(760), Theme.S(620));
        }

        void Run()
        {
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                CompareResult r = null; string e = null;
                try { r = Comparer2.Run(a, b, () => IsDisposed); } catch (Exception ex) { e = ex.Message; }
                Ui(() => { res = r; err = e; bar.Visible = false; Fill(); Invalidate(); });
            }) { IsBackground = true };
            t.Start();
        }

        void Fill()
        {
            if (res == null) return;
            if (res.Same || !res.BothText || res.TooLong || res.Lines.Count == 0) { box.Visible = false; return; }
            box.Visible = true;
            box.Clear();
            foreach (string line in res.Lines)
            {
                Color c = line.StartsWith("+ ") ? Theme.Success : line.StartsWith("- ") ? Theme.Error : line.StartsWith("@@") ? Theme.AccentColor : Theme.InkSoft;
                box.SelectionStart = box.TextLength; box.SelectionLength = 0;
                box.SelectionColor = c;
                box.AppendText(line + "\n");
            }
            box.SelectionStart = 0; box.ScrollToCaret();
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (box == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            headR = new Rectangle(p, Head, W - p * 2, Theme.S(170));
            bar.Bounds = M(new Rectangle(p, Head + Theme.S(60), W - p * 2, Theme.S(6)));
            box.Bounds = M(new Rectangle(p, headR.Bottom + Theme.S(6), W - p * 2, H - headR.Bottom - Theme.S(80)));
            int cw = Math.Max(Theme.S(100), bClose.PreferredWidth);
            bClose.Bounds = M(new Rectangle(W - p - cw, H - Theme.S(60), cw, Theme.S(40)));
        }

        protected override void PaintBody(Graphics g)
        {
            Font bf = Theme.F(9.5f, FontStyle.Bold), f = Theme.F(9f), mf = Theme.Mono(8.75f);
            int y = headR.Y;
            Theme.Text(g, "A  " + Path.GetFileName(a), bf, Theme.Ink, M(new Rectangle(headR.X, y, headR.Width, Theme.S(22))), Align.Start, true, false, true); y += Theme.S(22);
            Theme.Text(g, "B  " + Path.GetFileName(b), bf, Theme.Ink, M(new Rectangle(headR.X, y, headR.Width, Theme.S(22))), Align.Start, true, false, true); y += Theme.S(30);
            if (err != null) { Theme.Text(g, err, f, Theme.Error, M(new Rectangle(headR.X, y, headR.Width, Theme.S(40))), Align.Start, true, true); return; }
            if (res == null) { Theme.Text(g, L.T("fcmp_working"), f, Theme.InkSoft, M(new Rectangle(headR.X, y + Theme.S(14), headR.Width, Theme.S(22))), Align.Start); return; }
            Color c = res.Same ? Theme.Success : Theme.Warn;
            Rectangle v = new Rectangle(headR.X, y, headR.Width, Theme.S(38));
            Theme.FillRound(g, M(v), Theme.S(10), Theme.Mix(Theme.Card, c, 0.14f));
            Theme.Text(g, L.T(res.Same ? "cmp_same" : "cmp_diff"), bf, c, M(new Rectangle(v.X + Theme.S(14), v.Y, v.Width - Theme.S(28), v.Height)), Align.Start);
            y += Theme.S(46);
            string sizes = L.T("cmp_sizes", Theme.HumanSize(res.SizeA), Theme.HumanSize(res.SizeB));
            if (!res.Same && res.FirstDiff >= 0) sizes += "  ·  " + L.T("cmp_first", res.FirstDiff.ToString("N0"), "0x" + res.FirstDiff.ToString("X"));
            if (!res.Same && res.SizeA == res.SizeB) sizes += "  ·  " + L.T("cmp_bytes", res.DiffBytes.ToString("N0"));
            if (!res.Same && res.BothText && !res.TooLong) sizes += "  ·  " + L.T("cmp_lines", res.Added, res.Removed);
            Theme.Text(g, sizes, f, Theme.InkSoft, M(new Rectangle(headR.X, y, headR.Width, Theme.S(20))), Align.Start, true, false, true); y += Theme.S(22);
            Theme.Text(g, "SHA-256 A  " + res.HashA, mf, Theme.InkFaint, M(new Rectangle(headR.X, y, headR.Width, Theme.S(18))), Align.Start, true, false, true); y += Theme.S(18);
            Theme.Text(g, "SHA-256 B  " + res.HashB, mf, Theme.InkFaint, M(new Rectangle(headR.X, y, headR.Width, Theme.S(18))), Align.Start, true, false, true);
            if (!res.Same && res.TooLong) Theme.Text(g, L.T("cmp_too_long"), f, Theme.InkSoft, M(new Rectangle(headR.X, headR.Bottom + Theme.S(10), headR.Width, Theme.S(22))), Align.Start);
        }
    }
}
