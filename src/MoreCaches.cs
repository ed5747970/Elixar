// 1.2.72: more things to clean.
// - Other apps' web caches: any Electron app (Slack, Notion, Signal, Obsidian, Figma...) found by its folder layout, not by a list
// - Browser website storage (Service Worker caches) in Chrome, Edge, Brave and Vivaldi: a review item, never ticked by itself
// - More developer caches: pnpm, Bun, Yarn 2+, Deno, Unity, Rust sources, caches of old JetBrains IDE versions
// - Game launcher caches and logs: Battle.net, Riot, Ubisoft Connect, Roblox, GOG Galaxy
// (WSL disks are not shrunk: that means mounting a disk file from your folders as administrator, and a planted disk image
//  is how March 2025's exploited NTFS holes were set off; WSL's own "sparse" option warns it can corrupt data)
// - Old installers in Downloads: setup files of apps that are installed, older than 60 days (to Undo, never deleted outright)
// Everything goes through Fs.ClearIn / Fs.Delete / Quarantine, so links are never followed and protected folders never touched.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public static class MoreCaches
    {
        static IEnumerable<string> Dirs(string parent, string pattern = "*")
        {
            try { return Directory.Exists(parent) ? Directory.GetDirectories(parent, pattern).Where(d => !IsLink(d)) : new string[0]; }
            catch { return new string[0]; }
        }
        static bool IsLink(string d) { try { return (new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0; } catch { return true; } }
        static long Sum(IEnumerable<string> paths) { return paths.Sum(p => Fs.SizeOf(p)); }
        static void ClearAll(IEnumerable<string> dirs) { foreach (var d in dirs.ToList()) Fs.ClearIn(d); }
        static long MeasureAll(IEnumerable<string> dirs) { return dirs.Sum(d => Fs.MeasureIn(d)); }

        // ---------- other apps' web caches (Electron) ----------
        // Already their own item, or a browser (its cache is its own item; a profile's data is not a cache)
        static readonly string[] Covered = { "discord", "discordptb", "discordcanary", "code", "code - insiders", "zoom", "whatsapp", "spotify", "teams", "microsoft",
            "google", "bravesoftware", "vivaldi", "opera software", "mozilla", "chromium", "electronic arts", "steam", "epicgameslauncher", "nvidia", "nvidia corporation",
            "claude", "packages", "temp", "programs", "battle.net", "blizzard entertainment", "riot games", "ubisoft game launcher", "roblox", "jetbrains", "unity" };

        // an Electron app's own folder: Chromium's cache folders next to its "Preferences" or "Local State" file
        public static bool LooksElectron(string dir)
        {
            try
            {
                bool marker = File.Exists(Path.Combine(dir, "Preferences")) || File.Exists(Path.Combine(dir, "Local State"));
                bool cache = Directory.Exists(Path.Combine(dir, "Cache")) || Directory.Exists(Path.Combine(dir, "Code Cache")) || Directory.Exists(Path.Combine(dir, "GPUCache"));
                return marker && cache;
            }
            catch { return false; }
        }

        public static Func<List<string>> ElectronRootsOverride;   // self-test: Roaming and Local
        // { app name, its folder }
        public static List<string[]> ElectronApps()
        {
            var roots = ElectronRootsOverride != null ? ElectronRootsOverride() : new List<string> { UserDirs.Roaming, UserDirs.Local };
            var found = new List<string[]>();
            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
                foreach (var d in Dirs(root))
                {
                    string n = Path.GetFileName(d);
                    if (Covered.Contains(n.ToLowerInvariant())) continue;
                    if (LooksElectron(d)) { found.Add(new[] { n, d }); continue; }
                    // one level more: "Vendor\App" (Notion, Obsidian and Signal sit at the top; some apps one folder down)
                    foreach (var s in Dirs(d))
                        if (LooksElectron(s)) found.Add(new[] { n + " " + Path.GetFileName(s), s });
                }
            return found.GroupBy(f => f[1], StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }
        // only Chromium's own cache folders inside them (never Local Storage, IndexedDB or settings: those are your data)
        static List<string> ElectronCaches() { return ElectronApps().SelectMany(a => Caches.ChromiumCaches(a[1], 1)).ToList(); }

        // ---------- browser website storage (Service Worker) ----------
        public static Func<List<string>> BrowserRootsOverride;   // self-test: browsers' "User Data" folders
        public static List<string> ServiceWorkerCaches()
        {
            string L = UserDirs.Local;
            var roots = BrowserRootsOverride != null ? BrowserRootsOverride() : new List<string> {
                Path.Combine(L, "Google", "Chrome", "User Data"), Path.Combine(L, "Microsoft", "Edge", "User Data"),
                Path.Combine(L, "BraveSoftware", "Brave-Browser", "User Data"), Path.Combine(L, "Vivaldi", "User Data") };
            var l = new List<string>();
            foreach (var r in roots)
                foreach (var p in Dirs(r))
                {
                    string n = Path.GetFileName(p);
                    if (n != "Default" && !n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                    l.Add(Path.Combine(p, "Service Worker", "CacheStorage"));
                    l.Add(Path.Combine(p, "Service Worker", "ScriptCache"));
                }
            return l;
        }

        // ---------- more developer caches ----------
        public static List<string> DevCaches()
        {
            string L = UserDirs.Local, U = UserDirs.Home;
            var l = new List<string> {
                Path.Combine(L, "pnpm", "store"), Path.Combine(L, "pnpm-store"), Path.Combine(U, ".bun", "install", "cache"), Path.Combine(L, "Yarn", "Berry", "cache"),
                Path.Combine(L, "deno"), Path.Combine(L, "Unity", "cache"), Path.Combine(U, ".cargo", "registry", "src"), Path.Combine(U, ".cargo", "git", "checkouts"),
                Path.Combine(L, "node-gyp", "Cache") };
            l.AddRange(OldJetBrains(Path.Combine(L, "JetBrains")));
            return l;
        }

        // "IntelliJIdea2024.1" and "IntelliJIdea2025.2": the caches of the older version of each IDE (its settings live elsewhere)
        static readonly Regex JbRe = new Regex(@"^(?<p>[A-Za-z]+?)(?<v>20\d\d\.\d)$");
        public static List<string> OldJetBrains(string dir)
        {
            var parsed = new List<Tuple<string, Version, string>>();
            foreach (var d in Dirs(dir))
            {
                var m = JbRe.Match(Path.GetFileName(d)); Version v;
                if (m.Success && Version.TryParse(m.Groups["v"].Value, out v)) parsed.Add(Tuple.Create(m.Groups["p"].Value.ToLowerInvariant(), v, d));
            }
            return parsed.GroupBy(p => p.Item1).SelectMany(g => g.OrderByDescending(p => p.Item2).Skip(1).Select(p => p.Item3)).ToList();
        }

        // ---------- game launchers ----------
        public static List<string> LauncherCaches()
        {
            string L = UserDirs.Local;
            string PD = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var l = new List<string>();
            foreach (var r in new[] { Path.Combine(L, "Battle.net"), Path.Combine(L, "Blizzard Entertainment"), Path.Combine(L, "Riot Games"), Path.Combine(L, "Ubisoft Game Launcher") })
                l.AddRange(Caches.ChromiumCaches(r, 4));
            l.Add(Path.Combine(L, "Roblox", "logs"));
            l.Add(Path.Combine(L, "Ubisoft Game Launcher", "logs"));
            l.Add(Path.Combine(L, "Riot Games", "Riot Client", "Logs"));
            if (!string.IsNullOrEmpty(PD)) l.Add(Path.Combine(PD, "GOG.com", "Galaxy", "webcache"));
            return l;
        }

        // ---------- old installers in Downloads ----------
        public static Func<string> DownloadsOverride;          // self-test
        public static Func<List<string>> InstalledOverride;    // self-test: installed apps' names
        static readonly string[] SetupWords = { "setup", "installer", "install", "win64", "win32", "x64", "x86", "amd64", "arm64", "windows", "win", "full", "offline", "online", "web", "stub", "latest", "release", "stable", "user", "system",
            "app", "the", "pro", "free", "tool", "tools", "client", "desktop", "update", "updater", "portable", "beta", "msi", "exe", "new", "my" };

        // "vlc-3.0.20-win64.exe" -> "vlc"; "ChromeSetup.exe" -> "chrome"; "Firefox Installer.exe" -> "firefox"
        public static string ProductOf(string file)
        {
            string n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            n = Regex.Replace(n, @"\(\d+\)$", "");                                     // "setup (1)"
            n = Regex.Replace(n, @"([a-z])(setup|installer|install)\b", "$1 ");        // "chromesetup"
            var words = Regex.Split(n, @"[^a-z0-9]+").Where(w => w.Length > 0 && !SetupWords.Contains(w) && !Regex.IsMatch(w, @"^v?\d+$")).ToList();
            return words.Count == 0 ? "" : words[0];
        }

        public static List<string> OldInstallers(DateTime now)
        {
            string dl = DownloadsOverride != null ? DownloadsOverride() : Path.Combine(UserDirs.Home ?? "", "Downloads");
            var l = new List<string>();
            if (string.IsNullOrEmpty(dl) || !Directory.Exists(dl) || IsLink(dl)) return l;
            List<string> installed;
            try { installed = InstalledOverride != null ? InstalledOverride() : AppsData.ReadInstalled().Select(a => a.Name ?? "").ToList(); }
            catch { return l; }
            var names = installed.Select(Leftovers.N).Where(n => n.Length >= 3).ToList();
            FileInfo[] files;
            try { files = new DirectoryInfo(dl).GetFiles(); } catch { return l; }
            foreach (var f in files)
            {
                string ext = f.Extension.ToLowerInvariant();
                if (ext != ".exe" && ext != ".msi" && ext != ".msix" && ext != ".msixbundle") continue;
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0 || (now - f.LastWriteTime).TotalDays < 60) continue;
                string p = ProductOf(f.Name);
                if (p.Length < 3) continue;
                if (names.Any(n => n.Contains(p))) l.Add(f.FullName);
            }
            return l;
        }

        public static List<CleanItem> Items()
        {
            return new List<CleanItem>
            {
                new CleanItem { Name = "Other apps' web caches (Slack, Notion, Signal...)", Tier = Tier.Safe, Where = "App data of apps built on Chromium (found by their folders)",
                    Info = "The same web cache folders (Cache, Code Cache, GPUCache) in every other app built like Discord or Teams. Messages, notes and settings are kept. Close those apps first: what an open app is using is skipped.",
                    Measure = () => Sum(ElectronCaches()), Clean = log => ClearAll(ElectronCaches()) },
                new CleanItem { Name = "Browser website storage (Service Worker caches)", Tier = Tier.Review, Where = "Chrome, Edge, Brave, Vivaldi profiles\\Service Worker\\CacheStorage",
                    Procs = new[] { "chrome", "msedge", "brave", "vivaldi" },
                    Info = "Copies of websites' files that sites keep for themselves (often several GB). Sites download them again. Web apps you use offline lose their offline copy until you open them online. History, passwords and logins are kept. The browsers must be closed.",
                    Measure = () => MeasureAll(ServiceWorkerCaches()), Clean = log => ClearAll(ServiceWorkerCaches()) },
                new CleanItem { Name = "More developer caches (pnpm, Bun, Unity, Rust, old JetBrains)", Tier = Tier.Safe, Where = @"pnpm\store, .bun\install\cache, Unity\cache, .cargo\registry\src, JetBrains\<older versions> ...",
                    Procs = new[] { "idea64", "pycharm64", "rider64", "webstorm64", "clion64", "goland64", "Unity" },
                    Info = "Download caches tools fill again when needed (needs internet), and the caches of JetBrains IDE versions you updated from (the newest of each is kept; settings are kept). Projects are not touched.",
                    Measure = () => DevCaches().Sum(d => Fs.SizeOf(d)), Clean = log => { foreach (var d in DevCaches()) { if (d.IndexOf(@"\JetBrains\", StringComparison.OrdinalIgnoreCase) >= 0) Fs.Delete(d); else Fs.ClearIn(d); } } },
                new CleanItem { Name = "Game launcher caches (Battle.net, Riot, Ubisoft, Roblox, GOG)", Tier = Tier.Safe, Where = "Launchers' web caches and logs",
                    Procs = new[] { "Battle.net", "Agent", "RiotClientServices", "upc", "UbisoftConnect", "RobloxPlayerBeta", "GalaxyClient" },
                    Info = "Store pages, pictures and logs the launchers saved. Your games and saves are not touched. The launchers must be closed.",
                    Measure = () => MeasureAll(LauncherCaches()), Clean = log => ClearAll(LauncherCaches()) },
                new CleanItem { Name = "Old installers in Downloads", Tier = Tier.Review, Where = "Downloads: setup files of apps that are installed, over 60 days old",
                    Info = "Setup files (.exe, .msi) you downloaded for apps that are now installed. They go to Undo for 7 days first. You can always download a newer one from the app's site.",
                    Measure = () => Sum(OldInstallers(DateTime.Now)), Clean = log => { var f = OldInstallers(DateTime.Now); if (f.Count > 0) Quarantine.MoveToUndo("Old installers in Downloads (" + f.Count + ")", f, log); } },
            };
        }
    }
}
