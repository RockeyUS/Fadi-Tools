using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// "Read aloud": type (or paste) a text, pick a voice and a speed, listen, and add it to the video as a sound.
    /// Arabic text picks the Arabic voice by itself. Everything runs on this PC.
    public class TtsForm : Form
    {
        readonly MultiField text = new MultiField();
        readonly Segments voice;
        readonly Slider speed = new Slider();
        readonly Btn bListen, bAdd, bCancel;
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        Rectangle textLbl, voiceLbl, speedLbl, statusR;
        string status; Color statusColor;
        bool busy, userPicked;
        System.Media.SoundPlayer player;
        /// The WAV made for "Add to video" (set when the dialog closes with OK).
        public string Result;
        readonly string saveDir;
        /// Captions mode: every caption is read on its own, to be placed at its own time.
        readonly List<Cue> cues;
        /// Captions mode: the voice of each caption and when it starts on the timeline.
        public readonly List<KeyValuePair<string, double>> Results = new List<KeyValuePair<string, double>>();

        public TtsForm(string startText, string saveDir) : this(startText, saveDir, null) { }

        /// cues: read each caption aloud (the text box shows them; they're read as they are).
        public TtsForm(string startText, string saveDir, List<Cue> cues)
        {
            this.saveDir = saveDir;
            this.cues = cues != null && cues.Count > 0 ? cues : null;
            if (this.cues != null)
            {
                StringBuilder sb = new StringBuilder();
                foreach (Cue q in this.cues) if (!string.IsNullOrEmpty((q.Text ?? "").Trim())) sb.AppendLine(q.Text.Trim());
                startText = sb.ToString().TrimEnd();
            }
            Movable.Make(this, () => Theme.S(64));
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            Font = Theme.F(10f); BackColor = Theme.Card; Text = L.T("tts_title");
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            int W = Theme.S(520), pad = Theme.S(26), cw = W - pad * 2;
            voice = new Segments(() => L.T("tts_v_en_f"), () => L.T("tts_v_en_m"), () => L.T("tts_v_ar_m")) { FontSize = 8.75f };
            int vi = Array.FindIndex(Tts.Voices, v => v.Id == Settings.Get("TtsVoice", Tts.Voices[0].Id));
            voice.SetSilently(Math.Max(0, vi));
            voice.SelectedChanged += (s, e) => { userPicked = true; Settings.Set("TtsVoice", Tts.Voices[voice.SelectedIndex].Id); status = null; Invalidate(); };
            speed.Min = 50; speed.Max = 200;
            float sp; if (!float.TryParse(Settings.Get("TtsSpeed", "100"), NumberStyles.Float, CultureInfo.InvariantCulture, out sp)) sp = 100;
            speed.Value = Math.Max(50, Math.Min(200, sp));
            speed.Format = v => ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) + "%";
            speed.Committed += (s, e) => Settings.Set("TtsSpeed", ((int)Math.Round(speed.Value)).ToString(CultureInfo.InvariantCulture));
            text.Box.Text = (startText ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            text.Box.ScrollBars = ScrollBars.Vertical;
            if (this.cues != null) text.Box.ReadOnly = true;
            text.Box.TextChanged += (s, e) => { AutoVoice(); status = null; Invalidate(); };
            if (Cue.IsRtl(text.Box.Text)) { text.Box.RightToLeft = RightToLeft.Yes; }
            int y = Theme.S(84);
            textLbl = new Rectangle(pad, y, cw, Theme.S(20)); y += Theme.S(24);
            text.Bounds = Theme.Mirror(new Rectangle(pad, y, cw, Theme.S(130)), W); y += Theme.S(144);
            voiceLbl = new Rectangle(pad, y, cw, Theme.S(20)); y += Theme.S(24);
            voice.Bounds = Theme.Mirror(new Rectangle(pad, y, cw, Theme.S(36)), W); y += Theme.S(50);
            speedLbl = new Rectangle(pad, y, Theme.S(90), Theme.S(34));
            speed.Bounds = Theme.Mirror(new Rectangle(pad + Theme.S(90), y, cw - Theme.S(90), Theme.S(34)), W); y += Theme.S(44);
            statusR = new Rectangle(pad, y, cw, Theme.S(40)); y += Theme.S(48);
            bAdd = new Btn(() => this.cues != null ? L.T("tts_add_caps", Spoken) : L.T("tts_add"), Ico.Add, BtnKind.Primary);
            bAdd.Click += (s, e) => Make(true);
            bListen = new Btn(() => L.T("tts_listen"), Ico.Play, BtnKind.Secondary);
            bListen.Click += (s, e) => Make(false);
            bCancel = new Btn(() => L.T("cancel"), null, BtnKind.Ghost);
            bCancel.Click += (s, e) => { if (!busy) { DialogResult = DialogResult.Cancel; Close(); } };
            int aw = Math.Max(Theme.S(150), bAdd.PreferredWidth + Theme.S(20)), lw = Math.Max(Theme.S(110), bListen.PreferredWidth + Theme.S(16)), xw = Math.Max(Theme.S(90), bCancel.PreferredWidth + Theme.S(12));
            bAdd.Bounds = Theme.Mirror(new Rectangle(W - pad - aw, y, aw, Theme.S(40)), W);
            bListen.Bounds = Theme.Mirror(new Rectangle(W - pad - aw - Theme.S(10) - lw, y, lw, Theme.S(40)), W);
            bCancel.Bounds = Theme.Mirror(new Rectangle(pad - Theme.S(8), y + Theme.S(2), xw, Theme.S(36)), W);
            ClientSize = new Size(W, y + Theme.S(40) + pad);
            foreach (UiControl c in new UiControl[] { text, voice, speed, bAdd, bListen, bCancel }) { c.Surface = Surface.Card; Controls.Add(c); }
            AutoVoice();
            Shown += (s, e) => { text.Box.Focus(); text.Box.SelectionStart = text.Box.Text.Length; };
        }

        /// Arabic letters pick the Arabic voice (and Latin ones an English voice), until a voice is picked by hand.
        void AutoVoice()
        {
            // captions: by most of them (each is still read in its own language's voice)
            bool ar = cues != null ? cues.FindAll(q => Cue.IsRtl(q.Text)).Count * 2 > cues.Count : Cue.IsRtl(text.Box.Text);
            text.Box.RightToLeft = ar ? RightToLeft.Yes : RightToLeft.No;
            if (userPicked || text.Box.Text.Trim().Length == 0) return;
            bool isAr = Tts.Voices[voice.SelectedIndex].Lang == "ar";
            if (ar && !isAr) voice.SetSilently(Array.FindIndex(Tts.Voices, v => v.Lang == "ar"));
            else if (!ar && isAr) voice.SetSilently(0);
        }

        Tts.Voice Picked { get { return Tts.Voices[Math.Max(0, voice.SelectedIndex)]; } }

        void Ui(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }

        /// Reads the text into a WAV: to listen to it here, or (add) to put it in the video.
        void Make(bool add)
        {
            if (busy) return;
            if (add && cues != null) { MakeAll(); return; }
            string t = text.Box.Text.Trim();
            if (t.Length == 0) { status = L.T("tts_empty"); statusColor = Theme.Error; Invalidate(); return; }
            Tts.Voice v = Picked;
            long need = Tts.Needed(v);
            if (need > 0 && !Ask.Confirm(this, L.T("tts_dl_q_t"), L.T("tts_dl_q", (need + (1 << 20) - 1) >> 20), L.T("install"), L.T("not_now"), AskKind.Info)) return;
            busy = true; bAdd.Enabled = bListen.Enabled = false;
            status = L.T("tts_reading"); statusColor = Theme.InkSoft; Invalidate();
            float sp = speed.Value / 100f;
            StopListening();
            string wav = add ? NewPath() : Path.Combine(Path.GetTempPath(), "fadi_tts_listen.wav");
            Thread th = new Thread(() =>
            {
                string err = null;
                try
                {
                    if (Tts.Needed(v) > 0) Tts.Install(v, (f, what) => Ui(() => { status = what + "  " + (int)(f * 100) + "%"; Invalidate(); }), () => IsDisposed);
                    Ui(() => { status = L.T("tts_reading"); Invalidate(); });
                    Tts.Speak(t, v, sp, wav);
                }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    busy = false; bAdd.Enabled = bListen.Enabled = true;
                    if (err != null) { status = err; statusColor = Theme.Error; Invalidate(); return; }
                    if (add) { Result = wav; StopListening(); DialogResult = DialogResult.OK; Close(); return; }
                    status = L.T("tts_playing"); statusColor = Theme.Success; Invalidate();
                    try { player = new System.Media.SoundPlayer(wav); player.Play(); } catch { }
                });
            }) { IsBackground = true };
            th.Start();
        }

        int Spoken { get { return cues == null ? 0 : cues.FindAll(q => !string.IsNullOrEmpty((q.Text ?? "").Trim())).Count; } }

        /// Captions mode: each caption read into its own WAV. A caption whose voice runs longer than the caption is read
        /// a little faster (up to 1.6×) so it ends near where the next one begins.
        void MakeAll()
        {
            Tts.Voice v = Picked;
            List<Cue> todo = cues.FindAll(q => !string.IsNullOrEmpty((q.Text ?? "").Trim()));
            // each caption in a voice of its own language: Arabic captions with the Arabic voice, the rest with the
            // picked voice (or the first English one when Arabic was picked)
            Tts.Voice ar = Array.Find(Tts.Voices, x => x.Lang == "ar"), en = v.Lang == "ar" ? Tts.Voices[0] : v;
            Func<Cue, Tts.Voice> voiceOf = q => Cue.IsRtl(q.Text) ? ar : en;
            List<Tts.Voice> used = new List<Tts.Voice>();
            foreach (Cue q in todo) { Tts.Voice x = voiceOf(q); if (!used.Contains(x)) used.Add(x); }
            long need = 0;
            for (int i = 0; i < used.Count; i++) need += i == 0 ? Tts.Needed(used[i]) : (Tts.VoiceReady(used[i]) ? 0 : used[i].Bytes);
            if (need > 0 && !Ask.Confirm(this, L.T("tts_dl_q_t"), L.T("tts_dl_q", (need + (1 << 20) - 1) >> 20), L.T("install"), L.T("not_now"), AskKind.Info)) return;
            if (todo.Count == 0) { status = L.T("tts_empty"); statusColor = Theme.Error; Invalidate(); return; }
            busy = true; bAdd.Enabled = bListen.Enabled = false;
            float sp = speed.Value / 100f;
            StopListening();
            string stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);
            string dir = saveDir;
            try { Directory.CreateDirectory(dir); } catch { dir = Path.GetTempPath(); }
            Thread th = new Thread(() =>
            {
                string err = null;
                List<KeyValuePair<string, double>> made = new List<KeyValuePair<string, double>>();
                try
                {
                    foreach (Tts.Voice x in used)
                        if (Tts.Needed(x) > 0) Tts.Install(x, (f, what) => Ui(() => { status = what + "  " + (int)(f * 100) + "%"; Invalidate(); }), () => IsDisposed);
                    for (int i = 0; i < todo.Count; i++)
                    {
                        if (IsDisposed) return;
                        int n = i + 1;
                        Ui(() => { status = L.T("tts_reading_n", n, todo.Count); statusColor = Theme.InkSoft; Invalidate(); });
                        Cue q = todo[i];
                        string wav = Path.Combine(dir, L.T("tts_file") + " " + stamp + " " + n.ToString("000", CultureInfo.InvariantCulture) + ".wav");
                        Tts.Voice qv = voiceOf(q);
                        double len = Tts.Speak(q.Text, qv, sp, wav), room = Math.Max(0.3, q.End - q.Start);
                        if (len > room + 0.3 && sp < 1.6f) Tts.Speak(q.Text, qv, Math.Min(1.6f, sp * (float)(len / room)), wav);
                        made.Add(new KeyValuePair<string, double>(wav, q.Start));
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    busy = false; bAdd.Enabled = bListen.Enabled = true;
                    if (err != null) { status = err; statusColor = Theme.Error; Invalidate(); return; }
                    Results.AddRange(made);
                    DialogResult = DialogResult.OK; Close();
                });
            }) { IsBackground = true };
            th.Start();
        }

        string NewPath()
        {
            string d = saveDir;
            try { Directory.CreateDirectory(d); } catch { d = Path.GetTempPath(); }
            return Path.Combine(d, L.T("tts_file") + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + ".wav");
        }

        void StopListening() { if (player != null) { try { player.Stop(); player.Dispose(); } catch { } player = null; } }
        protected override void OnFormClosed(FormClosedEventArgs e) { StopListening(); base.OnFormClosed(e); }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int r = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r, r)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape && !busy) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (k == (Keys.Control | Keys.Enter)) { Make(true); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            using (Pen p = new Pen(Theme.CardBorder)) using (GraphicsPath gp = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9))) g.DrawPath(p, gp);
            int pad = Theme.S(26), W = Width;
            Func<Rectangle, Rectangle> m = r => Theme.Mirror(r, W);
            Rectangle ic = m(new Rectangle(pad, Theme.S(22), Theme.S(42), Theme.S(42)));
            Theme.FillRound(g, ic, Theme.S(11), Theme.AccentSoft);
            Theme.Glyph(g, Ico.Volume, 13f, Theme.AccentColor, ic);
            Theme.Text(g, L.T(cues != null ? "tts_caps_title" : "tts_title"), Theme.Display(15f), Theme.Ink, m(new Rectangle(pad + Theme.S(56), Theme.S(18), W - pad * 2 - Theme.S(56), Theme.S(28))), Align.Start);
            Theme.Text(g, L.T(cues != null ? "tts_caps_sub" : "tts_sub"), Theme.F(9f), Theme.InkSoft, m(new Rectangle(pad + Theme.S(56), Theme.S(46), W - pad * 2 - Theme.S(56), Theme.S(20))), Align.Start);
            Theme.Text(g, L.T("tts_text").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, m(textLbl), Align.Start);
            Theme.Text(g, L.T("tts_voice").ToUpper(CultureInfo.CurrentCulture), Theme.F(7.75f, FontStyle.Bold), Theme.InkFaint, m(voiceLbl), Align.Start);
            Theme.Text(g, L.T("tts_speed"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, m(speedLbl), Align.Start);
            string st = status;
            Color sc = statusColor;
            if (st == null)
            {
                long need = Tts.Needed(Picked);
                st = need > 0 ? L.T("tts_once", (need + (1 << 20) - 1) >> 20) : L.T(cues != null ? "tts_caps_ready" : "tts_ready");
                sc = Theme.InkSoft;
            }
            Rectangle nr = m(statusR);
            Theme.FillRound(g, nr, Theme.S(9), Theme.CardInner);
            Theme.Text(g, st, Theme.F(8.5f), sc, m(new Rectangle(statusR.X + Theme.S(12), statusR.Y, statusR.Width - Theme.S(24), statusR.Height)), Align.Start, true, true);
        }
    }
}
