// Privacy: traces of what you did. Never ticked for you, never part of the weekly clean.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class Privacy
    {
        static readonly string[] HistoryFiles = { "History", "History-journal", "Visited Links", "Top Sites", "Top Sites-journal", "Shortcuts", "Shortcuts-journal",
                                                  "Network Action Predictor", "Network Action Predictor-journal" };
        static readonly string[] CookieFiles = { "Cookies", "Cookies-journal", Path.Combine("Network", "Cookies"), Path.Combine("Network", "Cookies-journal") };

        public static List<string> BrowserRoots()
        {
            string L = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new[] { Path.Combine(L, "Google", "Chrome", "User Data"), Path.Combine(L, "Microsoft", "Edge", "User Data"),
                           Path.Combine(L, "BraveSoftware", "Brave-Browser", "User Data") }.ToList();
        }

        // files with these names in every browser profile (Default, Profile 1, ...)
        public static List<string> ProfileFiles(IEnumerable<string> roots, string[] names)
        {
            var list = new List<string>();
            foreach (var root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var p in Directory.GetDirectories(root))
                    {
                        string n = Path.GetFileName(p);
                        if (n != "Default" && !n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var f in names) { string full = Path.Combine(p, f); if (File.Exists(full)) list.Add(full); }
                    }
                }
                catch { }
            }
            return list;
        }

        public static List<string> FirefoxCookies(string profilesRoot)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(profilesRoot)) return list;
                foreach (var p in Directory.GetDirectories(profilesRoot))
                    foreach (var f in new[] { "cookies.sqlite", "cookies.sqlite-wal", "cookies.sqlite-shm" })
                    { string full = Path.Combine(p, f); if (File.Exists(full)) list.Add(full); }
            }
            catch { }
            return list;
        }

        static long Sum(IEnumerable<string> files) { long t = 0; foreach (var f in files) { try { t += new FileInfo(f).Length; } catch { } } return t; }
        static void DeleteAll(IEnumerable<string> files) { foreach (var f in files) Fs.Delete(f); }

        // registry lists of things you typed or opened (Run box, Explorer address bar, Explorer search, Open/Save dialogs)
        public static readonly string[] TraceKeys =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\OpenSavePidlMRU",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32\LastVisitedPidlMRU",
        };

        static long TraceSize()
        {
            long t = 0;
            if (!Win.IsWindows) return 0;
            foreach (var key in TraceKeys)
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (var n in k.GetValueNames()) { var v = k.GetValue(n); t += v is byte[] ? ((byte[])v).Length : (v ?? "").ToString().Length * 2; }
                        foreach (var sub in k.GetSubKeyNames())
                            using (var sk = k.OpenSubKey(sub))
                                if (sk != null) foreach (var n in sk.GetValueNames()) { var v = sk.GetValue(n); t += v is byte[] ? ((byte[])v).Length : 16; }
                    }
                }
                catch { }
            }
            return t;
        }

        static void ClearTraces(Action<string> log)
        {
            foreach (var key in TraceKeys)
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(key, true))
                    {
                        if (k == null) continue;
                        foreach (var n in k.GetValueNames()) k.DeleteValue(n, false);
                        foreach (var sub in k.GetSubKeyNames()) k.DeleteSubKeyTree(sub, false);
                    }
                }
                catch (Exception e) { if (log != null) log("  " + key + ": " + e.Message); }
            }
        }

        public static List<CleanItem> Items()
        {
            string R = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string recent = Path.Combine(R, "Microsoft", "Windows", "Recent");
            string ff = Path.Combine(R, "Mozilla", "Firefox", "Profiles");
            var chromium = new[] { "chrome", "msedge", "brave" };
            Func<List<string>> history = () => ProfileFiles(BrowserRoots(), HistoryFiles);
            Func<List<string>> cookies = () => ProfileFiles(BrowserRoots(), CookieFiles);
            Func<List<string>> recentFiles = () =>
            {
                var l = new List<string>();
                try { if (Directory.Exists(recent)) l.AddRange(Directory.GetFiles(recent, "*.lnk")); } catch { }
                foreach (var sub in new[] { "AutomaticDestinations", "CustomDestinations" })
                    try { string d = Path.Combine(recent, sub); if (Directory.Exists(d)) l.AddRange(Directory.GetFiles(d)); } catch { }
                return l;
            };
            return new List<CleanItem>
            {
                new CleanItem { Name = "Browsing history (Chrome, Edge, Brave)", Tier = Tier.Review, Privacy = true, Where = "Browser profiles", Procs = chromium,
                    Info = "Sites you visited, the address-bar suggestions and the most-visited tiles. Passwords, bookmarks and saved logins are not touched. Close the browser first.",
                    Measure = () => Sum(history()), Clean = log => DeleteAll(history()) },
                new CleanItem { Name = "Cookies (Chrome, Edge, Brave)", Tier = Tier.Review, Privacy = true, Where = "Browser profiles", Procs = chromium,
                    Info = "Remove trackers and site data. You will be signed out of websites. Close the browser first.",
                    Measure = () => Sum(cookies()), Clean = log => DeleteAll(cookies()) },
                new CleanItem { Name = "Cookies (Firefox)", Tier = Tier.Review, Privacy = true, Where = ff, Procs = new[] { "firefox" },
                    Info = "Remove trackers and site data. You will be signed out of websites. Your Firefox history and bookmarks are not touched.",
                    Measure = () => Sum(FirefoxCookies(ff)), Clean = log => DeleteAll(FirefoxCookies(ff)) },
                new CleanItem { Name = "Recent files and jump lists", Tier = Tier.Review, Privacy = true, Where = recent,
                    Info = "The lists of files you opened lately (Explorer Quick access, taskbar right-click lists). Also clears items you pinned in those lists. The files themselves are not touched.",
                    Measure = () => Sum(recentFiles()), Clean = log => DeleteAll(recentFiles()) },
                new CleanItem { Name = "Typed history (Run box, Explorer, Open/Save)", Tier = Tier.Review, Privacy = true, Where = @"HKCU\...\Explorer",
                    Info = "What you typed in Win+R and Explorer's address and search bars, and the recent folders in Open/Save windows.",
                    Measure = TraceSize, Clean = ClearTraces },
                new CleanItem { Name = "Clipboard", Tier = Tier.Review, Privacy = true, Where = "Clipboard",
                    Info = "Empties what you last copied (a password or a message you copied stays there until you copy something else).",
                    Measure = () => -1, Clean = log => IPhone.Sta(() => { try { System.Windows.Forms.Clipboard.Clear(); } catch { } return 0; }) },
                new CleanItem { Name = "DNS cache", Tier = Tier.Review, Privacy = true, Where = "Windows network cache",
                    Info = "The list of websites this PC looked up recently. Windows fills it again as you browse.",
                    Measure = () => -1, Clean = log => { try { var p = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns") { UseShellExecute = false, CreateNoWindow = true }); p.WaitForExit(30000); } catch { } } },
            };
        }
    }
}
