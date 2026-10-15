using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Video projects: everything on the timeline (clips, cuts, text, effects, music, subtitles) saved to a
    /// .fadiproj file you can open again later. The same format is used for the automatic minute-by-minute backup.
    public partial class TrimPage
    {
        string projectPath;
        System.Windows.Forms.Timer autoTimer;
        public string ProjectPath { get { return projectPath; } }
        public const string ProjectExt = "fadiproj";
        public static string ProjectsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Fadi Tools", "Projects"); } }

        void InitProjects()
        {
            autoTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            autoTimer.Tick += (s, e) => AutoSave();
            autoTimer.Start();
            Recovery.SaveNowRequested += () => { try { AutoSave(); } catch { } };
        }

        /// Writes the unsaved edit to the backup file (every minute, and once more if the app crashes).
        void AutoSave()
        {
            if (clips.Count == 0 || !editor.S.Dirty) return;
            Recovery.Write(Recovery.VideoFile, ProjectJson());
        }

        public void ForgetAutoSave() { Recovery.Clear(Recovery.VideoFile); }

        // ------------------------------------------------------------------ writing
        string ProjectJson()
        {
            EditSession s = editor.S;
            string baseDir = projectPath != null ? Path.GetDirectoryName(projectPath) : null;
            StringBuilder b = new StringBuilder();
            b.Append("{\n  \"app\": \"Fadi Tools\", \"kind\": \"video\", \"version\": 1,\n");
            b.Append("  \"saved\": ").Append(Js.Q(DateTime.Now.ToString("o"))).Append(", \"path\": ").Append(Js.Q(projectPath)).Append(",\n");
            b.Append("  \"clips\": [");
            for (int i = 0; i < clips.Count; i++)
            {
                Clip c = clips[i];
                string rel = null;
                try { if (baseDir != null) rel = RelativePath(baseDir, c.File); } catch { }
                b.Append(i > 0 ? ",\n    " : "\n    ").Append("{\"file\": ").Append(Js.Q(c.File)).Append(", \"rel\": ").Append(Js.Q(rel))
                 .Append(", \"start\": ").Append(Js.N(c.Start)).Append(", \"end\": ").Append(Js.N(c.End)).Append('}');
            }
            b.Append("\n  ],\n");
            b.Append("  \"cur\": ").Append(cur).Append(", \"playhead\": ").Append(Js.N(timeline.Playhead)).Append(",\n");
            b.Append("  \"w\": ").Append(s.W).Append(", \"h\": ").Append(s.H).Append(", \"ow\": ").Append(s.OW).Append(", \"oh\": ").Append(s.OH)
             .Append(", \"crop\": [").Append(s.Crop.X).Append(',').Append(s.Crop.Y).Append(',').Append(s.Crop.Width).Append(',').Append(s.Crop.Height).Append("],\n");
            b.Append("  \"texts\": [");
            for (int i = 0; i < s.Layers.Count; i++) b.Append(i > 0 ? ",\n    " : "\n    ").Append(Js.Layer(s.Layers[i]));
            b.Append(s.Layers.Count > 0 ? "\n  ],\n" : "],\n");
            b.Append("  \"mode\": ").Append(mode.SelectedIndex).Append(", \"accuracy\": ").Append(accuracy.SelectedIndex)
             .Append(", \"transition\": ").Append(Js.Q(TransKeys[TransIndex])).Append(", \"fxDur\": ").Append(Js.N(fxDur.Value))
             .Append(", \"fadeIn\": ").Append(Js.B(fadeIn.Checked)).Append(", \"fadeOut\": ").Append(Js.B(fadeOut.Checked)).Append(", \"volume\": ").Append(Js.N(volume.Value)).Append(",\n");
            b.Append("  \"speed\": ").Append(speedSeg.SelectedIndex).Append(", \"reverse\": ").Append(Js.B(reverse.Checked))
             .Append(", \"music\": ").Append(Js.Q(musicFile)).Append(", \"musicMode\": ").Append(musicMode.SelectedIndex).Append(", \"musicVol\": ").Append(Js.N(musicVol.Value)).Append(",\n");
            b.Append("  \"subs\": ").Append(Js.Q(subsFile)).Append(", \"subsAuto\": ").Append(Js.B(subsAuto)).Append(", \"subsSize\": ").Append(subsSize.SelectedIndex).Append(", \"saveAs\": ").Append(saveAs.SelectedIndex).Append("\n}\n");
            return b.ToString();
        }

        static string RelativePath(string fromDir, string file)
        {
            Uri a = new Uri(fromDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar), b = new Uri(file);
            if (a.Scheme != b.Scheme) return null;
            return Uri.UnescapeDataString(a.MakeRelativeUri(b).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        public void SaveProject(bool saveAs)
        {
            if (clips.Count == 0) return;
            string path = projectPath;
            if (path == null || saveAs || !Directory.Exists(Path.GetDirectoryName(path)))
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.Title = L.T("proj_save_title");
                    d.Filter = L.T("proj_files") + " (*." + ProjectExt + ")|*." + ProjectExt;
                    try { Directory.CreateDirectory(ProjectsDir); } catch { }
                    d.InitialDirectory = path != null ? Path.GetDirectoryName(path) : ProjectsDir;
                    string stem = path != null ? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(clips[0].File);
                    foreach (char ch in Path.GetInvalidFileNameChars()) stem = stem.Replace(ch, '_');
                    d.FileName = stem + "." + ProjectExt;
                    d.OverwritePrompt = true;
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    path = d.FileName;
                }
            }
            try
            {
                string keep = projectPath; projectPath = path;
                string json = ProjectJson();
                File.WriteAllText(path, json, new UTF8Encoding(false));
                editor.S.Dirty = false;
                ForgetAutoSave();
                AddRecent(path);
                ShowToast(L.T("proj_saved", Path.GetFileNameWithoutExtension(path)), true, () => L.T("show_file"), () => ActivityRows.ShowOutput(path));
                Invalidate();
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        // ------------------------------------------------------------------ recent projects
        public static List<string> RecentProjects
        {
            get
            {
                List<string> l = new List<string>();
                foreach (string p in Settings.Get("RecentProjects", "").Split('|'))
                    if (p.Length > 0 && File.Exists(p) && !l.Contains(p)) l.Add(p);
                return l;
            }
        }
        static void AddRecent(string path)
        {
            List<string> l = RecentProjects;
            l.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            l.Insert(0, path);
            if (l.Count > 8) l.RemoveRange(8, l.Count - 8);
            Settings.Set("RecentProjects", string.Join("|", l.ToArray()));
        }
        public static string Ago(DateTime t)
        {
            TimeSpan d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return L.T("act_just_now");
            if (d.TotalHours < 1) return L.T("act_min_ago", (int)d.TotalMinutes);
            if (d.TotalDays < 1) return L.T("proj_hours_ago", (int)d.TotalHours);
            if (d.TotalDays < 7) return L.T("proj_days_ago", (int)d.TotalDays);
            return t.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }

        public void BrowseProject() { string f = PickProject(FindForm()); if (f != null) OpenProject(f, false); }

        /// Asks which project to open (null = cancelled).
        public static string PickProject(IWin32Window owner)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("proj_open_title");
                d.Filter = L.T("proj_files") + " (*." + ProjectExt + ")|*." + ProjectExt;
                try { if (Directory.Exists(ProjectsDir)) d.InitialDirectory = ProjectsDir; } catch { }
                return d.ShowDialog(owner) == DialogResult.OK ? d.FileName : null;
            }
        }

        /// The "Project" button's menu: save, save as, open, and the recent ones.
        void ShowProjectMenu(Control anchor)
        {
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            bool has = clips.Count > 0;
            m.Add(new MenuPopup.Item { Text = L.T("proj_save"), Sub = projectPath != null ? Path.GetFileName(projectPath) : L.T("proj_not_saved"), Glyph = Ico.Save, Run = () => SaveProject(false), Disabled = !has || running });
            m.Add(new MenuPopup.Item { Text = L.T("proj_save_as"), Glyph = Ico.Copy, Run = () => SaveProject(true), Disabled = !has || running });
            m.Add(new MenuPopup.Item { Text = L.T("proj_open"), Glyph = Ico.FolderOpen, Run = BrowseProject, Disabled = running });
            List<string> rec = RecentProjects;
            if (rec.Count > 0)
            {
                m.Add(new MenuPopup.Item { Divider = true });
                m.Add(new MenuPopup.Item { Header = L.T("proj_recent") });
                foreach (string p in rec)
                {
                    string pp = p;
                    DateTime t = DateTime.Now; try { t = File.GetLastWriteTime(p); } catch { }
                    m.Add(new MenuPopup.Item { Text = Path.GetFileNameWithoutExtension(p), Sub = Ago(t), Glyph = "", Run = () => OpenProject(pp, false), Disabled = running });
                }
            }
            MenuPopup.Show(anchor, m, 300);
        }

        // ------------------------------------------------------------------ opening
        /// Opens a saved project (or the automatic backup, when recovered = true).
        public void OpenProject(string path, bool recovered)
        {
            if (running) return;
            object root;
            try { root = new JsonParser(File.ReadAllText(path, Encoding.UTF8)).Parse(); }
            catch (Exception ex) { ShowToast(L.T("proj_bad", ex.Message), false, null, null); return; }
            if (Js.S(root, "kind") != "video") { ShowToast(L.T("proj_bad", "kind"), false, null, null); return; }
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            // find every clip's file (next to the project if it was moved together with it)
            List<object> list = Js.A(root, "clips");
            List<string> files = new List<string>(), missing = new List<string>();
            foreach (object c in list)
            {
                string f = Js.S(c, "file"), rel = Js.S(c, "rel");
                if ((f == null || !File.Exists(f)) && rel != null) { string r = Path.GetFullPath(Path.Combine(dir, rel)); if (File.Exists(r)) f = r; }
                files.Add(f != null && File.Exists(f) ? f : null);
                if ((f == null || !File.Exists(f)) && f != null && !missing.Contains(Path.GetFileName(f))) missing.Add(Path.GetFileName(f));
            }
            if (!files.Exists(f => f != null)) { ShowToast(L.T("proj_all_missing"), false, null, null); return; }
            if (missing.Count > 0 && !Ask.Confirm(FindForm(), L.T("proj_missing_title"), L.T("proj_missing", string.Join(", ", missing.ToArray())), L.T("proj_open_anyway"), L.T("cancel"), AskKind.Warning)) return;
            if (clips.Count > 0 && editor.S.Dirty && !recovered && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("ed_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return;
            Unload();
            int token = ++loadToken;
            SetStatus(L.T("proj_opening"), Theme.InkSoft);
            ShowToast(L.T("proj_opening"), true, null, null);
            Thread t = new Thread(() =>
            {
                // one source per file, probed once
                Dictionary<string, MediaSource> srcs = new Dictionary<string, MediaSource>(StringComparer.OrdinalIgnoreCase);
                Image first = null;
                foreach (string f in files)
                {
                    if (f == null || srcs.ContainsKey(f)) continue;
                    MediaInfo mi = null; Image fr = null;
                    try { mi = Media.Probe(f); } catch { }
                    if (mi == null || (!mi.HasVideo && !mi.HasAudio) || mi.Duration <= 0) { srcs[f] = null; continue; }
                    if (mi.HasVideo) fr = Media.Frame(f, Math.Min(1, mi.Duration / 10), 540);
                    int w = mi.Width, h = mi.Height;
                    if (fr != null && w > 0 && h > 0)
                    {
                        float ar = fr.Width / (float)fr.Height;
                        if (Math.Abs(ar - h / (float)w) < Math.Abs(ar - w / (float)h)) { int tmp = w; w = h; h = tmp; }
                    }
                    else if (fr != null) { w = fr.Width; h = fr.Height; }
                    if (w <= 0) { w = 1280; h = 720; }
                    w += w % 2; h += h % 2;
                    srcs[f] = new MediaSource { File = f, Info = mi, W = w, H = h, Strip = new Image[StripFrames] };
                    if (first == null) first = fr; else if (fr != null) fr.Dispose();
                }
                Ui(() =>
                {
                    if (token != loadToken) { if (first != null) first.Dispose(); return; }
                    SetStatus("", Theme.InkSoft);
                    List<Clip> made = new List<Clip>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        MediaSource src; if (files[i] == null || !srcs.TryGetValue(files[i], out src) || src == null) continue;
                        double a = Math.Max(0, Js.D(list[i], "start")), b = Math.Min(src.Info.Duration, Js.D(list[i], "end", src.Info.Duration));
                        if (b - a < 0.05) { a = 0; b = src.Info.Duration; }
                        made.Add(new Clip { Src = src, Start = a, End = b });
                    }
                    if (made.Count == 0) { if (first != null) first.Dispose(); ShowToast(L.T("proj_all_missing"), false, null, null); return; }
                    // mixing videos and songs isn't possible: keep the kind of the first clip
                    bool vid = made[0].Info.HasVideo;
                    made.RemoveAll(c => c.Info.HasVideo != vid);
                    foreach (MediaSource ms in srcs.Values) if (ms != null && made.Exists(c => c.Src == ms)) sources.Add(ms);
                    clips.Clear(); clips.AddRange(made);
                    MediaSource s0 = made[0].Src;
                    editor.BeginVideo(s0.W, s0.H, s0.File, !vid);
                    view.Layers = vid ? editor.S.Layers : null;
                    // size, crop and text
                    EditSession s = editor.S;
                    int W = Js.I(root, "w", s.W), H = Js.I(root, "h", s.H);
                    List<object> cr = Js.A(root, "crop");
                    Rectangle crop = Rectangle.Empty;
                    if (cr.Count == 4)
                    {
                        Func<object, int> num = o => o is long ? (int)(long)o : o is double ? (int)(double)o : 0;
                        crop = new Rectangle(num(cr[0]), num(cr[1]), num(cr[2]), num(cr[3]));
                    }
                    if (Js.I(root, "ow", s.OW) == s.OW && Js.I(root, "oh", s.OH) == s.OH && W > 0 && H > 0) s.SetGeometry(W, H, crop);
                    foreach (object o in Js.A(root, "texts")) { TextLayer tl = Js.Layer(o); if (tl != null) s.Layers.Add(tl); }
                    // trim and effects
                    mode.SetSilently(Math.Max(0, Math.Min(1, Js.I(root, "mode"))));
                    accuracy.SetSilently(Math.Max(0, Math.Min(1, Js.I(root, "accuracy"))));
                    accuracy.Enabled = mode.SelectedIndex == 0;
                    int ti = Math.Max(0, Array.IndexOf(TransKeys, Js.S(root, "transition", "none")));
                    transition.SetItems(TransNames, TransNames[ti]);
                    fxDur.SetValue((float)Js.D(root, "fxDur", 0.8), false);
                    fadeIn.SetSilently(Js.Bo(root, "fadeIn")); fadeOut.SetSilently(Js.Bo(root, "fadeOut"));
                    volume.SetValue((float)Js.D(root, "volume", 100), false);
                    speedSeg.SetSilently(Math.Max(0, Math.Min(Speeds.Length - 1, Js.I(root, "speed", 2))));
                    reverse.SetSilently(Js.Bo(root, "reverse"));
                    musicFile = Js.S(root, "music"); if (musicFile != null && !File.Exists(musicFile)) musicFile = null;
                    musicMode.SetSilently(Math.Max(0, Math.Min(1, Js.I(root, "musicMode"))));
                    musicVol.SetValue((float)Js.D(root, "musicVol", 80), false);
                    subsFile = Js.S(root, "subs"); if (subsFile != null && !File.Exists(subsFile)) subsFile = null;
                    subsAuto = subsFile != null && Js.Bo(root, "subsAuto");
                    subsSize.SetSilently(Math.Max(0, Math.Min(2, Js.I(root, "subsSize", 1))));
                    saveAs.SetSilently(Math.Max(0, Math.Min(1, Js.I(root, "saveAs"))));
                    // picture and timeline
                    editor.SetFrame(first != null ? new Bitmap(first) : AudioArt());
                    if (first != null) first.Dispose();
                    editor.SyncCanvas();
                    editor.Select(-1);
                    view.Reset();
                    cur = Math.Max(0, Math.Min(clips.Count - 1, Js.I(root, "cur")));
                    lastOutput = null; btnShow.Visible = false; progress.Visible = false;
                    ShowEmpty(false);
                    timeline.Playhead = Math.Max(0, Math.Min(timeline.Total, Js.D(root, "playhead")));
                    ShowClip();
                    foreach (MediaSource ms in sources)
                    {
                        Clip c0 = clips.Find(c => c.Src == ms);
                        if (c0 == null) continue;
                        if (vid) { LoadStrip(c0, loadToken); if (ms.Info.HasAudio) LoadWave(c0); } else LoadWave(c0);
                    }
                    double local; timeline.ClipAt(timeline.Playhead, out local);
                    RequestFrame(local, false, false);
                    projectPath = recovered ? Js.S(root, "path") : Path.GetFullPath(path);
                    if (projectPath != null && !File.Exists(projectPath)) projectPath = null;
                    s.Dirty = recovered;
                    if (!recovered) AddRecent(projectPath);
                    editor.DoLayout(); DoLayout();
                    ShowToast(recovered ? L.T("proj_restored") : L.T("proj_opened", Path.GetFileNameWithoutExtension(path)), true, null, null);
                    if (recovered) AutoSave();
                });
            });
            t.IsBackground = true; t.Start();
        }
    }
}

namespace FileConverter
{
    /// "Recent video projects" chips shown in the empty Studio.
    public class RecentStrip : UiControl
    {
        public List<string> Paths = new List<string>();
        public event Action<string> Open;
        public event Action Browse;
        int hover = -1;
        public RecentStrip() { Surface = Surface.Background; Cursor = Cursors.Hand; }

        int Count { get { return Math.Min(Paths.Count, Math.Max(0, (Width - Theme.S(150)) / Theme.S(206))); } }
        Rectangle Chip(int i)   // i == Count → "Open project…"
        {
            int w = i == Count ? Theme.S(150) : Theme.S(198), x = i * Theme.S(206);
            return Theme.Mirror(new Rectangle(x, Theme.S(26), w, Height - Theme.S(28)), Width);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1; for (int i = 0; i <= Count; i++) if (Chip(i).Contains(e.Location)) h = i;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i <= Count; i++)
                if (Chip(i).Contains(e.Location)) { if (i == Count) { if (Browse != null) Browse(); } else if (Open != null) Open(Paths[i]); return; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Theme.Text(g, L.T("proj_recent_videos").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, Theme.Mirror(new Rectangle(Theme.S(2), 0, Width, Theme.S(20)), Width), Align.Start);
            for (int i = 0; i <= Count; i++)
            {
                Rectangle c = Chip(i);
                bool hot = i == hover;
                RectangleF r = new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1);
                Theme.FillRound(g, r, Theme.S(10), hot ? Theme.Hover : Theme.Card);
                Theme.StrokeRound(g, r, Theme.S(10), hot ? Theme.InkFaint : Theme.CardBorder);
                Rectangle ic = Theme.Mirror(new Rectangle((Theme.RTL ? Width - c.Right : c.X) + Theme.S(10), c.Y + (c.Height - Theme.S(32)) / 2, Theme.S(32), Theme.S(32)), Width);
                Theme.FillRound(g, ic, Theme.S(8), i == Count ? Theme.CardInner : Theme.AccentSoft);
                Theme.Glyph(g, i == Count ? Ico.FolderOpen : "", 9.5f, i == Count ? Theme.InkSoft : Theme.AccentColor, ic);
                int tx = (Theme.RTL ? Width - c.Right : c.X) + Theme.S(50), tw = c.Width - Theme.S(58);
                if (i == Count)
                {
                    Theme.Text(g, L.T("proj_open_short"), Theme.F(9f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(tx, c.Y, tw, c.Height), Width), Align.Start, true, false);
                    continue;
                }
                DateTime t = DateTime.Now; try { t = File.GetLastWriteTime(Paths[i]); } catch { }
                Theme.Text(g, Path.GetFileNameWithoutExtension(Paths[i]), Theme.F(9f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(tx, c.Y + Theme.S(7), tw, Theme.S(20)), Width), Align.Start, true, false);
                Theme.Text(g, TrimPage.Ago(t), Theme.F(8f), Theme.InkFaint, Theme.Mirror(new Rectangle(tx, c.Y + Theme.S(26), tw, Theme.S(17)), Width), Align.Start, true, false);
            }
        }
    }
}
