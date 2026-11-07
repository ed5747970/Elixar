// Leftover installer files: C:\Windows\Installer keeps a copy of every program's installer (.msi) and update (.msp),
// so Windows can repair or remove the program later. When programs are removed or updated, old copies often stay.
// Elixar asks Windows itself which copies are still used and offers only the others, moved to Undo (not deleted).
//
// Safety, in order:
//   1. Windows Installer's own list (every program and update, for every account) AND the registry's copy of it:
//      a file named by either is never offered
//   2. if either list can't be read, looks empty, or doesn't match any file in the folder, nothing is offered
//   3. Windows Installer busy (something installing right now): nothing is offered
//   4. only .msi and .msp files directly in the folder; never the folders in it ($PatchCache$, icons)
//   5. nothing changed in the last 30 days
//   6. moved to Undo (kept 7 days or more, Restore puts them back exactly), never deleted here
// Business: its own policy rule ("installers").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class InstallerFile { public string Path; public long Bytes; public DateTime Changed; public string From = ""; }

    public class InstallerScan
    {
        public List<InstallerFile> Orphans = new List<InstallerFile>();
        public int Files, Products, Patches, Used, UsedHere;
        public string Problem;   // why nothing is offered (null = the lists were read fine)
        public long Bytes { get { return Orphans.Sum(o => o.Bytes); } }
    }

    public static class InstallerFiles
    {
        public static string FolderOverride;                       // self-test
        public static Func<InstallerScan, HashSet<string>> UsedOverride;   // self-test: the files the programs use
        public static Func<bool> BusyOverride;
        public const int MinAgeDays = 30;

        public static string Folder { get { return FolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer"); } }

        public static InstallerScan Scan(DateTime now)
        {
            var s = new InstallerScan();
            if (!Win.IsWindows && FolderOverride == null) { s.Problem = "Only on Windows."; return s; }
            try
            {
                if (BusyOverride != null ? BusyOverride() : Busy()) { s.Problem = "Windows Installer is busy (something is being installed). Try again in a few minutes."; return s; }
                var files = List(Folder);
                s.Files = files.Count;
                HashSet<string> used;
                if (UsedOverride != null) used = UsedOverride(s);
                else
                {
                    var api = FromApi(s);
                    var reg = FromRegistry();
                    if (api == null || api.Count == 0 || s.Products == 0) { s.Problem = "Windows Installer's list of programs couldn't be read, so nothing is offered."; return s; }
                    if (reg.Count == 0) { s.Problem = "The registry's list of installed programs couldn't be read, so nothing is offered."; return s; }
                    used = new HashSet<string>(api, StringComparer.OrdinalIgnoreCase);
                    used.UnionWith(reg);   // named by either list: kept
                }
                s.Used = used.Count;
                s.Orphans = Find(files, used, now, out s.UsedHere);
                if (s.Files > 0 && s.UsedHere == 0) { s.Orphans.Clear(); s.Problem = "None of the files in the folder matched Windows' list (that isn't normal), so nothing is offered."; }
            }
            catch (Exception e) { s.Orphans.Clear(); s.Problem = "The check stopped: " + e.Message; }
            return s;
        }

        // the files in the folder (not in its folders), .msi and .msp only
        public static List<InstallerFile> List(string folder)
        {
            var l = new List<InstallerFile>();
            if (!Directory.Exists(folder)) return l;
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                string ext = f.Extension.ToLowerInvariant();
                if (ext != ".msi" && ext != ".msp") continue;
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                l.Add(new InstallerFile { Path = f.FullName, Bytes = f.Length, Changed = f.LastWriteTime });
            }
            return l;
        }

        static string Norm(string p) { try { return Path.GetFullPath(p.Trim().Trim('"')).TrimEnd('\\').ToLowerInvariant(); } catch { return (p ?? "").Trim().ToLowerInvariant(); } }

        // the rule itself: not used by any program or update, and not changed lately
        public static List<InstallerFile> Find(List<InstallerFile> files, HashSet<string> used, DateTime now, out int usedHere)
        {
            var u = new HashSet<string>(used.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Norm));
            usedHere = files.Count(f => u.Contains(Norm(f.Path)));
            return files.Where(f => !u.Contains(Norm(f.Path)) && (now - f.Changed).TotalDays >= MinAgeDays).OrderByDescending(f => f.Bytes).ToList();
        }

        static bool Busy()
        {
            try
            {
                // the installer's own lock: held while anything is being installed or removed
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\InProgress")) if (k != null) return true;
                using (var m = System.Threading.Mutex.OpenExisting(@"Global\_MSIExecute")) return true;
            }
            catch (System.Threading.WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }   // it exists but isn't ours: assume busy
            catch { return false; }
        }

        // ---- Windows Installer's own list ----
        const uint MSIINSTALLCONTEXT_ALL = 7, MSIPATCHSTATE_ALL = 15, ERROR_SUCCESS = 0, ERROR_NO_MORE_ITEMS = 259, ERROR_MORE_DATA = 234;
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiEnumProductsEx(string szProductCode, string szUserSid, uint dwContext, uint dwIndex, StringBuilder szInstalledProductCode, out uint pdwInstalledContext, StringBuilder szSid, ref uint pcchSid);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiGetProductInfoEx(string szProductCode, string szUserSid, uint dwContext, string szProperty, StringBuilder szValue, ref uint pcchValue);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiEnumPatchesEx(string szProductCode, string szUserSid, uint dwContext, uint dwFilter, uint dwIndex, StringBuilder szPatchCode, StringBuilder szTargetProductCode, out uint pdwTargetProductContext, StringBuilder szTargetUserSid, ref uint pcchTargetUserSid);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiGetPatchInfoEx(string szPatchCode, string szProductCode, string szUserSid, uint dwContext, string szProperty, StringBuilder lpValue, ref uint pcchValue);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiGetSummaryInformation(IntPtr hDatabase, string szDatabasePath, uint uiUpdateCount, out IntPtr phSummaryInfo);
        [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiSummaryInfoGetProperty(IntPtr hSummaryInfo, uint uiProperty, out uint puiDataType, out int piValue, ref System.Runtime.InteropServices.ComTypes.FILETIME pftValue, StringBuilder szValueBuf, ref uint pcchValueBuf);
        [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr hAny);

        static string Info(Func<StringBuilder, uint, Tuple<uint, uint>> call)
        {
            uint len = 260;
            for (int i = 0; i < 3; i++)
            {
                var sb = new StringBuilder((int)len + 1);
                var r = call(sb, len + 1);
                if (r.Item1 == ERROR_SUCCESS) return sb.ToString();
                if (r.Item1 != ERROR_MORE_DATA) return null;
                len = r.Item2 + 1;
            }
            return null;
        }

        // null = the list couldn't be read
        static HashSet<string> FromApi(InstallerScan s)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                for (uint i = 0; ; i++)
                {
                    var code = new StringBuilder(39); var sid = new StringBuilder(256); uint sidLen = 256, ctx;
                    uint r = MsiEnumProductsEx(null, "s-1-1-0", MSIINSTALLCONTEXT_ALL, i, code, out ctx, sid, ref sidLen);
                    if (r == ERROR_NO_MORE_ITEMS) break;
                    if (r != ERROR_SUCCESS) return null;   // a list read halfway is no list
                    s.Products++;
                    string pc = code.ToString(), us = sid.Length > 0 ? sid.ToString() : null; uint c = ctx;
                    string lp = Info((sb, n) => { uint l = n; uint e = MsiGetProductInfoEx(pc, us, c, "LocalPackage", sb, ref l); return Tuple.Create(e, l); });
                    if (!string.IsNullOrEmpty(lp)) used.Add(lp);
                }
                for (uint i = 0; ; i++)
                {
                    var patch = new StringBuilder(39); var prod = new StringBuilder(39); var sid = new StringBuilder(256); uint sidLen = 256, ctx;
                    uint r = MsiEnumPatchesEx(null, "s-1-1-0", MSIINSTALLCONTEXT_ALL, MSIPATCHSTATE_ALL, i, patch, prod, out ctx, sid, ref sidLen);
                    if (r == ERROR_NO_MORE_ITEMS) break;
                    if (r != ERROR_SUCCESS) return null;
                    s.Patches++;
                    string pa = patch.ToString(), pr = prod.ToString(), us = sid.Length > 0 ? sid.ToString() : null; uint c = ctx;
                    string lp = Info((sb, n) => { uint l = n; uint e = MsiGetPatchInfoEx(pa, pr, us, c, "LocalPackage", sb, ref l); return Tuple.Create(e, l); });
                    if (!string.IsNullOrEmpty(lp)) used.Add(lp);
                }
            }
            catch { return null; }
            return used;
        }

        // the same list as Windows keeps it in the registry (every account's programs and updates)
        static HashSet<string> FromRegistry()
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var ud = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData"))
                {
                    if (ud == null) return used;
                    foreach (var sid in ud.GetSubKeyNames())
                        using (var sk = ud.OpenSubKey(sid))
                        {
                            if (sk == null) continue;
                            using (var products = sk.OpenSubKey("Products"))
                                if (products != null)
                                    foreach (var p in products.GetSubKeyNames())
                                        using (var ip = products.OpenSubKey(p + @"\InstallProperties"))
                                        { var v = ip == null ? null : ip.GetValue("LocalPackage") as string; if (!string.IsNullOrEmpty(v)) used.Add(v); }
                            using (var patches = sk.OpenSubKey("Patches"))
                                if (patches != null)
                                    foreach (var p in patches.GetSubKeyNames())
                                        using (var pk = patches.OpenSubKey(p))
                                        { var v = pk == null ? null : pk.GetValue("LocalPackage") as string; if (!string.IsNullOrEmpty(v)) used.Add(v); }
                        }
                }
            }
            catch { }
            return used;
        }

        // "From": the title or subject the installer file carries (Adobe Acrobat Reader, an Office update...)
        public static string WhatItIs(string path)
        {
            if (!Win.IsWindows) return "";
            IntPtr h = IntPtr.Zero;
            try
            {
                if (MsiGetSummaryInformation(IntPtr.Zero, path, 0, out h) != ERROR_SUCCESS) return "";
                foreach (uint pid in new uint[] { 3, 2 })   // subject, then title
                {
                    uint type; int iv; var ft = new System.Runtime.InteropServices.ComTypes.FILETIME(); uint len = 512; var sb = new StringBuilder(513);
                    if (MsiSummaryInfoGetProperty(h, pid, out type, out iv, ref ft, sb, ref len) == ERROR_SUCCESS && sb.Length > 0) return sb.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
                }
                return "";
            }
            catch { return ""; }
            finally { if (h != IntPtr.Zero) try { MsiCloseHandle(h); } catch { } }
        }

        // moved to Undo (never deleted here); returns how many bytes went
        public static long MoveToUndo(List<InstallerFile> files, Action<string> log)
        {
            if (files == null || files.Count == 0) return 0;
            // checked again right before moving: still there, still not used, still old enough
            var again = Scan(DateTime.Now);
            if (again.Problem != null) { if (log != null) log("Nothing moved: " + again.Problem); return 0; }
            var still = new HashSet<string>(again.Orphans.Select(o => o.Path), StringComparer.OrdinalIgnoreCase);
            var go = files.Where(f => still.Contains(f.Path)).Select(f => f.Path).ToList();
            if (go.Count < files.Count && log != null) log((files.Count - go.Count) + " file(s) left alone: they changed or a program uses them now.");
            using (ChangeLog.In("Installer files"))
                return Quarantine.MoveToUndo("Leftover installer files (" + go.Count + ")", go, log, Quarantine.KeepDays);
        }
    }
}
