// What Elixar emails you: the weekly auto-clean report, alerts (C: almost full, a drive that may fail) and a weekly summary.
// Only categories and sizes, never file names or paths, and never a link.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace CDriveCleaner
{
    public class MailPrefs
    {
        public bool CleanReport = true, Alerts = true, Summary = true;
        public string Day = "Sunday", Time = "09:00";
        public DateTime LastSummary, LastSpaceAlert;
        public string DiskStamp = "";

        static string File_ { get { var d = GoogleAuth.Dir; return d == null ? null : Path.Combine(d, "prefs.txt"); } }

        // the day and time come from Policy manager once the schedules moved there
        public static MailPrefs Load()
        {
            var p = LoadSaved();
            try { if (Policy.Moved) { p.Day = Policy.Get("sched.mail.day"); p.Time = Policy.Get("sched.mail.time"); } } catch { }
            return p;
        }

        static MailPrefs LoadSaved()
        {
            var p = new MailPrefs();
            try
            {
                string f = File_;
                if (f == null || !File.Exists(f)) return p;
                foreach (var line in File.ReadAllLines(f))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    DateTime d;
                    switch (k)
                    {
                        case "clean": p.CleanReport = v == "1"; break;
                        case "alerts": p.Alerts = v == "1"; break;
                        case "summary": p.Summary = v == "1"; break;
                        case "day": if (Automation.Days.Contains(v)) p.Day = v; break;
                        case "time": if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d\d:\d\d$")) p.Time = v; break;
                        case "lastSummary": if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) p.LastSummary = d; break;
                        case "lastSpace": if (DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) p.LastSpaceAlert = d; break;
                        case "disk": p.DiskStamp = v; break;
                    }
                }
            }
            catch { }
            return p;
        }

        public string Choices() { return "Clean report " + (CleanReport ? "on" : "off") + ", alerts " + (Alerts ? "on" : "off") + ", weekly summary " + (Summary ? Day + " " + Time : "off"); }

        public void Save()
        {
            string f = File_;
            if (f == null) return;
            string before = null; try { before = Load().Choices(); } catch { }
            if (before != null && before != Choices()) ChangeLog.Add("Settings", "Changed the email reports", "Email", before, Choices(), "", false);
            Func<DateTime, string> D = d => d == DateTime.MinValue ? "" : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            File.WriteAllText(f, "clean=" + (CleanReport ? 1 : 0) + "\nalerts=" + (Alerts ? 1 : 0) + "\nsummary=" + (Summary ? 1 : 0) + "\nday=" + Day + "\ntime=" + Time +
                                 "\nlastSummary=" + D(LastSummary) + "\nlastSpace=" + D(LastSpaceAlert) + "\ndisk=" + DiskStamp + "\n");
        }

        // the weekly summary goes out on the chosen day, or as soon as possible if that day was missed (PC off)
        public static bool SummaryDue(DateTime last, DateTime today, string day)
        {
            if (last == DateTime.MinValue) return true;
            double since = (today.Date - last.Date).TotalDays;
            return (today.DayOfWeek.ToString() == day && since >= 6) || since >= 8;
        }
    }

    public static class MailReports
    {
        static string L(string en, string he)
        {
            if (Lx.On)   // Linux has no C: - the system disk
            {
                en = en.Replace("The cure for a full C: drive", "The cure for a full disk").Replace("Drive C: is almost full", "Your system disk is almost full").Replace(" C:", " the system disk");
                he = he.Replace("התרופה לכונן C: מלא", "התרופה לדיסק מלא").Replace("כונן C: כמעט מלא", "דיסק המערכת כמעט מלא").Replace("ב-C:", "בדיסק המערכת").Replace("C:", "דיסק המערכת");
            }
            return Lang.Hebrew ? he : en;
        }
        static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }
        static string Pct(long part, long whole) { return whole <= 0 ? "?" : Math.Round(part * 100.0 / whole) + "%"; }

        // ---------- a simple email: the same content as HTML and as plain text ----------
        public class Doc
        {
            readonly StringBuilder h = new StringBuilder(), t = new StringBuilder();
            public readonly string Subject;
            public Doc(string subject, string accent)
            {
                Subject = subject;
                string dir = Lang.Hebrew ? "rtl" : "ltr";
                h.Append("<!doctype html><html dir=\"").Append(dir).Append("\"><head><meta charset=\"utf-8\"></head><body style=\"margin:0;background:#f6f8fb;font-family:Segoe UI,Arial,sans-serif;color:#18202b\">");
                h.Append("<div style=\"max-width:640px;margin:0 auto;padding:20px\">");
                h.Append("<div style=\"background:linear-gradient(90deg,#261e5c,#14465a);color:#fff;border-radius:10px 10px 0 0;padding:14px 20px;font-size:18px;font-weight:600\">Elixar");
                h.Append("<span style=\"font-weight:400;font-size:13px;opacity:.8\"> &nbsp;").Append(H(L("The cure for a cluttered PC", "התרופה למחשב עמוס"))).Append("</span></div>");
                h.Append("<div style=\"background:#fff;border-radius:0 0 10px 10px;padding:18px 22px;border-top:4px solid ").Append(accent).Append("\">");
                h.Append("<h2 style=\"margin:0 0 10px;font-size:20px\">").Append(H(subject)).Append("</h2>");
                t.Append(subject).Append("\r\n").Append(new string('=', Math.Min(60, subject.Length))).Append("\r\n\r\n");
            }
            public readonly List<Mailer.Inline> Images = new List<Mailer.Inline>();
            // a picture made on this PC, carried inside the email (not loaded from the internet)
            public Doc Image(byte[] png, string alt)
            {
                if (!TrendChart.IsPng(png)) return this;
                string id = "chart" + Images.Count + "@elixar";
                Images.Add(new Mailer.Inline { Id = id, Png = png });
                h.Append("<img src=\"cid:").Append(id).Append("\" width=\"560\" alt=\"").Append(H(alt)).Append("\" style=\"display:block;width:100%;max-width:560px;height:auto;margin:6px 0 10px;border:1px solid #e6e9ef;border-radius:6px\">");
                return this;
            }
            public Doc P(string s) { h.Append("<p style=\"margin:8px 0;font-size:14px;line-height:1.45\">").Append(H(s)).Append("</p>"); t.Append(s).Append("\r\n\r\n"); return this; }
            public Doc Heading(string s) { h.Append("<h3 style=\"margin:18px 0 6px;font-size:15px\">").Append(H(s)).Append("</h3>"); t.Append(s.ToUpperInvariant()).Append("\r\n"); return this; }
            public Doc Big(string label, string value, string color)
            {
                h.Append("<div style=\"display:inline-block;margin:4px 18px 8px 0\"><div style=\"font-size:12px;color:#666\">").Append(H(label)).Append("</div><div style=\"font-size:24px;font-weight:600;color:")
                 .Append(color).Append("\"><span dir=\"ltr\" style=\"unicode-bidi:isolate\">").Append(H(value)).Append("</span></div></div>");
                t.Append(label).Append(": ").Append(value).Append("\r\n");
                return this;
            }
            public Doc Table(IEnumerable<string[]> rows)
            {
                var list = rows.ToList();
                if (list.Count == 0) return this;
                h.Append("<table style=\"border-collapse:collapse;width:100%;font-size:13px;margin:4px 0 8px\">");
                foreach (var r in list)
                {
                    h.Append("<tr>");
                    for (int i = 0; i < r.Length; i++)
                        h.Append("<td style=\"padding:5px 6px;border-bottom:1px solid #e6e9ef;").Append(i == 1 ? "text-align:" + (Lang.Hebrew ? "left" : "right") + ";white-space:nowrap;font-weight:600" : i == 2 ? "color:#666" : "").Append("\">").Append(i == 1 ? "<span dir=\"ltr\" style=\"unicode-bidi:isolate\">" + H(r[i]) + "</span>" : H(r[i])).Append("</td>");
                    h.Append("</tr>");
                    t.Append("  - ").Append(string.Join("  |  ", r.Where(x => !string.IsNullOrEmpty(x)))).Append("\r\n");
                }
                h.Append("</table>");
                t.Append("\r\n");
                return this;
            }
            public string Html()
            {
                string foot = L("Sent by Elixar on " + Environment.MachineName + ", only to you. Elixar never asks you to click a link or sign in from an email, so an email asking for that isn't from Elixar. " +
                                "To delete anything, open Elixar on your PC. To change or stop these emails: Elixar > Settings > Email reports.",
                                "נשלח על ידי Elixar במחשב " + Environment.MachineName + ", רק אליך. Elixar אף פעם לא מבקש ללחוץ על קישור או להתחבר מתוך מייל, כך שמייל שמבקש זאת אינו מ-Elixar. " +
                                "כדי למחוק משהו, פתחו את Elixar במחשב. כדי לשנות או להפסיק את המיילים: Elixar > הגדרות > דוחות במייל.");
                return h.ToString() + "<p style=\"margin:18px 0 0;font-size:11px;color:#888;line-height:1.4\">" + H(foot) + "</p></div></div></body></html>";
            }
            public string Text()
            {
                return t.ToString() + "--\r\n" + L("Sent by Elixar on " + Environment.MachineName + ". Elixar never asks you to click a link or sign in from an email. Change or stop these emails: Elixar > Settings > Email reports.",
                                                   "נשלח על ידי Elixar במחשב " + Environment.MachineName + ". Elixar אף פעם לא מבקש ללחוץ על קישור או להתחבר מתוך מייל. שינוי או הפסקה: Elixar > הגדרות > דוחות במייל.");
            }
            public string Send() { return MailAccount.Send(Subject, Text(), Html(), Images); }
        }

        // ---------- what can be deleted ----------
        public static List<CleanItem> Measure()
        {
            var items = Catalog.Build().Where(i => !i.Privacy).ToList();
            foreach (var it in items)
            {
                try { it.Size = it.Measure(); it.SizeUnknown = it.Size < 0; } catch { it.Size = -1; it.SizeUnknown = true; }
            }
            return items;
        }

        static IEnumerable<string[]> Rows(IEnumerable<CleanItem> items, int max, string note)
        {
            return items.Where(i => i.Size > (1L << 20)).OrderByDescending(i => i.Size).Take(max).Select(i => new[] { Lang.T(i.Name), Fs.Fmt(i.Size), note });
        }

        static void WhatToDelete(Doc d, List<CleanItem> items)
        {
            var safe = items.Where(i => i.Tier == Tier.Safe && i.Size > 0).ToList();
            var review = items.Where(i => i.Tier == Tier.Review && i.Size > 0).ToList();
            var caution = items.Where(i => i.Tier == Tier.Caution && i.Size > 0).ToList();
            d.Heading(L("What you can delete", "מה אפשר למחוק"));
            d.Big(L("Safe to delete", "בטוח למחיקה"), Fs.Fmt(safe.Sum(i => i.Size)), "#007d46");
            d.Big(L("Your decision", "ההחלטה שלך"), Fs.Fmt(review.Sum(i => i.Size)), "#285ac8");
            if (caution.Sum(i => i.Size) > (100L << 20)) d.Big(L("With care", "בזהירות"), Fs.Fmt(caution.Sum(i => i.Size)), "#be6900");
            d.Table(Rows(safe, 6, L("safe, rebuilt automatically", "בטוח, נבנה מחדש אוטומטית"))
                .Concat(Rows(review, 4, L("your files: check first", "הקבצים שלך: לבדוק קודם")))
                .Concat(Rows(caution, 2, L("read the note in Elixar first", "לקרוא קודם את ההערה ב-Elixar"))));
            d.P(L("Open Elixar > Cleaner to delete them. Nothing is deleted from an email.", "פתחו את Elixar > ניקוי כדי למחוק אותם. שום דבר לא נמחק מתוך מייל."));
        }

        static void SpaceLines(Doc d, long free, long total)
        {
            d.Big(L("Free on C:", "פנוי ב-C:"), Fs.Fmt(free) + " (" + Pct(free, total) + ")", free * 100.0 / Math.Max(1, total) < 10 ? "#c7362f" : "#18202b");
            var hist = SpaceHistory.Read();
            var fc = SpaceHistory.Predict(hist, DateTime.Today);
            var weekAgo = hist.Where(p => p.Day <= DateTime.Today.AddDays(-6)).OrderByDescending(p => p.Day).FirstOrDefault();
            if (weekAgo != null)
            {
                long ch = free - weekAgo.Free;
                d.Big(L("Change in a week", "שינוי בשבוע"), (ch >= 0 ? "+" : "-") + Fs.Fmt(Math.Abs(ch)), ch < -(1L << 30) ? "#be6900" : "#18202b");
            }
            if (fc.Text.Length > 0) d.P(Lang.T(fc.Text));
        }

        static void DiskLines(Doc d, DiskReport r)
        {
            if (r == null || r.Error != null || r.Disks.Count == 0) return;
            d.Heading(L("Disk health", "בריאות הכוננים"));
            d.Table(r.Disks.Select(x => new[] { x.Label, Lang.T(DiskHealth.LevelText(x.Level)),
                (x.LifeLeft >= 0 ? L(x.LifeLeft + "% life left", "נותרו " + x.LifeLeft + "% חיים") : "") + (x.Temp > 0 ? (x.LifeLeft >= 0 ? ", " : "") + x.Temp + " °C" : "") }));
        }

        // ---------- the four kinds of email ----------
        public static Doc Summary(long free, long total, List<CleanItem> items, DiskReport disks, string lastClean)
        {
            var d = new Doc(L("Your weekly Elixar summary", "הסיכום השבועי של Elixar"), "#5b4cc4");
            SpaceLines(d, free, total);
            try { d.Image(TrendChart.Png(SpaceHistory.Read(), DateTime.Today, 56), L("Free space on C: over the last 8 weeks", "השטח הפנוי ב-C: ב-8 השבועות האחרונים")); } catch { }
            if (lastClean != null) d.P(lastClean);
            WhatToDelete(d, items);
            WatchLines(d);
            DiskLines(d, disks);
            return d;
        }

        // the folders that keep growing, with how often to look at them (Dashboard > Watch)
        static void WatchLines(Doc d)
        {
            if (!Win.IsWindows && Growth.OverrideFile == null) return;
            List<Growth.Watch> w;
            try { w = Growth.Watchlist(DateTime.Today); } catch { return; }
            if (w.Count == 0) return;
            d.Heading(L("Keep an eye on", "במעקב"));
            d.P(L("These folders keep growing. Look at them as often as shown, in Elixar > Dashboard > Watch.", "התיקיות האלה ממשיכות לגדול. בדקו אותן בתדירות שמוצגת, ב-Elixar > לוח בקרה > במעקב."));
            d.Table(w.Take(5).Select(x => new[] { Lang.T(x.Name), "+" + Fs.Fmt(Math.Max(0, x.PerMonth)) + L(" a month", " בחודש"),
                                                  x.Due ? L("look now (" + Growth.EveryText(x.EveryDays) + ")", "לבדוק עכשיו (" + Lang.T(Growth.EveryText(x.EveryDays)) + ")")
                                                        : L("next look " + x.NextCheck.ToString("d MMM"), "הבדיקה הבאה " + x.NextCheck.ToString("d.M")) }));
        }

        public class CleanResult { public string Name; public long Freed; public string Skipped; }

        public static Doc CleanReport(List<CleanResult> results, long freed, long free, long total, List<CleanItem> left)
        {
            var d = new Doc(L("Weekly clean: freed " + Fs.Fmt(freed), "ניקוי שבועי: התפנו " + Fs.Fmt(freed)), "#007d46");
            d.Big(L("Freed", "התפנו"), Fs.Fmt(freed), "#007d46");
            d.Big(L("Free on C: now", "פנוי ב-C: עכשיו"), Fs.Fmt(free) + " (" + Pct(free, total) + ")", "#18202b");
            d.Heading(L("What was cleaned", "מה נוקה"));
            d.Table(results.Where(r => r.Skipped == null && r.Freed > 0).OrderByDescending(r => r.Freed).Select(r => new[] { Lang.T(r.Name), Fs.Fmt(r.Freed), "" }));
            var skipped = results.Where(r => r.Skipped != null).ToList();
            if (skipped.Count > 0)
            {
                d.Heading(L("Skipped", "דולגו"));
                d.Table(skipped.Select(r => new[] { Lang.T(r.Name), "", r.Skipped }));
            }
            if (left != null) WhatToDelete(d, left.Where(i => i.Tier != Tier.Safe || !i.DefaultOn).ToList());
            return d;
        }

        public static Doc SpaceAlert(long free, long total, int percent, List<CleanItem> items)
        {
            var d = new Doc(L("Drive C: is almost full", "כונן C: כמעט מלא"), "#c7362f");
            d.P(L("Only " + Fs.Fmt(free) + " is free on C: (" + Pct(free, total) + "), under your limit of " + percent + "%.",
                  "רק " + Fs.Fmt(free) + " פנויים ב-C: (" + Pct(free, total) + "), מתחת לגבול של " + percent + "% שבחרתם."));
            WhatToDelete(d, items);
            return d;
        }

        public static Doc DiskAlert(DiskReport r)
        {
            var w = r.Worst;
            var x = r.Disks.First(k => k.Level == w);
            var d = new Doc(w == HealthLevel.Failing ? L("A drive may fail soon: back up now", "כונן עלול להיכשל בקרוב: גבו עכשיו") : L("A drive needs attention", "כונן דורש תשומת לב"),
                            w == HealthLevel.Failing ? "#c7362f" : "#be6900");
            d.P(x.Label + " - " + Lang.T(DiskHealth.LevelText(w)));
            d.Table(x.Reasons.Select(s => new[] { Lang.T(s), "", "" }));
            d.P(L("What to do: ", "מה לעשות: ") + Lang.T(x.Advice));
            DiskLines(d, r);
            return d;
        }

        public static Doc Test()
        {
            var d = new Doc(L("Elixar email reports are on", "דוחות המייל של Elixar פעילים"), "#5b4cc4");
            d.P(L("This is a test. Elixar will email you here, from your own " + MailAccount.ProviderName + ", about what it finds on " + Environment.MachineName + ".",
                  "זו בדיקה. Elixar ישלח לכאן, מה-" + MailAccount.ProviderName + " שלך, מה הוא מוצא במחשב " + Environment.MachineName + "."));
            d.Big(L("Free on C:", "פנוי ב-C:"), Fs.Fmt(Sys.Free()) + " (" + Pct(Sys.Free(), Sys.Total()) + ")", "#18202b");
            return d;
        }

        // ---------- the daily email check (scheduled task, runs as administrator) ----------
        public static int Daily()
        {
            if (MailAccount.Email == null) { Automation.Log("Email check: not signed in, nothing sent."); return 0; }
            bool signedOut;
            string terr = MailAccount.CheckSignIn(out signedOut);
            if (terr != null)
            {
                Automation.Log("Email check: " + terr);
                if (!signedOut) { MailStatus.Failed(terr, false); return 0; }   // offline: try again tomorrow
                MailStatus.Load();
                if (MailStatus.ShouldNotify(DateTime.Today))
                {
                    MailStatus.Notified();
                    Automation.Notify(L("Elixar was signed out of your email", "Elixar נותק מהמייל שלך"),
                                      L("Email reports have stopped. Open Elixar > Settings > Email reports and click \"Sign in again\".",
                                        "דוחות המייל הפסיקו. פתחו את Elixar > הגדרות > דוחות במייל ולחצו \"התחברות מחדש\"."), System.Windows.Forms.ToolTipIcon.Warning, true);
                }
                return 0;
            }
            var p = MailPrefs.Load();
            long free = Sys.Free(), total = Sys.Total();
            DiskReport disks = null;
            List<CleanItem> items = null;
            Func<List<CleanItem>> measured = () => items ?? (items = Measure());
            if (p.Alerts)
            {
                int pct = Automation.PercentFromArgs(Automation.Query(Automation.CheckTask).Arguments, 15);
                if (Automation.ShouldWarn(free, total, pct) && p.LastSpaceAlert.Date != DateTime.Today)
                {
                    string e = SpaceAlert(free, total, pct, measured()).Send();
                    Automation.Log("Email: low-space alert " + (e == null ? "sent." : "not sent: " + e));
                    if (e == null) p.LastSpaceAlert = DateTime.Today;
                }
                disks = DiskHealth.Read();
                if (disks.Error == null && DiskHealth.ShouldAlert(disks.Worst, p.DiskStamp, DateTime.Today))
                {
                    string e = DiskAlert(disks).Send();
                    Automation.Log("Email: disk health alert " + (e == null ? "sent." : "not sent: " + e));
                    if (e == null) p.DiskStamp = DiskHealth.Stamp(disks.Worst, DateTime.Today);
                }
            }
            if (p.Summary && MailPrefs.SummaryDue(p.LastSummary, DateTime.Today, p.Day))
            {
                if (disks == null) disks = DiskHealth.Read();
                string e = Summary(free, total, measured(), disks, LastCleanLine()).Send();
                Automation.Log("Email: weekly summary " + (e == null ? "sent." : "not sent: " + e));
                if (e == null) p.LastSummary = DateTime.Today;
            }
            p.Save();
            return 0;
        }

        static string LastCleanLine()
        {
            try
            {
                var last = Automation.ReadHistory(Automation.HistoryFile).OrderByDescending(x => x.Item1).FirstOrDefault();
                if (last == null) return null;
                return L("Last automatic clean: " + last.Item1.ToString("d MMM", CultureInfo.InvariantCulture) + ", freed " + Fs.Fmt(last.Item2) + ".",
                         "הניקוי האוטומטי האחרון: " + last.Item1.ToString("dd/MM", CultureInfo.InvariantCulture) + ", התפנו " + Fs.Fmt(last.Item2) + ".");
            }
            catch { return null; }
        }
    }
}
