using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace FileConverter
{
    /// Floating panel opened from the sidebar's "Activity" row: everything running or recently finished.
    public class ActivityPanel : Form
    {
        readonly ActivityRows rows = new ActivityRows();
        readonly Btn clear;
        readonly Timer poll = new Timer { Interval = 150 };
        int seen = -1;
        public event Action<Control> Navigate;
        public Func<Control, string> GlyphOf;

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        bool closing; Rectangle anchor; Form owner;

        public ActivityPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Card;
            Font = Theme.F(9.75f);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            clear = new Btn(() => L.T("act_clear"), null, BtnKind.Ghost) { FontSize = 8.75f };
            clear.Click += (s, e) =>
            {
                // the finished rows are swept away one after another
                List<Rectangle> done = rows.VisibleRows(i => i < rows.Jobs.Count && !rows.Jobs[i].Running);
                Fx.SweepAway(rows, done, () => { Activity.ClearFinished(); Refresh2(); FitHeight(); });
            };
            rows.Picked += j => { Dismiss(); if (Navigate != null && j.Source != null) Navigate(j.Source); };
            rows.GlyphOf = c => GlyphOf != null ? GlyphOf(c) : null;
            rows.Changed += () => Refresh2();
            Controls.Add(rows); Controls.Add(clear);
            Deactivate += (s, e) => Dismiss();
            poll.Tick += (s, e) => { if (Activity.Version != seen) Refresh2(); };
            poll.Start();
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // drop shadow
        }

        void Refresh2()
        {
            seen = Activity.Version;
            rows.Jobs = Activity.Snapshot();
            clear.Visible = Activity.FinishedCount > 0;
            rows.Invalidate();
            Invalidate();
        }

        /// Opens next to the sidebar row it belongs to.
        public void ShowBeside(Control sidebar, Rectangle row)
        {
            Refresh2();
            Rectangle a = sidebar.RectangleToScreen(row);
            Rectangle wa = Screen.FromControl(sidebar).WorkingArea;
            int w = Theme.S(400);
            int n = Math.Max(1, rows.Jobs.Count);
            int h = Theme.S(70) + (rows.Jobs.Count == 0 ? Theme.S(130) : Math.Min(Theme.S(5.5f * 68), n * Theme.S(68))) + Theme.S(10);
            Rectangle side = sidebar.RectangleToScreen(sidebar.ClientRectangle);
            int x = Theme.RTL ? side.X - w - Theme.S(6) : side.Right + Theme.S(6);
            int y = a.Bottom - h;
            y = Math.Max(wa.Top + Theme.S(8), Math.Min(wa.Bottom - h - Theme.S(8), y));
            x = Math.Max(wa.Left, Math.Min(wa.Right - w, x));
            Bounds = new Rectangle(x, y, w, h);
            int cw = Math.Max(Theme.S(110), clear.PreferredWidth);
            clear.Bounds = Theme.Mirror(new Rectangle(w - cw - Theme.S(12), Theme.S(16), cw, Theme.S(32)), w);
            rows.Bounds = new Rectangle(Theme.S(6), Theme.S(66), w - Theme.S(12), h - Theme.S(66) - Theme.S(6));
            anchor = a;
            owner = sidebar.FindForm();
            // opens out of the Activity row: it grows from the row's corner, with a little spring, and fades in
            IntPtr hh = Handle;   // made now, so its picture can be taken before it shows
            Fx.PopIn(this, owner, a, Theme.S(9), 150, () => { if (!IsDisposed) Show(owner); });
        }

        /// After clearing: the panel takes the height its rows need now (its bottom stays where it was).
        void FitHeight()
        {
            int n = Math.Max(1, rows.Jobs.Count);
            int h = Theme.S(70) + (rows.Jobs.Count == 0 ? Theme.S(130) : Math.Min(Theme.S(5.5f * 68), n * Theme.S(68))) + Theme.S(10);
            if (h == Height) return;
            int bottom = Bottom;
            Bounds = new Rectangle(Left, bottom - h, Width, h);
            rows.Bounds = new Rectangle(Theme.S(6), Theme.S(66), Width - Theme.S(12), h - Theme.S(66) - Theme.S(6));
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
            Invalidate(true);
        }

        /// Shrinks back towards the Activity row and fades out, then closes.
        public void Dismiss()
        {
            if (closing || IsDisposed) return;
            closing = true;
            Fx.PopOut(this, owner, anchor, Theme.S(9), 110, () => { if (!IsDisposed) Close(); });
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Dismiss(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { poll.Stop(); poll.Dispose(); base.OnFormClosed(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            using (GraphicsPath p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)))
            using (Pen pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, p);
            Theme.Text(g, L.T("act_title"), Theme.Display(14f), Theme.Ink, Theme.Mirror(new Rectangle(Theme.S(18), Theme.S(12), Width - Theme.S(150), Theme.S(28)), Width));
            int run = Activity.RunningCount, fin = Activity.FinishedCount;
            string sub = run == 0 && fin == 0 ? L.T("act_none_short") : run > 0 ? L.T("act_running_n", run) + (fin > 0 ? "  ·  " + L.T("act_finished_n", fin) : "") : L.T("act_finished_n", fin);
            Theme.Text(g, sub, Theme.F(8.75f), Theme.InkFaint, Theme.Mirror(new Rectangle(Theme.S(18), Theme.S(38), Width - Theme.S(150), Theme.S(18)), Width));
            using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, Theme.S(14), Theme.S(63), Width - Theme.S(14), Theme.S(63));
        }
    }

    /// The rows of the Activity panel.
    public class ActivityRows : ScrollList
    {
        public List<ActJob> Jobs = new List<ActJob>();
        public event Action<ActJob> Picked;
        public event Action Changed;
        public Func<Control, string> GlyphOf;
        bool hoverAct;

        public ActivityRows() { RowHeight = 68; Surface = Surface.Card; Cursor = Cursors.Hand; }
        protected override string RowText(int i) { ActJob j = Jobs[i]; return j.Kind + ": " + j.Title + (j.Detail.Length > 0 ? ", " + j.Detail : ""); }
        public override int Count { get { return Jobs.Count; } }

        public override bool Step()
        {
            bool more = base.Step();
            foreach (ActJob j in Jobs) if (j.Running) { Invalidate(); return true; }   // spinners
            return more;
        }

        Rectangle ActRect(Rectangle r) { int s = Theme.S(30); return Theme.Mirror(new Rectangle(r.Right - s - Theme.S(14), r.Y + (r.Height - s) / 2, s, s), Width); }

        bool HasAction(ActJob j) { return (j.Running && j.Cancel != null) || (j.State == ActState.Done && j.Output != null); }

        protected override void RowHover(int index, Point local, Rectangle row)
        {
            bool h = HasAction(Jobs[index]) && ActRect(row).Contains(local);
            if (h != hoverAct) { hoverAct = h; Invalidate(); }
            Animator.Start(this);
        }

        protected override void RowClick(int index, Point local, Rectangle row)
        {
            ActJob j = Jobs[index];
            if (HasAction(j) && ActRect(row).Contains(local))
            {
                if (j.Running) { try { j.Cancel(); } catch { } Activity.Update(j, -1, L.T("cancelling")); }
                else ShowOutput(j.Output);
                if (Changed != null) Changed();
                return;
            }
            if (Picked != null) Picked(j);
        }

        public static void ShowOutput(string o)
        {
            try
            {
                if (o == null) return;
                if (File.Exists(o)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + o + "\"");
                else if (Directory.Exists(o)) System.Diagnostics.Process.Start("explorer.exe", "\"" + o + "\"");
            }
            catch { }
        }

        protected override void PaintEmpty(Graphics g)
        {
            Rectangle ic = new Rectangle(Width / 2 - Theme.S(18), Theme.S(14), Theme.S(36), Theme.S(36));
            Theme.FillRound(g, ic, Theme.S(10), Theme.CardInner);
            Theme.Glyph(g, "", 12f, Theme.InkFaint, ic);
            Theme.Text(g, L.T("act_empty"), Theme.F(9f), Theme.InkSoft, new Rectangle(Theme.S(24), Theme.S(56), Width - Theme.S(48), Math.Max(Theme.S(60), Height - Theme.S(60))), Align.Center, false, true);
        }

        static string Ago(DateTime t)
        {
            double m = (DateTime.Now - t).TotalMinutes;
            if (m < 1) return L.T("act_just_now");
            if (m < 60) return L.T("act_min_ago", (int)m);
            return t.ToString("HH:mm");
        }

        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            ActJob j = Jobs[i];
            Theme.Smooth(g);
            Rectangle inner = new Rectangle(r.X + Theme.S(4), r.Y + Theme.S(2), r.Width - Theme.S(8), r.Height - Theme.S(4));
            if (hover) Theme.FillRound(g, inner, Theme.S(10), Theme.Hover);
            Func<Rectangle, Rectangle> M = x => Theme.Mirror(x, Width);

            // icon tile: the page's icon; red for failures
            Rectangle tile = M(new Rectangle(r.X + Theme.S(14), r.Y + (r.Height - Theme.S(38)) / 2, Theme.S(38), Theme.S(38)));
            bool failed = j.State == ActState.Failed;
            Theme.FillRound(g, tile, Theme.S(10), failed ? Theme.ErrorBg : j.Running ? Theme.AccentSoft : Theme.CardInner);
            string glyph = (GlyphOf != null ? GlyphOf(j.Source) : null) ?? "";
            Theme.Glyph(g, failed ? Ico.Error : glyph, 11f, failed ? Theme.Error : j.Running ? Theme.AccentColor : Theme.InkSoft, tile);

            int tx = r.X + Theme.S(64), tw = r.Width - Theme.S(64) - Theme.S(56);
            Theme.Text(g, j.Title, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, M(new Rectangle(tx, r.Y + Theme.S(11), tw, Theme.S(20))), Align.Start, true, false);
            string line; Color lc = Theme.InkSoft;
            switch (j.State)
            {
                case ActState.Running:
                    line = j.Kind + (j.Detail.Length > 0 ? "  ·  " + j.Detail : j.Progress >= 0 ? "  ·  " + (int)(j.Progress * 100) + "%" : "");
                    lc = Theme.Mix(Theme.InkSoft, Theme.AccentColor, 0.45f);
                    break;
                case ActState.Done: line = (j.Detail.Length > 0 ? j.Detail : L.T("act_done")) + "  ·  " + Ago(j.Ended); lc = Theme.Success; break;
                case ActState.Failed: line = j.Detail.Length > 0 ? j.Detail : L.T("act_failed"); lc = Theme.Error; break;
                default: line = L.T("cancelled") + "  ·  " + Ago(j.Ended); lc = Theme.InkFaint; break;
            }
            Theme.Text(g, line, Theme.F(8.5f), lc, M(new Rectangle(tx, r.Y + Theme.S(32), tw, Theme.S(18))), Align.Start, true, false);
            if (j.Running)
            {
                Rectangle bar = M(new Rectangle(tx, r.Y + Theme.S(53), tw, Theme.S(4)));
                Theme.FillRound(g, bar, Theme.S(2), Theme.Selected);
                if (j.Progress >= 0)
                {
                    int fw = Math.Max(Theme.S(4), (int)(bar.Width * j.Progress));
                    Rectangle fill = Theme.RTL ? new Rectangle(bar.Right - fw, bar.Y, fw, bar.Height) : new Rectangle(bar.X, bar.Y, fw, bar.Height);
                    Theme.FillRound(g, fill, Theme.S(2), Theme.AccentColor);
                }
                else
                {
                    // unknown amount: a short segment sliding along
                    double ph = (Environment.TickCount % 1400) / 1400.0;
                    int sw = bar.Width / 4, sx = bar.X + (int)((bar.Width + sw) * ph) - sw;
                    Rectangle seg = Rectangle.Intersect(new Rectangle(sx, bar.Y, sw, bar.Height), bar);
                    if (seg.Width > 0) Theme.FillRound(g, seg, Theme.S(2), Theme.AccentColor);
                }
            }

            // action: stop while running, show the file when done
            if (HasAction(j))
            {
                Rectangle a = ActRect(r);
                bool hot = hover && hoverAct;
                if (j.Running)
                {
                    if (hot) Theme.FillRound(g, a, a.Width / 2f, Theme.ErrorBg);
                    Theme.Glyph(g, Ico.Close, 8f, hot ? Theme.Error : Theme.InkFaint, a);
                }
                else
                {
                    Theme.FillRound(g, a, Theme.S(8), hot ? Theme.Mix(Theme.AccentSoft, Theme.AccentColor, 0.25f) : Theme.AccentSoft);
                    Theme.Glyph(g, Ico.FolderOpen, 9f, Theme.AccentColor, a);
                }
            }
            else if (j.State == ActState.Done)
            {
                Rectangle a = ActRect(r);
                Theme.Glyph(g, Ico.Check, 9f, Theme.Success, a);
            }
        }
    }
}
