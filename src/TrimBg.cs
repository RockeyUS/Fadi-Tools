using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Studio videos: take the background out of the selected clip (transparent, a color, blurred or a picture).
    public partial class TrimPage
    {
        Btn btnVbg;
        bool vbgBusy, vbgStop;
        static readonly Color[] VbgColors = { Color.FromArgb(0, 177, 64), Color.White, Color.Black, Color.FromArgb(37, 99, 235), Color.FromArgb(244, 114, 182) };

        void InitVideoBg()
        {
            btnVbg = new Btn(() => vbgBusy ? L.T("vbg_stop") : L.T("vbg_btn"), "", BtnKind.Secondary);
            btnVbg.Click += (s, e) => { if (vbgBusy) vbgStop = true; else RemoveVideoBg(); };
            editor.AddToEffects(btnVbg);
        }

        public void RemoveVideoBg()
        {
            if (vbgBusy || running || clips.Count == 0) return;
            Clip c = clips[Math.Max(0, Math.Min(clips.Count - 1, cur))];
            if (c.Info == null || !c.Info.HasVideo) { ShowToast(L.T("vbg_no_video"), false, null, null); return; }
            double fps = VideoBg.Fps(c.Info);
            int frames = Math.Max(1, (int)Math.Ceiling(c.Length * fps));
            int minutes = Math.Max(1, (int)Math.Round(frames * 1.6 / 60));

            Segments mode = new Segments(() => L.T("vbg_m_clear"), () => L.T("vbg_m_color"), () => L.T("vbg_m_blur"), () => L.T("vbg_m_pic")) { FontSize = 9f };
            Swatches color = new Swatches { Palette = VbgColors, LivePreview = false };
            int m0, c0; int.TryParse(Settings.Get("VbgMode", "0"), out m0); int.TryParse(Settings.Get("VbgColor", "0"), out c0);
            mode.SetSilently(Math.Max(0, Math.Min(3, m0)));
            color.Selected = VbgColors[Math.Max(0, Math.Min(VbgColors.Length - 1, c0))];
            string msg = L.T("vbg_msg", Theme.Time(c.Length, false), minutes) + (BgRemover.Ready ? "" : "\n\n" + L.T("vbg_need_dl"));
            using (FormDialog d = new FormDialog(L.T("vbg_title"), msg, L.T("vbg_start")))
            {
                d.Add(() => L.T("vbg_new_bg"), mode, 36);
                d.Add(() => L.T("vbg_color"), color, 34);
                if (Ask.ShowDimmed(d, FindForm()) != DialogResult.OK) return;
            }
            int md = mode.SelectedIndex; Color col = color.Selected;
            Settings.Set("VbgMode", md.ToString()); Settings.Set("VbgColor", Math.Max(0, Array.FindIndex(VbgColors, k => k.ToArgb() == col.ToArgb())).ToString());
            string picture = null;
            if (md == 3)
            {
                using (OpenFileDialog o = new OpenFileDialog { Title = L.T("vbg_pick_pic"), Filter = L.T("ed_photo_files") + "|*.jpg;*.jpeg;*.png;*.webp;*.heic;*.avif;*.bmp|" + L.T("all_files") + "|*.*" })
                    if (LastDir.Show(o, FindForm(), "photo") == DialogResult.OK) picture = o.FileName; else return;
            }
            string ext = md == 0 ? "webm" : "mp4";
            string outp = SaveAsk.File(FindForm(), Path.GetFileNameWithoutExtension(c.File) + " " + L.T("vbg_suffix") + "." + ext, ext);
            if (outp == null) return;

            string input = c.File; double start = c.Start, len = c.Length;
            vbgBusy = true; vbgStop = false; btnVbg.Glyph = Ico.Stop; btnVbg.Invalidate(); editor.DoLayout();
            ActJob job = Activity.Begin(this, L.T("vbg_job"), Path.GetFileName(input), () => vbgStop = true);
            Thread t = new Thread(() =>
            {
                string err = null;
                try
                {
                    if (!BgRemover.Ready)
                    {
                        Activity.Update(job, -1, L.T("vbg_dl"));
                        BgRemover.Install((f, label) => Activity.Update(job, f * 0.1, label), () => vbgStop);
                        if (vbgStop) throw new OperationCanceledException();
                        if (!BgRemover.Ready) throw new UserError(L.T("vbg_need_ai"));
                    }
                    DateTime t0 = DateTime.Now;
                    VideoBg.Run(input, start, len, md, col, picture, outp, (n, total) =>
                    {
                        double per = (DateTime.Now - t0).TotalSeconds / n;
                        int left = (int)Math.Ceiling(per * (total - n) / 60);
                        Activity.Update(job, n / (double)total, n + " / " + total + (n >= 3 ? "  ·  " + L.T("vbg_left", Math.Max(1, left)) : ""));
                    }, () => vbgStop || Tools.ShuttingDown);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                bool stopped = vbgStop;
                Activity.End(job, err, stopped, L.T("vbg_done"), outp);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        vbgBusy = false; btnVbg.Glyph = ""; btnVbg.Invalidate(); editor.DoLayout();
                        if (stopped) ShowToast(L.T("cancelled"), true, null, null);
                        else if (err != null) ShowToast(err, false, null, null);
                        else ShowToast(L.T("vbg_done_file", Path.GetFileName(outp)), true, () => L.T("show_file"), () => ActivityRows.ShowOutput(outp));
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }
    }
}
