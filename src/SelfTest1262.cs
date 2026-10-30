// Self-test for 1.2.62: PC health (score, Security, Stability, Speed, Space, Drives, Safety net, Battery, weekly email).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class SelfTest1262
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

        static readonly DateTime Now = new DateTime(2026, 10, 30, 12, 0, 0);
        static string S(DateTime d) { return d.ToString("s"); }

        // a PC with something wrong in every area (what the mockup showed)
        public static HealthFacts Troubled()
        {
            var f = new HealthFacts();
            Health.Parse(
                "DEF|1|1|9|1\n" +
                "AV|Windows Defender|397568\n" +
                "FW|Domain|1\nFW|Private|1\nFW|Public|0\n" +
                "TPM|1|2.0, 0, 1.38\nBL|0\nWUN|40\n" +
                "WU|" + S(Now.AddDays(-41)) + "|2|0x00000000|2026-09 Cumulative Update\n" +
                "WU|" + S(Now.AddDays(-20)) + "|4|0x800F0922|2026-10 Cumulative Update\n" +
                "WU|" + S(Now.AddDays(-10)) + "|4|0x800F0922|2026-10 Cumulative Update\n" +
                "WU|" + S(Now.AddDays(-3)) + "|4|0x800F0922|2026-10 Cumulative Update\n" +
                "EV|ok\n" +
                "BSOD|" + S(Now.AddDays(-18)) + "|0x00000116 (0xffff, 0x0)\n" +
                "BSOD|" + S(Now.AddDays(-3)) + "|0x00000116 (0xffff, 0x0)\n" +
                "KP|" + S(Now.AddDays(-3).AddMinutes(-2)) + "|278\n" +
                string.Concat(Enumerable.Range(1, 6).Select(i => "CRASH|" + S(Now.AddDays(-i * 4)) + "|OUTLOOK.EXE|ZoomOutlookAddin.dll\n")) +
                "CRASH|" + S(Now.AddDays(-2)) + "|notepad.exe|ntdll.dll\n" +
                "RPK|ok\nRP|" + Now.AddDays(-63).ToString("yyyyMMddHHmmss") + ".000000-000\n" +
                "CH|10\nBAT|58000|41200|412\n", f);
            f.SecureBoot = true; f.Uac = true;
            f.AppUpdates = new List<AppUpdate> { new AppUpdate { Name = "Chrome", Source = "winget" }, new AppUpdate { Name = "7-Zip", Source = "winget" }, new AppUpdate { Name = "Zoom", Source = "winget" } };
            f.Startup = StartupImpact.Parse(
                "BOOT|" + S(Now.AddDays(-1)) + "|72000|30000|42000\nBOOT|" + S(Now.AddDays(-2)) + "|71000|30000|41000\nBOOT|" + S(Now.AddDays(-3)) + "|73000|30000|43000\n" +
                "BOOT|" + S(Now.AddDays(-30)) + "|48000|20000|28000\nBOOT|" + S(Now.AddDays(-32)) + "|48000|20000|28000\n" +
                "APP|" + S(Now.AddDays(-1)) + "|ms-teams.exe|Teams|9000\nAPP|" + S(Now.AddDays(-2)) + "|AdobeUpdater.exe|Adobe Updater|6000\n");
            f.RamTotal = 8L << 30; f.RamLoad = 92; f.TopMemory = new List<Tuple<string, long>> { Tuple.Create("Chrome", 2100L << 20) };
            f.PowerScheme = Health.PowerSaver; f.OnBattery = false;
            f.Free = 60L << 30; f.Total = 500L << 30;   // 12%
            f.Disk = HealthLevel.Healthy;
            f.FileHistory = false; f.OneDriveBackup = false;
            f.HasBattery = true;
            return f;
        }

        public static HealthFacts Fine()
        {
            var f = new HealthFacts();
            Health.Parse("DEF|1|1|0|1\nFW|Domain|1\nFW|Private|1\nFW|Public|1\nTPM|1|2.0\nBL|1\nWUN|5\nWU|" + S(Now.AddDays(-4)) + "|2|0x00000000|Update\nEV|ok\nRPK|ok\nRP|" + Now.AddDays(-2).ToString("yyyyMMddHHmmss") + ".000000+120\nCH|3\n", f);
            f.SecureBoot = true; f.Uac = true; f.AppUpdates = new List<AppUpdate>();
            f.Startup = StartupImpact.Parse("BOOT|" + S(Now.AddDays(-1)) + "|30000|15000|15000\n");
            f.RamTotal = 16L << 30; f.RamLoad = 40; f.PowerScheme = Health.Balanced;
            f.Free = 200L << 30; f.Total = 500L << 30; f.Disk = HealthLevel.Healthy; f.FileHistory = false; f.OneDriveBackup = true;
            return f;
        }

        static HFind Get(HealthReport r, string key) { return r.Items.FirstOrDefault(i => i.Key == key); }

        public static void All(string root, Action<bool, string> check)
        {
            bool wasHe = Lang.Hebrew; Lang.Hebrew = false;
            try { Tests(root, check); }
            finally { Lang.Hebrew = wasHe; Health.FactsOverride = null; Health.ScriptOverride = null; Health.SetPowerOverride = null; }
        }

        static void Tests(string root, Action<bool, string> check)
        {
            Run(check, "PC health: Windows' own records are read correctly (Defender, firewall, TPM, BitLocker, update history, blue screens, crashes, restore points, battery)", () =>
            {
                var f = Troubled();
                if (!f.DefKnown || !f.DefOn || f.DefSigAge != 9 || f.Antivirus.Count != 1) return "defender";
                if (f.Firewall.Count != 3 || f.Firewall["Public"] || !f.Firewall["Private"]) return "firewall";
                if (f.Tpm != "2.0" || f.BitLocker != 0) return "tpm/bitlocker: " + f.Tpm + "/" + f.BitLocker;
                if (!f.UpdatesKnown || f.Updates.Count != 4 || f.Updates.Count(u => u.Result == 4) != 3 || f.Updates[1].Code != "0x800F0922") return "updates";
                if (!f.CrashesKnown || f.Crashes.Count(c => c.Kind == "bluescreen") != 2 || f.Crashes.First(c => c.Kind == "bluescreen").Code != "0x116" || f.Crashes.Count(c => c.Kind == "crash") != 7) return "crashes";
                if (!f.RestoreKnown || !f.LastRestore.HasValue || (Now - f.LastRestore.Value).TotalDays < 62) return "restore point";
                if (f.DesignMwh != 58000 || f.FullMwh != 41200 || f.Cycles != 412 || !f.Chassis.SequenceEqual(new[] { 10 })) return "battery";
                // a broken or unexpected line is skipped, never read as something else
                var g = new HealthFacts(); Health.Parse("DEF|x\nWU|not a date|2|0x0|t\nRP|garbage\nBAT|a|b|c\nFW||1\nTPM|0|\nrandom text\n", g);
                if (g.DefKnown || g.Updates.Count != 0 || g.LastRestore.HasValue || g.DesignMwh != 0 || g.Firewall.Count != 0 || g.Tpm != "") return "bad lines";
                return null;
            });

            Run(check, "PC health: a PC with problems gets a low score, and each problem is named with its fix (old updates, firewall, blue screens, crashes, slow start, memory, no backup)", () =>
            {
                var r = Health.Evaluate(Troubled(), Now, new string[0]);
                var wu = Get(r, "wu"); if (wu == null || wu.Level != HLevel.Bad || !wu.Title.Contains("41 days") || !wu.Detail.Contains("0x800F0922") || wu.Act != HAct.RepairWindows) return "windows update: " + (wu == null ? "none" : wu.Title);
                var fw = Get(r, "fw"); if (fw == null || fw.Level != HLevel.Bad || !fw.Title.Contains("public")) return "firewall";
                var av = Get(r, "av"); if (av == null || av.Level != HLevel.Warn || !av.Title.Contains("9 days")) return "defender";
                var ap = Get(r, "apps"); if (ap == null || ap.Tag != "Safe" || ap.Updates.Count != 3 || ap.Act != HAct.UpdateApps) return "apps";
                var bl = Get(r, "bitlocker"); if (bl == null || bl.Level != HLevel.Info || bl.Tag != "Your decision" || !bl.CanIgnore) return "bitlocker";
                var bs = Get(r, "bluescreen"); if (bs == null || bs.Level != HLevel.Bad || !bs.Title.StartsWith("2 blue screens") || !bs.Detail.Contains("VIDEO_TDR_FAILURE") || bs.Act != HAct.Drivers) return "blue screens: " + (bs == null ? "none" : bs.Detail);
                if (Get(r, "shutdown") != null) return "the power-off right before a blue screen isn't a separate problem";
                var cr = Get(r, "crash:outlook.exe"); if (cr == null || !cr.Title.StartsWith("Outlook crashed 6 times") || !cr.Detail.Contains("ZoomOutlookAddin.dll")) return "crash";
                if (Get(r, "crash:notepad.exe") != null) return "one crash isn't a pattern";
                var bt = Get(r, "boot"); if (bt == null || bt.Level != HLevel.Warn || !bt.Title.Contains("1 min 12 s") || !bt.Title.Contains("48 s") || !bt.Detail.Contains("Teams")) return "boot: " + (bt == null ? "none" : bt.Title);
                var me = Get(r, "memory"); if (me == null || me.Level != HLevel.Warn || !me.Detail.Contains("Chrome")) return "memory";
                var pw = Get(r, "power"); if (pw == null || pw.Act != HAct.PowerBalanced) return "power plan";
                var fr = Get(r, "free"); if (fr == null || fr.Level != HLevel.Warn) return "space";
                var rp = Get(r, "restore"); if (rp == null || rp.Level != HLevel.Warn || !rp.Title.Contains("63 days")) return "restore";
                var bk = Get(r, "backup"); if (bk == null || bk.Level != HLevel.Warn || !bk.CanIgnore) return "backup";
                if (r.Scores.Count != 6 || r.Scores.Values.Any(v => v < 0)) return "every area scored";
                if (r.Scores["Drives"] != 100 || r.Scores["Stability"] > 60 || r.Scores["Security"] > 40) return "areas: " + string.Join(",", r.Scores.Select(kv => kv.Key + "=" + kv.Value));
                if (r.Total < 30 || r.Total >= 65 || r.Word != "Poor") return "total " + r.Total + " " + r.Word;
                if (r.ToFix < 10) return "to fix: " + r.ToFix;
                return null;
            });

            Run(check, "PC health: a healthy PC scores Good, and an area Elixar can't read on this PC doesn't count for or against it", () =>
            {
                var r = Health.Evaluate(Fine(), Now, new string[0]);
                if (r.Total < 95 || r.Word != "Good" || r.ToFix != 0 || r.Items.Any(i => i.Level == HLevel.Bad || i.Level == HLevel.Warn)) return "fine: " + r.Total + " " + string.Join("; ", r.Items.Where(i => i.Level >= HLevel.Warn).Select(i => i.Title));
                if (r.Laptop || r.Items.Any(i => i.Area == "Battery")) return "a desktop has no battery card";
                var only = Health.Evaluate(new HealthFacts { Free = 25L << 30, Total = 500L << 30 }, Now, null);   // 5%: only Space is known
                if (only.Scores.Count(kv => kv.Value >= 0) != 1 || only.Total != only.Scores["Space"] || only.Total > 65) return "only space: " + only.Total;
                if (Health.Evaluate(new HealthFacts(), Now, null).Total != -1) return "nothing known";
                return null;
            });

            Run(check, "PC health, antivirus: another antivirus that is on counts as protected; no antivirus at all is the worst finding", () =>
            {
                var f = Fine(); f.DefOn = false; f.DefRealTime = false; f.Antivirus.Add(new[] { "Bitdefender Antivirus", "397312" });   // 0x61000: on, up to date
                var a = Get(Health.Evaluate(f, Now, null), "av"); if (a == null || a.Level != HLevel.Ok || !a.Title.StartsWith("Bitdefender")) return "third party";
                f.Antivirus[0][1] = "397328";   // 0x61010: on, definitions out of date
                a = Get(Health.Evaluate(f, Now, null), "av"); if (a == null || a.Level != HLevel.Warn) return "out of date";
                f.Antivirus.Clear();
                var r = Health.Evaluate(f, Now, null); a = Get(r, "av");
                if (a == null || a.Level != HLevel.Bad || a.Penalty < 40 || a.Act != HAct.Defender) return "none";
                return null;
            });

            Run(check, "PC health, laptops: a battery AND a portable case make a laptop (a desktop's UPS doesn't); the battery's wear is shown, not scored", () =>
            {
                Func<bool, int[], bool> lap = (bat, ch) => { var f = new HealthFacts { HasBattery = bat }; f.Chassis.AddRange(ch); return Health.IsLaptop(f); };
                if (!lap(true, new[] { 10 }) || !lap(true, new[] { 31 }) || lap(true, new[] { 3 }) || lap(true, new[] { 7 }) || !lap(true, new int[0]) || !lap(true, new[] { 2 }) || lap(false, new[] { 10 })) return "detection";
                var r = Health.Evaluate(Troubled(), Now, null);
                var b = Get(r, "battery"); if (!r.Laptop || b == null || b.Level != HLevel.Warn || r.BatteryPct != 71 || !b.Detail.Contains("41.2 of 58.0 Wh") || !b.Detail.Contains("412 charge cycles")) return "battery: " + (b == null ? "none" : b.Title + " / " + b.Detail);
                if (r.Scores.ContainsKey("Battery")) return "the battery isn't part of the score";
                var bl = Get(r, "bitlocker"); if (bl.Penalty == 0) return "BitLocker off matters on a laptop";
                var d = Troubled(); d.Chassis.Clear(); d.Chassis.Add(3);
                if (Get(Health.Evaluate(d, Now, null), "battery") != null) return "a desktop with a UPS";
                return null;
            });

            Run(check, "PC health, Ignore: only your-decision findings (backup, BitLocker, power plan...) can be ignored, and then they don't lower the score", () =>
            {
                var a = Health.Evaluate(Troubled(), Now, null);
                var b = Health.Evaluate(Troubled(), Now, new[] { "backup", "bitlocker", "bluescreen", "wu" });
                if (!Get(b, "backup").Ignored || !Get(b, "bitlocker").Ignored || Get(b, "bluescreen").Ignored || Get(b, "wu").Ignored) return "what can be ignored";
                if (b.Scores["Safety net"] <= a.Scores["Safety net"] || b.Scores["Stability"] != a.Scores["Stability"] || b.ToFix != a.ToFix - 1) return "score after ignoring";
                return null;
            });

            Run(check, "PC health is read-only: the PowerShell script only reads (no Set-, Remove-, Invoke-, downloads or hidden commands), and Windows pages are a fixed list", () =>
            {
                string sc = Health.Script;
                var bad = new[] { "Set-", "Remove-", "New-Item", "Invoke-", "Start-", "Stop-", "Enable-", "Disable-", "Add-", "Clear-", "Restart-", "Update-", "Install-", "-Encoded", "Bypass", "DownloadString", "WebClient", "iex ", "Out-File", ">" + " " }
                    .Where(x => sc.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (bad.Count > 0) return "script has: " + string.Join(", ", bad);
                var com = Regex.Matches(sc, @"-ComObject\s+([\w.]+)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList();
                if (com.Count != 1 || com[0] != "Microsoft.Update.Session") return "COM objects: " + string.Join(",", com);
                var ch = ChangeLog.ChangingCmdlets(sc); if (ch.Count != 0) return "Logs would call it a change: " + string.Join(",", ch);
                if (!Regex.IsMatch(sc, @"\(C \$a\.displayName\)") || !Regex.IsMatch(sc, @"\(C \$h\.Title\)") || !Regex.IsMatch(sc, @"\(C \$e\.Properties\[0\]\.Value\)")) return "text values aren't cleaned";
                // a page is a program of Windows' own by name only (found in System32), or one of Windows' own links
                if (Health.Pages.Values.Any(p => p[0].EndsWith(".exe") ? p[0].IndexOfAny(new[] { '\\', '/', ':', ' ' }) >= 0 : !(p[0].StartsWith("windowsdefender:") || p[0].StartsWith("ms-settings:")))) return "pages";
                string got = null; Health.SetPowerOverride = g => { got = g; return true; };
                if (Health.UseBalanced() != null || got != Health.Balanced) return "power plan";
                return null;
            });

            Run(check, "PC health window: one tab per area (Battery on laptops), opens on the area that needs you most; Ignore updates the score; buttons open Elixar's tools or do one thing after asking", () =>
            {
                var acts = new List<HAct>();
                using (var f = new HealthForm(true, s => s, a => acts.Add(a)))
                {
                    f.CreateControl(); var handle = f.Handle;   // results from the background come back through the window
                    var r = Health.Evaluate(Troubled(), Now, null);
                    f.ShowReport(r);
                    if (f.Area != "Security") return "first tab: " + f.Area;
                    f.SelectArea("Stability");
                    if (f.Shown_.Count != 2 || f.Shown_[0].Key != "bluescreen") return "stability rows: " + f.Shown_.Count;
                    var drv = f.Buttons.FirstOrDefault(b => ((HFind)b.Tag).Act == HAct.Drivers);
                    if (drv == null) return "check drivers button";
                    f.Act((HFind)drv.Tag); if (acts.Count != 1 || acts[0] != HAct.Drivers) return "drivers: " + acts.Count;
                    f.SelectArea("Battery"); if (f.Area != "Battery" || f.Shown_.Count != 1) return "battery tab";
                    f.SelectArea("Safety net");
                    int before = f.Report.Scores["Safety net"];
                    var ig = f.Buttons.First(b => b.Text == "Ignore" && ((HFind)b.Tag).Key == "backup");
                    f.ToggleIgnore((HFind)ig.Tag);
                    if (f.Report.Scores["Safety net"] <= before || !Get(f.Report, "backup").Ignored) return "ignore";
                    f.SelectArea("Speed");
                    string set = null; Health.SetPowerOverride = g => { set = g; return true; };
                    f.Act((HFind)f.Buttons.First(b => ((HFind)b.Tag).Act == HAct.PowerBalanced).Tag);
                    Pump(() => f.StatusText.Contains("Balanced now"), 5000);
                    if (set != Health.Balanced || !f.StatusText.Contains("Balanced now")) return "power plan: " + f.StatusText;
                }
                bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                try
                {
                    using (var m = new MainForm(true))
                    {
                        var r = Health.Evaluate(Fine(), Now, null);
                        m.ShowHealthReport(r);
                        if (m.HealthOnDashboard != r) return "dashboard strip";
                        m.OpenHealth(); if (m.LastHealth == null || m.LastHealth.Report != r) return "the window from the Dashboard";
                    }
                }
                finally { MainForm.ForceWindowsUi = keepUi; }
                return null;
            });

            Run(check, "PC health in the weekly email: the score and what to fix, in English and Hebrew; nothing is fixed from an email", () =>
            {
                var r = Health.Evaluate(Troubled(), Now, null);
                var d = MailReports.Summary(40L << 30, 500L << 30, new List<CleanItem>(), null, null, r);
                string h = d.Html(), t = d.Text();
                if (!h.Contains("PC health") || !h.Contains(r.Total + "/100") || !h.Contains("blue screens") || !t.Contains("Windows Update hasn") || !h.Contains("Nothing is changed from an email")) return "english";
                if (MailReports.Summary(40L << 30, 500L << 30, new List<CleanItem>(), null, null).Html().Contains("PC health")) return "no report, no section";
                Lang.Hebrew = true;
                try
                {
                    var rh = Health.Evaluate(Troubled(), Now, null);
                    string hh = MailReports.Summary(40L << 30, 500L << 30, new List<CleanItem>(), null, null, rh).Text();   // (Mono's HTML encoder writes Hebrew letters as &#...;)
                    if (!hh.Contains("בריאות המחשב") || !hh.Contains("מסכים כחולים") || !hh.Contains("חשוב")) return "hebrew: " + (hh.Contains("בריאות המחשב") ? "" : "head ") + (hh.Contains("מסכים כחולים") ? "" : "title ") + (hh.Contains("חשוב") ? "" : "level");
                }
                finally { Lang.Hebrew = false; }
                return null;
            });

            Run(check, "PC health, the safety review: what you ignore is kept where only administrators can write (a planted line can't hide a finding), Secure Boot off can't be ignored, and a company's policy blocks app updates and startup changes here and in Check & fix", () =>
            {
                string ig = Path.Combine(root, "t1262-ignore.txt"); Health.IgnoreFileOverride = ig;
                try
                {
                    File.WriteAllLines(ig, new[] { "backup", "bad|line", @"x\..\y", new string('a', 200) });
                    var k = Health.IgnoredKeys(); if (k.Count != 1 || !k.Contains("backup")) return "ignore file: " + string.Join(",", k);
                    Health.Ignore("power", true); Health.Ignore("backup", false);
                    k = Health.IgnoredKeys(); if (k.Count != 1 || !k.Contains("power")) return "ignore and undo";
                }
                finally { Health.IgnoreFileOverride = null; }
                var f = Fine(); f.SecureBoot = false;
                var sb = Get(Health.Evaluate(f, Now, new[] { "secureboot" }), "secureboot");
                if (sb == null || sb.CanIgnore || sb.Ignored || sb.Level != HLevel.Warn) return "secure boot off";
                f.SecureBoot = true; f.Tpm = "";
                sb = Get(Health.Evaluate(f, Now, new[] { "secureboot" }), "secureboot");
                if (sb == null || !sb.Ignored) return "no TPM can be ignored";
                // the company blocks app updates and startup changes
                Policy.OrgOverride = () => new Dictionary<string, string> { { "apps.update", "block" }, { "startup.change", "block" } };
                try
                {
                    var t = Troubled(); t.AppsManaged = !Policy.Allowed("apps.update");
                    var ap = Get(Health.Evaluate(t, Now, null), "apps");
                    if (!t.AppsManaged || ap == null || ap.Button != "" || ap.Act != HAct.None || ap.Updates.Count != 0 || !ap.Detail.Contains("company")) return "health offers a blocked update";
                    var res = CheckFix.Fix(new[] { new FixItem { Kind = FixKind.Updates, On = true, Updates = new List<AppUpdate> { new AppUpdate { Name = "X", Source = "winget" } } },
                                                   new FixItem { Kind = FixKind.Startup, On = true, Startups = new List<StartupEntry> { new StartupEntry { Name = "Steam" } } } }, null);
                    if (res.Updated + res.UpdateFailed + res.StartupOff != 0 || res.Lines.Count != 0) return "check & fix did a blocked fix: " + string.Join(" / ", res.Lines);
                }
                finally { Policy.OrgOverride = null; }
                return null;
            });

            Run(check, "Audit fixes (1.2.62): Secure wipe can be turned off by policy (and is off on company PCs), checked in the wipe itself, not only in the window", () =>
            {
                if (!Policy.CompanyBlocks.Contains("wipe") || Policy.Rules.All(r => r.Key != "wipe")) return "rule";
                // in your profile, like the Secure wipe test (Temp and AppData are never shredded)
                string sd = Path.Combine(UserDirs.Home, "ElixarShredTest-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(sd);
                string f = Path.Combine(sd, "t1262-shred.txt"); File.WriteAllText(f, "secret");
                if (SecureWipe.CanShred(f) != null) return "allowed: " + SecureWipe.CanShred(f);
                Policy.OrgOverride = () => new Dictionary<string, string> { { "wipe", "block" } };
                try
                {
                    string why; if (SecureWipe.CanShred(f) == null || SecureWipe.Shred(f, 1, out why) || !File.Exists(f) || File.ReadAllText(f) != "secret") return "shredded while blocked";
                    var w = SecureWipe.WipeFree(new WipeDrive { Root = root, Media = "HDD" }, 1, null, CancellationToken.None);
                    if (w.Error == null || w.Written != 0) return "wiped while blocked";
                }
                finally { Policy.OrgOverride = null; }
                string err; bool done = SecureWipe.Shred(f, 1, out err);
                try { Directory.Delete(sd, true); } catch { }
                if (!done || File.Exists(f)) return "shred after allowing: " + err;
                return null;
            });

#if BUSINESS
            Run(check, "Audit fixes (1.2.62, Cure): wrong enrollment tokens are limited per address (real PCs still enroll), a name enrolled twice is shown, and error texts lose their paths", () =>
            {
                string dir = Path.Combine(root, "t1262-cure"); Directory.CreateDirectory(dir);
                var srv = new CureServer { Https = false, DataDir = dir, TokenHash = CureServer.HashToken("t") };
                Func<string, string, string, int> enroll = (tok, name, ip) => srv.Enroll(System.Text.Encoding.UTF8.GetBytes("{\"token\":\"" + tok + "\",\"computer\":\"" + name + "\"}"), ip).Status;
                for (int i = 0; i < CureServer.MaxBadEnrollPerHour; i++) if (enroll("wrong", "X", "10.0.0.9") != 403) return "wrong token " + i;
                if (enroll("t", "PC1", "10.0.0.9") != 429) return "the flooding address is stopped";
                if (enroll("t", "PC1", "10.0.0.5") != 200) return "a real PC elsewhere still enrolls";
                if (enroll("t", "PC1", "10.0.0.6") != 200) return "second enrollment";
                var pcs = srv.Pcs();
                if (pcs.Count != 2 || pcs.Any(p => !(bool)p["twice"]) || !pcs.All(p => (string)p["ip"] != "")) return "enrolled twice isn't shown";
                if (!(bool)srv.Pc((string)pcs[0]["id"])["twice"]) return "pc page";
                string a = CureProtocol.NoPaths("disks: Access to the path 'C:\\Users\\bob\\AppData\\x.log' is denied.");
                string b = CureProtocol.NoPaths("places: Could not find a part of the path C:\\Users\\alice\\Documents");
                string c2 = CureProtocol.NoPaths("apps: \\\\fs01\\home$\\carol\\x failed, retry");
                if (a.Contains("bob") || !a.Contains("'<path>' is denied") || b.Contains("alice") || c2.Contains("carol") || CureProtocol.NoPaths("still measuring: disks (3 min)") != "still measuring: disks (3 min)") return "paths: " + a + " | " + b + " | " + c2;
                var cleaned = CureProtocol.Clean(new CureReport { Errors = new[] { "x: C:\\Users\\dave\\y" } });
                if (cleaned.Errors[0].Contains("dave")) return "server side";
                if (!CureSetup.Q("x").StartsWith("\"")) return "q";
                return null;
            });
#endif

            Run(check, "PC health in Hebrew: the window, the tabs, the buttons and every finding", () =>
            {
                Lang.Hebrew = true;
                try
                {
                    foreach (var s in new[] { "PC health", "Security", "Stability", "Speed", "Space", "Drives", "Safety net", "Battery", "Check again", "Ignore", "Watch it again", "Use Balanced", "Make one now",
                                              "Repair Windows Update...", "Check drivers...", "Startup...", "What uses it", "Set up backup...", "How to...", "Open Windows Security", "Open firewall settings",
                                              "Open UAC settings", "Disk health...", "Check & fix...", "Show me", "Update", "PC health: Fair", "PC health: Good", "PC health: Poor", "3 things to fix", "1 thing to fix",
                                              "nothing to fix", "checked 5 min ago", "click to see", "Checking this PC...", "of 100", "You said this is fine", "Nothing found here.", "Your decision", "Safe",
                                              "Elixar reads what Windows already records. Checking changes nothing; every fix asks first and goes to Logs.", "Not checked yet", "3 things to fix.", "Nothing needs fixing.",
                                              "The power plan is Balanced now.", "Reading Windows Security, updates and error records...", "Checking the drives and backups..." })
                        if (Lang.T(s) == s) return "not in Hebrew: " + s;
                    var r = Health.Evaluate(Troubled(), Now, null);
                    var en = r.Items.FirstOrDefault(i => Regex.IsMatch(i.Title + i.Detail, "^[^֐-׿]*$") && i.Key != "secureboot");
                    if (en != null) return "finding in English: " + en.Title;
                }
                finally { Lang.Hebrew = false; }
                return null;
            });
        }
    }
}
