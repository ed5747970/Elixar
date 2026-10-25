// For IT: each PC writes a small status file (Elixar.exe --report <folder>) into one shared folder, and Policy manager >
// Fleet report shows them all in one table. The file holds the computer's name and numbers only: no user names, no file names.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class FleetStatus
    {
        public string Tool = "elixar-status";
        public int Format = 1;
        public string Computer = "", Elixar = "", Windows = "", Time = "", PolicyMode = "", PolicyFingerprint = "", LastClean = "";
        public long Total, Free, Saved, SavedThisMonth, UndoBytes, LastCleanBytes;
        public int FreePercent, AppUpdates = -1;
        public string[] TweaksOn = new string[0];
        public string File;   // where it was read from (not written)
    }

    public static class Fleet
    {
        public static FleetStatus Collect(bool withUpdates)
        {
            var s = new FleetStatus();
            s.Computer = Environment.MachineName;
            s.Elixar = Updates.Current.ToString(3);
            s.Windows = Environment.OSVersion.VersionString;
            s.Time = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            s.Total = Sys.Total(); s.Free = Sys.Free(); s.FreePercent = s.Total > 0 ? (int)Math.Round(s.Free * 100.0 / s.Total) : 0;
            try { var sv = Savings.Read(); s.Saved = sv.Bytes; s.SavedThisMonth = sv.ThisMonth; } catch { }
            try { s.UndoBytes = Quarantine.List().Sum(b => Math.Max(0, b.Bytes)); } catch { }
            try
            {
                var last = ChangeLog.Read().FirstOrDefault(e => e.Kind == "Cleaning" && !e.Failed);
                if (last != null) s.LastClean = last.Time;
                var runs = Automation.ReadHistory(Automation.HistoryFile);
                if (runs.Count > 0) { var r = runs.OrderByDescending(x => x.Item1).First(); if (string.IsNullOrEmpty(s.LastClean) || string.CompareOrdinal(r.Item1.ToString("s"), s.LastClean) > 0) { s.LastClean = r.Item1.ToString("s", CultureInfo.InvariantCulture); s.LastCleanBytes = r.Item2; } }
            }
            catch { }
            try
            {
                Policy.Get("clean.level");
                s.PolicyMode = Policy.Org().Keys.Any(k => !Policy.ByCompany(k)) ? "organization" : Policy.UseCustom ? "custom" : "default";
                s.PolicyFingerprint = Policy.Fingerprint(Policy.Rules.ToDictionary(r => r.Key, r => Policy.Get(r.Key)));
            }
            catch { }
            if (Win.IsWindows || Tweaks.TestPrefix != null) try { s.TweaksOn = Tweaks.All.Where(t => Tweaks.State(t) == TweakState.On).Select(t => t.Id).ToArray(); } catch { }
            if (withUpdates) try { string err; var l = WinGet.List(out err); if (err == null) s.AppUpdates = l.Count; } catch { }
            return s;
        }

        public static string Json(FleetStatus s)
        {
            var d = new Dictionary<string, object>
            {
                { "tool", s.Tool }, { "format", s.Format }, { "computer", s.Computer }, { "elixar", s.Elixar }, { "windows", s.Windows }, { "time", s.Time },
                { "total", s.Total }, { "free", s.Free }, { "freePercent", s.FreePercent }, { "saved", s.Saved }, { "savedThisMonth", s.SavedThisMonth },
                { "undoBytes", s.UndoBytes }, { "lastClean", s.LastClean }, { "lastCleanBytes", s.LastCleanBytes },
                { "policyMode", s.PolicyMode }, { "policyFingerprint", s.PolicyFingerprint }, { "tweaksOn", s.TweaksOn }, { "appUpdates", s.AppUpdates },
            };
            return new JavaScriptSerializer().Serialize(d);
        }

        public static FleetStatus Parse(string json)
        {
            if (json == null || json.Length > 64 * 1024) return null;
            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); } catch { return null; }
            if (d == null || !d.ContainsKey("tool") || (d["tool"] as string) != "elixar-status") return null;
            Func<string, string> str = k => { object o; return d.TryGetValue(k, out o) && o != null ? Clip(o.ToString(), 200) : ""; };
            Func<string, long> num = k => { object o; long v; return d.TryGetValue(k, out o) && o != null && long.TryParse(o.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0; };
            var s = new FleetStatus
            {
                Computer = str("computer"), Elixar = str("elixar"), Windows = str("windows"), Time = str("time"), PolicyMode = str("policyMode"), PolicyFingerprint = str("policyFingerprint"),
                LastClean = str("lastClean"), Total = num("total"), Free = num("free"), FreePercent = (int)Math.Max(0, Math.Min(100, num("freePercent"))), Saved = num("saved"),
                SavedThisMonth = num("savedThisMonth"), UndoBytes = num("undoBytes"), LastCleanBytes = num("lastCleanBytes"), AppUpdates = d.ContainsKey("appUpdates") ? (int)num("appUpdates") : -1,
            };
            object t; var arr = d.TryGetValue("tweaksOn", out t) ? t as System.Collections.ArrayList : null;
            if (arr != null) s.TweaksOn = arr.Cast<object>().Where(x => x != null).Select(x => Clip(x.ToString(), 40)).Take(20).ToArray();
            if (s.Computer.Length == 0) return null;
            return s;
        }

        static string Clip(string v, int n) { v = new string((v ?? "").Where(c => c >= ' ').ToArray()); return v.Length > n ? v.Substring(0, n) : v; }

        // a folder: <folder>\<COMPUTER>.json; a path ending in .json: that file
        public static string Target(string where)
        {
            if (string.IsNullOrEmpty(where)) throw new ArgumentException("no folder given");
            if (where.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(where);
            string name = new string(Environment.MachineName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
            if (name.Length == 0) name = "PC";
            return Path.Combine(Path.GetFullPath(where), name + ".json");
        }

        public static string Write(string where, FleetStatus s)
        {
            string file = Target(where);
            byte[] b = new UTF8Encoding(false).GetBytes(Json(s));
            bool unc = file.StartsWith(@"\\") && !file.StartsWith(@"\\?\") && !file.StartsWith(@"\\.\");
            if (unc || !Win.IsWindows)
            {
                // a network share: the server decides where names lead, not this PC
                string tmp = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                using (var f = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) f.Write(b, 0, b.Length);
                try { if (File.Exists(file)) File.Delete(file); File.Move(tmp, file); } catch { try { File.Delete(tmp); } catch { } throw; }
            }
            else SafeFile.Save(file, b);
            return file;
        }

        public static List<FleetStatus> ReadFolder(string folder, out int bad)
        {
            bad = 0;
            var list = new List<FleetStatus>();
            foreach (var f in Directory.GetFiles(folder, "*.json").Take(5000))
            {
                try
                {
                    if (new FileInfo(f).Length > 64 * 1024) { bad++; continue; }
                    var s = Parse(File.ReadAllText(f));
                    if (s == null) { bad++; continue; }
                    s.File = f; list.Add(s);
                }
                catch { bad++; }
            }
            return list.OrderBy(x => x.FreePercent).ThenBy(x => x.Computer).ToList();
        }

        public static string Csv(List<FleetStatus> list)
        {
            Func<string, string> q = v => { v = v ?? ""; if (v.Length > 0 && "=+-@\t\r".IndexOf(v[0]) >= 0) v = "'" + v; return "\"" + v.Replace("\"", "\"\"") + "\""; };
            var sb = new StringBuilder("Computer,Elixar,Free,Free %,Total,Saved,Last clean,Policy,Fingerprint,Tweaks on,App updates,Reported\r\n");
            foreach (var s in list)
                sb.Append(string.Join(",", new[] { q(s.Computer), q(s.Elixar), s.Free.ToString(), s.FreePercent.ToString(), s.Total.ToString(), s.Saved.ToString(), q(s.LastClean), q(s.PolicyMode),
                                                   q(s.PolicyFingerprint), q(string.Join(" ", s.TweaksOn)), s.AppUpdates.ToString(), q(s.Time) })).Append("\r\n");
            return sb.ToString();
        }
    }

    public partial class MainForm
    {
        TabPage polFleet;
        ListView fleetList;
        Label fleetStatus;
        TextBox fleetFolder;
        List<FleetStatus> fleetRows = new List<FleetStatus>();

        TabPage BuildPolicyFleet()
        {
            polFleet = new TabPage("Fleet report") { BackColor = Color.White };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(S(10), S(8), S(10), S(8)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(1000), 0), Margin = new Padding(0, 0, 0, S(6)), UseMnemonic = false,
                Text = "For IT: on each PC run  Elixar.exe --report \\\\server\\share\\elixar  (for example as a daily scheduled task). Each PC writes one small file " +
                       "with its name, free space, savings, policy and tweaks - no user names or file names. Open that folder here to see them all."
            }, 0, 0);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, S(6)) };
            fleetFolder = new TextBox { Width = S(420), Margin = new Padding(0, S(3), S(6), 0), Text = Settings.Get("FleetFolder", "") };
            var browse = MakeButton("Choose folder..."); var load = MakeButton("Show"); var csv = MakeButton("Save as CSV..."); var mine = MakeButton("Add this PC");
            row.Controls.AddRange(new Control[] { fleetFolder, browse, load, mine, csv });
            t.Controls.Add(row, 0, 1);
            fleetList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            foreach (var c in new[] { "Computer|150", "Free|90", "Free %|60", "Saved by Elixar|110", "Last clean|130", "Policy|140", "Tweaks on|260", "App updates|90", "Elixar|70", "Reported|130" })
            { var p = c.Split('|'); fleetList.Columns.Add(p[0], S(int.Parse(p[1]))); }
            t.Controls.Add(fleetList, 0, 2);
            fleetStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, S(6), 0, 0), UseMnemonic = false };
            t.Controls.Add(fleetStatus, 0, 3);
            polFleet.Controls.Add(t);
            browse.Click += (o, e) =>
            {
                if (testMode) return;
                using (var d = new FolderBrowserDialog { Description = Lang.T("The folder the PCs write their reports to"), SelectedPath = fleetFolder.Text })
                    if (d.ShowDialog(this) == DialogResult.OK) { fleetFolder.Text = d.SelectedPath; FleetLoad(); }
            };
            load.Click += (o, e) => FleetLoad();
            mine.Click += (o, e) =>
            {
                if (testMode && FleetHook == null) return;
                string dir = fleetFolder.Text.Trim(); if (dir.Length == 0) return;
                Task.Factory.StartNew(() => Fleet.Write(dir, Fleet.Collect(false))).ContinueWith(tk => UI(() =>
                {
                    if (tk.IsFaulted) fleetStatus.Text = Lang.T("Could not write the report: " + tk.Exception.GetBaseException().Message);
                    else { Log(Lang.T("Fleet report written: " + tk.Result)); FleetLoad(); }
                }));
            };
            csv.Click += (o, e) =>
            {
                if (testMode || fleetRows.Count == 0) return;
                using (var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Elixar fleet " + DateTime.Today.ToString("yyyy-MM-dd") + ".csv", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
                    if (d.ShowDialog(this) == DialogResult.OK) try { SafeFile.Save(d.FileName, "\uFEFF" + Fleet.Csv(fleetRows)); } catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); }
            };
            return polFleet;
        }
        public static object FleetHook;   // self-test

        public void FleetLoad()
        {
            string dir = fleetFolder.Text.Trim();
            if (dir.Length == 0) { fleetStatus.Text = Lang.T("Choose the folder the PCs write their reports to."); return; }
            if (!testMode) Settings.Set("FleetFolder", dir);
            fleetStatus.Text = Lang.T("Reading...");
            Task.Factory.StartNew(() => { int bad; var l = Fleet.ReadFolder(dir, out bad); return Tuple.Create(l, bad); }).ContinueWith(tk => UI(() =>
            {
                fleetList.BeginUpdate(); fleetList.Items.Clear();
                if (tk.IsFaulted) { fleetList.EndUpdate(); fleetStatus.Text = Lang.T("Could not read the folder: " + tk.Exception.GetBaseException().Message); return; }
                fleetRows = tk.Result.Item1;
                foreach (var s in fleetRows)
                {
                    var r = new ListViewItem(s.Computer) { UseItemStyleForSubItems = false, ToolTipText = s.Windows };
                    r.SubItems.Add(Fs.Fmt(s.Free));
                    var pc = r.SubItems.Add(s.FreePercent + "%"); pc.ForeColor = s.FreePercent < 10 ? Color.FromArgb(199, 54, 47) : s.FreePercent < 20 ? Color.FromArgb(190, 105, 0) : Color.FromArgb(0, 125, 70);
                    r.SubItems.Add(s.Saved > 0 ? Fs.Fmt(s.Saved) : "-");
                    r.SubItems.Add(Short(s.LastClean));
                    r.SubItems.Add(Lang.T(s.PolicyMode == "organization" ? "Organization" : s.PolicyMode == "custom" ? "Custom" : "Default") + (s.PolicyFingerprint.Length > 0 ? "  " + s.PolicyFingerprint.Substring(0, Math.Min(6, s.PolicyFingerprint.Length)) : ""));
                    r.SubItems.Add(s.TweaksOn.Length == 0 ? "-" : string.Join(", ", s.TweaksOn));
                    r.SubItems.Add(s.AppUpdates < 0 ? "-" : s.AppUpdates.ToString());
                    r.SubItems.Add(s.Elixar);
                    var rep = r.SubItems.Add(Short(s.Time));
                    DateTime w; if (DateTime.TryParse(s.Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out w) && (DateTime.Now - w).TotalDays > 7) rep.ForeColor = Color.FromArgb(190, 105, 0);
                    fleetList.Items.Add(r);
                }
                fleetList.EndUpdate();
                int low = fleetRows.Count(x => x.FreePercent < 10);
                fleetStatus.Text = Lang.T(fleetRows.Count + " PCs") + (low > 0 ? "  \u00B7  " + Lang.T(low + " under 10% free") : "") + (tk.Result.Item2 > 0 ? "  \u00B7  " + Lang.T(tk.Result.Item2 + " files skipped (not Elixar reports)") : "") +
                                   (fleetRows.Count > 0 ? "  \u00B7  " + Lang.T("saved " + Fs.Fmt(fleetRows.Sum(x => x.Saved)) + " in all") : "");
                if (FleetDone != null) FleetDone();
            }));
        }
        public event Action FleetDone;
        public int FleetRowCount { get { return fleetList.Items.Count; } }
        public string FleetRowText(int i) { return string.Join(" | ", fleetList.Items[i].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(x => x.Text)); }
        public void FleetSetFolder(string f) { fleetFolder.Text = f; }
        public void ShowFleet() { ShowPolicy(); polTabs.SelectedTab = polFleet; }

        static string Short(string iso)
        {
            DateTime d;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : (string.IsNullOrEmpty(iso) ? "-" : iso);
        }
    }
}
