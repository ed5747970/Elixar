// Who started Elixar with administrator rights. Any program, even one without administrator rights, can ask Windows to
// start Elixar as administrator with its own command-line switches: Windows then shows Elixar's usual prompt, and one
// "Yes" would let that program use Elixar's rights (change the policy, tweaks, clean an item, write a file anywhere).
// When the program that asked has no administrator rights itself, Elixar shows what it was asked to do and does it only
// if you say so. An administrator command prompt, Task Scheduler, Windows Installer and IT's tools (as SYSTEM) are not asked.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CDriveCleaner
{
    public static class ParentGuard
    {
        public static Func<string> ParentOverride;    // self-test: null = an elevated parent; otherwise the asking program's name
        public static Func<string, bool> AskOverride; // self-test

        [StructLayout(LayoutKind.Sequential)]
        struct PBI { public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId; }
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PBI info, int size, out int len);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
        const uint QueryLimited = 0x1000, TokenQuery = 0x0008, TokenDuplicate = 0x0002;

        // null when the program that started Elixar has administrator rights (or is Windows itself);
        // otherwise its name ("" if it can't be told)
        public static string NonAdminParent()
        {
            if (ParentOverride != null) return ParentOverride();
            if (!Win.IsWindows) return null;
            try
            {
                var me = Process.GetCurrentProcess();
                var pbi = new PBI(); int len;
                if (NtQueryInformationProcess(me.Handle, 0, ref pbi, Marshal.SizeOf(typeof(PBI)), out len) != 0) return "";
                int ppid = pbi.InheritedFromUniqueProcessId.ToInt32();
                IntPtr h = OpenProcess(QueryLimited, false, ppid);
                if (h == IntPtr.Zero) return "";   // gone, or not ours to look at: ask
                try
                {
                    // the number may have been reused by a newer program after the real parent closed: then it isn't the parent
                    long pc, x1, x2, x3, mc;
                    if (GetProcessTimes(h, out pc, out x1, out x2, out x3) && GetProcessTimes(me.Handle, out mc, out x1, out x2, out x3) && pc > mc) return "";
                    var sb = new StringBuilder(1024); int n = sb.Capacity;
                    string name = QueryFullProcessImageName(h, 0, sb, ref n) ? Path.GetFileName(sb.ToString()) : "";
                    // has it administrator rights in use now? (the same test Elixar uses for itself: with Windows' prompt on, a
                    // program you start normally has the Administrators group switched off; with the prompt off, it has it on)
                    IntPtr t;
                    if (!OpenProcessToken(h, TokenQuery | TokenDuplicate, out t)) return name;
                    try
                    {
                        using (var id = new System.Security.Principal.WindowsIdentity(t))
                            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) || id.IsSystem ? null : name;
                    }
                    finally { CloseHandle(t); }
                }
                finally { CloseHandle(h); }
            }
            catch { return ""; }
        }

        static bool RunsAsSystem() { try { using (var id = System.Security.Principal.WindowsIdentity.GetCurrent()) return id.IsSystem; } catch { return false; } }

        // true: go on. Asks you when a program without administrator rights started Elixar for this.
        public static bool Allow(string what)
        {
            if (!Win.IsWindows && ParentOverride == null) return true;
            if (ParentOverride == null && (!Sys.IsAdmin() || RunsAsSystem())) return true;   // nothing to borrow, or Windows/IT's tools
            string parent = NonAdminParent();
            if (parent == null) return true;
            if (AskOverride == null && !Environment.UserInteractive) return false;   // nobody could answer: not done
            string text = (Lang.Hebrew
                ? "תוכנה אחרת" + (parent.Length > 0 ? " (" + parent + ")" : "") + " הפעילה את Elixar עם הרשאות מנהל כדי:\n\n" + what +
                  "\n\nלתוכנה הזו עצמה אין הרשאות מנהל. אשרו רק אם אתם הפעלתם את זה."
                : "Another program" + (parent.Length > 0 ? " (" + parent + ")" : "") + " started Elixar with administrator rights to:\n\n" + what +
                  "\n\nThat program has no administrator rights itself. Only allow this if you started it yourself.") + "\n\n" + (Lang.Hebrew ? "לאשר?" : "Allow it?");
            bool ok = AskOverride != null ? AskOverride(text) : Lang.BoxNo(null, text, "Elixar") == System.Windows.Forms.DialogResult.Yes;
            try { ChangeLog.Add("Elixar", ok ? "Allowed a command another program started" : "Refused a command another program started", parent, what, ok ? "allowed" : "refused", "", !ok); } catch { }
            return ok;
        }
    }
}
