// Tools > Security check: three read-mostly checks for tricks unwanted software plays.
//   - BrowserExtensions: extensions in Chrome, Edge, Brave, Vivaldi, Opera and Firefox; flags ones from outside the store,
//     forced by a policy or added by another program, and what they may do (read every website, talk to programs...).
//     Elixar never removes one itself: the browser's own page does that.
//   - DeviceUse: which apps used the camera, microphone and location, and when (Windows keeps this list).
//   - NetCheck: the hosts file (a site sent to another address, a security program's updates blocked), the DNS servers
//     and the proxy. Flagged hosts lines can be removed; the old file goes to Undo.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public enum CheckLevel { Ok, Info, Warn, Danger }

    // ============================ browser extensions ============================
    public class BrowserExt
    {
        public string Browser = "", Profile = "", Id = "", Name = "", Version = "", Folder = "", Source = "", Why = "";
        public List<string> Can = new List<string>();   // what it may do, in plain words
        public bool Enabled = true;
        public DateTime Updated;
        public CheckLevel Level;
    }

    public static class BrowserExtensions
    {
        public static Func<List<string[]>> ChromiumOverride;   // self-test: { browser, User Data folder }
        public static Func<List<string>> FirefoxOverride;      // self-test: profile folders

        static List<string[]> ChromiumRoots()
        {
            if (ChromiumOverride != null) return ChromiumOverride();
            string la = UserDirs.Local, ra = UserDirs.Roaming;
            var l = new List<string[]>();
            if (!string.IsNullOrEmpty(la))
            {
                l.Add(new[] { "Chrome", Path.Combine(la, @"Google\Chrome\User Data") });
                l.Add(new[] { "Edge", Path.Combine(la, @"Microsoft\Edge\User Data") });
                l.Add(new[] { "Brave", Path.Combine(la, @"BraveSoftware\Brave-Browser\User Data") });
                l.Add(new[] { "Vivaldi", Path.Combine(la, @"Vivaldi\User Data") });
                l.Add(new[] { "Chromium", Path.Combine(la, @"Chromium\User Data") });
            }
            if (!string.IsNullOrEmpty(ra)) l.Add(new[] { "Opera", Path.Combine(ra, @"Opera Software") });
            return l;
        }

        static List<string> FirefoxProfiles()
        {
            if (FirefoxOverride != null) return FirefoxOverride();
            var l = new List<string>();
            try
            {
                string p = Path.Combine(UserDirs.Roaming ?? "", @"Mozilla\Firefox\Profiles");
                if (Directory.Exists(p)) l.AddRange(Directory.GetDirectories(p));
            }
            catch { }
            return l;
        }

        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 }; }

        // a JSON file of a few MB at most, or null
        public static Dictionary<string, object> ReadJson(string path, long max)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > max || (fi.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                return Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        static object Get(object o, params string[] path)
        {
            foreach (var k in path) { var d = o as Dictionary<string, object>; if (d == null || !d.TryGetValue(k, out o)) return null; }
            return o;
        }
        static string Str(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }
        static IEnumerable<string> Strs(object o) { var a = o as System.Collections.IEnumerable; if (a == null || o is string) return new string[0]; return a.Cast<object>().Where(x => x is string).Cast<string>(); }

        static readonly Regex AllSites = new Regex(@"^(<all_urls>|\*://\*/\*?|https?://\*/\*?|\*://\*\.\*/\*|file:///\*)$", RegexOptions.IgnoreCase);

        // what the permissions allow, in plain words (the ones that matter)
        public static List<string> Abilities(IEnumerable<string> permissions, IEnumerable<string> sites)
        {
            var p = new HashSet<string>(permissions.Select(x => x.ToLowerInvariant()));
            var l = new List<string>();
            if (sites.Any(s => AllSites.IsMatch(s.Trim()))) l.Add("Read and change every website");
            if (p.Contains("nativemessaging")) l.Add("Talk to programs on this PC");
            if (p.Contains("proxy")) l.Add("Change your proxy");
            if (p.Contains("debugger")) l.Add("Use the debugger on pages");
            if (p.Contains("management")) l.Add("Manage other extensions");
            if (p.Contains("cookies")) l.Add("Read your cookies");
            if (p.Contains("webrequestblocking") || (p.Contains("webrequest") && l.Count > 0)) l.Add("Watch and block your web traffic");
            if (p.Contains("history")) l.Add("Read your history");
            if (p.Contains("clipboardread")) l.Add("Read what you copy");
            if (p.Contains("downloads")) l.Add("Manage your downloads");
            return l;
        }

        // a "__MSG_name__" name from the extension's own words
        static string Localized(string folder, string name, string defaultLocale)
        {
            var m = Regex.Match(name ?? "", @"^__MSG_(.+)__$");
            if (!m.Success) return name;
            foreach (var loc in new[] { defaultLocale, "en", "en_US" }.Where(x => !string.IsNullOrEmpty(x)))
            {
                var d = ReadJson(Path.Combine(Path.Combine(Path.Combine(folder, "_locales"), loc), "messages.json"), 2 << 20);
                if (d == null) continue;
                foreach (var kv in d)
                    if (string.Equals(kv.Key, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) { string s = Str(Get(kv.Value, "message")); if (s.Length > 0) return s; }
            }
            return m.Groups[1].Value;
        }

        public static List<BrowserExt> Read()
        {
            var all = new List<BrowserExt>();
            foreach (var root in ChromiumRoots()) try { ReadChromium(root[0], root[1], all); } catch { }
            foreach (var prof in FirefoxProfiles()) try { ReadFirefox(prof, all); } catch { }
            foreach (var e in all) Judge(e);
            return all.OrderByDescending(e => e.Level).ThenBy(e => e.Browser).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static void ReadChromium(string browser, string userData, List<BrowserExt> all)
        {
            if (!Directory.Exists(userData)) return;
            var profiles = Directory.GetDirectories(userData).Where(d => File.Exists(Path.Combine(d, "Preferences")) || Directory.Exists(Path.Combine(d, "Extensions"))).ToList();
            if (File.Exists(Path.Combine(userData, "Preferences")) || Directory.Exists(Path.Combine(userData, "Extensions"))) profiles.Add(userData);   // Opera keeps one profile here
            foreach (var prof in profiles)
            {
                // the extensions' settings: newer browsers keep them in "Secure Preferences"
                var settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in new[] { "Preferences", "Secure Preferences" })
                {
                    var d = Get(ReadJson(Path.Combine(prof, f), 64L << 20), "extensions", "settings") as Dictionary<string, object>;
                    if (d != null) foreach (var kv in d) settings[kv.Key] = kv.Value;
                }
                string extDir = Path.Combine(prof, "Extensions");
                if (!Directory.Exists(extDir)) continue;
                foreach (var idDir in Directory.GetDirectories(extDir))
                {
                    string id = Path.GetFileName(idDir);
                    if (!Regex.IsMatch(id, "^[a-p]{32}$")) continue;
                    var verDir = Directory.GetDirectories(idDir).OrderByDescending(v => Directory.GetLastWriteTimeUtc(v)).FirstOrDefault();
                    if (verDir == null) continue;
                    var mf = ReadJson(Path.Combine(verDir, "manifest.json"), 4 << 20);
                    if (mf == null) continue;
                    object st; settings.TryGetValue(id, out st);
                    int loc = 1; int.TryParse(Str(Get(st, "location")), out loc);
                    if (loc == 5 || loc == 10) continue;   // part of the browser itself
                    var perms = Strs(Get(mf, "permissions")).ToList();
                    var sites = Strs(Get(mf, "host_permissions")).Concat(perms.Where(x => x.Contains("/") || x == "<all_urls>")).ToList();
                    var cs = Get(mf, "content_scripts") as System.Collections.IEnumerable;
                    if (cs != null) foreach (var c in cs) sites.AddRange(Strs(Get(c, "matches")));
                    bool store = Str(Get(st, "from_webstore")).Equals("True", StringComparison.OrdinalIgnoreCase);
                    string upd = Str(Get(mf, "update_url"));
                    if (!store && st == null) store = upd.Contains("clients2.google.com") || upd.Contains("edge.microsoft.com");
                    var e = new BrowserExt { Browser = browser, Profile = Path.GetFileName(prof), Id = id, Folder = verDir, Version = Str(Get(mf, "version")) };
                    e.Name = Localized(verDir, Str(Get(mf, "name")), Str(Get(mf, "default_locale")));
                    e.Source = loc == 4 || loc == 8 ? "Developer mode" : loc == 7 || loc == 9 ? "Forced by a policy" : loc == 2 || loc == 3 || loc == 6 ? "Added by another program" : store ? "Store" : "Not from the store";
                    var dr = Get(st, "disable_reasons");
                    e.Enabled = Str(Get(st, "state")) != "0" && !(dr is System.Collections.IEnumerable && ((System.Collections.IEnumerable)dr).Cast<object>().Any()) && !(dr is int && (int)dr != 0);
                    e.Can = Abilities(perms, sites);
                    try { e.Updated = Directory.GetLastWriteTime(verDir); } catch { }
                    all.Add(e);
                }
            }
        }

        static void ReadFirefox(string prof, List<BrowserExt> all)
        {
            var d = ReadJson(Path.Combine(prof, "extensions.json"), 64L << 20);
            var addons = Get(d, "addons") as System.Collections.IEnumerable;
            if (addons == null) return;
            foreach (var a in addons)
            {
                if (Str(Get(a, "type")) != "extension") continue;
                string loc = Str(Get(a, "location"));
                if (loc.StartsWith("app-system") || loc == "app-builtin" || loc == "app-builtin-addons") continue;   // part of Firefox
                int signed; int.TryParse(Str(Get(a, "signedState")), out signed);
                string src = Str(Get(a, "sourceURI"));
                var e = new BrowserExt { Browser = "Firefox", Profile = Path.GetFileName(prof), Id = Str(Get(a, "id")), Version = Str(Get(a, "version")), Folder = Str(Get(a, "path")) };
                e.Name = Str(Get(a, "defaultLocale", "name")); if (e.Name.Length == 0) e.Name = e.Id;
                e.Enabled = Str(Get(a, "active")).Equals("True", StringComparison.OrdinalIgnoreCase);
                e.Source = signed <= 0 ? "Not signed by Mozilla" : loc != "app-profile" ? "Added by another program" : src.Length > 0 && !src.StartsWith("https://addons.mozilla.org/", StringComparison.OrdinalIgnoreCase) ? "Not from the store" : "Store";
                e.Can = Abilities(Strs(Get(a, "userPermissions", "permissions")), Strs(Get(a, "userPermissions", "origins")));
                long ms; if (long.TryParse(Str(Get(a, "updateDate")), out ms) && ms > 0) e.Updated = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();
                all.Add(e);
            }
        }

        // Danger: from outside the store (or forced, or added by a program) and able to read every site or talk to programs.
        // Warn: from outside the store. Info: from the store but with wide powers (ad blockers, password managers: usually fine).
        public static void Judge(BrowserExt e)
        {
            bool outside = e.Source != "Store";
            bool strong = e.Can.Contains("Read and change every website") || e.Can.Contains("Talk to programs on this PC") || e.Can.Contains("Change your proxy") || e.Can.Contains("Use the debugger on pages");
            e.Level = outside && strong ? CheckLevel.Danger : outside ? CheckLevel.Warn : strong ? CheckLevel.Info : CheckLevel.Ok;
            e.Why = e.Level == CheckLevel.Danger ? e.Source + ", and it can " + Lower(e.Can.First(c => c == "Read and change every website" || c == "Talk to programs on this PC" || c == "Change your proxy" || c == "Use the debugger on pages")) + ". Remove it unless you added it yourself."
                  : e.Level == CheckLevel.Warn ? e.Source + ". Remove it unless you added it yourself."
                  : e.Level == CheckLevel.Info ? "From the store, with wide powers. Fine if you know it (ad blockers and password managers need them)."
                  : "From the store.";
        }
        static string Lower(string s) { return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1); }

        // the browser's own program (from the machine's list, never your own settings), to open its extensions page
        public static string BrowserExe(string browser)
        {
            string exe = browser == "Chrome" ? "chrome.exe" : browser == "Edge" ? "msedge.exe" : browser == "Brave" ? "brave.exe" : browser == "Vivaldi" ? "vivaldi.exe" : browser == "Opera" ? "opera.exe" : browser == "Firefox" ? "firefox.exe" : null;
            if (exe == null || !Win.IsWindows) return null;
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                {
                    string p = k == null ? null : k.GetValue("") as string;
                    if (!string.IsNullOrEmpty(p)) { p = p.Trim('"'); if (File.Exists(p)) return p; }
                }
            }
            catch { }
            return null;
        }
        public static string ExtensionsPage(string browser)
        {
            return browser == "Edge" ? "edge://extensions" : browser == "Firefox" ? "about:addons" : browser == "Opera" ? "opera://extensions" : browser == "Brave" ? "brave://extensions" : browser == "Vivaldi" ? "vivaldi://extensions" : "chrome://extensions";
        }
    }

    // ============================ camera, microphone and location ============================
    public class DeviceUseRow
    {
        public string App = "", Path = "", What = "", Package = "";
        public DateTime LastUsed;
        public bool InUse, Packaged, Missing;
        public string Signed = "";   // "Valid", "NotSigned"... (programs that aren't Store apps)
        public CheckLevel Level; public string Why = "";
    }

    public static class DeviceUse
    {
        public static Func<RegistryKey> RootOverride;   // self-test: a key holding webcam, microphone, location
        const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

        public static List<DeviceUseRow> Read(bool checkSignatures)
        {
            var rows = new List<DeviceUseRow>();
            var roots = new List<RegistryKey>();
            try
            {
                if (RootOverride != null) roots.Add(RootOverride());
                else if (Win.IsWindows) { roots.Add(Registry.CurrentUser.OpenSubKey(Store)); roots.Add(Registry.LocalMachine.OpenSubKey(Store)); }
                foreach (var root in roots.Where(r => r != null))
                    foreach (var cap in new[] { new[] { "webcam", "Camera" }, new[] { "microphone", "Microphone" }, new[] { "location", "Location" } })
                        using (var k = root.OpenSubKey(cap[0]))
                        {
                            if (k == null) continue;
                            foreach (var sub in k.GetSubKeyNames())
                                using (var a = k.OpenSubKey(sub))
                                {
                                    if (a == null) continue;
                                    if (string.Equals(sub, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                                    {
                                        foreach (var np in a.GetSubKeyNames())
                                            using (var n = a.OpenSubKey(np)) { var r = Row(n, cap[1]); if (r == null) continue; r.Path = np.Replace('#', '\\'); r.App = AppName(r.Path); rows.Add(r); }
                                    }
                                    else { var r = Row(a, cap[1]); if (r == null) continue; r.Packaged = true; r.Package = sub; r.App = PackageName(sub); rows.Add(r); }
                                }
                        }
            }
            finally { foreach (var r in roots.Where(r => r != null)) r.Dispose(); }
            // the same app in both lists: the newest use
            rows = rows.GroupBy(r => r.What + "|" + (r.Packaged ? r.Package : r.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(r => r.LastUsed).First()).ToList();
            Dictionary<string, string[]> sig = null;
            if (checkSignatures) try { sig = Trust.CheckMany(rows.Where(r => !r.Packaged && File.Exists(r.Path)).Select(r => r.Path)); } catch { }
            foreach (var r in rows)
            {
                string[] s; if (sig != null && sig.TryGetValue(r.Path, out s)) r.Signed = s[0];
                r.Missing = !r.Packaged && !File.Exists(r.Path);
                Judge(r);
            }
            return rows.OrderByDescending(r => r.Level).ThenByDescending(r => r.LastUsed).ToList();
        }

        static DeviceUseRow Row(RegistryKey k, string what)
        {
            long start = AsLong(k.GetValue("LastUsedTimeStart")), stop = AsLong(k.GetValue("LastUsedTimeStop"));
            if (start <= 0) return null;   // allowed, never used
            var r = new DeviceUseRow { What = what };
            try { r.LastUsed = DateTime.FromFileTime(start); } catch { return null; }
            r.InUse = stop < start;
            return r;
        }
        static long AsLong(object o) { try { return o == null ? 0 : Convert.ToInt64(o, CultureInfo.InvariantCulture); } catch { return 0; } }

        static string PackageName(string family) { string n = family.Split('_')[0]; int dot = n.LastIndexOf('.'); return dot > 0 && dot < n.Length - 1 ? n.Substring(dot + 1) : n; }
        static string AppName(string path)
        {
            try { var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path); if (!string.IsNullOrEmpty(v.FileDescription)) return v.FileDescription; } catch { }
            return path.Substring(path.LastIndexOfAny(new[] { '\\', '/' }) + 1);
        }

        // in a folder any program you run can write to (not Program Files, not Windows)
        public static bool UserPlace(string p)
        {
            string l = (p ?? "").ToLowerInvariant();
            return l.Contains(@"\appdata\") || l.Contains(@"\temp\") || l.Contains(@"\downloads\") || l.Contains(@"\desktop\") || (l.StartsWith(@"c:\programdata\") && l.Count(c => c == '\\') <= 3) || l.StartsWith(@"c:\users\public\");
        }

        public static void Judge(DeviceUseRow r)
        {
            bool unsigned = !r.Packaged && r.Signed.Length > 0 && r.Signed != "Valid";
            if (!r.Packaged && unsigned && UserPlace(r.Path)) { r.Level = CheckLevel.Danger; r.Why = "An unsigned program from your folders used it. Scan it with Microsoft Defender if you don't know it."; }
            else if (!r.Packaged && unsigned) { r.Level = CheckLevel.Warn; r.Why = "Not signed: check it's a program you know."; }
            else if (r.Missing) { r.Level = CheckLevel.Info; r.Why = "The program isn't there any more."; }
            else { r.Level = CheckLevel.Ok; r.Why = r.Packaged ? "A Store app." : r.Signed == "Valid" ? "Signed." : ""; }
        }

        public static string SettingsPage(string what) { return what == "Microphone" ? "ms-settings:privacy-microphone" : what == "Location" ? "ms-settings:privacy-location" : "ms-settings:privacy-webcam"; }
    }

    // ============================ hosts file, DNS and proxy ============================
    public class HostsLine { public int Line; public string Text = "", Address = "", Name = "", Why = ""; public CheckLevel Level; }
    public class NetFinding { public string Kind = "", What = "", Value = "", Why = ""; public CheckLevel Level; }

    public static class NetCheck
    {
        public static string HostsOverride;   // self-test

        public static string HostsPath
        {
            get { return HostsOverride ?? (Win.IsWindows ? Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts") : "/etc/hosts"); }
        }

        // sites unwanted software sends somewhere else (fake log-in pages, fake updates)
        static readonly string[] Watched = { "google.com", "bing.com", "microsoft.com", "live.com", "office.com", "windowsupdate.com", "windows.com", "apple.com", "icloud.com",
            "paypal.com", "facebook.com", "instagram.com", "amazon.com", "ebay.com", "github.com", "yahoo.com", "outlook.com", "gmail.com", "whatsapp.com", "twitter.com", "x.com",
            "binance.com", "coinbase.com", "chase.com", "wellsfargo.com", "bankofamerica.com", "citi.com", "hsbc.com", "barclays.co.uk", "leumi.co.il", "bankhapoalim.co.il", "discountbank.co.il", "mizrahi-tefahot.co.il" };
        // security programs' and Windows' update sites (blocked by unwanted software so protection goes stale)
        static readonly string[] Security = { "kaspersky.com", "kaspersky-labs.com", "eset.com", "avast.com", "avg.com", "bitdefender.com", "bitdefender.net", "malwarebytes.com", "mcafee.com", "norton.com",
            "symantec.com", "symantecliveupdate.com", "sophos.com", "trendmicro.com", "f-secure.com", "secureage.com", "virustotal.com", "windowsupdate.com", "update.microsoft.com",
            "definitionupdates.microsoft.com", "wdcp.microsoft.com", "wd.microsoft.com", "smartscreen.microsoft.com", "go.microsoft.com", "download.windowsupdate.com" };

        static bool Under(string name, string domain) { return name.Equals(domain, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase); }
        static bool Blackhole(string ip) { return ip == "0.0.0.0" || ip == "::" || ip == "::1" || ip.StartsWith("127."); }

        public static List<HostsLine> ReadHosts(out int entries, out string problem)
        {
            entries = 0; problem = null;
            var l = new List<HostsLine>();
            string text;
            try
            {
                var fi = new FileInfo(HostsPath);
                if (!fi.Exists) return l;
                if (fi.Length > (32L << 20)) { problem = "The hosts file is very big: " + Fs.Fmt(fi.Length) + "."; return l; }
                text = File.ReadAllText(HostsPath);
            }
            catch (Exception e) { problem = "Couldn't read the hosts file: " + e.Message; return l; }
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i]; int hash = raw.IndexOf('#');
                var p = (hash >= 0 ? raw.Substring(0, hash) : raw).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                IPAddress ip; if (!IPAddress.TryParse(p[0], out ip)) continue;
                foreach (var name in p.Skip(1))
                {
                    entries++;
                    var h = new HostsLine { Line = i + 1, Text = raw.Trim(), Address = p[0], Name = name };
                    if (Blackhole(p[0]) && Security.Any(d => Under(name, d))) { h.Level = CheckLevel.Danger; h.Why = "Blocks a security program's or Windows' updates or checks."; }
                    else if (!Blackhole(p[0]) && Watched.Concat(Security).Any(d => Under(name, d))) { h.Level = CheckLevel.Danger; h.Why = "Sends " + name + " to another address (" + p[0] + ")."; }
                    else continue;
                    l.Add(h);
                }
            }
            // the hosts file Windows really reads can be moved (DataBasePath): only Windows' own place is normal
            if (Win.IsWindows && HostsOverride == null)
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters"))
                    {
                        string db = k == null ? null : k.GetValue("DataBasePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if (!string.IsNullOrEmpty(db) && !db.Equals(@"%SystemRoot%\System32\drivers\etc", StringComparison.OrdinalIgnoreCase) &&
                            !Environment.ExpandEnvironmentVariables(db).TrimEnd('\\').Equals(Path.GetDirectoryName(HostsPath), StringComparison.OrdinalIgnoreCase))
                            l.Insert(0, new HostsLine { Line = 0, Text = db, Name = "(Windows' setting)", Level = CheckLevel.Danger, Why = "Windows reads its hosts file from another folder: " + db + "." });
                    }
                }
                catch { }
            return l;
        }

        // removes the flagged lines (the whole line); the old file goes to Undo first. Needs administrator rights.
        public static string FixHosts(List<HostsLine> flagged)
        {
            var lines = flagged.Where(f => f.Line > 0).Select(f => f.Line).Distinct().ToList();
            if (lines.Count == 0) return "Nothing to remove.";
            string path = HostsPath;
            if (HostsOverride == null && !SafeDelete.RealPathMatches(path)) return "The hosts file isn't where it should be (a link?).";
            // byte for byte (Latin-1 maps every byte to one character): comments in any code page come back unchanged
            var latin1 = Encoding.GetEncoding(28591);
            byte[] raw = File.ReadAllBytes(path);
            string text = latin1.GetString(raw);
            var all = text.Replace("\r\n", "\n").Split('\n').ToList();
            var kept = all.Where((s, i) => !lines.Contains(i + 1)).ToList();
            var b = Quarantine.Begin("Hosts file: " + lines.Count + " line(s) removed", "hosts", raw.Length + 1);
            try
            {
                string stored = "1-hosts";
                File.WriteAllBytes(Path.Combine(b.Dir, stored), raw);
                b.Items.Add(new[] { stored, path, raw.Length.ToString(CultureInfo.InvariantCulture) });
                Quarantine.Commit(b);
            }
            catch (Exception e) { try { Quarantine.Discard(b); } catch { } return "Couldn't keep a copy in Undo: " + e.Message; }
            WriteInPlace(path, latin1.GetBytes(string.Join("\r\n", kept)));
            ChangeLog.Add("Security", "Removed lines from the hosts file", path, "", lines.Count + " line(s)", string.Join("; ", flagged.Select(f => f.Name)), false);
            return null;
        }

        // the hosts file keeps its own permissions: its content is replaced, the file isn't
        public static void WriteInPlace(string path, byte[] bytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                fs.SetLength(0); fs.Write(bytes, 0, bytes.Length);
            }
        }

        // ---------- DNS servers and proxy ----------
        static readonly Dictionary<string, string> KnownDns = new Dictionary<string, string>
        {
            { "8.8.8.8", "Google" }, { "8.8.4.4", "Google" }, { "2001:4860:4860::8888", "Google" }, { "2001:4860:4860::8844", "Google" },
            { "1.1.1.1", "Cloudflare" }, { "1.0.0.1", "Cloudflare" }, { "1.1.1.2", "Cloudflare" }, { "1.0.0.2", "Cloudflare" }, { "1.1.1.3", "Cloudflare" }, { "1.0.0.3", "Cloudflare" },
            { "2606:4700:4700::1111", "Cloudflare" }, { "2606:4700:4700::1001", "Cloudflare" },
            { "9.9.9.9", "Quad9" }, { "149.112.112.112", "Quad9" }, { "2620:fe::fe", "Quad9" }, { "2620:fe::9", "Quad9" },
            { "208.67.222.222", "OpenDNS" }, { "208.67.220.220", "OpenDNS" }, { "94.140.14.14", "AdGuard" }, { "94.140.15.15", "AdGuard" },
            { "185.228.168.9", "CleanBrowsing" }, { "185.228.169.9", "CleanBrowsing" }, { "8.26.56.26", "Comodo" }, { "8.20.247.20", "Comodo" },
        };

        public static bool Private(IPAddress a)
        {
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xfe) == 0xfc;
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        public static NetFinding JudgeDns(string adapter, IPAddress a, bool setByHand)
        {
            string s = a.ToString(); string known;
            var f = new NetFinding { Kind = "DNS", What = adapter, Value = s };
            if (Private(a)) { f.Level = CheckLevel.Ok; f.Why = "Your router or network."; }
            else if (KnownDns.TryGetValue(s, out known)) { f.Level = CheckLevel.Ok; f.Why = known + " (a well-known DNS service)."; }
            else if (!setByHand) { f.Level = CheckLevel.Ok; f.Why = "Given by your network (your internet provider)."; }
            else { f.Level = CheckLevel.Warn; f.Why = "Set by hand to a DNS server Elixar doesn't know. Unwanted software does this to send you to fake sites; if you didn't set it, change it back to automatic."; }
            return f;
        }

        public static List<NetFinding> ReadNetwork()
        {
            var l = new List<NetFinding>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var hand = HandSetDns(ni.Id);
                    foreach (var a in ni.GetIPProperties().DnsAddresses)
                        l.Add(JudgeDns(ni.Name, a, hand.Contains(a.ToString())));
                }
            }
            catch { }
            if (Win.IsWindows)
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                    {
                        if (k != null)
                        {
                            object on = k.GetValue("ProxyEnable"); string server = k.GetValue("ProxyServer") as string, pac = k.GetValue("AutoConfigURL") as string;
                            if (on is int && (int)on != 0 && !string.IsNullOrEmpty(server)) l.Add(JudgeProxy("Proxy", server));
                            if (!string.IsNullOrEmpty(pac)) l.Add(JudgeProxy("Proxy script", pac));
                        }
                    }
                }
                catch { }
            return l;
        }

        public static NetFinding JudgeProxy(string what, string value)
        {
            var f = new NetFinding { Kind = "Proxy", What = what, Value = value };
            bool local = Regex.IsMatch(value, @"(^|[=/;])(localhost|127\.\d+\.\d+\.\d+|\[::1\])(:\d+)?", RegexOptions.IgnoreCase);
            if (local) { f.Level = CheckLevel.Info; f.Why = "A program on this PC (a VPN, an antivirus or a developer tool) sees your web traffic first."; }
            else { f.Level = Edition.Business ? CheckLevel.Info : CheckLevel.Warn; f.Why = "All your web pages go through " + value + ". Fine at work or with a VPN you chose; otherwise turn it off in Settings > Network > Proxy."; }
            return f;
        }

        // DNS servers typed in by hand (DHCP gives the rest)
        static HashSet<string> HandSetDns(string id)
        {
            var h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Win.IsWindows) return h;
            foreach (var root in new[] { @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\", @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" })
                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(root + id))
                    {
                        string ns = k == null ? null : k.GetValue("NameServer") as string;
                        if (!string.IsNullOrEmpty(ns)) foreach (var s in ns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) h.Add(s);
                    }
                }
                catch { }
            return h;
        }
    }
}
