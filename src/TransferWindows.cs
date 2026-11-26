using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    // ====================================================================== the send card

    /// Dropping files to send brings up this card: a picture of what's going (the file's own thumbnail, or its kind),
    /// its name and size, and who it's for: tick friends and PCs on the network (several at once), or get a code for
    /// anyone else. Once it's sending, the same card shows how it's going, wherever Fadi Tools' own window is.
    public class SendCard : MiniWindow
    {
        readonly TransferPage page;
        readonly List<string> items = new List<string>();
        readonly Btn bSend, bAdd, bAddFolder, bCopy, bStop, bApp;
        readonly Progress bar = new Progress();
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 300 };
        List<FtTarget> choices = new List<FtTarget>();
        readonly HashSet<string> picked = new HashSet<string>();
        bool wantCode;
        long size = -1; int count;
        int hot = -1, ticks, overTicks;
        Rectangle artR, listR, codeR;
        /// Still asking who it's for (not sending yet).
        public bool Choosing { get; private set; }
        const int MaxRows = 6;
        int RowH { get { return Theme.S(50); } }

        public static SendCard Choose(TransferPage page, List<string> items, Point at)
        {
            SendCard c = new SendCard(page, true);
            c.items.AddRange(items);
            c.LoadChoices();
            c.Recount();
            c.Size2();
            c.ShowNear(at);
            return c;
        }

        /// Opens straight on the progress of what the page is already sending (a drop on a friend).
        public static SendCard Watch(TransferPage page, Point at)
        {
            SendCard c = new SendCard(page, false);
            c.Size2();
            c.ShowNear(at);
            return c;
        }

        SendCard(TransferPage page, bool choosing) : base(() => L.T("ft_card_title"), Ico.Send)
        {
            this.page = page; Choosing = choosing;
            bSend = new Btn(() => SendText(), Ico.Send, BtnKind.Primary) { Surface = Surface.Card };
            bSend.Click += (s, e) => Go();
            bAdd = new Btn(() => L.T("ft_add_files"), Ico.Add, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 8.5f, GlyphSize = 8.5f };
            bAdd.Click += (s, e) =>
            {
                using (OpenFileDialog d = new OpenFileDialog { Multiselect = true, Title = L.T("ft_add_files"), Filter = L.T("all_files") + " (*.*)|*.*" })
                    if (d.ShowDialog(this) == DialogResult.OK) AddItems(new List<string>(d.FileNames));
            };
            bAddFolder = new Btn(() => L.T("ft_add_folder"), Ico.FolderOpen, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 8.5f, GlyphSize = 8.5f };
            bAddFolder.Click += (s, e) => { string d = FolderPicker.Pick(this, L.T("ft_add_folder"), null); if (d != null) AddItems(new List<string> { d }); };
            bCopy = new Btn(() => L.T("ft_copy"), Ico.Copy, BtnKind.Primary) { Surface = Surface.Card, FontSize = 9f };
            bCopy.Click += (s, e) => page.CopyCode();
            bStop = new Btn(() => L.T(page.SendOver || page.SendFailed ? "close" : "ft_stop_send"), null, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bStop.Click += (s, e) => { if (!page.SendOver && !page.SendFailed) page.StopFromCard(); Dismiss(); };
            bApp = new Btn(() => L.T("ft_card_open"), Ico.Open, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 9f };
            bApp.Click += (s, e) => page.Attention();
            bar.Surface = Surface.Card;
            Controls.AddRange(new Control[] { bSend, bAdd, bAddFolder, bCopy, bStop, bApp, bar });
            refresh.Tick += (s, e) => Tick();
            refresh.Start();
            Modes();
        }

        void Modes()
        {
            bSend.Visible = bAdd.Visible = bAddFolder.Visible = Choosing;
            bStop.Visible = bApp.Visible = !Choosing;
            bCopy.Visible = !Choosing && page.SendCode != null;
            bar.Visible = !Choosing && (page.SendProgress >= 0 || page.SendOver);
        }

        public void AddItems(List<string> more)
        {
            foreach (string p in more) if ((File.Exists(p) || Directory.Exists(p)) && !items.Contains(p)) items.Add(p);
            Recount(); Invalidate();
        }

        void Recount()
        {
            List<string> snap = new List<string>(items);
            size = -1;
            new Thread(() =>
            {
                string n; List<FtFile> l = FtHost.Collect(snap, out n);
                long s = 0; foreach (FtFile f in l) s += f.Size;
                Ui(() => { if (snap.Count == items.Count) { size = s; count = l.Count; Invalidate(); } });
            }) { IsBackground = true }.Start();
        }

        void LoadChoices()
        {
            choices = page.Choices();
            // someone who left the list isn't ticked any more
            picked.RemoveWhere(id => choices.Find(c => c.Id == id) == null);
            if (choices.Count == 0) wantCode = true;   // nobody to pick yet: a code it is
        }

        string SendText()
        {
            int n = picked.Count;
            if (n == 0) return L.T(wantCode ? "ft_card_get_code" : "ft_card_pick");
            if (n == 1 && !wantCode) { foreach (FtTarget t in choices) if (picked.Contains(t.Id)) return L.T("ft_card_send_to", t.Name); }
            return L.T(wantCode ? "ft_card_send_n_code" : "ft_card_send_n", n);
        }

        void Go()
        {
            if (!Choosing || (picked.Count == 0 && !wantCode)) return;
            List<FtTarget> to = new List<FtTarget>();
            foreach (FtTarget t in choices) if (picked.Contains(t.Id)) to.Add(t);
            if (page.BeginSend(new List<string>(items), to, wantCode)) ToWatching();
            // otherwise a download was asked for first (or declined): the card waits here
        }

        void ToWatching()
        {
            Choosing = false;
            Modes(); Size2(); DoLayout(); Invalidate();
        }

        void Tick()
        {
            if (IsDisposed) return;
            ticks++;
            if (Choosing)
            {
                if (!page.SendIdle) { ToWatching(); return; }     // it started (after a download that was asked for)
                if (ticks % 4 == 0) { int before = choices.Count; LoadChoices(); if (choices.Count != before) Size2(); Invalidate(); bSend.Invalidate(); }
                return;
            }
            if (page.SendIdle) { Dismiss(); return; }            // stopped from the page
            Modes();
            double p = page.SendProgress;
            if (p >= 0) bar.Value = p;
            Size2();
            bStop.Invalidate();
            Invalidate();
            // done: the card stays a little while, then goes by itself (not while the mouse is on it)
            if (page.SendOver) { overTicks++; if (overTicks > 30 && !Bounds.Contains(Cursor.Position)) Dismiss(); }
        }

        // ------------------------------------------------------------------ layout

        int Rows { get { return Math.Min(MaxRows, choices.Count) + 1; } }   // + the "get a code" row
        int watchTargets;

        /// The card is as tall as what's in it.
        void Size2()
        {
            int h;
            if (Choosing) h = Head + Theme.S(92) + Theme.S(34) + Rows * RowH + Theme.S(14) + Theme.S(46) + Theme.S(22);
            else
            {
                watchTargets = Math.Min(4, page.SendTargets.Count);
                h = Head + Theme.S(92) + Theme.S(74) + watchTargets * Theme.S(28) + (page.SendCode != null ? Theme.S(96) : 0) + Theme.S(40) + Theme.S(22);
            }
            Size want = new Size(Theme.S(430), h);
            if (ClientSize != want) ClientSize = want;
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (bSend == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, p = Pad;
            artR = new Rectangle(p, Head + Theme.S(4), Theme.S(76), Theme.S(76));
            int tx = artR.Right + Theme.S(16);
            int aw = bAdd.PreferredWidth + Theme.S(4), fw = bAddFolder.PreferredWidth + Theme.S(4);
            bAdd.Bounds = M(new Rectangle(tx - Theme.S(8), artR.Y + Theme.S(48), aw, Theme.S(28)));
            bAddFolder.Bounds = M(new Rectangle(tx - Theme.S(8) + aw + Theme.S(2), artR.Y + Theme.S(48), fw, Theme.S(28)));
            int y = Head + Theme.S(92);
            if (Choosing)
            {
                listR = new Rectangle(p - Theme.S(8), y + Theme.S(34), W - p * 2 + Theme.S(16), Rows * RowH);
                bSend.Bounds = M(new Rectangle(p, H - Theme.S(22) - Theme.S(46), W - p * 2, Theme.S(46)));
                return;
            }
            bar.Bounds = M(new Rectangle(p, y + Theme.S(58), W - p * 2, Theme.S(8)));
            y += Theme.S(74) + watchTargets * Theme.S(28);
            codeR = Rectangle.Empty;
            if (page.SendCode != null)
            {
                codeR = new Rectangle(p, y + Theme.S(4), W - p * 2, Theme.S(40));
                int cw = Math.Max(Theme.S(130), bCopy.PreferredWidth + Theme.S(12));
                bCopy.Bounds = M(new Rectangle(p, codeR.Bottom + Theme.S(8), cw, Theme.S(36)));
            }
            int by = H - Theme.S(22) - Theme.S(38);
            int sw = Math.Max(Theme.S(110), bStop.PreferredWidth + Theme.S(12)), ow = Math.Max(Theme.S(120), bApp.PreferredWidth + Theme.S(8));
            bStop.Bounds = M(new Rectangle(W - p - sw, by, sw, Theme.S(38)));
            bApp.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(2), ow, Theme.S(34)));
        }

        Rectangle RowR(int i) { return new Rectangle(listR.X, listR.Y + i * RowH, listR.Width, RowH - Theme.S(2)); }

        protected override void PaintBody(Graphics g)
        {
            int W = ClientSize.Width, p = Pad;
            int tx = artR.Right + Theme.S(16), tw = W - p - tx;
            string first = Choosing ? (items.Count > 0 ? items[0] : null) : page.SendFirstPath;
            int n = Choosing ? items.Count : page.SendItemCount;
            string name = Choosing ? (items.Count == 1 ? Path.GetFileName(items[0].TrimEnd('\\', '/')) : L.T("ft_n_items", items.Count)) : page.SendItemName;
            string sz = Choosing ? (size < 0 ? L.T("ft_counting") : TransferPage.FilesSize(count, size)) : page.SendSizeText;
            FtArt.Draw(g, M(artR), first, null, first != null ? Path.GetFileName(first.TrimEnd('\\', '/')) : "", first != null && Directory.Exists(first), n, () => Ui(Invalidate));
            Theme.Text(g, name, Theme.F(11f, FontStyle.Bold), Theme.Ink, M(new Rectangle(tx, artR.Y + Theme.S(2), tw, Theme.S(24))), Align.Start);
            Theme.Text(g, sz, Theme.F(8.75f), Theme.InkSoft, M(new Rectangle(tx, artR.Y + Theme.S(26), tw, Theme.S(20))), Align.Start);
            int y = Head + Theme.S(92);
            if (Choosing)
            {
                Theme.Text(g, L.T("ft_card_to").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, M(new Rectangle(p, y + Theme.S(8), W - p * 2, Theme.S(20))), Align.Start);
                int shown = Math.Min(MaxRows, choices.Count);
                for (int i = 0; i < shown; i++) PaintChoice(g, RowR(i), choices[i], i);
                // the last row: a code, for anyone who isn't in the list
                Rectangle cr = RowR(shown);
                bool chot = hot == shown;
                if (wantCode) Theme.FillRound(g, M(cr), Theme.S(12), Theme.Mix(Theme.Card, Theme.AccentColor, 0.1f));
                else if (chot) Theme.FillRound(g, M(cr), Theme.S(12), Theme.Hover);
                Rectangle av = new Rectangle(cr.X + Theme.S(10), cr.Y + (cr.Height - Theme.S(34)) / 2, Theme.S(34), Theme.S(34));
                using (SolidBrush b = new SolidBrush(Theme.CardInner)) g.FillEllipse(b, M(av));
                Theme.Glyph(g, Ico.Link, 9.5f, Theme.InkSoft, M(av));
                Theme.Text(g, L.T("ft_card_code"), Theme.F(9.5f, FontStyle.Bold), Theme.Ink, M(new Rectangle(cr.X + Theme.S(56), cr.Y + Theme.S(6), cr.Width - Theme.S(100), Theme.S(20))), Align.Start);
                Theme.Text(g, L.T(choices.Count == 0 ? "ft_card_none" : "ft_card_code_d"), Theme.F(8f), Theme.InkSoft, M(new Rectangle(cr.X + Theme.S(56), cr.Y + Theme.S(25), cr.Width - Theme.S(100), Theme.S(18))), Align.Start);
                Tick2(g, cr, wantCode);
                return;
            }
            // how it's going
            Color tc = page.SendOver ? Theme.Success : page.SendFailed ? Theme.Error : Theme.Ink;
            Theme.Text(g, page.SendTitle, Theme.F(10.5f, FontStyle.Bold), tc, M(new Rectangle(p, y, W - p * 2, Theme.S(24))), Align.Start);
            Theme.Text(g, page.SendDetail, Theme.F(8.5f), page.SendFailed ? Theme.Error : Theme.InkSoft, M(new Rectangle(p, y + Theme.S(24), W - p * 2, bar.Visible ? Theme.S(30) : Theme.S(46))), Align.Start, false, true);
            y += Theme.S(74);
            List<FtTarget> ts = page.SendTargets;
            for (int i = 0; i < watchTargets && i < ts.Count; i++)
            {
                Rectangle r = new Rectangle(p, y + i * Theme.S(28), W - p * 2, Theme.S(26));
                Color col; string st = page.TargetStatus(ts[i], out col);
                Theme.Text(g, ts[i].Name, Theme.F(9f, FontStyle.Bold), Theme.Ink, M(new Rectangle(r.X, r.Y, r.Width / 2, r.Height)), Align.Start);
                Theme.Text(g, st, Theme.F(8.5f), col, M(new Rectangle(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height)), Align.End);
            }
            if (!codeR.IsEmpty && page.SendCode != null)
            {
                Theme.FillRound(g, M(codeR), Theme.S(10), Theme.CardInner);
                Theme.StrokeRound(g, M(new Rectangle(codeR.X, codeR.Y, codeR.Width - 1, codeR.Height - 1)), Theme.S(10), Theme.Line);
                Theme.Text(g, page.SendCode, Theme.Mono(8.75f), Theme.InkSoft, M(Rectangle.Inflate(codeR, -Theme.S(12), 0)), Align.Start, true, false, true);
            }
        }

        void PaintChoice(Graphics g, Rectangle r, FtTarget t, int i)
        {
            bool on = picked.Contains(t.Id);
            if (on) Theme.FillRound(g, M(r), Theme.S(12), Theme.Mix(Theme.Card, Theme.AccentColor, 0.1f));
            else if (hot == i) Theme.FillRound(g, M(r), Theme.S(12), Theme.Hover);
            Rectangle av = new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - Theme.S(34)) / 2, Theme.S(34), Theme.S(34));
            using (SolidBrush b = new SolidBrush(t.Friend != null ? Theme.AccentSoft : Theme.CardInner)) g.FillEllipse(b, M(av));
            if (t.Friend != null) Theme.Text(g, t.Name.Length > 0 ? t.Name.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture) : "?", Theme.F(10f, FontStyle.Bold), Theme.AccentColor, M(av), Align.Center, true, false, true);
            else Theme.Glyph(g, Ico.Pc, 9f, Theme.InkSoft, M(av));
            bool? there = page.OnlineOf(t);
            if (there != null)
            {
                Rectangle dot = M(new Rectangle(av.Right - Theme.S(10), av.Bottom - Theme.S(10), Theme.S(11), Theme.S(11)));
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillEllipse(b, dot);
                Rectangle inner = Rectangle.Inflate(dot, -Theme.S(2), -Theme.S(2));
                if (there == true) using (SolidBrush b = new SolidBrush(Theme.Success)) g.FillEllipse(b, inner);
                else using (Pen pn = new Pen(Theme.InkFaint, 1.5f)) g.DrawEllipse(pn, inner);
            }
            Theme.Text(g, t.Name, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, M(new Rectangle(r.X + Theme.S(56), r.Y + Theme.S(6), r.Width - Theme.S(100), Theme.S(20))), Align.Start);
            Theme.Text(g, page.SubFor(t), Theme.F(8f), there == true ? Theme.Success : Theme.InkSoft, M(new Rectangle(r.X + Theme.S(56), r.Y + Theme.S(25), r.Width - Theme.S(100), Theme.S(18))), Align.Start);
            Tick2(g, r, on);
        }

        /// The round tick at the end of a row.
        void Tick2(Graphics g, Rectangle r, bool on)
        {
            Rectangle c = M(new Rectangle(r.Right - Theme.S(36), r.Y + (r.Height - Theme.S(22)) / 2, Theme.S(22), Theme.S(22)));
            if (on)
            {
                using (SolidBrush b = new SolidBrush(Theme.AccentFill)) g.FillEllipse(b, c);
                Theme.Glyph(g, Ico.Check, 7.5f, Theme.PrimaryInk, c);
            }
            else using (Pen pn = new Pen(Theme.FieldBorder, 1.5f)) g.DrawEllipse(pn, c.X + 1, c.Y + 1, c.Width - 2, c.Height - 2);
        }

        // ------------------------------------------------------------------ the mouse and the keyboard

        int HitChoice(Point pt)
        {
            if (!Choosing) return -1;
            Point q = Theme.RTL ? new Point(ClientSize.Width - pt.X, pt.Y) : pt;
            int shown = Math.Min(MaxRows, choices.Count);
            for (int i = 0; i <= shown; i++) if (RowR(i).Contains(q)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitChoice(e.Location);
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot >= 0) { hot = -1; Invalidate(); } }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int h = HitChoice(e.Location);
            if (h < 0) return;
            int shown = Math.Min(MaxRows, choices.Count);
            if (h == shown) { if (choices.Count > 0) wantCode = !wantCode; }
            else { string id = choices[h].Id; if (!picked.Remove(id)) picked.Add(id); }
            bSend.Enabled = picked.Count > 0 || wantCode;
            bSend.Invalidate(); Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            bSend.Enabled = picked.Count > 0 || wantCode;
            // files dropped on the card itself are added to what's going
            AllowDrop = true;
            DragEnter += (s, a) => a.Effect = Choosing && a.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, a) => { string[] p = a.Data.GetData(DataFormats.FileDrop) as string[]; if (p != null && Choosing) AddItems(new List<string>(p)); };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Enter && Choosing && bSend.Enabled) { Go(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { refresh.Stop(); refresh.Dispose(); base.OnFormClosed(e); }
    }

    // ====================================================================== the receive pop-up

    /// Someone is sending files and Fadi Tools isn't in front: a small card by the clock with a picture of what's
    /// coming, who it's from and its size, and Accept or Decline. Once accepted (or straight away for a friend
    /// whose files are accepted automatically) it shows the progress, then where it was saved.
    public class FtPopup : Form
    {
        static FtPopup open;
        readonly TransferPage page;
        readonly Btn bMain, bOther, bClose;
        readonly Progress bar = new Progress();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        int phase = -1, doneTicks;
        bool showMain, showOther;
        float slide;
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public static void Show(TransferPage page)
        {
            if (open != null && !open.IsDisposed) { open.Sync(); return; }
            open = new FtPopup(page);
            open.Pop();
        }

        FtPopup(TransferPage page)
        {
            this.page = page;
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Theme.Card; Font = Theme.F(10f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = L.T("ft_title");
            bMain = new Btn(() => MainText(), null, BtnKind.Primary) { Surface = Surface.Card, FontSize = 9f };
            bMain.Click += (s, e) => MainClick();
            bOther = new Btn(() => OtherText(), null, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bOther.Click += (s, e) => OtherClick();
            bClose = new Btn(() => "", Ico.Close, BtnKind.Ghost) { Surface = Surface.Card, GlyphSize = 8f, AccessibleName = L.T("close") };
            bClose.Click += (s, e) => Close();
            bar.Surface = Surface.Card;
            Controls.AddRange(new Control[] { bMain, bOther, bClose, bar });
            ClientSize = new Size(Theme.S(380), Theme.S(168));
            timer.Tick += (s, e) => Sync();
            page.RecvChanged += OnChanged;
        }
        void OnChanged() { try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)Sync); } catch { } }

        // shown without taking the keyboard away from what the person is doing
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x8; cp.ClassStyle |= 0x20000; return cp; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Theme.S(18), Theme.S(18))); } catch { }
        }

        /// By the clock: the bottom corner of the main screen (the left one in Arabic, where the clock is).
        void Pop()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int m = Theme.S(14);
            int x = Theme.RTL ? wa.Left + m : wa.Right - Width - m;
            Location = new Point(x, wa.Bottom - Height - m + (Fx.Motion ? Theme.S(18) : 0));
            Opacity = Fx.Motion ? 0 : 1;
            slide = 0;
            Sync();
            Show();
            timer.Start();
        }

        void Sync()
        {
            if (IsDisposed) return;
            int ph = page.RecvPhase;
            if (ph == 0) { Close(); return; }
            if (slide < 1 && Fx.Motion)
            {
                slide = Math.Min(1, slide + 0.34f);
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                try { Opacity = slide; Top = wa.Bottom - Height - Theme.S(14) + (int)(Theme.S(18) * (1 - slide)); } catch { }
            }
            if (ph != phase)
            {
                phase = ph; doneTicks = 0;
                bMain.Visible = showMain = ph == 2 || ph == 3 || ph == 4 || ph == 5;
                bOther.Visible = showOther = ph == 2 || ph >= 3;
                bar.Visible = ph == 3 || ph == 4 || ph == 1;
                bar.SetIndeterminate(ph == 1);
                DoLayout();
                bMain.Invalidate(); bOther.Invalidate();
            }
            if (ph == 3 || ph == 4) bar.Value = page.RecvProgress;
            // arrived: it stays a little while, then goes by itself (not while the mouse is on it)
            if (ph == 5) { doneTicks++; if (doneTicks > 48 && !Bounds.Contains(Cursor.Position)) { Close(); return; } }
            Invalidate();
        }

        string MainText() { return L.T(phase == 2 ? "ft_pop_accept" : phase == 3 ? "ft_pause" : phase == 4 ? "ft_resume_btn" : "ft_recent_show"); }
        string OtherText() { return L.T(phase == 2 ? "ft_decline" : "ft_card_open"); }
        void MainClick()
        {
            if (phase == 2) page.AcceptOffer();
            else if (phase == 3 || phase == 4) page.TogglePause();
            else if (phase == 5) { page.OpenReceived(); Close(); }
        }
        void OtherClick()
        {
            if (phase == 2) { page.DeclineOffer(); Close(); }
            else { page.Attention(); Close(); }
        }

        void DoLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height, p = Theme.S(16);
            bClose.Bounds = Theme.Mirror(new Rectangle(W - Theme.S(8) - Theme.S(26), Theme.S(8), Theme.S(26), Theme.S(26)), W);
            bar.Bounds = Theme.Mirror(new Rectangle(p, Theme.S(96), W - p * 2, Theme.S(6)), W);
            int by = H - p - Theme.S(36);
            int mw = Math.Max(Theme.S(110), bMain.PreferredWidth + Theme.S(14)), ow = Math.Max(Theme.S(96), bOther.PreferredWidth + Theme.S(12));
            int x = W - p;
            if (showMain) { x -= mw; bMain.Bounds = Theme.Mirror(new Rectangle(x, by, mw, Theme.S(36)), W); x -= Theme.S(8); }
            bOther.Bounds = Theme.Mirror(new Rectangle(x - ow, by, ow, Theme.S(36)), W);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width, p = Theme.S(16);
            Theme.Smooth(g);
            using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);
            using (Pen pn = new Pen(Theme.CardBorder)) g.DrawPath(pn, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));
            Rectangle art = new Rectangle(p, p, Theme.S(64), Theme.S(64));
            FtArt.Draw(g, Theme.Mirror(art, W), null, page.RecvThumb, page.RecvName, page.RecvIsFolder, page.RecvTops, null);
            int tx = art.Right + Theme.S(14), tw = W - tx - Theme.S(40);
            string who = page.RecvWho;
            string title = phase == 1 ? (who != null ? L.T("ft_lan_connecting", who) : L.T("ft_connecting")) :
                           phase == 2 ? (who != null ? L.T("ft_offer_from", who) : L.T("ft_offer_t")) :
                           phase == 3 ? (who != null ? L.T("ft_pop_receiving", who) : L.T("ft_receiving")) :
                           phase == 4 ? L.T("ft_paused_t") :
                           phase == 5 ? (who != null ? L.T("ft_pop_received", who) : L.T("ft_got_t")) : L.T("ft_failed");
            Theme.Text(g, title, Theme.F(8.75f, FontStyle.Bold), phase == 5 ? Theme.Success : phase == 6 ? Theme.Error : Theme.InkSoft, Theme.Mirror(new Rectangle(tx, p - Theme.S(2), tw, Theme.S(20)), W), Align.Start);
            Theme.Text(g, page.RecvName, Theme.F(10.5f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(tx, p + Theme.S(18), W - tx - p, Theme.S(24)), W), Align.Start);
            string err = page.RecvErrorText;
            string sub = err != null ? err : phase == 3 || phase == 4 ? page.RecvStats : page.RecvSizeText;
            Theme.Text(g, sub, Theme.F(8.5f), err != null ? Theme.Error : Theme.InkSoft, Theme.Mirror(new Rectangle(tx, p + Theme.S(42), W - tx - p, err != null ? Theme.S(36) : Theme.S(20)), W), Align.Start, err == null, err != null);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop(); timer.Dispose();
            page.RecvChanged -= OnChanged;
            if (open == this) open = null;
            base.OnFormClosed(e);
        }
    }

    // ====================================================================== File Transfer's settings

    /// The gear on the File Transfer page: your name, where received files go, how it connects (and the port), the
    /// home network switch, Windows Firewall, and the two small servers with where they are.
    public class FtSettingsWindow : MiniWindow
    {
        readonly TransferPage page;
        readonly TextField nameBox, portBox;
        readonly Segments mode;
        readonly Toggle forwarded = new Toggle(), lan = new Toggle();
        readonly Btn bDest, bFirewall, bSrv, bPipe;
        readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1000 };
        Rectangle nameL, destL, destV, howL, modeD, portL, fwdR, lanR, fwL, fwV, srvHead, srvR, pipeR;
        int[] lines = new int[0];
        bool fwAllowed, fwKnown;
        static FtSettingsWindow open;

        public static void Open(TransferPage page, Point at)
        {
            if (open != null && !open.IsDisposed) { try { open.Activate(); } catch { } return; }
            open = new FtSettingsWindow(page);
            open.ShowNear(at);
        }

        FtSettingsWindow(TransferPage page) : base(() => L.T("ft_settings"), Ico.Settings)
        {
            this.page = page;
            nameBox = new TextField(() => Environment.UserName, null) { Surface = Surface.Card };
            nameBox.Box.MaxLength = 40;
            nameBox.Box.Text = Settings.Get("TransferName", "");
            nameBox.Box.TextChanged += (s, e) => { FtIdentity.Name = nameBox.Box.Text; };
            bDest = new Btn(() => L.T("ft_change"), Ico.FolderOpen, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bDest.Click += (s, e) => { string d = FolderPicker.Pick(this, L.T("ft_set_dest"), page.Dest); if (d != null) { page.SetDest(d); Invalidate(); } };
            mode = new Segments(() => L.T("ft_mode_auto"), () => L.T("ft_mode_host"), () => L.T("ft_mode_engine")) { FontSize = 8.75f, Surface = Surface.Card };
            mode.SetSilently(FtSettings.Mode == "host" ? 1 : FtSettings.Mode == "engine" ? 2 : 0);
            mode.SelectedChanged += (s, e) => { FtSettings.Mode = new[] { "auto", "host", "engine" }[mode.SelectedIndex]; Invalidate(); };
            portBox = new TextField(() => "47821", null) { Surface = Surface.Card, ForceLtr = true };
            portBox.Box.Text = FtSettings.Port.ToString(CultureInfo.InvariantCulture);
            portBox.Box.TextChanged += (s, e) => { int p; if (int.TryParse(portBox.Box.Text.Trim(), out p) && p >= 1024 && p <= 65535) FtSettings.Port = p; };
            forwarded.Surface = Surface.Card; forwarded.SetSilently(FtSettings.Forwarded);
            forwarded.CheckedChanged += (s, e) => FtSettings.Forwarded = forwarded.Checked;
            lan.Surface = Surface.Card; lan.SetSilently(FtLan.On);
            lan.CheckedChanged += (s, e) => FtLan.On = lan.Checked;
            bFirewall = new Btn(() => L.T("ft_fw_allow"), Ico.Check, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f, Visible = false };
            bFirewall.Click += (s, e) => { bFirewall.Enabled = false; page.AllowFirewall(() => { if (!IsDisposed) { bFirewall.Enabled = true; AskFirewall(); } }); };
            bSrv = new Btn(() => L.T(FtEngine.Ready ? "ft_set_folder" : "ft_set_get"), FtEngine.Ready ? Ico.FolderOpen : Ico.Download, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bSrv.Click += (s, e) => { if (FtEngine.Ready) AddOns.OpenFolder(Path.GetDirectoryName(FtEngine.Exe)); else page.InstallEngine(null); };
            bPipe = new Btn(() => L.T(FtPipe.Ready ? "ft_set_folder" : "ft_set_get"), FtPipe.Ready ? Ico.FolderOpen : Ico.Download, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bPipe.Click += (s, e) => { if (FtPipe.Ready) AddOns.OpenFolder(Path.GetDirectoryName(FtPipe.Exe)); else page.AskInstallPipe(); };
            Controls.AddRange(new Control[] { nameBox, bDest, mode, portBox, forwarded, lan, bFirewall, bSrv, bPipe });
            ClientSize = new Size(Theme.S(600), Theme.S(668));
            refresh.Tick += (s, e) =>
            {
                // a download finished (or a server was removed in Settings → Add-ons)
                bSrv.Glyph = FtEngine.Ready ? Ico.FolderOpen : Ico.Download; bPipe.Glyph = FtPipe.Ready ? Ico.FolderOpen : Ico.Download;
                DoLayout(); bSrv.Invalidate(); bPipe.Invalidate(); Invalidate();
            };
            refresh.Start();
            AskFirewall();
        }

        /// Whether the firewall rule is there (asking Windows takes a moment).
        void AskFirewall()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) { fwKnown = true; fwAllowed = false; return; }
            new Thread(() => { bool a = FtFirewall.Allowed; Ui(() => { fwKnown = true; fwAllowed = a; bFirewall.Visible = !a; DoLayout(); Invalidate(); }); }) { IsBackground = true }.Start();
        }

        protected override void DoLayout()
        {
            base.DoLayout();
            if (mode == null) return;
            int W = ClientSize.Width, p = Pad, w = W - p * 2, lw = Theme.S(190);
            int y = Head + Theme.S(4);
            List<int> ln = new List<int>();
            // your name
            nameL = new Rectangle(p, y, lw, Theme.S(44));
            nameBox.Bounds = M(new Rectangle(p + lw, y + Theme.S(4), w - lw, Theme.S(36)));
            y += Theme.S(56);
            // where received files go
            int cw = Math.Max(Theme.S(110), bDest.PreferredWidth + Theme.S(10));
            destL = new Rectangle(p, y, lw, Theme.S(44));
            destV = new Rectangle(p + lw, y, w - lw - cw - Theme.S(10), Theme.S(44));
            bDest.Bounds = M(new Rectangle(p + w - cw, y + Theme.S(4), cw, Theme.S(36)));
            y += Theme.S(56); ln.Add(y); y += Theme.S(14);
            // how it connects
            howL = new Rectangle(p, y, lw, Theme.S(36));
            mode.Bounds = M(new Rectangle(p + lw, y, w - lw, Theme.S(36)));
            modeD = new Rectangle(p, y + Theme.S(42), w, Theme.S(38));
            y += Theme.S(88);
            portL = new Rectangle(p, y, lw, Theme.S(36));
            portBox.Bounds = M(new Rectangle(p + lw, y, Theme.S(110), Theme.S(36)));
            y += Theme.S(46);
            forwarded.Bounds = M(new Rectangle(p, y + Theme.S(4), Theme.S(48), Theme.S(30)));
            fwdR = new Rectangle(p + Theme.S(60), y, w - Theme.S(60), Theme.S(40));
            y += Theme.S(50); ln.Add(y); y += Theme.S(14);
            // the home network, and the firewall
            lan.Bounds = M(new Rectangle(p, y + Theme.S(4), Theme.S(48), Theme.S(30)));
            lanR = new Rectangle(p + Theme.S(60), y, w - Theme.S(60), Theme.S(40));
            y += Theme.S(50);
            int fw = Math.Max(Theme.S(220), bFirewall.PreferredWidth + Theme.S(14));
            fwL = new Rectangle(p, y, lw, Theme.S(40));
            fwV = new Rectangle(p + lw, y, w - lw, Theme.S(40));
            bFirewall.Bounds = M(new Rectangle(p + w - fw, y + Theme.S(2), fw, Theme.S(36)));
            y += Theme.S(52); ln.Add(y); y += Theme.S(14);
            // the servers
            srvHead = new Rectangle(p, y, w, Theme.S(22));
            y += Theme.S(28);
            int sbw = Math.Max(Theme.S(130), Math.Max(bSrv.PreferredWidth, bPipe.PreferredWidth) + Theme.S(12));
            srvR = new Rectangle(p, y, w - sbw - Theme.S(10), Theme.S(46));
            bSrv.Bounds = M(new Rectangle(p + w - sbw, y + Theme.S(5), sbw, Theme.S(36)));
            y += Theme.S(54);
            pipeR = new Rectangle(p, y, w - sbw - Theme.S(10), Theme.S(46));
            bPipe.Bounds = M(new Rectangle(p + w - sbw, y + Theme.S(5), sbw, Theme.S(36)));
            lines = ln.ToArray();
        }

        static string Shown(string path) { return Theme.RTL ? "‪" + path + "‬" : path; }

        protected override void PaintBody(Graphics g)
        {
            int W = ClientSize.Width, p = Pad;
            Font lab = Theme.F(9.25f, FontStyle.Bold), txt = Theme.F(8.75f), small = Theme.F(8.25f);
            using (Pen pn = new Pen(Theme.Line)) foreach (int ly in lines) g.DrawLine(pn, p, ly, W - p, ly);
            Theme.Text(g, L.T("ft_set_name"), lab, Theme.Ink, M(new Rectangle(nameL.X, nameL.Y + Theme.S(2), nameL.Width, Theme.S(22))), Align.Start);
            Theme.Text(g, L.T("ft_set_name_d"), small, Theme.InkFaint, M(new Rectangle(nameL.X, nameL.Y + Theme.S(22), nameL.Width, Theme.S(20))), Align.Start);
            Theme.Text(g, L.T("ft_set_dest"), lab, Theme.Ink, M(destL), Align.Start);
            Theme.Text(g, Shown(page.Dest), txt, Theme.InkSoft, M(destV), Align.Start, true, false, false);
            Theme.Text(g, L.T("ft_how"), lab, Theme.Ink, M(howL), Align.Start);
            Theme.Text(g, L.T(mode.SelectedIndex == 1 ? "ft_mode_host_d" : mode.SelectedIndex == 2 ? "ft_mode_engine_d" : "ft_mode_auto_d"), txt, Theme.InkSoft, M(modeD), Align.Start, false, true);
            Theme.Text(g, L.T("ft_port"), lab, Theme.Ink, M(portL), Align.Start);
            Theme.Text(g, L.T("ft_forwarded"), txt, Theme.Ink, M(fwdR), Align.Start, true, true);
            Theme.Text(g, L.T("ft_set_lan"), txt, Theme.Ink, M(lanR), Align.Start, true, true);
            Theme.Text(g, L.T("ft_set_fw"), lab, Theme.Ink, M(fwL), Align.Start);
            if (fwKnown && fwAllowed) Theme.Text(g, "✓ " + L.T("ft_fw_ok_short"), txt, Theme.Success, M(fwV), Align.Start);
            else if (fwKnown && Environment.OSVersion.Platform != PlatformID.Win32NT) Theme.Text(g, "–", txt, Theme.InkFaint, M(fwV), Align.Start);
            Theme.Text(g, L.T("ft_set_servers").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, M(srvHead), Align.Start);
            PaintServer(g, srvR, L.T("ft_engine_name"), FtEngine.Ready ? Shown(Path.GetDirectoryName(FtEngine.Exe)) : L.T("ft_set_srv_d"));
            PaintServer(g, pipeR, L.T("ft_pipe_name"), FtPipe.Ready ? Shown(Path.GetDirectoryName(FtPipe.Exe)) : L.T("ft_set_pipe_d"));
        }

        void PaintServer(Graphics g, Rectangle r, string name, string sub)
        {
            Theme.Text(g, name, Theme.F(9.25f, FontStyle.Bold), Theme.Ink, M(new Rectangle(r.X, r.Y + Theme.S(3), r.Width, Theme.S(22))), Align.Start);
            Theme.Text(g, sub, Theme.F(8.25f), Theme.InkSoft, M(new Rectangle(r.X, r.Y + Theme.S(24), r.Width, Theme.S(20))), Align.Start);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { refresh.Stop(); refresh.Dispose(); if (open == this) open = null; base.OnFormClosed(e); }
    }
}
