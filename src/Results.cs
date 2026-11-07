// "Did it help?": what Elixar changed on this PC, measured, from what Elixar already keeps here:
//   - space freed (Savings: every clean writes how much it freed)
//   - how long Windows takes to start, before and after you turned start-up apps off (Windows' own boot times,
//     kept here so the "before" is still there weeks later) and which apps you turned off (the change log)
//   - the PC health score, one reading a day
// It only reads and adds up; nothing leaves the PC.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CDriveCleaner
{
    public class ResultsSummary
    {
        public long Freed, FreedMonth; public DateTime? Since; public int Cleans;
        public int StartupOff; public DateTime? StartupAt;
        public double BootBefore = -1, BootAfter = -1;   // seconds, average of up to 5 starts each side
        public int HealthFirst = -1, HealthNow = -1; public DateTime? HealthSince;

        public double Faster { get { return BootBefore > 0 && BootAfter > 0 ? BootBefore - BootAfter : 0; } }
        public bool ShowFaster { get { return StartupOff > 0 && Faster >= 2; } }   // under 2 s is noise from one start to the next
        public int HealthGain { get { return HealthFirst >= 0 && HealthNow >= 0 ? HealthNow - HealthFirst : 0; } }
        public bool Any { get { return Freed > 0 || ShowFaster || HealthGain >= 3; } }
    }

    public static class Results
    {
        public static string BootFileOverride, HealthFileOverride;   // self-test
        static string BootFile { get { return BootFileOverride ?? Paths.FileIn("boot-history.txt"); } }
        static string HealthFile { get { return HealthFileOverride ?? Paths.FileIn("health-history.txt"); } }
        static readonly object gate = new object();
        const int KeepBoots = 300, KeepDays = 800;

        // ---- recording ----
        // Windows keeps only its last few boot times; every one Elixar sees is kept here (by its time, so never twice)
        public static void RecordBoots(IEnumerable<BootTime> boots)
        {
            if (boots == null) return;
            try
            {
                lock (gate)
                {
                    var all = ReadBoots();
                    var seen = new HashSet<string>(all.Select(b => Key(b.When)));
                    bool added = false;
                    foreach (var b in boots.Where(x => x.Ms > 0 && x.Ms < 30 * 60 * 1000))
                        if (seen.Add(Key(b.When))) { all.Add(new BootTime { When = b.When, Ms = b.Ms, MainMs = b.MainMs, AfterMs = b.AfterMs }); added = true; }
                    if (!added) return;
                    var keep = all.OrderBy(b => b.When).Skip(Math.Max(0, all.Count - KeepBoots));
                    File.WriteAllLines(BootFile, keep.Select(b => b.When.ToString("s", CultureInfo.InvariantCulture) + "\t" + b.Ms.ToString(CultureInfo.InvariantCulture)));
                }
            }
            catch { }
        }
        static string Key(DateTime t) { return t.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture); }

        public static List<BootTime> ReadBoots()
        {
            var l = new List<BootTime>();
            try
            {
                if (!File.Exists(BootFile) || new FileInfo(BootFile).Length > 1 << 20) return l;
                foreach (var line in File.ReadAllLines(BootFile))
                {
                    var p = line.Split('\t'); DateTime t; long ms;
                    if (p.Length >= 2 && DateTime.TryParseExact(p[0], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms) && ms > 0)
                        l.Add(new BootTime { When = t, Ms = ms });
                }
            }
            catch { }
            return l;
        }

        // the PC health score, one reading a day (the newest of the day wins)
        public static void RecordHealth(int score, DateTime at)
        {
            if (score < 0 || score > 100) return;
            try
            {
                lock (gate)
                {
                    var all = ReadHealth().Where(h => h.Item1.Date != at.Date).ToList();
                    all.Add(Tuple.Create(at.Date, score));
                    var keep = all.OrderBy(h => h.Item1).Skip(Math.Max(0, all.Count - KeepDays));
                    File.WriteAllLines(HealthFile, keep.Select(h => h.Item1.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + h.Item2.ToString(CultureInfo.InvariantCulture)));
                }
            }
            catch { }
        }

        public static List<Tuple<DateTime, int>> ReadHealth()
        {
            var l = new List<Tuple<DateTime, int>>();
            try
            {
                if (!File.Exists(HealthFile) || new FileInfo(HealthFile).Length > 1 << 20) return l;
                foreach (var line in File.ReadAllLines(HealthFile))
                {
                    var p = line.Split('\t'); DateTime d; int s;
                    if (p.Length >= 2 && DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && int.TryParse(p[1], out s) && s >= 0 && s <= 100)
                        l.Add(Tuple.Create(d, s));
                }
            }
            catch { }
            return l;
        }

        // ---- adding up ----
        public static ResultsSummary Read()
        {
            SavingsTotal sv = null; List<ChangeLog.Entry> log = null;
            try { sv = Savings.Read(); } catch { }
            try { log = ChangeLog.Read(); } catch { }
            return Compute(sv, log, ReadBoots(), ReadHealth(), DateTime.Now);
        }

        public static ResultsSummary Compute(SavingsTotal sv, List<ChangeLog.Entry> log, List<BootTime> boots, List<Tuple<DateTime, int>> health, DateTime now)
        {
            var r = new ResultsSummary();
            if (sv != null) { r.Freed = sv.Bytes; r.FreedMonth = sv.ThisMonth; r.Since = sv.Since; r.Cleans = sv.Cleans; }

            // start-up apps you turned off (and didn't turn back on), in the last 6 months
            var state = new Dictionary<string, Tuple<bool, DateTime>>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in (log ?? new List<ChangeLog.Entry>()).Where(x => !x.Failed && x.Detail != null && x.Detail.StartsWith("Startup: turned ")))
            {
                DateTime t; if (!DateTime.TryParse(e.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) continue;
                bool off = e.Detail.StartsWith("Startup: turned off ");
                string name = e.Detail.Substring(off ? "Startup: turned off ".Length : "Startup: turned on ".Length).Trim();
                if (name.Length == 0) continue;
                Tuple<bool, DateTime> was;
                if (!state.TryGetValue(name, out was) || was.Item2 <= t) state[name] = Tuple.Create(off, t);
            }
            var offNow = state.Values.Where(v => v.Item1 && (now - v.Item2).TotalDays <= 183).ToList();
            r.StartupOff = offNow.Count;
            if (offNow.Count > 0)
            {
                var at = offNow.Min(v => v.Item2); r.StartupAt = at;
                var b = (boots ?? new List<BootTime>()).Where(x => x.Ms > 0).ToList();
                var before = b.Where(x => x.When < at).OrderByDescending(x => x.When).Take(5).ToList();
                var after = b.Where(x => x.When > at).OrderByDescending(x => x.When).Take(5).ToList();
                if (before.Count >= 2 && after.Count >= 2)
                {
                    r.BootBefore = before.Average(x => x.Ms) / 1000.0;
                    r.BootAfter = after.Average(x => x.Ms) / 1000.0;
                }
            }

            var h = (health ?? new List<Tuple<DateTime, int>>()).OrderBy(x => x.Item1).ToList();
            if (h.Count >= 2) { r.HealthFirst = h[0].Item2; r.HealthNow = h[h.Count - 1].Item2; r.HealthSince = h[0].Item1; }
            return r;
        }

        // ---- words ----
        static string Month(DateTime d, DateTime now)
        {
            if (Lang.Hebrew) return new CultureInfo("he-IL").DateTimeFormat.GetMonthName(d.Month) + (d.Year != now.Year ? " " + d.Year : "");
            return CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(d.Month) + (d.Year != now.Year ? " " + d.Year : "");
        }

        // the big line: "23.4 GB freed since June"
        public static string Headline(ResultsSummary r, DateTime now)
        {
            if (r.Freed <= 0) return Lang.Hebrew ? "עוד לא נמדד" : "Nothing yet";
            string since = r.Since.HasValue ? (Lang.Hebrew ? " מאז " : " since ") + Month(r.Since.Value, now) : "";
            return Lang.Hebrew ? "פונו " + Fs.Fmt(r.Freed) + since : Fs.Fmt(r.Freed) + " freed" + since;   // Hebrew starts with a Hebrew word: right to left on any Windows
        }

        // the rest, each part only when there is something real to say
        public static List<string> Details(ResultsSummary r)
        {
            var l = new List<string>();
            if (r.FreedMonth > 0 && r.FreedMonth < r.Freed) l.Add(Lang.Hebrew ? "החודש: " + Fs.Fmt(r.FreedMonth) : "This month: " + Fs.Fmt(r.FreedMonth));
            if (r.ShowFaster)
            {
                int s = (int)Math.Round(r.Faster);
                l.Add(Lang.Hebrew ? "Windows עולה מהר יותר ב-" + s + " שניות מאז שכיבית " + (r.StartupOff == 1 ? "אפליקציה אחת" : r.StartupOff + " אפליקציות") + " בהפעלה"
                                  : "Windows starts " + s + " s faster since you turned off " + (r.StartupOff == 1 ? "1 start-up app" : r.StartupOff + " start-up apps"));
            }
            if (r.HealthGain >= 3) l.Add((Lang.Hebrew ? "בריאות המחשב " : "PC health ") + r.HealthFirst + " → " + r.HealthNow);
            return l;
        }

        public static string Line(ResultsSummary r, DateTime now)
        {
            if (!r.Any) return "";
            var parts = new List<string>();
            if (r.Freed > 0) parts.Add(Headline(r, now));
            parts.AddRange(Details(r));
            return string.Join(" · ", parts);
        }

        // after a clean: "Done: 3.1 GB freed. C: went from 12.4 GB to 15.5 GB free."
        public static string AfterClean(long freed, long freeBefore, long freeAfter, string drive)
        {
            if (Lang.Hebrew) return "סיום: פונו " + Fs.Fmt(freed) + ". ב-" + drive + " היו פנויים " + Fs.Fmt(freeBefore) + ", ועכשיו " + Fs.Fmt(freeAfter) + ".";
            return "Done: " + Fs.Fmt(freed) + " freed. " + drive + " went from " + Fs.Fmt(freeBefore) + " to " + Fs.Fmt(freeAfter) + " free.";
        }
    }
}

namespace CDriveCleaner
{
    using System.Drawing;
    using System.Windows.Forms;

    // Dashboard: "What Elixar did for you" (beside PC health on a wide window, under it on a narrow one)
    public class ResultsCard : PaintedCard
    {
        public ResultsSummary Data;
        public Font CapFont, BigFont, SmallFont;
        public ResultsCard() { Cursor = Cursors.Hand; }
        public string Text_ { get { return Data == null ? "" : Results.Line(Data, DateTime.Now); } }
        protected override void OnPaint(PaintEventArgs e) { try { Paint_(e.Graphics); } catch { } }
        void Paint_(Graphics g)
        {
            g.Clear(Under);
            if (Width < S(120) || Height < S(40)) return;
            var card = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Theme.Panel)) g.FillRectangle(b, card);
            using (var p = new Pen(Theme.Grid)) g.DrawRectangle(p, card);
            int x = S(14), w = Width - S(28);
            string cap = Lang.T("What Elixar did for you");
            Txt(g, Lang.On ? cap : cap.ToUpperInvariant(), CapFont, new Rectangle(x, S(10), w, S(16)), Theme.Quiet, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            var d = Data ?? new ResultsSummary();
            Txt(g, Results.Headline(d, DateTime.Now), BigFont, new Rectangle(x, S(26), w, S(28)), d.Freed > 0 ? Theme.Ink : Theme.Quiet, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            var more = Results.Details(d);
            string small = more.Count > 0 ? string.Join(" · ", more) : Lang.Hebrew ? "כל ניקוי נרשם כאן, וגם כמה מהר Windows עולה." : "Every clean is counted here, and how fast Windows starts.";
            int top = S(54);   // two lines fit under the big one
            Txt(g, small, SmallFont, new Rectangle(x, top, w, Math.Max(S(16), Height - top - S(8))), Theme.Quiet, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
    }
}
