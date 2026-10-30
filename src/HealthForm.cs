// Dashboard > PC health: the score (a ring), one tab per area, and what Elixar found in each. Checking only reads.
// A button either opens one of Elixar's own tools, opens Windows' own settings page, or (Update, Make a restore point,
// Use Balanced) does one logged thing after asking.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    // the ring with the score in it; also the Dashboard strip's left part
    public class HealthRing : PaintedCard
    {
        public int Score = -1; public string Small = "of 100"; public Font BigFont;
        public static Color ColorFor(int s) { return s < 0 ? Color.FromArgb(138, 145, 160) : s >= 85 ? Color.FromArgb(46, 125, 50) : s >= 65 ? Color.FromArgb(180, 95, 6) : Color.FromArgb(198, 40, 40); }
        // a drawing error must never reach Windows' error box: the ring is decoration
        protected override void OnPaint(PaintEventArgs e) { try { Paint_(e.Graphics); } catch { } }
        void Paint_(Graphics g)
        {
            g.Clear(Under); g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Math.Min(Width, Height) - S(4), th = Math.Max(S(6), d / 9);
            if (d < S(24)) return;   // too small to draw (while the window lays itself out)
            var r = new Rectangle((Width - d) / 2 + th / 2, (Height - d) / 2 + th / 2, d - th, d - th);
            using (var p = new Pen(Theme.Dark ? Color.FromArgb(60, 60, 72) : Color.FromArgb(230, 232, 238), th)) g.DrawEllipse(p, r);
            if (Score > 0) using (var p = new Pen(ColorFor(Score), th) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, r, -90, (float)(360.0 * Score / 100));   // an arc of 0 degrees is an error
            var big = BigFont ?? Font;
            {
                var bs = TextRenderer.MeasureText(g, Score < 0 ? "?" : Score.ToString(), big);
                TextRenderer.DrawText(g, Score < 0 ? "?" : Score.ToString(), big, new Point(Width / 2 - bs.Width / 2, Height / 2 - bs.Height / 2 - S(5)), ColorFor(Score), TextFormatFlags.NoPrefix);
                var ss = TextRenderer.MeasureText(g, Small, Font);
                TextRenderer.DrawText(g, Small, Font, new Point(Width / 2 - ss.Width / 2, Height / 2 + bs.Height / 2 - S(9)), Theme.Quiet, TextFormatFlags.NoPrefix);
            }
        }
    }

    // Dashboard: the score, the word, and the six areas; click to open the window
    public class HealthStrip : PaintedCard
    {
        public HealthReport Report; public bool Checking;
        public Font HeadFont, ChipFont;
        public HealthStrip() { Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e) { try { Paint_(e.Graphics); } catch { } }   // never an error box from drawing
        void Paint_(Graphics g)
        {
            g.Clear(Under); g.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < S(200) || Height < S(60)) return;   // too small to draw (while the window lays itself out)
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, card);
            using (var p = new Pen(Theme.Grid)) g.DrawRectangle(p, card);
            int ring = Height - S(16);
            var rr = M(new Rectangle(S(12), S(8), ring, ring));
            int score = Report == null ? -1 : Report.Total;
            int th = Math.Max(S(5), ring / 9);
            var arc = new Rectangle(rr.X + th / 2, rr.Y + th / 2, rr.Width - th, rr.Height - th);
            using (var p = new Pen(Theme.Dark ? Color.FromArgb(60, 60, 72) : Color.FromArgb(230, 232, 238), th)) g.DrawEllipse(p, arc);
            if (score > 0) using (var p = new Pen(HealthRing.ColorFor(score), th) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(p, arc, -90, (float)(360.0 * score / 100));
            string num = score < 0 ? "?" : score.ToString();
            TextRenderer.DrawText(g, num, HeadFont, rr, HealthRing.ColorFor(score), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | (Rtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : 0);
            int x = S(24) + ring;
            string word = Report == null ? (Checking ? Lang.T("Checking this PC...") : Lang.T("Not checked yet")) : Lang.T("PC health: " + Report.Word);
            string tail = Report == null ? "" : "  ·  " + Lang.T(Report.ToFix == 0 ? "nothing to fix" : Report.ToFix == 1 ? "1 thing to fix" : Report.ToFix + " things to fix") +
                                               "  ·  " + Lang.T(Checking ? "checking again..." : Ago(Report.At)) + "  ·  " + Lang.T("click to see");
            var head = M(new Rectangle(x, S(8), Width - x - S(12), S(24)));
            TextRenderer.DrawText(g, word + tail, ChipFont, head, Theme.Ink, flags);
            // the six areas, two rows of three
            int cw = (Width - x - S(12)) / 3, ch = S(24), y0 = S(36);
            ch = Math.Max(S(18), Math.Min(ch, (Height - y0 - S(10)) / 2 - S(3)));   // both rows always fit
            for (int i = 0; i < HealthReport.Areas.Length; i++)
            {
                string a = HealthReport.Areas[i]; int s = -1;
                if (Report != null) Report.Scores.TryGetValue(a, out s);
                var c = M(new Rectangle(x + (i % 3) * cw, y0 + (i / 3) * (ch + S(6)), cw - S(8), ch));
                using (var p = new Pen(Theme.Grid)) g.DrawRectangle(p, c);
                var inner = new Rectangle(c.X + S(8), c.Y, c.Width - S(16), c.Height);
                TextRenderer.DrawText(g, Lang.T(a), Font, inner, Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | (Rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left));
                TextRenderer.DrawText(g, s < 0 ? "-" : s.ToString(), ChipFont, inner, HealthRing.ColorFor(s), TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | (Rtl ? TextFormatFlags.Left : TextFormatFlags.Right));
            }
        }
        public static string Ago(DateTime at)
        {
            var d = DateTime.Now - at;
            if (d.TotalMinutes < 2) return "checked just now";
            if (d.TotalMinutes < 60) return "checked " + (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return "checked " + (int)d.TotalHours + (d.TotalHours < 2 ? " hour ago" : " hours ago");
            return "checked " + (int)d.TotalDays + (d.TotalDays < 2 ? " day ago" : " days ago");
        }
    }

    public class HealthForm : Form
    {
        readonly Func<int, int> S; readonly bool testMode;
        readonly Action<HAct> go;   // Elixar's own tools (the window closes first)
        readonly HealthRing ring; readonly Label head, sub, status; readonly Segmented areas; readonly FlowLayoutPanel list; readonly Button btnAgain;
        HealthReport report; string[] tabNames = new string[0];
        public HealthReport Report { get { return report; } }
        public string StatusText { get { return status.Text; } }
        public string Area { get { return tabNames.Length == 0 ? "" : tabNames[Math.Max(0, Math.Min(areas.Selected, tabNames.Length - 1))]; } }
        public List<HFind> Shown_ = new List<HFind>();
        public List<Button> Buttons = new List<Button>();
        public event Action Checked, Acted;

        public HealthForm(bool testMode, Func<int, int> scale, Action<HAct> go)
        {
            this.testMode = testMode; S = scale; this.go = go;
            Text = Lang.T("PC health"); Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; ShowIcon = false; ShowInTaskbar = false; BackColor = Color.White;
            ClientSize = new Size(S(860), S(640)); MinimumSize = new Size(S(620), S(440));
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(S(18), S(14), S(18), S(12)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, S(8)) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ring = new HealthRing { Size = new Size(S(84), S(84)), Scale_ = S(100) / 100f, Font = new Font("Segoe UI", 8f), BigFont = new Font("Segoe UI Semibold", 20f), Small = Lang.T("of 100"), Margin = new Padding(0, 0, S(14), 0) };
            var words = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, S(8), 0, 0) };
            head = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Text = Lang.T("Checking this PC..."), Margin = new Padding(0, 0, 0, S(2)) };
            sub = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(700), 0), Margin = new Padding(S(1), 0, 0, 0),
                              Text = Lang.T("Elixar reads what Windows already records. Checking changes nothing; every fix asks first and goes to Logs.") };
            words.Controls.Add(head); words.Controls.Add(sub);
            top.Controls.Add(ring, 0, 0); top.Controls.Add(words, 1, 0);
            areas = new Segmented { Options = new[] { "..." }, Scale_ = S(100) / 100f, Font = new Font("Segoe UI", 9f), Margin = new Padding(0, 0, 0, S(8)) };
            areas.Changed += () => ShowArea();
            list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            list.Resize += (o, e) => { foreach (Control c in list.Controls) c.Width = Math.Max(S(400), list.ClientSize.Width - S(24)); };
            status = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(800), 0), Margin = new Padding(S(1), S(8), 0, S(4)),
                                 Text = Lang.T("Score: each area 0-100 from what was found; Security and Stability count most. No registry cleaner, no \"RAM booster\", nothing downloaded from driver sites.") };
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            btnAgain = new Button { Text = Lang.T("Check again"), AutoSize = true, Enabled = false };
            var close = new Button { Text = Lang.T("Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
            bottom.Controls.AddRange(new Control[] { btnAgain, close });
            root.Controls.Add(top, 0, 0); root.Controls.Add(areas, 0, 1); root.Controls.Add(list, 0, 2); root.Controls.Add(status, 0, 3); root.Controls.Add(bottom, 0, 4);
            Controls.Add(root); CancelButton = close;
            btnAgain.Click += (o, e) => StartCheck();
            Shown += (o, e) => { Theme.Apply(this); Theme.DarkTitle(this); };
        }

        void UI(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch { } }

        public void StartCheck()
        {
            btnAgain.Enabled = false; head.Text = Lang.T("Checking this PC...");
            Task.Factory.StartNew(() => Health.Check(true, true, s => UI(() => sub.Text = Lang.T(s))), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                btnAgain.Enabled = true;
                if (t.IsFaulted) { head.Text = Lang.T("Couldn't finish the check"); sub.Text = t.Exception.GetBaseException().Message; return; }
                ShowReport(t.Result);
            }));
        }

        public void ShowReport(HealthReport r)
        {
            bool first = report == null;
            report = r; btnAgain.Enabled = true;
            ring.Score = r.Total; ring.Invalidate();
            head.Text = Lang.T("PC health: " + r.Word);
            head.ForeColor = HealthRing.ColorFor(r.Total);
            sub.Text = Lang.T(r.ToFix == 0 ? "Nothing needs fixing." : r.ToFix == 1 ? "1 thing to fix." : r.ToFix + " things to fix.") + "  " +
                       Lang.T("Elixar reads what Windows already records. Checking changes nothing; every fix asks first and goes to Logs.");
            var names = HealthReport.Areas.Where(a => r.Scores.ContainsKey(a) && r.Scores[a] >= 0).ToList();
            if (r.Items.Any(i => i.Area == "Battery")) names.Add("Battery");
            string was = Area;
            tabNames = names.ToArray();
            int keep = Math.Max(0, Array.IndexOf(tabNames, was));
            areas.Options = tabNames.Select(a => Lang.T(a) + (a == "Battery" ? (r.BatteryPct >= 0 ? " " + r.BatteryPct + "%" : "") : " " + r.Scores[a])).ToArray();
            // start on the area that needs you most
            if (first && tabNames.Length > 0)
            {
                var worst = tabNames.Where(a => a != "Battery").OrderBy(a => r.Scores[a]).FirstOrDefault();
                keep = worst != null && r.Scores[worst] < 100 ? Array.IndexOf(tabNames, worst) : 0;
            }
            areas.Selected = keep; areas.FitWidth(); areas.Invalidate();
            ShowArea();
            if (Checked != null) Checked();
        }

        public void SelectArea(string a) { int i = Array.IndexOf(tabNames, a); if (i >= 0) { areas.Selected = i; areas.Invalidate(); ShowArea(); } }

        static readonly Color Green = Color.FromArgb(46, 125, 50), Orange = Color.FromArgb(180, 95, 6), Red = Color.FromArgb(198, 40, 40), Grey = Color.FromArgb(138, 145, 160), Violet = Color.FromArgb(91, 76, 196);
        static Color LevelColor(HLevel l) { return l == HLevel.Ok ? Green : l == HLevel.Warn ? Orange : l == HLevel.Bad ? Red : Grey; }

        void ShowArea()
        {
            if (report == null) return;
            string a = Area;
            Shown_ = report.Items.Where(i => i.Area == a).OrderByDescending(i => i.Ignored ? -1 : (int)i.Level).ToList();
            Buttons.Clear();
            list.SuspendLayout(); list.Controls.Clear();
            foreach (var it in Shown_) list.Controls.Add(Row(it));
            if (Shown_.Count == 0) list.Controls.Add(new Label { AutoSize = true, ForeColor = Color.DimGray, Text = Lang.T("Nothing found here."), Margin = new Padding(S(2), S(10), 0, 0) });
            list.ResumeLayout();
            foreach (Control c in list.Controls) c.Width = Math.Max(S(400), list.ClientSize.Width - S(24));
        }

        Control Row(HFind it)
        {
            var p = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, S(2)), Padding = new Padding(0, S(6), 0, S(8)) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(30))); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var lvl = it.Ignored ? HLevel.Info : it.Level;
            var dot = new Label { Size = new Size(S(20), S(20)), Text = lvl == HLevel.Ok ? "✓" : lvl == HLevel.Info ? "i" : "!", TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White,
                                  BackColor = LevelColor(lvl), Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, S(2), S(8), 0) };
            var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            var titleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            titleRow.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(560), 0), Text = Lang.T(it.Title), Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = it.Level == HLevel.Bad && !it.Ignored ? Red : Color.FromArgb(24, 32, 43), Margin = new Padding(0), UseMnemonic = false });
            if (it.Tag.Length > 0) titleRow.Controls.Add(new Label { AutoSize = true, Text = Lang.T(it.Tag), Font = new Font("Segoe UI", 8f), ForeColor = it.Tag == "Safe" ? Green : Violet, Margin = new Padding(S(8), S(2), 0, 0) });
            if (it.Ignored) titleRow.Controls.Add(new Label { AutoSize = true, Text = Lang.T("You said this is fine"), Font = new Font("Segoe UI", 8f), ForeColor = Grey, Margin = new Padding(S(8), S(2), 0, 0) });
            text.Controls.Add(titleRow);
            text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(560), 0), Text = Lang.T(it.Detail), ForeColor = Color.DimGray, Margin = new Padding(0, S(1), 0, 0), UseMnemonic = false });
            var acts = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(S(8), 0, 0, 0) };
            if (it.Button.Length > 0 && !it.Ignored)
            {
                var b = new Button { Text = Lang.T(it.Button), AutoSize = true, Tag = it, UseMnemonic = false };
                if (it.Level == HLevel.Bad) { b.BackColor = Violet; b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; }
                b.Click += (o, e) => Act((HFind)((Button)o).Tag);
                acts.Controls.Add(b); Buttons.Add(b);
            }
            if (it.CanIgnore)
            {
                var ig = new Button { Text = Lang.T(it.Ignored ? "Watch it again" : "Ignore"), AutoSize = true, Tag = it, UseMnemonic = false };
                ig.Click += (o, e) => ToggleIgnore((HFind)((Button)o).Tag);
                acts.Controls.Add(ig); Buttons.Add(ig);
            }
            p.Controls.Add(dot, 0, 0); p.Controls.Add(text, 1, 0); p.Controls.Add(acts, 2, 0);
            return p;
        }

        public void ToggleIgnore(HFind it)
        {
            if (report == null || report.Facts == null) return;
            if (!testMode) Health.Ignore(it.Key, !it.Ignored);
            var ig = new HashSet<string>(report.Items.Where(i => i.Ignored).Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
            if (it.Ignored) ig.Remove(it.Key); else ig.Add(it.Key);
            var r = Health.Evaluate(report.Facts, report.At, ig);
            if (!testMode) Health.Remember(r);
            ShowReport(r);
        }

        // what a button does
        public void Act(HFind it)
        {
            switch (it.Act)
            {
                case HAct.UpdateApps:
                    if (Policy.Blocked(this, "apps.update")) return;
                    if (!testMode && Lang.Box(this, "Update " + it.Updates.Count + " app" + (it.Updates.Count == 1 ? "" : "s") + " from winget / Microsoft Store?\n\n" + string.Join(", ", it.Updates.Select(u => u.Name)) +
                                              "\n\nA restore point is made first. An app that is open may be closed to update.", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    Run("Updating...", () =>
                    {
                        var res = CheckFix.Fix(new[] { new FixItem { Kind = FixKind.Updates, On = true, Updates = it.Updates } }, null);
                        return res.Updated + " app" + (res.Updated == 1 ? "" : "s") + " updated" + (res.UpdateFailed > 0 ? ", " + res.UpdateFailed + " didn't work" : "") + ".";
                    });
                    break;
                case HAct.RestorePoint:
                    if (!testMode && Lang.Box(this, "Make a Windows restore point now? It takes a minute or two.", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    Run("Making a restore point...", () =>
                    {
                        string m = Health.MakeRestorePoint();
                        if (m.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("turned off", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            UI(() => { if (!testMode && Lang.Box(this, "System Protection is off for C:, so Windows can't make restore points. Open System Protection to turn it on?", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Health.Open(HAct.SystemProtection); });
                        }
                        return m;
                    });
                    break;
                case HAct.PowerBalanced:
                    if (it.Key != "power") return;
                    if (!testMode && Lang.Box(this, "Switch the power plan to Balanced? (Control Panel > Power Options switches it back.)", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    Run("Changing the power plan...", () => { string e = Health.UseBalanced(); return e == null ? "The power plan is Balanced now." : "Couldn't change the power plan: " + e; });
                    break;
                case HAct.Defender: case HAct.Firewall: case HAct.Uac: case HAct.BitLocker: case HAct.Backup: case HAct.Memory: case HAct.Restart: case HAct.SystemProtection:
                    if (!testMode) { string e = Health.Open(it.Act); if (e != null) status.Text = Lang.T("Couldn't open it: " + e); }
                    if (Acted != null) Acted();
                    break;
                default:
                    if (go != null) { if (!testMode) Close(); go(it.Act); }
                    if (Acted != null) Acted();
                    break;
            }
        }

        void Run(string what, Func<string> work)
        {
            status.Text = Lang.T(what); foreach (var b in Buttons) b.Enabled = false;
            Task.Factory.StartNew(work, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                foreach (var b in Buttons) b.Enabled = true;
                status.Text = t.IsFaulted ? Lang.T("Couldn't finish: " + t.Exception.GetBaseException().Message) : Lang.T(t.Result);
                if (Acted != null) Acted();
            }));
        }
    }

    public partial class MainForm
    {
        public HealthForm LastHealth;   // self-test and previews
        HealthStrip healthStrip; HealthReport healthNow; bool healthBusy; DateTime healthAt = DateTime.MinValue;

        public void OpenHealth()
        {
            using (var f = new HealthForm(testMode, S, HealthGo))
            {
                LastHealth = f;
                f.Checked += () => { healthNow = f.Report; healthAt = DateTime.Now; ShowHealthStrip(); };
                if (healthNow != null && healthNow.Facts != null && (DateTime.Now - healthAt).TotalMinutes < 30) f.ShowReport(healthNow);
                else if (!testMode) f.Shown += (o, e) => f.StartCheck();
                if (testMode) return;
                f.ShowDialog(this);
            }
        }

        void HealthGo(HAct a)
        {
            switch (a)
            {
                case HAct.RepairWindows: ShowRepair(); break;
                case HAct.Drivers: ShowTools(pageDrivers); break;
                case HAct.Startup: ShowStartup(); break;
                case HAct.DiskHealth: ShowDiskHealth(); break;
                case HAct.CheckFix: OpenCheckFix(); break;
                case HAct.Growing: OpenGrowing(); break;
            }
        }

        // the Dashboard strip: the last score at once, then a fresh check in the background (at most every 30 minutes)
        void RefreshHealth()
        {
            if (healthStrip == null || LinuxUi) return;
            if (healthNow == null && !testMode) { healthNow = Health.Remembered(); ShowHealthStrip(); }
            if (testMode || healthBusy || (healthNow != null && healthNow.Facts != null && (DateTime.Now - healthAt).TotalMinutes < 30)) return;
            healthBusy = true; healthStrip.Checking = true; healthStrip.Invalidate();
            Task.Factory.StartNew(() => Health.Check(true, null), TaskCreationOptions.LongRunning).ContinueWith(t =>
            {
                try { BeginInvoke((Action)(() => { healthBusy = false; healthStrip.Checking = false; if (!t.IsFaulted) { healthNow = t.Result; healthAt = DateTime.Now; } ShowHealthStrip(); })); } catch { }
            });
        }

        public void ShowHealthReport(HealthReport r) { healthNow = r; healthAt = DateTime.Now; ShowHealthStrip(); }   // self-test and previews
        public HealthReport HealthOnDashboard { get { return healthStrip == null ? null : healthStrip.Report; } }

        void ShowHealthStrip()
        {
            if (healthStrip == null) return;
            healthStrip.Report = healthNow; healthStrip.Invalidate();
        }
    }
}
