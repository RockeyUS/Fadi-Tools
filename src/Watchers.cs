using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// A watched folder: when a file of the chosen kind lands there, it's converted (or compressed, or run through a
    /// recipe) automatically, and the result is saved in the same folder. The original stays as it is.
    public class WatchRule
    {
        public string Id, Folder = "", Types = "heic", Action = "convert:jpg";
        public bool On = true;
        public WatchRule Copy() { return new WatchRule { Id = Id, Folder = Folder, Types = Types, Action = Action, On = On }; }
    }

    public static class Watchers
    {
        /// The kinds of file a rule can watch for, and which kind of file they are.
        public static readonly string[] TypeKeys = { "heic", "image", "video", "audio", "pdf", "doc" };
        public static string KindOfType(string t) { return t == "heic" ? "image" : t; }

        static readonly object lk = new object();
        static readonly Dictionary<string, FileSystemWatcher> active = new Dictionary<string, FileSystemWatcher>();
        static readonly Queue<KeyValuePair<WatchRule, string>> queue = new Queue<KeyValuePair<WatchRule, string>>();
        static readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // queued or done
        static readonly HashSet<string> produced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // files we made
        static Thread worker;
        static Control owner;
        public static event Action Changed;   // a rule ran (the page shows "last: …")
        public static readonly Dictionary<string, string> LastDone = new Dictionary<string, string>();

        // ------------------------------------------------------------------ the rules (kept in the settings)
        // WatchIds=a1b2,c3d4   Watch.a1b2=1|C:\Users\…\Downloads|heic|convert:jpg

        public static List<WatchRule> All()
        {
            List<WatchRule> l = new List<WatchRule>();
            foreach (string id in Settings.Get("WatchIds", "").Split(','))
            {
                if (id.Trim().Length == 0) continue;
                string[] p = Settings.Get("Watch." + id.Trim(), "").Split('|');
                if (p.Length < 4) continue;
                l.Add(new WatchRule { Id = id.Trim(), On = p[0] == "1", Folder = p[1], Types = p[2], Action = p[3] });
            }
            return l;
        }

        public static void Save(WatchRule r)
        {
            if (r.Id == null) r.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            Settings.Set("Watch." + r.Id, (r.On ? "1" : "0") + "|" + r.Folder.Replace("|", "") + "|" + r.Types + "|" + r.Action);
            List<string> ids = new List<string>(Settings.Get("WatchIds", "").Split(','));
            ids.RemoveAll(x => x.Trim().Length == 0);
            if (!ids.Contains(r.Id)) { ids.Add(r.Id); Settings.Set("WatchIds", string.Join(",", ids.ToArray())); }
            Apply();
        }

        public static void Delete(string id)
        {
            List<string> ids = new List<string>(Settings.Get("WatchIds", "").Split(','));
            ids.RemoveAll(x => x.Trim().Length == 0 || x == id);
            Settings.Set("WatchIds", string.Join(",", ids.ToArray()));
            Settings.Set("Watch." + id, "");
            Apply();
        }

        public static int OnCount { get { int n = 0; foreach (WatchRule r in All()) if (r.On && Directory.Exists(r.Folder)) n++; return n; } }

        /// The rule's file types read as words: "HEIC photos", "pictures"…
        public static string TypeName(string t) { return L.T("wf_t_" + t); }

        /// What the rule does, as words: "Convert to JPG", "Compress to 50%", or the recipe's name.
        public static string ActionName(string a)
        {
            if (a.StartsWith("convert:")) return L.T("wf_a_convert", a.Substring(8).ToUpperInvariant());
            if (a.StartsWith("compress:")) return L.T("wf_a_compress", a.Substring(9));
            Recipe r = Recipes.Get(a);
            return r != null ? r.Name : "?";
        }

        public static bool Matches(string types, string path)
        {
            string e = Formats.Ext(path);
            if (types == "heic") return e == "heic" || e == "heif";
            if (types == "pdf") return e == "pdf";
            string k = WheelConfig.KindOf(e);
            return k != null && k == types;
        }

        // ------------------------------------------------------------------ watching
        /// When the app starts: watch every rule that's on.
        public static void Start(Control ui) { owner = ui; Apply(); }

        /// (Re)starts watching after the rules changed.
        public static void Apply()
        {
            lock (lk)
            {
                foreach (FileSystemWatcher w in active.Values) try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
                active.Clear();
                if (SelfTest.Active) return;
                foreach (WatchRule r in All())
                {
                    if (!r.On || !Directory.Exists(r.Folder)) continue;
                    try
                    {
                        WatchRule rule = r;
                        FileSystemWatcher w = new FileSystemWatcher(r.Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                        w.Created += (s, e) => Arrived(rule, e.FullPath);
                        w.Renamed += (s, e) => Arrived(rule, e.FullPath);   // browsers download as .crdownload / .part, then rename
                        w.Changed += (s, e) => Arrived(rule, e.FullPath);
                        w.EnableRaisingEvents = true;
                        active[r.Id] = w;
                    }
                    catch { }
                }
            }
        }

        static void Arrived(WatchRule r, string path)
        {
            try
            {
                if (!Matches(r.Types, path) || Directory.Exists(path)) return;
                string name = Path.GetFileName(path);
                if (name.StartsWith("~") || name.StartsWith(".")) return;
                lock (lk)
                {
                    if (produced.Contains(path) || seen.Contains(path)) return;
                    seen.Add(path);
                    queue.Enqueue(new KeyValuePair<WatchRule, string>(r, path));
                    if (worker == null || !worker.IsAlive) { worker = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal }; worker.Start(); }
                }
            }
            catch { }
        }

        static void Work()
        {
            while (true)
            {
                KeyValuePair<WatchRule, string> next;
                lock (lk)
                {
                    if (queue.Count == 0) { worker = null; return; }
                    next = queue.Dequeue();
                }
                try { Process(next.Key, next.Value); } catch { }
            }
        }

        /// Waits until the file is complete (its size stops changing and nothing is writing to it).
        static bool Settled(string path)
        {
            long last = -1; int still = 0;
            for (int i = 0; i < 600 && !Tools.ShuttingDown; i++)   // up to 5 minutes for a big download
            {
                Thread.Sleep(500);
                if (!File.Exists(path)) return false;
                long n; try { n = new FileInfo(path).Length; } catch { continue; }
                if (n == last && n > 0)
                {
                    if (++still >= 3)
                        try { using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return true; } catch { still = 0; }
                }
                else still = 0;
                last = n;
            }
            return false;
        }

        static void Process(WatchRule r, string path)
        {
            if (!Settled(path)) { lock (lk) seen.Remove(path); return; }
            string outDir = r.Folder;
            ActJob act = Activity.Begin(null, L.T("wf_act", Path.GetFileName(r.Folder.TrimEnd('\\', '/'))), Path.GetFileName(path), null);
            string err = null, outp = null;
            try
            {
                if (r.Action.StartsWith("convert:"))
                {
                    string target = r.Action.Substring(8);
                    Job j = Engine.Build(path, target, outDir, Quality.Best);
                    lock (lk) produced.Add(j.Output);
                    err = Engine.Run(j, p => Activity.Update(act, p, null), () => Tools.ShuttingDown);
                    outp = j.Output;
                }
                else if (r.Action.StartsWith("compress:"))
                {
                    int pct; int.TryParse(r.Action.Substring(9), out pct); pct = Math.Max(5, Math.Min(100, pct));
                    long b = new FileInfo(path).Length;
                    double bytes = b * pct / 100.0;
                    double mb = Formats.CategoryOf(Formats.Ext(path)) == Category.Image ? bytes / 1e6 : bytes / 1048576.0;
                    Job j = Compressor.Build(path, outDir, Compressor.Level.Balanced, mb);
                    lock (lk) produced.Add(j.Output);
                    err = Engine.Run(j, p => Activity.Update(act, p, null), () => Tools.ShuttingDown);
                    outp = j.Output;
                }
                else
                {
                    Recipe rc = Recipes.Get(r.Action);
                    if (rc == null) throw new UserError(L.T("wf_no_recipe"));
                    // the recipe's result lands in the folder too: it must not be picked up again
                    string tmp = Path.Combine(Path.GetTempPath(), "fadi_watch_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(tmp);
                    try
                    {
                        string made = Recipes.Apply(rc, path, tmp, p => Activity.Update(act, p, null), () => Tools.ShuttingDown);
                        outp = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(made), Formats.Ext(made));
                        lock (lk) produced.Add(outp);
                        File.Move(made, outp);
                    }
                    finally { try { Directory.Delete(tmp, true); } catch { } }
                }
            }
            catch (Exception ex) { err = ex.Message; }
            if (outp != null) lock (lk) produced.Add(outp);
            Activity.End(act, err, false, err == null ? L.T("wf_done", Path.GetFileName(outp)) : null, err == null ? outp : null);
            if (err == null)
            {
                lock (lk) LastDone[r.Id] = Path.GetFileName(path) + "  →  " + Path.GetFileName(outp);
                Control o = owner;
                if (o != null && Changed != null) try { o.BeginInvoke(Changed); } catch { }
            }
        }
    }
}
