// The Smart scan window for one drive: duplicate files (you choose which copy stays) and files not used for months.
// Nothing is ticked for you. What you pick goes to the Recycle Bin, after you confirm.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class SmartScanForm : Form
    {
        readonly MainForm owner;
        readonly string root, title;
        readonly bool test;
        readonly Func<int, int> S;
        Label lblHead, lblSum, lblStatus;
        ProgressBar bar;
        Button btnStop, btnRescan;
        TabControl tabs;
        TabPage pDups, pOld, pSim, pEmpty;
        ListView lvDups, lvOld, lvSim, lvEmpty;
        CheckBox chkSimilar;
        Button btnSimPick, btnSimRecycle, btnEmptyAll, btnEmptyRemove, btnCompress;
        Label lblSimSel, lblEmptySel;
        ComboBox cmbKeep, cmbAge, cmbSize;
        Button btnDupPick, btnDupNone, btnDupRecycle, btnOldAll, btnOldNone, btnOldRecycle;
        Label lblDupSel, lblOldSel;
        CancellationTokenSource cts;
        SmartResult result;
        public event Action ScanDone;   // previews and the self-test
        public SmartResult Result { get { return result; } }
        public int DupRows { get { return lvDups.Items.Count; } }
        public int OldRows { get { return lvOld.Items.Count; } }
        public int SimilarRows { get { return lvSim.Items.Count; } }
        public int EmptyRows { get { return lvEmpty.Items.Count; } }
        public static Func<string, CancellationToken, SmartResult> RunOverride;   // previews

        static readonly Color SafeC = Color.FromArgb(0, 125, 70), ReviewC = Color.FromArgb(40, 90, 200), Quiet = Color.DimGray;

        public SmartScanForm(MainForm owner, string root, string title, float scale, bool testMode)
        {
            this.owner = owner; this.root = root; this.title = title; test = testMode;
            S = px => (int)Math.Round(px * scale);
            Text = Lang.T("Smart scan of " + title);
            Icon = Brand.Load(32); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f); Size = new Size(S(1080), S(720)); MinimumSize = new Size(S(760), S(520));
            BackColor = Color.FromArgb(246, 248, 251);
            Build();
            Shown += (o, e) => Start();
            FormClosing += (o, e) => { if (cts != null) cts.Cancel(); };
        }

        Button Btn(string t) { return new Button { Text = t, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(8), S(3), S(8), S(3)), Margin = new Padding(S(2), S(2), S(6), S(2)) }; }
        FlowLayoutPanel Row(params Control[] cs) { var f = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, S(4), 0, 0) }; f.Controls.AddRange(cs); return f; }

        void Build()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(S(14), S(10), S(14), S(10)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            lblHead = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Text = Lang.T("Smart scan of " + title), Margin = new Padding(0, 0, 0, S(2)) };
            lblSum = new Label { AutoSize = true, ForeColor = Quiet, MaximumSize = new Size(S(1040), 0), Margin = new Padding(S(1), 0, 0, S(6)) };
            bar = new ProgressBar { Style = ProgressBarStyle.Marquee, Width = S(220), Height = S(16), Margin = new Padding(S(2), S(6), S(8), 0) };
            lblStatus = new Label { AutoSize = true, ForeColor = Quiet, Margin = new Padding(0, S(6), S(8), 0) };
            btnStop = Btn("Stop"); btnRescan = Btn("Scan again"); btnRescan.Visible = false;
            var prog = Row(bar, btnStop, btnRescan, lblStatus);
            Load += (o, e) => { prog.Controls.Add(chkSimilar); };

            // duplicates
            pDups = new TabPage("Duplicate files") { BackColor = BackColor };
            cmbKeep = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240) };
            cmbKeep.Items.AddRange(new object[] { Lang.T("Keep the oldest copy"), Lang.T("Keep the newest copy"), Lang.T("Keep the copy with the shortest path") });
            cmbKeep.SelectedIndex = 0;
            btnDupPick = Btn("Select extra copies"); btnDupNone = Btn("Select none"); btnDupRecycle = Btn("Move selected to Recycle Bin...");
            btnDupRecycle.Font = new Font("Segoe UI Semibold", 9f);
            lblDupSel = new Label { AutoSize = true, ForeColor = Quiet, Margin = new Padding(S(6), S(8), 0, 0) };
            lvDups = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            lvDups.Columns.Add(Lang.T("File"), S(560)); lvDups.Columns.Add(Lang.T("Size"), S(90), HorizontalAlignment.Right); lvDups.Columns.Add(Lang.T("Changed"), S(100)); lvDups.Columns.Add("", S(170));
            var dt = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            dt.RowStyles.Add(new RowStyle(SizeType.AutoSize)); dt.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); dt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dt.Controls.Add(Row(new Label { AutoSize = true, Text = Lang.T("Which copy stays:"), Margin = new Padding(0, S(8), S(4), 0) }, cmbKeep, btnDupPick, btnDupNone), 0, 0);
            dt.Controls.Add(lvDups, 0, 1);
            dt.Controls.Add(Row(btnDupRecycle, lblDupSel), 0, 2);
            pDups.Controls.Add(dt);

            // not used lately
            pOld = new TabPage("Not used lately") { BackColor = BackColor };
            cmbAge = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(200) };
            cmbAge.Items.AddRange(new object[] { Lang.T("Not used for 6 months"), Lang.T("Not used for a year"), Lang.T("Not used for 2 years") });
            cmbAge.SelectedIndex = 0;
            cmbSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(140) };
            cmbSize.Items.AddRange(new object[] { Lang.T("10 MB and up"), Lang.T("100 MB and up"), Lang.T("1 GB and up") });
            cmbSize.SelectedIndex = 1;
            btnOldAll = Btn("Select all shown"); btnOldNone = Btn("Select none"); btnOldRecycle = Btn("Move selected to Recycle Bin...");
            btnOldRecycle.Font = new Font("Segoe UI Semibold", 9f);
            lblOldSel = new Label { AutoSize = true, ForeColor = Quiet, Margin = new Padding(S(6), S(8), 0, 0) };
            lvOld = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            lvOld.Columns.Add(Lang.T("File"), S(330)); lvOld.Columns.Add(Lang.T("Type"), S(90)); lvOld.Columns.Add(Lang.T("Size"), S(90), HorizontalAlignment.Right);
            lvOld.Columns.Add(Lang.T("Last used"), S(110)); lvOld.Columns.Add(Lang.T("Folder"), S(380));
            var ot = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            ot.RowStyles.Add(new RowStyle(SizeType.AutoSize)); ot.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); ot.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            ot.Controls.Add(Row(cmbAge, cmbSize, btnOldAll, btnOldNone), 0, 0);
            ot.Controls.Add(lvOld, 0, 1);
            btnCompress = Btn("Compress selected (keep them)...");
            ot.Controls.Add(Row(btnOldRecycle, btnCompress, lblOldSel), 0, 2);
            pOld.Controls.Add(ot);

            // similar photos
            pSim = new TabPage("Similar photos") { BackColor = BackColor };
            btnSimPick = Btn("Select all but the biggest"); var btnSimNone = Btn("Select none"); btnSimRecycle = Btn("Move selected to Recycle Bin...");
            btnSimRecycle.Font = new Font("Segoe UI Semibold", 9f);
            lblSimSel = new Label { AutoSize = true, ForeColor = Quiet, Margin = new Padding(S(6), S(8), 0, 0) };
            lvSim = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            lvSim.Columns.Add(Lang.T("File"), S(560)); lvSim.Columns.Add(Lang.T("Size"), S(90), HorizontalAlignment.Right); lvSim.Columns.Add(Lang.T("Changed"), S(100)); lvSim.Columns.Add("", S(170));
            var st = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            st.RowStyles.Add(new RowStyle(SizeType.AutoSize)); st.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); st.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            st.Controls.Add(Row(new Label { AutoSize = true, ForeColor = Quiet, Text = Lang.T("Photos that look alike but aren't exact copies (burst shots, resized or edited copies). Look before you pick: double-click shows a photo."), Margin = new Padding(0, S(8), S(8), 0), MaximumSize = new Size(S(560), 0) }, btnSimPick, btnSimNone), 0, 0);
            st.Controls.Add(lvSim, 0, 1); st.Controls.Add(Row(btnSimRecycle, lblSimSel), 0, 2);
            pSim.Controls.Add(st);
            btnSimNone.Click += (o, e) => { foreach (ListViewItem r in lvSim.Items) r.Checked = false; };

            // empty folders
            pEmpty = new TabPage("Empty folders") { BackColor = BackColor };
            btnEmptyAll = Btn("Select all shown"); var btnEmptyNone = Btn("Select none"); btnEmptyRemove = Btn("Remove selected empty folders...");
            btnEmptyRemove.Font = new Font("Segoe UI Semibold", 9f);
            lblEmptySel = new Label { AutoSize = true, ForeColor = Quiet, Margin = new Padding(S(6), S(8), 0, 0) };
            lvEmpty = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            lvEmpty.Columns.Add(Lang.T("Folder"), S(900));
            var et = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            et.RowStyles.Add(new RowStyle(SizeType.AutoSize)); et.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); et.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            et.Controls.Add(Row(new Label { AutoSize = true, ForeColor = Quiet, Text = Lang.T("Folders with no files in them (or only empty folders). They take no space but make your drive harder to sort. A folder is removed only if it is still empty."), Margin = new Padding(0, S(8), S(8), 0), MaximumSize = new Size(S(560), 0) }, btnEmptyAll, btnEmptyNone), 0, 0);
            et.Controls.Add(lvEmpty, 0, 1); et.Controls.Add(Row(btnEmptyRemove, lblEmptySel), 0, 2);
            pEmpty.Controls.Add(et);
            btnEmptyNone.Click += (o, e) => { foreach (ListViewItem r in lvEmpty.Items) r.Checked = false; };

            chkSimilar = new CheckBox { AutoSize = true, Checked = true, Text = Lang.T("Also look for similar photos (slower)"), Margin = new Padding(S(8), S(8), 0, 0) };

            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(12), S(4)) };
            tabs.TabPages.Add(pDups); tabs.TabPages.Add(pSim); tabs.TabPages.Add(pOld); tabs.TabPages.Add(pEmpty);

            t.Controls.Add(lblHead, 0, 0); t.Controls.Add(lblSum, 0, 1); t.Controls.Add(prog, 0, 2);
            t.Controls.Add(new Label { AutoSize = true, ForeColor = Quiet, MaximumSize = new Size(S(1040), 0), Margin = new Padding(S(1), S(4), 0, S(6)),
                Text = Lang.T("Nothing is ticked for you. What you pick goes to the Recycle Bin, so you can get it back. Windows, apps, games and AppData are never listed.") }, 0, 3);
            t.Controls.Add(tabs, 0, 4);
            Controls.Add(t);

            btnStop.Click += (o, e) => { if (cts != null) cts.Cancel(); };
            btnRescan.Click += (o, e) => Start();
            btnDupPick.Click += (o, e) => PickExtras();
            btnDupNone.Click += (o, e) => { foreach (ListViewItem r in lvDups.Items) r.Checked = false; };
            cmbKeep.SelectedIndexChanged += (o, e) => { if (result != null) { FillDups(); } };
            btnDupRecycle.Click += (o, e) => RecycleDups();
            lvDups.ItemChecked += (o, e) => UpdateSel();
            lvDups.DoubleClick += (o, e) => Reveal(lvDups);
            cmbAge.SelectedIndexChanged += (o, e) => FillOld();
            cmbSize.SelectedIndexChanged += (o, e) => FillOld();
            btnOldAll.Click += (o, e) => { foreach (ListViewItem r in lvOld.Items) r.Checked = true; };
            btnOldNone.Click += (o, e) => { foreach (ListViewItem r in lvOld.Items) r.Checked = false; };
            btnOldRecycle.Click += (o, e) => RecycleOld();
            lvOld.ItemChecked += (o, e) => UpdateSel();
            lvOld.DoubleClick += (o, e) => Reveal(lvOld);
            btnCompress.Click += (o, e) => CompressOld();
            btnSimPick.Click += (o, e) => { foreach (ListViewItem r in lvSim.Items) r.Checked = !"keep".Equals(r.SubItems[3].Tag); };
            btnSimRecycle.Click += (o, e) => RecycleSim();
            lvSim.ItemChecked += (o, e) => UpdateSel();
            lvSim.DoubleClick += (o, e) => { if (lvSim.SelectedItems.Count > 0) try { Shell.Open(((FileInfo)lvSim.SelectedItems[0].Tag).FullName); } catch { } };
            btnEmptyAll.Click += (o, e) => { foreach (ListViewItem r in lvEmpty.Items) r.Checked = true; };
            btnEmptyRemove.Click += (o, e) => RemoveEmpty();
            lvEmpty.ItemChecked += (o, e) => UpdateSel();
            lvEmpty.DoubleClick += (o, e) => { if (lvEmpty.SelectedItems.Count > 0) try { Shell.Open((string)lvEmpty.SelectedItems[0].Tag); } catch { } };
            SetBusy(true);
        }

        void SetBusy(bool busy)
        {
            bar.Visible = busy; btnStop.Visible = busy; btnRescan.Visible = !busy;
            foreach (var b in new[] { btnDupPick, btnDupNone, btnOldAll, btnOldNone }) b.Enabled = !busy;
            if (chkSimilar != null) chkSimilar.Enabled = !busy;
            if (btnSimPick != null) { btnSimPick.Enabled = btnEmptyAll.Enabled = !busy; }
            cmbKeep.Enabled = cmbAge.Enabled = cmbSize.Enabled = !busy;
            UpdateSel();
        }

        void UI(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch { } }

        public void Start()
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            lvDups.Items.Clear(); lvDups.Groups.Clear(); lvOld.Items.Clear(); lvSim.Items.Clear(); lvSim.Groups.Clear(); lvEmpty.Items.Clear();
            bool sim = chkSimilar == null || chkSimilar.Checked;
            lblSum.Text = Lang.T("Reading " + title + "...");
            SetBusy(true);
            string last = "";
            Task.Factory.StartNew(() => RunOverride != null ? RunOverride(root, token) :
                SmartScan.Run(root, s => { if (s != last) { last = s; UI(() => lblStatus.Text = Lang.T(s.Length > 110 ? s.Substring(0, 107) + "..." : s)); } }, token, false, sim),
                TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                cts = null;
                if (t.IsFaulted) { lblSum.Text = Lang.T("The scan stopped: ") + t.Exception.GetBaseException().Message; SetBusy(false); return; }
                result = t.Result;
                lblStatus.Text = "";
                FillDups(); FillOld(); FillSimilar(); FillEmpty();
                try { Settings.Set("SmartScanAt", DateTime.Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)); } catch { }
                Summarize();
                SetBusy(false);
                if (owner != null) owner.Log(Lang.T("Smart scan of " + title + " - " + result.Dups.Count + " sets of duplicates (" + Fs.Fmt(result.DupWaste) + " extra), " +
                                                   result.OlderThan(DateTime.Now.AddMonths(-6), SmartScan.OldMin).Count + " big files not used for 6 months."));
                if (ScanDone != null) ScanDone();
            }));
        }

        void Summarize()
        {
            var r = result;
            var old = r.OlderThan(Cutoff(), MinSize());
            var parts = new List<string>();
            parts.Add(Lang.T(r.HasWindows ? title + " has its own Windows installation: its Windows, apps and user AppData are skipped." : title + " is a data drive (no Windows on it): everything on it is yours to sort."));
            parts.Add(Lang.T(r.FilesSeen.ToString("N0") + " files read (" + Fs.Fmt(r.BytesSeen) + ")" + (r.Stopped ? ", stopped early." : ".")));
            parts.Add(Lang.T(r.Dups.Count == 0 ? "No duplicate files." : r.Dups.Count + " sets of duplicates: " + Fs.Fmt(r.DupWaste) + " in extra copies."));
            parts.Add(Lang.T(old.Count == 0 ? "Nothing unused lately." : old.Count + " files not used lately: " + Fs.Fmt(old.Sum(o => o.Size)) + "."));
            if (r.Similar.Count > 0) parts.Add(Lang.T(r.Similar.Count + " sets of similar photos: " + Fs.Fmt(r.SimilarWaste) + " besides the biggest of each."));
            if (r.EmptyFolders.Count > 0) parts.Add(Lang.T(r.EmptyFolders.Count + " empty folders."));
            if (!r.AccessTracked) parts.Add(Lang.T("Windows doesn't record when files are opened on this PC, so \"last used\" means last changed or copied here."));
            lblSum.Text = string.Join("  ", parts);
            pDups.Text = Lang.T("Duplicate files") + " (" + r.Dups.Count + ")";
            pOld.Text = Lang.T("Not used lately") + " (" + old.Count + ")";
            pSim.Text = Lang.T("Similar photos") + " (" + r.Similar.Count + ")";
            pEmpty.Text = Lang.T("Empty folders") + " (" + r.EmptyFolders.Count + ")";
        }

        DateTime Cutoff() { return DateTime.Now.AddMonths(cmbAge.SelectedIndex == 2 ? -24 : cmbAge.SelectedIndex == 1 ? -12 : -6); }
        long MinSize() { return cmbSize.SelectedIndex == 2 ? 1L << 30 : cmbSize.SelectedIndex == 1 ? 100L << 20 : 10L << 20; }
        SmartScan.Keep KeepRule() { return cmbKeep.SelectedIndex == 1 ? SmartScan.Keep.Newest : cmbKeep.SelectedIndex == 2 ? SmartScan.Keep.ShortestPath : SmartScan.Keep.Oldest; }

        void FillDups()
        {
            if (result == null) return;
            var was = new HashSet<string>(lvDups.CheckedItems.Cast<ListViewItem>().Select(r => ((FileInfo)r.Tag).FullName), StringComparer.OrdinalIgnoreCase);
            lvDups.BeginUpdate(); lvDups.Items.Clear(); lvDups.Groups.Clear();
            var rule = KeepRule();
            foreach (var g in result.Dups.Take(2000))
            {
                var keep = SmartScan.Keeper(g, rule);
                var lg = new ListViewGroup(Lang.T(g.Files.Count + " copies of " + Fs.Fmt(g.Size) + "  -  " + Fs.Fmt(g.Wasted) + " extra"), HorizontalAlignment.Left);
                lvDups.Groups.Add(lg);
                foreach (var f in g.Files)
                {
                    bool k = f == keep;
                    var lvi = new ListViewItem(f.FullName) { Tag = f, Group = lg, UseItemStyleForSubItems = false, ToolTipText = f.FullName, Checked = !k && was.Contains(f.FullName) };
                    lvi.SubItems.Add(Fs.Fmt(f.Length));
                    lvi.SubItems.Add(f.LastWriteTime.ToString("yyyy-MM-dd"));
                    var s = lvi.SubItems.Add(Lang.T(k ? "stays" : "extra copy"));
                    s.ForeColor = k ? SafeC : ReviewC; s.Tag = k ? "keep" : "extra";
                    lvDups.Items.Add(lvi);
                }
            }
            lvDups.EndUpdate();
            UpdateSel();
        }

        void PickExtras() { foreach (ListViewItem r in lvDups.Items) r.Checked = !"keep".Equals(r.SubItems[3].Tag); }

        void FillOld()
        {
            if (result == null) return;
            lvOld.BeginUpdate(); lvOld.Items.Clear();
            foreach (var o in result.OlderThan(Cutoff(), MinSize()).Take(5000))
            {
                var lvi = new ListViewItem(o.File.Name) { Tag = o.File, ToolTipText = o.File.FullName };
                lvi.SubItems.Add(Lang.T(o.Kind));
                lvi.SubItems.Add(Fs.Fmt(o.Size));
                lvi.SubItems.Add(o.LastUsed.ToString("yyyy-MM-dd"));
                lvi.SubItems.Add(o.File.DirectoryName ?? "");
                lvOld.Items.Add(lvi);
            }
            lvOld.EndUpdate();
            if (result != null) Summarize();
            UpdateSel();
        }

        static long Sum(IEnumerable<ListViewItem> rows) { return rows.Sum(r => { try { var f = r.Tag as FileInfo; return f == null ? 0L : f.Length; } catch { return 0L; } }); }

        void UpdateSel()
        {
            var d = lvDups.CheckedItems.Cast<ListViewItem>().ToList(); var o = lvOld.CheckedItems.Cast<ListViewItem>().ToList();
            lblDupSel.Text = d.Count == 0 ? "" : Lang.T(d.Count + " selected (" + Fs.Fmt(Sum(d)) + ")");
            lblOldSel.Text = o.Count == 0 ? "" : Lang.T(o.Count + " selected (" + Fs.Fmt(Sum(o)) + ")");
            var sm = lvSim.CheckedItems.Cast<ListViewItem>().ToList(); var em = lvEmpty.CheckedItems.Cast<ListViewItem>().ToList();
            lblSimSel.Text = sm.Count == 0 ? "" : Lang.T(sm.Count + " selected (" + Fs.Fmt(Sum(sm)) + ")");
            lblEmptySel.Text = em.Count == 0 ? "" : Lang.T(em.Count + " selected");
            bool idle = cts == null;
            btnSimRecycle.Enabled = idle && sm.Count > 0 && !test;
            btnEmptyRemove.Enabled = idle && em.Count > 0 && !test;
            btnCompress.Enabled = idle && o.Count > 0 && !test;
            btnDupRecycle.Enabled = idle && d.Count > 0 && !test;
            btnOldRecycle.Enabled = idle && o.Count > 0 && !test;
        }

        void Reveal(ListView lv)
        {
            if (lv.SelectedItems.Count == 0) return;
            try { Shell.Select(((FileInfo)lv.SelectedItems[0].Tag).FullName); } catch { }
        }

        void FillSimilar()
        {
            if (result == null) return;
            lvSim.BeginUpdate(); lvSim.Items.Clear(); lvSim.Groups.Clear();
            foreach (var g in result.Similar.Take(1000))
            {
                var lg = new ListViewGroup(Lang.T(g.Count + " similar photos"), HorizontalAlignment.Left);
                lvSim.Groups.Add(lg);
                for (int i = 0; i < g.Count; i++)
                {
                    var f = g[i];
                    var lvi = new ListViewItem(f.FullName) { Tag = f, Group = lg, UseItemStyleForSubItems = false, ToolTipText = f.FullName };
                    long len = 0; try { len = f.Length; } catch { }
                    lvi.SubItems.Add(Fs.Fmt(len)); lvi.SubItems.Add(f.LastWriteTime.ToString("yyyy-MM-dd"));
                    var k = lvi.SubItems.Add(Lang.T(i == 0 ? "stays (the biggest)" : "similar"));
                    k.ForeColor = i == 0 ? SafeC : ReviewC; k.Tag = i == 0 ? "keep" : "extra";
                    lvSim.Items.Add(lvi);
                }
            }
            lvSim.EndUpdate();
        }

        void FillEmpty()
        {
            if (result == null) return;
            lvEmpty.BeginUpdate(); lvEmpty.Items.Clear();
            foreach (var d in result.EmptyFolders.Take(5000)) lvEmpty.Items.Add(new ListViewItem(d) { Tag = d, ToolTipText = d });
            lvEmpty.EndUpdate();
        }

        void RecycleSim()
        {
            var sel = lvSim.CheckedItems.Cast<ListViewItem>().ToList();
            var all = sel.GroupBy(r => r.Group).Where(g => g.Count() == g.Key.Items.Count).Select(g => g.Key.Header).ToList();
            if (all.Count > 0) { Lang.Box(this, Lang.T("Keep at least one copy in each set. Untick one file in:") + "\n\n" + string.Join("\n", all.Take(10)), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Recycle(sel, lvSim);
        }

        void RemoveEmpty()
        {
            var sel = lvEmpty.CheckedItems.Cast<ListViewItem>().ToList();
            if (test || sel.Count == 0) return;
            if (Lang.Box(this, Lang.T("Remove " + sel.Count + " empty folders?") + "\n\n" + Lang.T("Only folders that are still empty are removed."), "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int ok = 0;
            foreach (var r in sel) if (SmartScan.RemoveEmpty((string)r.Tag)) { ok++; lvEmpty.Items.Remove(r); }
            string msg = "Removed " + ok + " of " + sel.Count + " empty folders.";
            lblStatus.Text = Lang.T(msg);
            if (owner != null) owner.Log(Lang.T("Smart scan of " + title + " - " + msg));
            UpdateSel();
        }

        void CompressOld()
        {
            var sel = lvOld.CheckedItems.Cast<ListViewItem>().ToList();
            if (test || sel.Count == 0) return;
            var can = sel.Where(r => SmartScan.Compressible(SmartScan.KindOf(((FileInfo)r.Tag).Name)) && SmartScan.OnNtfs(((FileInfo)r.Tag).FullName)).ToList();
            if (can.Count == 0) { Lang.Box(this, Lang.T("Videos, photos, music and archives are already compressed, and only NTFS drives can compress files. Pick documents, installers, disk images or backups."), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (Lang.Box(this, Lang.T("Compress " + can.Count + " files (" + Fs.Fmt(Sum(can)) + ")?") + "\n\n" +
                    Lang.T("They stay where they are and open as usual (Windows unpacks them on the fly). How much is saved depends on the file.") +
                    (can.Count < sel.Count ? "\n\n" + Lang.T((sel.Count - can.Count) + " videos, photos, music files or archives are skipped: they don't get smaller.") : ""),
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cursor = Cursors.WaitCursor; btnCompress.Enabled = false;
            var paths = can.Select(r => ((FileInfo)r.Tag).FullName).ToList();
            Task.Factory.StartNew(() => SmartScan.Compress(paths, null), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                Cursor = Cursors.Default;
                long saved = t.IsFaulted ? 0 : t.Result;
                string msg = "Compressed " + paths.Count + " files: " + Fs.Fmt(saved) + " saved.";
                lblStatus.Text = Lang.T(msg);
                if (owner != null) owner.Log(Lang.T("Smart scan of " + title + " - " + msg));
                foreach (var r in can) r.Checked = false;
                UpdateSel();
            }));
        }

        void RecycleDups()
        {
            var sel = lvDups.CheckedItems.Cast<ListViewItem>().ToList();
            // never every copy of a set
            var all = sel.GroupBy(r => r.Group).Where(g => g.Count() == g.Key.Items.Count).Select(g => g.Key.Header).ToList();
            if (all.Count > 0) { Lang.Box(this, Lang.T("Keep at least one copy in each set. Untick one file in:") + "\n\n" + string.Join("\n", all.Take(10)), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Recycle(sel, lvDups);
        }

        void RecycleOld() { Recycle(lvOld.CheckedItems.Cast<ListViewItem>().ToList(), lvOld); }

        void Recycle(List<ListViewItem> sel, ListView lv)
        {
            if (test || sel.Count == 0) return;
            var usb = sel.Where(r => Duplicates.OnUsb(((FileInfo)r.Tag).FullName)).ToList();
            string usbNote = usb.Count == 0 ? "" : "\n\n" + Lang.T(usb.Count + " of them are on a USB drive, which has no Recycle Bin: those are deleted for good.");
            if (Lang.Box(this, Lang.T("Move " + sel.Count + " files (" + Fs.Fmt(Sum(sel)) + ") to the Recycle Bin?") + "\n\n" + Lang.T("You can get them back from the Recycle Bin.") + usbNote,
                    "Elixar", MessageBoxButtons.OKCancel, usb.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;
            int ok = 0; long bytes = 0;
            Cursor = Cursors.WaitCursor;
            foreach (var r in sel)
            {
                var f = (FileInfo)r.Tag; long len = 0; try { len = f.Length; } catch { }
                if (Duplicates.Recycle(f.FullName)) { ok++; bytes += len; lv.Items.Remove(r); }
            }
            Cursor = Cursors.Default;
            string msg = "Moved " + ok + " of " + sel.Count + " files (" + Fs.Fmt(bytes) + ") to the Recycle Bin. Empty the Recycle Bin to free the space.";
            lblStatus.Text = Lang.T(msg);
            if (owner != null) { owner.Log(Lang.T("Smart scan of " + title + " - " + msg)); try { Activity.Add("clean", "Smart scan: " + ok + " files from " + title + " to the Recycle Bin", bytes); } catch { } }
            UpdateSel();
        }
    }
}
