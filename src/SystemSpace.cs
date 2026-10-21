// System space tools: WinSxS check, reserved storage, restore-point storage, Compact OS, WSL/Docker disks,
// and a duplicate finder for your own folders. Everything uses Windows' own commands.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CDriveCleaner
{
    public class ShadowInfo { public string Drive = ""; public long Used, Allocated, Max; }

    public static class SystemSpace
    {
        static string Exe(string name) { return Win.SystemExe(name); }

        public static int Run(string exe, string args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(Exe(exe), args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timed out"; ChangeLog.Command(exe, args, output, -1); return -1; }
                    output = (o.Result + "\n" + e.Result).Trim();
                    ChangeLog.Command(exe, args, output, p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; ChangeLog.Command(exe, args, output, -1); return -1; }
        }

        // ---------- WinSxS ----------
        // Returns DISM's own report; reclaimable/recommended are read when Windows is in English.
        public static string AnalyzeComponentStore(out int reclaimablePackages, out bool? recommended)
        {
            string outp;
            Run("dism.exe", "/Online /Cleanup-Image /AnalyzeComponentStore /English", 30 * 60 * 1000, out outp);
            ParseAnalyze(outp, out reclaimablePackages, out recommended);
            return outp;
        }

        public static void ParseAnalyze(string text, out int reclaimablePackages, out bool? recommended)
        {
            reclaimablePackages = -1; recommended = null;
            var m = Regex.Match(text ?? "", @"Number of Reclaimable Packages\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) reclaimablePackages = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var r = Regex.Match(text ?? "", @"Component Store Cleanup Recommended\s*:\s*(Yes|No)", RegexOptions.IgnoreCase);
            if (r.Success) recommended = r.Groups[1].Value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- reserved storage ----------
        public static string ReservedStorageState()
        {
            try { return StoreData.RunPs("(Get-WindowsReservedStorageState).ReservedStorageState", 60000).Trim(); }
            catch (Exception ex) { return "ERROR: " + ex.Message; }
        }

        public static string SetReservedStorage(bool enabled)
        {
            return StoreData.RunPs("try { Set-WindowsReservedStorageState -State " + (enabled ? "Enabled" : "Disabled") + " -ErrorAction Stop; 'OK' } catch { 'ERROR: ' + $_.Exception.Message }", 5 * 60 * 1000).Trim();
        }

        // ---------- restore-point storage ----------
        public const string ShadowScript =
            "$v = @{}; Get-CimInstance Win32_Volume | ForEach-Object { $v[$_.DeviceID] = $_.DriveLetter }; " +
            "Get-CimInstance Win32_ShadowStorage | ForEach-Object { @($v[$_.Volume.DeviceID], $_.UsedSpace, $_.AllocatedSpace, $_.MaxSpace) -join \"`t\" }";

        public static List<ShadowInfo> ParseShadow(string output)
        {
            var list = new List<ShadowInfo>();
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split('\t');
                if (p.Length < 4) continue;
                var s = new ShadowInfo { Drive = p[0].Trim() };
                if (long.TryParse(p[1], out s.Used) && long.TryParse(p[2], out s.Allocated) && long.TryParse(p[3], out s.Max)) list.Add(s);
            }
            return list;
        }

        public static ShadowInfo ShadowForC()
        {
            try { return ParseShadow(StoreData.RunPs(ShadowScript, 60000)).FirstOrDefault(x => x.Drive.StartsWith("C", StringComparison.OrdinalIgnoreCase)); }
            catch { return null; }
        }

        public static string ResizeArgs(string size)
        {
            return "resize shadowstorage /for=C: /on=C: /maxsize=" + size;
        }

        // ---------- Compact OS ----------
        public static string CompactOsQuery()
        {
            string o; Run("compact.exe", "/compactos:query", 120000, out o); return o;
        }

        public static string CompactOs(bool on)
        {
            string o; int c = Run("compact.exe", "/compactos:" + (on ? "always" : "never"), 2 * 60 * 60 * 1000, out o);
            return (c == 0 ? "OK. " : "ERROR. ") + (o.Length > 400 ? o.Substring(o.Length - 400) : o);
        }

        // ---------- WSL / Docker virtual disks ----------
        public static List<string> FindVirtualDisks()
        {
            var list = new List<string>();
            string L = UserDirs.Local;
            Action<string, string, SearchOption> add = (dir, pat, opt) =>
            {
                try { if (Directory.Exists(dir)) list.AddRange(Directory.GetFiles(dir, pat, opt)); } catch { }
            };
            try
            {
                foreach (var pkg in Directory.Exists(Path.Combine(L, "Packages")) ? Directory.GetDirectories(Path.Combine(L, "Packages")) : new string[0])
                    add(Path.Combine(pkg, "LocalState"), "ext4.vhdx", SearchOption.TopDirectoryOnly);
            }
            catch { }
            add(Path.Combine(L, "Docker", "wsl"), "*.vhdx", SearchOption.AllDirectories);
            add(Path.Combine(L, "wsl"), "*.vhdx", SearchOption.AllDirectories);
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string DiskpartScript(string vhdx)
        {
            return "select vdisk file=\"" + vhdx + "\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n";
        }

        // Shuts WSL down, then lets diskpart shrink each disk. Returns bytes saved.
        public static long CompactVirtualDisks(List<string> disks, Action<string> log)
        {
            string o;
            Run("wsl.exe", "--shutdown", 120000, out o);
            Thread.Sleep(3000);
            long saved = 0;
            foreach (var d in disks)
            {
                long before = 0;
                try { before = new FileInfo(d).Length; } catch { }
                // diskpart runs as administrator: its script must be in a folder no other program can change
                string work = Paths.Work;
                if (work == null) { log("  Skipped " + d + ": could not prepare a protected folder for diskpart."); continue; }
                // diskpart runs as administrator on this file: only when no link leads it to a different disk image
                if (!SafeDelete.RealPathMatches(d)) { log("  Skipped " + d + ": a link leads somewhere else."); continue; }
                string script = Path.Combine(work, "elixar-compact-" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(script, DiskpartScript(d));
                int code = Run("diskpart.exe", "/s \"" + script + "\"", 60 * 60 * 1000, out o);
                try { File.Delete(script); } catch { }
                long after = before;
                try { after = new FileInfo(d).Length; } catch { }
                saved += Math.Max(0, before - after);
                log("  " + Path.GetFileName(Path.GetDirectoryName(d)) + "\\" + Path.GetFileName(d) + ": " + Fs.Fmt(before) + " -> " + Fs.Fmt(after) + (code == 0 ? "" : " (diskpart reported a problem)"));
            }
            return saved;
        }
    }

    // ---------- duplicate finder ----------
    public class DupGroup { public string Hash; public long Size; public List<FileInfo> Files = new List<FileInfo>(); public long Wasted { get { return Size * (Files.Count - 1); } } }

    public static class Duplicates
    {
        // never scanned, even if inside a chosen folder
        // (games are left alone too: game folders share many identical files, and deleting one breaks the game)
        static readonly string[] Blocked = { @"\windows\", @"\program files", @"\programdata\", @"\appdata\", @"\$recycle.bin\", @"\system volume information\", @"\.git\", @"\node_modules\",
                                             @"\steamapps\", @"\windowsapps\", @"\xboxgames\", @"\epic games\", @"\gog games\", @"\ea games\", @"\ubisoft game launcher\", @"\riot games\",
                                             @"\windows.old\", @"\$windows.~bt\", @"\$windows.~ws\", @"\$winreagent\", @"\recovery\", @"\msocache\", @"\$sysreset\", @"\elixar undo\" };

        public static bool Allowed(string path)
        {
            string l = path.ToLowerInvariant().Replace('/', '\\') + "\\";
            return !Blocked.Any(b => l.Contains(b));
        }

        public static List<string> DefaultFolders()
        {
            var list = new List<string>();
            foreach (var f in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.DesktopDirectory })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) list.Add(p);
            }
            string dl = Path.Combine(UserDirs.Home, "Downloads");
            if (Directory.Exists(dl)) list.Add(dl);
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // every drive you can search: C: means your own folders (your profile); other drives are searched whole
        public static List<string> DriveRoots()
        {
            var roots = new List<string>();
            if (!Win.IsWindows) return roots;
            roots.Add(UserDirs.Home);
            foreach (var d in DriveClean.Others()) roots.Add(d.RootDirectory.FullName);
            return roots;
        }

        public static List<string> SearchRoot(string root)
        {
            if (!Win.IsWindows || root.StartsWith("C", StringComparison.OrdinalIgnoreCase)) return Win.IsWindows ? new List<string> { UserDirs.Home } : new List<string> { root };
            return new List<string> { root };
        }

        public static string DriveLabel(string root)
        {
            if (string.Equals(root, UserDirs.Home, StringComparison.OrdinalIgnoreCase)) return "C:  your folders (" + root + ")";
            try
            {
                var d = new DriveInfo(root);
                string lbl = d.VolumeLabel ?? "";
                return root.Substring(0, 2) + (lbl.Length > 0 ? "  " + lbl : "") + "  (" + Fs.Fmt(d.TotalSize - d.TotalFreeSpace) + " used" + (d.DriveType == DriveType.Removable ? ", USB" : "") + ")";
            }
            catch { return root; }
        }

        public static bool OnUsb(string path)
        {
            try { return Win.IsWindows && new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Removable; } catch { return false; }
        }

        static string Hash(string path, long maxBytes)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            {
                if (maxBytes <= 0) return Convert.ToBase64String(sha.ComputeHash(fs));
                var buf = new byte[maxBytes];
                int n = fs.Read(buf, 0, buf.Length);
                return Convert.ToBase64String(sha.ComputeHash(buf, 0, n));
            }
        }

        // same size -> same first 64 KB -> same full SHA-256
        public static List<DupGroup> Find(IEnumerable<string> folders, long minSize, Action<string> status, CancellationToken ct, bool trustStart = false)
        {
            var bySize = new Dictionary<long, List<FileInfo>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in folders)
            {
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    if (ct.IsCancellationRequested) return new List<DupGroup>();
                    string dir = stack.Pop();
                    // with trustStart only the part below the start folder is checked (used by the self-test, whose folder is in Temp)
                    if (!(trustStart ? Allowed(dir.Length > root.Length ? dir.Substring(root.Length) + "\\" : "\\") : Allowed(dir))) continue;
                    if (status != null) status("Looking in " + dir);
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        foreach (var f in di.GetFiles())
                        {
                            try
                            {
                                if (f.Length < minSize || !seen.Add(f.FullName)) continue;
                                if ((f.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                                List<FileInfo> l;
                                if (!bySize.TryGetValue(f.Length, out l)) bySize[f.Length] = l = new List<FileInfo>();
                                l.Add(f);
                            }
                            catch { }
                        }
                        foreach (var d in di.GetDirectories())
                            if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d.FullName);
                    }
                    catch { }
                }
            }

            return Group(bySize, status, ct);
        }

        // files already listed (the smart scan walks a drive once): same size -> same first 64 KB -> same full SHA-256
        public static List<DupGroup> Group(IEnumerable<FileInfo> files, long minSize, Action<string> status, CancellationToken ct)
        {
            var bySize = new Dictionary<long, List<FileInfo>>();
            foreach (var f in files)
            {
                if (f.Length < minSize) continue;
                List<FileInfo> l;
                if (!bySize.TryGetValue(f.Length, out l)) bySize[f.Length] = l = new List<FileInfo>();
                l.Add(f);
            }
            return Group(bySize, status, ct);
        }

        static List<DupGroup> Group(Dictionary<long, List<FileInfo>> bySize, Action<string> status, CancellationToken ct)
        {
            var groups = new List<DupGroup>();
            foreach (var kv in bySize.Where(k => k.Value.Count > 1))
            {
                if (ct.IsCancellationRequested) break;
                var partial = new Dictionary<string, List<FileInfo>>();
                foreach (var f in kv.Value)
                {
                    try { string h = Hash(f.FullName, 65536); List<FileInfo> l; if (!partial.TryGetValue(h, out l)) partial[h] = l = new List<FileInfo>(); l.Add(f); } catch { }
                }
                foreach (var pg in partial.Values.Where(v => v.Count > 1))
                {
                    var full = new Dictionary<string, List<FileInfo>>();
                    foreach (var f in pg)
                    {
                        if (status != null) status("Comparing " + f.Name);
                        try { string h = kv.Key <= 65536 ? "s" : Hash(f.FullName, 0); List<FileInfo> l; if (!full.TryGetValue(h, out l)) full[h] = l = new List<FileInfo>(); l.Add(f); } catch { }
                    }
                    foreach (var fg in full.Where(x => x.Value.Count > 1))
                        groups.Add(new DupGroup { Hash = fg.Key, Size = kv.Key, Files = fg.Value.OrderBy(x => x.CreationTime).ThenBy(x => x.FullName.Length).ToList() });
                }
            }
            return groups.OrderByDescending(g => g.Wasted).ToList();
        }

        // Recycle Bin, so it can be undone from the desktop
        // Windows only, in a method of its own: Ubuntu's Mono has no Microsoft.VisualBasic, and a method that mentions it
        // can't even start there. Linux never calls this one.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void ToRecycleBin(string path)
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        public static bool Recycle(string path)
        {
            if (Lx.On)
            {
                // Linux: the desktop's Trash (gio), never a link's target
                try { return !Protect.IsProtected(path) && (new FileInfo(path).Attributes & FileAttributes.ReparsePoint) == 0 && Lx.Trash(path); } catch { return false; }
            }
            try
            {
                // as administrator: only when no link leads it somewhere else (like Windows' own folders), and with every folder
                // above it held in place, so none can be swapped for a link between the check and the move
                using (var hold = SafeDelete.HoldParents(path))
                {
                    if (hold == null || Protect.IsProtected(path) || !SafeDelete.RealPathMatches(path)) return false;
                    ToRecycleBin(path);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
