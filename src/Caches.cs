// App, game and developer caches. All are rebuilt or re-downloaded automatically.
// None are ticked by default and none are part of the weekly auto-clean.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class Caches
    {
        // Chromium/Electron cache folders (Discord, Teams, VS Code, Epic, EA, ...). Only these exact names are cleared.
        static readonly string[] ChromiumCacheNames = { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache" };

        public static List<string> ChromiumCaches(string root, int maxDepth = 6)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;
            var stack = new Stack<Tuple<string, int>>();
            stack.Push(Tuple.Create(root, 0));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                string[] subs;
                try { subs = Directory.GetDirectories(cur.Item1); } catch { continue; }
                foreach (var s in subs)
                {
                    try { if ((new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    string name = Path.GetFileName(s);
                    if (ChromiumCacheNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) found.Add(s);
                    else if (cur.Item2 < maxDepth) stack.Push(Tuple.Create(s, cur.Item2 + 1));
                }
            }
            return found;
        }

        static IEnumerable<string> Dirs(string parent, string pattern)
        {
            try { return Directory.Exists(parent) ? Directory.GetDirectories(parent, pattern) : new string[0]; }
            catch { return new string[0]; }
        }

        // "publisher.name-1.2.3" or "publisher.name-1.2.3-win32-x64" -> ("publisher.name", 1.2.3)
        static readonly Regex ExtRe = new Regex(@"^(?<id>.+?)-(?<ver>\d+(\.\d+){1,3})(-.+)?$", RegexOptions.Compiled);

        // Older versions of the same VS Code extension (VS Code keeps them until it cleans up itself).
        public static List<string> OldExtensionFolders(string extDir)
        {
            var old = new List<string>();
            var parsed = new List<Tuple<string, Version, string>>();
            foreach (var d in Dirs(extDir, "*"))
            {
                var m = ExtRe.Match(Path.GetFileName(d));
                Version v;
                if (m.Success && Version.TryParse(m.Groups["ver"].Value, out v)) parsed.Add(Tuple.Create(m.Groups["id"].Value.ToLowerInvariant(), v, d));
            }
            foreach (var g in parsed.GroupBy(p => p.Item1))
                old.AddRange(g.OrderByDescending(p => p.Item2).Skip(1).Select(p => p.Item3));
            return old;
        }

        // Older version folders ("2.1.287", "2.1.293") inside one folder; keeps the newest.
        public static List<string> OldVersionFolders(string parent)
        {
            var vers = new List<Tuple<Version, string>>();
            foreach (var d in Dirs(parent, "*"))
            {
                Version v;
                if (Version.TryParse(Path.GetFileName(d), out v)) vers.Add(Tuple.Create(v, d));
            }
            return vers.OrderByDescending(x => x.Item1).Skip(1).Select(x => x.Item2).ToList();
        }

        public static string SteamPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var p = k == null ? null : k.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\');
                }
            }
            catch { }
            return @"C:\Program Files (x86)\Steam";
        }

        static long Sum(IEnumerable<string> paths) { return paths.Sum(p => Fs.SizeOf(p)); }
        static void Del(IEnumerable<string> paths) { foreach (var p in paths.ToList()) Fs.Delete(p); }
        static void ClearAll(IEnumerable<string> dirs) { foreach (var d in dirs.ToList()) Fs.ClearIn(d); }
        static long MeasureAll(IEnumerable<string> dirs) { return dirs.Sum(d => Fs.MeasureIn(d)); }

        public static List<CleanItem> Items()
        {
            string L = UserDirs.Local;
            string R = UserDirs.Roaming;
            string U = UserDirs.Home;
            string PD = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            Func<IEnumerable<string>> discord = () => new[] { "discord", "discordptb", "discordcanary" }.SelectMany(n => ChromiumCaches(Path.Combine(R, n)));
            Func<IEnumerable<string>> teams = () => ChromiumCaches(Path.Combine(L, "Packages", "MSTeams_8wekyb3d8bbwe", "LocalCache")).Concat(ChromiumCaches(Path.Combine(R, "Microsoft", "Teams")));
            Func<IEnumerable<string>> spotifyData = () => new[] { Path.Combine(L, "Spotify", "Data") }.Concat(Dirs(Path.Combine(L, "Packages"), "SpotifyAB.SpotifyMusic_*").Select(p => Path.Combine(p, "LocalCache", "Spotify", "Data")));
            Func<IEnumerable<string>> spotifyWeb = () => ChromiumCaches(Path.Combine(L, "Spotify"));
            Func<IEnumerable<string>> epic = () => Dirs(Path.Combine(L, "EpicGamesLauncher", "Saved"), "webcache*").Concat(new[] { Path.Combine(L, "EpicGamesLauncher", "Saved", "Logs") });
            Func<IEnumerable<string>> ea = () => ChromiumCaches(Path.Combine(L, "Electronic Arts")).Concat(ChromiumCaches(Path.Combine(R, "Electronic Arts")));
            string steam = SteamPath();
            Func<IEnumerable<string>> steamDirs = () => new[] { Path.Combine(steam, "appcache", "httpcache"), Path.Combine(steam, "steamapps", "shadercache"), Path.Combine(L, "Steam", "htmlcache") };
            Func<IEnumerable<string>> vscode = () => ChromiumCaches(Path.Combine(R, "Code"), 2).Concat(new[] { Path.Combine(R, "Code", "CachedData"), Path.Combine(R, "Code", "CachedExtensionVSIXs"), Path.Combine(R, "Code", "logs") });
            string extDir = Path.Combine(U, ".vscode", "extensions");
            Func<IEnumerable<string>> claudeOld = () => Dirs(Path.Combine(L, "Packages"), "Claude_*").Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude", "claude-code"))
                                                         .Concat(new[] { Path.Combine(R, "Claude", "claude-code") }).SelectMany(OldVersionFolders);
            string nvOta = Path.Combine(PD, "NVIDIA Corporation", "NVIDIA app", "UpdateFramework", "ota-artifacts");
            Func<IEnumerable<string>> dev = () => new[]
            {
                Path.Combine(L, "npm-cache"), Path.Combine(R, "npm-cache"), Path.Combine(L, "pip", "cache"), Path.Combine(L, "Yarn", "Cache"),
                Path.Combine(L, "NuGet", "v3-cache"), Path.Combine(L, "NuGet", "plugins-cache"), Path.Combine(L, "Temp", "NuGetScratch"),
                Path.Combine(U, ".gradle", "caches"), Path.Combine(L, "go-build"), Path.Combine(U, ".cargo", "registry", "cache"), Path.Combine(L, "pnpm-cache")
            };
            string nugetPkgs = Path.Combine(U, ".nuget", "packages");
            Func<IEnumerable<string>> telegram = () => Dirs(Path.Combine(R, "Telegram Desktop", "tdata"), "user_data*").Concat(new[] { Path.Combine(R, "Telegram Desktop", "tdata", "emoji") });
            Func<IEnumerable<string>> whatsapp = () => Dirs(Path.Combine(L, "Packages"), "5319275A.WhatsAppDesktop_*").SelectMany(p => ChromiumCaches(Path.Combine(p, "LocalCache"), 6))
                                                       .Concat(ChromiumCaches(Path.Combine(R, "WhatsApp"), 3));
            Func<IEnumerable<string>> zoom = () => ChromiumCaches(Path.Combine(R, "Zoom"), 4).Concat(new[] { Path.Combine(R, "Zoom", "logs") });
            Func<IEnumerable<string>> browsers = () =>
            {
                var l = new List<string>();
                foreach (var p in Dirs(Path.Combine(L, "Mozilla", "Firefox", "Profiles"), "*")) l.Add(Path.Combine(p, "cache2"));
                l.AddRange(Fs.ProfileCaches(Path.Combine(L, "BraveSoftware", "Brave-Browser", "User Data")));
                l.AddRange(Fs.ProfileCaches(Path.Combine(L, "Vivaldi", "User Data")));
                foreach (var o in new[] { "Opera Stable", "Opera GX Stable" }) l.AddRange(ChromiumCaches(Path.Combine(L, "Opera Software", o), 2));
                return l;
            };

            return new List<CleanItem>
            {
                new CleanItem { Name = "Discord cache", Tier = Tier.Safe, Where = Path.Combine(R, "discord") + @"\...\Cache", Procs = new[] { "Discord*" },
                    Info = "Images and code Discord downloaded. Your messages and settings are kept. Discord must be closed.",
                    Measure = () => Sum(discord()), Clean = log => ClearAll(discord()) },
                new CleanItem { Name = "Microsoft Teams cache", Tier = Tier.Safe, Where = "Teams app data (Cache, GPUCache, Code Cache)", Procs = new[] { "ms-teams", "Teams" },
                    Info = "Cached pictures and web files. Your chats are kept online. Teams must be closed.",
                    Measure = () => Sum(teams()), Clean = log => ClearAll(teams()) },
                new CleanItem { Name = "Spotify cache", Tier = Tier.Review, Where = Path.Combine(L, "Spotify", "Data"), Procs = new[] { "Spotify" },
                    Info = "Songs Spotify cached for streaming. If you downloaded songs for offline listening they may need to download again. Spotify must be closed.",
                    Measure = () => MeasureAll(spotifyData()) + Sum(spotifyWeb()), Clean = log => { ClearAll(spotifyData()); ClearAll(spotifyWeb()); } },
                new CleanItem { Name = "Epic Games Launcher cache", Tier = Tier.Safe, Where = Path.Combine(L, "EpicGamesLauncher", "Saved") + @"\webcache*, Logs", Procs = new[] { "EpicGamesLauncher", "EpicWebHelper" },
                    Info = "Store pages and logs the launcher saved. Your games are not touched. The launcher must be closed.",
                    Measure = () => MeasureAll(epic()), Clean = log => ClearAll(epic()) },
                new CleanItem { Name = "EA app cache", Tier = Tier.Safe, Where = Path.Combine(L, "Electronic Arts") + @"\...\Cache", Procs = new[] { "EADesktop" },
                    Info = "Web cache of the EA app. Your games are not touched. The EA app must be closed.",
                    Measure = () => Sum(ea()), Clean = log => ClearAll(ea()) },
                new CleanItem { Name = "Steam caches", Tier = Tier.Safe, Where = steam + @"\appcache\httpcache, steamapps\shadercache", Procs = new[] { "steam", "steamwebhelper" },
                    Info = "Steam's web cache and game shader caches. Games rebuild shaders (short stutter the first time). Your games are not touched. Steam must be closed.",
                    Measure = () => MeasureAll(steamDirs()), Clean = log => ClearAll(steamDirs()) },
                new CleanItem { Name = "VS Code cache", Tier = Tier.Safe, Where = Path.Combine(R, "Code") + @"\Cache, CachedData, logs", Procs = new[] { "Code" },
                    Info = "VS Code's caches and logs. Settings, extensions and open folders are kept. VS Code must be closed.",
                    Measure = () => vscode().Sum(d => Fs.MeasureIn(d)), Clean = log => ClearAll(vscode()) },
                new CleanItem { Name = "Old VS Code extension versions", Tier = Tier.Safe, Where = extDir, Procs = new[] { "Code" },
                    Info = "Older copies of extensions that were updated. The newest version of each extension is kept. VS Code must be closed.",
                    Measure = () => Sum(OldExtensionFolders(extDir)), Clean = log => Del(OldExtensionFolders(extDir)) },
                new CleanItem { Name = "Old Claude Code versions", Tier = Tier.Safe, Where = @"Claude app data\claude-code\<old versions>",
                    Info = "Older versions the Claude app kept after updating. The newest version is kept; files in use are skipped.",
                    Measure = () => Sum(claudeOld()), Clean = log => Del(claudeOld()) },
                new CleanItem { Name = "Developer caches (npm, pip, yarn, NuGet, Gradle, Go)", Tier = Tier.Safe, Where = @"npm-cache, pip\cache, Yarn\Cache, NuGet\v3-cache, .gradle\caches ...",
                    Info = "Package download caches. Tools download again when needed (needs internet). Projects are not touched.",
                    Measure = () => MeasureAll(dev()), Clean = log => ClearAll(dev()) },
                new CleanItem { Name = "NuGet global packages", Tier = Tier.Review, Where = nugetPkgs, Procs = new[] { "devenv", "dotnet", "msbuild" },
                    Info = "All NuGet packages your .NET projects use. They are downloaded again on the next build (needs internet; slower first build). Close Visual Studio first.",
                    Measure = () => Fs.MeasureIn(nugetPkgs), Clean = log => Fs.ClearIn(nugetPkgs) },
                new CleanItem { Name = "Telegram Desktop cache", Tier = Tier.Safe, Where = Path.Combine(R, "Telegram Desktop", "tdata") + @"\user_data", Procs = new[] { "Telegram" },
                    Info = "Photos, videos and files Telegram downloaded while you chatted. Everything stays in Telegram's cloud and downloads again when you open it. Telegram must be closed.",
                    Measure = () => MeasureAll(telegram()), Clean = log => ClearAll(telegram()) },
                new CleanItem { Name = "WhatsApp Desktop cache", Tier = Tier.Safe, Where = @"WhatsApp app data\...\Cache", Procs = new[] { "WhatsApp" },
                    Info = "WhatsApp's web cache on this PC. Your chats and media are kept. WhatsApp must be closed.",
                    Measure = () => Sum(whatsapp()), Clean = log => ClearAll(whatsapp()) },
                new CleanItem { Name = "Zoom cache and logs", Tier = Tier.Safe, Where = Path.Combine(R, "Zoom"), Procs = new[] { "Zoom" },
                    Info = "Zoom's web cache and logs. Recordings and settings are kept. Zoom must be closed.",
                    Measure = () => Sum(zoom()), Clean = log => ClearAll(zoom()) },
                new CleanItem { Name = "Other browsers' cache (Firefox, Brave, Opera, Vivaldi)", Tier = Tier.Safe, Where = "Browser profiles", Procs = new[] { "firefox", "brave", "opera", "vivaldi" },
                    Info = "Cached web pages and images. History, passwords and logins are kept. The browsers must be closed.",
                    Measure = () => MeasureAll(browsers()), Clean = log => ClearAll(browsers()) },
                new CleanItem { Name = "Docker unused data", Tier = Tier.Review, Where = "docker system prune",
                    Info = "Stopped containers, unused networks, dangling images and build cache, removed with Docker's own prune command. Running containers and images in use are kept. Docker Desktop must be running.",
                    Measure = () => Docker.Exe() == null ? 0 : -1,
                    Clean = log =>
                    {
                        string exe = Docker.Exe();
                        if (exe == null) { log("  Docker is not installed."); return; }
                        string o = Sys.Run(exe, "system prune -f", 30 * 60 * 1000);
                        log("  Docker: " + (Docker.ParseReclaimed(o) > 0 ? "reclaimed " + Fs.Fmt(Docker.ParseReclaimed(o)) : o.Trim().Split('\n').LastOrDefault()));
                    } },
                new CleanItem { Name = "NVIDIA app update downloads", Tier = Tier.Caution, Where = nvOta,
                    Info = "Driver and app installers the NVIDIA app downloaded for updates. Moved to Undo for 7 days first. The NVIDIA app downloads again if it needs them.",
                    Measure = () => Fs.MeasureIn(nvOta), Clean = log => Quarantine.MoveToUndo("NVIDIA app update downloads", Dirs(nvOta, "*"), log) },
            };
        }
    }
}
