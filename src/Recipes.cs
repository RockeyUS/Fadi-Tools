using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// One step of a recipe: what it does and its setting (crop "1:1", resize "1080", convert "jpg", compress "50"…).
    public class RecipeStep
    {
        public string Kind, Value;
        public RecipeStep(string kind, string value) { Kind = kind; Value = value ?? ""; }
        public RecipeStep Copy() { return new RecipeStep(Kind, Value); }
    }

    /// Several things done one after another to a file, as one slice of the Donut Wheel (or one watch-folder rule):
    /// e.g. "Square JPG": crop 1:1 → resize to 1080 → JPG → 60% of the size.
    public class Recipe
    {
        public string Id, Name, For;   // For: image, video or audio
        public List<RecipeStep> Steps = new List<RecipeStep>();
        public Recipe Copy()
        {
            Recipe r = new Recipe { Id = Id, Name = Name, For = For };
            foreach (RecipeStep s in Steps) r.Steps.Add(s.Copy());
            return r;
        }
    }

    public static class Recipes
    {
        public const string Prefix = "r:";
        public static readonly string[] Kinds = { "image", "video", "audio" };

        /// The step kinds a recipe for this kind of file can have, in the order they're offered.
        public static string[] StepKinds(string kind)
        {
            if (kind == "image") return new[] { "crop", "resize", "rotate", "nobg", "convert", "compress" };
            return new[] { "convert", "compress" };
        }

        /// The settings a step can have (what its chip offers).
        public static string[] Choices(string step, string kind)
        {
            switch (step)
            {
                case "crop": return new[] { "1:1", "4:5", "3:2", "4:3", "16:9", "9:16" };
                case "resize": return new[] { "512", "720", "1080", "1280", "1600", "1920", "2560", "3840" };
                case "rotate": return new[] { "90", "180", "270" };
                case "convert": return WheelConfig.Pool(kind);
                case "compress": return new[] { "25", "40", "50", "60", "75", "90" };
                default: return new string[0];
            }
        }

        public static string DefaultChoice(string step, string kind)
        {
            switch (step)
            {
                case "crop": return "1:1";
                case "resize": return "1080";
                case "rotate": return "90";
                case "convert": return kind == "image" ? "jpg" : kind == "video" ? "mp4" : "mp3";
                case "compress": return "50";
                default: return "";
            }
        }

        public static string StepName(string step)
        {
            switch (step)
            {
                case "crop": return L.T("wh_crop");
                case "resize": return L.T("rc_resize");
                case "rotate": return L.T("wh_rotate");
                case "nobg": return L.T("wh_nobg_short");
                case "convert": return L.T("rc_convert");
                default: return L.T("wh_compress");
            }
        }

        public static string StepGlyph(string step)
        {
            switch (step)
            {
                case "crop": return Ico.Crop;
                case "resize": return Ico.Scale;
                case "rotate": return Ico.Rotate;
                case "nobg": return Ico.Cut;
                case "convert": return Ico.Convert;
                default: return "";
            }
        }

        /// How a setting reads on its chip: "1:1", "1080 px", "JPG", "90° →", "50%".
        public static string ChoiceLabel(string step, string v)
        {
            switch (step)
            {
                case "resize": return L.T("rc_long_side", v);
                case "rotate": return v == "90" ? L.T("rt_right") : v == "270" ? L.T("rt_left") : "180°";
                case "convert": return v.ToUpperInvariant();
                case "compress": return L.T("rc_pct", v);
                default: return v;
            }
        }

        /// One line saying what the recipe does: "Crop 1:1 → 1080 px → JPG → 50%".
        public static string Summary(Recipe r)
        {
            List<string> parts = new List<string>();
            foreach (RecipeStep s in r.Steps) parts.Add(s.Kind == "nobg" ? StepName(s.Kind) : s.Kind == "crop" ? StepName(s.Kind) + " " + s.Value : ChoiceLabel(s.Kind, s.Value));
            return parts.Count == 0 ? L.T("rc_empty") : string.Join("  →  ", parts.ToArray());
        }

        // ------------------------------------------------------------------ kept in the settings
        // RecipeIds=a1b2,c3d4  and  Recipe.a1b2=image|Square JPG|crop=1:1;resize=1080;convert=jpg;compress=60

        static void SeedOnce()
        {
            if (Settings.Get("RecipesSeeded", "0") == "1") return;
            Settings.Set("RecipesSeeded", "1");
            if (Settings.Get("RecipeIds", "").Length > 0) return;
            Recipe r = new Recipe { Id = Prefix + "square", Name = L.T("rc_sample"), For = "image" };
            r.Steps.Add(new RecipeStep("crop", "1:1"));
            r.Steps.Add(new RecipeStep("resize", "1080"));
            r.Steps.Add(new RecipeStep("convert", "jpg"));
            r.Steps.Add(new RecipeStep("compress", "60"));
            Save(r);
        }

        public static List<Recipe> All()
        {
            SeedOnce();
            List<Recipe> l = new List<Recipe>();
            foreach (string id in Settings.Get("RecipeIds", "").Split(','))
            {
                if (id.Trim().Length == 0) continue;
                Recipe r = Get(Prefix + id.Trim());
                if (r != null) l.Add(r);
            }
            return l;
        }

        public static Recipe Get(string id)
        {
            if (!IsRecipe(id)) return null;
            string raw = Settings.Get("Recipe." + id.Substring(Prefix.Length), "");
            string[] p = raw.Split('|');
            if (p.Length < 3) return null;
            Recipe r = new Recipe { Id = id, For = p[0], Name = p[1] };
            foreach (string s in p[2].Split(';'))
            {
                if (s.Length == 0) continue;
                int eq = s.IndexOf('=');
                r.Steps.Add(eq < 0 ? new RecipeStep(s, "") : new RecipeStep(s.Substring(0, eq), s.Substring(eq + 1)));
            }
            return r;
        }

        static string Clean(string s) { return (s ?? "").Replace("|", "/").Replace(";", ",").Replace("\n", " ").Trim(); }

        public static void Save(Recipe r)
        {
            if (r.Id == null) r.Id = Prefix + Guid.NewGuid().ToString("N").Substring(0, 8);
            List<string> parts = new List<string>();
            foreach (RecipeStep s in r.Steps) parts.Add(s.Kind + (s.Value.Length > 0 ? "=" + Clean(s.Value) : ""));
            string key = r.Id.Substring(Prefix.Length);
            Settings.Set("Recipe." + key, r.For + "|" + Clean(r.Name) + "|" + string.Join(";", parts.ToArray()));
            List<string> ids = new List<string>(Settings.Get("RecipeIds", "").Split(','));
            ids.RemoveAll(x => x.Trim().Length == 0);
            if (!ids.Contains(key)) { ids.Add(key); Settings.Set("RecipeIds", string.Join(",", ids.ToArray())); }
        }

        public static void Delete(string id)
        {
            if (!IsRecipe(id)) return;
            string key = id.Substring(Prefix.Length);
            List<string> ids = new List<string>(Settings.Get("RecipeIds", "").Split(','));
            ids.RemoveAll(x => x.Trim().Length == 0 || x == key);
            Settings.Set("RecipeIds", string.Join(",", ids.ToArray()));
            Settings.Set("Recipe." + key, "");
            // off the wheel too
            foreach (string k in WheelConfig.Kinds)
            {
                string saved = Settings.Get("WheelAct." + k, "");
                if (Array.IndexOf(saved.Split(','), id) < 0) continue;
                List<string> l = new List<string>(saved.Split(','));
                l.Remove(id);
                Settings.Set("WheelAct." + k, l.Count > 0 ? string.Join(",", l.ToArray()) : "-");
            }
        }

        public static bool IsRecipe(string action) { return action != null && action.StartsWith(Prefix); }

        /// The recipes for this kind of file (they can go on its middle ring).
        public static List<string> IdsFor(string kind)
        {
            List<string> l = new List<string>();
            foreach (Recipe r in All()) if (r.For == kind) l.Add(r.Id);
            return l;
        }

        public static WheelItem Item(string id)
        {
            Recipe r = Get(id);
            string name = r != null ? r.Name : "?";
            return new WheelItem { Action = id, Glyph = Ico.Recipe, Label = () => name };
        }

        // ------------------------------------------------------------------ running one
        /// From the wheel: each file goes through the recipe in the background (shown in Activity); the result is
        /// saved next to it as "photo (Square JPG).jpg".
        public static void Run(string id, string[] files, Control activitySource)
        {
            Recipe r = Get(id);
            if (r == null || files == null || files.Length == 0) return;
            bool cancel = false;
            ActJob act = Activity.Begin(activitySource, r.Name, files.Length == 1 ? Path.GetFileName(files[0]) : L.T("n_files", files.Length), () => cancel = true);
            Thread t = new Thread(() =>
            {
                int ok = 0; string err = null, last = null;
                for (int i = 0; i < files.Length && !cancel; i++)
                {
                    int index = i;
                    try { last = Apply(r, files[i], DragWheel.OutDirFor(files[i]), p => Activity.Update(act, (index + p) / files.Length, (index + 1) + "/" + files.Length), () => cancel); ok++; }
                    catch (Exception ex) { if (!cancel) err = ex.Message; }
                }
                string where = last != null ? Path.GetFileName(Path.GetDirectoryName(last)) : "";
                Activity.End(act, ok == 0 && !cancel ? (err ?? L.T("none_converted")) : null, cancel,
                    ok == files.Length ? L.T("all_done", ok == 1 ? L.T("one_file") : L.T("n_files", ok), where) : L.T("some_failed", ok, files.Length - ok),
                    ok == 1 ? last : last != null ? Path.GetDirectoryName(last) : null);
                if (ok > 0 && Settings.OpenAfter && last != null) ActivityRows.ShowOutput(ok == 1 ? last : Path.GetDirectoryName(last));
            });
            t.IsBackground = true; t.Start();
        }

        /// Runs the steps on one file and saves the result in outDir. Returns the new file.
        public static string Apply(Recipe r, string input, string outDir, Action<double> progress, Func<bool> cancelled)
        {
            if (cancelled == null) cancelled = () => false;
            if (progress == null) progress = p => { };
            string work = Path.Combine(Path.GetTempPath(), "fadi_recipe_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(work);
            try
            {
                string cur = input;
                string origExt = Formats.Ext(input);
                bool converted = false;
                int n = Math.Max(1, r.Steps.Count), k = 0;
                // picture steps that only change the shape are done together (one pass, no quality lost in between)
                List<RecipeStep> shape = new List<RecipeStep>();
                Action flush = () =>
                {
                    if (shape.Count == 0) return;
                    cur = Shape(cur, shape, work);
                    shape.Clear();
                };
                foreach (RecipeStep s in r.Steps)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    if (s.Kind == "crop" || s.Kind == "resize" || s.Kind == "rotate") { shape.Add(s); k++; continue; }
                    flush();
                    if (s.Kind == "nobg")
                    {
                        if (!BgRemover.Ready) throw new UserError(L.T("wh_nobg_get"));
                        using (Image im = ViewerPage.Decode(cur, 4096))
                        using (Bitmap b = new Bitmap(im))
                        using (Bitmap cut = BgRemover.Cutout(b, BgRemover.Mask(b)))
                        {
                            string png = Path.Combine(work, "nobg" + k + ".png");
                            cut.Save(png, ImageFormat.Png);
                            cur = png;
                        }
                    }
                    else if (s.Kind == "convert")
                    {
                        if (Formats.Ext(cur) != s.Value)
                        {
                            string dir = Path.Combine(work, "c" + k); Directory.CreateDirectory(dir);
                            Job j = Engine.Build(cur, s.Value, dir, Quality.Best);
                            string e = Engine.Run(j, p => { if (progress != null) progress((k + p) / n); }, cancelled);
                            if (e != null) throw new UserError(e);
                            cur = j.Output;
                        }
                        converted = true;
                    }
                    else if (s.Kind == "compress")
                    {
                        int pct; int.TryParse(s.Value, out pct); pct = Math.Max(5, Math.Min(100, pct <= 0 ? 50 : pct));
                        long b = new FileInfo(cur).Length;
                        double bytes = b * pct / 100.0;
                        double mb = Formats.CategoryOf(Formats.Ext(cur)) == Category.Image ? bytes / 1e6 : bytes / 1048576.0;
                        string dir = Path.Combine(work, "z" + k); Directory.CreateDirectory(dir);
                        Job j = Compressor.Build(cur, dir, Compressor.Level.Balanced, mb);
                        string e = Engine.Run(j, p => { if (progress != null) progress((k + p) / n); }, cancelled);
                        if (e != null && j.Fallback != null) { try { File.Delete(j.Output); } catch { } j = j.Fallback(); e = Engine.Run(j, p => { }, cancelled); }
                        if (e != null) throw new UserError(e);
                        if (File.Exists(j.Output) && new FileInfo(j.Output).Length < b) cur = j.Output;
                    }
                    k++;
                    if (progress != null) progress(k / (double)n);
                }
                flush();
                // no "convert" step: the result keeps the original's format
                string ext = Formats.Ext(cur);
                if (!converted && ext != origExt && Formats.CategoryOf(origExt) == Category.Image && origExt != "gif")
                {
                    string dir = Path.Combine(work, "back"); Directory.CreateDirectory(dir);
                    try
                    {
                        Job j = Engine.Build(cur, origExt == "jpeg" ? "jpg" : origExt, dir, Quality.Best);
                        if (Engine.Run(j, p => { }, cancelled) == null && File.Exists(j.Output)) cur = j.Output;
                    }
                    catch { }
                    ext = Formats.Ext(cur);
                }
                string dst = Paths.UniquePath(outDir, Path.GetFileNameWithoutExtension(input) + " (" + Clean(r.Name) + ")", ext);
                File.Copy(cur, dst, false);
                return dst;
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        /// Crop (from the middle), resize (longest side) and turn, in one pass. Kept as PNG until the end.
        static string Shape(string cur, List<RecipeStep> steps, string work)
        {
            using (Image im = ViewerPage.Decode(cur, 16000))
            {
                Bitmap b = new Bitmap(im);
                try
                {
                    foreach (RecipeStep s in steps)
                    {
                        Bitmap next = null;
                        if (s.Kind == "crop")
                        {
                            string[] ab = s.Value.Split(':');
                            float ra, rb;
                            if (ab.Length == 2 && float.TryParse(ab[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ra) && float.TryParse(ab[1], NumberStyles.Float, CultureInfo.InvariantCulture, out rb) && ra > 0 && rb > 0)
                            {
                                float r = ra / rb;
                                int w = b.Width, h = (int)Math.Round(w / r);
                                if (h > b.Height) { h = b.Height; w = (int)Math.Round(h * r); }
                                Rectangle c = new Rectangle((b.Width - w) / 2, (b.Height - h) / 2, w, h);
                                next = b.Clone(c, PixelFormat.Format32bppArgb);
                            }
                        }
                        else if (s.Kind == "resize")
                        {
                            int side; int.TryParse(s.Value, out side);
                            int longest = Math.Max(b.Width, b.Height);
                            if (side > 0 && side != longest)
                            {
                                double k = side / (double)longest;
                                int w = Math.Max(1, (int)Math.Round(b.Width * k)), h = Math.Max(1, (int)Math.Round(b.Height * k));
                                next = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                                using (Graphics g = Graphics.FromImage(next)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(b, 0, 0, w, h); }
                            }
                        }
                        else if (s.Kind == "rotate")
                        {
                            b.RotateFlip(s.Value == "180" ? RotateFlipType.Rotate180FlipNone : s.Value == "270" ? RotateFlipType.Rotate270FlipNone : RotateFlipType.Rotate90FlipNone);
                        }
                        if (next != null) { b.Dispose(); b = next; }
                    }
                    string png = Path.Combine(work, "shape" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".png");
                    b.Save(png, ImageFormat.Png);
                    return png;
                }
                finally { b.Dispose(); }
            }
        }
    }
}
