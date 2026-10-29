using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    public enum MediaKind { Video, Gif, Image, Audio }

    /// Handles links that yt-dlp doesn't (or handles poorly): direct files, GIF sites and Pinterest pins.
    public static class Resolver
    {
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
        static readonly string[] VideoExt = { "mp4", "webm", "mov", "m4v", "mkv" };
        static readonly string[] ImgExt = { "jpg", "jpeg", "png", "webp", "avif", "bmp" };

        public static string Site(string url)
        {
            string u = url.ToLowerInvariant();
            if (u.Contains("youtube.com") || u.Contains("youtu.be")) return "YouTube";
            if (u.Contains("pinterest.") || u.Contains("pin.it")) return "Pinterest";
            if (u.Contains("giphy.com")) return "GIPHY";
            if (u.Contains("tenor.com")) return "Tenor";
            if (u.Contains("klipy.com")) return "KLIPY";
            if (u.Contains("discordapp.") || u.Contains("discord.com") || u.Contains("discord.gg")) return "Discord";
            if (u.Contains("twitter.com") || u.Contains("x.com/")) return "X";
            if (u.Contains("reddit.com") || u.Contains("redd.it")) return "Reddit";
            if (u.Contains("imgur.com")) return "Imgur";
            if (u.Contains("tiktok.com")) return "TikTok";
            if (u.Contains("instagram.com")) return "Instagram";
            if (u.Contains("facebook.com") || u.Contains("fb.watch")) return "Facebook";
            if (u.Contains("vimeo.com")) return "Vimeo";
            try { return new Uri(url).Host.Replace("www.", ""); } catch { return ""; }
        }

        static string ExtOf(string url)
        {
            try { return Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.').ToLowerInvariant(); } catch { return ""; }
        }

        public static bool IsDirectMedia(string url)
        {
            string e = ExtOf(url);
            return e == "gif" || Array.IndexOf(VideoExt, e) >= 0 || Array.IndexOf(ImgExt, e) >= 0;
        }

        // ------------------------------------------------------------------ image search results and links that wrap a picture
        static string Param(string url, params string[] names)
        {
            int q = url.IndexOf('?');
            if (q < 0) return null;
            string query = url.Substring(q + 1);
            int h = query.IndexOf('#'); if (h >= 0) query = query.Substring(0, h);
            foreach (string name in names)
                foreach (string part in query.Split('&'))
                {
                    int eq = part.IndexOf('=');
                    if (eq <= 0 || !part.Substring(0, eq).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    string v = part.Substring(eq + 1).Replace('+', ' ');
                    try { v = Uri.UnescapeDataString(v); } catch { }
                    if (v.StartsWith("//")) v = "https:" + v;
                    if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return v.Trim();
                }
            return null;
        }

        static bool IsGoogle(string host) { return Regex.IsMatch(host, @"(^|\.)google\.[a-z.]+$"); }

        /// The real address inside a link that only points at it: a Google / Bing / Yandex / DuckDuckGo image result
        /// ("Copy link address" on a picture in the results), or a search engine's redirect. Anything else comes back as it is.
        public static string Unwrap(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            url = url.Trim();
            for (int round = 0; round < 3; round++)
            {
                string host;
                try { host = new Uri(url).Host.ToLowerInvariant(); } catch { return url; }
                string path; try { path = new Uri(url).AbsolutePath.ToLowerInvariant(); } catch { path = ""; }
                string inner = null;
                if (IsGoogle(host) && path.StartsWith("/imgres")) inner = Param(url, "imgurl", "imgrefurl");
                else if (IsGoogle(host) && path.StartsWith("/url")) inner = Param(url, "url", "q");
                else if (host.EndsWith("bing.com") && path.StartsWith("/images")) inner = Param(url, "mediaurl");
                else if (host.Contains("yandex.") && path.Contains("/images")) inner = Param(url, "img_url");
                else if (host.EndsWith("duckduckgo.com")) inner = Param(url, "u", "uddg");
                else if (host.EndsWith("search.yahoo.com") || host.EndsWith("images.search.yahoo.com")) inner = Param(url, "imgurl");
                if (inner == null || inner == url) return url;
                url = inner;
            }
            return url;
        }

        /// A link copied from a picture in image search results (it carries the picture's address).
        public static bool IsImageResult(string url)
        {
            try
            {
                Uri u = new Uri(url);
                string host = u.Host.ToLowerInvariant(), path = u.AbsolutePath.ToLowerInvariant();
                return (IsGoogle(host) && path.StartsWith("/imgres")) || (host.EndsWith("bing.com") && path.StartsWith("/images") && Param(url, "mediaurl") != null)
                    || (host.Contains("yandex.") && Param(url, "img_url") != null) || host.StartsWith("external-content.duckduckgo");
            }
            catch { return false; }
        }

        /// A Google results page (not a picture): the link copied from the grid of results in some browsers.
        public static bool IsSearchPage(string url)
        {
            try
            {
                Uri u = new Uri(url);
                string host = u.Host.ToLowerInvariant(), path = u.AbsolutePath.ToLowerInvariant();
                return (IsGoogle(host) && path.StartsWith("/search")) || (host.EndsWith("bing.com") && path.StartsWith("/images") && Param(url, "mediaurl") == null);
            }
            catch { return false; }
        }

        /// A picture written right into the link (data:image/png;base64,…): saved to a temporary file.
        public static bool IsDataImage(string s) { return s != null && s.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && s.Contains(";base64,"); }

        static YtInfo FromData(string data)
        {
            int comma = data.IndexOf(',');
            string mime = data.Substring(5, data.IndexOf(';') - 5).ToLowerInvariant();
            string ext = mime.EndsWith("jpeg") || mime.EndsWith("jpg") ? "jpg" : mime.EndsWith("png") ? "png" : mime.EndsWith("gif") ? "gif" : mime.EndsWith("webp") ? "webp" : mime.EndsWith("avif") ? "avif" : mime.EndsWith("svg+xml") ? "svg" : "png";
            byte[] b = Convert.FromBase64String(data.Substring(comma + 1).Trim());
            string tmp = Path.Combine(Path.GetTempPath(), "fadi_pasted_" + Guid.NewGuid().ToString("N").Substring(0, 8) + "." + ext);
            File.WriteAllBytes(tmp, b);
            YtInfo info = new YtInfo();
            info.Url = new Uri(tmp).AbsoluteUri; info.DirectUrl = info.Url; info.DirectExt = ext;
            info.Kind = ext == "gif" ? MediaKind.Gif : MediaKind.Image;
            info.Title = L.T("dl_pasted_picture");
            info.Thumbnail = info.Url; info.DirectSize = b.Length;
            info.Site = L.T("dl_picture");
            return info;
        }

        /// A link with no file name at the end (image CDNs, "images?q=tbn:…"): what the server says it is.
        static YtInfo ByType(string url)
        {
            string type = null; long size = -1;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA; req.Method = "GET"; req.Timeout = 15000; req.AllowAutoRedirect = true;
                req.Accept = "image/avif,image/webp,image/*,video/*,*/*;q=0.8";
                req.AddRange(0L, 0L);
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
                {
                    type = (r.ContentType ?? "").ToLowerInvariant();
                    string cr = r.Headers["Content-Range"]; int slash = cr == null ? -1 : cr.LastIndexOf('/');
                    if (slash >= 0) long.TryParse(cr.Substring(slash + 1), out size);
                }
            }
            catch { return null; }
            string ext = null;
            if (type.StartsWith("image/jpeg") || type.StartsWith("image/jpg")) ext = "jpg";
            else if (type.StartsWith("image/png")) ext = "png";
            else if (type.StartsWith("image/gif")) ext = "gif";
            else if (type.StartsWith("image/webp")) ext = "webp";
            else if (type.StartsWith("image/avif")) ext = "avif";
            else if (type.StartsWith("image/bmp")) ext = "bmp";
            else if (type.StartsWith("video/mp4")) ext = "mp4";
            else if (type.StartsWith("video/webm")) ext = "webm";
            else if (type.StartsWith("video/quicktime")) ext = "mov";
            if (ext == null) return null;
            YtInfo info = new YtInfo();
            info.Url = url; info.DirectUrl = url; info.DirectExt = ext;
            info.Kind = ext == "gif" ? MediaKind.Gif : Array.IndexOf(ImgExt, ext) >= 0 ? MediaKind.Image : MediaKind.Video;
            string name = ""; try { name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath)); } catch { }
            info.Title = name.Length > 2 && name.Length < 80 && !name.Equals("images", StringComparison.OrdinalIgnoreCase) ? name : L.T("dl_picture");
            info.Thumbnail = info.Kind != MediaKind.Video ? url : "";
            info.DirectSize = size;
            info.Site = Site(url);
            return info;
        }

        static readonly Regex VideoSites = new Regex(@"(youtube\.com|youtu\.be|tiktok\.com|instagram\.com|twitter\.com|x\.com|reddit\.com|redd\.it|vimeo\.com|facebook\.com|fb\.watch|twitch\.tv|dailymotion\.com|soundcloud\.com|streamable\.com|bilibili\.com)", RegexOptions.IgnoreCase);

        /// Returns info for links we handle ourselves, or null to let yt-dlp try.
        public static YtInfo TryResolve(string url, Func<bool> cancelled)
        {
            if (IsDataImage(url)) return FromData(url);
            string orig = url;
            url = Unwrap(url);
            if (IsSearchPage(url)) throw new UserError(L.T("dl_search_page"));
            YtInfo own = null;
            try { own = TryKnown(url, cancelled); } catch (UserError) { throw; } catch { }
            if (own != null) return own;
            // a plain picture or video link without a file name (most image hosts): ask the server what it is
            if (!VideoSites.IsMatch(url)) own = ByType(url);
            // the picture itself turned us away: the page it's on (an image result's "imgrefurl") shows it too
            if (own == null && url != orig)
            {
                string page = Param(orig, "imgrefurl");
                if (page != null) try { own = FromPage(page); } catch { }
            }
            return own;
        }

        /// Whether a link needs yt-dlp (the video sites); pictures and GIF sites are fetched without it.
        public static bool NeedsEngine(string url)
        {
            if (IsDataImage(url)) return false;
            url = Unwrap(url);
            string st = Site(url);
            if (st == "GIPHY" || st == "Tenor" || st == "KLIPY" || st == "Discord" || st == "Pinterest" || IsDirectMedia(url)) return false;
            return VideoSites.IsMatch(url);
        }

        static YtInfo TryKnown(string url, Func<bool> cancelled)
        {
            string site = Site(url);
            if (site == "Discord") url = Discord(url);
            if (site == "GIPHY" || Site(url) == "GIPHY") return Giphy(url);
            if (IsDirectMedia(url)) return Direct(url, null, null);
            if (Site(url) == "KLIPY") return Klipy(url);
            if (Site(url) == "Tenor") return Tenor(url);
            if (site == "Pinterest") return Pinterest(url);
            return null;
        }

        /// Used when yt-dlp can't handle a page: looks for og:video / og:image.
        public static YtInfo FromPage(string url)
        {
            string html = Get(url);
            string title = Meta(html, "og:title") ?? Meta(html, "twitter:title") ?? "";
            string video = Meta(html, "og:video:secure_url") ?? Meta(html, "og:video:url") ?? Meta(html, "og:video");
            if (video != null && !video.Contains(".swf")) return Direct(video, title, Meta(html, "og:image"));
            string img = Meta(html, "og:image:secure_url") ?? Meta(html, "og:image");
            if (img != null) return Direct(img, title, img);
            return null;
        }

        // ------------------------------------------------------------------ helpers
        public static string Get(string url) { return Get(url, null); }

        public static string Get(string url, string[] headers)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA;
            if (headers != null)
                for (int i = 0; i + 1 < headers.Length; i += 2)
                {
                    if (headers[i] == "Referer") req.Referer = headers[i + 1];   // a "restricted" header in .NET
                    else req.Headers[headers[i]] = headers[i + 1];
                }
            req.Accept = "text/html,application/json;q=0.9,*/*;q=0.8";
            req.Headers["Accept-Language"] = "en-US,en;q=0.8";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 20000;
            using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string FinalUrl(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA; req.Method = "GET"; req.AllowAutoRedirect = true; req.Timeout = 15000;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse()) return r.ResponseUri.ToString();
            }
            catch { return url; }
        }

        public static long Size(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = UA; req.Method = "HEAD"; req.Timeout = 15000;
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse()) return r.ContentLength;
            }
            catch { return -1; }
        }

        static string Meta(string html, string prop)
        {
            Match m = Regex.Match(html, "<meta[^>]+(?:property|name)=[\"']" + Regex.Escape(prop) + "[\"'][^>]*content=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(html, "<meta[^>]+content=[\"']([^\"']+)[\"'][^>]*(?:property|name)=[\"']" + Regex.Escape(prop) + "[\"']", RegexOptions.IgnoreCase);
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        }

        static YtInfo Direct(string mediaUrl, string title, string thumb)
        {
            string e = ExtOf(mediaUrl);
            if (Array.IndexOf(VideoExt, e) < 0 && Array.IndexOf(ImgExt, e) < 0 && e != "gif" && !mediaUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                // no file name at the end (or an odd one): what the server says it is
                YtInfo t = ByType(mediaUrl);
                if (t != null) { if (!string.IsNullOrEmpty(title)) t.Title = title; if (thumb != null) t.Thumbnail = thumb; return t; }
            }
            YtInfo info = new YtInfo();
            info.Url = mediaUrl;
            info.DirectUrl = mediaUrl;
            info.Site = Site(mediaUrl);
            info.Kind = e == "gif" ? MediaKind.Gif : Array.IndexOf(ImgExt, e) >= 0 ? MediaKind.Image : MediaKind.Video;
            info.DirectExt = e.Length > 0 ? e : (info.Kind == MediaKind.Gif ? "gif" : "mp4");
            string name = "";
            try { name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(mediaUrl).AbsolutePath)); } catch { }
            info.Title = !string.IsNullOrEmpty(title) ? title : (name.Length > 0 ? name : "download");
            info.Thumbnail = thumb ?? (info.Kind != MediaKind.Video ? mediaUrl : "");
            info.DirectSize = Size(mediaUrl);
            return info;
        }

        // ------------------------------------------------------------------ GIPHY
        static YtInfo Giphy(string url)
        {
            // giphy.com/gifs/slug-ID, giphy.com/embed/ID, media*.giphy.com/media/ID/giphy.gif, i.giphy.com/ID.gif
            Match m = Regex.Match(url, @"giphy\.com/(?:gifs|stickers|clips|embed)/(?:[\w-]*-)?([A-Za-z0-9]+)(?:[/?#]|$)");
            if (!m.Success) m = Regex.Match(url, @"giphy\.com/media/(?:v1\.[^/]+/)?([A-Za-z0-9]+)/");
            if (!m.Success) m = Regex.Match(url, @"i\.giphy\.com/(?:media/)?([A-Za-z0-9]+)");
            if (!m.Success) return FromPage(url);
            string id = m.Groups[1].Value;
            string title = "GIPHY " + id;
            Match slug = Regex.Match(url, @"/(?:gifs|stickers)/([\w-]+)-" + id);
            if (slug.Success) title = slug.Groups[1].Value.Replace('-', ' ');
            YtInfo info = Direct("https://i.giphy.com/media/" + id + "/giphy.gif", title, null);
            info.Site = "GIPHY";
            info.AltVideoUrl = "https://i.giphy.com/media/" + id + "/giphy.mp4";
            return info;
        }

        // ------------------------------------------------------------------ Tenor
        static YtInfo Tenor(string url)
        {
            string html = Get(url);
            string gif = null;
            Match m = Regex.Match(html, @"https://media\d*\.tenor\.com/[^""'\s<>]+?\.gif");
            if (m.Success) gif = m.Value;
            if (gif == null) gif = Meta(html, "og:image");
            string title = Meta(html, "og:title") ?? "Tenor GIF";
            if (gif == null) return FromPage(url);
            YtInfo info = Direct(gif.Replace("\\u002F", "/"), title, null);
            info.Site = "Tenor";
            Match mp4 = Regex.Match(html, @"https://media\d*\.tenor\.com/[^""'\s<>]+?\.mp4");
            if (mp4.Success) info.AltVideoUrl = mp4.Value;
            return info;
        }

        // ------------------------------------------------------------------ Discord and KLIPY (Discord's GIFs since 2026)
        /// A link copied in Discord: its picture proxy ("images-ext-1.discordapp.net/external/…/https/media.tenor.com/x.gif")
        /// gives the real file; a resized preview ("media.discordapp.net/…?width=320") gives the full-size one.
        static string Discord(string url)
        {
            Match ext = Regex.Match(url, @"discordapp\.net/external/[^/]+/(?:[^/]*/)?(https?)/(.+)$", RegexOptions.IgnoreCase);
            if (ext.Success)
            {
                string inner = ext.Groups[2].Value;
                int q = inner.IndexOf('?');
                if (q >= 0 && !Regex.IsMatch(inner.Substring(q), @"[?&](ex|is|hm)=")) inner = inner.Substring(0, q);
                return ext.Groups[1].Value + "://" + inner;
            }
            if (url.IndexOf("media.discordapp.net/attachments/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // keep Discord's signature (ex, is, hm), drop the preview size and format
                int q = url.IndexOf('?');
                string head = url.Replace("media.discordapp.net", "cdn.discordapp.com");
                if (q < 0) return head;
                head = head.Substring(0, head.IndexOf('?'));
                List<string> keep = new List<string>();
                foreach (string part in url.Substring(q + 1).Split('&'))
                {
                    string k = part.Split('=')[0].ToLowerInvariant();
                    if (k.Length == 0 || k == "width" || k == "height" || k == "format" || k == "quality") continue;
                    keep.Add(part);
                }
                return keep.Count > 0 ? head + "?" + string.Join("&", keep.ToArray()) : head;
            }
            return url;
        }

        static bool HasOg(string html) { return html != null && html.IndexOf("og:image", StringComparison.OrdinalIgnoreCase) >= 0 && html.IndexOf("Just a moment", StringComparison.OrdinalIgnoreCase) < 0; }

        /// A KLIPY GIF page (klipy.com/gifs/name): the GIF itself, with its MP4 as the video choice.
        static YtInfo Klipy(string url)
        {
            // KLIPY's pages sit behind Cloudflare's "verify you are human" check, which only a person in a web
            // browser can pass. When the page is turned away, say how to get the GIF's own file link instead
            // (static.klipy.com/....gif downloads like any other file).
            string html = null;
            try { html = Get(url); } catch { }
            if (!HasOg(html)) throw new UserError(L.T("dl_klipy_blocked"));
            string title = Meta(html, "og:title") ?? "KLIPY GIF";
            if (title.StartsWith("KLIPY: ")) title = title.Substring(7);
            string gif = Meta(html, "og:image") ?? Meta(html, "twitter:image");
            if (gif == null || !gif.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
            {
                Match g = Regex.Match(html, @"https://[\w.-]*klipy\.com/[^""'\s<>]+?\.gif");
                if (g.Success) gif = g.Value;
            }
            string mp4 = Meta(html, "og:video:secure_url") ?? Meta(html, "og:video");
            if (mp4 == null) { Match v = Regex.Match(html, @"https://[\w.-]*klipy\.com/[^""'\s<>]+?\.mp4"); if (v.Success) mp4 = v.Value; }
            if (gif == null && mp4 == null) return null;
            YtInfo info = Direct((gif ?? mp4).Replace("\\u002F", "/"), title, gif);
            info.Site = "KLIPY";
            if (gif != null && mp4 != null) info.AltVideoUrl = mp4;
            return info;
        }

        // ------------------------------------------------------------------ Pinterest
        static YtInfo Pinterest(string url)
        {
            if (url.Contains("pin.it/")) url = FinalUrl(url);
            Match m = Regex.Match(url, @"/pin/(?:[\w-]*--)?([A-Za-z0-9_-]+)");
            if (!m.Success) return null;   // boards etc. -> yt-dlp
            string id = m.Groups[1].Value;
            // 1) Pinterest's own pin API, 2) the media links inside the pin page, 3) whatever the API had (a still image)
            string json = null, html = null;
            YtInfo api = null;
            try
            {
                string data = "{\"options\":{\"field_set_key\":\"unauth_react_main_pin\",\"id\":\"" + id + "\"}}";
                json = Get("https://www.pinterest.com/resource/PinResource/get/?data=" + Uri.EscapeDataString(data),
                           new[] { "X-Pinterest-PWS-Handler", "www/pin/[id].js", "X-Requested-With", "XMLHttpRequest", "Referer", "https://www.pinterest.com/" });
                Log("pinterest_api.json", json);
                api = PinFromApi(json, url, id);
            }
            catch (Exception ex) { Log("pinterest_api_error.txt", ex.ToString()); }
            if (api != null && api.Kind != MediaKind.Image) return api;
            YtInfo scanned = json != null ? PinFromText(json, url, id, api) : null;
            if (scanned != null) return scanned;
            try { html = Get(url); Log("pinterest_page.html", html); }
            catch (Exception ex) { Log("pinterest_page_error.txt", ex.ToString()); }
            if (html != null)
            {
                scanned = PinFromText(html, url, id, api);
                if (scanned != null) return scanned;
            }
            if (api != null) return api;
            if (html == null) return null;   // let yt-dlp have a go
            try { return FromPage(url); } catch { return null; }
        }

        static void Log(string name, string content)
        {
            try
            {
                string d = Path.Combine(Paths.AppDir, "logs");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, name), content ?? "");
            }
            catch { }
        }

        /// Looks for the pin's GIF or video links in raw page/API text (used when the structured data has none).
        static YtInfo PinFromText(string text, string url, string id, YtInfo api)
        {
            string t = text.Replace("\\u002F", "/").Replace("\\/", "/").Replace("\u002F", "/");
            // the pin itself comes first; related pins come later in the page
            int at = t.IndexOf("\"" + id + "\"", StringComparison.Ordinal);
            string near = at >= 0 ? t.Substring(at, Math.Min(t.Length - at, 80000)) : t;
            foreach (string scope in new[] { near, at >= 0 ? t : null })
            {
                if (scope == null) continue;
                string gif = null;
                foreach (Match g in Regex.Matches(scope, @"https?://i\.pinimg\.com/(?:originals|\d+x(?:\d+)?)/(?:[0-9a-f]{2}/){3}[0-9a-f]+\.gif", RegexOptions.IgnoreCase))
                {
                    if (gif == null || (g.Value.Contains("/originals/") && !gif.Contains("/originals/"))) gif = g.Value;
                    if (gif.Contains("/originals/")) break;
                }
                string title = api != null ? api.Title : PinTitle(scope, t, id);
                if (gif != null)
                {
                    YtInfo g2 = Direct(gif, title, null);
                    g2.Site = "Pinterest"; g2.Url = url;
                    return g2;
                }
                string mp4 = null;
                foreach (Match v in Regex.Matches(scope, @"https?://v\d*\.pinimg\.com/videos/[^""'\s\\<>]+?\.mp4", RegexOptions.IgnoreCase))
                {
                    if (mp4 == null || (v.Value.Contains("720p") && !mp4.Contains("720p"))) mp4 = v.Value;
                    if (mp4.Contains("720p")) break;
                }
                if (mp4 == null)
                {
                    Match h = Regex.Match(scope, @"https?://v\d*\.pinimg\.com/videos/[^""'\s\\<>]+?\.m3u8", RegexOptions.IgnoreCase);
                    if (h.Success) { string c = HlsToMp4(h.Value); if (c != null && Size(c) > 0) mp4 = c; }
                }
                if (mp4 != null)
                {
                    string thumb = api != null ? api.Thumbnail : null;
                    if (string.IsNullOrEmpty(thumb))
                    {
                        Match th = Regex.Match(scope, @"https?://i\.pinimg\.com/(?:736x|474x|originals)/(?:[0-9a-f]{2}/){3}[0-9a-f]+\.(?:jpg|png|webp)", RegexOptions.IgnoreCase);
                        thumb = th.Success ? th.Value : Meta(t, "og:image");
                    }
                    YtInfo v2 = Direct(mp4, title, thumb);
                    v2.Kind = MediaKind.Video; v2.DirectExt = "mp4";
                    v2.Site = "Pinterest"; v2.Url = url;
                    v2.PreferGif = true;   // a pin with a short clip and no other media is almost always a "GIF"
                    return v2;
                }
            }
            return null;
        }

        static string PinTitle(string near, string page, string id)
        {
            foreach (string key in new[] { "gridTitle", "grid_title", "title", "seoTitle", "seo_title" })
            {
                Match m = Regex.Match(near, "\"" + key + "\":\"([^\"]{2,140})\"");
                if (m.Success && m.Groups[1].Value.Trim().Length > 1) return Regex.Unescape(m.Groups[1].Value).Trim();
            }
            string og = Meta(page, "og:title");
            return !string.IsNullOrEmpty(og) ? og : "Pinterest " + id;
        }

        static YtInfo PinFromApi(string json, string url, string id)
        {
            {
                JsonObject root = new JsonParser(json).Parse() as JsonObject;
                JsonObject pin = JPath(root, "resource_response", "data") as JsonObject;
                if (pin == null) return null;
                string title = Str(JPath(pin, "title"));
                if (title.Length == 0) title = Str(JPath(pin, "grid_title"));
                if (title.Length == 0) title = Str(JPath(pin, "seo_title"));
                if (title.Length == 0) title = Str(JPath(pin, "description")).Trim();
                if (title.Length == 0) title = "Pinterest " + id;
                if (title.Length > 90) title = title.Substring(0, 90);
                string thumb = Str(JPath(pin, "images", "474x", "url"));
                string orig = Str(JPath(pin, "images", "orig", "url"));

                // 1) a real .gif file (classic GIF pins)
                string embedGif = Str(JPath(pin, "embed", "src"));
                string gif = embedGif.EndsWith(".gif") ? embedGif : (orig.EndsWith(".gif") ? orig : null);
                if (gif != null)
                {
                    YtInfo g = Direct(gif, title, null);
                    g.Site = "Pinterest"; g.Url = url;
                    return g;
                }

                // 2) video pins and "idea"/story pins (Pinterest stores most GIFs as short videos now)
                PinVideo best = null;
                Consider(JPath(pin, "videos", "video_list") as JsonObject, ref best);
                object pages = JPath(pin, "story_pin_data", "pages");
                if (pages is List<object>)
                    foreach (object pg in (List<object>)pages)
                    {
                        object blocks = JPath(pg as JsonObject, "blocks");
                        if (blocks is List<object>)
                            foreach (object bl in (List<object>)blocks)
                                Consider(JPath(bl as JsonObject, "video", "video_list") as JsonObject, ref best);
                    }

                YtInfo info;
                if (best != null)
                {
                    string video = best.Url;
                    bool hls = video.Contains(".m3u8");
                    if (hls)
                    {
                        // the same clip is usually published as a plain MP4 next to the stream
                        string mp4 = HlsToMp4(video);
                        if (mp4 != null && Size(mp4) > 0) { video = mp4; hls = false; }
                    }
                    string vthumb = best.Thumb.Length > 0 ? best.Thumb : (thumb.Length > 0 ? thumb : orig);
                    if (hls)
                    {
                        info = new YtInfo { Url = url, DirectUrl = video, DirectExt = "mp4", DirectIsHls = true, Kind = MediaKind.Video, Title = title, Thumbnail = vthumb };
                    }
                    else info = Direct(video, title, vthumb);
                    info.Kind = MediaKind.Video;
                    info.DirectExt = "mp4";
                    info.Duration = best.Duration;
                    if (best.Height > 0) info.Heights.Add(best.Height);
                    // short looping clips are what people call "GIFs" on Pinterest
                    bool isVideoPin = JPath(pin, "is_video") is bool && (bool)JPath(pin, "is_video");
                    info.PreferGif = best.Duration > 0 && best.Duration <= 45 && (!isVideoPin || best.Duration <= 20);
                }
                else if (orig.Length > 0) info = Direct(orig, title, orig);
                else return null;
                info.Site = "Pinterest";
                info.Url = url;
                return info;
            }
        }

        class PinVideo { public string Url = "", Thumb = ""; public int Height; public double Duration; public bool Mp4; }

        /// Keeps the best clip: a plain MP4 beats a stream, then the larger size wins.
        static void Consider(JsonObject list, ref PinVideo best)
        {
            if (list == null) return;
            foreach (KeyValuePair<string, object> kv in list)
            {
                JsonObject v = kv.Value as JsonObject; if (v == null) continue;
                string u = Str(JPath(v, "url"));
                bool mp4 = u.Contains(".mp4"), hls = u.Contains(".m3u8");
                if (!mp4 && !hls) continue;
                PinVideo c = new PinVideo { Url = u, Thumb = Str(JPath(v, "thumbnail")), Height = (int)Num(JPath(v, "height")), Duration = Num(JPath(v, "duration")) / 1000.0, Mp4 = mp4 };
                if (best == null || (c.Mp4 && !best.Mp4) || (c.Mp4 == best.Mp4 && c.Height > best.Height)) best = c;
            }
        }

        /// https://v1.pinimg.com/videos/iht/hls/1a/dd/32/HASH.m3u8 -> https://v1.pinimg.com/videos/mc/720p/1a/dd/32/HASH.mp4
        static string HlsToMp4(string m3u8)
        {
            Match m = Regex.Match(m3u8, @"^(https?://[^/]+/videos/)(?:[^/]+/)*?hls/((?:[0-9a-f]{2}/){3}[0-9a-f]+)(?:_\w+)?\.m3u8", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Value + "mc/720p/" + m.Groups[2].Value + ".mp4";
        }

        static object JPath(JsonObject o, params string[] keys)
        {
            object cur = o;
            foreach (string k in keys)
            {
                JsonObject jo = cur as JsonObject;
                if (jo == null) return null;
                object next = null;
                foreach (KeyValuePair<string, object> kv in jo) if (kv.Key == k) { next = kv.Value; break; }
                cur = next;
            }
            return cur;
        }
        static string Str(object v) { return v == null ? "" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); }
        static double Num(object v) { if (v is long) return (long)v; if (v is double) return (double)v; return 0; }
    }
}
