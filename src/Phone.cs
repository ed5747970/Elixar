// Phone: Android over USB (Google's adb), iPhone through Windows' own Apple device support.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CDriveCleaner
{
    // ============================ Android: adb ============================
    public static class Adb
    {
        public static string Exe;            // adb.exe, or the fake used by the self-test
        public static string Prefix = "";    // extra arguments put first (fake adb only)
        public const string ToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

        public static string ToolsDir
        {
            get
            {
                string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(la)) la = Path.GetTempPath();
                return Path.Combine(Path.Combine(la, "Elixar"), "platform-tools");
            }
        }

        // adb.exe from Elixar's own copy, the Android SDK, or PATH
        static readonly string[] AdbFiles = { "adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll" };

        // Elixar runs as administrator, so it only runs Google's tools from a folder only administrators can change,
        // after Windows has confirmed each file is signed by Google.
        public static string SafeDir { get { return Path.Combine(Install.Dir, "platform-tools"); } }

        public static bool SignedSet(string dir)
        {
            foreach (var f in AdbFiles)
            {
                string p = Path.Combine(dir, f);
                if (!File.Exists(p)) { if (f == "adb.exe") return false; continue; }
                if (!Trust.SignedBy(p, "Google")) return false;
            }
            return true;
        }

        // copies the tools into the protected folder and checks the copies there
        static string Protect(string fromDir)
        {
            if (!Locked.Lock(Install.Dir, true)) return null;
            Directory.CreateDirectory(SafeDir);
            if (!string.Equals(Path.GetFullPath(fromDir).TrimEnd('\\'), Path.GetFullPath(SafeDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                foreach (var f in AdbFiles)
                {
                    string src = Path.Combine(fromDir, f);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(SafeDir, f), true);
                }
            if (Locked.IsLocked(SafeDir) && SignedSet(SafeDir)) return Path.Combine(SafeDir, "adb.exe");
            foreach (var f in AdbFiles) { try { File.Delete(Path.Combine(SafeDir, f)); } catch { } }
            return null;
        }

        public static string Find()
        {
            if (Exe != null) return Exe;
            var dirs = new List<string> { SafeDir, ToolsDir, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "platform-tools") };
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(la)) dirs.Add(la + "\\Android\\Sdk\\platform-tools");
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (p.Trim().Length > 0) dirs.Add(p.Trim().Trim('"').TrimEnd('\\'));
            foreach (var d in dirs)
            {
                try
                {
                    if (!File.Exists(Path.Combine(d, "adb.exe"))) continue;
                    if (!Win.IsWindows) return Exe = Path.Combine(d, "adb.exe");
                    if (!Sys.IsAdmin()) { if (SignedSet(d)) return Exe = Path.Combine(d, "adb.exe"); continue; }
                    string safe = Protect(d);
                    if (safe != null) return Exe = safe;
                }
                catch { }
            }
            return null;
        }

        // Downloads Google's platform-tools (about 7 MB), keeps them only if every file is signed by Google
        public static string Download(Action<string> log)
        {
            string zip = Path.Combine(Path.GetTempPath(), "elixar-platform-tools-" + Guid.NewGuid().ToString("N") + ".zip");
            string tmp = Path.Combine(Path.GetTempPath(), "elixar-platform-tools-" + Guid.NewGuid().ToString("N"));
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                if (log != null) log("Downloading Android tools from Google...");
                using (var wc = new WebClient()) wc.DownloadFile(ToolsUrl, zip);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, tmp);
                string got = Path.Combine(tmp, "platform-tools");
                if (!File.Exists(Path.Combine(got, "adb.exe"))) return "The download did not contain adb.exe.";
                string exe = Sys.IsAdmin() ? Protect(got) : (SignedSet(got) ? got : null);
                if (exe == null) return "The downloaded tools are not signed by Google, so Elixar did not keep them.";
                if (!Sys.IsAdmin())
                {
                    if (Directory.Exists(ToolsDir)) Directory.Delete(ToolsDir, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(ToolsDir));
                    Directory.Move(got, ToolsDir);
                    exe = Path.Combine(ToolsDir, "adb.exe");
                }
                Exe = exe;
                if (log != null) log("Android tools ready (signed by Google): " + exe);
                return null;
            }
            catch (Exception e) { return "Could not get the Android tools: " + e.Message; }
            finally { try { File.Delete(zip); } catch { } try { Directory.Delete(tmp, true); } catch { } }
        }

        // Windows command-line quoting for one argument
        public static string WinArg(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(c);
            }
            sb.Append('\\', bs * 2);
            return sb.Append('"').ToString();
        }

        // phone shell quoting
        public static string Q(string s) { return "'" + (s ?? "").Replace("'", "'\\''") + "'"; }

        public static int Run(IEnumerable<string> args, out string output, int timeoutMs)
        {
            output = "";
            string exe = Find();
            if (exe == null) { output = "adb not found"; return -1; }
            string line = (Prefix.Length > 0 ? Prefix + " " : "") + string.Join(" ", args.Select(WinArg));
            var psi = new ProcessStartInfo(exe, line)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var err = new StringBuilder();
                    p.ErrorDataReceived += (o, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
                    p.BeginErrorReadLine();
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timed out"; return -2; }
                    p.WaitForExit();
                    output = outTask.Result;
                    lock (err) if (err.Length > 0) output += (output.Length > 0 && !output.EndsWith("\n") ? "\n" : "") + err;
                    return p.ExitCode;
                }
            }
            catch (Exception e) { output = e.Message; return -1; }
        }

        public static string Shell(string serial, string cmd, int timeoutMs = 120000)
        {
            string o;
            Run(new[] { "-s", serial, "shell", cmd }, out o, timeoutMs);
            return o.Replace("\r\n", "\n");
        }

        public static bool Pull(string serial, string remote, string localDir, out string output)
        {
            return Run(new[] { "-s", serial, "pull", "-a", remote, localDir }, out output, 6 * 3600 * 1000) == 0;
        }

        public static List<PhoneDevice> Devices(out string error)
        {
            error = null;
            var list = new List<PhoneDevice>();
            if (Find() == null) { error = "notools"; return list; }
            string o;
            int rc = Run(new[] { "devices", "-l" }, out o, 30000);
            if (rc != 0 && o.IndexOf("List of devices", StringComparison.Ordinal) < 0) { error = o.Trim(); return list; }
            return ParseDevices(o);
        }

        public static List<PhoneDevice> ParseDevices(string o)
        {
            var list = new List<PhoneDevice>();
            foreach (var raw in (o ?? "").Replace("\r", "").Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("List of devices") || l.StartsWith("*")) continue;
                var p = Regex.Split(l, @"\s+");
                if (p.Length < 2) continue;
                var d = new PhoneDevice { Serial = p[0], State = p[1], Kind = PhoneKind.Android };
                var m = Regex.Match(l, @"model:(\S+)");
                if (m.Success) d.Model = m.Groups[1].Value.Replace('_', ' ');
                list.Add(d);
            }
            return list;
        }
    }

    public enum PhoneKind { Android, IPhone }

    public class PhoneDevice
    {
        public PhoneKind Kind;
        public string Serial = "", State = "", Model = "", Maker = "", MarketName = "", Version = "";
        public long Total, Free;
        public string Title
        {
            get
            {
                string m = (Maker.Length > 0 && !Model.StartsWith(Maker, StringComparison.OrdinalIgnoreCase) ? Maker + " " : "") + Model;
                if (MarketName.Length > 0 && MarketName.IndexOf(Model, StringComparison.OrdinalIgnoreCase) < 0) m = MarketName + " (" + m + ")";
                return m.Trim().Length == 0 ? "phone" : m.Trim();
            }
        }
    }

    public class StorageSplit { public string Label; public long Bytes; }

    public class PhoneItem
    {
        public string Name, Info, Tier;               // Tier: safe / review
        public string[] Roots = new string[0];        // folders on the phone
        public string Pattern;                        // only files named like this (find -name)
        public int MaxDepth;                          // 0 = any depth
        public bool AllCaches;                        // "pm trim-caches" instead of files
        public bool DefaultOn;
        public long Bytes = -1; public int Files;
        public List<string> Found = new List<string>();   // roots that exist
    }

    public class PhoneApp
    {
        public string Package = "", Name = "", Why = "";
        public bool System, Removed;
        public string Tier = "";                      // "optional", "review" for pre-installed apps that can go
        public long AppBytes = -1, DataBytes = -1, CacheBytes = -1;
        public DateTime LastUsed;
        public long Total { get { return Math.Max(0, AppBytes) + Math.Max(0, DataBytes) + Math.Max(0, CacheBytes); } }
    }

    public class PhoneFile
    {
        public string Path = "", Name = "", Folder = "", Kind = "";
        public long Bytes; public DateTime Date;
    }

    // ============================ Android: what to look at ============================
    public static class Android
    {
        public const string Sd = "/storage/emulated/0";

        public static List<PhoneItem> Items()
        {
            string wa = Sd + "/Android/media/com.whatsapp/WhatsApp/Media", wb = Sd + "/Android/media/com.whatsapp.w4b/WhatsApp Business/Media", wo = Sd + "/WhatsApp/Media";
            return new List<PhoneItem>
            {
                new PhoneItem { Name = "Cache of every app", Tier = "safe", AllCaches = true, DefaultOn = true,
                    Info = "Temporary files apps keep to load faster. Apps rebuild what they need; you stay signed in and keep your data." },
                new PhoneItem { Name = "Gallery thumbnails", Tier = "safe", DefaultOn = true,
                    Roots = new[] { Sd + "/DCIM/.thumbnails", Sd + "/Pictures/.thumbnails", Sd + "/Movies/.thumbnails", Sd + "/Music/.thumbnails" },
                    Info = "Small preview pictures. The gallery makes them again when needed." },
                new PhoneItem { Name = "WhatsApp statuses you watched", Tier = "safe", DefaultOn = true,
                    Roots = new[] { wa + "/.Statuses", wb + "/.Statuses", wo + "/.Statuses" },
                    Info = "Copies of other people's statuses, kept after you watch them." },
                new PhoneItem { Name = "Deleted photos waiting in the trash", Tier = "review",
                    Roots = new[] { Sd + "/Android/.Trash", Sd + "/.Trash", Sd + "/MIUI/Gallery/cloud/.trashBin", Sd + "/DCIM/.trash", Sd + "/DCIM/.trashed" },
                    Info = "Photos and videos you already deleted. The gallery keeps them up to 30 days in case you change your mind." },
                new PhoneItem { Name = "Deleted photos waiting in the trash (Android 11+)", Tier = "review", Roots = new[] { Sd + "/DCIM", Sd + "/Pictures", Sd + "/Movies" }, Pattern = ".trashed-*",
                    Info = "Same as above, kept by newer Android versions next to your photos." },
                new PhoneItem { Name = "WhatsApp media you sent", Tier = "review",
                    Roots = new[] { wa + "/WhatsApp Images/Sent", wa + "/WhatsApp Video/Sent", wa + "/WhatsApp Documents/Sent", wa + "/WhatsApp Animated Gifs/Sent",
                                    wb + "/WhatsApp Business Images/Sent", wb + "/WhatsApp Business Video/Sent", wo + "/WhatsApp Images/Sent", wo + "/WhatsApp Video/Sent" },
                    Info = "Copies WhatsApp makes of every photo and video you send. The originals are usually still in your gallery." },
                new PhoneItem { Name = "Telegram downloaded files", Tier = "review",
                    Roots = new[] { Sd + "/Android/data/org.telegram.messenger/files/Telegram", Sd + "/Android/data/org.telegram.messenger.web/files/Telegram", Sd + "/Telegram" },
                    Info = "Telegram keeps everything in its cloud and downloads it again when you open the chat." },
                new PhoneItem { Name = "App installer files (.apk)", Tier = "review", Roots = new[] { Sd + "/Download", Sd + "/Downloads" }, Pattern = "*.apk", MaxDepth = 3,
                    Info = "Installer files left in Downloads. The installed apps keep working without them." },
            };
        }

        // one call reads model, version and free space
        public const string InfoScript =
            "echo \"MK=$(getprop ro.product.manufacturer)\"; echo \"MD=$(getprop ro.product.model)\"; echo \"MN=$(getprop ro.product.marketname)\"; " +
            "echo \"VR=$(getprop ro.build.version.release)\"; df -k /data 2>/dev/null";

        public static void ParseInfo(PhoneDevice d, string o)
        {
            foreach (var raw in (o ?? "").Split('\n'))
            {
                var l = raw.Trim();
                if (l.StartsWith("MK=")) d.Maker = Cap(l.Substring(3).Trim());
                else if (l.StartsWith("MD=") && l.Length > 3) d.Model = l.Substring(3).Trim();
                else if (l.StartsWith("MN=")) d.MarketName = l.Substring(3).Trim();
                else if (l.StartsWith("VR=")) d.Version = l.Substring(3).Trim();
                else
                {
                    var p = Regex.Split(l, @"\s+");
                    long total, avail;
                    if (p.Length >= 6 && p[p.Length - 1] == "/data" && long.TryParse(p[1], out total) && long.TryParse(p[3], out avail))
                    { d.Total = total * 1024; d.Free = avail * 1024; }
                }
            }
        }

        static string Cap(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        // dumpsys diskstats: Android's own storage breakdown and per-app sizes (refreshed by Android about once a day)
        public static List<StorageSplit> ParseDiskStats(string o, Dictionary<string, long[]> perApp)
        {
            var split = new List<StorageSplit>();
            string[] names = null; long[] app = null, data = null, cache = null;
            foreach (var raw in (o ?? "").Split('\n'))
            {
                var l = raw.Trim();
                var m = Regex.Match(l, @"^(App|App Data|App Cache|Photos|Videos|Audio|Downloads|System|Other) Size: (\d+)$");
                if (m.Success) { split.Add(new StorageSplit { Label = m.Groups[1].Value, Bytes = long.Parse(m.Groups[2].Value) }); continue; }
                if (l.StartsWith("Package Names: ")) names = Regex.Matches(l, "\"([^\"]*)\"").Cast<Match>().Select(x => x.Groups[1].Value).ToArray();
                else if (l.StartsWith("App Sizes: ")) app = Nums(l);
                else if (l.StartsWith("App Data Sizes: ")) data = Nums(l);
                else if (l.StartsWith("Cache Sizes: ")) cache = Nums(l);
            }
            if (perApp != null && names != null)
                for (int i = 0; i < names.Length; i++)
                    perApp[names[i]] = new[] { At(app, i), At(data, i), At(cache, i) };
            return split;
        }

        static long[] Nums(string l)
        {
            int a = l.IndexOf('['), b = l.LastIndexOf(']');
            if (a < 0 || b <= a) return new long[0];
            return l.Substring(a + 1, b - a - 1).Split(',').Select(x => { long v; return long.TryParse(x.Trim(), out v) ? v : -1; }).ToArray();
        }
        static long At(long[] a, int i) { return a != null && i < a.Length ? a[i] : -1; }

        // find ... -exec stat: "<bytes> <unix time> <path>"
        public static string FindCmd(IEnumerable<string> roots, string pattern, int maxDepth, string minSize, string action)
        {
            return "find " + string.Join(" ", roots.Select(Adb.Q)) + (maxDepth > 0 ? " -maxdepth " + maxDepth : "") + " -type f" +
                   (pattern != null ? " -name " + Adb.Q(pattern) : "") + (minSize != null ? " -size +" + minSize : "") + " " + action + " 2>/dev/null";
        }

        public const string StatAction = "-exec stat -c '%s %Y %n' {} +";

        public static List<PhoneFile> ParseStat(string o)
        {
            var list = new List<PhoneFile>();
            foreach (var raw in (o ?? "").Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                int a = l.IndexOf(' '); if (a <= 0) continue;
                int b = l.IndexOf(' ', a + 1); if (b <= a) continue;
                long size, t;
                if (!long.TryParse(l.Substring(0, a), out size) || !long.TryParse(l.Substring(a + 1, b - a - 1), out t)) continue;
                string path = l.Substring(b + 1);
                if (!path.StartsWith("/")) continue;
                int cut = path.LastIndexOf('/');
                var f = new PhoneFile { Path = path, Bytes = size, Name = path.Substring(cut + 1), Folder = cut > 0 ? path.Substring(0, cut) : "/", Date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime() };
                f.Kind = KindOf(f.Name);
                list.Add(f);
            }
            return list;
        }

        public static string KindOf(string name)
        {
            string e = System.IO.Path.GetExtension(name ?? "").ToLowerInvariant();
            if (new[] { ".mp4", ".mkv", ".mov", ".3gp", ".avi", ".webm", ".m4v" }.Contains(e)) return "Video";
            if (new[] { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".dng", ".raw" }.Contains(e)) return "Photo";
            if (new[] { ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".opus", ".amr" }.Contains(e)) return "Audio";
            if (new[] { ".zip", ".rar", ".7z", ".apk", ".xapk", ".obb", ".iso" }.Contains(e)) return "Archive / installer";
            if (new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt" }.Contains(e)) return "Document";
            return "Other";
        }

        // ---------------- measuring and cleaning ----------------
        public static void Measure(string serial, PhoneItem it, long appCache)
        {
            if (it.AllCaches) { it.Bytes = appCache; it.Files = 0; return; }
            var files = ParseStat(Adb.Shell(serial, FindCmd(it.Roots, it.Pattern, it.MaxDepth, null, StatAction)));
            it.Bytes = files.Sum(f => f.Bytes); it.Files = files.Count;
            it.Found = it.Roots.Where(r => files.Any(f => f.Path.StartsWith(r + "/"))).ToList();
        }

        // Removes the item's files. With backupRoot, copies them to the PC first and only deletes what was copied.
        public static long Clean(string serial, PhoneItem it, string backupRoot, Action<string> log)
        {
            if (it.AllCaches)
            {
                Adb.Shell(serial, "pm trim-caches 9999G", 600000);
                return Math.Max(0, it.Bytes);
            }
            var files = ParseStat(Adb.Shell(serial, FindCmd(it.Roots, it.Pattern, it.MaxDepth, null, StatAction)));
            long freed = 0;
            var gone = new List<string>();
            foreach (var f in files)
            {
                if (backupRoot != null)
                {
                    string dir = PhoneBackup.LocalFor(backupRoot, f.Folder), o;
                    if (!PullOne(serial, f, dir, out o)) { if (log != null) log("  kept " + f.Name + " (copy to PC failed: " + o.Trim() + ")"); continue; }
                }
                gone.Add(f.Path); freed += f.Bytes;
            }
            RemoveFiles(serial, gone);
            return freed;
        }

        // copies one file and checks the copy has the same size
        public static bool PullOne(string serial, PhoneFile f, string localDir, out string output)
        {
            Directory.CreateDirectory(localDir);
            if (!Adb.Pull(serial, f.Path, localDir, out output)) return false;
            string local = Path.Combine(localDir, f.Name);
            if (Adb.Prefix.Length > 0) return File.Exists(local);        // fake adb writes stand-in files
            return File.Exists(local) && new FileInfo(local).Length == f.Bytes;
        }

        public static void RemoveFiles(string serial, List<string> paths)
        {
            for (int i = 0; i < paths.Count; i += 40)
            {
                var chunk = paths.Skip(i).Take(40).ToList();
                Adb.Shell(serial, "rm -f " + string.Join(" ", chunk.Select(Adb.Q)), 300000);
                // tell the gallery the files are gone
                Adb.Shell(serial, string.Join("; ", chunk.Where(p => KindOf(p) != "Other").Select(p => "content delete --uri content://media/external/file --where " + Adb.Q("_data='" + p.Replace("'", "''") + "'") + " 2>/dev/null")) + "; true", 120000);
            }
        }

        public static List<PhoneFile> BigFiles(string serial, long minBytes)
        {
            return ParseStat(Adb.Shell(serial, FindCmd(new[] { Sd + "/" }, null, 0, minBytes + "c", StatAction), 600000))
                .Where(f => f.Path.IndexOf("/Android/data/", StringComparison.Ordinal) < 0 && f.Path.IndexOf("/Android/obb/", StringComparison.Ordinal) < 0)
                .OrderByDescending(f => f.Bytes).ToList();
        }

        public static bool Ok(string o) { return o.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0 || o.IndexOf("installed for user", StringComparison.OrdinalIgnoreCase) >= 0; }

        // pre-installed: removed for this user only, so it can be put back; your own apps: uninstalled
        public static string AppRemoveCmd(PhoneApp a) { return a.System ? "pm uninstall --user 0 " + Adb.Q(a.Package) : "pm uninstall " + Adb.Q(a.Package); }
        public static string AppRestoreCmd(PhoneApp a) { return "cmd package install-existing " + Adb.Q(a.Package); }

        // ---------------- apps ----------------
        public const string AppsScript =
            "echo '==3'; pm list packages -3 2>/dev/null; echo '==S'; pm list packages -s 2>/dev/null; echo '==U'; pm list packages -u 2>/dev/null; " +
            "echo '==I'; pm list packages 2>/dev/null; echo '==D'; dumpsys diskstats 2>/dev/null; echo '==T'; dumpsys usagestats 2>/dev/null | grep -E 'package=[^ ]+ .*lastTimeUsed=' 2>/dev/null";

        public static List<PhoneApp> ParseApps(string o, out List<StorageSplit> split)
        {
            var sets = new Dictionary<string, HashSet<string>>();
            var disk = new StringBuilder(); var times = new Dictionary<string, DateTime>();
            string sec = "";
            foreach (var raw in (o ?? "").Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.StartsWith("==") && l.Length == 3) { sec = l.Substring(2); if (!sets.ContainsKey(sec)) sets[sec] = new HashSet<string>(); continue; }
                if (sec == "D") { disk.AppendLine(l); continue; }
                if (sec == "T")
                {
                    var m = Regex.Match(l, "package=(\\S+) .*?lastTimeUsed=\"([0-9]{4}-[0-9]{2}-[0-9]{2} [0-9:]{8})\"");
                    DateTime t;
                    if (m.Success && DateTime.TryParseExact(m.Groups[2].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    { DateTime old; if (!times.TryGetValue(m.Groups[1].Value, out old) || t > old) times[m.Groups[1].Value] = t; }
                    continue;
                }
                if (sec.Length > 0 && l.StartsWith("package:")) sets[sec].Add(l.Substring(8).Trim());
            }
            Func<string, HashSet<string>> S = k => sets.ContainsKey(k) ? sets[k] : new HashSet<string>();
            var per = new Dictionary<string, long[]>();
            split = ParseDiskStats(disk.ToString(), per);
            var apps = new List<PhoneApp>();
            var user = S("3"); var sys = S("S"); var installed = S("I");
            foreach (var p in S("U").Union(installed).Distinct())
            {
                var a = new PhoneApp { Package = p, System = sys.Contains(p) || !user.Contains(p), Removed = !installed.Contains(p) };
                a.Name = Bloat.FriendlyName(p);
                long[] s; if (per.TryGetValue(p, out s)) { a.AppBytes = s[0]; a.DataBytes = s[1]; a.CacheBytes = s[2]; }
                DateTime t; if (times.TryGetValue(p, out t)) a.LastUsed = t;
                if (a.System) Bloat.Classify(a);
                apps.Add(a);
            }
            return apps;
        }
    }

    // pre-installed apps that are commonly removed; anything not listed is never offered
    public static class Bloat
    {
        // package -> tier|name|why
        static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "com.facebook.appmanager", "optional|Facebook App Manager|updates pre-installed Facebook apps in the background" },
            { "com.facebook.services", "optional|Facebook Services|background service for pre-installed Facebook" },
            { "com.facebook.system", "optional|Facebook App Installer|installs Facebook apps in the background" },
            { "com.facebook.katana", "review|Facebook|the Facebook app (pre-installed)" },
            { "com.linkedin.android", "optional|LinkedIn|pre-installed LinkedIn" },
            { "com.netflix.partner.activation", "optional|Netflix activation|helper for pre-installed Netflix" },
            { "com.netflix.mediaclient", "review|Netflix|pre-installed Netflix" },
            { "com.microsoft.skydrive", "review|OneDrive|Microsoft cloud storage" },
            { "com.microsoft.office.officehubrow", "optional|Microsoft 365 (Office)|pre-installed Office" },
            { "com.spotify.music", "review|Spotify|pre-installed Spotify" },
            { "com.booking", "optional|Booking.com|pre-installed Booking.com" },
            { "com.amazon.mShop.android.shopping", "optional|Amazon Shopping|pre-installed Amazon" },
            { "com.ebay.mobile", "optional|eBay|pre-installed eBay" },
            { "com.google.android.apps.youtube.music", "review|YouTube Music|music app" },
            { "com.google.android.videos", "optional|Google TV|films and shows store" },
            { "com.google.android.apps.tachyon", "review|Google Meet|video calls" },
            { "com.google.android.apps.magazines", "optional|Google News|news reader" },
            { "com.google.android.apps.podcasts", "optional|Google Podcasts|podcast player (closed by Google)" },
            { "com.google.android.apps.books", "optional|Google Play Books|e-books" },
            { "com.google.android.apps.subscriptions.red", "optional|Google One|cloud storage subscription app" },
            { "com.google.android.apps.wellbeing", "review|Digital Wellbeing|screen time stats" },
            { "com.samsung.android.app.tips", "optional|Samsung Tips|tips pop-ups" },
            { "com.samsung.android.arzone", "optional|AR Zone|augmented reality toys" },
            { "com.samsung.android.aremoji", "optional|AR Emoji|animated emoji" },
            { "com.samsung.android.ardrawing", "optional|AR Doodle|drawing in camera" },
            { "com.samsung.android.app.spage", "optional|Samsung Free|news and offers panel" },
            { "com.samsung.android.tvplus", "optional|Samsung TV Plus|free TV channels" },
            { "com.samsung.android.kidsinstaller", "optional|Samsung Kids installer|kids mode installer" },
            { "com.samsung.android.game.gamehome", "review|Game Launcher|game hub; games keep working without it" },
            { "com.samsung.android.bixby.agent", "review|Bixby Voice|Samsung's assistant; the side key stops opening Bixby" },
            { "com.samsung.android.visionintelligence", "review|Bixby Vision|camera recognition" },
            { "com.samsung.android.app.watchmanagerstub", "optional|Galaxy Wearable stub|placeholder for the watch app" },
            { "com.sec.android.app.sbrowser", "review|Samsung Internet|Samsung's browser" },
            { "com.miui.analytics", "optional|MIUI Analytics|usage data sent to Xiaomi" },
            { "com.miui.msa.global", "review|MIUI System Ads|ads in Xiaomi apps" },
            { "com.miui.videoplayer", "optional|Mi Video|video player" },
            { "com.mi.globalbrowser", "optional|Mi Browser|Xiaomi browser" },
            { "com.xiaomi.mipicks", "review|GetApps|Xiaomi app store" },
            { "com.miui.yellowpage", "optional|Yellow Pages|business caller ID" },
            { "com.xiaomi.glgm", "optional|Xiaomi Games|game center" },
            { "com.hihonor.gamecenter", "optional|Honor Game Center|game center" },
            { "com.oppo.market", "review|OPPO App Market|app store" },
            { "com.heytap.browser", "optional|HeyTap Browser|browser" },
        };

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "com.whatsapp", "WhatsApp" }, { "com.whatsapp.w4b", "WhatsApp Business" }, { "com.instagram.android", "Instagram" },
            { "com.zhiliaoapp.musically", "TikTok" }, { "com.ss.android.ugc.trill", "TikTok" }, { "org.telegram.messenger", "Telegram" },
            { "com.google.android.youtube", "YouTube" }, { "com.snapchat.android", "Snapchat" }, { "com.twitter.android", "X (Twitter)" },
            { "com.facebook.orca", "Messenger" }, { "com.waze", "Waze" }, { "com.google.android.apps.maps", "Google Maps" },
            { "com.google.android.apps.photos", "Google Photos" }, { "com.google.android.gm", "Gmail" }, { "com.android.chrome", "Chrome" },
            { "com.discord", "Discord" }, { "com.reddit.frontpage", "Reddit" }, { "com.pinterest", "Pinterest" },
            { "com.supercell.clashofclans", "Clash of Clans" }, { "com.supercell.clashroyale", "Clash Royale" }, { "com.supercell.brawlstars", "Brawl Stars" },
            { "com.activision.callofduty.shooter", "Call of Duty Mobile" }, { "com.tencent.ig", "PUBG Mobile" }, { "com.mojang.minecraftpe", "Minecraft" },
            { "com.roblox.client", "Roblox" }, { "com.king.candycrushsaga", "Candy Crush" }, { "com.miHoYo.GenshinImpact", "Genshin Impact" },
            { "com.netflix.mediaclient", "Netflix" }, { "com.spotify.music", "Spotify" }, { "com.disney.disneyplus", "Disney+" },
            { "com.microsoft.teams", "Microsoft Teams" }, { "com.microsoft.office.outlook", "Outlook" }, { "us.zoom.videomeetings", "Zoom" },
            { "com.moovit.android", "Moovit" }, { "com.gettaxi.android", "Gett" }, { "com.wolt.android", "Wolt" }, { "com.paybox", "PayBox" },
            { "com.ideomobile.hapoalim", "Bank Hapoalim" }, { "com.leumi.leumiwallet", "Leumi" }, { "com.shazam.android", "Shazam" },
            { "com.binance.dev", "Binance" }, { "com.coinbase.android", "Coinbase" }, { "com.robinhood.android", "Robinhood" }, { "com.etoro.openbook", "eToro" },
        };

        public static string FriendlyName(string pkg)
        {
            string n; if (Names.TryGetValue(pkg, out n)) return n;
            string k; if (Known.TryGetValue(pkg, out k)) return k.Split('|')[1];
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "com", "org", "net", "io", "co", "me", "tv", "app", "apps", "android", "mobile", "free", "lite", "client", "google", "sec", "samsung", "miui", "global", "main", "prod", "release", "il" };
            var parts = pkg.Split('.').Where(x => x.Length > 1 && !skip.Contains(x)).ToList();
            string w = parts.Count > 0 ? parts[0] : pkg.Split('.').Last();
            return char.ToUpperInvariant(w[0]) + w.Substring(1);
        }

        public static void Classify(PhoneApp a)
        {
            string k;
            if (!Known.TryGetValue(a.Package, out k)) return;
            var p = k.Split('|');
            a.Tier = p[0]; a.Name = p[1]; a.Why = p[2];
        }

        public static bool Offered(string pkg) { return Known.ContainsKey(pkg); }
    }

    // ============================ iPhone: Windows' Apple device support ============================
    // Windows shows an unlocked, trusted iPhone as a portable device with photos and videos in DCIM.
    public static class IPhone
    {
        static object Call(object o, string m, params object[] a) { return o.GetType().InvokeMember(m, BindingFlags.InvokeMethod, null, o, a); }
        static object Get(object o, string p) { return o.GetType().InvokeMember(p, BindingFlags.GetProperty, null, o, null); }
        static object NewShell() { return Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")); }

        // runs f on a fresh STA thread (Shell objects need it)
        public static T Sta<T>(Func<T> f)
        {
            T r = default(T); Exception ex = null;
            var t = new Thread(() => { try { r = f(); } catch (Exception e) { ex = e; } });
            t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start(); t.Join();
            if (ex != null) throw ex;
            return r;
        }

        static IEnumerable<object> Items(object folder)
        {
            if (folder == null) yield break;
            object items = null;
            try { items = Call(folder, "Items"); } catch { }
            if (items == null) yield break;
            int n = 0; try { n = (int)Get(items, "Count"); } catch { }
            for (int i = 0; i < n; i++)
            {
                object it = null; try { it = Call(items, "Item", i); } catch { }
                if (it != null) yield return it;
            }
        }

        static string Str(object o, string p) { try { return (Get(o, p) ?? "").ToString(); } catch { return ""; } }
        static object Ext(object o, string p) { try { return Call(o, "ExtendedProperty", p); } catch { return null; } }
        static bool IsFolder(object o) { try { return (bool)Get(o, "IsFolder"); } catch { return false; } }
        static object SubFolder(object o) { try { return Get(o, "GetFolder"); } catch { return null; } }
        static long Num(object v) { try { return v == null ? -1 : Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return -1; } }

        public class Found { public string Name = "", Storage = "", DcimPath = "", Error; public long Total = -1, Free = -1; }

        // looks under This PC for an Apple device and its DCIM folder
        public static Found Find()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return null;
            return Sta(() =>
            {
                var shell = NewShell();
                foreach (var dev in Items(Call(shell, "NameSpace", 17)))
                {
                    string name = Str(dev, "Name"), type = Str(dev, "Type");
                    bool apple = name.IndexOf("iPhone", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("iPad", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (name.IndexOf("Apple", StringComparison.OrdinalIgnoreCase) >= 0 && type.IndexOf("Portable", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!apple) continue;
                    var f = new Found { Name = name };
                    var storage = Items(SubFolder(dev)).FirstOrDefault(IsFolder);
                    if (storage == null) { f.Error = "locked"; return f; }
                    f.Storage = Str(storage, "Name");
                    f.Total = Num(Ext(storage, "System.Capacity")); f.Free = Num(Ext(storage, "System.FreeSpace"));
                    var dcim = Items(SubFolder(storage)).FirstOrDefault(x => Str(x, "Name").Equals("DCIM", StringComparison.OrdinalIgnoreCase));
                    if (dcim == null) { f.Error = "locked"; return f; }
                    f.DcimPath = Str(dcim, "Path");
                    return f;
                }
                return (Found)null;
            });
        }

        // every photo and video under a folder (the iPhone's DCIM, or any folder for the self-test)
        public static List<PhoneFile> ListMedia(string folderPath, long minBytes, Action<int> progress)
        {
            return Sta(() =>
            {
                var shell = NewShell();
                var list = new List<PhoneFile>();
                var root = Call(shell, "NameSpace", folderPath);
                var stack = new Stack<KeyValuePair<object, string>>();
                stack.Push(new KeyValuePair<object, string>(root, "DCIM"));
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    foreach (var it in Items(cur.Key))
                    {
                        string name = Str(it, "Name");
                        if (IsFolder(it)) { stack.Push(new KeyValuePair<object, string>(SubFolder(it), cur.Value + "/" + name)); continue; }
                        long size = -1; try { size = Convert.ToInt64(Get(it, "Size")); } catch { }
                        if (size <= 0) size = Num(Ext(it, "System.Size"));
                        DateTime when = DateTime.MinValue; try { when = (DateTime)Get(it, "ModifyDate"); } catch { }
                        if (size < minBytes) continue;
                        list.Add(new PhoneFile { Path = Str(it, "Path"), Name = name, Folder = cur.Value, Bytes = size, Date = when, Kind = Android.KindOf(name) });
                        if (progress != null && list.Count % 50 == 0) progress(list.Count);
                    }
                }
                return list;
            });
        }

        // ---- IFileOperation: copy and delete without dialogs, one file at a time ----
        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOperation
        {
            uint Advise(IntPtr pfops);
            void Unadvise(uint dwCookie);
            void SetOperationFlags(uint dwOperationFlags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
            void SetProgressDialog(IntPtr popd);
            void SetProperties(IntPtr pproparray);
            void SetOwnerWindow(IntPtr hwndOwner);
            void ApplyPropertiesToItem(IShellItem psiItem);
            void ApplyPropertiesToItems(IntPtr punkItems);
            void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
            void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
            void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
            void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
            void DeleteItems(IntPtr punkItems);
            uint NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
            void PerformOperations();
            [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_NOCONFIRMMKDIR = 0x200, FOF_NOERRORUI = 0x400, FOFX_EARLYFAILURE = 0x100000;

        static IShellItem Item(string parsingName)
        {
            var iid = typeof(IShellItem).GUID;
            IShellItem it;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out it);
            return it;
        }

        static IFileOperation NewOp()
        {
            var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09")));
            op.SetOperationFlags(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOCONFIRMMKDIR | FOF_NOERRORUI | FOFX_EARLYFAILURE);
            return op;
        }

        // copies one file (phone path or normal path) into a PC folder; true when the copy is there at full size
        public static bool CopyTo(string itemPath, string name, long bytes, string localDir)
        {
            Directory.CreateDirectory(localDir);
            string target = Path.Combine(localDir, name);
            bool ok = Sta(() =>
            {
                var op = NewOp();
                op.CopyItem(Item(itemPath), Item(localDir), null, IntPtr.Zero);
                op.PerformOperations();
                return !op.GetAnyOperationsAborted();
            });
            return ok && File.Exists(target) && (bytes <= 0 || new FileInfo(target).Length == bytes);
        }

        public static bool Delete(string itemPath)
        {
            return Sta(() =>
            {
                var op = NewOp();
                op.DeleteItem(Item(itemPath), IntPtr.Zero);
                op.PerformOperations();
                return !op.GetAnyOperationsAborted();
            });
        }

        public static bool Exists(string itemPath)
        {
            return Sta(() => { try { return Item(itemPath) != null; } catch { return false; } });
        }
    }

    // ============================ where phone copies go on the PC ============================
    public static class PhoneBackup
    {
        public static string Root(long need)
        {
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady || d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                        if (d.AvailableFreeSpace < need + (5L << 30)) continue;
                        return Path.Combine(d.RootDirectory.FullName, "Phone backup");
                    }
                    catch { }
                }
            }
            catch { }
            string pics = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(pics)) pics = Path.GetTempPath();
            return Path.Combine(pics, "Phone backup");
        }

        public static string Safe(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "phone" : s.Trim();
        }

        // phone folder -> PC folder under the backup root, keeping the phone's layout
        public static string LocalFor(string root, string phoneFolder)
        {
            string rel = phoneFolder.StartsWith(Android.Sd) ? phoneFolder.Substring(Android.Sd.Length) : phoneFolder;
            string dir = root;
            foreach (var part in rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) dir = Path.Combine(dir, Safe(part));
            return dir;
        }
    }

    // ============================ fake adb for the self-test and previews ============================
    public static class FakeAdb
    {
        public static int Run(string[] args)
        {
            var o = Console.OpenStandardOutput();
            Action<string> say = s => { var b = Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n")); o.Write(b, 0, b.Length); o.Flush(); };
            string log = Environment.GetEnvironmentVariable("ELIXAR_FAKE_ADB_LOG");
            if (!string.IsNullOrEmpty(log)) try { File.AppendAllText(log, string.Join(" | ", args) + "\n"); } catch { }
            if (args.Length > 0 && args[0] == "devices") { say("List of devices attached\nR5CT90ABCDE            device usb:1-4 product:dm1qxeea model:SM_S911B device:dm1q transport_id:2\n\n"); return 0; }
            if (args.Length >= 3 && args[2] == "pull")
            {
                string dir = args[args.Length - 1]; Directory.CreateDirectory(dir);
                string name = args[args.Length - 2].Split('/').Last();
                File.WriteAllText(Path.Combine(dir, name), "fake copy of " + args[args.Length - 2]);
                say("1 file pulled.\n"); return 0;
            }
            string cmd = args.Length >= 4 && args[2] == "shell" ? args[3] : "";
            if (cmd.StartsWith("echo \"MK=")) { say("MK=samsung\nMD=SM-S911B\nMN=Galaxy S23\nVR=14\nFilesystem      1K-blocks     Used Available Use% Mounted on\n/dev/block/dm-48 115712120 73412044 42300076  64% /data\n"); return 0; }
            if (cmd.StartsWith("echo '==3'")) { say(SampleApps); return 0; }
            if (cmd.Contains("-size +")) { say(SampleBig); return 0; }
            if (cmd.StartsWith("find ") && cmd.Contains(".thumbnails") && cmd.Contains("stat")) { say("482000 1760000000 /storage/emulated/0/DCIM/.thumbnails/a.jpg\n519000 1760000100 /storage/emulated/0/DCIM/.thumbnails/b.jpg\n"); return 0; }
            if (cmd.StartsWith("find ") && cmd.Contains("Sent") && cmd.Contains("stat")) { say("734003200 1750000000 /storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/Sent/VID-1.mp4\n"); return 0; }
            if (cmd.StartsWith("find ") && cmd.Contains(".Statuses") && cmd.Contains("stat")) { say("94371840 1760000000 /storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/.Statuses/s1.mp4\n"); return 0; }
            if (cmd.StartsWith("find ") && cmd.Contains(".Trash") && cmd.Contains("stat")) { say("1288490188 1759000000 /storage/emulated/0/Android/.Trash/com.sec.android.gallery3d/x.mp4\n"); return 0; }
            if (cmd.StartsWith("find ") && cmd.Contains("Telegram") && cmd.Contains("stat")) { say("2147483648 1758000000 /storage/emulated/0/Android/data/org.telegram.messenger/files/Telegram/Telegram Video/v.mp4\n"); return 0; }
            if (cmd.StartsWith("find ") && cmd.Contains("*.apk")) { say("157286400 1700000000 /storage/emulated/0/Download/old game.apk\n"); return 0; }
            if (cmd.StartsWith("pm trim-caches")) return 0;
            if (cmd.StartsWith("pm uninstall")) { say("Success\n"); return 0; }
            if (cmd.StartsWith("cmd package install-existing")) { say("Package installed for user: 0\n"); return 0; }
            return 0;
        }

        const string SampleApps =
            "==3\npackage:com.whatsapp\npackage:com.instagram.android\npackage:com.zhiliaoapp.musically\npackage:com.supercell.clashofclans\npackage:com.waze\npackage:com.example.oldgame\n" +
            "==S\npackage:android\npackage:com.android.systemui\npackage:com.facebook.appmanager\npackage:com.facebook.services\npackage:com.facebook.system\npackage:com.samsung.android.app.tips\n" +
            "package:com.samsung.android.arzone\npackage:com.samsung.android.tvplus\npackage:com.netflix.mediaclient\npackage:com.samsung.android.bixby.agent\npackage:com.microsoft.skydrive\npackage:com.google.android.apps.youtube.music\npackage:com.samsung.android.game.gamehome\n" +
            "==U\npackage:com.samsung.android.app.spage\n" +
            "==I\npackage:com.whatsapp\npackage:com.instagram.android\npackage:com.zhiliaoapp.musically\npackage:com.supercell.clashofclans\npackage:com.waze\npackage:com.example.oldgame\npackage:android\npackage:com.android.systemui\n" +
            "package:com.facebook.appmanager\npackage:com.facebook.services\npackage:com.facebook.system\npackage:com.samsung.android.app.tips\npackage:com.samsung.android.arzone\npackage:com.samsung.android.tvplus\npackage:com.netflix.mediaclient\n" +
            "package:com.samsung.android.bixby.agent\npackage:com.microsoft.skydrive\npackage:com.google.android.apps.youtube.music\npackage:com.samsung.android.game.gamehome\n" +
            "==D\nLatency: 2ms [512B Data Write]\nData-Free: 42300076K / 115712120K total = 36% free\nApp Size: 19327352832\nApp Data Size: 9663676416\nApp Cache Size: 3328599654\n" +
            "Photos Size: 6442450944\nVideos Size: 14495514624\nAudio Size: 536870912\nDownloads Size: 2147483648\nSystem Size: 16106127360\nOther Size: 4294967296\n" +
            "Package Names: [\"com.whatsapp\",\"com.instagram.android\",\"com.zhiliaoapp.musically\",\"com.supercell.clashofclans\",\"com.waze\",\"com.example.oldgame\",\"com.facebook.appmanager\",\"com.facebook.services\",\"com.facebook.system\",\"com.samsung.android.app.tips\",\"com.samsung.android.arzone\",\"com.samsung.android.tvplus\",\"com.netflix.mediaclient\",\"com.samsung.android.bixby.agent\",\"com.microsoft.skydrive\",\"com.google.android.apps.youtube.music\",\"com.samsung.android.game.gamehome\"]\n" +
            "App Sizes: [104857600,293601280,398458880,2684354560,209715200,1610612736,10485760,20971520,6291456,15728640,94371840,52428800,157286400,73400320,125829120,99614720,41943040]\n" +
            "App Data Sizes: [6442450944,1073741824,1610612736,524288000,314572800,104857600,1048576,52428800,2097152,4194304,1048576,31457280,10485760,94371840,20971520,15728640,8388608]\n" +
            "Cache Sizes: [524288000,838860800,1258291200,104857600,104857600,2097152,1048576,10485760,1048576,2097152,1048576,10485760,3145728,20971520,5242880,5242880,1048576]\n" +
            "==T\n        package=com.whatsapp totalTimeUsed=\"40:00:00\" lastTimeUsed=\"2026-10-16 21:40:12\" totalTimeVisible=\"40:00:00\"\n" +
            "        package=com.instagram.android totalTimeUsed=\"9:00:00\" lastTimeUsed=\"2026-10-15 20:01:00\"\n" +
            "        package=com.zhiliaoapp.musically totalTimeUsed=\"1:00:00\" lastTimeUsed=\"2026-08-02 13:00:00\"\n" +
            "        package=com.supercell.clashofclans totalTimeUsed=\"1:00:00\" lastTimeUsed=\"2026-10-14 13:00:00\"\n" +
            "        package=com.waze totalTimeUsed=\"1:00:00\" lastTimeUsed=\"2026-10-12 08:00:00\"\n" +
            "        package=com.example.oldgame totalTimeUsed=\"1:00:00\" lastTimeUsed=\"2025-11-30 18:00:00\"\n";

        const string SampleBig =
            "4187593113 1754000000 /storage/emulated/0/DCIM/Camera/20250815_191233.mp4\n" +
            "2899102924 1738000000 /storage/emulated/0/DCIM/Camera/20250127_120001.mp4\n" +
            "1932735283 1760500000 /storage/emulated/0/Movies/Screen recordings/Screen_Recording_20251015.mp4\n" +
            "1503238553 1745000000 /storage/emulated/0/Download/ubuntu-24.04.iso\n" +
            "943718400 1730000000 /storage/emulated/0/Download/movie.mkv\n" +
            "524288000 1720000000 /storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/VID-20240703-WA0001.mp4\n" +
            "314572800 1710000000 /storage/emulated/0/Documents/scans.pdf\n" +
            "104857600 1700000000 /storage/emulated/0/Music/podcast.mp3\n";
    }
}
