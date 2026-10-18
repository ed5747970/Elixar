// Elixar on Linux (Ubuntu, Debian, Mint): where its files go, the system's own tools, what can be cleaned,
// one password prompt for the system parts, and the schedules (systemd timers for your account).
// Elixar never runs as root on Linux: it asks for your password (pkexec) only for the fixed system commands below.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public static class Lx
    {
        public static bool On { get { return !Win.IsWindows; } }

        // Windows wording that would be wrong on Linux: the sign-in there is protected by your account's file permissions,
        // not encrypted by Windows, and there is no "C:"
        public static string Words(string s)
        {
            if (!On || s == null) return s;
            return s.Replace("is encrypted on this PC in a folder only administrators can open", "is kept on this PC in a folder only your account can open (and encrypted, when your desktop has a keyring)")
                    .Replace("keeps it encrypted on this PC in a folder only administrators can open", "keeps it on this PC in a folder only your account can open (and encrypted, when your desktop has a keyring)")
                    .Replace("Alerts: C: almost full (your limit in Automation, or 15%), or a drive that may fail", "Alerts: the system disk is almost full (your limit in Automation, or 15%)")
                    .Replace("Windows and your apps rebuild them by themselves", "your system and apps rebuild them by themselves")
                    .Replace("Follow Windows", "Follow the system");
        }

        [DllImport("libc", SetLastError = true)] static extern int chmod(string path, uint mode);
        [DllImport("libc")] static extern uint geteuid();

        public static uint Uid { get { try { return geteuid(); } catch { return uint.MaxValue; } } }

        // Linux "locked": owned by you or root, not a symlink, and (noWrite) nobody else can change it
        public static bool OwnedSafely(string path, bool noWrite)
        {
            if (!On) return true;
            string o;
            if (Run("stat", "-c %u:%a:%F " + Arg(path), 5000, out o) != 0) return false;
            var m = Regex.Match(o.Trim(), "^([0-9]+):([0-7]{3,4}):(.+)$");
            if (!m.Success || m.Groups[3].Value.Contains("symbolic link")) return false;
            uint owner = uint.Parse(m.Groups[1].Value);
            if (owner != 0 && owner != Uid) return false;
            return !noWrite || (Convert.ToInt32(m.Groups[2].Value, 8) & 0x12) == 0;
        }

        public static bool IsRoot { get { if (!On) return false; try { return geteuid() == 0; } catch { return false; } } }

        // only you can open it: 0700 for a folder, 0600 for a file (the Linux way to keep a sign-in private, like ~/.ssh)
        public static bool Private(string path, bool dir)
        {
            if (!On) return true;
            try { return chmod(path, dir ? 448u : 384u) == 0; } catch { return false; }
        }

        public static string Home
        {
            get
            {
                string h = Environment.GetEnvironmentVariable("HOME");
                return string.IsNullOrEmpty(h) || !h.StartsWith("/") ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : h;
            }
        }

        static string Xdg(string var, string fallback)
        {
            string v = Environment.GetEnvironmentVariable(var);
            return SafeUserDir(v) ? Path.GetFullPath(v).TrimEnd('/') : Path.Combine(Home, fallback);
        }

        static readonly string[] SystemDirs = { "/usr", "/etc", "/bin", "/sbin", "/lib", "/lib32", "/lib64", "/boot", "/var", "/opt", "/proc", "/sys", "/dev", "/run", "/snap", "/srv", "/tmp", "/media", "/mnt" };

        // An XDG folder Elixar cleans inside must be a real sub-folder: never "/", your home itself (or above it),
        // or a system folder. A mistyped XDG_CACHE_HOME=$HOME would otherwise make "Other app caches" mean everything you own.
        public static bool SafeUserDir(string v)
        {
            if (string.IsNullOrEmpty(v) || !v.StartsWith("/") || v.IndexOfAny(new[] { '\n', '\r', '\0' }) >= 0) return false;
            string full;
            try { full = Path.GetFullPath(v).TrimEnd('/'); } catch { return false; }
            if (full.Length < 2) return false;
            string home = Home.TrimEnd('/');
            if (full == home || (home + "/").StartsWith(full + "/")) return false;
            return !SystemDirs.Any(d => full == d || full.StartsWith(d + "/"));
        }
        public static string DataHome { get { return Xdg("XDG_DATA_HOME", ".local/share"); } }
        public static string ConfigHome { get { return Xdg("XDG_CONFIG_HOME", ".config"); } }
        public static string CacheHome { get { return Xdg("XDG_CACHE_HOME", ".cache"); } }
        public static string DataDir { get { return Path.Combine(DataHome, "elixar"); } }

        // A system tool by its full path in the system folders only. A name found first in ~/bin or another folder on PATH
        // could be anything; these folders belong to root.
        static readonly string[] Bins = { "/usr/bin", "/bin", "/usr/sbin", "/sbin" };
        public static string Tool(string name)
        {
            foreach (var d in Bins) { string p = Path.Combine(d, name); if (File.Exists(p)) return p; }
            return null;
        }

        // one argument, quoted for Mono's command-line splitter
        public static string Arg(string a) { return "\"" + (a ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        public static int Run(string tool, string args, int timeoutMs, out string output)
        {
            output = "";
            string exe = tool.StartsWith("/") ? tool : Tool(tool);
            if (exe == null || !File.Exists(exe)) { output = tool + " is not installed"; return -1; }
            try
            {
                var psi = new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.EnvironmentVariables["LC_ALL"] = "C";    // English, fixed number format: Elixar reads the output
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timed out"; return -1; }
                    output = (o.Result + "\n" + e.Result).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        // the same, with text written to the tool's input (never on its command line, where other programs can see it)
        public static int RunInput(string tool, string args, string input, int timeoutMs, out string output)
        {
            output = "";
            string exe = tool.StartsWith("/") ? tool : Tool(tool);
            if (exe == null || !File.Exists(exe)) { output = tool + " is not installed"; return -1; }
            try
            {
                var psi = new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.EnvironmentVariables["LC_ALL"] = "C";
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                    p.StandardInput.Write(input ?? ""); p.StandardInput.Close();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timed out"; return -1; }
                    output = (o.Result + "\n" + e.Result).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        public static void Open(string pathOrUrl)
        {
            string x = Tool("xdg-open");
            if (x != null) Process.Start(new ProcessStartInfo(x, Arg(pathOrUrl)) { UseShellExecute = false });
        }

        // to the Trash (it can be restored from there), the desktop's own way
        public static bool Trash(string path)
        {
            string o;
            return Run("gio", "trash " + Arg(path), 60000, out o) == 0;
        }

        public static void Notify(string title, string text, bool urgent)
        {
            string o;
            Run("notify-send", "-a Elixar -u " + (urgent ? "critical" : "normal") + " -i " + (urgent ? "dialog-warning" : "dialog-information") + " " + Arg(title) + " " + Arg(text), 10000, out o);
        }

        public static bool PrefersDark()
        {
            if (!On) return false;
            string o;
            if (Run("gsettings", "get org.gnome.desktop.interface color-scheme", 3000, out o) == 0 && o.Contains("dark")) return true;
            return Run("gsettings", "get org.gnome.desktop.interface gtk-theme", 3000, out o) == 0 && o.ToLowerInvariant().Contains("dark");
        }

        // ---------- disks and mounts ----------
        public class Mount { public string Device = "", Point = "", Type = ""; }

        // /proc/mounts writes a space in a name as \040
        public static string Unescape(string s)
        {
            return Regex.Replace(s ?? "", @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
        }

        public static List<Mount> ParseMounts(IEnumerable<string> lines)
        {
            var list = new List<Mount>();
            foreach (var l in lines)
            {
                var p = l.Split(' ');
                if (p.Length >= 3 && p[1].StartsWith("/")) list.Add(new Mount { Device = Unescape(p[0]), Point = Unescape(p[1]), Type = p[2] });
            }
            return list;
        }

        static List<Mount> mounts; static DateTime mountsAt;
        public static List<Mount> Mounts()
        {
            if (!On) return new List<Mount>();
            if (mounts != null && (DateTime.Now - mountsAt).TotalSeconds < 30) return mounts;
            try { mounts = ParseMounts(File.ReadAllLines("/proc/mounts")); } catch { mounts = new List<Mount>(); }
            mountsAt = DateTime.Now;
            return mounts;
        }

        static readonly HashSet<string> Pseudo = new HashSet<string> { "/proc", "/sys", "/dev", "/run" };

        // Measuring stays on one disk, like "du -x": /proc and /sys aren't files, and other disks mounted inside
        // (USB drives, /home on its own partition, snap images) are counted on their own, not twice.
        public static bool SkipDir(string dir, string start)
        {
            if (!On) return false;
            string d = dir.TrimEnd('/');
            if (d.Length == 0 || d == start.TrimEnd('/')) return false;
            if (Pseudo.Contains(d)) return true;
            return Mounts().Any(m => m.Point == d);
        }

        static readonly HashSet<string> RealFs = new HashSet<string> { "ext2", "ext3", "ext4", "btrfs", "xfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "f2fs", "jfs", "zfs", "reiserfs", "hfsplus" };

        // disks you keep files on: not the system disk itself, not /boot, not the read-only snap images
        public static List<Mount> DataMounts(IEnumerable<Mount> all)
        {
            var seen = new HashSet<string>();
            var list = new List<Mount>();
            foreach (var m in all)
            {
                if (!RealFs.Contains(m.Type) || m.Point == "/" || m.Point.StartsWith("/boot") || m.Point.StartsWith("/snap") || m.Point.StartsWith("/var/snap")) continue;
                if (m.Point.StartsWith("/run/") && !m.Point.StartsWith("/run/media/")) continue;
                if (!seen.Add(m.Device + "|" + m.Point)) continue;
                list.Add(m);
            }
            return list;
        }

        public static bool RemovableMount(string point) { return point.StartsWith("/media/") || point.StartsWith("/run/media/"); }

        // ---------- the system parts, as root, after one password prompt ----------
        // Elixar never builds a root command from text. It asks for named actions ("apt-clean", "snap-remove:firefox:4793"),
        // checked here and checked again by the root-owned helper the .deb installs, which has its own polkit message.
        public const string Helper = "/usr/lib/elixar/elixar-root";
        static readonly string[] Plain = { "apt-clean", "apt-autoremove", "journal-vacuum", "crash-clean", "flatpak-unused", "journal-uncap",
                                           "journal-cap:200M", "journal-cap:500M", "journal-cap:1G" };
        static readonly string[] DockerAt = { "/usr/bin/docker", "/bin/docker", "/usr/sbin/docker", "/sbin/docker", "/snap/bin/docker" };

        public static bool RootAction(string a)
        {
            if (a == null) return false;
            if (Plain.Contains(a)) return true;
            if (a.StartsWith("docker-safe:")) return DockerAt.Contains(a.Substring(12));
            if (a.StartsWith("docker-all:")) return DockerAt.Contains(a.Substring(11));
            var m = Regex.Match(a, "^snap-remove:([^:]*):([^:]*)$");
            return m.Success && SnapName(m.Groups[1].Value) && Digits(m.Groups[2].Value);
        }

        public static List<string> RootActions(string actions)
        {
            return (actions ?? "").Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // the installed helper is used only when root owns it and nobody else can change it (or its folders)
        public static bool HelperTrusted(string path)
        {
            if (!File.Exists(path)) return false;
            for (string p = path; !string.IsNullOrEmpty(p) && p != "/"; p = Path.GetDirectoryName(p))
            {
                string o;
                if (Run("stat", "-L -c %u:%a " + Arg(p), 5000, out o) != 0) return false;
                var m = Regex.Match(o.Trim(), "^0:([0-7]{3,4})$");
                if (!m.Success) return false;
                int mode = Convert.ToInt32(m.Groups[1].Value, 8);
                if ((mode & 0x12) != 0) return false;   // group or others can write
            }
            return true;
        }

        // without the helper (Elixar started from a folder, not installed): the same fixed commands, by full path
        public static string Fallback(string a)
        {
            Func<string, string> t = n => Tool(n) ?? ("/usr/bin/" + n);
            switch (a)
            {
                case "apt-clean": return t("apt-get") + " clean";
                case "apt-autoremove": return t("apt-get") + " autoremove --purge -y";
                case "journal-vacuum": return t("journalctl") + " --vacuum-size=200M";
                case "crash-clean": return "/bin/rm -f /var/crash/*.crash /var/crash/*.upload /var/crash/*.uploaded";
                case "flatpak-unused": return t("flatpak") + " uninstall --system --unused -y --noninteractive";
                case "journal-uncap": return "/bin/rm -f " + LxSystem.CapFile + " && " + t("systemctl") + " restart systemd-journald";
            }
            if (a.StartsWith("journal-cap:"))
            {
                string size = a.Substring(12);
                return "/bin/mkdir -p /etc/systemd/journald.conf.d && printf '[Journal]\\nSystemMaxUse=" + size + "\\n' > " + LxSystem.CapFile +
                       " && " + t("systemctl") + " restart systemd-journald && " + t("journalctl") + " --vacuum-size=" + size;
            }
            if (a.StartsWith("docker-safe:")) { string d = a.Substring(12); return d + " builder prune -f && " + d + " image prune -f"; }
            if (a.StartsWith("docker-all:")) { string d = a.Substring(11); return d + " container prune -f && " + d + " image prune -a -f"; }
            var m = Regex.Match(a, "^snap-remove:([^:]*):([^:]*)$");
            return (File.Exists("/usr/bin/snap") ? "/usr/bin/snap" : "/snap/bin/snap") + " remove " + m.Groups[1].Value + " --revision=" + m.Groups[2].Value;
        }

        public static int RunAsRoot(string actions, out string output)
        {
            output = "";
            var list = RootActions(actions);
            if (list.Count == 0 || list.Count > 200 || !list.All(RootAction)) { output = "refused: not one of Elixar's fixed actions"; return -1; }
            string pk = Tool("pkexec");
            if (pk == null) { output = "pkexec is not installed"; return -1; }
            if (HelperTrusted(Helper)) return Run(pk, Helper + " " + string.Join(" ", list), 30 * 60 * 1000, out output);
            string sh = File.Exists("/bin/sh") ? "/bin/sh" : Tool("sh");
            if (sh == null) { output = "sh is missing"; return -1; }
            return Run(pk, sh + " -c " + Arg(string.Join("; ", list.Select(Fallback))), 30 * 60 * 1000, out output);
        }

        public static bool SnapName(string s) { return Regex.IsMatch(s ?? "", "^[a-z0-9][a-z0-9-]{0,39}$"); }
        public static bool Digits(string s) { return Regex.IsMatch(s ?? "", "^[0-9]{1,12}$"); }

        // "snap list --all": the disabled (older, kept for rollback) revisions
        public static List<Tuple<string, string>> OldSnaps(string listOutput)
        {
            var list = new List<Tuple<string, string>>();
            foreach (var raw in (listOutput ?? "").Split('\n').Skip(1))
            {
                var c = Regex.Split(raw.Trim(), @"\s+");
                if (c.Length < 4 || !raw.Contains("disabled")) continue;
                if (SnapName(c[0]) && Digits(c[2])) list.Add(Tuple.Create(c[0], c[2]));
            }
            return list;
        }

        // "Archived and active journals take up 1.2G in the file system."
        public static long ParseSize(string text)
        {
            var m = Regex.Match(text ?? "", @"([0-9]+(?:\.[0-9]+)?)\s*([KMGT])?i?B?\b", RegexOptions.IgnoreCase);
            if (!m.Success) return -1;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value.ToUpperInvariant();
            int pow = u == "K" ? 1 : u == "M" ? 2 : u == "G" ? 3 : u == "T" ? 4 : 0;
            return (long)(v * Math.Pow(1024, pow));
        }

        public static long FilesIn(string dir, string pattern)
        {
            long t = 0;
            try { foreach (var f in new DirectoryInfo(dir).GetFiles(pattern)) if ((f.Attributes & FileAttributes.ReparsePoint) == 0) t += f.Length; } catch { }
            return t;
        }
    }

    // Opening a folder, a file's place, or a web page: Explorer on Windows, the desktop's own file manager / browser on Linux
    public static class Shell
    {
        // On Windows, Elixar runs as administrator. A web link, an .html file or "ms-settings:" opens with the program
        // registered for it under your account (HKCU), which any program you run can change: started from Elixar, that
        // program would get administrator rights. So everything is opened with your normal rights, through the desktop.
        public static void Open(string target)
        {
            if (Win.IsWindows) Explorer("\"" + target.Replace("\"", "") + "\"");
            else Lx.Open(target);
        }

        static void Explorer(string args)
        {
            string explorer = Win.SystemExe("explorer.exe");
            if (!Sys.IsAdmin()) { Process.Start(new ProcessStartInfo(explorer, args) { UseShellExecute = false }); return; }
            try { using (var p = Unelevated.Start(explorer, args)) { } }
            catch (Exception e) { throw new InvalidOperationException("Elixar didn't open it, so it never runs with administrator rights: " + e.Message, e); }
        }

        public static void Select(string path)
        {
            if (Win.IsWindows) { Explorer("/select,\"" + path.Replace("\"", "") + "\""); return; }
            // the file manager shows the file selected; if it can't, its folder opens
            string o;
            string uri = new Uri(path).AbsoluteUri;
            if (Lx.Run("gdbus", "call --session --dest org.freedesktop.FileManager1 --object-path /org/freedesktop/FileManager1 --method org.freedesktop.FileManager1.ShowItems " +
                       Lx.Arg("['" + uri.Replace("'", "%27") + "']") + " \"\"", 10000, out o) != 0)
                Lx.Open(Path.GetDirectoryName(path) ?? path);
        }
    }

    // ============================ what can be cleaned on Linux ============================
    public static class LinuxCatalog
    {
        const long KeepJournal = 200L << 20;

        public static List<CleanItem> Build()
        {
            string home = Lx.Home, cache = Lx.CacheHome, trash = Path.Combine(Lx.DataHome, "Trash");
            var items = new List<CleanItem>();
            Func<string, string> H = rel => Path.Combine(home, rel);

            // ---- your account (no password needed) ----
            string thumbs = Path.Combine(cache, "thumbnails");
            items.Add(new CleanItem
            {
                Name = "Thumbnail cache", Tier = Tier.Safe, DefaultOn = true, Where = thumbs,
                Info = "Small pictures of your photos and videos made by the file manager. Rebuilt automatically.",
                Measure = () => Fs.MeasureIn(thumbs), Clean = log => Fs.ClearIn(thumbs)
            });

            var browsers = new[] { Path.Combine(cache, "mozilla"), H("snap/firefox/common/.cache/mozilla"), Path.Combine(cache, "google-chrome"), Path.Combine(cache, "chromium"),
                                   H("snap/chromium/common/.cache/chromium"), Path.Combine(cache, "BraveSoftware"), Path.Combine(cache, "microsoft-edge"), Path.Combine(cache, "opera"), Path.Combine(cache, "vivaldi") };
            items.Add(new CleanItem
            {
                Name = "Browser caches", Tier = Tier.Safe, DefaultOn = false, Where = "~/.cache: Firefox, Chrome, Chromium, Brave, Edge, Opera, Vivaldi",
                Info = "Saved copies of websites. Your logins, history and bookmarks are not touched. Sites load a little slower the first time.",
                Procs = new[] { "firefox", "firefox-bin", "chrome", "chromium", "chromium-browse", "brave", "msedge", "opera", "vivaldi-bin" },
                Measure = () => browsers.Sum(b => Fs.SizeOf(b)), Clean = log => { foreach (var b in browsers) Fs.ClearIn(b); }
            });

            var dev = new[] { Path.Combine(cache, "pip"), H(".npm/_cacache"), Path.Combine(cache, "yarn"), Path.Combine(cache, "go-build"), Path.Combine(cache, "pnpm"), Path.Combine(cache, "electron"), H(".gradle/caches") };
            items.Add(new CleanItem
            {
                Name = "Developer caches (pip, npm, yarn, Go, Gradle)", Tier = Tier.Safe, DefaultOn = false, Where = "~/.cache/pip, ~/.npm/_cacache, ~/.cache/yarn, ~/.cache/go-build, ~/.gradle/caches ...",
                Info = "Downloaded packages kept by programming tools. They download again when needed.",
                Measure = () => dev.Sum(d => Fs.SizeOf(d)), Clean = log => { foreach (var d in dev) Fs.ClearIn(d); }
            });

            var known = new HashSet<string>(new[] { "thumbnails", "mozilla", "google-chrome", "chromium", "BraveSoftware", "microsoft-edge", "opera", "vivaldi", "pip", "yarn", "go-build", "pnpm", "electron" });
            Func<List<string>> others = () => Fs.Children(cache, "*").Where(p => !known.Contains(Path.GetFileName(p))).ToList();
            items.Add(new CleanItem
            {
                Name = "Other app caches (~/.cache)", Tier = Tier.Safe, DefaultOn = false, Where = cache,
                Info = "Everything else apps keep in ~/.cache, which is meant for files they can make again. Close your apps first.",
                Measure = () => others().Sum(p => Fs.SizeOf(p)), Clean = log => { foreach (var p in others()) Fs.Delete(p); }
            });

            items.Add(new CleanItem
            {
                Name = "Trash", Tier = Tier.Review, DefaultOn = false, Where = trash,
                Info = "Files you deleted, waiting in the Trash. Emptying it can't be undone, so look inside first.",
                Measure = () => Fs.SizeOf(Path.Combine(trash, "files")) + Fs.SizeOf(Path.Combine(trash, "info")),
                Clean = log => { Fs.ClearIn(Path.Combine(trash, "files")); Fs.ClearIn(Path.Combine(trash, "info")); Fs.ClearIn(Path.Combine(trash, "expunged")); }
            });

            // ---- the system (one password prompt for all of them together) ----
            bool apt = Lx.Tool("apt-get") != null && OsInfo.Current.Family == "debian";
            if (apt)
                items.Add(new CleanItem
                {
                    Name = "Downloaded update packages (apt)", Tier = Tier.Safe, DefaultOn = true, Where = "/var/cache/apt/archives", RootScript = "apt-clean",
                    Info = "Package files that updates already installed. Asks for your password once.",
                    Measure = () => Lx.FilesIn("/var/cache/apt/archives", "*.deb") + Lx.FilesIn("/var/cache/apt/archives/partial", "*")
                });

            if (Lx.Tool("journalctl") != null)
                items.Add(new CleanItem
                {
                    Name = "Old system logs (journal)", Tier = Tier.Safe, DefaultOn = true, Where = "/var/log/journal", RootScript = "journal-vacuum",
                    Info = "Old records of what the system did. The newest 200 MB stay for troubleshooting. Asks for your password once.",
                    Measure = () => { string o; return Lx.Run("journalctl", "--disk-usage", 20000, out o) == 0 ? Math.Max(0, Lx.ParseSize(o) - KeepJournal) : -1; }
                });

            if (Lx.Tool("snap") != null)
            {
                var snapItem = new CleanItem
                {
                    Name = "Old Snap app versions", Tier = Tier.Safe, DefaultOn = true, Where = "/var/lib/snapd/snaps",
                    Info = "Snap keeps the previous version of each app for a rollback. The versions in use are not touched. Asks for your password once."
                };
                snapItem.Measure = () => { long b; snapItem.RootScript = LxSystem.OldSnapScript(out b); return b; };
                items.Add(snapItem);
            }

            items.Add(new CleanItem
            {
                Name = "Crash reports", Tier = Tier.Safe, DefaultOn = true, Where = "/var/crash", RootScript = "crash-clean",
                Info = "Reports saved after an app crashed. Only useful if you send them to the developers. Asks for your password once.",
                Measure = () => Lx.FilesIn("/var/crash", "*.crash") + Lx.FilesIn("/var/crash", "*.upload*")
            });

            if (Lx.Tool("flatpak") != null)
                items.Add(new CleanItem
                {
                    Name = "Unused Flatpak runtimes", Tier = Tier.Safe, DefaultOn = false, Where = "/var/lib/flatpak, ~/.local/share/flatpak",
                    RootScript = "flatpak-unused",
                    Info = "Shared parts no installed Flatpak app needs any more. Asks for your password once.",
                    Measure = () => -1,
                    Clean = log => { string o; Lx.Run("flatpak", "uninstall --user --unused -y --noninteractive", 600000, out o); }
                });

            string dk = LxSystem.Docker;
            if (dk != null)
                foreach (bool all in new[] { false, true })
                {
                    var d = new CleanItem
                    {
                        Name = all ? "Unused Docker images and stopped containers" : "Docker build cache and leftover images",
                        Tier = all ? Tier.Review : Tier.Safe, DefaultOn = false, Where = "/var/lib/docker",
                        Info = all ? "Images no container uses and containers that are stopped (\"docker image prune -a\", \"docker container prune\"). You download the images again if you need them. Volumes, where your databases and app data live, are never touched. If you are not in the docker group, asks for your password once."
                                   : "What Docker kept from building images, and untagged images left behind (\"docker builder prune\", \"docker image prune\"). Rebuilt when needed. Volumes are never touched. If you are not in the docker group, asks for your password once."
                    };
                    bool everything = all;
                    d.Measure = () =>
                    {
                        var u = LxSystem.ReadDocker();
                        d.RootScript = u.NeedsRoot ? LxSystem.DockerScript(dk, everything) : null;
                        if (!u.Reachable) return u.NeedsRoot ? -1 : 0;
                        return everything ? u.ImagesFree + u.ContainersFree : u.CacheFree;
                    };
                    d.Clean = log => { if (d.RootScript != null) return; string o; if (LxSystem.DockerClean(everything, out o) != 0) log("Docker: " + o); };
                    items.Add(d);
                }

            if (apt)
                items.Add(new CleanItem
                {
                    Name = "Old kernels and unneeded packages", Tier = Tier.Review, DefaultOn = false, Where = "apt autoremove", RootScript = "apt-autoremove",
                    Info = "Older Linux kernels and packages that were installed only for something you removed (\"sudo apt autoremove\"). The running kernel always stays. Asks for your password once.",
                    Measure = () => -1
                });

            return items;
        }

        // the Dashboard's map of the system disk: id, name, path, tier, what to do, tool
        public static string[][] Locations()
        {
            string home = Lx.Home, cache = Lx.CacheHome;
            Func<string, string> H = rel => Path.Combine(home, rel);
            return new[]
            {
                new[] { "usr", "System programs (/usr)", "/usr", "critical", "Never delete. Remove apps you don't use with the App Center or apt.", "-" },
                new[] { "boot", "Boot files (/boot)", "/boot", "critical", "Never delete by hand. Old kernels go with \"Old kernels and unneeded packages\".", "Elixar" },
                new[] { "varlib", "System data (/var/lib)", "/var/lib", "critical", "Never delete by hand.", "-" },
                new[] { "opt", "Extra apps (/opt)", "/opt", "critical", "Remove those apps with their own uninstaller.", "-" },
                new[] { "swap", "Swap file", "/swapfile", "critical", "Managed by the system. Leave it.", "-" },
                new[] { "snaps", "Snap apps", "/var/lib/snapd/snaps", "caution", "Remove apps with Snap; Elixar removes the old versions.", "Elixar" },
                new[] { "flatpak", "Flatpak apps", "/var/lib/flatpak", "caution", "Remove apps with Flatpak; Elixar removes unused runtimes.", "Elixar" },
                new[] { "docker", "Docker", "/var/lib/docker", "caution", "Clean with \"docker system prune\".", "Docker" },
                new[] { "logs", "System logs (/var/log)", "/var/log", "caution", "Old logs: Elixar's \"Old system logs\".", "Elixar" },
                new[] { "downloads", "Downloads folder", H("Downloads"), "review", "Sort by size, delete old installers and archives.", "Files" },
                new[] { "videos", "Videos folder", H("Videos"), "review", "Move large videos to an external drive or cloud.", "Files" },
                new[] { "desktop", "Desktop", H("Desktop"), "review", "Review large files you no longer need.", "Files" },
                new[] { "trash", "Trash", Path.Combine(Lx.DataHome, "Trash"), "review", "Empty it once you are sure nothing in it is needed.", "Elixar" },
                new[] { "steam", "Steam games", H(".local/share/Steam"), "review", "Uninstall games you don't play from Steam.", "Steam" },
                new[] { "cache", "Your app caches (~/.cache)", cache, "safe", "Clear with Elixar (close apps first).", "Elixar" },
                new[] { "apt", "Downloaded update packages", "/var/cache/apt/archives", "safe", "Clear with Elixar.", "Elixar" },
                new[] { "journal", "System log journal", "/var/log/journal", "safe", "Shrink with Elixar.", "Elixar" },
                new[] { "crash", "Crash reports", "/var/crash", "safe", "Clear with Elixar.", "Elixar" },
            };
        }

        public static readonly string[][] Rules =
        {
            new[] { @"^/(swapfile|swap\.img)$", "critical", "Swap file. Managed by the system." },
            new[] { @"^/(usr|bin|sbin|lib|lib32|lib64|etc|boot)/", "critical", "Part of the system or an installed app. Remove the app instead." },
            new[] { @"^/var/cache/apt/", "safe", "Downloaded update package. Clear with Elixar." },
            new[] { @"^/var/crash/", "safe", "Crash report." },
            new[] { @"^/var/log/", "caution", "System log. Old logs: Elixar's \"Old system logs\"." },
            new[] { @"^/var/lib/snapd/snaps/", "caution", "Snap app package. Remove the app with Snap; Elixar removes old versions." },
            new[] { @"^/var/lib/(flatpak|docker)/|/\.local/share/flatpak/", "caution", "App platform data. Remove apps with their own tool." },
            new[] { @"/\.cache/", "safe", "App cache. Rebuilt automatically." },
            new[] { @"/\.local/share/trash/", "review", "Already in the Trash. Empty it if you don't need it back." },
            new[] { @"\.(vdi|vmdk|qcow2|vhdx|vhd)$", "caution", "Virtual disk. Delete only by removing that virtual machine." },
            new[] { @"^/(var|opt|snap|srv)/", "critical", "System or app data. Remove the app instead." },
            new[] { @"/downloads/", "review", "In Downloads. Delete if you no longer need it." },
            new[] { @"\.(iso|zip|tar|gz|xz|7z|rar|deb|appimage)$", "review", "Installer or archive. Delete if you no longer need it." },
            new[] { @"\.(mp4|mkv|mov|avi|webm)$", "review", "Video. Move to an external drive or cloud if you want to keep it." },
            new[] { @"^/home/", "review", "Your file. Only you know if you still need it." },
        };
    }

    // ============================ schedules: systemd timers for your account ============================
    // The Automation tab builds Windows Task Scheduler commands; on Linux the same commands become
    // ~/.config/systemd/user/elixar-*.timer files (they run as you, never as root).
    public static class LxTimers
    {
        public static string Dir { get { return Path.Combine(Path.Combine(Lx.ConfigHome, "systemd"), "user"); } }
        public static string UnitFor(string task) { return "elixar-" + Regex.Replace(task.Replace(Automation.Folder, "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-'); }

        static readonly string[] Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

        // the parts of one schtasks command: name, program and arguments, schedule
        public static Dictionary<string, string> Parse(string args)
        {
            var d = new Dictionary<string, string>();
            var m = Regex.Matches(args ?? "", "/(TN|TR|SC|D|ST|DELAY)\\s+(\"(?:\\\\\"|[^\"])*\"|\\S+)");
            foreach (Match x in m)
            {
                string v = x.Groups[2].Value;
                if (v.StartsWith("\"")) v = v.Substring(1, v.Length - 2).Replace("\\\"", "\"");
                d[x.Groups[1].Value] = v;
            }
            d["op"] = args.TrimStart().StartsWith("/Delete") ? "delete" : args.TrimStart().StartsWith("/Create") ? "create" : "?";
            return d;
        }

        // the unit files for one schtasks /Create command (null if the command isn't one Elixar makes)
        public static string[] Units(string args, string mono)
        {
            var p = Parse(args);
            string tr, tn;
            if (p["op"] != "create" || !p.TryGetValue("TN", out tn) || !p.TryGetValue("TR", out tr)) return null;
            var tm = Regex.Match(tr, "^\"([^\"]+)\" (--[a-z-]+(?: [0-9]{1,2})?)$");
            if (!tm.Success) return null;
            string exe = tm.Groups[1].Value, rest = tm.Groups[2].Value;
            if (exe.IndexOfAny(new[] { '\n', '\r', '%', '$', '\\' }) >= 0 || !exe.StartsWith("/")) return null;   // nothing systemd would read differently
            string service = "[Unit]\nDescription=Elixar: " + tn.Replace(Automation.Folder, "") + "\n\n[Service]\nType=oneshot\nExecStart=" + mono + " \"" + exe + "\" " + rest + "\n";
            string sc = p.ContainsKey("SC") ? p["SC"] : "", st = p.ContainsKey("ST") ? p["ST"] : "12:00";
            if (!Regex.IsMatch(st, "^[0-2][0-9]:[0-5][0-9]$")) return null;
            string when;
            if (sc == "DAILY") when = "OnCalendar=*-*-* " + st + ":00\nPersistent=true";
            else if (sc == "WEEKLY")
            {
                string day = p.ContainsKey("D") ? p["D"] : "SUN";
                string dd = Days.FirstOrDefault(x => x.Equals(day, StringComparison.OrdinalIgnoreCase));
                if (dd == null) return null;
                when = "OnCalendar=" + dd + " *-*-* " + st + ":00\nPersistent=true";
            }
            else if (sc == "ONLOGON") when = "OnStartupSec=3min";
            else return null;
            string timer = "[Unit]\nDescription=Elixar: " + tn.Replace(Automation.Folder, "") + "\n\n[Timer]\n" + when + "\n\n[Install]\nWantedBy=timers.target\n";
            return new[] { service, timer };
        }

        static string Systemctl(string args) { string o; Lx.Run("systemctl", "--user " + args, 30000, out o); return o; }

        public static int Apply(string args, out string output)
        {
            output = "";
            var p = Parse(args);
            string tn;
            if (!p.TryGetValue("TN", out tn)) { output = "no task name"; return 1; }
            string unit = UnitFor(tn), svc = Path.Combine(Dir, unit + ".service"), tim = Path.Combine(Dir, unit + ".timer");
            if (p["op"] == "delete")
            {
                if (!File.Exists(tim)) return 1;
                Systemctl("disable --now " + unit + ".timer");
                try { File.Delete(tim); File.Delete(svc); } catch (Exception e) { output = e.Message; return 1; }
                Systemctl("daemon-reload");
                return 0;
            }
            string mono = Lx.Tool("mono");
            if (mono == null) { output = "mono is not installed"; return 1; }
            var u = Units(args, mono);
            if (u == null) { output = "not a schedule Elixar makes"; return 1; }
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(svc, u[0]); File.WriteAllText(tim, u[1]);
            }
            catch (Exception e) { output = e.Message; return 1; }
            Systemctl("daemon-reload");
            output = Systemctl("enable --now " + unit + ".timer");
            return 0;
        }

        // what the Automation tab shows: the same TaskState Windows gives
        public static TaskState Query(string task)
        {
            var st = new TaskState();
            string unit = UnitFor(task), svc = Path.Combine(Dir, unit + ".service"), tim = Path.Combine(Dir, unit + ".timer");
            if (!File.Exists(tim) || !File.Exists(svc)) return st;
            st.Exists = true;
            try
            {
                string s = File.ReadAllText(svc), t = File.ReadAllText(tim);
                var ex = Regex.Match(s, "ExecStart=\\S+ \"([^\"]+)\" (.*)");
                if (ex.Success) { st.Command = ex.Groups[1].Value; st.Arguments = ex.Groups[2].Value.Trim(); }
                var cal = Regex.Match(t, "OnCalendar=(?:([A-Za-z]{3}) )?\\*-\\*-\\* ([0-9]{2}):([0-9]{2})");
                if (cal.Success)
                {
                    st.Start = DateTime.Today.AddHours(int.Parse(cal.Groups[2].Value)).AddMinutes(int.Parse(cal.Groups[3].Value));
                    if (cal.Groups[1].Success) { int i = Array.FindIndex(Days, x => x == cal.Groups[1].Value); if (i >= 0) st.Day = Automation.Days[i]; }
                }
                st.Enabled = Systemctl("is-enabled " + unit + ".timer").Trim().StartsWith("enabled");
            }
            catch { }
            return st;
        }
    }
}

namespace CDriveCleaner
{
    // ============================ Docker, the system journal and Snap, for the Linux tab ============================
    public static class LxSystem
    {
        // ---- Docker ----
        public static string Docker { get { return Lx.Tool("docker") ?? (File.Exists("/snap/bin/docker") ? "/snap/bin/docker" : null); } }

        public class DockerUse { public bool Installed, Reachable, NeedsRoot; public long Images, ImagesFree, Containers, ContainersFree, Volumes, Cache, CacheFree; public string Error = ""; }

        // "8.5GB", "512MB", "1.2kB", "0B": Docker counts in thousands
        public static long DockerSize(string s)
        {
            var m = Regex.Match(s ?? "", @"([0-9]+(?:\.[0-9]+)?)\s*([kKMGT]?)B");
            if (!m.Success) return 0;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value.ToUpperInvariant();
            return (long)(v * Math.Pow(1000, u == "K" ? 1 : u == "M" ? 2 : u == "G" ? 3 : u == "T" ? 4 : 0));
        }

        // "docker system df --format {{.Type}}|{{.Size}}|{{.Reclaimable}}"
        public static DockerUse ParseDf(string output)
        {
            var d = new DockerUse { Installed = true, Reachable = true };
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var p = raw.Trim().Split('|');
                if (p.Length < 3) continue;
                long size = DockerSize(p[1]), free = DockerSize(p[2]);
                string t = p[0].Trim().ToLowerInvariant();
                if (t == "images") { d.Images = size; d.ImagesFree = free; }
                else if (t == "containers") { d.Containers = size; d.ContainersFree = free; }
                else if (t.StartsWith("local volumes")) d.Volumes = size;
                else if (t.StartsWith("build cache")) { d.Cache = size; d.CacheFree = free; }
            }
            return d;
        }

        public static DockerUse ReadDocker()
        {
            string dk = Docker;
            if (dk == null) return new DockerUse();
            string o;
            int code = Lx.Run(dk, "system df --format \"{{.Type}}|{{.Size}}|{{.Reclaimable}}\"", 60000, out o);
            if (code == 0) return ParseDf(o);
            var u = new DockerUse { Installed = true, Error = o };
            u.NeedsRoot = o.IndexOf("permission denied", StringComparison.OrdinalIgnoreCase) >= 0;   // not in the "docker" group: the clean asks for your password
            return u;
        }

        // the only docker paths a script may name (found by Lx.Tool, or the Snap one)
        static readonly string[] DockerPaths = { "/usr/bin/docker", "/bin/docker", "/usr/sbin/docker", "/sbin/docker", "/snap/bin/docker" };

        // all = also images no container uses, and stopped containers. Volumes (databases, app data) are never touched.
        public static string DockerScript(string dk, bool all)
        {
            if (!DockerPaths.Contains(dk)) return null;
            return (all ? "docker-all:" : "docker-safe:") + dk;
        }

        // as you when you are in the "docker" group; otherwise the same fixed commands after one password prompt
        public static int DockerClean(bool all, out string output)
        {
            output = "";
            string dk = Docker;
            if (dk == null) { output = "Docker is not installed"; return -1; }
            var steps = all ? new[] { "container prune -f", "image prune -a -f" } : new[] { "builder prune -f", "image prune -f" };
            var sb = new StringBuilder();
            foreach (var a in steps)
            {
                string o;
                int code = Lx.Run(dk, a, 30 * 60 * 1000, out o);
                if (code != 0 && o.IndexOf("permission denied", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Lx.RunAsRoot(DockerScript(dk, all), out output);
                sb.AppendLine(o);
                if (code != 0) { output = sb.ToString().Trim(); return code; }
            }
            output = sb.ToString().Trim();
            return 0;
        }

        // ---- the system journal ----
        public const string CapFile = "/etc/systemd/journald.conf.d/60-elixar.conf";
        public static readonly string[] Caps = { "200M", "500M", "1G" };

        // the limit in force: the main file, then the drop-ins in name order (the last one wins)
        public static string CurrentCap()
        {
            string cap = null;
            var files = new List<string> { "/etc/systemd/journald.conf" };
            try { if (Directory.Exists("/etc/systemd/journald.conf.d")) files.AddRange(Directory.GetFiles("/etc/systemd/journald.conf.d", "*.conf").OrderBy(f => f, StringComparer.Ordinal)); } catch { }
            foreach (var f in files)
                try
                {
                    foreach (var l in File.ReadAllLines(f))
                    {
                        var m = Regex.Match(l.Trim(), @"^SystemMaxUse\s*=\s*([0-9]+[KMGT]?)\s*$");
                        if (m.Success) cap = m.Groups[1].Value;
                    }
                }
                catch { }
            return cap;
        }

        // fixed commands: only one of the three sizes above can be written
        public static string CapScript(string size)
        {
            return Caps.Contains(size) ? "journal-cap:" + size : null;
        }
        public static string CapText(string cap)
        {
            var m = System.Text.RegularExpressions.Regex.Match(cap ?? "", "^([0-9]+)([KMGT]?)$");
            return m.Success ? m.Groups[1].Value + " " + (m.Groups[2].Value == "" ? "bytes" : m.Groups[2].Value + "B") : cap;
        }
        public const string UncapScript = "journal-uncap";

        // ---- Snap ----
        public class SnapApp { public string Name = ""; public int Versions; public long Bytes; }

        // every .snap file is one version of one app: name_revision.snap
        public static List<SnapApp> Snaps(string dir)
        {
            var map = new Dictionary<string, SnapApp>();
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles("*.snap"))
                {
                    if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var m = Regex.Match(f.Name, @"^([a-z0-9][a-z0-9-]*)_(x?[0-9]+)\.snap$");
                    if (!m.Success) continue;
                    SnapApp a;
                    if (!map.TryGetValue(m.Groups[1].Value, out a)) map[m.Groups[1].Value] = a = new SnapApp { Name = m.Groups[1].Value };
                    a.Versions++; a.Bytes += f.Length;
                }
            }
            catch { }
            return map.Values.OrderByDescending(a => a.Bytes).ToList();
        }

        public const string SnapDir = "/var/lib/snapd/snaps";

        // the "snap remove" commands for every older version kept for a rollback (names and numbers checked)
        public static string OldSnapScript(out long bytes)
        {
            bytes = 0; string o;
            if (Lx.Run("snap", "list --all", 30000, out o) != 0) return null;
            var old = Lx.OldSnaps(o);
            bytes = old.Sum(x => { try { return new FileInfo(SnapDir + "/" + x.Item1 + "_" + x.Item2 + ".snap").Length; } catch { return 0L; } });
            return old.Count == 0 ? null : string.Join(" ", old.Select(x => "snap-remove:" + x.Item1 + ":" + x.Item2));
        }

        // the reminder comes at most once a week
        public static bool SnapWarnDue(string stamp, DateTime today)
        {
            DateTime last;
            return !DateTime.TryParseExact(stamp ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last) || (today - last).TotalDays >= 7;
        }

        public static string Kernel { get { try { return File.ReadAllText("/proc/sys/kernel/osrelease").Trim(); } catch { return ""; } } }

        // more than usual: over 10 GB, or over a tenth of the system disk
        public static bool SnapsLarge(long snapBytes, long diskTotal)
        {
            return snapBytes > (10L << 30) || (diskTotal > 0 && snapBytes > diskTotal / 10);
        }
    }
}
