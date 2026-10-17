// Settings > Email reports: sign in with Google, choose which emails to get.

using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        Label mailStatus; Button btnMailSignIn, btnMailSetup, btnMailTest, btnMailOut, btnMailSave;
        CheckBox chkMailClean, chkMailAlerts, chkMailSummary; ComboBox cmbMailDay; DateTimePicker timeMail;
        CancellationTokenSource mailCts;

        Control BuildEmailBox()
        {
            var box = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            mailStatus = new Label { AutoSize = true, MaximumSize = new Size(S(900), 0), Margin = new Padding(S(2), 0, 0, S(6)), Text = "" };
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            btnMailSignIn = MakeButton("Sign in with Google"); btnMailSignIn.Font = new Font("Segoe UI Semibold", 9f);
            btnMailSetup = MakeButton("Set up Google sign-in (one time)...");
            btnMailTest = MakeButton("Send a test email");
            btnMailOut = MakeButton("Sign out");
            row.Controls.AddRange(new Control[] { btnMailSignIn, btnMailSetup, btnMailTest, btnMailOut });
            var p = MailPrefs.Load();
            var choose = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(S(2), S(8), 0, 0) };
            chkMailClean = new CheckBox { AutoSize = true, Checked = p.CleanReport, Text = "Weekly auto-clean report: what was cleaned, how much it freed, and what is left for you to decide" };
            chkMailAlerts = new CheckBox { AutoSize = true, Checked = p.Alerts, Text = "Alerts: C: almost full (your limit in Automation, or 15%), or a drive that may fail" };
            var sumRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            chkMailSummary = new CheckBox { AutoSize = true, Checked = p.Summary, Text = "Weekly summary every", Margin = new Padding(S(3), S(5), S(4), 0) };
            cmbMailDay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(110) };
            cmbMailDay.Items.AddRange(Automation.Days); cmbMailDay.SelectedIndex = Math.Max(0, Array.IndexOf(Automation.Days, p.Day));
            timeMail = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = S(70) };
            DateTime tm; timeMail.Value = DateTime.TryParse(p.Time, out tm) ? DateTime.Today.Add(tm.TimeOfDay) : DateTime.Today.AddHours(9);
            var at = new Label { AutoSize = true, Text = "at", Margin = new Padding(S(4), S(6), S(4), 0) };
            var also = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "(alerts are checked daily at this time too)", Margin = new Padding(S(6), S(6), 0, 0) };
            var row2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            sumRow.Controls.AddRange(new Control[] { chkMailSummary, cmbMailDay, at, timeMail, also });
            btnMailSave = MakeButton("Save email settings"); btnMailSave.Margin = new Padding(S(2), S(8), 0, 0);
            row2.Controls.Add(btnMailSave);
            choose.Controls.AddRange(new Control[] { chkMailClean, chkMailAlerts, sumRow, row2 });
            var note = Note("Elixar can only send email, never read your inbox. Emails go from your Gmail to you only, list categories and sizes (never your file names), " +
                            "and never contain a link: to delete anything, open Elixar. Your Google sign-in is encrypted on this PC in a folder only administrators can open.");
            note.Margin = new Padding(S(2), S(8), 0, 0);
            box.Controls.AddRange(new Control[] { mailStatus, row, choose, note });

            btnMailSetup.Click += (o, e) => { using (var f = new GoogleSetupForm(s)) { f.Shown += (o2, e2) => { Theme.Apply(f); Lang.Apply(f); }; f.ShowDialog(this); } UpdateMailUi(); };
            btnMailSignIn.Click += (o, e) => SignInGoogle();
            btnMailOut.Click += (o, e) =>
            {
                if (Lang.Box(this, "Sign out of Google? Elixar stops sending emails and Google cancels its permission.", "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                GoogleAuth.SignOut();
                string o2; Automation.RunSchtasks(Automation.DeleteArgs(Automation.MailTask), out o2);
                Log("Email reports: signed out of Google.");
                UpdateMailUi();
            };
            btnMailTest.Click += (o, e) => RunMail("Sending a test email...", () => MailReports.Test().Send(), "Test email sent to " + GoogleAuth.SignedInEmail + ".");
            btnMailSave.Click += (o, e) => SaveMail();
            UpdateMailUi();
            return box;
        }

        void UpdateMailUi()
        {
            string who = GoogleAuth.SignedInEmail;
            bool signed = who != null, busy = mailCts != null;
            int level = 0;
            string health = signed ? MailStatus.Line(out level) : null;
            mailStatus.ForeColor = Theme.MapFore(!signed ? Color.DimGray : level == 2 ? DangerColor : level == 1 ? CautionColor : SafeColor);
            mailStatus.Text = Lang.T(signed ? "Signed in as " + who + " (Google). Emails go to this address." :
                                     GoogleAuth.HasClient ? "Not signed in." : "Not signed in. Set up Google sign-in once, then sign in.") +
                              (signed ? "\n" + Lang.T(health) : "");
            btnMailSignIn.Text = Lang.T(busy ? "Cancel sign-in" : signed ? "Sign in again" : "Sign in with Google");
            btnMailSignIn.Enabled = (GoogleAuth.HasClient || busy) && !testMode;
            btnMailSetup.Visible = !signed && GoogleAuth.BuiltInClient == null;   // the GitHub build has the Google app built in
            btnMailTest.Enabled = btnMailOut.Enabled = signed && !busy && !testMode;
            btnMailSave.Enabled = signed && !busy && !testMode;
            foreach (Control c in new Control[] { chkMailClean, chkMailAlerts, chkMailSummary, cmbMailDay, timeMail }) c.Enabled = signed;
        }

        void SignInGoogle()
        {
            if (mailCts != null) { mailCts.Cancel(); return; }
            mailCts = new CancellationTokenSource();
            var token = mailCts.Token;
            UpdateMailUi();
            bool keepMailbox = GoogleAuth.MailboxAccess;   // signing in again keeps Email cleaning if it was on
            Task.Factory.StartNew(() => GoogleAuth.SignIn(st => UI(() => mailStatus.Text = Lang.T(st)), token, keepMailbox), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                mailCts = null;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                UpdateMailUi();
                if (err != null) { Lang.Box(this, "Google sign-in didn't finish: " + err, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                Log("Email reports: signed in as " + GoogleAuth.SignedInEmail + ".");
                SaveMail();
                RunMail("Sending a test email...", () => MailReports.Test().Send(), "Signed in. A test email was sent to " + GoogleAuth.SignedInEmail + ".");
                Activate();
            }));
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
            if (testMode || GoogleAuth.SignedInEmail == null) return;
            var p = MailPrefs.Load();
            p.CleanReport = chkMailClean.Checked; p.Alerts = chkMailAlerts.Checked; p.Summary = chkMailSummary.Checked;
            p.Day = Automation.Days[Math.Max(0, cmbMailDay.SelectedIndex)];   // by position: the names are translated in Hebrew p.Time = timeMail.Value.ToString("HH:mm");
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
