// More space: Windows.old, the hibernation file, "Free X GB for me", moving personal folders and games off C:.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CDriveCleaner
{
    // ============================ Windows.old and upgrade leftovers (Windows' own Disk Cleanup) ============================
    public static class WindowsOld
    {
        public static readonly string[] Folders = { @"C:\Windows.old", @"C:\$Windows.~BT", @"C:\$Windows.~WS" };
        // Disk Cleanup handlers that remove them the supported way
        public static readonly string[] Handlers = { "Previous Installations", "Temporary Setup Files", "Windows Upgrade Log Files" };
        const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\";
        public const int Slot = 4242;   // cleanmgr /sagerun:4242 uses only what Elixar ticks here
        public static string FlagName { get { return "StateFlags" + Slot.ToString("0000", CultureInfo.InvariantCulture); } }

        public static long Size()
        {
            long n = 0; bool unknown = false;
            foreach (var f in Folders) { long m = WinLeftovers.Measure(f); if (m < 0) unknown = true; else n += m; }
            return n == 0 && unknown ? -1 : n;
        }

        public static void Clean(Action<string> log)
        {
            if (!Win.IsWindows) return;
            var set = new List<string>();
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    foreach (var h in Handlers)
                        using (var k = hklm.OpenSubKey(VolumeCaches + h, true))
                            if (k != null) { RegLog.Set(k, FlagName, 2, RegistryValueKind.DWord, "Previous Windows installation: tells Windows Disk Cleanup what to remove (taken off again right after)"); set.Add(h); }
                if (log != null) log("  Windows Disk Cleanup is removing the previous Windows installation (" + string.Join(", ", set) + "). This can take 10-30 minutes...");
                Sys.Run("cleanmgr.exe", "/sagerun:" + Slot, 2 * 60 * 60 * 1000);
                var left = Folders.Where(Directory.Exists).ToList();
                if (log != null)
                    log(left.Count == 0 ? "  The previous Windows installation is removed."
                                        : "  Still there: " + string.Join(", ", left) + ". Windows keeps it while it can still go back, or while an upgrade runs. " +
                                          "Remove it in Settings > System > Storage > Temporary files > Previous Windows installation(s), or restart and clean again.");
            }
            finally
            {
                // leave Disk Cleanup exactly as it was
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        foreach (var h in set)
                            using (var k = hklm.OpenSubKey(VolumeCaches + h, true))
                                if (k != null) RegLog.Delete(k, FlagName, "Previous Windows installation: Windows Disk Cleanup left as it was");
                }
                catch { }
            }
        }
    }

    // ============================ leftovers of Windows updates and upgrades ($GetCurrent, $WinREAgent) ============================
    // Windows makes these and doesn't always remove them. They are safe to delete once no update or upgrade is running and
    // nothing waits for a restart. Some of their folders are protected (only Windows may open them), so they are moved
    // with Windows' backup rights switched on for just that time (see BackupRights), never by changing permissions.
    public static class WinLeftovers
    {
        public static string GetCurrent { get { return SysRoot + "$GetCurrent"; } }
        public static string WinREAgent { get { return SysRoot + "$WinREAgent"; } }
        static string SysRoot { get { try { string w = Environment.GetFolderPath(Environment.SpecialFolder.Windows); return string.IsNullOrEmpty(w) ? @"C:\" : Path.GetPathRoot(w); } catch { return @"C:\"; } } }
        public static bool Allowed(string path)
        {
            string p; try { p = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
            return new[] { GetCurrent, WinREAgent }.Concat(WindowsOld.Folders).Any(x => string.Equals(x.TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase));
        }

        // pure: whether it is safe to remove them now, and why not
        public static string NotNowReason(bool restartPending, bool setupRunning, DateTime lastChange, DateTime now)
        {
            if (setupRunning) return "A Windows update or upgrade is running right now.";
            if (restartPending) return "Windows is waiting for a restart to finish an update.";
            if ((now - lastChange).TotalHours < 24) return "Windows changed it in the last day: an update may still be using it.";
            return null;
        }

        public static string NotNow(string path)
        {
            if (!Win.IsWindows) return null;
            bool restart = false, setup = false;
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var a = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                using (var b = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                    restart = a != null || b != null;
            }
            catch { }
            try { setup = Process.GetProcesses().Any(p => { try { var n = p.ProcessName.ToLowerInvariant(); return n == "setuphost" || n == "setupprep" || n == "windows10upgraderapp" || n == "windowsupdatebox" || n == "mediacreationtool"; } catch { return false; } }); } catch { }
            DateTime changed = DateTime.MinValue;
            try { if (Directory.Exists(path)) changed = Directory.GetLastWriteTime(path); } catch { }
            return NotNowReason(restart, setup, changed, DateTime.Now);
        }

        // a size even when part of it can't be read: -1 (shown as "size known after it runs") instead of a wrong 0
        public static long Measure(string path)
        {
            if (!Directory.Exists(path)) return 0;
            long n = Fs.SizeOf(path);
            if (n > 0) return n;
            try { return Directory.EnumerateFileSystemEntries(path).Any() ? n : 0; } catch { return -1; }
        }

        // counted with the backup rights on, so the protected parts are counted too
        public static long MeasureProtected(string path)
        {
            if (!Directory.Exists(path)) return 0;
            long n = 0; BackupRights.With(() => n = Measure(path), null); return n;
        }

        public static void Remove(string label, string path, Action<string> log)
        {
            if (!Directory.Exists(path)) return;
            string why = NotNow(path);
            if (why != null) { if (log != null) log("  Skipped " + path + ": " + why + " Clean it again later."); return; }
            if (string.Equals(path, WinREAgent, StringComparison.OrdinalIgnoreCase))
            {
                // a recovery image left mounted inside it keeps its files locked: Microsoft's tool releases it
                try { Sys.Run("dism.exe", "/Cleanup-Mountpoints", 10 * 60 * 1000); } catch { }
            }
            // with Windows' backup rights, so its protected parts can be moved too (permissions stay as they are)
            BackupRights.With(() => Quarantine.MoveToUndo(label, new[] { path }, log), log);
            if (Left(path) && log != null) log("  Still there: part of " + path + " couldn't be removed (in use or protected by Windows). Restart the PC and clean it again.");
        }

        static bool Left(string path) { try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); } catch { return Directory.Exists(path); } }
    }

    // Administrators hold Windows' backup and restore rights, switched off by default. Switched on, they let Elixar open
    // files that only Windows may open, for reading and moving, without changing anyone's permissions. Elixar switches
    // them on only while it removes Windows' own leftovers, and off again right after. Its file moves work by handle and
    // never follow links, so the rights reach nothing outside those folders.
    public static class BackupRights
    {
        [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct TokenPriv { public int Count; public Luid Luid; public int Attr; }
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string system, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPriv state, int len, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        static readonly object gate = new object();

        static bool Set(string name, bool on)
        {
            IntPtr tok;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28 /* adjust + query */, out tok)) return false;
            try
            {
                Luid l; if (!LookupPrivilegeValue(null, name, out l)) return false;
                var tp = new TokenPriv { Count = 1, Luid = l, Attr = on ? 2 : 0 };
                return AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(tok); }
        }

        public static void With(Action work, Action<string> log)
        {
            if (!Win.IsWindows) { work(); return; }
            lock (gate)
            {
                bool b = Set("SeBackupPrivilege", true), r = Set("SeRestorePrivilege", true);
                if (!(b && r) && log != null) log("  Windows' backup rights aren't available (Elixar isn't running as administrator?): protected files stay.");
                try { work(); }
                finally { Set("SeBackupPrivilege", false); Set("SeRestorePrivilege", false); }
            }
        }
    }

    // ============================ hibernation file ============================
    public static class Hibernation
    {
        public static long FileSize()
        {
            try
            {
                var f = new DirectoryInfo(@"C:\").GetFiles("hiberfil.sys");   // read from the folder listing, the file itself is locked
                return f.Length > 0 ? f[0].Length : 0;
            }
            catch { return 0; }
        }

        // "full", "reduced", "off" (or "" when unknown)
        public static string State()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
                {
                    if (k == null) return "";
                    object en = k.GetValue("HibernateEnabled"), type = k.GetValue("HiberFileType");
                    if (en is int && (int)en == 0) return "off";
                    if (type is int) return (int)type == 1 ? "reduced" : "full";
                    return FileSize() > 0 ? "on" : "off";
                }
            }
            catch { return ""; }
        }

        public static string Describe(long size, string state)
        {
            switch (state)
            {
                case "off": return "Hibernation is off (no hiberfil.sys).";
                case "reduced": return "Hibernation file: " + Fs.Fmt(size) + " (reduced: keeps Fast Startup, no Hibernate).";
                case "full": return "Hibernation file: " + Fs.Fmt(size) + " (full: Hibernate and Fast Startup).";
                default: return size > 0 ? "Hibernation file: " + Fs.Fmt(size) + "." : "No hibernation file.";
            }
        }

        public static string Set(string mode)   // "reduced", "off", "full"
        {
            string args = mode == "off" ? "/hibernate off" : mode == "reduced" ? "/hibernate /type reduced" : "/hibernate on";
            string o = Sys.Run("powercfg.exe", args, 60000);
            if (mode == "full") o += Sys.Run("powercfg.exe", "/hibernate /type full", 60000);
            return o.Trim().Length == 0 ? "Done." : o.Trim();
        }
    }

    // ============================ "Free X GB for me" ============================
    public static class Planner
    {
        public class Plan { public List<CleanItem> Items = new List<CleanItem>(); public long Bytes, Target, MaxPossible; public bool Reached { get { return Bytes >= Target; } } }

        static int Rank(CleanItem i) { return i.Tier == Tier.Safe ? 0 : i.Tier == Tier.Review ? 1 : 2; }

        // the safest items first (Safe, then Review, then Caution if allowed), biggest first within each, until the target is reached
        public static Plan Pick(IEnumerable<CleanItem> items, long target, bool allowCaution, bool allowRecycleBin)
        {
            var pool = items.Where(i => !i.Privacy && !i.SizeUnknown && i.Size > 0 && (allowCaution || i.Tier != Tier.Caution) &&
                                        (allowRecycleBin || !i.Name.StartsWith("Recycle Bin", StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(Rank).ThenByDescending(i => i.Size).ToList();
            var plan = new Plan { Target = target, MaxPossible = pool.Sum(i => i.Size) };
            foreach (var i in pool)
            {
                if (plan.Bytes >= target) break;
                plan.Items.Add(i); plan.Bytes += i.Size;
            }
            // drop picks that are no longer needed, the riskiest and smallest first (a big one picked later can make them unnecessary)
            foreach (var i in plan.Items.OrderByDescending(Rank).ThenBy(x => x.Size).ToList())
                if (plan.Bytes - i.Size >= target) { plan.Items.Remove(i); plan.Bytes -= i.Size; }
            return plan;
        }
    }

    // ============================ moving personal folders and games off C: ============================
    public class UserFolder
    {
        public string Name, Id;   // Id = Windows' KNOWNFOLDERID
        public string Path = ""; public long Bytes = -1; public bool OneDrive;
        public bool OnC { get { return Path.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase); } }
    }

    public class SteamGame { public string Name = "", Library = "", Folder = ""; public long Bytes; }

    public static class Mover
    {
        public static readonly string[][] Known =
        {
            new[] { "Downloads", "374DE290-123F-4565-9164-39C4925E467B" },
            new[] { "Documents", "FDD39AD0-238F-46AF-ADB4-6C85480369C7" },
            new[] { "Pictures", "33E28130-4E1E-4676-835A-98395C3BC3BB" },
            new[] { "Videos", "18989B1D-99B5-455B-841C-AB7C74E4DDFC" },
            new[] { "Music", "4BD8D571-6D19-48D3-BE97-422220080E43" },
            new[] { "Desktop", "B4BFCC3A-DB2C-424C-B029-7FE99A87C641" },
        };

        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
        [DllImport("shell32.dll")] static extern int SHSetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] string path);

        public static string GetPath(string id)
        {
            IntPtr p;
            if (SHGetKnownFolderPath(new Guid(id), 0, IntPtr.Zero, out p) != 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
        }

        public static bool SetPath(string id, string path) { return SHSetKnownFolderPath(new Guid(id), 0, IntPtr.Zero, path) == 0; }

        public static List<UserFolder> ReadFolders(bool measure)
        {
            var list = new List<UserFolder>();
            if (!Win.IsWindows) return list;
            foreach (var k in Known)
            {
                var f = new UserFolder { Name = k[0], Id = k[1], Path = GetPath(k[1]) ?? "" };
                f.OneDrive = f.Path.IndexOf("\\OneDrive", StringComparison.OrdinalIgnoreCase) >= 0;
                if (measure) f.Bytes = Fs.SizeOf(f.Path);
                list.Add(f);
            }
            return list;
        }

        // the usual new place: X:\Users\<you>\<Folder>
        public static string TargetFor(string drive, string folderName)
        {
            string user = Environment.UserName;
            return System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(drive, "Users"), user), folderName);
        }

        public static string HomeFor(string folderName)
        {
            return System.IO.Path.Combine(UserDirs.Home, folderName);
        }

        public class Result { public int Copied, Failed, LeftBehind; public long Bytes; public bool Switched; public List<string> Problems = new List<string>(); }

        // 1. copy everything (nothing deleted yet)  2. check every copy  3. point Windows at the new place  4. delete the originals
        public static Result Move(string from, string to, Func<string, bool> switchTo, Action<string> log)
        {
            var r = new Result();
            if (!Directory.Exists(from)) { r.Problems.Add("Folder not found: " + from); return r; }
            Directory.CreateDirectory(to);
            var copied = new List<string>();
            CopyTree(from, to, r, copied, log);
            if (r.Failed > 0) { r.Problems.Insert(0, r.Failed + " files could not be copied; nothing was changed. Close programs using them and try again."); return r; }
            if (switchTo != null && !switchTo(to)) { r.Problems.Add("Windows did not accept the new place; your files are still in " + from + " (a copy is in " + to + ")."); return r; }
            r.Switched = true;
            foreach (var f in copied)
            {
                bool gone = false;
                try { gone = Win.IsWindows ? SafeDelete.Delete(f) : Del(f); } catch { }
                if (!gone) r.LeftBehind++;
            }
            RemoveEmptyDirs(from);
            if (r.LeftBehind > 0) r.Problems.Add(r.LeftBehind + " files were in use and stay in " + from + "; delete them later.");
            return r;
        }

        static void CopyTree(string src, string dst, Result r, List<string> copied, Action<string> log)
        {
            DirectoryInfo di;
            try { di = new DirectoryInfo(src); if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return; } catch { return; }
            try { SafeFile.MakeDir(dst); } catch (Exception e) { r.Failed++; r.Problems.Add(dst + ": " + e.Message); return; }
            FileInfo[] files; DirectoryInfo[] dirs;
            try { files = di.GetFiles(); dirs = di.GetDirectories(); } catch (Exception e) { r.Failed++; r.Problems.Add(src + ": " + e.Message); return; }
            foreach (var f in files)
            {
                string target = Path.Combine(dst, f.Name);
                try
                {
                    if (!(File.Exists(target) && new FileInfo(target).Length == f.Length))
                    {
                        if (File.Exists(target)) Fs.Delete(target);
                        SafeFile.Copy(f.FullName, target);   // read and written through checked handles: a link can't redirect it
                    }
                    if (new FileInfo(target).Length != f.Length) throw new IOException("copy is a different size");
                    copied.Add(f.FullName); r.Copied++; r.Bytes += f.Length;
                }
                catch (Exception e) { r.Failed++; if (r.Problems.Count < 20) r.Problems.Add(f.FullName + ": " + e.Message); }
            }
            foreach (var d in dirs) CopyTree(d.FullName, Path.Combine(dst, d.Name), r, copied, log);
        }

        static bool Del(string f) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); return true; }

        static void RemoveEmptyDirs(string dir)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(dir)) RemoveEmptyDirs(d);
                if (Directory.GetFileSystemEntries(dir).Length == 0 && !Protect.IsProtected(dir)) { if (Win.IsWindows) SafeDelete.Delete(dir); else Directory.Delete(dir, false); }
            }
            catch { }
        }

        // ---- Steam games ----
        static readonly Regex VdfPair = new Regex("\"(?<k>[^\"]+)\"\\s+\"(?<v>[^\"]*)\"", RegexOptions.Compiled);

        public static List<string> SteamLibraries(string steamPath)
        {
            var libs = new List<string>();
            if (string.IsNullOrEmpty(steamPath)) return libs;
            libs.Add(steamPath);
            try
            {
                string vdf = Path.Combine(Path.Combine(steamPath, "steamapps"), "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in VdfPair.Matches(File.ReadAllText(vdf)))
                        if (m.Groups["k"].Value == "path") libs.Add(m.Groups["v"].Value.Replace("\\\\", "\\"));
            }
            catch { }
            return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static SteamGame ParseManifest(string acfText, string library)
        {
            string name = null, dir = null; long size = 0;
            foreach (Match m in VdfPair.Matches(acfText ?? ""))
            {
                string k = m.Groups["k"].Value, v = m.Groups["v"].Value;
                if (k == "name" && name == null) name = v;
                else if (k == "installdir" && dir == null) dir = v;
                else if (k == "SizeOnDisk" && size == 0) long.TryParse(v, out size);
            }
            if (name == null || dir == null) return null;
            // the folder name comes from a file any program can change: one plain name, nothing that climbs out of the library
            if (dir.Length == 0 || dir.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || dir.Trim().Trim('.').Length == 0 || dir.Contains("..")) return null;
            return new SteamGame { Name = name, Library = library, Folder = Path.Combine(Path.Combine(Path.Combine(library, "steamapps"), "common"), dir), Bytes = size };
        }

        public static List<SteamGame> SteamGamesOn(string drive, string steamPath)
        {
            var games = new List<SteamGame>();
            foreach (var lib in SteamLibraries(steamPath).Where(l => l.StartsWith(drive, StringComparison.OrdinalIgnoreCase)))
                try
                {
                    foreach (var acf in Directory.GetFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                    {
                        var g = ParseManifest(File.ReadAllText(acf), lib);
                        if (g != null) games.Add(g);
                    }
                }
                catch { }
            return games.OrderByDescending(g => g.Bytes).ToList();
        }

        // other fixed drives with room
        public static List<DriveInfo> TargetDrives()
        {
            var list = new List<DriveInfo>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                    try { if (d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase) && d.DriveFormat == "NTFS") list.Add(d); } catch { }
            }
            catch { }
            return list.OrderByDescending(d => d.AvailableFreeSpace).ToList();
        }
    }

    // ============================ Docker ============================
    public static class Docker
    {
        // Only Docker Desktop's own copy in Program Files, signed by Docker. A docker.exe found through PATH could sit in a
        // folder any program can write to (for example one in your user folder), and Elixar would run it as administrator.
        public static string Exe()
        {
            string pf = Win.ProgramFiles;
            string std = Path.Combine(pf ?? "", @"Docker\Docker\resources\bin\docker.exe");
            if (!File.Exists(std)) return null;
            if (Win.IsWindows && !Trust.SignedBy(std, Trust.DockerInc)) return null;
            return std;
        }

        // Docker reads its settings, sign-in helpers and plugins from folders in your account that any program you run can
        // change (%USERPROFILE%\.docker). Run as administrator, it would hand those administrator rights. So Elixar runs
        // Docker's prune with your normal rights, just as typing it in a command window would.
        public static string Prune(string exe)
        {
            if (!Win.IsWindows || !Sys.IsAdmin()) return Sys.Run(exe, "system prune -f", 30 * 60 * 1000);
            string outFile = Path.Combine(UserDirs.Temp, "elixar-docker-" + Guid.NewGuid().ToString("N") + ".txt");
            int code;
            Unelevated.Start(Install.CmdExe, PruneArgs(exe, outFile), 30 * 60 * 1000, out code);
            string text = ReadOwnOutput(outFile);
            try { SafeDelete.Delete(outFile); } catch { }
            return text;
        }
        public static string PruneArgs(string exe, string outFile) { return "/d /c \"\"" + exe + "\" system prune -f > \"" + outFile + "\" 2>&1\""; }

        // the text the unelevated Docker wrote: only a plain file with one name (never a link to another file), at most 64 KB
        static string ReadOwnOutput(string file)
        {
            try
            {
                if (!File.Exists(file) || !SafeDelete.RealPathMatches(file) || SmartScan.Links(file) != 1) return "";
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buf = new byte[Math.Min(fs.Length, 65536)];
                    int n = fs.Read(buf, 0, buf.Length);
                    return System.Text.Encoding.UTF8.GetString(buf, 0, n);
                }
            }
            catch { return ""; }
        }

        // "Total reclaimed space: 1.2GB" -> bytes
        public static long ParseReclaimed(string output)
        {
            var m = Regex.Match(output ?? "", @"Total reclaimed space:\s*([\d.]+)\s*([kKMG]?B)");
            if (!m.Success) return 0;
            double v; if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return 0;
            switch (m.Groups[2].Value.ToUpperInvariant()) { case "KB": v *= 1e3; break; case "MB": v *= 1e6; break; case "GB": v *= 1e9; break; }
            return (long)v;
        }
    }
}
