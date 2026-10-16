using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FileConverter
{
    /// Gives many files new names at once: numbered, find & replace, text added, or capital letters changed.
    /// Shows every new name before anything happens, and can undo the last rename.
    public class RenamePage : Page
    {
        readonly List<string> files = new List<string>();
        readonly List<string> newNames = new List<string>();
        readonly List<int> problems = new List<int>();   // per file: 0 fine, 1 unchanged, 2 name taken, 3 bad characters
        List<KeyValuePair<string, string>> undo;          // new path → old path, from the last rename

        DropZone drop;
        Segments mode, order, letters, infoKind, infoStyle;
        bool reading;   // dates / song tags are being read
        TextField fName, fStart, fFind, fReplace, fBefore, fAfter;
        Btn bAdd, bClear, bRename, bUndo;
        RenameList list;
        Rectangle optCard, listCard, modeLbl, l1, l2, l3, countR, statusR, emptyCard;
        string status; Theme.LiveColor statusColor;

        public RenamePage() : base("ren_title", "ren_sub")
        {
            drop = new DropZone(() => L.T("ren_drop"), () => L.T("ren_drop_sub")) { Glyph = "", Tips = new[] { "tip_ren_folder", "tip_ren_undo", "tip_palette" } };
            drop.Click += (s, e) => Browse();
            mode = new Segments(() => L.T("ren_m_number"), () => L.T("ren_m_replace"), () => L.T("ren_m_add"), () => L.T("ren_m_case"), () => L.T("ren_m_info")) { FontSize = 8.75f };
            int m0; int.TryParse(Settings.Get("RenameMode", "0"), out m0);
            mode.SetSilently(Math.Max(0, Math.Min(4, m0)));
            // date taken / song info: what to use, and how the name looks
            infoKind = new Segments(() => L.T("ren_k_date"), () => L.T("ren_k_song")) { FontSize = 9f };
            infoKind.Glyphs = new[] { Ico.Clock, Ico.Music };
            infoStyle = new Segments(() => L.T(infoKind.SelectedIndex == 0 ? "ren_s_date" : "ren_s_artist"), () => L.T(infoKind.SelectedIndex == 0 ? "ren_s_datetime" : "ren_s_track"), () => L.T(infoKind.SelectedIndex == 0 ? "ren_s_datename" : "ren_s_title")) { FontSize = 8.75f };
            int k0, s0; int.TryParse(Settings.Get("RenameInfo", "0"), out k0); int.TryParse(Settings.Get("RenameInfoStyle", "0"), out s0);
            infoKind.SetSilently(Math.Max(0, Math.Min(1, k0))); infoStyle.SetSilently(Math.Max(0, Math.Min(2, s0)));
            infoKind.SelectedChanged += (s, e) => { Settings.Set("RenameInfo", infoKind.SelectedIndex.ToString()); infoStyle.Invalidate(); status = null; Preview(); };
            infoStyle.SelectedChanged += (s, e) => { Settings.Set("RenameInfoStyle", infoStyle.SelectedIndex.ToString()); status = null; Preview(); };
            mode.FadeTarget = this;
            mode.FadeArea = () => new Rectangle(optCard.X, mode.Bottom + Theme.S(4), optCard.Width, Math.Max(0, optCard.Bottom - mode.Bottom - Theme.S(80)));
            mode.SelectedChanged += (s, e) => { Settings.Set("RenameMode", mode.SelectedIndex.ToString()); status = null; ShowFields(); ReadFacts(); Preview(); };
            order = new Segments(() => L.T("ren_by_name"), () => L.T("ren_by_date")) { FontSize = 8.75f };
            order.SelectedChanged += (s, e) => { Sort(); Preview(); };
            letters = new Segments(() => "abc", () => "ABC", () => "Abc") { FontSize = 9f };
            letters.SelectedChanged += (s, e) => Preview();
            fName = new TextField(() => L.T("ren_name_ph"), null);
            fStart = new TextField(() => "1", null) { ForceLtr = true };
            fStart.Box.Text = "1";
            fFind = new TextField(() => L.T("ren_find_ph"), Ico.Search);
            fReplace = new TextField(() => L.T("ren_replace_ph"), null);
            fBefore = new TextField(() => L.T("ren_before_ph"), null);
            fAfter = new TextField(() => L.T("ren_after_ph"), null);
            foreach (TextField f in new[] { fName, fStart, fFind, fReplace, fBefore, fAfter }) f.Box.TextChanged += (s, e) => { status = null; Preview(); };
            bAdd = new Btn(() => L.T("ren_add"), Ico.Add, BtnKind.Secondary) { FontSize = 9f };
            bAdd.Click += (s, e) => Browse();
            bClear = new Btn(() => L.T("clear"), Ico.Delete, BtnKind.Ghost) { FontSize = 9f };
            bClear.Click += (s, e) => Fx.SweepAway(list, list.VisibleRows(null), () => { files.Clear(); status = null; ShowState(); });
            bRename = new Btn(() => L.T("ren_go", ChangeCount), "", BtnKind.Primary);
            bRename.Click += (s, e) => DoRename();
            bUndo = new Btn(() => L.T("ren_undo"), "", BtnKind.Ghost) { FontSize = 9f };
            bUndo.Click += (s, e) => Undo();
            list = new RenameList(this);
            Controls.AddRange(new Control[] { drop, mode, order, letters, infoKind, infoStyle, fName, fStart, fFind, fReplace, fBefore, fAfter, bAdd, bClear, bRename, bUndo, list });
            EnableDrop(drop, list);
            ShowState();
        }

        public override void Restyle() { foreach (TextField f in new[] { fName, fStart, fFind, fReplace, fBefore, fAfter }) f.Restyle(); }

        public void Browse()
        {
            using (OpenFileDialog d = new OpenFileDialog { Multiselect = true, Title = L.T("ren_drop"), Filter = L.T("all_files") + "|*.*" })
                if (LastDir.Show(d, FindForm(), "rename") == DialogResult.OK) AcceptFiles(d.FileNames);
        }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null) return;
            foreach (string p in paths)
            {
                try
                {
                    if (Directory.Exists(p)) { foreach (string f in Directory.GetFiles(p)) Add(f); continue; }
                    if (File.Exists(p)) Add(p);
                }
                catch { }
            }
            if (fName.Box.Text.Length == 0 && files.Count > 0)
            {
                // a sensible starting name: the folder's name ("Holiday 2026" → Holiday 2026 1, 2, 3...)
                try { fName.Box.Text = new DirectoryInfo(Path.GetDirectoryName(files[0])).Name; } catch { }
            }
            Sort(); status = null; undo = null;
            ShowState();
            ReadFacts();
        }

        /// "Date & tags": reads every file's date taken / song tags in the background, then shows the new names.
        void ReadFacts()
        {
            if (mode.SelectedIndex != 4 || reading) return;
            List<string> todo = new List<string>();
            foreach (string f in files) if (FileFacts.Cached(f) == null) todo.Add(f);
            if (todo.Count == 0) return;
            reading = true; Preview();
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                foreach (string f in todo) { try { FileFacts.Read(f); } catch { } }
                Post(() => { reading = false; Preview(); ReadFacts(); });
            }) { IsBackground = true };
            t.Start();
        }

        static string Clean(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), c == ':' || c == '/' || c == '\\' ? "-" : "");
            return s.Trim().TrimEnd('.');
        }

        /// Date & tags: for files sharing a date, their place among them (1, 2, 3...), 0 when the date is unique.
        Dictionary<string, int> dateRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int dateDigits = 1;
        void RankDates()
        {
            dateRank.Clear();
            Dictionary<string, List<KeyValuePair<DateTime, string>>> byKey = new Dictionary<string, List<KeyValuePair<DateTime, string>>>();
            bool withTime = infoStyle.SelectedIndex == 1;
            foreach (string f in files)
            {
                Facts ft = FileFacts.Cached(f); if (ft == null) continue;
                string key = ft.Taken.ToString(withTime ? "yyyy-MM-dd HH.mm.ss" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                List<KeyValuePair<DateTime, string>> l;
                if (!byKey.TryGetValue(key, out l)) { l = new List<KeyValuePair<DateTime, string>>(); byKey[key] = l; }
                l.Add(new KeyValuePair<DateTime, string>(ft.Taken, f));
            }
            dateDigits = 1;
            foreach (List<KeyValuePair<DateTime, string>> l in byKey.Values)
            {
                if (l.Count < 2) continue;
                l.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : NaturalCompare(Path.GetFileName(a.Value), Path.GetFileName(b.Value)));
                for (int i = 0; i < l.Count; i++) dateRank[l[i].Value] = i + 1;
                dateDigits = Math.Max(dateDigits, l.Count.ToString(CultureInfo.InvariantCulture).Length);
            }
        }

        void Add(string f)
        {
            string full = Path.GetFullPath(f);
            foreach (string x in files) if (string.Equals(x, full, StringComparison.OrdinalIgnoreCase)) return;
            files.Add(full);
        }

        void Sort()
        {
            if (order.SelectedIndex == 1)
                files.Sort((a, b) => { try { return File.GetLastWriteTime(a).CompareTo(File.GetLastWriteTime(b)); } catch { return 0; } });
            else files.Sort((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
        }

        /// "photo 2" before "photo 10", like Windows Explorer.
        static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);
        }

        // ------------------------------------------------------------------ the new names
        string NewName(int i)
        {
            string path = files[i];
            string name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            switch (mode.SelectedIndex)
            {
                case 0:
                    {
                        int start; if (!int.TryParse(fStart.Box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out start)) start = 1;
                        int last = start + files.Count - 1;
                        int digits = Math.Max(Math.Max(1, fStart.Box.Text.Trim().Length), last.ToString(CultureInfo.InvariantCulture).Length);
                        string num = (start + i).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
                        string baseName = fName.Box.Text.Trim();
                        name = baseName.Length > 0 ? baseName + " " + num : num;
                        break;
                    }
                case 1:
                    {
                        string find = fFind.Box.Text;
                        if (find.Length > 0) name = ReplaceAll(name, find, fReplace.Box.Text);
                        break;
                    }
                case 2:
                    name = fBefore.Box.Text + name + fAfter.Box.Text;
                    break;
                case 3:
                    if (letters.SelectedIndex == 0) name = name.ToLower(CultureInfo.CurrentCulture);
                    else if (letters.SelectedIndex == 1) name = name.ToUpper(CultureInfo.CurrentCulture);
                    else name = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower(CultureInfo.CurrentCulture));
                    break;
                case 4:
                    {
                        Facts f = FileFacts.Cached(path);
                        if (f == null) break;   // still reading
                        int st = infoStyle.SelectedIndex;
                        if (infoKind.SelectedIndex == 0)
                        {
                            // 2026-03-14 · 2026-03-14 18.42.05 · 2026-03-14 IMG_2231 (files sharing a date get 1, 2, 3...)
                            string d = f.Taken.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                            if (st == 1) d += " " + f.Taken.ToString("HH.mm.ss", CultureInfo.InvariantCulture);
                            int rank;
                            if (st == 2) name = d + " " + name;
                            else name = dateRank.TryGetValue(path, out rank) ? d + " " + rank.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(2, dateDigits), '0') : d;
                        }
                        else
                        {
                            // Artist - Title · 03. Title · Title (files without tags keep their name)
                            if (f.Title.Length == 0) break;
                            if (st == 0) name = f.Artist.Length > 0 ? f.Artist + " - " + f.Title : f.Title;
                            else if (st == 1) name = f.Track > 0 ? f.Track.ToString("00", CultureInfo.InvariantCulture) + ". " + f.Title : f.Title;
                            else name = f.Title;
                        }
                        name = Clean(name);
                        break;
                    }
            }
            return name.Trim() + ext;
        }

        static string ReplaceAll(string s, string find, string with)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (true)
            {
                int k = s.IndexOf(find, i, StringComparison.OrdinalIgnoreCase);
                if (k < 0) { sb.Append(s.Substring(i)); break; }
                sb.Append(s, i, k - i).Append(with);
                i = k + find.Length;
            }
            return sb.ToString();
        }

        void Preview()
        {
            newNames.Clear(); problems.Clear();
            if (mode.SelectedIndex == 4 && infoKind.SelectedIndex == 0) RankDates();
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> mine = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < files.Count; i++)
            {
                string n = NewName(i);
                newNames.Add(n);
                string dir = Path.GetDirectoryName(files[i]);
                string target = Path.Combine(dir, n);
                int pr = 0;
                if (Path.GetFileNameWithoutExtension(n).Length == 0 || n.IndexOfAny(bad) >= 0) pr = 3;
                else if (string.Equals(n, Path.GetFileName(files[i]), StringComparison.Ordinal)) pr = 1;
                else if (!taken.Add(target)) pr = 2;
                else
                {
                    // another file already has that name (one that isn't being renamed too)
                    bool exists = false;
                    try { exists = (File.Exists(target) || Directory.Exists(target)) && !mine.Contains(target); } catch { }
                    if (exists && !string.Equals(target, files[i], StringComparison.OrdinalIgnoreCase)) pr = 2;
                }
                problems.Add(pr);
            }
            bRename.Enabled = ChangeCount > 0 && !HasProblem;
            bRename.Invalidate();
            list.Invalidate();
            Invalidate(countR); Invalidate(statusR);
        }

        int ChangeCount { get { int n = 0; foreach (int p in problems) if (p == 0) n++; return n; } }
        bool HasProblem { get { foreach (int p in problems) if (p >= 2) return true; return false; } }

        void DoRename()
        {
            Preview();
            if (ChangeCount == 0 || HasProblem) return;
            List<KeyValuePair<string, string>> done = new List<KeyValuePair<string, string>>();
            string err = Move(MovesFor(), done);
            undo = done.Count > 0 ? done : null;
            // the list keeps the files under their new names
            Dictionary<string, string> now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in done) now[kv.Value] = kv.Key;
            for (int i = 0; i < files.Count; i++) { string n; if (now.TryGetValue(files[i], out n)) files[i] = n; }
            if (err == null) { status = L.T("ren_done", done.Count); statusColor = Theme.Success; }
            else { status = L.T("ren_failed", err); statusColor = Theme.Error; }
            if (mode.SelectedIndex != 0) { fFind.Box.Text = ""; fBefore.Box.Text = ""; fAfter.Box.Text = ""; }
            ShowState();
        }

        /// old path → new path for every file that changes.
        List<KeyValuePair<string, string>> MovesFor()
        {
            List<KeyValuePair<string, string>> m = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < files.Count; i++)
                if (problems[i] == 0) m.Add(new KeyValuePair<string, string>(files[i], Path.Combine(Path.GetDirectoryName(files[i]), newNames[i])));
            return m;
        }

        /// Renames in two steps (first to a temporary name) so files can swap names safely.
        /// done gets new path → old path for each file renamed. Returns an error, or null.
        static string Move(List<KeyValuePair<string, string>> moves, List<KeyValuePair<string, string>> done)
        {
            List<KeyValuePair<string, string>> temps = new List<KeyValuePair<string, string>>();
            string err = null;
            foreach (KeyValuePair<string, string> mv in moves)
            {
                string tmp = Path.Combine(Path.GetDirectoryName(mv.Key), "~fadi_" + Guid.NewGuid().ToString("N").Substring(0, 10) + Path.GetExtension(mv.Key));
                try { File.Move(mv.Key, tmp); temps.Add(new KeyValuePair<string, string>(tmp, mv.Key)); }
                catch (Exception ex) { err = Path.GetFileName(mv.Key) + ": " + ex.Message; break; }
            }
            Dictionary<string, string> want = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> mv in moves) want[mv.Key] = mv.Value;
            foreach (KeyValuePair<string, string> t in temps)
            {
                string target = err == null ? want[t.Value] : t.Value;   // something failed: everything goes back
                try { File.Move(t.Key, target); if (err == null) done.Add(new KeyValuePair<string, string>(target, t.Value)); }
                catch (Exception ex)
                {
                    if (err == null) err = Path.GetFileName(t.Value) + ": " + ex.Message;
                    try { File.Move(t.Key, t.Value); } catch { }
                }
            }
            return err;
        }

        void Undo()
        {
            if (undo == null) return;
            List<KeyValuePair<string, string>> back = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> kv in undo) back.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
            List<KeyValuePair<string, string>> done = new List<KeyValuePair<string, string>>();
            string err = Move(back, done);
            Dictionary<string, string> now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in done) now[kv.Value] = kv.Key;
            for (int i = 0; i < files.Count; i++) { string n; if (now.TryGetValue(files[i], out n)) files[i] = n; }
            undo = null;
            if (err == null) { status = L.T("ren_undone", done.Count); statusColor = Theme.InkSoft; }
            else { status = L.T("ren_failed", err); statusColor = Theme.Error; }
            Sort(); ShowState();
        }

        // ------------------------------------------------------------------ layout
        void ShowState()
        {
            bool has = files.Count > 0;
            drop.Visible = !has;
            foreach (Control c in new Control[] { mode, bAdd, bClear, bRename, list }) c.Visible = has;
            bUndo.Visible = has && undo != null;
            ShowFields();
            Preview();
            DoLayout();
        }

        void ShowFields()
        {
            bool has = files.Count > 0;
            int m = mode.SelectedIndex;
            fName.Visible = fStart.Visible = order.Visible = has && m == 0;
            fFind.Visible = fReplace.Visible = has && m == 1;
            fBefore.Visible = fAfter.Visible = has && m == 2;
            letters.Visible = has && m == 3;
            infoKind.Visible = infoStyle.Visible = has && m == 4;
            DoLayout();
        }

        public override void DoLayout()
        {
            if (mode == null) return;
            int W = Width, H = Height, p = Pad, ip = Theme.S(20), gap = Theme.S(16);
            cards.Clear();
            if (files.Count == 0)
            {
                emptyCard = new Rectangle(p, Top0, W - p * 2, H - Top0 - p);
                cards.Add(emptyCard);
                Place(drop, new Rectangle(emptyCard.X + ip, emptyCard.Y + ip, emptyCard.Width - ip * 2, emptyCard.Height - ip * 2));
                Invalidate(); return;
            }
            int ow = Math.Max(Theme.S(300), Math.Min(Theme.S(400), (W - p * 2) * 2 / 5));
            optCard = new Rectangle(p, Top0, ow, H - Top0 - p);
            listCard = new Rectangle(p + ow + gap, Top0, W - p * 2 - ow - gap, H - Top0 - p);
            cards.Add(optCard); cards.Add(listCard);

            int x = optCard.X + ip, w = optCard.Width - ip * 2, y = optCard.Y + Theme.S(18);
            modeLbl = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
            Place(mode, new Rectangle(x, y, w, Theme.S(36))); y += Theme.S(52);
            int m = mode.SelectedIndex;
            l1 = new Rectangle(x, y, w, Theme.S(20));
            if (m == 3)
            {
                Place(letters, new Rectangle(x, y + Theme.S(24), w, Theme.S(36)));
                l2 = l3 = Rectangle.Empty;
            }
            else if (m == 4)
            {
                Place(infoKind, new Rectangle(x, y + Theme.S(24), w, Theme.S(36)));
                y += Theme.S(74);
                l2 = new Rectangle(x, y, w, Theme.S(20));
                Place(infoStyle, new Rectangle(x, y + Theme.S(24), w, Theme.S(36)));
                l3 = Rectangle.Empty;
            }
            else
            {
                TextField a = m == 0 ? fName : m == 1 ? fFind : fBefore, b = m == 0 ? fStart : m == 1 ? fReplace : fAfter;
                Place(a, new Rectangle(x, y + Theme.S(24), w, Theme.S(38)));
                y += Theme.S(74);
                l2 = new Rectangle(x, y, m == 0 ? Theme.S(110) : w, Theme.S(20));
                if (m == 0)
                {
                    Place(fStart, new Rectangle(x, y + Theme.S(24), Theme.S(110), Theme.S(38)));
                    l3 = new Rectangle(x + Theme.S(124), y, w - Theme.S(124), Theme.S(20));
                    Place(order, new Rectangle(x + Theme.S(124), y + Theme.S(25), w - Theme.S(124), Theme.S(36)));
                }
                else { Place(b, new Rectangle(x, y + Theme.S(24), w, Theme.S(38))); l3 = Rectangle.Empty; }
            }
            int by = optCard.Bottom - ip - Theme.S(42);
            Place(bRename, new Rectangle(x, by, w, Theme.S(42)));
            int uw = Math.Max(Theme.S(120), bUndo.PreferredWidth);
            Place(bUndo, new Rectangle(x, by - Theme.S(44), uw, Theme.S(34)));
            statusR = new Rectangle(x, by - Theme.S(110), w, Theme.S(60));

            int lx = listCard.X + ip, lw = listCard.Width - ip * 2;
            countR = new Rectangle(lx, listCard.Y + Theme.S(18), lw - Theme.S(260), Theme.S(36));
            int aw = Math.Max(Theme.S(110), bAdd.PreferredWidth), cw = Math.Max(Theme.S(90), bClear.PreferredWidth);
            Place(bClear, new Rectangle(lx + lw - cw, listCard.Y + Theme.S(18), cw, Theme.S(34)));
            Place(bAdd, new Rectangle(lx + lw - cw - aw - Theme.S(8), listCard.Y + Theme.S(18), aw, Theme.S(34)));
            Place(list, new Rectangle(listCard.X + Theme.S(6), listCard.Y + Theme.S(64), listCard.Width - Theme.S(12), listCard.Height - Theme.S(72)));
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            if (files.Count == 0) return;
            Font lf = Theme.F(9f, FontStyle.Bold);
            Label(g, L.T("ren_how"), lf, Theme.InkSoft, modeLbl);
            int m = mode.SelectedIndex;
            string[] k1 = { "ren_name", "ren_find", "ren_before", "ren_letters", "ren_info_use" };
            string[] k2 = { "ren_start", "ren_replace", "ren_after", null, "ren_info_look" };
            Label(g, L.T(k1[m]), lf, Theme.InkSoft, l1);
            if (k2[m] != null && !l2.IsEmpty) Label(g, L.T(k2[m]), lf, Theme.InkSoft, l2);
            if (m == 0 && !l3.IsEmpty) Label(g, L.T("ren_order"), lf, Theme.InkSoft, l3);
            int changes = ChangeCount, probs = 0; foreach (int pr in problems) if (pr >= 2) probs++;
            Label(g, L.T("ren_count", files.Count, changes), Theme.F(10.5f, FontStyle.Bold), Theme.Ink, countR);
            string st = status; Color sc = statusColor;
            if (st == null && reading) { st = L.T("ren_reading"); sc = Theme.InkSoft; }
            if (st == null && m == 4 && infoKind.SelectedIndex == 1 && changes == 0 && files.Count > 0) { st = L.T("ren_no_tags"); sc = Theme.InkSoft; }
            if (st == null && probs > 0) { st = L.T("ren_problems", probs); sc = Theme.Error; }
            if (st == null && files.Count > 0) { st = L.T("ren_hint"); sc = Theme.InkFaint; }
            if (st != null) Label(g, st, Theme.F(9f), sc, statusR, Align.Start, true, true);
        }

        protected override void DragHighlight(bool on) { drop.DragHot = on; drop.Invalidate(); }

        // ------------------------------------------------------------------ the list: new name, and what it was
        class RenameList : ScrollList
        {
            readonly RenamePage page;
            public RenameList(RenamePage p) { page = p; RowHeight = 50; }
            protected override string RowText(int i) { string a = System.IO.Path.GetFileName(page.files[i]); return i < page.newNames.Count ? a + " → " + page.newNames[i] : a; }
            public override int Count { get { return page.files.Count; } }

            protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
            {
                if (i >= page.newNames.Count) return;
                if (hover) Theme.FillRound(g, new Rectangle(r.X + Theme.S(6), r.Y + Theme.S(2), r.Width - Theme.S(12), r.Height - Theme.S(4)), Theme.S(8), Theme.Hover);
                int pr = page.problems[i];
                string oldName = Path.GetFileName(page.files[i]), nn = page.newNames[i];
                Rectangle ic = Theme.Mirror(new Rectangle(r.X + Theme.S(14), r.Y + Theme.S(9), Theme.S(32), Theme.S(32)), Width);
                if (!Thumbs.Draw(g, page.files[i], ic, Theme.S(7), Invalidate))
                {
                    Theme.FillRound(g, ic, Theme.S(7), Theme.Selected);
                    Theme.Glyph(g, Formats.GlyphFor(Formats.CategoryOf(Formats.Ext(page.files[i]))), 10f, Theme.InkSoft, ic);
                }
                int tx = Theme.S(58), tw = r.Width - tx - Theme.S(150);
                Color nc = pr >= 2 ? Theme.Error : pr == 1 ? Theme.InkSoft : Theme.Ink;
                Rectangle n1 = Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(6), tw, Theme.S(22)), Width);
                Rectangle n2 = Theme.Mirror(new Rectangle(r.X + tx, r.Y + Theme.S(27), tw, Theme.S(18)), Width);
                Theme.Text(g, nn, Theme.F(9.75f, pr == 0 ? FontStyle.Bold : FontStyle.Regular), nc, n1, Theme.RTL ? Align.End : Align.Start, true, false, true);
                if (pr != 1) Theme.Text(g, L.T("ren_was", oldName), Theme.F(8.5f), Theme.InkFaint, n2, Align.Start);
                string tag = pr == 1 ? L.T("ren_same") : pr == 2 ? L.T("ren_taken") : pr == 3 ? L.T("ren_bad") : "";
                if (tag.Length > 0)
                {
                    Rectangle tr = Theme.Mirror(new Rectangle(r.Right - Theme.S(140), r.Y, Theme.S(124), r.Height), Width);
                    Theme.Text(g, tag, Theme.F(8.5f, FontStyle.Bold), pr >= 2 ? Theme.Error : Theme.InkFaint, tr, Align.End);
                }
                using (Pen lp = new Pen(Theme.Line)) g.DrawLine(lp, r.X + Theme.S(14), r.Bottom - 1, r.Right - Theme.S(14), r.Bottom - 1);
            }

            protected override string[] DragFiles(int i) { return new[] { page.files[i] }; }
        }
    }
}
