// Self-test for 1.2.59: Check & fix, Repair Windows, Secure wipe.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class SelfTest1259
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

        const long MB = 1L << 20;

        public static void All(string root, Action<bool, string> check)
        {
            string R = Path.Combine(root, "t1259"); Directory.CreateDirectory(R);

            // ---------- Repair Windows ----------
            Run(check, "Repair Windows: DISM and SFC results are read in plain words (healthy, repaired, partly, no internet, restart first), and progress from both tools' own lines", () =>
            {
                if (WinRepair.Percent("[==========                 18.4%                          ]") != 18 || WinRepair.Percent("Verification 63% complete.") != 63 ||
                    WinRepair.Percent("Beginning system scan.") != -1 || WinRepair.Percent("120%") != -1) return "progress";
                Func<string, int, string, RepairOutcome> judge = (tool, code, o) => { var s = WinRepair.Steps().First(x => x.Tool == tool); WinRepair.Judge(s, code, o); return s.Outcome; };
                if (judge("dism.exe", 0, "No component store corruption detected.\r\nThe operation completed successfully.") != RepairOutcome.Healthy) return "dism healthy";
                if (judge("dism.exe", 0, "The restore operation completed successfully.\r\nThe component store corruption was repaired.") != RepairOutcome.Repaired) return "dism repaired";
                if (judge("dism.exe", unchecked((int)0x800f081f), "Error: 0x800f081f\r\nThe source files could not be found.") != RepairOutcome.Failed) return "dism no source";
                // SFC writes UTF-16: the NUL characters are dropped
                string u16 = string.Join("\0", "Windows Resource Protection did not find any integrity violations.".ToCharArray());
                if (judge("sfc.exe", 0, u16) != RepairOutcome.Healthy) return "sfc healthy (UTF-16)";
                if (judge("sfc.exe", 0, "Windows Resource Protection found corrupt files and successfully repaired them.") != RepairOutcome.Repaired) return "sfc repaired";
                if (judge("sfc.exe", 0, "Windows Resource Protection found corrupt files but was unable to fix some of them.") != RepairOutcome.PartlyRepaired) return "sfc partly";
                if (judge("sfc.exe", 1, "There is a system repair pending which requires reboot to complete.") != RepairOutcome.RestartFirst) return "sfc pending";
                if (judge("sfc.exe", 1, "Windows Resource Protection could not perform the requested operation.") != RepairOutcome.Failed) return "sfc failed";
                var stop = new CancellationTokenSource(); stop.Cancel();
                WinRepair.RunOverride = (t, a, p, tok) => Tuple.Create(0, "");
                try { var st = WinRepair.Steps()[0]; WinRepair.Run(st, null, stop.Token); if (st.Outcome != RepairOutcome.Stopped) return "stop"; }
                finally { WinRepair.RunOverride = null; }
                return null;
            });

            Run(check, "Repair Windows (Tools): runs DISM then SFC with their progress, shows each result, and says whether to restart", () =>
            {
                var ran = new List<string>();
                WinRepair.RunOverride = (tool, args, progress, tok) =>
                {
                    ran.Add(tool + " " + args);
                    if (progress != null) { progress(40); progress(100); }
                    return tool == "dism.exe" ? Tuple.Create(0, "The component store corruption was repaired.") : Tuple.Create(0, "Windows Resource Protection did not find any integrity violations.");
                };
                try
                {
                    using (var f = new MainForm(true))
                    {
                        bool done = false; f.RepairDone += () => done = true;
                        f.CreateControl();
                        var h = f.Handle;
                        f.StartRepair();
                        Pump(() => done, 20000);
                        if (!done) return "didn't finish";
                        if (string.Join("|", ran) != "dism.exe /Online /Cleanup-Image /RestoreHealth|sfc.exe /scannow") return "ran: " + string.Join("|", ran);
                        if (f.RepairSummary != "Windows was repaired. Restart the PC to finish.") return "summary: " + f.RepairSummary;
                        if (!f.RepairStepText(1).StartsWith("Every Windows system file is fine")) return "step 2: " + f.RepairStepText(1);
                        if (!ChangeLog.Read().Any(e => (e.Action ?? "").Contains("Windows repair"))) return "not in Logs";
                    }
                    return null;
                }
                finally { WinRepair.RunOverride = null; }
            });

            // ---------- Secure wipe ----------
            string wipeRoot = Path.Combine(R, "drive"); Directory.CreateDirectory(wipeRoot);
            File.WriteAllText(Path.Combine(wipeRoot, "keep.txt"), "my file");
            long keepChunk = SecureWipe.ChunkBytes;
            Run(check, "Secure wipe: fills only the free space (zeros, or random-random-zeros) in files that delete themselves, then nothing is left; your files stay; on an SSD it runs TRIM instead", () =>
            {
                SecureWipe.ChunkBytes = 8 * MB;
                string dir = Path.Combine(wipeRoot, SecureWipe.FolderName);
                SecureWipe.FreeOverride = r => 40 * MB - (Directory.Exists(dir) ? Directory.GetFiles(dir).Sum(x => { try { return new FileInfo(x).Length; } catch { return 0L; } }) : 0);
                var ran = new List<string>();
                SecureWipe.RunOverride = (exe, args) => { ran.Add(exe + " " + args); return 0; };
                try
                {
                    var d = new WipeDrive { Root = wipeRoot, Media = "HDD", Free = 40 * MB };
                    var once = SecureWipe.WipeFree(d, 1, null, CancellationToken.None);
                    if (once.Error != null || once.Written < 32 * MB || once.Written > 44 * MB) return "once: " + once.Written / MB + " MB " + once.Error;
                    if (Directory.Exists(dir)) return "the wipe folder stayed";
                    if (File.ReadAllText(Path.Combine(wipeRoot, "keep.txt")) != "my file" || Directory.GetFileSystemEntries(wipeRoot).Length != 1) return "touched other files";
                    var three = SecureWipe.WipeFree(d, 3, null, CancellationToken.None);
                    if (three.Written < 96 * MB || three.Passes != 3 || Directory.Exists(dir)) return "three passes: " + three.Written / MB + " MB";
                    var stop = new CancellationTokenSource(); stop.Cancel();
                    var stopped = SecureWipe.WipeFree(d, 1, null, stop.Token);
                    if (!stopped.Stopped || stopped.Written != 0 || Directory.Exists(dir)) return "stopped";
                    var ssd = SecureWipe.WipeFree(new WipeDrive { Root = "D:\\", Media = "SSD" }, 3, null, CancellationToken.None);
                    if (!ssd.Trimmed || ssd.Written != 0 || ran.Count != 1 || ran[0] != "defrag.exe D: /L") return "ssd: " + string.Join("|", ran);
                    if (!ChangeLog.Read().Any(e => (e.Action ?? "").StartsWith("Wiped the free space of"))) return "not in Logs";
                    return null;
                }
                finally { SecureWipe.FreeOverride = null; SecureWipe.RunOverride = null; SecureWipe.ChunkBytes = keepChunk; }
            });

            // shredding works on files outside AppData (the self-test's own folder is in Temp, which is an app place)
            string shredDir = Path.Combine(UserDirs.Home, "ElixarShredTest-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            try
            {
                Directory.CreateDirectory(shredDir);
                Run(check, "Shred files: overwrites, renames and deletes what you pick; never Windows', apps' or Elixar's files, folders or links", () =>
                {
                    string a = Path.Combine(shredDir, "private.docx"), b = Path.Combine(shredDir, "other.txt");
                    File.WriteAllBytes(a, Enumerable.Repeat((byte)0x41, 3 * (int)MB).ToArray()); File.WriteAllText(b, "stay");
                    string err;
                    string appFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SomeApp", "data.db");
                    if (SecureWipe.CanShred(appFile) == null) return "an app's file was allowed";
                    if (Win.IsWindows && SecureWipe.CanShred(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe")) == null) return "a Windows file was allowed";
                    if (SecureWipe.CanShred(Path.Combine(root, "x.txt")) == null && root.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)) return "Temp allowed";
                    if (SecureWipe.CanShred(shredDir) == null) return "a folder was allowed";
                    string link = Path.Combine(shredDir, "link.txt");
                    try { if (Win.IsWindows) Sys.Run("cmd.exe", "/c mklink \"" + link + "\" \"" + b + "\"", 15000); else Sys.Run("ln", "-s \"" + b + "\" \"" + link + "\"", 15000); } catch { }
                    if (File.Exists(link) && SecureWipe.CanShred(link) == null) return "a link was allowed";
                    if (SecureWipe.CanShred(a) != null) return "a normal file refused: " + SecureWipe.CanShred(a);
                    if (!SecureWipe.Shred(a, 1, out err)) return "shred: " + err;
                    if (File.Exists(a) || File.ReadAllText(b) != "stay") return "after shred";
                    if (Directory.GetFiles(shredDir).Any(x => x.EndsWith(".tmp"))) return "the renamed file stayed";
                    if (!ChangeLog.Read().Any(e => e.Action == "Shredded a file" && (e.Target ?? "").EndsWith("private.docx"))) return "not in Logs";
                    return null;
                });

                Run(check, "Secure wipe (Tools): the page lists files to shred, refuses what isn't allowed, and shreds after asking", () =>
                {
                    string c = Path.Combine(shredDir, "c.txt"); File.WriteAllText(c, "secret");
                    using (var f = new MainForm(true))
                    {
                        if (!MainForm.ForceWindowsUi && Lx.On) { }
                        bool done = false; f.WipeDone += () => done = true;
                        f.CreateControl(); var h = f.Handle;
                        f.AddShred(new[] { c, shredDir });
                        if (f.ShredReady != 1) return "ready: " + f.ShredReady;
                        f.StartShred();
                        Pump(() => done, 20000);
                        if (!done || File.Exists(c)) return "not shredded: " + f.WipeStatusText;
                        if (!f.WipeStatusText.StartsWith("Shredded 1 of 1 file")) return f.WipeStatusText;
                    }
                    return null;
                });
            }
            finally { try { Directory.Delete(shredDir, true); } catch { } }

            // ---------- Check & fix ----------
            Run(check, "Check & fix: finds the Safe items, startup programs you can open yourself, app updates, the Recycle Bin (not ticked) and how Windows is doing", () =>
            {
                var cleaned = new List<string>(); var turnedOff = new List<string>();
                Func<string, Tier, long, CleanItem> item = (n, t, b) => new CleanItem { Name = n, Tier = t, DefaultOn = true, Measure = () => b, Clean = log => cleaned.Add(n), Procs = new string[0] };
                CheckFix.CleansOverride = () => new List<CleanItem> { item("Temp files", Tier.Safe, 200 * MB), item("Thumbnail cache", Tier.Safe, 0), item("Recycle Bin", Tier.Review, 500 * MB) };
                CheckFix.StartupOverride = () => new List<StartupEntry>
                {
                    new StartupEntry { Name = "Steam", File = @"C:\Program Files (x86)\Steam\steam.exe", Enabled = true },
                    new StartupEntry { Name = "Spotify", File = @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe", Enabled = false },
                    new StartupEntry { Name = "Realtek audio", File = @"C:\Windows\System32\RtkAudUService64.exe", Enabled = true },
                    new StartupEntry { Name = "Discord", File = @"C:\Users\x\AppData\Local\Discord\Update.exe", Command = "Update.exe --processStart Discord.exe", Enabled = true, Flag = StartupFlag.Danger },
                };
                CheckFix.ImpactOverride = () => { var d = new StartupImpactData(); d.Apps["steam.exe"] = new AppImpact { File = "steam.exe", Times = 2, TotalMs = 6000 }; return d; };
                CheckFix.UpdatesOverride = () => new List<AppUpdate> { new AppUpdate { Name = "7-Zip", Id = "7zip.7zip", Version = "23.01", Available = "24.08", Source = "winget" },
                                                                       new AppUpdate { Name = "Evil", Id = "evil.app", Version = "1", Available = "2", Source = "someone-else" } };
                CheckFix.DiskOverride = () => "Healthy"; CheckFix.RestartOverride = () => false;
                CheckFix.SetStartupOverride = n => { turnedOff.Add(n); return true; };
                CheckFix.RestorePointOverride = () => "Restore point created.";
                var keepWinget = WinGet.RunOverride; WinGet.RunOverride = args => "Successfully installed";
                try
                {
                    if (CheckFix.OptionalName(new StartupEntry { File = @"C:\Windows\System32\SecurityHealthSystray.exe" }) != null) return "security app counted as optional";
                    var r = CheckFix.Scan(null);
                    var kinds = string.Join(",", r.Items.Select(i => i.Kind + (i.On ? "+" : "-")));
                    if (kinds != "Clean+,Startup+,Updates+,RecycleBin-,Info-") return "items: " + kinds;
                    if (r.CanFree != 200 * MB || r.SlowStartup != 1 || r.Updates != 1 || r.Disk != "Good") return "summary " + r.CanFree / MB + "/" + r.SlowStartup + "/" + r.Updates + "/" + r.Disk;
                    if (!r.Items[1].Title.StartsWith("Turn off 1 startup program") || r.Items[1].Right != "about 3 s faster start") return "startup row: " + r.Items[1].Title + " / " + r.Items[1].Right;
                    if (!r.Items[4].Locked || r.Items[4].Title != "Windows looks healthy") return "info row";
                    var res = CheckFix.Fix(r.Items, null);
                    if (string.Join(",", cleaned) != "Temp files") return "cleaned: " + string.Join(",", cleaned);   // never the Recycle Bin (not ticked)
                    if (string.Join(",", turnedOff) != "Steam" || res.StartupOff != 1 || res.Updated != 1 || res.Freed != 200 * MB) return "fix: " + string.Join(",", turnedOff) + " " + res.Updated + " " + res.Freed / MB;
                    if (res.RestorePoint != "Restore point created." || !res.Lines.First().StartsWith("Creating a restore point")) return "no restore point first";
                    // the window shows the same
                    using (var cf = new CheckFixForm(true, s => s))
                    {
                        cf.ShowReport(CheckFix.Scan(null));
                        if (cf.RowCount != 5) return "window rows: " + cf.RowCount;
                    }
                    return null;
                }
                finally
                {
                    CheckFix.CleansOverride = null; CheckFix.StartupOverride = null; CheckFix.ImpactOverride = null; CheckFix.UpdatesOverride = null; CheckFix.DiskOverride = null;
                    CheckFix.RestartOverride = null; CheckFix.SetStartupOverride = null; CheckFix.RestorePointOverride = null; WinGet.RunOverride = keepWinget;
                }
            });

            Run(check, "Check & fix is a button on the Dashboard; Repair Windows and Secure wipe are in Tools; their main texts are in Hebrew too", () =>
            {
                bool keep = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                try
                {
                    using (var f = new MainForm(true))
                    {
                        if (!f.CheckFixShown) return "no button";
                        var t = f.ToolsTabNames;
                        if (!t.Contains("Repair Windows") || !t.Contains("Secure wipe")) return "tools: " + string.Join(",", t);
                        f.OpenCheckFix(); if (f.LastCheckFix == null) return "the window didn't open";
                    }
                }
                finally { MainForm.ForceWindowsUi = keep; }
                bool wasHe = Lang.Hebrew;
                try
                {
                    Lang.Hebrew = true;
                    foreach (var s in new[] { "Check & fix", "Repair Windows", "Secure wipe", "Fix selected", "Wipe free space", "Shred files", "Here's what Elixar found", "Clean the Safe items", "Empty the Recycle Bin" })
                        if (Lang.T(s) == s) return "not in Hebrew: " + s;
                }
                finally { Lang.Hebrew = wasHe; }
                return null;
            });
            // ---------- folders growing fast ----------
            const long GB = 1L << 30;
            Run(check, "Growing fast: a folder that grew 1 GB in a week (or in 2 days), doubled, or is new and big is found; the folder that really grows is shown, not every folder above it; ignored ones stay quiet", () =>
            {
                string od = Path.Combine(R, "growdata"); Directory.CreateDirectory(od); FolderGrowth.OverrideDir = od;
                try
                {
                    var today = new DateTime(2026, 10, 29);
                    string a = Path.Combine(R, "A"), al = Path.Combine(a, "logs"), b = Path.Combine(R, "B"), c = Path.Combine(R, "C"), d = Path.Combine(R, "D");
                    Func<object[], Dictionary<string, long>> m = kv => { var x = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase); for (int i = 0; i < kv.Length; i += 2) x[(string)kv[i]] = (long)kv[i + 1]; return x; };
                    var snaps = new Dictionary<DateTime, Dictionary<string, long>>
                    {
                        { today.AddDays(-7), m(new object[] { a, 2 * GB, al, (long)(1.9 * GB), b, 5 * GB, c, 300L << 20 }) },
                        { today.AddDays(-2), m(new object[] { a, 5 * GB, al, (long)(4.9 * GB), b, (long)(5.1 * GB), c, 400L << 20 }) },
                        { today, m(new object[] { a, (long)(8.2 * GB), al, (long)(8.1 * GB), b, (long)(5.2 * GB), c, 900L << 20, d, (long)(1.5 * GB) }) },
                    };
                    var f = FolderGrowth.Detect(snaps, today);
                    string got = string.Join(",", f.Select(x => Path.GetFileName(x.Path)).OrderBy(x => x));
                    if (got != "C,D,logs") return "found: " + got;
                    var lg = f.First(x => x.Path == al);
                    if (lg.Days != 2 || FolderGrowth.GrewText(lg) != "+3.2 GB in 2 days") return "logs: " + FolderGrowth.GrewText(lg);
                    if (FolderGrowth.GrewText(f.First(x => x.Path == c)) != "+600 MB this week") return "doubled: " + FolderGrowth.GrewText(f.First(x => x.Path == c));
                    if (!f.First(x => x.Path == d).New) return "new folder";
                    FolderGrowth.Ignore(d);
                    if (FolderGrowth.Detect(snaps, today).Any(x => x.Path == d)) return "ignored";
                    if (!FolderGrowth.RandomName("x7f3k9q") || FolderGrowth.RandomName("office365") || FolderGrowth.RandomName("Win32") || FolderGrowth.RandomName("logs")) return "random names";
                    return null;
                }
                finally { FolderGrowth.OverrideDir = null; }
            });

            string gb = Path.Combine(R, "grow");
            Action<string, string, int, int> make = (dir, ext, count, daysOld) =>
            {
                Directory.CreateDirectory(dir);
                for (int i = 0; i < count; i++) { string fp = Path.Combine(dir, "f" + daysOld + "_" + i + ext); File.WriteAllBytes(fp, new byte[2048]); var t = DateTime.Now.AddDays(-daysOld); File.SetLastWriteTime(fp, t); }
            };
            string zl = Path.Combine(gb, "AppData", "Local", "Zoom", "logs"), vid = Path.Combine(gb, "Videos", "Captures"), cbs = Path.Combine(gb, "Windows", "Logs", "CBS"),
                   odd = Path.Combine(gb, "ProgramData", "x7f3k9q"), ransom = Path.Combine(gb, "Work", "Projects"), myst = Path.Combine(gb, "AppData", "Local", "Mystery");
            make(zl, ".log", 20, 40); make(zl, ".log", 5, 0); make(zl, ".docx", 1, 40); make(zl, ".exe", 0, 0);
            make(vid, ".mp4", 3, 1); make(cbs, ".log", 4, 1); make(odd, ".exe", 3, 0); make(odd, ".dat", 30, 0);
            make(ransom, ".k1ll3d", 150, 0); make(myst, ".dat", 20, 3);
            FolderGrowth.KindRootOverride = path => path.Contains(Path.DirectorySeparatorChar + "Videos") ? "yours" : path.Contains(Path.DirectorySeparatorChar + "Windows" + Path.DirectorySeparatorChar) ? "windows" : null;
            FolderGrowth.SignedOverride = path => false;
            var names = new List<string> { "Zoom Workplace", "Zoom Video Communications", "Spotify" };
            try
            {
                Run(check, "Growing fast: each folder is studied - an app's logs are safe to keep in check, your files and Windows' are never deleted, unsigned programs no app owns or many files turned into an unknown type (ransomware) look unusual", () =>
                {
                    var now = DateTime.Now;
                    var z = FolderGrowth.Study(zl, names, now);
                    if (z.Kind != GrowKind.Safe || z.Owner != "Zoom Workplace" || !z.Inside.Contains("old ones never removed")) return "Zoom logs: " + z.Kind + " / " + z.Owner + " / " + z.Inside;
                    var v = FolderGrowth.Study(vid, names, now); if (v.Kind != GrowKind.Yours) return "videos: " + v.Kind;
                    var w = FolderGrowth.Study(cbs, names, now); if (w.Kind != GrowKind.Windows || !w.Verdict.Contains("Repair Windows")) return "CBS: " + w.Kind;
                    var o = FolderGrowth.Study(odd, names, now); if (o.Kind != GrowKind.Unusual || o.OwnerKnown || o.Unsigned != 3 || !o.Verdict.Contains("Defender")) return "unknown programs: " + o.Kind + " " + o.Unsigned;
                    var r = FolderGrowth.Study(ransom, names, now); if (r.Kind != GrowKind.Unusual || !r.RansomLike || !r.Reason.Contains("ransomware")) return "ransomware look: " + r.Kind;
                    var y = FolderGrowth.Study(myst, names, now); if (y.Kind != GrowKind.Look) return "unknown data: " + y.Kind;
                    return null;
                });

                Run(check, "Keep in check: only for a folder studied as safe, after you allow it; it deletes only old log, cache and temp files (never programs or documents), the first two times to Undo", () =>
                {
                    string od = Path.Combine(R, "growdata2"); Directory.CreateDirectory(od); FolderGrowth.OverrideDir = od;
                    string undo = Path.Combine(R, "growundo"); Directory.CreateDirectory(undo); Quarantine.OverrideRoot = undo;
                    try
                    {
                        var now = DateTime.Now;
                        if (FolderGrowth.AddRule(FolderGrowth.Study(vid, names, now), 14) == null || FolderGrowth.AddRule(FolderGrowth.Study(odd, names, now), 14) == null || FolderGrowth.AddRule(FolderGrowth.Study(cbs, names, now), 14) == null) return "a rule for a folder that isn't safe";
                        var z = FolderGrowth.Study(zl, names, now);
                        if (FolderGrowth.AddRule(z, 14) != null || FolderGrowth.Rules().Count != 1) return "rule";
                        File.WriteAllBytes(Path.Combine(zl, "old.exe"), new byte[10]); File.SetLastWriteTime(Path.Combine(zl, "old.exe"), DateTime.Now.AddDays(-60));
                        var files = FolderGrowth.RuleFiles(FolderGrowth.Rules()[0], now);
                        if (files.Count != 20 || files.Any(x => !x.EndsWith(".log"))) return "would delete " + files.Count + ": " + string.Join(",", files.Where(x => !x.EndsWith(".log")).Select(Path.GetFileName));
                        FolderGrowth.RunRules(now, null);
                        int logsLeft = Directory.GetFiles(zl, "*.log").Length;
                        if (logsLeft != 5 || !File.Exists(Path.Combine(zl, "f40_0.docx")) || !File.Exists(Path.Combine(zl, "old.exe"))) return "after the first run: " + logsLeft + " logs";
                        if (!Quarantine.List().Any(q => (q.Label ?? "").StartsWith("Keep in check"))) return "the first run didn't go to Undo";
                        make(zl, ".log", 4, 40); FolderGrowth.RunRules(now, null); make(zl, ".log", 4, 40); FolderGrowth.RunRules(now, null);
                        if (Directory.GetFiles(zl, "*.log").Length != 5 || FolderGrowth.Rules()[0].Runs != 3) return "third run: " + Directory.GetFiles(zl, "*.log").Length;
                        if (!ChangeLog.Read().Any(e => e.Action == "Kept a folder in check")) return "not in Logs";
                        FolderGrowth.RemoveRule(zl); if (FolderGrowth.Rules().Count != 0) return "stop";
                        return null;
                    }
                    finally { FolderGrowth.OverrideDir = null; Quarantine.OverrideRoot = null; }
                });

                Run(check, "Growing fast, the daily check: one reading a day, the fast folders studied and announced once a week (at once when one starts to look unusual), shown on the Dashboard tile and in its window", () =>
                {
                    string od = Path.Combine(R, "growdata3"); Directory.CreateDirectory(od); FolderGrowth.OverrideDir = od;
                    var keepNames = Leftovers.NamesOverride; Leftovers.NamesOverride = () => names;
                    try
                    {
                        var d0 = DateTime.Now.Date.AddDays(-7);
                        var r0 = FolderGrowth.DailyRun(d0, null, () => new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { { zl, 1 * GB }, { odd, 200L << 20 } });
                        if (r0.Fast.Count != 0) return "first reading";
                        var r1 = FolderGrowth.DailyRun(DateTime.Now, null, () => new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { { zl, 4 * GB }, { odd, (long)(2.5 * GB) } });
                        if (r1.Fast.Count != 2 || r1.Alert.Count != 2) return "found " + r1.Fast.Count + ", alerts " + r1.Alert.Count;
                        var r2 = FolderGrowth.DailyRun(DateTime.Now, null, null);   // the same day: no new reading, no second alert
                        if (r2.Alert.Count != 0 || r2.Fast.Count != 2) return "second alert the same day";
                        var cur = FolderGrowth.Current();
                        if (cur.Count != 2 || !cur.Any(x => x.Item2 == GrowKind.Unusual)) return "for the Dashboard: " + cur.Count;
                        using (var gf = new GrowingForm(true, s => s, null, null, null))
                        {
                            gf.ShowStudies(r1.Fast.Select((x, i) => Tuple.Create(x, r1.Studies[i])).ToList());
                            if (gf.Studies.Count != 2) return "window";
                            var safe = gf.Studies.First(x => x.Kind == GrowKind.Safe);
                            if (!gf.KeepInCheck(safe) || FolderGrowth.Rules().Count != 1) return "allow from the window";
                        }
                        bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                        try { using (var f = new MainForm(true)) { f.OpenGrowing(); if (f.LastGrowing == null) return "the window didn't open"; } }
                        finally { MainForm.ForceWindowsUi = keepUi; }
                        bool wasHe = Lang.Hebrew;
                        try
                        {
                            Lang.Hebrew = true;
                            foreach (var t in new[] { "Growing fast", "Folders growing fast", "Safe to keep in check", "Looks unusual", "Your files", "Scan with Microsoft Defender", "Belongs to", "What's inside", "Clean once now" })
                                if (Lang.T(t) == t) return "not in Hebrew: " + t;
                        }
                        finally { Lang.Hebrew = wasHe; }
                        return null;
                    }
                    finally { FolderGrowth.OverrideDir = null; Leftovers.NamesOverride = keepNames; }
                });
            }
            finally { FolderGrowth.KindRootOverride = null; FolderGrowth.SignedOverride = null; }

#if BUSINESS
            // ---------- Cure email alerts (Elixar for Business) ----------
            Run(check, "Cure alerts: one email for every new problem, nothing again while it stays (a reminder after a week), an email when it's fixed, and only the kinds that are turned on", () =>
            {
                var t0 = new DateTime(2026, 10, 1, 9, 0, 0);
                Func<string, CureRisk, AlertPc> pc = (n, r) => new AlertPc { Id = "id-" + n, Name = n, Risk = r, Seen = t0, R = new CureReport { Total = 200L << 30, Free = r == CureRisk.Full ? 6L << 30 : 80L << 30, DiskLevel = r == CureRisk.Disk ? "Failing" : "Healthy" } };
                var p = new AlertPrefs { On = true };
                var pcs = new List<AlertPc> { pc("PC-OK", CureRisk.Ok), pc("PC-FULL", CureRisk.Full), pc("PC-DISK", CureRisk.Disk), pc("PC-LOW", CureRisk.Low) };
                Dictionary<string, AlertEntry> st;
                var m = CureAlerts.Plan(pcs, p, new Dictionary<string, AlertEntry>(), t0, out st);
                if (string.Join(",", m.New.Select(r => r.Pc.Name)) != "PC-DISK,PC-FULL" || m.Open.Count + m.Fixed.Count != 0 || st.Count != 2) return "first: " + string.Join(",", m.New.Select(r => r.Pc.Name));
                Dictionary<string, AlertEntry> st2;
                if (!CureAlerts.Plan(pcs, p, st, t0.AddHours(1), out st2).Empty) return "sent twice";
                pcs[1] = pc("PC-FULL", CureRisk.Ok);
                var m3 = CureAlerts.Plan(pcs, p, st2, t0.AddDays(3), out st);
                if (m3.Fixed.Count != 1 || m3.Fixed[0].Pc.Name != "PC-FULL" || m3.New.Count != 0 || st.ContainsKey("id-PC-FULL")) return "fixed";
                var m8 = CureAlerts.Plan(pcs, p, st, t0.AddDays(8), out st2);
                if (m8.Open.Count != 1 || m8.Open[0].Pc.Name != "PC-DISK" || m8.New.Count != 0) return "weekly reminder";
                p.Soon = true;
                var ms = CureAlerts.Plan(pcs, p, st2, t0.AddDays(8).AddHours(1), out st);
                if (ms.New.Count != 1 || ms.New[0].Pc.Name != "PC-LOW") return "getting full (turned on)";
                pcs[2] = pc("PC-DISK", CureRisk.Full);
                var mk = CureAlerts.Plan(pcs, p, st, t0.AddDays(9), out st2);
                if (mk.New.Count != 1 || mk.New[0].Kind != "full") return "a new kind of problem is new";
                p.Fixed = false; pcs[3] = pc("PC-LOW", CureRisk.Ok);
                if (CureAlerts.Plan(pcs, p, st2, t0.AddDays(10), out st).Fixed.Count != 0) return "fixed emails turned off";
                // the email: names, what to do, and a PC's text never becomes HTML
                var mail = new AlertMail();
                var weird = pc("PC-X", CureRisk.Full); mail.New.Add(new AlertRow { Pc = weird, Kind = "full", Problem = "Almost full", Free = "6.0 GB (3%)", ToDo = "<script>alert(1)</script> Windows.old" });
                CureAlerts.Compose(mail, "https://srv:8443/", "SRV");
                if (mail.Subject != "Elixar: 1 PC needs attention \u2014 PC-X") return "subject: " + mail.Subject;
                if (mail.Html.Contains("<script>") || !mail.Html.Contains("&lt;script&gt;") || !mail.Text.Contains("PC-X - Almost full - C: 6.0 GB (3%)") || !mail.Html.Contains("https://srv:8443/")) return "body";
                return null;
            });

            Run(check, "Cure alerts (hourly run): reads the Cure Server's reports, sends one email through the connected account, never twice, tries again when sending fails, and sends nothing when email is off or alerts are off", () =>
            {
                string dir = Path.Combine(R, "alerts"); if (Directory.Exists(dir)) Directory.Delete(dir, true);
                var srv = new CureServer { Https = false, DataDir = dir, TokenHash = CureServer.HashToken("t") };
                var js = CureProtocol.Json();
                Action<string, long, string> add = (name, free, disk) =>
                {
                    var en = srv.Enroll(Encoding.UTF8.GetBytes("{\"token\":\"t\",\"computer\":\"" + name + "\"}"), "");
                    var o = js.Deserialize<Dictionary<string, object>>(en.Body); string id = (string)o["id"]; var key = Convert.FromBase64String((string)o["key"]);
                    var body = Encoding.UTF8.GetBytes(js.Serialize(new CureReport { Elixar = "1.2.59", Total = 200L << 30, Free = free, DiskLevel = disk })); string t = CureProtocol.UnixNow(), n = CureProtocol.Nonce();
                    srv.Report(body, id, t, n, CureProtocol.Sign(key, t, n, body), "");
                };
                add("PC-OK", 120L << 30, "Healthy"); add("PC-FULL", 6L << 30, "Healthy"); add("PC-DISK", 90L << 30, "Failing");
                AlertPrefs.OverrideFile = Path.Combine(R, "alert-prefs.txt"); CureAlerts.StateOverride = Path.Combine(R, "alert-state.txt");
                var sent = new List<string>(); string fail = null;
                CureAlerts.SendOverride = (subj, text, html) => { if (fail != null) return fail; sent.Add(subj); return null; };
                CureAlerts.AccountOverride = () => "it@example.com";
                try
                {
                    new AlertPrefs { On = false }.Save();
                    CureAlerts.RunScheduled(dir, DateTime.Now);
                    if (sent.Count != 0) return "sent while off";
                    new AlertPrefs { On = true }.Save();
                    if (!Policy.Allowed("email")) { CureAlerts.RunScheduled(dir, DateTime.Now); return sent.Count == 0 ? null : "sent while email is off by policy"; }
                    fail = "no network";
                    if (CureAlerts.RunScheduled(dir, DateTime.Now) != 1 || CureAlerts.LoadState().Count != 0) return "a failed send was marked as sent";
                    fail = null;
                    CureAlerts.RunScheduled(dir, DateTime.Now);
                    if (sent.Count != 1 || !sent[0].StartsWith("Elixar: 2 PCs need attention") || !sent[0].Contains("PC-DISK") || !sent[0].Contains("PC-FULL")) return "sent: " + string.Join(" | ", sent);
                    CureAlerts.RunScheduled(dir, DateTime.Now.AddMinutes(60));
                    if (sent.Count != 1) return "sent again";
                    CureAlerts.AccountOverride = () => null;
                    if (CureAlerts.SendTest(dir) == null) return "a test with no account";
                    CureAlerts.AccountOverride = () => "it@example.com";
                    if (CureAlerts.SendTest(dir) != null || !sent.Last().StartsWith("Elixar: test of email alerts")) return "test email";
                    string a = Automation.CreateCureAlertArgs(@"C:\Program Files\Elixar\Elixar.exe");
                    if (!a.Contains("/TN \"Elixar\\Cure alerts\"") || !a.Contains("--cure-alerts") || !a.Contains("/SC HOURLY") || !a.Contains("/RL HIGHEST")) return "schedule: " + a;
                    return null;
                }
                finally { CureAlerts.SendOverride = null; CureAlerts.AccountOverride = null; }
            });

            Run(check, "Domain PCs > Email alerts: the button opens the choices, and Save keeps them", () =>
            {
                bool keep = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                CureAlerts.AccountOverride = () => "it@example.com";
                try
                {
                    using (var f = new MainForm(true))
                    {
                        if (!f.AlertsButtonShown) return "no button";
                        f.OpenAlerts();
                        var af = f.LastAlertsForm; if (af == null) return "no window";
                    }
                    using (var af = new CureAlertsForm(true, s => s, Path.Combine(R, "alerts")))
                    {
                        af.SetChoices(true, true);
                        if (af.SaveNow() != null) return "save";
                        var p = AlertPrefs.Load();
                        if (!p.On || !p.Soon || !p.Disk) return "saved choices";
                    }
                    return null;
                }
                finally { MainForm.ForceWindowsUi = keep; CureAlerts.AccountOverride = null; AlertPrefs.OverrideFile = null; CureAlerts.StateOverride = null; }
            });
#endif
        }
    }
}
