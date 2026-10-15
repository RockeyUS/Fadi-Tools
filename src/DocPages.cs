using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FileConverter
{
    /// Turns a PDF or an Office document into one picture per page, so the Studio can show and edit it.
    /// PDFs use the PDF engine built into Windows 10/11; Word/PowerPoint files go through LibreOffice first.
    public static class DocPages
    {
        static readonly string[] OfficeExt = { "doc", "docx", "odt", "rtf", "ppt", "pptx", "odp", "pages", "wpd" };
        static bool IsWindows { get { return Environment.OSVersion.Platform == PlatformID.Win32NT; } }

        public static bool IsDoc(string path)
        {
            string e = Formats.Ext(path);
            return e == "pdf" || Array.IndexOf(OfficeExt, e) >= 0;
        }

        /// Pictures of every page (PNG files in a temp folder). progress gets (page done, page count).
        public static List<string> Render(string file, Action<int, int> progress, Func<bool> cancelled)
        {
            string dir = Path.Combine(Path.GetTempPath(), "FadiPages_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(dir);
            string pdf = file;
            if (Formats.Ext(file) != "pdf")
            {
                if (Tools.Soffice == null) throw new UserError(L.T("doc_need_lo"));
                Job j = Engine.Build(file, "pdf", dir, Quality.Best);
                string err = Engine.Run(j, null, cancelled);
                if (err != null) throw new UserError(err);
                pdf = j.Output;
            }
            List<string> pages = IsWindows ? WindowsPdf(pdf, dir, progress, cancelled) : Poppler(pdf, dir);
            if (pages.Count == 0) throw new UserError(L.T("doc_failed"));
            // friendly names: "Report - page 3.png"
            string stem = Path.GetFileNameWithoutExtension(file);
            foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
            List<string> named = new List<string>();
            for (int i = 0; i < pages.Count; i++)
            {
                string n = Path.Combine(dir, stem + " - " + L.T("doc_page", i + 1) + ".png");
                try { File.Move(pages[i], n); named.Add(n); } catch { named.Add(pages[i]); }
            }
            return named;
        }

        const string Script = @"param([string]$In, [string]$Out, [int]$MaxPx = 2400)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$ext = [System.WindowsRuntimeSystemExtensions].GetMethods()
$asTaskOp = $ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asTaskAct = $ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await($op, [Type]$type) { $t = $asTaskOp.MakeGenericMethod($type).Invoke($null, @($op)); $null = $t.Wait(-1); $t.Result }
function AwaitAction($op) { $t = $asTaskAct.Invoke($null, @($op)); $null = $t.Wait(-1) }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($In)) ([Windows.Storage.StorageFile])
$doc = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
[Console]::Out.WriteLine('PAGES ' + $doc.PageCount)
for ($i = 0; $i -lt $doc.PageCount; $i++) {
  $page = $doc.GetPage([uint32]$i)
  $w = $page.Size.Width; $h = $page.Size.Height
  $k = [Math]::Min(2.0, $MaxPx / [Math]::Max($w, $h))
  $opt = New-Object Windows.Data.Pdf.PdfPageRenderOptions
  $opt.DestinationWidth = [uint32][Math]::Round($w * $k)
  $opt.DestinationHeight = [uint32][Math]::Round($h * $k)
  $ms = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
  AwaitAction ($page.RenderToStreamAsync($ms, $opt))
  $path = Join-Path $Out ('page{0:D4}.png' -f ($i + 1))
  $src = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ms.GetInputStreamAt(0))
  $fs = [System.IO.File]::Create($path)
  $src.CopyTo($fs)
  $fs.Close(); $src.Dispose(); $ms.Dispose(); $page.Dispose()
  [Console]::Out.WriteLine('PAGE ' + ($i + 1))
}
";

        /// Renders a PDF's pages into dir as page0001.png, page0002.png... (longest side about maxPx).
        /// progress gets (page done, page count) as each picture is written.
        public static List<string> RenderTo(string pdf, string dir, int maxPx, Action<int, int> progress, Func<bool> cancelled)
        {
            Directory.CreateDirectory(dir);
            return IsWindows ? WindowsPdf(pdf, dir, progress, cancelled, maxPx) : Poppler(pdf, dir, maxPx, progress);
        }

        static List<string> WindowsPdf(string pdf, string dir, Action<int, int> progress, Func<bool> cancelled, int maxPx = 2400)
        {
            string ps1 = Path.Combine(dir, "render.ps1");
            File.WriteAllText(ps1, Script, new UTF8Encoding(true));
            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            int total = 0;
            StringBuilder err = new StringBuilder();
            int code = Tools.Stream(ps, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Tools.Quote(ps1) + " -In " + Tools.Quote(pdf) + " -Out " + Tools.Quote(dir) + " -MaxPx " + maxPx,
                line =>
                {
                    Match m = Regex.Match(line ?? "", "^PAGES (\\d+)");
                    if (m.Success) total = int.Parse(m.Groups[1].Value);
                    m = Regex.Match(line ?? "", "^PAGE (\\d+)");
                    if (m.Success && progress != null) progress(int.Parse(m.Groups[1].Value), total);
                },
                line => { if (err.Length < 4000) err.AppendLine(line); }, cancelled);
            try { File.Delete(ps1); } catch { }
            List<string> pages = new List<string>(Directory.GetFiles(dir, "page*.png"));
            pages.Sort(StringComparer.OrdinalIgnoreCase);
            if (pages.Count == 0 && code != -2)
            {
                // older Windows without the PDF engine: LibreOffice can still show the first page
                if (Tools.Soffice != null)
                {
                    Job j = Engine.Build(pdf, "png", dir, Quality.Best);
                    if (Engine.Run(j, null, cancelled) == null && File.Exists(j.Output)) pages.Add(j.Output);
                }
                if (pages.Count == 0) throw new UserError(L.T("doc_failed") + " " + Tools.LastLines(err.ToString(), 2));
            }
            return pages;
        }

        /// (for testing outside Windows)
        static List<string> Poppler(string pdf, string dir, int maxPx = 0, Action<int, int> progress = null)
        {
            string tool = Tools.FindOnPath("pdftoppm");
            List<string> pages = new List<string>();
            if (tool == null) return pages;
            string o;
            Tools.RunHidden(tool, "-png " + (maxPx > 0 && maxPx < 2000 ? "-scale-to " + maxPx : "-r 110") + " " + Tools.Quote(pdf) + " " + Tools.Quote(Path.Combine(dir, "pp")), 300000, out o);
            List<string> raw = new List<string>(Directory.GetFiles(dir, "pp-*.png"));
            raw.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
            for (int i = 0; i < raw.Count; i++)
            {
                string n = Path.Combine(dir, "page" + (i + 1).ToString("D4") + ".png");
                try { File.Move(raw[i], n); } catch { n = raw[i]; }
                pages.Add(n);
                if (progress != null) progress(i + 1, raw.Count);
            }
            return pages;
        }
    }
}
