// Startup programs: what starts by itself when you sign in, who signed it, and switches that work like Task Manager's.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public enum StartupFlag { Ok, Review, Danger, Leftover }

    public class StartupEntry
    {
        public string Name = "", Command = "", File = "", Source = "", Where = "", ItemPath = "";
        public bool Enabled = true, Missing;
        public string SigStatus = "", Signer = "";
        public StartupFlag Flag = StartupFlag.Ok;
        public string Why = "";
    }

    public static class StartupApps
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        // source -> hive, view, key, approved hive, approved key, label
        public class Src { public string Id, Key, ApprovedKey, Label; public RegistryHive Hive, ApprovedHive; public RegistryView View; public bool Folder; }

        public static readonly Src[] Sources =
        {
            new Src { Id = "hkcu-run", Hive = RegistryHive.CurrentUser, View = RegistryView.Default, Key = RunKey, ApprovedHive = RegistryHive.CurrentUser, ApprovedKey = Approved + "Run", Label = "Registry, your account" },
            new Src { Id = "hklm-run", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64, Key = RunKey, ApprovedHive = RegistryHive.LocalMachine, ApprovedKey = Approved + "Run", Label = "Registry, all users" },
            new Src { Id = "hklm-run32", Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32, Key = RunKey, ApprovedHive = RegistryHive.LocalMachine, ApprovedKey = Approved + "Run32", Label = "Registry, all users (32-bit)" },
            new Src { Id = "user-folder", Folder = true, ApprovedHive = RegistryHive.CurrentUser, ApprovedKey = Approved + "StartupFolder", Label = "Startup folder, your account" },
            new Src { Id = "common-folder", Folder = true, ApprovedHive = RegistryHive.LocalMachine, ApprovedKey = Approved + "StartupFolder", Label = "Startup folder, all users" },
        };

        public static Src SourceOf(string id) { return Sources.First(s => s.Id == id); }

        static string FolderOf(Src s)
        {
            return Environment.GetFolderPath(s.Id == "user-folder" ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);
        }

        // Task Manager's on/off switch: first byte even = on, odd = off; no value = on
        public static bool IsOn(byte[] v) { return v == null || v.Length == 0 || (v[0] & 1) == 0; }

        public static byte[] SwitchValue(bool on)
        {
            var b = new byte[12];
            b[0] = (byte)(on ? 2 : 3);
            if (!on) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(b, 4);
            return b;
        }

        // the program a command line starts
        public static string ExeOf(string command)
        {
            string c = Environment.ExpandEnvironmentVariables((command ?? "").Trim());
            if (c.Length == 0) return "";
            if (c[0] == '"')
            {
                int q = c.IndexOf('"', 1);
                return q > 1 ? c.Substring(1, q - 1) : c.Trim('"');
            }
            // unquoted: the longest prefix (up to a space) that is an existing file
            var parts = c.Split(' ');
            for (int n = parts.Length; n >= 1; n--)
            {
                string cand = string.Join(" ", parts.Take(n));
                if (System.IO.File.Exists(cand)) return cand;
                if (!cand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(cand + ".exe")) return cand + ".exe";
            }
            string first = parts[0];
            if (!Path.IsPathRooted(first))
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                foreach (var cand in new[] { first, first + ".exe" })
                    if (!string.IsNullOrEmpty(sys) && System.IO.File.Exists(Path.Combine(sys, cand))) return Path.Combine(sys, cand);
            }
            return first;
        }

        static readonly string[] ScriptHosts = { "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "cmd.exe", "rundll32.exe", "regsvr32.exe" };

        // decides how much attention an entry needs
        public static void Judge(StartupEntry e)
        {
            string f = (e.File ?? "").ToLowerInvariant().Replace('/', '\\');
            string cmd = (e.Command ?? "").ToLowerInvariant();
            string exe = f.Substring(f.LastIndexOf('\\') + 1);
            if (e.Missing) { e.Flag = StartupFlag.Leftover; e.Why = "the program is gone; this entry does nothing and can be removed"; return; }
            if (ScriptHosts.Contains(exe))
            {
                bool hidden = cmd.Contains(" -enc") || cmd.Contains("encodedcommand") || cmd.Contains("http://") || cmd.Contains("https://") ||
                              cmd.Contains("-w hidden") || cmd.Contains("windowstyle hidden") || cmd.Contains("frombase64");
                e.Flag = hidden ? StartupFlag.Danger : StartupFlag.Review;
                e.Why = hidden ? "runs a hidden script through " + exe + " - typical of unwanted software" : "starts a script through " + exe + "; check what it runs";
                return;
            }
            if (f.Contains(@"\appdata\local\temp\") || f.Contains(@"\windows\temp\") || f.Contains(@"\downloads\") || f.Contains(@"\users\public\") || f.Contains(@"\$recycle.bin\"))
            {
                e.Flag = StartupFlag.Danger; e.Why = "starts from a temp or download folder - unusual for real programs"; return;
            }
            switch (e.SigStatus)
            {
                case "Valid": e.Flag = StartupFlag.Ok; e.Why = "signed by " + (e.Signer.Length > 0 ? e.Signer : "a known publisher"); return;
                case "HashMismatch": case "NotTrusted": case "Incompatible":
                    e.Flag = StartupFlag.Danger; e.Why = "its signature is broken - the file may have been changed"; return;
                case "NotSigned": e.Flag = StartupFlag.Review; e.Why = "not signed - make sure you know this program"; return;
                default: e.Flag = StartupFlag.Review; e.Why = "signature could not be checked"; return;
            }
        }

        static byte[] ReadSwitch(Src s, string name)
        {
            try
            {
                using (var bk = RegistryKey.OpenBaseKey(s.ApprovedHive, RegistryView.Registry64))
                using (var k = bk.OpenSubKey(s.ApprovedKey))
                    return k == null ? null : k.GetValue(name) as byte[];
            }
            catch { return null; }
        }

        public static List<StartupEntry> Read(bool checkSignatures)
        {
            var list = new List<StartupEntry>();
            if (!Win.IsWindows) return list;
            foreach (var s in Sources)
            {
                try
                {
                    if (s.Folder)
                    {
                        string dir = FolderOf(s);
                        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                        foreach (var file in Directory.GetFiles(dir))
                        {
                            string n = Path.GetFileName(file);
                            if (n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                            var e = new StartupEntry { Name = Path.GetFileNameWithoutExtension(file), Source = s.Id, Where = s.Label, ItemPath = file, Enabled = IsOn(ReadSwitch(s, n)) };
                            if (n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                var t = Shortcut(file);
                                e.File = t[0]; e.Command = ("\"" + t[0] + "\" " + t[1]).Trim();
                            }
                            else { e.File = file; e.Command = file; }
                            e.Missing = e.File.Length == 0 || !System.IO.File.Exists(e.File);
                            list.Add(e);
                        }
                    }
                    else
                    {
                        using (var bk = RegistryKey.OpenBaseKey(s.Hive, s.View))
                        using (var k = bk.OpenSubKey(s.Key))
                        {
                            if (k == null) continue;
                            foreach (var name in k.GetValueNames())
                            {
                                if (name.Length == 0) continue;
                                string cmd = (k.GetValue(name) ?? "").ToString();
                                var e = new StartupEntry { Name = name, Command = cmd, Source = s.Id, Where = s.Label, ItemPath = name, File = ExeOf(cmd), Enabled = IsOn(ReadSwitch(s, name)) };
                                e.Missing = e.File.Length == 0 || !System.IO.File.Exists(e.File);
                                list.Add(e);
                            }
                        }
                    }
                }
                catch { }
            }
            if (checkSignatures)
            {
                var sig = Trust.CheckMany(list.Where(e => !e.Missing).Select(e => e.File));
                foreach (var e in list)
                {
                    string[] r;
                    if (sig.TryGetValue(e.File, out r)) { e.SigStatus = r[0]; e.Signer = r[1]; }
                }
            }
            foreach (var e in list) Judge(e);
            return list;
        }

        // .lnk target and arguments (Windows Script Host)
        static string[] Shortcut(string lnk)
        {
            return IPhone.Sta(() =>
            {
                try
                {
                    var sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    var sc = sh.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                    string target = (sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null) ?? "").ToString();
                    string args = (sc.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, sc, null) ?? "").ToString();
                    return new[] { target, args };
                }
                catch { return new[] { "", "" }; }
            });
        }

        public static bool SetEnabled(StartupEntry e, bool on)
        {
            try
            {
                var s = SourceOf(e.Source);
                string name = s.Folder ? Path.GetFileName(e.ItemPath) : e.ItemPath;
                using (var bk = RegistryKey.OpenBaseKey(s.ApprovedHive, RegistryView.Registry64))
                using (var k = bk.CreateSubKey(s.ApprovedKey))
                    k.SetValue(name, SwitchValue(on), RegistryValueKind.Binary);
                e.Enabled = on;
                return true;
            }
            catch { return false; }
        }

        // ---- removing leftovers, with Undo ----
        public static string Encode(StartupEntry e)
        {
            return "reg:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Source + "\t" + e.ItemPath + "\t" + e.Command));
        }

        public static bool Decode(string s, out string source, out string name, out string command)
        {
            source = name = command = null;
            try
            {
                if (!s.StartsWith("reg:")) return false;
                var p = Encoding.UTF8.GetString(Convert.FromBase64String(s.Substring(4))).Split('\t');
                if (p.Length != 3 || !Sources.Any(x => x.Id == p[0] && !x.Folder)) return false;
                source = p[0]; name = p[1]; command = p[2];
                return name.Length > 0;
            }
            catch { return false; }
        }

        public static int RemoveLeftovers(List<StartupEntry> entries, Action<string> log)
        {
            var regs = entries.Where(e => e.Missing && !SourceOf(e.Source).Folder).ToList();
            var files = entries.Where(e => e.Missing && SourceOf(e.Source).Folder).Select(e => e.ItemPath).ToList();
            int n = 0;
            if (regs.Count > 0)
            {
                var b = Quarantine.Begin("Startup entries (" + regs.Count + ")", "startup", 0);
                foreach (var e in regs)
                {
                    try
                    {
                        var s = SourceOf(e.Source);
                        using (var bk = RegistryKey.OpenBaseKey(s.Hive, s.View))
                        using (var k = bk.OpenSubKey(s.Key, true))
                        {
                            if (k == null) continue;
                            b.Items.Add(new[] { Encode(e), s.Label + ": " + e.Name, "0" });
                            k.DeleteValue(e.ItemPath, false);
                            n++;
                            if (log != null) log("  Removed startup entry " + e.Name + " (" + e.Command + ")");
                        }
                    }
                    catch (Exception ex) { if (log != null) log("  Could not remove " + e.Name + ": " + ex.Message); }
                }
                Quarantine.Commit(b);
            }
            if (files.Count > 0) { Quarantine.MoveToUndo("Startup shortcuts (" + files.Count + ")", files, log); n += files.Count(f => !System.IO.File.Exists(f)); }
            return n;
        }

        public static bool RestoreEntry(string encoded, Action<string> log)
        {
            string src, name, cmd;
            if (!Decode(encoded, out src, out name, out cmd)) return false;
            var s = SourceOf(src);
            try
            {
                using (var bk = RegistryKey.OpenBaseKey(s.Hive, s.View))
                using (var k = bk.CreateSubKey(s.Key))
                {
                    if (k.GetValue(name) != null) return true;
                    k.SetValue(name, cmd, RegistryValueKind.String);
                }
                if (log != null) log("  Restored startup entry " + name);
                return true;
            }
            catch (Exception e) { if (log != null) log("  Could not restore " + name + ": " + e.Message); return false; }
        }

        // ---- Microsoft Defender ----
        public static string DefenderExe
        {
            get
            {
                return Path.Combine(Path.Combine(Win.ProgramFiles, "Windows Defender"), "MpCmdRun.exe");
            }
        }

        // 0 = nothing found, 2 = threat found (Defender deals with it), other = could not scan
        public static int DefenderScan(string file, out string output)
        {
            output = "";
            if (!System.IO.File.Exists(DefenderExe)) { output = "Microsoft Defender is not available on this PC."; return -1; }
            var psi = new ProcessStartInfo(DefenderExe, "-Scan -ScanType 3 -File \"" + file.Replace("\"", "") + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(10 * 60 * 1000)) { try { p.Kill(); } catch { } output = "timed out"; return -2; }
                output = o.Result;
                return p.ExitCode;
            }
        }
    }
}
