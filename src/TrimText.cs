using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace FileConverter
{
    /// Timed text on videos: each text shows from one moment to another (a bar on the timeline you can drag),
    /// and the saved video switches it on and off at exactly those times.
    public partial class TrimPage
    {
        Btn bTtStart, bTtEnd, bTtWhole;
        Rectangle ttLblR, ttInfoR;
        bool hadTexts;

        void InitTimedText()
        {
            bTtStart = new Btn(() => L.T("tt_start_here"), null, BtnKind.Secondary) { FontSize = 8.75f };
            bTtEnd = new Btn(() => L.T("tt_end_here"), null, BtnKind.Secondary) { FontSize = 8.75f };
            bTtWhole = new Btn(() => L.T("tt_whole"), null, BtnKind.Secondary) { FontSize = 8.75f };
            bTtStart.MouseEnter += (s, e) => tbTips.SetToolTip(bTtStart, L.T("tt_start_tip"));
            bTtEnd.MouseEnter += (s, e) => tbTips.SetToolTip(bTtEnd, L.T("tt_end_tip"));
            bTtStart.Click += (s, e) => SetTiming(0);
            bTtEnd.Click += (s, e) => SetTiming(1);
            bTtWhole.Click += (s, e) => SetTiming(2);
            editor.AddToTiming(bTtStart, bTtEnd, bTtWhole);
            editor.TextTimingLayout = TimingLayout;
            editor.TextTimingPaint = TimingPaint;
            editor.PrepareNewText = t =>
            {
                // CapCut-style: a new text starts at the red line and stays for 4 seconds
                double total = timeline.Total, ph = seqMode ? 0 : timeline.Playhead;
                if (total < 2) return;
                if (ph > total - 1) ph = 0;
                t.From = ph; t.To = Math.Min(total, ph + 4);
                if (t.To >= total - 0.05) t.To = -1;
            };
            editor.LayerSelected += i =>
            {
                timeline.SelectedText = i; timeline.Invalidate();
                if (i >= 0 && i < editor.S.Layers.Count && editor.S.Video && !seqMode && !editor.S.Layers[i].ShownAt(timeline.Playhead))
                    SeekTo(editor.S.Layers[i].From + 0.05);
            };
            editor.Changed += () =>
            {
                bool has = timeline.HasTexts;
                if (has != hadTexts) { hadTexts = has; DoLayout(); }
                timeline.Invalidate();
            };
            timeline.Texts = editor.S.Layers;
            timeline.TextPicked += i => { editor.SelectTab(EdTabPublic.Text); if (editor.S.Selected != i) editor.Select(i); };
            timeline.TextTimed += (i, final) =>
            {
                editor.S.Dirty = true;
                editor.Body.Invalidate();
                view.Invalidate();
                if (final) { editor.DoLayout(); seqKey = null; }
            };
            view.LayerShown = l => !editor.S.Video || (!seqMode && l.ShownAt(timeline.Playhead));
        }

        /// Moves the red line (and the picture) to a time.
        void SeekTo(double t)
        {
            StopPlay();
            t = Math.Max(0, Math.Min(timeline.Total, t));
            timeline.Playhead = t;
            double local; int i = timeline.ClipAt(t, out local);
            if (i >= 0 && i != cur) { cur = i; timeline.Selected = cur; SyncFields(); editor.DoLayout(); UpdateToolbar(); }
            view.Caption = Theme.Time(t, true) + "  /  " + Theme.Time(timeline.Total, true);
            RequestFrame(local, false, false);
            timeline.Invalidate(); view.Invalidate();
        }

        /// 0 = starts at the red line, 1 = ends at the red line, 2 = the whole video.
        void SetTiming(int what)
        {
            TextLayer l = editor.S.Current;
            if (l == null || seqMode) return;
            double ph = timeline.Playhead, total = timeline.Total;
            if (what == 0)
            {
                double end = l.To < 0 ? total : l.To;
                l.From = Math.Min(ph, Math.Max(0, end - 0.2));
                if (end - l.From < 0.2) l.To = Math.Min(total, l.From + 2);
            }
            else if (what == 1)
            {
                if (ph <= l.From + 0.1) { ShowToast(L.T("tt_end_before"), false, null, null); return; }
                l.To = ph >= total - 0.05 ? -1 : ph;
            }
            else { l.From = 0; l.To = -1; }
            editor.S.Dirty = true; seqKey = null;
            editor.DoLayout(); timeline.Invalidate(); view.Invalidate();
        }

        int TimingLayout(int y, int w)
        {
            int x = editor.Pad, gap = Theme.S(6);
            ttLblR = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
            ttInfoR = new Rectangle(x, y, w, Theme.S(34)); y += Theme.S(40);
            int bw = (w - gap * 2) / 3;
            editor.BodyPlace(bTtStart, new Rectangle(x, y, bw, Theme.S(34)));
            editor.BodyPlace(bTtEnd, new Rectangle(x + bw + gap, y, bw, Theme.S(34)));
            editor.BodyPlace(bTtWhole, new Rectangle(x + (bw + gap) * 2, y, w - (bw + gap) * 2, Theme.S(34)));
            foreach (Btn b in new[] { bTtStart, bTtEnd, bTtWhole }) { b.Visible = true; b.Enabled = !seqMode && !running; }
            y += Theme.S(46);
            return y;
        }

        void TimingPaint(Graphics g)
        {
            TextLayer l = editor.S.Current;
            if (l == null) return;
            int bw = editor.Body.Width;
            Func<Rectangle, Rectangle> m = r => Theme.Mirror(r, bw);
            Theme.Text(g, L.T("tt_on_screen"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, m(ttLblR), Align.Start);
            Rectangle r0 = m(ttInfoR);
            Theme.FillRound(g, r0, Theme.S(9), Theme.AccentSoft);
            Rectangle ic = Theme.RTL ? new Rectangle(r0.Right - Theme.S(32), r0.Y, Theme.S(26), r0.Height) : new Rectangle(r0.X + Theme.S(6), r0.Y, Theme.S(26), r0.Height);
            Theme.Glyph(g, Ico.Clock, 9f, Theme.AccentColor, ic);
            double total = timeline.Total, to = l.To < 0 ? total : l.To;
            bool whole = l.From <= 0.001 && l.To < 0;
            string t = whole ? L.T("tt_whole_long", Theme.Time(total, true)) : Theme.Time(l.From, true) + "  →  " + (l.To < 0 ? L.T("tt_end") : Theme.Time(l.To, true)) + "   ·   " + L.T("tt_secs", (to - l.From).ToString("0.0"));
            Rectangle tr = Theme.RTL ? new Rectangle(r0.X + Theme.S(8), r0.Y, r0.Width - Theme.S(44), r0.Height) : new Rectangle(r0.X + Theme.S(36), r0.Y, r0.Width - Theme.S(44), r0.Height);
            // dark/normal ink: any accent (even red) stays readable on its soft tint
            Theme.Text(g, t, Theme.F(9f, FontStyle.Bold), Theme.Ink, tr, Align.Start, true, false, !Theme.RTL || !whole);
        }

        // ------------------------------------------------------------------ export
        bool HasTimedText
        {
            get
            {
                foreach (TextLayer l in editor.S.Layers)
                    if (l.Visible && (l.From > 0.001 || l.To >= 0)) return true;
                return false;
            }
        }

        /// Timeline time → time in the saved video (transitions overlap clips; speed and reverse change the clock).
        double MapTime(double t)
        {
            int n = clips.Count;
            if (n == 0) return t;
            double[] len = new double[n]; double minLen = double.MaxValue, sum = 0;
            for (int i = 0; i < n; i++) { len[i] = Math.Max(0.1, clips[i].Length); minLen = Math.Min(minLen, len[i]); sum += len[i]; }
            bool xf = TransIndex > 0 && n > 1;
            double d = xf ? Math.Max(0.1, Math.Min(fxDur.Value, minLen / 2 - 0.05)) : 0;
            double acc = 0, outAcc = 0, x = 0;
            for (int i = 0; i < n; i++)
            {
                if (t <= acc + len[i] || i == n - 1) { x = outAcc + Math.Max(0, Math.Min(len[i], t - acc)); break; }
                acc += len[i]; outAcc += len[i] - d;
            }
            double total = sum - d * (n - 1);
            if (reverse.Checked) x = total - x;
            double speed = Speeds[Math.Max(0, Math.Min(Speeds.Length - 1, speedSeg.SelectedIndex))];
            return Math.Max(0, x / Math.Max(0.25, speed));
        }

        /// One transparent picture per group of texts that show at the same times. scaleW/H > 0 = smaller (preview).
        List<Media.Overlay> MakeOverlays(Func<double, double> map, int scaleW, int scaleH)
        {
            EditSession s = editor.S;
            List<Media.Overlay> list = new List<Media.Overlay>();
            Dictionary<string, List<TextLayer>> groups = new Dictionary<string, List<TextLayer>>();
            List<string> order = new List<string>();
            foreach (TextLayer l in s.Layers)
            {
                if (!l.Visible) continue;
                string key = Math.Round(l.From, 2) + "|" + Math.Round(l.To, 2);
                if (!groups.ContainsKey(key)) { groups[key] = new List<TextLayer>(); order.Add(key); }
                groups[key].Add(l);
            }
            foreach (string key in order)
            {
                List<TextLayer> g = groups[key];
                string png = Path.Combine(Path.GetTempPath(), "fadi_overlay_" + Guid.NewGuid().ToString("N") + ".png");
                using (Bitmap b = new Bitmap(s.W, s.H, PixelFormat.Format32bppArgb))
                {
                    using (Graphics gr = Graphics.FromImage(b))
                    {
                        gr.Clear(Color.Transparent);
                        gr.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                        // keep the stacking order of the whole set
                        foreach (TextLayer l in s.Layers) if (g.Contains(l)) l.Draw(gr);
                    }
                    if (scaleW > 0 && scaleH > 0) using (Bitmap small = EditSession.Scale(b, scaleW, scaleH)) small.Save(png, ImageFormat.Png);
                    else b.Save(png, ImageFormat.Png);
                }
                TextLayer f = g[0];
                Media.Overlay o = new Media.Overlay { Png = png };
                if (f.From > 0.001 || f.To >= 0)
                {
                    double a = map(Math.Max(0, f.From)), z = f.To >= 0 ? map(f.To) : -1;
                    if (z >= 0 && a > z) { double tmp = a; a = z; z = tmp; }
                    else if (z < 0 && reverse.Checked) { z = a; a = 0; }   // "until the end" played backwards = from the start
                    o.From = a; o.To = z;
                }
                list.Add(o);
            }
            return list;
        }

        string TextKey()
        {
            StringBuilder k = new StringBuilder();
            foreach (TextLayer l in editor.S.Layers)
                k.Append(l.Text).Append('|').Append(l.FontName).Append(Math.Round(l.SizePx)).Append(l.Color.ToArgb()).Append(l.Bold).Append(l.Outline).Append(l.Shadow).Append(l.Box)
                 .Append(Math.Round(l.Pos.X)).Append(',').Append(Math.Round(l.Pos.Y)).Append(l.PicturePath).Append(Math.Round(l.Angle, 1)).Append(l.OutlineColor.ToArgb()).Append(Math.Round(l.OutlineWidth, 2)).Append('@').Append(Math.Round(l.From, 2)).Append('-').Append(Math.Round(l.To, 2)).Append(';');
            // captions fixed in Notepad: the preview is made again
            try { if (subsFile != null && File.Exists(subsFile)) k.Append(File.GetLastWriteTimeUtc(subsFile).Ticks); } catch { }
            return k.ToString();
        }
    }
}
