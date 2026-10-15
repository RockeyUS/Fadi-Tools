using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileConverter
{
    /// One place for photos, videos and PDFs: the photo viewer/editor, the video editor and the PDF tools share this page.
    public class StudioPage : Page
    {
        public readonly ViewerPage Photos;
        public readonly TrimPage Videos;
        public readonly PdfPage Pdf;
        readonly Segments tabs;
        Page active;
        readonly Btn bClose;
        readonly Timer poll = new Timer { Interval = 400 };

        public StudioPage() : base("studio_title", "studio_sub")
        {
            Photos = new ViewerPage(true);
            Videos = new TrimPage();
            Photos.OpenMedia += f => { Show(Videos); Videos.AcceptFiles(new[] { f }); };
            Videos.OpenImage += f => { Show(Photos); Photos.LoadFiles(new[] { f }); };
            Photos.OpenProjectFile += f => { Show(Videos); Videos.OpenProject(f, false); };
            Photos.BrowseProjects += () => { string f = TrimPage.PickProject(FindForm()); if (f != null) { Show(Videos); Videos.OpenProject(f, false); } };
            Pdf = new PdfPage { TitleKey = "studio_title", SubKey = "studio_pdf_sub" };
            Videos.Visible = false; Pdf.Visible = false;
            Controls.Add(Photos); Controls.Add(Videos); Controls.Add(Pdf);
            // the three sides of the Studio
            tabs = new Segments(() => L.T("studio_t_photos"), () => L.T("studio_t_videos"), () => L.T("studio_t_pdf")) { FontSize = 9f, Surface = Surface.Background };
            tabs.Glyphs = new[] { Ico.Photo, Ico.Video, "\uEA90" };
            tabs.SelectedChanged += (s, e) => Show(new Page[] { Photos, Videos, Pdf }[tabs.SelectedIndex]);
            Controls.Add(tabs);
            active = Photos;
            // close the open photo/video and go back to the empty Studio
            bClose = new Btn(() => L.T("studio_close"), Ico.Close, BtnKind.Secondary) { Visible = false };
            bClose.FontSize = 9f;
            bClose.Click += (s, e) =>
            {
                bool ok = active == Videos ? Videos.CloseFile() : Photos.CloseAll();
                if (ok && active == Videos) Show(Photos);
                UpdateClose();
            };
            Controls.Add(bClose);
            bClose.BringToFront();
            poll.Tick += (s, e) => { if (Visible) UpdateClose(); };
            poll.Start();
        }

        public void Show(Page p)
        {
            if (active == p) return;
            if (Visible && active != null) Fx.Crossfade(this, 180);
            active = p;
            Photos.Visible = p == Photos; Videos.Visible = p == Videos; Pdf.Visible = p == Pdf;
            tabs.SetQuietly(p == Photos ? 0 : p == Videos ? 1 : 2);
            DoLayout();
            p.OnShown();
        }

        public override bool Busy { get { return Videos.Busy || Pdf.Busy; } }

        void UpdateClose()
        {
            bool v = active == Pdf ? false : active == Videos ? Videos.HasFile : Photos.HasFile;
            if (bClose.Visible != v) { bClose.Visible = v; if (v) bClose.BringToFront(); }
        }

        public override void DoLayout()
        {
            int tw = Math.Min(Theme.S(360), Math.Max(Theme.S(240), Width / 3));
            int w = Math.Max(Theme.S(110), bClose.PreferredWidth + Theme.S(12));
            foreach (Page p in new Page[] { Photos, Videos, Pdf })
            {
                p.Toast = Toast; p.Owner = Owner; p.Glyph = Glyph;
                p.HeaderReserve = tw + w + Theme.S(24);
                p.Bounds = ClientRectangle;
            }
            active.DoLayout();
            tabs.Glyphs = tw >= Theme.S(330) ? new[] { Ico.Photo, Ico.Video, "\uEA90" } : null;
            tabs.Bounds = Theme.Mirror(new Rectangle(Width - Pad - tw, Theme.S(30), tw, Theme.S(38)), Width);
            bClose.Bounds = Theme.Mirror(new Rectangle(Width - Pad - tw - Theme.S(10) - w, Theme.S(30), w, Theme.S(38)), Width);
            bClose.Surface = Surface.Background;
            tabs.BringToFront();
            bClose.BringToFront();
        }

        public override void Restyle() { Photos.Restyle(); Videos.Restyle(); Pdf.Restyle(); Photos.DoLayout(); Videos.DoLayout(); Pdf.DoLayout(); }
        public override void OnShown() { active.OnShown(); }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            // on the PDF side, PDFs and photos go into the PDF tools (combine / edit pages)
            if (active == Pdf)
            {
                bool fits = true;
                foreach (string p in paths) { string e = Formats.Ext(p); if (e != "pdf" && Formats.CategoryOf(e) != Category.Image) fits = false; }
                if (fits) { Pdf.AcceptFiles(paths); return; }
            }
            if (Formats.Ext(paths[0]) == TrimPage.ProjectExt) { Show(Videos); Videos.OpenProject(paths[0], false); return; }
            Category c = Formats.CategoryOf(Formats.Ext(paths[0]));
            // pictures dropped on a video being edited go on top of it
            if (active == Videos && c == Category.Image && Videos.AddPictures(paths)) return;
            if (c == Category.Video || c == Category.Audio) { Show(Videos); Videos.AcceptFiles(paths); }
            else if (DocPages.IsDoc(paths[0])) { Show(Photos); Photos.OpenDocument(paths[0]); }
            else { Show(Photos); Photos.LoadFiles(paths); }
        }

        public bool ConfirmClose()
        {
            if (!Photos.ConfirmDiscard()) return false;
            if (Videos.HasUnsavedText && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("ed_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return false;
            return true;
        }

        public bool HandleKey(Keys k, bool textBoxFocused)
        {
            if (active == Pdf) return false;
            if (active == Photos) return (Photos.Editing || !textBoxFocused) && Photos.HandleKey(k);
            return Videos.HandleKey(k);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintBg(e.Graphics); }
    }
}
