using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    public class YtFormat
    {
        public string Id = "", VCodec = "none", ACodec = "none", Ext = "";
        public int Height;
        public double Tbr, Abr;
        public long Size;      // exact or approximate, -1 unknown
        public string Url = "", Protocol = "";
        public bool HasVideo { get { return VCodec != "none" && VCodec != ""; } }
        public bool HasAudio { get { return ACodec != "none" && ACodec != ""; } }
    }

    public class YtInfo
    {
        public string Title = "", Uploader = "", Thumbnail = "", Url = "", Id = "", Site = "";
        public double Duration;
        public long Views;
        public bool IsPlaylist;
        public int Count;
        public List<int> Heights = new List<int>();
        public List<YtFormat> Formats = new List<YtFormat>();
        public MediaKind Kind = MediaKind.Video;
        // direct downloads (GIF sites, Pinterest images, plain links)
        public string DirectUrl, DirectExt, AltVideoUrl;
        public long DirectSize = -1;
        public bool DirectIsHls;          // DirectUrl is an .m3u8 stream (fetched with FFmpeg)
        public bool PreferGif;            // short looping clip (Pinterest "GIF" pins): suggest saving as GIF
        public bool IsDirect { get { return DirectUrl != null; } }
    }

    public enum SaveAs { Video, Audio, Gif, Original }

    public class YtOptions
    {
        public SaveAs Mode = SaveAs.Video;
        public int MaxHeight;              // 0 = best
        public string AudioFormat = "mp3";
        public int GifWidth = 480;
        public int GifSeconds = 10;        // 0 = whole clip
        public bool Playlist;
    }

    public class YtProgress
    {
        public double Percent;             // 0..1
        public long Done = -1, Total = -1;
        public double Speed = -1, Eta = -1;
        public bool Finishing;
        public string Stage = "";
        public int Item, Items;
    }

    /// yt-dlp (+ Deno for YouTube's JavaScript challenges) plus our own direct downloader and GIF maker.
    public static class YtDlp
    {
        public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        public const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

        public static bool Ready { get { return Tools.YtDlp != null; } }
        static string Q(string s) { return Tools.Quote(s); }

        static Dictionary<string, string> Env()
        {
            Dictionary<string, string> env = new Dictionary<string, string>();
            string path = Tools.YtDir;
            if (Tools.FFmpeg != null) path += ";" + Path.GetDirectoryName(Tools.FFmpeg);
            env["PATH"] = path + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
            env["PYTHONUTF8"] = "1";
            env["PYTHONIOENCODING"] = "utf-8";
            return env;
        }

        public static void Install(Action<string, double> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(Tools.YtDir);
            string tmp = Path.Combine(Path.GetTempPath(), "FileConverterYt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                string yt = Path.Combine(tmp, "yt-dlp.exe");
                Net.Download(YtDlpUrl, yt, (g, t) => progress(L.T("dl_progress", "yt-dlp", Theme.HumanSize(g), t > 0 ? Theme.HumanSize(t) : "?"), t > 0 ? 0.3 * g / t : 0), cancelled);
                File.Copy(yt, Path.Combine(Tools.YtDir, "yt-dlp.exe"), true);
                string zip = Path.Combine(tmp, "deno.zip");
                Net.Download(DenoUrl, zip, (g, t) => progress(L.T("dl_progress", "Deno", Theme.HumanSize(g), t > 0 ? Theme.HumanSize(t) : "?"), 0.3 + (t > 0 ? 0.65 * g / t : 0)), cancelled);
                progress(L.T("unpacking", "Deno"), 0.96);
                using (ZipArchive za = ZipFile.OpenRead(zip))
                    foreach (ZipArchiveEntry e in za.Entries)
                        if (e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase)) e.ExtractToFile(Path.Combine(Tools.YtDir, "deno.exe"), true);
                progress("", 1);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        public static string Update()
        {
            string yt = Tools.YtDlp;
            if (yt == null) return L.T("not_installed");
            string output;
            Tools.RunHidden(yt, "-U", 180000, out output);
            return Tools.LastLines(output, 1);
        }

        public static bool LooksLikeUrl(string s)
        {
            s = s.Trim();
            return s.Contains(".") && !s.Contains(" ") && Regex.IsMatch(s, @"^(https?://)?([\w-]+\.)+[\w-]+(:\d+)?(/\S*)?$", RegexOptions.IgnoreCase);
        }
        public static bool IsPlaylistOnly(string url) { return Regex.IsMatch(url, @"[?&]list=") && !Regex.IsMatch(url, @"([?&]v=|youtu\.be/|/shorts/)"); }
        public static bool HasPlaylist(string url) { return Regex.IsMatch(url, @"[?&]list=") && url.Contains("youtu"); }
        static bool IsYouTube(string url) { return Regex.IsMatch(url, @"(youtube\.com|youtu\.be)", RegexOptions.IgnoreCase); }

        // ------------------------------------------------------------------ details
        /// Gets details for any supported link (YouTube, Pinterest, GIF sites, direct links, 1000+ sites via yt-dlp).
        public static YtInfo Fetch(string url, Func<bool> cancelled)
        {
            url = url.Trim();
            YtInfo own = null;
            bool data = Resolver.IsDataImage(url);
            try { own = Resolver.TryResolve(url, cancelled); }
            catch (UserError) { throw; }
            catch { }
            if (own != null) return own;
            if (data) throw new UserError(L.T("dl_bad_picture"));
            url = Resolver.Unwrap(url);   // an image result's link: yt-dlp gets the picture's own address
            if (!Ready) throw new UserError(L.T("not_installed"));
            try { return FetchYtDlp(url, cancelled); }
            catch (OperationCanceledException) { throw; }
            catch (UserError)
            {
                // last resort: the page's own preview media (og:video / og:image)
                YtInfo page = null;
                try { page = Resolver.FromPage(url); } catch { }
                if (page != null) return page;
                throw;
            }
        }

        static YtInfo FetchYtDlp(string url, Func<bool> cancelled)
        {
            string yt = Tools.YtDlp;
            StringBuilder outb = new StringBuilder(), errb = new StringBuilder();
            string args = "-J --no-warnings --flat-playlist --no-colors " + (IsPlaylistOnly(url) ? "" : "--no-playlist ") + Q(url);
            int code = Tools.Stream(yt, args, l => outb.AppendLine(l), l => errb.AppendLine(l), cancelled, Env());
            if (code == -2) throw new OperationCanceledException();
            string json = outb.ToString().Trim();
            if ((code != 0 || !json.StartsWith("{")) && errb.ToString().IndexOf("impersonate", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // a site behind Cloudflare's "are you a browser?" check: ask again looking like a browser
                outb.Length = 0; errb.Length = 0;
                code = Tools.Stream(yt, Impersonate + args, l => outb.AppendLine(l), l => errb.AppendLine(l), cancelled, Env());
                if (code == -2) throw new OperationCanceledException();
                json = outb.ToString().Trim();
                if (code == 0 && json.StartsWith("{")) lock (browserLike) browserLike.Add(HostOf(url));
            }
            if (code != 0 || !json.StartsWith("{")) throw new UserError(CleanError(errb.ToString()));
            return ParseJson(url, json);
        }

        /// Reads yt-dlp's -J output.
        public static YtInfo ParseJson(string url, string json)
        {
            JsonObject o = new JsonParser(json).Parse() as JsonObject;
            YtInfo info = new YtInfo { Url = url, Site = Resolver.Site(url) };
            foreach (KeyValuePair<string, object> kv in o)
            {
                switch (kv.Key)
                {
                    case "title": info.Title = Str(kv.Value); break;
                    case "uploader": if (info.Uploader == "") info.Uploader = Str(kv.Value); break;
                    case "channel": info.Uploader = Str(kv.Value); break;
                    case "duration": info.Duration = Num(kv.Value); break;
                    case "view_count": info.Views = (long)Num(kv.Value); break;
                    case "thumbnail": info.Thumbnail = Str(kv.Value); break;
                    case "id": info.Id = Str(kv.Value); break;
                    case "_type": info.IsPlaylist = Str(kv.Value) == "playlist"; break;
                    case "playlist_count": info.Count = (int)Num(kv.Value); break;
                    case "entries": if (kv.Value is List<object>) info.Count = Math.Max(info.Count, ((List<object>)kv.Value).Count); break;
                    case "thumbnails":
                        if (info.Thumbnail == "" && kv.Value is List<object>)
                        {
                            List<object> th = (List<object>)kv.Value;
                            for (int i = th.Count - 1; i >= 0 && info.Thumbnail == ""; i--)
                            {
                                JsonObject t = th[i] as JsonObject; if (t == null) continue;
                                foreach (KeyValuePair<string, object> tk in t) if (tk.Key == "url") info.Thumbnail = Str(tk.Value);
                            }
                        }
                        break;
                    case "formats":
                        if (kv.Value is List<object>)
                            foreach (object fo in (List<object>)kv.Value)
                            {
                                JsonObject f = fo as JsonObject; if (f == null) continue;
                                YtFormat yf = new YtFormat { Size = -1 };
                                long exact = -1, approx = -1;
                                double vbr = 0; int width = 0;
                                string videoExt = null, audioExt = null; bool vKnown = false, aKnown = false;
                                foreach (KeyValuePair<string, object> fk in f)
                                {
                                    switch (fk.Key)
                                    {
                                        case "format_id": yf.Id = Str(fk.Value); break;
                                        case "height": yf.Height = (int)Num(fk.Value); break;
                                        case "vcodec": yf.VCodec = Str(fk.Value); vKnown = fk.Value != null && yf.VCodec.Length > 0; break;
                                        case "acodec": yf.ACodec = Str(fk.Value); aKnown = fk.Value != null && yf.ACodec.Length > 0; break;
                                        case "video_ext": videoExt = Str(fk.Value); break;
                                        case "audio_ext": audioExt = Str(fk.Value); break;
                                        case "width": width = (int)Num(fk.Value); break;
                                        case "vbr": vbr = Num(fk.Value); break;
                                        case "url": yf.Url = Str(fk.Value); break;
                                        case "protocol": yf.Protocol = Str(fk.Value); break;
                                        case "ext": yf.Ext = Str(fk.Value); break;
                                        case "tbr": yf.Tbr = Num(fk.Value); break;
                                        case "abr": yf.Abr = Num(fk.Value); break;
                                        case "filesize": if (fk.Value != null) exact = (long)Num(fk.Value); break;
                                        case "filesize_approx": if (fk.Value != null) approx = (long)Num(fk.Value); break;
                                    }
                                }
                                // Instagram, X and others often leave the codecs out: go by the video/audio extensions
                                // (a video of unknown make-up from these sites carries its sound too)
                                if (!vKnown)
                                {
                                    bool v = (videoExt != null && videoExt != "none" && videoExt.Length > 0) || yf.Height > 0 || width > 0;
                                    yf.VCodec = v ? "unknown" : "none";
                                }
                                if (!aKnown)
                                {
                                    bool a = audioExt != null ? audioExt != "none" && audioExt.Length > 0 : yf.HasVideo || yf.Abr > 0;
                                    yf.ACodec = a ? "unknown" : "none";
                                }
                                if (yf.Tbr <= 0 && vbr + yf.Abr > 0) yf.Tbr = vbr + yf.Abr;
                                yf.Size = exact > 0 ? exact : approx;
                                if (yf.Size <= 0 && yf.Tbr > 0 && info.Duration > 0) yf.Size = (long)(yf.Tbr * 1000 / 8 * info.Duration);
                                if (yf.Id.Contains("storyboard") || yf.Ext == "mhtml") continue;
                                info.Formats.Add(yf);
                                if (yf.HasVideo && yf.Height > 0 && !info.Heights.Contains(yf.Height)) info.Heights.Add(yf.Height);
                            }
                        break;
                }
            }
            info.Heights.Sort();
            FillMissingSizes(info);
            if (!info.IsPlaylist && info.Formats.Count == 0 && info.Thumbnail.Length > 0)
            {
                // e.g. image-only posts: offer the picture itself
                YtInfo img = new YtInfo { Url = url, Title = info.Title, Site = info.Site, Thumbnail = info.Thumbnail, DirectUrl = info.Thumbnail, Kind = MediaKind.Image };
                img.DirectExt = "jpg";
                try { string e = Path.GetExtension(new Uri(info.Thumbnail).AbsolutePath).TrimStart('.'); if (e.Length > 0) img.DirectExt = e; } catch { }
                img.DirectSize = Resolver.Size(info.Thumbnail);
                return img;
            }
            return info;
        }

        /// Still no size for the formats that would be picked (no bitrate given): ask the server how big the file is
        /// (a quick HEAD request, nothing is downloaded). Streamed formats (m3u8) can't be asked; they keep -1.
        static void FillMissingSizes(YtInfo info)
        {
            if (info.IsPlaylist || info.Formats.Count == 0) return;
            if (Estimate(info, new YtOptions()) > 0) return;
            List<YtFormat> ask = new List<YtFormat>();
            foreach (YtFormat f in info.Formats)
                if (f.Size <= 0 && f.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !f.Protocol.Contains("m3u8") && !f.Protocol.Contains("dash") && !f.Url.Contains(".m3u8"))
                    ask.Add(f);
            // the biggest videos first, and the best sound: at most a handful of quick requests
            ask.Sort((a, b) => b.Height != a.Height ? b.Height.CompareTo(a.Height) : b.Abr.CompareTo(a.Abr));
            int n = 0;
            foreach (YtFormat f in ask)
            {
                if (n++ >= 6) break;
                long len = Resolver.Size(f.Url);
                if (len > 0) f.Size = len;
            }
        }

        // ------------------------------------------------------------------ size estimate
        /// Estimated download size for the chosen options (bytes), or -1.
        public static long Estimate(YtInfo info, YtOptions opt)
        {
            if (info == null) return -1;
            if (info.IsDirect) return info.DirectSize;
            if (info.IsPlaylist || info.Formats.Count == 0) return -1;
            YtFormat bestAudio = null, m4a = null;
            foreach (YtFormat f in info.Formats)
            {
                if (f.HasVideo || !f.HasAudio || f.Size <= 0) continue;
                if (bestAudio == null || f.Abr > bestAudio.Abr) bestAudio = f;
                if (f.ACodec.StartsWith("mp4a") && (m4a == null || f.Abr > m4a.Abr)) m4a = f;
            }
            if (opt.Mode == SaveAs.Audio) return bestAudio != null ? bestAudio.Size : -1;
            int limit = opt.Mode == SaveAs.Gif ? GifSourceHeight(opt) : opt.MaxHeight;
            YtFormat video = null;
            foreach (YtFormat f in info.Formats)
            {
                if (!f.HasVideo || f.Size <= 0 || (limit > 0 && f.Height > limit)) continue;
                if (video == null || f.Height > video.Height ||
                    (f.Height == video.Height && Score(f) > Score(video))) video = f;
            }
            if (video == null) return -1;
            long size = video.Size;
            if (!video.HasAudio && opt.Mode != SaveAs.Gif) { YtFormat a = m4a ?? bestAudio; if (a != null) size += a.Size; }
            return size;
        }

        static double Score(YtFormat f) { return (f.VCodec.StartsWith("avc1") ? 1e7 : 0) + f.Tbr; }
        static int GifSourceHeight(YtOptions o) { return o.GifWidth <= 320 ? 360 : o.GifWidth <= 480 ? 480 : 720; }

        // ------------------------------------------------------------------ downloading
        static readonly Regex ProgRx = new Regex(@"^FCPROG\s*(\S+)\|(\S*)\|(\S*)\|(\S*)\|(.*)$");
        static readonly Regex ItemRx = new Regex(@"Downloading (?:item|video) (\d+) of (\d+)");

        static double D(string s) { double d; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : -1; }

        /// Downloads and returns the saved files. Throws UserError / OperationCanceledException.
        public static List<string> Download(YtInfo info, YtOptions opt, string outDir, long expected, Action<YtProgress> progress, Func<bool> cancelled)
        {
            if (info.IsDirect) return DownloadDirect(info, opt, outDir, progress, cancelled);
            if (opt.Mode == SaveAs.Gif)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "FileConverterGif_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                try
                {
                    YtOptions vopt = new YtOptions { Mode = SaveAs.Video, MaxHeight = GifSourceHeight(opt) };
                    List<string> got = RunYtDlp(info.Url, vopt, tmp, expected, progress, cancelled, false);
                    if (got.Count == 0) throw new UserError(L.T("err_unknown"));
                    string gif = Paths.UniquePath(outDir, SafeName(info.Title), "gif");
                    MakeGif(got[0], gif, opt, progress, cancelled);
                    return new List<string> { gif };
                }
                finally { try { Directory.Delete(tmp, true); } catch { } }
            }
            return RunYtDlp(info.Url, opt, outDir, expected, progress, cancelled, opt.Playlist);
        }

        static List<string> RunYtDlp(string url, YtOptions opt, string outDir, long expected, Action<YtProgress> progress, Func<bool> cancelled, bool playlist)
        {
            try { return RunYtDlpOnce(url, opt, outDir, expected, progress, cancelled, playlist, true); }
            catch (UserError ue)
            {
                // the fast (parallel) mode is occasionally refused by the server: retry the normal way
                if (cancelled()) throw new OperationCanceledException();
                if (ue.Message.Contains("403") || ue.Message.Contains("Forbidden") || ue.Message.Contains("fragment") || ue.Message.Contains("HTTP Error"))
                    return RunYtDlpOnce(url, opt, outDir, expected, progress, cancelled, playlist, false);
                throw;
            }
        }

        const string Impersonate = "--extractor-args \"generic:impersonate\" ";
        static readonly HashSet<string> browserLike = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string HostOf(string url) { try { return new Uri(url).Host; } catch { return ""; } }

        static List<string> RunYtDlpOnce(string url, YtOptions opt, string outDir, long expected, Action<YtProgress> progress, Func<bool> cancelled, bool playlist, bool fast)
        {
            string yt = Tools.YtDlp;
            if (yt == null) throw new UserError(L.T("not_installed"));
            StringBuilder a = new StringBuilder();
            a.Append("--newline --no-colors --progress --no-mtime --windows-filenames --no-warnings ");
            bool like; lock (browserLike) like = browserLike.Contains(HostOf(url));
            if (like) a.Append(Impersonate);
            if (fast)
            {
                // YouTube limits each connection; DASH pieces downloaded 8 at a time are much faster
                a.Append("-N 8 ");
                if (IsYouTube(url)) a.Append("--extractor-args ").Append(Q("youtube:formats=dashy")).Append(' ');
            }
            if (Tools.FFmpeg != null) a.Append("--ffmpeg-location ").Append(Q(Path.GetDirectoryName(Tools.FFmpeg))).Append(' ');
            a.Append("-P ").Append(Q(outDir)).Append(' ');
            a.Append(playlist ? "--yes-playlist -o " + Q("%(playlist_title|Playlist)s/%(playlist_index|0)03d - %(title).150B.%(ext)s") : "--no-playlist -o " + Q("%(title).150B.%(ext)s")).Append(' ');
            a.Append("--print ").Append(Q("after_move:FCFILE:%(filepath)s")).Append(' ');
            a.Append("--progress-template ").Append(Q("download:FCPROG %(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.format_id)s")).Append(' ');
            a.Append("--embed-metadata ");
            if (opt.Mode == SaveAs.Audio)
            {
                a.Append("-f \"ba/b\" -x --audio-format ").Append(opt.AudioFormat).Append(" --audio-quality 0 ");
                if (opt.AudioFormat == "mp3" || opt.AudioFormat == "m4a") a.Append("--embed-thumbnail ");
            }
            else
            {
                string sort = opt.MaxHeight > 0 ? "res:" + opt.MaxHeight + ",vcodec:h264,acodec:aac" : "res,vcodec:h264,acodec:aac";
                a.Append("-f \"bv*+ba/b\" -S ").Append(Q(sort)).Append(" --merge-output-format mp4 ");
            }
            a.Append(Q(url));

            List<string> files = new List<string>();
            StringBuilder err = new StringBuilder();
            YtProgress p = new YtProgress();
            Dictionary<string, long> doneBy = new Dictionary<string, long>(), totalBy = new Dictionary<string, long>();
            Action<string> onLine = line =>
            {
                string l = line.Trim();
                Match m = ProgRx.Match(l);
                if (m.Success)
                {
                    string fid = m.Groups[5].Value;
                    double done = D(m.Groups[1].Value), total = D(m.Groups[2].Value);
                    if (done >= 0) doneBy[fid] = (long)done;
                    if (total > 0) totalBy[fid] = (long)total;
                    long sumDone = 0, sumTotal = 0;
                    foreach (long v in doneBy.Values) sumDone += v;
                    foreach (long v in totalBy.Values) sumTotal += v;
                    long denom = Math.Max(sumTotal, expected);
                    if (playlist) denom = sumTotal;   // per video
                    if (playlist) { sumDone = done >= 0 ? (long)done : 0; denom = total > 0 ? (long)total : -1; }
                    p.Done = sumDone; p.Total = denom;
                    p.Percent = denom > 0 ? Math.Min(1, sumDone / (double)denom) : 0;
                    p.Speed = D(m.Groups[3].Value); p.Eta = D(m.Groups[4].Value);
                    p.Finishing = false; p.Stage = "";
                    progress(p); return;
                }
                if (l.StartsWith("FCFILE:")) { files.Add(l.Substring(7)); return; }
                Match im = ItemRx.Match(l);
                if (im.Success) { p.Item = int.Parse(im.Groups[1].Value); p.Items = int.Parse(im.Groups[2].Value); p.Percent = 0; doneBy.Clear(); totalBy.Clear(); progress(p); return; }
                if (l.StartsWith("[Merger]") || l.StartsWith("[ExtractAudio]") || l.StartsWith("[EmbedThumbnail]") || l.StartsWith("[Metadata]") || l.StartsWith("[VideoConvertor]") || l.StartsWith("[FixupM3u8]"))
                { p.Finishing = true; progress(p); return; }
                if (l.StartsWith("ERROR")) lock (err) err.AppendLine(l);
            };
            int code = Tools.Stream(yt, a.ToString(), onLine, l => onLine(l), cancelled, Env());
            if (code == -2) throw new OperationCanceledException();
            if (code != 0 && files.Count == 0) throw new UserError(CleanError(err.ToString()));
            return files;
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) s = "download";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');
            if (s.Length > 120) s = s.Substring(0, 120);
            return s.Length == 0 ? "download" : s;
        }

        static List<string> DownloadDirect(YtInfo info, YtOptions opt, string outDir, Action<YtProgress> progress, Func<bool> cancelled)
        {
            YtProgress p = new YtProgress();
            string name = SafeName(info.Title);
            DateTime start = DateTime.Now;
            Action<string, string> fetch = (url, dest) =>
            {
                if (info.DirectIsHls && url == info.DirectUrl)
                {
                    // streamed clip: let FFmpeg join the pieces into one MP4
                    p.Stage = "download";
                    Ffmpeg("-hide_banner -y -i " + Q(url) + " -c copy -bsf:a aac_adtstoasc -movflags +faststart -f mp4 " + Q(dest), progress, p, cancelled);
                    return;
                }
                if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    // a picture that came inside the link itself (data:image/…), already saved
                    File.Copy(new Uri(url).LocalPath, dest, true);
                    return;
                }
                try
                {
                    Net.Download(url, dest, (g, t) =>
                    {
                        double secs = Math.Max(0.2, (DateTime.Now - start).TotalSeconds);
                        p.Done = g; p.Total = t; p.Percent = t > 0 ? g / (double)t : 0; p.Speed = g / secs;
                        p.Eta = t > 0 && p.Speed > 0 ? (t - g) / p.Speed : -1;
                        progress(p);
                    }, cancelled);
                }
                catch (System.Net.WebException ex)
                {
                    // turned away as "not a browser" (Cloudflare): yt-dlp fetches the file looking like one
                    System.Net.HttpWebResponse r = ex.Response as System.Net.HttpWebResponse;
                    if (r == null || ((int)r.StatusCode != 403 && (int)r.StatusCode != 503)) throw;
                    try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                    StringBuilder err = new StringBuilder();
                    int code = Tools.YtDlp == null ? 1 : Tools.Stream(Tools.YtDlp, Impersonate + "--no-playlist --no-part --no-warnings -o " + Q(dest) + " " + Q(url), null, l => err.AppendLine(l), cancelled, Env());
                    if (code == -2) throw new OperationCanceledException();
                    if (code != 0 || !File.Exists(dest)) throw new UserError(err.Length > 0 ? CleanError(err.ToString()) : ex.Message);
                }
            };
            bool wantVideo = opt.Mode == SaveAs.Video, wantGif = opt.Mode == SaveAs.Gif;
            // GIF sites also offer an MP4 of the same clip
            if (info.Kind == MediaKind.Gif && wantVideo && info.AltVideoUrl != null)
            {
                string mp4 = Paths.UniquePath(outDir, name, "mp4");
                fetch(info.AltVideoUrl, mp4);
                return new List<string> { mp4 };
            }
            if (info.Kind == MediaKind.Gif && wantVideo)
            {
                string tmpGif = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".gif");
                try
                {
                    fetch(info.DirectUrl, tmpGif);
                    string mp4 = Paths.UniquePath(outDir, name, "mp4");
                    Ffmpeg("-hide_banner -y -i " + Q(tmpGif) + " -movflags +faststart -pix_fmt yuv420p -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" " + Q(mp4), progress, p, cancelled);
                    return new List<string> { mp4 };
                }
                finally { try { File.Delete(tmpGif); } catch { } }
            }
            if (info.Kind == MediaKind.Video && (wantGif || opt.Mode == SaveAs.Audio))
            {
                string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "." + (info.DirectExt ?? "mp4"));
                try
                {
                    fetch(info.DirectUrl, tmp);
                    if (wantGif)
                    {
                        string gif = Paths.UniquePath(outDir, name, "gif");
                        MakeGif(tmp, gif, opt, progress, cancelled);
                        return new List<string> { gif };
                    }
                    string aud = Paths.UniquePath(outDir, name, opt.AudioFormat);
                    string codec = opt.AudioFormat == "mp3" ? "-c:a libmp3lame -q:a 0" : opt.AudioFormat == "wav" ? "-c:a pcm_s16le" : opt.AudioFormat == "opus" ? "-c:a libopus -b:a 160k" : "-c:a aac -b:a 256k";
                    Ffmpeg("-hide_banner -y -i " + Q(tmp) + " -vn " + codec + " " + Q(aud), progress, p, cancelled);
                    return new List<string> { aud };
                }
                finally { try { File.Delete(tmp); } catch { } }
            }
            string dest2 = Paths.UniquePath(outDir, name, info.DirectExt ?? "bin");
            fetch(info.DirectUrl, dest2);
            return new List<string> { dest2 };
        }

        /// High quality GIF: palette per clip, chosen width/frame rate/length.
        public static void MakeGif(string video, string gif, YtOptions opt, Action<YtProgress> progress, Func<bool> cancelled)
        {
            int fps = opt.GifWidth >= 720 ? 15 : 12;
            string vf = "fps=" + fps + ",scale='min(" + opt.GifWidth + ",iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=sierra2_4a";
            string len = opt.GifSeconds > 0 ? " -t " + opt.GifSeconds : "";
            YtProgress p = new YtProgress { Stage = "gif" };
            Ffmpeg("-hide_banner -y" + len + " -i " + Q(video) + " -vf " + Q(vf) + " -loop 0 " + Q(gif), progress, p, cancelled);
        }

        static void Ffmpeg(string args, Action<YtProgress> progress, YtProgress p, Func<bool> cancelled)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            p.Finishing = true; p.Stage = "convert"; progress(p);
            string output;
            int code = Tools.RunHidden(ff, args, 30 * 60 * 1000, out output);
            if (cancelled()) throw new OperationCanceledException();
            if (code != 0) throw new UserError(Engine.Friendly(output));
        }

        static string CleanError(string e)
        {
            string line = Tools.LastLines(e, 1);
            line = Regex.Replace(line, @"^ERROR:\s*(\[[^\]]+\]\s*)?(\S+:\s*)?", "");
            if (line.Contains("Video unavailable") || line.Contains("Private video")) return L.T("yt_unavailable");
            if (line.Contains("Sign in to confirm")) return L.T("yt_signin");
            if (line.Contains("Unsupported URL") || line.Contains("is not a valid URL")) return L.T("yt_invalid");
            if (line.Contains("getaddrinfo") || (line.Contains("Unable to download") && line.Contains("Errno"))) return L.T("yt_offline");
            return line.Length > 0 ? line : L.T("err_unknown");
        }

        static string Str(object v) { return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        static double Num(object v) { if (v is double) return (double)v; if (v is long) return (long)v; return 0; }
    }
}
