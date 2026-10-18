// Tools > Disk health: the drives' own health, wear, temperature and error counters. Read only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pageDisk;
        ListView diskList; Label diskSummary, diskStatus; TextBox diskDetail; Button btnDiskCheck, btnDiskWindows, btnDiskBackup;
        bool diskBusy, diskLoaded;
        public event Action DiskHealthDone;
        public int DiskRows { get { return diskList == null ? 0 : diskList.Items.Count; } }

        static Color LevelColor(HealthLevel l)
        {
            return l == HealthLevel.Failing ? DangerColor : l == HealthLevel.Warning ? CautionColor : l == HealthLevel.Healthy ? SafeColor : Color.DimGray;
        }

        void BuildDiskHealth()
        {
            var t = Page(6);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            diskStatus = Note("Reads the health, wear and error counters your drives keep about themselves (the same data Windows uses), so you hear about a failing drive " +
                              "before you lose files. Elixar only reads: nothing is written to the drives, nothing is downloaded and nothing is sent anywhere.");
            diskSummary = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 11.5f), Text = "Not checked yet.", Margin = new Padding(S(1), 0, 0, S(6)) };
            diskList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true, HideSelection = false, MultiSelect = false };
            diskList.Columns.Add("Drive", S(270)); diskList.Columns.Add("Type", S(120)); diskList.Columns.Add("Size", S(80), HorizontalAlignment.Right);
            diskList.Columns.Add("Health", S(130)); diskList.Columns.Add("Life left", S(75), HorizontalAlignment.Right); diskList.Columns.Add("Temperature", S(95), HorizontalAlignment.Right);
            diskList.Columns.Add("Errors", S(140)); diskList.Columns.Add("Powered on", S(95), HorizontalAlignment.Right);
            diskDetail = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Segoe UI", 9.5f),
                                       Margin = new Padding(0, S(6), 0, 0), Text = "" };
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnDiskCheck = MakeButton("Check again");
            btnDiskBackup = MakeButton("Back up files...");
            btnDiskWindows = MakeButton("Windows' Disks & volumes"); btnDiskWindows.UseMnemonic = false;
            row.Controls.AddRange(new Control[] { btnDiskCheck, btnDiskBackup, btnDiskWindows });
            t.Controls.Add(Title("Disk health"), 0, 0); t.Controls.Add(diskStatus, 0, 1); t.Controls.Add(diskSummary, 0, 2);
            t.Controls.Add(diskList, 0, 3); t.Controls.Add(diskDetail, 0, 4); t.Controls.Add(row, 0, 5);
            pageDisk.Controls.Add(t);
            btnDiskCheck.Click += (o, e) => RefreshDiskHealth();
            // Windows' own pages; Elixar does not change any drive setting itself
            btnDiskBackup.Click += (o, e) => { try { Process.Start(Win.SystemExe("explorer.exe"), "ms-settings:backup"); } catch { } };
            btnDiskWindows.Click += (o, e) => { try { Process.Start(Win.SystemExe("explorer.exe"), "ms-settings:disksandvolumes"); } catch { } };
            diskList.SelectedIndexChanged += (o, e) => ShowDiskDetail();
        }

        // always reads afresh: it takes a few seconds and only reads
        public void ShowDiskHealth() { ShowTools(pageDisk); if (diskLoaded) RefreshDiskHealth(); }

        public void RefreshDiskHealth()
        {
            if (diskBusy) return;
            diskBusy = true; diskLoaded = true; btnDiskCheck.Enabled = false;
            diskList.Items.Clear(); diskDetail.Text = "";
            diskSummary.ForeColor = Color.DimGray; diskSummary.Text = Lang.T("Reading your drives (a few seconds)...");
            Task.Factory.StartNew(() => DiskHealth.Read(), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                diskBusy = false; btnDiskCheck.Enabled = true;
                var rep = t.IsFaulted ? new DiskReport { Error = "Could not read disk health: " + t.Exception.GetBaseException().Message } : t.Result;
                ShowDiskReport(rep);
                if (DiskHealthDone != null) DiskHealthDone();
            }));
        }

        void ShowDiskReport(DiskReport rep)
        {
            lastDiskReport = rep;
            diskList.BeginUpdate();
            diskList.Items.Clear();
            foreach (var d in rep.Disks)
            {
                var lvi = new ListViewItem(d.Label) { Tag = d, UseItemStyleForSubItems = false, ToolTipText = string.Join("\n", d.Reasons.Concat(d.Notes)) };
                lvi.SubItems.Add(Lang.T(d.Kind));
                lvi.SubItems.Add(d.Size > 0 ? Fs.Fmt(d.Size) : "");
                var h = lvi.SubItems.Add(Lang.T(DiskHealth.LevelText(d.Level)));
                h.ForeColor = Theme.MapFore(LevelColor(d.Level)); h.Font = new Font(diskList.Font, FontStyle.Bold);
                var life = lvi.SubItems.Add(d.LifeLeft >= 0 ? d.LifeLeft + "%" : "");
                if (d.LifeLeft >= 0 && d.Wear >= DiskHealth.WearWarn) life.ForeColor = Theme.MapFore(LevelColor(d.Wear >= 100 ? HealthLevel.Failing : HealthLevel.Warning));
                var temp = lvi.SubItems.Add(d.Temp > 0 ? d.Temp + " °C" : "");
                if (d.Temp >= d.HotAt) temp.ForeColor = Theme.MapFore(CautionColor);
                var err = lvi.SubItems.Add(Lang.T(DiskHealth.Errors(d)));
                if (d.Uncorrected > 0) err.ForeColor = Theme.MapFore(CautionColor);
                lvi.SubItems.Add(Lang.T(DiskHealth.Hours(d.Hours)));
                diskList.Items.Add(lvi);
            }
            diskList.EndUpdate();
            diskSummary.Text = Lang.T(rep.Summary);
            diskSummary.ForeColor = Theme.MapFore(rep.Error != null ? Color.DimGray : LevelColor(rep.Worst));
            if (diskList.Items.Count > 0) diskList.Items[0].Selected = true;
            else diskDetail.Text = Lang.T(rep.Summary);
            UpdateDashDiskButton(rep);
        }

        void ShowDiskDetail()
        {
            if (diskList.SelectedItems.Count == 0) return;
            var d = (DiskInfo)diskList.SelectedItems[0].Tag;
            var lines = new List<string> { d.Label + "  -  " + Lang.T(DiskHealth.LevelText(d.Level)), "" };
            if (d.Reasons.Count > 0) { lines.Add(Lang.T("Why:")); lines.AddRange(d.Reasons.Select(r => "  • " + Lang.T(r))); lines.Add(""); }
            lines.Add(Lang.T("What to do:") + " " + Lang.T(d.Advice));
            if (d.Notes.Count > 0) { lines.Add(""); lines.AddRange(d.Notes.Select(n => "• " + Lang.T(n))); }
            var facts = new List<string>();
            if (d.Health.Length > 0) facts.Add(Lang.T("Windows says:") + " " + d.Health + (d.OpStatus.Length > 0 ? " (" + d.OpStatus + ")" : ""));
            if (d.PredictFailure.HasValue) facts.Add(Lang.T("S.M.A.R.T. failure warning:") + " " + Lang.T(d.PredictFailure.Value ? "yes" : "no"));
            if (d.TempMax > 0) facts.Add(Lang.T("Highest temperature recorded:") + " " + d.TempMax + " °C");
            if (facts.Count > 0) { lines.Add(""); lines.AddRange(facts); }
            diskDetail.Text = string.Join(Environment.NewLine, lines);
        }

        // ---- the Dashboard's Disk health tile follows the last check ----
        DiskReport lastDiskReport;

        void UpdateDashDiskButton(DiskReport rep)
        {
            if (rep != null) RefreshOverview();
        }

        // a quiet check in the background when Elixar opens
        void CheckDisksQuietly()
        {
            if (!Win.IsWindows) return;
            Task.Factory.StartNew(() => DiskHealth.Read(), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                if (t.IsFaulted || diskLoaded) return;
                lastDiskReport = t.Result;
                UpdateDashDiskButton(t.Result);
            }));
        }
    }
}
