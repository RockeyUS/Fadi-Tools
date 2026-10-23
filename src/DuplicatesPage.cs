using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Finds files that are exactly the same (same bytes, whatever their names) in a folder, shows how much space
    /// the extra copies take, and moves the ones you pick to the Recycle Bin (so nothing is lost by mistake).
    public class DuplicatesPage : Page
    {
        class Dupe { public string Path; public long Size; public DateTime Changed; public bool Remove; }
        class Group { public List<Dupe> Files = new List<Dupe>(); public long Size; public long Wasted { get { return Size * (Files.Count - 1); } } }

        readonly List<string> folders = new List<string>();
        List<Group> groups = new List<Group>();
        readonly DupeList list;
        readonly DropZone drop;
        readonly Segments kind;
        readonly Toggle subfolders = new Toggle();
        readonly Btn bAdd, bClear, bFind, bRemove, bKeepOld, bKeepNew;
        readonly Progress progress = new Progress { Visible = false };
        Rectangle optCard, listCard, emptyCard, kindLbl, subLbl, foldersR, countR, statusR, keepLbl;
        string status; Theme.LiveColor statusColor;
        bool scanning, stop;

        public DuplicatesPage() : base("dup_title", "dup_sub")
        {
            drop = new DropZone(() => L.T("dup_drop"), () => L.T("dup_drop_sub")) { Glyph = Ico.Copy, Tips = new[] { "tip_dup_bin", "tip_dup_content" } };
            drop.Click += (s, e) => Browse();
            // one click: look in a common folder straight away
            drop.ExtrasTitle = () => L.T("dup_quick");
            drop.Extras = () =>
            {
                List<DropExtra> l = new List<DropExtra>();
                foreach (string[] f in new[] { new[] { Paths.Downloads, "downloads", Ico.Download }, new[] { Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "dup_pictures", Ico.Photo }, new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "dup_desktop", "\uE7F4" } })
                {
                    string dir = f[0];
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    l.Add(new DropExtra { Text = L.T(f[1]), Glyph = f[2], Run = () => { AcceptFiles(new[] { dir }); Scan(); } });
                }
                return l;
            };
            kind = new Segments(() => L.T("dup_k_all"), () => L.T("dup_k_media"), () => L.T("dup_k_music"), () => L.T("dup_k_docs")) { FontSize = 8.75f };
            int k0; int.TryParse(Settings.Get("DupKind", "0"), out k0); kind.SetSilently(Math.Max(0, Math.Min(3, k0)));
            kind.SelectedChanged += (s, e) => Settings.Set("DupKind", kind.SelectedIndex.ToString());
            subfolders.SetSilently(Settings.Get("DupSub", "1") == "1");
            subfolders.CheckedChanged += (s, e) => Settings.Set("DupSub", subfolders.Checked ? "1" : "0");
            bAdd = new Btn(() => L.T("dup_add"), Ico.FolderOpen, BtnKind.Secondary) { FontSize = 9f };
            bAdd.Click += (s, e) => Browse();
            bClear = new Btn(() => L.T("clear"), Ico.Delete, BtnKind.Ghost) { FontSize = 9f };
            bClear.Click += (s, e) => { if (scanning) return; Fx.SweepAway(list, list.VisibleRows(null), () => { folders.Clear(); groups.Clear(); status = null; Rebuild(); ShowState(); }); };
            bFind = new Btn(() => scanning ? L.T("cancel") : L.T("dup_find"), Ico.Search, BtnKind.Primary);
            bFind.Click += (s, e) => { if (scanning) stop = true; else Scan(); };
            bRemove = new Btn(() => L.T("dup_remove", RemoveCount, Theme.HumanSize(RemoveSize)), Ico.Delete, BtnKind.Danger);
            bRemove.Click += (s, e) => RemoveChosen();
            bKeepOld = new Btn(() => L.T("dup_keep_old"), Ico.Clock, BtnKind.Ghost) { FontSize = 9f, AlignStart = true };
            bKeepOld.Click += (s, e) => AutoPick(true);
            bKeepNew = new Btn(() => L.T("dup_keep_new"), Ico.Clock, BtnKind.Ghost) { FontSize = 9f, AlignStart = true };
            bKeepNew.Click += (s, e) => AutoPick(false);
            list = new DupeList(this);
            Controls.AddRange(new Control[] { drop, kind, subfolders, bAdd, bClear, bFind, bRemove, bKeepOld, bKeepNew, progress, list });
            EnableDrop(drop, list);
            ShowState();
        }

        public override bool Busy { get { return scanning; } }

        public void Browse()
        {
            string f = FolderPicker.Pick(FindForm(), L.T("dup_add"), folders.Count > 0 ? folders[0] : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            if (f != null) AcceptFiles(new[] { f });
        }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || scanning) return;
            foreach (string p in paths)
            {
                string d = Directory.Exists(p) ? p : File.Exists(p) ? Path.GetDirectoryName(p) : null;
                if (d == null) continue;
                d = Path.GetFullPath(d);
                if (!folders.Exists(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) folders.Add(d);
            }
            groups.Clear(); status = null; Rebuild();
            ShowState();
        }

        // ------------------------------------------------------------------ finding them
        bool Fits(string file)
        {
            Category c = Formats.CategoryOf(Formats.Ext(file));
            switch (kind.SelectedIndex)
            {
                case 1: return c == Category.Image || c == Category.Video;
                case 2: return c == Category.Audio;
                case 3: return c == Category.Document || c == Category.Data;
                default: return true;
            }
        }

        /// True while it is looking (the self-test waits for it).
        public bool Scanning { get { return scanning; } }

        public void Scan()
        {
            if (folders.Count == 0) { Browse(); if (folders.Count == 0) return; }
            scanning = true; stop = false; groups.Clear(); Rebuild();
            bool deep = subfolders.Checked;
            List<string> roots = new List<string>(folders);
            Func<string, bool> wanted = Fits;
            progress.Visible = true; progress.SetIndeterminate(true);
            SetStatus(L.T("dup_looking"), Theme.InkSoft);
            ShowState();
            ActJob job = Activity.Begin(this, L.T("act_k_dupes"), string.Join(", ", roots.ConvertAll(r => Path.GetFileName(r.TrimEnd('\\', '/')) ?? r).ToArray()), () => stop = true);
            Thread t = new Thread(() =>
            {
                string err = null; List<Group> found = null; int seen = 0;
                try { found = Find(roots, deep, wanted, n => { seen = n; }, (done, total) => Activity.Update(job, total > 0 ? done / (double)total : -1, L.T("dup_comparing", done, total)), job); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                bool stopped = stop;
                long wasted = 0; if (found != null) foreach (Group g in found) wasted += g.Wasted;
                Activity.End(job, err, stopped, found == null ? null : L.T("dup_found", found.Count, Theme.HumanSize(wasted)), null);
                Post(() =>
                {
                    scanning = false; progress.Visible = false; progress.SetIndeterminate(false);
                    if (found != null) { groups = found; AutoPick(true); }
                    if (stopped) SetStatus(L.T("cancelled"), Theme.InkSoft);
                    else if (err != null) SetStatus(err, Theme.Error);
                    else if (groups.Count == 0) SetStatus(L.T("dup_none", seen), Theme.Success);
                    else SetStatus(L.T("dup_found", groups.Count, Theme.HumanSize(wasted)), Theme.Ink);
                    Rebuild(); ShowState();
                });
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        /// Same size → same first and last 64 KB → same whole contents (SHA-1). Only files that could match are read.
        List<Group> Find(List<string> roots, bool deep, Func<string, bool> wanted, Action<int> seenCount, Action<int, int> comparing, ActJob job)
        {
            Dictionary<long, List<string>> bySize = new Dictionary<long, List<string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            Stack<string> dirs = new Stack<string>(roots);
            while (dirs.Count > 0)
            {
                if (stop) throw new OperationCanceledException();
                string d = dirs.Pop();
                string[] files = new string[0], subs = new string[0];
                try { files = Directory.GetFiles(d); } catch { }
                if (deep) try { subs = Directory.GetDirectories(d); } catch { }
                foreach (string s in subs)
                {
                    try { if ((File.GetAttributes(s) & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue; } catch { continue; }
                    dirs.Push(s);
                }
                foreach (string f in files)
                {
                    if (!seen.Add(f) || !wanted(f)) continue;
                    long len;
                    try { FileInfo fi = new FileInfo(f); if ((fi.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0) continue; len = fi.Length; } catch { continue; }
                    if (len <= 0) continue;
                    List<string> l; if (!bySize.TryGetValue(len, out l)) { l = new List<string>(); bySize[len] = l; }
                    l.Add(f);
                    count++;
                    if (count % 200 == 0) { seenCount(count); Activity.Update(job, -1, L.T("dup_looked", count)); }
                }
            }
            seenCount(count);
            List<List<string>> candidates = new List<List<string>>();
            int total = 0;
            foreach (List<string> l in bySize.Values) if (l.Count > 1) { candidates.Add(l); total += l.Count; }
            List<Group> result = new List<Group>();
            int done = 0;
            foreach (List<string> same in candidates)
            {
                long size = new FileInfo(same[0]).Length;
                // quick look first (start and end), then the whole file only for those that still match
                Dictionary<string, List<string>> quick = new Dictionary<string, List<string>>();
                foreach (string f in same)
                {
                    if (stop) throw new OperationCanceledException();
                    string h = Hash(f, size > 256 * 1024 ? 65536 : -1);
                    done++; comparing(done, total);
                    if (h == null) continue;
                    List<string> q; if (!quick.TryGetValue(h, out q)) { q = new List<string>(); quick[h] = q; }
                    q.Add(f);
                }
                foreach (List<string> q in quick.Values)
                {
                    if (q.Count < 2) continue;
                    Dictionary<string, List<string>> full = new Dictionary<string, List<string>>();
                    foreach (string f in q)
                    {
                        if (stop) throw new OperationCanceledException();
                        string h = size > 256 * 1024 ? Hash(f, -1) : "same";   // small files were read whole already
                        if (h == null) continue;
                        List<string> x; if (!full.TryGetValue(h, out x)) { x = new List<string>(); full[h] = x; }
                        x.Add(f);
                    }
                    foreach (List<string> x in full.Values)
                    {
                        if (x.Count < 2) continue;
                        Group g = new Group { Size = size };
                        foreach (string f in x) { DateTime w = DateTime.MinValue; try { w = File.GetLastWriteTime(f); } catch { } g.Files.Add(new Dupe { Path = f, Size = size, Changed = w }); }
                        result.Add(g);
                    }
                }
            }
            result.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
            return result;
        }

        /// SHA-1 of the whole file (part = -1), or of its first and last `part` bytes.
        static string Hash(string f, int part)
        {
            try
            {
                using (SHA1 sha = SHA1.Create())
                using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                {
                    if (part < 0) return Convert.ToBase64String(sha.ComputeHash(fs));
                    byte[] buf = new byte[part * 2];
                    int n = fs.Read(buf, 0, part);
                    if (fs.Length > part * 2) fs.Seek(-part, SeekOrigin.End);
                    int m = fs.Read(buf, n, part);
                    return Convert.ToBase64String(sha.ComputeHash(buf, 0, n + m));
                }
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ choosing what goes
        /// In every group, one copy stays (the oldest, or the newest) and the others are marked to go.
        void AutoPick(bool keepOldest)
        {
            foreach (Group g in groups)
            {
                Dupe keep = null;
                foreach (Dupe d in g.Files)
                    if (keep == null || (keepOldest ? d.Changed < keep.Changed : d.Changed > keep.Changed) || (d.Changed == keep.Changed && d.Path.Length < keep.Path.Length)) keep = d;
                foreach (Dupe d in g.Files) d.Remove = d != keep;
            }
            Rebuild(); ShowState();
        }

        int RemoveCount { get { int n = 0; foreach (Group g in groups) foreach (Dupe d in g.Files) if (d.Remove) n++; return n; } }
        long RemoveSize { get { long n = 0; foreach (Group g in groups) foreach (Dupe d in g.Files) if (d.Remove) n += d.Size; return n; } }

        void Toggle(Dupe d, Group g)
        {
            if (!d.Remove)
            {
                // at least one copy always stays
                int keep = 0; foreach (Dupe x in g.Files) if (!x.Remove) keep++;
                if (keep <= 1) { ShowToast(L.T("dup_keep_one"), false, null, null); return; }
            }
            d.Remove = !d.Remove;
            list.Invalidate(); bRemove.Invalidate(); ShowState();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        void RemoveChosen()
        {
            List<string> go = new List<string>();
            foreach (Group g in groups) foreach (Dupe d in g.Files) if (d.Remove) go.Add(d.Path);
            if (go.Count == 0) return;
            if (!Ask.Confirm(FindForm(), L.T("dup_confirm_title"), L.T("dup_confirm", go.Count, Theme.HumanSize(RemoveSize)), L.T("dup_confirm_yes"), L.T("cancel"), AskKind.Warning)) return;
            int moved = 0;
            try
            {
                // to the Recycle Bin, all at once (Windows can bring them back from there)
                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT { hwnd = FindForm().Handle, wFunc = 3, pFrom = string.Join("\0", go.ToArray()) + "\0\0", fFlags = 0x40 | 0x10 | 0x4 | 0x400 };
                SHFileOperation(ref op);
            }
            catch (Exception ex) { SetStatus(ex.Message, Theme.Error); }
            foreach (Group g in groups) g.Files.RemoveAll(d => { bool gone = d.Remove && !File.Exists(d.Path); if (gone) moved++; return gone; });
            groups.RemoveAll(g => g.Files.Count < 2);
            SetStatus(L.T("dup_moved", moved), moved > 0 ? Theme.Success : Theme.Error);
            Rebuild(); ShowState();
        }

        // ------------------------------------------------------------------ the list: a heading for each group, then its copies
        class Row { public Group G; public Dupe D; }
        readonly List<Row> rows = new List<Row>();
        void Rebuild()
        {
            rows.Clear();
            foreach (Group g in groups) { rows.Add(new Row { G = g }); foreach (Dupe d in g.Files) rows.Add(new Row { G = g, D = d }); }
            list.ClampScroll(); list.Invalidate();
        }

        void SetStatus(string s, Color c) { status = s; statusColor = c; Invalidate(); }

        void ShowState()
        {
            bool has = folders.Count > 0;
            drop.Visible = !has;
            foreach (Control c in new Control[] { kind, subfolders, bAdd, bClear, bFind, list }) c.Visible = has;
            bRemove.Visible = has && RemoveCount > 0 && !scanning;
            bKeepOld.Visible = bKeepNew.Visible = has && groups.Count > 0 && !scanning;
            bAdd.Enabled = bClear.Enabled = kind.Enabled = subfolders.Enabled = !scanning;
            bFind.Kind = scanning ? BtnKind.Secondary : BtnKind.Primary; bFind.Glyph = scanning ? Ico.Stop : Ico.Search;
            bFind.Invalidate(); bRemove.Invalidate();
            DoLayout();
        }

        public override void DoLayout()
        {
            if (list == null) return;
            int W = Width, H = Height, p = Pad, ip = Theme.S(20), gap = Theme.S(16);
            cards.Clear();
            if (folders.Count == 0)
            {
                emptyCard = new Rectangle(p, Top0, W - p * 2, H - Top0 - p);
                cards.Add(emptyCard);
                Place(drop, new Rectangle(emptyCard.X + ip, emptyCard.Y + ip, emptyCard.Width - ip * 2, emptyCard.Height - ip * 2));
                Invalidate(); return;
            }
            int ow = Math.Max(Theme.S(300), Math.Min(Theme.S(380), (W - p * 2) * 2 / 5));
            optCard = new Rectangle(p, Top0, ow, H - Top0 - p);
            listCard = new Rectangle(p + ow + gap, Top0, W - p * 2 - ow - gap, H - Top0 - p);
            cards.Add(optCard); cards.Add(listCard);
            int x = optCard.X + ip, w = optCard.Width - ip * 2, y = optCard.Y + Theme.S(18);
            foldersR = new Rectangle(x, y, w, Theme.S(64)); y += Theme.S(70);
            int aw = Math.Max(Theme.S(130), bAdd.PreferredWidth), cw = Math.Max(Theme.S(80), bClear.PreferredWidth);
            Place(bAdd, new Rectangle(x, y, aw, Theme.S(34)));
            Place(bClear, new Rectangle(x + aw + Theme.S(8), y, cw, Theme.S(34))); y += Theme.S(50);
            kindLbl = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
            Place(kind, new Rectangle(x, y, w, Theme.S(36))); y += Theme.S(48);
            subLbl = new Rectangle(x, y, w - Theme.S(60), Theme.S(34));
            Place(subfolders, new Rectangle(x + w - Theme.S(52), y, Theme.S(52), Theme.S(34))); y += Theme.S(50);
            keepLbl = new Rectangle(x, y, w, Theme.S(20));
            Place(bKeepOld, new Rectangle(x - Theme.S(8), y + Theme.S(22), w + Theme.S(8), Theme.S(34)));
            Place(bKeepNew, new Rectangle(x - Theme.S(8), y + Theme.S(58), w + Theme.S(8), Theme.S(34)));
            int by = optCard.Bottom - ip - Theme.S(42);
            Place(bFind, new Rectangle(x, by, w, Theme.S(42)));
            Place(progress, new Rectangle(x, by - Theme.S(12), w, Theme.S(4)));
            statusR = new Rectangle(x, by - Theme.S(70), w, Theme.S(52));

            int lx = listCard.X + ip, lw = listCard.Width - ip * 2;
            int rw = bRemove.Wanted ? Math.Max(Theme.S(200), bRemove.PreferredWidth + Theme.S(12)) : 0;
            countR = new Rectangle(lx, listCard.Y + Theme.S(18), lw - rw - Theme.S(10), Theme.S(36));
            Place(bRemove, new Rectangle(lx + lw - rw, listCard.Y + Theme.S(16), rw, Theme.S(38)));
            Place(list, new Rectangle(listCard.X + Theme.S(6), listCard.Y + Theme.S(66), listCard.Width - Theme.S(12), listCard.Height - Theme.S(72)));
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            if (folders.Count == 0) return;
            Font lf = Theme.F(9f, FontStyle.Bold);
            string fl = folders.Count == 1 ? folders[0] : L.T("dup_folders_n", folders.Count) + ": " + string.Join(", ", folders.ConvertAll(f => Path.GetFileName(f.TrimEnd('\\', '/')) ?? f).ToArray());
            Label(g, L.T("dup_in"), lf, Theme.InkSoft, new Rectangle(foldersR.X, foldersR.Y, foldersR.Width, Theme.S(20)));
            Label(g, fl, Theme.F(9f), Theme.Ink, new Rectangle(foldersR.X, foldersR.Y + Theme.S(22), foldersR.Width, Theme.S(40)), Align.Start, false, true);
            Label(g, L.T("dup_kind"), lf, Theme.InkSoft, kindLbl);
            Label(g, L.T("dup_sub_folders"), Theme.F(9.5f), Theme.Ink, subLbl);
            if (bKeepOld.Visible) Label(g, L.T("dup_pick"), lf, Theme.InkSoft, keepLbl);
            long wasted = 0; foreach (Group gr in groups) wasted += gr.Wasted;
            string head = groups.Count == 0 ? (scanning ? L.T("dup_looking") : L.T("dup_ready")) : bRemove.Wanted ? L.T("dup_groups_n", groups.Count) : L.T("dup_count", groups.Count, Theme.HumanSize(wasted));
            Label(g, head, Theme.F(10.5f, FontStyle.Bold), Theme.Ink, countR);
            string st = status; Color sc = statusColor;
            if (st == null) { st = L.T("dup_hint"); sc = Theme.InkFaint; }
            Label(g, st, Theme.F(9f), sc, statusR, Align.Start, true, true);
        }

        protected override void DragHighlight(bool on) { drop.DragHot = on; drop.Invalidate(); }

        class DupeList : ScrollList
        {
            readonly DuplicatesPage page;
            public DupeList(DuplicatesPage p) { page = p; RowHeight = 52; Cursor = Cursors.Hand; }
            protected override string RowText(int i) { Row r = page.rows[i]; if (r.D == null) return System.IO.Path.GetFileName(r.G.Files[0].Path) + ": " + L.T("dup_group", r.G.Files.Count, Theme.HumanSize(r.G.Size), Theme.HumanSize(r.G.Wasted)); return System.IO.Path.GetFileName(r.D.Path) + ", " + (r.D.Remove ? L.T("dup_goes") : L.T("dup_keeps")); }
            public override int Count { get { return page.rows.Count; } }

            protected override void PaintEmpty(Graphics g)
            {
                if (page.scanning || page.groups.Count > 0) return;
                Rectangle c = new Rectangle(Width / 2 - Theme.S(26), Height / 2 - Theme.S(46), Theme.S(52), Theme.S(52));
                Theme.FillRound(g, c, Theme.S(26), Theme.Selected);
                Theme.Glyph(g, Ico.Copy, 16f, Theme.InkFaint, c);
                Theme.Text(g, L.T("dup_empty"), Theme.F(9.5f), Theme.InkFaint, new Rectangle(Theme.S(30), c.Bottom + Theme.S(10), Width - Theme.S(60), Theme.S(44)), Align.Center, false, true);
            }

            protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
            {
                if (i >= page.rows.Count) return;
                Row row = page.rows[i];
                Theme.Smooth(g);
                Func<Rectangle, Rectangle> M = x => Theme.Mirror(x, Width);
                if (row.D == null)
                {
                    // the group's heading: how many copies, how big, how much space the extra ones take
                    string t = L.T("dup_group", row.G.Files.Count, Theme.HumanSize(row.G.Size), Theme.HumanSize(row.G.Wasted));
                    Theme.Text(g, t, Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, M(new Rectangle(r.X + Theme.S(14), r.Y + Theme.S(18), r.Width - Theme.S(28), r.Height - Theme.S(18))), Align.Start);
                    return;
                }
                Dupe d = row.D;
                if (hover) Theme.FillRound(g, new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(2), r.Width - Theme.S(12), r.Height - Theme.S(4)), Theme.S(8), Theme.Hover);
                Rectangle ic = M(new Rectangle(r.X + Theme.S(14), r.Y + Theme.S(8), Theme.S(36), Theme.S(36)));
                if (!Thumbs.Draw(g, d.Path, ic, Theme.S(7), Invalidate))
                {
                    Theme.FillRound(g, ic, Theme.S(7), Theme.Selected);
                    Theme.Glyph(g, Formats.GlyphFor(Formats.CategoryOf(Formats.Ext(d.Path))), 10f, Theme.InkSoft, ic);
                }
                int tx = Theme.S(62), tagW = Theme.S(120), tw = r.Width - tx - tagW - Theme.S(16);
                Color nc = d.Remove ? Theme.InkFaint : Theme.Ink;
                Theme.Text(g, Path.GetFileName(d.Path), Theme.F(9.75f, d.Remove ? FontStyle.Regular : FontStyle.Bold), nc, M(new Rectangle(r.X + tx, r.Y + Theme.S(6), tw, Theme.S(22))), Theme.RTL ? Align.End : Align.Start, true, false, true);
                string where = (Path.GetDirectoryName(d.Path) ?? "") + "  ·  " + d.Changed.ToString("d", CultureInfo.CurrentCulture);
                Theme.Text(g, where, Theme.F(8.25f), Theme.InkFaint, M(new Rectangle(r.X + tx, r.Y + Theme.S(27), tw, Theme.S(18))), Theme.RTL ? Align.End : Align.Start, true, false, true);
                // keep / goes to the Recycle Bin: a pill you click to switch
                Rectangle tag = M(new Rectangle(r.Right - tagW - Theme.S(10), r.Y + (r.Height - Theme.S(26)) / 2, tagW, Theme.S(26)));
                Theme.FillRound(g, tag, Theme.S(13), d.Remove ? Theme.ErrorBg : Theme.SuccessBg);
                Theme.Text(g, L.T(d.Remove ? "dup_goes" : "dup_keeps"), Theme.F(8.25f, FontStyle.Bold), d.Remove ? Theme.Error : Theme.Success, tag, Align.Center);
                using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, r.X + Theme.S(14), r.Bottom - 1, r.Right - Theme.S(14), r.Bottom - 1);
            }

            protected override void RowClick(int index, Point local, Rectangle row)
            {
                if (index < 0 || index >= page.rows.Count) return;
                Row r = page.rows[index];
                if (r.D != null) page.Toggle(r.D, r.G);
            }

            protected override void RowDoubleClick(int index)
            {
                if (index < 0 || index >= page.rows.Count || page.rows[index].D == null) return;
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + page.rows[index].D.Path + "\""); } catch { }
            }

            protected override string[] DragFiles(int i) { return i < page.rows.Count && page.rows[i].D != null ? new[] { page.rows[i].D.Path } : null; }
        }
    }
}
