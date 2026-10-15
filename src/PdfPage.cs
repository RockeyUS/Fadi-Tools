using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// qpdf (free, open source) does the page work: joining PDFs, picking, ordering and rotating pages, splitting.
    /// It's downloaded the first time it's needed (about 27 MB).
    public static class Qpdf
    {
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static string Dir { get { return Path.Combine(Paths.ToolsDir, "qpdf"); } }
        const string Fallback = "https://github.com/qpdf/qpdf/releases/download/v12.4.2/qpdf-12.4.2-msvc64.zip";

        public static string Exe
        {
            get
            {
                try
                {
                    if (Directory.Exists(Dir))
                        foreach (string f in Directory.GetFiles(Dir, IsWindows ? "qpdf.exe" : "qpdf", SearchOption.AllDirectories)) return f;
                }
                catch { }
                return Tools.FindOnPath(IsWindows ? "qpdf.exe" : "qpdf");
            }
        }
        public static bool Ready { get { return Exe != null; } }

        public static void Install(Action<long, long> progress, Func<bool> cancelled)
        {
            string url = Net.GitHubAsset("qpdf/qpdf", @"msvc64\.zip$") ?? Fallback;
            string zip = Path.Combine(Path.GetTempPath(), "fadi_qpdf_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Net.Download(url, zip, progress, cancelled);
                string tmp = Dir + ".new";
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                Directory.CreateDirectory(tmp);
                using (ZipArchive za = ZipFile.OpenRead(zip))
                    foreach (ZipArchiveEntry e in za.Entries)
                    {
                        // only the programs: bin\qpdf.exe and its DLLs
                        string n = e.FullName.Replace('\\', '/');
                        int bi = n.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase);
                        if (bi < 0 || e.Name.Length == 0) continue;
                        string dest = Path.Combine(tmp, "bin", e.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        e.ExtractToFile(dest, true);
                    }
                if (!File.Exists(Path.Combine(tmp, "bin", "qpdf.exe"))) throw new UserError(L.T("pdf_dl_bad"));
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
                Directory.Move(tmp, Dir);
            }
            finally { try { File.Delete(zip); } catch { } }
        }

        /// Runs qpdf; null when it worked (warnings are fine), otherwise a readable reason.
        public static string Run(string args)
        {
            string exe = Exe;
            if (exe == null) return L.T("pdf_need_qpdf");
            string o;
            int code = Tools.RunHidden(exe, args, 30 * 60 * 1000, out o);
            if (code == 0 || code == 3) return null;
            if (o != null && o.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0) return L.T("pdf_locked");
            return Tools.LastLines(o ?? "", 2);
        }

        /// 0 = needs a password to open, 3 = locked against changes only (opens without one), 2 = not locked.
        public static int Lock(string pdf)
        {
            string exe = Exe;
            if (exe == null) return 2;
            string o;
            try { return Tools.RunHidden(exe, "--requires-password " + Tools.Quote(pdf), 60000, out o); } catch { return 2; }
        }

        /// A quick look for "/Encrypt" near the start and the end of the file (where PDFs say they're locked).
        public static bool LooksLocked(string pdf)
        {
            try
            {
                using (FileStream fs = new FileStream(pdf, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n = (int)Math.Min(fs.Length, 256 * 1024);
                    byte[] a = new byte[n];
                    fs.Read(a, 0, n);
                    string head = Encoding.GetEncoding(28591).GetString(a);
                    if (head.Contains("/Encrypt")) return true;
                    if (fs.Length > n)
                    {
                        fs.Seek(-n, SeekOrigin.End);
                        fs.Read(a, 0, n);
                        return Encoding.GetEncoding(28591).GetString(a).Contains("/Encrypt");
                    }
                }
            }
            catch { }
            return false;
        }

        /// Page count (qpdf when available, otherwise a quick look inside the file). -1 = unknown.
        public static int Pages(string pdf)
        {
            string exe = Exe;
            if (exe != null)
            {
                string o;
                try { if (Tools.RunHidden(exe, "--show-npages " + Tools.Quote(pdf), 60000, out o) == 0) { int n; if (int.TryParse((o ?? "").Trim().Split('\n')[0].Trim(), out n)) return n; } }
                catch { }
            }
            try
            {
                FileInfo fi = new FileInfo(pdf);
                if (fi.Length > 200L << 20) return -1;
                string s = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(pdf));
                int c = Regex.Matches(s, @"/Type\s*/Page(?![a-zA-Z])").Count;
                return c > 0 ? c : -1;
            }
            catch { return -1; }
        }

        /// "1-3,5,7-9" from a list of page numbers.
        public static string Ranges(IList<int> pages)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < pages.Count)
            {
                int j = i;
                while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1) j++;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(pages[i]);
                if (j > i) sb.Append('-').Append(pages[j]);
                i = j + 1;
            }
            return sb.ToString();
        }
    }

    // ====================================================================== combine: an ordered list of PDFs and photos
    public class PdfItem { public string Path, Name; public long Size; public bool IsPdf; public int Pages = -1; public Image Thumb; public bool Loaded; }

    public class OrderList : ScrollList
    {
        public List<PdfItem> Items = new List<PdfItem>();
        public event Action Changed;
        public bool Locked;
        int hoverBtn = -1, downIndex = -1, dropAt = -1, downY;
        bool dragging;

        public OrderList() { RowHeight = 62; Surface = Surface.Card; }
        public override int Count { get { return Items.Count; } }

        Rectangle Btn(Rectangle r, int k)   // 0 up, 1 down, 2 remove (right side)
        {
            int s = Theme.S(30), x = r.Right - Theme.S(14) - (3 - k) * (s + Theme.S(4));
            return Theme.Mirror(new Rectangle(x, r.Y + (r.Height - s) / 2, s, s), Width);
        }

        protected override void RowHover(int index, Point local, Rectangle row)
        {
            int h = -1;
            if (!Locked) for (int k = 0; k < 3; k++) if (Btn(row, k).Contains(local)) h = k;
            if (h != hoverBtn) { hoverBtn = h; Invalidate(); }
        }

        protected override void RowClick(int index, Point local, Rectangle row)
        {
            if (Locked || dragging) return;
            for (int k = 0; k < 3; k++)
                if (Btn(row, k).Contains(local))
                {
                    if (k == 2) Items.RemoveAt(index);
                    else { int to = index + (k == 0 ? -1 : 1); if (to < 0 || to >= Items.Count) return; PdfItem it = Items[index]; Items.RemoveAt(index); Items.Insert(to, it); }
                    Invalidate(); if (Changed != null) Changed();
                    return;
                }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Locked || e.Button != MouseButtons.Left || hoverBtn >= 0) return;
            int i = (e.Y + scroll) / RH;
            downIndex = i >= 0 && i < Items.Count ? i : -1; downY = e.Y; dragging = false;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (downIndex < 0 || (e.Button & MouseButtons.Left) == 0) return;
            if (!dragging && Math.Abs(e.Y - downY) > Theme.S(8)) { dragging = true; Capture = true; Cursor = Cursors.SizeNS; }
            if (dragging) { dropAt = Math.Max(0, Math.Min(Items.Count, (e.Y + scroll + RH / 2) / RH)); Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && downIndex >= 0 && dropAt >= 0)
            {
                int to = dropAt > downIndex ? dropAt - 1 : dropAt;
                if (to != downIndex) { PdfItem it = Items[downIndex]; Items.RemoveAt(downIndex); Items.Insert(to, it); if (Changed != null) Changed(); }
            }
            bool was = dragging;
            dragging = false; downIndex = -1; dropAt = -1; Capture = false; Cursor = Cursors.Default;
            if (was) Invalidate();
        }

        protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
        {
            PdfItem it = Items[i];
            Theme.Smooth(g);
            Rectangle inner = new Rectangle(r.X + Theme.S(4), r.Y + Theme.S(3), r.Width - Theme.S(8), r.Height - Theme.S(6));
            if (dragging && i == downIndex) Theme.FillRound(g, inner, Theme.S(10), Theme.AccentSoft);
            else if (hover && !dragging) Theme.FillRound(g, inner, Theme.S(10), Theme.Hover);
            Func<Rectangle, Rectangle> M = x => Theme.Mirror(x, Width);
            // position number
            Rectangle num = M(new Rectangle(r.X + Theme.S(12), r.Y, Theme.S(24), r.Height));
            Theme.Text(g, (i + 1).ToString(), Theme.F(9f, FontStyle.Bold), Theme.InkFaint, num, Align.Center, true, false, true);
            // picture or document tile
            Rectangle tile = M(new Rectangle(r.X + Theme.S(42), r.Y + (r.Height - Theme.S(44)) / 2, Theme.S(44), Theme.S(44)));
            Theme.FillRound(g, tile, Theme.S(8), Theme.CardInner);
            if (it.Thumb != null)
            {
                using (GraphicsPath cp = Theme.Round(tile, Theme.S(8)))
                {
                    GraphicsState st = g.Save(); g.SetClip(cp, CombineMode.Intersect);
                    float sc = Math.Max(tile.Width / (float)it.Thumb.Width, tile.Height / (float)it.Thumb.Height);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(it.Thumb, tile.X + (tile.Width - it.Thumb.Width * sc) / 2, tile.Y + (tile.Height - it.Thumb.Height * sc) / 2, it.Thumb.Width * sc, it.Thumb.Height * sc);
                    g.Restore(st);
                }
            }
            else Theme.Glyph(g, it.IsPdf ? Ico.Doc : Ico.Photo, 12f, Theme.InkSoft, tile);
            Theme.StrokeRound(g, tile, Theme.S(8), Theme.CardBorder);
            int tx = r.X + Theme.S(98), tw = r.Width - Theme.S(98) - Theme.S(130);
            Theme.Text(g, it.Name, Theme.F(9.75f, FontStyle.Bold), Theme.Ink, M(new Rectangle(tx, r.Y + Theme.S(12), tw, Theme.S(20))), Align.Start, true, false);
            string sub = it.IsPdf ? "PDF" + (it.Pages > 0 ? "  ·  " + (it.Pages == 1 ? L.T("pdf_one_page") : L.T("pdf_n_pages", it.Pages)) : "") : L.T("pdf_photo", Formats.Ext(it.Path).ToUpperInvariant());
            Theme.Text(g, sub + "  ·  " + Theme.HumanSize(it.Size), Theme.F(8.5f), Theme.InkSoft, M(new Rectangle(tx, r.Y + Theme.S(33), tw, Theme.S(18))), Align.Start, true, false);
            if (!Locked && hover && !dragging)
            {
                string[] gl = { "\uE70E", "\uE70D", Ico.Close };
                for (int k = 0; k < 3; k++)
                {
                    if ((k == 0 && i == 0) || (k == 1 && i == Items.Count - 1)) continue;
                    Rectangle b = Btn(r, k);
                    bool hot = hoverBtn == k;
                    if (hot) Theme.FillRound(g, b, Theme.S(8), k == 2 ? Theme.ErrorBg : Theme.Selected);
                    Theme.Glyph(g, gl[k], k == 2 ? 7.5f : 8.5f, hot ? (k == 2 ? Theme.Error : Theme.Ink) : Theme.InkSoft, b);
                }
            }
            if (i < Items.Count - 1 && !(hover && !dragging))
                using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, r.X + Theme.S(98), r.Bottom - 1, r.Right - Theme.S(16), r.Bottom - 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (dragging && dropAt >= 0)
            {
                int y = dropAt * RH - scroll;
                using (Pen p = new Pen(Theme.AccentColor, Theme.S(2.5f))) e.Graphics.DrawLine(p, Theme.S(10), y, Width - Theme.S(10), y);
            }
        }
    }

    // ====================================================================== edit pages: a grid of page pictures
    public class PdfPageItem { public int Src; public int Rot; public Image Thumb; public bool Sel; }

    public class PageGrid : UiControl
    {
        public List<PdfPageItem> Pages = new List<PdfPageItem>();
        public event Action Changed, SelectionChanged;
        public bool Locked;
        int scroll; float scrollF; int target;
        int hover = -1, hoverTool = -1, anchor = -1, downIndex = -1, dropAt = -1; Point down; bool dragging;

        public PageGrid() { Surface = Surface.Card; Cursor = Cursors.Hand; SetStyle(ControlStyles.Selectable, true); TabStop = true; }

        int Pad { get { return Theme.S(14); } }
        int TW { get { return Theme.S(122); } }
        int TH { get { return Theme.S(158); } }
        int CellW { get { return TW + Theme.S(18); } }
        int CellH { get { return TH + Theme.S(40); } }
        int Cols { get { return Math.Max(1, (Width - Pad * 2 + Theme.S(18)) / CellW); } }
        int ContentH { get { return Pad * 2 + (Pages.Count + Cols - 1) / Cols * CellH; } }
        int MaxScroll { get { return Math.Max(0, ContentH - Height); } }

        Rectangle Cell(int i)
        {
            int cols = Cols;
            int used = cols * CellW - Theme.S(18), left = Math.Max(Pad, (Width - used) / 2);
            Rectangle r = new Rectangle(left + (i % cols) * CellW, Pad + (i / cols) * CellH - scroll, TW, TH);
            return Theme.Mirror(r, Width);
        }
        Rectangle Tool(Rectangle c, int k)   // 0 rotate left, 1 rotate right, 2 delete
        {
            int s = Theme.S(28), total = 3 * s + 2 * Theme.S(4), x = c.X + (c.Width - total) / 2 + k * (s + Theme.S(4));
            return new Rectangle(x, c.Bottom - s - Theme.S(8), s, s);
        }
        int At(Point p) { for (int i = 0; i < Pages.Count; i++) { Rectangle c = Cell(i); c.Height += Theme.S(24); if (c.Contains(p)) return i; } return -1; }

        public List<int> SelectedIndexes { get { List<int> l = new List<int>(); for (int i = 0; i < Pages.Count; i++) if (Pages[i].Sel) l.Add(i); return l; } }
        public int SelectedCount { get { int n = 0; foreach (PdfPageItem p in Pages) if (p.Sel) n++; return n; } }

        public void SelectAll(bool on) { foreach (PdfPageItem p in Pages) p.Sel = on; Invalidate(); if (SelectionChanged != null) SelectionChanged(); }
        public void RotateSelected(int deg) { foreach (PdfPageItem p in Pages) if (p.Sel) p.Rot = (p.Rot + deg + 360) % 360; Invalidate(); if (Changed != null) Changed(); }
        public void DeleteSelected()
        {
            if (SelectedCount == 0 || SelectedCount == Pages.Count) return;
            Pages.RemoveAll(p => p.Sel);
            Clamp(); Invalidate(); if (Changed != null) Changed(); if (SelectionChanged != null) SelectionChanged();
        }

        void Clamp() { scroll = Math.Max(0, Math.Min(MaxScroll, scroll)); target = Math.Max(0, Math.Min(MaxScroll, target)); }
        public override bool Step()
        {
            bool more = base.Step();
            if (!Fx.Motion) { scrollF = target; scroll = target; Invalidate(); return more; }
            if (scroll != target)
            {
                scrollF += (target - scrollF) * 0.3f;
                if (Math.Abs(target - scrollF) < 0.6f) scrollF = target;
                scroll = (int)Math.Round(scrollF); Invalidate();
                return true;
            }
            return more;
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (scroll == target) scrollF = scroll;
            target = Math.Max(0, Math.Min(MaxScroll, target - e.Delta / 120 * CellH / 2));
            Animator.Start(this);
            base.OnMouseWheel(e);
        }
        protected override void OnResize(EventArgs e) { Clamp(); base.OnResize(e); Invalidate(); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (downIndex >= 0 && (e.Button & MouseButtons.Left) != 0 && !Locked)
            {
                if (!dragging && (Math.Abs(e.X - down.X) > Theme.S(8) || Math.Abs(e.Y - down.Y) > Theme.S(8))) { dragging = true; Capture = true; }
                if (dragging)
                {
                    // where it would land: before the cell under the pointer (or after the last one)
                    dropAt = Pages.Count;
                    for (int i = 0; i < Pages.Count; i++)
                    {
                        Rectangle c = Cell(i);
                        bool before = Theme.RTL ? e.X > c.X + c.Width / 2 : e.X < c.X + c.Width / 2;
                        if (e.Y < c.Bottom + Theme.S(24) && (before || e.Y < c.Y - Theme.S(8))) { dropAt = i; break; }
                    }
                    // scroll near the edges while dragging
                    if (e.Y < Theme.S(30)) { target = Math.Max(0, target - Theme.S(20)); Animator.Start(this); }
                    else if (e.Y > Height - Theme.S(30)) { target = Math.Min(MaxScroll, target + Theme.S(20)); Animator.Start(this); }
                    Invalidate();
                    return;
                }
            }
            int h = At(e.Location), ht = -1;
            if (h >= 0 && !Locked) for (int k = 0; k < 3; k++) if (Tool(Cell(h), k).Contains(e.Location)) ht = k;
            if (h != hover || ht != hoverTool) { hover = h; hoverTool = ht; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { if (!dragging) { hover = -1; hoverTool = -1; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            int i = At(e.Location);
            down = e.Location; downIndex = -1; dragging = false;
            if (i < 0) { if (ModifierKeys == Keys.None) SelectAll(false); return; }
            if (!Locked && hoverTool >= 0 && hover == i)
            {
                if (hoverTool == 2) { if (Pages.Count > 1) { Pages.RemoveAt(i); hover = -1; Clamp(); if (Changed != null) Changed(); if (SelectionChanged != null) SelectionChanged(); } }
                else { Pages[i].Rot = (Pages[i].Rot + (hoverTool == 0 ? 270 : 90)) % 360; if (Changed != null) Changed(); }
                Invalidate();
                return;
            }
            if ((ModifierKeys & Keys.Control) != 0) { Pages[i].Sel = !Pages[i].Sel; anchor = i; }
            else if ((ModifierKeys & Keys.Shift) != 0 && anchor >= 0)
            {
                int a = Math.Min(anchor, i), b = Math.Max(anchor, i);
                for (int k = 0; k < Pages.Count; k++) Pages[k].Sel = k >= a && k <= b;
            }
            else
            {
                if (!Pages[i].Sel) foreach (PdfPageItem p in Pages) p.Sel = false;
                Pages[i].Sel = true; anchor = i;
            }
            downIndex = i;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && dropAt >= 0)
            {
                // move every selected page (in their order) to the drop point
                List<PdfPageItem> moving = Pages.FindAll(p => p.Sel);
                int at = dropAt;
                for (int i = 0; i < dropAt && i < Pages.Count; i++) if (Pages[i].Sel) at--;
                Pages.RemoveAll(p => p.Sel);
                Pages.InsertRange(Math.Max(0, Math.Min(Pages.Count, at)), moving);
                if (Changed != null) Changed();
            }
            else if (downIndex >= 0 && !dragging && ModifierKeys == Keys.None && SelectedCount > 1)
            {
                // a plain click on one of several selected pages: keep just that one
                for (int k = 0; k < Pages.Count; k++) Pages[k].Sel = k == downIndex;
                if (SelectionChanged != null) SelectionChanged();
            }
            dragging = false; downIndex = -1; dropAt = -1; Capture = false;
            Invalidate();
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Delete || k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Locked) return;
            if (e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
            else if (e.KeyCode == Keys.A && e.Control) { SelectAll(true); e.Handled = true; }
            else if (e.KeyCode == Keys.R && e.Control) { RotateSelected(e.Shift ? 270 : 90); e.Handled = true; }
        }

        static void DrawRotated(Graphics g, Image im, Rectangle box, int rot)
        {
            bool side = rot == 90 || rot == 270;
            float iw = side ? im.Height : im.Width, ih = side ? im.Width : im.Height;
            float sc = Math.Min(box.Width / iw, box.Height / ih);
            float w = iw * sc, h = ih * sc;
            GraphicsState st = g.Save();
            g.TranslateTransform(box.X + box.Width / 2f, box.Y + box.Height / 2f);
            g.RotateTransform(rot);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            float dw = side ? h : w, dh = side ? w : h;
            using (SolidBrush paper = new SolidBrush(Color.White)) g.FillRectangle(paper, -dw / 2, -dh / 2, dw, dh);
            g.DrawImage(im, -dw / 2, -dh / 2, dw, dh);
            g.Restore(st);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Clamp();
            for (int i = 0; i < Pages.Count; i++)
            {
                Rectangle c = Cell(i);
                if (c.Bottom + Theme.S(30) < 0 || c.Y > Height) continue;
                PdfPageItem p = Pages[i];
                bool hot = i == hover && !dragging;
                Theme.FillRound(g, c, Theme.S(10), hot ? Theme.Hover : Theme.CardInner);
                Rectangle box = Rectangle.Inflate(c, -Theme.S(10), -Theme.S(10));
                if (p.Thumb != null) DrawRotated(g, p.Thumb, box, p.Rot);
                else
                {
                    // still loading
                    using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.CardInner, Theme.Ink, 0.05f))) g.FillRectangle(b, box.X + box.Width / 6, box.Y, box.Width * 2 / 3, box.Height);
                }
                if (dragging && p.Sel) using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, Theme.Card))) g.FillRectangle(dim, c);
                if (p.Sel)
                {
                    Theme.StrokeRound(g, new RectangleF(c.X + 1, c.Y + 1, c.Width - 2, c.Height - 2), Theme.S(10), Theme.AccentColor, 2.4f);
                    Rectangle chk = new Rectangle(Theme.RTL ? c.X + Theme.S(6) : c.Right - Theme.S(26), c.Y + Theme.S(6), Theme.S(20), Theme.S(20));
                    Theme.FillRound(g, chk, Theme.S(10), Theme.AccentColor);
                    Theme.Glyph(g, Ico.Check, 6.5f, Theme.PrimaryInk, chk);
                }
                else Theme.StrokeRound(g, c, Theme.S(10), Theme.CardBorder);
                if (p.Rot != 0)
                {
                    Rectangle rb = new Rectangle(Theme.RTL ? c.Right - Theme.S(30) : c.X + Theme.S(6), c.Y + Theme.S(6), Theme.S(24), Theme.S(20));
                    Theme.FillRound(g, rb, Theme.S(6), Color.FromArgb(200, 0, 0, 0));
                    Theme.Glyph(g, Ico.Rotate, 6.5f, Color.White, rb);
                }
                if (hot && !Locked)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        Rectangle t = Tool(c, k);
                        bool th = hoverTool == k;
                        Theme.FillRound(g, t, Theme.S(8), th ? (k == 2 ? Theme.Error : Theme.Ink) : Color.FromArgb(215, 30, 30, 30));
                        string gl = k == 2 ? Ico.Delete : Ico.Rotate;
                        if (k == 0)
                        {
                            // the rotate icon, mirrored for "left"
                            GraphicsState st = g.Save();
                            g.TranslateTransform(t.X + t.Width / 2f, 0); g.ScaleTransform(-1, 1); g.TranslateTransform(-(t.X + t.Width / 2f), 0);
                            Theme.Glyph(g, gl, 7.5f, Color.White, t);
                            g.Restore(st);
                        }
                        else Theme.Glyph(g, gl, 7.5f, Color.White, t);
                    }
                }
                string label = (i + 1).ToString() + (p.Src != i + 1 ? "  ·  " + L.T("pdf_was", p.Src) : "");
                Theme.Text(g, label, Theme.F(8.5f, p.Sel ? FontStyle.Bold : FontStyle.Regular), p.Sel ? Theme.AccentColor : Theme.InkSoft, new Rectangle(c.X - Theme.S(8), c.Bottom + Theme.S(4), c.Width + Theme.S(16), Theme.S(20)), Align.Center, true, false);
            }
            if (dragging && dropAt >= 0 && Pages.Count > 0)
            {
                Rectangle c = dropAt < Pages.Count ? Cell(dropAt) : Cell(Pages.Count - 1);
                int x = dropAt < Pages.Count ? (Theme.RTL ? c.Right + Theme.S(9) : c.X - Theme.S(9)) : (Theme.RTL ? c.X - Theme.S(9) : c.Right + Theme.S(9));
                using (Pen pn = new Pen(Theme.AccentColor, Theme.S(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pn, x, c.Y - Theme.S(4), x, c.Bottom + Theme.S(4));
            }
            if (MaxScroll > 0)
            {
                int h = Math.Max(Theme.S(36), Height * Height / Math.Max(1, ContentH));
                int y = (int)((Height - h) * (scroll / (double)MaxScroll));
                Rectangle th = new Rectangle(Theme.RTL ? Theme.S(3) : Width - Theme.S(9), y, Theme.S(6), h);
                Theme.FillRound(g, th, th.Width / 2f, Color.FromArgb(90, Theme.InkFaint));
            }
        }
    }

    // ====================================================================== the page
    public class PdfPage : Page
    {
        readonly Segments mode;
        readonly OrderList list = new OrderList();
        readonly PageGrid grid = new PageGrid();
        readonly DropZone dropC, dropP;
        readonly Btn bAdd, bClear, bOpen, bGo, bRotL, bRotR, bDel, bAll, bReset, bExtract, bSplit, bImages, bShow;
        readonly Btn bLock, bNums, bMark, bSign;
        readonly ToolTip tips = new ToolTip();
        Rectangle stampCard, stampLblR;
        string shownPath;      // the file as it's called (pdfPath can be an unlocked copy of it)
        bool pdfLocked;        // it had a password: saving makes a copy without one
        readonly Segments pageSize, imgFmt;
        readonly Progress progress = new Progress { Visible = false };
        Rectangle left, opts, more, headR, modeR, optLblR, optHintR, selLblR, moreLblR, fmtLblR, statusR, infoR;
        string statusText = ""; Color statusColor;
        bool running, cancel;
        string pdfPath; int pdfPages, created; int loadToken; string thumbsDir;
        string lastOutput;

        public PdfPage() : base("pdf_title", "pdf_sub")
        {
            mode = new Segments(() => L.T("pdf_m_combine"), () => L.T("pdf_m_pages"));
            mode.Glyphs = new[] { "", "" };
            mode.FontSize = 9.25f;
            int m0; int.TryParse(Settings.Get("PdfMode", "0"), out m0);
            mode.SetSilently(Math.Max(0, Math.Min(1, m0)));
            mode.FadeTarget = this;
            mode.FadeArea = () => new Rectangle(0, mode.Bottom + Theme.S(4), Width, Math.Max(0, Height - mode.Bottom - Theme.S(4)));
            mode.SelectedChanged += (s, e) => { Settings.Set("PdfMode", mode.SelectedIndex.ToString()); SetStatus("", Theme.InkSoft); UpdateState(); };

            dropC = new DropZone(() => L.T("pdf_drop_c"), () => L.T("pdf_drop_c_sub")) { Glyph = "", Tips = new[] { "tip_pdf_order", "tip_pdf_sign", "tip_paste" } };
            dropC.Click += (s, e) => BrowseCombine();
            dropP = new DropZone(() => L.T("pdf_drop_p"), () => L.T("pdf_drop_p_sub")) { Glyph = "", Tips = new[] { "tip_pdf_sign", "tip_palette" } };
            dropP.Click += (s, e) => BrowsePdf();
            list.Changed += () => UpdateState();
            grid.Changed += () => UpdateState();
            grid.SelectionChanged += () => UpdateState();

            bAdd = new Btn(() => L.T("add_files"), Ico.Add, BtnKind.Secondary);
            bAdd.Click += (s, e) => { if (mode.SelectedIndex == 0) BrowseCombine(); else BrowsePdf(); };
            bClear = new Btn(() => L.T("clear"), Ico.Delete, BtnKind.Ghost);
            bClear.Click += (s, e) => ClearAll();
            bOpen = new Btn(() => L.T("pdf_open_other"), Ico.FolderOpen, BtnKind.Secondary);
            bOpen.Click += (s, e) => BrowsePdf();
            bGo = new Btn(() => running ? L.T("cancel") : GoLabel, "", BtnKind.Primary) { FontSize = 11f, Radius = 10 };
            bGo.Click += (s, e) => { if (running) { cancel = true; SetStatus(L.T("cancelling"), Theme.InkSoft); } else Go(); };

            bRotL = Tool("pdf_rot_l", "\uE7A8", () => grid.RotateSelected(270));
            bRotR = Tool("pdf_rot_r", Ico.Rotate, () => grid.RotateSelected(90));
            bDel = Tool("pdf_delete", Ico.Delete, () => grid.DeleteSelected());
            bAll = Tool("pdf_select_all", Ico.Check, () => grid.SelectAll(grid.SelectedCount < grid.Pages.Count));
            bReset = Tool("pdf_reset", Ico.Refresh, ResetPages);
            bExtract = Tool("pdf_extract", "", () => Export(1));
            bSplit = Tool("pdf_split", "", () => Export(2));
            bImages = Tool("pdf_images", Ico.Photo, () => Export(3));
            bLock = Tool("pdf_lock", "\uE72E", LockOrUnlock);
            bLock.Label = () => L.T(pdfLocked ? "pdf_unlock" : "pdf_lock");
            bNums = Tool("pdf_nums", "\uE8EF", AddNumbers);
            bMark = Tool("pdf_mark", "\uE790", AddWatermark);
            bSign = Tool("pdf_sign", "\uEC87", SignIt);
            foreach (Btn b in new[] { bLock, bNums, bMark, bSign }) { Btn k = b; k.MouseEnter += (s, e) => tips.SetToolTip(k, k.Caption); }
            bShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Ghost) { AlignStart = true, Visible = false };
            bShow.Click += (s, e) => ActivityRows.ShowOutput(lastOutput);
            pageSize = new Segments(() => "A4", () => "Letter", () => L.T("pdf_size_photo"));
            pageSize.FontSize = 9f;
            int ps; int.TryParse(Settings.Get("PdfPageSize", "0"), out ps);
            pageSize.SetSilently(Math.Max(0, Math.Min(2, ps)));
            pageSize.SelectedChanged += (s, e) => { Settings.Set("PdfPageSize", pageSize.SelectedIndex.ToString()); Invalidate(); };
            imgFmt = new Segments(() => "JPG", () => "PNG");
            imgFmt.FontSize = 9f;
            imgFmt.SetSilently(Settings.Get("PdfImgFmt", "jpg") == "png" ? 1 : 0);
            imgFmt.SelectedChanged += (s, e) => Settings.Set("PdfImgFmt", imgFmt.SelectedIndex == 1 ? "png" : "jpg");

            Controls.AddRange(new Control[] { mode, list, grid, dropC, dropP, bAdd, bClear, bOpen, bGo, bRotL, bRotR, bDel, bAll, bReset, bExtract, bSplit, bImages, bShow, pageSize, imgFmt, progress, bLock, bNums, bMark, bSign });
            EnableDrop(list, grid, dropC, dropP);
            UpdateState();
        }

        Btn Tool(string key, string glyph, Action a)
        {
            Btn b = new Btn(() => L.T(key), glyph, BtnKind.Ghost) { AlignStart = true, FontSize = 9.25f };
            b.Click += (s, e) => a();
            return b;
        }

        public override bool Busy { get { return running; } }
        bool Combine { get { return mode.SelectedIndex == 0; } }
        bool Edited
        {
            get
            {
                if (grid.Pages.Count != pdfPages) return true;
                for (int i = 0; i < grid.Pages.Count; i++) if (grid.Pages[i].Src != i + 1 || grid.Pages[i].Rot != 0) return true;
                return false;
            }
        }
        string GoLabel
        {
            get
            {
                if (Combine) return list.Items.Count > 1 ? L.T("pdf_go_combine_n", list.Items.Count) : L.T("pdf_go_combine");
                return L.T("pdf_go_save");
            }
        }

        /// From the quick search: open a mode (0 combine, 1 pages) and ask for files.
        public void StartMode(int m, bool browse)
        {
            mode.SelectedIndex = m;
            if (browse) { if (m == 0) BrowseCombine(); else BrowsePdf(); }
        }

        public override void Restyle() { UpdateState(); }

        void ClearAll()
        {
            if (running) return;
            if (Combine) { foreach (PdfItem it in list.Items) if (it.Thumb != null) it.Thumb.Dispose(); list.Items.Clear(); list.Invalidate(); }
            else ClosePdf();
            lastOutput = null; bShow.Visible = false;
            SetStatus("", Theme.InkSoft); UpdateState();
        }

        void ClosePdf()
        {
            loadToken++;
            foreach (PdfPageItem p in grid.Pages) if (p.Thumb != null) p.Thumb.Dispose();
            grid.Pages.Clear(); grid.Invalidate();
            pdfPath = null; shownPath = null; pdfLocked = false; pdfPages = 0; created = 0;
            if (thumbsDir != null) { string d = thumbsDir; ThreadPool.QueueUserWorkItem(_ => { try { Directory.Delete(d, true); } catch { } }); thumbsDir = null; }
        }

        // ------------------------------------------------------------------ layout & paint
        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, gap = Theme.S(18), rightW = Theme.S(320), ip = Theme.S(20);
            cards.Clear();
            left = new Rectangle(p, Top0, W - p * 2 - rightW - gap, H - Top0 - p);
            cards.Add(left);
            int rx = left.Right + gap;
            bool c = Combine, has = c ? list.Items.Count > 0 : pdfPath != null;
            // header: mode switch + add / clear
            int bw = Math.Max(Theme.S(86), bClear.PreferredWidth), aw = Math.Max(Theme.S(118), (c ? bAdd : bOpen).PreferredWidth);
            int room = left.Width - ip * 2 - (has ? aw + bw + Theme.S(24) : 0);
            Place(mode, new Rectangle(left.X + ip, left.Y + Theme.S(14), Math.Max(Theme.S(240), Math.Min(Theme.S(400), room)), Theme.S(38)));
            Place(bClear, new Rectangle(left.Right - ip - bw, left.Y + Theme.S(15), bw, Theme.S(36)));
            Btn add = c ? bAdd : bOpen;
            Place(add, new Rectangle(left.Right - ip - aw - (has ? bw + Theme.S(8) : 0), left.Y + Theme.S(15), aw, Theme.S(36)));
            bAdd.Visible = c && has; bOpen.Visible = !c && has; bClear.Visible = has && !running;
            infoR = new Rectangle(left.X + ip, left.Y + Theme.S(60), left.Width - ip * 2, Theme.S(22));
            Rectangle body = new Rectangle(left.X + Theme.S(6), left.Y + Theme.S(88), left.Width - Theme.S(12), left.Height - Theme.S(94));
            Place(list, body); Place(grid, body);
            Rectangle dz = new Rectangle(left.X + ip, left.Y + Theme.S(68), left.Width - ip * 2, left.Height - Theme.S(68) - ip);
            Place(dropC, dz); Place(dropP, dz);
            list.Visible = c && has; dropC.Visible = c && !has;
            grid.Visible = !c && has; dropP.Visible = !c && !has;

            // right column
            int y = Top0, w = rightW, x = rx + ip, cw = w - ip * 2;
            Place(bGo, new Rectangle(rx, y, w, Theme.S(52))); y += Theme.S(60);
            if (progress.Visible || statusText.Length > 0)
            {
                Place(progress, new Rectangle(rx + Theme.S(4), y + Theme.S(2), w - Theme.S(8), Theme.S(5)));
                statusR = new Rectangle(rx + Theme.S(4), y + Theme.S(10), w - Theme.S(8), Theme.S(36)); y += Theme.S(50);
            }
            else statusR = Rectangle.Empty;
            int oy = y + Theme.S(16);
            foreach (Control k in new Control[] { pageSize, bRotL, bRotR, bDel, bAll, bReset, bExtract, bSplit, bImages, imgFmt, bLock, bNums, bMark, bSign }) k.Visible = false;
            stampCard = Rectangle.Empty;
            if (c)
            {
                optLblR = new Rectangle(x, oy, cw, Theme.S(18)); oy += Theme.S(24);
                pageSize.Visible = true; Place(pageSize, new Rectangle(x, oy, cw, Theme.S(36))); oy += Theme.S(42);
                optHintR = new Rectangle(x, oy, cw, Theme.S(54)); oy += Theme.S(58);
                opts = new Rectangle(rx, y, w, oy - y);
                cards.Add(opts);
                more = Rectangle.Empty;
            }
            else
            {
                selLblR = new Rectangle(x, oy, cw, Theme.S(18)); oy += Theme.S(26);
                int ry = oy;
                foreach (Btn b in new[] { bRotL, bRotR, bDel, bAll, bReset })
                {
                    b.Visible = has; if (!has) continue;
                    if (b == bReset && !Edited) { b.Visible = false; continue; }
                    Place(b, new Rectangle(rx + Theme.S(8), ry, w - Theme.S(16), Theme.S(36))); ry += Theme.S(38);
                }
                if (!has) { optHintR = new Rectangle(x, oy, cw, Theme.S(54)); ry = oy + Theme.S(58); }
                else optHintR = Rectangle.Empty;
                opts = new Rectangle(rx, y, w, ry - y + Theme.S(10));
                cards.Add(opts);
                int my = opts.Bottom + gap;
                if (has)
                {
                    moreLblR = new Rectangle(x, my + Theme.S(16), cw, Theme.S(18));
                    int by = my + Theme.S(42);
                    foreach (Btn b in new[] { bExtract, bSplit, bImages })
                    {
                        b.Visible = true; Place(b, new Rectangle(rx + Theme.S(8), by, w - Theme.S(16), Theme.S(36))); by += Theme.S(38);
                    }
                    fmtLblR = new Rectangle(x, by + Theme.S(6), cw / 2, Theme.S(32));
                    imgFmt.Visible = true; Place(imgFmt, new Rectangle(x + cw / 2, by + Theme.S(4), cw / 2, Theme.S(32))); by += Theme.S(44);
                    more = new Rectangle(rx, my, w, by - my);
                    if (more.Bottom < H - p) cards.Add(more);
                    // protect & stamp: 2 × 2 with words when there's room, one row of icons when the window is short
                    int sy = more.Bottom + gap;
                    Btn[] st = { bLock, bNums, bMark, bSign };
                    bool roomy = sy + Theme.S(42) + Theme.S(38) * 2 + Theme.S(10) <= H - p;
                    stampLblR = new Rectangle(x, sy + Theme.S(16), cw, Theme.S(18));
                    int half = (w - Theme.S(16) - Theme.S(4)) / 2, qw = (w - Theme.S(16) - Theme.S(12)) / 4;
                    for (int i = 0; i < 4; i++)
                    {
                        Btn b = st[i]; b.Visible = true;
                        if (roomy)
                        {
                            b.Label = i == 0 ? (Func<string>)(() => L.T(pdfLocked ? "pdf_unlock" : "pdf_lock")) : i == 1 ? (Func<string>)(() => L.T("pdf_nums")) : i == 2 ? (Func<string>)(() => L.T("pdf_mark")) : () => L.T("pdf_sign");
                            b.AlignStart = true;
                            Place(b, new Rectangle(rx + Theme.S(8) + (i % 2) * (half + Theme.S(4)), sy + Theme.S(42) + (i / 2) * Theme.S(38), half, Theme.S(36)));
                        }
                        else
                        {
                            b.Label = () => ""; b.AlignStart = false;
                            Place(b, new Rectangle(rx + Theme.S(8) + i * (qw + Theme.S(4)), sy + Theme.S(42), qw, Theme.S(36)));
                        }
                    }
                    stampCard = new Rectangle(rx, sy, w, Theme.S(42) + (roomy ? Theme.S(76) : Theme.S(38)) + Theme.S(8));
                    if (stampCard.Bottom <= H - p + Theme.S(4)) cards.Add(stampCard);
                }
                else more = Rectangle.Empty;
            }
            if (bShow.Visible)
            {
                Rectangle last = cards[cards.Count - 1];
                Place(bShow, new Rectangle(rx + Theme.S(8), last.Bottom + Theme.S(10), w - Theme.S(16), Theme.S(36)));
            }
            Invalidate();
        }

        static void Caps(Graphics g, Page pg, string text, Rectangle r, Func<Rectangle, Rectangle> m)
        {
            Theme.Text(g, text.ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, m(r), Align.Start);
        }

        protected override void PaintContent(Graphics g)
        {
            Func<Rectangle, Rectangle> m = r => M(r);
            bool c = Combine;
            string info = "";
            if (c && list.Items.Count > 0)
            {
                int pages = 0; long size = 0; bool all = true;
                foreach (PdfItem it in list.Items) { size += it.Size; if (it.IsPdf) { if (it.Pages > 0) pages += it.Pages; else all = false; } else pages++; }
                info = (list.Items.Count == 1 ? L.T("one_file") : L.T("n_files", list.Items.Count)) + "  ·  " + (all ? L.T("pdf_n_pages", pages) + "  ·  " : "") + Theme.HumanSize(size) + "  ·  " + L.T("pdf_drag_hint");
            }
            else if (!c && pdfPath != null)
            {
                info = Path.GetFileName(shownPath ?? pdfPath) + (pdfLocked ? "  ·  " + L.T("pdf_unlocked_note") : "") + "  ·  " + (grid.Pages.Count == 1 ? L.T("pdf_one_page") : L.T("pdf_n_pages", grid.Pages.Count)) + (Edited ? "  ·  " + L.T("pdf_edited") : "") + "  ·  " + L.T("pdf_grid_hint");
            }
            if (info.Length > 0) Label(g, info, Theme.F(8.75f), Theme.InkSoft, infoR, Align.Start, true, false);
            if (c)
            {
                Caps(g, this, L.T("pdf_size_lbl"), optLblR, m);
                Label(g, L.T(pageSize.SelectedIndex == 2 ? "pdf_size_hint_photo" : "pdf_size_hint_paper"), Theme.F(8.5f), Theme.InkFaint, optHintR, Align.Start, false, true);
            }
            else
            {
                int n = grid.SelectedCount;
                Caps(g, this, pdfPath == null ? L.T("pdf_pages_lbl") : n == 0 ? L.T("pdf_sel_none") : L.T("pdf_sel_n", n, grid.Pages.Count), selLblR, m);
                if (!optHintR.IsEmpty) Label(g, L.T("pdf_pages_hint"), Theme.F(8.5f), Theme.InkFaint, optHintR, Align.Start, false, true);
                if (cards.Contains(more))
                {
                    Caps(g, this, L.T("pdf_more_lbl"), moreLblR, m);
                    Label(g, L.T("pdf_pics_as"), Theme.F(8.75f), Theme.InkSoft, fmtLblR);
                }
                if (!stampCard.IsEmpty) Caps(g, this, L.T("pdf_stamp_lbl"), stampLblR, m);
            }
            if (!statusR.IsEmpty) Label(g, statusText, Theme.F(9f), statusColor, statusR, Align.Start, false, true);
        }

        void SetStatus(string t, Color c) { statusText = t; statusColor = c; DoLayout(); }

        void UpdateState()
        {
            bool c = Combine;
            bool has = c ? list.Items.Count > 0 : pdfPath != null;
            bGo.Enabled = running || has;
            bGo.Kind = running ? BtnKind.Secondary : BtnKind.Primary;
            bGo.Glyph = running ? Ico.Stop : c ? "" : Ico.Save;
            list.Locked = grid.Locked = running;
            mode.Enabled = !running;
            int sel = grid.SelectedCount;
            bRotL.Enabled = bRotR.Enabled = sel > 0 && !running;
            bDel.Enabled = sel > 0 && sel < grid.Pages.Count && !running;
            bExtract.Enabled = sel > 0 && !running;
            bAll.Label = () => grid.SelectedCount == grid.Pages.Count && grid.Pages.Count > 0 ? L.T("pdf_select_none") : L.T("pdf_select_all");
            foreach (Btn b in new[] { bAll, bReset, bSplit, bImages, bAdd, bOpen, bClear, bLock, bNums, bMark, bSign }) b.Enabled = !running;
            pageSize.Enabled = imgFmt.Enabled = !running;
            DoLayout();
            foreach (Control k in Controls) k.Invalidate();
        }

        protected override void DragHighlight(bool on)
        {
            DropZone d = Combine ? dropC : dropP;
            d.DragHot = on; d.Invalidate();
        }

        // ------------------------------------------------------------------ files
        static bool IsPdf(string f) { return Formats.Ext(f) == "pdf"; }
        static bool IsPicture(string f) { return Formats.CategoryOf(Formats.Ext(f)) == Category.Image && Formats.Ext(f) != "pdf"; }

        void BrowseCombine()
        {
            if (running) return;
            using (OpenFileDialog d = new OpenFileDialog { Multiselect = true, Title = L.T("pdf_drop_c") })
            {
                List<string> pats = new List<string> { "*.pdf" };
                foreach (string e in Formats.ImageExt) if (e != "pdf") pats.Add("*." + e);
                d.Filter = L.T("pdf_and_photos") + "|" + string.Join(";", pats.ToArray()) + "|" + L.T("all_files") + "|*.*";
                if (LastDir.Show(d, FindForm(), "pdf") == DialogResult.OK) AddCombine(d.FileNames);
            }
        }

        void BrowsePdf()
        {
            if (running) return;
            using (OpenFileDialog d = new OpenFileDialog { Title = L.T("pdf_drop_p"), Filter = "PDF|*.pdf|" + L.T("all_files") + "|*.*" })
                if (LastDir.Show(d, FindForm(), "pdf") == DialogResult.OK) OpenPdf(d.FileName);
        }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0 || running) return;
            List<string> files = new List<string>();
            foreach (string p in paths)
                try { if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p)); else if (File.Exists(p)) files.Add(p); } catch { }
            files.RemoveAll(f => !IsPdf(f) && !IsPicture(f));
            if (files.Count == 0) { ShowToast(L.T("pdf_not_pdf"), false, null, null); return; }
            // one PDF dropped on "Edit pages" (or an empty "Combine" list): open it to edit its pages
            if (files.Count == 1 && IsPdf(files[0]) && !Combine) { OpenPdf(files[0]); return; }
            if (!Combine) mode.SelectedIndex = 0;
            AddCombine(files.ToArray());
        }

        void AddCombine(string[] paths)
        {
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PdfItem it in list.Items) have.Add(it.Path);
            List<PdfItem> added = new List<PdfItem>();
            foreach (string p in paths)
            {
                if (!File.Exists(p) || have.Contains(p) || (!IsPdf(p) && !IsPicture(p))) continue;
                long sz = 0; try { sz = new FileInfo(p).Length; } catch { }
                PdfItem it = new PdfItem { Path = Path.GetFullPath(p), Name = Path.GetFileName(p), Size = sz, IsPdf = IsPdf(p) };
                list.Items.Add(it); added.Add(it); have.Add(p);
            }
            list.Invalidate(); UpdateState();
            // page counts and small pictures, in the background
            Thread t = new Thread(() =>
            {
                foreach (PdfItem it in added)
                {
                    try
                    {
                        if (it.IsPdf) it.Pages = Qpdf.Pages(it.Path);
                        else { Image im = ViewerPage.Decode(it.Path, 160); it.Thumb = new Bitmap(im); im.Dispose(); }
                    }
                    catch { }
                    Post(() => { list.Invalidate(); Invalidate(); });
                }
            });
            t.IsBackground = true; t.Start();
        }

        public void OpenPdf(string path)
        {
            if (running || !File.Exists(path)) return;
            bool locked = Qpdf.LooksLocked(path);
            if (locked && !Qpdf.Ready && !Ask.Confirm(FindForm(), L.T("pdf_need_title"), L.T("pdf_need_unlock"), L.T("install"), L.T("not_now"), AskKind.Info)) return;
            if (Combine) mode.SetSilently(1);
            ClosePdf();
            pdfPath = Path.GetFullPath(path);
            shownPath = pdfPath;
            lastOutput = null; bShow.Visible = false;
            int token = ++loadToken;
            thumbsDir = Path.Combine(Path.GetTempPath(), "FadiPdf_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            string dir = thumbsDir, pdf = pdfPath;
            SetStatus(L.T("pdf_loading"), Theme.InkSoft);
            UpdateState();
            Thread t = new Thread(() =>
            {
                string err = null;
                try
                {
                    if (locked)
                    {
                        // a locked PDF: open an unlocked copy (asks for the password when it needs one)
                        Directory.CreateDirectory(dir);
                        EnsureQpdfInWorker(null);
                        string open = Unlock(pdf, Path.Combine(dir, "unlocked.pdf"), token);
                        if (open == null) { Post(() => { if (token == loadToken) { ClosePdf(); SetStatus(L.T("pdf_unlock_cancel"), Theme.InkSoft); UpdateState(); } }); return; }
                        if (open != pdf) { pdf = open; Post(() => { if (token == loadToken) { pdfPath = open; pdfLocked = true; UpdateState(); } }); }
                    }
                    int known = Qpdf.Ready ? Qpdf.Pages(pdf) : -1;
                    if (known > 0) Post(() => { if (token == loadToken) { EnsurePages(known); pdfPages = known; UpdateState(); } });
                    DocPages.RenderTo(pdf, dir, 240, (done, total) =>
                    {
                        if (token != loadToken) return;
                        string f = Path.Combine(dir, "page" + done.ToString("D4") + ".png");
                        Image im = null;
                        try { using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) im = new Bitmap(Image.FromStream(fs)); } catch { }
                        Post(() =>
                        {
                            if (token != loadToken) { if (im != null) im.Dispose(); return; }
                            EnsurePages(Math.Max(total, done));
                            if (pdfPages < Math.Max(total, done)) pdfPages = Math.Max(total, done);
                            PdfPageItem pi = grid.Pages.Find(x => x.Src == done);
                            if (pi != null) { if (pi.Thumb != null) pi.Thumb.Dispose(); pi.Thumb = im; } else if (im != null) im.Dispose();
                            grid.Invalidate();
                            statusText = L.T("pdf_loading_n", done, Math.Max(total, done)); Invalidate(M(statusR));
                        });
                    }, () => token != loadToken || IsDisposed || Tools.ShuttingDown);
                }
                catch (Exception ex) { err = ex.Message; }
                Post(() =>
                {
                    if (token != loadToken) return;
                    if (grid.Pages.Count == 0) { ClosePdf(); SetStatus(err ?? L.T("doc_failed"), Theme.Error); UpdateState(); return; }
                    SetStatus("", Theme.InkSoft); UpdateState();
                });
            });
            t.IsBackground = true; t.Start();
        }

        /// Adds placeholders for pages we now know about (pages removed by hand don't come back).
        void EnsurePages(int n)
        {
            if (n <= created) return;
            for (int i = created; i < n; i++) grid.Pages.Add(new PdfPageItem { Src = i + 1 });
            created = n;
            grid.Invalidate();
        }

        void ResetPages()
        {
            if (running || pdfPath == null) return;
            Dictionary<int, Image> thumbs = new Dictionary<int, Image>();
            foreach (PdfPageItem p in grid.Pages) if (p.Thumb != null) thumbs[p.Src] = p.Thumb;
            grid.Pages.Clear();
            for (int i = 1; i <= pdfPages; i++)
            {
                Image im; thumbs.TryGetValue(i, out im);
                if (im == null && thumbsDir != null)
                    try { using (FileStream fs = new FileStream(Path.Combine(thumbsDir, "page" + i.ToString("D4") + ".png"), FileMode.Open, FileAccess.Read)) im = new Bitmap(Image.FromStream(fs)); } catch { }
                grid.Pages.Add(new PdfPageItem { Src = i, Thumb = im });
            }
            grid.Invalidate(); UpdateState();
        }

        // ------------------------------------------------------------------ doing the work
        /// Makes sure qpdf is here (asks first, then downloads it). Returns false when it isn't.
        bool NeedQpdf()
        {
            if (Qpdf.Ready) return true;
            if (!Ask.Confirm(FindForm(), L.T("pdf_need_title"), L.T("pdf_need_q"), L.T("install"), L.T("not_now"), AskKind.Info)) return false;
            return true;   // the worker downloads it
        }

        void EnsureQpdfInWorker(ActJob act)
        {
            if (Qpdf.Ready) return;
            Post(() => SetStatus(L.T("pdf_dl"), Theme.InkSoft));
            Qpdf.Install((got, total) =>
            {
                double f = total > 0 ? got / (double)total : -1;
                Activity.Update(act, f, L.T("pdf_dl_short"));
                Post(() => { progress.Value = Math.Max(0, f); statusText = L.T("pdf_dl_n", Theme.HumanSize(got), total > 0 ? Theme.HumanSize(total) : "?"); Invalidate(M(statusR)); });
            }, () => cancel);
            if (!Qpdf.Ready) throw new UserError(L.T("pdf_need_qpdf"));
        }

        void Go()
        {
            if (running) return;
            if (Combine) DoCombine(); else Export(0);
        }

        void BeginWork(string kind, string title)
        {
            running = true; cancel = false; lastOutput = null; bShow.Visible = false;
            progress.Visible = true; progress.Value = 0;
            SetStatus(L.T("starting"), Theme.InkSoft);
            UpdateState();
        }

        void EndWork(ActJob act, string err, string output, string doneText)
        {
            running = false;
            progress.Visible = false;
            Activity.End(act, err, cancel, doneText, output);
            if (cancel) SetStatus(L.T("cancelled"), Theme.InkSoft);
            else if (err != null) SetStatus(err, Theme.Error);
            else
            {
                lastOutput = output; bShow.Visible = true;
                SetStatus(doneText, Theme.Success);
                ShowToast(doneText, true, () => L.T(File.Exists(output) ? "show_file" : "open_folder"), () => ActivityRows.ShowOutput(output));
                if (Settings.OpenAfter) ActivityRows.ShowOutput(output);
            }
            UpdateState();
        }

        void DoCombine()
        {
            if (list.Items.Count == 0) return;
            List<PdfItem> items = new List<PdfItem>(list.Items);
            bool anyPdf = items.Exists(i => i.IsPdf);
            if (anyPdf && !NeedQpdf()) return;
            string first = Path.GetFileNameWithoutExtension(items[0].Path);
            string outp = SaveAsk.File(FindForm(), first + (items.Count > 1 ? " " + L.T("pdf_combined_suffix") : "") + ".pdf", "pdf");
            if (outp == null) return;
            ImagePdf.PageSize size = pageSize.SelectedIndex == 0 ? ImagePdf.PageSize.A4 : pageSize.SelectedIndex == 1 ? ImagePdf.PageSize.Letter : ImagePdf.PageSize.Photo;
            BeginWork("", "");
            ActJob act = Activity.Begin(this, L.T("act_k_pdf_combine"), Path.GetFileName(outp), () => cancel = true);
            Thread t = new Thread(() =>
            {
                string err = null;
                string tmpDir = Path.Combine(Path.GetTempPath(), "FadiCombine_" + Guid.NewGuid().ToString("N").Substring(0, 10));
                try
                {
                    Directory.CreateDirectory(tmpDir);
                    if (anyPdf) EnsureQpdfInWorker(act);
                    // runs of photos become one PDF each; PDFs are used as they are
                    List<string> parts = new List<string>();
                    int done = 0;
                    for (int i = 0; i < items.Count && !cancel; )
                    {
                        if (items[i].IsPdf) { parts.Add(items[i].Path); i++; done++; continue; }
                        int j = i; while (j < items.Count && !items[j].IsPdf) j++;
                        List<PdfItem> run = items.GetRange(i, j - i);
                        string part = anyPdf ? Path.Combine(tmpDir, "photos" + parts.Count + ".pdf") : outp + ".tmp";
                        int baseDone = done;
                        ImagePdf.Write(run.Count, k => { using (Image im = ViewerPage.Decode(run[k].Path, 8000)) return new Bitmap(im); }, true, part, 90, size, (n, total) =>
                        {
                            double f = (baseDone + n) / (double)items.Count * (anyPdf ? 0.8 : 1);
                            Activity.Update(act, f, (baseDone + n) + "/" + items.Count);
                            Post(() => { progress.Value = f; statusText = L.T("pdf_adding", baseDone + n, items.Count); Invalidate(M(statusR)); });
                        });
                        parts.Add(part); done += run.Count; i = j;
                    }
                    if (cancel) throw new OperationCanceledException();
                    if (anyPdf)
                    {
                        Post(() => SetStatus(L.T("pdf_joining"), Theme.InkSoft));
                        StringBuilder a = new StringBuilder("--empty --pages");
                        foreach (string p in parts) a.Append(' ').Append(Tools.Quote(p));
                        a.Append(" -- ").Append(Tools.Quote(outp + ".tmp"));
                        string e = Qpdf.Run(a.ToString());
                        if (e != null) throw new UserError(e);
                    }
                    if (File.Exists(outp)) File.Delete(outp);
                    File.Move(outp + ".tmp", outp);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                try { if (File.Exists(outp + ".tmp")) File.Delete(outp + ".tmp"); } catch { }
                try { Directory.Delete(tmpDir, true); } catch { }
                int pages = Qpdf.Ready && err == null && !cancel ? Qpdf.Pages(outp) : -1;
                Post(() => EndWork(act, err, outp, L.T("pdf_done_combine", Path.GetFileName(outp)) + (pages > 0 ? "  ·  " + L.T("pdf_n_pages", pages) : "")));
            });
            t.IsBackground = true; t.Start();
        }

        /// Makes an unlocked copy of a locked PDF. Returns the file to open (the same one when it isn't really locked),
        /// or null when the person gave up on the password.
        string Unlock(string pdf, string outp, int token)
        {
            int lk = Qpdf.Lock(pdf);
            if (lk == 2) return pdf;
            if (lk == 3)
            {
                string e = Qpdf.Run("--decrypt " + Tools.Quote(pdf) + " " + Tools.Quote(outp));
                if (e != null) throw new UserError(e);
                return outp;
            }
            string msg = L.T("pdf_pw_msg", Path.GetFileName(pdf));
            for (int tries = 0; tries < 6; tries++)
            {
                string pw = null;
                try { Invoke((Action)(() => { if (token == loadToken) pw = Ask.Input(FindForm(), L.T("pdf_pw_title"), msg, "", L.T("pdf_pw_ph"), L.T("pdf_pw_open"), L.T("cancel"), null, null); })); }
                catch { return null; }
                if (pw == null || token != loadToken) return null;
                if (pw.IndexOf('"') >= 0) { msg = L.T("pdf_pw_wrong"); continue; }
                string e = Qpdf.Run("--decrypt " + Tools.Quote("--password=" + pw) + " " + Tools.Quote(pdf) + " " + Tools.Quote(outp));
                if (e == null && File.Exists(outp)) return outp;
                msg = L.T("pdf_pw_wrong");
            }
            return null;
        }

        /// The pages in their new order and turns, as one PDF in tmp (or the original when nothing changed).
        string MakeWork(string src, List<PdfPageItem> pages, bool edited, string tmp)
        {
            if (!edited) return src;
            string work = Path.Combine(tmp, "edited.pdf");
            List<int> order = new List<int>(); foreach (PdfPageItem p in pages) order.Add(p.Src);
            StringBuilder a = new StringBuilder();
            a.Append(Tools.Quote(src)).Append(" --pages ").Append(Tools.Quote(src)).Append(' ').Append(Qpdf.Ranges(order)).Append(" --");
            foreach (int rot in new[] { 90, 180, 270 })
            {
                List<int> at = new List<int>();
                for (int i = 0; i < pages.Count; i++) if (pages[i].Rot == rot) at.Add(i + 1);
                if (at.Count > 0) a.Append(" --rotate=+").Append(rot).Append(':').Append(Qpdf.Ranges(at));
            }
            a.Append(' ').Append(Tools.Quote(work));
            string e = Qpdf.Run(a.ToString());
            if (e != null) throw new UserError(e);
            return work;
        }

        /// The size of each page as it will look (from its picture), for laying things over it.
        List<PdfStamp.PageSpec> Specs(List<PdfPageItem> pages)
        {
            List<PdfStamp.PageSpec> l = new List<PdfStamp.PageSpec>();
            foreach (PdfPageItem p in pages)
            {
                float w = 612, h = 792;
                try { if (p.Thumb != null) { w = p.Thumb.Width; h = p.Thumb.Height; } } catch { }
                if (p.Rot % 180 == 90) { float t = w; w = h; h = t; }
                l.Add(new PdfStamp.PageSpec { W = 612, H = 612f * h / Math.Max(1, w) });
            }
            return l;
        }

        /// Saves the PDF (with its page changes) after doing something to it: work → result. Runs in the background.
        void SaveWith(string suffixKey, string kindKey, Func<string, string, List<PdfPageItem>, string> transform, Func<List<PdfPageItem>, string> doneText)
        {
            if (running || pdfPath == null || grid.Pages.Count == 0) return;
            if (!NeedQpdf()) return;
            List<PdfPageItem> pages = new List<PdfPageItem>(grid.Pages);
            bool edited = Edited;
            string stem = Path.GetFileNameWithoutExtension(shownPath ?? pdfPath);
            string outp = SaveAsk.File(FindForm(), stem + " " + L.T(suffixKey) + ".pdf", "pdf");
            if (outp == null) return;
            string src = pdfPath;
            BeginWork("", "");
            ActJob act = Activity.Begin(this, L.T(kindKey), Path.GetFileName(shownPath ?? src), () => cancel = true);
            Thread t = new Thread(() =>
            {
                string err = null, done = null;
                string tmp = Path.Combine(Path.GetTempPath(), "FadiPdfOut_" + Guid.NewGuid().ToString("N").Substring(0, 10));
                try
                {
                    Directory.CreateDirectory(tmp);
                    EnsureQpdfInWorker(act);
                    Post(() => SetStatus(L.T("pdf_working"), Theme.InkSoft));
                    string work = MakeWork(src, pages, edited, tmp);
                    if (cancel) throw new OperationCanceledException();
                    string res = Path.Combine(tmp, "result.pdf");
                    string e = transform(work, res, pages);
                    if (e != null) throw new UserError(e);
                    if (cancel) throw new OperationCanceledException();
                    if (File.Exists(outp)) File.Delete(outp);
                    File.Copy(File.Exists(res) ? res : work, outp);
                    done = doneText(pages);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                try { Directory.Delete(tmp, true); } catch { }
                Post(() => EndWork(act, err, outp, done ?? ""));
            });
            t.IsBackground = true; t.Start();
        }

        void LockOrUnlock()
        {
            if (pdfPath == null || running) return;
            if (pdfLocked)
            {
                // the open copy is already unlocked: saving it is all it takes
                SaveWith("pdf_unlocked_suffix", "act_k_pdf_unlock", (w, r, pg) => null, pg => L.T("pdf_done_unlock"));
                return;
            }
            TextField a = new TextField(() => L.T("pdf_pw_ph"), "\uE72E") { ForceLtr = true };
            TextField b = new TextField(() => L.T("pdf_pw_again_ph"), "\uE72E") { ForceLtr = true };
            using (FormDialog d = new FormDialog(L.T("pdf_lock_title"), L.T("pdf_lock_msg"), L.T("pdf_lock_ok")))
            {
                d.Add(() => L.T("pdf_pw"), a, 40);
                d.Add(() => L.T("pdf_pw_again"), b, 40);
                d.Validate2 = () =>
                {
                    string p1 = a.Box.Text, p2 = b.Box.Text;
                    string why = p1.Length == 0 ? L.T("pdf_pw_empty") : p1 != p2 ? L.T("pdf_pw_differ") : p1.IndexOf('"') >= 0 ? L.T("pdf_pw_quote") : null;
                    if (why != null) Ask.Info(d, L.T("pdf_lock_title"), why, AskKind.Warning);
                    return why == null;
                };
                if (Ask.ShowDimmed(d, FindForm()) != DialogResult.OK) return;
            }
            string pw = a.Box.Text;
            SaveWith("pdf_locked_suffix", "act_k_pdf_lock", (w, r, pg) =>
                Qpdf.Run(Tools.Quote(w) + " --encrypt " + Tools.Quote(pw) + " " + Tools.Quote(pw) + " 256 -- " + Tools.Quote(r)),
                pg => L.T("pdf_done_lock"));
        }

        void AddNumbers()
        {
            if (pdfPath == null || running) return;
            Segments style = new Segments(() => "3", () => "3 / 12", () => L.T("pdf_num_text", 3, 12)) { FontSize = 9f };
            Segments where = new Segments(() => L.T("pdf_num_bottom"), () => L.T("pdf_num_corner"), () => L.T("pdf_num_top")) { FontSize = 9f };
            Toggle skip = new Toggle();
            int s0, w0; int.TryParse(Settings.Get("PdfNumStyle", "1"), out s0); int.TryParse(Settings.Get("PdfNumWhere", "0"), out w0);
            style.SetSilently(Math.Max(0, Math.Min(2, s0))); where.SetSilently(Math.Max(0, Math.Min(2, w0)));
            skip.SetSilently(Settings.Get("PdfNumSkip", "0") == "1");
            using (FormDialog d = new FormDialog(L.T("pdf_nums_title"), L.T("pdf_nums_msg"), L.T("pdf_nums_ok")))
            {
                d.Add(() => L.T("pdf_num_style"), style, 36);
                d.Add(() => L.T("pdf_num_where"), where, 36);
                d.Add(() => L.T("pdf_num_skip"), skip, 34);
                if (Ask.ShowDimmed(d, FindForm()) != DialogResult.OK) return;
            }
            int st = style.SelectedIndex, wh = where.SelectedIndex; bool sk = skip.Checked;
            Settings.Set("PdfNumStyle", st.ToString()); Settings.Set("PdfNumWhere", wh.ToString()); Settings.Set("PdfNumSkip", sk ? "1" : "0");
            SaveWith("pdf_nums_suffix", "act_k_pdf_nums", (w, r, pg) =>
            {
                List<PdfStamp.PageSpec> specs = Specs(pg);
                for (int i = 0; i < specs.Count; i++)
                    if (!(sk && i == 0)) specs[i].Items.Add(PdfStamp.PageNumber(specs[i], i + 1, specs.Count, st, wh));
                return PdfStamp.Apply(w, r, specs);
            }, pg => L.T("pdf_done_nums", pg.Count));
        }

        static readonly Color[] MarkColors = { Color.FromArgb(120, 120, 128), Color.FromArgb(220, 38, 38), Color.FromArgb(37, 99, 235), Color.Black };

        void AddWatermark()
        {
            if (pdfPath == null || running) return;
            TextField text = new TextField(() => L.T("pdf_mark_ph"), null);
            text.Box.Text = Settings.Get("PdfMarkText", L.T("pdf_mark_default"));
            Segments where = new Segments(() => L.T("pdf_mark_across"), () => L.T("pdf_mark_middle"), () => L.T("pdf_mark_top")) { FontSize = 9f };
            Segments strength = new Segments(() => L.T("pdf_mark_light"), () => L.T("pdf_mark_medium"), () => L.T("pdf_mark_strong")) { FontSize = 9f };
            Swatches color = new Swatches { Palette = MarkColors, LivePreview = false };
            int w0, s0, c0; int.TryParse(Settings.Get("PdfMarkWhere", "0"), out w0); int.TryParse(Settings.Get("PdfMarkStrength", "1"), out s0); int.TryParse(Settings.Get("PdfMarkColor", "0"), out c0);
            where.SetSilently(Math.Max(0, Math.Min(2, w0))); strength.SetSilently(Math.Max(0, Math.Min(2, s0)));
            color.Selected = MarkColors[Math.Max(0, Math.Min(MarkColors.Length - 1, c0))];
            using (FormDialog d = new FormDialog(L.T("pdf_mark_title"), L.T("pdf_mark_msg"), L.T("pdf_mark_ok")))
            {
                d.Add(() => L.T("pdf_mark_text"), text, 40);
                d.Add(() => L.T("pdf_mark_where"), where, 36);
                d.Add(() => L.T("pdf_mark_strength"), strength, 36);
                d.Add(() => L.T("pdf_mark_color"), color, 34);
                d.Validate2 = () => text.Box.Text.Trim().Length > 0;
                if (Ask.ShowDimmed(d, FindForm()) != DialogResult.OK) return;
            }
            string t = text.Box.Text.Trim(); int wh = where.SelectedIndex, sg = strength.SelectedIndex;
            Color col = color.Selected;
            int ci = Array.FindIndex(MarkColors, c => c.ToArgb() == col.ToArgb());
            Settings.Set("PdfMarkText", t); Settings.Set("PdfMarkWhere", wh.ToString()); Settings.Set("PdfMarkStrength", sg.ToString()); Settings.Set("PdfMarkColor", Math.Max(0, ci).ToString());
            SaveWith("pdf_mark_suffix", "act_k_pdf_mark", (w, r, pg) =>
            {
                List<PdfStamp.PageSpec> specs = Specs(pg);
                // pages of the same shape share one picture (keeps the file small)
                Dictionary<string, Bitmap> made = new Dictionary<string, Bitmap>();
                foreach (PdfStamp.PageSpec sp in specs)
                {
                    string key = Math.Round(sp.H).ToString();
                    PdfStamp.Item it;
                    Bitmap have;
                    if (made.TryGetValue(key, out have)) it = new PdfStamp.Item { Image = have, Box = wh == 2 ? new RectangleF(0.06f, 0.02f, 0.88f, 0.05f) : new RectangleF(0, 0, 1, 1) };
                    else { it = PdfStamp.Watermark(sp, t, col, wh, sg); made[key] = it.Image; }
                    sp.Items.Add(it);
                }
                return PdfStamp.Apply(w, r, specs);
            }, pg => L.T("pdf_done_mark"));
        }

        void SignIt()
        {
            if (pdfPath == null || running || grid.Pages.Count == 0) return;
            List<int> sel = new List<int>();
            for (int i = 0; i < grid.Pages.Count; i++) if (grid.Pages[i].Sel) sel.Add(i);
            bool hasSel = sel.Count > 0;
            int previewAt = hasSel ? sel[0] : grid.Pages.Count - 1;
            Image pv = grid.Pages[previewAt].Thumb;
            Bitmap shown = null;
            if (pv != null)
            {
                // the page as it will look (turned like in the grid)
                shown = new Bitmap(pv);
                int rot = grid.Pages[previewAt].Rot;
                if (rot == 90) shown.RotateFlip(RotateFlipType.Rotate90FlipNone);
                else if (rot == 180) shown.RotateFlip(RotateFlipType.Rotate180FlipNone);
                else if (rot == 270) shown.RotateFlip(RotateFlipType.Rotate270FlipNone);
            }
            Bitmap sign; RectangleF box; int on;
            using (SignDialog d = new SignDialog(shown, hasSel))
            {
                if (Ask.ShowDimmed(d, FindForm()) != DialogResult.OK) { if (shown != null) shown.Dispose(); return; }
                sign = d.Pad.Export(); box = d.Place.Box; on = d.Where.SelectedIndex;
            }
            if (shown != null) shown.Dispose();
            if (sign == null) return;
            int count = grid.Pages.Count;
            HashSet<int> targets = new HashSet<int>();
            if (hasSel) { if (on == 0) foreach (int i in sel) targets.Add(i); else if (on == 1) targets.Add(count - 1); else for (int i = 0; i < count; i++) targets.Add(i); }
            else { if (on == 0) targets.Add(count - 1); else if (on == 1) targets.Add(0); else for (int i = 0; i < count; i++) targets.Add(i); }
            SaveWith("pdf_sign_suffix", "act_k_pdf_sign", (w, r, pg) =>
            {
                List<PdfStamp.PageSpec> specs = Specs(pg);
                for (int i = 0; i < specs.Count; i++)
                    if (targets.Contains(i)) specs[i].Items.Add(new PdfStamp.Item { Image = sign, Box = box });
                return PdfStamp.Apply(w, r, specs);
            }, pg => L.T("pdf_done_sign", targets.Count));
        }

        /// 0 = save the edited PDF, 1 = selected pages as a new PDF, 2 = every page as its own PDF, 3 = pages as pictures.
        void Export(int what)
        {
            if (running || pdfPath == null || grid.Pages.Count == 0) return;
            List<PdfPageItem> pages = what == 1 ? grid.Pages.FindAll(p => p.Sel) : new List<PdfPageItem>(grid.Pages);
            if (pages.Count == 0) return;
            bool edited = what == 1 || Edited;
            bool needQ = what <= 2 || edited;
            if (needQ && !NeedQpdf()) return;
            string stem = Path.GetFileNameWithoutExtension(shownPath ?? pdfPath);
            string outp, outDir = null;
            if (what <= 1)
            {
                outp = SaveAsk.File(FindForm(), stem + " " + L.T(what == 1 ? "pdf_extract_suffix" : "edited_suffix") + ".pdf", "pdf");
                if (outp == null) return;
            }
            else
            {
                outDir = SaveAsk.Folder(FindForm());
                if (outDir == null) return;
                outp = outDir;
            }
            string src = pdfPath;
            bool png = imgFmt.SelectedIndex == 1;
            BeginWork("", "");
            ActJob act = Activity.Begin(this, L.T(what == 3 ? "act_k_pdf_images" : what == 2 ? "act_k_pdf_split" : "act_k_pdf_save"), Path.GetFileName(src), () => cancel = true);
            Thread t = new Thread(() =>
            {
                string err = null, result = outp, doneText = null;
                string tmp = Path.Combine(Path.GetTempPath(), "FadiPdfOut_" + Guid.NewGuid().ToString("N").Substring(0, 10));
                try
                {
                    Directory.CreateDirectory(tmp);
                    if (needQ) EnsureQpdfInWorker(act);
                    Post(() => SetStatus(L.T("pdf_working"), Theme.InkSoft));
                    // the pages in their new order and turns, as one PDF
                    string work = src;
                    if (edited)
                    {
                        work = Path.Combine(tmp, "edited.pdf");
                        List<int> order = new List<int>(); foreach (PdfPageItem p in pages) order.Add(p.Src);
                        StringBuilder a = new StringBuilder();
                        a.Append(Tools.Quote(src)).Append(" --pages ").Append(Tools.Quote(src)).Append(' ').Append(Qpdf.Ranges(order)).Append(" --");
                        foreach (int rot in new[] { 90, 180, 270 })
                        {
                            List<int> at = new List<int>();
                            for (int i = 0; i < pages.Count; i++) if (pages[i].Rot == rot) at.Add(i + 1);
                            if (at.Count > 0) a.Append(" --rotate=+").Append(rot).Append(':').Append(Qpdf.Ranges(at));
                        }
                        a.Append(' ').Append(Tools.Quote(work));
                        string e = Qpdf.Run(a.ToString());
                        if (e != null) throw new UserError(e);
                    }
                    if (cancel) throw new OperationCanceledException();
                    if (what <= 1)
                    {
                        if (File.Exists(outp)) File.Delete(outp);
                        File.Copy(work, outp);
                        doneText = L.T("pdf_done_save", Path.GetFileName(outp)) + "  ·  " + (pages.Count == 1 ? L.T("pdf_one_page") : L.T("pdf_n_pages", pages.Count));
                    }
                    else if (what == 2)
                    {
                        string pattern = Path.Combine(tmp, "split - %d.pdf");
                        string e = Qpdf.Run("--split-pages " + Tools.Quote(work) + " " + Tools.Quote(pattern));
                        if (e != null) throw new UserError(e);
                        List<string> got = new List<string>(Directory.GetFiles(tmp, "split - *.pdf"));
                        got.Sort(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < got.Count; i++)
                        {
                            string dest = Paths.UniquePath(outDir, stem + " - " + L.T("doc_page", i + 1), "pdf");
                            File.Move(got[i], dest);
                        }
                        doneText = L.T("pdf_done_split", got.Count);
                    }
                    else
                    {
                        string rdir = Path.Combine(tmp, "render");
                        List<string> pics = DocPages.RenderTo(work, rdir, 2400, (done, total) =>
                        {
                            double f = total > 0 ? done / (double)total : -1;
                            Activity.Update(act, f, done + "/" + Math.Max(done, total));
                            Post(() => { progress.Value = Math.Max(0, f); statusText = L.T("pdf_rendering", done, Math.Max(done, total)); Invalidate(M(statusR)); });
                        }, () => cancel);
                        if (cancel) throw new OperationCanceledException();
                        if (pics.Count == 0) throw new UserError(L.T("doc_failed"));
                        System.Drawing.Imaging.ImageCodecInfo jpg = null;
                        foreach (var c in System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()) if (c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid) jpg = c;
                        for (int i = 0; i < pics.Count; i++)
                        {
                            string dest = Paths.UniquePath(outDir, stem + " - " + L.T("doc_page", i + 1), png ? "png" : "jpg");
                            if (png) File.Move(pics[i], dest);
                            else
                                using (Image im = Image.FromFile(pics[i]))
                                using (Bitmap flat = new Bitmap(im.Width, im.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                                using (System.Drawing.Imaging.EncoderParameters ep = new System.Drawing.Imaging.EncoderParameters(1))
                                {
                                    using (Graphics g = Graphics.FromImage(flat)) { g.Clear(Color.White); g.DrawImage(im, 0, 0, im.Width, im.Height); }
                                    ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                                    flat.Save(dest, jpg, ep);
                                }
                        }
                        doneText = L.T("pdf_done_images", pics.Count);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                try { Directory.Delete(tmp, true); } catch { }
                Post(() => EndWork(act, err, result, doneText ?? ""));
            });
            t.IsBackground = true; t.Start();
        }
    }
}
