using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace FileConverter
{
    public class ArchiveEntry
    {
        public string Path;
        public long Size, Packed, Offset;
        public bool Encrypted, IsDir;
        public DateTime When;
        public int Method;
    }

    /// What's inside ZIP files (and everything built on ZIP: Word, Excel, PowerPoint, APK, JAR, EPUB) and ISO disc
    /// images: the list, and taking one file (or all) out.
    public static class Inside
    {
        public static bool CanList(string realExt)
        {
            switch (realExt) { case "zip": case "docx": case "xlsx": case "pptx": case "jar": case "apk": case "epub": case "odt": case "ods": case "odp": case "nupkg": case "vsix": case "xpi": case "iso": return true; }
            return false;
        }

        public static List<ArchiveEntry> List(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] h = new byte[8];
                fs.Read(h, 0, 8);
                if (h[0] == 0x50 && h[1] == 0x4B) return Zip(fs);
                if (fs.Length > 0x8006) { fs.Position = 0x8001; byte[] id = new byte[5]; fs.Read(id, 0, 5); if (Encoding.ASCII.GetString(id) == "CD001") return Iso(fs); }
                List<ArchiveEntry> z = Zip(fs);   // a ZIP with something in front of it (self-extracting)
                return z;
            }
        }

        // ------------------------------------------------------------------ ZIP: the central directory
        static List<ArchiveEntry> Zip(FileStream fs)
        {
            long len = fs.Length;
            int back = (int)Math.Min(len, 65557);
            byte[] b = new byte[back];
            fs.Position = len - back; fs.Read(b, 0, back);
            int eocd = -1;
            for (int i = back - 22; i >= 0; i--) if (b[i] == 0x50 && b[i + 1] == 0x4B && b[i + 2] == 5 && b[i + 3] == 6) { eocd = i; break; }
            if (eocd < 0) return null;
            long count = U16(b, eocd + 10), cdSize = U32(b, eocd + 12), cdOff = U32(b, eocd + 16);
            long shift = (len - back + eocd) - (cdOff + cdSize);   // bytes in front of the ZIP (self-extracting files)
            if (cdOff == 0xFFFFFFFF || count == 0xFFFF)
            {
                // ZIP64: the record before tells the real place
                int loc = eocd - 20;
                if (loc >= 0 && b[loc] == 0x50 && b[loc + 1] == 0x4B && b[loc + 2] == 6 && b[loc + 3] == 7)
                {
                    long z64 = (long)U64(b, loc + 8);
                    byte[] r = new byte[56]; fs.Position = z64; fs.Read(r, 0, 56);
                    count = (long)U64(r, 32); cdSize = (long)U64(r, 40); cdOff = (long)U64(r, 48);
                    shift = 0;
                }
            }
            if (cdSize <= 0 || cdSize > 512L << 20) return null;
            byte[] cd = new byte[cdSize];
            fs.Position = cdOff + Math.Max(0, shift);
            int got = 0, rr;
            while (got < cd.Length && (rr = fs.Read(cd, got, cd.Length - got)) > 0) got += rr;
            List<ArchiveEntry> l = new List<ArchiveEntry>();
            int p = 0;
            while (p + 46 <= got && cd[p] == 0x50 && cd[p + 1] == 0x4B && cd[p + 2] == 1 && cd[p + 3] == 2)
            {
                int flags = U16(cd, p + 8), method = U16(cd, p + 10), time = U16(cd, p + 12), date = U16(cd, p + 14);
                long packed = U32(cd, p + 20), size = U32(cd, p + 24), off = U32(cd, p + 42);
                int nl = U16(cd, p + 28), xl = U16(cd, p + 30), cl = U16(cd, p + 32);
                Encoding enc = (flags & 0x800) != 0 ? Encoding.UTF8 : Encoding.GetEncoding(437);
                string name = enc.GetString(cd, p + 46, Math.Min(nl, got - p - 46));
                // ZIP64 sizes in the extra field
                int x = p + 46 + nl, xe = x + xl;
                while (x + 4 <= xe && x + 4 <= got)
                {
                    int id = U16(cd, x), sz = U16(cd, x + 2);
                    if (id == 1)
                    {
                        int q = x + 4;
                        if (size == 0xFFFFFFFF && q + 8 <= xe) { size = (long)U64(cd, q); q += 8; }
                        if (packed == 0xFFFFFFFF && q + 8 <= xe) { packed = (long)U64(cd, q); q += 8; }
                        if (off == 0xFFFFFFFF && q + 8 <= xe) { off = (long)U64(cd, q); q += 8; }
                    }
                    x += 4 + sz;
                }
                DateTime when = DateTime.MinValue;
                try { when = new DateTime(1980 + (date >> 9), Math.Max(1, (date >> 5) & 15), Math.Max(1, date & 31), time >> 11, (time >> 5) & 63, (time & 31) * 2); } catch { }
                l.Add(new ArchiveEntry { Path = name.Replace('\\', '/'), Size = size, Packed = packed, Offset = off + Math.Max(0, shift), Encrypted = (flags & 1) != 0, IsDir = name.EndsWith("/"), When = when, Method = method });
                p += 46 + nl + xl + cl;
                if (l.Count > 200000) break;
            }
            return l;
        }

        static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        static long U32(byte[] b, int i) { return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }
        static ulong U64(byte[] b, int i) { return (ulong)U32(b, i) | ((ulong)U32(b, i + 4) << 32); }

        // ------------------------------------------------------------------ ISO 9660 (CD/DVD images)
        static List<ArchiveEntry> Iso(FileStream fs)
        {
            byte[] pvd = new byte[2048];
            fs.Position = 16 * 2048; fs.Read(pvd, 0, 2048);
            int bs = pvd[128] | (pvd[129] << 8);
            if (bs <= 0) bs = 2048;
            List<ArchiveEntry> l = new List<ArchiveEntry>();
            long rootLba = U32(pvd, 156 + 2), rootLen = U32(pvd, 156 + 10);
            IsoDir(fs, bs, rootLba, rootLen, "", l, 0);
            return l;
        }

        static void IsoDir(FileStream fs, int bs, long lba, long len, string prefix, List<ArchiveEntry> l, int depth)
        {
            if (depth > 12 || len <= 0 || len > 64 << 20 || l.Count > 100000) return;
            byte[] d = new byte[len];
            fs.Position = lba * bs; fs.Read(d, 0, (int)len);
            int p = 0;
            while (p < len)
            {
                int rl = d[p];
                if (rl == 0) { p = (p / bs + 1) * bs; continue; }
                if (p + 33 > len) break;
                long elba = U32(d, p + 2), esize = U32(d, p + 10);
                int flags = d[p + 25], nl = d[p + 32];
                if (p + 33 + nl > len) break;
                string name = nl == 1 && (d[p + 33] == 0 || d[p + 33] == 1) ? null : Encoding.ASCII.GetString(d, p + 33, nl);
                if (name != null)
                {
                    int semi = name.IndexOf(';'); if (semi > 0) name = name.Substring(0, semi);
                    name = name.TrimEnd('.');
                    bool dir = (flags & 2) != 0;
                    DateTime when = DateTime.MinValue;
                    try { when = new DateTime(1900 + d[p + 18], Math.Max(1, (int)d[p + 19]), Math.Max(1, (int)d[p + 20]), d[p + 21], d[p + 22], d[p + 23]); } catch { }
                    l.Add(new ArchiveEntry { Path = prefix + name + (dir ? "/" : ""), Size = dir ? 0 : esize, Packed = dir ? 0 : esize, Offset = elba * bs, IsDir = dir, When = when, Method = -1 });
                    if (dir) IsoDir(fs, bs, elba, esize, prefix + name + "/", l, depth + 1);
                }
                p += rl;
            }
        }

        // ------------------------------------------------------------------ taking files out
        /// One file out of the archive, into a folder (the name kept). Returns its path.
        public static string Extract(string archive, ArchiveEntry e, string toDir)
        {
            Directory.CreateDirectory(toDir);
            string outp = Paths.UniquePath(toDir, System.IO.Path.GetFileNameWithoutExtension(e.Path.TrimEnd('/')), Formats.Ext(e.Path));
            if (Formats.Ext(e.Path) == "") outp = Paths.UniquePath(toDir, System.IO.Path.GetFileName(e.Path.TrimEnd('/')), "bin");
            if (e.Method == -1)
            {
                using (FileStream src = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (FileStream dst = File.Create(outp)) { src.Position = e.Offset; Copy(src, dst, e.Size); }
                return outp;
            }
            if (e.Encrypted) throw new UserError(L.T("in_locked"));
            using (ZipArchive z = ZipFile.OpenRead(archive))
            {
                ZipArchiveEntry ze = z.GetEntry(e.Path) ?? z.GetEntry(e.Path.Replace('/', '\\'));
                if (ze == null) foreach (ZipArchiveEntry x in z.Entries) if (x.FullName.Replace('\\', '/') == e.Path) { ze = x; break; }
                if (ze == null) throw new UserError(L.T("in_not_found"));
                using (Stream s = ze.Open()) using (FileStream dst = File.Create(outp)) s.CopyTo(dst);
            }
            return outp;
        }

        /// Everything out, into a new folder next to the archive.
        public static string ExtractAll(string archive, List<ArchiveEntry> list, Func<bool> cancelled)
        {
            string baseDir = DragWheel.OutDirFor(archive), stem = System.IO.Path.GetFileNameWithoutExtension(archive);
            string dir = System.IO.Path.Combine(baseDir, stem);
            for (int n = 2; Directory.Exists(dir) || File.Exists(dir); n++) dir = System.IO.Path.Combine(baseDir, stem + " (" + n + ")");
            Directory.CreateDirectory(dir);
            string root = System.IO.Path.GetFullPath(dir) + System.IO.Path.DirectorySeparatorChar;
            bool iso = list.Count > 0 && list[0].Method == -1;
            ZipArchive z = iso ? null : ZipFile.OpenRead(archive);
            try
            {
                using (FileStream src = iso ? new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null)
                    foreach (ArchiveEntry e in list)
                    {
                        if (cancelled != null && cancelled()) break;
                        string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, e.Path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never outside the folder ("../" names)
                        if (e.IsDir) { Directory.CreateDirectory(target); continue; }
                        if (e.Encrypted) continue;
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        if (iso) { using (FileStream dst = File.Create(target)) { src.Position = e.Offset; Copy(src, dst, e.Size); } continue; }
                        ZipArchiveEntry ze = z.GetEntry(e.Path);
                        if (ze == null) continue;
                        using (Stream s = ze.Open()) using (FileStream dst = File.Create(target)) s.CopyTo(dst);
                    }
            }
            finally { if (z != null) z.Dispose(); }
            return dir;
        }

        static void Copy(Stream src, Stream dst, long n)
        {
            byte[] b = new byte[1 << 16];
            while (n > 0) { int r = src.Read(b, 0, (int)Math.Min(b.Length, n)); if (r <= 0) break; dst.Write(b, 0, r); n -= r; }
        }
    }

    public class PeFacts
    {
        public string Machine = "", Kind = "", Subsystem = "";
        public bool Is64, IsDll, IsNet, IsDriver;
        public DateTime Built; public bool BuiltReal;
        public int Sections;
        public List<string> Dlls = new List<string>();
        public HashSet<string> Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public long Overlay = -1;
    }

    /// A Windows program's own header: 32 or 64-bit, .NET or not, when it was built, and what it calls in Windows
    /// (which says what it can do: go online, change the registry, start other programs…).
    public static class Pe
    {
        public static PeFacts Read(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] dos = new byte[64];
                if (fs.Read(dos, 0, 64) < 64 || dos[0] != 'M' || dos[1] != 'Z') return null;
                int peAt = BitConverter.ToInt32(dos, 60);
                if (peAt <= 0 || peAt > fs.Length - 256) return null;
                byte[] h = new byte[24 + 240];
                fs.Position = peAt; fs.Read(h, 0, h.Length);
                if (h[0] != 'P' || h[1] != 'E') return null;
                PeFacts pi = new PeFacts();
                int machine = BitConverter.ToUInt16(h, 4), nsec = BitConverter.ToUInt16(h, 6);
                uint stamp = BitConverter.ToUInt32(h, 8);
                int optSize = BitConverter.ToUInt16(h, 20), chars = BitConverter.ToUInt16(h, 22);
                pi.Sections = nsec;
                pi.Machine = machine == 0x8664 ? "x64" : machine == 0x14c ? "x86" : machine == 0xAA64 ? "ARM64" : machine == 0x1c4 ? "ARM" : "0x" + machine.ToString("X4");
                pi.IsDll = (chars & 0x2000) != 0;
                // a build stamp from the future or before 1995 is a "reproducible build" hash, not a date
                DateTime when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(stamp).ToLocalTime();
                pi.Built = when; pi.BuiltReal = when.Year >= 1995 && when <= DateTime.Now.AddDays(2);
                int opt = 24;
                int magic = BitConverter.ToUInt16(h, opt);
                pi.Is64 = magic == 0x20b;
                int sub = BitConverter.ToUInt16(h, opt + 68);
                pi.Subsystem = sub == 2 ? "gui" : sub == 3 ? "console" : sub == 1 ? "native" : sub == 10 || sub == 11 || sub == 12 ? "efi" : "other";
                pi.IsDriver = sub == 1;
                int dirs = opt + (pi.Is64 ? 112 : 96);
                uint impRva = BitConverter.ToUInt32(h, dirs + 8), clrRva = BitConverter.ToUInt32(h, dirs + 14 * 8), certOff = BitConverter.ToUInt32(h, dirs + 4 * 8), certSize = BitConverter.ToUInt32(h, dirs + 4 * 8 + 4);
                pi.IsNet = clrRva != 0;
                // sections: to turn addresses into places in the file
                byte[] st = new byte[nsec * 40];
                fs.Position = peAt + 24 + optSize; fs.Read(st, 0, st.Length);
                long lastEnd = 0;
                List<uint[]> secs = new List<uint[]>();
                for (int i = 0; i < nsec; i++)
                {
                    uint vs = BitConverter.ToUInt32(st, i * 40 + 8), va = BitConverter.ToUInt32(st, i * 40 + 12), rs = BitConverter.ToUInt32(st, i * 40 + 16), rp = BitConverter.ToUInt32(st, i * 40 + 20);
                    secs.Add(new[] { va, Math.Max(vs, rs), rp });
                    lastEnd = Math.Max(lastEnd, (long)rp + rs);
                }
                pi.Overlay = lastEnd;
                if (certOff > 0 && certOff >= lastEnd) pi.Overlay = Math.Max(lastEnd, (long)certOff + certSize);
                pi.Kind = pi.IsNet ? "net" : pi.IsDriver ? "driver" : pi.IsDll ? "dll" : "exe";
                try { Imports(fs, secs, impRva, pi.Is64, pi); } catch { }
                return pi;
            }
        }

        static long Off(List<uint[]> secs, uint rva)
        {
            foreach (uint[] s in secs) if (rva >= s[0] && rva < s[0] + s[1]) return rva - s[0] + s[2];
            return -1;
        }

        static string Str(FileStream fs, long at)
        {
            if (at < 0) return null;
            fs.Position = at;
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < 256; i++) { int c = fs.ReadByte(); if (c <= 0) break; b.Append((char)c); }
            return b.ToString();
        }

        static void Imports(FileStream fs, List<uint[]> secs, uint rva, bool is64, PeFacts pi)
        {
            long at = Off(secs, rva);
            if (at < 0) return;
            byte[] d = new byte[20];
            for (int n = 0; n < 512; n++)
            {
                fs.Position = at + n * 20;
                if (fs.Read(d, 0, 20) < 20) break;
                uint ilt = BitConverter.ToUInt32(d, 0), nameRva = BitConverter.ToUInt32(d, 12), iat = BitConverter.ToUInt32(d, 16);
                if (nameRva == 0) break;
                string dll = Str(fs, Off(secs, nameRva));
                if (string.IsNullOrEmpty(dll)) continue;
                pi.Dlls.Add(dll.ToLowerInvariant());
                long t = Off(secs, ilt != 0 ? ilt : iat);
                if (t < 0) continue;
                int sz = is64 ? 8 : 4;
                byte[] e = new byte[sz];
                for (int k = 0; k < 4000; k++)
                {
                    fs.Position = t + k * sz;
                    if (fs.Read(e, 0, sz) < sz) break;
                    ulong v = is64 ? BitConverter.ToUInt64(e, 0) : BitConverter.ToUInt32(e, 0);
                    if (v == 0) break;
                    bool byOrdinal = is64 ? (v & 0x8000000000000000UL) != 0 : (v & 0x80000000UL) != 0;
                    if (byOrdinal) continue;
                    string fn = Str(fs, Off(secs, (uint)(v & 0x7FFFFFFF)) + 2);
                    if (!string.IsNullOrEmpty(fn)) pi.Functions.Add(fn);
                }
            }
        }

        /// Where the program's own parts end (anything after is an add-on: installer data, a signature, or hidden data).
        public static long OverlayStart(FileStream fs)
        {
            try
            {
                byte[] dos = new byte[64]; fs.Position = 0;
                if (fs.Read(dos, 0, 64) < 64 || dos[0] != 'M' || dos[1] != 'Z') return -1;
                fs.Position = 0;
                string path = fs.Name;
                PeFacts pi = Read(path);
                return pi == null ? -1 : pi.Overlay;
            }
            catch { return -1; }
        }

        /// What the program can do, from the Windows functions it uses. Each: (level, key of the text).
        public static List<KeyValuePair<int, string>> Abilities(PeFacts pi)
        {
            List<KeyValuePair<int, string>> r = new List<KeyValuePair<int, string>>();
            Func<string[], bool> dll = names => { foreach (string n in names) if (pi.Dlls.Contains(n)) return true; return false; };
            Func<string[], bool> fn = names => { foreach (string n in names) foreach (string f in pi.Functions) if (f.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return true; return false; };
            if (dll(new[] { "ws2_32.dll", "wsock32.dll", "wininet.dll", "winhttp.dll", "urlmon.dll", "dnsapi.dll", "iphlpapi.dll" })) r.Add(new KeyValuePair<int, string>(0, "pa_net"));
            if (fn(new[] { "RegSetValue", "RegCreateKey", "RegDeleteKey", "RegDeleteValue" })) r.Add(new KeyValuePair<int, string>(0, "pa_reg"));
            if (fn(new[] { "CreateProcess", "ShellExecute", "WinExec" })) r.Add(new KeyValuePair<int, string>(0, "pa_run"));
            if (fn(new[] { "CreateService", "OpenSCManager", "StartService" })) r.Add(new KeyValuePair<int, string>(1, "pa_service"));
            if (fn(new[] { "SetWindowsHookEx", "GetAsyncKeyState", "GetKeyboardState", "RegisterRawInputDevices" })) r.Add(new KeyValuePair<int, string>(1, "pa_keys"));
            if (fn(new[] { "BitBlt" }) && fn(new[] { "GetDC", "GetWindowDC" }) || fn(new[] { "PrintWindow" })) r.Add(new KeyValuePair<int, string>(0, "pa_screen"));
            if (fn(new[] { "WriteProcessMemory", "CreateRemoteThread", "VirtualAllocEx", "NtWriteVirtualMemory", "QueueUserAPC" })) r.Add(new KeyValuePair<int, string>(1, "pa_inject"));
            if (fn(new[] { "CryptEncrypt", "BCryptEncrypt", "CryptProtectData" })) r.Add(new KeyValuePair<int, string>(0, "pa_crypt"));
            if (fn(new[] { "OpenClipboard", "GetClipboardData", "SetClipboardData" })) r.Add(new KeyValuePair<int, string>(0, "pa_clip"));
            if (fn(new[] { "AdjustTokenPrivileges", "LookupPrivilegeValue" })) r.Add(new KeyValuePair<int, string>(0, "pa_admin"));
            if (fn(new[] { "IsDebuggerPresent", "CheckRemoteDebuggerPresent" })) r.Add(new KeyValuePair<int, string>(0, "pa_debug"));
            if (fn(new[] { "waveIn", "mciSendString" }) || dll(new[] { "mfplat.dll", "avicap32.dll" })) r.Add(new KeyValuePair<int, string>(0, "pa_media"));
            return r;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        /// The program's icon at its biggest (up to 256 px), or null when it has none.
        public static Bitmap Icon(string path)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                foreach (int size in new[] { 256, 128, 64, 48, 32 })
                {
                    try
                    {
                        IntPtr[] h = new IntPtr[1]; int[] id = new int[1];
                        if (PrivateExtractIcons(path, 0, size, size, h, id, 1, 0) == 0 || h[0] == IntPtr.Zero) continue;
                        try { using (Icon ic = System.Drawing.Icon.FromHandle(h[0])) return ic.ToBitmap(); }
                        finally { DestroyIcon(h[0]); }
                    }
                    catch { }
                }
            try { using (Icon ic = System.Drawing.Icon.ExtractAssociatedIcon(path)) return ic == null ? null : ic.ToBitmap(); } catch { return null; }
        }

        public static FileVersionInfo Version(string path) { try { return FileVersionInfo.GetVersionInfo(path); } catch { return null; } }
    }
}
