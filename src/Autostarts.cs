// Hidden autostarts: besides the Startup list, programs can start themselves through scheduled tasks and services, which
// most people never see; that's where unwanted software often hides. Elixar lists those that apps added (Windows' own are
// hidden unless asked) and flags the ones worth a look: not signed, running from your folders or Temp, running a hidden
// script, or whose program is gone. Tasks can be turned off and on again (Task Scheduler's own switch); services are only
// shown, because turning one off can break the app or Windows. Nothing is deleted.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class AutoEntry : StartupEntry
    {
        public string Kind = "", Runs = "", TaskPath = "", Args = "";
        public bool WindowsOwn, ElixarOwn;
    }

    public static class Autostarts
    {
        public static Func<List<AutoEntry>> ReadOverride;              // self-test: what Windows has
        public static Func<string, string, int> RunOverride;           // self-test: schtasks
        static string TasksRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks"); } }

        public static List<AutoEntry> Read(bool checkSignatures) { return Read(checkSignatures, false); }
        // onlyRiskyPlaces: signatures only for programs in your folders or ProgramData (where "not signed" decides
        // something), for the PC health check that runs often
        public static List<AutoEntry> Read(bool checkSignatures, bool onlyRiskyPlaces)
        {
            var list = ReadOverride != null ? ReadOverride() : (Win.IsWindows ? Tasks().Concat(Services()).ToList() : new List<AutoEntry>());
            if (checkSignatures)
            {
                Func<AutoEntry, bool> risky = e => { string f = (e.File ?? "").ToLowerInvariant(); return f.Contains(@"\appdata\") || f.StartsWith(ProgramDataRoot()) || f.Contains(@"\users\"); };
                var sig = Trust.CheckMany(list.Where(e => !e.Missing && !e.ElixarOwn && !e.WindowsOwn && (!onlyRiskyPlaces || risky(e))).Select(e => e.File));
                foreach (var e in list)
                {
                    string[] r;
                    if (sig.TryGetValue(e.File ?? "", out r)) { e.SigStatus = r[0]; e.Signer = r.Length > 1 ? r[1] : ""; }
                }
            }
            foreach (var e in list) Judge(e);
            return list;
        }

        // Windows' own: under Windows' folder and signed by Microsoft (or a task in Task Scheduler's \Microsoft\ folder)
        static bool Microsoft(AutoEntry e) { return e.Signer.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0; }
        static string WinDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }

        public static void Judge(AutoEntry e)
        {
            if (e.ElixarOwn) { e.Flag = StartupFlag.Ok; e.Why = "Elixar's own"; return; }
            StartupApps.Judge(e);
            string f = (e.File ?? "").ToLowerInvariant().Replace('/', '\\');
            // unsigned and in your folders (AppData) or straight in ProgramData: real programs rarely do both
            if (e.Flag == StartupFlag.Review && e.SigStatus == "NotSigned" && (f.Contains(@"\appdata\") || f.StartsWith(ProgramDataRoot())))
            { e.Flag = StartupFlag.Danger; e.Why = "not signed, and runs from your user folders or ProgramData - typical of unwanted software"; }
            if (!string.IsNullOrEmpty(WinDir) && f.StartsWith(WinDir.ToLowerInvariant() + "\\") && (e.SigStatus == "Valid" && Microsoft(e))) e.WindowsOwn = true;
            if (e.Flag == StartupFlag.Danger) e.WindowsOwn = false;   // whatever looks like unwanted software is always shown
        }
        static bool UnderWindows(string file)
        {
            string w = WinDir;
            if (string.IsNullOrEmpty(w) || string.IsNullOrEmpty(file)) return !Win.IsWindows && (file ?? "").StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase);
            try { return Path.GetFullPath(file).StartsWith(w.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }
        static string ProgramDataRoot()
        {
            string pd = (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) ?? "").ToLowerInvariant();
            return pd.Length == 0 ? "\u0001" : pd + "\\";
        }

        // ---------- scheduled tasks: Task Scheduler keeps each one as an XML file under System32\Tasks ----------
        static List<AutoEntry> Tasks()
        {
            var list = new List<AutoEntry>();
            string root = TasksRoot;
            if (!Directory.Exists(root)) return list;
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0 && list.Count < 2000)
            {
                string d = stack.Pop();
                try
                {
                    foreach (var sub in Directory.GetDirectories(d))
                        if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0) stack.Push(sub);
                    foreach (var file in Directory.GetFiles(d))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0 || fi.Length > 1 << 20) continue;
                            string path = "\\" + file.Substring(root.Length).TrimStart('\\');
                            var e = ParseTask(File.ReadAllText(file), path);
                            if (e != null) list.Add(e);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return list;
        }

        public static AutoEntry ParseTask(string xml, string taskPath)
        {
            var doc = new XmlDocument { XmlResolver = null };
            using (var sr = new StringReader(xml ?? ""))
            using (var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                doc.Load(xr);
            var exec = doc.GetElementsByTagName("Exec").Cast<XmlNode>().FirstOrDefault();
            if (exec == null) return null;   // COM-handler tasks: Windows' own components
            string cmd = Child(exec, "Command"), args = Child(exec, "Arguments");
            if (cmd.Length == 0) return null;
            string expanded = Environment.ExpandEnvironmentVariables(cmd.Trim().Trim('"'));
            var en = doc.GetElementsByTagName("Enabled").Cast<XmlNode>().FirstOrDefault(n => n.ParentNode != null && n.ParentNode.LocalName == "Settings");
            var e = new AutoEntry
            {
                Kind = "Task", TaskPath = taskPath, ItemPath = taskPath, Name = taskPath.Substring(taskPath.LastIndexOf('\\') + 1),
                Source = "task", Where = "Task Scheduler" + (taskPath.LastIndexOf('\\') > 0 ? " (" + taskPath.Substring(0, taskPath.LastIndexOf('\\')) + ")" : ""),
                Command = ("\"" + expanded + "\" " + args).Trim(), Runs = Runs(doc),
                Enabled = en == null || !string.Equals(en.InnerText.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            };
            e.File = StartupApps.ExeOf("\"" + expanded + "\"");
            e.Args = args;
            e.Missing = Win.IsWindows && (e.File.Length == 0 || !File.Exists(e.File));
            // Windows' own: in Task Scheduler's \Microsoft\ folder AND running a program from Windows' folder (unwanted
            // software likes to hide in \Microsoft\ too, with a program somewhere else: that one is shown and checked)
            e.WindowsOwn = taskPath.StartsWith("\\Microsoft\\", StringComparison.OrdinalIgnoreCase) && UnderWindows(e.File);
            e.ElixarOwn = IsElixar(e.File, e.Args);
            return e;
        }

        // Elixar's own: its exact program, or Windows' command prompt running Elixar's launcher exactly as Elixar sets it up.
        // (Only mentioning Elixar's folder somewhere in the command is not enough: any program could do that to hide.)
        static readonly string[] OwnArgs = { "--auto-clean", "--mail-daily", "--daily-tidy", "--keep-in-check", "--cure-alerts", "--policy-clean" };
        public static Func<string> ExeOverride;   // self-test
        public static bool IsElixar(string file, string args)
        {
            try
            {
                if (string.IsNullOrEmpty(file)) return false;
                string exe = ExeOverride != null ? ExeOverride() : (Win.IsWindows ? Install.Exe : null);
                if (exe == null) return false;
                string f = Path.GetFullPath(file);
                if (string.Equals(f, Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return true;
                if (ExeOverride == null && string.Equals(f, Path.GetFullPath(Install.CmdExe), StringComparison.OrdinalIgnoreCase))
                    return OwnArgs.Any(a => string.Equals((args ?? "").Trim(), Install.LauncherArgs(a), StringComparison.OrdinalIgnoreCase));
                return false;
            }
            catch { return false; }
        }

        static string Child(XmlNode n, string name)
        {
            foreach (XmlNode c in n.ChildNodes) if (c.LocalName == name) return c.InnerText ?? "";
            return "";
        }

        // when it runs, in a few words (its first trigger)
        static string Runs(XmlDocument doc)
        {
            var trig = doc.GetElementsByTagName("Triggers").Cast<XmlNode>().FirstOrDefault();
            if (trig == null) return Lang.Hebrew ? "כשמפעילים אותה" : "When started";
            foreach (XmlNode t in trig.ChildNodes)
            {
                if (t.NodeType != XmlNodeType.Element) continue;
                string interval = "";
                var rep = t.ChildNodes.Cast<XmlNode>().FirstOrDefault(x => x.LocalName == "Repetition");
                if (rep != null) interval = Child(rep, "Interval");
                string every = Every(interval);
                switch (t.LocalName)
                {
                    case "LogonTrigger": return Lang.Hebrew ? "בכניסה" : "At sign-in";
                    case "BootTrigger": return Lang.Hebrew ? "בהפעלת Windows" : "At Windows startup";
                    case "IdleTrigger": return Lang.Hebrew ? "כשהמחשב פנוי" : "When the PC is idle";
                    case "EventTrigger": return Lang.Hebrew ? "באירוע של Windows" : "On a Windows event";
                    case "SessionStateChangeTrigger": return Lang.Hebrew ? "בנעילה או שחרור" : "On lock or unlock";
                    case "RegistrationTrigger": return Lang.Hebrew ? "כשנוצרה" : "When created";
                    case "TimeTrigger": return every ?? (Lang.Hebrew ? "פעם אחת" : "Once");
                    case "CalendarTrigger":
                        if (every != null) return every;
                        if (t.ChildNodes.Cast<XmlNode>().Any(x => x.LocalName == "ScheduleByWeek")) return Lang.Hebrew ? "כל שבוע" : "Weekly";
                        if (t.ChildNodes.Cast<XmlNode>().Any(x => x.LocalName == "ScheduleByMonth" || x.LocalName == "ScheduleByMonthDayOfWeek")) return Lang.Hebrew ? "כל חודש" : "Monthly";
                        return Lang.Hebrew ? "כל יום" : "Daily";
                }
            }
            return Lang.Hebrew ? "כשמפעילים אותה" : "When started";
        }
        static string Every(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return null;
            try
            {
                var ts = XmlConvert.ToTimeSpan(iso);
                if (ts.TotalMinutes < 1) return null;
                if (ts.TotalHours < 1) return Lang.Hebrew ? "כל " + (int)ts.TotalMinutes + " דקות" : "Every " + (int)ts.TotalMinutes + " minutes";
                if (ts.TotalHours < 2) return Lang.Hebrew ? "כל שעה" : "Every hour";
                return Lang.Hebrew ? "כל " + (int)ts.TotalHours + " שעות" : "Every " + (int)ts.TotalHours + " hours";
            }
            catch { return null; }
        }

        // ---------- services that start automatically ----------
        static List<AutoEntry> Services()
        {
            var list = new List<AutoEntry>();
            using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            {
                if (k == null) return list;
                foreach (var name in k.GetSubKeyNames())
                {
                    try
                    {
                        using (var s = k.OpenSubKey(name))
                        {
                            if (s == null) continue;
                            int type = Convert.ToInt32(s.GetValue("Type", 0)), start = Convert.ToInt32(s.GetValue("Start", 4));
                            if ((type & 0x30) == 0 || start != 2) continue;   // programs (not drivers) that start automatically
                            string img = (s.GetValue("ImagePath", "") ?? "").ToString();
                            if (img.Length == 0) continue;
                            string disp = (s.GetValue("DisplayName", "") ?? "").ToString();
                            if (disp.StartsWith("@")) disp = name;   // a resource reference: Windows' own text
                            var e = new AutoEntry
                            {
                                Kind = "Service", Name = disp.Length > 0 ? disp : name, ItemPath = name, Source = "service", Where = "Services (" + name + ")",
                                Command = Environment.ExpandEnvironmentVariables(img), Runs = Lang.Hebrew ? "אוטומטי" : "Automatic"
                            };
                            e.File = StartupApps.ExeOf(e.Command.StartsWith("\\??\\") ? e.Command.Substring(4) : e.Command);
                            e.Missing = e.File.Length == 0 || !File.Exists(e.File);
                            e.ElixarOwn = IsElixar(e.File, null);
                            list.Add(e);
                        }
                    }
                    catch { }
                }
            }
            return list;
        }

        // ---------- turning a task off or on (Task Scheduler's own switch); never Windows' own ----------
        public static string SetEnabled(AutoEntry e, bool on)
        {
            if (e == null || e.Kind != "Task") return Lang.Hebrew ? "רק משימות אפשר לכבות כאן." : "Only tasks can be turned off here.";
            if (e.WindowsOwn || e.ElixarOwn) return Lang.Hebrew ? "משימות של Windows ושל Elixar לא משתנות כאן." : "Windows' and Elixar's own tasks are not changed here.";
            if (!Policy.Allowed("startup.change")) return Lang.Hebrew ? "מדיניות הארגון לא מאפשרת זאת." : "Your organization's policy doesn't allow this.";
            string tn = e.TaskPath ?? "";
            if (tn.Length < 2 || tn[0] != '\\' || tn.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0 || tn.Contains("\\..")) return "Bad task name.";
            int code = RunOverride != null ? RunOverride("schtasks.exe", "/Change /TN " + Sys.ArgQuote(tn) + (on ? " /ENABLE" : " /DISABLE")) : RunSchtasks(tn, on);
            using (ChangeLog.In("Startup"))
                ChangeLog.Add("Startup", "Autostart task: turned " + (on ? "on " : "off ") + e.Name, tn, on ? "off" : "on", on ? "on" : "off", code == 0 ? "" : "schtasks exit " + code, code != 0);
            if (code != 0) return (Lang.Hebrew ? "Windows לא שינה את המשימה (קוד " : "Windows didn't change the task (code ") + code + ").";
            e.Enabled = on;
            return null;
        }
        static int RunSchtasks(string tn, bool on)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Win.SystemExe("schtasks.exe"), "/Change /TN " + Sys.ArgQuote(tn) + (on ? " /ENABLE" : " /DISABLE"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        // for PC health: enabled entries apps added that look like unwanted software
        public static int Suspicious(List<AutoEntry> list)
        {
            return list.Count(e => e.Enabled && !e.WindowsOwn && !e.ElixarOwn && e.Flag == StartupFlag.Danger);
        }
    }
}
