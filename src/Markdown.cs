using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    /// Small, dependency-free Markdown <-> HTML converter (CommonMark/GFM subset:
    /// headings, emphasis, code, lists, task lists, quotes, tables, links, images, rules).
    public static class Markdown
    {
        public static string ToHtmlDocument(string md, string title, string baseDir = null)
        {
            string baseTag = baseDir != null ? "<base href=\"" + Esc(new System.Uri(baseDir.TrimEnd('\\') + "\\").AbsoluteUri) + "\">\n" : "";
            return "<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n" + baseTag + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>" +
                Esc(title) + "</title>\n<style>\n" + Css + "\n</style>\n</head>\n<body>\n<article>\n" + ToHtml(md) + "</article>\n</body>\n</html>\n";
        }

        public const string Css =
@"body{margin:0;background:#fff;color:#1f2328;font:16px/1.65 'Segoe UI',system-ui,-apple-system,Arial,sans-serif}
article{max-width:860px;margin:0 auto;padding:40px 32px}
h1,h2,h3,h4,h5,h6{line-height:1.25;margin:1.6em 0 .6em;font-weight:650}
h1{font-size:2em;border-bottom:1px solid #d8dee4;padding-bottom:.3em}h2{font-size:1.5em;border-bottom:1px solid #d8dee4;padding-bottom:.3em}
h3{font-size:1.25em}p,ul,ol,blockquote,table,pre{margin:0 0 1em}
p,li,h1,h2,h3,h4,h5,h6,blockquote,td,th{unicode-bidi:plaintext}
a{color:#0b62d6;text-decoration:none}a:hover{text-decoration:underline}
code{font:.9em Consolas,'Cascadia Mono',monospace;background:#eff1f3;border-radius:6px;padding:.15em .4em}
pre{background:#f6f8fa;border:1px solid #e3e6ea;border-radius:10px;padding:14px 16px;overflow:auto;direction:ltr}
pre code{background:none;padding:0;font-size:.88em;line-height:1.5}
blockquote{border-left:4px solid #d0d7de;color:#59636e;padding:.2em 1em;margin-left:0}
table{border-collapse:collapse;display:block;overflow:auto}th,td{border:1px solid #d0d7de;padding:6px 13px}
th{background:#f6f8fa;font-weight:600}tr:nth-child(2n) td{background:#fafbfc}
img{max-width:100%}hr{border:0;border-top:1px solid #d8dee4;margin:2em 0}
li.task{list-style:none;margin-left:-1.3em}li.task input{margin-right:.5em}
@media print{article{padding:0}}";

        // ---------------------------------------------------------------- blocks
        public static string ToHtml(string md)
        {
            string[] lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").Split('\n');
            StringBuilder sb = new StringBuilder();
            RenderBlocks(new List<string>(lines), sb);
            return sb.ToString();
        }

        static readonly Regex HeadingRx = new Regex(@"^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$");
        static readonly Regex HrRx = new Regex(@"^ {0,3}((\*\s*){3,}|(-\s*){3,}|(_\s*){3,})$");
        static readonly Regex ListRx = new Regex(@"^( *)([-*+]|\d{1,9}[.)])\s+(.*)$");
        static readonly Regex FenceRx = new Regex(@"^ {0,3}(`{3,}|~{3,})\s*([\w+#.-]*)");
        static readonly Regex TableSepRx = new Regex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");

        static void RenderBlocks(List<string> lines, StringBuilder sb)
        {
            List<string> para = new List<string>();
            int i = 0;
            while (i < lines.Count)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (trimmed.Length == 0) { Flush(para, sb); i++; continue; }

                Match fm = FenceRx.Match(line);
                if (fm.Success)
                {
                    Flush(para, sb);
                    string fence = fm.Groups[1].Value;
                    string lang = fm.Groups[2].Value;
                    StringBuilder code = new StringBuilder();
                    i++;
                    while (i < lines.Count && !lines[i].Trim().StartsWith(fence)) { code.Append(lines[i]).Append('\n'); i++; }
                    i++;
                    sb.Append("<pre><code").Append(lang.Length > 0 ? " class=\"language-" + Esc(lang) + "\"" : "").Append('>')
                      .Append(Esc(code.ToString())).Append("</code></pre>\n");
                    continue;
                }

                Match hm = HeadingRx.Match(line);
                if (hm.Success)
                {
                    Flush(para, sb);
                    int lv = hm.Groups[1].Value.Length;
                    string text = hm.Groups[2].Value;
                    sb.Append("<h").Append(lv).Append(" id=\"").Append(Slug(text)).Append("\">").Append(Inline(text)).Append("</h").Append(lv).Append(">\n");
                    i++; continue;
                }

                // setext headings
                if (para.Count > 0 && Regex.IsMatch(trimmed, @"^(=+|-+)$") && !ListRx.IsMatch(para[para.Count - 1]))
                {
                    int lv = trimmed[0] == '=' ? 1 : 2;
                    string text = string.Join(" ", para.ToArray()).Trim();
                    para.Clear();
                    sb.Append("<h").Append(lv).Append(" id=\"").Append(Slug(text)).Append("\">").Append(Inline(text)).Append("</h").Append(lv).Append(">\n");
                    i++; continue;
                }

                if (HrRx.IsMatch(line)) { Flush(para, sb); sb.Append("<hr>\n"); i++; continue; }

                if (trimmed.StartsWith(">"))
                {
                    Flush(para, sb);
                    List<string> q = new List<string>();
                    while (i < lines.Count && lines[i].Trim().Length > 0)
                    {
                        string l = lines[i].TrimStart();
                        if (l.StartsWith(">")) l = l.Substring(1).StartsWith(" ") ? l.Substring(2) : l.Substring(1);
                        q.Add(l); i++;
                    }
                    sb.Append("<blockquote>\n");
                    RenderBlocks(q, sb);
                    sb.Append("</blockquote>\n");
                    continue;
                }

                if (line.Contains("|") && i + 1 < lines.Count && TableSepRx.IsMatch(lines[i + 1]) && lines[i + 1].Contains("-"))
                {
                    Flush(para, sb);
                    List<string> head = Cells(line);
                    List<string> aligns = new List<string>();
                    foreach (string c in Cells(lines[i + 1]))
                    {
                        string t = c.Trim();
                        aligns.Add(t.StartsWith(":") && t.EndsWith(":") ? "center" : t.EndsWith(":") ? "right" : t.StartsWith(":") ? "left" : "");
                    }
                    i += 2;
                    sb.Append("<table>\n<thead><tr>");
                    for (int c = 0; c < head.Count; c++) sb.Append(Cell("th", head[c], c < aligns.Count ? aligns[c] : ""));
                    sb.Append("</tr></thead>\n<tbody>\n");
                    while (i < lines.Count && lines[i].Contains("|") && lines[i].Trim().Length > 0)
                    {
                        List<string> row = Cells(lines[i]);
                        sb.Append("<tr>");
                        for (int c = 0; c < head.Count; c++) sb.Append(Cell("td", c < row.Count ? row[c] : "", c < aligns.Count ? aligns[c] : ""));
                        sb.Append("</tr>\n");
                        i++;
                    }
                    sb.Append("</tbody>\n</table>\n");
                    continue;
                }

                Match lm = ListRx.Match(line);
                if (lm.Success && (para.Count == 0 || lm.Groups[1].Value.Length == 0))
                {
                    Flush(para, sb);
                    i = RenderList(lines, i, sb);
                    continue;
                }

                if (line.StartsWith("    ") && para.Count == 0)
                {
                    StringBuilder code = new StringBuilder();
                    while (i < lines.Count && (lines[i].StartsWith("    ") || lines[i].Trim().Length == 0))
                    {
                        code.Append(lines[i].Length >= 4 ? lines[i].Substring(4) : "").Append('\n'); i++;
                    }
                    sb.Append("<pre><code>").Append(Esc(code.ToString().TrimEnd('\n') + "\n")).Append("</code></pre>\n");
                    continue;
                }

                if (trimmed.StartsWith("<") && para.Count == 0 && Regex.IsMatch(trimmed, @"^</?(div|p|table|img|br|details|summary|section|center|h\d|ul|ol|hr|span|a)\b", RegexOptions.IgnoreCase))
                {
                    sb.Append(line).Append('\n'); i++; continue;
                }

                para.Add(line);
                i++;
            }
            Flush(para, sb);
        }

        static int RenderList(List<string> lines, int i, StringBuilder sb)
        {
            Match first = ListRx.Match(lines[i]);
            int baseIndent = first.Groups[1].Value.Length;
            bool ordered = char.IsDigit(first.Groups[2].Value[0]);
            int startNum = 1;
            if (ordered) int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out startNum);
            sb.Append(ordered ? (startNum != 1 ? "<ol start=\"" + startNum + "\">\n" : "<ol>\n") : "<ul>\n");
            while (i < lines.Count)
            {
                Match m = ListRx.Match(lines[i]);
                if (!m.Success || m.Groups[1].Value.Length != baseIndent) break;
                bool thisOrdered = char.IsDigit(m.Groups[2].Value[0]);
                if (thisOrdered != ordered) break;
                int contentIndent = baseIndent + m.Groups[2].Value.Length + 1;
                List<string> item = new List<string>();
                item.Add(m.Groups[3].Value);
                i++;
                while (i < lines.Count)
                {
                    string l = lines[i];
                    if (l.Trim().Length == 0)
                    {
                        if (i + 1 < lines.Count && Indent(lines[i + 1]) > baseIndent) { item.Add(""); i++; continue; }
                        break;
                    }
                    int ind = Indent(l);
                    if (ind > baseIndent) { int strip = Math.Min(ind, Math.Max(baseIndent + 2, contentIndent)); item.Add(l.Substring(strip)); i++; continue; }
                    if (ListRx.IsMatch(l)) break;
                    // lazy continuation
                    item.Add(l.Trim()); i++;
                }
                string text = item[0];
                string cls = "";
                Match task = Regex.Match(text, @"^\[([ xX])\]\s+(.*)$");
                if (task.Success)
                {
                    cls = " class=\"task\"";
                    text = "<input type=\"checkbox\" disabled" + (task.Groups[1].Value.Trim().Length > 0 ? " checked" : "") + "> " + task.Groups[2].Value;
                }
                sb.Append("<li").Append(cls).Append('>');
                if (item.Count == 1) sb.Append(task.Success ? InlineKeepInput(text) : Inline(text));
                else
                {
                    List<string> rest = item.GetRange(1, item.Count - 1);
                    sb.Append(task.Success ? InlineKeepInput(text) : Inline(text)).Append('\n');
                    RenderBlocks(rest, sb);
                }
                sb.Append("</li>\n");
            }
            sb.Append(ordered ? "</ol>\n" : "</ul>\n");
            return i;
        }

        static string InlineKeepInput(string s)
        {
            int k = s.IndexOf('>');
            return s.Substring(0, k + 1) + Inline(s.Substring(k + 1));
        }

        static int Indent(string l) { int n = 0; while (n < l.Length && l[n] == ' ') n++; return n; }

        static void Flush(List<string> para, StringBuilder sb)
        {
            if (para.Count == 0) return;
            StringBuilder p = new StringBuilder();
            for (int k = 0; k < para.Count; k++)
            {
                string l = para[k];
                bool hard = l.EndsWith("  ") || l.EndsWith("\\");
                p.Append(Inline(l.Trim().TrimEnd('\\')));
                if (k < para.Count - 1) p.Append(hard ? "<br>\n" : "\n");
            }
            sb.Append("<p>").Append(p).Append("</p>\n");
            para.Clear();
        }

        static List<string> Cells(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
            List<string> cells = new List<string>();
            StringBuilder cur = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cur.Append('|'); i++; }
                else if (t[i] == '|') { cells.Add(cur.ToString().Trim()); cur.Clear(); }
                else cur.Append(t[i]);
            }
            cells.Add(cur.ToString().Trim());
            return cells;
        }

        static string Cell(string tag, string text, string align)
        {
            return "<" + tag + (align.Length > 0 ? " style=\"text-align:" + align + "\"" : "") + ">" + Inline(text) + "</" + tag + ">";
        }

        static string Slug(string s)
        {
            string t = Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\p{Nd}\s-]", "");
            return Regex.Replace(t.Trim(), @"\s+", "-");
        }

        // ---------------------------------------------------------------- inline
        public static string Inline(string s)
        {
            List<string> slots = new List<string>();
            Func<string, string> hold = v => { slots.Add(v); return "\u0001" + (slots.Count - 1) + "\u0002"; };

            // code spans
            s = Regex.Replace(s, @"(`+)(.+?)\1", m => hold("<code>" + Esc(m.Groups[2].Value.Trim()) + "</code>"));
            // autolinks
            s = Regex.Replace(s, @"<(https?://[^>\s]+)>", m => hold("<a href=\"" + Attr(m.Groups[1].Value) + "\">" + Esc(m.Groups[1].Value) + "</a>"));
            // inline html we allow through
            s = Regex.Replace(s, @"</?(br|kbd|sub|sup|u|mark|span|b|i|em|strong|small)\b[^>]*>", m => hold(m.Value), RegexOptions.IgnoreCase);
            // escapes
            s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!|~<>])", m => hold(Esc(m.Groups[1].Value)));

            s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

            // images and links
            s = Regex.Replace(s, @"!\[([^\]]*)\]\(\s*([^)\s]+)(?:\s+""([^""]*)"")?\s*\)", m =>
                hold("<img src=\"" + Attr(m.Groups[2].Value) + "\" alt=\"" + Attr(m.Groups[1].Value) + "\"" + (m.Groups[3].Success ? " title=\"" + Attr(m.Groups[3].Value) + "\"" : "") + ">"));
            s = Regex.Replace(s, @"\[([^\]]+)\]\(\s*([^)\s]+)(?:\s+""([^""]*)"")?\s*\)", m =>
                "<a href=\"" + Attr(m.Groups[2].Value) + "\"" + (m.Groups[3].Success ? " title=\"" + Attr(m.Groups[3].Value) + "\"" : "") + ">" + m.Groups[1].Value + "</a>");
            // bare urls
            s = Regex.Replace(s, @"(?<![""=>/])\b(https?://[^\s<]+[^\s<.,;:!?)\]])", m => hold("<a href=\"" + Attr(m.Value) + "\">" + m.Value + "</a>"));

            s = Regex.Replace(s, @"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", "<strong>$2</strong>");
            s = Regex.Replace(s, @"(?<![\w*])\*(?=\S)(.+?)(?<=\S)\*(?!\*)", "<em>$1</em>");
            s = Regex.Replace(s, @"(?<![\w_])_(?=\S)(.+?)(?<=\S)_(?![\w_])", "<em>$1</em>");
            s = Regex.Replace(s, @"~~(?=\S)(.+?)(?<=\S)~~", "<del>$1</del>");

            for (int k = 0; k < 4 && s.IndexOf('\u0001') >= 0; k++)
                s = Regex.Replace(s, "\u0001(\\d+)\u0002", m => slots[int.Parse(m.Groups[1].Value)]);
            return s;
        }

        static string Attr(string v) { return Esc(WebUtility.HtmlDecode(v)).Replace("\"", "&quot;"); }

        /// Minimal HTML escaping (keeps Arabic and other text readable in the file).
        public static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // ---------------------------------------------------------------- HTML -> Markdown / text
        public static string FromHtml(string html)
        {
            string s = html.Replace("\r\n", "\n");
            Match body = Regex.Match(s, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (body.Success) s = body.Groups[1].Value;
            s = Regex.Replace(s, @"<(script|style|head|noscript)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<!--.*?-->", "", RegexOptions.Singleline);

            List<string> slots = new List<string>();
            Func<string, string> hold = v => { slots.Add(v); return "\u0001" + (slots.Count - 1) + "\u0002"; };

            s = Regex.Replace(s, @"<pre[^>]*>(?:\s*<code[^>]*>)?(.*?)(?:</code>\s*)?</pre>", m =>
                hold("\n\n```\n" + WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")).TrimEnd() + "\n```\n\n"), RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+", " ");
            s = Regex.Replace(s, @"<table[^>]*>(.*?)</table>", m => hold(TableToMd(m.Groups[1].Value)), RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<h([1-6])[^>]*>(.*?)</h\1>", m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + Strip(m.Groups[2].Value).Trim() + "\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<(strong|b)\b[^>]*>(.*?)</\1>", m => "**" + m.Groups[2].Value.Trim() + "**", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<(em|i)\b[^>]*>(.*?)</\1>", m => "*" + m.Groups[2].Value.Trim() + "*", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<(del|s|strike)\b[^>]*>(.*?)</\1>", "~~$2~~", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<code[^>]*>(.*?)</code>", m => "`" + WebUtility.HtmlDecode(Strip(m.Groups[1].Value)) + "`", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<img\b[^>]*>", m =>
            {
                string src = AttrOf(m.Value, "src"), alt = AttrOf(m.Value, "alt");
                return src.Length > 0 ? "![" + alt + "](" + src + ")" : "";
            }, RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<a\b[^>]*>(.*?)</a>", m =>
            {
                string href = AttrOf(m.Value, "href");
                string text = m.Groups[1].Value.Trim();
                return href.Length > 0 && !href.StartsWith("#") ? "[" + text + "](" + href + ")" : text;
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<blockquote[^>]*>(.*?)</blockquote>", m =>
            {
                string inner = FromHtmlFragment(m.Groups[1].Value);
                return "\n\n> " + inner.Trim().Replace("\n", "\n> ") + "\n\n";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<ol[^>]*>(.*?)</ol>", m =>
            {
                int n = 0;
                return "\n\n" + Regex.Replace(m.Groups[1].Value, @"<li[^>]*>(.*?)(</li>|(?=<li)|$)", li => (++n) + ". " + Strip(li.Groups[1].Value).Trim() + "\n", RegexOptions.Singleline | RegexOptions.IgnoreCase) + "\n";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"<li[^>]*>(.*?)(</li>|(?=<li)|(?=</ul))", m => "\n- " + Strip(m.Groups[1].Value).Trim(), RegexOptions.IgnoreCase | RegexOptions.Singleline);
            s = Regex.Replace(s, @"</?(ul|ol)[^>]*>", "\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<br\s*/?>", "  \n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<hr[^>]*>", "\n\n---\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</?(p|div|section|article|header|footer|main|nav|aside|figure|figcaption|dl|dt|dd|tr)[^>]*>", "\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            for (int k = 0; k < 4 && s.IndexOf('\u0001') >= 0; k++)
                s = Regex.Replace(s, "\u0001(\\d+)\u0002", m => slots[int.Parse(m.Groups[1].Value)]);
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n[ \t]+(?=\S)", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim() + "\n";
        }

        static string FromHtmlFragment(string s) { return FromHtml("<body>" + s + "</body>"); }

        static string TableToMd(string inner)
        {
            List<List<string>> rows = new List<List<string>>();
            foreach (Match tr in Regex.Matches(inner, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                List<string> cells = new List<string>();
                foreach (Match td in Regex.Matches(tr.Groups[1].Value, @"<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                    cells.Add(WebUtility.HtmlDecode(Strip(td.Groups[1].Value)).Trim().Replace("|", "\\|"));
                if (cells.Count > 0) rows.Add(cells);
            }
            if (rows.Count == 0) return "";
            int cols = 0; foreach (List<string> r in rows) cols = Math.Max(cols, r.Count);
            StringBuilder sb = new StringBuilder("\n\n");
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append("|");
                for (int c = 0; c < cols; c++) sb.Append(' ').Append(c < rows[r].Count ? rows[r][c] : "").Append(" |");
                sb.Append('\n');
                if (r == 0) { sb.Append("|"); for (int c = 0; c < cols; c++) sb.Append(" --- |"); sb.Append('\n'); }
            }
            return sb.Append('\n').ToString();
        }

        static string Strip(string s) { return Regex.Replace(s, "<[^>]+>", ""); }

        static string AttrOf(string tag, string name)
        {
            Match m = Regex.Match(tag, name + @"\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            return WebUtility.HtmlDecode(m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[3].Success && m.Groups[3].Length > 0 ? m.Groups[3].Value : m.Groups[4].Value);
        }

        public static string HtmlToText(string html)
        {
            string s = html;
            Match body = Regex.Match(s, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (body.Success) s = body.Groups[1].Value;
            s = Regex.Replace(s, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            List<string> pres = new List<string>();
            s = Regex.Replace(s, @"<pre[^>]*>.*?</pre>", m => { pres.Add(m.Value); return "\u0003" + (pres.Count - 1) + "\u0004"; }, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s*\n\s*", " ");
            s = Regex.Replace(s, "\u0003(\\d+)\u0004", m => "\n" + pres[int.Parse(m.Groups[1].Value)] + "\n");
            s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<li[^>]*>\s*(<input[^>]*checked[^>]*>)", "\n☑ ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<li[^>]*>\s*(<input[^>]*>)", "\n☐ ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</tr>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</(p|div|h\d|table|ul|ol|blockquote|pre|section|article)>", "\n\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"</t[dh]>", "\t", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n[ \t]+", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim() + "\n";
        }

        public static string ToPlainText(string md) { return HtmlToText(ToHtml(md)); }

        public static string TextToHtmlDocument(string text, string title)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string para in Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n"))
                if (para.Trim().Length > 0) sb.Append("<p>").Append(Esc(para.Trim()).Replace("\n", "<br>\n")).Append("</p>\n");
            return "<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>" + Esc(title) + "</title>\n<style>\n" + Css + "\n</style>\n</head>\n<body>\n<article>\n" + sb + "</article>\n</body>\n</html>\n";
        }
    }
}
