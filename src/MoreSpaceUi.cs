// UI for More space: the planner window, the hibernation box and Tools > Move off C:.

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
        // ---------------- "Free space for me" ----------------
        void OnPlan()
        {
            var items = rows.Keys.ToList();
            using (var f = new Form { Text = "Free space for me", StartPosition = FormStartPosition.CenterParent, Font = Font, Icon = Icon,
                                      FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(12)) })
            {
                var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
                var top = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                top.Controls.Add(new Label { AutoSize = true, Text = "How much space do you want to free?", Margin = new Padding(0, S(6), S(8), 0) });
                var num = new NumericUpDown { Minimum = 1, Maximum = 2000, Value = 10, Width = S(70) };
                top.Controls.Add(num); top.Controls.Add(new Label { AutoSize = true, Text = "GB", Margin = new Padding(S(4), S(6), 0, 0) });
                var chkCaution = new CheckBox { AutoSize = true, Text = "Also use Caution items (Windows upgrade leftovers; most go to Undo)" };
                var chkBin = new CheckBox { AutoSize = true, Text = "Also empty the Recycle Bin" };
                var result = new Label { AutoSize = true, MaximumSize = new Size(S(560), 0), Margin = new Padding(0, S(10), 0, S(10)) };
                var row = new FlowLayoutPanel { AutoSize = true };
                var ok = new Button { Text = "Tick these", AutoSize = true, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
                row.Controls.Add(ok); row.Controls.Add(cancel);
                t.Controls.Add(top); t.Controls.Add(chkCaution); t.Controls.Add(chkBin); t.Controls.Add(result); t.Controls.Add(row);
                f.Controls.Add(t); f.AcceptButton = ok; f.CancelButton = cancel;
                Planner.Plan plan = null;
                Action update = () =>
                {
                    plan = Planner.Pick(items, (long)num.Value << 30, chkCaution.Checked, chkBin.Checked);
                    bool he = Lang.Hebrew;
                    result.Text = plan.Items.Count == 0 ? (he ? "אין מה לנקות כרגע. סרקו מחדש, או השתמשו באפליקציות ובכלים > העברה מ-C:." : "Nothing to clean right now. Rescan first, or use Apps and Tools > Move off C:.") :
                        (plan.Reached ? (he ? plan.Items.Count + " הפריטים האלה מפנים כ-" + Fs.Fmt(plan.Bytes) + ":" : "These " + plan.Items.Count + " items free about " + Fs.Fmt(plan.Bytes) + ":")
                                      : (he ? "Elixar יכול לפנות כך לכל היותר " + Fs.Fmt(plan.MaxPossible) + "; הכול מסומן:" : "Elixar can free at most " + Fs.Fmt(plan.MaxPossible) + " this way; it ticks all of it:")) +
                        "\n" + string.Join("\n", plan.Items.Select(i => "  \u2022  " + Lang.T(i.Name) + "  (" + Fs.Fmt(i.Size) + ", " + Lang.T(i.Tier.ToString()) + ")")) +
                        (plan.Reached ? "" : he ? "\n\nליותר מזה, הסירו אפליקציות שלא בשימוש (אפליקציות) או העבירו תיקיות ומשחקים (כלים > העברה מ-C:)." : "\n\nFor more, uninstall apps you don't use (Apps tab) or move folders and games (Tools > Move off C:).");
                    ok.Enabled = plan.Items.Count > 0;
                };
                num.ValueChanged += (o, e) => update(); chkCaution.CheckedChanged += (o, e) => update(); chkBin.CheckedChanged += (o, e) => update();
                update();
                Theme.Apply(f); Lang.Apply(f);
                if (f.ShowDialog(this) != DialogResult.OK || plan == null) return;
                foreach (var kv in rows) kv.Value.Checked = plan.Items.Contains(kv.Key);
                Log("Free space for me: ticked " + plan.Items.Count + " items for about " + Fs.Fmt(plan.Bytes) + ". Click \"Clean selected\" to clean them.");
            }
        }

        // ---------------- hibernation (in Tools > System space) ----------------
        Label lblHiber;

        GroupBox BuildHibernationBox()
        {
            var g = Box("Hibernation file", out lblHiber, "hiberfil.sys holds memory for Hibernate and Fast Startup; it is often 6-16 GB. \"Make smaller\" keeps Fast Startup (quick boot) but drops Hibernate. \"Turn off\" frees all of it.");
            Action<Action<string>> show = say => { long sz = Hibernation.FileSize(); string st = Hibernation.State(); string d = Hibernation.Describe(sz, st); say("  " + d); UI(() => lblHiber.Text = d); };
            SysButton(g, "Check", false, show);
            SysButton(g, "Make smaller", true, say => { long b = Sys.Free(); say("  " + Hibernation.Set("reduced")); say("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - b)) + "."); show(say); },
                "Make the hibernation file smaller?\n\nFast Startup keeps working; Hibernate (Start > Power > Hibernate) is no longer available. You can switch back here any time.");
            SysButton(g, "Turn off", true, say => { long b = Sys.Free(); say("  " + Hibernation.Set("off")); say("  Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - b)) + "."); show(say); },
                "Turn off hibernation?\n\nThis frees the whole file. Hibernate and Fast Startup are switched off (the PC may boot a few seconds slower). You can turn it back on here.");
            SysButton(g, "Turn back on (full)", true, say => { say("  " + Hibernation.Set("full")); show(say); });
            return g;
        }

        // ---------------- Tools > Move off C: ----------------
        TabPage pageMove;
        ListView moveList, gameList; ComboBox cmbMoveDrive; Label moveStatus; Button btnMoveGo, btnMoveBack, btnMoveRefresh; bool moveBusy, moveLoaded;
        public event Action MoveDone;

        void BuildMove()
        {
            var t = Page(7);
            for (int i = 0; i < 7; i++) t.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : i == 5 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 55 : 45));
            moveStatus = Note("Move your personal folders to another drive the way Windows does it (the same as Properties > Location): " +
                              "every file is copied and checked first, then Windows is pointed at the new place, then the old copies are deleted. Apps find the folders as before.");
            var row1 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            row1.Controls.Add(new Label { AutoSize = true, Text = "Move to drive:", Margin = new Padding(0, S(7), S(4), 0) });
            cmbMoveDrive = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(260) };
            row1.Controls.Add(cmbMoveDrive);
            moveList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill };
            moveList.Columns.Add("Folder", S(140)); moveList.Columns.Add("Size", S(90), HorizontalAlignment.Right); moveList.Columns.Add("Now in", S(420)); moveList.Columns.Add("Note", S(300));
            moveList.ItemChecked += (o, e) => UpdateMoveButtons();
            var row2 = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, S(6)) };
            btnMoveGo = MakeButton("Move selected"); btnMoveGo.Font = new Font("Segoe UI Semibold", 9f);
            btnMoveBack = MakeButton("Move back to C:");
            btnMoveRefresh = MakeButton("Check again");
            var btnStore = MakeButton("Store apps: move in Windows Settings");
            var btnNew = MakeButton("Save new files on another drive");
            row2.Controls.AddRange(new Control[] { btnMoveGo, btnMoveBack, btnMoveRefresh, btnStore, btnNew });
            gameList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
            gameList.Columns.Add("Steam games on C:", S(320)); gameList.Columns.Add("Size", S(90), HorizontalAlignment.Right); gameList.Columns.Add("Folder", S(520));
            var gameNote = Note("Steam moves games itself without downloading them again: Steam > Settings > Storage > pick the game > Move. Add a library on the other drive first (the + button).");
            t.Controls.Add(Title("Move off C:"), 0, 0); t.Controls.Add(moveStatus, 0, 1); t.Controls.Add(row1, 0, 2); t.Controls.Add(moveList, 0, 3);
            t.Controls.Add(row2, 0, 4); t.Controls.Add(gameList, 0, 5); t.Controls.Add(gameNote, 0, 6);
            pageMove.Controls.Add(t);
            btnMoveGo.Click += (o, e) => MoveFolders(false);
            btnMoveBack.Click += (o, e) => MoveFolders(true);
            btnMoveRefresh.Click += (o, e) => RefreshMove();
            btnStore.Click += (o, e) => { try { Shell.Open("ms-settings:appsfeatures"); } catch { } };
            btnNew.Click += (o, e) => { try { Shell.Open("ms-settings:savelocations"); } catch { } };
            UpdateMoveButtons();
        }

        void UpdateMoveButtons()
        {
            var sel = moveList.CheckedItems.Cast<ListViewItem>().Select(r => (UserFolder)r.Tag).ToList();
            bool can = !moveBusy && !testMode;
            btnMoveGo.Enabled = can && cmbMoveDrive.Items.Count > 0 && sel.Any(f => f.OnC && !f.OneDrive);
            btnMoveBack.Enabled = can && sel.Any(f => !f.OnC && !f.OneDrive);
            btnMoveRefresh.Enabled = !moveBusy;
        }

        public void RefreshMove()
        {
            if (moveBusy) return;
            moveBusy = true; moveLoaded = true; UpdateMoveButtons();
            moveStatus.Text = "Measuring your folders and Steam games...";
            Task.Factory.StartNew(() => Tuple.Create(Mover.ReadFolders(true), Mover.SteamGamesOn("C:", CachesSteam()), Mover.TargetDrives()), TaskCreationOptions.LongRunning)
                .ContinueWith(t => UI(() =>
                {
                    moveBusy = false;
                    if (t.IsFaulted) moveStatus.Text = "Could not read the folders: " + t.Exception.GetBaseException().Message;
                    else ShowMove(t.Result.Item1, t.Result.Item2, t.Result.Item3);
                    UpdateMoveButtons();
                    if (MoveDone != null) MoveDone();
                }));
        }

        static string CachesSteam()
        {
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) { var p = k == null ? null : k.GetValue("SteamPath") as string; if (!string.IsNullOrEmpty(p)) return p.Replace('/', '\\'); } } catch { }
            return @"C:\Program Files (x86)\Steam";
        }

        void ShowMove(List<UserFolder> folders, List<SteamGame> games, List<DriveInfo> drives)
        {
            cmbMoveDrive.Items.Clear();
            foreach (var d in drives) cmbMoveDrive.Items.Add(d.Name.TrimEnd('\\') + "   (" + Fs.Fmt(d.AvailableFreeSpace) + " free)");
            if (cmbMoveDrive.Items.Count > 0) cmbMoveDrive.SelectedIndex = 0;
            moveList.BeginUpdate(); moveList.Items.Clear();
            foreach (var f in folders.OrderByDescending(x => x.Bytes))
            {
                var r = new ListViewItem(f.Name) { Tag = f, UseItemStyleForSubItems = false };
                r.SubItems.Add(f.Bytes >= 0 ? Fs.Fmt(f.Bytes) : "");
                r.SubItems.Add(f.Path);
                r.SubItems.Add(f.OneDrive ? "kept in sync by OneDrive - move it in OneDrive's settings" : f.OnC ? "on C:" : "already on another drive").ForeColor = f.OneDrive ? CautionColor : f.OnC ? Color.DimGray : SafeColor;
                moveList.Items.Add(r);
            }
            moveList.EndUpdate();
            gameList.BeginUpdate(); gameList.Items.Clear();
            foreach (var g in games) { var r = new ListViewItem(g.Name); r.SubItems.Add(Fs.Fmt(g.Bytes)); r.SubItems.Add(g.Folder); gameList.Items.Add(r); }
            if (games.Count == 0) gameList.Items.Add(new ListViewItem("No Steam games on C:"));
            gameList.EndUpdate();
            long onC = folders.Where(f => f.OnC && !f.OneDrive).Sum(f => Math.Max(0, f.Bytes)) + games.Sum(g => g.Bytes);
            moveStatus.Text = drives.Count == 0
                ? "No second drive found. Moving needs another fixed drive (an internal disk or partition formatted NTFS)."
                : "Up to " + Fs.Fmt(onC) + " can move off C: (folders " + Fs.Fmt(folders.Where(f => f.OnC && !f.OneDrive).Sum(f => Math.Max(0, f.Bytes))) + ", Steam games " + Fs.Fmt(games.Sum(g => g.Bytes)) + "). " +
                  "Files are copied and checked before anything is deleted.";
        }

        void MoveFolders(bool back)
        {
            var sel = moveList.CheckedItems.Cast<ListViewItem>().Select(r => (UserFolder)r.Tag).Where(f => !f.OneDrive && (back ? !f.OnC : f.OnC)).ToList();
            if (sel.Count == 0 || testMode) return;
            string drive = back ? null : ((string)cmbMoveDrive.SelectedItem ?? "").Split(' ')[0] + "\\";
            var plan = sel.Select(f => Tuple.Create(f, back ? Mover.HomeFor(f.Name) : Mover.TargetFor(drive, f.Name))).ToList();
            string msg = (back ? "Move these folders back to C:?" : "Move these folders to " + drive + "?") + "\n\n" +
                         string.Join("\n", plan.Select(p => "  " + p.Item1.Name + " (" + Fs.Fmt(Math.Max(0, p.Item1.Bytes)) + ")  ->  " + p.Item2)) +
                         "\n\nClose programs that use these folders first. Every file is copied and checked before the old copy is deleted.";
            if (Lang.Box(this, msg, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            moveBusy = true; UpdateMoveButtons();
            Task.Factory.StartNew(() =>
            {
                var lines = new List<string>();
                foreach (var p in plan)
                {
                    var f = p.Item1;
                    UI(() => moveStatus.Text = "Moving " + f.Name + "...");
                    var r = Mover.Move(f.Path, p.Item2, to => Mover.SetPath(f.Id, to), Log);
                    ChangeLog.Add("Files", "Moved a personal folder: " + f.Name, f.Name, f.Path, r.Switched ? p.Item2 : "(not moved)",
                                  (r.Switched ? r.Copied + " files (" + Fs.Fmt(r.Bytes) + "). Windows now opens " + f.Name + " in the new place." : "") + (r.Problems.Count > 0 ? " Problems: " + string.Join("; ", r.Problems.Take(3)) : ""), !r.Switched);
                    lines.Add(f.Name + ": " + (r.Switched ? "moved " + r.Copied + " files (" + Fs.Fmt(r.Bytes) + ") to " + p.Item2 : "not moved") +
                              (r.Problems.Count > 0 ? "\n    " + string.Join("\n    ", r.Problems.Take(3)) : ""));
                    Log("Move: " + lines.Last().Replace("\n", " "));
                }
                return lines;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                moveBusy = false;
                var lines = t.IsFaulted ? new List<string> { t.Exception.GetBaseException().Message } : t.Result;
                Lang.Box(this, string.Join("\n\n", lines) + "\n\nSign out and back in once so every app picks up the new places.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshMove();
            }));
        }
    }
}
