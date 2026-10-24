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
        public RectangleF Box;               // where it is drawn now
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

        // the squarified layout: boxes as close to squares as they can be, biggest first
        public static void Layout(List<MapNode> items, RectangleF r)
        {
            var list = items.Where(i => i.Size > 0).OrderByDescending(i => i.Size).ToList();
            foreach (var i in items) i.Box = RectangleF.Empty;
            double total = list.Sum(i => (double)i.Size);
            if (total <= 0 || r.Width < 1 || r.Height < 1) return;
            double scale = r.Width * (double)r.Height / total;
            int start = 0; var rect = r;
            while (start < list.Count)
            {
                float side = Math.Min(rect.Width, rect.Height);
                var row = new List<MapNode> { list[start] }; double rowArea = list[start].Size * scale;
                double worst = Worst(row, rowArea, side, scale);
                int k = start + 1;
                while (k < list.Count)
                {
                    var tryRow = new List<MapNode>(row) { list[k] };
                    double a = rowArea + list[k].Size * scale, w = Worst(tryRow, a, side, scale);
                    if (w > worst) break;
                    row = tryRow; rowArea = a; worst = w; k++;
                }
                // place the row along the shorter side
                bool alongWidth = rect.Width >= rect.Height;
                float thick = (float)(rowArea / side);
                float pos = alongWidth ? rect.Y : rect.X;
                foreach (var n in row)
                {
                    float len = (float)(n.Size * scale / thick);
                    n.Box = alongWidth ? new RectangleF(rect.X, pos, thick, len) : new RectangleF(pos, rect.Y, len, thick);
                    pos += len;
                }
                rect = alongWidth ? new RectangleF(rect.X + thick, rect.Y, Math.Max(0, rect.Width - thick), rect.Height)
                                  : new RectangleF(rect.X, rect.Y + thick, rect.Width, Math.Max(0, rect.Height - thick));
                start = k;
            }
        }

        static double Worst(List<MapNode> row, double area, float side, double scale)
        {
            if (area <= 0) return double.MaxValue;
            double s2 = side * (double)side, a2 = area * area, w = 0;
            foreach (var n in row) { double r = n.Size * scale; if (r <= 0) continue; w = Math.Max(w, Math.Max(s2 * r / a2, a2 / (s2 * r))); }
            return w;
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

    // the drawing
    public class TreeMap : Control
    {
        public MapNode Root;
        public List<DashLoc> Places = new List<DashLoc>();
        public MapNode Hover, Picked;
        public event Action<MapNode> Opened, PickedChanged;
        public Func<string, Color> ColorFor;
        readonly ToolTip tip = new ToolTip();
        string lastTip = "";

        public TreeMap()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public void Show(MapNode n) { Root = n; Hover = null; Picked = null; Invalidate(); if (PickedChanged != null) PickedChanged(null); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Root == null || Root.Size <= 0) return;
            var area = new RectangleF(1, 1, Width - 3, Height - 3);
            SpaceScan.Layout(Root.Children, area);
            g.SmoothingMode = SmoothingMode.None;
            foreach (var n in Root.Children)
            {
                if (n.Box.Width < 1 || n.Box.Height < 1) continue;
                string tier = MapTier(n);
                Color c = ColorFor != null ? ColorFor(tier) : Color.SteelBlue;
                if (n.IsFiles) c = Blend(c, BackColor, 0.2);
                if (n == Hover) c = Blend(c, Color.White, 0.25);
                var r = Rectangle.Round(n.Box);
                using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)), Blend(c, Color.White, 0.18), Blend(c, Color.Black, 0.12), 45f))
                    g.FillRectangle(b, r);
                // the folders inside, as faint lines (a hint of what's in it)
                if (n.IsDir && r.Width > 40 && r.Height > 40 && n.Children.Count > 1)
                {
                    SpaceScan.Layout(n.Children, new RectangleF(r.X + 3, r.Y + 20, r.Width - 6, r.Height - 23));
                    using (var p = new Pen(Color.FromArgb(70, 255, 255, 255)))
                        foreach (var cc in n.Children) if (cc.Box.Width >= 3 && cc.Box.Height >= 3) g.DrawRectangle(p, Rectangle.Round(cc.Box));
                }
                using (var p = new Pen(BackColor, 1)) g.DrawRectangle(p, r);
                if (n == Picked && r.Width > 6 && r.Height > 6)
                {
                    using (var p = new Pen(Color.White, 3)) g.DrawRectangle(p, Rectangle.Inflate(r, -2, -2));
                    using (var p = new Pen(Color.FromArgb(20, 20, 30), 1)) g.DrawRectangle(p, Rectangle.Inflate(r, -4, -4));
                }
                if (r.Width > 60 && r.Height > 18)
                {
                    string text = Lang.T(n.Name) + "  " + Fs.Fmt(n.Size);
                    TextRenderer.DrawText(g, text, Font, new Rectangle(r.X + 4, r.Y + 2, r.Width - 8, 18), Color.White,
                        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | (Lang.Rtl ? TextFormatFlags.Right : 0));
                }
            }
            // the boxes are laid out again for the mouse (the inner hint changed the children's boxes)
            SpaceScan.Layout(Root.Children, area);
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

        MapNode At(Point p) { return Root == null ? null : Root.Children.FirstOrDefault(n => n.Box.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = At(e.Location);
            if (h != Hover) { Hover = h; Invalidate(); }
            string t = h == null ? "" : Lang.T(h.Name) + "\n" + Fs.Fmt(h.Size) + (Root.Size > 0 ? "  (" + Math.Round(h.Size * 100.0 / Root.Size) + "%)" : "") + "\n" + h.Path;
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t); }
            Cursor = h != null && h.IsDir && h.Children.Count > 0 ? Cursors.Hand : Cursors.Default;
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Hover = null; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var h = At(e.Location);
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
                                  Text = "Click \"Map this drive\". Big boxes take the most space; double-click a box to go inside." };
            t.Controls.Add(mapPath, 0, 2);
            map = new TreeMap { Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 32, 40), Font = new Font("Segoe UI", 9f), Margin = new Padding(0), ColorFor = MapColor };
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
                case "critical": return Color.FromArgb(178, 60, 56);
                case "caution": return Color.FromArgb(205, 128, 30);
                case "review": return Color.FromArgb(120, 100, 190);
                case "safe": return Color.FromArgb(46, 140, 90);
                default: return Color.FromArgb(70, 110, 160);
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
            mapPath.Text = n.Path + "  \u00B7  " + Fs.Fmt(n.Size) + "  \u00B7  " + Lang.T(n.Children.Count == 1 ? "1 box" : n.Children.Count + " boxes") + "  \u00B7  " + Lang.T("double-click a box to go inside");
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
