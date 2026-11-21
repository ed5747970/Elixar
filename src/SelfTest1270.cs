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
                foreach (var host in new[] { @"\\localhost\C$\Windows", @"\\127.0.0.1\C$", @"\\127.0.0.2\share", @"\\[::1]\C$", @"\\" + Environment.MachineName + @"\C$", @"\\" + Environment.MachineName.ToLowerInvariant() + @".corp.local\C$", @"\\.\pipe\x", @"\\localhost.\C$", @"\\[::ffff:127.0.0.1]\C$", @"\\127.1\C$" })
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
                // a drive letter made with subst (a folder on C:) and one mapped to \\localhost\C$: both really are C:
                if (Win.IsWindows)
                {
                    string letter = "QRSTUVWXY".Select(c => c + ":").FirstOrDefault(d => !Directory.Exists(d + "\\"));
                    if (letter != null)
                    {
                        try
                        {
                            Sys.Run("subst.exe", letter + " \"" + usb + "\"", 15000);
                            if (Directory.Exists(letter + "\\") && !Backup.OnSystemVolume(letter + "\\")) return "a subst drive on C: was taken for another drive";
                        }
                        finally { try { Sys.Run("subst.exe", letter + " /D", 15000); } catch { } }
                        try
                        {
                            Sys.Run("net.exe", "use " + letter + " \\\\localhost\\C$ /persistent:no", 30000);
                            if (Directory.Exists(letter + "\\") && !Backup.OnSystemVolume(letter + "\\")) return "a drive mapped to \\\\localhost\\C$ was taken for another drive";
                        }
                        finally { try { Sys.Run("net.exe", "use " + letter + " /delete /y", 30000); } catch { } }
                    }
                }
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

            Run(check, "Right-click menu: a command shown once but registered for folders in two places is turned off (and on) in both", () =>
            {
                Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                try
                {
                    foreach (var place in new[] { "Directory", "Folder" })
                        using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKCU\" + place + @"\shell\OpenWithTool\command")) k.SetValue("", "\"C:\\Program Files\\Tool\\tool.exe\" \"%1\"");
                    ContextMenus.ClassesOverride = () => new List<Tuple<string, RegistryKey>> { Tuple.Create("HKCU", Registry.CurrentUser.OpenSubKey(TestKey + @"\HKCU", true)) };
                    ContextMenus.BlockedOverride = () => Registry.CurrentUser.CreateSubKey(TestKey + @"\Blocked");
                    ContextMenus.ExistsOverride = p => true;
                    var rows = ContextMenus.Read().Where(e => e.Key.EndsWith("OpenWithTool", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (rows.Count != 1 || rows[0].Keys.Count != 2) return "rows: " + rows.Count;
                    if (ContextMenus.SetOff(rows[0], true) != null) return "couldn't turn it off";
                    Func<string, bool> off = place => { using (var k = Registry.CurrentUser.OpenSubKey(TestKey + @"\HKCU\" + place + @"\shell\OpenWithTool")) return k.GetValueNames().Contains("LegacyDisable"); };
                    if (!off("Directory") || !off("Folder")) return "still on in one place";
                    var again = ContextMenus.Read().First(e => e.Key.EndsWith("OpenWithTool", StringComparison.OrdinalIgnoreCase));
                    if (!again.Off || ContextMenus.SetOff(again, false) != null || off("Directory") || off("Folder")) return "turning it on again";
                    return null;
                }
                finally { ContextMenus.ClassesOverride = null; ContextMenus.BlockedOverride = null; ContextMenus.ExistsOverride = null; Registry.CurrentUser.DeleteSubKeyTree(TestKey, false); }
            });

            Run(check, "Security check: an extension whose settings were edited to say \"from the store\" but that updates from somewhere else counts as not from the store", () =>
            {
                string ud = Path.Combine(R, "Chrome UD"), prof = Path.Combine(ud, "Default"), id = new string('k', 32), id2 = new string('m', 32);
                Directory.CreateDirectory(Path.Combine(prof, "Extensions", id, "1.0_0")); Directory.CreateDirectory(Path.Combine(prof, "Extensions", id2, "2.0_0"));
                File.WriteAllText(Path.Combine(prof, "Extensions", id, "1.0_0", "manifest.json"), "{\"name\":\"Coupons\",\"version\":\"1.0\",\"update_url\":\"https://evil.example/update.xml\",\"host_permissions\":[\"<all_urls>\"]}");
                File.WriteAllText(Path.Combine(prof, "Extensions", id2, "2.0_0", "manifest.json"), "{\"name\":\"Real one\",\"version\":\"2.0\",\"update_url\":\"https://clients2.google.com/service/update2/crx\",\"host_permissions\":[\"<all_urls>\"]}");
                File.WriteAllText(Path.Combine(prof, "Secure Preferences"), "{\"extensions\":{\"settings\":{\"" + id + "\":{\"location\":1,\"from_webstore\":true},\"" + id2 + "\":{\"location\":1,\"from_webstore\":true}}}}");
                BrowserExtensions.ChromiumOverride = () => new List<string[]> { new[] { "Chrome", ud } }; BrowserExtensions.FirefoxOverride = () => new List<string>();
                try
                {
                    var l = BrowserExtensions.Read();
                    var fake = l.FirstOrDefault(e => e.Id == id); var real = l.FirstOrDefault(e => e.Id == id2);
                    if (fake == null || fake.Source == "Store" || fake.Level != CheckLevel.Danger) return "the edited one: " + (fake == null ? "missing" : fake.Source + " " + fake.Level);
                    if (real == null || real.Source != "Store" || real.Level != CheckLevel.Info) return "the real store one: " + (real == null ? "missing" : real.Source + " " + real.Level);
                    if (BrowserExtensions.StoreUpdates("http://clients2.google.com/service/update2/crx") || BrowserExtensions.StoreUpdates("https://clients2.google.com.evil.example/service/update2/crx")) return "a fake store address passed";
                    return null;
                }
                finally { BrowserExtensions.ChromiumOverride = null; BrowserExtensions.FirefoxOverride = null; }
            });

            Run(check, "Hosts file: lines are removed only if they still say what you were shown (the file can change after the check)", () =>
            {
                string hosts = Path.Combine(R, "hosts-race");
                File.WriteAllText(hosts, "# hosts\r\n127.0.0.1 localhost\r\n6.6.6.6 www.paypal.com\r\n");
                NetCheck.HostsOverride = hosts;
                try
                {
                    int n; string p; var flagged = NetCheck.ReadHosts(out n, out p);
                    if (flagged.Count != 1 || flagged[0].Line != 3) return "flagged: " + flagged.Count;
                    // a VPN rewrites the file: line 3 is now something else
                    string changed = "# hosts\r\n127.0.0.1 localhost\r\n10.8.0.1 vpn-gateway.corp\r\n6.6.6.6 www.paypal.com\r\n";
                    File.WriteAllText(hosts, changed);
                    string err = NetCheck.FixHosts(flagged);
                    if (err == null || !err.Contains("changed since") || File.ReadAllText(hosts) != changed) return "the wrong line was removed: " + err;
                    // checked again: the right line goes
                    flagged = NetCheck.ReadHosts(out n, out p);
                    if (NetCheck.FixHosts(flagged) != null || File.ReadAllText(hosts).Contains("paypal") || !File.ReadAllText(hosts).Contains("vpn-gateway")) return "after checking again";
                    return null;
                }
                finally { NetCheck.HostsOverride = null; }
            });

            Run(check, "Right-click menu: a Windows tool started on a file in your folders (rundll32 ...\\AppData\\x.dll) is shown and flagged, not hidden as Windows' own", () =>
            {
                Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKCU\*\shell\Helper\command")) k.SetValue("", @"rundll32.exe C:\Users\me\AppData\Roaming\helper\h.dll,Run %1");
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\HKCU\*\shell\Notes\command")) k.SetValue("", @"notepad.exe ""%1""");
                    ContextMenus.ClassesOverride = () => new List<Tuple<string, RegistryKey>> { Tuple.Create("HKCU", Registry.CurrentUser.OpenSubKey(TestKey + @"\HKCU", true)) };
                    ContextMenus.BlockedOverride = () => Registry.CurrentUser.CreateSubKey(TestKey + @"\Blocked");
                    ContextMenus.ExistsOverride = p => true;
                    var l = ContextMenus.Read();
                    var h = l.FirstOrDefault(e => e.Key.EndsWith("Helper", StringComparison.OrdinalIgnoreCase)); var n = l.FirstOrDefault(e => e.Key.EndsWith("Notes", StringComparison.OrdinalIgnoreCase));
                    if (h == null || h.WindowsOwn || h.Level != CheckLevel.Warn) return "the rundll32 one: " + (h == null ? "missing" : h.Level + " own=" + h.WindowsOwn);
                    if (n == null || !n.WindowsOwn) return "notepad on the file you clicked should stay Windows' own";
                    if (ContextMenus.SetOff(h, true) != null) return "couldn't turn it off";
                    return null;
                }
                finally { ContextMenus.ClassesOverride = null; ContextMenus.BlockedOverride = null; ContextMenus.ExistsOverride = null; Registry.CurrentUser.DeleteSubKeyTree(TestKey, false); }
            });

            Run(check, "Games: a store's list with an empty folder or name doesn't merge games into one row", () =>
            {
                string epic = Path.Combine(R, "epic"); Directory.CreateDirectory(epic);
                File.WriteAllText(Path.Combine(epic, "a.item"), "{\"DisplayName\":\"Game A\",\"InstallLocation\":\"\",\"InstallSize\":100}");
                File.WriteAllText(Path.Combine(epic, "b.item"), "{\"DisplayName\":\"Game B\",\"InstallLocation\":\"\",\"InstallSize\":200}");
                File.WriteAllText(Path.Combine(epic, "c.item"), "{\"DisplayName\":\"Game C\",\"InstallLocation\":\"" + Path.Combine(R, "GameC").Replace("\\", "\\\\") + "\",\"InstallSize\":300}");
                Games.EpicManifestsOverride = () => epic; Games.SteamOverride = () => null; Games.XboxRootsOverride = () => new List<string>(); Games.GogOverride = () => new List<string[]>();
                try
                {
                    var l = Games.Read(false);
                    if (l.Count != 1 || l[0].Name != "Game C") return "games: " + string.Join(", ", l.Select(g => g.Name));
                    return null;
                }
                finally { Games.EpicManifestsOverride = null; Games.SteamOverride = null; Games.XboxRootsOverride = null; Games.GogOverride = null; }
            });

            Run(check, "Leftovers: a fake app listed under your own account never gets a Program Files folder (like Common Files) offered, and Windows' own folders are never offered", () =>
            {
                // outside your user folder, like Program Files (the self-test's own folder is inside it)
                string pf = Win.IsWindows ? Path.Combine(Path.GetPathRoot(Path.GetFullPath(R)), "ElixarTestPF-" + Guid.NewGuid().ToString("N").Substring(0, 6)) : Path.Combine(R, "PF");
                string common = Path.Combine(pf, "Common Files");
                Directory.CreateDirectory(common); File.WriteAllText(Path.Combine(common, "x.dll"), "x");
                string appDir = Path.Combine(pf, "Real Tool"); Directory.CreateDirectory(appDir); File.WriteAllText(Path.Combine(appDir, "t.exe"), "x");
                Leftovers.ProgramRootsOverride = () => new List<string[]> { new[] { pf, "Program Files" } };
                Leftovers.RootsOverride = () => new List<string[]>();
                try
                {
                    var fake = new AppEntry { Name = "Common Files", Publisher = "Nobody", Location = common, PerUser = true };
                    if (Win.IsWindows && Leftovers.ForApp(fake, new List<string>()).Any()) return "a per-user app got a Program Files folder offered";
                    var machine = new AppEntry { Name = "Real Tool", Publisher = "Tools Inc", Location = appDir, PerUser = false };
                    if (!Leftovers.ForApp(machine, new List<string>()).Any(l => l.Path.EndsWith("Real Tool"))) return "a real app's leftover wasn't offered";
                    Leftovers.WindowsOwnedOverride = d => d.EndsWith("Real Tool");
                    if (Leftovers.ForApp(machine, new List<string>()).Any()) return "a folder owned by Windows (TrustedInstaller) was offered";
                    return null;
                }
                finally { Leftovers.ProgramRootsOverride = null; Leftovers.RootsOverride = null; Leftovers.WindowsOwnedOverride = null; if (Win.IsWindows) try { Directory.Delete(pf, true); } catch { } }
            });

            Run(check, "Steam caches: the Steam folder your own registry names is used only if it really is Steam's (Elixar empties caches in it as administrator)", () =>
            {
                string notSteam = Path.Combine(R, "NotSteam"), steam = Path.Combine(R, "RealSteam");
                Directory.CreateDirectory(Path.Combine(notSteam, "appcache", "httpcache")); Directory.CreateDirectory(steam); File.WriteAllText(Path.Combine(steam, "steam.exe"), "x");
                try
                {
                    Caches.SteamSettingOverride = () => notSteam;
                    if (string.Equals(Caches.SteamPath(), notSteam, StringComparison.OrdinalIgnoreCase)) return "a folder without Steam in it was used";
                    Caches.SteamSettingOverride = () => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (Win.IsWindows && string.Equals(Caches.SteamPath(), Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)) return "Windows' folder was used";
                    Caches.SteamSettingOverride = () => steam.Replace('\\', '/');
                    if (!string.Equals(Caches.SteamPath().TrimEnd('\\', '/'), steam.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && Win.IsWindows) return "the real Steam folder wasn't used: " + Caches.SteamPath();
                    return null;
                }
                finally { Caches.SteamSettingOverride = null; }
            });

            Run(check, "RedirectionGuard: on Windows that has it, Elixar never goes through a folder link made without administrator rights (the trick behind 2026's Defender and Windows Update holes)", () =>
            {
                if (!Win.IsWindows) return null;
                Install.HardenJunctions();
                bool? on = Install.JunctionGuardOn();
                if (on == null) return null;   // this Windows doesn't have it yet
                if (on != true) return "the guard is off (asked: " + Install.JunctionsHardened + ")";
                return null;
            });

            Run(check, "Hosts file: lines from 2026's DNS-changer adware that block security sites (Avira, Dr.Web, Emsisoft, Safe Browsing...) are flagged", () =>
            {
                string hosts = Path.Combine(R, "hosts-adware");
                File.WriteAllText(hosts, "127.0.0.1 localhost\r\n0.0.0.0 www.avira.com\r\n0.0.0.0 safebrowsing.googleapis.com\r\n127.0.0.1 download.drweb.com\r\n0.0.0.0 ads.example.net\r\n");
                NetCheck.HostsOverride = hosts;
                try
                {
                    int n; string p; var flagged = NetCheck.ReadHosts(out n, out p);
                    var names = flagged.Select(f => f.Name).ToList();
                    if (!names.Contains("www.avira.com") || !names.Contains("safebrowsing.googleapis.com") || !names.Contains("download.drweb.com")) return "not flagged: " + string.Join(", ", names);
                    if (names.Contains("ads.example.net")) return "an ad-blocking line was flagged";
                    return null;
                }
                finally { NetCheck.HostsOverride = null; }
            });

            Run(check, "Signature checks never reach for a file on another computer (that would send your Windows sign-in there)", () =>
            {
                if (!Sys.IsNetworkPath(@"\\attacker.invalid\share\x.exe") || !Sys.IsNetworkPath("//attacker.invalid/x.exe") || !Sys.IsNetworkPath(@"\\?\UNC\attacker.invalid\x.exe") || !Sys.IsNetworkPath(@"""\\attacker.invalid\x.exe""")) return "a network path not seen as one";
                if (Sys.IsNetworkPath(@"C:\Program Files\x.exe") || Sys.IsNetworkPath(@"\\?\C:\Program Files\x.exe") || Sys.IsNetworkPath("notepad.exe")) return "a local path seen as network";
                if (!Win.IsWindows) return null;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = Trust.CheckMany(new[] { @"\\attacker.invalid\share\x.exe" });
                if (r.Count != 0) return "it was checked: " + string.Join(",", r.Keys);
                return null;
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

            Run(check, "Backup: a file that can't be copied (in use, like an open Outlook file) keeps its last good copy; two backups never run at once", () =>
            {
                string home = Path.Combine(R, "bhome"), docs = Path.Combine(home, "Documents"), dest = Path.Combine(R, "bdest");
                Directory.CreateDirectory(docs); Directory.CreateDirectory(dest);
                string pst = Path.Combine(docs, "mail.pst"); File.WriteAllText(pst, "old mail");
                Backup.FoldersOverride = () => new List<string[]> { new[] { "Documents", "Documents", docs } };
                Backup.HomeOverride = () => home; Backup.SystemRootOverride = Win.IsWindows ? "Q:\\" : "/nowhere/";
                try
                {
                    var first = Backup.Run(dest, new[] { "Documents" }, DateTime.Now, null, System.Threading.CancellationToken.None);
                    string copy = Path.Combine(Path.Combine(Backup.Root(dest), "Documents"), "mail.pst");
                    if (first.Problem != null || !File.Exists(copy)) return "first run: " + first.Problem;
                    File.WriteAllText(pst, "new mail, and more of it"); File.SetLastWriteTime(pst, DateTime.Now.AddMinutes(5));
                    using (new FileStream(pst, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // in use: nobody else can read it
                    {
                        var second = Backup.Run(dest, new[] { "Documents" }, DateTime.Now.AddSeconds(3), null, System.Threading.CancellationToken.None);
                        if (!File.Exists(copy)) return "the last good copy was lost while the file was in use";
                        string now = File.ReadAllText(copy);
                        if (now != "old mail" && now != "new mail, and more of it") return "the copy is broken: " + now;
                        if (Directory.GetFiles(Path.GetDirectoryName(copy), "*.elixar-part").Length > 0) return "a half copy was left";
                    }
                    // a backup already running (the daily one): "Back up now" waits its turn instead of copying over it
                    var held = new System.Threading.ManualResetEvent(false); var done = new System.Threading.ManualResetEvent(false);
                    var t = new System.Threading.Thread(() => { using (var m = new System.Threading.Mutex(true, @"Local\ElixarBackup")) { held.Set(); done.WaitOne(10000); m.ReleaseMutex(); } });
                    t.IsBackground = true; t.Start(); held.WaitOne(10000);
                    try
                    {
                        var busy = Backup.Run(dest, new[] { "Documents" }, DateTime.Now.AddSeconds(6), null, System.Threading.CancellationToken.None);
                        if (busy.Problem == null || !busy.Problem.Contains("already running")) return "two backups ran at once: " + busy.Problem;
                        // the daily run finding "Back up now" busy says nothing (that one is today's backup)
                        string keepDest = Backup.Dest, keepEvery = Backup.Every, keepTold = Settings.Get("BackupToldMissing", "");
                        var told = new List<string>();
                        Automation.NotifyOverride = (a, b) => told.Add(a + ": " + b);
                        try
                        {
                            Backup.Dest = dest; Backup.Every = "day"; Settings.Set("BackupToldMissing", "");
                            if (Backup.Scheduled(null) != 0 || told.Count > 0) return "the daily run complained: " + string.Join("; ", told);
                        }
                        finally { Automation.NotifyOverride = null; Backup.Dest = keepDest; Backup.Every = keepEvery; Settings.Set("BackupToldMissing", keepTold); }
                    }
                    finally { done.Set(); t.Join(10000); }
                    var after = Backup.Run(dest, new[] { "Documents" }, DateTime.Now.AddSeconds(9), null, System.Threading.CancellationToken.None);
                    if (after.Problem != null || File.ReadAllText(copy) != "new mail, and more of it") return "after it closed: " + after.Problem;
                    return null;
                }
                finally { Backup.FoldersOverride = null; Backup.HomeOverride = null; Backup.SystemRootOverride = null; }
            });

            Run(check, "Backup: the daily task runs as soon as possible after a missed time (PC off at 13:17) and on battery", () =>
            {
                if (!Backup.CatchUpScript.Contains("StartWhenAvailable = $true") || !Backup.CatchUpScript.Contains("DisallowStartIfOnBatteries = $false")) return "the settings aren't asked for";
                if (!Win.IsWindows || !Sys.IsAdmin()) return null;
                // the same settings on a test task, read back from Windows
                const string name = @"Elixar\BackupSelfTest1270";
                string o;
                try
                {
                    if (Automation.RunSchtasks("/Create /F /TN \"" + name + "\" /TR \"\\\"" + Win.SystemExe("notepad.exe") + "\\\"\" /SC DAILY /ST 13:17", out o) != 0) return "couldn't make a test task: " + o;
                    string r = Sys.RunPowerShell(Backup.CatchUpScript.Replace("'Backup'", "'BackupSelfTest1270'"), 60000);
                    if (!r.Contains("ok")) return "the script: " + r;
                    Automation.RunSchtasks("/Query /XML /TN \"" + name + "\"", out o);
                    if (!o.Contains("<StartWhenAvailable>true</StartWhenAvailable>") || !o.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>")) return "Windows didn't take it: " + o.Substring(0, Math.Min(400, o.Length));
                    return null;
                }
                finally { Automation.RunSchtasks(Automation.DeleteArgs(name), out o); }
            });

            Run(check, "1.2.70 in Hebrew, Arabic, Russian and Spanish: the new refusals and why a file can't be shredded", () =>
            {
                var words = new[] { "Choose a drive letter or a network folder (\\\\server\\share).", "Choose a real drive: that network path leads back to this PC.",
                    "Choose another drive: that one is really on C:.", "refused: a file with a second name (a hard link): shredding it would destroy the other one too",
                    "refused: a link is in the way", "refused: one of Windows' or your main folders",
                    "The hosts file changed since the check. Click Check now again.", "Runs a file from your folders through a Windows tool. Turn it off if you don't know it.", "...and 4 more", "A backup is already running." };
                string keep = Lang.Code;
                try
                {
                    foreach (var lang in new[] { "he", "ar", "ru", "es" })
                    {
                        Lang.Init(lang);
                        foreach (var w in words) { string t = Lang.T(w); if (t == w || t.Contains("refused") || t.Contains("hard link") || t.Contains("in the way") || t.Contains("more") || t.Contains("Check now")) return "not in " + lang + ": " + w + " -> " + t; }
                    }
                }
                finally { Lang.Init(keep); }
                return null;
            });
        }
    }
}
