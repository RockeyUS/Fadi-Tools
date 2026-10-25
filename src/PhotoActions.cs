using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    public partial class PhotoStudio
    {
        // ------------------------------------------------------------------ long jobs
        System.Windows.Forms.Timer busyTick;
        /// Runs work off the screen's thread with a note over the photo; `done` gets the result back on it.
        void RunJob<T>(string note, Func<T> work, Action<T, string> done)
        {
            if (busyJob != null) return;
            busyJob = note; canvas.Busy = note;
            if (busyTick == null) { busyTick = new System.Windows.Forms.Timer { Interval = 40 }; busyTick.Tick += (s, e) => canvas.Invalidate(); }
            busyTick.Start();
            InvalidateBar(); canvas.Invalidate();
            Thread t = new Thread(() =>
            {
                T r = default(T); string err = null;
                try { r = work(); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    busyJob = null; canvas.Busy = null; busyTick.Stop();
                    canvas.Invalidate(); InvalidateBar();
                    done(r, err);
                });
            });
            t.IsBackground = true; t.Start();
        }

        /// The background remover is needed: asks before the one-time download.
        bool EngineOk()
        {
            if (BgRemover.Ready) return true;
            return Ask.Confirm(FindForm(), L.T("ps_engine_q"), L.T("bg_info"), L.T("ps_engine_get"), L.T("cancel"), AskKind.Info);
        }
        byte[] FindSubject(Bitmap b, Action<string> status)
        {
            if (!BgRemover.Ready)
            {
                DateTime last = DateTime.MinValue;
                BgRemover.Install((f, msg) => { if ((DateTime.Now - last).TotalMilliseconds < 200) return; last = DateTime.Now; status(msg); }, () => IsDisposed || Tools.ShuttingDown);
            }
            status(L.T("bg_working"));
            return BgRemover.Mask(b);
        }
        void JobStatus(string s) { Ui(() => { if (busyJob != null) { busyJob = s; canvas.Busy = s; InvalidateBar(); } }); }

        /// Select subject: the AI finds the person / pet / thing in the photo and selects it.
        void SelectSubject()
        {
            if (doc == null || busyJob != null || !EngineOk()) return;
            Bitmap flat = doc.Flatten();
            RunJob(L.T(BgRemover.Ready ? "bg_working" : "bg_dl_engine"), () => { try { return FindSubject(flat, JobStatus); } finally { flat.Dispose(); } }, (m, err) =>
            {
                if (err != null || m == null || doc == null) { ShowToast(L.T("bg_failed", err ?? "?"), false, null, null); return; }
                if (canvas.Tool != PhotoTool.Marquee && canvas.Tool != PhotoTool.Ellipse && canvas.Tool != PhotoTool.Lasso && canvas.Tool != PhotoTool.Wand) SetTool(PhotoTool.Marquee);
                canvas.ApplySel(m, SelOp.New, "ps_h_subject");
                ShowToast(L.T("ps_subject_done"), true, null, null);
            });
        }

        /// Remove background on a photo layer: what isn't the subject is hidden (a mask, so it can be painted back).
        void RemoveLayerBackground()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null || l.Kind != LayerKind.Image || l.Pixels == null || busyJob != null || !EngineOk()) return;
            Bitmap copy = new Bitmap(l.Pixels);
            RunJob(L.T(BgRemover.Ready ? "bg_working" : "bg_dl_engine"), () => { try { return FindSubject(copy, JobStatus); } finally { copy.Dispose(); } }, (m, err) =>
            {
                if (err != null || m == null || doc == null || !doc.Layers.Contains(l)) { ShowToast(L.T("bg_failed", err ?? "?"), false, null, null); return; }
                Change("ps_h_remove_bg");
                l.Mask = m; l.Version++;
                AfterChange(true);
                ShowToast(L.T("ps_bg_done"), true, null, null);
            });
        }

        /// Remove object: what was painted over is filled from the photo around it, on a retouch layer above.
        void RemoveObject(List<PointF> stroke)
        {
            if (doc == null || busyJob != null || stroke.Count == 0) return;
            int W = doc.W, H = doc.H;
            Rectangle box;
            float[] cov = Brush.Cover(Brush.Dabs(stroke, canvas.BrushSize / 2), canvas.BrushSize / 2, 0.9f, W, H, out box);
            byte[] hole = new byte[W * H];
            bool any = false;
            for (int i = 0; i < hole.Length; i++) if (cov[i] > 0.2f) { hole[i] = 255; any = true; }
            if (!any) return;
            if (doc.Sel != null) for (int i = 0; i < hole.Length; i++) if (doc.Sel[i] < 128) hole[i] = 0;
            Bitmap flat = doc.Flatten();
            canvas.PendingStroke = new List<PointF>(stroke);
            RunJob(L.T("ps_removing"), () =>
            {
                try
                {
                    Bitmap filled = Inpaint.Fill(flat, hole, () => IsDisposed || Tools.ShuttingDown);
                    // only the filled part goes on the layer, with a soft edge
                    byte[] soft = Selection.Feather(Grow(hole, W, H, 2), W, H, 2);
                    int[] f = Pix.Get(filled);
                    filled.Dispose();
                    for (int i = 0; i < f.Length; i++)
                    {
                        int a = soft[i];
                        f[i] = a == 0 ? 0 : (f[i] & 0xFFFFFF) | ((((int)((uint)f[i] >> 24) * a) / 255) << 24);
                    }
                    return Pix.Make(W, H, f);
                }
                finally { flat.Dispose(); }
            }, (patch, err) =>
            {
                canvas.PendingStroke = null;
                if (err != null || patch == null || doc == null || doc.W != W || doc.H != H) { if (patch != null) patch.Dispose(); if (err != null) ShowToast(err, false, null, null); canvas.Invalidate(); return; }
                Change("ps_h_remove");
                DocLayer l = doc.Cur;
                if (l != null && l.Kind == LayerKind.Paint && !doc.IsLocked(l) && l.Name == L.T("ps_l_retouch"))
                {
                    Bitmap b = new Bitmap(l.Pixels);
                    using (Graphics g = Graphics.FromImage(b)) g.DrawImage(patch, 0, 0, W, H);
                    patch.Dispose();
                    l.Pixels = b;
                }
                else
                {
                    DocLayer r = new DocLayer { Kind = LayerKind.Paint, Name = L.T("ps_l_retouch"), Pixels = patch, X = W / 2f, Y = H / 2f };
                    InsertAbove(r);
                }
                doc.Cur.Version++;
                AfterChange(true);
            });
        }

        static byte[] Grow(byte[] m, int w, int h, int r)
        {
            byte[] o = (byte[])m.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (m[y * w + x] == 0) continue;
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        for (int dx = -r; dx <= r; dx++) { int xx = x + dx; if (xx >= 0 && xx < w) o[yy * w + xx] = 255; }
                    }
                }
            return o;
        }

        // ------------------------------------------------------------------ layers
        /// Puts a new layer just above the current one (inside the same group; at the top of a selected group).
        void InsertAbove(DocLayer n)
        {
            DocLayer cur = doc.Cur;
            if (cur != null && cur.Kind == LayerKind.Group) { n.Group = cur.Id; doc.Layers.Insert(doc.Current, n); }
            else
            {
                if (cur != null) n.Group = cur.Group;
                doc.Layers.Insert(doc.Current < 0 ? doc.Layers.Count : doc.Current + 1, n);
            }
            LayerTree.Normalize(doc);
            doc.Current = doc.Layers.IndexOf(n);
        }

        void NewLayer()
        {
            if (doc == null) return;
            Change("ps_h_new_layer");
            InsertAbove(PhotoDoc.NewPaint(doc.W, doc.H, doc.NextName("ps_l_layer")));
            AfterChange(true); layers.Reveal();
        }

        void AdjMenu(Control anchor)
        {
            if (doc == null) return;
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("ps_adj_light"), Sub = L.T("ps_adj_light_sub"), Glyph = Ps.AdjLayer, Run = () => NewAdjustment(AdjKind.Light) });
            m.Add(new MenuPopup.Item { Text = L.T("ps_adj_levels"), Sub = L.T("ps_adj_levels_sub"), Glyph = Ps.Curves, Run = () => NewAdjustment(AdjKind.Levels) });
            m.Add(new MenuPopup.Item { Text = L.T("ps_adj_curves"), Sub = L.T("ps_adj_curves_sub"), Glyph = Ps.Curves, Run = () => NewAdjustment(AdjKind.Curves) });
            m.Add(new MenuPopup.Item { Text = L.T("ps_adj_huesat"), Sub = L.T("ps_adj_huesat_sub"), Glyph = Ps.AdjLayer, Run = () => NewAdjustment(AdjKind.HueSat) });
            MenuPopup.Show(anchor, m, 320);
        }

        void NewAdjustment(AdjKind k)
        {
            if (doc == null) return;
            Change("ps_h_new_adj");
            DocLayer a = new DocLayer { Kind = LayerKind.Adjust, AdjType = k, Adj = new Adjust(), Tone = new Tone(), Name = L.T("ps_adj_" + k.ToString().ToLowerInvariant()) };
            // the selection becomes where it applies
            if (doc.Sel != null) a.Mask = (byte[])doc.Sel.Clone();
            InsertAbove(a);
            if (canvas.Tool != PhotoTool.Move) SetTool(PhotoTool.Move);
            AfterChange(true); layers.Reveal();
        }

        void GroupLayer()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null) return;
            Change("ps_h_group");
            DocLayer g = new DocLayer { Kind = LayerKind.Group, Name = doc.NextName("ps_l_group"), Group = l.Group };
            int i = doc.Current;
            doc.Layers.Insert(i + 1, g);
            l.Group = g.Id;
            doc.Current = i + 1;
            LayerTree.Normalize(doc);
            doc.Current = doc.Layers.IndexOf(g);
            AfterChange(true);
        }

        void Ungroup()
        {
            DocLayer g = doc == null ? null : doc.Cur;
            if (g == null || g.Kind != LayerKind.Group) return;
            Change("ps_h_ungroup");
            foreach (DocLayer c in doc.Layers) if (c.Group == g.Id) c.Group = g.Group;
            int i = doc.Layers.IndexOf(g);
            doc.Layers.Remove(g);
            doc.Current = Math.Max(0, Math.Min(doc.Layers.Count - 1, i - 1));
            LayerTree.Normalize(doc);
            AfterChange(true);
        }

        void DuplicateLayer()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null) return;
            Change("ps_h_duplicate");
            DocLayer c = l.Duplicate();
            c.Name = l.Name.Length > 0 ? L.T("ps_copy_of", l.Name) : "";
            if (l.Kind == LayerKind.Group)
            {
                // a group: everything in it is copied too
                List<DocLayer> kids = new List<DocLayer>();
                foreach (DocLayer k in doc.Layers) if (LayerTree.IsInside(doc, k, l.Id)) kids.Add(k);
                Dictionary<string, string> ids = new Dictionary<string, string> { { l.Id, c.Id } };
                List<DocLayer> copies = new List<DocLayer>();
                foreach (DocLayer k in kids) { DocLayer kc = k.Duplicate(); ids[k.Id] = kc.Id; copies.Add(kc); }
                foreach (DocLayer kc in copies) kc.Group = ids.ContainsKey(kc.Group) ? ids[kc.Group] : kc.Group;
                int at = doc.Current + 1;
                foreach (DocLayer kc in copies) doc.Layers.Insert(at++, kc);
                doc.Layers.Insert(at, c);
            }
            else doc.Layers.Insert(doc.Current + 1, c);
            // slightly offset, so it's clear there are two
            if (c.Kind == LayerKind.Text) c.Text.Pos = new PointF(c.Text.Pos.X + doc.W / 60f, c.Text.Pos.Y + doc.W / 60f);
            LayerTree.Normalize(doc);
            doc.Current = doc.Layers.IndexOf(c);
            AfterChange(true);
        }

        void MergeDown()
        {
            if (doc == null || doc.Current <= 0) return;
            DocLayer top = doc.Cur, bot = doc.Layers[doc.Current - 1];
            if (top.Kind == LayerKind.Group || bot.Kind == LayerKind.Group || bot.Kind == LayerKind.Adjust) { ShowToast(L.T("ps_cant_merge"), false, null, null); return; }
            Change("ps_h_merge");
            doc.MergeDown(doc.Current);
            AfterChange(true);
        }

        void DeleteLayer()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null) return;
            Change("ps_h_delete_layer");
            int i = doc.Current;
            doc.Layers.RemoveAll(x => x == l || LayerTree.IsInside(doc, x, l.Id));
            doc.Current = Math.Max(0, Math.Min(doc.Layers.Count - 1, i - 1));
            if (doc.Layers.Count == 0) { doc.Layers.Add(PhotoDoc.NewPaint(doc.W, doc.H, doc.NextName("ps_l_layer"))); doc.Current = 0; }
            AfterChange(true);
        }

        void PickPictureLayer()
        {
            if (doc == null) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("ps_add_photo");
                List<string> pats = new List<string>();
                foreach (string e in Formats.ImageExt) pats.Add("*." + e);
                d.Filter = L.T("images") + "|" + string.Join(";", pats.ToArray()) + "|" + L.T("all_files") + "|*.*";
                d.Multiselect = true;
                if (LastDir.Show(d, FindForm(), "studio") == DialogResult.OK) AddPictures(d.FileNames);
            }
        }

        /// Pictures put on the photo as layers of their own (sized to fit, in the middle).
        public void AddPictures(string[] paths)
        {
            if (doc == null) { OpenFiles(paths); return; }
            List<string> pics = new List<string>();
            foreach (string p in paths) if (File.Exists(p) && Formats.CategoryOf(Formats.Ext(p)) == Category.Image) pics.Add(p);
            if (pics.Count == 0) return;
            RunJob(L.T("v_loading"), () =>
            {
                List<KeyValuePair<string, Bitmap>> got = new List<KeyValuePair<string, Bitmap>>();
                foreach (string p in pics) using (Image im = ViewerPage.Decode(p, 8000)) got.Add(new KeyValuePair<string, Bitmap>(p, Pix.Argb(im)));
                return got;
            }, (got, err) =>
            {
                if (err != null || got == null) { ShowToast(L.T("ed_photo_failed") + (err != null ? " " + err : ""), false, null, null); return; }
                if (doc == null) { foreach (KeyValuePair<string, Bitmap> kv in got) kv.Value.Dispose(); return; }
                Change("ps_h_add_photo");
                int n = 0;
                foreach (KeyValuePair<string, Bitmap> kv in got) { AddPictureLayer(kv.Value, Path.GetFileNameWithoutExtension(kv.Key), kv.Key, n++); }
                if (canvas.Tool != PhotoTool.Move) SetTool(PhotoTool.Move);
                AfterChange(true); layers.Reveal();
            });
        }

        void AddPictureLayer(Bitmap b, string name, string path, int n)
        {
            float k = Math.Min(1f, Math.Min(doc.W * 0.8f / b.Width, doc.H * 0.8f / b.Height));
            DocLayer l = new DocLayer { Kind = LayerKind.Image, Name = name, Pixels = b, X = doc.W / 2f + n * doc.W / 30f, Y = doc.H / 2f + n * doc.W / 30f, Scale = k, SourcePath = path };
            InsertAbove(l);
        }

        void AddTextAt(PointF at)
        {
            if (doc == null) return;
            Change("ps_h_text_add");
            TextLayer t = textStyle.CloneStyle();
            t.Text = L.T("ps_new_text");
            t.Pos = at;
            if (t.SizePx < 8 || Math.Abs(t.SizePx - 64) < 0.01f) t.SizePx = Math.Max(24, Math.Min(doc.W, doc.H) / 9f);
            DocLayer l = new DocLayer { Kind = LayerKind.Text, Name = "", Text = t };
            InsertAbove(l);
            AfterChange(true);
            layers.Reveal();
            // straight into typing
            BeginInvoke((Action)(() => { textBox.Box.Focus(); textBox.Box.SelectAll(); }));
        }

        // ------------------------------------------------------------------ selections
        void SelectAll()
        {
            if (doc == null) return;
            byte[] m = new byte[doc.W * doc.H];
            for (int i = 0; i < m.Length; i++) m[i] = 255;
            canvas.ApplySel(m, SelOp.New, "ps_h_select");
        }

        void InvertSel()
        {
            if (doc == null) return;
            if (canvas.BeforeChange != null) canvas.BeforeChange("ps_h_invert");
            byte[] inv = Selection.Invert(doc.Sel, doc.W, doc.H);
            doc.Sel = Selection.Empty(inv) ? null : inv;
            AfterChange(true);
        }

        /// The current layer as it shows on the photo, at full size (a picture turned, text…).
        Bitmap LayerPixels(DocLayer l)
        {
            Bitmap b = new Bitmap(doc.W, doc.H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b)) doc.DrawLayer(g, l, 1f, 1f);
            return b;
        }

        static Bitmap Masked(Bitmap b, byte[] sel)
        {
            int[] px = Pix.Get(b);
            for (int i = 0; i < px.Length; i++) { int a = (int)((uint)px[i] >> 24) * sel[i] / 255; px[i] = (px[i] & 0xFFFFFF) | (a << 24); }
            return Pix.Make(b.Width, b.Height, px);
        }

        /// Ctrl+J: what's selected on this layer becomes a layer of its own (no selection: the layer is copied).
        void LayerViaCopy()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null) return;
            if (doc.Sel == null || !(l.HasPixels || l.Kind == LayerKind.Text)) { DuplicateLayer(); return; }
            Change("ps_h_via_copy");
            Bitmap b;
            using (Bitmap all = LayerPixels(l)) b = Masked(all, doc.Sel);
            DocLayer n = new DocLayer { Kind = LayerKind.Paint, Name = L.T("ps_copy_of", LayersList.NameOf(l)), Pixels = b, X = doc.W / 2f, Y = doc.H / 2f };
            InsertAbove(n);
            AfterChange(true); layers.Reveal();
        }

        void FillSel()
        {
            if (doc == null) return;
            DocLayer l = doc.Cur;
            Change("ps_h_fill");
            if (l == null || l.Kind != LayerKind.Paint || doc.IsLocked(l))
            {
                DocLayer n = PhotoDoc.NewPaint(doc.W, doc.H, doc.NextName("ps_l_layer"));
                InsertAbove(n); l = n;
            }
            l.Pixels = Brush.Fill(l.Pixels, canvas.Fore, doc.Sel);
            l.Version++;
            AfterChange(true);
        }

        /// Delete: what's selected is cleared from the layer (hidden, on a photo); nothing selected: the layer goes.
        void DeleteKey()
        {
            DocLayer l = doc == null ? null : doc.Cur;
            if (l == null) return;
            if (doc.Sel == null) { DeleteLayer(); return; }
            if (doc.IsLocked(l)) { ShowToast(L.T("ps_locked"), false, null, null); return; }
            switch (l.Kind)
            {
                case LayerKind.Paint:
                    Change("ps_h_clear"); l.Pixels = Brush.Clear(l.Pixels, doc.Sel); break;
                case LayerKind.Image:
                    Change("ps_h_clear"); l.Mask = MaskOut(l, doc.Sel); break;
                case LayerKind.Adjust:
                    {
                        Change("ps_h_clear");
                        byte[] m = l.Mask != null ? (byte[])l.Mask.Clone() : MaskEdit.Full(doc.W, doc.H);
                        for (int i = 0; i < m.Length; i++) m[i] = (byte)(m[i] * (255 - doc.Sel[i]) / 255);
                        l.Mask = m; break;
                    }
                default: ShowToast(L.T("ps_cant_clear"), false, null, null); return;
            }
            l.Version++;
            AfterChange(true);
        }

        /// A photo layer's mask with the selection (on the canvas) taken out.
        byte[] MaskOut(DocLayer l, byte[] sel)
        {
            int w = l.Pixels.Width, h = l.Pixels.Height, W = doc.W, H = doc.H;
            byte[] m = l.Mask != null ? (byte[])l.Mask.Clone() : MaskEdit.Full(w, h);
            PointF o = Brush.FromLayer(l, 0.5f, 0.5f), dx = Brush.FromLayer(l, 1.5f, 0.5f), dy = Brush.FromLayer(l, 0.5f, 1.5f);
            float ax = dx.X - o.X, ay = dx.Y - o.Y, bx = dy.X - o.X, by = dy.Y - o.Y;
            for (int y = 0; y < h; y++)
            {
                float rx = o.X + bx * y, ry = o.Y + by * y;
                for (int x = 0; x < w; x++, rx += ax, ry += ay)
                {
                    int sx = (int)rx, sy = (int)ry;
                    if (sx < 0 || sy < 0 || sx >= W || sy >= H) continue;
                    byte s = sel[sy * W + sx];
                    if (s != 0) m[y * w + x] = (byte)(m[y * w + x] * (255 - s) / 255);
                }
            }
            return m;
        }

        void CropToSel()
        {
            if (doc == null || doc.Sel == null) return;
            Rectangle r = Selection.Bounds(doc.Sel, doc.W, doc.H);
            if (r.IsEmpty) return;
            Change("ps_h_crop");
            doc.Crop(r);
            doc.Sel = null;
            canvas.Fit();
            AfterChange(true);
        }

        void ApplyCrop()
        {
            if (doc == null) return;
            RectangleF c = canvas.CropRect;
            Rectangle r = Rectangle.Round(c);
            r.Intersect(new Rectangle(0, 0, doc.W, doc.H));
            if (r.Width < 2 || r.Height < 2) return;
            if (r.Width == doc.W && r.Height == doc.H) { SetTool(PhotoTool.Move); return; }
            Change("ps_h_crop");
            doc.Crop(r);
            canvas.CropRect = RectangleF.Empty;
            SetTool(PhotoTool.Move);
            canvas.Fit();
            AfterChange(true);
        }

        // ------------------------------------------------------------------ menus
        void CanvasMenu(Point at)
        {
            if (doc == null) return;
            Point screen = canvas.PointToScreen(at);
            DocLayer l = doc.Cur;
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            if (doc.Sel != null)
            {
                m.Add(new MenuPopup.Item { Header = L.T("ps_with_sel") });
                m.Add(new MenuPopup.Item { Text = L.T("ps_sel_copy"), Glyph = Ico.Copy, Shortcut = "Ctrl+J", Run = LayerViaCopy });
                m.Add(new MenuPopup.Item { Text = L.T("ps_sel_fill"), Glyph = Ps.Fill, Shortcut = "Shift+F5", Run = FillSel });
                m.Add(new MenuPopup.Item { Text = L.T("ps_sel_clear"), Glyph = Ico.Delete, Shortcut = "Del", Run = DeleteKey });
                m.Add(new MenuPopup.Item { Text = L.T("ps_sel_crop"), Glyph = Ico.Crop, Run = CropToSel });
                m.Add(new MenuPopup.Item { Text = L.T("ps_new_adj_here"), Glyph = Ps.AdjLayer, Run = () => NewAdjustment(AdjKind.Light) });
                m.Add(new MenuPopup.Item { Text = L.T("ps_sel_invert"), Glyph = Ps.Invert, Shortcut = "Ctrl+Shift+I", Run = InvertSel });
                m.Add(new MenuPopup.Item { Text = L.T("ps_sel_none"), Glyph = Ico.Close, Shortcut = "Ctrl+D", Run = () => canvas.ApplySel(null, SelOp.New, "ps_h_deselect") });
                m.Add(new MenuPopup.Item { Divider = true });
            }
            if (l != null) LayerItems(m, l);
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("ocr_btn"), Glyph = "", Run = CopyText });
            m.Add(new MenuPopup.Item { Text = L.T("ps_paste"), Glyph = Ico.Paste, Shortcut = "Ctrl+V", Run = Paste, Disabled = !Clipboard.ContainsImage() });
            MenuPopup.ShowAt(canvas, screen, m);
        }

        void LayerMenu(DocLayer l, Point screen)
        {
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("ps_rename"), Glyph = Ico.Edit, Shortcut = "F2", Run = () => layers.StartRename(l) });
            LayerItems(m, l);
            MenuPopup.ShowAt(layers, screen, m);
        }

        void LayerItems(List<MenuPopup.Item> m, DocLayer l)
        {
            m.Add(new MenuPopup.Item { Text = L.T("ps_duplicate"), Glyph = Ico.Copy, Shortcut = "Ctrl+J", Run = DuplicateLayer });
            if (l.Kind == LayerKind.Group) m.Add(new MenuPopup.Item { Text = L.T("ps_ungroup"), Glyph = Ico.Folder, Shortcut = "Ctrl+Shift+G", Run = Ungroup });
            else m.Add(new MenuPopup.Item { Text = L.T("ps_group"), Glyph = Ico.Folder, Shortcut = "Ctrl+G", Run = GroupLayer });
            m.Add(new MenuPopup.Item { Text = L.T("ps_merge"), Glyph = Ps.MergeDown, Shortcut = "Ctrl+E", Run = MergeDown, Disabled = !lMerge.Enabled });
            if (l.Kind == LayerKind.Text || l.Kind == LayerKind.Image)
                m.Add(new MenuPopup.Item { Text = L.T("ps_rasterize"), Sub = L.T("ps_rasterize_sub"), Glyph = Ps.Brush, Run = () => { Change("ps_h_rasterize"); doc.Rasterize(doc.Current); AfterChange(true); } });
            if (l.Kind == LayerKind.Image) m.Add(new MenuPopup.Item { Text = L.T(BgRemover.Ready ? "ps_remove_bg" : "bg_download"), Glyph = Ps.Subject, Run = RemoveLayerBackground });
            if (l.Mask != null) m.Add(new MenuPopup.Item { Text = L.T("ps_show_all"), Glyph = Ico.Refresh, Run = () => { Change("ps_h_mask"); l.Mask = null; l.Version++; AfterChange(true); } });
            m.Add(new MenuPopup.Item { Text = L.T(l.Locked ? "ps_unlock" : "ps_lock"), Glyph = l.Locked ? Ps.Unlock : Ps.Lock, Run = () => { Change("ps_h_lock"); l.Locked = !l.Locked; AfterChange(true); } });
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("ps_delete_layer"), Glyph = Ico.Delete, Danger = true, Run = DeleteLayer });
        }

        void ZoomMenu()
        {
            if (doc == null) return;
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("ps_fit"), Glyph = Ps.Fit, Shortcut = "Ctrl+0", Run = () => canvas.Fit() });
            foreach (int p in new[] { 25, 50, 100, 200, 400 })
            {
                int pp = p;
                m.Add(new MenuPopup.Item { Text = p + "%", Shortcut = p == 100 ? "Ctrl+1" : null, Run = () => canvas.ZoomCenter(pp / 100f) });
            }
            MenuPopup.Show(bZoom, m, 200);
        }

        void MoreMenu()
        {
            if (doc == null) return;
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("ps_save_project"), Sub = L.T("ps_save_project_sub"), Glyph = Ico.Save, Shortcut = "Ctrl+S", Run = () => SaveProject(false) });
            if (doc.ProjectPath != null) m.Add(new MenuPopup.Item { Text = L.T("ps_save_project_as"), Glyph = Ico.Save, Shortcut = "Ctrl+Shift+S", Run = () => SaveProject(true) });
            m.Add(new MenuPopup.Item { Text = L.T("ps_new_canvas"), Glyph = Ps.NewCanvas, Shortcut = "Ctrl+N", Run = NewCanvasDialog });
            m.Add(new MenuPopup.Item { Text = L.T("v_open"), Glyph = Ico.FolderOpen, Shortcut = "Ctrl+O", Run = PickFile });
            m.Add(new MenuPopup.Item { Header = L.T("ps_image") });
            m.Add(new MenuPopup.Item { Text = L.T("ps_image_size"), Glyph = Ico.Resize, Run = ImageSizeDialog });
            m.Add(new MenuPopup.Item { Text = L.T("lm_turn_left"), Glyph = "", Run = () => TurnDoc(3) });
            m.Add(new MenuPopup.Item { Text = L.T("lm_turn"), Glyph = Ico.Rotate, Run = () => TurnDoc(1) });
            m.Add(new MenuPopup.Item { Text = L.T("ps_flip_all_h"), Glyph = Ico.FlipH, Run = () => FlipDoc(true) });
            m.Add(new MenuPopup.Item { Text = L.T("ps_flip_all_v"), Glyph = Ico.FlipV, Run = () => FlipDoc(false) });
            m.Add(new MenuPopup.Item { Text = L.T("ps_flatten"), Sub = L.T("ps_flatten_sub"), Glyph = Ps.MergeDown, Run = () => { Change("ps_h_flatten"); doc.FlattenAll(); AfterChange(true); }, Disabled = doc.Layers.Count < 2 });
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("ocr_btn"), Sub = L.T("ps_ocr_sub"), Glyph = "", Run = CopyText });
            if (doc.SourcePath != null && File.Exists(doc.SourcePath))
            {
                m.Add(new MenuPopup.Item { Text = L.T("v_convert"), Glyph = Ico.Sync, Run = () => { if (SendToConverter != null) SendToConverter(doc.SourcePath); } });
                m.Add(new MenuPopup.Item { Text = L.T("v_folder"), Glyph = Ico.FolderOpen, Run = () => { try { Process.Start("explorer.exe", "/select,\"" + doc.SourcePath + "\""); } catch { } } });
            }
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("ps_shortcuts"), Glyph = "", Run = ShowShortcuts });
            MenuPopup.Show(bMore, m, 320);
        }

        void ShowShortcuts()
        {
            Ask.Info(FindForm(), L.T("ps_shortcuts"), L.T("ps_shortcuts_list"));
        }

        // ------------------------------------------------------------------ the whole photo
        void TurnDoc(int quarters)
        {
            if (doc == null) return;
            Change(quarters == 1 ? "lm_turn" : "lm_turn_left");
            doc.TurnLayers(quarters);
            canvas.Fit();
            AfterChange(true);
        }

        void FlipDoc(bool h)
        {
            if (doc == null) return;
            Change("ps_h_flip");
            doc.FlipAll(h);
            AfterChange(true);
        }

        void ImageSizeDialog()
        {
            if (doc == null) return;
            using (ImageSizeForm f = new ImageSizeForm(doc.W, doc.H))
            {
                if (Ask.ShowDimmed(f, FindForm()) != DialogResult.OK) return;
                if (f.W == doc.W && f.H == doc.H) return;
                Change("ps_h_image_size");
                doc.Resize(f.W, f.H);
                canvas.Fit();
                AfterChange(true);
            }
        }

        void NewCanvasDialog()
        {
            using (NewCanvasForm f = new NewCanvasForm(canvas.Fore))
            {
                if (Ask.ShowDimmed(f, FindForm()) != DialogResult.OK) return;
                NewCanvas(f.W, f.H, f.Background, L.T("ps_untitled"));
            }
        }

        // ------------------------------------------------------------------ saving
        string BaseName { get { string t = doc.SourcePath != null ? Path.GetFileNameWithoutExtension(doc.SourcePath) : Path.GetFileNameWithoutExtension(doc.Title); return string.IsNullOrEmpty(t) ? L.T("ps_untitled") : t; } }

        public void SaveProject(bool askAgain)
        {
            if (doc == null || busyJob != null) return;
            string path = doc.ProjectPath;
            if (path == null || askAgain)
            {
                path = SaveAsk.File(FindForm(), BaseName + "." + PhotoDoc.ProjectExt, PhotoDoc.ProjectExt);
                if (path == null) return;
            }
            int at = changes;
            busyJob = L.T("ps_saving"); InvalidateBar();
            SaveInBackground(path, err =>
            {
                busyJob = null; InvalidateBar();
                if (err != null) { ShowToast(err, false, null, null); return; }
                if (doc == null) return;
                doc.ProjectPath = path; doc.Title = Path.GetFileName(path);
                savedAt = at;
                Recovery.Clear(Recovery.PhotoProject);
                RecentFiles.Add("studio", path);
                ShowToast(L.T("ps_project_saved", Path.GetFileName(path)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } });
                Invalidate();
            });
        }

        public void Export()
        {
            if (doc == null || busyJob != null) return;
            if (canvas.Tool == PhotoTool.Crop) ApplyCrop();
            Bitmap flat = doc.Flatten();
            bool alpha = HasAlpha(flat);
            using (ExportForm f = new ExportForm(flat, alpha))
            {
                if (Ask.ShowDimmed(f, FindForm()) != DialogResult.OK) { flat.Dispose(); return; }
                string ext = f.Ext;
                string path = SaveAsk.File(FindForm(), BaseName + " " + L.T("ed_suffix") + "." + ext, ext);
                if (path == null) { flat.Dispose(); return; }
                int q = f.Quality; float scale = f.OutScale;
                int at = changes;
                RunJob(L.T("ps_exporting"), () =>
                {
                    try { ExportForm.Write(flat, path, ext, q, scale); return path; }
                    finally { flat.Dispose(); }
                }, (p, err) =>
                {
                    if (err != null) { ShowToast(err, false, null, null); return; }
                    savedAt = at;
                    Recovery.Clear(Recovery.PhotoProject);
                    RecentFiles.Add("studio", path);
                    ShowToast(L.T("v_saved", Path.GetFileName(path)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } });
                    Invalidate();
                });
            }
        }

        static bool HasAlpha(Bitmap b)
        {
            int[] px = Pix.Get(b);
            for (int i = 0; i < px.Length; i += 7) if (((uint)px[i] >> 24) < 250) return true;
            return false;
        }

        // ------------------------------------------------------------------ clipboard, text
        void CopyPicture()
        {
            if (doc == null) return;
            using (Bitmap flat = doc.Flatten())
            {
                Bitmap outp;
                if (doc.Sel != null)
                {
                    Rectangle r = Selection.Bounds(doc.Sel, doc.W, doc.H);
                    if (r.IsEmpty) return;
                    using (Bitmap m = Masked(flat, doc.Sel)) outp = m.Clone(r, PixelFormat.Format32bppArgb);
                }
                else outp = new Bitmap(flat);
                try { Clipboard.SetImage(outp); ShowToast(L.T("ps_copied"), true, null, null); } catch { }
                outp.Dispose();
            }
        }

        void Paste()
        {
            Image im = null;
            try { if (Clipboard.ContainsImage()) im = Clipboard.GetImage(); } catch { }
            if (im == null)
            {
                try { if (Clipboard.ContainsFileDropList()) { System.Collections.Specialized.StringCollection f = Clipboard.GetFileDropList(); string[] a = new string[f.Count]; f.CopyTo(a, 0); AcceptFiles(a); } } catch { }
                return;
            }
            Bitmap b = Pix.Argb(im); im.Dispose();
            if (doc == null) { PhotoDoc d = PhotoDoc.FromPhoto(b, null); d.Title = L.T("ps_pasted"); SetDoc(d); b.Dispose(); return; }
            Change("ps_h_paste");
            AddPictureLayer(b, L.T("ps_pasted"), null, 0);
            if (canvas.Tool != PhotoTool.Move) SetTool(PhotoTool.Move);
            AfterChange(true);
        }

        /// Reads the text in the photo (just the selected part, when something is selected) and copies it.
        public void CopyText()
        {
            if (doc == null) { PickFile(); return; }
            string tmp = Path.Combine(Path.GetTempPath(), "FadiOcr_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            using (Bitmap flat = doc.Flatten()) flat.Save(tmp, ImageFormat.Png);
            RectangleF area = new RectangleF(0, 0, 1, 1);
            if (doc.Sel != null)
            {
                Rectangle r = Selection.Bounds(doc.Sel, doc.W, doc.H);
                if (!r.IsEmpty) area = new RectangleF(r.X / (float)doc.W, r.Y / (float)doc.H, r.Width / (float)doc.W, r.Height / (float)doc.H);
            }
            Ocr.Run(this, tmp, 0, area, (msg, ok) => ShowToast(msg, ok, null, null), null);
        }
    }
}
