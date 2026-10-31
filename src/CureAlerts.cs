// Elixar for Business: email alerts to IT from the Cure Server PC. Every hour a scheduled task (as administrator, through
// Elixar's launcher) reads the PCs' latest reports - the same files the Domain PCs tab shows - and, when a PC newly needs
// attention, sends ONE email listing every new problem. A problem that stays is reminded once a week; optionally an email
// says when it's fixed. It sends through the email account connected in Settings (nothing new is connected here), and only
// when the policy allows email. Reports only: nothing is done on the PCs.

#if BUSINESS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class AlertPrefs
    {
        public bool On, Disk = true, Full = true, Silent = true, Soon, Worn, Fixed = true;
        public static string OverrideFile;   // self-test
        static string File_ { get { return OverrideFile ?? Path.Combine(Paths.Data, "cure-alerts.txt"); } }

        public static AlertPrefs Load()
        {
            var p = new AlertPrefs();
            try
            {
                if (!File.Exists(File_)) return p;
                foreach (var line in File.ReadAllLines(File_))
                {
                    var kv = line.Split(new[] { '=' }, 2); if (kv.Length != 2) continue;
                    bool v = kv[1].Trim() == "1";
                    switch (kv[0].Trim())
                    {
                        case "on": p.On = v; break; case "disk": p.Disk = v; break; case "full": p.Full = v; break; case "silent": p.Silent = v; break;
                        case "soon": p.Soon = v; break; case "worn": p.Worn = v; break; case "fixed": p.Fixed = v; break;
                    }
                }
            }
            catch { }
            return p;
        }

        public void Save()
        {
            Func<bool, string> b = x => x ? "1" : "0";
            SafeFile.Save(File_, "on=" + b(On) + "\ndisk=" + b(Disk) + "\nfull=" + b(Full) + "\nsilent=" + b(Silent) + "\nsoon=" + b(Soon) + "\nworn=" + b(Worn) + "\nfixed=" + b(Fixed) + "\n");
        }
    }

    public class AlertPc { public string Id = "", Name = ""; public CureReport R; public CureRisk Risk; public DateTime Seen = DateTime.MinValue; public List<long[]> History = new List<long[]>(); }
    public class AlertEntry { public string Kind = "", Name = ""; public DateTime Since, LastSent; }
    public class AlertRow { public AlertPc Pc; public string Kind = "", Problem = "", Free = "", ToDo = ""; }
    public class AlertMail
    {
        public List<AlertRow> New = new List<AlertRow>(), Open = new List<AlertRow>(), Fixed = new List<AlertRow>();
        public bool Empty { get { return New.Count + Open.Count + Fixed.Count == 0; } }
        public string Subject = "", Text = "", Html = "";
    }

    public static class CureAlerts
    {
        public const string Task = Automation.Folder + "Cure alerts";
        public static readonly TimeSpan Remind = TimeSpan.FromDays(7);
        public static string StateOverride;                                   // self-test
        public static Func<string, string, string, string> SendOverride;      // self-test: (subject, text, html) -> error or null
        public static Func<string> AccountOverride;                           // self-test: the connected address
        static string StateFile { get { return StateOverride ?? Path.Combine(Paths.Data, "cure-alerts-state.txt"); } }
        static string L(string en, string he) { return Lang.Hebrew && he != null ? he : en; }

        // which alert a PC's state is (null: none, or that kind is turned off)
        public static string KindOf(CureRisk r, AlertPrefs p)
        {
            switch (r)
            {
                case CureRisk.Disk: return p.Disk ? "disk" : null;
                case CureRisk.Full: return p.Full ? "full" : null;
                case CureRisk.Silent: return p.Silent ? "silent" : null;
                case CureRisk.Low: return p.Soon ? "soon" : null;
                case CureRisk.Worn: return p.Worn ? "worn" : null;
                default: return null;
            }
        }

        // ---------- the PCs, as the Domain PCs tab reads them ----------
        public static List<AlertPc> ReadPcs(string dataDir)
        {
            var srv = new CureServer { DataDir = dataDir };
            var list = new List<AlertPc>();
            foreach (var d in srv.Devices().Where(x => !x.Revoked))
            {
                DateTime seen; bool ok = DateTime.TryParse(d.LastSeen ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out seen);
                list.Add(new AlertPc { Id = d.Id, Name = d.Computer, R = srv.Latest(d.Id), Seen = ok ? seen : DateTime.MinValue, History = srv.History(d.Id) });
            }
            string newest = list.Where(p => p.R != null).Select(p => { Version v; return Version.TryParse(p.R.Elixar, out v) ? v : null; }).Where(v => v != null).DefaultIfEmpty(new Version(0, 0)).Max().ToString();
            foreach (var p in list) p.Risk = CureAdvice.Risk(p.R, p.Seen, newest, p.History);
            return list;
        }

        // ---------- the state: what was already sent ----------
        public static Dictionary<string, AlertEntry> LoadState()
        {
            var s = new Dictionary<string, AlertEntry>();
            try
            {
                if (File.Exists(StateFile))
                    foreach (var line in File.ReadAllLines(StateFile))
                    {
                        var f = line.Split('\t'); DateTime a, b;
                        if (f.Length < 5 || !CureProtocol.GoodName(f[4]) && f[4].Length > 0) continue;
                        if (!DateTime.TryParse(f[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out a) || !DateTime.TryParse(f[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out b)) continue;
                        s[f[0]] = new AlertEntry { Kind = f[1], Since = a, LastSent = b, Name = f[4] };
                    }
            }
            catch { }
            return s;
        }

        public static void SaveState(Dictionary<string, AlertEntry> s)
        {
            var sb = new StringBuilder();
            foreach (var kv in s) sb.Append(kv.Key).Append('\t').Append(kv.Value.Kind).Append('\t').Append(kv.Value.Since.ToString("s", CultureInfo.InvariantCulture)).Append('\t')
                                   .Append(kv.Value.LastSent.ToString("s", CultureInfo.InvariantCulture)).Append('\t').Append(kv.Value.Name).Append('\n');
            SafeFile.Save(StateFile, sb.ToString());
        }

        // ---------- what to send now (pure: the self-test drives it day by day) ----------
        public static AlertMail Plan(List<AlertPc> pcs, AlertPrefs p, Dictionary<string, AlertEntry> state, DateTime now, out Dictionary<string, AlertEntry> next)
        {
            var m = new AlertMail();
            next = new Dictionary<string, AlertEntry>();
            foreach (var pc in pcs)
            {
                string kind = KindOf(pc.Risk, p);
                AlertEntry prev; state.TryGetValue(pc.Id, out prev);
                if (kind != null)
                {
                    if (prev == null || prev.Kind != kind) { m.New.Add(Row(pc, kind)); next[pc.Id] = new AlertEntry { Kind = kind, Since = now, LastSent = now, Name = pc.Name }; }
                    else if (now - prev.LastSent >= Remind) { m.Open.Add(Row(pc, kind)); next[pc.Id] = new AlertEntry { Kind = kind, Since = prev.Since, LastSent = now, Name = pc.Name }; }
                    else next[pc.Id] = prev;
                }
                else if (prev != null && p.Fixed) m.Fixed.Add(Row(pc, "ok"));
            }
            // a PC removed from the Cure Server is simply forgotten
            m.New = m.New.OrderByDescending(r => Rank(r.Kind)).ThenBy(r => r.Pc.Name).ToList();
            m.Open = m.Open.OrderByDescending(r => Rank(r.Kind)).ThenBy(r => r.Pc.Name).ToList();
            return m;
        }

        static int Rank(string k) { return k == "disk" ? 5 : k == "full" ? 4 : k == "silent" ? 3 : k == "soon" ? 2 : 1; }

        static AlertRow Row(AlertPc pc, string kind)
        {
            var r = pc.R; int fp = CureAdvice.FreePercent(r);
            var row = new AlertRow { Pc = pc, Kind = kind, Free = r != null && r.Free >= 0 ? Fs.Fmt(r.Free) + (fp >= 0 ? " (" + fp + "%)" : "") : "" };
            switch (kind)
            {
                case "disk": row.Problem = L("Disk failing", "דיסק כושל"); break;
                case "full": row.Problem = L("Almost full", "כמעט מלא"); break;
                case "soon": row.Problem = L("Getting full", "מתמלא"); break;
                case "worn": row.Problem = L("Disk worn", "דיסק שחוק"); break;
                case "silent":
                    int days = pc.Seen == DateTime.MinValue ? -1 : (int)(DateTime.Now - pc.Seen).TotalDays;
                    row.Problem = days >= 0 ? L("Not seen for " + days + " days", "לא דיווח " + days + " ימים") : L("Never reported", "לא דיווח אף פעם"); break;
                default: row.Problem = L("OK again", "תקין שוב"); break;
            }
            if (kind == "silent")
                row.ToDo = L("Last report " + (pc.Seen == DateTime.MinValue ? "never" : pc.Seen.ToString("d MMM", CultureInfo.InvariantCulture)) + ". Is it switched off or gone? Remove it in Domain PCs if it's gone.",
                             "דיווח אחרון " + (pc.Seen == DateTime.MinValue ? "אף פעם" : pc.Seen.ToString("d.M", CultureInfo.InvariantCulture)) + ". האם הוא כבוי או שכבר אינו בשימוש? אם כן, הסר אותו ב-מחשבי הדומיין.");
            else if (kind == "ok")
                row.ToDo = r != null && r.Free >= 0 ? L("C: now " + row.Free + " free.", "ב-C: פנויים עכשיו " + row.Free + ".") : "";
            else
            {
                // what to DO: for a full disk the step that frees the most, not the warning itself
                var tips = CureAdvice.Tips(r, pc.History);
                var tip = kind == "full" || kind == "soon"
                    ? tips.FirstOrDefault(t => t.Saves > 0 && t.Saves < long.MaxValue / 4) ?? tips.FirstOrDefault()
                    : tips.FirstOrDefault();
                row.ToDo = tip != null ? Lang.T(tip.Text) : "";
            }
            return row;
        }

        // ---------- the email ----------
        public static void Compose(AlertMail m, string consoleUrl, string serverName)
        {
            var names = m.New.Concat(m.Open).Select(r => r.Pc.Name).ToList();
            int need = names.Count;
            string list = string.Join(", ", names.Take(3)) + (names.Count > 3 ? L(" and " + (names.Count - 3) + " more", " ועוד " + (names.Count - 3)) : "");
            if (m.New.Count > 0) m.Subject = "Elixar: " + (need == 1 ? L("1 PC needs attention", "מחשב אחד דורש טיפול") : L(need + " PCs need attention", need + " מחשבים דורשים טיפול")) + " — " + list;
            else if (m.Open.Count > 0) m.Subject = "Elixar: " + (need == 1 ? L("1 PC still needs attention", "מחשב אחד עדיין דורש טיפול") : L(need + " PCs still need attention", need + " מחשבים עדיין דורשים טיפול")) + " — " + list;
            else m.Subject = "Elixar: " + string.Join(", ", m.Fixed.Select(r => r.Pc.Name).Take(3)) + " " + (m.Fixed.Count == 1 ? L("is OK again", "תקין שוב") : L("are OK again", "תקינים שוב"));

            string dir = Lang.Hebrew ? " dir=\"rtl\"" : "";
            Func<string, string> H = WebUtility.HtmlEncode;
            Func<string, string> color = k => k == "disk" || k == "full" ? "#c62828" : k == "soon" || k == "worn" ? "#b45f06" : k == "silent" ? "#6b7280" : "#2e7d32";
            var html = new StringBuilder("<!DOCTYPE html><html><body" + dir + " style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1d2230\">");
            var text = new StringBuilder();
            Action<string, string, List<AlertRow>> section = (title, note, rows) =>
            {
                if (rows.Count == 0) return;
                html.Append("<p style=\"margin:16px 0 4px\"><b>").Append(H(title)).Append("</b>").Append(note == null ? "" : " <span style=\"color:#6b7280\">" + H(note) + "</span>").Append("</p>");
                html.Append("<table cellpadding=\"6\" style=\"border-collapse:collapse;font-size:13px;width:100%\"><tr style=\"color:#6b7280;text-align:left\"><th>PC</th><th>")
                    .Append(H(L("Problem", "בעיה"))).Append("</th><th>C:</th><th>").Append(H(L("What to do", "מה לעשות"))).Append("</th></tr>");
                text.Append(title).Append(note == null ? "" : " " + note).Append("\r\n");
                foreach (var r in rows)
                {
                    html.Append("<tr style=\"border-top:1px solid #eef0f3\"><td style=\"white-space:nowrap\"><b>").Append(H(r.Pc.Name)).Append("</b></td><td style=\"white-space:nowrap;color:").Append(color(r.Kind)).Append("\">&#9679; ")
                        .Append(H(r.Problem)).Append("</td><td style=\"white-space:nowrap\">").Append(H(r.Free)).Append("</td><td>").Append(H(r.ToDo)).Append("</td></tr>");
                    text.Append("  ").Append(r.Pc.Name).Append(" - ").Append(r.Problem).Append(r.Free.Length > 0 ? " - C: " + r.Free : "").Append(r.ToDo.Length > 0 ? " - " + r.ToDo : "").Append("\r\n");
                }
                html.Append("</table>"); text.Append("\r\n");
            };
            section(L("New since the last check", "חדש מאז הבדיקה הקודמת"), null, m.New);
            section(L("Still open", "עדיין פתוח"), L("(reminded once a week)", "(תזכורת פעם בשבוע)"), m.Open);
            section(L("Fixed", "תוקן"), null, m.Fixed);
            string foot1 = L("Open the Cure Console: ", "לפתיחת קונסולת Cure: ") + consoleUrl + L(" or the Domain PCs tab in Elixar for Business.", " או לשונית מחשבי הדומיין ב-Elixar for Business.");
            string foot2 = L("Reports only: Elixar didn't change anything on these PCs.", "דיווח בלבד: Elixar לא שינה דבר במחשבים האלה.");
            string foot3 = L("You get this because email alerts are on in Elixar for Business on " + serverName + ". Turn them off in Domain PCs > Email alerts.",
                             "המייל נשלח כי התראות במייל פעילות ב-Elixar for Business במחשב " + serverName + ". אפשר לכבות אותן ב-מחשבי הדומיין > התראות במייל.");
            html.Append("<p style=\"color:#6b7280;font-size:12px;border-top:1px solid #eef0f3;padding-top:8px;margin-top:14px\">").Append(H(foot1)).Append("<br>").Append(H(foot2)).Append("<br>").Append(H(foot3)).Append("</p></body></html>");
            text.Append(foot1).Append("\r\n").Append(foot2).Append("\r\n").Append(foot3).Append("\r\n");
            m.Html = html.ToString(); m.Text = text.ToString();
        }

        static string ServerName() { return Environment.MachineName; }
        static string ConsoleUrl()
        {
            string host = Environment.MachineName; try { host = Dns.GetHostEntry("").HostName; } catch { }
            int port = CureServer.DefaultPort; try { port = CureServer.FromRegistry().Port; } catch { }
            return "https://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
        }

        static string Account() { return AccountOverride != null ? AccountOverride() : MailAccount.Email; }
        static string Send(string subject, string text, string html) { return SendOverride != null ? SendOverride(subject, text, html) : MailAccount.Send(subject, text, html, null); }

        // the hourly run (Elixar.exe --cure-alerts); also called by the self-test with its own data folder
        public static int RunScheduled(string dataDir, DateTime now)
        {
            var p = AlertPrefs.Load();
            if (!p.On) return 0;
            if (!Policy.Allowed("email")) { Automation.Log("Cure alerts: email is turned off by the policy, nothing sent."); return 0; }
            if (Account() == null) { Automation.Log("Cure alerts: no email account is connected (Settings > Email account), nothing sent."); return 0; }
            Dictionary<string, AlertEntry> next;
            var m = Plan(ReadPcs(dataDir), p, LoadState(), now, out next);
            if (m.Empty) { SaveState(next); return 0; }
            Compose(m, ConsoleUrl(), ServerName());
            string err = Send(m.Subject, m.Text, m.Html);
            if (err != null) { Automation.Log("Cure alerts: not sent (" + err + "); trying again next hour."); return 1; }   // the state isn't saved, so the same alert goes next time
            SaveState(next);
            Automation.Log("Cure alerts: sent \"" + m.Subject + "\"");
            using (ChangeLog.In("Cure Server")) ChangeLog.Add("Email", "Sent a Cure alert", Redact.Emails(Account() ?? ""), "", "", m.Subject, false);
            return 0;
        }

        public static string SendTest(string dataDir)
        {
            if (!Policy.Allowed("email")) return MailAccount.OffText;
            if (Account() == null) return "No email account is connected (Settings > Email account).";
            var pcs = ReadPcs(dataDir);
            var m = new AlertMail();
            foreach (var pc in pcs.Where(x => KindOf(x.Risk, new AlertPrefs { Soon = true, Worn = true }) != null).OrderByDescending(x => (int)x.Risk).Take(5)) m.New.Add(Row(pc, KindOf(pc.Risk, new AlertPrefs { Soon = true, Worn = true })));
            Compose(m, ConsoleUrl(), ServerName());
            m.Subject = L("Elixar: test of email alerts", "Elixar: בדיקת התראות במייל") + " (" + pcs.Count + " PCs)";
            if (m.New.Count == 0)
            {
                int at = m.Html.IndexOf('>', m.Html.IndexOf("<body", StringComparison.Ordinal)) + 1;
                m.Html = m.Html.Insert(at, "<p>" + WebUtility.HtmlEncode(L("Email alerts work. No PC needs attention right now.", "התראות במייל עובדות. אף מחשב לא דורש טיפול כרגע.")) + "</p>");
            }
            return Send(m.Subject, L("Email alerts work.", "התראות במייל עובדות.") + "\r\n\r\n" + m.Text, m.Html);
        }

        // the hourly schedule: on with the alerts, gone without them
        public static string Apply(bool on, string exe)
        {
            string o;
            if (on)
            {
                // like every Elixar schedule: it starts the protected copy in Program Files, which only administrators can change
                string ierr = Install.Ensure();
                if (ierr != null) return "Elixar could not put its protected copy in " + Install.Dir + ": " + ierr;
            }
            int code = Automation.RunSchtasks(on ? Automation.CreateCureAlertArgs(exe) : Automation.DeleteArgs(Task), out o);
            return on && code != 0 ? "Windows didn't make the hourly check: " + o : null;
        }
    }

    // ============================ the window (Domain PCs > Email alerts...) ============================
    public class CureAlertsForm : Form
    {
        readonly Func<int, int> S; readonly bool testMode;
        readonly CheckBox on, disk, full, silent, soon, worn, fixd; readonly Label status; readonly Button test, save;
        public string StatusText { get { return status.Text; } }

        public CureAlertsForm(bool testMode, Func<int, int> scale, string dataDir)
        {
            WindowFit.Watch(this);   // fits the screen (a tall window is made smaller; its page scrolls)
            this.testMode = testMode; S = scale;
            Text = Lang.T("Email alerts"); Font = new Font("Segoe UI", 9.5f); StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false; ShowIcon = false; ShowInTaskbar = false; FormBorderStyle = FormBorderStyle.FixedDialog; BackColor = Color.White;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }
            var f = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(S(18), S(14), S(18), S(14)) };
            Func<string, Font, Color, Label> lab = (t, fo, c) => new Label { AutoSize = true, Text = Lang.T(t), Font = fo ?? Font, ForeColor = c, MaximumSize = new Size(S(560), 0), Margin = new Padding(0, S(2), 0, S(2)) };
            var p = AlertPrefs.Load();
            string acct = CureAlerts.AccountOverride != null ? CureAlerts.AccountOverride() : MailAccount.Email;
            f.Controls.Add(lab("Email alerts", new Font("Segoe UI Semibold", 14f), Color.FromArgb(24, 32, 43)));
            f.Controls.Add(lab("Elixar checks every PC's report each hour and emails you when one needs attention. Reports only: nothing is done on the PCs.", null, Color.DimGray));
            on = new CheckBox { AutoSize = true, Text = Lang.T("Email me when a PC needs attention"), Font = new Font("Segoe UI Semibold", 9.5f), Checked = p.On, Margin = new Padding(0, S(10), 0, S(2)) };
            f.Controls.Add(on);
            f.Controls.Add(lab(acct != null ? Lang.T("Send to: ") + acct + Lang.T("  (Settings > Email account)") : "No email account is connected yet: connect one in Settings > Email account first.", null, acct != null ? Color.FromArgb(24, 32, 43) : Color.FromArgb(198, 40, 40)));
            var g = lab("Alert me when", new Font("Segoe UI Semibold", 10f), Color.FromArgb(91, 76, 196)); g.Margin = new Padding(0, S(12), 0, S(2)); f.Controls.Add(g);
            Func<string, bool, CheckBox> cb = (t, v) => { var c = new CheckBox { AutoSize = true, Text = Lang.T(t), Checked = v, Margin = new Padding(S(12), S(1), 0, S(1)) }; f.Controls.Add(c); return c; };
            disk = cb("A disk is failing (right away)", p.Disk);
            full = cb("C: is almost full (under 10% or 10 GB free)", p.Full);
            silent = cb("A PC stopped reporting (not seen for 7 days)", p.Silent);
            soon = cb("C: will be full soon (under 20%, or full within 30 days at its pace)", p.Soon);
            worn = cb("A disk is worn (plan to replace it)", p.Worn);
            var h = lab("How often", new Font("Segoe UI Semibold", 10f), Color.FromArgb(91, 76, 196)); h.Margin = new Padding(0, S(12), 0, S(2)); f.Controls.Add(h);
            var hn = lab("One email lists every new problem (never one email per PC). A problem that is still there is reminded once a week.", null, Color.DimGray); hn.Margin = new Padding(S(12), 0, 0, S(2)); f.Controls.Add(hn);
            fixd = cb("Also email me when a problem is fixed", p.Fixed);
            if (!Policy.Allowed("email"))
            {
                var n = lab("This is a company PC: email is off on company PCs. To use alerts, allow email for this server in Policy manager > Email (allow).", null, Color.FromArgb(150, 90, 0));
                n.BackColor = Color.FromArgb(255, 246, 229); n.Padding = new Padding(S(6)); n.Margin = new Padding(0, S(10), 0, 0); f.Controls.Add(n);
            }
            status = lab("", null, Color.DimGray); f.Controls.Add(status);
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(12), 0, 0) };
            test = new Button { Text = Lang.T("Send a test email"), AutoSize = true, Enabled = acct != null && Policy.Allowed("email") };
            save = new Button { Text = Lang.T("Save"), AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(S(30), S(3), S(3), S(3)) };
            var cancel = new Button { Text = Lang.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            row.Controls.AddRange(new Control[] { test, save, cancel });
            f.Controls.Add(row);
            Controls.Add(f); CancelButton = cancel;
            Action sync = () => { foreach (var c in new[] { disk, full, silent, soon, worn, fixd }) c.Enabled = on.Checked; };
            on.CheckedChanged += (o, e) => sync(); sync();
            test.Click += (o, e) =>
            {
                test.Enabled = false; status.Text = Lang.T("Sending...");
                Task.Factory.StartNew(() => CureAlerts.SendTest(dataDir)).ContinueWith(t => { if (IsDisposed) return; BeginInvoke((Action)(() =>
                {
                    test.Enabled = true; string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                    status.Text = Lang.T(err == null ? "Sent. Check your inbox." : "Not sent: " + err);
                })); });
            };
            save.Click += (o, e) => { string err = SaveNow(); if (err == null) { DialogResult = DialogResult.OK; Close(); } else status.Text = Lang.T(err); };
            Shown += (o, e) => { Theme.Apply(this); Theme.DarkTitle(this); };
        }

        // saves the choices and puts the hourly check in Task Scheduler (or takes it out)
        public string SaveNow()
        {
            var p = new AlertPrefs { On = on.Checked, Disk = disk.Checked, Full = full.Checked, Silent = silent.Checked, Soon = soon.Checked, Worn = worn.Checked, Fixed = fixd.Checked };
            try { p.Save(); } catch (Exception ex) { return "Could not save: " + ex.Message; }
            using (ChangeLog.In("Cure Server")) ChangeLog.Add("Settings", p.On ? "Turned on Cure email alerts" : "Turned off Cure email alerts", "Domain PCs > Email alerts", "", "", "", false);
            if (testMode) return null;
            return CureAlerts.Apply(p.On, Install.Exe);
        }

        public void SetChoices(bool o, bool soonToo) { on.Checked = o; soon.Checked = soonToo; }
    }

    public partial class MainForm
    {
        public CureAlertsForm LastAlertsForm;   // self-test
        public void OpenAlerts()
        {
            using (var f = new CureAlertsForm(testMode, S, DomainDataDir))
            {
                LastAlertsForm = f;
                if (testMode) return;
                f.ShowDialog(this);
            }
        }
    }
}
#endif
