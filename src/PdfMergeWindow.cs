using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// The Donut Wheel's "Merge PDFs" and "Combine into PDF": the files in a list to put in order (drag a row, the
    /// arrows, by name or by date, reversed), each with its first page and page count, and a name for the new PDF.
    /// Nothing is joined until Merge is pressed.
    public class PdfMergeWindow : MiniWindow
    {
        readonly List<MergeItem> original = new List<MergeItem>();
        readonly MergeList list = new MergeList();
        readonly Segments order;
        readonly TextField name;
        readonly Progress bar = new Progress { Visible = false };
        readonly Btn bGo, bCancel, bShow, bReverse;
        readonly Control activitySource;
        readonly bool pictures;   // pictures among them: "Combine", otherwise "Merge"
        bool busy, cancel, done;
        string result, status; Color statusColor;
        Rectangle orderR, nameR, extR, summaryR, statusR, hintR;

        public static void Open(string[] files, Point at, Control activitySource)
        {
            if (files == null || files.Length == 0) return;
            PdfMergeWindow w = new PdfMergeWindow(files, activitySource);
            w.ShowNear(at);
            w.Load2();
        }

        static bool AnyPicture(string[] files) { foreach (string f in files) if (Formats.Ext(f) != "pdf") return true; return false; }

        PdfMergeWindow(string[] files, Control activitySource) : base(() => L.T(AnyPicture(files) ? "pm_title_pics" : "pm_title"), Ico.Combine)
        {
            this.activitySource = activitySource;
            pictures = AnyPicture(files);
            foreach (string f in files)
            {
                MergeItem it = new MergeItem { File = f, Glyph = Formats.Ext(f) == "pdf" ? Ico.Doc : Ico.Photo };
                try { FileInfo fi = new FileInfo(f); it.When = fi.LastWriteTime; it.Bytes = fi.Length; } catch { }
                original.Add(it); list.Items.Add(it);
            }
            list.ShellThumbs = true; list.ThumbW = 42; list.FirstGlyph = Ico.Open;
            list.SubText = Sub;
            list.Changed += () => { AfterChange(); };
            list.PlayAsked += it => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.File) { UseShellExecute = true }); } catch { } };
            order = new Segments(() => L.T("mg_as_dragged"), () => L.T("mg_by_name"), () => L.T("mg_by_date")) { FontSize = 8.75f, Surface = Surface.Card };
            order.SelectedChanged += (s, e) => Sort();
            bReverse = new Btn(() => "", "", BtnKind.Ghost) { Surface = Surface.Card, GlyphSize = 10f, AccessibleName = L.T("mg_reverse") };
            bReverse.Click += (s, e) => { list.Items.Reverse(); list.Invalidate(); AfterChange(); };
            new ToolTip().SetToolTip(bReverse, L.T("mg_reverse"));
            name = new TextField(() => L.T("mg_name_hint"), null) { Surface = Surface.Card };
            name.Box.Text = SuggestName(files[0]);
            name.Box.TextChanged += (s, e) => { if (done) Reset(); };
            bGo = new Btn(() => L.T(done ? "wn_ok" : pictures ? "pm_go_pics" : "pm_go"), Ico.Combine, BtnKind.Primary) { Surface = Surface.Card, Enabled = false };
            bGo.Click += (s, e) => { if (done) Dismiss(); else Go(); };
            bCancel = new Btn(() => L.T(busy ? "cancel" : "close"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { if (busy) cancel = true; else Dismiss(); };
            bShow = new Btn(() => L.T("show_file"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, Visible = false, FontSize = 9f };
            bShow.Click += (s, e) => { if (result != null) ShowInFolder(result); };
            Controls.AddRange(new Control[] { list, order, bReverse, name, bar, bGo, bCancel, bShow });
            int rows = Math.Max(3, Math.Min(6, files.Length));
            ClientSize = new Size(Theme.S(600), Theme.S(300) + rows * list.RowH);
        }

        static string SuggestName(string first)
        {
            string n = Path.GetFileNameWithoutExtension(first);
            if (n.Length > 40) n = n.Substring(0, 40);
            return n + " " + L.T("pdf_combined_suffix");
        }

        string Sub(MergeItem it)
        {
            string kind = Formats.Ext(it.File) == "pdf"
                ? (it.Pages > 0 ? Pages(it.Pages) : it.Ready ? "PDF" : L.T("mg_reading"))
                : L.T("pm_picture");
            if (it.Bad) kind = L.T("pm_locked");
            return kind + "  ·  " + Ltr(Theme.HumanSize(it.Bytes)) + "  ·  " + Ltr(it.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        static string Pages(int n) { return n == 1 ? L.T("pm_1_page") : L.T("pdf_n_pages", n); }
        /// Sizes and dates stay in their own order inside Arabic text.
        static string Ltr(string s) { return Theme.RTL ? "\u202A" + s + "\u202C" : s; }

        /// Counts each PDF's pages in the background (the list can be put in order meanwhile).
        void Load2()
        {
            List<MergeItem> todo = new List<MergeItem>(original);
            Thread th = new Thread(() =>
            {
                foreach (MergeItem it in todo)
                {
                    int pages = -1; bool locked = false;
                    if (Formats.Ext(it.File) == "pdf")
                    {
                        try { pages = Qpdf.Pages(it.File); } catch { }
                        // only a PDF that needs a password to open is left out (one that only stops printing or copying joins fine)
                        try { locked = Qpdf.Ready && Qpdf.LooksLocked(it.File) && Qpdf.Lock(it.File) == 0; } catch { }
                    }
                    else pages = 1;
                    MergeItem x = it; int n = pages; bool lk = locked;
                    Ui(() => { x.Pages = n; x.Bad = lk; x.Ready = true; AfterChange(); });
                }
            }) { IsBackground = true };
            th.Start();
            AfterChange();
        }

        void AfterChange()
        {
            bGo.Enabled = !busy && (done || Good.Count >= 2);
            list.Invalidate(); Invalidate();
        }

        void Sort()
        {
            List<MergeItem> l = new List<MergeItem>(original);
            l.RemoveAll(x => !list.Items.Contains(x));
            if (order.SelectedIndex == 1) l.Sort((a, b) => RenamePage.NaturalCompare(a.Name, b.Name));
            else if (order.SelectedIndex == 2) l.Sort((a, b) => a.When.CompareTo(b.When));
            list.Items = l;
            AfterChange();
        }

        /// The files that go in (a PDF locked with a password can't be read, so it's left out).
        List<MergeItem> Good { get { List<MergeItem> l = new List<MergeItem>(); foreach (MergeItem it in list.Items) if (!it.Bad) l.Add(it); return l; } }

        string Summary()
        {
            List<MergeItem> l = Good;
            int pages = 0; bool known = true; long bytes = 0;
            foreach (MergeItem it in l) { if (it.Pages > 0) pages += it.Pages; else known = false; bytes += it.Bytes; }
            return L.T("pm_summary", l.Count) + (known && pages > 0 ? "  ·  " + Pages(pages) : "");
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (list == null || bGo == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad, cw = W - p * 2;
            int y = Head + Theme.S(2);
            orderR = new Rectangle(p, y, Theme.S(110), Theme.S(32));
            int ow = Math.Min(Theme.S(330), cw - Theme.S(160));
            order.Bounds = M(new Rectangle(W - p - Theme.S(38) - ow, y, ow, Theme.S(32)));
            bReverse.Bounds = M(new Rectangle(W - p - Theme.S(32), y, Theme.S(32), Theme.S(32)));
            y += Theme.S(40);
            int bottomBlock = Theme.S(196);
            int lh = Math.Max(list.RowH * 2, H - y - bottomBlock);
            list.Bounds = M(new Rectangle(p, y, cw, lh));
            y += lh + Theme.S(6);
            hintR = new Rectangle(p, y, cw, Theme.S(20));
            y += Theme.S(26);
            nameR = new Rectangle(p, y, Theme.S(110), Theme.S(34));
            extR = new Rectangle(W - p - Theme.S(44), y, Theme.S(44), Theme.S(34));
            name.Bounds = M(new Rectangle(p + Theme.S(114), y, cw - Theme.S(114) - Theme.S(48), Theme.S(34)));
            y += Theme.S(44);
            summaryR = new Rectangle(p, y, cw, Theme.S(22));
            y += Theme.S(24);
            bar.Bounds = M(new Rectangle(p, y + Theme.S(2), cw, Theme.S(6)));
            statusR = new Rectangle(p, y + Theme.S(8), cw, Theme.S(28));
            int by = H - Theme.S(20) - Theme.S(40);
            int gw = Math.Max(Theme.S(130), bGo.PreferredWidth + Theme.S(12)), xw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bGo.Bounds = M(new Rectangle(W - p - gw, by, gw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - gw - Theme.S(8) - xw, by, xw, Theme.S(40)));
            bShow.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(110), bShow.PreferredWidth), Theme.S(34)));
        }

        protected override void PaintBody(Graphics g)
        {
            Font lf = Theme.F(9f, FontStyle.Bold);
            Theme.Text(g, L.T("mg_order"), lf, Theme.Ink, M(orderR), Align.Start);
            Theme.Text(g, L.T("pm_hint"), Theme.F(8.25f), Theme.InkFaint, M(hintR), Align.Start, true, false, true);
            Theme.Text(g, L.T("mg_name"), lf, Theme.Ink, M(nameR), Align.Start);
            Theme.Text(g, ".pdf", Theme.F(9.5f), Theme.InkSoft, M(extR), Align.Start, true, false, true);
            Theme.Text(g, Summary(), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, M(summaryR), Align.Start, true, false, true);
            if (status != null) Theme.Text(g, status, Theme.F(9f, done ? FontStyle.Bold : FontStyle.Regular), statusColor, M(statusR), Align.Start, true, true);
        }

        protected override bool CanClose() { if (busy) { cancel = true; return false; } return true; }

        void Reset()
        {
            done = false; result = null; status = null;
            bShow.Visible = false; bCancel.Visible = true; bGo.Glyph = Ico.Combine;
            AfterChange(); bGo.Invalidate(); DoLayout(); Invalidate();
        }

        void Go()
        {
            if (busy || done) return;
            List<MergeItem> l = Good;
            if (l.Count < 2) return;
            bool anyPdf = false;
            foreach (MergeItem it in l) if (Formats.Ext(it.File) == "pdf") anyPdf = true;
            // PDFs are joined with qpdf: it's downloaded the first time (asked first, like on the PDF page)
            if (anyPdf && !Qpdf.Ready && !Ask.Confirm(this, L.T("pdf_need_title"), L.T("pdf_need_q"), L.T("install"), L.T("not_now"), AskKind.Info)) return;
            string nm = name.Box.Text.Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) nm = nm.Replace(ch, '_');
            nm = nm.Trim().TrimEnd('.');
            if (nm.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) nm = nm.Substring(0, nm.Length - 4).Trim();
            if (nm.Length == 0) nm = SuggestName(l[0].File);
            string outp = Paths.UniquePath(DragWheel.OutDirFor(l[0].File), nm, "pdf");
            List<string> files = new List<string>();
            foreach (MergeItem it in l) files.Add(it.File);
            busy = true; cancel = false;
            bGo.Enabled = false; list.Enabled = order.Enabled = name.Enabled = bReverse.Enabled = false;
            bCancel.Invalidate();
            bar.Visible = true; bar.Value = 0;
            status = L.T("pm_working"); statusColor = Theme.InkSoft; Invalidate();
            ActJob act = Activity.Begin(activitySource, L.T("act_k_pdf_combine"), Path.GetFileName(outp), () => cancel = true);
            Thread th = new Thread(() =>
            {
                string err = null;
                try { QuickPdf.Combine(files, outp, v => { Activity.Update(act, v, (int)(v * 100) + "%"); Ui(() => bar.Value = v); }, () => cancel); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                if (cancel || err != null) try { if (File.Exists(outp)) File.Delete(outp); } catch { }
                if (err == null && !cancel && !File.Exists(outp)) err = L.T("mg_failed");
                int pages = Qpdf.Ready && err == null && !cancel ? Qpdf.Pages(outp) : -1;
                Activity.End(act, cancel ? null : err, cancel, err == null ? L.T("pdf_done_combine", Path.GetFileName(outp)) + (pages > 0 ? "  ·  " + L.T("pdf_n_pages", pages) : "") : null, err == null && !cancel ? outp : null);
                Ui(() =>
                {
                    busy = false; bar.Visible = false;
                    list.Enabled = order.Enabled = name.Enabled = bReverse.Enabled = true;
                    if (cancel) { status = L.T("cancelled"); statusColor = Theme.InkSoft; AfterChange(); bCancel.Invalidate(); return; }
                    if (err != null) { status = err; statusColor = Theme.Error; AfterChange(); bCancel.Invalidate(); return; }
                    done = true; result = outp;
                    long sz = 0; try { sz = new FileInfo(outp).Length; } catch { }
                    status = L.T("up_saved", Path.GetFileName(outp), Theme.HumanSize(sz)); statusColor = Theme.Success;
                    bGo.Enabled = true; bGo.Glyph = Ico.Check; bCancel.Visible = false; bShow.Visible = true;
                    DoLayout(); Invalidate(); bGo.Invalidate();
                    if (Settings.OpenAfter) ShowInFolder(outp);
                });
            }) { IsBackground = true };
            th.Start();
        }
    }
}
