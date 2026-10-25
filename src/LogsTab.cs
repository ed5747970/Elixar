// Logs (next to Settings): every change Elixar made to this PC - registry values with the value before and after, files, commands,
// apps, security and settings - with search, filters, details, undo for registry values, and export.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pageLogs;
        ListView logsList;
        ComboBox logsKind, logsPeriod;
        TextBox logsSearch, logsDetail;
        Label logsCount;
        Button btnLogsUndo, btnLogsCopy, btnLogsFile, btnLogsText, btnLogsExport, btnLogsRefresh;
        List<ChangeLog.Entry> logsAll = new List<ChangeLog.Entry>();
        ListBox logsSections; string logsSection = "*"; bool logsSyncing;
        class Rubric { public string Key = "", Name = ""; public int Count; public override string ToString() { return Name; } }
        public static Func<string, DialogResult> LogsConfirmHook;   // self-test: answers the undo question

        static readonly string[] LogKinds = { "All changes", "Registry", "Files", "Cleaning", "Windows command", "Apps", "Phone", "Security", "Email", "Settings", "Undo" };
        static readonly string[] LogPeriods = { "Last 7 days", "Last 30 days", "Everything" };

        // for the self-test and previews
        public int LogsRowCount { get { return logsList == null ? 0 : logsList.Items.Count; } }
        public string LogsRowText(int i) { var r = logsList.Items[i]; return string.Join(" | ", r.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text)); }
        public void LogsSelect(int i) { logsList.SelectedItems.Clear(); logsList.Items[i].Selected = true; logsList.Items[i].Focused = true; LogsShowSelected(); }
        public string LogsDetailText { get { return logsDetail.Text; } }
        public bool LogsUndoEnabled { get { return btnLogsUndo.Enabled; } }
        public void LogsSetFilter(int kind, int period, string search) { logsKind.SelectedIndex = kind; logsPeriod.SelectedIndex = period; logsSearch.Text = search ?? ""; LogsFill(); }
        public void ShowLogs() { tabs.SelectedTab = pageLogs; }

        // the logs are in a folder only administrators can read, so they open in Windows' Notepad from System32 (with Elixar's
        // administrator rights; a program opened the normal way couldn't read them)
        void OpenLog(string file)
        {
            if (!Win.IsWindows || !Sys.IsAdmin()) { Shell.Open(file); return; }
            Process.Start(new ProcessStartInfo(Win.SystemExe("notepad.exe"), "\"" + file + "\"") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Win.SystemExe("notepad.exe")) });
        }
        public void LogsClickUndo() { LogsUndo(); }
        public void LogsFocus() { logsList.Focus(); }
        public List<string> LogsSections { get { return logsSections.Items.Cast<Rubric>().Select(r => r.Key + ":" + r.Count).ToList(); } }
        public void LogsPickSection(string key) { logsSection = key; LogsFill(); }

        void BuildLogsTab()
        {
            pageLogs = new TabPage("Logs") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(6);
            t.ColumnCount = 2; t.ColumnStyles.Clear();
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(220))); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 68)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 32)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var title = Title("Change log"); t.Controls.Add(title, 0, 0); t.SetColumnSpan(title, 2);
            Control note = Note("Every change Elixar makes to this PC: registry values (with the value before and after), files moved to Undo or deleted, Windows commands, apps, startup, security and settings. " +
                                "If something stops working after a change, find it here: registry values can be put back with one click, files from Tools > Undo. Passwords are never written here.");
            t.Controls.Add(note, 0, 1); t.SetColumnSpan(note, 2);

            var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, S(6)) };
            Func<string, Label> lab = s => new Label { AutoSize = true, Text = s, Margin = new Padding(S(2), S(6), S(4), 0) };
            logsKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(170), Margin = new Padding(0, S(2), S(14), 0) };
            logsKind.Items.AddRange(LogKinds); logsKind.SelectedIndex = 0;
            logsPeriod = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(140), Margin = new Padding(0, S(2), S(14), 0) };
            logsPeriod.Items.AddRange(LogPeriods); logsPeriod.SelectedIndex = 1;
            logsSearch = new TextBox { Width = S(240), Margin = new Padding(0, S(2), S(14), 0) };
            logsCount = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(2), S(6), 0, 0) };
            bar.Controls.AddRange(new Control[] { lab("Type:"), logsKind, lab("When:"), logsPeriod, lab("Search:"), logsSearch, logsCount });
            t.Controls.Add(bar, 0, 2); t.SetColumnSpan(bar, 2);

            // the sections: which part of Elixar made the change (Cleaner, Duplicates, Startup...), each with how many
            logsSections = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(26), BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, S(8), S(4)) };
            logsSections.DrawItem += (o, e) =>
            {
                if (e.Index < 0) return;
                var r = (Rubric)logsSections.Items[e.Index];
                bool sel = (e.State & DrawItemState.Selected) != 0;
                bool head = r.Key.StartsWith("*") || r.Key.StartsWith("#");
                Color back = sel ? Theme.Accent : logsSections.BackColor, ink = sel ? Color.White : head ? Theme.Ink : Theme.Ink;
                using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
                var rect = new Rectangle(e.Bounds.X + S(8), e.Bounds.Y, e.Bounds.Width - S(16), e.Bounds.Height);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | (Lang.Rtl ? TextFormatFlags.RightToLeft : 0);
                using (var f = head ? new Font(logsSections.Font, FontStyle.Bold) : new Font(logsSections.Font, FontStyle.Regular))
                    TextRenderer.DrawText(e.Graphics, Lang.T(r.Name), f, rect, ink, flags | (Lang.Rtl ? TextFormatFlags.Right : TextFormatFlags.Left));
                TextRenderer.DrawText(e.Graphics, r.Count.ToString(CultureInfo.CurrentCulture), logsSections.Font, rect, sel ? Color.White : Theme.Quiet, flags | (Lang.Rtl ? TextFormatFlags.Left : TextFormatFlags.Right));
                if (r.Key == "#deleted")   // a line under the three summaries, before the sections
                    using (var p = new Pen(Theme.Grid)) e.Graphics.DrawLine(p, e.Bounds.X + S(6), e.Bounds.Bottom - 1, e.Bounds.Right - S(6), e.Bounds.Bottom - 1);
            };
            logsSections.SelectedIndexChanged += (o, e) =>
            {
                if (logsSyncing || logsSections.SelectedItem == null) return;
                logsSection = ((Rubric)logsSections.SelectedItem).Key; LogsFill();
            };
            t.Controls.Add(logsSections, 0, 3); t.SetRowSpan(logsSections, 2);

            logsList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false, GridLines = false };
            logsList.Columns.Add("Time", S(70)); logsList.Columns.Add("Section", S(120)); logsList.Columns.Add("What happened", S(300));
            logsList.Columns.Add("Where", S(360)); logsList.Columns.Add("Before", S(150)); logsList.Columns.Add("After", S(150)); logsList.Columns.Add("Status", S(110));
            t.Controls.Add(logsList, 1, 3);
            logsDetail = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), Margin = new Padding(0, S(6), 0, S(4)), RightToLeft = Lang.Rtl ? RightToLeft.Yes : RightToLeft.No };
            t.Controls.Add(logsDetail, 1, 4);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            btnLogsUndo = MakeButton("Undo this change..."); btnLogsUndo.Font = new Font("Segoe UI Semibold", 9f); btnLogsUndo.Enabled = false;
            btnLogsCopy = MakeButton("Copy details"); btnLogsCopy.Enabled = false;
            btnLogsExport = MakeButton("Save the log as...");
            btnLogsFile = MakeButton("Open the log file");
            btnLogsText = MakeButton("Open the activity log");
            btnLogsRefresh = MakeButton("Refresh");
            buttons.Controls.AddRange(new Control[] { btnLogsUndo, btnLogsCopy, btnLogsExport, btnLogsFile, btnLogsText, btnLogsRefresh });
            t.Controls.Add(buttons, 0, 5); t.SetColumnSpan(buttons, 2);
            pageLogs.Controls.Add(t);

            var typing = new Timer { Interval = 300 };
            typing.Tick += (o, e) => { typing.Stop(); LogsFill(); };
            logsSearch.TextChanged += (o, e) => { typing.Stop(); typing.Start(); };
            logsKind.SelectedIndexChanged += (o, e) => LogsFill();
            logsPeriod.SelectedIndexChanged += (o, e) => LogsFill();
            logsList.SelectedIndexChanged += (o, e) => LogsShowSelected();
            logsList.DoubleClick += (o, e) => { if (btnLogsUndo.Enabled) LogsUndo(); };
            btnLogsUndo.Click += (o, e) => LogsUndo();
            btnLogsCopy.Click += (o, e) => { try { if (logsDetail.Text.Length > 0) Clipboard.SetText(logsDetail.Text); } catch { } };
            btnLogsRefresh.Click += (o, e) => LogsLoad();
            btnLogsExport.Click += (o, e) => LogsExport();
            // changes.txt: the same list in plain words, opened in Notepad (with your normal rights)
            btnLogsFile.Click += (o, e) =>
            {
                try
                {
                    ChangeLog.EnsureText();
                    if (File.Exists(ChangeLog.TextPath)) OpenLog(ChangeLog.TextPath); else Lang.Box(this, "Nothing changed yet.", "Elixar");
                }
                catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); }
            };
            btnLogsText.Click += (o, e) => { try { if (File.Exists(logFile)) OpenLog(logFile); else Lang.Box(this, "There is no activity log yet.", "Elixar"); } catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); } };
        }

        void OnLogsTab() { LogsLoad(); }

        public void LogsLoad()
        {
            logsAll = ChangeLog.Read();
            LogsFill();
        }

        // a registry value reads name first in the list, so it isn't cut off behind the long key
        static string LogWhere(ChangeLog.Entry e)
        {
            string t = e.Target ?? ""; int i = t.LastIndexOf("  >  ", StringComparison.Ordinal);
            return e.Kind == "Registry" && i > 0 ? t.Substring(i + 5) + "   \u00B7   " + t.Substring(0, i) : t;
        }

        // a startup switch reads just On or Off in the list (the bytes are in the details)
        static string Brief(string v)
        {
            v = v ?? "";
            return v.StartsWith("On  (") ? "On" : v.StartsWith("Off  (") ? "Off" : v;
        }

        static string LogTime(ChangeLog.Entry e) { var w = e.When; return w == DateTime.MinValue ? e.Time : w.ToString(Lang.On ? "d.M.yyyy HH:mm" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        static string LogLongTime(ChangeLog.Entry e)
        {
            var w = e.When; if (w == DateTime.MinValue) return e.Time;
            return Lang.On ? w.ToString("dddd, d.M.yyyy, HH:mm:ss", Ui.Culture) : w.ToString("dddd d MMMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);
        }
        // the day heading over the lines: Today, Yesterday, or the date
        public static string LogDay(DateTime d, DateTime today)
        {
            if (d.Date == today.Date) return Lang.T("Today");
            if (d.Date == today.Date.AddDays(-1)) return Lang.T("Yesterday");
            return Lang.On ? d.ToString("dddd, d.M.yyyy", Ui.Culture) : d.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);
        }

        void LogsFill()
        {
            if (logsList == null) return;
            string kind = logsKind.SelectedIndex <= 0 ? null : LogKinds[logsKind.SelectedIndex];
            DateTime from = logsPeriod.SelectedIndex == 0 ? DateTime.Now.AddDays(-7) : logsPeriod.SelectedIndex == 1 ? DateTime.Now.AddDays(-30) : DateTime.MinValue;
            string q = (logsSearch.Text ?? "").Trim();
            var pre = logsAll.Where(e => (kind == null || e.Kind == kind) && e.When >= from &&
                                           (q.Length == 0 || new[] { e.Action, e.Target, e.Before, e.After, e.Detail, e.Kind, Lang.T(e.Action ?? ""), Lang.T(e.Kind ?? ""), Lang.T(ChangeLog.Summary(e)) }
                                                .Any(s => (s ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0))).ToList();
            // the sections on the left, with how many changes each has under the other filters
            var rubrics = new List<Rubric>
            {
                new Rubric { Key = "*", Name = "All changes", Count = pre.Count },
                new Rubric { Key = "#undo", Name = "Can be undone", Count = pre.Count(e => ChangeLog.StatusOf(e, logsAll) == "Can be undone") },
                new Rubric { Key = "#deleted", Name = "Deleted", Count = pre.Count(ChangeLog.IsDeletion) },
            };
            foreach (var sec in ChangeLog.Sections)
            {
                int n = pre.Count(e => ChangeLog.SectionOf(e) == sec);
                if (n > 0 || sec == logsSection || logsAll.Any(e => ChangeLog.SectionOf(e) == sec)) rubrics.Add(new Rubric { Key = sec, Name = sec, Count = n });
            }
            if (!rubrics.Any(r => r.Key == logsSection)) logsSection = "*";
            logsSyncing = true;
            logsSections.BeginUpdate(); logsSections.Items.Clear();
            foreach (var r in rubrics) logsSections.Items.Add(r);
            logsSections.SelectedIndex = rubrics.FindIndex(r => r.Key == logsSection);
            logsSections.EndUpdate();
            logsSyncing = false;
            var shown = pre.Where(e => logsSection == "*" || (logsSection == "#undo" ? ChangeLog.StatusOf(e, logsAll) == "Can be undone" :
                                                              logsSection == "#deleted" ? ChangeLog.IsDeletion(e) : ChangeLog.SectionOf(e) == logsSection)).Take(5000).ToList();
            var selE = LogsSelected(); string sel = selE == null ? null : selE.Id;
            logsList.BeginUpdate();
            logsList.Items.Clear();
            logsList.Groups.Clear();
            var groups = new Dictionary<DateTime, ListViewGroup>();
            foreach (var e in shown)
            {
                string status = ChangeLog.StatusOf(e, logsAll);
                var day = e.When.Date; ListViewGroup grp;
                if (!groups.TryGetValue(day, out grp)) { grp = new ListViewGroup(LogDay(day, DateTime.Today)); groups[day] = grp; logsList.Groups.Add(grp); }
                string time = e.When == DateTime.MinValue ? e.Time : e.When.ToString("HH:mm", CultureInfo.InvariantCulture);
                var r = new ListViewItem(new[] { time, Lang.T(ChangeLog.SectionOf(e)), Lang.T(ChangeLog.Summary(e)), LogWhere(e), Lang.T(Brief(e.Before)), Lang.T(Brief(e.After)), Lang.T(status) }) { Tag = e, UseItemStyleForSubItems = false, Group = grp };
                if (status == "Failed") r.SubItems[6].ForeColor = Theme.MapFore(Color.FromArgb(190, 40, 40));
                else if (status == "Can be undone") r.SubItems[6].ForeColor = Theme.MapFore(Theme.Accent);
                else if (status == "Undone") r.SubItems[6].ForeColor = Theme.Quiet;
                logsList.Items.Add(r);
                if (e.Id == sel) r.Selected = true;
            }
            logsList.EndUpdate();
            logsCount.Text = Lang.T(logsAll.Count == 0 ? "Nothing changed yet." : shown.Count == 1 ? "1 change" : shown.Count + " changes") + (logsAll.Count > 0 && shown.Count != logsAll.Count ? "  " + Lang.T("(of " + logsAll.Count + ")") : "");
            LogsShowSelected();
        }

        ChangeLog.Entry LogsSelected()
        {
            if (logsList.SelectedItems.Count > 0) return (ChangeLog.Entry)logsList.SelectedItems[0].Tag;
            var r = logsList.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Selected);   // before the list is on screen
            return r == null ? null : (ChangeLog.Entry)r.Tag;
        }

        void LogsShowSelected()
        {
            var e = LogsSelected();
            btnLogsCopy.Enabled = e != null;
            if (e == null) { logsDetail.Text = Lang.T("Select a line to see everything about that change."); btnLogsUndo.Enabled = false; btnLogsUndo.Text = Lang.T("Undo this change..."); return; }
            string status = ChangeLog.StatusOf(e, logsAll);
            var sb = new StringBuilder();
            // \u200E keeps numbers and paths in their own order after a Hebrew label
            Action<string, string> line = (k, v) => { if (!string.IsNullOrEmpty(v)) sb.Append(Lang.T(k)).Append(": \u200E").Append(v).Append("\u200E\r\n"); };
            string sum = ChangeLog.Summary(e);
            line("What happened", Lang.T(sum));
            line("When", LogLongTime(e));
            line("Section", Lang.T(ChangeLog.SectionOf(e)) + (ChangeLog.IsDeletion(e) ? "  -  " + Lang.T("Deleted") : ""));
            line("Kind", Lang.T(e.Kind ?? "") + (sum != e.Action ? "  -  " + Lang.T(e.Action ?? "") : ""));
            line(e.Kind == "Windows command" ? "Command" : "Where", e.Target);
            if (e.Kind == "Registry" && e.CanUndo) line("Registry view", e.View == "Registry32" ? "32-bit" : e.View == "Registry64" ? "64-bit" : "default");
            line("Before", Lang.T(e.Before ?? ""));
            line("After", Lang.T(e.After ?? ""));
            line("Status", Lang.T(status));
            line("How to undo", Lang.T(ChangeLog.HowToUndo(e, status)));
            if (e.Detail != sum) line("Details", e.Detail);
            var undid = e.Ref == null ? null : logsAll.FirstOrDefault(x => x.Id == e.Ref);
            if (undid != null) line("Undid", LogTime(undid) + "  " + Lang.T(ChangeLog.Summary(undid)));
            var by = logsAll.FirstOrDefault(x => x.Ref == e.Id);
            if (by != null) line("Undone on", LogTime(by));
            line("Change ID", e.Id);
            logsDetail.Text = sb.ToString();

            bool inUndo = status == "In Undo";
            btnLogsUndo.Text = Lang.T(inUndo ? "Open Tools > Undo" : "Undo this change...");
            btnLogsUndo.Enabled = inUndo || status == "Can be undone";
        }

        void LogsUndo()
        {
            var e = LogsSelected();
            if (e == null) return;
            if (ChangeLog.StatusOf(e, logsAll) == "In Undo") { ShowUndo(); return; }
            if (!e.CanUndo || ChangeLog.StatusOf(e, logsAll) != "Can be undone") return;
            string now;
            try { now = RegLog.ChangedSince(e); } catch (Exception ex) { Lang.Box(this, Lang.T("The value could not be read: ") + ex.Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            string ask = Lang.T(e.Old == null ? "Remove this value again?" : "Put back the value it had before?") + "\n\n" + e.Target + "\n\n" +
                         Lang.T("Now: ") + (now ?? e.After) + "\n" + Lang.T("Back to: ") + (e.Old == null ? Lang.T("(not there)") : e.Before) + "\n\n" +
                         (now != null ? Lang.T("Careful: this value was changed again after Elixar's change.") + "\n\n" : "") +
                         Lang.T("Elixar writes the undo down as a change of its own, so you can undo it again.");
            var answer = LogsConfirmHook != null ? LogsConfirmHook(ask) : Lang.BoxNo(this, ask, "Elixar");
            if (answer != DialogResult.Yes) return;
            string err = RegLog.Undo(e);
            if (err != null) { Log(Lang.T("Undo didn't work: ") + err); if (LogsConfirmHook == null) Lang.Box(this, Lang.T("Undo didn't work: ") + err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            else Log(Lang.T("Undone: ") + e.Target);
            LogsLoad();
        }

        // a cell starting with = + - @ would run as a formula in Excel (a file name can start with one): it gets a ' first
        static string Csv(string s)
        {
            s = s ?? "";
            if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0) s = "'" + s;
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        public static string LogsCsv(List<ChangeLog.Entry> all)
        {
            var sb = new StringBuilder();
            sb.Append("Time,Section,Kind,What,Where,Before,After,Status,Details,Change ID\r\n");
            foreach (var e in all)
                sb.Append(string.Join(",", new[] { e.Time, ChangeLog.SectionOf(e), e.Kind, e.Action, e.Target, e.Before, e.After, ChangeLog.StatusOf(e, all), e.Detail, e.Id }.Select(Csv))).Append("\r\n");
            return sb.ToString();
        }

        // the readable version: oldest first, like changes.txt
        public static string LogsText(List<ChangeLog.Entry> all)
        {
            var sb = new StringBuilder(ChangeLog.TextHeader);
            foreach (var e in Enumerable.Reverse(all)) sb.Append(ChangeLog.Block(e, all));
            return sb.ToString();
        }

        void LogsExport()
        {
            using (var d = new SaveFileDialog { Filter = "Spreadsheet (*.csv)|*.csv|Text, easy to read (*.txt)|*.txt", FileName = "Elixar change log " + DateTime.Today.ToString("yyyy-MM-dd") + ".csv",
                                                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), OverwritePrompt = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                bool txt = d.FilterIndex == 2 || d.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                // the mark lets Excel and Notepad read Hebrew and other letters correctly
                string text = "\uFEFF" + (txt ? LogsText(logsAll).Replace("\n", "\r\n") : LogsCsv(logsAll));
                try { SafeFile.Save(d.FileName, text); }
                catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); return; }
                Log(Lang.T("Change log saved: ") + d.FileName);
            }
        }
    }
}
