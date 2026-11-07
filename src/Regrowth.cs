// Smart cleaning: how fast each cleaned item fills up again, learned on this PC from Elixar's own scans.
// After a clean Elixar remembers how much it removed; each later scan says how big the item is again. Something that
// is back in a day or two (a browser's cache) isn't worth cleaning often: it only makes the app slower until it has
// refilled. The weekly clean leaves those out; the Cleaner shows "Comes back" for every item.
// Only sizes and times are kept, in Elixar's own folder; nothing about what's inside.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CDriveCleaner
{
    public class RegrowthRecord
    {
        public string Name; public DateTime CleanedAt; public long Cleaned;
        public DateTime? LastAt; public long LastBytes;
        public double Days = -1;   // learned: days until it's back to 80% of what was cleaned (-1 = not known yet)
    }

    public static class Regrowth
    {
        public static string OverrideFile;   // self-test
        static string File_ { get { return OverrideFile ?? Paths.FileIn("regrowth.txt"); } }
        static readonly object gate = new object();
        public const double FastDays = 3, SlowDays = 14;
        const long MinBytes = 1L << 20;   // under 1 MB there is nothing to learn

        public static Dictionary<string, RegrowthRecord> Load()
        {
            var d = new Dictionary<string, RegrowthRecord>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(File_) || new FileInfo(File_).Length > 1 << 20) return d;
                foreach (var line in File.ReadAllLines(File_))
                {
                    var p = line.Split('\t'); if (p.Length < 6) continue;
                    DateTime c; long cb, lb; double days;
                    if (!DateTime.TryParseExact(p[1], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out c) || !long.TryParse(p[2], out cb) ||
                        !double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out days) || !long.TryParse(p[5], out lb)) continue;
                    DateTime l; DateTime? last = DateTime.TryParseExact(p[4], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out l) ? l : (DateTime?)null;
                    d[p[0]] = new RegrowthRecord { Name = p[0], CleanedAt = c, Cleaned = cb, Days = days, LastAt = last, LastBytes = lb };
                }
            }
            catch { }
            return d;
        }

        static void Save(Dictionary<string, RegrowthRecord> d)
        {
            try
            {
                File.WriteAllLines(File_, d.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(r => string.Join("\t", new[] {
                    r.Name.Replace("\t", " "), r.CleanedAt.ToString("s", CultureInfo.InvariantCulture), r.Cleaned.ToString(CultureInfo.InvariantCulture),
                    r.Days.ToString("0.###", CultureInfo.InvariantCulture), r.LastAt.HasValue ? r.LastAt.Value.ToString("s", CultureInfo.InvariantCulture) : "",
                    r.LastBytes.ToString(CultureInfo.InvariantCulture) })));
            }
            catch { }
        }

        // what the last scan after a clean says: how many days until it's back to 80% (or -1 when it can't tell yet)
        public static double Estimate(RegrowthRecord r)
        {
            if (r == null || !r.LastAt.HasValue || r.Cleaned < MinBytes) return -1;
            double elapsed = (r.LastAt.Value - r.CleanedAt).TotalDays;
            if (elapsed < 0.5) return -1;
            double target = r.Cleaned * 0.8;
            if (r.LastBytes >= target) return Math.Max(0.25, elapsed * target / Math.Max(1, r.LastBytes));   // back already: when it got there, about
            if (elapsed >= SlowDays) return r.LastBytes < MinBytes ? 999 : Math.Max(SlowDays, elapsed * target / r.LastBytes);   // weeks and not back: slow
            return -1;   // still coming back: wait for more scans
        }

        public static void Cleaned(string name, long bytes, DateTime at)
        {
            if (string.IsNullOrEmpty(name) || bytes < MinBytes) return;
            try
            {
                lock (gate)
                {
                    var d = Load(); RegrowthRecord r;
                    double learned = -1;
                    if (d.TryGetValue(name, out r)) { double e = Estimate(r); learned = e >= 0 ? (r.Days >= 0 ? (r.Days + e) / 2 : e) : r.Days; }   // each round sharpens it
                    d[name] = new RegrowthRecord { Name = name, CleanedAt = at, Cleaned = bytes, Days = learned };
                    Save(d);
                }
            }
            catch { }
        }

        // a scan: the size of every item now
        public static void Observe(IEnumerable<KeyValuePair<string, long>> sizes, DateTime at)
        {
            try
            {
                lock (gate)
                {
                    var d = Load(); bool changed = false;
                    foreach (var kv in sizes)
                    {
                        RegrowthRecord r;
                        if (!d.TryGetValue(kv.Key, out r) || (at - r.CleanedAt).TotalHours < 12 || kv.Value < 0) continue;
                        r.LastAt = at; r.LastBytes = kv.Value; changed = true;
                    }
                    if (changed) Save(d);
                }
            }
            catch { }
        }

        // the best guess so far: from the scans since the last clean, or what earlier rounds taught
        public static double DaysFor(RegrowthRecord r)
        {
            if (r == null) return -1;
            double e = Estimate(r);
            return e >= 0 ? (r.Days >= 0 ? (r.Days + e) / 2 : e) : r.Days;
        }

        public static bool Fast(Dictionary<string, RegrowthRecord> d, string name)
        {
            RegrowthRecord r; if (d == null || !d.TryGetValue(name, out r)) return false;
            double x = DaysFor(r); return x >= 0 && x <= FastDays;
        }

        // the "Comes back" column
        public static string Label(Dictionary<string, RegrowthRecord> d, string name, DateTime now)
        {
            RegrowthRecord r;
            if (d == null || !d.TryGetValue(name, out r)) return Lang.Hebrew ? "עוד לא נוקה" : "not cleaned yet";
            double x = DaysFor(r);
            if (x < 0) return Lang.Hebrew ? "נמדד..." : "measuring...";
            if (x > SlowDays) return Lang.Hebrew ? "לאט (שבועות)" : "slowly (weeks)";
            int n = Math.Max(1, (int)Math.Round(x));
            return Lang.Hebrew ? (n == 1 ? "תוך יום" : "תוך ~" + n + " ימים") : (n == 1 ? "in ~1 day" : "in ~" + n + " days");
        }

        public static string Advice(string itemName)
        {
            return Lang.Hebrew ? itemName + " חוזר תוך יום-יומיים. ניקוי תכוף רק מאט את האפליקציה עד שהוא מתמלא שוב, ולכן הניקוי השבועי מדלג עליו."
                               : itemName + " comes back in a day or two. Cleaning it often only makes the app slower until it refills, so the weekly clean leaves it out.";
        }
    }
}
