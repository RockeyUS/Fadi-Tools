using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FileConverter
{
    /// A video or song file, shared by every clip cut from it (strips are loaded once).
    public class MediaSource
    {
        public string File; public MediaInfo Info;
        public int W, H;
        public Image[] Strip; public Image Wave;
        public void Dispose()
        {
            if (Strip != null) foreach (Image i in Strip) if (i != null) i.Dispose();
            Strip = null;
            if (Wave != null) Wave.Dispose();
            Wave = null;
        }
    }

    /// The project timeline, CapCut style: every clip side by side, a red playhead running across all of them.
    /// Click to jump (and play from there), drag the white handles of the selected clip to trim it,
    /// drag a clip sideways to move it.
    public class Track : UiControl
    {
        public List<Clip> Clips = new List<Clip>();
        public int Selected;
        public double Playhead;                  // project time
        public bool Sequence;                    // showing the rendered "preview with effects"
        public Image[] SeqStrip; public double SeqDur;
        public event Action<double, bool> Seek;  // project time, mouse released
        public event Action<int> Picked;
        public event Action<int, int, bool> Trimmed;  // clip, edge (1 start / 2 end), finished
        public event Action<int, int> Moved;     // from, to
        // timed text: one bar per text under the clips (drag to move, pull an end to change when it shows)
        public List<TextLayer> Texts;
        public int SelectedText = -1;
        public event Action<int> TextPicked;
        public event Action<int, bool> TextTimed;   // text, finished
        int textDrag = -1, textEdge, hoverText = -1, hoverTextEdge;
        double textFrom0, textTo0;
        public bool HasTexts { get { if (Texts == null || Sequence) return false; foreach (TextLayer l in Texts) if (l.Visible) return true; return false; } }
        int LaneH { get { return HasTexts ? Theme.S(24) : 0; } }
        Rectangle Lane { get { Rectangle a = Area; return new Rectangle(a.X, a.Bottom + Theme.S(5), a.Width, LaneH - Theme.S(4)); } }
        RectangleF TextBar(int i)
        {
            TextLayer l = Texts[i];
            float x0 = X(Math.Max(0, Math.Min(Total, l.From))), x1 = X(l.To < 0 ? Total : Math.Max(l.From, Math.Min(Total, l.To)));
            Rectangle ln = Lane;
            return new RectangleF(x0, ln.Y, Math.Max(Theme.S(10), x1 - x0), ln.Height);
        }
        int TextAt(Point p, out int edge)
        {
            edge = 0;
            if (!HasTexts || !Lane.Contains(p) && !(p.Y >= Lane.Y - Theme.S(3) && p.Y <= Lane.Bottom + Theme.S(3))) return -1;
            int grab = Theme.S(7);
            // the selected one first (it's drawn on top)
            List<int> order = new List<int>();
            if (SelectedText >= 0 && SelectedText < Texts.Count) order.Add(SelectedText);
            for (int i = Texts.Count - 1; i >= 0; i--) if (i != SelectedText) order.Add(i);
            foreach (int i in order)
            {
                if (!Texts[i].Visible) continue;
                RectangleF b = TextBar(i);
                if (p.X < b.X - grab || p.X > b.Right + grab) continue;
                if (Math.Abs(p.X - b.X) <= grab) edge = 1; else if (Math.Abs(p.X - b.Right) <= grab) edge = 2;
                return i;
            }
            return -1;
        }

        int drag;                 // 0 none, 1 left handle, 2 right handle, 3 seek, 4 move clip
        int dragClip = -1, hoverHandle;
        double lockedPps;         // pixels per second, frozen while trimming so the layout doesn't jump
        double origStart, origEnd;
        int downX; bool moved; int insertAt = -1, mouseX;
        Bitmap cache; string cacheKey;

        public Track() { Cursor = Cursors.Hand; }

        Rectangle Area { get { return new Rectangle(Theme.S(14), Theme.S(6), Width - Theme.S(28), Height - Theme.S(30) - (HasTexts ? Theme.S(24) : 0)); } }
        public double Total
        {
            get
            {
                if (Sequence) return Math.Max(0.1, SeqDur);
                double t = 0; foreach (Clip c in Clips) t += c.Length;
                return Math.Max(0.1, t);
            }
        }
        double Pps { get { return lockedPps > 0 ? lockedPps : Area.Width / Total; } }
        int Gap { get { return Clips.Count > 1 && !Sequence ? Theme.S(3) : 0; } }

        public double OffsetOf(int i) { double t = 0; for (int k = 0; k < i && k < Clips.Count; k++) t += Clips[k].Length; return t; }

        /// The clip under a project time, and the time inside its source file.
        public int ClipAt(double t, out double local)
        {
            double acc = 0;
            for (int i = 0; i < Clips.Count; i++)
            {
                double len = Clips[i].Length;
                if (t < acc + len || i == Clips.Count - 1) { local = Clips[i].Start + Math.Max(0, Math.Min(len, t - acc)); return i; }
                acc += len;
            }
            local = 0; return -1;
        }

        RectangleF Block(int i)
        {
            Rectangle a = Area;
            double pps = Pps;
            float x = a.X + (float)(OffsetOf(i) * pps) + i * Gap;
            float w = (float)(Clips[i].Length * pps);
            return new RectangleF(x, a.Y, Math.Max(2, w), a.Height);
        }
        float X(double t)
        {
            Rectangle a = Area;
            if (Sequence || Clips.Count == 0) return a.X + (float)(t * Pps);
            double local; int i = ClipAt(t, out local);
            return a.X + (float)(t * Pps) + Math.Max(0, i) * Gap;
        }
        double TimeAtX(int x)
        {
            Rectangle a = Area;
            if (Sequence || Clips.Count == 0) return Math.Max(0, Math.Min(Total, (x - a.X) / Pps));
            for (int i = 0; i < Clips.Count; i++)
            {
                RectangleF b = Block(i);
                if (x < b.Right + Gap / 2f || i == Clips.Count - 1)
                    return Math.Max(0, Math.Min(Total, OffsetOf(i) + Math.Max(0, Math.Min(Clips[i].Length, (x - b.X) / Pps))));
            }
            return 0;
        }
        int BlockAt(int x)
        {
            for (int i = 0; i < Clips.Count; i++) { RectangleF b = Block(i); if (x >= b.X - Gap && x <= b.Right + Gap) return i; }
            return -1;
        }
        int HandleAt(int x, int y)
        {
            if (Sequence || Selected < 0 || Selected >= Clips.Count) return 0;
            RectangleF b = Block(Selected);
            if (y < b.Y - Theme.S(4) || y > b.Bottom + Theme.S(4)) return 0;
            int grab = Theme.S(10);
            if (Math.Abs(x - b.X) <= grab) return 1;
            if (Math.Abs(x - b.Right) <= grab) return 2;
            return 0;
        }

        // ------------------------------------------------------------------ mouse
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Total <= 0) return;
            Focus();
            downX = e.X; moved = false; mouseX = e.X;
            int te; int ti = TextAt(e.Location, out te);
            if (ti >= 0)
            {
                textDrag = ti; textEdge = te; textFrom0 = Texts[ti].From; textTo0 = Texts[ti].To < 0 ? Total : Texts[ti].To;
                SelectedText = ti; lockedPps = Area.Width / Total;
                if (TextPicked != null) TextPicked(ti);
                Capture = true; Invalidate();
                return;
            }
            int h = HandleAt(e.X, e.Y);
            if (h != 0)
            {
                drag = h; dragClip = Selected;
                lockedPps = Area.Width / Total;
                origStart = Clips[Selected].Start; origEnd = Clips[Selected].End;
                Capture = true; Invalidate();
                return;
            }
            if (!Sequence)
            {
                int b = BlockAt(e.X);
                if (b >= 0 && b != Selected) { Selected = b; if (Picked != null) Picked(b); }
                dragClip = b;
            }
            drag = 3;
            Playhead = TimeAtX(e.X);
            if (Seek != null) Seek(Playhead, false);
            Capture = true; Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouseX = e.X;
            if (textDrag >= 0)
            {
                TextLayer l = Texts[textDrag];
                double d = (e.X - downX) / lockedPps, total = Total, len = textTo0 - textFrom0;
                if (textEdge == 1) l.From = Math.Max(0, Math.Min(textTo0 - 0.2, textFrom0 + d));
                else if (textEdge == 2) { double to = Math.Max(textFrom0 + 0.2, Math.Min(total, textTo0 + d)); l.To = to >= total - 0.02 && textTo0 >= total - 0.02 && d >= 0 ? -1 : to; }
                else
                {
                    double from = Math.Max(0, Math.Min(total - len, textFrom0 + d));
                    l.From = from; if (l.To >= 0 || from > 0.001) l.To = Math.Min(total, from + len);
                }
                if (Math.Abs(e.X - downX) > 2 && TextTimed != null) TextTimed(textDrag, false);
                Invalidate();
                return;
            }
            if (drag == 0)
            {
                int te; int ti = TextAt(e.Location, out te);
                if (ti != hoverText || te != hoverTextEdge) { hoverText = ti; hoverTextEdge = te; Invalidate(); }
                int h = ti >= 0 ? 0 : HandleAt(e.X, e.Y);
                if (h != hoverHandle) { hoverHandle = h; Invalidate(); }
                Cursor = (h != 0 || (ti >= 0 && te != 0)) ? Cursors.SizeWE : ti >= 0 ? Cursors.SizeAll : Cursors.Hand;
                return;
            }
            if (Math.Abs(e.X - downX) > Theme.S(6)) moved = true;
            if (drag == 1 || drag == 2)
            {
                Clip c = Clips[dragClip];
                double d = (e.X - downX) / lockedPps;
                if (drag == 1) c.Start = Math.Max(0, Math.Min(origEnd - 0.1, origStart + d));
                else c.End = Math.Min(c.Info.Duration, Math.Max(origStart + 0.1, origEnd + d));
                if (Trimmed != null) Trimmed(dragClip, drag, false);
                Invalidate();
                return;
            }
            // dragging a clip sideways moves it; otherwise scrub the playhead
            if (drag == 3 && moved && dragClip >= 0 && Clips.Count > 1 && Math.Abs(e.Y - Area.Y - Area.Height / 2) < Area.Height && ModifierKeys == Keys.None && Math.Abs(e.X - downX) > Theme.S(24))
                drag = 4;
            if (drag == 4)
            {
                insertAt = Clips.Count;
                for (int i = 0; i < Clips.Count; i++) { RectangleF b = Block(i); if (e.X < b.X + b.Width / 2) { insertAt = i; break; } }
                Invalidate();
                return;
            }
            Playhead = TimeAtX(e.X);
            if (Seek != null) Seek(Playhead, false);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (textDrag >= 0)
            {
                int t = textDrag; textDrag = -1; lockedPps = 0; Capture = false;
                if (TextTimed != null) TextTimed(t, true);
                Invalidate();
                return;
            }
            int was = drag; drag = 0; Capture = false;
            if (was == 1 || was == 2)
            {
                lockedPps = 0;
                if (Trimmed != null) Trimmed(dragClip, was, true);
            }
            else if (was == 4)
            {
                int to = insertAt > dragClip ? insertAt - 1 : insertAt;
                insertAt = -1;
                if (to != dragClip && to >= 0 && Moved != null) Moved(dragClip, to);
            }
            else if (was == 3 && Seek != null) Seek(Playhead, true);
            Invalidate();
        }

        // ------------------------------------------------------------------ paint
        /// The clips (pictures + sound) drawn once; repainting the playhead is then cheap.
        Bitmap Blocks()
        {
            Rectangle a = Area;
            int filled = 0; Image wave0 = null;
            foreach (Clip c in Clips) { if (c.Strip != null) foreach (Image i in c.Strip) if (i != null) filled++; if (c.Wave != null) wave0 = c.Wave; }
            if (SeqStrip != null) foreach (Image i in SeqStrip) if (i != null) filled++;
            string key = Width + "x" + Height + "|" + Sequence + "|" + filled + "|" + (wave0 != null) + "|" + Theme.Dark + "|" + Pps.ToString("0.0000") + "|" + Clips.Count;
            foreach (Clip c in Clips) key += "|" + c.Start.ToString("0.000") + "-" + c.End.ToString("0.000") + (c.Wave != null ? "w" : "");
            if (cache != null && key == cacheKey) return cache;
            if (cache != null) cache.Dispose();
            cache = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
            cacheKey = key;
            using (Graphics g = Graphics.FromImage(cache))
            {
                Theme.Smooth(g);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                if (Sequence)
                {
                    RectangleF r = new RectangleF(a.X, a.Y, (float)(SeqDur * Pps), a.Height);
                    DrawTiles(g, r, i => SeqStrip != null && SeqStrip.Length > 0 ? SeqStrip[Math.Min(SeqStrip.Length - 1, (int)(i * SeqStrip.Length))] : null, null, 0, 1);
                }
                else
                    for (int i = 0; i < Clips.Count; i++)
                    {
                        Clip c = Clips[i];
                        double dur = Math.Max(0.01, c.Info.Duration);
                        RectangleF r = Block(i);
                        Image[] st = c.Strip;
                        DrawTiles(g, r, f =>
                        {
                            if (st == null || st.Length == 0) return null;
                            double t = c.Start + f * c.Length;
                            int k = (int)Math.Min(st.Length - 1, Math.Max(0, t / dur * st.Length));
                            for (int d = 0; d < st.Length; d++)   // nearest loaded frame
                            {
                                if (k + d < st.Length && st[k + d] != null) return st[k + d];
                                if (k - d >= 0 && st[k - d] != null) return st[k - d];
                            }
                            return null;
                        }, c.Info.HasVideo ? c.Wave : null, (float)(c.Start / dur), (float)(c.End / dur), !c.Info.HasVideo ? c.Wave : null);
                    }
            }
            return cache;
        }

        void DrawTiles(Graphics g, RectangleF r, Func<double, Image> frameAt, Image wave, float w0, float w1, Image audioWave = null)
        {
            using (GraphicsPath clip = Theme.Round(r, Theme.S(8)))
            {
                GraphicsState st = g.Save();
                g.SetClip(clip, CombineMode.Intersect);
                g.Clear(Color.Transparent);
                using (SolidBrush bg = new SolidBrush(Theme.Dark ? Color.FromArgb(30, 32, 36) : Color.FromArgb(214, 218, 224))) g.FillRectangle(bg, r);
                if (audioWave != null)
                {
                    // songs: the waveform fills the block
                    RectangleF src = new RectangleF(audioWave.Width * w0, 0, audioWave.Width * Math.Max(0.001f, w1 - w0), audioWave.Height);
                    g.DrawImage(audioWave, r, src, GraphicsUnit.Pixel);
                }
                else
                {
                    float tileW = Math.Max(Theme.S(30), r.Height * 16f / 9f);
                    for (float x = r.X; x < r.Right; x += tileW)
                    {
                        Image im = frameAt(Math.Min(1, (x + tileW / 2 - r.X) / Math.Max(1, r.Width)));
                        if (im == null) continue;
                        RectangleF cell = new RectangleF(x, r.Y, tileW + 1, r.Height);
                        float sc = Math.Max(cell.Width / im.Width, cell.Height / im.Height);
                        float w = im.Width * sc, h = im.Height * sc;
                        g.DrawImage(im, cell.X + (cell.Width - w) / 2, cell.Y + (cell.Height - h) / 2, w, h);
                    }
                    if (wave != null)
                    {
                        float bh = Math.Max(8, r.Height * 0.34f);
                        RectangleF band = new RectangleF(r.X, r.Bottom - bh, r.Width, bh);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(150, 10, 11, 13))) g.FillRectangle(b, band);
                        RectangleF src = new RectangleF(wave.Width * w0, 0, wave.Width * Math.Max(0.001f, w1 - w0), wave.Height);
                        g.DrawImage(wave, new RectangleF(band.X, band.Y + 2, band.Width, band.Height - 4), src, GraphicsUnit.Pixel);
                    }
                }
                g.Restore(st);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            Rectangle a = Area;
            if (Clips.Count == 0 && !Sequence) return;
            g.DrawImageUnscaled(Blocks(), 0, 0);

            Color sel = Theme.Dark ? Color.FromArgb(240, 241, 243) : Color.FromArgb(30, 33, 38);
            if (!Sequence)
                for (int i = 0; i < Clips.Count; i++)
                {
                    RectangleF r = Block(i);
                    bool s = i == Selected;
                    if (drag == 4 && i == dragClip) using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, Theme.Card))) g.FillRectangle(dim, r);
                    if (!s) { Theme.StrokeRound(g, r, Theme.S(8), Theme.CardBorder); continue; }
                    // the selected clip: a bright frame with trim handles
                    using (Pen p = new Pen(sel, Theme.S(2.5f))) using (GraphicsPath gp = Theme.Round(r, Theme.S(8))) g.DrawPath(p, gp);
                    for (int k = 0; k < 2; k++)
                    {
                        bool hot = hoverHandle == k + 1 || drag == k + 1;
                        float hw = Theme.S(hot ? 13 : 11);
                        float hx = k == 0 ? r.X : r.Right - hw;
                        RectangleF h = new RectangleF(hx, r.Y, hw, r.Height);
                        Theme.FillRound(g, h, Theme.S(5), sel);
                        using (Pen grip = new Pen(Theme.Dark ? Color.FromArgb(40, 42, 46) : Color.White, Theme.S(1.5f)))
                        {
                            float cx = hx + hw / 2, cy = r.Y + r.Height / 2;
                            g.DrawLine(grip, cx - Theme.S(1.5f), cy - Theme.S(8), cx - Theme.S(1.5f), cy + Theme.S(8));
                            g.DrawLine(grip, cx + Theme.S(1.5f), cy - Theme.S(8), cx + Theme.S(1.5f), cy + Theme.S(8));
                        }
                    }
                    // clip length while trimming
                    if (drag == 1 || drag == 2)
                    {
                        string lt = Theme.Time(Clips[i].Length, true);
                        Font f0 = Theme.F(8f, FontStyle.Bold);
                        int tw0 = Theme.Measure(lt, f0) + Theme.S(12);
                        Rectangle pr = new Rectangle((int)(r.X + r.Width / 2 - tw0 / 2), (int)r.Y + Theme.S(4), tw0, Theme.S(18));
                        Theme.FillRound(g, pr, Theme.S(9), Color.FromArgb(200, 0, 0, 0));
                        Theme.Text(g, lt, f0, Color.White, pr, Align.Center, true, false, true);
                    }
                }
            if (drag == 4 && insertAt >= 0)
            {
                float ix = insertAt < Clips.Count ? Block(insertAt).X - Gap / 2f - 1 : Block(Clips.Count - 1).Right + Gap / 2f;
                using (Pen p = new Pen(Theme.PrimaryBottom == Color.Empty ? sel : sel, Theme.S(3))) g.DrawLine(p, ix, a.Y - Theme.S(4), ix, a.Bottom + Theme.S(4));
                RectangleF ghost = Block(dragClip); ghost.X = mouseX - ghost.Width / 2; ghost.Y -= Theme.S(3);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, sel))) using (GraphicsPath gp = Theme.Round(ghost, Theme.S(8))) g.FillPath(b, gp);
            }

            // timed text bars
            if (HasTexts)
            {
                Rectangle ln = Lane;
                using (GraphicsPath lp = Theme.Round(ln, Theme.S(6))) using (SolidBrush lb = new SolidBrush(Theme.Mix(Theme.Card, Theme.Ink, 0.04f))) g.FillPath(lb, lp);
                List<int> order = new List<int>();
                for (int i = 0; i < Texts.Count; i++) if (i != SelectedText) order.Add(i);
                if (SelectedText >= 0 && SelectedText < Texts.Count) order.Add(SelectedText);
                foreach (int i in order)
                {
                    TextLayer l = Texts[i];
                    if (!l.Visible) continue;
                    RectangleF b = TextBar(i);
                    bool on = i == SelectedText, hot = i == hoverText;
                    Color fill = on ? Theme.AccentFill : Theme.Mix(Theme.AccentSoft, Theme.AccentColor, hot ? 0.45f : 0.25f);
                    Theme.FillRound(g, b, Theme.S(6), fill);
                    if (!on) Theme.StrokeRound(g, b, Theme.S(6), Theme.Mix(fill, Theme.AccentColor, 0.5f));
                    string label = l.Label;
                    Rectangle tr = Rectangle.Round(new RectangleF(b.X + Theme.S(18), b.Y, Math.Max(0, b.Width - Theme.S(24)), b.Height));
                    Theme.Glyph(g, l.IsPicture ? Ico.Photo : Ico.Text, 6.5f, on ? Theme.PrimaryInk : Theme.AccentColor, Rectangle.Round(new RectangleF(b.X + Theme.S(3), b.Y, Theme.S(14), b.Height)));
                    if (tr.Width > Theme.S(12)) Theme.Text(g, label, Theme.F(7.75f, FontStyle.Bold), on ? Theme.PrimaryInk : Theme.Ink, tr, Align.Start, true, false, true);
                    if (on || (hot && hoverTextEdge != 0))
                        using (SolidBrush hb = new SolidBrush(on ? Theme.PrimaryInk : Theme.AccentColor))
                        {
                            g.FillRectangle(hb, b.X + Theme.S(2), b.Y + b.Height / 2 - Theme.S(4), Theme.S(2), Theme.S(8));
                            g.FillRectangle(hb, b.Right - Theme.S(4), b.Y + b.Height / 2 - Theme.S(4), Theme.S(2), Theme.S(8));
                        }
                }
                if (textDrag >= 0)
                {
                    // when it shows, while dragging
                    TextLayer l = Texts[textDrag];
                    string lt = Theme.Time(l.From, true) + " → " + (l.To < 0 ? L.T("tt_end") : Theme.Time(l.To, true));
                    Font f0 = Theme.F(8f, FontStyle.Bold);
                    int tw0 = Theme.Measure(lt, f0) + Theme.S(14);
                    RectangleF b = TextBar(textDrag);
                    Rectangle pr = new Rectangle((int)Math.Max(0, Math.Min(Width - tw0, b.X + b.Width / 2 - tw0 / 2)), a.Bottom - Theme.S(22), tw0, Theme.S(18));
                    Theme.FillRound(g, pr, Theme.S(9), Color.FromArgb(220, 0, 0, 0));
                    Theme.Text(g, lt, f0, Color.White, pr, Align.Center, true, false, true);
                }
            }

            // time marks
            Font f = Theme.F(8f);
            double total = Total;
            float phx = X(Playhead);
            int marksY = a.Bottom + Theme.S(6) + LaneH;
            for (int i = 0; i <= 4; i++)
            {
                double t = total * i / 4;
                float x = X(t);
                if (Math.Abs(x - phx) < Theme.S(48)) continue;
                Rectangle tr = new Rectangle((int)x - Theme.S(40), marksY, Theme.S(80), Theme.S(16));
                if (i == 0) tr.X = (int)x - Theme.S(4); if (i == 4) tr.X = (int)x - Theme.S(76);
                TextRenderer.DrawText(g, Theme.Time(t), f, tr, Theme.InkFaint, (i == 0 ? TextFormatFlags.Left : i == 4 ? TextFormatFlags.Right : TextFormatFlags.HorizontalCenter) | TextFormatFlags.NoPrefix);
            }
            // playhead
            Color ph = Color.FromArgb(239, 68, 68);
            int phBottom = a.Bottom + Theme.S(3) + LaneH;
            using (Pen p = new Pen(Color.FromArgb(120, 0, 0, 0), Theme.S(4))) g.DrawLine(p, phx, a.Y - Theme.S(3), phx, phBottom);
            using (Pen p = new Pen(ph, Theme.S(2))) g.DrawLine(p, phx, a.Y - Theme.S(3), phx, phBottom);
            using (SolidBrush b = new SolidBrush(ph)) g.FillEllipse(b, phx - Theme.S(5), a.Y - Theme.S(6), Theme.S(10), Theme.S(10));
            string tl = Theme.Time(Playhead, true);
            Font bf = Theme.F(8f, FontStyle.Bold);
            int tw = Theme.Measure(tl, bf) + Theme.S(12);
            Rectangle pill = new Rectangle((int)phx - tw / 2, marksY - Theme.S(2), tw, Theme.S(18));
            pill.X = Math.Max(0, Math.Min(Width - tw, pill.X));
            Theme.FillRound(g, pill, Theme.S(9), ph);
            Theme.Text(g, tl, bf, Color.White, pill, Align.Center, true, false, true);
        }

        public void Changed() { Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing && cache != null) cache.Dispose(); base.Dispose(disposing); }
    }
}
