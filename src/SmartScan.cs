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
        public List<string> EmptyFolders = new List<string>();   // the topmost empty folders (their empty sub-folders go with them)
        public List<FileInfo> Photos = new List<FileInfo>();     // for "similar photos"
        public List<List<FileInfo>> Similar = new List<List<FileInfo>>();
        public long SimilarWaste { get { return Similar.Sum(g => g.Sum(f => { try { return f.Length; } catch { return 0L; } }) - g.Max(f => { try { return f.Length; } catch { return 0L; } })); } }
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

        public static SmartResult Run(string root, Action<string> status, CancellationToken ct, bool trustStart = false, bool similar = false)
        {
            var r = new SmartResult { Root = root, HasWindows = HasWindows(root), AccessTracked = LastAccessTracked() };
            var all = new List<FileInfo>();
            // for empty folders: how many files each folder has, and its sub-folders
            var fileCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var kids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
                    var files = di.GetFiles();
                    fileCount[dir] = files.Length;
                    var sub = new List<string>(); kids[dir] = sub;
                    foreach (var f in files)
                    {
                        try
                        {
                            if ((f.Attributes & (FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Temporary)) != 0) continue;
                            r.FilesSeen++; r.BytesSeen += f.Length;
                            if (f.Length < DupMin) continue;
                            all.Add(f);
                            if (f.Length >= OldMin) r.Candidates.Add(new OldFile { File = f, LastUsed = LastUsed(f, r.AccessTracked), Kind = KindOf(f.Name) });
                            if (similar && f.Length >= (50L << 10) && PhotoExt(f.Name) && r.Photos.Count < 20000) r.Photos.Add(f);
                        }
                        catch { }
                    }
                    foreach (var d in di.GetDirectories())
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !Lx.SkipDir(d.FullName, root))
                        {
                            stack.Push(d.FullName);
                            // hidden or system folders, and ".config"-style folders, belong to apps: never offered as empty
                            if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !d.Name.StartsWith(".")) sub.Add(d.FullName);
                            else sub.Add("|keep");
                        }
                        else sub.Add("|keep");
                }
                catch { }
            }
            r.EmptyFolders = EmptyTops(root, fileCount, kids);
            if (status != null) status("Comparing files with the same size...");
            r.Dups = Duplicates.Group(all, DupMin, s => { if (status != null && (DateTime.Now - lastSay).TotalMilliseconds > 150) { lastSay = DateTime.Now; status(s); } }, ct);
            if (similar && !ct.IsCancellationRequested)
            {
                var exact = new HashSet<string>(r.Dups.SelectMany(g => g.Files.Skip(1)).Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
                r.Similar = SimilarPhotos.Find(r.Photos.Where(p => !exact.Contains(p.FullName)).ToList(), status, ct);
            }
            if (ct.IsCancellationRequested) r.Stopped = true;
            return r;
        }

        static readonly string[] PhotoExts = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
        public static bool PhotoExt(string name) { return PhotoExts.Contains(Path.GetExtension(name ?? "").ToLowerInvariant()); }

        // empty = no files anywhere inside; only the topmost such folder is listed, never the scanned folder itself
        static List<string> EmptyTops(string root, Dictionary<string, int> files, Dictionary<string, List<string>> kids)
        {
            var memo = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Func<string, bool> empty = null;
            empty = d =>
            {
                bool e;
                if (memo.TryGetValue(d, out e)) return e;
                int n; List<string> k;
                if (d == "|keep" || !files.TryGetValue(d, out n) || n > 0 || !kids.TryGetValue(d, out k)) e = false;   // not read (blocked, no access) counts as not empty
                else e = k.All(x => empty(x));
                memo[d] = e;
                return e;
            };
            var tops = new List<string>();
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                List<string> k;
                if (!kids.TryGetValue(d, out k)) continue;
                foreach (var c in k)
                {
                    if (c == "|keep") continue;
                    if (empty(c) && !Protect.IsProtected(c)) tops.Add(c);
                    else stack.Push(c);
                }
            }
            return tops.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // an empty folder is removed only if it is still empty (nothing was put in it since the scan). Each folder is removed
        // on its own, deepest first, with Windows' "remove an empty folder" call, which fails if anything is in it, so a file
        // saved there a moment ago is never deleted. A link inside (to another folder) makes Elixar leave the whole folder.
        public static bool RemoveEmpty(string dir)
        {
            try
            {
                if (!Directory.Exists(dir) || Protect.IsProtected(dir)) return false;
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) return false;
                var all = new List<string>(); var st = new Stack<string>(); st.Push(dir);
                while (st.Count > 0)
                {
                    string d = st.Pop(); all.Add(d);
                    foreach (var e in new DirectoryInfo(d).GetFileSystemInfos())
                    {
                        if (!(e is DirectoryInfo) || (e.Attributes & FileAttributes.ReparsePoint) != 0) return false;   // a file or a link: not empty
                        st.Push(e.FullName);
                    }
                    if (all.Count > 5000) return false;
                }
                foreach (var d in all.OrderByDescending(x => x.Length)) Directory.Delete(d, false);
                return !Directory.Exists(dir);
            }
            catch { return false; }
        }

        // ---------- NTFS compression for files you keep but rarely use ----------
        public static bool Compressible(string kind) { return kind != "Video" && kind != "Photo" && kind != "Music" && kind != "Archive"; }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern uint GetCompressedFileSizeW(string name, out uint high);
        public static long OnDisk(string path)
        {
            if (!Win.IsWindows) { try { return new FileInfo(path).Length; } catch { return 0; } }
            uint hi; uint lo = GetCompressedFileSizeW(SafeDelete.Long(Path.GetFullPath(path)), out hi);
            return lo == 0xFFFFFFFF && System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 0 ? 0 : ((long)hi << 32) | lo;
        }

        public static bool OnNtfs(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct FILE_STANDARD_INFO { public long AllocationSize, EndOfFile; public uint NumberOfLinks; public byte DeletePending, Directory; }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int cls, out FILE_STANDARD_INFO info, uint size);

        // how many names a file has (more than one: it is also somewhere else, maybe in Windows), read without following links
        public static uint Links(string path)
        {
            if (!Win.IsWindows) return 1;
            using (var h = CreateFileW(SafeDelete.Long(Path.GetFullPath(path)), 0x80 /* read attributes */, 7, IntPtr.Zero, 3, 0x00200000 /* open the link itself */, IntPtr.Zero))
            {
                FILE_STANDARD_INFO i;
                if (h.IsInvalid || !GetFileInformationByHandleEx(h, 1, out i, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FILE_STANDARD_INFO))) || i.Directory != 0) return 0;
                return i.NumberOfLinks;
            }
        }

        // Windows' own compact.exe, a few files at a time; returns the space saved. A file with a second name (a hard link)
        // is skipped: that second name could be a Windows file, and Elixar runs as administrator.
        public static long Compress(List<string> paths, Action<string> log)
        {
            if (!Win.IsWindows) return 0;
            var ok = paths.Where(p => { try { var fi = new FileInfo(p); return fi.Exists && (fi.Attributes & FileAttributes.ReparsePoint) == 0 && !Protect.IsProtected(p) && OnNtfs(p) && p.IndexOf('"') < 0 && Links(p) == 1; } catch { return false; } }).ToList();
            long before = ok.Sum(p => OnDisk(p));
            for (int i = 0; i < ok.Count; i += 20)
            {
                var batch = ok.Skip(i).Take(20).ToList();
                try { Sys.Run("compact.exe", "/c /a /i /q " + string.Join(" ", batch.Select(p => "\"" + p + "\"")), 30 * 60 * 1000); }
                catch (Exception e) { if (log != null) log("  " + e.Message); }
            }
            return Math.Max(0, before - ok.Sum(p => OnDisk(p)));
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
