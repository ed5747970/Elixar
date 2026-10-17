// Email tab: what can be cleaned in your Gmail, and moving it to Gmail's Trash. Off until you allow it.

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
        TabPage pageEmail;
        Label emStatus, emWho; ListView emList; TextBox emDetail;
        Button btnEmAllow, btnEmScan, btnEmTrash, btnEmOff, btnEmSettings;
        CancellationTokenSource emCts;
        public event Action EmailTabDone;

        void BuildEmailClean()
        {
            pageEmail = new TabPage("Email") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(7);
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var intro = Note("Finds old ads, social notifications, mailing lists, spam and large old emails in your Gmail, and moves the ones you pick to Gmail's Trash. " +
                             "Starred and Important emails are never touched, and Elixar only looks at senders, dates and sizes, never at what your emails say.");
            emWho = new Label { AutoSize = true, MaximumSize = new Size(S(1000), 0), Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(S(1), 0, 0, S(6)) };
            emList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
            emList.Columns.Add("What", S(380)); emList.Columns.Add("Emails", S(90), HorizontalAlignment.Right); emList.Columns.Add("About", S(100), HorizontalAlignment.Right);
            emList.Columns.Add("Level", S(110)); emList.Columns.Add("Most from", S(420));
            emDetail = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Margin = new Padding(0, S(6), 0, 0) };
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnEmAllow = MakeButton("Allow Elixar to check my Gmail..."); btnEmAllow.Font = new Font("Segoe UI Semibold", 9f);
            btnEmScan = MakeButton("Scan my Gmail");
            btnEmTrash = MakeButton("Move selected to Gmail's Trash..."); btnEmTrash.Font = new Font("Segoe UI Semibold", 9f);
            btnEmOff = MakeButton("Turn off Email cleaning...");
            btnEmSettings = MakeButton("Sign in first (Settings)");
            row.Controls.AddRange(new Control[] { btnEmSettings, btnEmAllow, btnEmScan, btnEmTrash, btnEmOff });
            emStatus = Note("Moved emails stay in Gmail's Trash for 30 days, so you can restore them there. To free the space in your Google account right away, empty the Trash in Gmail.");
            emStatus.Margin = new Padding(S(1), S(6), 0, 0);
            t.Controls.Add(Title("Email"), 0, 0); t.Controls.Add(intro, 0, 1); t.Controls.Add(emWho, 0, 2);
            t.Controls.Add(emList, 0, 3); t.Controls.Add(emDetail, 0, 4); t.Controls.Add(row, 0, 5); t.Controls.Add(emStatus, 0, 6);
            pageEmail.Controls.Add(t);

            btnEmSettings.Click += (o, e) => ShowSettings();
            btnEmAllow.Click += (o, e) => AllowMailbox();
            btnEmScan.Click += (o, e) => ScanMailbox();
            btnEmTrash.Click += (o, e) => TrashMail();
            btnEmOff.Click += (o, e) => TurnOffMailbox();
            emList.ItemChecked += (o, e) => UpdateEmailUi();
            emList.SelectedIndexChanged += (o, e) => ShowMailDetail();
            ShowMailCategories(Gmail.Categories(), false);
            UpdateEmailUi();
        }

        public void ShowEmailTab() { tabs.SelectedTab = pageEmail; UpdateEmailUi(); if (EmailTabDone != null) EmailTabDone(); }
        public int EmailRows { get { return emList == null ? 0 : emList.Items.Count; } }

        void UpdateEmailUi()
        {
            if (emList == null) return;
            string who = GoogleAuth.SignedInEmail;
            bool signed = who != null, access = signed && GoogleAuth.MailboxAccess, busy = emCts != null;
            emWho.ForeColor = Theme.MapFore(access ? SafeColor : Color.DimGray);
            emWho.Text = Lang.T(!signed ? "Not signed in. Sign in with Google in Settings > Email reports first." :
                                !access ? "Signed in as " + who + ". Email cleaning is off: Elixar can't see your emails until you allow it." :
                                "Email cleaning is on for " + who + ".");
            btnEmSettings.Visible = !signed;
            btnEmAllow.Visible = signed && !access;
            btnEmAllow.Enabled = !busy && !testMode && GoogleAuth.HasClient;
            btnEmScan.Visible = btnEmTrash.Visible = btnEmOff.Visible = access;
            btnEmScan.Text = Lang.T(busy ? "Stop" : "Scan my Gmail");
            btnEmScan.Enabled = !testMode;
            var sel = emList.CheckedItems.Cast<ListViewItem>().Select(r => (MailCategory)r.Tag).Where(c => c.Count > 0).ToList();
            int n = sel.Sum(c => c.Count);
            btnEmTrash.Enabled = access && !busy && !testMode && n > 0;
            btnEmTrash.Text = n > 0 ? Lang.T("Move " + n.ToString("N0") + " emails to Gmail's Trash...") : Lang.T("Move selected to Gmail's Trash...");
            btnEmOff.Enabled = !busy && !testMode;
        }

        void ShowMailCategories(List<MailCategory> cats, bool scanned)
        {
            emList.BeginUpdate();
            emList.Items.Clear();
            foreach (var c in cats)
            {
                var it = new ListViewItem(Lang.T(c.Name)) { Tag = c, Checked = c.DefaultOn && scanned && c.Count > 0, UseItemStyleForSubItems = false };
                it.SubItems.Add(!scanned ? "" : c.Error != null ? "?" : c.Count.ToString("N0") + (c.More ? "+" : ""));
                it.SubItems.Add(!scanned || c.Error != null ? "" : c.Bytes < 0 ? "?" : Fs.Fmt(c.Bytes));
                var lv = it.SubItems.Add(Lang.T(c.Tier == Tier.Safe ? "Safe" : "Your decision"));
                lv.ForeColor = Theme.MapFore(c.Tier == Tier.Safe ? SafeColor : ReviewColor);
                it.SubItems.Add(c.Error != null ? Lang.T("Could not check: ") + c.Error : string.Join(", ", c.Senders.Take(4).Select(kv => kv.Key)));
                emList.Items.Add(it);
            }
            emList.EndUpdate();
            Theme.FixItems(emList);
            UpdateEmailUi();
        }

        void ShowMailDetail()
        {
            if (emList.SelectedItems.Count == 0) return;
            var c = (MailCategory)emList.SelectedItems[0].Tag;
            var lines = new List<string> { Lang.T(c.Name), Lang.T(c.About), "", Lang.T("Gmail search used:") + " " + c.Query };
            if (c.Sampled > 0)
            {
                lines.Add("");
                lines.Add(Lang.T("Most from (in a sample of " + c.Sampled + " emails):"));
                lines.AddRange(c.Senders.Select(kv => "  • " + kv.Key + "  (" + kv.Value + ")"));
            }
            emDetail.Text = string.Join(Environment.NewLine, lines);
        }

        // Elixar asks in its own words first; only then does Google ask for the permission
        void AllowMailbox()
        {
            string q = Lang.T("Allow Elixar to check your Gmail?") + "\n\n" +
                       Lang.T("To find emails you can clean, Elixar needs Google's permission to see your emails' senders, dates and sizes, and to move emails to Gmail's Trash.") + "\n\n" +
                       "• " + Lang.T("Elixar looks only at senders, dates and sizes. It never opens what your emails say and saves nothing about them.") + "\n" +
                       "• " + Lang.T("Nothing leaves your PC: Elixar has no server.") + "\n" +
                       "• " + Lang.T("Emails are only moved to Gmail's Trash, only after you confirm, and can be restored there for 30 days.") + "\n" +
                       "• " + Lang.T("Starred and Important emails are never touched.") + "\n" +
                       "• " + Lang.T("You can turn it off any time on this tab.") + "\n\n" +
                       Lang.T("Google's own screen will describe this permission in broader words than what Elixar does with it. Continue?");
            if (MessageBox.Show(this, q, "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
                                Lang.Hebrew ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0) != DialogResult.Yes) return;
            emCts = new CancellationTokenSource();
            var token = emCts.Token;
            UpdateEmailUi();
            emWho.Text = Lang.T("Waiting for you to sign in to Google in your browser...");
            Task.Factory.StartNew(() => GoogleAuth.SignIn(st => UI(() => emWho.Text = Lang.T(st)), token, true), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                emCts = null;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                UpdateEmailUi(); UpdateMailUi();
                Activate();
                if (err != null) { Lang.Box(this, "Email cleaning wasn't turned on: " + err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Log("Email cleaning turned on for " + GoogleAuth.SignedInEmail + ".");
                ScanMailbox();
            }));
        }

        void ScanMailbox()
        {
            if (emCts != null) { emCts.Cancel(); return; }
            emCts = new CancellationTokenSource();
            var token = emCts.Token;
            var cats = Gmail.Categories();
            UpdateEmailUi();
            Task.Factory.StartNew(() =>
            {
                int i = 0;
                foreach (var c in cats)
                {
                    if (token.IsCancellationRequested) break;
                    int k = ++i;
                    UI(() => emWho.Text = Lang.T("Checking your Gmail (" + k + " of " + cats.Count + ")..."));
                    Gmail.Scan(c, token);
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                emCts = null;
                ShowMailCategories(cats, true);
                int total = cats.Where(c => c.Tier == Tier.Safe).Sum(c => c.Count);
                long bytes = cats.Where(c => c.Tier == Tier.Safe && c.Bytes > 0).Sum(c => c.Bytes);
                var bad = cats.FirstOrDefault(c => c.Error != null);
                emWho.Text = bad != null ? Lang.T("Some checks didn't finish: ") + bad.Error
                                         : Lang.T("Found " + total.ToString("N0") + " emails that are safe to clean (about " + Fs.Fmt(bytes) + "). The ones ticked are ready to move to Gmail's Trash.");
                Log("Email: scan found " + cats.Sum(c => c.Count) + " emails in " + cats.Count(c => c.Count > 0) + " groups.");   // counts only, never senders
                if (emList.Items.Count > 0) emList.Items[0].Selected = true;
            }));
        }

        void TrashMail()
        {
            var sel = emList.CheckedItems.Cast<ListViewItem>().Select(r => (MailCategory)r.Tag).Where(c => c.Count > 0).ToList();
            if (sel.Count == 0) return;
            int n = sel.Sum(c => c.Ids.Count);
            string list = string.Join("\n", sel.Select(c => "  - " + Lang.T(c.Name) + ": " + c.Ids.Count.ToString("N0")));
            if (Lang.Box(this, "Move " + n.ToString("N0") + " emails to Gmail's Trash?\n\n" + list + "\n\nYou can restore them from Gmail's Trash for 30 days. Starred and Important emails are not included.",
                         "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            emCts = new CancellationTokenSource();
            var token = emCts.Token;
            UpdateEmailUi();
            var ids = sel.SelectMany(c => c.Ids).Distinct().ToList();
            Task.Factory.StartNew(() => Gmail.Trash(ids, k => UI(() => emWho.Text = Lang.T("Moving to Gmail's Trash: " + k.ToString("N0") + " of " + ids.Count.ToString("N0") + "...")), token),
                                  TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                emCts = null;
                int done = t.IsFaulted ? 0 : t.Result;
                Log("Email: moved " + done + " of " + ids.Count + " emails to Gmail's Trash.");
                Lang.Box(this, done == ids.Count ? "Moved " + done.ToString("N0") + " emails to Gmail's Trash. Empty the Trash in Gmail to free the space now, or Gmail does it after 30 days."
                                                 : "Moved " + done.ToString("N0") + " of " + ids.Count.ToString("N0") + " emails to Gmail's Trash. Scan again to see what is left.",
                         "Elixar", MessageBoxButtons.OK, done == ids.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                ScanMailbox();
            }));
        }

        // Google can only take back a permission by signing out; email reports need one sign-in again (send only)
        void TurnOffMailbox()
        {
            if (Lang.Box(this, "Turn off Email cleaning?\n\nElixar signs out of Google so the permission to see your emails is taken back. To keep getting email reports, sign in again in Settings (that sign-in can only send).",
                         "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            GoogleAuth.SignOut();
            string o; Automation.RunSchtasks(Automation.DeleteArgs(Automation.MailTask), out o);
            Log("Email cleaning turned off; signed out of Google.");
            ShowMailCategories(Gmail.Categories(), false);
            UpdateMailUi();
            ShowSettings();
        }
    }
}
