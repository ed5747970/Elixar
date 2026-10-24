// Tools > Space map: a picture of a drive where every folder is a box as big as the space it takes, colored by its warning
// sign (Critical, Caution, Review, Safe). Click a box to go inside, Back to go up. Read-only, except "Move to Undo..." on
// folders Elixar may touch. Links are never followed, so nothing is counted twice and nothing outside is reached.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class MapNode
    {
        public string Name = "", Path = "";
        public long Size;
        public bool IsDir, IsFiles;          // IsFiles: the files directly in a folder, as one box
        public int FileCount;
        public List<string> Biggest = new List<string>();   // IsFiles: the biggest files (up to 3), for the name and the warning sign
        public MapNode Parent;
        public List<MapNode> Children = new List<MapNode>();
    }

    public static class SpaceScan
    {
        public static long PruneBelow = 1L << 20;

        // the whole tree under root: folders with their sizes (files directly in a folder become one box)
        public static MapNode Scan(string root, Action<string, long> progress, CancellationToken ct)
        {
            var top = new MapNode { Name = root, Path = root, IsDir = true };
            long seen = 0; var last = DateTime.MinValue;
            var stack = new Stack<MapNode>(); stack.Push(top);
            var order = new List<MapNode>();
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var n = stack.Pop(); order.Add(n);
                DirectoryInfo di;
                try { di = new DirectoryInfo(n.Path); } catch { continue; }
                long files = 0; int fc = 0;
                var big = new List<KeyValuePair<long, string>>();
                try
                {
                    foreach (var f in di.EnumerateFiles())
                    {
                        try
                        {
                            if ((f.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            long len = f.Length; files += len; fc++;
                            if (big.Count < 3 || len > big[big.Count - 1].Key) { big.Add(new KeyValuePair<long, string>(len, f.FullName)); big = big.OrderByDescending(x => x.Key).Take(3).ToList(); }
                        }
                        catch { }
                    }
                }
                catch { }
                if (files > 0)
                {
                    var names = big.Select(x => System.IO.Path.GetFileName(x.Value)).ToList();
                    n.Children.Add(new MapNode { Name = fc <= 3 ? string.Join(", ", names) : fc + " files", Path = fc == 1 ? big[0].Value : n.Path, Size = files, IsFiles = true, FileCount = fc, Parent = n,
                                                 Biggest = big.Select(x => x.Value).ToList() });
                }
                seen += files;
                try
                {
                    foreach (var d in di.EnumerateDirectories())
                    {
                        try
                        {
                            if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // a junction or link: counted where it really is
                            var c = new MapNode { Name = d.Name, Path = d.FullName, IsDir = true, Parent = n };
                            n.Children.Add(c); stack.Push(c);
                        }
                        catch { }
                    }
                }
                catch { }
                if (progress != null && (DateTime.Now - last).TotalMilliseconds > 250) { last = DateTime.Now; progress(n.Path, seen); }
            }
            // sizes from the bottom up
            for (int i = order.Count - 1; i >= 0; i--)
            {
                var n = order[i];
                n.Size = n.Children.Sum(c => c.Size);
                n.Children = n.Children.Where(c => c.Size > 0).OrderByDescending(c => c.Size).ToList();
            }
            // small folders keep only their size (a whole drive has hundreds of thousands of them)
            foreach (var n in order) if (n.Size < PruneBelow && n.Parent != null) n.Children = new List<MapNode>();
            return top;
        }

        // each child's share of a ring (in degrees), biggest first; slivers under 0.8 degrees are left out (drawn together as "other")
        public static List<KeyValuePair<MapNode, float>> Sweeps(MapNode parent, float sweep)
        {
            var l = new List<KeyValuePair<MapNode, float>>();
            if (parent == null || parent.Size <= 0) return l;
            foreach (var n in parent.Children)
            {
                float sw = (float)(sweep * n.Size / (double)parent.Size);
                if (sw >= 0.8f) l.Add(new KeyValuePair<MapNode, float>(n, sw));
            }
            return l;
        }

        // the warning sign of a path: the nearest place on the Dashboard's map that holds it (none = "")
        public static string TierOf(string path, List<DashLoc> locs)
        {
            DashLoc best = null;
            foreach (var l in locs)
            {
                if (string.IsNullOrEmpty(l.Path)) continue;
                bool inside = string.Equals(path.TrimEnd('\\', '/'), l.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || DashData.Inside(path, l.Path);
                if (inside && (best == null || l.Path.Length > best.Path.Length)) best = l;
            }
            return best == null ? "" : best.Tier;
        }
    }

    // the drawing: a circle (sunburst): the folder shown in the middle, each ring one level deeper, each slice as wide as the space it takes
    public class TreeMap : Control
    {
        public MapNode Root;
        public List<DashLoc> Places = new List<DashLoc>();
        public MapNode Hover, Picked;
        public event Action<MapNode> Opened, PickedChanged;
        public event Action GoUp;
        public Func<string, Color> ColorFor;
        public float Scale_ = 1f;
        readonly ToolTip tip = new ToolTip();
        string lastTip = "";
        const int Rings = 3;

        class Slice { public MapNode Node; public int Depth; public float Start, Sweep; }
        readonly List<Slice> slices = new List<Slice>();
        readonly List<KeyValuePair<MapNode, Rectangle>> rows = new List<KeyValuePair<MapNode, Rectangle>>();
        PointF center; float r0, ringW;

        public TreeMap()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        int S(int v) { return (int)Math.Round(v * Scale_); }

        public void Show(MapNode n) { Root = n; Hover = null; Picked = null; Invalidate(); if (PickedChanged != null) PickedChanged(null); }

        Color Bg { get { return Theme.Panel; } }
        Color TierCol(MapNode n) { return ColorFor != null ? ColorFor(MapTier(n)) : Color.SteelBlue; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);
            slices.Clear(); rows.Clear();
            if (Root == null || Root.Size <= 0)
            {
                TextRenderer.DrawText(g, Lang.T("Click \"Map this drive\" to see what takes the space."), Font, ClientRectangle, Theme.Quiet,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int listW = Width >= S(760) ? Math.Min(S(380), (int)(Width * 0.36)) : 0;
            var drawArea = new Rectangle(0, 0, Width - listW, Height);
            PaintRings(g, drawArea);
            if (listW > 0) PaintList(g, new Rectangle(Width - listW, 0, listW, Height));
        }

        // ---------- rings ----------
        void PaintRings(Graphics g, Rectangle a)
        {
            float R = Math.Min(a.Width, a.Height) / 2f - S(14);
            if (R < S(60)) return;
            center = new PointF(a.X + a.Width / 2f, a.Y + a.Height / 2f);
            r0 = R * 0.34f; ringW = (R - r0) / Rings;
            Ring(g, Root, -90f, 360f, 1);
            // the labels of the first ring, where there is room
            using (var f = new Font(Font.FontFamily, Font.Size * 0.95f, FontStyle.Bold))
                foreach (var sl in slices.Where(x => x.Depth == 1 && x.Sweep >= 16f))
                {
                    double mid = (sl.Start + sl.Sweep / 2) * Math.PI / 180;
                    float rr = r0 + ringW / 2;
                    var p = new PointF(center.X + (float)Math.Cos(mid) * rr, center.Y + (float)Math.Sin(mid) * rr);
                    string name = Lang.T(sl.Node.Name);
                    var sz = TextRenderer.MeasureText(name, f);
                    float room = (float)(sl.Sweep * Math.PI / 180 * rr);
                    if (sz.Width > Math.Max(room, ringW * 1.6f)) continue;
                    TextRenderer.DrawText(g, name, f, new Rectangle((int)(p.X - sz.Width / 2), (int)(p.Y - sz.Height / 2), sz.Width + 2, sz.Height), Color.White, TextFormatFlags.NoPrefix);
                }
            // the middle: what the mouse is on, or the folder shown
            var m = Hover ?? Picked ?? Root;
            using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(52, 52, 62) : Color.FromArgb(244, 246, 250))) g.FillEllipse(b, center.X - r0 + S(4), center.Y - r0 + S(4), (r0 - S(4)) * 2, (r0 - S(4)) * 2);
            int w = (int)(r0 * 1.6f);
            using (var big = new Font(Font.FontFamily, Font.Size * 1.9f, FontStyle.Bold))
            using (var mid = new Font(Font.FontFamily, Font.Size * 1.05f, FontStyle.Bold))
            {
                string pct = m != Root && Root.Size > 0 ? Math.Round(m.Size * 100.0 / Root.Size) + "%" : "";
                TextRenderer.DrawText(g, Lang.T(m == Root ? (Root.Parent == null ? Root.Path : Root.Name) : m.Name), mid, new Rectangle((int)center.X - w / 2, (int)(center.Y - S(44)), w, S(22)), Theme.Ink,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, Fs.Fmt(m.Size), big, new Rectangle((int)center.X - w / 2, (int)(center.Y - S(20)), w, S(36)), m == Root ? Theme.Ink : TierCol(m),
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                string sub = m == Root ? (Root.Parent != null ? Lang.T("click the middle to go back") : Lang.T("double-click a ring to go inside")) : pct + "  ·  " + Lang.T(TierShort(MapTier(m)));
                TextRenderer.DrawText(g, sub, Font, new Rectangle((int)center.X - w / 2, (int)(center.Y + S(18)), w, S(20)), Theme.Quiet,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }
        }

        void Ring(Graphics g, MapNode parent, float start, float sweep, int depth)
        {
            if (depth > Rings || parent.Size <= 0) return;
            float a = start, other = sweep;
            foreach (var kv in SpaceScan.Sweeps(parent, sweep))
            {
                var n = kv.Key; float sw = kv.Value; other -= sw;
                var sl = new Slice { Node = n, Depth = depth, Start = a, Sweep = sw };
                slices.Add(sl);
                Color c = TierCol(n);
                c = Blend(c, Bg, Math.Min(0.6, (depth - 1) * 0.22 + (n.IsFiles ? 0.18 : 0)));
                if (n == Hover || n == Picked) c = Blend(c, Color.White, 0.22);
                Sector(g, c, depth, a, sw, n == Picked);
                if (n.IsDir && n.Children.Count > 0) Ring(g, n, a, sw, depth + 1);
                a += sw;
            }
            if (other > 0.3f) Sector(g, Blend(Theme.Grid, Bg, 0.2), depth, a, other, false);
        }

        void Sector(Graphics g, Color c, int depth, float start, float sweep, bool picked)
        {
            float ri = r0 + (depth - 1) * ringW, ro = ri + ringW - S(2);
            using (var path = new GraphicsPath())
            {
                path.AddArc(center.X - ro, center.Y - ro, ro * 2, ro * 2, start, sweep);
                path.AddArc(center.X - ri, center.Y - ri, ri * 2, ri * 2, start + sweep, -sweep);
                path.CloseFigure();
                using (var b = new SolidBrush(c)) g.FillPath(b, path);
                using (var p = new Pen(Bg, S(2) >= 2 ? 1.6f : 1f)) g.DrawPath(p, path);
                if (picked) using (var p = new Pen(Theme.Ink, 2.5f)) g.DrawPath(p, path);
            }
        }

        static string TierShort(string t) { return t == "critical" ? "Critical" : t == "caution" ? "Caution" : t == "review" ? "Review" : t == "safe" ? "Safe" : "Other"; }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = Math.Max(1, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // ---------- the list beside it: the biggest, with a bar each ----------
        void PaintList(Graphics g, Rectangle a)
        {
            using (var p = new Pen(Theme.Grid)) g.DrawLine(p, a.X, a.Y + S(10), a.X, a.Bottom - S(10));
            int x = a.X + S(18), w = a.Width - S(30), y = a.Y + S(14), rowH = S(40);
            using (var head = new Font(Font.FontFamily, Font.Size * 1.05f, FontStyle.Bold))
                TextRenderer.DrawText(g, Lang.T("Biggest here"), head, new Point(x, y), Theme.Ink, TextFormatFlags.NoPrefix);
            y += S(30);
            int max = Math.Max(1, (a.Bottom - y - S(8)) / rowH);
            var top = Root.Children.Take(max).ToList();
            long biggest = top.Count > 0 ? Math.Max(1, top[0].Size) : 1;
            using (var bold = new Font(Font.FontFamily, Font.Size, FontStyle.Bold))
                foreach (var n in top)
                {
                    var rr = new Rectangle(a.X + S(8), y - S(4), a.Width - S(14), rowH - S(2));
                    rows.Add(new KeyValuePair<MapNode, Rectangle>(n, rr));
                    if (n == Hover || n == Picked)
                        using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(56, 56, 68) : Color.FromArgb(238, 240, 247))) using (var path = Round(rr, S(6))) g.FillPath(b, path);
                    Color c = TierCol(n);
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y + S(4), S(10), S(10));
                    string size = Fs.Fmt(n.Size);
                    var sw = TextRenderer.MeasureText(size, bold).Width;
                    TextRenderer.DrawText(g, Lang.T(n.Name), Font, new Rectangle(x + S(18), y, w - S(18) - sw - S(8), S(18)), Theme.Ink, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(g, size, bold, new Point(x + w - sw, y), Theme.Ink, TextFormatFlags.NoPrefix);
                    int bw = Math.Max(S(3), (int)((w - S(18)) * (double)n.Size / biggest));
                    using (var b = new SolidBrush(Theme.Grid)) using (var path = Round(new Rectangle(x + S(18), y + S(21), w - S(18), S(6)), S(3))) g.FillPath(b, path);
                    using (var b = new SolidBrush(c)) using (var path = Round(new Rectangle(x + S(18), y + S(21), bw, S(6)), S(3))) g.FillPath(b, path);
                    y += rowH;
                }
        }

        // a folder: its own sign; the files in a folder: the most serious sign among its biggest files
        public string MapTier(MapNode n)
        {
            if (!n.IsFiles) return SpaceScan.TierOf(n.Path, Places);
            string worst = SpaceScan.TierOf(n.Parent != null ? n.Parent.Path : n.Path, Places);
            foreach (var f in n.Biggest)
            {
                string t = SpaceScan.TierOf(f, Places);
                if (Array.IndexOf(DashData.TierOrder, t) >= 0 && (Array.IndexOf(DashData.TierOrder, worst) < 0 || Array.IndexOf(DashData.TierOrder, t) < Array.IndexOf(DashData.TierOrder, worst))) worst = t;
            }
            return worst;
        }

        static Color Blend(Color a, Color b, double t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }

        // what is under the mouse; middle = the centre of the rings
        MapNode At(Point p, out bool middle)
        {
            middle = false;
            if (Root == null) return null;
            foreach (var kv in rows) if (kv.Value.Contains(p)) return kv.Key;
            double dx = p.X - center.X, dy = p.Y - center.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d < r0) { middle = true; return null; }
            int depth = (int)((d - r0) / Math.Max(1, ringW)) + 1;
            if (depth > Rings) return null;
            double ang = Math.Atan2(dy, dx) * 180 / Math.PI; if (ang < -90) ang += 360;
            var sl = slices.FirstOrDefault(x => x.Depth == depth && ang >= x.Start && ang < x.Start + x.Sweep);
            return sl == null ? null : sl.Node;
        }
        MapNode At(Point p) { bool m; return At(p, out m); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool middle; var h = At(e.Location, out middle);
            if (h != Hover) { Hover = h; Invalidate(); }
            string t = h == null ? "" : Lang.T(h.Name) + "\n" + Fs.Fmt(h.Size) + (Root.Size > 0 ? "  (" + Math.Round(h.Size * 100.0 / Root.Size) + "%)" : "") + "\n" + h.Path;
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t); }
            Cursor = (h != null && h.IsDir && h.Children.Count > 0) || (middle && Root != null && Root.Parent != null) ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover = null; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            bool middle; var h = At(e.Location, out middle);
            if (middle) { if (Root != null && Root.Parent != null && GoUp != null) GoUp(); return; }
            Picked = h; Invalidate();
            if (PickedChanged != null) PickedChanged(h);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            var h = At(e.Location);
            if (h != null && h.IsDir && h.Children.Count > 0 && Opened != null) Opened(h);
        }
    }

    public partial class MainForm
    {
        TabPage pageMap;
        TreeMap map;
        ComboBox mapDrive;
        Label mapPath, mapInfo;
        Button btnMapScan, btnMapUp, btnMapOpen, btnMapExplain, btnMapUndo;
        CancellationTokenSource mapCancel;
        public event Action MapDone;
        public MapNode MapRoot { get { return map == null ? null : map.Root; } }
        public static string MapRootOverride;   // self-test and previews
        public static Func<List<DashLoc>> MapPlacesOverride;   // previews
        public void MapPick(string name) { var n = map.Root == null ? null : map.Root.Children.FirstOrDefault(c => c.Name == name); map.Picked = n; map.Invalidate(); var h = n; btnMapOpen.Enabled = btnMapExplain.Enabled = h != null; mapInfo.Text = h == null ? "" : Lang.T(h.Name) + "  \u00B7  " + Fs.Fmt(h.Size) + "  \u00B7  " + Lang.T(TierWords(map.MapTier(h))); }
        public void MapOpen(string name) { var n = map.Root == null ? null : map.Root.Children.FirstOrDefault(c => c.Name == name); if (n != null) MapShow(n); }

        void BuildSpaceMap()
        {
            pageMap = new TabPage("Space map") { BackColor = pageTools.BackColor };
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Title("Space map"), 0, 0);
            var top = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, S(4)) };
            mapDrive = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(90), Margin = new Padding(0, S(3), S(8), 0) };
            try { foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed)) mapDrive.Items.Add(d.Name); } catch { }
            if (mapDrive.Items.Count == 0) mapDrive.Items.Add(Win.IsWindows ? @"C:\" : "/");
            mapDrive.SelectedIndex = Math.Max(0, mapDrive.Items.Cast<string>().ToList().FindIndex(x => x.StartsWith("C", StringComparison.OrdinalIgnoreCase)));
            btnMapScan = MakeButton("Map this drive"); btnMapScan.Font = new Font("Segoe UI Semibold", 9f);
            btnMapUp = MakeButton("Back up a level"); btnMapUp.Enabled = false;
            top.Controls.AddRange(new Control[] { mapDrive, btnMapScan, btnMapUp });
            // the legend: the warning signs
            foreach (var tier in new[] { "critical", "caution", "review", "safe", "" })
            {
                var sw = new Panel { Size = new Size(S(14), S(14)), BackColor = MapColor(tier), Margin = new Padding(S(14), S(9), S(4), 0) };
                top.Controls.Add(sw);
                top.Controls.Add(new Label { AutoSize = true, Text = tier == "" ? "Other" : tier == "critical" ? "Critical" : tier == "caution" ? "Caution" : tier == "review" ? "Review" : "Safe", Margin = new Padding(0, S(7), 0, 0) });
            }
            t.Controls.Add(top, 0, 1);
            mapPath = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(S(2), 0, 0, S(4)), UseMnemonic = false,
                                  Text = "" };
            t.Controls.Add(mapPath, 0, 2);
            map = new TreeMap { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0), ColorFor = MapColor, Scale_ = s };
            t.Controls.Add(map, 0, 3);
            var bottom = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, S(6), 0, 0) };
            mapInfo = new Label { AutoSize = true, Margin = new Padding(S(2), S(7), S(12), 0), UseMnemonic = false, MaximumSize = new Size(S(700), 0) };
            btnMapOpen = MakeButton("Open in Explorer"); btnMapExplain = MakeButton("Is this safe to delete?"); btnMapUndo = MakeButton("Move to Undo...");
            btnMapOpen.Enabled = btnMapExplain.Enabled = btnMapUndo.Enabled = false;
            bottom.Controls.AddRange(new Control[] { btnMapExplain, btnMapOpen, btnMapUndo, mapInfo });
            t.Controls.Add(bottom, 0, 4);
            pageMap.Controls.Add(t);

            btnMapScan.Click += (o, e) => MapScan();
            btnMapUp.Click += (o, e) => { if (map.Root != null && map.Root.Parent != null) MapShow(map.Root.Parent); };
            map.Opened += n => MapShow(n);
            map.GoUp += () => btnMapUp.PerformClick();
            map.PickedChanged += n =>
            {
                btnMapOpen.Enabled = btnMapExplain.Enabled = n != null;
                btnMapUndo.Enabled = n != null && !testMode && MapMayMove(n);
                mapInfo.Text = n == null ? "" : Lang.T(n.Name) + "  \u00B7  " + Fs.Fmt(n.Size) + "  \u00B7  " + Lang.T(TierWords(map.MapTier(n)));
            };
            btnMapOpen.Click += (o, e) => { var n = map.Picked; if (n != null) try { Shell.Select(n.IsFiles && n.Biggest.Count > 0 ? n.Biggest[0] : n.Path); } catch { } };
            btnMapExplain.Click += (o, e) => { var n = map.Picked; if (n != null) ExplainPath(n.IsFiles && n.Biggest.Count > 0 ? n.Biggest[0] : n.Path); };
            btnMapUndo.Click += (o, e) => MapToUndo(map.Picked);
        }

        static Color MapColor(string tier)
        {
            switch (tier)
            {
                case "critical": return Color.FromArgb(214, 76, 72);
                case "caution": return Color.FromArgb(230, 145, 45);
                case "review": return Color.FromArgb(132, 110, 214);
                case "safe": return Color.FromArgb(46, 160, 105);
                default: return Color.FromArgb(78, 128, 190);
            }
        }
        static string TierWords(string tier)
        {
            return tier == "critical" ? "Critical - never delete" : tier == "caution" ? "Caution - only with the right tool" : tier == "review" ? "Review - your decision" :
                   tier == "safe" ? "Safe - rebuilt automatically" : "Not on the Dashboard's map";
        }

        // Dashboard > Folder map comes here
        public void ShowSpaceMap(string root)
        {
            ShowTools(pageMap);
            if (MapRootOverride == null)
            {
                int i = mapDrive.Items.Cast<string>().ToList().FindIndex(x => string.Equals(x, root, StringComparison.OrdinalIgnoreCase));
                if (i < 0) { mapDrive.Items.Add(root); i = mapDrive.Items.Count - 1; }
                mapDrive.SelectedIndex = i;
            }
            string want = MapRootOverride ?? root;
            var top = map.Root; while (top != null && top.Parent != null) top = top.Parent;
            if (mapCancel == null && (top == null || !string.Equals(top.Path, want, StringComparison.OrdinalIgnoreCase))) MapScan();
        }

        public void MapScan()
        {
            if (mapCancel != null) { mapCancel.Cancel(); return; }
            string root = MapRootOverride ?? (string)mapDrive.SelectedItem;
            mapCancel = new CancellationTokenSource(); var ct = mapCancel.Token;
            btnMapScan.Text = Lang.T("Stop"); btnMapUp.Enabled = false;
            map.Places = MapPlacesOverride != null ? MapPlacesOverride() : dashLocs ?? DashData.Build();
            mapPath.Text = Lang.T("Mapping " + root + "...");
            Task.Factory.StartNew(() => SpaceScan.Scan(root, (p, b) => UI(() => mapPath.Text = Lang.T("Mapping: " + Fs.Fmt(b) + " so far") + "  \u00B7  " + p), ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ContinueWith(t => UI(() =>
                {
                    mapCancel = null; btnMapScan.Text = Lang.T("Map this drive");
                    if (t.IsCanceled || t.IsFaulted) { mapPath.Text = Lang.T(t.IsCanceled ? "Stopped." : "Could not map it: " + t.Exception.GetBaseException().Message); return; }
                    MapShow(t.Result);
                    if (MapDone != null) MapDone();
                }));
        }

        void MapShow(MapNode n)
        {
            map.Show(n);
            btnMapUp.Enabled = n.Parent != null;
            mapPath.Text = n.Path + "  \u00B7  " + Fs.Fmt(n.Size) + "  \u00B7  " + Lang.T(n.Children.Count == 1 ? "1 item" : n.Children.Count + " items") + "  \u00B7  " + Lang.T("double-click a ring to go inside");
        }

        // only what isn't Critical, isn't one of Windows' folders, and isn't the whole drive
        bool MapMayMove(MapNode n)
        {
            if (n == null || n.Parent == null || n.IsFiles) return false;
            string tier = SpaceScan.TierOf(n.Path, map.Places);
            if (tier == "critical") return false;
            try { if (Protect.IsProtected(n.Path)) return false; } catch { return false; }
            return true;
        }

        void MapToUndo(MapNode n)
        {
            if (n == null || testMode || !MapMayMove(n)) return;
            if (Lang.BoxNo(this, Lang.T("Move this folder to Undo?") + "\n\n" + n.Path + "\n" + Fs.Fmt(n.Size) + "\n\n" +
                    Lang.T("It is kept in Tools > Undo for " + TidyPrefs.Load().UndoDays + " days, then deleted for good. Only do this if you know what it is: click \"Is this safe to delete?\" first."), "Elixar") != DialogResult.Yes) return;
            btnMapUndo.Enabled = false;
            Task.Factory.StartNew(() => ChangeLog.Within("Space map", () => Quarantine.MoveToUndo("Space map: " + n.Name, new[] { n.Path }, Log, TidyPrefs.Load().UndoDays))).ContinueWith(t => UI(() =>
            {
                long moved = t.IsFaulted ? 0 : t.Result;
                Log(Lang.T("Space map: moved " + Fs.Fmt(moved) + " to Undo from " + n.Path));
                if (moved > 0 && n.Parent != null) { n.Parent.Children.Remove(n); for (var p = n.Parent; p != null; p = p.Parent) p.Size -= moved; MapShow(map.Root); }
            }));
        }
    }
}
