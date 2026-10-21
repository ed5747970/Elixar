// Drivers in the Windows DriverStore that this PC does not need:
//   - old versions (mostly old NVIDIA/AMD/Intel graphics drivers)
//   - drivers that don't fit this Windows (built for 32-bit or ARM Windows)
//   - drivers for hardware that is not in this PC (no device, present or remembered, uses them)
//   - drivers for hardware that is not connected now (your decision)
// Never touches a driver a device uses, a boot-critical driver, printer drivers, or extension/software-component drivers.
// Each one is backed up into Undo, then removed with Windows' own pnputil.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public enum DrvKind { Kept, Old, NotForWindows, NoHardware, NotConnected, Locked }

    public class DriverPkg
    {
        public string Inf = "", Original = "", OriginalName = "", Provider = "", Class = "", Version = "";
        public DateTime Date;
        public bool BootCritical, InUse, Old;
        public string Devices = "";              // names of the devices in this PC that use it now
        public bool Remembered;                  // some device (connected or not) is set up with it
        public string Arch = "";                 // amd64, x86, arm64 ... from the package folder name
        public DrvKind Kind = DrvKind.Kept;
        public long Bytes = -1;
        public string Group { get { return (OriginalName + "|" + Provider + "|" + Class).ToLowerInvariant(); } }
        public string Folder { get { try { return Path.GetDirectoryName(Original); } catch { return ""; } } }
        public string Why = "";
        public bool Removable { get { return Kind == DrvKind.Old || Kind == DrvKind.NotForWindows || Kind == DrvKind.NoHardware || Kind == DrvKind.NotConnected; } }
        public bool Suggested { get { return Kind == DrvKind.Old || Kind == DrvKind.NotForWindows || Kind == DrvKind.NoHardware; } }
    }

    public static class Drivers
    {
        // Get-WindowsDriver -Online lists third-party driver packages; Win32_PnPSignedDriver says which devices use them now.
        // Lines starting with HW describe this PC's hardware.
        public const string ListScript =
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ErrorActionPreference = 'SilentlyContinue'; " +
            // hardware first: it needs no admin rights
            // (double quotes: PowerShell only turns `t into a tab inside double quotes)
            "try { \"HW`tCPU`t\" + ((Get-CimInstance Win32_Processor | Select-Object -First 1).Name) } catch {}; " +
            "try { Get-CimInstance Win32_VideoController | ForEach-Object { \"HW`tGraphics`t\" + $_.Name } } catch {}; " +
            "try { $b = Get-CimInstance Win32_BaseBoard; \"HW`tMotherboard`t\" + $b.Manufacturer + ' ' + $b.Product } catch {}; " +
            "try { Get-CimInstance Win32_DiskDrive | ForEach-Object { \"HW`tDisk`t\" + $_.Model } } catch {}; " +
            "try { Get-CimInstance Win32_NetworkAdapter -Filter 'PhysicalAdapter=True' | ForEach-Object { \"HW`tNetwork`t\" + $_.Name } } catch {}; " +
            // driver packages (Get-WindowsDriver needs admin rights)
            "try { $use = @{}; " +
            "Get-CimInstance Win32_PnPSignedDriver | ForEach-Object { if ($_.InfName) { $k = $_.InfName.ToLower(); $use[$k] = @($use[$k]) + @($_.DeviceName) } }; " +
            "Get-WindowsDriver -Online -ErrorAction Stop | ForEach-Object { $d = @($use[$_.Driver.ToLower()] | Where-Object { $_ }); " +
            "@($_.Driver, $_.OriginalFileName, $_.ProviderName, $_.ClassName, $_.Version, " +
            "$_.Date.ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture), $_.BootCritical, ($d.Count -gt 0), ($d -join '; ')) -join \"`t\" } } catch { 'NOADMIN' }";

        public static List<DriverPkg> Parse(string output)
        {
            var list = new List<DriverPkg>();
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split('\t');
                if (p.Length < 8 || !p[0].StartsWith("oem", StringComparison.OrdinalIgnoreCase)) continue;
                var d = new DriverPkg
                {
                    Inf = p[0].Trim(), Original = p[1].Trim(), Provider = p[2].Trim(), Class = p[3].Trim(), Version = p[4].Trim(),
                    BootCritical = p[6].Trim().Equals("True", StringComparison.OrdinalIgnoreCase),
                    InUse = p[7].Trim().Equals("True", StringComparison.OrdinalIgnoreCase),
                    Devices = p.Length > 8 ? p[8].Trim() : ""
                };
                int cut = d.Original.LastIndexOfAny(new[] { '\\', '/' });
                d.OriginalName = cut >= 0 ? d.Original.Substring(cut + 1) : d.Original;
                d.Arch = ArchOf(d.Original);
                DateTime dt;
                if (DateTime.TryParseExact(p[5].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) d.Date = dt;
                list.Add(d);
            }
            return list;
        }

        // "HW <tab> Graphics <tab> NVIDIA GeForce RTX 3070" -> ("Graphics", "NVIDIA GeForce RTX 3070")
        public static List<KeyValuePair<string, string>> ParseHardware(string output)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var p = raw.TrimEnd('\r').Replace("`t", "\t").Split('\t');
                if (p.Length >= 3 && p[0].Trim() == "HW" && p[2].Trim().Length > 0) list.Add(new KeyValuePair<string, string>(p[1].Trim(), p[2].Trim()));
            }
            return list;
        }

        public static bool SafeInf(string inf) { return Regex.IsMatch(inf ?? "", @"^oem\d{1,6}\.inf$", RegexOptions.IgnoreCase); }

        static readonly Regex ArchRe = new Regex(@"\.inf_(amd64|x86|arm64|arm|ia64)_", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string ArchOf(string original)
        {
            var m = ArchRe.Match(original ?? "");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
        }

        public static string OsArch()
        {
            string a = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
            a = a.ToUpperInvariant();
            if (a == "AMD64") return "amd64";
            if (a == "ARM64") return "arm64";
            if (a == "X86") return "x86";
            return Environment.Is64BitOperatingSystem ? "amd64" : "x86";
        }

        static Version V(string s)
        {
            Version v;
            return Version.TryParse(s, out v) ? v : new Version(0, 0);
        }

        // Marks every version except the newest of each driver as Old; never in-use or boot-critical ones.
        public static void MarkOld(List<DriverPkg> all)
        {
            foreach (var g in all.GroupBy(d => d.Group))
            {
                var ordered = g.OrderByDescending(d => V(d.Version)).ThenByDescending(d => d.Date).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var d = ordered[i];
                    d.Old = false;
                    if (i == 0) { d.Why = "newest - kept"; continue; }
                    if (d.InUse) { d.Why = "a device uses it - kept"; continue; }
                    if (d.BootCritical) { d.Why = "needed to start Windows - kept"; continue; }
                    d.Old = true;
                    d.Why = "older version of " + ordered[0].Version;
                }
            }
        }

        static readonly string[] IndirectClasses = { "Printer", "PrintQueue", "Extension", "SoftwareComponent" };
        public static readonly DateTime Windows10Release = new DateTime(2015, 7, 29);

        // Decides, for every package, whether it fits this PC. Call after MarkOld.
        public static void Classify(List<DriverPkg> all, HashSet<string> rememberedInfs, string osArch)
        {
            foreach (var d in all)
            {
                d.Remembered = rememberedInfs != null && rememberedInfs.Contains(d.Inf.ToLowerInvariant());
                if (d.BootCritical) { d.Kind = DrvKind.Locked; d.Why = "needed to start Windows - kept"; continue; }
                if (d.InUse) { d.Kind = DrvKind.Kept; d.Why = "used by " + (d.Devices.Length > 0 ? d.Devices : "a device in this PC") + " - kept"; continue; }
                if (IndirectClasses.Any(c => c.Equals(d.Class, StringComparison.OrdinalIgnoreCase)))
                { d.Kind = DrvKind.Locked; d.Why = (d.Class.StartsWith("Print", StringComparison.OrdinalIgnoreCase) ? "printer driver" : "Windows attaches these indirectly") + " - kept"; continue; }
                if (d.Arch.Length > 0 && osArch.Length > 0 && d.Arch != osArch)
                { d.Kind = DrvKind.NotForWindows; d.Why = "built for " + ArchName(d.Arch) + " Windows; this PC runs " + ArchName(osArch); continue; }
                if (d.Old) { d.Kind = DrvKind.Old; continue; }
                if (!d.Remembered)
                {
                    d.Kind = DrvKind.NoHardware;
                    d.Why = d.Date > DateTime.MinValue && d.Date < Windows10Release
                        ? "made for Windows 7/8 (" + d.Date.Year + ") and no hardware in this PC uses it"
                        : "no hardware in this PC uses it";
                    continue;
                }
                d.Kind = DrvKind.NotConnected;
                d.Why = "device not plugged in now - tick it if you no longer use it";
            }
        }

        public static string ArchName(string a)
        {
            switch (a) { case "amd64": return "64-bit"; case "x86": return "32-bit"; case "arm64": return "ARM64"; case "arm": return "ARM"; case "ia64": return "Itanium"; default: return a; }
        }

        // Every device Windows has ever set up (connected or not) points at its driver package through the registry.
        public static HashSet<string> RememberedInfs()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return set;
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var en = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum"))
                using (var cls = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class"))
                {
                    if (en == null || cls == null) return set;
                    foreach (var bus in en.GetSubKeyNames())
                        using (var b = Open(en, bus))
                            if (b != null)
                                foreach (var dev in b.GetSubKeyNames())
                                    using (var dk = Open(b, dev))
                                        if (dk != null)
                                            foreach (var inst in dk.GetSubKeyNames())
                                                using (var ik = Open(dk, inst))
                                                {
                                                    var drv = ik == null ? null : ik.GetValue("Driver") as string;
                                                    if (string.IsNullOrEmpty(drv)) continue;
                                                    using (var ck = Open(cls, drv))
                                                    {
                                                        var inf = ck == null ? null : ck.GetValue("InfPath") as string;
                                                        if (!string.IsNullOrEmpty(inf)) set.Add(inf.ToLowerInvariant());
                                                    }
                                                }
                }
            }
            catch { }
            return set;
        }

        static RegistryKey Open(RegistryKey parent, string name)
        {
            try { return parent.OpenSubKey(name); } catch { return null; }
        }

        public static List<KeyValuePair<string, string>> Hardware = new List<KeyValuePair<string, string>>();

        public static List<DriverPkg> Read()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return new List<DriverPkg>();
            string outp = StoreData.RunPs(ListScript, 240000);
            Hardware = ParseHardware(outp);
            var list = Parse(outp);
            MarkOld(list);
            Classify(list, RememberedInfs(), OsArch());
            foreach (var d in list)
            {
                try { if (d.Folder.Length > 3 && Directory.Exists(d.Folder)) d.Bytes = Fs.SizeOf(d.Folder); } catch { }
            }
            return list;
        }

        public static int Pnputil(string args, out string output)
        {
            output = "";
            try
            {
                // 64-bit pnputil even if something starts us as 32-bit
                string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32", "pnputil.exe");
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(5 * 60 * 1000)) { try { p.Kill(); } catch { } output = "timed out"; ChangeLog.Command(exe, args, output, -1); return -1; }
                    output = (o.Result + " " + e.Result).Trim();
                    if (output.Length > 300) output = output.Substring(output.Length - 300);
                    ChangeLog.Command(exe, args, output, p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        // Backs each driver up into one Undo entry, then removes it. Returns how many were removed.
        public static int Remove(List<DriverPkg> pkgs, Action<string> log)
        {
            pkgs = pkgs.Where(d => d.Removable && !d.InUse && !d.BootCritical).ToList();
            if (pkgs.Count == 0) return 0;
            var b = Quarantine.Begin("Drivers (" + pkgs.Count + ")", "drivers", pkgs.Sum(d => Math.Max(0, d.Bytes)));
            int removed = 0;
            foreach (var d in pkgs)
            {
                // only Windows' own "oemNN.inf" names go on pnputil's command line
                if (!SafeInf(d.Inf)) { log("  Skipped " + d.Inf + ": not a driver package name Elixar expects."); continue; }
                string backup = Path.Combine(b.Dir, d.Inf);
                Directory.CreateDirectory(backup);
                string outp;
                int code = Pnputil("/export-driver " + d.Inf + " \"" + backup + "\"", out outp);
                if (code != 0 || Directory.GetFiles(backup, "*.inf").Length == 0)
                {
                    log("  Skipped " + d.Provider + " " + d.Version + " (" + d.Inf + "): the backup failed, so it was not removed.");
                    try { Directory.Delete(backup, true); } catch { }
                    continue;
                }
                code = Pnputil("/delete-driver " + d.Inf, out outp);
                // a driver for unplugged hardware is still linked to that (absent) device: unlink it too
                if (code != 0 && code != 3010 && d.Kind == DrvKind.NotConnected) code = Pnputil("/delete-driver " + d.Inf + " /uninstall", out outp);
                if (code == 0 || code == 3010)
                {
                    removed++;
                    b.Items.Add(new[] { d.Inf, d.Provider + " " + d.Class + " " + d.Version + " (" + d.OriginalName + ")", Math.Max(0, d.Bytes).ToString(CultureInfo.InvariantCulture) });
                    log("  Removed " + d.Provider + " " + d.Version + " (" + d.Inf + ") - " + d.Why);
                }
                else
                {
                    log("  Windows kept " + d.Provider + " " + d.Version + " (" + d.Inf + "): " + outp);
                    try { Directory.Delete(backup, true); } catch { }
                }
            }
            if (b.Items.Count > 0) Quarantine.Commit(b);
            else Quarantine.Delete(b);
            return removed;
        }
    }
}
