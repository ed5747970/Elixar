// Folder drill-down: what is inside a folder, biggest first, one level at a time. Read-only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class DrillEntry
    {
        public string Name, Path;
        public bool IsFolder, IsLink, IsGroup;   // IsGroup = "N smaller files" summary row
        public long Bytes = -1;                  // -1 = still measuring
        public int Count;
        public DateTime Modified;
    }

    public static class DrillData
    {
        public const long FileRowMin = 10L << 20; // files of 10 MB or more get their own row

        // Lists what is directly inside a folder. Folders get Bytes = -1 until measured.
        public static List<DrillEntry> List(string dir)
        {
            var list = new List<DrillEntry>();
            var di = new DirectoryInfo(dir);
            long small = 0; int smallCount = 0;
            try
            {
                foreach (var d in di.EnumerateDirectories())
                {
                    bool link = (d.Attributes & FileAttributes.ReparsePoint) != 0;
                    list.Add(new DrillEntry { Name = d.Name, Path = d.FullName, IsFolder = true, IsLink = link, Bytes = link ? 0 : -1, Modified = SafeTime(d) });
                }
            }
            catch { }
            try
            {
                foreach (var f in di.EnumerateFiles())
                {
                    long len = 0;
                    try { len = f.Length; } catch { }
                    if (len >= FileRowMin) list.Add(new DrillEntry { Name = f.Name, Path = f.FullName, Bytes = len, Modified = SafeTime(f) });
                    else { small += len; smallCount++; }
                }
            }
            catch { }
            if (smallCount > 0) list.Add(new DrillEntry { Name = smallCount + " smaller files", Path = dir, IsGroup = true, Bytes = small, Count = smallCount });
            return list;
        }

        static DateTime SafeTime(FileSystemInfo i) { try { return i.LastWriteTime; } catch { return DateTime.MinValue; } }

        // Size of a folder that stops early when cancelled.
        public static long SizeOf(string path, CancellationToken ct)
        {
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) return total;
                string dir = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var f in di.EnumerateFiles()) { try { total += f.Length; } catch { } }
                    foreach (var d in di.EnumerateDirectories())
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !Lx.SkipDir(d.FullName, path)) stack.Push(d.FullName);
                }
                catch { }
            }
            return total;
        }

        public static string TierOf(DrillEntry e)
        {
            string t, r;
            Scanner.Classify(e.IsFolder || e.IsGroup ? e.Path.TrimEnd('\\') + "\\x" : e.Path, out t, out r);
            return t;
        }
    }

    public class DrillForm : Form
    {
        readonly Func<int, int> S;
        readonly Label crumb, status;
        readonly ListView list;
        readonly Button btnUp, btnOpen, btnExplorer, btnUninstall;
        readonly Func<string, string> ownerName;
        readonly Action<string> uninstall;
        CancellationTokenSource cts;
        string current;
        public event Action Measured;   // fired when a folder finished measuring (used by the self-test)

        static readonly Color CriticalColor = Color.FromArgb(199, 54, 47);
        static readonly Color CautionColor = Color.FromArgb(190, 105, 0);
        static readonly Color ReviewColor = Color.FromArgb(40, 90, 200);
        static readonly Color SafeColor = Color.FromArgb(0, 125, 70);

        public DrillForm(string startPath, float scale, Icon icon, Func<string, string> ownerName = null, Action<string> uninstall = null)
        {
            this.ownerName = ownerName;
            this.uninstall = uninstall;
            S = px => (int)Math.Round(px * scale);
            Text = "Inside a folder - Elixar";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterParent;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(S(980), wa.Width), Math.Min(S(680), wa.Height));
            if (icon != null) Icon = icon;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(S(12), S(10), S(12), S(10)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            crumb = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), Margin = new Padding(0, 0, 0, S(2)) };
            status = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(1), 0, 0, S(8)) };

            list = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, ShowItemToolTips = true, MultiSelect = false };
            list.Columns.Add("Name", S(330));
            list.Columns.Add("Size", S(90), HorizontalAlignment.Right);
            list.Columns.Add("Share", S(170));
            list.Columns.Add("Level", S(80));
            list.Columns.Add("Modified", S(95));
            list.Columns.Add("Type", S(110));

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, S(8), 0, 0) };
            btnUp = MkButton("<  Up one level");
            btnOpen = MkButton("Open selected folder");
            btnExplorer = MkButton("Show in Explorer");
            btnUninstall = MkButton("Uninstall app...");
            btnUninstall.Enabled = false;
            buttons.Controls.AddRange(new Control[] { btnUp, btnOpen, btnExplorer, btnUninstall,
                new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(10), S(8), 0, 0), Text = "Read-only: nothing is deleted from here." } });

            root.Controls.Add(crumb, 0, 0);
            root.Controls.Add(status, 0, 1);
            root.Controls.Add(list, 0, 2);
            root.Controls.Add(buttons, 0, 3);
            Controls.Add(root);

            list.DoubleClick += (o, e) => OpenSelected();
            list.SelectedIndexChanged += (o, e) => UpdateUninstall();
            btnUninstall.Click += (o, e) =>
            {
                string p = OwnerPath();
                if (p != null && uninstall != null) uninstall(p);
            };
            list.KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.Back) { GoUp(); e.Handled = true; }
            };
            btnUp.Click += (o, e) => GoUp();
            btnOpen.Click += (o, e) => OpenSelected();
            btnExplorer.Click += (o, e) =>
            {
                var sel = list.SelectedItems.Count > 0 ? (DrillEntry)list.SelectedItems[0].Tag : null;
                try
                {
                    if (sel != null && !sel.IsGroup) Shell.Select(sel.Path);
                    else Shell.Open(current);
                }
                catch { }
            };
            FormClosed += (o, e) => { if (cts != null) cts.Cancel(); };
            Shown += (o, e) => Navigate(startPath, false);
        }

        Button MkButton(string text)
        {
            return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(8), S(3), S(8), S(3)), Margin = new Padding(S(2), S(2), S(6), S(2)) };
        }

        // the selected row, or the folder we are in, if it belongs to an installed app
        string OwnerPath()
        {
            if (ownerName == null) return null;
            if (list.SelectedItems.Count > 0)
            {
                var e = (DrillEntry)list.SelectedItems[0].Tag;
                if (!e.IsGroup && ownerName(e.Path) != null) return e.Path;
            }
            return current != null && ownerName(current) != null ? current : null;
        }

        void UpdateUninstall()
        {
            string p = OwnerPath();
            string name = p == null ? null : ownerName(p);
            btnUninstall.Enabled = name != null && uninstall != null;
            btnUninstall.Text = name != null ? "Uninstall " + (name.Length > 32 ? name.Substring(0, 30) + "..." : name) + "..." : "Uninstall app...";
        }

        void OpenSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var e = (DrillEntry)list.SelectedItems[0].Tag;
            if (e.IsFolder && !e.IsLink) Navigate(e.Path, true);
        }

        void GoUp()
        {
            if (current == null || current.EndsWith(":\\") || current == "/") return;
            string parent = null;
            try { var p = Directory.GetParent(current.TrimEnd('\\', '/')); parent = p == null ? null : p.FullName; } catch { }
            if (parent != null) Navigate(parent, true);
        }

        public string CurrentPath { get { return current; } }
        public int RowCount { get { return list.Items.Count; } }
        public bool AllMeasured { get { return list.Items.Cast<ListViewItem>().All(r => ((DrillEntry)r.Tag).Bytes >= 0); } }

        void Navigate(string path, bool remember)
        {
            if (cts != null) cts.Cancel();
            cts = new CancellationTokenSource();
            var token = cts.Token;
            current = path;
            crumb.Text = path;
            btnUp.Enabled = !(path.EndsWith(":\\") || path == "/");
            status.Text = "Reading...";
            list.Items.Clear();
            UpdateUninstall();

            var watch = Stopwatch.StartNew();
            Task.Factory.StartNew(() => DrillData.List(path), token).ContinueWith(t =>
            {
                if (token.IsCancellationRequested || t.IsFaulted) return;
                var entries = t.Result;
                Ui(() =>
                {
                    list.BeginUpdate();
                    foreach (var e in entries)
                    {
                        string tier = DrillData.TierOf(e);
                        var lvi = new ListViewItem(e.Name) { Tag = e, UseItemStyleForSubItems = false };
                        if (e.IsFolder) lvi.Font = new Font(list.Font, FontStyle.Bold);
                        lvi.SubItems.Add(e.Bytes >= 0 ? Fs.Fmt(e.Bytes) : "...");
                        var bar = lvi.SubItems.Add("");
                        bar.Font = new Font("Consolas", 8f);
                        bar.ForeColor = TierColor(tier);
                        var lv = lvi.SubItems.Add(char.ToUpperInvariant(tier[0]) + tier.Substring(1));
                        lv.ForeColor = TierColor(tier);
                        lvi.SubItems.Add(e.Modified > DateTime.MinValue && !e.IsGroup ? e.Modified.ToString("yyyy-MM-dd") : "");
                        lvi.SubItems.Add(e.IsGroup ? "files" : e.IsLink ? "link (not followed)" : e.IsFolder ? "folder" : SafeExt(e.Name));
                        lvi.ToolTipText = e.IsGroup ? e.Count + " files under 10 MB each" : e.Path;
                        list.Items.Add(lvi);
                    }
                    list.EndUpdate();
                    status.Text = entries.Count(x => x.IsFolder) + " folders. Measuring...";
                });

                // measure folders one by one, biggest picture first
                foreach (var e in entries.Where(x => x.IsFolder && x.Bytes < 0))
                {
                    if (token.IsCancellationRequested) return;
                    var entry = e;
                    Ui(() => { if (!token.IsCancellationRequested) status.Text = "Measuring " + entry.Name + "..."; });
                    entry.Bytes = DrillData.SizeOf(entry.Path, token);
                    if (token.IsCancellationRequested) return;
                    Ui(() => UpdateRows(token, false));
                }
                Ui(() =>
                {
                    if (token.IsCancellationRequested) return;
                    UpdateRows(token, true);
                    long total = entries.Sum(x => Math.Max(0, x.Bytes));
                    status.Text = Fs.Fmt(total) + " in this folder, measured in " + Math.Max(1, (int)Math.Round(watch.Elapsed.TotalSeconds)) + " s.  " +
                                  "Double-click a folder to go inside it, Backspace to go up.";
                    if (Measured != null) Measured();
                });
            }, TaskContinuationOptions.LongRunning);
        }

        void UpdateRows(CancellationToken token, bool sort)
        {
            if (token.IsCancellationRequested) return;
            var rows = list.Items.Cast<ListViewItem>().ToList();
            long max = Math.Max(1, rows.Select(r => ((DrillEntry)r.Tag).Bytes).DefaultIfEmpty(1).Max());
            long whole = Math.Max(1, rows.Sum(r => Math.Max(0, ((DrillEntry)r.Tag).Bytes)));
            list.BeginUpdate();
            foreach (var r in rows)
            {
                var e = (DrillEntry)r.Tag;
                if (e.Bytes < 0) continue;
                r.SubItems[1].Text = Fs.Fmt(e.Bytes);
                r.SubItems[2].Text = DashData.Bar(e.Bytes, max, whole, 14);
            }
            if (sort)
            {
                var sorted = rows.OrderByDescending(r => ((DrillEntry)r.Tag).Bytes).ToList();
                list.Items.Clear();
                list.Items.AddRange(sorted.ToArray());
            }
            list.EndUpdate();
        }

        static string SafeExt(string name)
        {
            try { var x = Path.GetExtension(name); return string.IsNullOrEmpty(x) ? "file" : x.TrimStart('.').ToLowerInvariant() + " file"; }
            catch { return "file"; }
        }

        static Color TierColor(string t)
        {
            switch (t)
            {
                case "critical": return CriticalColor;
                case "caution": return CautionColor;
                case "review": return ReviewColor;
                default: return SafeColor;
            }
        }

        void Ui(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(a); } catch { }
        }
    }
}
