// OneDrive "free up space": files in OneDrive that you haven't used for a while can be made online-only.
// They stay in OneDrive and still show in File Explorer, but take no space on this PC until you open one
// (then OneDrive downloads it again, which needs internet). Nothing is deleted, and nothing leaves this PC.
//
// Elixar only touches real OneDrive files: each file must carry OneDrive's own cloud mark (its "reparse tag"),
// so even if the OneDrive folder setting (which any program can change) pointed somewhere else, nothing else
// could be changed. Files you marked "Always keep on this device" are left alone.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class OneDriveAccount { public string Kind = "", Email = "", Folder = ""; }

    public class OneDriveFile
    {
        public string Path = "";
        public long Size;
        public DateTime LastUsed;
        public bool Pinned;
        int Cut { get { return Math.Max(Path.LastIndexOf('\\'), Path.LastIndexOf('/')); } }
        public string Name { get { return Path.Substring(Cut + 1); } }
        public string Folder { get { return Cut < 0 ? "" : Path.Substring(0, Cut); } }
    }

    public class OneDriveScan
    {
        public OneDriveAccount Account;
        public long LocalBytes, OnlineBytes, PinnedBytes;
        public int LocalFiles, OnlineFiles;
        public bool Stopped;
        public List<OneDriveFile> Local = new List<OneDriveFile>();   // on this PC, not pinned, 1 MB and up
        public List<OneDriveFile> NotUsedSince(DateTime cutoff) { return Local.Where(f => f.LastUsed < cutoff).OrderByDescending(f => f.Size).ToList(); }
    }

    public static class OneDrive
    {
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400, FILE_ATTRIBUTE_OFFLINE = 0x1000, FILE_ATTRIBUTE_DIRECTORY = 0x10,
                          FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000, FILE_ATTRIBUTE_PINNED = 0x80000, FILE_ATTRIBUTE_UNPINNED = 0x100000,
                          FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;
        public const long MinSize = 1L << 20;

        public static Func<List<OneDriveAccount>> AccountsOverride;   // previews and the self-test
        public static Func<OneDriveAccount, DateTime, OneDriveScan> ScanOverride;   // previews

        // OneDrive's cloud files carry a reparse tag of the IO_REPARSE_TAG_CLOUD family (0x9000x01A)
        public static bool CloudTag(uint tag) { return (tag & 0xFFFF0FFF) == 0x9000001A; }
        // on this PC: none of the "download on open" marks
        public static bool OnThisPc(uint attrs) { return (attrs & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_OFFLINE)) == 0; }

        // signed-in OneDrive accounts on this Windows account (Personal, Business1, Business2...)
        public static List<OneDriveAccount> Accounts()
        {
            if (AccountsOverride != null) return AccountsOverride();
            var list = new List<OneDriveAccount>();
            if (!Win.IsWindows) return list;
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts"))
                {
                    if (root == null) return list;
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name))
                        {
                            string email = k == null ? null : k.GetValue("UserEmail") as string, folder = k == null ? null : k.GetValue("UserFolder") as string;
                            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(folder)) continue;   // not signed in
                            try { folder = Path.GetFullPath(folder).TrimEnd('\\'); } catch { continue; }
                            if (!Directory.Exists(folder) || Protect.IsProtected(folder) && !string.Equals(folder, Path.Combine(UserDirs.Home, "OneDrive"), StringComparison.OrdinalIgnoreCase) || folder.Length <= 3) continue;
                            list.Add(new OneDriveAccount { Kind = name.StartsWith("Business", StringComparison.OrdinalIgnoreCase) ? "Work or school" : "Personal", Email = email, Folder = folder });
                        }
                }
            }
            catch { }
            return list;
        }

        // ---------- reading the folder without downloading anything ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes; public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr FindFirstFileExW(string name, int infoLevel, out WIN32_FIND_DATA data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFileAttributesW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetFileAttributesW(string name, uint attrs);

        static DateTime T(System.Runtime.InteropServices.ComTypes.FILETIME f)
        {
            long v = ((long)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;
            try { return v <= 0 ? DateTime.MinValue : DateTime.FromFileTime(v); } catch { return DateTime.MinValue; }
        }

        public static OneDriveScan Scan(OneDriveAccount a, DateTime unused, Action<string> status, CancellationToken ct)
        {
            if (ScanOverride != null) return ScanOverride(a, unused);
            var r = new OneDriveScan { Account = a };
            if (!Win.IsWindows) return r;
            bool access = SmartScan.LastAccessTracked();
            var stack = new Stack<string>(); stack.Push(a.Folder);
            DateTime say = DateTime.MinValue;
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) { r.Stopped = true; break; }
                string dir = stack.Pop();
                if (status != null && (DateTime.Now - say).TotalMilliseconds > 200) { say = DateTime.Now; status("Looking in " + dir); }
                WIN32_FIND_DATA d;
                IntPtr h = FindFirstFileExW(SafeDelete.Long(dir.TrimEnd('\\') + "\\*"), 1 /* basic */, out d, 0, IntPtr.Zero, 2 /* large fetch */);
                if (h == new IntPtr(-1)) continue;
                try
                {
                    do
                    {
                        if (d.cFileName == "." || d.cFileName == "..") continue;
                        string p = dir.TrimEnd('\\') + "\\" + d.cFileName;
                        bool reparse = (d.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                        bool cloud = reparse && CloudTag(d.dwReserved0);
                        if ((d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            if (!reparse || cloud) stack.Push(p);   // never a junction or a link to somewhere else
                            continue;
                        }
                        if (!cloud) continue;                       // only OneDrive's own files
                        long size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                        if (!OnThisPc(d.dwFileAttributes)) { r.OnlineFiles++; r.OnlineBytes += size; continue; }
                        r.LocalFiles++; r.LocalBytes += size;
                        bool pinned = (d.dwFileAttributes & FILE_ATTRIBUTE_PINNED) != 0;
                        if (pinned) { r.PinnedBytes += size; continue; }
                        if (size < MinSize) continue;
                        DateTime used = T(d.ftLastWriteTime), c = T(d.ftCreationTime), la = T(d.ftLastAccessTime);
                        if (c > used) used = c;
                        if (access && la > used) used = la;
                        r.Local.Add(new OneDriveFile { Path = p, Size = size, LastUsed = used });
                    } while (FindNextFileW(h, out d));
                }
                finally { FindClose(h); }
            }
            return r;
        }

        // OneDrive's own "Free up space": the file stays in OneDrive and becomes online-only
        public static bool MakeOnlineOnly(string path, OneDriveAccount a)
        {
            if (!Win.IsWindows || a == null) return false;
            try
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(a.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || full.Contains("\\..")) return false;
                WIN32_FIND_DATA d;
                IntPtr h = FindFirstFileExW(SafeDelete.Long(full), 1, out d, 0, IntPtr.Zero, 0);
                if (h == new IntPtr(-1)) return false;
                FindClose(h);
                // still a OneDrive file, still on this PC, not "Always keep on this device"
                if ((d.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0 || (d.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0 || !CloudTag(d.dwReserved0)) return false;
                if ((d.dwFileAttributes & FILE_ATTRIBUTE_PINNED) != 0) return false;
                uint attrs = (d.dwFileAttributes & ~FILE_ATTRIBUTE_PINNED) | FILE_ATTRIBUTE_UNPINNED;
                // SetFileAttributes takes only these bits; the cloud marks themselves are kept by OneDrive
                attrs &= 0x1 | 0x2 | 0x4 | 0x20 | 0x80 | 0x100 | 0x2000 | FILE_ATTRIBUTE_PINNED | FILE_ATTRIBUTE_UNPINNED;
                return SetFileAttributesW(SafeDelete.Long(full), attrs == 0 ? 0x80u : attrs);
            }
            catch { return false; }
        }

        // a quick estimate for the Dashboard: what could be online-only (files unused for 3 months), cached for an hour
        static long cachedFree = -1; static DateTime cachedAt = DateTime.MinValue;
        public static long QuickFreeable()
        {
            if ((DateTime.Now - cachedAt).TotalMinutes < 60) return cachedFree;
            long total = 0;
            try
            {
                foreach (var a in Accounts())
                    total += Scan(a, DateTime.Now.AddMonths(-3), null, CancellationToken.None).NotUsedSince(DateTime.Now.AddMonths(-3)).Sum(f => f.Size);
            }
            catch { }
            cachedAt = DateTime.Now; cachedFree = total;
            return total;
        }
        public static void Forget() { cachedAt = DateTime.MinValue; }
    }
}
