using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    public partial class VideoStudio
    {
        // ------------------------------------------------------------------ adding things
        void AddPicked(string what)
        {
            StopPlay();
            switch (what)
            {
                case "media": Browse(true); break;
                case "overlay": PickInto(VTrackKind.Overlay, L.T("vs_a_overlay"), true); break;
                case "audio": PickInto(VTrackKind.Audio, L.T("vs_a_audio"), false); break;
                case "text": AddText(); break;
                case "captions": MakeCaptions(); break;
                case "subs": ImportSubs(); break;
            }
        }

        void PickInto(VTrackKind kind, string title, bool pictures)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = title;
                List<string> pats = new List<string>();
                foreach (string e in Formats.ImageExt) pats.Add("*." + e);
                d.Filter = pictures ? L.T("photos_videos") + "|" + string.Join(";", pats.ToArray()) + ";*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v|" + L.T("all_files") + "|*.*"
                                    : L.T("vs_sounds") + "|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.mp4;*.mov;*.mkv|" + L.T("all_files") + "|*.*";
                d.Multiselect = true;
                if (LastDir.Show(d, FindForm(), pictures ? "video" : "music") == DialogResult.OK) AddMedia(d.FileNames, kind, Playhead);
            }
        }

        void AddText()
        {
            Change("vs_h_add_text");
            double at = Playhead >= P.Duration - 0.2 ? 0 : Playhead;
            TextLayer t = new TextLayer { Text = L.T("ps_new_text"), SizePx = Math.Max(24, Math.Min(P.W, P.H) / 11f), Color = Color.White, Bold = true, Outline = true, Shadow = true };
            VClip c = new VClip { Kind = VKind.Text, Text = t, In = 0, Out = Math.Min(3, Math.Max(1, P.Duration - at)), At = at, X = 0.5f, Y = 0.5f, AnimIn = "fade", AnimOut = "fade", AnimInDur = 0.4, AnimOutDur = 0.4 };
            P.TrackFor(VTrackKind.Text, c.At, c.Length).Clips.Add(c);
            tab = 0;
            Select(c);
            AfterChange(true);
            BeginInvoke((Action)(() => { textBox.Box.Focus(); textBox.Box.SelectAll(); }));
        }

        void ImportSubs()
        {
            using (OpenFileDialog d = new OpenFileDialog { Title = L.T("vs_a_subs"), Filter = L.T("vs_srt_files") + "|*.srt|" + L.T("all_files") + "|*.*" })
            {
                if (LastDir.Show(d, FindForm(), "subs") != DialogResult.OK) return;
                VClip cap = null;
                try { cap = Captions.FromSrt(d.FileName); } catch (Exception ex) { ShowToast(ex.Message, false, null, null); return; }
                if (cap == null) { ShowToast(L.T("vs_srt_empty"), false, null, null); return; }
                Change("vs_h_caps");
                P.TrackFor(VTrackKind.Captions, cap.At, cap.Length).Clips.Add(cap);
                Select(cap);
                AfterChange(true);
            }
        }

        // ------------------------------------------------------------------ editing clips
        void Split()
        {
            VClip c = Sel;
            if (c == null)
            {
                // nothing chosen: the main clip under the playhead
                foreach (VClip m in P.Main.Clips) if (Playhead > m.At + 0.05 && Playhead < m.End - 0.05) c = m;
                if (c == null) return;
            }
            if (Playhead <= c.At + 0.05 || Playhead >= c.End - 0.05) return;
            StopPlay();
            Change("vs_h_split");
            VTrack t = P.TrackOf(c);
            double cut = Playhead - c.At;
            VClip b = c.Duplicate();
            if (c.Kind == VKind.Video || c.Kind == VKind.Audio) { double src = c.In + cut * c.Speed; c.Out = src; b.In = src; }
            else { c.Out = c.In + cut; b.In = 0; b.Out = Math.Max(0.1, (b.Out - b.In) - cut); if (c.Kind == VKind.Image) { b.In = 0; } }
            b.At = c.At + cut;
            // the left part keeps its fade in, the right part its fade out; animations likewise
            c.FadeOut = 0; b.FadeIn = 0; c.AnimOut = "none"; b.AnimIn = "none";
            if (t.Kind == VTrackKind.Main) { c.Trans = "none"; }
            // keyframes: each part keeps its own
            foreach (string p in new List<string>(c.Keys.Keys))
            {
                List<VKey> all = c.Keys[p];
                List<VKey> left = all.FindAll(k => k.T <= cut + 0.001), right = new List<VKey>();
                foreach (VKey k in all) if (k.T >= cut - 0.001) right.Add(new VKey { T = Math.Max(0, k.T - cut), V = k.V });
                c.Keys[p] = left; b.Keys[p] = right;
                if (left.Count == 0) c.Keys.Remove(p);
                if (right.Count == 0) b.Keys.Remove(p);
            }
            if (c.Cues != null)
            {
                b.Cues = c.Cues.FindAll(q => q.Start >= Playhead);
                c.Cues = c.Cues.FindAll(q => q.Start < Playhead);
            }
            t.Clips.Insert(t.Clips.IndexOf(c) + 1, b);
            Select(b);
            AfterChange(true);
        }

        /// I / O: the selected clip starts (or ends) at the playhead.
        void TrimToPlayhead(bool start)
        {
            VClip c = Sel;
            if (c == null || Playhead <= c.At + 0.05 || Playhead >= c.End - 0.05) return;
            Change("vs_h_trim");
            double cut = Playhead - c.At;
            bool media = c.Kind == VKind.Video || c.Kind == VKind.Audio;
            if (start)
            {
                if (media) c.In += cut * c.Speed; else c.Out -= cut;
                if (P.TrackOf(c).Kind != VTrackKind.Main) c.At += cut;
                foreach (List<VKey> l in c.Keys.Values) { foreach (VKey k in l) k.T -= cut; l.RemoveAll(k => k.T < 0); }
            }
            else { if (media) c.Out = c.In + cut * c.Speed; else c.Out = c.In + cut; }
            P.Tidy();
            AfterChange(true);
        }

        void DeleteSelected()
        {
            VClip c = Sel;
            if (c == null) return;
            StopPlay();
            Change("vs_h_delete");
            VTrack t = P.TrackOf(c);
            if (t != null) t.Clips.Remove(c);
            // the video whose sound this was: hear it again
            P.Tidy();
            Select(null);
            AfterChange(true);
            if (P.Empty) DoLayout();
        }

        void DuplicateSelected()
        {
            VClip c = Sel;
            if (c == null) return;
            Change("vs_h_duplicate");
            VTrack t = P.TrackOf(c);
            VClip d = c.Duplicate();
            if (t.Kind == VTrackKind.Main) t.Clips.Insert(t.Clips.IndexOf(c) + 1, d);
            else { d.At = c.End; P.TrackFor(t.Kind, d.At, d.Length).Clips.Add(d); }
            Select(d);
            AfterChange(true);
        }

        /// The video's sound goes to an audio track of its own (to move, cut or change it apart from the picture).
        void DetachAudio()
        {
            VClip c = Sel;
            if (c == null || c.Kind != VKind.Video || !c.HasSound) return;
            Change("vs_h_detach");
            VClip a = c.Duplicate();
            a.Kind = VKind.Audio; a.Detached = false; a.Adj = new Adjust(); a.Keys.Clear(); a.Trans = "none"; a.At = c.At;
            c.Detached = true;
            P.TrackFor(VTrackKind.Audio, a.At, a.Length).Clips.Add(a);
            Select(a);
            AfterChange(true);
            ShowToast(L.T("vs_detached"), true, null, null);
        }

        void ReplaceMedia()
        {
            VClip c = Sel;
            if (c == null || c.Src == null) return;
            using (OpenFileDialog d = new OpenFileDialog { Title = L.T("vs_replace"), Filter = L.T("photos_videos") + "|*.*" })
            {
                if (LastDir.Show(d, FindForm(), "video") != DialogResult.OK) return;
                string f = d.FileName;
                Thread t = new Thread(() =>
                {
                    VKind k = Formats.CategoryOf(Formats.Ext(f)) == Category.Image ? VKind.Image : VKind.Video;
                    MediaSource s = Source(f, k);
                    Ui(() =>
                    {
                        if (s == null) { ShowToast(L.T("not_media"), false, null, null); return; }
                        Change("vs_h_replace");
                        double len = c.Length;
                        c.Src = s; c.Kind = k == VKind.Image ? VKind.Image : (s.Info.HasVideo ? (c.Kind == VKind.Audio ? VKind.Audio : VKind.Video) : VKind.Audio);
                        c.In = 0; c.Out = k == VKind.Image ? len : Math.Min(s.Info.Duration, len * c.Speed);
                        AfterChange(true);
                    });
                });
                t.IsBackground = true; t.Start();
            }
        }

        void ClipMenu(VClip c, Point screen)
        {
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("vs_split"), Glyph = Ico.Cut, Shortcut = "S", Run = Split, Disabled = !(Playhead > c.At + 0.05 && Playhead < c.End - 0.05) });
            m.Add(new MenuPopup.Item { Text = L.T("vs_duplicate"), Glyph = Ico.Copy, Shortcut = "Ctrl+D", Run = DuplicateSelected });
            if (c.Kind == VKind.Video && c.HasSound) m.Add(new MenuPopup.Item { Text = L.T("vs_detach"), Glyph = "", Run = DetachAudio });
            if (c.Kind == VKind.Video) m.Add(new MenuPopup.Item { Text = L.T("vs_remove_bg"), Glyph = Ps.Subject, Run = RemoveVideoBg });
            if (c.Src != null) m.Add(new MenuPopup.Item { Text = L.T("vs_replace"), Glyph = Ico.Refresh, Run = ReplaceMedia });
            if (c.Kind == VKind.Image && P.TrackOf(c).Kind == VTrackKind.Main) m.Add(new MenuPopup.Item { Text = L.T("vs_edit_photo"), Glyph = Ico.Edit, Run = () => { if (OpenImage != null) OpenImage(c.File); } });
            if (c.Cues != null) m.Add(new MenuPopup.Item { Text = L.T("vs_caps_srt"), Glyph = Ico.Save, Run = ExportSrt });
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("vs_delete"), Glyph = Ico.Delete, Shortcut = "Del", Danger = true, Run = DeleteSelected });
            MenuPopup.ShowAt(timeline, screen, m);
        }

        void AspectMenu()
        {
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            foreach (string[] a in new[] { new[] { "orig", "vs_ar_orig", "vs_ar_orig_sub" }, new[] { "16:9", "vs_s_wide", "vs_ar_wide_sub" }, new[] { "9:16", "vs_s_tall", "vs_ar_tall_sub" }, new[] { "1:1", "vs_s_square", "vs_ar_square_sub" }, new[] { "4:5", "vs_s_portrait", "vs_ar_portrait_sub" }, new[] { "4:3", "4:3", "vs_ar_43_sub" } })
            {
                string k = a[0];
                m.Add(new MenuPopup.Item { Text = (k == "orig" ? L.T(a[1]) : (a[1] == k ? k : L.T(a[1]) + "  ·  " + k)) + (P.Aspect == k ? "  ✓" : ""), Sub = L.T(a[2]), Glyph = "", Run = () => SetAspect(k) });
            }
            MenuPopup.Show(bAspect, m, 300);
        }

        void MoreMenu()
        {
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("proj_save"), Sub = projectPath != null ? Path.GetFileName(projectPath) : L.T("proj_not_saved"), Glyph = Ico.Save, Shortcut = "Ctrl+S", Run = () => SaveProject(false), Disabled = exporting });
            m.Add(new MenuPopup.Item { Text = L.T("proj_save_as"), Glyph = Ico.Copy, Shortcut = "Ctrl+Shift+S", Run = () => SaveProject(true), Disabled = exporting });
            m.Add(new MenuPopup.Item { Text = L.T("proj_open"), Glyph = Ico.FolderOpen, Shortcut = "Ctrl+O", Run = () => { string f = PickProject(FindForm()); if (f != null) OpenProject(f, false); } });
            m.Add(new MenuPopup.Item { Text = L.T("vs_new_project"), Glyph = "", Run = () => CloseFile() });
            List<string> rec = RecentProjects;
            if (rec.Count > 0)
            {
                m.Add(new MenuPopup.Item { Header = L.T("proj_recent") });
                foreach (string p in rec.GetRange(0, Math.Min(5, rec.Count)))
                {
                    string pp = p; DateTime t = DateTime.Now; try { t = File.GetLastWriteTime(p); } catch { }
                    m.Add(new MenuPopup.Item { Text = Path.GetFileNameWithoutExtension(p), Sub = Ago(t), Glyph = "", Run = () => OpenProject(pp, false) });
                }
            }
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("vs_shortcuts"), Glyph = "", Run = () => Ask.Info(FindForm(), L.T("vs_shortcuts"), L.T("vs_shortcuts_list")) });
            MenuPopup.Show(bMore, m, 320);
        }

        // ------------------------------------------------------------------ captions (spoken words → captions clip)
        bool captioning; volatile bool capCancel;
        public void MakeCaptions()
        {
            if (captioning) { capCancel = true; return; }
            if (exporting || P.Empty) return;
            bool sound = false;
            foreach (VTrack t in P.Tracks) if (!t.Muted) foreach (VClip c in t.Clips) if (c.HasSound && !c.Muted) sound = true;
            if (!sound) { ShowToast(L.T("cap_no_sound"), false, null, null); return; }
            int q; string lang; bool english;
            using (CaptionsForm f = new CaptionsForm())
            {
                f.StartPosition = FormStartPosition.CenterParent;
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                q = f.Quality; lang = f.Language; english = f.English;
                if (english) q = 0;
            }
            StopPlay();
            VProject snap = P.Copy();
            // only the sound of the project, as Whisper wants it
            foreach (VTrack t in snap.Tracks) if (t.Visual && t.Kind != VTrackKind.Main) t.Hidden = true;
            snap.Tracks.RemoveAll(t => t.Kind == VTrackKind.Captions);
            captioning = true; capCancel = false;
            string stem = projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : FirstName();
            ActJob act = Activity.Begin(this, L.T("act_k_captions"), stem, () => capCancel = true);
            busyJob = L.T("cap_starting"); InvalidateBar();
            Thread th = new Thread(() =>
            {
                string err = null; List<Cue> cues = null;
                string wav = Path.Combine(Path.GetTempPath(), "fadi_speech_" + Guid.NewGuid().ToString("N") + ".mp3");
                Action<string, double> say = (s, f) => { Activity.Update(act, f, s); Ui(() => { busyJob = s + (f >= 0 ? "  " + (int)(f * 100) + "%" : ""); InvalidateBar(); }); };
                try
                {
                    if (!Whisper.Ready(q))
                        Whisper.Install(q, (what, a, b) => say(L.T(what == "model" ? "cap_dl_model" : "cap_dl_program", Theme.HumanSize(a), b > 0 ? Theme.HumanSize(b) : "?"), b > 0 ? a / (double)b : -1), () => capCancel);
                    if (capCancel) throw new OperationCanceledException();
                    say(L.T("cap_prep"), 0);
                    Job j = VideoRender.Build(snap, wav, new RenderOptions { W = 320, H = 180, Format = "mp3", Preview = false });
                    string e1 = Engine.Run(j, f => say(L.T("cap_prep"), f * 0.05), () => capCancel);
                    if (capCancel) throw new OperationCanceledException();
                    if (e1 != null) throw new UserError(e1);
                    // Whisper reads 16 kHz WAV
                    string wav16 = wav + ".wav";
                    string o; Tools.RunHidden(Tools.FFmpeg, "-hide_banner -y -i " + Tools.Quote(wav) + " -ar 16000 -ac 1 " + Tools.Quote(wav16), 600000, out o);
                    string srt = Whisper.Transcribe(wav16, q, lang, english, f => say(L.T("cap_listening", (int)(f * 100)), 0.05 + f * 0.95), () => capCancel);
                    cues = Captions.ParseSrt(File.ReadAllText(srt, Encoding.UTF8));
                    try { File.Delete(srt); File.Delete(wav16); } catch { }
                    if (cues.Count == 0) err = L.T("cap_nothing");
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                try { File.Delete(wav); } catch { }
                Ui(() =>
                {
                    captioning = false; busyJob = null; InvalidateBar();
                    Activity.End(act, err, capCancel, L.T("cap_done_short"), null);
                    if (capCancel) return;
                    if (err != null) { ShowToast(err, false, null, null); return; }
                    if (cues == null || cues.Count == 0) return;
                    Change("vs_h_caps");
                    VClip cap = Captions.Make(cues);
                    cap.CapStyle = "highlight";
                    P.TrackFor(VTrackKind.Captions, cap.At, cap.Length).Clips.Add(cap);
                    tab = 0;
                    Select(cap);
                    AfterChange(true);
                    ShowToast(L.T("vs_caps_done", cues.Count), true, null, null);
                });
            });
            th.IsBackground = true; th.Start();
        }

        void ExportSrt()
        {
            VClip c = Sel;
            if (c == null || c.Cues == null) return;
            string path = SaveAsk.File(FindForm(), FirstName() + ".srt", "srt");
            if (path == null) return;
            try { File.WriteAllText(path, Captions.ToSrt(c.Cues), new UTF8Encoding(false)); ShowToast(L.T("v_saved", Path.GetFileName(path)), true, () => L.T("show_file"), () => ActivityRows.ShowOutput(path)); }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        // ------------------------------------------------------------------ removing a video's background (on this PC)
        public void RemoveVideoBg()
        {
            VClip c = Sel;
            if (c == null) c = P.Main.Clips.Find(x => x.Kind == VKind.Video && Playhead >= x.At && Playhead < x.End) ?? P.Main.Clips.Find(x => x.Kind == VKind.Video);
            if (c == null || c.Kind != VKind.Video || c.Src == null || !c.Src.Info.HasVideo) { ShowToast(L.T("vbg_no_video"), false, null, null); return; }
            if (busyJob != null || exporting) return;
            double vfps = VideoBg.Fps(c.Src.Info);
            int frames2 = Math.Max(1, (int)Math.Ceiling((c.Out - c.In) * vfps));
            int minutes = Math.Max(1, (int)Math.Round(frames2 * 1.6 / 60));
            string msg = L.T("vs_bg_msg", Theme.Time(c.Out - c.In, false), minutes) + (BgRemover.Ready ? "" : "\n\n" + L.T("vbg_need_dl"));
            if (!Ask.Confirm(FindForm(), L.T("vbg_title"), msg, L.T("vbg_start"), L.T("cancel"), AskKind.Info)) return;
            string outp = Path.Combine(ProjectsDir, "Media", Path.GetFileNameWithoutExtension(c.File) + " " + L.T("vbg_suffix") + " " + DateTime.Now.ToString("HHmmss") + ".webm");
            try { Directory.CreateDirectory(Path.GetDirectoryName(outp)); } catch { }
            string input = c.File; double start = c.In, len = c.Out - c.In;
            volatile_stop = false;
            ActJob job = Activity.Begin(this, L.T("vbg_job"), Path.GetFileName(input), () => volatile_stop = true);
            busyJob = L.T("vbg_job"); InvalidateBar();
            Thread t = new Thread(() =>
            {
                string err = null;
                try
                {
                    if (!BgRemover.Ready)
                    {
                        Activity.Update(job, -1, L.T("vbg_dl"));
                        BgRemover.Install((f, label) => Activity.Update(job, f * 0.1, label), () => volatile_stop);
                        if (volatile_stop) throw new OperationCanceledException();
                        if (!BgRemover.Ready) throw new UserError(L.T("vbg_need_ai"));
                    }
                    VideoBg.Run(input, start, len, 0, Color.Black, null, outp, (n, total) =>
                    {
                        Activity.Update(job, n / (double)total, n + " / " + total);
                        Ui(() => { busyJob = L.T("vbg_job") + "  " + (n * 100 / Math.Max(1, total)) + "%"; InvalidateBar(); });
                    }, () => volatile_stop || Tools.ShuttingDown);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                bool stopped = volatile_stop;
                Activity.End(job, err, stopped, L.T("vbg_done"), outp);
                Ui(() =>
                {
                    busyJob = null; InvalidateBar();
                    if (stopped) { ShowToast(L.T("cancelled"), true, null, null); return; }
                    if (err != null || !File.Exists(outp)) { ShowToast(err ?? L.T("vbg_failed"), false, null, null); return; }
                    // the clip now plays the cut-out (see-through) version; on the main track it's moved over the background
                    MediaSource s = Source(outp, VKind.Video);
                    if (s == null) return;
                    Change("vs_h_remove_bg");
                    VTrack tr = P.TrackOf(c);
                    if (tr != null && tr.Kind == VTrackKind.Main)
                    {
                        // the main clip stays as it is (it becomes the background); the cut-out goes over it, full size
                        VClip o = c.Duplicate();
                        o.Src = s; o.Alpha = true; o.In = 0; o.Out = Math.Min(s.Info.Duration, len); o.Speed = c.Speed;
                        o.At = c.At; o.Scale = 1; o.X = 0.5f; o.Y = 0.5f; o.Trans = "none"; o.Detached = true; o.FadeIn = o.FadeOut = 0;
                        P.TrackFor(VTrackKind.Overlay, o.At, o.Length).Clips.Add(o);
                        Select(o);
                    }
                    else { c.Src = s; c.Alpha = true; c.In = 0; c.Out = Math.Min(s.Info.Duration, len); c.Detached = true; }
                    AfterChange(true);
                    ShowToast(L.T("vs_bg_done"), true, null, null);
                });
            });
            t.IsBackground = true; t.Start();
        }
        volatile bool volatile_stop;

        // ------------------------------------------------------------------ export
        volatile bool exportCancel;
        public void Export()
        {
            if (P.Empty || exporting) return;
            StopPlay();
            using (VideoExportForm f = new VideoExportForm(P))
            {
                if (Ask.ShowDimmed(f, FindForm()) != DialogResult.OK) return;
                RenderOptions o = f.Options;
                string ext = o.Format == "gif" ? "gif" : o.Format == "mp3" ? "mp3" : "mp4";
                string path = SaveAsk.File(FindForm(), (projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : FirstName() + " " + L.T("edited_suffix")) + "." + ext, ext);
                if (path == null) return;
                Job job;
                try { job = VideoRender.Build(P.Copy(), path, o); }
                catch (Exception ex) { ShowToast(ex.Message, false, null, null); return; }
                exporting = true; exportCancel = false;
                busyJob = L.T("trimming", 0); InvalidateBar();
                ActJob act = Activity.Begin(this, L.T("act_k_export"), Path.GetFileName(path), () => exportCancel = true);
                bExport.Kind = BtnKind.Secondary; bExport.Glyph = Ico.Stop; bExport.Label = () => L.T("cancel"); bExport.Invalidate();
                bExport.Click -= ExportClickCancel; bExport.Click += ExportClickCancel;
                Thread t = new Thread(() =>
                {
                    DateTime last = DateTime.MinValue;
                    string err = Engine.Run(job, fr =>
                    {
                        if ((DateTime.Now - last).TotalMilliseconds < 150) return;
                        last = DateTime.Now;
                        Activity.Update(act, fr, (int)(fr * 100) + "%");
                        Ui(() => { busyJob = L.T("trimming", (int)(fr * 100)); InvalidateBar(); });
                    }, () => exportCancel);
                    // the graphics card failed: once more on the processor
                    if (err != null && !exportCancel && o.Gpu) { HwEnc.Disable(); o.Gpu = false; try { job = VideoRender.Build(P.Copy(), path, o); err = Engine.Run(job, fr => Activity.Update(act, fr, (int)(fr * 100) + "%"), () => exportCancel); } catch (Exception ex) { err = ex.Message; } }
                    foreach (string tmp in job.Temps) try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); else File.Delete(tmp); } catch { }
                    Activity.End(act, err, exportCancel, null, err == null && !exportCancel ? path : null);
                    Ui(() =>
                    {
                        exporting = false; busyJob = null; InvalidateBar();
                        bExport.Click -= ExportClickCancel;
                        bExport.Kind = BtnKind.Primary; bExport.Glyph = Ico.Upload; bExport.Label = () => L.T("ps_export"); bExport.Invalidate();
                        if (exportCancel) { try { File.Delete(path); } catch { } ShowToast(L.T("cancelled"), true, null, null); return; }
                        if (err != null) { ShowToast(err, false, null, null); return; }
                        savedAt = changes;
                        Recovery.Clear(Recovery.VideoFile);
                        ShowToast(L.T("v_saved", Path.GetFileName(path)), true, () => L.T("show_file"), () => ActivityRows.ShowOutput(path));
                        Invalidate();
                    });
                });
                t.IsBackground = true; t.Start();
            }
        }
        void ExportClickCancel(object s, EventArgs e) { if (exporting) exportCancel = true; }

        // ------------------------------------------------------------------ projects
        public void SaveProject(bool saveAs)
        {
            if (P.Empty) return;
            string path = projectPath;
            if (path == null || saveAs || !Directory.Exists(Path.GetDirectoryName(path)))
            {
                using (SaveFileDialog d = new SaveFileDialog())
                {
                    d.Title = L.T("proj_save_title");
                    d.Filter = L.T("proj_files") + " (*." + ProjectExt + ")|*." + ProjectExt;
                    try { Directory.CreateDirectory(ProjectsDir); } catch { }
                    d.InitialDirectory = path != null ? Path.GetDirectoryName(path) : ProjectsDir;
                    string stem = path != null ? Path.GetFileNameWithoutExtension(path) : FirstName();
                    foreach (char ch in Path.GetInvalidFileNameChars()) stem = stem.Replace(ch, '_');
                    d.FileName = stem + "." + ProjectExt;
                    d.OverwritePrompt = true;
                    if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                    path = d.FileName;
                }
            }
            try
            {
                File.WriteAllText(path, P.ToJson(path), new UTF8Encoding(false));
                projectPath = path;
                savedAt = changes;
                Recovery.Clear(Recovery.VideoFile);
                AddRecent(path);
                ShowToast(L.T("proj_saved", Path.GetFileNameWithoutExtension(path)), true, () => L.T("show_file"), () => ActivityRows.ShowOutput(path));
                Invalidate();
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        int autoAt = -1;
        void AutoSave()
        {
            if (P.Empty || !HasUnsavedText || changes == autoAt || !Wanted) return;
            autoAt = changes;
            try { Recovery.Write(Recovery.VideoFile, P.ToJson(projectPath)); } catch { }
        }

        /// Opens a saved project (or the automatic backup, when recovered = true). Old projects are brought up to date.
        public void OpenProject(string path, bool recovered)
        {
            if (exporting) return;
            if (HasUnsavedText && !Ask.Confirm(FindForm(), L.T("ed_discard_title"), L.T("vs_discard"), L.T("ed_discard_yes"), L.T("ed_keep_editing"), AskKind.Warning)) return;
            object root;
            try { root = new JsonParser(File.ReadAllText(path, Encoding.UTF8)).Parse(); }
            catch (Exception ex) { ShowToast(L.T("proj_bad", ex.Message), false, null, null); return; }
            StopPlay();
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            loading = true; busyJob = L.T("v_loading"); InvalidateBar(); Invalidate();
            Thread t = new Thread(() =>
            {
                List<string> missing = new List<string>();
                VProject np = null; string err = null;
                try { np = VProject.FromJson(root, dir, (f, k) => Source(f, k), missing); }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    loading = false; busyJob = null;
                    if (err != null || np == null) { ShowToast(L.T("proj_bad", err ?? "?"), false, null, null); InvalidateBar(); return; }
                    if (np.Empty) { ShowToast(L.T("proj_all_missing"), false, null, null); InvalidateBar(); return; }
                    if (missing.Count > 0) ShowToast(L.T("proj_missing", string.Join(", ", missing.ToArray())), false, null, null);
                    P = np; timeline.P = P; preview.P = P;
                    timeline.Selected = null; preview.Selected = null;
                    undo.Clear(); redo.Clear(); undoNames.Clear();
                    changes = recovered ? 1 : 0; savedAt = 0;
                    projectPath = recovered ? Js.S(root, "path") : path;
                    if (projectPath != null && !File.Exists(projectPath)) projectPath = null;
                    if (!recovered) AddRecent(path);
                    playhead = 0;
                    DoLayout();
                    timeline.FitAll();
                    Select(null);
                    AfterChange(true);
                });
            });
            t.IsBackground = true; t.Start();
        }

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

        /// Asks which project to open (null = cancelled).
        public static string PickProject(IWin32Window owner)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("proj_open_title");
                d.Filter = L.T("proj_files") + " (*." + ProjectExt + ")|*." + ProjectExt;
                try { if (Directory.Exists(ProjectsDir)) d.InitialDirectory = ProjectsDir; } catch { }
                return LastDir.Show(d, owner, "project") == DialogResult.OK ? d.FileName : null;
            }
        }
    }

    /// Export: the size, smoothness, quality and kind of file, with how big it will roughly be.
    public class VideoExportForm : FormDialog
    {
        readonly VProject p;
        readonly ChipGrid res = new ChipGrid { Columns = 3, RowH = 34 }, rate = new ChipGrid { Columns = 4, RowH = 32 }, quality = new ChipGrid { Columns = 3, RowH = 34 }, kind = new ChipGrid { Columns = 3, RowH = 34 };
        readonly Toggle gpu = new Toggle();
        Rectangle estR, gpuR;
        bool gpuShown;

        public VideoExportForm(VProject p) : base(L.T("vs_export_title"), L.T("vs_export_sub", Theme.Time(p.Duration, false)), L.T("ps_export"))
        {
            this.p = p;
            DialogWidth = 540;
            kind.LabelOf = k => L.T("vs_ex_" + k);
            kind.SetItems(new[] { "mp4", "gif", "mp3" }, Settings.Get("VsFormat", "mp4"));
            List<string> rs = new List<string> { "orig" };
            foreach (int r in new[] { 2160, 1440, 1080, 720, 480 }) if (r < Math.Min(p.W, p.H) - 8) rs.Add(r.ToString());
            res.LabelOf = k => k == "orig" ? L.T("vs_ex_orig", p.W + "×" + p.H) : k == "2160" ? "4K" : k + "p";
            res.SetItems(rs, rs.Contains(Settings.Get("VsRes", "orig")) ? Settings.Get("VsRes", "orig") : "orig");
            rate.LabelOf = k => k + " fps";
            rate.SetItems(new[] { "24", "25", "30", "60" }, ((int)Math.Round(p.Fps)).ToString());
            quality.LabelOf = k => L.T("vs_q_" + k);
            quality.SetItems(new[] { "small", "good", "best" }, Settings.Get("VsQuality", "good"));
            foreach (ChipGrid c in new[] { res, rate, quality, kind }) c.SelectedChanged += (s, e) => Invalidate();
            gpu.SetSilently(Settings.Get("VsGpu", "1") == "1");
            Add(() => L.T("vs_ex_kind"), kind, 34);
            int rows = (rs.Count + 2) / 3;
            Add(() => L.T("vs_ex_res"), res, rows * 34 + (rows - 1) * 8);
            Add(() => L.T("vs_ex_fps"), rate, 32);
            Add(() => L.T("vs_ex_quality"), quality, 34);
            gpuShown = HwEnc.Detect() != "";
            if (gpuShown) Controls.Add(gpu);
            Validate2 = () =>
            {
                Settings.Set("VsFormat", kind.Selected); Settings.Set("VsRes", res.Selected); Settings.Set("VsQuality", quality.Selected); Settings.Set("VsGpu", gpu.Checked ? "1" : "0");
                return true;
            };
        }

        public RenderOptions Options
        {
            get
            {
                int w = p.W, h = p.H;
                if (res.Selected != "orig")
                {
                    int r = int.Parse(res.Selected);
                    double k = r / (double)Math.Min(p.W, p.H);
                    w = (int)Math.Round(p.W * k); h = (int)Math.Round(p.H * k);
                }
                if (kind.Selected == "gif") { double k = Math.Min(1, 480.0 / Math.Max(w, h)); w = (int)(w * k); h = (int)(h * k); }
                return new RenderOptions { W = w - w % 2, H = h - h % 2, Fps = double.Parse(rate.Selected, CultureInfo.InvariantCulture), Crf = quality.Selected == "small" ? 26 : quality.Selected == "best" ? 17 : 21, Gpu = gpuShown && gpu.Checked, Format = kind.Selected };
            }
        }

        /// A rough size: pixels × frames × how hard it's squeezed.
        string Estimate()
        {
            RenderOptions o = Options;
            double sec = p.Duration;
            if (o.Format == "mp3") return L.T("ps_about", Theme.HumanSize((long)(sec * 190000 / 8)));
            double pixels = o.W * (double)o.H * o.Fps;
            double bpp = o.Format == "gif" ? 0.9 : o.Crf <= 17 ? 0.16 : o.Crf <= 21 ? 0.09 : 0.05;
            double bits = pixels * bpp * sec + (o.Format == "gif" ? 0 : 192000 * sec);
            return o.W + " × " + o.H + "   ·   " + L.T("ps_about", Theme.HumanSize((long)(bits / 8)));
        }

        protected override void LayoutExtra(int x, ref int y, int w)
        {
            if (gpuShown)
            {
                gpuR = new Rectangle(x, y, w - Theme.S(52), Theme.S(28));
                gpu.Bounds = Theme.Mirror(new Rectangle(x + w - Theme.S(44), y + Theme.S(2), Theme.S(44), Theme.S(24)), ClientSize.Width > 0 ? ClientSize.Width : Theme.S(540));
                y += Theme.S(36);
            }
            estR = new Rectangle(x, y, w, Theme.S(28)); y += Theme.S(32);
        }
        protected override void PaintExtra(Graphics g)
        {
            int W = ClientSize.Width;
            if (gpuShown) Theme.Text(g, L.T("vs_ex_gpu", HwEnc.Friendly(HwEnc.Detect())), Theme.F(9.25f), Theme.Ink, Theme.Mirror(gpuR, W), Align.Start);
            Rectangle r = Theme.Mirror(estR, W);
            Theme.FillRound(g, r, Theme.S(8), Theme.CardInner);
            Theme.Text(g, Estimate(), Theme.F(9.5f, FontStyle.Bold), Theme.Ink, new Rectangle(r.X + Theme.S(12), r.Y, r.Width - Theme.S(24), r.Height), Align.Start, true, false, true);
        }
    }
}
