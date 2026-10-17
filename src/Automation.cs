// Low-space alert and weekly auto-clean, run by Windows Task Scheduler.
//   --check-space N : shows a notification when C: has less than N% free (no admin needed)
//   --auto-clean    : cleans the default Safe items (needs admin; the task runs with highest rights)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace CDriveCleaner
{
    public class TaskState
    {
        public bool Exists;
        public string Arguments = "";
        public string Command = "";
        public DateTime? Start;
        public string Day = "";   // for weekly tasks, e.g. "Sunday"
        public bool Enabled = true;
    }

    public static class Automation
    {
        public const string Folder = @"Elixar\";
        public const string CheckTask = Folder + "Low space check";
        public const string LogonTask = Folder + "Low space check at sign-in";
        public const string CleanTask = Folder + "Weekly clean";
        public const string MailTask = Folder + "Email reports";
        // schedules saved before the rename (version 1.3/1.4 was called "C Drive Cleaner")
        public const string OldFolder = @"C Drive Cleaner\";
        public static readonly string[] OldTasks = { OldFolder + "Low space check", OldFolder + "Low space check at sign-in", OldFolder + "Weekly clean" };
        public static readonly string[] Days = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

        static string BaseDir { get { return Paths.Data; } }
        static string LogFile { get { return Path.Combine(BaseDir, "cleaner-log.txt"); } }
        public static string HistoryFile { get { return Path.Combine(BaseDir, "auto-clean-history.txt"); } }

        public static void Log(string m)
        {
            try { File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [auto] " + m + Environment.NewLine); } catch { }
        }

        // ---------- pure helpers (tested by the self-test) ----------
        public static bool ShouldWarn(long free, long total, int percent)
        {
            if (total <= 0) return false;
            return free * 100.0 / total < percent;
        }

        static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        public static string CreateCheckArgs(string exe, int percent, string hhmm)
        {
            return "/Create /F /TN " + Quote(CheckTask) + " /TR " + Quote("\"" + exe + "\" --check-space " + percent) + " /SC DAILY /ST " + hhmm;
        }

        public static string CreateLogonArgs(string exe, int percent)
        {
            return "/Create /F /TN " + Quote(LogonTask) + " /TR " + Quote("\"" + exe + "\" --check-space " + percent) + " /SC ONLOGON /DELAY 0003:00";
        }

        public static string CreateCleanArgs(string exe, string day, string hhmm)
        {
            return "/Create /F /TN " + Quote(CleanTask) + " /TR " + Quote("\"" + exe + "\" --auto-clean") +
                   " /SC WEEKLY /D " + day.Substring(0, 3).ToUpperInvariant() + " /ST " + hhmm + " /RL HIGHEST";
        }

        // runs as administrator: the Google sign-in is kept in a folder only administrators can open
        public static string CreateMailArgs(string exe, string hhmm)
        {
            return "/Create /F /TN " + Quote(MailTask) + " /TR " + Quote("\"" + exe + "\" --mail-daily") + " /SC DAILY /ST " + hhmm + " /RL HIGHEST";
        }

        public static string DeleteArgs(string task) { return "/Delete /F /TN " + Quote(task); }

        public static int PercentFromArgs(string args, int fallback)
        {
            var parts = (args ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                int v;
                if (parts[i] == "--check-space" && int.TryParse(parts[i + 1], out v) && v > 0 && v < 100) return v;
            }
            return fallback;
        }

        public static List<Tuple<DateTime, long>> ReadHistory(string file)
        {
            var list = new List<Tuple<DateTime, long>>();
            try
            {
                if (!File.Exists(file)) return list;
                foreach (var line in File.ReadAllLines(file))
                {
                    var p = line.Split('\t');
                    DateTime t; long b;
                    if (p.Length == 2 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && long.TryParse(p[1], out b))
                        list.Add(Tuple.Create(t, b));
                }
            }
            catch { }
            return list;
        }

        public static TaskState ParseTaskXml(string xml)
        {
            var st = new TaskState();
            if (string.IsNullOrWhiteSpace(xml) || xml.IndexOf("<Task", StringComparison.Ordinal) < 0) return st;
            var doc = new XmlDocument();
            doc.LoadXml(xml.Substring(xml.IndexOf("<", StringComparison.Ordinal)));
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            st.Exists = true;
            var cmd = doc.SelectSingleNode("//t:Exec/t:Command", ns);
            if (cmd != null) st.Command = cmd.InnerText.Trim().Trim('"');
            var args = doc.SelectSingleNode("//t:Exec/t:Arguments", ns);
            if (args != null) st.Arguments = args.InnerText;
            var en = doc.SelectSingleNode("//t:Settings/t:Enabled", ns);
            if (en != null) st.Enabled = en.InnerText.Trim().ToLowerInvariant() != "false";
            var start = doc.SelectSingleNode("//t:CalendarTrigger/t:StartBoundary", ns);
            DateTime d;
            if (start != null && DateTime.TryParse(start.InnerText, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) st.Start = d;
            var days = doc.SelectSingleNode("//t:ScheduleByWeek/t:DaysOfWeek", ns);
            if (days != null && days.FirstChild != null) st.Day = days.FirstChild.LocalName;
            return st;
        }

        // ---------- Task Scheduler ----------
        public static int RunSchtasks(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(Win.SystemExe("schtasks.exe"), args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } output = "timed out"; return -1; }
                    output = (o.Result + " " + e.Result).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        public static TaskState Query(string task)
        {
            string xml;
            if (RunSchtasks("/Query /XML /TN " + Quote(task), out xml) != 0) return new TaskState();
            try { return ParseTaskXml(xml); } catch { return new TaskState { Exists = true }; }
        }

        // ---------- what the scheduled runs do ----------
        public static void Notify(string title, string text, ToolTipIcon kind, bool openOnClick)
        {
            using (var icon = new NotifyIcon())
            {
                try { icon.Icon = Brand.Load(16) ?? SystemIcons.Information; } catch { icon.Icon = SystemIcons.Information; }
                icon.Text = "Elixar";
                icon.Visible = true;
                var timer = new Timer { Interval = 20000 };
                Action quit = () => { timer.Stop(); icon.Visible = false; Application.ExitThread(); };
                timer.Tick += (o, e) => quit();
                icon.BalloonTipClicked += (o, e) =>
                {
                    if (openOnClick) { try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true }); } catch { } }
                    quit();
                };
                icon.BalloonTipClosed += (o, e) => quit();
                icon.ShowBalloonTip(15000, title, text, kind);
                timer.Start();
                Application.Run();
                icon.Visible = false;
            }
        }

        public static int CheckSpace(int percent)
        {
            long total = Sys.Total(), free = Sys.Free();
            SpaceHistory.Record(free, total);   // one reading a day for the space trend
            CheckDisks();
            if (!ShouldWarn(free, total, percent))
            {
                Log("Space check: " + Fs.Fmt(free) + " free (" + Math.Round(free * 100.0 / Math.Max(1, total)) + "%). No alert needed (limit " + percent + "%).");
                return 0;
            }
            Log("Space check: only " + Fs.Fmt(free) + " free. Showing the alert.");
            Notify("Drive C: is almost full",
                "Only " + Fs.Fmt(free) + " free (" + Math.Round(free * 100.0 / total) + "%). Click to open Elixar.",
                ToolTipIcon.Warning, true);
            return 0;
        }

        // read only; runs with the same rights as the space check
        static void CheckDisks()
        {
            try
            {
                var r = DiskHealth.Read();
                if (r.Error != null) { Log("Disk health: " + r.Error); return; }
                Log("Disk health: " + r.Summary);
                var w = r.Worst;
                if (!DiskHealth.ShouldAlert(w, Settings.Get("DiskAlert", ""), DateTime.Today)) return;
                Settings.Set("DiskAlert", DiskHealth.Stamp(w, DateTime.Today));
                var d = r.Disks.First(x => x.Level == w);
                Notify(w == HealthLevel.Failing ? "A drive may fail soon: back up now" : "A drive needs attention",
                       d.Label + ": " + (d.Reasons.Count > 0 ? d.Reasons[0] : DiskHealth.LevelText(w)) + " Click to open Elixar.", w == HealthLevel.Failing ? ToolTipIcon.Error : ToolTipIcon.Warning, true);
            }
            catch (Exception e) { Log("Disk health check failed: " + e.Message); }
        }

        public static List<CleanItem> AutoItems()
        {
            // only the Safe items that are ticked by default; never Caution, Review, DISM or browser caches
            return Catalog.Build().Where(i => i.Tier == Tier.Safe && i.DefaultOn).ToList();
        }

        public static int AutoClean(bool notify)
        {
            if (!Sys.IsAdmin()) { Log("Weekly clean skipped: it needs administrator rights."); return 1; }
            Log("Weekly clean started.");
            long start = Sys.Free();
            var results = new List<MailReports.CleanResult>();
            foreach (var it in AutoItems())
            {
                if (it.Procs.Length > 0 && Sys.FindProcesses(it.Procs).Count > 0)
                {
                    Log("  Skipped " + it.Name + " (the app is open; it is never closed automatically).");
                    results.Add(new MailReports.CleanResult { Name = it.Name, Skipped = Lang.Hebrew ? "האפליקציה הייתה פתוחה" : "the app was open" });
                    continue;
                }
                long before = Sys.Free();
                try { it.Clean(m => Log(m)); } catch (Exception ex) { Log("  Problem with " + it.Name + ": " + ex.Message); }
                long got = Math.Max(0, Sys.Free() - before);
                results.Add(new MailReports.CleanResult { Name = it.Name, Freed = got });
                Log("  " + it.Name + ": freed " + Fs.Fmt(got));
            }
            long freed = Math.Max(0, Sys.Free() - start);
            Quarantine.PurgeOld(m => Log(m));
            Log("Weekly clean done. Freed " + Fs.Fmt(freed) + ".");
            try { File.AppendAllText(HistoryFile, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\t" + freed + Environment.NewLine); } catch { }
            try
            {
                if (MailAccount.Email != null && MailPrefs.Load().CleanReport)
                {
                    string e = MailReports.CleanReport(results, freed, Sys.Free(), Sys.Total(), MailReports.Measure()).Send();
                    Log("Email: weekly clean report " + (e == null ? "sent." : "not sent: " + e));
                }
            }
            catch (Exception ex) { Log("Email: weekly clean report not sent: " + ex.Message); }
            if (notify)
                Notify("Weekly clean finished", "Freed " + Fs.Fmt(freed) + " on C:. " + Fs.Fmt(Sys.Free()) + " is free now.", ToolTipIcon.Info, false);
            return 0;
        }
    }

    public partial class MainForm
    {
        TabPage pageAuto;
        CheckBox chkAlert, chkLogon, chkClean;
        NumericUpDown numPercent;
        DateTimePicker timeCheck, timeClean;
        ComboBox cmbDay;
        Label lblAlertState, lblCleanState, lblAutoItems;
        Button btnSaveAuto, btnTestAlert, btnRunAuto;

        void BuildAutomation()
        {
            pageAuto = new TabPage("Automation") { BackColor = Color.FromArgb(246, 248, 251) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(S(16), S(12), S(16), S(12)), AutoScroll = true };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Text = "Automation", Margin = new Padding(0, 0, 0, S(2)) };
            var intro = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(900), 0), Margin = new Padding(S(1), 0, 0, S(12)),
                Text = "These run through Windows Task Scheduler, even when this program is closed. If you move the program folder, open this tab and click Save again."
            };

            // --- alert
            var gAlert = new GroupBox { Text = "Low-space alert", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(S(12), S(8), S(12), S(10)), Margin = new Padding(0, 0, 0, S(12)), MinimumSize = new Size(S(760), 0) };
            var fa = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var r1 = Row();
            chkAlert = new CheckBox { AutoSize = true, Text = "Warn me when C: has less than", Margin = new Padding(0, S(4), S(4), 0) };
            numPercent = new NumericUpDown { Minimum = 5, Maximum = 50, Value = 15, Width = S(56) };
            r1.Controls.AddRange(new Control[] { chkAlert, numPercent, Lbl("% free") });
            var r2 = Row();
            timeCheck = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = S(70), Value = DateTime.Today.AddHours(12) };
            chkLogon = new CheckBox { AutoSize = true, Checked = true, Text = "and a few minutes after I sign in", Margin = new Padding(S(10), S(4), 0, 0) };
            r2.Controls.AddRange(new Control[] { Lbl("Check every day at", S(22)), timeCheck, chkLogon });
            lblAlertState = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(22), S(6), 0, 0), Text = "Checking..." };
            fa.Controls.AddRange(new Control[] { r1, r2, lblAlertState });
            gAlert.Controls.Add(fa);

            // --- weekly clean
            var gClean = new GroupBox { Text = "Weekly auto-clean", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(S(12), S(8), S(12), S(10)), Margin = new Padding(0, 0, 0, S(12)), MinimumSize = new Size(S(760), 0) };
            var fc = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var r3 = Row();
            chkClean = new CheckBox { AutoSize = true, Text = "Clean the Safe items automatically every", Margin = new Padding(0, S(4), S(4), 0) };
            cmbDay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(110) };
            cmbDay.Items.AddRange(Automation.Days);
            cmbDay.SelectedIndex = 6;   // Sunday (by position: the names are translated in Hebrew)
            timeClean = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = S(70), Value = DateTime.Today.AddHours(20) };
            r3.Controls.AddRange(new Control[] { chkClean, cmbDay, Lbl("at"), timeClean });
            lblAutoItems = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(860), 0), Margin = new Padding(S(22), S(6), 0, 0),
                Text = "Cleans: " + string.Join(", ", Automation.AutoItems().Select(i => i.Name)) +
                       ".\nNever: Caution items, Recycle Bin, browser caches or DISM. Open apps are skipped, never closed. A notification shows what was freed."
            };
            lblCleanState = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(22), S(6), 0, 0), Text = "Checking..." };
            fc.Controls.AddRange(new Control[] { r3, lblAutoItems, lblCleanState });
            gClean.Controls.Add(fc);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            btnSaveAuto = MakeButton("Save");
            btnSaveAuto.Font = new Font("Segoe UI Semibold", 9.5f);
            btnTestAlert = MakeButton("Show a test alert");
            btnRunAuto = MakeButton("Run the weekly clean now");
            buttons.Controls.AddRange(new Control[] { btnSaveAuto, btnTestAlert, btnRunAuto });

            root.Controls.Add(title, 0, 0);
            root.Controls.Add(intro, 0, 1);
            root.Controls.Add(gAlert, 0, 2);
            root.Controls.Add(gClean, 0, 3);
            root.Controls.Add(buttons, 0, 4);
            pageAuto.Controls.Add(root);

            if (testMode) btnSaveAuto.Enabled = btnRunAuto.Enabled = false;
            btnSaveAuto.Click += (o, e) => SaveAutomation();
            btnTestAlert.Click += (o, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--test-alert") { UseShellExecute = false });
                    Log("Test alert shown.");
                }
                catch (Exception ex) { Log("Could not show the test alert: " + ex.Message); }
            };
            btnRunAuto.Click += (o, e) =>
            {
                if (testMode) return;
                if (Lang.Box(this, "Run the weekly clean now?\n\nIt cleans: " + string.Join(", ", Automation.AutoItems().Select(i => i.Name)) + ".",
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                SetBusy(true);
                btnRunAuto.Enabled = false;
                System.Threading.Tasks.Task.Factory.StartNew(() => Automation.AutoClean(false)).ContinueWith(t => UI(() =>
                {
                    btnRunAuto.Enabled = true;
                    SetBusy(false);
                    Log("Weekly clean ran now. Details are in cleaner-log.txt.");
                    LoadAutomationState();
                    StartMeasure(() => RefreshDashboard());
                }));
            };
        }

        FlowLayoutPanel Row() { return new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(2), 0, S(2)) }; }
        Label Lbl(string t, int left = 0) { return new Label { AutoSize = true, Text = t, Margin = new Padding(left, S(6), S(6), 0) }; }

        public void LoadAutomationState()
        {
            System.Threading.Tasks.Task.Factory.StartNew(() => new[] { Automation.Query(Automation.CheckTask), Automation.Query(Automation.LogonTask), Automation.Query(Automation.CleanTask),
                                                                        Automation.Query(Automation.OldTasks[0]), Automation.Query(Automation.OldTasks[1]), Automation.Query(Automation.OldTasks[2]) })
            .ContinueWith(t => UI(() =>
            {
                if (t.IsFaulted) { lblAlertState.Text = lblCleanState.Text = "Could not read Task Scheduler."; return; }
                var check = t.Result[0]; var logon = t.Result[1]; var clean = t.Result[2];
                bool old = t.Result[3].Exists || t.Result[4].Exists || t.Result[5].Exists;
                bool fromOld = old && !check.Exists && !logon.Exists && !clean.Exists;
                if (fromOld) { check = t.Result[3]; logon = t.Result[4]; clean = t.Result[5]; }   // show the old settings so Save moves them
                chkAlert.Checked = check.Exists || logon.Exists;
                chkLogon.Checked = logon.Exists || !check.Exists;
                int pct = Automation.PercentFromArgs(check.Exists ? check.Arguments : logon.Arguments, 15);
                numPercent.Value = Math.Max(numPercent.Minimum, Math.Min(numPercent.Maximum, pct));
                if (check.Start.HasValue) timeCheck.Value = DateTime.Today.Add(check.Start.Value.TimeOfDay);
                lblAlertState.Text = !chkAlert.Checked ? "Off." :
                    "On: warns under " + pct + "% free" + (check.Exists ? ", checks daily at " + timeCheck.Value.ToString("HH:mm") : "") +
                    (logon.Exists ? (check.Exists ? " and" : ", checks") + " after sign-in" : "") + "." + (check.Enabled ? "" : " (disabled in Task Scheduler)");

                chkClean.Checked = clean.Exists;
                if (clean.Day.Length > 0 && Automation.Days.Contains(clean.Day)) cmbDay.SelectedIndex = Array.IndexOf(Automation.Days, clean.Day);
                if (clean.Start.HasValue) timeClean.Value = DateTime.Today.Add(clean.Start.Value.TimeOfDay);
                var hist = Automation.ReadHistory(Automation.HistoryFile);
                string last = hist.Count == 0 ? "Not run yet." :
                    "Last run " + hist.Last().Item1.ToString("d MMM HH:mm") + ", freed " + Fs.Fmt(hist.Last().Item2) +
                    ".  All runs: " + hist.Count + ", " + Fs.Fmt(hist.Sum(h => h.Item2)) + " freed in total.";
                if (fromOld)
                {
                    lblAlertState.Text = "Found schedules saved under the old name (C Drive Cleaner). Click Save to move them to Elixar.";
                    lblAlertState.ForeColor = CautionColor;
                }
                else if (old) { lblAlertState.Text += "  Old C Drive Cleaner schedules are still there; Save removes them."; }
                var unsafeTasks = new[] { check, logon, clean }.Where(x => x.Exists && !Install.IsProtectedExe(x.Command)).ToList();
                if (unsafeTasks.Count > 0)
                {
                    lblAlertState.Text += "  Not protected: a schedule starts Elixar from a folder other programs can change. Click Save to secure it.";
                    lblAlertState.ForeColor = CautionColor;
                }
                lblCleanState.Text = (clean.Exists ? "On: every " + cmbDay.SelectedItem + " at " + timeClean.Value.ToString("HH:mm") + ".  " : "Off.  ") + last;
            }));
        }

        void SaveAutomation()
        {
            if (testMode) return;
            // schedules always start the protected copy in Program Files, which only administrators can change
            string exe = Application.ExecutablePath;
            if (chkAlert.Checked || chkClean.Checked)
            {
                string ierr = Install.Ensure();
                if (ierr != null)
                {
                    Lang.Box(this, "Elixar could not put its protected copy in " + Install.Dir + ":\n" + ierr + "\n\nThe schedules were not changed.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                exe = Install.Exe;
                Log("Protected copy ready: " + exe);
            }
            var errors = new List<string>();
            Action<string, string> run = (what, args) =>
            {
                string outp;
                int code = Automation.RunSchtasks(args, out outp);
                if (code != 0 && !args.StartsWith("/Delete")) errors.Add(what + ": " + outp);
                Log((code == 0 ? "Saved: " : "Could not save: ") + what + (code == 0 ? "" : " (" + outp + ")"));
            };

            int pct = (int)numPercent.Value;
            if (chkAlert.Checked)
            {
                run("daily space check", Automation.CreateCheckArgs(exe, pct, timeCheck.Value.ToString("HH:mm")));
                if (chkLogon.Checked) run("space check at sign-in", Automation.CreateLogonArgs(exe, pct));
                else run("remove sign-in check", Automation.DeleteArgs(Automation.LogonTask));
            }
            else
            {
                run("remove daily space check", Automation.DeleteArgs(Automation.CheckTask));
                run("remove sign-in check", Automation.DeleteArgs(Automation.LogonTask));
            }
            if (chkClean.Checked) run("weekly clean", Automation.CreateCleanArgs(exe, Automation.Days[Math.Max(0, cmbDay.SelectedIndex)], timeClean.Value.ToString("HH:mm")));
            else run("remove weekly clean", Automation.DeleteArgs(Automation.CleanTask));
            // clean up schedules left from the old name
            foreach (var oldTask in Automation.OldTasks)
            {
                string o2;
                if (Automation.RunSchtasks(Automation.DeleteArgs(oldTask), out o2) == 0) Log("Removed old schedule: " + oldTask);
            }

            LoadAutomationState();
            if (errors.Count > 0)
                Lang.Box(this, "Some settings could not be saved:\n\n" + string.Join("\n", errors), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else
                Lang.Box(this, "Saved.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
