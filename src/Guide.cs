// The user guide, built into Elixar.exe: open it (F1 or the footer link) or save a copy.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CDriveCleaner
{
    public static class Guide
    {
        public const string FileName = "Elixar User Guide.html";

        public static string Html()
        {
            using (var st = typeof(Guide).Assembly.GetManifestResourceStream("Elixar.guide.html"))
            {
                if (st == null) return "";
                using (var r = new StreamReader(st, Encoding.UTF8)) return r.ReadToEnd();
            }
        }

        // a fresh, unguessable file each time: nothing planted at that name in advance can be overwritten
        public static string WriteTemp()
        {
            string path = Path.Combine(Path.GetTempPath(), "Elixar-User-Guide-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".html");
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(fs, new UTF8Encoding(true))) w.Write(Html());
            return path;
        }

        // opens in your normal browser (through Explorer, so the browser never runs as administrator)
        public static void Open()
        {
            string p = WriteTemp();
            if (Win.IsWindows) Process.Start(Win.SystemExe("explorer.exe"), "\"" + p + "\"");
            else Process.Start(p);
        }

        public static void SaveCopy(string path)
        {
            File.WriteAllText(path, Html(), new UTF8Encoding(true));
        }
    }
}
