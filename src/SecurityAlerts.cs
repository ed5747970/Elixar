// Security and PC health alerts: once a day (with the low-space check) Elixar reads what PC health reads and tells
// you at once when something important changes: no antivirus on, the firewall off for public networks, a new blue
// screen, or PC health down 10 points or more. It only reads and tells; nothing is changed.
// One alert per problem: again only after it was fixed and came back. With email connected, the daily email task
// sends the same alerts. What was already said is kept in Elixar's settings for your account (the check runs
// without administrator rights, as you).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CDriveCleaner
{
    public class SecAlert { public string Key = "", Title = "", Text = ""; public bool Bad; }

    public static class SecurityAlerts
    {
        public static Func<string, string> GetOverride;            // self-test: settings
        public static Action<string, string> SetOverride;
        static string Get(string k, string d) { return GetOverride != null ? (GetOverride(k) ?? d) : Settings.Get(k, d); }
        static void Set(string k, string v) { if (SetOverride != null) SetOverride(k, v); else Settings.Set(k, v); }
        const char Sep = '\u001f';

        // on unless you turned it off (Automation); the company policy can fix it on or off
        public static bool On
        {
            get
            {
                string p = Policy.Get("sec.alerts");
                if (p == "on") return true; if (p == "off") return false;
                return Get("SecurityAlerts", "1") == "1";
            }
        }
        public static void SetOn(bool on) { Set("SecurityAlerts", on ? "1" : "0"); }

        // pure: what to tell now, from this check, what was already told, and the score before it
        public static List<SecAlert> Decide(HealthReport r, Dictionary<string, string> state, int before, DateTime now)
        {
            var list = new List<SecAlert>();
            if (r == null) return list;
            Func<string, HFind> bad = k => r.Items.FirstOrDefault(i => i.Key == k && i.Level == HLevel.Bad && !i.Ignored);
            var active = new List<string>();
            var said = new HashSet<string>(Val(state, "active").Split(',').Where(x => x.Length > 0));
            var av = bad("av");
            if (av != null) { active.Add("av"); if (!said.Contains("av")) list.Add(new SecAlert { Key = "av", Bad = true, Title = H("Your antivirus is off", "האנטי-וירוס כבוי"), Text = av.Detail }); }
            var fw = bad("fw");
            if (fw != null) { active.Add("fw"); if (!said.Contains("fw")) list.Add(new SecAlert { Key = "fw", Bad = true, Title = fw.Title, Text = fw.Detail }); }
            state["active"] = string.Join(",", active);   // fixed problems leave the list: if one comes back, it is told again

            // a new blue screen (in the last 2 days, newer than the last one told)
            if (r.Facts != null)
            {
                var blue = r.Facts.Crashes.Where(c => c.Kind == "bluescreen").OrderByDescending(c => c.When).FirstOrDefault();
                DateTime told; bool has = DateTime.TryParse(Val(state, "blue"), CultureInfo.InvariantCulture, DateTimeStyles.None, out told);
                if (blue != null && (now - blue.When).TotalDays <= 2 && (!has || blue.When > told))
                {
                    list.Add(new SecAlert { Key = "bluescreen", Bad = true, Title = H("Windows stopped with a blue screen", "Windows נעצר במסך כחול"),
                                            Text = H("At " + blue.When.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) + (blue.Code.Length > 0 ? " (" + blue.Code + ")" : "") + ". PC health shows what usually causes it.",
                                                     "ב-" + blue.When.ToString("d.M HH:mm", CultureInfo.InvariantCulture) + (blue.Code.Length > 0 ? " (" + blue.Code + ")" : "") + ". בריאות המחשב מראה מה בדרך כלל גורם לזה.") });
                    state["blue"] = blue.When.ToString("s", CultureInfo.InvariantCulture);
                }
            }

            // PC health down 10 points or more since the last reading (at most once a week)
            DateTime dropAt; bool dropped = DateTime.TryParse(Val(state, "drop"), CultureInfo.InvariantCulture, DateTimeStyles.None, out dropAt);
            if (before >= 0 && r.Total >= 0 && before - r.Total >= 10 && (!dropped || (now - dropAt).TotalDays >= 7))
            {
                var why = r.Items.Where(i => !i.Ignored && (i.Level == HLevel.Bad || i.Level == HLevel.Warn)).OrderByDescending(i => (int)i.Level).ThenByDescending(i => i.Penalty).Take(2).Select(i => i.Title).ToList();
                list.Add(new SecAlert { Key = "drop", Title = H("PC health dropped: " + before + " → " + r.Total, "בריאות המחשב ירדה: " + before + " → " + r.Total),
                                        Text = (why.Count > 0 ? string.Join("; ", why) + ". " : "") + H("Click to see.", "לחצו כדי לראות.") });
                state["drop"] = now.ToString("s", CultureInfo.InvariantCulture);
            }
            return list;
        }

        static string Val(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) && v != null ? v : ""; }
        static string H(string en, string he) { return Lang.Hebrew ? he : en; }

        static Dictionary<string, string> Load()
        {
            return new Dictionary<string, string> { { "active", Get("SecAlertActive", "") }, { "blue", Get("SecAlertBlue", "") }, { "drop", Get("SecAlertDrop", "") } };
        }
        static void Save(Dictionary<string, string> s)
        {
            Set("SecAlertActive", Val(s, "active")); Set("SecAlertBlue", Val(s, "blue")); Set("SecAlertDrop", Val(s, "drop"));
        }

        public static Func<HealthReport> CheckOverride; public static Action<SecAlert> NotifyOverride;   // self-test

        // the daily check (from the low-space check, as you)
        public static int Run(Action<string> log)
        {
            if (!On || (!Win.IsWindows && CheckOverride == null)) return 0;
            try
            {
                // compared with this check's own last score (same account, same rights), not PC health's
                int before; if (!int.TryParse(Get("SecAlertScore", ""), out before)) before = -1;
                var r = CheckOverride != null ? CheckOverride() : Health.CheckQuiet();
                var state = Load();
                var alerts = Decide(r, state, before, DateTime.Now);
                Save(state);
                if (r != null && r.Total >= 0) Set("SecAlertScore", r.Total.ToString(CultureInfo.InvariantCulture));
                foreach (var a in alerts.Take(3))
                {
                    if (log != null) log("Security alert: " + a.Title);
                    if (NotifyOverride != null) NotifyOverride(a);
                    else Automation.Notify(a.Title, a.Text + (Lang.Hebrew ? " לחצו כדי לפתוח את Elixar." : " Click to open Elixar."), a.Bad ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Warning, true);
                }
                if (alerts.Count > 0) Queue(alerts);
                return alerts.Count;
            }
            catch (Exception e) { if (log != null) log("Security alerts: " + e.Message); return 0; }
        }

        // for the email: kept until the daily email task (which has the sign-in) sends them
        static void Queue(List<SecAlert> alerts)
        {
            var have = Pending();
            have.AddRange(alerts.Select(a => DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\t" + a.Title.Replace("\t", " ") + "\t" + a.Text.Replace("\t", " ")));
            Set("SecAlertMail", string.Join(Sep.ToString(), have.Skip(Math.Max(0, have.Count - 10))));
        }
        // only alerts from the last 3 days (an old one, say from before email was connected, isn't news)
        public static List<string> Pending()
        {
            return Get("SecAlertMail", "").Split(Sep).Where(x =>
            {
                DateTime t; return x.Length > 0 && DateTime.TryParse(x.Split('\t')[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && (DateTime.Now - t).TotalDays <= 3;
            }).ToList();
        }
        public static void Sent() { Set("SecAlertMail", ""); }
    }
}
