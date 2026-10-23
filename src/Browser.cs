using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace FileConverter
{
    /// A real Microsoft Edge window (kept off the screen) that Fadi Tools drives itself, for the few sites that only
    /// let real browsers in (KLIPY, behind Cloudflare): it opens the page like any browser, waits until the page is
    /// really there, and reads it — or a file from it. It has its own empty profile and closes when done.
    public sealed class BrowserSession : IDisposable
    {
        Process proc;
        string profile;
        ClientWebSocket ws;
        int nextId = 1;

        /// Starts Edge on a page; null when Edge isn't here or doesn't answer.
        public static BrowserSession Start(string url)
        {
            string edge = Environment.GetEnvironmentVariable("FADI_EDGE") ?? Tools.Edge;
            if (edge == null || !File.Exists(edge)) return null;
            BrowserSession s = new BrowserSession();
            try
            {
                int port = FreePort();
                s.profile = Path.Combine(Path.GetTempPath(), "fadi_browser_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(s.profile);
                string args = "--remote-debugging-port=" + port + " --user-data-dir=" + Tools.Quote(s.profile) +
                              " --no-first-run --no-default-browser-check --disable-extensions --disable-sync --mute-audio" +
                              " --window-position=-32000,-32000 --window-size=1100,800 --new-window " + Tools.Quote(url);
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) args += " --no-sandbox --headless=new";   // (tests)
                s.proc = Process.Start(new ProcessStartInfo(edge, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Minimized });
                // the page's control line (DevTools), once Edge is listening
                string wsUrl = null;
                for (int i = 0; i < 60 && wsUrl == null; i++)
                {
                    Thread.Sleep(250);
                    try
                    {
                        using (WebClient wc = new WebClient())
                        {
                            wc.Proxy = null;
                            string list = wc.DownloadString("http://127.0.0.1:" + port + "/json/list");
                            object parsed = new JsonParser(list).Parse();
                            List<object> targets = parsed as List<object>;
                            if (targets != null)
                                foreach (object t in targets)
                                {
                                    JsonObject o = t as JsonObject;
                                    if (o != null && Str(o, "type") == "page" && Str(o, "webSocketDebuggerUrl") != null) { wsUrl = Str(o, "webSocketDebuggerUrl"); break; }
                                }
                        }
                    }
                    catch { }
                }
                if (wsUrl == null) { s.Dispose(); return null; }
                s.ws = new ClientWebSocket();
                s.ws.Options.Proxy = null;
                s.ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait(10000);
                if (s.ws.State != WebSocketState.Open) { s.Dispose(); return null; }
                return s;
            }
            catch { s.Dispose(); return null; }
        }

        static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        static string Str(JsonObject o, string key) { foreach (KeyValuePair<string, object> kv in o) if (kv.Key == key) return kv.Value as string; return null; }
        static object Get(JsonObject o, string key) { foreach (KeyValuePair<string, object> kv in o) if (kv.Key == key) return kv.Value; return null; }

        static string Json(string s)
        {
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        /// Sends one DevTools command and waits for its answer (the "result" object).
        JsonObject Call(string method, string paramsJson, int timeoutMs)
        {
            int id = nextId++;
            byte[] msg = Encoding.UTF8.GetBytes("{\"id\":" + id + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}");
            ws.SendAsync(new ArraySegment<byte>(msg), WebSocketMessageType.Text, true, CancellationToken.None).Wait(timeoutMs);
            DateTime until = DateTime.Now.AddMilliseconds(timeoutMs);
            byte[] buf = new byte[1 << 16];
            while (DateTime.Now < until)
            {
                MemoryStream ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                    if (!t.Wait(Math.Max(100, (int)(until - DateTime.Now).TotalMilliseconds))) return null;
                    r = t.Result;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                JsonObject o = new JsonParser(Encoding.UTF8.GetString(ms.ToArray())).Parse() as JsonObject;
                if (o == null) continue;
                object oid = Get(o, "id");
                if (oid == null || Convert.ToInt32(oid) != id) continue;   // an event, or another answer
                return Get(o, "result") as JsonObject;
            }
            return null;
        }

        /// Runs JavaScript in the page; its value as text (null on failure).
        public string Eval(string js, int timeoutMs = 20000)
        {
            JsonObject res = Call("Runtime.evaluate", "{\"expression\":" + Json(js) + ",\"returnByValue\":true,\"awaitPromise\":true}", timeoutMs);
            if (res == null) return null;
            JsonObject val = Get(res, "result") as JsonObject;
            if (val == null) return null;
            object v = Get(val, "value");
            return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Navigate(string url) { Call("Page.navigate", "{\"url\":" + Json(url) + "}", 15000); }

        /// The page's HTML once it has the thing being waited for (a CSS selector), within the time given.
        public string HtmlWhen(string selector, int timeoutMs)
        {
            DateTime until = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < until)
            {
                string h = Eval("document.querySelector(" + Json(selector) + ") ? document.documentElement.outerHTML : ''", 5000);
                if (!string.IsNullOrEmpty(h)) return h;
                Thread.Sleep(500);
            }
            return null;
        }

        /// Saves a file the browser can reach (it goes to the file's own address, then reads it from there).
        public bool SaveFile(string fileUrl, string dest, int timeoutMs)
        {
            Navigate(fileUrl);
            DateTime until = DateTime.Now.AddMilliseconds(timeoutMs);
            string js = "(async()=>{try{const r=await fetch(location.href,{credentials:'include'});if(!r.ok)return '';" +
                        "const b=new Uint8Array(await r.arrayBuffer());let s='';for(let i=0;i<b.length;i+=32768){s+=String.fromCharCode.apply(null,b.subarray(i,i+32768));}" +
                        "return btoa(s);}catch(e){return '';}})()";
            while (DateTime.Now < until)
            {
                Thread.Sleep(700);
                string here = Eval("location.href", 3000) ?? "";
                if (here.IndexOf("://", StringComparison.Ordinal) < 0 || Eval("document.title", 3000) == "Just a moment...") continue;
                string b64 = Eval(js, Math.Max(5000, (int)(until - DateTime.Now).TotalMilliseconds));
                if (string.IsNullOrEmpty(b64)) continue;
                try { File.WriteAllBytes(dest, Convert.FromBase64String(b64)); return new FileInfo(dest).Length > 0; } catch { return false; }
            }
            return false;
        }

        public void Dispose()
        {
            try { if (ws != null && ws.State == WebSocketState.Open) ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).Wait(1000); } catch { }
            try { if (ws != null) ws.Dispose(); } catch { }
            try { if (proc != null && !proc.HasExited) { Tools.KillTree(proc); proc.WaitForExit(3000); } } catch { }
            if (profile != null)
                for (int i = 0; i < 10; i++) { try { if (Directory.Exists(profile)) Directory.Delete(profile, true); break; } catch { Thread.Sleep(300); } }
        }
    }
}
