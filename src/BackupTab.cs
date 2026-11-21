// Tools > Backup: the page for Backup.cs.

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
    public partial class MainForm
    {
        TabPage pageBackup;
        TextBox bakDest; Label bakStatus, bakLast; Button btnBakChoose, btnBakNow, btnBakStop, btnBakOpen;
        RadioButton bakDay, bakWeek, bakOff; FlowLayoutPanel bakFolders; CancellationTokenSource bakCts;
        public readonly Dictionary<string, CheckBox> BackupBoxes = new Dictionary<string, CheckBox>();   // self-test
        public BackupResult LastBackup;                                                                    // self-test
        public event Action BackupDone;                                                                    // self-test
        public void BackupDestForTest(string d) { bakDest.Text = d; Backup.Dest = d; }
        public RadioButton BackupDayForTest { get { return bakDay; } }

        void BuildBackup()
        {
            pageBackup = new TabPage("Backup") { BackColor = pageTools.BackColor, AutoScroll = true };
            var t = Page(10);
            for (int i = 0; i < 10; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var note = Note("A plain copy of your folders on another drive, a USB disk or a network folder, kept up to date by itself. Only new and changed files are copied. " +
                            "A file that changed or was deleted here is kept in the backup for 30 days, so a mistake or ransomware doesn't take the backup with it. " +
                            "Your files open straight from the backup: no special program is needed to get them back.");
            Func<string, Label> head = s => new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Text = s, Margin = new Padding(0, S(8), 0, S(2)) };

            var destRow = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) });
            bakDest = new TextBox { ReadOnly = true, Width = S(420), Margin = new Padding(0, S(4), S(6), 0), Text = Backup.Dest };
            btnBakChoose = MakeButton("Choose a drive or folder...");
            destRow.Controls.AddRange(new Control[] { bakDest, btnBakChoose });

            bakFolders = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) });
            var chosen = Backup.Chosen;
            foreach (var f in Backup.Folders())
            {
                var cb = new CheckBox { AutoSize = true, Text = f[1], Checked = chosen.Contains(f[0]), Tag = f[0], Margin = new Padding(0, S(4), S(18), 0) };
                string why = Backup.SourceProblem(f[2]);
                if (why != null) { cb.Enabled = false; cb.Checked = false; }
                new ToolTip().SetToolTip(cb, f[2] + (why != null ? "  (" + Lang.T(why) + ")" : ""));
                cb.CheckedChanged += (o, e) => { Backup.Chosen = BackupBoxes.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToList(); };
                BackupBoxes[f[0]] = cb; bakFolders.Controls.Add(cb);
            }

            var everyRow = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) });
            bakDay = new RadioButton { AutoSize = true, Text = "Every day", Margin = new Padding(0, S(4), S(18), 0) };
            bakWeek = new RadioButton { AutoSize = true, Text = "Every week (Sunday)", Margin = new Padding(0, S(4), S(18), 0) };
            bakOff = new RadioButton { AutoSize = true, Text = "Only when I click Back up now", Margin = new Padding(0, S(4), S(18), 0) };
            (Backup.Every == "day" ? bakDay : Backup.Every == "week" ? bakWeek : bakOff).Checked = true;
            everyRow.Controls.AddRange(new Control[] { bakDay, bakWeek, bakOff });

            var actRow = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(8), 0, 0) });
            btnBakNow = MakeButton("Back up now"); btnBakNow.Font = new Font("Segoe UI Semibold", 9f);
            btnBakStop = MakeButton("Stop"); btnBakStop.Enabled = false;
            btnBakOpen = MakeButton("Open the backup");
            actRow.Controls.AddRange(new Control[] { btnBakNow, btnBakStop, btnBakOpen });
            bakStatus = Note(""); bakLast = Note("");

            t.Controls.Add(Title("Backup"), 0, 0); t.Controls.Add(note, 0, 1);
            t.Controls.Add(head("Back up to"), 0, 2); t.Controls.Add(destRow, 0, 3);
            t.Controls.Add(head("Folders"), 0, 4); t.Controls.Add(bakFolders, 0, 5);
            t.Controls.Add(head("When"), 0, 6); t.Controls.Add(everyRow, 0, 7); t.Controls.Add(actRow, 0, 8);
            var stat = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            stat.Controls.Add(bakLast); stat.Controls.Add(bakStatus);
            t.Controls.Add(stat, 0, 9);
            pageBackup.Controls.Add(t);
            pageBackup.AutoScrollMinSize = new Size(0, S(480));

            btnBakChoose.Click += (o, e) => ChooseBackupDest();
            EventHandler every = (o, e) => { if (((RadioButton)o).Checked) SaveBackupEvery(); };
            bakDay.CheckedChanged += every; bakWeek.CheckedChanged += every; bakOff.CheckedChanged += every;
            btnBakNow.Click += (o, e) => RunBackupNow();
            btnBakStop.Click += (o, e) => { if (bakCts != null) bakCts.Cancel(); };
            btnBakOpen.Click += (o, e) => { string d = Backup.Dest; if (!testMode && d.Length > 0) try { string r = Backup.Root(d), f = Directory.Exists(r) ? r : d; if (Directory.Exists(f)) Shell.Open(f.TrimEnd('\\') + "\\"); } catch { } };   // only a folder (the setting could name a program)
            ShowBackupLast();
        }

        void ShowBackupLast()
        {
            var last = Backup.Last;
            bakLast.Text = last.HasValue ? Lang.T("Last backup: " + last.Value.ToString("f")) + "  " + Lang.T(Backup.LastSummary) : Lang.T("No backup yet.");
            btnBakOpen.Enabled = Backup.Dest.Length > 0;
        }

        void ChooseBackupDest()
        {
            using (var d = new FolderBrowserDialog { Description = Lang.T("Choose another drive, a USB disk or a network folder for the backup."), ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                SetBackupDest(d.SelectedPath);
            }
        }

        public string SetBackupDest(string path)
        {
            string why = Backup.DestProblem(path, Backup.Folders().Select(f => f[2]));
            if (why != null) { bakStatus.Text = Lang.T(why); return why; }
            Backup.Dest = path; bakDest.Text = path; bakStatus.Text = Lang.T("The backup goes to " + Backup.Root(path) + ".");
            if (Backup.Every != "off" && !testMode) { string e = Backup.Schedule(); if (e != null) bakStatus.Text = Lang.T(e); }
            ShowBackupLast();
            return null;
        }

        void SaveBackupEvery()
        {
            Backup.Every = bakDay.Checked ? "day" : bakWeek.Checked ? "week" : "off";
            if (testMode) return;
            if (Backup.Every != "off" && Backup.Dest.Length == 0) { bakStatus.Text = Lang.T("Choose where the backup goes first."); return; }
            string e = null;
            try { e = Backup.Schedule(); } catch (Exception ex) { e = ex.Message; }
            bakStatus.Text = e != null ? Lang.T(e) : Lang.T(Backup.Every == "off" ? "No automatic backup." : Backup.Every == "day" ? "Backs up every day at 13:17 (or when the PC is next on)." : "Backs up every Sunday at 13:17 (or when the PC is next on).");
            Log("Backup: " + (e ?? "schedule " + Backup.Every));
        }

        public void RunBackupNow()
        {
            if (bakCts != null || (!testMode && Policy.Blocked(this, "backup"))) return;
            string dest = Backup.Dest;
            var keys = Backup.Chosen;
            bakCts = new CancellationTokenSource(); var ct = bakCts.Token;
            btnBakNow.Enabled = false; btnBakStop.Enabled = true; bakStatus.Text = Lang.T("Starting the backup...");
            Task.Factory.StartNew(() => Backup.Run(dest, keys, DateTime.Now, s => UI(() => bakStatus.Text = Lang.T(s)), ct), TaskCreationOptions.LongRunning).ContinueWith(tk => UI(() =>
            {
                bakCts = null; btnBakNow.Enabled = true; btnBakStop.Enabled = false;
                if (tk.IsFaulted) { bakStatus.Text = Lang.T("The backup stopped: ") + tk.Exception.GetBaseException().Message; return; }
                LastBackup = tk.Result;
                bakStatus.Text = Lang.T(tk.Result.Summary()) + (tk.Result.Errors.Count > 0 ? "  " + Lang.T("Couldn't copy " + tk.Result.Errors.Count + " (see Logs).") : "");
                foreach (var er in tk.Result.Errors.Take(20)) Log("Backup: " + er);
                Log("Backup: " + tk.Result.Summary());
                if (tk.Result.Problem == null) Sounds.Play(Snd.Done);
                ShowBackupLast();
                if (BackupDone != null) BackupDone();
            }));
        }
    }
}
