// Dashboard data and the small painted controls (drive bar, brand bar, tier cards), and the scan snapshot.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class DashLoc
    {
        public string Id, Name, Path, Tier, Action, Tool;
        public long? Bytes;   // measured size; null = not present
        public long Own;      // size without other listed locations inside it
        public bool OnC = true;
    }

    public static class DashData
    {
        public static readonly string[] TierOrder = { "critical", "caution", "review", "safe" };

        public static List<DashLoc> Build()
        {
            bool win = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var list = new List<DashLoc>();
            foreach (var l in Scanner.Locations())
                list.Add(new DashLoc
                {
                    Id = l[0], Name = l[1], Path = l[2], Tier = l[3], Action = l[4], Tool = l[5],
                    OnC = !win || l[2].StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase)
                });
            return list;
        }

        public static void Measure(DashLoc d)
        {
            if (!d.OnC) { d.Bytes = null; return; }
            bool exists = File.Exists(d.Path) || Directory.Exists(d.Path);
            d.Bytes = exists ? (long?)Fs.SizeOf(d.Path) : null;
        }

        static string Norm(string p) { return p.TrimEnd('\\', '/').ToLowerInvariant(); }

        public static bool Inside(string child, string parent)
        {
            string c = Norm(child), pa = Norm(parent);
            return c.Length > pa.Length && c.StartsWith(pa, StringComparison.Ordinal) && (c[pa.Length] == '\\' || c[pa.Length] == '/');
        }

        // Subtract listed locations that sit inside another listed location, so nothing is counted twice.
        public static void ComputeOwn(List<DashLoc> locs)
        {
            foreach (var a in locs)
            {
                a.Own = a.Bytes ?? 0;
                foreach (var b in locs)
                {
                    if (b == a || !b.Bytes.HasValue || !Inside(b.Path, a.Path)) continue;
                    bool deeper = locs.Any(c => c != a && c != b && c.Bytes.HasValue && Inside(c.Path, a.Path) && Inside(b.Path, c.Path));
                    if (!deeper) a.Own -= b.Bytes.Value;
                }
                if (a.Own < 0) a.Own = 0;
            }
        }

        public static Dictionary<string, long> Totals(List<DashLoc> locs)
        {
            var t = TierOrder.ToDictionary(x => x, x => 0L);
            foreach (var l in locs) if (l.Bytes.HasValue && t.ContainsKey(l.Tier)) t[l.Tier] += l.Own;
            return t;
        }

        // text bar for a list cell, e.g. "||||||||       12%"
        public static string Bar(long part, long max, long whole, int width)
        {
            int n = max <= 0 ? 0 : (int)Math.Round(width * (double)part / max);
            if (part > 0 && n == 0) n = 1;
            string pct = whole <= 0 ? "" : (part * 100.0 / whole < 1 && part > 0 ? "<1%" : Math.Round(part * 100.0 / whole) + "%");
            return new string('\u2588', n) + new string(' ', Math.Max(0, width - n)) + " " + pct;
        }
    }

    public class DashSorter : System.Collections.IComparer
    {
        public int Compare(object x, object y)
        {
            var a = (DashLoc)((ListViewItem)x).Tag;
            var b = (DashLoc)((ListViewItem)y).Tag;
            long sa = a.Bytes.HasValue && a.OnC ? a.Own : -1, sb = b.Bytes.HasValue && b.OnC ? b.Own : -1;
            return sb.CompareTo(sa);
        }
    }

    // stacked bar of drive C: with a legend under it
    public class DriveBar : Control
    {
        public class Seg { public string Label; public long Bytes; public Color Color; public bool Outline; }
        public List<Seg> Segments = new List<Seg>();
        public long Total;
        public string Of = "C:";
        public int BarHeight = 26;
        readonly ToolTip tip = new ToolTip();
        string lastTip = "";

        public DriveBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var r = new Rectangle(0, 0, Width - 1, BarHeight);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = PaintedCard.Round(r, Math.Max(4, BarHeight / 3f)))
            {
                using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(62, 62, 76) : Color.FromArgb(232, 236, 242))) g.FillPath(b, path);
                var clip = g.Clip;
                g.SetClip(path);
                if (Total > 0)
                {
                    float x = r.X;
                    foreach (var sg in Segments)
                    {
                        float w = (float)(r.Width * (double)sg.Bytes / Total);
                        if (w <= 0) continue;
                        using (var b = new SolidBrush(sg.Color)) g.FillRectangle(b, RightToLeft == RightToLeft.Yes ? r.Right - x - w : x, r.Y, w, r.Height);
                        x += w;
                    }
                }
                g.Clip = clip;
                using (var p = new Pen(Theme.Grid)) g.DrawPath(p, path);
            }

            int lx = 0, ly = r.Bottom + 8, box = Math.Max(10, Font.Height - 4);
            foreach (var sg in Segments)
            {
                string text = sg.Label + "  " + Fs.Fmt(sg.Bytes);
                var size = TextRenderer.MeasureText(g, text, Font);
                if (lx > 0 && lx + box + 6 + size.Width > Width) { lx = 0; ly += size.Height + 4; }
                using (var b = new SolidBrush(sg.Color)) g.FillRectangle(b, lx, ly + (size.Height - box) / 2, box, box);
                if (sg.Outline) using (var p = new Pen(Color.Silver)) g.DrawRectangle(p, lx, ly + (size.Height - box) / 2, box, box);
                TextRenderer.DrawText(g, text, Font, new Point(lx + box + 4, ly), ForeColor);
                lx += box + 6 + size.Width + 16;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string t = "";
            if (Total > 0 && e.Y <= BarHeight)
            {
                double x = 0;
                foreach (var sg in Segments)
                {
                    double w = (Width - 1) * (double)sg.Bytes / Total;
                    if (e.X >= x && e.X < x + w) { t = sg.Label + ": " + Fs.Fmt(sg.Bytes) + " (" + Math.Round(sg.Bytes * 100.0 / Total) + "% of " + Of + ")"; break; }
                    x += w;
                }
            }
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t); }
        }
    }

    public static class Brand
    {
        // the full icon is embedded in the exe, so every size stays sharp
        public static Icon Load(int size)
        {
            try
            {
                using (var st = typeof(Brand).Assembly.GetManifestResourceStream("Elixar.app.ico"))
                    if (st != null) return new Icon(st, size, size);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; }
        }

        static Image picture;
        // 128px PNG of the logo for the brand bar
        public static Image Picture()
        {
            if (picture != null) return picture;
            try
            {
                using (var st = typeof(Brand).Assembly.GetManifestResourceStream("Elixar.brand.png"))
                    if (st != null) { using (var tmp = Image.FromStream(st)) picture = new Bitmap(tmp); }
            }
            catch { }
            return picture;
        }
    }

    // Elixar brand bar across the top of the window
    public class BrandBar : Control
    {
        public Icon AppIcon;
        public Font NameFont, TagFont;
        public BrandBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(ClientRectangle.Width > 0 ? ClientRectangle : new Rectangle(0, 0, 1, 1),
                       Color.FromArgb(38, 22, 74), Color.FromArgb(18, 92, 104), System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                g.FillRectangle(b, ClientRectangle);
            int pad = Height / 5, x = pad + 4;
            if (AppIcon != null)
            {
                int sz = Height - 2 * pad;
                try
                {
                    var img = Brand.Picture();
                    if (img != null) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(img, new Rectangle(x, pad, sz, sz)); }
                }
                catch { }
                x += sz + pad;
            }
            var nf = NameFont ?? Font;
            var ns = TextRenderer.MeasureText(g, "Elixar", nf);
            TextRenderer.DrawText(g, "Elixar", nf, new Point(x, (Height - ns.Height) / 2), Color.White);
            var tf = TagFont ?? Font;
            // the edition, as a small pill after the name
            string ed = Lang.T("for " + Edition.Short);
            var es = TextRenderer.MeasureText(g, ed, tf);
            var er = new Rectangle(x + ns.Width + 6, (Height - es.Height) / 2 - 1, es.Width + 10, es.Height + 2);
            using (var pb = new SolidBrush(Color.FromArgb(Edition.Business ? 70 : 45, 255, 255, 255))) g.FillRectangle(pb, er);
            TextRenderer.DrawText(g, ed, tf, new Point(er.X + 5, er.Y + 1), Color.White);
            ns.Width += er.Width + 8;
            string tag = Lang.T("The cure for a cluttered PC");
            var ts = TextRenderer.MeasureText(g, tag, tf);
            TextRenderer.DrawText(g, tag, tf, new Point(x + ns.Width + 8, (Height - ts.Height) / 2 + 1), Color.FromArgb(190, 225, 230));
            // signature on the right
            var v = typeof(BrandBar).Assembly.GetName().Version;
            string sig = "Made by Elisar   v" + v.Major + "." + v.Minor + "." + v.Build;
            var ss = TextRenderer.MeasureText(g, sig, tf);
            int sx = Width - ss.Width - pad - 6;
            if (sx > x + ns.Width + ts.Width + 30)
                TextRenderer.DrawText(g, sig, tf, new Point(sx, (Height - ss.Height) / 2 + 1), Color.FromArgb(225, 236, 245));
        }
    }

    // one card per level: colored stripe, title, big number, short advice
    public class TierCard : Control
    {
        public Color Accent = Color.Gray;
        public string Title = "", Big = "", Sub = "";
        public Font TitleFont, BigFont;

        public TierCard()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Back);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int pad = Math.Max(10, Font.Height - 2);
            using (var path = PaintedCard.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), pad * 0.8f))
            {
                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Grid)) g.DrawPath(p, path);
            }
            var tf = TitleFont ?? Font;
            var bf = BigFont ?? Font;
            bool rtl = RightToLeft == RightToLeft.Yes;
            Color acc = Theme.MapFore(Accent);
            int y = pad, dot = Math.Max(8, tf.Height / 2);
            var dr = new Rectangle(pad, y + (tf.Height - dot) / 2, dot, dot);
            if (rtl) dr.X = Width - dr.Right;
            using (var b = new SolidBrush(acc)) g.FillEllipse(b, dr);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            TextRenderer.DrawText(g, Title, tf, new Rectangle(rtl ? pad : pad + dot + 6, y, Width - 2 * pad - dot - 6, tf.Height + 2), acc, flags);
            y += tf.Height + 4;
            TextRenderer.DrawText(g, Big, bf, new Rectangle(pad, y, Width - 2 * pad, bf.Height + 2), Theme.Ink, flags);
            y += bf.Height + 2;
            TextRenderer.DrawText(g, Sub, Font, new Rectangle(pad, y, Width - 2 * pad, Height - y - 4), Theme.Quiet, flags | TextFormatFlags.WordBreak);
        }
    }

    // ---------------- scan history for Rescan ----------------
    public class ScanSnapshot
    {
        public DateTime Time;
        public long Free;
        public Dictionary<string, long> Sizes = new Dictionary<string, long>();

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.Append("time\t").Append(Time.ToString("o")).Append('\n');
            sb.Append("free\t").Append(Free).Append('\n');
            foreach (var kv in Sizes) sb.Append("item\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            File.WriteAllText(path, sb.ToString());
        }

        public static ScanSnapshot Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var snap = new ScanSnapshot();
                bool hasTime = false;
                foreach (var line in File.ReadAllLines(path))
                {
                    var p = line.Split('\t');
                    if (p[0] == "time" && p.Length == 2) hasTime = DateTime.TryParse(p[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out snap.Time);
                    else if (p[0] == "free" && p.Length == 2) long.TryParse(p[1], out snap.Free);
                    else if (p[0] == "item" && p.Length == 3) { long v; if (long.TryParse(p[2], out v)) snap.Sizes[p[1]] = v; }
                }
                return hasTime ? snap : null;
            }
            catch { return null; }
        }

        // "-5.7 GB" when it shrank, "+12 MB" when it grew, "no change" under 1 MB
        public static string FmtDelta(long d)
        {
            if (Math.Abs(d) < (1L << 20)) return "no change";
            return (d < 0 ? "-" : "+") + Fs.Fmt(Math.Abs(d));
        }

        public static string FmtWhen(DateTime t)
        {
            return t.Date == DateTime.Now.Date ? t.ToString("HH:mm") : t.ToString("d MMM HH:mm");
        }
    }

    // ---------------- window ----------------
}
