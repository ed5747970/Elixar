// Install Elixar on this PC (Start menu, Windows' installed apps list, a protected copy) and remove it again.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class Setup
    {
        // waits a moment for Elixar to close, then removes its folder; /d skips cmd's AutoRun commands
        public static string RemoveFolderArgs(string ping, string dir) { return "/d /c \"\"" + ping + "\" -n 4 127.0.0.1 >nul & rmdir /s /q \"" + dir + "\"\""; }

        public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Elixar";

        public static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Elixar.lnk"); }
        }

        public static bool IsRegistered()
        {
            try { using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) using (var k = hklm.OpenSubKey(UninstallKey)) return k != null; }
            catch { return false; }
        }

        // the values Windows' "Installed apps" list shows
        public static System.Collections.Generic.Dictionary<string, object> Entry(string exe, string version, long bytes)
        {
            return new System.Collections.Generic.Dictionary<string, object>
            {
                { "DisplayName", "Elixar" }, { "DisplayVersion", version }, { "Publisher", "Elisar" },
                { "DisplayIcon", "\"" + exe + "\",0" }, { "InstallLocation", Path.GetDirectoryName(exe) },
                { "UninstallString", "\"" + exe + "\" --uninstall" }, { "QuietUninstallString", "\"" + exe + "\" --uninstall --quiet" },
                { "EstimatedSize", (int)Math.Max(1, bytes / 1024) }, { "NoModify", 1 }, { "NoRepair", 1 },
                { "InstallDate", DateTime.Now.ToString("yyyyMMdd") }, { "URLInfoAbout", Updates.ReleasesPage },
            };
        }

        public static string InstallHere(Action<string> log)
        {
            string err = Install.Ensure();
            if (err != null) return err;
            try
            {
                var v = Updates.Current;
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.CreateSubKey(UninstallKey))
                    foreach (var kv in Entry(Install.Exe, v.Major + "." + v.Minor + "." + v.Build, new FileInfo(Install.Exe).Length))
                        RegLog.Set(k, kv.Key, kv.Value, kv.Value is int ? RegistryValueKind.DWord : RegistryValueKind.String, "Elixar installed: its entry in Windows' installed apps list");
                IPhone.Sta(() =>
                {
                    var sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    var sc = sh.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { StartMenuLink });
                    Action<string, object> set = (n, val) => sc.GetType().InvokeMember(n, System.Reflection.BindingFlags.SetProperty, null, sc, new[] { val });
                    set("TargetPath", Install.Exe); set("WorkingDirectory", Install.Dir); set("Description", "Elixar - the cure for a cluttered PC"); set("IconLocation", Install.Exe + ",0");
                    sc.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                    return 0;
                });
                if (log != null) log("Installed Elixar in " + Install.Dir + " (Start menu and Windows' installed apps list).");
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        // removes the schedules, Start menu entry, apps-list entry and the Program Files copy; your Undo backups stay until they expire
        public static int Uninstall(bool quiet)
        {
            if (!quiet && Lang.Box(null, "Remove Elixar from this PC?\n\nThe schedules, the Start menu entry and the copy in Program Files are removed. Undo backups are kept until their 7 days are up.",
                    "Elixar", System.Windows.Forms.MessageBoxButtons.OKCancel, System.Windows.Forms.MessageBoxIcon.Question) != System.Windows.Forms.DialogResult.OK) return 1;
            string o;
            foreach (var t in new[] { Automation.CheckTask, Automation.LogonTask, Automation.CleanTask, Automation.MailTask, Automation.TidyTask }) Automation.RunSchtasks(Automation.DeleteArgs(t), out o);
            try { GoogleAuth.SignOut(); } catch { }   // the Google sign-in is removed from this PC and cancelled at Google
            try { MicrosoftAuth.SignOut(); OtherMail.Forget(); MailAccount.Disconnect(); } catch { }
            try { File.Delete(StartMenuLink); } catch { }
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    int vals = 0; using (var k = hklm.OpenSubKey(UninstallKey)) if (k != null) vals = k.ValueCount;
                    hklm.DeleteSubKeyTree(UninstallKey, false);
                    if (vals > 0) RegLog.ClearedKey(hklm, UninstallKey, vals, 0, "Elixar removed from this PC: its entry in Windows' installed apps list");
                }
            }
            catch { }
            // the running file can't delete itself: Windows removes the folder a moment after Elixar closes
            try
            {
                // full paths and System32 as the working folder: a "ping" in the current folder would otherwise run as administrator;
                // /d: cmd skips the AutoRun commands any program can set for your account
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string cmd = Path.Combine(sys, "cmd.exe"), ping = Path.Combine(sys, "PING.EXE");
                Process.Start(new ProcessStartInfo(cmd, RemoveFolderArgs(ping, Install.Dir)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sys });
            }
            catch { }
            if (!quiet) Lang.Box(null, "Elixar was removed.", "Elixar");
            return 0;
        }
    }
}
