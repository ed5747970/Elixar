// A gentle invitation to connect email: shown once after the first scan, never at start-up and never by a scheduled run.
// "Not now" asks again after 14 days, three times at most; "Don't ask again" never asks again. Nothing connects unless
// the person clicks "Connect email..." and then signs in themselves in Settings > Email account.

using System;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class MailNudge
    {
        public const int Every = 14, MaxAsks = 3;
        // saved state: "" never asked | "never" | "later|yyyy-MM-dd|count"
        public static bool Due(string state, DateTime today, bool connected)
        {
            if (connected || state == "never") return false;
            if (string.IsNullOrEmpty(state)) return true;
            var p = state.Split('|'); DateTime last; int n;
            if (p.Length != 3 || p[0] != "later" || !DateTime.TryParseExact(p[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last) || !int.TryParse(p[2], out n)) return true;
            return n < MaxAsks && (today.Date - last.Date).TotalDays >= Every;
        }

        public static string After(string state, DialogResult choice, DateTime today)
        {
            if (choice == DialogResult.Cancel) return "never";   // "Don't ask again"
            int n = 0; var p = (state ?? "").Split('|');
            if (p.Length == 3) int.TryParse(p[2], out n);
            return "later|" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "|" + (n + 1).ToString(CultureInfo.InvariantCulture);
        }

        // built from lines that are translated one by one, so the list keeps its layout in Hebrew too
        public static string Text(Func<string, string> T)
        {
            return T("Connect your email to get:") + "\n\n" +
                   string.Join("\n", new[] { "A weekly summary of your PC's space", "An alert before the system drive gets full", "A report after each automatic clean",
                                             "Email cleaning: old ads, social updates and spam (only if you turn it on)" }.Select(x => "  - " + T(x))) + "\n\n" +
                   T("Elixar emails only you, never puts a link in an email, and can't read your mailbox unless you turn on Email cleaning. You can disconnect any time in Settings.");
        }
    }

    public partial class MainForm
    {
        bool nudgeDone;

        // called when the Dashboard is filled in: asks once the person is looking at Elixar, a few seconds later
        void MaybeNudgeMail(DashSummary m)
        {
            if (nudgeDone || testMode || m == null || !m.Measured || m.MailEmail != null || !Policy.Allowed("email")) return;
            string state = Settings.Get("MailNudge", "");
            if (!MailNudge.Due(state, DateTime.Today, false)) { nudgeDone = true; return; }
            nudgeDone = true;
            var t = new Timer { Interval = 4000 };
            t.Tick += (o, e) =>
            {
                t.Stop(); t.Dispose();
                // only when Elixar is the window in front and no other question is open
                if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized || Form.ActiveForm != this || cleaningNow) { nudgeDone = false; return; }
                if (MailAccount.Email != null) return;
                var r = NiceBox.ShowWith(this, MailNudge.Text(Lang.T), Lang.MailT("Get more from Elixar"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information, 0,
                                         new[] { Lang.MailT("Connect email..."), Lang.MailT("Not now"), Lang.MailT("Don't ask again") });
                Settings.Set("MailNudge", MailNudge.After(state, r, DateTime.Today));
                if (r == DialogResult.Yes) OpenMailSettings();
            };
            t.Start();
        }

        public void OpenMailSettings()
        {
            ShowSettings();
            try
            {
                Control c = mailStatus;
                while (c != null && !(c is ScrollableControl && ((ScrollableControl)c).AutoScroll)) c = c.Parent;
                if (c != null) ((ScrollableControl)c).ScrollControlIntoView(mailStatus);
            }
            catch { }
        }
    }
}
