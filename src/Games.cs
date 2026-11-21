// Tools > Games: installed games from Steam, Epic, GOG and the Xbox app, biggest first, with when each was last played
// (Steam keeps that). Elixar never deletes or moves a game itself: each store does that properly (it knows its own files),
// so the buttons open the game in its store, and the page says how each store moves games to another drive.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class Game
    {
        public string Name = "", Store = "", Folder = "", Id = "";
        public long Bytes; public DateTime? LastPlayed;
        public string Drive { get { try { return Path.GetPathRoot(Folder).TrimEnd('\\'); } catch { return ""; } } }
        public bool Unplayed(DateTime now) { return LastPlayed.HasValue && (now - LastPlayed.Value).TotalDays > 182; }
    }

    public static class Games
    {
        public static Func<string> SteamOverride;                 // self-test: Steam's folder
        public static Func<string> EpicManifestsOverride;         // self-test
        public static Func<List<string>> XboxRootsOverride;       // self-test
        public static Func<List<string[]>> GogOverride;           // self-test: { id, name, folder }

        public static List<Game> Read(bool measure)
        {
            var l = new List<Game>();
            try { l.AddRange(Steam()); } catch { }
            try { l.AddRange(Epic()); } catch { }
            try { l.AddRange(Gog(measure)); } catch { }
            try { l.AddRange(Xbox(measure)); } catch { }
            return l.GroupBy(g => g.Folder.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase).Select(x => x.First()).OrderByDescending(g => g.Bytes).ToList();
        }

        // ---------- Steam: libraryfolders.vdf, then each appmanifest_*.acf ----------
        public static Dictionary<string, string> Vdf(string text)
        {
            // flat "key" "value" pairs, with the path of the sections ("libraryfolders/1/path")
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = new List<string>(); string pending = null;
            foreach (Match m in Regex.Matches(text ?? "", "\"((?:[^\"\\\\]|\\\\.)*)\"|([{}])"))
            {
                if (m.Groups[2].Success) { if (m.Groups[2].Value == "{") { path.Add(pending ?? ""); pending = null; } else if (path.Count > 0) path.RemoveAt(path.Count - 1); continue; }
                string s = m.Groups[1].Value.Replace("\\\\", "\\");
                if (pending == null) pending = s;
                else { d[string.Join("/", path.Concat(new[] { pending }))] = s; pending = null; }
            }
            return d;
        }

        static string SteamDir()
        {
            if (SteamOverride != null) return SteamOverride();
            if (!Win.IsWindows) return null;
            foreach (var v in new[] { Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"), Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") })
                try { using (v) { string p = v == null ? null : (v.GetValue("SteamPath") ?? v.GetValue("InstallPath")) as string; if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\'); } } catch { }
            return null;
        }

        static long Long(Dictionary<string, string> d, string k) { string v; long n; return d.TryGetValue(k, out v) && long.TryParse(v, out n) ? n : 0; }

        public static List<Game> Steam()
        {
            var l = new List<Game>();
            string steam = SteamDir(); if (steam == null) return l;
            var libs = new List<string> { steam };
            string lf = Path.Combine(Path.Combine(steam, "steamapps"), "libraryfolders.vdf");
            if (File.Exists(lf) && new FileInfo(lf).Length < (4 << 20))
                foreach (var kv in Vdf(File.ReadAllText(lf)))
                    if (kv.Key.EndsWith("/path", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(kv.Key, @"^libraryfolders/\d+$", RegexOptions.IgnoreCase)) libs.Add(kv.Value);
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                    try
                    {
                        if (new FileInfo(acf).Length > (1 << 20)) continue;
                        var d = Vdf(File.ReadAllText(acf));
                        string id, name, dir;
                        if (!d.TryGetValue("AppState/appid", out id) || !Regex.IsMatch(id, @"^\d{1,10}$") || !d.TryGetValue("AppState/name", out name) || !d.TryGetValue("AppState/installdir", out dir)) continue;
                        if (id == "228980") continue;   // Steamworks Common Redistributables
                        var g = new Game { Name = name, Store = "Steam", Id = id, Folder = Path.Combine(Path.Combine(apps, "common"), dir), Bytes = Long(d, "AppState/SizeOnDisk") };
                        long lp = Long(d, "AppState/LastPlayed");
                        if (lp > 0) g.LastPlayed = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(lp).ToLocalTime();
                        l.Add(g);
                    }
                    catch { }
            }
            return l;
        }

        // ---------- Epic: one .item file (JSON) per game ----------
        public static List<Game> Epic()
        {
            var l = new List<Game>();
            string dir = EpicManifestsOverride != null ? EpicManifestsOverride() : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return l;
            foreach (var f in Directory.GetFiles(dir, "*.item"))
            {
                var d = BrowserExtensions.ReadJson(f, 1 << 20);
                if (d == null) continue;
                object n, loc, size, app;
                if (!d.TryGetValue("DisplayName", out n) || !d.TryGetValue("InstallLocation", out loc)) continue;
                var g = new Game { Name = Convert.ToString(n), Store = "Epic", Folder = Convert.ToString(loc) };
                if (d.TryGetValue("InstallSize", out size)) { long b; if (long.TryParse(Convert.ToString(size, CultureInfo.InvariantCulture), out b)) g.Bytes = b; }
                if (d.TryGetValue("AppName", out app)) g.Id = Convert.ToString(app);
                l.Add(g);
            }
            return l;
        }

        // ---------- GOG: listed by GOG Galaxy in the machine's registry ----------
        public static List<Game> Gog(bool measure)
        {
            var l = new List<Game>();
            var rows = new List<string[]>();
            if (GogOverride != null) rows = GogOverride();
            else if (Win.IsWindows)
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
                        if (k != null)
                            foreach (var id in k.GetSubKeyNames())
                                using (var g = k.OpenSubKey(id)) { if (g == null) continue; rows.Add(new[] { id, g.GetValue("gameName") as string ?? id, g.GetValue("path") as string ?? "" }); }
                }
                catch { }
            foreach (var r in rows.Where(r => r[2].Length > 0 && Directory.Exists(r[2])))
                l.Add(new Game { Name = r[1], Store = "GOG", Id = r[0], Folder = r[2], Bytes = measure ? Size(r[2]) : 0 });
            return l;
        }

        // ---------- the Xbox app: \XboxGames on any drive ----------
        public static List<Game> Xbox(bool measure)
        {
            var l = new List<Game>();
            var roots = XboxRootsOverride != null ? XboxRootsOverride() : DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => Path.Combine(d.RootDirectory.FullName, "XboxGames")).ToList();
            foreach (var r in roots.Where(Directory.Exists))
                foreach (var d in Directory.GetDirectories(r))
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0 || Path.GetFileName(d).Equals("GameSave", StringComparison.OrdinalIgnoreCase)) continue;
                    l.Add(new Game { Name = Path.GetFileName(d), Store = "Xbox", Folder = d, Bytes = measure ? Size(d) : 0 });
                }
            return l;
        }

        // a folder's size without following links
        public static long Size(string dir)
        {
            long n = 0; var st = new Stack<string>(); st.Push(dir); int seen = 0;
            while (st.Count > 0 && seen < 500000)
            {
                var cur = st.Pop();
                try
                {
                    var di = new DirectoryInfo(cur);
                    foreach (var f in di.EnumerateFiles()) { seen++; try { if ((f.Attributes & FileAttributes.ReparsePoint) == 0) n += f.Length; } catch { } }
                    foreach (var s in di.EnumerateDirectories()) try { if ((s.Attributes & FileAttributes.ReparsePoint) == 0) st.Push(s.FullName); } catch { }
                }
                catch { }
            }
            return n;
        }

        // the store's own page for the game (it uninstalls and moves games properly)
        public static string StoreLink(Game g)
        {
            if (g.Store == "Steam" && Regex.IsMatch(g.Id, @"^\d{1,10}$")) return "steam://nav/games/details/" + g.Id;
            if (g.Store == "Epic") return "com.epicgames.launcher://store/library";
            if (g.Store == "Xbox") return "ms-windows-store://downloadsandupdates";
            if (g.Store == "GOG") return "goggalaxy://openGameView/" + (Regex.IsMatch(g.Id, @"^\d{1,12}$") ? g.Id : "");
            return null;
        }
        public static string UninstallLink(Game g) { return g.Store == "Steam" && Regex.IsMatch(g.Id, @"^\d{1,10}$") ? "steam://uninstall/" + g.Id : null; }

        public static string HowToMove(string store)
        {
            if (store == "Steam") return "Steam: Settings > Storage, tick the game, Move.";
            if (store == "Epic") return "Epic: Library, the game's ... menu, Manage, Move.";
            if (store == "Xbox") return "Xbox app: the game's ... menu, Manage, Files, Move.";
            if (store == "GOG") return "GOG Galaxy: the game's settings, Manage installation, Move.";
            return "";
        }
    }
}
