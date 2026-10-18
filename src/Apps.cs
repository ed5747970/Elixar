// Apps by size: installed programs from the registry, measured size, last-used estimate,
// and Uninstall through each app's own uninstaller.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class AppEntry
    {
        public string Name, Publisher, Version, Location, Uninstall, KeyPath;
        public DateTime? Installed;
        public long ReportedBytes;     // EstimatedSize from the registry
        public long MeasuredBytes = -1; // size of InstallLocation on disk, -1 = not measured
        public DateTime? LastUsed;     // newest Prefetch entry for one of its .exe files
        public bool HasExe;            // we found .exe files to look up
        public bool PerUser;           // listed under your account (HKCU): any program you run could have written it
        public long Size { get { return MeasuredBytes > 0 ? MeasuredBytes : ReportedBytes; } }
        public bool SizeMeasured { get { return MeasuredBytes > 0; } }
    }

    public static class AppsData
    {
        // Turns one registry entry (as a dictionary of values) into an AppEntry, or null when it should be hidden.
        public static AppEntry FromValues(IDictionary<string, object> v, string keyPath)
        {
            Func<string, string> S = k => { object o; return v.TryGetValue(k, out o) && o != null ? o.ToString().Trim() : ""; };
            Func<string, long> N = k =>
            {
                object o; if (!v.TryGetValue(k, out o) || o == null) return 0;
                try { return Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return 0; }
            };

            string name = S("DisplayName");
            if (name.Length == 0) return null;
            if (N("SystemComponent") == 1) return null;                       // hidden by Windows itself
            if (S("ParentKeyName").Length > 0) return null;                   // updates/patches of another app
            string rt = S("ReleaseType");
            if (rt.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 || rt.IndexOf("hotfix", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            string un = S("UninstallString");
            if (un.Length == 0) return null;                                  // cannot be uninstalled here

            var a = new AppEntry
            {
                Name = name,
                Publisher = S("Publisher"),
                Version = S("DisplayVersion"),
                Location = CleanLocation(S("InstallLocation")),
                Uninstall = un,
                KeyPath = keyPath,
                ReportedBytes = N("EstimatedSize") * 1024L
            };
            DateTime d;
            if (DateTime.TryParseExact(S("InstallDate"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) a.Installed = d;
            return a;
        }

        public static string CleanLocation(string loc)
        {
            loc = (loc ?? "").Trim().Trim('"').Trim();
            if (loc.Length == 0) return "";
            if (loc.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { try { loc = Path.GetDirectoryName(loc); } catch { return ""; } }
            return loc.TrimEnd('\\');
        }

        // Same program can be listed twice (64-bit and 32-bit views, or per user); keep one.
        public static List<AppEntry> Dedupe(IEnumerable<AppEntry> apps)
        {
            return apps.GroupBy(a => (a.Name + "|" + a.Version).ToLowerInvariant())
                       .Select(g => g.OrderByDescending(x => x.Location.Length).First())
                       .ToList();
        }

        // Command line that runs an uninstall string exactly as Windows does.
        public static ProcessStartInfo UninstallCommand(string uninstallString)
        {
            // cmd /s /c "..." keeps every quote inside the original string; it starts in System32, so a bare name like
            // "MsiExec.exe" can never be picked up from whatever folder Elixar was started in
            var psi = new ProcessStartInfo(Win.SystemExe("cmd.exe"), "/s /c \"" + uninstallString + "\"") { UseShellExecute = false, CreateNoWindow = true };
            try { string sys = Environment.GetFolderPath(Environment.SpecialFolder.System); if (!string.IsNullOrEmpty(sys)) psi.WorkingDirectory = sys; } catch { }
            return psi;
        }

        static readonly System.Text.RegularExpressions.Regex ProgramPath = new System.Text.RegularExpressions.Regex(
            @"[A-Za-z]:\\[^""<>|*?\r\n]*?\.(exe|dll|msi|bat|cmd|com|ps1|vbs|js)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // the program files an uninstall command runs
        public static List<string> ProgramsIn(string uninstallString)
        {
            string s = uninstallString ?? "";
            try { s = Environment.ExpandEnvironmentVariables(s); } catch { }
            return ProgramPath.Matches(s).Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Windows' folder and Program Files: only administrators can change what is in them
        public static bool InAdminFolder(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }
            var roots = new List<string>();
            foreach (var r in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetEnvironmentVariable("ProgramW6432"),
                                      Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
                if (!string.IsNullOrEmpty(r)) roots.Add(r.TrimEnd('\\') + "\\");
            return roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) && !full.Contains("..");
        }

        // uninstaller files that sit where other programs can change them and carry no valid signature:
        // running those as administrator could hand administrator rights to whatever replaced them
        public static List<string> RiskyPrograms(string uninstallString, Func<string, bool> signed)
        {
            return ProgramsIn(uninstallString).Where(p => !InAdminFolder(p) && !signed(p)).ToList();
        }

        // A location is only measured when it is a real app folder on C:, never a drive root or a shared system folder.
        public static bool SafeToMeasure(string loc)
        {
            if (string.IsNullOrEmpty(loc)) return false;
            string l = loc.TrimEnd('\\').ToLowerInvariant();
            if (l.Length <= 3) return false;
            string[] shared = { @"c:\windows", @"c:\program files", @"c:\program files (x86)", @"c:\programdata", @"c:\users" };
            if (shared.Contains(l)) return false;
            if (l.StartsWith(@"c:\windows\")) return false;
            return l.StartsWith(@"c:\");
        }

        public static List<AppEntry> ReadInstalled()
        {
            var list = new List<AppEntry>();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return list;
            const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var sources = new List<Tuple<RegistryHive, RegistryView>>
            {
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32),
                Tuple.Create(RegistryHive.CurrentUser, RegistryView.Default)
            };
            foreach (var src in sources)
            {
                try
                {
                    using (var bk = RegistryKey.OpenBaseKey(src.Item1, src.Item2))
                    using (var root = bk.OpenSubKey(sub))
                    {
                        if (root == null) continue;
                        foreach (var name in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = root.OpenSubKey(name))
                                {
                                    if (k == null) continue;
                                    var v = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var vn in k.GetValueNames()) v[vn] = k.GetValue(vn);
                                    var a = FromValues(v, src.Item1 + "\\" + sub + "\\" + name);
                                    if (a != null) { a.PerUser = src.Item1 == RegistryHive.CurrentUser; list.Add(a); }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return Dedupe(list);
        }

        // Windows Prefetch keeps one file per recently run program: NAME.EXE-HASH.pf, written each time it runs.
        public static Dictionary<string, DateTime> ReadPrefetch()
        {
            var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in new DirectoryInfo(@"C:\Windows\Prefetch").GetFiles("*.pf"))
                {
                    int dash = f.Name.LastIndexOf('-');
                    if (dash <= 0) continue;
                    string exe = f.Name.Substring(0, dash);
                    DateTime t = f.LastWriteTime, old;
                    if (!map.TryGetValue(exe, out old) || t > old) map[exe] = t;
                }
            }
            catch { }
            return map;
        }

        public static void FillDetails(AppEntry a, Dictionary<string, DateTime> prefetch)
        {
            if (!SafeToMeasure(a.Location) || !Directory.Exists(a.Location)) return;
            try { a.MeasuredBytes = Fs.SizeOf(a.Location); } catch { }
            DateTime? last = null;
            int seen = 0;
            var stack = new Stack<string>();
            stack.Push(a.Location);
            while (stack.Count > 0 && seen < 3000)
            {
                string dir = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var f in di.EnumerateFiles("*.exe"))
                    {
                        seen++;
                        a.HasExe = true;
                        DateTime t;
                        if (prefetch.TryGetValue(f.Name, out t) && (!last.HasValue || t > last.Value)) last = t;
                    }
                    foreach (var d in di.EnumerateDirectories())
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(d.FullName);
                }
                catch { }
            }
            a.LastUsed = last;
        }

        public static string LastUsedText(AppEntry a)
        {
            if (a.LastUsed.HasValue)
            {
                var days = (DateTime.Now - a.LastUsed.Value).TotalDays;
                if (days < 1) return "today";
                if (days < 2) return "yesterday";
                if (days < 60) return ((int)days) + " days ago";
                return a.LastUsed.Value.ToString("d MMM yyyy");
            }
            return a.HasExe ? "no recent use found" : "unknown";
        }

        public static bool LooksUnused(AppEntry a)
        {
            if (a.LastUsed.HasValue) return (DateTime.Now - a.LastUsed.Value).TotalDays > 90;
            return a.HasExe;
        }
    }

    public class AppSorter : System.Collections.IComparer
    {
        public int Column; public bool Asc;
        public int Compare(object x, object y)
        {
            var a = (AppEntry)((ListViewItem)x).Tag;
            var b = (AppEntry)((ListViewItem)y).Tag;
            int r;
            switch (Column)
            {
                case 1: r = a.Size.CompareTo(b.Size); break;
                case 2: r = (a.LastUsed ?? DateTime.MinValue).CompareTo(b.LastUsed ?? DateTime.MinValue); break;
                case 3: r = (a.Installed ?? DateTime.MinValue).CompareTo(b.Installed ?? DateTime.MinValue); break;
                case 4: r = string.Compare(a.Publisher, b.Publisher, StringComparison.OrdinalIgnoreCase); break;
                case 5: r = string.Compare(a.Location, b.Location, StringComparison.OrdinalIgnoreCase); break;
                default: r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
            }
            return Asc ? r : -r;
        }
    }

    public partial class MainForm
    {
        public event Action AppsDone;
        TabPage pageApps;
        ListView appsList;
        Label appsHeader, appsStatus;
        Button btnAppsRefresh, btnUninstall, btnAppFolder, btnAppsSettings;
        bool appsBusy;
        readonly AppSorter appSorter = new AppSorter { Column = 1, Asc = false };
        public int AppsCount { get { return appsList == null ? 0 : appsList.Items.Count; } }

        void BuildApps()
        {
            pageApps = new TabPage("Apps") { BackColor = Color.FromArgb(246, 248, 251) };
            appsTabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(12), S(4)) };
            pagePrograms = new TabPage("Programs") { BackColor = Color.FromArgb(246, 248, 251) };
            appsTabs.TabPages.Add(pagePrograms);
            pageApps.Controls.Add(appsTabs);
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(S(12), S(10), S(12), S(10)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            appsHeader = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Text = "Installed apps", Margin = new Padding(0, S(2), 0, 0) };
            btnAppsRefresh = MakeButton("Refresh");
            head.Controls.Add(appsHeader, 0, 0);
            head.Controls.Add(btnAppsRefresh, 1, 0);

            appsStatus = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(1), 0, 0, S(8)),
                Text = "Uninstalling apps you don't use is the safe way to shrink the Critical part of C:. Orange = no use found in 90 days."
            };

            appsList = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, ShowItemToolTips = true, MultiSelect = false };
            appsList.Columns.Add("App", S(300));
            appsList.Columns.Add("Size", S(90), HorizontalAlignment.Right);
            appsList.Columns.Add("Last used (estimate)", S(150));
            appsList.Columns.Add("Installed", S(95));
            appsList.Columns.Add("Publisher", S(200));
            appsList.Columns.Add("Folder", S(380));
            appsList.ListViewItemSorter = appSorter;
            appsList.ColumnClick += (o, e) =>
            {
                if (appSorter.Column == e.Column) appSorter.Asc = !appSorter.Asc;
                else { appSorter.Column = e.Column; appSorter.Asc = e.Column == 0 || e.Column == 4 || e.Column == 5; }
                appsList.Sort();
            };

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, S(8), 0, 0) };
            btnUninstall = MakeButton("Uninstall...");
            btnUninstall.Font = new Font("Segoe UI Semibold", 9f);
            btnAppFolder = MakeButton("Open folder");
            btnAppsSettings = MakeButton("Open Windows Installed apps");
            btnRow.Controls.AddRange(new Control[] { btnUninstall, btnAppFolder, btnAppsSettings });
            btnUninstall.Enabled = btnAppFolder.Enabled = false;

            root.Controls.Add(head, 0, 0);
            root.Controls.Add(appsStatus, 0, 1);
            root.Controls.Add(appsList, 0, 2);
            root.Controls.Add(btnRow, 0, 3);
            pagePrograms.Controls.Add(root);

            appsList.SelectedIndexChanged += (o, e) =>
            {
                var a = SelectedApp();
                btnUninstall.Enabled = a != null && !testMode;
                btnAppFolder.Enabled = a != null && a.Location.Length > 0;
            };
            appsList.DoubleClick += (o, e) => OpenAppFolder();
            btnAppsRefresh.Click += (o, e) => RefreshApps();
            btnAppFolder.Click += (o, e) => OpenAppFolder();
            btnUninstall.Click += (o, e) => UninstallSelected();
            btnAppsSettings.Click += (o, e) =>
            {
                try { Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }); }
                catch (Exception ex) { Log("Could not open Windows settings: " + ex.Message); }
            };
        }

        AppEntry SelectedApp()
        {
            return appsList.SelectedItems.Count == 0 ? null : (AppEntry)appsList.SelectedItems[0].Tag;
        }

        void OpenAppFolder()
        {
            var a = SelectedApp();
            if (a == null || a.Location.Length == 0) return;
            try { Process.Start(Win.SystemExe("explorer.exe"), "\"" + a.Location + "\""); }
            catch (Exception ex) { Log("Could not open the folder: " + ex.Message); }
        }

        void UninstallSelected()
        {
            var a = SelectedApp();
            if (a != null) UninstallProgram(a);
        }

        public void UninstallProgram(AppEntry a)
        {
            if (a == null || testMode) return;
            var ok = Lang.Box(this,
                "Uninstall " + a.Name + "?\n\nThis opens the app's own uninstaller (the same one Windows uses). Follow its steps.\n" +
                "Size: " + Fs.Fmt(a.Size) + "\n\nWhen it has finished, click Refresh.",
                "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (ok != DialogResult.OK) return;
            try
            {
                var cmd = AppsData.UninstallCommand(a.Uninstall);
                if (a.PerUser && Win.IsWindows && Sys.IsAdmin())
                {
                    // installed for your account only: its uninstaller runs with your normal rights, not as administrator
                    string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                    try { Unelevated.Start(cmdExe, cmd.Arguments); }
                    catch (Exception ue)
                    {
                        Log("Could not start the uninstaller without admin rights: " + ue.Message);
                        if (Lang.Box(this, a.Name + " is installed for your account only, so Elixar runs its uninstaller without administrator rights. That did not work:\n" + ue.Message +
                                "\n\nRun it as administrator instead? Only do this if you trust this app.\n\nCommand: " + a.Uninstall,
                                "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                        Process.Start(cmd);
                    }
                }
                else
                {
                    var risky = Win.IsWindows ? AppsData.RiskyPrograms(a.Uninstall, Trust.HasValidSignature) : new List<string>();
                    if (risky.Count > 0)
                    {
                        Log("Uninstaller for " + a.Name + " is unsigned and outside Program Files: " + string.Join(", ", risky));
                        if (Lang.BoxNo(this, "The uninstaller of " + a.Name + " is not signed, and it sits in a folder other programs can change:\n\n" + string.Join("\n", risky) +
                                "\n\nElixar runs with administrator rights, so running it from here would skip Windows' own permission prompt. " +
                                "Safer: uninstall it from Windows' Installed apps (the button on this tab), which asks you first.\n\nRun it from Elixar anyway?",
                                "Elixar") != DialogResult.Yes) return;
                    }
                    Process.Start(cmd);
                }
                Log("Started the uninstaller for " + a.Name + (a.PerUser ? " (with your normal rights)." : "."));
                appsStatus.Text = "Uninstaller for " + a.Name + " started. When it has finished, click Refresh.";
            }
            catch (Exception ex)
            {
                Log("Could not start the uninstaller for " + a.Name + ": " + ex.Message);
                Lang.Box(this, "Could not start the uninstaller:\n" + ex.Message + "\n\nUse \"Open Windows Installed apps\" instead.",
                    "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void RefreshApps()
        {
            if (appsBusy) return;
            appsBusy = true;
            btnAppsRefresh.Enabled = false;
            appsList.Items.Clear();
            appsStatus.Text = "Reading installed apps...";
            var watch = Stopwatch.StartNew();
            Task.Factory.StartNew(() =>
            {
                var apps = AppsData.ReadInstalled();
                var pf = AppsData.ReadPrefetch();
                UI(() => ShowApps(apps, false));
                int i = 0;
                foreach (var a in apps)
                {
                    i++;
                    var app = a; int n = i;
                    UI(() => appsStatus.Text = "Measuring " + n + " of " + apps.Count + ": " + app.Name);
                    try { AppsData.FillDetails(app, pf); } catch { }
                }
                return apps;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                appsBusy = false;
                btnAppsRefresh.Enabled = true;
                if (t.IsFaulted)
                {
                    appsStatus.Text = "Could not read installed apps: " + t.Exception.GetBaseException().Message;
                }
                else
                {
                    ShowApps(t.Result, true);
                    programsIndex = t.Result;
                    long total = t.Result.Sum(a => a.Size);
                    int unused = t.Result.Count(AppsData.LooksUnused);
                    appsHeader.Text = "Installed apps   " + t.Result.Count + " apps, " + Fs.Fmt(total);
                    appsStatus.Text = "Updated " + DateTime.Now.ToString("HH:mm") + " (took " + (int)Math.Round(watch.Elapsed.TotalSeconds) + " s).  " +
                        unused + " apps show no use in the last 90 days (orange). Click a column to sort.";
                }
                if (AppsDone != null) AppsDone();
            }));
        }

        void ShowApps(List<AppEntry> apps, bool final)
        {
            appsList.BeginUpdate();
            appsList.Items.Clear();
            foreach (var a in apps)
            {
                var lvi = new ListViewItem(a.Name) { Tag = a, UseItemStyleForSubItems = false };
                lvi.SubItems.Add(a.Size > 0 ? Fs.Fmt(a.Size) + (a.SizeMeasured || !final ? "" : "*") : "?");
                var last = lvi.SubItems.Add(final ? AppsData.LastUsedText(a) : "...");
                if (final && AppsData.LooksUnused(a)) { last.ForeColor = CautionColor; lvi.ForeColor = CautionColor; }
                lvi.SubItems.Add(a.Installed.HasValue ? a.Installed.Value.ToString("yyyy-MM-dd") : "");
                lvi.SubItems.Add(a.Publisher);
                var loc = lvi.SubItems.Add(a.Location);
                loc.ForeColor = Color.DimGray;
                lvi.ToolTipText = a.Name + (a.Version.Length > 0 ? "  " + a.Version : "") +
                    (final && !a.SizeMeasured && a.Size > 0 ? "\nSize as reported by the app (*), its folder could not be measured." : "");
                appsList.Items.Add(lvi);
            }
            appsList.Sort();
            appsList.EndUpdate();
        }
    }
}
