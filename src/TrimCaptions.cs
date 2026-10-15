using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Auto captions: the timeline's sound goes to whisper (on this PC), which writes subtitles that are then
    /// burned into the saved video like any subtitle file — and can be fixed in Notepad first.
    public partial class TrimPage
    {
        Btn btnCaptions, btnCapEdit;
        bool captioning; volatile bool capCancel;
        double capProgress = -1; string capText = "";
        ActJob capAct;
        public static string CaptionsDir { get { return Path.Combine(Paths.AppDir, "captions"); } }

        void InitCaptions()
        {
            btnCaptions = new Btn(() => captioning ? capText : L.T("cap_btn"), "", BtnKind.Secondary);
            btnCaptions.Click += (s, e) => MakeCaptions();
            btnCaptions.MouseEnter += (s, e) => tbTips.SetToolTip(btnCaptions, L.T(captioning ? "cap_stop_tip" : "cap_tip"));
            btnCapEdit = new Btn(() => L.T("cap_edit"), Ico.Edit, BtnKind.Ghost) { FontSize = 9f };
            btnCapEdit.Click += (s, e) => EditCaptions();
            editor.AddToEffects(btnCaptions, btnCapEdit);
        }

        /// Starts (or stops) making captions for the whole timeline.
        public void MakeCaptions()
        {
            if (captioning) { capCancel = true; capText = L.T("cancelling"); btnCaptions.Invalidate(); return; }
            if (running || clips.Count == 0) return;
            if (!clips[0].Info.HasVideo) { ShowToast(L.T("cap_video_only"), false, null, null); return; }
            if (reverse.Checked) { ShowToast(L.T("cap_no_reverse"), false, null, null); return; }
            if (!clips.Exists(c => c.Info.HasAudio)) { ShowToast(L.T("cap_no_sound"), false, null, null); return; }
            int q; string lang;
            using (CaptionsForm f = new CaptionsForm())
            {
                f.StartPosition = FormStartPosition.CenterParent;
                if (f.ShowDialog(FindForm()) != DialogResult.OK) return;
                q = f.Quality; lang = f.Language;
            }
            StopPlay();
            editor.SelectTab(EdTabPublic.Effects);
            List<Media.MergeClip> mc = new List<Media.MergeClip>();
            foreach (Clip c in clips) mc.Add(new Media.MergeClip { File = c.File, Info = c.Info, Start = c.Start, End = c.End });
            string trans = clips.Count > 1 ? TransKeys[TransIndex] : "none";
            double tdur = fxDur.Value, speed = Speeds[Math.Max(0, Math.Min(Speeds.Length - 1, speedSeg.SelectedIndex))];
            string stem = projectPath != null ? Path.GetFileNameWithoutExtension(projectPath) : Path.GetFileNameWithoutExtension(clips[0].File);
            foreach (char ch in Path.GetInvalidFileNameChars()) stem = stem.Replace(ch, '_');
            captioning = true; capCancel = false;
            capAct = Activity.Begin(this, L.T("act_k_captions"), stem, () => capCancel = true);
            SetCap(L.T("cap_starting"), -1);
            editor.DoLayout();   // the button turns into a stop button and "Add subtitles" steps aside
            int token = loadToken;
            Thread t = new Thread(() =>
            {
                string err = null, srtOut = null;
                string wav = Path.Combine(Path.GetTempPath(), "fadi_speech_" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    if (!Whisper.Ready(q))
                        Whisper.Install(q, (what, a, b) =>
                        {
                            double f = b > 0 ? a / (double)b : -1;
                            SetCap(L.T(what == "model" ? "cap_dl_model" : "cap_dl_program", Theme.HumanSize(a), b > 0 ? Theme.HumanSize(b) : "?"), f);
                        }, () => capCancel);
                    if (capCancel) throw new OperationCanceledException();
                    SetCap(L.T("cap_prep"), 0);
                    Job j = Media.BuildSpeech(mc, trans, tdur, speed, wav);
                    string e1 = Engine.Run(j, f => SetCap(L.T("cap_prep"), f * 0.05), () => capCancel);
                    if (capCancel) throw new OperationCanceledException();
                    if (e1 != null) throw new UserError(e1);
                    string srt = Whisper.Transcribe(wav, q, lang, f => SetCap(L.T("cap_listening", (int)(f * 100)), 0.05 + f * 0.95), () => capCancel);
                    Directory.CreateDirectory(CaptionsDir);
                    srtOut = Path.Combine(CaptionsDir, stem + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + ".srt");
                    File.Copy(srt, srtOut, true);
                    try { File.Delete(srt); } catch { }
                    if (File.ReadAllText(srtOut).Trim().Length == 0) { err = L.T("cap_nothing"); try { File.Delete(srtOut); } catch { } srtOut = null; }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                try { File.Delete(wav); } catch { }
                Ui(() =>
                {
                    captioning = false;
                    Activity.End(capAct, err, capCancel, L.T("cap_done_short"), srtOut);
                    if (!capCancel && err == null && srtOut != null && token == loadToken)
                    {
                        subsFile = srtOut; subsAuto = true;
                        FxChanged(); seqKey = null;
                        ShowToast(L.T("cap_done"), true, () => L.T("fx_preview"), () => EnterSequence(true));
                    }
                    else if (err != null && !capCancel) ShowToast(err, false, null, null);
                    SetStatus("", Theme.InkSoft);
                    tbCaptions.Glyph = "\uE8BE"; tbCaptions.Invalidate();
                    editor.DoLayout(); btnCaptions.Invalidate();
                });
            });
            t.IsBackground = true; t.Start();
        }

        void SetCap(string text, double f)
        {
            capText = f >= 0 ? text + (text.Contains("%") ? "" : "  " + (int)(f * 100) + "%") : text;
            capProgress = f;
            Activity.Update(capAct, f, text);
            Ui(() => { btnCaptions.Invalidate(); tbCaptions.Glyph = Ico.Stop; tbCaptions.Invalidate(); SetStatus(capText, Theme.InkSoft); });
        }

        /// Opens the captions in Notepad to fix words; the saved video uses whatever is in the file then.
        void EditCaptions()
        {
            if (subsFile == null || !File.Exists(subsFile)) return;
            try { Process.Start("notepad.exe", Tools.Quote(subsFile)); }
            catch { try { Process.Start(new ProcessStartInfo(subsFile) { UseShellExecute = true }); } catch { } }
            ShowToast(L.T("cap_edit_hint"), true, null, null);
        }
    }
}
