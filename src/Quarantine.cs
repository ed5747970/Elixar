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
        public int DaysLeft(int keepDays) { return Math.Max(0, keepDays - (int)Math.Floor((DateTime.Now - Time).TotalDays)); }
    }

    public static class Quarantine
    {
        public const int KeepDays = 7;
        public const string FolderName = "Elixar Undo";
        public static string OverrideRoot;   // used by the self-test only

        // A second fixed drive with room to spare, else C:\ProgramData.
        public static string RootFor(long need)
        {
            if (OverrideRoot != null) return OverrideRoot;
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
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string root = Path.Combine(string.IsNullOrEmpty(pd) ? Path.GetTempPath() : pd, FolderName);
            Locked.Lock(root);
            return root;
        }

        public static bool RootIsOffC(string root)
        {
            return root != null && !root.StartsWith("C:", StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<string> AllRoots()
        {
            if (OverrideRoot != null) { yield return OverrideRoot; yield break; }
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

        public static UndoBatch Begin(string label, string kind, long need)
        {
            string root = RootFor(need);
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
            try { foreach (var f in di.GetFiles()) total += MoveTree(f.FullName, Path.Combine(dst, f.Name), log); } catch { }
            try { foreach (var d in di.GetDirectories()) total += MoveTree(d.FullName, Path.Combine(dst, d.Name), log); } catch { }
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
            if (ok == b.Items.Count) Delete(b);
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

        public static void Delete(UndoBatch b)
        {
            try { Fs.Delete(b.Dir); } catch { }
        }

        public static int PurgeOld(Action<string> log)
        {
            int n = 0;
            foreach (var b in List().Where(x => x.DaysLeft(KeepDays) == 0))
            {
                Delete(b);
                n++;
                if (log != null) log("Undo: deleted \"" + b.Label + "\" from " + b.Time.ToString("d MMM") + " (" + Fs.Fmt(b.Bytes) + "), kept " + KeepDays + " days.");
            }
            return n;
        }

        // one call for a whole cleanup item: everything goes into one Undo entry
        public static void MoveToUndo(string label, IEnumerable<string> paths, Action<string> log)
        {
            var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
            if (list.Count == 0) return;
            long need = list.Sum(p => Fs.SizeOf(p));
            var b = Begin(label, "files", need);
            foreach (var p in list) MoveIn(b, p, log);
            Commit(b);
            if (log != null) log("  Kept in Undo for " + KeepDays + " days: " + b.Dir + (RootIsOffC(b.Dir) ? "" : "  (no second drive: the space comes back after " + KeepDays + " days or when you delete it in Tools > Undo)"));
        }
    }
}
