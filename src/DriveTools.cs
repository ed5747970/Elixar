// Cleaning on your other drives (D:, E:, USB drives). Windows and apps keep their leftovers on C:, so the list for other
// drives is short and honest: their Recycle Bin, pieces left by a disk check, and Steam's caches and unfinished downloads
// in game libraries there. Nothing is ticked for you, and the weekly clean never touches other drives.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public static class DriveClean
    {
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct SHQUERYRBINFO { public int cbSize; public long i64Size, i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBinW(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string root, uint flags);

        public static Func<List<DriveInfo>> DrivesOverride;   // self-test
        public static Func<List<CleanItem>> ItemsOverride;   // previews

        // ready drives other than C:, fixed or USB
        public static List<DriveInfo> Others()
        {
            if (DrivesOverride != null) return DrivesOverride();
            var list = new List<DriveInfo>();
            if (!Win.IsWindows) return list;
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                    try
                    {
                        if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                        if (!d.IsReady || d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase) || d.TotalSize < (1L << 30)) continue;
                        list.Add(d);
                    }
                    catch { }
            }
            catch { }
            return list;
        }

        public static long RecycleBinSize(string root)
        {
            try { var i = new SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO)) }; return SHQueryRecycleBinW(root, ref i) == 0 ? i.i64Size : -1; }
            catch { return -1; }
        }

        // "D:\found.000" ... what CHKDSK saved from damaged files; usually unreadable pieces
        public static List<string> CheckDiskLeftovers(string root)
        {
            try { return Directory.GetDirectories(root, "found.*").Where(d => Regex.IsMatch(Path.GetFileName(d), @"^found\.\d{3}$", RegexOptions.IgnoreCase)).ToList(); }
            catch { return new List<string>(); }
        }

        // Steam game libraries on this drive (not Steam's own folder: that one is in "Steam caches" for C:)
        public static List<string> SteamLibrariesOn(string root, string steamPath)
        {
            var libs = new List<string>();
            foreach (var l in Mover.SteamLibraries(steamPath))
            {
                string full;
                try { full = Path.GetFullPath(l).TrimEnd('\\'); } catch { continue; }
                // the list comes from a Steam file any program can change: only plain folders on this drive, never Windows' own
                if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !string.Equals(full + "\\", root, StringComparison.OrdinalIgnoreCase)) continue;
                if (steamPath != null && string.Equals(full, Path.GetFullPath(steamPath).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                if (Protect.IsProtected(full) || !Directory.Exists(Path.Combine(full, "steamapps"))) continue;
                libs.Add(full);
            }
            return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<CleanItem> Items(string steamPath)
        {
            if (ItemsOverride != null) return ItemsOverride();
            var items = new List<CleanItem>();
            foreach (var d in Others())
            {
                string root = d.RootDirectory.FullName, letter = root.Substring(0, 2);   // "D:"
                bool usb = d.DriveType == DriveType.Removable;
                if (!usb)
                    items.Add(new CleanItem
                    {
                        Name = letter + " Recycle Bin", Tier = Tier.Review, DefaultOn = false, Drive = letter, Where = root + "$Recycle.Bin",
                        Info = "Files you deleted from " + letter + ", waiting in its Recycle Bin. Emptying it can't be undone, so look inside first.",
                        Measure = () => Math.Max(0, RecycleBinSize(root)),
                        Clean = log => { try { SHEmptyRecycleBinW(IntPtr.Zero, root, 0x7); } catch (Exception e) { if (log != null) log("  " + e.Message); } }
                    });
                if (CheckDiskLeftovers(root).Count > 0)
                    items.Add(new CleanItem
                    {
                        Name = letter + " disk check leftovers", Tier = Tier.Review, DefaultOn = false, Drive = letter, Where = root + "found.000 ...",
                        Info = "Pieces of damaged files that a disk check (CHKDSK) saved, in folders named found.000. They are rarely readable. Look inside first if a file went missing.",
                        Measure = () => CheckDiskLeftovers(root).Sum(f => Fs.SizeOf(f)),
                        Clean = log => { foreach (var f in CheckDiskLeftovers(root)) Fs.Delete(f); }
                    });
                var libs = SteamLibrariesOn(root, steamPath);
                if (libs.Count > 0)
                {
                    Func<string, List<string>> In = sub => libs.Select(l => Path.Combine(Path.Combine(l, "steamapps"), sub)).Where(Directory.Exists).ToList();
                    items.Add(new CleanItem
                    {
                        Name = letter + " Steam shader cache", Tier = Tier.Safe, DefaultOn = false, Drive = letter, Where = string.Join(", ", libs.Select(l => l + @"\steamapps\shadercache")),
                        Info = "Graphics data Steam games build for your graphics card. Rebuilt automatically (the first start of a game can be slower). Close Steam first.",
                        Procs = new[] { "steam", "steamwebhelper" },
                        Measure = () => In("shadercache").Sum(f => Fs.SizeOf(f)),
                        Clean = log => { foreach (var f in In("shadercache")) Fs.ClearIn(f); }
                    });
                    items.Add(new CleanItem
                    {
                        Name = letter + " unfinished Steam downloads", Tier = Tier.Review, DefaultOn = false, Drive = letter, Where = string.Join(", ", libs.Select(l => l + @"\steamapps\downloading, temp")),
                        Info = "Game updates and installs Steam started but didn't finish. If you still want them, Steam downloads them again. Close Steam first.",
                        Procs = new[] { "steam", "steamwebhelper" },
                        Measure = () => In("downloading").Concat(In("temp")).Sum(f => Fs.SizeOf(f)),
                        Clean = log => { foreach (var f in In("downloading").Concat(In("temp"))) Fs.ClearIn(f); }
                    });
                }
            }
            return items;
        }
    }
}
