// Tools > Security check: browser extensions, camera/microphone/location use, and the hosts file, DNS and proxy
// (SecurityCheck.cs). Elixar only reads here, except "Remove the flagged hosts lines" (asked first, old file to Undo).

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
        TabPage pageSec;
        ListView secExt, secDev, secNet; Label secStatus;
        Button btnSecCheck, btnSecExtOpen, btnSecDevOpen, btnSecHostsFix, btnSecHostsShow, btnSecProxy;
        bool secBusy, secLoaded;
        public List<BrowserExt> SecExtensions = new List<BrowserExt>();     // self-test
        public List<DeviceUseRow> SecDevices = new List<DeviceUseRow>();
        public List<HostsLine> SecHosts = new List<HostsLine>();
        public List<NetFinding> SecNetwork = new List<NetFinding>();
        public event Action SecurityChecked;                                 // self-test
        public Func<string, bool> SecConfirmForTest;

        static Color LevelColor(CheckLevel l) { return l == CheckLevel.Danger ? Color.FromArgb(199, 54, 47) : l == CheckLevel.Warn ? Color.FromArgb(190, 105, 0) : l == CheckLevel.Info ? Color.FromArgb(70, 90, 140) : Color.Empty; }

        void BuildSecurityCheck()
        {
            pageSec = new TabPage("Security check") { BackColor = pageTools.BackColor, AutoScroll = true };
            var t = Page(12);
            foreach (var r in new[] { 0, 1, 2, 3 }) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 36)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 32)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 32)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var secIntro = Note("Three tricks unwanted software plays: a browser extension that reads every page you open, a program that turns on your camera or microphone, " +
                             "and a changed hosts file, DNS server or proxy that sends you to fake sites. Elixar only looks; it tells you what to remove and where.");
            btnSecCheck = MakeButton("Check now"); btnSecCheck.Font = new Font("Segoe UI Semibold", 9f);
            var top = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) }); secStatus = StatusNote(); top.Controls.AddRange(new Control[] { btnSecCheck, secStatus });
            Func<string, Label> head = s => new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Text = s, Margin = new Padding(0, S(8), 0, S(2)) };
            Func<ListView> list = () => new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true, MultiSelect = false, HideSelection = false };

            secExt = list();
            secExt.Columns.Add("Extension", S(210)); secExt.Columns.Add("Browser", S(110)); secExt.Columns.Add("Where it came from", S(160)); secExt.Columns.Add("It can", S(250)); secExt.Columns.Add("What to do", S(360));
            btnSecExtOpen = MakeButton("Open the browser's extensions page");
            var extRow = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, 0) }); extRow.Controls.Add(btnSecExtOpen);

            secDev = list();
            secDev.Columns.Add("App", S(210)); secDev.Columns.Add("Used", S(100)); secDev.Columns.Add("Last time", S(150)); secDev.Columns.Add("Program", S(330)); secDev.Columns.Add("What to do", S(330));
            btnSecDevOpen = MakeButton("Open Windows' privacy settings");
            var devRow = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, 0) }); devRow.Controls.Add(btnSecDevOpen);

            secNet = list(); FillLast(secExt); FillLast(secDev); FillLast(secNet);
            secNet.Columns.Add("What", S(210)); secNet.Columns.Add("Setting", S(260)); secNet.Columns.Add("What it means", S(560));
            btnSecHostsFix = MakeButton("Remove the flagged hosts lines..."); btnSecHostsShow = MakeButton("Show the hosts file"); btnSecProxy = MakeButton("Open proxy settings");
            var netRow = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, 0) });
            netRow.Controls.AddRange(new Control[] { btnSecHostsFix, btnSecHostsShow, btnSecProxy });

            t.Controls.Add(Title("Security check"), 0, 0); t.Controls.Add(secIntro, 0, 1); t.Controls.Add(top, 0, 2);
            t.Controls.Add(head("Browser extensions"), 0, 3); t.Controls.Add(secExt, 0, 4); t.Controls.Add(extRow, 0, 5);
            t.Controls.Add(head("Camera, microphone and location"), 0, 6); t.Controls.Add(secDev, 0, 7); t.Controls.Add(devRow, 0, 8);
            t.Controls.Add(head("Hosts file, DNS and proxy"), 0, 9); t.Controls.Add(secNet, 0, 10); t.Controls.Add(netRow, 0, 11);
            pageSec.Controls.Add(t);
            pageSec.AutoScrollMinSize = new Size(0, S(780));

            btnSecCheck.Click += (o, e) => RunSecurityCheck();
            btnSecExtOpen.Click += (o, e) => OpenExtensionsPage();
            btnSecDevOpen.Click += (o, e) =>
            {
                var r = secDev.SelectedItems.Count > 0 ? secDev.SelectedItems[0].Tag as DeviceUseRow : null;
                if (!testMode) try { Shell.Open(DeviceUse.SettingsPage(r == null ? "Camera" : r.What)); } catch (Exception ex) { secStatus.Text = Lang.T("Couldn't open it: " + ex.Message); }
            };
            btnSecHostsFix.Click += (o, e) => FixHostsLines();
            btnSecHostsShow.Click += (o, e) => { if (!testMode) try { Shell.Select(NetCheck.HostsPath); } catch { } };
            btnSecProxy.Click += (o, e) => { if (!testMode) try { Shell.Open("ms-settings:network-proxy"); } catch { } };
            secExt.SelectedIndexChanged += (o, e) => SecButtons();
            SecButtons();
        }

        void SecButtons()
        {
            btnSecExtOpen.Enabled = secExt.SelectedItems.Count > 0 && BrowserExtensions.BrowserExe(((BrowserExt)secExt.SelectedItems[0].Tag).Browser) != null;
            btnSecHostsFix.Enabled = !secBusy && SecHosts.Any(h => h.Line > 0);
            btnSecCheck.Enabled = !secBusy;
        }

        void OnSecurityTab() { if (!secLoaded) RunSecurityCheck(); }

        public void RunSecurityCheck()
        {
            if (secBusy) return;
            secBusy = true; secLoaded = true; SecButtons();
            secStatus.Text = Lang.T("Checking browser extensions, camera and microphone use, the hosts file, DNS and proxy...");
            Task.Factory.StartNew(() =>
            {
                var ext = BrowserExtensions.Read();
                var dev = DeviceUse.Read(!testMode);
                int entries; string problem; var hosts = NetCheck.ReadHosts(out entries, out problem);
                var net = NetCheck.ReadNetwork();
                return Tuple.Create(ext, dev, Tuple.Create(hosts, entries, problem), net);
            }, TaskCreationOptions.LongRunning).ContinueWith(tk => UI(() =>
            {
                secBusy = false;
                if (tk.IsFaulted) { secStatus.Text = Lang.T("The check stopped: ") + tk.Exception.GetBaseException().Message; SecButtons(); return; }
                SecExtensions = tk.Result.Item1; SecDevices = tk.Result.Item2; SecHosts = tk.Result.Item3.Item1; SecNetwork = tk.Result.Item4;
                FillSecurity(tk.Result.Item3.Item2, tk.Result.Item3.Item3);
                SecButtons();
                if (SecurityChecked != null) SecurityChecked();
            }));
        }

        void FillSecurity(int hostsEntries, string hostsProblem)
        {
            Action<ListViewItem, CheckLevel> color = (it, l) => { var c = LevelColor(l); if (c != Color.Empty) it.ForeColor = c; };
            secExt.BeginUpdate(); secExt.Items.Clear();
            foreach (var e in SecExtensions)
            {
                var it = new ListViewItem((e.Enabled ? "" : Lang.T("(off) ")) + NameText.Plain(e.Name)) { Tag = e, ToolTipText = Lang.T(e.Why) + "\n" + e.Id + "  " + NameText.Plain(e.Version) + "\n" + e.Folder };
                it.SubItems.Add(e.Browser + (e.Profile == "Default" || e.Profile.Length == 0 ? "" : " (" + e.Profile + ")"));
                it.SubItems.Add(Lang.T(e.Source));
                it.SubItems.Add(e.Can.Count == 0 ? "-" : string.Join(", ", e.Can.Select(c => Lang.T(c))));
                it.SubItems.Add(Lang.T(e.Why));
                color(it, e.Level); secExt.Items.Add(it);
            }
            if (SecExtensions.Count == 0) secExt.Items.Add(new ListViewItem(Lang.T("No extensions found")));
            secExt.EndUpdate();

            secDev.BeginUpdate(); secDev.Items.Clear();
            foreach (var r in SecDevices)
            {
                var it = new ListViewItem(NameText.Plain(r.App)) { Tag = r, ToolTipText = NameText.Plain(r.Packaged ? r.Package : r.Path) };
                it.SubItems.Add(Lang.T(r.What));
                it.SubItems.Add(r.InUse ? Lang.T("In use now") : r.LastUsed.ToString("g"));
                it.SubItems.Add(r.Packaged ? Lang.T("Store app") : NameText.Plain(r.Path));
                it.SubItems.Add(Lang.T(r.Why));
                color(it, r.Level); secDev.Items.Add(it);
            }
            if (SecDevices.Count == 0) secDev.Items.Add(new ListViewItem(Lang.T("No app has used them")));
            secDev.EndUpdate();

            secNet.BeginUpdate(); secNet.Items.Clear();
            foreach (var h in SecHosts)
            {
                var it = new ListViewItem(Lang.T("Hosts file") + (h.Line > 0 ? ", " + Lang.T("line") + " " + h.Line : "")) { Tag = h, ToolTipText = h.Text };
                it.SubItems.Add(h.Line > 0 ? h.Address + "  " + h.Name : h.Text); it.SubItems.Add(Lang.T(h.Why));
                color(it, h.Level); secNet.Items.Add(it);
            }
            if (SecHosts.Count == 0)
            {
                var it = new ListViewItem(Lang.T("Hosts file"));
                it.SubItems.Add(hostsProblem != null ? "-" : Lang.T(hostsEntries + " entries"));
                it.SubItems.Add(Lang.T(hostsProblem ?? "Nothing sends a known site somewhere else or blocks security updates."));
                secNet.Items.Add(it);
            }
            foreach (var n in SecNetwork)
            {
                var it = new ListViewItem(Lang.T(n.Kind == "DNS" ? "DNS server" : n.What) + (n.Kind == "DNS" ? " (" + n.What + ")" : "")) { Tag = n };
                it.SubItems.Add(n.Value); it.SubItems.Add(Lang.T(n.Why));
                color(it, n.Level); secNet.Items.Add(it);
            }
            secNet.EndUpdate();

            int bad = SecExtensions.Count(e => e.Level >= CheckLevel.Warn) + SecDevices.Count(d => d.Level >= CheckLevel.Warn) + SecHosts.Count + SecNetwork.Count(n => n.Level >= CheckLevel.Warn);
            secStatus.Text = bad == 0 ? Lang.T("Nothing suspicious found. " + SecExtensions.Count + " extensions, " + SecDevices.Count + " apps that used the camera, microphone or location.")
                                      : Lang.T(bad + (bad == 1 ? " thing to look at" : " things to look at") + " (in red and orange).");
            Log("Security check: " + bad + " to look at.");
        }

        void OpenExtensionsPage()
        {
            if (secExt.SelectedItems.Count == 0) return;
            var e = (BrowserExt)secExt.SelectedItems[0].Tag;
            string exe = BrowserExtensions.BrowserExe(e.Browser);
            if (exe == null || testMode) return;
            // with your normal rights: the browser never runs as administrator
            try { using (Unelevated.Start(exe, BrowserExtensions.ExtensionsPage(e.Browser))) { } }
            catch (Exception ex) { secStatus.Text = Lang.T("Couldn't open it: " + ex.Message); }
        }

        public void FixHostsLines()
        {
            var flagged = SecHosts.Where(h => h.Line > 0).ToList();
            if (flagged.Count == 0) return;
            // the whole lines that go (a line can name more than one site), every one of them
            var lines = flagged.GroupBy(h => h.Line).Select(g => g.First().Text).ToList();
            string q = Lang.T("Remove these lines from the hosts file?") + "\n\n" + string.Join("\n", lines.Take(15).Select(t => t.Length > 90 ? t.Substring(0, 90) + "..." : t)) +
                       (lines.Count > 15 ? "\n" + Lang.T("...and " + (lines.Count - 15) + " more") : "") +
                       "\n\n" + Lang.T("The rest of the file stays. The old file goes to Undo first.");
            bool yes = SecConfirmForTest != null ? SecConfirmForTest(q) : Lang.Box(this, q, "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            if (!yes) return;
            string err;
            try { using (ChangeLog.In("Security check")) err = NetCheck.FixHosts(flagged); }
            catch (Exception ex) { err = ex.Message; }
            secStatus.Text = err == null ? Lang.T("Removed. Tools > Undo puts the old hosts file back.") : Lang.T("Not changed: " + err);
            Log("Hosts file: " + (err ?? "flagged lines removed."));
            if (err == null) RunSecurityCheck();
        }
    }
}
