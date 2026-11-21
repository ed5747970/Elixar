// Self-test for 1.2.72: a faster scan. Folders are counted the fast Windows way (with the same answer as before), the
// cleaning list is measured several items at once, and the shared lists used while measuring are made safely from many threads.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CDriveCleaner
{
    public static class SelfTest1272
    {
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

        public static void All(string root, Action<bool, string> check)
        {
            string R = Path.Combine(root, "t1272"); Directory.CreateDirectory(R);

            Run(check, "Fast count: a folder's size comes out the same as the plain way (deep folders, long paths, links not followed)", () =>
            {
                // Windows writes the listing into this layout: one field off and the names are read wrong (in 1.2.72's first
                // build an empty name sent the count round the same folder forever)
                if (!Fs.FindDataLayoutOk()) return "the folder-listing layout doesn't match Windows' (592 bytes, name at 44)";
                string top = Path.Combine(R, "tree");
                var rnd = new Random(7); long expect = 0;
                for (int i = 0; i < 40; i++)
                {
                    string d = Path.Combine(top, "d" + (i % 7), "sub" + (i % 3), "x" + i);
                    Directory.CreateDirectory(d);
                    int n = rnd.Next(0, 5000); File.WriteAllBytes(Path.Combine(d, "f" + i + ".bin"), new byte[n]); expect += n;
                }
                // a long path (over 260 characters)
                if (Win.IsWindows)
                {
                    string deep = top; while (deep.Length < 300) deep = Path.Combine(deep, "a-rather-long-folder-name");
                    try { Directory.CreateDirectory(@"\\?\" + deep); File.WriteAllBytes(@"\\?\" + Path.Combine(deep, "deep.bin"), new byte[1234]); expect += 1234; } catch { }
                    // a folder whose name ends in a dot, next to one without: each counted once, as itself
                    try
                    {
                        Directory.CreateDirectory(@"\\?\" + Path.Combine(top, "dot.")); File.WriteAllBytes(@"\\?\" + Path.Combine(top, "dot.", "a.bin"), new byte[777]); expect += 777;
                        Directory.CreateDirectory(Path.Combine(top, "dot")); File.WriteAllBytes(Path.Combine(top, "dot", "b.bin"), new byte[55]); expect += 55;
                    }
                    catch { }
                    // a junction to a big folder: not counted
                    string big = Path.Combine(R, "big"); Directory.CreateDirectory(big); File.WriteAllBytes(Path.Combine(big, "big.bin"), new byte[100000]);
                    try { Sys.Run("cmd.exe", "/c mklink /J \"" + Path.Combine(top, "link") + "\" \"" + big + "\"", 15000); } catch { }
                }
                Fs.FastSizeOff = true; long plain = Fs.SizeOf(top); Fs.FastSizeOff = false;
                long fast = Fs.SizeOf(top);
                // the plain way can't read past 260 characters on every Windows: the fast way counts those too
                if (!Win.IsWindows && fast != plain) return "fast " + fast + " vs plain " + plain;
                if (Win.IsWindows && fast < plain) return "fast " + fast + " is less than plain " + plain;
                if (Win.IsWindows && (fast != expect || Fs.FastSize(top) != expect)) return "expected " + expect + ", got " + fast;
                if (Fs.SizeOf(Path.Combine(R, "missing")) != 0) return "a missing folder isn't 0";
                return null;
            });

            Run(check, "Other apps' web caches: any Electron app is found by its folders; only Chromium's cache folders go, never its settings or local data", () =>
            {
                string roaming = Path.Combine(R, "Roaming"), local = Path.Combine(R, "Local");
                string slack = Path.Combine(roaming, "Slack"), notion = Path.Combine(roaming, "Notion"), vendorApp = Path.Combine(local, "Vendor", "App");
                foreach (var app in new[] { slack, notion, vendorApp })
                {
                    Directory.CreateDirectory(Path.Combine(app, "Cache", "Cache_Data")); File.WriteAllBytes(Path.Combine(app, "Cache", "Cache_Data", "f_000001"), new byte[3000]);
                    Directory.CreateDirectory(Path.Combine(app, "Code Cache", "js")); File.WriteAllBytes(Path.Combine(app, "Code Cache", "js", "a"), new byte[1000]);
                    Directory.CreateDirectory(Path.Combine(app, "Local Storage", "leveldb")); File.WriteAllText(Path.Combine(app, "Local Storage", "leveldb", "000003.log"), "your notes");
                    File.WriteAllText(Path.Combine(app, "Preferences"), "{}");
                }
                // not Electron: a folder named Cache without Chromium's files next to it; and Discord (its own item)
                // (even with a "Preferences" file and a "Cache" folder: only folders that only Chromium makes count)
                string other = Path.Combine(roaming, "SomeTool"); Directory.CreateDirectory(Path.Combine(other, "Cache")); File.WriteAllText(Path.Combine(other, "Cache", "data.db"), "needed");
                File.WriteAllText(Path.Combine(other, "Preferences"), "{}");
                // an Electron app's own "cache" right inside it, not laid out like Chromium's, is the app's too
                string figma = Path.Combine(roaming, "Figma"); File.WriteAllText(Path.Combine(Directory.CreateDirectory(figma).FullName, "Preferences"), "{}");
                Directory.CreateDirectory(Path.Combine(figma, "GPUCache")); File.WriteAllBytes(Path.Combine(figma, "GPUCache", "data_1"), new byte[200]);
                string own = Path.Combine(figma, "cache"); Directory.CreateDirectory(own); File.WriteAllText(Path.Combine(own, "offline.db"), "the app's");
                if (MoreCaches.RealChromiumCache(own) || !MoreCaches.RealChromiumCache(Path.Combine(slack, "Cache")) || !MoreCaches.RealChromiumCache(Path.Combine(slack, "GPUCache")))
                    return "Chromium's cache layout isn't told apart";
                // an Electron app's own "cache" deeper down is the app's, not Chromium's
                string plug = Path.Combine(slack, "plugins", "cache"); Directory.CreateDirectory(plug); File.WriteAllText(Path.Combine(plug, "x"), "the app's");
                string discord = Path.Combine(roaming, "discord"); Directory.CreateDirectory(Path.Combine(discord, "Cache")); File.WriteAllText(Path.Combine(discord, "Preferences"), "{}");
                MoreCaches.ElectronRootsOverride = () => new List<string> { roaming, local };
                try
                {
                    var apps = MoreCaches.ElectronApps().Select(a => a[1]).ToList();
                    if (apps.Count != 4 || !apps.Contains(slack) || !apps.Contains(notion) || !apps.Contains(vendorApp) || !apps.Contains(figma)) return "apps: " + string.Join(", ", apps.Select(Path.GetFileName));
                    var item = MoreCaches.Items().First(i => i.Name.StartsWith("Other apps' web caches"));
                    if (item.Measure() != 3 * 4000 + 200) return "measured " + item.Measure();
                    item.Clean(x => { });
                    foreach (var app in new[] { slack, notion, vendorApp })
                    {
                        if (Directory.GetFiles(Path.Combine(app, "Cache"), "*", SearchOption.AllDirectories).Length > 0) return "a cache was left";
                        if (!File.Exists(Path.Combine(app, "Local Storage", "leveldb", "000003.log")) || !File.Exists(Path.Combine(app, "Preferences"))) return "your data was touched";
                    }
                    if (!File.Exists(Path.Combine(other, "Cache", "data.db"))) return "a non-Electron app's Cache folder was cleared";
                    if (!File.Exists(Path.Combine(plug, "x"))) return "a cache folder deeper inside an app was cleared";
                    if (!File.Exists(Path.Combine(own, "offline.db"))) return "an app's own cache folder was cleared";
                    if (File.Exists(Path.Combine(figma, "GPUCache", "data_1"))) return "the app's GPU cache was left";
                    return null;
                }
                finally { MoreCaches.ElectronRootsOverride = null; }
            });

            Run(check, "Browser website storage: only Service Worker caches of browser profiles (a review item, never ticked by itself)", () =>
            {
                string ud = Path.Combine(R, "Chrome User Data"), prof = Path.Combine(ud, "Default");
                Directory.CreateDirectory(Path.Combine(prof, "Service Worker", "CacheStorage", "abc")); File.WriteAllBytes(Path.Combine(prof, "Service Worker", "CacheStorage", "abc", "x"), new byte[500]);
                Directory.CreateDirectory(Path.Combine(prof, "Service Worker", "Database")); File.WriteAllText(Path.Combine(prof, "Service Worker", "Database", "MANIFEST"), "keep");
                File.WriteAllText(Path.Combine(prof, "Login Data"), "passwords");
                MoreCaches.BrowserRootsOverride = () => new List<string> { ud };
                try
                {
                    var item = MoreCaches.Items().First(i => i.Name.StartsWith("Browser website storage"));
                    if (item.Tier != Tier.Review || item.DefaultOn) return "it would be ticked by itself";
                    if (item.Measure() != 500) return "measured " + item.Measure();
                    item.Clean(x => { });
                    if (Directory.GetFiles(Path.Combine(prof, "Service Worker", "CacheStorage"), "*", SearchOption.AllDirectories).Length > 0) return "left";
                    if (!File.Exists(Path.Combine(prof, "Service Worker", "Database", "MANIFEST")) || !File.Exists(Path.Combine(prof, "Login Data"))) return "other data was touched";
                    return null;
                }
                finally { MoreCaches.BrowserRootsOverride = null; }
            });

            Run(check, "Developer caches: Deno's downloads go, its scripts' saved data stays; never the pnpm store, Bun's or Yarn 2's cache (hard links / run from)", () =>
            {
                var l = MoreCaches.DevCaches();
                if (l.Any(d => d.IndexOf("pnpm", StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf(".bun", StringComparison.OrdinalIgnoreCase) >= 0 || d.IndexOf("Berry", StringComparison.OrdinalIgnoreCase) >= 0))
                    return "listed: " + string.Join(", ", l);
                if (l.Any(d => d.EndsWith("deno", StringComparison.OrdinalIgnoreCase) || d.IndexOf("location_data", StringComparison.OrdinalIgnoreCase) >= 0)) return "all of Deno's folder";
                if (!l.Any(d => d.EndsWith(Path.Combine("deno", "deps"), StringComparison.OrdinalIgnoreCase))) return "Deno's downloads missing";
                return null;
            });

            Run(check, "Counting Windows' protected leftovers switches on the right to read only, never the right to write (other items are counted at the same time)", () =>
            {
                string d = Path.Combine(R, "prot"); Directory.CreateDirectory(d); File.WriteAllBytes(Path.Combine(d, "a"), new byte[10]);
                BackupRights.RestoreOnForTest = true;
                long n = WinLeftovers.MeasureProtected(d);
                if (n != 10) return "measured " + n;
                if (BackupRights.RestoreOnForTest) return "the right to write was on while counting";
                BackupRights.With(() => { }, null);
                if (!BackupRights.RestoreOnForTest) return "removing lost its rights";
                return null;
            });

            Run(check, "Business agent: the new items are each account's own; the PC's shared launcher cache (GOG, in ProgramData) is counted once", () =>
            {
                var items = MoreCaches.Items();
                if (!items.All(i => i.PerAccount)) return "not marked as each account's: " + string.Join(", ", items.Where(i => !i.PerAccount).Select(i => i.Name));
                var cats = Caches.Items().Where(i => i.Name.StartsWith("Microsoft Teams") || i.Name.StartsWith("Developer caches") || i.Name.StartsWith("WhatsApp")).ToList();
                if (cats.Count != 3 || !cats.All(i => i.PerAccount)) return "older items without a path in Where";
                if (Win.IsWindows || Catalog.WindowsOnLinux)
                {
                    bool withGog = MoreCaches.LauncherCaches().Any(d => d.IndexOf("GOG.com", StringComparison.OrdinalIgnoreCase) >= 0);
                    MoreCaches.AccountOnly = true;
                    bool later; try { later = MoreCaches.LauncherCaches().Any(d => d.IndexOf("GOG.com", StringComparison.OrdinalIgnoreCase) >= 0); } finally { MoreCaches.AccountOnly = false; }
                    if (!withGog && !string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData))) return "GOG missing";
                    if (later) return "GOG counted again for another account";
                }
                return null;
            });

            Run(check, "Old JetBrains versions: the caches of older versions go, the newest of each IDE and Toolbox stay", () =>
            {
                string jb = Path.Combine(R, "JetBrains");
                foreach (var d in new[] { "IntelliJIdea2024.1", "IntelliJIdea2025.2", "PyCharm2023.3", "Rider2025.1", "Rider2024.3", "Toolbox" }) Directory.CreateDirectory(Path.Combine(jb, d));
                var old = MoreCaches.OldJetBrains(jb).Select(Path.GetFileName).OrderBy(x => x).ToList();
                if (string.Join(",", old) != "IntelliJIdea2024.1,Rider2024.3") return "old: " + string.Join(",", old);
                return null;
            });

            Run(check, "Old installers in Downloads: setup files of installed apps over 60 days old go to Undo; new ones, other apps' and other files stay", () =>
            {
                string dl = Path.Combine(R, "Downloads"); Directory.CreateDirectory(dl);
                Func<string, int, string> make = (n, days) => { string f = Path.Combine(dl, n); File.WriteAllBytes(f, new byte[100]); File.SetLastWriteTime(f, DateTime.Now.AddDays(-days)); return f; };
                string chrome = make("ChromeSetup.exe", 90), vlc = make("vlc-3.0.20-win64.exe", 200), fresh = make("Firefox Installer.exe", 5), notInstalled = make("Blender-4.2-windows-x64.msi", 300),
                       doc = make("report.pdf", 300), bare = make("setup.exe", 300), app = make("App Installer.exe", 300),
                       part = make("art-studio-setup.exe", 300), git = make("Git-2.43.0-64-bit.exe", 300), earth = make("google-earth-pro-setup.exe", 300),
                       gh = make("GitKraken-amd64.exe", 300), obs = make("OBS-Studio-30.0-Full-Installer-x64.exe", 300),
                       putty = make("putty.exe", 300);
                MoreCaches.DownloadsOverride = () => dl;
                MoreCaches.InstalledOverride = () => new List<string> { "Google Chrome", "VLC media player", "Mozilla Firefox", "App Installer", "Samsung Smart Switch", "Git", "OBS Studio", "PuTTY release 0.81 (64-bit)" };
                try
                {
                    if (MoreCaches.ProductOf("ChromeSetup.exe") != "chrome" || MoreCaches.ProductOf("vlc-3.0.20-win64.exe") != "vlc" || MoreCaches.ProductOf("Firefox Installer (1).exe") != "firefox") return "names: " + MoreCaches.ProductOf("ChromeSetup.exe") + "/" + MoreCaches.ProductOf("vlc-3.0.20-win64.exe");
                    if (MoreCaches.NameHas(new List<string> { "github", "desktop" }, "git") || !MoreCaches.NameHas(new List<string> { "google", "earth", "pro" }, "googleearth")) return "whole words";
                    if (MoreCaches.LooksInstaller("putty.exe") || !MoreCaches.LooksInstaller("putty-64bit-0.81-installer.msi") || !MoreCaches.LooksInstaller("vlc-3.0.20-win64.exe")) return "setup files told apart";
                    var l = MoreCaches.OldInstallers(DateTime.Now).Select(Path.GetFileName).OrderBy(x => x).ToList();
                    if (string.Join(",", l) != "ChromeSetup.exe,Git-2.43.0-64-bit.exe,OBS-Studio-30.0-Full-Installer-x64.exe,vlc-3.0.20-win64.exe") return "found: " + string.Join(",", l);
                    var item = MoreCaches.Items().First(i => i.Name == "Old installers in Downloads");
                    if (item.DefaultOn || item.Tier != Tier.Review) return "it would be ticked by itself";
                    return null;
                }
                finally { MoreCaches.DownloadsOverride = null; MoreCaches.InstalledOverride = null; }
            });

            Run(check, "New cleaning items are in the list (AMD/Intel shaders, LiveKernelReports in crash dumps, other apps, website storage, developer, launchers, old installers) and translated", () =>
            {
                var names = Catalog.Build().Select(i => i.Name).ToList();
                var want = new[] { "AMD and Intel shader cache", "Other apps' web caches (Slack, Notion, Signal...)", "Browser website storage (Service Worker caches)",
                    "More developer caches (Deno, Unity, Rust, old JetBrains)", "Game launcher caches (Battle.net, Riot, Ubisoft, Roblox, GOG)", "Old installers in Downloads" };
                if (!Lx.On || Catalog.WindowsOnLinux) foreach (var w in want) if (!names.Contains(w)) return "missing: " + w;
                var crash = Catalog.Build().FirstOrDefault(i => i.Name == "Crash dumps");
                if (crash != null && !crash.Where.Contains("LiveKernelReports")) return "LiveKernelReports not in crash dumps";
                string keep = Lang.Code;
                try
                {
                    foreach (var lang in new[] { "he", "ar", "ru", "es" })
                    {
                        Lang.Init(lang);
                        foreach (var w in want) if (Lang.T(w) == w) return "not in " + lang + ": " + w;
                    }
                }
                finally { Lang.Init(keep); }
                return null;
            });

            Run(check, "Measuring several at once: Windows' folder lists and your profile are made once, safely, from many threads", () =>
            {
                var errors = new List<string>();
                Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
                {
                    try
                    {
                        if (Win.IsWindows && !Protect.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.Windows))) lock (errors) errors.Add("Windows not protected");
                        if (string.IsNullOrEmpty(UserDirs.Home)) lock (errors) errors.Add("no home");
                        if (string.IsNullOrEmpty(Paths.Data)) lock (errors) errors.Add("no data folder");
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex.GetType().Name); }
                });
                if (errors.Count > 0) return string.Join(", ", errors.Distinct());
                if (MainForm.MeasureThreads < 2) return "measured one by one";
                return null;
            });
        }
    }
}
