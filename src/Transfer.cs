using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FileConverter
{
    // ====================================================================== File Transfer: the parts under the page
    //
    // Two ways to get files from one PC to another across the internet:
    //  - Hosting (built in): the sender's PC accepts the friend's connection itself. The router is asked (UPnP) to
    //    let it in. Nothing sits in between: the files go straight from one PC to the other, over several
    //    connections at once, encrypted with a password that only the code carries, and every piece is checked.
    //  - The engine (sendme, downloaded when first needed): for internet that can't host (a shared address). It
    //    connects the two PCs directly through "hole punching" when it can, or through a relay when it can't.
    // The code the sender shares carries the ways to reach it; the receiver tries hosting first.

    /// One file being sent: where it is, the name it arrives under (folders kept), its size and date.
    public class FtFile
    {
        public string Path, Rel;
        public long Size, Mtime;
    }

    /// File Transfer's settings.
    public static class FtSettings
    {
        /// "auto" (hosting, then the engine), "host" (hosting only) or "engine" (the engine only).
        public static string Mode
        {
            get { string m = Settings.Get("TransferMode", "auto"); return m == "host" || m == "engine" ? m : "auto"; }
            set { Settings.Set("TransferMode", value); }
        }
        public static int Port
        {
            get { int p; return int.TryParse(Settings.Get("TransferPort", "47821"), out p) && p >= 1024 && p <= 65535 ? p : 47821; }
            set { Settings.Set("TransferPort", value.ToString(CultureInfo.InvariantCulture)); }
        }
        /// The port was forwarded to this PC by hand in the router: hosting is trusted without the router saying so.
        public static bool Forwarded { get { return Settings.Get("TransferForwarded", "0") == "1"; } set { Settings.Set("TransferForwarded", value ? "1" : "0"); } }
        /// Connections used at once by a hosted transfer (more keeps far-away transfers at full speed).
        public const int Streams = 6;
        /// The piece size a transfer is cut into (resuming starts again at the first piece not finished).
        public const int Block = 16 << 20;
    }

    // ---------------------------------------------------------------------- who this PC is

    /// This PC as File Transfer knows it: a random id made once (never anything personal), and the name friends see.
    public static class FtIdentity
    {
        public static byte[] IdBytes
        {
            get
            {
                string s = Settings.Get("TransferDevice", "");
                byte[] b = null;
                try { if (s.Length > 0) b = Convert.FromBase64String(s); } catch { }
                if (b == null || b.Length != 8) { b = FtCrypto.Rand(8); Settings.Set("TransferDevice", Convert.ToBase64String(b)); }
                return b;
            }
        }
        public static string Id { get { return FtCode.Hex(IdBytes); } }
        /// The name other PCs see (Settings, or the Windows user's name).
        public static string Name
        {
            get { string n = Settings.Get("TransferName", "").Trim(); if (n.Length == 0) n = Environment.UserName; return n.Length > 40 ? n.Substring(0, 40) : n; }
            set { Settings.Set("TransferName", (value ?? "").Trim()); }
        }
    }

    /// Friends: PCs this one exchanged a key with after a transfer (the receiver chose "Remember"). Both keep the
    /// same key, so each can prove who it is to the other; the friend's engine ticket lets this PC reach theirs from
    /// anywhere (File Transfer's friends list), and the dates are the history. Kept in the settings.
    public static class FtTrust
    {
        public class Entry
        {
            public string Id, Name, Ticket;
            /// A name given to this friend on this PC (Rename); their own name is kept underneath.
            public string Nick;
            /// The name to show: the one given here, or their own.
            public string Shown { get { return !string.IsNullOrEmpty(Nick) ? Nick : Name ?? ""; } }
            public byte[] Key;
            /// Their files are accepted without asking (chosen on this PC).
            public bool Auto;
            public long LastSent, LastGot;   // UTC ticks; 0 = never
            public long Last { get { return Math.Max(LastSent, LastGot); } }
        }
        public static event Action Changed;

        /// Every friend, the most recent first.
        public static List<Entry> All()
        {
            Dictionary<string, Entry> d = new Dictionary<string, Entry>();
            foreach (string row in Settings.Get("TransferTrust", "").Split(';'))
            {
                string[] p = row.Split('|');
                if (p.Length < 5) continue;
                try
                {
                    Entry e = new Entry { Id = p[1], Name = Encoding.UTF8.GetString(Convert.FromBase64String(p[2])), Key = Convert.FromBase64String(p[3]), Auto = p[4] == "1" };
                    if (p.Length >= 8) { e.Ticket = p[5].Length > 0 ? p[5] : null; long.TryParse(p[6], out e.LastSent); long.TryParse(p[7], out e.LastGot); }
                    if (p.Length >= 9 && p[8].Length > 0) e.Nick = Encoding.UTF8.GetString(Convert.FromBase64String(p[8]));
                    Entry old;
                    if (d.TryGetValue(e.Id, out old)) { old.Auto |= e.Auto; if (old.Ticket == null) old.Ticket = e.Ticket; } else d[e.Id] = e;
                }
                catch { }
            }
            d.Remove(FtIdentity.Id);   // this PC itself is never its own friend (a transfer to itself, to try things out)
            List<Entry> l = new List<Entry>(d.Values);
            l.Sort((a, b) => b.Last.CompareTo(a.Last));
            return l;
        }
        static void Save(List<Entry> l)
        {
            List<string> rows = new List<string>();
            foreach (Entry e in l)
                rows.Add("f|" + e.Id + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Name ?? "")) + "|" + Convert.ToBase64String(e.Key) + "|" + (e.Auto ? "1" : "0") + "|" +
                         (e.Ticket ?? "") + "|" + e.LastSent.ToString(CultureInfo.InvariantCulture) + "|" + e.LastGot.ToString(CultureInfo.InvariantCulture) + "|" +
                         (string.IsNullOrEmpty(e.Nick) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Nick))));
            Settings.Set("TransferTrust", string.Join(";", rows.ToArray()));
            if (Changed != null) try { Changed(); } catch { }
        }
        public static Entry Find(string id) { foreach (Entry e in All()) if (e.Id == id) return e; return null; }
        public static void Put(Entry n)
        {
            List<Entry> l = All();
            Entry old = l.Find(e => e.Id == n.Id);
            if (old != null && n.Nick == null) n.Nick = old.Nick;   // the name given here stays
            l.RemoveAll(e => e.Id == n.Id); l.Add(n); Save(l);
        }
        /// Changes one friend (when it's there).
        public static void Update(string id, Action<Entry> change)
        {
            List<Entry> l = All();
            Entry e = l.Find(x => x.Id == id);
            if (e == null) return;
            change(e);
            Save(l);
        }
        public static void Forget(string id) { List<Entry> l = All(); l.RemoveAll(e => e.Id == id); Save(l); }
    }

    // ---------------------------------------------------------------------- the code shared with the friend

    /// What the receiver needs, packed as small as it goes: a 10-byte secret (the transfer's name and password
    /// are made from it), the few addresses worth trying (internet, one IPv6, one on the home network), and, only
    /// when the PC can't surely host, the engine's ticket with the size of what's coming.
    public class FtCode
    {
        public byte[] Secret;
        public List<string> Hosts = new List<string>();
        public string Ticket, Name;
        public int Count;
        public long Size;

        const string Prefix = "FADI-";

        public static FtCode New() { return new FtCode { Secret = FtCrypto.Rand(10) }; }
        public byte[] Id { get { return Derive("fadi-ft-id", 16); } }
        public byte[] Key { get { return Derive("fadi-ft-key", 32); } }
        byte[] Derive(string what, int n)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] w = Encoding.ASCII.GetBytes(what), b = new byte[w.Length + Secret.Length];
                Buffer.BlockCopy(w, 0, b, 0, w.Length); Buffer.BlockCopy(Secret, 0, b, w.Length, Secret.Length);
                byte[] d = h.ComputeHash(b), r = new byte[n];
                Buffer.BlockCopy(d, 0, r, 0, n);
                return r;
            }
        }

        public override string ToString()
        {
            using (MemoryStream m = new MemoryStream())
            {
                bool t = Ticket != null;
                m.WriteByte((byte)(0x20 | (t ? 1 : 0)));
                m.Write(Secret, 0, 10);
                List<IPEndPoint> eps = new List<IPEndPoint>();
                foreach (string h in Hosts) { IPEndPoint ep = FtReceiver.ParseEp(h); if (ep != null) eps.Add(ep); }
                m.WriteByte((byte)eps.Count);
                foreach (IPEndPoint ep in eps)
                {
                    byte[] a = ep.Address.GetAddressBytes();
                    m.WriteByte((byte)(a.Length == 4 ? 4 : 6));
                    m.Write(a, 0, a.Length);
                    m.WriteByte((byte)(ep.Port >> 8)); m.WriteByte((byte)ep.Port);
                }
                if (t)
                {
                    // what the engine route needs shown before accepting: the size, how many files, the name
                    Var(m, (ulong)Math.Max(0, Size)); Var(m, (ulong)Math.Max(0, Count));
                    byte[] n = Encoding.UTF8.GetBytes(Clip(Name ?? "", 48));
                    m.WriteByte((byte)n.Length); m.Write(n, 0, n.Length);
                    byte[] tk = Squeeze(TicketBytes(Ticket));
                    m.Write(tk, 0, tk.Length);
                }
                return Prefix + Convert.ToBase64String(m.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }

        /// Reads a code (a message around it and line breaks a chat app added are fine); null when it isn't one.
        public static FtCode Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int at = text.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            StringBuilder s = new StringBuilder();
            for (int i = at + Prefix.Length; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_') s.Append(c);
                else if (c == '\r' || c == '\n') continue;   // a chat app may break a long code over lines
                else break;
            }
            try
            {
                string b64 = s.ToString().Replace('-', '+').Replace('_', '/');
                while (b64.Length % 4 != 0) b64 += "=";
                byte[] b = Convert.FromBase64String(b64);
                if (b.Length < 12 || (b[0] >> 4) != 2) return null;
                FtCode c = new FtCode { Secret = new byte[10] };
                Buffer.BlockCopy(b, 1, c.Secret, 0, 10);
                int p = 11, n = b[p++];
                for (int i = 0; i < n; i++)
                {
                    int len = b[p++] == 4 ? 4 : 16;
                    byte[] a = new byte[len]; Buffer.BlockCopy(b, p, a, 0, len); p += len;
                    int port = b[p] << 8 | b[p + 1]; p += 2;
                    IPAddress ip = new IPAddress(a);
                    c.Hosts.Add((len == 16 ? "[" + ip + "]" : ip.ToString()) + ":" + port);
                }
                if ((b[0] & 1) != 0)
                {
                    c.Size = (long)ReadVar(b, ref p); c.Count = (int)ReadVar(b, ref p);
                    int nl = b[p++]; c.Name = Encoding.UTF8.GetString(b, p, nl); p += nl;
                    byte[] rest = new byte[b.Length - p]; Buffer.BlockCopy(b, p, rest, 0, rest.Length);
                    c.Ticket = "blob" + Base32(Unsqueeze(rest));
                }
                return c;
            }
            catch { return null; }
        }

        static string Clip(string s, int bytes) { while (Encoding.UTF8.GetByteCount(s) > bytes) s = s.Substring(0, s.Length - 1); return s; }
        static void Var(Stream m, ulong v) { while (v >= 0x80) { m.WriteByte((byte)(v | 0x80)); v >>= 7; } m.WriteByte((byte)v); }
        static ulong ReadVar(byte[] b, ref int p) { ulong v = 0; int sh = 0; while (true) { byte x = b[p++]; v |= (ulong)(x & 0x7F) << sh; if (x < 0x80) return v; sh += 7; } }

        // the engine's ticket is "blob" + base32 of its bytes; the bytes go in the code as they are
        const string B32 = "abcdefghijklmnopqrstuvwxyz234567";
        static byte[] TicketBytes(string t)
        {
            if (!t.StartsWith("blob")) throw new FormatException("ticket");
            List<byte> o = new List<byte>(); int buf = 0, bits = 0;
            foreach (char ch in t.Substring(4))
            {
                int v = B32.IndexOf(char.ToLowerInvariant(ch)); if (v < 0) throw new FormatException("ticket");
                buf = buf << 5 | v; bits += 5;
                if (bits >= 8) { bits -= 8; o.Add((byte)(buf >> bits)); buf &= (1 << bits) - 1; }
            }
            return o.ToArray();
        }
        static string Base32(byte[] b)
        {
            StringBuilder s = new StringBuilder(); int buf = 0, bits = 0;
            foreach (byte x in b) { buf = buf << 8 | x; bits += 8; while (bits >= 5) { bits -= 5; s.Append(B32[(buf >> bits) & 31]); } buf &= (1 << bits) - 1; }
            if (bits > 0) s.Append(B32[(buf << (5 - bits)) & 31]);
            return s.ToString();
        }

        // the relay's web address inside the ticket is mostly the same words every time: they're swapped for one
        // byte each (0xFF then a number; a real 0xFF is written 0xFF 0x00), and put back exactly on the other side
        static readonly string[] Words = { ".relay.n0.iroh.iroh.link./", ".relay.n0.iroh-canary.iroh.link./", "https://", ".iroh.link./" };
        static byte[] Squeeze(byte[] b)
        {
            List<byte> o = new List<byte>();
            for (int i = 0; i < b.Length; )
            {
                int hit = -1;
                for (int w = 0; w < Words.Length && hit < 0; w++)
                {
                    byte[] wb = Encoding.ASCII.GetBytes(Words[w]);
                    if (i + wb.Length > b.Length) continue;
                    bool same = true;
                    for (int k = 0; k < wb.Length && same; k++) if (b[i + k] != wb[k]) same = false;
                    if (same) hit = w;
                }
                if (hit >= 0) { o.Add(0xFF); o.Add((byte)(hit + 1)); i += Words[hit].Length; continue; }
                if (b[i] == 0xFF) { o.Add(0xFF); o.Add(0); } else o.Add(b[i]);
                i++;
            }
            return o.ToArray();
        }
        static byte[] Unsqueeze(byte[] b)
        {
            List<byte> o = new List<byte>();
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] != 0xFF) { o.Add(b[i]); continue; }
                int w = b[++i];
                if (w == 0) o.Add(0xFF); else o.AddRange(Encoding.ASCII.GetBytes(Words[w - 1]));
            }
            return o.ToArray();
        }

        public static string Hex(byte[] b) { StringBuilder s = new StringBuilder(); foreach (byte x in b) s.Append(x.ToString("x2")); return s.ToString(); }
    }

    // ---------------------------------------------------------------------- encryption and the wire

    static class FtCrypto
    {
        public static byte[] Rand(int n) { byte[] b = new byte[n]; using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        public static byte[] Mac(byte[] key, params byte[][] parts)
        {
            using (HMACSHA256 h = new HMACSHA256(key))
            {
                foreach (byte[] p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
                h.TransformFinalBlock(new byte[0], 0, 0);
                return h.Hash;
            }
        }
        public static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }
        /// Compares without giving away how much matched.
        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }

    /// HMAC-SHA256 over Windows' own SHA-256 (CNG, which uses the processor's SHA instructions when it has them):
    /// several times faster than .NET's built-in HMAC, which matters at gigabit speeds.
    sealed class FastHmac : IDisposable
    {
        readonly HashAlgorithm inner, outer;
        readonly byte[] ipad = new byte[64], opad = new byte[64];
        public FastHmac(byte[] key)
        {
            inner = Make(); outer = Make();
            byte[] k = key.Length > 64 ? Make().ComputeHash(key) : key;
            for (int i = 0; i < 64; i++) { byte b = i < k.Length ? k[i] : (byte)0; ipad[i] = (byte)(b ^ 0x36); opad[i] = (byte)(b ^ 0x5c); }
            Begin();
        }
        static HashAlgorithm Make()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) try { return new SHA256Cng(); } catch { }
            return SHA256.Create();
        }
        public void Begin() { inner.Initialize(); inner.TransformBlock(ipad, 0, 64, null, 0); }
        public void Add(byte[] b, int off, int len) { inner.TransformBlock(b, off, len, null, 0); }
        /// The tag for everything added since Begin; ready for the next one afterwards.
        public byte[] End()
        {
            inner.TransformFinalBlock(new byte[0], 0, 0);
            byte[] ih = inner.Hash;
            outer.Initialize();
            outer.TransformBlock(opad, 0, 64, null, 0);
            outer.TransformFinalBlock(ih, 0, ih.Length);
            byte[] tag = outer.Hash;
            Begin();
            return tag;
        }
        public void Dispose() { inner.Dispose(); outer.Dispose(); }
    }

    /// One connection, after both sides proved they hold the transfer's password: every message is encrypted
    /// (AES-256) and signed (HMAC-SHA256) with keys made fresh for this connection, and numbered, so nothing can
    /// be changed, replayed or reordered on the way.
    sealed class FtChannel : IDisposable
    {
        public const byte List = 1, ListReply = 2, Get = 3, Data = 4, Err = 5, Done = 6, Who = 7, WhoReply = 8, Pair = 9, PairOk = 10, Thumb = 11, ThumbReply = 12;
        static readonly byte[] Magic = FtCrypto.Ascii("FADIFT1\0");
        const int MaxFrame = 24 << 20;

        readonly Socket sock;
        readonly NetworkStream ns;
        readonly Aes aes = Aes.Create();
        byte[] encOut, encIn;
        FastHmac macOut, macIn;
        long seqOut, seqIn;
        byte dirOut, dirIn;
        public readonly IPEndPoint Remote;

        FtChannel(Socket s)
        {
            sock = s;
            s.NoDelay = true;
            try { s.SendBufferSize = 4 << 20; s.ReceiveBufferSize = 4 << 20; } catch { }
            s.ReceiveTimeout = 90000; s.SendTimeout = 90000;
            ns = new NetworkStream(s, true);
            Remote = s.RemoteEndPoint as IPEndPoint;
            aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; aes.KeySize = 256;
        }

        void Keys(byte[] key, byte[] cn, byte[] sn, bool client)
        {
            byte[] ec = FtCrypto.Mac(key, FtCrypto.Ascii("EC"), cn, sn), es = FtCrypto.Mac(key, FtCrypto.Ascii("ES"), cn, sn);
            byte[] mc = FtCrypto.Mac(key, FtCrypto.Ascii("MC"), cn, sn), ms = FtCrypto.Mac(key, FtCrypto.Ascii("MS"), cn, sn);
            encOut = client ? ec : es; encIn = client ? es : ec;
            macOut = new FastHmac(client ? mc : ms); macIn = new FastHmac(client ? ms : mc);
            dirOut = (byte)(client ? 1 : 2); dirIn = (byte)(client ? 2 : 1);
        }

        /// Connects and proves the password to a sender (and checks the sender knows it too).
        public static FtChannel Connect(IPEndPoint ep, byte[] id, byte[] key, int timeoutMs)
        {
            Socket s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                IAsyncResult ar = s.BeginConnect(ep, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) throw new TimeoutException();
                s.EndConnect(ar);
                FtChannel c = new FtChannel(s);
                s.ReceiveTimeout = Math.Max(5000, timeoutMs);
                byte[] cn = FtCrypto.Rand(16);
                byte[] hello = new byte[40];
                Buffer.BlockCopy(Magic, 0, hello, 0, 8); Buffer.BlockCopy(id, 0, hello, 8, 16); Buffer.BlockCopy(cn, 0, hello, 24, 16);
                c.ns.Write(hello, 0, hello.Length);
                byte[] reply = c.ReadExact(48);
                byte[] sn = new byte[16], proof = new byte[32];
                Buffer.BlockCopy(reply, 0, sn, 0, 16); Buffer.BlockCopy(reply, 16, proof, 0, 32);
                if (!FtCrypto.Same(proof, FtCrypto.Mac(key, FtCrypto.Ascii("S"), cn, sn))) throw new IOException("wrong sender");
                byte[] mine = FtCrypto.Mac(key, FtCrypto.Ascii("C"), cn, sn);
                c.ns.Write(mine, 0, mine.Length);
                c.Keys(key, cn, sn, true);
                s.ReceiveTimeout = 90000;
                return c;
            }
            catch { try { s.Close(); } catch { } throw; }
        }

        /// The sender's side of the greeting: the password for the asked-for transfer, or the connection is dropped.
        public static FtChannel Accept(Socket s, Func<byte[], byte[]> keyFor)
        {
            FtChannel c = new FtChannel(s);
            try
            {
                s.ReceiveTimeout = 15000;
                byte[] hello = c.ReadExact(40);
                for (int i = 0; i < 8; i++) if (hello[i] != Magic[i]) throw new IOException("not a transfer");
                byte[] id = new byte[16], cn = new byte[16];
                Buffer.BlockCopy(hello, 8, id, 0, 16); Buffer.BlockCopy(hello, 24, cn, 0, 16);
                byte[] key = keyFor(id);
                if (key == null) throw new IOException("unknown transfer");
                byte[] sn = FtCrypto.Rand(16);
                byte[] reply = new byte[48];
                Buffer.BlockCopy(sn, 0, reply, 0, 16); Buffer.BlockCopy(FtCrypto.Mac(key, FtCrypto.Ascii("S"), cn, sn), 0, reply, 16, 32);
                c.ns.Write(reply, 0, reply.Length);
                byte[] proof = c.ReadExact(32);
                if (!FtCrypto.Same(proof, FtCrypto.Mac(key, FtCrypto.Ascii("C"), cn, sn))) throw new IOException("wrong password");
                c.Keys(key, cn, sn, false);
                s.ReceiveTimeout = 90000;
                return c;
            }
            catch { c.Dispose(); throw; }
        }

        byte[] ReadExact(int n)
        {
            byte[] b = new byte[n];
            ReadInto(b, 0, n);
            return b;
        }
        void ReadInto(byte[] b, int off, int n)
        {
            while (n > 0)
            {
                int r = ns.Read(b, off, n);
                if (r <= 0) throw new IOException("closed");
                off += r; n -= r;
            }
        }

        byte[] Seq(long n, byte dir) { byte[] b = new byte[9]; b[0] = dir; for (int i = 0; i < 8; i++) b[i + 1] = (byte)(n >> (8 * i)); return b; }

        /// Sends one message: a type and its bytes.
        public void Send(byte type, byte[] data, int off, int len)
        {
            byte[] plain = new byte[len + 1];
            plain[0] = type;
            if (len > 0) Buffer.BlockCopy(data, off, plain, 1, len);
            byte[] iv = FtCrypto.Rand(16);
            byte[] ct;
            using (ICryptoTransform e = aes.CreateEncryptor(encOut, iv)) ct = e.TransformFinalBlock(plain, 0, plain.Length);
            byte[] s = Seq(seqOut++, dirOut);
            macOut.Add(s, 0, s.Length);
            macOut.Add(iv, 0, 16);
            macOut.Add(ct, 0, ct.Length);
            byte[] tag = macOut.End();
            int body = 16 + ct.Length + 32;
            byte[] frame = new byte[4 + body];
            frame[0] = (byte)body; frame[1] = (byte)(body >> 8); frame[2] = (byte)(body >> 16); frame[3] = (byte)(body >> 24);
            Buffer.BlockCopy(iv, 0, frame, 4, 16);
            Buffer.BlockCopy(ct, 0, frame, 20, ct.Length);
            Buffer.BlockCopy(tag, 0, frame, 20 + ct.Length, 32);
            ns.Write(frame, 0, frame.Length);
        }
        public void Send(byte type, byte[] data) { Send(type, data, 0, data == null ? 0 : data.Length); }

        /// The next message (its type is at [0], the bytes follow). A message that was changed fails here.
        public byte[] Receive()
        {
            byte[] h = ReadExact(4);
            int body = h[0] | h[1] << 8 | h[2] << 16 | h[3] << 24;
            if (body < 16 + 16 + 32 || body > MaxFrame) throw new IOException("bad message");
            byte[] b = ReadExact(body);
            int ctLen = body - 48;
            byte[] s = Seq(seqIn++, dirIn);
            macIn.Add(s, 0, s.Length);
            macIn.Add(b, 0, 16 + ctLen);
            byte[] tag = new byte[32];
            Buffer.BlockCopy(b, 16 + ctLen, tag, 0, 32);
            if (!FtCrypto.Same(tag, macIn.End())) throw new FtTamperException();
            byte[] iv = new byte[16];
            Buffer.BlockCopy(b, 0, iv, 0, 16);
            using (ICryptoTransform d = aes.CreateDecryptor(encIn, iv)) return d.TransformFinalBlock(b, 16, ctLen);
        }

        public void Dispose()
        {
            try { ns.Close(); } catch { }
            try { sock.Close(); } catch { }
            try { aes.Dispose(); } catch { }
            if (macOut != null) try { macOut.Dispose(); } catch { }
            if (macIn != null) try { macIn.Dispose(); } catch { }
        }
    }

    /// Writing into the folder failed (full, not allowed, name too long): stops the transfer instead of retrying.
    public class FtWriteError : Exception { public FtWriteError(Exception inner) : base(inner.Message, inner) { } }

    /// A piece arrived that wasn't what the sender sent (changed on the way): the connection is dropped.
    public class FtTamperException : IOException { public FtTamperException() : base("check failed") { } }

    // ---------------------------------------------------------------------- the router: UPnP

    /// Asks the home router to let the friend's connection in (UPnP "port mapping"), and what the internet
    /// address is. Many routers have it on; when they don't, the port can be forwarded by hand.
    public static class Upnp
    {
        public class Gateway { public string Control, Service; public IPAddress Local, Router; }

        static Gateway known;
        static readonly object busy = new object();

        /// The router: the one found before when it still answers, otherwise searched for (asked three times over
        /// about 3 seconds: some routers ignore a search that comes soon after another). Null when none answered.
        public static Gateway Find()
        {
            lock (busy)
            {
                Gateway k = known;
                if (k != null && External(k) != null) return k;
                Gateway g = Search();
                if (g != null) known = g;
                return g;
            }
        }

        static Gateway Search()
        {
            List<string> seen = new List<string>();
            try
            {
                using (UdpClient u = new UdpClient(0))
                {
                    u.Client.ReceiveTimeout = 700;
                    IPEndPoint ssdp = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                    Action ask = () =>
                    {
                        foreach (string st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:device:InternetGatewayDevice:2", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
                        {
                            byte[] q = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: " + st + "\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n");
                            try { u.Send(q, q.Length, ssdp); } catch { }
                        }
                    };
                    ask();
                    DateTime start = DateTime.UtcNow, until = start.AddSeconds(3.5);
                    int asked = 1;
                    while (DateTime.UtcNow < until)
                    {
                        if (asked < 3 && (DateTime.UtcNow - start).TotalSeconds > asked * 1.1) { ask(); asked++; }
                        IPEndPoint from = null;
                        byte[] r;
                        try { r = u.Receive(ref from); } catch (SocketException) { continue; }
                        string txt = Encoding.ASCII.GetString(r);
                        foreach (string line in txt.Split('\n'))
                            if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))
                            {
                                string loc = line.Substring(9).Trim();
                                if (seen.Contains(loc)) continue;
                                seen.Add(loc);
                                Gateway g = Describe(loc, from.Address);
                                if (g != null) return g;
                            }
                    }
                }
            }
            catch { }
            return null;
        }

        static Gateway Describe(string location, IPAddress router)
        {
            try
            {
                string xml;
                using (WebClient wc = new WebClient()) xml = wc.DownloadString(location);
                System.Xml.XmlDocument d = new System.Xml.XmlDocument();
                d.LoadXml(xml);
                foreach (System.Xml.XmlNode n in d.GetElementsByTagName("service"))
                {
                    string type = null, ctrl = null;
                    foreach (System.Xml.XmlNode c in n.ChildNodes)
                    {
                        if (c.LocalName == "serviceType") type = c.InnerText.Trim();
                        if (c.LocalName == "controlURL") ctrl = c.InnerText.Trim();
                    }
                    if (type == null || ctrl == null || !(type.Contains("WANIPConnection") || type.Contains("WANPPPConnection"))) continue;
                    Uri u = new Uri(new Uri(location), ctrl);
                    Gateway g = new Gateway { Control = u.ToString(), Service = type, Router = router };
                    // the address of this PC on the router's side
                    try { using (UdpClient p = new UdpClient()) { p.Connect(new IPEndPoint(IPAddress.Parse(new Uri(location).Host), 1900)); g.Local = ((IPEndPoint)p.Client.LocalEndPoint).Address; } } catch { }
                    if (g.Local != null) return g;
                }
            }
            catch { }
            return null;
        }

        static string Soap(Gateway g, string action, string args)
        {
            string body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" +
                          action + " xmlns:u=\"" + g.Service + "\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(g.Control);
            r.Method = "POST"; r.Timeout = 4000; r.ContentType = "text/xml; charset=\"utf-8\"";
            r.Headers.Add("SOAPACTION", "\"" + g.Service + "#" + action + "\"");
            byte[] b = Encoding.UTF8.GetBytes(body);
            r.ContentLength = b.Length;
            using (Stream s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            using (WebResponse w = r.GetResponse()) using (StreamReader sr = new StreamReader(w.GetResponseStream())) return sr.ReadToEnd();
        }

        /// The router's internet address (null when it won't say).
        public static IPAddress External(Gateway g)
        {
            try
            {
                string x = Soap(g, "GetExternalIPAddress", "");
                int a = x.IndexOf("<NewExternalIPAddress>"), b = x.IndexOf("</NewExternalIPAddress>");
                IPAddress ip;
                if (a >= 0 && b > a && IPAddress.TryParse(x.Substring(a + 22, b - a - 22).Trim(), out ip)) return ip;
            }
            catch { }
            return null;
        }

        /// Opens the port to this PC; false when the router refused.
        public static bool Open(Gateway g, int port)
        {
            foreach (int lease in new[] { 0, 86400 })
                try
                {
                    Soap(g, "AddPortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol><NewInternalPort>" + port +
                         "</NewInternalPort><NewInternalClient>" + g.Local + "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>Fadi Tools File Transfer</NewPortMappingDescription><NewLeaseDuration>" + lease + "</NewLeaseDuration>");
                    return true;
                }
                catch { }
            return false;
        }

        public static void Close(Gateway g, int port)
        {
            // waits for a search or an opening going on, so a "close" from the last send can't undo the next one
            lock (busy) try { Soap(g, "DeletePortMapping", "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port + "</NewExternalPort><NewProtocol>TCP</NewProtocol>"); } catch { }
        }

        /// A private or shared address (a home network, or the internet provider's shared one): not reachable from outside.
        public static bool Private(IPAddress ip)
        {
            if (ip == null) return true;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || IPAddress.IsLoopback(ip) || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || (b[0] == 169 && b[1] == 254) || b[0] == 0;
        }
        /// The internet provider's shared range (CGNAT, 100.64.0.0/10).
        public static bool Shared(IPAddress ip) { if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false; byte[] b = ip.GetAddressBytes(); return b[0] == 100 && b[1] >= 64 && b[1] <= 127; }

        /// How fast this PC's connection to the router runs (bits per second; 0 when unknown): a cable or port
        /// running at 100 Mbps caps any transfer at about 12 MB/s.
        public static long LinkSpeed()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    if (d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("vpn")) continue;
                    bool gw = false;
                    foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses) if (g.Address != null && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)) gw = true;
                    if (gw && ni.Speed > 0) return ni.Speed;
                }
            }
            catch { }
            return 0;
        }

        /// This PC's addresses: home network IPv4 ones, and IPv6 ones that work on the internet (the longest-lasting first).
        public static void LocalAddresses(List<IPAddress> lan, List<IPAddress> v6)
        {
            List<long> lives = new List<long>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                    if (d.Contains("virtual") || d.Contains("vmware") || d.Contains("hyper-v") || d.Contains("vethernet") || d.Contains("loopback")) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        IPAddress ip = a.Address;
                        if (ip.AddressFamily == AddressFamily.InterNetwork) { if (!IPAddress.IsLoopback(ip) && !(ip.GetAddressBytes()[0] == 169)) lan.Add(ip); }
                        else if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !Private(ip) && !ip.IsIPv6Teredo && !ip.IsIPv6Multicast)
                        {
                            // Windows keeps a lasting address and a short-lived "temporary" one: the lasting one goes first
                            long life = 0;
                            try { life = a.AddressPreferredLifetime; } catch { }
                            v6.Add(ip); lives.Add(life);
                        }
                    }
                }
            }
            catch { }
            for (int i = 0; i < v6.Count; i++)
                for (int j = i + 1; j < v6.Count; j++)
                    if (lives[j] > lives[i]) { IPAddress t = v6[i]; v6[i] = v6[j]; v6[j] = t; long l = lives[i]; lives[i] = lives[j]; lives[j] = l; }
        }
    }

    // ---------------------------------------------------------------------- the sender: hosting

    /// Hosts one transfer: listens for the friend, proves the password both ways, and serves the files in pieces
    /// over as many connections as the friend opens. Only the files chosen can be asked for.
    public sealed class FtHost : IDisposable
    {
        /// The secret the code carries: the transfer's name and password come from it.
        public readonly FtCode Code = FtCode.New();
        public readonly byte[] Id, Key;
        public readonly List<FtFile> Files;
        public readonly string Name;
        public readonly long Total;
        public int Port { get; private set; }
        /// How much the friend reports having (resumed parts included), and whether they finished.
        public long FriendHas;
        public bool FriendDone;
        public int Connected;
        public string FriendAddress;
        public event Action Changed;
        /// A small picture of what's being sent (JPEG), shown to the receiver before they accept.
        public volatile byte[] Thumb;

        /// One PC that's taking the files (several can at once: the same code works for each).
        public class Recipient
        {
            public string Key, Id;     // how it's told apart (its id when it said one, otherwise its address), and its PC id
            public long Has;
            public bool Done;
            public int Conns;
        }
        readonly Dictionary<string, Recipient> recips = new Dictionary<string, Recipient>();
        readonly Dictionary<string, string> idOfAddr = new Dictionary<string, string>();
        public List<Recipient> Recipients
        {
            get
            {
                List<Recipient> l = new List<Recipient>();
                lock (recips) foreach (Recipient r in recips.Values) l.Add(new Recipient { Key = r.Key, Id = r.Id, Has = r.Has, Done = r.Done, Conns = r.Conns });
                return l;
            }
        }
        public int DoneCount { get { int n = 0; lock (recips) foreach (Recipient r in recips.Values) if (r.Done) n++; return n; } }
        /// Everyone who started taking the files has all of them.
        public bool AllDone { get { lock (recips) { if (recips.Count == 0) return FriendDone; foreach (Recipient r in recips.Values) if (!r.Done) return false; return true; } } }

        Recipient Who(string addr, string rid)
        {
            lock (recips)
            {
                if (rid != null) idOfAddr[addr] = rid;
                else idOfAddr.TryGetValue(addr, out rid);
                string key = rid ?? addr;
                Recipient r;
                if (!recips.TryGetValue(key, out r))
                {
                    r = new Recipient { Key = key, Id = rid };
                    // it was known by its address until it said who it is
                    Recipient byAddr;
                    if (rid != null && recips.TryGetValue(addr, out byAddr)) { recips.Remove(addr); r.Has = byAddr.Has; r.Done = byAddr.Done; r.Conns = byAddr.Conns; }
                    recips[key] = r;
                }
                return r;
            }
        }

        TcpListener listener;
        volatile bool stopped;
        readonly List<FtChannel> open = new List<FtChannel>();
        byte[] manifest;

        public FtHost(List<FtFile> files, string name)
        {
            Files = files; Name = name;
            Id = Code.Id; Key = Code.Key;
            foreach (FtFile f in files) Total += f.Size;
        }

        /// Starts listening on the port (or the next free one); throws when none is free.
        public void Start(int port)
        {
            Exception last = null;
            for (int p = port; p < port + 20 && p <= 65535; p++)
            {
                try
                {
                    TcpListener l = new TcpListener(IPAddress.IPv6Any, p);
                    try { l.Server.DualMode = true; } catch { }
                    l.Start(64);
                    listener = l; Port = p;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try
                    {
                        // no IPv6 on this PC: IPv4 only
                        TcpListener l4 = new TcpListener(IPAddress.Any, p);
                        l4.Start(64);
                        listener = l4; Port = p;
                        break;
                    }
                    catch (Exception ex2) { last = ex2; }
                }
            }
            if (listener == null) throw last ?? new IOException("no free port");
            new Thread(AcceptLoop) { IsBackground = true, Name = "ft-accept" }.Start();
        }

        void AcceptLoop()
        {
            while (!stopped)
            {
                Socket s;
                try { s = listener.AcceptSocket(); } catch { if (stopped) return; Thread.Sleep(100); continue; }
                new Thread(() => Serve(s)) { IsBackground = true, Name = "ft-serve" }.Start();
            }
        }

        void Fire() { Action a = Changed; if (a != null) try { a(); } catch { } }

        byte[] Manifest()
        {
            if (manifest != null) return manifest;
            StringBuilder b = new StringBuilder();
            b.Append(Name.Replace('\n', ' ')).Append('\n');
            foreach (FtFile f in Files)
                b.Append(f.Size.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(f.Mtime.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(f.Rel.Replace('\n', ' ')).Append('\n');
            byte[] raw = Encoding.UTF8.GetBytes(b.ToString());
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream z = new DeflateStream(ms, CompressionMode.Compress, true)) z.Write(raw, 0, raw.Length);
                return manifest = ms.ToArray();
            }
        }

        void Serve(Socket s)
        {
            FtChannel c = null;
            FileStream fs = null; int fsFor = -1;
            Recipient mine = null;
            try
            {
                c = FtChannel.Accept(s, id => FtCrypto.Same(id, Id) ? Key : null);
                lock (open) { open.Add(c); Connected = open.Count; }
                if (c.Remote != null) FriendAddress = c.Remote.Address.ToString();
                string addr = FriendAddress ?? "?";
                if (addr.StartsWith("::ffff:")) addr = addr.Substring(7);
                bool counted = false;
                Fire();
                byte[] buf = new byte[1 << 20];
                while (!stopped)
                {
                    byte[] m = c.Receive();
                    if (m[0] == FtChannel.List) { c.Send(FtChannel.ListReply, Manifest()); continue; }
                    if (m[0] == FtChannel.Thumb) { c.Send(FtChannel.ThumbReply, Thumb ?? new byte[0]); continue; }
                    if (m[0] == FtChannel.Done)
                    {
                        Recipient dr = Who(addr, m.Length >= 9 ? FtCode.Hex(Sub(m, 1, 8)) : null);
                        lock (recips) { dr.Done = true; dr.Has = Total; }
                        FriendDone = true; FriendHas = Total;
                        // the transfer was finished by the one holding the code: their newest engine ticket is kept
                        if (pendingTicket != null) { string tk = pendingTicket; FtTrust.Update(pendingFor, e => e.Ticket = tk); pendingTicket = null; }
                        Fire(); continue;
                    }
                    if (m[0] == FtChannel.Who && m.Length >= 1 + 8 + 16)
                    {
                        // who's sending: this PC's id and name, and, for a friend who remembered it, proof it's really this PC
                        string rid = FtCode.Hex(Sub(m, 1, 8));
                        Who(addr, rid);
                        byte[] nonce = Sub(m, 9, 16);
                        byte[] id = FtIdentity.IdBytes, nm = Encoding.UTF8.GetBytes(FtIdentity.Name);
                        FtTrust.Entry t = FtTrust.Find(rid);
                        byte[] proof = t != null ? FtCrypto.Mac(t.Key, FtCrypto.Ascii("who"), nonce, id) : new byte[0];
                        // a friend is sending again: the history says so (and their newest engine ticket is kept;
                        // it only counts once they've proven themselves with the key, below)
                        string theirs = m.Length > 25 ? Encoding.ASCII.GetString(m, 25, m.Length - 25) : null;
                        if (t != null) FtTrust.Update(rid, e => { e.LastSent = DateTime.UtcNow.Ticks; });
                        pendingTicket = t != null && theirs != null && theirs.StartsWith("endpoint") ? theirs : null; pendingFor = rid;
                        byte[] tk = Encoding.ASCII.GetBytes(FtPipe.MyTicket ?? "");
                        byte[] r = new byte[8 + 1 + nm.Length + 1 + proof.Length + 2 + tk.Length];
                        int at = 0;
                        Buffer.BlockCopy(id, 0, r, at, 8); at += 8;
                        r[at++] = (byte)nm.Length; Buffer.BlockCopy(nm, 0, r, at, nm.Length); at += nm.Length;
                        r[at++] = (byte)proof.Length; Buffer.BlockCopy(proof, 0, r, at, proof.Length); at += proof.Length;
                        r[at++] = (byte)(tk.Length >> 8); r[at++] = (byte)tk.Length; Buffer.BlockCopy(tk, 0, r, at, tk.Length);
                        c.Send(FtChannel.WhoReply, r);
                        continue;
                    }
                    if (m[0] == FtChannel.Pair && m.Length >= 1 + 8 + 32 + 1)
                    {
                        // the friend wants to remember this PC: the key both keep, to prove who's who next time
                        string rid = FtCode.Hex(Sub(m, 1, 8));
                        byte[] key = Sub(m, 9, 32);
                        int at = 41, nl = m[at++];
                        string rname = Encoding.UTF8.GetString(m, at, nl); at += nl;
                        int tl = m.Length >= at + 2 ? m[at] << 8 | m[at + 1] : 0; at += 2;
                        string rticket = tl > 0 && m.Length >= at + tl ? Encoding.ASCII.GetString(m, at, tl) : null;
                        // a new friend (this PC asks before taking their files; that's chosen on this PC)
                        FtTrust.Entry old = FtTrust.Find(rid);
                        FtTrust.Put(new FtTrust.Entry { Id = rid, Name = rname, Key = key, Ticket = rticket ?? (old != null ? old.Ticket : null), Auto = old != null && old.Auto, LastSent = DateTime.UtcNow.Ticks, LastGot = old != null ? old.LastGot : 0 });
                        c.Send(FtChannel.PairOk, Encoding.ASCII.GetBytes(FtPipe.MyTicket ?? ""));
                        FtPipe.Refresh();
                        continue;
                    }
                    if (m[0] != FtChannel.Get || m.Length < 25) throw new IOException("bad request");
                    int fi = BitConverter.ToInt32(m, 1);
                    long off = BitConverter.ToInt64(m, 5);
                    int len = BitConverter.ToInt32(m, 13);
                    long has = BitConverter.ToInt64(m, 17);
                    // which PC is asking (newer ones say; older ones are told apart by their address)
                    Recipient rc = Who(addr, m.Length >= 33 ? FtCode.Hex(Sub(m, 25, 8)) : null);
                    bool moved = false;
                    lock (recips)
                    {
                        if (!counted) { rc.Conns++; counted = true; mine = rc; moved = true; }
                        if (has > rc.Has && !rc.Done) { rc.Has = Math.Min(Total, has); moved = true; }
                    }
                    if (has > FriendHas) { FriendHas = has; moved = true; }
                    if (moved) Fire();
                    if (fi < 0 || fi >= Files.Count || off < 0 || len < 0 || off + len > Files[fi].Size)
                    { c.Send(FtChannel.Err, Encoding.UTF8.GetBytes("bad range")); continue; }
                    if (fsFor != fi)
                    {
                        if (fs != null) fs.Dispose();
                        fs = null; fsFor = -1;
                        try { fs = new FileStream(Files[fi].Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan); fsFor = fi; }
                        catch (Exception ex) { c.Send(FtChannel.Err, Encoding.UTF8.GetBytes(L.T("ft_err_read", Files[fi].Rel, ex.Message))); continue; }
                    }
                    fs.Seek(off, SeekOrigin.Begin);
                    while (len > 0 && !stopped)
                    {
                        int n = fs.Read(buf, 0, Math.Min(buf.Length, len));
                        if (n <= 0) { c.Send(FtChannel.Err, Encoding.UTF8.GetBytes(L.T("ft_err_changed", Files[fi].Rel))); break; }
                        c.Send(FtChannel.Data, buf, 0, n);
                        len -= n;
                    }
                }
            }
            catch { }
            finally
            {
                if (fs != null) try { fs.Dispose(); } catch { }
                if (mine != null) lock (recips) mine.Conns = Math.Max(0, mine.Conns - 1);
                if (c != null) { lock (open) { open.Remove(c); Connected = open.Count; } c.Dispose(); Fire(); }
                else try { s.Close(); } catch { }
            }
        }

        static byte[] Sub(byte[] b, int at, int n) { byte[] r = new byte[n]; Buffer.BlockCopy(b, at, r, 0, n); return r; }
        volatile string pendingTicket, pendingFor;

        public void Dispose()
        {
            stopped = true;
            try { if (listener != null) listener.Stop(); } catch { }
            lock (open) { foreach (FtChannel c in open) c.Dispose(); open.Clear(); }
        }

        /// Everything under the chosen files and folders, with the names they'll arrive under (a folder keeps its
        /// name and what's inside it). Two items with the same name get " (2)".
        public static List<FtFile> Collect(IList<string> items, out string name)
        {
            List<FtFile> l = new List<FtFile>();
            HashSet<string> tops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in items)
            {
                string p = raw.TrimEnd('\\', '/');
                string top = System.IO.Path.GetFileName(p);
                if (string.IsNullOrEmpty(top)) top = "Drive";
                string unique = top; int n = 2;
                while (tops.Contains(unique)) unique = System.IO.Path.GetFileNameWithoutExtension(top) + " (" + (n++) + ")" + System.IO.Path.GetExtension(top);
                tops.Add(unique);
                if (Directory.Exists(p)) Walk(p, unique, l);
                else if (File.Exists(p)) l.Add(Make(p, unique));
            }
            // one item: its name; several: left empty, and the friend's Fadi Tools says "3 items" in their own language
            name = items.Count == 1 ? System.IO.Path.GetFileName(items[0].TrimEnd('\\', '/')) : "";
            return l;
        }
        static FtFile Make(string path, string rel)
        {
            FileInfo fi = new FileInfo(path);
            return new FtFile { Path = path, Rel = rel, Size = fi.Length, Mtime = fi.LastWriteTimeUtc.Ticks };
        }
        static void Walk(string dir, string rel, List<FtFile> l)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    try { if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0) l.Add(Make(f, rel + "/" + System.IO.Path.GetFileName(f))); } catch { }
                foreach (string d in Directory.GetDirectories(dir))
                    try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) Walk(d, rel + "/" + System.IO.Path.GetFileName(d), l); } catch { }
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------- the receiver: hosting

    /// Downloads a hosted transfer: several connections pull 16 MB pieces at once and write them straight into
    /// place. What's done is remembered next to the files, so a dropped connection (or closing the app) carries
    /// on from there. Every piece is checked on arrival; the files get their names only when complete.
    public sealed class FtReceiver
    {
        public readonly FtCode Code;
        public IPEndPoint Address;
        public string Name;
        public List<FtFile> Files = new List<FtFile>();
        public long Total;
        public long Done;          // bytes in place (resumed ones included)
        public long Resumed;       // bytes that were already there from an earlier try
        public int Live;           // connections working now
        public bool Reconnecting;
        public string Dest, Top;   // the folder it goes into, and the name the first item got there
        /// Who's sending (their PC's id and name), and whether it's proven to be a sender this PC remembered.
        public string SenderId, SenderName, SenderTicket;
        public bool Proven;
        /// Set before Run to remember the sender when it's done (they keep the same key on their side).
        public bool Remember;
        /// Holds the download where it is (the connections wait); set back to false to carry on.
        public volatile bool Paused;

        /// The small picture the sender made of what's coming (JPEG), or null (an older sender, or none).
        public byte[] FetchThumb()
        {
            try
            {
                using (FtChannel c = FtChannel.Connect(Address, Code.Id, Code.Key, 4000))
                {
                    c.Send(FtChannel.Thumb, null);
                    byte[] m = c.Receive();
                    if (m[0] != FtChannel.ThumbReply || m.Length < 100) return null;
                    byte[] b = new byte[m.Length - 1];
                    Buffer.BlockCopy(m, 1, b, 0, b.Length);
                    return b;
                }
            }
            catch { return null; }
        }

        public FtReceiver(FtCode code) { Code = code; }

        /// Tries every address in the code at once; the first that answers (and knows the password) wins.
        /// Then reads the list of files. False when none answered.
        public bool Connect(int timeoutMs, Func<bool> cancelled)
        {
            List<IPEndPoint> eps = new List<IPEndPoint>();
            foreach (string h in Code.Hosts)
            {
                IPEndPoint ep = ParseEp(h);
                if (ep != null) eps.Add(ep);
            }
            if (eps.Count == 0) return false;
            FtChannel won = null;
            object lk = new object();
            int left = eps.Count;
            bool over = false;
            ManualResetEvent done = new ManualResetEvent(false);
            foreach (IPEndPoint ep in eps)
            {
                IPEndPoint e2 = ep;
                new Thread(() =>
                {
                    FtChannel c = null;
                    try { c = FtChannel.Connect(e2, Code.Id, Code.Key, timeoutMs); } catch { }
                    lock (lk)
                    {
                        // the first to get through wins; later ones (or ones after giving up) are closed
                        if (c != null && won == null && !over) { won = c; Address = e2; c = null; done.Set(); }
                        if (--left == 0) done.Set();
                    }
                    if (c != null) c.Dispose();
                }) { IsBackground = true }.Start();
            }
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs + 1500);
            while (!done.WaitOne(200)) if (DateTime.UtcNow > until || (cancelled != null && cancelled())) break;
            FtChannel ch;
            lock (lk) { ch = won; won = null; over = true; }
            if (ch == null) return false;
            try
            {
                ch.Send(FtChannel.List, null);
                byte[] m = ch.Receive();
                if (m[0] != FtChannel.ListReply) throw new IOException("no list");
                string t;
                using (DeflateStream d = new DeflateStream(new MemoryStream(m, 1, m.Length - 1), CompressionMode.Decompress))
                using (StreamReader r = new StreamReader(d, Encoding.UTF8)) t = r.ReadToEnd();
                string[] lines = t.Split('\n');
                Name = lines[0];
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] p = lines[i].Split('\t');
                    if (p.Length < 3) continue;
                    string rel = Safe(p[2]);
                    if (rel == null) continue;
                    FtFile f = new FtFile { Rel = rel, Size = long.Parse(p[0], CultureInfo.InvariantCulture), Mtime = long.Parse(p[1], CultureInfo.InvariantCulture) };
                    Files.Add(f); Total += f.Size;
                }
                // who's sending (and, when this PC remembered them, the proof it's really them)
                try
                {
                    byte[] mt = Encoding.ASCII.GetBytes(FtPipe.MyTicket ?? "");
                    byte[] nonce = FtCrypto.Rand(16), q = new byte[24 + mt.Length];
                    Buffer.BlockCopy(FtIdentity.IdBytes, 0, q, 0, 8); Buffer.BlockCopy(nonce, 0, q, 8, 16); Buffer.BlockCopy(mt, 0, q, 24, mt.Length);
                    ch.Send(FtChannel.Who, q);
                    byte[] w = ch.Receive();
                    if (w[0] == FtChannel.WhoReply && w.Length >= 10)
                    {
                        byte[] sid = new byte[8]; Buffer.BlockCopy(w, 1, sid, 0, 8);
                        SenderId = FtCode.Hex(sid);
                        int at = 9, nl = w[at++];
                        SenderName = Encoding.UTF8.GetString(w, at, nl); at += nl;
                        int pl = w[at++];
                        byte[] proof = new byte[pl]; Buffer.BlockCopy(w, at, proof, 0, pl); at += pl;
                        int tl = w.Length >= at + 2 ? w[at] << 8 | w[at + 1] : 0; at += 2;
                        SenderTicket = tl > 0 && w.Length >= at + tl ? Encoding.ASCII.GetString(w, at, tl) : null;
                        FtTrust.Entry known = FtTrust.Find(SenderId);
                        if (known != null && pl == 32)
                        {
                            Proven = FtCrypto.Same(proof, FtCrypto.Mac(known.Key, FtCrypto.Ascii("who"), nonce, sid));
                            // a proven friend: their newest ticket and name, and the history
                            if (Proven) { string tk = SenderTicket, nmx = SenderName; FtTrust.Update(SenderId, e => { if (tk != null) e.Ticket = tk; if (nmx.Length > 0) e.Name = nmx; }); }
                        }
                    }
                }
                catch { }   // an older sender: no names, nothing remembered
                first = ch;
                return true;
            }
            catch { ch.Dispose(); return false; }
        }
        FtChannel first;

        public static IPEndPoint ParseEp(string h)
        {
            try
            {
                int c = h.LastIndexOf(':');
                if (c <= 0) return null;
                string host = h.Substring(0, c).Trim('[', ']');
                IPAddress ip;
                if (!IPAddress.TryParse(host, out ip)) return null;
                return new IPEndPoint(ip, int.Parse(h.Substring(c + 1), CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }

        /// A path from the sender, made safe: no drive, no "..", no characters Windows refuses.
        static string Safe(string rel)
        {
            List<string> parts = new List<string>();
            foreach (string raw in rel.Replace('\\', '/').Split('/'))
            {
                string p = raw.Trim();
                if (p.Length == 0 || p == "." || p == "..") continue;
                foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) p = p.Replace(ch, '_');
                p = p.TrimEnd('.', ' ');
                if (p.Length == 0) p = "_";
                parts.Add(p);
            }
            return parts.Count == 0 ? null : string.Join("/", parts.ToArray());
        }

        // ---- the work

        long[] blockDone;      // per file: a bit per piece, packed in longs
        int[] blockBase;       // per file: index of its first piece
        int blocks;
        readonly Queue<int> todo = new Queue<int>();
        readonly object qLock = new object();
        FileStream[] streams;
        object[] locks;
        string statePath, error;
        volatile bool stop;
        int working;

        string Final(int i) { return System.IO.Path.Combine(Dest, Mapped(Files[i].Rel).Replace('/', System.IO.Path.DirectorySeparatorChar)); }
        string Part(int i) { return Final(i) + ".fadipart"; }
        string Mapped(string rel) { int s = rel.IndexOf('/'); string top = s < 0 ? rel : rel.Substring(0, s); return s < 0 ? (topMap.ContainsKey(top) ? topMap[top] : top) : (topMap.ContainsKey(top) ? topMap[top] : top) + rel.Substring(s); }
        readonly Dictionary<string, string> topMap = new Dictionary<string, string>();

        string StateId()
        {
            StringBuilder b = new StringBuilder(Name);
            foreach (FtFile f in Files) b.Append('|').Append(f.Rel).Append('|').Append(f.Size).Append('|').Append(f.Mtime);
            using (SHA256 h = SHA256.Create()) return FtCode.Hex(h.ComputeHash(Encoding.UTF8.GetBytes(b.ToString()))).Substring(0, 16);
        }

        /// How much there is left to download into a folder (less when an earlier try got part of it).
        public long Remaining(string dest)
        {
            Dest = dest;
            LoadOrPlan(false);
            return Total - Resumed;
        }

        /// Reads what an earlier try left in this folder, or chooses names that don't clash with what's there.
        void LoadOrPlan(bool commit)
        {
            blockBase = new int[Files.Count];
            blocks = 0;
            for (int i = 0; i < Files.Count; i++) { blockBase[i] = blocks; blocks += (int)((Files[i].Size + FtSettings.Block - 1) / FtSettings.Block); }
            blockDone = new long[(blocks + 63) / 64];
            topMap.Clear(); Resumed = 0;
            statePath = System.IO.Path.Combine(Dest, ".fadi-transfer-" + StateId() + ".txt");
            bool loaded = false;
            try
            {
                if (File.Exists(statePath))
                {
                    string[] l = File.ReadAllLines(statePath, Encoding.UTF8);
                    foreach (string line in l)
                    {
                        if (line.StartsWith("top\t")) { string[] p = line.Split('\t'); if (p.Length == 3) topMap[p[1]] = p[2]; }
                        else if (line.StartsWith("done\t")) { byte[] bits = Convert.FromBase64String(line.Substring(5)); if (bits.Length == blockDone.Length * 8) Buffer.BlockCopy(bits, 0, blockDone, 0, bits.Length); }
                    }
                    loaded = true;
                }
            }
            catch { blockDone = new long[(blocks + 63) / 64]; topMap.Clear(); loaded = false; }
            if (loaded)
            {
                // a piece counts only when its file is still there
                for (int i = 0; i < Files.Count; i++)
                {
                    bool there = File.Exists(Part(i)) && new FileInfo(Part(i)).Length == Files[i].Size;
                    for (int b = 0; b < BlocksOf(i); b++)
                    {
                        int k = blockBase[i] + b;
                        if (!IsDone(k)) continue;
                        if (!there) { SetDone(k, false); continue; }
                        Resumed += BlockLen(i, b);
                    }
                }
            }
            else
            {
                // first try: a name that's already taken in the folder gets " (2)"
                foreach (FtFile f in Files)
                {
                    int s = f.Rel.IndexOf('/');
                    string top = s < 0 ? f.Rel : f.Rel.Substring(0, s);
                    if (topMap.ContainsKey(top)) continue;
                    string pick = top; int n = 2;
                    while (File.Exists(System.IO.Path.Combine(Dest, pick)) || Directory.Exists(System.IO.Path.Combine(Dest, pick)) || File.Exists(System.IO.Path.Combine(Dest, pick) + ".fadipart"))
                        pick = (s < 0 ? System.IO.Path.GetFileNameWithoutExtension(top) : top) + " (" + (n++) + ")" + (s < 0 ? System.IO.Path.GetExtension(top) : "");
                    topMap[top] = pick;
                }
            }
            if (Files.Count > 0) { string r0 = Files[0].Rel; int s0 = r0.IndexOf('/'); Top = Mapped(s0 < 0 ? r0 : r0.Substring(0, s0)); }
        }

        int BlocksOf(int i) { return (int)((Files[i].Size + FtSettings.Block - 1) / FtSettings.Block); }
        int BlockLen(int i, int b) { return (int)Math.Min(FtSettings.Block, Files[i].Size - (long)b * FtSettings.Block); }
        bool IsDone(int k) { return (Interlocked.Read(ref blockDone[k >> 6]) & (1L << (k & 63))) != 0; }
        void SetDone(int k, bool on)
        {
            while (true)
            {
                long o = Interlocked.Read(ref blockDone[k >> 6]);
                long n = on ? o | (1L << (k & 63)) : o & ~(1L << (k & 63));
                if (Interlocked.CompareExchange(ref blockDone[k >> 6], n, o) == o) return;
            }
        }

        void SaveState()
        {
            try
            {
                // what's written reaches the disk before it's counted as done
                if (streams != null) for (int i = 0; i < streams.Length; i++) if (streams[i] != null) lock (locks[i]) try { streams[i].Flush(); } catch { }
                StringBuilder b = new StringBuilder();
                b.Append("Fadi Tools file transfer (carries on from here if it's opened again)\n");
                foreach (KeyValuePair<string, string> kv in topMap) b.Append("top\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                byte[] bits = new byte[blockDone.Length * 8];
                for (int i = 0; i < blockDone.Length; i++) { long v = Interlocked.Read(ref blockDone[i]); for (int j = 0; j < 8; j++) bits[i * 8 + j] = (byte)(v >> (8 * j)); }
                b.Append("done\t").Append(Convert.ToBase64String(bits)).Append('\n');
                string tmp = statePath + ".new";
                File.WriteAllText(tmp, b.ToString(), Encoding.UTF8);
                if (File.Exists(statePath)) File.Delete(statePath);
                File.Move(tmp, statePath);
                try { File.SetAttributes(statePath, FileAttributes.Hidden); } catch { }
            }
            catch { }
        }

        /// Downloads everything into the folder. Returns null when done (every file complete and checked), or why it
        /// stopped. Cancelling keeps what arrived so far for next time.
        public string Run(string dest, Func<bool> cancelled)
        {
            // a second go (after Stop) starts clean: nothing left over from the first one
            stop = false; error = null; Live = 0; Reconnecting = false;
            lock (qLock) todo.Clear();
            Dest = dest;
            Directory.CreateDirectory(dest);
            LoadOrPlan(true);
            Done = Resumed;
            streams = new FileStream[Files.Count];
            locks = new object[Files.Count];
            for (int i = 0; i < Files.Count; i++) locks[i] = new object();
            for (int k = 0; k < blocks; k++) if (!IsDone(k)) todo.Enqueue(k);
            SaveState();
            int n = Math.Max(1, Math.Min(FtSettings.Streams, todo.Count));
            List<Thread> threads = new List<Thread>();
            for (int w = 0; w < n; w++)
            {
                FtChannel c = w == 0 ? first : null;
                if (w == 0) first = null;
                Thread t = new Thread(() => Worker(c, cancelled)) { IsBackground = true, Name = "ft-recv" };
                threads.Add(t); t.Start();
            }
            DateTime lastSave = DateTime.UtcNow;
            while (true)
            {
                bool alive = false;
                foreach (Thread t in threads) if (t.IsAlive) alive = true;
                if (!alive) break;
                if (cancelled != null && cancelled()) stop = true;
                if ((DateTime.UtcNow - lastSave).TotalSeconds > 2) { SaveState(); lastSave = DateTime.UtcNow; }
                Thread.Sleep(150);
            }
            SaveState();
            for (int i = 0; i < streams.Length; i++) if (streams[i] != null) { try { streams[i].Dispose(); } catch { } streams[i] = null; }
            if (error != null) return error;
            if (cancelled != null && cancelled()) return L.T("ft_stopped");
            for (int k = 0; k < blocks; k++) if (!IsDone(k)) return L.T("ft_err_lost");
            // complete: each file gets its real name and date
            for (int i = 0; i < Files.Count; i++)
            {
                string fin = Final(i), part = Part(i);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fin));
                if (Files[i].Size == 0 && !File.Exists(part)) File.WriteAllBytes(part, new byte[0]);
                if (new FileInfo(part).Length != Files[i].Size) return L.T("ft_err_lost");
                if (File.Exists(fin)) File.Delete(fin);
                File.Move(part, fin);
                try { File.SetLastWriteTimeUtc(fin, new DateTime(Files[i].Mtime, DateTimeKind.Utc)); } catch { }
            }
            try { File.Delete(statePath); } catch { }
            // remembering the sender (asked for on the offer): a new key both sides keep
            try
            {
                using (FtChannel c = FtChannel.Connect(Address, Code.Id, Code.Key, 4000))
                {
                    if (Remember && SenderId != null && !Proven)
                    {
                        // becoming friends: a new key both keep, and each other's engine tickets (to reach each other later)
                        byte[] key = FtCrypto.Rand(32), nm = Encoding.UTF8.GetBytes(FtIdentity.Name), tk = Encoding.ASCII.GetBytes(FtPipe.MyTicket ?? "");
                        byte[] q = new byte[8 + 32 + 1 + nm.Length + 2 + tk.Length];
                        int at = 0;
                        Buffer.BlockCopy(FtIdentity.IdBytes, 0, q, at, 8); at += 8;
                        Buffer.BlockCopy(key, 0, q, at, 32); at += 32;
                        q[at++] = (byte)nm.Length; Buffer.BlockCopy(nm, 0, q, at, nm.Length); at += nm.Length;
                        q[at++] = (byte)(tk.Length >> 8); q[at++] = (byte)tk.Length; Buffer.BlockCopy(tk, 0, q, at, tk.Length);
                        c.Send(FtChannel.Pair, q);
                        byte[] ok = c.Receive();
                        if (ok[0] == FtChannel.PairOk)
                        {
                            string their = ok.Length > 1 ? Encoding.ASCII.GetString(ok, 1, ok.Length - 1) : null;
                            FtTrust.Put(new FtTrust.Entry { Id = SenderId, Name = SenderName ?? "", Key = key, Auto = true, Ticket = string.IsNullOrEmpty(their) ? SenderTicket : their, LastGot = DateTime.UtcNow.Ticks });
                            FtPipe.Refresh();
                        }
                    }
                    else if (Proven) FtTrust.Update(SenderId, e => e.LastGot = DateTime.UtcNow.Ticks);
                    // the sender learns it all arrived
                    c.Send(FtChannel.Done, FtIdentity.IdBytes);
                }
            }
            catch { }
            return null;
        }

        /// Removes what arrived of this transfer in the folder (the unfinished files and the note of where it got to),
        /// and the folders it made that are now empty. Files that were complete before aren't touched.
        public void DeletePartial(string dest)
        {
            Dest = dest;
            LoadOrPlan(false);
            for (int i = 0; i < Files.Count; i++) try { if (File.Exists(Part(i))) File.Delete(Part(i)); } catch { }
            try { if (File.Exists(statePath)) File.Delete(statePath); } catch { }
            foreach (string top in topMap.Values) RemoveEmpty(System.IO.Path.Combine(dest, top));
            Resumed = 0; Done = 0;
        }
        static void RemoveEmpty(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string d in Directory.GetDirectories(dir)) RemoveEmpty(d);
                if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
            }
            catch { }
        }

        FileStream StreamFor(int i)
        {
            lock (locks[i])
            {
                if (streams[i] != null) return streams[i];
                string p = Part(i);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                FileStream fs = new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
                if (fs.Length != Files[i].Size) fs.SetLength(Files[i].Size);   // room for all of it up front
                return streams[i] = fs;
            }
        }

        void Worker(FtChannel c, Func<bool> cancelled)
        {
            int fails = 0;
            Interlocked.Increment(ref working);
            try
            {
                while (!stop)
                {
                    while (Paused && !stop) Thread.Sleep(100);
                    if (stop) return;
                    int k;
                    lock (qLock) { if (todo.Count == 0) return; k = todo.Dequeue(); }
                    int fi = Array.BinarySearch(blockBase, k);
                    if (fi < 0) fi = ~fi - 1;
                    while (fi + 1 < blockBase.Length && blockBase[fi + 1] <= k) fi++;
                    int b = k - blockBase[fi];
                    long off = (long)b * FtSettings.Block;
                    int len = BlockLen(fi, b);
                    long got = 0;
                    try
                    {
                        if (c == null)
                        {
                            Reconnecting = fails > 0;
                            c = FtChannel.Connect(Address, Code.Id, Code.Key, 8000);
                        }
                        Interlocked.Increment(ref Live);
                        try
                        {
                            byte[] req = new byte[32];   // the last 8: which PC this is (several can take the same files)
                            Buffer.BlockCopy(FtIdentity.IdBytes, 0, req, 24, 8);
                            Buffer.BlockCopy(BitConverter.GetBytes(fi), 0, req, 0, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes(off), 0, req, 4, 8);
                            Buffer.BlockCopy(BitConverter.GetBytes(len), 0, req, 12, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes(Interlocked.Read(ref Done)), 0, req, 16, 8);
                            c.Send(FtChannel.Get, req);
                            FileStream fs;
                            try { fs = StreamFor(fi); } catch (Exception wx) { throw new FtWriteError(wx); }
                            while (got < len)
                            {
                                if (stop) throw new OperationCanceledException();
                                // paused: nothing more is read, so the sender waits too (it carries on from here)
                                while (Paused && !stop) Thread.Sleep(100);
                                byte[] m = c.Receive();
                                if (m[0] == FtChannel.Err) { error = Encoding.UTF8.GetString(m, 1, m.Length - 1); stop = true; throw new OperationCanceledException(); }
                                if (m[0] != FtChannel.Data) throw new IOException("unexpected");
                                int n = m.Length - 1;
                                if (got + n > len) throw new IOException("too much");
                                try { lock (locks[fi]) { fs.Seek(off + got, SeekOrigin.Begin); fs.Write(m, 1, n); } }
                                catch (Exception wx) { throw new FtWriteError(wx); }
                                got += n;
                                Interlocked.Add(ref Done, n);
                            }
                            SetDone(k, true);
                            fails = 0; Reconnecting = false;
                        }
                        finally { Interlocked.Decrement(ref Live); }
                    }
                    catch (Exception ex)
                    {
                        // this piece goes back in the queue; the connection is made again (a little later each time)
                        Interlocked.Add(ref Done, -got);
                        lock (qLock) todo.Enqueue(k);
                        if (c != null) { c.Dispose(); c = null; }
                        if (stop || ex is OperationCanceledException) return;
                        FtWriteError we = ex as FtWriteError;
                        if (we != null)
                        {
                            // the folder, not the connection: no point trying again
                            int hr = we.InnerException != null ? Marshal.GetHRForException(we.InnerException) : 0;
                            error = hr == unchecked((int)0x80070070) || hr == unchecked((int)0x80070027) ? L.T("ft_err_disk") : L.T("ft_err_write", we.InnerException != null ? we.InnerException.Message : ex.Message);
                            stop = true; return;
                        }
                        fails++;
                        Reconnecting = true;
                        for (int w = 0; w < Math.Min(10, fails) * 10 && !stop && !(cancelled != null && cancelled()); w++) Thread.Sleep(100);
                    }
                }
            }
            finally
            {
                if (c != null) c.Dispose();
                Interlocked.Decrement(ref working);
            }
        }
    }

    // ---------------------------------------------------------------------- the engine (sendme)

    /// The engine used when a PC can't host: n0's "sendme" (MIT / Apache), built on iroh. It connects the two PCs
    /// directly through hole punching when it can, and through a relay when it can't; checks every byte (BLAKE3)
    /// and carries on after a break. Downloaded (about 4 MB) the first time it's needed, asked first.
    public static class FtEngine
    {
        const string Fallback = "https://github.com/n0-computer/sendme/releases/download/v0.37.1/sendme-v0.37.1-windows-x86_64.zip";
        public static string Dir { get { return Path.Combine(Paths.ToolsDir, "sendme"); } }
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static string Exe
        {
            get
            {
                string n = IsWindows ? "sendme.exe" : "sendme";
                try { if (Directory.Exists(Dir)) foreach (string f in Directory.GetFiles(Dir, n, SearchOption.AllDirectories)) return f; } catch { }
                return Tools.FindOnPath(n);
            }
        }
        public static bool Ready { get { return Exe != null; } }

        public static void Install(Action<long, long> progress, Func<bool> cancelled)
        {
            string url = null;
            try { url = Net.GitHubAsset("n0-computer/sendme", @"windows-x86_64\.zip$"); } catch { }
            string zip = Path.Combine(Path.GetTempPath(), "fadi_sendme_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                // GitHub's download servers sometimes answer "busy" (502/503/504) for a while: a few more tries,
                // a little later each time, then a plain explanation instead of the server's words
                for (int attempt = 0; ; attempt++)
                {
                    try { Net.Download(attempt % 2 == 0 ? url ?? Fallback : Fallback, zip, progress, cancelled); break; }
                    catch (WebException wx)
                    {
                        HttpWebResponse r = wx.Response as HttpWebResponse;
                        int code = r != null ? (int)r.StatusCode : 0;
                        bool busy = code >= 500 || wx.Status == WebExceptionStatus.Timeout || wx.Status == WebExceptionStatus.ConnectFailure || wx.Status == WebExceptionStatus.NameResolutionFailure;
                        if (!busy) throw;
                        if (attempt >= 3) throw new UserError(L.T(code >= 500 ? "ft_engine_github_busy" : "ft_engine_offline"));
                        for (int w = 0; w < (attempt + 1) * 30; w++) { if (cancelled != null && cancelled()) throw new OperationCanceledException(); Thread.Sleep(100); }
                    }
                }
                string tmp = Dir + ".new";
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                Directory.CreateDirectory(tmp);
                using (ZipArchive za = ZipFile.OpenRead(zip))
                    foreach (ZipArchiveEntry e in za.Entries)
                        if (e.Name.Equals("sendme.exe", StringComparison.OrdinalIgnoreCase)) e.ExtractToFile(Path.Combine(tmp, "sendme.exe"), true);
                if (!File.Exists(Path.Combine(tmp, "sendme.exe"))) throw new UserError(L.T("ft_engine_bad"));
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
                Directory.Move(tmp, Dir);
            }
            finally { try { File.Delete(zip); } catch { } }
        }

        /// The relay the engine falls back on: its own public ones, unless Settings has "TransferRelay"
        /// (a relay of your own, or "disabled").
        static string Relay { get { string r = Settings.Get("TransferRelay", "").Trim(); return r.Length == 0 ? "" : " --relay " + Tools.Quote(r); } }

        static Process Start(string args, string cwd, bool readOut)
        {
            args += Relay;
            ProcessStartInfo psi = new ProcessStartInfo(Exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd,
                RedirectStandardOutput = readOut, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            return Process.Start(psi);
        }

        /// Shares a file or folder. Returns its ticket once the engine has read the files through (big ones take
        /// a little while), and keeps sharing until stopped.
        public sealed class Sender : IDisposable
        {
            Process p;
            string cwd, staging;
            public string Ticket, Error;
            public bool Running { get { try { return p != null && !p.HasExited; } catch { return false; } } }

            /// One file or folder goes as it is; several are gathered into one folder first (as links: nothing is copied).
            public void Begin(IList<string> items, string friendlyFolder, Func<bool> cancelled)
            {
                string path = items.Count == 1 ? items[0].TrimEnd('\\', '/') : Gather(items, friendlyFolder);
                cwd = Path.Combine(Path.GetTempPath(), "fadi_send_" + Guid.NewGuid().ToString("N").Substring(0, 10));
                Directory.CreateDirectory(cwd);
                // the ticket carries the relay's address only (the two PCs still connect directly when they can):
                // much shorter than one listing every address of this PC
                string tt = Settings.Get("TransferTicket", "Relay");
                p = Start("send " + Tools.Quote(path) + " --no-progress --ticket-type " + tt, cwd, true);
                StringBuilder err = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                string line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (cancelled != null && cancelled()) { Dispose(); return; }
                    int at = line.IndexOf("sendme receive ", StringComparison.Ordinal);
                    if (at >= 0) { Ticket = line.Substring(at + 15).Trim(); break; }
                }
                if (Ticket == null) { lock (err) Error = err.ToString().Trim(); if (string.IsNullOrEmpty(Error)) Error = L.T("ft_engine_failed"); }
                else new Thread(() => { try { while (p.StandardOutput.ReadLine() != null) { } } catch { } }) { IsBackground = true }.Start();
            }

            /// Several items as one folder for the engine, made of hard links next to the first item (same drive,
            /// so nothing is copied). Throws when an item is on another drive.
            string Gather(IList<string> items, string folderName)
            {
                string parent = Path.GetDirectoryName(items[0].TrimEnd('\\', '/'));
                staging = Path.Combine(parent, ".fadi-send-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string root = Path.Combine(staging, folderName);
                Directory.CreateDirectory(root);
                try { File.SetAttributes(staging, FileAttributes.Hidden | FileAttributes.Directory); } catch { }
                string name;
                foreach (FtFile f in FtHost.Collect(items, out name))
                {
                    string dest = Path.Combine(root, f.Rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    if (!Link(dest, f.Path)) throw new UserError(L.T("ft_engine_drives"));
                }
                return root;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CreateHardLink(string link, string existing, IntPtr sec);
            static bool Link(string link, string existing)
            {
                if (IsWindows) return CreateHardLink(link, existing, IntPtr.Zero);
                try { Process.Start("ln", Tools.Quote(existing) + " " + Tools.Quote(link)).WaitForExit(); return File.Exists(link); } catch { return false; }
            }

            public void Dispose()
            {
                try { if (p != null && !p.HasExited) p.Kill(); } catch { }
                try { if (p != null) p.WaitForExit(3000); } catch { }
                p = null;
                // the engine's own temporary folder, and the links (never the files themselves)
                if (cwd != null) try { Directory.Delete(cwd, true); } catch { }
                if (staging != null) try { Directory.Delete(staging, true); } catch { }
            }
        }

        /// Removes the engine's unfinished download in a folder.
        public static void DeletePartial(string dest)
        {
            try { foreach (string d in Directory.GetDirectories(dest, ".sendme-recv-*")) Directory.Delete(d, true); } catch { }
        }

        /// Receives a ticket into a folder. What arrived stays in a hidden folder there until it's complete, so
        /// stopping and trying again (even with a new code for the same files) carries on.
        public static string Receive(string ticket, string dest, Func<bool> cancelled, Action<long> got)
        {
            Directory.CreateDirectory(dest);
            Process p = Start("receive " + ticket + " --no-progress", dest, true);
            StringBuilder err = new StringBuilder();
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.OutputDataReceived += (s, e) => { };
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            string partDir = null;
            while (!p.WaitForExit(500))
            {
                if (cancelled != null && cancelled()) { try { p.Kill(); } catch { } return L.T("ft_stopped"); }
                // how far: the size of the engine's folder for this transfer
                try
                {
                    if (partDir == null) foreach (string d in Directory.GetDirectories(dest, ".sendme-recv-*")) partDir = d;
                    if (partDir != null && got != null) got(AddOns.SizeOf(partDir));
                }
                catch { }
            }
            if (p.ExitCode == 0) return null;
            string e2; lock (err) e2 = err.ToString().Trim();
            return string.IsNullOrEmpty(e2) ? L.T("ft_engine_failed") : e2;
        }
    }

    // ---------------------------------------------------------------------- keeping the PC awake

    /// Keeps Windows from sleeping while something is being sent or received (the screen may still turn off).
    public static class KeepAwake
    {
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
        static int holders;
        /// Call on the window's thread: on, then off again when done.
        public static void Hold(bool on)
        {
            holders = Math.Max(0, holders + (on ? 1 : -1));
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            try { SetThreadExecutionState(holders > 0 ? 0x80000001u : 0x80000000u); } catch { }
        }
    }
}
