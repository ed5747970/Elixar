// Space trend (free space over time + when C: fills up) and the folder map (a treemap of what fills a folder).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    // ============================ free space history ============================
    public static class SpaceHistory
    {
        public class Point { public DateTime Day; public long Free, Total; }

        static string AdminFile { get { return Path.Combine(Paths.Data, "space-history.txt"); } }
        static string UserFile
        {
            get
            {
                string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(string.IsNullOrEmpty(la) ? Path.GetTempPath() : la, "Elixar"), "space-history.txt");
            }
        }
        public static string OverrideFile;   // self-test only

        // one line per day: the newest reading of that day wins
        public static void Record(long free, long total)
        {
            if (total <= 0) return;
            string file = OverrideFile ?? (Sys.IsAdmin() ? AdminFile : UserFile);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\t" + free + "\t" + total + Environment.NewLine);
            }
            catch { }
        }

        public static List<Point> Parse(IEnumerable<string> lines)
        {
            var byDay = new Dictionary<DateTime, Point>();
            foreach (var raw in lines)
            {
                var p = raw.Trim().Split('\t');
                DateTime d; long f, t;
                if (p.Length < 3 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ||
                    !long.TryParse(p[1], out f) || !long.TryParse(p[2], out t)) continue;
                byDay[d] = new Point { Day = d, Free = f, Total = t };
            }
            return byDay.Values.OrderBy(x => x.Day).ToList();
        }

        public static List<Point> Read()
        {
            var lines = new List<string>();
            foreach (var f in OverrideFile != null ? new[] { OverrideFile } : new[] { AdminFile, UserFile })
                try { if (File.Exists(f) && new FileInfo(f).Length < (4L << 20)) lines.AddRange(File.ReadAllLines(f)); } catch { }   // one line a day: never megabytes
            return Parse(lines);
        }

        public class Forecast { public double GbPerMonth; public DateTime? FullOn; public int Days; public string Text = ""; }

        // a straight line through the last 90 days of readings
        public static Forecast Predict(List<Point> pts, DateTime today)
        {
            var fc = new Forecast();
            var recent = pts.Where(p => (today - p.Day).TotalDays <= 90).ToList();
            if (recent.Count < 3 || (recent.Last().Day - recent.First().Day).TotalDays < 3)
            {
                fc.Text = "Not enough history yet: Elixar records free space each day it runs (and with the low-space alert). Check back in a few days.";
                return fc;
            }
            double x0 = recent.First().Day.Ticks;
            var xs = recent.Select(p => (p.Day.Ticks - x0) / (double)TimeSpan.TicksPerDay).ToList();
            var ys = recent.Select(p => p.Free / (double)(1L << 30)).ToList();
            double mx = xs.Average(), my = ys.Average();
            double sxy = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum(), sxx = xs.Sum(x => (x - mx) * (x - mx));
            double slope = sxx == 0 ? 0 : sxy / sxx;   // GB per day
            fc.GbPerMonth = slope * 30; fc.Days = (int)Math.Round(xs.Last());
            double freeNow = ys.Last();
            if (slope < -0.02)
            {
                double daysLeft = freeNow / -slope;
                fc.FullOn = recent.Last().Day.AddDays(daysLeft);
                fc.Text = "C: loses about " + Math.Abs(fc.GbPerMonth).ToString("0.0", CultureInfo.InvariantCulture) + " GB a month. At this pace it is full around " +
                          fc.FullOn.Value.ToString("MMMM yyyy", CultureInfo.InvariantCulture) + ".";
            }
            else fc.Text = slope > 0.02 ? "Free space is growing (" + fc.GbPerMonth.ToString("+0.0", CultureInfo.InvariantCulture) + " GB a month)." : "Free space is steady.";
            return fc;
        }
    }

    public class TrendForm : Form
    {
        readonly List<SpaceHistory.Point> pts;
        readonly SpaceHistory.Forecast fc;
        readonly Label lead;

        public TrendForm(float scale, List<SpaceHistory.Point> points) : this(scale, points, null) { }
        public TrendForm(float scale, List<SpaceHistory.Point> points, List<Growth.Change> grew)
        {
            WindowFit.Watch(this);   // fits the screen (a tall window is made smaller; its page scrolls)
            Func<int, int> S = px => (int)Math.Round(px * scale);
            pts = points; fc = SpaceHistory.Predict(points, DateTime.Today);
            Text = "Space trend - drive C:"; Icon = Brand.Load(32); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f); Size = new Size(S(860), S(500)); MinimumSize = new Size(S(600), S(380));
            BackColor = Theme.Back; ForeColor = Theme.Ink;
            lead = new Label { Dock = DockStyle.Top, Height = S(54), Padding = new Padding(S(12), S(10), S(12), 0), Font = new Font("Segoe UI Semibold", 11f), Text = fc.Text };
            var chart = new Panel { Dock = DockStyle.Fill };
            chart.Paint += (o, e) => Draw(e.Graphics, chart.ClientRectangle, S);
            chart.Resize += (o, e) => chart.Invalidate();
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(chart, true, null);
            Controls.Add(chart); Controls.Add(lead);
            if (grew != null)
            {
                // what grew in the last week (places on the Dashboard map), biggest first
                var lines = grew.Take(5).Select(c => "  \u2022  " + Lang.T(c.Name) + ":  +" + Fs.Fmt(c.Bytes) + "   (" + Lang.T("now") + " " + Fs.Fmt(c.Now) + ")").ToList();
                var box = new Label { Dock = DockStyle.Bottom, AutoSize = false, Height = S(36) + S(20) * Math.Max(1, lines.Count), Padding = new Padding(S(12), S(8), S(12), S(6)),
                    Text = Lang.T("What grew in the last 7 days") + "\n" + (lines.Count == 0 ? "  " + Lang.T("Nothing grew by more than 100 MB (or there aren't a week of readings yet).") : string.Join("\n", lines)) };
                Controls.Add(box);
                Height += box.Height;
            }
        }

        void Draw(Graphics g, Rectangle r, Func<int, int> S)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Back);
            var plot = new Rectangle(r.X + S(64), r.Y + S(16), r.Width - S(96), r.Height - S(64));
            if (pts.Count == 0) { TextRenderer.DrawText(g, "No readings yet.", Font, plot, Theme.Quiet, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
            DateTime d0 = pts.First().Day, d1 = pts.Last().Day;
            if (fc.FullOn.HasValue && fc.FullOn.Value < d1.AddDays(365)) d1 = fc.FullOn.Value; else if (fc.FullOn.HasValue) d1 = d1.AddDays(Math.Max(30, (d1 - d0).TotalDays * 0.25));
            if ((d1 - d0).TotalDays < 7) d1 = d0.AddDays(7);
            double gb = 1L << 30, maxY = Math.Max(1, pts.Max(p => p.Free) / gb) * 1.15;
            Func<DateTime, float> X = d => plot.X + (float)((d - d0).TotalDays / (d1 - d0).TotalDays) * plot.Width;
            Func<double, float> Y = v => plot.Bottom - (float)(v / maxY) * plot.Height;
            using (var grid = new Pen(Theme.Grid))
            using (var axis = new Pen(Theme.Quiet))
            {
                double step = maxY > 200 ? 50 : maxY > 80 ? 20 : maxY > 40 ? 10 : 5;
                for (double v = 0; v <= maxY; v += step)
                {
                    g.DrawLine(grid, plot.X, Y(v), plot.Right, Y(v));
                    TextRenderer.DrawText(g, v.ToString("0") + " GB", Font, new Rectangle(r.X, (int)Y(v) - S(9), S(58), S(18)), Theme.Quiet, TextFormatFlags.Right);
                }
                g.DrawLine(axis, plot.X, plot.Bottom, plot.Right, plot.Bottom);
                var months = new List<DateTime>();
                for (var m = new DateTime(d0.Year, d0.Month, 1).AddMonths(1); m <= d1; m = m.AddMonths(1)) months.Add(m);
                int every = Math.Max(1, months.Count / 8);
                for (int i = 0; i < months.Count; i += every)
                    TextRenderer.DrawText(g, months[i].ToString("MMM yy", CultureInfo.InvariantCulture), Font, new Rectangle((int)X(months[i]) - S(30), plot.Bottom + S(6), S(60), S(18)), Theme.Quiet, TextFormatFlags.HorizontalCenter);
            }
            var line = pts.Select(p => new PointF(X(p.Day), Y(p.Free / gb))).ToArray();
            using (var pen = new Pen(Theme.Accent, S(2)))
            {
                if (line.Length > 1) g.DrawLines(pen, line);
                foreach (var p in line) g.FillEllipse(new SolidBrush(Theme.Accent), p.X - S(3), p.Y - S(3), S(6), S(6));
            }
            g.SetClip(new Rectangle(plot.X, r.Y, plot.Width + S(2), r.Height));
            if (fc.FullOn.HasValue)
            {
                using (var dash = new Pen(Theme.Danger, S(2)) { DashStyle = DashStyle.Dash })
                    g.DrawLine(dash, line.Last(), new PointF(X(fc.FullOn.Value), Y(0)));
                if (X(fc.FullOn.Value) <= plot.Right + 1)
                    TextRenderer.DrawText(g, "full ~ " + fc.FullOn.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture), Font,
                        new Rectangle((int)X(fc.FullOn.Value) - S(120), (int)Y(0) - S(24), S(120), S(18)), Theme.Danger, TextFormatFlags.Right);
                else
                    TextRenderer.DrawText(g, "full ~ " + fc.FullOn.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture) + "  \u2192", Font,
                        new Rectangle(plot.Right - S(160), plot.Y, S(160), S(18)), Theme.Danger, TextFormatFlags.Right);
            }
            g.ResetClip();
            var last = pts.Last();
            TextRenderer.DrawText(g, Fs.Fmt(last.Free) + " free now", Font, new Rectangle((int)line.Last().X - S(150), (int)line.Last().Y - S(40), S(140), S(18)), Theme.Ink, TextFormatFlags.Right);
        }
    }

    // ============================ folder map (treemap) ============================
    public static class Treemap
    {
        public class Node { public string Name = "", Path = ""; public long Bytes; public bool IsDir, Group; public RectangleF Rect; }

        // squarified treemap: rows of boxes whose shapes stay close to squares
        public static void Layout(List<Node> nodes, RectangleF area)
        {
            var items = nodes.Where(n => n.Bytes > 0).OrderByDescending(n => n.Bytes).ToList();
            double total = items.Sum(n => (double)n.Bytes);
            if (total <= 0 || area.Width <= 0 || area.Height <= 0) return;
            double scale = area.Width * area.Height / total;
            var rect = area;
            int i = 0;
            while (i < items.Count)
            {
                double side = Math.Min(rect.Width, rect.Height);
                var row = new List<Node> { items[i] };
                double rowArea = items[i].Bytes * scale;
                double best = Worst(row, rowArea, side, scale);
                int j = i + 1;
                while (j < items.Count)
                {
                    var trial = new List<Node>(row) { items[j] };
                    double ta = rowArea + items[j].Bytes * scale;
                    double w = Worst(trial, ta, side, scale);
                    if (w > best) break;
                    row = trial; rowArea = ta; best = w; j++;
                }
                // lay the row along the shorter side
                double thick = rowArea / side;
                double pos = 0;
                foreach (var n in row)
                {
                    double len = n.Bytes * scale / thick;
                    n.Rect = rect.Width >= rect.Height
                        ? new RectangleF(rect.X, rect.Y + (float)pos, (float)thick, (float)len)
                        : new RectangleF(rect.X + (float)pos, rect.Y, (float)len, (float)thick);
                    pos += len;
                }
                rect = rect.Width >= rect.Height
                    ? new RectangleF(rect.X + (float)thick, rect.Y, rect.Width - (float)thick, rect.Height)
                    : new RectangleF(rect.X, rect.Y + (float)thick, rect.Width, rect.Height - (float)thick);
                i = j;
            }
        }

        static double Worst(List<Node> row, double rowArea, double side, double scale)
        {
            double max = row.Max(n => n.Bytes * scale), min = row.Min(n => n.Bytes * scale), s2 = side * side, a2 = rowArea * rowArea;
            return Math.Max(s2 * max / a2, a2 / (s2 * min));
        }

        public static List<Node> Read(string dir, Dictionary<string, long> cache, Action<string, List<Node>> progress, CancellationToken ct)
        {
            var nodes = new List<Node>();
            string[] dirs = new string[0]; FileInfo[] files = new FileInfo[0];
            try { dirs = Directory.GetDirectories(dir); } catch { }
            try { files = new DirectoryInfo(dir).GetFiles(); } catch { }
            long small = 0; int smallCount = 0;
            foreach (var f in files)
            {
                long len = 0; try { len = f.Length; } catch { }
                if (len >= 256L << 20) nodes.Add(new Node { Name = f.Name, Path = f.FullName, Bytes = len });
                else { small += len; smallCount++; }
            }
            if (small > 0) nodes.Add(new Node { Name = smallCount + " smaller files", Path = dir, Bytes = small, Group = true });
            foreach (var d in dirs)
            {
                if (ct.IsCancellationRequested) break;
                try { if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0 || Lx.SkipDir(d, dir)) continue; } catch { continue; }
                var n = new Node { Name = Path.GetFileName(d), Path = d, IsDir = true };
                long sz;
                if (!cache.TryGetValue(d, out sz)) { if (progress != null) progress(n.Name, nodes); sz = Fs.SizeOf(d); lock (cache) cache[d] = sz; }
                n.Bytes = sz;
                nodes.Add(n);
                if (progress != null) progress(null, nodes);
            }
            return nodes;
        }
    }

}
