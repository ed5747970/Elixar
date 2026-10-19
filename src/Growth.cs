// What grew this week: the size of each place on the Dashboard map is written down once a day (on this PC only),
// so Elixar can say "Downloads grew 4.2 GB this week". Only sizes, never file names.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CDriveCleaner
{
    public static class Growth
    {
        public class Change { public string Id = "", Name = ""; public long Bytes, Now; }
        public static string OverrideFile;   // self-test
        static string FileName { get { return OverrideFile ?? Paths.FileIn("growth.txt"); } }

        // one line per place per day: yyyy-MM-dd <tab> id <tab> name <tab> bytes
        public static void Record(IEnumerable<DashLoc> locs, DateTime today)
        {
            try
            {
                string day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var keep = Read().Where(l => l.Item1 != day && (today - DateTime.ParseExact(l.Item1, "yyyy-MM-dd", CultureInfo.InvariantCulture)).TotalDays <= 90)
                                 .Select(l => l.Item1 + "\t" + l.Item2 + "\t" + l.Item3 + "\t" + l.Item4.ToString(CultureInfo.InvariantCulture)).ToList();
                foreach (var d in locs.Where(x => x.OnC && x.Bytes.HasValue && !string.IsNullOrEmpty(x.Id)))
                    keep.Add(day + "\t" + Clean(d.Id) + "\t" + Clean(d.Name) + "\t" + d.Own.ToString(CultureInfo.InvariantCulture));
                File.WriteAllLines(FileName, keep);
            }
            catch { }
        }

        static string Clean(string s) { return (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }

        static List<Tuple<string, string, string, long>> Read()
        {
            var list = new List<Tuple<string, string, string, long>>();
            try
            {
                if (!File.Exists(FileName)) return list;
                foreach (var l in File.ReadAllLines(FileName))
                {
                    var p = l.Split('\t'); long b; DateTime d;
                    if (p.Length == 4 && DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && long.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                        list.Add(Tuple.Create(p[0], p[1], p[2], b));
                }
            }
            catch { }
            return list;
        }

        // grew by at least minBytes since the reading closest to "days" ago (at least 5 days old)
        public static List<Change> Grew(DateTime today, int days, long minBytes)
        {
            var rows = Read();
            if (rows.Count == 0) return new List<Change>();
            Func<string, DateTime> D = s => DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var latestDay = rows.Max(r => D(r.Item1));
            var olderDays = rows.Select(r => D(r.Item1)).Distinct().Where(d => (latestDay - d).TotalDays >= 5).ToList();
            if (olderDays.Count == 0) return new List<Change>();
            var baseDay = olderDays.OrderBy(d => Math.Abs((latestDay - d).TotalDays - days)).First();
            var then = rows.Where(r => D(r.Item1) == baseDay).ToDictionary(r => r.Item2, r => r.Item4);
            var list = new List<Change>();
            foreach (var r in rows.Where(r => D(r.Item1) == latestDay))
            {
                long before;
                if (!then.TryGetValue(r.Item2, out before)) continue;
                if (r.Item4 - before >= minBytes) list.Add(new Change { Id = r.Item2, Name = r.Item3, Bytes = r.Item4 - before, Now = r.Item4 });
            }
            return list.OrderByDescending(c => c.Bytes).ToList();
        }
    }
}
