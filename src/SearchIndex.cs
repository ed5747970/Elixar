// Windows Search's index (the file Start-menu search uses). Sometimes it gets damaged and grows to 10-50 GB.
// Elixar only measures it, and when it is far too big offers Windows' own reset: the Search service is told its
// setup didn't finish (SetupCompletedSuccessfully = 0), so on its next start Windows deletes the index and builds a
// new one (what Microsoft's "Reset Windows Search" does). Elixar deletes no file itself.

using System;
using System.IO;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class SearchIndex
    {
        public static Func<long> SizeOverride;                  // self-test
        public static Func<string, string, int> RunOverride;    // self-test: (exe, args) -> exit code
        public const long TooBig = 4L << 30;                    // over 4 GB: offered (usually it is under 1-2 GB)
        const string Key = @"SOFTWARE\Microsoft\Windows Search";

        // where Windows keeps it (it can be moved in Indexing Options)
        public static string Folder
        {
            get
            {
                string d = null;
                try { using (var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(Key)) d = k == null ? null : k.GetValue("DataDirectory") as string; } catch { }
                if (string.IsNullOrEmpty(d)) d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Search\Data\");
                return Path.Combine(Environment.ExpandEnvironmentVariables(d), @"Applications\Windows");
            }
        }

        public static long Size()
        {
            if (SizeOverride != null) return SizeOverride();
            if (!Win.IsWindows) return -1;
            try { string f = Folder; return Directory.Exists(f) ? Fs.SizeOf(f) : 0; } catch { return -1; }
        }

        public static bool Worth(long size) { return size >= TooBig; }

        public static string Describe(long size)
        {
            if (size < 0) return Lang.Hebrew ? "לא ניתן היה למדוד את האינדקס." : "The index couldn't be measured.";
            if (Worth(size)) return Lang.Hebrew ? "אינדקס החיפוש של Windows: " + Fs.Fmt(size) + ". בדרך כלל הוא פחות מ-1-2 GB; גודל כזה בדרך כלל אומר שהוא פגום."
                                                : "Windows Search index: " + Fs.Fmt(size) + ". Usually under 1-2 GB; this size usually means it is damaged.";
            return Lang.Hebrew ? "אינדקס החיפוש של Windows: " + Fs.Fmt(size) + ". גודל רגיל." : "Windows Search index: " + Fs.Fmt(size) + ". A normal size.";
        }

        static int Run(string exe, string args) { if (RunOverride != null) return RunOverride(exe, args); string o; return SystemSpace.Run(exe, args, 120000, out o); }

        // Windows' own reset; returns null when done, else what went wrong
        public static string Rebuild(Action<string> say)
        {
            using (ChangeLog.In("System space"))
            {
                if (RunOverride == null && !Win.IsWindows) return "Only on Windows.";
                Run(Win.SystemExe("net.exe"), "stop wsearch /y");
                try
                {
                    if (RunOverride == null)
                        using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (var k = hk.CreateSubKey(Key))
                            RegLog.Set(k, "SetupCompletedSuccessfully", 0, RegistryValueKind.DWord, "Search index: Windows rebuilds it", false);
                }
                catch (Exception e) { Run(Win.SystemExe("net.exe"), "start wsearch"); return "Couldn't ask Windows to rebuild it: " + e.Message; }
                int c = Run(Win.SystemExe("net.exe"), "start wsearch");
                if (say != null) say(c == 0 ? "  Windows Search started again: it deletes the old index and builds a new one in the background (an hour or a few)." : "  Windows Search will rebuild the index the next time it starts (or after a restart).");
                return null;
            }
        }
    }
}
