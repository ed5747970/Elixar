// Folders growing fast: once a day Elixar writes down the size of the bigger folders on C: (your profile, AppData,
// ProgramData, Windows' temp and log folders), finds the ones growing fast, and studies each one - who writes there, what
// kind of files, whether old files are ever removed - to say plainly what it is:
//   Safe to keep in check  an app's logs, caches, crash dumps: you can allow a daily rule that deletes old ones
//   Your files             never deleted by Elixar
//   Windows                never deleted; the right tool is named
//   Looks unusual          no known owner plus unsigned programs, a hidden random-named folder, or many files turned into an
//                          unknown file type (how ransomware looks): never deleted (that could hide what happened), scan it
//   Look at it             anything else
// Only folder sizes and counts are kept, never file names. Nothing is deleted without a rule you allowed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public enum GrowKind { Safe, Yours, Windows, Unusual, Look }

    public class FastFolder { public string Path = ""; public long Now, Grew; public int Days; public bool New; }

    public class FolderStudy
    {
        public string Path = ""; public GrowKind Kind = GrowKind.Look;
        public string Owner = "", Inside = "", Verdict = "", Reason = "";
        public bool OwnerKnown, Hidden, RandomName, RansomLike; public int NewPrograms, Unsigned, Files; public double TidyShare; public long Bytes;
    }

    public class KeepRule { public string Path = ""; public int Days = 14; public DateTime Since; public int Runs; }

    public static class FolderGrowth
    {
        public const long MinTracked = 100L << 20;     // folders under 100 MB aren't written down
        public const long FastBytes = 1L << 30;        // 1 GB in a week (or in 2 days) is "fast"
        public const int KeepDays = 12;                // daily readings kept
        public static string OverrideDir;              // self-test: where the files go
        public static Func<List<string[]>> RootsOverride;          // self-test: { root, depth }
        public static Func<string, bool> SignedOverride;           // self-test: is this program signed?
        public static Func<string, string> KindRootOverride;       // self-test: "windows" / "yours" / null for a path
        // the findings are kept in your own profile: the daily check runs with your normal rights. They only ever lead to
        // a fresh look (every folder is studied again before anything is offered), never to a deletion by themselves.
        static string F(string n)
        {
            if (OverrideDir != null) return System.IO.Path.Combine(OverrideDir, n);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string d = System.IO.Path.Combine(System.IO.Path.Combine(string.IsNullOrEmpty(la) ? System.IO.Path.GetTempPath() : la, "Elixar"), "growing");
            try { Directory.CreateDirectory(d); } catch { }
            return System.IO.Path.Combine(d, n);
        }
        // the rules that delete are kept where only administrators can write, and run only as administrator
        static string A(string n) { return OverrideDir != null ? System.IO.Path.Combine(OverrideDir, n) : Paths.FileIn(n); }
        static string Ctx { get { return Win.IsWindows && Sys.IsAdmin() ? "admin" : "user"; } }   // admins see more folders: readings are only compared with readings made the same way
        static string SnapFile { get { return F("readings-" + Ctx + ".txt"); } }
        static string RulesFile { get { return A("keep-in-check.txt"); } }
        static string IgnoreFile { get { return F("growing-ignored.txt"); } }
        static string AlertedFile { get { return F("growing-alerted.txt"); } }
        static string NowFile { get { return F("growing-now.txt"); } }

        // ---------- what is measured ----------
        public static List<string[]> Roots()
        {
            if (RootsOverride != null) return RootsOverride();
            var l = new List<string[]>();
            Func<Environment.SpecialFolder, string> sf = f => { try { return Environment.GetFolderPath(f); } catch { return ""; } };
            string home = UserDirs.Home, pd = sf(Environment.SpecialFolder.CommonApplicationData), win = sf(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(home)) l.Add(new[] { home, "5" });
            if (!string.IsNullOrEmpty(pd)) l.Add(new[] { pd, "4" });
            if (!string.IsNullOrEmpty(win)) { l.Add(new[] { System.IO.Path.Combine(win, "Temp"), "3" }); l.Add(new[] { System.IO.Path.Combine(win, "Logs"), "3" }); }
            return l.Where(r => Directory.Exists(r[0])).ToList();
        }

        static bool Skip(string path)
        {
            string n = path.TrimEnd('\\', '/').ToLowerInvariant();
            if (n.EndsWith("$recycle.bin") || n.Contains(SecureWipe.FolderName.ToLowerInvariant())) return true;
            try { if (Win.IsWindows && (n == Paths.Data.TrimEnd('\\').ToLowerInvariant() || n.StartsWith(Paths.Data.TrimEnd('\\').ToLowerInvariant() + "\\"))) return true; } catch { }
            foreach (var q in Quarantine.Roots()) { string r = q.TrimEnd('\\', '/').ToLowerInvariant(); if (n == r || n.StartsWith(r + "\\") || n.StartsWith(r + "/")) return true; }
            return false;
        }

        // the size of every folder (with what's under it), down to a depth; links are never followed
        // null when it ran out of time: half a reading would make folders look new or shrunk the next day
        public static Dictionary<string, long> Measure(TimeSpan budget)
        {
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var sw = Stopwatch.StartNew();
            foreach (var r in Roots())
            {
                int depth; int.TryParse(r[1], out depth);
                Walk(r[0], 0, Math.Max(1, depth), sizes, sw, budget);
            }
            if (sw.Elapsed > budget) return null;
            return sizes.Where(kv => kv.Value >= MinTracked).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        }

        static long Walk(string dir, int level, int depth, Dictionary<string, long> sizes, Stopwatch sw, TimeSpan budget)
        {
            if (sw.Elapsed > budget || level > 64 || Skip(dir)) return 0;   // 64 levels: a planted endless tree can't stop the check
            long total = 0;
            try
            {
                var di = new DirectoryInfo(dir);
                foreach (var f in di.EnumerateFiles()) { try { if ((f.Attributes & FileAttributes.ReparsePoint) == 0) total += f.Length; } catch { } }
                foreach (var d in di.EnumerateDirectories())
                {
                    try { if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                    total += Walk(d.FullName, level + 1, depth, sizes, sw, budget);
                }
            }
            catch { }
            if (level <= depth) sizes[dir] = total;
            return total;
        }

        // ---------- daily readings ----------
        public static Dictionary<DateTime, Dictionary<string, long>> ReadSnaps()
        {
            var all = new Dictionary<DateTime, Dictionary<string, long>>();
            try
            {
                if (!File.Exists(SnapFile)) return all;
                foreach (var line in File.ReadAllLines(SnapFile))
                {
                    var p = line.Split('\t'); DateTime d; long b;
                    if (p.Length != 3 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) || !long.TryParse(p[1], out b)) continue;
                    Dictionary<string, long> m; if (!all.TryGetValue(d, out m)) all[d] = m = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    m[p[2]] = b;
                }
            }
            catch { }
            return all;
        }

        public static void SaveSnap(DateTime day, Dictionary<string, long> sizes)
        {
            var all = ReadSnaps();
            all[day.Date] = sizes;
            var lines = all.Where(kv => (day.Date - kv.Key).TotalDays < KeepDays).OrderBy(kv => kv.Key)
                           .SelectMany(kv => kv.Value.Select(x => kv.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + x.Value.ToString(CultureInfo.InvariantCulture) + "\t" + x.Key.Replace("\t", " ")));
            SafeFile.Save(SnapFile, string.Join("\n", lines) + "\n");
        }

        // ---------- which folders grow fast ----------
        public static List<FastFolder> Detect(Dictionary<DateTime, Dictionary<string, long>> snaps, DateTime today)
        {
            var res = new List<FastFolder>();
            Dictionary<string, long> now; if (!snaps.TryGetValue(today.Date, out now)) return res;
            Func<int, int, int, KeyValuePair<DateTime, Dictionary<string, long>>?> pick = (min, max, target) =>
            {
                var c = snaps.Where(kv => (today.Date - kv.Key).TotalDays >= min && (today.Date - kv.Key).TotalDays <= max).OrderBy(kv => Math.Abs((today.Date - kv.Key).TotalDays - target)).ToList();
                return c.Count > 0 ? c[0] : (KeyValuePair<DateTime, Dictionary<string, long>>?)null;
            };
            var week = pick(5, 9, 7); var two = pick(1, 3, 2);
            if (week == null && two == null) return res;
            var ign = Ignored();
            foreach (var kv in now)
            {
                if (ign.Contains(kv.Key)) continue;
                FastFolder f = null;
                if (two != null)
                {
                    long b; bool had = two.Value.Value.TryGetValue(kv.Key, out b);
                    long grew = kv.Value - (had ? b : 0);
                    if (grew >= FastBytes) f = new FastFolder { Path = kv.Key, Now = kv.Value, Grew = grew, Days = (int)(today.Date - two.Value.Key).TotalDays, New = !had };
                }
                if (f == null && week != null)
                {
                    long b; bool had = week.Value.Value.TryGetValue(kv.Key, out b);
                    long grew = kv.Value - (had ? b : 0);
                    bool doubled = had && b > 0 && kv.Value >= (512L << 20) && kv.Value >= 2 * b && grew >= (256L << 20);
                    if (grew >= FastBytes || doubled) f = new FastFolder { Path = kv.Key, Now = kv.Value, Grew = grew, Days = (int)(today.Date - week.Value.Key).TotalDays, New = !had };
                }
                if (f != null) res.Add(f);
            }
            // show the folder that actually grows, not each folder above it
            var keep = new List<FastFolder>();
            foreach (var f in res.OrderByDescending(x => x.Path.Length))
            {
                bool explained = keep.Any(c => c.Path.StartsWith(f.Path.TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && c.Grew * 10 >= f.Grew * 8);
                if (!explained) keep.Add(f);
            }
            return keep.OrderByDescending(x => x.Grew).Take(12).ToList();
        }

        // ---------- what a folder is ----------
        static readonly Regex TidyName = new Regex(@"\.(log|etl|tmp|temp|dmp|mdmp|hdmp|trace|evtx\.bak)$|\.log\.\d+$|\.log\d+$|\.\d+\.log$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex CacheDir = new Regex(@"^(cache|caches|cache2|code cache|gpucache|shadercache|dawncache|grshadercache|temp|tmp|crashdumps|crashpad|logs?|diagnostics)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // never a "tidy" file, whatever the folder is called: documents, pictures, videos, music, archives
        static readonly HashSet<string> UserTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pdf", ".odt", ".ods", ".rtf", ".csv", ".jpg", ".jpeg", ".png", ".gif", ".heic", ".webp", ".bmp", ".tif", ".tiff",
            ".raw", ".cr2", ".nef", ".psd", ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".mp3", ".wav", ".flac", ".m4a", ".zip", ".7z", ".rar", ".pst", ".ost", ".kdbx", ".key", ".pfx"
        };
        static bool Tidy(string fileName, bool inCacheDir)
        {
            string ext = Path.GetExtension(fileName);
            if (UserTypes.Contains(ext) || Programs.Contains(ext.ToLowerInvariant())) return false;
            return inCacheDir || TidyName.IsMatch(fileName);
        }
        static readonly string[] Programs = { ".exe", ".dll", ".scr", ".com", ".ps1", ".bat", ".cmd", ".vbs", ".js", ".jse", ".msi", ".hta" };
        static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".log", ".etl", ".tmp", ".dmp", ".txt", ".json", ".xml", ".db", ".sqlite", ".dat", ".bin", ".cache", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".mp4", ".mkv", ".mov", ".avi",
            ".mp3", ".wav", ".flac", ".m4a", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".zip", ".7z", ".rar", ".iso", ".exe", ".dll", ".msi", ".ldb", ".pak", ".idx", ".tar", ".gz",
            ".wim", ".esd", ".cab", ".vhdx", ".csv", ".ini", ".cfg", ".lock", ".tmp", ".part", ".crdownload", ".ts", ".m2ts", ".mpg", ".psd", ".raw", ".cr2", ".nef", ".js", ".css", ".html", ".ttf", ".otf"
        };

        // e.g. "x7f3k9q": letters and digits mixed back and forth, not a word
        public static bool RandomName(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Length < 6 || n.Length > 24 || !Regex.IsMatch(n, "^[a-z0-9]+$")) return false;
            int turns = 0; for (int i = 1; i < n.Length; i++) if (char.IsDigit(n[i]) != char.IsDigit(n[i - 1])) turns++;
            return turns >= 4;
        }

        static string RootKind(string path)
        {
            if (KindRootOverride != null) return KindRootOverride(path);
            Func<Environment.SpecialFolder, string> sf = f => { try { return Environment.GetFolderPath(f); } catch { return ""; } };
            Func<string, bool> under = r => !string.IsNullOrEmpty(r) && (path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
            string pd = sf(Environment.SpecialFolder.CommonApplicationData);
            if (under(sf(Environment.SpecialFolder.Windows)) || under(System.IO.Path.Combine(pd, "Microsoft")) || under(sf(Environment.SpecialFolder.ProgramFiles) + "\\WindowsApps")) return "windows";
            string home = UserDirs.Home;
            foreach (var y in new[] { sf(Environment.SpecialFolder.Desktop), sf(Environment.SpecialFolder.MyDocuments), sf(Environment.SpecialFolder.MyPictures), sf(Environment.SpecialFolder.MyVideos),
                                      sf(Environment.SpecialFolder.MyMusic), string.IsNullOrEmpty(home) ? "" : System.IO.Path.Combine(home, "Downloads"), Environment.GetEnvironmentVariable("OneDrive") ?? "" })
                if (under(y)) return "yours";
            return null;
        }

        // who may own a folder: installed apps only (Windows' list of installed programs: names, publishers, install folders).
        // Not running programs, services or startup entries - malware has those too, so they never vouch for a folder.
        public static Func<List<string>> NamesOverride;   // self-test
        public static List<string> OwnerNames()
        {
            if (NamesOverride != null) return NamesOverride();
            var names = new List<string>();
            if (!Win.IsWindows) return names;
            try
            {
                foreach (var a in AppsData.ReadInstalled())
                {
                    names.Add(a.Name); names.Add(a.Publisher);
                    foreach (Match m in Regex.Matches(a.Location ?? "", @"\\([^\\""]+)")) names.Add(m.Groups[1].Value);
                }
            }
            catch { }
            return names.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        }

        static readonly HashSet<string> Generic = new HashSet<string>
        {
            "system", "systems", "windows", "microsoft", "update", "updater", "updates", "service", "services", "host", "runtime", "common", "shared", "data", "local", "program", "programs",
            "app", "apps", "application", "applications", "temp", "cache", "logs", "log", "user", "users", "software", "files", "tools", "driver", "drivers", "client", "helper", "setup", "install", "installer"
        };

        static string OwnerOf(string path, List<string> names)
        {
            var segs = path.Split('\\', '/').Where(s => s.Length > 0).ToList();
            string[] skip = { "users", "appdata", "local", "roaming", "locallow", "programdata", "windows", "temp", "packages", "c:" };
            var cands = names.Select(n => Tuple.Create(n, Leftovers.N(n))).Where(t => t.Item2.Length >= 3 && !Generic.Contains(t.Item2)).ToList();
            foreach (var seg in segs.Skip(1))
            {
                if (skip.Contains(seg.ToLowerInvariant()) || string.Equals(seg, Environment.UserName, StringComparison.OrdinalIgnoreCase)) continue;
                string f = Leftovers.N(seg); if (f.Length < 3 || Generic.Contains(f)) continue;
                // the same name, a longer name that starts with it ("Zoom" - "Zoom Workplace"), or a folder that starts with a long app name
                var hit = cands.FirstOrDefault(t => t.Item2 == f) ?? cands.FirstOrDefault(t => f.Length >= 4 && t.Item2.StartsWith(f)) ?? cands.FirstOrDefault(t => t.Item2.Length >= 6 && f.StartsWith(t.Item2));
                if (hit != null) return hit.Item1;
            }
            return null;
        }

        static string Ago(TimeSpan t) { int d = (int)t.TotalDays; return d >= 60 ? (d / 30) + " months" : d >= 2 ? d + " days" : "today"; }

        public static FolderStudy Study(string path, List<string> names, DateTime now)
        {
            var s = new FolderStudy { Path = path };
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            try { s.Hidden = (new DirectoryInfo(path).Attributes & FileAttributes.Hidden) != 0; } catch { }
            s.RandomName = RandomName(name);
            string root = RootKind(path);
            string owner = root == null ? OwnerOf(path, names ?? new List<string>()) : null;
            s.OwnerKnown = owner != null;
            // what's inside (at most 50,000 files are looked at)
            long tidy = 0, all = 0; int seen = 0, recent = 0; DateTime oldest = DateTime.MaxValue, newest = DateTime.MinValue;
            var byExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); var recentExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var newPrograms = new List<string>();
            var st = new Stack<Tuple<string, bool>>(); st.Push(Tuple.Create(path, CacheDir.IsMatch(name)));
            while (st.Count > 0 && seen < 50000)
            {
                var cur = st.Pop();
                try
                {
                    var di = new DirectoryInfo(cur.Item1);
                    foreach (var f in di.EnumerateFiles())
                    {
                        if (++seen > 50000) break;
                        try
                        {
                            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            long len = f.Length; all += len;
                            string ext = f.Extension.ToLowerInvariant();
                            byExt[ext] = (byExt.ContainsKey(ext) ? byExt[ext] : 0) + 1;
                            if (Tidy(f.Name, cur.Item2)) tidy += len;
                            DateTime w = f.LastWriteTime;
                            if (w < oldest) oldest = w; if (w > newest) newest = w;
                            if ((now - w).TotalHours <= 24) { recent++; recentExt[ext] = (recentExt.ContainsKey(ext) ? recentExt[ext] : 0) + 1; }
                            if (Programs.Contains(ext) && (now - f.CreationTime).TotalDays <= 7) newPrograms.Add(f.FullName);
                        }
                        catch { }
                    }
                    foreach (var d in di.EnumerateDirectories())
                        try { if ((d.Attributes & FileAttributes.ReparsePoint) == 0) st.Push(Tuple.Create(d.FullName, cur.Item2 || CacheDir.IsMatch(d.Name))); } catch { }
                }
                catch { }
            }
            s.Files = seen; s.Bytes = all; s.TidyShare = all > 0 ? (double)tidy / all : 0; s.NewPrograms = newPrograms.Count;
            if (newPrograms.Count > 0)
            {
                var check = newPrograms.Take(20).ToList();
                if (SignedOverride != null) s.Unsigned = check.Count(p => !SignedOverride(p));
                else if (Win.IsWindows)
                    try { var r = Trust.CheckMany(check); s.Unsigned = check.Count(p => { string[] v; return !r.TryGetValue(p, out v) || v[0] != "Valid"; }); } catch { }
            }
            // many files changed to one file type nobody knows: how ransomware looks
            var topRecent = recentExt.OrderByDescending(kv => kv.Value).FirstOrDefault();
            // (apps write odd file types into their own AppData/ProgramData folders all the time: those aren't judged this way)
            bool appData = Regex.IsMatch(path, @"[\\/](AppData|ProgramData)[\\/]", RegexOptions.IgnoreCase) && s.OwnerKnown;
            s.RansomLike = !appData && recent >= 100 && topRecent.Key != null && topRecent.Value * 10 >= recent * 6 && !Known.Contains(topRecent.Key) && topRecent.Key.Length > 1;

            // in plain words
            var top = byExt.OrderByDescending(kv => kv.Value).FirstOrDefault();
            string kind = s.TidyShare >= 0.7 ? "log, cache and temp files" : "files";
            s.Inside = seen.ToString("N0", CultureInfo.InvariantCulture) + (seen >= 50000 ? "+" : "") + " " + kind + (top.Key != null && top.Key.Length > 0 ? " (mostly " + top.Key + ")" : "") +
                       (oldest != DateTime.MaxValue ? " · oldest " + Ago(now - oldest) : "") +
                       (recent > 0 ? " · " + recent.ToString("N0", CultureInfo.InvariantCulture) + " written in the last day" : "") +
                       (oldest != DateTime.MaxValue && (now - oldest).TotalDays > 30 && recent > 0 ? ", old ones never removed" : "") +
                       (s.NewPrograms > 0 ? " · " + s.NewPrograms + " new program file" + (s.NewPrograms == 1 ? "" : "s") + (s.Unsigned > 0 ? " (" + s.Unsigned + " not signed)" : "") : "") +
                       (s.Hidden ? " · hidden folder" : "");

            // the verdict
            // unsigned new programs are judged outside your own folders, Windows' folders and the temp folders (installers
            // unpack unsigned helpers in Temp all the time); the ransomware and hidden-random-folder signs are judged everywhere
            bool temp = Regex.IsMatch(path, @"[\\/](AppData[\\/]Local|Windows)[\\/]Temp([\\/][^\\/]+)?$", RegexOptions.IgnoreCase);
            bool odd = s.RansomLike || (root == null && !temp && s.Unsigned > 0 && !s.OwnerKnown) || (!s.OwnerKnown && root == null && s.Hidden && s.RandomName);
            if (odd)
            {
                s.Kind = GrowKind.Unusual;
                s.Owner = s.OwnerKnown ? owner : "No installed app owns it";
                s.Reason = s.RansomLike ? "Lots of files were changed to an unknown file type (" + topRecent.Key + ") in a day - this is how ransomware looks."
                         : s.Unsigned > 0 ? "New programs that aren't signed are being written here."
                         : "A hidden folder with a random name that no app owns.";
                s.Verdict = "This doesn't look like a normal app. Elixar won't delete it (that could hide what happened): scan it with Microsoft Defender first.";
            }
            else if (temp && root == null)
            {
                s.Kind = GrowKind.Safe; s.Owner = "Temporary files (apps and installers)";
                s.Verdict = "Temporary files that apps and installers leave behind. Old ones can be deleted safely - the Cleaner's Safe items clean them too.";
            }
            else if (root == "windows")
            {
                s.Kind = GrowKind.Windows; s.Owner = "Windows";
                bool cbs = path.IndexOf(@"\Logs\CBS", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf(@"\SoftwareDistribution", StringComparison.OrdinalIgnoreCase) >= 0;
                s.Verdict = cbs ? "Windows' own folder: Elixar doesn't delete here. A log that keeps growing here usually means a stuck update - Repair Windows fixes it."
                                : "Windows' own folder: Elixar doesn't delete here. The Cleaner's Windows items and Repair Windows are the safe way.";
            }
            else if (root == "yours")
            {
                s.Kind = GrowKind.Yours; s.Owner = "Your files";
                s.Verdict = "These are your own files: Elixar never deletes them by itself. Move them to another drive or to OneDrive if C: is getting full.";
            }
            else if (s.OwnerKnown && s.TidyShare >= 0.7 && s.NewPrograms == 0)
            {
                s.Kind = GrowKind.Safe; s.Owner = owner;
                s.Verdict = owner + " keeps old log, cache or temp files here. Deleting old ones doesn't affect " + owner + " or Windows.";
            }
            else
            {
                s.Kind = GrowKind.Look; s.Owner = s.OwnerKnown ? owner : "Not sure who writes here";
                s.Verdict = "Elixar can't tell for sure what this is, so it won't offer to delete anything here. Open it and look.";
            }
            return s;
        }

        // ---------- "keep it in check" rules ----------
        public static List<KeepRule> Rules()
        {
            var l = new List<KeepRule>();
            try
            {
                if (File.Exists(RulesFile))
                    foreach (var line in File.ReadAllLines(RulesFile))
                    {
                        var p = line.Split('\t'); int d, r; DateTime since;
                        if (p.Length == 4 && int.TryParse(p[0], out d) && int.TryParse(p[1], out r) && DateTime.TryParseExact(p[2], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out since))
                            l.Add(new KeepRule { Days = Math.Max(3, Math.Min(365, d)), Runs = r, Since = since, Path = p[3] });
                    }
            }
            catch { }
            return l;
        }

        static void SaveRules(List<KeepRule> l)
        {
            SafeFile.Save(RulesFile, string.Join("\n", l.Select(r => r.Days + "\t" + r.Runs + "\t" + r.Since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + r.Path)) + "\n");
        }

        // a rule is only ever made for a folder Elixar studied as "Safe to keep in check"
        public static string AddRule(FolderStudy s, int days)
        {
            if (s == null || s.Kind != GrowKind.Safe) return "Only an app's log, cache and temp folders can be kept in check.";
            string why = RuleProblem(s.Path); if (why != null) return why;
            var l = Rules().Where(r => !string.Equals(r.Path, s.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            l.Add(new KeepRule { Path = s.Path, Days = Math.Max(3, Math.Min(365, days)), Since = DateTime.Today });
            SaveRules(l);
            ChangeLog.Add("Settings", "Keep in check: delete old log, cache and temp files", s.Path, "", "older than " + days + " days, daily", "Allowed by you; the first two runs go to Undo", false);
            return null;
        }

        public static void RemoveRule(string path)
        {
            var l = Rules(); int n = l.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (n > 0) { SaveRules(l); ChangeLog.Add("Settings", "Stopped keeping a folder in check", path, "", "", "", false); }
        }

        // the daily task that runs the rules (as administrator, through Elixar's launcher): there while a rule exists
        public const string Task = Automation.Folder + "Keep in check";
        public static string Schedule(string exe)
        {
            string o; bool any = Rules().Count > 0;
            if (any)
            {
                // like every Elixar schedule: it starts the protected copy in Program Files, which only administrators can change
                string ierr = Install.Ensure();
                if (ierr != null) return "Elixar could not put its protected copy in " + Install.Dir + ": " + ierr;
            }
            int code = Automation.RunSchtasks(any ? Automation.CreateKeepArgs(exe) : Automation.DeleteArgs(Task), out o);
            return any && code != 0 ? "Windows didn't make the daily task: " + o : null;
        }

        static string RuleProblem(string path)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                if (Protect.IsProtected(full)) return "one of Windows' or your main folders";
                string k = RootKind(full); if (k != null) return k == "windows" ? "a Windows folder" : "your own files";
                if (!Directory.Exists(full)) return "the folder is gone";
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || !SafeDelete.RealPathMatches(full)) return "a link is in the way";
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        // the files a rule may delete: old, of a tidy type (or anything in a cache folder), not a program, not a link
        public static List<string> RuleFiles(KeepRule r, DateTime now)
        {
            var l = new List<string>();
            string name = System.IO.Path.GetFileName(r.Path.TrimEnd('\\', '/'));
            var st = new Stack<Tuple<string, bool>>(); st.Push(Tuple.Create(r.Path, CacheDir.IsMatch(name)));
            while (st.Count > 0 && l.Count < 100000)
            {
                var cur = st.Pop();
                try
                {
                    var di = new DirectoryInfo(cur.Item1);
                    foreach (var f in di.EnumerateFiles())
                        try
                        {
                            if ((f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue;
                            if (Programs.Contains(f.Extension.ToLowerInvariant())) continue;
                            if ((now - f.LastWriteTime).TotalDays < r.Days) continue;
                            if (Tidy(f.Name, cur.Item2)) l.Add(f.FullName);
                        }
                        catch { }
                    foreach (var d in di.EnumerateDirectories())
                        try { if ((d.Attributes & FileAttributes.ReparsePoint) == 0) st.Push(Tuple.Create(d.FullName, cur.Item2 || CacheDir.IsMatch(d.Name))); } catch { }
                }
                catch { }
            }
            return l;
        }

        // runs every rule once (daily); the first two runs of a rule go to Undo; returns the bytes freed
        public static long RunRules(DateTime now, Action<string> log)
        {
            long freed = 0;
            if (Win.IsWindows && !Sys.IsAdmin()) { if (log != null) log("Keep in check: needs administrator rights, nothing done."); return 0; }
            var rules = Rules();
            using (ChangeLog.In("Keep in check"))
                foreach (var r in rules)
                {
                    string why = RuleProblem(r.Path);
                    if (why != null) { if (log != null) log("Keep in check: skipped " + r.Path + " (" + why + ")"); continue; }
                    freed += RunRule(r, now, log);
                }
            SaveRules(rules);
            return freed;
        }

        public static long RunRule(KeepRule r, DateTime now, Action<string> log)
        {
            string why = RuleProblem(r.Path);   // checked again on every run, whoever asks for it
            if (why != null) { if (log != null) log("Keep in check: skipped " + r.Path + " (" + why + ")"); return 0; }
            var files = RuleFiles(r, now);
            if (files.Count == 0) { r.Runs++; return 0; }
            long bytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            long done;
            if (r.Runs < 2) done = Quarantine.MoveToUndo("Keep in check: " + System.IO.Path.GetFileName(r.Path.TrimEnd('\\', '/')), files, log, 7);
            else
            {
                done = 0;
                foreach (var f in files)
                {
                    long len = 0; try { len = new FileInfo(f).Length; } catch { }
                    bool gone; if (Win.IsWindows) gone = SafeDelete.Delete(f); else { Fs.Delete(f); gone = !File.Exists(f); }
                    if (gone) done += len;
                }
                ChangeLog.Add("Cleaning", "Kept a folder in check", r.Path, Fs.Fmt(bytes), Fs.Fmt(done) + " deleted", files.Count + " old log, cache or temp files older than " + r.Days + " days", false);
            }
            r.Runs++;
            if (log != null) log("Keep in check: " + r.Path + ": " + Fs.Fmt(done) + (r.Runs <= 2 ? " moved to Undo" : " deleted"));
            return done;
        }

        // ---------- ignored folders ----------
        public static HashSet<string> Ignored()
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(IgnoreFile)) foreach (var l in File.ReadAllLines(IgnoreFile)) if (l.Trim().Length > 0) h.Add(l.Trim()); } catch { }
            return h;
        }
        public static void Ignore(string path) { var h = Ignored(); h.Add(path); SafeFile.Save(IgnoreFile, string.Join("\n", h) + "\n"); }

        // ---------- the daily run (the low-space check, a few minutes after sign-in or at its time) ----------
        public class DayResult { public List<FastFolder> Fast = new List<FastFolder>(); public List<FolderStudy> Studies = new List<FolderStudy>(); public List<FolderStudy> Alert = new List<FolderStudy>(); public long Freed; }

        public static DayResult DailyRun(DateTime now, Action<string> log, Func<Dictionary<string, long>> measureOverride)
        {
            var res = new DayResult();
            var snaps = ReadSnaps();
            if (!snaps.ContainsKey(now.Date))
            {
                var sizes = measureOverride != null ? measureOverride() : Measure(TimeSpan.FromMinutes(8));
                if (sizes == null) { if (log != null) log("Growing folders: the reading took too long and wasn't kept."); return res; }
                SaveSnap(now, sizes);
                snaps = ReadSnaps();
            }
            res.Fast = Detect(snaps, now);
            var names = res.Fast.Count > 0 ? OwnerNames() : new List<string>();
            foreach (var f in res.Fast) res.Studies.Add(Study(f.Path, names, now));
            SaveNow(res.Fast, res.Studies);
            // a notification once a week per folder (at once again if it now looks unusual)
            var alerted = ReadAlerted();
            foreach (var s in res.Studies)
            {
                Tuple<DateTime, GrowKind> was;
                bool due = !alerted.TryGetValue(s.Path, out was) || (now - was.Item1).TotalDays >= 7 || (s.Kind == GrowKind.Unusual && was.Item2 != GrowKind.Unusual);
                if (due) { res.Alert.Add(s); alerted[s.Path] = Tuple.Create(now.Date, s.Kind); }
            }
            SafeFile.Save(AlertedFile, string.Join("\n", alerted.Where(kv => (now - kv.Value.Item1).TotalDays < 60).Select(kv => kv.Value.Item1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + kv.Value.Item2 + "\t" + kv.Key)) + "\n");
            return res;
        }

        static Dictionary<string, Tuple<DateTime, GrowKind>> ReadAlerted()
        {
            var d = new Dictionary<string, Tuple<DateTime, GrowKind>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(AlertedFile))
                    foreach (var l in File.ReadAllLines(AlertedFile))
                    {
                        var p = l.Split('\t'); DateTime t; GrowKind k;
                        if (p.Length == 3 && DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && Enum.TryParse(p[1], out k)) d[p[2]] = Tuple.Create(t, k);
                    }
            }
            catch { }
            return d;
        }

        // the latest findings, for the Dashboard tile: path, grew, now, days, kind
        static void SaveNow(List<FastFolder> fast, List<FolderStudy> st)
        {
            var lines = fast.Select((f, i) => f.Grew + "\t" + f.Now + "\t" + f.Days + "\t" + (i < st.Count ? st[i].Kind.ToString() : "Look") + "\t" + f.Path);
            SafeFile.Save(NowFile, string.Join("\n", lines) + "\n");
        }

        public static List<Tuple<FastFolder, GrowKind>> Current()
        {
            var l = new List<Tuple<FastFolder, GrowKind>>();
            try
            {
                var roots = Roots().Select(r => r[0].TrimEnd('\\', '/')).ToList();
                // only real folders inside the measured places: never a file, a link or a path that climbs out
                Func<string, bool> inside = path => roots.Any(r => path.StartsWith(r + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) && path.IndexOf("..", StringComparison.Ordinal) < 0
                    && Directory.Exists(path) && RealFolder(path);
                if (File.Exists(NowFile) && (DateTime.Now - File.GetLastWriteTime(NowFile)).TotalDays < 3)
                    foreach (var line in File.ReadAllLines(NowFile))
                    {
                        var p = line.Split('\t'); long g, n; int d; GrowKind k;
                        if (p.Length == 5 && long.TryParse(p[0], out g) && long.TryParse(p[1], out n) && int.TryParse(p[2], out d) && Enum.TryParse(p[3], out k) && inside(p[4]) && !Ignored().Contains(p[4]))
                            l.Add(Tuple.Create(new FastFolder { Grew = g, Now = n, Days = d, Path = p[4] }, k));
                    }
            }
            catch { }
            return l;
        }

        static bool RealFolder(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && SafeDelete.NoLinks(path); } catch { return false; }
        }

        public static string GrewText(FastFolder f) { return "+" + Fs.Fmt(f.Grew) + (f.Days <= 3 ? " in " + f.Days + " day" + (f.Days == 1 ? "" : "s") : " this week"); }

        // the notification text for the daily run
        public static void Notify(DayResult r)
        {
            if (r.Alert.Count == 0) return;
            var odd = r.Alert.FirstOrDefault(s => s.Kind == GrowKind.Unusual);
            Func<FolderStudy, FastFolder> fastOf = s => r.Fast.FirstOrDefault(f => f.Path == s.Path) ?? new FastFolder { Path = s.Path };
            Func<string, string> shortP = p => { var segs = p.Split('\\', '/').Where(x => x.Length > 0).ToList(); return segs.Count <= 2 ? p : string.Join("\\", segs.Skip(segs.Count - 2)); };
            if (odd != null)
                Automation.Notify("Unusual growth on C:", shortP(odd.Path) + " grew " + GrewText(fastOf(odd)).TrimStart('+') + ". " + odd.Reason + " Nothing was deleted. Click to open Elixar.", System.Windows.Forms.ToolTipIcon.Warning, true);
            else
            {
                var s = r.Alert[0];
                string what = s.Kind == GrowKind.Safe ? "It's " + s.Owner + "'s old log, cache or temp files - safe to keep in check." : s.Kind == GrowKind.Yours ? "These are your own files." : s.Kind == GrowKind.Windows ? "It's a Windows folder." : "Elixar isn't sure what it is.";
                Automation.Notify(r.Alert.Count == 1 ? "A folder on C: is growing fast" : r.Alert.Count + " folders on C: are growing fast",
                                  shortP(s.Path) + " grew " + GrewText(fastOf(s)).TrimStart('+') + ". " + what + " Click to open Elixar.", System.Windows.Forms.ToolTipIcon.Info, true);
            }
        }
    }
}
