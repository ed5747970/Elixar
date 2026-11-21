// Self-test for 1.2.70: what an attacker could make Elixar (running as administrator) do to Windows, found in a review
// "as an attacker": the backup going into Windows through a tricky path, shredding a file that has a second name,
// and a Windows part dressed up as a broken app entry in the right-click menu.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class SelfTest1270
    {
        static void Run(Action<bool, string> check, string name, Func<string> test)
        {
            string why;
            try { why = test(); }
            catch (Exception ex)
            {
                var at = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Contains("CDriveCleaner.")).Take(4);
                why = "ERROR " + ex.GetType().Name + ": " + ex.Message + " @ " + string.Join(" < ", at);
            }
            check(why == null, name + (why == null ? "" : "  (" + why + ")"));
        }

        const string TestKey = @"Software\ElixarSelfTest1270";

        public static void All(string root, Action<bool, string> check)
        {
            string R = Path.Combine(root, "t1270"); Directory.CreateDirectory(R);

            Run(check, "Backup: a destination written so it leads into Windows (\\\\?\\, \\\\.\\, a network path back to this PC, a drive letter that is really a folder on C:, a file stream) is refused", () =>
            {
                string usb = Path.Combine(R, "usb"); Directory.CreateDirectory(usb);
                var none = new string[0];
                foreach (var bad in new[] { @"\\?\C:\Windows", @"\\.\C:\Windows", @"\\?\GLOBALROOT\Device\HarddiskVolume3\Windows", @"\??\C:\Windows", "//?/C:/Windows", @"E:\Backup:hidden" })
                    if (Backup.DestProblem(bad, none) == null) return "allowed: " + bad;
                foreach (var host in new[] { @"\\localhost\C$\Windows", @"\\127.0.0.1\C$", @"\\127.0.0.2\share", @"\\[::1]\C$", @"\\" + Environment.MachineName + @"\C$", @"\\" + Environment.MachineName.ToLowerInvariant() + @".corp.local\C$", @"\\.\pipe\x" })
                    if (!Backup.LocalHost(host)) return "not seen as this PC: " + host;
                if (Backup.LocalHost(@"\\fileserver\backups") || Backup.LocalHost(@"\\10.20.30.40\backups")) return "a real server seen as this PC";
                var keepRoot = Backup.SystemRootOverride;
                try
                {
                    Backup.SystemRootOverride = Win.IsWindows ? "Q:\\" : "/nowhere/";
                    if (Backup.DestProblem(usb, none) != null) return "a good folder refused: " + Backup.DestProblem(usb, none);
                    Backup.OnSystemVolumeOverride = d => true;   // a subst / mounted drive letter that is really on C:
                    string p = Backup.DestProblem(usb, none);
                    if (p == null || !p.Contains("really on C:")) return "a drive letter that is really C: was allowed: " + p;
                }
                finally { Backup.OnSystemVolumeOverride = null; Backup.SystemRootOverride = keepRoot; }
                // on Windows, the real check: Windows' own drive, through \\?\GLOBALROOT-free normal paths, is seen as C:
                if (Win.IsWindows && !Backup.OnSystemVolume(Environment.GetFolderPath(Environment.SpecialFolder.Windows))) return "Windows' folder not seen as on C:";
                return null;
            });

            Run(check, "Shred: a file with a second name (a hard link, maybe to a Windows file) is never overwritten", () =>
            {
                if (!Win.IsWindows) return null;   // hard links: Windows only
                // in your user folder, outside AppData (the self-test's own folder is in AppData, where nothing is shredded anyway)
                string dir = Path.Combine(UserDirs.Home, "ElixarShredTest-" + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(dir);
                try {
                string other = Path.Combine(dir, "important.dat"), name2 = Path.Combine(dir, "looks-harmless.txt");
                File.WriteAllText(other, "data another program needs");
                try { Sys.Run("cmd.exe", "/c mklink /H \"" + name2 + "\" \"" + other + "\"", 15000); } catch { }
                if (!File.Exists(name2)) return null;   // can't make hard links here
                string why = SecureWipe.CanShred(name2), err;
                if (why == null || !why.Contains("hard link")) return "allowed: " + why;
                if (SecureWipe.Shred(name2, 1, out err) || File.ReadAllText(other) != "data another program needs") return "the other name was overwritten";
                File.Delete(name2);
                if (SecureWipe.CanShred(other) != null) return "a plain file refused: " + SecureWipe.CanShred(other);
                return null;
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            });

            Run(check, "Right-click menu: a Windows part dressed up as a broken app entry (from your own registry) stays Windows' own and is never blocked", () =>
            {
                Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                string clsid = "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}";
                try
                {
                    // the machine lists it as Windows' (no program file of an app); your own registry overrides it with a missing app DLL
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKLM\CLSID\" + clsid)) k.SetValue("", "A Windows part");
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKCU\*\shellex\ContextMenuHandlers\FakeBroken")) k.SetValue("", clsid);
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKCU\CLSID\" + clsid + @"\InprocServer32")) k.SetValue("", @"C:\Program Files\Gone\fake.dll");
                    // and a real app's handler, registered machine-wide with its own DLL (that one may be turned off)
                    string app = "{12121212-3434-5656-7878-909090909090}";
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKLM\*\shellex\ContextMenuHandlers\RealApp")) k.SetValue("", app);
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKLM\CLSID\" + app + @"\InprocServer32")) k.SetValue("", @"C:\Program Files\Gone2\app.dll");
                    ContextMenus.ClassesOverride = () => new List<Tuple<string, RegistryKey>> {
                        Tuple.Create("HKLM", Registry.CurrentUser.OpenSubKey(TestKey + @"\HKLM", true)), Tuple.Create("HKCU", Registry.CurrentUser.OpenSubKey(TestKey + @"\HKCU", true)) };
                    ContextMenus.BlockedOverride = () => Registry.CurrentUser.CreateSubKey(TestKey + @"\Blocked");
                    ContextMenus.ExistsOverride = p => !p.Contains("Gone");
                    var l = ContextMenus.Read();
                    var fake = l.FirstOrDefault(e => e.Clsid.Equals(clsid, StringComparison.OrdinalIgnoreCase));
                    var real = l.FirstOrDefault(e => e.Clsid.Equals(app, StringComparison.OrdinalIgnoreCase));
                    if (fake == null || !fake.WindowsOwn || fake.Level == CheckLevel.Warn) return "the dressed-up one: " + (fake == null ? "missing" : fake.Level + " own=" + fake.WindowsOwn);
                    fake.WindowsOwn = false;   // even if the list was read before (or changed afterwards), turning it off checks again
                    if (ContextMenus.SetOff(fake, true) == null) return "a Windows part was blocked";
                    using (var b = Registry.CurrentUser.OpenSubKey(TestKey + @"\Blocked")) if (b != null && b.GetValueNames().Any(v => v.Equals(clsid, StringComparison.OrdinalIgnoreCase))) return "on the blocked list";
                    if (real == null || real.WindowsOwn || !real.Missing || real.Level != CheckLevel.Warn) return "the real app's broken handler: " + (real == null ? "missing" : real.Level.ToString());
                    if (ContextMenus.SetOff(real, true) != null) return "the real app's handler couldn't be turned off";
                    // a program on a network path is never reached for (that would send your sign-in there)
                    ContextMenus.ExistsOverride = null;
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKCU\Directory\shell\NetTool\command")) k.SetValue("", "\"\\\\attacker-server\\share\\tool.exe\" \"%1\"");
                    var net = ContextMenus.Read().FirstOrDefault(e => e.Key.EndsWith("NetTool", StringComparison.OrdinalIgnoreCase));
                    if (net == null || net.Missing) return "a network program: " + (net == null ? "not listed" : "reached for");
                    return null;
                }
                finally
                {
                    ContextMenus.ClassesOverride = null; ContextMenus.BlockedOverride = null; ContextMenus.ExistsOverride = null;
                    Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                }
            });

            Run(check, "PC health: Windows' pages opened by a link (ms-settings:, windowsdefender:) open with your normal rights, never as administrator (your own registry says what opens a link)", () =>
            {
                if (!Win.IsWindows) return null;
                var opened = new List<string>();
                Shell.OpenOverride = t => opened.Add(t);
                try
                {
                    foreach (var a in new[] { HAct.Defender, HAct.Firewall, HAct.Restart }) Health.Open(a);
                    if (opened.Count != 3 || !opened.Contains("ms-settings:windowsupdate") || !opened.Contains("windowsdefender:")) return "opened as administrator: " + string.Join(", ", opened);
                }
                finally { Shell.OpenOverride = null; }
                return null;
            });

            Run(check, "1.2.70 in Hebrew, Arabic, Russian and Spanish: the new refusals and why a file can't be shredded", () =>
            {
                var words = new[] { "Choose a drive letter or a network folder (\\\\server\\share).", "Choose a real drive: that network path leads back to this PC.",
                    "Choose another drive: that one is really on C:.", "refused: a file with a second name (a hard link): shredding it would destroy the other one too",
                    "refused: a link is in the way", "refused: one of Windows' or your main folders" };
                string keep = Lang.Code;
                try
                {
                    foreach (var lang in new[] { "he", "ar", "ru", "es" })
                    {
                        Lang.Init(lang);
                        foreach (var w in words) { string t = Lang.T(w); if (t == w || t.Contains("refused") || t.Contains("hard link") || t.Contains("in the way")) return "not in " + lang + ": " + w + " -> " + t; }
                    }
                }
                finally { Lang.Init(keep); }
                return null;
            });
        }
    }
}
