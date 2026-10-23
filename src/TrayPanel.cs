using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// What a right click on the icon by the clock opens: a small card with the app's state, quick switches
    /// (Donut Wheel, keep running, copied links, notifications) and the way back in or out.
    /// It rises out of the taskbar, and goes away when you click anywhere else.
    public class TrayPanel : Form
    {
        static TrayPanel open;
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        readonly Timer watch = new Timer { Interval = 40 };
        bool wasDown;
        static DateTime closedByClick = DateTime.MinValue;

        public static event Action OpenRequested, ExitRequested, CustomizeRequested, SettingsRequested;

        readonly Toggle[] tg = new Toggle[4];
        readonly string[] keys = { "tp_wheel", "tp_close_tray", "tp_links", "tp_notify" };
        readonly string[] glyphs = { Ico.Convert, "", Ico.Link, "" };
        readonly Rectangle[] rows = new Rectangle[4];
        readonly Btn bOpen, bSettings, bExit, bCustomize;
        Rectangle logoR, titleR, statusR, line1, line2;
        float rise;            // 0 → 1 as it comes up
        int finalY;
        readonly Timer anim = new Timer { Interval = 15 };
        bool closing;

        /// Shows the panel by the mouse (where the tray icon was clicked); a second right click closes it.
        public static void Toggle(Point at)
        {
            if (open != null && !open.IsDisposed) { open.Dismiss(); return; }
            // the click on the icon that just closed it (as a click outside) shouldn't open it again
            if ((DateTime.Now - closedByClick).TotalMilliseconds < 400) return;
            open = new TrayPanel();
            open.PlaceNear(at);
            open.Opacity = Fx.Motion ? 0 : 1;
            open.Show();
            try { SetForegroundWindow(open.Handle); } catch { }
            open.Activate();
            if (Fx.Motion) open.anim.Start();
        }

        TrayPanel()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; KeyPreview = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Theme.Card; Font = Theme.F(10f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = L.T("app_name");
            if (Theme.AppIcon != null) Icon = Theme.AppIcon;

            for (int i = 0; i < 4; i++) { tg[i] = new Toggle(); Controls.Add(tg[i]); }
            tg[0].SetSilently(DragWheel.On);
            tg[1].SetSilently(Settings.Get("CloseToTray", "0") == "1");
            tg[2].SetSilently(Settings.Get("WatchLinks", "1") == "1");
            tg[3].SetSilently(Notifier.Enabled);
            tg[0].CheckedChanged += (s, e) =>
            {
                DragWheel.Switch(tg[0].Checked, Notifier.Owner);
                // the wheel only lives while the app runs: keep it running when its window is closed
                if (tg[0].Checked && !tg[1].Checked) tg[1].Checked = true;
            };
            tg[1].CheckedChanged += (s, e) => { Settings.Set("CloseToTray", tg[1].Checked ? "1" : "0"); DragWheel.NotifyChanged(); };
            tg[2].CheckedChanged += (s, e) => { Settings.Set("WatchLinks", tg[2].Checked ? "1" : "0"); DragWheel.NotifyChanged(); };
            tg[3].CheckedChanged += (s, e) => { Notifier.Enabled = tg[3].Checked; DragWheel.NotifyChanged(); };

            bCustomize = new Btn(() => "", Ico.Edit, BtnKind.Ghost) { GlyphSize = 8.5f, InkColor = Theme.AccentColor, AccessibleName = L.T("tp_customize") };
            bCustomize.Click += (s, e) => After(CustomizeRequested);
            bOpen = new Btn(() => L.T("tp_open"), Ico.Open, BtnKind.Primary) { FontSize = 8.5f, GlyphSize = 8.5f };
            bOpen.Click += (s, e) => After(OpenRequested);
            bSettings = new Btn(() => "", Ico.Settings, BtnKind.Secondary) { AccessibleName = L.T("tp_settings"), GlyphSize = 8.5f };
            bSettings.Click += (s, e) => After(SettingsRequested);
            bExit = new Btn(() => L.T("tray_exit"), "", BtnKind.Ghost) { InkColor = Theme.Error, FontSize = 8.5f, GlyphSize = 8.5f };
            bExit.Click += (s, e) => After(ExitRequested);
            Controls.AddRange(new Control[] { bCustomize, bOpen, bSettings, bExit });
            foreach (Control c in Controls) { UiControl u = c as UiControl; if (u != null) u.Surface = Surface.Card; }

            DoLayout();
            Deactivate += (s, e) => Dismiss();
            anim.Tick += (s, e) => Step();
            // a click anywhere else closes it, even when Windows didn't let it take the focus
            // (it never stays in the way of the taskbar or other apps)
            watch.Tick += (s, e) =>
            {
                try { WatchClicks(); } catch { watch.Stop(); }
            };
            Shown += (s, e) => { wasDown = true; watch.Start(); };
        }

        void WatchClicks()
        {
            {
                bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
                if (down && !wasDown && !Bounds.Contains(Cursor.Position)) { closedByClick = DateTime.Now; Dismiss(); }
                wasDown = down;
            }
        }

        /// Closes, then does what was picked (after the panel is gone, so a window it opens comes to the front).
        void After(Action a)
        {
            Dismiss();
            if (a != null) { Form o = Notifier.Owner; try { if (o != null && !o.IsDisposed) o.BeginInvoke(a); else a(); } catch { } }
        }

        const int RowPx = 30;
        void DoLayout()
        {
            // compact, like a menu: one header line, a row per switch, one row of buttons
            int W = Theme.S(232), p = Theme.S(10), rowH = Theme.S(RowPx);
            logoR = new Rectangle(p, p, Theme.S(22), Theme.S(22));
            titleR = new Rectangle(p + Theme.S(30), p, Theme.S(90), Theme.S(22));
            statusR = new Rectangle(titleR.Right + Theme.S(4), p, W - p - titleR.Right - Theme.S(4), Theme.S(22));
            int y = logoR.Bottom + Theme.S(8);
            line1 = new Rectangle(p, y, W - p * 2, 1);
            y += Theme.S(3);
            for (int i = 0; i < 4; i++)
            {
                rows[i] = new Rectangle(p, y, W - p * 2, rowH);
                Rectangle t = new Rectangle(rows[i].Right - Theme.S(34), rows[i].Y + (rowH - Theme.S(20)) / 2, Theme.S(34), Theme.S(20));
                tg[i].Bounds = Theme.Mirror(t, W);
                y = rows[i].Bottom;
            }
            // a small pencil beside the Donut Wheel switch: customize it
            int cs = Theme.S(24);
            bCustomize.Bounds = Theme.Mirror(new Rectangle(rows[0].Right - Theme.S(34) - Theme.S(4) - cs, rows[0].Y + (rowH - cs) / 2, cs, cs), W);
            y += Theme.S(3);
            line2 = new Rectangle(p, y, W - p * 2, 1);
            y += Theme.S(8);
            int bh = Theme.S(28), sw = bh;
            int ew = Math.Max(Theme.S(56), bExit.PreferredWidth);
            int ow = W - p * 2 - sw - ew - Theme.S(8);
            bOpen.Bounds = Theme.Mirror(new Rectangle(p, y, ow, bh), W);
            bSettings.Bounds = Theme.Mirror(new Rectangle(p + ow + Theme.S(4), y, sw, bh), W);
            bExit.Bounds = Theme.Mirror(new Rectangle(W - p - ew, y, ew, bh), W);
            ClientSize = new Size(W, y + bh + p);
        }

        /// By the clock: inside the screen's work area (above, below or beside the taskbar, wherever it is), near the mouse.
        void PlaceNear(Point at)
        {
            Screen sc = Screen.FromPoint(at);
            Rectangle wa = sc.WorkingArea, b = sc.Bounds;
            int m = Theme.S(8), w = Width, h = Height;
            int x = at.X - w / 2, y = at.Y - h - m;
            bool inFlyout = wa.Contains(at);   // the "hidden icons" box sits above the taskbar: open beside it, not over it
            if (inFlyout) { x = Theme.RTL ? at.X + m * 3 : at.X - w - m * 3; y = at.Y - h + m * 2; }
            else if (wa.Bottom < b.Bottom) y = wa.Bottom - h - m;             // taskbar at the bottom
            else if (wa.Top > b.Top) y = wa.Top + m;                          // at the top
            else if (wa.Right < b.Right) { x = wa.Right - w - m; y = at.Y - h / 2; }   // on the right
            else if (wa.Left > b.Left) { x = wa.Left + m; y = at.Y - h / 2; }         // on the left
            x = Math.Max(wa.Left + m, Math.Min(wa.Right - w - m, x));
            y = Math.Max(wa.Top + m, Math.Min(wa.Bottom - h - m, y));
            finalY = y;
            bool fromBelow = wa.Bottom < b.Bottom || (wa.Top == b.Top && wa.Left == b.Left && wa.Right == b.Right);
            Location = new Point(x, Fx.Motion ? y + (fromBelow ? 1 : -1) * Theme.S(14) : y);
            riseFrom = Location.Y;
        }
        int riseFrom;

        void Step()
        {
            if (IsDisposed) { anim.Stop(); return; }
            rise = Math.Min(1, rise + 0.09f * Animator.K);
            float e = 1 - (float)Math.Pow(1 - rise, 3);
            try
            {
                Opacity = closing ? Math.Max(0, 1 - rise * 1.4) : Math.Min(1, e * 1.2);
                if (!closing) Top = (int)Math.Round(riseFrom + (finalY - riseFrom) * e);
            }
            catch { }
            if (rise >= 1) { anim.Stop(); if (closing) Close(); }
        }

        void Dismiss()
        {
            if (closing || IsDisposed) return;
            closing = true;
            if (open == this) open = null;
            if (!Fx.Motion || !Visible) { Close(); return; }
            rise = 0.3f; anim.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { watch.Stop(); watch.Dispose(); anim.Stop(); anim.Dispose(); if (open == this) open = null; base.OnFormClosed(e); }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; cp.ExStyle |= 0x80; return cp; }   // a shadow; no Alt+Tab entry
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(14);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Dismiss(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width;
            Theme.Smooth(g);
            using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);
            using (Pen pn = new Pen(Theme.CardBorder)) g.DrawPath(pn, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));
            Theme.DrawLogo(g, Theme.Mirror(logoR, W));
            Theme.Text(g, L.T("app_name"), Theme.F(9f, FontStyle.Bold), Theme.Ink, Theme.Mirror(titleR, W), Align.Start, true, false, true);
            int n = Activity.RunningCount;
            bool busy = n > 0;
            // a little dot: green while it works on something, quiet otherwise
            string status = busy ? L.T("tp_working", n) : L.T("tp_running");
            Rectangle sr = Theme.Mirror(statusR, W);
            int d = Theme.S(6);
            Rectangle dot = Theme.Mirror(new Rectangle(statusR.X, statusR.Y + (statusR.Height - d) / 2, d, d), W);
            using (SolidBrush b = new SolidBrush(busy ? Theme.AccentColor : Theme.Success)) g.FillEllipse(b, dot);
            Theme.Text(g, status, Theme.F(7.5f), Theme.InkSoft, Theme.Mirror(new Rectangle(statusR.X + Theme.S(11), statusR.Y, statusR.Width - Theme.S(11), statusR.Height), W), Align.Start, true, false, true);
            using (Pen lp = new Pen(Theme.Line))
            {
                Rectangle a = Theme.Mirror(line1, W), b = Theme.Mirror(line2, W);
                g.DrawLine(lp, a.X, a.Y, a.Right, a.Y); g.DrawLine(lp, b.X, b.Y, b.Right, b.Y);
            }
            int rowH = Theme.S(RowPx);
            for (int i = 0; i < 4; i++)
            {
                Rectangle r = rows[i];
                Theme.Glyph(g, glyphs[i], 8f, tg[i].Checked ? Theme.AccentColor : Theme.InkFaint, Theme.Mirror(new Rectangle(r.X, r.Y, Theme.S(16), rowH), W));
                Theme.Text(g, L.T(keys[i]), Theme.F(8.25f), Theme.Ink, Theme.Mirror(new Rectangle(r.X + Theme.S(20), r.Y, r.Width - Theme.S(20) - Theme.S(i == 0 ? 70 : 40), rowH), W), Align.Start, true, false, true);
            }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); bOpen.Focus(); }
    }
}
