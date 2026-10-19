// Golden image: what Sysprep itself doesn't check, but makes the difference between an image that works and one that
// copies the same passwords, IDs and leftovers to every PC.
//
//   - secrets:   a saved automatic sign-in password, Wi-Fi passwords, Windows' saved sign-ins (Credential Manager),
//                old answer files Windows Setup would read before yours, saved browser passwords, SSH and cloud keys,
//                and Elixar's own email sign-in
//   - clone IDs: programs that give each PC its own ID at install (management, security and remote-support agents),
//                Microsoft Entra join and Intune enrollment, Office signed in
//   - how:       a virtual machine (take a checkpoint), Audit mode, the Start layout and default apps for new accounts
//   - after:     an image passport (what is in the image) and a capture kit (DISM scripts for Windows PE)
//
// Reading is done with Windows' own records (registry, profile files, Credential Manager, LSA) and never reads a password
// itself: only whether one is there. Every removal is listed and asked first by the window.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;

namespace CDriveCleaner
{
    // a junction, symbolic link or other reparse point: never followed or deleted through
    static class Links
    {
        public static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return false; }
        }
    }

    public static class GoldenImage
    {
        static RegistryKey Hklm() { return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default); }
        static string WinDir { get { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); } }
        static string SysDrive { get { try { string w = WinDir; return string.IsNullOrEmpty(w) ? "C:" : (Path.GetPathRoot(w) ?? @"C:\").TrimEnd('\\'); } catch { return "C:"; } } }

        // ======================= reading (Windows only; each part on its own, a failure only skips that part) =======================
        public static void ReadInto(SysprepFacts f)
        {
            if (!Win.IsWindows) return;
            var profiles = Profiles();
            try { f.AutoLogonSecret = AutoLogonSaved(); } catch { }
            try { f.WifiKeys.AddRange(WifiWithKeys()); } catch { }
            try { foreach (var c in SavedCredentials()) { f.Creds.Add(c); } } catch { }
            try { f.OldAnswers.AddRange(OldAnswerFiles()); } catch { }
            try { f.UsbAnswers.AddRange(UsbAnswerFiles()); } catch { }
            try { f.BrowserLogins.AddRange(BrowserLogins(profiles)); } catch { }
            try { f.KeyFiles.AddRange(KeyFiles(profiles)); } catch { }
            try { f.ElixarMail = MailAccount.Email; } catch { }
            try { ReadClone(f, profiles); } catch { }
            try
            {
                using (var hk = Hklm())
                {
                    using (var k = hk.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                        if (k != null) f.Vm = VmName(k.GetValue("SystemManufacturer") as string, k.GetValue("SystemProductName") as string);
                    using (var k = hk.OpenSubKey(@"SYSTEM\Setup"))
                        f.AuditMode = k != null && (k.GetValue("AuditInProgress") as int? ?? 0) == 1;
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        int b; f.Win11 = k != null && int.TryParse(k.GetValue("CurrentBuild") as string ?? "", out b) && b >= 22000;
                    }
                }
            }
            catch { }
            try
            {
                string def = DefaultProfile();
                if (def != null)
                    f.StartForNew = File.Exists(Path.Combine(def, @"AppData\Local\Microsoft\Windows\Shell\LayoutModification.json")) ||
                                    File.Exists(Path.Combine(def, @"AppData\Local\Microsoft\Windows\Shell\LayoutModification.xml"));
                f.AppsForNew = File.Exists(DefaultsMarker);
            }
            catch { }
            try { if (File.Exists(Unattend.SysprepFile)) f.CopyProfile = Unattend.Read(File.ReadAllText(Unattend.SysprepFile)).CopyProfile; } catch { }
        }

        // every account's folder (from Windows' own list, not from the folder names)
        public class ProfileDir { public string Sid = "", Name = "", Path = ""; }
        public static List<ProfileDir> Profiles()
        {
            var list = new List<ProfileDir>();
            try
            {
                using (var hk = Hklm())
                using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                    if (k != null)
                        foreach (var sid in k.GetSubKeyNames().Where(x => x.StartsWith("S-1-5-21-") && !x.EndsWith(".bak")))
                            using (var p = k.OpenSubKey(sid))
                            {
                                string path = p == null ? null : p.GetValue("ProfileImagePath") as string;
                                if (string.IsNullOrEmpty(path)) continue;
                                path = Environment.ExpandEnvironmentVariables(path);
                                if (Directory.Exists(path) && !Links.IsLink(path)) list.Add(new ProfileDir { Sid = sid, Name = System.IO.Path.GetFileName(path.TrimEnd('\\')), Path = path });
                            }
            }
            catch { }
            return list;
        }

        static string DefaultProfile()
        {
            try
            {
                using (var hk = Hklm())
                using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                {
                    string d = k == null ? null : k.GetValue("Default") as string;
                    if (string.IsNullOrEmpty(d)) return null;
                    d = Environment.ExpandEnvironmentVariables(d);
                    return Directory.Exists(d) && !Links.IsLink(d) ? d : null;
                }
            }
            catch { return null; }
        }

        // ---------- automatic sign-in: the Winlogon value, or the LSA secret Sysinternals Autologon uses ----------
        const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        static bool AutoLogonSaved()
        {
            bool reg = false;
            using (var hk = Hklm())
            using (var k = hk.OpenSubKey(Winlogon))
                reg = k != null && !string.IsNullOrEmpty(k.GetValue("DefaultPassword") as string);
            return reg || Lsa.HasSecret("DefaultPassword");
        }

        public static string RemoveAutoLogon()
        {
            try
            {
                using (var hk = Hklm())
                using (var k = hk.OpenSubKey(Winlogon, true))
                    if (k != null)
                    {
                        k.DeleteValue("DefaultPassword", false);
                        if (k.GetValue("AutoAdminLogon") != null) k.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                    }
                string e = Lsa.DeleteSecret("DefaultPassword");
                return e;
            }
            catch (Exception e) { return e.Message; }
        }

        static class Lsa
        {
            [StructLayout(LayoutKind.Sequential)] struct LsaString { public ushort Length, MaximumLength; public IntPtr Buffer; }
            [StructLayout(LayoutKind.Sequential)] struct LsaAttributes { public int Length; public IntPtr RootDirectory, ObjectName; public uint Attributes; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
            [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref LsaAttributes attrs, int access, out IntPtr handle);
            [DllImport("advapi32.dll")] static extern uint LsaRetrievePrivateData(IntPtr handle, ref LsaString key, out IntPtr data);
            [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr handle, ref LsaString key, IntPtr data);
            [DllImport("advapi32.dll")] static extern uint LsaFreeMemory(IntPtr buffer);
            [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr handle);
            [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
            const uint NotFound = 0xC0000034;

            static T With<T>(string name, int access, Func<IntPtr, LsaString, T> act, T failed)
            {
                var a = new LsaAttributes(); a.Length = Marshal.SizeOf(typeof(LsaAttributes));
                IntPtr h;
                if (LsaOpenPolicy(IntPtr.Zero, ref a, access, out h) != 0) return failed;
                IntPtr buf = Marshal.StringToHGlobalUni(name);
                try
                {
                    var key = new LsaString { Buffer = buf, Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2 + 2) };
                    return act(h, key);
                }
                finally { Marshal.FreeHGlobal(buf); LsaClose(h); }
            }

            // only whether there is one: the value itself is never read into Elixar's memory as text
            public static bool HasSecret(string name)
            {
                return With(name, 0x4, (h, key) =>
                {
                    IntPtr data;
                    uint st = LsaRetrievePrivateData(h, ref key, out data);
                    if (st != 0 || data == IntPtr.Zero) return false;
                    try { return ((LsaString)Marshal.PtrToStructure(data, typeof(LsaString))).Length > 0; }
                    finally { LsaFreeMemory(data); }
                }, false);
            }

            public static string DeleteSecret(string name)
            {
                return With(name, 0x20, (h, key) =>
                {
                    uint st = LsaStorePrivateData(h, ref key, IntPtr.Zero);
                    return st == 0 || st == NotFound ? null : "LSA error " + LsaNtStatusToWinError(st);
                }, null);
            }
        }

        // ---------- Wi-Fi: the profiles Windows keeps for every account, with a saved password ----------
        static IEnumerable<string> WifiWithKeys()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Wlansvc\Profiles\Interfaces");
            var names = new List<string>();
            if (!Directory.Exists(root)) return names;
            foreach (var dir in Directory.GetDirectories(root))
                foreach (var file in Directory.GetFiles(dir, "*.xml"))
                {
                    try
                    {
                        if (new FileInfo(file).Length > 256 * 1024) continue;
                        string n = WifiProfileWithKey(File.ReadAllText(file));
                        if (n != null && !names.Contains(n)) names.Add(n);
                    }
                    catch { }
                }
            return names;
        }

        // the profile's name when it holds a saved key (a home or office Wi-Fi password); null otherwise
        public static string WifiProfileWithKey(string xml)
        {
            try
            {
                string name = null; bool key = false;
                var s = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 };
                using (var r = XmlReader.Create(new StringReader(xml), s))
                    while (r.Read())
                    {
                        if (r.NodeType != XmlNodeType.Element) continue;
                        if (r.LocalName == "name" && name == null && r.Depth == 1) name = r.ReadElementContentAsString().Trim();
                        else if (r.LocalName == "keyMaterial") key = true;
                    }
                return key && !string.IsNullOrEmpty(name) ? name : null;
            }
            catch { return null; }
        }

        public static bool SafeWifiName(string n) { return !string.IsNullOrEmpty(n) && n.Length <= 64 && n.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) < 0; }

        public static string RemoveWifi(IEnumerable<string> names)
        {
            var errs = new List<string>();
            foreach (var n in names)
            {
                if (!SafeWifiName(n)) { errs.Add(n + ": skipped (unusual name)"); continue; }
                try { Sys.Run("netsh.exe", "wlan delete profile name=\"" + n + "\"", 30000); } catch (Exception e) { errs.Add(n + ": " + e.Message); }
            }
            var left = WifiWithKeys().Where(x => names.Contains(x)).ToList();
            foreach (var n in left) if (!errs.Any(e => e.StartsWith(n + ":"))) errs.Add(n + ": still saved");
            return errs.Count == 0 ? null : string.Join("\n", errs.Take(6));
        }

        // ---------- Credential Manager (this account) ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Credential
        {
            public uint Flags, Type; public IntPtr TargetName, Comment; public long LastWritten;
            public uint BlobSize; public IntPtr Blob; public uint Persist, AttributeCount; public IntPtr Attributes, TargetAlias, UserName;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr list);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDelete(string target, int type, int flags);
        [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);

        // { target, type }: only ones kept beyond this sign-in (session ones vanish by themselves)
        static List<string[]> SavedCredentials()
        {
            var list = new List<string[]>();
            int n; IntPtr arr;
            if (!CredEnumerate(null, 0, out n, out arr)) return list;
            try
            {
                for (int i = 0; i < n && i < 2000; i++)
                {
                    var c = (Credential)Marshal.PtrToStructure(Marshal.ReadIntPtr(arr, i * IntPtr.Size), typeof(Credential));
                    if (c.Persist == 1) continue;
                    string t = Marshal.PtrToStringUni(c.TargetName) ?? "";
                    if (t.Length > 0) list.Add(new[] { t, c.Type.ToString(CultureInfo.InvariantCulture) });
                }
            }
            finally { CredFree(arr); }
            return list;
        }

        public static string RemoveCredentials(List<string[]> creds)
        {
            var errs = new List<string>();
            foreach (var c in creds)
            {
                int type; if (!int.TryParse(c[1], out type)) continue;
                if (!CredDelete(c[0], type, 0)) { int e = Marshal.GetLastWin32Error(); if (e != 1168) errs.Add(c[0] + ": error " + e); }
            }
            return errs.Count == 0 ? null : string.Join("\n", errs.Take(6));
        }

        // ---------- answer files Windows Setup reads before the one in Sysprep's folder ----------
        public static List<string> AnswerCandidates()
        {
            string panther = Path.Combine(WinDir, "Panther");
            var l = new List<string> {
                Path.Combine(Path.Combine(panther, "Unattend"), "Unattend.xml"), Path.Combine(Path.Combine(panther, "Unattend"), "Autounattend.xml"),
                Path.Combine(panther, "Unattend.xml"), Path.Combine(panther, "Autounattend.xml"),
                SysDrive + @"\Unattend.xml", SysDrive + @"\Autounattend.xml" };
            return l;
        }

        static IEnumerable<string> OldAnswerFiles()
        {
            var found = AnswerCandidates().Where(p => File.Exists(p) && !Links.IsLink(p)).ToList();
            try
            {
                using (var hk = Hklm())
                using (var k = hk.OpenSubKey(@"SYSTEM\Setup"))
                {
                    string u = k == null ? null : k.GetValue("UnattendFile") as string;
                    if (!string.IsNullOrEmpty(u)) { u = Environment.ExpandEnvironmentVariables(u); if (File.Exists(u) && !found.Contains(u, StringComparer.OrdinalIgnoreCase)) found.Add(u); }
                }
            }
            catch { }
            return found;
        }

        static IEnumerable<string> UsbAnswerFiles()
        {
            var l = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Removable || !d.IsReady) continue;
                    foreach (var n in new[] { "Autounattend.xml", "Unattend.xml" })
                        if (File.Exists(Path.Combine(d.RootDirectory.FullName, n))) l.Add(Path.Combine(d.RootDirectory.FullName, n));
                }
                catch { }
            }
            return l;
        }

        // deletes only files from the list Elixar itself looks at (never a path handed in from elsewhere)
        public static string DeleteAnswerFiles(IEnumerable<string> paths)
        {
            var allowed = new HashSet<string>(OldAnswerFiles(), StringComparer.OrdinalIgnoreCase);
            var errs = new List<string>();
            foreach (var p in paths)
            {
                if (!allowed.Contains(p)) { errs.Add(p + ": skipped"); continue; }
                try { if (Links.IsLink(p)) { errs.Add(p + ": skipped (a link)"); continue; } File.Delete(p); }
                catch (Exception e) { errs.Add(p + ": " + e.Message); }
            }
            return errs.Count == 0 ? null : string.Join("\n", errs.Take(6));
        }

        // ---------- saved browser passwords (counted, never read) ----------
        static readonly string[][] Chromium = {
            new[] { "Chrome", @"AppData\Local\Google\Chrome\User Data" }, new[] { "Edge", @"AppData\Local\Microsoft\Edge\User Data" },
            new[] { "Brave", @"AppData\Local\BraveSoftware\Brave-Browser\User Data" }, new[] { "Vivaldi", @"AppData\Local\Vivaldi\User Data" },
            new[] { "Opera", @"AppData\Roaming\Opera Software" } };

        static IEnumerable<string> BrowserLogins(List<ProfileDir> profiles)
        {
            var l = new List<string>();
            foreach (var p in profiles)
            {
                foreach (var b in Chromium)
                {
                    string root = Path.Combine(p.Path, b[1]);
                    if (!Directory.Exists(root)) continue;
                    int total = 0;
                    foreach (var prof in SafeDirs(root).Concat(new[] { root }))
                        foreach (var file in new[] { "Login Data", "Login Data For Account" })
                        {
                            string f = Path.Combine(prof, file);
                            if (!File.Exists(f) || Links.IsLink(f)) continue;
                            int n = ChromiumLogins(ReadShared(f, 64L << 20));
                            if (n > 0) total += n;
                        }
                    if (total > 0) l.Add(b[0] + " (" + p.Name + "): " + total);
                }
                string ff = Path.Combine(p.Path, @"AppData\Roaming\Mozilla\Firefox\Profiles");
                if (Directory.Exists(ff))
                {
                    int total = 0;
                    foreach (var prof in SafeDirs(ff))
                    {
                        string f = Path.Combine(prof, "logins.json");
                        if (File.Exists(f) && !Links.IsLink(f)) { var b = ReadShared(f, 32L << 20); if (b != null) total += FirefoxLogins(Encoding.UTF8.GetString(b)); }
                    }
                    if (total > 0) l.Add("Firefox (" + p.Name + "): " + total);
                }
            }
            return l;
        }

        static IEnumerable<string> SafeDirs(string root)
        {
            try { return Directory.GetDirectories(root).Where(d => !Links.IsLink(d)).ToList(); } catch { return new List<string>(); }
        }

        static byte[] ReadShared(string file, long max)
        {
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > max || fs.Length < 100) return null;
                    var b = new byte[fs.Length]; int got = 0;
                    while (got < b.Length) { int r = fs.Read(b, got, b.Length - got); if (r <= 0) break; got += r; }
                    return got == b.Length ? b : null;
                }
            }
            catch { return null; }
        }

        public static int FirefoxLogins(string json) { return Regex.Matches(json ?? "", "\"encryptedPassword\"\\s*:\\s*\"[^\"]+\"").Count; }

        // rows of Chromium's "logins" table whose password column isn't empty, read straight from the SQLite file
        // (sites marked "never save" have an empty password). -1 when the file can't be read.
        public static int ChromiumLogins(byte[] db)
        {
            try
            {
                var sq = new MiniSqlite(db);
                string sql; int root = sq.FindTable("logins", out sql);
                if (root <= 0 || sql == null) return 0;
                int col = MiniSqlite.ColumnIndex(sql, "password_value");
                if (col < 0) return -1;
                int n = 0;
                sq.Walk(root, rec => { var t = MiniSqlite.SerialTypes(rec); if (col < t.Count && MiniSqlite.ValueLength(t[col]) > 0) n++; });
                return n;
            }
            catch { return -1; }
        }

        // ---------- SSH keys and cloud sign-ins in each account ----------
        static IEnumerable<string> KeyFiles(List<ProfileDir> profiles)
        {
            var l = new List<string>();
            foreach (var p in profiles)
            {
                string ssh = Path.Combine(p.Path, ".ssh");
                if (Directory.Exists(ssh) && !Links.IsLink(ssh))
                    foreach (var f in SafeFiles(ssh))
                    {
                        string n = Path.GetFileName(f);
                        if (n.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) || n.StartsWith("known_hosts") || n == "config" || n == "authorized_keys") continue;
                        if (LooksLikePrivateKey(f)) l.Add(f);
                    }
                foreach (var rel in new[] { @".aws\credentials", @".azure\msal_token_cache.bin", @".azure\msal_token_cache.json", @".azure\accessTokens.json", @".git-credentials",
                                            @".kube\config", @"AppData\Roaming\gcloud\credentials.db", @"AppData\Roaming\gcloud\application_default_credentials.json" })
                {
                    string f = Path.Combine(p.Path, rel);
                    if (File.Exists(f) && !Links.IsLink(f) && new FileInfo(f).Length > 0) l.Add(f);
                }
                foreach (var pair in new[] { new[] { @".docker\config.json", "\"auth\"" }, new[] { @".npmrc", "_authToken" } })
                {
                    string f = Path.Combine(p.Path, pair[0]);
                    try { if (File.Exists(f) && !Links.IsLink(f) && new FileInfo(f).Length < 1 << 20 && File.ReadAllText(f).Contains(pair[1])) l.Add(f); } catch { }
                }
            }
            return l;
        }

        static IEnumerable<string> SafeFiles(string dir) { try { return Directory.GetFiles(dir).Where(f => !Links.IsLink(f)).ToList(); } catch { return new List<string>(); } }

        static bool LooksLikePrivateKey(string file)
        {
            try
            {
                if (new FileInfo(file).Length > 64 * 1024) return false;
                using (var r = new StreamReader(file)) { var buf = new char[200]; int n = r.Read(buf, 0, buf.Length); return new string(buf, 0, n).Contains("PRIVATE KEY") || file.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase); }
            }
            catch { return false; }
        }

        // ---------- programs that give each PC its own ID ----------
        // service name -> what it is, and what to do before capture (the vendors' own golden-image steps)
        public static readonly string[][] CloneCatalog = {
            new[] { "CcmExec", "Configuration Manager client (SCCM)", "Before capture: stop CcmExec, delete C:\\Windows\\SMSCFG.INI and the SMS certificates, or install the client after deployment." },
            new[] { "IntuneManagementExtension", "Intune Management Extension", "This PC is managed by Intune. Build images on a PC that isn't enrolled." },
            new[] { "CSFalconService", "CrowdStrike Falcon", "Install it in the image with NO_START=1 (CrowdStrike's golden-image option), or after deployment." },
            new[] { "SentinelAgent", "SentinelOne", "Follow SentinelOne's golden-image (VDI) steps, or install it after deployment." },
            new[] { "CbDefense", "Carbon Black Cloud", "Use Carbon Black's golden-image (VDI) install option, or install it after deployment." },
            new[] { "cyserver", "Cortex XDR", "Use the agent's golden-image (VDI) preparation, or install it after deployment." },
            new[] { "SepMasterService", "Symantec Endpoint Protection", "Clear its hardware ID before capture (Symantec's ClientSideClonePrepTool), or install it after deployment." },
            new[] { "klnagent", "Kaspersky Network Agent", "Run klmover -dupfix before capture, or install it after deployment." },
            new[] { "EraAgentSvc", "ESET Management Agent", "Follow ESET's cloning steps (VDI / golden image), or install it after deployment." },
            new[] { "Sophos MCS Agent", "Sophos Central", "Follow Sophos' golden-image steps (remove the machine ID), or install it after deployment." },
            new[] { "EPSecurityService", "Bitdefender GravityZone", "Use Bitdefender's golden-image (VDI) install option, or install it after deployment." },
            new[] { "Tanium Client", "Tanium Client", "Follow Tanium's golden-image steps (remove the ComputerID), or install it after deployment." },
            new[] { "QualysAgent", "Qualys Cloud Agent", "Follow Qualys' golden-image steps, or install it after deployment." },
            new[] { "ir_agent", "Rapid7 Insight Agent", "Follow Rapid7's golden-image steps, or install it after deployment." },
            new[] { "Tenable Nessus Agent", "Tenable Nessus Agent", "Unlink it before capture (nessuscli agent unlink), or install it after deployment." },
            new[] { "SplunkForwarder", "Splunk Universal Forwarder", "Run splunk clone-prep-clear-config before capture." },
            new[] { "TeamViewer", "TeamViewer", "Every copy would share one TeamViewer ID. Follow TeamViewer's imaging steps, or install it after deployment." },
            new[] { "AnyDesk", "AnyDesk", "Every copy would share one AnyDesk ID. Follow AnyDesk's imaging steps, or install it after deployment." },
            new[] { "ScreenConnect Client*", "ConnectWise ScreenConnect", "Each PC needs its own ID: install it after deployment." },
            new[] { "LTService", "ConnectWise Automate", "Each PC needs its own ID: install it after deployment." },
            new[] { "Kaseya Agent*", "Kaseya agent", "Each PC needs its own ID: install it after deployment." },
            new[] { "CagService", "Datto RMM", "Each PC needs its own ID: install it after deployment." },
            new[] { "NinjaRMMAgent", "NinjaOne", "Each PC needs its own ID: install it after deployment." } };

        // pure: service names in, "Product: what to do" out
        public static List<string> CloneMatches(IEnumerable<string> services)
        {
            var have = services.ToList(); var l = new List<string>();
            foreach (var c in CloneCatalog)
            {
                bool hit = c[0].EndsWith("*") ? have.Any(s => s.StartsWith(c[0].TrimEnd('*'), StringComparison.OrdinalIgnoreCase))
                                              : have.Any(s => s.Equals(c[0], StringComparison.OrdinalIgnoreCase));
                if (hit && !l.Any(x => x.StartsWith(c[1] + ":"))) l.Add(c[1] + ": " + c[2]);
            }
            return l;
        }

        static void ReadClone(SysprepFacts f, List<ProfileDir> profiles)
        {
            var names = new List<string>();
            try { names.AddRange(System.ServiceProcess.ServiceController.GetServices().Select(s => s.ServiceName)); } catch { }
            f.CloneApps.AddRange(CloneMatches(names));
            using (var hk = Hklm())
            {
                // Defender for Endpoint counts only when it is onboarded
                try
                {
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows Advanced Threat Protection\Status"))
                        if (k != null && (k.GetValue("OnboardingState") as int? ?? 0) == 1)
                            f.CloneApps.Add("Microsoft Defender for Endpoint: Offboard it, and onboard new PCs with Microsoft's script for VDI / golden images.");
                }
                catch { }
                try { using (var k = hk.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo")) f.EntraJoined = k != null && k.SubKeyCount > 0; } catch { }
                try
                {
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments"))
                        if (k != null)
                            foreach (var n in k.GetSubKeyNames())
                                using (var e = k.OpenSubKey(n))
                                    if (e != null && string.Equals(e.GetValue("ProviderID") as string, "MS DM Server", StringComparison.OrdinalIgnoreCase)) f.MdmEnrolled = true;
                }
                catch { }
            }
            // Microsoft 365 Apps signed in (this account)
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common\Identity\Identities"))
                    if (k != null && k.SubKeyCount > 0)
                        f.CloneApps.Add("Microsoft 365 Apps (signed in): Sign out of Office before capture. Microsoft's guidance is to install Office in an image without activating it.");
            }
            catch { }
        }

        public static string VmName(string maker, string model)
        {
            string m = (maker ?? "") + " " + (model ?? "");
            if (m.IndexOf("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) >= 0 && m.IndexOf("Virtual Machine", StringComparison.OrdinalIgnoreCase) >= 0) return "Hyper-V";
            foreach (var p in new[] { new[] { "VMware", "VMware" }, new[] { "VirtualBox", "VirtualBox" }, new[] { "innotek", "VirtualBox" }, new[] { "QEMU", "QEMU/KVM" },
                                      new[] { "KVM", "QEMU/KVM" }, new[] { "Xen", "Xen" }, new[] { "Parallels", "Parallels" }, new[] { "Amazon EC2", "Amazon EC2" }, new[] { "Google Compute Engine", "Google Cloud" } })
                if (m.IndexOf(p[0], StringComparison.OrdinalIgnoreCase) >= 0) return p[1];
            return "";
        }

        // ======================= the checklist (pure: tested by the self-test) =======================
        public static void AddChecks(SysprepFacts f, List<SysprepCheck> list)
        {
            int found = 0;
            if (f.AutoLogonSecret) { found++;
                list.Add(new SysprepCheck { Id = "sec-autologon", Level = 2, Title = "A password is saved for automatic sign-in",
                    Detail = "Windows keeps a password so an account signs in by itself (Winlogon or Sysinternals Autologon). Every PC made from this image would sign in with it.",
                    FixText = "Remove it..." }); }
            if (f.WifiKeys.Count > 0) { found++;
                list.Add(new SysprepCheck { Id = "sec-wifi", Level = 2, Title = f.WifiKeys.Count == 1 ? "1 Wi-Fi password is saved" : f.WifiKeys.Count + " Wi-Fi passwords are saved",
                    Detail = "Saved Wi-Fi networks go into the image with their passwords. Remove the ones new PCs shouldn't know (this PC disconnects from them).",
                    FixText = "Remove them...", Items = f.WifiKeys.ToList() }); }
            if (f.Creds.Count > 0) { found++;
                list.Add(new SysprepCheck { Id = "sec-creds", Level = 2, Title = f.Creds.Count == 1 ? "1 saved sign-in in Credential Manager" : f.Creds.Count + " saved sign-ins in Credential Manager",
                    Detail = "Windows keeps these passwords and tokens for network drives, remote desktops and apps in this account. With this account or CopyProfile in the image, they go to every PC.",
                    FixText = "Remove them...", Items = f.Creds.Select(c => c[0]).ToList() }); }
            if (f.OldAnswers.Count > 0) { found++;
                list.Add(new SysprepCheck { Id = "sec-answer", Level = 2, Title = "An old answer file would be used instead of yours",
                    Detail = "Windows Setup reads these before the answer file in Sysprep's folder, so new PCs would get their settings. They can hold passwords too.",
                    FixText = "Delete them...", Items = f.OldAnswers.ToList() }); }
            if (f.UsbAnswers.Count > 0) { found++;
                list.Add(new SysprepCheck { Id = "sec-usb", Level = 2, Title = "A USB drive has an answer file",
                    Detail = "Windows Setup reads answer files from USB drives too. Unplug the drive before running Sysprep.", Items = f.UsbAnswers.ToList() }); }
            if (f.BrowserLogins.Count > 0) { found++;
                list.Add(new SysprepCheck { Id = "sec-browser", Level = 2, Title = "Browsers have saved passwords",
                    Detail = "Each account's browser keeps them in its profile folder, which goes into the image. Delete them in the browser (Settings > Passwords), or sign out of the browser, before capture.",
                    Items = f.BrowserLogins.ToList() }); }
            if (f.KeyFiles.Count > 0) { found++;
                list.Add(new SysprepCheck { Id = "sec-keys", Level = 2, Title = f.KeyFiles.Count == 1 ? "1 key or cloud sign-in in a user folder" : f.KeyFiles.Count + " keys and cloud sign-ins in user folders",
                    Detail = "SSH keys and command-line sign-ins (AWS, Azure, Google Cloud, Kubernetes, Docker, Git, npm) would be copied to every PC. Move them off this PC before capture.",
                    FixText = "Show the files", Items = f.KeyFiles.ToList() }); }
            if (!string.IsNullOrEmpty(f.ElixarMail)) { found++;
                list.Add(new SysprepCheck { Id = "sec-elixar", Level = 2, Title = "Elixar is signed in to email (" + f.ElixarMail + ")",
                    Detail = "Elixar's email sign-in would go into the image. Sign out before capture, and sign in again on the PCs that need it.",
                    FixText = "Sign Elixar out..." }); }
            if (found == 0)
                list.Add(new SysprepCheck { Id = "secrets", Level = 0, Title = "No saved passwords or keys found",
                    Detail = "Checked: automatic sign-in, Wi-Fi, Credential Manager, old answer files, browsers, SSH and cloud keys, and Elixar's email sign-in." });

            if (f.CloneApps.Count > 0)
                list.Add(new SysprepCheck { Id = "clone", Level = 2, Title = f.CloneApps.Count == 1 ? "1 program gives each PC its own ID" : f.CloneApps.Count + " programs give each PC its own ID",
                    Detail = "These programs make an ID when they are installed. Copied in an image, every PC shares it, and the PCs replace each other in the vendor's console. Prepare each one as shown, or install it after deployment.",
                    Items = f.CloneApps.ToList(), FixText = "Open Apps" });
            if (f.EntraJoined)
                list.Add(new SysprepCheck { Id = "entra", Level = 2, Title = "This PC is joined to Microsoft Entra ID",
                    Detail = "Microsoft: an image must come from a Windows that hasn't been joined to Entra ID (Azure AD). Disconnect it before capture, or build the image on another PC.",
                    FixText = "Open Access work or school" });
            else if (f.MdmEnrolled)
                list.Add(new SysprepCheck { Id = "mdm", Level = 2, Title = "This PC is enrolled in device management (Intune)",
                    Detail = "The enrollment would be copied into the image. Build images on a PC that isn't enrolled.", FixText = "Open Access work or school" });

            if (f.Vm.Length > 0)
                list.Add(new SysprepCheck { Id = "vm", Level = 1, Title = "This is a virtual machine (" + f.Vm + ")",
                    Detail = "Take a checkpoint (snapshot) before Sysprep. Sysprep can't be undone; with a checkpoint you can go back, update the image and capture it again." });
            list.Add(f.AuditMode
                ? new SysprepCheck { Id = "audit", Level = 0, Title = "Working in Audit mode", Detail = "Microsoft's way to build an image: changes are made before any user account exists. When you're done, run Sysprep with OOBE and Generalize." }
                : new SysprepCheck { Id = "audit", Level = 1, Title = "Not in Audit mode",
                    Detail = "Microsoft's way to build an image is Audit mode: Windows signs in as the built-in Administrator before any user account is made, so nothing personal goes into the image. Restarting into Audit mode runs Sysprep /audit.",
                    FixText = "Restart into Audit mode..." });
            list.Add(f.StartForNew && f.AppsForNew
                ? new SysprepCheck { Id = "defaults", Level = 0, Title = "New accounts get your Start menu and default apps", Detail = "Copied with Elixar. Copy again after you change them.", FixText = "Copy mine again..." }
                : new SysprepCheck { Id = "defaults", Level = 1, Title = "New accounts get Windows' standard Start menu and apps",
                    Detail = "Elixar can copy your pinned Start apps and your default apps (browser, PDF reader, mail) to every new account, the way Microsoft documents for images.",
                    FixText = "Copy mine to new accounts..." });
        }

        // ======================= image cleanup: the Cleaner's items an image doesn't need, plus four for images only =======================
        public static readonly string[] ImageCatalogNames = { "Windows Update downloads", "Delivery Optimization cache", "Windows temp files", "Your temp files", "Thumbnail cache",
            "Error reports", "Crash dumps", "Old Windows servicing logs", "Recycle Bin", "Windows upgrade media ($GetCurrent)", "Recovery update leftovers ($WinREAgent)",
            "Previous Windows installation (Windows.old)" };

        public static List<CleanItem> ImageItems()
        {
            var cat = Catalog.Build();
            var l = ImageCatalogNames.Select(n => cat.FirstOrDefault(c => c.Name == n)).Where(c => c != null).ToList();
            foreach (var c in l) c.DefaultOn = true;   // for an image all of them: nothing here is needed on the new PCs
            string sys = SysDrive, logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"winevt\Logs");
            l.Add(new CleanItem {
                Name = "Windows component cleanup, final (DISM /ResetBase)", Tier = Tier.Caution, DefaultOn = true, Where = Path.Combine(WinDir, "WinSxS"),
                Info = "Microsoft's tool removes every replaced version of Windows components for good. The image gets smaller; the updates already installed can no longer be uninstalled. Takes 10-30 minutes.",
                Measure = () => -1,
                Clean = log => {
                    log("  DISM is running. This can take 10-30 minutes...");
                    string r = Sys.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /ResetBase", 2 * 60 * 60 * 1000);
                    string last = r.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "";
                    if (last.Length > 0) log("  DISM: " + last);
                } });
            l.Add(new CleanItem {
                Name = "Windows event logs", Tier = Tier.Review, DefaultOn = true, Where = logs,
                Info = "Clears Windows' event logs, so every new PC starts with empty logs instead of this PC's history.",
                Measure = () => Fs.SizeOf(logs),
                Clean = log => {
                    var names = Sys.Run("wevtutil.exe", "el", 120000).Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && x.IndexOf('"') < 0).ToList();
                    int done = 0;
                    foreach (var n in names) { Sys.Run("wevtutil.exe", "cl \"" + n + "\"", 30000); if (++done % 200 == 0) log("  " + done + " / " + names.Count + " logs"); }
                    log("  " + names.Count + " logs cleared.");
                } });
            l.Add(new CleanItem {
                Name = "Hibernation file (hiberfil.sys)", Tier = Tier.Review, DefaultOn = false, Where = sys + @"\hiberfil.sys",
                Info = "Turns hibernation off, which removes hiberfil.sys (often several GB) from the image. Fast Startup goes off with it; turn it back on with: powercfg /hibernate on.",
                Measure = () => { try { var fi = new FileInfo(sys + @"\hiberfil.sys"); return fi.Exists ? fi.Length : 0; } catch { return 0; } },
                Clean = log => log("  " + (Sys.Run("powercfg.exe", "/hibernate off", 60000).Trim().Length == 0 ? "Hibernation is off." : "powercfg: done.")) });
            l.Add(new CleanItem {
                Name = "Restore points", Tier = Tier.Caution, DefaultOn = true, Where = sys + @"\System Volume Information",
                Info = "Deletes the Windows drive's restore points, including the ones Elixar made before Sysprep fixes. The image doesn't need them. Done last.",
                Measure = () => {
                    string o = StoreData.RunPs("$ErrorActionPreference = 'SilentlyContinue'; $v = (Get-CimInstance Win32_Volume -Filter \"DriveLetter='" + sys + "'\").DeviceID; $u = 0; " +
                                               "Get-CimInstance Win32_ShadowStorage | Where-Object { $_.Volume.DeviceID -eq $v } | ForEach-Object { $u += [int64]$_.UsedSpace }; $u", 120000).Trim();
                    long n; return long.TryParse(o.Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : -1;
                },
                Clean = log => { Sys.Run("vssadmin.exe", "delete shadows /for=" + sys + " /all /quiet", 10 * 60 * 1000); log("  Restore points deleted."); } });
            return l;
        }

        // ======================= Audit mode =======================
        public static string RestartIntoAudit()
        {
            try
            {
                string exe = Sysprep.Exe;
                if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess) exe = exe.Replace(@"\System32\", @"\Sysnative\");
                if (!File.Exists(exe)) return "Sysprep wasn't found on this PC.";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "/audit /reboot") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        // ======================= Start layout and default apps for new accounts =======================
        static string AppsFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OEMDefaultAssociations.xml"); } }
        static string DefaultsMarker { get { return Paths.FileIn("defaults-copied.txt"); } }

        public static string CopyDefaults(bool win11, Action<string> log)
        {
            string work = Paths.Work; if (work == null) return "Elixar's protected work folder isn't available.";
            string def = DefaultProfile(); if (def == null) return "The default account folder wasn't found.";
            var errs = new List<string>();
            // default apps: Microsoft's export and import (DISM); Windows' own file is kept as a copy the first time
            try
            {
                string xml = Path.Combine(work, "default-apps.xml");
                if (File.Exists(xml)) File.Delete(xml);
                Sys.Run("dism.exe", "/Online /Export-DefaultAppAssociations:\"" + xml + "\"", 300000);
                if (!File.Exists(xml)) errs.Add("Default apps: the export didn't work.");
                else
                {
                    if (File.Exists(AppsFile) && !File.Exists(AppsFile + ".before-elixar")) File.Copy(AppsFile, AppsFile + ".before-elixar");
                    string r = Sys.Run("dism.exe", "/Online /Import-DefaultAppAssociations:\"" + xml + "\"", 300000);
                    File.WriteAllText(DefaultsMarker, DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                    log("Default apps: " + (r.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0) ?? "done"));
                }
            }
            catch (Exception e) { errs.Add("Default apps: " + e.Message); }
            // Start layout: Windows 11 reads LayoutModification.json from the default account; Windows 10 imports an XML
            try
            {
                string shell = Path.Combine(def, @"AppData\Local\Microsoft\Windows\Shell");
                Directory.CreateDirectory(shell);
                string ext = win11 ? "json" : "xml", tmp = Path.Combine(work, "start-layout." + ext), dest = Path.Combine(shell, "LayoutModification." + ext);
                if (File.Exists(tmp)) File.Delete(tmp);
                string r = StoreData.RunPs("$ErrorActionPreference = 'Stop'; try { Export-StartLayout -Path " + Sys.PsQuote(tmp) + "; 'OK' } catch { 'ERROR: ' + $_.Exception.Message }", 120000).Trim();
                if (!File.Exists(tmp)) errs.Add("Start layout: " + r);
                else if (win11)
                {
                    if (File.Exists(dest) && !File.Exists(dest + ".before-elixar")) File.Copy(dest, dest + ".before-elixar");
                    File.Copy(tmp, dest, true); log("Start layout copied to new accounts.");
                }
                else
                {
                    string imp = StoreData.RunPs("$ErrorActionPreference = 'Stop'; try { Import-StartLayout -LayoutPath " + Sys.PsQuote(tmp) + " -MountPath " + Sys.PsQuote(SysDrive + "\\") + "; 'OK' } catch { 'ERROR: ' + $_.Exception.Message }", 120000).Trim();
                    if (!imp.EndsWith("OK")) errs.Add("Start layout: " + imp); else log("Start layout copied to new accounts.");
                }
            }
            catch (Exception e) { errs.Add("Start layout: " + e.Message); }
            return errs.Count == 0 ? null : string.Join("\n", errs);
        }

        // ======================= image passport =======================
        public class PassportData
        {
            public string Computer = "", Windows = "", Build = "", Edition = "", Arch = "", Installed = "";
            public long DiskUsed, DiskTotal;
            public List<string[]> Programs = new List<string[]>();   // name, version, publisher
            public List<string[]> StoreApps = new List<string[]>();  // name, version
            public List<string[]> Updates = new List<string[]>();    // id, date
            public List<string[]> Drivers = new List<string[]>();    // provider, class, version, date
            public List<SysprepCheck> Checks = new List<SysprepCheck>();
            public UnattendSpec Answer;                               // without its passwords (they are never written)
            public DateTime Made = DateTime.Now;
        }

        public static PassportData ReadPassport(List<SysprepCheck> checks, Action<string> status)
        {
            var d = new PassportData { Computer = Environment.MachineName, Arch = Unattend.ThisArch(), Checks = checks ?? new List<SysprepCheck>() };
            if (!Win.IsWindows) return d;
            try
            {
                using (var hk = Hklm())
                using (var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (k != null)
                    {
                        string build = k.GetValue("CurrentBuild") as string ?? "";
                        object ubr = k.GetValue("UBR");
                        int b; bool w11 = int.TryParse(build, out b) && b >= 22000;
                        d.Windows = ((k.GetValue("ProductName") as string) ?? "Windows").Replace("Windows 10", w11 ? "Windows 11" : "Windows 10") + " " + (k.GetValue("DisplayVersion") as string ?? "");
                        d.Build = build + (ubr is int ? "." + ubr : "");
                        d.Edition = k.GetValue("EditionID") as string ?? "";
                        object inst = k.GetValue("InstallDate");
                        if (inst is int) d.Installed = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((uint)(int)inst).ToLocalTime().ToString("yyyy-MM-dd");
                    }
            }
            catch { }
            try { var di = new DriveInfo(SysDrive); d.DiskTotal = di.TotalSize; d.DiskUsed = di.TotalSize - di.TotalFreeSpace; } catch { }
            if (status != null) status("Reading the installed programs...");
            try { foreach (var a in AppsData.ReadInstalled().OrderBy(x => x.Name)) d.Programs.Add(new[] { a.Name, a.Version, a.Publisher }); } catch { }
            if (status != null) status("Reading updates, Store apps and drivers...");
            try
            {
                string o = StoreData.RunPs("$ErrorActionPreference = 'SilentlyContinue'; " +
                    "Get-CimInstance Win32_QuickFixEngineering | ForEach-Object { 'UPD|' + $_.HotFixID + '|' + $(if ($_.InstalledOn) { $_.InstalledOn.ToString('yyyy-MM-dd') } else { '' }) }; " +
                    "Get-AppxProvisionedPackage -Online | ForEach-Object { 'APX|' + $_.DisplayName + '|' + $_.Version }; " +
                    "Get-WindowsDriver -Online | ForEach-Object { 'DRV|' + $_.ProviderName + '|' + $_.ClassName + '|' + $_.Version + '|' + $(if ($_.Date) { $_.Date.ToString('yyyy-MM-dd') } else { '' }) }", 600000);
                ApplyPassportPs(d, o);
            }
            catch { }
            try { if (File.Exists(Unattend.SysprepFile)) d.Answer = Unattend.Read(File.ReadAllText(Unattend.SysprepFile)); } catch { }
            return d;
        }

        public static void ApplyPassportPs(PassportData d, string output)
        {
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var p = raw.Trim().Split('|');
                if (p[0] == "UPD" && p.Length >= 3 && p[1].Length > 0) d.Updates.Add(new[] { p[1], p[2] });
                else if (p[0] == "APX" && p.Length >= 3 && p[1].Length > 0) d.StoreApps.Add(new[] { p[1], p[2] });
                else if (p[0] == "DRV" && p.Length >= 5) d.Drivers.Add(new[] { p[1], p[2], p[3], p[4] });
            }
        }

        static string H(string s) { return WebUtility.HtmlEncode(s ?? ""); }

        // pure: the report as one HTML file. Passwords and the product key are never in it.
        public static string Passport(PassportData d)
        {
            Func<string, string> T = Lang.T;
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"").Append(Lang.Hebrew ? "he\" dir=\"rtl" : "en").Append("\"><head><meta charset=\"utf-8\"><title>")
              .Append(H(T("Image passport") + " - " + d.Computer)).Append("</title><style>")
              .Append("body{font-family:'Segoe UI',Arial,sans-serif;margin:24px;color:#1d2433;background:#fff}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:22px 0 6px;border-bottom:1px solid #dde2ea;padding-bottom:4px}")
              .Append(".q{color:#6b7280}table{border-collapse:collapse;width:100%;font-size:13px}td,th{padding:4px 8px;border-bottom:1px solid #eef1f5;text-align:start;vertical-align:top}th{background:#f4f6f9}")
              .Append(".l3{color:#c7362f}.l2{color:#c47800}.l1{color:#2563eb}.l0{color:#007d46}.kv td:first-child{width:220px;color:#6b7280}")
              .Append("@media print{body{margin:8mm}}</style></head><body>");
            sb.Append("<h1>").Append(H(T("Image passport"))).Append("</h1><div class=\"q\">").Append(H(T("Made by Elixar on ") + d.Made.ToString("yyyy-MM-dd HH:mm"))).Append("</div>");
            Action<string, string> kv = (k, v) => sb.Append("<tr><td>").Append(H(T(k))).Append("</td><td>").Append(H(v)).Append("</td></tr>");
            sb.Append("<h2>").Append(H(T("This PC"))).Append("</h2><table class=\"kv\">");
            kv("Computer", d.Computer); kv("Windows", d.Windows); kv("Build", d.Build); kv("Edition", d.Edition); kv("Architecture", d.Arch); kv("Installed on", d.Installed);
            if (d.DiskTotal > 0) kv("Windows drive", Fs.Fmt(d.DiskUsed) + " " + T("used of") + " " + Fs.Fmt(d.DiskTotal));
            sb.Append("</table>");

            sb.Append("<h2>").Append(H(T("Sysprep checklist"))).Append("</h2><table><tr><th>").Append(H(T("Status"))).Append("</th><th>").Append(H(T("Check"))).Append("</th></tr>");
            foreach (var c in d.Checks)
            {
                string tag = c.Level == 3 ? "Stops Sysprep" : c.Level == 2 ? "Fix first" : c.Level == 1 ? "Good to know" : "Ready";
                // only counts and titles: never the names of Wi-Fi networks, saved sign-ins or key files
                bool secret = c.Id.StartsWith("sec-");
                sb.Append("<tr><td class=\"l").Append(c.Level).Append("\">").Append(H(T(tag))).Append("</td><td>").Append(H(T(c.Title)));
                if (!secret && c.Items.Count > 0) sb.Append("<div class=\"q\">").Append(H(string.Join(", ", c.Items.Take(20)))).Append(c.Items.Count > 20 ? " ..." : "").Append("</div>");
                sb.Append("</td></tr>");
            }
            sb.Append("</table>");

            if (d.Answer != null)
            {
                var a = d.Answer;
                sb.Append("<h2>").Append(H(T("Answer file"))).Append("</h2><table class=\"kv\">");
                kv("Computer name", a.ComputerName); kv("Owner", a.Owner); kv("Organization", a.Organization); kv("Time zone", a.TimeZone);
                kv("Languages", a.UILanguage + " / " + a.SystemLocale + " / " + a.UserLocale + " / " + a.InputLocale);
                kv("Local account", a.CreateAccount ? a.AccountName + (a.AutoLogon ? " (" + T("signs in once by itself") + ")" : "") : T("none"));
                kv("Built-in Administrator", a.EnableAdmin ? T("on") : T("off"));
                kv("Product key", a.ProductKey.Length > 0 ? T("set") : T("none"));
                kv("CopyProfile", a.CopyProfile ? T("on") : T("off"));
                kv("First sign-in commands", a.FirstLogon.Count.ToString(CultureInfo.InvariantCulture));
                kv("Deleted after setup", a.DeleteAfter ? T("yes") : T("no"));
                sb.Append("</table>");
            }

            Action<string, List<string[]>, string[]> table = (title, rows, heads) =>
            {
                sb.Append("<h2>").Append(H(T(title))).Append(" (").Append(rows.Count).Append(")</h2>");
                if (rows.Count == 0) { sb.Append("<div class=\"q\">").Append(H(T("None found."))).Append("</div>"); return; }
                sb.Append("<table><tr>"); foreach (var h in heads) sb.Append("<th>").Append(H(T(h))).Append("</th>"); sb.Append("</tr>");
                foreach (var r in rows.Take(3000)) { sb.Append("<tr>"); for (int i = 0; i < heads.Length; i++) sb.Append("<td>").Append(H(i < r.Length ? r[i] : "")).Append("</td>"); sb.Append("</tr>"); }
                sb.Append("</table>");
            };
            table("Programs", d.Programs, new[] { "Name", "Version", "Publisher" });
            table("Store apps for every new account", d.StoreApps, new[] { "Name", "Version" });
            table("Windows updates", d.Updates.OrderByDescending(u => u[1]).ToList(), new[] { "Update", "Installed" });
            table("Added drivers", d.Drivers.OrderBy(x => x[1]).ToList(), new[] { "Provider", "Class", "Version", "Date" });
            sb.Append("<p class=\"q\">").Append(H(T("Passwords, the product key and the names of Wi-Fi networks and saved sign-ins are never written in this passport."))).Append("</p></body></html>");
            return sb.ToString();
        }

        // ======================= capture kit (Windows PE / Windows Setup's command prompt) =======================
        // pure: file name -> contents (CRLF). Names typed in the scripts are checked to letters, digits and _ before use.
        public static Dictionary<string, string> CaptureKit()
        {
            const string head =
@"@echo off
setlocal EnableExtensions EnableDelayedExpansion
set ""KIT=%~d0""
if /i ""%KIT%""==""X:"" ( echo  Copy the kit to a USB drive and run it from there. & goto end )
";
            const string askName =
@":name
set ""NAME=""
set /p ""NAME= Image name (letters, digits and _ ; max 40): ""
if not defined NAME goto name
set ""NAME=!NAME:""=!""
if not defined NAME goto name
if not ""!NAME:~40!""=="""" ( echo  Too long. & goto name )
for /f ""eol=; delims=0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_"" %%x in (""A!NAME!"") do ( echo  Use letters, digits and _ only. & goto name )
";
            string capture = head +
@"title Elixar capture kit
echo.
echo  ELIXAR CAPTURE KIT
echo  Captures the Windows partition of this PC into a .wim image with DISM.
echo  Run it after Sysprep has shut the PC down, from Windows PE or from Windows Setup (press Shift+F10).
echo  Don't start Windows on this PC before the capture: it would undo Sysprep's preparation.
echo.
set ""WIN=""
for %%L in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do (
  if not defined WIN if /i not ""%%L:""==""%KIT%"" if exist ""%%L:\Windows\System32\config\SYSTEM"" set ""WIN=%%L:""
)
if not defined WIN ( echo  No Windows partition was found. & goto end )
fsutil fsinfo volumeinfo %KIT%\ 2>nul | find /i ""FAT32"" >nul && (
  echo  This drive is FAT32: it can't hold files over 4 GB.
  echo  Use an NTFS or exFAT drive for the capture, then Split-Image.cmd to make FAT32 parts.
  goto end
)
echo  Windows partition: %WIN%\
echo  Image folder:      %KIT%\Images
echo.
" + askName +
@"if not exist ""%KIT%\Images"" mkdir ""%KIT%\Images""
set ""WIM=%KIT%\Images\!NAME!.wim""
if exist ""!WIM!"" ( echo  !WIM! already exists. Choose another name. & goto name )
echo.
set ""OK=""
set /p ""OK= Capture %WIN%\ into !WIM! now? (Y/N): ""
if /i not ""!OK!""==""Y"" goto end
echo.
echo  Capturing. This takes 15-60 minutes...
dism /Capture-Image /ImageFile:""!WIM!"" /CaptureDir:%WIN%\ /Name:""!NAME!"" /Compress:max /CheckIntegrity /Verify
if errorlevel 1 ( echo. & echo  The capture did not finish. The messages above say why ^(log: X:\Windows\Logs\DISM\dism.log^). & goto end )
echo.
echo  Done: !WIM!
dism /Get-WimInfo /WimFile:""!WIM!""
:end
echo.
pause
";
            string split = head +
@"title Elixar capture kit - split
echo.
echo  Splits an image in %KIT%\Images into parts under 4 GB (.swm), for a FAT32 USB drive or Windows Setup media.
echo.
" + askName +
@"set ""WIM=%KIT%\Images\!NAME!.wim""
if not exist ""!WIM!"" ( echo  !WIM! wasn't found. & goto name )
dism /Split-Image /ImageFile:""!WIM!"" /SWMFile:""%KIT%\Images\!NAME!.swm"" /FileSize:3800
if errorlevel 1 ( echo  The split did not finish. & goto end )
echo.
echo  Done: %KIT%\Images\!NAME!*.swm
:end
echo.
pause
";
            string readme =
@"ELIXAR CAPTURE KIT
==================

1. On the PC you prepared: Elixar > Tools > Sysprep. Fix what the checklist shows,
   then run Sysprep (OOBE, Generalize, Shutdown). The PC shuts down.
2. Copy this kit to a USB drive formatted NTFS or exFAT (FAT32 can't hold files
   over 4 GB). Leave room for the image (usually 10-30 GB).
3. Start the PC from Windows PE or from a Windows installation USB. Don't start
   Windows itself: it would undo Sysprep's preparation.
   On the Windows installation screen press Shift+F10 for a command prompt.
4. Go to the USB drive (for example: E:) and run Capture-Image.cmd.
   It finds the Windows partition, asks for a name and captures it to
   \Images\<name>.wim with DISM (compressed, verified).
5. For a FAT32 USB drive or Windows installation media, run Split-Image.cmd
   to make parts under 4 GB (.swm).

To put the image on a PC (from Windows PE), after preparing its partitions:
   dism /Apply-Image /ImageFile:E:\Images\<name>.wim /Index:1 /ApplyDir:W:\
   bcdboot W:\Windows

Elixar's image passport (Tools > Sysprep > Image passport) lists what is in
the image: keep it next to the .wim.
";
            Func<string, string> crlf = s => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return new Dictionary<string, string> { { "Capture-Image.cmd", crlf(capture) }, { "Split-Image.cmd", crlf(split) }, { "README.txt", crlf(readme) } };
        }

        // writes the kit into the folder you chose (the USB drive); files of the same name there are replaced, links are refused
        public static string WriteKit(string folder)
        {
            try
            {
                if (Links.IsLink(folder)) return "That folder is a link.";
                foreach (var kv in CaptureKit())
                {
                    string f = Path.Combine(folder, kv.Key);
                    if (File.Exists(f) && Links.IsLink(f)) return f + " is a link.";
                    File.WriteAllText(f, kv.Value, new UTF8Encoding(false));
                }
                return null;
            }
            catch (Exception e) { return e.Message; }
        }
    }

    // ======================= reading Chromium's password database without SQLite (counts rows only) =======================
    public class MiniSqlite
    {
        readonly byte[] db; readonly int page, usable, enc;
        public MiniSqlite(byte[] data)
        {
            if (data == null || data.Length < 512 || Encoding.ASCII.GetString(data, 0, 15) != "SQLite format 3") throw new InvalidDataException("not SQLite");
            db = data;
            int ps = (data[16] << 8) | data[17]; page = ps == 1 ? 65536 : ps;
            if (page < 512 || (page & (page - 1)) != 0) throw new InvalidDataException("page size");
            usable = page - data[20];
            enc = (data[56] << 24) | (data[57] << 16) | (data[58] << 8) | data[59];
            if (enc < 0 || enc > 3) throw new InvalidDataException("encoding");
        }
        int Off(int pgno) { return (pgno - 1) * page; }
        int U16(int o) { return (db[o] << 8) | db[o + 1]; }
        int U32(int o) { return (db[o] << 24) | (db[o + 1] << 16) | (db[o + 2] << 8) | db[o + 3]; }

        public static long Varint(byte[] b, int o, out int len)
        {
            long v = 0;
            for (int i = 0; i < 9; i++)
            {
                if (o + i >= b.Length) throw new InvalidDataException("varint");
                if (i == 8) { v = (v << 8) | b[o + i]; len = 9; return v; }
                v = (v << 7) | (uint)(b[o + i] & 0x7f);
                if ((b[o + i] & 0x80) == 0) { len = i + 1; return v; }
            }
            len = 9; return v;
        }

        // every table row (its record bytes) under a root page; guards against loops and damaged files
        public void Walk(int root, Action<byte[]> row)
        {
            var seen = new HashSet<int>(); int rows = 0;
            Action<int, int> visit = null;
            visit = (pg, depth) =>
            {
                if (depth > 30 || pg < 1 || (long)Off(pg) + page > db.Length || !seen.Add(pg)) throw new InvalidDataException("tree");
                int h = Off(pg) + (pg == 1 ? 100 : 0);
                int type = db[h], cells = U16(h + 3);
                if (cells > page) throw new InvalidDataException("cells");
                if (type == 0x05)
                {
                    for (int i = 0; i < cells; i++) visit(U32(Off(pg) + U16(h + 12 + i * 2)), depth + 1);
                    visit(U32(h + 8), depth + 1);
                }
                else if (type == 0x0d)
                {
                    for (int i = 0; i < cells; i++)
                    {
                        if (++rows > 2000000) throw new InvalidDataException("rows");
                        row(Payload(Off(pg) + U16(h + 8 + i * 2)));
                    }
                }
                else throw new InvalidDataException("page type");
            };
            visit(root, 0);
        }

        // a table-leaf cell's payload, following overflow pages
        byte[] Payload(int cell)
        {
            int l1, l2;
            long size = Varint(db, cell, out l1);
            Varint(db, cell + l1, out l2);
            int start = cell + l1 + l2;
            if (size < 0 || size > 16 << 20) throw new InvalidDataException("payload");
            int x = usable - 35, local;
            if (size <= x) local = (int)size;
            else
            {
                int m = ((usable - 12) * 32 / 255) - 23, k = m + (int)((size - m) % (usable - 4));
                local = k <= x ? k : m;
            }
            var outp = new byte[size];
            Array.Copy(db, start, outp, 0, local);
            int done = local, next = local < size ? U32(start + local) : 0, guard = 0;
            while (done < size)
            {
                if (next < 1 || (long)Off(next) + page > db.Length || ++guard > 100000) throw new InvalidDataException("overflow");
                int o = Off(next), n = (int)Math.Min(usable - 4, size - done);
                Array.Copy(db, o + 4, outp, done, n);
                done += n; next = U32(o);
            }
            return outp;
        }

        public static List<long> SerialTypes(byte[] rec)
        {
            int l; long hsize = Varint(rec, 0, out l);
            var t = new List<long>(); int o = l;
            while (o < hsize && o < rec.Length) { int n; t.Add(Varint(rec, o, out n)); o += n; }
            return t;
        }
        public static long ValueLength(long st)
        {
            if (st >= 12) return (st - (st % 2 == 0 ? 12 : 13)) / 2;
            return st == 1 ? 1 : st == 2 ? 2 : st == 3 ? 3 : st == 4 ? 4 : st == 5 ? 6 : st == 6 || st == 7 ? 8 : 0;
        }

        // the values of one record (numbers and text only; blobs as their length)
        List<object> Values(byte[] rec)
        {
            int l; long hsize = Varint(rec, 0, out l);
            var types = SerialTypes(rec); var vals = new List<object>(); int o = (int)hsize;
            foreach (var st in types)
            {
                int len = (int)ValueLength(st);
                if (o + len > rec.Length) throw new InvalidDataException("record");
                if (st >= 13 && st % 2 == 1) vals.Add(TextOf(rec, o, len));
                else if (st >= 1 && st <= 6) { long v = (rec[o] & 0x80) != 0 ? -1 : 0; for (int i = 0; i < len; i++) v = (v << 8) | rec[o + i]; vals.Add(v); }
                else if (st == 8) vals.Add(0L); else if (st == 9) vals.Add(1L); else vals.Add(null);
                o += len;
            }
            return vals;
        }
        string TextOf(byte[] b, int o, int n) { return (enc == 2 ? Encoding.Unicode : enc == 3 ? Encoding.BigEndianUnicode : Encoding.UTF8).GetString(b, o, n); }

        // sqlite_master: the root page and CREATE statement of a table
        public int FindTable(string name, out string sql)
        {
            sql = null; int root = 0; string s = null;
            Walk(1, rec =>
            {
                var v = Values(rec);
                if (v.Count >= 5 && "table".Equals(v[0] as string) && name.Equals(v[1] as string, StringComparison.OrdinalIgnoreCase) && v[3] is long)
                { root = (int)(long)v[3]; s = v[4] as string; }
            });
            sql = s; return root;
        }

        // the position of a column in "CREATE TABLE x (a ..., b ..., UNIQUE (...))"
        public static int ColumnIndex(string createSql, string column)
        {
            int open = createSql.IndexOf('('), close = createSql.LastIndexOf(')');
            if (open < 0 || close <= open) return -1;
            string body = createSql.Substring(open + 1, close - open - 1);
            var parts = new List<string>(); int depth = 0, startAt = 0;
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] == '(') depth++; else if (body[i] == ')') depth--;
                else if (body[i] == ',' && depth == 0) { parts.Add(body.Substring(startAt, i - startAt)); startAt = i + 1; }
            }
            parts.Add(body.Substring(startAt));
            int idx = 0;
            foreach (var p in parts)
            {
                string t = p.Trim();
                string first = t.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                string up = first.ToUpperInvariant();
                if (up == "UNIQUE" || up == "PRIMARY" || up == "CONSTRAINT" || up == "CHECK" || up == "FOREIGN") continue;
                if (first.Trim('"', '`', '[', ']').Equals(column, StringComparison.OrdinalIgnoreCase)) return idx;
                idx++;
            }
            return -1;
        }
    }
}
