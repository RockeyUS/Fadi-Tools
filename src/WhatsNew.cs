using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// "What's new" after an update: the main changes of this version, one row each.
    public static class WhatsNew
    {
        public const string ReleasesUrl = "https://github.com/RockeyUS/Fadi-Tools/releases";

        /// glyph, title key, text key
        public static readonly string[][] Items =
        {
            // 1.7.0
            new[] { "\uE7C4", "wn_ps170_t", "wn_ps170_d" },
            new[] { "\uF427", "wn_remove170_t", "wn_remove170_d" },
            new[] { Ico.Video, "wn_vs170_t", "wn_vs170_d" },
            new[] { "\uF440", "wn_keys170_t", "wn_keys170_d" },
            new[] { "\uE8BE", "wn_caps170_t", "wn_caps170_d" },
            new[] { "\uF40F", "wn_fixes170_t", "wn_fixes170_d" },
            // 1.6.4
            new[] { Ico.Contrast, "wn_look164_t", "wn_look164_d" },
            new[] { Ico.Search, "wn_empty164_t", "wn_empty164_d" },
            new[] { Ico.Doc, "wn_studio164_t", "wn_studio164_d" },
            // 1.6.3
            new[] { Ico.Edit, "wn_pdfedit_t", "wn_pdfedit_d" },
            new[] { Ico.Upscale, "wn_wheeltools_t", "wn_wheeltools_d" },
            new[] { "\uE73F", "wn_cmpslider_t", "wn_cmpslider_d" },
            new[] { Ico.Recipe, "wn_recipes_t", "wn_recipes_d" },
            new[] { "\uE7E7", "wn_startup_t", "wn_startup_d" },
            // 1.6.2
            new[] { Ico.Crop, "wn_crop_t", "wn_crop_d" },
            new[] { "\uE73F", "wn_cmpwin_t", "wn_cmpwin_d" },
            new[] { Ico.Convert, "wn_wheelsize_t", "wn_wheelsize_d" },
            // 1.6.1
            new[] { Ico.Convert, "wn_donut_t", "wn_donut_d" },
            new[] { "\uE7E7", "wn_traypanel_t", "wn_traypanel_d" },
            new[] { Ico.Photo, "wn_onemenu_t", "wn_onemenu_d" },
            new[] { Ico.Contrast, "wn_logo161_t", "wn_logo161_d" },
            // 1.6.0
            new[] { Ico.Convert, "wn_wheel_t", "wn_wheel_d" },
            new[] { Ico.Photo, "wn_icon_t", "wn_icon_d" },
            new[] { "\uE7E7", "wn_closetray_t", "wn_closetray_d" },
            new[] { Ico.Download, "wn_sizes_t", "wn_sizes_d" },
            new[] { Ico.Search, "wn_close_t", "wn_close_d" },
            new[] { Ico.Folder, "wn_where_t", "wn_where_d" },
            // 1.5.1
            new[] { "\uE7C4", "wn_tabs_t", "wn_tabs_d" },
            new[] { "\uE712", "wn_menu_t", "wn_menu_d" },
            new[] { "\uE706", "wn_live_t", "wn_live_d" },
            new[] { "\uE8EF", "wn_ocr2_t", "wn_ocr2_d" },
            new[] { Ico.Code, "wn_insp_t", "wn_insp_d" },
            new[] { Ico.Link, "wn_dlx_t", "wn_dlx_d" },
            new[] { "\uE7E7", "wn_tray2_t", "wn_tray2_d" },
            // 1.5.0
            new[] { Ico.Copy, "wn_dupes_t", "wn_dupes_d" },
            new[] { "\uE8EF", "wn_ocrpdf_t", "wn_ocrpdf_d" },
            new[] { "\uE916", "wn_rename_t", "wn_rename_d" },
            new[] { "\uE7E7", "wn_tray_t", "wn_tray_d" },
            new[] { "\uE71B", "wn_links_t", "wn_links_d" },
            new[] { "\uEA90", "wn_photospdf_t", "wn_photospdf_d" },
            new[] { "\uE8C8", "wn_copytext_t", "wn_copytext_d" },
            new[] { "\uE765", "wn_keys_t", "wn_keys_d" },
        };

        /// Shows the window once after the app was updated (not on a first install: the tour is shown then).
        public static void ShowIfUpdated(Form owner, bool tourShown)
        {
            string seen = Settings.Get("SeenVersion", "");
            Settings.Set("SeenVersion", Shell.Version);
            if (tourShown || seen == Shell.Version || Environment.GetEnvironmentVariable("FADI_NO_TOUR") != null) return;
            Show(owner);
        }

        public static void Show(Form owner)
        {
            using (WhatsNewForm f = new WhatsNewForm()) Ask.ShowDimmed(f, owner);
        }
    }

    class WhatsNewForm : Form
    {
        readonly Btn bOk, bAll;
        readonly WnList list = new WnList();
        Rectangle logoR, titleR, subR;
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

        public WhatsNewForm()
        {
            Movable.Make(this, () => Theme.S(100));   // drag it by the top
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Theme.Card; Font = Theme.F(10f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = L.T("wn_title", Shell.Version);
            int W = Theme.S(560), pad = Theme.S(28);
            int maxH = Math.Min(Theme.S(760), (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.85));
            logoR = new Rectangle(pad, pad, Theme.S(48), Theme.S(48));
            titleR = new Rectangle(pad + Theme.S(62), pad - Theme.S(2), W - pad * 2 - Theme.S(62), Theme.S(30));
            subR = new Rectangle(pad + Theme.S(62), pad + Theme.S(28), W - pad * 2 - Theme.S(62), Theme.S(22));
            int top = pad + Theme.S(66);
            int listH = Math.Min(WhatsNew.Items.Length * Theme.S(list.RowHeight), maxH - top - Theme.S(40) - pad * 2);
            list.Bounds = new Rectangle(Theme.S(14), top, W - Theme.S(28), listH);
            Controls.Add(list);
            int by = list.Bottom + Theme.S(18);
            bOk = new Btn(() => L.T("wn_ok"), null, BtnKind.Primary) { Surface = Surface.Card };
            bOk.Click += (s, e) => Close();
            bAll = new Btn(() => L.T("wn_all"), Ico.Open, BtnKind.Ghost) { Surface = Surface.Card, FontSize = 9f };
            bAll.Click += (s, e) => { try { System.Diagnostics.Process.Start(WhatsNew.ReleasesUrl); } catch { } };
            int ow = Math.Max(Theme.S(120), bOk.PreferredWidth + Theme.S(20)), aw = bAll.PreferredWidth;
            bOk.Bounds = Theme.Mirror(new Rectangle(W - pad - ow, by, ow, Theme.S(40)), W);
            bAll.Bounds = Theme.Mirror(new Rectangle(pad - Theme.S(10), by + Theme.S(3), aw, Theme.S(34)), W);
            Controls.Add(bOk); Controls.Add(bAll);
            ClientSize = new Size(W, by + Theme.S(40) + pad);
            Shown += (s, e) => { bOk.Focus(); list.Start(); };
        }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape || k == Keys.Enter) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.CardBorder)) g.DrawPath(p, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));
            int W = ClientSize.Width;
            Rectangle lr = Theme.Mirror(logoR, W);
            Theme.DrawLogo(g, lr);
            Theme.Text(g, L.T("wn_title", Shell.Version), Theme.Display(16f), Theme.Ink, Theme.Mirror(titleR, W), Align.Start);
            Theme.Text(g, L.T("wn_sub"), Theme.F(9.5f), Theme.InkSoft, Theme.Mirror(subR, W), Align.Start);
        }

        /// The changes, sliding in one after another.
        class WnList : ScrollList
        {
            float frame = -1;
            public WnList() { RowHeight = 72; Surface = Surface.Card; }
            public override int Count { get { return WhatsNew.Items.Length; } }
            public void Start() { frame = Fx.Motion ? 0 : 1000; Animator.Start(this); }
            public override bool Step()
            {
                bool more = base.Step();
                if (frame >= 0 && frame < Count * 3 + 14) { frame += Animator.K; Invalidate(); more = true; }
                return more;
            }

            protected override void DrawRow(Graphics g, int i, Rectangle r, bool hover)
            {
                float t = frame < 0 ? 0 : Math.Max(0, Math.Min(1, (frame - i * 3) / 12f));
                if (t <= 0) return;
                float ease = 1 - (float)Math.Pow(1 - t, 3);
                int dy = (int)((1 - ease) * Theme.S(14));
                r.Offset(0, dy);
                Theme.Smooth(g);
                string[] it = WhatsNew.Items[i];
                int a = (int)(255 * ease);
                Rectangle ic = Theme.Mirror(new Rectangle(r.X + Theme.S(14), r.Y + Theme.S(12), Theme.S(40), Theme.S(40)), Width);
                Theme.FillRound(g, ic, Theme.S(11), Color.FromArgb(a, Theme.AccentSoft));
                Theme.Glyph(g, it[0], 12f, Color.FromArgb(a, Theme.AccentColor), ic);
                Rectangle tr = Theme.Mirror(new Rectangle(r.X + Theme.S(68), r.Y + Theme.S(8), r.Width - Theme.S(84), Theme.S(22)), Width);
                Rectangle dr = Theme.Mirror(new Rectangle(r.X + Theme.S(68), r.Y + Theme.S(29), r.Width - Theme.S(84), Theme.S(42)), Width);
                Theme.Text(g, L.T(it[1]), Theme.F(10f, FontStyle.Bold), Color.FromArgb(a, Theme.Ink), tr, Align.Start);
                Theme.Text(g, L.T(it[2]), Theme.F(8.75f), Color.FromArgb(a, Theme.InkSoft), dr, Align.Start, false, true);
            }
        }
    }
}
