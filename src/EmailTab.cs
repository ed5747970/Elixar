// Settings > Email account: connect Gmail, Outlook/Hotmail or any other email (IMAP), turn Email cleaning on or off,
// and choose which emails Elixar sends you. The Email tab only scans and cleans.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        Label mailStatus, mailCleanState, mailNote, otherWarn, otherHelp;
        RadioButton rbGmail, rbOutlook, rbOther;
        FlowLayoutPanel pnlGoogle, pnlMicrosoft, pnlOther, pnlConnected, pnlChoose, pnlReports;
        Button btnMailSignIn, btnMailSetup, btnMsSignIn, btnMsSetup, btnOtherConnect, btnMailTest, btnMailOut, btnMailSave, btnMailCleanOn;
        TextBox txtOtherEmail, txtOtherPass, txtOtherImap, txtOtherSmtp;
        CheckBox chkMailClean, chkMailAlerts, chkMailSummary; ComboBox cmbMailDay; DateTimePicker timeMail;
        CancellationTokenSource mailCts;

        Control BuildEmailBox()
        {
            var box = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            mailStatus = new Label { AutoSize = true, MaximumSize = new Size(S(900), 0), Margin = new Padding(S(2), 0, 0, S(6)), Text = "" };
            Func<FlowLayoutPanel> Row = () => new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };

            // 1. which email
            pnlChoose = Row();
            var lblWhich = new Label { AutoSize = true, Text = "Your email:", Margin = new Padding(S(2), S(5), S(10), 0) };
            rbGmail = new RadioButton { AutoSize = true, Text = "Gmail", Checked = true, Margin = new Padding(0, S(3), S(16), 0) };
            rbOutlook = new RadioButton { AutoSize = true, Text = "Outlook / Hotmail / Live", Margin = new Padding(0, S(3), S(16), 0) };
            rbOther = new RadioButton { AutoSize = true, Text = "Other (Yahoo, iCloud, AOL, GMX...)", Margin = new Padding(0, S(3), 0, 0) };
            pnlChoose.Controls.AddRange(new Control[] { lblWhich, rbGmail, rbOutlook, rbOther });

            pnlGoogle = Row(); pnlGoogle.Margin = new Padding(0, S(6), 0, 0);
            btnMailSignIn = MakeButton("Sign in with Google"); btnMailSignIn.Font = new Font("Segoe UI Semibold", 9f);
            btnMailSetup = MakeButton("Set up Google sign-in (one time)...");
            pnlGoogle.Controls.AddRange(new Control[] { btnMailSignIn, btnMailSetup });

            pnlMicrosoft = Row(); pnlMicrosoft.Margin = new Padding(0, S(6), 0, 0);
            btnMsSignIn = MakeButton("Sign in with Microsoft"); btnMsSignIn.Font = new Font("Segoe UI Semibold", 9f);
            btnMsSetup = MakeButton("Set up Microsoft sign-in (one time)...");
            pnlMicrosoft.Controls.AddRange(new Control[] { btnMsSignIn, btnMsSetup });

            pnlOther = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, S(6), 0, 0) };
            Func<string, int, TextBox> Field = (label, w) => new TextBox { Width = S(w) };
            txtOtherEmail = Field("Email", 230); txtOtherPass = Field("App password", 180); txtOtherPass.UseSystemPasswordChar = true;
            txtOtherImap = Field("IMAP", 200); txtOtherSmtp = Field("SMTP", 200);
            Func<string, Label> Cap = t => new Label { AutoSize = true, Text = t, Margin = new Padding(S(2), S(6), S(4), 0) };
            var r1 = Row(); r1.Controls.AddRange(new Control[] { Cap("Email address"), txtOtherEmail, Cap("App password"), txtOtherPass });
            var r2 = Row(); r2.Controls.AddRange(new Control[] { Cap("Incoming (IMAP, port 993)"), txtOtherImap, Cap("Outgoing (SMTP, port 587)"), txtOtherSmtp });
            otherHelp = new Label { AutoSize = true, MaximumSize = new Size(S(900), 0), ForeColor = Color.DimGray, UseMnemonic = false, Margin = new Padding(S(2), S(4), 0, 0) };
            otherWarn = new Label { AutoSize = true, MaximumSize = new Size(S(900), 0), ForeColor = CautionColor, Margin = new Padding(S(2), S(4), 0, 0),
                                    Text = "An app password lets a program open your whole mailbox (that's how email programs work). Elixar keeps it encrypted on this PC in a folder only administrators can open, " +
                                           "uses it only for what you turn on here, and always connects securely. You can delete the app password on your provider's site any time." };
            btnOtherConnect = MakeButton("Connect"); btnOtherConnect.Font = new Font("Segoe UI Semibold", 9f); btnOtherConnect.Margin = new Padding(S(2), S(6), 0, 0);
            pnlOther.Controls.AddRange(new Control[] { r1, r2, otherHelp, otherWarn, btnOtherConnect });

            pnlConnected = Row(); pnlConnected.Margin = new Padding(0, S(6), 0, 0);
            btnMailTest = MakeButton("Send a test email");
            btnMailOut = MakeButton("Disconnect");
            pnlConnected.Controls.AddRange(new Control[] { btnMailTest, btnMailOut });

            // 2. Email cleaning: separate, off until turned on
            var cleanRow = Row(); cleanRow.Margin = new Padding(0, S(10), 0, 0);
            mailCleanState = new Label { AutoSize = true, MaximumSize = new Size(S(640), 0), Margin = new Padding(S(2), S(7), S(8), 0) };
            btnMailCleanOn = MakeButton("Turn on Email cleaning...");
            cleanRow.Controls.Add(mailCleanState); cleanRow.Controls.Add(btnMailCleanOn);

            // 3. which emails Elixar sends you
            var p = MailPrefs.Load();
            pnlReports = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(S(2), S(10), 0, 0) };
            chkMailClean = new CheckBox { AutoSize = true, Checked = p.CleanReport, Text = "Weekly auto-clean report: what was cleaned, how much it freed, and what is left for you to decide" };
            chkMailAlerts = new CheckBox { AutoSize = true, Checked = p.Alerts, Text = "Alerts: C: almost full (your limit in Automation, or 15%), or a drive that may fail" };
            var sumRow = Row();
            chkMailSummary = new CheckBox { AutoSize = true, Checked = p.Summary, Text = "Weekly summary every", Margin = new Padding(S(3), S(5), S(4), 0) };
            cmbMailDay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(110) };
            cmbMailDay.Items.AddRange(Automation.Days); cmbMailDay.SelectedIndex = Math.Max(0, Array.IndexOf(Automation.Days, p.Day));
            timeMail = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = S(70) };
            DateTime tm; timeMail.Value = DateTime.TryParse(p.Time, out tm) ? DateTime.Today.Add(tm.TimeOfDay) : DateTime.Today.AddHours(9);
            var at = new Label { AutoSize = true, Text = "at", Margin = new Padding(S(4), S(6), S(4), 0) };
            var also = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "(alerts are checked daily at this time too)", Margin = new Padding(S(6), S(6), 0, 0) };
            sumRow.Controls.AddRange(new Control[] { chkMailSummary, cmbMailDay, at, timeMail, also });
            var row2 = Row();
            btnMailSave = MakeButton("Save email settings"); btnMailSave.Margin = new Padding(S(2), S(8), 0, 0);
            row2.Controls.Add(btnMailSave);
            pnlReports.Controls.AddRange(new Control[] { chkMailClean, chkMailAlerts, sumRow, row2 });

            mailNote = Note(""); mailNote.Margin = new Padding(S(2), S(8), 0, 0);
            box.Controls.AddRange(new Control[] { mailStatus, pnlChoose, pnlGoogle, pnlMicrosoft, pnlOther, pnlConnected, cleanRow, pnlReports, mailNote });

            EventHandler pick = (o, e) => UpdateMailUi();
            rbGmail.CheckedChanged += pick; rbOutlook.CheckedChanged += pick; rbOther.CheckedChanged += pick;
            txtOtherEmail.Leave += (o, e) => FillOtherServers(false);
            btnMailSetup.Click += (o, e) => { using (var f = new GoogleSetupForm(s)) { f.Shown += (o2, e2) => { Theme.Apply(f); Lang.Apply(f); }; f.ShowDialog(this); } UpdateMailUi(); };
            btnMsSetup.Click += (o, e) => { using (var f = new MicrosoftSetupForm(s)) { f.Shown += (o2, e2) => { Theme.Apply(f); Lang.Apply(f); }; f.ShowDialog(this); } UpdateMailUi(); };
            btnMailSignIn.Click += (o, e) => SignInMail("google", false);
            btnMsSignIn.Click += (o, e) => SignInMail("microsoft", false);
            btnOtherConnect.Click += (o, e) => ConnectOther();
            btnMailOut.Click += (o, e) => DisconnectMail();
            btnMailTest.Click += (o, e) => RunMail("Sending a test email...", () => MailReports.Test().Send(), "Test email sent to " + MailAccount.Email + ".");
            btnMailSave.Click += (o, e) => SaveMail();
            btnMailCleanOn.Click += (o, e) => { if (MailAccount.Cleaning) ToggleCleaning(false); else ToggleCleaning(true); };
            string k = MailAccount.Kind;
            (k == "microsoft" ? rbOutlook : k == "other" ? rbOther : rbGmail).Checked = true;
            UpdateMailUi();
            return box;
        }

        string ChosenKind { get { return rbOutlook.Checked ? "microsoft" : rbOther.Checked ? "other" : "google"; } }

        void FillOtherServers(bool force)
        {
            var pre = OtherMail.PresetFor(txtOtherEmail.Text);
            if (force || txtOtherImap.Text.Trim().Length == 0) txtOtherImap.Text = pre.Imap;
            if (force || txtOtherSmtp.Text.Trim().Length == 0) txtOtherSmtp.Text = pre.Smtp;
            otherHelp.Text = Lang.T(pre.Help.Length > 0 ? "How to get an app password: " + pre.Help : "Type your email address: Elixar fills in the servers for well-known providers.");
        }

        void UpdateMailUi()
        {
            if (mailStatus == null) return;
            string who = MailAccount.Email, kind = MailAccount.Kind;
            bool signed = who != null, busy = mailCts != null, cleaning = signed && MailAccount.Cleaning;
            int level = 0;
            string health = signed ? MailStatus.Line(out level) : null;
            mailStatus.ForeColor = Theme.MapFore(!signed ? Color.DimGray : level == 2 ? DangerColor : level == 1 ? CautionColor : SafeColor);
            mailStatus.Text = signed ? Lang.T("Connected: " + who + " (" + MailAccount.ProviderName + "). Emails go to this address.") + "\n" + Lang.T(health)
                                     : Lang.T("Not connected. Choose your email and connect it to get email reports.");
            pnlChoose.Visible = !signed;
            string pickKind = signed ? kind : ChosenKind;
            pnlGoogle.Visible = !signed && pickKind == "google";
            pnlMicrosoft.Visible = !signed && pickKind == "microsoft";
            pnlOther.Visible = !signed && pickKind == "other";
            pnlConnected.Visible = signed;
            btnMailSignIn.Text = Lang.T(busy ? "Cancel sign-in" : "Sign in with Google");
            btnMailSignIn.Enabled = (GoogleAuth.HasClient || busy) && !testMode;
            btnMailSetup.Visible = GoogleAuth.BuiltInClient == null;   // the GitHub build has the Google app built in
            btnMsSignIn.Text = Lang.T(busy ? "Cancel sign-in" : "Sign in with Microsoft");
            btnMsSignIn.Enabled = (MicrosoftAuth.ClientId != null || busy) && !testMode;
            btnMsSetup.Visible = !MicrosoftAuth.BuiltIn;
            btnOtherConnect.Enabled = !busy && !testMode;
            if (pnlOther.Visible && otherHelp.Text.Length == 0) FillOtherServers(false);
            btnMailTest.Enabled = btnMailOut.Enabled = signed && !busy && !testMode;
            btnMailSave.Enabled = signed && !busy && !testMode;
            foreach (Control c in new Control[] { chkMailClean, chkMailAlerts, chkMailSummary, cmbMailDay, timeMail }) c.Enabled = signed;
            mailCleanState.Text = Lang.T(!signed ? "Email cleaning: off. Connect your email first." :
                                         cleaning ? "Email cleaning: on. Use the Email tab to scan and clean." :
                                         "Email cleaning: off. Optional: lets Elixar find old ads, notifications and spam in your " + MailAccount.ProviderName + ".");
            mailCleanState.ForeColor = Theme.MapFore(cleaning ? SafeColor : Color.DimGray);
            btnMailCleanOn.Text = Lang.T(cleaning ? "Turn off Email cleaning..." : "Turn on Email cleaning...");
            btnMailCleanOn.Enabled = signed && !busy && !testMode;
            mailNote.Text = Lang.T((signed ? kind : ChosenKind) != "other"
                ? (cleaning ? "Emails go to you only, list categories and sizes (never your file names), and never contain a link. With Email cleaning on, Elixar can also see your emails' senders, dates and sizes and move emails to the trash, only from the Email tab and only after you confirm. Your sign-in is encrypted on this PC in a folder only administrators can open."
                            : "With Google or Microsoft, Elixar can only send email to you, never read your inbox, until you turn on Email cleaning. Emails list categories and sizes (never your file names) and never contain a link: to delete anything, open Elixar. Your sign-in is encrypted on this PC in a folder only administrators can open.")
                : "Emails go to you only, list categories and sizes (never your file names), and never contain a link. Your app password is encrypted on this PC in a folder only administrators can open, and Elixar looks at your mailbox only when Email cleaning is on.");
            UpdateEmailUi();
        }

        // Google or Microsoft: sign in in the browser. cleaning: also ask for the permission to see and tidy emails.
        void SignInMail(string kind, bool cleaning)
        {
            if (mailCts != null) { mailCts.Cancel(); return; }
            if (!cleaning && MailAccount.Email != null && MailAccount.Kind != kind) MailAccount.Disconnect();
            mailCts = new CancellationTokenSource();
            var token = mailCts.Token;
            UpdateMailUi();
            Action<string> st = x => UI(() => mailStatus.Text = Lang.T(x));
            Task.Factory.StartNew(() => kind == "microsoft" ? MicrosoftAuth.SignIn(st, token, cleaning) : GoogleAuth.SignIn(st, token, cleaning), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                mailCts = null;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                if (err == null) MailAccount.SetKind(kind);
                UpdateMailUi();
                Activate();
                if (err != null) { Lang.Box(this, (cleaning ? "Email cleaning wasn't turned on: " : "Sign-in didn't finish: ") + err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Log((cleaning ? "Email cleaning turned on for " : "Email reports: connected ") + MailAccount.Email + ".");
                if (cleaning) { ShowEmailTab(); ScanMailbox(); return; }
                SaveMail();
                RunMail("Sending a test email...", () => MailReports.Test().Send(), "Connected. A test email was sent to " + MailAccount.Email + ".");
            }));
        }

        // any other provider: check the settings by signing in to IMAP, then send a test email through SMTP
        void ConnectOther()
        {
            var st = new OtherMail.Settings_ { Email = txtOtherEmail.Text.Trim(), Password = txtOtherPass.Text.Trim(), Imap = txtOtherImap.Text.Trim(), Smtp = txtOtherSmtp.Text.Trim() };
            string better = OtherMail.BetterChoice(st.Email);
            if (better != null)
            {
                Lang.Box(this, better == "google" ? "This is a Gmail address: choose \"Gmail\" above. Sign in with Google is safer than an app password."
                                                  : "This is a Microsoft address: choose \"Outlook / Hotmail / Live\" above. Sign in with Microsoft is safer than an app password.",
                         "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                (better == "google" ? rbGmail : rbOutlook).Checked = true;
                return;
            }
            if (MailAccount.Email != null) MailAccount.Disconnect();
            mailCts = new CancellationTokenSource();
            UpdateMailUi();
            mailStatus.Text = Lang.T("Connecting to " + st.Imap + "...");
            Task.Factory.StartNew(() => OtherMail.Connect(st), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                mailCts = null;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                if (err == null) { MailAccount.SetKind("other"); txtOtherPass.Text = ""; }
                UpdateMailUi();
                if (err != null) { Lang.Box(this, err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Log("Email reports: connected " + st.Email + " (" + st.Imap + ").");
                SaveMail();
                RunMail("Sending a test email...", () => MailReports.Test().Send(), "Connected. A test email was sent to " + st.Email + ".");
            }));
        }

        // Elixar asks in its own words first; only then does Google or Microsoft ask for the permission
        public void ToggleCleaning(bool on)
        {
            string kind = MailAccount.Kind;
            if (MailAccount.Email == null) return;
            if (!on)
            {
                string q = kind == "other" ? "Turn off Email cleaning? Elixar stops looking at your mailbox. Email reports keep working."
                         : "Turn off Email cleaning?\n\nElixar signs out so the permission to see your emails is taken back. Then sign in again here to keep getting email reports (that sign-in can only send)." +
                           (kind == "microsoft" ? "\n\nMicrosoft keeps a record of permissions you gave: to remove it too, open account.live.com/consent/Manage and remove Elixar." : "");
                if (Lang.Box(this, q, "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                if (kind == "other") { var s2 = OtherMail.Load(); s2.Clean = false; OtherMail.Save(s2); }
                else
                {
                    MailAccount.Disconnect();
                    string o; Automation.RunSchtasks(Automation.DeleteArgs(Automation.MailTask), out o);
                    (kind == "microsoft" ? rbOutlook : rbGmail).Checked = true;
                }
                Log("Email cleaning turned off.");
                UpdateMailUi();
                return;
            }
            string prov = MailAccount.ProviderName;
            string ask = Lang.T("Allow Elixar to check your email?") + "\n\n" +
                         Lang.T(kind == "other" ? "Elixar will use your app password to look at your mailbox and move emails you pick to its Trash."
                                                : "To find emails you can clean, Elixar needs " + (kind == "microsoft" ? "Microsoft" : "Google") + "'s permission to see your emails' senders, dates and sizes, and to move emails to the trash.") + "\n\n" +
                         "• " + Lang.T("Elixar looks only at senders, dates and sizes. It never opens what your emails say and saves nothing about them.") + "\n" +
                         "• " + Lang.T("Nothing leaves your PC: Elixar has no server.") + "\n" +
                         "• " + Lang.T("Emails are only moved to the trash, only after you confirm, and can be restored from there.") + "\n" +
                         "• " + Lang.T("Starred, flagged and Important emails are never touched.") + "\n" +
                         "• " + Lang.T("You can turn it off any time here in Settings.") +
                         (kind == "other" ? "" : "\n\n" + Lang.T("The sign-in screen will describe this permission in broader words than what Elixar does with it.")) + "\n\n" + Lang.T("Continue?");
            if (MessageBox.Show(this, ask, "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
                                Lang.Hebrew ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0) != DialogResult.Yes) return;
            if (kind == "other")
            {
                var s3 = OtherMail.Load(); s3.Clean = true; OtherMail.Save(s3);
                Log("Email cleaning turned on for " + s3.Email + ".");
                UpdateMailUi(); ShowEmailTab(); ScanMailbox();
                return;
            }
            SignInMail(kind, true);
        }

        void DisconnectMail()
        {
            string kind = MailAccount.Kind;
            if (Lang.Box(this, "Disconnect " + MailAccount.Email + "? Elixar stops sending emails" + (kind == "google" ? " and Google cancels its permission." : kind == "microsoft" ? " and forgets its Microsoft sign-in." : " and forgets the app password."),
                         "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            MailAccount.Disconnect();
            string o2; Automation.RunSchtasks(Automation.DeleteArgs(Automation.MailTask), out o2);
            Log("Email reports: disconnected.");
            UpdateMailUi();
        }

        void RunMail(string doing, Func<string> work, string done)
        {
            mailStatus.Text = Lang.T(doing);
            btnMailTest.Enabled = false;
            Task.Factory.StartNew(work).ContinueWith(t => UI(() =>
            {
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                UpdateMailUi();
                if (err == null) { mailStatus.Text = Lang.T(done); Log("Email reports: " + done); }
                else Lang.Box(this, "The email wasn't sent: " + err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }));
        }

        // saves the choices and the daily email schedule (it starts the protected copy in Program Files, as administrator)
        void SaveMail()
        {
            if (testMode || MailAccount.Email == null) return;
            var p = MailPrefs.Load();
            p.CleanReport = chkMailClean.Checked; p.Alerts = chkMailAlerts.Checked; p.Summary = chkMailSummary.Checked;
            p.Day = Automation.Days[Math.Max(0, cmbMailDay.SelectedIndex)];   // by position: the names are translated in Hebrew
            p.Time = timeMail.Value.ToString("HH:mm");
            p.Save();
            string outp;
            if (p.Alerts || p.Summary)
            {
                string ierr = Install.Ensure();
                if (ierr != null) { Lang.Box(this, "Elixar could not put its protected copy in " + Install.Dir + ":\n" + ierr, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                int code = Automation.RunSchtasks(Automation.CreateMailArgs(Install.Exe, p.Time), out outp);
                Log(code == 0 ? "Email reports saved: daily check at " + p.Time + "." : "Could not save the email schedule: " + outp);
            }
            else Automation.RunSchtasks(Automation.DeleteArgs(Automation.MailTask), out outp);
            mailStatus.Text = Lang.T("Email settings saved.");
        }
    }

    // ---------------- one-time setup: the Microsoft "app ID" ----------------
    public class MicrosoftSetupForm : Form
    {
        public MicrosoftSetupForm(float scale)
        {
            Func<int, int> S = px => (int)Math.Round(px * scale);
            Text = Lang.T("Set up Microsoft sign-in"); Icon = Brand.Load(32); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(S(16)); Font = new Font("Segoe UI", 9.5f);
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Func<string, Label> L = t => new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(S(640), 0), Text = t, Margin = new Padding(0, 0, 0, S(8)) };
            f.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 12.5f), Text = Lang.T("Set up Microsoft sign-in (one time, about 5 minutes)"), Margin = new Padding(0, 0, 0, S(10)) });
            f.Controls.Add(L(Lang.T("Elixar has no server of its own, so Microsoft needs to know \"Elixar on your PC\" as your own small app. It's free; you do this once:")));
            foreach (var st in new[] {
                "1. Open entra.microsoft.com and sign in with your Outlook/Hotmail account.",
                "2. App registrations > New registration. Name: Elixar. Supported account types: \"Accounts in any organizational directory and personal Microsoft accounts\".",
                "3. Redirect URI: platform \"Public client/native (mobile & desktop)\", address http://localhost. Click Register.",
                "4. API permissions > Add a permission > Microsoft Graph > Delegated: Mail.Send (and Mail.ReadWrite only if you want Email cleaning). User.Read is already there.",
                "5. Copy the \"Application (client) ID\" from the Overview page and paste it below. No secret is needed." })
                f.Controls.Add(L(Lang.T(st)));
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            var id = new TextBox { Width = S(300), Text = MicrosoftAuth.ClientId ?? "" };
            var save = new Button { AutoSize = true, Text = Lang.T("Save"), Font = new Font("Segoe UI Semibold", 9.5f) };
            var open = new Button { AutoSize = true, Text = Lang.T("Open entra.microsoft.com") };
            var close = new Button { AutoSize = true, Text = Lang.T("Close"), DialogResult = DialogResult.OK };
            row.Controls.AddRange(new Control[] { id, save, open, close });
            var status = new Label { AutoSize = true, MaximumSize = new Size(S(640), 0), ForeColor = Color.DimGray, Margin = new Padding(0, S(6), 0, 0),
                                     Text = Lang.T(MicrosoftAuth.ClientId != null ? "A Microsoft app ID is already set up. You can replace it." : "No Microsoft app ID set up yet.") };
            f.Controls.Add(row); f.Controls.Add(status);
            Controls.Add(f); CancelButton = close;
            open.Click += (o, e) => { try { Process.Start(Win.SystemExe("explorer.exe"), "\"https://entra.microsoft.com/\""); } catch { } };
            save.Click += (o, e) =>
            {
                string err = MicrosoftAuth.SaveClientId(id.Text);
                status.ForeColor = err == null ? Color.FromArgb(0, 125, 70) : Color.FromArgb(199, 54, 47);
                status.Text = Lang.T(err ?? "Done. Close this window and click \"Sign in with Microsoft\".");
            };
        }
    }

    // ---------------- one-time setup: the Google "app keys" ----------------
    public class GoogleSetupForm : Form
    {
        public GoogleSetupForm(float scale)
        {
            Func<int, int> S = px => (int)Math.Round(px * scale);
            Text = Lang.T("Set up Google sign-in"); Icon = Brand.Load(32); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(S(16)); Font = new Font("Segoe UI", 9.5f);
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            Func<string, Label> L = t => new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(S(640), 0), Text = t, Margin = new Padding(0, 0, 0, S(8)) };
            var title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 12.5f), Text = Lang.T("Set up Google sign-in (one time, about 10 minutes)"), Margin = new Padding(0, 0, 0, S(10)) };
            f.Controls.Add(title);
            f.Controls.Add(L(Lang.T("Elixar has no server of its own, so Google needs to know \"Elixar on your PC\" as your own small app. You do this once, in your Google account:")));
            var steps = new[]
            {
                "1. Open console.cloud.google.com and sign in. Create a project named Elixar.",
                "2. APIs & Services > Library: find \"Gmail API\" and click Enable.",
                "3. Google Auth Platform > Get started: app name Elixar, your email, Audience: External.",
                "4. Data access > Add or remove scopes: tick \".../auth/gmail.send\" (send email). Only if you want Email cleaning, also \".../auth/gmail.modify\". Nothing else.",
                "5. Audience > Publish app (In production). Otherwise Google signs Elixar out every 7 days.",
                "6. Clients > Create client > Application type: Desktop app > Create > Download JSON.",
                "7. Click the button below and choose that file.",
            };
            foreach (var st in steps) f.Controls.Add(L(Lang.T(st)));
            f.Controls.Add(L(Lang.T("When you sign in, Google shows \"Google hasn't verified this app\" because the app is yours and private. Click Advanced > Go to Elixar. " +
                                    "Then allow only \"Send email on your behalf\". Elixar can't read your mail.")));
            var status = new Label { AutoSize = true, MaximumSize = new Size(S(640), 0), ForeColor = Color.DimGray, Margin = new Padding(0, S(4), 0, S(8)),
                                     Text = Lang.T(GoogleAuth.HasClient ? "A Google client is already set up. You can replace it." : "No Google client set up yet.") };
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            var btnOpen = new Button { AutoSize = true, Text = Lang.T("Open Google Cloud console") };
            var btnFile = new Button { AutoSize = true, Text = Lang.T("Choose the downloaded file..."), Font = new Font("Segoe UI Semibold", 9.5f) };
            var btnClose = new Button { AutoSize = true, Text = Lang.T("Close"), DialogResult = DialogResult.OK };
            row.Controls.AddRange(new Control[] { btnFile, btnOpen, btnClose });
            f.Controls.Add(status); f.Controls.Add(row);
            Controls.Add(f); CancelButton = btnClose;
            btnOpen.Click += (o, e) => { try { System.Diagnostics.Process.Start(Win.SystemExe("explorer.exe"), "\"https://console.cloud.google.com/\""); } catch { } };
            btnFile.Click += (o, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "Google client file (*.json)|*.json", Title = Lang.T("Choose the file you downloaded from Google") })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    string err = GoogleAuth.ImportClient(d.FileName);
                    status.ForeColor = err == null ? Color.FromArgb(0, 125, 70) : Color.FromArgb(199, 54, 47);
                    status.Text = Lang.T(err == null ? "Done. Close this window and click \"Sign in with Google\"." : err);
                }
            };
        }
    }
}
