// Self-test for 1.2.66: "Did it help?" results, how fast things come back, the clean when C: is low, Fix all safe,
// leftover installer files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class SelfTest1266
    {
        static void Pump(Func<bool> done, int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (!done() && DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(15); }
        }

        static void Run(Action<bool, string> check, string name, Func<string> test)
        {
            string why;
            try { why = test(); }
            catch (Exception ex)
            {
                var at = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Contains("CDriveCleaner.")).Take(4);
                why = "ERROR " + ex.GetType().Name + ": " + ex.Message + " @ " + string.Join(" < ", at);
            }
            check(why == null, name + (why == null ? "" : "  (" + why + ")"));
        }

        static readonly DateTime Now = new DateTime(2026, 11, 7, 12, 0, 0);
        static ChangeLog.Entry Startup(DateTime t, string detail) { return new ChangeLog.Entry { Time = t.ToString("s"), Kind = "Registry", Detail = detail }; }
        static BootTime Boot(DateTime t, double secs) { return new BootTime { When = t, Ms = (long)(secs * 1000) }; }

        public static void All(string root, Action<bool, string> check)
        {
            // ---------- 1. "Did it help?" ----------
            Run(check, "Did it help: space freed, Windows starting faster since start-up apps were turned off (before and after), PC health then and now; nothing that isn't real", () =>
            {
                var at = Now.AddDays(-20);
                var log = new List<ChangeLog.Entry>
                {
                    Startup(at, "Startup: turned off Spotify"), Startup(at.AddMinutes(1), "Startup: turned off Teams"),
                    Startup(at.AddMinutes(2), "Startup: turned off Zoom"), Startup(at.AddDays(1), "Startup: turned on Zoom"),   // turned back on: not counted
                    new ChangeLog.Entry { Time = at.ToString("s"), Detail = "Startup: turned off Skype", Failed = true },      // failed: not counted
                };
                var boots = new List<BootTime>();
                for (int i = 1; i <= 6; i++) boots.Add(Boot(at.AddDays(-i), 60));
                for (int i = 1; i <= 4; i++) boots.Add(Boot(at.AddDays(i * 3), 45));
                var health = new List<Tuple<DateTime, int>> { Tuple.Create(Now.AddDays(-60).Date, 71), Tuple.Create(Now.AddDays(-30).Date, 79), Tuple.Create(Now.Date, 86) };
                var sv = new SavingsTotal { Bytes = (long)(23.4 * (1L << 30)), ThisMonth = (long)(6.2 * (1L << 30)), Since = new DateTime(2026, 6, 3), Cleans = 12 };
                var r = Results.Compute(sv, log, boots, health, Now);
                if (r.StartupOff != 2) return "start-up apps off: " + r.StartupOff;
                if (Math.Abs(r.Faster - 15) > 0.01 || !r.ShowFaster) return "faster: " + r.Faster;
                if (r.HealthGain != 15) return "health " + r.HealthFirst + " -> " + r.HealthNow;
                string line = Results.Line(r, Now);
                foreach (var want in new[] { "23.4 GB freed since June", "This month: 6.2 GB", "Windows starts 15 s faster since you turned off 2 start-up apps", "PC health 71 \u2192 86" })
                    if (!line.Contains(want)) return "missing \"" + want + "\" in: " + line;
                // only one start after the change: too early to say
                var few = Results.Compute(sv, log, boots.Where(b => b.When < at).Concat(new[] { Boot(at.AddDays(2), 40) }).ToList(), health, Now);
                if (few.ShowFaster || Results.Line(few, Now).Contains("faster")) return "faster after one start";
                // slower (or within 2 s): not claimed
                var slow = Results.Compute(sv, log, boots.Select(b => b.When > at ? Boot(b.When, 61) : b).ToList(), health, Now);
                if (slow.ShowFaster) return "a slower start was shown as faster";
                // nothing yet
                var none = Results.Compute(new SavingsTotal(), new List<ChangeLog.Entry>(), new List<BootTime>(), new List<Tuple<DateTime, int>>(), Now);
                if (none.Any || Results.Line(none, Now) != "" || Results.Headline(none, Now) != "Nothing yet") return "nothing yet";
                if (Results.AfterClean(3L << 30, 12L << 30, 15L << 30, "C:") != "Done: 3.0 GB freed. C: went from 12.0 GB to 15.0 GB free.") return "after clean: " + Results.AfterClean(3L << 30, 12L << 30, 15L << 30, "C:");
                return null;
            });

            Run(check, "Did it help: Windows' boot times are kept (each once), the health score once a day", () =>
            {
                string kb = Results.BootFileOverride, kh = Results.HealthFileOverride;
                Results.BootFileOverride = Path.Combine(root, "boots-1266.txt"); Results.HealthFileOverride = Path.Combine(root, "health-1266.txt");
                try
                {
                    File.Delete(Results.BootFileOverride); File.Delete(Results.HealthFileOverride);
                    var b = new[] { Boot(Now.AddDays(-2), 50), Boot(Now.AddDays(-1), 40) };
                    Results.RecordBoots(b); Results.RecordBoots(b); Results.RecordBoots(new[] { Boot(Now, 30), Boot(Now.AddDays(-3), 0) });   // 0 = not a real time
                    var all = Results.ReadBoots();
                    if (all.Count != 3 || all.Sum(x => x.Ms) != 120000) return "boots: " + all.Count;
                    Results.RecordHealth(70, Now.AddDays(-1)); Results.RecordHealth(75, Now); Results.RecordHealth(80, Now.AddHours(1)); Results.RecordHealth(101, Now);
                    var h = Results.ReadHealth();
                    if (h.Count != 2 || h[1].Item2 != 80) return "health: " + string.Join(",", h.Select(x => x.Item2));
                    return null;
                }
                finally { Results.BootFileOverride = kb; Results.HealthFileOverride = kh; }
            });

            Run(check, "Did it help: the Dashboard shows it beside PC health on a wide window and under it on a narrow one; the weekly email has it too", () =>
            {
                bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                try
                {
                    using (var m = new MainForm(true))
                    {
                        m.Size = new System.Drawing.Size(1400, 860); m.Show(); Pump(() => false, 600);
                        if (m.ResultsCardForTest == null || !m.ResultsCardForTest.Visible) return "no card";
                        // the test PC's screen may be smaller than 1400 (the window then fits the screen): the rule is what counts
                        if (m.HealthWideForTest != (m.OverviewWidthForTest >= 1100)) return "side by side " + m.HealthWideForTest + " at " + m.OverviewWidthForTest + " px";
                        m.Size = new System.Drawing.Size(800, 600); Pump(() => false, 500);
                        if (m.HealthWideForTest) return "still side by side on a narrow window";
                        if (m.ResultsCardForTest.Height < 40 || m.ResultsCardForTest.Width < 300) return "too small when stacked: " + m.ResultsCardForTest.Size;
                        m.Close();
                    }
                }
                finally { MainForm.ForceWindowsUi = keepUi; }
                var keep = MailReports.ResultsOverride;
                MailReports.ResultsOverride = () => new ResultsSummary { Freed = 5L << 30, Since = new DateTime(2026, 6, 1), StartupOff = 1, BootBefore = 50, BootAfter = 40 };
                try
                {
                    string html = MailReports.Summary(50L << 30, 500L << 30, new List<CleanItem>(), null, null).Html();
                    if (!html.Contains("What Elixar did for you") || !html.Contains("5.0 GB freed since June") || !html.Contains("10 s faster")) return "email";
                }
                finally { MailReports.ResultsOverride = keep; }
                return null;
            });

            // ---------- 2. how fast things come back ----------
            Run(check, "Comes back: learned from the scans after a clean (back in ~2 days, slowly, still measuring), each clean sharpens it; the weekly clean leaves out what's back in a day or two", () =>
            {
                string keep = Regrowth.OverrideFile; Regrowth.OverrideFile = Path.Combine(root, "regrowth-1266.txt");
                try
                {
                    File.Delete(Regrowth.OverrideFile);
                    var t0 = Now.AddDays(-10);
                    Regrowth.Cleaned("Chrome cache", 1L << 30, t0); Regrowth.Cleaned("Windows Update leftovers", 4L << 30, t0); Regrowth.Cleaned("Temp files", 2L << 30, t0);
                    Regrowth.Cleaned("Tiny", 1000, t0);   // under 1 MB: nothing to learn
                    var d = Regrowth.Load();
                    if (Regrowth.Label(d, "Chrome cache", Now) != "measuring...") return "before any scan: " + Regrowth.Label(d, "Chrome cache", Now);
                    if (Regrowth.Label(d, "Tiny", Now) != "not cleaned yet" || Regrowth.Label(d, "Never", Now) != "not cleaned yet") return "unknown";
                    Regrowth.Observe(new Dictionary<string, long> { { "Chrome cache", 900L << 20 } }, t0.AddHours(6));   // 6 hours after: too soon, ignored
                    if (Regrowth.Load()["Chrome cache"].LastAt.HasValue) return "a scan 6 hours after counted";
                    Regrowth.Observe(new Dictionary<string, long> { { "Chrome cache", 900L << 20 }, { "Temp files", 200L << 20 } }, t0.AddDays(2));
                    d = Regrowth.Load();
                    if (Regrowth.Label(d, "Chrome cache", Now) != "in ~2 days" || !Regrowth.Fast(d, "Chrome cache")) return "chrome: " + Regrowth.Label(d, "Chrome cache", Now);
                    if (Regrowth.Label(d, "Temp files", Now) != "measuring..." || Regrowth.Fast(d, "Temp files")) return "temp after 2 days, 10% back: " + Regrowth.Label(d, "Temp files", Now);
                    Regrowth.Observe(new Dictionary<string, long> { { "Windows Update leftovers", 20L << 20 } }, t0.AddDays(16));
                    d = Regrowth.Load();
                    if (Regrowth.Label(d, "Windows Update leftovers", Now) != "slowly (weeks)" || Regrowth.Fast(d, "Windows Update leftovers")) return "slow: " + Regrowth.Label(d, "Windows Update leftovers", Now);
                    // a second round: what was learned stays and is averaged with the new one
                    Regrowth.Cleaned("Chrome cache", 1L << 30, t0.AddDays(3));
                    d = Regrowth.Load();
                    if (Math.Abs(d["Chrome cache"].Days - 2 * 0.8 * 1024 / 900) > 0.01) return "kept from round 1: " + d["Chrome cache"].Days;
                    Regrowth.Observe(new Dictionary<string, long> { { "Chrome cache", 1L << 30 } }, t0.AddDays(4));
                    double two = Regrowth.DaysFor(Regrowth.Load()["Chrome cache"]);
                    if (two < 1 || two > 1.6) return "round 2: " + two;
                    if (!Regrowth.Advice("Chrome cache").Contains("weekly clean leaves it out")) return "advice";
                    return null;
                }
                finally { Regrowth.OverrideFile = keep; }
            });

            Run(check, "Comes back: the Cleaner has the column and fills it after a scan", () =>
            {
                string keep = Regrowth.OverrideFile; Regrowth.OverrideFile = Path.Combine(root, "regrowth-1266b.txt");
                bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                try
                {
                    File.Delete(Regrowth.OverrideFile);
                    using (var m = new MainForm(true))
                    {
                        m.Show(); Pump(() => false, 300);
                        var cols = m.CleanerColumnsForTest;
                        if (cols.IndexOf("Comes back") != 4 || cols.Last() != "Location") return "columns: " + string.Join(", ", cols);
                        var it = m.CleanerItemsForTest.First();
                        Regrowth.Cleaned(it.Name, 1L << 30, DateTime.Now.AddDays(-3));
                        m.ShowRegrowth(false);
                        if (m.ComesBackText(it) != "measuring...") return "after a clean: " + m.ComesBackText(it);
                        if (m.ComesBackText(m.CleanerItemsForTest.Last()) != "not cleaned yet") return "never cleaned: " + m.ComesBackText(m.CleanerItemsForTest.Last());
                        m.Close();
                    }
                    return null;
                }
                finally { Regrowth.OverrideFile = keep; MainForm.ForceWindowsUi = keepUi; }
            });

            // ---------- 3. the clean when C: is low ----------
            Run(check, "Low space: with the clean by itself on, the space check starts the Safe clean first (once a day) and then warns only if C: is still low", () =>
            {
                var keepPrefs = TidyPrefs.Load(); var keepRun = Automation.RunTaskOverride;
                string ran = null;
                Automation.RunTaskOverride = task => { ran = task; return 0; };
                try
                {
                    var p = new TidyPrefs { LowClean = false }; p.Save();
                    if (Automation.StartLowClean() || ran != null) return "started while off";
                    p.LowClean = true; p.LastLowClean = ""; p.Save();
                    if (!Automation.StartLowClean() || ran != Automation.TidyTask) return "didn't start the daily tidy";
                    ran = null; p.LastLowClean = DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); p.Save();
                    if (Automation.StartLowClean() || ran != null) return "started twice in a day";
                    Automation.RunTaskOverride = task => 1; p.LastLowClean = ""; p.Save();
                    if (Automation.StartLowClean()) return "a task that didn't start counted";
                    bool still;
                    string ok = Automation.LowCleanNotice(5L << 30, 9L << 30, 100L << 30, 8, out still);
                    if (still || !ok.Contains("5.0 GB \u2192 9.0 GB free") || !ok.Contains("Nothing of yours was touched")) return "freed: " + ok;
                    string bad = Automation.LowCleanNotice(5L << 30, 6L << 30, 100L << 30, 8, out still);
                    if (!still || !bad.Contains("freed 1.0 GB") || !bad.Contains("still has only 6.0 GB")) return "still low: " + bad;
                    return null;
                }
                finally { keepPrefs.Save(); Automation.RunTaskOverride = keepRun; }
            });

            // ---------- 4. Fix all safe ----------
            Run(check, "Fix all safe: only the one-click fixes (restore point first, power plan), listed before it starts, then PC health is measured again", () =>
            {
                var r = Health.Evaluate(SelfTest1262.Troubled(), Now, new string[0]);
                var plan = Health.PlanSafe(r, false);
                if (!plan.Power || plan.Count != 2 || !plan.Worth) return "plan: power " + plan.Power + ", " + plan.Count;
                var lines = Health.PlanLines(plan);
                if (lines.Count != 2 || !lines[0].Contains("restore point") || !lines[1].Contains("Balanced")) return "lines: " + string.Join(" | ", lines);
                var ignored = Health.Evaluate(SelfTest1262.Troubled(), Now, new[] { "power" });
                if (Health.PlanSafe(ignored, false).Worth) return "an ignored finding was fixed";
                var fine = Health.Evaluate(SelfTest1262.Fine(), Now, new string[0]);
                if (Health.PlanSafe(fine, false).Worth) return "something to fix on a fine PC";
                var keepRp = CheckFix.RestorePointOverride; var keepPw = Health.SetPowerOverride;
                var order = new List<string>();
                CheckFix.RestorePointOverride = () => { order.Add("restore"); return "Restore point created."; };
                Health.SetPowerOverride = s => { order.Add("power"); return true; };
                try
                {
                    string done = Health.DoSafe(plan, null);
                    if (string.Join(",", order) != "restore,power") return "order: " + string.Join(",", order);
                    if (!done.Contains("restore point made") || !done.Contains("power plan Balanced")) return "done: " + done;
                    using (var f = new HealthForm(true, x => x, a => { }))
                    {
                        f.Show(); f.ShowReport(r); Pump(() => false, 200);
                        if (!f.FixAllShown || f.FixAllText != "Fix all safe (2)") return "button: " + f.FixAllShown + " " + f.FixAllText;
                        bool fixedNow = false; f.FixedInTest += () => fixedNow = true;
                        order.Clear(); f.FixAllSafe(); Pump(() => fixedNow, 5000);
                        if (!fixedNow || string.Join(",", order) != "restore,power") return "from the window: " + string.Join(",", order);
                        f.ShowReport(fine); Pump(() => false, 100);
                        if (f.FixAllShown) return "shown with nothing to fix";
                        f.Close();
                    }
                }
                finally { CheckFix.RestorePointOverride = keepRp; Health.SetPowerOverride = keepPw; }
                return null;
            });

            // ---------- 5. leftover installer files ----------
            Run(check, "Leftover installers: only .msi/.msp in the folder itself, not used by any program or update, not changed for 30 days; nothing offered when Windows' list doesn't match or Windows Installer is busy", () =>
            {
                string dir = Path.Combine(root, "Installer-1266"); if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(Path.Combine(dir, "$PatchCache$"));
                Func<string, int, int, string> make = (name, kb, days) => { string p = Path.Combine(dir, name); File.WriteAllBytes(p, new byte[kb * 1024]); File.SetLastWriteTime(p, DateTime.Now.AddDays(-days)); return p; };
                string used1 = make("1a2b3c.msi", 50, 400), orphan1 = make("4f5e6d.msp", 300, 400), orphan2 = make("9c8b7a.msi", 100, 90);
                make("recent.msi", 10, 5); make("notes.txt", 1, 400); make(Path.Combine("$PatchCache$", "old.msp"), 10, 400);
                var keepF = InstallerFiles.FolderOverride; var keepU = InstallerFiles.UsedOverride; var keepB = InstallerFiles.BusyOverride;
                InstallerFiles.FolderOverride = dir; InstallerFiles.BusyOverride = () => false;
                try
                {
                    InstallerFiles.UsedOverride = s => { s.Products = 3; return new HashSet<string> { used1.ToUpperInvariant(), @"C:\Elsewhere\x.msi" }; };   // any letter case
                    var r = InstallerFiles.Scan(DateTime.Now);
                    if (r.Problem != null) return "problem: " + r.Problem;
                    var names = r.Orphans.Select(o => Path.GetFileName(o.Path)).ToList();
                    if (string.Join(",", names) != "4f5e6d.msp,9c8b7a.msi" || r.Files != 4 || r.UsedHere != 1) return "found: " + string.Join(",", names) + " files " + r.Files + " used " + r.UsedHere;
                    InstallerFiles.UsedOverride = s => new HashSet<string> { @"C:\Windows\Installer\other.msi" };
                    r = InstallerFiles.Scan(DateTime.Now);
                    if (r.Problem == null || r.Orphans.Count != 0) return "a list that matches nothing was trusted";
                    InstallerFiles.UsedOverride = s => new HashSet<string> { used1 };
                    InstallerFiles.BusyOverride = () => true;
                    r = InstallerFiles.Scan(DateTime.Now);
                    if (r.Problem == null || r.Orphans.Count != 0) return "busy";
                    return null;
                }
                finally { InstallerFiles.FolderOverride = keepF; InstallerFiles.UsedOverride = keepU; InstallerFiles.BusyOverride = keepB; }
            });

            Run(check, "Leftover installers: checked again right before moving, moved to Undo (not deleted), and Undo puts them back exactly", () =>
            {
                string dir = Path.Combine(root, "Installer-1266m"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
                string uroot = Path.Combine(root, "undo-1266"); if (Directory.Exists(uroot)) Directory.Delete(uroot, true); Directory.CreateDirectory(uroot);
                Func<string, string> make = name => { string p = Path.Combine(dir, name); File.WriteAllBytes(p, new byte[20000]); File.SetLastWriteTime(p, DateTime.Now.AddDays(-100)); return p; };
                string a = make("a.msi"), b = make("b.msp"), c = make("c.msi");
                var keepF = InstallerFiles.FolderOverride; var keepU = InstallerFiles.UsedOverride; var keepB = InstallerFiles.BusyOverride; string keepQ = Quarantine.OverrideRoot;
                InstallerFiles.FolderOverride = dir; InstallerFiles.BusyOverride = () => false; Quarantine.OverrideRoot = uroot;
                var used = new HashSet<string> { a };
                InstallerFiles.UsedOverride = s => { s.Products = 1; return new HashSet<string>(used); };
                try
                {
                    var found = InstallerFiles.Scan(DateTime.Now).Orphans;
                    if (found.Count != 2) return "found " + found.Count;
                    used.Add(c);   // a program started using c.msi between the look and the move
                    long moved = InstallerFiles.MoveToUndo(found, null);
                    if (File.Exists(b) || !File.Exists(c) || !File.Exists(a) || moved != 20000) return "moved " + moved + ", b " + File.Exists(b) + ", c " + File.Exists(c);
                    var batch = Quarantine.List().FirstOrDefault(x => x.Label.StartsWith("Leftover installer files"));
                    if (batch == null) return "not in Undo";
                    Quarantine.Restore(batch, null);
                    if (!File.Exists(b) || new FileInfo(b).Length != 20000) return "Undo didn't put it back";
                    return null;
                }
                finally { InstallerFiles.FolderOverride = keepF; InstallerFiles.UsedOverride = keepU; InstallerFiles.BusyOverride = keepB; Quarantine.OverrideRoot = keepQ; }
            });

            if (Win.IsWindows)
                Run(check, "Leftover installers on this Windows PC (reads only): Windows Installer's list and the registry's agree with the folder, and nothing a program uses is offered", () =>
                {
                    var s = InstallerFiles.Scan(DateTime.Now);
                    if (s.Problem != null) return "problem: " + s.Problem + " (programs " + s.Products + ", files " + s.Files + ")";
                    if (s.Products == 0 || s.UsedHere == 0) return "programs " + s.Products + ", used here " + s.UsedHere;
                    if (s.Orphans.Any(o => !Path.GetDirectoryName(o.Path).Equals(InstallerFiles.Folder, StringComparison.OrdinalIgnoreCase))) return "a file outside the folder";
                    if (s.Orphans.Any(o => (DateTime.Now - o.Changed).TotalDays < InstallerFiles.MinAgeDays)) return "a recent file";
                    return null;
                });

            Run(check, "Leftover installers: the Tools page lists what's found, all ticked, and the policy can turn it off", () =>
            {
                string dir = Path.Combine(root, "Installer-1266t"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
                string p1 = Path.Combine(dir, "z.msi"); File.WriteAllBytes(p1, new byte[5000]); File.SetLastWriteTime(p1, DateTime.Now.AddDays(-60));
                var keepF = InstallerFiles.FolderOverride; var keepU = InstallerFiles.UsedOverride; var keepB = InstallerFiles.BusyOverride;
                bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                InstallerFiles.FolderOverride = dir; InstallerFiles.BusyOverride = () => false;
                InstallerFiles.UsedOverride = s => { s.Products = 2; return new HashSet<string> { p1, Path.Combine(dir, "gone.msi") }; };
                try
                {
                    if (Policy.Rule("installers") == null || Policy.Rule("installers").Default != "allow") return "policy rule";
                    using (var m = new MainForm(true))
                    {
                        m.Show(); Pump(() => false, 300);
                        if (!m.ToolsPageNamesForTest.Contains("Leftover installers")) return "no page";
                        bool done = false; m.InstallersFound += () => done = true;
                        File.WriteAllBytes(Path.Combine(dir, "y.msp"), new byte[7000]); File.SetLastWriteTime(Path.Combine(dir, "y.msp"), DateTime.Now.AddDays(-60));
                        m.FindInstallers(); Pump(() => done, 5000);
                        var s = m.LastInstallerScan;
                        if (!done || s == null || s.Orphans.Count != 1 || m.InstallerRowsForTest != 1 || m.InstallerCheckedForTest != 1) return "listed: " + (s == null ? "none" : s.Orphans.Count + " / rows " + m.InstallerRowsForTest);
                        m.Close();
                    }
                    return null;
                }
                finally { InstallerFiles.FolderOverride = keepF; InstallerFiles.UsedOverride = keepU; InstallerFiles.BusyOverride = keepB; MainForm.ForceWindowsUi = keepUi; }
            });
        }
    }
}

namespace CDriveCleaner
{
    public partial class MainForm
    {
        // self-test accessors (1.2.66)
        public ResultsCard ResultsCardForTest { get { return resultsCard; } }
        public bool HealthWideForTest { get { return healthWide == true; } }
        public int OverviewWidthForTest { get { return dashOverview == null ? 0 : dashOverview.ClientSize.Width; } }
        public List<string> CleanerColumnsForTest { get { return list.Columns.Cast<ColumnHeader>().Select(c => c.Text).ToList(); } }
        public List<CleanItem> CleanerItemsForTest { get { return items; } }
        public List<string> ToolsPageNamesForTest { get { return toolsTabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList(); } }
        public int InstallerRowsForTest { get { return instList.Items.Count; } }
        public int InstallerCheckedForTest { get { return instList.CheckedItems.Count; } }
    }
}

namespace CDriveCleaner
{
    public partial class MainForm
    {
        public void ShowCleanerForShot() { tabs.SelectedTab = pageClean; }   // previews
    }
}
