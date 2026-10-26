// Cure (Elixar for Business): Cure Agent on each PC sends a status report to the company's own Cure Server; IT reads them
// in the Cure Console (a web page on that server). Stage 1 is reports only: the server answers a report with nothing but
// "report again in N minutes". The agent has no code that runs, downloads or changes anything because of a server's answer.
//
// How a PC proves who it is
//   1. enroll: the agent sends the company's enrollment token (from Group Policy) once; the server gives it a device id
//      and a 32-byte device key. The key is kept with Windows' machine encryption (DPAPI) in a folder only administrators
//      and SYSTEM can open.
//   2. report: every report is signed: HMAC-SHA256(device key, time \n nonce \n SHA-256 of the body). The server refuses
//      a report more than 5 minutes off, a nonce it has seen, or a wrong signature.
// How the PC knows it's the company's server
//   HTTPS (TLS 1.2+), and when a certificate fingerprint is set (Group Policy), only that certificate is accepted.
// What is never sent: file names, file contents, emails, browsing, who is signed in, passwords or keys.

#if BUSINESS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace CDriveCleaner
{
    public class CureDrive { public string Letter = ""; public long Total, Free; }
    public class CureDisk { public string Name = "", Kind = "", Level = "", Letters = ""; public int Wear = -1, Temp = -1; public string[] Reasons = new string[0]; }
    // a cleaning item (the Cleaner's list) with its size on this PC, all accounts together
    public class CureItem { public string Name = "", Tier = ""; public long Bytes = -1; public bool Auto; }
    // a place on C: (the Dashboard's Details view), all accounts together
    public class CurePlace { public string Name = "", Tier = "", Action = ""; public long Bytes = -1; }
    public class CureApp { public string Name = "", Publisher = "", LastUsed = "", Installed = ""; public long Bytes = -1; }

    // what a PC reports (all optional except Computer; numbers are -1 when unknown)
    public class CureReport
    {
        public int Format = 2;
        public string Agent = "", Elixar = "", Edition = "", Computer = "", Company = "", Ou = "", Model = "", Windows = "";
        public string Time = "", BootTime = "", MeasuredAt = "", LastClean = "", DiskLevel = "", PolicyMode = "", PolicyFingerprint = "";
        public bool PendingReboot, EventLog;
        public long Total = -1, Free = -1, CleanSafe = -1, CleanReview = -1, CleanCaution = -1, LastCleanBytes = -1, Saved = -1, SavedThisMonth = -1, UndoBytes = -1;
        public int AppUpdates = -1, StartupCount = -1;
        public CureDrive[] Drives = new CureDrive[0];
        public CureDisk[] Disks = new CureDisk[0];
        public string[] Locks = new string[0], Errors = new string[0];
        // the full picture (format 2): what can be removed, what fills C:, the biggest apps, what starts with Windows
        public long FillSafe = -1, FillReview = -1, FillCaution = -1, FillCritical = -1;
        public CureItem[] Items = new CureItem[0];
        public CurePlace[] Places = new CurePlace[0];
        public CureApp[] Apps = new CureApp[0];
        public string[] Startup = new string[0];
        public int Accounts = -1;
        public bool WeeklyClean;
        public string PlacesAt = "";
    }

    public static class CureProtocol
    {
        public const int MaxBody = 64 * 1024;          // a report is about 2-4 KB
        public const int MaxSkewSeconds = 300;
        public const int MinInterval = 5, MaxInterval = 24 * 60, DefaultInterval = 15;   // minutes
        public const string Version = "1.0";

        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = MaxBody, RecursionLimit = 8 }; }

        public static string Hex(byte[] b) { var sb = new StringBuilder(b.Length * 2); foreach (var x in b) sb.Append(x.ToString("x2")); return sb.ToString(); }

        public static string Sign(byte[] key, string time, string nonce, byte[] body)
        {
            string bodyHash; using (var sha = SHA256.Create()) bodyHash = Hex(sha.ComputeHash(body ?? new byte[0]));
            using (var h = new HMACSHA256(key)) return Hex(h.ComputeHash(Encoding.UTF8.GetBytes(time + "\n" + nonce + "\n" + bodyHash)));
        }

        // equal in constant time (a wrong signature takes as long as a nearly right one)
        public static bool Same(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        public static string Nonce() { var b = new byte[16]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return Hex(b); }
        public static byte[] NewKey() { var b = new byte[32]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        public static string UnixNow() { return ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString(CultureInfo.InvariantCulture); }

        // a report as the server keeps it: every text clipped and cleaned, every number sane
        public static CureReport Clean(CureReport r)
        {
            if (r == null) return null;
            Func<string, int, string> c = (s, n) =>
            {
                s = new string((s ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
                return s.Length > n ? s.Substring(0, n) : s;
            };
            Func<long, long> nn = v => v < -1 ? -1 : v;
            var o = new CureReport
            {
                Agent = c(r.Agent, 20), Elixar = c(r.Elixar, 20), Edition = c(r.Edition, 20), Computer = c(r.Computer, 63), Company = c(r.Company, 80), Ou = c(r.Ou, 200),
                Model = c(r.Model, 80), Windows = c(r.Windows, 80), Time = c(r.Time, 30), BootTime = c(r.BootTime, 30), MeasuredAt = c(r.MeasuredAt, 30), LastClean = c(r.LastClean, 30),
                DiskLevel = c(r.DiskLevel, 20), PolicyMode = c(r.PolicyMode, 20), PolicyFingerprint = c(r.PolicyFingerprint, 64),
                PendingReboot = r.PendingReboot, EventLog = r.EventLog,
                Total = nn(r.Total), Free = nn(r.Free), CleanSafe = nn(r.CleanSafe), CleanReview = nn(r.CleanReview), CleanCaution = nn(r.CleanCaution),
                LastCleanBytes = nn(r.LastCleanBytes), Saved = nn(r.Saved), SavedThisMonth = nn(r.SavedThisMonth), UndoBytes = nn(r.UndoBytes),
                AppUpdates = Math.Max(-1, Math.Min(r.AppUpdates, 10000)), StartupCount = Math.Max(-1, Math.Min(r.StartupCount, 10000)),
                Drives = (r.Drives ?? new CureDrive[0]).Where(d => d != null).Take(26).Select(d => new CureDrive { Letter = c(d.Letter, 3), Total = nn(d.Total), Free = nn(d.Free) }).ToArray(),
                Disks = (r.Disks ?? new CureDisk[0]).Where(d => d != null).Take(16).Select(d => new CureDisk { Name = c(d.Name, 80), Kind = c(d.Kind, 20), Level = c(d.Level, 20), Letters = c(d.Letters, 30),
                                                                                                           Wear = Math.Max(-1, Math.Min(d.Wear, 1000)), Temp = Math.Max(-1, Math.Min(d.Temp, 200)),
                                                                                                           Reasons = (d.Reasons ?? new string[0]).Take(8).Select(x => c(x, 200)).ToArray() }).ToArray(),
                Locks = (r.Locks ?? new string[0]).Take(10).Select(x => c(x, 20)).ToArray(),
                Errors = (r.Errors ?? new string[0]).Take(10).Select(x => c(x, 200)).ToArray(),
                FillSafe = nn(r.FillSafe), FillReview = nn(r.FillReview), FillCaution = nn(r.FillCaution), FillCritical = nn(r.FillCritical),
                Items = (r.Items ?? new CureItem[0]).Where(x => x != null).Take(80).Select(x => new CureItem { Name = c(x.Name, 80), Tier = Tier(c(x.Tier, 10)), Bytes = nn(x.Bytes), Auto = x.Auto }).ToArray(),
                Places = (r.Places ?? new CurePlace[0]).Where(x => x != null).Take(40).Select(x => new CurePlace { Name = c(x.Name, 80), Tier = Tier(c(x.Tier, 10)), Action = c(x.Action, 200), Bytes = nn(x.Bytes) }).ToArray(),
                Apps = (r.Apps ?? new CureApp[0]).Where(x => x != null).Take(30).Select(x => new CureApp { Name = c(x.Name, 80), Publisher = c(x.Publisher, 80), LastUsed = c(x.LastUsed, 30), Installed = c(x.Installed, 30), Bytes = nn(x.Bytes) }).ToArray(),
                Startup = (r.Startup ?? new string[0]).Take(60).Select(x => c(x, 80)).ToArray(),
                Accounts = Math.Max(-1, Math.Min(r.Accounts, 10000)), WeeklyClean = r.WeeklyClean, PlacesAt = c(r.PlacesAt, 30), Format = Math.Max(1, Math.Min(r.Format, 99))
            };
            if (o.Total >= 0 && o.Free > o.Total) o.Free = o.Total;
            return o;
        }

        static string Tier(string t) { return t == "safe" || t == "review" || t == "caution" || t == "critical" ? t : ""; }

        // a computer name Windows allows (letters, digits, - and _), so it can't smuggle anything into the console or files
        public static bool GoodName(string n) { return !string.IsNullOrEmpty(n) && n.Length <= 63 && n.All(ch => ch < 128 && (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')); }
    }
}
#endif
