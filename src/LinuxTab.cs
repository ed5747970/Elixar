// The Linux tab (only on Linux): this PC, Docker, the size of the system journal, and the Snap apps.
// Every change here runs fixed commands only; the system ones ask for your password (pkexec) each time.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pageLinux;
        Label lxInfo, lxTools, lxDocker, lxJournal, lxSnapWarn, lxSnapTotal;
        Button btnDockerSafe, btnDockerAll, btnJournalCap, btnJournalUncap, btnSnapOld;
        RadioButton rbCap200, rbCap500, rbCap1G;
        ListView lxSnaps;
        bool lxLoading;
        public bool LinuxTabLoaded;   // for the self-test
        public string LinuxTabState { get { return lxDocker == null ? "" : lxInfo.Text + " | " + lxDocker.Text + " | " + lxJournal.Text + " | snaps " + lxSnaps.Items.Count; } }

        string ChosenCap { get { return rbCap200.Checked ? "200M" : rbCap1G.Checked ? "1G" : "500M"; } }

        void BuildLinuxTab()
        {
            pageLinux = new TabPage("Linux") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(20);
            for (int i = 0; i < 19; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.AutoScroll = true;
            Func<string, Label> head = text => new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Text = text, Margin = new Padding(0, S(12), 0, S(4)) };
            Func<Control[], FlowLayoutPanel> row = cs => { var f = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) }; f.Controls.AddRange(cs); return f; };
            Func<string, Button> button = text =>
            {
                var b = MakeButton(text);
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.White; b.FlatAppearance.BorderColor = Color.FromArgb(214, 219, 228);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 241, 255);
                b.Margin = new Padding(S(2), S(4), S(8), S(4));
                return b;
            };
            Func<string, RadioButton> radio = text => new RadioButton { Text = text, AutoSize = true, Margin = new Padding(S(4), S(4), S(18), S(4)) };

            lxInfo = Note(""); lxTools = Note("");
            lxDocker = Note("Reading Docker...");
            btnDockerSafe = button("Clean the build cache");
            btnDockerAll = button("Remove unused images and stopped containers...");
            lxJournal = Note("Reading the journal...");
            rbCap200 = radio("200 MB"); rbCap500 = radio("500 MB (recommended)"); rbCap1G = radio("1 GB"); rbCap500.Checked = true;
            btnJournalCap = button("Keep the logs at this size");
            btnJournalUncap = button("Remove the limit");
            lxSnapWarn = new Label { AutoSize = true, Visible = false, ForeColor = Color.FromArgb(176, 96, 0), Font = new Font("Segoe UI Semibold", 9.5f), MaximumSize = new Size(S(1000), 0), Margin = new Padding(S(1), 0, 0, S(6)) };
            lxSnapTotal = Note("");
            lxSnaps = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, Height = S(190), Width = S(560), Margin = new Padding(S(2), 0, 0, S(4)) };
            lxSnaps.Columns.Add("App", S(260)); lxSnaps.Columns.Add("Versions kept", S(130)); lxSnaps.Columns.Add("Size", S(130), HorizontalAlignment.Right);
            btnSnapOld = button("Remove old versions");
            var btnRefresh = button("Refresh");

            t.Controls.Add(Title("Linux"), 0, 0);
            t.Controls.Add(Note("Extra care for Linux. Changes here run fixed commands only, and the system ones ask for your password each time."), 0, 1);
            t.Controls.Add(head("This PC"), 0, 2); t.Controls.Add(lxInfo, 0, 3); t.Controls.Add(lxTools, 0, 4);
            t.Controls.Add(head("Docker"), 0, 5); t.Controls.Add(lxDocker, 0, 6); t.Controls.Add(row(new Control[] { btnDockerSafe, btnDockerAll }), 0, 7);
            t.Controls.Add(Note("Volumes, where your databases and app data live, are never touched."), 0, 8);
            t.Controls.Add(head("Keep the system logs small"), 0, 9); t.Controls.Add(lxJournal, 0, 10);
            t.Controls.Add(row(new Control[] { rbCap200, rbCap500, rbCap1G }), 0, 11);
            t.Controls.Add(row(new Control[] { btnJournalCap, btnJournalUncap }), 0, 12);
            t.Controls.Add(Note("A permanent limit: the system itself deletes the oldest logs, so they never grow past it. 500 MB keeps weeks of history for troubleshooting."), 0, 13);
            t.Controls.Add(head("Snap apps"), 0, 14); t.Controls.Add(lxSnapWarn, 0, 15); t.Controls.Add(lxSnapTotal, 0, 16);
            t.Controls.Add(lxSnaps, 0, 17); t.Controls.Add(row(new Control[] { btnSnapOld, btnRefresh }), 0, 18);
            pageLinux.Controls.Add(t);

            btnDockerSafe.Click += (o, e) => LinuxAction("Docker", () => { string x; int c = LxSystem.DockerClean(false, out x); return c == 0 ? null : x; });
            btnDockerAll.Click += (o, e) =>
            {
                if (Lang.BoxNo(this, "Remove every image no container uses, and every stopped container?\n\nYou download images again when you need them. Volumes (databases, app data) are not touched.", "Docker") != DialogResult.Yes) return;
                LinuxAction("Docker", () => { string x; int c = LxSystem.DockerClean(true, out x); return c == 0 ? null : x; });
            };
            btnJournalCap.Click += (o, e) => { string size = ChosenCap; LinuxAction("System logs", () => { string x; return Lx.RunAsRoot(LxSystem.CapScript(size), out x) == 0 ? null : x; }); };
            btnJournalUncap.Click += (o, e) => LinuxAction("System logs", () => { string x; return Lx.RunAsRoot(LxSystem.UncapScript, out x) == 0 ? null : x; });
            btnSnapOld.Click += (o, e) => LinuxAction("Snap", () =>
            {
                long b; string script = LxSystem.OldSnapScript(out b), x;
                if (script == null) return "There are no old versions to remove.";
                return Lx.RunAsRoot(script, out x) == 0 ? null : x;
            });
            btnRefresh.Click += (o, e) => OnLinuxTab();
        }

        // runs one change in the background, then reads everything again
        void LinuxAction(string what, Func<string> work)
        {
            if (testMode) { Lang.Box(this, "Test mode: nothing was changed.", what); return; }
            foreach (var b in new[] { btnDockerSafe, btnDockerAll, btnJournalCap, btnJournalUncap, btnSnapOld }) b.Enabled = false;
            Log(what + ": working...");
            Task.Factory.StartNew(() => { try { return work(); } catch (Exception ex) { return ex.Message; } }, TaskCreationOptions.LongRunning).ContinueWith(r => UI(() =>
            {
                string err = r.Result;
                if (err == null) Log(what + ": done.");
                else { Log(what + ": " + err); if (!err.Contains("dismissed") && !err.Contains("Not authorized")) Lang.Box(this, Lang.T("It did not finish:") + "\n\n" + err, what); }
                OnLinuxTab();
            }));
        }

        public static Func<LxRead> LinuxReadOverride;   // previews and tests
        public class LxRead { public LxSystem.DockerUse Docker; public long Journal = -1; public string Cap, Kernel = ""; public List<LxSystem.SnapApp> Snaps; public List<string> Tools = new List<string>(); public long Total, Free; public bool HasSnap, HasJournal; }

        void OnLinuxTab()
        {
            if (pageLinux == null || lxLoading) return;
            lxLoading = true;
            var os = OsInfo.Current;
            Task.Factory.StartNew(() =>
            {
                if (LinuxReadOverride != null) return LinuxReadOverride();
                var r = new LxRead { Total = Sys.Total(), Free = Sys.Free(), Kernel = LxSystem.Kernel, HasSnap = Lx.Tool("snap") != null, HasJournal = Lx.Tool("journalctl") != null };
                r.Tools = new[] { "apt-get", "snap", "flatpak", "journalctl", "pkexec", "notify-send" }.Where(x => Lx.Tool(x) != null).ToList();
                if (LxSystem.Docker != null) r.Tools.Add("docker");
                try { r.Docker = LxSystem.ReadDocker(); } catch { r.Docker = new LxSystem.DockerUse(); }
                try { string o; if (Lx.Run("journalctl", "--disk-usage", 20000, out o) == 0) r.Journal = Lx.ParseSize(o); } catch { }
                try { r.Cap = LxSystem.CurrentCap(); } catch { }
                try { r.Snaps = LxSystem.Snaps(LxSystem.SnapDir); } catch { r.Snaps = new List<LxSystem.SnapApp>(); }
                return r;
            }, TaskCreationOptions.LongRunning).ContinueWith(task => UI(() =>
            {
                lxLoading = false;
                var r = task.Result;
                string k = r.Kernel;
                lxInfo.Text = os.Name + (k != "" ? "   ·   " + Lang.T("kernel") + " " + k : "") + "   ·   " +
                              Lang.T("System disk: " + Fs.Fmt(r.Free) + " free of " + Fs.Fmt(r.Total));
                var found = r.Tools;
                lxTools.Text = Lang.T("Tools found:") + " " + (found.Count == 0 ? "-" : string.Join(", ", found));

                var d = r.Docker;
                bool dockerOk = d.Installed && (d.Reachable || d.NeedsRoot);
                lxDocker.Text = Lang.T(!d.Installed ? "Docker is not installed." :
                                d.Reachable ? "Images " + Fs.Fmt(d.Images) + " (" + Fs.Fmt(d.ImagesFree) + " unused)   ·   containers " + Fs.Fmt(d.Containers) + "   ·   build cache " + Fs.Fmt(d.Cache) + "   ·   volumes " + Fs.Fmt(d.Volumes) + " (kept)" :
                                d.NeedsRoot ? "You are not in the docker group, so Elixar can't read the sizes. Cleaning asks for your password." :
                                "Docker is installed but not running.");
                btnDockerSafe.Enabled = btnDockerAll.Enabled = dockerOk;

                bool jd = r.HasJournal;
                lxJournal.Text = Lang.T(!jd ? "This system has no systemd journal." :
                                 (r.Journal >= 0 ? "The journal uses " + Fs.Fmt(r.Journal) + ". " : "") +
                                 (r.Cap == null ? "No limit is set (systemd's default: up to a tenth of the disk, at most 4 GB)." : "The limit is " + LxSystem.CapText(r.Cap) + "."));
                if (r.Cap == "200M") rbCap200.Checked = true; else if (r.Cap == "1G") rbCap1G.Checked = true; else rbCap500.Checked = true;
                btnJournalCap.Enabled = jd;
                btnJournalUncap.Enabled = jd && File.Exists(LxSystem.CapFile);

                lxSnaps.BeginUpdate(); lxSnaps.Items.Clear();
                foreach (var a in r.Snaps)
                {
                    var it = new ListViewItem(a.Name); it.SubItems.Add(a.Versions.ToString()); it.SubItems.Add(Fs.Fmt(a.Bytes));
                    lxSnaps.Items.Add(it);
                }
                lxSnaps.EndUpdate();
                long snapBytes = r.Snaps.Sum(a => a.Bytes);
                bool snap = r.HasSnap;
                lxSnapTotal.Text = Lang.T(!snap ? "Snap is not installed." : r.Snaps.Count == 0 ? "No Snap apps found." : r.Snaps.Count + " Snap apps take " + Fs.Fmt(snapBytes) + ".");
                lxSnapWarn.Visible = LxSystem.SnapsLarge(snapBytes, r.Total);
                lxSnapWarn.Text = Lang.T("Snap apps take " + Fs.Fmt(snapBytes) + ", more than usual. Remove old versions, or apps you don't use (\"snap remove <app>\").");
                btnSnapOld.Enabled = snap && r.Snaps.Any(a => a.Versions > 1);
                LinuxTabLoaded = true;
            }));
        }
    }
}
