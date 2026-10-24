// Dashboard overview: how C: is doing at a glance, one safe clean, status tiles, quick wins, other drives and recent activity.
// Everything here only reads; the clean buttons go through the same confirmed clean as the Cleaner tab.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    // ============================ what Elixar did, for "Recent activity" ============================
    // kept in Elixar's data folder, which only administrators can change; the text is only ever shown, never run
    public static class Activity
    {
        public class Entry { public DateTime Time; public string Kind = "", Text = ""; public long Bytes; }
        public static string OverrideFile;   // self-test only
        static string File_ { get { return OverrideFile ?? Paths.FileIn("activity.txt"); } }
        static readonly object gate = new object();
        const int Keep = 60;

        static string Clean(string s) { return (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        public static void Add(string kind, string text, long bytes)
        {
            lock (gate)
            {
                try
                {
                    string f = File_;
                    var lines = File.Exists(f) ? File.ReadAllLines(f).ToList() : new List<string>();
                    lines.Add(DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\t" + Clean(kind) + "\t" + bytes + "\t" + Clean(text));
                    if (lines.Count > Keep) lines = lines.Skip(lines.Count - Keep).ToList();
                    File.WriteAllLines(f, lines);
                }
                catch { }
            }
            ChangeLog.Add("Cleaning", Clean(text), "", "", bytes > 0 ? Fs.Fmt(bytes) : "", "", false);
        }

        public static List<Entry> Read()
        {
            var list = new List<Entry>();
            try
            {
                string f = File_;
                if (!File.Exists(f)) return list;
                foreach (var line in File.ReadAllLines(f))
                {
                    var p = line.Split('\t');
                    DateTime t; long b;
                    if (p.Length == 4 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && long.TryParse(p[2], out b))
                        list.Add(new Entry { Time = t, Kind = p[1], Bytes = b, Text = p[3] });
                }
            }
            catch { }
            return list;
        }

        // Elixar's own record plus the weekly cleans, the Undo folder and the last email, newest first
        public static List<Entry> Recent(int n, List<Entry> own, List<Tuple<DateTime, long>> autoRuns, List<UndoBatch> undo, DateTime mailSent, string mailWhat)
        {
            var all = new List<Entry>(own);
            foreach (var r in autoRuns) all.Add(new Entry { Time = r.Item1, Kind = "auto", Bytes = r.Item2, Text = "Weekly clean freed " + Fs.Fmt(r.Item2) });
            foreach (var b in undo) all.Add(new Entry { Time = b.Time, Kind = "undo", Bytes = b.Bytes, Text = "Moved to Undo: " + b.Label + " (" + Fs.Fmt(b.Bytes) + ")" });
            if (mailSent != DateTime.MinValue) all.Add(new Entry { Time = mailSent, Kind = "mail", Text = "Email sent: " + mailWhat });
            return all.OrderByDescending(e => e.Time).Take(n).ToList();
        }
    }

    public class DriveRow { public string Letter = "", Label = "", Root = "", Media = ""; public bool Removable; public long Free, Total; public HealthLevel Health = HealthLevel.Unknown; }

    // ============================ everything the overview shows ============================
    public class DashSummary
    {
        public long Free, Total, Safe;
        public int SafeCount;
        public bool Measured, Cleaning;
        public Dictionary<string, long> Tiers;           // null until the Details view measured
        public List<SpaceHistory.Point> History = new List<SpaceHistory.Point>();
        public SpaceHistory.Forecast Forecast;
        public bool DiskChecked; public HealthLevel Disk = HealthLevel.Unknown; public int DiskCount;
        public DateTime? LastClean; public long LastCleanBytes; public bool LastCleanAuto;
        public bool AutoKnown, AutoClean, AutoAlert; public string AutoDay = "";
        public bool TidyOn, TidyLow; public int TidyDays;   // auto-delete (the daily tidy)
        public string MailEmail; public bool MailReports; public int MailLevel;
        public long UndoBytes; public int UndoCount;
        public long TrashBytes = -1, AptBytes = -1, SnapBytes = -1, OneDriveFree = -1;
        public List<Growth.Watch> Watch = new List<Growth.Watch>();   // folders that keep growing
        public bool WatchHistory;                                       // two weeks of readings so far
        public List<Growth.Change> Grew = new List<Growth.Change>();   // what grew this week   // Linux tiles and the Snap warning
        public List<DriveRow> Drives = new List<DriveRow>();
        public List<Activity.Entry> Recent = new List<Activity.Entry>();
        public List<CleanItem> TopSafe = new List<CleanItem>();
        public DateTime Now = DateTime.Now;

        // what "Free ... safely" cleans: the Safe items the weekly clean also does (ticked by default, never privacy), measured, biggest first
        public static List<CleanItem> SafePick(IEnumerable<CleanItem> items)
        {
            return items.Where(i => i.Tier == Tier.Safe && i.DefaultOn && !i.Privacy && !i.SizeUnknown && i.Size >= (1L << 20)).OrderByDescending(i => i.Size).ToList();
        }

        public double FreeShare { get { return Total > 0 ? Free / (double)Total : 0; } }

        // 0 fine, 1 getting full, 2 almost full
        public int Level { get { return LevelOf(Free, Total); } }

        // 0 fine, 1 getting full, 2 almost full: for any drive
        public static int LevelOf(long free, long total)
        {
            if (total <= 0) return 0;
            double share = free / (double)total;
            if (share < 0.10 || free < (8L << 30)) return 2;
            if (share < 0.20 || free < (25L << 30)) return 1;
            return 0;
        }

        public string Headline
        {
            get
            {
                if (Total <= 0) return Win.IsWindows ? "Measuring drive C:..." : "Measuring your system disk...";
                if (!Win.IsWindows) return Level == 2 ? "Your system disk is almost full" : Level == 1 ? "Your system disk is getting full" : "Your system disk is in good shape";
                return Level == 2 ? "C: is almost full" : Level == 1 ? "C: is getting full" : "C: is in good shape";
            }
        }

        // short English sentences; each one is translated on its own
        public List<string> SubLines
        {
            get
            {
                var l = new List<string>();
                if (Total <= 0) return l;
                if (Cleaning) l.Add("Cleaning now...");
                else if (!Measured) l.Add("Measuring what can be cleaned...");
                else if (Safe >= (256L << 20)) l.Add(Lx.Words(Fs.Fmt(Safe) + " of caches and temp files can be freed safely: Windows and your apps rebuild them by themselves."));
                else if (Level == 0) l.Add("Nothing needs cleaning right now.");
                else l.Add(Win.IsWindows ? "The safe items are already clean. The Details view shows what else fills C:." : "The safe items are already clean. The Details view shows what else fills the disk.");
                if (Forecast != null && Forecast.FullOn.HasValue && (Forecast.FullOn.Value - Now).TotalDays < 365)
                    l.Add((Win.IsWindows ? "At this pace C: is full around " : "At this pace your system disk is full around ") + Ui.Month(Forecast.FullOn.Value) + ".");
                if (Grew.Count > 0 && Grew[0].Bytes >= (1L << 30))
                    l.Add(Grew[0].Name + " grew " + Fs.Fmt(Grew[0].Bytes) + " this week.");
                if (OneDriveFree >= (1L << 30))
                    l.Add("OneDrive: " + Fs.Fmt(OneDriveFree) + " of files you haven't used for 3 months could be online-only (see the OneDrive tab).");
                if (SnapBytes > 0 && LxSystem.SnapsLarge(SnapBytes, Total))
                    l.Add("Snap apps take " + Fs.Fmt(SnapBytes) + ", more than usual: the Linux tab shows which ones.");
                return l;
            }
        }

        public string PrimaryText
        {
            get
            {
                if (Cleaning) return "Cleaning...";
                if (!Measured) return "Measuring...";
                return Safe >= (1L << 20) ? "Free " + Fs.Fmt(Safe) + " safely" : "Nothing to clean now";
            }
        }
        public bool PrimaryEnabled { get { return Measured && !Cleaning && Safe >= (1L << 20); } }
    }

    // small text helpers shared by the overview
    public static class Ui
    {
        static CultureInfo he;
        public static CultureInfo Culture
        {
            get
            {
                if (!Lang.Hebrew) return CultureInfo.InvariantCulture;
                if (he == null) { try { he = new CultureInfo("he-IL"); } catch { he = CultureInfo.InvariantCulture; } }
                return he;
            }
        }
        public static string Month(DateTime d) { return d.ToString("MMMM yyyy", Culture); }
        public static string ShortMonth(DateTime d) { return Lang.Hebrew ? d.ToString("MM/yyyy", CultureInfo.InvariantCulture) : d.ToString("MMM yyyy", CultureInfo.InvariantCulture); }

        public static string Ago(DateTime t, DateTime now)
        {
            int d = (int)Math.Round((now.Date - t.Date).TotalDays);
            return d <= 0 ? "Today" : d == 1 ? "Yesterday" : d + " days ago";
        }

        public static string When(DateTime t, DateTime now)
        {
            int d = (int)Math.Round((now.Date - t.Date).TotalDays);
            string hm = t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (d <= 0) return "Today " + hm;
            if (d == 1) return "Yesterday " + hm;
            return t.ToString(d < 7 ? "ddd HH:mm" : "d MMM", Culture);
        }
    }

    // ============================ painted pieces ============================
    public class PaintedCard : Control
    {
        public float Scale_ = 1f;
        protected int S(int px) { return (int)Math.Round(px * Scale_); }

        public PaintedCard()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected bool Rtl { get { return RightToLeft == RightToLeft.Yes; } }
        protected Rectangle M(Rectangle r) { return Rtl ? new Rectangle(Width - r.Right, r.Y, r.Width, r.Height) : r; }
        protected RectangleF M(RectangleF r) { return Rtl ? new RectangleF(Width - r.Right, r.Y, r.Width, r.Height) : r; }
        protected Color Under { get { return Parent != null ? Parent.BackColor : Theme.Back; } }

        // text in a rectangle given in left-to-right terms; mirrored for Hebrew
        protected void Txt(Graphics g, string t, Font f, Rectangle r, Color c, TextFormatFlags extra)
        {
            bool right = (extra & TextFormatFlags.Right) != 0;
            var flags = (extra & ~TextFormatFlags.Right) | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            if (Rtl ^ right) flags |= TextFormatFlags.Right;
            if (Rtl) flags |= TextFormatFlags.RightToLeft;
            TextRenderer.DrawText(g, t ?? "", f, M(r), c, flags);
        }

        protected static int W(Graphics g, string t, Font f) { return TextRenderer.MeasureText(g, t ?? "", f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width; }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // the card itself: rounded, with a hairline border
        protected void Card(Graphics g, Color fill, Color border)
        {
            g.Clear(Under);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Round(r, S(10)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(border)) g.DrawPath(p, path);
            }
        }

        // simple line icons, so nothing depends on an icon font
        public static void Glyph(Graphics g, string kind, RectangleF b, Color c, float w)
        {
            float x = b.X, y = b.Y, X = b.Width, Y = b.Height;
            Func<float, float, PointF> P = (fx, fy) => new PointF(x + X * fx, y + Y * fy);
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                switch (kind)
                {
                    case "disk":
                        using (var path = Round(new RectangleF(x + X * .12f, y + Y * .26f, X * .76f, Y * .48f), X * .1f)) g.DrawPath(p, path);
                        g.DrawLine(p, P(.25f, .5f), P(.5f, .5f));
                        g.FillEllipse(br, x + X * .64f, y + Y * .44f, X * .12f, Y * .12f);
                        break;
                    case "trend":
                        g.DrawLines(p, new[] { P(.1f, .74f), P(.36f, .48f), P(.56f, .62f), P(.88f, .28f) });
                        g.DrawLines(p, new[] { P(.68f, .27f), P(.88f, .28f), P(.88f, .48f) });
                        break;
                    case "clean":
                        Star(g, br, P(.42f, .55f), X * .36f, X * .1f);
                        Star(g, br, P(.78f, .22f), X * .16f, X * .05f);
                        break;
                    case "clock":
                        g.DrawEllipse(p, x + X * .13f, y + Y * .13f, X * .74f, Y * .74f);
                        g.DrawLines(p, new[] { P(.5f, .3f), P(.5f, .5f), P(.65f, .6f) });
                        break;
                    case "mail":
                        using (var path = Round(new RectangleF(x + X * .12f, y + Y * .24f, X * .76f, Y * .52f), X * .07f)) g.DrawPath(p, path);
                        g.DrawLines(p, new[] { P(.16f, .3f), P(.5f, .54f), P(.84f, .3f) });
                        break;
                    case "watch":   // an eye: the folders Elixar keeps watching
                        using (var path = new GraphicsPath())
                        {
                            path.AddBezier(P(.1f, .5f), P(.3f, .2f), P(.7f, .2f), P(.9f, .5f));
                            path.AddBezier(P(.9f, .5f), P(.7f, .8f), P(.3f, .8f), P(.1f, .5f));
                            g.DrawPath(p, path);
                        }
                        g.DrawEllipse(p, x + X * .38f, y + Y * .38f, X * .24f, Y * .24f);
                        break;
                    case "undo":
                        using (var path = new GraphicsPath())
                        {
                            path.AddLine(P(.26f, .32f), P(.6f, .32f));
                            path.AddArc(x + X * .42f, y + Y * .32f, X * .36f, Y * .44f, 270, 180);
                            path.AddLine(P(.6f, .76f), P(.34f, .76f));
                            g.DrawPath(p, path);
                        }
                        g.DrawLines(p, new[] { P(.4f, .18f), P(.26f, .32f), P(.4f, .46f) });
                        break;
                    case "trash":
                        g.DrawLine(p, P(.18f, .28f), P(.82f, .28f));
                        g.DrawLines(p, new[] { P(.4f, .28f), P(.42f, .18f), P(.58f, .18f), P(.6f, .28f) });
                        g.DrawLines(p, new[] { P(.26f, .28f), P(.31f, .84f), P(.69f, .84f), P(.74f, .28f) });
                        g.DrawLine(p, P(.43f, .42f), P(.44f, .7f)); g.DrawLine(p, P(.57f, .42f), P(.56f, .7f));
                        break;
                    case "drive":
                        using (var path = Round(new RectangleF(x + X * .1f, y + Y * .3f, X * .8f, Y * .4f), X * .08f)) g.DrawPath(p, path);
                        g.FillEllipse(br, x + X * .7f, y + Y * .45f, X * .1f, Y * .1f);
                        break;
                }
            }
        }

        static void Star(Graphics g, Brush b, PointF c, float R, float r)
        {
            var pts = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                double a = Math.PI / 4 * i - Math.PI / 2;
                float rad = i % 2 == 0 ? R : r;
                pts[i] = new PointF(c.X + (float)(Math.Cos(a) * rad), c.Y + (float)(Math.Sin(a) * rad));
            }
            g.FillPolygon(b, pts);
        }

        // a pill-shaped button drawn inside a card
        protected void Pill(Graphics g, Rectangle r, string text, Font f, bool primary, bool enabled, bool hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rf = new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1);
            using (var path = Round(rf, r.Height / 2f))
            {
                if (primary)
                {
                    Color fill = !enabled ? Mix(Theme.Accent, Theme.Panel, 0.55) : hover ? Mix(Theme.Accent, Color.Black, Theme.Dark ? 0 : 0.14) : Theme.Accent;
                    if (enabled && hover && Theme.Dark) fill = Mix(Theme.Accent, Color.White, 0.15);
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    TextRenderer.DrawText(g, text, f, r, Theme.Dark ? Color.FromArgb(24, 20, 48) : Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | (Rtl ? TextFormatFlags.RightToLeft : 0));
                }
                else
                {
                    using (var b = new SolidBrush(hover && enabled ? Mix(Theme.Panel, Theme.Accent, 0.08) : Theme.Panel)) g.FillPath(b, path);
                    using (var p = new Pen(hover && enabled ? Theme.Accent : Theme.Grid)) g.DrawPath(p, path);
                    TextRenderer.DrawText(g, text, f, r, enabled ? (hover ? Theme.Accent : Theme.Ink) : Theme.Quiet,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | (Rtl ? TextFormatFlags.RightToLeft : 0));
                }
            }
        }
    }

    // the big card at the top: a ring for how full C: is, one sentence, the safe clean, and what fills C:
    public class HeroCard : PaintedCard
    {
        public double UsedShare = -1;
        public Color LevelColor = Color.Gray;
        public string Big = "", BigSub = "", Headline = "", Sub = "", Primary = "", Secondary = "", Third = "", BreakTitle = "";
        public bool PrimaryOn;
        public List<Tuple<string, long, Color>> Break = new List<Tuple<string, long, Color>>();
        public long BreakTotal;
        public Font HeadFont, BigFont, BoldFont, SmallFont;
        public event Action PrimaryClicked, SecondaryClicked, ThirdClicked;
        Rectangle pRect, sRect, tRect;
        int hover;   // 0 none, 1 primary, 2 secondary

        static Color A { get { return Theme.Dark ? Color.FromArgb(50, 44, 82) : Color.FromArgb(243, 240, 255); } }
        static Color B { get { return Theme.Dark ? Color.FromArgb(30, 54, 62) : Color.FromArgb(232, 246, 248); } }
        static Color Edge { get { return Theme.Dark ? Color.FromArgb(80, 74, 112) : Color.FromArgb(221, 216, 244); } }
        static Color Track { get { return Theme.Dark ? Color.FromArgb(72, 68, 98) : Color.FromArgb(224, 221, 240); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Under);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Round(r, S(14)))
            {
                using (var lg = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)), Rtl ? B : A, Rtl ? A : B, 0f)) g.FillPath(lg, path);
                using (var p = new Pen(Edge)) g.DrawPath(p, path);
            }
            var hf = HeadFont ?? Font; var bf = BigFont ?? Font; var bold = BoldFont ?? Font;
            int pad = S(22);

            // the ring: how much of C: is used
            int ring = Math.Max(S(60), Math.Min(Height - 2 * pad, S(150)));
            var rr = M(new Rectangle(pad + S(4), (Height - ring) / 2, ring, ring));
            float th = S(14);
            var arc = new RectangleF(rr.X + th / 2, rr.Y + th / 2, ring - th, ring - th);
            using (var p = new Pen(Track, th)) g.DrawEllipse(p, arc);
            if (UsedShare >= 0)
            {
                float sweep = (float)(360 * Math.Max(0.01, Math.Min(1, UsedShare)));
                using (var p = new Pen(LevelColor, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, arc, -90, Rtl ? -sweep : sweep);
            }
            var smf = SmallFont ?? Font;
            int bh = bf.Height, sh = Font.Height, ssh = smf.Height;
            TextRenderer.DrawText(g, Big, bf, new Rectangle(rr.X, rr.Y + (ring - bh - ssh) / 2, ring, bh), Theme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, BigSub, smf, new Rectangle(rr.X + (int)th, rr.Y + (ring - bh - ssh) / 2 + bh, ring - 2 * (int)th, ssh), Theme.Quiet,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | (Rtl ? TextFormatFlags.RightToLeft : 0));

            // what fills C:, on the right when there is room
            bool showBreak = Width >= S(900) && Break.Count > 0;
            int breakW = showBreak ? S(250) : 0;
            int x0 = pad + S(4) + ring + S(28);
            int textW = Width - x0 - pad - (showBreak ? breakW + S(32) : 0);

            int y = pad + S(2);
            Txt(g, Headline, hf, new Rectangle(x0, y, textW, hf.Height + S(2)), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += hf.Height + S(6);
            int bH = S(36), by = Height - pad - bH;
            bool buttons = Primary.Length > 0 || Secondary.Length > 0 || Third.Length > 0;
            Txt(g, Sub, Font, new Rectangle(x0, y, textW, Math.Max(sh, (buttons ? by - S(12) : Height - pad) - y)), Theme.Ink, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

            int pw = Primary.Length > 0 ? W(g, Primary, bold) + S(40) : 0, sw = Secondary.Length > 0 ? W(g, Secondary, bold) + S(36) : 0;
            pRect = pw > 0 ? M(new Rectangle(x0, by, pw, bH)) : Rectangle.Empty;
            sRect = sw > 0 ? M(new Rectangle(x0 + (pw > 0 ? pw + S(10) : 0), by, sw, bH)) : Rectangle.Empty;
            int tw = Third.Length > 0 ? W(g, Third, bold) + S(36) : 0;
            tRect = tw > 0 ? M(new Rectangle(x0 + (pw > 0 ? pw + S(10) : 0) + (sw > 0 ? sw + S(10) : 0), by, tw, bH)) : Rectangle.Empty;
            if (pw > 0) Pill(g, pRect, Primary, bold, true, PrimaryOn, hover == 1);
            if (sw > 0) Pill(g, sRect, Secondary, bold, false, true, hover == 2);
            if (tw > 0) Pill(g, tRect, Third, bold, false, true, hover == 3);

            if (showBreak)
            {
                int bx = Width - pad - breakW, rowH = S(25);
                int top = (Height - (S(22) + rowH * Break.Count)) / 2;
                Txt(g, BreakTitle, bold, new Rectangle(bx, top, breakW, bold.Height), Theme.Quiet, TextFormatFlags.SingleLine);
                int yy = top + S(22);
                foreach (var b in Break)
                {
                    int dot = S(9);
                    using (var br = new SolidBrush(b.Item3)) g.FillEllipse(br, M(new Rectangle(bx, yy + (sh - dot) / 2, dot, dot)));
                    string size = b.Item2 < 0 ? "..." : Fs.Fmt(b.Item2);
                    int sizeW = W(g, size, Font);
                    Txt(g, b.Item1, Font, new Rectangle(bx + S(16), yy, breakW - S(16) - sizeW - S(8), sh), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    Txt(g, size, Font, new Rectangle(bx + breakW - sizeW, yy, sizeW, sh), Theme.Quiet, TextFormatFlags.SingleLine);
                    var track = new RectangleF(bx + S(16), yy + sh + S(2), breakW - S(16), S(4));
                    using (var tb = new SolidBrush(Track)) using (var tp = Round(M(track), S(2))) g.FillPath(tb, tp);
                    if (BreakTotal > 0 && b.Item2 > 0)
                    {
                        var fill = new RectangleF(track.X, track.Y, Math.Max(S(4), track.Width * (float)Math.Min(1, b.Item2 / (double)BreakTotal)), track.Height);
                        using (var fb = new SolidBrush(b.Item3)) using (var fp = Round(M(fill), S(2))) g.FillPath(fb, fp);
                    }
                    yy += rowH;
                }
            }
        }

        int Hit(Point p) { return pRect.Contains(p) ? 1 : sRect.Contains(p) ? 2 : tRect.Contains(p) ? 3 : 0; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h == 1 && !PrimaryOn) h = 0;
            Cursor = h > 0 ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != 0) { hover = 0; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int h = Hit(e.Location);
            if (h == 1 && PrimaryOn && PrimaryClicked != null) PrimaryClicked();
            else if (h == 2 && SecondaryClicked != null) SecondaryClicked();
            else if (h == 3 && ThirdClicked != null) ThirdClicked();
        }
    }

    // one small status card; the whole card is a button
    public class TileCard : PaintedCard
    {
        public string Kind = "", Caption = "", Value = "", Sub = "";
        public Color Accent = Color.Gray;
        public List<double> Spark;
        public Font ValueFont, CapFont;
        bool hover;

        // an on/off switch in the corner (the Auto-delete tile): clicking it flips the switch, clicking elsewhere opens the tab
        public bool HasSwitch, SwitchOn, SwitchBusy;
        public event Action SwitchClicked;
        public Rectangle SwitchRect { get; private set; }
        bool downOnSwitch;

        public TileCard() { Cursor = Cursors.Hand; }

        protected override void OnMouseDown(MouseEventArgs e) { downOnSwitch = HasSwitch && SwitchRect.Contains(e.Location); base.OnMouseDown(e); }
        protected override void OnClick(EventArgs e)
        {
            if (downOnSwitch) { downOnSwitch = false; if (!SwitchBusy && SwitchClicked != null) SwitchClicked(); return; }
            base.OnClick(e);
        }
        public void ClickSwitch() { if (HasSwitch && !SwitchBusy && SwitchClicked != null) SwitchClicked(); }   // self-test and keyboard

        // drawn on the value line ("On" / "Off"), so the caption above keeps its full width
        void DrawSwitch(Graphics g, Color on, int rowTop, int rowH)
        {
            int w = S(40), h = S(22), pad = S(14);
            var r = new Rectangle(Rtl ? pad : Width - pad - w, rowTop + (rowH - h) / 2, w, h);
            SwitchRect = r;
            var back = SwitchBusy ? Theme.Grid : SwitchOn ? on : (Theme.Dark ? Color.FromArgb(78, 78, 94) : Color.FromArgb(196, 201, 212));
            using (var path = new GraphicsPath())
            {
                int d = h;
                path.AddArc(r.X, r.Y, d, d, 90, 180); path.AddArc(r.Right - d, r.Y, d, d, 270, 180); path.CloseFigure();
                using (var b = new SolidBrush(back)) g.FillPath(b, path);
            }
            int k = h - S(6);
            bool right = SwitchOn ^ Rtl;
            var knob = new Rectangle(right ? r.Right - k - S(3) : r.X + S(3), r.Y + S(3), k, k);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, knob);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color acc = Theme.MapFore(Accent);
            Card(g, hover ? Mix(Theme.Panel, acc, Theme.Dark ? 0.08 : 0.04) : Theme.Panel, hover ? acc : Theme.Grid);
            int pad = S(14), ic = S(32);
            var icon = M(new Rectangle(pad, pad, ic, ic));
            using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 60 : 30, acc))) g.FillEllipse(b, icon);
            Glyph(g, Kind, new RectangleF(icon.X + ic * .2f, icon.Y + ic * .2f, ic * .6f, ic * .6f), acc, Math.Max(1.5f, S(2) * .9f));

            var cf = CapFont ?? Font; var vf = ValueFont ?? Font;
            bool spark = Spark != null && Spark.Count >= 2;
            int sparkW = spark ? Math.Min(S(58), Width / 3) : 0;
            int cx = pad + ic + S(10);
            Txt(g, Caption, cf, new Rectangle(cx, pad + (ic - cf.Height) / 2, Width - cx - pad, cf.Height + 2), Theme.Quiet, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            int y = pad + ic + S(10);
            int swW = HasSwitch ? S(48) : 0;
            if (HasSwitch) DrawSwitch(g, Theme.MapFore(Color.FromArgb(0, 150, 90)), y, vf.Height + 2);
            Txt(g, Value, vf, new Rectangle(pad, y, Width - 2 * pad - swW, vf.Height + 2), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += vf.Height + S(2);
            if (spark) DrawSpark(g, M(new Rectangle(Width - pad - sparkW, y - S(1), sparkW, Font.Height + S(2))), acc);
            Txt(g, Sub, Font, new Rectangle(pad, y, Width - 2 * pad - (spark ? sparkW + S(6) : 0), Math.Max(Font.Height, Height - y - S(8))), Theme.Quiet, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        void DrawSpark(Graphics g, Rectangle r, Color c)
        {
            double lo = Spark.Min(), hi = Spark.Max();
            if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
            var pts = new PointF[Spark.Count];
            for (int i = 0; i < Spark.Count; i++)
            {
                float fx = Spark.Count == 1 ? 0 : i / (float)(Spark.Count - 1);
                if (Rtl) fx = 1 - fx;
                pts[i] = new PointF(r.X + fx * r.Width, r.Bottom - (float)((Spark[i] - lo) / (hi - lo)) * r.Height);
            }
            var area = new List<PointF>(pts) { new PointF(pts[pts.Length - 1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) };
            using (var b = new SolidBrush(Color.FromArgb(40, c))) g.FillPolygon(b, area.ToArray());
            using (var p = new Pen(c, Math.Max(1.5f, S(2) * .8f)) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
            var last = pts[pts.Length - 1];
            using (var b = new SolidBrush(c)) g.FillEllipse(b, last.X - S(3), last.Y - S(3), S(6), S(6));
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    }

    // a card with a title and a few rows (quick wins, other drives, recent activity)
    public class ListCard : PaintedCard
    {
        public class Row
        {
            public string Text = "", Right = "", Sub = "", Action;
            public Color Dot = Color.Empty, BarColor = Color.Gray;
            public double Bar = -1;
            public object Tag;
            public Rectangle ActionRect;
        }
        public string Title = "", Empty = "", Footer = "";
        public List<Row> Rows = new List<Row>();
        public Font TitleFont, SmallFont;
        public bool ActionsOn = true;
        public event Action<Row> ActionClicked;
        public event Action FooterClicked;
        Rectangle footRect; Row hoverRow; bool hoverFoot;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Card(g, Theme.Panel, Theme.Grid);
            var tf = TitleFont ?? Font; var sf = SmallFont ?? Font;
            int pad = S(16), lineH = Font.Height;
            Txt(g, Title, tf, new Rectangle(pad, pad, Width - 2 * pad, tf.Height + 2), Theme.Ink, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            int y = pad + tf.Height + S(12);
            int footH = Footer.Length > 0 ? lineH + S(10) : 0;
            int bottom = Height - pad - footH;
            if (Rows.Count == 0)
                Txt(g, Empty, Font, new Rectangle(pad, y, Width - 2 * pad, Math.Max(lineH, bottom - y)), Theme.Quiet, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            foreach (var row in Rows)
            {
                int need = lineH + (row.Bar >= 0 ? S(10) : 0) + (row.Sub.Length > 0 ? sf.Height + S(2) : 0);
                if (y + need > bottom) { row.ActionRect = Rectangle.Empty; continue; }
                int tx = pad;
                if (row.Dot != Color.Empty)
                {
                    int d = S(8);
                    using (var b = new SolidBrush(Soft(row.Dot))) g.FillEllipse(b, M(new Rectangle(pad, y + (lineH - d) / 2, d, d)));
                    tx = pad + S(16);
                }
                int end = Width - pad;
                row.ActionRect = Rectangle.Empty;
                if (row.Action != null)
                {
                    int aw = W(g, row.Action, sf) + S(22);
                    var ar = new Rectangle(end - aw, y - S(3), aw, lineH + S(6));
                    row.ActionRect = M(ar);
                    Pill(g, row.ActionRect, row.Action, sf, false, ActionsOn, hoverRow == row);
                    end = ar.X - S(10);
                }
                int rw = row.Right.Length > 0 ? W(g, row.Right, Font) : 0;
                if (rw > 0) Txt(g, row.Right, Font, new Rectangle(end - rw, y, rw, lineH), Theme.Quiet, TextFormatFlags.SingleLine);
                Txt(g, row.Text, Font, new Rectangle(tx, y, Math.Max(0, end - rw - S(8) - tx), lineH), Theme.Ink, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                y += lineH + S(4);
                if (row.Bar >= 0)
                {
                    var track = new RectangleF(tx, y, Math.Max(1, end - tx), S(5));
                    using (var tb = new SolidBrush(Theme.Dark ? Color.FromArgb(62, 62, 76) : Color.FromArgb(234, 236, 242))) using (var tp = Round(M(track), S(3))) g.FillPath(tb, tp);
                    var fill = new RectangleF(track.X, track.Y, Math.Max(S(5), track.Width * (float)Math.Min(1, row.Bar)), track.Height);
                    using (var fb = new SolidBrush(Soft(row.BarColor))) using (var fp = Round(M(fill), S(3))) g.FillPath(fb, fp);
                    y += S(6);
                }
                if (row.Sub.Length > 0)
                {
                    y += S(2);
                    Txt(g, row.Sub, sf, new Rectangle(tx, y, Math.Max(0, end - tx), sf.Height), Theme.MapFore(row.BarColor), TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    y += sf.Height;
                }
                y += S(12);
            }
            footRect = Rectangle.Empty;
            if (Footer.Length > 0)
            {
                string f = Footer + (Rtl ? "  ‹" : "  ›");
                int fw = W(g, f, Font);
                var fr = new Rectangle(pad, Height - pad - lineH, fw, lineH);
                footRect = M(fr);
                Txt(g, f, Font, fr, hoverFoot ? Mix(Theme.Accent, Theme.Ink, 0.3) : Theme.Accent, TextFormatFlags.SingleLine);
            }
        }

        // colours a little calmer on the dark theme
        static Color Soft(Color c) { return Theme.Dark ? Mix(Theme.MapFore(c), Theme.Panel, 0.22) : c; }

        Row HitRow(Point p) { return ActionsOn ? Rows.FirstOrDefault(r => r.Action != null && r.ActionRect.Contains(p)) : null; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var r = HitRow(e.Location); bool f = footRect.Contains(e.Location);
            Cursor = r != null || f ? Cursors.Hand : Cursors.Default;
            if (r != hoverRow || f != hoverFoot) { hoverRow = r; hoverFoot = f; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverRow = null; hoverFoot = false; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            var r = HitRow(e.Location);
            if (r != null && ActionClicked != null) ActionClicked(r);
            else if (footRect.Contains(e.Location) && FooterClicked != null) FooterClicked();
        }
    }

    // "Overview | Details"
    public class Segmented : PaintedCard
    {
        public string[] Options = new string[0];
        public int Selected;
        public event Action Changed;
        Rectangle[] rects = new Rectangle[0];

        public Segmented() { Cursor = Cursors.Hand; }

        public void FitWidth()
        {
            using (var g = CreateGraphics())
            {
                int w = S(6);
                foreach (var o in Options) w += W(g, o, Font) + S(32);
                Size = new Size(w, Font.Height + S(16));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Under);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Round(outer, Height / 2f))
            using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(48, 48, 58) : Color.FromArgb(232, 234, 241)))
                g.FillPath(b, path);
            rects = new Rectangle[Options.Length];
            int x = S(3);
            for (int i = 0; i < Options.Length; i++)
            {
                int w = W(g, Options[i], Font) + S(32);
                rects[i] = M(new Rectangle(x, S(3), w, Height - S(6)));
                if (i == Selected)
                    using (var path = Round(rects[i], (Height - S(6)) / 2f))
                    {
                        using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                        using (var p = new Pen(Theme.Grid)) g.DrawPath(p, path);
                    }
                TextRenderer.DrawText(g, Options[i], Font, rects[i], i == Selected ? Theme.Ink : Theme.Quiet,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | (Rtl ? TextFormatFlags.RightToLeft : 0));
                x += w;
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < rects.Length; i++)
                if (rects[i].Contains(e.Location) && i != Selected) { Selected = i; Invalidate(); if (Changed != null) Changed(); }
        }
    }

    // ============================ the overview in the main window ============================
    public partial class MainForm
    {
        Panel dashHost; TableLayoutPanel dashOverview, dashDetails, tileGrid;
        Segmented dashView;
        HeroCard hero;
        TileCard[] tiles;
        ListCard cardWins, cardDrives, cardActivity;
        DashSummary dashSum;
        bool sumBusy, sumAgain, cleaningNow, formClosing;
        DateTime lastOverview = DateTime.MinValue;
        int tileCols;
        public event Action OverviewDone;
        static readonly string[] TileKinds = { "disk", "trend", "clean", "clock", "mail", "undo", "watch" };
        static readonly string[] LinuxTileKinds = { "trash", "trend", "clean", "clock", "mail", "drive" };

        Control BuildOverview()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0), Padding = new Padding(0, 0, 0, S(4)), AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(206)));
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, S(130)));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.AutoScrollMinSize = new Size(0, S(660));

            hero = new HeroCard
            {
                Dock = DockStyle.Fill, Margin = new Padding(S(5)), Scale_ = s,
                HeadFont = new Font("Segoe UI Semibold", 16f), BigFont = new Font("Segoe UI Semibold", 15f), BoldFont = new Font("Segoe UI Semibold", 9.5f),
                SmallFont = new Font("Segoe UI", 8.75f), Font = new Font("Segoe UI", 10f), Headline = Lang.T(Win.IsWindows ? "Measuring drive C:..." : "Measuring your system disk..."), Primary = Lang.T("Measuring..."), Secondary = Lang.T("Choose what to clean"),
                BreakTitle = Lang.T(Win.IsWindows ? "What fills C:" : "What fills the disk")
            };
            hero.PrimaryClicked += FreeSafely;
            hero.SecondaryClicked += () => tabs.SelectedTab = pageClean;

            tileGrid = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0) };
            var capFont = new Font("Segoe UI Semibold", 8.25f); var valFont = new Font("Segoe UI Semibold", 12.5f);
            string[] caps = !LinuxUi ? new[] { "Disk health", "Space trend", "Last clean", "Auto-delete", "Email reports", "Undo", "Watch" }
                                          : new[] { "Trash", "Space trend", "Last clean", "Automation", "Email reports", "Update packages" };
            tiles = new TileCard[caps.Length];
            for (int i = 0; i < tiles.Length; i++)
            {
                string cap = Lang.T(caps[i]);
                tiles[i] = new TileCard
                {
                    Dock = DockStyle.Fill, Margin = new Padding(S(5)), Scale_ = s, Kind = !LinuxUi ? TileKinds[i] : LinuxTileKinds[i], Caption = Lang.Hebrew ? cap : cap.ToUpperInvariant(),
                    Value = "...", Accent = Theme.Accent, CapFont = capFont, ValueFont = valFont, AccessibleName = cap
                };
                int k = i;
                tiles[i].Click += (o, e) => TileClicked(k);
            }
            tiles[3].HasSwitch = !LinuxUi;
            tiles[3].SwitchClicked += () => SetAutoDelete(!tiles[3].SwitchOn);
            LayoutTiles(tiles.Length);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), Padding = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var titleFont = new Font("Segoe UI Semibold", 10.5f); var small = new Font("Segoe UI", 8.5f);
            Func<string, string, string, ListCard> card = (title, empty, foot) => new ListCard
            {
                Dock = DockStyle.Fill, Margin = new Padding(S(5)), Scale_ = s, Title = Lang.T(title), Empty = Lang.T(empty), Footer = foot.Length > 0 ? Lang.T(foot) : "",
                TitleFont = titleFont, SmallFont = small
            };
            cardWins = card("Quick wins", "Measuring...", "See everything in the Cleaner");
            cardDrives = Win.IsWindows ? card("Other drives", "No other drives. A second drive or a USB drive gives you room to move big folders off C:.", "Move files off C:")
                                       : card("Other disks", "No other disks. A second disk or a USB drive gives you room for big folders.", "");
            cardActivity = card("Recent activity", Win.IsWindows ? "Nothing yet. Cleans, emails and moves to Undo show up here." : "Nothing yet. Cleans and emails show up here.", "");
            cardWins.ActionsOn = !testMode;
            cardWins.ActionClicked += r => CleanItems(new List<CleanItem> { (CleanItem)r.Tag });
            cardWins.FooterClicked += () => tabs.SelectedTab = pageClean;
            cardDrives.FooterClicked += () => ShowMove();
            bottom.Controls.Add(cardWins, 0, 0); bottom.Controls.Add(cardDrives, 1, 0); bottom.Controls.Add(cardActivity, 2, 0);

            t.Controls.Add(hero, 0, 0); t.Controls.Add(tileGrid, 0, 1); t.Controls.Add(bottom, 0, 2);
            t.Resize += (o, e) => LayoutTiles(tiles.Length == 7 ? (t.ClientSize.Width >= S(1250) ? 7 : 4) : (t.ClientSize.Width >= S(1000) ? 6 : 3));
            FormClosing += (o, e) => { if (!e.Cancel) formClosing = true; };
            dashOverview = t;
            return t;
        }

        // all tiles in one row on a wide window; on a narrower one two rows (3 + 3, or 4 + 3 with seven tiles,
        // laid on a 12-column grid so both rows are filled edge to edge)
        void LayoutTiles(int cols)
        {
            if (cols == tileCols || tileGrid == null) return;
            tileCols = cols;
            int n = tiles.Length, rows = (n + cols - 1) / cols;
            tileGrid.SuspendLayout();
            tileGrid.Controls.Clear();
            tileGrid.ColumnStyles.Clear(); tileGrid.RowStyles.Clear();
            int grid = rows == 1 ? n : 12;
            tileGrid.ColumnCount = grid; tileGrid.RowCount = rows;
            for (int c = 0; c < grid; c++) tileGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / grid));
            for (int r = 0; r < rows; r++) tileGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            int at = 0;
            for (int r = 0; r < rows; r++)
            {
                int inRow = Math.Min(cols, n - at), span = grid / Math.Max(1, inRow);
                for (int k = 0; k < inRow; k++, at++) { tileGrid.Controls.Add(tiles[at], k * span, r); tileGrid.SetColumnSpan(tiles[at], span); }
            }
            tileGrid.ResumeLayout();
            if (dashOverview != null)
            {
                dashOverview.RowStyles[1].Height = S(130) * rows;
                dashOverview.AutoScrollMinSize = new Size(0, S(660) + S(130) * (rows - 1));
            }
        }

        void ShowDashView(int i)
        {
            if (dashView != null && dashView.Selected != i) { dashView.Selected = i; dashView.Invalidate(); }
            ShowDashArea();
            if (!testMode) try { Settings.Set("DashView", i == 1 ? "details" : "overview"); } catch { }
        }

        void TileClicked(int k)
        {
            if (LinuxUi && (k == 0 || k == 5))
            {
                if (k == 0) { try { Lx.Open("trash:///"); } catch { } }
                else tabs.SelectedTab = pageClean;
                return;
            }
            switch (k)
            {
                case 0: ShowDiskHealth(); break;
                case 1: using (var tf = new TrendForm(s, SpaceHistory.Read(), Win.IsWindows || Growth.OverrideFile != null ? Growth.Grew(DateTime.Today, 7, 100L << 20) : null)) { tf.Shown += (o2, e2) => { Theme.Apply(tf); Lang.Apply(tf); }; tf.ShowDialog(this); } break;
                case 2: tabs.SelectedTab = pageClean; break;
                case 3: tabs.SelectedTab = pageAuto; break;
                case 4: ShowSettings(); break;
                case 5: ShowUndo(); break;
                case 6:
                    WatchForm.ShowFor(this, testMode, dashLocs, p => { if (System.IO.Directory.Exists(p)) OpenDrill(p); else ShowInFolder(p); }, RefreshOverview);
                    break;
            }
        }

        List<CleanItem> SafeToFree() { return DashSummary.SafePick(items); }

        void FreeSafely()
        {
            if (!ready || cleaningNow) return;
            var sel = SafeToFree();
            if (sel.Count > 0) CleanItems(sel);
        }

        // ---------------- reading everything (slow parts in the background) ----------------
        DateTime autoRead = DateTime.MinValue; bool autoClean, autoAlert; string autoDay = "";

        public void RefreshOverview()
        {
            if (hero == null || IsDisposed || formClosing) return;
            if (sumBusy) { sumAgain = true; return; }
            sumBusy = true;
            var sum = new DashSummary { Measured = ready, Cleaning = cleaningNow, Free = Sys.Free(), Total = Sys.Total() };
            if (ready)
            {
                var safe = SafeToFree();
                sum.Safe = safe.Sum(i => i.Size); sum.SafeCount = safe.Count;
                sum.TopSafe = items.Where(i => i.Tier == Tier.Safe && !i.Privacy && i.Drive == null && !i.SizeUnknown && i.Size >= (1L << 20)).OrderByDescending(i => i.Size).Take(5).ToList();
            }
            if (dashLocs != null && DashboardFilled()) sum.Tiers = DashData.Totals(dashLocs);
            var rep = lastDiskReport;
            if (rep != null && rep.Error == null) { sum.DiskChecked = true; sum.Disk = rep.Worst; sum.DiskCount = rep.Disks.Count; }
            bool readAuto = Win.IsWindows && !testMode && (DateTime.Now - autoRead).TotalSeconds > 60;
            Task.Factory.StartNew(() =>
            {
                try { sum.History = SpaceHistory.Read(); sum.Forecast = SpaceHistory.Predict(sum.History, DateTime.Today); } catch { }
                var runs = new List<Tuple<DateTime, long>>();
                try { runs = Automation.ReadHistory(Automation.HistoryFile); } catch { }
                var undo = new List<UndoBatch>();
                try { undo = Quarantine.List(); } catch { }
                sum.UndoCount = undo.Count; sum.UndoBytes = undo.Sum(b => Math.Max(0, b.Bytes));
                if (!LinuxUi) try { var tp = TidyPrefs.Load(); sum.TidyOn = tp.AnyDaily; sum.TidyLow = tp.LowClean; sum.TidyDays = tp.BinDays; } catch { }
                if (Win.IsWindows || Growth.OverrideFile != null)
                {
                    try { sum.Grew = Growth.Grew(DateTime.Today, 7, 256L << 20); } catch { }
                    try { sum.Watch = Growth.Watchlist(DateTime.Today); sum.WatchHistory = Growth.HasHistory(DateTime.Today); } catch { }
                }
                if (odAccounts.Count > 0 && !LinuxUi) try { sum.OneDriveFree = OneDrive.QuickFreeable(); } catch { }
                if (Lx.On)
                {
                    sum.TrashBytes = Fs.SizeOf(Path.Combine(Path.Combine(Lx.DataHome, "Trash"), "files"));
                    sum.AptBytes = Lx.FilesIn("/var/cache/apt/archives", "*.deb");
                    try { sum.SnapBytes = LxSystem.Snaps(LxSystem.SnapDir).Sum(a => a.Bytes); } catch { }
                }
                try
                {
                    sum.MailEmail = MailAccount.Email;
                    if (sum.MailEmail != null)
                    {
                        var p = MailPrefs.Load(); sum.MailReports = p.Summary || p.CleanReport || p.Alerts;
                        int lvl; MailStatus.Line(out lvl); sum.MailLevel = lvl;
                    }
                    else MailStatus.Load();
                }
                catch { }
                if (readAuto)
                {
                    try
                    {
                        var c = Automation.Query(Automation.CleanTask); var a = Automation.Query(Automation.CheckTask); var l = Automation.Query(Automation.LogonTask);
                        autoClean = c.Exists && c.Enabled; autoDay = c.Day; autoAlert = (a.Exists && a.Enabled) || (l.Exists && l.Enabled);
                        autoRead = DateTime.Now;
                    }
                    catch { }
                }
                sum.AutoKnown = true; sum.AutoClean = autoClean; sum.AutoAlert = autoAlert; sum.AutoDay = autoDay ?? "";

                try { sum.Drives = DrivesOverride != null ? DrivesOverride() : OtherDrives(rep); } catch { sum.Drives = new List<DriveRow>(); }
                var own = Activity.Read();
                var cleans = own.Where(x => x.Kind == "clean").Select(x => Tuple.Create(x.Time, x.Bytes, false))
                                .Concat(runs.Select(x => Tuple.Create(x.Item1, x.Item2, true))).OrderByDescending(x => x.Item1).FirstOrDefault();
                if (cleans != null) { sum.LastClean = cleans.Item1; sum.LastCleanBytes = cleans.Item2; sum.LastCleanAuto = cleans.Item3; }
                sum.Recent = Activity.Recent(6, own, runs, undo, MailStatus.LastSent, MailStatus.LastWhat);
                return sum;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                sumBusy = false;
                try { if (!t.IsFaulted) ShowSummary(t.Result); else Log("Dashboard overview: " + t.Exception.GetBaseException().Message); }
                catch (Exception ex) { Log("Dashboard overview could not be drawn: " + ex.Message); }
                if (sumAgain) { sumAgain = false; RefreshOverview(); return; }
                if (OverviewDone != null) OverviewDone();
            }));
        }

        // a drive's name is set by whoever formatted it (a USB stick can come from anyone): show it as plain text only
        public static string CleanLabel(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s ?? "")
                if (!char.IsControl(c) && !(c >= '\u202A' && c <= '\u202E') && !(c >= '\u2066' && c <= '\u2069') && c != '\u200E' && c != '\u200F' && c != '\u061C') sb.Append(c);
            string r = sb.ToString().Trim();
            return r.Length > 32 ? r.Substring(0, 32) : r;
        }

        // the other disks you keep files on: /home on its own partition, a second disk, USB drives
        public static List<DriveRow> LinuxDrives()
        {
            var list = new List<DriveRow>();
            foreach (var m in Lx.DataMounts(Lx.Mounts()))
            {
                try
                {
                    var di = new DriveInfo(m.Point);
                    if (di.TotalSize < (1L << 30)) continue;
                    string name = m.Point == "/home" ? "Home" : CleanLabel(Path.GetFileName(m.Point.TrimEnd('/')));
                    list.Add(new DriveRow { Letter = m.Point, Label = name.Length > 0 ? name : m.Point, Root = m.Point, Removable = Lx.RemovableMount(m.Point),
                                            Free = di.AvailableFreeSpace, Total = di.TotalSize });
                }
                catch { }
            }
            return list;
        }

        static List<DriveRow> OtherDrives(DiskReport rep)
        {
            var list = new List<DriveRow>();
            if (!Win.IsWindows) return LinuxDrives();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                        string letter = d.Name.Substring(0, 1).ToUpperInvariant();
                        if (letter == "C" || !d.IsReady || d.TotalSize < (1L << 30)) continue;
                        var row = new DriveRow { Letter = letter, Label = CleanLabel(d.VolumeLabel), Root = d.RootDirectory.FullName, Removable = d.DriveType == DriveType.Removable, Free = d.AvailableFreeSpace, Total = d.TotalSize };
                        if (rep != null) { var disk = rep.Disks.FirstOrDefault(x => x.Letters.Split(',').Any(l => l.Trim().Equals(letter, StringComparison.OrdinalIgnoreCase))); if (disk != null) { row.Health = disk.Level; row.Media = disk.Kind; } }
                        list.Add(row);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        public bool OverviewFilled { get { return dashSum != null && dashSum.Measured && tiles.All(x => x.Value != "...") && cardWins.Rows.Count + (cardWins.Empty.Length > 0 ? 1 : 0) > 0; } }

        static Color LevelTint(int level) { return level == 2 ? CriticalColor : level == 1 ? CautionColor : SafeColor; }

        // ---------------- drawing a summary ----------------
        void ShowSummary(DashSummary m)
        {
            dashSum = m;
            MaybeNudgeMail(m);
            UpdateTray();
            lastOverview = DateTime.Now;
            // the ring and the sentence
            hero.UsedShare = m.Total > 0 ? 1 - m.FreeShare : -1;
            hero.LevelColor = Theme.MapFore(LevelTint(m.Level));
            hero.Big = m.Total > 0 ? Fs.Fmt(m.Free) : "...";
            hero.BigSub = m.Total > 0 ? Lang.T("free of " + Fs.Fmt(m.Total)) : "";
            hero.Headline = Lang.T(m.Headline);
            hero.Sub = string.Join(" ", m.SubLines.Select(Lang.T));
            hero.Primary = Lang.T(m.PrimaryText);
            hero.PrimaryOn = m.PrimaryEnabled && !testMode;
            hero.Break = new List<Tuple<string, long, Color>>();
            if (m.Tiers != null)
            {
                hero.Break.Add(Tuple.Create(Lang.T("Safe to clean"), m.Tiers["safe"], Theme.MapFore(SafeColor)));
                hero.Break.Add(Tuple.Create(Lang.T("Your files"), m.Tiers["review"], Theme.MapFore(ReviewColor)));
                hero.Break.Add(Tuple.Create(Lang.T("Leftovers and system stores"), m.Tiers["caution"], Theme.MapFore(CautionColor)));
                hero.Break.Add(Tuple.Create(Lang.T(Win.IsWindows ? "Windows and apps" : "System and apps"), m.Tiers["critical"], Theme.MapFore(CriticalColor)));
                hero.Break.Add(Tuple.Create(Lang.T("Free"), m.Free, Theme.Dark ? Color.FromArgb(150, 150, 170) : Color.FromArgb(160, 168, 182)));
                hero.BreakTotal = m.Total;
            }
            hero.Invalidate();

            // tiles
            Action<int, string, string, Color> tile = (i, v, sub, c) => { tiles[i].Value = Lang.T(v); tiles[i].Sub = Lang.T(sub); tiles[i].Accent = c; tiles[i].Invalidate(); };
            if (LinuxUi) tile(0, m.TrashBytes <= 0 ? "Empty" : Fs.Fmt(m.TrashBytes), m.TrashBytes <= 0 ? "Nothing waiting in the Trash" : "Click to look inside", m.TrashBytes > (1L << 30) ? CautionColor : Theme.Accent);
            else if (!m.DiskChecked) tile(0, "Not checked yet", "Click to check your drives", Theme.Accent);
            else tile(0, m.Disk == HealthLevel.Failing ? "Back up now!" : m.Disk == HealthLevel.Warning ? "Needs a look" : m.Disk == HealthLevel.Healthy ? "Healthy" : "Unknown",
                      m.DiskCount == 1 ? "1 drive checked" : m.DiskCount + " drives checked",
                      m.Disk == HealthLevel.Failing ? CriticalColor : m.Disk == HealthLevel.Warning ? CautionColor : m.Disk == HealthLevel.Healthy ? SafeColor : Theme.Accent);

            var fc = m.Forecast;
            var pts = m.History.Where(p => (DateTime.Today - p.Day).TotalDays <= 60 && p.Total > 0).Select(p => p.Free / (double)(1L << 30)).ToList();
            tiles[1].Spark = pts.Count >= 2 ? pts : null;
            if (fc == null || fc.Days == 0 && !fc.FullOn.HasValue && fc.GbPerMonth == 0)
                tile(1, "Collecting data", "A few more days of readings are needed", Theme.Accent);
            else
            {
                string rate = (fc.GbPerMonth >= 0 ? "+" : "-") + Math.Abs(fc.GbPerMonth).ToString("0.0", CultureInfo.InvariantCulture) + " GB a month";
                if (fc.FullOn.HasValue && (fc.FullOn.Value - DateTime.Today).TotalDays < 730)
                    tile(1, "Full ~ " + Ui.ShortMonth(fc.FullOn.Value), rate, (fc.FullOn.Value - DateTime.Today).TotalDays < 90 ? CriticalColor : (fc.FullOn.Value - DateTime.Today).TotalDays < 365 ? CautionColor : Theme.Accent);
                else tile(1, fc.GbPerMonth > 0.6 ? "Growing" : "Steady", rate, SafeColor);
            }

            if (!m.LastClean.HasValue) tile(2, "Not yet", "Clean once to start the record", CautionColor);
            else tile(2, Ui.Ago(m.LastClean.Value, m.Now), "Freed " + Fs.Fmt(m.LastCleanBytes) + (m.LastCleanAuto ? " (weekly clean)" : ""),
                      (m.Now - m.LastClean.Value).TotalDays > 30 ? CautionColor : SafeColor);

            if (LinuxUi) tile(3, m.AutoClean ? "Weekly clean on" : "Off", m.AutoClean ? "Every " + m.AutoDay : "Set it up in Automation", m.AutoClean ? SafeColor : Theme.Accent);
            else if (m.TidyOn)
                tile(3, "On", m.TidyDays > 0 ? (m.TidyLow ? "Old trash (" + m.TidyDays + "+ days) and a clean when C: is low" : "Recycle Bin items over " + m.TidyDays + " days old") : "A clean when C: is low", SafeColor);
            else tile(3, "Off", "Switch on to empty old trash by itself", Theme.Accent);
            if (tiles[3].HasSwitch && !tiles[3].SwitchBusy) tiles[3].SwitchOn = m.TidyOn;

            if (m.MailEmail == null) tile(4, "Not connected", "Connect your email in Settings", Theme.Accent);
            else if (m.MailLevel == 2) tile(4, "Signed out", "Connect again in Settings", CriticalColor);
            else if (m.MailLevel == 1) tile(4, "Last email failed", m.MailEmail, CautionColor);
            else tile(4, m.MailReports ? "On" : "Connected", m.MailEmail, SafeColor);
            if (m.MailEmail != null) tiles[4].Sub = m.MailLevel == 2 ? Lang.T("Connect again in Settings") : m.MailEmail;   // the address is never translated

            if (LinuxUi) tile(5, m.AptBytes <= 0 ? "Clean" : Fs.Fmt(m.AptBytes), m.AptBytes <= 0 ? "No downloaded update packages" : "Downloaded update packages, safe to clean", m.AptBytes > (256L << 20) ? CautionColor : SafeColor);
            else if (m.UndoCount == 0) tile(5, "Empty", "Removed items wait here before they are deleted for good", Theme.Accent);
            else tile(5, Fs.Fmt(m.UndoBytes), (m.UndoCount == 1 ? "1 item" : m.UndoCount + " items") + ", Tools > Undo puts them back", Theme.Accent);

            // folders to watch: the one that most needs a look
            if (tiles.Length > 6)
            {
                var w = m.Watch ?? new List<Growth.Watch>();
                var top = w.FirstOrDefault();
                if (!m.WatchHistory) tile(6, "Collecting data", "Folders that keep growing show up after two weeks", Theme.Accent);
                else if (top == null) tile(6, "All steady", "No folder keeps growing", SafeColor);
                else
                {
                    tile(6, top.Name, "", top.Due ? CautionColor : Theme.Accent);
                    int more = w.Count - 1;
                    tiles[6].Sub = Lang.T("+" + Fs.Fmt(Math.Max(0, top.PerMonth)) + " a month") + ". " + Lang.T(top.Due ? "Check it " + Growth.EveryText(top.EveryDays) : "Checked") +
                                   (more > 0 ? " · " + Lang.T("+" + more + " more") : "");
                    tiles[6].Invalidate();
                }
            }

            // quick wins
            long max = m.TopSafe.Count > 0 ? Math.Max(1, m.TopSafe.Max(i => i.Size)) : 1;
            cardWins.Rows = m.TopSafe.Select(i => new ListCard.Row { Text = Lang.T(i.Name), Right = Fs.Fmt(i.Size), Bar = i.Size / (double)max, BarColor = SafeColor, Action = Lang.T("Clean"), Tag = i }).ToList();
            cardWins.Empty = Lang.T(!m.Measured ? "Measuring..." : "The safe items are already clean.");
            cardWins.ActionsOn = !testMode && !m.Cleaning;
            cardWins.Invalidate();

            // other drives
            cardDrives.Rows = m.Drives.Select(d =>
            {
                double usedShare = d.Total > 0 ? 1 - d.Free / (double)d.Total : 0;
                var r = new ListCard.Row
                {
                    Text = Win.IsWindows ? d.Letter + ":  " + (d.Label.Length > 0 ? d.Label : Lang.T(d.Removable ? "USB drive" : "Local disk")) : d.Label + (d.Removable ? "  (USB)" : ""),
                    Right = Lang.T(Fs.Fmt(d.Free) + " free of " + Fs.Fmt(d.Total)),
                    Bar = usedShare, BarColor = usedShare > 0.9 ? CriticalColor : usedShare > 0.8 ? CautionColor : ReviewColor
                };
                if (d.Health == HealthLevel.Failing) { r.Sub = Lang.T("May fail soon: back up what is on it"); r.BarColor = CriticalColor; }
                else if (d.Health == HealthLevel.Warning) { r.Sub = Lang.T("Needs a look: see Disk health"); r.BarColor = CautionColor; }
                return r;
            }).ToList();
            cardDrives.Invalidate();

            // recent activity
            cardActivity.Rows = m.Recent.Select(a => new ListCard.Row
            {
                Text = Lang.T(a.Text), Sub = Lang.T(Ui.When(a.Time, m.Now)), BarColor = Color.DimGray,
                Dot = a.Kind == "clean" || a.Kind == "auto" ? SafeColor : a.Kind == "undo" ? ReviewColor : a.Kind == "mail" ? Theme.Accent : CautionColor
            }).ToList();
            cardActivity.Invalidate();

            UpdateDrivePicker(m.Drives);
        }
    }
}
