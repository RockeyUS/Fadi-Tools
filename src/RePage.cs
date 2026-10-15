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
    /// Runs REA ("Reverse Engineer Anything", rea-agents on npm) through Node.js.
    public static class Rea
    {
        public const string Package = "rea-agents@6.4.0";
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        public static string NodeExe
        {
            get
            {
                string n = Tools.FindOnPath(IsWindows ? "node.exe" : "node");
                if (n != null) return n;
                if (IsWindows)
                    foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
                                                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs") })
                    {
                        string p = Path.Combine(root, "nodejs", "node.exe");
                        if (root.Length > 0 && File.Exists(p)) return p;
                    }
                return null;
            }
        }

        /// Node.js version, or null when Node.js is missing. ok = new enough for REA (22.19+, 24.11+, 26+).
        public static string NodeVersion(out bool ok)
        {
            ok = false;
            string node = NodeExe;
            if (node == null) return null;
            string output;
            try { if (Tools.RunHidden(node, "--version", 15000, out output) != 0) return null; } catch { return null; }
            string v = output.Trim().TrimStart('v');
            string[] p = v.Split('.');
            int major, minor;
            if (p.Length >= 2 && int.TryParse(p[0], out major) && int.TryParse(p[1], out minor))
                ok = (major == 22 && minor >= 19) || (major == 24 && minor >= 11) || major >= 26 || (major == 25);
            return "v" + v;
        }

        /// Runs one REA command; stdout goes to a file, stderr lines to the callback. Returns the exit code.
        public static int Run(string args, string outFile, Action<string> onErr, Func<bool> cancelled)
        {
            string node = NodeExe;
            if (node == null) throw new Exception(L.T("re_need_node"));
            string dir = Path.GetDirectoryName(node);
            Dictionary<string, string> env = new Dictionary<string, string>();
            env["PATH"] = dir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
            env["NO_COLOR"] = "1";
            string exe, a;
            if (IsWindows)
            {
                exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                a = "/d /s /c \"\"" + Path.Combine(dir, "npx.cmd") + "\" --yes " + Package + " " + args + " 1> \"" + outFile + "\"\"";
            }
            else
            {
                exe = "/bin/sh";
                a = "-c \"npx --yes " + Package + " " + args.Replace("\"", "\\\"") + " > '" + outFile + "'\"";
            }
            return Tools.Stream(exe, a, null, onErr, cancelled, env);
        }

        public static bool IsDotNet(string path)
        {
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] b = new byte[(int)Math.Min(4096, fs.Length)];
                    fs.Read(b, 0, b.Length);
                    if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return false;
                    int pe = BitConverter.ToInt32(b, 0x3C);
                    if (pe <= 0 || pe + 0x100 > b.Length || b[pe] != 'P' || b[pe + 1] != 'E') return false;
                    int opt = pe + 24;
                    bool plus = BitConverter.ToUInt16(b, opt) == 0x20B;
                    int dirs = opt + (plus ? 112 : 96);
                    return dirs + 8 * 15 <= b.Length && BitConverter.ToUInt32(b, dirs + 8 * 14) != 0;
                }
            }
            catch { return false; }
        }

        public static bool IsPe(string path)
        {
            try { using (FileStream fs = File.OpenRead(path)) return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z'; } catch { return false; }
        }

        // ------------------------------------------------------------------ readable summary of an Evidence JSON
        static readonly HashSet<string> Skip = new HashSet<string> { "evidence_id", "provider", "predicate_type", "parameters", "raw_result", "analysis_profile",
            "identity_scope", "location", "data_entry_location", "data_directory_location", "entry_locations", "row_offset", "manifest_id", "root_artifact_id",
            "inspection_id", "substep_id", "artifact_id", "evidence", "local_path", "fingerprint", "evidence_links", "directory", "directories", "rva", "index",
            "code_page", "reserved", "planes", "color_count", "machine", "candidate_resource_indices", "same_language_resource_index", "digest", "token" };
        static readonly string[] NameKeys = { "name", "full_name", "type_name", "namespace", "signature", "path", "module", "entry_point", "url", "method", "statement",
            "operation", "code", "detail", "rationale", "value", "summary" };
        static readonly System.Text.RegularExpressions.Regex IdLike = new System.Text.RegularExpressions.Regex("^[a-z]{1,6}_[0-9a-f]{16,}$");

        /// Windows resource type numbers → names.
        static string ResType(long id)
        {
            switch (id)
            {
                case 1: return "Cursor"; case 2: return "Bitmap"; case 3: return "Icon"; case 4: return "Menu"; case 5: return "Dialog";
                case 6: return "String table"; case 7: return "Font directory"; case 8: return "Font"; case 9: return "Accelerators";
                case 10: return "Raw data"; case 11: return "Message table"; case 12: return "Cursor group"; case 14: return "Icon group";
                case 16: return "Version info"; case 17: return "Dialog include"; case 21: return "Animated cursor"; case 22: return "Animated icon";
                case 23: return "HTML"; case 24: return "Manifest"; default: return "Type " + id;
            }
        }

        public static string Outline(object root)
        {
            StringBuilder sb = new StringBuilder();
            JsonObject o = root as JsonObject;
            if (o != null)
            {
                object subject = Get(o, "subject"), norm = Get(o, "normalized_result");
                if (subject is JsonObject)
                {
                    sb.AppendLine(L.T("re_file") + ": " + Str(Get((JsonObject)subject, "name")));
                    string fmt = Str(Get((JsonObject)subject, "format")), arch = Str(Get((JsonObject)subject, "architecture"));
                    if (fmt.Length > 0) sb.AppendLine(L.T("re_format") + ": " + fmt.ToUpperInvariant() + (arch.Length > 0 ? "  ·  " + arch : ""));
                    sb.AppendLine();
                }
                if (norm != null) { Write(sb, norm, 0, null); return sb.ToString(); }
            }
            Write(sb, root, 0, null);
            return sb.ToString();
        }

        static object Get(JsonObject o, string k) { foreach (KeyValuePair<string, object> kv in o) if (kv.Key == k) return kv.Value; return null; }
        static bool IsNum(object v) { return v is double || v is long || v is int; }
        static long Num(object v) { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
        static bool IsZero(object v) { return IsNum(v) && Convert.ToDouble(v, CultureInfo.InvariantCulture) == 0; }

        static string Str(object v)
        {
            if (v == null) return "";
            if (v is bool) return (bool)v ? L.T("rd_yes") : L.T("rd_no");
            if (v is double) return ((double)v).ToString("0.###", CultureInfo.InvariantCulture);
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Replace("\r", " ").Replace("\n", " ").Trim();
            if (s == "null") return "";
            if (IdLike.IsMatch(s)) return "";                       // internal ids like aih_1653… mean nothing to a person
            if (s.Length >= 40 && IsHex(s)) return s.Substring(0, 16) + "…";
            return s.Length > 300 ? s.Substring(0, 297) + "…" : s;
        }
        static bool IsHex(string s) { foreach (char c in s) if (!Uri.IsHexDigit(c)) return false; return true; }
        static string Nice(string key) { string k = key.Replace('_', ' '); return k.Length > 0 ? char.ToUpperInvariant(k[0]) + k.Substring(1) : k; }
        static bool Hidden(string key) { return Skip.Contains(key) || key.EndsWith("_location") || key.EndsWith("_offset") || key.EndsWith("_rva") || key.EndsWith("_id") && key != "resource_id"; }

        /// {"kind":"id","id":3} / {"kind":"name","name":"X"} → "3" / "X"; null when the object isn't such a reference.
        static string Ref(JsonObject o, string key)
        {
            object kind = Get(o, "kind");
            if (!(kind is string) || o.Count > 3) return null;
            object id = Get(o, "id"), name = Get(o, "name"), val = Get(o, "value");
            if (IsNum(id)) return key == "type" ? ResType(Num(id)) : Str(id);
            if (name is string) return (string)name;
            if (val != null && !(val is JsonObject)) return Str(val);
            return null;
        }

        /// Size of a block that carries {"location":{"bytes":N}}.
        static string SizeOf(JsonObject o)
        {
            JsonObject loc = Get(o, "location") as JsonObject;
            object b = loc != null ? Get(loc, "bytes") : null;
            return IsNum(b) ? Theme.HumanSize(Num(b)) : null;
        }

        static void Write(StringBuilder sb, object v, int depth, string parentKey)
        {
            string pad = new string(' ', depth * 3);
            JsonObject o = v as JsonObject;
            if (o == null) { string s = Str(v); if (s.Length > 0) sb.AppendLine(pad + s); return; }
            foreach (KeyValuePair<string, object> kv in o)
            {
                if (Hidden(kv.Key)) continue;
                object x = kv.Value;
                if (x == null) continue;
                if (x is JsonObject)
                {
                    JsonObject xo = (JsonObject)x;
                    string r = Ref(xo, kv.Key);
                    if (r != null) { sb.AppendLine(pad + Nice(kv.Key) + ": " + r); continue; }
                    if (xo.Count == 0 || depth >= 4) continue;
                    int before = sb.Length;
                    sb.AppendLine(pad + "▸ " + Nice(kv.Key));
                    int mark = sb.Length;
                    Write(sb, x, depth + 1, kv.Key);
                    if (sb.Length == mark) sb.Length = before;          // nothing worth showing inside
                }
                else if (x is List<object>)
                {
                    List<object> l = (List<object>)x;
                    if (l.Count == 0) continue;
                    if (!(l[0] is JsonObject))
                    {
                        List<string> parts = new List<string>();
                        bool longItems = false;
                        foreach (object i in l) { string s = Str(i); if (s.Length > 0) { parts.Add(s); if (s.Length > 50) longItems = true; } }
                        if (parts.Count == 0) continue;
                        if (longItems)
                        {
                            sb.AppendLine(pad + "▸ " + Nice(kv.Key) + " (" + parts.Count + ")");
                            foreach (string s in parts) sb.AppendLine(pad + "   • " + s);
                        }
                        else
                            sb.AppendLine(pad + Nice(kv.Key) + " (" + parts.Count + "): " + string.Join(", ", parts.GetRange(0, Math.Min(14, parts.Count)).ToArray()) + (parts.Count > 14 ? ", …" : ""));
                    }
                    else
                    {
                        // identical items are shown once with a count
                        List<string> lines = new List<string>(); Dictionary<string, int> count = new Dictionary<string, int>();
                        foreach (object i in l)
                        {
                            string s = OneLine(i as JsonObject, kv.Key);
                            if (s.Length == 0) continue;
                            if (count.ContainsKey(s)) count[s]++; else { count[s] = 1; lines.Add(s); }
                        }
                        if (lines.Count == 0) continue;
                        sb.AppendLine(pad + "▸ " + Nice(kv.Key) + " (" + l.Count + ")");
                        int n = Math.Min(depth == 0 ? 80 : 30, lines.Count);
                        for (int i = 0; i < n; i++) sb.AppendLine(pad + "   • " + lines[i] + (count[lines[i]] > 1 ? "   ×" + count[lines[i]] : ""));
                        if (lines.Count > n) sb.AppendLine(pad + "   … " + L.T("re_more", lines.Count - n));
                    }
                }
                else
                {
                    string s = Str(x);
                    if (s.Length > 0) sb.AppendLine(pad + Nice(kv.Key) + ": " + s);
                }
            }
        }

        /// One list item as a single line: its name first, then the fields that tell it apart.
        static string OneLine(JsonObject o, string listKey)
        {
            if (o == null) return "";
            string r = Ref(o, listKey);
            if (r != null) return r;
            List<string> parts = new List<string>();
            HashSet<string> used = new HashSet<string>();
            object w = Get(o, "width"), h = Get(o, "height");
            if (IsNum(w) && IsNum(h))
            {
                parts.Add((IsZero(w) ? "256" : Str(w)) + "×" + (IsZero(h) ? "256" : Str(h)));
                used.Add("width"); used.Add("height");
                object bits = Get(o, "bit_count");
                if (IsNum(bits) && !IsZero(bits)) { parts.Add(Str(bits) + "-bit"); used.Add("bit_count"); }
            }
            JsonObject tObj = Get(o, "type") as JsonObject, nObj = Get(o, "name") as JsonObject;
            string tRef = tObj != null ? Ref(tObj, "type") : null, nRef = nObj != null ? Ref(nObj, "name") : null;
            if (tRef != null && nRef != null) { parts.Add(tRef); parts.Add("#" + nRef); used.Add("type"); used.Add("name"); }   // a Windows resource
            foreach (string k in NameKeys)
            {
                if (used.Contains(k)) continue;
                object v = Get(o, k);
                if (v == null || v is List<object>) continue;
                string s = v is JsonObject ? (Ref((JsonObject)v, k) ?? OneLine((JsonObject)v, k)) : Str(v);
                if (s.Length == 0) continue;
                parts.Add(s); used.Add(k);
                if (parts.Count >= 2) break;
            }
            foreach (KeyValuePair<string, object> kv in o)
            {
                if (parts.Count >= 6) break;
                if (used.Contains(kv.Key) || Hidden(kv.Key) || kv.Value == null) continue;
                object v = kv.Value; string s;
                if (v is JsonObject)
                {
                    JsonObject vo = (JsonObject)v;
                    string rr = Ref(vo, kv.Key);
                    if (rr != null) { if (rr == "0") continue; s = kv.Key == "type" ? rr : Nice(kv.Key) + " " + rr; }
                    else { string size = SizeOf(vo); if (size == null) continue; s = kv.Key == "payload" ? size : Nice(kv.Key) + " " + size; }
                }
                else if (v is List<object>)
                {
                    List<object> lv = (List<object>)v; if (lv.Count == 0) continue;
                    JsonObject first = lv[0] as JsonObject;
                    if (first != null && Get(first, "width") != null)
                    {
                        List<string> sizes = new List<string>();
                        foreach (object i in lv) { string one = OneLine(i as JsonObject, kv.Key).Split(new[] { "  ·  " }, StringSplitOptions.None)[0]; if (!sizes.Contains(one)) sizes.Add(one); }
                        s = Nice(kv.Key) + " (" + lv.Count + "): " + string.Join(", ", sizes.ToArray());
                    }
                    else s = Nice(kv.Key) + " (" + lv.Count + ")";
                }
                else
                {
                    if (IsZero(v)) continue;
                    string sv = Str(v); if (sv.Length == 0) continue;
                    s = Nice(kv.Key) + " " + sv;
                }
                parts.Add(s);
            }
            string line = string.Join("  ·  ", parts.ToArray());
            return line.Length > 400 ? line.Substring(0, 397) + "…" : line;
        }
    }

    /// Reads the icons and version details stored inside a Windows program (.exe / .dll).
    public static class PeInfo
    {
        /// The program's main icon as a complete .ico file, or null.
        public static byte[] MainIcon(string path)
        {
            byte[] b;
            try { FileInfo fi = new FileInfo(path); if (fi.Length > 400L * 1024 * 1024) return null; b = File.ReadAllBytes(path); } catch { return null; }
            try
            {
                if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return null;
                int pe = BitConverter.ToInt32(b, 0x3C);
                if (pe <= 0 || pe + 24 > b.Length || b[pe] != 'P' || b[pe + 1] != 'E') return null;
                int nsec = BitConverter.ToUInt16(b, pe + 6), optSize = BitConverter.ToUInt16(b, pe + 20), opt = pe + 24;
                bool plus = BitConverter.ToUInt16(b, opt) == 0x20B;
                int dd = opt + (plus ? 112 : 96);
                uint resRva = BitConverter.ToUInt32(b, dd + 16);
                if (resRva == 0) return null;
                int secs = opt + optSize;
                Func<uint, int> off = rva =>
                {
                    for (int i = 0; i < nsec; i++)
                    {
                        int sh = secs + i * 40;
                        uint va = BitConverter.ToUInt32(b, sh + 12), vs = Math.Max(BitConverter.ToUInt32(b, sh + 8), BitConverter.ToUInt32(b, sh + 16)), raw = BitConverter.ToUInt32(b, sh + 20);
                        if (rva >= va && rva < va + vs) return (int)(rva - va + raw);
                    }
                    return -1;
                };
                int root = off(resRva);
                if (root < 0) return null;
                Dictionary<uint, byte[]> icons = new Dictionary<uint, byte[]>();
                List<byte[]> groups = new List<byte[]>();
                foreach (KeyValuePair<uint, int> type in Entries(b, root, root))
                {
                    if (type.Key != 3 && type.Key != 14) continue;
                    foreach (KeyValuePair<uint, int> name in Entries(b, root, type.Value))
                    {
                        foreach (KeyValuePair<uint, int> lang in Entries(b, root, name.Value))
                        {
                            int de = lang.Value;   // data entry: RVA, size
                            uint rva = BitConverter.ToUInt32(b, de), size = BitConverter.ToUInt32(b, de + 4);
                            int o = off(rva);
                            if (o < 0 || o + size > b.Length) continue;
                            byte[] data = new byte[size];
                            Buffer.BlockCopy(b, o, data, 0, (int)size);
                            if (type.Key == 3) { if (!icons.ContainsKey(name.Key)) icons[name.Key] = data; }
                            else groups.Add(data);
                            break;   // first language is enough
                        }
                    }
                }
                foreach (byte[] g in groups)
                {
                    byte[] ico = BuildIco(g, icons);
                    if (ico != null) return ico;
                }
            }
            catch { }
            return null;
        }

        /// Child entries of a resource directory: (id, offset of the child directory or data entry).
        static List<KeyValuePair<uint, int>> Entries(byte[] b, int root, int dir)
        {
            List<KeyValuePair<uint, int>> l = new List<KeyValuePair<uint, int>>();
            int n = BitConverter.ToUInt16(b, dir + 12) + BitConverter.ToUInt16(b, dir + 14);
            for (int i = 0; i < n && i < 4096; i++)
            {
                int e = dir + 16 + i * 8;
                uint id = BitConverter.ToUInt32(b, e), to = BitConverter.ToUInt32(b, e + 4);
                l.Add(new KeyValuePair<uint, int>((id & 0x80000000) != 0 ? 0xFFFFFFFF : id, root + (int)(to & 0x7FFFFFFF)));
            }
            return l;
        }

        /// GRPICONDIR + the RT_ICON images → a normal .ico file.
        static byte[] BuildIco(byte[] grp, Dictionary<uint, byte[]> icons)
        {
            int count = BitConverter.ToUInt16(grp, 4);
            List<byte[]> imgs = new List<byte[]>(); List<int> entries = new List<int>();
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 14;
                if (e + 14 > grp.Length) break;
                uint id = BitConverter.ToUInt16(grp, e + 12);
                byte[] im;
                if (!icons.TryGetValue(id, out im)) continue;
                imgs.Add(im); entries.Add(e);
            }
            if (imgs.Count == 0) return null;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)imgs.Count);
                int offset = 6 + 16 * imgs.Count;
                for (int i = 0; i < imgs.Count; i++)
                {
                    int e = entries[i];
                    w.Write(grp, e, 8);                       // width, height, colors, reserved, planes, bit count
                    w.Write((uint)imgs[i].Length);
                    w.Write((uint)offset);
                    offset += imgs[i].Length;
                }
                foreach (byte[] im in imgs) w.Write(im);
                return ms.ToArray();
            }
        }

        /// The biggest picture in an .ico file, as a bitmap.
        public static Bitmap Largest(byte[] ico)
        {
            if (ico == null) return null;
            try
            {
                int count = BitConverter.ToUInt16(ico, 4), best = -1, bestSize = -1;
                for (int i = 0; i < count; i++)
                {
                    int e = 6 + i * 16;
                    int w = ico[e] == 0 ? 256 : ico[e];
                    int bits = BitConverter.ToUInt16(ico, e + 6);
                    int score = w * 100 + bits;
                    if (score > bestSize) { bestSize = score; best = i; }
                }
                int be = 6 + best * 16;
                int len = BitConverter.ToInt32(ico, be + 8), at = BitConverter.ToInt32(ico, be + 12);
                if (len > 8 && ico[at] == 0x89 && ico[at + 1] == 'P' && ico[at + 2] == 'N' && ico[at + 3] == 'G')
                    using (MemoryStream ms = new MemoryStream(ico, at, len)) using (Image im = Image.FromStream(ms)) return new Bitmap(im);
                int size = ico[be] == 0 ? 256 : ico[be];
                using (MemoryStream ms = new MemoryStream(ico))
                using (Icon icon = new Icon(ms, size, size)) return icon.ToBitmap();
            }
            catch { return null; }
        }

        /// Publisher, product, version… from the program's version block.
        public static List<KeyValuePair<string, string>> Version(string path)
        {
            List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
                Action<string, string> add = (k, val) => { if (!string.IsNullOrEmpty(val) && val.Trim().Length > 0 && val.Trim() != "0.0.0.0") l.Add(new KeyValuePair<string, string>(L.T(k), val.Trim())); };
                add("re_v_product", v.ProductName);
                add("re_v_company", v.CompanyName);
                add("re_v_desc", v.FileDescription);
                add("re_v_version", v.FileVersion);
                if (v.ProductVersion != v.FileVersion) add("re_v_pversion", v.ProductVersion);
                add("re_v_copyright", v.LegalCopyright);
                add("re_v_original", v.OriginalFilename);
            }
            catch { }
            return l;
        }
    }

    /// "Reverse engineer": look inside programs and apps with REA.
    public class RePage : Page
    {
        class Analysis { public string Key, Command; public Func<string, bool> Fits; }
        static readonly Analysis[] All =
        {
            new Analysis { Key = "re_a_overview", Command = "inspect-artifact", Fits = p => File.Exists(p) },
            new Analysis { Key = "re_a_net", Command = "inspect-managed-artifact", Fits = p => File.Exists(p) && Rea.IsDotNet(p) },
            new Analysis { Key = "re_a_members", Command = "inspect-managed-members", Fits = p => File.Exists(p) && Rea.IsDotNet(p) },
            new Analysis { Key = "re_a_native", Command = "inspect-managed-native-boundaries", Fits = p => File.Exists(p) && Rea.IsDotNet(p) },
            new Analysis { Key = "re_a_resources", Command = "inspect-pe-resources", Fits = p => File.Exists(p) && Rea.IsPe(p) },
            new Analysis { Key = "re_a_js", Command = "analyze-javascript-application", Fits = p => Directory.Exists(p) || p.EndsWith(".asar", StringComparison.OrdinalIgnoreCase) },
            new Analysis { Key = "re_a_har", Command = "inspect-web-network-capture", Fits = p => p.EndsWith(".har", StringComparison.OrdinalIgnoreCase) },
            new Analysis { Key = "re_a_apk", Command = "inspect-android-package", Fits = p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) },
        };

        Rectangle top, results, engineR, whatLbl, statusR, emptyR;
        readonly TextField path = new TextField(() => L.T("re_path_hint"), Ico.Search) { ForceLtr = true };
        readonly Btn bFile, bFolder, bRun, bSetup, bNode, bSave, bCopy, bIcon;
        byte[] iconBytes; Bitmap iconImg; string iconFor;
        List<KeyValuePair<string, string>> versionInfo = new List<KeyValuePair<string, string>>();
        Rectangle iconR;
        readonly ChipGrid what = new ChipGrid { Columns = 3, RowH = 34 };
        readonly Segments view = new Segments(() => L.T("re_summary"), () => L.T("re_raw"));
        readonly TextBox output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None };
        readonly Progress busy = new Progress { Visible = false };
        List<Analysis> fits = new List<Analysis>();
        string nodeVer; bool nodeOk, checkedNode, running, cancel;
        string status = ""; Color statusColor;
        string lastJson, lastSummary, lastName;

        public RePage() : base("re_title", "re_sub")
        {
            bFile = new Btn(() => L.T("re_pick_file"), Ico.Open, BtnKind.Secondary);
            bFile.Click += (s, e) => PickFile();
            bFolder = new Btn(() => L.T("re_pick_folder"), Ico.FolderOpen, BtnKind.Secondary);
            bFolder.Click += (s, e) => { string d = FolderPicker.Pick(FindForm(), L.T("re_pick_folder"), null); if (d != null) SetTarget(d); };
            bRun = new Btn(() => running ? L.T("cancel") : L.T("re_run"), Ico.Play, BtnKind.Primary);
            bRun.Click += (s, e) => { if (running) cancel = true; else Run(); };
            bSetup = new Btn(() => L.T("re_setup_agents"), Ico.Settings, BtnKind.Ghost);
            bSetup.Click += (s, e) => SetupAgents();
            bNode = new Btn(() => L.T("re_install_node"), Ico.Download, BtnKind.Primary) { Visible = false };
            bNode.Click += (s, e) => InstallNode();
            bSave = new Btn(() => L.T("re_save"), Ico.Save, BtnKind.Secondary) { Visible = false };
            bSave.Click += (s, e) => SaveReport();
            bCopy = new Btn(() => L.T("re_copy"), Ico.Copy, BtnKind.Ghost) { Visible = false };
            bCopy.Click += (s, e) => { try { Clipboard.SetText(output.Text); ShowToast(L.T("re_copied"), true, null, null); } catch { } };
            bIcon = new Btn(() => L.T("re_save_icon"), Ico.Photo, BtnKind.Secondary) { Visible = false };
            bIcon.Click += (s, e) => SaveIcon();
            path.Box.TextChanged += (s, e) => RefreshChoices();
            view.SelectedChanged += (s, e) => ShowOutput();
            view.Visible = false;
            output.Font = Theme.Mono(9.5f);
            output.Visible = false;
            Controls.AddRange(new Control[] { path, bFile, bFolder, what, bRun, bSetup, bNode, busy, view, bSave, bCopy, output, bIcon });
            EnableDrop(path, what);
            RefreshChoices();
            Restyle();
        }

        public override bool Busy { get { return running; } }
        public override void OnShown() { if (!checkedNode) CheckNode(); }

        void CheckNode()
        {
            checkedNode = true;
            Thread t = new Thread(() =>
            {
                bool ok; string v = Rea.NodeVersion(out ok);
                Ui(() => { nodeVer = v; nodeOk = ok; bNode.Visible = !ok; DoLayout(); });
            });
            t.IsBackground = true; t.Start();
        }

        void Ui(Action a) { Post(a); }

        public override void AcceptFiles(string[] paths) { if (paths != null && paths.Length > 0) SetTarget(paths[0]); }

        void SetTarget(string p) { path.Box.Text = p; RefreshChoices(); }

        void PickFile()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("re_pick_file");
                d.Filter = L.T("re_filter") + "|*.exe;*.dll;*.asar;*.har;*.apk;*.sys;*.ocx|" + L.T("all_files") + "|*.*";
                if (d.ShowDialog(FindForm()) == DialogResult.OK) SetTarget(d.FileName);
            }
        }

        void RefreshChoices()
        {
            string p = path.Box.Text.Trim().Trim('"');
            fits = new List<Analysis>();
            if (p.Length > 0) foreach (Analysis a in All) { bool ok = false; try { ok = a.Fits(p); } catch { } if (ok) fits.Add(a); }
            List<string> names = new List<string>();
            foreach (Analysis a in fits) names.Add(L.T(a.Key));
            string keep = what.Selected;
            what.SetItems(names, names.Contains(keep) ? keep : (names.Count > 1 && Rea.IsDotNet(p) ? names[1] : (names.Count > 0 ? names[0] : null)));
            what.Visible = names.Count > 0;
            bRun.Enabled = names.Count > 0;
            LoadPeDetails(p);
            DoLayout();
        }

        void LoadPeDetails(string p)
        {
            if (p == iconFor) return;
            iconFor = p;
            if (iconImg != null) { iconImg.Dispose(); iconImg = null; }
            iconBytes = null; versionInfo = new List<KeyValuePair<string, string>>();
            bIcon.Visible = false;
            if (!File.Exists(p) || !Rea.IsPe(p)) return;
            Thread t = new Thread(() =>
            {
                byte[] ico = PeInfo.MainIcon(p);
                Bitmap img = PeInfo.Largest(ico);
                List<KeyValuePair<string, string>> ver = PeInfo.Version(p);
                Ui(() =>
                {
                    if (iconFor != p) { if (img != null) img.Dispose(); return; }
                    iconBytes = ico; iconImg = img; versionInfo = ver;
                    bIcon.Visible = ico != null;
                    DoLayout();
                });
            });
            t.IsBackground = true; t.Start();
        }

        void SaveIcon()
        {
            if (iconBytes == null) return;
            try
            {
                string name = Path.GetFileNameWithoutExtension(iconFor) + " " + L.T("re_icon_suffix");
                string ip = SaveAsk.File(FindForm(), name + ".ico", "ico");
                if (ip == null) return;
                File.WriteAllBytes(ip, iconBytes);
                if (iconImg != null) iconImg.Save(Path.ChangeExtension(ip, ".png"), System.Drawing.Imaging.ImageFormat.Png);
                ShowToast(L.T("re_icon_saved", Path.GetFileName(ip)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + ip + "\""); } catch { } });
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        /// The file's version block at the top of the summary.
        string VersionText()
        {
            if (versionInfo.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("▸ " + L.T("re_details"));
            foreach (KeyValuePair<string, string> kv in versionInfo) sb.AppendLine("   " + kv.Key + ": " + kv.Value);
            sb.AppendLine();
            return sb.ToString();
        }

        public override void Restyle()
        {
            path.Restyle();
            output.BackColor = Theme.Field; output.ForeColor = Theme.Ink;
            Theme.ApplyNativeTheme(output);
        }

        public override void DoLayout()
        {
            int W = Width, H = Height, p = Pad, ip = Theme.S(20);
            cards.Clear();
            int rows = Math.Max(1, (fits.Count + 2) / 3);
            int whatH = fits.Count > 0 ? what.NeededHeight(fits.Count) : 0;
            top = new Rectangle(p, Top0, W - p * 2, ip * 2 + Theme.S(44) + Theme.S(14) + Theme.S(24) + whatH + Theme.S(16) + Theme.S(42) + Theme.S(4));
            results = new Rectangle(p, top.Bottom + Theme.S(18), W - p * 2, Math.Max(Theme.S(140), H - top.Bottom - Theme.S(18) - p));
            cards.Add(top); cards.Add(results);
            int x = top.X + ip, w = top.Width - ip * 2, y = top.Y + ip;
            int fw = Math.Max(Theme.S(120), bFile.PreferredWidth), dw = Math.Max(Theme.S(120), bFolder.PreferredWidth);
            Place(path, new Rectangle(x, y, w - fw - dw - Theme.S(20), Theme.S(44)));
            Place(bFile, new Rectangle(x + w - fw - dw - Theme.S(10), y, fw, Theme.S(44)));
            Place(bFolder, new Rectangle(x + w - dw, y, dw, Theme.S(44)));
            y += Theme.S(58);
            whatLbl = new Rectangle(x, y, w, Theme.S(20)); y += Theme.S(24);
            if (fits.Count > 0) { Place(what, new Rectangle(x, y, w, whatH)); y += whatH + Theme.S(16); }
            int rw = Math.Max(Theme.S(170), bRun.PreferredWidth);
            Place(bRun, new Rectangle(x + w - rw, y, rw, Theme.S(42)));
            int sw = Math.Max(Theme.S(200), bSetup.PreferredWidth);
            Place(bSetup, new Rectangle(x + w - rw - Theme.S(12) - sw, y, sw, Theme.S(42)));
            int nw = Math.Max(Theme.S(160), bNode.PreferredWidth);
            Place(bNode, new Rectangle(x, y, nw, Theme.S(42)));
            int lead = bNode.Visible ? nw + Theme.S(12) : 0;
            iconR = Rectangle.Empty;
            if (iconImg != null && !bNode.Visible)
            {
                iconR = new Rectangle(x, y + Theme.S(1), Theme.S(40), Theme.S(40));
                lead = Theme.S(52);
            }
            int iw = bIcon.Visible ? Math.Max(Theme.S(130), bIcon.PreferredWidth) : 0;
            if (bIcon.Visible) Place(bIcon, new Rectangle(x + w - rw - sw - Theme.S(24) - iw, y, iw, Theme.S(42)));
            engineR = new Rectangle(x + lead, y, w - rw - sw - Theme.S(24) - lead - (iw > 0 ? iw + Theme.S(12) : 0), Theme.S(42));
            Place(busy, new Rectangle(top.X + ip, top.Bottom - Theme.S(8), top.Width - ip * 2, Theme.S(4)));

            int rx = results.X + ip, rwid = results.Width - ip * 2, ry = results.Y + Theme.S(14);
            Place(view, new Rectangle(rx, ry, Theme.S(240), Theme.S(34)));
            int cw = Math.Max(Theme.S(110), bCopy.PreferredWidth), sv = Math.Max(Theme.S(140), bSave.PreferredWidth);
            Place(bSave, new Rectangle(rx + rwid - sv, ry, sv, Theme.S(34)));
            Place(bCopy, new Rectangle(rx + rwid - sv - Theme.S(8) - cw, ry, cw, Theme.S(34)));
            statusR = new Rectangle(rx + Theme.S(256), ry, rwid - Theme.S(256) - sv - cw - Theme.S(20), Theme.S(34));
            Rectangle outR = new Rectangle(rx, ry + Theme.S(46), rwid, results.Bottom - ry - Theme.S(46) - Theme.S(16));
            output.Bounds = M(outR);
            emptyR = new Rectangle(results.X, results.Y + Theme.S(50), results.Width, results.Height - Theme.S(60));
            Invalidate();
        }

        protected override void PaintContent(Graphics g)
        {
            Label(g, L.T(fits.Count > 0 ? "re_what" : "re_choose_first"), Theme.F(9f, FontStyle.Bold), Theme.InkSoft, whatLbl);
            if (!iconR.IsEmpty && iconImg != null)
            {
                Rectangle ir = M(iconR);
                Theme.FillRound(g, ir, Theme.S(10), Theme.CardInner);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(iconImg, Rectangle.Inflate(ir, -Theme.S(4), -Theme.S(4)));
            }
            string eng = !checkedNode || (nodeVer == null && !checkedNode) ? L.T("re_checking") :
                         nodeVer == null ? L.T("re_node_missing") : nodeOk ? L.T("re_ready", nodeVer, Rea.Package.Replace("rea-agents@", "")) : L.T("re_node_old", nodeVer);
            Label(g, eng, Theme.F(8.75f), nodeOk ? Theme.InkSoft : Theme.Warn, engineR, Align.Start, true, true);
            if (!string.IsNullOrEmpty(status)) Label(g, status, Theme.F(9f), statusColor, statusR, Align.Start, true, true);
            if (!output.Visible)
            {
                Rectangle c = new Rectangle(emptyR.X + emptyR.Width / 2 - Theme.S(30), emptyR.Y + emptyR.Height / 2 - Theme.S(60), Theme.S(60), Theme.S(60));
                Theme.FillRound(g, M(c), Theme.S(30), Theme.Selected);
                Theme.Glyph(g, Ico.Code, 18f, Theme.InkFaint, M(c));
                Label(g, L.T("re_empty"), Theme.F(9.5f), Theme.InkFaint, new Rectangle(emptyR.X + Theme.S(40), c.Bottom + Theme.S(12), emptyR.Width - Theme.S(80), Theme.S(60)), Align.Center, false, true);
            }
        }

        void SetStatus(string s, Color c) { status = s; statusColor = c; Invalidate(M(statusR)); }

        void Run()
        {
            string target = path.Box.Text.Trim().Trim('"');
            int idx = Math.Max(0, (what.Selected != null ? fits.FindIndex(x => L.T(x.Key) == what.Selected) : 0));
            if (fits.Count == 0 || (!File.Exists(target) && !Directory.Exists(target))) { SetStatus(L.T("re_choose_first"), Theme.Warn); return; }
            if (Rea.NodeExe == null || !nodeOk) { SetStatus(nodeVer == null ? L.T("re_node_missing") : L.T("re_node_old", nodeVer), Theme.Warn); return; }
            Analysis a = fits[idx];
            running = true; cancel = false;
            bRun.Kind = BtnKind.Secondary; bRun.Glyph = Ico.Stop; bRun.Invalidate();
            busy.Visible = true; busy.SetIndeterminate(true);
            SetStatus(L.T("re_running"), Theme.InkSoft);
            string outFile = Path.Combine(Path.GetTempPath(), "fadi_rea_" + Guid.NewGuid().ToString("N") + ".json");
            string vtext = VersionText();
            DateTime started = DateTime.Now;
            ActJob act = Activity.Begin(this, L.T("act_k_analyse"), Path.GetFileName(target.TrimEnd('\\', '/')), () => cancel = true);
            Thread t = new Thread(() =>
            {
                string err = null, json = null, summary = null; StringBuilder errText = new StringBuilder();
                try
                {
                    int code = Rea.Run(a.Command + " \"" + target + "\" --format json", outFile,
                        line => { lock (errText) if (errText.Length < 20000) errText.AppendLine(line); Ui(() => SetStatus(L.T("re_running_t", (int)(DateTime.Now - started).TotalSeconds), Theme.InkSoft)); },
                        () => cancel || IsDisposed);
                    if (code == -2) err = L.T("cancelled");
                    if (File.Exists(outFile))
                    {
                        FileInfo fi = new FileInfo(outFile);
                        if (fi.Length > 60L * 1024 * 1024) err = L.T("re_too_big", Theme.HumanSize(fi.Length));
                        else json = File.ReadAllText(outFile, Encoding.UTF8);
                    }
                    if (err == null && string.IsNullOrWhiteSpace(json)) err = Tools.LastLines(errText.ToString(), 3);
                    if (err == null)
                    {
                        object root = null;
                        try { root = new JsonParser(json).Parse(); } catch { }
                        summary = (vtext + (root != null ? Rea.Outline(root) : json));
                        if (code != 0) err = L.T("re_partial");
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try { File.Delete(outFile); } catch { }
                Ui(() =>
                {
                    running = false;
                    bRun.Kind = BtnKind.Primary; bRun.Glyph = Ico.Play; bRun.Invalidate();
                    busy.SetIndeterminate(false); busy.Visible = false;
                    if (json != null && summary != null)
                    {
                        lastJson = json; lastSummary = summary;
                        lastName = Path.GetFileName(target.TrimEnd('\\', '/')) + " - " + a.Command;
                        view.Visible = bSave.Visible = bCopy.Visible = output.Visible = true;
                        ShowOutput();
                    }
                    SetStatus(err ?? L.T("re_done", (int)(DateTime.Now - started).TotalSeconds), err == null ? Theme.Success : (summary != null ? Theme.Warn : Theme.Error));
                    if (summary != null) Activity.Done(act, err ?? L.T("re_done", (int)(DateTime.Now - started).TotalSeconds), null);
                    else Activity.End(act, err ?? "?", cancel, null, null);
                    DoLayout();
                });
            });
            t.IsBackground = true; t.Start();
        }

        void ShowOutput()
        {
            if (lastJson == null) return;
            bool sum = view.SelectedIndex == 0;
            string text = sum ? lastSummary : PrettyJson(lastJson);
            output.WordWrap = sum;
            output.ScrollBars = sum ? ScrollBars.Vertical : ScrollBars.Both;
            output.Font = sum ? Theme.F(9.75f) : Theme.Mono(9.5f);
            output.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            output.SelectionStart = 0; output.ScrollToCaret();
        }

        static string PrettyJson(string json)
        {
            if (json.Length > 3000000) return json;
            try { return Readable.PrettyJson(json); } catch { return json; }
        }

        void SaveReport()
        {
            if (lastJson == null) return;
            try
            {
                string safe = lastName;
                foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
                string jp = SaveAsk.File(FindForm(), safe + " (REA).json", "json");
                if (jp == null) return;
                File.WriteAllText(jp, lastJson, Encoding.UTF8);
                string tp = Path.ChangeExtension(jp, ".txt");
                File.WriteAllText(tp, lastSummary, Encoding.UTF8);
                ShowToast(L.T("re_saved", Path.GetFileName(jp)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + jp + "\""); } catch { } });
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        /// Opens a console that runs REA's own setup, which connects it to AI agents (Claude Code, Cursor, Codex…).
        void SetupAgents()
        {
            string node = Rea.NodeExe;
            if (node == null) { SetStatus(L.T("re_node_missing"), Theme.Warn); return; }
            try
            {
                string dir = Path.GetDirectoryName(node);
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/k \"set \"PATH=" + dir + ";%PATH%\" && \"" + Path.Combine(dir, "npx.cmd") + "\" --yes " + Rea.Package + " setup\"");
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        void InstallNode()
        {
            string winget = Tools.WinGet;
            if (winget == null) { try { Process.Start("https://nodejs.org/en/download"); } catch { } return; }
            bNode.Enabled = false;
            SetStatus(L.T("re_installing_node"), Theme.InkSoft);
            busy.Visible = true; busy.SetIndeterminate(true);
            Thread t = new Thread(() =>
            {
                string output2;
                try { Tools.RunHidden(winget, "install -e --id OpenJS.NodeJS.LTS --accept-package-agreements --accept-source-agreements --silent", 20 * 60 * 1000, out output2); } catch { }
                bool ok; string v = Rea.NodeVersion(out ok);
                Ui(() =>
                {
                    busy.SetIndeterminate(false); busy.Visible = false; bNode.Enabled = true;
                    nodeVer = v; nodeOk = ok; bNode.Visible = !ok;
                    SetStatus(ok ? L.T("re_node_ok", v) : L.T("re_node_failed"), ok ? Theme.Success : Theme.Error);
                    DoLayout();
                });
            });
            t.IsBackground = true; t.Start();
        }
    }
}
