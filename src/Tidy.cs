// Trash and Undo, kept tidy by themselves.
//
// - Files you remove yourself (Smart scan, Duplicates) go to Elixar's Undo, kept for 7, 30 or 90 days (your choice),
//   then deleted for good. Tools > Undo puts them back. Unlike the Recycle Bin this also works for USB drives.
// - The Recycle Bin is emptied a little at a time: only what you deleted more than N days ago, on every internal drive,
//   and only your own Recycle Bin (never another Windows account's). What you deleted recently stays.
// - When C: drops under your alert limit, the Safe clean (the same items as the weekly clean) runs right away, at most
//   once a day.
// These run once a day as "Elixar\Daily tidy", through the same protected launcher as the weekly clean.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public class TidyPrefs
    {
        public static readonly int[] UndoChoices = { 7, 30, 90 };
        public static readonly int[] BinChoices = { 14, 30, 60, 90 };
        public int UndoDays = 30;       // your own files in Undo
        public int BinDays = 0;         // 0: off; else items deleted more than this many days ago leave the Recycle Bin
        public int BinChoice = 30;      // the days used when auto-delete is switched on (also from the Dashboard)
        public bool LowClean = false;   // run the Safe clean when C: is under the alert limit
        public string LastLowClean = "";
        public bool AnyDaily { get { return BinDays > 0 || LowClean; } }

        public static string OverrideFile;   // self-test
        static string File_ { get { return OverrideFile ?? Paths.FileIn("tidy.txt"); } }

        public static TidyPrefs Parse(IEnumerable<string> lines)
        {
            var p = new TidyPrefs();
            foreach (var line in lines ?? new string[0])
            {
                int i = line.IndexOf('='); if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                int n; bool num = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);
                if (k == "undoDays" && num && UndoChoices.Contains(n)) p.UndoDays = n;            // anything else: the default
                else if (k == "binDays" && num && (n == 0 || BinChoices.Contains(n))) p.BinDays = n;
                else if (k == "binChoice" && num && BinChoices.Contains(n)) p.BinChoice = n;
                else if (k == "lowClean") p.LowClean = v == "1";
                else if (k == "lastLowClean" && Regex.IsMatch(v, @"^\d{4}-\d\d-\d\d$")) p.LastLowClean = v;
            }
            return p;
        }

        public static TidyPrefs Load()
        {
            try { string f = File_; if (File.Exists(f)) return Parse(File.ReadAllLines(f)); } catch { }
            return new TidyPrefs();
        }

        public string Text()
        {
            return "undoDays=" + UndoDays + "\nbinDays=" + BinDays + "\nbinChoice=" + BinChoice + "\nlowClean=" + (LowClean ? 1 : 0) + "\nlastLowClean=" + LastLowClean + "\n";
        }

        public bool Save()
        {
            try { File.WriteAllText(File_, Text()); return true; } catch { return false; }
        }
    }

    public static class RecycleAging
    {
        public class Entry { public string Info = "", Data = "", Original = ""; public DateTime Deleted; public long Size; }

        // Windows' record of a deleted file ($I...): version, original size, when it was deleted, where it was
        public static Entry ParseInfo(byte[] b)
        {
            if (b == null || b.Length < 24) return null;
            long ver = BitConverter.ToInt64(b, 0);
            if (ver != 1 && ver != 2) return null;
            var e = new Entry { Size = Math.Max(0, BitConverter.ToInt64(b, 8)) };
            long ft = BitConverter.ToInt64(b, 16);
            try { e.Deleted = ft <= 0 ? DateTime.MinValue : DateTime.FromFileTime(ft); } catch { return null; }
            try
            {
                if (ver == 2 && b.Length >= 28)
                {
                    int len = BitConverter.ToInt32(b, 24);
                    if (len > 0 && len <= 32767 && 28 + len * 2 <= b.Length) e.Original = Encoding.Unicode.GetString(b, 28, len * 2).TrimEnd('\0');
                }
                else if (ver == 1) e.Original = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24)).Split('\0')[0];
            }
            catch { }
            return e;
        }

        public static byte[] MakeInfo(DateTime deleted, long size, string original)   // self-test
        {
            var name = Encoding.Unicode.GetBytes((original ?? "") + "\0");
            var b = new byte[28 + name.Length];
            BitConverter.GetBytes(2L).CopyTo(b, 0); BitConverter.GetBytes(size).CopyTo(b, 8); BitConverter.GetBytes(deleted.ToFileTime()).CopyTo(b, 16);
            BitConverter.GetBytes(name.Length / 2).CopyTo(b, 24); name.CopyTo(b, 28);
            return b;
        }

        static readonly Regex InfoName = new Regex(@"^\$I[A-Za-z0-9]{6}(\.[^\\/:]{0,200})?$");

        static bool Plain(FileSystemInfo i) { return (i.Attributes & FileAttributes.ReparsePoint) == 0; }

        // the deleted items in one Recycle Bin folder (a drive's $Recycle.Bin\<your account>)
        public static List<Entry> Read(string binDir)
        {
            var list = new List<Entry>();
            try
            {
                var di = new DirectoryInfo(binDir);
                if (!di.Exists || !Plain(di)) return list;
                foreach (var f in di.GetFiles("$I*"))
                {
                    try
                    {
                        if (!InfoName.IsMatch(f.Name) || !Plain(f) || f.Length > 65536) continue;
                        var e = ParseInfo(File.ReadAllBytes(f.FullName));
                        if (e == null) continue;
                        e.Info = f.FullName; e.Data = Path.Combine(binDir, "$R" + f.Name.Substring(2));
                        list.Add(e);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        // your own Recycle Bin on every internal drive (never another Windows account's), and never through a link
        public static Func<List<string>> BinsOverride;   // self-test
        public static List<string> UserBins()
        {
            if (BinsOverride != null) return BinsOverride();
            var bins = new List<string>();
            if (!Win.IsWindows) return bins;
            string sid = null;
            try { using (var id = WindowsIdentity.GetCurrent()) sid = id.User == null ? null : id.User.Value; } catch { }
            if (string.IsNullOrEmpty(sid)) return bins;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var top = new DirectoryInfo(Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin"));
                    if (!top.Exists || !Plain(top)) continue;
                    var mine = new DirectoryInfo(Path.Combine(top.FullName, sid));
                    if (mine.Exists && Plain(mine)) bins.Add(mine.FullName);
                }
                catch { }
            }
            return bins;
        }

        public static void Stats(int days, out long total, out long old)
        {
            total = 0; old = 0;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, days));
            foreach (var bin in UserBins())
                foreach (var e in Read(bin)) { total += e.Size; if (e.Deleted > DateTime.MinValue && e.Deleted < cutoff) old += e.Size; }
        }

        // removes what was deleted before the cutoff; returns the bytes freed (as Windows recorded them)
        public static long EmptyOldIn(string binDir, DateTime cutoff, out int count)
        {
            long bytes = 0; count = 0;
            foreach (var e in Read(binDir).Where(x => x.Deleted > DateTime.MinValue && x.Deleted < cutoff))
            {
                try
                {
                    if (File.Exists(e.Data) || Directory.Exists(e.Data)) Fs.Delete(e.Data);   // link-proof: a link is removed, never followed
                    if (File.Exists(e.Data) || Directory.Exists(e.Data)) continue;            // in use: keep its record too
                    Fs.Delete(e.Info);
                    count++; bytes += e.Size;
                }
                catch { }
            }
            return bytes;
        }

        public static long EmptyOld(int days, Action<string> log, out int count)
        {
            long bytes = 0; count = 0;
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, days));
            foreach (var bin in UserBins())
            {
                int n; bytes += EmptyOldIn(bin, cutoff, out n); count += n;
                if (n > 0 && log != null) log("  Recycle Bin on " + bin.Substring(0, 2) + ": removed " + n + " items deleted more than " + days + " days ago.");
            }
            return bytes;
        }
    }

    public static class Tidy
    {
        // once a day, with administrator rights (the "Elixar\Daily tidy" schedule)
        public static int Daily()
        {
            if (Win.IsWindows && !Sys.IsAdmin()) { Automation.Log("Daily tidy skipped: it needs administrator rights."); return 1; }
            var p = TidyPrefs.Load();
            Automation.Log("Daily tidy started.");
            try { Quarantine.PurgeOld(m => Automation.Log(m)); } catch (Exception e) { Automation.Log("  Undo: " + e.Message); }
            if (p.BinDays > 0)
            {
                int n; long b = RecycleAging.EmptyOld(p.BinDays, m => Automation.Log(m), out n);
                Automation.Log("  Recycle Bin: " + (n == 0 ? "nothing older than " + p.BinDays + " days." : n + " old items removed (" + Fs.Fmt(b) + ")."));
                if (n > 0) try { Activity.Add("clean", "Daily tidy: " + n + " old items left the Recycle Bin", b); } catch { }
            }
            string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (p.LowClean && p.LastLowClean != today)
            {
                int pct = 15;
                try { pct = Automation.PercentFromArgs(Automation.Query(Automation.CheckTask).Arguments, 15); } catch { }
                if (Automation.ShouldWarn(Sys.Free(), Sys.Total(), pct))
                {
                    Automation.Log("  C: is under " + pct + "% free: running the Safe clean now.");
                    p.LastLowClean = today; p.Save();
                    Automation.AutoClean(true);
                }
            }
            Automation.Log("Daily tidy done.");
            return 0;
        }
    }
}
