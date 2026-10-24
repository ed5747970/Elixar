// Apps > Updates: the apps with a newer version waiting, updated with Windows' own package manager (winget).
// winget is started only from Windows' protected apps folder (Program Files\WindowsApps, which only Windows can change),
// never through the shortcut in your account's folder, which any program you run could replace.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class AppUpdate { public string Name = "", Id = "", Version = "", Available = "", Source = ""; }

    public static class WinGet
    {
        public static Func<string, string> RunOverride;   // self-test and previews: the output for these arguments
        static string exe;

        // winget.exe inside Microsoft's App Installer package, in the folder only Windows can change
        public static string Exe
        {
            get
            {
                if (exe != null) return exe;
                if (!Win.IsWindows) return null;
                try
                {
                    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
                    string loc = StoreData.RunPs("(Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1).InstallLocation", 60000).Trim();
                    if (loc.Length == 0 || !loc.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || !loc.EndsWith("__8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase)) return null;
                    string f = Path.Combine(loc, "winget.exe");
                    if (!File.Exists(f) || !SafeDelete.RealPathMatches(f)) return null;
                    return exe = f;
                }
                catch { return null; }
            }
        }

        public static bool Available { get { return RunOverride != null || Exe != null; } }

        public static string Run(string args, int timeoutMs)
        {
            if (RunOverride != null) return RunOverride(args);
            string w = Exe;
            if (w == null) return "ERROR: winget isn't available on this PC.";
            var psi = new ProcessStartInfo(w, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                                                      StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(w) };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "ERROR: timed out"; }
                return o.Result + (e.Result.Trim().Length > 0 ? "\n" + e.Result : "") + (p.ExitCode != 0 ? "\nEXIT " + p.ExitCode : "");
            }
        }

        public const string ListArgs = "upgrade --include-unknown --accept-source-agreements --disable-interactivity";

        // a package id is letters, digits and . _ + - (anything else is refused: it goes on winget's command line)
        public static bool ValidId(string id) { return Regex.IsMatch(id ?? "", @"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$"); }

        public static string UpgradeArgs(string id)
        {
            if (!ValidId(id)) throw new ArgumentException("not a package id");
            return "upgrade --id " + id + " --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
        }

        // winget's table: the header gives where each column starts; the line of dashes under it starts the rows
        public static List<AppUpdate> Parse(string output)
        {
            var list = new List<AppUpdate>();
            if (string.IsNullOrEmpty(output)) return list;
            var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => { int i = l.LastIndexOf('\r'); return i >= 0 ? l.Substring(i + 1) : l; }).ToList();
            int dash = lines.FindIndex(l => l.Trim().Length > 10 && l.Trim().All(c => c == '-'));
            if (dash < 1) return list;
            string head = lines[dash - 1];
            // each column starts where a header word follows a space; a two-word header ("Nueva versión") is one column
            var starts = new List<int>(); var ends = new List<int>();
            for (int i = 0; i < head.Length; i++)
            {
                if (head[i] != ' ' && (i == 0 || head[i - 1] == ' ')) starts.Add(i);
                if (head[i] != ' ' && (i == head.Length - 1 || head[i + 1] == ' ')) ends.Add(i);
            }
            while (starts.Count > 5)
            {
                int best = -1;
                for (int i = 0; i + 1 < starts.Count; i++) if (starts[i + 1] - ends[i] == 2) { best = i; break; }
                if (best < 0) break;
                starts.RemoveAt(best + 1); ends.RemoveAt(best);
            }
            if (starts.Count < 4) return list;
            Func<string, int, string> col = (l, n) =>
            {
                int a = starts[n], b = n + 1 < starts.Count ? starts[n + 1] : l.Length;
                if (a >= l.Length) return "";
                return l.Substring(a, Math.Min(l.Length, b) - a).Trim();
            };
            for (int k = dash + 1; k < lines.Count; k++)
            {
                string l = lines[k];
                if (l.Trim().Length == 0) continue;
                if (!l.StartsWith(" ") && Regex.IsMatch(l, @"^\d+ (upgrades?|package)", RegexOptions.IgnoreCase)) break;   // "3 upgrades available."
                var u = new AppUpdate { Name = col(l, 0), Id = col(l, 1), Version = col(l, 2), Available = col(l, 3), Source = starts.Count > 4 ? col(l, 4) : "" };
                if (!ValidId(u.Id) || u.Available.Length == 0) continue;
                list.Add(u);
            }
            return list;
        }

        public static List<AppUpdate> List(out string error)
        {
            error = null;
            string o = Run(ListArgs, 180000);
            if (o.StartsWith("ERROR")) { error = o.Substring(7); return new List<AppUpdate>(); }
            return Parse(o);
        }

        public static bool Upgrade(AppUpdate u, Action<string> log)
        {
            string o = Run(UpgradeArgs(u.Id), 30 * 60 * 1000);
            bool ok = !o.StartsWith("ERROR") && !o.Contains("\nEXIT ") || o.Contains("Successfully installed");
            string last = o.Replace("\r", "\n").Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0 && !x.StartsWith("EXIT")) ?? "";
            if (log != null) log("  " + u.Name + ": " + (ok ? "updated to " + u.Available : "not updated (" + last + ")"));
            using (ChangeLog.In("App updates"))
                ChangeLog.Add("Apps", ok ? "Updated an app" : "App update didn't work", u.Name + " (" + u.Id + ")", u.Version, ok ? u.Available : "Failed", last, !ok);
            return ok;
        }
    }

    public partial class MainForm
    {
        TabPage pageUpdates;
        ListView updList;
        Label updStatus;
        Button btnUpdCheck, btnUpdSelected, btnUpdAll;
        bool appUpdBusy;
        public void ShowAppUpdates() { tabs.SelectedTab = pageApps; if (appsTabs.TabPages.Contains(pageUpdates)) appsTabs.SelectedTab = pageUpdates; }
        public int UpdateRows { get { return updList == null ? 0 : updList.Items.Count; } }
        public event Action UpdatesDone;

        void BuildAppUpdates()
        {
            pageUpdates = new TabPage("Updates") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Title("App updates"), 0, 0);
            t.Controls.Add(Note("Apps with a newer version waiting. Outdated apps are the most common way in for attackers, so keep them up to date. " +
                                "Elixar uses Windows' own package manager (winget) and the app's own installer, quietly. Each update is written to Logs."), 0, 1);
            updStatus = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(S(2), 0, 0, S(6)), UseMnemonic = false, Text = "Click \"Check for updates\"." };
            t.Controls.Add(updStatus, 0, 2);
            updList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false };
            updList.Columns.Add("App", S(320)); updList.Columns.Add("Installed", S(130)); updList.Columns.Add("New version", S(130)); updList.Columns.Add("Package", S(300)); updList.Columns.Add("Source", S(90));
            t.Controls.Add(updList, 0, 3);
            var b = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
            btnUpdCheck = MakeButton("Check for updates"); btnUpdSelected = MakeButton("Update selected"); btnUpdAll = MakeButton("Update all");
            btnUpdSelected.Font = new Font("Segoe UI Semibold", 9f); btnUpdSelected.Enabled = btnUpdAll.Enabled = false;
            b.Controls.AddRange(new Control[] { btnUpdCheck, btnUpdSelected, btnUpdAll });
            t.Controls.Add(b, 0, 4);
            pageUpdates.Controls.Add(t);
            btnUpdCheck.Click += (o, e) => CheckAppUpdates();
            btnUpdSelected.Click += (o, e) => RunAppUpdates(updList.CheckedItems.Cast<ListViewItem>().Select(r => (AppUpdate)r.Tag).ToList());
            btnUpdAll.Click += (o, e) => RunAppUpdates(updList.Items.Cast<ListViewItem>().Select(r => (AppUpdate)r.Tag).ToList());
            updList.ItemChecked += (o, e) => { if (!appUpdBusy) btnUpdSelected.Enabled = updList.CheckedItems.Count > 0 && !testMode; };
        }

        public void CheckAppUpdates()
        {
            if (appUpdBusy) return;
            if (!WinGet.Available) { updStatus.Text = Lang.T("Windows' package manager (winget) isn't on this PC. Get \"App Installer\" from the Microsoft Store, then check again."); return; }
            appUpdBusy = true; btnUpdCheck.Enabled = btnUpdSelected.Enabled = btnUpdAll.Enabled = false;
            updStatus.Text = Lang.T("Checking for updates (this takes up to a minute)..."); updStatus.ForeColor = Theme.Ink;
            Task.Factory.StartNew(() => { string err; var l = WinGet.List(out err); return Tuple.Create(l, err); }).ContinueWith(t => UI(() =>
            {
                appUpdBusy = false; btnUpdCheck.Enabled = true;
                var l = t.IsFaulted ? new List<AppUpdate>() : t.Result.Item1;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result.Item2;
                updList.BeginUpdate(); updList.Items.Clear();
                foreach (var u in l.OrderBy(x => x.Name))
                    updList.Items.Add(new ListViewItem(new[] { u.Name, u.Version, u.Available, u.Id, u.Source }) { Tag = u, Checked = true });
                updList.EndUpdate();
                updStatus.Text = Lang.T(err != null ? "Could not check: " + err : l.Count == 0 ? "Every app is up to date." : l.Count == 1 ? "1 app has an update." : l.Count + " apps have an update.");
                updStatus.ForeColor = Theme.MapFore(err != null ? CautionColor : l.Count == 0 ? SafeColor : ReviewColor);
                btnUpdSelected.Enabled = btnUpdAll.Enabled = l.Count > 0 && !testMode;
                if (UpdatesDone != null) UpdatesDone();
            }));
        }

        void RunAppUpdates(List<AppUpdate> list)
        {
            if (appUpdBusy || list.Count == 0 || testMode || Policy.Blocked(this, "apps.update")) return;
            if (Lang.Box(this, Lang.T("Update these " + list.Count + " apps?") + "\n\n" + string.Join("\n", list.Take(15).Select(u => "  - " + u.Name + "  " + u.Version + " > " + u.Available)) +
                    (list.Count > 15 ? "\n  ..." : "") + "\n\n" + Lang.T("Close them first: an app that is open may not update. Each update runs the app's own installer, quietly."),
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            appUpdBusy = true; btnUpdCheck.Enabled = btnUpdSelected.Enabled = btnUpdAll.Enabled = false;
            Task.Factory.StartNew(() =>
            {
                int ok = 0, i = 0;
                foreach (var u in list)
                {
                    i++; var cur = u; int n = i;
                    UI(() => updStatus.Text = Lang.T("Updating " + cur.Name + " (" + n + " of " + list.Count + ")..."));
                    try { if (WinGet.Upgrade(u, Log)) ok++; } catch (Exception ex) { Log("  " + u.Name + ": " + ex.Message); }
                }
                return ok;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                appUpdBusy = false;
                int ok = t.IsFaulted ? 0 : t.Result;
                Log(Lang.T("App updates: " + ok + " of " + list.Count + " updated."));
                appUpdBusy = false;
                CheckAppUpdates();
            }));
        }
    }
}
