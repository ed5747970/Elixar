// How much each startup program slows Windows down, from Windows' own measurements: the Diagnostics-Performance log
// writes an entry each time a program makes startup slower than usual (event 101), and how long each startup took (event 100).
// Read-only. Needs administrator rights, which Elixar has.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CDriveCleaner
{
    public class AppImpact { public string File = "", Friendly = ""; public int Times; public long TotalMs; public DateTime Last; public long AvgMs { get { return Times == 0 ? 0 : TotalMs / Times; } } }
    public class BootTime { public DateTime When; public long Ms, MainMs, AfterMs; }

    public class StartupImpactData
    {
        public List<BootTime> Boots = new List<BootTime>();
        public Dictionary<string, AppImpact> Apps = new Dictionary<string, AppImpact>(StringComparer.OrdinalIgnoreCase);
        public AppImpact For(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string name = file.Substring(file.LastIndexOfAny(new[] { '\\', '/' }) + 1);   // a Windows path, wherever this runs
            AppImpact a; return Apps.TryGetValue(name, out a) ? a : null;
        }
    }

    public static class StartupImpact
    {
        public static Func<string> RunOverride;   // self-test

        const string Log = "Microsoft-Windows-Diagnostics-Performance/Operational";
        public static readonly string Script =
            "$ErrorActionPreference='SilentlyContinue'\n" +
            "function D($e){ $x=[xml]$e.ToXml(); $d=@{}; foreach($n in $x.Event.EventData.Data){ $d[$n.Name]=(''+$n.'#text') -replace '[|\\r\\n]',' ' }; $d }\n" +
            "foreach($e in (Get-WinEvent -FilterHashtable @{LogName='" + Log + "'; Id=100} -MaxEvents 10)){ $d=D $e; 'BOOT|'+$e.TimeCreated.ToString('s')+'|'+$d['BootTime']+'|'+$d['MainPathBootTime']+'|'+$d['BootPostBootTime'] }\n" +
            "foreach($e in (Get-WinEvent -FilterHashtable @{LogName='" + Log + "'; Id=101; StartTime=(Get-Date).AddDays(-90)} -MaxEvents 1000)){ $d=D $e; $f=$d['Name']; if(-not $f){$f=$d['FileName']}; 'APP|'+$e.TimeCreated.ToString('s')+'|'+$f+'|'+$d['FriendlyName']+'|'+$d['DegradationTime'] }\n";

        public static StartupImpactData Read()
        {
            string raw = RunOverride != null ? RunOverride() : (Win.IsWindows ? Sys.RunPowerShell(Script, 60000) : "");
            return Parse(raw);
        }

        public static StartupImpactData Parse(string raw)
        {
            var d = new StartupImpactData();
            foreach (var line in (raw ?? "").Split('\n'))
            {
                var p = line.Trim().Split('|');
                DateTime when;
                if (p.Length < 2 || !DateTime.TryParse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) continue;
                if (p[0] == "BOOT" && p.Length >= 5)
                {
                    long ms, main, after;
                    if (long.TryParse(p[2], out ms) && ms > 0 && ms < 3600000)
                        d.Boots.Add(new BootTime { When = when, Ms = ms, MainMs = long.TryParse(p[3], out main) ? main : 0, AfterMs = long.TryParse(p[4], out after) ? after : 0 });
                }
                else if (p[0] == "APP" && p.Length >= 5)
                {
                    long deg; string file = (p[2] ?? "").Trim(); file = file.Substring(file.LastIndexOfAny(new[] { '\\', '/' }) + 1);
                    if (file.Length == 0 || !long.TryParse(p[4], out deg) || deg < 0 || deg > 3600000) continue;
                    AppImpact a;
                    if (!d.Apps.TryGetValue(file, out a)) d.Apps[file] = a = new AppImpact { File = file, Friendly = p[3].Trim() };
                    a.Times++; a.TotalMs += deg; if (when > a.Last) a.Last = when;
                }
            }
            d.Boots = d.Boots.OrderByDescending(b => b.When).ToList();
            return d;
        }

        public static string Seconds(long ms) { return (ms / 1000.0).ToString(ms < 10000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s"; }

        public static string Words(AppImpact a)
        {
            if (a == null) return "-";
            return "+" + Seconds(a.AvgMs) + (a.Times > 1 ? "  (" + a.Times + " times)" : "");
        }

        public static string Summary(StartupImpactData d)
        {
            if (d == null || d.Boots.Count == 0) return "";
            var last = d.Boots[0];
            long avg = (long)d.Boots.Average(b => b.Ms);
            return "Last startup took " + Seconds(last.Ms) + " (until the desktop: " + Seconds(last.MainMs) + "); the last " + d.Boots.Count + " took " + Seconds(avg) + " on average.";
        }
    }
}
