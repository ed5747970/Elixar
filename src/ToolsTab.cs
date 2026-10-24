// Tools tab: Disk health, Drivers, Startup, System space, Move off C:, Duplicates, Undo.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pageTools, pageDrivers, pageStartup, pageSys, pageDups, pageUndo;
        TabControl toolsTabs;
        public event Action DriversDone, UndoDone;

        // drivers
        ListView drvList; Label drvStatus; Button btnDrvScan, btnDrvSelect, btnDrvRemove; CheckBox chkDrvRestore; bool drvBusy, drvLoaded;
        // system space
        TextBox sysOut; Label lblSxs, lblReserved, lblShadow, lblCompact, lblVhd; ComboBox cmbShadow; bool sysBusy;
        readonly List<Button> sysButtons = new List<Button>();
        // duplicates
        ListBox dupFolders; ComboBox cmbDupMin; ListView dupList; Label dupStatus; Button btnDupFind, btnDupStop, btnDupSelect, btnDupRecycle; CancellationTokenSource dupCts;
        // undo
        ListView undoList; Label undoStatus; Button btnUndoRestore, btnUndoDelete, btnUndoOpen, btnUndoRefresh;

        TableLayoutPanel Page(int rows)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Padding = new Padding(S(12), S(10), S(12), S(10)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }
        Label Title(string t) { return new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Text = t, Margin = new Padding(0, S(2), 0, S(2)) }; }
        Label Note(string t) { return new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(1000), 0), Text = t, Margin = new Padding(S(1), 0, 0, S(8)) }; }

        void BuildTools()
        {
            pageTools = new TabPage("Tools") { BackColor = Color.FromArgb(246, 248, 251) };
            toolsTabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(12), S(4)) };
            pageDisk = new TabPage("Disk health") { BackColor = pageTools.BackColor };
            pageDrivers = new TabPage("Drivers") { BackColor = pageTools.BackColor };
            pageStartup = new TabPage("Startup") { BackColor = pageTools.BackColor };
            pageSys = new TabPage("System space") { BackColor = pageTools.BackColor };
            pageMove = new TabPage("Move off C:") { BackColor = pageTools.BackColor };
            pageDups = new TabPage("Duplicates") { BackColor = pageTools.BackColor };
            pageUndo = new TabPage("Undo") { BackColor = pageTools.BackColor };
            pageLeft = new TabPage("Leftovers") { BackColor = pageTools.BackColor };
            pageSysprep = new TabPage("Sysprep") { BackColor = pageTools.BackColor };
            BuildSpaceMap(); BuildTweaks();
            if (LinuxUi) toolsTabs.TabPages.AddRange(new[] { pageMap, pageDups });   // the others are Windows' own
            else toolsTabs.TabPages.AddRange(new[] { pageMap, pageDisk, pageDrivers, pageStartup, pageTweaks, pageSys, pageMove, pageDups, pageLeft, pageSysprep, pageUndo });
            pageTools.Controls.Add(toolsTabs);
            BuildDiskHealth(); BuildDrivers(); BuildStartup(); BuildSystemSpace(); BuildMove(); BuildDuplicates(); BuildLeftovers(); BuildSysprep(); BuildUndo();
            toolsTabs.SelectedIndexChanged += (o, e) => OnToolsTab();
        }

        void OnToolsTab()
        {
            if (toolsTabs.SelectedTab == pageDisk && !diskLoaded) RefreshDiskHealth();
            else if (toolsTabs.SelectedTab == pageDrivers && !drvLoaded) ScanDrivers();
            else if (toolsTabs.SelectedTab == pageStartup && !startLoaded) RefreshStartup();
            else if (toolsTabs.SelectedTab == pageMove && !moveLoaded) RefreshMove();
            else if (toolsTabs.SelectedTab == pageUndo) RefreshUndo();
            else if (toolsTabs.SelectedTab == pageTweaks) RefreshTweaks();
            else if (toolsTabs.SelectedTab == pageSysprep) OnSysprepTab();
        }

        public void ShowTools(int inner) { tabs.SelectedTab = pageTools; toolsTabs.SelectedIndex = inner; OnToolsTab(); }
        public void ShowTools(TabPage p) { int i = toolsTabs.TabPages.IndexOf(p); if (i >= 0) ShowTools(i); }
        public void ShowUndo() { ShowTools(pageUndo); }
        public void ShowStartup() { ShowTools(pageStartup); }
        public void ShowMove() { ShowTools(pageMove); }

        // ================= Drivers =================
        Label drvHardware; Button btnDrvSelectFit;

        void BuildDrivers()
        {
            var t = Page(6);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            drvHardware = new Label { AutoSize = true, MaximumSize = new Size(S(1040), 0), ForeColor = Color.FromArgb(46, 38, 92), Margin = new Padding(S(1), 0, 0, S(4)), Text = "This PC: reading hardware..." };
            drvStatus = Note("Finds drivers this PC doesn't need: old versions, drivers built for a different kind of Windows, and drivers for hardware that isn't in this PC. " +
                             "Never one a device uses or one Windows needs to start. Each is backed up to Undo before it is removed.");
            drvList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            drvList.Columns.Add("Driver", S(250)); drvList.Columns.Add("Version", S(130)); drvList.Columns.Add("Date", S(90)); drvList.Columns.Add("Size", S(80), HorizontalAlignment.Right);
            drvList.Columns.Add("Status", S(420)); drvList.Columns.Add("Package", S(90));
            drvList.ItemCheck += (o, e) => { var d = (DriverPkg)drvList.Items[e.Index].Tag; if (!d.Removable && e.NewValue == CheckState.Checked) e.NewValue = CheckState.Unchecked; };
            drvList.ItemChecked += (o, e) => UpdateDrvButton();
            chkDrvRestore = new CheckBox { AutoSize = true, Checked = true, Text = "Create a restore point first (recommended)", Margin = new Padding(S(2), S(8), 0, 0) };
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnDrvRemove = MakeButton("Remove selected..."); btnDrvRemove.Font = new Font("Segoe UI Semibold", 9f); btnDrvRemove.Enabled = false;
            btnDrvSelect = MakeButton("Select all old versions");
            btnDrvSelectFit = MakeButton("Select drivers that don't fit this PC");
            btnDrvScan = MakeButton("Scan again");
            row.Controls.AddRange(new Control[] { btnDrvRemove, btnDrvSelect, btnDrvSelectFit, btnDrvScan });
            t.Controls.Add(Title("Drivers"), 0, 0); t.Controls.Add(drvHardware, 0, 1); t.Controls.Add(drvStatus, 0, 2); t.Controls.Add(drvList, 0, 3); t.Controls.Add(chkDrvRestore, 0, 4); t.Controls.Add(row, 0, 5);
            pageDrivers.Controls.Add(t);
            btnDrvScan.Click += (o, e) => ScanDrivers();
            btnDrvSelect.Click += (o, e) => { foreach (ListViewItem r in drvList.Items) { var d = (DriverPkg)r.Tag; if (d.Kind == DrvKind.Old) r.Checked = true; } };
            btnDrvSelectFit.Click += (o, e) => { foreach (ListViewItem r in drvList.Items) { var d = (DriverPkg)r.Tag; if (d.Kind == DrvKind.NotForWindows || d.Kind == DrvKind.NoHardware) r.Checked = true; } };
            btnDrvRemove.Click += (o, e) => RemoveDrivers();
        }

        void UpdateDrvButton()
        {
            var sel = drvList.CheckedItems.Cast<ListViewItem>().Select(r => (DriverPkg)r.Tag).ToList();
            btnDrvRemove.Enabled = sel.Count > 0 && !drvBusy && !testMode;
            btnDrvRemove.Text = sel.Count > 0 ? "Remove " + sel.Count + " (" + Fs.Fmt(sel.Sum(d => Math.Max(0, d.Bytes))) + ")..." : "Remove selected...";
        }

        public void ScanDrivers()
        {
            if (drvBusy) return;
            drvBusy = true; drvLoaded = true; btnDrvScan.Enabled = false;
            drvList.Items.Clear(); drvList.Groups.Clear();
            drvStatus.Text = "Reading this PC's hardware and the driver store (up to a minute)...";
            Task.Factory.StartNew(() => Drivers.Read(), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                drvBusy = false; btnDrvScan.Enabled = true;
                if (t.IsFaulted) { drvStatus.Text = "Could not read drivers: " + t.Exception.GetBaseException().Message; }
                else ShowDrivers(t.Result, Drivers.Hardware);
                UpdateDrvButton();
                if (DriversDone != null) DriversDone();
            }));
        }

        ListViewItem DrvRow(DriverPkg d, ListViewGroup g, bool showName)
        {
            var lvi = new ListViewItem(showName ? d.Provider + " - " + d.Class + " (" + d.OriginalName + ")" : "   " + d.OriginalName)
                { Tag = d, Group = g, UseItemStyleForSubItems = false, ToolTipText = d.Original + (d.Arch.Length > 0 ? "\nBuilt for: " + Drivers.ArchName(d.Arch) + " Windows" : "") };
            lvi.SubItems.Add(d.Version);
            lvi.SubItems.Add(d.Date > DateTime.MinValue ? d.Date.ToString("yyyy-MM-dd") : "");
            lvi.SubItems.Add(d.Bytes > 0 ? Fs.Fmt(d.Bytes) : "?");
            string text = d.Kind == DrvKind.Old ? "old - " + d.Why : d.Why;
            var st = lvi.SubItems.Add(text);
            st.ForeColor = d.Kind == DrvKind.NotConnected ? ReviewColor : d.Suggested ? CautionColor : SafeColor;
            lvi.SubItems.Add(d.Inf);
            if (!d.Removable) lvi.ForeColor = Color.Gray;
            return lvi;
        }

        void ShowDrivers(List<DriverPkg> all, List<KeyValuePair<string, string>> hw)
        {
            drvHardware.Text = hw == null || hw.Count == 0 ? "This PC: hardware details not available." :
                "This PC:  " + string.Join("   |   ", hw.GroupBy(h => h.Key).Select(g => g.Key + ": " + string.Join(", ", g.Select(x => x.Value).Distinct())));
            drvList.BeginUpdate();
            drvList.Items.Clear(); drvList.Groups.Clear();

            Func<IEnumerable<DriverPkg>, long> size = x => x.Sum(d => Math.Max(0, d.Bytes));
            Action<string, IEnumerable<DriverPkg>> section = (title, items) =>
            {
                var list = items.OrderByDescending(d => d.Bytes).ToList();
                if (list.Count == 0) return;
                var g = new ListViewGroup(title + "   (" + list.Count + ", " + Fs.Fmt(size(list)) + ")", HorizontalAlignment.Left);
                drvList.Groups.Add(g);
                foreach (var d in list) drvList.Items.Add(DrvRow(d, g, true));
            };

            // 1. old versions, shown with the version that is kept
            foreach (var grp in all.GroupBy(d => d.Group).Where(g => g.Any(d => d.Kind == DrvKind.Old)).OrderByDescending(g => size(g.Where(d => d.Kind == DrvKind.Old))))
            {
                var keep = grp.OrderByDescending(d => d.Kind == DrvKind.Old ? 0 : 1).ThenByDescending(d => d.Date).First();
                var lg = new ListViewGroup("Old versions: " + keep.Provider + " - " + keep.Class + " (" + keep.OriginalName + ")   keeping " + keep.Version, HorizontalAlignment.Left);
                drvList.Groups.Add(lg);
                foreach (var d in grp.OrderByDescending(x => x.Date)) drvList.Items.Add(DrvRow(d, lg, false));
            }
            // 2. doesn't fit this Windows   3. no hardware uses it   4. hardware not connected
            section("Not for this Windows (built for " + "another kind of Windows)", all.Where(d => d.Kind == DrvKind.NotForWindows));
            section("No hardware in this PC uses these", all.Where(d => d.Kind == DrvKind.NoHardware));
            section("For hardware that isn't connected now - your decision", all.Where(d => d.Kind == DrvKind.NotConnected));
            drvList.EndUpdate();

            var old = all.Where(d => d.Kind == DrvKind.Old).ToList();
            var nofit = all.Where(d => d.Kind == DrvKind.NotForWindows || d.Kind == DrvKind.NoHardware).ToList();
            var unplugged = all.Where(d => d.Kind == DrvKind.NotConnected).ToList();
            string where = Quarantine.RootFor(size(old) + size(nofit));
            drvStatus.Text = old.Count + nofit.Count + unplugged.Count == 0
                ? "Nothing to remove: every driver is in use, needed by Windows, or the only version."
                : old.Count + " old versions (" + Fs.Fmt(size(old)) + "), " + nofit.Count + " that don't fit this PC (" + Fs.Fmt(size(nofit)) + "), " +
                  unplugged.Count + " for unplugged hardware (" + Fs.Fmt(size(unplugged)) + "). " + all.Count(d => !d.Removable) + " are in use or locked. " +
                  "Backups go to " + where + (Quarantine.RootIsOffC(where) ? " (not on C:, so space is freed right away)." : " (on C:, so space comes back after 7 days).");
        }

        void RemoveDrivers()
        {
            var sel = drvList.CheckedItems.Cast<ListViewItem>().Select(r => (DriverPkg)r.Tag).Where(d => d.Removable).ToList();
            if (sel.Count == 0 || testMode || Policy.Blocked(this, "drivers.remove")) return;
            var lines = sel.GroupBy(d => d.Kind).Select(g => "  - " + g.Count() + " " + (g.Key == DrvKind.Old ? "old versions" : g.Key == DrvKind.NotForWindows ? "built for another kind of Windows" :
                                                                                       g.Key == DrvKind.NoHardware ? "for hardware not in this PC" : "for hardware that isn't connected now"));
            if (Lang.Box(this, "Remove " + sel.Count + " drivers (" + Fs.Fmt(sel.Sum(d => Math.Max(0, d.Bytes))) + ")?\n\n" + string.Join("\n", lines) +
                    "\n\nDrivers your devices use are never touched. Each removed one is backed up to Undo for 7 days and can be put back from Tools > Undo.",
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            bool restore = chkDrvRestore.Checked;
            drvBusy = true; UpdateDrvButton(); btnDrvScan.Enabled = false;
            Task.Factory.StartNew(() =>
            {
                if (restore) Log("  " + CreateRestorePoint("Before Elixar removed drivers"));
                long before = Sys.Free();
                int n = Drivers.Remove(sel, m => { Log(m); UI(() => drvStatus.Text = m.Trim()); });
                Log("Drivers: removed " + n + " of " + sel.Count + ". Free space change on C: " + ScanSnapshot.FmtDelta(Sys.Free() - before) + ".");
                return n;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                drvBusy = false;
                int n = t.IsFaulted ? 0 : t.Result;
                Lang.Box(this, "Removed " + n + " of " + sel.Count + " drivers." + (n < sel.Count ? "\nWindows kept some; the log on the Cleaner tab says why." : ""),
                    "Elixar", MessageBoxButtons.OK, n == sel.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                ScanDrivers();
            }));
        }

        string CreateRestorePoint(string description)
        {
            try
            {
                return Sys.RunPowerShell("try { Checkpoint-Computer -Description " + Sys.PsQuote(description) + " -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue; " +
                    "if ($w) { 'Restore point not created: ' + ($w -join ' ') } else { 'Restore point created.' } } catch { 'Restore point not created: ' + $_.Exception.Message }", 5 * 60 * 1000);
            }
            catch (Exception ex) { return "Restore point not created: " + ex.Message; }
        }

        // ================= System space =================
        GroupBox Box(string title, out Label status, string note)
        {
            var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(S(900), 0), Padding = new Padding(S(10), S(6), S(10), S(8)), Margin = new Padding(0, 0, 0, S(10)) };
            var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            status = new Label { AutoSize = true, Text = "Not checked yet.", MaximumSize = new Size(S(860), 0), Margin = new Padding(0, S(2), 0, S(2)) };
            f.Controls.Add(status);
            f.Controls.Add(new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(860), 0), Text = note, Margin = new Padding(0, 0, 0, S(4)) });
            g.Controls.Add(f);
            g.Tag = f;
            return g;
        }

        Button SysButton(GroupBox g, string text, bool changes, Action<Action<string>> work, string confirm = null)
        {
            var row = ((FlowLayoutPanel)g.Tag).Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
            if (row == null) { row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) }; ((FlowLayoutPanel)g.Tag).Controls.Add(row); }
            var b = MakeButton(text);
            row.Controls.Add(b);
            sysButtons.Add(b);
            if (changes && testMode) b.Enabled = false;
            b.Tag = changes;
            b.Click += (o, e) =>
            {
                if (sysBusy) return;
                if (confirm != null && Lang.Box(this, confirm, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                sysBusy = true;
                foreach (var x in sysButtons) x.Enabled = false;
                UseWaitCursor = true;
                Action<string> say = m => { Log(m); UI(() => sysOut.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + m + Environment.NewLine)); };
                say(text + "...");
                Task.Factory.StartNew(() => work(say), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
                {
                    if (t.IsFaulted) say("Stopped: " + t.Exception.GetBaseException().Message);
                    sysBusy = false; UseWaitCursor = false;
                    foreach (var x in sysButtons) x.Enabled = !((bool)x.Tag && testMode);
                }));
            };
            return b;
        }

        void BuildSystemSpace()
        {
            var t = Page(3);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 70)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };

            var gSxs = Box("Windows component store (WinSxS)", out lblSxs, "Old copies of Windows parts kept after updates. Windows' own DISM tool removes the ones no longer needed. Never delete WinSxS by hand.");
            SysButton(gSxs, "Check what can be cleaned", false, say =>
            {
                int n; bool? rec;
                string outp = SystemSpace.AnalyzeComponentStore(out n, out rec);
                foreach (var line in outp.Split('\n').Where(l => l.Contains(":") && (l.Contains("Size") || l.Contains("Reclaimable") || l.Contains("Recommended") || l.Contains("Date"))))
                    say("  " + line.Trim());
                UI(() => lblSxs.Text = rec == null ? "Checked. See the details below." :
                    (rec.Value ? "Cleanup recommended: " + n + " old packages can be removed." : "No cleanup needed right now (" + Math.Max(0, n) + " old packages)."));
            });
            SysButton(gSxs, "Clean up now (5-20 min)", true, say =>
            {
                long before = Sys.Free();
                string o; SystemSpace.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", 2 * 60 * 60 * 1000, out o);
                say("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - before)) + ".");
            });

            var gRes = Box("Reserved storage", out lblReserved, "Windows keeps about 7 GB aside so updates always have room. Turning it off frees that space; turn it back on before big Windows feature updates.");
            SysButton(gRes, "Check", false, say => { string st = SystemSpace.ReservedStorageState(); say("  Reserved storage: " + st); UI(() => lblReserved.Text = "Reserved storage is " + st + "."); });
            SysButton(gRes, "Turn off (frees ~7 GB)", true, say =>
            {
                long before = Sys.Free();
                string r = SystemSpace.SetReservedStorage(false);
                say("  " + r + (r == "OK" ? " Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - before)) + " (sometimes only after a restart)." : ""));
                UI(() => lblReserved.Text = "Reserved storage: " + SystemSpace.ReservedStorageState() + ".");
            }, "Turn off reserved storage?\n\nThis frees about 7 GB. If a big Windows update later fails for lack of space, turn it back on here first.");
            SysButton(gRes, "Turn on", true, say => { say("  " + SystemSpace.SetReservedStorage(true)); UI(() => lblReserved.Text = "Reserved storage: " + SystemSpace.ReservedStorageState() + "."); });

            var gShadow = Box("Restore points", out lblShadow, "Restore points let you undo system changes. Windows deletes the oldest ones automatically to stay under the limit you set here.");
            SysButton(gShadow, "Check", false, say => ShowShadow(say));
            cmbShadow = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(90), Margin = new Padding(S(10), S(6), S(4), 0) };
            cmbShadow.Items.AddRange(new object[] { "5 GB", "10 GB", "15 GB", "5%", "10%" });
            cmbShadow.SelectedIndex = 1;
            var shadowRow = ((FlowLayoutPanel)gShadow.Tag).Controls.OfType<FlowLayoutPanel>().First();
            shadowRow.Controls.Add(new Label { AutoSize = true, Text = "Limit:", Margin = new Padding(S(10), S(8), 0, 0) });
            shadowRow.Controls.Add(cmbShadow);
            SysButton(gShadow, "Set limit", true, say =>
            {
                string size = "";
                Invoke((Action)(() => size = ((string)cmbShadow.SelectedItem).Replace(" ", "")));
                string o; int c = SystemSpace.Run("vssadmin.exe", SystemSpace.ResizeArgs(size), 120000, out o);
                say(c == 0 ? "  Limit set to " + size + "." : "  Could not set the limit: " + o);
                ShowShadow(say);
            });

            var gCompact = Box("Compact OS", out lblCompact, "Windows compresses its own files (about 2 GB on most PCs). Safe and reversible; can be slightly slower on very old PCs. Takes 10-20 minutes.");
            SysButton(gCompact, "Check", false, say => { string o = SystemSpace.CompactOsQuery(); say("  " + o.Replace("\n", " ").Trim()); UI(() => lblCompact.Text = o.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? "Checked."); });
            SysButton(gCompact, "Turn on", true, say => { long b = Sys.Free(); say("  " + SystemSpace.CompactOs(true)); say("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - b)) + "."); },
                "Turn on Compact OS?\n\nWindows compresses its own files. It takes 10-20 minutes and can be turned off again here.");
            SysButton(gCompact, "Turn off", true, say => say("  " + SystemSpace.CompactOs(false)));

            var gVhd = Box("WSL and Docker disks", out lblVhd, "Linux (WSL) and Docker disks grow but never shrink by themselves. Shrinking stops WSL first; close Docker Desktop before you start.");
            SysButton(gVhd, "Find", false, say =>
            {
                var disks = SystemSpace.FindVirtualDisks();
                foreach (var d in disks) { long len = 0; try { len = new FileInfo(d).Length; } catch { } say("  " + d + "  " + Fs.Fmt(len)); }
                UI(() => lblVhd.Text = disks.Count == 0 ? "No WSL or Docker disks found." : disks.Count + " disks found, " + Fs.Fmt(disks.Sum(d => { try { return new FileInfo(d).Length; } catch { return 0L; } })) + " in total.");
            });
            SysButton(gVhd, "Shrink them", true, say =>
            {
                var disks = SystemSpace.FindVirtualDisks();
                if (disks.Count == 0) { say("  No WSL or Docker disks found."); return; }
                long saved = SystemSpace.CompactVirtualDisks(disks, say);
                say("  Saved " + Fs.Fmt(saved) + ".");
            }, "Shrink WSL and Docker disks?\n\nWSL is stopped first (open Linux windows close). Close Docker Desktop before you continue.");

            flow.Controls.AddRange(new Control[] { gSxs, BuildHibernationBox(), gRes, gShadow, gCompact, gVhd });
            sysOut = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window };
            t.Controls.Add(Title("System space"), 0, 0); t.Controls.Add(flow, 0, 1); t.Controls.Add(sysOut, 0, 2);
            pageSys.Controls.Add(t);
        }

        void ShowShadow(Action<string> say)
        {
            var s = SystemSpace.ShadowForC();
            string text = s == null ? "No restore point storage on C: (System Protection may be off)."
                : "Restore points use " + Fs.Fmt(s.Used) + " (limit " + (s.Max >= long.MaxValue / 2 ? "none" : Fs.Fmt(s.Max)) + ").";
            say("  " + text);
            UI(() => lblShadow.Text = text);
        }

        // ================= Duplicates =================
        void BuildDuplicates()
        {
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            dupStatus = Note("Finds files with exactly the same content in your own folders, on any drive and across drives (compared byte for byte by fingerprint). Windows, apps and AppData are never searched. Extra copies go to Undo, so you can get them back (Tools > Undo).");
            var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, S(6)) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dupFolders = new ListBox { Dock = DockStyle.Fill, Height = S(96), IntegralHeight = false };
            foreach (var f in Duplicates.DefaultFolders()) dupFolders.Items.Add(f);
            var side = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(S(6), 0, 0, 0) };
            var btnAdd = MakeButton("Add folder..."); var btnRemove = MakeButton("Remove folder");
            var btnDrive = MakeButton("Add a whole drive...");
            cmbDupMin = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(130) };
            cmbDupMin.Items.AddRange(new object[] { "Files over 1 MB", "Files over 10 MB", "Files over 100 MB" });
            cmbDupMin.SelectedIndex = 1;
            side.Controls.AddRange(new Control[] { btnAdd, btnDrive, btnRemove, cmbDupMin });
            top.Controls.Add(dupFolders, 0, 0); top.Controls.Add(side, 1, 0);

            dupList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            dupList.Columns.Add("File", S(560)); dupList.Columns.Add("Size", S(85), HorizontalAlignment.Right); dupList.Columns.Add("Modified", S(95)); dupList.Columns.Add("", S(140));
            dupList.ItemChecked += (o, e) => UpdateDupButton();
            dupList.DoubleClick += (o, e) => { if (dupList.SelectedItems.Count > 0) try { Shell.Select(((FileInfo)dupList.SelectedItems[0].Tag).FullName); } catch { } };

            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnDupFind = MakeButton("Find duplicates"); btnDupFind.Font = new Font("Segoe UI Semibold", 9f);
            btnDupStop = MakeButton("Stop"); btnDupStop.Enabled = false;
            btnDupSelect = MakeButton("Select extra copies");
            btnDupRecycle = MakeButton("Move selected to Undo..."); btnDupRecycle.Enabled = false;
            row.Controls.AddRange(new Control[] { btnDupFind, btnDupStop, btnDupSelect, btnDupRecycle });

            t.Controls.Add(Title("Duplicate files"), 0, 0); t.Controls.Add(dupStatus, 0, 1); t.Controls.Add(top, 0, 2); t.Controls.Add(dupList, 0, 3); t.Controls.Add(row, 0, 4);
            pageDups.Controls.Add(t);

            btnAdd.Click += (o, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Choose a folder to search for duplicates" })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        if (!Duplicates.Allowed(d.SelectedPath)) Lang.Box(this, "Windows, app and AppData folders are never searched.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else if (!dupFolders.Items.Contains(d.SelectedPath)) dupFolders.Items.Add(d.SelectedPath);
                    }
            };
            btnRemove.Click += (o, e) => { if (dupFolders.SelectedItem != null) dupFolders.Items.Remove(dupFolders.SelectedItem); };
            btnDrive.Click += (o, e) =>
            {
                var menu = new ContextMenuStrip();
                foreach (var r in Duplicates.DriveRoots())
                {
                    string root = r;
                    menu.Items.Add(Lang.T(Duplicates.DriveLabel(root)), null, (o2, e2) => AddDupFolder(root));
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(Lang.T("All drives"), null, (o2, e2) => { dupFolders.Items.Clear(); foreach (var r in Duplicates.DriveRoots()) AddDupFolder(r); });
                Lang.Menu(menu);
                menu.Show(btnDrive, new Point(0, btnDrive.Height));
            };
            btnDupFind.Click += (o, e) => FindDuplicates();
            btnDupStop.Click += (o, e) => { if (dupCts != null) dupCts.Cancel(); };
            btnDupSelect.Click += (o, e) => { foreach (ListViewItem r in dupList.Items) r.Checked = !"keep".Equals(r.SubItems[3].Tag); };
            btnDupRecycle.Click += (o, e) => RecycleDuplicates();
        }

        void AddDupFolder(string p)
        {
            if (!dupFolders.Items.Cast<string>().Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) dupFolders.Items.Add(p);
        }

        // from the Dashboard: "Find duplicates" on one drive (for C:, your own folders)
        public void FindDuplicatesOn(string root)
        {
            ShowTools(pageDups);
            if (dupCts != null) return;
            dupFolders.Items.Clear();
            foreach (var f in Duplicates.SearchRoot(root)) AddDupFolder(f);
            FindDuplicates();
        }
        public List<string> DupFolderList { get { return dupFolders.Items.Cast<string>().ToList(); } }   // self-test

        void UpdateDupButton()
        {
            var sel = dupList.CheckedItems.Cast<ListViewItem>().ToList();
            btnDupRecycle.Enabled = sel.Count > 0 && !testMode && dupCts == null;
            btnDupRecycle.Text = Lang.T(sel.Count > 0 ? "Move " + sel.Count + " to Undo (" + Fs.Fmt(sel.Sum(r => ((FileInfo)r.Tag).Length)) + ")..." : "Move selected to Undo...");
        }

        public void FindDuplicates()
        {
            if (dupCts != null) return;
            var folders = dupFolders.Items.Cast<string>().ToList();
            long min = cmbDupMin.SelectedIndex == 0 ? 1L << 20 : cmbDupMin.SelectedIndex == 1 ? 10L << 20 : 100L << 20;
            dupCts = new CancellationTokenSource();
            var token = dupCts.Token;
            btnDupFind.Enabled = false; btnDupStop.Enabled = true;
            dupList.Items.Clear(); dupList.Groups.Clear();
            string last = "";
            Task.Factory.StartNew(() => Duplicates.Find(folders, min, s => { if (s != last) { last = s; UI(() => dupStatus.Text = s.Length > 120 ? s.Substring(0, 117) + "..." : s); } }, token),
                TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                dupCts = null; btnDupFind.Enabled = true; btnDupStop.Enabled = false;
                if (t.IsFaulted) { dupStatus.Text = "Search stopped: " + t.Exception.GetBaseException().Message; return; }
                ShowDuplicates(t.Result, token.IsCancellationRequested);
            }));
        }

        void ShowDuplicates(List<DupGroup> groups, bool stopped)
        {
            dupList.BeginUpdate();
            foreach (var g in groups)
            {
                var lg = new ListViewGroup(g.Files.Count + " copies of " + Fs.Fmt(g.Size) + "  -  " + Fs.Fmt(g.Wasted) + " extra", HorizontalAlignment.Left);
                dupList.Groups.Add(lg);
                for (int i = 0; i < g.Files.Count; i++)
                {
                    var f = g.Files[i];
                    var lvi = new ListViewItem(f.FullName) { Tag = f, Group = lg, UseItemStyleForSubItems = false, ToolTipText = f.FullName };
                    lvi.SubItems.Add(Fs.Fmt(f.Length));
                    lvi.SubItems.Add(f.LastWriteTime.ToString("yyyy-MM-dd"));
                    var k = lvi.SubItems.Add(i == 0 ? "keep (oldest copy)" : "extra copy");
                    k.Tag = i == 0 ? "keep" : "extra";
                    k.ForeColor = i == 0 ? SafeColor : ReviewColor;
                    dupList.Items.Add(lvi);
                }
            }
            dupList.EndUpdate();
            dupStatus.Text = (stopped ? "Stopped. " : "") + (groups.Count == 0 ? "No duplicates found." :
                groups.Count + " sets of duplicates, " + Fs.Fmt(groups.Sum(g => g.Wasted)) + " in extra copies. Click \"Select extra copies\" to keep one of each. Double-click a file to see it.");
            UpdateDupButton();
        }

        void RecycleDuplicates()
        {
            if (testMode) return;
            var sel = dupList.CheckedItems.Cast<ListViewItem>().ToList();
            // never all copies of a set
            var blocked = sel.GroupBy(r => r.Group).Where(g => g.Count() == g.Key.Items.Count).Select(g => g.Key.Header).ToList();
            if (blocked.Count > 0)
            {
                Lang.Box(this, "Keep at least one copy in each set. Untick one file in:\n\n" + string.Join("\n", blocked), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // to Undo, kept for the days you chose (also from USB drives, which have no Recycle Bin)
            int keep = TidyPrefs.Load().UndoDays;
            if (Lang.Box(this, Lang.T("Move " + sel.Count + " files (" + Fs.Fmt(sel.Sum(r => ((FileInfo)r.Tag).Length)) + ") to Undo?") + "\n\n" +
                    Lang.T("They are kept for " + keep + " days, and Tools > Undo puts them back. After that they are deleted for good. You can change the days in Automation."),
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var paths = sel.Select(r => ((FileInfo)r.Tag).FullName).ToList();
            btnDupRecycle.Enabled = false; dupStatus.Text = Lang.T("Moving " + sel.Count + " files to Undo...");
            Task.Factory.StartNew(() => Quarantine.MoveToUndo("Duplicates (" + paths.Count + " files)", paths, null, keep), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                int ok = 0;
                foreach (var r in sel) if (!File.Exists(((FileInfo)r.Tag).FullName)) { ok++; dupList.Items.Remove(r); }
                long bytes = t.IsFaulted ? 0 : t.Result;
                string msg = t.IsFaulted ? "Could not move the files to Undo: " + t.Exception.GetBaseException().Message
                                         : "Moved " + ok + " of " + sel.Count + " files (" + Fs.Fmt(bytes) + ") to Undo, kept for " + keep + " days (Tools > Undo puts them back).";
                Log("Duplicates: " + msg);
                dupStatus.Text = Lang.T(msg);
                if (ok > 0) try { Activity.Add("clean", "Duplicates: " + ok + " files to Undo", bytes); } catch { }
                UpdateDupButton();
            }));
        }

        // ================= Undo =================
        void BuildUndo()
        {
            var t = Page(4);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            undoStatus = Note("Files you remove, Caution items and removed drivers are kept here for a while before they are deleted for good. Restore puts them back exactly where they were.");
            undoList = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, MultiSelect = false, ShowItemToolTips = true };
            undoList.Columns.Add("When", S(130)); undoList.Columns.Add("What", S(330)); undoList.Columns.Add("Size", S(85), HorizontalAlignment.Right);
            undoList.Columns.Add("Deleted for good in", S(140)); undoList.Columns.Add("Kept at", S(380));
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnUndoRestore = MakeButton("Restore"); btnUndoRestore.Font = new Font("Segoe UI Semibold", 9f);
            btnUndoDelete = MakeButton("Delete now");
            btnUndoOpen = MakeButton("Open folder");
            btnUndoRefresh = MakeButton("Refresh");
            row.Controls.AddRange(new Control[] { btnUndoRestore, btnUndoDelete, btnUndoOpen, btnUndoRefresh });
            t.Controls.Add(Title("Undo"), 0, 0); t.Controls.Add(undoStatus, 0, 1); t.Controls.Add(undoList, 0, 2); t.Controls.Add(row, 0, 3);
            pageUndo.Controls.Add(t);
            undoList.SelectedIndexChanged += (o, e) => UpdateUndoButtons();
            btnUndoRefresh.Click += (o, e) => RefreshUndo();
            btnUndoOpen.Click += (o, e) => { var b = SelectedUndo(); if (b != null) try { Shell.Open(b.Dir); } catch { } };
            btnUndoRestore.Click += (o, e) =>
            {
                var b = SelectedUndo(); if (b == null || testMode) return;
                if (Lang.Box(this, "Put \"" + b.Label + "\" back where it was?" + (b.Kind == "drivers" ? "\n\nThe drivers are added back to Windows." : ""),
                        "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                btnUndoRestore.Enabled = false;
                Task.Factory.StartNew(() => Quarantine.Restore(b, Log)).ContinueWith(t2 => UI(() =>
                {
                    int n = t2.IsFaulted ? 0 : t2.Result;
                    Log("Undo: restored " + n + " of " + b.Items.Count + " from \"" + b.Label + "\".");
                    Lang.Box(this, "Restored " + n + " of " + b.Items.Count + ".", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    RefreshUndo();
                }));
            };
            btnUndoDelete.Click += (o, e) =>
            {
                var b = SelectedUndo(); if (b == null || testMode) return;
                if (Lang.Box(this, "Delete \"" + b.Label + "\" for good now?\n\nIt can no longer be restored.", "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                Quarantine.Delete(b);
                Log("Undo: deleted \"" + b.Label + "\" (" + Fs.Fmt(b.Bytes) + ").");
                RefreshUndo();
            };
            UpdateUndoButtons();
        }

        UndoBatch SelectedUndo() { return undoList.SelectedItems.Count == 0 ? null : (UndoBatch)undoList.SelectedItems[0].Tag; }

        void UpdateUndoButtons()
        {
            bool sel = SelectedUndo() != null;
            btnUndoRestore.Enabled = sel && !testMode; btnUndoDelete.Enabled = sel && !testMode; btnUndoOpen.Enabled = sel;
        }

        public void RefreshUndo()
        {
            Task.Factory.StartNew(() => Quarantine.List()).ContinueWith(t => UI(() =>
            {
                undoList.BeginUpdate();
                undoList.Items.Clear();
                if (!t.IsFaulted)
                    foreach (var b in t.Result)
                    {
                        var lvi = new ListViewItem(b.Time.ToString("d MMM yyyy HH:mm")) { Tag = b, ToolTipText = string.Join("\n", b.Items.Select(i => i[1]).Take(15)) };
                        lvi.SubItems.Add(b.Label);
                        lvi.SubItems.Add(Fs.Fmt(b.Bytes));
                        int d = b.DaysLeft();
                        lvi.SubItems.Add(d <= 0 ? "today" : d == 1 ? "1 day" : d + " days");
                        lvi.SubItems.Add(b.Dir);
                        undoList.Items.Add(lvi);
                    }
                undoList.EndUpdate();
                int n = undoList.Items.Count;
                undoStatus.Text = n == 0 ? "Nothing in Undo. Files you remove are kept here for " + TidyPrefs.Load().UndoDays + " days, Caution items and drivers for " + Quarantine.KeepDays + " days."
                    : n + (n == 1 ? " item, " : " items, ") + Fs.Fmt(t.Result.Sum(b => b.Bytes)) + " in total. Each is deleted for good when its time runs out (see the last columns). Select one to restore it.";
                UpdateUndoButtons();
                if (UndoDone != null) UndoDone();
            }));
        }
    }
}
