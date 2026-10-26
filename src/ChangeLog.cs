// Logs: every change Elixar makes to this PC, in one list - registry values (with the old and new value), files moved to Undo,
// Windows commands that change something, apps, startup, security and Elixar's own settings - so a problem can be traced and fixed.
// Kept in Elixar's data folder, which only administrators can change. Passwords and secrets are never written here: only that they were removed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class ChangeLog
    {
        public class Entry
        {
            public string Id { get; set; }
            public string Time { get; set; }           // local time, "s" format
            public string Kind { get; set; }           // Registry, Files, Windows command, Apps, Startup, Security, Settings, Email, Cleaning, Undo
            public string Section { get; set; }        // the part of Elixar that made it: Cleaner, Duplicates, Startup... (see Sections)
            public string Action { get; set; }         // what was done, in a few words
            public string Target { get; set; }         // the key, file, app or command it was done to
            public string Before { get; set; }
            public string After { get; set; }
            public string Detail { get; set; }
            public bool Failed { get; set; }
            public string Ref { get; set; }            // an undo points at the change it undid
            // registry undo: only set when the change can be put back
            public string Hive { get; set; }
            public string View { get; set; }
            public string Key { get; set; }
            public string Name { get; set; }
            public string Old { get; set; }            // encoded old value, null = the value did not exist
            public string New { get; set; }            // encoded new value, null = the value was deleted
            public bool CanUndo { get; set; }

            public DateTime When { get { DateTime t; return DateTime.TryParse(Time, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : DateTime.MinValue; } }
        }

        public static string OverrideFile;    // self-test and previews
        public static Func<DateTime> NowOverride;
        public static long MaxBytes = 4L * 1024 * 1024;   // then the file becomes changes-old.jsonl and a new one starts
        static readonly object gate = new object();
        static readonly Random rnd = new Random();
        [ThreadStatic] static int quiet;

        static string File_ { get { return OverrideFile ?? Paths.LogIn("changes.jsonl"); } }
        public static string FilePath { get { return File_; } }
        static string OldFile(string f) { return Path.Combine(Path.GetDirectoryName(f) ?? "", Path.GetFileNameWithoutExtension(f) + "-old.jsonl"); }

        // ================= sections: which part of Elixar made each change =================
        public static readonly string[] Sections =
        {
            "Cleaner", "Check & fix", "Weekly clean", "Daily tidy", "Duplicates", "Smart scan", "Leftovers", "Startup", "Apps", "App updates", "Windows tweaks", "Drivers", "Phone", "Move folders",
            "System space", "Space map", "Repair Windows", "Secure wipe", "Restore points", "Privacy", "Sysprep", "Security", "Email", "Policy", "Policy cleaning", "Settings", "Schedules", "Undo", "Install and updates", "OneDrive", "Command line", "Other"
        };

        [ThreadStatic] static string scope;
        // everything written down inside it belongs to that section (the Cleaner's DISM run, say)
        public static IDisposable In(string section) { string was = scope; scope = section; return new Done(() => scope = was); }
        public static T Within<T>(string section, Func<T> work) { using (In(section)) return work(); }
        public static void Within(string section, Action work) { using (In(section)) work(); }

        // a change that names its own section; restore points always go under Restore points
        public static string SectionOf(Entry e)
        {
            if (e == null) return "Other";
            if (!string.IsNullOrEmpty(e.Section)) return e.Section;
            return Derive(e);
        }

        static bool Hs(string s, string w) { return (s ?? "").IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0; }

        // from the words of the change: also sorts lines written before sections existed
        public static string Derive(Entry e)
        {
            string a = e.Action ?? "", d = e.Detail ?? "", t = e.Target ?? "", all = a + " | " + d + " | " + t, k = e.Kind ?? "";
            if (a.StartsWith("Updated an app") || a.StartsWith("App update")) return "App updates";
            if (Hs(d, "Windows tweak")) return "Windows tweaks";
            if (a.StartsWith("Policy cleaning") || Hs(d, "Cleaned by the policy")) return "Policy cleaning";
            if (a.StartsWith("Policy")) return "Policy";
            if (Hs(a, "restore point")) return "Restore points";
            if (k == "Phone") return "Phone";
            if (k == "Email") return "Email";
            if (k == "Undo" || a.StartsWith("Put back from Undo") || a.StartsWith("Deleted from Undo")) return "Undo";
            if (Hs(all, "Duplicates")) return "Duplicates";
            if (Hs(all, "Smart scan") || a.StartsWith("Removed empty folders") || a.StartsWith("Compressed files")) return "Smart scan";
            if (Hs(all, "Leftovers of removed apps")) return "Leftovers";
            if (Hs(d, "Startup:") || Hs(a, "Startup") || Hs(t, @"\StartupApproved\") || Hs(t, @"\CurrentVersion\Run")) return "Startup";
            if (Hs(all, "Weekly clean")) return "Weekly clean";
            if (Hs(all, "Daily tidy") || a.StartsWith("Recycle Bin:")) return "Daily tidy";
            if (Hs(all, "Golden image") || Hs(all, "Sysprep") || Hs(a, "answer file") || Hs(a, "capture kit") || Hs(a, "Start layout") || Hs(a, "default apps") || Hs(a, "event logs")) return "Sysprep";
            if (Hs(d, "Privacy:")) return "Privacy";
            if (Hs(a, "driver")) return "Drivers";
            if (a.StartsWith("Moved a personal folder")) return "Move folders";
            if (Hs(all, "Elixar's protected copy") || Hs(d, "Elixar installed") || Hs(d, "Elixar removed")) return "Install and updates";
            if (k == "Settings" || d.StartsWith("Elixar setting")) return "Settings";
            if (Hs(a, "scheduled task")) return "Schedules";
            if (a.StartsWith("OneDrive")) return "OneDrive";
            if (k == "Security" || d.StartsWith("Security:")) return "Security";
            if (k == "Apps") return "Apps";
            if (Hs(all, "Previous Windows")) return "Cleaner";
            if (k == "Windows command") return "System space";
            if (k == "Cleaning" || k == "Files") return "Cleaner";
            return "Other";
        }

        // anything removed for good: files, registry values, phone files, restore points, logs
        public static bool IsDeletion(Entry e)
        {
            string a = e.Action ?? "";
            if (e.Failed || Hs(e.Detail, "left as it was")) return false;   // nothing was removed / a setting Elixar only lent for a moment
            return a.StartsWith("Deleted") || a.StartsWith("Removed empty") || a.StartsWith("Emptied") || a.StartsWith("Cleared") || a.StartsWith("Uninstalled") || a.StartsWith("Removed a");
        }

        // the steps inside a change Elixar already writes down as one line (the weekly clean's own schedule, say) are not written twice
        public static IDisposable Quiet() { quiet++; return new Done(() => quiet--); }
        class Done : IDisposable { readonly Action a; bool d; public Done(Action a) { this.a = a; } public void Dispose() { if (!d) { d = true; a(); } } }

        static string Cut(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            s = Redact.Emails(s);
            return s.Length > n ? s.Substring(0, n) + "..." : s;
        }

        public static Entry Add(string kind, string action, string target, string before, string after, string detail, bool failed)
        {
            return Add(new Entry { Kind = kind, Action = action, Target = target, Before = before, After = after, Detail = detail, Failed = failed });
        }

        public static Entry Add(Entry e)
        {
            if (quiet > 0 || e == null) return e;
            var now = NowOverride != null ? NowOverride() : DateTime.Now;
            if (string.IsNullOrEmpty(e.Id)) lock (rnd) e.Id = now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + rnd.Next(0x10000).ToString("x4");
            e.Time = now.ToString("s", CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(e.Section)) { string dv = Derive(e); e.Section = dv == "Restore points" || scope == null ? dv : scope; }
            e.Kind = Cut(e.Kind, 40); e.Action = Cut(e.Action, 200); e.Target = Cut(e.Target, 400);
            e.Before = Cut(e.Before, 300); e.After = Cut(e.After, 300); e.Detail = Cut(e.Detail, 1000);
            string line;
            try { line = new JavaScriptSerializer().Serialize(e); } catch { return e; }
            lock (gate)
            {
                Mutex m = null; bool owned = false;
                try
                {
                    try { m = new Mutex(false, @"Global\Elixar-ChangeLog"); owned = m.WaitOne(3000); } catch (AbandonedMutexException) { owned = true; } catch { }
                    string f = File_;
                    try { var fi = new FileInfo(f); if (fi.Exists && fi.Length > MaxBytes) { string old = OldFile(f); if (File.Exists(old)) File.Delete(old); File.Move(f, old); } } catch { }
                    Append(f, line + "\n");
                    // the same change, in plain words, in changes.txt (opens in Notepad)
                    string tf = TextFile(f);
                    try { var ti = new FileInfo(tf); if (ti.Exists && ti.Length > MaxBytes) { string old = OldText(tf); if (File.Exists(old)) File.Delete(old); File.Move(tf, old); } } catch { }
                    if (!File.Exists(tf))
                    {
                        // first time (or after it was deleted): everything still in the log, oldest first
                        var sb = new StringBuilder(TextHeader);
                        var all = Read(); all.Reverse();
                        foreach (var x in all) sb.Append(Block(x, all));
                        Append(tf, sb.ToString());
                    }
                    else Append(tf, Block(e, null));
                }
                finally { if (m != null) { if (owned) try { m.ReleaseMutex(); } catch { } m.Dispose(); } }
            }
            WinEvents.Write(e);   // and in Windows' Event Log, for the company's security tools
            return e;
        }

        static void Append(string f, string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    using (var s = new FileStream(f, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    { var b = new UTF8Encoding(false).GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n").Replace("\r\r", "\r")); s.Write(b, 0, b.Length); }
                    return;
                }
                catch (IOException) { Thread.Sleep(40); }
                catch { return; }
            }
        }

        // changes.txt is made from the log when it isn't there yet (a log from before it existed, or after it was deleted)
        public static void EnsureText()
        {
            lock (gate)
            {
                string tf = TextPath;
                if (File.Exists(tf)) return;
                var all = Read(); if (all.Count == 0) return;
                all.Reverse();
                var sb = new StringBuilder(TextHeader);
                foreach (var x in all) sb.Append(Block(x, all));
                Append(tf, sb.ToString());
            }
        }

        public static string TextFile(string f) { return Path.Combine(Path.GetDirectoryName(f) ?? "", Path.GetFileNameWithoutExtension(f) + ".txt"); }
        static string OldText(string tf) { return Path.Combine(Path.GetDirectoryName(tf) ?? "", Path.GetFileNameWithoutExtension(tf) + "-old.txt"); }
        public static string TextPath { get { return TextFile(File_); } }

        public const string TextHeader =
            "======================================================================\n" +
            " ELIXAR CHANGE LOG  -  every change Elixar made to this PC\n" +
            " Newest at the bottom. Passwords, PINs and tokens are never written here.\n" +
            " To undo a registry change: Elixar > Logs > select it > Undo this change.\n" +
            " To put files back: Elixar > Tools > Undo.\n" +
            "======================================================================\n\n";

        // one change, in plain words, for changes.txt and "Save the log as... (text)"
        public static string Block(Entry e, List<Entry> all)
        {
            var w = e.When;
            string status = StatusOf(e, all);
            var sb = new StringBuilder();
            sb.Append(w == DateTime.MinValue ? e.Time : w.ToString("ddd d MMM yyyy  HH:mm:ss", CultureInfo.InvariantCulture)).Append("   ").Append(SectionOf(e).ToUpperInvariant()).Append("  (").Append(e.Kind ?? "").Append(")");
            if (status == "Failed") sb.Append("   ** FAILED **");
            else if (status == "Can be undone") sb.Append("   (can be undone in Elixar > Logs)");
            else if (status == "In Undo") sb.Append("   (files can be put back in Tools > Undo)");
            else if (status == "Undone") sb.Append("   (undone later)");
            sb.Append("\n");
            Action<string, string> f = (k, v) => { if (!string.IsNullOrEmpty(v)) sb.Append("  ").Append((k + ":").PadRight(9)).Append(v).Append("\n"); };
            string sum = Summary(e);
            f("What", sum);
            if (sum != e.Action) f("Change", e.Action);
            f(e.Kind == "Windows command" ? "Command" : "Where", e.Target);
            f("Before", e.Before);
            f("After", e.After);
            if (e.Kind != "Registry" || sum != e.Detail) f("Details", e.Detail);
            f("ID", e.Id + (string.IsNullOrEmpty(e.Ref) ? "" : "   (undoes " + e.Ref + ")"));
            sb.Append("\n");
            return sb.ToString();
        }

        // the line in plain words: for a registry value, why Elixar changed it ("Startup: turned off Spotify")
        public static string Summary(Entry e)
        {
            if (e == null) return "";
            if (e.Kind == "Registry" && !string.IsNullOrEmpty(e.Detail)) return e.Detail;
            return e.Action ?? "";
        }

        // what to do to reverse it, for the details under the list
        public static string HowToUndo(Entry e, string status)
        {
            if (status == "Can be undone") return e.Old == null ? "Click \"Undo this change\" to remove this value again." : "Click \"Undo this change\" to put the value back as it was before.";
            if (status == "Undone") return "Already undone (see the line it points to).";
            if (status == "In Undo") return "Click \"Open Tools > Undo\" to put the files back, until they are deleted for good.";
            if (status == "Failed") return "Nothing to undo: it did not work. The details say why.";
            if (e.Kind == "Security" || (e.Detail ?? "").Contains("not put back on purpose") || (e.Before ?? "").StartsWith("(hidden")) return "Not put back from here on purpose: it was a security fix.";
            if (e.Kind == "Cleaning") return "What went to Undo can be put back in Tools > Undo; the rest (caches, temp files) is rebuilt by Windows and apps when needed.";
            if (e.Kind == "Windows command") return "Windows commands can't be reversed from Elixar. A restore point from before the change (Tools) takes the PC back.";
            if (e.Kind == "Registry") return "Can't be undone from here: what was there is not kept (privacy or security).";
            if (e.Kind == "Settings") return "Change it back where you set it.";
            if (e.Kind == "Email") return "Connect or disconnect the account again in Settings > Email account.";
            if (e.Kind == "Phone") return "Pre-installed phone apps come back with \"Put back\" in the Phone tab; deleted phone files are gone.";
            if (e.Kind == "Apps") return "Install the app again from its website or the Microsoft Store.";
            return "";
        }

        // newest first; the older file too, so a long history is still there
        public static List<Entry> Read()
        {
            var list = new List<Entry>();
            var js = new JavaScriptSerializer();
            string f = File_;
            foreach (var path in new[] { OldFile(f), f })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    string text;
                    using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
                    foreach (var raw in text.Split('\n'))
                    {
                        string l = raw.Trim();
                        if (l.Length < 2 || l[0] != '{') continue;
                        try { var e = js.Deserialize<Entry>(l); if (e != null && !string.IsNullOrEmpty(e.Id)) list.Add(e); } catch { }
                    }
                }
                catch { }
            }
            list.Reverse();
            // newest first by the time of the change (a stable sort: changes in the same second keep their order)
            return list.Select((e, i) => new { e, i }).OrderByDescending(x => x.e.When).ThenBy(x => x.i).Select(x => x.e).ToList();
        }

        public static string StatusOf(Entry e, List<Entry> all)
        {
            if (e.Failed) return "Failed";
            if (e.Kind == "Undo") return "Undo";
            if (all != null && all.Any(x => x.Ref == e.Id)) return "Undone";
            if (e.CanUndo) return "Can be undone";
            if (e.Kind == "Files" && (e.Action ?? "").StartsWith("Moved")) return "In Undo";
            return "Done";
        }

        // ================= Windows commands =================
        static string Has(string args, params string[] words)
        {
            foreach (var w in words) if (args.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return w;
            return null;
        }

        // which of Elixar's commands change something, in plain words; null = it only reads
        public static string Describe(string exe, string args)
        {
            string x = Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant(); args = args ?? "";
            switch (x)
            {
                case "dism":
                    if (Has(args, "/StartComponentCleanup") != null) return "Windows component cleanup (DISM)" + (Has(args, "/ResetBase") != null ? ", without uninstall of updates" : "");
                    if (Has(args, "/Import-DefaultAppAssociations") != null) return "Default apps for new users (DISM)";
                    if (Has(args, "/Cleanup-Mountpoints") != null) return "Removed leftover mounted images (DISM)";
                    if (Has(args, "/RestoreHealth") != null) return "Windows repair (DISM)";
                    return null;
                case "sfc":
                    return Has(args, "/scannow") != null ? "Windows repair (SFC)" : null;
                case "defrag":
                    return Has(args, "/L") != null ? "TRIM on an SSD (defrag /L)" : null;
                case "powercfg":
                    if (Has(args, "/hibernate", "-h ") != null) return "Hibernation: " + args.Trim();
                    return Has(args, "/change", "/setactive", "/delete", "/import") != null ? "Power settings: " + args.Trim() : null;
                case "vssadmin":
                    if (Has(args, "delete") != null) return "Deleted restore points (vssadmin)";
                    return Has(args, "resize") != null ? "Changed the space for restore points (vssadmin)" : null;
                case "netsh":
                    if (Has(args, " delete ", "delete profile") != null) return "Removed a saved Wi-Fi network";
                    return Has(args, " add ", " set ") != null ? "Network setting: " + args.Trim() : null;
                case "cleanmgr": return Has(args, "/sagerun") != null ? "Windows Disk Cleanup ran" : null;
                case "schtasks":
                    if (Has(args, "/create") != null) return "Created or updated a scheduled task";
                    if (Has(args, "/delete") != null) return "Deleted a scheduled task";
                    if (Has(args, "/change") != null) return "Changed a scheduled task";
                    return null;
                case "compact":
                    if (Has(args, "/compactos:always") != null) return "Compact OS turned on";
                    if (Has(args, "/compactos:never") != null) return "Compact OS turned off";
                    return Has(args, "/c ", "/u ") != null && Has(args, "/compactos:query") == null ? (Has(args, "/u ") != null ? "Uncompressed files" : "Compressed files (NTFS)") : null;
                case "diskpart": return "Compacted a virtual disk (diskpart)";
                case "pnputil":
                    if (Has(args, "/delete-driver", "-d ") != null) return "Removed a driver package";
                    return Has(args, "/add-driver", "-a ") != null ? "Added a driver package" : null;
                case "sysprep": return "Sysprep: " + args.Trim();
                case "ipconfig": return Has(args, "/flushdns") != null ? "Cleared the DNS cache" : null;
                case "reg":
                    return Has(args, "add ", "delete ", "import ", "restore ", "load ", "unload ") != null ? "Registry tool: " + args.Trim() : null;
                default: return null;
            }
        }

        public static void Command(string exe, string args, string output, int exitCode)
        {
            try
            {
                string what = Describe(exe, args);
                if (what == null) return;
                string o = (output ?? "").Trim();
                bool failed = o == "timed out" || exitCode > 0 && exitCode != 3010 /* restart needed */;
                Add("Windows command", what, Path.GetFileName(exe ?? "") + " " + (args ?? ""), "", failed ? "Failed (code " + exitCode + ")" : "", Tail(o, 400), failed);
            }
            catch { }
        }

        static string Tail(string s, int n) { s = (s ?? "").Trim(); return s.Length > n ? "..." + s.Substring(s.Length - n) : s; }

        // PowerShell: the commands in a script that change something
        static readonly Regex Changing = new Regex(@"\b(?:Remove|Set|New|Add|Clear|Delete|Disable|Enable|Import|Register|Unregister|Install|Uninstall|Checkpoint|Suspend|Resume|Restart|Update|Rename|Move|Copy|Reset|Repair|Optimize|Resize|Format|Initialize|Mount|Dismount|Stop|Start|Invoke)-[A-Za-z]+\b", RegexOptions.IgnoreCase);
        static readonly HashSet<string> NotChanges = new HashSet<string>(new[] { "Set-StrictMode", "Set-Location", "Set-Variable", "New-Object", "Add-Type", "Add-Member", "New-TimeSpan", "Start-Sleep", "Import-Module",
                                                                                  "Invoke-Expression", "Stop-Process", "Start-Process", "Invoke-WebRequest", "Invoke-RestMethod", "New-Item" }, StringComparer.OrdinalIgnoreCase);
        static readonly Regex Secret = new Regex(@"(-(?:Password|Secret|Token|Key|Credential)\w*\s+)(?:'[^']*'|""[^""]*""|\S+)", RegexOptions.IgnoreCase);

        public static List<string> ChangingCmdlets(string script)
        {
            return Changing.Matches(script ?? "").Cast<Match>().Select(m => m.Value).Where(v => !NotChanges.Contains(v) && !v.StartsWith("Start-", StringComparison.OrdinalIgnoreCase) || v.Equals("Start-Service", StringComparison.OrdinalIgnoreCase))
                           .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // each changing command with what follows it, up to the next ; | or }
        public static string Commands(string script)
        {
            var parts = new List<string>();
            foreach (Match m in Changing.Matches(script ?? ""))
            {
                if (!ChangingCmdlets(m.Value).Any()) continue;
                int end = m.Index;
                int depth = 0; bool q1 = false, q2 = false;
                for (; end < script.Length; end++)
                {
                    char c = script[end];
                    if (c == '\'' && !q2) q1 = !q1; else if (c == '"' && !q1) q2 = !q2;
                    else if (!q1 && !q2) { if (c == '(') depth++; else if (c == ')') { if (depth == 0) break; depth--; } else if ((c == ';' || c == '|' || c == '}') && depth == 0) break; }
                }
                parts.Add(Secret.Replace(script.Substring(m.Index, end - m.Index).Trim(), "$1(hidden)"));
            }
            return string.Join("; ", parts.Distinct());
        }

        public static void PowerShell(string script, string output)
        {
            try
            {
                var c = ChangingCmdlets(script);
                if (c.Count == 0) return;
                string o = (output ?? "").Trim();
                bool failed = o.StartsWith("ERROR") || o == "timed out";
                string all = string.Join(", ", c);
                string kind = all.IndexOf("Appx", StringComparison.OrdinalIgnoreCase) >= 0 ? "Apps"
                            : Regex.IsMatch(all, "BitLocker|LocalUser|MpPreference|Firewall", RegexOptions.IgnoreCase) ? "Security" : "Windows command";
                string what = c.Count == 1 && c[0].Equals("Checkpoint-Computer", StringComparison.OrdinalIgnoreCase) ? "Created a restore point"
                            : c.Count == 1 && c[0].Equals("Remove-AppxPackage", StringComparison.OrdinalIgnoreCase) ? "Removed a Microsoft Store app" : "PowerShell: " + all;
                if (what == "Created a restore point" && (o.StartsWith("Not created") || o.Contains("Not created"))) failed = true;
                Add(kind, what, Commands(script), "", failed ? Cut(o, 200) : "", Tail(o, 400), failed);
            }
            catch { }
        }

        // one line for each item a clean runs (the files it moved to Undo and the commands it ran have their own lines)
        public static void Cleaned(CleanItem it, long freed, string problem, string how)
        {
            try
            {
                if (it == null) return;
                if (problem == null && freed > 0) Savings.Add(freed, it.Name);
                Add("Cleaning", "Cleaned: " + it.Name, it.Where ?? "", "", problem != null ? "Problem" : freed > 0 ? "freed " + Fs.Fmt(freed) : "",
                    (how ?? "") + (problem != null ? " - " + problem : ""), problem != null);
            }
            catch { }
        }

        // ================= files =================
        public static void Files(string action, string label, IList<string> paths, long bytes, string detail)
        {
            try
            {
                string list = paths == null ? "" : string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? " and " + (paths.Count - 5) + " more" : "");
                Add("Files", action + (string.IsNullOrEmpty(label) ? "" : ": " + label), list, "", bytes > 0 ? Fs.Fmt(bytes) : "", detail, false);
            }
            catch { }
        }
    }

    // ================= registry: every value Elixar writes or deletes, with what it was before =================
    public static class RegLog
    {
        // the only keys an undo may write to (also if the log file were ever changed by hand)
        static readonly string[] UndoKeys =
        {
            @"Software\Elixar",
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Elixar",
            @"SYSTEM\Setup",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SoftwareProtectionPlatform",
            @"SOFTWARE\Policies\Microsoft\WindowsStore",
        };
        // ...and the values Tools > Windows tweaks changes
        static readonly string[] TweakKeys = Tweaks.Keys.ToArray();

        public static bool UndoAllowed(string hive, string key)
        {
            if (hive != "HKLM" && hive != "HKCU") return false;
            key = (key ?? "").Trim('\\');
            if (key.Length == 0 || key.Contains("..")) return false;
            return UndoKeys.Concat(TweakKeys).Any(p => key.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || key.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }

        static bool Sensitive(string name) { return Regex.IsMatch(name ?? "", "password|secret|token|credential|key$", RegexOptions.IgnoreCase); }

        static void Split(RegistryKey k, out string hive, out string key)
        {
            string n = k.Name ?? ""; int i = n.IndexOf('\\');
            string h = i < 0 ? n : n.Substring(0, i); key = i < 0 ? "" : n.Substring(i + 1);
            hive = h == "HKEY_LOCAL_MACHINE" ? "HKLM" : h == "HKEY_CURRENT_USER" ? "HKCU" : h == "HKEY_USERS" ? "HKU" : h == "HKEY_CLASSES_ROOT" ? "HKCR" : h;
        }

        static string ViewOf(RegistryKey k) { try { return k.View.ToString(); } catch { return "Default"; } }

        public static string Target(string hive, string key, string name, string view)
        {
            return hive + "\\" + key + (view == "Registry32" ? " (32-bit)" : "") + "  >  " + (string.IsNullOrEmpty(name) ? "(Default)" : name);
        }

        static object ReadRaw(RegistryKey k, string name, out RegistryValueKind kind)
        {
            kind = RegistryValueKind.Unknown;
            try
            {
                object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (v == null) return null;
                try { kind = k.GetValueKind(name); } catch { kind = v is int ? RegistryValueKind.DWord : v is long ? RegistryValueKind.QWord : v is byte[] ? RegistryValueKind.Binary : v is string[] ? RegistryValueKind.MultiString : RegistryValueKind.String; }
                return v;
            }
            catch { return null; }
        }

        // kind:payload, so the exact value can be put back
        public static string Encode(object v, RegistryValueKind kind)
        {
            if (v == null) return null;
            switch (kind)
            {
                case RegistryValueKind.String: return "sz:" + v;
                case RegistryValueKind.ExpandString: return "expand:" + v;
                case RegistryValueKind.DWord: return "dword:" + unchecked((uint)Convert.ToInt32(v)).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.QWord: return "qword:" + unchecked((ulong)Convert.ToInt64(v)).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.Binary: return "hex:" + BitConverter.ToString((byte[])v).Replace("-", "");
                case RegistryValueKind.MultiString: return "multi:" + new JavaScriptSerializer().Serialize((string[])v);
                default: return null;
            }
        }

        public static object Decode(string s, out RegistryValueKind kind)
        {
            kind = RegistryValueKind.Unknown;
            if (s == null) return null;
            int i = s.IndexOf(':'); if (i < 0) return null;
            string t = s.Substring(0, i), p = s.Substring(i + 1);
            switch (t)
            {
                case "sz": kind = RegistryValueKind.String; return p;
                case "expand": kind = RegistryValueKind.ExpandString; return p;
                case "dword": kind = RegistryValueKind.DWord; return unchecked((int)uint.Parse(p, CultureInfo.InvariantCulture));
                case "qword": kind = RegistryValueKind.QWord; return unchecked((long)ulong.Parse(p, CultureInfo.InvariantCulture));
                case "hex":
                    kind = RegistryValueKind.Binary;
                    if (p.Length % 2 != 0) return null;
                    var b = new byte[p.Length / 2];
                    for (int j = 0; j < b.Length; j++) b[j] = Convert.ToByte(p.Substring(j * 2, 2), 16);
                    return b;
                case "multi": kind = RegistryValueKind.MultiString; return new JavaScriptSerializer().Deserialize<string[]>(p);
                default: return null;
            }
        }

        // how a value reads in the log
        public static string Show(string encoded)
        {
            if (encoded == null) return "(not there)";
            RegistryValueKind kind;
            object v;
            try { v = Decode(encoded, out kind); } catch { return "?"; }
            if (v == null) return "?";
            switch (kind)
            {
                case RegistryValueKind.DWord: return ((uint)(int)v) + " (0x" + ((uint)(int)v).ToString("X") + ")";
                case RegistryValueKind.QWord: return ((ulong)(long)v).ToString(CultureInfo.InvariantCulture);
                case RegistryValueKind.Binary: var b = (byte[])v; return BitConverter.ToString(b.Take(24).ToArray()).Replace("-", " ") + (b.Length > 24 ? " ..." : "") + (b.Length == 0 ? "(empty)" : "");
                case RegistryValueKind.MultiString: return string.Join(" | ", (string[])v);
                default: return "\"" + v + "\"";
            }
        }

        // a startup switch reads On or Off (Windows: an even first byte is on, odd is off)
        public static string Friendly(string key, string enc)
        {
            string shown = Show(enc);
            if (enc != null && enc.StartsWith("hex:") && enc.Length >= 6 && (key ?? "").IndexOf(@"\StartupApproved\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int b; if (int.TryParse(enc.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b)) return (b % 2 == 0 ? "On" : "Off") + "  (" + shown + ")";
            }
            return shown;
        }

        [ThreadStatic] static string pendingRef;
        [ThreadStatic] static bool written;

        static ChangeLog.Entry Record(RegistryKey k, string name, string oldEnc, string newEnc, string why, bool secret, bool undoable)
        {
            string hive, key; Split(k, out hive, out key);
            string view = ViewOf(k);
            bool hide = secret || Sensitive(name);
            var e = new ChangeLog.Entry
            {
                Kind = "Registry",
                Action = pendingRef != null ? "Put back the old value" : oldEnc == null ? "Added a value" : newEnc == null ? "Deleted a value" : "Changed a value",
                Target = Target(hive, key, name, view),
                Before = hide && oldEnc != null ? "(hidden: secret)" : Friendly(key, oldEnc),
                After = hide && newEnc != null ? "(hidden: secret)" : Friendly(key, newEnc),
                Detail = why,
                Ref = pendingRef,
            };
            written = true;
            if (undoable && !hide && UndoAllowed(hive, key) && (oldEnc == null || Decodes(oldEnc)) && (newEnc == null || Decodes(newEnc)))
            { e.Hive = hive; e.View = view; e.Key = key; e.Name = name; e.Old = oldEnc; e.New = newEnc; e.CanUndo = true; }
            return ChangeLog.Add(e);
        }

        static bool Decodes(string s) { try { RegistryValueKind k; return Decode(s, out k) != null; } catch { return false; } }

        // sets a value and writes down what it was before (nothing is written when it already had that value)
        public static void Set(RegistryKey k, string name, object value, RegistryValueKind kind, string why) { Set(k, name, value, kind, why, true); }
        public static void Set(RegistryKey k, string name, object value, RegistryValueKind kind, string why, bool undoable)
        {
            RegistryValueKind oldKind; object old = ReadRaw(k, name, out oldKind);
            string oldEnc = Encode(old, oldKind);
            k.SetValue(name, value, kind);
            string newEnc = Encode(value, kind);
            if (old != null && oldEnc == newEnc) return;
            try { Record(k, name, old == null ? null : oldEnc ?? "?", newEnc, why, false, undoable && (old == null || oldEnc != null)); } catch { }
        }

        // deletes a value and writes down what it was; secret = the value itself is never written down (a saved password)
        public static bool Delete(RegistryKey k, string name, string why) { return Delete(k, name, why, false, true); }
        public static bool Delete(RegistryKey k, string name, string why, bool secret, bool undoable)
        {
            RegistryValueKind oldKind; object old = ReadRaw(k, name, out oldKind);
            if (old == null) return false;
            k.DeleteValue(name, false);
            string oldEnc = Encode(old, oldKind);
            try { Record(k, name, oldEnc ?? "?", null, why, secret, undoable && oldEnc != null); } catch { }
            return true;
        }

        // a whole key, or everything in it: only how much is written down, never what was in it
        public static void ClearedKey(RegistryKey k, string sub, int values, int keys, string why)
        {
            try
            {
                string hive, key; Split(k, out hive, out key);
                if (!string.IsNullOrEmpty(sub)) key = key + "\\" + sub;
                if (values == 0 && keys == 0) return;
                ChangeLog.Add(new ChangeLog.Entry
                {
                    Kind = "Registry", Action = string.IsNullOrEmpty(sub) ? "Emptied a key" : "Deleted a key", Target = hive + "\\" + key,
                    Before = values + " values, " + keys + " subkeys", After = "(empty)", Detail = why
                });
            }
            catch { }
        }

        static RegistryKey Base(string hive, string view)
        {
            var v = view == "Registry64" ? RegistryView.Registry64 : view == "Registry32" ? RegistryView.Registry32 : RegistryView.Default;
            return RegistryKey.OpenBaseKey(hive == "HKLM" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, v);
        }

        // what the value is now, encoded like the log
        public static string Current(ChangeLog.Entry e)
        {
            using (var b = Base(e.Hive, e.View))
            using (var k = b.OpenSubKey(e.Key))
            {
                if (k == null) return null;
                RegistryValueKind kind; object v = ReadRaw(k, e.Name, out kind);
                return Encode(v, kind);
            }
        }

        // null when the value is still exactly what Elixar left; otherwise how it reads now
        public static string ChangedSince(ChangeLog.Entry e)
        {
            string now = Current(e);
            return now == e.New ? null : Show(now);
        }

        // puts the old value back (or removes a value Elixar added); written down as a change of its own, which can be undone too
        public static string Undo(ChangeLog.Entry e)
        {
            if (e == null || !e.CanUndo) return "This change can't be undone from here.";
            if (!UndoAllowed(e.Hive, e.Key)) return "Elixar only puts back values in the keys it changes itself.";
            if (e.Hive == "HKLM" && !Sys.IsAdmin()) return "Run Elixar as administrator to undo this.";
            try
            {
                using (var b = Base(e.Hive, e.View))
                using (var k = e.Old == null ? b.OpenSubKey(e.Key, true) : b.CreateSubKey(e.Key))
                {
                    string why = "Undo of: " + ChangeLog.Summary(e);
                    pendingRef = e.Id; written = false;
                    try
                    {
                        if (e.Old == null) { if (k != null) Delete(k, e.Name, why); }
                        else { RegistryValueKind kind; object v = Decode(e.Old, out kind); if (v == null) return "The old value could not be read."; Set(k, e.Name, v, kind, why); }
                    }
                    finally { pendingRef = null; }
                    // it already had the old value: still written down, so the change shows as undone
                    if (!written) ChangeLog.Add(new ChangeLog.Entry { Kind = "Undo", Action = "Already back to the old value", Target = e.Target, Before = e.After, After = e.Before, Detail = why, Ref = e.Id });
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }
    }
}
