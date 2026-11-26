using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// "Click to pick": click the thing to keep (more clicks if a part is missing), right-click what should go.
    /// The picked part lights up at once. Used by the Photos studio (a layer's mask or a selection) and the
    /// Video studio (the pick then follows the thing through the clip).
    public class PickForm : Form
    {
        readonly Bitmap picture;          // the caller's; not disposed here
        readonly Segments mode;
        readonly Btn bGo, bCancel, bUndo, bClear;
        readonly PickCanvas canvas;
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        Rectangle statusR;
        string status; Color statusColor;
        readonly string titleKey, subKey;
        public readonly List<PickPoint> Points = new List<PickPoint>();
        Sam.Look look;
        Sam.Answer answer;
        volatile bool closing;
        bool asking, again;

        /// startPoints: an earlier pick of the same picture (shown again, can be changed).
        public PickForm(Bitmap picture, string titleKey, string subKey, IList<PickPoint> startPoints)
        {
            this.picture = picture; this.titleKey = titleKey; this.subKey = subKey;
            if (startPoints != null) Points.AddRange(startPoints);
            Movable.Make(this, () => Theme.S(64));
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            Font = Theme.F(10f); BackColor = Theme.Card; Text = L.T(titleKey);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;

            // the picture as big as fits (about 720×480 at 100%), never stretched
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int maxW = Math.Min(Theme.S(720), wa.Width - Theme.S(120)), maxH = Math.Min(Theme.S(480), wa.Height - Theme.S(330));
            double k = Math.Min(maxW / (double)picture.Width, maxH / (double)picture.Height);
            int pw = Math.Max(Theme.S(200), (int)(picture.Width * k)), ph = Math.Max(Theme.S(120), (int)(picture.Height * k));
            int pad = Theme.S(26), W = Math.Max(Theme.S(560), pw + pad * 2), cw = W - pad * 2;

            int y = Theme.S(84);
            mode = new Segments(() => L.T("pick_keep"), () => L.T("pick_drop")) { FontSize = 8.75f };
            mode.Glyphs = new[] { Ico.Add, "" };
            mode.Bounds = Theme.Mirror(new Rectangle(pad, y, Math.Min(Theme.S(250), cw / 2), Theme.S(34)), W);
            bClear = new Btn(() => L.T("pick_clear"), null, BtnKind.Ghost) { FontSize = 9f };
            bClear.Click += (s, e) => { Points.Clear(); answer = null; canvas.Invalidate(); Changed(); };
            bUndo = new Btn(() => L.T("pick_undo"), "", BtnKind.Secondary) { FontSize = 9f };
            bUndo.Click += (s, e) => Undo();
            int uw = Math.Max(Theme.S(96), bUndo.PreferredWidth + Theme.S(10)), clw = Math.Max(Theme.S(80), bClear.PreferredWidth + Theme.S(8));
            bClear.Bounds = Theme.Mirror(new Rectangle(W - pad - clw, y, clw, Theme.S(34)), W);
            bUndo.Bounds = Theme.Mirror(new Rectangle(W - pad - clw - Theme.S(8) - uw, y, uw, Theme.S(34)), W);
            y += Theme.S(46);

            canvas = new PickCanvas(this) { Bounds = new Rectangle((W - pw) / 2, y, pw, ph) };
            y += ph + Theme.S(12);
            statusR = new Rectangle(pad, y, cw, Theme.S(40)); y += Theme.S(50);

            bGo = new Btn(() => L.T("pick_go"), Ps.Subject, BtnKind.Primary);
            bGo.Click += (s, e) => Done();
            bCancel = new Btn(() => L.T("cancel"), null, BtnKind.Ghost);
            bCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            int gw = Math.Max(Theme.S(150), bGo.PreferredWidth + Theme.S(20)), xw = Math.Max(Theme.S(90), bCancel.PreferredWidth + Theme.S(12));
            bGo.Bounds = Theme.Mirror(new Rectangle(W - pad - gw, y, gw, Theme.S(40)), W);
            bCancel.Bounds = Theme.Mirror(new Rectangle(pad - Theme.S(8), y + Theme.S(2), xw, Theme.S(36)), W);
            ClientSize = new Size(W, y + Theme.S(40) + pad);
            foreach (UiControl c in new UiControl[] { mode, bUndo, bClear, bGo, bCancel }) { c.Surface = Surface.Card; Controls.Add(c); }
            Controls.Add(canvas);
            Enable();
            Shown += (s, e) => Start();
        }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        bool HasKeep { get { return Points.Exists(p => p.Keep); } }
        void Enable()
        {
            bool ok = look != null;
            bGo.Enabled = ok && HasKeep && answer != null && answer.Area > 0;
            bUndo.Enabled = ok && Points.Count > 0;
            bClear.Enabled = ok && Points.Count > 0;
        }

        /// Downloads the models the first time (the caller asked already), then looks at the picture once.
        void Start()
        {
            status = Sam.Ready ? L.T("pick_looking") : L.T("pick_dl_start"); statusColor = Theme.InkSoft; Invalidate();
            Bitmap copy;
            lock (picture) copy = new Bitmap(picture);
            Thread th = new Thread(() =>
            {
                string err = null; Sam.Look l = null;
                try
                {
                    if (!Sam.Ready)
                    {
                        DateTime last = DateTime.MinValue;
                        Sam.Install((f, what) =>
                        {
                            if ((DateTime.Now - last).TotalMilliseconds < 150) return; last = DateTime.Now;
                            Ui(() => { status = what + "  " + (int)(f * 100) + "%"; Invalidate(); });
                        }, () => closing || Tools.ShuttingDown);
                        Ui(() => { status = L.T("pick_looking"); Invalidate(); });
                    }
                    l = Sam.See(copy);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { err = ex.Message; }
                finally { copy.Dispose(); }
                Ui(() =>
                {
                    if (err != null) { status = L.T("pick_failed", err); statusColor = Theme.Error; Invalidate(); return; }
                    look = l;
                    canvas.Cursor = Cursors.Cross;
                    Changed();
                });
            }) { IsBackground = true };
            th.Start();
        }

        /// Asks SAM again after a click (one at a time; clicks made meanwhile are asked together right after).
        void Changed()
        {
            Enable();
            if (look == null) { Invalidate(); return; }
            if (!HasKeep) { answer = null; status = null; canvas.Invalidate(); Invalidate(); Enable(); return; }
            if (asking) { again = true; return; }
            asking = true; again = false;
            List<PickPoint> pts = new List<PickPoint>(Points);
            Sam.Look l = look;
            Thread th = new Thread(() =>
            {
                Sam.Answer a = null; string err = null;
                try { a = Sam.Ask(l, pts, null, null); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    asking = false;
                    if (err != null) { status = L.T("pick_failed", err); statusColor = Theme.Error; }
                    else { answer = a; status = null; canvas.Picked(a, l); }
                    if (again) { Changed(); return; }
                    Enable(); Invalidate();
                });
            }) { IsBackground = true };
            th.Start();
        }

        internal void Click2(float u, float v, bool right)
        {
            if (look == null) return;
            bool keep = right ? false : mode.SelectedIndex == 0;
            if (!keep && !HasKeep) { status = L.T("pick_first"); statusColor = Theme.InkSoft; Invalidate(); return; }
            Points.Add(new PickPoint(Math.Max(0, Math.Min(1, u)), Math.Max(0, Math.Min(1, v)), keep));
            canvas.Invalidate();
            Changed();
        }

        void Undo()
        {
            if (Points.Count == 0) return;
            Points.RemoveAt(Points.Count - 1);
            if (!HasKeep) Points.Clear();
            canvas.Invalidate();
            Changed();
        }

        void Done()
        {
            if (asking || answer == null || answer.Area == 0 || !HasKeep) { if (!HasKeep) { status = L.T("pick_first"); statusColor = Theme.InkSoft; Invalidate(); } return; }
            DialogResult = DialogResult.OK; Close();
        }

        /// The pick as a mask (255 = keep) at w×h (the picture's shape). Only after OK.
        public byte[] MaskFor(int w, int h) { return answer == null || look == null ? null : Sam.Mask(answer.Low, look.AW, look.AH, w, h); }

        protected override void OnFormClosing(FormClosingEventArgs e) { closing = true; base.OnFormClosing(e); }
        protected override void OnFormClosed(FormClosedEventArgs e) { canvas.Drop(); base.OnFormClosed(e); }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (k == Keys.Enter) { Done(); return true; }
            if (k == (Keys.Control | Keys.Z) || k == Keys.Back) { Undo(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            using (Pen p = new Pen(Theme.CardBorder)) using (GraphicsPath gp = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9))) g.DrawPath(p, gp);
            int pad = Theme.S(26), W = Width;
            Func<Rectangle, Rectangle> m = r => Theme.Mirror(r, W);
            Rectangle ic = m(new Rectangle(pad, Theme.S(22), Theme.S(42), Theme.S(42)));
            Theme.FillRound(g, ic, Theme.S(11), Theme.AccentSoft);
            Theme.Glyph(g, Ps.Subject, 13f, Theme.AccentColor, ic);
            Theme.Text(g, L.T(titleKey), Theme.Display(15f), Theme.Ink, m(new Rectangle(pad + Theme.S(56), Theme.S(18), W - pad * 2 - Theme.S(56), Theme.S(28))), Align.Start);
            Theme.Text(g, L.T(subKey), Theme.F(9f), Theme.InkSoft, m(new Rectangle(pad + Theme.S(56), Theme.S(46), W - pad * 2 - Theme.S(56), Theme.S(20))), Align.Start);
            // a soft frame around the picture
            Rectangle cr = canvas.Bounds; cr.Inflate(1, 1);
            using (Pen p = new Pen(Theme.CardBorder)) g.DrawRectangle(p, cr);
            string st = status; Color sc = statusColor;
            if (st == null)
            {
                st = look == null ? L.T("pick_looking") : !HasKeep ? L.T("pick_hint") : answer != null && answer.Area > 0 ? L.T("pick_more") : L.T("pick_nothing");
                sc = Theme.InkSoft;
            }
            Rectangle nr = m(statusR);
            Theme.FillRound(g, nr, Theme.S(9), Theme.CardInner);
            Theme.Text(g, st, Theme.F(8.5f), sc, m(new Rectangle(statusR.X + Theme.S(12), statusR.Y, statusR.Width - Theme.S(24), statusR.Height)), Align.Start, true, true);
        }

        /// The picture with the pick lit up and the clicks as dots.
        class PickCanvas : Control
        {
            readonly PickForm f;
            Bitmap shown, lit;   // the picture at this size; and with the pick drawn in
            public PickCanvas(PickForm f)
            {
                this.f = f;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.WaitCursor;
            }

            Bitmap Shown
            {
                get
                {
                    if (shown == null || shown.Width != Width || shown.Height != Height)
                    {
                        if (shown != null) shown.Dispose();
                        shown = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(shown))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            using (SolidBrush cb = new SolidBrush(Color.FromArgb(200, 200, 200))) g.FillRectangle(cb, 0, 0, Width, Height);
                            lock (f.picture) g.DrawImage(f.picture, new Rectangle(0, 0, Width, Height));
                        }
                    }
                    return shown;
                }
            }

            /// Lights up what is picked: everything else is dimmed, the picked part gets a light tint and an edge.
            public void Picked(Sam.Answer a, Sam.Look l)
            {
                if (lit != null) { lit.Dispose(); lit = null; }
                if (a != null && a.Area > 0)
                {
                    int w = Width, h = Height;
                    float[] al = Sam.Alpha(a.Low, l.AW, l.AH, w, h);
                    Bitmap s = Shown;
                    lit = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    Rectangle r = new Rectangle(0, 0, w, h);
                    BitmapData sd = s.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb), ld = lit.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int[] px = new int[w * h];
                        for (int y = 0; y < h; y++) Marshal.Copy(new IntPtr(sd.Scan0.ToInt64() + (long)y * sd.Stride), px, y * w, w);
                        Color ac = Theme.AccentColor;
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                            {
                                int i = y * w + x, c = px[i];
                                int R = (c >> 16) & 255, G = (c >> 8) & 255, B = c & 255;
                                bool inside = al[i] > 0.5f;
                                bool edge = inside && ((x > 0 && al[i - 1] <= 0.5f) || (x < w - 1 && al[i + 1] <= 0.5f) || (y > 0 && al[i - w] <= 0.5f) || (y < h - 1 && al[i + w] <= 0.5f));
                                if (edge) { R = 255; G = 255; B = 255; }
                                else if (inside) { R = (R * 3 + ac.R) / 4; G = (G * 3 + ac.G) / 4; B = (B * 3 + ac.B) / 4; }
                                else { R = R * 2 / 5; G = G * 2 / 5; B = B * 2 / 5; }
                                px[i] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
                            }
                        for (int y = 0; y < h; y++) Marshal.Copy(px, y * w, new IntPtr(ld.Scan0.ToInt64() + (long)y * ld.Stride), w);
                    }
                    finally { s.UnlockBits(sd); lit.UnlockBits(ld); }
                }
                Invalidate();
            }

            public void Drop() { if (shown != null) shown.Dispose(); if (lit != null) lit.Dispose(); shown = lit = null; }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
                f.Click2(e.X / (float)Math.Max(1, Width), e.Y / (float)Math.Max(1, Height), e.Button == MouseButtons.Right);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.DrawImage(f.HasKeep && lit != null ? lit : Shown, 0, 0);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int rr = Theme.S(7);
                foreach (PickPoint p in f.Points)
                {
                    float x = p.U * Width, y = p.V * Height;
                    using (SolidBrush b = new SolidBrush(p.Keep ? Color.FromArgb(46, 204, 113) : Color.FromArgb(255, 77, 79)))
                        g.FillEllipse(b, x - rr, y - rr, rr * 2, rr * 2);
                    using (Pen pen = new Pen(Color.White, Math.Max(1.5f, Theme.S(2)))) g.DrawEllipse(pen, x - rr, y - rr, rr * 2, rr * 2);
                    using (Pen pen = new Pen(Color.White, Math.Max(1.5f, Theme.S(2))))
                    {
                        float k = rr * 0.5f;
                        g.DrawLine(pen, x - k, y, x + k, y);
                        if (p.Keep) g.DrawLine(pen, x, y - k, x, y + k);
                    }
                }
                if (f.look == null)
                {
                    using (SolidBrush veil = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(veil, ClientRectangle);
                }
            }
        }
    }
}
