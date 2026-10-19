// The OneDrive tab (shown only when OneDrive is signed in on this PC): make files you don't use online-only.

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
        TabPage pageOneDrive;
        List<OneDriveAccount> odAccounts = new List<OneDriveAccount>();
        ComboBox odAccount, odAge;
        Label odInfo, odSummary, odSel;
        ListView odList;
        Button odScan, odAll, odNone, odFree;
        CancellationTokenSource odCts;
        OneDriveScan odResult;
        public bool OneDriveShown { get { return pageOneDrive != null && tabs.TabPages.Contains(pageOneDrive); } }   // self-test
        public int OneDriveRows { get { return odList == null ? 0 : odList.Items.Count; } }

        void BuildOneDriveTab()
        {
            odAccounts = LinuxUi ? new List<OneDriveAccount>() : OneDrive.Accounts();
            pageOneDrive = new TabPage("OneDrive") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(6);
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            odInfo = Note("");
            odAccount = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(420), Visible = odAccounts.Count > 1 };
            foreach (var a in odAccounts) odAccount.Items.Add(Redact.Emails(a.Email) + "  (" + Lang.T(a.Kind) + ")");
            if (odAccounts.Count > 0) odAccount.SelectedIndex = 0;
            odAge = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(220) };
            odAge.Items.AddRange(new object[] { Lang.T("Not opened for 1 month"), Lang.T("Not opened for 3 months"), Lang.T("Not opened for 6 months"), Lang.T("Not opened for a year") });
            odAge.SelectedIndex = 1;
            odScan = MakeButton("Look again");
            odSummary = new Label { AutoSize = true, MaximumSize = new Size(S(1240), 0), Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(S(1), S(6), 0, S(6)) };
            odList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            odList.Columns.Add("File", S(320)); odList.Columns.Add("Size", S(90), HorizontalAlignment.Right); odList.Columns.Add("Last used", S(110)); odList.Columns.Add("Folder", S(460));
            odAll = MakeButton("Select all shown"); odNone = MakeButton("Select none");
            odFree = MakeButton("Make selected online-only..."); odFree.Font = new Font("Segoe UI Semibold", 9f);
            odSel = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(6), S(8), 0, 0) };
            var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            top.Controls.AddRange(new Control[] { odAccount, odAge, odScan, odAll, odNone });
            var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            bottom.Controls.AddRange(new Control[] { odFree, odSel });

            t.Controls.Add(Title("OneDrive: free up space"), 0, 0);
            t.Controls.Add(Note("Files you haven't used for a while can be online-only: they stay safe in OneDrive and still show in File Explorer, but take no space on this PC until you open one (then OneDrive downloads it, which needs internet). Nothing is deleted. Files you marked \"Always keep on this device\" are left alone. Nothing is ticked for you."), 0, 1);
            t.Controls.Add(odInfo, 0, 2);
            var mid = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0) };
            mid.Controls.Add(top); mid.Controls.Add(odSummary);
            t.Controls.Add(mid, 0, 3);
            t.Controls.Add(odList, 0, 4);
            t.Controls.Add(bottom, 0, 5);
            pageOneDrive.Controls.Add(t);

            odScan.Click += (o, e) => OneDriveLook();
            odAccount.SelectedIndexChanged += (o, e) => OneDriveLook();
            odAge.SelectedIndexChanged += (o, e) => OneDriveFill();
            odAll.Click += (o, e) => { foreach (ListViewItem r in odList.Items) r.Checked = true; };
            odNone.Click += (o, e) => { foreach (ListViewItem r in odList.Items) r.Checked = false; };
            odList.ItemChecked += (o, e) => OneDriveSel();
            odList.DoubleClick += (o, e) => { if (odList.SelectedItems.Count > 0) try { Shell.Select(((OneDriveFile)odList.SelectedItems[0].Tag).Path); } catch { } };
            odFree.Click += (o, e) => OneDriveFree();
            OneDriveSel();
        }

        OneDriveAccount CurOneDrive { get { return odAccounts.Count == 0 ? null : odAccounts[Math.Max(0, odAccount.SelectedIndex)]; } }
        DateTime OdCutoff() { return odAge.SelectedIndex == 0 ? DateTime.Now.AddMonths(-1) : odAge.SelectedIndex == 2 ? DateTime.Now.AddMonths(-6) : odAge.SelectedIndex == 3 ? DateTime.Now.AddYears(-1) : DateTime.Now.AddMonths(-3); }

        bool odLooked;
        void OnOneDriveTab() { if (!odLooked) { odLooked = true; OneDriveLook(); } }

        public void OneDriveLook()
        {
            var a = CurOneDrive;
            if (a == null || odCts != null) return;
            odCts = new CancellationTokenSource(); var tok = odCts.Token;
            odInfo.Text = Lang.T("Signed in to OneDrive as " + Redact.Emails(a.Email) + " (" + a.Kind + ")  ·  " + a.Folder);
            odSummary.Text = Lang.T("Reading your OneDrive folder (nothing is downloaded)...");
            odList.Items.Clear(); odScan.Enabled = false; OneDriveSel();
            string last = "";
            var cutoff = OdCutoff();
            Task.Factory.StartNew(() => OneDrive.Scan(a, cutoff, s => { if (s != last) { last = s; UI(() => odSummary.Text = Lang.T(s.Length > 110 ? s.Substring(0, 107) + "..." : s)); } }, tok), TaskCreationOptions.LongRunning)
                .ContinueWith(t => UI(() =>
                {
                    odCts = null; odScan.Enabled = true;
                    if (t.IsFaulted) { odSummary.Text = Lang.T("OneDrive could not be read: ") + t.Exception.GetBaseException().Message; return; }
                    odResult = t.Result;
                    OneDriveFill();
                }));
        }

        void OneDriveFill()
        {
            if (odResult == null) return;
            var r = odResult; var list = r.NotUsedSince(OdCutoff());
            odList.BeginUpdate(); odList.Items.Clear();
            foreach (var f in list.Take(5000))
            {
                var lvi = new ListViewItem(f.Name) { Tag = f, ToolTipText = f.Path };
                lvi.SubItems.Add(Fs.Fmt(f.Size)); lvi.SubItems.Add(f.LastUsed.ToString("yyyy-MM-dd")); lvi.SubItems.Add(f.Folder);
                odList.Items.Add(lvi);
            }
            odList.EndUpdate();
            odSummary.Text = Lang.T("On this PC: " + Fs.Fmt(r.LocalBytes) + " of OneDrive files (" + Fs.Fmt(r.PinnedBytes) + " marked \"Always keep\"). Already online-only: " + Fs.Fmt(r.OnlineBytes) + ".") + "  " +
                             Lang.T(list.Count == 0 ? "Nothing here you haven't used lately." : list.Count + " files you haven't used lately could be online-only: " + Fs.Fmt(list.Sum(f => f.Size)) + ".");
            OneDriveSel();
        }

        void OneDriveSel()
        {
            var sel = odList.CheckedItems.Cast<ListViewItem>().ToList();
            odSel.Text = sel.Count == 0 ? "" : Lang.T(sel.Count + " selected (" + Fs.Fmt(sel.Sum(r => ((OneDriveFile)r.Tag).Size)) + ")");
            odFree.Enabled = sel.Count > 0 && odCts == null && !testMode;
        }

        void OneDriveFree()
        {
            var a = CurOneDrive; var sel = odList.CheckedItems.Cast<ListViewItem>().ToList();
            if (a == null || sel.Count == 0 || testMode) return;
            long bytes = sel.Sum(r => ((OneDriveFile)r.Tag).Size);
            if (Lang.Box(this, Lang.T("Make " + sel.Count + " files (" + Fs.Fmt(bytes) + ") online-only?") + "\n\n" +
                    Lang.T("They stay in OneDrive and in File Explorer. Opening one downloads it again, which needs internet. Nothing is deleted."),
                    "OneDrive", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int ok = 0; long done = 0;
            Cursor = Cursors.WaitCursor;
            foreach (var r in sel) { var f = (OneDriveFile)r.Tag; if (OneDrive.MakeOnlineOnly(f.Path, a)) { ok++; done += f.Size; odList.Items.Remove(r); } }
            Cursor = Cursors.Default;
            OneDrive.Forget();
            string msg = "OneDrive: " + ok + " of " + sel.Count + " files (" + Fs.Fmt(done) + ") are now online-only. OneDrive frees the space in a minute or two.";
            Log(Lang.T(msg)); odSummary.Text = Lang.T(msg);
            try { Activity.Add("clean", "OneDrive: " + ok + " files made online-only", done); } catch { }
            OneDriveSel();
        }
    }
}
