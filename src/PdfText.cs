using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FileConverter
{
    /// "Make text searchable": an invisible layer of text (read from the page pictures) is laid over a PDF's pages,
    /// so you can search it, select it and copy it — like a scanned document run through a scanner's OCR.
    public static class PdfText
    {
        /// A tiny font with no shapes at all (every letter is invisible): the text layer only needs the letters' places.
        const string GlyphLess = "AAEAAAAKAIAAAwAgT1MvMkUAQ7AAAAEoAAAAYGNtYXAADABzAAABkAAAADRnbHlmAAAAAAAAAcwAAAABaGVhZC0F+BQAAACsAAAANmhoZWEDIgEuAAAA5AAAACRobXR4AfQAAAAAAYgAAAAGbG9jYQAAAAAAAAHEAAAABm1heHAAAwACAAABCAAAACBuYW1lqWij/wAAAdAAAABmcG9zdDIGAmcAAAI4AAAAKQABAAAAAQAAAGTVd18PPPUAAwPoAAAAAOb325oAAAAA5vfbmgAAAAAAAAAAAAAAAwACAAAAAAAAAAEAAAMg/zgAAAH0AAAAAAAAAAEAAAAAAAAAAAAAAAAAAAABAAEAAAACAAAAAAAAAAAAAgAAAAAAAAAAAAAAAAAAAAAAAwH0AZAABQAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAPz8/PwAAACAAIAMg/zgAAAMgAMgAAAAAAAAAAAAAAAAAAAAgAAAB9AAAAAAAAAAAAAIAAAADAAAAFAADAAEAAAAUAAQAIAAAAAQABAABAAAAIP//AAAAIP///+EAAQAAAAAAAAAAAAAAAAAAAAAAAAAEADYAAQAAAAAAAQAJAAAAAQAAAAAAAgAHAAkAAwABBAkAAQASABAAAwABBAkAAgAOACJHbHlwaExlc3NSZWd1bGFyAEcAbAB5AHAAaABMAGUAcwBzAFIAZQBnAHUAbABhAHIAAAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAAAECAmcxAAAA";

        public class PageText { public int ImgW, ImgH; public List<List<Ocr.Word>> Lines = new List<List<Ocr.Word>>(); }

        static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        /// Writes a PDF whose pages hold only invisible text, each word where it sits on the page's picture.
        public static void WriteLayer(string path, IList<PageText> pages)
        {
            // 1 catalog, 2 pages, 3 font, 4 CID font, 5 descriptor, 6 font file, 7 CID→glyph map, 8 to-Unicode map, then page + content each
            int next = 9;
            int[] pageNo = new int[pages.Count], contentNo = new int[pages.Count];
            for (int i = 0; i < pages.Count; i++) { pageNo[i] = next++; contentNo[i] = next++; }
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                long[] off = new long[next];
                Action<string> w = s => { byte[] d = Encoding.ASCII.GetBytes(s); fs.Write(d, 0, d.Length); };
                Action<int, string, byte[]> stream = (n, dict, data) =>
                {
                    off[n] = fs.Position;
                    w(n + " 0 obj\n<< " + dict + " /Length " + data.Length + " >>\nstream\n");
                    fs.Write(data, 0, data.Length);
                    w("\nendstream\nendobj\n");
                };
                w("%PDF-1.5\n%âã\n");
                off[1] = fs.Position; w("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                StringBuilder kids = new StringBuilder();
                foreach (int n in pageNo) kids.Append(n).Append(" 0 R ");
                off[2] = fs.Position; w("2 0 obj\n<< /Type /Pages /Kids [ " + kids + "] /Count " + pages.Count + " >>\nendobj\n");
                off[3] = fs.Position; w("3 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /GlyphLessFont /Encoding /Identity-H /DescendantFonts [4 0 R] /ToUnicode 8 0 R >>\nendobj\n");
                off[4] = fs.Position; w("4 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /GlyphLessFont /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /DW 500 /CIDToGIDMap 7 0 R >>\nendobj\n");
                off[5] = fs.Position; w("5 0 obj\n<< /Type /FontDescriptor /FontName /GlyphLessFont /Flags 5 /FontBBox [0 0 500 1000] /ItalicAngle 0 /Ascent 1000 /Descent 0 /CapHeight 1000 /StemV 80 /FontFile2 6 0 R >>\nendobj\n");
                byte[] font = Convert.FromBase64String(GlyphLess);
                stream(6, "/Length1 " + font.Length + " /Filter /FlateDecode", PdfStamp.Zlib(font));
                // every letter (CID) is drawn with the one invisible shape
                byte[] map = new byte[65536 * 2];
                for (int i = 0; i < 65536; i++) { map[i * 2] = 0; map[i * 2 + 1] = 1; }
                stream(7, "/Filter /FlateDecode", PdfStamp.Zlib(map));
                // and every CID is its own Unicode letter (so copying and searching get the real text)
                StringBuilder cm = new StringBuilder();
                cm.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
                for (int start = 0; start < 256; start += 100)
                {
                    int n = Math.Min(100, 256 - start);
                    cm.Append(n).Append(" beginbfrange\n");
                    for (int hi = start; hi < start + n; hi++) cm.AppendFormat("<{0:X2}00> <{0:X2}FF> <{0:X2}00>\n", hi);
                    cm.Append("endbfrange\n");
                }
                cm.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
                stream(8, "/Filter /FlateDecode", PdfStamp.Zlib(Encoding.ASCII.GetBytes(cm.ToString())));

                for (int i = 0; i < pages.Count; i++)
                {
                    PageText p = pages[i];
                    float iw = Math.Max(1, p.ImgW), ih = Math.Max(1, p.ImgH);
                    // qpdf shrinks an overlay page to fit the real page but never enlarges it: make it big (same shape)
                    float PW = 6120f, PH = PW * ih / iw;
                    if (PH > 14000f) { PH = 14000f; PW = PH * iw / ih; }
                    float k = PW / iw;
                    StringBuilder c = new StringBuilder("BT\n3 Tr\n");
                    foreach (List<Ocr.Word> line in p.Lines)
                    {
                        // in the order they're seen, left to right (PDF readers put right-to-left text back in reading order)
                        List<Ocr.Word> words = new List<Ocr.Word>(line);
                        words.Sort((u, v) => u.X.CompareTo(v.X));
                        if (words.Count == 0) continue;
                        bool arabic = false;
                        foreach (Ocr.Word wd in words) if (Ocr.IsArabic(wd.Text)) arabic = true;
                        if (arabic)
                        {
                            // a line with Arabic: one piece of text across the whole line (word by word, readers would
                            // put the words left to right)
                            StringBuilder hex = new StringBuilder(); int n = 0;
                            int x0 = int.MaxValue, x1 = 0, y1 = 0, hMax = 1;
                            // words in reading order, each Arabic word's letters as seen (that's what PDF readers expect)
                            List<Ocr.Word> reading = Ocr.LogicalWords(line);
                            for (int wi = 0; wi < reading.Count; wi++)
                            {
                                Ocr.Word wd = reading[wi];
                                if (wi > 0) { hex.Append("0020"); n++; }
                                n += AddText(hex, wd.Text);
                                x0 = Math.Min(x0, wd.X); x1 = Math.Max(x1, wd.X + wd.W); y1 = Math.Max(y1, wd.Y + wd.H); hMax = Math.Max(hMax, wd.H);
                            }
                            if (n == 0) continue;
                            Place(c, hex, n, x0 * k, PH - y1 * k, (x1 - x0) * k, hMax * k);
                            continue;
                        }
                        for (int wi = 0; wi < words.Count; wi++)
                        {
                            Ocr.Word wd = words[wi];
                            StringBuilder hex = new StringBuilder();
                            int n = AddText(hex, wd.Text);
                            if (n == 0) continue;
                            if (wi < words.Count - 1) hex.Append("0020");   // a space after each word but the last
                            Place(c, hex, n, wd.X * k, PH - (wd.Y + wd.H) * k, wd.W * k, Math.Max(1, wd.H) * k);
                        }
                    }
                    c.Append("ET\n");
                    off[pageNo[i]] = fs.Position;
                    w(pageNo[i] + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(PW) + " " + F(PH) + "] /Resources << /Font << /F1 3 0 R >> >> /Contents " + contentNo[i] + " 0 R >>\nendobj\n");
                    stream(contentNo[i], "/Filter /FlateDecode", PdfStamp.Zlib(Encoding.ASCII.GetBytes(c.ToString())));
                }
                long xref = fs.Position;
                StringBuilder xr = new StringBuilder("xref\n0 " + next + "\n0000000000 65535 f \n");
                for (int i = 1; i < next; i++) xr.Append(off[i].ToString("D10")).Append(" 00000 n \n");
                xr.Append("trailer\n<< /Size " + next + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
                w(xr.ToString());
            }
        }

        /// Adds a word's letters (as 4-digit hex codes); Arabic in the order it's seen (right to left). Returns how many.
        static int AddText(StringBuilder hex, string text)
        {
            if (Ocr.IsArabic(text)) { char[] rv = text.ToCharArray(); Array.Reverse(rv); text = new string(rv); }
            int n = 0;
            foreach (char ch in text) { if (char.IsSurrogate(ch)) continue; hex.Append(((int)ch).ToString("X4")); n++; }
            return n;
        }

        /// Invisible text from (x, y) — its baseline, in page units — stretched to width w, size h.
        static void Place(StringBuilder c, StringBuilder hex, int n, float x, float y, float w, float h)
        {
            float natural = n * 0.5f * h;
            float tz = Math.Max(1f, Math.Min(10000f, w / Math.Max(0.01f, natural) * 100f));
            c.Append("/F1 ").Append(F(h)).Append(" Tf ").Append(F(tz)).Append(" Tz 1 0 0 1 ").Append(F(x)).Append(' ').Append(F(y)).Append(" Tm <").Append(hex).Append("> Tj\n");
        }

        /// Reads every page of a PDF and lays the invisible text over it. progress(done, total, what).
        public static void MakeSearchable(string inPdf, string outPdf, string workDir, Action<int, int, string> progress, Func<bool> cancelled)
        {
            string pics = Path.Combine(workDir, "pages");
            // pictures of the pages, sharp enough to read small print
            List<string> pngs = DocPages.RenderTo(inPdf, pics, 3000, (d, t) => progress(d, Math.Max(d, t), "render"), cancelled);
            if (cancelled()) throw new OperationCanceledException();
            List<PageText> pages = new List<PageText>();
            int batch = 4;
            for (int i = 0; i < pngs.Count; i += batch)
            {
                if (cancelled()) throw new OperationCanceledException();
                List<string> part = pngs.GetRange(i, Math.Min(batch, pngs.Count - i));
                List<List<Ocr.Read>> reads = Ocr.ReadPictures(part, workDir);
                for (int j = 0; j < part.Count; j++)
                {
                    PageText pt = new PageText();
                    try { using (System.Drawing.Image im = System.Drawing.Image.FromFile(part[j])) { pt.ImgW = im.Width; pt.ImgH = im.Height; } } catch { pt.ImgW = 1000; pt.ImgH = 1414; }
                    Ocr.Read best = j < reads.Count ? Ocr.Best(reads[j]) : null;
                    if (best != null) pt.Lines = best.Lines;
                    pages.Add(pt);
                }
                progress(Math.Min(pngs.Count, i + batch), pngs.Count, "read");
            }
            string layer = Path.Combine(workDir, "text_layer.pdf");
            WriteLayer(layer, pages);
            string err = PdfStamp.ApplyFile(inPdf, outPdf, layer);
            if (err != null) throw new UserError(err);
        }
    }
}
