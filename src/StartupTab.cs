// Tools > Startup: what starts with Windows, checked for signatures and odd places.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        ListView startList; Label startStatus;
        Button btnStartOff, btnStartOn, btnStartLeft, btnStartScan, btnStartOpen, btnStartRefresh;
        bool startBusy, startLoaded;
        public event Action StartupDone;
        static readonly Color DangerColor = Color.FromArgb(199, 54, 47);

        void BuildStartup()
        {
            var t = Page(4);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            startStatus = Note("Everything that starts by itself when you sign in. Elixar checks who signed each program and where it runs from. " +
                               "Turning one off works like Task Manager's Startup tab and can be turned back on any time.");
            startList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            startList.Columns.Add("Program", S(220)); startList.Columns.Add("Starts", S(60)); startList.Columns.Add("Check", S(430));
            startList.Columns.Add("File", S(380)); startList.Columns.Add("Where", S(200));
            startList.ItemChecked += (o, e) => UpdateStartButtons();
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnStartOff = MakeButton("Turn off"); btnStartOn = MakeButton("Turn on");
            btnStartLeft = MakeButton("Remove leftover entries...");
            btnStartScan = MakeButton("Scan with Microsoft Defender");
            btnStartOpen = MakeButton("Open file location");
            btnStartRefresh = MakeButton("Check again");
            row.Controls.AddRange(new Control[] { btnStartOff, btnStartOn, btnStartLeft, btnStartScan, btnStartOpen, btnStartRefresh });
            t.Controls.Add(Title("Startup programs"), 0, 0); t.Controls.Add(startStatus, 0, 1); t.Controls.Add(startList, 0, 2); t.Controls.Add(row, 0, 3);
            pageStartup.Controls.Add(t);
            btnStartOff.Click += (o, e) => SwitchStartup(false);
            btnStartOn.Click += (o, e) => SwitchStartup(true);
            btnStartLeft.Click += (o, e) => RemoveStartupLeftovers();
            btnStartScan.Click += (o, e) => ScanStartup();
            btnStartOpen.Click += (o, e) =>
            {
                var sel = StartSelected().FirstOrDefault(x => !x.Missing);
                if (sel != null) try { Shell.Select(sel.File); } catch { }
            };
            btnStartRefresh.Click += (o, e) => RefreshStartup();
            UpdateStartButtons();
        }

        List<StartupEntry> StartSelected() { return startList.CheckedItems.Cast<ListViewItem>().Select(r => (StartupEntry)r.Tag).ToList(); }

        void UpdateStartButtons()
        {
            var sel = StartSelected();
            bool can = !startBusy && !testMode;
            btnStartOff.Enabled = can && sel.Any(x => x.Enabled);
            btnStartOn.Enabled = can && sel.Any(x => !x.Enabled);
            btnStartLeft.Enabled = can && startList.Items.Cast<ListViewItem>().Any(r => ((StartupEntry)r.Tag).Missing);
            btnStartScan.Enabled = !startBusy && sel.Any(x => !x.Missing);
            btnStartOpen.Enabled = sel.Any(x => !x.Missing);
            btnStartRefresh.Enabled = !startBusy;
        }

        public void RefreshStartup()
        {
            if (startBusy) return;
            startBusy = true; startLoaded = true; UpdateStartButtons();
            startStatus.Text = "Reading startup programs and checking their signatures...";
            Task.Factory.StartNew(() => StartupApps.Read(true), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                startBusy = false;
                if (t.IsFaulted) startStatus.Text = "Could not read startup programs: " + t.Exception.GetBaseException().Message;
                else ShowStartupList(t.Result);
                UpdateStartButtons();
                if (StartupDone != null) StartupDone();
            }));
        }

        void ShowStartupList(List<StartupEntry> list)
        {
            startList.BeginUpdate(); startList.Items.Clear(); startList.Groups.Clear();
            var gLook = new ListViewGroup("Needs a look", HorizontalAlignment.Left);
            var gLeft = new ListViewGroup("Leftovers - the program is gone", HorizontalAlignment.Left);
            var gOk = new ListViewGroup("Signed by a known publisher", HorizontalAlignment.Left);
            startList.Groups.AddRange(new[] { gLook, gLeft, gOk });
            foreach (var e in list.OrderBy(x => x.Flag == StartupFlag.Danger ? 0 : x.Flag == StartupFlag.Review ? 1 : 2).ThenBy(x => x.Name))
            {
                var g = e.Flag == StartupFlag.Leftover ? gLeft : e.Flag == StartupFlag.Ok ? gOk : gLook;
                var r = new ListViewItem(e.Name) { Tag = e, Group = g, UseItemStyleForSubItems = false, ToolTipText = e.Command };
                r.SubItems.Add(e.Enabled ? "On" : "Off").ForeColor = e.Enabled ? Color.Black : Color.Gray;
                var c = r.SubItems.Add(e.Why);
                c.ForeColor = e.Flag == StartupFlag.Danger ? DangerColor : e.Flag == StartupFlag.Ok ? SafeColor : CautionColor;
                r.SubItems.Add(e.File);
                r.SubItems.Add(e.Where).ForeColor = Color.DimGray;
                if (!e.Enabled) r.ForeColor = Color.Gray;
                startList.Items.Add(r);
            }
            startList.EndUpdate();
            int danger = list.Count(x => x.Flag == StartupFlag.Danger), review = list.Count(x => x.Flag == StartupFlag.Review), left = list.Count(x => x.Missing);
            startStatus.Text = list.Count + " programs start with Windows (" + list.Count(x => x.Enabled) + " turned on). " +
                (danger + review + left == 0 ? "All are signed by known publishers." :
                 (danger > 0 ? danger + " look unusual, " : "") + (review > 0 ? review + " are not signed, " : "") + (left > 0 ? left + " are leftovers. " : "") +
                 "Not signed doesn't mean harmful: many small tools aren't signed. Turn off what you don't recognise, or scan it with Microsoft Defender.");
        }

        void SwitchStartup(bool on)
        {
            var sel = StartSelected().Where(x => x.Enabled != on).ToList();
            if (sel.Count == 0 || testMode) return;
            int ok = sel.Count(x => StartupApps.SetEnabled(x, on));
            Log("Startup: turned " + (on ? "on " : "off ") + ok + " of " + sel.Count + ": " + string.Join(", ", sel.Select(x => x.Name)));
            RefreshStartup();
        }

        void RemoveStartupLeftovers()
        {
            var left = startList.Items.Cast<ListViewItem>().Select(r => (StartupEntry)r.Tag).Where(x => x.Missing).ToList();
            if (left.Count == 0 || testMode) return;
            if (Lang.Box(this, "Remove " + left.Count + " startup entries whose program is gone?\n\n" + string.Join("\n", left.Take(12).Select(x => "  - " + x.Name)) +
                    "\n\nThey are kept in Tools > Undo for 7 days.", "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int n = StartupApps.RemoveLeftovers(left, Log);
            Log("Startup: removed " + n + " leftover entries.");
            RefreshStartup();
        }

        void ScanStartup()
        {
            var files = StartSelected().Where(x => !x.Missing).Select(x => x.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0 || startBusy) return;
            startBusy = true; UpdateStartButtons();
            startStatus.Text = "Microsoft Defender is scanning " + files.Count + " file(s)...";
            Task.Factory.StartNew(() =>
            {
                var res = new List<string>();
                foreach (var f in files)
                {
                    string o;
                    int code = StartupApps.DefenderScan(f, out o);
                    res.Add((code == 0 ? "No threats: " : code == 2 ? "THREAT FOUND (Defender is dealing with it): " : "Could not scan: ") + f + (code != 0 && code != 2 ? " (" + o.Trim() + ")" : ""));
                    Log("Defender: " + res.Last());
                }
                return res;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                startBusy = false; UpdateStartButtons();
                var lines = t.IsFaulted ? new List<string> { t.Exception.GetBaseException().Message } : t.Result;
                startStatus.Text = "Defender scan finished.";
                Lang.Box(this, string.Join("\n\n", lines), "Microsoft Defender", MessageBoxButtons.OK,
                    lines.Any(l => l.StartsWith("THREAT")) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                if (lines.Any(l => l.StartsWith("THREAT"))) RefreshStartup();
            }));
        }
    }
}
