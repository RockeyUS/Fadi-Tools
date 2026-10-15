using System;
using System.Drawing;
using System.Windows.Forms;

namespace FileConverter
{
    /// One place for photos and videos: the photo viewer/editor and the video trimmer/editor share this page.
    public class StudioPage : Page
    {
        public readonly ViewerPage Photos;
        public readonly TrimPage Videos;
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
            Videos.Visible = false;
            Controls.Add(Photos); Controls.Add(Videos);
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
            Photos.Visible = p == Photos; Videos.Visible = p == Videos;
            DoLayout();
            p.OnShown();
        }

        public override bool Busy { get { return Videos.Busy; } }

        void UpdateClose()
        {
            bool v = active == Videos ? Videos.HasFile : Photos.HasFile;
            if (bClose.Visible != v) { bClose.Visible = v; if (v) bClose.BringToFront(); }
        }

        public override void DoLayout()
        {
            foreach (Page p in new Page[] { Photos, Videos })
            {
                p.Toast = Toast; p.Owner = Owner; p.Glyph = Glyph;
                p.Bounds = ClientRectangle;
            }
            active.DoLayout();
            int w = Math.Max(Theme.S(110), bClose.PreferredWidth + Theme.S(12));
            bClose.Bounds = Theme.Mirror(new Rectangle(Width - Pad - w, Theme.S(30), w, Theme.S(38)), Width);
            bClose.Surface = Surface.Background;
            bClose.BringToFront();
        }

        public override void Restyle() { Photos.Restyle(); Videos.Restyle(); Photos.DoLayout(); Videos.DoLayout(); }
        public override void OnShown() { active.OnShown(); }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
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
            if (active == Photos) return (Photos.Editing || !textBoxFocused) && Photos.HandleKey(k);
            return Videos.HandleKey(k);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintBg(e.Graphics); }
    }
}
