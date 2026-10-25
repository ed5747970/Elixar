// Every change Elixar writes in Logs also goes to Windows' Event Log, so a company's security tools (SIEM, Windows Event
// Forwarding, EDR) can collect it: Event Viewer > Applications and Services Logs > Elixar.
// The log can be read only by SYSTEM, administrators and the "Event Log Readers" group (the change log names files and settings).
// Policy manager > "Write changes to the Windows Event Log" turns it off; uninstall --purge removes the log.

using System;
using System.Diagnostics;
using System.Text;

namespace CDriveCleaner
{
    public static class WinEvents
    {
        public const string LogName = "Elixar", Source = "Elixar";
        // read: SYSTEM, Administrators, Event Log Readers (S-1-5-32-573); write and clear: SYSTEM and Administrators
        public const string Access = "O:BAG:SYD:(A;;0xf0007;;;SY)(A;;0x7;;;BA)(A;;0x1;;;S-1-5-32-573)";
        public static Action<int, EventLogEntryType, string> WriteOverride;   // self-test
        static bool ready, broken;
        [ThreadStatic] static bool busy;   // this thread is already writing (making the log writes a change of its own)
        // 20 MB, the oldest events make room for new ones (Windows' default for a new log keeps only 512 KB)
        public const string Size = "/ms:20971520 /rt:false /ab:false";
        static readonly object gate = new object();

        // the event id says what kind of change it was
        public static int Id(string kind)
        {
            switch (kind ?? "")
            {
                case "Cleaning": case "Files": return 1000;
                case "Undo": return 1001;
                case "Registry": case "Settings": return 1002;
                case "Apps": case "Startup": return 1003;
                case "Security": return 1004;
                case "Email": return 1005;
                case "Windows command": return 1006;
                default: return 1099;
            }
        }

        public static string Text(ChangeLog.Entry e)
        {
            var sb = new StringBuilder();
            sb.Append("Elixar ").Append(e.Failed ? "could not make a change" : "made a change").Append("\r\n\r\n");
            Action<string, string> line = (n, v) => { if (!string.IsNullOrEmpty(v)) sb.Append(n).Append(": ").Append(v).Append("\r\n"); };
            line("Action", e.Action); line("Target", e.Target); line("Before", e.Before); line("After", e.After);
            line("Kind", e.Kind); line("Part of Elixar", e.Section); line("Detail", e.Detail);
            line("Windows user", Environment.UserDomainName + "\\" + Environment.UserName);
            line("Elixar", Updates.Current.ToString()); line("Change id", e.Id);
            string s = sb.ToString().Replace("%%", "% %");   // Event Viewer would read %%n as a message number
            return s.Length > 30000 ? s.Substring(0, 30000) : s;   // an event holds up to 32K characters
        }

        public static void Write(ChangeLog.Entry e)
        {
            if (e == null || busy) return;
            if (WriteOverride == null && (ChangeLog.OverrideFile != null || !Win.IsWindows || broken)) return;   // a test log is never sent to the real Event Log
            if (Policy.Get("eventlog") == "off") return;
            var type = e.Failed ? EventLogEntryType.Warning : EventLogEntryType.Information;
            if (WriteOverride != null) { WriteOverride(Id(e.Kind), type, Text(e)); return; }
            if (!WriteNow(e)) broken = true;   // not an administrator, or the Event Log service is off: Logs still has it
        }

        // really writes it (the self-test calls this on GitHub's Windows test PC)
        public static bool WriteNow(ChangeLog.Entry e)
        {
            lock (gate)
            {
                busy = true;
                try { Ensure(); EventLog.WriteEntry(Source, Text(e), e.Failed ? EventLogEntryType.Warning : EventLogEntryType.Information, Id(e.Kind)); return true; }
                catch { return false; }
                finally { busy = false; }
            }
        }

        // the first time: make the log and its source, and let only administrators and Event Log Readers read it
        static void Ensure()
        {
            if (ready) return;
            using (ChangeLog.Quiet())
            {
                // the source must write to Elixar's own log, never to a log everyone can read (Application)
                if (EventLog.SourceExists(Source) && !string.Equals(EventLog.LogNameFromSourceName(Source, "."), LogName, StringComparison.OrdinalIgnoreCase))
                    EventLog.DeleteEventSource(Source);
                if (!EventLog.SourceExists(Source)) EventLog.CreateEventSource(new EventSourceCreationData(Source, LogName));
                Sys.Run("wevtutil.exe", "sl " + LogName + " /ca:" + Access + " " + Size, 30000);
                if (!Sys.Run("wevtutil.exe", "gl " + LogName, 30000).Contains("S-1-5-32-573")) throw new InvalidOperationException("the Elixar log's access could not be set");
            }
            ready = true;
        }

        public static bool Exists() { try { return Win.IsWindows && EventLog.Exists(LogName); } catch { return false; } }

        // uninstall --purge
        public static void Remove()
        {
            if (!Win.IsWindows) return;
            ready = false;
            try { if (EventLog.SourceExists(Source)) EventLog.DeleteEventSource(Source); } catch { }
            try { if (EventLog.Exists(LogName)) EventLog.Delete(LogName); } catch { }
        }
    }
}
