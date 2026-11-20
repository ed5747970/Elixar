// Dashboard > Growing fast: each folder that grows fast, what Elixar found out about it, and what you can do.
// Deleting is only ever offered for an app's log, cache and temp folders, and only as a rule you allow.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class GrowingForm : Form
    {
        readonly Func<int, int> S; readonly bool testMode;
        readonly Action<string> open; readonly Action showMove, showRepair;
        readonly FlowLayoutPanel list; readonly Label head, sub, status; readonly Button btnCheck;
        public List<FolderStudy> Studies = new List<FolderStudy>();
        public event Action Loaded;
        public string StatusText { get { return status.Text; } }

        public GrowingForm(bool testMode, Func<int, int> scale, Action<string> open, Action showMove, Action showRepair)
        {
            this.testMode = testMode; S = scale; this.open = open; this.showMove = showMove; this.showRepair = showRepair;
            Text = Lang.T("Growing fast"); Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; ShowIcon = false; ShowInTaskbar = false; BackColor = Color.White;
            ClientSize = new Size(S(900), S(680)); MinimumSize = new Size(S(640), S(420)); WindowFit.Watch(this);
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(S(18), S(14), S(18), S(12)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            head = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Text = Lang.T("Folders growing fast"), Margin = new Padding(0, 0, 0, S(2)) };
            sub = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(850), 0), Margin = new Padding(S(1), 0, 0, S(8)),
                              Text = Lang.T("Elixar measures the folders on C: once a day and studies the ones that grow fast: who writes there, what kind of files, and whether old files are ever removed. Nothing is deleted unless you allow it.") };
            list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            list.Resize += (o, e) => { foreach (Control c in list.Controls) c.Width = Math.Max(S(400), list.ClientSize.Width - S(24)); };
            status = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(850), 0), Margin = new Padding(S(1), S(8), 0, S(4)), Text = "" };
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            btnCheck = new Button { Text = Lang.T("Check now"), AutoSize = true };
            var close = new Button { Text = Lang.T("Close"), AutoSize = true, DialogResult = DialogResult.Cancel };
            bottom.Controls.AddRange(new Control[] { btnCheck, close });
            root.Controls.Add(head, 0, 0); root.Controls.Add(sub, 0, 1); root.Controls.Add(list, 0, 2); root.Controls.Add(status, 0, 3); root.Controls.Add(bottom, 0, 4);
            Controls.Add(root); CancelButton = close;
            btnCheck.Click += (o, e) => CheckNow();
            Shown += (o, e) => { Theme.Apply(this); Theme.DarkTitle(this); if (!testMode) Load_(); };
        }

        void UI(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch { } }

        // studies the folders the daily check found (fresh, every time the window opens)
        public void Load_()
        {
            var cur = FolderGrowth.Current();
            if (cur.Count == 0) { ShowStudies(new List<Tuple<FastFolder, FolderStudy>>()); return; }
            status.Text = Lang.T("Studying " + cur.Count + " folder" + (cur.Count == 1 ? "" : "s") + "...");
            Task.Factory.StartNew(() =>
            {
                var names = FolderGrowth.OwnerNames();
                return cur.Select(c => Tuple.Create(c.Item1, FolderGrowth.Study(c.Item1.Path, names, DateTime.Now))).ToList();
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() => ShowStudies(t.IsFaulted ? new List<Tuple<FastFolder, FolderStudy>>() : t.Result)));
        }

        void CheckNow()
        {
            btnCheck.Enabled = false; status.Text = Lang.T("Measuring the folders on C: (a few minutes)...");
            Task.Factory.StartNew(() => FolderGrowth.DailyRun(DateTime.Now, null, null), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                btnCheck.Enabled = true;
                if (t.IsFaulted) { status.Text = Lang.T("Couldn't measure: " + t.Exception.GetBaseException().Message); return; }
                ShowStudies(t.Result.Fast.Select((f, i) => Tuple.Create(f, t.Result.Studies[i])).ToList());
            }));
        }

        public void ShowStudies(List<Tuple<FastFolder, FolderStudy>> items)
        {
            Studies = items.Select(x => x.Item2).ToList();
            list.SuspendLayout(); list.Controls.Clear();
            if (items.Count == 0)
            {
                bool history = FolderGrowth.ReadSnaps().Count >= 2;
                list.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(820), 0), Margin = new Padding(S(2), S(10), 0, 0), ForeColor = Color.DimGray,
                                              Text = Lang.T(history ? "No folder on C: is growing fast right now." : "Elixar measures the folders once a day; it can tell what grows fast after a few days of readings. Check now takes the first reading.") });
            }
            foreach (var it in items) list.Controls.Add(Card(it.Item1, it.Item2));
            var rules = FolderGrowth.Rules();
            if (rules.Count > 0)
            {
                var rp = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, S(14), 0, 0) };
                rp.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), ForeColor = Color.FromArgb(91, 76, 196), Text = Lang.T("Your rules (allowed by you)") });
                foreach (var r in rules)
                {
                    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(S(4), S(2), 0, 0) };
                    row.Controls.Add(new Label { AutoSize = true, Text = r.Path + "  -  " + Lang.T(FolderGrowth.RuleText(r)), Margin = new Padding(0, S(6), S(8), 0), UseMnemonic = false });
                    var stop = new Button { Text = Lang.T("Stop"), AutoSize = true, Tag = r.Path };
                    stop.Click += (o, e) => { string err = Try(() => { FolderGrowth.RemoveRule((string)((Button)o).Tag); return testMode ? null : FolderGrowth.Schedule(Install.Exe); }); ShowStudies(items); if (err != null) status.Text = Lang.T("Not saved:") + " " + Lang.T(err); };
                    row.Controls.Add(stop); rp.Controls.Add(row);
                }
                list.Controls.Add(rp);
            }
            list.ResumeLayout();
            foreach (Control c in list.Controls) c.Width = Math.Max(S(400), list.ClientSize.Width - S(24));
            status.Text = items.Count == 0 ? "" : Lang.T(items.Count + " folder" + (items.Count == 1 ? "" : "s") + " growing fast.");
            if (Loaded != null) Loaded();
        }

        static readonly Color Green = Color.FromArgb(46, 125, 50), Violet = Color.FromArgb(91, 76, 196), Grey = Color.FromArgb(90, 98, 116), Red = Color.FromArgb(198, 40, 40), Orange = Color.FromArgb(180, 95, 6);

        public static string KindText(GrowKind k)
        {
            switch (k) { case GrowKind.Safe: return "Safe to keep in check"; case GrowKind.Yours: return "Your files"; case GrowKind.Windows: return "Windows"; case GrowKind.Unusual: return "Looks unusual"; default: return "Look at it"; }
        }
        static Color KindColor(GrowKind k) { return k == GrowKind.Safe ? Green : k == GrowKind.Yours ? Violet : k == GrowKind.Unusual ? Red : Grey; }

        Control Card(FastFolder f, FolderStudy s)
        {
            var p = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, S(10)), Padding = new Padding(S(10), S(8), S(10), S(8)) };
            p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var top = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            top.Controls.Add(new Label { AutoSize = true, Text = f.Path, Font = new Font("Consolas", 9.5f), Margin = new Padding(0, S(2), S(12), 0), UseMnemonic = false });
            top.Controls.Add(new Label { AutoSize = true, Text = Lang.T(FolderGrowth.GrewText(f)) + "  ·  " + Fs.Fmt(f.Now), Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = s.Kind == GrowKind.Unusual ? Red : Orange, Margin = new Padding(0, S(2), S(10), 0) });
            top.Controls.Add(new Label { AutoSize = true, Text = Lang.T(KindText(s.Kind)), Font = new Font("Segoe UI Semibold", 8.5f), ForeColor = KindColor(s.Kind), Margin = new Padding(0, S(3), 0, 0) });
            p.Controls.Add(top);
            Func<string, string, Color, Control> line = (k, v, c) =>
            {
                var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(4), 0, 0) };
                r.Controls.Add(new Label { AutoSize = false, Width = S(100), Height = S(20), Text = Lang.T(k), ForeColor = Color.DimGray, Margin = new Padding(0) });
                r.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(700), 0), Text = Lang.T(v), ForeColor = c, Margin = new Padding(0), UseMnemonic = false });
                return r;
            };
            p.Controls.Add(line("Belongs to", s.Owner, s.Kind == GrowKind.Unusual && !s.OwnerKnown ? Red : Color.FromArgb(24, 32, 43)));
            p.Controls.Add(line("What's inside", s.Inside, Color.FromArgb(24, 32, 43)));
            var verdict = new Label { AutoSize = true, MaximumSize = new Size(S(800), 0), Padding = new Padding(S(6)), Margin = new Padding(0, S(8), 0, S(6)), UseMnemonic = false,
                                      Text = Lang.T((s.Reason.Length > 0 ? s.Reason + " " : "") + s.Verdict),
                                      BackColor = s.Kind == GrowKind.Safe ? Color.FromArgb(233, 246, 236) : s.Kind == GrowKind.Yours ? Color.FromArgb(239, 233, 251) : s.Kind == GrowKind.Unusual ? Color.FromArgb(253, 236, 234) : Color.FromArgb(238, 240, 243) };
            p.Controls.Add(verdict);
            var acts = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            Func<string, Action, Button> btn = (t, a) => { var b = new Button { Text = Lang.T(t), AutoSize = true, UseMnemonic = false }; b.Click += (o, e) => a(); acts.Controls.Add(b); return b; };
            var rule = FolderGrowth.Rules().FirstOrDefault(r => string.Equals(r.Path, s.Path, StringComparison.OrdinalIgnoreCase));
#if !BUSINESS
            // Elixar for Private: you decide what happens to an app's folder that keeps growing
            if (FolderGrowth.ChoiceProblem(s) == null)
            {
                p.Controls.Add(Choices(s, rule, p));
                return p;
            }
#endif
            if (s.Kind == GrowKind.Safe)
            {
                if (rule != null) acts.Controls.Add(new Label { AutoSize = true, ForeColor = Green, Text = Lang.T("Kept in check: older than " + rule.Days + " days, daily"), Margin = new Padding(0, S(8), S(8), 0) });
                else { var k = btn("Keep it in check...  (delete old ones daily)", () => KeepInCheck(s)); k.Font = new Font("Segoe UI Semibold", 9.5f); }
                btn("Clean once now", () => CleanOnce(s));
            }
            else if (s.Kind == GrowKind.Yours && showMove != null) btn("Move off C:...", () => { Close(); showMove(); });
            else if (s.Kind == GrowKind.Windows && showRepair != null) btn("Repair Windows...", () => { Close(); showRepair(); });
            else if (s.Kind == GrowKind.Unusual) { var d = btn("Scan with Microsoft Defender", () => Scan(s)); d.BackColor = Red; d.ForeColor = Color.White; }
            btn("Open", () => { if (open != null) open(s.Path); });
            btn(s.Kind == GrowKind.Unusual ? "It's mine, ignore" : "Ignore", () => { FolderGrowth.Ignore(s.Path); p.Visible = false; });
            p.Controls.Add(acts);
            return p;
        }

#if !BUSINESS
        public readonly Dictionary<string, ChoiceBox> ChoiceBoxes = new Dictionary<string, ChoiceBox>(StringComparer.OrdinalIgnoreCase);   // self-test
        public class ChoiceBox { public RadioButton Tell, Cap, Age, Ignore; public NumericUpDown CapGb, DownGb; public ComboBox Days; public Button Save; public Label Saved; }
        public Func<string, bool> ConfirmForTest;   // self-test: answers the question instead of a person

        Control Choices(FolderStudy s, KeepRule rule, Control card)
        {
            var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, S(2), 0, S(2)), Padding = new Padding(S(8), S(6), S(8), S(6)) };
            box.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            box.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), ForeColor = Violet, Text = Lang.T("What should Elixar do with this folder?"), Margin = new Padding(0, 0, 0, S(4)) });
            var cb = new ChoiceBox();
            Func<string, Label> note = t => new Label { AutoSize = true, MaximumSize = new Size(S(720), 0), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 8.75f), Text = Lang.T(t), Margin = new Padding(S(20), 0, 0, S(6)) };
            Func<string, Label> word = t => new Label { AutoSize = true, Text = Lang.T(t), Margin = new Padding(0, S(6), S(2), 0) };
            Func<int, int, int, NumericUpDown> num = (v, min, max) => new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, v)), Width = S(64), Margin = new Padding(S(2), S(3), S(2), 0) };
            Func<Control[], FlowLayoutPanel> row = cs => { var r = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0), MaximumSize = new Size(S(760), 0) }; r.Controls.AddRange(cs); return r; };

            cb.Tell = new RadioButton { AutoSize = true, Text = Lang.T("Just tell me"), Margin = new Padding(0, S(2), 0, 0) };
            box.Controls.Add(cb.Tell); box.Controls.Add(note("A notice once a week, nothing else."));

            long nowGb = Math.Max(1, s.Bytes >> 30);
            bool sap = s.Known == "secureaplus";
            int capDefault = rule != null && rule.Mode == "cap" ? (int)(rule.Cap >> 30) : sap ? 20 : (int)Math.Max(2, Math.Min(500, nowGb));
            int downDefault = rule != null && rule.Mode == "cap" ? (int)(rule.Down >> 30) : sap ? 2 : Math.Max(1, capDefault / 2);
            cb.Cap = new RadioButton { AutoSize = true, Text = Lang.T("Cap its size: when it passes"), Margin = new Padding(0, S(4), 0, 0) };
            cb.CapGb = num(capDefault, 2, 2000); cb.DownGb = num(downDefault, 0, 1999);
            box.Controls.Add(row(new Control[] { cb.Cap, cb.CapGb, word("GB, delete the oldest files until it's under"), cb.DownGb, word("GB") }));
            box.Controls.Add(note("Checked daily. The newest file always stays, and programs (.exe, .dll...) are never deleted. Deleted for good: copies in Undo would take the same space."));

            cb.Age = new RadioButton { AutoSize = true, Text = Lang.T(s.Kind == GrowKind.Safe ? "Delete old log, cache and temp files older than" : "Delete files older than"), Margin = new Padding(0, S(4), 0, 0) };
            cb.Days = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(56), Margin = new Padding(S(2), S(3), S(2), 0) };
            foreach (var d in new[] { 7, 14, 30, 90 }) cb.Days.Items.Add(d.ToString());
            int days = rule != null && rule.Mode != "cap" ? rule.Days : 14;
            cb.Days.SelectedItem = new[] { 7, 14, 30, 90 }.Contains(days) ? days.ToString() : "14";
            box.Controls.Add(row(new Control[] { cb.Age, cb.Days, word("days, daily") }));
            box.Controls.Add(note(s.Kind == GrowKind.Safe ? "Only those kinds of files. The first two times they go to Undo, then they're deleted." : "Every kind of file except programs. The first two times they go to Undo, then they're deleted."));

            cb.Ignore = new RadioButton { AutoSize = true, Text = Lang.T("Ignore this folder"), Margin = new Padding(0, S(4), 0, 0) };
            box.Controls.Add(cb.Ignore); box.Controls.Add(note("No more notices about it."));

            if (rule == null) cb.Tell.Checked = true; else if (rule.Mode == "cap") cb.Cap.Checked = true; else cb.Age.Checked = true;
            // a number typed or a list picked selects its choice
            cb.CapGb.ValueChanged += (o, e) => cb.Cap.Checked = true; cb.DownGb.ValueChanged += (o, e) => cb.Cap.Checked = true; cb.Days.SelectedIndexChanged += (o, e) => { if (cb.Days.Focused) cb.Age.Checked = true; };
            // radio buttons in different rows are one group here
            var radios = new[] { cb.Tell, cb.Cap, cb.Age, cb.Ignore };
            foreach (var rb in radios) rb.CheckedChanged += (o, e) => { var me = (RadioButton)o; if (me.Checked) foreach (var other in radios) if (other != me) other.Checked = false; };

            var acts = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(4), 0, 0) };
            cb.Save = new Button { Text = Lang.T("Save my choice..."), AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f) };
            cb.Saved = new Label { AutoSize = true, ForeColor = Green, Margin = new Padding(S(6), S(8), 0, 0), Text = rule != null ? Lang.T("Saved:") + " " + Lang.T(FolderGrowth.RuleText(rule)) : "" };
            cb.Save.Click += (o, e) => SaveChoice(s, cb, card);
            var openB = new Button { Text = Lang.T("Open"), AutoSize = true }; openB.Click += (o, e) => { if (open != null) open(s.Path); };
            acts.Controls.AddRange(new Control[] { cb.Save, openB, cb.Saved });
            box.Controls.Add(acts);
            ChoiceBoxes[s.Path] = cb;
            return box;
        }

        bool Ask(string q) { return ConfirmForTest != null ? ConfirmForTest(q) : Lang.Box(this, q, "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }

        public void SaveChoice(FolderStudy s, ChoiceBox cb, Control card)
        {
            string err = null;
            if (cb.Ignore.Checked)
            {
                string e1 = Try(() => { FolderGrowth.RemoveRule(s.Path); FolderGrowth.Ignore(s.Path); return testMode ? null : FolderGrowth.Schedule(Install.Exe); });
                if (e1 != null) { status.Text = Lang.T("Not saved:") + " " + Lang.T(e1); return; }
                card.Visible = false; status.Text = Lang.T("Ignored: no more notices about this folder."); return;
            }
            if (cb.Tell.Checked)
            {
                string e2 = Try(() => { FolderGrowth.RemoveRule(s.Path); return testMode ? null : FolderGrowth.Schedule(Install.Exe); });
                if (e2 != null) { status.Text = Lang.T("Not saved:") + " " + Lang.T(e2); return; }
                cb.Saved.Text = Lang.T("Saved: just tell me"); status.Text = ""; return;
            }
            if (cb.Cap.Checked)
            {
                long cap = (long)cb.CapGb.Value << 30, down = (long)cb.DownGb.Value << 30;
                if (down >= cap) { status.Text = Lang.T("The size to go back down to must be smaller than the limit."); return; }
                long total; var now = FolderGrowth.CapFiles(new KeepRule { Path = s.Path, Mode = "cap", Cap = cap, Down = down }, out total);
                long bytes = now.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                if (!Ask(Lang.T("Cap this folder at " + Fs.Fmt(cap) + "?") + "\n\n" + s.Path + "\n\n" +
                         Lang.T("Every day, when it's over " + Fs.Fmt(cap) + ", Elixar deletes its oldest files until it's under " + Fs.Fmt(down) + ". The newest file always stays, and programs are never deleted.") + "\n\n" +
                         Lang.T(now.Count > 0 ? "Right now this deletes " + now.Count + " files (" + Fs.Fmt(bytes) + "). They can't be put back." : "Right now it's under the limit: nothing is deleted today."))) return;
                err = Try(() => FolderGrowth.AddChoice(s, "cap", 14, cap, down));
            }
            else
            {
                int days = int.Parse((string)cb.Days.SelectedItem ?? "14");
                if (!Ask(Lang.T(s.Kind == GrowKind.Safe ? "Every day, delete old log, cache and temp files older than " + days + " days in this folder?" : "Every day, delete files older than " + days + " days in this folder?") + "\n\n" + s.Path + "\n\n" +
                         Lang.T("Programs are never deleted. The first two times they go to Undo (Tools > Undo puts them back), then they're deleted. Change it here any time."))) return;
                err = Try(() => FolderGrowth.AddChoice(s, "age", days, 0, 0));
            }
            if (err == null && !testMode) err = Try(() => FolderGrowth.Schedule(Install.Exe));
            var rule = err != null ? null : FolderGrowth.Rules().FirstOrDefault(r => string.Equals(r.Path, s.Path, StringComparison.OrdinalIgnoreCase));
            if (err == null && rule == null) err = "Elixar couldn't write it down (it needs administrator rights).";
            if (err != null) { status.Text = Lang.T("Not saved:") + " " + Lang.T(err); return; }
            cb.Saved.Text = Lang.T("Saved:") + " " + Lang.T(FolderGrowth.RuleText(rule));
            // a cap is done once now as well: the space is wanted today, not at the next daily run
            if (rule.Mode == "cap")
            {
                status.Text = Lang.T("Cleaning...");
                Task.Factory.StartNew(() => { using (ChangeLog.In("Keep in check")) return FolderGrowth.RunRule(rule, DateTime.Now, null); })
                    .ContinueWith(t => UI(() =>
                    {
                        status.Text = Lang.T(t.IsFaulted ? "Couldn't clean: " + t.Exception.GetBaseException().Message : t.Result > 0 ? "Freed " + Fs.Fmt(t.Result) : "Nothing to delete today.");
                        if (CapDone != null) CapDone();
                    }));
            }
        }
        public event Action CapDone;   // self-test
#endif

        // a save that can fail (the rules are kept where only administrators can write): its message, never a crash
        static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return e.Message; } }

        // asks first, then makes the rule; the first two runs go to Undo
        public bool KeepInCheck(FolderStudy s)
        {
            if (!testMode && Lang.Box(this, "Delete old log, cache and temp files in\n" + s.Path + "\nevery day, once they are older than 14 days?\n\n" +
                                            "Only files of those kinds, never programs, your files or Windows' files. The first two times they go to Undo (Tools > Undo puts them back). Stop it here any time.",
                                            "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            string err = FolderGrowth.AddRule(s, 14);
            if (err == null && !testMode) err = FolderGrowth.Schedule(Install.Exe);
            status.Text = Lang.T(err == null ? "Allowed: " + Path.GetFileName(s.Path.TrimEnd('\\', '/')) + " is kept in check from now on." : "Not allowed: " + err);
            if (err == null) ShowStudies(Studies.Select(x => Tuple.Create(new FastFolder { Path = x.Path, Now = x.Bytes }, x)).ToList());
            return err == null;
        }

        void CleanOnce(FolderStudy s)
        {
            if (!testMode && Lang.Box(this, "Move the log, cache and temp files older than 14 days in\n" + s.Path + "\nto Undo now?", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            status.Text = Lang.T("Cleaning...");
            Task.Factory.StartNew(() => { using (ChangeLog.In("Keep in check")) return FolderGrowth.RunRule(new KeepRule { Path = s.Path, Days = 14 }, DateTime.Now, null); })
                .ContinueWith(t => UI(() => status.Text = Lang.T(t.IsFaulted ? "Couldn't clean: " + t.Exception.GetBaseException().Message : "Moved " + Fs.Fmt(t.Result) + " to Undo.")));
        }

        void Scan(FolderStudy s)
        {
            status.Text = Lang.T("Microsoft Defender is scanning " + s.Path + "...");
            Task.Factory.StartNew(() => { string o; int c = StartupApps.DefenderScan(s.Path.TrimEnd('\\', '/'), out o); return Tuple.Create(c, o); }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                if (t.IsFaulted) { status.Text = Lang.T("Couldn't scan: " + t.Exception.GetBaseException().Message); return; }
                int c = t.Result.Item1;
                status.Text = Lang.T(c == 0 ? "Microsoft Defender found no threats there." : c == 2 ? "Microsoft Defender found threats and dealt with them: open Windows Security > Protection history." :
                                     c == -1 ? "Microsoft Defender isn't available on this PC: scan the folder with your antivirus." : "Microsoft Defender couldn't finish (" + c + "): open Windows Security and scan the folder.");
                using (ChangeLog.In("Security")) ChangeLog.Add("Security", "Scanned a fast-growing folder with Microsoft Defender", s.Path, "", status.Text, "", false);
            }));
        }
    }

    public partial class MainForm
    {
        public GrowingForm LastGrowing;   // self-test
        public void OpenGrowing()
        {
            using (var f = new GrowingForm(testMode, S, p => { if (Directory.Exists(p)) OpenDrill(p); else ShowInFolder(p); }, ShowMove, ShowRepair))
            {
                LastGrowing = f;
                if (testMode) return;
                f.ShowDialog(this);
            }
            RefreshOverview();
        }
    }
}
