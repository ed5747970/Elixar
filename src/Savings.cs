// How much space Elixar has freed on this PC, all cleans together (the Cleaner, the weekly clean, the policy's cleaning and
// the daily tidy). One line per clean in savings.txt, in Elixar's admin-only data folder.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CDriveCleaner
{
    public class SavingsTotal { public long Bytes, ThisMonth; public int Cleans; public DateTime? Since; }

    public static class Savings
    {
        public static string OverrideFile;   // self-test and previews
        static string File_ { get { return OverrideFile ?? Paths.FileIn("savings.txt"); } }
        static readonly object gate = new object();
        public static event Action Changed;

        public static void Add(long bytes, string source)
        {
            if (bytes <= 0) return;
            try
            {
                string line = DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\t" + bytes.ToString(CultureInfo.InvariantCulture) + "\t" + (source ?? "").Replace("\t", " ").Replace("\n", " ").Replace("\r", " ") + "\r\n";
                lock (gate)
                    using (var s = new FileStream(File_, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    { var b = new UTF8Encoding(false).GetBytes(line); s.Write(b, 0, b.Length); }
            }
            catch { }
            var c = Changed; if (c != null) try { c(); } catch { }
        }

        public static SavingsTotal Read()
        {
            var t = new SavingsTotal();
            try
            {
                string f = File_;
                if (!File.Exists(f) || new FileInfo(f).Length > 8 << 20) return t;
                var now = DateTime.Now;
                foreach (var line in File.ReadAllLines(f))
                {
                    var p = line.Split('\t');
                    DateTime when; long b;
                    if (p.Length < 2 || !DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out when) || !long.TryParse(p[1], out b) || b <= 0 || b > (1L << 50)) continue;
                    t.Bytes += b; t.Cleans++;
                    if (when.Year == now.Year && when.Month == now.Month) t.ThisMonth += b;
                    if (!t.Since.HasValue || when < t.Since.Value) t.Since = when;
                }
            }
            catch { }
            return t;
        }

        public static string Words(SavingsTotal t)
        {
            if (t == null || t.Bytes <= 0) return "";
            return "Elixar has freed " + Fs.Fmt(t.Bytes) + " on this PC since " + t.Since.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture) +
                   (t.ThisMonth > 0 ? " (" + Fs.Fmt(t.ThisMonth) + " this month)" : "") + ".";
        }
    }
}
