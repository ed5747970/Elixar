// Disk health: reads the health, wear and error counters your drives already keep (the same data Windows uses)
// and warns before a drive fails. Read only: nothing is written to any drive, nothing is downloaded or sent anywhere.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CDriveCleaner
{
    public enum HealthLevel { Unknown, Healthy, Warning, Failing }

    public class DiskInfo
    {
        public string Id = "", Name = "", Media = "", Bus = "", Health = "", OpStatus = "", Letters = "";
        public long Size = -1, ReadErrors = -1, ReadUncorrected = -1, WriteErrors = -1, WriteUncorrected = -1, Hours = -1;
        public int Wear = -1, Temp = -1, TempMax = -1;
        public bool? PredictFailure;
        public HealthLevel Level;
        public List<string> Reasons = new List<string>();
        public List<string> Notes = new List<string>();
        public string Advice = "";

        public bool Solid { get { return Media == "SSD" || Media == "SCM" || Bus == "NVMe"; } }
        public bool Spinning { get { return Media == "HDD"; } }
        public int LifeLeft { get { return Wear < 0 ? -1 : Math.Max(0, 100 - Wear); } }
        public bool HasCounters { get { return Wear >= 0 || Temp > 0 || ReadErrors >= 0 || WriteErrors >= 0 || Hours >= 0; } }
        public bool HoldsC { get { return Letters.Split(',').Any(l => l.Trim().Equals("C", StringComparison.OrdinalIgnoreCase)); } }
        public long Uncorrected { get { return Math.Max(0, ReadUncorrected) + Math.Max(0, WriteUncorrected); } }
        public long Corrected { get { return Math.Max(0, Math.Max(0, ReadErrors) + Math.Max(0, WriteErrors) - Uncorrected); } }
        public int HotAt { get { return Spinning ? DiskHealth.HddHot : Solid ? DiskHealth.SsdHot : 60; } }

        public string Kind
        {
            get
            {
                if (Bus == "USB") return Solid ? "USB SSD" : Spinning ? "USB hard disk" : "USB drive";
                if (Bus == "NVMe") return "NVMe SSD";
                string m = Media == "SSD" ? "SSD" : Media == "HDD" ? "hard disk" : Media == "SCM" ? "storage-class memory" : "drive";
                return (Bus == "SATA" || Bus == "SAS" || Bus == "RAID" ? Bus + " " : Bus == "Virtual" || Bus == "File Backed Virtual" ? "virtual " : "") + m;
            }
        }
        public string Label { get { return Letters.Length > 0 ? string.Join(" ", Letters.Split(',').Select(l => l.Trim() + ":")) + "  " + Name : Name; } }
    }

    public class DiskReport
    {
        public List<DiskInfo> Disks = new List<DiskInfo>();
        public bool Admin, Complete;
        public string Error;
        public HealthLevel Worst { get { return Disks.Count == 0 ? HealthLevel.Unknown : Disks.Max(d => d.Level); } }
        public string Summary { get { return DiskHealth.Summarize(this); } }
    }

    public static class DiskHealth
    {
        public const int SsdHot = 70, HddHot = 55, WearWarn = 80;

        // Fixed text: nothing from outside is ever put into it. It only calls Get-* commands (checked by the self-test)
        // and prints one tab-separated line per drive. Serial numbers are not read.
        public const string Script = @"$ErrorActionPreference = 'SilentlyContinue'
function Txt($v) { if ($null -eq $v) { return '' }; return (([string]$v) -replace ""[`t`r`n]"", ' ').Trim() }
$admin = $false
try { $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch {}
""ADMIN`t$admin""
$pred = @{}
try { foreach ($p in @(Get-CimInstance -Namespace root\wmi -ClassName MSStorageDriver_FailurePredictStatus)) { $pred[[string]$p.InstanceName] = [bool]$p.PredictFailure } } catch {}
$pnp = @{}
try { foreach ($w in @(Get-CimInstance -ClassName Win32_DiskDrive)) { $pnp[[string]$w.Index] = [string]$w.PNPDeviceID } } catch {}
$disks = @()
try { $disks = @(Get-PhysicalDisk) } catch {}
foreach ($d in $disks) {
  try {
    $id = [string]$d.DeviceId
    $r = $null
    try { $r = $d | Get-StorageReliabilityCounter } catch {}
    $letters = ''
    try { $letters = (@(Get-Partition -DiskNumber ([int]$id) | Where-Object { $_.DriveLetter } | ForEach-Object { [string]$_.DriveLetter }) -join ',') } catch {}
    $pf = ''
    $dev = [string]$pnp[$id]
    if ($dev.Length -gt 0) { foreach ($k in $pred.Keys) { if ($k.ToUpperInvariant().StartsWith($dev.ToUpperInvariant())) { $pf = [string]$pred[$k] } } }
    $ops = ''
    try { $ops = (@($d.OperationalStatus) | ForEach-Object { [string]$_ }) -join ',' } catch {}
    $f = @('DISK', $id, (Txt $d.FriendlyName), (Txt $d.MediaType), (Txt $d.BusType), (Txt $d.HealthStatus), (Txt $ops), (Txt $d.Size),
           (Txt $r.Wear), (Txt $r.Temperature), (Txt $r.TemperatureMax), (Txt $r.ReadErrorsTotal), (Txt $r.ReadErrorsUncorrected),
           (Txt $r.WriteErrorsTotal), (Txt $r.WriteErrorsUncorrected), (Txt $r.PowerOnHours), $pf, $letters)
    $f -join ""`t""
  } catch {}
}
'END'
";

        // the only commands the script may use: reading, filtering, nothing that changes anything
        public static readonly string[] AllowedCommands = { "Get-CimInstance", "Get-PhysicalDisk", "Get-StorageReliabilityCounter", "Get-Partition", "Where-Object", "ForEach-Object" };

        public static List<string> CommandsIn(string script)
        {
            return Regex.Matches(script, @"\b[A-Za-z]+-[A-Za-z]+\b").Cast<Match>().Select(m => m.Value)
                        .Where(w => char.IsUpper(w[0]) && char.IsUpper(w[w.IndexOf('-') + 1])).Distinct().ToList();
        }

        public static bool ScriptOnlyReads(string script)
        {
            string low = script.ToLowerInvariant();
            return CommandsIn(script).All(c => AllowedCommands.Contains(c)) &&
                   new[] { "http", "iex", "invoke", "start-", "set-", "remove-", "new-", "out-file", "add-content", ">", "&", "format", "clear", "repair", "optimize", "initialize", "reset" }
                       .All(bad => !low.Contains(bad));
        }

        // Windows reports some values as numbers and some as names, depending on version: accept both
        static readonly Dictionary<string, string> MediaNames = new Dictionary<string, string> { { "0", "" }, { "3", "HDD" }, { "4", "SSD" }, { "5", "SCM" }, { "Unspecified", "" } };
        static readonly Dictionary<string, string> BusNames = new Dictionary<string, string>
        {
            { "1", "SCSI" }, { "2", "ATAPI" }, { "3", "ATA" }, { "4", "1394" }, { "5", "SSA" }, { "6", "Fibre Channel" }, { "7", "USB" }, { "8", "RAID" }, { "9", "iSCSI" },
            { "10", "SAS" }, { "11", "SATA" }, { "12", "SD" }, { "13", "MMC" }, { "14", "Virtual" }, { "15", "File Backed Virtual" }, { "16", "Storage Spaces" }, { "17", "NVMe" },
            { "FileBackedVirtual", "File Backed Virtual" }, { "Spaces", "Storage Spaces" }, { "Fibre Channel", "Fibre Channel" }
        };
        static readonly Dictionary<string, string> HealthNames = new Dictionary<string, string> { { "0", "Healthy" }, { "1", "Warning" }, { "2", "Unhealthy" }, { "5", "Unknown" } };
        static readonly Dictionary<string, string> OpNames = new Dictionary<string, string>
        {
            { "0", "Unknown" }, { "1", "Other" }, { "2", "OK" }, { "3", "Degraded" }, { "4", "Stressed" }, { "5", "Predictive Failure" }, { "6", "Error" }, { "7", "Non-Recoverable Error" },
            { "8", "Starting" }, { "9", "Stopping" }, { "10", "Stopped" }, { "11", "In Service" }, { "12", "No Contact" }, { "13", "Lost Communication" }, { "14", "Aborted" },
            { "15", "Dormant" }, { "16", "Supporting Entity in Error" }, { "17", "Completed" }, { "53264", "Online" }, { "53265", "Not Ready" }, { "53266", "No Media" }, { "53267", "Offline" }, { "53268", "Failed" }
        };
        static string Name(Dictionary<string, string> map, string v) { v = (v ?? "").Trim(); string n; return map.TryGetValue(v, out n) ? n : v; }
        static long L(string v) { long x; return long.TryParse((v ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x) && x >= 0 ? x : -1; }
        static int I(string v) { long x = L(v); return x < 0 || x > int.MaxValue ? -1 : (int)x; }

        public static DiskReport Parse(string output)
        {
            var rep = new DiskReport();
            foreach (var raw in (output ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                var f = line.Split('\t');
                if (f[0] == "ADMIN" && f.Length > 1) rep.Admin = f[1].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
                else if (f[0].Trim() == "END") rep.Complete = true;
                else if (f[0] == "DISK" && f.Length >= 17)
                {
                    var d = new DiskInfo
                    {
                        Id = f[1].Trim(), Name = f[2].Trim().Length > 0 ? f[2].Trim() : "Disk " + f[1].Trim(), Media = Name(MediaNames, f[3]), Bus = Name(BusNames, f[4]),
                        Health = Name(HealthNames, f[5]), OpStatus = string.Join(", ", f[6].Split(',').Select(o => Name(OpNames, o)).Where(o => o.Length > 0)),
                        Size = L(f[7]), Wear = I(f[8]), Temp = I(f[9]), TempMax = I(f[10]), ReadErrors = L(f[11]), ReadUncorrected = L(f[12]),
                        WriteErrors = L(f[13]), WriteUncorrected = L(f[14]), Hours = L(f[15]), Letters = f.Length > 17 ? f[17].Trim() : ""
                    };
                    string pf = f[16].Trim();
                    if (pf.Length > 0) d.PredictFailure = pf.Equals("True", StringComparison.OrdinalIgnoreCase);
                    if (d.Wear > 100) d.Wear = -1;          // not a percentage: ignore
                    if (d.Temp > 150) d.Temp = -1;          // nonsense reading (some USB bridges report 255)
                    if (d.TempMax > 150) d.TempMax = -1;
                    rep.Disks.Add(d);
                }
            }
            foreach (var d in rep.Disks) Judge(d, rep.Admin);
            // the drive with Windows on it first, then the most worrying
            rep.Disks = rep.Disks.OrderByDescending(d => d.HoldsC).ThenByDescending(d => d.Level).ThenBy(d => d.Id).ToList();
            return rep;
        }

        static bool Has(string ops, params string[] words) { return words.Any(w => ops.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0); }
        static string NotOk(string ops) { return string.Join(", ", ops.Split(',').Select(o => o.Trim()).Where(o => o.Length > 0 && o != "OK" && o != "Online")); }

        public static void Judge(DiskInfo d, bool admin)
        {
            d.Reasons.Clear(); d.Notes.Clear();
            var fail = new List<string>(); var warn = new List<string>();
            if (d.Health == "Unhealthy") fail.Add("Windows reports this drive as unhealthy.");
            if (d.PredictFailure == true) fail.Add("The drive's own self-check (S.M.A.R.T.) predicts it will fail soon.");
            if (Has(d.OpStatus, "Predictive Failure")) fail.Add("Windows marks this drive: predictive failure.");
            if (Has(d.OpStatus, "Non-Recoverable Error", "Failed")) fail.Add("Windows marks this drive: " + NotOk(d.OpStatus) + ".");
            if (d.Wear >= 100) fail.Add("It has used up all of its rated write life (100%).");
            if (d.Health == "Warning") warn.Add("Windows reports a warning for this drive.");
            if (!Has(d.OpStatus, "Non-Recoverable Error", "Failed") && Has(d.OpStatus, "Degraded", "Stressed", "Error", "Lost Communication", "No Contact"))
                warn.Add("Windows marks this drive: " + NotOk(d.OpStatus) + ".");
            if (d.Wear >= WearWarn && d.Wear < 100) warn.Add("Only " + d.LifeLeft + "% of its rated write life is left.");
            if (d.Uncorrected > 0) warn.Add(d.Uncorrected + " read or write errors could not be fixed by the drive.");
            if (d.Temp >= d.HotAt) warn.Add("It is running hot: " + d.Temp + " °C (" + d.HotAt + " °C or more is too warm for this kind of drive).");

            if (fail.Count > 0) d.Level = HealthLevel.Failing;
            else if (warn.Count > 0) d.Level = HealthLevel.Warning;
            else if (d.Health == "Healthy" || (d.HasCounters && d.Health != "Unknown")) d.Level = HealthLevel.Healthy;
            else d.Level = d.HasCounters ? HealthLevel.Healthy : HealthLevel.Unknown;
            d.Reasons.AddRange(fail); d.Reasons.AddRange(warn);

            if (d.Wear >= 0 && d.Wear < WearWarn && d.Solid) d.Notes.Add(d.LifeLeft + "% of its rated write life is left.");
            if (d.Corrected > 0 && d.Uncorrected == 0) d.Notes.Add("The drive fixed " + d.Corrected + " read or write errors by itself. A few are normal; check again if the number keeps growing.");
            if (d.TempMax >= d.HotAt + 10 && d.Temp < d.HotAt) d.Notes.Add("It has reached " + d.TempMax + " °C at some point. Make sure the PC's air vents are free.");
            if (!d.HasCounters)
                d.Notes.Add(!admin ? "Wear, temperature and error counts need administrator rights: open Elixar as administrator to see them."
                                   : d.Bus == "USB" ? "Windows can't read wear or temperature through this USB connection; that is normal for many USB drives."
                                   : "This drive doesn't share its wear and temperature with Windows.");

            d.Advice = d.Level == HealthLevel.Failing
                ? "Back up now. Copy your important files to another drive or to the cloud today, then plan to replace this drive. Until it is backed up, avoid moving large amounts of data on it."
                : d.Level == HealthLevel.Warning
                ? (warn.All(w => w.StartsWith("It is running hot"))
                    ? "Let the PC cool down: make sure its air vents are free and nothing heavy is running. Check again later."
                    : "Keep a recent backup of anything important and check again in a few days. Replace the drive if the problem grows.")
                : d.Level == HealthLevel.Healthy ? "Nothing to do." : "Windows did not share enough about this drive to judge it.";
        }

        public static string Summarize(DiskReport r)
        {
            if (r.Error != null) return r.Error;
            if (r.Disks.Count == 0) return "Windows did not report any drives.";
            var bad = r.Disks.Where(d => d.Level == HealthLevel.Failing).ToList();
            if (bad.Count > 0) return bad[0].Label + " may fail soon: back up your files now.";
            var warn = r.Disks.Where(d => d.Level == HealthLevel.Warning).ToList();
            if (warn.Count > 0) return warn[0].Label + " needs attention." + (warn.Count > 1 ? " So do " + (warn.Count - 1) + " more." : "");
            if (r.Disks.All(d => d.Level == HealthLevel.Healthy)) return r.Disks.Count == 1 ? "Your drive looks healthy." : "All " + r.Disks.Count + " drives look healthy.";
            return "No problems found, but Windows did not share enough about every drive.";
        }

        public static string LevelText(HealthLevel l)
        {
            return l == HealthLevel.Failing ? "May fail soon" : l == HealthLevel.Warning ? "Needs attention" : l == HealthLevel.Healthy ? "Healthy" : "Unknown";
        }

        public static string Hours(long h)
        {
            if (h < 0) return "";
            if (h < 48) return h + " hours";
            if (h < 24 * 365) return (h / 24) + " days";
            return (h / (24.0 * 365)).ToString("0.0", CultureInfo.InvariantCulture) + " years";
        }

        public static string Errors(DiskInfo d)
        {
            if (d.ReadErrors < 0 && d.WriteErrors < 0 && d.ReadUncorrected < 0 && d.WriteUncorrected < 0) return "";
            if (d.Uncorrected > 0) return d.Uncorrected + " not fixed";
            if (d.Corrected > 0) return d.Corrected + " fixed by the drive";
            return "none";
        }

        public static DiskReport Read()
        {
            if (!Win.IsWindows) return new DiskReport { Error = "Disk health needs Windows." };
            try
            {
                var r = Parse(StoreData.RunPs(Script, 90000));
                if (!r.Complete && r.Disks.Count == 0) r.Error = "Windows did not answer in time. Try again in a moment.";
                return r;
            }
            catch (Exception e) { return new DiskReport { Error = "Could not read disk health: " + e.Message }; }
        }

        // the daily check: a failing drive is reported every day, a warning once a week, and at once when it gets worse
        public static bool ShouldAlert(HealthLevel now, string last, DateTime today)
        {
            if (now < HealthLevel.Warning) return false;
            var p = (last ?? "").Split('|');
            DateTime when; HealthLevel was;
            if (p.Length != 2 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out when) || !LevelFrom(p[1], out was)) return true;
            if (now > was) return true;
            return (today.Date - when.Date).TotalDays >= (now == HealthLevel.Failing ? 1 : 7);
        }
        static bool LevelFrom(string s, out HealthLevel l)
        {
            foreach (HealthLevel x in new[] { HealthLevel.Unknown, HealthLevel.Healthy, HealthLevel.Warning, HealthLevel.Failing })
                if (x.ToString() == s.Trim()) { l = x; return true; }
            l = HealthLevel.Unknown; return false;
        }
        public static string Stamp(HealthLevel l, DateTime today) { return today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + l; }
    }
}
