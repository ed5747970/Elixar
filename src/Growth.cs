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

        // ======================= folders to watch: the places that keep growing =======================
        // From the daily readings: a place "always grows" when it grew in at least 3 of every 4 weeks (and by 256 MB or more),
        // and "grows fast" at 2 GB a month or more. Each one gets how often to look at it, and you can mark it as checked.
        public class Watch
        {
            public string Id = "", Name = "";
            public long Now, PerMonth, Total;
            public int Weeks, GrewWeeks, EveryDays;
            public bool Steady, Fast, Due;
            public DateTime? Checked;
            public DateTime NextCheck;
        }
        public static string OverrideCheckedFile;   // self-test
        static string CheckedFile { get { return OverrideCheckedFile ?? Paths.FileIn("watch-checked.txt"); } }

        // true once there are two weeks of readings: before that nothing can be said about growth
        public static bool HasHistory(DateTime today)
        {
            var days = Read().Select(r => DateTime.ParseExact(r.Item1, "yyyy-MM-dd", CultureInfo.InvariantCulture)).Distinct().ToList();
            return days.Count >= 3 && (days.Max() - days.Min()).TotalDays >= 14;
        }

        public static List<Watch> Watchlist(DateTime today)
        {
            var list = new List<Watch>();
            var checks = ReadChecked();
            Func<string, DateTime> D = x => DateTime.ParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var g in Read().GroupBy(r => r.Item2))
            {
                var pts = g.Select(r => new { Day = D(r.Item1), Bytes = r.Item4, Name = r.Item3 }).Where(p => (today - p.Day).TotalDays <= 90).OrderBy(p => p.Day).ToList();
                if (pts.Count < 3 || (pts.Last().Day - pts.First().Day).TotalDays < 14) continue;
                // one reading per week (the last one of each week), so a PC used every day and one used once a week count alike
                var first = pts.First().Day;
                var weekly = pts.GroupBy(p => (int)((p.Day - first).TotalDays / 7)).OrderBy(wk => wk.Key).Select(wk => wk.Last()).ToList();
                if (weekly.Count < 3) continue;
                int steps = weekly.Count - 1, grew = 0;
                for (int i = 1; i < weekly.Count; i++) if (weekly[i].Bytes - weekly[i - 1].Bytes > (16L << 20)) grew++;
                // the trend: least squares over every reading
                double mx = pts.Average(p => (p.Day - first).TotalDays), my = pts.Average(p => (double)p.Bytes), sxy = 0, sxx = 0;
                foreach (var p in pts) { double dx = (p.Day - first).TotalDays - mx; sxy += dx * (p.Bytes - my); sxx += dx * dx; }
                double perDay = sxx > 0 ? sxy / sxx : 0;
                long perMonth = (long)(perDay * 30), total = pts.Last().Bytes - pts.First().Bytes;
                bool steady = steps >= 2 && grew * 4 >= steps * 3 && total >= (256L << 20) && perMonth > 0;
                bool fast = perMonth >= (2L << 30) && total >= (1L << 30);
                if (!steady && !fast) continue;
                var w = new Watch { Id = g.Key, Name = pts.Last().Name, Now = pts.Last().Bytes, PerMonth = perMonth, Total = total, Weeks = steps, GrewWeeks = grew, Steady = steady, Fast = fast,
                                    EveryDays = perMonth >= (5L << 30) ? 7 : perMonth >= (1L << 30) ? 14 : 30 };
                DateTime c; if (checks.TryGetValue(g.Key, out c)) w.Checked = c;
                w.NextCheck = w.Checked.HasValue ? w.Checked.Value.AddDays(w.EveryDays) : today;
                w.Due = w.NextCheck <= today;
                list.Add(w);
            }
            return list.OrderByDescending(x => x.Due).ThenByDescending(x => x.PerMonth).ToList();
        }

        static Dictionary<string, DateTime> ReadChecked()
        {
            var d = new Dictionary<string, DateTime>();
            try
            {
                if (!File.Exists(CheckedFile)) return d;
                foreach (var l in File.ReadAllLines(CheckedFile))
                {
                    var p = l.Split('\t'); DateTime t;
                    if (p.Length == 2 && DateTime.TryParseExact(p[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) d[p[0]] = t;
                }
            }
            catch { }
            return d;
        }

        // "I checked it": the folder is quiet until its next check is due
        public static void MarkChecked(string id, DateTime today)
        {
            try
            {
                var d = ReadChecked(); d[Clean(id)] = today.Date;
                File.WriteAllLines(CheckedFile, d.Select(kv => kv.Key + "\t" + kv.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            catch { }
        }

        public static string EveryText(int days) { return days <= 7 ? "every week" : days <= 14 ? "every 2 weeks" : "every month"; }

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
