using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Two ways to run the app without anyone at the keyboard:
    ///   "/selftest folder"    opens every page in light, dark and Arabic, saves a picture of each, runs a few checks
    ///                         and exits with 0 when everything went well (the build check on GitHub runs this);
    ///   "/screenshots folder" fills the pages with made-up example files and saves pictures of each page for the
    ///                         project page (Ctrl+Alt+Shift+S in the app starts it).
    /// Both keep the person's settings untouched: they start from fresh settings that are never saved.
    public static class SelfTest
    {
        /// True while the app runs as a self-test or screenshot run (no tour, no update check, nothing saved).
        public static bool Active;
        /// The screenshot run keeps 100% size, so pictures come out the same size on every screen.
        public static bool FixedScale;

        static readonly List<string> problems = new List<string>();
        static StreamWriter log;
        static string outDir;

        static void Log(string s)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s;
            try { if (log != null) log.WriteLine(line); } catch { }
            try { Console.WriteLine(line); } catch { }
        }
        static void Fail(string what, Exception ex)
        {
            string s = what + (ex != null ? ": " + ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace : "");
            lock (problems) problems.Add(s);
            Log("PROBLEM " + s);
        }
        static void Check(string name, Func<bool> test)
        {
            try { if (test()) Log("ok      " + name); else Fail(name, null); }
            catch (Exception ex) { Fail(name, ex); }
        }

        // ------------------------------------------------------------------ the two runs
        public static int Run(string folder, bool screenshots)
        {
            Active = true;
            FixedScale = screenshots;
            Settings.StartFresh();
            Settings.Set("TourDone", TourForm.Version);
            Settings.Set("SideCollapsed", "0");
            Settings.Set("Accent", "");
            if (screenshots) { Settings.Set("RenameMode", "4"); Theme.SetDpi(96); }
            outDir = folder;
            Directory.CreateDirectory(outDir);
            log = new StreamWriter(Path.Combine(outDir, screenshots ? "screenshots.log" : "selftest.log"), false, new UTF8Encoding(false)) { AutoFlush = true };
            Log("Fadi Tools " + Shell.Version + (screenshots ? " screenshots" : " self-test") + " · " + Environment.OSVersion + " · scale " + Theme.Scale);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Fail("error on the UI thread", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { Fail("error", e.ExceptionObject as Exception); Finish(3); };
            // a run that hangs still ends (and says so)
            new Thread(() => { Thread.Sleep(screenshots ? 300000 : 240000); Fail("timed out", null); Finish(4); }) { IsBackground = true }.Start();

            if (!screenshots) QuickChecks();
            MainForm mf = null;
            try
            {
                Look("light", "en");
                mf = new MainForm(new string[0]);
                mf.StartPosition = FormStartPosition.Manual;
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                mf.Location = new Point(wa.X + 10, wa.Y + 10);
                Size client = new Size(Theme.S(1220), Theme.S(780));
                Driver d = new Driver(mf);
                mf.Shown += (s, e) => { mf.ClientSize = client; d.Start(screenshots ? ShotSteps(mf, d) : TestSteps(mf, d)); };
                Application.Run(mf);
            }
            catch (Exception ex) { Fail("the window", ex); }
            return Finish(problems.Count == 0 ? 0 : 1);
        }

        static int finished = -1;
        static int Finish(int code)
        {
            if (Interlocked.CompareExchange(ref finished, code, -1) != -1) return finished;
            Log(problems.Count == 0 ? "PASSED" : "FAILED: " + problems.Count + " problem(s)");
            foreach (string p in problems) Log("  - " + p.Split('\r')[0]);
            try { log.Flush(); } catch { }
            if (code >= 3) Environment.Exit(code);   // from a stuck or broken run: stop right here
            return code;
        }

        // ------------------------------------------------------------------ checks that need no window
        static void QuickChecks()
        {
            Check("translations: every text in English and Arabic", () =>
            {
                List<string> bad = new List<string>();
                foreach (KeyValuePair<string, string[]> kv in L.All)
                {
                    string[] v = kv.Value;
                    if (v == null || v.Length != 2 || v[0].Trim().Length == 0 && v[1].Trim().Length > 0 || v[1].Trim().Length == 0 && v[0].Trim().Length > 0) { bad.Add(kv.Key); continue; }
                    for (int i = 0; i < 6; i++)
                        if (v[0].Contains("{" + i + "}") != v[1].Contains("{" + i + "}")) { bad.Add(kv.Key + " {" + i + "}"); break; }
                }
                if (bad.Count > 0) Log("        " + string.Join(", ", bad.ToArray()));
                return bad.Count == 0;
            });
            Check("copy text: a ] read as 1 is fixed", () => Ocr.FixBrackets("[Total Mark for this Assignment is 81") == "[Total Mark for this Assignment is 8]");
            Check("copy text: real numbers are left alone", () => Ocr.FixBrackets("[Note 1") == "[Note 1" && Ocr.FixBrackets("(see page 41") == "(see page 41");
            Check("searchable PDF: the hidden text layer is written", () =>
            {
                string f = Path.Combine(outDir, "text-layer.pdf");
                PdfText.PageText p = new PdfText.PageText { ImgW = 1000, ImgH = 1400 };
                p.Lines.Add(new List<Ocr.Word> { new Ocr.Word { X = 100, Y = 100, W = 120, H = 30, Text = "[Total" }, new Ocr.Word { X = 230, Y = 100, W = 60, H = 30, Text = "8]" } });
                PdfText.WriteLayer(f, new[] { p });
                string head = Encoding.ASCII.GetString(File.ReadAllBytes(f));
                return head.StartsWith("%PDF") && head.Contains("/ToUnicode") && head.Contains("/GlyphLessFont") && head.TrimEnd().EndsWith("%%EOF");
            });
            Check("formats: every format has a kind", () =>
            {
                foreach (string e in new[] { "jpg", "png", "webp", "mp4", "mkv", "mp3", "wav", "pdf", "docx", "csv" })
                    if (Formats.CategoryOf(e) == Category.Unknown) { Log("        " + e); return false; }
                return true;
            });
        }

        // ------------------------------------------------------------------ the window, page by page
        static void Look(string theme, string lang)
        {
            Settings.Set("Theme", theme); Settings.Set("Language", lang);
            Theme.ApplySettings();
        }
        static void LookNow(string theme, string lang) { Look(theme, lang); Theme.RaiseChanged(); }

        static string PageName(MainForm mf, int i)
        {
            Page p = mf.PageAt(i);
            string n = p.GetType().Name.Replace("Page", "").Replace("Host", "");
            return n.Length == 0 ? "page" : n.ToLowerInvariant();
        }

        static List<Step> TestSteps(MainForm mf, Driver d)
        {
            List<Step> s = new List<Step>();
            string[][] looks = { new[] { "light", "en" }, new[] { "dark", "en" }, new[] { "light", "ar" } };
            foreach (string[] look in looks)
            {
                string[] lk = look;
                s.Add(new Step(200, () => LookNow(lk[0], lk[1])));
                for (int i = 0; i < mf.PageCount; i++)
                {
                    int k = i;
                    s.Add(new Step(150, () => mf.Go(k)));
                    s.Add(new Step(450, () => Save(mf, string.Format("{0}-{1}-{2:00}-{3}.png", lk[0], lk[1], k, PageName(mf, k)), true)));
                }
            }
            // every page at the smallest and a large window size, and folded side list
            foreach (Size sz in new[] { new Size(1000, 600), new Size(1600, 1000) })
            {
                Size z = sz;
                s.Add(new Step(100, () => mf.ClientSize = new Size(Theme.S(z.Width), Theme.S(z.Height))));
                for (int i = 0; i < mf.PageCount; i++) { int k = i; s.Add(new Step(120, () => mf.Go(k))); }
                s.Add(new Step(150, () => Log("ok      every page laid out at " + z.Width + "×" + z.Height)));
            }
            s.Add(new Step(100, () => LookNow("light", "en")));
            s.Add(new Step(300, () => { mf.ClientSize = new Size(Theme.S(1220), Theme.S(780)); mf.Go(0); }));
            s.Add(new Step(400, () => Save(mf, "final.png", true)));
            return s;
        }

        static List<Step> ShotSteps(MainForm mf, Driver d)
        {
            List<Step> s = new List<Step>();
            Demo demo = null;
            // the pointer out of the way (no row lit up under it)
            s.Add(new Step(50, () => { try { Rectangle wa = Screen.FromControl(mf).WorkingArea; Cursor.Position = new Point(wa.Right - 4, wa.Bottom - 4); } catch { } }));
            s.Add(new Step(100, () => { demo = Demo.Make(Path.Combine(outDir, "Example files")); Log("example files ready"); }));
            s.Add(new Step(100, () =>
            {
                mf.PageAt(0).AcceptFiles(demo.ForConvert);
                mf.PageAt(2).AcceptFiles(demo.ForCompress);
                mf.PageAt(3).AcceptFiles(demo.Pictures);
                mf.Go(4); mf.PageAt(4).AcceptFiles(new[] { demo.Inspect });
                mf.Go(1); mf.PageAt(1).AcceptFiles(new[] { demo.Pictures[0] });
                mf.Go(5); mf.PageAt(5).AcceptFiles(new[] { demo.DupesFolder });
                ((DuplicatesPage)mf.PageAt(5)).Scan();
            }));
            s.Add(new Step(300, null, () => !((DuplicatesPage)mf.PageAt(5)).Scanning, 60000));
            s.Add(new Step(1500, () => Log("pages filled")));
            string[][] looks = { new[] { "light", "en" }, new[] { "dark", "en" }, new[] { "light", "ar" }, new[] { "dark", "ar" } };
            foreach (string[] look in looks)
            {
                string[] lk = look;
                s.Add(new Step(300, () => LookNow(lk[0], lk[1])));
                for (int i = 0; i < mf.PageCount; i++)
                {
                    int k = i;
                    s.Add(new Step(200, () => mf.Go(k)));
                    s.Add(new Step(900, () => Save(mf, string.Format("{0}-{1}-{2}.png", PageName(mf, k), lk[0], lk[1]), false)));
                }
            }
            s.Add(new Step(200, () => { try { Process.Start("explorer.exe", Tools.Quote(outDir)); } catch { } }));
            return s;
        }

        // ------------------------------------------------------------------ pictures of the window
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        static void Save(Form f, string name, bool mustShowSomething)
        {
            using (Bitmap b = Capture(f))
            {
                b.Save(Path.Combine(outDir, name), ImageFormat.Png);
                if (mustShowSomething && Blank(b)) Fail(name + " is empty", null); else Log("saved   " + name);
            }
        }

        /// The inside of the window as it looks on the screen (or, where Windows can't do that, drawn piece by piece).
        public static Bitmap Capture(Form f)
        {
            Size cs = f.ClientSize;
            Bitmap b = new Bitmap(Math.Max(1, cs.Width), Math.Max(1, cs.Height), PixelFormat.Format32bppArgb);
            bool ok = false;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                try
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        IntPtr dc = g.GetHdc();
                        try { ok = PrintWindow(f.Handle, dc, 1 | 2); }   // client area, as the graphics card shows it
                        finally { g.ReleaseHdc(dc); }
                    }
                    if (ok && Blank(b)) ok = false;
                }
                catch { ok = false; }
            }
            if (!ok)
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(f.BackColor);
                    for (int i = f.Controls.Count - 1; i >= 0; i--) DrawTree(g, f.Controls[i], f.Controls[i].Location, new Rectangle(Point.Empty, cs));
                }
            // fully opaque (PrintWindow leaves the alpha empty in places)
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int[] px = new int[bd.Stride / 4 * b.Height];
            Marshal.Copy(bd.Scan0, px, 0, px.Length);
            for (int i = 0; i < px.Length; i++) px[i] |= unchecked((int)0xFF000000);
            Marshal.Copy(px, 0, bd.Scan0, px.Length);
            b.UnlockBits(bd);
            return b;
        }

        /// A piece of the window and everything inside it, back to front.
        static void DrawTree(Graphics g, Control c, Point at, Rectangle clip)
        {
            if (!c.Visible || c.Width <= 0 || c.Height <= 0) return;
            Rectangle mine = Rectangle.Intersect(clip, new Rectangle(at, c.Size));
            if (mine.Width <= 0 || mine.Height <= 0) return;
            using (Bitmap cb = new Bitmap(c.Width, c.Height))
            {
                try { c.DrawToBitmap(cb, new Rectangle(0, 0, c.Width, c.Height)); } catch { return; }
                g.SetClip(mine);
                g.DrawImageUnscaled(cb, at.X, at.Y);
                g.ResetClip();
            }
            for (int i = c.Controls.Count - 1; i >= 0; i--)
            {
                Control k = c.Controls[i];
                DrawTree(g, k, new Point(at.X + k.Left, at.Y + k.Top), mine);
            }
        }

        /// One colour everywhere (nothing was drawn).
        static bool Blank(Bitmap b)
        {
            int first = b.GetPixel(0, 0).ToArgb() & 0xFFFFFF;
            for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 40))
                for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 40))
                    if ((b.GetPixel(x, y).ToArgb() & 0xFFFFFF) != first) return false;
            return true;
        }

        // ------------------------------------------------------------------ running the steps one after another
        class Step
        {
            public readonly int Wait; public readonly Action Do; public readonly Func<bool> Until; public readonly int MaxMs;
            public Step(int wait, Action a) { Wait = wait; Do = a; }
            public Step(int wait, Action a, Func<bool> until, int maxMs) { Wait = wait; Do = a; Until = until; MaxMs = maxMs; }
        }

        class Driver
        {
            readonly MainForm mf;
            readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
            List<Step> steps; int at; DateTime since; bool ran;
            public Driver(MainForm f) { mf = f; timer.Tick += Tick; }
            public void Start(List<Step> s) { steps = s; at = 0; since = DateTime.Now; ran = false; timer.Start(); }
            void Tick(object sender, EventArgs e)
            {
                if (at >= steps.Count) { timer.Stop(); mf.CloseForTest(); return; }
                Step st = steps[at];
                double ms = (DateTime.Now - since).TotalMilliseconds;
                if (ms < st.Wait) return;
                if (!ran)
                {
                    ran = true;
                    try { if (st.Do != null) st.Do(); } catch (Exception ex) { Fail("step " + (at + 1), ex); }
                }
                if (st.Until != null)
                {
                    bool done;
                    try { done = st.Until(); } catch (Exception ex) { Fail("step " + (at + 1), ex); done = true; }
                    if (!done && ms < st.Wait + st.MaxMs) return;
                    if (!done) Fail("step " + (at + 1) + " took too long", null);
                }
                at++; since = DateTime.Now; ran = false;
            }
        }

        // ------------------------------------------------------------------ made-up example files
        class Demo
        {
            public string[] Pictures, ForConvert, ForCompress;
            public string Inspect, DupesFolder;

            public static Demo Make(string dir)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                Directory.CreateDirectory(dir);
                Demo d = new Demo();
                DateTime day = new DateTime(DateTime.Now.Year, 7, 14, 18, 32, 5);
                string[] names = { "Beach sunset.jpg", "Mountain trip.jpg", "Forest walk.jpg", "City lights.jpg", "Desert road.jpg" };
                List<string> pics = new List<string>();
                for (int i = 0; i < names.Length; i++)
                {
                    string p = Path.Combine(dir, names[i]);
                    using (Bitmap b = Paint(i, 1600, 1067)) SaveJpeg(b, p, day.AddDays(i / 2).AddMinutes(i * 37));
                    pics.Add(p);
                }
                d.Pictures = pics.ToArray();
                List<string> conv = new List<string> { pics[0], pics[1], pics[3] };
                List<string> comp = new List<string> { pics[1], pics[2] };
                d.Inspect = pics[0];
                string ff = Tools.FFmpeg;
                if (ff != null)
                {
                    string clip = Path.Combine(dir, "Holiday clip.mp4");
                    string args = "-y -loop 1 -t 4 -i " + Tools.Quote(pics[0]) + " -loop 1 -t 4 -i " + Tools.Quote(pics[1]) + " -loop 1 -t 4 -i " + Tools.Quote(pics[3]) +
                                  " -f lavfi -t 12 -i \"sine=frequency=330:sample_rate=44100\" -filter_complex \"[0][1][2]concat=n=3:v=1:a=0,scale=1280:-2,format=yuv420p[v]\" -map \"[v]\" -map 3:a -r 30 -c:v libx264 -crf 22 -c:a aac -b:a 128k -shortest " + Tools.Quote(clip);
                    string o;
                    if (Tools.RunHidden(ff, args, 60000, out o) == 0 && File.Exists(clip)) { conv.Add(clip); comp.Insert(0, clip); d.Inspect = clip; }
                    string song = Path.Combine(dir, "Morning song.mp3");
                    if (Tools.RunHidden(ff, "-y -f lavfi -t 20 -i \"sine=frequency=523:sample_rate=44100\" -metadata artist=\"The Example Band\" -metadata title=\"Morning song\" -metadata track=1 -c:a libmp3lame -b:a 192k " + Tools.Quote(song), 60000, out o) == 0 && File.Exists(song))
                        conv.Add(song);
                }
                d.ForConvert = conv.ToArray(); d.ForCompress = comp.ToArray();
                // a folder with the same photos saved twice in different places
                d.DupesFolder = Path.Combine(dir, "Phone backup");
                Directory.CreateDirectory(Path.Combine(d.DupesFolder, "Camera"));
                Directory.CreateDirectory(Path.Combine(d.DupesFolder, "WhatsApp Images"));
                File.Copy(pics[0], Path.Combine(d.DupesFolder, "Camera", "IMG_2041.jpg"));
                File.Copy(pics[0], Path.Combine(d.DupesFolder, "WhatsApp Images", "IMG-20260714-WA0007.jpg"));
                File.Copy(pics[0], Path.Combine(d.DupesFolder, "Beach sunset (1).jpg"));
                File.Copy(pics[1], Path.Combine(d.DupesFolder, "Camera", "IMG_2057.jpg"));
                File.Copy(pics[1], Path.Combine(d.DupesFolder, "Mountain trip copy.jpg"));
                File.Copy(pics[2], Path.Combine(d.DupesFolder, "Camera", "IMG_2063.jpg"));
                File.Copy(pics[4], Path.Combine(d.DupesFolder, "Camera", "IMG_2071.jpg"));
                return d;
            }

            static Bitmap Paint(int kind, int w, int h)
            {
                Bitmap b = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Color[][] skies = {
                        new[] { Color.FromArgb(255, 154, 90), Color.FromArgb(106, 76, 147) },
                        new[] { Color.FromArgb(142, 197, 252), Color.FromArgb(224, 195, 252) },
                        new[] { Color.FromArgb(168, 224, 99), Color.FromArgb(86, 171, 47) },
                        new[] { Color.FromArgb(15, 32, 39), Color.FromArgb(44, 83, 100) },
                        new[] { Color.FromArgb(253, 200, 48), Color.FromArgb(243, 115, 53) } };
                    using (LinearGradientBrush sky = new LinearGradientBrush(new Rectangle(0, 0, w, h), skies[kind][0], skies[kind][1], 90f)) g.FillRectangle(sky, 0, 0, w, h);
                    Random r = new Random(kind * 7 + 3);
                    if (kind == 0)
                    {
                        using (SolidBrush sun = new SolidBrush(Color.FromArgb(255, 222, 140))) g.FillEllipse(sun, w / 2 - 150, h / 2 - 190, 300, 300);
                        using (LinearGradientBrush sea = new LinearGradientBrush(new Rectangle(0, h * 6 / 10, w, h * 4 / 10 + 1), Color.FromArgb(70, 80, 140), Color.FromArgb(25, 40, 80), 90f)) g.FillRectangle(sea, 0, h * 6 / 10, w, h * 4 / 10);
                        for (int i = 0; i < 14; i++) using (SolidBrush gl = new SolidBrush(Color.FromArgb(120, 255, 210, 140))) g.FillRectangle(gl, w / 2 - 120 + r.Next(240) - i * 3, h * 6 / 10 + 20 + i * 26, 60 + r.Next(140), 6);
                    }
                    else if (kind == 1 || kind == 4)
                    {
                        Color far = kind == 1 ? Color.FromArgb(120, 140, 175) : Color.FromArgb(214, 140, 80), near = kind == 1 ? Color.FromArgb(60, 80, 105) : Color.FromArgb(170, 95, 55);
                        using (SolidBrush m1 = new SolidBrush(far)) g.FillPolygon(m1, new[] { new Point(0, h * 7 / 10), new Point(w / 4, h / 3), new Point(w / 2, h * 6 / 10), new Point(w * 3 / 4, h / 4), new Point(w, h * 6 / 10), new Point(w, h), new Point(0, h) });
                        using (SolidBrush m2 = new SolidBrush(near)) g.FillPolygon(m2, new[] { new Point(0, h * 8 / 10), new Point(w / 3, h / 2), new Point(w * 2 / 3, h * 8 / 10), new Point(w, h * 6 / 10), new Point(w, h), new Point(0, h) });
                        if (kind == 1) using (SolidBrush snow = new SolidBrush(Color.FromArgb(240, 245, 255))) { g.FillPolygon(snow, new[] { new Point(w * 3 / 4, h / 4), new Point(w * 3 / 4 - 70, h / 4 + 80), new Point(w * 3 / 4 + 75, h / 4 + 85) }); g.FillPolygon(snow, new[] { new Point(w / 4, h / 3), new Point(w / 4 - 60, h / 3 + 70), new Point(w / 4 + 60, h / 3 + 72) }); }
                        else using (SolidBrush road = new SolidBrush(Color.FromArgb(70, 60, 60))) g.FillPolygon(road, new[] { new Point(w / 2 - 20, h * 7 / 10), new Point(w / 2 + 20, h * 7 / 10), new Point(w / 2 + 380, h), new Point(w / 2 - 380, h) });
                    }
                    else if (kind == 2)
                    {
                        for (int i = 0; i < 26; i++)
                        {
                            int x = r.Next(-50, w), th = 300 + r.Next(450), tw = th / 3;
                            Color c = Color.FromArgb(20 + r.Next(40), 80 + r.Next(60), 30 + r.Next(30));
                            using (SolidBrush t = new SolidBrush(c)) g.FillPolygon(t, new[] { new Point(x, h - th), new Point(x - tw, h), new Point(x + tw, h) });
                        }
                    }
                    else
                    {
                        for (int i = 0; i < 18; i++)
                        {
                            int bw = 60 + r.Next(80), bh = 200 + r.Next(500), x = i * (w / 18) + r.Next(20);
                            using (SolidBrush t = new SolidBrush(Color.FromArgb(25 + r.Next(25), 35 + r.Next(25), 50 + r.Next(30)))) g.FillRectangle(t, x, h - bh, bw, bh);
                            for (int y = h - bh + 16; y < h - 20; y += 26)
                                for (int xx = x + 10; xx < x + bw - 14; xx += 20)
                                    if (r.Next(3) == 0) using (SolidBrush lt = new SolidBrush(Color.FromArgb(255, 210 + r.Next(40), 120 + r.Next(60)))) g.FillRectangle(lt, xx, y, 9, 13);
                        }
                    }
                }
                return b;
            }

            /// Saves a JPEG with the moment it was "taken" inside (so Rename by date has something to show).
            static void SaveJpeg(Bitmap b, string path, DateTime taken)
            {
                try
                {
                    PropertyItem pi = (PropertyItem)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PropertyItem));
                    byte[] v = Encoding.ASCII.GetBytes(taken.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "\0");
                    pi.Id = 0x9003; pi.Type = 2; pi.Len = v.Length; pi.Value = v;
                    b.SetPropertyItem(pi);
                }
                catch { }
                ImageCodecInfo jpg = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.MimeType == "image/jpeg");
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                    b.Save(path, jpg, ep);
                }
                try { File.SetLastWriteTime(path, taken); } catch { }
            }
        }
    }
}
