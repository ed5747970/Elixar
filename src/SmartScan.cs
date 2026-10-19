// Smart scan of one drive: one walk over the drive finds duplicate files and files you haven't used for a long time.
// It only reads. Nothing is ticked for you: you pick, and what you pick goes to the Recycle Bin (it can be undone).
// Windows, apps, AppData, games, the Recycle Bin and restore points are never listed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class OldFile
    {
        public FileInfo File;
        public DateTime LastUsed;
        public string Kind = "";
        public long Size { get { try { return File.Length; } catch { return 0; } } }
    }

    public class SmartResult
    {
        public string Root = "";
        public bool HasWindows, AccessTracked, Stopped;
        public int FilesSeen;
        public long BytesSeen;
        public List<DupGroup> Dups = new List<DupGroup>();
        public List<OldFile> Candidates = new List<OldFile>();   // big enough to matter; filtered by age in the window
        public long DupWaste { get { return Dups.Sum(g => g.Wasted); } }
        public List<OldFile> OlderThan(DateTime cutoff, long minSize) { return Candidates.Where(c => c.LastUsed < cutoff && c.Size >= minSize).OrderByDescending(c => c.Size).ToList(); }
    }

    public static class SmartScan
    {
        public const long DupMin = 1L << 20;       // duplicates: 1 MB and up (photos count)
        public const long OldMin = 10L << 20;      // "not used lately": 10 MB and up

        // another Windows installation on that drive (an old system disk, or dual boot)
        public static bool HasWindows(string root)
        {
            try { return Directory.Exists(Path.Combine(Path.Combine(root, "Windows"), "System32")); } catch { return false; }
        }

        // Does Windows record when a file was last opened? (NTFS "last access"; often switched off to save writes)
        public static bool LastAccessTracked()
        {
            if (!Win.IsWindows) return true;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))
                {
                    object v = k == null ? null : k.GetValue("NtfsDisableLastAccessUpdate");
                    if (v == null) return false;
                    long n = Convert.ToInt64(v) & 0xFFFFFFFFL;
                    return (n & 3) == 0;   // 0 / 0x80000000: on; 1, 2 (system managed), 3: off or not on every drive
                }
            }
            catch { return false; }
        }

        // the latest sign of use: changed, copied here, or (when Windows records it) opened
        public static DateTime LastUsed(FileInfo f, bool access)
        {
            DateTime t = f.LastWriteTime;
            if (f.CreationTime > t) t = f.CreationTime;
            if (access && f.LastAccessTime > t) t = f.LastAccessTime;
            return t;
        }

        static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static SmartScan()
        {
            Action<string, string> k = (kind, exts) => { foreach (var e in exts.Split(' ')) Kinds["." + e] = kind; };
            k("Video", "mp4 mkv mov avi wmv m4v webm mpg mpeg ts m2ts 3gp flv");
            k("Photo", "jpg jpeg png heic heif gif bmp tif tiff raw cr2 cr3 nef arw dng webp psd");
            k("Music", "mp3 flac wav m4a aac ogg wma aiff");
            k("Installer", "exe msi msix appx msixbundle");
            k("Disk image", "iso img vhd vhdx vmdk vdi qcow2 dmg");
            k("Archive", "zip rar 7z tar gz tgz bz2 xz cab");
            k("Document", "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf csv epub");
            k("Backup", "bak old tmp bkf wbcat");
        }
        public static string KindOf(string name) { string k; return Kinds.TryGetValue(Path.GetExtension(name ?? ""), out k) ? k : "Other"; }

        public static SmartResult Run(string root, Action<string> status, CancellationToken ct, bool trustStart = false)
        {
            var r = new SmartResult { Root = root, HasWindows = HasWindows(root), AccessTracked = LastAccessTracked() };
            var all = new List<FileInfo>();
            var stack = new Stack<string>();
            stack.Push(root);
            DateTime lastSay = DateTime.MinValue;
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) { r.Stopped = true; return r; }
                string dir = stack.Pop();
                // with trustStart only the part below the start folder is checked (the self-test's folder is in Temp)
                if (!(trustStart ? Duplicates.Allowed(dir.Length > root.Length ? dir.Substring(root.Length) + Path.DirectorySeparatorChar : "\\") : Duplicates.Allowed(dir))) continue;
                if (status != null && (DateTime.Now - lastSay).TotalMilliseconds > 150) { lastSay = DateTime.Now; status("Looking in " + dir); }
                try
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var f in di.GetFiles())
                    {
                        try
                        {
                            if ((f.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Temporary)) != 0) continue;
                            r.FilesSeen++; r.BytesSeen += f.Length;
                            if (f.Length < DupMin) continue;
                            all.Add(f);
                            if (f.Length >= OldMin) r.Candidates.Add(new OldFile { File = f, LastUsed = LastUsed(f, r.AccessTracked), Kind = KindOf(f.Name) });
                        }
                        catch { }
                    }
                    foreach (var d in di.GetDirectories())
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !Lx.SkipDir(d.FullName, root)) stack.Push(d.FullName);
                }
                catch { }
            }
            if (status != null) status("Comparing files with the same size...");
            r.Dups = Duplicates.Group(all, DupMin, s => { if (status != null && (DateTime.Now - lastSay).TotalMilliseconds > 150) { lastSay = DateTime.Now; status(s); } }, ct);
            if (ct.IsCancellationRequested) r.Stopped = true;
            return r;
        }

        // which copy to keep in a set of duplicates
        public enum Keep { Oldest, Newest, ShortestPath }
        public static FileInfo Keeper(DupGroup g, Keep how)
        {
            if (how == Keep.Newest) return g.Files.OrderByDescending(f => f.LastWriteTime).First();
            if (how == Keep.ShortestPath) return g.Files.OrderBy(f => f.FullName.Length).ThenBy(f => f.CreationTime).First();
            return g.Files.OrderBy(f => f.CreationTime).ThenBy(f => f.FullName.Length).First();
        }
    }
}
