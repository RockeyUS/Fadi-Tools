using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    public class UserError : Exception { public UserError(string m) : base(m) { } }

    public class JobStep
    {
        public string Exe, Args;
        public bool Ffmpeg;
        public string TmpDir;        // a tool writes its result here...
        public string CollectTo;     // ...and we move it to this path
        public Action Native;        // or: run C# code
        public double Weight = 1;
        public string WaitFor;       // a file the tool may finish writing after it exits (Edge)
        public double Duration;      // expected output length (ffmpeg progress), 0 = read from the input
    }

    public class Job
    {
        public string Output;
        public List<JobStep> Steps = new List<JobStep>();
        public List<string> Temps = new List<string>();
        public Func<Job> Fallback;     // a slower, safer way to do the same thing (e.g. CPU instead of the graphics card)
        public string Encoder;         // what does the heavy lifting, for the status line
        public string Note;            // set while running: e.g. the new picture size ("1200 × 900") when a size limit needed it
    }

    public static class Engine
    {
        static readonly string[] AudioT = { "mp3", "wav", "ogg", "flac", "m4a", "aac", "opus", "wma", "aiff", "ac3" };
        static readonly string[] VideoT = { "mp4", "mov", "mkv", "avi", "webm", "wmv", "m4v", "mpg", "3gp" };
        static readonly string[] ImageT = { "jpg", "png", "webp", "heic", "avif", "bmp", "tiff", "ico" };
        static readonly string[] DocT = { "docx", "odt", "rtf", "txt", "html", "md", "epub", "pptx", "odp" };
        static readonly string[] DataT = { "xlsx", "ods", "csv", "tsv", "json", "xml" };
        static readonly string[] TextLike = { "txt", "log", "ini", "cfg" };
        static readonly string[] Sheets = { "xlsx", "xls", "ods", "numbers" };

        static bool In(string[] a, string v) { return Array.IndexOf(a, v) >= 0; }
        static string Q(string s) { return Tools.Quote(s); }
        static UserError Err(string key, params object[] a) { return new UserError(L.T(key, a)); }

        static string NeedFF() { string f = Tools.FFmpeg; if (f == null) throw Err("err_need_ff"); return f; }
        static string NeedIM() { string m = Tools.Magick; if (m == null) throw Err("err_need_im"); return m; }
        static string NeedLO() { string s = Tools.Soffice; if (s == null) throw Err("err_need_lo"); return s; }

        static string NewTemp(Job j)
        {
            string d = Path.Combine(Path.GetTempPath(), "FileConverter_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            j.Temps.Add(d);
            return d;
        }

        public static Job Build(string input, string target, string outDir, Quality q)
        {
            if (target == "jpeg")
            {
                // same pictures as JPG, saved with the longer ".jpeg" name
                if (Formats.Ext(input) == "jpeg") throw Err("err_already", "JPEG");
                if (Formats.Ext(input) == "jpg")
                {
                    // already a JPG: just copy it under the new name
                    Job c = new Job();
                    string dst0 = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(input), "jpeg");
                    c.Output = dst0;
                    c.Steps.Add(new JobStep { Native = () => File.Copy(input, dst0) });
                    return c;
                }
                Job j = Build(input, "jpg", outDir, q);
                string src = j.Output, dst = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(input), "jpeg");
                j.Steps.Add(new JobStep { Native = () => { if (File.Exists(dst)) File.Delete(dst); File.Move(src, dst); } });
                j.Output = dst;
                return j;
            }
            string ext = Formats.Ext(input);
            Category cat = Formats.CategoryOf(ext);
            if (In(TextLike, ext) && cat != Category.Data) cat = Category.Document;
            string baseName = Path.GetFileNameWithoutExtension(input);
            string EXT = ext.ToUpperInvariant();
            if (ext == target || (ext == "jpeg" && target == "jpg") || (ext == "tif" && target == "tiff") || (ext == "htm" && target == "html") || (Formats.IsMarkdown(ext) && target == "md"))
                throw Err("err_already", target.ToUpperInvariant());

            Job job = new Job();

            // ---------- audio output
            if (In(AudioT, target))
            {
                if (cat == Category.Image || cat == Category.Document || cat == Category.Data) throw Err("err_no_audio_from", EXT);
                string ff = NeedFF();
                job.Output = Paths.UniquePath(outDir, baseName, target);
                int qi = (int)q;
                string codec;
                switch (target)
                {
                    case "mp3": codec = "-c:a libmp3lame -q:a " + new[] { "0", "2", "5" }[qi]; break;
                    case "wav": codec = "-c:a pcm_s16le"; break;
                    case "aiff": codec = "-c:a pcm_s16be"; break;
                    case "ogg": codec = "-c:a libvorbis -q:a " + new[] { "8", "5", "3" }[qi]; break;
                    case "flac": codec = "-c:a flac"; break;
                    case "opus": codec = "-c:a libopus -b:a " + new[] { "192k", "128k", "96k" }[qi]; break;
                    case "wma": codec = "-c:a wmav2 -b:a " + new[] { "256k", "192k", "128k" }[qi]; break;
                    case "ac3": codec = "-c:a ac3 -b:a " + new[] { "448k", "384k", "192k" }[qi]; break;
                    default: codec = "-c:a aac -b:a " + new[] { "256k", "192k", "128k" }[qi]; break;
                }
                job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = "-hide_banner -y -i " + Q(input) + " -vn -map_metadata 0 " + codec + " " + Q(job.Output) });
                return job;
            }

            // ---------- video output
            if (In(VideoT, target))
            {
                if (cat == Category.Audio) throw Err("err_audio_no_video");
                if (cat == Category.Document || cat == Category.Data) throw Err("err_video_from_doc");
                if (cat == Category.Image && ext != "gif" && ext != "webp") throw Err("err_anim_only");
                string ff = NeedFF();
                job.Output = Paths.UniquePath(outDir, baseName, target);
                string crf = new[] { "18", "22", "27" }[(int)q];
                string scale = q == Quality.Small ? "scale=-2:'trunc(min(720,ih)/2)*2'" : "scale=trunc(iw/2)*2:trunc(ih/2)*2";
                string vf = "-vf \"" + scale + "\"";
                string ab = q == Quality.Small ? "128k" : "192k";
                string codec;
                switch (target)
                {
                    case "webm": codec = "-c:v libvpx-vp9 -crf " + new[] { "28", "32", "38" }[(int)q] + " -b:v 0 -row-mt 1 -pix_fmt yuv420p " + (q == Quality.Small ? vf : "") + " -c:a libopus -b:a 128k"; break;
                    case "avi": codec = "-c:v mpeg4 -q:v " + new[] { "2", "4", "7" }[(int)q] + " " + vf + " -c:a libmp3lame -q:a 2"; break;
                    case "wmv": codec = "-c:v wmv2 -b:v " + new[] { "8M", "5M", "2M" }[(int)q] + " " + vf + " -c:a wmav2 -b:a " + ab; break;
                    case "mpg": codec = "-c:v mpeg2video -q:v " + new[] { "2", "4", "7" }[(int)q] + " " + vf + " -c:a mp2 -b:a " + ab; break;
                    case "3gp": codec = "-c:v libx264 -profile:v baseline -crf " + crf + " -pix_fmt yuv420p -vf \"scale=-2:'trunc(min(480,ih)/2)*2'\" -c:a aac -ar 44100 -b:a 96k"; break;
                    case "mkv": codec = "-c:v libx264 -crf " + crf + " -preset medium -pix_fmt yuv420p " + vf + " -c:a aac -b:a " + ab; break;
                    default: codec = "-c:v libx264 -crf " + crf + " -preset medium -pix_fmt yuv420p " + vf + " -c:a aac -b:a " + ab + " -movflags +faststart"; break;
                }
                job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = "-hide_banner -y -i " + Q(input) + " " + codec + " " + Q(job.Output) });
                return job;
            }

            // ---------- animated GIF from video
            if (target == "gif" && cat != Category.Image)
            {
                if (cat != Category.Video && cat != Category.Unknown) throw Err("err_gif_from", EXT);
                string ff = NeedFF();
                job.Output = Paths.UniquePath(outDir, baseName, "gif");
                int w = new[] { 960, 720, 480 }[(int)q];
                string vf = "fps=" + (q == Quality.Best ? "15" : "12") + ",scale='min(" + w + ",iw)':-2:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse";
                job.Steps.Add(new JobStep { Exe = ff, Ffmpeg = true, Args = "-hide_banner -y -i " + Q(input) + " -vf " + Q(vf) + " -loop 0 " + Q(job.Output) });
                return job;
            }

            // ---------- images (and image -> PDF)
            if (In(ImageT, target) || target == "gif" || (target == "pdf" && cat == Category.Image))
            {
                if (cat == Category.Audio) throw Err("err_no_picture");
                if (cat == Category.Video)
                {
                    if (!In(new[] { "jpg", "png", "bmp", "webp", "tiff" }, target)) throw Err("err_frame");
                    string ff = NeedFF();
                    job.Output = Paths.UniquePath(outDir, baseName, target);
                    job.Steps.Add(new JobStep { Exe = ff, Args = "-hide_banner -y -i " + Q(input) + " -frames:v 1 -q:v 2 " + Q(job.Output) });
                    return job;
                }
                if (cat == Category.Document || cat == Category.Data)
                {
                    if (target != "png" && target != "jpg") throw Err("err_doc_page");
                    job.Output = Paths.UniquePath(outDir, baseName, target);
                    AddOffice(job, input, target, null, job.Output);
                    return job;
                }
                string im = NeedIM();
                job.Output = Paths.UniquePath(outDir, baseName, target);
                string src = input;
                bool multi = In(new[] { "gif", "webp", "tif", "tiff", "ico", "psd" }, ext);
                if ((multi && !In(new[] { "gif", "webp", "tiff", "pdf" }, target)) || ext == "psd") src = input + "[0]";
                int qi = (int)q;
                string ops = "-auto-orient";
                switch (target)
                {
                    case "jpg": ops += " -background white -alpha remove -alpha off -quality " + new[] { "95", "88", "75" }[qi]; break;
                    case "bmp": ops += " -background white -alpha remove -alpha off"; break;
                    case "heic": ops += " -quality " + new[] { "90", "80", "65" }[qi]; break;
                    case "avif": ops += " -quality " + new[] { "85", "70", "55" }[qi]; break;
                    case "webp": ops += " -quality " + new[] { "95", "85", "70" }[qi]; break;
                    case "png": if (q == Quality.Small) ops += " -define png:compression-level=9"; break;
                    case "ico": ops += " -resize \"256x256>\" -define icon:auto-resize=256,128,64,48,32,16"; break;
                }
                if (q == Quality.Small && target != "ico") ops += " -resize \"2560x2560>\"";
                job.Steps.Add(new JobStep { Exe = im, Args = Q(src) + " " + ops + " " + Q(job.Output) });
                return job;
            }

            // ---------- data formats
            if (In(DataT, target))
            {
                if (cat != Category.Data) throw Err("err_data_only");
                job.Output = Paths.UniquePath(outDir, baseName, target);
                bool sheetIn = In(Sheets, ext);
                bool sheetOut = target == "xlsx" || target == "ods";
                string outp = job.Output;
                if (sheetIn && sheetOut)
                {
                    AddOffice(job, input, target == "xlsx" ? "xlsx:\"Calc MS Excel 2007 XML\"" : "ods", null, outp);
                }
                else if (sheetIn)
                {
                    string tmp = NewTemp(job);
                    string csv = Path.Combine(tmp, "data.csv");
                    AddOffice(job, input, "csv:\"Text - txt - csv (StarCalc)\":44,34,76,1", null, csv);
                    if (target == "csv") job.Steps[job.Steps.Count - 1].CollectTo = outp;
                    else job.Steps.Add(new JobStep { Native = () => DataConv.Write(DataConv.Read(csv), outp, target) });
                }
                else if (sheetOut)
                {
                    string tmp = NewTemp(job);
                    string csv = Path.Combine(tmp, baseName + ".csv");
                    job.Steps.Add(new JobStep { Native = () => File.WriteAllText(csv, DataConv.WriteDelimited(ReadTable(input), ','), new UTF8Encoding(false)) });
                    AddOffice(job, csv, target == "xlsx" ? "xlsx:\"Calc MS Excel 2007 XML\"" : "ods", "CSV:44,34,76,1", outp);
                }
                else job.Steps.Add(new JobStep { Native = () => DataConv.Write(ReadTable(input), outp, target) });
                return job;
            }

            // ---------- documents
            if (target == "pdf" || In(DocT, target))
            {
                if (cat == Category.Audio || cat == Category.Video) throw Err("err_media_doc", EXT);
                if (cat == Category.Image) throw Err("err_img_pdf");
                job.Output = Paths.UniquePath(outDir, baseName, target);
                string outp = job.Output;
                bool md = Formats.IsMarkdown(ext);
                bool html = ext == "html" || ext == "htm";
                bool text = In(TextLike, ext);

                if (cat == Category.Data)
                {
                    if (target == "pptx" || target == "odp" || target == "epub") throw Err("err_unsupported");
                    if (In(Sheets, ext) && (target == "pdf" || target == "html" || target == "odt" || target == "docx" || target == "rtf" || target == "txt"))
                    {
                        if (target == "pdf" || target == "html") { AddOffice(job, input, target, null, outp); return job; }
                        throw Err("err_unsupported");
                    }
                    if (In(Sheets, ext)) throw Err("err_unsupported");
                    // csv/json/xml -> table as Markdown / HTML / text / PDF
                    if (target == "md") { job.Steps.Add(new JobStep { Native = () => File.WriteAllText(outp, TableMarkdown(ReadTable(input)), new UTF8Encoding(false)) }); return job; }
                    if (target == "txt") { job.Steps.Add(new JobStep { Native = () => File.WriteAllText(outp, DataConv.WriteDelimited(ReadTable(input), '\t'), new UTF8Encoding(true)) }); return job; }
                    string tmp = NewTemp(job);
                    string h = target == "html" ? outp : Path.Combine(tmp, baseName + ".html");
                    job.Steps.Add(new JobStep { Native = () => File.WriteAllText(h, Markdown.ToHtmlDocument(TableMarkdown(ReadTable(input)), baseName), new UTF8Encoding(false)) });
                    if (target == "html") return job;
                    if (target == "pdf") { AddHtmlToPdf(job, h, outp); return job; }
                    AddOffice(job, h, target, "HTML (StarWriter)", outp);
                    return job;
                }

                if (md || html || text)
                {
                    if (target == "pptx" || target == "odp") throw Err("err_unsupported");
                    Func<string> readSrc = () => DataConv.ReadText(input);
                    if (target == "md")
                    {
                        job.Steps.Add(new JobStep { Native = () => File.WriteAllText(outp, html ? Markdown.FromHtml(readSrc()) : readSrc(), new UTF8Encoding(false)) });
                        return job;
                    }
                    if (target == "txt")
                    {
                        job.Steps.Add(new JobStep { Native = () => File.WriteAllText(outp, md ? Markdown.ToPlainText(readSrc()) : Markdown.HtmlToText(readSrc()), new UTF8Encoding(true)) });
                        return job;
                    }
                    string srcDir = Path.GetDirectoryName(Path.GetFullPath(input));
                    Func<string> toHtml = () => md ? Markdown.ToHtmlDocument(readSrc(), baseName, srcDir) : text ? Markdown.TextToHtmlDocument(readSrc(), baseName) : readSrc();
                    if (target == "html") { job.Steps.Add(new JobStep { Native = () => File.WriteAllText(outp, md ? Markdown.ToHtmlDocument(readSrc(), baseName) : toHtml(), new UTF8Encoding(false)) }); return job; }
                    string tmp = NewTemp(job);
                    string h = Path.Combine(tmp, SafeName(baseName) + ".html");
                    if (html && !md) h = input; // keep relative images working
                    else job.Steps.Add(new JobStep { Native = () => File.WriteAllText(h, toHtml(), new UTF8Encoding(false)) });
                    if (target == "pdf") { AddHtmlToPdf(job, h, outp); return job; }
                    AddOffice(job, h, target, "HTML (StarWriter)", outp);
                    return job;
                }

                // office documents and PDF
                string infilter = null;
                if (ext == "pdf") infilter = (target == "pptx" || target == "odp") ? "impress_pdf_import" : "writer_pdf_import";
                if (target == "md")
                {
                    string tmp = NewTemp(job);
                    string h = Path.Combine(tmp, "doc.html");
                    AddOffice(job, input, "html", infilter, h);
                    job.Steps.Add(new JobStep { Native = () => File.WriteAllText(outp, Markdown.FromHtml(DataConv.ReadText(h)), new UTF8Encoding(false)) });
                    return job;
                }
                string filter = target == "txt" ? "txt:Text" : target;
                AddOffice(job, input, filter, infilter, outp);
                return job;
            }
            throw Err("err_unsupported");
        }

        static Table ReadTable(string path)
        {
            try { return DataConv.Read(path); }
            catch (Exception ex) { throw Err("err_data", ex.Message); }
        }

        static string TableMarkdown(Table t)
        {
            StringBuilder sb = new StringBuilder();
            Func<string, string> esc = v => (v ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
            sb.Append('|'); foreach (string h in t.Headers) sb.Append(' ').Append(esc(h)).Append(" |"); sb.Append('\n');
            sb.Append('|'); foreach (string h in t.Headers) sb.Append(" --- |"); sb.Append('\n');
            foreach (List<string> r in t.Rows)
            {
                sb.Append('|');
                for (int c = 0; c < t.Headers.Count; c++) sb.Append(' ').Append(esc(c < r.Count ? r[c] : "")).Append(" |");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static void AddOffice(Job job, string input, string filter, string infilter, string collectTo)
        {
            string so = NeedLO();
            string tmp = NewTemp(job);
            string profile = new Uri(Path.Combine(Paths.AppDir, "lo-profile")).AbsoluteUri;
            string args = "-env:UserInstallation=" + profile + " --headless --norestore --nologo " +
                          (infilter != null ? "--infilter=" + Q(infilter) + " " : "") +
                          "--convert-to " + filter + " --outdir " + Q(tmp) + " " + Q(input);
            job.Steps.Add(new JobStep { Exe = so, Args = args, TmpDir = tmp, CollectTo = collectTo, Weight = 2 });
        }

        /// HTML -> PDF with Microsoft Edge (best looking) or LibreOffice.
        static void AddHtmlToPdf(Job job, string html, string pdf)
        {
            string edge = Tools.Edge;
            if (edge != null)
            {
                string prof = NewTemp(job);
                string uri = new Uri(html).AbsoluteUri;
                job.Steps.Add(new JobStep
                {
                    Exe = edge,
                    Args = "--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions --no-pdf-header-footer --run-all-compositor-stages-before-draw --virtual-time-budget=4000 --user-data-dir=" + Q(prof) + " --print-to-pdf=" + Q(pdf) + " " + Q(uri),
                    Weight = 2,
                    WaitFor = pdf
                });
                return;
            }
            if (Tools.Soffice == null) throw Err("err_pdf_needs");
            AddOffice(job, html, "pdf:writer_pdf_Export", "HTML (StarWriter)", pdf);
        }

        static readonly Regex DurationRx = new Regex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");

        /// Runs a job. Returns null on success, or an error message.
        public static string Run(Job job, Action<double> progress, Func<bool> cancelled)
        {
            double total = 0; foreach (JobStep s in job.Steps) total += s.Weight;
            double done = 0;
            try
            {
                foreach (JobStep step in job.Steps)
                {
                    if (cancelled()) { Cleanup(job, true); return L.T("cancelled"); }
                    double w = step.Weight / total, d0 = done;
                    if (step.Native != null)
                    {
                        try { step.Native(); }
                        catch (UserError ue) { Cleanup(job, true); return ue.Message; }
                        catch (Exception ex) { Cleanup(job, true); return ex.Message; }
                    }
                    else
                    {
                        string err = RunProcess(step, f => { if (progress != null) progress(Math.Min(0.99, d0 + f * w)); }, cancelled);
                        if (err != null) { Cleanup(job, true); return err; }
                        if (step.WaitFor != null && !WaitForFile(step.WaitFor, 90, cancelled))
                        {
                            Cleanup(job, true);
                            return L.T("err_pdf_edge");
                        }
                        if (step.CollectTo != null)
                        {
                            string[] produced = Directory.Exists(step.TmpDir) ? Directory.GetFiles(step.TmpDir) : new string[0];
                            if (produced.Length == 0) { Cleanup(job, true); return L.T("err_lo_fail"); }
                            if (File.Exists(step.CollectTo)) File.Delete(step.CollectTo);
                            File.Move(produced[0], step.CollectTo);
                        }
                    }
                    done += w;
                    if (progress != null) progress(Math.Min(0.99, done));
                }
                if (job.Output == null || !File.Exists(job.Output) || new FileInfo(job.Output).Length == 0)
                {
                    Cleanup(job, true);
                    return L.T("err_unknown");
                }
                Cleanup(job, false);
                return null;
            }
            catch (Exception ex) { Cleanup(job, true); return ex.Message; }
        }

        /// Waits until a file exists and has stopped growing.
        static bool WaitForFile(string path, int seconds, Func<bool> cancelled)
        {
            long last = -1; int stable = 0;
            for (int i = 0; i < seconds * 4; i++)
            {
                if (cancelled()) return false;
                if (File.Exists(path))
                {
                    long len = 0; try { len = new FileInfo(path).Length; } catch { }
                    if (len > 0 && len == last) { if (++stable >= 3) return true; } else stable = 0;
                    last = len;
                }
                System.Threading.Thread.Sleep(250);
            }
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }

        static string RunProcess(JobStep step, Action<double> progress, Func<bool> cancelled)
        {
            string args = step.Ffmpeg ? "-nostats -progress pipe:1 " + step.Args : step.Args;
            StringBuilder err = new StringBuilder();
            double duration = step.Duration;
            int code = Tools.Stream(step.Exe, args,
                line =>
                {
                    if (!step.Ffmpeg) { lock (err) if (err.Length < 100000) err.AppendLine(line); return; }
                    if (line.StartsWith("out_time_us=") || line.StartsWith("out_time_ms="))
                    {
                        long us;
                        if (long.TryParse(line.Substring(12), out us) && duration > 0) progress(Math.Min(0.99, us / 1e6 / duration));
                    }
                },
                line =>
                {
                    lock (err) if (err.Length < 200000) err.AppendLine(line);
                    if (duration <= 0)
                    {
                        Match m = DurationRx.Match(line);
                        if (m.Success)
                            duration = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    }
                }, cancelled);
            if (code == -2) return L.T("cancelled");
            string text; lock (err) text = err.ToString();
            if (code != 0) return Friendly(text);
            return null;
        }

        public static string Friendly(string err)
        {
            if (err.Contains("does not contain any stream") || err.Contains("Output file is empty") || err.Contains("matches no streams") || err.Contains("Output file does not contain"))
                return L.T("err_no_stream");
            if (err.Contains("Invalid data found when processing input")) return L.T("err_damaged");
            if (err.Contains("no decode delegate") || err.Contains("NoDecodeDelegate")) return L.T("err_img_read");
            return Tools.LastLines(err, 1);
        }

        static void Cleanup(Job job, bool deleteOutput)
        {
            foreach (string t in job.Temps) try { if (Directory.Exists(t)) Directory.Delete(t, true); else if (File.Exists(t)) File.Delete(t); } catch { }
            if (deleteOutput) try { if (job.Output != null && File.Exists(job.Output)) File.Delete(job.Output); } catch { }
        }
    }
}
