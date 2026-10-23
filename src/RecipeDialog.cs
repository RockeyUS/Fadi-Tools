using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FileConverter
{
    /// Making or changing a recipe: its name, the kind of file it's for, and its steps in order. Each step's setting
    /// is a chip that opens its choices; steps move up and down and come off with ×.
    public class RecipeDialog : Form
    {
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        readonly Recipe r;
        readonly bool isNew;
        readonly TextField name;
        readonly Segments kind;
        readonly StepList steps;
        readonly Btn bAdd, bSave, bCancel, bDelete;
        Rectangle titleR, nameLbl, kindLbl, stepsLbl;
        public bool Deleted;

        public static Recipe Edit(IWin32Window owner, Recipe existing, string kindForNew)
        {
            using (RecipeDialog d = new RecipeDialog(existing, kindForNew))
            {
                if (Ask.ShowDimmed(d, owner) != DialogResult.OK) return null;
                if (d.Deleted) { Recipes.Delete(existing.Id); return existing; }
                Recipes.Save(d.r);
                return d.r;
            }
        }

        RecipeDialog(Recipe existing, string kindForNew)
        {
            isNew = existing == null;
            r = existing != null ? existing.Copy() : new Recipe { Name = "", For = Array.IndexOf(Recipes.Kinds, kindForNew) >= 0 ? kindForNew : "image" };
            Movable.Make(this, () => Theme.S(70));
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; KeyPreview = true; StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            BackColor = Theme.Card; Font = Theme.F(10f);
            RightToLeft = Theme.RTL ? RightToLeft.Yes : RightToLeft.No;
            Text = L.T(isNew ? "rc_new" : "rc_edit");

            name = new TextField(() => L.T("rc_name_ph"), null) { Surface = Surface.Card };
            name.Box.Text = r.Name;
            kind = new Segments(() => L.T("wk_image"), () => L.T("wk_video"), () => L.T("wk_audio")) { Surface = Surface.Card, Glyphs = new[] { Ico.Photo, Ico.Video, Ico.Music } };
            kind.SetSilently(Math.Max(0, Array.IndexOf(Recipes.Kinds, r.For)));
            kind.SelectedChanged += (s, e) =>
            {
                r.For = Recipes.Kinds[kind.SelectedIndex];
                // steps that don't fit the new kind go; a "convert" step gets a format of the new kind
                string[] ok = Recipes.StepKinds(r.For);
                r.Steps.RemoveAll(x => Array.IndexOf(ok, x.Kind) < 0);
                foreach (RecipeStep x in r.Steps) if (Array.IndexOf(Recipes.Choices(x.Kind, r.For), x.Value) < 0) x.Value = Recipes.DefaultChoice(x.Kind, r.For);
                steps.Changed(); Invalidate();
            };
            steps = new StepList(r);
            steps.Edited += () => { Invalidate(); bSave.Enabled = r.Steps.Count > 0; bSave.Invalidate(); };
            bAdd = new Btn(() => L.T("rc_add_step"), Ico.Add, BtnKind.Secondary) { Surface = Surface.Card, FontSize = 9f };
            bAdd.Click += (s, e) => AddMenu();
            bSave = new Btn(() => L.T("rc_save"), Ico.Check, BtnKind.Primary) { Surface = Surface.Card, Enabled = r.Steps.Count > 0 };
            bSave.Click += (s, e) => Save();
            bCancel = new Btn(() => L.T("cancel"), null, BtnKind.Secondary) { Surface = Surface.Card };
            bCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            bDelete = new Btn(() => L.T("rc_delete"), Ico.Delete, BtnKind.Ghost) { Surface = Surface.Card, InkColor = Theme.Error, FontSize = 9f, Visible = !isNew };
            bDelete.Click += (s, e) => { Deleted = true; DialogResult = DialogResult.OK; Close(); };
            Controls.AddRange(new Control[] { name, kind, steps, bAdd, bSave, bCancel, bDelete });
            ClientSize = new Size(Theme.S(560), Theme.S(620));
            DoLayout();
            Shown += (s, e) => { if (isNew) name.Box.Focus(); };
        }

        void DoLayout()
        {
            int W = ClientSize.Width, H = ClientSize.Height, p = Theme.S(26);
            Func<Rectangle, Rectangle> M = x => Theme.Mirror(x, W);
            titleR = new Rectangle(p, Theme.S(20), W - p * 2, Theme.S(34));
            int y = Theme.S(70);
            nameLbl = new Rectangle(p, y, W - p * 2, Theme.S(20));
            name.Bounds = M(new Rectangle(p, y + Theme.S(24), W - p * 2, Theme.S(40)));
            y += Theme.S(76);
            kindLbl = new Rectangle(p, y, W - p * 2, Theme.S(20));
            kind.Bounds = M(new Rectangle(p, y + Theme.S(24), W - p * 2, Theme.S(38)));
            y += Theme.S(76);
            stepsLbl = new Rectangle(p, y, W - p * 2 - Theme.S(150), Theme.S(30));
            int aw = Math.Max(Theme.S(130), bAdd.PreferredWidth);
            bAdd.Bounds = M(new Rectangle(W - p - aw, y - Theme.S(2), aw, Theme.S(34)));
            y += Theme.S(40);
            int by = H - Theme.S(24) - Theme.S(40);
            steps.Bounds = M(new Rectangle(p, y, W - p * 2, by - Theme.S(16) - y));
            int sw = Math.Max(Theme.S(120), bSave.PreferredWidth + Theme.S(12)), cw = Math.Max(Theme.S(96), bCancel.PreferredWidth);
            bSave.Bounds = M(new Rectangle(W - p - sw, by, sw, Theme.S(40)));
            bCancel.Bounds = M(new Rectangle(W - p - sw - Theme.S(8) - cw, by, cw, Theme.S(40)));
            bDelete.Bounds = M(new Rectangle(p - Theme.S(8), by + Theme.S(3), Math.Max(Theme.S(100), bDelete.PreferredWidth), Theme.S(34)));
        }

        void AddMenu()
        {
            List<MenuPopup.Item> items = new List<MenuPopup.Item>();
            foreach (string k in Recipes.StepKinds(r.For))
            {
                string kk = k;
                bool have = r.Steps.Exists(x => x.Kind == kk);
                items.Add(new MenuPopup.Item { Text = Recipes.StepName(k), Glyph = Recipes.StepGlyph(k), Disabled = have && k != "compress" && k != "convert",
                    Run = () => { r.Steps.Add(new RecipeStep(kk, Recipes.DefaultChoice(kk, r.For))); steps.Changed(); } });
            }
            MenuPopup.Show(bAdd, items, 240);
        }

        void Save()
        {
            string n = name.Box.Text.Trim();
            if (n.Length == 0) n = Recipes.Summary(r);
            if (n.Length > 40) n = n.Substring(0, 40);
            r.Name = n;
            if (r.Steps.Count == 0) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int rr = Theme.S(18);
            try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, rr, rr)); } catch { }
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys k)
        {
            if (k == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            return base.ProcessCmdKey(ref msg, k);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int W = ClientSize.Width;
            Theme.Smooth(g);
            using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillRectangle(b, ClientRectangle);
            using (Pen pn = new Pen(Theme.CardBorder)) g.DrawPath(pn, Theme.Round(new RectangleF(0.5f, 0.5f, Width - 2, Height - 2), Theme.S(9)));
            Theme.Text(g, L.T(isNew ? "rc_new" : "rc_edit"), Theme.Display(16f), Theme.Ink, Theme.Mirror(titleR, W), Align.Start);
            Theme.Text(g, L.T("rc_name"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(nameLbl, W), Align.Start);
            Theme.Text(g, L.T("rc_for"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(kindLbl, W), Align.Start);
            Theme.Text(g, L.T("rc_steps"), Theme.F(8.75f, FontStyle.Bold), Theme.InkSoft, Theme.Mirror(stepsLbl, W), Align.Start);
        }
    }

    /// The steps of a recipe, one row each: what it does, its setting (a chip with choices), and move / remove.
    public class StepList : UiControl
    {
        readonly Recipe r;
        int hover = -1, hoverPart = -1;   // part: 0 chip, 1 up, 2 down, 3 remove
        public event Action Edited;

        public StepList(Recipe r) { this.r = r; Surface = Surface.Card; }
        public void Changed() { Invalidate(); if (Edited != null) Edited(); }

        int RowH { get { return Theme.S(52); } }
        Rectangle Row(int i) { return new Rectangle(0, i * (RowH + Theme.S(6)), Width, RowH); }
        Rectangle Part(Rectangle row, int part)
        {
            int b = Theme.S(30), y = row.Y + (row.Height - b) / 2;
            switch (part)
            {
                case 3: return new Rectangle(row.Right - Theme.S(10) - b, y, b, b);
                case 2: return new Rectangle(row.Right - Theme.S(10) - b * 2 - Theme.S(2), y, b, b);
                case 1: return new Rectangle(row.Right - Theme.S(10) - b * 3 - Theme.S(4), y, b, b);
                default: return new Rectangle(row.Right - Theme.S(10) - b * 3 - Theme.S(12) - Theme.S(150), row.Y + (row.Height - Theme.S(34)) / 2, Theme.S(150), Theme.S(34));
            }
        }
        Point Ltr(Point p) { return Theme.RTL ? new Point(Width - p.X, p.Y) : p; }

        void HitTest(Point p, out int row, out int part)
        {
            row = -1; part = -1;
            p = Ltr(p);
            for (int i = 0; i < r.Steps.Count; i++)
            {
                Rectangle rr = Row(i);
                if (!rr.Contains(p)) continue;
                row = i;
                for (int k = 0; k < 4; k++)
                {
                    if (k == 0 && Recipes.Choices(r.Steps[i].Kind, r.For).Length == 0) continue;
                    if (Part(rr, k).Contains(p)) { part = k; break; }
                }
                return;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row, part; HitTest(e.Location, out row, out part);
            if (row != hover || part != hoverPart) { hover = row; hoverPart = part; Cursor = part >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = hoverPart = -1; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int row, part; HitTest(e.Location, out row, out part);
            if (row < 0 || part < 0) return;
            RecipeStep s = r.Steps[row];
            if (part == 3) { r.Steps.RemoveAt(row); Changed(); return; }
            if (part == 1 && row > 0) { r.Steps.RemoveAt(row); r.Steps.Insert(row - 1, s); Changed(); return; }
            if (part == 2 && row < r.Steps.Count - 1) { r.Steps.RemoveAt(row); r.Steps.Insert(row + 1, s); Changed(); return; }
            if (part == 0)
            {
                List<MenuPopup.Item> items = new List<MenuPopup.Item>();
                foreach (string c in Recipes.Choices(s.Kind, r.For))
                {
                    string cc = c;
                    items.Add(new MenuPopup.Item { Text = Recipes.ChoiceLabel(s.Kind, c), Glyph = c == s.Value ? Ico.Check : null, Run = () => { s.Value = cc; Changed(); } });
                }
                Rectangle chip = Theme.Mirror(Part(Row(row), 0), Width);
                MenuPopup.ShowAt(this, PointToScreen(new Point(chip.X, chip.Bottom + Theme.S(4))), items, 200);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            PaintBg(g);
            Theme.Smooth(g);
            int W = Width;
            if (r.Steps.Count == 0)
            {
                Theme.Text(g, L.T("rc_no_steps"), Theme.F(9f), Theme.InkFaint, new Rectangle(0, 0, Width, Theme.S(50)), Align.Center, true, true);
                return;
            }
            for (int i = 0; i < r.Steps.Count; i++)
            {
                RecipeStep s = r.Steps[i];
                Rectangle row = Row(i);
                Theme.FillRound(g, Theme.Mirror(row, W), Theme.S(12), hover == i ? Theme.Hover : Theme.CardInner);
                // its number, then what it does
                Rectangle num = new Rectangle(row.X + Theme.S(12), row.Y + (row.Height - Theme.S(26)) / 2, Theme.S(26), Theme.S(26));
                Theme.FillRound(g, Theme.Mirror(num, W), Theme.S(13), Theme.AccentSoft);
                Theme.Text(g, (i + 1).ToString(), Theme.F(8.5f, FontStyle.Bold), Theme.AccentColor, Theme.Mirror(num, W), Align.Center, true, false, true);
                Rectangle gl = new Rectangle(num.Right + Theme.S(8), row.Y, Theme.S(22), row.Height);
                Theme.Glyph(g, Recipes.StepGlyph(s.Kind), 10f, Theme.Ink, Theme.Mirror(gl, W));
                Rectangle chip = Part(row, 0);
                bool hasChip = Recipes.Choices(s.Kind, r.For).Length > 0;
                Theme.Text(g, Recipes.StepName(s.Kind), Theme.F(9.5f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(gl.Right + Theme.S(6), row.Y, (hasChip ? chip.X : Part(row, 1).X) - gl.Right - Theme.S(12), row.Height), W), Align.Start, true, false, true);
                if (hasChip)
                {
                    bool hot = hover == i && hoverPart == 0;
                    Theme.FillRound(g, Theme.Mirror(chip, W), Theme.S(9), hot ? Theme.Mix(Theme.Card, Theme.AccentColor, 0.15f) : Theme.Card);
                    Theme.StrokeRound(g, Theme.Mirror(chip, W), Theme.S(9), hot ? Theme.AccentColor : Theme.ChipBorder);
                    Theme.Text(g, Recipes.ChoiceLabel(s.Kind, s.Value), Theme.F(9f, FontStyle.Bold), Theme.Ink, Theme.Mirror(new Rectangle(chip.X + Theme.S(10), chip.Y, chip.Width - Theme.S(30), chip.Height), W), Align.Start, true, false, true);
                    Theme.Glyph(g, "", 7f, Theme.InkSoft, Theme.Mirror(new Rectangle(chip.Right - Theme.S(24), chip.Y, Theme.S(20), chip.Height), W));
                }
                string[] gs = { null, "", "", Ico.Close };
                for (int k = 1; k < 4; k++)
                {
                    Rectangle b = Part(row, k);
                    bool can = k == 3 || (k == 1 && i > 0) || (k == 2 && i < r.Steps.Count - 1);
                    if (hover == i && hoverPart == k && can) Theme.FillRound(g, Theme.Mirror(b, W), Theme.S(8), Theme.Selected);
                    Theme.Glyph(g, gs[k], 8.5f, !can ? Theme.Mix(Theme.InkFaint, Theme.CardInner, 0.5f) : k == 3 && hover == i && hoverPart == 3 ? Theme.Error : Theme.InkSoft, Theme.Mirror(b, W));
                }
            }
        }
    }
}
