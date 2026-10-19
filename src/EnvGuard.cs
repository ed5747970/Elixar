// Start-up guards against code being slipped into Elixar while it runs as administrator.
//
// 1. Files next to Elixar.exe. Windows and .NET obey three kinds of files placed next to a program: "Elixar.exe.config"
//    (can make .NET load an extra DLL of someone's choosing), "Elixar.exe.local" (can make Windows load DLLs from that
//    folder) and "Elixar.exe.manifest". Elixar uses none of them. In a folder like Downloads any program can put them
//    there, so Elixar refuses to start while one is present.
// 2. Your account's environment variables. Any program you run can set them (HKCU\Environment) without administrator
//    rights. A few make every .NET program load a DLL (a "profiler") or another copy of .NET; others (ProgramFiles, Path,
//    ComSpec) change where programs are looked for. Before asking for administrator rights Elixar checks for the first
//    kind and offers to remove them. Once it runs as administrator it puts back Windows' own values for the second kind,
//    so the programs it starts (uninstallers, PowerShell, Windows tools) can't be pointed somewhere else.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class EnvGuard
    {
        // .NET's profiler and runtime switches
        public static bool Risky(string name)
        {
            string n = (name ?? "").Trim().ToUpperInvariant();
            if (n == "PSMODULEPATH") return false;   // put back to Windows' own value instead
            return n.StartsWith("COR_") || n.StartsWith("COMPLUS_") || n.StartsWith("APPDOMAIN_MANAGER_") || Install.Cleared.Contains(n);
        }

        public static readonly string[] PlantedSuffixes = { ".config", ".local", ".manifest" };

        // files or folders next to the program that Windows or .NET would obey
        public static List<string> PlantedFiles(string exePath)
        {
            var found = new List<string>();
            if (string.IsNullOrEmpty(exePath)) return found;
            foreach (var s in PlantedSuffixes)
            {
                string p = exePath + s;
                try { if (File.Exists(p) || Directory.Exists(p)) found.Add(p); } catch { }
            }
            return found;
        }

        public static Func<List<string>> AccountOverride;   // self-test

        // risky names in your account's own variables (both places Windows reads them from)
        public static List<string> InAccount()
        {
            if (AccountOverride != null) return AccountOverride();
            var found = new List<string>();
            if (!Win.IsWindows) return found;
            foreach (var key in new[] { "Environment", "Volatile Environment" })
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(key))
                        if (k != null) found.AddRange(k.GetValueNames().Where(Risky).Select(n => key + "\\" + n));
                }
                catch { }
            return found;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wp, string lp, uint flags, uint timeout, out IntPtr result);

        // removes them from your account (no administrator rights needed: it's your own setting)
        public static int RemoveFromAccount(IEnumerable<string> found)
        {
            int n = 0;
            foreach (var f in found)
            {
                int i = f.LastIndexOf('\\');
                if (i <= 0) continue;
                try { using (var k = Registry.CurrentUser.OpenSubKey(f.Substring(0, i), true)) if (k != null) { k.DeleteValue(f.Substring(i + 1), false); n++; } } catch { }
            }
            // tell Windows (Explorer) so programs started from now on don't get them either
            try { IntPtr r; SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "Environment", 0x0002, 3000, out r); } catch { }
            return n;
        }

        static void Set(string name, string value) { if (!string.IsNullOrEmpty(value)) Environment.SetEnvironmentVariable(name, value); }

        static string Hklm(string key, string value, bool expand)
        {
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default))
                using (var k = hk.OpenSubKey(key))
                {
                    string v = k == null ? null : k.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    return v == null ? null : expand ? Environment.ExpandEnvironmentVariables(v) : v;
                }
            }
            catch { return null; }
        }

        // for a process running as administrator: drop the .NET switches and put back Windows' own values; returns what was dropped
        public static List<string> Sanitize()
        {
            var dropped = new List<string>();
            if (!Win.IsWindows) return dropped;
            try
            {
                foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                {
                    string n = e.Key as string;
                    if (Risky(n)) { Environment.SetEnvironmentVariable(n, null); dropped.Add(n); }
                }
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                Set("SystemRoot", win); Set("windir", win);
                if (!string.IsNullOrEmpty(sys)) Set("ComSpec", Path.Combine(sys, "cmd.exe"));
                const string cv = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
                bool os64 = Environment.Is64BitOperatingSystem, p64 = Environment.Is64BitProcess;
                Set("ProgramFiles", p64 || !os64 ? Win.ProgramFiles : Win.ProgramFilesX86);
                Set("CommonProgramFiles", Hklm(cv, p64 || !os64 ? (os64 ? "CommonW6432Dir" : "CommonFilesDir") : "CommonFilesDir (x86)", false));
                if (os64)
                {
                    Set("ProgramW6432", Win.ProgramFiles); Set("ProgramFiles(x86)", Win.ProgramFilesX86);
                    Set("CommonProgramW6432", Hklm(cv, "CommonW6432Dir", false)); Set("CommonProgramFiles(x86)", Hklm(cv, "CommonFilesDir (x86)", false));
                }
                string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                Set("ProgramData", pd); Set("ALLUSERSPROFILE", pd);
                // the machine's own Path, PATHEXT and PowerShell module folders (additions made for your account are left out)
                const string se = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
                Set("Path", Hklm(se, "Path", true));
                Set("PATHEXT", Hklm(se, "PATHEXT", true));
                Set("PSModulePath", Hklm(se, "PSModulePath", true));
            }
            catch { }
            return dropped;
        }
    }
}
