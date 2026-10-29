using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FileConverter
{
    /// The File Inspector's deeper checks: is it safe, what's hidden in it, what's inside it, what the program is,
    /// searching the bytes, comparing, the report, and whole folders.
    public partial class InspectPage
    {
        List<string> keys = new List<string>();
        SafetyPill pill;
        SafetyView safetyView; Btn btnUnblock, btnLookup, btnHiddenOut;
        DetailsView hiddenView; Btn btnClean, btnMap;
        EntriesView entries; Btn btnExtractAll;
        DetailsView progView; Btn btnSaveIcon;
        TextField hexFind, hexGo; Btn btnFind; EntropyStrip strip;
        Btn btnMore, btnBack;
        DetailsView wrongView; Btn btnFixAll;

        SafetyReport safety; PrivacyReport privacy; List<ArchiveEntry> inside; PeFacts pe;
        List<string[]> mediaRows, extraRows; Bitmap progIcon; bool fontSample;
        string folder; FolderReport folderRep; int scanned = -1;
        readonly Stack<string[]> back = new Stack<string[]>();   // the archive we came from: path, its name, the label shown
        string nestedLabel;
        Rectangle progIconR, sumR;
        static string TempRoot { get { return Path.Combine(Path.GetTempPath(), "fadi_inspect"); } }

        string Key { get { int i = tabs.SelectedIndex; return i >= 0 && i < keys.Count ? keys[i] : (folder != null ? "f_info" : "info"); } }

        void BuildExtras()
        {
            pill = new SafetyPill { Visible = false };
            pill.Click += (s, e) => SelectKey("safety");
            safetyView = new SafetyView();
            btnUnblock = new Btn(() => L.T("sf_unblock"), Ico.Check, BtnKind.Secondary);
            btnUnblock.Click += (s, e) => Unblock();
            btnLookup = new Btn(() => L.T("sf_lookup"), Ico.Globe, BtnKind.Secondary);
            btnLookup.Click += (s, e) => LookUp();
            btnHiddenOut = new Btn(() => L.T("sf_extract_hidden"), Ico.Save, BtnKind.Secondary);
            btnHiddenOut.Click += (s, e) => ExtractHidden();

            hiddenView = new DetailsView { EmptyText = () => L.T("pv_none") };
            btnClean = new Btn(() => L.T("pv_clean"), Ico.Delete, BtnKind.Primary);
            btnClean.Click += (s, e) => Clean();
            btnMap = new Btn(() => L.T("pv_map"), Ico.Globe, BtnKind.Secondary);
            btnMap.Click += (s, e) => OpenMap();

            entries = new EntriesView();
            entries.Open += OpenEntry;
            entries.Save += SaveEntry;
            btnExtractAll = new Btn(() => L.T("in_extract_all"), Ico.FolderOpen, BtnKind.Secondary);
            btnExtractAll.Click += (s, e) => ExtractAll();

            progView = new DetailsView();
            progView.Copied += lbl => ShowToast(L.T("copied", lbl), true, null, null);
            btnSaveIcon = new Btn(() => L.T("pg_save_icon"), Ico.Save, BtnKind.Secondary);
            btnSaveIcon.Click += (s, e) => SaveIcon();

            hexFind = new TextField(() => L.T("hx_find_ph"), Ico.Search) { Surface = Surface.Card };
            hexFind.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindNext(); } };
            btnFind = new Btn(() => L.T("hx_find"), null, BtnKind.Secondary);
            btnFind.Click += (s, e) => FindNext();
            hexGo = new TextField(() => L.T("hx_go_ph"), null) { ForceLtr = true, Surface = Surface.Card };
            hexGo.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; GoToOffset(); } };
            strip = new EntropyStrip();
            strip.Jump += off => { hex.GoTo(off, 1); strip.Mark = off; strip.Invalidate(); };

            btnMore = new Btn(() => "", Ico.More, BtnKind.Ghost) { AccessibleName = L.T("in_more") };
            btnMore.Click += (s, e) => ShowMore();
            btnBack = new Btn(() => back.Count > 0 ? L.T("in_back", back.Peek()[1]) : "", Ico.Back, BtnKind.Secondary);
            btnBack.Click += (s, e) => GoBack();

            wrongView = new DetailsView { EmptyText = () => L.T(scanned >= 0 ? "fd_scanning" : "fd_no_wrong") };
            btnFixAll = new Btn(() => L.T("fd_fix_all"), Ico.Check, BtnKind.Primary);
            btnFixAll.Click += (s, e) => FixAll();

            Controls.AddRange(new Control[] { pill, safetyView, btnUnblock, btnLookup, btnHiddenOut, hiddenView, btnClean, btnMap, entries, btnExtractAll,
                progView, btnSaveIcon, hexFind, btnFind, hexGo, strip, btnMore, btnBack, wrongView, btnFixAll });
            EnableDrop(safetyView, hiddenView, entries, progView, wrongView);
        }

        // ------------------------------------------------------------------ tabs
        void SetTabs()
        {
            string was = keys.Count > 0 ? Key : null;
            keys = new List<string>();
            if (folder != null) { keys.Add("f_info"); keys.Add("f_wrong"); }
            else
            {
                keys.Add("info");
                keys.Add("safety");
                if (privacy != null && (privacy.Personal || privacy.CanClean)) keys.Add("hidden");
                if (inside != null) keys.Add("inside");
                if (pe != null) keys.Add("program");
                keys.Add("preview"); keys.Add("hex"); keys.Add("text"); keys.Add("strings");
            }
            List<Func<string>> items = new List<Func<string>>();
            foreach (string k in keys) { string kk = k == "f_info" ? "tab_info" : k == "f_wrong" ? "tab_wrong" : "tab_" + k; items.Add(() => L.T(kk)); }
            tabs.Items = items.ToArray();
            int at = was == null ? -1 : keys.IndexOf(was);
            tabs.SetSilently(at >= 0 ? at : 0);
            tabs.Invalidate();
        }

        int TabsWidth()
        {
            int w = 0;
            Font f = Theme.F(tabs.FontSize);
            foreach (Func<string> it in tabs.Items) w += Math.Max(Theme.S(92), TextRenderer.MeasureText(it(), f).Width + Theme.S(34));
            return Math.Max(Theme.S(200), w);
        }

        void SelectKey(string k)
        {
            int i = keys.IndexOf(k);
            if (i < 0) return;
            tabs.SelectedIndex = i;
            ShowTab();
        }

        void ShowExtrasState()
        {
            bool f = file != null;
            pill.Visible = f;
            pill.Level = safety == null ? -1 : safety.Level;
            btnMore.Visible = f;
            btnBack.Visible = f && back.Count > 0;
        }

        void ShowExtrasTab(string k)
        {
            bool f = file != null;
            safetyView.Visible = f && k == "safety" && safety != null;
            btnLookup.Visible = f && k == "safety";
            btnUnblock.Visible = f && k == "safety" && safety != null && safety.FromInternet;
            btnHiddenOut.Visible = f && k == "safety" && safety != null && safety.HiddenAt > 0;
            hiddenView.Visible = f && k == "hidden";
            btnClean.Visible = f && k == "hidden" && privacy != null && privacy.CanClean && privacy.Personal;
            btnMap.Visible = f && k == "hidden" && privacy != null && !double.IsNaN(privacy.Lat);
            entries.Visible = f && k == "inside";
            btnExtractAll.Visible = f && k == "inside" && inside != null && inside.Count > 0;
            progView.Visible = f && k == "program";
            btnSaveIcon.Visible = f && k == "program" && progIcon != null;
            hexFind.Visible = btnFind.Visible = hexGo.Visible = strip.Visible = f && k == "hex";
            wrongView.Visible = folder != null && k == "f_wrong";
            btnFixAll.Visible = folder != null && k == "f_wrong" && folderRep != null && folderRep.Wrong.Count > 0;
            Invalidate();
        }

        void LayoutExtras(Rectangle content)
        {
            int ip = Theme.S(14);
            int barH = Theme.S(56);
            Rectangle list = new Rectangle(content.X, content.Y, content.Width, content.Height - barH);
            int by = content.Bottom - Theme.S(46);
            sumR = Rectangle.Empty; progIconR = Rectangle.Empty;
            string k = Key;

            // safety
            Place(safetyView, list);
            int x = content.X + ip;
            foreach (Btn b in new[] { btnLookup, btnUnblock, btnHiddenOut })
            {
                if (!b.Visible) continue;
                int w = Math.Max(Theme.S(140), b.PreferredWidth);
                Place(b, new Rectangle(x, by, w, Theme.S(36))); x += w + Theme.S(10);
            }

            // hidden details
            if (k == "hidden" || k == "inside") sumR = new Rectangle(content.X + ip + Theme.S(2), content.Y, content.Width - ip * 2, Theme.S(40));
            Place(hiddenView, new Rectangle(list.X, list.Y + Theme.S(44), list.Width, list.Height - Theme.S(44)));
            x = content.X + ip;
            foreach (Btn b in new[] { btnClean, btnMap })
            {
                if (!b.Visible) continue;
                int w = Math.Max(Theme.S(150), b.PreferredWidth);
                Place(b, new Rectangle(x, by, w, Theme.S(36))); x += w + Theme.S(10);
            }

            // inside
            Place(entries, new Rectangle(list.X, list.Y + Theme.S(44), list.Width, list.Height - Theme.S(44)));
            Place(btnExtractAll, new Rectangle(content.X + ip, by, Math.Max(Theme.S(150), btnExtractAll.PreferredWidth), Theme.S(36)));

            // program
            int top = progIcon != null ? Theme.S(70) : 0;
            if (k == "program" && progIcon != null) progIconR = new Rectangle(content.X + ip + Theme.S(2), content.Y + Theme.S(6), Theme.S(52), Theme.S(52));
            Place(btnSaveIcon, new Rectangle(content.X + ip + Theme.S(70), content.Y + Theme.S(14), Math.Max(Theme.S(130), btnSaveIcon.PreferredWidth), Theme.S(36)));
            Place(progView, new Rectangle(content.X, content.Y + top, content.Width, content.Height - top));

            // hex: search, go to, the map of the file
            int fw = Math.Min(Theme.S(320), content.Width / 3), gw = Math.Min(Theme.S(200), content.Width / 4), fbw = Math.Max(Theme.S(90), btnFind.PreferredWidth);
            Place(hexFind, new Rectangle(content.X + ip, content.Y, fw, Theme.S(36)));
            Place(btnFind, new Rectangle(content.X + ip + fw + Theme.S(8), content.Y, fbw, Theme.S(36)));
            Place(hexGo, new Rectangle(content.X + ip + fw + fbw + Theme.S(24), content.Y, gw, Theme.S(36)));
            Place(strip, new Rectangle(content.X + ip, content.Y + Theme.S(44), content.Width - ip * 2, Theme.S(22)));
            if (file != null) Place(hex, new Rectangle(content.X, content.Y + Theme.S(74), content.Width, content.Height - Theme.S(74)));

            // folder
            Place(wrongView, folderRep != null && folderRep.Wrong.Count > 0 ? list : content);
            Place(btnFixAll, new Rectangle(content.X + ip, by, Math.Max(Theme.S(150), btnFixAll.PreferredWidth), Theme.S(36)));
        }

        void PaintExtras(Graphics g)
        {
            string k = Key;
            if (k == "hidden" && file != null && !sumR.IsEmpty)
            {
                string t = privacy != null && privacy.Personal ? L.T("pv_intro") : L.T("pv_none_long");
                Label(g, t, Theme.F(9f), Theme.InkSoft, sumR, Align.Start, true, true);
            }
            if (k == "inside" && file != null && !sumR.IsEmpty && inside != null)
            {
                int n = 0; long size = 0, packed = 0; int risky = 0;
                foreach (ArchiveEntry e in inside) if (!e.IsDir) { n++; size += e.Size; packed += e.Packed; if (Safety.RunsCode(Formats.Ext(e.Path))) risky++; }
                string t = L.T("in_summary", n, Theme.HumanSize(size));
                if (risky > 0) t += "  ·  " + L.T("in_programs", risky);
                Label(g, t, Theme.F(9.25f, FontStyle.Bold), risky > 0 ? Theme.Warn : Theme.InkSoft, sumR);
            }
            if (k == "program" && progIcon != null && !progIconR.IsEmpty)
            {
                Rectangle r = M(progIconR);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(progIcon, r);
            }
        }

        void ResetExtras()
        {
            safety = null; privacy = null; inside = null; pe = null; mediaRows = null; extraRows = null; fontSample = false;
            if (progIcon != null) { progIcon.Dispose(); progIcon = null; }
            safetyView.Report = null; safetyView.ScrollTo(0); safetyView.Invalidate();
            hiddenView.Rows.Clear(); entries.Items = new List<ArchiveEntry>(); entries.ScrollTo(0); progView.Rows.Clear();
            strip.Map = null; strip.Mark = -1; hexFind.Box.Text = ""; hexGo.Box.Text = "";
            wrongView.Rows.Clear(); scanned = -1;
            SetTabs();
        }

        // ------------------------------------------------------------------ looking deeper (in the background)
        void Analyze(int tk)
        {
            string path = file; Sniff s = sniff;
            Thread t = new Thread(() =>
            {
                SafetyReport sr = null;
                try { sr = Safety.Check(path, s); } catch { }
                Ui(() =>
                {
                    if (tk != token) return;
                    safety = sr ?? new SafetyReport();
                    safetyView.Report = safety; safetyView.Invalidate();
                    ShowState();
                });

                PrivacyReport pr = null; List<ArchiveEntry> ins = null; PeFacts pf = null; List<string[]> prog = null; Bitmap icon = null;
                List<string[]> md = null, extra = new List<string[]>(); Bitmap font = null;
                string ext = Formats.Ext(path);
                try { pr = Privacy.Read(path, s); } catch { }
                if (tk != token) return;
                try { if (Inside.CanList(s.Ext) || Inside.CanList(ext)) ins = Inside.List(path); } catch { }
                try
                {
                    if (Rea.IsPe(path))
                    {
                        pf = Pe.Read(path);
                        if (pf != null) { prog = ProgramRows(path, pf); icon = Pe.Icon(path); }
                    }
                }
                catch { }
                if (tk != token) return;
                try
                {
                    if (s.Category == Category.Video || s.Category == Category.Audio) md = MediaDetails.Read(path);
                    else if (s.Category == Category.Image) extra.AddRange(MediaDetails.Picture(path));
                    if (ext == "cer" || ext == "crt" || ext == "der" || ext == "pem" || s.Ext == "cer") extra.AddRange(Special.Certificate(path));
                    if (ext == "lnk")
                    {
                        ShortcutInfo si = Special.Shortcut(path);
                        if (si != null)
                        {
                            if (!string.IsNullOrEmpty(si.Target)) extra.Add(new[] { L.T("lnk_target"), si.Target, "1" });
                            if (!string.IsNullOrEmpty(si.Arguments)) extra.Add(new[] { L.T("lnk_args"), si.Arguments, "1" });
                            if (!string.IsNullOrEmpty(si.Folder)) extra.Add(new[] { L.T("lnk_folder"), si.Folder });
                            if (!string.IsNullOrEmpty(si.Description)) extra.Add(new[] { L.T("lnk_desc"), si.Description });
                        }
                    }
                    if (ext == "url") { string u = Special.WebLink(path); if (u != null) extra.Add(new[] { L.T("lnk_web"), u, "1" }); }
                    if (ext == "ttf" || ext == "otf" || s.Ext == "ttf" || s.Ext == "otf")
                    {
                        string fam; font = Special.FontSample(path, 1100, out fam);
                        if (fam != null) extra.Add(new[] { L.T("font_name"), fam });
                    }
                }
                catch { }
                float[] map = null;
                try { map = EntropyMap.Of(path, 320); } catch { }
                Ui(() =>
                {
                    if (tk != token) { if (icon != null) icon.Dispose(); if (font != null) font.Dispose(); return; }
                    privacy = pr; inside = ins; pe = pf; mediaRows = md; extraRows = extra.Count > 0 ? extra : null;
                    hiddenView.Rows.Clear(); if (pr != null) hiddenView.Rows.AddRange(pr.Rows); hiddenView.Invalidate();
                    entries.Items = ins ?? new List<ArchiveEntry>(); entries.Invalidate();
                    progView.Rows.Clear(); if (prog != null) progView.Rows.AddRange(prog); progView.Invalidate();
                    if (progIcon != null) progIcon.Dispose(); progIcon = icon;
                    if (font != null) { if (preview.Frame != null) preview.Frame.Dispose(); preview.Frame = font; fontSample = true; preview.Invalidate(); }
                    strip.Map = map; strip.Length = new FileInfo(path).Length; strip.Invalidate();
                    BuildDetails();
                    SetTabs();
                    ShowState();
                });
            });
            t.IsBackground = true; t.Start();
        }

        static List<string[]> ProgramRows(string path, PeFacts pf)
        {
            List<string[]> r = new List<string[]>();
            try { foreach (KeyValuePair<string, string> kv in PeInfo.Version(path)) r.Add(new[] { kv.Key, kv.Value }); } catch { }
            r.Add(new[] { L.T("pg_kind"), (pf.IsDll ? L.T("pg_dll") : pf.IsDriver ? L.T("pg_driver") : L.T("pg_exe")) + "  ·  " + (pf.Is64 ? "64-bit" : "32-bit") + "  ·  " + pf.Machine });
            r.Add(new[] { L.T("pg_made"), pf.IsNet ? L.T("pg_net") : L.T("pg_native") });
            if (pf.Subsystem.Length > 0) r.Add(new[] { L.T("pg_window"), pf.Subsystem });
            if (pf.BuiltReal) r.Add(new[] { L.T("pg_built"), pf.Built.ToLocalTime().ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture) });
            long extraBytes = pf.Overlay > 0 ? new FileInfo(path).Length - pf.Overlay : 0;
            if (extraBytes > 0) r.Add(new[] { L.T("pg_overlay"), Theme.HumanSize(extraBytes) });
            List<KeyValuePair<int, string>> ab = Pe.Abilities(pf);
            if (ab.Count > 0)
            {
                List<string> names = new List<string>();
                foreach (KeyValuePair<int, string> a in ab) names.Add(L.T(a.Value));
                r.Add(new[] { L.T("pg_can"), string.Join("  ·  ", names.ToArray()) });
            }
            if (pf.Dlls.Count > 0) r.Add(new[] { L.T("pg_uses"), string.Join(", ", pf.Dlls.ToArray()) });
            return r;
        }

        // ------------------------------------------------------------------ safety actions
        void Unblock()
        {
            if (file == null) return;
            if (!Ask.Confirm(FindForm(), L.T("sf_unblock"), L.T("sf_unblock_q"), L.T("sf_unblock"), L.T("cancel"), AskKind.Warning)) return;
            if (Zone.Unblock(file))
            {
                ShowToast(L.T("sf_unblocked"), true, null, null);
                Analyze(token);
            }
            else ShowToast(L.T("sf_unblock_fail"), false, null, null);
        }

        void LookUp()
        {
            if (file == null) return;
            if (hashes == null) { ShowToast(L.T("computing"), false, null, null); return; }
            // only the file's fingerprint leaves the computer, never the file itself
            try { Process.Start(new ProcessStartInfo("https://www.virustotal.com/gui/file/" + hashes[2].ToLowerInvariant()) { UseShellExecute = true }); } catch { }
        }

        void ExtractHidden()
        {
            if (file == null || safety == null || safety.HiddenAt <= 0) return;
            try
            {
                string outp = Hidden.Extract(file, safety.HiddenAt, safety.HiddenType);
                ShowSaved(outp);
            }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        void ShowSaved(string path)
        {
            ShowToast(L.T("saved_copy", Path.GetFileName(path)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { } });
        }

        // ------------------------------------------------------------------ hidden details
        void Clean()
        {
            if (file == null) return;
            string f = file; Sniff s = sniff;
            btnClean.Enabled = false;
            Thread t = new Thread(() =>
            {
                string outp = null, err = null;
                try { outp = Privacy.Clean(f, s); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    btnClean.Enabled = true;
                    if (outp == null) { ShowToast(err ?? L.T("pv_clean_failed"), false, null, null); return; }
                    ShowToast(L.T("pv_cleaned", Path.GetFileName(outp)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "/select,\"" + outp + "\""); } catch { } });
                });
            }) { IsBackground = true };
            t.Start();
        }

        void OpenMap()
        {
            if (privacy == null || double.IsNaN(privacy.Lat)) return;
            string la = privacy.Lat.ToString("0.######", CultureInfo.InvariantCulture), lo = privacy.Lon.ToString("0.######", CultureInfo.InvariantCulture);
            try { Process.Start(new ProcessStartInfo("https://www.openstreetmap.org/?mlat=" + la + "&mlon=" + lo + "#map=16/" + la + "/" + lo) { UseShellExecute = true }); } catch { }
        }

        // ------------------------------------------------------------------ inside archives
        void OpenEntry(ArchiveEntry e)
        {
            if (file == null) return;
            string arch = file, label = nestedLabel ?? Path.GetFileName(file);
            Thread t = new Thread(() =>
            {
                string outp = null, err = null;
                try
                {
                    string dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N").Substring(0, 10));
                    outp = Inside.Extract(arch, e, dir);
                }
                catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    if (outp == null) { ShowToast(err ?? L.T("in_not_found"), false, null, null); return; }
                    back.Push(new[] { arch, Path.GetFileName(arch), nestedLabel });
                    nestedLabel = label + "  ›  " + Path.GetFileName(e.Path.TrimEnd('/'));
                    Inspect(outp);
                });
            }) { IsBackground = true };
            t.Start();
        }

        void SaveEntry(ArchiveEntry e)
        {
            if (file == null) return;
            try { ShowSaved(Inside.Extract(file, e, DragWheel.OutDirFor(back.Count > 0 ? back.ToArray()[back.Count - 1][0] : file))); }
            catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
        }

        void ExtractAll()
        {
            if (file == null || inside == null) return;
            string f = back.Count > 0 ? null : file;
            if (f == null) { ShowToast(L.T("in_nested_all"), false, null, null); return; }
            List<ArchiveEntry> list = inside; int tk = token;
            btnExtractAll.Enabled = false;
            Thread t = new Thread(() =>
            {
                string dir = null, err = null;
                try { dir = Inside.ExtractAll(f, list, () => tk != token); } catch (Exception ex) { err = ex.Message; }
                Ui(() =>
                {
                    btnExtractAll.Enabled = true;
                    if (dir == null) { ShowToast(err ?? L.T("in_not_found"), false, null, null); return; }
                    ShowToast(L.T("in_extracted", Path.GetFileName(dir)), true, () => L.T("show_file"), () => { try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch { } });
                });
            }) { IsBackground = true };
            t.Start();
        }

        void GoBack()
        {
            if (back.Count == 0) return;
            string[] b = back.Pop();
            nestedLabel = b[2];
            if (back.Count == 0) CleanTemp();
            Inspect(b[0]);
        }

        void DropBack() { if (back.Count > 0) { back.Clear(); CleanTemp(); } nestedLabel = null; }

        void CleanTemp()
        {
            string root = TempRoot;
            ThreadPool.QueueUserWorkItem(_ => { try { Thread.Sleep(1500); if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } });
        }

        // ------------------------------------------------------------------ program
        void SaveIcon()
        {
            if (file == null) return;
            byte[] ico = null;
            try { ico = PeInfo.MainIcon(file); } catch { }
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.FileName = Path.GetFileNameWithoutExtension(nestedLabel != null ? nestedLabel.Substring(nestedLabel.LastIndexOf('›') + 1).Trim() : file) + (ico != null ? ".ico" : ".png");
                d.Filter = ico != null ? L.T("pg_icon_files") + "|*.ico|PNG|*.png" : "PNG|*.png";
                if (LastDir.Show(d, FindForm(), "inspect_icon") != DialogResult.OK) return;
                try
                {
                    if (ico != null && d.FileName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(d.FileName, ico);
                    else if (progIcon != null) progIcon.Save(d.FileName, System.Drawing.Imaging.ImageFormat.Png);
                    ShowSaved(d.FileName);
                }
                catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
            }
        }

        // ------------------------------------------------------------------ hex search
        void FindNext()
        {
            if (file == null) return;
            string q = hexFind.Box.Text.Trim();
            if (q.Length == 0) return;
            byte[] pat = EntropyMap.Pattern(q, false);
            if (pat == null || pat.Length == 0) return;
            byte[] pat16 = EntropyMap.Pattern(q, true);   // text is also looked for as UTF-16 (how Windows programs keep it)
            if (pat16 != null && pat16.Length == pat.Length) pat16 = null;
            long from = hex.HiStart >= 0 ? hex.HiStart + 1 : 0;
            string f = file; int tk = token;
            btnFind.Enabled = false;
            Thread t = new Thread(() =>
            {
                long at = -1; int len = pat.Length; bool wrapped = false;
                try
                {
                    at = EntropyMap.Find(f, pat, from, () => tk != token);
                    if (at < 0 && pat16 != null && pat16.Length != pat.Length) { long a2 = EntropyMap.Find(f, pat16, from, () => tk != token); if (a2 >= 0) { at = a2; len = pat16.Length; } }
                    if (at < 0 && from > 0) { wrapped = true; at = EntropyMap.Find(f, pat, 0, () => tk != token); }
                }
                catch { }
                Ui(() =>
                {
                    btnFind.Enabled = true;
                    if (tk != token) return;
                    if (at < 0) { ShowToast(L.T("hx_not_found"), false, null, null); return; }
                    hex.GoTo(at, len); strip.Mark = at; strip.Invalidate();
                    if (wrapped) ShowToast(L.T("hx_wrapped"), true, null, null);
                });
            }) { IsBackground = true };
            t.Start();
        }

        void GoToOffset()
        {
            string q = hexGo.Box.Text.Trim().Replace(" ", "");
            if (q.Length == 0) return;
            long v; bool ok;
            if (q.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) ok = long.TryParse(q.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            else if (q.EndsWith("h", StringComparison.OrdinalIgnoreCase)) ok = long.TryParse(q.Substring(0, q.Length - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            else if (q.EndsWith("%")) { double p; ok = double.TryParse(q.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out p); v = (long)(hex.Length * Math.Max(0, Math.Min(100, p)) / 100); }
            else
            {
                ok = long.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
                if (!ok) ok = long.TryParse(q, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            }
            if (!ok || v < 0 || v >= Math.Max(1, hex.Length)) { ShowToast(L.T("hx_bad_offset"), false, null, null); return; }
            hex.GoTo(v, 1); strip.Mark = v; strip.Invalidate();
        }

        // ------------------------------------------------------------------ the ⋯ menu
        void ShowMore()
        {
            if (file == null) return;
            List<MenuPopup.Item> m = new List<MenuPopup.Item>();
            m.Add(new MenuPopup.Item { Text = L.T("mo_compare"), Sub = L.T("mo_compare_sub"), Glyph = Ico.Copy, Run = CompareWith });
            m.Add(new MenuPopup.Item { Text = L.T("mo_checksum"), Sub = L.T("mo_checksum_sub"), Glyph = Ico.Fingerprint, Run = CheckChecksum });
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("mo_copy_report"), Glyph = Ico.Copy, Run = () => { try { Clipboard.SetText(Report()); ShowToast(L.T("mo_report_copied"), true, null, null); } catch { } } });
            m.Add(new MenuPopup.Item { Text = L.T("mo_save_report"), Glyph = Ico.Save, Run = SaveReport });
            m.Add(new MenuPopup.Item { Divider = true });
            m.Add(new MenuPopup.Item { Text = L.T("sf_lookup"), Sub = L.T("sf_lookup_sub"), Glyph = Ico.Globe, Run = LookUp });
            if (safety != null && safety.FromInternet) m.Add(new MenuPopup.Item { Text = L.T("sf_unblock"), Glyph = Ico.Check, Run = Unblock });
            m.Add(new MenuPopup.Item { Text = L.T("mo_folder"), Glyph = Ico.FolderOpen, Run = () => { try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } } });
            // lined up with the ⋯ button's far edge, so the menu stays over the page
            Rectangle br = btnMore.RectangleToScreen(btnMore.ClientRectangle);
            int mw = Theme.S(320);
            MenuPopup.ShowAt(btnMore, new Point(Theme.RTL ? br.Left + mw : br.Right - mw, br.Bottom + Theme.S(4)), m, 320);
        }

        void CompareWith()
        {
            if (file == null) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("mo_compare"); d.Filter = L.T("all_files") + "|*.*";
                if (LastDir.Show(d, FindForm(), "inspect") != DialogResult.OK) return;
                CompareWindow.Open(file, d.FileName, FindForm());
            }
        }

        void CheckChecksum()
        {
            if (file == null) return;
            string clip = "";
            try { if (Clipboard.ContainsText()) clip = Clipboard.GetText().Trim(); } catch { }
            string guess = System.Text.RegularExpressions.Regex.Match(clip, @"\b[0-9A-Fa-f]{32,128}\b").Value;
            string got = Ask.Input(FindForm(), L.T("mo_checksum"), L.T("ck_msg"), guess, L.T("ck_ph"), L.T("ck_check"), L.T("cancel"), null, null);
            if (got == null) return;
            string want = System.Text.RegularExpressions.Regex.Replace(got, @"[^0-9A-Fa-f]", "").ToUpperInvariant();
            if (want.Length == 0) return;
            string f = file; int tk = token;
            Thread t = new Thread(() =>
            {
                string[] h = hashes;
                try { if (h == null) h = Sniffer.Hashes(f, null, () => tk != token); } catch { }
                string kind = null; bool match = false;
                if (h != null)
                {
                    string[] names = { "MD5", "SHA-1", "SHA-256" };
                    for (int i = 0; i < 3; i++) if (h[i] != null && h[i].Replace(" ", "").ToUpperInvariant().Length == want.Length) { kind = names[i]; match = h[i].Replace(" ", "").ToUpperInvariant() == want; }
                    if (kind == null && want.Length == 128)
                    {
                        kind = "SHA-512";
                        try { using (FileStream fs = File.OpenRead(f)) using (System.Security.Cryptography.SHA512 sha = System.Security.Cryptography.SHA512.Create()) match = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "") == want; } catch { }
                    }
                }
                Ui(() =>
                {
                    if (kind == null) { Ask.Info(FindForm(), L.T("mo_checksum"), L.T("ck_unknown"), AskKind.Warning); return; }
                    if (match) Ask.Info(FindForm(), L.T("ck_match_t"), L.T("ck_match", kind), AskKind.Info);
                    else Ask.Info(FindForm(), L.T("ck_nomatch_t"), L.T("ck_nomatch", kind), AskKind.Danger);
                });
            }) { IsBackground = true };
            t.Start();
        }

        string Report()
        {
            StringBuilder sb = new StringBuilder();
            string name = nestedLabel ?? Path.GetFileName(file);
            sb.AppendLine(name);
            sb.AppendLine(new string('=', Math.Min(60, name.Length)));
            foreach (string[] r in details.Rows) sb.AppendLine(r[0] + ": " + r[1]);
            if (safety != null)
            {
                sb.AppendLine(); sb.AppendLine(L.T("tab_safety") + ": " + Levels.Verdict(safety.Level));
                foreach (Finding f in safety.Findings) sb.AppendLine("  - " + f.Title + " — " + f.Detail);
            }
            if (privacy != null && privacy.Rows.Count > 0)
            {
                sb.AppendLine(); sb.AppendLine(L.T("tab_hidden") + ":");
                foreach (string[] r in privacy.Rows) sb.AppendLine("  " + r[0] + ": " + r[1]);
            }
            if (progView.Rows.Count > 0)
            {
                sb.AppendLine(); sb.AppendLine(L.T("tab_program") + ":");
                foreach (string[] r in progView.Rows) sb.AppendLine("  " + r[0] + ": " + r[1]);
            }
            if (inside != null)
            {
                sb.AppendLine(); sb.AppendLine(L.T("tab_inside") + ":");
                int n = 0;
                foreach (ArchiveEntry e in inside) { if (e.IsDir) continue; if (++n > 500) { sb.AppendLine("  …"); break; } sb.AppendLine("  " + e.Path + "  (" + Theme.HumanSize(e.Size) + ")"); }
            }
            sb.AppendLine(); sb.AppendLine("— Fadi Tools " + Shell.Version + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        void SaveReport()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.FileName = Path.GetFileNameWithoutExtension(file) + " report.txt";
                d.Filter = L.T("mo_text_files") + "|*.txt";
                if (LastDir.Show(d, FindForm(), "inspect_report") != DialogResult.OK) return;
                try { File.WriteAllText(d.FileName, Report(), new UTF8Encoding(true)); ShowSaved(d.FileName); }
                catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
            }
        }

        // ------------------------------------------------------------------ folders
        void InspectFolder(string dir)
        {
            int tk = ++token;
            file = null; sniff = null; media = null; hashes = null;
            ResetExtras();
            hex.Close();
            if (preview.Frame != null) { preview.Frame.Dispose(); preview.Frame = null; }
            folder = dir; folderRep = null; scanned = 0;
            details.Rows.Clear(); details.Rows.Add(new[] { L.T("fd_scanning"), "…" }); details.Invalidate();
            SetTabs(); tabs.SetSilently(0);
            ShowState();
            Thread t = new Thread(() =>
            {
                FolderReport rep = null;
                DateTime last = DateTime.MinValue;
                try
                {
                    rep = FolderScan.Run(dir, () => tk != token, n =>
                    {
                        if ((DateTime.Now - last).TotalMilliseconds < 150) return;
                        last = DateTime.Now;
                        Ui(() => { if (tk != token) return; scanned = n; details.Rows.Clear(); details.Rows.Add(new[] { L.T("fd_scanning"), n.ToString("N0") }); details.Invalidate(); Invalidate(); });
                    });
                }
                catch (Exception ex) { Ui(() => ShowToast(ex.Message, false, null, null)); }
                Ui(() =>
                {
                    if (tk != token) return;
                    folderRep = rep; scanned = -1;
                    FillFolder();
                    ShowState();
                });
            }) { IsBackground = true };
            t.Start();
        }

        void FillFolder()
        {
            details.Rows.Clear(); wrongView.Rows.Clear();
            if (folderRep == null) { details.Invalidate(); return; }
            FolderReport r = folderRep;
            details.Rows.Add(new[] { L.T("location"), folder });
            details.Rows.Add(new[] { L.T("fd_holds"), L.T("fd_counts", r.Files.ToString("N0"), r.Folders.ToString("N0")) + (r.Truncated ? "  ·  " + L.T("fd_truncated") : "") });
            details.Rows.Add(new[] { L.T("size"), Theme.HumanSize(r.Bytes) });
            List<KeyValuePair<Category, long[]>> kinds = new List<KeyValuePair<Category, long[]>>(r.ByKind);
            kinds.Sort((a, b) => b.Value[1].CompareTo(a.Value[1]));
            foreach (KeyValuePair<Category, long[]> kv in kinds)
                details.Rows.Add(new[] { L.T(Formats.CategoryKey(kv.Key)), L.T("fd_kind", kv.Value[0].ToString("N0"), Theme.HumanSize(kv.Value[1])) });
            if (r.Wrong.Count > 0) details.Rows.Add(new[] { L.T("fd_wrong"), r.Wrong.Count.ToString("N0") });
            int root = folder.TrimEnd('\\', '/').Length + 1;
            foreach (KeyValuePair<string, long> kv in r.Largest)
                details.Rows.Add(new[] { Theme.HumanSize(kv.Value), kv.Key.Length > root ? kv.Key.Substring(root) : kv.Key });
            foreach (string[] w in r.Wrong)
                wrongView.Rows.Add(new[] { "." + w[1] + "  →  ." + w[2], (w[0].Length > root ? w[0].Substring(root) : w[0]) + "  (" + w[3] + ")" });
            details.Invalidate(); wrongView.Invalidate();
            Invalidate();
        }

        void FixAll()
        {
            if (folderRep == null || folderRep.Wrong.Count == 0) return;
            if (!Ask.Confirm(FindForm(), L.T("fd_fix_all"), L.T("fd_fix_q", folderRep.Wrong.Count), L.T("fd_fix_all"), L.T("cancel"))) return;
            int n = 0;
            try { n = FolderScan.Fix(folderRep.Wrong); } catch (Exception ex) { ShowToast(ex.Message, false, null, null); }
            ShowToast(L.T("fd_fixed", n), n > 0, null, null);
            InspectFolder(folder);
        }

        void PaintFolderHead(Graphics g)
        {
            Rectangle badge = new Rectangle(head.X + Theme.S(20), head.Y + Theme.S(18), Theme.S(50), Theme.S(50));
            Theme.FillRoundGradient(g, M(badge), Theme.S(12), Theme.PrimaryTop, Theme.PrimaryBottom);
            Theme.Glyph(g, Ico.Folder, 16f, Theme.PrimaryInk, M(badge));
            string nm = Path.GetFileName(folder.TrimEnd('\\', '/'));
            Label(g, nm.Length > 0 ? nm : folder, Theme.F(12f, FontStyle.Bold), Theme.Ink, nameR);
            string t = folderRep != null ? L.T("fd_counts", folderRep.Files.ToString("N0"), folderRep.Folders.ToString("N0")) + "  ·  " + Theme.HumanSize(folderRep.Bytes)
                                         : L.T("fd_scanning") + (scanned > 0 ? "  " + scanned.ToString("N0") : "");
            Label(g, t, Theme.F(9.25f), Theme.InkSoft, typeR);
        }
    }
}
