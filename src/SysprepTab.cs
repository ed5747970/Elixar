// Tools > Sysprep: a checklist that gets this PC ready for Sysprep (capturing an image with Generalize).

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
        TabPage pageSysprep;
        Label spSummary;
        FlowLayoutPanel spList;
        Button spCheck, spOpen, spLog, spAnswer, spClean, spPassport, spKit;
        SysprepFacts spFacts;
        List<SysprepCheck> spChecks = new List<SysprepCheck>();
        bool spBusy, spLoaded;
        public List<SysprepCheck> SysprepChecks { get { return spChecks; } }   // self-test

        void BuildSysprep()
        {
            var t = Page(6);
            for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            spSummary = new Label { AutoSize = true, MaximumSize = new Size(S(1000), 0), Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(S(1), S(4), 0, S(8)), Text = Lang.T("Not checked yet.") };
            spCheck = MakeButton("Check this PC"); spCheck.Font = new Font("Segoe UI Semibold", 9f);
            spOpen = MakeButton("Open Sysprep...");
            spLog = MakeButton("Sysprep's log");
            spAnswer = MakeButton("Answer file...");
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(8)) };
            row.Controls.AddRange(new Control[] { spCheck, spOpen, spAnswer, spLog });
            // golden image: what to do before and after Sysprep
            spClean = MakeButton("Image cleanup..."); spPassport = MakeButton("Image passport..."); spKit = MakeButton("Capture kit...");
            var row2 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(8)) };
            row2.Controls.Add(new Label { AutoSize = true, Text = Lang.T("Golden image:"), Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(S(1), S(7), S(8), 0) });
            row2.Controls.AddRange(new Control[] { spClean, spPassport, spKit });
            spList = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            t.Controls.Add(Title("Get this PC ready for Sysprep"), 0, 0);
            t.Controls.Add(Note("Before you capture an image of this PC (Sysprep with Generalize), Elixar checks what would make Sysprep stop and offers Microsoft's fix for each. Nothing changes without asking, and a restore point is made before a fix. Sysprep itself is only opened: you choose its options there."), 0, 1);
            t.Controls.Add(spSummary, 0, 2);
            t.Controls.Add(row, 0, 3);
            t.Controls.Add(row2, 0, 4);
            t.Controls.Add(spList, 0, 5);
            pageSysprep.Controls.Add(t);
            spCheck.Click += (o, e) => SysprepRefresh();
            spAnswer.Click += (o, e) => { UnattendForm.ShowFor(this, testMode, Log); if (spLoaded) SysprepRefresh(); };
            spOpen.Click += (o, e) => SysprepOpen();
            spClean.Click += (o, e) => { ImageCleanForm.ShowFor(this, testMode, Log); if (spLoaded) SysprepRefresh(); };
            spPassport.Click += (o, e) => SysprepPassport();
            spKit.Click += (o, e) => SysprepKit();
            spLog.Click += (o, e) =>
            {
                string err = Path.Combine(Sysprep.Panther, "setuperr.log"), act = Path.Combine(Sysprep.Panther, "setupact.log");
                string f = File.Exists(err) && new FileInfo(err).Length > 0 ? err : File.Exists(act) ? act : null;
                if (f == null) { Lang.Box(this, "Sysprep hasn't written a log on this PC yet.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                try { Shell.Open(f); } catch { }
            };
            spList.Resize += (o, e) => { foreach (Control c in spList.Controls) c.Width = Math.Max(S(500), spList.ClientSize.Width - S(24)); };
        }

        void OnSysprepTab() { if (!spLoaded) { spLoaded = true; SysprepRefresh(); } }

        public void SysprepRefresh()
        {
            if (spBusy) return;
            spBusy = true; spCheck.Enabled = spOpen.Enabled = false;
            spSummary.Text = Lang.T("Checking this PC..."); spSummary.ForeColor = Theme.Ink;
            Task.Factory.StartNew(() => Sysprep.Read(s => UI(() => spSummary.Text = Lang.T(s))), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                spBusy = false; spCheck.Enabled = true; spOpen.Enabled = !testMode;
                if (t.IsFaulted) { spSummary.Text = Lang.T("The check stopped: ") + t.Exception.GetBaseException().Message; return; }
                spFacts = t.Result;
                spChecks = Sysprep.Assess(spFacts);
                SysprepFill();
            }));
        }

        void SysprepFill()
        {
            int block = spChecks.Count(c => c.Level == 3), fix = spChecks.Count(c => c.Level == 2);
            spSummary.Text = Lang.T(Sysprep.Summary(spChecks));
            spSummary.ForeColor = Theme.MapFore(block > 0 ? CriticalColor : fix > 0 ? CautionColor : SafeColor);
            spList.SuspendLayout();
            foreach (Control c in spList.Controls.Cast<Control>().ToList()) { spList.Controls.Remove(c); c.Dispose(); }
            foreach (var c in spChecks) { var card = SysprepCard(c); spList.Controls.Add(card); Theme.Apply(card); }
            spList.ResumeLayout();
        }

        Control SysprepCard(SysprepCheck c)
        {
            Color tone = c.Level == 3 ? CriticalColor : c.Level == 2 ? CautionColor : c.Level == 1 ? Theme.Accent : SafeColor;
            tone = Theme.MapFore(tone);
            string tag = c.Level == 3 ? "Stops Sysprep" : c.Level == 2 ? "Fix first" : c.Level == 1 ? "Good to know" : "Ready";
            var card = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Panel,
                                              Padding = new Padding(S(12), S(10), S(12), S(10)), Margin = new Padding(0, 0, 0, S(8)), Width = Math.Max(S(500), spList.ClientSize.Width - S(24)) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(118))); card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var pill = new Label { AutoSize = true, Text = Lang.T(tag), ForeColor = tone, Font = new Font("Segoe UI Semibold", 8.5f), Margin = new Padding(0, S(2), S(8), 0) };
            var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), BackColor = Theme.Panel };
            int tw = S(640);
            text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(tw, 0), Text = Lang.T(c.Title), Font = new Font("Segoe UI Semibold", 10f), ForeColor = Theme.Ink, Margin = new Padding(0, 0, 0, S(2)), UseMnemonic = false });
            text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(tw, 0), Text = Lang.T(c.Detail), ForeColor = Theme.Quiet, Margin = new Padding(0), UseMnemonic = false });
            if (c.Items.Count > 0)
                text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(tw, 0), Text = string.Join("\n", c.Items.Take(12).Select(i => "•  " + (c.Id == "clone" ? Lang.T(i) : i))) + (c.Items.Count > 12 ? "\n+ " + (c.Items.Count - 12) : ""),
                                              ForeColor = Theme.Ink, Margin = new Padding(0, S(4), 0, 0), UseMnemonic = false });
            card.Controls.Add(pill, 0, 0); card.Controls.Add(text, 1, 0);
            if (c.FixText.Length > 0)
            {
                var b = MakeButton(Lang.T(c.FixText)); b.Anchor = AnchorStyles.Right | AnchorStyles.Top; b.Enabled = !testMode;
                b.Click += (o, e) => SysprepFix(c);
                card.Controls.Add(b, 2, 0);
            }
            card.Paint += (o, e) => { using (var p = new Pen(Theme.Grid)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1); using (var br = new SolidBrush(tone)) e.Graphics.FillRectangle(br, Lang.Rtl ? card.Width - S(4) : 0, 0, S(4), card.Height); };
            return card;
        }

        void SysprepFix(SysprepCheck c)
        {
            if (testMode || spBusy) return;
            switch (c.Id)
            {
                case "accounts": try { Shell.Open("ms-settings:otherusers"); } catch { } return;
                case "space": tabs.SelectedTab = pageClean; return;
                case "log": spLog.PerformClick(); return;
                case "answer": spAnswer.PerformClick(); return;
                case "clone": tabs.SelectedTab = pageApps; return;
                case "entra": case "mdm": try { Shell.Open("ms-settings:workplace"); } catch { } return;
                case "sec-keys":
                    try
                    {
                        string first = c.Items.FirstOrDefault(File.Exists);
                        if (first != null) Process.Start(new ProcessStartInfo(Win.SystemExe("explorer.exe"), "/select,\"" + first + "\"") { UseShellExecute = false });
                    }
                    catch { }
                    return;
                case "sec-elixar":
                    if (Lang.Box(this, Lang.T("Sign Elixar out of email?") + "\n\n" + Lang.T("Elixar forgets the sign-in on this PC and cancels it. Sign in again on the PCs that need it (Settings > Email account)."),
                                 "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    try { MailAccount.Disconnect(); Log(Lang.T("Sysprep: Elixar signed out of email.")); } catch (Exception ex) { Log("Sysprep: " + ex.Message); }
                    SysprepRefresh(); return;
                case "storeupdates":
                {
                    bool pause = !spFacts.StoreUpdatesPaused;
                    string err = Sysprep.StoreUpdates(pause);
                    Log(Lang.T(err == null ? (pause ? "Sysprep: Store updates paused." : "Sysprep: Store updates turned back on.") : "Sysprep: " + err));
                    SysprepRefresh(); return;
                }
            }
            string ask = null; bool risky = false; Func<string> run = null;
            switch (c.Id)
            {
                case "appx":
                    var names = spFacts.Unprovisioned.Select(x => x[1]).ToList();
                    ask = Lang.T("Remove these Store apps for all accounts?") + "\n\n" + string.Join("\n", spFacts.Unprovisioned.Take(20).Select(x => "  - " + x[0])) + "\n\n" +
                          Lang.T("You can install them again from the Store after the image is made.");
                    run = () => Sysprep.RemoveApps(names);
                    break;
                case "bitlocker":
                    ask = Lang.T("Turn BitLocker off for the Windows drive?") + "\n\n" + Lang.T("The drive is decrypted in the background (it can take an hour) and the PC stays usable. Check again when it has finished.");
                    run = Sysprep.BitLockerOff;
                    break;
                case "upgrade":
                    risky = true;
                    ask = Lang.T("Apply the workaround for an upgraded Windows?") + "\n\n" + Lang.T("Microsoft supports Sysprep only on a clean install. This workaround removes the \"upgraded\" mark and resets Sysprep's state (3 registry values). It usually works; if Sysprep still stops, a clean install is the way.");
                    run = Sysprep.ClearUpgrade;
                    break;
                case "state":
                    bool rearm = Sysprep.ParseLog(spFacts.LogText).Any(x => x.Kind == "rearm");
                    ask = Lang.T("Reset Sysprep's state?") + "\n\n" + Lang.T(rearm ? "Sysprep's state is reset, the activation rearm is skipped (SkipRearm), and the old logs are kept under a new name."
                                                                                    : "Sysprep's state is reset and the old logs are kept under a new name.");
                    run = () => Sysprep.ResetState(rearm);
                    break;
                case "sec-autologon":
                    ask = Lang.T("Remove the saved automatic sign-in password?") + "\n\n" + Lang.T("Automatic sign-in stops on this PC; you sign in with your password.");
                    run = GoldenImage.RemoveAutoLogon;
                    break;
                case "sec-wifi":
                    var nets = spFacts.WifiKeys.ToList();
                    ask = Lang.T("Remove these saved Wi-Fi networks?") + "\n\n" + string.Join("\n", nets.Take(20).Select(x => "  - " + x)) + "\n\n" +
                          Lang.T("This PC forgets them and their passwords, and disconnects if it is on one of them.");
                    run = () => GoldenImage.RemoveWifi(nets);
                    break;
                case "sec-creds":
                    var creds = spFacts.Creds.ToList();
                    ask = Lang.T("Remove these saved sign-ins from Credential Manager?") + "\n\n" + string.Join("\n", creds.Take(20).Select(x => "  - " + x[0])) + (creds.Count > 20 ? "\n  + " + (creds.Count - 20) : "") + "\n\n" +
                          Lang.T("Network drives, remote desktops and apps will ask for their passwords again.");
                    run = () => GoldenImage.RemoveCredentials(creds);
                    break;
                case "sec-answer":
                    risky = true;
                    var files = spFacts.OldAnswers.ToList();
                    ask = Lang.T("Delete these old answer files?") + "\n\n" + string.Join("\n", files.Select(x => "  - " + x)) + "\n\n" +
                          Lang.T("They are deleted, not moved to Undo: they can hold passwords.");
                    run = () => GoldenImage.DeleteAnswerFiles(files);
                    break;
                case "audit":
                    risky = true;
                    ask = Lang.T("Restart into Audit mode now?") + "\n\n" + Lang.T("Save your work first: the PC restarts at once. Windows signs in as the built-in Administrator. When the image is ready, check here again and run Sysprep with OOBE and Generalize.");
                    run = GoldenImage.RestartIntoAudit;
                    break;
                case "defaults":
                    ask = Lang.T("Copy your Start menu and default apps to every new account?") + "\n\n" +
                          Lang.T("Elixar exports them with Windows' own tools and puts them in the default account. Windows' original files are kept as copies (.before-elixar).");
                    bool w11 = spFacts.Win11;
                    run = () => GoldenImage.CopyDefaults(w11, m => UI(() => Log(Lang.T(m))));
                    break;
                default: return;
            }
            if ((risky ? Lang.BoxNo(this, ask, "Elixar") != DialogResult.Yes : Lang.Box(this, ask, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)) return;
            spBusy = true; spCheck.Enabled = spOpen.Enabled = false;
            spSummary.Text = Lang.T("Creating a restore point, then fixing...");
            Task.Factory.StartNew(() => { Log(CreateRestorePoint("Before Elixar: Sysprep fix")); return run(); }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                spBusy = false;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                Log(Lang.T(err == null ? "Sysprep: fixed - " + c.Title : "Sysprep: could not fix " + c.Title + ": " + err));
                if (err != null) Lang.Box(this, Lang.T("That didn't work:") + "\n\n" + err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SysprepRefresh();
            }));
        }

        void SysprepPassport()
        {
            if (testMode || spBusy) return;
            string file;
            using (var d = new SaveFileDialog { Filter = "HTML (*.html)|*.html", Title = Lang.T("Save the image passport"),
                                                FileName = "Image passport - " + Environment.MachineName + " - " + DateTime.Now.ToString("yyyy-MM-dd") + ".html",
                                                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                file = d.FileName;
            }
            spBusy = true; spCheck.Enabled = spOpen.Enabled = spPassport.Enabled = false;
            spSummary.Text = Lang.T("Writing the image passport..."); spSummary.ForeColor = Theme.Ink;
            var checks = spChecks.ToList();
            Task.Factory.StartNew(() =>
            {
                var data = GoldenImage.ReadPassport(checks, s => UI(() => spSummary.Text = Lang.T(s)));
                SafeFile.Save(file, GoldenImage.Passport(data));
                return data;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                spBusy = false; spCheck.Enabled = spPassport.Enabled = true; spOpen.Enabled = !testMode;
                if (spLoaded) SysprepFill(); else spSummary.Text = Lang.T("Not checked yet.");
                if (t.IsFaulted) { Lang.Box(this, Lang.T("The passport could not be written: ") + t.Exception.GetBaseException().Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Log(Lang.T("Image passport saved: " + file));
                try { Shell.Open(file); } catch { }
            }));
        }

        void SysprepKit()
        {
            if (testMode) return;
            string folder;
            using (var d = new FolderBrowserDialog { Description = Lang.T("Choose the USB drive (or a folder on it) for the capture kit. Use an NTFS or exFAT drive with room for the image."), ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                folder = d.SelectedPath;
            }
            string err = GoldenImage.WriteKit(folder);
            if (err != null) { Lang.Box(this, Lang.T("The capture kit could not be written: ") + err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            Log(Lang.T("Capture kit saved: " + folder));
            Lang.Box(this, Lang.T("The capture kit is ready:") + "\n\n  " + folder + "\n\n" +
                     Lang.T("After Sysprep shuts the PC down, start it from Windows PE or a Windows installation USB (Shift+F10 opens a command prompt) and run Capture-Image.cmd from this drive. README.txt has the steps."),
                     "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Shell.Open(folder); } catch { }
        }

        void SysprepOpen()
        {
            if (testMode) return;
            int block = spChecks.Count(c => c.Level == 3);
            string how = Lang.T("In Sysprep choose: Enter System Out-of-Box Experience (OOBE), tick Generalize, and Shutdown. When the PC has shut down, capture the image.");
            if (!spLoaded || spFacts == null)
            { if (Lang.Box(this, Lang.T("Open Sysprep without checking this PC first?") + "\n\n" + how, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return; }
            else if (block > 0)
            { if (Lang.BoxNo(this, Lang.T(block == 1 ? "1 thing still stops Sysprep." : block + " things still stop Sysprep.") + "\n\n" + Lang.T("Open Sysprep anyway?"), "Elixar") != DialogResult.Yes) return; }
            else if (Lang.Box(this, Lang.T("Open Sysprep?") + "\n\n" + how, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                string exe = Sysprep.Exe;
                if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess) exe = exe.Replace(@"\System32\", @"\Sysnative\");
                if (!File.Exists(exe)) { Lang.Box(this, "Sysprep wasn't found on this PC.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                Log(Lang.T("Sysprep opened."));
            }
            catch (Exception e) { Lang.Box(this, Lang.T("Sysprep could not be opened: ") + e.Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
