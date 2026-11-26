using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FileConverter
{
    /// A PC with Fadi Tools on the same network.
    public class FtPeer
    {
        public string Id, Name;
        public IPAddress Address;
        public int Port;
        public DateTime Seen;
        /// The offer came from a friend, through the engine (proven).
        public bool Friend;
    }

    /// PCs on the same network find each other (no server, no code): each running Fadi Tools says "here I am" on
    /// the network every few seconds (a small UDP broadcast), and keeps a door open for offers. Sending to one of
    /// them hosts the files as usual and hands it the code directly; it still has to be accepted on that PC.
    public static class FtLan
    {
        public static int Port { get { int p; return int.TryParse(Settings.Get("TransferLanPort", "47820"), out p) && p > 1024 && p < 65536 ? p : 47820; } }
        /// This PC shows up for others (and sees them); Settings "TransferLan".
        public static bool On { get { return Settings.Get("TransferLan", "1") == "1"; } set { Settings.Set("TransferLan", value ? "1" : "0"); Apply(); } }

        static readonly Dictionary<string, FtPeer> peers = new Dictionary<string, FtPeer>();
        static readonly object lk = new object();
        static UdpClient udp;
        static TcpListener inbox;
        static Timer announce;
        static volatile bool running;

        /// The PCs seen in the last 15 seconds, by name.
        public static List<FtPeer> Peers
        {
            get
            {
                List<FtPeer> l = new List<FtPeer>();
                lock (lk) foreach (FtPeer p in peers.Values) if ((DateTime.UtcNow - p.Seen).TotalSeconds < 15) l.Add(p);
                l.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                return l;
            }
        }
        /// The list changed (worker thread).
        public static event Action PeersChanged;
        /// An offer came in: (from, code text). Return "OK" to take it, or "BUSY" / "NO". Worker thread.
        public static Func<FtPeer, string, string> OfferReceived;

        /// Starts or stops (after the setting changes, and when the app starts).
        public static void Apply()
        {
            if (SelfTest.Active) return;
            StartInbox();
            if (On) Start(); else Stop();
            FtPipe.Refresh();
        }

        static volatile bool inboxRunning;
        /// The door for offers: from PCs on the network (shown to accept), and from friends through the engine
        /// (those arrive from this PC itself and must prove they're a friend).
        static void StartInbox()
        {
            if (inboxRunning) return;
            try
            {
                // the network's PCs reach it only when "show this PC" is on; friends always (through 127.0.0.1)
                inbox = new TcpListener(IPAddress.Any, Port);
                inbox.Start(8);
                inboxRunning = true;
                new Thread(Inbox) { IsBackground = true, Name = "ft-inbox" }.Start();
            }
            catch { inbox = null; }
        }

        static void Start()
        {
            if (running) return;
            try
            {
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.EnableBroadcast = true;
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            }
            catch { Stop(); return; }
            running = true;
            new Thread(Listen) { IsBackground = true, Name = "ft-lan" }.Start();
            announce = new Timer(_ => Hello(false), null, 300, 4000);
            Hello(true);
        }

        static void Stop()
        {
            running = false;
            if (announce != null) { announce.Dispose(); announce = null; }
            if (udp != null) { try { udp.Close(); } catch { } udp = null; }
            lock (lk) peers.Clear();
            Fire();
        }

        static void Fire() { Action a = PeersChanged; if (a != null) try { a(); } catch { } }

        /// "FADIFT1 hello <id> <port> <ask> <name in base64>": ask=1 asks everyone to answer at once.
        static void Hello(bool ask)
        {
            UdpClient u = udp;
            if (u == null || !running) return;
            byte[] msg = Encoding.ASCII.GetBytes("FADIFT1 hello " + FtIdentity.Id + " " + Port + " " + (ask ? "1" : "0") + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(FtIdentity.Name)));
            foreach (IPAddress b in Broadcasts())
                try { u.Send(msg, msg.Length, new IPEndPoint(b, Port)); } catch { }
            // a PC that left: off the list
            bool gone = false;
            lock (lk)
                foreach (string k in new List<string>(peers.Keys))
                    if ((DateTime.UtcNow - peers[k].Seen).TotalSeconds > 15) { peers.Remove(k); gone = true; }
            if (gone) Fire();
        }

        /// Everyone on the network: the general broadcast and each home network's own.
        static List<IPAddress> Broadcasts()
        {
            List<IPAddress> l = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask == null) continue;
                        byte[] ip = a.Address.GetAddressBytes(), m = a.IPv4Mask.GetAddressBytes(), bc = new byte[4];
                        for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~m[i]);
                        IPAddress x = new IPAddress(bc);
                        if (!l.Contains(x)) l.Add(x);
                    }
                }
            }
            catch { }
            return l;
        }

        static void Listen()
        {
            while (running)
            {
                try
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    UdpClient u = udp;
                    if (u == null) return;
                    byte[] b = u.Receive(ref from);
                    string[] p = Encoding.ASCII.GetString(b).Split(' ');
                    if (p.Length < 6 || p[0] != "FADIFT1" || p[1] != "hello" || p[2] == FtIdentity.Id || !Upnp.Private(from.Address)) continue;
                    int port; if (!int.TryParse(p[3], out port)) continue;
                    string name = Encoding.UTF8.GetString(Convert.FromBase64String(p[5]));
                    bool isNew;
                    lock (lk)
                    {
                        FtPeer old;
                        isNew = !peers.TryGetValue(p[2], out old) || old.Name != name || !old.Address.Equals(from.Address);
                        peers[p[2]] = new FtPeer { Id = p[2], Name = name.Length > 40 ? name.Substring(0, 40) : name, Address = from.Address, Port = port, Seen = DateTime.UtcNow };
                    }
                    if (p[4] == "1") Hello(false);   // a PC that just appeared asked: answer now
                    if (isNew) Fire();
                }
                catch { if (!running) return; Thread.Sleep(200); }
            }
        }

        /// Offers from PCs on the network: one line "FADIFT1 offer <id> <name in base64> <code>"; the answer is one word.
        static void Inbox()
        {
            while (inboxRunning)
            {
                TcpClient c;
                try { TcpListener l = inbox; if (l == null) return; c = l.AcceptTcpClient(); } catch { Thread.Sleep(200); continue; }
                new Thread(() =>
                {
                    try
                    {
                        using (c)
                        {
                            IPEndPoint from = c.Client.RemoteEndPoint as IPEndPoint;
                            if (from == null || !Upnp.Private(from.Address)) return;   // only from the home network (or the engine, on this PC)
                            bool viaEngine = IPAddress.IsLoopback(from.Address);
                            if (!viaEngine && !On) return;
                            c.ReceiveTimeout = 5000; c.SendTimeout = 5000;
                            NetworkStream ns = c.GetStream();
                            string line = ReadLine(ns, 8192);
                            string[] p = line == null ? new string[0] : line.Split(' ');
                            string answer = "NO";
                            // "are you there?" (the green dot next to a friend): any PC running Fadi Tools answers
                            if (p.Length >= 2 && p[0] == "FADIFT1" && p[1] == "ping") answer = "PONG";
                            if (p.Length >= 5 && p[0] == "FADIFT1" && p[1] == "offer" && OfferReceived != null)
                            {
                                string nm;
                                try { nm = Encoding.UTF8.GetString(Convert.FromBase64String(p[3])); } catch { nm = null; }
                                FtTrust.Entry friend = null;
                                if (viaEngine)
                                {
                                    // through the engine: only a friend, proven with the key both keep
                                    friend = FtTrust.Find(p[2]);
                                    if (friend == null || p.Length < 6 || !FtCrypto.Same(Unhex(p[5]), FtCrypto.Mac(friend.Key, FtCrypto.Ascii("offer"), Encoding.ASCII.GetBytes(p[4])))) nm = null;
                                    else
                                    {
                                        nm = friend.Shown;
                                        // their newest engine ticket rides along
                                        if (p.Length >= 7 && p[6].Length > 10) { string tk = p[6]; FtTrust.Update(friend.Id, e => e.Ticket = tk); }
                                    }
                                }
                                if (nm != null)
                                {
                                    FtPeer who = new FtPeer { Id = p[2], Name = nm.Length > 40 ? nm.Substring(0, 40) : nm, Address = from.Address, Seen = DateTime.UtcNow, Friend = friend != null };
                                    answer = OfferReceived(who, p[4]) ?? "NO";
                                }
                            }
                            byte[] a = Encoding.ASCII.GetBytes(answer + "\n");
                            ns.Write(a, 0, a.Length);
                        }
                    }
                    catch { }
                }) { IsBackground = true }.Start();
            }
        }

        static string ReadLine(Stream s, int max)
        {
            StringBuilder b = new StringBuilder();
            while (b.Length < max)
            {
                int x = s.ReadByte();
                if (x < 0) return b.Length > 0 ? b.ToString() : null;
                if (x == '\n') return b.ToString();
                b.Append((char)x);
            }
            return null;
        }

        static byte[] Unhex(string h)
        {
            try { byte[] b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = byte.Parse(h.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture); return b; }
            catch { return new byte[0]; }
        }

        /// Hands a code to a PC on the network. Its answer: "OK" (shown there), "BUSY" (receiving something else), "NO".
        public static string SendOffer(FtPeer to, string code) { return SendOffer(to.Address, to.Port, code, null); }

        /// Hands a code over (to a friend through the engine when friendKey is set: it carries the proof, and this
        /// PC's newest engine ticket).
        public static string SendOffer(IPAddress address, int port, string code, byte[] friendKey)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(address, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) return "NO";
                    c.EndConnect(ar);
                    c.ReceiveTimeout = 8000; c.SendTimeout = 5000;
                    NetworkStream ns = c.GetStream();
                    string line = "FADIFT1 offer " + FtIdentity.Id + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(FtIdentity.Name)) + " " + code;
                    if (friendKey != null) line += " " + FtCode.Hex(FtCrypto.Mac(friendKey, FtCrypto.Ascii("offer"), Encoding.ASCII.GetBytes(code))) + " " + (FtPipe.MyTicket ?? "-");
                    byte[] msg = Encoding.ASCII.GetBytes(line + "\n");
                    ns.Write(msg, 0, msg.Length);
                    return (ReadLine(ns, 64) ?? "NO").Trim();
                }
            }
            catch { return "NO"; }
        }

        /// Sends one line and returns the one-line answer, or null when nothing answered.
        public static string Ask(IPAddress address, int port, string line, int timeoutMs)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(address, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(Math.Min(5000, timeoutMs))) return null;
                    c.EndConnect(ar);
                    c.ReceiveTimeout = timeoutMs; c.SendTimeout = 5000;
                    NetworkStream ns = c.GetStream();
                    byte[] msg = Encoding.ASCII.GetBytes(line + "\n");
                    ns.Write(msg, 0, msg.Length);
                    string a = ReadLine(ns, 64);
                    return a == null ? null : a.Trim();
                }
            }
            catch { return null; }
        }

        /// This PC's home network addresses that reach a given PC (the same network first).
        public static List<IPAddress> AddressesFor(IPAddress peer)
        {
            List<IPAddress> same = new List<IPAddress>(), other = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork || a.IPv4Mask == null) continue;
                        byte[] ip = a.Address.GetAddressBytes(), m = a.IPv4Mask.GetAddressBytes(), pe = peer.GetAddressBytes();
                        bool match = true;
                        for (int i = 0; i < 4; i++) if ((ip[i] & m[i]) != (pe[i] & m[i])) match = false;
                        (match ? same : other).Add(a.Address);
                    }
                }
            }
            catch { }
            same.AddRange(other);
            return same;
        }
    }

    /// The friends engine: n0's "dumbpipe" (MIT / Apache, the makers of the transfer engine). Each PC gets an id that
    /// never changes (made from a secret kept in the settings), so a friend's PC can be reached wherever it is:
    /// directly when the two can connect, through n0's free relay when they can't. Here it only carries the offer
    /// (the code); the files go the usual way. Downloaded the first time it's needed, asked first.
    public static class FtPipe
    {
        const string Fallback = "https://github.com/n0-computer/dumbpipe/releases/download/v0.40.1/dumbpipe-v0.40.1-windows-x86_64.zip";
        public static string Dir { get { return Path.Combine(Paths.ToolsDir, "dumbpipe"); } }
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }
        public static string Exe
        {
            get
            {
                string n = IsWindows ? "dumbpipe.exe" : "dumbpipe";
                try { if (Directory.Exists(Dir)) foreach (string f in Directory.GetFiles(Dir, n, SearchOption.AllDirectories)) return f; } catch { }
                return Tools.FindOnPath(n);
            }
        }
        public static bool Ready { get { return Exe != null; } }

        public static void Install(Action<long, long> progress, Func<bool> cancelled)
        {
            string url = null;
            try { url = Net.GitHubAsset("n0-computer/dumbpipe", @"windows-x86_64\.zip$"); } catch { }
            string zip = Path.Combine(Path.GetTempPath(), "fadi_dumbpipe_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { Net.Download(attempt % 2 == 0 ? url ?? Fallback : Fallback, zip, progress, cancelled); break; }
                    catch (WebException wx)
                    {
                        HttpWebResponse r = wx.Response as HttpWebResponse;
                        int code = r != null ? (int)r.StatusCode : 0;
                        if (!(code >= 500 || wx.Status == WebExceptionStatus.Timeout || wx.Status == WebExceptionStatus.ConnectFailure)) throw;
                        if (attempt >= 3) throw new UserError(L.T(code >= 500 ? "ft_engine_github_busy" : "ft_engine_offline"));
                        Thread.Sleep((attempt + 1) * 3000);
                    }
                }
                string tmp = Dir + ".new";
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                Directory.CreateDirectory(tmp);
                using (System.IO.Compression.ZipArchive za = System.IO.Compression.ZipFile.OpenRead(zip))
                    foreach (System.IO.Compression.ZipArchiveEntry e in za.Entries)
                        if (e.Name.Equals("dumbpipe.exe", StringComparison.OrdinalIgnoreCase)) System.IO.Compression.ZipFileExtensions.ExtractToFile(e, Path.Combine(tmp, "dumbpipe.exe"), true);
                if (!File.Exists(Path.Combine(tmp, "dumbpipe.exe"))) throw new UserError(L.T("ft_engine_bad"));
                try { if (Directory.Exists(Dir)) Directory.Delete(Dir, true); } catch { }
                Directory.Move(tmp, Dir);
            }
            finally { try { File.Delete(zip); } catch { } }
            Refresh();
        }

        /// This PC's secret for the engine (made once): the id friends reach it by comes from it.
        static string Secret
        {
            get
            {
                string s = Settings.Get("TransferPipeSecret", "");
                if (s.Length != 64) { s = FtCode.Hex(FtCrypto.Rand(32)); Settings.Set("TransferPipeSecret", s); }
                return s;
            }
        }

        static Process Run(string args, bool readErr)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = readErr, RedirectStandardOutput = true, RedirectStandardInput = true };
            psi.EnvironmentVariables["IROH_SECRET"] = Secret;
            return Process.Start(psi);
        }

        /// The ticket friends use to reach this PC (null until the engine is here). The mailbox's own (which also
        /// says how to reach it right now) when it's running, otherwise the plain one made from the id.
        public static string MyTicket
        {
            get
            {
                string t = live;
                if (t != null) return t;
                t = Settings.Get("TransferPipeTicket", "");
                if (t.Length > 0 && Settings.Get("TransferPipeTicketFor", "") == Secret.Substring(0, 8)) return t;
                if (!Ready) return null;
                try
                {
                    using (Process p = Run("generate-ticket", false))
                    {
                        string o = p.StandardOutput.ReadToEnd().Trim();
                        p.WaitForExit(15000);
                        if (o.StartsWith("endpoint")) { Settings.Set("TransferPipeTicket", o); Settings.Set("TransferPipeTicketFor", Secret.Substring(0, 8)); return o; }
                    }
                }
                catch { }
                return null;
            }
        }

        // ---- the mailbox: friends' offers come in through it (while Fadi Tools runs)
        static Process mailbox;
        static volatile string live;
        static volatile bool wanted;
        static readonly object lk = new object();

        /// Runs the mailbox when there are friends and the engine is here; stops it otherwise.
        public static void Refresh()
        {
            bool want = Ready && !SelfTest.Active && FtTrust.All().Count > 0;
            lock (lk)
            {
                wanted = want;
                if (want && (mailbox == null || HasExited(mailbox))) StartMailbox();
                if (!want && mailbox != null) { Kill(mailbox); mailbox = null; live = null; }
            }
        }
        static bool HasExited(Process p) { try { return p.HasExited; } catch { return true; } }
        static void Kill(Process p) { try { if (!p.HasExited) p.Kill(); } catch { } }

        static void StartMailbox()
        {
            try
            {
                Process p = Run("listen-tcp --host 127.0.0.1:" + FtLan.Port, true);
                mailbox = p;
                p.EnableRaisingEvents = true;
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    int at = e.Data.IndexOf("connect-tcp ", StringComparison.Ordinal);
                    if (at >= 0) { string t = e.Data.Substring(at + 12).Trim(); if (t.StartsWith("endpoint")) live = t; }
                };
                p.Exited += (s, e) =>
                {
                    // it stopped (the network changed, it crashed): started again in a moment while it's wanted
                    live = null;
                    new Thread(() => { Thread.Sleep(5000); lock (lk) if (wanted && mailbox == p) StartMailbox(); }) { IsBackground = true }.Start();
                };
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
            }
            catch { mailbox = null; }
        }

        public static void Shutdown() { lock (lk) { wanted = false; if (mailbox != null) { Kill(mailbox); mailbox = null; } live = null; } }

        /// Hands a code to a friend's PC, wherever it is, through the engine. Its answer: "OK", "BUSY", "NO", or
        /// "OFF" (their PC couldn't be reached: Fadi Tools isn't running there, or it's offline).
        public static string Offer(FtTrust.Entry friend, string code)
        {
            string a = Tunnel(friend, 25, port => FtLan.SendOffer(IPAddress.Loopback, port, code, friend.Key));
            return a == "OK" || a == "BUSY" ? a : "OFF";
        }

        /// Whether a friend's PC can be reached right now (Fadi Tools running there and online). Takes a few seconds.
        public static bool Ping(FtTrust.Entry friend)
        {
            return Tunnel(friend, 14, port => FtLan.Ask(IPAddress.Loopback, port, "FADIFT1 ping " + FtIdentity.Id, 6000)) != null;
        }

        /// Opens a way to the friend's mailbox on a port of this PC, uses it, and closes it. Null when it couldn't.
        static string Tunnel(FtTrust.Entry friend, int seconds, Func<int, string> use)
        {
            if (!Ready || string.IsNullOrEmpty(friend.Ticket)) return null;
            int port;
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Process p = null;
            try
            {
                p = Run("connect-tcp --addr 127.0.0.1:" + port + " " + friend.Ticket, true);
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                // the engine listens here once it's online (a few seconds)
                DateTime until = DateTime.UtcNow.AddSeconds(seconds);
                bool up = false;
                while (DateTime.UtcNow < until && !HasExited(p))
                {
                    try { using (TcpClient t = new TcpClient()) { t.Connect(IPAddress.Loopback, port); up = true; break; } } catch { Thread.Sleep(400); }
                }
                if (!up || HasExited(p)) return null;
                return use(port);
            }
            catch { return null; }
            finally { if (p != null) Kill(p); }
        }
    }

    /// Windows Firewall: one rule that lets Fadi Tools take incoming connections, so Windows never stops a friend
    /// (or asks) in the middle of a transfer. Made once, with Windows' administrator prompt.
    public static class FtFirewall
    {
        const string RuleName = "Fadi Tools File Transfer";
        static int known = -1;   // -1 unknown, 0 no, 1 yes

        static string[] Programs()
        {
            List<string> l = new List<string>();
            try { l.Add(Path.GetFullPath(System.Windows.Forms.Application.ExecutablePath)); } catch { }
            try { if (File.Exists(Paths.InstalledExe) && !l.Exists(x => x.Equals(Paths.InstalledExe, StringComparison.OrdinalIgnoreCase))) l.Add(Paths.InstalledExe); } catch { }
            return l.ToArray();
        }

        /// Whether the rule is there (asks Windows; a moment the first time, then remembered).
        public static bool Allowed
        {
            get
            {
                if (known >= 0) return known == 1;
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) { known = 0; return false; }
                string o;
                try { known = Tools.RunHidden("netsh", "advfirewall firewall show rule name=\"" + RuleName + "\"", 8000, out o) == 0 && (o ?? "").Contains(RuleName) ? 1 : 0; }
                catch { known = 0; }
                return known == 1;
            }
        }

        /// Replaces whatever Windows made for Fadi Tools (an "Allow" or a "Cancel" answered earlier) with one rule that
        /// allows it. Windows asks for administrator permission first. True when the rule is there afterwards.
        public static bool Allow()
        {
            StringBuilder cmd = new StringBuilder("/c ");
            bool first = true;
            foreach (string exe in Programs())
            {
                if (!first) cmd.Append(" & ");
                first = false;
                cmd.Append("netsh advfirewall firewall delete rule name=all dir=in program=\"").Append(exe).Append("\" & ");
                cmd.Append("netsh advfirewall firewall add rule name=\"").Append(RuleName).Append("\" dir=in action=allow enable=yes profile=any program=\"").Append(exe).Append("\"");
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", cmd.ToString()) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (Process p = Process.Start(psi)) p.WaitForExit(30000);
            }
            catch { return false; }   // the administrator prompt was declined
            known = -1;
            return Allowed;
        }
    }
}
