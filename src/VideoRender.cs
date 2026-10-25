using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace FileConverter
{
    /// How a video project is written out (export, or the quick preview the editor plays).
    public class RenderOptions
    {
        public int W, H;
        public double Fps = 30;
        public int Crf = 20;
        public bool Gpu;
        public bool Preview;
        /// "mp4", "gif" or "mp3" (just the sound).
        public string Format = "mp4";
    }

    /// Turns a video project into one ffmpeg run: every track, clip, keyframe, transition, text and caption.
    public static class VideoRender
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        static string N(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "0" : v.ToString("0.####", CI); }
        static string Q(string s) { return Tools.Quote(s); }
        static string FilterPath(string p) { return p.Replace("\\", "/").Replace(":", "\\:").Replace("'", ""); }

        public static string Atempo(double s)
        {
            List<string> parts = new List<string>();
            while (s > 2.0001) { parts.Add("atempo=2"); s /= 2; }
            while (s < 0.4999) { parts.Add("atempo=0.5"); s /= 0.5; }
            if (Math.Abs(s - 1) > 0.0001) parts.Add("atempo=" + N(s));
            return parts.Count == 0 ? "" : string.Join(",", parts.ToArray()) + ",";
        }

        /// Light & color (and a look) as ffmpeg filters, matching what the editor shows.
        public static string AdjFilter(Adjust a, int shortSide)
        {
            if (a == null || a.None) return "";
            StringBuilder f = new StringBuilder();
            float[][] m = a.Matrix();
            bool ident = true;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) if (Math.Abs(m[i][j] - (i == j ? 1 : 0)) > 0.0005f) ident = false;
            if (!ident)
                f.AppendFormat(CI, "colorchannelmixer=rr={0}:rg={1}:rb={2}:gr={3}:gg={4}:gb={5}:br={6}:bg={7}:bb={8},",
                    N(m[0][0]), N(m[1][0]), N(m[2][0]), N(m[0][1]), N(m[1][1]), N(m[2][1]), N(m[0][2]), N(m[1][2]), N(m[2][2]));
            if (Math.Abs(m[4][0]) > 0.001f || Math.Abs(m[4][1]) > 0.001f || Math.Abs(m[4][2]) > 0.001f)
                f.AppendFormat("lutrgb=r='clip(val+{0},0,255)':g='clip(val+{1},0,255)':b='clip(val+{2},0,255)',", N(m[4][0] * 255), N(m[4][1] * 255), N(m[4][2] * 255));
            if (a.Blur > 0) f.Append("gblur=sigma=" + N(Math.Max(0.5, a.Blur / 100f * 0.025f * shortSide)) + ",");
            if (a.Sharpen > 0) f.Append("unsharp=5:5:" + N(a.Sharpen / 100f * 1.1f) + ",");
            return f.ToString();
        }

        /// A property over time as an ffmpeg expression, eased between keyframes. local: in the clip's own time
        /// (filters applied before it's moved into place), else in the output's time; tv: the filter's name for time.
        public static string Expr(VClip c, string prop, float plain, Func<float, double> map, bool local = false, string tv = "t")
        {
            List<VKey> k;
            if (!c.Keys.TryGetValue(prop, out k) || k.Count == 0) return N(map(plain));
            if (k.Count == 1) return N(map(k[0].V));
            double base0 = local ? 0 : c.At;
            // nested ifs from the end: before the first key, its value; after the last, the last value
            string e = N(map(k[k.Count - 1].V));
            for (int i = k.Count - 1; i >= 1; i--)
            {
                double t0 = base0 + k[i - 1].T, t1 = base0 + k[i].T;
                double v0 = map(k[i - 1].V), v1 = map(k[i].V);
                string f = "clip((" + tv + "-" + N(t0) + ")/" + N(Math.Max(0.001, t1 - t0)) + ",0,1)";
                string lerp = N(v0) + "+(" + N(v1 - v0) + ")*(" + f + ")*(" + f + ")*(3-2*(" + f + "))";
                e = "if(lt(" + tv + "," + N(t1) + ")," + lerp + "," + e + ")";
            }
            return "if(lt(" + tv + "," + N(base0 + k[0].T) + ")," + N(map(k[0].V)) + "," + e + ")";
        }

        /// 0 → 1 as a clip comes in (its "in" animation); 1 → 0 as it goes.
        static string InCurve(VClip c, bool local) { string f = "clip((t-" + N(local ? 0 : c.At) + ")/" + N(Math.Max(0.05, c.AnimInDur)) + ",0,1)"; return "(1-(1-" + f + ")*(1-" + f + "))"; }
        static string OutCurve(VClip c, bool local) { string f = "clip((" + N(local ? c.Length : c.End) + "-t)/" + N(Math.Max(0.05, c.AnimOutDur)) + ",0,1)"; return "(1-(1-" + f + ")*(1-" + f + "))"; }

        // ------------------------------------------------------------------ text and captions as pictures / subtitles
        /// The text as a picture (just its size, see-through around it), at a scale of the project's size.
        public static Bitmap TextPng(TextLayer src, string text, float scale, out PointF center)
        {
            TextLayer t = src.CloneStyle();
            if (text != null) t.Text = text;
            t.Pos = PointF.Empty;
            t.Opacity = 1;
            t.Touch();
            RectangleF b = t.Bounds;
            int pad = (int)Math.Ceiling(Math.Max(8, t.SizePx * 0.25f) * scale);
            int w = Math.Max(2, (int)Math.Ceiling(b.Width * scale) + pad * 2), h = Math.Max(2, (int)Math.Ceiling(b.Height * scale) + pad * 2);
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            float ox = pad - b.X * scale, oy = pad - b.Y * scale;
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                t.DrawScreen(g, ox, oy, scale);
            }
            // where (0,0) of the text — its middle — is in the picture
            center = new PointF(ox, oy);
            return bmp;
        }

        static string AssColor(Color c, int alpha = 0) { return "&H" + alpha.ToString("X2") + c.B.ToString("X2") + c.G.ToString("X2") + c.R.ToString("X2"); }
        static string AssTime(double t) { TimeSpan s = TimeSpan.FromSeconds(Math.Max(0, t)); return string.Format("{0}:{1:00}:{2:00}.{3:00}", (int)s.TotalHours, s.Minutes, s.Seconds, s.Milliseconds / 10); }
        static string AssText(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("{", "(").Replace("}", ")").Replace("\r\n", "\\N").Replace("\n", "\\N"); }

        /// Captions in their style, as an .ass subtitles file the size of the picture.
        public static string Ass(VClip cap, int W, int H)
        {
            string font = Composer.CapFontName(cap);
            int size = (int)Math.Round(Math.Min(W, H) * 0.062 * cap.CapSize * (cap.CapStyle == "bold" ? 1.25 : 1));
            int outline = cap.CapStyle == "box" ? 0 : Math.Max(2, size / (cap.CapStyle == "bold" ? 7 : 12));
            int border = cap.CapStyle == "box" ? 3 : 1;
            string back = cap.CapStyle == "box" ? AssColor(Color.Black, 0x40) : AssColor(Color.Black, 0x80);
            bool bold = cap.CapStyle == "bold" || cap.CapStyle == "highlight";
            StringBuilder b = new StringBuilder();
            b.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: ").Append(W).Append("\nPlayResY: ").Append(H).Append("\nWrapStyle: 0\nScaledBorderAndShadow: yes\n\n");
            b.Append("[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
            // Encoding -1: libass works out the direction from the words, so Arabic reads right to left
            // (with 1 it laid the words out left to right as soon as a word was highlighted)
            b.AppendFormat("Style: Cap,{0},{1},{2},{3},{4},{5},{6},0,0,0,100,100,0,0,{7},{8},{9},5,{10},{10},0,-1\n\n",
                font.Replace(",", " "), size, AssColor(cap.CapColor), AssColor(cap.CapHigh), AssColor(cap.CapStyle == "box" ? Color.Black : Color.Black), back, bold ? -1 : 0, border, outline, cap.CapStyle == "box" ? 0 : 1, (int)(W * 0.06));
            b.Append("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
            string pos = "{\\an5\\pos(" + (W / 2) + "," + (int)(cap.CapY * H) + ")}";
            foreach (Cue q in cap.Cues)
            {
                string text = cap.CapStyle == "bold" ? q.Text.ToUpper(CultureInfo.CurrentCulture) : q.Text;
                if (cap.CapStyle == "highlight" || cap.CapStyle == "bold")
                {
                    // the word being said in the highlight color (each word gets its own moment)
                    Cue up = q.Copy(); up.Text = text;   // keeps when each word is said
                    List<KeyValuePair<string, double>> words = up.Words();
                    for (int i = 0; i < words.Count; i++)
                    {
                        double s = words[i].Value, e = i + 1 < words.Count ? words[i + 1].Value : q.End;
                        StringBuilder line = new StringBuilder(pos);
                        for (int j = 0; j < words.Count; j++)
                        {
                            if (j > 0) line.Append(' ');
                            if (j == i) line.Append("{\\c" + AssColor(cap.CapHigh) + (cap.CapStyle == "bold" ? "\\fscx112\\fscy112" : "") + "}").Append(AssText(words[j].Key)).Append("{\\r}");
                            else line.Append(AssText(words[j].Key));
                        }
                        b.Append("Dialogue: 0,").Append(AssTime(s)).Append(',').Append(AssTime(e)).Append(",Cap,,0,0,0,,").Append(line).Append('\n');
                    }
                }
                else b.Append("Dialogue: 0,").Append(AssTime(q.Start)).Append(',').Append(AssTime(q.End)).Append(",Cap,,0,0,0,,").Append(pos).Append(AssText(text)).Append('\n');
            }
            return b.ToString();
        }

        // ------------------------------------------------------------------ the whole project
        class Ctx
        {
            public StringBuilder Args = new StringBuilder(), Fc = new StringBuilder();
            public int Inputs;
            public Job Job;
            public string Tmp;
            public int Label;
            public string L(string p) { return p + (Label++); }
        }

        public static Job Build(VProject p, string output, RenderOptions o)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            p.Layout();
            double D = p.Duration;
            if (D <= 0.05) throw new UserError(L.T("vs_empty"));
            bool visual = false;
            foreach (VTrack t in p.Tracks) if (t.Visual && !t.Hidden) foreach (VClip c in t.Clips) if (c.Visual) visual = true;
            bool audioOnly = !visual || o.Format == "mp3";
            bool gif = o.Format == "gif" && visual;
            int W = Math.Max(16, o.W - o.W % 2), H = Math.Max(16, o.H - o.H % 2);
            float k = W / (float)p.W;   // project pixels → output pixels
            double fps = Math.Max(5, Math.Min(120, o.Fps));
            Ctx x = new Ctx { Job = new Job { Output = output } };
            x.Tmp = Path.Combine(Path.GetTempPath(), "fadi_render_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(x.Tmp);
            x.Job.Temps.Add(x.Tmp);
            x.Args.Append("-hide_banner -y");
            List<string> audios = new List<string>();
            string video = null;
            if (!audioOnly)
            {
                // the background, the full length of the project
                Color bg = p.Fill == "color" ? p.FillColor : Color.Black;
                x.Fc.AppendFormat("color=c=0x{0:X2}{1:X2}{2:X2}:s={3}x{4}:r={5}:d={6},format=yuv420p[base];", bg.R, bg.G, bg.B, W, H, N(fps), N(D));
                video = "base";
            }
            // ---- the main track
            VTrack main = p.Main;
            if (main.Clips.Count > 0)
            {
                string mv, ma;
                MainChain(p, main, x, W, H, fps, audioOnly || main.Hidden, main.Muted, out mv, out ma);
                if (mv != null && video != null) { string l = x.L("vm"); x.Fc.AppendFormat("[{0}][{1}]overlay=0:0:eof_action=pass[{2}];", video, mv, l); video = l; }
                if (ma != null) audios.Add(ma);
            }
            // ---- overlays and texts, from the one just above the main track up to the top
            int mi = p.MainIndex;
            for (int ti = mi - 1; ti >= 0; ti--)
            {
                VTrack t = p.Tracks[ti];
                foreach (VClip c in t.Clips)
                {
                    if (c.Kind == VKind.Captions) continue;
                    if (c.Kind == VKind.Text) { if (!audioOnly && !t.Hidden) video = TextOver(p, c, x, video, k, fps); continue; }
                    string a;
                    video = Overlay(p, c, x, video, W, H, k, fps, audioOnly || t.Hidden, t.Muted, out a);
                    if (a != null) audios.Add(a);
                }
            }
            // ---- captions on top of everything
            if (!audioOnly)
                foreach (VTrack t in p.Tracks)
                {
                    if (t.Hidden) continue;
                    foreach (VClip c in t.Clips)
                    {
                        if (c.Kind != VKind.Captions || c.Cues == null || c.Cues.Count == 0) continue;
                        string ass = Path.Combine(x.Tmp, "cap" + (x.Label++) + ".ass");
                        File.WriteAllText(ass, Ass(c, W, H), new UTF8Encoding(false));
                        string fdir = Directory.Exists(FontPack.Dir) ? ":fontsdir='" + FilterPath(FontPack.Dir) + "'" : "";
                        string l = x.L("vc");
                        x.Fc.AppendFormat("[{0}]subtitles=filename='{1}'{2}[{3}];", video, FilterPath(ass), fdir, l);
                        video = l;
                    }
                }
            // ---- sound: every audio track
            foreach (VTrack t in p.Tracks)
            {
                if (t.Kind != VTrackKind.Audio || t.Muted) continue;
                foreach (VClip c in t.Clips) { string a = AudioClip(c, x, true); if (a != null) audios.Add(a); }
            }
            x.Fc.AppendFormat("anullsrc=r=48000:cl=stereo,atrim=0:{0},aformat=sample_fmts=fltp:channel_layouts=stereo[abase];", N(D));
            string aout = "abase";
            if (audios.Count > 0)
            {
                StringBuilder ins = new StringBuilder("[abase]");
                foreach (string a in audios) ins.Append('[').Append(a).Append(']');
                x.Fc.AppendFormat("{0}amix=inputs={1}:duration=first:dropout_transition=0:normalize=0[amix];", ins, audios.Count + 1);
                aout = "amix";
            }
            x.Fc.AppendFormat("[{0}]alimiter=limit=0.97:level=false[aout]", aout);
            string maps = " -map \"[aout]\"";
            string codecs;
            if (audioOnly)
            {
                codecs = o.Preview ? "-c:a pcm_s16le" : "-c:a libmp3lame -q:a 2";
            }
            else
            {
                if (gif) x.Fc.AppendFormat(";[{0}]fps={1},split[g1][g2];[g1]palettegen=stats_mode=diff[gp];[g2][gp]paletteuse=dither=sierra2_4a[vout]", video, N(Math.Min(15, fps)));
                else x.Fc.AppendFormat(";[{0}]format=yuv420p[vout]", video);
                maps = " -map \"[vout]\"" + (gif ? "" : maps);
                if (gif) codecs = "-loop 0";
                else if (o.Preview) codecs = "-c:v libx264 -preset ultrafast -crf 30 -pix_fmt yuv420p -g 15 -c:a aac -b:a 128k";
                else
                {
                    string enc = o.Gpu ? HwEnc.Detect() : "";
                    int q = o.Crf;
                    string v;
                    if (enc == "h264_nvenc") v = "-c:v h264_nvenc -preset p5 -rc vbr -cq " + (q + 3) + " -b:v 0 -spatial-aq 1 -pix_fmt yuv420p";
                    else if (enc == "h264_qsv") v = "-c:v h264_qsv -preset medium -global_quality " + (q + 3) + " -pix_fmt nv12";
                    else if (enc == "h264_amf") v = "-c:v h264_amf -quality balanced -rc cqp -qp_i " + (q + 1) + " -qp_p " + (q + 3) + " -qp_b " + (q + 5) + " -pix_fmt yuv420p";
                    else v = "-c:v libx264 -preset medium -crf " + q + " -pix_fmt yuv420p";
                    codecs = v + " -c:a aac -b:a 192k -movflags +faststart";
                    if (enc != "") x.Job.Encoder = HwEnc.Friendly(enc);
                }
            }
            string script = Path.Combine(x.Tmp, "graph.txt");
            File.WriteAllText(script, x.Fc.ToString(), new UTF8Encoding(false));
            x.Args.Append(" " + ScriptOption(ff) + " " + Q(script) + maps + " -t " + N(D) + " " + codecs + " " + Q(output));
            x.Job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = x.Args.ToString(), Duration = D });
            return x.Job;
        }

        static string scriptOpt, scriptOptFor;

        /// How this FFmpeg reads the filter graph from a file. FFmpeg 8 removed "-filter_complex_script" in favour of
        /// "-/filter_complex file" (which FFmpeg 7 added); older builds only know the first, so ask the build itself.
        static string ScriptOption(string ff)
        {
            lock (typeof(VideoRender))
            {
                if (scriptOpt != null && scriptOptFor == ff) return scriptOpt;
                string help = "";
                try { byte[] b = Media.Capture(ff, "-hide_banner -h full", 20000); if (b != null) help = Encoding.UTF8.GetString(b); } catch { }
                // when the help couldn't be read, keep the long-standing option
                scriptOpt = help.Length > 0 && help.IndexOf("filter_complex_script", StringComparison.Ordinal) < 0 ? "-/filter_complex" : "-filter_complex_script";
                scriptOptFor = ff;
                return scriptOpt;
            }
        }

        static int Input(Ctx x, string pre, string file)
        {
            x.Args.Append(' ').Append(pre).Append(pre.Length > 0 ? " " : "").Append("-i ").Append(Q(file));
            return x.Inputs++;
        }

        /// A clip's own picture: its part of the file, at its speed, with its colors (and see-through kept for overlays).
        static string Picture(VClip c, Ctx x, double fps, int shortSide, bool alpha)
        {
            int i;
            if (c.Kind == VKind.Image) i = Input(x, "-loop 1 -framerate " + N(fps) + " -t " + N(c.Length + 0.5), c.File);
            else i = Input(x, (c.Alpha ? "-c:v libvpx-vp9 " : "") + "-ss " + N(c.In) + " -t " + N(c.Out - c.In + 0.05), c.File);
            string l = x.L("p");
            string speed = c.Kind == VKind.Video && Math.Abs(c.Speed - 1) > 0.001 ? "setpts=(PTS-STARTPTS)/" + N(c.Speed) + "," : "setpts=PTS-STARTPTS,";
            x.Fc.AppendFormat("[{0}:v:0]{1}fps={2},trim=duration={3},setpts=PTS-STARTPTS,{4}format={5}[{6}];", i, speed, N(fps), N(c.Length), AdjFilter(c.Adj, shortSide), alpha ? "rgba" : "yuv420p", l);
            return l;
        }

        /// The main track: clips one after another (fitted to the picture, gaps filled), joined with their transitions.
        static void MainChain(VProject p, VTrack main, Ctx x, int W, int H, double fps, bool noVideo, bool noSound, out string vOut, out string aOut)
        {
            vOut = null; aOut = null;
            List<VClip> m = main.Clips;
            List<string> vs = new List<string>(), az = new List<string>();
            foreach (VClip c in m)
            {
                if (!noVideo && c.Kind != VKind.Audio)
                {
                    string pic = Picture(c, x, fps, Math.Min(W, H), false);
                    string l = x.L("mv");
                    string fit = "scale=" + W + ":" + H + ":force_original_aspect_ratio=decrease,setsar=1";
                    string fades = (c.FadeIn > 0.01 ? ",fade=t=in:st=0:d=" + N(c.FadeIn) : "") + (c.FadeOut > 0.01 ? ",fade=t=out:st=" + N(Math.Max(0, c.Length - c.FadeOut)) + ":d=" + N(c.FadeOut) : "");
                    if (p.Fill == "blur")
                    {
                        string a = x.L("ba"), b = x.L("bb"), bg = x.L("bg"), fg = x.L("fg");
                        x.Fc.AppendFormat("[{0}]split[{1}][{2}];[{1}]scale={3}:{4}:force_original_aspect_ratio=increase,crop={3}:{4},boxblur=luma_radius=min(h\\,w)/18:luma_power=2,eq=brightness=-0.04[{5}];[{2}]{6}[{7}];[{5}][{7}]overlay=(W-w)/2:(H-h)/2,setsar=1{8},format=yuv420p[{9}];",
                            pic, a, b, W, H, bg, fit, fg, fades, l);
                    }
                    else
                    {
                        Color f = p.Fill == "color" ? p.FillColor : Color.Black;
                        x.Fc.AppendFormat("[{0}]{1},pad={2}:{3}:(ow-iw)/2:(oh-ih)/2:color=0x{4:X2}{5:X2}{6:X2}{7},format=yuv420p[{8}];", pic, fit, W, H, f.R, f.G, f.B, fades, l);
                    }
                    vs.Add(l);
                }
                else if (!noVideo)
                {
                    // a song on the main track: a still frame of the background
                    string l = x.L("mv");
                    x.Fc.AppendFormat("color=c=black:s={0}x{1}:r={2}:d={3},format=yuv420p[{4}];", W, H, N(fps), N(c.Length), l);
                    vs.Add(l);
                }
                string snd = noSound ? null : AudioClip(c, x, false);
                if (snd == null)
                {
                    snd = x.L("ms");
                    x.Fc.AppendFormat("anullsrc=r=48000:cl=stereo,atrim=0:{0},aformat=sample_fmts=fltp:channel_layouts=stereo[{1}];", N(c.Length), snd);
                }
                az.Add(snd);
            }
            // joined: a transition where there is one, a plain cut elsewhere
            string v = vs.Count > 0 ? vs[0] : null, a0 = az[0];
            for (int i = 1; i < m.Count; i++)
            {
                double d = p.TransLen(i - 1);
                if (v != null)
                {
                    string l = x.L("vj");
                    if (d > 0) x.Fc.AppendFormat("[{0}][{1}]xfade=transition={2}:duration={3}:offset={4}[{5}];", v, vs[i], XfadeName(m[i - 1].Trans), N(d), N(m[i].At), l);
                    else x.Fc.AppendFormat("[{0}][{1}]concat=n=2:v=1:a=0[{2}];", v, vs[i], l);
                    v = l;
                }
                string al = x.L("aj");
                if (d > 0) x.Fc.AppendFormat("[{0}][{1}]acrossfade=d={2}[{3}];", a0, az[i], N(d), al);
                else x.Fc.AppendFormat("[{0}][{1}]concat=n=2:v=0:a=1[{2}];", a0, az[i], al);
                a0 = al;
            }
            vOut = v; aOut = a0;
        }

        static string XfadeName(string t)
        {
            switch (t)
            {
                case "fade": case "fadeblack": case "fadewhite": case "slideleft": case "slideright": case "slideup": case "wipeleft": case "circleopen":
                case "dissolve": case "zoomin": case "smoothleft": case "pixelize": case "radial": case "hblur": return t;
            }
            return "fade";
        }

        /// A picture (or video) laid over the picture: where it is, its size, turn, see-through, animations, timing.
        static string Overlay(VProject p, VClip c, Ctx x, string video, int W, int H, float k, double fps, bool noVideo, bool noSound, out string audio)
        {
            audio = noSound ? null : AudioClip(c, x, true);
            if (noVideo || video == null || c.Kind == VKind.Audio || c.Src == null) return video;
            string pic = Picture(c, x, fps, Math.Min(W, H), true);
            // its width on the picture: Scale × the picture's width (the photo / video keeps its shape)
            bool animScale = c.HasKeys("scale") || c.AnimIn == "pop" || c.AnimOut == "pop";
            string chain;
            if (!animScale) chain = "scale=" + Even(c.Scale * W) + ":-2," + Turn(c) + Fades(c) + Opacity(c);
            else
            {
                // a size that changes every frame has to be the chain's last step (FFmpeg 8 stops any filter that
                // gets a frame of another size), so: the largest size first, then turn, fades and see-through, then shrink
                float maxScale = MaxScale(c);
                chain = "scale=" + Even(maxScale * W) + ":-2," + Turn(c) + Fades(c) + Opacity(c) +
                        "scale=w='max(2,trunc(iw*(" + Expr(c, "scale", c.Scale, v => v, true) + ")/" + N(maxScale) + PopFactor(c) + "/2)*2)':h=-2:eval=frame,";
            }
            string l = x.L("ov");
            x.Fc.AppendFormat("[{0}]{1}setpts=PTS+{2}/TB[{3}];", pic, chain, N(c.At), l);
            return Place(c, x, video, l, W, H);
        }

        static float MaxScale(VClip c)
        {
            float m = c.Scale;
            List<VKey> sk; if (c.Keys.TryGetValue("scale", out sk)) foreach (VKey q in sk) m = Math.Max(m, q.V);
            return Math.Max(0.05f, m);
        }

        static string Turn(VClip c)
        {
            return c.HasKeys("rot") || Math.Abs(c.Rot) > 0.01f ? "rotate=a='(" + Expr(c, "rot", c.Rot, v => v, true) + ")*PI/180':c=none:ow='hypot(iw,ih)':oh=ow," : "";
        }

        static string Opacity(VClip c)
        {
            if (!c.HasKeys("opacity") && c.Opacity >= 0.999f) return "";
            return c.HasKeys("opacity") ? "geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*(" + Expr(c, "opacity", c.Opacity, v => v, true, "T") + ")'," : "colorchannelmixer=aa=" + N(c.Opacity) + ",";
        }

        static int Even(double v) { int i = (int)Math.Round(v); return Math.Max(2, i - i % 2); }

        static string PopFactor(VClip c)
        {
            string f = "";
            if (c.AnimIn == "pop") f += "*(0.6+0.4*" + InCurve(c, true) + ")";
            if (c.AnimOut == "pop") f += "*(0.6+0.4*" + OutCurve(c, true) + ")";
            return f;
        }

        /// Fading in and out (the clip's own fades and its fade / slide / pop animations), on see-through.
        static string Fades(VClip c)
        {
            string s = "";
            double din = c.AnimIn != "none" ? c.AnimInDur : c.FadeIn, dout = c.AnimOut != "none" ? c.AnimOutDur : c.FadeOut;
            if (din > 0.01) s += "fade=t=in:st=0:d=" + N(din) + ":alpha=1,";
            if (dout > 0.01) s += "fade=t=out:st=" + N(Math.Max(0, c.Length - dout)) + ":d=" + N(dout) + ":alpha=1,";
            return s;
        }

        /// Lays a prepared picture onto the video at the clip's place (its middle at X, Y), between its start and end.
        static string Place(VClip c, Ctx x, string video, string over, int W, int H)
        {
            string xs = "(" + Expr(c, "x", c.X, v => v * W) + ")-w/2", ys = "(" + Expr(c, "y", c.Y, v => v * H) + ")-h/2";
            if (c.AnimIn == "slideup") ys += "+" + N(H * 0.08) + "*(1-" + InCurve(c, false) + ")";
            if (c.AnimOut == "slideup") ys += "-" + N(H * 0.08) + "*(1-" + OutCurve(c, false) + ")";
            if (c.AnimIn == "slideleft") xs += "+" + N(W * 0.1) + "*(1-" + InCurve(c, false) + ")";
            if (c.AnimOut == "slideleft") xs += "-" + N(W * 0.1) + "*(1-" + OutCurve(c, false) + ")";
            string l = x.L("vo");
            x.Fc.AppendFormat("[{0}][{1}]overlay=x='{2}':y='{3}':eof_action=pass:enable='between(t,{4},{5})'[{6}];", video, over, xs, ys, N(c.At), N(c.End), l);
            return l;
        }

        /// A text clip: drawn as a picture (or, typing in, a few pictures) and laid over the video.
        static string TextOver(VProject p, VClip c, Ctx x, string video, float k, double fps)
        {
            if (video == null || c.Text == null || string.IsNullOrEmpty(c.Text.Text)) return video;
            int W = (int)Math.Round(p.W * k), H = (int)Math.Round(p.H * k);
            float maxScale = c.Scale;
            List<VKey> sk; if (c.Keys.TryGetValue("scale", out sk)) foreach (VKey q in sk) maxScale = Math.Max(maxScale, q.V);
            float rs = k * Math.Max(0.05f, maxScale);
            string l0 = x.L("tx");
            if (c.AnimIn == "type")
            {
                // typing: the text grows letter by letter (lined up on the left), then stays
                TextLayer tl = c.Text.CloneStyle(); tl.Align = StringAlignment.Near;
                string txt = tl.Text;
                int steps = Math.Max(1, Math.Min(30, txt.Length));
                string dir = Path.Combine(x.Tmp, "type" + x.Label++);
                Directory.CreateDirectory(dir);
                Size big;
                PointF full;
                using (Bitmap fb = TextPng(tl, txt, rs, out full)) big = fb.Size;
                for (int s = 1; s <= steps; s++)
                {
                    int n = (int)Math.Round(txt.Length * s / (double)steps);
                    PointF cc;
                    using (Bitmap part = TextPng(tl, txt.Substring(0, n), rs, out cc))
                    using (Bitmap canvas = new Bitmap(big.Width, big.Height, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(canvas)) { g.Clear(Color.Transparent); g.DrawImage(part, 0, 0, part.Width, part.Height); }
                        canvas.Save(Path.Combine(dir, "t" + s.ToString("D3") + ".png"), ImageFormat.Png);
                    }
                }
                double rate = steps / Math.Max(0.1, c.AnimInDur);
                int i = Input(x, "-framerate " + N(rate) + " -start_number 1", Path.Combine(dir, "t%03d.png"));
                x.Fc.AppendFormat("[{0}:v]fps={1},tpad=stop_mode=clone:stop_duration={2},trim=duration={3},setpts=PTS-STARTPTS,format=rgba[{4}];", i, N(fps), N(c.Length + 1), N(c.Length), l0);
            }
            else
            {
                PointF cc;
                string png = Path.Combine(x.Tmp, "text" + x.Label++ + ".png");
                using (Bitmap b = TextPng(c.Text, null, rs, out cc)) b.Save(png, ImageFormat.Png);
                int i = Input(x, "-loop 1 -framerate " + N(fps) + " -t " + N(c.Length + 0.5), png);
                x.Fc.AppendFormat("[{0}:v]trim=duration={1},setpts=PTS-STARTPTS,format=rgba[{2}];", i, N(c.Length), l0);
            }
            bool animScale = c.HasKeys("scale") || c.AnimIn == "pop" || c.AnimOut == "pop";
            // the text is drawn at its largest size; a size that changes every frame comes last (see Overlay)
            string chain = Turn(c) + Fades(c) + Opacity(c);
            if (animScale) chain += "scale=w='max(2,trunc(iw*(" + Expr(c, "scale", c.Scale, v => v, true) + ")/" + N(maxScale) + PopFactor(c) + "/2)*2)':h=-2:eval=frame,";
            string l = x.L("tv");
            x.Fc.AppendFormat("[{0}]{1}setpts=PTS+{2}/TB[{3}];", l0, chain, N(c.At), l);
            return Place(c, x, video, l, W, H);
        }

        /// A clip's sound: its part, at its speed and volume (with keyframes), cleaned up, faded, at its time.
        static string AudioClip(VClip c, Ctx x, bool delay)
        {
            if (!c.HasSound || c.Muted || c.Src == null) return null;
            int i = Input(x, "-ss " + N(c.In) + " -t " + N(c.Out - c.In + 0.05), c.File);
            string l = x.L("a");
            StringBuilder f = new StringBuilder();
            f.AppendFormat("[{0}:a:0]asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,", i);
            if (Math.Abs(c.Speed - 1) > 0.001) f.Append(Atempo(c.Speed));
            f.Append("atrim=duration=" + N(c.Length) + ",asetpts=PTS-STARTPTS,");
            if (c.Denoise) f.Append("afftdn=nr=18:nf=-30,highpass=f=70,");
            if (c.HasKeys("vol"))
            {
                f.Append("volume='" + Expr(c, "vol", c.Volume, v => v, true) + "':eval=frame,");
            }
            else if (Math.Abs(c.Volume - 1) > 0.005f) f.Append("volume=" + N(c.Volume) + ",");
            if (c.FadeIn > 0.01) f.Append("afade=t=in:st=0:d=" + N(c.FadeIn) + ",");
            if (c.FadeOut > 0.01) f.Append("afade=t=out:st=" + N(Math.Max(0, c.Length - c.FadeOut)) + ":d=" + N(c.FadeOut) + ",");
            if (delay && c.At > 0.001) { int ms = (int)Math.Round(c.At * 1000); f.Append("adelay=" + ms + "|" + ms + ","); }
            f.Append("anull[" + l + "];");
            x.Fc.Append(f);
            return l;
        }
    }
}
