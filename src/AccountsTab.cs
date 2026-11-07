// Tools > Old accounts: the page for OldAccounts (folders of accounts nobody signed in to for 6 months).

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
        TabPage pageAcct;
        ListView acctList; Label acctStatus, acctSel; Button btnAcctFind, btnAcctCopy, btnAcctRemove; bool acctBusy;
        public ProfileScan LastAccountScan;   // self-test
        public event Action AccountsFound;    // self-test

        void BuildAccounts()
        {
            pageAcct = new TabPage("Old accounts") { BackColor = pageTools.BackColor, AutoScroll = true };
            var t = Page(5);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            acctStatus = Note("On a PC several people used, accounts nobody has signed in to for months keep their whole folder: documents, downloads and app data. " +
                              "Never listed: you, anyone signed in now, Windows' own accounts, and any account used in the last 6 months. " +
                              "The account itself stays; only its files on this PC go, and that can't be undone.");
            btnAcctFind = MakeButton("Look for old accounts"); btnAcctFind.Font = new Font("Segoe UI Semibold", 9f);
            var row = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) });
            row.Controls.Add(btnAcctFind);
            acctList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            acctList.Columns.Add("Account", S(220)); acctList.Columns.Add("Last signed in", S(170)); acctList.Columns.Add("Size", S(90), HorizontalAlignment.Right); acctList.Columns.Add("Folder", S(230));
            btnAcctCopy = MakeButton("Copy to another drive first...");
            btnAcctRemove = MakeButton("Remove selected accounts' files..."); btnAcctRemove.Font = new Font("Segoe UI Semibold", 9f);
            acctSel = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(6), S(8), 0, 0) };
            var bottom = TextFit.WrapRow(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) });
            bottom.Controls.AddRange(new Control[] { btnAcctCopy, btnAcctRemove, acctSel });
            t.Controls.Add(Title("Old user accounts"), 0, 0); t.Controls.Add(acctStatus, 0, 1); t.Controls.Add(row, 0, 2);
            t.Controls.Add(acctList, 0, 3); t.Controls.Add(bottom, 0, 4);
            pageAcct.Controls.Add(t);
            pageAcct.AutoScrollMinSize = new Size(0, S(420));

            btnAcctFind.Click += (o, e) => FindAccounts();
            acctList.ItemChecked += (o, e) => AcctSel();
            btnAcctCopy.Click += (o, e) => CopyAccounts();
            btnAcctRemove.Click += (o, e) => RemoveAccounts();
            AcctSel();
        }

        public void FindAccounts()
        {
            if (acctBusy) return;
            acctBusy = true; btnAcctFind.Enabled = false; acctList.Items.Clear(); AcctSel();
            acctStatus.Text = Lang.T("Asking Windows which accounts have a folder on this PC, and measuring the old ones...");
            Task.Factory.StartNew(() => OldAccounts.Scan(DateTime.Now, true), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                acctBusy = false; btnAcctFind.Enabled = true;
                if (t.IsFaulted) { acctStatus.Text = Lang.T("The check stopped: ") + t.Exception.GetBaseException().Message; AcctSel(); return; }
                var s = t.Result; LastAccountScan = s;
                if (s.Problem != null) acctStatus.Text = Lang.T(s.Problem);
                else
                {
                    acctList.BeginUpdate();
                    foreach (var p in s.Old)
                    {
                        int months = Math.Max(Months(p.LastUse), OldAccounts.Months);
                        var lvi = new ListViewItem(p.Name) { Tag = p, Checked = false, ToolTipText = p.Sid };   // nothing ticked: it can't be undone
                        lvi.SubItems.Add(p.LastUse.ToString("yyyy-MM-dd") + (Lang.Hebrew ? " (" + months + " חודשים)" : " (" + months + " months)"));
                        lvi.SubItems.Add(Fs.Fmt(p.Bytes)); lvi.SubItems.Add(p.Folder);
                        acctList.Items.Add(lvi);
                    }
                    acctList.EndUpdate();
                    long total = s.Old.Sum(p => p.Bytes);
                    acctStatus.Text = s.Old.Count == 0
                        ? (Lang.Hebrew ? "אין חשבונות ישנים: כל " + s.All + " החשבונות במחשב שימשו ב-6 החודשים האחרונים." : "No old accounts: all " + s.All + " accounts on this PC were used in the last 6 months.")
                        : (Lang.Hebrew ? s.Old.Count + " חשבונות שאף אחד לא נכנס אליהם 6 חודשים או יותר: " + Fs.Fmt(total)
                                       : s.Old.Count + (s.Old.Count == 1 ? " account" : " accounts") + " nobody has signed in to for 6 months or more: " + Fs.Fmt(total)) +
                          (Sys.IsAdmin() ? "" : "  " + Lang.T("Elixar isn't running as administrator: sizes may be smaller than they are, and removing needs administrator."));
                }
                AcctSel();
                if (AccountsFound != null) AccountsFound();
            }));
        }

        static int Months(DateTime since) { var n = DateTime.Now; return (n.Year - since.Year) * 12 + n.Month - since.Month - (n.Day < since.Day ? 1 : 0); }

        List<OldProfile> AcctChecked() { return acctList.CheckedItems.Cast<ListViewItem>().Select(r => (OldProfile)r.Tag).ToList(); }

        void AcctSel()
        {
            var sel = AcctChecked();
            acctSel.Text = sel.Count == 0 ? "" : Lang.T(sel.Count + " selected (" + Fs.Fmt(sel.Sum(p => p.Bytes)) + ")");
            btnAcctCopy.Enabled = sel.Count > 0 && !acctBusy && !testMode;
            btnAcctRemove.Enabled = sel.Count > 0 && !acctBusy && !testMode && Policy.Allowed("profiles");
        }

        void CopyAccounts()
        {
            var sel = AcctChecked();
            if (sel.Count == 0 || testMode) return;
            string dest;
            using (var d = new FolderBrowserDialog { Description = Lang.T("Choose another drive (or a folder on it) for the copies. Each account gets its own folder."), ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                dest = d.SelectedPath;
            }
            if (OldAccounts.SameDrive(dest, sel[0].Folder) != null)
            {
                Lang.Box(this, Lang.T("Choose another drive: a copy on the same drive frees nothing and is lost with it."), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            long need = sel.Sum(p => p.Bytes), free = 0;
            try { free = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(dest)).AvailableFreeSpace; } catch { }
            if (free > 0 && free < need + (need / 20))
            {
                Lang.Box(this, (Lang.Hebrew ? "אין מספיק מקום: צריך " + Fs.Fmt(need) + ", פנוי " + Fs.Fmt(free) + "." : "Not enough room: " + Fs.Fmt(need) + " needed, " + Fs.Fmt(free) + " free."), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            acctBusy = true; AcctSel(); btnAcctFind.Enabled = false;
            acctStatus.Text = Lang.T("Copying... This can take a while for large folders.");
            Task.Factory.StartNew(() => sel.Select(p => { var e = OldAccounts.CopyTo(p, dest); Log("Old accounts: copy of " + p.Name + (e == null ? " done." : ": " + e)); return e; }).Where(e => e != null).ToList(),
                                  TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                acctBusy = false; btnAcctFind.Enabled = true;
                var errs = t.IsFaulted ? new List<string> { t.Exception.GetBaseException().Message } : t.Result;
                acctStatus.Text = errs.Count == 0 ? (Lang.Hebrew ? "ההעתקה הסתיימה: " : "Copied to: ") + dest : string.Join("  ", errs);
                AcctSel();
            }));
        }

        void RemoveAccounts()
        {
            var sel = AcctChecked();
            if (sel.Count == 0 || testMode || Policy.Blocked(this, "profiles")) return;
            if (!Sys.IsAdmin())
            {
                Lang.Box(this, Lang.T("Removing an account's files needs Elixar to run as administrator."), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            long bytes = sel.Sum(p => p.Bytes);
            string names = string.Join("\n", sel.Select(p => "  " + p.Name + "  (" + Fs.Fmt(p.Bytes) + ")"));
            if (Lang.Box(this, (Lang.Hebrew ? "להסיר את הקבצים של " + sel.Count + " חשבונות (" + Fs.Fmt(bytes) + ")?" : "Remove the files of " + sel.Count + (sel.Count == 1 ? " account" : " accounts") + " (" + Fs.Fmt(bytes) + ")?") + "\n\n" + names + "\n\n" +
                    Lang.T("Their documents, downloads, desktop and app data on this PC are removed by Windows' own profile removal. This can't be undone and they don't go to Undo. The accounts themselves stay: signing in again starts with a fresh, empty folder.") + "\n\n" +
                    Lang.T("To keep a copy, press Cancel and use Copy to another drive first."),
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            if (Lang.BoxNo(this, "Are you sure? This is the last question: the files can't be brought back.", "Elixar") != DialogResult.Yes) return;
            acctBusy = true; AcctSel(); btnAcctFind.Enabled = false;
            acctStatus.Text = Lang.T("Windows is removing the accounts' files...");
            Task.Factory.StartNew(() =>
            {
                long freed = 0; var errs = new List<string>();
                foreach (var p in sel) { var e = OldAccounts.Remove(p, Log); if (e == null) freed += p.Bytes; else errs.Add(p.Name + ": " + e); }
                return Tuple.Create(freed, errs);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                acctBusy = false; btnAcctFind.Enabled = true;
                if (t.IsFaulted) { acctStatus.Text = t.Exception.GetBaseException().Message; AcctSel(); return; }
                long freed = t.Result.Item1; var errs = t.Result.Item2;
                foreach (ListViewItem r in acctList.CheckedItems.Cast<ListViewItem>().ToList()) if (!errs.Any(x => x.StartsWith(((OldProfile)r.Tag).Name + ": "))) acctList.Items.Remove(r);
                acctStatus.Text = (Lang.Hebrew ? "פונו " + Fs.Fmt(freed) + "." : "Freed " + Fs.Fmt(freed) + ".") + (errs.Count > 0 ? "  " + string.Join("  ", errs) : "");
                if (freed > 0) Activity.Add("clean", "Old user accounts' files removed", freed);
                AcctSel();
            }));
        }
    }
}
