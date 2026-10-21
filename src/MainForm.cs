// The main window: tabs, the Cleaner list, measuring and cleaning.

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
    public partial class MainForm : Form
    {
        readonly List<CleanItem> items;
        readonly Dictionary<CleanItem, ListViewItem> rows = new Dictionary<CleanItem, ListViewItem>();
        readonly float s;
        readonly object logLock = new object();
        readonly string logFile;
        bool ready;
        readonly bool testMode;
        public event Action MeasureDone;
        ScanSnapshot lastScan;
        readonly string snapFile;
        Label lblScan;

        // Linux: only the parts that make sense there (the self-test can ask for the Windows layout on a Linux test machine)
        public static bool ForceWindowsUi;
        bool LinuxUi { get { return Lx.On && !ForceWindowsUi; } }

        // dashboard
        public event Action DashboardDone;
        TabControl tabs;
        TabPage pageDash, pageClean;
        DriveBar driveBar;
        readonly Dictionary<string, TierCard> cards = new Dictionary<string, TierCard>();
        readonly Dictionary<string, ListViewGroup> dashGroups = new Dictionary<string, ListViewGroup>();
        readonly Dictionary<string, ListViewGroup> fileGroups = new Dictionary<string, ListViewGroup>();
        ListView dashList, filesList;
        Label dashHeader, dashStatus, dashNote, filesStatus;
        Button btnDashRefresh, btnFindFiles;
        List<DashLoc> dashLocs;
        bool dashBusy, filesBusy;
        static readonly Color CriticalColor = Color.FromArgb(199, 54, 47);
        static readonly Color OtherColor = Color.FromArgb(150, 160, 175);
        static readonly Color FreeColor = Color.FromArgb(236, 239, 244);

        ListView list;
        Label lblTop, lblInfo;
        CheckBox chkRestore;
        Button btnMeasure, btnSafe, btnNone, btnScan, btnClean, btnPlan;
        ProgressBar progress;
        TextBox logBox;

        static readonly Color SafeColor = Color.FromArgb(0, 125, 70);
        static readonly Color ReviewColor = Color.FromArgb(40, 90, 200);
        static readonly Color CautionColor = Color.FromArgb(190, 105, 0);

        int S(int px) { return (int)Math.Round(px * s); }

        public MainForm() : this(false) { }

        public MainForm(bool testMode)
        {
            this.testMode = testMode;
            items = Catalog.Build();
            logFile = Paths.FileIn("cleaner-log.txt");
            // the self-test window never reads or overwrites your saved scan
            snapFile = testMode ? null : Paths.FileIn("last-scan.txt");
            if (snapFile != null) lastScan = ScanSnapshot.Load(snapFile);

            using (var g = CreateGraphics()) s = Math.Max(1f, g.DpiX / 96f);

            Text = testMode ? "Elixar - TEST MODE (cleaning is switched off)" : LinuxUi ? "Elixar - disk care" : "Elixar - drive C: care";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(S(1120), wa.Width), Math.Min(S(880), wa.Height));
            MinimumSize = new Size(Math.Min(S(780), wa.Width), Math.Min(S(560), wa.Height));
            try { Icon = Brand.Load(32); } catch { }

            lblTop = new Label
            {
                Dock = DockStyle.Top,
                Height = S(38),
                Padding = new Padding(S(12), S(12), S(12), 0),
                Font = new Font("Segoe UI Semibold", 11f),
                Text = "Scanning..."
            };
            lblScan = new Label
            {
                Dock = DockStyle.Top,
                Height = S(26),
                Padding = new Padding(S(13), S(2), S(12), S(4)),
                ForeColor = Color.DimGray,
                Text = lastScan == null ? "First scan on this PC." : "Previous scan: " + ScanSnapshot.FmtWhen(lastScan.Time)
            };

            list = new ListView
            {
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                Dock = DockStyle.Fill
            };
            list.Columns.Add("Item", S(280));
            list.Columns.Add("Level", S(80));
            list.Columns.Add("Size", S(90), HorizontalAlignment.Right);
            list.Columns.Add("Change", S(110), HorizontalAlignment.Right);
            list.Columns.Add("Location", S(500));

            var gSafe = new ListViewGroup("Safe - rebuilt automatically", HorizontalAlignment.Left);
            var gReview = new ListViewGroup("Review - your decision", HorizontalAlignment.Left);
            var gCaution = new ListViewGroup("Caution - leftovers, read the note first", HorizontalAlignment.Left);
            var gPrivacy = new ListViewGroup("Privacy - traces of what you did (never ticked for you)", HorizontalAlignment.Left);
            list.Groups.AddRange(new[] { gSafe, gReview, gCaution, gPrivacy });
            var driveGroups = new Dictionary<string, ListViewGroup>();
            Func<string, ListViewGroup> DriveGroup = d =>
            {
                ListViewGroup g;
                if (!driveGroups.TryGetValue(d, out g)) { g = new ListViewGroup("Drive " + d + " - your other drive (nothing is ticked for you)", HorizontalAlignment.Left); driveGroups[d] = g; list.Groups.Add(g); }
                return g;
            };

            foreach (var it in items)
            {
                var lvi = new ListViewItem(Lang.T(it.Name)) { UseItemStyleForSubItems = false, Checked = it.DefaultOn, Tag = it };
                var tierSub = lvi.SubItems.Add(Lang.T(it.Tier.ToString()));
                tierSub.ForeColor = it.Tier == Tier.Safe ? SafeColor : it.Tier == Tier.Review ? ReviewColor : CautionColor;
                lvi.SubItems.Add("...");
                lvi.SubItems.Add("");
                var locSub = lvi.SubItems.Add(it.Where);
                locSub.ForeColor = Color.DimGray;
                lvi.Group = it.Drive != null ? DriveGroup(it.Drive) : it.Privacy ? gPrivacy : it.Tier == Tier.Safe ? gSafe : it.Tier == Tier.Review ? gReview : gCaution;
                if (it.Privacy) tierSub.Text = Lang.T("Privacy");
                list.Items.Add(lvi);
                rows[it] = lvi;
            }

            // bottom area
            var bottom = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = S(300),
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(S(10), S(6), S(10), S(10))
            };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, S(44)));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, S(22)));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            lblInfo = new Label { Dock = DockStyle.Fill, Text = "Click an item to see what it removes." };

            chkRestore = new CheckBox { AutoSize = true, Checked = !LinuxUi, Visible = !LinuxUi, Text = "Create a restore point first (recommended)", Margin = new Padding(S(2), S(2), 0, S(4)) };

            var btnRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            btnMeasure = MakeButton("Rescan (F5)");
            btnSafe = MakeButton("Select all Safe");
            btnNone = MakeButton("Select none");
            btnScan = MakeButton("Save scan for dashboard");
            var btnCleanApps = MakeButton("Remove apps...");
            btnCleanApps.Click += (o, e) => ShowStoreTab();
            btnPlan = MakeButton("Free space for me...");
            btnPlan.Click += (o, e) => OnPlan();
            flow.Controls.AddRange(new Control[] { btnPlan, btnMeasure, btnSafe, btnNone, btnScan, btnCleanApps });
            if (LinuxUi) { btnPlan.Visible = false; btnCleanApps.Visible = false; }   // the Dashboard's "Free ... safely" does this on Linux; apps are removed with the App Center
            btnClean = MakeButton("Clean selected");
            btnClean.Font = new Font("Segoe UI Semibold", 9.5f);
            btnClean.Padding = new Padding(S(18), S(4), S(18), S(4));
            btnClean.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnRow.Controls.Add(flow, 0, 0);
            btnRow.Controls.Add(btnClean, 1, 0);

            progress = new ProgressBar { Dock = DockStyle.Fill, Margin = new Padding(S(2), S(6), S(2), S(2)) };

            logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9f),
                BackColor = SystemColors.Window
            };

            bottom.Controls.Add(lblInfo, 0, 0);
            bottom.Controls.Add(chkRestore, 0, 1);
            bottom.Controls.Add(btnRow, 0, 2);
            bottom.Controls.Add(progress, 0, 3);
            bottom.Controls.Add(logBox, 0, 4);

            tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(14), S(5)) };
            pageDash = new TabPage("Dashboard") { BackColor = Color.FromArgb(246, 248, 251) };
            pageClean = new TabPage("Cleaner");

            // docking order: fill first, then edges
            pageClean.Controls.Add(list);
            pageClean.Controls.Add(bottom);
            pageClean.Controls.Add(lblScan);
            pageClean.Controls.Add(lblTop);

            BuildDashboard();
            BuildApps();
            BuildStoreApps();
            appsTabs.TabPages.Add(pageStore);
            appsTabs.SelectedIndexChanged += (o, e) => { if (appsTabs.SelectedTab == pageStore && !storeLoaded) RefreshStoreApps(); };
            BuildAutomation();
            BuildTools();
            BuildPhone();
            BuildSettingsTab();
            BuildLogsTab();
            BuildPolicyTab();
            BuildEmailClean();
            BuildLinuxTab();
            BuildOneDriveTab();
            tabs.TabPages.Add(pageDash);
            if (!LinuxUi) tabs.TabPages.Add(pageApps);
            tabs.TabPages.Add(pageClean);
            tabs.TabPages.Add(pageTools);
            if (!LinuxUi) tabs.TabPages.Add(pagePhone);
            tabs.TabPages.Add(pageEmail);
            if (!LinuxUi && odAccounts.Count > 0) tabs.TabPages.Add(pageOneDrive);   // only when OneDrive is signed in on this PC
            tabs.TabPages.Add(pageAuto);
            if (LinuxUi) tabs.TabPages.Add(pageLinux);
            tabs.TabPages.Add(pageSettings);
            tabs.TabPages.Add(pageLogs);
            tabs.TabPages.Add(pagePolicy);
            bool appsLoaded = false, autoLoaded = false;
            tabs.SelectedIndexChanged += (o, e) =>
            {
                if (tabs.SelectedTab == pageApps && !appsLoaded) { appsLoaded = true; if (appsTabs.SelectedTab == pageStore) RefreshStoreApps(); else RefreshApps(); }
                if (tabs.SelectedTab == pageAuto && !autoLoaded) { autoLoaded = true; LoadAutomationState(); }
                if (tabs.SelectedTab == pageEmail) UpdateEmailUi();
                if (tabs.SelectedTab == pageDash && ready && (DateTime.Now - lastOverview).TotalSeconds > 20) RefreshOverview();
                if (tabs.SelectedTab == pageTools) OnToolsTab();
                if (tabs.SelectedTab == pagePhone) OnPhoneTab();
                if (tabs.SelectedTab == pageLinux) OnLinuxTab();
                if (tabs.SelectedTab == pageLogs) OnLogsTab();
                if (tabs.SelectedTab == pagePolicy) OnPolicyTab();
                if (tabs.SelectedTab == pageOneDrive) OnOneDriveTab();
            };
            BuildNav();
            ApplyPolicyUi();
            BuildTray();
            Controls.Add(tabHost);
            Controls.Add(nav);
            Controls.Add(new BrandBar
            {
                Dock = DockStyle.Top, Height = S(46), AppIcon = Icon,
                NameFont = new Font("Segoe UI Semibold", 15f), TagFont = new Font("Segoe UI", 9.5f)
            });
            var ver = typeof(MainForm).Assembly.GetName().Version;
            // footer: made by Elisar, the built-in user guide, and Verify this copy
            var footBack = Color.FromArgb(236, 233, 246); var footInk = Color.FromArgb(70, 52, 120); var footFont = new Font("Segoe UI", 8.5f);
            var footer = new Panel { Dock = DockStyle.Bottom, Height = S(24), BackColor = footBack };
            var footRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = footBack, Margin = new Padding(0) };
            Func<string, Label> footText = t => new Label { AutoSize = true, Text = t, Font = footFont, ForeColor = footInk, Margin = new Padding(0, S(4), 0, 0) };
            Func<string, LinkLabel> footLink = t => new LinkLabel { AutoSize = true, Text = t, Font = footFont, LinkColor = footInk, ActiveLinkColor = Color.FromArgb(91, 76, 196), Margin = new Padding(0, S(4), 0, 0) };
            var lnkGuide = footLink("User guide"); var lnkVerify = footLink("Verify this copy");
            footRow.Controls.AddRange(new Control[] { footText("Made by Elisar   \u00b7   Elixar v" + ver.Major + "." + ver.Minor + "." + ver.Build + "   \u00b7   "), lnkGuide, footText("   \u00b7   "), lnkVerify });
            footer.Controls.Add(footRow);
            footer.Resize += (o, e) => footRow.Location = new Point(Math.Max(0, (footer.Width - footRow.Width) / 2), 0);
            footRow.SizeChanged += (o, e) => footRow.Location = new Point(Math.Max(0, (footer.Width - footRow.Width) / 2), 0);
            var guideMenu = new ContextMenuStrip();
            guideMenu.Items.Add("Open the user guide (F1)", null, (o, e) => OpenGuide());
            guideMenu.Items.Add("Save a copy of the guide...", null, (o, e) => SaveGuide());
            lnkGuide.LinkClicked += (o, e) => guideMenu.Show(lnkGuide, new Point(0, -guideMenu.PreferredSize.Height));
            lnkVerify.LinkClicked += (o, e) => { using (var f = new IntegrityForm(s)) { f.Shown += (o2, e2) => { Theme.Apply(f); Lang.Apply(f); }; f.ShowDialog(this); } };
            var lnkReport = footLink("Export report");
            footRow.Controls.Add(footText("   \u00b7   ")); footRow.Controls.Add(lnkReport);
            lnkReport.LinkClicked += (o, e) => ExportReport();
            var lnkSettings = footLink("Settings");
            footRow.Controls.Add(footText("   \u00b7   ")); footRow.Controls.Add(lnkSettings);
            lnkSettings.LinkClicked += (o, e) => ShowSettings();
            Lang.Menu(guideMenu);
            lnkUpdate = footLink("Update available");
            lnkUpdate.LinkColor = Color.FromArgb(0, 125, 70); lnkUpdate.Visible = false;
            footRow.Controls.Add(lnkUpdate);
            lnkUpdate.LinkClicked += (o, e) => CheckForUpdates(true);
            Controls.Add(footer);

            KeyPreview = true;
            KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.F1) { e.Handled = true; OpenGuide(); return; }
                if (e.KeyCode != Keys.F5) return;
                e.Handled = true;
                if (tabs.SelectedTab == pageDash) RefreshCurrentDrive();
                else if (tabs.SelectedTab == pageApps) { if (appsTabs.SelectedTab == pageStore) RefreshStoreApps(); else RefreshApps(); }
                else if (tabs.SelectedTab == pageAuto) LoadAutomationState();
                else if (tabs.SelectedTab == pagePhone) DetectPhone();
                else if (tabs.SelectedTab == pageLinux) OnLinuxTab();
                else if (tabs.SelectedTab == pageTools) { if (toolsTabs.SelectedTab == pageDrivers) ScanDrivers(); else if (toolsTabs.SelectedTab == pageUndo) RefreshUndo(); }
                else if (btnMeasure.Enabled) StartMeasure(null);
            };

            list.ItemChecked += (o, e) => { if (ready) UpdateTotals(); };
            list.SelectedIndexChanged += (o, e) =>
            {
                if (list.SelectedItems.Count > 0)
                {
                    var it = (CleanItem)list.SelectedItems[0].Tag;
                    lblInfo.Text = Lang.T(it.Name) + ": " + Lang.T(it.Info);
                }
            };
            btnMeasure.Click += (o, e) => StartMeasure(null);
            btnSafe.Click += (o, e) => { foreach (ListViewItem r in list.Items) { var it = (CleanItem)r.Tag; r.Checked = it.Tier == Tier.Safe && !it.SizeUnknown; } };
            btnNone.Click += (o, e) => { foreach (ListViewItem r in list.Items) r.Checked = false; };
            btnClean.Click += (o, e) => OnClean();
            btnScan.Click += (o, e) => OnScan();

            Theme.Apply(this); Lang.Apply(this); Theme.Watch(this);
            Shown += (o, e) =>
            {
                if (!testMode) { var pt = new System.Windows.Forms.Timer { Interval = 1500 }; pt.Tick += (o2, e2) => { pt.Stop(); pt.Dispose(); if (Form.ActiveForm == this) { PolicyPinCheck(); OfferPinOnce(); } }; pt.Start(); }
                Log("Elixar " + (LinuxUi ? "(running as you; system items ask for your password when you clean them)" : Sys.IsAdmin() ? "(running as administrator)" : "(NOT running as administrator - some items will be skipped)"));
                Log(lastScan == null ? "First scan..." : "Scanning and comparing with the scan from " + ScanSnapshot.FmtWhen(lastScan.Time) + "...");
                RefreshOverview();
                StartMeasure(() =>
                {
                    Log("Ready. Tick what you want removed and click \"Clean selected\". Nothing is deleted until you confirm.");
                    RefreshDashboard();
                    LoadAppIndex();
                    CheckForUpdates(false);
                    CheckDisksQuietly();
                    if (!testMode && Win.IsWindows && Sys.IsAdmin())
                        Task.Factory.StartNew(() => Automation.UpgradeElevatedTasks()).ContinueWith(t =>
                        { if (!t.IsFaulted && t.Result.Count > 0) UI(() => Log("Secured the " + string.Join(" and ", t.Result) + " schedule (it now starts through Elixar's protected launcher).")); });
                    try
                    {
                        int lvl = 0; string ml = GoogleAuth.SignedInEmail != null ? MailStatus.Line(out lvl) : null;
                        if (ml != null && lvl == 2) Log("Email reports: " + ml + " (Settings > Email reports)");
                    }
                    catch { }
                    if (!testMode) Task.Factory.StartNew(() => Quarantine.PurgeOld(Log));
                });
            };
        }

        LinkLabel lnkUpdate;
        bool updBusy;

        // interactive: from the Settings window or the footer link; otherwise a quiet check at most once a week
        public void CheckForUpdates(bool interactive)
        {
            if (updBusy || testMode) return;
            if (!interactive)
            {
                DateTime last;
                if (!Updates.Configured || (DateTime.TryParse(Settings.Get("LastUpdateCheck", ""), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out last) && (DateTime.Now - last).TotalDays < 7)) return;
            }
            updBusy = true;
            Task.Factory.StartNew(() => { string err; var r = Updates.Latest(out err); return Tuple.Create(r, err); }).ContinueWith(t => UI(() =>
            {
                updBusy = false;
                Settings.Set("LastUpdateCheck", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture));
                var r = t.IsFaulted ? null : t.Result.Item1;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result.Item2;
                bool newer = r != null && r.Version != null && Updates.IsNewer(r.Version, Updates.Current);
                lnkUpdate.Visible = newer;
                if (newer) { lnkUpdate.Text = Lang.T("Update available") + ": " + r.Version.ToString(3); Log("Elixar " + r.Version.ToString(3) + " is available."); }
                if (!interactive) return;
                if (r == null) { Lang.Box(this, err ?? "Could not check for updates.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!newer) { Lang.Box(this, "Elixar is up to date (" + Updates.Current.ToString(3) + ").", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!Win.IsWindows)
                {
                    if (Lang.Box(this, "Elixar " + r.Version.ToString(3) + " is available (you have " + Updates.Current.ToString(3) + ").\n\nOpen the download page for the new .deb package?", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        Shell.Open(Updates.ReleasesPage);
                    return;
                }
                if (Lang.Box(this, "Elixar " + r.Version.ToString(3) + " is available (you have " + Updates.Current.ToString(3) + ").\n\n" + (r.Notes.Length > 600 ? r.Notes.Substring(0, 600) + "..." : r.Notes) +
                        "\n\nDownload and install it now? Elixar checks Elisar's signature and the file fingerprints first, then restarts.", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                updBusy = true;
                Task.Factory.StartNew(() => { string e2; string exe = Updates.Prepare(r, Log, out e2); return Tuple.Create(exe, e2); }).ContinueWith(t2 => UI(() =>
                {
                    updBusy = false;
                    string exe = t2.IsFaulted ? null : t2.Result.Item1;
                    if (exe == null)
                    {
                        string e3 = t2.IsFaulted ? t2.Exception.GetBaseException().Message : t2.Result.Item2;
                        Log("Update: " + e3);
                        if (Lang.Box(this, e3 + "\n\nOpen the download page?", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes && Updates.ReleasesPage.Length > 0)
                            try { Shell.Open(Updates.ReleasesPage); } catch { }
                        return;
                    }
                    Log("Installing the update and restarting...");
                    Updates.Launch(exe);
                    Close();
                }));
            }));
        }

        public void OpenGuide()
        {
            try { Guide.Open(); Log("Opened the user guide."); }
            catch (Exception ex) { Lang.Box(this, "Could not open the user guide: " + ex.Message + "\n\nUse \"Save a copy of the guide...\" instead.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void SaveGuide()
        {
            using (var d = new SaveFileDialog { Title = "Save the Elixar user guide", FileName = Guide.FileName, Filter = "Web page (*.html)|*.html",
                                                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    Guide.SaveCopy(d.FileName);
                    Log("Saved the user guide to " + d.FileName);
                    if (Lang.Box(this, "Saved:\n" + d.FileName + "\n\nOpen it now? (It works offline, and your browser can print it or save it as PDF.)", "Elixar",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes && Win.IsWindows)
                        Shell.Open(d.FileName);
                }
                catch (Exception ex) { Lang.Box(this, "Could not save it: " + ex.Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        Button MakeButton(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(8), S(3), S(8), S(3)),
                Margin = new Padding(S(2), S(2), S(6), S(2))
            };
        }

        void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }

        public void Log(string m)
        {
            m = Redact.Emails(m);
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + m;
            lock (logLock)
            {
                try { File.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine); } catch { }
            }
            if (InvokeRequired) UI(() => logBox.AppendText(line + Environment.NewLine));
            else logBox.AppendText(line + Environment.NewLine);
        }

        void SetBusy(bool busy)
        {
            foreach (Control c in new Control[] { btnMeasure, btnSafe, btnNone, btnScan, btnClean, btnPlan, list, chkRestore })
                c.Enabled = !busy;
            if (testMode) btnClean.Enabled = false;
            UseWaitCursor = busy;
        }

        void UpdateTotals()
        {
            long sum = 0;
            foreach (ListViewItem r in list.CheckedItems) { var it = (CleanItem)r.Tag; if (!it.SizeUnknown) sum += it.Size; }
            lblTop.Text = string.Format("Free on " + Sys.Main + "  {0} of {1}          Selected to clean:  {2}",
                Fs.Fmt(Sys.Free()), Fs.Fmt(Sys.Total()), Fs.Fmt(sum));
        }

        public void Rescan() { StartMeasure(null); }

        public bool ChangeColumnFilled()
        {
            return rows.Values.All(r => r.SubItems[3].Text.Length > 0);
        }

        void StartMeasure(Action after)
        {
            ready = false;
            SetBusy(true);
            var prev = lastScan;
            var watch = Stopwatch.StartNew();
            foreach (var r in rows.Values) { r.SubItems[2].Text = "scanning..."; r.SubItems[3].Text = ""; }
            lblScan.Text = "Scanning...";
            Task.Factory.StartNew(() =>
            {
                foreach (var it in items)
                {
                    long v = 0;
                    try { v = it.Measure(); }
                    catch (Exception ex) { Log("Could not measure " + it.Name + ": " + ex.Message); }
                    it.SizeUnknown = v < 0;
                    it.Size = Math.Max(0, v);
                    var row = rows[it];
                    string text = it.SizeUnknown ? "varies" : Fs.Fmt(it.Size);
                    UI(() => row.SubItems[2].Text = text);
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                ready = true;
                SetBusy(false);
                UpdateTotals();
                if (t.IsFaulted) Log("Measuring failed: " + t.Exception.GetBaseException().Message);
                ShowComparison(prev, watch.Elapsed);
                if (!testMode && !t.IsFaulted) SpaceHistory.Record(Sys.Free(), Sys.Total());
                if (after != null) after();
                if (MeasureDone != null) MeasureDone();
            }));
        }

        // ================= Dashboard tab =================
        static string TierTitle(string t)
        {
            switch (t)
            {
                case "critical": return "Critical - never delete";
                case "caution": return "Caution - only with the right tool";
                case "review": return "Review - your decision";
                default: return "Safe - rebuilt automatically";
            }
        }

        static Color TierColor(string t)
        {
            switch (t)
            {
                case "critical": return CriticalColor;
                case "caution": return CautionColor;
                case "review": return ReviewColor;
                default: return SafeColor;
            }
        }

        static string Cap(string t) { return t.Substring(0, 1).ToUpperInvariant() + t.Substring(1); }

        void BuildDashboard()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(S(7), S(10), S(7), S(6)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 0 header
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));      // 1 overview or details

            // header, two rows:  [drives .............. Refresh  Explore  Folder map  Remove apps]
            //                    [status line ......................... Overview | Details]
            // every control is centred in its row, so pills, switch and buttons line up
            var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 5, RowCount = 2, Margin = new Padding(S(5), 0, S(5), S(6)) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++) head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize)); head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dashHeader = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Text = "Drive C:", Margin = new Padding(0) };
            statusTail = Lang.T("Waiting for the first scan...");
            // one line that never wraps: when the window is narrow it ends with "..." (the whole text is in the tooltip)
            dashStatus = new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = S(24), TextAlign = Lang.Hebrew ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
                                     ForeColor = Color.DimGray, Text = StatusText(), Margin = new Padding(S(2), S(4), S(12), 0), UseMnemonic = false };
            var statusTip = new ToolTip();
            dashStatus.TextChanged += (o, e) => statusTip.SetToolTip(dashStatus, dashStatus.Text);
            var picker = BuildDrivePicker(); picker.Anchor = AnchorStyles.Left; picker.Margin = new Padding(0, S(2), S(12), S(2));
            dashView = new Segmented { Options = new[] { Lang.T("Overview"), Lang.T("Details") }, Scale_ = s, Font = new Font("Segoe UI", 9f), Margin = new Padding(0, S(4), S(3), 0), Anchor = AnchorStyles.Right };
            dashView.FitWidth();
            Func<string, Button> tool = text =>
            {
                var b = MakeButton(text);
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.White; b.FlatAppearance.BorderColor = Color.FromArgb(214, 219, 228);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 241, 255);
                b.Margin = new Padding(S(3), S(2), S(3), S(2)); b.Anchor = AnchorStyles.Left;
                b.MinimumSize = new Size(0, picker.Height);   // as tall as the drive pills
                return b;
            };
            btnDashRefresh = tool("Refresh (F5)");
            var btnExplore = tool("Explore");
            var btnMap = tool("Folder map");
            var btnDashApps = tool("Remove apps...");
            btnDashApps.Visible = !LinuxUi;
            head.Controls.Add(picker, 0, 0); head.Controls.Add(btnDashRefresh, 1, 0);
            head.Controls.Add(btnExplore, 2, 0); head.Controls.Add(btnMap, 3, 0); head.Controls.Add(btnDashApps, 4, 0);
            head.Controls.Add(dashStatus, 0, 1); head.Controls.Add(dashView, 1, 1); head.SetColumnSpan(dashView, 4);
            btnMap.Click += (o, e) => OpenMap(CurRoot());
            btnDashApps.Click += (o, e) => ShowStoreTab();
            btnExplore.Click += (o, e) => OpenDrill(CurRoot());

            dashNote = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "", Margin = new Padding(S(1), 0, 0, S(6)) };

            driveBar = new DriveBar { Dock = DockStyle.Fill, BarHeight = S(26), BackColor = pageDash.BackColor, Margin = new Padding(0, 0, 0, S(4)) };

            var cardRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, S(8)) };
            var titleFont = new Font("Segoe UI Semibold", 9f);
            var bigFont = new Font("Segoe UI Semibold", 17f);
            var subs = new Dictionary<string, string>
            {
                { "critical", LinuxUi ? "The system and installed apps. Never delete; remove apps you don't use." : "Windows and installed apps. Never delete; uninstall apps you don't use." },
                { "caution", "Leftovers and system stores. Only with the tool named in each row." },
                { "review", LinuxUi ? "Your own files: Downloads, Videos, Trash. Your decision." : "Your own files: Downloads, Videos, Recycle Bin. Your decision." },
                { "safe", "Caches and temp files. Clean them in the Cleaner tab." }
            };
            for (int i = 0; i < 4; i++)
            {
                string t = DashData.TierOrder[i];
                cardRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                var c = new TierCard
                {
                    Dock = DockStyle.Fill, Accent = TierColor(t), Title = Lang.T(t.ToUpperInvariant()), Big = "...", Sub = Lang.T(subs[t]),
                    TitleFont = titleFont, BigFont = bigFont, ForeColor = Color.FromArgb(24, 32, 43),
                    Margin = new Padding(i == 0 ? 0 : S(5), 0, i == 3 ? 0 : S(5), 0)
                };
                cards[t] = c;
                cardRow.Controls.Add(c, i, 0);
            }

            var sec = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Text = LinuxUi ? "Everything on your system disk, from critical to safe   (double-click a folder to see what is inside)" : "Everything on C:, from critical to safe   (double-click a folder to see what is inside)", Margin = new Padding(0, S(2), 0, S(4)) };

            dashList = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, ShowItemToolTips = true };
            dashList.Columns.Add("Location", S(250));
            dashList.Columns.Add("Size", S(85), HorizontalAlignment.Right);
            dashList.Columns.Add("Share of used space", S(190));
            dashList.Columns.Add("What to do", S(380));
            dashList.Columns.Add("Path", S(360));
            foreach (var t in DashData.TierOrder)
            {
                var g = new ListViewGroup(TierTitle(t), HorizontalAlignment.Left);
                dashGroups[t] = g;
                dashList.Groups.Add(g);
            }
            var barFont = new Font("Consolas", 8f);
            dashLocs = DashData.Build();
            foreach (var t in DashData.TierOrder)
            {
                foreach (var d in dashLocs.Where(x => x.Tier == t))
                {
                    var lvi = new ListViewItem(Lang.T(d.Name)) { UseItemStyleForSubItems = false, Tag = d, Group = dashGroups[t], ToolTipText = Lang.T(d.Action) };
                    lvi.SubItems.Add("...");
                    var bar = lvi.SubItems.Add("");
                    bar.Font = barFont;
                    bar.ForeColor = TierColor(t);
                    lvi.SubItems.Add(Lang.T(d.Action));
                    var path = lvi.SubItems.Add(d.OnC ? d.Path : d.Path + "  (not on C:)");
                    path.ForeColor = Color.DimGray;
                    dashList.Items.Add(lvi);
                }
            }

            // largest files
            var fhead = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, S(8), 0, S(4)) };
            fhead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fhead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var ftitle = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false };
            ftitle.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Text = LinuxUi ? "Largest files on your system disk" : "Largest files on C:", Margin = new Padding(0, S(4), S(10), 0) });
            filesStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Not scanned yet. Read-only; double-click a file to show it in its folder.", Margin = new Padding(0, S(6), 0, 0) };
            ftitle.Controls.Add(filesStatus);
            btnFindFiles = MakeButton("Find largest files (2-5 min)");
            fhead.Controls.Add(ftitle, 0, 0);
            fhead.Controls.Add(btnFindFiles, 1, 0);

            filesList = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, ShowItemToolTips = true };
            filesList.Columns.Add("Level", S(80));
            filesList.Columns.Add("Size", S(85), HorizontalAlignment.Right);
            filesList.Columns.Add("Modified", S(90));
            filesList.Columns.Add("File", S(520));
            filesList.Columns.Add("Why", S(420));
            foreach (var t in DashData.TierOrder)
            {
                var g = new ListViewGroup(TierTitle(t), HorizontalAlignment.Left);
                fileGroups[t] = g;
                filesList.Groups.Add(g);
            }
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show in folder", null, (o, e) => ShowSelectedFile());
            menu.Items.Add("Copy path", null, (o, e) =>
            {
                if (filesList.SelectedItems.Count == 0) return;
                try { Clipboard.SetText((string)filesList.SelectedItems[0].Tag); } catch { }
            });
            var miUninstall = new ToolStripMenuItem("Uninstall the app this file belongs to...");
            miUninstall.Click += (o, e) => { if (filesList.SelectedItems.Count > 0) UninstallOwner((string)filesList.SelectedItems[0].Tag); };
            menu.Items.Add(miUninstall);
            menu.Opening += (o, e) =>
            {
                string p = filesList.SelectedItems.Count > 0 ? (string)filesList.SelectedItems[0].Tag : null;
                string owner = p == null ? null : OwnerName(p);
                miUninstall.Enabled = owner != null && !testMode;
                miUninstall.Text = owner != null ? "Uninstall " + owner + "..." : "Uninstall app (this file does not belong to a removable app)";
            };
            filesList.ContextMenuStrip = menu;
            filesList.DoubleClick += (o, e) => ShowSelectedFile();

            // Details: the drive bar, the four levels, every location and the largest files
            var det = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = new Padding(0), Padding = new Padding(S(5), 0, S(5), 0) };
            det.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 0 note
            det.RowStyles.Add(new RowStyle(SizeType.Absolute, S(84)));   // 1 drive bar
            det.RowStyles.Add(new RowStyle(SizeType.Absolute, S(124)));  // 2 cards
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 3 section title
            det.RowStyles.Add(new RowStyle(SizeType.Percent, 58));       // 4 locations
            det.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // 5 files header
            det.RowStyles.Add(new RowStyle(SizeType.Percent, 42));       // 6 files
            det.Controls.Add(dashNote, 0, 0);
            det.Controls.Add(driveBar, 0, 1);
            det.Controls.Add(cardRow, 0, 2);
            det.Controls.Add(sec, 0, 3);
            det.Controls.Add(dashList, 0, 4);
            det.Controls.Add(fhead, 0, 5);
            det.Controls.Add(filesList, 0, 6);
            dashDetails = det;

            dashHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            dashHost.Controls.Add(dashDetails);
            dashHost.Controls.Add(BuildOverview());
            BuildDriveViews();
            root.Controls.Add(head, 0, 0);
            root.Controls.Add(dashHost, 0, 1);
            pageDash.Controls.Add(root);
            dashView.Changed += () => ShowDashView(dashView.Selected);
            ShowDashView(!testMode && Settings.Get("DashView", "overview") == "details" ? 1 : 0);

            btnDashRefresh.Click += (o, e) => RefreshCurrentDrive();
            dashList.DoubleClick += (o, e) =>
            {
                if (dashList.SelectedItems.Count == 0) return;
                var d = (DashLoc)dashList.SelectedItems[0].Tag;
                if (Directory.Exists(d.Path)) OpenDrill(d.Path);
                else if (File.Exists(d.Path)) { try { Shell.Select(d.Path); } catch { } }
            };
            btnFindFiles.Click += (o, e) => FindLargestFiles();
        }

        public DrillForm OpenDrill(string path)
        {
            var f = new DrillForm(path, s, Icon, OwnerName, p => UninstallOwner(p));
            f.Shown += (o, e) => { Theme.Apply(f); Lang.Apply(f); Theme.Watch(f); };
            f.Show(this);
            return f;
        }

        void ShowSelectedFile()
        {
            if (filesList.SelectedItems.Count == 0) return;
            string path = (string)filesList.SelectedItems[0].Tag;
            try { Shell.Select(path); }
            catch (Exception ex) { Log("Could not open the folder: " + ex.Message); }
        }

        public void RefreshDashboard()
        {
            if (dashBusy) return;
            dashBusy = true;
            btnDashRefresh.Enabled = false;
            var watch = Stopwatch.StartNew();
            foreach (ListViewItem r in dashList.Items) { r.SubItems[1].Text = "..."; r.SubItems[2].Text = ""; }
            dashStatus.Text = "Measuring...";
            Task.Factory.StartNew(() =>
            {
                foreach (var d in dashLocs)
                {
                    var loc = d;
                    UI(() => dashStatus.Text = "Measuring " + loc.Name + "...");
                    try { DashData.Measure(loc); }
                    catch (Exception ex) { loc.Bytes = null; Log("Could not measure " + loc.Name + ": " + ex.Message); }
                }
                DashData.ComputeOwn(dashLocs);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                try
                {
                    if (t.IsFaulted) Log("Dashboard failed: " + t.Exception.GetBaseException().Message);
                    FillDashboard(watch.Elapsed);
                }
                catch (Exception ex) { Log("Dashboard could not be drawn: " + ex.Message); }
                finally
                {
                    dashBusy = false;
                    btnDashRefresh.Enabled = true;
                    if (DashboardDone != null) DashboardDone();
                }
            }));
        }

        public List<string> TabNames { get { return tabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList(); } }
        public List<string> CleanerItemNames { get { return items.Select(i => i.Name).ToList(); } }

        public bool DashboardFilled()
        {
            return dashList.Items.Cast<ListViewItem>().All(r => r.SubItems[1].Text != "...") && cards.Values.All(c => c.Big != "...");
        }

        void FillDashboard(TimeSpan took)
        {
            long total = Sys.Total(), free = Sys.Free(), used = Math.Max(0, total - free);
            var totals = DashData.Totals(dashLocs);
            long known = totals.Values.Sum();
            long max = Math.Max(1, dashLocs.Where(d => d.Bytes.HasValue).Select(d => d.Own).DefaultIfEmpty(1).Max());
            long whole = used > 0 ? used : Math.Max(1, known);

            foreach (ListViewItem r in dashList.Items)
            {
                var d = (DashLoc)r.Tag;
                if (!d.OnC) { r.SubItems[1].Text = "-"; r.SubItems[2].Text = ""; continue; }
                if (!d.Bytes.HasValue) { r.SubItems[1].Text = "not present"; r.SubItems[1].ForeColor = Color.DimGray; r.SubItems[2].Text = ""; continue; }
                r.SubItems[1].ForeColor = SystemColors.WindowText;
                r.SubItems[1].Text = Fs.Fmt(d.Own);
                r.SubItems[2].Text = DashData.Bar(d.Own, max, whole, 16);
                r.ToolTipText = Lang.T(d.Action) + (d.Own != d.Bytes.Value ? "\n(" + Fs.Fmt(d.Bytes.Value) + " including the items listed separately)" : "");
            }

            // biggest first inside each level; missing locations last
            dashList.ListViewItemSorter = new DashSorter();
            dashList.Sort();

            foreach (var t in DashData.TierOrder)
            {
                dashGroups[t].Header = TierTitle(t) + "   (" + Fs.Fmt(totals[t]) + ")";
                cards[t].Big = Fs.Fmt(totals[t]);
                cards[t].Invalidate();
            }

            // drive bar: the four levels, the rest of used space, free space
            double scale = known > used && used > 0 ? (double)used / known : 1.0;
            driveBar.Total = total;
            driveBar.Segments = new List<DriveBar.Seg>();
            foreach (var t in DashData.TierOrder)
                driveBar.Segments.Add(new DriveBar.Seg { Label = Cap(t), Bytes = (long)(totals[t] * scale), Color = TierColor(t) });
            driveBar.Segments.Add(new DriveBar.Seg { Label = "Other used", Bytes = Math.Max(0, used - (long)(known * scale)), Color = OtherColor });
            driveBar.Segments.Add(new DriveBar.Seg { Label = "Free", Bytes = free, Color = FreeColor, Outline = true });
            driveBar.Invalidate();

            dashHeader.Text = Lang.T("Drive C:");
            string tookText = took.TotalSeconds < 1 ? "under 1 s" : ((int)Math.Round(took.TotalSeconds)) + " s";
            statusTail = Lang.T("Updated " + DateTime.Now.ToString("HH:mm") + " (took " + tookText + ").");
            dashStatus.Text = StatusText();
            dashNote.Text = Lang.T(total > 0 ? Fs.Fmt(free) + " free of " + Fs.Fmt(total) + "." : "") + (Sys.IsAdmin() ? "" : "  " + Lang.T("Not running as administrator: some Windows folders may show smaller than they are.")) +
                "  " + Lang.T("WinSxS looks bigger than it is because many of its files are shared with System32.");
            if (!testMode && Win.IsWindows) Growth.Record(dashLocs, DateTime.Today);
            RefreshOverview();
            Log("Dashboard updated: critical " + Fs.Fmt(totals["critical"]) + ", caution " + Fs.Fmt(totals["caution"]) +
                ", review " + Fs.Fmt(totals["review"]) + ", safe " + Fs.Fmt(totals["safe"]) + ".");
        }

        void FindLargestFiles()
        {
            if (filesBusy) return;
            filesBusy = true;
            btnFindFiles.Enabled = false;
            filesList.Items.Clear();
            filesStatus.Text = "Scanning " + Sys.Main + " ...";
            var watch = Stopwatch.StartNew();
            string last = "";
            Task.Factory.StartNew(() => Scanner.LargestFiles(Sys.Root, 50L << 20, 200, d =>
            {
                if (d == last) return;
                last = d;
                UI(() => filesStatus.Text = "Scanning " + (d.Length > 90 ? d.Substring(0, 87) + "..." : d));
            }), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                filesBusy = false;
                btnFindFiles.Enabled = true;
                if (t.IsFaulted) { filesStatus.Text = "Scan failed: " + t.Exception.GetBaseException().Message; return; }
                var rows = new List<Tuple<string, Scanner.BigFile, string>>();
                foreach (var f in t.Result) { string tier, reason; Scanner.Classify(f.Path, out tier, out reason); rows.Add(Tuple.Create(tier, f, reason)); }
                filesList.BeginUpdate();
                foreach (var tier in DashData.TierOrder)
                {
                    foreach (var row in rows.Where(x => x.Item1 == tier).OrderByDescending(x => x.Item2.Bytes))
                    {
                        var f = row.Item2;
                        var lvi = new ListViewItem(Cap(tier)) { UseItemStyleForSubItems = false, Tag = f.Path, Group = fileGroups[tier], ToolTipText = row.Item3 };
                        lvi.ForeColor = TierColor(tier);
                        lvi.SubItems.Add(Fs.Fmt(f.Bytes));
                        lvi.SubItems.Add(f.Modified.ToString("yyyy-MM-dd"));
                        lvi.SubItems.Add(f.Path);
                        var why = lvi.SubItems.Add(row.Item3);
                        why.ForeColor = Color.DimGray;
                        filesList.Items.Add(lvi);
                    }
                    int n = rows.Count(x => x.Item1 == tier);
                    fileGroups[tier].Header = TierTitle(tier) + "   (" + n + " files, " + Fs.Fmt(rows.Where(x => x.Item1 == tier).Sum(x => x.Item2.Bytes)) + ")";
                }
                filesList.EndUpdate();
                filesStatus.Text = rows.Count + " files over 50 MB, found in " + (int)Math.Round(watch.Elapsed.TotalSeconds) + " s. Read-only; double-click a file to show it in its folder.";
                Log("Largest files: " + rows.Count + " files over 50 MB listed on the Dashboard.");
            }));
        }

        void ShowComparison(ScanSnapshot prev, TimeSpan took)
        {
            var now = new ScanSnapshot { Time = DateTime.Now, Free = Sys.Free() };
            var changed = new List<string>();
            foreach (var it in items)
            {
                var cell = rows[it].SubItems[3];
                if (it.SizeUnknown) { cell.Text = "-"; cell.ForeColor = Color.DimGray; continue; }
                now.Sizes[it.Name] = it.Size;
                long before;
                if (prev == null || !prev.Sizes.TryGetValue(it.Name, out before)) { cell.Text = "-"; cell.ForeColor = Color.DimGray; continue; }
                long d = it.Size - before;
                cell.Text = ScanSnapshot.FmtDelta(d);
                cell.ForeColor = Math.Abs(d) < (1L << 20) ? Color.DimGray : d < 0 ? SafeColor : CautionColor;
                if (Math.Abs(d) >= (1L << 20)) changed.Add("  " + it.Name + ": " + ScanSnapshot.FmtDelta(d));
            }

            string tookText = took.TotalSeconds < 1 ? "under 1 s" : ((int)Math.Round(took.TotalSeconds)) + " s";
            if (prev == null)
            {
                lblScan.Text = "Scanned at " + now.Time.ToString("HH:mm:ss") + " (took " + tookText + ").  The Change column fills in from your next rescan.";
                Log("Scan done in " + tookText + ".");
            }
            else
            {
                long fd = now.Free - prev.Free;
                string freeText = Math.Abs(fd) < (1L << 20) ? "free space unchanged" : "free space " + ScanSnapshot.FmtDelta(fd);
                lblScan.Text = "Scanned at " + now.Time.ToString("HH:mm:ss") + " (took " + tookText + ").  Compared with " +
                               ScanSnapshot.FmtWhen(prev.Time) + ": " + freeText + ".";
                Log("Rescan done in " + tookText + ". Since " + ScanSnapshot.FmtWhen(prev.Time) + ": " + freeText + ".");
                if (changed.Count > 0) { Log("Changed since then:"); foreach (var c in changed) Log(c); }
                else Log("No item changed by 1 MB or more.");
            }

            lastScan = now;
            if (snapFile != null) { try { now.Save(snapFile); } catch (Exception ex) { Log("Could not save the scan for next time: " + ex.Message); } }
        }

        void OnClean()
        {
            var sel = list.CheckedItems.Cast<ListViewItem>().Select(r => (CleanItem)r.Tag).ToList();
            if (sel.Count == 0)
            {
                Lang.Box(this, "Tick at least one item to clean.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CleanItems(sel);
        }

        // the one way Elixar cleans from the window: the Cleaner tab and the Dashboard both come here, and it always asks first
        void CleanItems(List<CleanItem> sel)
        {
            if (testMode || cleaningNow || !ready || sel.Count == 0) return;
            sel = PolicyFilter(sel);
            if (sel.Count == 0) return;

            long est = sel.Where(i => !i.SizeUnknown).Sum(i => i.Size);
            var lines = sel.Select(i => "  - " + Lang.T(i.Name) + "  (" + (i.SizeUnknown ? Lang.T("varies") : Fs.Fmt(i.Size)) + ")");
            string msg = Lang.T("Delete these " + sel.Count + " items?") + "\n\n" + string.Join("\n", lines) +
                         "\n\n" + Lang.T("Estimated space freed: " + Fs.Fmt(est)) + "\n\n" + Lang.T("This cannot be undone.");
            if (Lang.Box(this, msg, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            // apps that must be closed first
            var toRun = new List<CleanItem>();
            var toKill = new List<Process>();
            foreach (var it in sel)
            {
                if (it.Procs.Length > 0)
                {
                    var running = Sys.FindProcesses(it.Procs);
                    if (running.Count > 0)
                    {
                        string names = string.Join(", ", running.Select(p => { try { return p.ProcessName; } catch { return "?"; } }).Distinct());
                        var ans = Lang.Box(this,
                            it.Name + ": " + names + " is running.\n\nYes = close it now and clean\nNo = skip this item\nCancel = stop",
                            "Elixar", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (ans == DialogResult.Yes) toKill.AddRange(running);
                        else if (ans == DialogResult.No) { Log("Skipped " + it.Name + " (" + names + " was running)."); continue; }
                        else { Log("Stopped by you. Nothing was deleted."); return; }
                    }
                }
                toRun.Add(it);
            }
            if (toRun.Count == 0) return;

            string rpRule = Policy.Get("clean.restore");
            bool restore = rpRule == "always" ? !LinuxUi : rpRule == "never" ? false : chkRestore.Checked;
            SetBusy(true);
            cleaningNow = true;
            RefreshOverview();
            progress.Maximum = toRun.Count;
            progress.Value = 0;

            Task.Factory.StartNew(() => ChangeLog.Within("Cleaner", () =>
            {
                foreach (var p in toKill) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
                if (toKill.Count > 0) Thread.Sleep(1500);

                if (restore)
                {
                    Log("Creating a restore point (can take a minute)...");
                    string r;
                    try { r = Sys.RunPowerShell(
                        "try { Checkpoint-Computer -Description 'Before Elixar' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue; " +
                        "if ($w) { 'Not created: ' + ($w -join ' ') } else { 'Restore point created.' } } " +
                        "catch { 'Not created (System Protection may be off): ' + $_.Exception.Message }", 5 * 60 * 1000); }
                    catch (Exception ex) { r = "Not created: " + ex.Message; }
                    Log("  " + r);
                }

                long start = Sys.Free();
                var asRoot = toRun.Where(i => i.RootScript != null).ToList();
                if (asRoot.Count > 0)
                {
                    Log("Asking for your password for: " + string.Join(", ", asRoot.Select(i => i.Name)) + "...");
                    long before = Sys.Free();
                    string script = string.Join("\n", asRoot.Select(i => i.RootScript));
                    string outp;
                    int code = Lx.RunAsRoot(script, out outp);
                    ChangeLog.Add("Cleaning", "Cleaned as root: " + string.Join(", ", asRoot.Select(i => i.Name)), "", "", code == 0 ? "freed " + Fs.Fmt(Math.Max(0, Sys.Free() - before)) : "Problem (" + code + ")", "Cleaner", code != 0);
                    if (code == 126 || code == 127) Log("  Not done: the password wasn't entered.");
                    else if (code != 0) Log("  Finished with a problem (" + code + "): " + (outp.Length > 300 ? outp.Substring(0, 300) + "..." : outp));
                    Log("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - before)));
                    foreach (var it in asRoot) UI(() => progress.Value = Math.Min(progress.Maximum, progress.Value + 1));
                }
                foreach (var it in toRun.Where(i => i.RootScript == null || i.Clean != null))
                {
                    if (it.Clean == null) continue;
                    Log("Cleaning " + it.Name + "...");
                    long before = Sys.Free(); string problem = null;
                    try { it.Clean(Log); }
                    catch (Exception ex) { Log("  Problem: " + ex.Message); problem = ex.Message; }
                    Log("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - before)));
                    ChangeLog.Cleaned(it, Math.Max(0, Sys.Free() - before), problem, "Cleaner");
                    UI(() => progress.Value = Math.Min(progress.Maximum, progress.Value + 1));
                }
                long total = Math.Max(0, Sys.Free() - start);
                Log("Done. Total freed: " + Fs.Fmt(total));
                return total;
            }), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                long total = t.IsFaulted ? 0 : t.Result;
                if (t.IsFaulted) Log("Cleaning stopped with an error: " + t.Exception.GetBaseException().Message);
                cleaningNow = false;
                Activity.Add("clean", "Cleaned " + (toRun.Count == 1 ? toRun[0].Name : toRun.Count + " items") + ", freed " + Fs.Fmt(total), total);
                SetBusy(false);
                StartMeasure(() =>
                {
                    RefreshDashboard();
                    Lang.Box(this, "Done. Freed " + Fs.Fmt(total) + " on C:.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            }));
        }

        void OnScan()
        {
            var ok = Lang.Box(this,
                "This reads drive C: and saves c-drive-scan.json to your Desktop for the Elixar Drive Map dashboard.\n\nIt only reads; nothing is deleted. It takes a few minutes.",
                "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (ok != DialogResult.OK) return;

            SetBusy(true);
            progress.Style = ProgressBarStyle.Marquee;
            Log("Scan for dashboard started (read-only)...");
            string lastStatus = "";
            Task.Factory.StartNew(() => Scanner.Run(st =>
            {
                if (st == lastStatus) return;
                lastStatus = st;
                UI(() => lblTop.Text = st.Length > 110 ? st.Substring(0, 107) + "..." : st);
            }), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                progress.Style = ProgressBarStyle.Blocks;
                SetBusy(false);
                UpdateTotals();
                if (t.IsFaulted)
                {
                    Log("Scan failed: " + t.Exception.GetBaseException().Message);
                    Lang.Box(this, "The scan failed:\n\n" + t.Exception.GetBaseException().Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Log("Scan saved: " + t.Result);
                    Lang.Box(this, "Saved:\n" + t.Result + "\n\nOpen the Elixar Drive Map dashboard and drop this file on it.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }));
        }
    }

    // ---------------- built-in self-test ----------------
    // Only creates and deletes files inside its own folder under %TEMP%.
    // Real cleanup items are only MEASURED (read-only). The window opens with cleaning switched off.
}
