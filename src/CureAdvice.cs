// Elixar for Business: what a PC's report means, in plain words - how much at risk it is and what to clean or remove there.
// Only reads a report; it never does anything on the PC (Cure stage 1 is reports only).

#if BUSINESS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CDriveCleaner
{
    public enum CureRisk { Ok = 0, Old = 1, Watch = 2, Worn = 3, Low = 4, Full = 5, Disk = 6, Silent = 7 }

    public class CureTip { public int Level; public string Text = ""; public long Saves; }   // Level: 0 good to do, 1 should do, 2 do now

    public static class CureAdvice
    {
        public const double FullPercent = 10, LowPercent = 20;
        public const long FullBytes = 10L << 30;

        public static int FreePercent(CureReport r) { return r == null || r.Total <= 0 || r.Free < 0 ? -1 : (int)Math.Round(r.Free * 100.0 / r.Total); }

        // GB lost a day over the history (negative = filling up), from one point a day
        public static double PerDay(List<long[]> history)
        {
            var h = (history ?? new List<long[]>()).Where(x => x.Length > 1 && x[1] >= 0).ToList();
            if (h.Count < 3) return 0;
            h = h.Skip(Math.Max(0, h.Count - 14)).ToList();
            double n = h.Count, sx = 0, sy = 0, sxy = 0, sxx = 0;
            for (int i = 0; i < h.Count; i++) { double y = h[i][1] / (double)(1L << 30); sx += i; sy += y; sxy += i * y; sxx += i * i; }
            double d = n * sxx - sx * sx;
            return d == 0 ? 0 : (n * sxy - sx * sy) / d;
        }

        // days until C: is full at this pace; -1 when it isn't filling up
        public static int DaysToFull(CureReport r, List<long[]> history)
        {
            double pd = PerDay(history);
            if (r == null || r.Free < 0 || pd > -0.05) return -1;
            return (int)Math.Max(0, Math.Floor(r.Free / (double)(1L << 30) / -pd));
        }

        public static CureRisk Risk(CureReport r, DateTime lastSeen, string newest, List<long[]> history)
        {
            if (r == null || (lastSeen != DateTime.MinValue && DateTime.Now - lastSeen > TimeSpan.FromDays(7))) return CureRisk.Silent;
            if (r.DiskLevel == "Failing") return CureRisk.Disk;
            int fp = FreePercent(r);
            if (fp >= 0 && (fp < FullPercent || r.Free < FullBytes)) return CureRisk.Full;
            int dtf = DaysToFull(r, history);
            if (fp >= 0 && fp < LowPercent || dtf >= 0 && dtf <= 30) return CureRisk.Low;
            if (r.DiskLevel == "Warning") return CureRisk.Worn;
            Version a, b;
            if (Version.TryParse(r.Elixar, out a) && Version.TryParse(newest ?? "", out b) && a < b) return CureRisk.Old;
            return CureRisk.Ok;
        }

        public static string RiskText(CureRisk k)
        {
            switch (k)
            {
                case CureRisk.Silent: return "Not seen for 7+ days";
                case CureRisk.Disk: return "Disk failing";
                case CureRisk.Full: return "Almost full";
                case CureRisk.Low: return "Getting full";
                case CureRisk.Worn: return "Disk worn";
                case CureRisk.Old: return "Old Elixar";
                default: return "OK";
            }
        }

        static string Gb(long b) { return Fs.Fmt(b); }
        static string Days(string iso)
        {
            DateTime t; if (!DateTime.TryParse(iso ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return null;
            int d = (int)Math.Floor((DateTime.Now - t).TotalDays);
            return d.ToString(CultureInfo.InvariantCulture);
        }

        // what to do on this PC, the most urgent first
        public static List<CureTip> Tips(CureReport r, List<long[]> history)
        {
            var t = new List<CureTip>();
            if (r == null) return t;
            int fp = FreePercent(r), dtf = DaysToFull(r, history);
            if (r.DiskLevel == "Failing") t.Add(new CureTip { Level = 2, Saves = long.MaxValue, Text = "The disk is failing: back up this PC's files now and replace the disk." });
            if (fp >= 0 && (fp < FullPercent || r.Free < FullBytes))
                t.Add(new CureTip { Level = 2, Saves = long.MaxValue / 2, Text = "C: has only " + Gb(r.Free) + " free (" + fp + "%)" + (dtf >= 0 ? ": at this pace it's full in about " + dtf + " days." : ". Windows and apps slow down and updates fail when it's full.") });
            else if (dtf >= 0 && dtf <= 30) t.Add(new CureTip { Level = 1, Text = "C: fills up about " + Math.Abs(PerDay(history)).ToString("0.0", CultureInfo.InvariantCulture) + " GB a day: full in about " + dtf + " days." });

            // the Safe items: the weekly clean (or the Cleaner on that PC) frees them without asking anything
            var items = (r.Items ?? new CureItem[0]).Where(i => i.Bytes > 0).ToList();
            long safeAuto = items.Where(i => i.Tier == "safe" && i.Auto).Sum(i => i.Bytes);
            if (safeAuto >= (500L << 20))
            {
                var top = items.Where(i => i.Tier == "safe" && i.Auto).OrderByDescending(i => i.Bytes).Take(3).Select(i => i.Name + " " + Gb(i.Bytes));
                t.Add(new CureTip { Level = fp >= 0 && fp < LowPercent ? 2 : 1, Saves = safeAuto,
                                    Text = "Run the Safe clean: frees " + Gb(safeAuto) + " (" + string.Join(", ", top) + ")." + (r.WeeklyClean ? "" : " The weekly clean is off on this PC: turn it on (Policy manager > Weekly clean) so it stays clean.") });
            }
            else if (!r.WeeklyClean && fp >= 0 && fp < LowPercent) t.Add(new CureTip { Level = 1, Text = "The weekly clean is off on this PC: turn it on in Policy manager (Weekly clean)." });

            // big single items worth a look, each with how
            foreach (var i in items.Where(i => !(i.Tier == "safe" && i.Auto) && i.Bytes >= (1L << 30)).OrderByDescending(i => i.Bytes).Take(6))
            {
                string how = i.Name.IndexOf("Windows.old", StringComparison.OrdinalIgnoreCase) >= 0 ? "remove it in Elixar's Cleaner on that PC once the update is fine (it can't be undone)." :
                             i.Name.IndexOf("Recycle Bin", StringComparison.OrdinalIgnoreCase) >= 0 ? "empty it, or set Policy manager > Empty old Recycle Bin items after 30 days." :
                             i.Tier == "safe" ? "Safe to clean (not part of the weekly clean): tick it in the Cleaner on that PC." :
                             i.Tier == "review" ? "check it with the user first (Cleaner on that PC)." :
                             "only with the tool named in Elixar's Cleaner on that PC.";
                t.Add(new CureTip { Level = i.Bytes >= (5L << 30) ? 1 : 0, Saves = i.Bytes, Text = i.Name + ": " + Gb(i.Bytes) + " - " + how });
            }

            // what fills C: that isn't a cleaning item: the user's own files
            foreach (var p in (r.Places ?? new CurePlace[0]).Where(p => p.Tier == "review" && p.Bytes >= (5L << 30)).OrderByDescending(p => p.Bytes).Take(3))
                t.Add(new CureTip { Level = 0, Saves = p.Bytes, Text = p.Name + ": " + Gb(p.Bytes) + " - ask the user to sort it, or move it off C: (Tools > Move off C:)." });

            // apps nobody uses
            foreach (var a in (r.Apps ?? new CureApp[0]).Where(a => a.Bytes >= (1L << 30)))
            {
                string d = Days(a.LastUsed);
                int days; if (d == null || !int.TryParse(d, out days) || days < 90) continue;
                t.Add(new CureTip { Level = 0, Saves = a.Bytes, Text = "Uninstall " + a.Name + " (" + Gb(a.Bytes) + ") if it isn't needed: not used for " + days + " days." });
            }

            if (r.PendingReboot) t.Add(new CureTip { Level = 0, Text = "A restart is waiting: it finishes updates and frees what they leave behind." });
            string lc = Days(r.LastClean);
            int lcd; if (lc == null) t.Add(new CureTip { Level = 0, Text = "This PC was never cleaned with Elixar." });
            else if (int.TryParse(lc, out lcd) && lcd > 30) t.Add(new CureTip { Level = 0, Text = "Last cleaned " + lcd + " days ago." });
            if (r.AppUpdates > 5) t.Add(new CureTip { Level = 0, Text = r.AppUpdates + " apps have updates (Apps > Updates on that PC, or your update tool)." });
            if (r.StartupCount > 15) t.Add(new CureTip { Level = 0, Text = r.StartupCount + " programs start with Windows: turning off the ones not needed makes it start faster (Tools > Startup)." });
            foreach (var d in (r.Disks ?? new CureDisk[0]).Where(d => d.Level == "Warning" || d.Wear >= DiskHealth.WearWarn))
                t.Add(new CureTip { Level = 1, Text = d.Name + ": " + (d.Wear >= 0 ? "wear " + d.Wear + "%" : "needs attention") + " - plan to replace it." });
            return t.OrderByDescending(x => x.Level).ThenByDescending(x => x.Saves).ToList();
        }

        // everything the tips above could free (not counted twice)
        public static long CouldFree(CureReport r)
        {
            if (r == null) return 0;
            var items = (r.Items ?? new CureItem[0]).Where(i => i.Bytes > 0 && i.Tier != "critical");
            return items.Where(i => i.Tier == "safe").Sum(i => i.Bytes) + items.Where(i => i.Tier != "safe" && i.Bytes >= (1L << 30)).Sum(i => i.Bytes);
        }
    }
}
#endif
