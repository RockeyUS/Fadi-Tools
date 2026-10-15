using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FileConverter
{
    /// Turns pictures, GIFs and videos into stickers that fit each app's rules (size, format, file size).
    public static class Stickers
    {
        public class Spec
        {
            public string Key;
            public int Box;            // pixels
            public bool Square;        // padded to a square (with a see-through border)
            public int Margin;         // empty border inside the square
            public long StaticMax, AnimMax;
            public string StaticExt, AnimExt;
            public double MaxSeconds;
        }

        public static readonly Spec[] All =
        {
            // WhatsApp: 512×512 WEBP, under 100 KB still, 500 KB moving
            new Spec { Key = "wa", Box = 512, Square = true, Margin = 16, StaticMax = 100 * 1000, AnimMax = 500 * 1000, StaticExt = "webp", AnimExt = "webp", MaxSeconds = 8 },
            // Telegram (@Stickers bot): 512 px on the long side; still PNG/WEBP ≤ 512 KB, video sticker WEBM VP9 ≤ 256 KB, ≤ 3 s
            new Spec { Key = "tg", Box = 512, Square = false, StaticMax = 512 * 1000, AnimMax = 256 * 1000, StaticExt = "webp", AnimExt = "webm", MaxSeconds = 3 },
            // Discord server sticker: 320×320 PNG (or GIF when it moves), ≤ 512 KB
            new Spec { Key = "dc", Box = 320, Square = true, StaticMax = 500 * 1024, AnimMax = 500 * 1024, StaticExt = "png", AnimExt = "gif", MaxSeconds = 5 },
            // Discord emoji: 128×128, ≤ 256 KB
            new Spec { Key = "emoji", Box = 128, Square = true, StaticMax = 250 * 1024, AnimMax = 250 * 1024, StaticExt = "png", AnimExt = "gif", MaxSeconds = 5 },
        };

        public static Spec Get(string key) { foreach (Spec s in All) if (s.Key == key) return s; return All[0]; }

        static string Q(string s) { return Tools.Quote(s); }

        /// True for videos and for GIF/WEBP pictures with more than one frame.
        public static bool Moves(string path)
        {
            string e = Formats.Ext(path);
            Category c = Formats.CategoryOf(e);
            if (c == Category.Video) return true;
            if (e != "gif" && e != "webp") return false;
            string im = Tools.Magick;
            if (im == null) return e == "gif";
            try
            {
                string o;
                if (Tools.RunHidden(im, Q(path) + " -format \"%n\\n\" info:", 60000, out o) == 0)
                {
                    Match m = Regex.Match(o, @"(\d+)");
                    return m.Success && int.Parse(m.Groups[1].Value) > 1;
                }
            }
            catch { }
            return e == "gif";
        }

        public static string ExtFor(string key, string path) { Spec s = Get(key); return Moves(path) ? s.AnimExt : s.StaticExt; }

        /// Makes the sticker. Throws UserError with a plain message when it can't.
        public static void Make(string input, string key, string output, Func<bool> cancelled)
        {
            Spec s = Get(key);
            if (Moves(input)) MakeMoving(input, s, output, cancelled);
            else MakeStill(input, s, output);
        }

        // ------------------------------------------------------------------ still pictures (ImageMagick)
        static void MakeStill(string input, Spec s, string output)
        {
            string im = Tools.Magick;
            if (im == null) throw new UserError(L.T("err_need_im"));
            int inner = s.Box - s.Margin * 2;
            string fit = s.Square
                ? " -resize " + inner + "x" + inner + " -background none -gravity center -extent " + s.Box + "x" + s.Box
                : " -resize " + s.Box + "x" + s.Box;
            string baseArgs = Q(input + "[0]") + " -auto-orient -strip" + fit;
            string err = null;
            if (s.StaticExt == "webp")
            {
                foreach (int q in new[] { 92, 82, 72, 60, 48, 36, 25 })
                {
                    string o;
                    if (Tools.RunHidden(im, baseArgs + " -quality " + q + " -define webp:method=6 -define webp:alpha-quality=" + Math.Max(40, q) + " " + Q(output), 120000, out o) != 0) { err = o; break; }
                    if (Size(output) <= s.StaticMax) return;
                }
            }
            else
            {
                foreach (string ops in new[] { "-define png:compression-level=9", "-dither FloydSteinberg -colors 256 -define png:compression-level=9", "-dither FloydSteinberg -colors 64 -define png:compression-level=9" })
                {
                    string o;
                    if (Tools.RunHidden(im, baseArgs + " " + ops + " " + Q(output), 120000, out o) != 0) { err = o; break; }
                    if (Size(output) <= s.StaticMax) return;
                }
            }
            if (err != null) throw new UserError(L.T("st_failed") + " " + LastLine(err));
            throw new UserError(L.T("st_too_big", Theme.HumanSize(s.StaticMax)));
        }

        // ------------------------------------------------------------------ moving stickers (ffmpeg)
        static void MakeMoving(string input, Spec s, string output, Func<bool> cancelled)
        {
            string ff = Tools.FFmpeg;
            if (ff == null) throw new UserError(L.T("err_need_ff"));
            string src = input, tmpGif = null;
            // ffmpeg can't read moving WEBP pictures: turn them into a GIF first
            if (Formats.Ext(input) == "webp")
            {
                string im = Tools.Magick;
                if (im == null) throw new UserError(L.T("err_need_im"));
                tmpGif = Path.Combine(Path.GetTempPath(), "fadi_st_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".gif");
                string o;
                if (Tools.RunHidden(im, Q(input) + " -coalesce " + Q(tmpGif), 300000, out o) != 0) throw new UserError(L.T("st_failed") + " " + LastLine(o));
                src = tmpGif;
            }
            try
            {
                int inner = s.Box - s.Margin * 2;
                string err = null;
                // tries from best looking to smallest until the file is small enough
                object[][] tries;
                if (s.AnimExt == "webm") tries = new[] { new object[] { 30, 30, 0 }, new object[] { 30, 38, 0 }, new object[] { 24, 45, 0 }, new object[] { 20, 52, 0 }, new object[] { 15, 60, 0 } };
                else if (s.AnimExt == "webp") tries = new[] { new object[] { 15, 75, 0 }, new object[] { 15, 60, 0 }, new object[] { 12, 48, 0 }, new object[] { 10, 38, 0 }, new object[] { 10, 28, 1 }, new object[] { 8, 20, 2 } };
                else tries = new[] { new object[] { 15, 256, 0 }, new object[] { 12, 128, 0 }, new object[] { 10, 96, 1 }, new object[] { 10, 64, 1 }, new object[] { 8, 48, 2 }, new object[] { 6, 32, 2 } };
                foreach (object[] t in tries)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    int fps = (int)t[0], level = (int)t[1], shorter = (int)t[2];
                    double secs = s.MaxSeconds * (shorter == 0 ? 1 : shorter == 1 ? 0.75 : 0.5);
                    string size = "scale=" + inner + ":" + inner + ":force_original_aspect_ratio=decrease:flags=lanczos";
                    string pad = s.Square ? ",pad=" + s.Box + ":" + s.Box + ":(ow-iw)/2:(oh-ih)/2:color=0x00000000" : "";
                    string vf = "fps=" + fps + "," + size + ",format=rgba" + pad;
                    string args;
                    string t0 = " -t " + secs.ToString("0.##", CultureInfo.InvariantCulture);
                    if (s.AnimExt == "webm")
                        args = "-hide_banner -y -i " + Q(src) + t0 + " -an -vf " + Q(vf + ",format=yuva420p") + " -c:v libvpx-vp9 -pix_fmt yuva420p -b:v 0 -crf " + level + " -deadline good -row-mt 1 " + Q(output);
                    else if (s.AnimExt == "webp")
                        args = "-hide_banner -y -i " + Q(src) + t0 + " -an -vf " + Q(vf) + " -c:v libwebp -lossless 0 -q:v " + level + " -compression_level 6 -loop 0 " + Q(output);
                    else
                        args = "-hide_banner -y -i " + Q(src) + t0 + " -an -vf " + Q(vf + ",split[a][b];[a]palettegen=max_colors=" + level + ":reserve_transparent=1:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=3:alpha_threshold=128") + " -loop 0 " + Q(output);
                    string o;
                    int code = Tools.RunHidden(ff, args, 600000, out o);
                    if (code != 0)
                    {
                        // an ffmpeg without the WEBP encoder: make a GIF and let ImageMagick turn it into WEBP
                        if (s.AnimExt == "webp" && o.IndexOf("libwebp", StringComparison.OrdinalIgnoreCase) >= 0) { WebpByMagick(ff, src, vf, secs, s, output); return; }
                        err = o; break;
                    }
                    if (Size(output) <= s.AnimMax) return;
                }
                if (err != null) throw new UserError(L.T("st_failed") + " " + LastLine(err));
                throw new UserError(L.T("st_too_big", Theme.HumanSize(s.AnimMax)));
            }
            finally { if (tmpGif != null) try { File.Delete(tmpGif); } catch { } }
        }

        static void WebpByMagick(string ff, string src, string vf, double secs, Spec s, string output)
        {
            string im = Tools.Magick;
            if (im == null) throw new UserError(L.T("err_need_im"));
            string gif = Path.Combine(Path.GetTempPath(), "fadi_st_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".gif");
            try
            {
                string o;
                string args = "-hide_banner -y -i " + Q(src) + " -t " + secs.ToString("0.##", CultureInfo.InvariantCulture) + " -an -vf " + Q(vf + ",split[a][b];[a]palettegen=reserve_transparent=1[p];[b][p]paletteuse") + " -loop 0 " + Q(gif);
                if (Tools.RunHidden(ff, args, 600000, out o) != 0) throw new UserError(L.T("st_failed") + " " + LastLine(o));
                foreach (int q in new[] { 70, 55, 40, 30, 20 })
                {
                    if (Tools.RunHidden(im, Q(gif) + " -coalesce -quality " + q + " -define webp:method=4 " + Q(output), 600000, out o) != 0) throw new UserError(L.T("st_failed") + " " + LastLine(o));
                    if (Size(output) <= s.AnimMax) return;
                }
                throw new UserError(L.T("st_too_big", Theme.HumanSize(s.AnimMax)));
            }
            finally { try { File.Delete(gif); } catch { } }
        }

        static long Size(string f) { try { return new FileInfo(f).Length; } catch { return long.MaxValue; } }

        static string LastLine(string o)
        {
            if (string.IsNullOrEmpty(o)) return "";
            string[] lines = o.Trim().Split('\n');
            return lines[lines.Length - 1].Trim();
        }
    }
}
