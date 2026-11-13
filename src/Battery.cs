// Battery health (laptops): how much charge the battery still holds compared with when it was new, from Windows' own
// battery report (powercfg /batteryreport). Read-only. The report is written into Elixar's protected data folder;
// one reading a month is kept so PC health can show the trend. A PC without a battery shows nothing.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace CDriveCleaner
{
    public class BatteryInfo
    {
        public long DesignMWh, FullMWh; public int Cycles = -1; public int Count;
        public int Percent { get { return DesignMWh > 0 ? (int)Math.Round(100.0 * FullMWh / DesignMWh) : -1; } }
    }

    public static class Battery
    {
        public static Func<string> ReportOverride;            // self-test: the XML report
        public static string HistoryFileOverride;
        static string HistoryFile { get { return HistoryFileOverride ?? Paths.FileIn("battery-history.txt"); } }
        static string Folder { get { return Path.Combine(Paths.Data, "battery"); } }

        // null: no battery, or Windows didn't say
        public static BatteryInfo Read()
        {
            string xml = ReportOverride != null ? ReportOverride() : Report(true);
            return Parse(xml);
        }

        // powercfg writes the report into a folder only administrators can change (never a folder other programs can)
        static string Report(bool asXml)
        {
            if (!Win.IsWindows) return null;
            try
            {
                if (!Locked.Lock(Folder, true)) return null;
                string f = Path.Combine(Folder, asXml ? "battery-report.xml" : "battery-report.html");
                try { if (File.Exists(f)) SafeDelete.Delete(f); } catch { }
                Sys.Run("powercfg.exe", "/batteryreport " + (asXml ? "/xml " : "") + "/output " + Sys.ArgQuote(f), 60000);
                if (!File.Exists(f) || !SafeDelete.RealPathMatches(f)) return null;
                return asXml ? File.ReadAllText(f) : f;
            }
            catch { return null; }
        }

        // the full report, opened in your browser with your normal rights
        public static string OpenFullReport()
        {
            string f = Report(false);
            if (f == null) return Lang.Hebrew ? "Windows לא יצר את הדוח." : "Windows didn't make the report.";
            Shell.Open(f);
            return null;
        }

        public static BatteryInfo Parse(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            try
            {
                var doc = new XmlDocument { XmlResolver = null };   // no outside entities
                using (var sr = new StringReader(xml))
                using (var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    doc.Load(xr);
                var b = new BatteryInfo();
                foreach (XmlNode n in doc.GetElementsByTagName("Battery"))
                {
                    long d = Num(n, "DesignCapacity"), full = Num(n, "FullChargeCapacity"); long c = Num(n, "CycleCount");
                    if (d <= 0 || full <= 0) continue;
                    b.DesignMWh += d; b.FullMWh += full; b.Count++;
                    if (c >= 0) b.Cycles = Math.Max(0, b.Cycles) + (int)c;
                }
                return b.Count == 0 ? null : b;
            }
            catch { return null; }
        }
        static long Num(XmlNode n, string name)
        {
            foreach (XmlNode c in n.ChildNodes)
            {
                long v;
                if (c.LocalName == name && long.TryParse(c.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            }
            return -1;
        }

        // one reading a month
        public static void Remember(BatteryInfo b, DateTime now)
        {
            if (b == null || b.Percent < 0) return;
            try
            {
                var lines = History();
                string month = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (lines.Any(l => l.Key == month)) return;
                var all = lines.Select(l => l.Key + "\t" + l.Value).ToList();
                all.Add(month + "\t" + b.Percent.ToString(CultureInfo.InvariantCulture));
                SafeFile.Save(HistoryFile, string.Join("\n", all.Skip(Math.Max(0, all.Count - 60))));
            }
            catch { }
        }
        public static List<KeyValuePair<string, int>> History()
        {
            var l = new List<KeyValuePair<string, int>>();
            try
            {
                if (!File.Exists(HistoryFile)) return l;
                foreach (var line in File.ReadAllLines(HistoryFile))
                {
                    var p = line.Split('\t'); int v;
                    if (p.Length == 2 && p[0].Length == 7 && int.TryParse(p[1], out v) && v >= 0 && v <= 150) l.Add(new KeyValuePair<string, int>(p[0], v));
                }
            }
            catch { }
            return l;
        }

        // the change over about six months ("down 4 points"), or null
        public static int? Trend(List<KeyValuePair<string, int>> h, DateTime now, int percent)
        {
            string then = now.AddMonths(-6).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var old = h.Where(x => string.CompareOrdinal(x.Key, then) <= 0).OrderBy(x => x.Key).LastOrDefault();
            if (old.Key == null) return null;
            return percent - old.Value;
        }

        public static HFind Finding(BatteryInfo b, List<KeyValuePair<string, int>> history, DateTime now)
        {
            if (b == null || b.Percent < 0) return null;
            int p = Math.Min(100, b.Percent);
            var lvl = p >= 80 ? HLevel.Ok : (p >= 60 ? HLevel.Info : HLevel.Warn);
            int? t = Trend(history, now, b.Percent);
            string wh = (b.FullMWh / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " of " + (b.DesignMWh / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Wh";
            string whHe = (b.FullMWh / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " מתוך " + (b.DesignMWh / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Wh";
            string en = wh + (b.Cycles >= 0 ? ", " + b.Cycles + " charge cycles" : "") +
                        (t.HasValue && t.Value < 0 ? ", down " + (-t.Value) + " points in the last 6 months" : "") + ". " +
                        (p >= 80 ? "Good." : p >= 60 ? "Wearing, still fine for daily use; under 60% it lasts noticeably shorter." : "It lasts noticeably shorter than when new: replacing the battery is worth a look.");
            string he = whHe + (b.Cycles >= 0 ? ", " + b.Cycles + " מחזורי טעינה" : "") +
                        (t.HasValue && t.Value < 0 ? ", ירידה של " + (-t.Value) + " נקודות ב-6 החודשים האחרונים" : "") + ". " +
                        (p >= 80 ? "טוב." : p >= 60 ? "נשחקת, עדיין בסדר לשימוש יומיומי; מתחת ל-60% היא מחזיקה פחות באופן מורגש." : "היא מחזיקה פחות באופן מורגש מאשר כשהייתה חדשה: כדאי לשקול להחליף את הסוללה.");
            return new HFind
            {
                Area = "Stability", Key = "battery", Level = lvl, Penalty = lvl == HLevel.Warn ? 5 : 0, CanIgnore = lvl != HLevel.Ok,
                Act = HAct.Battery, Button = "Open the full battery report",
                Title = Lang.Hebrew ? "הסוללה מחזיקה " + p + "% מהטעינה המקורית" : "Battery holds " + p + "% of its original charge",
                Detail = Lang.Hebrew ? he : en
            };
        }
    }
}
