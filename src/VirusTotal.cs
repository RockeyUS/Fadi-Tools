using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FileConverter
{
    /// Online check with VirusTotal (70+ antivirus programs). Uses the person's own free key.
    /// Looking a file up only sends its fingerprint (SHA-256); the file itself is sent only when they agree.
    public static class VirusTotal
    {
        public const string JoinUrl = "https://www.virustotal.com/gui/join-us";
        public const string KeyUrl = "https://www.virustotal.com/gui/my-apikey";
        const string Api = "https://www.virustotal.com/api/v3/";
        public const long MaxUpload = 650L * 1024 * 1024;

        public static string Key
        {
            get { return Settings.Get("VtKey", "").Trim(); }
            set { Settings.Set("VtKey", (value ?? "").Trim()); }
        }
        public static bool HasKey { get { return Key.Length >= 32; } }

        public static string ReportUrl(string sha256) { return "https://www.virustotal.com/gui/file/" + sha256; }

        public class Report
        {
            public string Sha256;
            public int Malicious, Suspicious, Clean, Total;
            public DateTime Scanned;
            public string Label;          // e.g. "trojan.agent" (only when something flags it)
            public bool Pending { get { return Total == 0; } }
        }

        /// A problem with the key, the daily limit or the connection, already in plain words.
        public class Problem : Exception { public Problem(string m) : base(m) { } }

        public static string Sha256(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
            using (SHA256 h = SHA256.Create())
                return Hex(h.ComputeHash(fs));
        }
        static string Hex(byte[] b) { StringBuilder sb = new StringBuilder(); foreach (byte x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }

        /// null = VirusTotal has never seen this file.
        public static Report Lookup(string sha256)
        {
            int status;
            string json = Call("GET", Api + "files/" + sha256, null, null, out status);
            if (status == 404) return null;
            return Parse(json, sha256);
        }

        static Report Parse(string json, string sha256)
        {
            object root = new JsonParser(json).Parse();
            object attrs = Get(root, "data", "attributes");
            Report r = new Report { Sha256 = sha256 };
            object stats = Get(attrs, "last_analysis_stats");
            JsonObject so = stats as JsonObject;
            if (so != null)
                foreach (KeyValuePair<string, object> kv in so)
                {
                    int n = ToInt(kv.Value);
                    if (kv.Key == "malicious") r.Malicious = n;
                    else if (kv.Key == "suspicious") r.Suspicious = n;
                    else if (kv.Key == "undetected" || kv.Key == "harmless") r.Clean += n;
                    else continue;   // timeouts and "can't scan this type" aren't counted, like on the website
                    r.Total += n;
                }
            long date = ToLong(Get(attrs, "last_analysis_date"));
            if (date > 0) r.Scanned = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(date).ToLocalTime();
            r.Label = Get(attrs, "popular_threat_classification", "suggested_threat_label") as string;
            return r;
        }

        /// Sends the file to be scanned; returns the scan's id (wait for it with Finished).
        public static string Upload(string path, Action<long, long> progress, Func<bool> cancelled)
        {
            long size = new FileInfo(path).Length;
            if (size > MaxUpload) throw new Problem(L.T("vt_too_big"));
            string url = Api + "files";
            int status;
            if (size > 32L * 1024 * 1024)
            {
                // big files go to a one-time address
                string j = Call("GET", Api + "files/upload_url", null, null, out status);
                url = Get(new JsonParser(j).Parse(), "data") as string;
                if (string.IsNullOrEmpty(url)) throw new Problem(L.T("vt_failed", "upload_url"));
            }
            string boundary = "----FadiTools" + Guid.NewGuid().ToString("N");
            HttpWebRequest req = Req("POST", url);
            req.ContentType = "multipart/form-data; boundary=" + boundary;
            req.Timeout = 30 * 60 * 1000; req.ReadWriteTimeout = 5 * 60 * 1000;
            req.AllowWriteStreamBuffering = false;
            string name = Path.GetFileName(path).Replace("\"", "'");
            byte[] head = Encoding.UTF8.GetBytes("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" + name + "\"\r\nContent-Type: application/octet-stream\r\n\r\n");
            byte[] tail = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
            req.ContentLength = head.Length + size + tail.Length;
            try
            {
                using (Stream rs = req.GetRequestStream())
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    rs.Write(head, 0, head.Length);
                    byte[] buf = new byte[256 * 1024]; int n; long sent = 0;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled != null && cancelled()) { req.Abort(); throw new OperationCanceledException(); }
                        rs.Write(buf, 0, n); sent += n;
                        if (progress != null) progress(sent, size);
                    }
                    rs.Write(tail, 0, tail.Length);
                }
                string json = Read(req, out status);
                object id = Get(new JsonParser(json).Parse(), "data", "id");
                if (id == null) throw new Problem(L.T("vt_failed", status.ToString()));
                return id.ToString();
            }
            catch (WebException ex) { throw Explain(ex); }
        }

        /// True once VirusTotal has finished scanning an upload.
        public static bool Finished(string analysisId)
        {
            int status;
            string json = Call("GET", Api + "analyses/" + analysisId, null, null, out status);
            return (Get(new JsonParser(json).Parse(), "data", "attributes", "status") as string) == "completed";
        }

        // ------------------------------------------------------------------ plumbing
        static HttpWebRequest Req(string method, string url)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.UserAgent = "FadiTools/" + Shell.Version;
            req.Headers["x-apikey"] = Key;
            req.Accept = "application/json";
            req.Timeout = 30000; req.ReadWriteTimeout = 30000;
            return req;
        }

        static string Call(string method, string url, byte[] body, string type, out int status)
        {
            if (!HasKey) throw new Problem(L.T("vt_no_key"));
            HttpWebRequest req = Req(method, url);
            try
            {
                if (body != null)
                {
                    req.ContentType = type; req.ContentLength = body.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                }
                return Read(req, out status);
            }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 404) { status = 404; return null; }
                throw Explain(ex);
            }
        }

        static string Read(HttpWebRequest req, out int status)
        {
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                status = (int)resp.StatusCode;
                return sr.ReadToEnd();
            }
        }

        static Exception Explain(WebException ex)
        {
            HttpWebResponse r = ex.Response as HttpWebResponse;
            if (r == null) return new Problem(L.T("vt_offline"));
            int code = (int)r.StatusCode;
            if (code == 401 || code == 403) return new Problem(L.T("vt_bad_key"));
            if (code == 429) return new Problem(L.T("vt_quota"));
            if (code == 413) return new Problem(L.T("vt_too_big"));
            return new Problem(L.T("vt_failed", code.ToString()));
        }

        static object Get(object o, params string[] keys)
        {
            foreach (string k in keys)
            {
                JsonObject jo = o as JsonObject;
                if (jo == null) return null;
                object next = null;
                foreach (KeyValuePair<string, object> kv in jo) if (kv.Key == k) { next = kv.Value; break; }
                o = next;
            }
            return o;
        }
        static int ToInt(object o) { return (int)ToLong(o); }
        static long ToLong(object o)
        {
            if (o is long) return (long)o;
            if (o is double) return (long)(double)o;
            long l; if (o != null && long.TryParse(o.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return 0;
        }
    }
}
