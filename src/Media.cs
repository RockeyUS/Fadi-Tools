using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    public class MediaInfo
    {
        public double Duration;
        public bool HasVideo, HasAudio;
        public int Width, Height;
        public string VCodec = "", ACodec = "", Container = "";
        public double Fps;
        public long Bitrate;
        public int SampleRate, Channels;
    }

    public enum TrimMode { Keep, Remove }

    /// FFmpeg helpers for the Trim page and the Inspector.
    public static class Media
    {
        static string Q(string s) { return Tools.Quote(s); }

        public static MediaInfo Probe(string file)
        {
            MediaInfo mi = new MediaInfo();
            string probe = Tools.FFprobe;
            if (probe != null)
            {
                string output;
                int code = Tools.RunHidden(probe, "-v error -show_entries format=duration,bit_rate,format_name:stream=codec_type,codec_name,width,height,avg_frame_rate,sample_rate,channels:stream_disposition=attached_pic -of json " + Q(file), 60000, out output);
                if (code == 0)
                {
                    try
                    {
                        int start = output.IndexOf('{');
                        object root = new JsonParser(output.Substring(start)).Parse();
                        JsonObject o = root as JsonObject;
                        foreach (KeyValuePair<string, object> kv in o)
                        {
                            if (kv.Key == "format" && kv.Value is JsonObject)
                                foreach (KeyValuePair<string, object> f in (JsonObject)kv.Value)
                                {
                                    if (f.Key == "duration") mi.Duration = Num(f.Value);
                                    if (f.Key == "bit_rate") mi.Bitrate = (long)Num(f.Value);
                                    if (f.Key == "format_name") mi.Container = Convert.ToString(f.Value);
                                }
                            if (kv.Key == "streams" && kv.Value is List<object>)
                                foreach (object so in (List<object>)kv.Value)
                                {
                                    JsonObject s = so as JsonObject; if (s == null) continue;
                                    string type = Get(s, "codec_type");
                                    bool cover = false;
                                    foreach (KeyValuePair<string, object> sk in s)
                                        if (sk.Key == "disposition" && sk.Value is JsonObject)
                                            foreach (KeyValuePair<string, object> dk in (JsonObject)sk.Value)
                                                if (dk.Key == "attached_pic" && Num(dk.Value) == 1) cover = true;
                                    if (type == "video" && !cover && !mi.HasVideo)
                                    {
                                        mi.HasVideo = true;
                                        mi.VCodec = Get(s, "codec_name");
                                        mi.Width = (int)Num(GetObj(s, "width")); mi.Height = (int)Num(GetObj(s, "height"));
                                        string fr = Get(s, "avg_frame_rate");
                                        string[] p = fr.Split('/');
                                        double a, b;
                                        if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a) && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b) && b > 0) mi.Fps = a / b;
                                    }
                                    if (type == "audio" && !mi.HasAudio)
                                    {
                                        mi.HasAudio = true;
                                        mi.ACodec = Get(s, "codec_name");
                                        mi.SampleRate = (int)Num(GetObj(s, "sample_rate"));
                                        mi.Channels = (int)Num(GetObj(s, "channels"));
                                    }
                                }
                        }
                        if (mi.Duration > 0 || mi.HasVideo || mi.HasAudio) return mi;
                    }
                    catch { }
                }
            }
            // fallback: parse ffmpeg -i
            string ff = Tools.FFmpeg;
            if (ff == null) return mi;
            string text;
            Tools.RunHidden(ff, "-hide_banner -i " + Q(file), 60000, out text);
            Match d = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (d.Success) mi.Duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
            Match v = Regex.Match(text, @"Stream #.*Video: (\w+).*?, (\d{2,5})x(\d{2,5})");
            if (v.Success && !text.Contains("attached pic")) { mi.HasVideo = true; mi.VCodec = v.Groups[1].Value; mi.Width = int.Parse(v.Groups[2].Value); mi.Height = int.Parse(v.Groups[3].Value); }
            Match a2 = Regex.Match(text, @"Stream #.*Audio: (\w+)");
            if (a2.Success) { mi.HasAudio = true; mi.ACodec = a2.Groups[1].Value; }
            return mi;
        }

        static object GetObj(JsonObject o, string k) { foreach (KeyValuePair<string, object> kv in o) if (kv.Key == k) return kv.Value; return null; }
        static string Get(JsonObject o, string k) { object v = GetObj(o, k); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        static double Num(object v)
        {
            if (v == null) return 0;
            if (v is double) return (double)v;
            if (v is long) return (long)v;
            double d;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
        }

        /// Runs a tool and captures its binary stdout (e.g. a PNG written to pipe).
        public static byte[] Capture(string exe, string args, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                MemoryStream ms = new MemoryStream();
                System.Threading.Tasks.Task copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
                copy.Wait(5000);
                return ms.Length > 0 ? ms.ToArray() : null;
            }
        }

        public static Image Frame(string file, double t, int height)
        {
            string ff = Tools.FFmpeg; if (ff == null) return null;
            string ts = Math.Max(0, t).ToString("0.###", CultureInfo.InvariantCulture);
            byte[] png = Capture(ff, "-hide_banner -loglevel error -ss " + ts + " -i " + Q(file) + " -frames:v 1 -vf \"scale=-2:" + height + "\" -f image2pipe -vcodec png -", 30000);
            if (png == null && t > 0.5) png = Capture(ff, "-hide_banner -loglevel error -i " + Q(file) + " -frames:v 1 -vf \"scale=-2:" + height + "\" -f image2pipe -vcodec png -", 30000);
            if (png == null) return null;
            try { return Image.FromStream(new MemoryStream(png)); } catch { return null; }
        }

        public static Image Waveform(string file, int w, int h, Color c)
        {
            string ff = Tools.FFmpeg; if (ff == null) return null;
            string col = string.Format("0x{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
            byte[] png = Capture(ff, "-hide_banner -loglevel error -i " + Q(file) + " -filter_complex \"aformat=channel_layouts=mono,showwavespic=s=" + w + "x" + h + ":colors=" + col + "\" -frames:v 1 -f image2pipe -vcodec png -", 120000);
            if (png == null) return null;
            try { return Image.FromStream(new MemoryStream(png)); } catch { return null; }
        }

        static string T(double s) { return Math.Max(0, s).ToString("0.###", CultureInfo.InvariantCulture); }

        /// Builds the ffmpeg arguments to trim (keep) or cut out (remove) a range.
        public static Job BuildTrim(string input, MediaInfo mi, double start, double end, TrimMode mode, bool precise, string outDir, string overlayPng = null, int outW = 0, int outH = 0, Rectangle crop = default(Rectangle), double volume = 1)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            string ext = Formats.Ext(input);
            if (ext == "" ) ext = mi.HasVideo ? "mp4" : "mp3";
            Job job = new Job();
            job.Output = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(input) + " " + L.T("trimmed_suffix"), ext);
            double dur = mi.Duration;
            start = Math.Max(0, start); end = Math.Min(dur > 0 ? dur : end, end);
            if (end - start < 0.05) throw new UserError(L.T("err_sel_short"));

            if (mode == TrimMode.Remove)
            {
                bool atStart = start <= 0.05, atEnd = dur > 0 && end >= dur - 0.05;
                if (atStart && atEnd) throw new UserError(L.T("err_sel_whole"));
                if (atStart) { start = end; end = dur; mode = TrimMode.Keep; }
                else if (atEnd) { end = start; start = 0; mode = TrimMode.Keep; }
            }

            if (Math.Abs(volume - 1) > 0.005 && mi.HasAudio) precise = true;   // a new volume means re-encoding the sound
            string af = Math.Abs(volume - 1) > 0.005 && mi.HasAudio ? " -af \"volume=" + volume.ToString("0.###", CultureInfo.InvariantCulture) + "\"" : "";
            string codecs = Codecs(ext, mi);
            // text on top and/or a new size: the picture has to be re-encoded through a filter
            bool vf = mi.HasVideo && (overlayPng != null || (outW > 0 && outH > 0) || !crop.IsEmpty);
            string ovIn = overlayPng != null ? " -i " + Q(overlayPng) : "";
            Func<string, string> chain = src =>
            {
                string cr = crop.IsEmpty ? "" : "crop=" + crop.Width + ":" + crop.Height + ":" + crop.X + ":" + crop.Y + ",";
                string sc = outW > 0 && outH > 0 ? src + cr + "scale=" + outW + ":" + outH + ":flags=lanczos,setsar=1" : src + cr + "null";
                return overlayPng != null ? sc + "[vb];[vb][1:v]overlay=0:0:format=auto[v]" : sc + "[v]";
            };
            if (vf && Formats.CategoryOf(ext) != Category.Video) { ext = "mp4"; job.Output = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(input) + " " + L.T("edited_suffix"), ext); codecs = Codecs(ext, mi); }
            if (vf) job.Output = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(input) + " " + L.T("edited_suffix"), ext);
            if (vf && mode == TrimMode.Keep)
            {
                string args = "-hide_banner -y -ss " + T(start) + " -i " + Q(input) + ovIn + " -t " + T(end - start) +
                              " -filter_complex \"" + chain("[0:v:0]") + "\" -map \"[v]\" -map 0:a:0?" + af + " " + codecs + " " + Q(job.Output);
                job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = args });
                return job;
            }
            if (mode == TrimMode.Keep)
            {
                string args;
                if (!precise)
                    args = "-hide_banner -y -ss " + T(start) + " -i " + Q(input) + " -t " + T(end - start) + " -map 0:v? -map 0:a? -c copy -avoid_negative_ts make_zero " + Q(job.Output);
                else
                    args = "-hide_banner -y -ss " + T(start) + " -i " + Q(input) + " -t " + T(end - start) + " -map 0:v:0? -map 0:a:0?" + af + " " + codecs + " " + Q(job.Output);
                job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = args });
                return job;
            }

            // remove the middle: keep [0,start] + [end,dur] and join
            StringBuilder fc = new StringBuilder();
            string maps;
            if (mi.HasVideo && mi.HasAudio)
            {
                fc.AppendFormat("[0:v:0]trim=0:{0},setpts=PTS-STARTPTS[v0];[0:v:0]trim=start={1},setpts=PTS-STARTPTS[v1];", T(start), T(end));
                fc.AppendFormat("[0:a:0]atrim=0:{0},asetpts=PTS-STARTPTS[a0];[0:a:0]atrim=start={1},asetpts=PTS-STARTPTS[a1];", T(start), T(end));
                fc.Append(vf ? "[v0][a0][v1][a1]concat=n=2:v=1:a=1[vc][a];" + chain("[vc]") : "[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]");
                maps = "-map \"[v]\" -map \"[a]\"";
            }
            else if (mi.HasVideo)
            {
                fc.AppendFormat("[0:v:0]trim=0:{0},setpts=PTS-STARTPTS[v0];[0:v:0]trim=start={1},setpts=PTS-STARTPTS[v1];", T(start), T(end));
                fc.Append(vf ? "[v0][v1]concat=n=2:v=1:a=0[vc];" + chain("[vc]") : "[v0][v1]concat=n=2:v=1:a=0[v]");
                maps = "-map \"[v]\"";
            }
            else
            {
                fc.AppendFormat("[0:a:0]atrim=0:{0},asetpts=PTS-STARTPTS[a0];[0:a:0]atrim=start={1},asetpts=PTS-STARTPTS[a1];[a0][a1]concat=n=2:v=0:a=1[a]", T(start), T(end));
                maps = "-map \"[a]\"";
            }
            if (af.Length > 0 && maps.Contains("[a]")) { fc.Append(";[a]volume=" + volume.ToString("0.###", CultureInfo.InvariantCulture) + "[av]"); maps = maps.Replace("[a]", "[av]"); }
            job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = "-hide_banner -y -i " + Q(input) + (vf ? ovIn : "") + " -filter_complex \"" + fc + "\" " + maps + " " + codecs + " " + Q(job.Output) });
            return job;
        }

        public class MergeClip { public string File; public MediaInfo Info; public double Start, End; }

        /// A picture laid over the finished video (text), shown from..to seconds of the output (both < 0 = all the time).
        public class Overlay { public string Png; public double From = -1, To = -1; }

        /// Extra effects applied to the finished video.
        public class Fx
        {
            public double Volume = 1, Speed = 1;
            public bool Reverse;
            public string Music; public double MusicVolume = 0.8; public bool MusicReplace;
            public string Subs; public string SubsFont = "Segoe UI"; public int SubsSize = 22;
            public bool Gif; public int GifWidth = 480;
            public bool Special { get { return Math.Abs(Speed - 1) > 0.001 || Reverse || Music != null || Subs != null || Gif; } }
            public string Key
            {
                get
                {
                    return Volume.ToString("0.00") + "|" + Speed.ToString("0.00") + "|" + Reverse + "|" + Music + "|" + MusicVolume.ToString("0.00") + "|" + MusicReplace + "|" +
                           Subs + "|" + SubsFont + "|" + SubsSize;
                }
            }
        }

        /// A path written the way ffmpeg filters want it (C\:/folder/file).
        static string FilterPath(string p) { return p.Replace("\\", "/").Replace(":", "\\:").Replace("'", ""); }

        static string Atempo(double s)
        {
            // atempo works best between 0.5 and 2: chain it for anything outside
            List<string> parts = new List<string>();
            while (s > 2.0001) { parts.Add("atempo=2"); s /= 2; }
            while (s < 0.4999) { parts.Add("atempo=0.5"); s /= 0.5; }
            parts.Add("atempo=" + s.ToString("0.####", CultureInfo.InvariantCulture));
            return string.Join(",", parts.ToArray()) + ",";
        }

        /// Joins several clips (each trimmed) with optional transitions, fades, crop, new size, text, speed, music, subtitles - or makes a GIF.
        public static Job BuildMerge(List<MergeClip> clips, int baseW, int baseH, Rectangle crop, int outW, int outH, List<Overlay> overlays,
                                     string transition, double tDur, bool fadeIn, bool fadeOut, string outDir, Fx fx, bool preview = false)
        {
            if (fx == null) fx = new Fx();
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            bool video = clips[0].Info.HasVideo;
            bool gif = fx.Gif && video && !preview;
            bool audio = !gif;
            string first = clips[0].File;
            string ext = gif ? "gif" : video ? "mp4" : Formats.Ext(first);
            if (!video && (ext == "" || Formats.CategoryOf(ext) != Category.Audio)) ext = "mp3";
            Job job = new Job();
            job.Output = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(first) + " " + L.T(clips.Count > 1 ? "merged_suffix" : "edited_suffix"), ext);
            int n = clips.Count;
            double[] len = new double[n];
            for (int i = 0; i < n; i++) len[i] = Math.Max(0.1, clips[i].End - clips[i].Start);
            double minLen = double.MaxValue; foreach (double l in len) minLen = Math.Min(minLen, l);
            bool xf = transition != null && transition != "none" && n > 1;
            double d = xf ? Math.Max(0.1, Math.Min(tDur, minLen / 2 - 0.05)) : 0;
            double total = 0; foreach (double l in len) total += l;
            total -= d * (n - 1);
            double speed = Math.Max(0.25, Math.Min(4, fx.Speed));
            if (Math.Abs(speed - 1) > 0.001) total /= speed;
            baseW += baseW % 2; baseH += baseH % 2;
            StringBuilder args = new StringBuilder("-hide_banner -y");
            foreach (MergeClip c in clips) args.Append(" -i " + Q(c.File));
            int next = n;
            List<int> ovIdx = new List<int>(); int musIdx = -1;
            if (overlays != null && video) foreach (Overlay o in overlays) { args.Append(" -i " + Q(o.Png)); ovIdx.Add(next++); }
            if (fx.Music != null && audio) { args.Append(" -stream_loop -1 -i " + Q(fx.Music)); musIdx = next++; }
            StringBuilder fc = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                MergeClip c = clips[i];
                if (video)
                    fc.AppendFormat("[{0}:v:0]trim=start={1}:end={2},setpts=PTS-STARTPTS,scale={3}:{4}:force_original_aspect_ratio=decrease,pad={3}:{4}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{0}];",
                        i, T(c.Start), T(c.End), baseW, baseH);
                if (!audio) continue;
                if (c.Info.HasAudio)
                    fc.AppendFormat("[{0}:a:0]atrim=start={1}:end={2},asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{0}];", i, T(c.Start), T(c.End));
                else
                    fc.AppendFormat("anullsrc=r=48000:cl=stereo,atrim=0:{1},aformat=sample_fmts=fltp:channel_layouts=stereo[a{0}];", i, T(len[i]));
            }
            string vOut = "v0", aOut = "a0";
            if (n > 1)
            {
                if (xf)
                {
                    double offset = 0;
                    for (int i = 1; i < n; i++)
                    {
                        offset += len[i - 1] - d;
                        string vo = "vx" + i, ao = "ax" + i;
                        if (video) fc.AppendFormat("[{0}][v{1}]xfade=transition={2}:duration={3}:offset={4}[{5}];", vOut, i, transition, T(d), T(offset), vo);
                        if (audio) fc.AppendFormat("[{0}][a{1}]acrossfade=d={2}[{3}];", aOut, i, T(d), ao);
                        vOut = vo; aOut = ao;
                    }
                }
                else
                {
                    for (int i = 0; i < n; i++) fc.Append((video ? "[v" + i + "]" : "") + (audio ? "[a" + i + "]" : ""));
                    fc.AppendFormat("concat=n={0}:v={1}:a={2}{3}{4};", n, video ? 1 : 0, audio ? 1 : 0, video ? "[vc]" : "", audio ? "[ac]" : "");
                    vOut = "vc"; aOut = "ac";
                }
            }
            // speed and direction, then fades, crop, size, text and subtitles
            string vchain = "", achain = Math.Abs(fx.Volume - 1) > 0.005 ? "volume=" + fx.Volume.ToString("0.###", CultureInfo.InvariantCulture) + "," : "";
            if (fx.Reverse) { vchain += "reverse,"; achain += "areverse,"; }
            if (Math.Abs(speed - 1) > 0.001) { vchain += "setpts=PTS/" + speed.ToString("0.####", CultureInfo.InvariantCulture) + ","; achain += Atempo(speed); }
            string fades = "", afades = "";
            if (fadeIn) { fades += "fade=t=in:st=0:d=0.6,"; afades += "afade=t=in:st=0:d=0.6,"; }
            if (fadeOut) { fades += "fade=t=out:st=" + T(Math.Max(0, total - 0.8)) + ":d=0.8,"; afades += "afade=t=out:st=" + T(Math.Max(0, total - 0.8)) + ":d=0.8,"; }
            vchain += fades;
            if (!crop.IsEmpty) vchain += "crop=" + crop.Width + ":" + crop.Height + ":" + crop.X + ":" + crop.Y + ",";
            if (outW > 0 && outH > 0) vchain += "scale=" + outW + ":" + outH + ":flags=lanczos,setsar=1,";
            string maps = "";
            if (audio)
            {
                if (musIdx >= 0)
                {
                    fc.AppendFormat("[{0}]{1}anull[amain];", aOut, achain);
                    fc.AppendFormat("[{0}:a:0]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={1},atrim=0:{2},asetpts=PTS-STARTPTS[mus];",
                        musIdx, fx.MusicVolume.ToString("0.###", CultureInfo.InvariantCulture), T(total));
                    if (fx.MusicReplace) fc.Append("[mus]anull[amix];");
                    else fc.Append("[amain][mus]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[amix];");
                    fc.AppendFormat("[amix]{0}anull[aout]", afades);
                    if (fx.MusicReplace) fc.Append(";[amain]anullsink");
                }
                else fc.AppendFormat("[{0}]{1}{2}anull[aout]", aOut, achain, afades);
                maps = " -map \"[aout]\"";
            }
            if (video)
            {
                fc.AppendFormat("{0}[{1}]{2}null[vpre]", audio ? ";" : "", vOut, vchain);
                string last = "vpre";
                for (int k = 0; k < ovIdx.Count; k++)
                {
                    // timed text: only between its start and end
                    Overlay o = overlays[k];
                    string en = o.From > 0.001 || o.To >= 0 ? ":enable='between(t," + T(Math.Max(0, o.From)) + "," + T(o.To >= 0 ? o.To : 1e6) + ")'" : "";
                    fc.AppendFormat(";[{0}][{1}:v]overlay=0:0:format=auto{2}[vov{3}]", last, ovIdx[k], en, k);
                    last = "vov" + k;
                }
                if (fx.Subs != null && File.Exists(fx.Subs))
                {
                    // the .srt is copied to a plain temp name: ffmpeg's filter syntax doesn't like unusual characters in paths
                    string tmpSubs = Path.Combine(Path.GetTempPath(), "fadi_subs_" + Guid.NewGuid().ToString("N") + ".srt");
                    File.Copy(fx.Subs, tmpSubs, true);
                    job.Temps.Add(tmpSubs);
                    string style = "FontName=" + fx.SubsFont.Replace(",", " ").Replace("'", "") + ",FontSize=" + fx.SubsSize + ",PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=2,Shadow=1,MarginV=22";
                    string fdir = Directory.Exists(FontPack.Dir) ? ":fontsdir='" + FilterPath(FontPack.Dir) + "'" : "";
                    fc.AppendFormat(";[{0}]subtitles=filename='{1}'{2}:force_style='{3}'[vsub]", last, FilterPath(tmpSubs), fdir, style);
                    last = "vsub";
                }
                if (gif)
                    fc.AppendFormat(";[{0}]fps=12,scale='min({1},iw)':-2:flags=lanczos,split[g1][g2];[g1]palettegen=stats_mode=diff[gp];[g2][gp]paletteuse=dither=sierra2_4a[vout]", last, fx.GifWidth);
                else fc.AppendFormat(";[{0}]format=yuv420p[vout]", last);
                maps = " -map \"[vout]\"" + maps;
            }
            string codecs = gif ? "-loop 0" : video ? "-c:v libx264 -crf 19 -preset medium -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart" : Codecs(ext, clips[0].Info);
            if (preview)
            {
                // quick low-resolution render for watching the effects inside the app
                codecs = video ? "-c:v libx264 -crf 30 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 128k" : "-c:a pcm_s16le";
                job.Output = Path.Combine(Path.GetTempPath(), "fadi_preview_" + Guid.NewGuid().ToString("N") + (video ? ".mp4" : ".wav"));
            }
            if (musIdx >= 0) codecs += " -shortest";
            args.Append(" -filter_complex \"" + fc + "\"" + maps + " " + codecs + " " + Q(job.Output));
            job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = args.ToString(), Duration = total });
            return job;
        }

        /// The joined sound of the timeline (with its cuts, transitions and speed), as 16 kHz mono for speech recognition.
        public static Job BuildSpeech(List<MergeClip> clips, string transition, double tDur, double speedIn, string outWav)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            int n = clips.Count;
            double[] len = new double[n];
            for (int i = 0; i < n; i++) len[i] = Math.Max(0.1, clips[i].End - clips[i].Start);
            double minLen = double.MaxValue; foreach (double l in len) minLen = Math.Min(minLen, l);
            bool xf = transition != null && transition != "none" && n > 1;
            double d = xf ? Math.Max(0.1, Math.Min(tDur, minLen / 2 - 0.05)) : 0;
            double total = 0; foreach (double l in len) total += l;
            total -= d * (n - 1);
            double speed = Math.Max(0.25, Math.Min(4, speedIn));
            if (Math.Abs(speed - 1) > 0.001) total /= speed;
            StringBuilder args = new StringBuilder("-hide_banner -y");
            foreach (MergeClip c in clips) args.Append(" -i " + Q(c.File));
            StringBuilder fc = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                MergeClip c = clips[i];
                if (c.Info.HasAudio)
                    fc.AppendFormat("[{0}:a:0]atrim=start={1}:end={2},asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo[a{0}];", i, T(c.Start), T(c.End));
                else
                    fc.AppendFormat("anullsrc=r=48000:cl=stereo,atrim=0:{1}[a{0}];", i, T(len[i]));
            }
            string aOut = "a0";
            if (n > 1)
            {
                if (xf)
                    for (int i = 1; i < n; i++) { fc.AppendFormat("[{0}][a{1}]acrossfade=d={2}[ax{1}];", aOut, i, T(d)); aOut = "ax" + i; }
                else
                {
                    for (int i = 0; i < n; i++) fc.Append("[a" + i + "]");
                    fc.AppendFormat("concat=n={0}:v=0:a=1[ac];", n); aOut = "ac";
                }
            }
            string chain = Math.Abs(speed - 1) > 0.001 ? Atempo(speed) : "";
            fc.AppendFormat("[{0}]{1}aresample=16000,aformat=sample_fmts=s16:channel_layouts=mono[aout]", aOut, chain);
            Job job = new Job { Output = outWav };
            args.Append(" -filter_complex \"" + fc + "\" -map \"[aout]\" -c:a pcm_s16le -ar 16000 -ac 1 " + Q(outWav));
            job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = args.ToString(), Duration = total });
            return job;
        }

        static string Codecs(string ext, MediaInfo mi)
        {
            string a;
            switch (ext)
            {
                case "mp3": return "-c:a libmp3lame -q:a 0";
                case "wav": return "-c:a pcm_s16le";
                case "flac": return "-c:a flac";
                case "ogg": case "oga": return "-c:a libvorbis -q:a 7";
                case "opus": return "-c:a libopus -b:a 160k";
                case "wma": return "-c:a wmav2 -b:a 256k";
                case "aiff": case "aif": return "-c:a pcm_s16be";
                case "m4a": case "aac": case "m4b": return "-c:a aac -b:a 256k";
            }
            a = "-c:a aac -b:a 192k";
            string v = "-c:v libx264 -crf 18 -preset medium -pix_fmt yuv420p";
            if (ext == "webm") { v = "-c:v libvpx-vp9 -crf 28 -b:v 0 -row-mt 1"; a = "-c:a libopus -b:a 160k"; }
            else if (ext == "avi") { v = "-c:v mpeg4 -q:v 2"; a = "-c:a libmp3lame -q:a 2"; }
            else if (ext == "wmv" || ext == "asf") { v = "-c:v wmv2 -b:v 8M"; a = "-c:a wmav2 -b:a 192k"; }
            else if (ext == "mpg" || ext == "mpeg" || ext == "vob") { v = "-c:v mpeg2video -q:v 2"; a = "-c:a mp2 -b:a 224k"; }
            else if (ext == "flv") { a = "-c:a aac -b:a 160k"; }
            string extra = (ext == "mp4" || ext == "mov" || ext == "m4v") ? " -movflags +faststart" : "";
            return (mi.HasVideo ? v + " " : "") + (mi.HasAudio ? a : "") + extra;
        }

        static Process preview;
        public static void Play(string file, double start, double length, bool hasVideo)
        {
            StopPreview();
            string fp = Tools.FFplay;
            if (fp == null) { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } return; }
            string args = "-hide_banner -loglevel error -autoexit -window_title " + Q(L.T("preview_title")) + " -ss " + T(start) + (length > 0 ? " -t " + T(length) : "") +
                          (hasVideo ? " -x 960 -y 540" : " -showmode 1 -x 720 -y 240") + " " + Q(file);
            ProcessStartInfo psi = new ProcessStartInfo(fp, args);
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            try { preview = Process.Start(psi); } catch { }
        }

        public static void StopPreview()
        {
            try { if (preview != null && !preview.HasExited) preview.Kill(); } catch { }
            preview = null;
        }

        public static bool Previewing { get { try { return preview != null && !preview.HasExited; } catch { return false; } } }
    }
}

namespace FileConverter
{
    /// In-app preview. FFmpeg decodes small raw frames that we paint ourselves; the sound is decoded to a WAV in
    /// memory and started at the exact moment the first frame shows, so picture and sound stay together.
    public class PreviewPlayer
    {
        System.Diagnostics.Process video;
        System.Media.SoundPlayer sound;
        System.Threading.Thread thread;
        volatile bool stop;
        public bool Playing { get { return thread != null && thread.IsAlive && !stop; } }
        public int Fps = 25;

        static byte[] DecodeWav(string file, double from, double dur, double volume)
        {
            string ff = Tools.FFmpeg; if (ff == null) return null;
            System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
            string args = "-hide_banner -loglevel quiet -ss " + from.ToString("0.###", ci) + " -i \"" + file + "\" -t " + Math.Min(dur, 600).ToString("0.###", ci) +
                          " -vn -ac 2 -ar 44100 -af \"volume=" + volume.ToString("0.###", ci) + "\" -f wav pipe:1";
            System.Diagnostics.Process p = StartHidden(ff, args, true);
            System.IO.MemoryStream ms = new System.IO.MemoryStream();
            try { p.StandardOutput.BaseStream.CopyTo(ms); p.WaitForExit(5000); } catch { return null; }
            byte[] b = ms.ToArray();
            if (b.Length < 1000) return null;
            // a WAV written to a pipe has no sizes in its header: fill them in
            WriteInt(b, 4, b.Length - 8);
            for (int i = 12; i + 8 < b.Length && i < 4096; i++)
                if (b[i] == 'd' && b[i + 1] == 'a' && b[i + 2] == 't' && b[i + 3] == 'a') { WriteInt(b, i + 4, b.Length - i - 8); break; }
            return b;
        }
        static void WriteInt(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24); }

        public void Start(string file, double from, double to, int w, int h, bool hasVideo, double volume,
                          Action<System.Drawing.Bitmap, double> frame, Action<double> tick, Action done)
        {
            Stop();
            stop = false;
            double dur = Math.Max(0.1, to - from);
            string ts = from.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string ds = dur.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            w += w % 2; h += h % 2;
            thread = new System.Threading.Thread(() =>
            {
                try
                {
                    byte[] wav = volume > 0.001 ? DecodeWav(file, from, dur, volume) : null;
                    if (stop) return;
                    if (wav != null)
                        try { sound = new System.Media.SoundPlayer(new System.IO.MemoryStream(wav)); sound.Load(); } catch { sound = null; }
                    System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
                    if (hasVideo && Tools.FFmpeg != null)
                    {
                        video = StartHidden(Tools.FFmpeg, "-hide_banner -loglevel quiet -ss " + ts + " -i \"" + file + "\" -t " + ds + " -an -vf \"scale=" + w + ":" + h + "\" -r " + Fps + " -f rawvideo -pix_fmt bgra pipe:1", true);
                        System.IO.Stream s = video.StandardOutput.BaseStream;
                        byte[] buf = new byte[w * h * 4];
                        int i = 0;
                        while (!stop)
                        {
                            int got = 0, r;
                            while (got < buf.Length && (r = s.Read(buf, got, buf.Length - got)) > 0) got += r;
                            if (got < buf.Length) break;
                            if (i == 0)
                            {
                                // sync point: sound and clock start together with the first picture
                                try { if (sound != null) sound.Play(); } catch { }
                                sw.Start();
                            }
                            double t = i / (double)Fps;
                            double ahead = t - sw.Elapsed.TotalSeconds;
                            if (ahead > 0) System.Threading.Thread.Sleep((int)(ahead * 1000));
                            else if (ahead < -0.12) { i++; continue; }   // running late: skip this frame
                            System.Drawing.Bitmap b = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            System.Drawing.Imaging.BitmapData bd = b.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(buf, y * w * 4, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), w * 4);
                            b.UnlockBits(bd);
                            if (stop) { b.Dispose(); break; }
                            frame(b, from + t);
                            i++;
                        }
                    }
                    else
                    {
                        try { if (sound != null) sound.Play(); } catch { }
                        sw.Start();
                        while (!stop && sw.Elapsed.TotalSeconds < dur) { tick(from + sw.Elapsed.TotalSeconds); System.Threading.Thread.Sleep(40); }
                    }
                }
                catch { }
                bool finished = !stop;
                Kill();
                if (finished) done();
            });
            thread.IsBackground = true;
            thread.Start();
        }

        static System.Diagnostics.Process StartHidden(string exe, string args, bool readOut)
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = readOut, RedirectStandardError = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            return System.Diagnostics.Process.Start(psi);
        }

        void Kill()
        {
            if (video != null) try { if (!video.HasExited) video.Kill(); } catch { }
            video = null;
            if (sound != null) try { sound.Stop(); sound.Dispose(); } catch { }
            sound = null;
        }

        public void Stop()
        {
            stop = true;
            Kill();
            thread = null;
        }
    }
}
