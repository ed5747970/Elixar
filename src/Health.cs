// PC health: one score from what Windows already records - Security, Stability, Speed, Space, Drives and Safety net
// (and the battery on a laptop). Checking only reads: one PowerShell script that uses Get- commands and reads the
// Windows Update history, plus a few registry values and Windows' own counters. Nothing is changed and nothing is sent.
// Every fix is a button the person clicks; most open Elixar's own tools or Windows' own settings page.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public enum HLevel { Ok, Info, Warn, Bad }
    public enum HAct { None, RepairWindows, UpdateApps, Restart, Drivers, Startup, Memory, PowerBalanced, RestorePoint, SystemProtection, Backup, BitLocker, Defender, Firewall, Uac, DiskHealth, CheckFix, Growing }

    public class HFind
    {
        public string Area = "", Key = "", Title = "", Detail = "", Tag = "", Button = "";
        public HLevel Level; public int Penalty; public HAct Act; public bool CanIgnore, Ignored;
        public List<AppUpdate> Updates = new List<AppUpdate>();
    }

    public class HWu { public DateTime When; public int Result; public string Code = "", Title = ""; }
    public class HCrash { public DateTime When; public string Kind = "", Name = "", Module = "", Code = ""; }

    // everything that was read, before it is judged (the self-test fills this by hand)
    public class HealthFacts
    {
        // security
        public bool DefKnown, DefOn, DefRealTime, DefService; public int DefSigAge = -1;
        public List<string[]> Antivirus = new List<string[]>();      // name, productState
        public List<string[]> FirewallApps = new List<string[]>();   // name, productState
        public Dictionary<string, bool> Firewall = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public List<HWu> Updates = new List<HWu>(); public bool UpdatesKnown;
        public List<AppUpdate> AppUpdates; public bool AppsManaged, RestartPending;
        public bool? SecureBoot, Uac; public string Tpm;   // null: unknown, "": none, else the version
        public int BitLocker = -1;                          // -1 unknown, 0 off, 1 on
        // stability (last 30 days)
        public List<HCrash> Crashes = new List<HCrash>(); public bool CrashesKnown;
        // speed
        public StartupImpactData Startup; public long RamTotal; public int RamLoad = -1;
        public List<Tuple<string, long>> TopMemory = new List<Tuple<string, long>>();
        public string PowerScheme = ""; public bool OnBattery;
        // space and drives
        public long Free = -1, Total; public int FastFolders, UnusualFolders; public HealthLevel Disk = HealthLevel.Unknown;
        // safety net
        public bool RestoreKnown; public DateTime? LastRestore; public bool? FileHistory, OneDriveBackup;
        // battery
        public bool HasBattery; public List<int> Chassis = new List<int>(); public long DesignMwh, FullMwh; public int Cycles = -1;
    }

    public class HealthReport
    {
        public static readonly string[] Areas = { "Security", "Stability", "Speed", "Space", "Drives", "Safety net" };
        static readonly int[] Weights = { 25, 20, 15, 15, 15, 10 };
        public List<HFind> Items = new List<HFind>();
        public Dictionary<string, int> Scores = new Dictionary<string, int>();   // -1: not known on this PC
        public int Total = -1; public DateTime At; public bool Laptop; public int BatteryPct = -1; public HealthFacts Facts;

        public string Word { get { return Total < 0 ? "Unknown" : Total >= 85 ? "Good" : Total >= 65 ? "Fair" : "Poor"; } }
        public int ToFix { get { return Items.Count(i => !i.Ignored && (i.Level == HLevel.Warn || i.Level == HLevel.Bad)); } }

        public void Score()
        {
            double sum = 0, w = 0;
            for (int i = 0; i < Areas.Length; i++)
            {
                string a = Areas[i];
                int s;
                if (!Scores.TryGetValue(a, out s) || s < 0) { Scores[a] = -1; continue; }
                s = Math.Max(0, Math.Min(100, 100 - Items.Where(x => x.Area == a && !x.Ignored).Sum(x => x.Penalty)));
                Scores[a] = s; sum += s * Weights[i]; w += Weights[i];
            }
            Total = w == 0 ? -1 : (int)Math.Round(sum / w);
        }
    }

    public static class Health
    {
        public static Func<string> ScriptOverride;          // self-test: what the PowerShell script printed
        public static Func<HealthFacts> FactsOverride;      // self-test and previews: everything read
        public static Func<string, bool> SetPowerOverride;  // self-test: the scheme a power-plan change would set

        public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e", PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

        static string H(string en, string he) { return Lang.Hebrew ? he : en; }

        // ---------------- reading (read only) ----------------
        // Only Get- commands and the Windows Update history (a read-only search object). Values that could hold a "|" or a
        // line break are cleaned, so one value can never be read as another.
        public static readonly string Script =
            "$ErrorActionPreference='SilentlyContinue'\n" +
            "function C($s){ (''+$s) -replace '[|\\r\\n]',' ' }\n" +
            "$m=Get-CimInstance -Namespace root/Microsoft/Windows/Defender -ClassName MSFT_MpComputerStatus\n" +
            "if($m){ 'DEF|'+[int][bool]$m.AntivirusEnabled+'|'+[int][bool]$m.RealTimeProtectionEnabled+'|'+$m.AntivirusSignatureAge+'|'+[int][bool]$m.AMServiceEnabled }\n" +
            "foreach($a in (Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct)){ 'AV|'+(C $a.displayName)+'|'+$a.productState }\n" +
            "foreach($a in (Get-CimInstance -Namespace root/SecurityCenter2 -ClassName FirewallProduct)){ 'FWP|'+(C $a.displayName)+'|'+$a.productState }\n" +
            "foreach($p in (Get-NetFirewallProfile)){ 'FW|'+$p.Name+'|'+[int]((''+$p.Enabled) -eq 'True') }\n" +
            "$t=Get-CimInstance -Namespace root/cimv2/Security/MicrosoftTpm -ClassName Win32_Tpm\n" +
            "if($t){ 'TPM|'+[int][bool]$t.IsEnabled_InitialValue+'|'+(C $t.SpecVersion) } else { 'TPM|0|' }\n" +
            "$b=Get-CimInstance -Namespace root/cimv2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter \"DriveLetter='C:'\"\n" +
            "if($b){ 'BL|'+$b.ProtectionStatus }\n" +
            "$u=(New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher(); $n=$u.GetTotalHistoryCount(); 'WUN|'+$n\n" +
            "if($n -gt 0){ foreach($h in $u.QueryHistory(0,[Math]::Min($n,60))){ if($h.Operation -eq 1){ 'WU|'+$h.Date.ToString('s')+'|'+$h.ResultCode+'|'+('0x{0:X8}' -f $h.HResult)+'|'+(C $h.Title) } } }\n" +
            "$d=(Get-Date).AddDays(-30); 'EV|ok'\n" +
            "foreach($e in (Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-WER-SystemErrorReporting'; Id=1001; StartTime=$d} -MaxEvents 50)){ 'BSOD|'+$e.TimeCreated.ToString('s')+'|'+(C $e.Properties[0].Value) }\n" +
            "foreach($e in (Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Power'; Id=41; StartTime=$d} -MaxEvents 50)){ 'KP|'+$e.TimeCreated.ToString('s')+'|'+(C $e.Properties[0].Value) }\n" +
            "foreach($e in (Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Error'; Id=1000; StartTime=$d} -MaxEvents 300)){ 'CRASH|'+$e.TimeCreated.ToString('s')+'|'+(C $e.Properties[0].Value)+'|'+(C $e.Properties[3].Value) }\n" +
            "foreach($e in (Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='Application Hang'; Id=1002; StartTime=$d} -MaxEvents 300)){ 'HANG|'+$e.TimeCreated.ToString('s')+'|'+(C $e.Properties[0].Value) }\n" +
            "'RPK|ok'; foreach($r in (Get-CimInstance -Namespace root/default -ClassName SystemRestore)){ 'RP|'+(C $r.CreationTime) }\n" +
            "'CH|'+((Get-CimInstance Win32_SystemEnclosure).ChassisTypes -join ',')\n" +
            "$s=@(Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData); $f=@(Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity); $c=@(Get-CimInstance -Namespace root/wmi -ClassName BatteryCycleCount)\n" +
            "if($s.Count -gt 0){ 'BAT|'+(($s|Measure-Object DesignedCapacity -Sum).Sum)+'|'+(($f|Measure-Object FullChargedCapacity -Sum).Sum)+'|'+(($c|Measure-Object CycleCount -Maximum).Maximum) }\n";

        public static void Parse(string raw, HealthFacts f)
        {
            foreach (var line in (raw ?? "").Split('\n'))
            {
                var p = line.Trim().Split('|');
                if (p.Length < 2) continue;
                DateTime when; int n; long l;
                switch (p[0])
                {
                    case "DEF":
                        if (p.Length >= 5) { f.DefKnown = true; f.DefOn = p[1] == "1"; f.DefRealTime = p[2] == "1"; f.DefSigAge = int.TryParse(p[3], out n) ? n : -1; f.DefService = p[4] == "1"; }
                        break;
                    case "AV": if (p.Length >= 3) f.Antivirus.Add(new[] { p[1].Trim(), p[2].Trim() }); break;
                    case "FWP": if (p.Length >= 3) f.FirewallApps.Add(new[] { p[1].Trim(), p[2].Trim() }); break;
                    case "FW": if (p.Length >= 3 && p[1].Trim().Length > 0) f.Firewall[p[1].Trim()] = p[2] == "1"; break;
                    case "TPM": f.Tpm = p[1] == "1" ? (p.Length >= 3 ? p[2].Split(',')[0].Trim() : "?") : ""; break;
                    case "BL": if (int.TryParse(p[1], out n)) f.BitLocker = n == 1 ? 1 : n == 0 ? 0 : -1; break;
                    case "WUN": f.UpdatesKnown = int.TryParse(p[1], out n); break;
                    case "WU":
                        if (p.Length >= 5 && Date(p[1], out when) && int.TryParse(p[2], out n)) f.Updates.Add(new HWu { When = when, Result = n, Code = p[3].Trim(), Title = p[4].Trim() });
                        break;
                    case "EV": f.CrashesKnown = true; break;
                    case "BSOD": if (Date(p[1], out when)) f.Crashes.Add(new HCrash { When = when, Kind = "bluescreen", Code = p.Length >= 3 ? StopCode(p[2]) : "" }); break;
                    case "KP": if (Date(p[1], out when)) f.Crashes.Add(new HCrash { When = when, Kind = "shutdown", Code = p.Length >= 3 ? p[2].Trim() : "" }); break;
                    case "CRASH": if (p.Length >= 3 && Date(p[1], out when)) f.Crashes.Add(new HCrash { When = when, Kind = "crash", Name = p[2].Trim(), Module = p.Length >= 4 ? p[3].Trim() : "" }); break;
                    case "HANG": if (p.Length >= 3 && Date(p[1], out when)) f.Crashes.Add(new HCrash { When = when, Kind = "hang", Name = p[2].Trim() }); break;
                    case "RPK": f.RestoreKnown = true; break;
                    case "RP":
                        var m = Regex.Match(p[1], @"^(\d{14})");
                        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out when) && (!f.LastRestore.HasValue || when > f.LastRestore.Value)) f.LastRestore = when;
                        break;
                    case "CH": foreach (var c in p[1].Split(',')) if (int.TryParse(c.Trim(), out n)) f.Chassis.Add(n); break;
                    case "BAT":
                        if (p.Length >= 4) { if (long.TryParse(p[1], out l)) f.DesignMwh = l; if (long.TryParse(p[2], out l)) f.FullMwh = l; if (int.TryParse(p[3], out n)) f.Cycles = n; }
                        break;
                }
            }
        }

        static bool Date(string s, out DateTime d) { return DateTime.TryParse((s ?? "").Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out d); }

        // "0x0000009f (0x..., ...)" -> "0x9F"
        static string StopCode(string s)
        {
            var m = Regex.Match(s ?? "", @"0x([0-9a-fA-F]{1,8})");
            if (!m.Success) return "";
            uint v; return uint.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? "0x" + v.ToString("X") : "";
        }

        static readonly Dictionary<string, string[]> StopWords = new Dictionary<string, string[]>
        {
            { "0xD1", new[] { "a driver used memory it shouldn't (DRIVER_IRQL_NOT_LESS_OR_EQUAL)", "דרייבר ניגש לזיכרון שאסור לו (DRIVER_IRQL_NOT_LESS_OR_EQUAL)" } },
            { "0xA", new[] { "a driver used memory it shouldn't (IRQL_NOT_LESS_OR_EQUAL)", "דרייבר ניגש לזיכרון שאסור לו (IRQL_NOT_LESS_OR_EQUAL)" } },
            { "0x9F", new[] { "a driver didn't handle sleep or power (DRIVER_POWER_STATE_FAILURE)", "דרייבר לא טיפל בשינה או בצריכת חשמל (DRIVER_POWER_STATE_FAILURE)" } },
            { "0x116", new[] { "the display driver stopped responding (VIDEO_TDR_FAILURE)", "דרייבר התצוגה הפסיק להגיב (VIDEO_TDR_FAILURE)" } },
            { "0x117", new[] { "the display driver stopped responding (VIDEO_TDR_TIMEOUT)", "דרייבר התצוגה הפסיק להגיב (VIDEO_TDR_TIMEOUT)" } },
            { "0x50", new[] { "memory or a driver failed (PAGE_FAULT_IN_NONPAGED_AREA)", "זיכרון או דרייבר נכשלו (PAGE_FAULT_IN_NONPAGED_AREA)" } },
            { "0x1A", new[] { "a memory problem (MEMORY_MANAGEMENT)", "בעיה בזיכרון (MEMORY_MANAGEMENT)" } },
            { "0x124", new[] { "a hardware error (WHEA_UNCORRECTABLE_ERROR) - often heat or overclocking", "שגיאת חומרה (WHEA_UNCORRECTABLE_ERROR) - לרוב חום או אוברקלוק" } },
            { "0x7E", new[] { "a driver failed (SYSTEM_THREAD_EXCEPTION_NOT_HANDLED)", "דרייבר נכשל (SYSTEM_THREAD_EXCEPTION_NOT_HANDLED)" } },
            { "0x3B", new[] { "a driver failed (SYSTEM_SERVICE_EXCEPTION)", "דרייבר נכשל (SYSTEM_SERVICE_EXCEPTION)" } },
            { "0x133", new[] { "a driver took too long (DPC_WATCHDOG_VIOLATION)", "דרייבר לקח יותר מדי זמן (DPC_WATCHDOG_VIOLATION)" } },
            { "0xEF", new[] { "a key Windows process stopped (CRITICAL_PROCESS_DIED)", "תהליך חיוני של Windows נעצר (CRITICAL_PROCESS_DIED)" } },
            { "0x7A", new[] { "the disk couldn't be read (KERNEL_DATA_INPAGE_ERROR)", "לא ניתן היה לקרוא מהדיסק (KERNEL_DATA_INPAGE_ERROR)" } },
        };
        public static string StopText(string code)
        {
            string[] w;
            return StopWords.TryGetValue(code ?? "", out w) ? H(w[0], w[1]) : H("stop code " + code, "קוד עצירה " + code);
        }

        // ---------------- the laptop question ----------------
        // A laptop has a battery AND says it is portable. A desktop with a UPS on USB also reports a battery, so the case
        // decides; only when the PC doesn't say what its case is does the battery decide alone.
        static readonly int[] Portable = { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };   // portable, laptop, notebook, hand held, docking, sub notebook, expansion, peripheral, tablet, convertible, detachable
        static readonly int[] Desktop = { 3, 4, 5, 6, 7, 13, 15, 16, 17, 23, 24, 35, 36 }; // desktop, low profile, pizza box, mini tower, tower, all in one, space-saving, lunch box, main server, rack, sealed, mini PC, stick
        public static bool IsLaptop(HealthFacts f)
        {
            if (!f.HasBattery) return false;
            if (f.Chassis.Any(c => Portable.Contains(c))) return true;
            if (f.Chassis.Any(c => Desktop.Contains(c))) return false;
            return true;   // unknown, "other" or "not known": trust the battery
        }

        // ---------------- judging ----------------
        // Microsoft's own antivirus, by its exact names ("Bitdefender" also contains "defender")
        static bool IsMsDefender(string name) { string n = (name ?? "").Trim(); return n.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase) || n.Equals("Microsoft Defender", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Microsoft Defender ", StringComparison.OrdinalIgnoreCase); }
        static bool AvOn(string state) { int s; if (!int.TryParse(state, out s)) return false; int k = (s >> 8) & 0xFF; return k == 0x10 || k == 0x11; }
        static bool AvCurrent(string state) { int s; return int.TryParse(state, out s) && (s & 0xFF) == 0; }
        static int Days(DateTime now, DateTime then) { return Math.Max(0, (int)(now.Date - then.Date).TotalDays); }
        static string Plural(int n, string one, string many) { return n + " " + (n == 1 ? one : many); }

        // what you chose to ignore: kept in Elixar's data folder, which only administrators can change, so a program running
        // as you can't hide a finding. Only findings that are your decision can be ignored at all (see CanIgnore).
        public static string IgnoreFileOverride;   // self-test
        static string IgnoreFile { get { return IgnoreFileOverride ?? Paths.FileIn("health-ignore.txt"); } }
        public static HashSet<string> IgnoredKeys()
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(IgnoreFile)) foreach (var k in File.ReadAllLines(IgnoreFile)) if (Regex.IsMatch(k.Trim(), @"^[a-z0-9:._ -]{1,80}$", RegexOptions.IgnoreCase)) h.Add(k.Trim()); } catch { }
            return h;
        }
        public static void Ignore(string key, bool on)
        {
            var h = IgnoredKeys(); if (on) h.Add(key); else h.Remove(key);
            try { File.WriteAllLines(IgnoreFile, h.OrderBy(x => x).ToArray()); } catch { }
        }

        public static HealthReport Evaluate(HealthFacts f, DateTime now, ICollection<string> ignored)
        {
            var r = new HealthReport { At = now, Facts = f };
            Action<HFind> add = x => { if (ignored != null && x.CanIgnore && ignored.Contains(x.Key)) x.Ignored = true; r.Items.Add(x); };
            Func<string, bool> known = a => { r.Scores[a] = 100; return true; };

            // ---- Security ----
            bool sec = f.DefKnown || f.Antivirus.Count > 0 || f.Firewall.Count > 0 || f.UpdatesKnown || f.Uac.HasValue;
            if (sec)
            {
                known("Security");
                var thirdOn = f.Antivirus.Where(a => !IsMsDefender(a[0]) && AvOn(a[1])).ToList();
                bool defOn = f.DefKnown ? f.DefOn && f.DefRealTime : f.Antivirus.Any(a => IsMsDefender(a[0]) && AvOn(a[1]));
                if (thirdOn.Count > 0)
                {
                    var a = thirdOn[0]; bool cur = AvCurrent(a[1]);
                    add(new HFind { Area = "Security", Key = "av", Level = cur ? HLevel.Ok : HLevel.Warn, Penalty = cur ? 0 : 15,
                                    Title = cur ? H(a[0] + " protects this PC", a[0] + " מגן על המחשב") : H(a[0] + " is on, but its virus definitions are out of date", a[0] + " פועל, אבל הגדרות הווירוסים שלו לא מעודכנות"),
                                    Detail = H("Your antivirus program, as Windows Security reports it.", "תוכנת האנטי-וירוס שלך, כפי ש-Windows Security מדווח."), Act = HAct.Defender, Button = cur ? "" : "Open Windows Security" });
                }
                else if (defOn)
                {
                    bool old = f.DefSigAge > 7;
                    add(new HFind { Area = "Security", Key = "av", Level = old ? HLevel.Warn : HLevel.Ok, Penalty = old ? 15 : 0,
                                    Title = old ? H("Microsoft Defender's virus definitions are " + f.DefSigAge + " days old", "הגדרות הווירוסים של Microsoft Defender בנות " + f.DefSigAge + " ימים")
                                                : H("Microsoft Defender is on and up to date", "Microsoft Defender פועל ומעודכן"),
                                    Detail = old ? H("New threats aren't recognised. Windows Update normally refreshes them every day.", "איומים חדשים לא מזוהים. Windows Update מרענן אותן בדרך כלל כל יום.")
                                                 : H("Real-time protection is on" + (f.DefSigAge >= 0 ? " · virus definitions from " + (f.DefSigAge == 0 ? "today" : Plural(f.DefSigAge, "day", "days") + " ago") : ""),
                                                     "ההגנה בזמן אמת פועלת" + (f.DefSigAge >= 0 ? " · הגדרות וירוסים מ" + (f.DefSigAge == 0 ? "היום" : "לפני " + f.DefSigAge + " ימים") : "")),
                                    Act = HAct.Defender, Button = old ? "Open Windows Security" : "" });
                }
                else if (f.DefKnown || f.Antivirus.Count > 0)
                    add(new HFind { Area = "Security", Key = "av", Level = HLevel.Bad, Penalty = 45, Act = HAct.Defender, Button = "Open Windows Security",
                                    Title = H("No antivirus is protecting this PC", "שום אנטי-וירוס לא מגן על המחשב"),
                                    Detail = H("Microsoft Defender's real-time protection is off and no other antivirus is on. Turn it on in Windows Security.", "ההגנה בזמן אמת של Microsoft Defender כבויה ואין אנטי-וירוס אחר פועל. הפעל אותה ב-Windows Security.") });

                if (f.Firewall.Count > 0)
                {
                    bool otherFw = f.FirewallApps.Any(a => AvOn(a[1]) && a[0].IndexOf("Windows", StringComparison.OrdinalIgnoreCase) < 0);
                    var off = f.Firewall.Where(kv => !kv.Value && !kv.Key.Equals("Domain", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
                    if (off.Count == 0 || otherFw)
                        add(new HFind { Area = "Security", Key = "fw", Level = HLevel.Ok, Title = H("Firewall is on", "חומת האש פועלת"),
                                        Detail = otherFw && off.Count > 0 ? H("Another firewall program protects this PC", "תוכנת חומת אש אחרת מגינה על המחשב") : H("For private and public networks", "לרשתות פרטיות וציבוריות") });
                    else
                    {
                        bool pub = off.Any(x => x.Equals("Public", StringComparison.OrdinalIgnoreCase));
                        add(new HFind { Area = "Security", Key = "fw", Level = pub ? HLevel.Bad : HLevel.Warn, Penalty = pub ? 25 : 15, Act = HAct.Firewall, Button = "Open firewall settings",
                                        Title = H("The firewall is off for " + string.Join(" and ", off.Select(x => x.ToLowerInvariant())) + " networks", "חומת האש כבויה לרשתות " + string.Join(" ו", off.Select(x => x.Equals("Public", StringComparison.OrdinalIgnoreCase) ? "ציבוריות" : "פרטיות"))),
                                        Detail = H("On public Wi-Fi (cafés, airports) anyone nearby can reach this PC. Windows Security turns it back on.", "ברשת Wi-Fi ציבורית (בתי קפה, שדות תעופה) כל מי שבסביבה יכול להגיע למחשב. Windows Security מפעיל אותה שוב.") });
                    }
                }

                if (f.UpdatesKnown && f.Updates.Count > 0)
                {
                    var ok = f.Updates.Where(u => u.Result == 2 || u.Result == 3).OrderByDescending(u => u.When).FirstOrDefault();
                    var recent = f.Updates.OrderByDescending(u => u.When).ToList();
                    int fails = 0; string code = "";
                    foreach (var u in recent) { if (u.Result == 4 || u.Result == 5) { fails++; if (code == "") code = u.Code; } else if (u.Result == 2 || u.Result == 3) break; }
                    int days = ok == null ? 999 : Days(now, ok.When);
                    if (days > 35 || fails >= 3)
                    {
                        bool bad = days > 60 || fails >= 3;
                        string t = ok == null ? H("Windows Update hasn't installed anything", "Windows Update לא התקין שום דבר")
                                              : H("Windows Update hasn't installed anything for " + days + " days", "Windows Update לא התקין שום דבר כבר " + days + " ימים");
                        string d = fails >= 2 ? H("The last " + fails + " tries failed (error " + code + "). ", "ה-" + fails + " ניסיונות האחרונים נכשלו (שגיאה " + code + "). ") : "";
                        d += H("You're missing security fixes. Repair Windows fixes the store updates come from; then run Windows Update again.", "חסרים לך תיקוני אבטחה. תיקון Windows מתקן את המאגר שממנו מגיעים העדכונים; אחר כך הרץ שוב את Windows Update.");
                        add(new HFind { Area = "Security", Key = "wu", Level = bad ? HLevel.Bad : HLevel.Warn, Penalty = bad ? 30 : 15, Title = t, Detail = d, Act = HAct.RepairWindows, Button = "Repair Windows Update..." });
                    }
                    else add(new HFind { Area = "Security", Key = "wu", Level = HLevel.Ok, Title = H("Windows Update is working", "Windows Update עובד"),
                                         Detail = H("Last update installed " + (days == 0 ? "today" : Plural(days, "day", "days") + " ago"), "העדכון האחרון הותקן " + (days == 0 ? "היום" : "לפני " + days + " ימים")) });
                }
                if (f.RestartPending)
                    add(new HFind { Area = "Security", Key = "restart", Level = HLevel.Info, Penalty = 5, Act = HAct.Restart, Title = H("Windows is waiting for a restart", "Windows ממתין להפעלה מחדש"),
                                    Detail = H("Updates are installed only after you restart.", "העדכונים מותקנים רק אחרי הפעלה מחדש.") });

                if (f.AppUpdates != null && f.AppUpdates.Count > 0)
                {
                    int n = f.AppUpdates.Count;
                    add(new HFind { Area = "Security", Key = "apps", Level = HLevel.Warn, Penalty = Math.Min(15, 3 * n), Tag = f.AppsManaged ? "" : "Safe", Act = f.AppsManaged ? HAct.None : HAct.UpdateApps, Button = f.AppsManaged ? "" : "Update", Updates = f.AppsManaged ? new List<AppUpdate>() : f.AppUpdates,
                                    Title = H(Plural(n, "app has an update", "apps have updates") + " waiting", n == 1 ? "לאפליקציה אחת יש עדכון שממתין" : "ל-" + n + " אפליקציות יש עדכונים שממתינים"),
                                    Detail = string.Join(", ", f.AppUpdates.Take(5).Select(u => u.Name)) + (n > 5 ? H(" and " + (n - 5) + " more", " ועוד " + (n - 5)) : "") +
                                             (f.AppsManaged ? H(". Your company's policy manages app updates on this PC.", ". מדיניות החברה מנהלת את עדכוני האפליקציות במחשב הזה.")
                                                            : H(" (from winget / Microsoft Store). Updates often close security holes.", " (מ-winget / Microsoft Store). עדכונים סוגרים לרוב פרצות אבטחה.")) });
                }

                var sbParts = new List<string>(); var sbHe = new List<string>(); int sbPen = 0; bool sbWarn = false;
                if (f.SecureBoot.HasValue) { sbParts.Add("Secure Boot " + (f.SecureBoot.Value ? "on" : "off")); sbHe.Add("Secure Boot " + (f.SecureBoot.Value ? "פועל" : "כבוי")); if (!f.SecureBoot.Value) { sbPen += 10; sbWarn = true; } }
                if (f.Tpm != null) { sbParts.Add(f.Tpm == "" ? "no TPM" : "TPM " + f.Tpm); sbHe.Add(f.Tpm == "" ? "אין TPM" : "TPM " + f.Tpm); if (f.Tpm == "") sbPen += 5; }
                if (f.Uac.HasValue && f.Uac.Value) { sbParts.Add("User Account Control on"); sbHe.Add("בקרת חשבון משתמש פועלת"); }
                if (sbParts.Count > 0)
                    add(new HFind { Area = "Security", Key = "secureboot", Level = sbWarn ? HLevel.Warn : sbPen > 0 ? HLevel.Info : HLevel.Ok, Penalty = sbPen, CanIgnore = sbPen > 0 && !sbWarn,
                                    Title = H(string.Join(" · ", sbParts), string.Join(" · ", sbHe)),
                                    Detail = sbWarn ? H("Secure Boot lets Windows start only trusted code, which stops boot-level malware. It is switched on in the PC's firmware (BIOS/UEFI) settings.", "Secure Boot מאפשר ל-Windows להפעיל רק קוד מהימן, וכך עוצר נוזקות ברמת האתחול. מפעילים אותו בהגדרות הקושחה (BIOS/UEFI) של המחשב.")
                                                    : H("Windows starts only trusted code; apps must ask before changing the PC", "Windows מפעיל רק קוד מהימן; אפליקציות חייבות לבקש רשות לפני שינוי במחשב") });
                if (f.Uac.HasValue && !f.Uac.Value)
                    add(new HFind { Area = "Security", Key = "uac", Level = HLevel.Bad, Penalty = 20, Act = HAct.Uac, Button = "Open UAC settings",
                                    Title = H("User Account Control is off", "בקרת חשבון משתמש כבויה"),
                                    Detail = H("Any program can change Windows without asking you. Turn it back on (a restart is needed).", "כל תוכנה יכולה לשנות את Windows בלי לשאול אותך. הפעל אותה שוב (נדרשת הפעלה מחדש).") });
                if (f.BitLocker == 0)
                {
                    bool lap = IsLaptop(f);
                    add(new HFind { Area = "Security", Key = "bitlocker", Level = HLevel.Info, Penalty = lap ? 5 : 0, Tag = "Your decision", CanIgnore = true, Act = HAct.BitLocker, Button = "How to...",
                                    Title = H("C: is not encrypted (BitLocker off)", "C: לא מוצפן (BitLocker כבוי)"),
                                    Detail = lap ? H("On a laptop, a lost or stolen PC means your files can be read. Save the recovery key before turning it on.", "במחשב נייד, אם הוא אובד או נגנב אפשר לקרוא את הקבצים שלך. שמור את מפתח השחזור לפני שמפעילים.")
                                                 : H("Matters most on laptops. If you turn it on, save the recovery key first.", "חשוב בעיקר במחשבים ניידים. אם מפעילים, קודם שומרים את מפתח השחזור.") });
                }
            }

            // ---- Stability (last 30 days) ----
            if (f.CrashesKnown)
            {
                known("Stability");
                var month = f.Crashes.Where(c => (now - c.When).TotalDays <= 30).ToList();
                var blue = month.Where(c => c.Kind == "bluescreen").OrderBy(c => c.When).ToList();
                if (blue.Count > 0)
                {
                    var codes = blue.GroupBy(c => c.Code).OrderByDescending(g => g.Count()).ToList();
                    string why = codes.Count == 1 ? H((blue.Count == 1 ? "Cause: " : "All: ") + StopText(codes[0].Key), (blue.Count == 1 ? "סיבה: " : "כולם: ") + StopText(codes[0].Key))
                                                  : H("Most often: " + StopText(codes[0].Key), "הכי הרבה: " + StopText(codes[0].Key));
                    add(new HFind { Area = "Stability", Key = "bluescreen", Level = HLevel.Bad, Penalty = Math.Min(60, 20 * blue.Count), Act = HAct.Drivers, Button = "Check drivers...",
                                    Title = H(Plural(blue.Count, "blue screen", "blue screens") + " in the last 30 days", blue.Count == 1 ? "מסך כחול אחד ב-30 הימים האחרונים" : blue.Count + " מסכים כחולים ב-30 הימים האחרונים"),
                                    Detail = why + " · " + string.Join(", ", blue.Select(c => c.When.ToString("d MMM", CultureInfo.InvariantCulture)).Distinct().Take(5)) +
                                             H(". Most blue screens come from a driver: update the graphics, chipset and Wi-Fi drivers from the PC maker.", ". רוב המסכים הכחולים נגרמים מדרייבר: עדכן את דרייברי הגרפיקה, ערכת השבבים וה-Wi-Fi מיצרן המחשב.") });
                }
                // a sudden power-off without a blue screen (power cut, power button held, overheating)
                var cut = month.Where(c => c.Kind == "shutdown" && (c.Code == "" || c.Code == "0") && !blue.Any(b => Math.Abs((b.When - c.When).TotalMinutes) < 10)).ToList();
                if (cut.Count >= 2)
                    add(new HFind { Area = "Stability", Key = "shutdown", Level = HLevel.Warn, Penalty = Math.Min(15, 5 * cut.Count),
                                    Title = H("Windows turned off unexpectedly " + cut.Count + " times", "Windows כבה באופן לא צפוי " + cut.Count + " פעמים"),
                                    Detail = H("A power cut, holding the power button, or the PC overheating. If it happens by itself, check the fans and the power supply.", "הפסקת חשמל, החזקת כפתור ההפעלה או התחממות יתר. אם זה קורה מעצמו, בדוק את המאווררים ואת ספק הכוח.") });
                foreach (var g in month.Where(c => c.Kind == "crash" && c.Name.Length > 0).GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count()).Take(4))
                {
                    var mod = g.Where(c => c.Module.Length > 0).GroupBy(c => c.Module, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).FirstOrDefault();
                    string app = AppName(g.Key);
                    bool same = mod != null && mod.Count() * 10 >= g.Count() * 6 && !mod.Key.Equals(g.Key, StringComparison.OrdinalIgnoreCase);
                    add(new HFind { Area = "Stability", Key = "crash:" + g.Key.ToLowerInvariant(), Level = HLevel.Warn, Penalty = 5, CanIgnore = true,
                                    Title = H(app + " crashed " + g.Count() + " times this month", app + " קרס " + g.Count() + " פעמים החודש"),
                                    Detail = same ? H("Most times in the same file (" + mod.Key + "): often an add-in or plug-in. Update or remove it.", "רוב הפעמים באותו קובץ (" + mod.Key + "): לרוב תוסף. עדכן או הסר אותו.")
                                                  : H("Updating or reinstalling it usually helps.", "עדכון או התקנה מחדש בדרך כלל עוזרים.") });
                }
                if (!r.Items.Any(i => i.Area == "Stability"))
                    add(new HFind { Area = "Stability", Key = "stable", Level = HLevel.Ok, Title = H("No blue screens or repeated crashes in the last 30 days", "אין מסכים כחולים או קריסות חוזרות ב-30 הימים האחרונים"),
                                    Detail = H("From Windows' own error records.", "מרישומי השגיאות של Windows.") });
            }

            // ---- Speed ----
            bool speed = (f.Startup != null && f.Startup.Boots.Count > 0) || f.RamLoad >= 0 || f.PowerScheme.Length > 0;
            if (speed)
            {
                known("Speed");
                if (f.Startup != null && f.Startup.Boots.Count > 0)
                {
                    var boots = f.Startup.Boots.OrderByDescending(b => b.When).ToList();
                    long recent = (long)boots.Take(3).Average(b => b.Ms);
                    var older = boots.Where(b => (boots[0].When - b.When).TotalDays >= 14).ToList();
                    long before = older.Count > 0 ? (long)older.Average(b => b.Ms) : 0;
                    var slow = f.Startup.Apps.Values.Where(a => (now - a.Last).TotalDays <= 30).OrderByDescending(a => a.AvgMs).Take(2).ToList();
                    string slowText = slow.Count == 0 ? "" : H("Slowest: ", "האיטיים ביותר: ") + string.Join(", ", slow.Select(a => (a.Friendly.Length > 0 ? a.Friendly : a.File) + " +" + StartupImpact.Seconds(a.AvgMs))) + H(" (Windows' own measurements)", " (המדידות של Windows)");
                    bool slowStart = recent > 90000, worse = before > 0 && recent > before * 13 / 10 && recent - before > 10000;
                    add(new HFind { Area = "Speed", Key = "boot", Level = recent > 150000 ? HLevel.Bad : slowStart || worse ? HLevel.Warn : HLevel.Ok, Penalty = recent > 150000 ? 25 : slowStart || worse ? 15 : 0,
                                    Act = HAct.Startup, Button = slowStart || worse ? "Startup..." : "",
                                    Title = H("Windows takes " + Dur(recent) + " to start" + (before > 0 ? " (" + Dur(before) + " " + (older[0].When > now.AddDays(-45) ? "a few weeks ago" : "before") + ")" : ""),
                                              "Windows עולה תוך " + DurHe(recent) + (before > 0 ? " (" + DurHe(before) + " לפני כמה שבועות)" : "")),
                                    Detail = slowText.Length > 0 ? slowText : H("From Windows' own startup measurements.", "מהמדידות של Windows בזמן ההפעלה.") });
                }
                if (f.RamLoad >= 0)
                {
                    string top = string.Join(", ", f.TopMemory.Take(3).Select(t => t.Item1 + " " + Fs.Fmt(t.Item2)));
                    long gb = (f.RamTotal + (512L << 20)) >> 30;
                    bool full = f.RamLoad >= 90, busy = f.RamLoad >= 80, little = gb > 0 && gb < 8;
                    add(new HFind { Area = "Speed", Key = "memory", Level = full ? HLevel.Warn : little ? HLevel.Info : HLevel.Ok, Penalty = full ? 15 : little ? 5 : 0, Act = HAct.Memory, Button = full || busy ? "What uses it" : "",
                                    Title = H("Memory is " + f.RamLoad + "% full" + (full ? "" : busy ? " (busy)" : ""), "הזיכרון מלא ב-" + f.RamLoad + "%" + (full ? "" : busy ? " (עמוס)" : "")),
                                    Detail = H(gb + " GB in this PC" + (top.Length > 0 ? " · most: " + top : "") + (full || little ? ". More RAM would help most; closing browser tabs helps now." : ""),
                                               gb + " GB במחשב" + (top.Length > 0 ? " · הכי הרבה: " + top : "") + (full || little ? ". יותר זיכרון יעזור הכי הרבה; סגירת לשוניות בדפדפן עוזרת עכשיו." : "")) });
                }
                if (f.PowerScheme.Equals(PowerSaver, StringComparison.OrdinalIgnoreCase) && !f.OnBattery)
                    add(new HFind { Area = "Speed", Key = "power", Level = HLevel.Info, Penalty = 5, Tag = "Your decision", CanIgnore = true, Act = HAct.PowerBalanced, Button = "Use Balanced",
                                    Title = H("Power plan: Power saver while plugged in", "תוכנית צריכת חשמל: חיסכון בחשמל כשמחובר לחשמל"),
                                    Detail = H("Balanced is faster and still saves power on battery.", "מאוזן מהיר יותר ועדיין חוסך בחשמל על סוללה.") });
            }

            // ---- Space ----
            if (f.Free >= 0 && f.Total > 0)
            {
                known("Space");
                double pct = f.Free * 100.0 / f.Total;
                var lvl = pct < 10 ? HLevel.Bad : pct < 15 ? HLevel.Warn : HLevel.Ok;
                add(new HFind { Area = "Space", Key = "free", Level = lvl, Penalty = pct < 5 ? 50 : pct < 10 ? 35 : pct < 15 ? 15 : 0, Act = HAct.CheckFix, Button = lvl == HLevel.Ok ? "" : "Check & fix...",
                                Title = H(Fs.Fmt(f.Free) + " free on C: (" + (int)Math.Round(pct) + "%)", Fs.Fmt(f.Free) + " פנויים ב-C: (" + (int)Math.Round(pct) + "%)"),
                                Detail = lvl == HLevel.Ok ? H("Enough room for updates and Windows' own work.", "מספיק מקום לעדכונים ולעבודה של Windows.")
                                                          : H("Windows slows down and updates fail when C: is nearly full.", "כש-C: כמעט מלא, Windows מאט ועדכונים נכשלים.") });
                if (f.UnusualFolders > 0)
                    add(new HFind { Area = "Space", Key = "unusual", Level = HLevel.Warn, Penalty = 10, Act = HAct.Growing, Button = "Show me",
                                    Title = H(Plural(f.UnusualFolders, "folder grows fast and looks unusual", "folders grow fast and look unusual"), f.UnusualFolders == 1 ? "תיקייה אחת גדלה מהר ונראית חריגה" : f.UnusualFolders + " תיקיות גדלות מהר ונראות חריגות"),
                                    Detail = H("Scan it with Microsoft Defender (Dashboard > Growing fast).", "סרוק עם Microsoft Defender (לוח בקרה > גדל מהר).") });
                else if (f.FastFolders > 0)
                    add(new HFind { Area = "Space", Key = "fast", Level = HLevel.Info, Penalty = 5, Act = HAct.Growing, Button = "Show me",
                                    Title = H(Plural(f.FastFolders, "folder is growing fast", "folders are growing fast"), f.FastFolders == 1 ? "תיקייה אחת גדלה מהר" : f.FastFolders + " תיקיות גדלות מהר"),
                                    Detail = H("Elixar studied each one (Dashboard > Growing fast).", "Elixar בדק כל אחת (לוח בקרה > גדל מהר).") });
            }

            // ---- Drives ----
            if (f.Disk != HealthLevel.Unknown)
            {
                known("Drives");
                add(new HFind { Area = "Drives", Key = "disk", Level = f.Disk == HealthLevel.Failing ? HLevel.Bad : f.Disk == HealthLevel.Warning ? HLevel.Warn : HLevel.Ok,
                                Penalty = f.Disk == HealthLevel.Failing ? 100 : f.Disk == HealthLevel.Warning ? 40 : 0, Act = HAct.DiskHealth, Button = f.Disk == HealthLevel.Healthy ? "" : "Disk health...",
                                Title = f.Disk == HealthLevel.Failing ? H("A drive is failing", "כונן כושל") : f.Disk == HealthLevel.Warning ? H("A drive is worn", "כונן שחוק") : H("The drives are healthy", "הכוננים תקינים"),
                                Detail = f.Disk == HealthLevel.Failing ? H("Back up your files now and replace it.", "גבה את הקבצים עכשיו והחלף אותו.") : f.Disk == HealthLevel.Warning ? H("Keep a backup and plan to replace it.", "שמור גיבוי ותכנן להחליף אותו.")
                                                                       : H("From the health counters the drives keep themselves.", "ממוני הבריאות שהכוננים עצמם שומרים.") });
            }

            // ---- Safety net ----
            if (f.RestoreKnown || f.FileHistory.HasValue || f.OneDriveBackup.HasValue)
            {
                known("Safety net");
                if (f.RestoreKnown)
                {
                    int days = f.LastRestore.HasValue ? Days(now, f.LastRestore.Value) : -1;
                    bool old = days < 0 || days > 30;
                    add(new HFind { Area = "Safety net", Key = "restore", Level = old ? HLevel.Warn : HLevel.Ok, Penalty = old ? 25 : 0, CanIgnore = old, Act = HAct.RestorePoint, Button = old ? "Make one now" : "",
                                    Title = days < 0 ? H("No restore point", "אין נקודת שחזור") : H("Last restore point: " + (days == 0 ? "today" : Plural(days, "day", "days") + " ago"), "נקודת השחזור האחרונה: " + (days == 0 ? "היום" : "לפני " + days + " ימים")),
                                    Detail = H("If an update or a driver breaks Windows, a restore point takes it back.", "אם עדכון או דרייבר שוברים את Windows, נקודת שחזור מחזירה אותו לאחור.") });
                }
                bool backed = (f.FileHistory ?? false) || (f.OneDriveBackup ?? false);
                if (f.FileHistory.HasValue || f.OneDriveBackup.HasValue)
                    add(new HFind { Area = "Safety net", Key = "backup", Level = backed ? HLevel.Ok : HLevel.Warn, Penalty = backed ? 0 : 35, CanIgnore = !backed, Act = HAct.Backup, Button = backed ? "" : "Set up backup...",
                                    Title = backed ? H("Your files are backed up (" + ((f.OneDriveBackup ?? false) ? "OneDrive" : "File History") + ")", "הקבצים שלך מגובים (" + ((f.OneDriveBackup ?? false) ? "OneDrive" : "היסטוריית קבצים") + ")")
                                                   : H("No backup of your files found", "לא נמצא גיבוי של הקבצים שלך"),
                                    Detail = backed ? H("Documents and Desktop are kept somewhere else too.", "המסמכים ושולחן העבודה נשמרים גם במקום אחר.")
                                                    : H("Neither File History nor OneDrive backup is on. A failing drive or ransomware would take your documents. Use another backup program? Click Ignore.", "לא היסטוריית קבצים ולא גיבוי OneDrive פועלים. כונן כושל או כופרה ייקחו את המסמכים שלך. משתמש בתוכנת גיבוי אחרת? לחץ התעלם.") });
            }

            // ---- Battery (laptops; not part of the score) ----
            r.Laptop = IsLaptop(f);
            if (r.Laptop && f.DesignMwh > 0 && f.FullMwh > 0)
            {
                int pct = (int)Math.Round(Math.Min(100.0, f.FullMwh * 100.0 / f.DesignMwh));
                r.BatteryPct = pct;
                add(new HFind { Area = "Battery", Key = "battery", Level = pct < 60 ? HLevel.Bad : pct < 80 ? HLevel.Warn : HLevel.Ok,
                                Title = H(pct + "% of its original capacity", pct + "% מהקיבולת המקורית"),
                                Detail = H("Holds " + Wh(f.FullMwh) + " of " + Wh(f.DesignMwh) + " Wh" + (f.Cycles > 0 ? " · " + f.Cycles + " charge cycles" : "") + " · from Windows' own battery data. " +
                                           (pct < 60 ? "Think about a new battery." : pct < 80 ? "It runs out noticeably sooner than when it was new." : "Like new."),
                                           "מחזיקה " + Wh(f.FullMwh) + " מתוך " + Wh(f.DesignMwh) + " Wh" + (f.Cycles > 0 ? " · " + f.Cycles + " מחזורי טעינה" : "") + " · מנתוני הסוללה של Windows. " +
                                           (pct < 60 ? "כדאי לשקול סוללה חדשה." : pct < 80 ? "היא נגמרת מהר יותר באופן מורגש מאשר כשהייתה חדשה." : "כמו חדשה.")) });
            }

            r.Score();
            return r;
        }

        static string Wh(long mwh) { return (mwh / 1000.0).ToString("0.0", CultureInfo.InvariantCulture); }
        static string Dur(long ms) { long s = (ms + 500) / 1000; return s >= 60 ? (s / 60) + " min " + (s % 60) + " s" : s + " s"; }
        static string DurHe(long ms) { long s = (ms + 500) / 1000; return s >= 60 ? (s / 60) + " דק׳ " + (s % 60) + " שנ׳" : s + " שנ׳"; }

        // "OUTLOOK.EXE" -> "Outlook"
        static readonly Dictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "outlook.exe", "Outlook" }, { "winword.exe", "Word" }, { "excel.exe", "Excel" }, { "powerpnt.exe", "PowerPoint" }, { "chrome.exe", "Chrome" }, { "msedge.exe", "Edge" },
            { "firefox.exe", "Firefox" }, { "explorer.exe", "File Explorer" }, { "teams.exe", "Teams" }, { "ms-teams.exe", "Teams" }, { "zoom.exe", "Zoom" }, { "acrord32.exe", "Adobe Reader" }, { "acrobat.exe", "Adobe Acrobat" },
        };
        public static string AppName(string exe)
        {
            string s; if (Known.TryGetValue(exe ?? "", out s)) return s;
            s = Path.GetFileNameWithoutExtension(exe ?? "");
            return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s.Substring(1) : exe;
        }

        // ---------------- reading on this PC ----------------
        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

        static bool? RegBool(string path, string name, int on)
        {
            try
            {
                using (var b = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var k = b.OpenSubKey(path))
                {
                    object v = k == null ? null : k.GetValue(name);
                    if (v is int) return (int)v == on;
                    return null;
                }
            }
            catch { return null; }
        }

        static readonly object appGate = new object(); static List<AppUpdate> apps; static DateTime appsAt = DateTime.MinValue; [ThreadStatic] static bool fresh;
        public static HealthFacts Collect(bool withAppUpdates, Action<string> status) { return Collect(withAppUpdates, false, status); }
        public static HealthFacts Collect(bool withAppUpdates, bool freshApps, Action<string> status)
        {
            fresh = freshApps;
            if (FactsOverride != null) return FactsOverride();
            var f = new HealthFacts();
            Action<string> say = s => { if (status != null) status(s); };
            try { f.Free = Sys.Free(); f.Total = Sys.Total(); } catch { }
            if (!Win.IsWindows) return f;

            say("Reading Windows Security, updates and error records...");
            try { Parse(ScriptOverride != null ? ScriptOverride() : Sys.RunPowerShell(Script, 120000), f); } catch { }
            f.SecureBoot = RegBool(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", 1);
            f.Uac = RegBool(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA", 1);
            try { f.RestartPending = CheckFix.RestartPendingNow(); } catch { }

            say("Reading startup times and memory...");
            try { f.Startup = StartupImpact.Read(); } catch { }
            try
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                if (GlobalMemoryStatusEx(ref m)) { f.RamLoad = (int)m.dwMemoryLoad; f.RamTotal = (long)m.ullTotalPhys; }
            }
            catch { }
            try
            {
                var by = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in Process.GetProcesses())
                    try { using (p) { long b = p.PrivateMemorySize64; string n = p.ProcessName; long had; by[n] = (by.TryGetValue(n, out had) ? had : 0) + b; } } catch { }
                f.TopMemory = by.Where(kv => kv.Value > (200L << 20) && !kv.Key.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(kv => kv.Value).Take(5).Select(kv => Tuple.Create(AppName(kv.Key + ".exe"), kv.Value)).ToList();
            }
            catch { }
            try
            {
                var o = Sys.Run("powercfg.exe", "/getactivescheme", 15000);
                var m = Regex.Match(o, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                if (m.Success) f.PowerScheme = m.Value.ToLowerInvariant();
            }
            catch { }
            try
            {
                var ps = System.Windows.Forms.SystemInformation.PowerStatus;
                f.HasBattery = (ps.BatteryChargeStatus & System.Windows.Forms.BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
                f.OnBattery = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline;
            }
            catch { }

            say("Checking the drives and backups...");
            try { f.Disk = DiskHealth.Read().Worst; } catch { }
            try { var cur = FolderGrowth.Current(); f.FastFolders = cur.Count; f.UnusualFolders = cur.Count(x => x.Item2 == GrowKind.Unusual); } catch { }
            f.FileHistory = FileHistoryOn();
            f.OneDriveBackup = OneDriveBacksUp();

            if (withAppUpdates && !Policy.Allowed("apps.update")) f.AppsManaged = true;   // the company manages app updates: winget isn't asked at all
            else if (withAppUpdates)
            {
                lock (appGate)
                {
                    if (appsAt > DateTime.Now.AddHours(-6) && apps != null && !fresh) f.AppUpdates = apps;
                    else
                    {
                        say("Checking for app updates...");
                        try { if (WinGet.Available) { string e; f.AppUpdates = WinGet.List(out e).Where(u => WinGet.TrustedSource(u.Source)).ToList(); apps = f.AppUpdates; appsAt = DateTime.Now; } } catch { }
                    }
                }
                f.AppsManaged = !Policy.Allowed("apps.update");
            }
            return f;
        }

        // File History keeps its settings in your profile once it has been set up and switched on
        static bool? FileHistoryOn()
        {
            try
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\FileHistory\Configuration");
                if (!Directory.Exists(d)) return false;
                foreach (var x in Directory.GetFiles(d, "Config*.xml"))
                {
                    if (new FileInfo(x).Length > (1 << 20)) continue;
                    string t = File.ReadAllText(x);
                    // <UserData><... and a target; "Enabled" is 1 while it runs
                    if (Regex.IsMatch(t, @"<Enabled>\s*1\s*</Enabled>|Enabled=""1""|<IsEnabled>\s*true", RegexOptions.IgnoreCase)) return true;
                }
                return false;
            }
            catch { return null; }
        }

        // OneDrive's folder backup moves Documents (and Desktop) into the OneDrive folder
        static bool? OneDriveBacksUp()
        {
            try
            {
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrEmpty(docs)) return null;
                foreach (var a in OneDrive.Accounts())
                    if (!string.IsNullOrEmpty(a.Folder) && docs.StartsWith(a.Folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            catch { return null; }
        }

        // ---------------- the few things Elixar does itself (after you click, and logged) ----------------
        public static string UseBalanced()
        {
            if (SetPowerOverride != null) return SetPowerOverride(Balanced) ? null : "failed";
            if (!Win.IsWindows) return "Windows only";
            string o = Sys.Run("powercfg.exe", "/setactive " + Balanced, 30000);   // in Logs as "Power settings"
            var check = Sys.Run("powercfg.exe", "/getactivescheme", 15000);
            return check.IndexOf(Balanced, StringComparison.OrdinalIgnoreCase) >= 0 ? null : (o.Trim().Length > 0 ? o.Trim() : "Windows didn't change the power plan.");
        }

        public static string MakeRestorePoint()
        {
            if (CheckFix.RestorePointOverride != null) return CheckFix.RestorePointOverride();
            if (!Win.IsWindows) return "";
            try
            {
                return Sys.RunPowerShell("try { Checkpoint-Computer -Description 'Made by Elixar (PC health)' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue; " +
                    "if ($w) { 'Restore point not created: ' + ($w -join ' ') } else { 'Restore point created.' } } catch { 'Restore point not created: ' + $_.Exception.Message }", 5 * 60 * 1000).Trim();
            }
            catch (Exception ex) { return "Restore point not created: " + ex.Message; }
        }

        // Windows' own settings pages: fixed names only, never text from a report
        public static readonly Dictionary<HAct, string[]> Pages = new Dictionary<HAct, string[]>
        {
            { HAct.Defender, new[] { "windowsdefender:", "" } },
            { HAct.Firewall, new[] { "windowsdefender://network", "" } },
            { HAct.Uac, new[] { "UserAccountControlSettings.exe", "" } },
            { HAct.BitLocker, new[] { "control.exe", "/name Microsoft.BitLockerDriveEncryption" } },
            { HAct.Backup, new[] { "control.exe", "/name Microsoft.FileHistory" } },
            { HAct.SystemProtection, new[] { "SystemPropertiesProtection.exe", "" } },
            { HAct.Memory, new[] { "taskmgr.exe", "" } },
            { HAct.Restart, new[] { "ms-settings:windowsupdate", "" } },
        };
        public static string Open(HAct a)
        {
            string[] p;
            if (!Pages.TryGetValue(a, out p)) return "nothing to open";
            if (!Win.IsWindows) return "Windows only";
            try
            {
                if (p[0].EndsWith(".exe")) Process.Start(new ProcessStartInfo(Win.SystemExe(p[0]), p[1]) { UseShellExecute = false });
                else Process.Start(new ProcessStartInfo(p[0]) { UseShellExecute = true });
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------------- the last score, for the Dashboard before a new check finishes ----------------
        public static void Remember(HealthReport r)
        {
            if (r == null || r.Total < 0) return;
            try { Settings.Set("HealthLast", r.Total + "|" + r.At.ToString("s", CultureInfo.InvariantCulture) + "|" + string.Join(",", HealthReport.Areas.Select(a => r.Scores[a])) + "|" + r.ToFix); } catch { }
        }
        public static HealthReport Remembered()
        {
            try
            {
                var p = Settings.Get("HealthLast", "").Split('|');
                int t, n; DateTime at;
                if (p.Length < 4 || !int.TryParse(p[0], out t) || !DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out at) || !int.TryParse(p[3], out n)) return null;
                var s = p[2].Split(',');
                if (s.Length != HealthReport.Areas.Length || t < 0 || t > 100) return null;
                var r = new HealthReport { Total = t, At = at };
                for (int i = 0; i < s.Length; i++) { int v; r.Scores[HealthReport.Areas[i]] = int.TryParse(s[i], out v) && v >= -1 && v <= 100 ? v : -1; }
                for (int i = 0; i < n && i < 50; i++) r.Items.Add(new HFind { Level = HLevel.Warn });
                return r;
            }
            catch { return null; }
        }

        public static HealthReport Check(bool withAppUpdates, Action<string> status) { return Check(withAppUpdates, false, status); }
        public static HealthReport Check(bool withAppUpdates, bool freshApps, Action<string> status)
        {
            var f = Collect(withAppUpdates, freshApps, status);
            var r = Evaluate(f, DateTime.Now, IgnoredKeys());
            Remember(r);
            return r;
        }
    }
}
