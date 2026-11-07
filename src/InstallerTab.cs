// Tools > Leftover installers: the page for InstallerFiles (old installer copies in C:\Windows\Installer).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pageInst;
        ListView instList; Label instStatus, instSel; Button btnInstFind, btnInstMove; bool instBusy;
        public InstallerScan LastInstallerScan;   // self-test

        void BuildInstallers()
        {
            pageInst = new TabPage("Leftover installers") { BackColor = pageTools.BackColor, AutoScroll = true };
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            instStatus = Note("Windows keeps a copy of every program's installer in C:\\Windows\\Installer, so it can repair or remove the program later. " +
                              "When programs are removed or updated, old copies often stay. Elixar asks Windows itself which copies are still used and offers only the others. " +
                              "Nothing used by a program or update, nothing changed in the last 30 days, and they go to Undo, not deleted.");
            btnInstFind = MakeButton("Look for leftover installers"); btnInstFind.Font = new Font("Segoe UI Semibold", 9f);
            var bAll = MakeButton("Select all"); var bNone = MakeButton("Select none"); var bOpen = MakeButton("Open the folder");
            var row = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) });
            row.Controls.AddRange(new Control[] { btnInstFind, bAll, bNone, bOpen });
            instList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            instList.Columns.Add("File", S(150)); instList.Columns.Add("From", S(360)); instList.Columns.Add("Size", S(90), HorizontalAlignment.Right); instList.Columns.Add("Last changed", S(110));
            btnInstMove = MakeButton("Move selected to Undo..."); btnInstMove.Font = new Font("Segoe UI Semibold", 9f);
            instSel = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(6), S(8), 0, 0) };
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            bottom.Controls.AddRange(new Control[] { btnInstMove, instSel });
            t.Controls.Add(Title("Leftover installer files"), 0, 0); t.Controls.Add(instStatus, 0, 1); t.Controls.Add(row, 0, 2);
            t.Controls.Add(instList, 0, 3); t.Controls.Add(bottom, 0, 4);
            pageInst.Controls.Add(t);
            pageInst.AutoScrollMinSize = new Size(0, S(420));

            btnInstFind.Click += (o, e) => FindInstallers();
            bAll.Click += (o, e) => { foreach (ListViewItem r in instList.Items) r.Checked = true; };
            bNone.Click += (o, e) => { foreach (ListViewItem r in instList.Items) r.Checked = false; };
            bOpen.Click += (o, e) => { if (!testMode) try { Shell.Open(InstallerFiles.Folder); } catch { } };
            instList.ItemChecked += (o, e) => InstSel();
            btnInstMove.Click += (o, e) => MoveInstallers();
            InstSel();
        }

        public event Action InstallersFound;   // self-test

        public void FindInstallers()
        {
            if (instBusy) return;
            instBusy = true; btnInstFind.Enabled = false; instList.Items.Clear(); InstSel();
            instStatus.Text = Lang.T("Asking Windows Installer which installer files the programs and updates on this PC still use...");
            Task.Factory.StartNew(() =>
            {
                var s = InstallerFiles.Scan(DateTime.Now);
                foreach (var f in s.Orphans) try { f.From = InstallerFiles.WhatItIs(f.Path); } catch { }
                return s;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                instBusy = false; btnInstFind.Enabled = true;
                if (t.IsFaulted) { instStatus.Text = Lang.T("The check stopped: ") + t.Exception.GetBaseException().Message; return; }
                var s = t.Result; LastInstallerScan = s;
                if (s.Problem != null) instStatus.Text = Lang.T(s.Problem);
                else
                {
                    instList.BeginUpdate();
                    foreach (var f in s.Orphans)
                    {
                        var lvi = new ListViewItem(System.IO.Path.GetFileName(f.Path)) { Tag = f, Checked = true, ToolTipText = f.Path };
                        lvi.SubItems.Add(f.From.Length > 0 ? f.From : Lang.T("unknown")); lvi.SubItems.Add(Fs.Fmt(f.Bytes)); lvi.SubItems.Add(f.Changed.ToString("yyyy-MM-dd"));
                        instList.Items.Add(lvi);
                    }
                    instList.EndUpdate();
                    string checkedWith = Lang.Hebrew ? " (נבדק מול הרשימה של Windows: " + s.Products + " תוכניות ו-" + s.Patches + " עדכונים)"
                                                     : " (checked against Windows' own list of " + s.Products + " programs and " + s.Patches + " updates)";
                    instStatus.Text = (s.Orphans.Count == 0 ? (Lang.Hebrew ? "אין קבצי התקנה שנשארו. כל הקבצים בתיקייה בשימוש או חדשים." : "No leftover installer files: every file in the folder is used or new.")
                                     : (Lang.Hebrew ? Fs.Fmt(s.Bytes) + " ב-" + s.Orphans.Count + " קבצים ששום תוכנית מותקנת לא משתמשת בהם" : Fs.Fmt(s.Bytes) + " in " + s.Orphans.Count + " files that no installed program uses")) + checkedWith;
                }
                InstSel();
                if (InstallersFound != null) InstallersFound();
            }));
        }

        void InstSel()
        {
            var sel = instList.CheckedItems.Cast<ListViewItem>().ToList();
            instSel.Text = sel.Count == 0 ? "" : Lang.T(sel.Count + " selected (" + Fs.Fmt(sel.Sum(r => ((InstallerFile)r.Tag).Bytes)) + ")");
            btnInstMove.Enabled = sel.Count > 0 && !instBusy && !testMode;
        }

        void MoveInstallers()
        {
            var sel = instList.CheckedItems.Cast<ListViewItem>().Select(r => (InstallerFile)r.Tag).ToList();
            if (sel.Count == 0 || testMode || Policy.Blocked(this, "installers")) return;
            long bytes = sel.Sum(f => f.Bytes);
            if (Lang.Box(this, (Lang.Hebrew ? "להעביר " + sel.Count + " קבצי התקנה (" + Fs.Fmt(bytes) + ") לביטול?" : "Move " + sel.Count + " installer files (" + Fs.Fmt(bytes) + ") to Undo?") + "\n\n" +
                    Lang.T("Elixar checks them once more right before moving. They wait in Undo: if a program ever asks for its installer, Tools > Undo puts it back."),
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cursor = Cursors.WaitCursor;
            long moved = 0;
            try { moved = InstallerFiles.MoveToUndo(sel, Log); }
            catch (Exception ex) { Log("Leftover installers: " + ex.Message); }
            Cursor = Cursors.Default;
            foreach (ListViewItem r in instList.CheckedItems.Cast<ListViewItem>().ToList()) if (!System.IO.File.Exists(((InstallerFile)r.Tag).Path)) instList.Items.Remove(r);
            string msg = Lang.Hebrew ? "הועברו " + Fs.Fmt(moved) + " לביטול." : "Moved " + Fs.Fmt(moved) + " to Undo.";
            instStatus.Text = msg; Log("Leftover installers: " + msg);
            if (moved > 0) Activity.Add("clean", "Leftover installer files moved to Undo", moved);
            InstSel();
        }
    }
}
