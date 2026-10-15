using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace FileConverter
{
    public enum ItemState { Ready, Waiting, Working, Done, Failed, Skipped }

    public class FileItem
    {
        public string Path, Name, Ext;
        public long Size;
        public ItemState State = ItemState.Ready;
        public string Message = "";
        public double Progress;
        public string Output;
        public string DoneText; public bool DoneQuiet;   // compressor: "4 MB → 1 MB", or a grey note
    }

    /// The converter's file list.
    public class FileList : ScrollList
    {
        public List<FileItem> Items = new List<FileItem>();
        public bool Locked;
        bool hoverX;
        public event Action Changed;
        public event Action<FileItem> Opened;

        public FileList() { RowHeight = 64; }
        /// Big rows with a picture of each file instead of an icon.
        public bool Previews { get { return previews; } set { previews = value; RowHeight = value ? 112 : 64; ClampScroll(); Invalidate(); } }
        bool previews;
        void Repaint() { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { } }
        public override int Count { get { return Items.Count; } }
        protected override string[] DragFiles(int i)
        {
            FileItem it = Items[i];
            return new[] { it.State == ItemState.Done && !string.IsNullOrEmpty(it.Output) && System.IO.File.Exists(it.Output) ? it.Output : it.Path };
        }

        Rectangle XRect(Rectangle r)
        {
            int s = Theme.S(30);
            Rectangle x = new Rectangle(r.Right - s - Theme.S(10), r.Y + (r.Height - s) / 2, s, s);
            return Theme.RTL ? new Rectangle(r.X + Theme.S(10), x.Y, s, s) : x;
        }

        protected override void RowHover(int index, Point local, Rectangle row)
        {
            bool x = !Locked && XRect(row).Contains(local);
            if (x != hoverX) { hoverX = x; Invalidate(); }
            Cursor = x ? Cursors.Hand : Cursors.Default;
        }

        protected override void RowClick(int index, Point local, Rectangle row)
        {
            if (!Locked && XRect(row).Contains(local))
            {
                Items.RemoveAt(index);
                hoverIndex = -1; hoverX = false;
                ClampScroll(); Invalidate();
                if (Changed != null) Changed();
            }
        }

        protected override void RowDoubleClick(int index) { if (Opened != null) Opened(Items[index]); }

        public void RefreshItem(FileItem it)
        {
            int i = Items.IndexOf(it);
            if (i >= 0) Invalidate(RowRect(i));
        }

        protected override void DrawRow(Graphics g, int i, Rectangle b, bool hover)
        {
            FileItem it = Items[i];
            Theme.Smooth(g);
            if (hover) Theme.FillRound(g, new Rectangle(b.X + Theme.S(4), b.Y + Theme.S(2), b.Width - Theme.S(8), b.Height - Theme.S(4)), Theme.S(10), Theme.Hover);

            // badge, or (previews) a picture of the file
            int bs = Theme.S(44), tw = previews ? Theme.S(148) : bs, th = previews ? b.Height - Theme.S(16) : bs;
            Rectangle badge = new Rectangle(b.X + Theme.S(14), b.Y + (b.Height - th) / 2, tw, th);
            badge = Mir(badge, b);
            Category cat = Formats.CategoryOf(it.Ext);
            if (previews && Thumbs.Draw(g, it.Path, badge, Theme.S(10), Repaint)) Theme.StrokeRound(g, badge, Theme.S(10), Theme.ChipBorder);
            else
            {
                Theme.FillRoundGradient(g, badge, Theme.S(10), Theme.ChipTop, Theme.ChipBottom);
                Theme.StrokeRound(g, badge, Theme.S(10), Theme.ChipBorder);
                int gy = badge.Y + (badge.Height - bs) / 2;
                Theme.Glyph(g, Formats.GlyphFor(cat), previews ? 13f : 10f, Theme.InkSoft, new Rectangle(badge.X, gy + Theme.S(previews ? 0 : 4), badge.Width, Theme.S(previews ? 24 : 18)));
                string ext = it.Ext.ToUpperInvariant(); if (ext.Length > 4) ext = ext.Substring(0, 4); if (ext.Length == 0) ext = "?";
                Theme.Text(g, ext, Theme.F(ext.Length > 3 ? 6.75f : 7.5f, FontStyle.Bold), Theme.Ink, new Rectangle(badge.X, gy + Theme.S(previews ? 26 : 22), badge.Width, Theme.S(16)), Align.Center, true, false, true);
            }
            if (previews) b = new Rectangle(b.X, b.Y + (b.Height - Theme.S(64)) / 2, b.Width, Theme.S(64));   // the text lines, centered beside the picture

            int tx = Theme.S(14) + tw + Theme.S(14);
            int xw = Theme.S(50);
            int statusW = Theme.S(130);
            Rectangle nameR = Mir(new Rectangle(b.X + tx, b.Y + Theme.S(12), b.Width - tx - xw - statusW, Theme.S(22)), b);
            Theme.Text(g, it.Name, Theme.F(10f, FontStyle.Bold), Theme.Ink, nameR, Align.Start);

            string sub = Theme.HumanSize(it.Size);
            Color subC = Theme.InkSoft;
            if (it.State == ItemState.Done) { sub = it.DoneText ?? L.T("saved_as", System.IO.Path.GetFileName(it.Output)); subC = it.DoneQuiet ? Theme.InkSoft : Theme.Success; }
            else if (it.State == ItemState.Failed) { sub = it.Message; subC = Theme.Error; }
            Rectangle subR = Mir(new Rectangle(b.X + tx, b.Y + Theme.S(34), b.Width - tx - xw - Theme.S(10), Theme.S(20)), b);
            Theme.Text(g, sub, Theme.F(9f), subC, subR, Align.Start);

            Rectangle pill = Mir(new Rectangle(b.Right - xw - statusW, b.Y + Theme.S(12), statusW - Theme.S(8), Theme.S(22)), b);
            if (it.State == ItemState.Working)
            {
                RectangleF track = new RectangleF(pill.X, pill.Y + Theme.S(13), pill.Width, Theme.S(5));
                Theme.FillRound(g, track, track.Height / 2, Theme.Track);
                if (it.Progress > 0)
                {
                    float fw = Math.Max(track.Height, (float)(track.Width * it.Progress));
                    RectangleF f = Theme.RTL ? new RectangleF(track.Right - fw, track.Y, fw, track.Height) : new RectangleF(track.X, track.Y, fw, track.Height);
                    Theme.FillRound(g, f, track.Height / 2, Theme.AccentColor);
                }
                Theme.Text(g, it.Progress > 0 ? ((int)(it.Progress * 100)) + "%" : L.T("st_converting"), Theme.F(8f), Theme.InkSoft,
                    new Rectangle(pill.X, pill.Y - Theme.S(4), pill.Width, Theme.S(16)), Align.End);
            }
            else if (it.State != ItemState.Ready)
            {
                string label; Color fg, bg; string glyph = null;
                switch (it.State)
                {
                    case ItemState.Done: label = L.T("st_done"); fg = Theme.Success; bg = Theme.SuccessBg; glyph = Ico.Check; break;
                    case ItemState.Failed: label = L.T("st_failed"); fg = Theme.Error; bg = Theme.ErrorBg; glyph = Ico.Error; break;
                    case ItemState.Waiting: label = L.T("st_waiting"); fg = Theme.InkSoft; bg = Theme.Selected; break;
                    default: label = L.T("st_skipped"); fg = Theme.InkSoft; bg = Theme.Selected; break;
                }
                Font pf = Theme.F(8f, FontStyle.Bold);
                int w = Theme.Measure(label, pf) + Theme.S(glyph != null ? 30 : 16);
                Rectangle pr = Theme.RTL ? new Rectangle(pill.X, pill.Y, w, pill.Height) : new Rectangle(pill.Right - w, pill.Y, w, pill.Height);
                Theme.FillRound(g, pr, pr.Height / 2f, bg);
                if (glyph != null)
                {
                    Rectangle gr = Theme.RTL ? new Rectangle(pr.Right - Theme.S(22), pr.Y, Theme.S(16), pr.Height) : new Rectangle(pr.X + Theme.S(6), pr.Y, Theme.S(16), pr.Height);
                    Theme.Glyph(g, glyph, 7f, fg, gr);
                    Rectangle tr = Theme.RTL ? new Rectangle(pr.X + Theme.S(6), pr.Y, pr.Width - Theme.S(28), pr.Height) : new Rectangle(pr.X + Theme.S(22), pr.Y, pr.Width - Theme.S(28), pr.Height);
                    Theme.Text(g, label, pf, fg, tr, Align.Center);
                }
                else Theme.Text(g, label, pf, fg, pr, Align.Center);
            }

            if (!Locked && hover)
            {
                Rectangle xr = XRect(b);
                if (hoverX) Theme.FillRound(g, xr, xr.Width / 2f, Theme.Pressed);
                Theme.Glyph(g, Ico.Close, 8.5f, Theme.InkSoft, xr);
            }
            if (i < Items.Count - 1)
                using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, b.X + Theme.S(16), b.Bottom - 1, b.Right - Theme.S(16), b.Bottom - 1);
        }

        static Rectangle Mir(Rectangle r, Rectangle b)
        {
            if (!Theme.RTL) return r;
            return new Rectangle(b.X + (b.Right - r.Right), r.Y, r.Width, r.Height);
        }
    }

    /// Shows a folder (icon, name, path) and opens the Explorer folder picker when clicked.
    public class FolderButton : UiControl
    {
        public event EventHandler Picked;
        public FolderButton() { Cursor = Cursors.Hand; }

        protected override void OnClick(EventArgs e)
        {
            string chosen = FolderPicker.Pick(FindForm(), L.T("choose_save_folder"), Settings.OutputDir);
            if (chosen != null)
            {
                Settings.OutputDir = string.Equals(chosen.TrimEnd('\\'), Paths.Downloads.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "" : chosen;
                Invalidate();
                if (Picked != null) Picked(this, EventArgs.Empty);
            }
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRoundGradient(g, r, Theme.S(11), Theme.Mix(Theme.ChipTop, Theme.Hover, hot), Theme.Mix(Theme.ChipBottom, Theme.Pressed, hot * 0.7f));
            Theme.StrokeRound(g, r, Theme.S(11), Theme.Mix(Theme.ChipBorder, Theme.InkFaint, hot * 0.5f));
            string dir = Settings.OutputDir;
            string name = Settings.OutputIsDefault ? L.T("downloads") : Path.GetFileName(dir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = dir;
            int ic = Theme.S(36);
            Rectangle icon = Theme.Mirror(new Rectangle(Theme.S(12), (Height - ic) / 2, ic, ic), Width);
            Theme.FillRound(g, icon, Theme.S(9), Theme.Selected);
            Theme.Glyph(g, Settings.OutputIsDefault ? Ico.Download : Ico.Folder, 12f, Theme.Ink, icon);
            int tx = Theme.S(12) + ic + Theme.S(12);
            int cw = Theme.S(70);
            Theme.Text(g, name, Theme.F(10f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(tx, Theme.S(8), Width - tx - cw, Height / 2 - Theme.S(6)), Width), Align.Start, false);
            Theme.Text(g, dir, Theme.F(8.25f), Theme.InkSoft, Theme.Mirror(new Rectangle(tx, Height / 2, Width - tx - cw, Height / 2 - Theme.S(6)), Width), Align.Start, false, false, true);
            Rectangle ch = Theme.Mirror(new Rectangle(Width - cw, 0, cw - Theme.S(10), Height), Width);
            Theme.Text(g, L.T("change"), Theme.F(9f, FontStyle.Bold), Theme.Mix(Theme.InkSoft, Theme.Ink, hot), ch, Align.End);
        }
    }

    /// A rounded box that shows a message with an icon (info / warning / error).
    public class Notice : UiControl
    {
        public Func<string> Message;
        public string Glyph = Ico.Info;
        public int Kind; // 0 info, 1 warn, 2 error, 3 success
        public Notice(Func<string> msg) { Message = msg; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Color bg = Kind == 1 ? Theme.WarnBg : Kind == 2 ? Theme.ErrorBg : Kind == 3 ? Theme.SuccessBg : Theme.CardInner;
            Color fg = Kind == 1 ? Theme.Warn : Kind == 2 ? Theme.Error : Kind == 3 ? Theme.Success : Theme.InkSoft;
            Theme.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(10), bg);
            Rectangle gr = Theme.Mirror(new Rectangle(Theme.S(10), 0, Theme.S(24), Height), Width);
            Theme.Glyph(g, Glyph, 11f, fg, gr);
            Rectangle tr = Theme.Mirror(new Rectangle(Theme.S(40), Theme.S(4), Width - Theme.S(52), Height - Theme.S(8)), Width);
            Theme.Text(g, Message(), Theme.F(9f), Kind == 0 ? Theme.InkSoft : fg, tr, Align.Start, true, true);
        }
    }
}
