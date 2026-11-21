// 1.2.72: more things to clean.
// - Other apps' web caches: any Electron app (Slack, Notion, Signal, Obsidian, Figma...) found by its folder layout, not by a list
// - Browser website storage (Service Worker caches) in Chrome, Edge, Brave and Vivaldi: a review item, never ticked by itself
// - More developer caches: Deno, Unity, Rust sources, caches of old JetBrains IDE versions
//   (not the pnpm store or Bun's cache: projects' files are hard links into them, so deleting frees almost nothing and
//    the scan would promise space that never comes back; not Yarn 2+'s global cache: Plug'n'Play projects run straight from it)
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

        // an Electron app's own folder: Chromium's cache folders next to its "Preferences" or "Local State" file.
        // A plain "Cache" folder isn't enough (any app can have one, with its own data in it): it takes one only Chromium makes,
        // GPUCache, Code Cache, or a Cache holding Chromium's Cache_Data.
        public static bool LooksElectron(string dir)
        {
            try
            {
                bool marker = File.Exists(Path.Combine(dir, "Preferences")) || File.Exists(Path.Combine(dir, "Local State"));
                bool cache = Directory.Exists(Path.Combine(dir, "GPUCache")) || Directory.Exists(Path.Combine(dir, "Code Cache")) || Directory.Exists(Path.Combine(dir, "Cache", "Cache_Data"));
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
        // and only right inside the app's folder: a "cache" deeper down belongs to the app itself, not to Chromium
        static List<string> ElectronCaches() { return ElectronApps().SelectMany(a => Caches.ChromiumCaches(a[1], 0)).Where(RealChromiumCache).ToList(); }

        // a folder called "Cache" (any case) is Chromium's web cache only when it is laid out like one: Cache_Data inside
        // (Chromium 92 and later), or its index and data_0 files (older). An app's own "cache" folder, with its own data, isn't.
        // The other names (Code Cache, GPUCache, DawnCache...) only Chromium makes.
        public static bool RealChromiumCache(string dir)
        {
            if (!string.Equals(Path.GetFileName(dir), "Cache", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                return Directory.Exists(Path.Combine(dir, "Cache_Data")) ||
                       (File.Exists(Path.Combine(dir, "index")) && File.Exists(Path.Combine(dir, "data_0")));
            }
            catch { return false; }
        }

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
            string deno = Path.Combine(L, "deno");
            var l = new List<string> {
                // Deno: only its downloads and compiled code (location_data is scripts' saved data, kept)
                Path.Combine(deno, "deps"), Path.Combine(deno, "gen"), Path.Combine(deno, "npm"), Path.Combine(deno, "registries"), Path.Combine(deno, "remote"),
                Path.Combine(L, "Unity", "cache"), Path.Combine(U, ".cargo", "registry", "src"), Path.Combine(U, ".cargo", "git", "checkouts"),
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
        // Business agent: measuring the second and later accounts, so the PC's shared part (GOG's, in ProgramData) is counted once
        [ThreadStatic] public static bool AccountOnly;
        public static List<string> LauncherCaches()
        {
            string L = UserDirs.Local;
            string PD = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var l = new List<string>();
            foreach (var r in new[] { Path.Combine(L, "Battle.net"), Path.Combine(L, "Blizzard Entertainment"), Path.Combine(L, "Riot Games"), Path.Combine(L, "Ubisoft Game Launcher") })
                l.AddRange(Caches.ChromiumCaches(r, 4).Where(RealChromiumCache));   // a launcher's own "cache" (game ownership, settings) stays
            l.Add(Path.Combine(L, "Roblox", "logs"));
            l.Add(Path.Combine(L, "Ubisoft Game Launcher", "logs"));
            l.Add(Path.Combine(L, "Riot Games", "Riot Client", "Logs"));
            if (!string.IsNullOrEmpty(PD) && !AccountOnly) l.Add(Path.Combine(PD, "GOG.com", "Galaxy", "webcache"));   // the PC's, not an account's
            return l;
        }

        // ---------- old installers in Downloads ----------
        public static Func<string> DownloadsOverride;          // self-test
        public static Func<List<string>> InstalledOverride;    // self-test: installed apps' names
        static readonly string[] SetupWords = { "setup", "installer", "install", "win64", "win32", "x64", "x86", "amd64", "arm64", "windows", "win", "full", "offline", "online", "web", "stub", "latest", "release", "stable", "user", "system",
            "app", "the", "pro", "free", "tool", "tools", "client", "desktop", "update", "updater", "portable", "beta", "msi", "exe", "new", "my", "bit", "64bit", "32bit" };

        // "vlc-3.0.20-win64.exe" -> "vlc"; "ChromeSetup.exe" -> "chrome"; "Firefox Installer.exe" -> "firefox"
        public static string ProductOf(string file)
        {
            string n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            n = Regex.Replace(n, @"\(\d+\)$", "");                                     // "setup (1)"
            n = Regex.Replace(n, @"([a-z])(setup|installer|install)\b", "$1 ");        // "chromesetup"
            var words = Regex.Split(n, @"[^a-z0-9]+").Where(w => w.Length > 0 && !SetupWords.Contains(w) && !Regex.IsMatch(w, @"^v?\d+$")).ToList();
            return string.Concat(words);   // "google-earth-pro-setup" -> "googleearth": all of it, so Google Chrome doesn't count for it
        }

        // the product is whole words of the app's name, one after another: "googleearth" in "Google Earth Pro", "chrome" in
        // "Google Chrome"; "git" not in "GitHub Desktop", "art" not in "Samsung Smart Switch"
        public static bool NameHas(List<string> words, string product)
        {
            for (int i = 0; i < words.Count; i++)
            {
                string acc = "";
                for (int j = i; j < words.Count && acc.Length < product.Length; j++)
                {
                    acc += words[j];
                    if (acc == product) return true;
                }
            }
            return false;
        }

        // an .exe counts as a setup file only when its name says so (setup / install, a version number, a build like x64);
        // "putty.exe" or "procexp.exe" is a program you run from Downloads, not an installer
        public static bool LooksInstaller(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".msi" || ext == ".msix" || ext == ".msixbundle") return true;
            if (ext != ".exe") return false;
            string n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            return Regex.IsMatch(n, @"setup|install") || Regex.IsMatch(n, @"\d+\.\d+") ||
                   Regex.IsMatch(n, @"(^|[^a-z])(win32|win64|x64|x86|amd64|arm64|64-?bit|32-?bit)([^a-z]|$)");
        }

        public static List<string> OldInstallers(DateTime now)
        {
            string dl = DownloadsOverride != null ? DownloadsOverride() : Path.Combine(UserDirs.Home ?? "", "Downloads");
            var l = new List<string>();
            if (string.IsNullOrEmpty(dl) || !Directory.Exists(dl) || IsLink(dl)) return l;
            List<string> installed;
            try { installed = InstalledOverride != null ? InstalledOverride() : AppsData.ReadInstalled().Select(a => a.Name ?? "").ToList(); }
            catch { return l; }
            // each installed app's name as words ("Google Chrome" -> google, chrome)
            var names = installed.Select(a => Regex.Split((a ?? "").ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length > 0).ToList()).Where(w => w.Count > 0).ToList();
            FileInfo[] files;
            try { files = new DirectoryInfo(dl).GetFiles(); } catch { return l; }
            foreach (var f in files)
            {
                if (!LooksInstaller(f.Name)) continue;
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0 || (now - f.LastWriteTime).TotalDays < 60) continue;
                string p = ProductOf(f.Name);
                if (p.Length < 3) continue;
                if (names.Any(n => NameHas(n, p))) l.Add(f.FullName);
            }
            return l;
        }

        public static List<CleanItem> Items()
        {
            return new List<CleanItem>
            {
                new CleanItem { PerAccount = true, Name = "Other apps' web caches (Slack, Notion, Signal...)", Tier = Tier.Safe, Where = "App data of apps built on Chromium (found by their folders)",
                    Info = "The same web cache folders (Cache, Code Cache, GPUCache) in every other app built like Discord or Teams. Messages, notes and settings are kept. Close those apps first: what an open app is using is skipped.",
                    Measure = () => Sum(ElectronCaches()), Clean = log => ClearAll(ElectronCaches()) },
                new CleanItem { PerAccount = true, Name = "Browser website storage (Service Worker caches)", Tier = Tier.Review, Where = "Chrome, Edge, Brave, Vivaldi profiles\\Service Worker\\CacheStorage",
                    Procs = new[] { "chrome", "msedge", "brave", "vivaldi" },
                    Info = "Copies of websites' files that sites keep for themselves (often several GB). Sites download them again. Web apps you use offline lose their offline copy until you open them online. History, passwords and logins are kept. The browsers must be closed.",
                    Measure = () => MeasureAll(ServiceWorkerCaches()), Clean = log => ClearAll(ServiceWorkerCaches()) },
                new CleanItem { PerAccount = true, Name = "More developer caches (Deno, Unity, Rust, old JetBrains)", Tier = Tier.Safe, Where = @"deno\deps, Unity\cache, .cargo\registry\src, JetBrains\<older versions> ...",
                    Procs = new[] { "idea64", "pycharm64", "rider64", "webstorm64", "clion64", "goland64", "Unity" },
                    Info = "Download caches tools fill again when needed (needs internet), and the caches of JetBrains IDE versions you updated from (the newest of each is kept; settings are kept). Projects are not touched.",
                    Measure = () => DevCaches().Sum(d => Fs.SizeOf(d)), Clean = log => { foreach (var d in DevCaches()) { if (d.IndexOf(@"\JetBrains\", StringComparison.OrdinalIgnoreCase) >= 0) Fs.Delete(d); else Fs.ClearIn(d); } } },
                new CleanItem { PerAccount = true, Name = "Game launcher caches (Battle.net, Riot, Ubisoft, Roblox, GOG)", Tier = Tier.Safe, Where = "Launchers' web caches and logs",
                    Procs = new[] { "Battle.net", "Agent", "RiotClientServices", "upc", "UbisoftConnect", "RobloxPlayerBeta", "GalaxyClient" },
                    Info = "Store pages, pictures and logs the launchers saved. Your games and saves are not touched. The launchers must be closed.",
                    Measure = () => MeasureAll(LauncherCaches()), Clean = log => ClearAll(LauncherCaches()) },
                new CleanItem { PerAccount = true, Name = "Old installers in Downloads", Tier = Tier.Review, Where = "Downloads: setup files of apps that are installed, over 60 days old",
                    Info = "Setup files (.exe, .msi) you downloaded for apps that are now installed. They go to Undo for 7 days first. You can always download a newer one from the app's site.",
                    Measure = () => Sum(OldInstallers(DateTime.Now)), Clean = log => { var f = OldInstallers(DateTime.Now); if (f.Count > 0) Quarantine.MoveToUndo("Old installers in Downloads (" + f.Count + ")", f, log); } },
            };
        }
    }
}
