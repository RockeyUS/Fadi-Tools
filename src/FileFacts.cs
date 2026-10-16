using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace FileConverter
{
    /// What a file says about itself: when a photo or video was taken, and a song's artist, title and track.
    public class Facts
    {
        public DateTime Taken;
        /// No date inside the file: Taken is when the file was last changed.
        public bool TakenFromFile;
        public string Artist = "", Title = "", Album = "";
        public int Track;
    }

    public static class FileFacts
    {
        static readonly Dictionary<string, Facts> cache = new Dictionary<string, Facts>(StringComparer.OrdinalIgnoreCase);

        public static Facts Cached(string path)
        {
            lock (cache) { Facts f; return cache.TryGetValue(path, out f) ? f : null; }
        }

        /// Reads (and remembers) a file's facts. Can take a moment (it may ask FFmpeg): call it off the UI thread.
        public static Facts Read(string path)
        {
            Facts f = Cached(path);
            if (f != null) return f;
            f = new Facts();
            string ext = Formats.Ext(path);
            Category cat = Formats.CategoryOf(ext);
            DateTime? when = null;
            try
            {
                if (ext == "jpg" || ext == "jpeg" || ext == "tif" || ext == "tiff") when = ExifDate(path);
                if (when == null && (cat == Category.Image || cat == Category.Video || cat == Category.Audio)) Tags(path, f, ref when);
                if (when == null && cat == Category.Image && Tools.Magick != null) when = MagickDate(path);
            }
            catch { }
            if (when.HasValue) f.Taken = when.Value;
            else { try { f.Taken = File.GetLastWriteTime(path); } catch { f.Taken = DateTime.Now; } f.TakenFromFile = true; }
            lock (cache) cache[path] = f;
            return f;
        }

        /// The moment a photo was taken, from its EXIF data (JPEG and TIFF).
        static DateTime? ExifDate(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (Image im = Image.FromStream(fs, false, false))
            {
                foreach (int id in new[] { 0x9003, 0x9004, 0x0132 })
                {
                    try
                    {
                        System.Drawing.Imaging.PropertyItem p = im.GetPropertyItem(id);
                        if (p == null || p.Value == null) continue;
                        DateTime d;
                        if (ParseExif(System.Text.Encoding.ASCII.GetString(p.Value).Trim('\0', ' '), out d)) return d;
                    }
                    catch { }
                }
            }
            return null;
        }

        static bool ParseExif(string s, out DateTime d)
        {
            return DateTime.TryParseExact(s, new[] { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd" }, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d) && d.Year > 1900;
        }

        /// Tags FFmpeg can read: a video's or HEIC photo's creation time, a song's artist/title/album/track.
        static void Tags(string path, Facts f, ref DateTime? when)
        {
            string probe = Tools.FFprobe;
            if (probe == null) return;
            string output;
            if (Tools.RunHidden(probe, "-v error -show_entries format_tags:stream_tags -of json " + Tools.Quote(path), 30000, out output) != 0) return;
            int start = output.IndexOf('{');
            if (start < 0) return;
            List<KeyValuePair<string, string>> tags = new List<KeyValuePair<string, string>>();
            Collect(new JsonParser(output.Substring(start)).Parse(), tags);
            foreach (KeyValuePair<string, string> t in tags)
            {
                string k = t.Key.ToLowerInvariant(), v = (t.Value ?? "").Trim();
                if (v.Length == 0) continue;
                if (k == "artist" && f.Artist.Length == 0) f.Artist = v;
                else if (k == "album_artist" && f.Artist.Length == 0) f.Artist = v;
                else if (k == "title" && f.Title.Length == 0) f.Title = v;
                else if (k == "album" && f.Album.Length == 0) f.Album = v;
                else if (k == "track" && f.Track == 0) { int n; if (int.TryParse(v.Split('/')[0].Trim(), out n)) f.Track = n; }
                else if ((k == "creation_time" || k == "com.apple.quicktime.creationdate" || k == "date") && when == null)
                {
                    DateTime d;
                    if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d) && d.Year > 1971)
                        when = k == "creation_time" ? d.ToLocalTime() : d;
                    else if (ParseExif(v, out d)) when = d;
                }
            }
        }

        static void Collect(object o, List<KeyValuePair<string, string>> tags)
        {
            JsonObject jo = o as JsonObject;
            if (jo != null)
            {
                foreach (KeyValuePair<string, object> kv in jo)
                {
                    if (kv.Key == "tags" && kv.Value is JsonObject)
                        foreach (KeyValuePair<string, object> t in (JsonObject)kv.Value) tags.Add(new KeyValuePair<string, string>(t.Key, Convert.ToString(t.Value, CultureInfo.InvariantCulture)));
                    else Collect(kv.Value, tags);
                }
                return;
            }
            List<object> list = o as List<object>;
            if (list != null) foreach (object x in list) Collect(x, tags);
        }

        static DateTime? MagickDate(string path)
        {
            string output;
            if (Tools.RunHidden(Tools.Magick, "identify -format \"%[EXIF:DateTimeOriginal]\" " + Tools.Quote(path + "[0]"), 30000, out output) != 0) return null;
            DateTime d;
            return ParseExif((output ?? "").Trim(), out d) ? d : (DateTime?)null;
        }
    }
}
