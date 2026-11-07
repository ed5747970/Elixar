// Self-test for the second part of 1.2.66: security and PC health alerts, Windows setup leftovers, an oversized
// search index, old user accounts.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class SelfTest1266b
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

        static HealthReport Report(int total, bool avOff, bool fwOff, DateTime? blue)
        {
            var r = new HealthReport { Total = total, Facts = new HealthFacts() };
            if (avOff) r.Items.Add(new HFind { Key = "av", Level = HLevel.Bad, Title = "No antivirus is on", Detail = "Real-time protection is off." });
            else r.Items.Add(new HFind { Key = "av", Level = HLevel.Ok, Title = "Antivirus on" });
            if (fwOff) r.Items.Add(new HFind { Key = "fw", Level = HLevel.Bad, Title = "The firewall is off for public networks", Detail = "Anyone on the same Wi-Fi can reach this PC.", Penalty = 20 });
            r.Items.Add(new HFind { Key = "wu", Level = HLevel.Warn, Title = "Windows Update failed 3 times", Penalty = 15 });
            if (blue != null) r.Facts.Crashes.Add(new HCrash { When = blue.Value, Kind = "bluescreen", Code = "0x0000007e" });
            return r;
        }

        static RawProfile P(string sid, string folder, DateTime? last, bool loaded = false, bool special = false)
        {
            return new RawProfile { Sid = sid, Folder = folder, LastUse = last, Loaded = loaded, Special = special };
        }
        const string Dom = "S-1-5-21-1004336348-1177238915-682003330-";

        public static void All(string root, Action<bool, string> check)
        {
            // ---------- 6. security and PC health alerts ----------
            Run(check, "Security alerts: antivirus or firewall off is told once (again only after it was fixed and came back), a new blue screen once, PC health down 10+ at most weekly", () =>
            {
                var st = new Dictionary<string, string>();
                var a = SecurityAlerts.Decide(Report(80, true, false, null), st, 82, Now);
                if (a.Count != 1 || a[0].Key != "av" || !a[0].Bad) return "av off: " + string.Join(",", a.Select(x => x.Key));
                if (SecurityAlerts.Decide(Report(80, true, false, null), st, 80, Now.AddDays(1)).Count != 0) return "av told twice";
                if (SecurityAlerts.Decide(Report(85, false, false, null), st, 80, Now.AddDays(2)).Count != 0 || st["active"] != "") return "fixed: " + st["active"];
                a = SecurityAlerts.Decide(Report(80, true, true, null), st, 85, Now.AddDays(3));
                if (a.Count != 2 || !a.Any(x => x.Key == "av") || !a.Any(x => x.Key == "fw" && x.Title.Contains("firewall"))) return "came back: " + string.Join(",", a.Select(x => x.Key));
                // a blue screen yesterday: once; an old one: never
                var st2 = new Dictionary<string, string>();
                a = SecurityAlerts.Decide(Report(80, false, false, Now.AddDays(-1)), st2, 80, Now);
                if (a.Count != 1 || a[0].Key != "bluescreen" || !a[0].Text.Contains("0x0000007e")) return "blue screen: " + string.Join(",", a.Select(x => x.Key));
                if (SecurityAlerts.Decide(Report(80, false, false, Now.AddDays(-1)), st2, 80, Now.AddHours(5)).Count != 0) return "blue screen told twice";
                if (SecurityAlerts.Decide(Report(80, false, false, Now.AddDays(-5)), new Dictionary<string, string>(), 80, Now).Count != 0) return "an old blue screen";
                // PC health 86 -> 71: told with the main reasons; 71 -> 60 three days later: not again that week
                var st3 = new Dictionary<string, string>();
                a = SecurityAlerts.Decide(Report(71, false, false, null), st3, 86, Now);
                if (a.Count != 1 || a[0].Key != "drop" || !a[0].Title.Contains("86 \u2192 71") || !a[0].Text.Contains("Windows Update failed")) return "drop: " + (a.Count > 0 ? a[0].Title + " / " + a[0].Text : "none");
                if (SecurityAlerts.Decide(Report(60, false, false, null), st3, 71, Now.AddDays(3)).Count != 0) return "drop told twice in a week";
                if (SecurityAlerts.Decide(Report(48, false, false, null), st3, 60, Now.AddDays(8)).Count != 1) return "drop a week later";
                if (SecurityAlerts.Decide(Report(80, false, false, null), new Dictionary<string, string>(), 86, Now).Count != 0) return "a 6-point drop";
                if (SecurityAlerts.Decide(Report(80, false, false, null), new Dictionary<string, string>(), -1, Now).Count != 0) return "no earlier score";
                // something you chose to ignore in PC health is never an alert
                var ig = Report(80, true, false, null); ig.Items[0].Ignored = true;
                if (SecurityAlerts.Decide(ig, new Dictionary<string, string>(), 80, Now).Count != 0) return "an ignored finding";
                return null;
            });

            Run(check, "Security alerts: the daily check notifies and keeps them for the email (sent once), and is off when you or the policy turn it off", () =>
            {
                var store = new Dictionary<string, string>(); var told = new List<SecAlert>();
                SecurityAlerts.GetOverride = k => store.ContainsKey(k) ? store[k] : null; SecurityAlerts.SetOverride = (k, v) => store[k] = v;
                SecurityAlerts.CheckOverride = () => Report(-1, true, true, null); SecurityAlerts.NotifyOverride = x => told.Add(x);
                try
                {
                    if (!SecurityAlerts.On) return "off by default";
                    if (SecurityAlerts.Run(null) != 2 || told.Count != 2) return "first run: " + told.Count;
                    if (SecurityAlerts.Run(null) != 0 || told.Count != 2) return "told again";
                    var pend = SecurityAlerts.Pending();
                    if (pend.Count != 2 || !pend[0].Contains("\t")) return "for the email: " + pend.Count;
                    var doc = MailReports.SecurityAlert(pend);
                    string html = doc.Html(), text = doc.Text();
                    if (!html.Contains("firewall is off") || !html.Contains("antivirus is off") || !text.Contains("Nothing is changed from an email") || !doc.Subject.Contains("needs you")) return "email: " + doc.Subject;
                    // what's kept is only which alert and its numbers: text another program writes there is never emailed
                    string t0 = DateTime.Now.ToString("s");
                    var forged = new List<string> { t0 + "\tav\tx\ty", t0 + "\tphish\tClick http://evil.example\t!", t0 + "\tdrop\t90\t<b>evil</b>", t0 + "\tbluescreen\t" + t0 + "\t0x7e; visit evil.example" };
                    var fd = MailReports.SecurityAlert(forged);
                    if (fd.Html().Contains("evil") || fd.Text().Contains("evil") || !fd.Text().Contains("antivirus is off") || !fd.Text().Contains("blue screen")) return "forged text reached the email";
                    if (SecurityAlerts.Describe(t0 + "\tdrop\t86\t71")[0] != "PC health dropped: 86 \u2192 71") return "drop text";
                    SecurityAlerts.Sent();
                    if (SecurityAlerts.Pending().Count != 0) return "sent, still pending";
                    store["SecAlertMail"] = "2020-01-01T00:00:00\tOld alert\tfrom long ago";
                    if (SecurityAlerts.Pending().Count != 0) return "an old alert would be emailed";
                    SecurityAlerts.SetOn(false); store["SecAlertActive"] = "";
                    if (SecurityAlerts.On || SecurityAlerts.Run(null) != 0 || told.Count != 2) return "turned off";
                    SecurityAlerts.SetOn(true);
                    Policy.OrgOverride = () => new Dictionary<string, string> { { "sec.alerts", "off" } };
                    try { if (SecurityAlerts.On || SecurityAlerts.Run(null) != 0) return "policy off"; } finally { Policy.OrgOverride = null; }
                    SecurityAlerts.SetOn(false);
                    Policy.OrgOverride = () => new Dictionary<string, string> { { "sec.alerts", "on" } };
                    try { if (!SecurityAlerts.On || !Policy.Fixes("sec.alerts")) return "policy on"; } finally { Policy.OrgOverride = null; }
                    if (Policy.Rule("sec.alerts") == null || Policy.Rule("sec.alerts").Default != "choice") return "policy rule";
                    // the drop is measured against this check's own last score (it runs without administrator rights)
                    store.Clear(); told.Clear();
                    SecurityAlerts.CheckOverride = () => Report(86, false, false, null);
                    if (SecurityAlerts.Run(null) != 0 || store["SecAlertScore"] != "86") return "first score";
                    SecurityAlerts.CheckOverride = () => Report(71, false, false, null);
                    if (SecurityAlerts.Run(null) != 1 || told.Count != 1 || told[0].Key != "drop" || store["SecAlertScore"] != "71") return "drop from its own score";
                    return null;
                }
                finally { SecurityAlerts.GetOverride = null; SecurityAlerts.SetOverride = null; SecurityAlerts.CheckOverride = null; SecurityAlerts.NotifyOverride = null; }
            });

            // ---------- 7. Windows setup leftovers ----------
            Run(check, "Setup leftovers: C:\\ESD and C:\\Windows10Upgrade are Caution items, never ticked for you, removed only through the update-leftover rules (to Undo)", () =>
            {
                bool keepW = Catalog.WindowsOnLinux; Catalog.WindowsOnLinux = true;
                List<CleanItem> all; try { all = Catalog.Build(); } finally { Catalog.WindowsOnLinux = keepW; }
                foreach (var name in new[] { "Windows setup download (Media Creation Tool)", "Windows Update Assistant leftovers" })
                {
                    var it = all.FirstOrDefault(x => x.Name == name);
                    if (it == null) return "missing: " + name;
                    if (it.Tier != Tier.Caution || it.DefaultOn) return name + ": tier " + it.Tier + ", on " + it.DefaultOn;
                    if (it.Where != WinLeftovers.Esd && it.Where != WinLeftovers.UpgradeAssistant) return name + ": path " + it.Where;
                    if (Win.IsWindows && !WinLeftovers.Allowed(it.Where)) return name + ": path not allowed " + it.Where;
                }
                if (Win.IsWindows && (WinLeftovers.Allowed(WinLeftovers.Esd + "\\..\\Windows") || WinLeftovers.Allowed(@"C:\ESD2"))) return "a look-alike path is allowed";
                if (WinLeftovers.NotNowReason(true, false, Now.AddDays(-30), Now) == null || WinLeftovers.NotNowReason(false, true, Now.AddDays(-30), Now) == null) return "removed while an upgrade waits";
                return null;
            });

            // ---------- 8. oversized search index ----------
            Run(check, "Search index: over 4 GB it is shown in PC health and System space; the rebuild is Windows' own (stop Search, mark for rebuild, start), nothing deleted by Elixar", () =>
            {
                var f = new HealthFacts { SearchIndex = 23L << 30, Free = 50L << 30, Total = 200L << 30 };
                var r = Health.Evaluate(f, Now, new List<string>());
                var it = r.Items.FirstOrDefault(i => i.Key == "searchindex");
                if (it == null || it.Level != HLevel.Warn || it.Act != HAct.SystemSpace || !it.Title.Contains("23.0 GB")) return "finding: " + (it == null ? "none" : it.Title);
                f.SearchIndex = 900L << 20;
                if (Health.Evaluate(f, Now, new List<string>()).Items.Any(i => i.Key == "searchindex")) return "a normal index was flagged";
                if (SearchIndex.Worth(3L << 30) || !SearchIndex.Worth(5L << 30)) return "limit";
                if (!SearchIndex.Describe(23L << 30).Contains("damaged") || !SearchIndex.Describe(1L << 30).Contains("normal")) return "describe";
                var ran = new List<string>();
                SearchIndex.RunOverride = (exe, args) => { ran.Add(Path.GetFileName(exe) + " " + args); return 0; };
                try
                {
                    string said = ""; var e = SearchIndex.Rebuild(s => said += s);
                    if (e != null) return "rebuild: " + e;
                    if (ran.Count != 2 || !ran[0].EndsWith("stop wsearch /y") || !ran[1].EndsWith("start wsearch")) return "ran: " + string.Join(" | ", ran);
                    if (!said.Contains("builds a new one")) return "said: " + said;
                }
                finally { SearchIndex.RunOverride = null; }
                return null;
            });

            // ---------- 9. old user accounts ----------
            Run(check, "Old accounts: only people's accounts unused for 6 months; never you, anyone signed in, Windows' own, recent ones, unknown dates or folders outside C:\\Users", () =>
            {
                string me = Dom + "1001";
                var raw = new List<RawProfile>
                {
                    P(me, @"C:\Users\me", Now.AddYears(-2)),                                   // you
                    P(Dom + "1002", @"C:\Users\dana.k", Now.AddMonths(-11)),                    // old: listed
                    P(Dom + "1003", @"C:\Users\busy", Now.AddMonths(-9), loaded: true),         // signed in now
                    P("S-1-5-18", @"C:\Windows\system32\config\systemprofile", Now.AddYears(-3), special: true),
                    P("S-1-5-19", @"C:\Windows\ServiceProfiles\LocalService", Now.AddYears(-3)),
                    P(Dom + "1004", @"C:\Users\recent", Now.AddMonths(-2)),                     // recent
                    P(Dom + "1005", @"C:\Users\nodate", null),                                  // unknown: never
                    P(Dom + "1006", @"D:\Profiles\elsewhere", Now.AddYears(-1)),                // not in C:\Users
                    P(Dom + "1007", @"C:\Users\hive", Now.AddMonths(-10)),                      // Windows' date old, but its registry file changed last month
                    P("S-1-12-1-1234567890-1234567890-1234567890-1234567890", @"C:\Users\student", Now.AddMonths(-8)),   // work/school account: listed
                    P(Dom + "1008", @"C:\Users\..\Windows", Now.AddYears(-1)),
                };
                Func<string, DateTime?> hive = folder => folder.EndsWith("hive") ? (DateTime?)Now.AddMonths(-1) : null;
                var got = OldAccounts.Pick(raw, me, Now, hive);
                string names = string.Join(",", got.Select(x => x.Folder.Substring(x.Folder.LastIndexOf('\\') + 1)));
                if (names != "dana.k,student") return "listed: " + names;
                if (got[0].LastUse != Now.AddMonths(-11)) return "date";
                // what Windows prints is read back the same
                string o = "P\t" + Dom + "1002\tC:\\Users\\dana.k\t2025-12-02T09:15:00\t0\t0\r\nP\tS-1-5-18\tC:\\Windows\\system32\\config\\systemprofile\t\t0\t1\r\nnoise\r\n";
                var parsed = OldAccounts.Parse(o);
                if (parsed.Count != 2 || parsed[0].LastUse != new DateTime(2025, 12, 2, 9, 15, 0) || parsed[0].Loaded || !parsed[1].Special || parsed[1].LastUse != null) return "parse";
                if (OldAccounts.PersonSid("S-1-5-21-1-2-3") || OldAccounts.PersonSid(Dom + "1002' ; rm") || !OldAccounts.PersonSid(Dom + "500")) return "sid check";
                if (OldAccounts.UnderUsers(@"C:\Users", @"C:\Users") || OldAccounts.UnderUsers(@"C:\Users\a\b", @"C:\Users") || !OldAccounts.UnderUsers(@"C:\Users\a\", @"C:\Users")) return "folder check";
                return null;
            });

            Run(check, "Old accounts: removal checks once more right before (someone signed in meanwhile: not removed), uses Windows' own removal, and the policy turns it off (off on company PCs)", () =>
            {
                string me = Dom + "1001";
                var raw = new List<RawProfile> { P(me, @"C:\Users\me", DateTime.Now), P(Dom + "1002", @"C:\Users\dana.k", DateTime.Now.AddMonths(-11)), P(Dom + "1003", @"C:\Users\lee", DateTime.Now.AddMonths(-7)) };
                var removed = new List<string>();
                OldAccounts.ListOverride = () => raw; OldAccounts.MeOverride = () => me; OldAccounts.HiveOverride = f => null;
                OldAccounts.RemoveOverride = sid => { removed.Add(sid); return null; }; OldAccounts.NameOverride = sid => "CONTOSO\\" + sid.Substring(sid.Length - 4);
                try
                {
                    var s = OldAccounts.Scan(DateTime.Now, false);
                    if (s.Problem != null || s.Old.Count != 2 || s.All != 3 || s.Old[0].Name != "CONTOSO\\1002") return "scan: " + s.Problem + " " + s.Old.Count;
                    raw[2].Loaded = true;   // lee signs in between the look and the removal
                    if (OldAccounts.Remove(s.Old[1], null) == null || removed.Count != 0) return "removed a signed-in account";
                    if (OldAccounts.Remove(s.Old[0], null) != null || removed.Count != 1 || removed[0] != Dom + "1002") return "removed: " + string.Join(",", removed);
                    if (OldAccounts.Remove(new OldProfile { Sid = "S-1-5-18", Folder = @"C:\Windows" }, null) == null || removed.Count != 1) return "removed Windows' own";
                    OldAccounts.ListOverride = () => null;
                    if (OldAccounts.Scan(DateTime.Now, false).Problem == null) return "a failed read offered something";
                    OldAccounts.ListOverride = () => raw;
                    if (Policy.Rule("profiles") == null || Policy.Rule("profiles").Default != "allow" || !Policy.CompanyBlocks.Contains("profiles")) return "policy rule";
                    Policy.CompanyOverride = () => "contoso.com";
                    try
                    {
                        if (Policy.Allowed("profiles") || OldAccounts.Remove(s.Old[0], null) == null || removed.Count != 1) return "removed on a company PC";
                        Policy.OrgOverride = () => new Dictionary<string, string> { { "profiles", "allow" } };   // IT allowed it
                        if (!Policy.Allowed("profiles")) return "IT's allow";
                    }
                    finally { Policy.CompanyOverride = null; Policy.OrgOverride = null; }
                    return null;
                }
                finally { OldAccounts.ListOverride = null; OldAccounts.MeOverride = null; OldAccounts.HiveOverride = null; OldAccounts.RemoveOverride = null; OldAccounts.NameOverride = null; }
            });

            if (Win.IsWindows)
                Run(check, "Old accounts on this Windows PC (reads only): Windows lists the accounts, and you are never offered", () =>
                {
                    var s = OldAccounts.Scan(DateTime.Now, false);
                    if (s.Problem != null) return "problem: " + s.Problem;
                    string me; using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) me = id.User.Value;
                    if (s.Old.Any(p => p.Sid == me)) return "offered you";
                    if (s.Old.Any(p => (DateTime.Now - p.LastUse).TotalDays < 180)) return "a recent account";
                    return null;
                });

            // ---------- attack review (1.2.66) ----------
            Run(check, "Attack review: an empty folder is removed by handle, only if still empty; a folder reached through a link is never removed", () =>
            {
                string dir = Path.Combine(root, "rmempty-1266"); if (Directory.Exists(dir)) Directory.Delete(dir, true);
                string a = Path.Combine(dir, "A"), b = Path.Combine(a, "B"), full = Path.Combine(dir, "Full");
                Directory.CreateDirectory(b); Directory.CreateDirectory(full); File.WriteAllText(Path.Combine(full, "keep.txt"), "x");
                if (SafeDelete.RemoveEmptyDir(full) || !File.Exists(Path.Combine(full, "keep.txt"))) return "removed a folder with a file";
                if (Win.IsWindows)
                {
                    // J is a link to A: removing "J\B" would really remove A\B (how a planted link redirects a delete)
                    string j = Path.Combine(dir, "J"); string o = Sys.Run("cmd.exe", "/c mklink /J " + Sys.ArgQuote(j) + " " + Sys.ArgQuote(a), 30000);
                    if (!Directory.Exists(j)) return "couldn't make the test link: " + o;
                    if (SafeDelete.RemoveEmptyDir(Path.Combine(j, "B")) || !Directory.Exists(b)) return "removed through a link";
                    if (SafeDelete.RemoveEmptyDir(j) || !Directory.Exists(a)) return "removed the link's target";
                    try { Directory.Delete(j, false); } catch { }
                }
                if (!SafeDelete.RemoveEmptyDir(b) || Directory.Exists(b)) return "an empty folder stayed";
                if (!SmartScan.RemoveEmpty(a) || Directory.Exists(a)) return "Smart scan's empty folders";
                return null;
            });

            if (Win.IsWindows)
                Run(check, "Attack review: copies of old accounts go only into a new folder Elixar makes and locks, never into an existing one or through a link", () =>
                {
                    string dir = Path.Combine(root, "acctcopy-1266"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, "copy1");
                    var e = OldAccounts.PrepareCopyFolder(dir, dest);
                    if (e != null || !Directory.Exists(dest)) return "new folder: " + e;
                    string fs = new DriveInfo(Path.GetPathRoot(dir)).DriveFormat;
                    if (fs == "NTFS" && !Locked.IsLocked(dest)) return "not locked: " + Locked.LockProblem(dest);
                    if (OldAccounts.PrepareCopyFolder(dir, dest) == null) return "used a folder that already exists";
                    string j = Path.Combine(dir, "J"); Sys.Run("cmd.exe", "/c mklink /J " + Sys.ArgQuote(j) + " " + Sys.ArgQuote(dest), 30000);
                    if (!Directory.Exists(j)) return "couldn't make the test link";
                    if (OldAccounts.PrepareCopyFolder(j, Path.Combine(j, "copy2")) == null) return "copied through a link";
                    try { Directory.Delete(j, false); } catch { }
                    return null;
                });

            Run(check, "Attack review: Move off C: replaces a same-size file someone put in the new place (only an identical file counts as copied)", () =>
            {
                string dir = Path.Combine(root, "move-1266"); if (Directory.Exists(dir)) Directory.Delete(dir, true);
                string from = Path.Combine(dir, "from"), to = Path.Combine(dir, "to");
                Directory.CreateDirectory(from); Directory.CreateDirectory(to);
                File.WriteAllText(Path.Combine(from, "report.docx"), "my real report");
                File.WriteAllText(Path.Combine(to, "report.docx"), "planted file!!");   // same length, other content
                File.WriteAllText(Path.Combine(from, "same.txt"), "identical"); File.WriteAllText(Path.Combine(to, "same.txt"), "identical");
                var r = Mover.Move(from, to, x => true, null);
                if (!r.Switched || r.Failed != 0) return "move: " + string.Join("; ", r.Problems);
                if (File.ReadAllText(Path.Combine(to, "report.docx")) != "my real report") return "the planted file was kept and the real one deleted";
                if (File.ReadAllText(Path.Combine(to, "same.txt")) != "identical") return "identical file";
                if (!Mover.SameContent(Path.Combine(to, "same.txt"), Path.Combine(to, "same.txt")) || Mover.SameContent(Path.Combine(to, "same.txt"), Path.Combine(to, "report.docx"))) return "compare";
                return null;
            });

            if (Win.IsWindows)
                Run(check, "Attack review: Move off C: never uses a folder another account made; the folders it makes are private like C:\\Users\\you", () =>
                {
                    System.Security.Principal.SecurityIdentifier me; using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) me = id.User;
                    var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
                    var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
                    if (Mover.TrustedOwner(users, me) || Mover.TrustedOwner(null, me) || Mover.TrustedOwner(new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1009"), me)) return "another account counted as yours";
                    if (!Mover.TrustedOwner(me, me) || !Mover.TrustedOwner(admins, me)) return "you or administrators";
                    string dir = Path.Combine(root, "movetarget-1266"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
                    string to = Path.Combine(Path.Combine(dir, "me"), "Videos");
                    var e = Mover.PrepareTarget(to);
                    if (e != null || !Directory.Exists(to)) return "prepare: " + e;
                    if (new DriveInfo(Path.GetPathRoot(dir)).DriveFormat == "NTFS")
                    {
                        var ds = new DirectoryInfo(Path.Combine(dir, "me")).GetAccessControl();
                        if (!ds.AreAccessRulesProtected) return "the new folder still takes other accounts' rights from above";
                        foreach (System.Security.AccessControl.FileSystemAccessRule rule in ds.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)))
                        {
                            var sid = (System.Security.Principal.SecurityIdentifier)rule.IdentityReference;
                            if (!sid.Equals(me) && !sid.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) && !sid.Equals(admins)) return "someone else has rights: " + sid.Value;
                        }
                    }
                    return null;
                });

            Run(check, "Attack review: another program can't borrow Elixar's administrator rights through Windows' prompt (quiet uninstall, --purge, command-line changes are confirmed)", () =>
            {
                if (Program.ElevationArgs(new[] { "--uninstall", "--quiet", "--purge" }) != "--uninstall") return "elevation passes: " + Program.ElevationArgs(new[] { "--uninstall", "--quiet", "--purge" });
                if (Program.ElevationArgs(new[] { "--install", "--policy", "x" }) != "--install") return "install";
                if (Cli.Changes(new[] { "--status" }) || Cli.Changes(new[] { "--help" }) || !Cli.Changes(new[] { "--policy", "a.elixarpolicy" }) || !Cli.Changes(new[] { "--report", "C:\\x.json" }) || !Cli.Changes(new[] { "--tweaks", "on" })) return "which commands change";
                string asked = null;
                ParentGuard.AskOverride = t => { asked = t; return false; };
                try
                {
                    ParentGuard.ParentOverride = () => "evil.exe";
                    if (ParentGuard.Allow("Elixar.exe --policy evil.elixarpolicy") || asked == null || !asked.Contains("evil.exe") || !asked.Contains("--policy evil.elixarpolicy")) return "a program without admin rights wasn't stopped: " + asked;
                    asked = null; ParentGuard.ParentOverride = () => null;   // an administrator command prompt, Task Scheduler, Windows Installer
                    if (!ParentGuard.Allow("Elixar.exe --status") || asked != null) return "an administrator's own command was questioned";
                    ParentGuard.AskOverride = t => true; ParentGuard.ParentOverride = () => "";
                    if (!ParentGuard.Allow("Elixar.exe --clean")) return "you said yes";
                }
                finally { ParentGuard.AskOverride = null; ParentGuard.ParentOverride = null; }
                if (Win.IsWindows && Sys.IsAdmin() && ParentGuard.NonAdminParent() != null) return "this self-test was started with administrator rights, but its starter looked like it had none: '" + ParentGuard.NonAdminParent() + "'";
                return null;
            });

            Run(check, "Old accounts and alerts in the window: the Tools page lists old accounts with nothing ticked; Automation has the alerts box, fixed when the policy sets it", () =>
            {
                string me = Dom + "1001";
                bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                OldAccounts.ListOverride = () => new List<RawProfile> { P(me, @"C:\Users\me", DateTime.Now), P(Dom + "1002", @"C:\Users\dana.k", DateTime.Now.AddMonths(-11)) };
                OldAccounts.MeOverride = () => me; OldAccounts.HiveOverride = f => null; OldAccounts.NameOverride = sid => "dana.k";
                try
                {
                    using (var m = new MainForm(true))
                    {
                        m.Show(); Pump(() => false, 300);
                        if (!m.ToolsPageNamesForTest.Contains("Old accounts")) return "no page: " + string.Join(",", m.ToolsPageNamesForTest);
                        bool done = false; m.AccountsFound += () => done = true;
                        m.FindAccounts(); Pump(() => done, 5000);
                        if (!done || m.LastAccountScan == null || m.AccountRowsForTest != 1 || m.AccountCheckedForTest != 0) return "rows " + m.AccountRowsForTest + ", ticked " + m.AccountCheckedForTest;
                        var box = m.SecAlertBoxForTest;
                        if (box == null || !box.Checked || !box.Enabled || !box.Text.Contains("blue screen")) return "alerts box";
                        m.Close();
                    }
                    return null;
                }
                finally { OldAccounts.ListOverride = null; OldAccounts.MeOverride = null; OldAccounts.HiveOverride = null; OldAccounts.NameOverride = null; MainForm.ForceWindowsUi = keepUi; }
            });
        }
    }
}

namespace CDriveCleaner
{
    public partial class MainForm
    {
        // self-test accessors (1.2.66, second part)
        public int AccountRowsForTest { get { return acctList.Items.Count; } }
        public int AccountCheckedForTest { get { return acctList.CheckedItems.Count; } }
        public CheckBox SecAlertBoxForTest { get { return chkSecAlerts; } }
    }
}
