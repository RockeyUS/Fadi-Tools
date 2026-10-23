using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// Several pictures (or PDFs) dropped on the Donut Wheel's "Combine into PDF" / "Merge PDFs": one PDF of them all,
    /// in the order they were picked, saved next to the first one. It runs in the background and shows in Activity.
    public static class QuickPdf
    {
        public static void CombineInBackground(string[] files, Control activitySource)
        {
            if (files == null || files.Length == 0) return;
            bool anyPdf = false;
            foreach (string f in files) if (Formats.Ext(f) == "pdf") anyPdf = true;
            // PDFs are joined with qpdf: it's downloaded the first time (asked first, like on the PDF page)
            if (anyPdf && !Qpdf.Ready && !Ask.Confirm(null, L.T("pdf_need_title"), L.T("pdf_need_q"), L.T("install"), L.T("not_now"), AskKind.Info)) return;
            string first = files[0];
            string outp = Paths.UniquePath(DragWheel.OutDirFor(first), Path.GetFileNameWithoutExtension(first) + " " + L.T("pdf_combined_suffix"), "pdf");
            bool cancel = false;
            ActJob act = Activity.Begin(activitySource, L.T("act_k_pdf_combine"), Path.GetFileName(outp), () => cancel = true);
            Thread t = new Thread(() =>
            {
                string err = null;
                try { Combine(files, outp, v => Activity.Update(act, v, (int)(v * 100) + "%"), () => cancel); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { err = ex.Message; }
                if (cancel || err != null) try { if (File.Exists(outp)) File.Delete(outp); } catch { }
                int pages = Qpdf.Ready && err == null && !cancel ? Qpdf.Pages(outp) : -1;
                Activity.End(act, err, cancel, L.T("pdf_done_combine", Path.GetFileName(outp)) + (pages > 0 ? "  ·  " + L.T("pdf_n_pages", pages) : ""), outp);
                if (err == null && !cancel && Settings.OpenAfter) ActivityRows.ShowOutput(outp);
            });
            t.IsBackground = true; t.Start();
        }

        /// Pictures become pages (each page the picture's own shape); PDFs keep their pages.
        public static void Combine(IList<string> files, string outp, Action<double> progress, Func<bool> cancelled)
        {
            bool anyPdf = false;
            foreach (string f in files) if (Formats.Ext(f) == "pdf") anyPdf = true;
            string tmpDir = Path.Combine(Path.GetTempPath(), "FadiCombine_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            Directory.CreateDirectory(tmpDir);
            try
            {
                if (anyPdf && !Qpdf.Ready) Qpdf.Install((got, total) => { if (progress != null && total > 0) progress(0.2 * got / total); }, cancelled);
                if (anyPdf && !Qpdf.Ready) throw new UserError(L.T("pdf_need_qpdf"));
                List<string> parts = new List<string>();
                int done = 0;
                for (int i = 0; i < files.Count; )
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    if (Formats.Ext(files[i]) == "pdf") { parts.Add(files[i]); i++; done++; continue; }
                    int j = i; while (j < files.Count && Formats.Ext(files[j]) != "pdf") j++;
                    List<string> run = new List<string>();
                    for (int k = i; k < j; k++) run.Add(files[k]);
                    string part = anyPdf ? Path.Combine(tmpDir, "photos" + parts.Count + ".pdf") : outp + ".tmp";
                    int baseDone = done;
                    ImagePdf.Write(run.Count, k => { using (Image im = ViewerPage.Decode(run[k], 8000)) return new Bitmap(im); }, true, part, 90, ImagePdf.PageSize.Photo,
                        (n, total) => { if (progress != null) progress((baseDone + n) / (double)files.Count * (anyPdf ? 0.8 : 1)); });
                    parts.Add(part); done += run.Count; i = j;
                }
                if (anyPdf)
                {
                    StringBuilder a = new StringBuilder("--empty --pages");
                    foreach (string p in parts) a.Append(' ').Append(Tools.Quote(p));
                    a.Append(" -- ").Append(Tools.Quote(outp + ".tmp"));
                    string e = Qpdf.Run(a.ToString());
                    if (e != null) throw new UserError(e);
                }
                if (File.Exists(outp)) File.Delete(outp);
                File.Move(outp + ".tmp", outp);
                if (progress != null) progress(1);
            }
            finally
            {
                try { if (File.Exists(outp + ".tmp")) File.Delete(outp + ".tmp"); } catch { }
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }
    }
}
