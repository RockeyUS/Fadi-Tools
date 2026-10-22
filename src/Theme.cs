using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FileConverter
{
    public enum Surface { Background, Card, Sidebar, Field }
    public enum Align { Start, Center, End }

    /// Colors, fonts, metrics and drawing helpers. Supports light/dark and right-to-left.
    public static class Theme
    {
        public static bool Dark;
        public static bool RTL;
        public static event Action Changed;
        /// Raised just before the look (theme, language, accent) changes — the window takes a picture to fade from.
        public static event Action Changing;
        public static void RaiseChanged() { if (Changed != null) Changed(); }

        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        static Color P(bool dark, Color light, Color d) { return dark ? d : light; }

        // ------------------------------------------------------------ custom accent color (Settings > Appearance)
        public static Color Accent = Color.Empty;   // empty = the default Claude-like clay look
        /// Blends a neutral surface toward a custom accent so the whole window takes on its color.
        static Color T(Color c, float light, float dark) { return Accent.IsEmpty ? c : Mix(c, Accent, Dark ? dark : light); }
        static float Lum(Color c) { return (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f; }

        /// The default accent: a warm orange (it sits well with the fez red of the logo).
        public static Color Clay { get { return P(Dark, C(238, 98, 46), C(245, 120, 70)); } }
        public static Color AccentColor { get { return Accent.IsEmpty ? Clay : Accent; } }

        // clean, flat surfaces: warm cream with white cards (light), soft black with charcoal cards (dark)
        public static Color Bg { get { return T(P(Dark, C(246, 240, 233), C(22, 22, 24)), 0.03f, 0.05f); } }
        public static Color BgTop { get { return Bg; } }
        public static Color BgBottom { get { return Bg; } }
        public static Color Side { get { return T(P(Dark, C(255, 255, 255), C(29, 29, 31)), 0.0f, 0.04f); } }
        public static Color SideTop { get { return Side; } }
        public static Color SideBottom { get { return Side; } }
        public static Color SideBorder { get { return T(P(Dark, C(234, 227, 218), C(44, 44, 48)), 0.06f, 0.06f); } }
        public static Color Card { get { return T(P(Dark, C(255, 255, 255), C(32, 32, 35)), 0.0f, 0.03f); } }
        public static Color CardBorder { get { return T(P(Dark, C(234, 227, 218), C(48, 48, 52)), 0.06f, 0.06f); } }
        public static Color CardInner { get { return T(P(Dark, C(249, 245, 240), C(27, 27, 30)), 0.03f, 0.04f); } }
        public static Color Ink { get { return P(Dark, C(24, 22, 20), C(242, 242, 244)); } }
        public static Color InkSoft { get { return P(Dark, C(86, 82, 76), C(176, 176, 183)); } }
        public static Color InkFaint { get { return P(Dark, C(136, 130, 122), C(124, 124, 132)); } }
        public static Color Line { get { return P(Dark, C(238, 232, 224), C(46, 46, 50)); } }
        public static Color Hover { get { return T(P(Dark, C(245, 240, 234), C(42, 42, 46)), 0.04f, 0.06f); } }
        public static Color Pressed { get { return T(P(Dark, C(236, 229, 220), C(52, 52, 57)), 0.06f, 0.08f); } }
        public static Color Selected { get { return T(P(Dark, C(241, 234, 226), C(48, 48, 53)), 0.08f, 0.1f); } }
        public static Color Field { get { return T(P(Dark, C(255, 255, 255), C(36, 36, 39)), 0.0f, 0.03f); } }
        public static Color FieldBorder { get { return T(P(Dark, C(222, 214, 204), C(64, 64, 70)), 0.06f, 0.06f); } }
        public static Color ChipTop { get { return T(P(Dark, C(255, 255, 255), C(38, 38, 41)), 0.0f, 0.03f); } }
        public static Color ChipBottom { get { return ChipTop; } }
        public static Color ChipBorder { get { return T(P(Dark, C(228, 220, 210), C(58, 58, 63)), 0.06f, 0.06f); } }
        // primary actions: flat accent with white text (Claude's clay by default)
        public static Color PrimaryTop { get { return AccentColor; } }
        public static Color PrimaryBottom { get { return AccentColor; } }
        public static Color PrimaryInk { get { return Lum(AccentColor) > 0.68f ? C(24, 24, 22) : Color.White; } }
        public static Color AccentSoft { get { return Mix(Card, AccentColor, Dark ? 0.22f : 0.12f); } }
        public static Color Success { get { return P(Dark, C(46, 125, 72), C(110, 196, 136)); } }
        public static Color SuccessBg { get { return P(Dark, C(228, 242, 231), C(38, 60, 44)); } }
        public static Color Error { get { return P(Dark, C(184, 58, 44), C(240, 130, 112)); } }
        public static Color ErrorBg { get { return P(Dark, C(250, 233, 228), C(72, 40, 34)); } }
        public static Color Warn { get { return P(Dark, C(158, 106, 18), C(236, 188, 102)); } }

        /// A text colour that follows light / dark: remember one of the theme's own colours (Ink, InkSoft, Success...)
        /// and it is shown in the matching colour of whichever look is on when it's drawn.
        public struct LiveColor
        {
            int tone; Color raw;
            public static implicit operator LiveColor(Color c)
            {
                int a = c.ToArgb();
                int t = a == Ink.ToArgb() ? 1 : a == InkSoft.ToArgb() ? 2 : a == InkFaint.ToArgb() ? 3 : a == Success.ToArgb() ? 4 : a == Error.ToArgb() ? 5 : a == Warn.ToArgb() ? 6 : 0;
                return new LiveColor { tone = t, raw = c };
            }
            public static implicit operator Color(LiveColor l)
            {
                switch (l.tone)
                {
                    case 1: return Ink; case 2: return InkSoft; case 3: return InkFaint;
                    case 4: return Success; case 5: return Error; case 6: return Warn;
                    default: return l.raw;
                }
            }
        }
        public static Color WarnBg { get { return P(Dark, C(250, 241, 222), C(66, 55, 32)); } }
        public static Color Track { get { return T(P(Dark, C(230, 223, 214), C(58, 58, 63)), 0.08f, 0.1f); } }
        public static Color Shadow { get { return P(Dark, Color.FromArgb(22, 60, 50, 30), Color.FromArgb(30, 0, 0, 0)); } }

        public static float Scale = 1f;
        public static int S(float v) { return (int)Math.Round(v * Scale); }

        public static string FontName = "Segoe UI";
        public static string IconFontName = "Segoe MDL2 Assets";
        public static bool HasIconFont;

        static Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        /// A size in points at 96 dpi → pixels on this screen. Fonts are made in pixels (not points), so they follow
        /// Scale exactly — also after moving the window to a screen with a different size setting.
        public static float Px(float points) { return points * Scale * 96f / 72f; }
        /// Cached font. size in points.
        public static Font F(float size, FontStyle style = FontStyle.Regular)
        {
            string key = FontName + size + style + "@" + Scale;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(FontName, Px(size), style, GraphicsUnit.Pixel); fonts[key] = f; }
            return f;
        }

        /// The screen's size setting changed (the window moved to another screen): sizes and fonts follow it.
        public static void SetDpi(int dpi)
        {
            float s = Math.Max(1f, dpi / 96f);
            if (Math.Abs(s - Scale) < 0.001f) return;
            Scale = s;
            fonts = new Dictionary<string, Font>();
            // (the old fonts are left for the garbage collector: controls may still be drawing with them this moment)
        }
        static string serifName;
        /// Headings in a book serif, like Claude's titles (Arabic stays in the UI font).
        public static Font Serif(float size, FontStyle style = FontStyle.Regular)
        {
            if (RTL || serifName == null) return F(size, style == FontStyle.Regular ? FontStyle.Bold : style);
            string key = "serif" + size + style + "@" + Scale;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(serifName, Px(size), style, GraphicsUnit.Pixel); fonts[key] = f; }
            return f;
        }
        public static Font Icon(float size)
        {
            string key = "icon" + size + "@" + Scale;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(HasIconFont ? IconFontName : FontName, Px(size), FontStyle.Regular, GraphicsUnit.Pixel); fonts[key] = f; }
            return f;
        }
        public static Font Mono(float size)
        {
            string key = "mono" + size + "@" + Scale;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(monoName, Px(size), FontStyle.Regular, GraphicsUnit.Pixel); fonts[key] = f; }
            return f;
        }
        static string monoName = "Consolas";

        static Image logo, logoSmall;
        static readonly Dictionary<int, Bitmap> logoAt = new Dictionary<int, Bitmap>();
        /// Light or Dark (Settings → App logo). Dark unless Light was chosen.
        public static bool LogoDark { get { return Settings.Get("Logo", "dark") != "light"; } }
        static Image Res(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                if (s != null) { MemoryStream ms = new MemoryStream(); s.CopyTo(ms); return Image.FromStream(ms); }
            return null;
        }
        /// The app logo, big (256 px): the detailed drawing.
        public static Image Logo
        {
            get
            {
                if (logo == null) logo = Res(LogoDark ? "FileConverter.logo_dark.png" : "FileConverter.logo.png");
                return logo;
            }
        }
        /// The bolder drawing made for small sizes (the sidebar, the title bar, the taskbar).
        public static Image LogoSmall
        {
            get
            {
                if (logoSmall == null) logoSmall = Res(LogoDark ? "FileConverter.logo_dark_small.png" : "FileConverter.logo_small.png") ?? Logo;
                return logoSmall;
            }
        }

        /// Draws the logo smoothly at any size: the small drawing up to 72 px, scaled down once in high quality
        /// and kept, so its rounded edges stay soft (no hard clipping).
        public static void DrawLogo(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            Image src = r.Width <= S(72) ? LogoSmall : Logo;
            if (src == null) return;
            int key = r.Width * 2 + (src == logoSmall ? 1 : 0);
            Bitmap b;
            if (!logoAt.TryGetValue(key, out b))
            {
                b = new Bitmap(r.Width, r.Width, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using (Graphics bg = Graphics.FromImage(b))
                {
                    bg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    bg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    bg.CompositingQuality = CompositingQuality.HighQuality;
                    bg.SmoothingMode = SmoothingMode.AntiAlias;
                    using (System.Drawing.Imaging.ImageAttributes ia = new System.Drawing.Imaging.ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);   // no grey seam at the edges when scaling down
                        bg.DrawImage(src, new Rectangle(0, 0, r.Width, r.Width), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                logoAt[key] = b;
            }
            g.DrawImage(b, new Rectangle(r.X, r.Y + (r.Height - r.Width) / 2, r.Width, r.Width));
        }

        public static string CustomLogoPath { get { return Path.Combine(Paths.AppDir, "custom_logo.png"); } }
        /// Call after the logo setting changes: the sidebar, windows and taskbar pick up the new one.
        public static void ResetLogo()
        {
            logo = null; logoSmall = null; appIcon = null;
            foreach (Bitmap b in logoAt.Values) b.Dispose();
            logoAt.Clear();
        }

        static System.Drawing.Icon appIcon;
        /// The window / taskbar icon: the app's own (dark) icon, or the light logo when that was chosen.
        public static System.Drawing.Icon AppIcon
        {
            get
            {
                if (appIcon == null && !LogoDark && LogoSmall != null)
                    try { using (Bitmap b = new Bitmap(64, 64)) { using (Graphics g = Graphics.FromImage(b)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.DrawImage(LogoSmall, 0, 0, 64, 64); } appIcon = System.Drawing.Icon.FromHandle(b.GetHicon()); } } catch { }
                if (appIcon == null) try { appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                return appIcon;
            }
        }

        [DllImport("user32.dll")] static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        static int SystemDpi() { try { return (int)GetDpiForSystem(); } catch { return 0; } }
        /// The size setting of the screen a window is on (0 when Windows can't say: older than Windows 10 1607).
        public static int DpiOf(IntPtr hwnd) { try { return (int)GetDpiForWindow(hwnd); } catch { return 0; } }

        public static void Init()
        {
            int sys = SystemDpi();
            if (sys > 0) Scale = Math.Max(1f, sys / 96f);
            else
                using (Bitmap b = new Bitmap(1, 1))
                using (Graphics g = Graphics.FromImage(b))
                    Scale = Math.Max(1f, g.DpiX / 96f);
            using (InstalledFontCollection fc = new InstalledFontCollection())
            {
                bool segoe = false, mono = false;
                foreach (FontFamily ff in fc.Families)
                {
                    if (ff.Name == "Segoe UI") segoe = true;
                    if (ff.Name == IconFontName) HasIconFont = true;
                    if (ff.Name == "Consolas") mono = true;
                    if (ff.Name == "Georgia" && serifName == null) serifName = "Georgia";
                }
                if (!segoe) FontName = SystemFonts.MessageBoxFont.FontFamily.Name;
                if (!mono) monoName = FontFamily.GenericMonospace.Name;
                if (serifName == null) foreach (FontFamily ff in fc.Families) if (ff.Name == "Cambria" || ff.Name == "DejaVu Serif" || ff.Name == "Liberation Serif") { serifName = ff.Name; break; }
            }
            ApplySettings();
        }

        /// Reads theme + language from settings.
        public static void ApplySettings()
        {
            if (Changing != null) { try { Changing(); } catch { } }
            string t = Settings.Get("Theme", "system");
            Dark = t == "dark" || (t == "system" && SystemPrefersDark());
            RTL = Settings.Get("Language", "en") == "ar";
            string a = Settings.Get("Accent", "");
            Accent = Color.Empty;
            if (a.Length == 7 && a[0] == '#') try { Accent = ColorTranslator.FromHtml(a); } catch { }
        }

        public static bool SystemPrefersDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 0;
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------ window chrome
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        public static void ApplyTitleBar(Form f)
        {
            try
            {
                int v = Dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
                // nudge Windows 10 to repaint the caption
                if (f.Visible) { f.Width += 1; f.Width -= 1; }
            }
            catch { }
        }

        /// Dark scrollbars for native controls (TextBox etc.)
        public static void ApplyNativeTheme(Control c)
        {
            try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        // ------------------------------------------------------------ geometry
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Rectangle Mirror(Rectangle r, int width)
        {
            if (!RTL) return r;
            return new Rectangle(width - r.Right, r.Y, r.Width, r.Height);
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // ------------------------------------------------------------ painting
        /// Paints the window gradient behind a control, continuous with the form.
        public static void PaintBackdrop(Graphics g, Control c) { g.Clear(Bg); }

        public static void PaintSurface(Graphics g, Control c, Surface s)
        {
            switch (s)
            {
                case Surface.Card: g.Clear(Card); break;
                case Surface.Field: g.Clear(Field); break;
                case Surface.Sidebar: g.Clear(Side); break;
                default: g.Clear(Bg); break;
            }
        }

        /// A card: flat fill, hairline border and (in light mode) the faintest shadow.
        public static void PaintCard(Graphics g, Rectangle r)
        {
            Smooth(g);
            float rad = S(14);
            if (!Dark)
                for (int i = 1; i <= 2; i++)
                    using (GraphicsPath sp = Round(new RectangleF(r.X - i + 1, r.Y - i + 1 + S(1), r.Width + 2 * i - 2, r.Height + 2 * i - 2), rad + i))
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(i == 1 ? 10 : 5, 60, 50, 30))) g.FillPath(sb, sp);
            using (GraphicsPath p = Round(r, rad))
            {
                using (SolidBrush b = new SolidBrush(Card)) g.FillPath(b, p);   // solid: controls inside paint the same color
                using (Pen pen = new Pen(CardBorder, 1f)) g.DrawPath(pen, p);
            }
        }

        public static void FillRound(Graphics g, RectangleF r, float radius, Color c)
        {
            using (GraphicsPath p = Round(r, radius)) using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void FillRoundGradient(Graphics g, RectangleF r, float radius, Color top, Color bottom)
        {
            using (GraphicsPath p = Round(r, radius))
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, LinearGradientMode.Vertical))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, RectangleF r, float radius, Color c, float width = 1f)
        {
            using (GraphicsPath p = Round(r, radius)) using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        /// Draws text with logical alignment (Start = left in English, right in Arabic).
        public static void Text(Graphics g, string text, Font f, Color c, Rectangle r, Align h = Align.Start, bool vcenter = true, bool wrap = false, bool forceLtr = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool rtl = RTL && !forceLtr;
            TextFormatFlags fl = TextFormatFlags.NoPrefix;
            if (h == Align.Center) fl |= TextFormatFlags.HorizontalCenter;
            else if ((h == Align.Start) != rtl) fl |= TextFormatFlags.Left;
            else fl |= TextFormatFlags.Right;
            if (rtl) fl |= TextFormatFlags.RightToLeft;
            fl |= vcenter ? TextFormatFlags.VerticalCenter : TextFormatFlags.Top;
            if (rtl && text[0] != '\u200F') text = "\u200F" + text;   // force right-to-left base direction for mixed text
            if (wrap) fl |= TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
            else fl |= TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, text, f, r, c, fl);
        }

        public static int Measure(string text, Font f)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
        }

        /// Draws an icon glyph (Segoe MDL2 Assets) centered in r.
        public static void Glyph(Graphics g, string glyph, float size, Color c, Rectangle r)
        {
            if (string.IsNullOrEmpty(glyph)) return;
            if (VectorIcons.Draw(g, glyph, size, c, r)) return;
            TextRenderer.DrawText(g, glyph, Icon(size), r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        public static string HumanSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            if (bytes >= 1L << 10) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }

        public static string Time(double seconds, bool ms = false)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            string s = t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
            if (ms) s += "." + (t.Milliseconds / 100);
            return s;
        }
    }

    /// Icon glyphs from Segoe MDL2 Assets (built into Windows 10).
    public static class Ico
    {
        public const string Crop = "\uF406", Convert = "", Cut = "", Download = "", Search = "", Photo = "",
            Settings = "", Sun = "", Moon = "", Globe = "", Add = "", Folder = "",
            FolderOpen = "", Delete = "", Close = "", Play = "", Pause = "", Paste = "",
            Link = "", Check = "", Error = "", Info = "", Music = "", Video = "",
            Doc = "", Code = "", Rotate = "", ZoomIn = "", ZoomOut = "", Left = "",
            Right = "", Save = "", Copy = "", Refresh = "", Clock = "", Open = "",
            Upload = "", Warning = "", Fingerprint = "", Page = "", Sync = "", Heart = "",
            Contrast = "", Back = "", Forward = "", Stop = "", Volume = "", More = "",
            Edit = "", Bold = "", Italic = "", AlignLeft = "", AlignCenter = "", AlignRight = "", FontGlyph = "", Resize = "", Text = "";
    }

    /// Drives the animations at up to ~60 fps. Movement follows the clock, not the frame count:
    /// when a frame takes longer (a big window, a busy PC) things move further, so nothing slows down.
    public static class Animator
    {
        static readonly List<IAnimated> items = new List<IAnimated>();
        static Timer timer;
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        static double last;
        /// How many 15 ms frames' worth of time this frame stands for (1 = on time).
        public static float K = 1;
        /// Milliseconds since the app started (for animations that follow the clock).
        public static double Now { get { return clock.Elapsed.TotalMilliseconds; } }

        /// A "move this fraction of the way per 15 ms frame" speed, corrected for the real length of this frame.
        public static float Rate(float perFrame) { return K == 1 ? perFrame : 1 - (float)Math.Pow(1 - Math.Min(0.999f, perFrame), K); }

        public static void Start(IAnimated a)
        {
            if (!items.Contains(a)) items.Add(a);
            if (timer == null) { timer = new Timer(); timer.Interval = 15; timer.Tick += Tick; }
            if (!timer.Enabled) { last = Now - 15; timer.Start(); }
        }
        static void Tick(object s, EventArgs e)
        {
            double now = Now;
            K = (float)Math.Max(0.5, Math.Min(4, (now - last) / 15.0));
            last = now;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (i >= items.Count) continue;
                IAnimated it = items[i];
                bool more = false;
                try { more = it.Step(); } catch { }
                if (!more) { int k = items.IndexOf(it); if (k >= 0) items.RemoveAt(k); }
            }
            if (items.Count == 0) timer.Stop();
        }
    }

    public interface IAnimated { bool Step(); }
}
