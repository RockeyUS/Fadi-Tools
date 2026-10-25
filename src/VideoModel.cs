using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace FileConverter
{
    public enum VKind { Video, Image, Audio, Text, Captions }
    public enum VTrackKind { Main, Overlay, Text, Audio, Captions }

    /// One keyframe: a value at a time (seconds from the start of its clip on the timeline).
    public class VKey { public double T; public float V; public VKey Copy() { return new VKey { T = T, V = V }; } }

    /// One caption: its words and when it shows (seconds on the timeline).
    public class Cue
    {
        public double Start, End; public string Text = "";
        public Cue Copy() { return new Cue { Start = Start, End = End, Text = Text }; }
        /// Roughly when each word is said (spread over the cue by length), for "word highlight" captions.
        public List<KeyValuePair<string, double>> Words()
        {
            List<KeyValuePair<string, double>> r = new List<KeyValuePair<string, double>>();
            string[] w = (Text ?? "").Replace("\r", " ").Replace("\n", " ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int total = 0; foreach (string x in w) total += x.Length + 1;
            double t = Start, span = Math.Max(0.1, End - Start);
            foreach (string x in w) { r.Add(new KeyValuePair<string, double>(x, t)); t += span * (x.Length + 1) / Math.Max(1, total); }
            return r;
        }
    }

    /// A clip on the timeline: part of a video, a photo, a sound, a text, or the captions.
    public class VClip
    {
        public string Id = NewId();
        public VKind Kind;
        public MediaSource Src;
        public string File { get { return Src == null ? null : Src.File; } }
        /// Which part of the source (seconds); photos and texts: 0 … their length.
        public double In, Out;
        /// Where it starts on the timeline (the main track works this out itself).
        public double At;
        public double Speed = 1;
        public float Volume = 1;
        public double FadeIn, FadeOut;
        public bool Muted, Denoise;
        /// This video's sound was moved to an audio track ("Detach audio").
        public bool Detached;
        public Adjust Adj = new Adjust();
        /// Main track: how it goes into the next clip.
        public string Trans = "none"; public double TransDur = 0.6;
        // where it is on the picture (overlays, texts): the middle as a share of the picture, size, turn, how see-through
        public float X = 0.5f, Y = 0.5f, Scale = 1f, Rot = 0, Opacity = 1;
        /// Keyframes: "x", "y", "scale", "rot", "opacity" (and "vol" for sound).
        public Dictionary<string, List<VKey>> Keys = new Dictionary<string, List<VKey>>();
        // text
        public TextLayer Text;
        public string AnimIn = "none", AnimOut = "none";
        public double AnimInDur = 0.5, AnimOutDur = 0.5;
        // captions
        public List<Cue> Cues;
        public string CapStyle = "outline";
        public Color CapColor = Color.White, CapHigh = Color.FromArgb(255, 214, 10);
        public float CapSize = 1f, CapY = 0.82f;
        public string CapFont;
        /// A video with see-through parts (a removed background): decoded so they stay see-through.
        public bool Alpha;

        public static string NewId() { return Guid.NewGuid().ToString("N").Substring(0, 10); }

        public double Length { get { return Math.Max(0.05, (Out - In) / Math.Max(0.01, Speed)); } }
        public double End { get { return At + Length; } }
        public bool Visual { get { return Kind == VKind.Video || Kind == VKind.Image || Kind == VKind.Text || Kind == VKind.Captions; } }
        public bool HasSound { get { return (Kind == VKind.Video && Src != null && Src.Info != null && Src.Info.HasAudio && !Detached) || Kind == VKind.Audio; } }
        public string Name
        {
            get
            {
                if (Kind == VKind.Text) return Text != null ? Text.Label : "";
                if (Kind == VKind.Captions) return L.T("vs_captions");
                return File != null ? Path.GetFileName(File) : "";
            }
        }

        public VClip Copy()
        {
            VClip c = (VClip)MemberwiseClone();
            c.Adj = Adj != null ? Adj.Clone() : new Adjust();
            c.Keys = new Dictionary<string, List<VKey>>();
            foreach (KeyValuePair<string, List<VKey>> kv in Keys) { List<VKey> l = new List<VKey>(); foreach (VKey k in kv.Value) l.Add(k.Copy()); c.Keys[kv.Key] = l; }
            if (Text != null) c.Text = Text.CloneStyle();
            if (Cues != null) { c.Cues = new List<Cue>(); foreach (Cue q in Cues) c.Cues.Add(q.Copy()); }
            return c;
        }
        public VClip Duplicate() { VClip c = Copy(); c.Id = NewId(); return c; }

        /// A property at a time on the timeline (from its keyframes, or its plain value).
        public float Eval(string prop, double t, float plain)
        {
            List<VKey> k;
            if (!Keys.TryGetValue(prop, out k) || k.Count == 0) return plain;
            double lt = t - At;
            if (lt <= k[0].T) return k[0].V;
            if (lt >= k[k.Count - 1].T) return k[k.Count - 1].V;
            for (int i = 1; i < k.Count; i++)
                if (lt <= k[i].T)
                {
                    double f = (lt - k[i - 1].T) / Math.Max(0.0001, k[i].T - k[i - 1].T);
                    f = f * f * (3 - 2 * f);   // eased, like CapCut's default
                    return (float)(k[i - 1].V + (k[i].V - k[i - 1].V) * f);
                }
            return plain;
        }
        public bool HasKeys(string prop) { List<VKey> k; return Keys.TryGetValue(prop, out k) && k.Count > 0; }
        public VKey KeyAt(string prop, double t)
        {
            List<VKey> k; if (!Keys.TryGetValue(prop, out k)) return null;
            foreach (VKey x in k) if (Math.Abs(x.T - (t - At)) < 0.04) return x;
            return null;
        }
        /// Sets a keyframe at a time (keeps them in order).
        public void SetKey(string prop, double t, float v)
        {
            List<VKey> k;
            if (!Keys.TryGetValue(prop, out k)) { k = new List<VKey>(); Keys[prop] = k; }
            double lt = Math.Max(0, Math.Min(Length, t - At));
            VKey ex = k.Find(x => Math.Abs(x.T - lt) < 0.04);
            if (ex != null) { ex.V = v; return; }
            k.Add(new VKey { T = lt, V = v });
            k.Sort((a, b) => a.T.CompareTo(b.T));
        }
        public void RemoveKey(string prop, double t)
        {
            List<VKey> k;
            if (!Keys.TryGetValue(prop, out k)) return;
            k.RemoveAll(x => Math.Abs(x.T - (t - At)) < 0.04);
            if (k.Count == 0) Keys.Remove(prop);
        }
        /// All keyframe times (seconds from the clip's start), for the little diamonds on the timeline.
        public List<double> KeyTimes()
        {
            List<double> r = new List<double>();
            foreach (List<VKey> l in Keys.Values) foreach (VKey k in l) if (!r.Exists(x => Math.Abs(x - k.T) < 0.04)) r.Add(k.T);
            r.Sort();
            return r;
        }
    }

    public class VTrack
    {
        public string Id = VClip.NewId();
        public VTrackKind Kind;
        public List<VClip> Clips = new List<VClip>();
        public bool Muted, Hidden;
        public VTrack Copy() { VTrack t = (VTrack)MemberwiseClone(); t.Clips = new List<VClip>(); foreach (VClip c in Clips) t.Clips.Add(c.Copy()); return t; }
        public bool Visual { get { return Kind != VTrackKind.Audio; } }
    }

    /// A video project: the picture size and shape, and the tracks (top of the list = top of the timeline).
    public class VProject
    {
        public int W = 1920, H = 1080;
        public double Fps = 30;
        /// "orig" (the first clip's shape), "16:9", "9:16", "1:1", "4:5", "4:3".
        public string Aspect = "orig";
        /// What fills the empty parts when a clip doesn't fit the shape: "blur", "black", "color".
        public string Fill = "blur";
        public Color FillColor = Color.Black;
        public List<VTrack> Tracks = new List<VTrack>();

        public VProject() { Tracks.Add(new VTrack { Kind = VTrackKind.Main }); }

        public VTrack Main { get { foreach (VTrack t in Tracks) if (t.Kind == VTrackKind.Main) return t; return null; } }
        public int MainIndex { get { return Tracks.IndexOf(Main); } }

        /// Lines up the main track's clips one after another (transitions overlap them a little).
        public void Layout()
        {
            List<VClip> m = Main.Clips;
            double at = 0;
            for (int i = 0; i < m.Count; i++)
            {
                m[i].At = at;
                at += m[i].Length - (i < m.Count - 1 ? TransLen(i) : 0);
            }
        }

        /// How long the transition after main clip i really is (never more than half of either clip).
        public double TransLen(int i)
        {
            List<VClip> m = Main.Clips;
            if (i < 0 || i >= m.Count - 1 || m[i].Trans == "none" || m[i].Kind == VKind.Audio) return 0;
            return Math.Max(0.1, Math.Min(m[i].TransDur, Math.Min(m[i].Length, m[i + 1].Length) / 2 - 0.05));
        }

        public double Duration
        {
            get
            {
                double d = 0;
                foreach (VTrack t in Tracks) foreach (VClip c in t.Clips) if (c.End > d) d = c.End;
                return d;
            }
        }

        public bool Empty { get { foreach (VTrack t in Tracks) if (t.Clips.Count > 0) return false; return true; } }

        public VClip Find(string id) { foreach (VTrack t in Tracks) foreach (VClip c in t.Clips) if (c.Id == id) return c; return null; }
        public VTrack TrackOf(VClip c) { foreach (VTrack t in Tracks) if (t.Clips.Contains(c)) return t; return null; }

        public VProject Copy()
        {
            VProject p = (VProject)MemberwiseClone();
            p.Tracks = new List<VTrack>();
            foreach (VTrack t in Tracks) p.Tracks.Add(t.Copy());
            return p;
        }

        /// The picture size for a shape, from the main video's size (even numbers, at most 4K on the long side).
        public void SizeFor(int srcW, int srcH)
        {
            if (srcW <= 0 || srcH <= 0) { srcW = 1920; srcH = 1080; }
            int longSide = Math.Max(720, Math.Min(3840, Math.Max(srcW, srcH)));
            int w, h;
            switch (Aspect)
            {
                case "16:9": w = longSide; h = longSide * 9 / 16; break;
                case "9:16": h = longSide; w = longSide * 9 / 16; break;
                case "1:1": w = h = Math.Min(longSide, 2160); break;
                case "4:5": h = longSide; w = longSide * 4 / 5; break;
                case "4:3": w = longSide; h = longSide * 3 / 4; break;
                default: w = srcW; h = srcH; break;
            }
            W = Math.Max(16, w - w % 2); H = Math.Max(16, h - h % 2);
        }

        /// A track of a kind (made when there isn't a free one): overlays and texts go above the main track, sounds under it.
        public VTrack TrackFor(VTrackKind kind, double at, double len)
        {
            foreach (VTrack t in Tracks)
            {
                if (t.Kind != kind) continue;
                bool free = true;
                foreach (VClip c in t.Clips) if (c.At < at + len - 0.01 && c.End > at + 0.01) { free = false; break; }
                if (free) return t;
            }
            VTrack n = new VTrack { Kind = kind };
            if (kind == VTrackKind.Audio) Tracks.Add(n);
            else if (kind == VTrackKind.Captions) Tracks.Insert(0, n);
            else
            {
                // texts above overlays, both above the main track
                int at2 = MainIndex;
                if (kind == VTrackKind.Text) { int first = Tracks.FindIndex(t => t.Kind == VTrackKind.Overlay); if (first >= 0) at2 = first; }
                Tracks.Insert(at2, n);
            }
            return n;
        }

        /// Drops tracks with nothing on them (except the main one).
        public void Tidy() { Tracks.RemoveAll(t => t.Kind != VTrackKind.Main && t.Clips.Count == 0); foreach (VTrack t in Tracks) if (t.Kind != VTrackKind.Main) t.Clips.Sort((a, b) => a.At.CompareTo(b.At)); }

        // ------------------------------------------------------------------ the project file (.fadiproj, version 2)
        public string ToJson(string projectPath)
        {
            string baseDir = projectPath != null ? Path.GetDirectoryName(projectPath) : null;
            StringBuilder b = new StringBuilder();
            b.Append("{\n  \"app\": \"Fadi Tools\", \"kind\": \"video\", \"version\": 2,\n");
            b.Append("  \"saved\": ").Append(Js.Q(DateTime.Now.ToString("o"))).Append(",\n");
            b.Append("  \"w\": ").Append(W).Append(", \"h\": ").Append(H).Append(", \"fps\": ").Append(Js.N(Fps)).Append(", \"aspect\": ").Append(Js.Q(Aspect))
             .Append(", \"fill\": ").Append(Js.Q(Fill)).Append(", \"fillColor\": ").Append(FillColor.ToArgb()).Append(",\n  \"tracks\": [");
            for (int ti = 0; ti < Tracks.Count; ti++)
            {
                VTrack t = Tracks[ti];
                b.Append(ti > 0 ? ",\n    " : "\n    ").Append("{\"kind\": ").Append(Js.Q(t.Kind.ToString())).Append(", \"muted\": ").Append(Js.B(t.Muted)).Append(", \"hidden\": ").Append(Js.B(t.Hidden)).Append(", \"clips\": [");
                for (int ci = 0; ci < t.Clips.Count; ci++)
                {
                    VClip c = t.Clips[ci];
                    b.Append(ci > 0 ? ",\n      " : "\n      ").Append('{');
                    b.Append("\"kind\": ").Append(Js.Q(c.Kind.ToString()));
                    if (c.File != null)
                    {
                        string rel = null; try { if (baseDir != null) rel = RelPath(baseDir, c.File); } catch { }
                        b.Append(", \"file\": ").Append(Js.Q(c.File)).Append(", \"rel\": ").Append(Js.Q(rel));
                    }
                    b.Append(", \"in\": ").Append(Js.N(c.In)).Append(", \"out\": ").Append(Js.N(c.Out)).Append(", \"at\": ").Append(Js.N(c.At)).Append(", \"speed\": ").Append(Js.N(c.Speed));
                    b.Append(", \"vol\": ").Append(Js.N(c.Volume)).Append(", \"fadeIn\": ").Append(Js.N(c.FadeIn)).Append(", \"fadeOut\": ").Append(Js.N(c.FadeOut));
                    b.Append(", \"muted\": ").Append(Js.B(c.Muted)).Append(", \"denoise\": ").Append(Js.B(c.Denoise)).Append(", \"detached\": ").Append(Js.B(c.Detached)).Append(", \"alpha\": ").Append(Js.B(c.Alpha));
                    if (c.Adj != null && !c.Adj.None) b.Append(", \"adjust\": ").Append(Js.Q(c.Adj.Key()));
                    b.Append(", \"trans\": ").Append(Js.Q(c.Trans)).Append(", \"transDur\": ").Append(Js.N(c.TransDur));
                    b.Append(", \"x\": ").Append(Js.N(c.X)).Append(", \"y\": ").Append(Js.N(c.Y)).Append(", \"scale\": ").Append(Js.N(c.Scale)).Append(", \"rot\": ").Append(Js.N(c.Rot)).Append(", \"opacity\": ").Append(Js.N(c.Opacity));
                    if (c.Keys.Count > 0)
                    {
                        b.Append(", \"keys\": {");
                        int n = 0;
                        foreach (KeyValuePair<string, List<VKey>> kv in c.Keys)
                        {
                            b.Append(n++ > 0 ? ", " : "").Append(Js.Q(kv.Key)).Append(": [");
                            for (int i = 0; i < kv.Value.Count; i++) b.Append(i > 0 ? "," : "").Append('[').Append(Js.N(kv.Value[i].T)).Append(',').Append(Js.N(kv.Value[i].V)).Append(']');
                            b.Append(']');
                        }
                        b.Append('}');
                    }
                    if (c.Text != null) b.Append(", \"text\": ").Append(Js.Layer(c.Text));
                    b.Append(", \"animIn\": ").Append(Js.Q(c.AnimIn)).Append(", \"animOut\": ").Append(Js.Q(c.AnimOut)).Append(", \"animInDur\": ").Append(Js.N(c.AnimInDur)).Append(", \"animOutDur\": ").Append(Js.N(c.AnimOutDur));
                    if (c.Cues != null)
                    {
                        b.Append(", \"capStyle\": ").Append(Js.Q(c.CapStyle)).Append(", \"capColor\": ").Append(c.CapColor.ToArgb()).Append(", \"capHigh\": ").Append(c.CapHigh.ToArgb())
                         .Append(", \"capSize\": ").Append(Js.N(c.CapSize)).Append(", \"capY\": ").Append(Js.N(c.CapY)).Append(", \"capFont\": ").Append(Js.Q(c.CapFont)).Append(", \"cues\": [");
                        for (int i = 0; i < c.Cues.Count; i++) b.Append(i > 0 ? "," : "").Append('[').Append(Js.N(c.Cues[i].Start)).Append(',').Append(Js.N(c.Cues[i].End)).Append(',').Append(Js.Q(c.Cues[i].Text)).Append(']');
                        b.Append(']');
                    }
                    b.Append('}');
                }
                b.Append(t.Clips.Count > 0 ? "\n    ]}" : "]}");
            }
            b.Append("\n  ]\n}\n");
            return b.ToString();
        }

        static string RelPath(string fromDir, string file)
        {
            Uri a = new Uri(fromDir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar), b = new Uri(file);
            if (a.Scheme != b.Scheme) return null;
            return Uri.UnescapeDataString(a.MakeRelativeUri(b).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        /// The files a project needs (looked up next to the project when it was moved with them), and the missing ones.
        public static string Locate(string file, string rel, string dir)
        {
            if (file != null && System.IO.File.Exists(file)) return file;
            if (rel != null && dir != null) { try { string r = Path.GetFullPath(Path.Combine(dir, rel)); if (System.IO.File.Exists(r)) return r; } catch { } }
            if (file != null && dir != null) { string n = Path.Combine(dir, Path.GetFileName(file)); if (System.IO.File.Exists(n)) return n; }
            return null;
        }

        /// Reads a project; `source` makes (or reuses) the media for a file. Old projects (version 1) are converted.
        public static VProject FromJson(object root, string dir, Func<string, VKind, MediaSource> source, List<string> missing)
        {
            if (Js.S(root, "kind") != "video") throw new UserError(L.T("proj_bad", "kind"));
            if (Js.I(root, "version", 1) < 2) return FromV1(root, dir, source, missing);
            VProject p = new VProject();
            p.Tracks.Clear();
            p.W = Js.I(root, "w", 1920); p.H = Js.I(root, "h", 1080); p.Fps = Js.D(root, "fps", 30);
            p.Aspect = Js.S(root, "aspect", "orig"); p.Fill = Js.S(root, "fill", "blur"); p.FillColor = Color.FromArgb(Js.I(root, "fillColor", -16777216));
            foreach (object to in Js.A(root, "tracks"))
            {
                VTrack t = new VTrack { Muted = Js.Bo(to, "muted"), Hidden = Js.Bo(to, "hidden") };
                try { t.Kind = (VTrackKind)Enum.Parse(typeof(VTrackKind), Js.S(to, "kind", "Main")); } catch { continue; }
                foreach (object co in Js.A(to, "clips"))
                {
                    VClip c = new VClip();
                    try { c.Kind = (VKind)Enum.Parse(typeof(VKind), Js.S(co, "kind", "Video")); } catch { continue; }
                    string f = Js.S(co, "file");
                    if (f != null)
                    {
                        string found = Locate(f, Js.S(co, "rel"), dir);
                        if (found == null) { if (missing != null && !missing.Contains(Path.GetFileName(f))) missing.Add(Path.GetFileName(f)); continue; }
                        c.Src = source(found, c.Kind);
                        if (c.Src == null) continue;
                    }
                    c.In = Js.D(co, "in"); c.Out = Js.D(co, "out", 1); c.At = Js.D(co, "at"); c.Speed = Js.D(co, "speed", 1);
                    c.Volume = (float)Js.D(co, "vol", 1); c.FadeIn = Js.D(co, "fadeIn"); c.FadeOut = Js.D(co, "fadeOut");
                    c.Muted = Js.Bo(co, "muted"); c.Denoise = Js.Bo(co, "denoise"); c.Detached = Js.Bo(co, "detached"); c.Alpha = Js.Bo(co, "alpha");
                    string adj = Js.S(co, "adjust"); if (adj != null) c.Adj = Adjust.FromKey(adj) ?? new Adjust();
                    c.Trans = Js.S(co, "trans", "none"); c.TransDur = Js.D(co, "transDur", 0.6);
                    c.X = (float)Js.D(co, "x", 0.5); c.Y = (float)Js.D(co, "y", 0.5); c.Scale = (float)Js.D(co, "scale", 1); c.Rot = (float)Js.D(co, "rot"); c.Opacity = (float)Js.D(co, "opacity", 1);
                    JsonObject keys = Js.Get(co, "keys") as JsonObject;
                    if (keys != null)
                        foreach (KeyValuePair<string, object> kv in keys)
                        {
                            List<VKey> l = new List<VKey>();
                            List<object> arr = kv.Value as List<object>;
                            if (arr != null) foreach (object k in arr) { List<object> pr = k as List<object>; if (pr != null && pr.Count >= 2) l.Add(new VKey { T = Num(pr[0]), V = (float)Num(pr[1]) }); }
                            if (l.Count > 0) c.Keys[kv.Key] = l;
                        }
                    object tx = Js.Get(co, "text"); if (tx != null) c.Text = Js.Layer(tx);
                    c.AnimIn = Js.S(co, "animIn", "none"); c.AnimOut = Js.S(co, "animOut", "none"); c.AnimInDur = Js.D(co, "animInDur", 0.5); c.AnimOutDur = Js.D(co, "animOutDur", 0.5);
                    if (Js.Get(co, "cues") != null)
                    {
                        c.Cues = new List<Cue>();
                        foreach (object q in Js.A(co, "cues")) { List<object> pr = q as List<object>; if (pr != null && pr.Count >= 3) c.Cues.Add(new Cue { Start = Num(pr[0]), End = Num(pr[1]), Text = pr[2] as string ?? "" }); }
                        c.CapStyle = Js.S(co, "capStyle", "outline"); c.CapColor = Color.FromArgb(Js.I(co, "capColor", -1)); c.CapHigh = Color.FromArgb(Js.I(co, "capHigh", unchecked((int)0xFFFFD60A)));
                        c.CapSize = (float)Js.D(co, "capSize", 1); c.CapY = (float)Js.D(co, "capY", 0.82); c.CapFont = Js.S(co, "capFont");
                    }
                    t.Clips.Add(c);
                }
                p.Tracks.Add(t);
            }
            if (p.Main == null) p.Tracks.Insert(0, new VTrack { Kind = VTrackKind.Main });
            p.Layout();
            return p;
        }

        static double Num(object o) { if (o is long) return (long)o; if (o is double) return (double)o; return 0; }

        /// A project from before 1.7: one row of clips, texts on top, one transition, music and subtitles for everything.
        static VProject FromV1(object root, string dir, Func<string, VKind, MediaSource> source, List<string> missing)
        {
            VProject p = new VProject();
            string trans = Js.S(root, "transition", "none"); double tdur = Js.D(root, "fxDur", 0.8);
            double speed = 1; int si = Js.I(root, "speed", 2); double[] speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 }; if (si >= 0 && si < speeds.Length) speed = speeds[si];
            float vol = (float)(Js.D(root, "volume", 100) / 100);
            foreach (object co in Js.A(root, "clips"))
            {
                string f = Locate(Js.S(co, "file"), Js.S(co, "rel"), dir);
                if (f == null) { string n = Js.S(co, "file"); if (n != null && missing != null && !missing.Contains(Path.GetFileName(n))) missing.Add(Path.GetFileName(n)); continue; }
                MediaSource src = source(f, VKind.Video);
                if (src == null) continue;
                bool audioOnly = src.Info != null && !src.Info.HasVideo;
                p.Main.Clips.Add(new VClip { Kind = audioOnly ? VKind.Audio : VKind.Video, Src = src, In = Js.D(co, "start"), Out = Js.D(co, "end", 1), Speed = speed, Volume = vol, Trans = trans, TransDur = tdur });
            }
            if (p.Main.Clips.Count > 0)
            {
                if (Js.Bo(root, "fadeIn")) p.Main.Clips[0].FadeIn = 0.6;
                if (Js.Bo(root, "fadeOut")) p.Main.Clips[p.Main.Clips.Count - 1].FadeOut = 0.8;
                MediaSource s0 = p.Main.Clips[0].Src;
                p.W = Js.I(root, "w", s0.W); p.H = Js.I(root, "h", s0.H);
            }
            p.Layout();
            double total = p.Duration;
            // texts: each becomes a text clip where it showed
            foreach (object tx in Js.A(root, "texts"))
            {
                TextLayer t = Js.Layer(tx);
                if (t.IsPicture || string.IsNullOrEmpty(t.Text)) continue;
                double from = Math.Max(0, t.From), to = t.To < 0 ? total : Math.Min(total, t.To);
                if (to - from < 0.1) continue;
                VClip c = new VClip { Kind = VKind.Text, Text = t, In = 0, Out = to - from, At = from, X = t.Pos.X / Math.Max(1, p.W), Y = t.Pos.Y / Math.Max(1, p.H) };
                p.TrackFor(VTrackKind.Text, c.At, c.Length).Clips.Add(c);
            }
            string music = Locate(Js.S(root, "music"), null, dir);
            if (music != null)
            {
                MediaSource ms = source(music, VKind.Audio);
                if (ms != null && ms.Info != null)
                {
                    double len = Math.Min(total, ms.Info.Duration);
                    VClip mc = new VClip { Kind = VKind.Audio, Src = ms, In = 0, Out = Math.Max(0.1, len), At = 0, Volume = (float)(Js.D(root, "musicVol", 80) / 100) };
                    p.TrackFor(VTrackKind.Audio, 0, mc.Length).Clips.Add(mc);
                    if (Js.I(root, "musicMode") == 1) foreach (VClip c in p.Main.Clips) c.Muted = true;
                }
            }
            string subs = Locate(Js.S(root, "subs"), null, dir);
            if (subs != null)
            {
                VClip cap = Captions.FromSrt(subs);
                if (cap != null) p.TrackFor(VTrackKind.Captions, cap.At, cap.Length).Clips.Add(cap);
            }
            return p;
        }
    }

    /// Captions: reading .srt files into a captions clip, and writing them back out.
    public static class Captions
    {
        public static VClip FromSrt(string path)
        {
            List<Cue> cues = ParseSrt(File.ReadAllText(path, Encoding.UTF8));
            if (cues.Count == 0) return null;
            return Make(cues);
        }

        public static VClip Make(List<Cue> cues)
        {
            double a = cues[0].Start, b = cues[cues.Count - 1].End;
            return new VClip { Kind = VKind.Captions, Cues = cues, At = a, In = 0, Out = Math.Max(0.2, b - a) };
        }

        public static List<Cue> ParseSrt(string text)
        {
            List<Cue> l = new List<Cue>();
            string[] blocks = text.Replace("\r\n", "\n").Replace("\r", "\n").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string blk in blocks)
            {
                string[] lines = blk.Trim('\n').Split('\n');
                int ti = Array.FindIndex(lines, x => x.Contains("-->"));
                if (ti < 0) continue;
                string[] tt = lines[ti].Split(new[] { "-->" }, StringSplitOptions.None);
                double s, e;
                if (!Time(tt[0], out s) || !Time(tt[1], out e)) continue;
                List<string> words = new List<string>();
                for (int i = ti + 1; i < lines.Length; i++) if (lines[i].Trim().Length > 0) words.Add(System.Text.RegularExpressions.Regex.Replace(lines[i].Trim(), "<[^>]+>", ""));
                if (words.Count == 0) continue;
                l.Add(new Cue { Start = s, End = Math.Max(s + 0.2, e), Text = string.Join("\n", words.ToArray()) });
            }
            l.Sort((x, y) => x.Start.CompareTo(y.Start));
            return l;
        }

        static bool Time(string s, out double t)
        {
            t = 0;
            s = s.Trim().Split(' ')[0].Replace(',', '.');
            string[] p = s.Split(':');
            double h = 0, m = 0, sec;
            if (p.Length == 3) { if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out h) || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out m)) return false; }
            else if (p.Length == 2) { if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out m)) return false; }
            if (!double.TryParse(p[p.Length - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out sec)) return false;
            t = h * 3600 + m * 60 + sec;
            return true;
        }

        public static string ToSrt(List<Cue> cues)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < cues.Count; i++)
                b.Append(i + 1).Append("\r\n").Append(Stamp(cues[i].Start)).Append(" --> ").Append(Stamp(cues[i].End)).Append("\r\n").Append(cues[i].Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
            return b.ToString();
        }
        static string Stamp(double t) { TimeSpan s = TimeSpan.FromSeconds(Math.Max(0, t)); return string.Format("{0:00}:{1:00}:{2:00},{3:000}", (int)s.TotalHours, s.Minutes, s.Seconds, s.Milliseconds); }
    }
}
