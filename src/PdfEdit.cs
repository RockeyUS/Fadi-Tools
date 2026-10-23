using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// One change on a PDF page: a line of text replaced (or removed), new text added, or an area wiped clean.
    /// Box is in pixels of the page's picture.
    public class TextEdit
    {
        public int Page;
        public RectangleF Box;
        public string Original = "", Text = "";
        public float Size = 20;          // letter height in page pixels
        public bool Bold, Serif, Added, Area, Rtl, Touched;
        public Color Ink = Color.Black, Paper = Color.White;
        public bool Erased { get { return !Added && Text.Trim().Length == 0; } }
    }

    /// A line of text found on a page (by Windows' own text reader).
    public class FoundLine { public RectangleF Box; public string Text; }

    /// Studio → PDF → Edit text: the page as it looks, with every line of text you can click and type over.
    /// "Add text" writes something new where you click; "Erase" wipes an area you drag over. Saved as a new PDF:
    /// everything you didn't touch stays exactly as it was (sharp text, links, pages), the changes are laid on top.
    public class PdfTextEditor : UiControl
    {
        public event Action Closed;
        /// Save: the changes and each page picture's size.
        public event Action<List<TextEdit>, List<Size>> SaveRequested;
        public Action<string, bool> Toast;


        List<string> pngs = new List<string>();
        readonly List<Size> sizes = new List<Size>();
        readonly Dictionary<int, List<FoundLine>> found = new Dictionary<int, List<FoundLine>>();
        readonly HashSet<int> reading = new HashSet<int>();
        public readonly List<TextEdit> Edits = new List<TextEdit>();
        int page;
        Bitmap pic;          // the page shown
        string status;
        int mode;            // 0 edit text, 1 add text, 2 erase
        TextEdit sel;        // the change being worked on
        FoundLine hotLine; TextEdit hotEdit;
        Point dragFrom; bool dragging; RectangleF dragBox;
        float scroll;        // pixels the page is scrolled down

        readonly Btn bBack, bPrev, bNext, bSave, bUndo, bErase;
        readonly Segments modes;
        readonly TextBox box = new TextBox { BorderStyle = BorderStyle.None, Visible = false, Multiline = false };
        readonly Slider size;
        readonly Toggle bold;
        readonly Segments font;
        readonly Swatches ink;
        Rectangle canvasR, sideR, pageNavR, sizeLbl, boldLbl, fontLbl, inkLbl, hintR;

        /// A line's box (from the top of its tallest letter to the bottom of its lowest) is a bit less than the letter size.
        const float LetterPerBox = 1.07f;

        public PdfTextEditor()
        {
            Surface = Surface.Background;
            bBack = new Btn(() => L.T("pe_back"), Ico.Back, BtnKind.Secondary);
            bBack.Click += (s, e) => { Commit(); if (Edits.Count > 0 && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("pe_discard"), L.T("pe_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return; if (Closed != null) Closed(); };
            bPrev = new Btn(() => "", Ico.Left, BtnKind.Ghost) { AccessibleName = L.T("pe_prev") };
            bPrev.Click += (s, e) => GoPage(page - 1);
            bNext = new Btn(() => "", Ico.Right, BtnKind.Ghost) { AccessibleName = L.T("pe_next") };
            bNext.Click += (s, e) => GoPage(page + 1);
            bSave = new Btn(() => L.T("pe_save"), Ico.Save, BtnKind.Primary) { Enabled = false };
            bSave.Click += (s, e) => { Commit(); if (Edits.Count > 0 && SaveRequested != null) SaveRequested(new List<TextEdit>(Edits), new List<Size>(sizes)); };
            modes = new Segments(() => L.T("pe_mode_edit"), () => L.T("pe_mode_add"), () => L.T("pe_mode_erase")) { Glyphs = new[] { Ico.Edit, Ico.Add, "" } };
            modes.SelectedChanged += (s, e) => { Commit(); mode = modes.SelectedIndex; sel = null; SyncSide(); Invalidate(); };
            bUndo = new Btn(() => L.T("pe_undo"), Ico.Refresh, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bUndo.Click += (s, e) => { if (sel != null) { Edits.Remove(sel); sel = null; box.Visible = false; Changed(); } };
            bErase = new Btn(() => L.T("pe_erase_line"), "", BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bErase.Click += (s, e) => { if (sel != null) { sel.Text = ""; box.Text = ""; Commit(); } };
            size = new Slider { Surface = Surface.Card, Min = 6, Max = 120, Format = v => ((int)Math.Round(v)) + " px" };
            size.ValueChanged += (s, e) => { if (sel != null) { sel.Size = size.Value; sel.Touched = true; PlaceBox(); Invalidate(); } };
            bold = new Toggle { Surface = Surface.Card };
            bold.CheckedChanged += (s, e) => { if (sel != null) { sel.Bold = bold.Checked; sel.Touched = true; PlaceBox(); Invalidate(); } };
            font = new Segments(() => L.T("pe_sans"), () => L.T("pe_serif")) { Surface = Surface.Card, FontSize = 9f };
            font.SelectedChanged += (s, e) => { if (sel != null) { sel.Serif = font.SelectedIndex == 1; sel.Touched = true; PlaceBox(); Invalidate(); } };
            ink = new Swatches { Surface = Surface.Card, Palette = new[] { Color.Black, Color.FromArgb(64, 64, 64), Color.FromArgb(37, 99, 235), Color.FromArgb(220, 38, 38), Color.FromArgb(22, 128, 61), Color.White } };
            ink.SelectedChanged += (s, e) => { if (sel != null) { sel.Ink = ink.Selected; sel.Touched = true; Invalidate(); } };
            box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Commit(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Cancel(); }
            };
            box.TextChanged += (s, e) => { if (sel != null) { sel.Text = box.Text; Invalidate(); } };
            Controls.AddRange(new Control[] { bBack, bPrev, bNext, bSave, modes, bUndo, bErase, size, bold, font, ink, box });
            SyncSide();
        }

        // ------------------------------------------------------------------ opening
        /// Shows the PDF's pages (pictures already made, one per page).
        public void Open(string pdfPath, List<string> pages)
        {
            pngs = pages;
            sizes.Clear(); Edits.Clear(); found.Clear(); reading.Clear(); sel = null;
            foreach (string p in pngs)
                try { using (Image im = Image.FromFile(p)) sizes.Add(im.Size); } catch { sizes.Add(new Size(1240, 1754)); }
            page = -1;
            GoPage(0);
        }

        void GoPage(int i)
        {
            if (pngs.Count == 0) return;
            i = Math.Max(0, Math.Min(pngs.Count - 1, i));
            if (i == page) return;
            Commit();
            if (Visible && Fx.Motion && page >= 0) Fx.Crossfade(this, () => canvasR, 180);
            page = i; sel = null; scroll = 0;
            if (pic != null) pic.Dispose();
            try { using (Image im = Image.FromFile(pngs[i])) pic = new Bitmap(im); } catch { pic = null; }
            bPrev.Enabled = page > 0; bNext.Enabled = page < pngs.Count - 1;
            Find(page);
            SyncSide(); DoLayout(); Invalidate();
        }

        /// Reads the text on a page (once), in the background.
        void Find(int p)
        {
            if (found.ContainsKey(p) || reading.Contains(p)) return;
            reading.Add(p);
            status = L.T("pe_reading");
            string png = pngs[p];
            Thread t = new Thread(() =>
            {
                List<FoundLine> lines = new List<FoundLine>(); string err = null;
                string work = Path.Combine(Path.GetTempPath(), "fadi_pe_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    Directory.CreateDirectory(work);
                    List<List<Ocr.Read>> all = Ocr.ReadPictures(new[] { png }, work);
                    Ocr.Read r = all.Count > 0 && all[0].Count > 0 ? Ocr.Best(all[0]) : null;
                    if (r != null) lines = Lines(r);
                }
                catch (Exception ex) { err = ex.Message; }
                finally { try { Directory.Delete(work, true); } catch { } }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        reading.Remove(p);
                        found[p] = lines;
                        if (p == page) status = err ?? (lines.Count == 0 ? L.T("pe_no_text") : null);
                        Invalidate();
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }

        /// The reader's lines as boxes with their words (in reading order).
        static List<FoundLine> Lines(Ocr.Read r)
        {
            List<FoundLine> l = new List<FoundLine>();
            string[] texts = r.Text.Replace("\r", "").Split('\n');
            for (int i = 0; i < r.Lines.Count; i++)
            {
                List<Ocr.Word> ws = r.Lines[i];
                if (ws.Count == 0) continue;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = 0, y1 = 0;
                foreach (Ocr.Word w in ws) { x0 = Math.Min(x0, w.X); y0 = Math.Min(y0, w.Y); x1 = Math.Max(x1, w.X + w.W); y1 = Math.Max(y1, w.Y + w.H); }
                string text = texts.Length == r.Lines.Count ? texts[i] : JoinWords(ws);
                l.Add(new FoundLine { Box = RectangleF.FromLTRB(x0, y0, x1, y1), Text = text });
            }
            return l;
        }
        static string JoinWords(List<Ocr.Word> ws) { List<string> t = new List<string>(); foreach (Ocr.Word w in Ocr.LogicalWords(ws)) t.Add(w.Text); return string.Join(" ", t.ToArray()); }

        /// For tests (and pages read elsewhere): the lines of a page.
        public void SetLines(int p, List<FoundLine> lines) { found[p] = lines; reading.Remove(p); if (p == page) status = null; Invalidate(); }

        // ------------------------------------------------------------------ layout
        public void DoLayout()
        {
            int W = Width, H = Height, p = Theme.S(20), side = Theme.S(272);
            int bw = Math.Max(Theme.S(110), bBack.PreferredWidth);
            bBack.Bounds = Theme.Mirror(new Rectangle(p, Theme.S(14), bw, Theme.S(38)), W);
            int mw = Math.Min(Theme.S(380), W - bw - Theme.S(420));
            modes.Bounds = Theme.Mirror(new Rectangle(p + bw + Theme.S(16), Theme.S(15), Math.Max(Theme.S(240), mw), Theme.S(36)), W);
            int sw = Math.Max(Theme.S(150), bSave.PreferredWidth + Theme.S(12));
            bSave.Bounds = Theme.Mirror(new Rectangle(W - p - sw, Theme.S(14), sw, Theme.S(38)), W);
            pageNavR = new Rectangle(W - p - sw - Theme.S(16) - Theme.S(170), Theme.S(14), Theme.S(170), Theme.S(38));
            bPrev.Bounds = Theme.Mirror(new Rectangle(pageNavR.X, pageNavR.Y + Theme.S(2), Theme.S(34), Theme.S(34)), W);
            bNext.Bounds = Theme.Mirror(new Rectangle(pageNavR.Right - Theme.S(34), pageNavR.Y + Theme.S(2), Theme.S(34), Theme.S(34)), W);
            int top = Theme.S(66);
            canvasR = new Rectangle(p, top, W - p * 2 - side - Theme.S(16), H - top - p);
            sideR = new Rectangle(canvasR.Right + Theme.S(16), top, side, H - top - p);
            int x = sideR.X + Theme.S(18), w = side - Theme.S(36), y = sideR.Y + Theme.S(16);
            hintR = new Rectangle(x, y, w, Theme.S(110));
            sizeLbl = new Rectangle(x, y, w, Theme.S(20));
            size.Bounds = Theme.Mirror(new Rectangle(x - Theme.S(8), y + Theme.S(22), w + Theme.S(8), Theme.S(34)), W);
            y += Theme.S(64);
            fontLbl = new Rectangle(x, y, w, Theme.S(20));
            font.Bounds = Theme.Mirror(new Rectangle(x, y + Theme.S(22), w, Theme.S(34)), W);
            y += Theme.S(66);
            boldLbl = new Rectangle(x, y, w - Theme.S(60), Theme.S(34));
            bold.Bounds = Theme.Mirror(new Rectangle(x + w - Theme.S(52), y, Theme.S(52), Theme.S(34)), W);
            y += Theme.S(44);
            inkLbl = new Rectangle(x, y, w, Theme.S(20));
            ink.Bounds = Theme.Mirror(new Rectangle(x, y + Theme.S(22), w, Theme.S(34)), W);
            y += Theme.S(70);
            bErase.Bounds = Theme.Mirror(new Rectangle(x, y, w, Theme.S(36)), W);
            bUndo.Bounds = Theme.Mirror(new Rectangle(x, y + Theme.S(42), w, Theme.S(36)), W);
            PlaceBox();
            Invalidate();
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Width > 0) DoLayout(); }

        void SyncSide()
        {
            bool on = sel != null && !sel.Area;
            foreach (Control c in new Control[] { size, bold, font, ink, bErase }) c.Visible = on;
            bUndo.Visible = sel != null;
            if (on)
            {
                size.SetValue(sel.Size, false);
                bold.SetSilently(sel.Bold);
                font.SetSilently(sel.Serif ? 1 : 0);
                ink.Selected = sel.Ink; ink.Invalidate();
            }
            bSave.Enabled = Edits.Count > 0; bSave.Invalidate();
            Invalidate();
        }

        void Changed() { SyncSide(); Invalidate(); }

        // ------------------------------------------------------------------ where the page is on the screen
        float Zoom { get { if (pic == null) return 1; return Math.Min((canvasR.Width - Theme.S(16)) / (float)pic.Width, 4f); } }
        RectangleF PageRect
        {
            get
            {
                if (pic == null) return RectangleF.Empty;
                float k = Zoom, w = pic.Width * k, h = pic.Height * k;
                float y = canvasR.Y + Theme.S(8) - scroll;
                if (h < canvasR.Height - Theme.S(16)) y = canvasR.Y + (canvasR.Height - h) / 2;
                return new RectangleF(canvasR.X + (canvasR.Width - w) / 2, y, w, h);
            }
        }
        RectangleF ToScreen(RectangleF b) { RectangleF pr = PageRect; float k = Zoom; return new RectangleF(pr.X + b.X * k, pr.Y + b.Y * k, b.Width * k, b.Height * k); }
        PointF ToPage(Point p) { RectangleF pr = PageRect; float k = Zoom; return new PointF((p.X - pr.X) / k, (p.Y - pr.Y) / k); }
        float MaxScroll { get { return pic == null ? 0 : Math.Max(0, pic.Height * Zoom + Theme.S(16) - canvasR.Height); } }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float n = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120f * Theme.S(80)));
            if (n != scroll) { scroll = n; PlaceBox(); Invalidate(); }
        }

        // ------------------------------------------------------------------ pointing and clicking
        TextEdit EditAt(PointF pp)
        {
            for (int i = Edits.Count - 1; i >= 0; i--)
            {
                TextEdit e = Edits[i];
                if (e.Page == page && Hit(e).Contains(pp)) return e;
            }
            return null;
        }
        FoundLine LineAt(PointF pp)
        {
            List<FoundLine> l;
            if (!found.TryGetValue(page, out l)) return null;
            foreach (FoundLine f in l) { RectangleF b = f.Box; b.Inflate(3, 3); if (b.Contains(pp)) return f; }
            return null;
        }
        /// Where a change sits (its new text may be wider than the old line).
        RectangleF Hit(TextEdit e) { RectangleF r = e.Box; RectangleF t = TextBounds(e); if (!t.IsEmpty) r = RectangleF.Union(r, t); r.Inflate(2, 2); return r; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!canvasR.Contains(Ltr(e.Location)) || pic == null) { if (hotLine != null || hotEdit != null) { hotLine = null; hotEdit = null; Invalidate(); } Cursor = Cursors.Default; return; }
            PointF pp = ToPage(e.Location);
            if (dragging) { dragBox = RectangleF.FromLTRB(Math.Min(pp.X, ToPage(dragFrom).X), Math.Min(pp.Y, ToPage(dragFrom).Y), Math.Max(pp.X, ToPage(dragFrom).X), Math.Max(pp.Y, ToPage(dragFrom).Y)); Invalidate(); return; }
            TextEdit he = EditAt(pp);
            FoundLine hl = he == null && mode == 0 ? LineAt(pp) : null;
            if (he != hotEdit || hl != hotLine) { hotEdit = he; hotLine = hl; Invalidate(); }
            Cursor = mode == 2 ? Cursors.Cross : mode == 1 ? Cursors.IBeam : he != null || hl != null ? Cursors.IBeam : Cursors.Default;
        }
        Point Ltr(Point p) { return p; }   // the page itself isn't mirrored

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || pic == null || !canvasR.Contains(e.Location)) return;
            Focus();
            if (mode == 2) { Commit(); dragging = true; dragFrom = e.Location; dragBox = RectangleF.Empty; Capture = true; }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false; Capture = false;
                if (dragBox.Width > 3 && dragBox.Height > 3)
                {
                    TextEdit te = new TextEdit { Page = page, Box = dragBox, Area = true, Added = true, Paper = Sample(dragBox, true) };
                    Edits.Add(te); sel = te; Changed();
                }
                Invalidate();
                return;
            }
            if (e.Button != MouseButtons.Left || pic == null || !canvasR.Contains(e.Location)) return;
            PointF pp = ToPage(e.Location);
            Commit();
            TextEdit ex = EditAt(pp);
            if (ex != null) { Select(ex); return; }
            if (mode == 0)
            {
                FoundLine fl = LineAt(pp);
                if (fl == null) { sel = null; SyncSide(); return; }
                // a found line becomes a change: its look is taken from the page (paper, ink, size)
                TextEdit te = new TextEdit { Page = page, Box = fl.Box, Original = fl.Text, Text = fl.Text, Rtl = Ocr.IsArabic(fl.Text) };
                te.Paper = Sample(fl.Box, true); te.Ink = Sample(fl.Box, false, te.Paper);
                te.Size = Math.Max(6, fl.Box.Height * LetterPerBox);
                Edits.Add(te);
                Select(te);
            }
            else if (mode == 1)
            {
                float h = 22;
                List<FoundLine> l;
                if (found.TryGetValue(page, out l) && l.Count > 0) { float sum = 0; foreach (FoundLine f in l) sum += f.Box.Height; h = sum / l.Count; }
                TextEdit te = new TextEdit { Page = page, Box = new RectangleF(pp.X, pp.Y - h / 2, 4, h), Added = true, Size = Math.Max(8, h * LetterPerBox), Rtl = Theme.RTL };
                te.Paper = Sample(te.Box, true); te.Ink = Color.Black;
                Edits.Add(te);
                Select(te);
            }
        }

        void Select(TextEdit te)
        {
            sel = te;
            SyncSide();
            if (te.Area) { box.Visible = false; Invalidate(); return; }
            box.Text = te.Text;
            PlaceBox();
            box.Visible = true; box.Focus(); box.SelectAll();
            Invalidate();
        }

        /// The typing box sits right over the text being changed, in the same size.
        void PlaceBox()
        {
            if (sel == null || sel.Area || sel.Page != page || pic == null) { box.Visible = false; return; }
            RectangleF s = ToScreen(sel.Box);
            float px = Math.Max(8, sel.Size * Zoom);
            Font f = new Font(sel.Serif ? "Times New Roman" : "Arial", px, sel.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            Font old = box.Font; box.Font = f; if (old != null && old != Font) old.Dispose();
            box.RightToLeft = sel.Rtl ? RightToLeft.Yes : RightToLeft.No;
            box.BackColor = sel.Paper; box.ForeColor = sel.Ink;
            int h = (int)Math.Ceiling(Math.Max(s.Height, px * 1.35f));
            int w = (int)Math.Max(s.Width + Theme.S(40), TextRenderer.MeasureText(box.Text + "  ", f).Width + Theme.S(20));
            w = Math.Min(w, canvasR.Right - (int)s.X);
            box.Bounds = new Rectangle((int)s.X - 2, (int)(s.Y + s.Height / 2 - h / 2f), Math.Max(Theme.S(60), w), h);
        }

        /// Finishes typing: an unchanged line stops being a change; an empty new text goes away.
        void Commit()
        {
            if (sel == null) { box.Visible = false; return; }
            TextEdit e = sel;
            if (!e.Area)
            {
                e.Text = box.Visible ? box.Text : e.Text;
                if (e.Added && e.Text.Trim().Length == 0) Edits.Remove(e);
                else if (!e.Added && e.Text == e.Original && !e.Touched) Edits.Remove(e);
            }
            box.Visible = false;
            sel = null;
            SyncSide();
        }

        void Cancel()
        {
            if (sel != null && sel.Added && sel.Text.Trim().Length == 0) Edits.Remove(sel);
            else if (sel != null && !sel.Added && sel.Text == sel.Original && !sel.Touched) Edits.Remove(sel);
            box.Visible = false; sel = null; SyncSide();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (!box.Focused)
            {
                if (k == Keys.Delete && sel != null) { if (sel.Area || sel.Added) Edits.Remove(sel); else { sel.Text = ""; } box.Visible = false; sel = null; Changed(); return true; }
                if (k == Keys.PageDown || (k == Keys.Right && sel == null)) { GoPage(page + 1); return true; }
                if (k == Keys.PageUp || (k == Keys.Left && sel == null)) { GoPage(page - 1); return true; }
                if (k == (Keys.Control | Keys.Z) && Edits.Count > 0) { Edits.RemoveAt(Edits.Count - 1); sel = null; box.Visible = false; Changed(); return true; }
            }
            return base.ProcessCmdKey(ref msg, k);
        }

        // ------------------------------------------------------------------ the page's own colours
        /// paper: the colour around a box (what's behind the text); otherwise the ink: the colour furthest from the paper inside it.
        Color Sample(RectangleF b, bool paper, Color behind = default(Color))
        {
            if (pic == null) return paper ? Color.White : Color.Black;
            List<Color> cs = new List<Color>();
            Rectangle r = Rectangle.Round(b);
            r.Intersect(new Rectangle(0, 0, pic.Width, pic.Height));
            if (r.Width <= 0 || r.Height <= 0) return paper ? Color.White : Color.Black;
            if (paper)
            {
                Rectangle o = Rectangle.Inflate(r, 3, 3);
                o.Intersect(new Rectangle(0, 0, pic.Width, pic.Height));
                for (int x = o.Left; x < o.Right; x += Math.Max(1, o.Width / 40)) { cs.Add(pic.GetPixel(x, o.Top)); cs.Add(pic.GetPixel(x, o.Bottom - 1)); }
                for (int y = o.Top; y < o.Bottom; y += Math.Max(1, o.Height / 10)) { cs.Add(pic.GetPixel(o.Left, y)); cs.Add(pic.GetPixel(o.Right - 1, y)); }
                // the most common light-ish colour: sort by brightness and take the middle of the brighter half
                cs.Sort((a, c) => (a.R + a.G + a.B).CompareTo(c.R + c.G + c.B));
                return cs[Math.Min(cs.Count - 1, cs.Count * 3 / 4)];
            }
            int step = Math.Max(1, Math.Max(r.Width, r.Height) / 120);
            for (int x = r.Left; x < r.Right; x += step)
                for (int y = r.Top; y < r.Bottom; y += step) cs.Add(pic.GetPixel(x, y));
            Color bg = behind;
            cs.Sort((a, c) => Dist(c, bg).CompareTo(Dist(a, bg)));
            int n = Math.Max(1, cs.Count / 12);
            int R = 0, G = 0, B = 0;
            for (int i = 0; i < n; i++) { R += cs[i].R; G += cs[i].G; B += cs[i].B; }
            return Color.FromArgb(R / n, G / n, B / n);
        }
        static int Dist(Color a, Color b) { return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); }

        // ------------------------------------------------------------------ drawing a change (on screen and in the saved PDF)
        static Font FontFor(TextEdit e) { return new Font(e.Serif ? "Times New Roman" : "Arial", Math.Max(4, e.Size), e.Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel); }

        /// Where the new text lands, in page pixels (empty for none).
        static RectangleF TextBounds(TextEdit e)
        {
            if (e.Area || e.Text.Trim().Length == 0) return RectangleF.Empty;
            using (Font f = FontFor(e))
            using (Bitmap b = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(b))
            {
                SizeF sz = g.MeasureString(e.Text, f, PointF.Empty, StringFormat.GenericTypographic);
                float x = e.Rtl ? e.Box.Right - sz.Width : e.Box.X;
                float y = e.Box.Y + e.Box.Height / 2 - f.GetHeight() / 2;
                return new RectangleF(x, y, sz.Width, f.GetHeight());
            }
        }

        /// Paints one change in page pixels: the old text covered with the paper's colour, then the new text.
        public static void PaintEdit(Graphics g, TextEdit e)
        {
            RectangleF cover = e.Box;
            if (!e.Area) cover.Inflate(Math.Max(1.5f, e.Box.Height * 0.08f), Math.Max(1.5f, e.Box.Height * 0.12f));
            RectangleF tb = TextBounds(e);
            if (!e.Added || e.Area)
                using (SolidBrush b = new SolidBrush(e.Paper)) g.FillRectangle(b, cover);
            if (!tb.IsEmpty)
            {
                // new text that's longer than the old line also covers what was beside it
                if (!e.Added && tb.Right > cover.Right) using (SolidBrush b = new SolidBrush(e.Paper)) g.FillRectangle(b, cover.Right - 1, cover.Y, tb.Right - cover.Right + 3, cover.Height);
                if (!e.Added && tb.Left < cover.Left) using (SolidBrush b = new SolidBrush(e.Paper)) g.FillRectangle(b, tb.Left - 2, cover.Y, cover.Left - tb.Left + 3, cover.Height);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (Font f = FontFor(e))
                using (SolidBrush ib = new SolidBrush(e.Ink))
                using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    if (e.Rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                    sf.FormatFlags |= StringFormatFlags.NoWrap;
                    g.DrawString(e.Text, f, ib, e.Rtl ? new PointF(tb.Right, tb.Y) : new PointF(tb.X, tb.Y), sf);
                }
            }
        }

        /// The new text's words with where each one sits (for the invisible, searchable text in the saved PDF).
        public static List<Ocr.Word> Words(TextEdit e)
        {
            List<Ocr.Word> l = new List<Ocr.Word>();
            RectangleF tb = TextBounds(e);
            if (tb.IsEmpty) return l;
            string[] ws = e.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int total = 0; foreach (string w in ws) total += w.Length + 1;
            float x = tb.X, per = tb.Width / Math.Max(1, total);
            foreach (string w in ws)
            {
                float ww = per * w.Length;
                l.Add(new Ocr.Word { X = (int)x, Y = (int)tb.Y, W = Math.Max(1, (int)ww), H = Math.Max(1, (int)tb.Height), Text = w });
                x += ww + per;
            }
            return l;
        }

        /// A see-through picture of a page's changes at the page picture's size (for laying over the PDF page).
        public static Bitmap Overlay(List<TextEdit> edits, int pageIndex, Size pageSize, float scale)
        {
            Bitmap b = new Bitmap(Math.Max(1, (int)(pageSize.Width * scale)), Math.Max(1, (int)(pageSize.Height * scale)), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(scale, scale);
                foreach (TextEdit e in edits) if (e.Page == pageIndex) PaintEdit(g, e);
            }
            return b;
        }

        // ------------------------------------------------------------------ painting
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            int W = Width;
            // the page count between the arrows
            if (pngs.Count > 0)
                Theme.Text(g, L.T("pe_page", page + 1, pngs.Count), Theme.F(9.5f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(pageNavR.X + Theme.S(36), pageNavR.Y, pageNavR.Width - Theme.S(72), pageNavR.Height), W), Align.Center, true, false, true);
            // the side card
            Theme.PaintCard(g, Theme.Mirror(sideR, W));
            if (sel == null || sel.Area)
            {
                string h = L.T(mode == 0 ? "pe_hint_edit" : mode == 1 ? "pe_hint_add" : "pe_hint_erase");
                if (sel != null && sel.Area) h = L.T("pe_hint_area");
                Theme.Text(g, h, Theme.F(9f), Theme.InkSoft, Theme.Mirror(hintR, W), Align.Start, false, true);
                Theme.Text(g, L.T("pe_count", Edits.Count), Theme.F(8.5f, FontStyle.Bold), Edits.Count > 0 ? Theme.AccentColor : Theme.InkFaint, Theme.Mirror(new Rectangle(hintR.X, hintR.Bottom + Theme.S(4), hintR.Width, Theme.S(20)), W), Align.Start);
            }
            else
            {
                Theme.Text(g, L.T("pe_size"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(sizeLbl, W), Align.Start);
                Theme.Text(g, L.T("pe_font"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(fontLbl, W), Align.Start);
                Theme.Text(g, L.T("pe_bold"), Theme.F(9.25f), Theme.Ink, Theme.Mirror(boldLbl, W), Align.Start);
                Theme.Text(g, L.T("pe_color"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(inkLbl, W), Align.Start);
            }
            // the page
            Theme.FillRound(g, canvasR, Theme.S(12), Theme.Mix(Theme.BgBottom, Theme.Ink, 0.05f));
            if (pic == null) { Theme.Text(g, status ?? L.T("loading"), Theme.F(9.5f), Theme.InkSoft, canvasR, Align.Center); return; }
            g.SetClip(canvasR);
            RectangleF pr = PageRect;
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillRectangle(sh, pr.X + 2, pr.Y + 4, pr.Width, pr.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(pic, pr);
            // the changes, as they'll be saved
            GraphicsState st = g.Save();
            g.TranslateTransform(pr.X, pr.Y);
            g.ScaleTransform(Zoom, Zoom);
            foreach (TextEdit te in Edits) if (te.Page == page && te != sel) PaintEdit(g, te);
            if (sel != null && sel.Page == page && sel.Area) PaintEdit(g, sel);
            g.Restore(st);
            // the lines that can be changed: a soft outline on the one pointed at
            List<FoundLine> lines;
            if (mode == 0 && found.TryGetValue(page, out lines))
                foreach (FoundLine f in lines)
                {
                    bool taken = Edits.Exists(x => x.Page == page && !x.Added && x.Box == f.Box);
                    if (taken) continue;
                    RectangleF s = ToScreen(f.Box); s.Inflate(3, 2);
                    if (f == hotLine)
                    {
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb(28, Theme.AccentColor))) g.FillRectangle(hb, s);
                        using (Pen hp = new Pen(Theme.AccentColor, 1.5f)) g.DrawRectangle(hp, s.X, s.Y, s.Width, s.Height);
                    }
                    else using (Pen lp = new Pen(Color.FromArgb(70, Theme.AccentColor), 1f) { DashPattern = new float[] { 3, 3 } }) g.DrawRectangle(lp, s.X, s.Y, s.Width, s.Height);
                }
            foreach (TextEdit te in Edits)
            {
                if (te.Page != page) continue;
                RectangleF s = ToScreen(Hit(te));
                if (te == sel || te == hotEdit) using (Pen p = new Pen(Theme.AccentColor, te == sel ? 2f : 1.2f)) g.DrawRectangle(p, s.X, s.Y, s.Width, s.Height);
                else using (Pen p = new Pen(Color.FromArgb(120, Theme.AccentColor), 1f)) g.DrawRectangle(p, s.X, s.Y, s.Width, s.Height);
            }
            if (dragging && !dragBox.IsEmpty)
            {
                RectangleF s = ToScreen(dragBox);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(b, s);
                using (Pen p = new Pen(Theme.AccentColor, 1.5f) { DashPattern = new float[] { 4, 3 } }) g.DrawRectangle(p, s.X, s.Y, s.Width, s.Height);
            }
            g.ResetClip();
            if (status != null)
            {
                Rectangle sr = new Rectangle(canvasR.X + canvasR.Width / 2 - Theme.S(170), canvasR.Bottom - Theme.S(48), Theme.S(340), Theme.S(34));
                Theme.FillRound(g, sr, Theme.S(17), Color.FromArgb(225, 30, 30, 32));
                Theme.Text(g, status, Theme.F(8.75f), Color.White, sr, Align.Center, true, false, true);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing && pic != null) { pic.Dispose(); pic = null; } base.Dispose(disposing); }
    }
}
