// Dashboard > Check & fix: one button for the everyday care of a PC. It checks, then shows what it found; only the safe
// fixes are ticked and nothing is done until you click Fix. Every fix is one Elixar already has (the weekly clean's Safe
// items, the Startup switch, winget updates, the Recycle Bin), so each one is logged and can be undone the same way.

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
    public enum FixKind { Clean, Startup, Updates, RecycleBin, Info }

    public class FixItem
    {
        public FixKind Kind; public string Title = "", Detail = "", Tag = "", Right = "";
        public long Bytes = -1; public bool On, Locked, Warning;
        public List<CleanItem> Cleans = new List<CleanItem>();
        public List<StartupEntry> Startups = new List<StartupEntry>();
        public List<AppUpdate> Updates = new List<AppUpdate>();
    }

    public class CheckReport
    {
        public List<FixItem> Items = new List<FixItem>();
        public long CanFree; public int SlowStartup = -1, Updates = -1; public string Disk = "", DiskLevel = "";
        public TimeSpan Took;
    }

    public class FixResult { public long Freed; public int StartupOff, Updated, UpdateFailed; public string RestorePoint = ""; public List<string> Lines = new List<string>(); }

    public static class CheckFix
    {
        // self-test hooks
        public static Func<List<CleanItem>> CleansOverride;
        public static Func<List<StartupEntry>> StartupOverride;
        public static Func<StartupImpactData> ImpactOverride;
        public static Func<List<AppUpdate>> UpdatesOverride;
        public static Func<string> DiskOverride;          // "Healthy" / "Warning" / "Failing"
        public static Func<bool> RestartOverride;
        public static Func<string, bool> SetStartupOverride;   // name -> ok
        public static Func<string> RestorePointOverride;

        // startup programs you can start yourself when you need them: game launchers, chat, music, updaters.
        // Never security software, drivers' helpers (audio, touchpad, graphics) or anything that syncs your files.
        static readonly string[][] Optional =
        {
            new[] { "steam.exe", "Steam" }, new[] { "epicgameslauncher.exe", "Epic Games" }, new[] { "eadesktop.exe", "EA app" }, new[] { "origin.exe", "Origin" },
            new[] { "battle.net.exe", "Battle.net" }, new[] { "upc.exe", "Ubisoft Connect" }, new[] { "ubisoftconnect.exe", "Ubisoft Connect" }, new[] { "galaxyclient.exe", "GOG Galaxy" },
            new[] { "discord", "Discord" }, new[] { "spotify.exe", "Spotify" }, new[] { "skype.exe", "Skype" }, new[] { "zoom.exe", "Zoom" }, new[] { "slack.exe", "Slack" },
            new[] { "whatsapp", "WhatsApp" }, new[] { "telegram.exe", "Telegram" }, new[] { "ms-teams.exe", "Teams" }, new[] { "teams.exe", "Teams" },
            new[] { "ccxprocess.exe", "Adobe Creative Cloud" }, new[] { "creative cloud.exe", "Adobe Creative Cloud" }, new[] { "adobecollabsync.exe", "Adobe Acrobat sync" },
            new[] { "acrotray.exe", "Adobe Acrobat" }, new[] { "ituneshelper.exe", "iTunes helper" }, new[] { "utorrent.exe", "uTorrent" }, new[] { "qbittorrent.exe", "qBittorrent" },
            new[] { "opera_autoupdate", "Opera updater" }, new[] { "browser_assistant.exe", "Opera browser assistant" }, new[] { "cortana", "Cortana" },
        };

        public static string OptionalName(StartupEntry e)
        {
            string f = Path.GetFileName((e.File ?? "").Replace('/', '\\').Split('\\').Last()).ToLowerInvariant(), cmd = (e.Command ?? "").ToLowerInvariant();
            foreach (var o in Optional)
                if (o[0].EndsWith(".exe") ? f == o[0] : (f.Contains(o[0]) || cmd.Contains(o[0]))) return o[1];
            return null;
        }

        public static bool RestartPendingNow() { return RestartPending(); }

        static bool RestartPending()
        {
            if (RestartOverride != null) return RestartOverride();
            if (!Win.IsWindows) return false;
            try
            {
                using (var b = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64))
                using (var a = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                using (var c = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                    return a != null || c != null;
            }
            catch { return false; }
        }

        public static CheckReport Scan(Action<string> status)
        {
            var started = DateTime.Now;
            var r = new CheckReport();
            Action<string> say = s => { if (status != null) status(s); };

            // 1. the Safe items (the same ones the weekly clean does)
            say("Measuring what can be cleaned safely...");
            var cleans = (CleansOverride != null ? CleansOverride() : Automation.AutoItems()).Where(i => i.Tier == Tier.Safe).ToList();
            var measured = new List<CleanItem>();
            foreach (var it in cleans) { long b = -1; try { b = it.Measure != null ? it.Measure() : -1; } catch { } it.Size = b; if (b > 0) measured.Add(it); }
            long safe = measured.Sum(i => i.Size);
            if (safe >= (50L << 20))
            {
                var top = measured.OrderByDescending(i => i.Size).Take(3).Select(i => i.Name + " " + Fs.Fmt(i.Size));
                r.Items.Add(new FixItem { Kind = FixKind.Clean, Title = "Clean the Safe items", Tag = "Safe", On = true, Bytes = safe, Right = Fs.Fmt(safe), Cleans = measured,
                                          Detail = string.Join(", ", top) + (measured.Count > 3 ? " and " + (measured.Count - 3) + " more" : "") + ". Open apps are skipped, never closed." });
            }
            r.CanFree = safe;

            // 2. startup programs you can start yourself
            say("Reading the programs that start with Windows...");
            var startup = new List<StartupEntry>();
            try { startup = StartupOverride != null ? StartupOverride() : StartupApps.Read(false); } catch { }
            StartupImpactData impact = null;
            try { impact = ImpactOverride != null ? ImpactOverride() : (Win.IsWindows ? StartupImpact.Read() : null); } catch { }
            var optional = startup.Where(e => e.Enabled && !e.Missing && e.Flag != StartupFlag.Danger && OptionalName(e) != null).ToList();
            if (!Policy.Allowed("startup.change")) optional.Clear();   // the company's policy decides (Policy manager)
            r.SlowStartup = optional.Count;
            if (optional.Count > 0)
            {
                long ms = optional.Sum(e => { var a = impact == null ? null : impact.For(e.File); return a == null ? 0 : a.AvgMs; });
                r.Items.Add(new FixItem { Kind = FixKind.Startup, Tag = "Safe", On = true, Startups = optional,
                                          Title = "Turn off " + optional.Count + " startup program" + (optional.Count == 1 ? "" : "s") + " you can open yourself when you need " + (optional.Count == 1 ? "it" : "them"),
                                          Right = ms >= 1000 ? "about " + (ms / 1000) + " s faster start" : "",
                                          Detail = string.Join(", ", optional.Select(OptionalName).Distinct()) + " - turn any back on in Tools > Startup." });
            }

            // 3. app updates from winget / the Microsoft Store
            say("Checking for app updates...");
            var ups = new List<AppUpdate>();
            try { if (UpdatesOverride != null) ups = UpdatesOverride(); else if (Win.IsWindows && WinGet.Available) { string e; ups = WinGet.List(out e); } } catch { }
            ups = ups.Where(u => WinGet.TrustedSource(u.Source)).ToList();
            r.Updates = ups.Count;
            if (!Policy.Allowed("apps.update")) ups.Clear();   // counted, but not offered: the company's policy decides
            if (ups.Count > 0)
                r.Items.Add(new FixItem { Kind = FixKind.Updates, Tag = "Safe", On = true, Updates = ups, Right = "",
                                          Title = "Update " + ups.Count + " app" + (ups.Count == 1 ? "" : "s"),
                                          Detail = string.Join(", ", ups.Take(6).Select(u => u.Name)) + (ups.Count > 6 ? " and " + (ups.Count - 6) + " more" : "") + " (from winget / Microsoft Store). An app that is open may be closed to update." });

            // 4. the Recycle Bin: your decision, never ticked
            var bin = (CleansOverride != null ? CleansOverride() : Catalog.Build()).FirstOrDefault(i => i.Name == "Recycle Bin");
            if (bin != null)
            {
                long b = -1; try { b = bin.Measure(); } catch { }
                if (b >= (100L << 20))
                    r.Items.Add(new FixItem { Kind = FixKind.RecycleBin, Tag = "Your decision", On = false, Bytes = b, Right = Fs.Fmt(b), Cleans = new List<CleanItem> { bin },
                                              Title = "Empty the Recycle Bin", Detail = "Files you deleted. Look inside first if you're not sure." });
            }

            // 5. how Windows and the disk are doing: information only
            say("Checking the disk and Windows...");
            string disk = "";
            try { disk = DiskOverride != null ? DiskOverride() : (Win.IsWindows ? DiskHealth.Read().Worst.ToString() : ""); } catch { }
            r.DiskLevel = disk;
            r.Disk = disk == "Failing" ? "Failing" : disk == "Warning" ? "Worn" : disk == "Healthy" ? "Good" : "Unknown";
            bool restart = RestartPending();
            if (disk == "Failing")
                r.Items.Add(new FixItem { Kind = FixKind.Info, Locked = true, Warning = true, Tag = "Important", Title = "The disk is failing", Detail = "Back up your files now and replace the disk (Tools > Disk health)." });
            else if (disk == "Warning")
                r.Items.Add(new FixItem { Kind = FixKind.Info, Locked = true, Warning = true, Tag = "Info", Title = "The disk is worn", Detail = "Keep a backup and plan to replace it (Tools > Disk health)." });
            r.Items.Add(new FixItem { Kind = FixKind.Info, Locked = true, Tag = "Info",
                                      Title = restart ? "Windows is waiting for a restart" : "Windows looks healthy",
                                      Detail = restart ? "Restart to finish updates; it also frees what they leave behind." : "No restart pending. If Windows misbehaves: Tools > Repair Windows." });
            r.Took = DateTime.Now - started;
            return r;
        }

        public static string RestorePoint()
        {
            if (RestorePointOverride != null) return RestorePointOverride();
            if (!Win.IsWindows) return "";
            try
            {
                return Sys.RunPowerShell("try { Checkpoint-Computer -Description 'Before Elixar Check and fix' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue; " +
                    "if ($w) { 'Restore point not created: ' + ($w -join ' ') } else { 'Restore point created.' } } catch { 'Restore point not created: ' + $_.Exception.Message }", 5 * 60 * 1000).Trim();
            }
            catch (Exception ex) { return "Restore point not created: " + ex.Message; }
        }

        public static FixResult Fix(IEnumerable<FixItem> chosen, Action<string> log)
        {
            var res = new FixResult();
            // checked again here, whoever asks: a fix the policy blocks is never done
            var sel = chosen.Where(i => i.On && !i.Locked && (i.Kind != FixKind.Startup || Policy.Allowed("startup.change")) && (i.Kind != FixKind.Updates || Policy.Allowed("apps.update"))).ToList();
            Action<string> say = s => { res.Lines.Add(s); if (log != null) log(s); };
            if (sel.Count == 0) return res;
            using (ChangeLog.In("Check & fix"))
            {
                if (sel.Any(i => i.Kind == FixKind.Startup || i.Kind == FixKind.Updates))
                {
                    say("Creating a restore point first...");
                    res.RestorePoint = RestorePoint(); if (res.RestorePoint.Length > 0) say("  " + res.RestorePoint);
                }
                foreach (var it in sel.Where(i => i.Kind == FixKind.Clean || i.Kind == FixKind.RecycleBin))
                    foreach (var c in it.Cleans)
                    {
                        if (c.Procs.Length > 0 && Sys.FindProcesses(c.Procs).Count > 0) { say("Skipped " + c.Name + " (the app is open)."); continue; }
                        long before = Sys.Free(); string problem = null;
                        try { c.Clean(m => { }); } catch (Exception ex) { problem = ex.Message; }
                        long got = Math.Max(0, Sys.Free() - before);
                        if (CleansOverride != null && got == 0 && problem == null) got = Math.Max(0, c.Size);   // the self-test's items free nothing real
                        ChangeLog.Cleaned(c, got, problem, "Check & fix");
                        res.Freed += got;
                        say(problem == null ? "Cleaned " + c.Name + ": " + Fs.Fmt(got) : "Problem with " + c.Name + ": " + problem);
                    }
                foreach (var it in sel.Where(i => i.Kind == FixKind.Startup))
                    foreach (var e in it.Startups)
                    {
                        bool ok = SetStartupOverride != null ? SetStartupOverride(e.Name) : StartupApps.SetEnabled(e, false);
                        if (ok) res.StartupOff++;
                        say((ok ? "Turned off at startup: " : "Couldn't turn off: ") + e.Name);
                    }
                foreach (var it in sel.Where(i => i.Kind == FixKind.Updates))
                    foreach (var u in it.Updates)
                    {
                        say("Updating " + u.Name + "...");
                        if (WinGet.Upgrade(u, s => say(s.Trim()))) res.Updated++; else res.UpdateFailed++;
                    }
            }
            string sum = "Check & fix: " + string.Join(", ", new[] {
                res.Freed > 0 ? "freed " + Fs.Fmt(res.Freed) : null,
                res.StartupOff > 0 ? res.StartupOff + " startup program" + (res.StartupOff == 1 ? "" : "s") + " off" : null,
                res.Updated > 0 ? res.Updated + " app" + (res.Updated == 1 ? "" : "s") + " updated" : null }.Where(x => x != null));
            Activity.Add("clean", sum, res.Freed);
            return res;
        }
    }

    // ============================ the window ============================
    public class CheckFixForm : Form
    {
        readonly Func<int, int> S;
        readonly Label head, sub, status; readonly TableLayoutPanel cards, rows; readonly Button btnFix, btnClose;
        readonly Dictionary<string, Label> cardValue = new Dictionary<string, Label>();
        CheckReport report; readonly List<CheckBox> boxes = new List<CheckBox>();
        public event Action Scanned, Fixed;
        public CheckReport Report { get { return report; } }
        public FixResult LastFix;
        public int RowCount { get { return report == null ? 0 : report.Items.Count; } }
        public string StatusText { get { return status.Text; } }

        public CheckFixForm(bool testMode, Func<int, int> scale)
        {
            S = scale;
            Text = Lang.T("Check & fix"); Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowIcon = false; ShowInTaskbar = false;
            ClientSize = new Size(S(720), S(600)); MinimumSize = new Size(S(560), S(420)); BackColor = Color.White;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(S(18), S(14), S(18), S(14)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            head = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Text = Lang.T("Checking this PC..."), Margin = new Padding(0, 0, 0, S(2)) };
            sub = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(680), 0), Text = Lang.T("Nothing is changed while Elixar checks."), Margin = new Padding(S(1), 0, 0, S(10)) };
            cards = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, S(10)) };
            foreach (var k in new[] { "Can be freed", "Slows startup", "App updates", "Disk health" })
            {
                cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                var p = new Panel { Dock = DockStyle.Fill, Height = S(62), Margin = new Padding(0, 0, S(8), 0), BorderStyle = BorderStyle.FixedSingle };
                var v = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 15f), Text = "...", Location = new Point(S(8), S(24)) };
                p.Controls.Add(new Label { AutoSize = true, Text = Lang.T(k).ToUpperInvariant(), Font = new Font("Segoe UI", 7.5f), ForeColor = Color.DimGray, Location = new Point(S(8), S(6)) });
                p.Controls.Add(v); cardValue[k] = v; cards.Controls.Add(p);
            }
            rows = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 3, Margin = new Padding(0) };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(26))); rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(680), 0), Margin = new Padding(S(1), S(8), 0, S(4)), Text = "" };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
            btnClose = new Button { Text = Lang.T("Close"), AutoSize = true, Margin = new Padding(S(6), 0, 0, 0) };
            btnFix = new Button { Text = Lang.T("Fix selected"), AutoSize = true, Enabled = false, Font = new Font("Segoe UI Semibold", 9.5f), BackColor = Color.FromArgb(91, 76, 196), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnFix.FlatAppearance.BorderSize = 0;
            var note = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, S(6), S(12), 0), Text = Lang.T("A restore point is made first. Every change is in Logs.") };
            bottom.Controls.AddRange(new Control[] { btnClose, btnFix, note });
            root.Controls.Add(head, 0, 0); root.Controls.Add(sub, 0, 1); root.Controls.Add(cards, 0, 2); root.Controls.Add(rows, 0, 3); root.Controls.Add(status, 0, 4); root.Controls.Add(bottom, 0, 5);
            Controls.Add(root);
            AcceptButton = btnFix; CancelButton = btnClose;
            btnClose.Click += (o, e) => Close();
            btnFix.Click += (o, e) => DoFix();
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }
            Shown += (o, e) => { Theme.Apply(this); Theme.DarkTitle(this); if (!testMode) StartScan(); };
        }

        void UI(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch { } }

        public void StartScan()
        {
            Task.Factory.StartNew(() => CheckFix.Scan(s => UI(() => sub.Text = Lang.T(s))), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                if (t.IsFaulted) { head.Text = Lang.T("Couldn't finish the check"); sub.Text = t.Exception.GetBaseException().Message; return; }
                ShowReport(t.Result);
            }));
        }

        public void ShowReport(CheckReport r)
        {
            report = r;
            head.Text = Lang.T("Here's what Elixar found");
            int secs = Math.Max(1, (int)r.Took.TotalSeconds);
            sub.Text = Lang.T("Checked in " + secs + (secs == 1 ? " second" : " seconds") + ". Only the safe fixes are ticked; nothing is done until you click Fix.");
            Action<string, string, Color> card = (k, v, c) => { cardValue[k].Text = Lang.T(v); cardValue[k].ForeColor = c; };
            Color green = Color.FromArgb(46, 125, 50), orange = Color.FromArgb(180, 95, 6), red = Color.FromArgb(198, 40, 40);
            card("Can be freed", Fs.Fmt(r.CanFree), r.CanFree > 0 ? green : Color.DimGray);
            card("Slows startup", r.SlowStartup < 0 ? "?" : r.SlowStartup + (r.SlowStartup == 1 ? " app" : " apps"), r.SlowStartup > 0 ? orange : green);
            card("App updates", r.Updates < 0 ? "?" : r.Updates.ToString(), r.Updates > 0 ? orange : green);
            card("Disk health", r.Disk, r.Disk == "Failing" ? red : r.Disk == "Worn" ? orange : r.Disk == "Good" ? green : Color.DimGray);
            rows.SuspendLayout(); rows.Controls.Clear(); rows.RowStyles.Clear(); boxes.Clear();
            int row = 0;
            foreach (var it in r.Items)
            {
                var cb = new CheckBox { AutoSize = true, Checked = it.On, Enabled = !it.Locked, Visible = !it.Locked, Tag = it, Margin = new Padding(S(2), S(10), 0, 0) };
                cb.CheckedChanged += (o, e) => { ((FixItem)((CheckBox)o).Tag).On = ((CheckBox)o).Checked; UpdateFix(); };
                boxes.Add(cb);
                var tagColor = it.Warning ? red : it.Tag == "Safe" ? green : it.Tag == "Your decision" ? Color.FromArgb(91, 76, 196) : Color.DimGray;
                var text = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, S(8), 0, S(4)) };
                var titleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                titleRow.Controls.Add(new Label { AutoSize = true, Text = Lang.T(it.Title), Font = new Font("Segoe UI Semibold", 9.5f), ForeColor = it.Warning ? red : Color.FromArgb(24, 32, 43), Margin = new Padding(0) });
                titleRow.Controls.Add(new Label { AutoSize = true, Text = Lang.T(it.Tag), Font = new Font("Segoe UI", 8f), ForeColor = tagColor, Margin = new Padding(S(8), S(2), 0, 0) });
                text.Controls.Add(titleRow);
                text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(480), 0), Text = Lang.T(it.Detail), ForeColor = Color.DimGray, Margin = new Padding(0, S(1), 0, 0) });
                var right = new Label { AutoSize = true, Text = Lang.T(it.Right), ForeColor = Color.DimGray, Margin = new Padding(S(8), S(10), 0, 0) };
                rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                rows.Controls.Add(cb, 0, row); rows.Controls.Add(text, 1, row); rows.Controls.Add(right, 2, row);
                row++;
            }
            rows.RowCount = row + 1; rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rows.ResumeLayout();
            UpdateFix();
            if (Scanned != null) Scanned();
        }

        void UpdateFix()
        {
            int n = report == null ? 0 : report.Items.Count(i => i.On && !i.Locked);
            btnFix.Enabled = n > 0;
            btnFix.Text = Lang.T(n > 0 ? "Fix selected (" + n + ")" : "Fix selected");
        }

        public void DoFix()
        {
            if (report == null) return;
            var chosen = report.Items.Where(i => i.On && !i.Locked).ToList();
            if (chosen.Count == 0) return;
            btnFix.Enabled = false; foreach (var b in boxes) b.Enabled = false;
            head.Text = Lang.T("Fixing...");
            Task.Factory.StartNew(() => CheckFix.Fix(chosen, s => UI(() => status.Text = Lang.T(s))), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                if (t.IsFaulted) { head.Text = Lang.T("Couldn't finish"); status.Text = t.Exception.GetBaseException().Message; return; }
                LastFix = t.Result;
                head.Text = Lang.T("Done");
                var parts = new List<string>();
                if (LastFix.Freed > 0) parts.Add("freed " + Fs.Fmt(LastFix.Freed));
                if (LastFix.StartupOff > 0) parts.Add(LastFix.StartupOff + " startup program" + (LastFix.StartupOff == 1 ? "" : "s") + " turned off");
                if (LastFix.Updated > 0) parts.Add(LastFix.Updated + " app" + (LastFix.Updated == 1 ? "" : "s") + " updated");
                if (LastFix.UpdateFailed > 0) parts.Add(LastFix.UpdateFailed + " update" + (LastFix.UpdateFailed == 1 ? "" : "s") + " didn't work");
                sub.Text = Lang.T(parts.Count > 0 ? char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts).Substring(1) + "." : "Nothing needed changing.");
                status.Text = Lang.T("Every change is in Logs; cleaned items that go to Undo can be put back in Tools > Undo." + (LastFix.RestorePoint.StartsWith("Restore point created") ? " A restore point was made first." : ""));
                btnClose.Text = Lang.T("Close");
                if (Fixed != null) Fixed();
            }));
        }
    }

    public partial class MainForm
    {
        public CheckFixForm LastCheckFix;   // self-test and previews
        public bool CheckFixShown { get { return btnCheckFix != null && !LinuxUi; } }

        public void OpenCheckFix()
        {
            using (var f = new CheckFixForm(testMode, S))
            {
                LastCheckFix = f;
                f.Fixed += () => RefreshCurrentDrive();
                if (testMode) return;
                f.ShowDialog(this);
            }
        }
    }
}
