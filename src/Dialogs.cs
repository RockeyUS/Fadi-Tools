using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    public enum AskKind { Question, Info, Warning, Danger }

    /// App-styled replacement for MessageBox: rounded card, theme colors, Arabic layout, dimmed window behind.
    public static class Ask
    {
        /// Returns true when the first (main) button is pressed.
        public static bool Confirm(IWin32Window owner, string title, string message, string yes, string no, AskKind kind = AskKind.Question)
        {
            return Show(owner, title, message, yes, no, kind);
        }

        public static void Info(IWin32Window owner, string title, string message, AskKind kind = AskKind.Info)
        {
            Show(owner, title, message, L.T("ok"), null, kind);
        }

        static bool Show(IWin32Window owner, string title, string message, string yes, string no, AskKind kind)
        {
            Form ownerForm = owner as Form;
            if (ownerForm == null && owner is Control) ownerForm = ((Control)owner).FindForm();
            Form dim = null;
            try
            {
                if (ownerForm != null && ownerForm.Visible && ownerForm.WindowState != FormWindowState.Minimized)
                {
                    // darken the window behind so the question stands out
                    dim = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                        BackColor = Color.Black, Opacity = Theme.Dark ? 0.45 : 0.32, Bounds = ownerForm.RectangleToScreen(ownerForm.ClientRectangle)
                    };
                    dim.Show(ownerForm);
                }
                using (AskForm f = new AskForm(title, message, yes, no, kind))
                {
                    IWin32Window o = (IWin32Window)dim ?? ownerForm;
                    if (o != null) f.StartPosition = FormStartPosition.CenterParent;
                    return (o != null ? f.ShowDialog(o) : f.ShowDialog()) == DialogResult.Yes;
                }
            }
            finally
            {
                if (dim != null) { dim.Close(); dim.Dispose(); }
                if (ownerForm != null) try { ownerForm.Activate(); } catch { }
            }
        }
    }

    class AskForm : Form
    {
        readonly string title, message; readonly AskKind kind;
        readonly Btn bYes, bNo;
        Rectangle iconR, titleR, msgR;

        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public AskForm(string title, string message, string yes, string no, AskKind kind)
        {
            this.title = title; this.message = message; this.kind = kind;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Font = Theme.F(10f);
            BackColor = Theme.Card;
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = title;

            int W = Theme.S(460), pad = Theme.S(28);
            int textX = pad + Theme.S(60);
            int textW = W - textX - pad;
            Font tf = Theme.F(13f, FontStyle.Bold), mf = Theme.F(10f);
            int th = TextRenderer.MeasureText(title, tf, new Size(textW, 999), TextFormatFlags.WordBreak).Height;
            int mh = string.IsNullOrEmpty(message) ? 0 : TextRenderer.MeasureText(message, mf, new Size(textW, 999), TextFormatFlags.WordBreak).Height;
            iconR = new Rectangle(pad, pad, Theme.S(46), Theme.S(46));
            titleR = new Rectangle(textX, pad + Theme.S(2), textW, th + Theme.S(4));
            msgR = new Rectangle(textX, titleR.Bottom + Theme.S(6), textW, mh + Theme.S(6));
            int by = Math.Max(iconR.Bottom, msgR.Bottom) + Theme.S(24);
            int bh = Theme.S(40);
            ClientSize = new Size(W, by + bh + pad);

            bYes = new Btn(() => yes, null, kind == AskKind.Danger ? BtnKind.Danger : BtnKind.Primary);
            bYes.Click += (s, e) => { DialogResult = DialogResult.Yes; Close(); };
            int bw = Math.Max(Theme.S(110), bYes.PreferredWidth + Theme.S(20));
            Controls.Add(bYes);
            int x = W - pad;
            x -= bw; bYes.Bounds = Mirror(new Rectangle(x, by, bw, bh));
            if (no != null)
            {
                bNo = new Btn(() => no, null, BtnKind.Secondary);
                bNo.Click += (s, e) => { DialogResult = DialogResult.No; Close(); };
                int nw = Math.Max(Theme.S(110), bNo.PreferredWidth + Theme.S(20));
                x -= Theme.S(10) + nw;
                bNo.Bounds = Mirror(new Rectangle(x, by, nw, bh));
                Controls.Add(bNo);
            }
            foreach (Control c in Controls) ((UiControl)c).Surface = Surface.Card;
            Shown += (s, e) => { if (kind == AskKind.Danger && bNo != null) bNo.Focus(); else bYes.Focus(); };
        }

        Rectangle Mirror(Rectangle r) { return Theme.Mirror(r, ClientSize.Width); }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; /* CS_DROPSHADOW */ return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { DialogResult = bNo != null ? DialogResult.No : DialogResult.Yes; Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            using (LinearGradientBrush b = new LinearGradientBrush(ClientRectangle, Theme.Card, Theme.Mix(Theme.Card, Theme.BgBottom, 0.35f), LinearGradientMode.Vertical))
                g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.CardBorder)) g.DrawPath(p, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));

            Rectangle ir = Mirror(iconR);
            Color ring = kind == AskKind.Danger ? Theme.Error : kind == AskKind.Warning ? Theme.Warn : Theme.Ink;
            Color bg = kind == AskKind.Danger ? Theme.ErrorBg : kind == AskKind.Warning ? Theme.WarnBg : Theme.Selected;
            using (SolidBrush b = new SolidBrush(bg)) g.FillEllipse(b, ir);
            string glyph = kind == AskKind.Danger || kind == AskKind.Warning ? Ico.Warning : kind == AskKind.Info ? Ico.Info : "";
            Theme.Glyph(g, glyph, 15f, ring, ir);

            Theme.Text(g, title, Theme.F(13f, FontStyle.Bold), Theme.Ink, Mirror(titleR), Align.Start, false, true);
            if (!string.IsNullOrEmpty(message))
                Theme.Text(g, message, Theme.F(10f), Theme.InkSoft, Mirror(msgR), Align.Start, false, true);
        }
    }

    /// A few friendly tips the first time the app opens (and again from Settings).
    public class TourForm : Form
    {
        public const string Version = "6";
        static readonly string[] Glyphs = { "", Ico.Search, Ico.Video, "\uE7C4", "\uE9D9" };
        int page;
        readonly Btn bNext, bBack, bSkip;
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public static void ShowTour(Form owner)
        {
            Settings.Set("TourDone", Version);
            Form dim = null;
            try
            {
                if (owner != null && owner.Visible)
                {
                    dim = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, BackColor = Color.Black, Opacity = Theme.Dark ? 0.45 : 0.3, Bounds = owner.RectangleToScreen(owner.ClientRectangle) };
                    dim.Show(owner);
                }
                using (TourForm f = new TourForm()) { f.StartPosition = FormStartPosition.CenterParent; f.ShowDialog((IWin32Window)dim ?? owner); }
            }
            finally { if (dim != null) { dim.Close(); dim.Dispose(); } if (owner != null) try { owner.Activate(); } catch { } }
        }

        public TourForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Theme.Card; Font = Theme.F(10f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = L.T("tour_title");
            ClientSize = new Size(Theme.S(520), Theme.S(400));
            int pad = Theme.S(28), bh = Theme.S(40), by = ClientSize.Height - pad - bh;
            bNext = new Btn(() => page == Glyphs.Length - 1 ? L.T("tour_done") : L.T("tour_next"), null, BtnKind.Primary) { Surface = Surface.Card };
            bNext.Click += (s, e) => { if (page == Glyphs.Length - 1) Close(); else { page++; Changed(); } };
            bBack = new Btn(() => L.T("tour_back"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bBack.Click += (s, e) => { if (page > 0) { page--; Changed(); } };
            bSkip = new Btn(() => L.T("tour_skip"), null, BtnKind.Ghost) { Surface = Surface.Card };
            bSkip.Click += (s, e) => Close();
            int W = ClientSize.Width;
            bNext.Bounds = Theme.Mirror(new Rectangle(W - pad - Theme.S(130), by, Theme.S(130), bh), W);
            bBack.Bounds = Theme.Mirror(new Rectangle(W - pad - Theme.S(130) - Theme.S(10) - Theme.S(110), by, Theme.S(110), bh), W);
            bSkip.Bounds = Theme.Mirror(new Rectangle(pad - Theme.S(10), by, Theme.S(110), bh), W);
            Controls.AddRange(new Control[] { bNext, bBack, bSkip });
            Changed();
            Shown += (s, e) => bNext.Focus();
        }

        void Changed() { bBack.Visible = page > 0; bNext.Invalidate(); Invalidate(); }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(20);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { Close(); return true; }
            if (k == Keys.Right || k == Keys.Left)
            {
                bool fwd = (k == Keys.Right) != Theme.RTL;
                if (fwd && page < Glyphs.Length - 1) { page++; Changed(); }
                else if (!fwd && page > 0) { page--; Changed(); }
                return true;
            }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            g.Clear(Theme.Card);
            using (Pen p = new Pen(Theme.CardBorder)) g.DrawPath(p, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(10)));
            int W = ClientSize.Width, pad = Theme.S(28);
            // picture: the logo on the first page, a big glyph after
            Rectangle art = new Rectangle(W / 2 - Theme.S(44), pad + Theme.S(8), Theme.S(88), Theme.S(88));
            if (page == 0 && Theme.Logo != null)
            {
                using (GraphicsPath gp = Theme.Round(art, Theme.S(22))) { GraphicsState st = g.Save(); g.SetClip(gp); g.DrawImage(Theme.Logo, art); g.Restore(st); }
            }
            else
            {
                using (LinearGradientBrush b = new LinearGradientBrush(art, Theme.PrimaryTop, Theme.PrimaryBottom, LinearGradientMode.Vertical))
                using (GraphicsPath gp = Theme.Round(art, Theme.S(26))) g.FillPath(b, gp);
                Theme.Glyph(g, Glyphs[page], 26f, Theme.PrimaryInk, art);
            }
            Rectangle tr = new Rectangle(pad, art.Bottom + Theme.S(22), W - pad * 2, Theme.S(34));
            Theme.Text(g, L.T("tour_t" + page), Theme.F(15f, FontStyle.Bold), Theme.Ink, tr, Align.Center);
            Rectangle br = new Rectangle(pad + Theme.S(10), tr.Bottom + Theme.S(8), W - pad * 2 - Theme.S(20), Theme.S(90));
            Theme.Text(g, L.T("tour_b" + page), Theme.F(10.5f), Theme.InkSoft, br, Align.Center, false, true);
            // page dots
            int n = Glyphs.Length, d = Theme.S(8), gap = Theme.S(8);
            int dx = W / 2 - (n * d + (n - 1) * gap + Theme.S(12)) / 2, dy = ClientSize.Height - pad - Theme.S(40) - Theme.S(30);
            for (int i = 0; i < n; i++)
            {
                int idx = Theme.RTL ? n - 1 - i : i;
                int w = idx == page ? d + Theme.S(12) : d;
                Rectangle dr = new Rectangle(dx, dy, w, d);
                Theme.FillRound(g, dr, d / 2f, idx == page ? Theme.Ink : Theme.Mix(Theme.InkFaint, Theme.Card, 0.4f));
                dx += w + gap;
            }
        }
    }
}
