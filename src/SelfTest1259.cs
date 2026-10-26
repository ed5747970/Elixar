// Self-test for 1.2.59: Check & fix, Repair Windows, Secure wipe.

using System;
using System.Collections.Generic;
using System.IO;
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
        }
    }
}
