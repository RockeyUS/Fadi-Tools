using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace FileConverter
{
    /// Clean outline icons (24×24 grid, round strokes), drawn as vectors so they stay crisp at any size.
    /// Each icon is a tiny SVG-style path: M L H V C Q A Z (lower case = relative), plus
    /// "O cx cy r" for a circle and "R x y w h r" for a rounded rectangle. A leading "!" fills the shape.
    public static class VectorIcons
    {
        static readonly Dictionary<char, string> Paths = new Dictionary<char, string>
        {
            { '\uF406', "M6 2V16a2 2 0 0 0 2 2H22M2 6H16a2 2 0 0 1 2 2V22" },                         // crop
            { '\uE8FD', "M9 6H21M9 12H21M9 18H21M4 6H4.01M4 12H4.01M4 18H4.01" },                // list view
            { '\uF0E2', "R3 4 8 7 1.5R13 4 8 7 1.5R3 13 8 7 1.5R13 13 8 7 1.5" },                   // previews (picture view)
            { '', "M12 5V19M5 12H19" },                                                         // add
            { '', "M18 6L6 18M6 6L18 18" },                                                     // close
            { '', "M6.5 6.5L17.5 17.5M17.5 6.5L6.5 17.5" },                                     // window close
            { '', "M6 12H18" },                                                                 // minimize
            { '', "R6 6 12 12 1.5" },                                                           // maximize
            { '', "R5 9 10 10 1.5M9 5H17a2 2 0 0 1 2 2V15" },                                   // restore
            { '', "M20 6L9 17L4 12" },                                                          // check
            { '', "M6 9L12 15L18 9" },                                                          // chevron down
            { '', "M18 15L12 9L6 15" },                                                         // chevron up
            { '', "M15 18L9 12L15 6" },                                                         // chevron left
            { '', "M9 18L15 12L9 6" },                                                          // chevron right
            { '', "M19 12H5M12 19L5 12L12 5" },                                                 // back
            { '', "M5 12H19M12 5L19 12L12 19" },                                                // forward
            { '', "O11 11 7M20 20L16 16" },                                                     // search
            { '', "M20 7H11M14 17H5O17 17 3O7 7 3" },                                           // settings (sliders)
            { '', "M21 15V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V15M7 10L12 15L17 10M12 15V3" },      // download
            { '', "M21 15V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V15M17 8L12 3L7 8M12 3V15" },         // upload
            { '', "R3 3 18 18 2O9 9 2M21 15L17.9 11.9a2 2 0 0 0-2.8 0L6 21" },                  // photo
            { '', "O12 12 4M12 2V4M12 20V22M4.9 4.9L6.3 6.3M17.7 17.7L19.1 19.1M2 12H4M20 12H22M6.3 17.7L4.9 19.1M19.1 4.9L17.7 6.3" }, // sun
            { '', "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z" },                                       // moon
            { '', "O12 12 9.5M12 2.5a14 14 0 0 0 0 19 14 14 0 0 0 0-19M2.5 12H21.5" },          // globe
            { '', "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2H12.1a2 2 0 0 1-1.7-.9L9.6 3.9A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2V18a2 2 0 0 0 2 2Z" }, // folder
            { '', "M6 14L7.5 11.1A2 2 0 0 1 9.2 10H20a2 2 0 0 1 1.9 2.5L20.4 18.5A2 2 0 0 1 18.5 20H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2H7.9a2 2 0 0 1 1.7.9L10.4 5.1A2 2 0 0 0 12.1 6H18a2 2 0 0 1 2 2V10" }, // folder open
            { '', "M3 6H21M19 6V20a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6M8 6V4a2 2 0 0 1 2-2H14a2 2 0 0 1 2 2V6M10 11V17M14 11V17" }, // delete
            { '', "!M7 4.5V19.5a1 1 0 0 0 1.5.9L20 13a1.2 1.2 0 0 0 0-2L8.5 3.6A1 1 0 0 0 7 4.5Z" }, // play
            { '', "!R6 4 4 16 1R14 4 4 16 1" },                                                 // pause
            { '', "!R6 6 12 12 2" },                                                            // stop
            { '', "R8 2 8 4 1M16 4H18a2 2 0 0 1 2 2V20a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2H8" }, // paste
            { '', "M10 13a5 5 0 0 0 7.5.5L20.5 10.5a5 5 0 0 0-7-7L11.8 5.2M14 11a5 5 0 0 0-7.5-.5L3.5 13.5a5 5 0 0 0 7 7L12.2 18.8" }, // link
            { '', "O12 12 9.5M15 9L9 15M9 9L15 15" },                                           // error
            { '', "O12 12 9.5M12 16V11.5M12 8H12.01" },                                        // info
            { '', "O12 12 9.5M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17H12.01" },                 // help
            { '', "M9 18V5L21 3V16O6 18 3O18 16 3" },                                          // music
            { '', "R2 6 14 12 2M16 10.5L21.2 7.5a.5.5 0 0 1 .8.4V16.1a.5.5 0 0 1-.8.4L16 13.5" }, // video
            { '', "M14.5 2H6a2 2 0 0 0-2 2V20a2 2 0 0 0 2 2H18a2 2 0 0 0 2-2V7.5ZM14 2V8H20M16 13H8M16 17H8M10 9H8" }, // document
            { '', "M14.5 2H6a2 2 0 0 0-2 2V20a2 2 0 0 0 2 2H18a2 2 0 0 0 2-2V7.5ZM14 2V8H20" }, // page
            { '', "M16 18L22 12L16 6M8 6L2 12L8 18" },                                          // code
            { '', "M21 12a9 9 0 1 1-9-9C14.5 3 16.9 4 18.7 5.7L21 8M21 3V8H16" },              // rotate
            { '', "O11 11 7M20 20L16 16M11 8V14M8 11H14" },                                     // zoom in
            { '', "O11 11 7M20 20L16 16M8 11H14" },                                             // zoom out
            { '', "M15.2 3a2 2 0 0 1 1.4.6L20.4 7.4a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2ZM17 21V14a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1V21M7 3V7a1 1 0 0 0 1 1H15" }, // save
            { '', "R8 8 14 14 2M4 16a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2H14a2 2 0 0 1 2 2" },          // copy
            { '', "M3 12a9 9 0 0 1 9-9C14.5 3 16.9 4 18.7 5.7L21 8M21 3V8H16M21 12a9 9 0 0 1-9 9C9.5 21 7.1 20 5.3 18.3L3 16M8 16H3V21" }, // refresh
            { '', "O12 12 9.5M12 7V12L15.5 14" },                                               // clock
            { '', "M15 3H21V9M10 14L21 3M18 13V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2H11" }, // open
            { '', "M10.3 4.1L2.4 18A2 2 0 0 0 4.1 21H19.9a2 2 0 0 0 1.7-3L13.7 4.1a2 2 0 0 0-3.4 0ZM12 9V13M12 17H12.01" }, // warning
            { '', "M4 9H20M4 15H20M10 3L8 21M16 3L14 21" },                                     // hash (fingerprint)
            { '', "M8 3L4 7L8 11M4 7H20M16 21L20 17L16 13M20 17H4" },                           // convert
            { '', "M19 14c1.5-1.5 3-3.2 3-5.5A5.5 5.5 0 0 0 16.5 3C14.7 3 13.5 3.5 12 5 10.5 3.5 9.3 3 7.5 3A5.5 5.5 0 0 0 2 8.5C2 10.8 3.5 12.6 5 14L12 21Z" }, // heart
            { '', "O12 12 9.5M12 2.5V21.5M12 7.5L16.5 4M12 12.5L20.5 7.5M12 17.5L19.5 15" },     // appearance
            { '', "M11 5L6 9H2V15H6L11 19ZM15.5 8.5a5 5 0 0 1 0 7M19 5a10 10 0 0 1 0 14" },     // volume
            { '', "!O5 12 1.6O12 12 1.6O19 12 1.6" },                                           // more
            { '', "M17 3a2.8 2.8 0 1 1 4 4L7.5 20.5L2 22L3.5 16.5ZM15 5L19 9" },                // edit
            { '', "M6 12H15a4 4 0 0 1 0 8H6V4H14a4 4 0 0 1 0 8" },                              // bold
            { '', "M19 4H10M14 20H5M15 4L9 20" },                                               // italic
            { '', "M15 12H3M17 18H3M21 6H3" },                                                  // align left
            { '', "M17 12H7M19 18H5M21 6H3" },                                                  // align center
            { '', "M21 12H9M21 18H7M21 6H3" },                                                  // align right
            { '', "M4 7V4H20V7M9 20H15M12 4V20" },                                              // text
            { '', "M15 3H21V9M9 21H3V15M21 3L14 10M3 21L10 14" },                               // resize
            { '', "O6 6 3O6 18 3M20 4L8.1 15.9M14.5 14.5L20 20M8.1 8.1L12 12" },                // cut
            { '', "M9 14L4 9L9 4M4 9H14.5a5.5 5.5 0 0 1 0 11H11" },                             // undo
            { '', "M15 14L20 9L15 4M20 9H9.5a5.5 5.5 0 0 0 0 11H13" },                          // redo
            { '', "M12 3L13.9 8.1L19 10L13.9 11.9L12 17L10.1 11.9L5 10L10.1 8.1ZM19 3V7M21 5H17M5 17V21M7 19H3" }, // effects
            { '', "M4 14H10V20M20 10H14V4M14 10L21 3M3 21L10 14" },                             // compress
            { '', "M12 3L13.9 8.1L19 10L13.9 11.9L12 17L10.1 11.9L5 10L10.1 8.1Z" },            // sparkle
            { '', "M3 12a9 9 0 1 0 9-9C9.5 3 7.1 4 5.3 5.7L3 8M3 3V8H8" },                              // rotate left
            { '', "M22 12H18L15 20L9 4L6 12H2" },                                                        // activity
            { '', "M15.5 2H9a2 2 0 0 0-2 2V15a2 2 0 0 0 2 2H18a2 2 0 0 0 2-2V6.5ZM15 2V7H20M4 7.5V19a2 2 0 0 0 2 2H15" }, // pdf tools (pages)
            { '', "M8 6L12 2L16 6M12 2V10a4 4 0 0 1-1.2 2.8L5 19M19 19L14.8 14.8" },                     // merge
            { '', "M16 3H21V8M8 3H3V8M12 21V13.7a4 4 0 0 0-1.2-2.9L3 3M15 9L21 3" },                     // split
            { '', "M21 16L17 20L13 16M17 20V4M3 8L7 4L11 8M7 4V20" },                                    // reorder
            { '', "M3 7V5a2 2 0 0 1 2-2H7M17 3H19a2 2 0 0 1 2 2V7M21 17V19a2 2 0 0 1-2 2H17M7 21H5a2 2 0 0 1-2-2V17M7 8H15M7 12H17M7 16H12" }, // scan text
            { '', "R3 5 18 14 2.5M7 15H11M14 15H17M7 11H9M12 11H17" },                                   // captions
            { '', "M3 12a9 9 0 1 0 2.6-6.4L3 8M3 3V8H8M12 7V12L15.5 14" },                               // history
            { '', "M12 2L2 7L12 12L22 7ZM2 17L12 22L22 17M2 12L12 17L22 12" },                           // layers / project
            { '', "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2H12.1a2 2 0 0 1-1.7-.9L9.6 3.9A2 2 0 0 0 7.9 3H4a2 2 0 0 0-2 2V18a2 2 0 0 0 2 2ZM12 10V16M9 13H15" }, // new folder / new project
            { '', "R2 5 20 14 2.5M6 9H6.01M10 9H10.01M14 9H14.01M18 9H18.01M7 15H17" },                  // keyboard
            { '', "M10.3 21a2 2 0 0 0 3.4 0M3.3 15.3A1 1 0 0 0 4 17H20a1 1 0 0 0 .7-1.7C19.4 14 18 12.6 18 8A6 6 0 0 0 6 8C6 12.6 4.6 14 3.3 15.3" }, // bell
            { '', "M3 3H8V8H3ZM16 3H21V8H16ZM3 16H8V21H3ZM16 16H21V21H16Z" },                            // grid
        };

        static readonly Dictionary<char, GraphicsPath[]> cache = new Dictionary<char, GraphicsPath[]>();
        static readonly Dictionary<char, bool> filled = new Dictionary<char, bool>();

        public static bool Has(string glyph) { return glyph != null && glyph.Length == 1 && Paths.ContainsKey(glyph[0]); }

        /// Draws the icon for a glyph centered in r; returns false when there is no vector version.
        public static bool Draw(Graphics g, string glyph, float size, Color c, RectangleF r)
        {
            if (!Has(glyph)) return false;
            char k = glyph[0];
            GraphicsPath[] parts;
            if (!cache.TryGetValue(k, out parts))
            {
                string src = Paths[k];
                bool fill = src.StartsWith("!");
                filled[k] = fill;
                try { parts = Parse(fill ? src.Substring(1) : src); } catch { parts = new GraphicsPath[0]; }
                cache[k] = parts;
            }
            // the font sizes used across the app are points: match the old glyph size on screen
            float px = size * 1.42f * Theme.Scale;
            float k2 = px / 24f;
            GraphicsState st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(r.X + (r.Width - px) / 2f, r.Y + (r.Height - px) / 2f);
            g.ScaleTransform(k2, k2);
            float stroke = Math.Max(1.15f / k2, 1.7f);
            using (Pen pen = new Pen(c, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (SolidBrush b = new SolidBrush(c))
                foreach (GraphicsPath p in parts)
                {
                    if (filled[k]) g.FillPath(b, p);
                    else g.DrawPath(pen, p);
                }
            g.Restore(st);
            return true;
        }

        // ------------------------------------------------------------------ tiny path parser
        static List<string> Tokens(string s)
        {
            List<string> t = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char ch = s[i];
                if (char.IsLetter(ch)) { t.Add(ch.ToString()); i++; continue; }
                if (ch == ' ' || ch == ',') { i++; continue; }
                int st = i;
                if (ch == '-' || ch == '+') i++;
                bool dot = false;
                while (i < s.Length && (char.IsDigit(s[i]) || (s[i] == '.' && !dot)))
                {
                    if (s[i] == '.') dot = true;
                    i++;
                }
                if (i == st) { i++; continue; }
                t.Add(s.Substring(st, i - st));
            }
            return t;
        }

        static GraphicsPath[] Parse(string d)
        {
            List<GraphicsPath> parts = new List<GraphicsPath>();
            GraphicsPath p = new GraphicsPath();
            List<string> t = Tokens(d);
            int i = 0;
            char cmd = 'M';
            PointF cur = PointF.Empty, start = PointF.Empty;
            Func<float> num = () => float.Parse(t[i++], CultureInfo.InvariantCulture);
            while (i < t.Count)
            {
                if (char.IsLetter(t[i][0])) cmd = t[i++][0];
                bool rel = char.IsLower(cmd) && cmd != 'O';
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        {
                            float x = num(), y = num();
                            if (rel) { x += cur.X; y += cur.Y; }
                            p.StartFigure();
                            cur = start = new PointF(x, y);
                            cmd = rel ? 'l' : 'L';   // further pairs are lines
                            break;
                        }
                    case 'L':
                        {
                            float x = num(), y = num();
                            if (rel) { x += cur.X; y += cur.Y; }
                            p.AddLine(cur, new PointF(x, y)); cur = new PointF(x, y);
                            break;
                        }
                    case 'H': { float x = num(); if (rel) x += cur.X; p.AddLine(cur, new PointF(x, cur.Y)); cur = new PointF(x, cur.Y); break; }
                    case 'V': { float y = num(); if (rel) y += cur.Y; p.AddLine(cur, new PointF(cur.X, y)); cur = new PointF(cur.X, y); break; }
                    case 'C':
                        {
                            float x1 = num(), y1 = num(), x2 = num(), y2 = num(), x = num(), y = num();
                            if (rel) { x1 += cur.X; y1 += cur.Y; x2 += cur.X; y2 += cur.Y; x += cur.X; y += cur.Y; }
                            p.AddBezier(cur, new PointF(x1, y1), new PointF(x2, y2), new PointF(x, y)); cur = new PointF(x, y);
                            break;
                        }
                    case 'Q':
                        {
                            float qx = num(), qy = num(), x = num(), y = num();
                            if (rel) { qx += cur.X; qy += cur.Y; x += cur.X; y += cur.Y; }
                            PointF c1 = new PointF(cur.X + 2f / 3 * (qx - cur.X), cur.Y + 2f / 3 * (qy - cur.Y));
                            PointF c2 = new PointF(x + 2f / 3 * (qx - x), y + 2f / 3 * (qy - y));
                            p.AddBezier(cur, c1, c2, new PointF(x, y)); cur = new PointF(x, y);
                            break;
                        }
                    case 'A':
                        {
                            float rx = num(), ry = num(); num(); float large = num(), sweep = num(), x = num(), y = num();
                            if (rel) { x += cur.X; y += cur.Y; }
                            Arc(p, cur, new PointF(x, y), rx, ry, large != 0, sweep != 0);
                            cur = new PointF(x, y);
                            break;
                        }
                    case 'Z':
                        p.CloseFigure(); cur = start;
                        break;
                    case 'O':
                        {
                            float cx = num(), cy = num(), rr = num();
                            GraphicsPath c = new GraphicsPath(); c.AddEllipse(cx - rr, cy - rr, rr * 2, rr * 2);
                            parts.Add(c);
                            break;
                        }
                    case 'R':
                        {
                            float x = num(), y = num(), w = num(), h = num(), rr = num();
                            parts.Add(Theme.Round(new RectangleF(x, y, w, h), rr));
                            break;
                        }
                    default: i++; break;
                }
            }
            if (p.PointCount > 0) parts.Insert(0, p);
            return parts.ToArray();
        }

        /// SVG endpoint arc → GDI+ arc (no rotation needed for these icons).
        static void Arc(GraphicsPath p, PointF a, PointF b, float rx, float ry, bool large, bool sweep)
        {
            if (rx <= 0 || ry <= 0) { p.AddLine(a, b); return; }
            double x1p = (a.X - b.X) / 2.0, y1p = (a.Y - b.Y) / 2.0;
            double lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lam > 1) { double s = Math.Sqrt(lam); rx = (float)(rx * s); ry = (float)(ry * s); }
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            double co = Math.Sqrt(Math.Max(0, num / Math.Max(1e-9, den))) * (large == sweep ? -1 : 1);
            double cxp = co * rx * y1p / ry, cyp = -co * ry * x1p / rx;
            double cx = cxp + (a.X + b.X) / 2.0, cy = cyp + (a.Y + b.Y) / 2.0;
            double t1 = Math.Atan2((y1p - cyp) / ry, (x1p - cxp) / rx);
            double t2 = Math.Atan2((-y1p - cyp) / ry, (-x1p - cxp) / rx);
            double dt = t2 - t1;
            if (sweep && dt < 0) dt += 2 * Math.PI;
            if (!sweep && dt > 0) dt -= 2 * Math.PI;
            p.AddArc((float)(cx - rx), (float)(cy - ry), rx * 2, ry * 2, (float)(t1 * 180 / Math.PI), (float)(dt * 180 / Math.PI));
        }
    }
}
