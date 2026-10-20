// What can be cleaned on Windows, and the scanner that classifies files and folders.

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

namespace CDriveCleaner
{
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

                // ticked by themselves when no update or upgrade is running and nothing waits for a restart
                new CleanItem {
                    Name = "Windows upgrade media ($GetCurrent)", Tier = Tier.Caution, DefaultOn = WinLeftovers.NotNow(getCurrent) == null, Where = getCurrent,
                    Info = "Setup files left from a past Windows upgrade. Removed only when no update or upgrade is running and nothing waits for a restart. Moved to Undo for 7 days first.",
                    Measure = () => WinLeftovers.MeasureProtected(getCurrent),
                    Clean = log => WinLeftovers.Remove("Windows upgrade media ($GetCurrent)", getCurrent, log) },

                new CleanItem {
                    Name = "Recovery update leftovers ($WinREAgent)", Tier = Tier.Caution, DefaultOn = WinLeftovers.NotNow(winReAgent) == null, Where = winReAgent,
                    Info = "Leftovers from updating the Windows recovery environment. Removed only when no update is running and nothing waits for a restart. Moved to Undo for 7 days first.",
                    Measure = () => WinLeftovers.MeasureProtected(winReAgent),
                    Clean = log => WinLeftovers.Remove("Recovery update leftovers ($WinREAgent)", winReAgent, log) },

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
}
