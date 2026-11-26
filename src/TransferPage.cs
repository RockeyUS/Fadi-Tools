using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Someone files can be sent to: a friend (wherever they are) or a PC on the network.
    public class FtTarget
    {
        public string Id, Name;
        public FtTrust.Entry Friend;
        public FtPeer Peer;
        /// While sending: "asking" (reaching their PC), "waiting" (it's showing there), "off", "busy", "no".
        public string State;
    }

    /// File Transfer: send files and folders of any size straight to a friend's PC across the internet, or receive
    /// them. Friends (and PCs on the network) sit in a panel on the right: drop files on one and they go to it.
    /// Anyone else gets a code to paste. Connection settings are behind the gear.
    public class TransferPage : Page
    {
        readonly Segments tabs;
        readonly DropZone drop;
        readonly Btn bGear, bCopy, bCopyMsg, bStop;
        // a friend picked in the panel: the middle becomes theirs (drop, paste or choose files and they go to them)
        readonly Btn bToPaste, bToFiles, bToFolder, bToBack;
        string selId;
        bool dragMain;
        Rectangle selAvatar, selName, selSub, selHint;
        readonly Btn bPaste, bConnect, bAccept, bDecline, bChange, bOpen, bAgain, bPause, bRecvStop, bRecvDelete, bDeletePart;
        readonly Toggle remember = new Toggle();
        readonly TextField codeBox;
        readonly Progress bar = new Progress();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 500 };
        readonly System.Windows.Forms.Timer onlineTimer = new System.Windows.Forms.Timer { Interval = 60000 };
        /// The page wants to be seen (the pop-up's "Open Fadi Tools"): the window shows it.
        public event Action WantsAttention;

        // ---- the friends panel
        class Row { public FtTrust.Entry Friend; public FtPeer Peer; public Rectangle R; public string Id, Name; }
        readonly List<Row> rows = new List<Row>();
        List<FtPeer> peers = new List<FtPeer>();
        List<FtTrust.Entry> trusted = new List<FtTrust.Entry>();
        readonly Dictionary<string, bool> online = new Dictionary<string, bool>();   // friends asked "are you there?"
        Rectangle panel, lanHeadR;
        int hotRow = -1, dragRow = -1, panelScroll, panelContent;
        bool hotMore, pinging;
        Row pickFor;

        // ---- recent transfers
        List<FtHistory.Entry> recent = new List<FtHistory.Entry>();
        Rectangle recentCard, recentClearR;
        readonly List<Rectangle> recentR = new List<Rectangle>(), recentShowR = new List<Rectangle>(), recentAgainR = new List<Rectangle>();
        int hotRecent = -1, hotRecentBtn;   // 1 = show in folder, 2 = send again, 3 = clear

        // ---- sending
        readonly List<string> items = new List<string>();
        long itemsSize; int itemsCount;
        enum SendState { Pick, Preparing, Waiting, Sending, Sent, Failed }
        SendState send = SendState.Pick;
        List<FtTarget> targets = new List<FtTarget>();
        bool wantCode, sentLogged;
        FtHost host;
        FtCode codeObj;
        FtEngine.Sender engineSend;
        Upnp.Gateway gateway; int mappedPort;
        string code, sendNote, sendError;
        bool canHost, viaEngine, copyWhenReady;
        List<string> checks = new List<string>();   // "ok|text" / "warn|text" / "bad|text"
        SendCard card0;

        // ---- receiving
        enum RecvState { Enter, Connecting, Offer, Receiving, Received, Failed }
        RecvState recv = RecvState.Enter;
        FtCode inCode;
        FtReceiver receiver;
        bool recvEngine, recvCancel, recvPaused, deleteAfter;
        string dest, recvError, recvRoute, incomingFrom, engineInto;
        long recvDone, recvTotal, recvRemaining, slowLink;
        Image recvThumb;
        public event Action RecvChanged;

        // ---- speed
        readonly Queue<KeyValuePair<DateTime, long>> samples = new Queue<KeyValuePair<DateTime, long>>();
        double speed;
        // each transfer is also a row in Activity (with its notification when it ends)
        ActJob sendAct, recvAct;

        Rectangle card, artR, checksR, codeR, statsR, targetsR, offerR, destR, helpR, rememberR, noteR;

        public int ActiveCount { get { return (send == SendState.Waiting || send == SendState.Sending || send == SendState.Preparing ? 1 : 0) + (recv == RecvState.Receiving || recv == RecvState.Connecting ? 1 : 0); } }
        public override bool Busy { get { return false; } }

        public TransferPage() : base("ft_title", "ft_sub")
        {
            tabs = new Segments(() => L.T("ft_tab_send"), () => L.T("ft_tab_receive")) { Glyphs = new[] { Ico.Send, Ico.Download } };
            tabs.SelectedChanged += (s, e) => { ShowState(); };
            drop = new DropZone(() => L.T("ft_drop"), () => L.T("ft_drop_sub2")) { Glyph = Ico.Send };
            drop.Click += (s, e) => BrowseFiles();
            drop.ExtrasTitle = () => L.T("ft_or");
            drop.Extras = () => new List<DropExtra> { new DropExtra { Text = L.T("ft_add_folder"), Glyph = Ico.FolderOpen, Run = BrowseFolder } };
            bGear = new Btn(() => "", Ico.Settings, BtnKind.Secondary) { GlyphSize = 11f, AccessibleName = L.T("ft_settings") };
            bGear.Click += (s, e) => FtSettingsWindow.Open(this, bGear.PointToScreen(new Point(bGear.Width / 2, bGear.Height + Theme.S(280))));
            bToPaste = new Btn(() => L.T("ft_paste"), Ico.Paste, BtnKind.Secondary) { Surface = Surface.Card };
            bToPaste.Click += (s, e) => PasteTo();
            bToFiles = new Btn(() => L.T("ft_sel_files"), Ico.Page, BtnKind.Secondary) { Surface = Surface.Card };
            bToFiles.Click += (s, e) => { pickFor = Sel; BrowseFiles(); pickFor = null; };
            bToFolder = new Btn(() => L.T("ft_sel_folder"), Ico.FolderOpen, BtnKind.Secondary) { Surface = Surface.Card };
            bToFolder.Click += (s, e) => { pickFor = Sel; BrowseFolder(); pickFor = null; };
            bToBack = new Btn(() => L.T("ft_sel_back"), Theme.RTL ? Ico.Forward : Ico.Back, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 9f };
            bToBack.Click += (s, e) => { selId = null; ShowState(); };
            bCopy = new Btn(() => L.T("ft_copy"), Ico.Copy, BtnKind.Primary);
            bCopy.Click += (s, e) => CopyText(code, "ft_copied");
            bCopyMsg = new Btn(() => L.T("ft_copy_msg"), Ico.Text, BtnKind.Secondary) { FontSize = 9f };
            bCopyMsg.Click += (s, e) => CopyText(L.T("ft_msg", SendName(), Theme.HumanSize(itemsSize), code), "ft_copied");
            bStop = new Btn(() => L.T(send == SendState.Sent || send == SendState.Failed ? "ft_send_more" : host != null && host.DoneCount > 0 ? "ft_finish_send" : "ft_stop_send"), null, BtnKind.Secondary) { FontSize = 9f };
            bStop.Click += (s, e) => { StopSending(); ShowState(); };

            codeBox = new TextField(() => L.T("ft_code_hint"), null) { Surface = Surface.Card, ForceLtr = true };
            codeBox.Box.TextChanged += (s, e) => { bConnect.Enabled = FtCode.Parse(codeBox.Box.Text) != null; Invalidate(); };
            codeBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && bConnect.Enabled) { e.SuppressKeyPress = true; Connect(); } };
            bPaste = new Btn(() => L.T("ft_paste"), Ico.Paste, BtnKind.Secondary) { FontSize = 9f, Surface = Surface.Card };
            bPaste.Click += (s, e) => { try { if (Clipboard.ContainsText()) codeBox.Box.Text = Clipboard.GetText().Trim(); } catch { } };
            bConnect = new Btn(() => L.T("ft_connect"), Ico.Link, BtnKind.Primary) { Enabled = false };
            bConnect.Click += (s, e) => Connect();
            bAccept = new Btn(() => L.T("ft_accept"), Ico.Download, BtnKind.Primary);
            bAccept.Click += (s, e) => Accept();
            bDecline = new Btn(() => L.T("ft_decline"), null, BtnKind.Secondary);
            bDecline.Click += (s, e) => { ResetReceive(); };
            bChange = new Btn(() => L.T("ft_change"), Ico.FolderOpen, BtnKind.Ghost) { FontSize = 9f, Surface = Surface.Card };
            bChange.Click += (s, e) => { string d = FolderPicker.Pick(FindForm(), L.T("ft_save_to"), dest); if (d != null) { SetDest(d); } };
            bOpen = new Btn(() => L.T("ft_open_folder"), Ico.FolderOpen, BtnKind.Primary);
            bOpen.Click += (s, e) => OpenReceived();
            bAgain = new Btn(() => L.T("ft_receive_more"), null, BtnKind.Secondary);
            bAgain.Click += (s, e) => ResetReceive();
            // hold it where it is, and carry on later from the same place
            bPause = new Btn(() => L.T(recvPaused ? "ft_resume_btn" : "ft_pause"), Ico.Pause, BtnKind.Primary);
            bPause.Click += (s, e) => TogglePause();
            bRecvStop = new Btn(() => L.T("ft_stop_recv"), null, BtnKind.Secondary) { FontSize = 9f };
            bRecvStop.Click += (s, e) => { recvCancel = true; if (receiver != null) receiver.Paused = false; if (recvPaused && recvEngine) { recvPaused = false; recv = RecvState.Offer; Plan(); ShowState(); } };
            // stop, and remove what arrived (a friend's idea): nothing is left behind in the folder
            bRecvDelete = new Btn(() => L.T("ft_cancel_delete"), Ico.Delete, BtnKind.Danger) { FontSize = 9f };
            bRecvDelete.Click += (s, e) =>
            {
                if (!Ask.Confirm(FindForm(), L.T("ft_cancel_delete_q_t"), L.T("ft_cancel_delete_q"), L.T("ft_cancel_delete"), L.T("cancel"), AskKind.Danger)) return;
                if (recvPaused && recvEngine) { recvPaused = false; DeleteArrived(); ResetReceive(); ShowToast(L.T("ft_deleted"), true, null, null); return; }
                deleteAfter = true; recvCancel = true; if (receiver != null) receiver.Paused = false;
            };
            bDeletePart = new Btn(() => L.T("ft_delete_part"), Ico.Delete, BtnKind.Ghost) { FontSize = 9f, Surface = Surface.Card, InkColor = Theme.Error };
            bDeletePart.Click += (s, e) => { DeleteArrived(); Plan(); ShowState(); };
            remember.Surface = Surface.Card;
            remember.SetSilently(true);
            FtLan.PeersChanged += () => Post(() => { peers = FtLan.Peers; BuildRows(); ShowState(); });
            FtLan.OfferReceived = OnOffer;
            FtTrust.Changed += () => Post(() => { trusted = FtTrust.All(); BuildRows(); ShowState(); });
            FtHistory.Changed += () => Post(() => { recent = FtHistory.All(); DoLayout(); Invalidate(); });
            trusted = FtTrust.All();
            recent = FtHistory.All();
            BuildRows();

            Controls.AddRange(new Control[] { tabs, bGear, drop, bToPaste, bToFiles, bToFolder, bToBack, bCopy, bCopyMsg, bStop, codeBox, bPaste, bConnect, bAccept, bDecline, bChange, bOpen, bAgain, bPause, bRecvStop, bRecvDelete, bDeletePart, remember, bar });
            HookDrop(this); HookDrop(drop);
            dest = Settings.Get("TransferDest", "");
            if (dest.Length == 0 || !Directory.Exists(dest)) dest = Paths.Downloads;
            tick.Tick += (s, e) => Tick();
            onlineTimer.Tick += (s, e) => { if (Visible && Owner != null && Owner.Visible) RefreshOnline(); };
            // the app closing: the router's door is shut and the servers stopped
            Application.ApplicationExit += (s, e) => { try { StopSending(); } catch { } recvCancel = true; FtPipe.Shutdown(); };
            ShowState();
        }

        public override void OnShown() { ShowState(); RefreshOnline(); onlineTimer.Start(); }

        /// Where received files go (File Transfer's settings, or "Change" on an offer).
        public string Dest { get { return dest; } }
        public void SetDest(string d) { dest = d; Settings.Set("TransferDest", d); if (recv == RecvState.Offer) Plan(); Invalidate(); }

        // ================================================================== drag and drop

        void HookDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += (s, e) => DragAt(e);
            c.DragOver += (s, e) => DragAt(e);
            c.DragLeave += (s, e) => DragEnd();
            c.DragDrop += (s, e) =>
            {
                int row = dragRow;
                DragEnd();
                if (DragOut.Active) return;
                string[] p = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (p == null || p.Length == 0) return;
                // dropped on a friend (their row, or the middle when they're picked): it goes to them at once;
                // anywhere else: the card asks who it's for
                Row sel = Sel;
                if (row >= 0 && row < rows.Count) SendTo(rows[row], p);
                else if (sel != null && tabs.SelectedIndex == 0) SendTo(sel, p);
                else OfferFiles(p, Cursor.Position);
            };
        }

        void DragAt(DragEventArgs e)
        {
            bool ok = !DragOut.Active && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (!ok) return;
            int r = HitRow(PointToClient(new Point(e.X, e.Y)));
            bool zone = r < 0;
            if (r != dragRow || drop.DragHot != zone || dragMain != zone) { dragRow = r; drop.DragHot = zone; dragMain = zone; drop.Invalidate(); Invalidate(); }
        }
        void DragEnd() { if (dragRow >= 0 || drop.DragHot || dragMain) { dragRow = -1; drop.DragHot = false; dragMain = false; drop.Invalidate(); Invalidate(); } }

        /// Files dropped on the window's edge or the sidebar while this page shows.
        /// (Ctrl+V lands here too.) With a friend picked, they go to that friend.
        public override void AcceptFiles(string[] paths)
        {
            Row sel = Sel;
            if (sel != null && tabs.SelectedIndex == 0) SendTo(sel, paths); else OfferFiles(paths, Cursor.Position);
        }

        /// The friend (or PC) picked in the panel, while it's still there.
        Row Sel { get { if (selId == null) return null; foreach (Row r in rows) if (r.Id == selId) return r; return null; } }

        /// Paste: copied files, or a copied picture (saved as a PNG first), go to the picked friend.
        void PasteTo()
        {
            Row sel = Sel;
            if (sel == null) return;
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    System.Collections.Specialized.StringCollection sc = Clipboard.GetFileDropList();
                    string[] a = new string[sc.Count]; sc.CopyTo(a, 0);
                    SendTo(sel, a);
                    return;
                }
                if (Clipboard.ContainsImage())
                {
                    using (Image img = Clipboard.GetImage())
                    {
                        if (img != null)
                        {
                            string dir = Path.Combine(Path.GetTempPath(), "FadiTools");
                            Directory.CreateDirectory(dir);
                            string path = Path.Combine(dir, L.T("pasted_image") + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + ".png");
                            img.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                            SendTo(sel, new[] { path });
                            return;
                        }
                    }
                }
            }
            catch { }
            ShowToast(L.T("ft_sel_nothing"), false, null, null);
        }

        // ================================================================== choosing what to send, and to whom

        void BrowseFiles()
        {
            using (OpenFileDialog d = new OpenFileDialog { Multiselect = true, Title = L.T("ft_add_files"), Filter = L.T("all_files") + " (*.*)|*.*" })
                if (d.ShowDialog(FindForm()) == DialogResult.OK) Picked(d.FileNames);
        }
        void BrowseFolder()
        {
            string d = FolderPicker.Pick(FindForm(), L.T("ft_add_folder"), null);
            if (d != null) Picked(new[] { d });
        }
        void Picked(string[] paths)
        {
            Row to = pickFor; pickFor = null;
            if (to != null) SendTo(to, paths); else OfferFiles(paths, CentreOnScreen());
        }
        Point CentreOnScreen() { try { return PointToScreen(new Point(Width / 2, Height / 2)); } catch { return Cursor.Position; } }

        /// Everyone files can go to right now: friends who can be reached, and PCs on the network.
        public List<FtTarget> Choices()
        {
            List<FtTarget> l = new List<FtTarget>();
            foreach (Row r in rows)
            {
                if (r.Friend != null && r.Peer == null && string.IsNullOrEmpty(r.Friend.Ticket)) continue;   // reachable only with a code
                l.Add(new FtTarget { Id = r.Id, Name = r.Name, Friend = r.Friend, Peer = r.Peer });
            }
            return l;
        }
        /// Null = not known yet.
        public bool? OnlineOf(FtTarget t)
        {
            if (t.Peer != null) return true;
            bool on;
            return online.TryGetValue(t.Id, out on) ? (bool?)on : null;
        }
        public string SubFor(FtTarget t)
        {
            if (t.Peer != null) return L.T("ft_panel_lan");
            bool? on = OnlineOf(t);
            if (on == true) return L.T("ft_row_online");
            return (on == false ? L.T("ft_row_offline") + (Theme.RTL ? "  ‏·‏  " : "  ·  ") : "") + LastWith(t.Friend);
        }

        /// A transfer that's over (sent, or failed) makes room for the next one.
        void ClearFinished() { if (send == SendState.Sent || send == SendState.Failed) { StopSending(); ShowState(); } }

        /// The card that asks who the files are for (or says a transfer is already going).
        public void OfferFiles(string[] paths, Point at)
        {
            ClearFinished();
            List<string> ok = new List<string>();
            foreach (string p in paths) if ((File.Exists(p) || Directory.Exists(p)) && !ok.Contains(p)) ok.Add(p);
            if (ok.Count == 0) return;
            if (send != SendState.Pick) { ShowToast(L.T("ft_busy_sending"), false, null, null); if (card0 != null && !card0.IsDisposed) try { card0.Activate(); } catch { } return; }
            if (card0 != null && !card0.IsDisposed && card0.Choosing) { card0.AddItems(ok); try { card0.Activate(); } catch { } return; }
            card0 = SendCard.Choose(this, ok, at);
        }

        System.Windows.Forms.Timer outside;
        readonly List<string> outsideItems = new List<string>();
        Point outsideAt;
        /// Files sent from File Explorer's right-click menu or the Donut Wheel: the card pops up where the mouse is
        /// (Explorer hands them over one by one, so it waits a moment for the rest).
        public void SendFromOutside(string[] paths)
        {
            ClearFinished();
            if (send != SendState.Pick) { ShowToast(L.T("ft_busy_sending"), false, null, null); Notify(L.T("ft_title"), L.T("ft_busy_sending")); return; }
            if (outsideItems.Count == 0) outsideAt = Cursor.Position;
            foreach (string p in paths) if (!outsideItems.Contains(p)) outsideItems.Add(p);
            if (outside == null)
            {
                outside = new System.Windows.Forms.Timer { Interval = 700 };
                outside.Tick += (s, e) => { outside.Stop(); string[] all = outsideItems.ToArray(); outsideItems.Clear(); OfferFiles(all, outsideAt); };
            }
            outside.Stop(); outside.Start();
        }

        void Notify(string title, string text)
        {
            try { if (Owner != null && (!Owner.Visible || Form.ActiveForm != Owner)) Notifier.Show(Owner, title, text, true, () => { if (WantsAttention != null) WantsAttention(); }); } catch { }
        }

        /// Straight to one friend or PC (a drop on their row, or a click and then choosing files).
        void SendTo(Row r, string[] paths)
        {
            bool needCode = r.Friend != null && r.Peer == null && string.IsNullOrEmpty(r.Friend.Ticket);
            List<string> ok = new List<string>();
            foreach (string p in paths) if ((File.Exists(p) || Directory.Exists(p)) && !ok.Contains(p)) ok.Add(p);
            if (ok.Count == 0) return;
            ClearFinished();
            if (send != SendState.Pick) { ShowToast(L.T("ft_busy_sending"), false, null, null); return; }
            List<FtTarget> to = new List<FtTarget>();
            // (a friend who can't be reached without a code yet: a code to send them, this once more)
            if (!needCode) to.Add(new FtTarget { Id = r.Id, Name = r.Name, Friend = r.Friend, Peer = r.Peer });
            // a card still asking who it's for gives way: this drop said who
            if (card0 != null && !card0.IsDisposed && card0.Choosing) { try { card0.Close(); } catch { } card0 = null; }
            if (BeginSend(ok, to, needCode))
            {
                card0 = SendCard.Watch(this, Cursor.Position);
                if (needCode) ShowToast(L.T("ft_friend_no_ticket", r.Name), true, null, null);
            }
        }

        /// Starts sending. False when it didn't start (something's already going, or a download was declined).
        public bool BeginSend(List<string> paths, List<FtTarget> to, bool withCode)
        {
            if (send != SendState.Pick || paths.Count == 0) return false;
            // friends away from this network are reached through the friends server: asked before it's downloaded
            bool needPipe = false;
            foreach (FtTarget t in to) if (t.Friend != null && t.Peer == null) needPipe = true;
            if (needPipe && !FtPipe.Ready)
            {
                if (!Ask.Confirm(FindForm(), L.T("ft_pipe_q_t"), L.T("ft_pipe_q"), L.T("install"), L.T("not_now"), AskKind.Info)) return false;
                InstallPipe(() => { if (BeginSend(paths, to, withCode) && (card0 == null || card0.IsDisposed)) card0 = SendCard.Watch(this, CentreOnScreen()); });
                return false;
            }
            items.Clear(); items.AddRange(paths);
            targets = new List<FtTarget>(to);
            foreach (FtTarget t in targets) t.State = null;
            wantCode = withCode || targets.Count == 0;
            copyWhenReady = wantCode;
            sentLogged = false;
            Recount();
            StartSending();
            return send != SendState.Pick;
        }

        /// Counts what's chosen (folders take a moment, so it's done in the background).
        void Recount()
        {
            List<string> snap = new List<string>(items);
            itemsSize = -1; itemsCount = 0;
            new Thread(() =>
            {
                string n; List<FtFile> l = FtHost.Collect(snap, out n);
                long s = 0; foreach (FtFile f in l) s += f.Size;
                Post(() => { if (snap.Count == items.Count) { itemsSize = s; itemsCount = l.Count; Invalidate(); } });
            }) { IsBackground = true }.Start();
        }
        string SendName() { return items.Count == 1 ? Path.GetFileName(items[0].TrimEnd('\\', '/')) : L.T("ft_n_items", items.Count); }

        // ================================================================== what the send card shows

        public bool SendIdle { get { return send == SendState.Pick; } }
        public bool SendOver { get { return send == SendState.Sent; } }
        public bool SendFailed { get { return send == SendState.Failed; } }
        public string SendItemName { get { return SendName(); } }
        public string SendFirstPath { get { return items.Count > 0 ? items[0] : null; } }
        public int SendItemCount { get { return items.Count; } }
        public string SendSizeText { get { return itemsSize < 0 ? L.T("ft_counting") : FilesSize(itemsCount, itemsSize); } }
        /// "1 file · 2.4 MB", "214 files · 1.7 GB".
        public static string FilesSize(int count, long size)
        {
            return count == 1 ? L.T("ft_1_file_size", Ltr(Theme.HumanSize(size))) : L.T("ft_n_files_size", count, Ltr(Theme.HumanSize(size)));
        }
        public string SendCode { get { return ShowCode ? code : null; } }
        public double SendProgress { get { return send == SendState.Sent ? 1 : send == SendState.Sending && Whole > 0 ? Math.Min(1, Moved / (double)Whole) : -1; } }
        public string SendTitle
        {
            get
            {
                if (send == SendState.Preparing) return L.T("ft_getting_ready");
                if (send == SendState.Sent) return L.T(targets.Count > 1 ? "ft_sent_all" : "ft_sent_t");
                if (send == SendState.Failed) return L.T("ft_failed");
                if (host != null && host.DoneCount > 0) return L.T("ft_sent_some", host.DoneCount, Math.Max(host.DoneCount + 1, Expected()));
                if (send == SendState.Sending) return L.T("ft_sending");
                if (targets.Count == 1 && targets[0].State != "off" && targets[0].State != "no" && targets[0].State != "busy") return L.T("ft_lan_waiting", targets[0].Name);
                if (targets.Count > 1 && !ShowCode) return L.T("ft_waiting_many");
                return L.T("ft_waiting");
            }
        }
        /// The line under the title: what's happening, or what to do.
        public string SendDetail
        {
            get
            {
                if (sendError != null) return sendError;
                if (sendNote != null) return sendNote;
                if (send == SendState.Sent) return L.T("ft_sent_d", SendName());
                if (send == SendState.Sending) return Stats();
                if (send != SendState.Waiting) return "";
                if (ShowCode && targets.Count > 0 && !wantCode) return L.T("ft_code_instead");
                if (ShowCode) return L.T(viaEngine && host == null ? "ft_waiting_engine" : "ft_waiting_d");
                if (targets.Count == 1) return L.T(targets[0].Peer != null ? "ft_lan_waiting_d" : "ft_friend_waiting_d", targets[0].Name);
                return L.T("ft_waiting_many_d");
            }
        }
        public void CopyCode() { CopyText(code, "ft_copied"); }
        public void StopFromCard() { StopSending(); ShowState(); }
        public void Attention() { if (WantsAttention != null) WantsAttention(); }

        /// The code is there to share: it was asked for, or someone couldn't be reached.
        bool ShowCode
        {
            get
            {
                if (code == null || send == SendState.Sent) return false;
                if (wantCode) return true;
                foreach (FtTarget t in targets) if (t.State == "off" || t.State == "no" || t.State == "busy") return true;
                return false;
            }
        }
        /// How many PCs are expected to take the files.
        int Expected()
        {
            int n = 0;
            foreach (FtTarget t in targets) if (t.State == "waiting" || t.State == "asking") n++;
            return Math.Max(1, n);
        }

        /// Where one of the people it's going to has got to: "Reaching their PC…", "43%", "Received".
        public string TargetStatus(FtTarget t, out Color col)
        {
            col = Theme.InkSoft;
            FtHost.Recipient rc = null;
            if (host != null) foreach (FtHost.Recipient r in host.Recipients) if (r.Id == t.Id) rc = r;
            if (rc != null && rc.Done) { col = Theme.Success; return (Theme.RTL ? "\u200F" : "") + "\u2713 " + L.T("ft_t_done"); }
            if (rc != null && (rc.Has > 0 || rc.Conns > 0)) { col = Theme.AccentColor; return Ltr(((int)(100.0 * rc.Has / Math.Max(1, host.Total))).ToString(CultureInfo.InvariantCulture) + "%"); }
            switch (t.State)
            {
                case "asking": return L.T("ft_t_asking");
                case "waiting": return L.T("ft_t_waiting");
                case "busy": col = Theme.Warn; return L.T("ft_t_busy");
                case "off": col = Theme.Warn; return L.T("ft_t_off");
                case "no": col = Theme.Warn; return L.T("ft_t_no");
            }
            return "";
        }
        public List<FtTarget> SendTargets { get { return targets; } }

        // ================================================================== sending

        void StartSending()
        {
            if (items.Count == 0 || send != SendState.Pick) return;
            string m = FtSettings.Mode;
            // only PCs on this network, and no code: nothing outside the home network is needed
            bool lanOnly = !wantCode && targets.Count > 0;
            foreach (FtTarget t in targets) if (t.Peer == null) lanOnly = false;
            // the server is needed (or might be): asked before anything is downloaded
            if (!lanOnly && m == "engine" && !FtEngine.Ready) { InstallEngine(StartSending); return; }
            send = SendState.Preparing; sendError = null; code = null; codeObj = null; checks.Clear(); canHost = false; viaEngine = false;
            sendNote = L.T("ft_preparing");
            KeepAwake.Hold(true);
            tick.Start();
            ShowState();
            List<string> snap = new List<string>(items);
            byte[] thumb = FtArt.Small(snap[0]);
            new Thread(() => Prepare(snap, m, lanOnly, thumb)) { IsBackground = true, Name = "ft-prepare" }.Start();
        }

        void Check(string kind, string text) { Post(() => { checks.Add(kind + "|" + text); DoLayout(); Invalidate(); }); }

        void Prepare(List<string> snap, string m, bool lanOnly, byte[] thumb)
        {
            try
            {
                string name; List<FtFile> files = FtHost.Collect(snap, out name);
                if (files.Count == 0) throw new UserError(L.T("ft_nothing"));
                FtCode c = FtCode.New();
                FtHost h = null;
                if (m != "engine" || lanOnly)
                {
                    h = new FtHost(files, name);
                    h.Thumb = thumb ?? FtArt.Small(snap[0]);
                    c = h.Code;
                }
                c.Name = name; c.Count = files.Count;
                foreach (FtFile f in files) c.Size += f.Size;
                long link = Upnp.LinkSpeed();
                if (link >= 10000000L && link <= 100000000L) Check("warn", L.T("ft_chk_slow_link", link / 1000000, Math.Max(1, link / 8 / 1000000)));
                if (h != null)
                {
                    h.Start(FtSettings.Port);
                    h.Changed += () => Post(OnHostChanged);
                    FtHost h2 = h;
                    Post(() => host = h2);
                    if (lanOnly)
                    {
                        // handed to each PC with this PC's address on the network they share (in Deliver)
                        List<IPAddress> lan0 = new List<IPAddress>(), v60 = new List<IPAddress>();
                        Upnp.LocalAddresses(lan0, v60);
                        if (lan0.Count > 0) c.Hosts.Add(lan0[0] + ":" + h.Port);
                        string s0 = c.ToString();
                        Post(() => { if (send != SendState.Preparing) return; code = s0; codeObj = c; send = SendState.Waiting; sendNote = null; ShowState(); CodeReady(); });
                        return;
                    }
                    Post(() => sendNote = L.T("ft_asking_router"));
                    Upnp.Gateway g = Upnp.Find();
                    IPAddress ext = null; bool mapped = false;
                    if (g != null) { mapped = Upnp.Open(g, h.Port); ext = Upnp.External(g); if (mapped) Post(() => { gateway = g; mappedPort = h2.Port; }); }
                    List<IPAddress> lan = new List<IPAddress>(), v6 = new List<IPAddress>();
                    Upnp.LocalAddresses(lan, v6);
                    if (ext == null)
                    {
                        // the router didn't say: one small question to a public "what's my address" service
                        try { IPAddress ip; if (IPAddress.TryParse(Net.GetString("https://api.ipify.org").Trim(), out ip)) ext = ip; } catch { }
                    }
                    // only the addresses worth trying, so the code stays short: the internet one, one IPv6 (the one
                    // that lasts longest), and one on the home network (the one the router sees)
                    if (ext != null && !Upnp.Private(ext)) c.Hosts.Add(ext + ":" + h.Port);
                    if (v6.Count > 0) c.Hosts.Add("[" + v6[0] + "]:" + h.Port);
                    IPAddress home = g != null && g.Local != null ? g.Local : lan.Count > 0 ? lan[0] : null;
                    if (home != null) c.Hosts.Add(home + ":" + h.Port);
                    bool forwarded = FtSettings.Forwarded && ext != null && !Upnp.Private(ext);
                    bool ok = (mapped || forwarded) && ext != null && !Upnp.Private(ext);
                    Post(() => canHost = ok);
                    if (mapped && ok) Check("ok", L.T("ft_chk_host_ok", h.Port));
                    else if (ok) Check("ok", L.T("ft_chk_forwarded", h.Port));
                    else if (ext != null && Upnp.Private(ext)) Check("bad", L.T(Upnp.Shared(ext) ? "ft_chk_cgnat" : "ft_chk_double"));
                    else if (g == null) Check("warn", L.T("ft_chk_no_upnp", h.Port));
                    else Check("warn", L.T("ft_chk_refused", h.Port));
                    if (v6.Count > 0) Check("ok", L.T("ft_chk_v6"));
                    Check("ok", L.T("ft_chk_lan"));
                    if (FtFirewall.Allowed) Check("ok", L.T("ft_chk_fw_ok")); else Check("info", L.T("ft_chk_firewall"));
                    if (m == "host" && !ok) Check("warn", L.T("ft_chk_host_only"));
                    // the code can go out now when hosting will work (or hosting is all that was asked for)
                    if (m == "host" || ok)
                    {
                        string s = c.ToString();
                        Post(() => { if (send != SendState.Preparing) return; code = s; codeObj = c; send = SendState.Waiting; sendNote = null; ShowState(); CodeReady(); });
                        return;
                    }
                }
                // the server: for when hosting can't work
                if (!FtEngine.Ready)
                {
                    bool yes = false;
                    Invoke((Action)(() => yes = Ask.Confirm(FindForm(), L.T("ft_engine_q_t"), L.T("ft_engine_q"), L.T("install"), L.T("not_now"), AskKind.Info)));
                    if (yes) { Post(() => sendNote = L.T("ft_engine_downloading")); FtEngine.Install(null, () => send == SendState.Pick); }
                }
                if (FtEngine.Ready)
                {
                    Post(() => sendNote = L.T("ft_engine_reading"));
                    FtEngine.Sender es = new FtEngine.Sender();
                    Post(() => engineSend = es);
                    es.Begin(snap, L.T("ft_folder_from", Environment.UserName), () => send == SendState.Pick);
                    if (es.Ticket != null) { c.Ticket = es.Ticket; Post(() => viaEngine = true); Check("ok", L.T("ft_chk_engine")); }
                    else Check("bad", L.T("ft_chk_engine_failed", es.Error));
                }
                else Check("warn", L.T("ft_chk_no_engine"));
                if (c.Hosts.Count == 0 && c.Ticket == null) throw new UserError(L.T("ft_no_way"));
                string code2 = c.ToString();
                Post(() => { if (send != SendState.Preparing) return; code = code2; codeObj = c; send = SendState.Waiting; sendNote = null; ShowState(); CodeReady(); });
            }
            catch (Exception ex)
            {
                string msg = ex is UserError ? ex.Message : L.T("ft_err_start", ex.Message);
                Post(() =>
                {
                    if (send == SendState.Pick) return;
                    // what was being sent stays on screen next to why it didn't work
                    List<string> keep = new List<string>(items);
                    StopSending(); items.AddRange(keep);
                    send = SendState.Failed; sendError = msg; ShowState();
                });
            }
        }

        /// The code is ready: it's handed to everyone it's for (each in its own time), and copied when one was asked for.
        void CodeReady()
        {
            foreach (FtTarget t in targets) Deliver(t);
            if (copyWhenReady && code != null && wantCode) { copyWhenReady = false; CopyText(code, "ft_copied_auto"); }
        }

        /// Hands the code to one friend or PC: over the home network when they're on it, otherwise through the
        /// friends server. When they can't be reached, the code is shown to share by hand.
        void Deliver(FtTarget t)
        {
            t.State = "asking";
            string c = code; FtCode co = codeObj; FtHost h = host;
            FtPeer peer = t.Peer;
            if (peer == null) foreach (FtPeer p in FtLan.Peers) if (p.Id == t.Id) peer = p;
            new Thread(() =>
            {
                string a;
                if (peer != null)
                {
                    string s = c;
                    if (h != null && co != null)
                    {
                        // this PC's address on the network the two share, so it never leaves the house
                        FtCode v = new FtCode { Secret = co.Secret, Name = co.Name, Count = co.Count, Size = co.Size };
                        foreach (IPAddress ip in FtLan.AddressesFor(peer.Address)) if (v.Hosts.Count < 2) v.Hosts.Add(ip + ":" + h.Port);
                        if (v.Hosts.Count > 0) s = v.ToString();
                    }
                    a = FtLan.SendOffer(peer.Address, peer.Port, s, t.Friend != null ? t.Friend.Key : null);
                    if (a != "OK" && a != "BUSY") a = "NO";
                }
                else a = t.Friend != null ? FtPipe.Offer(t.Friend, c) : "NO";
                Post(() =>
                {
                    if (code != c || !targets.Contains(t)) return;
                    t.State = a == "OK" ? "waiting" : a == "BUSY" ? "busy" : a == "OFF" ? "off" : "no";
                    ShowState();
                });
            }) { IsBackground = true, Name = "ft-deliver" }.Start();
        }

        void OnHostChanged()
        {
            if (host == null) return;
            bool everyone = host.DoneCount > 0 && host.AllDone;
            if (everyone)
                foreach (FtTarget t in targets)
                {
                    if (t.State != "waiting" && t.State != "asking") continue;
                    bool done = false;
                    foreach (FtHost.Recipient r in host.Recipients) if (r.Id == t.Id && r.Done) done = true;
                    if (!done) everyone = false;   // someone hasn't accepted yet: it stays open for them
                }
            if (everyone && send != SendState.Sent)
            {
                LogSent();
                send = SendState.Sent;
                Activity.End(sendAct, null, false, L.T("ft_sent_t"), null); sendAct = null;
                CloseHost();
                ShowState();
                return;
            }
            // "Sending" once someone has started taking the files (looking at the offer isn't that yet)
            bool taking = false;
            foreach (FtHost.Recipient r in host.Recipients) if (r.Conns > 0 || r.Has > 0) taking = true;
            if (taking && send == SendState.Waiting)
            {
                send = SendState.Sending; samples.Clear();
                if (sendAct == null) sendAct = Activity.Begin(this, L.T("ft_act_send"), SendName(), () => Post(() => { StopSending(); ShowState(); }));
                ShowState();
            }
            Invalidate();
        }

        /// What was sent goes in the Recent list (once), with who got it.
        void LogSent()
        {
            if (sentLogged || host == null || host.DoneCount == 0) return;
            sentLogged = true;
            List<string> names = new List<string>();
            foreach (FtHost.Recipient r in host.Recipients)
            {
                if (!r.Done) continue;
                string n = null;
                foreach (FtTarget t in targets) if (t.Id == r.Id) n = t.Name;
                if (n == null && r.Id != null) { FtTrust.Entry f = FtTrust.Find(r.Id); if (f != null) n = f.Shown; }
                if (n != null && !names.Contains(n)) names.Add(n);
            }
            FtHistory.Add(new FtHistory.Entry { Sent = true, Name = SendName(), Who = string.Join(", ", names.ToArray()), Size = host.Total, Count = host.Files.Count, Paths = new List<string>(items) });
        }

        /// Shuts the router's door and stops listening (the server too).
        void CloseHost()
        {
            FtHost h = host; Upnp.Gateway g = gateway; int p = mappedPort;
            FtEngine.Sender es = engineSend;
            gateway = null; engineSend = null;
            if (h != null) h.Dispose();
            if (g != null || es != null) new Thread(() => { if (g != null) Upnp.Close(g, p); if (es != null) es.Dispose(); }) { IsBackground = true }.Start();
            KeepAwake.Hold(false);
        }

        void StopSending()
        {
            LogSent();
            if (sendAct != null) { Activity.End(sendAct, null, host == null || host.DoneCount == 0, host != null && host.DoneCount > 0 ? L.T("ft_sent_t") : null, null); sendAct = null; }
            bool was = send != SendState.Pick && send != SendState.Sent && send != SendState.Failed;
            if (was || host != null || engineSend != null) CloseHost();
            host = null; targets = new List<FtTarget>(); wantCode = false;
            send = SendState.Pick; code = null; codeObj = null; checks.Clear(); sendError = null; sendNote = null;
            items.Clear();
        }

        void CopyText(string t, string toastKey)
        {
            if (string.IsNullOrEmpty(t)) return;
            try { Clipboard.SetText(t); ShowToast(L.T(toastKey), true, null, null); } catch { }
        }

        // ================================================================== the servers and the firewall (File Transfer's settings)

        public void InstallEngine(Action then)
        {
            if (FtEngine.Ready) { if (then != null) then(); return; }
            if (!Ask.Confirm(FindForm(), L.T("ft_engine_q_t"), L.T("ft_engine_q"), L.T("install"), L.T("not_now"), AskKind.Info)) return;
            ActJob act = Activity.Begin(this, L.T("ft_engine_name"), "sendme", null);
            new Thread(() =>
            {
                string err = null;
                try { FtEngine.Install((got, total) => { if (total > 0) Activity.Update(act, got / (double)total, Theme.HumanSize(got)); }, null); }
                catch (Exception ex) { err = ex.Message; }
                Activity.End(act, err, false, err == null ? L.T("ft_engine_ready") : null, null);
                Post(() => { ShowState(); if (err == null && then != null) then(); else if (err != null) ShowToast(err, false, null, null); });
            }) { IsBackground = true }.Start();
        }

        /// The friends server, asked first (from File Transfer's settings).
        public void AskInstallPipe()
        {
            if (FtPipe.Ready) return;
            if (!Ask.Confirm(FindForm(), L.T("ft_pipe_q_t"), L.T("ft_pipe_q"), L.T("install"), L.T("not_now"), AskKind.Info)) return;
            InstallPipe(null);
        }

        /// Downloads the friends server (asked first by the caller), then carries on.
        void InstallPipe(Action then)
        {
            ActJob act = Activity.Begin(this, L.T("ft_pipe_name"), "dumbpipe", null);
            new Thread(() =>
            {
                string err = null;
                try { FtPipe.Install((got, total) => { if (total > 0) Activity.Update(act, got / (double)total, Theme.HumanSize(got)); }, null); }
                catch (Exception ex) { err = ex.Message; }
                Activity.End(act, err, false, err == null ? L.T("ft_engine_ready") : null, null);
                Post(() => { ShowState(); if (err == null) { FtPipe.Refresh(); if (then != null) then(); } else ShowToast(err, false, null, null); });
            }) { IsBackground = true }.Start();
        }

        public void AllowFirewall(Action done)
        {
            new Thread(() =>
            {
                bool ok = FtFirewall.Allow();
                Post(() => { ShowToast(L.T(ok ? "ft_fw_done" : "ft_fw_failed"), ok, null, null); ShowState(); if (done != null) done(); });
            }) { IsBackground = true }.Start();
        }

        // ================================================================== friends: who's there

        void BuildRows()
        {
            rows.Clear();
            foreach (FtTrust.Entry f in trusted)
            {
                FtPeer here = null;
                foreach (FtPeer p in peers) if (p.Id == f.Id) here = p;
                rows.Add(new Row { Friend = f, Peer = here, Id = f.Id, Name = f.Shown });
            }
            foreach (FtPeer p in peers)
            {
                bool friend = false;
                foreach (FtTrust.Entry f in trusted) if (f.Id == p.Id) friend = true;
                if (!friend) rows.Add(new Row { Peer = p, Id = p.Id, Name = p.Name });
            }
            if (hotRow >= rows.Count) hotRow = -1;
        }

        /// Asks each friend's PC "are you there?" through the friends server (a few seconds each, one after the
        /// other, in the background): the green dots.
        void RefreshOnline()
        {
            if (pinging || !FtPipe.Ready || SelfTest.Active) return;
            List<FtTrust.Entry> ask = new List<FtTrust.Entry>();
            foreach (Row r in rows) if (r.Friend != null && r.Peer == null && !string.IsNullOrEmpty(r.Friend.Ticket) && ask.Count < 12) ask.Add(r.Friend);
            if (ask.Count == 0) return;
            pinging = true;
            new Thread(() =>
            {
                try
                {
                    foreach (FtTrust.Entry f in ask)
                    {
                        bool on = FtPipe.Ping(f);
                        string id = f.Id;
                        Post(() => { online[id] = on; Invalidate(); });
                    }
                }
                finally { pinging = false; }
            }) { IsBackground = true, Name = "ft-online" }.Start();
        }

        /// The last time something went either way with a friend ("Sent 2 days ago").
        static string LastWith(FtTrust.Entry f)
        {
            if (f == null) return "";
            if (f.Last <= 0) return L.T("ft_never");
            return L.T(f.LastSent >= f.LastGot ? "ft_sent_when" : "ft_got_when", FtHistory.Ago(f.Last));
        }

        void RowMenu(Row r, Point screen)
        {
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("ft_menu_send"), Glyph = Ico.Send, Run = () => { pickFor = r; BrowseFiles(); pickFor = null; } });
            if (r.Friend != null)
            {
                FtTrust.Entry f = r.Friend;
                m.Add(new MenuPopup.Item { Text = L.T("ft_menu_rename"), Glyph = Ico.Edit, Run = () => Rename(f) });
                m.Add(new MenuPopup.Item { Text = L.T("ft_menu_auto"), Sub = L.T(f.Auto ? "ft_auto_on" : "ft_auto_off"), Glyph = f.Auto ? Ico.Check : Ico.Download, Run = () => FtTrust.Update(f.Id, e => e.Auto = !e.Auto) });
                m.Add(new MenuPopup.Item { Divider = true });
                m.Add(new MenuPopup.Item { Text = L.T("ft_forget"), Glyph = Ico.Delete, Danger = true, Run = () =>
                {
                    if (!Ask.Confirm(FindForm(), L.T("ft_forget_q_t", f.Shown), L.T("ft_forget_q"), L.T("ft_forget"), L.T("cancel"), AskKind.Danger)) return;
                    FtTrust.Forget(f.Id); online.Remove(f.Id); FtPipe.Refresh();
                } });
            }
            MenuPopup.ShowAt(this, screen, m, Theme.S(280));
        }

        /// A name of your own for a friend (only on this PC). Empty goes back to the name they chose.
        void Rename(FtTrust.Entry f)
        {
            string n = Ask.Input(FindForm(), L.T("ft_rename_t", f.Shown), L.T("ft_rename_d", f.Name), f.Shown, f.Name, L.T("ft_menu_rename"), L.T("cancel"), null, null);
            if (n == null) return;
            n = n.Trim();
            if (n.Length > 40) n = n.Substring(0, 40);
            string own = f.Name;
            FtTrust.Update(f.Id, e => e.Nick = n.Length == 0 || n == own ? null : n);
        }

        // ================================================================== receiving

        /// An offer from a PC on the network or from a friend (worker thread): it shows on the Receive tab, and in
        /// a pop-up by the clock when this page isn't in front. Its answer goes back to that PC.
        string OnOffer(FtPeer from, string codeText)
        {
            if (FtCode.Parse(codeText) == null) return "NO";
            string answer = "NO";
            try
            {
                Invoke((Action)(() =>
                {
                    if (recv == RecvState.Receiving || recv == RecvState.Connecting) { answer = "BUSY"; return; }
                    answer = "OK";
                    tabs.SelectedIndex = 1;
                    incomingFrom = from.Name;
                    codeBox.Box.Text = codeText;
                    Connect();
                    if (!InFront) FtPopup.Show(this);
                }));
            }
            catch { }
            return answer;
        }
        /// Whether this page is what the person is looking at.
        bool InFront { get { return Owner != null && Owner.Visible && Form.ActiveForm == Owner && Visible && Owner.WindowState != FormWindowState.Minimized; } }

        void Connect()
        {
            FtCode c = FtCode.Parse(codeBox.Box.Text);
            if (c == null) return;
            inCode = c; recvError = null; recvCancel = false; recvEngine = false; receiver = null; recvPaused = false; engineInto = null;
            if (recvThumb != null) { recvThumb = null; }
            recv = RecvState.Connecting; recvRoute = L.T("ft_connecting");
            ShowState();
            string m = FtSettings.Mode;
            new Thread(() =>
            {
                FtReceiver r = new FtReceiver(c);
                bool direct = m != "engine" && c.Hosts.Count > 0 && r.Connect(7000, () => recvCancel);
                Post(() =>
                {
                    if (recv != RecvState.Connecting || inCode != c) return;
                    if (direct)
                    {
                        receiver = r; recvTotal = r.Total; Plan(); recv = RecvState.Offer;
                        remember.SetSilently(true);
                        ShowState();
                        // the picture the sender made of it
                        new Thread(() => { Image im = FtArt.FromBytes(r.FetchThumb()); if (im != null) Post(() => { if (receiver == r) { recvThumb = im; Invalidate(); Changed(); } }); }) { IsBackground = true }.Start();
                        // a friend this PC remembered (and who proved it's them): accepted straight away
                        FtTrust.Entry t = r.Proven ? FtTrust.Find(r.SenderId) : null;
                        if (t != null && t.Auto) { ShowToast(L.T("ft_auto_accept", t.Shown), true, null, null); Accept(); }
                        return;
                    }
                    if (c.Ticket != null && m != "host")
                    {
                        // can't reach the friend's PC directly: the server finds a way
                        recvEngine = true; recvTotal = c.Size; Plan(); recv = RecvState.Offer; ShowState(); return;
                    }
                    recv = RecvState.Failed;
                    recvError = L.T(c.Ticket == null ? "ft_err_reach" : "ft_err_reach_mode");
                    ShowState();
                });
            }) { IsBackground = true, Name = "ft-connect" }.Start();
        }

        /// What's left to download into the chosen folder (an earlier try may have brought part of it).
        void Plan()
        {
            long ls = Upnp.LinkSpeed();
            slowLink = ls >= 10000000L && ls <= 100000000L ? ls : 0;   // under 10 Mbps is Windows not knowing, not a real link
            recvRemaining = recvTotal;
            if (receiver != null) try { recvRemaining = receiver.Remaining(dest); } catch { }
            Invalidate();
        }

        long FreeSpace(string dir) { try { return new DriveInfo(Path.GetPathRoot(dir)).AvailableFreeSpace; } catch { return -1; } }

        void Accept()
        {
            if (recv != RecvState.Offer) return;
            long free = FreeSpace(dest);
            if (free >= 0 && free < recvRemaining + (64L << 20))
            {
                recvError = L.T("ft_err_space", Theme.HumanSize(recvRemaining), Theme.HumanSize(free));
                Invalidate(); Changed(); return;
            }
            recvError = null; recvCancel = false; recvPaused = false; recv = RecvState.Receiving; samples.Clear(); recvDone = 0;
            recvRoute = L.T(recvEngine ? "ft_route_engine" : receiver.Address != null && Upnp.Private(receiver.Address.Address) ? "ft_route_lan" : "ft_route_direct");
            KeepAwake.Hold(true);
            tick.Start();
            recvAct = Activity.Begin(this, L.T("ft_act_recv"), IncomingName(), () => { recvCancel = true; if (receiver != null) receiver.Paused = false; });
            ShowState();
            FtReceiver r = receiver;
            if (r != null) r.Remember = !r.Proven && r.SenderId != null && r.SenderId != FtIdentity.Id && remember.Checked;
            if (r != null && r.Remember && !FtPipe.Ready && Settings.Get("TransferPipeAsked", "0") != "1")
            {
                // so the friend can send again without a code (and this PC back to them): the small friends server
                Settings.Set("TransferPipeAsked", "1");
                if (Ask.Confirm(FindForm(), L.T("ft_pipe_q_t"), L.T("ft_pipe_q"), L.T("install"), L.T("not_now"), AskKind.Info)) InstallPipe(null);
            }
            RunReceive();
        }

        /// The download itself (again after a pause on the server's route: it carries on from what's there).
        void RunReceive()
        {
            FtReceiver r = receiver; FtCode c = inCode; string d = dest;
            new Thread(() =>
            {
                string err;
                if (!recvEngine) err = r.Run(d, () => recvCancel);
                else
                {
                    // the server puts the files in the folder under their own name; a name already there gets its own folder
                    string into = engineInto ?? d;
                    if (engineInto == null)
                    {
                        bool resuming = false;
                        try { resuming = Directory.GetDirectories(d, ".sendme-recv-*").Length > 0; } catch { }
                        if (!resuming && !string.IsNullOrEmpty(c.Name) && (File.Exists(Path.Combine(d, c.Name)) || Directory.Exists(Path.Combine(d, c.Name))))
                            into = Path.Combine(d, L.T("ft_folder_received", DateTime.Now.ToString("yyyy-MM-dd HH.mm", CultureInfo.InvariantCulture)));
                        string into0 = into;
                        Post(() => engineInto = into0);
                    }
                    err = FtEngine.Receive(c.Ticket, into, () => recvCancel, got => recvDone = got);
                    if (err == null) Post(() => dest = into);
                }
                Post(() =>
                {
                    if (inCode != c) return;
                    // paused on the server's route: it stopped, and waits here for Resume
                    if (recvPaused && recvEngine && err != null) { recvCancel = false; ShowState(); return; }
                    KeepAwake.Hold(false);
                    string outp = receiver != null && receiver.Top != null ? Path.Combine(dest, receiver.Top) : !string.IsNullOrEmpty(c.Name) ? Path.Combine(dest, c.Name) : dest;
                    Activity.End(recvAct, recvCancel ? null : err, recvCancel && err != null, L.T("ft_got_d", IncomingName()), err == null ? outp : null);
                    recvAct = null; recvPaused = false;
                    if (err == null)
                    {
                        recv = RecvState.Received;
                        FtHistory.Add(new FtHistory.Entry { Sent = false, Name = IncomingName(), Who = RecvWho ?? "", Size = recvTotal, Count = receiver != null ? receiver.Files.Count : c.Count, Paths = new List<string> { outp } });
                    }
                    else if (recvCancel && deleteAfter) { deleteAfter = false; DeleteArrived(); ResetReceive(); ShowToast(L.T("ft_deleted"), true, null, null); return; }
                    else if (recvCancel) { recv = RecvState.Offer; Plan(); }
                    else { recv = RecvState.Failed; recvError = err; }
                    ShowState();
                });
            }) { IsBackground = true, Name = "ft-receive" }.Start();
        }

        /// Pause holds the download where it is; Resume carries on from there.
        public void TogglePause()
        {
            if (recv != RecvState.Receiving) return;
            recvPaused = !recvPaused;
            if (!recvEngine) { if (receiver != null) receiver.Paused = recvPaused; }
            else if (recvPaused) recvCancel = true;          // the server's route: stopped, what arrived is kept
            else { recvCancel = false; RunReceive(); }      // ...and started again from there
            samples.Clear(); speed = 0;
            ShowState();
        }

        /// Removes what arrived so far of the transfer on offer (unfinished files only).
        void DeleteArrived()
        {
            try
            {
                if (recvEngine) FtEngine.DeletePartial(engineInto ?? dest);
                else if (receiver != null) receiver.DeletePartial(dest);
            }
            catch { }
        }

        void ResetReceive()
        {
            recvCancel = true;
            if (receiver != null) receiver.Paused = false;
            recv = RecvState.Enter; receiver = null; inCode = null; recvError = null; incomingFrom = null; recvPaused = false; recvThumb = null;
            codeBox.Box.Text = "";
            ShowState();
        }

        public void OpenReceived()
        {
            string target = receiver != null && receiver.Top != null ? Path.Combine(dest, receiver.Top) : inCode != null && inCode.Name != null ? Path.Combine(dest, inCode.Name) : dest;
            Reveal(target, dest);
        }
        static void Reveal(string target, string folder)
        {
            try
            {
                if (File.Exists(target) || Directory.Exists(target)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + target + "\"");
                else if (folder != null) AddOns.OpenFolder(folder);
            }
            catch { }
        }

        // ---- what the pop-up by the clock shows
        public int RecvPhase { get { return recv == RecvState.Connecting ? 1 : recv == RecvState.Offer ? 2 : recv == RecvState.Receiving ? (recvPaused ? 4 : 3) : recv == RecvState.Received ? 5 : recv == RecvState.Failed ? 6 : 0; } }
        public string RecvWho { get { return receiver != null && !string.IsNullOrEmpty(receiver.SenderName) ? FriendName(receiver.SenderId, receiver.SenderName) : incomingFrom; } }
        static string FriendName(string id, string theirs) { FtTrust.Entry f = id != null ? FtTrust.Find(id) : null; return f != null ? f.Shown : theirs; }
        public string RecvName { get { return IncomingName(); } }
        public bool RecvIsFolder { get { return receiver != null ? receiver.Files.Count > 0 && receiver.Files[0].Rel.IndexOf('/') >= 0 : inCode != null && inCode.Count > 1; } }
        public int RecvTops { get { return receiver != null ? TopCount(receiver.Files) : 1; } }
        public Image RecvThumb { get { return recvThumb; } }
        public string RecvSizeText { get { int count = receiver != null ? receiver.Files.Count : inCode != null ? inCode.Count : 0; return FilesSize(count, recvTotal > 0 ? recvTotal : inCode != null ? inCode.Size : 0); } }
        public string RecvStats { get { return recvPaused ? L.T("ft_paused_short") : StatsOf(RecvMoved, recvTotal); } }
        public double RecvProgress { get { return recv == RecvState.Received ? 1 : recvTotal > 0 ? Math.Min(1, RecvMoved / (double)recvTotal) : 0; } }
        public string RecvErrorText { get { return recvError; } }
        public void AcceptOffer() { Accept(); }
        public void DeclineOffer() { ResetReceive(); }
        void Changed() { if (RecvChanged != null) try { RecvChanged(); } catch { } }

        // ================================================================== progress

        long RecvMoved { get { return recvEngine ? recvDone : receiver != null ? Interlocked.Read(ref receiver.Done) : 0; } }
        long SendMoved
        {
            get
            {
                if (host == null) return 0;
                long s = 0; int n = 0;
                foreach (FtHost.Recipient r in host.Recipients) { s += Math.Min(host.Total, r.Has); n++; }
                return n == 0 ? host.FriendHas : s;
            }
        }
        long SendWhole { get { if (host == null) return itemsSize; int n = host.Recipients.Count; return host.Total * Math.Max(1, n); } }
        long Moved { get { return tabs.SelectedIndex == 0 ? SendMoved : RecvMoved; } }
        long Whole { get { return tabs.SelectedIndex == 0 ? SendWhole : recvTotal; } }

        void Tick()
        {
            bool active = send == SendState.Sending || send == SendState.Waiting || send == SendState.Preparing || recv == RecvState.Receiving || recv == RecvState.Connecting;
            if (!active) { tick.Stop(); return; }
            long now = Moved;
            DateTime t = DateTime.UtcNow;
            if (recvPaused && tabs.SelectedIndex == 1) samples.Clear();   // paused: no speed, no time left
            samples.Enqueue(new KeyValuePair<DateTime, long>(t, now));
            while (samples.Count > 2 && (t - samples.Peek().Key).TotalSeconds > 4) samples.Dequeue();
            KeyValuePair<DateTime, long> first = samples.Peek();
            double secs = (t - first.Key).TotalSeconds;
            speed = secs > 0.4 ? Math.Max(0, (now - first.Value) / secs) : 0;
            if (Whole > 0) bar.Value = Math.Min(1, now / (double)Whole);
            if (sendAct != null) Activity.Update(sendAct, host != null && SendWhole > 0 ? SendMoved / (double)SendWhole : -1, speed > 1024 && tabs.SelectedIndex == 0 ? Theme.HumanSize((long)speed) + "/s" : null);
            if (recvAct != null) Activity.Update(recvAct, recvTotal > 0 ? RecvMoved / (double)recvTotal : -1, recvPaused ? L.T("ft_paused_short") : null);
            Invalidate();
            Changed();
        }

        string Stats() { return StatsOf(Moved, Whole); }
        string StatsOf(long now, long whole)
        {
            long all = Math.Max(1, whole);
            // in Arabic the dots between the parts are marked right-to-left, so the parts keep their order
            string sep = Theme.RTL ? "  ‏·‏  " : "  ·  ";
            string s = Ltr(((int)(100.0 * now / all)).ToString(CultureInfo.InvariantCulture) + "%") + sep + L.T("ft_of", Ltr(Theme.HumanSize(now)), Ltr(Theme.HumanSize(all)));
            if (speed > 1024) s += sep + Ltr(Theme.HumanSize((long)speed) + "/s");
            if (speed > 1024 && now < all) s += sep + L.T("ft_left", Ltr(Theme.Time((all - now) / speed)));
            return s;
        }
        /// Sizes and times keep their own order inside Arabic text.
        static string Ltr(string s) { return Theme.RTL ? "‪" + s + "‬" : s; }

        // ================================================================== layout

        void ShowState()
        {
            bool sendTab = tabs.SelectedIndex == 0;
            bool picking = sendTab && send == SendState.Pick, toOne = picking && Sel != null;
            drop.Visible = picking && !toOne;
            bToPaste.Visible = bToFiles.Visible = bToFolder.Visible = bToBack.Visible = toOne;
            bCopy.Visible = bCopyMsg.Visible = sendTab && ShowCode && (send == SendState.Waiting || send == SendState.Sending);
            bStop.Visible = sendTab && send != SendState.Pick;
            remember.Visible = !sendTab && recv == RecvState.Offer && receiver != null && receiver.SenderId != null && !receiver.Proven && receiver.SenderId != FtIdentity.Id;   // (not for this PC sending to itself)

            codeBox.Visible = bPaste.Visible = bConnect.Visible = !sendTab && recv == RecvState.Enter;
            bAccept.Visible = bDecline.Visible = bChange.Visible = !sendTab && recv == RecvState.Offer;
            bOpen.Visible = !sendTab && recv == RecvState.Received;
            bAgain.Visible = !sendTab && (recv == RecvState.Received || recv == RecvState.Failed);
            bPause.Visible = bRecvStop.Visible = bRecvDelete.Visible = !sendTab && recv == RecvState.Receiving;
            bPause.Glyph = recvPaused ? Ico.Play : Ico.Pause;
            bDeletePart.Visible = !sendTab && recv == RecvState.Offer && recvTotal > 0 && recvRemaining < recvTotal;
            bar.Visible = sendTab ? send == SendState.Sending || send == SendState.Sent : recv == RecvState.Receiving || recv == RecvState.Received || recv == RecvState.Connecting;
            bar.SetIndeterminate(!sendTab ? recv == RecvState.Connecting : false);
            if (send == SendState.Sent && sendTab) bar.Value = 1;
            if (recv == RecvState.Received && !sendTab) bar.Value = 1;
            DoLayout();
            Invalidate();
            Changed();
        }

        public override void DoLayout()
        {
            if (tabs == null) return;
            int W = Width, p = Pad, cw = W - p * 2;   // the whole width of the window, like the other pages
            int g = Theme.S(40);
            Place(tabs, new Rectangle(p, Top0, Math.Min(cw - g - Theme.S(12), Theme.S(340)), Theme.S(40)));
            Place(bGear, new Rectangle(p + cw - g, Top0, g, g));
            int y = Top0 + Theme.S(56);
            // the friends panel on the right; everything else on the left
            int side = cw >= Theme.S(820) ? Theme.S(300) : cw >= Theme.S(620) ? Theme.S(240) : 0;
            int gap = side > 0 ? Theme.S(16) : 0, mw = cw - side - gap;
            panel = side > 0 ? new Rectangle(p + mw + gap, y, side, Math.Max(Theme.S(160), Height - y - p)) : Rectangle.Empty;
            LayoutPanel();
            cards.Clear();
            if (!panel.IsEmpty) cards.Add(panel);

            int ip = Theme.S(22), x = p + ip, w = mw - ip * 2;
            bool sendTab = tabs.SelectedIndex == 0;
            int bottom = Height - p;
            int rcH = RecentHeight(4);
            if (sendTab && send == SendState.Pick)
            {
                // the drop area takes what the Recent list leaves
                int dh = Math.Max(Theme.S(200), bottom - y - (rcH > 0 ? rcH + Theme.S(16) : 0));
                Place(drop, new Rectangle(p, y, mw, dh));
                card = Rectangle.Empty;
                if (Sel != null)
                {
                    // a friend is picked: the middle is theirs (their round badge, their name, and three ways to send)
                    card = new Rectangle(p, y, mw, dh);
                    cards.Add(card);
                    int av = Math.Max(Theme.S(84), Math.Min(Theme.S(132), dh - Theme.S(250)));
                    int block = av + Theme.S(24) + Theme.S(30) + Theme.S(24) + Theme.S(26) + Theme.S(46) + Theme.S(14) + Theme.S(20);
                    int cx = card.X + card.Width / 2, top = card.Y + Math.Max(Theme.S(44), (dh - block) / 2);
                    selAvatar = new Rectangle(cx - av / 2, top, av, av);
                    selName = new Rectangle(card.X + Theme.S(20), selAvatar.Bottom + Theme.S(24), card.Width - Theme.S(40), Theme.S(30));
                    selSub = new Rectangle(selName.X, selName.Bottom, selName.Width, Theme.S(24));
                    int bw = Math.Min(Theme.S(170), (card.Width - Theme.S(60)) / 3), gapb = Theme.S(10), by2 = selSub.Bottom + Theme.S(26);
                    int bx = cx - (bw * 3 + gapb * 2) / 2;
                    Place(bToPaste, new Rectangle(bx, by2, bw, Theme.S(46)));
                    Place(bToFiles, new Rectangle(bx + bw + gapb, by2, bw, Theme.S(46)));
                    Place(bToFolder, new Rectangle(bx + (bw + gapb) * 2, by2, bw, Theme.S(46)));
                    selHint = new Rectangle(selName.X, by2 + Theme.S(46) + Theme.S(14), selName.Width, Theme.S(20));
                    int bkw = Math.Max(Theme.S(120), bToBack.PreferredWidth + Theme.S(8));
                    Place(bToBack, new Rectangle(card.X + Theme.S(10), card.Y + Theme.S(10), bkw, Theme.S(32)));
                }
                LayoutRecent(p, y + dh + Theme.S(16), mw, bottom);
                return;
            }
            card = new Rectangle(p, y, mw, bottom - y);
            cards.Add(card);
            int cy = card.Y + Theme.S(20);
            int art = Theme.S(84), tx = x + art + Theme.S(18);
            if (sendTab)
            {
                // what's going (its picture), then the code, who it's for, and how it's going
                artR = new Rectangle(x, cy, art, art);
                noteR = new Rectangle(tx, cy + Theme.S(50), x + w - tx, Theme.S(40));
                cy = artR.Bottom + Theme.S(18);
                bool showCode = ShowCode;
                codeR = new Rectangle(x, cy, w, showCode ? Theme.S(48) : 0);
                if (showCode)
                {
                    int bw2 = Math.Max(Theme.S(150), bCopy.PreferredWidth + Theme.S(16)), mw2 = Math.Max(Theme.S(150), bCopyMsg.PreferredWidth + Theme.S(12));
                    Place(bCopy, new Rectangle(x, codeR.Bottom + Theme.S(10), bw2, Theme.S(40)));
                    Place(bCopyMsg, new Rectangle(x + bw2 + Theme.S(8), codeR.Bottom + Theme.S(10), mw2, Theme.S(40)));
                    cy = codeR.Bottom + Theme.S(64);
                }
                targetsR = new Rectangle(x, cy, w, targets.Count * Theme.S(32));
                cy += targets.Count > 0 ? targetsR.Height + Theme.S(10) : 0;
                statsR = new Rectangle(x, cy, w, Theme.S(26));
                bool moving = send == SendState.Sending || send == SendState.Sent;
                Place(bar, new Rectangle(x, cy + Theme.S(32), w, Theme.S(8)));
                if (moving) cy += Theme.S(52);
                checksR = new Rectangle(x, cy, w, Theme.S(26) * checks.Count);
                int stw = Math.Max(Theme.S(130), bStop.PreferredWidth + Theme.S(12));
                Place(bStop, new Rectangle(x + w - stw, checksR.Bottom + Theme.S(12), stw, Theme.S(40)));
                Fit(checksR.Bottom + Theme.S(52));
                LayoutRecent(p, card.Bottom + Theme.S(16), mw, bottom);
                return;
            }
            // receiving
            if (recv == RecvState.Enter)
            {
                helpR = new Rectangle(x, cy, w, Theme.S(44));
                cy += Theme.S(54);
                int pw = Math.Max(Theme.S(96), bPaste.PreferredWidth + Theme.S(8)), cnw = Math.Max(Theme.S(130), bConnect.PreferredWidth + Theme.S(16));
                Place(codeBox, new Rectangle(x, cy, w - pw - cnw - Theme.S(16), Theme.S(42)));
                Place(bPaste, new Rectangle(x + w - pw - cnw - Theme.S(8), cy, pw, Theme.S(42)));
                Place(bConnect, new Rectangle(x + w - cnw, cy, cnw, Theme.S(42)));
                Fit(cy + Theme.S(42));
                LayoutRecent(p, card.Bottom + Theme.S(16), mw, bottom);
                return;
            }
            artR = new Rectangle(x, cy, art, art);
            offerR = new Rectangle(tx, cy, x + w - tx, Theme.S(116));
            cy = offerR.Bottom + Theme.S(8);
            destR = new Rectangle(x, cy, w - Theme.S(140), Theme.S(40));
            int chw = Math.Max(Theme.S(120), bChange.PreferredWidth + Theme.S(8));
            Place(bChange, new Rectangle(x + w - chw, cy + Theme.S(2), chw, Theme.S(36)));
            cy += Theme.S(48);
            if (remember.Visible)
            {
                Place(remember, new Rectangle(x, cy, Theme.S(48), Theme.S(30)));
                rememberR = new Rectangle(x + Theme.S(60), cy, w - Theme.S(60), Theme.S(30));
                cy += Theme.S(40);
            }
            else cy += Theme.S(8);
            statsR = new Rectangle(x, cy, w, Theme.S(52));
            Place(bar, new Rectangle(x, cy + Theme.S(56), w, Theme.S(8)));
            int by = recv == RecvState.Offer || recv == RecvState.Failed && receiver == null && inCode == null ? statsR.Y + Theme.S(12) : statsR.Bottom + Theme.S(28);
            if (recv == RecvState.Offer && recvError != null) { statsR = new Rectangle(x, destR.Bottom + Theme.S(12), w, Theme.S(44)); by = statsR.Bottom + Theme.S(12); }
            Fit(by + Theme.S(42));
            int aw = Math.Max(Theme.S(140), bAccept.PreferredWidth + Theme.S(16)), dw = Math.Max(Theme.S(110), bDecline.PreferredWidth + Theme.S(8));
            Place(bAccept, new Rectangle(x + w - aw, by, aw, Theme.S(42)));
            Place(bDecline, new Rectangle(x + w - aw - Theme.S(8) - dw, by, dw, Theme.S(42)));
            int ow = Math.Max(Theme.S(150), bOpen.PreferredWidth + Theme.S(16)), gw = Math.Max(Theme.S(150), bAgain.PreferredWidth + Theme.S(12));
            Place(bOpen, new Rectangle(x + w - ow, by, ow, Theme.S(42)));
            Place(bAgain, new Rectangle(x + w - ow - Theme.S(8) - gw, by, gw, Theme.S(42)));
            if (recv == RecvState.Failed) Place(bAgain, new Rectangle(x + w - gw, by, gw, Theme.S(42)));
            // Pause (or Resume) is the main one; Stop and Cancel-and-delete sit beside it
            int pzw = Math.Max(Theme.S(120), bPause.PreferredWidth + Theme.S(16));
            Place(bPause, new Rectangle(x + w - pzw, by, pzw, Theme.S(42)));
            int rw = Math.Max(Theme.S(120), bRecvStop.PreferredWidth + Theme.S(12));
            Place(bRecvStop, new Rectangle(x + w - pzw - Theme.S(8) - rw, by, rw, Theme.S(42)));
            int xw = Math.Max(Theme.S(150), bRecvDelete.PreferredWidth + Theme.S(16));
            Place(bRecvDelete, new Rectangle(x, by, xw, Theme.S(42)));
            int dpw = Math.Max(Theme.S(150), bDeletePart.PreferredWidth + Theme.S(8));
            Place(bDeletePart, new Rectangle(x, by, dpw, Theme.S(42)));
            LayoutRecent(p, card.Bottom + Theme.S(16), mw, bottom);
        }

        /// The card ends just under what's in it.
        void Fit(int contentBottom)
        {
            card.Height = Math.Max(Theme.S(120), contentBottom + Theme.S(22) - card.Y);
            for (int i = 0; i < cards.Count; i++) if (cards[i].X == card.X && cards[i].Y == card.Y) cards[i] = card;
        }

        // ---- the friends panel

        Rectangle PanelBody { get { return new Rectangle(panel.X + Theme.S(8), panel.Y + Theme.S(50), panel.Width - Theme.S(16), panel.Height - Theme.S(58)); } }

        void LayoutPanel()
        {
            lanHeadR = Rectangle.Empty;
            if (panel.IsEmpty) { foreach (Row r in rows) r.R = Rectangle.Empty; return; }
            Rectangle b = PanelBody;
            int y = b.Y - panelScroll, rh = Theme.S(54);
            bool anyFriend = false, lanHead = false;
            foreach (Row r in rows)
            {
                if (r.Friend != null) anyFriend = true;
                if (r.Friend == null && !lanHead)
                {
                    // PCs on the network that aren't friends (yet) come under their own small heading
                    lanHead = true;
                    if (anyFriend) y += Theme.S(8);
                    lanHeadR = new Rectangle(b.X + Theme.S(10), y, b.Width - Theme.S(20), Theme.S(24));
                    y += Theme.S(28);
                }
                r.R = new Rectangle(b.X, y, b.Width, rh);
                y += rh + Theme.S(2);
            }
            panelContent = y + panelScroll - b.Y;
            int max = Math.Max(0, panelContent - b.Height);
            if (panelScroll > max) { panelScroll = max; LayoutPanel(); }
        }

        int HitRow(Point client)
        {
            if (panel.IsEmpty) return -1;
            Point q = Ltr(client);
            if (!PanelBody.Contains(q)) return -1;
            for (int i = 0; i < rows.Count; i++) if (rows[i].R.Contains(q)) return i;
            return -1;
        }
        Rectangle MoreR(Row r) { return new Rectangle(r.R.Right - Theme.S(38), r.R.Y + (r.R.Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30)); }

        void PaintPanel(Graphics g)
        {
            if (panel.IsEmpty) return;
            Label(g, L.T("ft_friends_head"), Theme.F(11f, FontStyle.Bold), Theme.Ink, new Rectangle(panel.X + Theme.S(18), panel.Y + Theme.S(12), panel.Width / 2, Theme.S(28)), Align.Start);
            Label(g, L.T("ft_lan_you", FtIdentity.Name), Theme.F(8f), Theme.InkFaint, new Rectangle(panel.X + panel.Width / 3, panel.Y + Theme.S(12), panel.Width * 2 / 3 - Theme.S(18), Theme.S(28)), Align.End, true, false);
            Rectangle body = PanelBody;
            if (rows.Count == 0)
            {
                Rectangle e = new Rectangle(body.X + Theme.S(10), body.Y + Theme.S(6), body.Width - Theme.S(20), Math.Min(body.Height, Theme.S(150)));
                Theme.Glyph(g, Ico.Send, 16f, Theme.InkFaint, M(new Rectangle(e.X, e.Y + Theme.S(8), e.Width, Theme.S(40))));
                Label(g, L.T("ft_panel_empty"), Theme.F(8.75f), Theme.InkSoft, new Rectangle(e.X, e.Y + Theme.S(54), e.Width, e.Height - Theme.S(54)), Align.Center, false, true);
                return;
            }
            Region keep = g.Clip;
            g.SetClip(M(body), System.Drawing.Drawing2D.CombineMode.Intersect);
            if (!lanHeadR.IsEmpty) Label(g, L.T("ft_panel_lan").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.5f, FontStyle.Bold), Theme.InkFaint, lanHeadR, Align.Start);
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (r.R.Bottom < body.Y || r.R.Y > body.Bottom) continue;
                bool hot = hotRow == i, dropOn = dragRow == i;
                if (dropOn)
                {
                    Theme.FillRound(g, M(r.R), Theme.S(12), Theme.Mix(Theme.Card, Theme.AccentColor, 0.16f));
                    Theme.StrokeRound(g, M(new Rectangle(r.R.X, r.R.Y, r.R.Width - 1, r.R.Height - 1)), Theme.S(12), Theme.AccentColor, 1.5f);
                }
                else if (r.Id == selId)
                {
                    Theme.FillRound(g, M(r.R), Theme.S(12), Theme.Selected);
                    Theme.FillRound(g, M(new Rectangle(r.R.X, r.R.Y + Theme.S(14), Theme.S(3), r.R.Height - Theme.S(28))), Theme.S(1.5f), Theme.AccentColor);
                }
                else if (hot) Theme.FillRound(g, M(r.R), Theme.S(12), Theme.Hover);
                // a round badge: the first letter (a friend) or a PC
                Rectangle av = new Rectangle(r.R.X + Theme.S(10), r.R.Y + (r.R.Height - Theme.S(36)) / 2, Theme.S(36), Theme.S(36));
                using (SolidBrush b = new SolidBrush(r.Friend != null ? Theme.AccentSoft : Theme.CardInner)) g.FillEllipse(b, M(av));
                if (r.Friend != null)
                {
                    string letter = r.Name.Length > 0 ? r.Name.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture) : "?";
                    Theme.Text(g, letter, Theme.F(10.5f, FontStyle.Bold), Theme.AccentColor, M(av), Align.Center, true, false, true);
                }
                else Theme.Glyph(g, Ico.Pc, 9.5f, Theme.InkSoft, M(av));
                // the dot: green when their PC is there right now, hollow when it isn't
                bool? on = r.Peer != null ? (bool?)true : online.ContainsKey(r.Id) ? (bool?)online[r.Id] : null;
                if (on != null)
                {
                    Rectangle dot = M(new Rectangle(av.Right - Theme.S(11), av.Bottom - Theme.S(11), Theme.S(12), Theme.S(12)));
                    using (SolidBrush b = new SolidBrush(dropOn ? Theme.Mix(Theme.Card, Theme.AccentColor, 0.16f) : r.Id == selId ? Theme.Selected : hot ? Theme.Hover : Theme.Card)) g.FillEllipse(b, dot);
                    Rectangle inner = Rectangle.Inflate(dot, -Theme.S(2), -Theme.S(2));
                    if (on == true) using (SolidBrush b = new SolidBrush(Theme.Success)) g.FillEllipse(b, inner);
                    else using (Pen pn = new Pen(Theme.InkFaint, 1.5f)) g.DrawEllipse(pn, inner);
                }
                int tx = r.R.X + Theme.S(58), tw = r.R.Width - Theme.S(58) - (hot && r.Friend != null ? Theme.S(42) : Theme.S(8));
                string sub = dropOn ? L.T("ft_row_drop") : r.Friend != null && r.Peer == null && string.IsNullOrEmpty(r.Friend.Ticket) ? L.T("ft_friend_unreachable") :
                             SubFor(new FtTarget { Id = r.Id, Friend = r.Friend, Peer = r.Peer });
                Label(g, r.Name, Theme.F(9.5f, FontStyle.Bold), Theme.Ink, new Rectangle(tx, r.R.Y + Theme.S(8), tw, Theme.S(20)), Align.Start);
                Label(g, sub, Theme.F(8f), dropOn ? Theme.AccentColor : on == true ? Theme.Success : Theme.InkSoft, new Rectangle(tx, r.R.Y + Theme.S(28), tw, Theme.S(18)), Align.Start);
                if (hot && r.Friend != null)
                {
                    Rectangle mr = MoreR(r);
                    if (hotMore) Theme.FillRound(g, M(mr), Theme.S(8), Theme.Pressed);
                    Theme.Glyph(g, Ico.More, 10f, Theme.InkSoft, M(mr));
                }
            }
            g.Clip = keep;
        }

        // ---- recent transfers

        int RecentHeight(int most) { return recent.Count == 0 ? 0 : Theme.S(52) + Math.Min(most, recent.Count) * Theme.S(48) + Theme.S(10); }

        void LayoutRecent(int x, int y, int w, int bottom)
        {
            recentR.Clear(); recentShowR.Clear(); recentAgainR.Clear();
            recentCard = Rectangle.Empty;
            int room = bottom - y;
            if (recent.Count == 0 || room < Theme.S(52) + Theme.S(48) + Theme.S(10)) return;
            int n = Math.Min(recent.Count, (room - Theme.S(62)) / Theme.S(48));
            recentCard = new Rectangle(x, y, w, Theme.S(52) + n * Theme.S(48) + Theme.S(10));
            cards.Add(recentCard);
            Font lf = Theme.F(8.5f, FontStyle.Bold);
            int cw = Theme.Measure(L.T("ft_recent_clear"), lf) + Theme.S(16);
            recentClearR = new Rectangle(x + w - Theme.S(14) - cw, y + Theme.S(12), cw, Theme.S(28));
            for (int i = 0; i < n; i++)
            {
                Rectangle r = new Rectangle(x + Theme.S(10), y + Theme.S(48) + i * Theme.S(48), w - Theme.S(20), Theme.S(46));
                recentR.Add(r);
                FtHistory.Entry e = recent[i];
                bool there = e.Exists;
                int rx = r.Right - Theme.S(8);
                Rectangle show = Rectangle.Empty, again = Rectangle.Empty;
                if (there) { int sw = Theme.Measure(L.T("ft_recent_show"), lf) + Theme.S(16); show = new Rectangle(rx - sw, r.Y + Theme.S(9), sw, Theme.S(28)); rx -= sw + Theme.S(4); }
                if (there && e.Sent) { int aw = Theme.Measure(L.T("ft_recent_again"), lf) + Theme.S(16); again = new Rectangle(rx - aw, r.Y + Theme.S(9), aw, Theme.S(28)); }
                recentShowR.Add(show); recentAgainR.Add(again);
            }
        }

        void PaintRecent(Graphics g)
        {
            if (recentCard.IsEmpty) return;
            Font lf = Theme.F(8.5f, FontStyle.Bold);
            Label(g, L.T("ft_recent"), Theme.F(11f, FontStyle.Bold), Theme.Ink, new Rectangle(recentCard.X + Theme.S(22), recentCard.Y + Theme.S(12), recentCard.Width / 2, Theme.S(28)), Align.Start);
            Label(g, L.T("ft_recent_clear"), lf, hotRecentBtn == 3 ? Theme.AccentColor : Theme.InkFaint, recentClearR, Align.Center);
            string sep = Theme.RTL ? "  ‏·‏  " : "  ·  ";
            for (int i = 0; i < recentR.Count && i < recent.Count; i++)
            {
                Rectangle r = recentR[i];
                FtHistory.Entry e = recent[i];
                bool hot = hotRecent == i;
                if (hot) Theme.FillRound(g, M(r), Theme.S(10), Theme.Hover);
                Rectangle ic = new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - Theme.S(30)) / 2, Theme.S(30), Theme.S(30));
                Theme.FillRound(g, M(ic), Theme.S(9), e.Sent ? Theme.AccentSoft : Theme.SuccessBg);
                Theme.Glyph(g, e.Sent ? Ico.Send : Ico.Download, 9f, e.Sent ? Theme.AccentColor : Theme.Success, M(ic));
                int tx = ic.Right + Theme.S(12);
                int right = !recentAgainR[i].IsEmpty ? recentAgainR[i].X : !recentShowR[i].IsEmpty ? recentShowR[i].X : r.Right;
                int tw = Math.Max(Theme.S(60), right - tx - Theme.S(8));
                string who = string.IsNullOrEmpty(e.Who) ? L.T(e.Sent ? "ft_recent_code" : "ft_recent_got") : L.T(e.Sent ? "ft_recent_to" : "ft_recent_from", e.Who);
                Label(g, e.Name, Theme.F(9.25f, FontStyle.Bold), Theme.Ink, new Rectangle(tx, r.Y + Theme.S(5), tw, Theme.S(20)), Align.Start);
                Label(g, who + sep + Ltr(Theme.HumanSize(e.Size)) + sep + FtHistory.Ago(e.When), Theme.F(8f), Theme.InkSoft, new Rectangle(tx, r.Y + Theme.S(24), tw, Theme.S(18)), Align.Start);
                if (!recentAgainR[i].IsEmpty) Label(g, L.T("ft_recent_again"), lf, hot && hotRecentBtn == 2 ? Theme.AccentColor : Theme.InkSoft, recentAgainR[i], Align.Center);
                if (!recentShowR[i].IsEmpty) Label(g, L.T("ft_recent_show"), lf, hot && hotRecentBtn == 1 ? Theme.AccentColor : Theme.InkSoft, recentShowR[i], Align.Center);
            }
        }

        // ================================================================== painting

        protected override void PaintContent(Graphics g)
        {
            PaintPanel(g);
            PaintRecent(g);
            bool sendTab = tabs.SelectedIndex == 0;
            if (card.IsEmpty) return;
            if (sendTab && send == SendState.Pick) { PaintPicked(g); return; }
            Font h1 = Theme.F(12f, FontStyle.Bold), body = Theme.F(9.25f), small = Theme.F(8.5f);
            int ip = Theme.S(22), x = card.X + ip, w = card.Width - ip * 2;
            int tx = artR.Right + Theme.S(18), tw = x + w - tx;
            if (sendTab)
            {
                // what's going, with its picture
                string first = items.Count > 0 ? items[0] : null;
                FtArt.Draw(g, M(artR), first, null, first != null ? Path.GetFileName(first.TrimEnd('\\', '/')) : "", first != null && Directory.Exists(first), items.Count, () => Post(Invalidate));
                Label(g, SendTitle, h1, send == SendState.Sent ? Theme.Success : send == SendState.Failed ? Theme.Error : Theme.Ink, new Rectangle(tx, artR.Y - Theme.S(2), tw, Theme.S(28)), Align.Start);
                Label(g, SendName() + (Theme.RTL ? "  ‏·‏  " : "  ·  ") + SendSizeText, small, Theme.InkSoft, new Rectangle(tx, artR.Y + Theme.S(26), tw, Theme.S(20)), Align.Start);
                string detail = send == SendState.Sending ? (host != null ? L.T("ft_friend_on", host.Connected) : L.T("ft_via_engine_send")) : SendDetail;
                Label(g, detail, small, sendError != null ? Theme.Error : ShowCode && !wantCode && send == SendState.Waiting ? Theme.Warn : Theme.InkSoft, noteR, Align.Start, false, true);
                if (ShowCode)
                {
                    Theme.FillRound(g, M(codeR), Theme.S(10), Theme.CardInner);
                    Theme.StrokeRound(g, M(new Rectangle(codeR.X, codeR.Y, codeR.Width - 1, codeR.Height - 1)), Theme.S(10), Theme.Line);
                    // the code reads left to right in both languages (cut short with … at its end)
                    Theme.Text(g, code, Theme.Mono(9f), Theme.InkSoft, M(Rectangle.Inflate(codeR, -Theme.S(14), 0)), Align.Start, true, false, true);
                }
                // who it's for, and where each has got to
                for (int i = 0; i < targets.Count; i++)
                {
                    Rectangle r = new Rectangle(targetsR.X, targetsR.Y + i * Theme.S(32), targetsR.Width, Theme.S(30));
                    FtTarget t = targets[i];
                    Rectangle av = new Rectangle(r.X, r.Y + Theme.S(3), Theme.S(24), Theme.S(24));
                    using (SolidBrush b = new SolidBrush(t.Friend != null ? Theme.AccentSoft : Theme.CardInner)) g.FillEllipse(b, M(av));
                    if (t.Friend != null) Theme.Text(g, t.Name.Length > 0 ? t.Name.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture) : "?", Theme.F(8f, FontStyle.Bold), Theme.AccentColor, M(av), Align.Center, true, false, true);
                    else Theme.Glyph(g, Ico.Pc, 7.5f, Theme.InkSoft, M(av));
                    Color col; string st = TargetStatus(t, out col);
                    Label(g, t.Name, Theme.F(9.25f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(34), r.Y, r.Width / 2 - Theme.S(34), r.Height), Align.Start);
                    Label(g, st, Theme.F(8.75f), col, new Rectangle(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height), Align.End);
                }
                if (send == SendState.Sending || send == SendState.Sent)
                    Label(g, send == SendState.Sent ? L.T("ft_sent_d", SendName()) : Stats(), Theme.F(10f, FontStyle.Bold), send == SendState.Sent ? Theme.Success : Theme.Ink, statsR, Align.Start);
                PaintChecks(g, checksR);
                return;
            }
            // receiving
            if (recv == RecvState.Enter)
            {
                Label(g, L.T("ft_recv_t"), h1, Theme.Ink, new Rectangle(x, helpR.Y - Theme.S(4), w, Theme.S(26)), Align.Start);
                Label(g, L.T("ft_recv_d"), small, Theme.InkSoft, new Rectangle(x, helpR.Y + Theme.S(22), w, Theme.S(20)), Align.Start, true, false);
                return;
            }
            string who = RecvWho;
            string head = recv == RecvState.Connecting ? (incomingFrom != null ? L.T("ft_lan_connecting", incomingFrom) : L.T("ft_connecting")) :
                          recv == RecvState.Offer ? (who != null ? L.T("ft_offer_from", who) : L.T("ft_offer_t")) : recv == RecvState.Receiving ? (recvPaused ? L.T("ft_paused_t") : L.T("ft_receiving")) : recv == RecvState.Received ? L.T("ft_got_t") : L.T("ft_failed");
            if (inCode != null) FtArt.Draw(g, M(artR), null, recvThumb, IncomingName(), RecvIsFolder, RecvTops, null);
            Label(g, head, h1, recv == RecvState.Received ? Theme.Success : recv == RecvState.Failed ? Theme.Error : Theme.Ink, new Rectangle(tx, offerR.Y - Theme.S(4), tw, Theme.S(28)), Align.Start);
            if (inCode != null)
            {
                Label(g, IncomingName(), Theme.F(10.5f, FontStyle.Bold), Theme.Ink, new Rectangle(tx, offerR.Y + Theme.S(28), tw, Theme.S(24)), Align.Start, true, false);
                Label(g, RecvSizeText, body, Theme.InkSoft, new Rectangle(tx, offerR.Y + Theme.S(52), tw, Theme.S(20)), Align.Start);
                string route = recv == RecvState.Connecting ? null : recvEngine ? L.T("ft_route_engine") : receiver != null && receiver.Address != null && Upnp.Private(receiver.Address.Address) ? L.T("ft_route_lan") : L.T("ft_route_direct");
                if (route != null) Label(g, route, small, Theme.AccentColor, new Rectangle(tx, offerR.Y + Theme.S(74), tw, Theme.S(20)), Align.Start);
                if (recv == RecvState.Offer && slowLink > 0 && !(recvRemaining < recvTotal && recvTotal > 0))
                    Label(g, L.T("ft_slow_here", slowLink / 1000000, Math.Max(1, slowLink / 8 / 1000000)), small, Theme.Warn, new Rectangle(tx, offerR.Y + Theme.S(94), tw, Theme.S(20)), Align.Start);
                else if (recv == RecvState.Offer && recvRemaining < recvTotal && recvTotal > 0)
                    Label(g, L.T("ft_resume", Theme.HumanSize(recvTotal - recvRemaining)), small, Theme.Success, new Rectangle(tx, offerR.Y + Theme.S(94), tw, Theme.S(20)), Align.Start);
            }
            if (recv == RecvState.Offer || recv == RecvState.Receiving || recv == RecvState.Received)
            {
                Label(g, L.T("ft_save_to"), Theme.F(9f, FontStyle.Bold), Theme.Ink, new Rectangle(destR.X, destR.Y, Theme.S(100), Theme.S(20)), Align.Start);
                Label(g, Theme.RTL ? "‪" + dest + "‬" : dest, small, Theme.InkSoft, new Rectangle(destR.X, destR.Y + Theme.S(20), destR.Width, Theme.S(20)), Align.Start, true, false);
            }
            if (remember.Visible) Label(g, L.T("ft_remember", who ?? ""), Theme.F(8.75f), Theme.Ink, rememberR, Align.Start, true, false);
            if (recv == RecvState.Offer && receiver != null && receiver.Proven)
                Label(g, "✓ " + L.T("ft_is_trusted", who ?? ""), Theme.F(8.75f), Theme.Success, new Rectangle(destR.X, destR.Bottom + Theme.S(8), destR.Width, Theme.S(22)), Align.Start);
            if (recv == RecvState.Receiving || recv == RecvState.Received)
            {
                Label(g, StatsOf(RecvMoved, recvTotal), Theme.F(10f, FontStyle.Bold), Theme.Ink, new Rectangle(statsR.X, statsR.Y, statsR.Width, Theme.S(26)), Align.Start);
                int nfiles = receiver != null ? receiver.Files.Count : inCode != null ? inCode.Count : 0;
                string sub = recv == RecvState.Received ? (nfiles == 1 ? L.T("ft_verified_1") : L.T(recvEngine ? "ft_verified_engine" : "ft_verified", nfiles))
                           : recvPaused ? L.T("ft_paused")
                           : receiver != null && receiver.Reconnecting ? L.T("ft_reconnecting") : recvEngine && recvDone >= recvTotal * 0.995 && recvTotal > 0 ? L.T("ft_finishing") : L.T("ft_keep_open");
                Label(g, sub, small, recv == RecvState.Received ? Theme.Success : recvPaused ? Theme.Warn : Theme.InkSoft, new Rectangle(statsR.X, statsR.Y + Theme.S(26), statsR.Width, Theme.S(22)), Align.Start);
            }
            if (recvError != null) Label(g, recvError, body, Theme.Error, new Rectangle(statsR.X, statsR.Y + (recv == RecvState.Offer ? 0 : Theme.S(70)), statsR.Width, Theme.S(60)), Align.Start, false, true);
        }

        /// The middle when a friend is picked: their round badge (it lights up while files are dragged over), their
        /// name, and whether they can be reached. Dropping anywhere here sends to them.
        void PaintPicked(Graphics g)
        {
            Row r = Sel;
            if (r == null) return;
            bool needCode = r.Friend != null && r.Peer == null && string.IsNullOrEmpty(r.Friend.Ticket);
            bool? on = r.Peer != null ? (bool?)true : online.ContainsKey(r.Id) ? (bool?)online[r.Id] : null;
            Rectangle av = M(selAvatar);
            // the dotted ring around the badge (solid and orange while dragging over)
            Rectangle ring = Rectangle.Inflate(av, Theme.S(12), Theme.S(12));
            using (Pen pn = new Pen(dragMain ? Theme.AccentColor : Theme.InkFaint, dragMain ? 2.5f : 1.5f))
            {
                if (!dragMain) pn.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                g.DrawEllipse(pn, ring);
            }
            using (SolidBrush b = new SolidBrush(dragMain ? Theme.Mix(Theme.Card, Theme.AccentColor, 0.3f) : r.Friend != null ? Theme.AccentSoft : Theme.CardInner)) g.FillEllipse(b, av);
            if (r.Friend != null)
            {
                string letter = r.Name.Length > 0 ? r.Name.Substring(0, 1).ToUpper(CultureInfo.CurrentCulture) : "?";
                Theme.Text(g, letter, Theme.F(Math.Max(20f, selAvatar.Width / (float)Theme.S(100) * 30f), FontStyle.Bold), Theme.AccentColor, av, Align.Center, true, false, true);
            }
            else Theme.Glyph(g, Ico.Pc, Math.Max(18f, selAvatar.Width / (float)Theme.S(100) * 26f), Theme.InkSoft, av);
            if (on != null)
            {
                int d = Theme.S(24);
                Rectangle dot = M(new Rectangle(selAvatar.Right - d - Theme.S(4), selAvatar.Bottom - d - Theme.S(4), d, d));
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillEllipse(b, dot);
                Rectangle inner = Rectangle.Inflate(dot, -Theme.S(4), -Theme.S(4));
                if (on == true) using (SolidBrush b = new SolidBrush(Theme.Success)) g.FillEllipse(b, inner);
                else using (Pen pn = new Pen(Theme.InkFaint, 2f)) g.DrawEllipse(pn, inner);
            }
            Label(g, dragMain ? L.T("ft_sel_drop", r.Name) : r.Name, Theme.F(15f, FontStyle.Bold), dragMain ? Theme.AccentColor : Theme.Ink, selName, Align.Center);
            string sub = needCode ? L.T("ft_sel_need_code") : SubFor(new FtTarget { Id = r.Id, Friend = r.Friend, Peer = r.Peer });
            Label(g, sub, Theme.F(9f), needCode ? Theme.Warn : on == true ? Theme.Success : Theme.InkSoft, selSub, Align.Center, true, false);
            Label(g, L.T("ft_sel_hint"), Theme.F(8.5f), Theme.InkFaint, selHint, Align.Center, true, false);
        }

        /// What's coming, in this PC's language when it's several items.
        string IncomingName()
        {
            string nm = receiver != null && !string.IsNullOrEmpty(receiver.Name) ? receiver.Name : inCode != null ? inCode.Name : "";
            return string.IsNullOrEmpty(nm) ? L.T("ft_n_items", receiver != null ? TopCount(receiver.Files) : inCode != null ? inCode.Count : 0) : nm;
        }

        static int TopCount(List<FtFile> files)
        {
            HashSet<string> tops = new HashSet<string>();
            foreach (FtFile f in files) { int i = f.Rel.IndexOf('/'); tops.Add(i < 0 ? f.Rel : f.Rel.Substring(0, i)); }
            return tops.Count;
        }

        void PaintChecks(Graphics g, Rectangle r)
        {
            int y = r.Y;
            foreach (string c in checks)
            {
                int bar2 = c.IndexOf('|');
                string kind = c.Substring(0, bar2), text = c.Substring(bar2 + 1);
                Color col = kind == "ok" ? Theme.Success : kind == "bad" ? Theme.Error : kind == "info" ? Theme.InkFaint : Theme.Warn;
                string gl = kind == "ok" ? Ico.Check : kind == "bad" ? Ico.Error : kind == "info" ? Ico.Info : Ico.Warning;
                Theme.Glyph(g, gl, 8.5f, col, M(new Rectangle(r.X, y, Theme.S(20), Theme.S(24))));
                Label(g, text, Theme.F(8.5f), Theme.InkSoft, new Rectangle(r.X + Theme.S(26), y, r.Width - Theme.S(26), Theme.S(24)), Align.Start, true, false);
                y += Theme.S(26);
            }
        }

        // ================================================================== the mouse

        Point Ltr(Point p) { return Theme.RTL ? new Point(Width - p.X, p.Y) : p; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point q = Ltr(e.Location);
            int hr = HitRow(e.Location);
            bool more = hr >= 0 && rows[hr].Friend != null && MoreR(rows[hr]).Contains(q);
            int rc = -1, rb = 0;
            if (!recentCard.IsEmpty)
            {
                if (recentClearR.Contains(q)) rb = 3;
                for (int i = 0; i < recentR.Count; i++)
                    if (recentR[i].Contains(q)) { rc = i; rb = recentShowR[i].Contains(q) ? 1 : recentAgainR[i].Contains(q) ? 2 : 0; }
            }
            if (hr != hotRow || more != hotMore || rc != hotRecent || rb != hotRecentBtn)
            {
                hotRow = hr; hotMore = more; hotRecent = rc; hotRecentBtn = rb;
                Cursor = hr >= 0 || rb > 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hotRow >= 0 || hotRecent >= 0 || hotRecentBtn != 0) { hotRow = -1; hotMore = false; hotRecent = -1; hotRecentBtn = 0; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (panel.IsEmpty || !panel.Contains(Ltr(e.Location))) return;
            int max = Math.Max(0, panelContent - PanelBody.Height);
            int to = Math.Max(0, Math.Min(max, panelScroll - Math.Sign(e.Delta) * Theme.S(56)));
            if (to != panelScroll) { panelScroll = to; LayoutPanel(); Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int hr = HitRow(e.Location);
            if (hr >= 0)
            {
                Row r = rows[hr];
                if (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && r.Friend != null && MoreR(r).Contains(Ltr(e.Location)))) { RowMenu(r, Cursor.Position); return; }
                if (e.Button != MouseButtons.Left) return;
                // a friend or a PC: the middle becomes theirs, ready for a drop (a second click lets go)
                ClearFinished();
                selId = selId == r.Id ? null : r.Id;
                if (selId != null && tabs.SelectedIndex != 0) tabs.SelectedIndex = 0;
                ShowState();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (hotRecentBtn == 3) { FtHistory.Clear(); hotRecentBtn = 0; return; }
            if (hotRecent >= 0 && hotRecent < recent.Count)
            {
                FtHistory.Entry h = recent[hotRecent];
                if (hotRecentBtn == 1) { foreach (string p in h.Paths) if (File.Exists(p) || Directory.Exists(p)) { Reveal(p, null); break; } }
                else if (hotRecentBtn == 2)
                {
                    List<string> there = new List<string>();
                    foreach (string p in h.Paths) if (File.Exists(p) || Directory.Exists(p)) there.Add(p);
                    if (there.Count > 0) { tabs.SelectedIndex = 0; OfferFiles(there.ToArray(), Cursor.Position); }
                }
            }
        }
    }
}
