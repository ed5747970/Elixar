// Old user accounts: on a PC several people used, accounts nobody signed in to for months keep their whole folder
// (documents, downloads, app data). Elixar lists them and you decide. Never listed: you, anyone signed in now,
// Windows' own accounts, and any account used in the last 6 months (by Windows' record or by the account's own
// registry file, whichever is newer). The account itself stays; only its files on this PC go, removed by Windows'
// own profile removal (the same as System > Advanced > User profiles > Delete), so nothing half-removed is left.
// That can't be undone, so nothing is ticked, Elixar asks twice and offers to copy the folders to another drive first.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public class RawProfile { public string Sid = "", Folder = ""; public DateTime? LastUse; public bool Loaded, Special; }
    public class OldProfile { public string Sid = "", Name = "", Folder = ""; public DateTime LastUse; public long Bytes; }
    public class ProfileScan { public List<OldProfile> Old = new List<OldProfile>(); public int All; public string Problem; }

    public static class OldAccounts
    {
        public const int Months = 6;
        public static Func<List<RawProfile>> ListOverride;          // self-test: what Windows lists
        public static Func<string> MeOverride;                      // self-test: the account running Elixar
        public static Func<string, DateTime?> HiveOverride;         // self-test: when the account's registry file last changed
        public static Func<string, string> RemoveOverride;          // self-test: Windows' profile removal (null = done)
        public static Func<string, string> NameOverride;

        // a real person's account: a local or domain account (S-1-5-21-...) or a work/school (Entra ID) account (S-1-12-1-...)
        public static bool PersonSid(string sid)
        {
            return sid != null && Regex.IsMatch(sid, @"^S-1-(5-21|12-1)(-\d{1,10}){4,6}$");
        }

        // pure: which profiles are old enough to offer
        public static List<OldProfile> Pick(List<RawProfile> all, string me, DateTime now, Func<string, DateTime?> hive)
        {
            var list = new List<OldProfile>();
            DateTime cutoff = now.AddMonths(-Months);
            string users = UsersRoot();
            foreach (var p in all ?? new List<RawProfile>())
            {
                if (p == null || p.Special || p.Loaded || !PersonSid(p.Sid)) continue;
                if (me != null && string.Equals(p.Sid, me, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrEmpty(p.Folder) || !UnderUsers(p.Folder, users)) continue;
                DateTime? h = hive != null ? hive(p.Folder) : null;
                DateTime? last = p.LastUse;
                if (h != null && (last == null || h.Value > last.Value)) last = h;
                if (last == null || last.Value > cutoff) continue;   // unknown or recent: never offered
                list.Add(new OldProfile { Sid = p.Sid, Folder = p.Folder.TrimEnd('\\'), LastUse = last.Value });
            }
            return list.OrderBy(x => x.LastUse).ToList();
        }

        static string UsersRoot()
        {
            if (!Win.IsWindows) return @"C:\Users";
            try { return Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\')) ?? @"C:\Users"; } catch { return @"C:\Users"; }
        }
        // only a folder directly in C:\Users (a profile somewhere else is left alone)
        public static bool UnderUsers(string folder, string users)
        {
            string f = (folder ?? "").TrimEnd('\\'), u = (users ?? "").TrimEnd('\\');
            if (f.Length == 0 || u.Length == 0 || f.Contains("..")) return false;
            int i = f.LastIndexOf('\\');
            return i > 0 && string.Equals(f.Substring(0, i), u, StringComparison.OrdinalIgnoreCase) && f.Length > i + 1;
        }

        static string Me()
        {
            if (MeOverride != null) return MeOverride();
            try { using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) return id.User.Value; } catch { return null; }
        }

        static DateTime? Hive(string folder)
        {
            if (HiveOverride != null) return HiveOverride(folder);
            try { var f = Path.Combine(folder, "NTUSER.DAT"); return File.Exists(f) ? (DateTime?)File.GetLastWriteTime(f) : null; } catch { return null; }
        }

        public static string NameOf(string sid, string folder)
        {
            if (NameOverride != null) return NameOverride(sid);
            try { return new System.Security.Principal.SecurityIdentifier(sid).Translate(typeof(System.Security.Principal.NTAccount)).Value; }
            catch { return Path.GetFileName(folder) + (Lang.Hebrew ? " (החשבון כבר לא נמצא)" : " (the account no longer exists)"); }
        }

        const string ListScript = "$ErrorActionPreference = 'SilentlyContinue'; Get-CimInstance Win32_UserProfile | ForEach-Object { " +
                                  "$t = ''; if ($_.LastUseTime) { $t = $_.LastUseTime.ToString('s') }; " +
                                  "'P' + [char]9 + $_.SID + [char]9 + $_.LocalPath + [char]9 + $t + [char]9 + [int][bool]$_.Loaded + [char]9 + [int][bool]$_.Special }";

        public static List<RawProfile> Parse(string output)
        {
            var list = new List<RawProfile>();
            foreach (var line in (output ?? "").Split('\n'))
            {
                var c = line.TrimEnd('\r').Split('\t');
                if (c.Length < 6 || c[0] != "P") continue;
                DateTime t; DateTime? last = DateTime.TryParse(c[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? (DateTime?)t : null;
                list.Add(new RawProfile { Sid = c[1].Trim(), Folder = c[2].Trim(), LastUse = last, Loaded = c[4].Trim() != "0", Special = c[5].Trim() != "0" });
            }
            return list;
        }

        static List<RawProfile> Raw()
        {
            if (ListOverride != null) return ListOverride();
            if (!Win.IsWindows) return null;
            string o = StoreData.RunPs(ListScript, 60000);
            var l = Parse(o);
            return l.Count == 0 ? null : l;   // Windows always has a few: none means the read failed
        }

        public static ProfileScan Scan(DateTime now, bool sizes)
        {
            var s = new ProfileScan();
            var raw = Raw();
            if (raw == null) { s.Problem = "Windows didn't say which accounts have a folder on this PC, so nothing is offered."; return s; }
            s.All = raw.Count(p => !p.Special && PersonSid(p.Sid));
            s.Old = Pick(raw, Me(), now, Hive);
            foreach (var p in s.Old)
            {
                p.Name = NameOf(p.Sid, p.Folder);
                if (sizes) p.Bytes = Fs.SizeOf(p.Folder);
            }
            return s;
        }

        // Windows' own profile removal for one account, checked once more right before
        public static string Remove(OldProfile p, Action<string> log)
        {
            if (p == null || !PersonSid(p.Sid)) return Lang.Hebrew ? "זה לא חשבון של אדם." : "That isn't a person's account.";
            if (!Policy.Allowed("profiles")) return Lang.Hebrew ? "מדיניות הארגון לא מאפשרת זאת." : "Your organization's policy doesn't allow this.";
            var now = Scan(DateTime.Now, false);
            if (now.Problem != null) return Lang.T(now.Problem);
            if (!now.Old.Any(x => x.Sid == p.Sid)) return Lang.Hebrew ? "החשבון בשימוש עכשיו או שימש לאחרונה: לא הוסר." : "The account is signed in now or was used recently: not removed.";
            string err;
            if (RemoveOverride != null) err = RemoveOverride(p.Sid);
            else
            {
                string o = Sys.RunPowerShell("$ErrorActionPreference = 'Stop'; try { Get-CimInstance Win32_UserProfile -Filter " + Sys.PsQuote("SID='" + p.Sid + "'") +
                                             " | Remove-CimInstance; 'OK' } catch { 'ERROR: ' + $_.Exception.Message }", 3600000);   // a big folder takes a while
                err = o.Contains("OK") && !o.Contains("ERROR") ? null : o.Trim();
            }
            using (ChangeLog.In("Old accounts"))
                ChangeLog.Add("Files", "Removed an old account's files (Windows' profile removal; can't be undone): " + p.Name, p.Folder, Fs.Fmt(p.Bytes), err == null ? "(removed)" : "Not removed", err ?? "", err != null);
            if (log != null) log("Old accounts: " + p.Name + (err == null ? " removed." : " not removed: " + err));
            return err;
        }

        // robocopy (part of Windows) copies the folder to another drive; links inside it are not followed
        public static Func<string, string, string> CopyOverride;
        public static string CopyTo(OldProfile p, string destRoot)
        {
            if (string.IsNullOrEmpty(destRoot)) return "no folder";
            string dest = Path.Combine(destRoot, "Elixar account copy - " + Path.GetFileName(p.Folder) + " " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (CopyOverride != null) return CopyOverride(p.Folder, dest);
            string o = Sys.Run("robocopy.exe", Sys.ArgQuote(p.Folder) + " " + Sys.ArgQuote(dest) + " /E /XJ /R:1 /W:1 /COPY:DAT /DCOPY:T /NP /NFL /NDL", 6 * 3600 * 1000);
            // robocopy says ERROR on a file it couldn't copy; codes 8+ mean failures
            return o.Contains(" ERROR ") ? (Lang.Hebrew ? "חלק מהקבצים לא הועתקו: " : "Some files were not copied: ") + dest : null;
        }

        public static string SameDrive(string destRoot, string folder)
        {
            try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(destRoot)), Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase) ? "same" : null; } catch { return "bad"; }
        }
    }
}
