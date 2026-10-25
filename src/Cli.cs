// Command line for IT and scripts. No window, no questions; it needs an administrator command prompt and follows the policy
// (a blocked action stays blocked). Every change goes into Logs as usual. Exit codes: 0 done, 1 problem, 2 blocked by the policy,
// 3 not administrator, 4 wrong command.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CDriveCleaner
{
    public static class Cli
    {
        public static readonly string[] Commands = { "--help", "/?", "--status", "--clean", "--clean-item", "--report", "--policy", "--tweaks", "--update-apps", "--list-items" };

        public static bool IsCli(string[] args) { return args.Length > 0 && Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase); }

        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        public static Action<string> Out = Console.WriteLine;   // self-test: catch the output
        public static bool AssumeAdmin;                         // self-test
        public static bool NoSchedules;                         // self-test: never touch Task Scheduler

        public const string Help =
            "Elixar command line (run it from an administrator command prompt)\n\n" +
            "  Elixar.exe --status                    free space, savings, policy and tweaks\n" +
            "  Elixar.exe --clean                     the weekly clean: Safe items ticked by default\n" +
            "  Elixar.exe --clean-item \"<name>\"       one cleaning item by its name (see --list-items)\n" +
            "  Elixar.exe --list-items                every cleaning item with its level\n" +
            "  Elixar.exe --report <folder|file.json> [--with-updates]   status file for Policy manager > Fleet report\n" +
            "  Elixar.exe --policy <file.elixarpolicy> use this policy as the Custom policy\n" +
            "  Elixar.exe --tweaks on|default         every Windows tweak on, or all back to Windows' default\n" +
            "  Elixar.exe --update-apps               update every app winget can update (Microsoft's winget and Store sources only)\n" +
            "  Elixar.exe --uninstall --quiet [--purge]  remove Elixar; --purge also deletes its Undo backups, logs and settings\n\n" +
            "Exit codes: 0 done, 1 problem, 2 blocked by the policy, 3 not administrator, 4 wrong command.\n" +
            "In cmd, start it with  start /wait Elixar.exe ...  so the prompt waits for it (then %errorlevel% is the exit code).\n" +
            "Everything Elixar changes is written in Logs, the same as from the window.";

        static string Arg(string[] a, int i) { return i < a.Length ? a[i] : null; }

        public static int Run(string[] args)
        {
            if (Win.IsWindows && Out == (Action<string>)Console.WriteLine) try { AttachConsole(-1); Out(""); } catch { }
            string cmd = args[0].ToLowerInvariant();
            if (cmd == "--help" || cmd == "/?") { Out(Help); return 0; }
            if (Win.IsWindows && !AssumeAdmin && !Sys.IsAdmin()) { Out("Elixar: run this from an administrator command prompt."); return 3; }
            try
            {
                using (ChangeLog.In("Command line"))
                    switch (cmd)
                    {
                        case "--status": return Status();
                        case "--list-items":
                            foreach (var it in Catalog.Build()) Out(Tag(it.Tier) + "  " + it.Name + (Policy.Never(it) ? "  (never, by the policy)" : it.Tier > Policy.MaxTier ? "  (above the policy's level)" : ""));
                            return 0;
                        case "--clean": return Automation.AutoClean(false) == 0 ? 0 : 1;
                        case "--clean-item": return CleanItem(Arg(args, 1));
                        case "--report": return Report(Arg(args, 1), args.Contains("--with-updates"));
                        case "--policy": return UsePolicy(Arg(args, 1));
                        case "--tweaks": return SetTweaks(Arg(args, 1));
                        case "--update-apps": return UpdateApps();
                    }
            }
            catch (Exception ex) { Out("Elixar: " + ex.Message); return 1; }
            Out(Help); return 4;
        }

        static string Tag(Tier t) { return t == Tier.Safe ? "[Safe]      " : t == Tier.Review ? "[Check]     " : "[No undo]   "; }

        static int Status()
        {
            var s = Fleet.Collect(false);
            Out("Elixar " + s.Elixar + " on " + s.Computer);
            Out("C: " + Fs.Fmt(s.Free) + " free of " + Fs.Fmt(s.Total) + " (" + s.FreePercent + "%)");
            Out("Saved by Elixar: " + (s.Saved > 0 ? Fs.Fmt(s.Saved) : "nothing yet") + (s.SavedThisMonth > 0 ? " (" + Fs.Fmt(s.SavedThisMonth) + " this month)" : ""));
            Out("Last clean: " + (string.IsNullOrEmpty(s.LastClean) ? "never" : s.LastClean));
            Out("Policy: " + s.PolicyMode + " (" + s.PolicyFingerprint + ")");
            Out("Windows tweaks on: " + (s.TweaksOn.Length == 0 ? "none" : string.Join(", ", s.TweaksOn)));
            Out("In Undo: " + Fs.Fmt(s.UndoBytes));
            return 0;
        }

        static int CleanItem(string name)
        {
            if (string.IsNullOrEmpty(name)) { Out("Elixar: give the item's name. --list-items shows them."); return 4; }
            var it = Catalog.Build().FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
            if (it == null) { Out("Elixar: no cleaning item called \"" + name + "\". --list-items shows them."); return 4; }
            if (Policy.Never(it) || it.Tier > Policy.MaxTier) { Out("Elixar: the policy doesn't allow cleaning \"" + it.Name + "\"."); return 2; }
            if (it.RootScript != null) { Out("Elixar: \"" + it.Name + "\" needs the window (it asks for your password)."); return 1; }
            if (it.Procs.Length > 0 && Sys.FindProcesses(it.Procs).Count > 0) { Out("Elixar: skipped \"" + it.Name + "\": the app is open (it is never closed by Elixar)."); return 1; }
            long before = Sys.Free(); string problem = null;
            try { using (ChangeLog.In("Command line")) it.Clean(m => Out("  " + m)); } catch (Exception ex) { problem = ex.Message; }
            long got = Math.Max(0, Sys.Free() - before);
            ChangeLog.Cleaned(it, got, problem, "Command line");
            Automation.Log("Command line: cleaned " + it.Name + ", freed " + Fs.Fmt(got) + (problem != null ? " (problem: " + problem + ")" : ""));
            Out(problem == null ? "Cleaned \"" + it.Name + "\": freed " + Fs.Fmt(got) + "." : "Problem with \"" + it.Name + "\": " + problem);
            return problem == null ? 0 : 1;
        }

        static int Report(string where, bool withUpdates)
        {
            if (string.IsNullOrEmpty(where)) { Out("Elixar: give a folder (or a .json file) for the report."); return 4; }
            string f = Fleet.Write(where, Fleet.Collect(withUpdates));
            Out("Report written: " + f);
            return 0;
        }

        static int UsePolicy(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) { Out("Elixar: give an .elixarpolicy file."); return 4; }
            if (Policy.Get("pin.policy") == "on" && PinLock.IsSet) { Out("Elixar: this policy is protected by a PIN. Change it in Policy manager, or set it for the organization (HKLM\\SOFTWARE\\Policies\\Elixar)."); return 2; }
            if (new FileInfo(file).Length > 64 * 1024) { Out("Elixar: not an Elixar policy file."); return 1; }
            var res = Policy.Import(File.ReadAllText(file));
            foreach (var p in res.Problems) Out("  left out: " + p);
            if (res.HadFingerprint && !res.FingerprintOk) { Out("Elixar: the file was changed after it was exported (its fingerprint doesn't match). Nothing was changed."); return 1; }
            string err = Policy.Save(true, res.Rules);
            if (err != null) { Out("Elixar: " + err); return 1; }
            Out("Custom policy saved (" + Policy.Fingerprint(Policy.Rules.ToDictionary(r => r.Key, r => Policy.Get(r.Key))) + ").");
            if (!NoSchedules) try { var errs = Automation.ApplySchedules(Automation.SavedPercent(), null); foreach (var e in errs) Out("  schedule: " + e); } catch { }
            int n = Tweaks.ApplyPolicy(m => Out("  " + m));
            if (n > 0) Out(n + " Windows tweaks set by the policy.");
            return 0;
        }

        static int SetTweaks(string how)
        {
            bool on = how == "on";
            if (how != "on" && how != "default") { Out("Elixar: --tweaks on  or  --tweaks default"); return 4; }
            int fixedBy = 0, errs = 0;
            foreach (var t in Tweaks.All)
            {
                string p = Policy.Get(Tweaks.PolicyKey(t));
                if (p != "choice") { fixedBy++; Out("  " + t.Name + ": set by the policy (" + p + ")"); continue; }
                if ((Tweaks.State(t) == TweakState.On) == on && Tweaks.State(t) != TweakState.Partly) { Out("  " + t.Name + ": already " + (on ? "on" : "Windows' default")); continue; }
                var e = Tweaks.Set(t, on, null);
                errs += e.Count;
                Out("  " + t.Name + ": " + (e.Count == 0 ? (on ? "on" : "Windows' default") : string.Join("; ", e)));
            }
            Out("Some tweaks show after you sign out or restart.");
            return errs > 0 ? 1 : fixedBy == Tweaks.All.Length ? 2 : 0;
        }

        static int UpdateApps()
        {
            if (!Policy.Allowed("apps.update")) { Out("Elixar: the policy doesn't allow updating apps."); return 2; }
            string err; var list = WinGet.List(out err);
            if (err != null) { Out("Elixar: " + err); return 1; }
            if (list.Count == 0) { Out("Every app is up to date."); return 0; }
            int bad = 0;
            foreach (var u in list)
            {
                Out("Updating " + u.Name + " " + u.Version + " -> " + u.Available + "...");
                if (!WinGet.Upgrade(u, m => Out("  " + m))) bad++;
            }
            Out((list.Count - bad) + " of " + list.Count + " apps updated.");
            return bad > 0 ? 1 : 0;
        }
    }
}
