// Dashboard drive switcher: C: (the full overview), any other drive (what fills it), or all drives side by side.
// Everything here only reads and measures; opening a folder or a file shows it in Explorer.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    // one drive in "All drives"; the whole card is a button
    public class DriveCard : PaintedCard
    {
        public string Title = "", Line1 = "", Line2 = "", Line3 = "", Center = "", CenterSub = "";
        public double UsedShare = -1;
        public Color LevelColor = Color.Gray, HealthColor = Color.Empty;
        public Font TitleFont, CenterFont, SmallFont;
        bool hover;

        public DriveCard() { Cursor = Cursors.Hand; }

        static Color Track { get { return Theme.Dark ? Color.FromArgb(62, 62, 76) : Color.FromArgb(232, 234, 241); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color lvl = Theme.MapFore(LevelColor);
            Card(g, hover ? Mix(Theme.Panel, Theme.Accent, Theme.Dark ? 0.08 : 0.04) : Theme.Panel, hover ? Theme.Accent : Theme.Grid);
            int pad = S(16), ring = Math.Min(Height - 2 * pad, S(92));
            var rr = M(new Rectangle(pad, (Height - ring) / 2, ring, ring));
            float th = S(9);
            var arc = new RectangleF(rr.X + th / 2, rr.Y + th / 2, ring - th, ring - th);
            using (var p = new Pen(Track, th)) g.DrawEllipse(p, arc);
            if (UsedShare >= 0)
                using (var p = new Pen(lvl, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, arc, -90, (Rtl ? -1 : 1) * (float)(360 * Math.Max(0.01, Math.Min(1, UsedShare))));
            var cf = CenterFont ?? Font; var sf = SmallFont ?? Font; var tf = TitleFont ?? Font;
            int ch = cf.Height + sf.Height;
            TextRenderer.DrawText(g, Center, cf, new Rectangle(rr.X, rr.Y + (ring - ch) / 2, ring, cf.Height), Theme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, CenterSub, sf, new Rectangle(rr.X, rr.Y + (ring - ch) / 2 + cf.Height, ring, sf.Height), Theme.Quiet,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | (Rtl ? TextFormatFlags.RightToLeft : 0));

            int x = pad + ring + S(18), w = Width - x - pad;
            int y = (Height - (tf.Height + Font.Height + sf.Height + S(14) + (Line3.Length > 0 ? sf.Height + S(4) : 0))) / 2;
            Txt(g, Title, tf, new Rectangle(x, y, w, tf.Height + 2), Theme.Ink, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            y += tf.Height + S(6);
            Txt(g, Line1, Font, new Rectangle(x, y, w, Font.Height), Theme.Ink, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            y += Font.Height + S(8);
            int tx = x;
            if (HealthColor != Color.Empty)
            {
                int d = S(8);
                using (var b = new SolidBrush(Theme.MapFore(HealthColor))) g.FillEllipse(b, M(new Rectangle(x, y + (sf.Height - d) / 2, d, d)));
                tx = x + S(14);
            }
            Txt(g, Line2, sf, new Rectangle(tx, y, Width - tx - pad, sf.Height), Theme.Quiet, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Line3.Length > 0)
                Txt(g, Line3, sf, new Rectangle(x, y + sf.Height + S(4), w, sf.Height), Theme.Quiet, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
    }

    public partial class MainForm
    {
        public static Func<List<DriveRow>> DrivesOverride;   // previews and the self-test only
        Segmented drivePick;
        List<string> pickKeys = new List<string> { "C", "*" };
        string curDrive = "C";
        List<DriveRow> knownDrives = new List<DriveRow>();
        TableLayoutPanel dashDrive, dashAll, allGrid;
        HeroCard dHero, aHero;
        ListCard dFolders, dFiles;
        CancellationTokenSource folderCts, filesCts;
        readonly Dictionary<string, List<Treemap.Node>> folderCache = new Dictionary<string, List<Treemap.Node>>();
        readonly Dictionary<string, List<Scanner.BigFile>> filesCache = new Dictionary<string, List<Scanner.BigFile>>();
        string folderMeasuring;
        readonly HashSet<string> readFailing = new HashSet<string>();   // failing drives you asked Elixar to measure anyway
        public event Action DriveViewDone;
        static readonly Color[] DrivePalette = { Color.FromArgb(91, 76, 196), Color.FromArgb(40, 90, 200), Color.FromArgb(18, 140, 150), Color.FromArgb(190, 105, 0), Color.FromArgb(150, 160, 175) };

        // the line under the drive picker: "Disk 0 of ELISAR-PC  ·  Windows 11 Pro  ·  Updated 21:58"
        string statusTail = "";
        string StatusText()
        {
            string host = Lang.T(curDrive == "*" ? DiskNumber.Host : DiskNumber.Tag(Win.IsWindows ? curDrive : RootOf(curDrive)));
            return host + "   \u00b7   " + OsInfo.Current.Name + (statusTail.Length > 0 ? "   \u00b7   " + statusTail : "");
        }
        public string DashStatusText { get { return dashStatus.Text; } }   // self-test

        public string CurrentDrive { get { return curDrive; } }
        public int DriveChoices { get { return pickKeys.Count; } }
        public int DriveFolderRows { get { return dFolders == null ? 0 : dFolders.Rows.Count; } }
        public int AllDriveCards { get { return allGrid == null ? 0 : allGrid.Controls.Count; } }

        Control BuildDrivePicker()
        {
            drivePick = new Segmented { Options = new[] { PickC(), Lang.T("All drives") }, Scale_ = s, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 0, 0, S(4)) };
            drivePick.FitWidth();
            drivePick.Changed += () => SelectDrive(pickKeys[Math.Min(drivePick.Selected, pickKeys.Count - 1)]);
            return drivePick;
        }

        // how a drive is named: "D:" on Windows; on Linux its name ("Home", "Backup") or the folder it is mounted at
        string DT(string key)
        {
            if (Win.IsWindows) return key + ":";
            if (key == "C") return Lang.T("System disk");
            var d = knownDrives.FirstOrDefault(x => x.Letter == key);
            return d != null && d.Label.Length > 0 ? d.Label : key;
        }
        // "D:  Data" on Windows; "Backup  (/media/you/Backup)" on Linux
        string DriveName(DriveRow r)
        {
            if (Win.IsWindows) return r.Letter + ":" + (r.Label.Length > 0 ? "  " + r.Label : "");
            return r.Letter == "C" ? Lang.T("System disk") + "  (/)" : DT(r.Letter) + (r.Root.Length > 0 && r.Root != DT(r.Letter) ? "  (" + r.Root + ")" : "");
        }

        static string PickC() { return Win.IsWindows ? "C:  " + Lang.T("Windows") : Lang.T("System disk"); }

        string RootOf(string key)
        {
            if (key != "C" && key != "*")
            {
                var d = knownDrives.FirstOrDefault(x => x.Letter == key);
                if (d != null) return d.Root.Length > 0 ? d.Root : key + ":\\";
            }
            return Win.IsWindows ? @"C:\" : "/";
        }
        string CurRoot() { return RootOf(curDrive); }

        void OpenMap(string root)
        {
            var mf = new TreemapForm(root, s);
            mf.Shown += (o2, e2) => { Theme.Apply(mf); Lang.Apply(mf); };
            mf.Show(this);
        }

        void ShowInFolder(string path)
        {
            try { Shell.Select(path); } catch (Exception ex) { Log("Could not open the folder: " + ex.Message); }
        }

        // ---------------- building the two extra views ----------------
        void BuildDriveViews()
        {
            var bold = new Font("Segoe UI Semibold", 9.5f); var titleFont = new Font("Segoe UI Semibold", 10.5f); var small = new Font("Segoe UI", 8.5f);
            Func<HeroCard> hero_ = () => new HeroCard
            {
                Dock = DockStyle.Fill, Margin = new Padding(S(5)), Scale_ = s,
                HeadFont = new Font("Segoe UI Semibold", 16f), BigFont = new Font("Segoe UI Semibold", 15f), BoldFont = bold,
                SmallFont = new Font("Segoe UI", 8.75f), Font = new Font("Segoe UI", 10f)
            };

            // one drive: the ring, what fills it, its biggest folders and its largest files
            dashDrive = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0, 0, 0, S(4)), AutoScroll = true, Visible = false };
            dashDrive.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); dashDrive.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            dashDrive.RowStyles.Add(new RowStyle(SizeType.Absolute, S(206))); dashDrive.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            dashDrive.AutoScrollMinSize = new Size(0, S(560));
            dHero = hero_();
            dHero.PrimaryOn = true;
            dHero.PrimaryClicked += () => OpenDrill(CurRoot());
            dHero.SecondaryClicked += () => OpenMap(CurRoot());
            dFolders = new ListCard { Dock = DockStyle.Fill, Margin = new Padding(S(5)), Scale_ = s, TitleFont = titleFont, SmallFont = small, Footer = Lang.T("Open the folder map") };
            dFiles = new ListCard { Dock = DockStyle.Fill, Margin = new Padding(S(5)), Scale_ = s, TitleFont = titleFont, SmallFont = small };
            dFolders.ActionClicked += r => { var n = (Treemap.Node)r.Tag; if (n.IsDir) OpenDrill(n.Path); else if (!n.Group) ShowInFolder(n.Path); else OpenDrill(n.Path); };
            dFolders.FooterClicked += () =>
            {
                var cur = knownDrives.FirstOrDefault(x => x.Letter == curDrive);
                if (cur != null && cur.Health == HealthLevel.Failing && !readFailing.Contains(curDrive) && !folderCache.ContainsKey(curDrive))
                {
                    if (Lang.BoxNo(this, DT(curDrive) + " may fail soon. Measuring reads through the whole drive, which strains it. Back up first if you can.\n\nMeasure it now?", "Elixar") != DialogResult.Yes) return;
                    readFailing.Add(curDrive);
                    ShowDrive(curDrive, true);
                    return;
                }
                OpenMap(CurRoot());
            };
            dFiles.ActionClicked += r => ShowInFolder((string)r.Tag);
            dFiles.FooterClicked += () => FindDriveFiles(curDrive);
            dashDrive.Controls.Add(dHero, 0, 0); dashDrive.SetColumnSpan(dHero, 2);
            dashDrive.Controls.Add(dFolders, 0, 1); dashDrive.Controls.Add(dFiles, 1, 1);

            // all drives: one summary, then a card per drive
            dashAll = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0, 0, 0, S(4)), Visible = false };
            dashAll.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dashAll.RowStyles.Add(new RowStyle(SizeType.Absolute, S(206))); dashAll.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            aHero = hero_();
            aHero.PrimaryOn = true;
            aHero.PrimaryClicked += () => ShowMove();
            allGrid = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0), AutoScroll = true };
            allGrid.Resize += (o, e) => { int cols = allGrid.ClientSize.Width >= S(900) ? 3 : 2; if (cols != allGrid.ColumnCount && allGrid.Controls.Count > 0) RefreshAllDrives(); };
            dashAll.Controls.Add(aHero, 0, 0); dashAll.Controls.Add(allGrid, 0, 1);

            dashHost.Controls.Add(dashDrive);
            dashHost.Controls.Add(dashAll);
        }

        // ---------------- switching ----------------
        void ShowDashArea()
        {
            bool c = curDrive == "C";
            if (dashOverview == null || dashDetails == null) return;
            dashOverview.Visible = c && dashView.Selected == 0;
            dashDetails.Visible = c && dashView.Selected == 1;
            if (dashDrive != null) dashDrive.Visible = !c && curDrive != "*";
            if (dashAll != null) dashAll.Visible = curDrive == "*";
            if (dashView != null) dashView.Visible = c;
        }

        public void SelectDrive(string key)
        {
            if (!pickKeys.Contains(key)) key = "C";
            if (filesCts != null && key != curDrive) { filesCts.Cancel(); filesCts = null; }
            if (folderCts != null && key != curDrive) { folderCts.Cancel(); folderCts = null; folderMeasuring = null; }
            curDrive = key;
            int i = pickKeys.IndexOf(key);
            if (drivePick.Selected != i) { drivePick.Selected = i; drivePick.Invalidate(); }
            ShowDashArea();
            if (dashStatus != null) dashStatus.Text = StatusText();
            if (key == "*") RefreshAllDrives();
            else if (key != "C") ShowDrive(key, false);
        }

        // F5 and the Refresh button: whatever is showing
        void RefreshCurrentDrive()
        {
            if (curDrive == "C") { RefreshDashboard(); return; }
            if (curDrive != "*") { folderCache.Remove(curDrive); ShowDrive(curDrive, true); }
            RefreshOverview();   // reads the drives again, then redraws this view
        }

        // the choices: C:, each other drive, All drives
        void UpdateDrivePicker(List<DriveRow> others)
        {
            knownDrives = others ?? new List<DriveRow>();
            var keys = new List<string> { "C" };
            keys.AddRange(knownDrives.Select(d => d.Letter));
            keys.Add("*");
            var labels = new List<string> { PickC() };
            Func<string, string> cut = x => x.Length > 12 ? x.Substring(0, 11) + "\u2026" : x;
            labels.AddRange(knownDrives.Select(d => Win.IsWindows ? d.Letter + ":" + (d.Label.Length > 0 ? "  " + cut(d.Label) : "") : cut(DT(d.Letter))));
            labels.Add(Lang.T("All drives"));
            bool changed = !keys.SequenceEqual(pickKeys) || !labels.SequenceEqual(drivePick.Options);
            pickKeys = keys;
            if (changed) { drivePick.Options = labels.ToArray(); drivePick.FitWidth(); }
            if (!pickKeys.Contains(curDrive)) { SelectDrive("C"); return; }
            int i = pickKeys.IndexOf(curDrive);
            if (drivePick.Selected != i || changed) { drivePick.Selected = i; drivePick.Invalidate(); }
            if (curDrive == "*") RefreshAllDrives();
            else if (curDrive != "C") ShowDriveHero(curDrive);
        }

        // ---------------- one drive ----------------
        static string KindText(DriveRow d)
        {
            var parts = new List<string>();
            if (d.Label.Length > 0) parts.Add(d.Label);
            if (!d.Media.StartsWith("USB")) parts.Add(Lang.T(d.Removable ? "USB drive" : "Local disk"));
            if (d.Media.Length > 0) parts.Add(Lang.T(d.Media));
            if (d.Health != HealthLevel.Unknown) parts.Add(Lang.T(HealthWord(d.Health)));
            return string.Join("  \u00b7  ", parts);
        }
        static string HealthWord(HealthLevel h) { return h == HealthLevel.Failing ? "Back up now!" : h == HealthLevel.Warning ? "Needs a look" : h == HealthLevel.Healthy ? "Healthy" : "Unknown"; }
        static Color HealthColorOf(HealthLevel h) { return h == HealthLevel.Failing ? CriticalColor : h == HealthLevel.Warning ? CautionColor : h == HealthLevel.Healthy ? SafeColor : Color.Empty; }

        void ShowDriveHero(string letter)
        {
            var d = knownDrives.FirstOrDefault(x => x.Letter == letter);
            if (d == null) return;
            int level = DashSummary.LevelOf(d.Free, d.Total);
            dHero.UsedShare = d.Total > 0 ? 1 - d.Free / (double)d.Total : -1;
            dHero.LevelColor = Theme.MapFore(LevelTint(level));
            dHero.Big = Fs.Fmt(d.Free);
            dHero.BigSub = Lang.T("free of " + Fs.Fmt(d.Total));
            dHero.Headline = Lang.T(DT(letter) + " " + (level == 2 ? "is almost full" : level == 1 ? "is getting full" : "has plenty of room"));
            var lines = new List<string> { KindText(d) + "." };
            if (d.Health == HealthLevel.Failing) lines.Add(Lang.T("This drive may fail soon: back up what is on it."));
            else if (level == 0) lines.Add(Lang.T("Room to spare: a good place for big folders moved off C:."));
            else lines.Add(Lang.T("The biggest folders below show what fills it."));
            if (folderMeasuring != null) lines.Add(Lang.T("Measuring " + folderMeasuring + "..."));
            dHero.Sub = string.Join(" ", lines);
            dHero.Primary = Lang.T("Explore " + DT(letter));
            dHero.Secondary = Lang.T("Folder map");
            dHero.BreakTitle = Lang.T("What fills " + DT(letter));
            List<Treemap.Node> nodes;
            dHero.Break = new List<Tuple<string, long, Color>>();
            if (folderCache.TryGetValue(letter, out nodes) || (nodes = lastFolders) != null)
            {
                int k = 0;
                foreach (var n in nodes.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).Take(4))
                    dHero.Break.Add(Tuple.Create(FolderName(n), n.Bytes, Theme.MapFore(DrivePalette[k++ % 4])));
                if (dHero.Break.Count > 0) dHero.Break.Add(Tuple.Create(Lang.T("Free"), d.Free, Theme.Dark ? Color.FromArgb(150, 150, 170) : Color.FromArgb(160, 168, 182)));
            }
            dHero.BreakTotal = d.Total;
            dHero.Invalidate();
        }
        List<Treemap.Node> lastFolders;
        Font[] cardFonts;   // made once: the cards are rebuilt on every refresh

        static string FolderName(Treemap.Node n)
        {
            if (n.Group) return Lang.T(n.Name);
            if (n.Name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return Lang.T("Recycle Bin");
            return n.Name;
        }

        void ShowDrive(string letter, bool remeasure)
        {
            var d = knownDrives.FirstOrDefault(x => x.Letter == letter);
            if (d == null) { SelectDrive("C"); return; }
            lastFolders = null;
            dFolders.Title = Lang.T("Biggest folders on " + DT(letter));
            dFiles.Title = Lang.T("Largest files on " + DT(letter));
            List<Treemap.Node> nodes;
            if (!remeasure && folderCache.TryGetValue(letter, out nodes)) { folderMeasuring = null; ShowFolders(letter, nodes); }
            else if (d.Health == HealthLevel.Failing && !readFailing.Contains(letter))
            {
                // reading a whole drive that may fail can make it fail sooner: back up first, measure only when asked
                folderMeasuring = null;
                dFolders.Rows = new List<ListCard.Row>();
                dFolders.Empty = Lang.T("This drive may fail soon, so Elixar doesn't read through it on its own. Back up what is on it first.");
                dFolders.Footer = Lang.T("Measure it anyway");
                dFolders.Invalidate();
            }
            else MeasureFolders(letter, d.RootPath());
            List<Scanner.BigFile> files;
            if (filesCts == null)
            {
                if (filesCache.TryGetValue(letter, out files)) ShowFiles(letter, files);
                else
                {
                    dFiles.Rows = new List<ListCard.Row>();
                    dFiles.Empty = Lang.T("Not looked for yet. Finding the largest files reads the whole drive and takes a few minutes; nothing is changed.");
                    dFiles.Footer = Lang.T("Find the largest files");
                    dFiles.Invalidate();
                }
            }
            ShowDriveHero(letter);
        }

        void MeasureFolders(string letter, string root)
        {
            if (folderCts != null) folderCts.Cancel();
            folderCts = new CancellationTokenSource();
            var tok = folderCts.Token;
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            folderMeasuring = DT(letter);
            dFolders.Rows = new List<ListCard.Row>(); dFolders.Empty = Lang.T("Measuring..."); dFolders.Invalidate();
            Task.Factory.StartNew(() => Treemap.Read(root, sizes, (measuring, sofar) =>
            {
                var copy = sofar.ToList();
                UI(() =>
                {
                    if (tok.IsCancellationRequested || curDrive != letter) return;
                    if (measuring != null) folderMeasuring = measuring;
                    lastFolders = copy;
                    ShowFolders(letter, copy);
                    ShowDriveHero(letter);
                });
            }, tok), tok, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith(t => UI(() =>
            {
                if (tok.IsCancellationRequested) return;
                folderCts = null; folderMeasuring = null;
                var nodes = t.IsFaulted || t.IsCanceled ? new List<Treemap.Node>() : t.Result;
                folderCache[letter] = nodes;
                if (curDrive == letter) { ShowFolders(letter, nodes); ShowDriveHero(letter); }
                Log("Dashboard: measured the folders on " + letter + ":.");
                if (DriveViewDone != null) DriveViewDone();
            }));
        }

        void ShowFolders(string letter, List<Treemap.Node> nodes)
        {
            if (curDrive != letter) return;
            var list = nodes.Where(n => n.Bytes > 0).OrderByDescending(n => n.Bytes).ToList();
            long max = list.Count > 0 ? Math.Max(1, list[0].Bytes) : 1;
            int k = 0;
            dFolders.Rows = list.Select(n => new ListCard.Row
            {
                Text = FolderName(n), Right = Fs.Fmt(n.Bytes), Bar = n.Bytes / (double)max,
                BarColor = k < 4 ? DrivePalette[k++] : DrivePalette[4], Action = Lang.T(n.IsDir || n.Group ? "Open" : "Show"), Tag = n
            }).ToList();
            dFolders.Empty = Lang.T(folderMeasuring != null ? "Measuring..." : "This drive is empty.");
            dFolders.Footer = Lang.T("Open the folder map");
            dFolders.Invalidate();
        }

        // the largest files: only when you ask, because it reads the whole drive
        void FindDriveFiles(string letter)
        {
            if (filesCts != null) { filesCts.Cancel(); filesCts = null; dFiles.Footer = Lang.T("Find the largest files"); dFiles.Empty = Lang.T("Stopped."); dFiles.Invalidate(); return; }
            var d = knownDrives.FirstOrDefault(x => x.Letter == letter);
            if (d == null) return;
            if (d.Health == HealthLevel.Failing &&
                Lang.BoxNo(this, DT(letter) + " may fail soon. Finding the largest files reads through the whole drive, which strains it. Back up first if you can.\n\nLook through it now?", "Elixar") != DialogResult.Yes) return;
            filesCts = new CancellationTokenSource();
            var cts = filesCts; var tok = cts.Token;
            string root = d.RootPath();
            dFiles.Rows = new List<ListCard.Row>();
            dFiles.Empty = Lang.T("Looking through " + DT(letter) + " ...");
            dFiles.Footer = Lang.T("Stop");
            dFiles.Invalidate();
            Task.Factory.StartNew(() => Scanner.LargestFiles(root, 50L << 20, 60, dir => UI(() =>
            {
                if (tok.IsCancellationRequested || curDrive != letter || dFiles.Rows.Count > 0) return;
                dFiles.Empty = Lang.T("Looking through " + DT(letter) + " ...") + "\n" + (dir.Length > 70 ? "..." + dir.Substring(dir.Length - 67) : dir);
                dFiles.Invalidate();
            }), tok), tok, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith(t => UI(() =>
            {
                if (filesCts == cts) filesCts = null;
                if (t.IsCanceled || tok.IsCancellationRequested) return;
                var files = t.IsFaulted ? new List<Scanner.BigFile>() : t.Result;
                filesCache[letter] = files;
                Log("Dashboard: " + files.Count + " files over 50 MB on " + letter + ":.");
                if (curDrive == letter) ShowFiles(letter, files);
            }));
        }

        void ShowFiles(string letter, List<Scanner.BigFile> files)
        {
            dFiles.Rows = files.Select(f => new ListCard.Row
            {
                Text = Path.GetFileName(f.Path), Right = Fs.Fmt(f.Bytes), Sub = Path.GetDirectoryName(f.Path) ?? "", BarColor = Color.DimGray,
                Action = Lang.T("Show"), Tag = f.Path
            }).ToList();
            dFiles.Empty = Lang.T("No files over 50 MB.");
            dFiles.Footer = Lang.T("Look again");
            dFiles.Invalidate();
        }

        // ---------------- all drives ----------------
        List<DriveRow> AllDriveRows()
        {
            var rows = new List<DriveRow>();
            long free = dashSum != null && dashSum.Total > 0 ? dashSum.Free : Sys.Free(), total = dashSum != null && dashSum.Total > 0 ? dashSum.Total : Sys.Total();
            var c = new DriveRow { Letter = "C", Label = Win.IsWindows ? Lang.T("Windows") : "", Free = free, Total = total, Root = Win.IsWindows ? @"C:\" : "/" };
            var rep = lastDiskReport;
            if (rep != null && rep.Error == null) { var disk = rep.Disks.FirstOrDefault(x => x.HoldsC); if (disk != null) { c.Health = disk.Level; c.Media = disk.Kind; } }
            rows.Add(c);
            rows.AddRange(knownDrives);
            return rows;
        }

        void RefreshAllDrives()
        {
            if (allGrid == null) return;
            var rows = AllDriveRows().Where(r => r.Total > 0).ToList();
            long free = rows.Sum(r => r.Free), total = rows.Sum(r => r.Total);
            int n = rows.Count;

            aHero.UsedShare = total > 0 ? 1 - free / (double)total : -1;
            aHero.LevelColor = Theme.MapFore(LevelTint(DashSummary.LevelOf(free, total)));
            aHero.Big = total > 0 ? Fs.Fmt(free) : "...";
            aHero.BigSub = Lang.T(n == 1 ? "free on 1 drive" : "free on " + n + " drives");
            aHero.Headline = Lang.T("All your drives");
            var lines = new List<string>();
            foreach (var bad in rows.Where(r => r.Health == HealthLevel.Failing)) lines.Add(Lang.T(DT(bad.Letter) + " may fail soon: back up what is on it."));
            if (total > 0) lines.Add(Lang.T(Fs.Fmt(free) + " free of " + Fs.Fmt(total) + " across " + n + (n == 1 ? " drive." : " drives.")));
            var best = rows.Where(r => r.Letter != "C" && r.Health != HealthLevel.Failing).OrderByDescending(r => r.Free).FirstOrDefault();
            var cRow = rows.FirstOrDefault(r => r.Letter == "C");
            bool cFull = cRow != null && DashSummary.LevelOf(cRow.Free, cRow.Total) >= 1;
            if (best != null) lines.Add(Lang.T("The most room is on " + DT(best.Letter) + " (" + Fs.Fmt(best.Free) + " free)."));
            bool offer = Win.IsWindows && cFull && best != null && best.Free >= (20L << 30);
            if (offer) lines.Add(Lang.T("C: is getting full: big folders can move to " + best.Letter + ":."));
            else if (best == null) lines.Add(Lang.T(Win.IsWindows ? "C: is the only drive. A second drive or a USB drive gives you room to move big folders off C:." : "Your system disk is the only disk. A second disk or a USB drive gives you room for big folders."));
            aHero.Sub = string.Join(" ", lines);
            aHero.Primary = offer ? Lang.T("Move files off C:") : "";
            aHero.Secondary = "";
            aHero.BreakTitle = Lang.T("Used space by drive");
            aHero.Break = new List<Tuple<string, long, Color>>();
            var byUse = rows.OrderByDescending(r => r.Total - r.Free).ToList();
            for (int i = 0; i < byUse.Count && i < 4; i++)
                aHero.Break.Add(Tuple.Create(DriveName(byUse[i]), byUse[i].Total - byUse[i].Free, Theme.MapFore(DrivePalette[i])));
            if (byUse.Count > 4) aHero.Break.Add(Tuple.Create(Lang.T("Other drives"), byUse.Skip(4).Sum(r => r.Total - r.Free), Theme.MapFore(DrivePalette[4])));
            if (total > 0) aHero.Break.Add(Tuple.Create(Lang.T("Free"), free, Theme.Dark ? Color.FromArgb(150, 150, 170) : Color.FromArgb(160, 168, 182)));
            aHero.BreakTotal = total;
            aHero.Invalidate();

            // a card per drive
            int cols = allGrid.ClientSize.Width >= S(900) || allGrid.ClientSize.Width == 0 ? 3 : 2, rowsN = (n + cols - 1) / cols;
            allGrid.SuspendLayout();
            foreach (Control old in allGrid.Controls.Cast<Control>().ToList()) { allGrid.Controls.Remove(old); old.Dispose(); }
            allGrid.ColumnStyles.Clear(); allGrid.RowStyles.Clear();
            allGrid.ColumnCount = cols; allGrid.RowCount = Math.Max(1, rowsN) + 1;
            for (int i = 0; i < cols; i++) allGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            for (int i = 0; i < rowsN; i++) allGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, S(132)));
            allGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            if (cardFonts == null) cardFonts = new[] { new Font("Segoe UI Semibold", 11.5f), new Font("Segoe UI Semibold", 12f), new Font("Segoe UI", 8.5f), new Font("Segoe UI", 9.5f) };
            Font tf = cardFonts[0], cf = cardFonts[1], sf = cardFonts[2];
            for (int i = 0; i < n; i++)
            {
                var r = rows[i];
                int level = DashSummary.LevelOf(r.Free, r.Total);
                var card = new DriveCard
                {
                    Dock = DockStyle.Fill, Margin = new Padding(S(5)), Scale_ = s, TitleFont = tf, CenterFont = cf, SmallFont = sf, Font = cardFonts[3],
                    Title = DriveName(r),
                    Line1 = Lang.T(Fs.Fmt(r.Free) + " free of " + Fs.Fmt(r.Total)),
                    Line2 = KindText(new DriveRow { Removable = r.Removable, Media = r.Media, Health = r.Health }),
                    Line3 = Lang.T(DiskNumber.Tag(Win.IsWindows ? r.Letter : r.Root)),
                    Center = Math.Round(r.Free * 100.0 / r.Total) + "%", CenterSub = Lang.T("free"),
                    UsedShare = 1 - r.Free / (double)r.Total, LevelColor = LevelTint(level), HealthColor = HealthColorOf(r.Health),
                    AccessibleName = DT(r.Letter)
                };
                string key = r.Letter;
                card.Click += (o, e) => SelectDrive(key);
                allGrid.Controls.Add(card, i % cols, i / cols);
            }
            allGrid.ResumeLayout();
        }
    }

    static class DriveRowExt
    {
        // where a drive starts: its own folder when known, else "D:\"
        public static string RootPath(this DriveRow d) { return d.Root.Length > 0 ? d.Root : d.Letter + ":\\"; }
    }
}
