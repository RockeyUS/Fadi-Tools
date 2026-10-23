using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FileConverter
{
    /// Settings: the card that opens Watch folders.
    public class WatchEntry : WheelEntry
    {
        protected override string TitleKey { get { return "wf_title"; } }
        protected override string SubKey { get { return "wf_entry_d"; } }
        protected override bool IsOn { get { return Watchers.OnCount > 0; } }
        protected override string StateText { get { int n = Watchers.OnCount; return n > 0 ? L.T("wf_n_on", n) : L.T("whp_state_off"); } }

        /// A folder with a small arrow going round it (things arriving get changed).
        protected override void DrawArt(Graphics g, Rectangle r, float hot)
        {
            Rectangle tile = Rectangle.Inflate(r, -Theme.S(4), -Theme.S(4));
            Theme.FillRound(g, tile, Theme.S(14), Theme.Mix(Theme.CardInner, Theme.AccentColor, 0.18f + 0.12f * hot));
            Theme.Glyph(g, Ico.Folder, 15f, Theme.AccentColor, tile);
            Rectangle badge = new Rectangle(tile.Right - Theme.S(24), tile.Bottom - Theme.S(24), Theme.S(26), Theme.S(26));
            Theme.FillRound(g, badge, Theme.S(13), Theme.AccentFill);
            System.Drawing.Drawing2D.GraphicsState st = g.Save();
            g.TranslateTransform(badge.X + badge.Width / 2f, badge.Y + badge.Height / 2f);
            g.RotateTransform(hot * 180f);
            g.TranslateTransform(-(badge.X + badge.Width / 2f), -(badge.Y + badge.Height / 2f));
            Theme.Glyph(g, Ico.Sync, 8f, Theme.PrimaryInk, badge);
            g.Restore(st);
        }
    }

    /// Settings → Watch folders: each folder with what kind of file to watch for and what to do with it.
    /// Everything is saved straight away; the folders are watched while Fadi Tools runs (also by the clock).
    public class WatchPage : Page
    {
        readonly Btn back, bAdd;
        public event Action BackRequested;
        List<WatchRule> rules = new List<WatchRule>();
        readonly List<Toggle> toggles = new List<Toggle>();
        // per rule (in LTR page coordinates): the card, the two chips, "change folder", remove
        readonly List<Rectangle> cardR = new List<Rectangle>(), typeR = new List<Rectangle>(), actR = new List<Rectangle>(), folderR = new List<Rectangle>(), delR = new List<Rectangle>();
        Rectangle emptyR, noteR;
        int hot = -1, hotPart = -1;   // part: 0 type chip, 1 action chip, 2 folder, 3 remove
        int maxScroll;

        public WatchPage() : base("wf_title", "wf_sub")
        {
            back = new Btn(() => L.T("studio_back"), Ico.Back, BtnKind.Secondary);
            back.Click += (s, e) => { if (BackRequested != null) BackRequested(); };
            bAdd = new Btn(() => L.T("wf_add"), Ico.Add, BtnKind.Primary);
            bAdd.Click += (s, e) => AddFolder();
            Controls.AddRange(new Control[] { back, bAdd });
            Watchers.Changed += () => { if (IsHandleCreated && Visible) Invalidate(); };
        }

        public override void OnShown() { Reload(); }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape && BackRequested != null) { BackRequested(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        void Reload()
        {
            rules = Watchers.All();
            foreach (Toggle t in toggles) { Controls.Remove(t); t.Dispose(); }
            toggles.Clear();
            foreach (WatchRule r in rules)
            {
                WatchRule rule = r;
                Toggle t = new Toggle();
                t.SetSilently(r.On);
                t.CheckedChanged += (s, e) => { rule.On = t.Checked; Watchers.Save(rule); Invalidate(); };
                toggles.Add(t); Controls.Add(t);
            }
            DoLayout();
        }

        void AddFolder()
        {
            string start = Paths.Downloads;
            string dir = FolderPicker.Pick(FindForm(), L.T("wf_pick"), start);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            WatchRule r = new WatchRule { Folder = dir, Types = "heic", Action = "convert:jpg", On = true };
            Watchers.Save(r);
            if (Settings.Get("CloseToTray", "0") != "1")
                ShowToast(L.T("wf_needs_tray"), true, () => L.T("wheel_keep_running"), () => { Settings.Set("CloseToTray", "1"); DragWheel.NotifyChanged(); });
            Reload();
        }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, ip = Theme.S(20), gap = Theme.S(14);
            cards.Clear(); cardR.Clear(); typeR.Clear(); actR.Clear(); folderR.Clear(); delR.Clear();
            int bw = Math.Max(Theme.S(110), back.PreferredWidth);
            HeaderReserve = bw + Theme.S(12);
            Place(back, new Rectangle(W - p - bw, Theme.S(24) - ScrollY, bw, Theme.S(38)));
            int cw = Math.Min(W - p * 2, Theme.S(900));
            int y = Top0;
            if (rules.Count == 0)
            {
                emptyR = new Rectangle(p, y, cw, Theme.S(150));
                cards.Add(emptyR);
                y = emptyR.Bottom + gap;
            }
            for (int i = 0; i < rules.Count; i++)
            {
                Rectangle c = new Rectangle(p, y, cw, Theme.S(136));
                cards.Add(c); cardR.Add(c);
                Place(toggles[i], new Rectangle(c.Right - ip - Theme.S(52), c.Y + Theme.S(16), Theme.S(52), Theme.S(34)));
                folderR.Add(new Rectangle(c.Right - ip - Theme.S(52) - Theme.S(12) - Theme.S(110), c.Y + Theme.S(18), Theme.S(110), Theme.S(30)));
                int cy = c.Y + Theme.S(66);
                // "When [HEIC photos ▾] arrive → [Convert to JPG ▾]"
                int lead = Theme.Measure(L.T("wf_when"), Theme.F(9.5f)) + Theme.S(10);
                Rectangle t = new Rectangle(c.X + ip + lead, cy, ChipW(Watchers.TypeName(rules[i].Types)), Theme.S(34));
                int mid = Theme.Measure(L.T("wf_then"), Theme.F(9.5f)) + Theme.S(20);
                Rectangle a = new Rectangle(t.Right + mid, cy, ChipW(Watchers.ActionName(rules[i].Action)), Theme.S(34));
                typeR.Add(t); actR.Add(a);
                delR.Add(new Rectangle(c.Right - ip - Theme.S(34), cy, Theme.S(34), Theme.S(34)));
                y = c.Bottom + gap;
            }
            int aw = Math.Max(Theme.S(170), bAdd.PreferredWidth + Theme.S(12));
            Place(bAdd, rules.Count == 0 ? new Rectangle(emptyR.X + ip, emptyR.Bottom - Theme.S(60), aw, Theme.S(40)) : new Rectangle(p, y, aw, Theme.S(40)));
            if (rules.Count > 0) y += Theme.S(52);
            noteR = new Rectangle(p, y, cw, Theme.S(40));
            int bottom = noteR.Bottom + p + ScrollY;
            maxScroll = Math.Max(0, bottom - H);
            if (ScrollY > maxScroll) { ScrollY = maxScroll; DoLayout(); return; }
            Invalidate();
        }

        static int ChipW(string text) { return Math.Min(Theme.S(260), Theme.Measure(text, Theme.F(9f, FontStyle.Bold)) + Theme.S(44)); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (maxScroll <= 0) return;
            int n = Math.Max(0, Math.Min(maxScroll, ScrollY - e.Delta / 120 * Theme.S(70)));
            if (n != ScrollY) { ScrollY = n; DoLayout(); Update(); }
        }

        Point Ltr(Point p) { return Theme.RTL ? new Point(Width - p.X, p.Y) : p; }

        void Hit(Point p, out int rule, out int part)
        {
            rule = -1; part = -1; p = Ltr(p);
            for (int i = 0; i < cardR.Count; i++)
            {
                if (!cardR[i].Contains(p)) continue;
                rule = i;
                if (typeR[i].Contains(p)) part = 0;
                else if (actR[i].Contains(p)) part = 1;
                else if (folderR[i].Contains(p)) part = 2;
                else if (delR[i].Contains(p)) part = 3;
                return;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int r, pt; Hit(e.Location, out r, out pt);
            if (r != hot || pt != hotPart) { hot = r; hotPart = pt; Cursor = pt >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = hotPart = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int i, pt; Hit(e.Location, out i, out pt);
            if (i < 0 || pt < 0) return;
            WatchRule r = rules[i];
            Rectangle chip = M(pt == 0 ? typeR[i] : pt == 1 ? actR[i] : pt == 2 ? folderR[i] : delR[i]);
            Point below = PointToScreen(new Point(chip.X, chip.Bottom + Theme.S(4)));
            if (pt == 3) { Watchers.Delete(r.Id); Reload(); return; }
            if (pt == 2)
            {
                string dir = FolderPicker.Pick(FindForm(), L.T("wf_pick"), r.Folder);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) { r.Folder = dir; Watchers.Save(r); Reload(); }
                return;
            }
            List<MenuPopup.Item> items = new List<MenuPopup.Item>();
            if (pt == 0)
            {
                foreach (string t in Watchers.TypeKeys)
                {
                    string tt = t;
                    items.Add(new MenuPopup.Item { Text = Watchers.TypeName(t), Glyph = t == r.Types ? Ico.Check : null, Run = () =>
                    {
                        string oldKind = Watchers.KindOfType(r.Types);
                        r.Types = tt;
                        // a different kind of file: the action changes to one that fits it
                        if (Watchers.KindOfType(tt) != oldKind) r.Action = DefaultAction(tt);
                        Watchers.Save(r); Reload();
                    } });
                }
            }
            else
            {
                string kind = Watchers.KindOfType(r.Types);
                items.Add(new MenuPopup.Item { Header = L.T("wf_h_convert") });
                foreach (string f in WheelConfig.Pool(kind))
                {
                    if (r.Types == "heic" && f == "heic") continue;
                    string a = "convert:" + f;
                    items.Add(new MenuPopup.Item { Text = L.T("wf_a_convert", f.ToUpperInvariant()), Glyph = a == r.Action ? Ico.Check : Ico.Convert, Run = () => { r.Action = a; Watchers.Save(r); Reload(); } });
                }
                if (kind == "image" || kind == "video" || kind == "audio" || kind == "pdf")
                {
                    items.Add(new MenuPopup.Item { Header = L.T("wf_h_compress") });
                    foreach (string pc in new[] { "25", "50", "75" })
                    {
                        string a = "compress:" + pc;
                        items.Add(new MenuPopup.Item { Text = L.T("wf_a_compress", pc), Glyph = a == r.Action ? Ico.Check : "", Run = () => { r.Action = a; Watchers.Save(r); Reload(); } });
                    }
                }
                List<string> rec = Array.IndexOf(Recipes.Kinds, kind) >= 0 ? Recipes.IdsFor(kind) : new List<string>();
                if (rec.Count > 0)
                {
                    items.Add(new MenuPopup.Item { Header = L.T("rc_title") });
                    foreach (string id in rec)
                    {
                        string a = id;
                        Recipe rc = Recipes.Get(id);
                        items.Add(new MenuPopup.Item { Text = rc.Name, Sub = Recipes.Summary(rc), Glyph = a == r.Action ? Ico.Check : Ico.Recipe, Run = () => { r.Action = a; Watchers.Save(r); Reload(); } });
                    }
                }
            }
            MenuPopup.ShowAt(this, below, items, 280);
        }

        static string DefaultAction(string types)
        {
            switch (Watchers.KindOfType(types))
            {
                case "image": return "convert:jpg";
                case "video": return "convert:mp4";
                case "audio": return "convert:mp3";
                case "pdf": return "convert:docx";
                default: return "convert:pdf";
            }
        }

        protected override void PaintContent(Graphics g)
        {
            int ip = Theme.S(20);
            if (rules.Count == 0)
            {
                Rectangle ic = new Rectangle(emptyR.X + ip, emptyR.Y + Theme.S(22), Theme.S(44), Theme.S(44));
                Theme.FillRound(g, M(ic), Theme.S(12), Theme.AccentSoft);
                Theme.Glyph(g, Ico.Folder, 13f, Theme.AccentColor, M(ic));
                Label(g, L.T("wf_empty_t"), Theme.F(11f, FontStyle.Bold), Theme.Ink, new Rectangle(ic.Right + Theme.S(14), emptyR.Y + Theme.S(18), emptyR.Width - Theme.S(100), Theme.S(26)), Align.Start);
                Label(g, L.T("wf_empty_d"), Theme.F(9f), Theme.InkSoft, new Rectangle(ic.Right + Theme.S(14), emptyR.Y + Theme.S(44), emptyR.Width - Theme.S(100), Theme.S(40)), Align.Start, false, true);
            }
            for (int i = 0; i < rules.Count; i++)
            {
                WatchRule r = rules[i];
                Rectangle c = cardR[i];
                bool exists = Directory.Exists(r.Folder);
                Rectangle ic = new Rectangle(c.X + ip, c.Y + Theme.S(16), Theme.S(36), Theme.S(36));
                Theme.FillRound(g, M(ic), Theme.S(10), r.On && exists ? Theme.AccentSoft : Theme.Selected);
                Theme.Glyph(g, Ico.Folder, 11f, r.On && exists ? Theme.AccentColor : Theme.InkFaint, M(ic));
                int tw = folderR[i].X - ic.Right - Theme.S(24);
                string name = Path.GetFileName(r.Folder.TrimEnd('\\', '/'));
                Label(g, name.Length > 0 ? name : r.Folder, Theme.F(10.5f, FontStyle.Bold), r.On ? Theme.Ink : Theme.InkSoft, new Rectangle(ic.Right + Theme.S(12), c.Y + Theme.S(13), tw, Theme.S(22)), Align.Start);
                Label(g, exists ? r.Folder : L.T("wf_missing", r.Folder), Theme.F(8.5f), exists ? Theme.InkSoft : Theme.Error, new Rectangle(ic.Right + Theme.S(12), c.Y + Theme.S(35), tw, Theme.S(18)), Align.Start);
                bool hf = hot == i && hotPart == 2;
                if (hf) Theme.FillRound(g, M(folderR[i]), Theme.S(8), Theme.Hover);
                Label(g, L.T("wf_change"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, folderR[i], Align.Center);
                // the sentence
                Rectangle t = typeR[i], a = actR[i];
                Label(g, L.T("wf_when"), Theme.F(9.5f), Theme.Ink, new Rectangle(c.X + ip, t.Y, t.X - c.X - ip, t.Height), Align.Start);
                Chip(g, t, Watchers.TypeName(r.Types), hot == i && hotPart == 0);
                Label(g, L.T("wf_then"), Theme.F(9.5f), Theme.Ink, new Rectangle(t.Right, t.Y, a.X - t.Right, t.Height), Align.Center);
                Chip(g, a, Watchers.ActionName(r.Action), hot == i && hotPart == 1);
                bool hd = hot == i && hotPart == 3;
                if (hd) Theme.FillRound(g, M(delR[i]), Theme.S(8), Theme.Mix(Theme.Card, Theme.Error, 0.12f));
                Theme.Glyph(g, Ico.Delete, 9f, hd ? Theme.Error : Theme.InkSoft, M(delR[i]));
                string last;
                lock (Watchers.LastDone) Watchers.LastDone.TryGetValue(r.Id, out last);
                Label(g, last != null ? L.T("wf_last", last) : r.On ? L.T("wf_waiting") : L.T("wf_paused"), Theme.F(8.25f), last != null ? Theme.Success : Theme.InkFaint, new Rectangle(c.X + ip, c.Bottom - Theme.S(30), c.Width - ip * 2, Theme.S(20)), Align.Start);
            }
            Label(g, L.T("wf_note"), Theme.F(8.5f), Theme.InkFaint, noteR, Align.Start, false, true);
        }

        void Chip(Graphics g, Rectangle ltr, string text, bool hover)
        {
            Rectangle r = M(ltr);
            Theme.FillRound(g, r, Theme.S(10), hover ? Theme.Mix(Theme.CardInner, Theme.AccentColor, 0.18f) : Theme.CardInner);
            Theme.StrokeRound(g, new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), Theme.S(10), hover ? Theme.AccentColor : Theme.ChipBorder);
            Label(g, text, Theme.F(9f, FontStyle.Bold), Theme.Ink, new Rectangle(ltr.X + Theme.S(12), ltr.Y, ltr.Width - Theme.S(34), ltr.Height), Align.Start);
            Theme.Glyph(g, "", 7f, Theme.InkSoft, M(new Rectangle(ltr.Right - Theme.S(26), ltr.Y, Theme.S(20), ltr.Height)));
        }
    }
}
