// Tools > Leftovers: folders left behind by apps you removed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pageLeft;
        ListView leftList; Label leftStatus, leftSel; Button btnLeftFind, btnLeftMove;
        CancellationTokenSource leftCts;
        public int LeftoverRows { get { return leftList == null ? 0 : leftList.Items.Count; } }

        void BuildLeftovers()
        {
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            leftStatus = Note("Folders in AppData and ProgramData that no installed app, service or running program belongs to, and that nothing has changed for 3 months. These are guesses: look before you pick. Nothing is ticked for you, and what you pick waits in Undo for 7 days (Tools > Undo puts it back).");
            btnLeftFind = MakeButton("Find leftovers"); btnLeftFind.Font = new Font("Segoe UI Semibold", 9f);
            var bAll = MakeButton("Select all"); var bNone = MakeButton("Select none");
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            row.Controls.AddRange(new Control[] { btnLeftFind, bAll, bNone });
            leftList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            leftList.Columns.Add("Folder", S(260)); leftList.Columns.Add("In", S(130)); leftList.Columns.Add("Size", S(90), HorizontalAlignment.Right);
            leftList.Columns.Add("Last changed", S(110)); leftList.Columns.Add("Location", S(480));
            btnLeftMove = MakeButton("Move selected to Undo..."); btnLeftMove.Font = new Font("Segoe UI Semibold", 9f);
            leftSel = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(6), S(8), 0, 0) };
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            bottom.Controls.AddRange(new Control[] { btnLeftMove, leftSel });
            t.Controls.Add(Title("Leftovers of removed apps"), 0, 0); t.Controls.Add(leftStatus, 0, 1); t.Controls.Add(row, 0, 2);
            t.Controls.Add(leftList, 0, 3); t.Controls.Add(bottom, 0, 4);
            pageLeft.Controls.Add(t);

            btnLeftFind.Click += (o, e) => FindLeftovers();
            bAll.Click += (o, e) => { foreach (ListViewItem r in leftList.Items) r.Checked = true; };
            bNone.Click += (o, e) => { foreach (ListViewItem r in leftList.Items) r.Checked = false; };
            leftList.ItemChecked += (o, e) => LeftSel();
            leftList.DoubleClick += (o, e) => { if (leftList.SelectedItems.Count > 0) try { Shell.Open(((Leftover)leftList.SelectedItems[0].Tag).Path); } catch { } };
            btnLeftMove.Click += (o, e) => MoveLeftovers();
            LeftSel();
        }

        public void FindLeftovers()
        {
            if (leftCts != null) return;
            leftCts = new CancellationTokenSource(); var tok = leftCts.Token;
            btnLeftFind.Enabled = false; leftList.Items.Clear(); LeftSel();
            leftStatus.Text = Lang.T("Reading the installed apps, services and running programs...");
            string last = "";
            Task.Factory.StartNew(() => Leftovers.Find(Leftovers.Roots(), Leftovers.InUseNames(), s => { if (s != last) { last = s; UI(() => leftStatus.Text = Lang.T(s.Length > 110 ? s.Substring(0, 107) + "..." : s)); } }, tok),
                TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                leftCts = null; btnLeftFind.Enabled = true;
                if (t.IsFaulted) { leftStatus.Text = Lang.T("The search stopped: ") + t.Exception.GetBaseException().Message; return; }
                leftList.BeginUpdate();
                foreach (var l in t.Result)
                {
                    var lvi = new ListViewItem(l.Name) { Tag = l, ToolTipText = l.Path };
                    lvi.SubItems.Add(l.Where); lvi.SubItems.Add(Fs.Fmt(l.Bytes)); lvi.SubItems.Add(l.LastChange.ToString("yyyy-MM-dd")); lvi.SubItems.Add(l.Path);
                    leftList.Items.Add(lvi);
                }
                leftList.EndUpdate();
                leftStatus.Text = Lang.T(t.Result.Count == 0 ? "No leftovers found." : t.Result.Count + " folders look left behind: " + Fs.Fmt(t.Result.Sum(x => x.Bytes)) + ". Look before you pick; double-click opens a folder.");
                LeftSel();
            }));
        }

        void LeftSel()
        {
            var sel = leftList.CheckedItems.Cast<ListViewItem>().ToList();
            leftSel.Text = sel.Count == 0 ? "" : Lang.T(sel.Count + " selected (" + Fs.Fmt(sel.Sum(r => ((Leftover)r.Tag).Bytes)) + ")");
            btnLeftMove.Enabled = sel.Count > 0 && leftCts == null && !testMode;
        }

        void MoveLeftovers()
        {
            var sel = leftList.CheckedItems.Cast<ListViewItem>().ToList();
            if (sel.Count == 0 || testMode) return;
            long bytes = sel.Sum(r => ((Leftover)r.Tag).Bytes);
            if (Lang.Box(this, Lang.T("Move " + sel.Count + " folders (" + Fs.Fmt(bytes) + ") to Undo?") + "\n\n" +
                    Lang.T("They wait in Undo for 7 days: if an app misses something, Tools > Undo puts it back. After 7 days they are deleted."),
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            var paths = sel.Select(r => ((Leftover)r.Tag).Path).ToList();
            Cursor = Cursors.WaitCursor;
            try { Quarantine.MoveToUndo("Leftovers of removed apps (" + paths.Count + ")", paths, Log); }
            catch (Exception ex) { Log("Leftovers: " + ex.Message); }
            Cursor = Cursors.Default;
            int moved = paths.Count(p => !System.IO.Directory.Exists(p));
            foreach (var r in sel) if (!System.IO.Directory.Exists(((Leftover)r.Tag).Path)) leftList.Items.Remove(r);
            string msg = "Moved " + moved + " of " + paths.Count + " folders to Undo (kept 7 days).";
            leftStatus.Text = Lang.T(msg); Log(Lang.T("Leftovers: " + msg));
            LeftSel();
        }
    }
}
