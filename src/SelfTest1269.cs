// Self-test for 1.2.69: Security check (browser extensions, camera/microphone, hosts/DNS/proxy), Backup, Right-click menu
// and Games.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class SelfTest1269
    {
        static void Pump(Func<bool> done, int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (!done() && DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(15); }
        }

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

        static void W(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text); }
        const string TestKey = @"Software\ElixarSelfTest1269";

        // a fake Chrome profile and Firefox profile
        static void MakeBrowsers(string R, out string chrome, out string ffProf)
        {
            chrome = Path.Combine(R, "Chrome", "User Data");
            string prof = Path.Combine(chrome, "Default"), ext = Path.Combine(prof, "Extensions");
            string bad = new string('a', 32), good = new string('b', 32), wide = new string('c', 32), part = new string('d', 32);
            W(Path.Combine(ext, bad, "1.0_0", "manifest.json"), "{\"name\":\"__MSG_appName__\",\"default_locale\":\"en\",\"version\":\"1.0\",\"permissions\":[\"nativeMessaging\",\"<all_urls>\"]}");
            W(Path.Combine(ext, bad, "1.0_0", "_locales", "en", "messages.json"), "{\"appName\":{\"message\":\"Coupon Helper\"}}");
            W(Path.Combine(ext, good, "2.0_0", "manifest.json"), "{\"name\":\"Plain Notes\",\"version\":\"2.0\",\"permissions\":[\"storage\"]}");
            W(Path.Combine(ext, wide, "3.0_0", "manifest.json"), "{\"name\":\"Ad Blocker\",\"version\":\"3.0\",\"host_permissions\":[\"<all_urls>\"],\"permissions\":[\"declarativeNetRequest\"]}");
            W(Path.Combine(ext, part, "1_0", "manifest.json"), "{\"name\":\"Built in\",\"version\":\"1\"}");
            W(Path.Combine(prof, "Preferences"), "{\"extensions\":{\"settings\":{\"" + good + "\":{\"location\":1,\"from_webstore\":true},\"" + wide + "\":{\"location\":1,\"from_webstore\":true}}}}");
            W(Path.Combine(prof, "Secure Preferences"), "{\"extensions\":{\"settings\":{\"" + bad + "\":{\"location\":4,\"from_webstore\":false},\"" + part + "\":{\"location\":5}}}}");
            ffProf = Path.Combine(R, "Firefox", "abc.default");
            W(Path.Combine(ffProf, "extensions.json"), "{\"addons\":[" +
                "{\"id\":\"sneaky@x\",\"type\":\"extension\",\"location\":\"app-profile\",\"signedState\":0,\"active\":true,\"defaultLocale\":{\"name\":\"Sneaky\"},\"userPermissions\":{\"permissions\":[\"proxy\"],\"origins\":[]}}," +
                "{\"id\":\"ok@mozilla\",\"type\":\"extension\",\"location\":\"app-profile\",\"signedState\":2,\"active\":true,\"sourceURI\":\"https://addons.mozilla.org/x.xpi\",\"defaultLocale\":{\"name\":\"Tab Groups\"}}," +
                "{\"id\":\"sys@mozilla\",\"type\":\"extension\",\"location\":\"app-system-defaults\",\"signedState\":3,\"defaultLocale\":{\"name\":\"Built in\"}}]}");
        }

        static void MakeMenus(RegistryKey classes)
        {
            using (var k = classes.CreateSubKey(@"*\shellex\ContextMenuHandlers\GoneTool")) k.SetValue("", "{11111111-2222-3333-4444-555555555555}");
            using (var k = classes.CreateSubKey(@"CLSID\{11111111-2222-3333-4444-555555555555}")) k.SetValue("", "Gone Tool menu");
            using (var k = classes.CreateSubKey(@"CLSID\{11111111-2222-3333-4444-555555555555}\InprocServer32")) k.SetValue("", @"C:\Program Files\Gone\gone.dll");
            using (var k = classes.CreateSubKey(@"Directory\shell\OpenWithFoo")) k.SetValue("MUIVerb", "Open with &Foo");
            using (var k = classes.CreateSubKey(@"Directory\shell\OpenWithFoo\command")) k.SetValue("", "\"C:\\Program Files\\Foo\\foo.exe\" \"%1\"");
            using (var k = classes.CreateSubKey(@"Directory\Background\shell\cmd\command")) k.SetValue("", "cmd.exe /s /k pushd \"%V\"");
        }

        public static void All(string root, Action<bool, string> check)
        {
            string R = Path.Combine(root, "t1269"); Directory.CreateDirectory(R);

            // ---------- browser extensions ----------
            Run(check, "Security check, browser extensions: Chrome-style and Firefox extensions are listed with where they came from and what they can do; one from outside the store that can read every site or talk to programs is red; the browser's own aren't listed", () =>
            {
                string chrome, ff; MakeBrowsers(R, out chrome, out ff);
                BrowserExtensions.ChromiumOverride = () => new List<string[]> { new[] { "Chrome", chrome } };
                BrowserExtensions.FirefoxOverride = () => new List<string> { ff };
                try
                {
                    var l = BrowserExtensions.Read();
                    if (l.Count != 5) return "found " + l.Count + ": " + string.Join(", ", l.Select(x => x.Name));
                    var bad = l.FirstOrDefault(x => x.Name == "Coupon Helper");
                    if (bad == null || bad.Level != CheckLevel.Danger || bad.Source != "Developer mode" || !bad.Can.Contains("Talk to programs on this PC") || !bad.Can.Contains("Read and change every website")) return "the bad one: " + (bad == null ? "missing (name from _locales?)" : bad.Level + " " + bad.Source);
                    if (l[0] != bad) return "the red one isn't first";
                    var ad = l.First(x => x.Name == "Ad Blocker"); if (ad.Level != CheckLevel.Info || ad.Source != "Store") return "store extension with wide powers: " + ad.Level;
                    var notes = l.First(x => x.Name == "Plain Notes"); if (notes.Level != CheckLevel.Ok) return "plain store extension: " + notes.Level;
                    var sn = l.First(x => x.Name == "Sneaky"); if (sn.Level != CheckLevel.Danger || sn.Source != "Not signed by Mozilla" || !sn.Can.Contains("Change your proxy")) return "unsigned Firefox add-on: " + sn.Level + " " + sn.Source;
                    if (l.Any(x => x.Name == "Built in")) return "the browser's own listed";
                    if (BrowserExtensions.ExtensionsPage("Edge") != "edge://extensions" || BrowserExtensions.ExtensionsPage("Firefox") != "about:addons") return "pages";
                    // a huge settings file is skipped, not read
                    if (BrowserExtensions.ReadJson(Path.Combine(chrome, "Default", "Preferences"), 10) != null) return "size limit";
                    return null;
                }
                finally { BrowserExtensions.ChromiumOverride = null; BrowserExtensions.FirefoxOverride = null; }
            });

            // ---------- camera, microphone, location ----------
            Run(check, "Security check, camera and microphone: apps Windows lists as having used them, when, and whether in use now; an unsigned program from your folders is red", () =>
            {
                Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\Consent\webcam\NonPackaged\C:#Users#me#AppData#Local#Temp#spy.exe"))
                    { k.SetValue("LastUsedTimeStart", DateTime.Now.AddHours(-1).ToFileTime(), RegistryValueKind.QWord); k.SetValue("LastUsedTimeStop", 0L, RegistryValueKind.QWord); }
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\Consent\microphone\Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe"))
                    { k.SetValue("LastUsedTimeStart", DateTime.Now.AddDays(-2).ToFileTime(), RegistryValueKind.QWord); k.SetValue("LastUsedTimeStop", DateTime.Now.AddDays(-2).AddMinutes(5).ToFileTime(), RegistryValueKind.QWord); }
                    using (var k = Registry.CurrentUser.CreateSubKey(TestKey + @"\Consent\location\NeverUsed_123")) { k.SetValue("LastUsedTimeStart", 0L, RegistryValueKind.QWord); }
                    DeviceUse.RootOverride = () => Registry.CurrentUser.OpenSubKey(TestKey + @"\Consent");
                    var l = DeviceUse.Read(false);
                    if (l.Count != 2) return "rows: " + l.Count;
                    var cam = l.First(x => x.What == "Camera");
                    if (!cam.InUse || cam.Packaged || !string.Equals(cam.Path, @"C:\Users\me\AppData\Local\Temp\spy.exe", StringComparison.OrdinalIgnoreCase)) return "camera row: " + cam.Path + " " + cam.InUse;
                    var mic = l.First(x => x.What == "Microphone");
                    if (mic.InUse || !mic.Packaged || !string.Equals(mic.App, "WindowsSoundRecorder", StringComparison.OrdinalIgnoreCase)) return "microphone row: " + mic.App;
                    var r = new DeviceUseRow { Path = cam.Path, Signed = "NotSigned" }; DeviceUse.Judge(r);
                    if (r.Level != CheckLevel.Danger) return "unsigned from your folders: " + r.Level;
                    r = new DeviceUseRow { Path = @"C:\Program Files\Zoom\Zoom.exe", Signed = "Valid" }; DeviceUse.Judge(r);
                    if (r.Level != CheckLevel.Ok) return "signed: " + r.Level;
                    if (DeviceUse.SettingsPage("Microphone") != "ms-settings:privacy-microphone") return "settings page";
                    return null;
                }
                finally { DeviceUse.RootOverride = null; Registry.CurrentUser.DeleteSubKeyTree(TestKey, false); }
            });

            // ---------- hosts, DNS, proxy ----------
            Run(check, "Security check, hosts file: a well-known site sent to another address and a blocked security update are flagged (ad-blocking lines aren't); removing them keeps the rest and Undo puts the old file back; DNS set by hand to an unknown server and a proxy are told", () =>
            {
                string hosts = Path.Combine(R, "hosts"), undo = Path.Combine(R, "undo-hosts"); Directory.CreateDirectory(undo);
                string original = "# hosts (caf\u00e9, a comment in the PC's own code page)\r\n127.0.0.1 localhost\r\n0.0.0.0 ads.example.com\r\n1.2.3.4 www.paypal.com paypal.com\r\n127.0.0.1 update.microsoft.com\r\n0.0.0.0 www.google-analytics.com\r\n";
                var latin1 = System.Text.Encoding.GetEncoding(28591); byte[] originalBytes = latin1.GetBytes(original);
                File.WriteAllBytes(hosts, originalBytes);
                NetCheck.HostsOverride = hosts; Quarantine.OverrideRoot = undo;
                try
                {
                    int n; string p; var l = NetCheck.ReadHosts(out n, out p);
                    if (p != null || n != 6 || l.Count != 3 || l.Select(x => x.Line).Distinct().Count() != 2) return "flagged: " + l.Count + " of " + n + " " + p;
                    if (!l.Any(x => x.Name == "www.paypal.com" && x.Why.Contains("1.2.3.4")) || !l.Any(x => x.Name == "update.microsoft.com" && x.Why.Contains("Blocks"))) return "why";
                    string err = NetCheck.FixHosts(l);
                    if (err != null) return "fix: " + err;
                    string now = latin1.GetString(File.ReadAllBytes(hosts));
                    if (!now.StartsWith("# hosts (caf\u00e9")) return "the comment's bytes changed";
                    if (now.Contains("paypal") || now.Contains("update.microsoft") || !now.Contains("ads.example.com") || !now.Contains("localhost")) return "after: " + now;
                    var b = Quarantine.List().FirstOrDefault(x => x.Kind == "hosts");
                    if (b == null || Quarantine.Restore(b, null) != 1 || !File.ReadAllBytes(hosts).SequenceEqual(originalBytes)) return "Undo";
                    // Undo never writes the old content anywhere but the hosts file
                    var fake = new UndoBatch { Kind = "hosts", Dir = b.Dir }; fake.Items.Add(new[] { b.Items[0][0], Path.Combine(R, "elsewhere.txt"), "1" });
                    if (Quarantine.Restore(fake, null) != 0 || File.Exists(Path.Combine(R, "elsewhere.txt"))) return "Undo wrote elsewhere";
                    // DNS and proxy
                    if (NetCheck.JudgeDns("Wi-Fi", IPAddress.Parse("192.168.1.1"), true).Level != CheckLevel.Ok) return "router";
                    if (NetCheck.JudgeDns("Wi-Fi", IPAddress.Parse("1.1.1.1"), true).Level != CheckLevel.Ok) return "Cloudflare";
                    if (NetCheck.JudgeDns("Wi-Fi", IPAddress.Parse("203.0.113.7"), false).Level != CheckLevel.Ok) return "the provider's";
                    if (NetCheck.JudgeDns("Wi-Fi", IPAddress.Parse("203.0.113.7"), true).Level != CheckLevel.Warn) return "unknown, set by hand";
                    if (NetCheck.JudgeProxy("Proxy", "127.0.0.1:8888").Level != CheckLevel.Info) return "local proxy";
                    if (NetCheck.JudgeProxy("Proxy", "proxy.evil.example:3128").Level != (Edition.Business ? CheckLevel.Info : CheckLevel.Warn)) return "proxy";
                    var f = new HealthFacts { Free = 50L << 30, Total = 200L << 30, HostsBad = 2 };
                    var h = Health.Evaluate(f, DateTime.Now, new string[0]).Items.FirstOrDefault(i => i.Key == "hosts");
                    if (h == null || h.Area != "Security" || h.Act != HAct.SecurityCheck) return "PC health";
                    return null;
                }
                finally { NetCheck.HostsOverride = null; Quarantine.OverrideRoot = null; }
            });

            // ---------- backup ----------
            Run(check, "Backup: copies new and changed files to another drive, keeps the older copy of a changed file and a deleted file for 30 days, never onto C: or inside what it backs up, only folders in your own user folder", () =>
            {
                string home = Path.Combine(R, "home"), docs = Path.Combine(home, "Documents"), pics = Path.Combine(home, "Pictures"), dest = Path.Combine(R, "USB");
                W(Path.Combine(docs, "a.txt"), "one"); W(Path.Combine(docs, "sub", "b.txt"), "two"); W(Path.Combine(pics, "p.jpg"), "pic");
                Directory.CreateDirectory(dest);
                string other = Path.Combine(R, "OtherUser", "Documents"); W(Path.Combine(other, "secret.txt"), "theirs");
                Backup.FoldersOverride = () => new List<string[]> { new[] { "Documents", "Documents", docs }, new[] { "Pictures", "Pictures", pics }, new[] { "Other", "Other", other } };
                Backup.HomeOverride = () => home; Backup.SystemRootOverride = Win.IsWindows ? "Q:\\" : "/nowhere/";
                string keepDest = Backup.Dest, keepLast = Settings.Get("BackupLast", ""), keepSum = Backup.LastSummary;
                try
                {
                    if (Backup.SourceProblem(other) == null) return "another account's folder allowed";
                    if (Backup.DestProblem(Path.Combine(docs, "bk"), new[] { docs }) == null && Directory.Exists(docs)) { Directory.CreateDirectory(Path.Combine(docs, "bk")); if (Backup.DestProblem(Path.Combine(docs, "bk"), new[] { docs }) == null) return "a backup inside Documents allowed"; }
                    Backup.SystemRootOverride = Path.GetPathRoot(Path.GetFullPath(dest));
                    if (Win.IsWindows && Backup.DestProblem(dest, new[] { docs }) == null) return "a backup on C: allowed";
                    Backup.SystemRootOverride = Win.IsWindows ? "Q:\\" : "/nowhere/";
                    if (Backup.DestProblem(Path.Combine(R, "not-there"), new[] { docs }) == null) return "a missing drive allowed";

                    var r1 = Backup.Run(dest, new[] { "Documents", "Pictures", "Other" }, DateTime.Now.AddMinutes(-10), null, CancellationToken.None);
                    string broot = Backup.Root(dest);
                    if (r1.Problem != null || r1.Copied != 3 || !File.Exists(Path.Combine(broot, "Documents", "sub", "b.txt")) || Directory.Exists(Path.Combine(broot, "Other"))) return "first: " + r1.Summary() + " " + string.Join("; ", r1.Errors);
                    if (!r1.Errors.Any(e => e.Contains("not in your user folder"))) return "the other account wasn't said";
                    File.WriteAllText(Path.Combine(docs, "a.txt"), "one, changed"); File.SetLastWriteTime(Path.Combine(docs, "a.txt"), DateTime.Now.AddMinutes(1));
                    File.Delete(Path.Combine(docs, "sub", "b.txt"));
                    var r2 = Backup.Run(dest, new[] { "Documents", "Pictures" }, DateTime.Now, null, CancellationToken.None);
                    if (r2.Updated != 1 || r2.Removed != 1 || r2.Kept != 1 || r2.Copied != 0) return "second: " + r2.Summary();
                    if (File.ReadAllText(Path.Combine(broot, "Documents", "a.txt")) != "one, changed") return "not updated";
                    var vers = Directory.GetFiles(Path.Combine(broot, "_versions"), "a.txt", SearchOption.AllDirectories);
                    var dels = Directory.GetFiles(Path.Combine(broot, "_deleted"), "b.txt", SearchOption.AllDirectories);
                    if (vers.Length != 1 || File.ReadAllText(vers[0]) != "one" || dels.Length != 1) return "kept aside: " + vers.Length + "/" + dels.Length;
                    if (Directory.GetFiles(broot, "*.elixar-part", SearchOption.AllDirectories).Length != 0) return "a half copy was left";
                    // after 30 days the older copies go
                    var r3 = Backup.Run(dest, new[] { "Documents", "Pictures" }, DateTime.Now.AddDays(31), null, CancellationToken.None);
                    if (Directory.GetFiles(Path.Combine(broot, "_versions"), "*", SearchOption.AllDirectories).Length != 0) return "old versions kept";
                    if (!File.Exists(Path.Combine(broot, "Elixar backup.txt")) || Backup.Last == null) return "notes";
                    if (!Backup.CreateArgs(@"C:\Program Files\Elixar\Elixar.exe", "week", "13:17").Contains("--backup\" /SC WEEKLY /D SUN /ST 13:17") || Backup.CreateArgs("x", "day", "13:17").Contains("/RL")) return "schedule";
                    var f = new HealthFacts { Free = 50L << 30, Total = 200L << 30, ElixarBackupOn = true, ElixarBackup = DateTime.Now.AddDays(-20), FileHistory = false, OneDriveBackup = false };
                    var items = Health.Evaluate(f, DateTime.Now, new string[0]).Items;
                    if (!items.Any(i => i.Key == "elixarbackup" && i.Act == HAct.ElixarBackup)) return "PC health: an old backup";
                    f.ElixarBackup = DateTime.Now.AddDays(-2);
                    var ok = Health.Evaluate(f, DateTime.Now, new string[0]).Items.FirstOrDefault(i => i.Key == "backup");
                    if (ok == null || ok.Level != HLevel.Ok || !ok.Title.Contains("Elixar Backup")) return "PC health: backed up by Elixar";
                    // links: a junction put in the backup drive is never followed, and (as administrator) a file with a second name is skipped
                    if (Win.IsWindows)
                    {
                        string victim = Path.Combine(R, "victim"), evil = Path.Combine(R, "USB2"); Directory.CreateDirectory(victim); Directory.CreateDirectory(evil);
                        try { Sys.Run("cmd.exe", "/c mklink /J \"" + Path.Combine(evil, "Elixar Backup") + "\" \"" + victim + "\"", 15000); } catch { }
                        if (Directory.Exists(Path.Combine(evil, "Elixar Backup")))
                        {
                            var jr = Backup.Run(evil, new[] { "Documents" }, DateTime.Now, null, CancellationToken.None);
                            if (jr.Problem == null || Directory.GetFileSystemEntries(victim).Length > 0) return "a link in the backup drive was followed";
                        }
                        if (Sys.IsAdmin())
                        {
                            string secret = Path.Combine(R, "admin-only.txt"), hl = Path.Combine(docs, "hl.txt"); File.WriteAllText(secret, "secret");
                            try { Sys.Run("cmd.exe", "/c mklink /H \"" + hl + "\" \"" + secret + "\"", 15000); } catch { }
                            if (File.Exists(hl))
                            {
                                var hr = Backup.Run(dest, new[] { "Documents" }, DateTime.Now, null, CancellationToken.None);
                                File.Delete(hl);
                                if (File.Exists(Path.Combine(Backup.Root(dest), "Documents", "hl.txt")) || !hr.Errors.Any(x => x.Contains("hard link"))) return "a hard link was copied as administrator";
                            }
                        }
                    }
                    // a company PC: off unless IT allows it (company files on a USB disk are IT's call)
                    var keepCo = Policy.CompanyOverride; var keepOrg = Policy.OrgOverride;
                    try
                    {
                        var org = new Dictionary<string, string>(); Policy.OrgOverride = () => org; Policy.CompanyOverride = () => "contoso.local";
                        if (Policy.Allowed("backup")) return "company PC: backup allowed by default";
                        var blocked = Backup.Run(dest, new[] { "Documents" }, DateTime.Now, null, CancellationToken.None);
                        if (blocked.Problem == null || blocked.Copied > 0) return "company PC: the backup ran";
                        if (Backup.Scheduled(null) != 0) return "company PC: the scheduled backup complained";
                        org["backup"] = "allow";
                        if (!Policy.Allowed("backup") || Backup.Run(dest, new[] { "Documents" }, DateTime.Now, null, CancellationToken.None).Problem != null) return "company PC: IT allowed it but it didn't run";
                    }
                    finally { Policy.CompanyOverride = keepCo; Policy.OrgOverride = keepOrg; }
                    return null;
                }
                finally { Backup.FoldersOverride = null; Backup.HomeOverride = null; Backup.SystemRootOverride = null; Backup.Dest = keepDest; Settings.Set("BackupLast", keepLast); Backup.LastSummary = keepSum; }
            });

            // ---------- right-click menu ----------
            Run(check, "Right-click menu: entries apps added are listed, one whose program is gone is flagged, Turn off and Turn on use Windows' own switches (blocked list, LegacyDisable) and nothing is deleted; Windows' own entries are never changed", () =>
            {
                Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                try
                {
                    var classes = Registry.CurrentUser.CreateSubKey(TestKey + @"\Classes");
                    MakeMenus(classes); classes.Dispose();
                    ContextMenus.ClassesOverride = () => new List<Tuple<string, RegistryKey>> { Tuple.Create("HKCU", Registry.CurrentUser.OpenSubKey(TestKey + @"\Classes", true)) };
                    ContextMenus.BlockedOverride = () => Registry.CurrentUser.CreateSubKey(TestKey + @"\Blocked");
                    ContextMenus.ExistsOverride = p => !p.Contains("Gone");
                    var l = ContextMenus.Read();
                    var gone = l.FirstOrDefault(e => string.Equals(e.Name, "GoneTool", StringComparison.OrdinalIgnoreCase));
                    var foo = l.FirstOrDefault(e => e.Name == "Open with Foo");
                    var cmd = l.FirstOrDefault(e => e.Program == "cmd.exe");
                    if (gone == null || !gone.Missing || gone.Level != CheckLevel.Warn || gone.Kind != "Handler") return "the broken one: " + (gone == null ? "missing" : gone.Level.ToString());
                    if (foo == null || foo.Missing || foo.Level != CheckLevel.Info || foo.Where != "Folders") return "the app's command: " + (foo == null ? "missing" : foo.Where);
                    if (cmd == null || !cmd.WindowsOwn) return "Windows' own";
                    if (ContextMenus.SetOff(cmd, true) == null) return "Windows' own was changed";
                    if (ContextMenus.SetOff(gone, true) != null || ContextMenus.SetOff(foo, true) != null) return "turn off";
                    var again = ContextMenus.Read();
                    if (!again.First(e => e.Clsid == gone.Clsid).Off || !again.First(e => e.Name == "Open with Foo").Off) return "not off after a new read";
                    using (var b = Registry.CurrentUser.OpenSubKey(TestKey + @"\Blocked")) if (b == null || b.GetValue(gone.Clsid) == null) return "blocked list";
                    using (var k = Registry.CurrentUser.OpenSubKey(TestKey + @"\Classes\*\shellex\ContextMenuHandlers\GoneTool")) if (k == null) return "something was deleted";
                    if (ContextMenus.SetOff(again.First(e => e.Name == "Open with Foo"), false) != null || ContextMenus.SetOff(again.First(e => e.Clsid == gone.Clsid), false) != null) return "turn on";
                    var third = ContextMenus.Read();
                    if (third.Any(e => e.Off)) return "still off";
                    var keepOrgM = Policy.OrgOverride;
                    try
                    {
                        Policy.OrgOverride = () => new Dictionary<string, string> { { "menu.change", "block" } };
                        if (ContextMenus.SetOff(third.First(e => e.Name == "Open with Foo"), true) == null || ContextMenus.Read().Any(e => e.Off)) return "changed although the policy blocks it";
                    }
                    finally { Policy.OrgOverride = keepOrgM; }
                    if (ContextMenus.ProgramOf("\"C:\\A B\\x.exe\" \"%1\"") != @"C:\A B\x.exe" || ContextMenus.ProgramOf(@"C:\Tools\y.exe %1") != @"C:\Tools\y.exe") return "program of a command";
                    return null;
                }
                finally { ContextMenus.ClassesOverride = null; ContextMenus.BlockedOverride = null; ContextMenus.ExistsOverride = null; Registry.CurrentUser.DeleteSubKeyTree(TestKey, false); }
            });

            // ---------- games ----------
            Run(check, "Games: Steam (every library), Epic, GOG and Xbox games, biggest first, with when a Steam game was last played; a game not played for 6 months is marked; the store links only take a game's real number", () =>
            {
                string steam = Path.Combine(R, "Steam"), lib2 = Path.Combine(R, "D", "SteamLibrary");
                W(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib2.Replace("\\", "\\\\") + "\"\n\t}\n}\n");
                long old = (long)(DateTime.UtcNow.AddDays(-300) - new DateTime(1970, 1, 1)).TotalSeconds, recent = (long)(DateTime.UtcNow.AddDays(-3) - new DateTime(1970, 1, 1)).TotalSeconds;
                W(Path.Combine(steam, "steamapps", "appmanifest_100.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"100\"\n\t\"name\"\t\t\"Big Old Game\"\n\t\"installdir\"\t\t\"BigOld\"\n\t\"SizeOnDisk\"\t\t\"90000000000\"\n\t\"LastPlayed\"\t\t\"" + old + "\"\n}\n");
                W(Path.Combine(lib2, "steamapps", "appmanifest_200.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"200\"\n\t\"name\"\t\t\"New Game\"\n\t\"installdir\"\t\t\"NewG\"\n\t\"SizeOnDisk\"\t\t\"20000000000\"\n\t\"LastPlayed\"\t\t\"" + recent + "\"\n}\n");
                W(Path.Combine(steam, "steamapps", "appmanifest_228980.acf"), "\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"Steamworks Shared\" \"SizeOnDisk\" \"1\" }");
                string epic = Path.Combine(R, "Epic"); W(Path.Combine(epic, "x.item"), "{\"DisplayName\":\"Epic Game\",\"InstallLocation\":\"E:\\\\Epic\\\\EG\",\"InstallSize\":50000000000,\"AppName\":\"eg\"}");
                string xbox = Path.Combine(R, "XboxGames"); W(Path.Combine(xbox, "Forza", "Content", "a.pak"), new string('x', 2048)); Directory.CreateDirectory(Path.Combine(xbox, "GameSave"));
                Games.SteamOverride = () => steam; Games.EpicManifestsOverride = () => epic; Games.XboxRootsOverride = () => new List<string> { xbox }; Games.GogOverride = () => new List<string[]>();
                try
                {
                    var l = Games.Read(true);
                    if (l.Count != 4) return "found " + l.Count + ": " + string.Join(", ", l.Select(g => g.Name));
                    if (l[0].Name != "Big Old Game" || !l[0].Unplayed(DateTime.Now) || l.First(g => g.Name == "New Game").Unplayed(DateTime.Now)) return "order or last played";
                    if (l.First(g => g.Name == "Epic Game").Bytes != 50000000000 || l.First(g => g.Name == "Forza").Bytes != 2048) return "sizes";
                    if (Games.StoreLink(l[0]) != "steam://nav/games/details/100" || Games.UninstallLink(l[0]) != "steam://uninstall/100") return "Steam links";
                    if (Games.UninstallLink(new Game { Store = "Steam", Id = "1 & calc" }) != null || Games.StoreLink(new Game { Store = "Steam", Id = "../x" }) != null) return "a bad number was linked";
                    if (Games.UninstallLink(l.First(g => g.Store == "Epic")) != null) return "uninstall for Epic";
                    var v = Games.Vdf("\"LibraryFolders\"\n{\n\t\"1\"\t\t\"D:\\\\Games\"\n}");
                    if (!v.ContainsKey("LibraryFolders/1") || v["LibraryFolders/1"] != @"D:\Games") return "old library list";
                    return null;
                }
                finally { Games.SteamOverride = null; Games.EpicManifestsOverride = null; Games.XboxRootsOverride = null; Games.GogOverride = null; }
            });

            // ---------- the four pages ----------
            Run(check, "Tools: Security check, Right-click menu, Games and Backup pages open, fill their lists and keep Windows' own right-click entries hidden until asked", () =>
            {
                string chrome, ff; MakeBrowsers(Path.Combine(R, "ui"), out chrome, out ff);
                string hosts = Path.Combine(R, "ui", "hosts"); File.WriteAllText(hosts, "127.0.0.1 localhost\r\n6.6.6.6 login.live.com\r\n");
                BrowserExtensions.ChromiumOverride = () => new List<string[]> { new[] { "Chrome", chrome } }; BrowserExtensions.FirefoxOverride = () => new List<string> { ff };
                Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                Registry.CurrentUser.CreateSubKey(TestKey + @"\Consent\webcam").Dispose();
                DeviceUse.RootOverride = () => Registry.CurrentUser.OpenSubKey(TestKey + @"\Consent");
                NetCheck.HostsOverride = hosts;
                var classes = Registry.CurrentUser.CreateSubKey(TestKey + @"\Classes"); MakeMenus(classes); classes.Dispose();
                ContextMenus.ClassesOverride = () => new List<Tuple<string, RegistryKey>> { Tuple.Create("HKCU", Registry.CurrentUser.OpenSubKey(TestKey + @"\Classes", true)) };
                ContextMenus.BlockedOverride = () => Registry.CurrentUser.CreateSubKey(TestKey + @"\Blocked");
                ContextMenus.ExistsOverride = p => !p.Contains("Gone");
                Games.SteamOverride = () => null; Games.EpicManifestsOverride = () => null; Games.XboxRootsOverride = () => new List<string>(); Games.GogOverride = () => new List<string[]> { new[] { "1", "Witcher", Path.Combine(R, "ui") } };
                bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                try
                {
                    using (var f = new MainForm(true))
                    {
                        var h = f.Handle;
                        bool sec = false, menu = false, games = false; f.SecurityChecked += () => sec = true; f.MenuDone += () => menu = true; f.GamesDone += () => games = true;
                        f.ShowSecurityCheck(); Pump(() => sec, 20000);
                        if (!sec || f.SecExtensions.Count != 5 || f.SecHosts.Count != 1) return "Security check: " + sec + " " + f.SecExtensions.Count + "/" + f.SecHosts.Count;
                        f.ShowMenu(); Pump(() => menu, 10000);
                        int shown = f.MenuRowsForTest; f.MenuShowWindowsForTest(true);
                        if (!menu || shown != 2 || f.MenuRowsForTest != 3) return "Right-click menu: " + shown + "/" + f.MenuRowsForTest;
                        f.ShowGames(); Pump(() => games, 10000);
                        if (!games || f.GameRows.Count != 1 || f.GameRows[0].Store != "GOG") return "Games: " + f.GameRows.Count;
                        f.ShowBackup();
                        if (f.BackupBoxes.Count == 0) return "Backup: no folders";
                        if (f.SetBackupDest(Path.Combine(R, "no-drive")) == null) return "Backup: a missing drive was taken";
                    }
                    return null;
                }
                finally
                {
                    MainForm.ForceWindowsUi = keepUi;
                    BrowserExtensions.ChromiumOverride = null; BrowserExtensions.FirefoxOverride = null; DeviceUse.RootOverride = null; NetCheck.HostsOverride = null;
                    ContextMenus.ClassesOverride = null; ContextMenus.BlockedOverride = null; ContextMenus.ExistsOverride = null;
                    Games.SteamOverride = null; Games.EpicManifestsOverride = null; Games.XboxRootsOverride = null; Games.GogOverride = null;
                    Registry.CurrentUser.DeleteSubKeyTree(TestKey, false);
                }
            });

            // ---------- translated ----------
            Run(check, "1.2.69 in Hebrew, Arabic, Russian and Spanish: the four new pages", () =>
            {
                var words = new List<string> { "Security check", "Browser extensions", "Camera, microphone and location", "Hosts file, DNS and proxy", "Check now", "Open the browser's extensions page",
                    "Open Windows' privacy settings", "Remove the flagged hosts lines...", "Show the hosts file", "Open proxy settings", "Read and change every website", "Talk to programs on this PC",
                    "Developer mode", "Not from the store", "Store", "Backup", "Back up to", "Folders", "When", "Every day", "Every week (Sunday)", "Only when I click Back up now", "Back up now", "Stop", "Open the backup",
                    "Choose a drive or folder...", "Right-click menu", "Show Windows' own", "Turn off", "Turn on", "Games", "Open in its store", "Uninstall in Steam...", "Open folder",
                    "Broken: its program is gone. Turn it off.", "Steam: Settings > Storage, tick the game, Move.", "The hosts file sends sites somewhere else" };
                // texts with numbers or names in them
                var dynamic = new List<string> { "Not from the store, and it can read and change every website. Remove it unless you added it yourself.",
                    "Developer mode. Remove it unless you added it yourself.", "Sends login.live.com to another address (10.0.0.5).", "1.1.1.1 (a well-known DNS service).",
                    "Nothing suspicious found. 4 extensions, 2 apps that used the camera, microphone or location.", "3 things to look at (in red and orange).", "1 thing to look at (in red and orange).",
                    "12 new and 3 changed files copied (1.2 GB), 400 unchanged, 2 deleted here kept for 30 days, 1 skipped.", "5 new and 0 changed files copied (10 MB), 0 unchanged.",
                    "Backing up Documents...", "Copied 200 files (300 MB)...", "Couldn't copy 2 (see Logs).", "14 entries added by apps, 2 broken (in orange): turn them off.",
                    "9 entries added by apps, none broken.", "7 games, 210 GB.", "7 games, 210 GB; 80 GB in games not played for over 6 months (in orange).", "Your last backup is 9 days old",
                    "Couldn't open it: no app", "Not changed: The entry is gone." };
                string keep = Lang.Code;
                try
                {
                    foreach (var lang in new[] { "he", "ar", "ru", "es" })
                    {
                        Lang.Init(lang);
                        foreach (var w in words) if (Lang.T(w) == w) return "not in " + lang + ": " + w;
                        foreach (var w in dynamic)
                        {
                            string t = Lang.T(w);
                            if (t == w || Regex.IsMatch(t, @"\b(Remove it|the backup|entries added|in games|things to look|Not from the store|Read and change|Couldn't|files copied|unchanged|Last backup)\b")) return "not in " + lang + ": " + w + " -> " + t;
                        }
                    }
                }
                finally { Lang.Init(keep); }
                return null;
            });
        }
    }
}
