using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;

namespace FileConverter
{
    /// Free Arabic fonts (Google Fonts, OFL) downloaded on demand, plus any .ttf/.otf the user drops in the folder.
    /// They're loaded privately, so nothing gets installed into Windows.
    public static class FontPack
    {
        const string Base = "https://raw.githubusercontent.com/google/fonts/main/ofl/";
        static readonly string[] Files = {
            "amiri/Amiri-Regular.ttf", "amiri/Amiri-Bold.ttf", "arefruqaa/ArefRuqaa-Regular.ttf", "arefruqaa/ArefRuqaa-Bold.ttf",
            "arefruqaaink/ArefRuqaaInk-Regular.ttf", "reemkufi/ReemKufi%5Bwght%5D.ttf", "reemkufifun/ReemKufiFun%5Bwght%5D.ttf",
            "lateef/Lateef-Regular.ttf", "scheherazadenew/ScheherazadeNew-Regular.ttf", "cairo/Cairo%5Bslnt,wght%5D.ttf",
            "tajawal/Tajawal-Regular.ttf", "tajawal/Tajawal-Bold.ttf", "elmessiri/ElMessiri%5Bwght%5D.ttf", "lalezar/Lalezar-Regular.ttf",
            "rakkas/Rakkas-Regular.ttf", "katibeh/Katibeh-Regular.ttf", "marhey/Marhey%5Bwght%5D.ttf", "mirza/Mirza-Regular.ttf",
            "jomhuria/Jomhuria-Regular.ttf", "blaka/Blaka-Regular.ttf", "alkalami/Alkalami-Regular.ttf", "gulzar/Gulzar-Regular.ttf",
            "kufam/Kufam%5Bwght%5D.ttf", "notonaskharabic/NotoNaskhArabic%5Bwght%5D.ttf" };

        public static string Dir { get { return Path.Combine(Paths.ToolsDir, "fonts"); } }
        static PrivateFontCollection pfc;
        static readonly Dictionary<string, FontFamily> byName = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<string> Families = new List<string>();
        public static bool Ready { get { return Families.Count >= 6; } }

        public static void Load()
        {
            try
            {
                if (!Directory.Exists(Dir)) return;
                if (pfc == null) pfc = new PrivateFontCollection();
                foreach (string f in Directory.GetFiles(Dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if ((ext != ".ttf" && ext != ".otf") || loaded.Contains(f)) continue;
                    try { pfc.AddFontFile(f); loaded.Add(f); } catch { }
                }
                foreach (FontFamily fam in pfc.Families)
                    if (!byName.ContainsKey(fam.Name)) { byName[fam.Name] = fam; Families.Add(fam.Name); }
                Families.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
        }

        /// After the fonts were uninstalled: they stop being offered (the ones already drawn stay valid until the app closes).
        public static void Forget()
        {
            bool gone = true;
            try { if (Directory.Exists(Dir) && Directory.GetFiles(Dir, "*.ttf").Length > 0) gone = false; } catch { }
            if (!gone) return;
            Families.Clear(); byName.Clear(); loaded.Clear();
        }

        static bool getting;
        /// Downloads the Arabic fonts in the background (the font menus' "Get free Arabic fonts").
        /// say(message, ok) reports; done() runs on the screen's thread when it's over.
        public static void Get(System.Windows.Forms.Control owner, Action<string, bool> say, Action done)
        {
            if (getting) return;
            getting = true;
            say(L.T("fonts_downloading"), true);
            System.Threading.Thread t = new System.Threading.Thread(() =>
            {
                string err = null;
                try { Download(null, () => owner.IsDisposed || Tools.ShuttingDown); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    owner.BeginInvoke((Action)(() =>
                    {
                        getting = false;
                        if (Ready) say(L.T("fonts_ready", Families.Count), true);
                        else say(L.T("fonts_failed") + (err != null ? " " + err : ""), false);
                        if (done != null) done();
                    }));
                }
                catch { getting = false; }
            }) { IsBackground = true };
            t.Start();
        }

        public static FontFamily Find(string name)
        {
            FontFamily f;
            return name != null && byName.TryGetValue(name, out f) ? f : null;
        }

        public static void Download(Action<int, int> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(Dir);
            for (int i = 0; i < Files.Length; i++)
            {
                if (cancelled != null && cancelled()) return;
                string name = Uri.UnescapeDataString(Path.GetFileName(Files[i])).Replace("[", "_").Replace("]", "_").Replace(",", "_");
                string dest = Path.Combine(Dir, name);
                if (!File.Exists(dest))
                {
                    try { Net.Download(Base + Files[i], dest + ".part", null, cancelled); File.Move(dest + ".part", dest); }
                    catch { try { File.Delete(dest + ".part"); } catch { } }
                }
                if (progress != null) progress(i + 1, Files.Length);
            }
            Load();
        }
    }
}
