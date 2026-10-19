// Elixar
// Removes caches, temp files and update leftovers from drive C:.
// Never touches Windows system files, Program Files or your documents.
// Also saves a read-only scan (c-drive-scan.json) for the Elixar Drive Map dashboard.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// every DllImport looks only in System32, never next to Elixar.exe (a Downloads folder is writable by any program)
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
[assembly: AssemblyTitle("Elixar")]
[assembly: AssemblyProduct("Elixar")]
[assembly: AssemblyDescription("Elixar - the cure for a full C: drive. Made by Elisar.")]
[assembly: AssemblyCompany("Elisar")]
[assembly: AssemblyCopyright("Made by Elisar (c) 2026")]
[assembly: AssemblyVersion("2.2.1.0")]
[assembly: AssemblyFileVersion("2.2.1.0")]

namespace CDriveCleaner
{
    public enum Tier { Safe, Review, Caution }

    public class CleanItem
    {
        public string Name;
        public Tier Tier;
        public bool DefaultOn;
        public string Where;
        public string Info;
        public string[] Procs = new string[0];
        public Func<long> Measure;          // -1 = size unknown until it runs
        public Action<Action<string>> Clean; // gets a log callback
        public long Size;
        public bool SizeUnknown;
        public bool Privacy;               // traces of what you did, not disk space
        public string RootScript;          // Linux: fixed commands run as root after one password prompt (null: runs as you)
        public string Drive;               // "D:" for items on another drive (null: C: and the system)
    }

    // ---------------- file helpers ----------------
    public static class Fs
    {
        public static long SizeOf(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (!Directory.Exists(path)) return 0;
                // a junction or symlink points somewhere else: do not count what it points to
                if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0) return 0;
            }
            catch { return 0; }

            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var f in di.EnumerateFiles())
                    {
                        try { if (!Lx.On || (f.Attributes & FileAttributes.ReparsePoint) == 0) total += f.Length; } catch { }
                    }
                    foreach (var d in di.EnumerateDirectories())
                    {
                        // skip junctions and symlinks so nothing is counted twice; on Linux also /proc, /sys and other disks
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !Lx.SkipDir(d.FullName, path)) stack.Push(d.FullName);
                    }
                }
                catch { }
            }
            return total;
        }

        public static string[] Children(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return new string[0];
                return Directory.GetFileSystemEntries(dir, pattern);
            }
            catch { return new string[0]; }
        }

        public static long MeasureIn(string dir, string pattern = "*")
        {
            long t = 0;
            foreach (var c in Children(dir, pattern)) t += SizeOf(c);
            return t;
        }

        // Deletes everything inside dir that matches pattern. The folder itself stays.
        // Files that are in use are skipped.
        public static void ClearIn(string dir, string pattern = "*")
        {
            foreach (var c in Children(dir, pattern)) Delete(c);
        }

        // Deletes a file or a whole folder. Junctions/symlinks are removed as links, never followed.
        public static void Delete(string path)
        {
            if (Win.IsWindows) { try { SafeDelete.Delete(path); } catch { } return; }
            DeleteLinux(path, new HashSet<string>(Lx.Mounts().Select(m => m.Point)));
        }

        // Linux: a link is removed as a link (its target is never touched, and its permissions are never changed),
        // and a folder where another disk, a network share or a cloud drive is mounted is left alone completely.
        static void DeleteLinux(string path, HashSet<string> mountPoints)
        {
            try
            {
                if (mountPoints.Contains(path.TrimEnd('/'))) return;
                if (File.Exists(path)) { File.Delete(path); return; }
                if (!Directory.Exists(path)) return;
                var di = new DirectoryInfo(path);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path, false); return; }
                foreach (var c in Directory.GetFileSystemEntries(path)) DeleteLinux(c, mountPoints);
                Directory.Delete(path, false);
            }
            catch { }
        }

        public static List<string> ProfileCaches(string userDataRoot)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(userDataRoot)) return list;
                foreach (var p in Directory.GetDirectories(userDataRoot))
                {
                    string n = Path.GetFileName(p);
                    if (n == "Default" || n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(Path.Combine(p, "Cache"));
                        list.Add(Path.Combine(p, "Code Cache"));
                        list.Add(Path.Combine(p, "GPUCache"));
                    }
                }
            }
            catch { }
            return list;
        }

        public static string Fmt(long b)
        {
            if (b < 0) return "varies";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("N1") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("N0") + " MB";
            if (b > 0) return Math.Max(1, b / 1024).ToString("N0") + " KB";
            return "0";
        }
    }

    // ---------------- system helpers ----------------
    public static class Sys
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static void TryDpiAware() { try { SetProcessDPIAware(); } catch { } }

        public static void EmptyRecycleBin()
        {
            // no confirmation, no progress window, no sound
            SHEmptyRecycleBin(IntPtr.Zero, "C:\\", 0x7);
        }

        public static long Free() { try { return new DriveInfo(Win.IsWindows ? "C" : "/").AvailableFreeSpace; } catch { return 0; } }
        public static long Total() { try { return new DriveInfo(Win.IsWindows ? "C" : "/").TotalSize; } catch { return 0; } }
        public static string Root { get { return Win.IsWindows ? @"C:\" : "/"; } }
        public static string Main { get { return Win.IsWindows ? "C:" : "/"; } }      // short label

        public static bool IsAdmin()
        {
            if (!Win.IsWindows) return Lx.IsRoot;
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string Run(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(Win.SystemExe(exe), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    return "timed out";
                }
                return (o.Result + " " + e.Result).Trim();
            }
        }

        // a PowerShell string literal: PowerShell also ends a '...' string at the curly quotes, so those are doubled too
        public static string PsQuote(string v)
        {
            var sb = new StringBuilder("'");
            foreach (char c in v ?? "") { if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B') sb.Append(c); sb.Append(c); }
            return sb.Append('\'').ToString();
        }

        // TLS 1.2 (and 1.3 where Windows offers it) only. Adding 1.2 to the old default would still allow SSL 3 and TLS 1.0.
        public static void SecureTls()
        {
            var keep13 = System.Net.ServicePointManager.SecurityProtocol & (System.Net.SecurityProtocolType)12288;
            System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072 | keep13;
        }

        // PowerShell looks for the commands it loads (Remove-AppxPackage, Checkpoint-Computer...) in the folders listed in
        // PSModulePath, starting with Documents\WindowsPowerShell\Modules, which any program you run can write to.
        // As administrator, a planted module there would run instead of Windows' own. So only Windows' modules are used.
        public static string PsSafe(string script)
        {
            return "$env:PSModulePath = Join-Path $PSHOME 'Modules'; " + script;
        }

        public static string RunPowerShell(string script, int timeoutMs)
        {
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(PsSafe(script)));
            return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, timeoutMs);
        }

        // Stops the services that are running; returns their names so they can be started again.
        public static List<string> StopServices(params string[] names)
        {
            var stopped = new List<string>();
            foreach (var n in names)
            {
                try
                {
                    using (var sc = new ServiceController(n))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                            stopped.Add(n);
                        }
                    }
                }
                catch { }
            }
            return stopped;
        }

        public static void StartServices(IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                try { using (var sc = new ServiceController(n)) sc.Start(); } catch { }
            }
        }

        public static List<Process> FindProcesses(string[] patterns)
        {
            var found = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                string n;
                try { n = p.ProcessName; } catch { continue; }
                foreach (var pat in patterns)
                {
                    bool hit = pat.EndsWith("*")
                        ? n.StartsWith(pat.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                        : n.Equals(pat, StringComparison.OrdinalIgnoreCase);
                    if (hit) { found.Add(p); break; }
                }
            }
            return found;
        }
    }

    // ---------------- what can be cleaned ----------------
    public static class Catalog
    {
        public static bool WindowsOnLinux;   // self-test only: check the Windows list's logic on a Linux test machine
        public static List<CleanItem> Build()
        {
            if (Lx.On && !WindowsOnLinux) return LinuxCatalog.Build();
            string L = UserDirs.Local;
            string R = UserDirs.Roaming;
            string T = UserDirs.Temp;

            string nvDx = Path.Combine(L, "NVIDIA", "DXCache");
            string nvGl = Path.Combine(L, "NVIDIA", "GLCache");
            string stremio = Path.Combine(R, "stremio", "stremio-server", "stremio-cache");
            string winTemp = @"C:\Windows\Temp";
            string wuDl = @"C:\Windows\SoftwareDistribution\Download";
            string doCache = @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache";
            string d3d = Path.Combine(L, "D3DSCache");
            string explorer = Path.Combine(L, "Microsoft", "Windows", "Explorer");
            string werArchive = @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive";
            string werQueue = @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue";
            string minidump = @"C:\Windows\Minidump";
            string memDump = @"C:\Windows\MEMORY.DMP";
            string appDumps = Path.Combine(L, "CrashDumps");
            string cbs = @"C:\Windows\Logs\CBS";
            string chrome = Path.Combine(L, "Google", "Chrome", "User Data");
            string edge = Path.Combine(L, "Microsoft", "Edge", "User Data");
            string recycle = @"C:\$Recycle.Bin";
            string getCurrent = @"C:\$GetCurrent";
            string winReAgent = @"C:\$WinREAgent";
            string adobeArm = @"C:\ProgramData\Adobe\ARM";

            return new List<CleanItem>
            {
                new CleanItem {
                    Name = "NVIDIA shader cache", Tier = Tier.Safe, DefaultOn = true, Where = nvDx,
                    Info = "Compiled game shaders. Games rebuild them, so the first launch after cleaning can stutter for a minute. Close all games first. Tip: cap it in NVIDIA Control Panel > Manage 3D settings > Shader Cache Size.",
                    Measure = () => Fs.MeasureIn(nvDx) + Fs.MeasureIn(nvGl),
                    Clean = log => { Fs.ClearIn(nvDx); Fs.ClearIn(nvGl); } },

                new CleanItem {
                    Name = "Stremio cache", Tier = Tier.Safe, DefaultOn = true, Where = stremio, Procs = new[] { "stremio*" },
                    Info = "Pieces of videos you streamed. Stremio must be closed; the cleaner offers to close it. Tip: set a limit in Stremio > Settings > Streaming > Caching.",
                    Measure = () => Fs.MeasureIn(stremio),
                    Clean = log => Fs.ClearIn(stremio) },

                new CleanItem {
                    Name = "Your temp files", Tier = Tier.Safe, DefaultOn = true, Where = T,
                    Info = "Temporary files left by apps and installers. Files still in use are skipped automatically.",
                    Measure = () => Fs.MeasureIn(T),
                    Clean = log => Fs.ClearIn(T) },

                new CleanItem {
                    Name = "Windows temp files", Tier = Tier.Safe, DefaultOn = true, Where = winTemp,
                    Info = "Temporary files left by Windows and installers. Files still in use are skipped.",
                    Measure = () => Fs.MeasureIn(winTemp),
                    Clean = log => Fs.ClearIn(winTemp) },

                new CleanItem {
                    Name = "Windows Update downloads", Tier = Tier.Safe, DefaultOn = true, Where = wuDl,
                    Info = "Update files that are already installed. Run this when Windows Update is not in the middle of installing. Windows Update is paused for a moment and started again.",
                    Measure = () => Fs.MeasureIn(wuDl),
                    Clean = log => {
                        var stopped = Sys.StopServices("wuauserv", "bits");
                        try { Fs.ClearIn(wuDl); } finally { Sys.StartServices(stopped); }
                    } },

                new CleanItem {
                    Name = "Delivery Optimization cache", Tier = Tier.Safe, DefaultOn = true, Where = doCache,
                    Info = "Update pieces Windows shares with other PCs. Cleared with the official Windows command.",
                    Measure = () => Fs.MeasureIn(doCache),
                    Clean = log => {
                        try { Sys.RunPowerShell("if (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue) { Delete-DeliveryOptimizationCache -Force }", 120000); }
                        catch { }
                        Fs.ClearIn(doCache);
                    } },

                new CleanItem {
                    Name = "DirectX shader cache", Tier = Tier.Safe, DefaultOn = true, Where = d3d,
                    Info = "Windows shader cache. Games rebuild it.",
                    Measure = () => Fs.MeasureIn(d3d),
                    Clean = log => Fs.ClearIn(d3d) },

                new CleanItem {
                    Name = "Thumbnail cache", Tier = Tier.Safe, DefaultOn = true, Where = explorer + @"\thumbcache_*.db",
                    Info = "Picture previews in File Explorer. Rebuilt automatically. Files Explorer is using are skipped.",
                    Measure = () => Fs.MeasureIn(explorer, "thumbcache_*.db"),
                    Clean = log => Fs.ClearIn(explorer, "thumbcache_*.db") },

                new CleanItem {
                    Name = "Error reports", Tier = Tier.Safe, DefaultOn = true, Where = @"C:\ProgramData\Microsoft\Windows\WER",
                    Info = "Reports Windows saved after app crashes.",
                    Measure = () => Fs.MeasureIn(werArchive) + Fs.MeasureIn(werQueue),
                    Clean = log => { Fs.ClearIn(werArchive); Fs.ClearIn(werQueue); } },

                new CleanItem {
                    Name = "Crash dumps", Tier = Tier.Safe, DefaultOn = true, Where = minidump + ", " + memDump + ", " + appDumps,
                    Info = "Memory snapshots from past crashes. Only useful if you are investigating a crash.",
                    Measure = () => Fs.MeasureIn(minidump) + Fs.SizeOf(memDump) + Fs.MeasureIn(appDumps),
                    Clean = log => { Fs.ClearIn(minidump); Fs.Delete(memDump); Fs.ClearIn(appDumps); } },

                new CleanItem {
                    Name = "Old Windows servicing logs", Tier = Tier.Safe, DefaultOn = true, Where = cbs + @"\CbsPersist_*",
                    Info = "Archived Windows Update logs. The current log is kept.",
                    Measure = () => Fs.MeasureIn(cbs, "CbsPersist_*"),
                    Clean = log => Fs.ClearIn(cbs, "CbsPersist_*") },

                new CleanItem {
                    Name = "Windows component cleanup (DISM)", Tier = Tier.Safe, DefaultOn = false, Where = @"C:\Windows\WinSxS",
                    Info = "Microsoft's own tool removes replaced versions of Windows components. Safe, but takes 5-20 minutes. Size is only known after it runs.",
                    Measure = () => -1,
                    Clean = log => {
                        log("  DISM is running. This can take 5-20 minutes...");
                        string r = Sys.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 60 * 60 * 1000);
                        string last = r.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
                        if (last.Length > 0) log("  DISM: " + last);
                    } },

                new CleanItem {
                    Name = "Chrome cache", Tier = Tier.Safe, DefaultOn = false, Where = chrome + @"\<profile>\Cache", Procs = new[] { "chrome" },
                    Info = "Cached web pages and images. Passwords, history and logins are kept. Chrome must be closed; the cleaner offers to close it.",
                    Measure = () => Fs.ProfileCaches(chrome).Sum(d => Fs.MeasureIn(d)),
                    Clean = log => { foreach (var d in Fs.ProfileCaches(chrome)) Fs.ClearIn(d); } },

                new CleanItem {
                    Name = "Edge cache", Tier = Tier.Safe, DefaultOn = false, Where = edge + @"\<profile>\Cache", Procs = new[] { "msedge" },
                    Info = "Cached web pages and images. Passwords, history and logins are kept. Edge must be closed; the cleaner offers to close it.",
                    Measure = () => Fs.ProfileCaches(edge).Sum(d => Fs.MeasureIn(d)),
                    Clean = log => { foreach (var d in Fs.ProfileCaches(edge)) Fs.ClearIn(d); } },

                new CleanItem {
                    Name = "Recycle Bin", Tier = Tier.Review, DefaultOn = false, Where = recycle,
                    Info = "Permanently deletes everything in the Recycle Bin. Check it first if unsure.",
                    Measure = () => Fs.MeasureIn(recycle),
                    Clean = log => Sys.EmptyRecycleBin() },

                new CleanItem {
                    Name = "Windows upgrade media ($GetCurrent)", Tier = Tier.Caution, DefaultOn = false, Where = getCurrent,
                    Info = "Setup files left from a past Windows upgrade. Delete only if no Windows version upgrade is in progress right now. Moved to Undo for 7 days first.",
                    Measure = () => Fs.SizeOf(getCurrent),
                    Clean = log => Quarantine.MoveToUndo("Windows upgrade media ($GetCurrent)", new[] { getCurrent }, log) },

                new CleanItem {
                    Name = "Recovery update leftovers ($WinREAgent)", Tier = Tier.Caution, DefaultOn = false, Where = winReAgent,
                    Info = "Leftovers from updating the Windows recovery environment. Delete after updates have finished and you have restarted. Moved to Undo for 7 days first.",
                    Measure = () => Fs.SizeOf(winReAgent),
                    Clean = log => Quarantine.MoveToUndo("Recovery update leftovers ($WinREAgent)", new[] { winReAgent }, log) },

                new CleanItem {
                    Name = "Adobe Reader old updates", Tier = Tier.Caution, DefaultOn = false, Where = adobeArm + @"\Reader_*  and  Acrobat_*",
                    Info = "Update packages Adobe Reader already installed. Reader keeps working; its updater downloads again if it needs to. Moved to Undo for 7 days first.",
                    Measure = () => Fs.MeasureIn(adobeArm, "Reader_*") + Fs.MeasureIn(adobeArm, "Acrobat_*"),
                    Clean = log =>
                    {
                        var dirs = new List<string>();
                        foreach (var pat in new[] { "Reader_*", "Acrobat_*" }) dirs.AddRange(Fs.Children(adobeArm, pat));
                        Quarantine.MoveToUndo("Adobe Reader old updates", dirs, log);
                    } },

                new CleanItem {
                    Name = "Previous Windows installation (Windows.old)", Tier = Tier.Caution, DefaultOn = false, Where = @"C:\Windows.old, C:\$Windows.~BT, C:\$Windows.~WS",
                    Info = "The old Windows kept after a big upgrade, so you can go back for 10 days. Removing it means you can no longer go back. Windows' own Disk Cleanup removes it (10-30 minutes). Too big for Undo.",
                    Measure = WindowsOld.Size,
                    Clean = WindowsOld.Clean },
            }.Concat(Caches.Items()).Concat(Privacy.Items()).Concat(DriveClean.Items(Caches.SteamPath())).ToList();
        }
    }

    // ---------------- read-only scan for the dashboard ----------------
    public static class Scanner
    {
        class Rule { public Regex Re; public string Tier; public string Reason; }

        static readonly Rule[] Rules = new[]
        {
            R(@"^c:\\(pagefile|swapfile)\.sys$", "critical", "Windows memory file. Managed by Windows."),
            R(@"^c:\\hiberfil\.sys$", "caution", "Hibernation file. \"powercfg /h off\" removes it (also turns off Fast Startup)."),
            R(@"\\windows\\installer\\", "critical", "Installer cache. Needed to repair and uninstall apps."),
            R(@"^c:\\windows\.old\\", "caution", "Previous Windows. Remove via Settings > Storage > Temporary files."),
            R(@"^c:\\\$windows\.~(bt|ws)\\", "caution", "Upgrade leftovers. Remove via Disk Cleanup > Windows Setup temporary files."),
            R(@"\\windows\\winsxs\\", "caution", "Component store. Clean only with DISM, never by hand."),
            R(@"\\\$recycle\.bin\\", "review", "Already in the Recycle Bin. Empty the bin if you do not need it back."),
            R(@"\\nvidia\\(dxcache|glcache)\\", "safe", "NVIDIA shader cache. Games rebuild it (short stutter on first launch)."),
            R(@"\\logs\\cbs\\cbspersist_", "safe", "Old Windows servicing log."),
            R(@"stremio-cache", "safe", "Streaming cache. Clear it in the app settings or with this cleaner."),
            R(@"^c:\\\$getcurrent\\", "caution", "Leftover Windows upgrade media. Delete if no Windows upgrade is in progress."),
            R(@"^c:\\\$winreagent\\", "caution", "Recovery-environment update leftovers. Delete after updates finish and you restart."),
            R(@"\\adobe\\arm\\", "caution", "Old Adobe Reader update packages."),
            R(@"\\windows\\softwaredistribution\\download\\", "safe", "Downloaded update files. Clear with Windows Update Cleanup."),
            R(@"\\deliveryoptimization\\", "safe", "Update sharing cache."),
            R(@"\\appdata\\local\\temp\\|^c:\\windows\\temp\\", "safe", "Temporary file."),
            R(@"\\(minidump|crashdumps)\\|\.dmp$", "safe", "Crash dump. Only useful for debugging a past crash."),
            R(@"\\appdata\\.*\\(cache|code cache|gpucache|cache_data)\\", "safe", "App or browser cache. Rebuilt automatically."),
            R(@"\.(vhdx|vhd|vmdk|vdi)$", "caution", "Virtual disk (WSL, Docker, VM). Delete only by removing that VM or distro."),
            R(@"^c:\\(windows|program files|program files \(x86\)|programdata|boot|recovery|system volume information)\\", "critical", "Part of Windows or an installed app. Uninstall the app instead."),
            R(@"\\appdata\\", "caution", "App data (settings, saves, offline files). Remove from inside the app."),
            R(@"^c:\\users\\.*\.(iso|zip|rar|7z|msi|exe|msix)$", "review", "Installer or archive. Delete if you no longer need it."),
            R(@"\\downloads\\", "review", "In Downloads. Delete if you no longer need it."),
            R(@"\.(mp4|mkv|mov|avi|wmv)$", "review", "Video. Move to an external drive or cloud if you want to keep it."),
            R(@"^c:\\users\\", "review", "Your file. Only you know if you still need it."),
        };

        static Rule R(string re, string tier, string reason)
        {
            return new Rule { Re = new Regex(re, RegexOptions.IgnoreCase | RegexOptions.Compiled), Tier = tier, Reason = reason };
        }

        static readonly Rule[] LinuxRules = LinuxCatalog.Rules.Select(r => R(r[0], r[1], r[2])).ToArray();

        public static void Classify(string path, out string tier, out string reason)
        {
            string l = path.ToLowerInvariant();
            if (Lx.On && !l.Contains(":\\"))
            {
                foreach (var r in LinuxRules)
                    if (r.Re.IsMatch(l)) { tier = r.Tier; reason = r.Reason; return; }
                tier = "caution"; reason = "Unrecognised location. Check what created it before deleting.";
                return;
            }
            foreach (var r in Rules)
                if (r.Re.IsMatch(l)) { tier = r.Tier; reason = r.Reason; return; }
            tier = "caution"; reason = "Unrecognised location. Check what created it before deleting.";
        }

        // id, name, path, tier, action, tool
        public static string[][] Locations()
        {
            if (Lx.On && !Catalog.WindowsOnLinux) return LinuxCatalog.Locations();
            string L = UserDirs.Local;
            string U = UserDirs.Home;
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string R = UserDirs.Roaming;
            return new[]
            {
                new[] { "system32", "Windows system files", @"C:\Windows\System32", "critical", "Never delete.", "-" },
                new[] { "progfiles", "Installed programs", @"C:\Program Files", "critical", "Never delete by hand. Uninstall apps you don't use in the Apps tab.", "Apps tab" },
                new[] { "progx86", "Installed programs (32-bit)", @"C:\Program Files (x86)", "critical", "Never delete by hand. Uninstall apps you don't use in the Apps tab.", "Apps tab" },
                new[] { "progdata", "Shared app data", @"C:\ProgramData", "critical", "Never delete by hand. Shrinks when apps are uninstalled.", "-" },
                new[] { "installer", "Installer cache", @"C:\Windows\Installer", "critical", "Never delete. Breaks repair and uninstall of apps.", "-" },
                new[] { "pagefile", "Page file", @"C:\pagefile.sys", "critical", "Managed by Windows. Leave it.", "-" },
                new[] { "winold", "Previous Windows (Windows.old)", @"C:\Windows.old", "caution", "Remove with Elixar: Cleaner > Previous Windows installation.", "Elixar" },
                new[] { "winbt", "Upgrade leftovers ($WINDOWS.~BT)", @"C:\$WINDOWS.~BT", "caution", "Remove with Elixar together with Windows.old (Cleaner).", "Elixar" },
                new[] { "getcurrent", "Upgrade media ($GetCurrent)", @"C:\$GetCurrent", "caution", "Delete with Elixar if no Windows upgrade is in progress.", "Elixar" },
                new[] { "winreagent", "Recovery update leftovers ($WinREAgent)", @"C:\$WinREAgent", "caution", "Delete with Elixar after updates finish and you restart.", "Elixar" },
                new[] { "adobearm", "Adobe Reader update cache", @"C:\ProgramData\Adobe\ARM", "caution", "Delete old update packages with Elixar.", "Elixar" },
                new[] { "winsxs", "Component store (WinSxS)", @"C:\Windows\WinSxS", "caution", "Clean only with DISM (option in Elixar). Size is overstated because of hard links.", "DISM" },
                new[] { "hiberfil", "Hibernation file", @"C:\hiberfil.sys", "caution", "Shrink or turn off with Elixar: Tools > System space > Hibernation file.", "Elixar" },
                new[] { "logs", "Windows logs", @"C:\Windows\Logs", "caution", "Old servicing logs can be cleared with Elixar.", "Elixar" },
                new[] { "downloads", "Downloads folder", Path.Combine(U, "Downloads"), "review", "Sort by size, delete old installers and archives.", "WizTree" },
                new[] { "recycle", "Recycle Bin", @"C:\$Recycle.Bin", "review", "Empty it once you are sure nothing in it is needed.", "Elixar" },
                new[] { "videos", "Videos folder", Path.Combine(U, "Videos"), "review", "Move large videos to an external drive or cloud.", "WizTree" },
                new[] { "desktop", "Desktop", desk, "review", "Review large files you no longer need.", "WizTree" },
                new[] { "nvdx", "NVIDIA shader cache", Path.Combine(L, "NVIDIA", "DXCache"), "safe", "Clear with Elixar (close games first).", "Elixar" },
                new[] { "stremio", "Stremio cache", Path.Combine(R, "stremio", "stremio-server", "stremio-cache"), "safe", "Clear with Elixar or set a limit in Stremio settings.", "Elixar" },
                new[] { "usertemp", "Your temp files", UserDirs.Temp.TrimEnd('\\'), "safe", "Safe to clear. Files in use are skipped automatically.", "Elixar" },
                new[] { "wintemp", "Windows temp files", @"C:\Windows\Temp", "safe", "Safe to clear.", "Elixar" },
                new[] { "wudl", "Windows Update downloads", @"C:\Windows\SoftwareDistribution\Download", "safe", "Safe after updates finished installing.", "Elixar" },
                new[] { "dosvc", "Delivery Optimization cache", @"C:\Windows\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache", "safe", "Safe to clear.", "Elixar" },
                new[] { "wer", "Error reports", @"C:\ProgramData\Microsoft\Windows\WER", "safe", "Safe to clear.", "Elixar" },
                new[] { "minidump", "Crash dumps (small)", @"C:\Windows\Minidump", "safe", "Safe unless you are investigating a crash.", "Elixar" },
                new[] { "memdump", "Crash dump (full)", @"C:\Windows\MEMORY.DMP", "safe", "Safe unless you are investigating a crash.", "Elixar" },
                new[] { "thumbs", "Thumbnail cache", Path.Combine(L, "Microsoft", "Windows", "Explorer"), "safe", "Rebuilt automatically.", "Elixar" },
                new[] { "d3d", "DirectX shader cache", Path.Combine(L, "D3DSCache"), "safe", "Games rebuild it.", "Elixar" },
                new[] { "chrome", "Chrome cache", Path.Combine(L, "Google", "Chrome", "User Data", "Default", "Cache"), "safe", "Clear with Elixar (Chrome closed).", "Elixar" },
                new[] { "edge", "Edge cache", Path.Combine(L, "Microsoft", "Edge", "User Data", "Default", "Cache"), "safe", "Clear with Elixar (Edge closed).", "Elixar" },
            };
        }

        public class BigFile { public string Path; public long Bytes; public DateTime Modified; }

        public static List<BigFile> LargestFiles(string root, long minBytes, int top, Action<string> status)
        {
            return LargestFiles(root, minBytes, top, status, CancellationToken.None);
        }

        public static List<BigFile> LargestFiles(string root, long minBytes, int top, Action<string> status, CancellationToken ct)
        {
            var big = new List<BigFile>();
            var stack = new Stack<string>();
            stack.Push(root);
            int dirs = 0;
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();
                if (++dirs % 2000 == 0 && status != null) status(dir);
                try
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var f in di.EnumerateFiles())
                    {
                        try { if (f.Length >= minBytes) big.Add(new BigFile { Path = f.FullName, Bytes = f.Length, Modified = f.LastWriteTime }); }
                        catch { }
                    }
                    foreach (var d in di.EnumerateDirectories())
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !Lx.SkipDir(d.FullName, root)) stack.Push(d.FullName);
                }
                catch { }
            }
            return big.OrderByDescending(b => b.Bytes).Take(top).ToList();
        }

        public static string Esc(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string BuildJson(string computer, bool admin, int minFileMB, long total, long free,
                                       List<string[]> locs, List<long?> locBytes, List<BigFile> files)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"tool\": \"c-drive-cleanup-scan\",\n  \"version\": 1,\n");
            sb.Append("  \"computer\": ").Append(Esc(computer)).Append(",\n");
            sb.Append("  \"scannedAt\": ").Append(Esc(DateTime.Now.ToString("s"))).Append(",\n");
            sb.Append("  \"admin\": ").Append(admin ? "true" : "false").Append(",\n");
            sb.Append("  \"minFileMB\": ").Append(minFileMB).Append(",\n");
            sb.Append("  \"drive\": { \"total\": ").Append(total).Append(", \"free\": ").Append(free).Append(" },\n");
            sb.Append("  \"locations\": [\n");
            for (int i = 0; i < locs.Count; i++)
            {
                var l = locs[i];
                long? b = locBytes[i];
                sb.Append("    { \"id\": ").Append(Esc(l[0]))
                  .Append(", \"name\": ").Append(Esc(l[1]))
                  .Append(", \"path\": ").Append(Esc(l[2]))
                  .Append(", \"tier\": ").Append(Esc(l[3]))
                  .Append(", \"bytes\": ").Append(b.HasValue ? b.Value.ToString() : "null")
                  .Append(", \"files\": 0, \"exists\": ").Append(b.HasValue ? "true" : "false")
                  .Append(", \"action\": ").Append(Esc(l[4]))
                  .Append(", \"tool\": ").Append(Esc(l[5])).Append(" }")
                  .Append(i < locs.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ],\n  \"largestFiles\": [\n");
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                string tier, reason;
                Classify(f.Path, out tier, out reason);
                sb.Append("    { \"path\": ").Append(Esc(f.Path))
                  .Append(", \"bytes\": ").Append(f.Bytes)
                  .Append(", \"modified\": ").Append(Esc(f.Modified.ToString("yyyy-MM-dd")))
                  .Append(", \"tier\": ").Append(Esc(tier))
                  .Append(", \"reason\": ").Append(Esc(reason)).Append(" }")
                  .Append(i < files.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }

        // Returns the path of the saved file.
        public static string Run(Action<string> status)
        {
            var locs = Locations().ToList();
            var bytes = new List<long?>();
            foreach (var l in locs)
            {
                if (status != null) status("Measuring " + l[1] + "...");
                bool exists = File.Exists(l[2]) || Directory.Exists(l[2]);
                bytes.Add(exists ? (long?)Fs.SizeOf(l[2]) : null);
            }
            if (status != null) status("Looking for the largest files on C: (a few minutes)...");
            var files = LargestFiles(Sys.Root, 50L << 20, 200, d => { if (status != null) status("Scanning " + d); });

            string json = BuildJson(Environment.MachineName, Sys.IsAdmin(), 50, Sys.Total(), Sys.Free(), locs, bytes, files);
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) desk = AppDomain.CurrentDomain.BaseDirectory;
            string outPath = Path.Combine(desk, "c-drive-scan.json");
            SafeFile.WriteText(outPath, json);
            return outPath;
        }
    }

    // ---------------- dashboard data ----------------
    public class DashLoc
    {
        public string Id, Name, Path, Tier, Action, Tool;
        public long? Bytes;   // measured size; null = not present
        public long Own;      // size without other listed locations inside it
        public bool OnC = true;
    }

    public static class DashData
    {
        public static readonly string[] TierOrder = { "critical", "caution", "review", "safe" };

        public static List<DashLoc> Build()
        {
            bool win = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var list = new List<DashLoc>();
            foreach (var l in Scanner.Locations())
                list.Add(new DashLoc
                {
                    Id = l[0], Name = l[1], Path = l[2], Tier = l[3], Action = l[4], Tool = l[5],
                    OnC = !win || l[2].StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)
                });
            return list;
        }

        public static void Measure(DashLoc d)
        {
            if (!d.OnC) { d.Bytes = null; return; }
            bool exists = File.Exists(d.Path) || Directory.Exists(d.Path);
            d.Bytes = exists ? (long?)Fs.SizeOf(d.Path) : null;
        }

        static string Norm(string p) { return p.TrimEnd('\\', '/').ToLowerInvariant(); }

        public static bool Inside(string child, string parent)
        {
            string c = Norm(child), pa = Norm(parent);
            return c.Length > pa.Length && c.StartsWith(pa, StringComparison.Ordinal) && (c[pa.Length] == '\\' || c[pa.Length] == '/');
        }

        // Subtract listed locations that sit inside another listed location, so nothing is counted twice.
        public static void ComputeOwn(List<DashLoc> locs)
        {
            foreach (var a in locs)
            {
                a.Own = a.Bytes ?? 0;
                foreach (var b in locs)
                {
                    if (b == a || !b.Bytes.HasValue || !Inside(b.Path, a.Path)) continue;
                    bool deeper = locs.Any(c => c != a && c != b && c.Bytes.HasValue && Inside(c.Path, a.Path) && Inside(b.Path, c.Path));
                    if (!deeper) a.Own -= b.Bytes.Value;
                }
                if (a.Own < 0) a.Own = 0;
            }
        }

        public static Dictionary<string, long> Totals(List<DashLoc> locs)
        {
            var t = TierOrder.ToDictionary(x => x, x => 0L);
            foreach (var l in locs) if (l.Bytes.HasValue && t.ContainsKey(l.Tier)) t[l.Tier] += l.Own;
            return t;
        }

        // text bar for a list cell, e.g. "||||||||       12%"
        public static string Bar(long part, long max, long whole, int width)
        {
            int n = max <= 0 ? 0 : (int)Math.Round(width * (double)part / max);
            if (part > 0 && n == 0) n = 1;
            string pct = whole <= 0 ? "" : (part * 100.0 / whole < 1 && part > 0 ? "<1%" : Math.Round(part * 100.0 / whole) + "%");
            return new string('\u2588', n) + new string(' ', Math.Max(0, width - n)) + " " + pct;
        }
    }

    public class DashSorter : System.Collections.IComparer
    {
        public int Compare(object x, object y)
        {
            var a = (DashLoc)((ListViewItem)x).Tag;
            var b = (DashLoc)((ListViewItem)y).Tag;
            long sa = a.Bytes.HasValue && a.OnC ? a.Own : -1, sb = b.Bytes.HasValue && b.OnC ? b.Own : -1;
            return sb.CompareTo(sa);
        }
    }

    // stacked bar of drive C: with a legend under it
    public class DriveBar : Control
    {
        public class Seg { public string Label; public long Bytes; public Color Color; public bool Outline; }
        public List<Seg> Segments = new List<Seg>();
        public long Total;
        public string Of = "C:";
        public int BarHeight = 26;
        readonly ToolTip tip = new ToolTip();
        string lastTip = "";

        public DriveBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var r = new Rectangle(0, 0, Width - 1, BarHeight);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = PaintedCard.Round(r, Math.Max(4, BarHeight / 3f)))
            {
                using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(62, 62, 76) : Color.FromArgb(232, 236, 242))) g.FillPath(b, path);
                var clip = g.Clip;
                g.SetClip(path);
                if (Total > 0)
                {
                    float x = r.X;
                    foreach (var sg in Segments)
                    {
                        float w = (float)(r.Width * (double)sg.Bytes / Total);
                        if (w <= 0) continue;
                        using (var b = new SolidBrush(sg.Color)) g.FillRectangle(b, RightToLeft == RightToLeft.Yes ? r.Right - x - w : x, r.Y, w, r.Height);
                        x += w;
                    }
                }
                g.Clip = clip;
                using (var p = new Pen(Theme.Grid)) g.DrawPath(p, path);
            }

            int lx = 0, ly = r.Bottom + 8, box = Math.Max(10, Font.Height - 4);
            foreach (var sg in Segments)
            {
                string text = sg.Label + "  " + Fs.Fmt(sg.Bytes);
                var size = TextRenderer.MeasureText(g, text, Font);
                if (lx > 0 && lx + box + 6 + size.Width > Width) { lx = 0; ly += size.Height + 4; }
                using (var b = new SolidBrush(sg.Color)) g.FillRectangle(b, lx, ly + (size.Height - box) / 2, box, box);
                if (sg.Outline) using (var p = new Pen(Color.Silver)) g.DrawRectangle(p, lx, ly + (size.Height - box) / 2, box, box);
                TextRenderer.DrawText(g, text, Font, new Point(lx + box + 4, ly), ForeColor);
                lx += box + 6 + size.Width + 16;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string t = "";
            if (Total > 0 && e.Y <= BarHeight)
            {
                double x = 0;
                foreach (var sg in Segments)
                {
                    double w = (Width - 1) * (double)sg.Bytes / Total;
                    if (e.X >= x && e.X < x + w) { t = sg.Label + ": " + Fs.Fmt(sg.Bytes) + " (" + Math.Round(sg.Bytes * 100.0 / Total) + "% of " + Of + ")"; break; }
                    x += w;
                }
            }
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t); }
        }
    }

    public static class Brand
    {
        // the full icon is embedded in the exe, so every size stays sharp
        public static Icon Load(int size)
        {
            try
            {
                using (var st = typeof(Brand).Assembly.GetManifestResourceStream("Elixar.app.ico"))
                    if (st != null) return new Icon(st, size, size);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; }
        }

        static Image picture;
        // 128px PNG of the logo for the brand bar
        public static Image Picture()
        {
            if (picture != null) return picture;
            try
            {
                using (var st = typeof(Brand).Assembly.GetManifestResourceStream("Elixar.brand.png"))
                    if (st != null) { using (var tmp = Image.FromStream(st)) picture = new Bitmap(tmp); }
            }
            catch { }
            return picture;
        }
    }

    // Elixar brand bar across the top of the window
    public class BrandBar : Control
    {
        public Icon AppIcon;
        public Font NameFont, TagFont;
        public BrandBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle.Width > 0 ? ClientRectangle : new Rectangle(0, 0, 1, 1),
                       Color.FromArgb(38, 22, 74), Color.FromArgb(18, 92, 104), System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                g.FillRectangle(b, ClientRectangle);
            int pad = Height / 5, x = pad + 4;
            if (AppIcon != null)
            {
                int sz = Height - 2 * pad;
                try
                {
                    var img = Brand.Picture();
                    if (img != null) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(img, new Rectangle(x, pad, sz, sz)); }
                }
                catch { }
                x += sz + pad;
            }
            var nf = NameFont ?? Font;
            var ns = TextRenderer.MeasureText(g, "Elixar", nf);
            TextRenderer.DrawText(g, "Elixar", nf, new Point(x, (Height - ns.Height) / 2), Color.White);
            var tf = TagFont ?? Font;
            string tag = Win.IsWindows ? "The cure for a full C: drive" : "The cure for a full disk";
            var ts = TextRenderer.MeasureText(g, tag, tf);
            TextRenderer.DrawText(g, tag, tf, new Point(x + ns.Width + 8, (Height - ts.Height) / 2 + 1), Color.FromArgb(190, 225, 230));
            // signature on the right
            var v = typeof(BrandBar).Assembly.GetName().Version;
            string sig = "Made by Elisar   v" + v.Major + "." + v.Minor + "." + v.Build;
            var ss = TextRenderer.MeasureText(g, sig, tf);
            int sx = Width - ss.Width - pad - 6;
            if (sx > x + ns.Width + ts.Width + 30)
                TextRenderer.DrawText(g, sig, tf, new Point(sx, (Height - ss.Height) / 2 + 1), Color.FromArgb(225, 236, 245));
        }
    }

    // one card per level: colored stripe, title, big number, short advice
    public class TierCard : Control
    {
        public Color Accent = Color.Gray;
        public string Title = "", Big = "", Sub = "";
        public Font TitleFont, BigFont;

        public TierCard()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int pad = Math.Max(10, Font.Height - 2);
            using (var path = PaintedCard.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), pad * 0.8f))
            {
                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Grid)) g.DrawPath(p, path);
            }
            var tf = TitleFont ?? Font;
            var bf = BigFont ?? Font;
            bool rtl = RightToLeft == RightToLeft.Yes;
            Color acc = Theme.MapFore(Accent);
            int y = pad, dot = Math.Max(8, tf.Height / 2);
            var dr = new Rectangle(pad, y + (tf.Height - dot) / 2, dot, dot);
            if (rtl) dr.X = Width - dr.Right;
            using (var b = new SolidBrush(acc)) g.FillEllipse(b, dr);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            TextRenderer.DrawText(g, Title, tf, new Rectangle(rtl ? pad : pad + dot + 6, y, Width - 2 * pad - dot - 6, tf.Height + 2), acc, flags);
            y += tf.Height + 4;
            TextRenderer.DrawText(g, Big, bf, new Rectangle(pad, y, Width - 2 * pad, bf.Height + 2), Theme.Ink, flags);
            y += bf.Height + 2;
            TextRenderer.DrawText(g, Sub, Font, new Rectangle(pad, y, Width - 2 * pad, Height - y - 4), Theme.Quiet, flags | TextFormatFlags.WordBreak);
        }
    }

    // ---------------- scan history for Rescan ----------------
    public class ScanSnapshot
    {
        public DateTime Time;
        public long Free;
        public Dictionary<string, long> Sizes = new Dictionary<string, long>();

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.Append("time\t").Append(Time.ToString("o")).Append('\n');
            sb.Append("free\t").Append(Free).Append('\n');
            foreach (var kv in Sizes) sb.Append("item\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            File.WriteAllText(path, sb.ToString());
        }

        public static ScanSnapshot Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var snap = new ScanSnapshot();
                bool hasTime = false;
                foreach (var line in File.ReadAllLines(path))
                {
                    var p = line.Split('\t');
                    if (p[0] == "time" && p.Length == 2) hasTime = DateTime.TryParse(p[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out snap.Time);
                    else if (p[0] == "free" && p.Length == 2) long.TryParse(p[1], out snap.Free);
                    else if (p[0] == "item" && p.Length == 3) { long v; if (long.TryParse(p[2], out v)) snap.Sizes[p[1]] = v; }
                }
                return hasTime ? snap : null;
            }
            catch { return null; }
        }

        // "-5.7 GB" when it shrank, "+12 MB" when it grew, "no change" under 1 MB
        public static string FmtDelta(long d)
        {
            if (Math.Abs(d) < (1L << 20)) return "no change";
            return (d < 0 ? "-" : "+") + Fs.Fmt(Math.Abs(d));
        }

        public static string FmtWhen(DateTime t)
        {
            return t.Date == DateTime.Now.Date ? t.ToString("HH:mm") : t.ToString("d MMM HH:mm");
        }
    }

    // ---------------- window ----------------
    public partial class MainForm : Form
    {
        readonly List<CleanItem> items;
        readonly Dictionary<CleanItem, ListViewItem> rows = new Dictionary<CleanItem, ListViewItem>();
        readonly float s;
        readonly object logLock = new object();
        readonly string logFile;
        bool ready;
        readonly bool testMode;
        public event Action MeasureDone;
        ScanSnapshot lastScan;
        readonly string snapFile;
        Label lblScan;

        // Linux: only the parts that make sense there (the self-test can ask for the Windows layout on a Linux test machine)
        public static bool ForceWindowsUi;
        bool LinuxUi { get { return Lx.On && !ForceWindowsUi; } }

        // dashboard
        public event Action DashboardDone;
        TabControl tabs;
        TabPage pageDash, pageClean;
        DriveBar driveBar;
        readonly Dictionary<string, TierCard> cards = new Dictionary<string, TierCard>();
        readonly Dictionary<string, ListViewGroup> dashGroups = new Dictionary<string, ListViewGroup>();
        readonly Dictionary<string, ListViewGroup> fileGroups = new Dictionary<string, ListViewGroup>();
        ListView dashList, filesList;
        Label dashHeader, dashStatus, dashNote, filesStatus;
        Button btnDashRefresh, btnFindFiles;
        List<DashLoc> dashLocs;
        bool dashBusy, filesBusy;
        static readonly Color CriticalColor = Color.FromArgb(199, 54, 47);
        static readonly Color OtherColor = Color.FromArgb(150, 160, 175);
        static readonly Color FreeColor = Color.FromArgb(236, 239, 244);

        ListView list;
        Label lblTop, lblInfo;
        CheckBox chkRestore;
        Button btnMeasure, btnSafe, btnNone, btnScan, btnClean, btnPlan;
        ProgressBar progress;
        TextBox logBox;

        static readonly Color SafeColor = Color.FromArgb(0, 125, 70);
        static readonly Color ReviewColor = Color.FromArgb(40, 90, 200);
        static readonly Color CautionColor = Color.FromArgb(190, 105, 0);

        int S(int px) { return (int)Math.Round(px * s); }

        public MainForm() : this(false) { }

        public MainForm(bool testMode)
        {
            this.testMode = testMode;
            items = Catalog.Build();
            logFile = Paths.FileIn("cleaner-log.txt");
            // the self-test window never reads or overwrites your saved scan
            snapFile = testMode ? null : Paths.FileIn("last-scan.txt");
            if (snapFile != null) lastScan = ScanSnapshot.Load(snapFile);

            using (var g = CreateGraphics()) s = Math.Max(1f, g.DpiX / 96f);

            Text = testMode ? "Elixar - TEST MODE (cleaning is switched off)" : LinuxUi ? "Elixar - disk care" : "Elixar - drive C: care";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(S(1120), wa.Width), Math.Min(S(880), wa.Height));
            MinimumSize = new Size(Math.Min(S(780), wa.Width), Math.Min(S(560), wa.Height));
            try { Icon = Brand.Load(32); } catch { }

            lblTop = new Label
            {
                Dock = DockStyle.Top,
                Height = S(38),
                Padding = new Padding(S(12), S(12), S(12), 0),
                Font = new Font("Segoe UI Semibold", 11f),
                Text = "Scanning..."
            };
            lblScan = new Label
            {
                Dock = DockStyle.Top,
                Height = S(26),
                Padding = new Padding(S(13), S(2), S(12), S(4)),
                ForeColor = Color.DimGray,
                Text = lastScan == null ? "First scan on this PC." : "Previous scan: " + ScanSnapshot.FmtWhen(lastScan.Time)
            };

            list = new ListView
            {
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                Dock = DockStyle.Fill
            };
            list.Columns.Add("Item", S(280));
            list.Columns.Add("Level", S(80));
            list.Columns.Add("Size", S(90), HorizontalAlignment.Right);
            list.Columns.Add("Change", S(110), HorizontalAlignment.Right);
            list.Columns.Add("Location", S(500));

            var gSafe = new ListViewGroup("Safe - rebuilt automatically", HorizontalAlignment.Left);
            var gReview = new ListViewGroup("Review - your decision", HorizontalAlignment.Left);
            var gCaution = new ListViewGroup("Caution - leftovers, read the note first", HorizontalAlignment.Left);
            var gPrivacy = new ListViewGroup("Privacy - traces of what you did (never ticked for you)", HorizontalAlignment.Left);
            list.Groups.AddRange(new[] { gSafe, gReview, gCaution, gPrivacy });
            var driveGroups = new Dictionary<string, ListViewGroup>();
            Func<string, ListViewGroup> DriveGroup = d =>
            {
                ListViewGroup g;
                if (!driveGroups.TryGetValue(d, out g)) { g = new ListViewGroup("Drive " + d + " - your other drive (nothing is ticked for you)", HorizontalAlignment.Left); driveGroups[d] = g; list.Groups.Add(g); }
                return g;
            };

            foreach (var it in items)
            {
                var lvi = new ListViewItem(Lang.T(it.Name)) { UseItemStyleForSubItems = false, Checked = it.DefaultOn, Tag = it };
                var tierSub = lvi.SubItems.Add(Lang.T(it.Tier.ToString()));
                tierSub.ForeColor = it.Tier == Tier.Safe ? SafeColor : it.Tier == Tier.Review ? ReviewColor : CautionColor;
                lvi.SubItems.Add("...");
                lvi.SubItems.Add("");
                var locSub = lvi.SubItems.Add(it.Where);
                locSub.ForeColor = Color.DimGray;
                lvi.Group = it.Drive != null ? DriveGroup(it.Drive) : it.Privacy ? gPrivacy : it.Tier == Tier.Safe ? gSafe : it.Tier == Tier.Review ? gReview : gCaution;
                if (it.Privacy) tierSub.Text = Lang.T("Privacy");
                list.Items.Add(lvi);
                rows[it] = lvi;
            }

            // bottom area
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = S(300),
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(S(10), S(6), S(10), S(10))
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, S(44)));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, S(22)));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            lblInfo = new Label { Dock = DockStyle.Fill, Text = "Click an item to see what it removes." };

            chkRestore = new CheckBox { AutoSize = true, Checked = !LinuxUi, Visible = !LinuxUi, Text = "Create a restore point first (recommended)", Margin = new Padding(S(2), S(2), 0, S(4)) };

            var btnRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            btnMeasure = MakeButton("Rescan (F5)");
            btnSafe = MakeButton("Select all Safe");
            btnNone = MakeButton("Select none");
            btnScan = MakeButton("Save scan for dashboard");
            var btnCleanApps = MakeButton("Remove apps...");
            btnCleanApps.Click += (o, e) => ShowStoreTab();
            btnPlan = MakeButton("Free space for me...");
            btnPlan.Click += (o, e) => OnPlan();
            flow.Controls.AddRange(new Control[] { btnPlan, btnMeasure, btnSafe, btnNone, btnScan, btnCleanApps });
            if (LinuxUi) { btnPlan.Visible = false; btnCleanApps.Visible = false; }   // the Dashboard's "Free ... safely" does this on Linux; apps are removed with the App Center
            btnClean = MakeButton("Clean selected");
            btnClean.Font = new Font("Segoe UI Semibold", 9.5f);
            btnClean.Padding = new Padding(S(18), S(4), S(18), S(4));
            btnClean.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnRow.Controls.Add(flow, 0, 0);
            btnRow.Controls.Add(btnClean, 1, 0);

            progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(S(2), S(6), S(2), S(2)) };

            logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = SystemColors.Window
            };

            bottom.Controls.Add(lblInfo, 0, 0);
            bottom.Controls.Add(chkRestore, 0, 1);
            bottom.Controls.Add(btnRow, 0, 2);
            bottom.Controls.Add(progress, 0, 3);
            bottom.Controls.Add(logBox, 0, 4);

            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(14), S(5)) };
            pageDash = new TabPage("Dashboard") { BackColor = Color.FromArgb(246, 248, 251) };
            pageClean = new TabPage("Cleaner");

            // docking order: fill first, then edges
            pageClean.Controls.Add(list);
            pageClean.Controls.Add(bottom);
            pageClean.Controls.Add(lblScan);
            pageClean.Controls.Add(lblTop);

            BuildDashboard();
            BuildApps();
            BuildStoreApps();
            appsTabs.TabPages.Add(pageStore);
            appsTabs.SelectedIndexChanged += (o, e) => { if (appsTabs.SelectedTab == pageStore && !storeLoaded) RefreshStoreApps(); };
            BuildAutomation();
            BuildTools();
            BuildPhone();
            BuildSettingsTab();
            BuildEmailClean();
            BuildLinuxTab();
            tabs.TabPages.Add(pageDash);
            if (!LinuxUi) tabs.TabPages.Add(pageApps);
            tabs.TabPages.Add(pageClean);
            tabs.TabPages.Add(pageTools);
            if (!LinuxUi) tabs.TabPages.Add(pagePhone);
            tabs.TabPages.Add(pageEmail);
            tabs.TabPages.Add(pageAuto);
            if (LinuxUi) tabs.TabPages.Add(pageLinux);
            tabs.TabPages.Add(pageSettings);
            bool appsLoaded = false, autoLoaded = false;
            tabs.SelectedIndexChanged += (o, e) =>
            {
                if (tabs.SelectedTab == pageApps && !appsLoaded) { appsLoaded = true; if (appsTabs.SelectedTab == pageStore) RefreshStoreApps(); else RefreshApps(); }
                if (tabs.SelectedTab == pageAuto && !autoLoaded) { autoLoaded = true; LoadAutomationState(); }
                if (tabs.SelectedTab == pageEmail) UpdateEmailUi();
                if (tabs.SelectedTab == pageDash && ready && (DateTime.Now - lastOverview).TotalSeconds > 20) RefreshOverview();
                if (tabs.SelectedTab == pageTools) OnToolsTab();
                if (tabs.SelectedTab == pagePhone) OnPhoneTab();
                if (tabs.SelectedTab == pageLinux) OnLinuxTab();
            };
            BuildNav();
            Controls.Add(tabHost);
            Controls.Add(nav);
            Controls.Add(new BrandBar
            {
                Dock = DockStyle.Top, Height = S(46), AppIcon = Icon,
                NameFont = new Font("Segoe UI Semibold", 15f), TagFont = new Font("Segoe UI", 9.5f)
            });
            var ver = typeof(MainForm).Assembly.GetName().Version;
            // footer: made by Elisar, the built-in user guide, and Verify this copy
            var footBack = Color.FromArgb(236, 233, 246); var footInk = Color.FromArgb(70, 52, 120); var footFont = new Font("Segoe UI", 8.5f);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = S(24), BackColor = footBack };
            var footRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = footBack, Margin = new Padding(0) };
            Func<string, Label> footText = t => new Label { AutoSize = true, Text = t, Font = footFont, ForeColor = footInk, Margin = new Padding(0, S(4), 0, 0) };
            Func<string, LinkLabel> footLink = t => new LinkLabel { AutoSize = true, Text = t, Font = footFont, LinkColor = footInk, ActiveLinkColor = Color.FromArgb(91, 76, 196), Margin = new Padding(0, S(4), 0, 0) };
            var lnkGuide = footLink("User guide"); var lnkVerify = footLink("Verify this copy");
            footRow.Controls.AddRange(new Control[] { footText("Made by Elisar   \u00b7   Elixar v" + ver.Major + "." + ver.Minor + "." + ver.Build + "   \u00b7   "), lnkGuide, footText("   \u00b7   "), lnkVerify });
            footer.Controls.Add(footRow);
            footer.Resize += (o, e) => footRow.Location = new Point(Math.Max(0, (footer.Width - footRow.Width) / 2), 0);
            footRow.SizeChanged += (o, e) => footRow.Location = new Point(Math.Max(0, (footer.Width - footRow.Width) / 2), 0);
            var guideMenu = new ContextMenuStrip();
            guideMenu.Items.Add("Open the user guide (F1)", null, (o, e) => OpenGuide());
            guideMenu.Items.Add("Save a copy of the guide...", null, (o, e) => SaveGuide());
            lnkGuide.LinkClicked += (o, e) => guideMenu.Show(lnkGuide, new Point(0, -guideMenu.PreferredSize.Height));
            lnkVerify.LinkClicked += (o, e) => { using (var f = new IntegrityForm(s)) { f.Shown += (o2, e2) => { Theme.Apply(f); Lang.Apply(f); }; f.ShowDialog(this); } };
            var lnkSettings = footLink("Settings");
            footRow.Controls.Add(footText("   \u00b7   ")); footRow.Controls.Add(lnkSettings);
            lnkSettings.LinkClicked += (o, e) => ShowSettings();
            Lang.Menu(guideMenu);
            lnkUpdate = footLink("Update available");
            lnkUpdate.LinkColor = Color.FromArgb(0, 125, 70); lnkUpdate.Visible = false;
            footRow.Controls.Add(lnkUpdate);
            lnkUpdate.LinkClicked += (o, e) => CheckForUpdates(true);
            Controls.Add(footer);

            KeyPreview = true;
            KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.F1) { e.Handled = true; OpenGuide(); return; }
                if (e.KeyCode != Keys.F5) return;
                e.Handled = true;
                if (tabs.SelectedTab == pageDash) RefreshCurrentDrive();
                else if (tabs.SelectedTab == pageApps) { if (appsTabs.SelectedTab == pageStore) RefreshStoreApps(); else RefreshApps(); }
                else if (tabs.SelectedTab == pageAuto) LoadAutomationState();
                else if (tabs.SelectedTab == pagePhone) DetectPhone();
                else if (tabs.SelectedTab == pageLinux) OnLinuxTab();
                else if (tabs.SelectedTab == pageTools) { if (toolsTabs.SelectedTab == pageDrivers) ScanDrivers(); else if (toolsTabs.SelectedTab == pageUndo) RefreshUndo(); }
                else if (btnMeasure.Enabled) StartMeasure(null);
            };

            list.ItemChecked += (o, e) => { if (ready) UpdateTotals(); };
            list.SelectedIndexChanged += (o, e) =>
            {
                if (list.SelectedItems.Count > 0)
                {
                    var it = (CleanItem)list.SelectedItems[0].Tag;
                    lblInfo.Text = Lang.T(it.Name) + ": " + Lang.T(it.Info);
                }
            };
            btnMeasure.Click += (o, e) => StartMeasure(null);
            btnSafe.Click += (o, e) => { foreach (ListViewItem r in list.Items) { var it = (CleanItem)r.Tag; r.Checked = it.Tier == Tier.Safe && !it.SizeUnknown; } };
            btnNone.Click += (o, e) => { foreach (ListViewItem r in list.Items) r.Checked = false; };
            btnClean.Click += (o, e) => OnClean();
            btnScan.Click += (o, e) => OnScan();

            Theme.Apply(this); Lang.Apply(this); Theme.Watch(this);
            Shown += (o, e) =>
            {
                Log("Elixar " + (LinuxUi ? "(running as you; system items ask for your password when you clean them)" : Sys.IsAdmin() ? "(running as administrator)" : "(NOT running as administrator - some items will be skipped)"));
                Log(lastScan == null ? "First scan..." : "Scanning and comparing with the scan from " + ScanSnapshot.FmtWhen(lastScan.Time) + "...");
                RefreshOverview();
                StartMeasure(() =>
                {
                    Log("Ready. Tick what you want removed and click \"Clean selected\". Nothing is deleted until you confirm.");
                    RefreshDashboard();
                    LoadAppIndex();
                    CheckForUpdates(false);
                    CheckDisksQuietly();
                    if (!testMode && Win.IsWindows && Sys.IsAdmin())
                        Task.Factory.StartNew(() => Automation.UpgradeElevatedTasks()).ContinueWith(t =>
                        { if (!t.IsFaulted && t.Result.Count > 0) UI(() => Log("Secured the " + string.Join(" and ", t.Result) + " schedule (it now starts through Elixar's protected launcher).")); });
                    try
                    {
                        int lvl = 0; string ml = GoogleAuth.SignedInEmail != null ? MailStatus.Line(out lvl) : null;
                        if (ml != null && lvl == 2) Log("Email reports: " + ml + " (Settings > Email reports)");
                    }
                    catch { }
                    if (!testMode) Task.Factory.StartNew(() => Quarantine.PurgeOld(Log));
                });
            };
        }

        LinkLabel lnkUpdate;
        bool updBusy;

        // interactive: from the Settings window or the footer link; otherwise a quiet check at most once a week
        public void CheckForUpdates(bool interactive)
        {
            if (updBusy || testMode) return;
            if (!interactive)
            {
                DateTime last;
                if (!Updates.Configured || (DateTime.TryParse(Settings.Get("LastUpdateCheck", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out last) && (DateTime.Now - last).TotalDays < 7)) return;
            }
            updBusy = true;
            Task.Factory.StartNew(() => { string err; var r = Updates.Latest(out err); return Tuple.Create(r, err); }).ContinueWith(t => UI(() =>
            {
                updBusy = false;
                Settings.Set("LastUpdateCheck", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture));
                var r = t.IsFaulted ? null : t.Result.Item1;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result.Item2;
                bool newer = r != null && r.Version != null && r.Version > Updates.Current;
                lnkUpdate.Visible = newer;
                if (newer) { lnkUpdate.Text = Lang.T("Update available") + ": " + r.Version.ToString(3); Log("Elixar " + r.Version.ToString(3) + " is available."); }
                if (!interactive) return;
                if (r == null) { Lang.Box(this, err ?? "Could not check for updates.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!newer) { Lang.Box(this, "Elixar is up to date (" + Updates.Current.ToString(3) + ").", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!Win.IsWindows)
                {
                    if (Lang.Box(this, "Elixar " + r.Version.ToString(3) + " is available (you have " + Updates.Current.ToString(3) + ").\n\nOpen the download page for the new .deb package?", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        Shell.Open(Updates.ReleasesPage);
                    return;
                }
                if (Lang.Box(this, "Elixar " + r.Version.ToString(3) + " is available (you have " + Updates.Current.ToString(3) + ").\n\n" + (r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "..." : r.Notes) +
                        "\n\nDownload and install it now? Elixar checks Elisar's signature and the file fingerprints first, then restarts.", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                updBusy = true;
                Task.Factory.StartNew(() => { string e2; string exe = Updates.Prepare(r, Log, out e2); return Tuple.Create(exe, e2); }).ContinueWith(t2 => UI(() =>
                {
                    updBusy = false;
                    string exe = t2.IsFaulted ? null : t2.Result.Item1;
                    if (exe == null)
                    {
                        string e3 = t2.IsFaulted ? t2.Exception.GetBaseException().Message : t2.Result.Item2;
                        Log("Update: " + e3);
                        if (Lang.Box(this, e3 + "\n\nOpen the download page?", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes && Updates.ReleasesPage.Length > 0)
                            try { Shell.Open(Updates.ReleasesPage); } catch { }
                        return;
                    }
                    Log("Installing the update and restarting...");
                    Updates.Launch(exe);
                    Close();
                }));
            }));
        }

        public void OpenGuide()
        {
            try { Guide.Open(); Log("Opened the user guide."); }
            catch (Exception ex) { Lang.Box(this, "Could not open the user guide: " + ex.Message + "\n\nUse \"Save a copy of the guide...\" instead.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void SaveGuide()
        {
            using (var d = new SaveFileDialog { Title = "Save the Elixar user guide", FileName = Guide.FileName, Filter = "Web page (*.html)|*.html",
                                                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Guide.SaveCopy(d.FileName);
                    Log("Saved the user guide to " + d.FileName);
                    if (Lang.Box(this, "Saved:\n" + d.FileName + "\n\nOpen it now? (It works offline, and your browser can print it or save it as PDF.)", "Elixar",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes && Win.IsWindows)
                        Shell.Open(d.FileName);
                }
                catch (Exception ex) { Lang.Box(this, "Could not save it: " + ex.Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        Button MakeButton(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(8), S(3), S(8), S(3)),
                Margin = new Padding(S(2), S(2), S(6), S(2))
            };
        }

        void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        public void Log(string m)
        {
            m = Redact.Emails(m);
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + m;
            lock (logLock)
            {
                try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine); } catch { }
            }
            if (InvokeRequired) UI(() => logBox.AppendText(line + Environment.NewLine));
            else logBox.AppendText(line + Environment.NewLine);
        }

        void SetBusy(bool busy)
        {
            foreach (Control c in new Control[] { btnMeasure, btnSafe, btnNone, btnScan, btnClean, btnPlan, list, chkRestore })
                c.Enabled = !busy;
            if (testMode) btnClean.Enabled = false;
            UseWaitCursor = busy;
        }

        void UpdateTotals()
        {
            long sum = 0;
            foreach (ListViewItem r in list.CheckedItems) { var it = (CleanItem)r.Tag; if (!it.SizeUnknown) sum += it.Size; }
            lblTop.Text = string.Format("Free on " + Sys.Main + "  {0} of {1}          Selected to clean:  {2}",
                Fs.Fmt(Sys.Free()), Fs.Fmt(Sys.Total()), Fs.Fmt(sum));
        }

        public void Rescan() { StartMeasure(null); }

        public bool ChangeColumnFilled()
        {
            return rows.Values.All(r => r.SubItems[3].Text.Length > 0);
        }

        void StartMeasure(Action after)
        {
            ready = false;
            SetBusy(true);
            var prev = lastScan;
            var watch = Stopwatch.StartNew();
            foreach (var r in rows.Values) { r.SubItems[2].Text = "scanning..."; r.SubItems[3].Text = ""; }
            lblScan.Text = "Scanning...";
            Task.Factory.StartNew(() =>
            {
                foreach (var it in items)
                {
                    long v = 0;
                    try { v = it.Measure(); }
                    catch (Exception ex) { Log("Could not measure " + it.Name + ": " + ex.Message); }
                    it.SizeUnknown = v < 0;
                    it.Size = Math.Max(0, v);
                    var row = rows[it];
                    string text = it.SizeUnknown ? "varies" : Fs.Fmt(it.Size);
                    UI(() => row.SubItems[2].Text = text);
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                ready = true;
                SetBusy(false);
                UpdateTotals();
                if (t.IsFaulted) Log("Measuring failed: " + t.Exception.GetBaseException().Message);
                ShowComparison(prev, watch.Elapsed);
                if (!testMode && !t.IsFaulted) SpaceHistory.Record(Sys.Free(), Sys.Total());
                if (after != null) after();
                if (MeasureDone != null) MeasureDone();
            }));
        }

        // ================= Dashboard tab =================
        static string TierTitle(string t)
        {
            switch (t)
            {
                case "critical": return "Critical - never delete";
                case "caution": return "Caution - only with the right tool";
                case "review": return "Review - your decision";
                default: return "Safe - rebuilt automatically";
            }
        }

        static Color TierColor(string t)
        {
            switch (t)
            {
                case "critical": return CriticalColor;
                case "caution": return CautionColor;
                case "review": return ReviewColor;
                default: return SafeColor;
            }
        }

        static string Cap(string t) { return t.Substring(0, 1).ToUpperInvariant() + t.Substring(1); }

        void BuildDashboard()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(S(7), S(10), S(7), S(6)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 0 header
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // 1 overview or details

            // header: title and status, Overview | Details, and a few tools
            var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 6, RowCount = 1, Margin = new Padding(S(5), 0, S(5), S(6)) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 5; i++) head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var titles = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            dashHeader = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Text = "Drive C:", Margin = new Padding(0) };
            statusTail = Lang.T("Waiting for the first scan...");
            dashStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = StatusText(), Margin = new Padding(S(2), 0, 0, 0) };
            titles.Controls.Add(BuildDrivePicker()); titles.Controls.Add(dashStatus);
            dashView = new Segmented { Options = new[] { Lang.T("Overview"), Lang.T("Details") }, Scale_ = s, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0, S(9), S(14), 0) };
            dashView.FitWidth();
            Func<string, Button> tool = text =>
            {
                var b = MakeButton(text);
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.White; b.FlatAppearance.BorderColor = Color.FromArgb(214, 219, 228);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 241, 255);
                b.Margin = new Padding(S(3), S(9), S(3), 0);
                return b;
            };
            btnDashRefresh = tool("Refresh (F5)");
            var btnExplore = tool("Explore");
            var btnMap = tool("Folder map");
            var btnDashApps = tool("Remove apps...");
            btnDashApps.Visible = !LinuxUi;
            head.Controls.Add(titles, 0, 0); head.Controls.Add(dashView, 1, 0); head.Controls.Add(btnDashRefresh, 2, 0);
            head.Controls.Add(btnExplore, 3, 0); head.Controls.Add(btnMap, 4, 0); head.Controls.Add(btnDashApps, 5, 0);
            btnMap.Click += (o, e) => OpenMap(CurRoot());
            btnDashApps.Click += (o, e) => ShowStoreTab();
            btnExplore.Click += (o, e) => OpenDrill(CurRoot());

            dashNote = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "", Margin = new Padding(S(1), 0, 0, S(6)) };

            driveBar = new DriveBar { Dock = DockStyle.Fill, BarHeight = S(26), BackColor = pageDash.BackColor, Margin = new Padding(0, 0, 0, S(4)) };

            var cardRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, S(8)) };
            var titleFont = new Font("Segoe UI Semibold", 9f);
            var bigFont = new Font("Segoe UI Semibold", 17f);
            var subs = new Dictionary<string, string>
            {
                { "critical", LinuxUi ? "The system and installed apps. Never delete; remove apps you don't use." : "Windows and installed apps. Never delete; uninstall apps you don't use." },
                { "caution", "Leftovers and system stores. Only with the tool named in each row." },
                { "review", LinuxUi ? "Your own files: Downloads, Videos, Trash. Your decision." : "Your own files: Downloads, Videos, Recycle Bin. Your decision." },
                { "safe", "Caches and temp files. Clean them in the Cleaner tab." }
            };
            for (int i = 0; i < 4; i++)
            {
                string t = DashData.TierOrder[i];
                cardRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                var c = new TierCard
                {
                    Dock = DockStyle.Fill, Accent = TierColor(t), Title = Lang.T(t.ToUpperInvariant()), Big = "...", Sub = Lang.T(subs[t]),
                    TitleFont = titleFont, BigFont = bigFont, ForeColor = Color.FromArgb(24, 32, 43),
                    Margin = new Padding(i == 0 ? 0 : S(5), 0, i == 3 ? 0 : S(5), 0)
                };
                cards[t] = c;
                cardRow.Controls.Add(c, i, 0);
            }

            var sec = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Text = LinuxUi ? "Everything on your system disk, from critical to safe   (double-click a folder to see what is inside)" : "Everything on C:, from critical to safe   (double-click a folder to see what is inside)", Margin = new Padding(0, S(2), 0, S(4)) };

            dashList = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, ShowItemToolTips = true };
            dashList.Columns.Add("Location", S(250));
            dashList.Columns.Add("Size", S(85), HorizontalAlignment.Right);
            dashList.Columns.Add("Share of used space", S(190));
            dashList.Columns.Add("What to do", S(380));
            dashList.Columns.Add("Path", S(360));
            foreach (var t in DashData.TierOrder)
            {
                var g = new ListViewGroup(TierTitle(t), HorizontalAlignment.Left);
                dashGroups[t] = g;
                dashList.Groups.Add(g);
            }
            var barFont = new Font("Consolas", 8f);
            dashLocs = DashData.Build();
            foreach (var t in DashData.TierOrder)
            {
                foreach (var d in dashLocs.Where(x => x.Tier == t))
                {
                    var lvi = new ListViewItem(Lang.T(d.Name)) { UseItemStyleForSubItems = false, Tag = d, Group = dashGroups[t], ToolTipText = Lang.T(d.Action) };
                    lvi.SubItems.Add("...");
                    var bar = lvi.SubItems.Add("");
                    bar.Font = barFont;
                    bar.ForeColor = TierColor(t);
                    lvi.SubItems.Add(Lang.T(d.Action));
                    var path = lvi.SubItems.Add(d.OnC ? d.Path : d.Path + "  (not on C:)");
                    path.ForeColor = Color.DimGray;
                    dashList.Items.Add(lvi);
                }
            }

            // largest files
            var fhead = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, S(8), 0, S(4)) };
            fhead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var ftitle = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            ftitle.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Text = LinuxUi ? "Largest files on your system disk" : "Largest files on C:", Margin = new Padding(0, S(4), S(10), 0) });
            filesStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Not scanned yet. Read-only; double-click a file to show it in its folder.", Margin = new Padding(0, S(6), 0, 0) };
            ftitle.Controls.Add(filesStatus);
            btnFindFiles = MakeButton("Find largest files (2-5 min)");
            fhead.Controls.Add(ftitle, 0, 0);
            fhead.Controls.Add(btnFindFiles, 1, 0);

            filesList = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, ShowItemToolTips = true };
            filesList.Columns.Add("Level", S(80));
            filesList.Columns.Add("Size", S(85), HorizontalAlignment.Right);
            filesList.Columns.Add("Modified", S(90));
            filesList.Columns.Add("File", S(520));
            filesList.Columns.Add("Why", S(420));
            foreach (var t in DashData.TierOrder)
            {
                var g = new ListViewGroup(TierTitle(t), HorizontalAlignment.Left);
                fileGroups[t] = g;
                filesList.Groups.Add(g);
            }
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show in folder", null, (o, e) => ShowSelectedFile());
            menu.Items.Add("Copy path", null, (o, e) =>
            {
                if (filesList.SelectedItems.Count == 0) return;
                try { Clipboard.SetText((string)filesList.SelectedItems[0].Tag); } catch { }
            });
            var miUninstall = new ToolStripMenuItem("Uninstall the app this file belongs to...");
            miUninstall.Click += (o, e) => { if (filesList.SelectedItems.Count > 0) UninstallOwner((string)filesList.SelectedItems[0].Tag); };
            menu.Items.Add(miUninstall);
            menu.Opening += (o, e) =>
            {
                string p = filesList.SelectedItems.Count > 0 ? (string)filesList.SelectedItems[0].Tag : null;
                string owner = p == null ? null : OwnerName(p);
                miUninstall.Enabled = owner != null && !testMode;
                miUninstall.Text = owner != null ? "Uninstall " + owner + "..." : "Uninstall app (this file does not belong to a removable app)";
            };
            filesList.ContextMenuStrip = menu;
            filesList.DoubleClick += (o, e) => ShowSelectedFile();

            // Details: the drive bar, the four levels, every location and the largest files
            var det = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = new Padding(0), Padding = new Padding(S(5), 0, S(5), 0) };
            det.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 0 note
            det.RowStyles.Add(new RowStyle(SizeType.Absolute, S(84)));   // 1 drive bar
            det.RowStyles.Add(new RowStyle(SizeType.Absolute, S(124)));  // 2 cards
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 3 section title
            det.RowStyles.Add(new RowStyle(SizeType.Percent, 58));       // 4 locations
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 5 files header
            det.RowStyles.Add(new RowStyle(SizeType.Percent, 42));       // 6 files
            det.Controls.Add(dashNote, 0, 0);
            det.Controls.Add(driveBar, 0, 1);
            det.Controls.Add(cardRow, 0, 2);
            det.Controls.Add(sec, 0, 3);
            det.Controls.Add(dashList, 0, 4);
            det.Controls.Add(fhead, 0, 5);
            det.Controls.Add(filesList, 0, 6);
            dashDetails = det;

            dashHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            dashHost.Controls.Add(dashDetails);
            dashHost.Controls.Add(BuildOverview());
            BuildDriveViews();
            root.Controls.Add(head, 0, 0);
            root.Controls.Add(dashHost, 0, 1);
            pageDash.Controls.Add(root);
            dashView.Changed += () => ShowDashView(dashView.Selected);
            ShowDashView(!testMode && Settings.Get("DashView", "overview") == "details" ? 1 : 0);

            btnDashRefresh.Click += (o, e) => RefreshCurrentDrive();
            dashList.DoubleClick += (o, e) =>
            {
                if (dashList.SelectedItems.Count == 0) return;
                var d = (DashLoc)dashList.SelectedItems[0].Tag;
                if (Directory.Exists(d.Path)) OpenDrill(d.Path);
                else if (File.Exists(d.Path)) { try { Shell.Select(d.Path); } catch { } }
            };
            btnFindFiles.Click += (o, e) => FindLargestFiles();
        }

        public DrillForm OpenDrill(string path)
        {
            var f = new DrillForm(path, s, Icon, OwnerName, p => UninstallOwner(p));
            f.Shown += (o, e) => { Theme.Apply(f); Lang.Apply(f); Theme.Watch(f); };
            f.Show(this);
            return f;
        }

        void ShowSelectedFile()
        {
            if (filesList.SelectedItems.Count == 0) return;
            string path = (string)filesList.SelectedItems[0].Tag;
            try { Shell.Select(path); }
            catch (Exception ex) { Log("Could not open the folder: " + ex.Message); }
        }

        public void RefreshDashboard()
        {
            if (dashBusy) return;
            dashBusy = true;
            btnDashRefresh.Enabled = false;
            var watch = Stopwatch.StartNew();
            foreach (ListViewItem r in dashList.Items) { r.SubItems[1].Text = "..."; r.SubItems[2].Text = ""; }
            dashStatus.Text = "Measuring...";
            Task.Factory.StartNew(() =>
            {
                foreach (var d in dashLocs)
                {
                    var loc = d;
                    UI(() => dashStatus.Text = "Measuring " + loc.Name + "...");
                    try { DashData.Measure(loc); }
                    catch (Exception ex) { loc.Bytes = null; Log("Could not measure " + loc.Name + ": " + ex.Message); }
                }
                DashData.ComputeOwn(dashLocs);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                try
                {
                    if (t.IsFaulted) Log("Dashboard failed: " + t.Exception.GetBaseException().Message);
                    FillDashboard(watch.Elapsed);
                }
                catch (Exception ex) { Log("Dashboard could not be drawn: " + ex.Message); }
                finally
                {
                    dashBusy = false;
                    btnDashRefresh.Enabled = true;
                    if (DashboardDone != null) DashboardDone();
                }
            }));
        }

        public List<string> TabNames { get { return tabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList(); } }
        public List<string> CleanerItemNames { get { return items.Select(i => i.Name).ToList(); } }

        public bool DashboardFilled()
        {
            return dashList.Items.Cast<ListViewItem>().All(r => r.SubItems[1].Text != "...") && cards.Values.All(c => c.Big != "...");
        }

        void FillDashboard(TimeSpan took)
        {
            long total = Sys.Total(), free = Sys.Free(), used = Math.Max(0, total - free);
            var totals = DashData.Totals(dashLocs);
            long known = totals.Values.Sum();
            long max = Math.Max(1, dashLocs.Where(d => d.Bytes.HasValue).Select(d => d.Own).DefaultIfEmpty(1).Max());
            long whole = used > 0 ? used : Math.Max(1, known);

            foreach (ListViewItem r in dashList.Items)
            {
                var d = (DashLoc)r.Tag;
                if (!d.OnC) { r.SubItems[1].Text = "-"; r.SubItems[2].Text = ""; continue; }
                if (!d.Bytes.HasValue) { r.SubItems[1].Text = "not present"; r.SubItems[1].ForeColor = Color.DimGray; r.SubItems[2].Text = ""; continue; }
                r.SubItems[1].ForeColor = SystemColors.WindowText;
                r.SubItems[1].Text = Fs.Fmt(d.Own);
                r.SubItems[2].Text = DashData.Bar(d.Own, max, whole, 16);
                r.ToolTipText = Lang.T(d.Action) + (d.Own != d.Bytes.Value ? "\n(" + Fs.Fmt(d.Bytes.Value) + " including the items listed separately)" : "");
            }

            // biggest first inside each level; missing locations last
            dashList.ListViewItemSorter = new DashSorter();
            dashList.Sort();

            foreach (var t in DashData.TierOrder)
            {
                dashGroups[t].Header = TierTitle(t) + "   (" + Fs.Fmt(totals[t]) + ")";
                cards[t].Big = Fs.Fmt(totals[t]);
                cards[t].Invalidate();
            }

            // drive bar: the four levels, the rest of used space, free space
            double scale = known > used && used > 0 ? (double)used / known : 1.0;
            driveBar.Total = total;
            driveBar.Segments = new List<DriveBar.Seg>();
            foreach (var t in DashData.TierOrder)
                driveBar.Segments.Add(new DriveBar.Seg { Label = Cap(t), Bytes = (long)(totals[t] * scale), Color = TierColor(t) });
            driveBar.Segments.Add(new DriveBar.Seg { Label = "Other used", Bytes = Math.Max(0, used - (long)(known * scale)), Color = OtherColor });
            driveBar.Segments.Add(new DriveBar.Seg { Label = "Free", Bytes = free, Color = FreeColor, Outline = true });
            driveBar.Invalidate();

            dashHeader.Text = Lang.T("Drive C:");
            string tookText = took.TotalSeconds < 1 ? "under 1 s" : ((int)Math.Round(took.TotalSeconds)) + " s";
            statusTail = Lang.T("Updated " + DateTime.Now.ToString("HH:mm") + " (took " + tookText + ").");
            dashStatus.Text = StatusText();
            dashNote.Text = Lang.T(total > 0 ? Fs.Fmt(free) + " free of " + Fs.Fmt(total) + "." : "") + (Sys.IsAdmin() ? "" : "  " + Lang.T("Not running as administrator: some Windows folders may show smaller than they are.")) +
                "  " + Lang.T("WinSxS looks bigger than it is because many of its files are shared with System32.");
            RefreshOverview();
            Log("Dashboard updated: critical " + Fs.Fmt(totals["critical"]) + ", caution " + Fs.Fmt(totals["caution"]) +
                ", review " + Fs.Fmt(totals["review"]) + ", safe " + Fs.Fmt(totals["safe"]) + ".");
        }

        void FindLargestFiles()
        {
            if (filesBusy) return;
            filesBusy = true;
            btnFindFiles.Enabled = false;
            filesList.Items.Clear();
            filesStatus.Text = "Scanning " + Sys.Main + " ...";
            var watch = Stopwatch.StartNew();
            string last = "";
            Task.Factory.StartNew(() => Scanner.LargestFiles(Sys.Root, 50L << 20, 200, d =>
            {
                if (d == last) return;
                last = d;
                UI(() => filesStatus.Text = "Scanning " + (d.Length > 90 ? d.Substring(0, 87) + "..." : d));
            }), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                filesBusy = false;
                btnFindFiles.Enabled = true;
                if (t.IsFaulted) { filesStatus.Text = "Scan failed: " + t.Exception.GetBaseException().Message; return; }
                var rows = new List<Tuple<string, Scanner.BigFile, string>>();
                foreach (var f in t.Result) { string tier, reason; Scanner.Classify(f.Path, out tier, out reason); rows.Add(Tuple.Create(tier, f, reason)); }
                filesList.BeginUpdate();
                foreach (var tier in DashData.TierOrder)
                {
                    foreach (var row in rows.Where(x => x.Item1 == tier).OrderByDescending(x => x.Item2.Bytes))
                    {
                        var f = row.Item2;
                        var lvi = new ListViewItem(Cap(tier)) { UseItemStyleForSubItems = false, Tag = f.Path, Group = fileGroups[tier], ToolTipText = row.Item3 };
                        lvi.ForeColor = TierColor(tier);
                        lvi.SubItems.Add(Fs.Fmt(f.Bytes));
                        lvi.SubItems.Add(f.Modified.ToString("yyyy-MM-dd"));
                        lvi.SubItems.Add(f.Path);
                        var why = lvi.SubItems.Add(row.Item3);
                        why.ForeColor = Color.DimGray;
                        filesList.Items.Add(lvi);
                    }
                    int n = rows.Count(x => x.Item1 == tier);
                    fileGroups[tier].Header = TierTitle(tier) + "   (" + n + " files, " + Fs.Fmt(rows.Where(x => x.Item1 == tier).Sum(x => x.Item2.Bytes)) + ")";
                }
                filesList.EndUpdate();
                filesStatus.Text = rows.Count + " files over 50 MB, found in " + (int)Math.Round(watch.Elapsed.TotalSeconds) + " s. Read-only; double-click a file to show it in its folder.";
                Log("Largest files: " + rows.Count + " files over 50 MB listed on the Dashboard.");
            }));
        }

        void ShowComparison(ScanSnapshot prev, TimeSpan took)
        {
            var now = new ScanSnapshot { Time = DateTime.Now, Free = Sys.Free() };
            var changed = new List<string>();
            foreach (var it in items)
            {
                var cell = rows[it].SubItems[3];
                if (it.SizeUnknown) { cell.Text = "-"; cell.ForeColor = Color.DimGray; continue; }
                now.Sizes[it.Name] = it.Size;
                long before;
                if (prev == null || !prev.Sizes.TryGetValue(it.Name, out before)) { cell.Text = "-"; cell.ForeColor = Color.DimGray; continue; }
                long d = it.Size - before;
                cell.Text = ScanSnapshot.FmtDelta(d);
                cell.ForeColor = Math.Abs(d) < (1L << 20) ? Color.DimGray : d < 0 ? SafeColor : CautionColor;
                if (Math.Abs(d) >= (1L << 20)) changed.Add("  " + it.Name + ": " + ScanSnapshot.FmtDelta(d));
            }

            string tookText = took.TotalSeconds < 1 ? "under 1 s" : ((int)Math.Round(took.TotalSeconds)) + " s";
            if (prev == null)
            {
                lblScan.Text = "Scanned at " + now.Time.ToString("HH:mm:ss") + " (took " + tookText + ").  The Change column fills in from your next rescan.";
                Log("Scan done in " + tookText + ".");
            }
            else
            {
                long fd = now.Free - prev.Free;
                string freeText = Math.Abs(fd) < (1L << 20) ? "free space unchanged" : "free space " + ScanSnapshot.FmtDelta(fd);
                lblScan.Text = "Scanned at " + now.Time.ToString("HH:mm:ss") + " (took " + tookText + ").  Compared with " +
                               ScanSnapshot.FmtWhen(prev.Time) + ": " + freeText + ".";
                Log("Rescan done in " + tookText + ". Since " + ScanSnapshot.FmtWhen(prev.Time) + ": " + freeText + ".");
                if (changed.Count > 0) { Log("Changed since then:"); foreach (var c in changed) Log(c); }
                else Log("No item changed by 1 MB or more.");
            }

            lastScan = now;
            if (snapFile != null) { try { now.Save(snapFile); } catch (Exception ex) { Log("Could not save the scan for next time: " + ex.Message); } }
        }

        void OnClean()
        {
            var sel = list.CheckedItems.Cast<ListViewItem>().Select(r => (CleanItem)r.Tag).ToList();
            if (sel.Count == 0)
            {
                Lang.Box(this, "Tick at least one item to clean.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CleanItems(sel);
        }

        // the one way Elixar cleans from the window: the Cleaner tab and the Dashboard both come here, and it always asks first
        void CleanItems(List<CleanItem> sel)
        {
            if (testMode || cleaningNow || !ready || sel.Count == 0) return;

            long est = sel.Where(i => !i.SizeUnknown).Sum(i => i.Size);
            var lines = sel.Select(i => "  - " + i.Name + "  (" + (i.SizeUnknown ? "varies" : Fs.Fmt(i.Size)) + ")");
            string msg = "Delete these " + sel.Count + " items?\n\n" + string.Join("\n", lines) +
                         "\n\nEstimated space freed: " + Fs.Fmt(est) + "\n\nThis cannot be undone.";
            if (Lang.Box(this, msg, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            // apps that must be closed first
            var toRun = new List<CleanItem>();
            var toKill = new List<Process>();
            foreach (var it in sel)
            {
                if (it.Procs.Length > 0)
                {
                    var running = Sys.FindProcesses(it.Procs);
                    if (running.Count > 0)
                    {
                        string names = string.Join(", ", running.Select(p => { try { return p.ProcessName; } catch { return "?"; } }).Distinct());
                        var ans = Lang.Box(this,
                            it.Name + ": " + names + " is running.\n\nYes = close it now and clean\nNo = skip this item\nCancel = stop",
                            "Elixar", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (ans == DialogResult.Yes) toKill.AddRange(running);
                        else if (ans == DialogResult.No) { Log("Skipped " + it.Name + " (" + names + " was running)."); continue; }
                        else { Log("Stopped by you. Nothing was deleted."); return; }
                    }
                }
                toRun.Add(it);
            }
            if (toRun.Count == 0) return;

            bool restore = chkRestore.Checked;
            SetBusy(true);
            cleaningNow = true;
            RefreshOverview();
            progress.Maximum = toRun.Count;
            progress.Value = 0;

            Task.Factory.StartNew(() =>
            {
                foreach (var p in toKill) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                if (toKill.Count > 0) Thread.Sleep(1500);

                if (restore)
                {
                    Log("Creating a restore point (can take a minute)...");
                    string r;
                    try { r = Sys.RunPowerShell(
                        "try { Checkpoint-Computer -Description 'Before Elixar' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue; " +
                        "if ($w) { 'Not created: ' + ($w -join ' ') } else { 'Restore point created.' } } " +
                        "catch { 'Not created (System Protection may be off): ' + $_.Exception.Message }", 5 * 60 * 1000); }
                    catch (Exception ex) { r = "Not created: " + ex.Message; }
                    Log("  " + r);
                }

                long start = Sys.Free();
                var asRoot = toRun.Where(i => i.RootScript != null).ToList();
                if (asRoot.Count > 0)
                {
                    Log("Asking for your password for: " + string.Join(", ", asRoot.Select(i => i.Name)) + "...");
                    long before = Sys.Free();
                    string script = string.Join("\n", asRoot.Select(i => i.RootScript));
                    string outp;
                    int code = Lx.RunAsRoot(script, out outp);
                    if (code == 126 || code == 127) Log("  Not done: the password wasn't entered.");
                    else if (code != 0) Log("  Finished with a problem (" + code + "): " + (outp.Length > 300 ? outp.Substring(0, 300) + "..." : outp));
                    Log("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - before)));
                    foreach (var it in asRoot) UI(() => progress.Value = Math.Min(progress.Maximum, progress.Value + 1));
                }
                foreach (var it in toRun.Where(i => i.RootScript == null || i.Clean != null))
                {
                    if (it.Clean == null) continue;
                    Log("Cleaning " + it.Name + "...");
                    long before = Sys.Free();
                    try { it.Clean(Log); }
                    catch (Exception ex) { Log("  Problem: " + ex.Message); }
                    Log("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - before)));
                    UI(() => progress.Value = Math.Min(progress.Maximum, progress.Value + 1));
                }
                long total = Math.Max(0, Sys.Free() - start);
                Log("Done. Total freed: " + Fs.Fmt(total));
                return total;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                long total = t.IsFaulted ? 0 : t.Result;
                if (t.IsFaulted) Log("Cleaning stopped with an error: " + t.Exception.GetBaseException().Message);
                cleaningNow = false;
                Activity.Add("clean", "Cleaned " + (toRun.Count == 1 ? toRun[0].Name : toRun.Count + " items") + ", freed " + Fs.Fmt(total), total);
                SetBusy(false);
                StartMeasure(() =>
                {
                    RefreshDashboard();
                    Lang.Box(this, "Done. Freed " + Fs.Fmt(total) + " on C:.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            }));
        }

        void OnScan()
        {
            var ok = Lang.Box(this,
                "This reads drive C: and saves c-drive-scan.json to your Desktop for the Elixar Drive Map dashboard.\n\nIt only reads; nothing is deleted. It takes a few minutes.",
                "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (ok != DialogResult.OK) return;

            SetBusy(true);
            progress.Style = ProgressBarStyle.Marquee;
            Log("Scan for dashboard started (read-only)...");
            string lastStatus = "";
            Task.Factory.StartNew(() => Scanner.Run(st =>
            {
                if (st == lastStatus) return;
                lastStatus = st;
                UI(() => lblTop.Text = st.Length > 110 ? st.Substring(0, 107) + "..." : st);
            }), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                progress.Style = ProgressBarStyle.Blocks;
                SetBusy(false);
                UpdateTotals();
                if (t.IsFaulted)
                {
                    Log("Scan failed: " + t.Exception.GetBaseException().Message);
                    Lang.Box(this, "The scan failed:\n\n" + t.Exception.GetBaseException().Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Log("Scan saved: " + t.Result);
                    Lang.Box(this, "Saved:\n" + t.Result + "\n\nOpen the Elixar Drive Map dashboard and drop this file on it.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }));
        }
    }

    // ---------------- built-in self-test ----------------
    // Only creates and deletes files inside its own folder under %TEMP%.
    // Real cleanup items are only MEASURED (read-only). The window opens with cleaning switched off.
    public static class SelfTest
    {
        public static int Run(bool showResult)
        {
            var rep = new StringBuilder();
            int fails = 0;
            if (Lx.On) Catalog.WindowsOnLinux = true;   // sections 1-4 check Windows' lists and logic; 4L checks Linux
            var failed = new List<string>();
            Action<bool, string> check = (ok, what) => { rep.AppendLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) { fails++; failed.Add(what); } };
            rep.AppendLine("Elixar self-test  " + DateTime.Now.ToString("s") + "   (Elixar " + typeof(SelfTest).Assembly.GetName().Version + ", made by Elisar)");
            rep.AppendLine("System: " + OsInfo.Current.Name + "   (" + Environment.OSVersion + ")   .NET: " + Environment.Version + "   64-bit process: " + Environment.Is64BitProcess + "   admin: " + Sys.IsAdmin());
            rep.AppendLine();

            string root = Path.Combine(UserDirs.Temp, "CDC-SelfTest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                // 1. delete logic, in a throwaway folder
                try {
                string cache = Path.Combine(root, "cache");
                Directory.CreateDirectory(Path.Combine(cache, "sub", "deep"));
                File.WriteAllBytes(Path.Combine(cache, "a.bin"), new byte[4000]);
                File.WriteAllBytes(Path.Combine(cache, "sub", "deep", "b.bin"), new byte[3000]);
                string ro = Path.Combine(cache, "sub", "readonly.bin");
                File.WriteAllBytes(ro, new byte[1000]);
                File.SetAttributes(ro, FileAttributes.ReadOnly);
                string outside = Path.Combine(root, "outside");
                Directory.CreateDirectory(outside);
                File.WriteAllBytes(Path.Combine(outside, "keep.bin"), new byte[500]);
                string link = Path.Combine(cache, "link");
                bool linked = false;
                try
                {
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                        Sys.Run("cmd.exe", "/c mklink /J \"" + link + "\" \"" + outside + "\"", 15000);
                    else
                        Sys.Run("ln", "-s \"" + outside + "\" \"" + link + "\"", 15000);
                    linked = Directory.Exists(link);
                }
                catch { }

                check(Fs.MeasureIn(cache) == 8000, "Measuring a folder (got " + Fs.MeasureIn(cache) + " bytes, expected 8000)");
                string logs = Path.Combine(root, "logs");
                Directory.CreateDirectory(logs);
                File.WriteAllBytes(Path.Combine(logs, "CbsPersist_1.log"), new byte[2000]);
                File.WriteAllBytes(Path.Combine(logs, "CBS.log"), new byte[10]);
                check(Fs.MeasureIn(logs, "CbsPersist_*") == 2000, "Measuring with a name pattern");
                Fs.ClearIn(logs, "CbsPersist_*");
                check(!File.Exists(Path.Combine(logs, "CbsPersist_1.log")) && File.Exists(Path.Combine(logs, "CBS.log")), "Pattern delete removes only matching files");
                Fs.ClearIn(cache);
                check(Directory.Exists(cache) && Directory.GetFileSystemEntries(cache).Length == 0, "Emptying a folder (including a read-only file) keeps the folder itself");
                check(File.Exists(Path.Combine(outside, "keep.bin")), "A linked folder's contents are never deleted" + (linked ? "" : " (link could not be created; skipped)"));
                string gc = Path.Combine(root, "$GetCurrent");
                Directory.CreateDirectory(Path.Combine(gc, "media"));
                File.WriteAllBytes(Path.Combine(gc, "media", "x.esd"), new byte[100]);
                Fs.Delete(gc);
                check(!Directory.Exists(gc), "Deleting a whole folder");

                } catch (Exception ex) { check(false, "Delete tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 2. every real cleanup item: build and MEASURE only
                try {
                rep.AppendLine();
                rep.AppendLine("Cleanup items on this PC (measured only, nothing deleted):");
                List<CleanItem> items = null;
                try { items = Catalog.Build(); } catch (Exception ex) { check(false, "Building the list of items: " + ex.Message); }
                if (items != null)
                {
                    check(items.Count == 43 && items.Where(i => i.Privacy).All(i => !i.DefaultOn && i.Tier == Tier.Review), "List has 43 items; privacy items are never ticked for you");
                    bool allOk = true;
                    foreach (var it in items)
                    {
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            long v = it.Measure();
                            rep.AppendLine(string.Format("      {0,-42} {1,10}   ({2} ms)", it.Name, Fs.Fmt(v), sw.ElapsedMilliseconds));
                        }
                        catch (Exception ex) { allOk = false; rep.AppendLine("      " + it.Name + "  ERROR: " + ex.Message); }
                    }
                    check(allOk, "Every item measures without errors");
                }
                rep.AppendLine();

                } catch (Exception ex) { check(false, "Measuring stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3. classification + dashboard file
                try {
                string t, r;
                Scanner.Classify(@"C:\Windows\System32\drivers\x.sys", out t, out r); check(t == "critical", "System32 is rated critical");
                Scanner.Classify(@"C:\ProgramData\Adobe\ARM\Reader_25\x.msp", out t, out r); check(t == "caution", "Adobe update cache is rated caution");
                Scanner.Classify(@"C:\Users\x\AppData\Local\NVIDIA\DXCache\a.nvph", out t, out r); check(t == "safe", "NVIDIA cache is rated safe");
                try
                {
                    var locs = new List<string[]> { new[] { "x", "Name \"q\"", @"C:\a\b", "safe", "act", "tool" } };
                    var json = Scanner.BuildJson("PC", true, 50, 100, 40, locs, new List<long?> { null },
                        new List<Scanner.BigFile> { new Scanner.BigFile { Path = @"C:\pagefile.sys", Bytes = 2147483648L, Modified = DateTime.Now } });
                    var d = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
                    var l0 = (Dictionary<string, object>)((object[])d["locations"])[0];
                    var f0 = (Dictionary<string, object>)((object[])d["largestFiles"])[0];
                    check((string)l0["path"] == @"C:\a\b" && l0["bytes"] == null && (string)f0["tier"] == "critical", "Dashboard file is valid JSON in the right format");
                }
                catch (Exception ex) { check(false, "Dashboard file: " + ex.Message); }

                } catch (Exception ex) { check(false, "Rating tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3a. dashboard math
                try {
                    char sep = Path.DirectorySeparatorChar;
                    string pd = Path.Combine(root, "ProgramData");
                    var locs = new List<DashLoc>
                    {
                        new DashLoc { Name = "ProgramData", Path = pd, Tier = "critical", Bytes = 10000 },
                        new DashLoc { Name = "WER", Path = Path.Combine(pd, "WER"), Tier = "safe", Bytes = 3000 },
                        new DashLoc { Name = "Adobe", Path = Path.Combine(pd, "Adobe" + sep + "ARM"), Tier = "caution", Bytes = 2000 },
                        new DashLoc { Name = "Missing", Path = Path.Combine(root, "x"), Tier = "review", Bytes = null }
                    };
                    DashData.ComputeOwn(locs);
                    var tot = DashData.Totals(locs);
                    check(locs[0].Own == 5000 && tot["critical"] == 5000 && tot["safe"] == 3000 && tot["caution"] == 2000 && tot["review"] == 0,
                          "Dashboard never counts a folder twice");
                    check(!DashData.Inside(pd + "2", pd) && DashData.Inside(Path.Combine(pd, "WER"), pd), "Folder nesting check");
                } catch (Exception ex) { check(false, "Dashboard math stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3b. rescan memory
                try {
                    string sf = Path.Combine(root, "last-scan.txt");
                    var a = new ScanSnapshot { Time = new DateTime(2026, 10, 16, 19, 30, 0), Free = 43396870144L };
                    a.Sizes["Windows upgrade media ($GetCurrent)"] = 6120000000L;
                    a.Save(sf);
                    var b = ScanSnapshot.Load(sf);
                    check(b != null && b.Free == a.Free && b.Time == a.Time && b.Sizes["Windows upgrade media ($GetCurrent)"] == 6120000000L, "Rescan remembers the previous scan");
                    check(ScanSnapshot.FmtDelta(-6120000000L) == "-5.7 GB" && ScanSnapshot.FmtDelta(12L << 20) == "+12 MB" && ScanSnapshot.FmtDelta(1000) == "no change", "Change column text");
                    check(ScanSnapshot.Load(Path.Combine(root, "nope.txt")) == null, "Missing scan file is handled");
                } catch (Exception ex) { check(false, "Rescan tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3c. apps
                bool win = Environment.OSVersion.Platform == PlatformID.Win32NT;
                try {
                    Func<object[], Dictionary<string, object>> V = kv => { var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase); for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return d; };
                    var game = AppsData.FromValues(V(new object[] { "DisplayName", "Epic Games Launcher", "UninstallString", "MsiExec.exe /X{ABC}", "EstimatedSize", 1048576, "InstallDate", "20240315", "InstallLocation", "\"C:\\Program Files (x86)\\Epic Games\\\"" }), "k");
                    check(game != null && game.ReportedBytes == 1073741824L && game.Installed == new DateTime(2024, 3, 15) && game.Location == @"C:\Program Files (x86)\Epic Games",
                          "Apps: reads name, size, install date and folder");
                    check(AppsData.FromValues(V(new object[] { "DisplayName", "Hidden", "UninstallString", "x", "SystemComponent", 1 }), "k") == null &&
                          AppsData.FromValues(V(new object[] { "DisplayName", "KB123 update", "UninstallString", "x", "ParentKeyName", "Office" }), "k") == null &&
                          AppsData.FromValues(V(new object[] { "DisplayName", "No uninstaller" }), "k") == null,
                          "Apps: hides system parts, updates and entries without an uninstaller");
                    check(AppsData.UninstallCommand("\"C:\\Program Files\\App\\uninst.exe\" /S").Arguments == "/s /c \"\"C:\\Program Files\\App\\uninst.exe\" /S\"",
                          "Apps: uninstall command keeps its quotes");
                    check(!AppsData.SafeToMeasure(@"C:\") && !AppsData.SafeToMeasure(@"C:\Program Files") && !AppsData.SafeToMeasure(@"C:\Windows\System32") &&
                          AppsData.SafeToMeasure(@"C:\Program Files\Steam") && !AppsData.SafeToMeasure(@"D:\Games"),
                          "Apps: only measures real app folders on C:");
                    check(AppsData.Dedupe(new[] { game, game }).Count == 1, "Apps: the same app listed twice is shown once");
                    if (win)
                    {
                        var installed = AppsData.ReadInstalled();
                        check(installed.Count > 0, "Apps: found " + installed.Count + " installed apps on this PC");
                    }
                } catch (Exception ex) { check(false, "Apps tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3d. folder drill-down
                try {
                    string dr = Path.Combine(root, "drill");
                    Directory.CreateDirectory(Path.Combine(dr, "big", "inner"));
                    Directory.CreateDirectory(Path.Combine(dr, "small"));
                    File.WriteAllBytes(Path.Combine(dr, "big", "inner", "a.bin"), new byte[300000]);
                    File.WriteAllBytes(Path.Combine(dr, "small", "b.bin"), new byte[1000]);
                    using (var fs = File.Create(Path.Combine(dr, "huge.iso"))) fs.SetLength(12L << 20);
                    File.WriteAllBytes(Path.Combine(dr, "note.txt"), new byte[500]);
                    var entries = DrillData.List(dr);
                    foreach (var e in entries.Where(x => x.IsFolder)) e.Bytes = DrillData.SizeOf(e.Path, System.Threading.CancellationToken.None);
                    var big = entries.FirstOrDefault(x => x.Name == "big");
                    var iso = entries.FirstOrDefault(x => x.Name == "huge.iso");
                    var grp = entries.FirstOrDefault(x => x.IsGroup);
                    check(entries.Count == 4 && big != null && big.Bytes == 300000 && iso != null && iso.Bytes == (12L << 20) && grp != null && grp.Count == 1 && grp.Bytes == 500,
                          "Drill-down: lists folders, big files and a row for small files with correct sizes");
                } catch (Exception ex) { check(false, "Drill-down tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3e. alert and weekly clean (nothing is scheduled by the self-test)
                try {
                    check(Automation.ShouldWarn(10, 100, 15) && !Automation.ShouldWarn(20, 100, 15) && !Automation.ShouldWarn(5, 0, 15), "Alert: warns only under the limit");
                    string ca = Automation.CreateCheckArgs(@"C:\Users\E\Downloads\Elixar\Elixar.exe", 15, "12:00");
                    check(ca == "/Create /F /TN \"Elixar\\Low space check\" /TR \"\\\"C:\\Users\\E\\Downloads\\Elixar\\Elixar.exe\\\" --check-space 15\" /SC DAILY /ST 12:00",
                          "Alert: scheduled task command is quoted correctly");
                    check(Automation.CreateCleanArgs("C:\\x.exe", "Sunday", "20:00").EndsWith("/SC WEEKLY /D SUN /ST 20:00 /RL HIGHEST"), "Weekly clean: runs weekly with admin rights");
                    check(Automation.PercentFromArgs("--check-space 20", 15) == 20 && Automation.PercentFromArgs("", 15) == 15, "Alert: reads the saved limit back");
                    check(Automation.CheckTask == "Elixar\\Low space check" && Automation.OldTasks.Length == 3 &&
                          Automation.DeleteArgs(Automation.OldTasks[2]) == "/Delete /F /TN \"C Drive Cleaner\\Weekly clean\"",
                          "Rename: schedules use the Elixar folder and old C Drive Cleaner schedules can be removed");
                    var xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><CalendarTrigger><StartBoundary>2026-10-18T20:00:00</StartBoundary><ScheduleByWeek><DaysOfWeek><Sunday /></DaysOfWeek><WeeksInterval>1</WeeksInterval></ScheduleByWeek></CalendarTrigger></Triggers><Settings><Enabled>true</Enabled></Settings><Actions><Exec><Command>C:\\x.exe</Command><Arguments>--auto-clean</Arguments></Exec></Actions></Task>";
                    var ts = Automation.ParseTaskXml(xml);
                    check(ts.Exists && ts.Day == "Sunday" && ts.Start.HasValue && ts.Start.Value.Hour == 20 && ts.Arguments == "--auto-clean" && ts.Enabled, "Weekly clean: reads the saved schedule back");
                    var auto = Automation.AutoItems();
                    check(auto.Count > 0 && auto.All(i => i.Tier == Tier.Safe && i.DefaultOn) && !auto.Any(i => i.Name.Contains("DISM") || i.Name.Contains("Recycle")),
                          "Weekly clean: only default Safe items, never Caution, Recycle Bin or DISM");
                    string hf = Path.Combine(root, "hist.txt");
                    File.WriteAllText(hf, "2026-10-18T20:00:05\t1288490188\nbad line\n");
                    var h = Automation.ReadHistory(hf);
                    check(h.Count == 1 && h[0].Item2 == 1288490188L, "Weekly clean: history is read correctly");
                } catch (Exception ex) { check(false, "Automation tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3f. built-in & Store apps (nothing is removed by the self-test)
                try {
                    Func<string, string, bool, bool, string> L = (name, sig, fw, nr) =>
                    {
                        var a = new StoreApp { Name = name, Signature = sig, Framework = fw, NonRemovable = nr };
                        StoreData.Classify(a);
                        return a.Level;
                    };
                    check(L("Microsoft.Copilot", "Store", false, false) == "optional" && L("Microsoft.Windows.Ai.Copilot.Provider", "Store", false, false) == "optional" &&
                          L("Microsoft.BingNews", "Store", false, false) == "optional" && L("king.com.CandyCrushSaga", "Store", false, false) == "optional",
                          "Store apps: Copilot, News and preinstalled games are Optional");
                    check(L("Microsoft.GamingApp", "Store", false, false) == "review" && L("Microsoft.XboxIdentityProvider", "Store", false, false) == "review" &&
                          L("5319275A.WhatsAppDesktop", "Store", false, false) == "review",
                          "Store apps: Xbox and apps you installed are Your decision");
                    check(L("Microsoft.WindowsStore", "Store", false, false) == "keep" && L("Microsoft.SecHealthUI", "Store", false, false) == "keep" &&
                          L("Microsoft.VCLibs.140.00", "Store", true, false) == "keep" && L("Microsoft.LanguageExperiencePackhe-IL", "Store", false, false) == "keep" &&
                          L("Microsoft.HEVCVideoExtension", "Store", false, false) == "keep" && L("NVIDIACorp.NVIDIAControlPanel", "Store", false, false) == "keep" &&
                          L("Some.Unknown", "System", false, false) == "keep" && L("Microsoft.Copilot", "System", false, false) == "keep" && L("Microsoft.Copilot", "Store", false, true) == "keep",
                          "Store apps: the Store, security, codecs, language packs, drivers and Windows parts are locked");
                    var parsed = StoreData.ParseList("Microsoft.Copilot\tMicrosoft.Copilot_1.0_x64__8wekyb3d8bbwe\t1.0\tCN=Microsoft\tFalse\tFalse\tStore\tC:\\Program Files\\WindowsApps\\Microsoft.Copilot_1.0_x64__8wekyb3d8bbwe\r\n" +
                                                    "Microsoft.Copilot\tMicrosoft.Copilot_1.0_arm64__8wekyb3d8bbwe\t1.0\tCN=Microsoft\tFalse\tFalse\tStore\tC:\\x\r\n" +
                                                    "Microsoft.VCLibs.140.00\tMicrosoft.VCLibs_x\t14\tCN=Microsoft\tTrue\tFalse\tStore\tC:\\y\r\nbad line\r\n");
                    check(parsed.Count == 2 && parsed[0].Friendly == "Copilot" && parsed[0].Level == "optional" && parsed[1].Level == "keep",
                          "Store apps: the app list is read correctly");
                    check(StoreData.RemoveScript("Microsoft.BingWeather_4.53.0.0_x64__8wekyb3d8bbwe").Contains("-Package 'Microsoft.BingWeather_4.53.0.0_x64__8wekyb3d8bbwe'") &&
                          !StoreData.RemoveScript("A.B_1.0_x64__x'y").Contains("Remove-AppxPackage") && !StoreData.RemoveScript("A\u2019; Remove-Item C:\\ -Recurse #").Contains("Remove-AppxPackage") &&
                          Sys.PsQuote("a'b\u2019c") == "'a''b\u2019\u2019c'",
                          "Store apps: the remove command takes only a real package name, quoted safely (curly quotes too)");
                    {
                        string lt = Install.LauncherText;
                        check(lt.Contains("set \"COR_ENABLE_PROFILING=\"") && lt.Contains("set \"COR_PROFILER_PATH=\"") && lt.Contains("set \"COMPLUS_INSTALLROOT=\"") &&
                              lt.Contains("\r\n\"%~dp0Elixar.exe\" %*\r\n") && !lt.Contains("\r\nstart ") && !lt.Contains("%PATH%") &&
                              Install.LauncherArgs("--auto-clean").StartsWith("/d /c \"\"") &&
                              (!win || (Automation.CreateCleanArgs(Install.Exe, "Sunday", "20:00").Contains("cmd.exe\\\" /d /c") && Automation.CreateMailArgs(Install.Exe, "09:00").Contains("--mail-daily"))) &&
                              !Install.IsSafeElevatedTask(Install.Exe, "--auto-clean", "--auto-clean") == win,
                              "Schedules with admin rights start through a fixed launcher that clears the variables that could make .NET load someone else's code" + (win ? "" : " (launcher text checked)"));
                        check(Sys.PsSafe("Get-Date").StartsWith("$env:PSModulePath = Join-Path $PSHOME 'Modules'; ") && StoreData.RunPsScriptFor("x").StartsWith("$env:PSModulePath"),
                              "PowerShell as administrator uses only Windows' own modules (not ones planted in Documents)");
                        if (win)
                        {
                            UserDirs.ProfileOverride = @"C:\Users\elisar";
                            try
                            {
                                check(UserDirs.Checked(@"C:\Users\elisar\AppData\Local\Temp", @"AppData\Local\Temp") == @"C:\Users\elisar\AppData\Local\Temp" &&
                                      UserDirs.Checked(@"C:\Windows\Installer", @"AppData\Local\Temp") == @"C:\Users\elisar\AppData\Local\Temp" &&
                                      UserDirs.Checked(@"C:\Users\elisar\..\..\Program Files", @"AppData\Local") == @"C:\Users\elisar\AppData\Local" &&
                                      UserDirs.Checked(@"C:\Users\elisar2\AppData\Local", @"AppData\Local") == @"C:\Users\elisar\AppData\Local",
                                      "Security: a %TEMP% or AppData redirected outside your profile (e.g. to C:\\Windows\\Installer) is never cleaned as administrator");
                            }
                            finally { UserDirs.ProfileOverride = null; }
                            check(UserDirs.Profile != null && Directory.Exists(UserDirs.Profile), "Security: your real profile folder is read from Windows' own record (HKLM)");
                            check(Win.ProgramFiles.Length > 3 && Directory.Exists(Win.ProgramFiles) && Install.Dir.StartsWith(Win.ProgramFiles), "Security: Program Files comes from Windows' own record, not from a variable any program can change");
                        }
                    }
                    check(!win || Install.DllSearchHardened, "Security: DLLs load only from System32, never from the folder Elixar.exe sits in (like Downloads)" + (win ? "" : " (Windows only: not tried)"));
                    var progs = new List<AppEntry> { new AppEntry { Name = "Epic Games Launcher", Location = @"C:\Program Files (x86)\Epic Games", Uninstall = "x" } };
                    var store = new List<StoreApp> { new StoreApp { Name = "Microsoft.Copilot", Friendly = "Copilot", Level = "optional", Location = @"C:\Program Files\WindowsApps\Microsoft.Copilot_1.0_x64__8wekyb3d8bbwe" } };
                    var o1 = MainForm.FindOwner(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\a.pak", progs, store) as AppEntry;
                    var o2 = MainForm.FindOwner(@"C:\Program Files\WindowsApps\Microsoft.Copilot_1.0_x64__8wekyb3d8bbwe\app\msedge.dll", progs, store) as StoreApp;
                    check(o1 != null && o1.Name == "Epic Games Launcher" && o2 != null && o2.Friendly == "Copilot" &&
                          MainForm.FindOwner(@"C:\Program Files (x86)\Epic GamesX\a.dll", progs, store) == null &&
                          MainForm.FindOwner(@"C:\Windows\System32\a.dll", progs, store) == null,
                          "Finds which app a file belongs to (and never a similar-looking folder)");
                    if (win)
                    {
                        var real = StoreData.ReadInstalled(false);
                        var st = real.FirstOrDefault(a => a.Name == "Microsoft.WindowsStore");
                        check(real.Count > 0 && (st == null || st.Level == "keep"), "Store apps: found " + real.Count + " on this PC; the Microsoft Store itself is locked");
                    }
                } catch (Exception ex) { check(false, "Store app tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3g. Undo area (in the test folder only)
                try {
                    string uroot = Path.Combine(root, "undo-area");
                    Quarantine.OverrideRoot = uroot;
                    string victim = Path.Combine(root, "victim", "$GetCurrent");
                    Directory.CreateDirectory(Path.Combine(victim, "media", "sources"));
                    File.WriteAllBytes(Path.Combine(victim, "media", "sources", "install.esd"), new byte[5000]);
                    File.WriteAllBytes(Path.Combine(victim, "readme.txt"), new byte[100]);
                    var logs = new List<string>();
                    Quarantine.MoveToUndo("Windows upgrade media ($GetCurrent)", new[] { victim }, logs.Add);
                    var batches = Quarantine.List();
                    check(!Directory.Exists(victim) && batches.Count == 1 && batches[0].Bytes == 5100 && batches[0].Label.Contains("$GetCurrent") && batches[0].DaysLeft(7) == 7,
                          "Undo: a Caution item is moved into Undo with its size and 7 days to go");
                    int restored = Quarantine.Restore(batches[0], logs.Add);
                    check(restored == 1 && File.Exists(Path.Combine(victim, "media", "sources", "install.esd")) && new FileInfo(Path.Combine(victim, "media", "sources", "install.esd")).Length == 5000 &&
                          Quarantine.List().Count == 0, "Undo: Restore puts every file back exactly where it was");
                    Quarantine.MoveToUndo("old item", new[] { victim }, logs.Add);
                    var old = Quarantine.List()[0];
                    File.WriteAllText(Path.Combine(old.Dir, "manifest.txt"), File.ReadAllText(Path.Combine(old.Dir, "manifest.txt"))
                        .Replace(old.Time.ToString("s", System.Globalization.CultureInfo.InvariantCulture), DateTime.Now.AddDays(-8).ToString("s", System.Globalization.CultureInfo.InvariantCulture)));
                    check(Quarantine.PurgeOld(null) == 1 && Quarantine.List().Count == 0, "Undo: items older than 7 days are deleted for good");
                } catch (Exception ex) { check(false, "Undo tests stopped: " + ex.GetType().Name + ": " + ex.Message); }
                finally { Quarantine.OverrideRoot = null; }

                // 3h. old drivers (logic only; nothing is removed by the self-test)
                try {
                    var pk = Drivers.Parse(
                        "oem10.inf\tC:\\Windows\\System32\\DriverStore\\FileRepository\\nv_dispi.inf_amd64_a\\nv_dispi.inf\tNVIDIA\tDisplay\t32.0.15.6094\t2024-08-01\tFalse\tFalse\r\n" +
                        "oem22.inf\tC:\\Windows\\System32\\DriverStore\\FileRepository\\nv_dispi.inf_amd64_b\\nv_dispi.inf\tNVIDIA\tDisplay\t32.0.15.6590\t2025-01-10\tFalse\tTrue\r\n" +
                        "oem31.inf\tC:\\Windows\\System32\\DriverStore\\FileRepository\\nv_dispi.inf_amd64_c\\nv_dispi.inf\tNVIDIA\tDisplay\t32.0.15.7680\t2026-09-30\tFalse\tFalse\r\n" +
                        "oem5.inf\tC:\\x\\iastor.inf_1\\iastor.inf\tIntel\tSCSIAdapter\t18.0.0.1\t2020-01-01\tTrue\tFalse\r\n" +
                        "oem6.inf\tC:\\x\\iastor.inf_2\\iastor.inf\tIntel\tSCSIAdapter\t19.0.0.1\t2022-01-01\tTrue\tFalse\r\n" +
                        "oem7.inf\tC:\\x\\rt640x64.inf_1\\rt640x64.inf\tRealtek\tNet\t10.1.0.0\t2023-01-01\tFalse\tFalse\r\nbad\r\n");
                    Drivers.MarkOld(pk);
                    Func<string, DriverPkg> D = inf => pk.First(x => x.Inf == inf);
                    check(pk.Count == 6 && !D("oem31.inf").Old && !D("oem22.inf").Old && D("oem10.inf").Old,
                          "Old drivers: keeps the newest and the one in use, marks the older one");
                    check(!D("oem5.inf").Old && !D("oem6.inf").Old && !D("oem7.inf").Old, "Old drivers: never boot-critical drivers or a driver with only one version");

                    // drivers that don't fit this PC
                    var fit = Drivers.Parse(
                        "oem1.inf\tC:\\R\\nv_dispi.inf_amd64_a\\nv_dispi.inf\tNVIDIA\tDisplay\t31.0\t2024-01-01\tFalse\tTrue\tNVIDIA GeForce RTX 3070\r\n" +
                        "oem2.inf\tC:\\R\\nv_dispi.inf_amd64_b\\nv_dispi.inf\tNVIDIA\tDisplay\t30.0\t2023-01-01\tFalse\tFalse\t\r\n" +
                        "oem3.inf\tC:\\R\\usbser.inf_x86_c\\usbser.inf\tAcme\tPorts\t1.0\t2019-01-01\tFalse\tFalse\t\r\n" +
                        "oem4.inf\tC:\\R\\hpprint.inf_x86_d\\hpprint.inf\tHP\tPrinter\t1.0\t2019-01-01\tFalse\tFalse\t\r\n" +
                        "oem8.inf\tC:\\R\\oldcam.inf_amd64_e\\oldcam.inf\tCreative\tImage\t1.0\t2012-05-01\tFalse\tFalse\t\r\n" +
                        "oem9.inf\tC:\\R\\phone.inf_amd64_f\\phone.inf\tSamsung\tUSB\t2.0\t2024-03-01\tFalse\tFalse\t\r\n" +
                        "oem11.inf\tC:\\R\\ext.inf_amd64_g\\ext.inf\tIntel\tExtension\t1.0\t2022-01-01\tFalse\tFalse\t\r\n" +
                        "oem12.inf\tC:\\R\\ancient.inf_amd64_h\\ancient.inf\tGenius\tMouse\t1.0\t2018-01-01\tFalse\tFalse\t\r\n" +
                        "HW\tCPU\tAMD Ryzen 7 5800X\r\nHW\tGraphics\tNVIDIA GeForce RTX 3070\r\nHW\tDisk\t\r\n");
                    Drivers.MarkOld(fit);
                    Drivers.Classify(fit, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oem1.inf", "oem2.inf", "oem9.inf" }, "amd64");
                    Func<string, DriverPkg> F = inf => fit.First(x => x.Inf == inf);
                    check(F("oem1.inf").Kind == DrvKind.Kept && F("oem1.inf").Why.Contains("RTX 3070") && !F("oem1.inf").Removable, "Drivers: a driver a device uses is kept and shows the device");
                    check(F("oem2.inf").Kind == DrvKind.Old && F("oem2.inf").Suggested, "Drivers: an older version is suggested for removal");
                    check(F("oem3.inf").Kind == DrvKind.NotForWindows && F("oem3.inf").Why.Contains("32-bit") && F("oem3.inf").Suggested, "Drivers: a 32-bit driver on 64-bit Windows is marked 'not for this Windows'");
                    check(F("oem4.inf").Kind == DrvKind.Locked && F("oem11.inf").Kind == DrvKind.Locked, "Drivers: printer and extension drivers are never offered");
                    check(F("oem8.inf").Kind == DrvKind.NoHardware && F("oem8.inf").Why.Contains("Windows 7/8") && F("oem12.inf").Kind == DrvKind.NoHardware && !F("oem12.inf").Why.Contains("Windows 7"),
                          "Drivers: drivers no hardware in this PC uses are suggested (and pre-2015 ones say Windows 7/8)");
                    check(F("oem9.inf").Kind == DrvKind.NotConnected && F("oem9.inf").Removable && !F("oem9.inf").Suggested, "Drivers: hardware that is only unplugged is left for you to decide");
                    var hwl = Drivers.ParseHardware("x\r\nHW\tCPU\tAMD Ryzen 7 5800X\r\nHW\tGraphics\tNVIDIA GeForce RTX 3070\r\nHW\tDisk\t\r\n");
                    check(hwl.Count == 2 && hwl[1].Key == "Graphics" && Drivers.ArchOf(@"C:\R\a.inf_arm64_1\a.inf") == "arm64" && Drivers.ArchOf(@"C:\x\a.inf") == "" &&
                          new[] { "amd64", "x86", "arm64" }.Contains(Drivers.OsArch()), "Drivers: reads hardware, driver type and Windows type (" + Drivers.OsArch() + ")");
                    if (win)
                    {
                        var real = Drivers.Read();
                        check(real.All(d => !(d.Removable && (d.InUse || d.BootCritical))) && (real.Count > 0 || !Sys.IsAdmin()),
                              "Drivers: read " + real.Count + " driver packages on this PC; none in use is offered for removal" + (Sys.IsAdmin() ? "" : " (not admin: Windows only lists drivers to administrators)"));
                        check(Drivers.Hardware.Count > 0, "Drivers: this PC's hardware: " + string.Join(", ", Drivers.Hardware.Take(3).Select(h => h.Value)));
                    }
                } catch (Exception ex) { check(false, "Driver tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3i. app, game and developer caches
                try {
                    string cr = Path.Combine(root, "discord");
                    foreach (var d in new[] { "Cache", "Code Cache", "GPUCache", Path.Combine("Partitions", "x", "Cache"), "Local Storage", "Cache2" }) Directory.CreateDirectory(Path.Combine(cr, d));
                    var cc = Caches.ChromiumCaches(cr).Select(x => x.Substring(cr.Length + 1)).OrderBy(x => x).ToList();
                    check(cc.Count == 4 && !cc.Contains("Local Storage") && !cc.Contains("Cache2"), "Caches: finds only real cache folders (never Local Storage, where logins live)");
                    string ext = Path.Combine(root, "extensions");
                    foreach (var d in new[] { "anthropic.claude-code-2.1.287-win32-x64", "anthropic.claude-code-2.1.293-win32-x64", "ms-python.python-2025.2.0", "ms-python.python-2026.1.0", "esbenp.prettier-vscode-11.0.0" })
                        Directory.CreateDirectory(Path.Combine(ext, d));
                    var oldExt = Caches.OldExtensionFolders(ext).Select(Path.GetFileName).OrderBy(x => x).ToList();
                    check(oldExt.Count == 2 && oldExt[0] == "anthropic.claude-code-2.1.287-win32-x64" && oldExt[1] == "ms-python.python-2025.2.0",
                          "Caches: keeps the newest version of every VS Code extension");
                    string vers = Path.Combine(root, "claude-code");
                    foreach (var d in new[] { "2.1.287", "2.1.293", "2.0.9", "notes" }) Directory.CreateDirectory(Path.Combine(vers, d));
                    var oldV = Caches.OldVersionFolders(vers).Select(Path.GetFileName).OrderBy(x => x).ToList();
                    check(oldV.Count == 2 && oldV.Contains("2.0.9") && oldV.Contains("2.1.287"), "Caches: keeps the newest app version, ignores other folders");
                    var cat = Catalog.Build();
                    check(cat.Where(i => Caches.Items().Any(c => c.Name == i.Name)).All(i => !i.DefaultOn) && Automation.AutoItems().Count == 11,
                          "Caches: none are ticked by default and the weekly clean is unchanged");
                } catch (Exception ex) { check(false, "Cache tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3j. system space helpers and duplicates
                try {
                    int n; bool? rec;
                    SystemSpace.ParseAnalyze("Actual Size of Component Store : 9.12 GB\r\nNumber of Reclaimable Packages : 4\r\nComponent Store Cleanup Recommended : Yes\r\n", out n, out rec);
                    check(n == 4 && rec == true, "WinSxS: reads how many packages can be cleaned");
                    var sh = SystemSpace.ParseShadow("C:\t3221225472\t3500000000\t10737418240\r\nD:\t0\t0\t0\r\n");
                    check(sh.Count == 2 && sh[0].Drive == "C:" && sh[0].Used == 3221225472L && sh[0].Max == 10737418240L, "Restore points: reads used space and limit");
                    check(SystemSpace.ResizeArgs("10GB") == "resize shadowstorage /for=C: /on=C: /maxsize=10GB" &&
                          SystemSpace.DiskpartScript(@"C:\a b\ext4.vhdx").Contains("select vdisk file=\"C:\\a b\\ext4.vhdx\"") && SystemSpace.DiskpartScript("x").Contains("attach vdisk readonly"),
                          "System space: Windows commands are built correctly");
                    string dd = Path.Combine(root, "dups");
                    Directory.CreateDirectory(Path.Combine(dd, "a")); Directory.CreateDirectory(Path.Combine(dd, "b")); Directory.CreateDirectory(Path.Combine(dd, "AppData", "x"));
                    var payload = new byte[200000]; new Random(7).NextBytes(payload);
                    File.WriteAllBytes(Path.Combine(dd, "a", "video.mp4"), payload);
                    File.WriteAllBytes(Path.Combine(dd, "b", "video (1).mp4"), payload);
                    File.WriteAllBytes(Path.Combine(dd, "copy.mp4"), payload);
                    File.WriteAllBytes(Path.Combine(dd, "AppData", "x", "video.mp4"), payload);
                    var diff = (byte[])payload.Clone(); diff[150000] ^= 1;
                    File.WriteAllBytes(Path.Combine(dd, "almost.mp4"), diff);
                    var groups = Duplicates.Find(new[] { dd }, 1000, null, System.Threading.CancellationToken.None, true);
                    check(groups.Count == 1 && groups[0].Files.Count == 3 && groups[0].Wasted == 400000,
                          "Duplicates: finds identical files only (not a file that differs by one byte, not inside AppData)");
                } catch (Exception ex) { check(false, "System space tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3k. phone: a simulated Android phone (Elixar plays the phone itself), and the iPhone file plumbing
                try {
                    string me = typeof(SelfTest).Assembly.Location;
                    if (win) { Adb.Exe = me; Adb.Prefix = "--fake-adb"; } else { Adb.Exe = "mono"; Adb.Prefix = Adb.WinArg(me) + " --fake-adb"; }
                    string flog = Path.Combine(root, "fake-adb.log");
                    Environment.SetEnvironmentVariable("ELIXAR_FAKE_ADB_LOG", flog);
                    Environment.SetEnvironmentVariable("ELIXAR_SELFTEST", "1");
                    check(Adb.WinArg("a b") == "\"a b\"" && Adb.WinArg("x\"y") == "\"x\\\"y\"" && Adb.WinArg("C:\\a b\\") == "\"C:\\a b\\\\\"" && Adb.Q("it's") == "'it'\\''s'",
                          "Phone: commands are quoted safely for Windows and for the phone");
                    string derr; var devs = Adb.Devices(out derr);
                    check(devs.Count == 1 && devs[0].State == "device" && devs[0].Model == "SM S911B", "Phone: finds the connected Android phone");
                    var dev = devs[0];
                    Android.ParseInfo(dev, Adb.Shell(dev.Serial, Android.InfoScript));
                    check(dev.Title == "Galaxy S23 (Samsung SM-S911B)" && dev.Version == "14" && dev.Total == 115712120L * 1024 && dev.Free == 42300076L * 1024, "Phone: reads model, Android version and free space");
                    List<StorageSplit> split;
                    var apps = Android.ParseApps(Adb.Shell(dev.Serial, Android.AppsScript), out split);
                    Func<string, PhoneApp> A = pk => apps.FirstOrDefault(x => x.Package == pk);
                    check(split.Count == 9 && split.First(x => x.Label == "App Cache").Bytes == 3328599654L, "Phone: reads Android's storage split");
                    check(A("com.whatsapp") != null && !A("com.whatsapp").System && A("com.whatsapp").DataBytes == 6442450944L && A("com.whatsapp").LastUsed.Year == 2026 && A("com.whatsapp").Name == "WhatsApp",
                          "Phone: reads your apps with sizes and last use");
                    check(A("com.facebook.appmanager").Tier == "optional" && A("com.samsung.android.bixby.agent").Tier == "review" && A("android").Tier == "" && A("com.android.systemui").Tier == "",
                          "Phone: suggests known pre-installed extras, never Android's own parts");
                    check(A("com.samsung.android.app.spage") != null && A("com.samsung.android.app.spage").Removed, "Phone: lists apps removed earlier so they can be put back");
                    check(Android.AppRemoveCmd(A("com.facebook.appmanager")) == "pm uninstall --user 0 'com.facebook.appmanager'" && Android.AppRemoveCmd(A("com.example.oldgame")) == "pm uninstall 'com.example.oldgame'" &&
                          Android.AppRestoreCmd(A("com.samsung.android.app.spage")) == "cmd package install-existing 'com.samsung.android.app.spage'",
                          "Phone: pre-installed apps are removed for you only (can be put back)");
                    var items = Android.Items();
                    foreach (var it in items) Android.Measure(dev.Serial, it, 3328599654L);
                    Func<string, PhoneItem> I = n => items.First(x => x.Name.StartsWith(n));
                    check(I("Cache of every").Bytes == 3328599654L && I("Gallery thumbnails").Bytes == 1001000 && I("Gallery thumbnails").Files == 2 && I("WhatsApp media you sent").Bytes == 734003200L &&
                          items.All(x => x.AllCaches || x.Tier == "review" || x.DefaultOn), "Phone: measures caches, thumbnails and WhatsApp media");
                    string bdir = Path.Combine(root, "phone-backup");
                    try { File.Delete(flog); } catch { }
                    long freed = Android.Clean(dev.Serial, I("WhatsApp media you sent"), bdir, null) + Android.Clean(dev.Serial, I("Cache of every"), null, null);
                    string cmds = File.Exists(flog) ? File.ReadAllText(flog) : "";
                    check(freed == 734003200L + 3328599654L && File.Exists(Path.Combine(bdir, "Android", "media", "com.whatsapp", "WhatsApp", "Media", "WhatsApp Video", "Sent", "VID-1.mp4")) &&
                          !Directory.Exists(Path.Combine(bdir, PhoneBackup.Incoming)) &&
                          cmds.Contains("rm -f '/storage/emulated/0/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/Sent/VID-1.mp4'") && cmds.Contains("pm trim-caches") &&
                          cmds.IndexOf("| pull |", StringComparison.Ordinal) < cmds.IndexOf("rm -f", StringComparison.Ordinal),
                          "Phone: copies your files to the PC first, then removes only those from the phone");
                    var big = Android.BigFiles(dev.Serial, 50L << 20);
                    check(big.Count == 8 && big[0].Name == "20250815_191233.mp4" && big[0].Folder == "/storage/emulated/0/DCIM/Camera" && big[0].Kind == "Video" &&
                          big.All(f => f.Path.IndexOf("/Android/data/", StringComparison.Ordinal) < 0), "Phone: lists big files, biggest first, leaving apps' own files out");
                    check(PhoneBackup.LocalFor(@"D:\Phone backup", "/storage/emulated/0/DCIM/Camera") == Path.Combine(Path.Combine(@"D:\Phone backup", "DCIM"), "Camera") &&
                          PhoneBackup.Safe("Elisar's iPhone") == "Elisar's iPhone" && PhoneBackup.Safe("a" + Path.DirectorySeparatorChar + "b") == "a_b",
                          "Phone: copies keep the phone's folders on the PC");
                    if (win)
                    {
                        // the same Windows calls used for the iPhone, on normal files
                        string isrc = Path.Combine(root, "iphone-src"), idst = Path.Combine(root, "iphone-dst");
                        Directory.CreateDirectory(Path.Combine(isrc, "100APPLE")); Directory.CreateDirectory(idst);
                        File.WriteAllBytes(Path.Combine(isrc, "100APPLE", "IMG_0001.MOV"), new byte[300000]);
                        File.WriteAllBytes(Path.Combine(isrc, "100APPLE", "IMG_0002.HEIC"), new byte[1000]);
                        var media = IPhone.ListMedia(isrc, 100000, null);
                        check(media.Count == 1 && media[0].Name == "IMG_0001.MOV" && media[0].Bytes == 300000 && media[0].Folder == "DCIM/100APPLE",
                              "iPhone: Windows' shell lists photos and videos with their sizes");
                        bool copied = IPhone.CopyTo(media[0].Path, media[0].Name, media[0].Bytes, idst, media[0].Folder);
                        bool deleted = copied && IPhone.Delete(media[0].Path) && !File.Exists(Path.Combine(isrc, "100APPLE", "IMG_0001.MOV"));
                        string icopy = Path.Combine(Path.Combine(Path.Combine(idst, "DCIM"), "100APPLE"), "IMG_0001.MOV");
                        check(copied && deleted && File.Exists(icopy) && new FileInfo(icopy).Length == 300000 && !Directory.Exists(Path.Combine(idst, PhoneBackup.Incoming)),
                              "iPhone: copy, check and delete work without pop-ups (through a protected folder)");
                        IPhone.Found ip = null; Exception ipe = null;
                        try { ip = IPhone.Find(); } catch (Exception e) { ipe = e; }
                        check(ipe == null, "iPhone: looking for an iPhone works" + (ip != null ? " (found " + ip.Name + (ip.Error != null ? ", " + ip.Error : "") + ")" : " (none plugged in)") + (ipe != null ? " ERROR: " + ipe.Message : ""));
                    }
                } catch (Exception ex) { check(false, "Phone tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3l. security: protected folders, links, locked folders, restore targets, signatures, startup checks, privacy
                try {
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (string.IsNullOrEmpty(winDir)) winDir = @"C:\Windows";
                    string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    check((!win || Protect.IsProtected(@"C:\")) && Protect.IsProtected(Path.GetPathRoot(root)) && (!win || Protect.IsProtected(Path.Combine(winDir, "System32"))) && (!win || Protect.IsProtected(Path.Combine(Path.Combine(winDir, "System32"), "drivers"))) &&
                          Protect.IsProtected(up) && Protect.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) &&
                          !Protect.IsProtected(Path.Combine(root, "cache")) && !Protect.IsProtected(Path.Combine(Path.Combine(winDir, "Temp"), "x")),
                          "Security: Windows' own folders, drive roots and your main folders are never deleted");
                    check(!Quarantine.SafeTarget(Path.Combine(Path.Combine(winDir, "System32"), "evil.dll")) && !Quarantine.SafeTarget("relative\\path") &&
                          !Quarantine.SafeTarget(@"\\server\share\x") && !Quarantine.SafeTarget(root + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "x") &&
                          Quarantine.SafeTarget(Path.Combine(root, "restore-here")), "Security: Undo only puts things back in safe places");
                    string ex1 = Path.Combine(root, "My App"); Directory.CreateDirectory(ex1); File.WriteAllText(Path.Combine(ex1, "app.exe"), "x");
                    check(StartupApps.ExeOf("\"C:\\Program Files\\App\\a.exe\" --min") == @"C:\Program Files\App\a.exe" &&
                          StartupApps.ExeOf(Path.Combine(ex1, "app.exe") + " /background") == Path.Combine(ex1, "app.exe") &&
                          StartupApps.IsOn(null) && StartupApps.IsOn(new byte[] { 2, 0 }) && StartupApps.IsOn(new byte[] { 6, 0 }) && !StartupApps.IsOn(new byte[] { 3, 0 }) &&
                          !StartupApps.IsOn(StartupApps.SwitchValue(false)) && StartupApps.IsOn(StartupApps.SwitchValue(true)) && StartupApps.SwitchValue(false).Length == 12,
                          "Startup: reads program paths and Task Manager's on/off switch");
                    Func<string, string, string, bool, StartupFlag> J = (file, cmd, sig, missing) => { var e = new StartupEntry { File = file, Command = cmd, SigStatus = sig, Signer = "X", Missing = missing }; StartupApps.Judge(e); return e.Flag; };
                    check(J(@"C:\Gone\a.exe", "a", "", true) == StartupFlag.Leftover &&
                          J(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell -w hidden -enc AAAA", "Valid", false) == StartupFlag.Danger &&
                          J(@"C:\Users\x\AppData\Local\Temp\upd.exe", "", "Valid", false) == StartupFlag.Danger &&
                          J(@"C:\Program Files\App\a.exe", "", "Valid", false) == StartupFlag.Ok && J(@"C:\Program Files\App\a.exe", "", "NotSigned", false) == StartupFlag.Review &&
                          J(@"C:\Program Files\App\a.exe", "", "HashMismatch", false) == StartupFlag.Danger,
                          "Startup: flags leftovers, hidden scripts, programs in temp folders and broken signatures");
                    string es, en, ec;
                    var enc = StartupApps.Encode(new StartupEntry { Source = "hkcu-run", ItemPath = "My App", Command = "\"C:\\a b\\a.exe\" -x\tz" });
                    check(StartupApps.Decode(enc, out es, out en, out ec) == false || (es == "hkcu-run" && en == "My App"), "Startup: removed entries can be put back");
                    var enc2 = StartupApps.Encode(new StartupEntry { Source = "hkcu-run", ItemPath = "My App", Command = "\"C:\\a b\\a.exe\" -x" });
                    check(StartupApps.Decode(enc2, out es, out en, out ec) && es == "hkcu-run" && en == "My App" && ec == "\"C:\\a b\\a.exe\" -x" &&
                          !StartupApps.Decode("reg:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("user-folder\tx\ty")), out es, out en, out ec) && !StartupApps.Decode("reg:###", out es, out en, out ec),
                          "Startup: Undo only accepts its own registry entries");
                    string ud2 = Path.Combine(root, "UserData");
                    Directory.CreateDirectory(Path.Combine(ud2, "Default")); Directory.CreateDirectory(Path.Combine(ud2, "Profile 2", "Network")); Directory.CreateDirectory(Path.Combine(ud2, "System Profile"));
                    File.WriteAllBytes(Path.Combine(ud2, "Default", "History"), new byte[700]); File.WriteAllBytes(Path.Combine(ud2, "Default", "Login Data"), new byte[50]);
                    File.WriteAllBytes(Path.Combine(ud2, "Profile 2", "Network", "Cookies"), new byte[300]); File.WriteAllBytes(Path.Combine(ud2, "System Profile", "History"), new byte[9]);
                    var hf = Privacy.ProfileFiles(new[] { ud2 }, new[] { "History", Path.Combine("Network", "Cookies") });
                    check(hf.Count == 2 && !hf.Any(f => f.Contains("Login Data") || f.Contains("System Profile")), "Privacy: finds history and cookies in every profile, never saved passwords");
                    if (win)
                    {
                        // a link planted inside a folder Elixar cleans must not lead it anywhere else
                        string victim = Path.Combine(root, "victim"), clean = Path.Combine(root, "to-clean");
                        Directory.CreateDirectory(victim); Directory.CreateDirectory(clean);
                        File.WriteAllText(Path.Combine(victim, "important.txt"), "keep");
                        File.WriteAllText(Path.Combine(clean, "junk.tmp"), "junk");
                        var mk = Process.Start(new ProcessStartInfo(Win.SystemExe("cmd.exe"), "/c mklink /J \"" + Path.Combine(clean, "link") + "\" \"" + victim + "\"") { UseShellExecute = false, CreateNoWindow = true });
                        mk.WaitForExit();
                        Fs.ClearIn(clean);
                        check(mk.ExitCode == 0 && File.Exists(Path.Combine(victim, "important.txt")) && Directory.GetFileSystemEntries(clean).Length == 0,
                              "Security: a link planted in a folder being cleaned is removed without touching what it points to");
                        string ro2 = Path.Combine(clean, "ro.txt"); File.WriteAllText(ro2, "x"); File.SetAttributes(ro2, FileAttributes.ReadOnly | FileAttributes.Hidden);
                        Fs.Delete(ro2);
                        check(!File.Exists(ro2), "Security: link-safe delete also removes read-only and hidden files");
                        string lk = Path.Combine(root, "locked");
                        bool admin = Sys.IsAdmin();
                        check(!Locked.IsLocked(root) && (!admin || (Locked.Lock(lk) && Locked.IsLocked(lk) && Locked.OwnedByAdmins(lk))),
                              "Security: Undo folders are locked to administrators" + (admin ? "" : " (not admin: lock not tried)"));
                        // signatures: Windows' own check must agree with PowerShell's on a signed Microsoft file, and reject an unsigned one
                        // any program in Program Files that carries its own signature (most apps do)
                        string signedFile = StoreData.RunPs("$c = @(Get-ChildItem -Path \"$env:ProgramFiles\\*\\*.exe\", \"${env:ProgramFiles(x86)}\\*\\*.exe\" -ErrorAction SilentlyContinue | Select-Object -First 60 -ExpandProperty FullName); " +
                                                            "($c | ForEach-Object { Get-AuthenticodeSignature -LiteralPath $_ } | Where-Object { $_.SignatureType -eq 'Authenticode' -and $_.Status -eq 'Valid' } | Select-Object -First 1).Path", 60000)
                            .Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 3 && l.IndexOfAny(Path.GetInvalidPathChars()) < 0 && l.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) < 0 && File.Exists(l)) ?? "";
                        string unsigned = Path.Combine(root, "unsigned.exe"); File.WriteAllBytes(unsigned, new byte[] { 0x4D, 0x5A, 0, 0 });
                        check(signedFile.Length == 0 || (Trust.HasValidSignature(signedFile) && Trust.Signer(signedFile).Length > 0), "Security: Windows' signature check works (" + (signedFile.Length > 0 ? Path.GetFileName(signedFile) + ", signed by " + Trust.Signer(signedFile) : "no signed program found to test with") + ")");
                        check(!Trust.HasValidSignature(unsigned) && !Adb.SignedSet(root), "Security: unsigned files and fake Android tools are refused");
                        var sigs = Trust.CheckMany(new[] { Path.Combine(winDir, "notepad.exe"), Path.Combine(Path.Combine(winDir, "System32"), "notepad.exe") });
                        check(sigs.Values.Any(v => v[0] == "Valid" && v[1].Contains("Microsoft")), "Startup: signatures of Windows' own programs are recognised");
                        var su = StartupApps.Read(true);
                        check(su != null, "Startup: read " + su.Count + " startup programs on this PC (" + su.Count(x => x.Flag == StartupFlag.Danger) + " unusual, " + su.Count(x => x.Missing) + " leftovers)");
                        check(Install.Dir.EndsWith("Elixar") && Install.IsProtectedExe(Install.Exe) && !Install.IsProtectedExe(Path.Combine(root, "Elixar.exe")) &&
                              Paths.Data.EndsWith("Elixar"),
                              "Security: schedules only trust the protected copy in " + Install.Dir);
                        if (admin)
                        {
                            Exception ue = null; Process up2 = null;
                            bool desk = true;
                            try { up2 = Unelevated.Start(Path.Combine(Path.Combine(winDir, "System32"), "cmd.exe"), "/c exit 0"); if (up2 != null) up2.WaitForExit(10000); }
                            catch (InvalidOperationException) { desk = false; }
                            catch (Exception e) { ue = e; }
                            check(ue == null, "Security: uninstallers for your account can run without admin rights" + (!desk ? " (no desktop on this machine: skipped)" : "") + (ue != null ? " ERROR: " + ue.Message : ""));
                        }
                    }
                    string gh = Guide.Html(); string gp = Guide.WriteTemp(), gp2 = null;
                    check(gh.Contains("<title>Elixar User Guide</title>") && gh.Contains("id=\"security\"") && File.ReadAllText(gp, System.Text.Encoding.UTF8).Length == gh.Length && gp != (gp2 = Guide.WriteTemp()),
                          "User guide: built into Elixar and opens from a fresh file each time (" + (gh.Length / 1024) + " KB)");
                    try { File.Delete(gp); if (gp2 != null) File.Delete(gp2); } catch { }
                    var rep2 = new IntegrityForm(1f).Report();
                    check(System.Text.RegularExpressions.Regex.IsMatch(rep2, "SHA-256:\\s+[0-9A-F]{64}"), "Verify: shows Elixar's fingerprint (SHA-256)");
                } catch (Exception ex) { check(false, "Security tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3m. more space: planner, moving folders, Steam games, Windows.old, hibernation, Docker
                try {
                    Func<string, Tier, long, bool, CleanItem> CI = (n, tr, sz, priv) => new CleanItem { Name = n, Tier = tr, Size = sz, Privacy = priv };
                    long G = 1L << 30;
                    var pool = new List<CleanItem> { CI("Temp", Tier.Safe, 2 * G, false), CI("Shader", Tier.Safe, G / 2, false), CI("Spotify", Tier.Review, 3 * G, false),
                                                     CI("Recycle Bin", Tier.Review, 9 * G, false), CI("Windows.old", Tier.Caution, 20 * G, false), CI("History", Tier.Review, 5 * G, true) };
                    var p1 = Planner.Pick(pool, 2 * G, false, false);
                    var p2 = Planner.Pick(pool, 5 * G, false, false);
                    var p3 = Planner.Pick(pool, 30 * G, false, false);
                    var p4 = Planner.Pick(pool, 10 * G, true, false);
                    check(p1.Reached && p1.Items.Count == 1 && p1.Items[0].Name == "Temp" &&
                          p2.Reached && p2.Items.Any(i => i.Name == "Spotify") && p2.Items.Any(i => i.Name == "Temp") && !p2.Items.Any(i => i.Name == "Recycle Bin") &&
                          !p3.Reached && p3.MaxPossible == (long)(5.5 * G) && p3.Items.All(i => !i.Privacy && i.Tier != Tier.Caution) &&
                          p4.Reached && p4.Items.Any(i => i.Name == "Windows.old"),
                          "Free space for me: picks the safest items first, never privacy items, Caution and Recycle Bin only when allowed");
                    string mf = Path.Combine(root, "move-from"), mt = Path.Combine(root, "move-to");
                    Directory.CreateDirectory(Path.Combine(mf, "sub")); File.WriteAllBytes(Path.Combine(mf, "a.bin"), new byte[3000]); File.WriteAllBytes(Path.Combine(mf, "sub", "b.bin"), new byte[2000]);
                    string switched = null;
                    var mr = Mover.Move(mf, mt, to => { switched = to; return true; }, null);
                    check(mr.Switched && switched == mt && mr.Copied == 2 && mr.Bytes == 5000 && File.Exists(Path.Combine(mt, "sub", "b.bin")) && !File.Exists(Path.Combine(mf, "a.bin")),
                          "Move off C: copies and checks every file, switches the folder, then removes the old copies");
                    string mf2 = Path.Combine(root, "move-from2"); Directory.CreateDirectory(mf2); File.WriteAllBytes(Path.Combine(mf2, "keep.bin"), new byte[100]);
                    var mr2 = Mover.Move(mf2, Path.Combine(root, "move-to2"), to => false, null);
                    check(!mr2.Switched && File.Exists(Path.Combine(mf2, "keep.bin")), "Move off C: keeps your files where they were if Windows refuses the new place");
                    var sg = Mover.ParseManifest("\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"name\"\t\t\"Counter-Strike 2\"\n\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"\n\t\"SizeOnDisk\"\t\t\"35791394816\"\n}", @"C:\Steam");
                    string sroot = Path.Combine(root, "steam"); Directory.CreateDirectory(Path.Combine(sroot, "steamapps"));
                    File.WriteAllText(Path.Combine(sroot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}");
                    var libs = Mover.SteamLibraries(sroot);
                    check(sg != null && sg.Name == "Counter-Strike 2" && sg.Bytes == 35791394816L && sg.Folder.EndsWith("Counter-Strike Global Offensive") &&
                          libs.Contains(@"D:\SteamLibrary") && libs.Contains(@"C:\Program Files (x86)\Steam"), "Move off C: reads Steam libraries and game sizes");
                    check(WindowsOld.FlagName == "StateFlags4242" && WindowsOld.Handlers.Contains("Previous Installations") &&
                          Docker.ParseReclaimed("Deleted Images:\nTotal reclaimed space: 2.5GB") == 2500000000L && Docker.ParseReclaimed("nothing") == 0 &&
                          Hibernation.Describe(6L << 30, "reduced").Contains("6.0 GB") && Hibernation.Describe(0, "off").Contains("off"),
                          "Windows.old uses Windows' own Disk Cleanup; hibernation and Docker results are read correctly");
                    if (win)
                    {
                        var uf = Mover.ReadFolders(false);
                        check(uf.Count == 6 && uf.All(f => f.Path.Length > 3) && Directory.Exists(uf.First(f => f.Name == "Downloads").Path),
                              "Move off C: finds your personal folders (Downloads is in " + uf.First(f => f.Name == "Downloads").Path + ")");
                        string hs = Hibernation.State(); long hz = Hibernation.FileSize();
                        check(hs != null, "Hibernation: " + Hibernation.Describe(hz, hs));
                    }
                } catch (Exception ex) { check(false, "More space tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3n. space trend, folder map, Hebrew, dark mode
                try {
                    var hist = SpaceHistory.Parse(new[] { "2026-07-01\t" + (90L << 30) + "\t" + (500L << 30), "2026-08-01\t" + (80L << 30) + "\t" + (500L << 30),
                                                          "2026-08-01\t" + (79L << 30) + "\t" + (500L << 30), "2026-09-01\t" + (70L << 30) + "\t" + (500L << 30), "bad line" });
                    var fc = SpaceHistory.Predict(hist, new DateTime(2026, 9, 2));
                    var flat = SpaceHistory.Predict(SpaceHistory.Parse(new[] { "2026-09-01\t100\t500", "2026-09-05\t100\t500", "2026-09-09\t100\t500" }), new DateTime(2026, 9, 10));
                    check(hist.Count == 3 && hist[1].Free == (79L << 30) && fc.FullOn.HasValue && fc.GbPerMonth < -9 && fc.GbPerMonth > -11 &&
                          fc.FullOn.Value.Year == 2027 && !flat.FullOn.HasValue && SpaceHistory.Predict(hist.Take(1).ToList(), DateTime.Today).Text.StartsWith("Not enough"),
                          "Space trend: one reading a day, a straight-line forecast of when C: fills (" + fc.Text + ")");
                    var tm = new List<Treemap.Node> { new Treemap.Node { Name = "a", Bytes = 600 }, new Treemap.Node { Name = "b", Bytes = 300 }, new Treemap.Node { Name = "c", Bytes = 100 }, new Treemap.Node { Name = "z", Bytes = 0 } };
                    Treemap.Layout(tm, new RectangleF(0, 0, 100, 50));
                    bool inside = tm.Where(n => n.Bytes > 0).All(n => n.Rect.Left >= -0.01 && n.Rect.Top >= -0.01 && n.Rect.Right <= 100.01 && n.Rect.Bottom <= 50.01);
                    bool areas = tm.Where(n => n.Bytes > 0).All(n => Math.Abs(n.Rect.Width * n.Rect.Height - n.Bytes * 5000.0 / 1000) < 1);
                    bool apart = true;
                    foreach (var x1 in tm.Where(n => n.Bytes > 0)) foreach (var x2 in tm.Where(n => n.Bytes > 0 && n != x1)) { var i2 = RectangleF.Intersect(x1.Rect, x2.Rect); if (i2.Width * i2.Height > 0.5f) apart = false; }
                    string tmd = Path.Combine(root, "tm"); Directory.CreateDirectory(Path.Combine(tmd, "big")); File.WriteAllBytes(Path.Combine(tmd, "big", "x.bin"), new byte[5000]); File.WriteAllBytes(Path.Combine(tmd, "s.txt"), new byte[10]);
                    var tmn = Treemap.Read(tmd, new Dictionary<string, long>(), null, System.Threading.CancellationToken.None);
                    check(inside && areas && apart && tm.Single(n => n.Name == "z").Rect.Width == 0 && tmn.Count == 2 && tmn.First(n => n.IsDir).Bytes == 5000 && tmn.Any(n => n.Group && n.Bytes == 10),
                          "Folder map: every box sits inside the map, sized by space used, never overlapping");
                    bool wasHe = Lang.Hebrew;
                    Lang.Hebrew = true;
                    bool heOk = Lang.T("Cleaner") == "ניקוי" && Lang.T("Clean 3 (1.2 GB)").StartsWith("ניקוי 3") && Lang.T("Not in the table at all") == "Not in the table at all" &&
                                Catalog.Build().All(i => Lang.T(i.Name) != i.Name);
                    Lang.Hebrew = false;
                    check(heOk && Lang.T("Cleaner") == "Cleaner", "Hebrew: every cleanup item and the main screens are translated; English stays as it was");
                    Lang.Hebrew = wasHe;
                    bool wasDark = Theme.Dark;
                    Theme.Dark = true;
                    bool darkOk = Theme.MapBack(Color.White) == Theme.Panel && Theme.MapBack(Color.FromArgb(246, 248, 251)) == Theme.Back && Theme.MapFore(Color.Black) == Theme.Ink &&
                                  Theme.MapFore(Color.DimGray) == Theme.Quiet && Theme.MapFore(Color.FromArgb(0, 125, 70)).G > 125;
                    Theme.Dark = false;
                    check(darkOk && Theme.MapBack(Color.White) == Color.White, "Dark mode: light colours turn dark, text stays readable; light mode unchanged");
                    Theme.Dark = wasDark;
                } catch (Exception ex) { check(false, "Trend, map, Hebrew and theme tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3o. updates, installer
                try {
                    var rel = Updates.ParseRelease("{\"tag_name\":\"v1.9.2\",\"html_url\":\"https://github.com/elisar/elixar/releases/tag/v1.9.2\",\"body\":\"Notes\",\"assets\":[" +
                        "{\"name\":\"Elixar.zip\",\"browser_download_url\":\"https://github.com/elisar/elixar/releases/download/v1.9.2/Elixar.zip\"}," +
                        "{\"name\":\"elixar-manifest.txt\",\"browser_download_url\":\"https://github.com/elisar/elixar/releases/download/v1.9.2/elixar-manifest.txt\"}," +
                        "{\"name\":\"elixar-manifest.sig\",\"browser_download_url\":\"https://github.com/elisar/elixar/releases/download/v1.9.2/elixar-manifest.sig\"}]}");
                    var man = Updates.ParseManifest("version=1.9.2.0\nzip-sha256=aa11\nexe-sha256=BB22\n");
                    check(rel.Version == new Version(1, 9, 2) && rel.ZipUrl.EndsWith("Elixar.zip") && rel.SigUrl.EndsWith(".sig") && man.Version == new Version(1, 9, 2, 0) && man.ZipSha256 == "AA11" &&
                          Updates.ParseTag("v2.0") == new Version(2, 0) && Updates.ParseTag("nightly") == null, "Updates: reads GitHub's release and the signed manifest");
                    using (var key = new System.Security.Cryptography.RSACryptoServiceProvider(2048, new System.Security.Cryptography.CspParameters(24)))
                    {
                        key.PersistKeyInCsp = false;
                        var data = Encoding.UTF8.GetBytes("version=9.9.9\nzip-sha256=AB\nexe-sha256=CD\n");
                        var sig = key.SignData(data, System.Security.Cryptography.CryptoConfig.MapNameToOID("SHA256"));
                        string pub = key.ToXmlString(false);
                        var bad = (byte[])data.Clone(); bad[8] ^= 1;
                        using (var other = new System.Security.Cryptography.RSACryptoServiceProvider(2048)) { other.PersistKeyInCsp = false;
                            check(Updates.VerifySignature(data, sig, pub) && !Updates.VerifySignature(bad, sig, pub) && !Updates.VerifySignature(data, sig, other.ToXmlString(false)),
                                  "Updates: only a manifest signed with Elisar's key is accepted (a changed one or another key is refused)"); }
                    }
                    var entry = Setup.Entry(@"C:\Program Files\Elixar\Elixar.exe", "1.8.0", 900000);
                    check((string)entry["DisplayName"] == "Elixar" && (string)entry["Publisher"] == "Elisar" && ((string)entry["UninstallString"]).EndsWith("--uninstall") && (int)entry["EstimatedSize"] == 878,
                          "Installer: Elixar appears in Windows' installed apps list with an Uninstall command");
                    check(!Updates.Configured || Updates.Repo.Contains("/"), "Updates: " + (Updates.Configured ? "signed updates from github.com/" + Updates.Repo : "local build, updates switch on in the GitHub build"));
                } catch (Exception ex) { check(false, "Update and installer tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3p. disk health (read only) and Windows tools started by full path
                try {
                    string canned = "ADMIN\tTrue\n" +
                        "DISK\t0\tSamsung SSD 980 1TB\tSSD\tNVMe\tHealthy\tOK\t1000204886016\t4\t38\t61\t0\t0\t0\t0\t5210\tFalse\tC\n" +
                        "DISK\t1\tWDC WD20EZRZ\t3\t11\t1\t2\t2000398934016\t\t57\t60\t120\t3\t0\t0\t31000\t\tD,E\n" +
                        "DISK\t2\tOld Crucial\t4\t11\t2\tPredictive Failure\t256060514304\t100\t40\t48\t9\t0\t0\t0\t40000\tTrue\t\n" +
                        "DISK\t3\tUSB Stick\tUnspecified\tUSB\tHealthy\tOK\t32010928128\t\t255\t\t\t\t\t\t\t\tF\n" +
                        "garbage line\nEND\n";
                    var dr = DiskHealth.Parse(canned);
                    var d0 = dr.Disks.First(d => d.Id == "0"); var d1 = dr.Disks.First(d => d.Id == "1"); var d2 = dr.Disks.First(d => d.Id == "2"); var d3 = dr.Disks.First(d => d.Id == "3");
                    check(dr.Complete && dr.Admin && dr.Disks.Count == 4 && dr.Disks[0].Id == "0" && d0.Kind == "NVMe SSD" && d0.LifeLeft == 96 && d0.Temp == 38 && d0.HoldsC &&
                          d0.Level == HealthLevel.Healthy && d0.PredictFailure == false && DiskHealth.Hours(d0.Hours) == "217 days" && DiskHealth.Errors(d0) == "none",
                          "Disk health: reads a healthy NVMe drive (96% life left, 38 °C, no errors)");
                    check(d1.Kind == "SATA hard disk" && d1.Health == "Warning" && d1.Level == HealthLevel.Warning && d1.Temp == 57 && d1.Reasons.Any(r => r.Contains("running hot")) &&
                          d1.Uncorrected == 3 && d1.Reasons.Any(r => r.Contains("could not be fixed")) && d1.Letters == "D,E",
                          "Disk health: a warm hard disk with errors it could not fix needs attention");
                    check(d2.Level == HealthLevel.Failing && d2.Reasons.Count >= 4 && d2.Advice.StartsWith("Back up now") && dr.Worst == HealthLevel.Failing &&
                          dr.Summary.Contains("back up your files now"), "Disk health: a drive that predicts its own failure says: back up now");
                    check(d3.Kind == "USB drive" && d3.Temp == -1 && d3.Level == HealthLevel.Healthy && d3.Notes.Any(n => n.Contains("USB")),
                          "Disk health: odd readings from USB drives are ignored, not reported as a problem");
                    var none = DiskHealth.Parse("ADMIN\tFalse\nDISK\t5\t\t\t\t5\t\t\t\t\t\t\t\t\t\t\t\t\nEND");
                    check(none.Disks[0].Level == HealthLevel.Unknown && none.Disks[0].Name == "Disk 5" && none.Disks[0].Notes.Any(n => n.Contains("administrator")) &&
                          DiskHealth.Parse("").Summary == "Windows did not report any drives.", "Disk health: a drive that shares nothing is shown as unknown, never as healthy");
                    var today = new DateTime(2026, 10, 17);
                    check(!DiskHealth.ShouldAlert(HealthLevel.Healthy, "", today) && DiskHealth.ShouldAlert(HealthLevel.Warning, "", today) &&
                          !DiskHealth.ShouldAlert(HealthLevel.Warning, DiskHealth.Stamp(HealthLevel.Warning, today.AddDays(-3)), today) &&
                          DiskHealth.ShouldAlert(HealthLevel.Warning, DiskHealth.Stamp(HealthLevel.Warning, today.AddDays(-7)), today) &&
                          DiskHealth.ShouldAlert(HealthLevel.Failing, DiskHealth.Stamp(HealthLevel.Warning, today), today) &&
                          DiskHealth.ShouldAlert(HealthLevel.Failing, DiskHealth.Stamp(HealthLevel.Failing, today.AddDays(-1)), today) &&
                          !DiskHealth.ShouldAlert(HealthLevel.Failing, DiskHealth.Stamp(HealthLevel.Failing, today), today),
                          "Disk health alerts: a failing drive once a day, a warning once a week, at once when it gets worse");
                    var cmds = DiskHealth.CommandsIn(DiskHealth.Script);
                    check(DiskHealth.ScriptOnlyReads(DiskHealth.Script) && cmds.Contains("Get-PhysicalDisk") && cmds.Contains("Get-StorageReliabilityCounter") &&
                          !DiskHealth.ScriptOnlyReads(DiskHealth.Script + "\nSet-PhysicalDisk -FriendlyName x") && !DiskHealth.ScriptOnlyReads(DiskHealth.Script + "\n'x' > c:\\x.txt") &&
                          !DiskHealth.Script.ToLowerInvariant().Contains("serial"),
                          "Disk health only reads: its fixed script uses " + string.Join(", ", cmds) + " and nothing that writes, downloads or reads serial numbers");
                    if (win)
                    {
                        string wdir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                        var tools = new[] { "powershell.exe", "cmd.exe", "schtasks.exe", "explorer.exe", "dism.exe" }.Select(Win.SystemExe).ToList();
                        check(tools.All(p => Path.IsPathRooted(p) && p.StartsWith(wdir, StringComparison.OrdinalIgnoreCase) && File.Exists(p)) &&
                              Win.SystemExe(@"C:\x\y.exe") == @"C:\x\y.exe",
                              "Windows tools start from the Windows folder only (" + tools[0] + "), never a same-named file next to Elixar");
                        var real = DiskHealth.Read();
                        check(real.Error == null && real.Disks.Count > 0,
                              "Disk health on this PC: " + (real.Error ?? real.Summary + " " + string.Join("; ", real.Disks.Select(d => d.Label + " = " + DiskHealth.LevelText(d.Level) +
                              (d.LifeLeft >= 0 ? ", " + d.LifeLeft + "% life left" : "") + (d.Temp > 0 ? ", " + d.Temp + " °C" : "")))));
                    }
                    else check(Win.SystemExe("powershell.exe") == "powershell.exe", "Windows tools are named by full path (not on Windows here)");
                } catch (Exception ex) { check(false, "Disk health tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3q. 1.9.1 hardening: the running file, link-proof copies and moves, phone names, tool locations
                try {
                    check(SelfImage.Verified, "Security: the Elixar.exe on disk is the program that is running" + (win ? " (checked section by section, held open so it can't be swapped)" : ""));
                    {
                        // the same check against a simulated Windows loader: sections laid out, addresses relocated, imports filled in
                        byte[] fb = File.ReadAllBytes(Install.Current);
                        Func<byte[], int, int> R32 = (b, o) => BitConverter.ToInt32(b, o);
                        int pe = R32(fb, 0x3C), opt = pe + 24, nsec = BitConverter.ToUInt16(fb, pe + 6), optSize = BitConverter.ToUInt16(fb, pe + 20);
                        int dd = opt + (BitConverter.ToUInt16(fb, opt) == 0x20B ? 112 : 96), tab = opt + optSize;
                        int sizeImg = R32(fb, opt + 56), sizeHdr = R32(fb, opt + 60);
                        Func<byte[]> load = () =>
                        {
                            var img = new byte[sizeImg + 64];
                            Array.Copy(fb, img, sizeHdr);
                            for (int i = 0; i < nsec; i++) { int o = tab + 40 * i; Array.Copy(fb, R32(fb, o + 20), img, R32(fb, o + 12), Math.Min(R32(fb, o + 16), R32(fb, o + 8))); }
                            int rr = R32(fb, dd + 40), rs = R32(fb, dd + 44), p = rr;
                            while (rs > 0 && p + 8 <= rr + rs) { int page = R32(img, p), blk = R32(img, p + 4); if (blk < 8) break;
                                for (int e = p + 8; e + 2 <= p + blk; e += 2) { int v = BitConverter.ToUInt16(img, e); if (v >> 12 == 3) { int at = page + (v & 0xFFF); BitConverter.GetBytes(R32(img, at) + 0x7F0000).CopyTo(img, at); } }
                                p += blk; }
                            int iat = R32(fb, dd + 96), isz = R32(fb, dd + 100);
                            for (int k = 0; k < isz; k++) img[iat + k] = 0xCC;
                            return img;
                        };
                        var good = load();
                        Func<byte[], Func<int, int, byte[]>> view = im => (at, len) => { var x = new byte[len]; Array.Copy(im, at, x, 0, len); return x; };
                        var bad = load();
                        int text = R32(fb, tab + 12), textLen = Math.Min(R32(fb, tab + 16), R32(fb, tab + 8));
                        bad[text + textLen / 2] ^= 0x01;
                        // Windows rewrites a 32-bit .NET program's header to the 64-bit form in memory: the section table moves
                        var conv = load();
                        Array.Copy(conv, tab, conv, tab + 16, 40 * nsec); BitConverter.GetBytes((ushort)(optSize + 16)).CopyTo(conv, pe + 20);
                        var extra = load(); BitConverter.GetBytes((ushort)(nsec + 1)).CopyTo(extra, pe + 6);
                        check(SelfImage.Compare(fb, view(good)) && !SelfImage.Compare(fb, view(bad)) && SelfImage.Compare(fb, view(conv)) && !SelfImage.Compare(fb, view(extra)),
                              "Security: the running-program check accepts Windows' own changes when loading and catches a single changed byte");
                    }
                    string sf = Path.Combine(root, "safefile"); Directory.CreateDirectory(sf);
                    File.WriteAllBytes(Path.Combine(sf, "a.bin"), new byte[70000]);
                    long sc = SafeFile.Copy(Path.Combine(sf, "a.bin"), Path.Combine(sf, "b.bin"));
                    bool noOverwrite = false; try { SafeFile.CreateNew(Path.Combine(sf, "b.bin")).Dispose(); } catch (IOException) { noOverwrite = true; }
                    Directory.CreateDirectory(Path.Combine(sf, "into"));
                    bool moved = SafeFile.MoveInto(Path.Combine(sf, "b.bin"), Path.Combine(sf, "into"), "c.bin");
                    SafeFile.WriteText(Path.Combine(sf, "t.txt"), "one"); SafeFile.WriteText(Path.Combine(sf, "t.txt"), "two");
                    check(sc == 70000 && noOverwrite && moved && new FileInfo(Path.Combine(sf, "into", "c.bin")).Length == 70000 && !File.Exists(Path.Combine(sf, "b.bin")) &&
                          File.ReadAllText(Path.Combine(sf, "t.txt")) == "two" && SafeFile.FreeName(Path.Combine(sf, "into"), "c.bin") == "c (2).bin",
                          "Security: careful copy, move and save work (never writing over an existing file by accident)");
                    if (win)
                    {
                        string target = Path.Combine(root, "pretend-windows"), lnk = Path.Combine(sf, "jump");
                        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "system.dll"), "keep");
                        var mk2 = Process.Start(new ProcessStartInfo(Win.SystemExe("cmd.exe"), "/c mklink /J \"" + lnk + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true });
                        mk2.WaitForExit();
                        int blockedBefore = SafeFile.Blocked;
                        bool w1 = false, w2 = false, w3 = false;
                        try { SafeFile.Copy(Path.Combine(sf, "a.bin"), Path.Combine(lnk, "planted.dll")); } catch (IOException) { w1 = true; }
                        try { SafeFile.MoveInto(Path.Combine(sf, "a.bin"), lnk, "planted2.dll"); } catch (IOException) { w2 = true; }
                        try { SafeFile.OpenRead(Path.Combine(lnk, "system.dll")).Dispose(); } catch (IOException) { w3 = true; }
                        bool del = SafeDelete.Delete(Path.Combine(lnk, "system.dll"));
                        check(mk2.ExitCode == 0 && w1 && w2 && w3 && !del && SafeFile.Blocked >= blockedBefore + 3 && File.Exists(Path.Combine(target, "system.dll")) &&
                              !File.Exists(Path.Combine(target, "planted.dll")) && !File.Exists(Path.Combine(target, "planted2.dll")) && !SafeDelete.NoLinks(lnk) && SafeDelete.NoLinks(sf),
                              "Security: a link in the path can't redirect a copy, move, read or delete (as admin) into another folder");
                        check(!Locked.OnlyAdminsMadeAnything(sf), "Security: a folder with a planted link in it is never adopted as a locked folder");
                        string work = Paths.Work;
                        check(!Sys.IsAdmin() || (work != null && Locked.IsLocked(work)), "Security: files an administrator writes and then uses (like diskpart's script) go in a locked folder" + (Sys.IsAdmin() ? "" : " (not admin: not tried)"));
                        string dk = Docker.Exe();
                        check(dk == null || (dk.StartsWith(Win.ProgramFiles, StringComparison.OrdinalIgnoreCase) && Trust.SignedBy(dk, Trust.DockerInc)),
                              "Security: Docker is only run from Program Files and signed by Docker" + (dk == null ? " (not installed)" : ""));
                        string me = Install.Current;
                        check(SafeFile.HasDownloadMark(me) || !SafeFile.HasDownloadMark(me), "Download mark on this copy: " + (SafeFile.HasDownloadMark(me) ? "yes (Windows asks before running it; the installed copy has none)" : "none") +
                              (Install.IsInstalled ? "; installed copy: " + (SafeFile.HasDownloadMark(Install.Exe) ? "MARKED" : "none") : ""));
                    }
                    check(Drivers.SafeInf("oem12.inf") && Drivers.SafeInf("OEM7.INF") && !Drivers.SafeInf("oem1.inf /uninstall") && !Drivers.SafeInf("..\\x.inf") && !Drivers.SafeInf("nvlddmkm.inf"),
                          "Security: only Windows' own driver package names (oemNN.inf) are given to pnputil");
                    check(PhoneBackup.Safe("..") == "phone" && PhoneBackup.Safe("CON") == "_CON" && PhoneBackup.Safe("nul.txt") == "_nul.txt" && PhoneBackup.Safe("photo. ") == "photo" &&
                          !PhoneBackup.PlainName("..") && !PhoneBackup.PlainName("a" + Path.DirectorySeparatorChar + "b") && PhoneBackup.PlainName("IMG_0001.HEIC") &&
                          PhoneBackup.LocalFor(Path.Combine(root, "pb"), "/storage/emulated/0/../../../Windows/System32").StartsWith(Path.Combine(root, "pb")) &&
                          !PhoneBackup.LocalFor(Path.Combine(root, "pb"), "/storage/emulated/0/../../../Windows/System32").Contains(".."),
                          "Security: names from a phone can't lead outside the backup folder");
                } catch (Exception ex) { check(false, "1.9.1 security tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 3r. email reports: Google sign-in pieces and the emails themselves (nothing is sent by the self-test)
                try {
                    string cerr;
                    var gc = GoogleAuth.ParseClientFile("{\"installed\":{\"client_id\":\"123-abc.apps.googleusercontent.com\",\"project_id\":\"elixar\",\"auth_uri\":\"https://accounts.google.com/o/oauth2/auth\",\"client_secret\":\"GOCSPX-test\",\"redirect_uris\":[\"http://localhost\"]}}", out cerr);
                    string werr; GoogleAuth.ParseClientFile("{\"web\":{\"client_id\":\"1.apps.googleusercontent.com\",\"client_secret\":\"x\"}}", out werr);
                    string berr; var bad = GoogleAuth.ParseClientFile("{\"installed\":{\"client_id\":\"evil.example.com\",\"client_secret\":\"x\"}}", out berr);
                    string url = gc == null ? "" : GoogleAuth.AuthorizeUrl(gc, "http://127.0.0.1:50123/", "st8", GoogleAuth.Challenge("dBjftJeZ4CVP-mJ92K1QW7AlCl7QaPYhrXo9ZzG3CHs"));
                    check(gc != null && gc.Secret == "GOCSPX-test" && werr != null && werr.Contains("Desktop app") && bad == null &&
                          GoogleAuth.Challenge("dBjftJeZ4CVP-mJ92K1QW7AlCl7QaPYhrXo9ZzG3CHs") == "YWBkEzPoKgHufHmIl-EGam7u8O13MDYaK4GfqvdBdm8" &&
                          url.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?") && url.Contains("code_challenge_method=S256") && url.Contains("gmail.send") &&
                          !url.Contains("gmail.readonly") && !url.Contains("mail.google.com") && url.Contains("redirect_uri=http%3A%2F%2F127.0.0.1"),
                          "Email: Google sign-in asks only for permission to send (with PKCE), answered on this PC only");
                    var q = GoogleAuth.ParseQuery("GET /?state=st8&code=4%2F0Ab-x&scope=email HTTP/1.1");
                    Func<string, string> jwt = body => "eyJhbGciOiJSUzI1NiJ9." + GoogleAuth.B64Url(Encoding.UTF8.GetBytes(body)) + ".sig";
                    bool v1, v2, v3;
                    string em = GoogleAuth.EmailFromIdToken(jwt("{\"iss\":\"https://accounts.google.com\",\"aud\":\"123-abc.apps.googleusercontent.com\",\"email\":\"elisar@gmail.com\",\"email_verified\":true}"), "123-abc.apps.googleusercontent.com", out v1);
                    string other = GoogleAuth.EmailFromIdToken(jwt("{\"iss\":\"https://accounts.google.com\",\"aud\":\"someone-else.apps.googleusercontent.com\",\"email\":\"x@gmail.com\",\"email_verified\":true}"), "123-abc.apps.googleusercontent.com", out v2);
                    string inj = GoogleAuth.EmailFromIdToken(jwt("{\"iss\":\"https://accounts.google.com\",\"aud\":\"123-abc.apps.googleusercontent.com\",\"email\":\"a@b.com\\r\\nBcc: c@d.com\",\"email_verified\":true}"), "123-abc.apps.googleusercontent.com", out v3);
                    check(q["code"] == "4/0Ab-x" && q["state"] == "st8" && GoogleAuth.ParseQuery("POST /?code=1 HTTP/1.1").Count == 0 && em == "elisar@gmail.com" && v1 && other == null && inj == null,
                          "Email: the sign-in answer is checked (right app, verified address, nothing extra slipped into it)");
                    string mdir = Path.Combine(root, "mail"); Directory.CreateDirectory(mdir);
                    GoogleAuth.OverrideDir = mdir;
                    GoogleAuth.Save(new GoogleAuth.Account { Email = "elisar@gmail.com", Refresh = "1//refresh-secret", ClientId = "c", ClientSecret = "s", Since = DateTime.Now });
                    var back = GoogleAuth.Load();
                    byte[] raw = File.ReadAllBytes(Path.Combine(mdir, "account.dat"));
                    check(back != null && back.Email == "elisar@gmail.com" && back.Refresh == "1//refresh-secret" && (!win || Encoding.UTF8.GetString(raw).IndexOf("refresh-secret", StringComparison.Ordinal) < 0),
                          "Email: the Google sign-in is stored encrypted for your Windows account" + (win ? "" : " (not on Windows: stored plain in the test)"));
                    GoogleAuth.OverrideDir = null;
                    string mime = Mailer.Mime("elisar@gmail.com", "Weekly clean: freed 3.2 GB", "plain text", "<b>html</b>");
                    check(mime.Contains("To: <elisar@gmail.com>") && mime.Contains("multipart/alternative") && mime.Contains("=?UTF-8?B?") && mime.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("plain text"))) &&
                          !Mailer.Mime("a@b.com", "x\r\nBcc: evil@x.com", "t", "h").Contains("\r\nBcc:"), "Email: messages are built correctly and a subject can't add other recipients");
                    var items = Catalog.Build().Where(i => !i.Privacy).ToList();
                    foreach (var it in items) it.Size = it.Tier == Tier.Safe ? 700L << 20 : it.Tier == Tier.Review ? 2L << 30 : 0;
                    var cr = MailReports.CleanReport(new List<MailReports.CleanResult> { new MailReports.CleanResult { Name = items[0].Name, Freed = 900L << 20 },
                                                                                       new MailReports.CleanResult { Name = "Chrome cache", Skipped = "the app was open" } }, 900L << 20, 40L << 30, 500L << 30, items);
                    var sm = MailReports.Summary(40L << 30, 500L << 30, items, DiskHealth.Parse("ADMIN\tTrue\nDISK\t0\tSamsung SSD\tSSD\tNVMe\tHealthy\tOK\t1000204886016\t6\t41\t68\t0\t0\t0\t0\t6380\tFalse\tC\nEND"), null);
                    string all = cr.Html() + cr.Text() + sm.Html() + sm.Text();
                    check(cr.Subject.Contains("900") && all.Contains("What you can delete") && all.Contains("Samsung SSD") && all.Contains("never asks you to click") &&
                          !all.Contains("C:\\Users") && !all.Contains("C:\\Windows") && !all.Contains("href") && !all.Contains("http"),
                          "Email: reports list what was cleaned and what you can delete, with sizes only: no file paths and no links");
                    var today = new DateTime(2026, 10, 18);   // a Sunday
                    check(MailPrefs.SummaryDue(DateTime.MinValue, today, "Sunday") && MailPrefs.SummaryDue(today.AddDays(-7), today, "Sunday") && !MailPrefs.SummaryDue(today.AddDays(-2), today, "Sunday") &&
                          !MailPrefs.SummaryDue(today.AddDays(-3), today.AddDays(1), "Sunday") && MailPrefs.SummaryDue(today.AddDays(-8), today.AddDays(1), "Sunday"),
                          "Email: the weekly summary goes out once a week on your day (or the next day the PC is on)");
                    check(Automation.CreateMailArgs(@"C:\Program Files\Elixar\Elixar.exe", "09:00").Contains("--mail-daily") && Automation.CreateMailArgs("x", "09:00").Contains("/RL HIGHEST"),
                          "Email: the daily email check runs from the protected copy");
                    GoogleAuth.OverrideDir = mdir;
                    int lv0, lv1, lv2;
                    MailStatus.Reset(); MailStatus.Sent("Weekly summary"); string s0 = MailStatus.Line(out lv0);
                    MailStatus.Failed("Could not reach Google: offline", false); string s1 = MailStatus.Line(out lv1);
                    MailStatus.Failed(GoogleAuth.SignedOutText, true); string s2 = MailStatus.Line(out lv2);
                    bool n1 = MailStatus.ShouldNotify(DateTime.Today); MailStatus.Notified(); MailStatus.Load(); bool n2 = MailStatus.ShouldNotify(DateTime.Today);
                    MailStatus.Sent("Test"); MailStatus.Load(); bool cleared = !MailStatus.SignedOut;
                    check(lv0 == 0 && s0.StartsWith("Last email:") && s0.Contains("Weekly summary") && lv1 == 1 && s1.Contains("offline") && lv2 == 2 && s2.Contains("signed Elixar out") &&
                          n1 && !n2 && cleared, "Email: Settings shows the last email sent, a problem, or that Google signed Elixar out (with one reminder a day)");
                    MailStatus.Reset();
                    GoogleAuth.OverrideDir = null;
                    var tpts = new List<SpaceHistory.Point>();
                    for (int i = 40; i >= 0; i--) tpts.Add(new SpaceHistory.Point { Day = DateTime.Today.AddDays(-i), Free = (60L << 30) - (long)i * (1L << 28) * -1 - (i % 3) * (1L << 29), Total = 500L << 30 });
                    byte[] png = TrendChart.Png(tpts, DateTime.Today, 56);
                    string hf2 = Path.Combine(root, "space-history-mail.txt");
                    File.WriteAllLines(hf2, tpts.Select(x => x.Day.ToString("yyyy-MM-dd") + "\t" + x.Free + "\t" + x.Total));
                    SpaceHistory.OverrideFile = hf2;
                    var sm2 = MailReports.Summary(40L << 30, 500L << 30, items, null, null);
                    SpaceHistory.OverrideFile = null;
                    string mime2 = Mailer.Mime("elisar@gmail.com", sm2.Subject, sm2.Text(), sm2.Html(), sm2.Images);
                    check(TrendChart.IsPng(png) && png.Length < 120000 && TrendChart.Png(tpts.Take(1).ToList(), DateTime.Today, 56) == null && sm2.Images.Count == 1 &&
                          sm2.Html().Contains("src=\"cid:chart0@elixar\"") && mime2.Contains("multipart/related") && mime2.Contains("Content-ID: <chart0@elixar>") && mime2.Contains("Content-Type: image/png"),
                          "Email: the weekly summary carries a space-trend chart made on this PC (" + (png == null ? 0 : png.Length / 1024) + " KB, inside the email, no web link)");
                    // Email cleaning: fixed searches, never Starred/Important, only with the extra permission, fails safely offline
                    var cats = Gmail.Categories();
                    string u1 = gc == null ? "" : GoogleAuth.AuthorizeUrl(gc, "http://127.0.0.1:1/", "s", "c", false), u2 = gc == null ? "" : GoogleAuth.AuthorizeUrl(gc, "http://127.0.0.1:1/", "s", "c", true);
                    var acc = new GoogleAuth.Account { Scopes = "openid https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/userinfo.email" };
                    var acc2 = new GoogleAuth.Account { Scopes = "https://www.googleapis.com/auth/gmail.send " + GoogleAuth.ModifyScope + " openid" };
                    check(cats.Count == 6 && cats.Where(c => !c.SpamFolder).All(c => c.Query.Contains("-is:starred") && c.Query.Contains("-is:important")) &&
                          cats.Where(c => c.Tier == Tier.Review).All(c => !c.DefaultOn) && !u1.Contains("gmail.modify") && u2.Contains("gmail.modify") && !u2.Contains("mail.google.com") &&
                          !acc.Mailbox && acc2.Mailbox, "Email cleaning: only with its own extra permission; fixed searches that never include Starred or Important emails");
                    check(Gmail.SenderName("\"Amazon.com\" <store-news@amazon.com>") == "Amazon.com" && Gmail.SenderName("noreply@linkedin.com") == "linkedin.com" &&
                          Gmail.SenderName("<x@y.org>") == "y.org" && Gmail.SenderName("") == "(unknown)", "Email cleaning: shows who sends the most, by name");
                    GoogleAuth.OverrideDir = mdir;
                    GoogleAuth.Save(new GoogleAuth.Account { Email = "elisar@gmail.com", Refresh = "1//x", ClientId = "c.apps.googleusercontent.com", ClientSecret = "s", Since = DateTime.Now, Scopes = acc2.Scopes });
                    GoogleAuth.Forget();
                    var probe = Gmail.Categories()[0];
                    if (!win) Gmail.Scan(probe, System.Threading.CancellationToken.None);   // no internet in the test machine: must fail politely
                    bool polite = win || (probe.Error != null && probe.Count == 0 && probe.Ids.Count == 0);
                    MailStatus.Reset();
                    GoogleAuth.OverrideDir = null;
                    check(polite, "Email cleaning: a failed check shows a message and touches nothing");
                    check(true, "Email: Google app " + (GoogleAuth.BuiltInClient != null ? "built into this copy (no setup needed)" : "not built in (local build): each PC sets it up once"));
                } catch (Exception ex) { check(false, "Email tests stopped: " + ex.GetType().Name + ": " + ex.Message); GoogleAuth.OverrideDir = null; }

                // 3s. other email providers: Outlook (Microsoft sign-in) and any IMAP provider, against a pretend mail server on this PC
                try {
                    string mu1 = MicrosoftAuth.AuthorizeUrl("00000000-1111-2222-3333-444444444444", "http://localhost:5000/", "st", "ch", false);
                    string mu2 = MicrosoftAuth.AuthorizeUrl("00000000-1111-2222-3333-444444444444", "http://localhost:5000/", "st", "ch", true);
                    check(mu1.Contains("Mail.Send") && !mu1.Contains("Mail.ReadWrite") && mu2.Contains("Mail.ReadWrite") && mu1.Contains("code_challenge_method=S256") &&
                          MicrosoftAuth.ValidId("00000000-1111-2222-3333-444444444444") && !MicrosoftAuth.ValidId("not-an-id") && new MicrosoftAuth.Account { Scopes = "Mail.Send Mail.ReadWrite" }.Clean,
                          "Outlook: Sign in with Microsoft asks only to send, plus Mail.ReadWrite only when you turn on Email cleaning");
                    check(OtherMail.PresetFor("me@yahoo.com").Imap == "imap.mail.yahoo.com" && OtherMail.PresetFor("x@icloud.com").Smtp == "smtp.mail.me.com" && OtherMail.PresetFor("a@aol.com").Imap == "imap.aol.com" &&
                          OtherMail.PresetFor("a@walla.co.il").Imap == "imap.walla.co.il" && OtherMail.BetterChoice("x@gmail.com") == "google" && OtherMail.BetterChoice("x@hotmail.com") == "microsoft" &&
                          OtherMail.BetterChoice("x@yahoo.com") == null, "Other email: servers filled in for Yahoo, iCloud, AOL and more; Gmail and Hotmail are sent to their safer sign-in");
                    var okS = new OtherMail.Settings_ { Email = "me@example.com", Password = "abcd efgh", Imap = "imap.example.com", Smtp = "smtp.example.com" };
                    bool qBad = false; try { Imap.Q("a\r\nE9 DELETE INBOX"); } catch (IOException) { qBad = true; }
                    check(OtherMail.Check(okS) == null && OtherMail.Check(new OtherMail.Settings_ { Email = "me@example.com", Password = "p", Imap = "imap.example.com", Smtp = "smtp.example.com", ImapPort = 143 }) != null &&
                          OtherMail.Check(new OtherMail.Settings_ { Email = "me@example.com", Password = "p", Imap = "imap.example.com", Smtp = "smtp.example.com", SmtpPort = 25 }) != null &&
                          OtherMail.Check(new OtherMail.Settings_ { Email = "me@example.com", Password = "p", Imap = "evil host;", Smtp = "smtp.example.com" }) != null &&
                          Imap.Q("pa\"ss\\") == "\"pa\\\"ss\\\\\"" && qBad, "Other email: secure connections only (993 and 587), and nothing typed can sneak extra commands in");
                    string mdir2 = Path.Combine(root, "mail2"); Directory.CreateDirectory(mdir2);
                    GoogleAuth.OverrideDir = mdir2;
                    using (var fk = new FakeImap())
                    {
                        Imap.OpenOverride = (h, p) => fk.Connect();
                        string bad = OtherMail.Connect(new OtherMail.Settings_ { Email = "me@example.com", Password = "wrong", Imap = "imap.example.com", Smtp = "smtp.example.com" });
                        string good = OtherMail.Connect(okS);
                        MailAccount.SetKind("other");
                        bool offFirst = !MailAccount.Cleaning;
                        var on = OtherMail.Load(); on.Clean = true; OtherMail.Save(on);
                        var ocats = MailAccount.Categories();
                        foreach (var c in ocats) MailAccount.Scan(c, System.Threading.CancellationToken.None);
                        Func<string, MailCategory> OC = k => ocats.First(c => c.Key == k);
                        bool readOnly = fk.Commands.Where(x => x.Contains(" UID ")).All(x => !x.Contains("MOVE") && !x.Contains("STORE")) && fk.Commands.Any(x => x.Contains("EXAMINE"));
                        check(bad != null && good == null && offFirst && MailAccount.Email == "me@example.com" && OC("spam").Count == 12 && OC("spam").Folder == "Bulk Mail" &&
                              OC("newsletters").Count == 40 && OC("large").Count == 3 && OC("large").Bytes > 40000000 && OC("newsletters").Senders.Count == 3 && readOnly && !fk.Commands.Any(x => x.Contains("abcd")),
                              "Other email (IMAP): signs in, finds Spam, newsletters and large emails, read-only while scanning, never logs the password");
                        fk.Validity = "8";   // the server renumbered the folders after the check
                        bool renumRefused = false;
                        try { MailAccount.Trash(new List<MailCategory> { OC("spam") }, null, System.Threading.CancellationToken.None); } catch (IOException) { renumRefused = true; }
                        int movedRenum = fk.Moved;
                        fk.Validity = "7";
                        check(renumRefused && movedRenum == 0 && OC("spam").UidValidity == "7",
                              "Other email (IMAP): if the mailbox was renumbered after the check, nothing is moved (the numbers could mean other emails)");
                        int moved = MailAccount.Trash(ocats.Where(c => c.DefaultOn).ToList(), null, System.Threading.CancellationToken.None);
                        check(moved == 52 && fk.Moved == 52 && fk.Boxes["Trash"].Count == 52 && fk.Boxes["INBOX"].Count == 3 && fk.Boxes["Bulk Mail"].Count == 0,
                              "Other email (IMAP): moves only the picked emails to Trash (52), leaves the rest (large emails stay)");
                        MailAccount.Disconnect();
                        check(MailAccount.Email == null && OtherMail.Load() == null, "Other email: Disconnect forgets the app password");
                        Imap.OpenOverride = null;
                    }
                    GoogleAuth.OverrideDir = null;
                    string pick = Path.Combine(root, "smtp-pickup"); Directory.CreateDirectory(pick);
                    using (var msg = OtherMail.Message("me@example.com", "Weekly clean: freed 2 GB", "plain", "<img src=\"cid:chart0@elixar\">", new List<Mailer.Inline> { new Mailer.Inline { Id = "chart0@elixar", Png = TrendChart.Png(new List<SpaceHistory.Point> { new SpaceHistory.Point { Day = DateTime.Today.AddDays(-9), Free = 50L << 30, Total = 500L << 30 }, new SpaceHistory.Point { Day = DateTime.Today, Free = 45L << 30, Total = 500L << 30 } }, DateTime.Today, 56) } }))
                    using (var sc = new System.Net.Mail.SmtpClient("localhost") { DeliveryMethod = System.Net.Mail.SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = pick })
                        sc.Send(msg);
                    string eml = Directory.GetFiles(pick).Select(File.ReadAllText).FirstOrDefault() ?? "";
                    check(eml.Contains("me@example.com") && eml.IndexOf("Content-ID: <chart0@elixar>", StringComparison.OrdinalIgnoreCase) >= 0 && eml.Contains("image/png") && eml.Contains("text/html"),
                          "Other email (SMTP): the report email is built with its chart inside, to your own address only");
                } catch (Exception ex) { check(false, "Other email tests stopped: " + ex.GetType().Name + ": " + ex.Message); Imap.OpenOverride = null; GoogleAuth.OverrideDir = null; }

                // 3t. the Dashboard overview: what it says, what "Free ... safely" picks, and the activity record
                try
                {
                    long G = 1L << 30;
                    Func<long, long, DashSummary> mk = (free, total) => new DashSummary { Free = free, Total = total, Measured = true };
                    check(mk(120 * G, 476 * G).Level == 0 && mk(61 * G, 476 * G).Level == 1 && mk(30 * G, 476 * G).Level == 2 && mk(7 * G, 1000 * G).Level == 2 && mk(30 * G, 120 * G).Level == 0,
                          "Dashboard: says fine / getting full / almost full at the right amount of free space");
                    check(mk(61 * G, 476 * G).Headline == (Win.IsWindows ? "C: is getting full" : "Your system disk is getting full") && new DashSummary().Headline == (Win.IsWindows ? "Measuring drive C:..." : "Measuring your system disk..."), "Dashboard: the headline matches the level");
                    var ms = mk(61 * G, 476 * G); ms.Safe = 34 * G;
                    var notYet = mk(61 * G, 476 * G); notYet.Measured = false; notYet.Safe = 34 * G;
                    var busy = mk(61 * G, 476 * G); busy.Safe = 34 * G; busy.Cleaning = true;
                    var none = mk(61 * G, 476 * G);
                    check(ms.PrimaryEnabled && ms.PrimaryText == "Free " + Fs.Fmt(34 * G) + " safely" && !notYet.PrimaryEnabled && !busy.PrimaryEnabled && !none.PrimaryEnabled && none.PrimaryText == "Nothing to clean now",
                          "Dashboard: \"Free ... safely\" is offered only when measured, not cleaning, and there is something safe to free");
                    var soon = mk(61 * G, 476 * G); soon.Forecast = new SpaceHistory.Forecast { FullOn = DateTime.Today.AddDays(200) };
                    var far = mk(61 * G, 476 * G); far.Forecast = new SpaceHistory.Forecast { FullOn = DateTime.Today.AddDays(900) };
                    check(soon.SubLines.Any(l => l.StartsWith("At this pace ") && l.Contains(" is full around")) && !far.SubLines.Any(l => l.StartsWith("At this pace")),
                          "Dashboard: warns when C: fills within a year, stays quiet otherwise");
                    var pickFrom = new List<CleanItem>
                    {
                        new CleanItem { Name = "a", Tier = Tier.Safe, DefaultOn = true, Size = 5 * G },
                        new CleanItem { Name = "b", Tier = Tier.Safe, DefaultOn = true, Size = 9 * G },
                        new CleanItem { Name = "c", Tier = Tier.Safe, DefaultOn = false, Size = 20 * G },
                        new CleanItem { Name = "d", Tier = Tier.Review, DefaultOn = true, Size = 30 * G },
                        new CleanItem { Name = "e", Tier = Tier.Safe, DefaultOn = true, Privacy = true, Size = 3 * G },
                        new CleanItem { Name = "f", Tier = Tier.Safe, DefaultOn = true, SizeUnknown = true },
                        new CleanItem { Name = "g", Tier = Tier.Caution, DefaultOn = true, Size = 40 * G }
                    };
                    var picked = DashSummary.SafePick(pickFrom);
                    var weekly = new HashSet<string>(Automation.AutoItems().Select(i => i.Name));
                    var realPick = DashSummary.SafePick(Catalog.Build().Select(i => { i.Size = 1 * G; return i; }));
                    check(string.Join(",", picked.Select(i => i.Name)) == "b,a" && realPick.Count > 0 && realPick.All(i => weekly.Contains(i.Name)),
                          "Dashboard: \"Free ... safely\" picks only Safe items ticked by default (the weekly clean's set), never Review, Caution or privacy");
                    check(Ui.Ago(DateTime.Now, DateTime.Now) == "Today" && Ui.Ago(DateTime.Now.AddDays(-1), DateTime.Now) == "Yesterday" && Ui.Ago(DateTime.Now.AddDays(-3), DateTime.Now) == "3 days ago",
                          "Dashboard: says when things happened in plain words");
                    Activity.OverrideFile = Path.Combine(root, "activity.txt");
                    for (int i = 0; i < 70; i++) Activity.Add("clean", "Cleaned item " + i + "\twith\na tab", i);
                    var act = Activity.Read();
                    var recent = Activity.Recent(4, act, new List<Tuple<DateTime, long>> { Tuple.Create(DateTime.Now.AddMinutes(5), 3 * G) },
                                                 new List<UndoBatch> { new UndoBatch { Time = DateTime.Now.AddMinutes(10), Label = "Old item", Bytes = G } }, DateTime.Now.AddMinutes(1), "Weekly summary");
                    check(act.Count == 60 && act.Last().Bytes == 69 && !act.Any(a => a.Text.Contains("\t") || a.Text.Contains("\n")) && recent.Count == 4 &&
                          recent[0].Kind == "undo" && recent[1].Kind == "auto" && recent[2].Kind == "mail" && recent[3].Kind == "clean",
                          "Dashboard: the activity record keeps the newest 60 lines, one per line, and merges weekly cleans, Undo and emails newest first");
                    Activity.OverrideFile = null;
                    bool wasHe = Lang.Hebrew;
                    try
                    {
                        Lang.Hebrew = true;
                        check(Lang.T("Free " + Fs.Fmt(34 * G) + " safely") == "פינוי " + Fs.Fmt(34 * G) + " בבטחה" && Lang.T("Moved to Undo: Startup shortcuts (2) (12 KB)") == "הועבר לביטול: Startup shortcuts (2) (12 KB)" &&
                              Lang.T("C: is getting full") == "C: מתמלא" && Lang.T("Every Sunday") == "כל יום ראשון",
                              "Dashboard: the new texts are in Hebrew too");
                    }
                    finally { Lang.Hebrew = wasHe; }
                } catch (Exception ex) { check(false, "Dashboard overview tests stopped: " + ex.GetType().Name + ": " + ex.Message); Activity.OverrideFile = null; }

                // 3u. the weaknesses found in the October 2026 security review stay closed
                try
                {
                    check(Trust.IsPublisher("Google LLC", Trust.Google) && Trust.IsPublisher("Google Inc.", Trust.Google) && !Trust.IsPublisher("Not Google LLC", Trust.Google) &&
                          !Trust.IsPublisher("Googly Tools Ltd", Trust.Google) && !Trust.IsPublisher("", Trust.Google) && Trust.IsPublisher("Docker, Inc.", Trust.DockerInc) && Trust.IsPublisher("Docker Inc", Trust.DockerInc),
                          "Security: \"signed by Google / Docker\" means exactly that publisher, not any name containing the word");
                    var risky = AppsData.RiskyPrograms("\"C:\\Users\\me\\AppData\\Local\\Tool\\unins000.exe\" /SILENT", p => false);
                    var dll = AppsData.RiskyPrograms("rundll32.exe C:\\Users\\Public\\x\\setup.dll,Uninstall", p => false);
                    var msi = AppsData.RiskyPrograms("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", p => false);
                    var signedOk = AppsData.RiskyPrograms("\"C:\\Users\\me\\AppData\\Local\\Tool\\unins000.exe\"", p => true);
                    string pfx = Win.IsWindows ? Win.ProgramFiles : null;
                    bool pfOk = pfx == null || AppsData.RiskyPrograms("\"" + pfx + "\\Some App\\uninstall.exe\" /S", p => false).Count == 0;
                    check(risky.Count == 1 && dll.Count == 1 && msi.Count == 0 && signedOk.Count == 0 && pfOk,
                          "Security: an unsigned uninstaller in a folder other programs can change is flagged before it runs as administrator");
                    var ucmd = AppsData.UninstallCommand("MsiExec.exe /X{1}");
                    check(!win || string.Equals(ucmd.WorkingDirectory, Environment.GetFolderPath(Environment.SpecialFolder.System), StringComparison.OrdinalIgnoreCase),
                          "Security: uninstall commands start in System32, so a bare program name can't be picked up from another folder");
                    check(Mover.ParseManifest("\"AppState\" { \"name\" \"Evil\" \"installdir\" \"..\\\\..\\\\..\\\\Windows\" }", "D:\\SteamLibrary") == null &&
                          Mover.ParseManifest("\"AppState\" { \"name\" \"Evil\" \"installdir\" \"C:\\\\Windows\" }", "D:\\SteamLibrary") == null &&
                          Mover.ParseManifest("\"AppState\" { \"name\" \"Good\" \"installdir\" \"Good Game\" }", "D:\\SteamLibrary") != null,
                          "Security: a Steam game folder can't point outside its library");
                    check(Redact.Emails("Email reports: connected elisar.test@gmail.com (imap.mail.yahoo.com).") == "Email reports: connected e***@gmail.com (imap.mail.yahoo.com)." &&
                          Redact.Emails("no address here") == "no address here",
                          "Privacy: email addresses are shortened before they reach the log every account can read");
                    string lbl = MainForm.CleanLabel("Photos\u202Egpj.exe\r\n\t" + new string('x', 60));
                    check(lbl.IndexOf('\u202E') < 0 && lbl.IndexOf('\n') < 0 && lbl.Length <= 32, "Security: a USB drive's name can't hide text direction tricks or control characters");
                    Func<string, string> imapFlood = repeat =>
                    {
                        Imap.OpenOverride = (h, p) => new FloodStream(repeat);
                        try { using (Imap.Open(new OtherMail.Settings_ { Email = "a@b.c", Password = "x", Imap = "fake" })) { } return "no error"; }
                        catch (IOException e) { return e.Message; }
                        catch (Exception e) { return e.GetType().Name; }
                        finally { Imap.OpenOverride = null; }
                    };
                    string flood = imapFlood("* 1 EXISTS " + new string('x', 1000) + "\r\n"), huge = imapFlood("* 1 FETCH {99999999999}\r\n");
                    check(flood == "reply too big" && huge == "reply too big", "Security: an email server that never stops answering, or claims an impossible size, is cut off (" + flood + " / " + huge + ")");
                    if (win)
                    {
                        string hp = Path.Combine(root, "hold"), hf = Path.Combine(hp, "inner", "f.txt");
                        Directory.CreateDirectory(Path.GetDirectoryName(hf)); File.WriteAllText(hf, "x");
                        bool blocked = false, freed = false;
                        using (var hold = SafeDelete.HoldParents(hf))
                        {
                            try { Directory.Move(Path.Combine(hp, "inner"), Path.Combine(hp, "swapped")); } catch (IOException) { blocked = true; } catch (UnauthorizedAccessException) { blocked = true; }
                            if (hold == null) blocked = false;
                        }
                        try { Directory.Move(Path.Combine(hp, "inner"), Path.Combine(hp, "swapped")); freed = true; } catch { }
                        check(blocked && freed, "Security: while a duplicate goes to the Recycle Bin, the folders above it can't be swapped for a link");
                    }
                } catch (Exception ex) { check(false, "Security review tests stopped: " + ex.GetType().Name + ": " + ex.Message); Imap.OpenOverride = null; }

                // a pretend second drive for the drive switcher (a folder in the test area)
                string fakeD = Path.Combine(root, "drive-d");
                Directory.CreateDirectory(Path.Combine(fakeD, "Games")); Directory.CreateDirectory(Path.Combine(fakeD, "Photos"));
                File.WriteAllBytes(Path.Combine(fakeD, "Games", "big.bin"), new byte[300000]); File.WriteAllBytes(Path.Combine(fakeD, "Photos", "a.jpg"), new byte[100000]);
                MainForm.DrivesOverride = () => new List<DriveRow> { new DriveRow { Letter = "D", Label = "Test", Root = fakeD, Free = 100L << 30, Total = 200L << 30 } };
                check(DashSummary.LevelOf(100L << 30, 200L << 30) == 0 && DashSummary.LevelOf(30L << 30, 200L << 30) == 1 && DashSummary.LevelOf(5L << 30, 2000L << 30) == 2,
                      "Drives: every drive gets the same fine / getting full / almost full rule");

                bool winUi = Win.IsWindows || Environment.GetEnvironmentVariable("ELIXAR_TEST_WINDOWS_UI") == "1";
                if (!winUi) rep.AppendLine("SKIP  The Windows window checks (this PC runs " + OsInfo.Current.Name + "; the Linux window is checked below)");
                else
                {
                MainForm.ForceWindowsUi = true;
                    // 4. the window, with cleaning switched off
                    string settingsWhy = "", driveWhy = ""; bool navOk = false; string navWhy = ""; bool driveOk = false, overOk = false, uiOk = false, dashOk = false, appsOk = false, storeOk = false, toolsOk = false, phoneOk = false, diskOk = false, settingsOk = false, emailOk = false;
                    string uiErr = null;
                    try
                    {
                        var form = new MainForm(true);
                        int scans = 0;
                        bool changeOk = false, overDone = false, driveStarted = false, driveDone = false, dashDone = false, appsDone = false, storeDone = false, undoDone = false, phoneDone = false, startDone = false, moveDone = false, diskDone = false;
                        bool closing = false;
                        Action maybeClose = () =>
                        {
                            if (closing) return;
                            if (scans >= 2 && dashDone && appsDone && storeDone && undoDone && phoneDone && startDone && moveDone && diskDone && overDone && driveDone)
                            {
                                toolsOk = true;
                                storeOk = !win || form.StoreCount > 0;
                                dashOk = form.DashboardFilled();
                                appsOk = !win || form.AppsCount > 0;
                                uiOk = changeOk;
                                phoneOk = form.PhoneRows > 10;
                                diskOk = !win || form.DiskRows > 0;
                                settingsOk = form.SettingsTabReady;
                                navOk = form.NavGreyed.SequenceEqual(new[] { "Linux" }) && form.NavRight == "Settings" && !form.TabNames.Contains("Linux") && form.NavEnabled.Contains("Apps") && form.NavEnabled.Contains("Phone");
                                if (!navOk) navWhy = "  (greyed " + string.Join(",", form.NavGreyed) + ", right " + form.NavRight + ")";
                                if (!settingsOk) settingsWhy = "  (" + form.SettingsState + ")";
                                emailOk = form.EmailRows == 6;
                                closing = true;
                                form.Close();
                            }
                        };
                        form.MeasureDone += () =>
                        {
                            scans++;
                            if (scans == 1) form.Rescan();                       // second scan compares with the first
                            else { changeOk = form.ChangeColumnFilled(); maybeClose(); }
                        };
                        form.DashboardDone += () => { dashDone = true; maybeClose(); };
                        form.OverviewDone += () =>
                        {
                            if (form.OverviewFilled) { overDone = true; overOk = true; }
                            if (!driveStarted && form.DriveChoices == 3) { driveStarted = true; form.SelectDrive("D"); }
                            maybeClose();
                        };
                        form.DriveViewDone += () =>
                        {
                            if (driveDone) return;
                            int folders = form.DriveFolderRows;
                            form.SelectDrive("*"); int cards = form.AllDriveCards;
                            form.SelectDrive("C");
                            driveOk = folders == 2 && cards == (Sys.Total() > 0 ? 2 : 1) && form.CurrentDrive == "C";   // C: shows when this PC has one
                            if (!driveOk) driveWhy = "  (folders " + folders + ", cards " + cards + ", now on " + form.CurrentDrive + ")";
                            driveDone = true; maybeClose();
                        };
                        form.AppsDone += () => { appsDone = true; maybeClose(); };
                        form.StoreDone += () => { storeDone = true; maybeClose(); };
                        form.UndoDone += () => { undoDone = true; maybeClose(); };
                        form.UndoDone += () => { if (!phoneDone) form.ShowPhone(); };
                        form.PhoneDone += () => { phoneDone = true; if (!startDone) form.ShowStartup(); };
                        form.StartupDone += () => { startDone = true; if (!moveDone) form.ShowMove(); };
                        form.MoveDone += () => { moveDone = true; form.ShowDiskHealth(); };
                        form.DiskHealthDone += () => { diskDone = true; maybeClose(); };
                        form.StoreDone += () => form.ShowUndo();
                        form.Shown += (o, e) => { form.RefreshApps(); form.ShowStoreTab(); };
                        var timeout = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
                        timeout.Tick += (o, e) => { timeout.Stop(); form.Close(); };
                        timeout.Start();
                        Application.Run(form);
                        timeout.Dispose();
                    }
                    catch (Exception ex) { uiErr = ex.GetType().Name + ": " + ex.Message; }
                    check(uiOk, "Window opens, scans, rescans with the Change column filled, and closes" + (uiErr != null ? "  ERROR: " + uiErr : ""));
                    check(dashOk, "Dashboard measures every location and fills the cards");
                    check(overOk, "Dashboard overview fills in: the ring, Free ... safely, the six tiles, quick wins, drives and activity");
                    check(driveOk, "Drives: switching to another drive measures its folders, All drives shows a card per drive, and C: comes back" + driveWhy);
                    MainForm.DrivesOverride = null;
                    check(appsOk, "Apps tab reads and measures installed apps");
                    check(storeOk, "Built-in and Store apps list opens and fills in");
                    check(toolsOk, "Tools tab opens and the Undo list loads");
                    check(phoneOk, "Phone tab finds the (simulated) phone and lists what to clean and its apps");
                    check(diskOk, "Disk health tab opens and lists the drives");
                    check(settingsOk, "Settings tab shows the saved language and theme, ready to switch" + settingsWhy);
                    check(navOk, "Tab bar: Settings sits on the right, and the Linux tab is greyed out on Windows" + navWhy);
                    check(emailOk, "Email tab lists what it can clean in Gmail (off until you allow it)");
                    Adb.Exe = null; Adb.Prefix = "";
                MainForm.ForceWindowsUi = false;
                }


                // 4L. Linux (and which system this is): everything Elixar does differently on Linux
                try
                {
                    var ubuntu = OsInfo.ReadLinux(new[] { "PRETTY_NAME=\"Ubuntu 24.04.5 LTS\"", "NAME=\"Ubuntu\"", "VERSION_ID=\"24.04\"", "ID=ubuntu", "ID_LIKE=debian" });
                    var mint = OsInfo.ReadLinux(new[] { "NAME=\"Linux Mint\"", "VERSION_ID=\"22\"", "ID=linuxmint", "ID_LIKE=\"ubuntu debian\"" });
                    var fedora = OsInfo.ReadLinux(new[] { "PRETTY_NAME=\"Fedora Linux 41 (Workstation Edition)\"", "ID=fedora" });
                    var arch = OsInfo.ReadLinux(new[] { "PRETTY_NAME=\"Arch Linux\"", "ID=arch" });
                    var junk = OsInfo.ReadLinux(new[] { "garbage", "=x" });
                    check(ubuntu.Name == "Ubuntu 24.04.5 LTS" && ubuntu.Family == "debian" && ubuntu.Supported && mint.Name == "Linux Mint 22" && mint.Family == "debian" &&
                          fedora.Family == "fedora" && !fedora.Supported && arch.Family == "arch" && junk.Name == "Linux" && junk.Family == "other",
                          "System: recognises Ubuntu, Linux Mint, Fedora and Arch from /etc/os-release");
                    check(OsInfo.FromWindows("Windows 10 Pro", "24H2", 26100, 2033).Name == "Windows 11 Pro 24H2 (build 26100.2033)" &&
                          OsInfo.FromWindows("Windows 10 Home", "22H2", 19045, 0).Name == "Windows 10 Home 22H2 (build 19045)",
                          "System: tells Windows 11 from Windows 10 by its build number (Windows still calls 11 \"Windows 10\")");
                    check(OsInfo.Current.Name.Length > 0, "System: this PC runs " + OsInfo.Current.Name);
                    {
                        string dr = Path.Combine(root, "drv"); Directory.CreateDirectory(dr);
                        foreach (var n in new[] { "found.000", "found.001", "found.abc", "found.0001", "foundling" }) Directory.CreateDirectory(Path.Combine(dr, n));
                        var lo = DriveClean.CheckDiskLeftovers(dr).Select(Path.GetFileName).OrderBy(x => x).ToList();
                        bool steamOk = true;
                        if (win)
                        {
                            string steam = Path.Combine(dr, "Steam"); Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
                            string lib = Path.Combine(dr, "Games"); Directory.CreateDirectory(Path.Combine(lib, "steamapps"));
                            string other = Path.Combine(root, "elsewhere"); Directory.CreateDirectory(Path.Combine(other, "steamapps"));
                            File.WriteAllText(Path.Combine(Path.Combine(steam, "steamapps"), "libraryfolders.vdf"),
                                "\"libraryfolders\" { \"0\" { \"path\" \"" + steam.Replace("\\", "\\\\") + "\" } \"1\" { \"path\" \"" + lib.Replace("\\", "\\\\") + "\" } \"2\" { \"path\" \"" + other.Replace("\\", "\\\\") + "\" } \"3\" { \"path\" \"C:\\\\Windows\" } }");
                            var libs = DriveClean.SteamLibrariesOn(dr + "\\", steam);
                            steamOk = libs.Count == 1 && libs[0] == lib;
                        }
                        check(lo.SequenceEqual(new[] { "found.000", "found.001" }) && steamOk &&
                              !Duplicates.Allowed(@"D:\SteamLibrary\steamapps\common\Game") && !Duplicates.Allowed(@"D:\Windows.old\x") && !Duplicates.Allowed(@"E:\Epic Games\Fortnite") &&
                              Duplicates.Allowed(@"D:\Photos\2024") && (!win || (Duplicates.SearchRoot(@"C:\")[0] == UserDirs.Home && Duplicates.SearchRoot(@"D:\")[0] == @"D:\")),
                              "Other drives: finds disk-check leftovers and Steam libraries on that drive only; duplicates skip game folders" + (win ? "" : " (Steam part on Windows only)"));
                        check(Catalog.Build().Where(i => i.Drive != null).All(i => !i.DefaultOn && i.Info.Length > 0) && Automation.AutoItems().All(i => i.Drive == null),
                              "Other drives: nothing there is ticked for you, and the weekly clean never touches other drives");
                    }
                    {
                        string sm = Path.Combine(root, "smart"); Directory.CreateDirectory(Path.Combine(sm, "Photos")); Directory.CreateDirectory(Path.Combine(sm, "Backup"));
                        Directory.CreateDirectory(Path.Combine(sm, "SteamLibrary", "steamapps", "common")); Directory.CreateDirectory(Path.Combine(sm, "Old Windows", "Windows", "System32"));
                        var pic = new byte[(3 << 19)]; new Random(9).NextBytes(pic);                 // 1.5 MB photo, twice
                        File.WriteAllBytes(Path.Combine(sm, "Photos", "IMG_1.jpg"), pic); File.WriteAllBytes(Path.Combine(sm, "Backup", "IMG_1 (copy).jpg"), pic);
                        var big = new byte[11 << 20];
                        string oldF = Path.Combine(sm, "Backup", "old-movie.mkv"), newF = Path.Combine(sm, "Photos", "new-movie.mp4"), game = Path.Combine(sm, "SteamLibrary", "steamapps", "common", "game.pak");
                        File.WriteAllBytes(oldF, big); big[0] = 1; File.WriteAllBytes(newF, big); big[0] = 2; File.WriteAllBytes(game, big);
                        File.SetCreationTime(oldF, DateTime.Now.AddYears(-2)); File.SetLastWriteTime(oldF, DateTime.Now.AddYears(-2)); File.SetLastAccessTime(oldF, DateTime.Now.AddYears(-2));
                        var sr = SmartScan.Run(sm, null, System.Threading.CancellationToken.None, true);
                        var olds = sr.OlderThan(DateTime.Now.AddMonths(-6), SmartScan.OldMin);
                        var g0 = sr.Dups.FirstOrDefault();
                        check(sr.Dups.Count == 1 && g0.Files.Count == 2 && olds.Count == 1 && olds[0].File.Name == "old-movie.mkv" && olds[0].Kind == "Video" &&
                              !sr.Candidates.Any(c => c.File.Name == "game.pak") && SmartScan.HasWindows(Path.Combine(sm, "Old Windows")) && !SmartScan.HasWindows(sm) &&
                              SmartScan.KindOf("a.ISO") == "Disk image" && SmartScan.KindOf("x.unknown") == "Other" &&
                              SmartScan.Keeper(g0, SmartScan.Keep.ShortestPath).Name == "IMG_1.jpg",
                              "Smart scan: finds duplicates and big files unused for 6+ months in one pass, skips game folders, and knows a drive with its own Windows");
                    }
                    check(DiskNumber.DiskOfDevice("/dev/nvme0n1p2") == "nvme0n1" && DiskNumber.DiskOfDevice("/dev/sda1") == "sda" && DiskNumber.DiskOfDevice("/dev/mmcblk0p1") == "mmcblk0" &&
                          DiskNumber.DiskOfDevice("/dev/mapper/vg-root") == "" && DiskNumber.Host.Length > 0 && !DiskNumber.Host.Contains(" ") &&
                          (!win || System.Text.RegularExpressions.Regex.IsMatch(DiskNumber.Of("C"), "^[0-9]+$")) && DiskNumber.Tag(win ? "C" : "/").EndsWith(" of " + DiskNumber.Host) == (DiskNumber.Of(win ? "C" : "/").Length > 0),
                          "Dashboard: shows which disk a drive is on and this PC's name (" + DiskNumber.Tag(win ? "C" : "/") + ")");

                    var mts = Lx.ParseMounts(new[] { "/dev/sda2 / ext4 rw 0 0", "/dev/sda3 /home ext4 rw 0 0", "proc /proc proc rw 0 0", "/dev/loop3 /snap/firefox/123 squashfs ro 0 0",
                                                     "/dev/sdb1 /media/elisar/My\\040Backup exfat rw 0 0", "/dev/nvme0n1p1 /boot/efi vfat rw 0 0", "tmpfs /run tmpfs rw 0 0" });
                    var data = Lx.DataMounts(mts);
                    check(data.Count == 2 && data[0].Point == "/home" && data[1].Point == "/media/elisar/My Backup" && Lx.RemovableMount(data[1].Point) && !Lx.RemovableMount("/home"),
                          "Linux: finds your other disks (a /home partition, a USB drive with a space in its name), never /proc, /boot or the snap images");
                    var snaps = Lx.OldSnaps("Name     Version   Rev    Tracking       Publisher   Notes\nfirefox  130.0     4793   latest/stable  mozilla**   disabled\nfirefox  131.0     4848   latest/stable  mozilla**   -\n" +
                                            "evil;rm  1         12     latest/stable  x           disabled\ncore22   2024      1621   latest/stable  canonical** base,disabled\nbad      1         12a    latest/stable  x           disabled\n");
                    check(snaps.Count == 2 && snaps[0].Item1 == "firefox" && snaps[0].Item2 == "4793" && snaps[1].Item1 == "core22" && !Lx.SnapName("x$(reboot)") && !Lx.Digits("1;2"),
                          "Linux: only old Snap versions with plain names and numbers go into the root command (a name like \"evil;rm\" is refused)");
                    check(Lx.ParseSize("Archived and active journals take up 1.5G in the file system.") == (long)(1.5 * (1L << 30)) && Lx.ParseSize("nothing") == -1,
                          "Linux: reads the size of the system journal");
                    var allowed = new HashSet<string> { "apt-clean", "journal-vacuum", "crash-clean", "flatpak-unused", "apt-autoremove" };
                    var lcat = LinuxCatalog.Build();
                    check(lcat.Any(i => i.Name == "Thumbnail cache") && lcat.Where(i => i.RootScript != null).All(i => allowed.Contains(i.RootScript)) &&
                          lcat.Where(i => i.RootScript != null).All(i => i.Info.Contains("password")),
                          "Linux: everything that runs as root is one of a few fixed commands written in Elixar, and says it asks for your password");
                    var dfu = LxSystem.ParseDf("Images|14.2GB|9.8GB (69%)\nContainers|310MB|120MB (38%)\nLocal Volumes|3.1GB|0B (0%)\nBuild Cache|6.4GB|6.4GB\nnoise");
                    check(dfu.Images == 14200000000L && dfu.ImagesFree == 9800000000L && dfu.ContainersFree == 120000000L && dfu.Volumes == 3100000000L && dfu.CacheFree == 6400000000L &&
                          LxSystem.DockerSize("1.2kB") == 1200 && LxSystem.DockerSize("0B") == 0 && LxSystem.DockerSize("junk") == 0,
                          "Docker: reads what images, containers, volumes and the build cache take (\"docker system df\")");
                    string ds = LxSystem.DockerScript("/usr/bin/docker", false), da = LxSystem.DockerScript("/snap/bin/docker", true);
                    check(ds == "docker-safe:/usr/bin/docker" && da == "docker-all:/snap/bin/docker" &&
                          Lx.Fallback(ds) == "/usr/bin/docker builder prune -f && /usr/bin/docker image prune -f" && Lx.Fallback(da) == "/snap/bin/docker container prune -f && /snap/bin/docker image prune -a -f" &&
                          LxSystem.DockerScript("/tmp/docker", false) == null && LxSystem.DockerScript("/usr/bin/docker; rm -rf /", true) == null && LxSystem.DockerScript(null, true) == null &&
                          !(Lx.Fallback(ds) + Lx.Fallback(da)).Contains("volume") && !(Lx.Fallback(ds) + Lx.Fallback(da)).Contains("system prune"),
                          "Docker: only fixed prune commands from the real docker, and never volumes (where databases live)");
                    check(LxSystem.CapScript("500M") == "journal-cap:500M" && Lx.Fallback("journal-cap:500M").Contains("SystemMaxUse=500M") && Lx.Fallback("journal-cap:500M").Contains("/etc/systemd/journald.conf.d/60-elixar.conf") &&
                          LxSystem.CapScript("200M") != null && LxSystem.CapScript("1G") != null && LxSystem.CapScript("100M") == null && LxSystem.CapScript("500M; rm -rf /") == null && LxSystem.CapScript(null) == null &&
                          LxSystem.UncapScript == "journal-uncap" && LxSystem.CapText("500M") == "500 MB" && LxSystem.CapText("1G") == "1 GB",
                          "System logs: the size limit is one of 200 MB, 500 MB or 1 GB, written to Elixar's own settings file");
                    string rout;
                    check(Lx.RootAction("apt-clean") && Lx.RootAction("snap-remove:firefox:4793") && Lx.RootAction("journal-cap:1G") && Lx.RootAction("docker-all:/usr/bin/docker") &&
                          !Lx.RootAction("apt-get clean") && !Lx.RootAction("snap-remove:x$(id):1") && !Lx.RootAction("snap-remove:firefox:12a") && !Lx.RootAction("snap-remove:a:1:2") &&
                          !Lx.RootAction("journal-cap:100M") && !Lx.RootAction("docker-safe:/tmp/docker") && !Lx.RootAction("") && !Lx.RootAction(null) &&
                          Lx.RunAsRoot("apt-clean reboot", out rout) == -1 && Lx.RunAsRoot("", out rout) == -1 && Lx.RunAsRoot("apt-clean;id", out rout) == -1,
                          "Root actions: Elixar asks only for its own named actions; anything else is refused before the password prompt");
                    if (Lx.On && Lx.HelperTrusted(Lx.Helper))
                    {
                        string h1, h2, h3;
                        int good = Lx.Run(Lx.Helper, "--dry-run journal-vacuum journal-uncap", 10000, out h1);
                        int bad1 = Lx.Run(Lx.Helper, "--dry-run apt-clean snap-remove:x;id:1", 10000, out h2);
                        int bad2 = Lx.Run(Lx.Helper, "--dry-run journal-cap:500M;reboot", 10000, out h3);
                        check(good == 0 && h1.Contains("--vacuum-size=200M") && bad1 == 2 && bad2 == 2 && !h2.Contains("apt-get"),
                              "Root helper (installed): runs only its fixed actions, and refuses everything if one argument is wrong");
                    }
                    else if (Lx.On) rep.AppendLine("SKIP  Root helper: not installed here (it comes with the .deb); the same fixed commands run by full path instead");
                    check(!Lx.HelperTrusted(Path.Combine(root, "nope")), "Root helper: a missing or user-owned helper is never used");
                    if (Lx.On)
                    {
                        string hm = Lx.Home.TrimEnd('/');
                        check(!Lx.SafeUserDir("/") && !Lx.SafeUserDir(hm) && !Lx.SafeUserDir(hm + "/") && !Lx.SafeUserDir(Path.GetDirectoryName(hm)) && !Lx.SafeUserDir("/usr/share") &&
                              !Lx.SafeUserDir("relative/.cache") && !Lx.SafeUserDir(hm + "/../..") && !Lx.SafeUserDir("") && Lx.SafeUserDir(hm + "/.cache") && Lx.SafeUserDir(hm + "/my cache"),
                              "Linux: a wrong XDG_CACHE_HOME (/, your home, a system folder) is ignored, so cleaning never means everything you own");
                        string lk2 = Path.Combine(root, "lockme"); Directory.CreateDirectory(lk2);
                        bool l1 = Locked.Lock(lk2) && Locked.IsLocked(lk2);
                        string m2; Lx.Run("chmod", "777 " + Lx.Arg(lk2), 5000, out m2);
                        bool l2 = !Locked.IsLocked(lk2);
                        string ln2 = Path.Combine(root, "lockme-link"); Lx.Run("ln", "-s " + Lx.Arg(lk2) + " " + Lx.Arg(ln2), 5000, out m2);
                        check(l1 && l2 && !Locked.IsLocked(ln2) && !Locked.OwnedByAdmins(ln2) && !Locked.IsLocked(Path.Combine(root, "missing")),
                              "Linux: a folder counts as locked only when it is yours (or root's), not a link, and nobody else can change it");
                        check(Quarantine.RootFor(0).StartsWith(Paths.Data) && Locked.IsLocked(Quarantine.RootFor(0)),
                              "Linux: Undo keeps files only in Elixar's private folder (never on a USB drive or a disk's top level)");
                    }
                    {
                        var k1 = new byte[64]; var k2 = new byte[64]; new Random(5).NextBytes(k1); new Random(6).NextBytes(k2);
                        var en = Encoding.UTF8.GetBytes("Elixar mail account v2"); var secret = Encoding.UTF8.GetBytes("{\"refresh\":\"1//secret-token\"}");
                        var box = SecretBox.Seal(secret, en, k1); var box2 = SecretBox.Seal(secret, en, k1);
                        var bent = (byte[])box.Clone(); bent[20] ^= 1;
                        Func<Func<byte[]>, bool> refused = f => { try { f(); return false; } catch (System.Security.Cryptography.CryptographicException) { return true; } };
                        check(SecretBox.IsSealed(box) && !Encoding.UTF8.GetString(box).Contains("secret-token") && !box.SequenceEqual(box2) &&
                              SecretBox.Open(box, en, k1).SequenceEqual(secret) && refused(() => SecretBox.Open(bent, en, k1)) && refused(() => SecretBox.Open(box, en, k2)) &&
                              refused(() => SecretBox.Open(box, Encoding.UTF8.GetBytes("other"), k1)) && !SecretBox.IsSealed(secret),
                              "Sign-in file: sealed with AES-256 + HMAC; a changed byte, another key or another purpose is refused");
                        if (Lx.On)
                        {
                            SecretBox.KeyOverride = () => k1;
                            try { check(SecretBox.Unprotect(SecretBox.Protect(secret, en), en).SequenceEqual(secret) && SecretBox.Unprotect(secret, en).SequenceEqual(secret),
                                        "Linux: the sign-in is encrypted with the keyring key, and an older plain file still opens (and is sealed on the next read)"); }
                            finally { SecretBox.KeyOverride = null; }
                            if (SecretBox.KeyringReady) check(SecretBox.Unprotect(SecretBox.Protect(secret, en), en).SequenceEqual(secret), "Linux: this desktop's keyring holds Elixar's key and it works");
                            else rep.AppendLine("SKIP  Linux keyring: none on this PC (the sign-in file stays readable only by your account)");
                        }
                    }
                    string sd = Path.Combine(root, "snaps"); Directory.CreateDirectory(sd);
                    foreach (var sn in new[] { "firefox_4793.snap", "firefox_4848.snap", "core22_1621.snap", "evil;x_1.snap", "notes.txt", "code_x1.snap" }) File.WriteAllBytes(Path.Combine(sd, sn), new byte[sn.Length]);
                    var sl = LxSystem.Snaps(sd);
                    check(sl.Count == 3 && sl[0].Name == "firefox" && sl[0].Versions == 2 && sl[0].Bytes == 34 && sl.Any(x => x.Name == "code") && !sl.Any(x => x.Name.Contains(";")),
                          "Snap: adds up every version of each app");
                    check(LxSystem.SnapsLarge(11L << 30, 1L << 40) && LxSystem.SnapsLarge(30L << 30, 256L << 30) && !LxSystem.SnapsLarge(5L << 30, 256L << 30) && !LxSystem.SnapsLarge(0, 0) &&
                          LxSystem.SnapWarnDue("", DateTime.Today) && !LxSystem.SnapWarnDue(DateTime.Today.AddDays(-3).ToString("yyyy-MM-dd"), DateTime.Today) &&
                          LxSystem.SnapWarnDue(DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd"), DateTime.Today) &&
                          new DashSummary { Total = 256L << 30, Measured = true, SnapBytes = 30L << 30 }.SubLines.Any(l => l.StartsWith("Snap apps take")) &&
                          !new DashSummary { Total = 256L << 30, Measured = true, SnapBytes = 3L << 30 }.SubLines.Any(l => l.StartsWith("Snap apps take")),
                          "Snap: warns when Snap apps take over 10 GB or a tenth of the disk (on the Dashboard, and at most once a week as a notification)");
                    string mono = "/usr/bin/mono";
                    var cu = LxTimers.Units(Automation.CreateCleanArgs("/usr/lib/elixar/Elixar.exe", "Sunday", "20:00"), mono);
                    var ck = LxTimers.Units(Automation.CreateCheckArgs("/usr/lib/elixar/Elixar.exe", 15, "12:00"), mono);
                    var lg = LxTimers.Units(Automation.CreateLogonArgs("/usr/lib/elixar/Elixar.exe", 15), mono);
                    check(cu != null && cu[0].Contains("ExecStart=/usr/bin/mono \"/usr/lib/elixar/Elixar.exe\" --auto-clean\n") && cu[1].Contains("OnCalendar=Sun *-*-* 20:00:00") &&
                          ck != null && ck[0].Contains("--check-space 15") && ck[1].Contains("OnCalendar=*-*-* 12:00:00") && lg != null && lg[1].Contains("OnStartupSec=3min") && !cu[0].Contains("root"),
                          "Linux: the weekly clean and space check become systemd timers for your account");
                    check(LxTimers.Units(Automation.CreateCleanArgs("Elixar.exe", "Sunday", "20:00"), mono) == null &&
                          LxTimers.Units(Automation.CreateCleanArgs("/tmp/a%h/Elixar.exe", "Sunday", "20:00"), mono) == null &&
                          LxTimers.Units("/Create /F /TN \"x\" /TR \"\\\"/usr/lib/elixar/Elixar.exe\\\" --auto-clean; rm -rf ~\" /SC DAILY /ST 12:00", mono) == null &&
                          LxTimers.Units(Automation.CreateCleanArgs("/usr/lib/elixar/Elixar.exe", "Funday", "20:00"), mono) == null,
                          "Linux: a schedule is written only for Elixar's own commands (no relative paths, systemd specials, extra commands or odd days)");
                    if (Lx.On)
                    {
                        string oldCfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                        try
                        {
                            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "xdg"));
                            string o1, o2;
                            LxTimers.Apply(Automation.CreateCleanArgs("/usr/lib/elixar/Elixar.exe", "Sunday", "20:00"), out o1);
                            var q = LxTimers.Query(Automation.CleanTask);
                            bool made = q.Exists && q.Day == "Sunday" && q.Start.HasValue && q.Start.Value.Hour == 20 && q.Arguments == "--auto-clean" && q.Command == "/usr/lib/elixar/Elixar.exe";
                            LxTimers.Apply(Automation.DeleteArgs(Automation.CleanTask), out o2);
                            check(made && !LxTimers.Query(Automation.CleanTask).Exists, "Linux: a saved schedule reads back the same (day, time, command) and Off removes it");
                        }
                        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", oldCfg); }
                        string pd = Path.Combine(root, "private"); Directory.CreateDirectory(pd);
                        string mode; Lx.Private(pd, true); Lx.Run("stat", "-c %a " + Lx.Arg(pd), 5000, out mode);
                        check(mode.Trim() == "700", "Linux: Elixar's own folders (sign-ins, logs) can be opened only by you (700)");
                        check(Lx.SkipDir("/proc", "/") && Lx.SkipDir("/sys", "/") && !Lx.SkipDir("/usr", "/") && !Lx.SkipDir("/", "/"),
                              "Linux: measuring the system disk never walks into /proc, /sys or other disks");
                        string t1, r1, t2, r2, t3, r3;
                        Scanner.Classify("/var/cache/apt/archives/x.deb", out t1, out r1); Scanner.Classify("/usr/lib/x/big.so", out t2, out r2); Scanner.Classify("/home/u/Videos/a.mkv", out t3, out r3);
                        check(t1 == "safe" && t2 == "critical" && t3 == "review", "Linux: largest files are sorted into Safe / Review / Critical the Linux way");
                        check(Automation.AutoItems().Count == 0 || Catalog.WindowsOnLinux || Automation.AutoItems().All(i => i.RootScript == null),
                              "Linux: the weekly clean never runs anything that needs your password");

                        // the Linux window itself
                        bool wasWin = Catalog.WindowsOnLinux;
                        Catalog.WindowsOnLinux = false; MainForm.ForceWindowsUi = false;
                        bool lxOk = false; string lxWhy = "";
                        try
                        {
                            var lf = new MainForm(true);
                            bool measured = false, over = false;
                            Action done = () =>
                            {
                                if (!measured || !over) return;
                                var names = lf.TabNames; var items2 = lf.CleanerItemNames;
                                lxOk = !names.Contains("Apps") && !names.Contains("Phone") && names.Contains("Email") && names.Contains("Automation") && names.Contains("Linux") &&
                                       lf.NavGreyed.SequenceEqual(new[] { "Apps", "Phone" }) && lf.NavRight == "Settings" && lf.NavEnabled.Contains("Linux") &&
                                       items2.Contains("Thumbnail cache") && !items2.Contains("Windows Update downloads") && lf.OverviewFilled;
                                if (!lxOk) lxWhy = "  (tabs: " + string.Join(", ", names) + "; greyed: " + string.Join(", ", lf.NavGreyed) + "; items: " + items2.Count + ")";
                                lf.Close();
                            };
                            lf.MeasureDone += () => { measured = true; done(); };
                            lf.OverviewDone += () => { if (lf.OverviewFilled) { over = true; done(); } };
                            var to = new System.Windows.Forms.Timer { Interval = 3 * 60 * 1000 };
                            to.Tick += (o, e) => { to.Stop(); lxWhy = "  (timed out)"; lf.Close(); };
                            to.Start();
                            Application.Run(lf);
                            to.Dispose();
                        }
                        catch (Exception ex) { lxWhy = "  ERROR: " + ex.Message; }
                        Catalog.WindowsOnLinux = wasWin;
                        check(lxOk, "Linux: the window shows the Linux tab (Apps and Phone greyed out, Settings on the right), the cleaning list, and the Dashboard fills in" + lxWhy);
                    }
                } catch (Exception ex) { check(false, "Linux tests stopped: " + ex.GetType().Name + ": " + ex.Message); }

                // 5. drill-down window on the test folder
                bool drillOk = false;
                try
                {
                    string dr = Path.Combine(root, "drill");
                    var df = new DrillForm(dr, 1f, null);
                    df.Measured += () => { drillOk = df.RowCount == 4 && df.AllMeasured; df.Close(); };
                    var t2 = new System.Windows.Forms.Timer { Interval = 60000 };
                    t2.Tick += (o, e) => { t2.Stop(); df.Close(); };
                    t2.Start();
                    Application.Run(df);
                    t2.Dispose();
                }
                catch (Exception ex) { check(false, "Drill-down window: " + ex.Message); }
                check(drillOk, "Drill-down window opens a folder and measures everything in it");
            }
            catch (Exception ex)
            {
                check(false, "Unexpected error: " + ex);
            }
            finally
            {
                try { Fs.Delete(root); } catch { }
                Catalog.WindowsOnLinux = false; MainForm.ForceWindowsUi = false;
            }

            rep.AppendLine();
            rep.AppendLine(fails == 0 ? "RESULT: ALL TESTS PASSED" : "RESULT: " + fails + " TEST(S) FAILED");
            string report = rep.ToString();
            // next to the program, written through a checked handle (as administrator, a file there could be a planted link);
            // a copy also goes to Elixar's locked data folder
            string outPath = Win.IsWindows ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest-report.txt") : Paths.FileIn("selftest-report.txt");
            try { SafeFile.WriteText(outPath, report); }
            catch { outPath = Paths.FileIn("selftest-report.txt"); }
            if (Win.IsWindows && Sys.IsAdmin()) try { File.WriteAllText(Paths.FileIn("selftest-report.txt"), report); } catch { }
            Console.WriteLine(report);
            if (showResult)
                MessageBox.Show((fails == 0 ? "All tests passed." : fails + " test(s) failed:\n\n- " + string.Join("\n- ", failed.Select(f => f.Length > 160 ? f.Substring(0, 157) + "..." : f))) + "\n\nReport saved to:\n" + outPath,
                    "Elixar self-test", MessageBoxButtons.OK, fails == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return fails;
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            SelfImage.Hold();          // first: nobody can rename or replace this Elixar.exe while it runs
            Install.HardenDllSearch();
            Theme.Init(Settings.ThemeMode);
            Lang.Init(Settings.Language);
            // the pretend phone used by the self-test only (never answers outside a self-test run)
            if (args.Length > 0 && args[0] == "--fake-adb" && Environment.GetEnvironmentVariable("ELIXAR_SELFTEST") == "1") return FakeAdb.Run(args.Skip(1).ToArray());
            if (args.Contains("--selftest"))
            {
                Sys.TryDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return SelfTest.Run(!args.Contains("--quiet"));
            }
            int ci = Array.IndexOf(args, "--check-space");
            if (ci >= 0)
            {
                int pct = 15;
                if (ci + 1 < args.Length) int.TryParse(args[ci + 1], out pct);
                Application.EnableVisualStyles();
                return Automation.CheckSpace(Math.Max(1, Math.Min(99, pct)));
            }
            if (args.Contains("--test-alert"))
            {
                Application.EnableVisualStyles();
                Automation.Notify("Test alert from Elixar",
                    "This is how the low-space alert looks. " + Fs.Fmt(Sys.Free()) + " is free on C: right now.", ToolTipIcon.Info, false);
                return 0;
            }
            if (args.Contains("--mail-daily") || args.Contains("--auto-clean")) Install.HideLauncherWindow();
            if (args.Contains("--mail-daily"))
            {
                Application.EnableVisualStyles();
                return MailReports.Daily();
            }
            if (args.Contains("--auto-clean"))
            {
                Application.EnableVisualStyles();
                return Automation.AutoClean(true);
            }

            bool testUi = args.Contains("--test");
            bool noElevate = testUi || args.Contains("--no-elevate") || !Win.IsWindows;
            if (Lx.IsRoot && !testUi)
            {
                // a window running as root could be told to do anything; Elixar asks for your password only for its fixed system commands
                Application.EnableVisualStyles();
                MessageBox.Show("Please start Elixar without sudo.\n\nIt runs as you and asks for your password only when a system clean needs it.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            if (!noElevate && !Sys.IsAdmin())
            {
                try
                {
                    // the protected copy is preferred when it is the same version or newer: only administrators can change it,
                    // and it carries no "downloaded" mark, so Windows doesn't show its SmartScreen box for it
                    string self = Application.ExecutablePath;
                    try
                    {
                        if (Install.IsInstalled && !Install.RunningInstalled && Locked.IsLocked(Install.Exe) &&
                            new Version(FileVersionInfo.GetVersionInfo(Install.Exe).FileVersion) >= new Version(FileVersionInfo.GetVersionInfo(self).FileVersion))
                            self = Install.Exe;
                    }
                    catch { }
                    // keep --install / --uninstall when asking for admin rights
                    string pass = string.Join(" ", args.Where(x => x == "--install" || x == "--uninstall" || x == "--quiet"));
                    Process.Start(new ProcessStartInfo(self, pass) { UseShellExecute = true, Verb = "runas" });
                }
                catch (Win32Exception)
                {
                    MessageBox.Show("Elixar needs administrator rights to clean Windows folders.\n\nStart it again and click Yes on the Windows prompt.",
                        "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }

            int au = Array.IndexOf(args, "--apply-update");
            if (au >= 0 && au + 2 < args.Length) { int pid; int.TryParse(args[au + 2], out pid); return Updates.Apply(args[au + 1], pid); }
            if (args.Contains("--uninstall")) { Application.EnableVisualStyles(); return Setup.Uninstall(args.Contains("--quiet")); }
            if (args.Contains("--install"))
            {
                Application.EnableVisualStyles();
                string err = Setup.InstallHere(null);
                MessageBox.Show(err == null ? "Elixar is installed: find it in the Start menu. It also appears in Windows' installed apps list." : "Could not install Elixar: " + err, "Elixar",
                    MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (err == null) try { Process.Start(new ProcessStartInfo(Install.Exe) { UseShellExecute = false }); } catch { }
                return err == null ? 0 : 1;
            }

            Sys.TryDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (o, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (o, e) => ShowCrash(e.ExceptionObject as Exception);

            Application.Run(new MainForm(testUi));
            return 0;
        }

        static void ShowCrash(Exception ex)
        {
            string text = ex == null ? "Unknown error" : ex.ToString();
            // as administrator, only into Elixar's locked data folder (a file next to a downloaded Elixar.exe could be a link)
            string logAt = Sys.IsAdmin() || !Win.IsWindows ? Paths.FileIn("cleaner-log.txt") : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cleaner-log.txt");   // Linux: your private folder
            try { File.AppendAllText(logAt, DateTime.Now.ToString("s") + " CRASH " + text + Environment.NewLine); } catch { }
            MessageBox.Show("Something went wrong:\n\n" + (ex == null ? text : ex.Message) + "\n\nDetails were saved to " + logAt + ".",
                "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
