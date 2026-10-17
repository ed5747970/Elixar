// Email tab: what can be cleaned in your mailbox (Gmail, Outlook or any other email), and moving it to the trash.
// Connecting and turning Email cleaning on or off happen in Settings > Email account.

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
        Label emStatus, emWho, emIntro; ListView emList; TextBox emDetail;
        Button btnEmScan, btnEmTrash, btnEmSettings;
        CancellationTokenSource emCts;
        public event Action EmailTabDone;

        void BuildEmailClean()
        {
            pageEmail = new TabPage("Email") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(7);
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            emIntro = Note("");
            emWho = new Label { AutoSize = true, MaximumSize = new Size(S(1000), 0), Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(S(1), 0, 0, S(6)) };
            emList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
            emList.Columns.Add("What", S(380)); emList.Columns.Add("Emails", S(90), HorizontalAlignment.Right); emList.Columns.Add("About", S(100), HorizontalAlignment.Right);
            emList.Columns.Add("Level", S(110)); emList.Columns.Add("Most from", S(420));
            emDetail = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Margin = new Padding(0, S(6), 0, 0) };
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnEmScan = MakeButton("Scan my email"); btnEmScan.Font = new Font("Segoe UI Semibold", 9f);
            btnEmTrash = MakeButton("Move selected to the trash..."); btnEmTrash.Font = new Font("Segoe UI Semibold", 9f);
            btnEmSettings = MakeButton("Email settings...");
            row.Controls.AddRange(new Control[] { btnEmScan, btnEmTrash, btnEmSettings });
            emStatus = Note("");
            emStatus.Margin = new Padding(S(1), S(6), 0, 0);
            t.Controls.Add(Title("Email"), 0, 0); t.Controls.Add(emIntro, 0, 1); t.Controls.Add(emWho, 0, 2);
            t.Controls.Add(emList, 0, 3); t.Controls.Add(emDetail, 0, 4); t.Controls.Add(row, 0, 5); t.Controls.Add(emStatus, 0, 6);
            pageEmail.Controls.Add(t);

            btnEmSettings.Click += (o, e) => ShowSettings();
            btnEmScan.Click += (o, e) => { if (emCts != null) emCts.Cancel(); else if (MailAccount.Cleaning) ScanMailbox(); else ShowSettings(); };
            btnEmTrash.Click += (o, e) => TrashMail();
            emList.ItemChecked += (o, e) => UpdateEmailUi();
            emList.SelectedIndexChanged += (o, e) => ShowMailDetail();
            ShowMailCategories(MailAccount.Categories(), false);
            UpdateEmailUi();
        }

        public void ShowEmailTab() { tabs.SelectedTab = pageEmail; UpdateEmailUi(); if (EmailTabDone != null) EmailTabDone(); }
        public int EmailRows { get { return emList == null ? 0 : emList.Items.Count; } }
        string lastKind = "";

        void UpdateEmailUi()
        {
            if (emList == null) return;
            string who = MailAccount.Email, kind = MailAccount.Kind ?? "google";
            bool signed = who != null, on = signed && MailAccount.Cleaning, busy = emCts != null;
            string prov = MailAccount.ProviderName, trash = MailAccount.TrashName;
            if (kind != lastKind && !busy) { lastKind = kind; ShowMailCategories(MailAccount.Categories(), false); return; }
            emIntro.Text = Lang.T("Finds old ads, notifications, newsletters, spam and large old emails in your " + prov + ", and moves the ones you pick to the trash. " +
                                  "Starred, flagged and Important emails are never touched, and Elixar only looks at senders, dates and sizes, never at what your emails say.");
            emWho.ForeColor = Theme.MapFore(on ? SafeColor : Color.DimGray);
            if (!busy)
                emWho.Text = Lang.T(!signed ? "Your email isn't connected. Connect it in Settings > Email account, then turn on Email cleaning there." :
                                    !on ? "Email cleaning is off for " + who + ". Turn it on in Settings > Email account: Elixar asks you first." :
                                    "Email cleaning is on for " + who + " (" + prov + ").");
            btnEmScan.Text = Lang.T(busy ? "Stop" : on ? "Scan my email" : "Turn it on in Settings...");
            btnEmScan.Enabled = !testMode;
            var sel = emList.CheckedItems.Cast<ListViewItem>().Select(r => (MailCategory)r.Tag).Where(c => c.Count > 0).ToList();
            int n = sel.Sum(c => c.Count);
            btnEmTrash.Visible = on;
            btnEmTrash.Enabled = on && !busy && !testMode && n > 0;
            btnEmTrash.Text = n > 0 ? Lang.T("Move " + n.ToString("N0") + " emails to the trash...") : Lang.T("Move selected to the trash...");
            emStatus.Text = Lang.T(kind == "microsoft" ? "Moved emails go to Outlook's Deleted Items, where you can restore them. Empty Deleted Items in Outlook to free the space right away."
                                 : kind == "other" ? "Moved emails go to your mailbox's Trash folder, where you can restore them. Empty the Trash in your email to free the space right away."
                                 : "Moved emails stay in Gmail's Trash for 30 days, so you can restore them there. To free the space in your Google account right away, empty the Trash in Gmail.");
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
            var lines = new List<string> { Lang.T(c.Name), Lang.T(c.About), "", Lang.T("Search used:") + " " + c.Query.Replace("|", "  ") };
            if (c.Senders.Count > 0)
            {
                lines.Add("");
                lines.Add(Lang.T("Most from (in a sample of " + c.Sampled + " emails):"));
                lines.AddRange(c.Senders.Select(kv => "  • " + kv.Key + "  (" + kv.Value + ")"));
            }
            emDetail.Text = string.Join(Environment.NewLine, lines);
        }

        void ScanMailbox()
        {
            if (emCts != null) { emCts.Cancel(); return; }
            if (!MailAccount.Cleaning) return;
            emCts = new CancellationTokenSource();
            var token = emCts.Token;
            var cats = MailAccount.Categories();
            UpdateEmailUi();
            Task.Factory.StartNew(() =>
            {
                int i = 0;
                foreach (var c in cats)
                {
                    if (token.IsCancellationRequested) break;
                    int k = ++i;
                    UI(() => emWho.Text = Lang.T("Checking your email (" + k + " of " + cats.Count + ")..."));
                    MailAccount.Scan(c, token);
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                emCts = null;
                ShowMailCategories(cats, true);
                int total = cats.Where(c => c.Tier == Tier.Safe).Sum(c => c.Count);
                long bytes = cats.Where(c => c.Tier == Tier.Safe && c.Bytes > 0).Sum(c => c.Bytes);
                var bad = cats.FirstOrDefault(c => c.Error != null);
                emWho.Text = bad != null ? Lang.T("Some checks didn't finish: ") + bad.Error
                                         : Lang.T("Found " + total.ToString("N0") + " emails that are safe to clean (about " + Fs.Fmt(bytes) + "). The ones ticked are ready to move to the trash.");
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
            if (Lang.Box(this, "Move " + n.ToString("N0") + " emails to " + MailAccount.TrashName + "?\n\n" + list + "\n\nYou can restore them from there. Starred, flagged and Important emails are not included.",
                         "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            emCts = new CancellationTokenSource();
            var token = emCts.Token;
            UpdateEmailUi();
            Task.Factory.StartNew(() => MailAccount.Trash(sel, k => UI(() => emWho.Text = Lang.T("Moving to the trash: " + k.ToString("N0") + " of " + n.ToString("N0") + "...")), token),
                                  TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                emCts = null;
                int done = t.IsFaulted ? 0 : t.Result;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : null;
                Log("Email: moved " + done + " of " + n + " emails to the trash.");
                Lang.Box(this, err != null ? "Nothing more was moved: " + err :
                               done == n ? "Moved " + done.ToString("N0") + " emails to " + MailAccount.TrashName + ". Empty it in your email to free the space now."
                                         : "Moved " + done.ToString("N0") + " of " + n.ToString("N0") + " emails to the trash. Scan again to see what is left.",
                         "Elixar", MessageBoxButtons.OK, err == null && done == n ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                ScanMailbox();
            }));
        }
    }
}
