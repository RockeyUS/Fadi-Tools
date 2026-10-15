using System;
using System.IO;

namespace FileConverter
{
    public enum Category { Image, Audio, Video, Document, Data, Unknown }
    public enum Quality { Best, Balanced, Small }

    public static class Formats
    {
        public static readonly string[] ImageExt = { "jpg", "jpeg", "jfif", "png", "bmp", "gif", "tif", "tiff", "webp", "heic", "heif", "avif", "ico",
            "jxl", "psd", "tga", "dds", "svg", "jp2", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "pef", "srw", "exr", "hdr", "pcx", "ppm", "pgm" };
        public static readonly string[] AudioExt = { "mp3", "wav", "ogg", "oga", "flac", "aac", "m4a", "opus", "wma", "aiff", "aif", "amr", "ac3",
            "mka", "ape", "wv", "caf", "mp2", "au", "3ga", "m4b", "dts", "eac3", "mid", "midi" };
        public static readonly string[] VideoExt = { "mp4", "mov", "mkv", "avi", "webm", "wmv", "flv", "m4v", "mpg", "mpeg", "3gp", "3g2", "ts", "mts",
            "m2ts", "vob", "ogv", "hevc", "asf", "f4v", "divx", "mxf", "rm", "rmvb" };
        public static readonly string[] DocExt = { "pdf", "doc", "docx", "odt", "rtf", "txt", "html", "htm", "md", "markdown", "mdown", "ppt", "pptx", "odp",
            "epub", "wpd", "pages", "key", "xps", "log", "ini", "cfg" };
        public static readonly string[] DataExt = { "csv", "tsv", "json", "xml", "xlsx", "xls", "ods", "numbers", "jsonl" };

        public static readonly string[] TabKeys = { "tab_image", "tab_audio", "tab_video", "tab_doc", "tab_data" };
        public static readonly string[] TabGlyphs = { Ico.Photo, Ico.Music, Ico.Video, Ico.Doc, Ico.Code };

        public static readonly string[][] Targets =
        {
            new[] { "jpg", "jpeg", "png", "webp", "heic", "avif", "bmp", "tiff", "gif", "ico", "pdf", "sticker" },
            new[] { "mp3", "wav", "ogg", "flac", "m4a", "aac", "opus", "wma", "aiff", "ac3" },
            new[] { "mp4", "mov", "mkv", "avi", "webm", "wmv", "m4v", "mpg", "3gp", "gif", "sticker" },
            new[] { "pdf", "docx", "odt", "rtf", "txt", "html", "md", "epub", "pptx", "odp" },
            new[] { "xlsx", "ods", "csv", "tsv", "json", "xml" },
        };

        public static readonly string[] ViewerExt = { "heic", "heif", "avif", "webp", "jxl", "psd", "tga", "dds", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf" };

        public static string Ext(string path) { return Path.GetExtension(path).TrimStart('.').ToLowerInvariant(); }

        public static Category CategoryOf(string ext)
        {
            if (Array.IndexOf(ImageExt, ext) >= 0) return Category.Image;
            if (Array.IndexOf(AudioExt, ext) >= 0) return Category.Audio;
            if (Array.IndexOf(VideoExt, ext) >= 0) return Category.Video;
            if (Array.IndexOf(DataExt, ext) >= 0) return Category.Data;
            if (Array.IndexOf(DocExt, ext) >= 0) return Category.Document;
            return Category.Unknown;
        }

        public static string CategoryKey(Category c)
        {
            switch (c)
            {
                case Category.Image: return "tab_image";
                case Category.Audio: return "tab_audio";
                case Category.Video: return "tab_video";
                case Category.Document: return "tab_doc";
                case Category.Data: return "tab_data";
                default: return "cat_file";
            }
        }

        public static string GlyphFor(Category c)
        {
            switch (c)
            {
                case Category.Image: return Ico.Photo;
                case Category.Audio: return Ico.Music;
                case Category.Video: return Ico.Video;
                case Category.Document: return Ico.Doc;
                case Category.Data: return Ico.Code;
                default: return Ico.Page;
            }
        }

        public static bool IsMarkdown(string ext) { return ext == "md" || ext == "markdown" || ext == "mdown"; }

        public static void Suggest(string ext, out int tab, out string target)
        {
            switch (CategoryOf(ext))
            {
                case Category.Image: tab = 0; target = (ext == "jpg" || ext == "jpeg" || ext == "jfif") ? "png" : "jpg"; return;
                case Category.Audio: tab = 1; target = ext == "mp3" ? "wav" : "mp3"; return;
                case Category.Video:
                    if (ext == "mp4") { tab = 1; target = "mp3"; return; }
                    tab = 2; target = "mp4"; return;
                case Category.Document:
                    tab = 3;
                    if (ext == "pdf") target = "docx";
                    else if (IsMarkdown(ext)) target = "pdf";
                    else if (ext == "html" || ext == "htm") target = "md";
                    else target = "pdf";
                    return;
                case Category.Data:
                    tab = 4;
                    if (ext == "csv" || ext == "tsv") target = "xlsx";
                    else if (ext == "json" || ext == "jsonl" || ext == "xml") target = "csv";
                    else target = "csv";
                    return;
                default: tab = 0; target = "jpg"; return;
            }
        }
    }
}
