// Undo area: risky deletions are moved here first and kept 7 days, with one-click Restore.
// Lives on a second drive when there is one (so C: space is freed right away), otherwise on C:.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CDriveCleaner
{
    public class UndoBatch
    {
        public string Dir;
        public DateTime Time;
        public string Label = "";
        public string Kind = "files";      // files | drivers
        public long Bytes;
        public List<string[]> Items = new List<string[]>();  // { storedName, originalPath, bytes }
        public int Keep = Quarantine.KeepDays;   // how many days this batch is kept (your own files: your choice in Automation)
        public int DaysLeft(int keepDays) { return Math.Max(0, keepDays - (int)Math.Floor((DateTime.Now - Time).TotalDays)); }
        public int DaysLeft() { return DaysLeft(Keep); }
    }

    public static class Quarantine
    {
        public const int KeepDays = 7;
        public const string FolderName = "Elixar Undo";
        public static string OverrideRoot;   // used by the self-test only

        // A second fixed drive with room to spare, else C:\ProgramData.
        // Linux: only Elixar's own private folder (0700) - never a folder on a USB drive or a disk's top level
        static string LinuxRoot { get { return Path.Combine(Paths.Data, "undo"); } }

        public static string RootFor(long need)
        {
            if (OverrideRoot != null) return OverrideRoot;
            if (!Win.IsWindows) { Locked.Lock(LinuxRoot); return LinuxRoot; }
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        if (d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase)) continue;
                        if (d.DriveFormat != "NTFS") continue;
                        if (d.AvailableFreeSpace < need + (2L << 30)) continue;
                        string r = Path.Combine(d.RootDirectory.FullName, FolderName);
                        if (Locked.Lock(r)) return r;         // only administrators can see or change it
                    }
                    catch { }
                }
            }
            catch { }
            string root = Path.Combine(ProgramData, FolderName);
            Locked.Lock(root);
            return root;
        }

        // never a folder of the signed-in user (like Temp): Undo is written with administrator rights
        static string ProgramData { get { string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); return string.IsNullOrEmpty(pd) ? @"C:\ProgramData" : pd; } }

        public class DriveRoom { public string Root = ""; public bool Usable; public bool System; public long Free, Total; }

        // Where Undo goes for these files, so that no drive is filled up:
        //  1. all files on one other internal drive (D:): Undo on that drive - a move there is a rename and needs no room
        //  2. another internal drive with room for what has to be copied there, plus 2 GB to spare (files from C: free C:)
        //  3. the Windows drive, if what has to be copied onto it still leaves 5 GB or 5% free (files already on C: need no room)
        // Otherwise null: nothing is moved, and Elixar says there isn't enough room.
        public static string ChooseRoot(List<DriveRoom> drives, Dictionary<string, long> bytesByDrive)
        {
            Func<string, string> key = r => (r ?? "").TrimEnd('\\').ToUpperInvariant();
            Func<DriveRoom, long> toCopy = d => bytesByDrive.Where(kv => key(kv.Key) != key(d.Root)).Sum(kv => kv.Value);
            if (bytesByDrive.Count == 1)
            {
                var same = drives.FirstOrDefault(d => d.Usable && !d.System && key(d.Root) == key(bytesByDrive.Keys.First()));
                if (same != null) return same.Root;
            }
            foreach (var d in drives.Where(x => x.Usable && !x.System))
                if (d.Free - toCopy(d) >= (2L << 30)) return d.Root;
            var sys = drives.FirstOrDefault(x => x.System);
            if (sys != null && (toCopy(sys) == 0 || sys.Free - toCopy(sys) >= Math.Max(5L << 30, sys.Total / 20))) return sys.Root;   // files already on it: a rename
            return null;
        }

        public static Func<List<DriveRoom>> DrivesOverride;   // self-test
        static List<DriveRoom> Drives()
        {
            if (DrivesOverride != null) return DrivesOverride();
            var list = new List<DriveRoom>();
            string sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
            foreach (var d in DriveInfo.GetDrives())
                try
                {
                    if (!d.IsReady) continue;
                    bool sys = string.Equals(d.RootDirectory.FullName, sysRoot, StringComparison.OrdinalIgnoreCase);
                    list.Add(new DriveRoom { Root = d.RootDirectory.FullName, System = sys, Free = d.AvailableFreeSpace, Total = d.TotalSize,
                                             Usable = d.DriveType == DriveType.Fixed && d.DriveFormat == "NTFS" });
                }
                catch { }
            return list;
        }

        // a batch for these files on the right drive (see ChooseRoot); an IOException when no drive has room
        public static UndoBatch BeginFor(string label, string kind, IList<string> paths)
        {
            if (OverrideRoot != null || !Win.IsWindows) return Begin(label, kind, 0);
            var by = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                string r = Path.GetPathRoot(Path.GetFullPath(p)) ?? "";
                long sz = Fs.SizeOf(p); long cur; by.TryGetValue(r, out cur); by[r] = cur + sz;
            }
            var drives = Drives();
            string root = ChooseRoot(drives, by);
            if (root == null)
                throw new IOException("There isn't enough free space to keep these files in Undo (" + Fs.Fmt(by.Values.Sum()) + "). Free up some space first, or pick fewer files.");
            var sys = drives.FirstOrDefault(d => d.System);
            string dir = sys != null && string.Equals(root, sys.Root, StringComparison.OrdinalIgnoreCase) ? Path.Combine(ProgramData, FolderName) : Path.Combine(root, FolderName);
            if (!Locked.Lock(dir)) throw new IOException("Undo could not prepare a protected folder on " + root);
            return NewBatch(dir, label, kind);
        }

        public static bool RootIsOffC(string root)
        {
            return root != null && !root.StartsWith("C:", StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<string> AllRoots()
        {
            if (OverrideRoot != null) { yield return OverrideRoot; yield break; }
            if (!Win.IsWindows) { yield return LinuxRoot; yield break; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DriveInfo[] drives = new DriveInfo[0];
            try { drives = DriveInfo.GetDrives(); } catch { }
            foreach (var d in drives)
            {
                string r = null;
                try { if (d.DriveType == DriveType.Fixed && d.IsReady) r = Path.Combine(d.RootDirectory.FullName, FolderName); } catch { }
                if (r != null && seen.Add(r)) yield return r;
            }
            string pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);
            if (seen.Add(pd)) yield return pd;
        }

        static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            var t = sb.ToString().Trim('-');
            return t.Length > 40 ? t.Substring(0, 40) : t;
        }

        public static UndoBatch Begin(string label, string kind, long need) { return NewBatch(RootFor(need), label, kind); }

        static UndoBatch NewBatch(string root, string label, string kind)
        {
            var b = new UndoBatch { Time = DateTime.Now, Label = label, Kind = kind };
            b.Dir = Path.Combine(root, b.Time.ToString("yyyyMMdd-HHmmss") + "-" + Safe(label));
            int n = 1;
            while (Directory.Exists(b.Dir)) b.Dir = Path.Combine(root, b.Time.ToString("yyyyMMdd-HHmmss") + "-" + Safe(label) + "-" + (++n));
            Directory.CreateDirectory(b.Dir);
            return b;
        }

        public static void Commit(UndoBatch b)
        {
            var sb = new StringBuilder();
            sb.Append("label\t").Append(b.Label).Append('\n');
            sb.Append("kind\t").Append(b.Kind).Append('\n');
            sb.Append("time\t").Append(b.Time.ToString("s", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("keep\t").Append(b.Keep.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var it in b.Items) sb.Append("item\t").Append(it[0]).Append('\t').Append(it[1]).Append('\t').Append(it[2]).Append('\n');
            File.WriteAllText(Path.Combine(b.Dir, "manifest.txt"), sb.ToString());
        }

        public static UndoBatch Read(string dir)
        {
            try
            {
                string mf = Path.Combine(dir, "manifest.txt");
                if (!File.Exists(mf)) return null;
                var b = new UndoBatch { Dir = dir };
                bool hasTime = false;
                foreach (var line in File.ReadAllLines(mf))
                {
                    var p = line.Split('\t');
                    if (p[0] == "label" && p.Length >= 2) b.Label = p[1];
                    else if (p[0] == "kind" && p.Length >= 2) b.Kind = p[1];
                    else if (p[0] == "keep" && p.Length >= 2) { int k; if (int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out k)) b.Keep = Math.Max(1, Math.Min(365, k)); }
                    else if (p[0] == "time" && p.Length >= 2) hasTime = DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out b.Time);
                    else if (p[0] == "item" && p.Length >= 4)
                    {
                        b.Items.Add(new[] { p[1], p[2], p[3] });
                        long v; if (long.TryParse(p[3], out v)) b.Bytes += v;
                    }
                }
                return hasTime ? b : null;
            }
            catch { return null; }
        }

        public static List<UndoBatch> List()
        {
            var list = new List<UndoBatch>();
            foreach (var root in AllRoots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    // an Undo folder made before 1.7 is locked now; anything a normal program put there is ignored
                    if (OverrideRoot == null && Sys.IsAdmin()) Locked.Lock(root);
                    bool test = OverrideRoot != null;
                    if (!test && !Locked.IsLocked(root)) continue;
                    foreach (var d in Directory.GetDirectories(root))
                    {
                        if (!test && (!Locked.OwnedByAdmins(d) || !Locked.OwnedByAdmins(Path.Combine(d, "manifest.txt")))) continue;
                        var b = Read(d);
                        if (b != null) list.Add(b);
                    }
                }
                catch { }
            }
            return list.OrderByDescending(b => b.Time).ToList();
        }

        // Moves a file or folder into the batch. Files that cannot be moved (in use) stay where they are.
        public static long MoveIn(UndoBatch b, string path, Action<string> log)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return 0;
            string stored = (b.Items.Count + 1) + "-" + Safe(Path.GetFileName(path.TrimEnd('\\', '/')));
            string dest = Path.Combine(b.Dir, stored);
            long moved = MoveTree(path, dest, log);
            b.Items.Add(new[] { stored, path, moved.ToString(CultureInfo.InvariantCulture) });
            b.Bytes += moved;
            return moved;
        }

        static bool SameVolume(string a, string b)
        {
            try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // copy + delete (works across drives); a file is only removed after its copy succeeded
        public static long MoveTree(string src, string dst, Action<string> log)
        {
            long total = 0;
            if (File.Exists(src))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                long len = 0;
                try { len = new FileInfo(src).Length; } catch { }
                if (!SafeDelete.RealPathMatches(src)) { if (log != null) log("  Skipped " + src + ": a link was in the way"); return 0; }
                try
                {
                    // by handle: the file and the folder it goes into are checked for where they really are
                    if (SameVolume(src, dst)) SafeFile.MoveInto(src, Path.GetDirectoryName(dst), Path.GetFileName(dst));
                    else
                    {
                        SafeFile.Copy(src, dst);
                        bool gone = false;
                        try { if (Win.IsWindows) gone = SafeDelete.Delete(src); else { File.SetAttributes(src, FileAttributes.Normal); File.Delete(src); gone = true; } } catch { }
                        if (!gone) { Fs.Delete(dst); return 0; }   // in use: leave it, drop the copy
                    }
                    return len;
                }
                catch (Exception ex) { if (log != null) log("  Skipped " + src + ": " + ex.Message); return 0; }
            }
            if (!Directory.Exists(src)) return 0;
            var di = new DirectoryInfo(src);
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return 0;   // never follow links
            Directory.CreateDirectory(dst);
            try { foreach (var f in di.GetFiles()) total += MoveTree(f.FullName, Path.Combine(dst, f.Name), log); }
            catch (Exception ex) { if (log != null) log("  Skipped the files in " + src + ": " + ex.Message); }
            try { foreach (var d in di.GetDirectories()) total += MoveTree(d.FullName, Path.Combine(dst, d.Name), log); }
            catch (Exception ex) { if (log != null) log("  Skipped the folders in " + src + ": " + ex.Message); }
            try { if (Directory.GetFileSystemEntries(src).Length == 0) { di.Attributes = FileAttributes.Directory; if (Win.IsWindows) SafeDelete.Delete(src); else Directory.Delete(src, false); } } catch { }
            return total;
        }

        // Puts every item back where it came from (files that exist again are not overwritten).
        public static int Restore(UndoBatch b, Action<string> log)
        {
            int ok = 0;
            if (b.Kind == "startup")
            {
                foreach (var it in b.Items)
                {
                    if (it[0].StartsWith("reg:")) { if (StartupApps.RestoreEntry(it[0], log)) ok++; continue; }
                    string from = Path.Combine(b.Dir, it[0]);
                    if ((File.Exists(from) || Directory.Exists(from)) && SafeTarget(it[1])) { MoveTree(from, it[1], log); ok++; }
                }
            }
            else if (b.Kind == "drivers")
            {
                foreach (var it in b.Items)
                {
                    string dir = Path.Combine(b.Dir, it[0]);
                    string outp;
                    int code = Drivers.Pnputil("/add-driver \"" + Path.Combine(dir, "*.inf") + "\" /subdirs /install", out outp);
                    if (code == 0 || code == 3010) { ok++; if (log != null) log("  Restored driver " + it[1]); }
                    else if (log != null) log("  Could not restore driver " + it[1] + ": " + outp);
                }
            }
            else
            {
                foreach (var it in b.Items)
                {
                    string from = Path.Combine(b.Dir, it[0]);
                    if (!File.Exists(from) && !Directory.Exists(from)) continue;
                    if (!SafeTarget(it[1])) { if (log != null) log("  Not restored (unsafe place): " + it[1]); continue; }
                    MoveTree(from, it[1], log);
                    ok++;
                    if (log != null) log("  Restored " + it[1]);
                }
            }
            try { ChangeLog.Add("Undo", "Put back from Undo: " + b.Label, string.Join(", ", b.Items.Select(i => i.Length > 1 ? i[1] : i[0]).Take(5)) + (b.Items.Count > 5 ? " and " + (b.Items.Count - 5) + " more" : ""),
                                "", ok + " of " + b.Items.Count + " put back", "", ok < b.Items.Count); } catch { }
            if (ok == b.Items.Count) Delete(b, false);
            return ok;
        }

        // somewhere Elixar is allowed to put things back: a plain local path, not a Windows folder, no links on the way
        public static bool SafeTarget(string target)
        {
            try
            {
                if (string.IsNullOrEmpty(target) || !Path.IsPathRooted(target) || target.StartsWith(@"\\") || target.StartsWith("//")) return false;
                string full = Path.GetFullPath(target);
                if (!string.Equals(full.TrimEnd('\\', '/'), target.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
                if (Protect.IsProtected(full)) return false;
                for (string d = full; d != null; d = Path.GetDirectoryName(d))
                    if ((File.Exists(d) || Directory.Exists(d)) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) return false;
                return true;
            }
            catch { return false; }
        }

        public static void Delete(UndoBatch b) { Delete(b, true); }
        static void Delete(UndoBatch b, bool record)
        {
            try { Fs.Delete(b.Dir); } catch { }
            if (record) try { ChangeLog.Add("Files", "Deleted from Undo for good: " + b.Label, b.Dir, b.Bytes > 0 ? Fs.Fmt(b.Bytes) : "", Directory.Exists(b.Dir) ? "Some files are still there" : "(deleted)", "", Directory.Exists(b.Dir)); } catch { }
        }

        public static int PurgeOld(Action<string> log)
        {
            int n = 0;
            foreach (var b in List().Where(x => x.DaysLeft() == 0))
            {
                Delete(b);
                n++;
                if (log != null) log("Undo: deleted \"" + b.Label + "\" from " + b.Time.ToString("d MMM") + " (" + Fs.Fmt(b.Bytes) + "), kept " + b.Keep + " days.");
            }
            return n;
        }

        // one call for a whole cleanup item: everything goes into one Undo entry
        public static void MoveToUndo(string label, IEnumerable<string> paths, Action<string> log) { MoveToUndo(label, paths, log, KeepDays); }

        // returns how many bytes went into Undo (files in use stay where they are)
        public static long MoveToUndo(string label, IEnumerable<string> paths, Action<string> log, int keepDays)
        {
            var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (Fs.MinAgeDays > 0) list = Fs.OldOnly(list);   // the policy keeps newer files for this item
            if (list.Count == 0) return 0;
            var b = BeginFor(label, "files", list);
            b.Keep = Math.Max(1, Math.Min(365, keepDays));
            long moved = 0;
            foreach (var p in list) moved += MoveIn(b, p, log);
            Commit(b);
            ChangeLog.Files("Moved to Undo", label, list, moved, "Kept for " + b.Keep + " days in " + b.Dir + ". Put it back in Tools > Undo.");
            if (log != null) log("  Kept in Undo for " + b.Keep + " days: " + b.Dir + (RootIsOffC(b.Dir) ? "" : "  (no second drive: the space comes back after " + b.Keep + " days or when you delete it in Tools > Undo)"));
            return moved;
        }
    }
}
