// Sysprep readiness: before you capture an image of this PC (Sysprep /generalize), Elixar checks the things that make
// Sysprep stop, explains each one, and offers Microsoft's documented fix. Nothing is changed without asking, a restore
// point is made first, and Sysprep itself is only opened (you choose its options there).
//
// What stops Sysprep (from Microsoft's documentation and its error codes):
//   - a Store app installed for a user but not provisioned for all users        (0x80073cf2, setupact.log names the package)
//   - BitLocker on the Windows drive                                            (0x80310039)
//   - Windows upgraded in place from an earlier version                        (0x0f0036, "will not run on an upgraded OS")
//   - Sysprep's own state left from an earlier run, or run too many times      ("A fatal error occurred")
//   - a restart Windows is waiting for (updates)
// and things worth knowing: other accounts on the PC, domain membership, Store updates arriving mid-way, little free space.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class SysprepCheck
    {
        public string Id = "", Title = "", Detail = "", FixText = "";
        public int Level;                 // 0 ready, 1 good to know, 2 should fix, 3 stops Sysprep
        public bool FixRisky;             // ask twice / explain more
        public List<string> Items = new List<string>();   // e.g. the Store apps to remove
    }

    public class SysprepFacts
    {
        public bool Upgraded, PendingRestart, Domain, StoreUpdatesPaused, Admin = true;
        public int GeneralizationState = 7, CleanupState = -1;
        public string BitLocker = "none";   // none | FullyDecrypted | DecryptionInProgress | FullyEncrypted | EncryptionInProgress ...
        public int BitLockerPercent;
        public List<string[]> Unprovisioned = new List<string[]>();   // { name, full name }
        public List<string> OtherAccounts = new List<string>();
        public long Free = -1;
        public string LogText = "";          // setuperr.log + the end of setupact.log, from the last Sysprep run
        public DateTime LogTime = DateTime.MinValue;
        public bool AnswerExists; public int AnswerBad, AnswerWarn;   // the answer file in Sysprep's folder
        // golden image (GoldenImage.cs): only whether a secret is there, never the secret itself
        public bool AutoLogonSecret;
        public List<string> WifiKeys = new List<string>();        // Wi-Fi profiles with a saved password
        public List<string[]> Creds = new List<string[]>();       // Credential Manager { target, type } of this account
        public List<string> OldAnswers = new List<string>();      // answer files Windows Setup reads before Sysprep's
        public List<string> UsbAnswers = new List<string>();      // on USB drives
        public List<string> BrowserLogins = new List<string>();   // "Chrome (Tech): 12"
        public List<string> KeyFiles = new List<string>();        // SSH keys, cloud sign-ins
        public string ElixarMail;                                 // Elixar's own email sign-in
        public List<string> CloneApps = new List<string>();       // "Product: what to do"
        public bool EntraJoined, MdmEnrolled, AuditMode, CopyProfile, Win11, StartForNew, AppsForNew;
        public string Vm = "";
    }

    public static class Sysprep
    {
        public static string Exe { get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Sysprep"), "sysprep.exe"); } }
        public static string Panther { get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Sysprep"), "Panther"); } }
        static string SysDrive { get { return (Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\").TrimEnd('\\'); } }

        public static Func<SysprepFacts> FactsOverride;   // previews and the self-test

        // ---------- reading the PC (Windows only) ----------
        public const string PsScript =
            "$ErrorActionPreference = 'SilentlyContinue'; " +
            "$bl = Get-BitLockerVolume -MountPoint '__SYS__'; if ($bl) { 'BL|' + $bl.VolumeStatus + '|' + $bl.EncryptionPercentage } else { 'BL|none|0' }; " +
            "'DOM|' + (Get-CimInstance Win32_ComputerSystem).PartOfDomain; " +
            "$prov = @(Get-AppxProvisionedPackage -Online | ForEach-Object { $_.DisplayName }); " +
            "Get-AppxPackage -AllUsers | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' -and ($prov -notcontains $_.Name) } | " +
            "ForEach-Object { 'PKG|' + $_.Name + '|' + $_.PackageFullName }";

        public static SysprepFacts Read(Action<string> status)
        {
            if (FactsOverride != null) return FactsOverride();
            var f = new SysprepFacts();
            if (!Win.IsWindows) return f;
            f.Admin = Sys.IsAdmin();
            using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default))
            {
                try { using (var k = hk.OpenSubKey(@"SYSTEM\Setup")) f.Upgraded = k != null && k.GetValue("Upgrade") != null; } catch { }
                try
                {
                    using (var k = hk.OpenSubKey(@"SYSTEM\Setup\Status\SysprepStatus"))
                        if (k != null)
                        {
                            object g = k.GetValue("GeneralizationState"), c = k.GetValue("CleanupState");
                            if (g is int) f.GeneralizationState = (int)g;
                            if (c is int) f.CleanupState = (int)c;
                        }
                }
                catch { }
                try
                {
                    using (var a = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                    using (var b = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                        f.PendingRestart = a != null || b != null;
                }
                catch { }
                try { using (var k = hk.OpenSubKey(@"SOFTWARE\Policies\Microsoft\WindowsStore")) f.StoreUpdatesPaused = k != null && (k.GetValue("AutoDownload") as int? ?? 0) == 2; } catch { }
                try
                {
                    string me = null;
                    try { using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) me = id.User.Value; } catch { }
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                        if (k != null)
                            foreach (var sid in k.GetSubKeyNames().Where(x => x.StartsWith("S-1-5-21-") && x != me && !x.EndsWith(".bak")))
                                using (var p = k.OpenSubKey(sid))
                                {
                                    string path = p == null ? null : p.GetValue("ProfileImagePath") as string;
                                    if (!string.IsNullOrEmpty(path)) f.OtherAccounts.Add(Path.GetFileName(path.TrimEnd('\\')));
                                }
                }
                catch { }
            }
            try { f.Free = new DriveInfo(SysDrive).AvailableFreeSpace; } catch { }
            if (status != null) status("Checking BitLocker and the Store apps of every account...");
            try { ApplyPs(f, StoreData.RunPs(PsScript.Replace("__SYS__", SysDrive), 180000)); } catch { }
            try
            {
                if (File.Exists(Unattend.SysprepFile))
                {
                    f.AnswerExists = true;
                    var probs = Unattend.CheckXml(File.ReadAllText(Unattend.SysprepFile), Unattend.ThisArch());
                    f.AnswerBad = probs.Count(p => p.Level == 3); f.AnswerWarn = probs.Count(p => p.Level == 2);
                }
            }
            catch { f.AnswerExists = true; f.AnswerBad = 1; }
            if (status != null) status("Looking for saved passwords, keys and programs with their own ID...");
            try { GoldenImage.ReadInto(f); } catch { }
            try
            {
                string err = Path.Combine(Panther, "setuperr.log"), act = Path.Combine(Panther, "setupact.log");
                if (File.Exists(err) || File.Exists(act))
                {
                    f.LogTime = File.Exists(act) ? File.GetLastWriteTime(act) : File.GetLastWriteTime(err);
                    f.LogText = Tail(err, 200) + "\n" + Tail(act, 400);
                }
            }
            catch { }
            return f;
        }

        static string Tail(string file, int lines)
        {
            try
            {
                if (!File.Exists(file)) return "";
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - 256 * 1024);   // the end of the log is enough
                    fs.Seek(start, SeekOrigin.Begin);
                    using (var r = new StreamReader(fs)) { var all = r.ReadToEnd().Split('\n'); return string.Join("\n", all.Skip(Math.Max(0, all.Length - lines))); }
                }
            }
            catch { return ""; }
        }

        public static void ApplyPs(SysprepFacts f, string output)
        {
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var p = raw.Trim().Split('|');
                if (p[0] == "BL" && p.Length >= 3) { f.BitLocker = p[1]; int n; if (int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) f.BitLockerPercent = n; }
                else if (p[0] == "DOM" && p.Length >= 2) f.Domain = p[1].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
                else if (p[0] == "PKG" && p.Length >= 3 && p[2].Length > 0 && !f.Unprovisioned.Any(x => x[1] == p[2])) f.Unprovisioned.Add(new[] { p[1], p[2] });
            }
        }

        // ---------- what the last Sysprep run said ----------
        public class LogFinding { public string Kind = "", Package = "", Line = ""; }
        static readonly Regex PkgLine = new Regex(@"Package\s+(\S+)\s+was installed for a user, but not provisioned for all users", RegexOptions.IgnoreCase);

        public static List<LogFinding> ParseLog(string text)
        {
            var list = new List<LogFinding>();
            foreach (var line in (text ?? "").Split('\n'))
            {
                string l = line.Trim();
                if (l.Length == 0) continue;
                var m = PkgLine.Match(l);
                if (m.Success) { if (!list.Any(x => x.Package == m.Groups[1].Value)) list.Add(new LogFinding { Kind = "appx", Package = m.Groups[1].Value, Line = l }); continue; }
                if (l.IndexOf("0x80310039", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("BitLocker", StringComparison.OrdinalIgnoreCase) >= 0 && l.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                { if (!list.Any(x => x.Kind == "bitlocker")) list.Add(new LogFinding { Kind = "bitlocker", Line = l }); continue; }
                if (l.IndexOf("0x0f0036", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("upgraded OS", StringComparison.OrdinalIgnoreCase) >= 0)
                { if (!list.Any(x => x.Kind == "upgrade")) list.Add(new LogFinding { Kind = "upgrade", Line = l }); continue; }
                if (l.IndexOf("rearm", StringComparison.OrdinalIgnoreCase) >= 0 && l.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("0xc004d307", StringComparison.OrdinalIgnoreCase) >= 0)
                { if (!list.Any(x => x.Kind == "rearm")) list.Add(new LogFinding { Kind = "rearm", Line = l }); continue; }
            }
            return list;
        }

        // ---------- the checklist (pure: tested by the self-test) ----------
        public static List<SysprepCheck> Assess(SysprepFacts f)
        {
            var list = new List<SysprepCheck>();
            var log = ParseLog(f.LogText);
            bool bitlocker = f.BitLocker != "none" && f.BitLocker != "FullyDecrypted";
            list.Add(bitlocker
                ? new SysprepCheck { Id = "bitlocker", Level = 3, Title = "BitLocker is on for the Windows drive",
                                     Detail = f.BitLocker == "DecryptionInProgress" ? "Windows is turning BitLocker off (" + (100 - f.BitLockerPercent) + "% done). Wait until it has finished, then check again."
                                                                                     : "Sysprep stops while the Windows drive is encrypted. Turning BitLocker off decrypts the drive in the background (it can take an hour); the PC stays usable.",
                                     FixText = f.BitLocker == "DecryptionInProgress" ? "" : "Turn BitLocker off..." }
                : new SysprepCheck { Id = "bitlocker", Level = 0, Title = "BitLocker is off", Detail = "The Windows drive is not encrypted." });

            if (f.Unprovisioned.Count > 0)
            {
                var c = new SysprepCheck { Id = "appx", Level = 3, Title = f.Unprovisioned.Count == 1 ? "1 Store app is installed for one account only" : f.Unprovisioned.Count + " Store apps are installed for one account only",
                                           Detail = "Sysprep stops on Store apps that were installed for a user but not for every account. Removing them for all accounts fixes it; you can install them again after the image is made.",
                                           FixText = "Remove them for all accounts..." };
                c.Items.AddRange(f.Unprovisioned.Select(x => x[0]));
                list.Add(c);
            }
            else list.Add(new SysprepCheck { Id = "appx", Level = 0, Title = "Store apps are ready", Detail = "Every Store app is set up for all accounts." });

            if (f.Upgraded)
                list.Add(new SysprepCheck { Id = "upgrade", Level = 3, Title = "Windows was upgraded in place", FixRisky = true,
                    Detail = "Sysprep refuses Windows that was upgraded from an earlier version. Microsoft's supported way is a clean install; a known workaround tells Windows it is a clean install (it changes 3 registry values).",
                    FixText = "Apply the workaround..." });

            if (f.GeneralizationState != 7 || (f.CleanupState != -1 && f.CleanupState != 2) || log.Any(x => x.Kind == "rearm"))
                list.Add(new SysprepCheck { Id = "state", Level = 3, Title = "Sysprep's state is left from an earlier run",
                    Detail = log.Any(x => x.Kind == "rearm") ? "The last run stopped because Sysprep was run too many times on this Windows. Resetting its state (and skipping the activation rearm) lets it run again."
                                                             : "Sysprep remembers an unfinished earlier run and stops with \"A fatal error occurred\". Resetting its state lets it run again.",
                    FixText = "Reset Sysprep's state..." });

            if (f.AnswerExists)
                list.Add(f.AnswerBad > 0
                    ? new SysprepCheck { Id = "answer", Level = 3, Title = "The answer file has problems", Detail = "Windows Setup would stop on the answer file in Sysprep's folder (" + f.AnswerBad + " problem(s)). Fix it, or delete it.", FixText = "Edit the answer file..." }
                    : new SysprepCheck { Id = "answer", Level = f.AnswerWarn > 0 ? 1 : 0, Title = "Answer file ready", Detail = f.AnswerWarn > 0 ? "Sysprep's folder has an answer file. " + f.AnswerWarn + " thing(s) worth a look." : "Sysprep's folder has an answer file, and it checks out.", FixText = "Edit the answer file..." });
            else
                list.Add(new SysprepCheck { Id = "answer", Level = 1, Title = "No answer file", Detail = "Optional: an answer file sets up each new PC by itself (name, language, time zone, setup screens, an account).", FixText = "Create an answer file..." });

            if (f.PendingRestart)
                list.Add(new SysprepCheck { Id = "restart", Level = 2, Title = "Windows is waiting for a restart", Detail = "Updates are half-installed. Restart the PC first, then check again." });

            if (!f.StoreUpdatesPaused)
                list.Add(new SysprepCheck { Id = "storeupdates", Level = 1, Title = "Store apps can update by themselves",
                    Detail = "An app the Store updates while you prepare the PC can stop Sysprep again. Pausing Store updates prevents that; turn them back on after the image is made.",
                    FixText = "Pause Store updates" });
            else
                list.Add(new SysprepCheck { Id = "storeupdates", Level = 0, Title = "Store updates are paused", Detail = "Turn them back on after the image is made.", FixText = "Turn Store updates back on" });

            if (f.OtherAccounts.Count > 0)
                list.Add(new SysprepCheck { Id = "accounts", Level = f.CopyProfile ? 2 : 1, Title = f.OtherAccounts.Count == 1 ? "1 other account on this PC" : f.OtherAccounts.Count + " other accounts on this PC",
                    Detail = f.CopyProfile ? "Your answer file uses CopyProfile: keep only the account you customized, so that one is copied to new accounts. Each account's files and Store apps also go into the image. Remove the others in Settings > Accounts > Other users."
                                           : "Each account's files and Store apps go into the image. Remove the accounts you don't need in Settings > Accounts > Other users.",
                    FixText = "Open Accounts settings", Items = f.OtherAccounts.ToList() });

            if (f.Domain)
                list.Add(new SysprepCheck { Id = "domain", Level = 1, Title = "This PC is in a domain", Detail = "Sysprep takes the PC out of the domain. Join it again after setup (or let your deployment do it)." });

            if (f.Free >= 0 && f.Free < (10L << 30))
                list.Add(new SysprepCheck { Id = "space", Level = 2, Title = "Little free space on the Windows drive (" + Fs.Fmt(f.Free) + ")",
                    Detail = "Clean before you capture: a smaller image is quicker to copy, and Sysprep needs room to work.", FixText = "Open the Cleaner" });

            foreach (var x in log.Where(x => x.Kind == "appx" && !f.Unprovisioned.Any(u => x.Package.StartsWith(u[0], StringComparison.OrdinalIgnoreCase))))
                list.Add(new SysprepCheck { Id = "logappx", Level = 2, Title = "The last Sysprep run stopped on " + x.Package,
                    Detail = "That app is no longer installed for a single account. Run Sysprep again; if it stops again, check here once more." });

            if (f.LogTime > DateTime.MinValue && log.Count == 0 && f.LogText.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                list.Add(new SysprepCheck { Id = "log", Level = 1, Title = "The last Sysprep run (" + f.LogTime.ToString("d MMM HH:mm") + ") wrote errors",
                    Detail = "None of them is a known blocker. Open the log to read them.", FixText = "Open the log" });

            GoldenImage.AddChecks(f, list);
            return list.OrderByDescending(c => c.Level).ToList();
        }

        public static string Summary(List<SysprepCheck> checks)
        {
            int block = checks.Count(c => c.Level == 3), fix = checks.Count(c => c.Level == 2);
            if (block == 0 && fix == 0) return "Ready for Sysprep. Open Sysprep and choose OOBE, Generalize and Shutdown.";
            return (block > 0 ? block + (block == 1 ? " thing stops" : " things stop") + " Sysprep" : "") + (block > 0 && fix > 0 ? ", " : "") +
                   (fix > 0 ? fix + (fix == 1 ? " thing to fix" : " things to fix") : "") + " first.";
        }

        // ---------- the fixes (each asked for and logged by the window) ----------
        public static string RemoveApps(IEnumerable<string> fullNames)
        {
            var errs = new List<string>();
            foreach (var n in fullNames)
            {
                if (!Regex.IsMatch(n ?? "", @"^[A-Za-z0-9.\-_~]+$")) { errs.Add(n + ": not a package name"); continue; }
                string r = StoreData.RunPs("$ErrorActionPreference = 'Stop'; try { Remove-AppxPackage -Package " + Sys.PsQuote(n) + " -AllUsers; 'OK' } catch { 'ERROR: ' + $_.Exception.Message }", 5 * 60 * 1000).Trim();
                if (!r.EndsWith("OK")) errs.Add(n + ": " + r);
            }
            return errs.Count == 0 ? null : string.Join("\n", errs.Take(5));
        }

        public static string BitLockerOff()
        {
            string r = StoreData.RunPs("$ErrorActionPreference = 'Stop'; try { Disable-BitLocker -MountPoint " + Sys.PsQuote(SysDrive) + " | Out-Null; 'OK' } catch { 'ERROR: ' + $_.Exception.Message }", 120000).Trim();
            return r.EndsWith("OK") ? null : r;
        }

        static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default); }

        // Microsoft's documented workaround for an upgraded Windows
        public static string ClearUpgrade()
        {
            try
            {
                using (var hk = Hklm())
                {
                    using (var k = hk.OpenSubKey(@"SYSTEM\Setup", true)) if (k != null) k.DeleteValue("Upgrade", false);
                    ResetStateIn(hk);
                }
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        static void ResetStateIn(RegistryKey hk)
        {
            using (var k = hk.CreateSubKey(@"SYSTEM\Setup\Status\SysprepStatus"))
            {
                k.SetValue("GeneralizationState", 7, RegistryValueKind.DWord);
                if (k.GetValue("CleanupState") != null) k.SetValue("CleanupState", 2, RegistryValueKind.DWord);
            }
        }

        // after an unfinished run or too many runs: Sysprep's state, the activation rearm, and the old logs (kept, renamed)
        public static string ResetState(bool skipRearm)
        {
            try
            {
                using (var hk = Hklm())
                {
                    ResetStateIn(hk);
                    if (skipRearm) using (var k = hk.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform")) k.SetValue("SkipRearm", 1, RegistryValueKind.DWord);
                }
                if (Directory.Exists(Panther) && (new DirectoryInfo(Panther).Attributes & FileAttributes.ReparsePoint) == 0)
                    Directory.Move(Panther, Panther + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        public static string StoreUpdates(bool pause)
        {
            try
            {
                using (var hk = Hklm())
                using (var k = hk.CreateSubKey(@"SOFTWARE\Policies\Microsoft\WindowsStore"))
                {
                    if (pause) k.SetValue("AutoDownload", 2, RegistryValueKind.DWord); else k.DeleteValue("AutoDownload", false);
                }
                return null;
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
