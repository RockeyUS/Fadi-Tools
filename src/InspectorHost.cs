using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileConverter
{
    /// File inspector: what a file really is (details, preview, hex, text) and, for programs, reverse engineering.
    public class InspectorHost : Page
    {
        public readonly InspectPage Inspect;
        public readonly RePage Re;
        readonly Segments tabs;
        Page active;

        public InspectorHost() : base("insp_title", "insp_sub")
        {
            Inspect = new InspectPage();
            Re = new RePage { TitleKey = "insp_title", SubKey = "re_sub" };
            Re.Visible = false;
            Controls.Add(Inspect); Controls.Add(Re);
            active = Inspect;
            tabs = new Segments(() => L.T("insp_t_file"), () => L.T("insp_t_re")) { FontSize = 9f, Surface = Surface.Background };
            tabs.Glyphs = new[] { Ico.Search, Ico.Code };
            tabs.SelectedChanged += (s, e) => Show(tabs.SelectedIndex == 0 ? (Page)Inspect : Re);
            Controls.Add(tabs);
            // "Open in reverse engineer" from the details
            Inspect.SendToRe += f => { Show(Re); Re.AcceptFiles(new[] { f }); };
        }

        public void Show(Page p)
        {
            if (active == p) return;
            if (Visible && active != null) Fx.Crossfade(this, 180);
            active = p;
            Inspect.Visible = p == Inspect; Re.Visible = p == Re;
            tabs.SetQuietly(p == Inspect ? 0 : 1);
            DoLayout();
            p.OnShown();
        }

        public override bool Busy { get { return Inspect.Busy || Re.Busy; } }

        public override void DoLayout()
        {
            int tw = Math.Min(Theme.S(340), Math.Max(Theme.S(230), Width / 3));
            foreach (Page p in new Page[] { Inspect, Re })
            {
                p.Toast = Toast; p.Owner = Owner; p.Glyph = Glyph;
                p.HeaderReserve = tw + Theme.S(16);
                p.Bounds = ClientRectangle;
            }
            active.DoLayout();
            tabs.Glyphs = tw >= Theme.S(300) ? new[] { Ico.Search, Ico.Code } : null;
            tabs.Bounds = Theme.Mirror(new Rectangle(Width - Pad - tw, Theme.S(30), tw, Theme.S(38)), Width);
            tabs.BringToFront();
        }

        public override void Restyle() { Inspect.Restyle(); Re.Restyle(); Inspect.DoLayout(); Re.DoLayout(); }
        public override void OnShown() { active.OnShown(); }

        /// Programs and app packages can go straight to reverse engineering; anything else is inspected.
        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            if (active == Re) Re.AcceptFiles(paths); else Inspect.AcceptFiles(paths);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintBg(e.Graphics); }
    }
}
