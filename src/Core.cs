// Elixar core: cleaning items, file helpers and system helpers.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public enum Tier { Safe, Review, Caution }

    public class CleanItem
    {
        public string Name;
        public Tier Tier;
        public bool DefaultOn;
        public string Where;
        public string Info;
        public string[] Procs = new string[0];
        public Func<long> Measure;          // -1 = size unknown until it runs
        public Action<Action<string>> Clean; // gets a log callback
        public long Size;
        public bool SizeUnknown;
        public bool Privacy;               // traces of what you did, not disk space
        public string RootScript;          // Linux: fixed commands run as root after one password prompt (null: runs as you)
        public string Drive;               // "D:" for items on another drive (null: C: and the system)
    }

    // ---------------- file helpers ----------------
    public static class Fs
    {
        public static long SizeOf(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (!Directory.Exists(path)) return 0;
                // a junction or symlink points somewhere else: do not count what it points to
                if ((new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0) return 0;
            }
            catch { return 0; }

            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var f in di.EnumerateFiles())
                    {
                        try { if (!Lx.On || (f.Attributes & FileAttributes.ReparsePoint) == 0) total += f.Length; } catch { }
                    }
                    foreach (var d in di.EnumerateDirectories())
                    {
                        // skip junctions and symlinks so nothing is counted twice; on Linux also /proc, /sys and other disks
                        if ((d.Attributes & FileAttributes.ReparsePoint) == 0 && !Lx.SkipDir(d.FullName, path)) stack.Push(d.FullName);
                    }
                }
                catch { }
            }
            return total;
        }

        public static string[] Children(string dir, string pattern)
        {
            try
            {
                if (!Directory.Exists(dir)) return new string[0];
                return Directory.GetFileSystemEntries(dir, pattern);
            }
            catch { return new string[0]; }
        }

        public static long MeasureIn(string dir, string pattern = "*")
        {
            long t = 0;
            foreach (var c in Children(dir, pattern)) t += SizeOf(c);
            return t;
        }

        // Deletes everything inside dir that matches pattern. The folder itself stays.
        // Files that are in use are skipped.
        public static void ClearIn(string dir, string pattern = "*")
        {
            foreach (var c in Children(dir, pattern)) Delete(c);
        }

        // Deletes a file or a whole folder. Junctions/symlinks are removed as links, never followed.
        public static void Delete(string path)
        {
            if (Win.IsWindows) { try { SafeDelete.Delete(path); } catch { } return; }
            DeleteLinux(path, new HashSet<string>(Lx.Mounts().Select(m => m.Point)));
        }

        // Linux: a link is removed as a link (its target is never touched, and its permissions are never changed),
        // and a folder where another disk, a network share or a cloud drive is mounted is left alone completely.
        static void DeleteLinux(string path, HashSet<string> mountPoints)
        {
            try
            {
                if (mountPoints.Contains(path.TrimEnd('/'))) return;
                if (File.Exists(path)) { File.Delete(path); return; }
                if (!Directory.Exists(path)) return;
                var di = new DirectoryInfo(path);
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path, false); return; }
                foreach (var c in Directory.GetFileSystemEntries(path)) DeleteLinux(c, mountPoints);
                Directory.Delete(path, false);
            }
            catch { }
        }

        public static List<string> ProfileCaches(string userDataRoot)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(userDataRoot)) return list;
                foreach (var p in Directory.GetDirectories(userDataRoot))
                {
                    string n = Path.GetFileName(p);
                    if (n == "Default" || n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(Path.Combine(p, "Cache"));
                        list.Add(Path.Combine(p, "Code Cache"));
                        list.Add(Path.Combine(p, "GPUCache"));
                    }
                }
            }
            catch { }
            return list;
        }

        public static string Fmt(long b)
        {
            if (b < 0) return "varies";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("N1") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("N0") + " MB";
            if (b > 0) return Math.Max(1, b / 1024).ToString("N0") + " KB";
            return "0";
        }
    }

    // ---------------- system helpers ----------------
    public static class Sys
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static void TryDpiAware() { try { SetProcessDPIAware(); } catch { } }

        public static void EmptyRecycleBin()
        {
            // no confirmation, no progress window, no sound
            SHEmptyRecycleBin(IntPtr.Zero, "C:\\", 0x7);
        }

        public static long Free() { try { return new DriveInfo(Win.IsWindows ? "C" : "/").AvailableFreeSpace; } catch { return 0; } }
        public static long Total() { try { return new DriveInfo(Win.IsWindows ? "C" : "/").TotalSize; } catch { return 0; } }
        public static string Root { get { return Win.IsWindows ? @"C:\" : "/"; } }
        public static string Main { get { return Win.IsWindows ? "C:" : "/"; } }      // short label

        public static bool IsAdmin()
        {
            if (!Win.IsWindows) return Lx.IsRoot;
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string Run(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(Win.SystemExe(exe), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    ChangeLog.Command(exe, args, "timed out", -1);
                    return "timed out";
                }
                string r = (o.Result + " " + e.Result).Trim();
                int code = 0; try { code = p.ExitCode; } catch { }
                ChangeLog.Command(exe, args, r, code);
                return r;
            }
        }

        // a PowerShell string literal: PowerShell also ends a '...' string at the curly quotes, so those are doubled too
        public static string PsQuote(string v)
        {
            var sb = new StringBuilder("'");
            foreach (char c in v ?? "") { if (c == '\'' || c == '\u2018' || c == '\u2019' || c == '\u201A' || c == '\u201B') sb.Append(c); sb.Append(c); }
            return sb.Append('\'').ToString();
        }

        // TLS 1.2 (and 1.3 where Windows offers it) only. Adding 1.2 to the old default would still allow SSL 3 and TLS 1.0.
        public static void SecureTls()
        {
            var keep13 = System.Net.ServicePointManager.SecurityProtocol & (System.Net.SecurityProtocolType)12288;
            System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072 | keep13;
        }

        // PowerShell looks for the commands it loads (Remove-AppxPackage, Checkpoint-Computer...) in the folders listed in
        // PSModulePath, starting with Documents\WindowsPowerShell\Modules, which any program you run can write to.
        // As administrator, a planted module there would run instead of Windows' own. So only Windows' modules are used.
        public static string PsSafe(string script)
        {
            return "$env:PSModulePath = Join-Path $PSHOME 'Modules'; " + script;
        }

        public static string RunPowerShell(string script, int timeoutMs)
        {
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(PsSafe(script)));
            string r = Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, timeoutMs);
            ChangeLog.PowerShell(script, r);
            return r;
        }

        // Stops the services that are running; returns their names so they can be started again.
        public static List<string> StopServices(params string[] names)
        {
            var stopped = new List<string>();
            foreach (var n in names)
            {
                try
                {
                    using (var sc = new ServiceController(n))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                            stopped.Add(n);
                        }
                    }
                }
                catch { }
            }
            return stopped;
        }

        public static void StartServices(IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                try { using (var sc = new ServiceController(n)) sc.Start(); } catch { }
            }
        }

        public static List<Process> FindProcesses(string[] patterns)
        {
            var found = new List<Process>();
            foreach (var p in Process.GetProcesses())
            {
                string n;
                try { n = p.ProcessName; } catch { continue; }
                foreach (var pat in patterns)
                {
                    bool hit = pat.EndsWith("*")
                        ? n.StartsWith(pat.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                        : n.Equals(pat, StringComparison.OrdinalIgnoreCase);
                    if (hit) { found.Add(p); break; }
                }
            }
            return found;
        }
    }

    // ---------------- what can be cleaned ----------------
}
