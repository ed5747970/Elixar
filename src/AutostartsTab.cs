// Startup > Also starts by itself: scheduled tasks and services that apps added (Autostarts).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        ListView autoList; Label autoStatus; CheckBox chkAutoWindows;
        Button btnAutoOff, btnAutoOn, btnAutoTasks, btnAutoServices, btnAutoScan;
        bool autoBusy; List<AutoEntry> autoAll = new List<AutoEntry>();
        public event Action AutostartsDone;   // self-test

        void BuildAutostarts(TableLayoutPanel t, int at)
        {
            var title = Title("Also starts by itself"); title.Margin = new Padding(0, S(14), 0, S(2));
            autoStatus = Note("Scheduled tasks and services that apps added. Programs can start themselves this way without showing in the list above; that's where unwanted software often hides. " +
                              "Tasks can be turned off and on again. Services are only shown: turning one off can break the app or Windows.");
            autoList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            autoList.Columns.Add("Name", S(220)); autoList.Columns.Add("Kind", S(70)); autoList.Columns.Add("Runs", S(120)); autoList.Columns.Add("On", S(50));
            autoList.Columns.Add("Check", S(360)); autoList.Columns.Add("Program", S(380)); autoList.Columns.Add("Where", S(220));
            autoList.ItemChecked += (o, e) => UpdateAutoButtons();
            var row = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) });
            btnAutoOff = MakeButton("Turn off selected tasks"); btnAutoOn = MakeButton("Turn on");
            btnAutoScan = MakeButton("Scan with Microsoft Defender");
            btnAutoTasks = MakeButton("Open Task Scheduler"); btnAutoServices = MakeButton("Open Services");
            chkAutoWindows = new CheckBox { AutoSize = true, Text = "Show Windows' own", Margin = new Padding(S(10), S(8), 0, 0) };
            row.Controls.AddRange(new Control[] { btnAutoOff, btnAutoOn, btnAutoScan, btnAutoTasks, btnAutoServices, chkAutoWindows });
            t.Controls.Add(title, 0, at); t.Controls.Add(autoStatus, 0, at + 1); t.Controls.Add(autoList, 0, at + 2); t.Controls.Add(row, 0, at + 3);
            btnAutoOff.Click += (o, e) => SwitchAutostarts(false);
            btnAutoOn.Click += (o, e) => SwitchAutostarts(true);
            btnAutoScan.Click += (o, e) => ScanAutostarts();
            btnAutoTasks.Click += (o, e) => { try { System.Diagnostics.Process.Start(Win.SystemExe("mmc.exe"), Sys.ArgQuote(Win.SystemExe("taskschd.msc"))); } catch { } };
            btnAutoServices.Click += (o, e) => { try { System.Diagnostics.Process.Start(Win.SystemExe("mmc.exe"), Sys.ArgQuote(Win.SystemExe("services.msc"))); } catch { } };
            chkAutoWindows.CheckedChanged += (o, e) => ShowAutostarts();
            UpdateAutoButtons();
        }

        List<AutoEntry> AutoSelected() { return autoList.CheckedItems.Cast<ListViewItem>().Select(r => (AutoEntry)r.Tag).ToList(); }

        void UpdateAutoButtons()
        {
            var sel = AutoSelected();
            bool can = !autoBusy && !testMode && Policy.Allowed("startup.change");
            Func<AutoEntry, bool> changeable = x => x.Kind == "Task" && !x.WindowsOwn && !x.ElixarOwn;
            btnAutoOff.Enabled = can && sel.Any(x => changeable(x) && x.Enabled);
            btnAutoOn.Enabled = can && sel.Any(x => changeable(x) && !x.Enabled);
            btnAutoScan.Enabled = !autoBusy && sel.Any(x => !x.Missing);
        }

        public void RefreshAutostarts()
        {
            if (autoBusy) return;
            autoBusy = true; UpdateAutoButtons();
            autoStatus.Text = Lang.T("Reading scheduled tasks and services, and checking their signatures...");
            Task.Factory.StartNew(() => Autostarts.Read(true), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                autoBusy = false;
                if (t.IsFaulted) autoStatus.Text = Lang.T("Could not read them: ") + t.Exception.GetBaseException().Message;
                else { autoAll = t.Result; ShowAutostarts(); }
                UpdateAutoButtons();
                if (AutostartsDone != null) AutostartsDone();
            }));
        }

        void ShowAutostarts()
        {
            var list = autoAll.Where(x => chkAutoWindows.Checked || !x.WindowsOwn).ToList();
            autoList.BeginUpdate(); autoList.Items.Clear(); autoList.Groups.Clear();
            var gLook = new ListViewGroup(Lang.T("Needs a look"), HorizontalAlignment.Left);
            var gLeft = new ListViewGroup(Lang.T("Leftovers - the program is gone"), HorizontalAlignment.Left);
            var gOk = new ListViewGroup(Lang.T("Signed by a known publisher"), HorizontalAlignment.Left);
            autoList.Groups.AddRange(new[] { gLook, gLeft, gOk });
            foreach (var e in list.OrderBy(x => x.Flag == StartupFlag.Danger ? 0 : x.Flag == StartupFlag.Review ? 1 : 2).ThenBy(x => x.Name))
            {
                var g = e.Flag == StartupFlag.Leftover ? gLeft : e.Flag == StartupFlag.Ok ? gOk : gLook;
                var r = new ListViewItem(e.Name) { Tag = e, Group = g, UseItemStyleForSubItems = false, ToolTipText = e.Command };
                r.SubItems.Add(Lang.Hebrew ? (e.Kind == "Task" ? "משימה" : "שירות") : e.Kind);
                r.SubItems.Add(e.Runs);
                r.SubItems.Add(e.Enabled ? Lang.T("On") : Lang.T("Off")).ForeColor = e.Enabled ? Color.Black : Color.Gray;
                var c = r.SubItems.Add(Lang.T(e.Why));
                c.ForeColor = e.Flag == StartupFlag.Danger ? DangerColor : e.Flag == StartupFlag.Ok ? SafeColor : CautionColor;
                r.SubItems.Add(e.File);
                r.SubItems.Add(e.Where).ForeColor = Color.DimGray;
                if (!e.Enabled) r.ForeColor = Color.Gray;
                autoList.Items.Add(r);
            }
            autoList.EndUpdate();
            int look = list.Count(x => x.Flag == StartupFlag.Danger || x.Flag == StartupFlag.Review), danger = list.Count(x => x.Flag == StartupFlag.Danger);
            autoStatus.Text = Lang.Hebrew
                ? list.Count + " משימות ושירותים שאפליקציות הוסיפו" + (look > 0 ? "; " + look + " כדאי לבדוק" + (danger > 0 ? " (" + danger + " נראים כמו תוכנה לא רצויה)" : "") : ": כולם חתומים על ידי מפרסמים מוכרים") + ". משימות אפשר לכבות ולהדליק שוב; שירותים רק מוצגים."
                : list.Count + " tasks and services that apps added" + (look > 0 ? "; " + look + " need a look" + (danger > 0 ? " (" + danger + (danger == 1 ? " looks" : " look") + " like unwanted software)" : "") : ": all signed by known publishers") + ". Tasks can be turned off and on again; services are only shown.";
        }

        void SwitchAutostarts(bool on)
        {
            var sel = AutoSelected().Where(x => x.Kind == "Task" && !x.WindowsOwn && !x.ElixarOwn && x.Enabled != on).ToList();
            if (sel.Count == 0 || testMode || Policy.Blocked(this, "startup.change")) return;
            var errs = new List<string>();
            foreach (var e in sel) { var err = Autostarts.SetEnabled(e, on); if (err != null) errs.Add(e.Name + ": " + err); }
            Log("Startup: tasks turned " + (on ? "on" : "off") + ": " + (sel.Count - errs.Count) + " of " + sel.Count + (errs.Count > 0 ? " (" + string.Join("; ", errs) + ")" : ""));
            if (errs.Count > 0) Lang.Box(this, string.Join("\n", errs), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowAutostarts(); UpdateAutoButtons();
        }

        void ScanAutostarts()
        {
            var files = AutoSelected().Where(x => !x.Missing).Select(x => x.File).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0 || autoBusy) return;
            autoBusy = true; UpdateAutoButtons();
            autoStatus.Text = Lang.T("Microsoft Defender is scanning " + files.Count + " file(s)...");
            Task.Factory.StartNew(() => files.Select(f =>
            {
                string o; int code = StartupApps.DefenderScan(f, out o);
                string line = (code == 0 ? "No threats: " : code == 2 ? "THREAT FOUND (Defender is dealing with it): " : "Could not scan: ") + f + (code != 0 && code != 2 ? " (" + o.Trim() + ")" : "");
                Log("Defender: " + line); return line;
            }).ToList(), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                autoBusy = false; UpdateAutoButtons();
                var lines = t.IsFaulted ? new List<string> { t.Exception.GetBaseException().Message } : t.Result;
                autoStatus.Text = Lang.T("Defender scan finished.");
                Lang.Box(this, string.Join("\n\n", lines), "Microsoft Defender", MessageBoxButtons.OK, lines.Any(l => l.StartsWith("THREAT")) ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                if (lines.Any(l => l.StartsWith("THREAT"))) RefreshAutostarts();
            }));
        }
    }
}
