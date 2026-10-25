using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace FileConverter
{
    /// The photo studio's own icons (drawn as vectors, see VectorIcons).
    public static class Ps
    {
        public const string Move = "", Marquee = "", Ellipse = "", Lasso = "", Wand = "", Brush = "", Eraser = "",
            Remove = "", Dropper = "", Hand = "", Eye = "", EyeOff = "", Lock = "", Unlock = "",
            AlignL = "", AlignC = "", AlignR = "", AlignT = "", AlignM = "", AlignB = "",
            Subject = "", Fill = "", NewLayer = "", MergeDown = "", Swap = "", Invert = "", NewCanvas = "",
            Fit = "", AdjLayer = "", Curves = "";
    }

    /// The layers as a tree: groups (folders) hold layers; the list shows the top layer first.
    public static class LayerTree
    {
        public class Row { public DocLayer L; public int Depth; }

        static bool TopLevel(PhotoDoc d, DocLayer l) { return l.Group == null || d.Find(l.Group) == null || d.Find(l.Group).Kind != LayerKind.Group; }

        /// What's directly inside a group (null = the top level), top first.
        public static List<DocLayer> Children(PhotoDoc d, string parent)
        {
            List<DocLayer> r = new List<DocLayer>();
            for (int i = d.Layers.Count - 1; i >= 0; i--)
            {
                DocLayer l = d.Layers[i];
                if (parent == null ? TopLevel(d, l) : l.Group == parent) r.Add(l);
            }
            return r;
        }

        /// Every row the list shows (closed groups hide what's in them), top first.
        public static List<Row> Rows(PhotoDoc d)
        {
            List<Row> rows = new List<Row>();
            Add(d, null, 0, rows, 0);
            return rows;
        }
        static void Add(PhotoDoc d, string parent, int depth, List<Row> rows, int guard)
        {
            if (guard > 20) return;
            foreach (DocLayer l in Children(d, parent))
            {
                rows.Add(new Row { L = l, Depth = depth });
                if (l.Kind == LayerKind.Group && !l.Collapsed) Add(d, l.Id, depth + 1, rows, guard + 1);
            }
        }

        public static bool IsInside(PhotoDoc d, DocLayer l, string groupId)
        {
            string g = l.Group; int guard = 0;
            while (g != null && guard++ < 20) { if (g == groupId) return true; DocLayer p = d.Find(g); if (p == null) break; g = p.Group; }
            return false;
        }

        /// Puts the layers back in an order where every group sits right above what's in it.
        public static void Normalize(PhotoDoc d)
        {
            DocLayer cur = d.Cur;
            List<DocLayer> outp = new List<DocLayer>();
            Emit(d, null, outp, 0);
            // anything missed (a loop of groups): kept at the top
            foreach (DocLayer l in d.Layers) if (!outp.Contains(l)) { l.Group = null; outp.Add(l); }
            d.Layers = outp;
            d.Current = cur == null ? Math.Min(d.Current, d.Layers.Count - 1) : d.Layers.IndexOf(cur);
        }
        static void Emit(PhotoDoc d, string parent, List<DocLayer> outp, int guard)
        {
            if (guard > 20) return;
            List<DocLayer> kids = Children(d, parent);
            for (int i = kids.Count - 1; i >= 0; i--)
            {
                DocLayer l = kids[i];
                if (l.Kind == LayerKind.Group) Emit(d, l.Id, outp, guard + 1);
                outp.Add(l);
            }
        }

        /// Moves a layer (and, for a group, everything in it) into `parent` at `index` (0 = top).
        public static void Move(PhotoDoc d, DocLayer l, string parent, int index)
        {
            if (parent == l.Id || (parent != null && l.Kind == LayerKind.Group && IsInside(d, d.Find(parent), l.Id))) return;
            List<DocLayer> sib = Children(d, parent);
            int was = sib.IndexOf(l);
            if (was >= 0) { sib.RemoveAt(was); if (was < index) index--; }
            index = Math.Max(0, Math.Min(sib.Count, index));
            sib.Insert(index, l);
            l.Group = parent;
            // rebuild: the siblings keep this order, everything else as it was
            Dictionary<DocLayer, int> rank = new Dictionary<DocLayer, int>();
            for (int i = 0; i < sib.Count; i++) rank[sib[i]] = i;
            DocLayer cur = d.Cur;
            List<DocLayer> outp = new List<DocLayer>();
            EmitOrdered(d, null, outp, parent, sib, 0);
            foreach (DocLayer x in d.Layers) if (!outp.Contains(x)) { x.Group = null; outp.Add(x); }
            d.Layers = outp;
            d.Current = d.Layers.IndexOf(cur ?? l);
        }
        static void EmitOrdered(PhotoDoc d, string parent, List<DocLayer> outp, string changed, List<DocLayer> order, int guard)
        {
            if (guard > 20) return;
            List<DocLayer> kids = parent == changed ? order : Children(d, parent);
            for (int i = kids.Count - 1; i >= 0; i--)
            {
                DocLayer l = kids[i];
                if (l.Kind == LayerKind.Group) EmitOrdered(d, l.Id, outp, changed, order, guard + 1);
                outp.Add(l);
            }
        }
    }

    /// The tools down the left side (one key each), and the two paint colors under them.
    public class ToolRail : UiControl
    {
        public class Item { public PhotoTool Tool; public string Glyph, NameKey, Key; public bool GapBefore; }
        public readonly List<Item> Items = new List<Item>
        {
            new Item { Tool = PhotoTool.Move, Glyph = Ps.Move, NameKey = "ps_t_move", Key = "V" },
            new Item { Tool = PhotoTool.Marquee, Glyph = Ps.Marquee, NameKey = "ps_t_rect", Key = "M", GapBefore = true },
            new Item { Tool = PhotoTool.Ellipse, Glyph = Ps.Ellipse, NameKey = "ps_t_ellipse", Key = "M" },
            new Item { Tool = PhotoTool.Lasso, Glyph = Ps.Lasso, NameKey = "ps_t_lasso", Key = "L" },
            new Item { Tool = PhotoTool.Wand, Glyph = Ps.Wand, NameKey = "ps_t_wand", Key = "W" },
            new Item { Tool = PhotoTool.Brush, Glyph = Ps.Brush, NameKey = "ps_t_brush", Key = "B", GapBefore = true },
            new Item { Tool = PhotoTool.Eraser, Glyph = Ps.Eraser, NameKey = "ps_t_eraser", Key = "E" },
            new Item { Tool = PhotoTool.Remove, Glyph = Ps.Remove, NameKey = "ps_t_remove", Key = "J" },
            new Item { Tool = PhotoTool.Text, Glyph = Ico.Text, NameKey = "ps_t_text", Key = "T", GapBefore = true },
            new Item { Tool = PhotoTool.Crop, Glyph = Ico.Crop, NameKey = "ps_t_crop", Key = "C" },
            new Item { Tool = PhotoTool.Eyedropper, Glyph = Ps.Dropper, NameKey = "ps_t_dropper", Key = "I" },
            new Item { Tool = PhotoTool.Hand, Glyph = Ps.Hand, NameKey = "ps_t_hand", Key = "H", GapBefore = true },
            new Item { Tool = PhotoTool.Zoom, Glyph = Ico.ZoomIn, NameKey = "ps_t_zoom", Key = "Z" },
        };
        public PhotoTool Tool = PhotoTool.Move;
        public Color Fore = Color.Black, Back = Color.White;
        public event Action<PhotoTool> Picked;
        public event Action ForeClicked, BackClicked, SwapClicked;
        int hover = -1;   // tool index, or -2 fore, -3 back, -4 swap
        readonly ToolTip tips = new ToolTip { InitialDelay = 350 };
        string tipShown;

        public ToolRail() { Cursor = Cursors.Hand; }

        int ColorsH { get { return Theme.S(76); } }
        int Gaps { get { int gaps = 0; foreach (Item i in Items) if (i.GapBefore) gaps++; return gaps; } }
        /// Short windows: the dividers get thinner and the buttons smaller, so nothing runs into the colors.
        int GapH { get { return Height - ColorsH - Theme.S(12) - Items.Count * Theme.S(30) < Gaps * Theme.S(9) ? Theme.S(3) : Theme.S(9); } }
        int Btn { get { return Math.Max(Theme.S(20), Math.Min(Theme.S(40), (Height - ColorsH - Theme.S(12) - Gaps * GapH) / Items.Count)); } }

        Rectangle Cell(int i)
        {
            int b = Btn, y = Theme.S(6), gh = GapH;
            for (int k = 0; k <= i; k++) { if (Items[k].GapBefore) y += gh; if (k < i) y += b; }
            return new Rectangle((Width - b) / 2, y, b, b);
        }
        Rectangle ForeR { get { int s = Theme.S(24); return new Rectangle(Width / 2 - s + Theme.S(2), Height - ColorsH + Theme.S(14), s, s); } }
        Rectangle BackR { get { int s = Theme.S(24); return new Rectangle(Width / 2 - Theme.S(4), Height - ColorsH + Theme.S(28), s, s); } }
        Rectangle SwapR { get { int s = Theme.S(16); return new Rectangle(Width / 2 + Theme.S(4), Height - ColorsH + Theme.S(6), s, s); } }

        int HitTest(Point p)
        {
            if (ForeR.Contains(p)) return -2;
            if (SwapR.Contains(p)) return -4;
            if (BackR.Contains(p)) return -3;
            for (int i = 0; i < Items.Count; i++) if (Cell(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            string t = h >= 0 ? L.T(Items[h].NameKey) + "  (" + Items[h].Key + ")" : h == -2 ? L.T("ps_fore") : h == -3 ? L.T("ps_back") : h == -4 ? L.T("ps_swap") : null;
            if (t != tipShown) { tipShown = t; tips.SetToolTip(this, t ?? ""); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int h = HitTest(e.Location);
            if (h >= 0) { Tool = Items[h].Tool; Invalidate(); if (Picked != null) Picked(Tool); }
            else if (h == -2 && ForeClicked != null) ForeClicked();
            else if (h == -3 && BackClicked != null) BackClicked();
            else if (h == -4 && SwapClicked != null) SwapClicked();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            for (int i = 0; i < Items.Count; i++)
            {
                Rectangle c = Cell(i);
                if (Items[i].GapBefore && i > 0)
                    using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, c.X + Theme.S(8), c.Y - GapH / 2 - 1, c.Right - Theme.S(8), c.Y - GapH / 2 - 1);
                bool on = Items[i].Tool == Tool;
                RectangleF r = RectangleF.Inflate(c, -Theme.S(3), -Theme.S(2));
                if (on) Theme.FillRound(g, r, Theme.S(8), Theme.AccentSoft);
                else if (i == hover) Theme.FillRound(g, r, Theme.S(8), Theme.Hover);
                Color ink = on ? (Theme.Dark ? Theme.Ink : Theme.Mix(Theme.AccentColor, Color.Black, 0.2f)) : Theme.InkSoft;
                Theme.Glyph(g, Items[i].Glyph, 11.5f, ink, c);
            }
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, Theme.S(10), Height - ColorsH, Width - Theme.S(10), Height - ColorsH);
            // the back color under, the paint color on top
            Chip(g, BackR, Back, hover == -3);
            Chip(g, ForeR, Fore, hover == -2);
            Theme.Glyph(g, Ps.Swap, 7.5f, hover == -4 ? Theme.Ink : Theme.InkFaint, SwapR);
        }

        static void Chip(Graphics g, Rectangle r, Color c, bool hot)
        {
            Theme.FillRound(g, r, Theme.S(5), c);
            Theme.StrokeRound(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(5), hot ? Theme.AccentColor : Theme.FieldBorder, hot ? 2f : 1f);
        }
        protected override string AccName { get { return L.T("ps_tools"); } }
    }

    /// The layers, top first: eye (show / hide), a small picture, the name, a lock. Drag to reorder (or into a group),
    /// double-click to rename, right-click for more.
    public class LayersList : UiControl
    {
        public PhotoDoc Doc;
        public event Action<DocLayer> Picked;
        public event Action<DocLayer> ToggleShown, ToggleLock, ToggleOpen;
        public event Action<DocLayer, string> Renamed;
        public event Action<DocLayer, string, int> Moved;      // layer, new group (null = top), index among it (0 = top)
        public event Action<DocLayer, Point> Menu;
        List<LayerTree.Row> rows = new List<LayerTree.Row>();
        int scroll, hover = -1, hoverPart;   // part: 0 row, 1 eye, 2 lock, 3 chevron
        int down = -1; Point downAt; bool dragging; int dropRow = -1; int dropMode;   // 0 above, 1 into (group), 2 below
        TextBox rename; DocLayer renaming;
        readonly Dictionary<string, KeyValuePair<string, Bitmap>> thumbs = new Dictionary<string, KeyValuePair<string, Bitmap>>();

        public LayersList() { Surface = Surface.Card; TabStop = true; }

        int RH { get { return Theme.S(44); } }
        public void Refresh2()
        {
            rows = Doc == null ? new List<LayerTree.Row>() : LayerTree.Rows(Doc);
            scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, rows.Count * RH - Height)));
            Invalidate();
        }
        /// Scrolls so the current layer shows.
        public void Reveal()
        {
            if (Doc == null) return;
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].L == Doc.Cur)
                {
                    int y = i * RH;
                    if (y < scroll) scroll = y; else if (y + RH > scroll + Height) scroll = y + RH - Height;
                    Invalidate();
                }
        }

        Rectangle RowR(int i) { return new Rectangle(0, i * RH - scroll, Width, RH); }
        Rectangle EyeR(Rectangle r) { return Theme.Mirror(new Rectangle(r.X + Theme.S(4), r.Y, Theme.S(30), r.Height), Width); }
        Rectangle LockR(Rectangle r) { return Theme.Mirror(new Rectangle(r.Right - Theme.S(34), r.Y, Theme.S(30), r.Height), Width); }
        Rectangle ChevR(Rectangle r, int depth) { return Theme.Mirror(new Rectangle(r.X + Theme.S(34) + depth * Theme.S(14), r.Y, Theme.S(14), r.Height), Width); }

        int RowAt(int y) { int i = (y + scroll) / RH; return y < 0 || i < 0 || i >= rows.Count ? -1 : i; }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, Math.Min(Math.Max(0, rows.Count * RH - Height), scroll - e.Delta / 120 * RH));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (down >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && (Math.Abs(e.X - downAt.X) > Theme.S(4) || Math.Abs(e.Y - downAt.Y) > Theme.S(4))) dragging = true;
                if (dragging)
                {
                    int i = RowAt(e.Y);
                    if (i < 0) { i = e.Y < 0 ? 0 : rows.Count - 1; dropMode = e.Y < 0 ? 0 : 2; }
                    else
                    {
                        int off = (e.Y + scroll) - i * RH;
                        bool grp = rows[i].L.Kind == LayerKind.Group;
                        dropMode = grp ? (off < RH / 4 ? 0 : off > RH * 3 / 4 ? 2 : 1) : (off < RH / 2 ? 0 : 2);
                    }
                    dropRow = i;
                    // near the edges: scroll
                    if (e.Y < Theme.S(16)) scroll = Math.Max(0, scroll - Theme.S(8));
                    else if (e.Y > Height - Theme.S(16)) scroll = Math.Min(Math.Max(0, rows.Count * RH - Height), scroll + Theme.S(8));
                    Invalidate();
                    return;
                }
            }
            int h = RowAt(e.Y), part = 0;
            if (h >= 0)
            {
                Rectangle r = RowR(h);
                if (EyeR(r).Contains(e.Location)) part = 1;
                else if (LockR(r).Contains(e.Location)) part = 2;
                else if (rows[h].L.Kind == LayerKind.Group && ChevR(r, rows[h].Depth).Contains(e.Location)) part = 3;
            }
            if (h != hover || part != hoverPart) { hover = h; hoverPart = part; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = RowAt(e.Y);
            if (i < 0) return;
            DocLayer l = rows[i].L;
            if (e.Button == MouseButtons.Right) { if (Picked != null) Picked(l); if (Menu != null) Menu(l, PointToScreen(e.Location)); return; }
            if (e.Button != MouseButtons.Left) return;
            Rectangle r = RowR(i);
            if (EyeR(r).Contains(e.Location)) { if (ToggleShown != null) ToggleShown(l); return; }
            if (LockR(r).Contains(e.Location)) { if (ToggleLock != null) ToggleLock(l); return; }
            if (l.Kind == LayerKind.Group && ChevR(r, rows[i].Depth).Contains(e.Location)) { if (ToggleOpen != null) ToggleOpen(l); return; }
            if (Picked != null) Picked(l);
            down = i; downAt = e.Location; dragging = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && down >= 0 && dropRow >= 0 && down < rows.Count && dropRow < rows.Count && Moved != null)
            {
                DocLayer l = rows[down].L, t = rows[dropRow].L;
                if (t != l && !(l.Kind == LayerKind.Group && LayerTree.IsInside(Doc, t, l.Id)))
                {
                    if (dropMode == 1) Moved(l, t.Id, 0);
                    else
                    {
                        string parent = t.Group != null && Doc.Find(t.Group) != null ? t.Group : null;
                        List<DocLayer> sib = LayerTree.Children(Doc, parent);
                        int at = sib.IndexOf(t) + (dropMode == 2 ? 1 : 0);
                        // below an open group: the first thing in it
                        if (dropMode == 2 && t.Kind == LayerKind.Group && !t.Collapsed && LayerTree.Children(Doc, t.Id).Count > 0) { parent = t.Id; at = 0; }
                        Moved(l, parent, at);
                    }
                }
            }
            down = -1; dragging = false; dropRow = -1;
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            int i = RowAt(p.Y);
            if (i >= 0) StartRename(rows[i].L);
        }

        public void StartRename(DocLayer l)
        {
            int i = rows.FindIndex(rw => rw.L == l);
            if (i < 0) return;
            EndRename(false);
            Rectangle r = RowR(i);
            int x = r.X + Theme.S(80) + rows[i].Depth * Theme.S(14);
            renaming = l;
            rename = new TextBox { Text = NameOf(l), BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(9.5f), BackColor = Theme.Field, ForeColor = Theme.Ink };
            rename.Bounds = Theme.Mirror(new Rectangle(x, r.Y + (r.Height - Theme.S(24)) / 2, Math.Max(Theme.S(60), r.Right - Theme.S(40) - x), Theme.S(24)), Width);
            rename.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndRename(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndRename(false); }
            };
            rename.LostFocus += (s, e) => EndRename(true);
            Controls.Add(rename);
            rename.Focus(); rename.SelectAll();
        }
        public bool Renaming { get { return rename != null; } }
        void EndRename(bool keep)
        {
            if (rename == null) return;
            TextBox tb = rename; DocLayer l = renaming;
            rename = null; renaming = null;
            string n = tb.Text.Trim();
            Controls.Remove(tb);
            BeginInvoke((Action)(() => tb.Dispose()));
            if (keep && n.Length > 0 && n != l.Name && Renamed != null) Renamed(l, n);
            Invalidate();
        }

        Bitmap Thumb(DocLayer l, Size s)
        {
            string key = l.Version + "/" + (l.Pixels == null ? 0 : l.Pixels.GetHashCode()) + "/" + (l.Mask == null ? 0 : l.Mask.GetHashCode()) + "/" + s.Width + "/" + Doc.W + "x" + Doc.H + "/" + l.X + "," + l.Y + "," + l.Scale + "," + l.Angle;
            KeyValuePair<string, Bitmap> c;
            if (thumbs.TryGetValue(l.Id, out c) && c.Key == key) return c.Value;
            if (c.Value != null) c.Value.Dispose();
            Bitmap b = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    float k = Math.Min(s.Width / (float)Doc.W, s.Height / (float)Doc.H);
                    g.TranslateTransform((s.Width - Doc.W * k) / 2, (s.Height - Doc.H * k) / 2);
                    Doc.DrawLayer(g, l, k, 1f);
                }
            }
            catch { }
            thumbs[l.Id] = new KeyValuePair<string, Bitmap>(key, b);
            return b;
        }

        static Bitmap check;
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            if (Doc == null) return;
            if (check == null)
            {
                check = new Bitmap(8, 8);
                using (Graphics cg = Graphics.FromImage(check)) { cg.Clear(Color.White); using (SolidBrush b = new SolidBrush(Color.FromArgb(220, 220, 220))) { cg.FillRectangle(b, 0, 0, 4, 4); cg.FillRectangle(b, 4, 4, 4, 4); } }
            }
            Font nf = Theme.F(9.25f), bf = Theme.F(9.25f, FontStyle.Bold), sf = Theme.F(7.75f);
            for (int i = 0; i < rows.Count; i++)
            {
                Rectangle r = RowR(i);
                if (r.Bottom < 0 || r.Y > Height) continue;
                DocLayer l = rows[i].L;
                bool cur = l == Doc.Cur, shown = Doc.Shown(l);
                if (cur) Theme.FillRound(g, RectangleF.Inflate(r, -Theme.S(3), -Theme.S(2)), Theme.S(8), Theme.AccentSoft);
                else if (i == hover && !dragging) Theme.FillRound(g, RectangleF.Inflate(r, -Theme.S(3), -Theme.S(2)), Theme.S(8), Theme.Hover);
                Color ink = cur && !Theme.Dark ? Theme.Mix(Theme.AccentColor, Color.Black, 0.35f) : Theme.Ink;
                // eye
                Theme.Glyph(g, l.Hidden ? Ps.EyeOff : Ps.Eye, 9.5f, l.Hidden ? Theme.InkFaint : (i == hover && hoverPart == 1 ? Theme.Ink : Theme.InkSoft), EyeR(r));
                int x = r.X + Theme.S(34) + rows[i].Depth * Theme.S(14);
                if (l.Kind == LayerKind.Group)
                    Theme.Glyph(g, l.Collapsed ? (Theme.RTL ? "" : "") : "", 7f, Theme.InkSoft, ChevR(r, rows[i].Depth));
                x += Theme.S(16);
                // the small picture
                Rectangle tr = Theme.Mirror(new Rectangle(x, r.Y + Theme.S(6), Theme.S(32), Theme.S(32)), Width);
                if (l.Kind == LayerKind.Image || l.Kind == LayerKind.Paint)
                {
                    // the photo's shape inside the square: see-through parts show the checkerboard
                    float k = Math.Min(tr.Width / (float)Doc.W, tr.Height / (float)Doc.H);
                    RectangleF dr = new RectangleF(tr.X + (tr.Width - Doc.W * k) / 2, tr.Y + (tr.Height - Doc.H * k) / 2, Doc.W * k, Doc.H * k);
                    using (TextureBrush tb = new TextureBrush(check)) g.FillRectangle(tb, dr);
                    g.DrawImage(Thumb(l, tr.Size), tr);
                    using (Pen p = new Pen(Theme.CardBorder)) g.DrawRectangle(p, dr.X, dr.Y, dr.Width, dr.Height);
                }
                else
                {
                    Theme.FillRound(g, tr, Theme.S(6), Theme.CardInner);
                    string gl = l.Kind == LayerKind.Text ? Ico.Text : l.Kind == LayerKind.Adjust ? (l.AdjType == AdjKind.Curves || l.AdjType == AdjKind.Levels ? Ps.Curves : Ps.AdjLayer) : Ico.Folder;
                    Theme.Glyph(g, gl, 10f, Theme.InkSoft, tr);
                }
                x += Theme.S(40);
                Rectangle nr = Theme.Mirror(new Rectangle(x, r.Y + Theme.S(5), Math.Max(10, r.Right - Theme.S(40) - x), Theme.S(20)), Width);
                Theme.Text(g, NameOf(l), cur ? bf : nf, shown ? ink : Theme.InkFaint, nr, Align.Start);
                string sub = Sub(l);
                if (sub.Length > 0)
                {
                    Rectangle sr = Theme.Mirror(new Rectangle(x, r.Y + Theme.S(23), Math.Max(10, r.Right - Theme.S(40) - x), Theme.S(16)), Width);
                    Theme.Text(g, sub, sf, Theme.InkFaint, sr, Align.Start);
                }
                if (l.Locked || (i == hover && hoverPart == 2) || (i == hover && !dragging))
                    Theme.Glyph(g, l.Locked ? Ps.Lock : Ps.Unlock, 8.5f, l.Locked ? Theme.InkSoft : Theme.InkFaint, LockR(r));
            }
            if (dragging && dropRow >= 0 && dropRow < rows.Count)
            {
                Rectangle r = RowR(dropRow);
                if (dropMode == 1) Theme.StrokeRound(g, RectangleF.Inflate(r, -Theme.S(3), -Theme.S(2)), Theme.S(8), Theme.AccentColor, 2f);
                else
                {
                    int y = dropMode == 0 ? r.Y : r.Bottom;
                    int x0 = Theme.S(34) + rows[dropRow].Depth * Theme.S(14);
                    using (Pen p = new Pen(Theme.AccentColor, Theme.S(2))) g.DrawLine(p, Theme.RTL ? Width - x0 : x0, y, Theme.RTL ? Theme.S(8) : Width - Theme.S(8), y);
                }
            }
            if (rows.Count * RH > Height)
            {
                float frac = Height / (float)(rows.Count * RH);
                int th = Math.Max(Theme.S(24), (int)(Height * frac)), ty = (int)((Height - th) * (scroll / (float)Math.Max(1, rows.Count * RH - Height)));
                Theme.FillRound(g, Theme.Mirror(new Rectangle(Width - Theme.S(4), ty, Theme.S(3), th), Width), Theme.S(1.5f), Color.FromArgb(90, Theme.InkFaint));
            }
        }

        /// A layer's name (text layers without one of their own show their words).
        public static string NameOf(DocLayer l)
        {
            if (!string.IsNullOrEmpty(l.Name)) return l.Name;
            if (l.Kind == LayerKind.Text && l.Text != null) { string t = l.Text.Label.Trim(); return t.Length > 40 ? t.Substring(0, 40) + "…" : t.Length > 0 ? t : L.T("ps_kind_text"); }
            return L.T("ps_kind_" + l.Kind.ToString().ToLowerInvariant());
        }

        static string Sub(DocLayer l)
        {
            List<string> p = new List<string>();
            if (l.Kind == LayerKind.Adjust && L.T("ps_adj_" + l.AdjType.ToString().ToLowerInvariant()) != l.Name) p.Add(L.T("ps_adj_" + l.AdjType.ToString().ToLowerInvariant()));
            if (l.Blend != BlendMode.Normal) p.Add(L.T("ps_bl_" + l.Blend.ToString().ToLowerInvariant()));
            if (l.Opacity < 0.995f) p.Add((int)Math.Round(l.Opacity * 100) + "%");
            if (l.Mask != null && l.Kind != LayerKind.Adjust) p.Add(L.T("ps_masked"));
            return string.Join(" · ", p.ToArray());
        }

        protected override string AccName { get { return L.T("ps_layers"); } }
        protected override AccessibleRole AccRole { get { return AccessibleRole.List; } }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Doc == null || rows.Count == 0) return;
            int i = rows.FindIndex(rw => rw.L == Doc.Cur);
            if (e.KeyCode == Keys.Up && i > 0) { e.Handled = true; if (Picked != null) Picked(rows[i - 1].L); }
            else if (e.KeyCode == Keys.Down && i < rows.Count - 1) { e.Handled = true; if (Picked != null) Picked(rows[i + 1].L); }
            else if (e.KeyCode == Keys.F2 && i >= 0) { e.Handled = true; StartRename(rows[i].L); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) foreach (KeyValuePair<string, Bitmap> kv in thumbs.Values) if (kv.Value != null) kv.Value.Dispose();
            base.Dispose(disposing);
        }
    }

    /// Every change made, oldest first; click one to go back (or forward) to it.
    public class HistoryList : ScrollList
    {
        public PhotoDoc Doc;
        public event Action<int> Jump;   // how many steps: negative = back
        public HistoryList() { RowHeight = 34; Surface = Surface.Card; }
        public override int Count { get { return Doc == null ? 0 : 1 + Doc.Undo.Count + Doc.Redo.Count; } }
        int Now { get { return Doc == null ? 0 : Doc.Undo.Count; } }
        string LabelAt(int i)
        {
            if (i == 0) return L.T(Doc.Undo.Count >= PhotoDoc.MaxHistory ? "ps_h_earlier" : "ps_h_open");
            if (i <= Doc.Undo.Count) return L.T(Doc.Undo[i - 1].Name);
            return L.T(Doc.Redo[Doc.Redo.Count - (i - Doc.Undo.Count)].Name);
        }
        protected override void DrawRow(Graphics g, int index, Rectangle r, bool hover)
        {
            bool cur = index == Now, future = index > Now;
            RectangleF rr = RectangleF.Inflate(r, -Theme.S(3), -Theme.S(1));
            if (cur) Theme.FillRound(g, rr, Theme.S(7), Theme.AccentSoft);
            else if (hover) Theme.FillRound(g, rr, Theme.S(7), Theme.Hover);
            Theme.Glyph(g, index == 0 ? Ico.Photo : "", 8.5f, future ? Theme.InkFaint : Theme.InkSoft, Theme.Mirror(new Rectangle(r.X + Theme.S(8), r.Y, Theme.S(22), r.Height), Width));
            Theme.Text(g, LabelAt(index), cur ? Theme.F(9.25f, FontStyle.Bold) : Theme.F(9.25f), future ? Theme.InkFaint : Theme.Ink,
                       Theme.Mirror(new Rectangle(r.X + Theme.S(36), r.Y, r.Width - Theme.S(44), r.Height), Width), Align.Start);
        }
        protected override void RowClick(int index, Point local, Rectangle row) { if (Jump != null && index != Now) Jump(index - Now); }
        protected override string RowText(int index) { return Doc == null ? null : LabelAt(index); }
        public void ShowNow() { EnsureVisible(Now); Invalidate(); }
    }

    /// Curves: drag the line to make parts of the photo lighter or darker. Click to add a point, drag one off the side to remove it.
    public class CurveEditor : UiControl
    {
        public Tone Tone;
        public int[] Histogram;
        public event Action Changed, Committed;
        int drag = -1, hover = -1;
        public CurveEditor() { Cursor = Cursors.Cross; Surface = Surface.Card; }
        RectangleF Area { get { float m = Theme.S(6); return new RectangleF(m, m, Width - m * 2, Height - m * 2); } }
        PointF ToScreen(Point p) { RectangleF a = Area; return new PointF(a.X + p.X / 255f * a.Width, a.Bottom - p.Y / 255f * a.Height); }
        Point ToCurve(Point s) { RectangleF a = Area; return new Point(Clamp((int)Math.Round((s.X - a.X) / a.Width * 255)), Clamp((int)Math.Round((a.Bottom - s.Y) / a.Height * 255))); }
        static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }
        int Near(Point s) { if (Tone == null) return -1; for (int i = 0; i < Tone.Curve.Count; i++) { PointF p = ToScreen(Tone.Curve[i]); if (Math.Abs(p.X - s.X) <= Theme.S(8) && Math.Abs(p.Y - s.Y) <= Theme.S(8)) return i; } return -1; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Tone == null || e.Button != MouseButtons.Left) return;
            int i = Near(e.Location);
            if (i < 0)
            {
                Point c = ToCurve(e.Location);
                c.Y = Clamp((int)Math.Round(Tone.CurveAt(c.X)));
                int at = 0; while (at < Tone.Curve.Count && Tone.Curve[at].X < c.X) at++;
                if (at == 0 || at == Tone.Curve.Count) return;
                if (Tone.Curve[at].X - c.X < 6 || c.X - Tone.Curve[at - 1].X < 6) return;
                Tone.Curve.Insert(at, c); i = at;
                if (Changed != null) Changed();
            }
            drag = i; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Tone == null) return;
            if (drag < 0) { int h = Near(e.Location); if (h != hover) { hover = h; Invalidate(); } return; }
            Point c = ToCurve(e.Location);
            List<Point> p = Tone.Curve;
            bool end = drag == 0 || drag == p.Count - 1;
            // dragged well off the side or top/bottom: removed (the two ends stay)
            if (!end && (e.Y < -Theme.S(24) || e.Y > Height + Theme.S(24)))
            {
                p.RemoveAt(drag); drag = -1; Invalidate(); if (Changed != null) Changed(); return;
            }
            int lo = drag == 0 ? 0 : p[drag - 1].X + 4, hi = drag == p.Count - 1 ? 255 : p[drag + 1].X - 4;
            if (drag == 0) hi = Math.Min(hi, 250); if (drag == p.Count - 1) lo = Math.Max(lo, 5);
            p[drag] = new Point(Math.Max(lo, Math.Min(hi, c.X)), c.Y);
            Invalidate();
            if (Changed != null) Changed();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (drag >= 0) { drag = -1; if (Committed != null) Committed(); } Invalidate(); }
        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (Tone == null) return;
            int i = Near(PointToClient(Cursor.Position));
            if (i > 0 && i < Tone.Curve.Count - 1) { Tone.Curve.RemoveAt(i); Invalidate(); if (Changed != null) Changed(); if (Committed != null) Committed(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF a = Area;
            Theme.FillRound(g, a, Theme.S(6), Theme.CardInner);
            Hist.Draw(g, Histogram, a);
            using (Pen p = new Pen(Theme.Line)) for (int i = 1; i < 4; i++) { g.DrawLine(p, a.X + a.Width * i / 4, a.Y, a.X + a.Width * i / 4, a.Bottom); g.DrawLine(p, a.X, a.Y + a.Height * i / 4, a.Right, a.Y + a.Height * i / 4); }
            using (Pen p = new Pen(Theme.InkFaint) { DashStyle = DashStyle.Dot }) g.DrawLine(p, a.X, a.Bottom, a.Right, a.Y);
            if (Tone == null) return;
            PointF[] pts = new PointF[65];
            for (int i = 0; i <= 64; i++) { int x = i * 255 / 64; pts[i] = ToScreen(new Point(x, Clamp((int)Math.Round(Tone.CurveAt(x))))); }
            using (Pen p = new Pen(Theme.Ink, Theme.S(2))) g.DrawLines(p, pts);
            for (int i = 0; i < Tone.Curve.Count; i++)
            {
                PointF c = ToScreen(Tone.Curve[i]); float r = Theme.S(i == drag || i == hover ? 6 : 5);
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillEllipse(b, c.X - r, c.Y - r, r * 2, r * 2);
                using (Pen p = new Pen(Theme.AccentColor, Theme.S(2))) g.DrawEllipse(p, c.X - r, c.Y - r, r * 2, r * 2);
            }
        }
    }

    /// Levels: the photo's light as a chart, with the dark, middle and light points under it, and the output range.
    public class LevelsBar : UiControl
    {
        public Tone Tone;
        public int[] Histogram;
        public event Action Changed, Committed;
        int drag = -1;   // 0 black, 1 gray, 2 white, 3 out black, 4 out white
        public LevelsBar() { Cursor = Cursors.Hand; Surface = Surface.Card; }
        float M { get { return Theme.S(8); } }
        RectangleF HistR { get { return new RectangleF(M, 0, Width - M * 2, Height - Theme.S(64)); } }
        float InY { get { return HistR.Bottom + Theme.S(10); } }
        float OutY { get { return Height - Theme.S(16); } }
        float X(float v) { return M + v / 255f * (Width - M * 2); }
        float V(float x) { return Math.Max(0, Math.Min(255, (x - M) / (Width - M * 2) * 255)); }
        float GrayV { get { float t = (float)Math.Pow(0.5, Tone.Gamma); return Tone.InBlack + t * (Tone.InWhite - Tone.InBlack); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Tone == null) return;
            float tol = Theme.S(10);
            float[][] h = { new[] { X(Tone.InBlack), InY }, new[] { X(GrayV), InY }, new[] { X(Tone.InWhite), InY }, new[] { X(Tone.OutBlack), OutY }, new[] { X(Tone.OutWhite), OutY } };
            float best = tol * 2; drag = -1;
            for (int i = 0; i < 5; i++) { float d = Math.Abs(e.X - h[i][0]) + Math.Abs(e.Y - h[i][1] - Theme.S(4)) * 0.6f; if (d < best) { best = d; drag = i; } }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag < 0 || Tone == null) return;
            int v = (int)Math.Round(V(e.X));
            switch (drag)
            {
                case 0: Tone.InBlack = Math.Min(v, Tone.InWhite - 4); break;
                case 2: Tone.InWhite = Math.Max(v, Tone.InBlack + 4); break;
                case 1:
                    {
                        float t = (V(e.X) - Tone.InBlack) / Math.Max(1, Tone.InWhite - Tone.InBlack);
                        t = Math.Max(0.03f, Math.Min(0.97f, t));
                        Tone.Gamma = (float)Math.Max(0.1, Math.Min(9.99, Math.Log(t) / Math.Log(0.5)));
                        break;
                    }
                case 3: Tone.OutBlack = Math.Min(v, Tone.OutWhite - 4); break;
                case 4: Tone.OutWhite = Math.Max(v, Tone.OutBlack + 4); break;
            }
            Invalidate();
            if (Changed != null) Changed();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (drag >= 0) { drag = -1; if (Committed != null) Committed(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF hr = HistR;
            Theme.FillRound(g, hr, Theme.S(6), Theme.CardInner);
            Hist.Draw(g, Histogram, hr);
            if (Tone == null) return;
            // input: dark, middle, light
            using (Pen p = new Pen(Theme.Line, Theme.S(2))) g.DrawLine(p, M, InY, Width - M, InY);
            Tri(g, X(Tone.InBlack), InY, Color.Black);
            Tri(g, X(GrayV), InY, Color.FromArgb(128, 128, 128));
            Tri(g, X(Tone.InWhite), InY, Color.White);
            Font f = Theme.F(8f);
            Theme.Text(g, Tone.InBlack + "   " + Tone.Gamma.ToString("0.00") + "   " + Tone.InWhite, f, Theme.InkSoft, new Rectangle(0, (int)InY + Theme.S(12), Width, Theme.S(16)), Align.Center, true, false, true);
            // output
            RectangleF ob = new RectangleF(M, OutY - Theme.S(8), Width - M * 2, Theme.S(6));
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(ob.X - 1, ob.Y, ob.Width + 2, ob.Height), Color.Black, Color.White, LinearGradientMode.Horizontal)) g.FillRectangle(b, ob);
            Tri(g, X(Tone.OutBlack), OutY, Color.Black);
            Tri(g, X(Tone.OutWhite), OutY, Color.White);
        }

        static void Tri(Graphics g, float x, float y, Color c)
        {
            float s = Theme.S(7);
            PointF[] p = { new PointF(x, y), new PointF(x - s, y + s * 1.4f), new PointF(x + s, y + s * 1.4f) };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, p);
            using (Pen pen = new Pen(Theme.InkSoft, 1f)) g.DrawPolygon(pen, p);
        }
    }

    /// Draws a light histogram (256 counts) as a soft area.
    public static class Hist
    {
        public static void Draw(Graphics g, int[] h, RectangleF r)
        {
            if (h == null || h.Length < 256) return;
            // the tallest bars are often one pure color: scale to the 99th-percentile bar so the shape shows
            int[] s = (int[])h.Clone(); Array.Sort(s);
            float max = Math.Max(1, s[252]);
            PointF[] pts = new PointF[258];
            pts[0] = new PointF(r.X, r.Bottom);
            for (int i = 0; i < 256; i++) pts[i + 1] = new PointF(r.X + i / 255f * r.Width, r.Bottom - Math.Min(1, h[i] / max) * (r.Height - 4));
            pts[257] = new PointF(r.Right, r.Bottom);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 50, Theme.Ink))) g.FillPolygon(b, pts);
        }

        public static int[] Of(Bitmap b)
        {
            int[] h = new int[256];
            if (b == null) return h;
            int[] px = Pix.Get(b);
            for (int i = 0; i < px.Length; i++)
            {
                int p = px[i];
                if (((uint)p >> 24) < 8) continue;
                h[(((p >> 16) & 255) * 77 + ((p >> 8) & 255) * 150 + (p & 255) * 29) >> 8]++;
            }
            return h;
        }
    }
}
