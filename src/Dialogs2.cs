using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// An app-styled dialog with a title, a short message and rows of controls (label above each).
    public class FormDialog : Form
    {
        readonly string title, message;
        readonly List<Func<string>> labels = new List<Func<string>>();
        readonly List<Control> rows = new List<Control>();
        readonly List<int> heights = new List<int>();
        readonly List<Rectangle> labelR = new List<Rectangle>();
        protected readonly Btn bOk, bCancel;
        Rectangle titleR, msgR;
        protected int DialogWidth = 500;
        public Func<bool> Validate2;   // false = keep the dialog open

        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public FormDialog(string title, string message, string ok)
        {
            Movable.Make(this, () => Theme.S(64));   // drag it by the top
            this.title = title; this.message = message;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Font = Theme.F(10f);
            BackColor = Theme.Card;
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = title;
            bOk = new Btn(() => ok, null, BtnKind.Primary) { Surface = Surface.Card };
            bOk.Click += (s, e) => { if (Validate2 != null && !Validate2()) return; DialogResult = DialogResult.OK; Close(); };
            bCancel = new Btn(() => L.T("cancel"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(bOk); Controls.Add(bCancel);
        }

        /// A row: a small heading and a control under it (label null = no heading).
        public void Add(Func<string> label, Control c, int height)
        {
            labels.Add(label); rows.Add(c); heights.Add(height);
            if (c is UiControl) ((UiControl)c).Surface = Surface.Card;
            Controls.Add(c);
        }

        protected virtual void LayoutExtra(int x, ref int y, int w) { }

        void DoLayout()
        {
            int W = Theme.S(DialogWidth), pad = Theme.S(28), w = W - pad * 2;
            Font tf = Theme.F(13f, FontStyle.Bold), mf = Theme.F(9.75f);
            int th = TextRenderer.MeasureText(title, tf, new Size(w, 999), TextFormatFlags.WordBreak).Height;
            int mh = string.IsNullOrEmpty(message) ? 0 : TextRenderer.MeasureText(message, mf, new Size(w, 999), TextFormatFlags.WordBreak).Height;
            titleR = new Rectangle(pad, pad, w, th + Theme.S(4));
            msgR = new Rectangle(pad, titleR.Bottom + Theme.S(4), w, mh + Theme.S(4));
            int y = (mh > 0 ? msgR.Bottom : titleR.Bottom) + Theme.S(14);
            labelR.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                if (labels[i] != null) { labelR.Add(new Rectangle(pad, y, w, Theme.S(20))); y += Theme.S(24); } else labelR.Add(Rectangle.Empty);
                rows[i].Bounds = Theme.Mirror(new Rectangle(pad, y, w, Theme.S(heights[i])), W);
                y += Theme.S(heights[i]) + Theme.S(14);
            }
            LayoutExtra(pad, ref y, w);
            y += Theme.S(8);
            int bh = Theme.S(40);
            int ow = Math.Max(Theme.S(110), bOk.PreferredWidth + Theme.S(20)), cw = Math.Max(Theme.S(100), bCancel.PreferredWidth + Theme.S(20));
            bOk.Bounds = Theme.Mirror(new Rectangle(W - pad - ow, y, ow, bh), W);
            bCancel.Bounds = Theme.Mirror(new Rectangle(W - pad - ow - Theme.S(10) - cw, y, cw, bh), W);
            ClientSize = new Size(W, y + bh + pad);
        }

        protected override void OnLoad(EventArgs e) { DoLayout(); base.OnLoad(e); }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DoLayout();
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            using (LinearGradientBrush b = new LinearGradientBrush(ClientRectangle, Theme.Card, Theme.Mix(Theme.Card, Theme.BgBottom, 0.35f), LinearGradientMode.Vertical))
                g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.CardBorder)) g.DrawPath(p, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));
            int W = ClientSize.Width;
            Theme.Text(g, title, Theme.F(13f, FontStyle.Bold), Theme.Ink, Theme.Mirror(titleR, W), Align.Start, false, true);
            if (!string.IsNullOrEmpty(message)) Theme.Text(g, message, Theme.F(9.75f), Theme.InkSoft, Theme.Mirror(msgR, W), Align.Start, false, true);
            for (int i = 0; i < labels.Count && i < labelR.Count; i++)
                if (labels[i] != null) Theme.Text(g, labels[i](), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(labelR[i], W), Align.Start);
            PaintExtra(g);
        }
        protected virtual void PaintExtra(Graphics g) { }
    }

    /// Draw your signature with the mouse (or pen / finger).
    public class SignPad : UiControl
    {
        readonly List<List<PointF>> strokes = new List<List<PointF>>();
        List<PointF> cur;
        public Color Ink = Color.FromArgb(20, 20, 30);
        public Bitmap Picture;      // or a picture of a signature (chosen from a file)
        public event Action Changed;

        public SignPad() { Cursor = Cursors.Cross; Surface = Surface.Card; }
        public bool Empty { get { return strokes.Count == 0 && Picture == null; } }
        public void Clear() { strokes.Clear(); if (Picture != null) { Picture.Dispose(); Picture = null; } Invalidate(); if (Changed != null) Changed(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (Picture != null) { Picture.Dispose(); Picture = null; }
            cur = new List<PointF> { e.Location };
            strokes.Add(cur);
            Capture = true; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (cur == null) return;
            PointF last = cur[cur.Count - 1];
            if (Math.Abs(last.X - e.X) + Math.Abs(last.Y - e.Y) < 1.5f) return;
            cur.Add(e.Location); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (cur == null) return;
            if (cur.Count == 1) cur.Add(new PointF(cur[0].X + 0.5f, cur[0].Y + 0.5f));   // a dot
            cur = null; Capture = false; Invalidate();
            if (Changed != null) Changed();
        }

        static void DrawStrokes(Graphics g, List<List<PointF>> all, Color ink, float width)
        {
            using (Pen p = new Pen(ink, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                foreach (List<PointF> s in all)
                {
                    if (s.Count < 2) continue;
                    if (s.Count < 3) { g.DrawLines(p, s.ToArray()); continue; }
                    g.DrawCurve(p, s.ToArray(), 0.4f);
                }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Theme.FillRound(g, r, Theme.S(12), Color.White);
            Theme.StrokeRound(g, r, Theme.S(12), Theme.FieldBorder);
            // a line to sign on
            using (Pen lp = new Pen(Color.FromArgb(60, 0, 0, 0)) { DashPattern = new float[] { 3, 3 } })
                g.DrawLine(lp, Theme.S(24), Height * 0.72f, Width - Theme.S(24), Height * 0.72f);
            if (Picture != null)
            {
                float k = Math.Min((Width - Theme.S(30)) / (float)Picture.Width, (Height - Theme.S(30)) / (float)Picture.Height);
                float w = Picture.Width * k, h = Picture.Height * k;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Picture, (Width - w) / 2, (Height - h) / 2, w, h);
            }
            else if (strokes.Count == 0)
                Theme.Text(g, L.T("sign_here"), Theme.F(10f), Color.FromArgb(150, 110, 110, 120), new Rectangle(0, 0, Width, (int)(Height * 0.72f) - Theme.S(6)), Align.Center, true, false);
            DrawStrokes(g, strokes, Ink, Theme.S(2.6f));
        }

        /// The signature as a see-through picture, cropped to the ink (null when nothing was drawn).
        public Bitmap Export()
        {
            if (Picture != null) return new Bitmap(Picture);
            if (strokes.Count == 0) return null;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (List<PointF> s in strokes) foreach (PointF p in s) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            float pad = Theme.S(6), k = 4f;
            int w = (int)((maxX - minX + pad * 2) * k), h = (int)((maxY - minY + pad * 2) * k);
            Bitmap b = new Bitmap(Math.Max(8, w), Math.Max(8, h), PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(k, k);
                g.TranslateTransform(-minX + pad, -minY + pad);
                DrawStrokes(g, strokes, Ink, Theme.S(2.6f));
            }
            return b;
        }

        /// A photo or scan of a signature: the paper around it becomes see-through.
        public void LoadPicture(string path)
        {
            using (Image src = ViewerPage.Decode(path, 1600))
            {
                Bitmap b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(b)) g.DrawImage(src, 0, 0, src.Width, src.Height);
                BitmapData d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] px = new byte[d.Stride * b.Height];
                    Marshal.Copy(d.Scan0, px, 0, px.Length);
                    for (int i = 0; i + 3 < px.Length; i += 4)
                    {
                        int lum = (px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000;
                        // light paper fades out; ink stays (a soft edge keeps it smooth)
                        int a = lum >= 215 ? 0 : lum <= 150 ? 255 : (215 - lum) * 255 / 65;
                        px[i + 3] = (byte)Math.Min(px[i + 3], a);
                    }
                    Marshal.Copy(px, 0, d.Scan0, px.Length);
                }
                finally { b.UnlockBits(d); }
                if (Picture != null) Picture.Dispose();
                Picture = b;
            }
            strokes.Clear();
            Invalidate();
            if (Changed != null) Changed();
        }
    }

    /// A page with the signature on it: drag it where it goes, drag the corner to make it bigger or smaller.
    public class PlacePreview : UiControl
    {
        public Image Page;
        public Bitmap Sign;
        public RectangleF Box = new RectangleF(0.6f, 0.82f, 0.3f, 0.08f);   // 0..1 of the page
        int drag;   // 0 none, 1 move, 2 resize
        PointF from; RectangleF boxFrom;

        public PlacePreview() { Surface = Surface.Card; }

        RectangleF PageRect
        {
            get
            {
                if (Page == null) return new RectangleF(Theme.S(10), Theme.S(10), Width - Theme.S(20), Height - Theme.S(20));
                float k = Math.Min((Width - Theme.S(16)) / (float)Page.Width, (Height - Theme.S(16)) / (float)Page.Height);
                float w = Page.Width * k, h = Page.Height * k;
                return new RectangleF((Width - w) / 2, (Height - h) / 2, w, h);
            }
        }
        RectangleF SignRect { get { RectangleF p = PageRect; return new RectangleF(p.X + Box.X * p.Width, p.Y + Box.Y * p.Height, Box.Width * p.Width, Box.Height * p.Height); } }
        RectangleF Handle { get { RectangleF s = SignRect; float h = Theme.S(12); return new RectangleF(s.Right - h / 2, s.Bottom - h / 2, h, h); } }

        /// Keeps the signature's own shape (width : height) and sets a sensible starting size.
        public void Fit()
        {
            if (Sign == null) return;
            RectangleF p = PageRect;
            float aspect = Sign.Height / (float)Sign.Width * (p.Width / Math.Max(1, p.Height));
            float w = Math.Min(0.35f, Box.Width);
            float h = w * aspect;
            Box = new RectangleF(Math.Min(Box.X, 1 - w), Math.Min(Box.Y, 1 - h), w, h);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Sign == null) return;
            RectangleF hr = Handle; hr.Inflate(Theme.S(4), Theme.S(4));
            if (hr.Contains(e.Location)) drag = 2;
            else if (SignRect.Contains(e.Location)) drag = 1;
            else return;
            from = e.Location; boxFrom = Box; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            RectangleF p = PageRect;
            if (drag == 0)
            {
                RectangleF hr = Handle; hr.Inflate(Theme.S(4), Theme.S(4));
                Cursor = Sign != null && hr.Contains(e.Location) ? Cursors.SizeNWSE : Sign != null && SignRect.Contains(e.Location) ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            float dx = (e.X - from.X) / p.Width, dy = (e.Y - from.Y) / p.Height;
            if (drag == 1)
                Box = new RectangleF(Math.Max(0, Math.Min(1 - boxFrom.Width, boxFrom.X + dx)), Math.Max(0, Math.Min(1 - boxFrom.Height, boxFrom.Y + dy)), boxFrom.Width, boxFrom.Height);
            else
            {
                float aspect = boxFrom.Height / Math.Max(0.0001f, boxFrom.Width);
                float w = Math.Max(0.05f, Math.Min(1 - boxFrom.X, boxFrom.Width + dx));
                float h = w * aspect;
                if (boxFrom.Y + h > 1) { h = 1 - boxFrom.Y; w = h / aspect; }
                Box = new RectangleF(boxFrom.X, boxFrom.Y, w, h);
            }
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); drag = 0; Capture = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            RectangleF p = PageRect;
            // a soft grey tray, so a white page stands out in light mode too
            Theme.FillRound(g, new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(12), Theme.Dark ? Theme.Mix(Theme.Card, Color.Black, 0.25f) : Theme.Mix(Theme.Card, Color.Black, 0.07f));
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillRectangle(sh, p.X + 2, p.Y + 3, p.Width, p.Height);
            g.FillRectangle(Brushes.White, p);
            if (Page != null) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(Page, p); }
            if (Sign == null) return;
            RectangleF s = SignRect;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Sign, s);
            using (Pen bp = new Pen(Theme.AccentColor, 1.4f) { DashPattern = new float[] { 3, 2 } }) g.DrawRectangle(bp, s.X, s.Y, s.Width, s.Height);
            RectangleF h = Handle;
            using (SolidBrush hb = new SolidBrush(Theme.AccentColor)) g.FillEllipse(hb, h);
            g.DrawEllipse(Pens.White, h);
        }
    }

    /// Sign a PDF: draw (or pick a picture of) the signature, then put it on the page.
    public class SignDialog : FormDialog
    {
        public readonly SignPad Pad = new SignPad();
        public readonly PlacePreview Place = new PlacePreview();
        public readonly Segments Ink, Where;

        /// The drawing area with its ink color and clear / picture buttons under it.
        class SignBox : UiControl
        {
            readonly SignDialog d;
            readonly Btn bClear, bPicture;
            public SignBox(SignDialog dlg)
            {
                d = dlg; Surface = Surface.Card;
                bClear = new Btn(() => L.T("sign_clear"), Ico.Delete, BtnKind.Ghost) { FontSize = 9f, Surface = Surface.Card };
                bClear.Click += (s, e) => d.Pad.Clear();
                bPicture = new Btn(() => L.T("sign_picture"), Ico.Photo, BtnKind.Ghost) { FontSize = 9f, Surface = Surface.Card };
                bPicture.Click += (s, e) =>
                {
                    using (OpenFileDialog o = new OpenFileDialog { Title = L.T("sign_picture"), Filter = L.T("ed_photo_files") + "|*.png;*.jpg;*.jpeg;*.webp;*.heic;*.bmp|" + L.T("all_files") + "|*.*" })
                        if (LastDir.Show(o, d, "sign") == DialogResult.OK)
                            try { d.Pad.LoadPicture(o.FileName); } catch (Exception ex) { Ask.Info(d, L.T("sign_title"), ex.Message, AskKind.Warning); }
                };
                Controls.AddRange(new Control[] { d.Pad, d.Ink, bClear, bPicture });
            }
            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                int bh = Theme.S(32), W = Width;
                d.Pad.Bounds = new Rectangle(0, 0, W, Height - bh - Theme.S(8));
                int y = Height - bh;
                int cw = Math.Max(Theme.S(90), bClear.PreferredWidth), pw = Math.Max(Theme.S(120), bPicture.PreferredWidth);
                d.Ink.Bounds = Theme.Mirror(new Rectangle(0, y, Theme.S(180), bh), W);
                bPicture.Bounds = Theme.Mirror(new Rectangle(W - cw - pw - Theme.S(6), y, pw, bh), W);
                bClear.Bounds = Theme.Mirror(new Rectangle(W - cw, y, cw, bh), W);
            }
            protected override void OnPaint(PaintEventArgs e) { PaintBg(e.Graphics); }
        }

        public SignDialog(Image page, bool hasSelection) : base(L.T("sign_title"), L.T("sign_msg"), L.T("sign_ok"))
        {
            DialogWidth = 560;
            Ink = new Segments(() => L.T("sign_black"), () => L.T("sign_blue")) { FontSize = 9f, Surface = Surface.Card };
            Ink.SelectedChanged += (s, e) => { Pad.Ink = Ink.SelectedIndex == 1 ? Color.FromArgb(25, 60, 170) : Color.FromArgb(20, 20, 30); Pad.Invalidate(); Refresh2(); };
            Where = hasSelection
                ? new Segments(() => L.T("sign_on_sel"), () => L.T("sign_on_last"), () => L.T("sign_on_all")) { FontSize = 9f }
                : new Segments(() => L.T("sign_on_last"), () => L.T("sign_on_first"), () => L.T("sign_on_all")) { FontSize = 9f };
            Place.Page = page;
            Pad.Changed += Refresh2;
            Add(null, new SignBox(this), 190);
            Add(() => L.T("sign_place"), Place, 250);
            Add(() => L.T("sign_on"), Where, 36);
            Validate2 = () => !Pad.Empty;
            bOk.Enabled = false;
        }

        void Refresh2()
        {
            if (Place.Sign != null) Place.Sign.Dispose();
            Place.Sign = Pad.Export();
            Place.Fit();
            bOk.Enabled = !Pad.Empty; bOk.Invalidate();
        }
    }
}
