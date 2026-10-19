// "Export report": a one-page summary of this PC's drives as a web page you can keep, send or print to PDF.
// Sizes and folder names only; no file names, no email addresses.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class Report
    {
        static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        public static string Build(DashSummary sum, List<DriveRow> drives, List<CleanItem> items, List<Growth.Change> grew, Dictionary<string, long> tiers)
        {
            var b = new StringBuilder();
            var ver = typeof(Report).Assembly.GetName().Version;
            b.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Elixar report - " + H(DiskNumber.Host) + "</title><style>")
             .Append("body{font-family:Segoe UI,Arial,sans-serif;color:#18202b;background:#f6f8fb;margin:0;padding:24px}main{max-width:860px;margin:auto;background:#fff;border-radius:12px;padding:28px 32px;box-shadow:0 2px 12px #0001}")
             .Append("h1{font-size:24px;margin:0}h2{font-size:17px;margin:22px 0 8px;color:#5b4cc4}table{border-collapse:collapse;width:100%;font-size:14px}td,th{padding:6px 8px;border-bottom:1px solid #e6e8ef;text-align:left}th{color:#666;font-weight:600}td.n{text-align:right;white-space:nowrap}.q{color:#777;font-size:13px}")
             .Append("@media print{body{background:#fff;padding:0}main{box-shadow:none}.noprint{display:none}}</style></head><body><main>");
            b.Append("<h1>" + H(Lang.T("Elixar report")) + "</h1><p class=\"q\">" + H(DiskNumber.Host) + " &middot; " + H(OsInfo.Current.Name) + " &middot; " + H(DateTime.Now.ToString("yyyy-MM-dd HH:mm")) + " &middot; Elixar " + ver.Major + "." + ver.Minor + "." + ver.Build + "</p>");
            b.Append("<p class=\"q noprint\">" + H(Lang.T("To keep it as a PDF: press Ctrl+P and choose \"Microsoft Print to PDF\".")) + "</p>");

            b.Append("<h2>" + H(Lang.T("Drives")) + "</h2><table><tr><th>" + H(Lang.T("Drive")) + "</th><th>" + H(Lang.T("Disk")) + "</th><th class=\"n\">" + H(Lang.T("Free")) + "</th><th class=\"n\">" + H(Lang.T("Size")) + "</th><th>" + H(Lang.T("Health")) + "</th></tr>");
            foreach (var d in drives)
            {
                string name = d.Letter.Length == 1 ? d.Letter + ":" + (d.Label.Length > 0 ? "  " + d.Label : "") : d.Label;
                b.Append("<tr><td>" + H(name) + "</td><td>" + H(Lang.T(DiskNumber.Tag(Win.IsWindows ? d.Letter : d.Root))) + "</td><td class=\"n\">" + H(Fs.Fmt(d.Free)) + "</td><td class=\"n\">" + H(Fs.Fmt(d.Total)) +
                         "</td><td>" + H(d.Health == HealthLevel.Unknown ? "-" : Lang.T(d.Health == HealthLevel.Failing ? "Back up now!" : d.Health == HealthLevel.Warning ? "Needs a look" : "Healthy")) + "</td></tr>");
            }
            b.Append("</table>");

            if (tiers != null)
            {
                b.Append("<h2>" + H(Lang.T("What fills C:")) + "</h2><table>");
                foreach (var t in new[] { "safe", "review", "caution", "critical" })
                {
                    long v; if (!tiers.TryGetValue(t, out v)) continue;
                    string label = t == "safe" ? "Safe to clean" : t == "review" ? "Your files" : t == "caution" ? "Leftovers and system stores" : "Windows and apps";
                    b.Append("<tr><td>" + H(Lang.T(label)) + "</td><td class=\"n\">" + H(Fs.Fmt(v)) + "</td></tr>");
                }
                b.Append("</table>");
            }

            var can = items.Where(i => !i.SizeUnknown && i.Size >= (1L << 20) && !i.Privacy).OrderByDescending(i => i.Size).Take(15).ToList();
            b.Append("<h2>" + H(Lang.T("What can be cleaned")) + "</h2>");
            if (can.Count == 0) b.Append("<p class=\"q\">" + H(Lang.T("Nothing big to clean right now.")) + "</p>");
            else
            {
                b.Append("<table><tr><th>" + H(Lang.T("Item")) + "</th><th>" + H(Lang.T("Level")) + "</th><th class=\"n\">" + H(Lang.T("Size")) + "</th></tr>");
                foreach (var i in can) b.Append("<tr><td>" + H(Lang.T(i.Name)) + "</td><td>" + H(Lang.T(i.Tier.ToString())) + "</td><td class=\"n\">" + H(Fs.Fmt(i.Size)) + "</td></tr>");
                b.Append("</table>");
            }

            if (grew != null && grew.Count > 0)
            {
                b.Append("<h2>" + H(Lang.T("What grew in the last 7 days")) + "</h2><table>");
                foreach (var g in grew.Take(8)) b.Append("<tr><td>" + H(Lang.T(g.Name)) + "</td><td class=\"n\">+" + H(Fs.Fmt(g.Bytes)) + "</td></tr>");
                b.Append("</table>");
            }

            if (sum != null && sum.Watch != null && sum.Watch.Count > 0)
            {
                b.Append("<h2>" + H(Lang.T("Folders to watch")) + "</h2><table>");
                foreach (var w in sum.Watch.Take(8))
                    b.Append("<tr><td>" + H(Lang.T(w.Name)) + "</td><td class=\"n\">" + H(Lang.T("+" + Fs.Fmt(Math.Max(0, w.PerMonth)) + " a month")) + "</td><td>" +
                             H(Lang.T("Check it " + Growth.EveryText(w.EveryDays))) + "</td></tr>");
                b.Append("</table>");
            }

            if (sum != null && sum.LastClean.HasValue)
                b.Append("<h2>" + H(Lang.T("Last clean")) + "</h2><p>" + H(sum.LastClean.Value.ToString("yyyy-MM-dd HH:mm")) + " &middot; " + H(Lang.T("Freed " + Fs.Fmt(sum.LastCleanBytes))) + "</p>");
            b.Append("<p class=\"q\" style=\"margin-top:26px\">" + H(Lang.T("Made by Elixar on this PC. It lists sizes and folder names only, never your file names or email address.")) + "</p></main></body></html>");
            return b.ToString();
        }
    }

    public partial class MainForm
    {
        public void ExportReport()
        {
            string html;
            try { html = Report.Build(dashSum, AllDriveRows(), items, Win.IsWindows || Growth.OverrideFile != null ? Growth.Grew(DateTime.Today, 7, 100L << 20) : null, dashLocs != null && DashboardFilled() ? DashData.Totals(dashLocs) : null); }
            catch (Exception e) { Lang.Box(this, Lang.T("The report could not be made: ") + e.Message, "Elixar"); return; }
            using (var d = new SaveFileDialog { Filter = "Web page (*.html)|*.html", FileName = "Elixar report " + DateTime.Today.ToString("yyyy-MM-dd") + ".html",
                                                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), OverwritePrompt = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { SafeFile.WriteText(d.FileName, html); }
                catch { try { File.WriteAllText(d.FileName, html, new UTF8Encoding(false)); } catch (Exception e) { Lang.Box(this, e.Message, "Elixar"); return; } }
                Log(Lang.T("Report saved: ") + d.FileName);
                try { Shell.Open(d.FileName); } catch { }
            }
        }
    }
}
