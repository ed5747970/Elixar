// Email reports: how the last email went (shown in Settings), and the space-trend picture for the weekly summary.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CDriveCleaner
{
    // kept in the protected mail folder: the last email sent, the last problem, and whether Google signed Elixar out
    public static class MailStatus
    {
        public static DateTime LastSent, LastFail, LastNotified;
        public static string LastWhat = "", LastError = "";
        public static bool SignedOut;

        static string File_ { get { var d = GoogleAuth.Dir; return d == null ? null : Path.Combine(d, "status.txt"); } }
        static string D(DateTime t) { return t == DateTime.MinValue ? "" : t.ToString("o", CultureInfo.InvariantCulture); }
        static DateTime P(string s) { DateTime t; return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t) ? t : DateTime.MinValue; }
        static string Clean(string s) { return (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim(); }

        public static void Load()
        {
            LastSent = LastFail = LastNotified = DateTime.MinValue; LastWhat = LastError = ""; SignedOut = false;
            try
            {
                string f = File_;
                if (f == null || !File.Exists(f)) return;
                foreach (var line in File.ReadAllLines(f))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    if (k == "sent") LastSent = P(v); else if (k == "what") LastWhat = v; else if (k == "fail") LastFail = P(v);
                    else if (k == "error") LastError = v; else if (k == "signedOut") SignedOut = v == "1"; else if (k == "notified") LastNotified = P(v);
                }
            }
            catch { }
        }

        static void Save()
        {
            try
            {
                string f = File_;
                if (f == null) return;
                File.WriteAllText(f, "sent=" + D(LastSent) + "\nwhat=" + Clean(LastWhat) + "\nfail=" + D(LastFail) + "\nerror=" + Clean(LastError) +
                                     "\nsignedOut=" + (SignedOut ? 1 : 0) + "\nnotified=" + D(LastNotified) + "\n");
            }
            catch { }
        }

        public static void Sent(string what) { Load(); LastSent = DateTime.Now; LastWhat = what; SignedOut = false; Save(); }
        public static void Failed(string error, bool signedOut) { Load(); LastFail = DateTime.Now; LastError = error; if (signedOut) SignedOut = true; Save(); }
        public static void Reset() { try { string f = File_; if (f != null && File.Exists(f)) File.Delete(f); } catch { } Load(); }
        public static void Notified() { Load(); LastNotified = DateTime.Now; Save(); }

        // the line shown in Settings; level: 0 fine, 1 a problem, 2 signed out
        public static string Line(out int level)
        {
            Load();
            level = 0;
            Func<DateTime, string> when = t => t.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
            if (SignedOut) { level = 2; return "Google signed Elixar out on " + when(LastFail) + ". Emails have stopped: click \"Sign in again\"."; }
            if (LastFail > LastSent && LastError.Length > 0) { level = 1; return "The last email didn't go out (" + when(LastFail) + "): " + LastError; }
            if (LastSent != DateTime.MinValue) return "Last email: " + when(LastSent) + " - " + LastWhat + " ✓";
            return "No email sent yet.";
        }

        // the daily check notifies at most once a day while the sign-in is broken
        public static bool ShouldNotify(DateTime today) { return SignedOut && LastNotified.Date != today.Date; }
    }

    // a small chart of free space on C:, drawn on this PC, for the weekly summary email
    public static class TrendChart
    {
        public static byte[] Png(List<SpaceHistory.Point> pts, DateTime today, int days)
        {
            var p = pts.Where(x => x.Day > today.AddDays(-days) && x.Total > 0).OrderBy(x => x.Day).ToList();
            if (p.Count < 2) return null;
            const int W = 1120, H = 320, L = 96, R = 24, T = 28, B = 56;
            using (var bmp = new Bitmap(W, H))
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Segoe UI", 20f, GraphicsUnit.Pixel))
            using (var small = new Font("Segoe UI", 18f, GraphicsUnit.Pixel))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.White);
                double gb = 1 << 30;
                double max = p.Max(x => x.Free) / gb, min = p.Min(x => x.Free) / gb;
                double lo = Math.Max(0, Math.Floor((min - Math.Max(2, (max - min) * 0.3)) / 5) * 5), hi = Math.Ceiling((max + Math.Max(2, (max - min) * 0.3)) / 5) * 5;
                hi = lo + Math.Max(20, Math.Ceiling((hi - lo) / 20) * 20);   // four even steps of 5, 10, 15... GB
                DateTime d0 = p[0].Day.Date, d1 = today.Date;
                double span = Math.Max(1, (d1 - d0).TotalDays);
                Func<DateTime, float> X = d => (float)(L + (W - L - R) * ((d.Date - d0).TotalDays / span));
                Func<double, float> Y = v => (float)(T + (H - T - B) * (1 - (v - lo) / (hi - lo)));
                using (var grid = new Pen(Color.FromArgb(226, 230, 236), 2))
                using (var ink = new SolidBrush(Color.FromArgb(110, 116, 128)))
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        double v = lo + (hi - lo) * i / 4;
                        float y = Y(v);
                        g.DrawLine(grid, L, y, W - R, y);
                        string s = v.ToString("0", CultureInfo.InvariantCulture) + " GB";
                        var sz = g.MeasureString(s, small);
                        g.DrawString(s, small, ink, L - sz.Width - 8, y - sz.Height / 2);
                    }
                    foreach (var d in new[] { d0, d0.AddDays(span / 2), d1 })
                    {
                        string s = d.ToString("d MMM", CultureInfo.InvariantCulture);
                        var sz = g.MeasureString(s, small);
                        float x = Math.Min(W - R - sz.Width, Math.Max(L, X(d) - sz.Width / 2));
                        g.DrawString(s, small, ink, x, H - B + 12);
                    }
                }
                var line = p.Select(x => new PointF(X(x.Day), Y(x.Free / gb))).ToArray();
                var area = new List<PointF> { new PointF(line[0].X, Y(lo)) };
                area.AddRange(line);
                area.Add(new PointF(line[line.Length - 1].X, Y(lo)));
                using (var fill = new LinearGradientBrush(new Rectangle(0, T, W, H - T - B), Color.FromArgb(70, 91, 76, 196), Color.FromArgb(8, 91, 76, 196), 90f))
                    g.FillPolygon(fill, area.ToArray());
                using (var pen = new Pen(Color.FromArgb(91, 76, 196), 5) { LineJoin = LineJoin.Round })
                    g.DrawLines(pen, line);
                var last = line[line.Length - 1];
                using (var dot = new SolidBrush(Color.FromArgb(91, 76, 196))) g.FillEllipse(dot, last.X - 8, last.Y - 8, 16, 16);
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
            }
        }

        public static bool IsPng(byte[] b) { return b != null && b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47; }
    }
}
