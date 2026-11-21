// Tools > Backup: a plain copy of your folders (Documents, Desktop, Pictures...) on another drive or a USB disk, kept up to
// date on a schedule. Only new and changed files are copied. A file that changed or was deleted on this PC is kept in the
// backup for 30 days (in "_versions" and "_deleted"), so a mistake or ransomware doesn't wipe the backup too.
// Your files can be opened straight from the backup: no special program is needed to get them back.
//
// Safety: only folders inside your own user folder are copied (never another account's or Windows' files), never onto C:,
// and every read and write goes through Elixar's link-proof file calls, because "Back up now" can run as administrator.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CDriveCleaner
{
    public class BackupResult
    {
        public int Copied, Updated, Kept, Removed, Skipped, CloudOnly, InUse; public long Bytes;
        public string Problem; public DateTime Time; public TimeSpan Took;
        public List<string> Errors = new List<string>();
        public string Summary()
        {
            if (Problem != null) return Problem;
            return Copied + " new and " + Updated + " changed files copied (" + Fs.Fmt(Bytes) + "), " + Kept + " unchanged" +
                   (Removed > 0 ? ", " + Removed + " deleted here kept for 30 days" : "") + (InUse + Skipped > 0 ? ", " + (InUse + Skipped) + " skipped" : "") +
                   (CloudOnly > 0 ? ", " + CloudOnly + " only in OneDrive (not on this PC)" : "") + ".";
        }
    }

    public static class Backup
    {
        public const int KeepDays = 30;
        public const string Task = @"Elixar\Backup";
        public static Func<List<string[]>> FoldersOverride;   // self-test
        public static Func<string> HomeOverride;              // self-test: your user folder
        public static string SystemRootOverride;             // self-test: the drive Windows is on

        // { key, name, path } of the folders you can choose
        public static List<string[]> Folders()
        {
            if (FoldersOverride != null) return FoldersOverride();
            Func<Environment.SpecialFolder, string> sf = f => { try { return Environment.GetFolderPath(f); } catch { return ""; } };
            var l = new List<string[]> {
                new[] { "Documents", "Documents", sf(Environment.SpecialFolder.MyDocuments) },
                new[] { "Desktop", "Desktop", sf(Environment.SpecialFolder.DesktopDirectory) },
                new[] { "Pictures", "Pictures", sf(Environment.SpecialFolder.MyPictures) },
                new[] { "Videos", "Videos", sf(Environment.SpecialFolder.MyVideos) },
                new[] { "Music", "Music", sf(Environment.SpecialFolder.MyMusic) } };
            string home = UserDirs.Home; if (!string.IsNullOrEmpty(home)) l.Add(new[] { "Downloads", "Downloads", Path.Combine(home, "Downloads") });
            return l.Where(x => !string.IsNullOrEmpty(x[2])).ToList();
        }

        // ---------- what you chose (your own settings: the backup runs with your rights) ----------
        public static string Dest { get { return Settings.Get("BackupDest", ""); } set { Settings.Set("BackupDest", value ?? ""); } }
        public static List<string> Chosen
        {
            get { var v = Settings.Get("BackupFolders", "Documents|Desktop|Pictures"); return v.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList(); }
            set { Settings.Set("BackupFolders", string.Join("|", value)); }
        }
        public static string Every { get { string v = Settings.Get("BackupEvery", "off"); return v == "day" || v == "week" ? v : "off"; } set { Settings.Set("BackupEvery", value == "day" || value == "week" ? value : "off"); } }
        public static DateTime? Last
        {
            get { DateTime d; return DateTime.TryParse(Settings.Get("BackupLast", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d : (DateTime?)null; }
            set { Settings.Set("BackupLast", value.HasValue ? value.Value.ToString("s", CultureInfo.InvariantCulture) : ""); }
        }
        public static string LastSummary { get { return Settings.Get("BackupLastResult", ""); } set { Settings.Set("BackupLastResult", value ?? ""); } }
        public static bool Configured { get { return Dest.Length > 0 && Every != "off"; } }

        static string Home { get { return HomeOverride != null ? HomeOverride() : UserDirs.Home; } }
        static string SystemRoot { get { return SystemRootOverride ?? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "C:\\"); } }

        static bool Inside(string path, string folder)
        {
            string p = Path.GetFullPath(path).TrimEnd('\\', '/'), f = Path.GetFullPath(folder).TrimEnd('\\', '/');
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // a folder is copied only when it's inside your own user folder (never another account's or Windows' files)
        public static string SourceProblem(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return "not on this PC";
            string home = Home;
            if (string.IsNullOrEmpty(home) || !Inside(path, home) || Path.GetFullPath(path).TrimEnd('\\', '/').Equals(Path.GetFullPath(home).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return "not in your user folder";
            if (Win.IsWindows && ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !SafeDelete.RealPathMatches(path))) return "a link is in the way";
            return null;
        }

        // where the backup may go: another drive or a network folder, never C: or inside what is backed up
        public static string DestProblem(string dest, IEnumerable<string> sources)
        {
            try
            {
                if (string.IsNullOrEmpty(dest)) return "Choose a drive or folder for the backup.";
                if (!Path.IsPathRooted(dest)) return "Choose a whole path, like E:\\Backup.";
                // only a drive letter or \\server\share: "\\?\", "\\.\" and device names can point anywhere, even into Windows
                if (dest.StartsWith(@"\\?") || dest.StartsWith(@"\\.") || dest.StartsWith("//") || dest.StartsWith(@"\??") || dest.IndexOf(':', 2) >= 0)
                    return "Choose a drive letter or a network folder (\\\\server\\share).";
                string full = Path.GetFullPath(dest);
                if (!Directory.Exists(full)) return "The backup drive isn't connected (" + full + ").";
                bool network = full.StartsWith(@"\\");
                if (network && LocalHost(full)) return "Choose a real drive: that network path leads back to this PC.";
                if (!network && Win.IsWindows && string.Equals(Path.GetPathRoot(full), SystemRoot, StringComparison.OrdinalIgnoreCase))
                    return "Choose another drive: a backup on C: goes with C: if the drive fails.";
                // a drive letter can really be a folder on C: (subst, a mounted folder, a link): where it really is decides
                if (!network && OnSystemVolume(full)) return "Choose another drive: that one is really on C:.";
                if (sources.Any(s => !string.IsNullOrEmpty(s) && (Inside(full, s) || Inside(s, full)))) return "The backup can't be inside a folder it backs up.";
                if (Protect.IsProtected(full) && !network) return "Choose another folder: that one is Windows' or a main folder.";
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        // the folder really is on the Windows drive (through subst, a mount point or a link)
        public static Func<string, bool> OnSystemVolumeOverride;   // self-test
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder buf, uint size, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder name, uint size);
        public static bool OnSystemVolume(string dir)
        {
            if (OnSystemVolumeOverride != null) return OnSystemVolumeOverride(dir);
            if (!Win.IsWindows || SystemRootOverride != null) return false;   // the self-test names its own "system drive"
            try
            {
                var sys = new StringBuilder(64);
                if (!GetVolumeNameForVolumeMountPointW(SystemRoot.TrimEnd('\\') + "\\", sys, 64)) return true;   // can't tell: not allowed
                using (var h = CreateFileW(@"\\?\" + Path.GetFullPath(dir), 0x80 /* read attributes */, 7, IntPtr.Zero, 3, 0x02000000 /* a folder */, IntPtr.Zero))
                {
                    if (h.IsInvalid) return true;
                    var b = new StringBuilder(1024);
                    uint n = GetFinalPathNameByHandleW(h, b, 1024, 1 /* VOLUME_NAME_GUID */);
                    if (n > 0 && n < 1024) return b.ToString().StartsWith(sys.ToString().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                    // a mapped network drive (Z: = \\server\share) has no volume name: where it really leads decides
                    b.Length = 0; n = GetFinalPathNameByHandleW(h, b, 1024, 0 /* VOLUME_NAME_DOS */);
                    if (n == 0 || n >= 1024) return true;
                    string dos = b.ToString();
                    if (dos.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return LocalHost(@"\\" + dos.Substring(8));
                    return true;
                }
            }
            catch { return true; }
        }

        // \\localhost\C$, \\127.0.0.1\..., \\<this PC's name>\...: a network path that leads back to this PC
        public static bool LocalHost(string unc)
        {
            try
            {
                string host = unc.TrimStart('\\', '/').Split('\\', '/')[0].Trim('[', ']').TrimEnd('.').ToLowerInvariant();
                if (host.Length == 0 || host == "." || host == "?" || host == "localhost" || host.StartsWith("127.") || host == "::1" || host == "0.0.0.0" || host.EndsWith(".ipv6-literal.net")) return true;
                string me = Environment.MachineName.ToLowerInvariant();
                if (host == me || host.StartsWith(me + ".")) return true;
                IPAddress ip;
                var addrs = new List<IPAddress>();
                if (IPAddress.TryParse(host, out ip)) addrs.Add(ip);
                else
                {
                    // a name: what it leads to (an alias of this PC counts as this PC); a name that can't be looked up quickly isn't
                    var look = System.Threading.Tasks.Task.Factory.StartNew(() => { try { return Dns.GetHostAddresses(host); } catch { return new IPAddress[0]; } });
                    if (look.Wait(3000)) addrs.AddRange(look.Result);
                }
                var mine = new List<IPAddress>();
                try { foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) try { foreach (var a in ni.GetIPProperties().UnicastAddresses) mine.Add(a.Address); } catch { } } catch { }
                foreach (var a0 in addrs)
                {
                    var a = a0.IsIPv4MappedToIPv6 ? a0.MapToIPv4() : a0;
                    if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || mine.Any(m => m.Equals(a))) return true;
                }
                return false;
            }
            catch { return true; }
        }

        public static string Root(string dest) { return Path.Combine(Path.Combine(dest, "Elixar Backup"), Safe(Environment.MachineName)); }
        static string Safe(string s) { var sb = new StringBuilder(); foreach (char c in s ?? "PC") sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c); return sb.Length == 0 ? "PC" : sb.ToString(); }

        // OneDrive "online only" files: reading one would download it
        const FileAttributes CloudOnlyAttrs = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

        public static BackupResult Run(string dest, IList<string> keys, DateTime now, Action<string> progress, CancellationToken ct)
        {
            var r = new BackupResult { Time = now };
            if (!Policy.Allowed("backup")) { r.Problem = "Your organization's policy doesn't allow this."; return r; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var folders = Folders().Where(f => keys.Contains(f[0])).ToList();
            r.Problem = folders.Count == 0 ? "Choose at least one folder to back up." : DestProblem(dest, folders.Select(f => f[2]));
            if (r.Problem != null) return r;
            string root = Root(dest), stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            // one backup at a time (the daily one and "Back up now" would otherwise copy over each other's files)
            bool mine;
            Mutex one;
            try { one = OneAtATime(out mine); }
            catch (UnauthorizedAccessException) { r.Problem = BusyProblem; return r; }   // held by another Elixar we may not open: busy
            using (one)
            {
                if (!mine) { try { mine = one.WaitOne(0); } catch (AbandonedMutexException) { mine = true; } }   // a run that crashed left it
                if (!mine) { r.Problem = BusyProblem; return r; }
                try { return RunLocked(root, stamp, folders, now, progress, ct, r, sw); }
                finally { try { one.ReleaseMutex(); } catch { } }
            }
        }

        // the same lock for "Back up now" (as administrator) and the daily run (with your normal rights): made so that both may open it
        static Mutex OneAtATime(out bool mine)
        {
            if (Win.IsWindows)
                try
                {
                    var sec = new System.Security.AccessControl.MutexSecurity();
                    var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                    sec.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(me, System.Security.AccessControl.MutexRights.Synchronize | System.Security.AccessControl.MutexRights.Modify, System.Security.AccessControl.AccessControlType.Allow));
                    return new Mutex(true, @"Local\ElixarBackup", out mine, sec);
                }
                catch (UnauthorizedAccessException) { throw; }
                catch { }
            return new Mutex(true, @"Local\ElixarBackup", out mine);
        }

        static BackupResult RunLocked(string root, string stamp, List<string[]> folders, DateTime now, Action<string> progress, CancellationToken ct, BackupResult r, System.Diagnostics.Stopwatch sw)
        {
            try { SafeFile.MakeDir(root); }
            catch (Exception e) { r.Problem = "Couldn't make the backup folder: " + e.Message; return r; }
            foreach (var f in folders)
            {
                if (ct.IsCancellationRequested) break;
                string why = SourceProblem(f[2]);
                if (why != null) { r.Errors.Add(f[1] + ": " + why); continue; }
                if (progress != null) progress("Backing up " + f[1] + "...");
                Mirror(f[2], Path.Combine(root, f[0]), Path.Combine(Path.Combine(Path.Combine(root, "_versions"), stamp), f[0]),
                       Path.Combine(Path.Combine(Path.Combine(root, "_deleted"), stamp), f[0]), r, progress, ct);
            }
            Prune(Path.Combine(root, "_versions"), now); Prune(Path.Combine(root, "_deleted"), now);
            r.Took = sw.Elapsed;
            if (ct.IsCancellationRequested) r.Problem = "Stopped. What was copied so far is in the backup.";
            try
            {
                SafeFile.WriteText(Path.Combine(root, "Elixar backup.txt"),
                    "Elixar backup of " + Environment.MachineName + "\r\nLast run: " + now.ToString("f") + "\r\n" + r.Summary() + "\r\n\r\n" +
                    "Each folder here is a plain copy: open the files directly, or copy them back.\r\n" +
                    "_versions: the older copy of a file that changed, kept " + KeepDays + " days.\r\n_deleted: files deleted on the PC, kept " + KeepDays + " days.\r\n");
            }
            catch { }
            if (r.Problem == null) { Last = now; LastSummary = r.Summary(); }
            ChangeLog.Add("Files", "Backup", root, Fs.Fmt(r.Bytes), r.Summary(), string.Join("; ", r.Errors.Take(5)), r.Problem != null);
            return r;
        }

        static void Mirror(string src, string dst, string versions, string deleted, BackupResult r, Action<string> progress, CancellationToken ct)
        {
            var dirs = new Stack<Tuple<string, string>>(); dirs.Push(Tuple.Create(src, dst));
            bool elevated = Win.IsWindows && Sys.IsAdmin();
            while (dirs.Count > 0 && !ct.IsCancellationRequested)
            {
                var cur = dirs.Pop(); string s = cur.Item1, d = cur.Item2;
                string rel = s.Length > src.Length ? s.Substring(src.Length).TrimStart('\\', '/') : "";
                DirectoryInfo di = new DirectoryInfo(s);
                FileInfo[] files; DirectoryInfo[] subs;
                try { files = di.GetFiles(); subs = di.GetDirectories(); }
                catch (Exception e) { r.Errors.Add(s + ": " + e.Message); continue; }   // unreadable: nothing in the backup counts as deleted
                try { SafeFile.MakeDir(d); }
                catch (Exception e) { r.Errors.Add(d + ": " + e.Message); continue; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    if (ct.IsCancellationRequested) return;
                    seen.Add(f.Name);
                    try
                    {
                        if ((f.Attributes & FileAttributes.ReparsePoint) != 0 && (f.Attributes & CloudOnlyAttrs) == 0) { r.Skipped++; continue; }
                        if ((f.Attributes & CloudOnlyAttrs) != 0) { r.CloudOnly++; continue; }
                        // a second name (hard link) could be any file on the PC, and "Back up now" runs as administrator: skipped
                        if (elevated && SmartScan.Links(f.FullName) > 1) { r.Skipped++; if (r.Errors.Count < 200) r.Errors.Add(f.FullName + ": it has a second name (a hard link), so it was skipped"); continue; }
                        if (f.FullName.Length > 250 || Path.Combine(d, f.Name).Length > 250) { r.Skipped++; r.Errors.Add("Path too long: " + f.FullName); continue; }
                        string target = Path.Combine(d, f.Name);
                        var t = new FileInfo(target);
                        if (t.Exists && t.Length == f.Length && Math.Abs((t.LastWriteTimeUtc - f.LastWriteTimeUtc).TotalSeconds) < 2) { r.Kept++; continue; }
                        bool had = t.Exists;
                        CopyOne(f.FullName, target, f.LastWriteTimeUtc, Path.Combine(versions, rel));
                        if (had) r.Updated++; else r.Copied++;
                        r.Bytes += f.Length;
                        if (progress != null && (r.Copied + r.Updated) % 200 == 0) progress("Copied " + (r.Copied + r.Updated) + " files (" + Fs.Fmt(r.Bytes) + ")...");
                    }
                    catch (Exception e)
                    {
                        if (e is IOException && IsInUse((IOException)e)) r.InUse++;
                        else { r.Skipped++; if (r.Errors.Count < 200) r.Errors.Add(f.FullName + ": " + e.Message); }
                    }
                }
                foreach (var sub in subs)
                {
                    seen.Add(sub.Name);
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // never followed
                    dirs.Push(Tuple.Create(sub.FullName, Path.Combine(d, sub.Name)));
                }
                // deleted on this PC: kept aside in the backup, not deleted
                try
                {
                    var bd = new DirectoryInfo(d);
                    foreach (var x in bd.GetFiles()) if (!seen.Contains(x.Name) && !x.Name.EndsWith(".elixar-part")) { MoveAside(x.FullName, Path.Combine(deleted, rel)); r.Removed++; }
                    foreach (var x in bd.GetDirectories())
                        if (!seen.Contains(x.Name) && (x.Attributes & FileAttributes.ReparsePoint) == 0) { r.Removed += MoveDirAside(x.FullName, Path.Combine(deleted, Path.Combine(rel, x.Name))); }
                }
                catch (Exception e) { r.Errors.Add(d + ": " + e.Message); }
            }
        }

        static bool IsInUse(IOException e) { int h = System.Runtime.InteropServices.Marshal.GetHRForException(e) & 0xFFFF; return h == 32 || h == 33; }

        // the copy goes to a ".elixar-part" file first; only when it is complete does the older copy move to _versions and the
        // new one get its real name. A file that can't be copied (in use, like an open Outlook file) keeps its last good copy.
        static void CopyOne(string src, string target, DateTime mtimeUtc, string versionsDir)
        {
            string part = target + ".elixar-part";
            Action drop = () => { try { if (File.Exists(part)) { if (Win.IsWindows) SafeDelete.Delete(part); else File.Delete(part); } } catch { } };
            drop();
            try { SafeFile.Copy(src, part); File.SetLastWriteTimeUtc(part, mtimeUtc); }
            catch { drop(); throw; }
            if (File.Exists(target)) MoveAside(target, versionsDir);
            SafeFile.MoveInto(part, Path.GetDirectoryName(target), Path.GetFileName(target));
        }

        static void MoveAside(string file, string toDir)
        {
            SafeFile.MakeDir(toDir);
            SafeFile.MoveInto(file, toDir, SafeFile.FreeName(toDir, Path.GetFileName(file)));
        }

        static int MoveDirAside(string dir, string toDir)
        {
            int n = 0;
            foreach (var f in new DirectoryInfo(dir).GetFiles()) { MoveAside(f.FullName, toDir); n++; }
            foreach (var s in new DirectoryInfo(dir).GetDirectories()) if ((s.Attributes & FileAttributes.ReparsePoint) == 0) n += MoveDirAside(s.FullName, Path.Combine(toDir, s.Name));
            try { SafeDelete.RemoveEmptyDir(dir); } catch { }
            return n;
        }

        // older copies and deleted files: kept KeepDays days, then deleted for good
        static void Prune(string dir, DateTime now)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    DateTime t;
                    if (!DateTime.TryParseExact(Path.GetFileName(d), "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) || (now - t).TotalDays <= KeepDays) continue;
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    if (Win.IsWindows) SafeDelete.Delete(d); else Directory.Delete(d, true);   // never through a link
                }
            }
            catch { }
        }

        // ---------- the schedule: with your own rights, not an administrator's ----------
        public static string CreateArgs(string exe, string every, string hhmm)
        {
            return "/Create /F /TN \"" + Task + "\" /TR \"\\\"" + exe + "\\\" --backup\" " + (every == "week" ? "/SC WEEKLY /D SUN" : "/SC DAILY") + " /ST " + hhmm;
        }

        public static string Schedule()
        {
            string o;
            if (Every == "off") { Automation.RunSchtasks(Automation.DeleteArgs(Task), out o); return null; }
            if (!Policy.Allowed("backup")) return "Your organization's policy doesn't allow this.";
            string ierr = Install.Ensure();
            if (ierr != null) return "Elixar could not put its protected copy in " + Install.Dir + ": " + ierr;
            if (Automation.RunSchtasks(CreateArgs(Install.Exe, Every, "13:17"), out o) != 0) return "Windows didn't make the backup task: " + o;
            // schtasks.exe can't say "run as soon as possible after a missed time" or "also on battery": without them a PC
            // that's off (or a laptop on battery) at 13:17 would never back up
            if (Win.IsWindows && !Lx.On)
            {
                string r = Sys.RunPowerShell(CatchUpScript, 60000);
                if (r.StartsWith("ERROR") || r.Contains("Exception")) ChangeLog.Add("Settings", "Backup: couldn't set the task to catch up", Task, "", "", r, true);
            }
            return null;
        }

        public const string BusyProblem = "A backup is already running.";
        public const string CatchUpScript = "$t = Get-ScheduledTask -TaskPath '\\Elixar\\' -TaskName 'Backup' -ErrorAction Stop; " +
            "$t.Settings.StartWhenAvailable = $true; $t.Settings.DisallowStartIfOnBatteries = $false; $t.Settings.StopIfGoingOnBatteries = $false; " +
            "Set-ScheduledTask -InputObject $t -ErrorAction Stop | Out-Null; 'ok'";

        // the scheduled run: quiet; a backup drive that isn't connected is said once every 3 days
        public static int Scheduled(Action<string> log)
        {
            string dest = Dest;
            if (dest.Length == 0 || Every == "off") { if (log != null) log("Backup: not set up."); return 0; }
            if (!Policy.Allowed("backup")) { if (log != null) log("Backup: not allowed by the policy."); return 0; }
            var r = Run(dest, Chosen, DateTime.Now, null, CancellationToken.None);
            if (log != null) log("Backup: " + r.Summary());
            if (r.Problem == BusyProblem) return 0;   // "Back up now" is running: that one is the backup of today
            if (r.Problem != null)
            {
                DateTime told; bool recent = DateTime.TryParse(Settings.Get("BackupToldMissing", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out told) && (DateTime.Now - told).TotalDays < 3;
                if (!recent) { Settings.Set("BackupToldMissing", DateTime.Now.ToString("s", CultureInfo.InvariantCulture)); Automation.Notify("Backup didn't run", r.Problem + " Click to open Elixar.", System.Windows.Forms.ToolTipIcon.Warning, true); }
                return 1;
            }
            return 0;
        }
    }
}
