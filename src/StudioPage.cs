using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace FileConverter
{
    /// One place for photos, PDFs and videos. The "Photos / PDF" side is the viewer/editor (photos, and PDFs page by
    /// page: add text and pictures, copy text, save) plus the PDF page tools (combine, reorder, sign, lock…);
    /// the "Videos" side is the video editor. Each photo or PDF opens in its own tab, so several can be worked on.
    public class StudioPage : Page
    {
        /// The photo / PDF tab that's showing (each tab is its own viewer).
        public ViewerPage Photos { get { return activePhoto; } }
        readonly List<ViewerPage> photoTabs = new List<ViewerPage>();
        ViewerPage activePhoto;
        public readonly TrimPage Videos;
        public readonly PdfPage Pdf;
        readonly Segments tabs;
        readonly FileTabs fileTabs;
        Page active;
        /// Which part of the "Photos / PDF" side was shown last (a photo tab or the PDF page tools).
        Page photoSide;
        readonly Btn bClose;
        readonly Timer poll = new Timer { Interval = 400 };
        public event Action<string> SendToConverter;

        public StudioPage() : base("studio_title", "studio_sub")
        {
            Videos = new TrimPage();
            Videos.OpenImage += f => OpenPhotos(new[] { f }, true);
            Pdf = new PdfPage { TitleKey = "studio_title", SubKey = "studio_pdf_sub" };
            Pdf.EditContent += (f, page) => OpenPdf(f, page);
            Videos.Visible = false; Pdf.Visible = false;
            Controls.Add(Videos); Controls.Add(Pdf);
            // the files open on the photo side, one tab each
            fileTabs = new FileTabs { Visible = false };
            fileTabs.Picked += t => Show((ViewerPage)t.Tag);
            fileTabs.CloseClicked += t => CloseTab((ViewerPage)t.Tag);
            fileTabs.AddClicked += () => { ViewerPage v = NewTab(); Show(v); };
            Controls.Add(fileTabs);
            activePhoto = NewTab();
            // the three sides of the Studio: photos, PDFs, videos
            tabs = new Segments(() => L.T("studio_t_photos"), () => "PDF", () => L.T("studio_t_videos")) { FontSize = 9f, Surface = Surface.Background };
            tabs.Glyphs = new[] { Ico.Photo, Ico.Doc, Ico.Video };
            tabs.SelectedChanged += (s, e) => Show(tabs.SelectedIndex == 2 ? (Page)Videos : tabs.SelectedIndex == 1 ? (Page)Pdf : activePhoto);
            Controls.Add(tabs);
            active = activePhoto; photoSide = activePhoto; activePhoto.Visible = true;
            // close the open photo, PDF or video (several photo tabs: close this tab)
            bClose = new Btn(() => L.T("studio_close"), Ico.Close, BtnKind.Secondary) { Visible = false };
            bClose.FontSize = 9f;
            bClose.Click += (s, e) =>
            {
                if (active == Pdf) { if (Pdf.PdfPath != null) Pdf.CloseFile(); UpdateClose(); return; }
                if (active == Videos) { if (Videos.CloseFile()) Show(activePhoto); UpdateClose(); return; }
                CloseTab(activePhoto);
            };
            Controls.Add(bClose);
            bClose.BringToFront();
            poll.Tick += (s, e) => { if (Visible) { UpdateClose(); fileTabs.Invalidate(); } };
            poll.Start();
        }

        /// A new, empty photo / PDF tab (not shown yet).
        ViewerPage NewTab()
        {
            ViewerPage v = new ViewerPage(true);
            v.OpenMedia += f => { Show(Videos); Videos.AcceptFiles(new[] { f }); };
            v.OpenProjectFile += f => { Show(Videos); Videos.OpenProject(f, false); };
            v.BrowseProjects += () => { string f = TrimPage.PickProject(FindForm()); if (f != null) { Show(Videos); Videos.OpenProject(f, false); } };
            v.SendToConverter += f => { if (SendToConverter != null) SendToConverter(f); };
            // from the viewer to the PDF page tools (with the open PDF, if there is one) and back
            v.OrganizePages += f =>
            {
                if (f != null) { if (!v.CloseAll()) return; Pdf.StartMode(1, false); Show(Pdf); Pdf.OpenPdf(f); if (photoTabs.Count > 1) DropTab(v); }
                else Show(Pdf);
            };
            v.DropTo = AcceptFiles;
            v.PdfPicked += f => { if (!v.HasFile && photoTabs.Count > 1) DropTab(v); OpenInPdfTools(f); };
            v.Visible = false;
            v.Toast = Toast; v.Owner = Owner; v.Glyph = Glyph;
            Controls.Add(v);
            photoTabs.Add(v);
            fileTabs.Add(new FileTabs.Tab { Title = () => v.TabTitle, Glyph = () => v.TabGlyph, Changed = () => v.HasUnsavedEdits, Tag = v }, false);
            if (Width > 0) DoLayout();
            return v;
        }

        FileTabs.Tab TabOf(ViewerPage v) { foreach (FileTabs.Tab t in fileTabs.Tabs) if (t.Tag == v && !t.Closing) return t; return null; }

        /// Closes a photo / PDF tab (asking first about unsaved changes). The last tab just empties.
        void CloseTab(ViewerPage v)
        {
            if (v == null) return;
            if (!v.CloseAll()) return;
            if (photoTabs.Count > 1) DropTab(v);
            UpdateClose(); DoLayout();
        }

        void DropTab(ViewerPage v)
        {
            int i = photoTabs.IndexOf(v);
            if (i < 0 || photoTabs.Count < 2) return;
            photoTabs.RemoveAt(i);
            FileTabs.Tab t = TabOf(v);
            if (t != null) fileTabs.Remove(t);
            if (activePhoto == v)
            {
                ViewerPage next = photoTabs[Math.Min(i, photoTabs.Count - 1)];
                activePhoto = next;
                if (photoSide == v) photoSide = next;
                if (active == v) Show(next);
            }
            Controls.Remove(v);
            BeginInvoke((Action)(() => { try { v.Dispose(); } catch { } }));
        }

        /// Where new photos / PDFs go: the tab that's showing when it's empty, otherwise a new tab.
        ViewerPage TabForNew()
        {
            ViewerPage v = activePhoto;
            if (v.HasFile || v.Busy) v = NewTab();
            return v;
        }

        /// A PDF opens with the PDF tools: its pages, and everything that can be done to them.
        public void OpenInPdfTools(string pdf)
        {
            if (Pdf.Busy) return;
            Pdf.StartMode(1, false);
            Show(Pdf);
            Pdf.OpenPdf(pdf);
            UpdateClose();
        }

        /// Opens photos (or a document) on the photo side; newTab = in a tab of their own when this one is in use.
        void OpenPhotos(string[] paths, bool newTab)
        {
            if (paths.Length == 1 && Formats.Ext(paths[0]) == "pdf") { OpenInPdfTools(paths[0]); return; }
            ViewerPage v = newTab ? TabForNew() : activePhoto;
            Show(v);
            if (paths.Length == 1 && DocPages.IsDoc(paths[0])) v.OpenDocument(paths[0]);
            else v.LoadFiles(paths);
        }

        public void Show(Page p)
        {
            ViewerPage vp = p as ViewerPage;
            if (vp != null) { activePhoto = vp; FileTabs.Tab t = TabOf(vp); if (t != null) fileTabs.Select(t); }
            if (active == p) return;
            if (Visible && active != null) Fx.Crossfade(this, 180);
            active = p;
            if (p != Videos) photoSide = p;
            foreach (ViewerPage v in photoTabs) v.Visible = v == p;
            Videos.Visible = p == Videos; Pdf.Visible = p == Pdf;
            tabs.SetQuietly(p == Videos ? 2 : p == Pdf ? 1 : 0);
            DoLayout();
            UpdateClose();
            p.OnShown();
        }

        /// Shows the part of the Studio a control belongs to (a job in Activity was clicked).
        public bool ShowPart(Control c)
        {
            foreach (ViewerPage v in photoTabs) if (c == v) { Show(v); return true; }
            if (c == Videos || c == Pdf) { Show((Page)c); return true; }
            return false;
        }

        /// Opens a PDF (page = the page to show first) in the viewer, where it can be read and edited page by page.
        public void OpenPdf(string path, int page)
        {
            ViewerPage v = TabForNew();
            Show(v);
            v.OpenDocument(path, page);
        }

        /// "Open" (or the quick search): pick a photo or document; it opens in a tab of its own when this one is in use.
        public void PickPhoto()
        {
            ViewerPage v = TabForNew();
            Show(v);
            v.PickFile();
            if (!v.HasFile && v != photoTabs[0] && photoTabs.Count > 1 && !v.Busy) BeginInvoke((Action)(() => { if (!v.HasFile) DropTab(v); }));
        }

        /// Brings back a photo edit kept after a crash (in a tab of its own when one is in use).
        public void RestorePhotoEdit(string file) { ViewerPage v = TabForNew(); Show(v); v.RestoreEdit(file); }

        public override bool Busy { get { if (Videos.Busy || Pdf.Busy) return true; foreach (ViewerPage v in photoTabs) if (v.Busy) return true; return false; } }

        bool TabsShown { get { return active is ViewerPage && (photoTabs.Count > 1 || activePhoto.HasFile); } }

        void UpdateClose()
        {
            bool v = active == Pdf ? Pdf.PdfPath != null : active == Videos ? Videos.HasFile : (activePhoto.HasFile || photoTabs.Count > 1);
            bClose.Glyph = Ico.Close;
            bool layout = false;
            if (bClose.Wanted != v) { bClose.Visible = v; if (v) bClose.BringToFront(); layout = true; }
            else bClose.Invalidate();
            if (fileTabs.Wanted != TabsShown) layout = true;
            if (layout) DoLayout();
        }

        public override void DoLayout()
        {
            if (fileTabs == null || activePhoto == null) return;
            int tw = Math.Min(Theme.S(360), Math.Max(Theme.S(250), Width / 4));
            int w = Math.Max(Theme.S(110), bClose.PreferredWidth + Theme.S(12));
            bool showTabs = TabsShown;
            List<Page> all = new List<Page>(photoTabs); all.Add(Videos); all.Add(Pdf);
            foreach (Page p in all)
            {
                p.Toast = Toast; p.Owner = Owner; p.Glyph = Glyph;
                p.HeaderReserve = tw + (bClose.Wanted ? w + Theme.S(10) : 0) + Theme.S(24);
                p.TopExtra = p is ViewerPage && showTabs ? Theme.S(44) : 0;
                p.Bounds = ClientRectangle;
            }
            active.DoLayout();
            tabs.Glyphs = tw >= Theme.S(300) ? new[] { Ico.Photo, Ico.Doc, Ico.Video } : null;
            tabs.Bounds = Theme.Mirror(new Rectangle(Width - Pad - tw, Theme.S(30), tw, Theme.S(38)), Width);
            bClose.Bounds = Theme.Mirror(new Rectangle(Width - Pad - tw - Theme.S(10) - w, Theme.S(30), w, Theme.S(38)), Width);
            bClose.Surface = Surface.Background;
            fileTabs.Visible = showTabs;
            fileTabs.Bounds = Theme.Mirror(new Rectangle(Pad - Theme.S(3), Theme.S(92), Width - Pad * 2 + Theme.S(6), Theme.S(40)), Width);
            tabs.BringToFront();
            bClose.BringToFront();
            fileTabs.BringToFront();
        }

        public override void Restyle() { foreach (Page p in AllParts()) { p.Restyle(); p.DoLayout(); } fileTabs.Invalidate(); }
        public override void OnShown() { active.OnShown(); }
        IEnumerable<Page> AllParts() { foreach (ViewerPage v in photoTabs) yield return v; yield return Videos; yield return Pdf; }

        public override void AcceptFiles(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            // in the PDF tools, PDFs and photos go into them (combine / edit pages)
            if (active == Pdf)
            {
                bool fits = true;
                foreach (string p in paths) { string e = Formats.Ext(p); if (e != "pdf" && Formats.CategoryOf(e) != Category.Image) fits = false; }
                if (fits) { Pdf.AcceptFiles(paths); return; }
            }
            // several PDFs: combined in the PDF tools; one opens in the viewer (below)
            bool allPdf = paths.Length > 1;
            foreach (string p in paths) if (Formats.Ext(p) != "pdf") allPdf = false;
            if (allPdf) { Pdf.StartMode(0, false); Show(Pdf); Pdf.AcceptFiles(paths); return; }
            // one PDF: straight into the PDF tools (pages, Edit text, Add text & pictures, sign…), all in one place
            if (paths.Length == 1 && Formats.Ext(paths[0]) == "pdf") { OpenInPdfTools(paths[0]); return; }
            if (Formats.Ext(paths[0]) == TrimPage.ProjectExt) { Show(Videos); Videos.OpenProject(paths[0], false); return; }
            Category c = Formats.CategoryOf(Formats.Ext(paths[0]));
            // pictures dropped on a video being edited go on top of it
            if (active == Videos && c == Category.Image && Videos.AddPictures(paths)) return;
            if (c == Category.Video || c == Category.Audio) { Show(Videos); Videos.AcceptFiles(paths); return; }
            // pictures dropped on a photo or page being edited go on top of it
            if (active == activePhoto && activePhoto.Editing && c == Category.Image)
            {
                bool pics = true;
                foreach (string p in paths) if (Formats.CategoryOf(Formats.Ext(p)) != Category.Image) pics = false;
                if (pics) { activePhoto.LoadFiles(paths); return; }
            }
            // anything else opens in a tab of its own (the open one stays as it is)
            OpenPhotos(paths, true);
        }

        public bool ConfirmClose()
        {
            foreach (ViewerPage v in photoTabs)
                if (v.HasUnsavedEdits) { Show(v); if (!v.ConfirmDiscard()) return false; }
            if (Videos.HasUnsavedText && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("ed_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return false;
            return true;
        }

        public bool HandleKey(Keys k, bool textBoxFocused)
        {
            if (active == Pdf) return false;
            if (active is ViewerPage)
            {
                // Ctrl+W closes the tab, Ctrl+T opens a new one
                if (!activePhoto.Editing && k == (Keys.Control | Keys.W) && (photoTabs.Count > 1 || activePhoto.HasFile)) { CloseTab(activePhoto); return true; }
                if (!activePhoto.Editing && k == (Keys.Control | Keys.T)) { Show(NewTab()); return true; }
                return (activePhoto.Editing || !textBoxFocused) && activePhoto.HandleKey(k);
            }
            return Videos.HandleKey(k);
        }

        protected override void OnPaint(PaintEventArgs e) { PaintBg(e.Graphics); }
    }
}
