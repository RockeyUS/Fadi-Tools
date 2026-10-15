using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// A window that draws its own title bar (app colors, rounded buttons) but keeps Windows' dragging, snapping,
    /// resizing, shadow and minimize/maximize animations: only the caption is removed, the frame stays.
    public class ChromeForm : Form
    {
        public readonly TitleBar Bar;
        public static bool Native { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1, HTCAPTION = 2, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct NCCALCSIZE_PARAMS { public RECT r0, r1, r2; public IntPtr pos; }
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int h2, uint flags);

        public ChromeForm()
        {
            Bar = new TitleBar(this);
        }

        bool barShown = true;
        /// (Bar.Visible reads false while the window itself is still hidden, so this keeps the real choice.)
        public bool BarShown { get { return barShown; } set { barShown = value; Bar.Visible = value; } }
        public int BarHeight { get { return barShown ? Bar.Height : 0; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // ask Windows to recalculate the frame now that the caption is gone
            if (Native) try { SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0020 | 0x0002 | 0x0001 | 0x0004 | 0x0200); } catch { }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Bar.Invalidate();
        }
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); Bar.Invalidate(); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Bar.Invalidate(); }

        int MaxInset { get { try { return GetSystemMetrics(33) + GetSystemMetrics(92); } catch { return 8; } } }   // frame + padded border

        protected override void WndProc(ref Message m)
        {
            if (Native && FormBorderStyle != FormBorderStyle.None)
            {
                if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
                {
                    // keep the side and bottom frame (resizing, shadow) but let the client area start at the very top
                    NCCALCSIZE_PARAMS p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                    int top = p.r0.Top;
                    base.WndProc(ref m);
                    p = (NCCALCSIZE_PARAMS)Marshal.PtrToStructure(m.LParam, typeof(NCCALCSIZE_PARAMS));
                    p.r0.Top = top + (WindowState == FormWindowState.Maximized ? MaxInset : 0);
                    Marshal.StructureToPtr(p, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_NCHITTEST)
                {
                    base.WndProc(ref m);
                    if ((int)m.Result == HTCLIENT)
                    {
                        int lp = m.LParam.ToInt32();
                        Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                        int edge = Theme.S(5);
                        if (WindowState != FormWindowState.Maximized && FormBorderStyle == FormBorderStyle.Sizable && pt.Y < edge)
                            m.Result = (IntPtr)(pt.X < edge * 3 ? HTTOPLEFT : pt.X > ClientSize.Width - edge * 3 ? HTTOPRIGHT : HTTOP);
                        else if (barShown && Bar.Bounds.Contains(pt) && Bar.ButtonAt(Bar.PointToClient(PointToScreen(pt))) < 0)
                            m.Result = (IntPtr)HTCAPTION;
                    }
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    /// The app's own title bar: blends into the sidebar and page, with rounded minimize / maximize / close buttons.
    public class TitleBar : UiControl
    {
        readonly Form f;
        int hover = -1, press = -1;
        public bool CanMaximize = true, CanMinimize = true;
        public Func<string> Title;                 // optional text on the left
        public int SideWidth; public bool SideRight;   // part painted like the sidebar
        /// Light/dark switch at the far side from the window buttons.
        public event Action ThemeClicked;
        public bool ShowThemeButton { get { return ThemeClicked != null; } }
        const int ThemeSlot = 99;
        Rectangle ThemeCell { get { return Theme.Mirror(new Rectangle(Theme.S(6), 0, Theme.S(42), Height - 1), Width); } }
        Point dragFrom; bool dragging;             // (only used where Windows can't drag for us)

        const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;

        public TitleBar(Form owner)
        {
            f = owner;
            Height = Theme.S(36);
            Surface = Surface.Background;
        }

        int Count { get { return 1 + (CanMaximize ? 1 : 0) + (CanMinimize ? 1 : 0); } }
        /// 0 close, 1 maximize, 2 minimize (from the outer edge inwards).
        int Kind(int slot) { if (slot == 0) return 0; if (slot == 1) return CanMaximize ? 1 : 2; return 2; }
        Rectangle Cell(int slot)
        {
            int w = Theme.S(46);
            Rectangle r = new Rectangle(Width - (slot + 1) * w, 0, w, Height);
            return Theme.Mirror(r, Width);
        }
        public int ButtonAt(Point p)
        {
            for (int i = 0; i < Count; i++) if (Cell(i).Contains(p)) return i;
            if (ShowThemeButton && ThemeCell.Contains(p)) return ThemeSlot;
            return -1;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && ChromeForm.Native)
            {
                int lp = m.LParam.ToInt32();
                Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                bool top = pt.Y < Theme.S(5) && f.WindowState != FormWindowState.Maximized;
                if (top || ButtonAt(pt) < 0) { m.Result = (IntPtr)HTTRANSPARENT; return; }   // the window handles dragging and resizing
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { f.Location = new Point(f.Location.X + e.X - dragFrom.X, f.Location.Y + e.Y - dragFrom.Y); return; }
            int h = ButtonAt(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover >= 0) { hover = -1; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            press = ButtonAt(e.Location);
            if (press < 0 && !ChromeForm.Native && f.WindowState == FormWindowState.Normal) { dragging = true; dragFrom = e.Location; }
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            int was = press; press = -1; Invalidate();
            if (was < 0 || ButtonAt(e.Location) != was) return;
            if (was == ThemeSlot) { if (ThemeClicked != null) ThemeClicked(); return; }
            switch (Kind(was))
            {
                case 0: f.Close(); break;
                case 1: f.WindowState = f.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                case 2: f.WindowState = FormWindowState.Minimized; break;
            }
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (!ChromeForm.Native && CanMaximize && ButtonAt(e.Location) < 0)
                f.WindowState = f.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.PaintBackdrop(g, this);
            if (SideWidth > 0)
            {
                Rectangle sr = SideRight ? new Rectangle(Width - SideWidth, 0, SideWidth, Height) : new Rectangle(0, 0, SideWidth, Height);
                g.SetClip(sr);
                Theme.PaintSurface(g, this, Surface.Sidebar);
                g.ResetClip();
                using (Pen p = new Pen(Theme.SideBorder)) { int x = SideRight ? Width - SideWidth : SideWidth - 1; g.DrawLine(p, x, 0, x, Height); }
            }
            // a line that separates the top bar from the app
            using (Pen line = new Pen(Theme.SideBorder)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
            Theme.Smooth(g);
            bool active = Form.ActiveForm == f;
            if (ShowThemeButton)
            {
                Rectangle c = ThemeCell;
                Rectangle pill = new Rectangle(c.X + Theme.S(5), c.Y + Theme.S(5), c.Width - Theme.S(10), c.Height - Theme.S(10));
                bool hot = hover == ThemeSlot;
                if (hot) Theme.FillRound(g, pill, Theme.S(8), press == ThemeSlot ? Theme.Pressed : Theme.Hover);
                // the moon in light mode (switch to dark), the sun in dark mode
                Theme.Glyph(g, Theme.Dark ? "\uE706" : "\uE708", 9f, active || hot ? Theme.Ink : Theme.InkFaint, pill);
            }
            if (Title != null)
            {
                string t = Title();
                int x0 = SideWidth > 0 && !SideRight ? SideWidth : (ShowThemeButton ? Theme.S(48) : 0);
                Rectangle tr = new Rectangle(x0 + Theme.S(16), 0, Width - x0 - Count * Theme.S(46) - Theme.S(32), Height);
                Theme.Text(g, t, Theme.F(9f, FontStyle.Bold), active ? Theme.InkSoft : Theme.InkFaint, Theme.Mirror(tr, Width), Align.Start);
            }
            for (int i = 0; i < Count; i++)
            {
                Rectangle c = Cell(i);
                int kind = Kind(i);
                Rectangle pill = new Rectangle(c.X + Theme.S(5), c.Y + Theme.S(5), c.Width - Theme.S(10), c.Height - Theme.S(10));
                bool hot = hover == i, down = press == i && hot;
                Color ink = active ? Theme.Ink : Theme.InkFaint;
                if (hot)
                {
                    if (kind == 0) { Theme.FillRound(g, pill, Theme.S(8), down ? Color.FromArgb(200, 30, 40) : Color.FromArgb(232, 17, 35)); ink = Color.White; }
                    else Theme.FillRound(g, pill, Theme.S(8), down ? Theme.Pressed : Theme.Hover);
                }
                string glyph = kind == 0 ? "" : kind == 2 ? "" : (f.WindowState == FormWindowState.Maximized ? "" : "");
                Theme.Glyph(g, glyph, kind == 0 ? 8f : 8.5f, ink, pill);
            }
        }
    }
}
