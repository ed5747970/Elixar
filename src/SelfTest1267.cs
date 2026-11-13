// Self-test for 1.2.67: battery health, hidden autostarts, the cookie keep-list.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class SelfTest1267
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

        static readonly DateTime Now = new DateTime(2026, 11, 13, 12, 0, 0);

        const string BatteryXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><BatteryReport xmlns=\"http://schemas.microsoft.com/battery/2012\"><Batteries>" +
            "<Battery><Id>DELL 1</Id><DesignCapacity>52600</DesignCapacity><FullChargeCapacity>41200</FullChargeCapacity><CycleCount>412</CycleCount></Battery>" +
            "</Batteries></BatteryReport>";

        public static void All(string root, Action<bool, string> check)
        {
            // ---------- battery health ----------
            Run(check, "Battery health: read from Windows' battery report; good / wearing / worth replacing; one reading a month and the 6-month trend", () =>
            {
                var b = Battery.Parse(BatteryXml);
                if (b == null || b.Percent != 78 || b.Cycles != 412) return "parse: " + (b == null ? "null" : b.Percent + "% " + b.Cycles);
                if (Battery.Parse("<BatteryReport><Batteries/></BatteryReport>") != null) return "a desktop has a battery";
                if (Battery.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><BatteryReport>&e;</BatteryReport>") != null) return "outside entities were read";
                var two = Battery.Parse(BatteryXml.Replace("</Batteries>", "<Battery><DesignCapacity>40000</DesignCapacity><FullChargeCapacity>20000</FullChargeCapacity></Battery></Batteries>"));
                if (two == null || two.Count != 2 || two.Percent != 66) return "two batteries: " + (two == null ? "null" : two.Percent.ToString());
                var hist = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("2026-04", 82), new KeyValuePair<string, int>("2026-08", 80) };
                var f = Battery.Finding(b, hist, Now);
                if (f == null || f.Level != HLevel.Info || f.Penalty != 0 || !f.Title.Contains("78%") || !f.Detail.Contains("412 charge cycles") || !f.Detail.Contains("down 4 points")) return "78%: " + (f == null ? "none" : f.Level + " " + f.Title + " / " + f.Detail);
                var worn = Battery.Finding(new BatteryInfo { DesignMWh = 50000, FullMWh = 25000, Count = 1 }, new List<KeyValuePair<string, int>>(), Now);
                if (worn.Level != HLevel.Warn || worn.Penalty != 5 || !worn.Detail.Contains("replacing")) return "50%: " + worn.Level;
                if (Battery.Finding(new BatteryInfo { DesignMWh = 50000, FullMWh = 49000, Count = 1 }, hist, Now).Level != HLevel.Ok) return "98%";
                if (Battery.Finding(null, hist, Now) != null) return "no battery";
                string hf = Path.Combine(root, "battery-1267.txt"); if (File.Exists(hf)) File.Delete(hf);
                Battery.HistoryFileOverride = hf;
                try
                {
                    Battery.Remember(b, Now); Battery.Remember(new BatteryInfo { DesignMWh = 100, FullMWh = 10, Count = 1 }, Now.AddDays(3));
                    Battery.Remember(b, Now.AddMonths(1));
                    var h = Battery.History();
                    if (h.Count != 2 || h[0].Key != "2026-11" || h[0].Value != 78 || h[1].Key != "2026-12") return "history: " + string.Join(",", h.Select(x => x.Key + "=" + x.Value));
                }
                finally { Battery.HistoryFileOverride = null; }
                // in PC health (Stability), only for a laptop
                var facts = new HealthFacts { Battery = b, BatteryHistory = hist, Free = 50L << 30, Total = 200L << 30 };
                if (!Health.Evaluate(facts, Now, new List<string>()).Items.Any(i => i.Key == "battery" && i.Area == "Stability" && i.Act == HAct.Battery)) return "not in PC health";
                facts.Battery = null;
                if (Health.Evaluate(facts, Now, new List<string>()).Items.Any(i => i.Key == "battery")) return "a desktop got a battery finding";
                return null;
            });

            // ---------- hidden autostarts ----------
            Run(check, "Hidden autostarts: tasks are read from Task Scheduler's files (when they run, on/off); unsigned from your folders or hidden scripts are flagged", () =>
            {
                string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                             "<Triggers><TimeTrigger><Repetition><Interval>PT1H</Interval></Repetition><StartBoundary>2026-01-01T00:00:00</StartBoundary></TimeTrigger><LogonTrigger/></Triggers>" +
                             "<Settings><Enabled>false</Enabled></Settings><Actions Context=\"Author\"><Exec><Command>C:\\Users\\you\\AppData\\Roaming\\upd\\run.exe</Command><Arguments>--quiet</Arguments></Exec></Actions></Task>";
                var e = Autostarts.ParseTask(xml, "\\Vendor\\UpdaterSvcHelper");
                if (e == null || e.Name != "UpdaterSvcHelper" || e.Kind != "Task" || e.Enabled || e.Runs != "Every hour" || !e.Command.EndsWith("--quiet") || e.WindowsOwn) return "parse: " + (e == null ? "null" : e.Name + " " + e.Runs + " " + e.Enabled);
                if (Autostarts.ParseTask(xml.Replace("<Settings><Enabled>false</Enabled></Settings>", "").Replace("<TimeTrigger><Repetition><Interval>PT1H</Interval></Repetition><StartBoundary>2026-01-01T00:00:00</StartBoundary></TimeTrigger>", ""), "\\Microsoft\\Windows\\X").Runs != "At sign-in") return "logon trigger";
                var ms = Autostarts.ParseTask(xml.Replace("C:\\Users\\you\\AppData\\Roaming\\upd\\run.exe", "C:\\Windows\\System32\\defrag.exe"), "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag");
                if (!ms.WindowsOwn) return "a Windows program in \\Microsoft\\ is Windows' own";
                // unwanted software hiding in Task Scheduler's \Microsoft\ folder is shown and checked
                if (Autostarts.ParseTask(xml, "\\Microsoft\\Windows\\Maintenance\\Updater").WindowsOwn) return "a program from AppData in \\Microsoft\\ was hidden";
                // Elixar's own: only its exact program (mentioning its folder isn't enough)
                Autostarts.ExeOverride = () => @"C:\Program Files\Elixar\Elixar.exe";
                try
                {
                    if (!Autostarts.IsElixar(@"C:\Program Files\Elixar\Elixar.exe", "--check-space 15")) return "Elixar's program";
                    if (Autostarts.IsElixar(@"C:\Users\you\AppData\evil.exe", @"C:\Program Files\Elixar\Elixar.exe")) return "a program that only names Elixar";
                    if (Autostarts.IsElixar(@"C:\Program Files\Elixar\other.exe", "")) return "another program in Elixar's folder";
                }
                finally { Autostarts.ExeOverride = null; }
                if (Autostarts.ParseTask("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><ComHandler><ClassId>{x}</ClassId></ComHandler></Actions></Task>", "\\A") != null) return "a COM task";
                // judging
                Func<string, string, string, AutoEntry> J = (file, cmd, sig) => { var a = new AutoEntry { Kind = "Task", Name = "x", File = file, Command = cmd, SigStatus = sig, Signer = sig == "Valid" ? "Contoso Ltd" : "" }; Autostarts.Judge(a); return a; };
                if (J(@"C:\Users\you\AppData\Roaming\upd\run.exe", "run.exe", "NotSigned").Flag != StartupFlag.Danger) return "unsigned in AppData";
                if (J(@"C:\ProgramData\fsrch\fsrch.exe", "fsrch.exe", "NotSigned").Flag != StartupFlag.Danger && Win.IsWindows) return "unsigned in ProgramData";
                if (J(@"C:\Program Files\Tool\tool.exe", "tool.exe", "NotSigned").Flag != StartupFlag.Review) return "unsigned in Program Files";
                if (J(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell.exe -w hidden -enc AAAA", "Valid").Flag != StartupFlag.Danger) return "a hidden script";
                if (J(@"C:\Users\you\AppData\Local\Zoom\bin\Zoom.exe", "Zoom.exe", "Valid").Flag != StartupFlag.Ok) return "a signed updater in AppData";
                var gone = new AutoEntry { Kind = "Task", Name = "Old", File = @"C:\Program Files\Old\tray.exe", Missing = true }; Autostarts.Judge(gone);
                if (gone.Flag != StartupFlag.Leftover) return "program gone";
                var mine = new AutoEntry { Kind = "Task", Name = "Elixar", File = "cmd.exe", ElixarOwn = true }; Autostarts.Judge(mine);
                if (mine.Flag != StartupFlag.Ok) return "Elixar's own task";
                return null;
            });

            Run(check, "Hidden autostarts: only apps' tasks are turned off or on (Task Scheduler's switch, logged); never Windows', Elixar's, services, odd names, or against the policy; PC health warns", () =>
            {
                var ran = new List<string>();
                Autostarts.RunOverride = (exe, args) => { ran.Add(exe + " " + args); return 0; };
                try
                {
                    var t = new AutoEntry { Kind = "Task", Name = "UpdaterSvcHelper", TaskPath = "\\Vendor\\UpdaterSvcHelper", Enabled = true };
                    if (Autostarts.SetEnabled(t, false) != null || t.Enabled || ran.Count != 1 || ran[0] != "schtasks.exe /Change /TN \"\\Vendor\\UpdaterSvcHelper\" /DISABLE") return "off: " + string.Join(" | ", ran);
                    if (Autostarts.SetEnabled(t, true) != null || !t.Enabled || !ran[1].EndsWith("/ENABLE")) return "on";
                    if (Autostarts.SetEnabled(new AutoEntry { Kind = "Task", TaskPath = "\\Microsoft\\Windows\\X", WindowsOwn = true }, false) == null) return "Windows' own";
                    if (Autostarts.SetEnabled(new AutoEntry { Kind = "Task", TaskPath = "\\TidyTask", ElixarOwn = true }, false) == null) return "Elixar's own";
                    if (Autostarts.SetEnabled(new AutoEntry { Kind = "Service", ItemPath = "fsrch" }, false) == null) return "a service";
                    if (Autostarts.SetEnabled(new AutoEntry { Kind = "Task", TaskPath = "\\a\" /DELETE /F /TN \"\\b" }, false) == null) return "a name with quotes";
                    if (Autostarts.SetEnabled(new AutoEntry { Kind = "Task", TaskPath = "Vendor\\x" }, false) == null) return "a name without the leading \\";
                    if (ran.Count != 2) return "ran: " + ran.Count;
                    Policy.OrgOverride = () => new Dictionary<string, string> { { "startup.change", "block" } };
                    try { if (Autostarts.SetEnabled(t, false) == null || ran.Count != 2) return "policy"; } finally { Policy.OrgOverride = null; }
                }
                finally { Autostarts.RunOverride = null; }
                var list = new List<AutoEntry>
                {
                    new AutoEntry { Kind = "Task", Enabled = true, Flag = StartupFlag.Danger },
                    new AutoEntry { Kind = "Task", Enabled = false, Flag = StartupFlag.Danger },
                    new AutoEntry { Kind = "Service", Enabled = true, Flag = StartupFlag.Danger, WindowsOwn = true },
                    new AutoEntry { Kind = "Service", Enabled = true, Flag = StartupFlag.Review }
                };
                if (Autostarts.Suspicious(list) != 1) return "suspicious: " + Autostarts.Suspicious(list);
                var f = new HealthFacts { AutostartsBad = 2, Free = 50L << 30, Total = 200L << 30 };
                var it = Health.Evaluate(f, Now, new List<string>()).Items.FirstOrDefault(i => i.Key == "autostarts");
                if (it == null || it.Area != "Security" || it.Act != HAct.Startup || !it.Title.Contains("2 hidden autostarts")) return "PC health";
                f.AutostartsBad = 0;
                if (Health.Evaluate(f, Now, new List<string>()).Items.Any(i => i.Key == "autostarts")) return "nothing suspicious, still a finding";
                return null;
            });

            if (Win.IsWindows)
                Run(check, "Hidden autostarts on this Windows PC (reads only): tasks and services are found, Windows' own are recognised", () =>
                {
                    var l = Autostarts.Read(false);
                    if (l.Count == 0) return "nothing found";
                    if (!l.Any(x => x.Kind == "Task") || !l.Any(x => x.Kind == "Service")) return "tasks " + l.Count(x => x.Kind == "Task") + ", services " + l.Count(x => x.Kind == "Service");
                    if (!l.Any(x => x.Kind == "Task" && x.WindowsOwn)) return "no Windows task recognised";
                    return null;
                });

            // ---------- cookie keep-list ----------
            Run(check, "Cookie keep-list: sites from cookie hosts (co.il, co.uk); the saved list accepts only real site names", () =>
            {
                var cases = new Dictionary<string, string> { { ".google.com", "google.com" }, { "accounts.google.com", "google.com" }, { "www.bank.co.il", "bank.co.il" },
                    { "login.example.co.uk", "example.co.uk" }, { "localhost", "localhost" }, { "192.168.1.1", "192.168.1.1" }, { "GitHub.COM", "github.com" } };
                foreach (var c in cases) if (CookieKeep.SiteOf(c.Key) != c.Value) return c.Key + " -> " + CookieKeep.SiteOf(c.Key);
                string lf = Path.Combine(root, "cookie-keep-1267.txt"); if (File.Exists(lf)) File.Delete(lf);
                CookieKeep.ListFileOverride = lf;
                try
                {
                    CookieKeep.Save(new[] { "google.com", "GitHub.com", "bad site", "x';drop", "", "bank.co.il", "google.com" });
                    var l = CookieKeep.Load();
                    if (string.Join(",", l) != "bank.co.il,github.com,google.com") return "saved: " + string.Join(",", l);
                    File.AppendAllText(lf, "\n../../etc\n%x%\n");
                    if (CookieKeep.Load().Count != 3) return "odd lines were read";
                }
                finally { CookieKeep.ListFileOverride = null; }
                return null;
            });

            Run(check, "Cookie keep-list: only other sites' cookies go (Chrome and Firefox), the original goes to Undo and comes back whole; sites are listed with sign-in suggestions", () =>
            {
                string dir = Path.Combine(root, "cookies-1267"); if (Directory.Exists(dir)) Directory.Delete(dir, true);
                string chrome = Path.Combine(dir, "Chrome", "Default", "Network"), ffp = Path.Combine(dir, "Firefox", "abc.default");
                Directory.CreateDirectory(chrome); Directory.CreateDirectory(ffp);
                string cdb = Path.Combine(chrome, "Cookies"), fdb = Path.Combine(ffp, "cookies.sqlite");
                using (var db = new SqliteDb(cdb, true))
                {
                    db.Exec("CREATE TABLE cookies (host_key TEXT, name TEXT, value TEXT)");
                    db.Exec("INSERT INTO cookies VALUES ('.google.com','SAPISID','1'),('accounts.google.com','__Secure-3PSID','2'),('.doubleclick.net','IDE','3'),('shop.example.com','cart','4'),('.github.com','user_session','5')");
                }
                using (var db = new SqliteDb(fdb, true))
                {
                    db.Exec("CREATE TABLE moz_cookies (host TEXT, name TEXT, value TEXT)");
                    db.Exec("INSERT INTO moz_cookies VALUES ('.youtube.com','PREF','1'),('.google.com','NID','2'),('.criteo.com','uid','3')");
                }
                var dbs = new List<CookieKeep.CookieDb>
                {
                    new CookieKeep.CookieDb { File = cdb, Browser = "Chrome", Table = "cookies", Column = "host_key" },
                    new CookieKeep.CookieDb { File = fdb, Browser = "Firefox", Table = "moz_cookies", Column = "host" }
                };
                string undo = Path.Combine(dir, "undo"); Directory.CreateDirectory(undo);
                CookieKeep.DbsOverride = () => dbs; CookieKeep.WorkOverride = Path.Combine(dir, "work"); Quarantine.OverrideRoot = undo;
                try
                {
                    List<string> problems;
                    var sites = CookieKeep.Sites(out problems);
                    if (problems.Count > 0) return "read: " + string.Join("; ", problems);
                    var g = sites.FirstOrDefault(s => s.Site == "google.com");
                    if (sites.Count != 6 || g == null || g.Count != 3 || !g.SignedIn || g.Browsers.Count != 2) return "sites: " + string.Join(",", sites.Select(s => s.Site + ":" + s.Count + (s.SignedIn ? "*" : "")));
                    if (!sites.First(s => s.Site == "doubleclick.net").Tracker || sites.First(s => s.Site == "doubleclick.net").SignedIn) return "tracker";
                    if (!sites.First(s => s.Site == "github.com").SignedIn || sites.First(s => s.Site == "example.com").SignedIn) return "sign-in guess";

                    var keep = new HashSet<string> { "google.com", "github.com" };
                    var log = new List<string>();
                    int removed = CookieKeep.Clean(dbs, keep, log.Add);
                    if (removed != 4) return "removed " + removed + ": " + string.Join(" | ", log);
                    Func<string, string, string, string> hosts = (f, table, col) => { using (var db = new SqliteDb(f, false)) return string.Join(",", db.Rows("SELECT rowid, " + col + " FROM " + table + " ORDER BY " + col).Select(r => r.Value)); };
                    if (hosts(cdb, "cookies", "host_key") != ".github.com,.google.com,accounts.google.com") return "Chrome left: " + hosts(cdb, "cookies", "host_key");
                    if (hosts(fdb, "moz_cookies", "host") != ".google.com") return "Firefox left: " + hosts(fdb, "moz_cookies", "host");
                    var batch = Quarantine.List().FirstOrDefault(b => b.Kind == "cookies");
                    if (batch == null || batch.Items.Count != 2) return "Undo: " + (batch == null ? "none" : batch.Items.Count.ToString());
                    if (Directory.Exists(Path.Combine(dir, "work")) && Directory.GetDirectories(Path.Combine(dir, "work")).Length > 0) return "work copies left";
                    // a cookie file with code in it (a trigger that would run with administrator rights) is never edited
                    string bad = Path.Combine(dir, "bad", "Cookies"); Directory.CreateDirectory(Path.GetDirectoryName(bad));
                    using (var db = new SqliteDb(bad, true))
                    {
                        db.Exec("CREATE TABLE cookies (host_key TEXT, name TEXT)"); db.Exec("CREATE TABLE log (x TEXT)");
                        db.Exec("CREATE TRIGGER t AFTER DELETE ON cookies BEGIN INSERT INTO log VALUES ('ran'); END");
                        db.Exec("INSERT INTO cookies VALUES ('.evil.example','a')");
                    }
                    var badLog = new List<string>();
                    if (CookieKeep.Clean(new[] { new CookieKeep.CookieDb { File = bad, Browser = "Chrome", Table = "cookies", Column = "host_key" } }, keep, badLog.Add) != 0 || !string.Join(" ", badLog).Contains("code in it")) return "a cookie file with a trigger: " + string.Join(" | ", badLog);
                    string viewDb = Path.Combine(dir, "bad", "Cookies2");
                    using (var db = new SqliteDb(viewDb, true)) { db.Exec("CREATE TABLE real (host_key TEXT, name TEXT)"); db.Exec("CREATE VIEW cookies AS SELECT * FROM real"); }
                    if (CookieKeep.Clean(new[] { new CookieKeep.CookieDb { File = viewDb, Browser = "Chrome", Table = "cookies", Column = "host_key" } }, keep, null) != 0) return "a view";
                    // nothing more to remove: nothing changes, no new Undo entry
                    if (CookieKeep.Clean(dbs, keep, null) != 0 || Quarantine.List().Count(b => b.Kind == "cookies") != 1) return "second clean";
                    Quarantine.Restore(batch, null);
                    if (hosts(cdb, "cookies", "host_key").Split(',').Length != 5 || hosts(fdb, "moz_cookies", "host").Split(',').Length != 3) return "Undo didn't bring them all back";
                    // the item in the Cleaner says it keeps sites
                    CookieKeep.ListFileOverride = Path.Combine(dir, "keep.txt"); CookieKeep.Save(keep);
                    var item = Privacy.Items().First(i => i.Name == "Cookies (Chrome, Edge, Brave)");
                    if (!item.Where.Contains("keeps 2 sites") || !item.Info.Contains("google.com")) return "item: " + item.Where;
                }
                finally { CookieKeep.DbsOverride = null; CookieKeep.WorkOverride = null; CookieKeep.ListFileOverride = null; Quarantine.OverrideRoot = null; }
                return null;
            });

            Run(check, "New pages: Startup lists the hidden autostarts (Windows' own hidden until asked); Sites to keep suggests signed-in sites and saves the list", () =>
            {
                bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                string dir = Path.Combine(root, "cookies-ui-1267"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
                string cdb = Path.Combine(dir, "Cookies");
                using (var db = new SqliteDb(cdb, true))
                {
                    db.Exec("CREATE TABLE cookies (host_key TEXT, name TEXT)");
                    db.Exec("INSERT INTO cookies VALUES ('.github.com','user_session'),('.doubleclick.net','IDE'),('shop.example.com','cart')");
                }
                Autostarts.ReadOverride = () => new List<AutoEntry>
                {
                    new AutoEntry { Kind = "Task", Name = "UpdaterSvcHelper", TaskPath = "\\Vendor\\UpdaterSvcHelper", File = @"C:\Users\you\AppData\Roaming\upd\run.exe", SigStatus = "NotSigned", Enabled = true, Runs = "Every hour" },
                    new AutoEntry { Kind = "Task", Name = "ScheduledDefrag", TaskPath = "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", File = @"C:\Windows\System32\defrag.exe", SigStatus = "Valid", Signer = "Microsoft Windows", WindowsOwn = true, Enabled = true }
                };
                CookieKeep.DbsOverride = () => new List<CookieKeep.CookieDb> { new CookieKeep.CookieDb { File = cdb, Browser = "Chrome", Table = "cookies", Column = "host_key" } };
                CookieKeep.WorkOverride = Path.Combine(dir, "work"); CookieKeep.ListFileOverride = Path.Combine(dir, "keep.txt");
                try
                {
                    using (var m = new MainForm(true))
                    {
                        m.Show(); Pump(() => false, 300);
                        bool done = false; m.AutostartsDone += () => done = true;
                        m.RefreshAutostarts(); Pump(() => done, 5000);
                        if (!done || m.AutoRowsForTest != 1) return "autostarts rows: " + m.AutoRowsForTest;
                        m.AutoShowWindowsForTest = true;
                        if (m.AutoRowsForTest != 2) return "with Windows' own: " + m.AutoRowsForTest;
                        // the cookie window
                        string err = null;
                        var t = new System.Windows.Forms.Timer { Interval = 50 };
                        bool loaded = false;
                        t.Tick += (o, e) =>
                        {
                            var f = m.CookieKeepForTest; if (f == null) return;
                            if (!f.LoadedForTest) return;   // wait until it has read the cookies (whenever that was)
                            t.Stop();
                            loaded = true;
                            if (f.RowsForTest != 3) err = "rows " + f.RowsForTest;
                            else if (string.Join(",", f.TickedForTest) != "github.com") err = "suggested: " + string.Join(",", f.TickedForTest);
                            f.SaveForTest();
                        };
                        t.Start();
                        m.ShowCookieKeep();
                        t.Dispose();
                        if (!loaded) return "the window didn't load";
                        if (err != null) return err;
                        if (string.Join(",", CookieKeep.Load()) != "github.com") return "saved: " + string.Join(",", CookieKeep.Load());
                        m.Close();
                    }
                    return null;
                }
                finally { Autostarts.ReadOverride = null; CookieKeep.DbsOverride = null; CookieKeep.WorkOverride = null; CookieKeep.ListFileOverride = null; MainForm.ForceWindowsUi = keepUi; }
            });
        }
    }
}

namespace CDriveCleaner
{
    public partial class MainForm
    {
        public int AutoRowsForTest { get { return autoList.Items.Count; } }
        public bool AutoShowWindowsForTest { set { chkAutoWindows.Checked = value; } }
    }
}
