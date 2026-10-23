using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    public class ConvertPage : Page
    {
        Rectangle left, right, filesTitleR, filesCountR, convToR, qualityR, saveToR, statusR, summaryR;
        Btn btnAdd, btnClear, btnConvert, btnOpen, btnDocs, btnView;
        FileList list;
        DropZone drop;
        Segments tabs, quality, stickerFor;
        static readonly string[] StickerKeys = { "wa", "tg", "dc", "emoji" };
        bool Sticker { get { return chips.Selected == "sticker"; } }
        ChipGrid chips;
        FolderButton folder;
        Progress progress;

        string statusText = ""; Theme.LiveColor statusColor;
        bool running, cancel, userPicked;
        string lastOutputFile, lastOutputDir;
        int doneCount;

        public ConvertPage() : base("conv_title", "conv_sub")
        {
            btnAdd = new Btn(() => L.T("add_files"), Ico.Add, BtnKind.Secondary);
            btnAdd.Click += (s, e) => Browse();
            btnClear = new Btn(() => L.T("clear"), Ico.Delete, BtnKind.Ghost);
            btnClear.Click += (s, e) => Fx.SweepAway(list, list.VisibleRows(null), () =>
            {
                list.Items.Clear(); list.Invalidate(); userPicked = false; progress.Value = 0; btnOpen.Visible = false; SetStatus("", Theme.InkSoft); DoLayout(); UpdateState();
            });

            list = new FileList();
            list.Previews = Settings.Get("ConvertPreviews", "1") == "1";
            // List / Previews: big rows with a picture of each file
            btnView = new Btn(() => L.T(list.Previews ? "view_list" : "view_previews"), list.Previews ? "\uE8FD" : "\uF0E2", BtnKind.Ghost);
            btnView.Click += (s, e) => { list.Previews = !list.Previews; Settings.Set("ConvertPreviews", list.Previews ? "1" : "0"); btnView.Glyph = list.Previews ? "\uE8FD" : "\uF0E2"; DoLayout(); };
            list.Changed += () => UpdateState();
            list.Opened += OpenItem;

            drop = new DropZone(() => L.T("drop_title"), () => L.T("drop_sub")) { Tips = new[] { "tip_menu", "tip_drag_out", "tip_conv_folder" } };
            drop.Click += (s, e) => Browse();

            List<Func<string>> tabLabels = new List<Func<string>>();
            foreach (string k in Formats.TabKeys) { string key = k; tabLabels.Add(() => L.T(key)); }
            tabs = new Segments(tabLabels.ToArray());
            tabs.FontSize = 9f;
            tabs.FadeTarget = this;
            tabs.FadeArea = () => new Rectangle(0, tabs.Bottom + Theme.S(2), Width, Math.Max(0, Height - tabs.Bottom - Theme.S(2)));
            tabs.SelectedChanged += (s, e) => { chips.SetItems(Formats.Targets[tabs.SelectedIndex], chips.Selected); DoLayout(); UpdateState(); };
            tabs.MouseClick += (s, e) => userPicked = true;

            chips = new ChipGrid();
            chips.LabelOf = k => k == "sticker" ? L.T("st_chip") : k.ToUpperInvariant();
            chips.SetItems(Formats.Targets[0], "jpg");
            chips.MouseClick += (s, e) => userPicked = true;
            chips.SelectedChanged += (s, e) => UpdateState();

            quality = new Segments(() => L.T("q_best"), () => L.T("q_balanced"), () => L.T("q_small"));
            quality.FontSize = 9f;
            int q; int.TryParse(Settings.Get("Quality", "1"), out q);
            quality.SetSilently(Math.Max(0, Math.Min(2, q)));
            quality.SelectedChanged += (s, e) => Settings.Set("Quality", quality.SelectedIndex.ToString());
            stickerFor = new Segments(() => "WhatsApp", () => "Telegram", () => "Discord", () => L.T("st_emoji")) { FontSize = 8.75f };
            int sf; int.TryParse(Settings.Get("StickerFor", "0"), out sf);
            stickerFor.SetSilently(Math.Max(0, Math.Min(3, sf)));
            stickerFor.SelectedChanged += (s, e) => { Settings.Set("StickerFor", stickerFor.SelectedIndex.ToString()); Invalidate(); };

            btnDocs = new Btn(() => L.T("enable_docs"), Ico.Download, BtnKind.Secondary);
            btnDocs.Click += (s, e) => LibreOffice.Enable(this, ok => { UpdateState(); if (ok) ShowToast(L.T("docs_ready"), true, null, null); });

            folder = new FolderButton();
            folder.Picked += (s, e) => UpdateState();

            progress = new Progress();
            btnConvert = new Btn(() => running ? L.T("cancel") : (list.Items.Count == 0 ? L.T("convert_need_files") : Sticker ? L.T("st_make") : L.T("convert_btn", (chips.Selected ?? "").ToUpperInvariant())), Ico.Sync, BtnKind.Primary);
            btnConvert.Click += (s, e) => { if (running) { cancel = true; SetStatus(L.T("cancelling"), Theme.InkSoft); } else Start(); };
            btnOpen = new Btn(() => doneCount == 1 ? L.T("show_file") : L.T("open_folder"), Ico.FolderOpen, BtnKind.Secondary);
            btnOpen.Visible = false;
            btnOpen.Click += (s, e) => OpenOutput();

            Controls.AddRange(new Control[] { btnAdd, btnClear, btnView, list, drop, tabs, chips, quality, stickerFor, btnDocs, folder, progress, btnConvert, btnOpen });
            EnableDrop(list, drop);
            UpdateState();
        }

        public override bool Busy { get { return running; } }

        public override void DoLayout()
        {
            int W = Width, H = Height;
            int p = Pad, gap = Theme.S(20), rightW = Theme.S(360);
            left = new Rectangle(p, Top0, W - p * 2 - rightW - gap, H - Top0 - p);
            right = new Rectangle(left.Right + gap, Top0, rightW, H - Top0 - p);
            cards.Clear(); cards.Add(left); cards.Add(right);

            int ip = Theme.S(20);
            filesTitleR = new Rectangle(left.X + ip, left.Y + Theme.S(16), Theme.S(70), Theme.S(32));
            filesCountR = new Rectangle(filesTitleR.Right, left.Y + Theme.S(16), Theme.S(200), Theme.S(32));
            int bw = Math.Max(Theme.S(90), btnClear.PreferredWidth);
            Place(btnClear, new Rectangle(left.Right - ip - bw, left.Y + Theme.S(14), bw, Theme.S(36)));
            int aw = Math.Max(Theme.S(120), btnAdd.PreferredWidth);
            bool empty = list.Items.Count == 0;
            Place(btnAdd, new Rectangle(left.Right - ip - aw - (empty ? 0 : bw + Theme.S(8)), left.Y + Theme.S(14), aw, Theme.S(36)));
            // with room: "Previews" / "List" in words, otherwise just the icon
            bool viewWords = left.Width > Theme.S(640);
            btnView.Label = viewWords ? (Func<string>)(() => L.T(list.Previews ? "view_list" : "view_previews")) : () => "";
            int vw = viewWords ? Math.Max(Theme.S(100), btnView.PreferredWidth) : Theme.S(36);
            btnView.Glyph = list.Previews ? "\uE8FD" : "\uF0E2";
            btnView.Visible = !empty && left.Width > Theme.S(420);
            Place(btnView, new Rectangle(left.Right - ip - aw - bw - vw - Theme.S(16), left.Y + Theme.S(14), vw, Theme.S(36)));
            Place(list, new Rectangle(left.X + Theme.S(6), left.Y + Theme.S(64), left.Width - Theme.S(12), left.Height - Theme.S(72)));
            Place(drop, new Rectangle(left.X + ip, left.Y + Theme.S(64), left.Width - ip * 2, left.Height - Theme.S(64) - ip));

            int x = right.X + ip, w = right.Width - ip * 2, y = right.Y + Theme.S(18);
            bool compact = right.Height < Theme.S(520);
            convToR = new Rectangle(x, y, w, Theme.S(26)); y += Theme.S(34);
            Place(tabs, new Rectangle(x, y, w, Theme.S(38))); y += Theme.S(compact ? 46 : 52);
            chips.RowH = compact ? 32 : 36;
            Place(chips, new Rectangle(x, y, w, chips.NeededHeight(Formats.Targets[tabs.SelectedIndex].Length)));
            y += chips.NeededHeight(10) + Theme.S(compact ? 14 : 20);
            qualityR = new Rectangle(x, y, w, Theme.S(24));
            Place(quality, new Rectangle(x, y + Theme.S(28), w, Theme.S(34)));
            Place(stickerFor, new Rectangle(x, y + Theme.S(28), w, Theme.S(34)));
            Place(btnDocs, new Rectangle(x, y + Theme.S(28), w, Theme.S(36)));
            y += Theme.S(28) + Theme.S(34) + Theme.S(compact ? 14 : 22);
            saveToR = new Rectangle(x, y, w, Theme.S(24)); y += Theme.S(28);
            Place(folder, new Rectangle(x, y, w, Theme.S(58)));
            y += Theme.S(58) + Theme.S(compact ? 14 : 22);

            int bottom = right.Bottom - ip;
            int bh = Theme.S(compact ? 44 : 50);
            // the "HEIC → JPG" summary fills the space above the progress bar (only when there is room for it)
            int sumBottom = bottom - bh - Theme.S(54) - Theme.S(14);
            summaryR = sumBottom - y >= Theme.S(84) ? new Rectangle(x, y, w, Theme.S(84)) : Rectangle.Empty;
            if (btnOpen.Visible)
            {
                int ow = (w - Theme.S(10)) * 2 / 5;
                Place(btnOpen, new Rectangle(x, bottom - bh, ow, bh));
                Place(btnConvert, new Rectangle(x + ow + Theme.S(10), bottom - bh, w - ow - Theme.S(10), bh));
            }
            else Place(btnConvert, new Rectangle(x, bottom - bh, w, bh));
            statusR = new Rectangle(x, bottom - bh - Theme.S(46), w, Theme.S(38));
            Place(progress, new Rectangle(x, bottom - bh - Theme.S(54), w, Theme.S(6)));
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            Label(g, L.T("files"), Theme.F(12.5f, FontStyle.Bold), Theme.Ink, filesTitleR);
            Rectangle cr = filesTitleR; cr.X += Theme.Measure(L.T("files"), Theme.F(12.5f, FontStyle.Bold)) + Theme.S(10); cr.Width = Theme.S(200);
            Label(g, CountText(), Theme.F(9f), Theme.InkFaint, cr);
            using (Pen lp = new Pen(Theme.Line)) { Rectangle l = M(new Rectangle(left.X + Theme.S(20), left.Y + Theme.S(60), left.Width - Theme.S(40), 1)); g.DrawLine(lp, l.X, l.Y, l.Right, l.Y); }
            Label(g, L.T("convert_to"), Theme.F(12.5f, FontStyle.Bold), Theme.Ink, convToR);
            Label(g, Sticker ? L.T("st_for") : IsMediaTab ? L.T("quality") : L.T("documents_engine"), Theme.F(10f, FontStyle.Bold), Theme.Ink, qualityR);
            if (Sticker && string.IsNullOrEmpty(statusText)) Label(g, L.T("st_note_" + StickerKeys[stickerFor.SelectedIndex]), Theme.F(9f), Theme.InkFaint, statusR, Align.Start, false, true);
            if (!IsMediaTab && !btnDocs.Visible) Label(g, L.T("docs_ok"), Theme.F(9f), Theme.Success, new Rectangle(qualityR.X, qualityR.Y + Theme.S(28), qualityR.Width, Theme.S(34)), Align.Start, true, true);
            Label(g, L.T("save_to"), Theme.F(10f, FontStyle.Bold), Theme.Ink, saveToR);
            Label(g, statusText, Theme.F(9.25f), statusColor, statusR, Align.Start, false, true);
            PaintSummary(g);
        }

        /// What will happen, at a glance: "3 files · 12.4 MB" over "HEIC → JPG".
        void PaintSummary(Graphics g)
        {
            if (summaryR.IsEmpty || list.Items.Count == 0 || chips.Selected == null) return;
            Rectangle r = summaryR;
            Theme.FillRound(g, M(r), Theme.S(12), Theme.CardInner);
            Theme.StrokeRound(g, M(r), Theme.S(12), Theme.Line);
            int pad = Theme.S(16);
            Label(g, CountText(), Theme.F(9f), Theme.InkSoft, new Rectangle(r.X + pad, r.Y + Theme.S(10), r.Width - pad * 2, Theme.S(22)));

            List<string> exts = new List<string>();
            foreach (FileItem it in list.Items) { string e = (it.Ext ?? "").ToUpperInvariant(); if (e.Length > 0 && !exts.Contains(e)) exts.Add(e); }
            string from = exts.Count == 0 ? "" : exts.Count == 1 ? exts[0] : exts.Count == 2 ? exts[0] + ", " + exts[1] : L.T("n_types", exts.Count);
            string to = Sticker ? L.T("st_chip") : chips.Selected.ToUpperInvariant();
            string arrow = Theme.RTL ? "  ←  " : "  →  ";
            Font big = Theme.Display(17f);
            // laid out left to right, then mirrored like the rest of the page, so Arabic reads source ← target
            int y = r.Y + Theme.S(34), h = Theme.S(38), x = r.X + pad, end = r.Right - pad;
            int wf = Math.Min(Theme.Measure(from, big) + 2, end - x);
            Theme.Text(g, from, big, Theme.Ink, M(new Rectangle(x, y, wf, h)), Align.Start, true, false, true);
            x += wf;
            int wa = Math.Min(Theme.Measure(arrow, big) + 2, Math.Max(0, end - x));
            Theme.Text(g, arrow, big, Theme.InkFaint, M(new Rectangle(x, y, wa, h)), Align.Start, true, false, true);
            x += wa;
            if (end - x > Theme.S(12)) Theme.Text(g, to, big, Theme.AccentColor, M(new Rectangle(x, y, end - x, h)), Theme.RTL ? Align.End : Align.Start, true, false, true);
        }

        bool IsMediaTab { get { return tabs.SelectedIndex <= 2; } }

        string CountText()
        {
            if (list.Items.Count == 0) return "";
            long total = 0; foreach (FileItem it in list.Items) total += it.Size;
            return (list.Items.Count == 1 ? L.T("one_file") : L.T("n_files", list.Items.Count)) + "  ·  " + Theme.HumanSize(total);
        }

        void SetStatus(string t, Color c) { statusText = t; statusColor = c; Invalidate(M(statusR)); }

        void UpdateState()
        {
            bool empty = list.Items.Count == 0;
            drop.Visible = empty; list.Visible = !empty;
            btnClear.Visible = !empty;
            DoLayout();
            btnClear.Enabled = !running; btnAdd.Enabled = !running;
            tabs.Enabled = chips.Enabled = quality.Enabled = stickerFor.Enabled = folder.Enabled = !running;
            quality.Visible = IsMediaTab && !Sticker;
            stickerFor.Visible = Sticker;
            btnDocs.Visible = !IsMediaTab && Tools.Soffice == null;
            btnConvert.Enabled = running || !empty;
            btnConvert.Kind = running ? BtnKind.Secondary : BtnKind.Primary;
            btnConvert.Glyph = running ? Ico.Stop : Ico.Sync;
            list.Locked = running;
            progress.Visible = running || progress.Value > 0;
            list.Invalidate(); btnConvert.Invalidate(); folder.Invalidate();
            Invalidate();
        }

        public override void Restyle() { UpdateState(); }

        // ------------------------------------------------------------------ files
        protected override void DragHighlight(bool on) { drop.DragHot = on; drop.Invalidate(); }

        public void Browse()
        {
            if (running) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Multiselect = true;
                d.Title = L.T("choose_files");
                d.Filter = L.T("all_files") + " (*.*)|*.*";
                if (LastDir.Show(d, FindForm(), "convert") == DialogResult.OK) AcceptFiles(d.FileNames);
            }
        }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || running) return;
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileItem it in list.Items) have.Add(it.Path);
            List<string> all = new List<string>();
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;
                try
                {
                    if (Directory.Exists(p)) all.AddRange(Directory.GetFiles(p, "*", SearchOption.AllDirectories));
                    else if (File.Exists(p)) all.Add(Path.GetFullPath(p));
                }
                catch { }
            }
            bool wasEmpty = list.Items.Count == 0;
            foreach (FileItem it in list.Items) { it.State = ItemState.Ready; it.Message = ""; }
            foreach (string f in all)
            {
                if (have.Contains(f)) continue;
                have.Add(f);
                FileItem it = new FileItem { Path = f, Name = Path.GetFileName(f), Ext = Formats.Ext(f) };
                try { it.Size = new FileInfo(f).Length; } catch { }
                list.Items.Add(it);
            }
            btnOpen.Visible = false; progress.Value = 0; SetStatus("", Theme.InkSoft);
            if (list.Items.Count > 0 && (wasEmpty || !userPicked) && !userPicked)
            {
                int tab; string target;
                Formats.Suggest(list.Items[0].Ext, out tab, out target);
                tabs.SetSilently(tab);
                chips.SetItems(Formats.Targets[tab], target);
            }
            DoLayout();
            UpdateState();
        }

        void OpenItem(FileItem it)
        {
            try
            {
                if (it.State == ItemState.Done && it.Output != null && File.Exists(it.Output)) Process.Start("explorer.exe", "/select,\"" + it.Output + "\"");
                else Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true });
            }
            catch { }
        }

        void OpenOutput()
        {
            try
            {
                if (doneCount == 1 && lastOutputFile != null && File.Exists(lastOutputFile)) Process.Start("explorer.exe", "/select,\"" + lastOutputFile + "\"");
                else if (lastOutputDir != null) Process.Start("explorer.exe", "\"" + lastOutputDir + "\"");
            }
            catch { }
        }

        // ------------------------------------------------------------------ converting
        void Start()
        {
            if (list.Items.Count == 0 || chips.Selected == null) return;
            string target = chips.Selected;
            if (target == "sticker") target = "sticker-" + StickerKeys[stickerFor.SelectedIndex];
            string outDir, single = null;
            if (list.Items.Count == 1 && SaveAsk.Enabled)
            {
                string sx = target.StartsWith("sticker-") ? Stickers.ExtFor(target.Substring(8), list.Items[0].Path) : target;
                single = SaveAsk.File(FindForm(), Path.GetFileNameWithoutExtension(list.Items[0].Path) + "." + sx, sx);
                if (single == null) return;
                outDir = SaveAsk.Scratch();
            }
            else
            {
                outDir = SaveAsk.Folder(FindForm());
                if (outDir == null) return;
            }
            try { Directory.CreateDirectory(outDir); }
            catch { SetStatus(L.T("err_folder"), Theme.Error); return; }
            Quality q = (Quality)quality.SelectedIndex;
            List<FileItem> items = new List<FileItem>(list.Items);
            foreach (FileItem it in items) { it.State = ItemState.Waiting; it.Message = ""; it.Progress = 0; it.Output = null; }
            running = true; cancel = false; doneCount = 0; lastOutputDir = single != null ? Path.GetDirectoryName(single) : outDir; lastOutputFile = null;
            btnOpen.Visible = false; progress.Value = 0;
            DoLayout(); UpdateState();
            SetStatus(L.T("starting"), Theme.InkSoft);
            act = Activity.Begin(this, target.StartsWith("sticker-") ? L.T("st_make") : L.T("act_k_convert", target.ToUpperInvariant()), items.Count == 1 ? items[0].Name : L.T("n_files", items.Count), () => cancel = true);
            Thread t = new Thread(() => Worker(items, target, outDir, q, single));
            t.IsBackground = true;
            t.Start();
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        ActJob act;

        void Worker(List<FileItem> items, string target, string outDir, Quality q, string single)
        {
            int ok = 0, fail = 0;
            for (int i = 0; i < items.Count; i++)
            {
                FileItem it = items[i];
                if (cancel) { it.State = ItemState.Skipped; continue; }
                int index = i;
                it.State = ItemState.Working;
                Ui(() =>
                {
                    list.RefreshItem(it);
                    int li = list.Items.IndexOf(it); if (li >= 0) list.EnsureVisible(li);
                    SetStatus(L.T("converting_n", index + 1, items.Count) + "  " + it.Name, Theme.InkSoft);
                    progress.Value = index / (double)items.Count;
                });
                string error;
                try
                {
                    if (!File.Exists(it.Path)) throw new UserError(L.T("err_gone"));
                    Job job = Engine.Build(it.Path, target, outDir, q);
                    if (single != null) SaveAsk.Redirect(job, single);
                    DateTime last = DateTime.MinValue;
                    error = Engine.Run(job, f =>
                    {
                        it.Progress = f;
                        if ((DateTime.Now - last).TotalMilliseconds < 100) return;
                        last = DateTime.Now;
                        Activity.Update(act, (index + f) / items.Count, items.Count > 1 ? (index + 1) + "/" + items.Count + "  ·  " + (int)(f * 100) + "%" : (int)(f * 100) + "%");
                        Ui(() => { list.RefreshItem(it); progress.Value = (index + f) / items.Count; });
                    }, () => cancel);
                    if (error == null) it.Output = job.Output;
                }
                catch (UserError ue) { error = ue.Message; }
                catch (Exception ex) { error = ex.Message; }

                if (error == null) { it.State = ItemState.Done; ok++; lastOutputFile = it.Output; }
                else if (cancel) it.State = ItemState.Skipped;
                else { it.State = ItemState.Failed; it.Message = error; fail++; }
                Ui(() => { list.RefreshItem(it); progress.Value = (index + 1) / (double)items.Count; });
            }
            Ui(() => Finished(ok, fail));
        }

        void Finished(int ok, int fail)
        {
            running = false;
            doneCount = ok;
            string where = Settings.OutputIsDefault ? L.T("downloads") : Path.GetFileName(lastOutputDir.TrimEnd('\\'));
            string files = ok == 1 ? L.T("one_file") : L.T("n_files", ok);
            Activity.End(act, ok == 0 ? L.T("none_converted") : null, cancel, fail == 0 ? L.T("all_done", files, where) : L.T("some_failed", ok, fail), ok == 1 ? lastOutputFile : lastOutputDir);
            if (cancel) SetStatus(L.T("cancelled") + "  " + L.T("n_converted", ok), Theme.InkSoft);
            else if (fail == 0) SetStatus(L.T("all_done", files, where), Theme.Success);
            else if (ok == 0) SetStatus(L.T("none_converted"), Theme.Error);
            else SetStatus(L.T("some_failed", ok, fail), Theme.Error);
            progress.Value = cancel ? 0 : 1;
            btnOpen.Visible = ok > 0;
            DoLayout(); UpdateState();
            if (ok > 0 && !cancel)
            {
                ShowToast(L.T("all_done", files, where), fail == 0, () => doneCount == 1 ? L.T("show_file") : L.T("open_folder"), OpenOutput);
                if (Settings.OpenAfter) OpenOutput();
            }
        }
    }

    /// Installs LibreOffice (free) for document conversions.
    public static class LibreOffice
    {
        static bool busy;
        public static void Enable(Control owner, Action<bool> done)
        {
            if (Tools.Soffice != null) { done(true); return; }
            if (busy) return;
            string winget = Tools.WinGet;
            bool yes = Ask.Confirm(owner.FindForm(), L.T("enable_docs"), L.T("lo_explain") + "\n\n" + (winget != null ? L.T("lo_install_q") : L.T("lo_page_q")),
                winget != null ? L.T("install") : L.T("open_page"), L.T("not_now"), AskKind.Info);
            if (!yes) return;
            if (winget == null) { OpenPage(); return; }
            busy = true;
            Thread t = new Thread(() =>
            {
                string output;
                try { Tools.RunHidden(winget, "install -e --id TheDocumentFoundation.LibreOffice --accept-package-agreements --accept-source-agreements --silent", 45 * 60 * 1000, out output); } catch { }
                Tools.Reset();
                busy = false;
                try
                {
                    owner.BeginInvoke((Action)(() =>
                    {
                        if (Tools.Soffice == null) OpenPage();
                        done(Tools.Soffice != null);
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }
        public static bool Busy { get { return busy; } }
        static void OpenPage() { try { Process.Start(new ProcessStartInfo("https://www.libreoffice.org/download/download-libreoffice/") { UseShellExecute = true }); } catch { } }
    }
}
