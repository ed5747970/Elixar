// Installing Cure Agent and Cure Server (Elixar for Business). Both must run from Elixar's admin-only folder in Program Files,
// so a Windows service never starts a file other programs can change.
//   CureAgent.exe  --install --server https://cure.contoso.local:8443 --token <token> [--thumbprint <sha1>] [--interval 15]
//   CureServer.exe --install [--port 8443] [--cert <thumbprint>] [--group "CONTOSO\Cure Console Users"]
// The server makes a certificate itself when none is given (5 years, key can't be exported), and prints the settings
// for the PCs (also saved as a .reg file in its admin-only data folder, because it holds the enrollment token).

#if BUSINESS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class CureSetup
    {
        public static Func<string, string, int> RunOverride;   // self-test: (exe, args) -> exit code; nothing is changed
        public static List<string> Ran = new List<string>();

        static int Run(string exe, string args, out string output)
        {
            Ran.Add(exe + " " + args);
            if (RunOverride != null) { output = ""; return RunOverride(exe, args); }
            var psi = new ProcessStartInfo(Win.SystemExe(exe), args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                                                                        WorkingDirectory = Path.GetDirectoryName(Win.SystemExe(exe)) };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } output = "timed out"; return -1; }
                output = (o.Result + " " + e.Result).Trim();
                return p.ExitCode;
            }
        }
        static int Run(string exe, string args) { string o; return Run(exe, args, out o); }

        // a settings key only administrators and SYSTEM (and the service that needs it) can read: it can hold the enrollment token
        static void LockKey(RegistryKey hklm, string path, string reader)
        {
            using (var k = hklm.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey | RegistryRights.ReadPermissions))
            {
                if (k == null) return;
                var rs = new RegistrySecurity();
                rs.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
                    rs.AddAccessRule(new RegistryAccessRule(new SecurityIdentifier(sid, null), RegistryRights.FullControl, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                if (reader != null) rs.AddAccessRule(new RegistryAccessRule(new NTAccount(reader), RegistryRights.ReadKey, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                k.SetAccessControl(rs);
            }
        }

        // "quoted" for sc.exe / netsh (no quote can get inside)
        public static string Q(string s) { if ((s ?? "").IndexOf('"') >= 0) throw new ArgumentException("A quote isn't allowed here."); return "\"" + s + "\""; }
        // sc.exe binPath= "\"C:\Program Files\Elixar\X.exe\" --service"
        static string BinPath(string exe) { if ((exe ?? "").IndexOf('"') >= 0) throw new ArgumentException("A quote isn't allowed here."); return "\"\\\"" + exe + "\\\" --service\""; }

        // the exe must sit in Program Files\Elixar (admin-only, no link in the way)
        public static string CheckPlace(string exe)
        {
            if (RunOverride != null) return null;
            if (!Win.IsWindows) return "Cure runs on Windows.";
            if (!Sys.IsAdmin()) return "Run this from an administrator command prompt.";
            string dir = Path.GetDirectoryName(Path.GetFullPath(exe));
            if (!string.Equals(dir.TrimEnd('\\'), Install.Dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return "Install Elixar for Business first (the MSI), then run this from " + Install.Dir + ".";
            if (!SafeDelete.RealPathMatches(exe)) return "A link is in the way of " + exe + ", so no service was made.";
            string why = Locked.LockProblem(dir);
            if (why != null) return "Elixar's folder isn't protected (only administrators may change it): " + why + ". No service was made.";
            return null;
        }

        // ---------- updates ----------
        // An update of Elixar for Business brings new CureAgent.exe / CureServer.exe. They are copied into Program Files only
        // from Elixar's own update folder (admin-only, its zip checked against the signed fingerprint) - never from Downloads,
        // where another program could swap a file that then runs as a Windows service. Running services restart on the new files.
        public static void UpdateFiles(string runningExe)
        {
            try
            {
                string from = Path.GetDirectoryName(Path.GetFullPath(runningExe)), updates = Path.Combine(Paths.Data, "update");
                if (!from.StartsWith(updates + "\\", StringComparison.OrdinalIgnoreCase) || !Locked.IsLocked(updates)) return;
                foreach (var name in new[] { "CureAgent.exe", "CureServer.exe" })
                {
                    string src = Path.Combine(from, name), dst = Path.Combine(Install.Dir, name);
                    if (!File.Exists(src) || !SafeDelete.RealPathMatches(src)) continue;
                    string svc = name == "CureAgent.exe" ? CureAgent.ServiceName : CureServer.ServiceName;
                    bool wasThere = File.Exists(dst);
                    if (wasThere) { try { File.Delete(dst); } catch { File.Move(dst, dst + ".old-" + DateTime.Now.Ticks); } }
                    SafeFile.Copy(src, dst);
                    string o;
                    if (Run("sc.exe", "query " + svc, out o) == 0) { Run("sc.exe", "stop " + svc); System.Threading.Thread.Sleep(3000); Run("sc.exe", "start " + svc); }
                    ChangeLog.Add("Apps", wasThere ? "Updated " + name : "Added " + name, dst, "", "", "From the checked update", false);
                }
            }
            catch { }
        }

        // ---------- Cure Agent ----------
        public static string InstallAgent(string exe, CureConfig c)
        {
            string err = CheckPlace(exe); if (err != null) return err;
            // no server given: the service waits for Group Policy (HKLM\SOFTWARE\Policies\Elixar\Cure)
            if (c.Server.Length > 0 && !c.Ok) return c.Error;
            if (RunOverride == null && c.Server.Length > 0)
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.CreateSubKey(CureAgent.OwnKey))
                {
                    k.SetValue("Server", c.Server); k.SetValue("Thumbprint", c.Thumbprint); k.SetValue("Interval", c.Interval, RegistryValueKind.DWord);
                    if (c.Token.Length > 0) k.SetValue("EnrollToken", c.Token);
                    k.Close(); LockKey(b, CureAgent.OwnKey, null);
                }
            Run("sc.exe", "stop " + CureAgent.ServiceName); Run("sc.exe", "delete " + CureAgent.ServiceName);
            int code = Run("sc.exe", "create " + CureAgent.ServiceName + " binPath= " + BinPath(exe) + " start= delayed-auto obj= LocalSystem DisplayName= " + Q("Cure Agent (Elixar for Business)"));
            if (code != 0) return "Windows didn't make the Cure Agent service (code " + code + ").";
            Run("sc.exe", "description " + CureAgent.ServiceName + " " + Q("Sends this PC's disk and health status to the company's Cure Server. Reports only: it never runs anything it's sent."));
            Run("sc.exe", "failure " + CureAgent.ServiceName + " reset= 86400 actions= restart/60000/restart/300000//");
            Run("sc.exe", "start " + CureAgent.ServiceName);
            using (ChangeLog.In("Cure Agent")) ChangeLog.Add("Settings", "Installed Cure Agent", c.Server, "", "service CureAgent (LocalSystem)", "Reports only: the server can't start anything on this PC", false);
            return null;
        }

        public static void UninstallAgent()
        {
            Run("sc.exe", "stop " + CureAgent.ServiceName); Run("sc.exe", "delete " + CureAgent.ServiceName);
            if (RunOverride == null)
            {
                try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) b.DeleteSubKeyTree(CureAgent.OwnKey, false); } catch { }
                CureAgent.ForgetDevice();
            }
            using (ChangeLog.In("Cure Agent")) ChangeLog.Add("Settings", "Removed Cure Agent", "service CureAgent", "", "", "", false);
        }

        // ---------- Cure Server ----------
        const string AppId = "{6d9b1c3e-2f4a-4e8b-9c1d-5a7e3b2f8c10}";
        const string ServiceAccount = "NT SERVICE\\" + CureServer.ServiceName;
        const string FirewallRule = "Cure Server (Elixar for Business)";

        public class ServerResult { public string Error, Token, Thumbprint, Url, RegFile; }

        public static ServerResult InstallServer(string exe, int port, string cert, string group)
        {
            var res = new ServerResult();
            res.Error = CheckPlace(exe); if (res.Error != null) return res;
            if (port < 1 || port > 65535) { res.Error = "Not a port number."; return res; }
            cert = (cert ?? "").Replace(" ", "").Replace("\u200e", "").ToLowerInvariant();   // Windows shows it with spaces (and sometimes a hidden mark)
            if (cert.Length != 0 && (cert.Length != 40 || !cert.All(Uri.IsHexDigit))) { res.Error = "Give the certificate's thumbprint (40 letters and digits, from the computer's certificate store)."; return res; }
            if ((group ?? "").IndexOf('"') >= 0) { res.Error = "Not a group name."; return res; }
            string host = Environment.MachineName, fqdn = host;
            try { fqdn = System.Net.Dns.GetHostEntry("").HostName; } catch { }
            bool ownCert = false;
            if (cert.Length == 0)
            {
                string o;
                string ps = "$c = New-SelfSignedCertificate -DnsName " + Sys.PsQuote(fqdn) + "," + Sys.PsQuote(host) + " -CertStoreLocation 'Cert:\\LocalMachine\\My' -NotAfter (Get-Date).AddYears(5) " +
                            "-KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy NonExportable -FriendlyName 'Cure Server (Elixar for Business)'; $c.Thumbprint";
                if (RunOverride != null) { Ran.Add("powershell New-SelfSignedCertificate"); o = "0123456789abcdef0123456789abcdef01234567"; }
                else o = StoreData.RunPs(ps, 120000);
                cert = new string((o ?? "").Trim().Split('\n').Last().Trim().Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
                if (cert.Length != 40) { res.Error = "Windows didn't make a certificate: " + o; return res; }
                ownCert = true;
            }
            string token = CureServer.NewToken();
            if (RunOverride == null)
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.CreateSubKey(CureServer.SettingsKey))
                {
                    k.SetValue("Port", port, RegistryValueKind.DWord); k.SetValue("Group", group ?? ""); k.SetValue("TokenHash", CureServer.HashToken(token));
                    k.SetValue("Certificate", cert); k.SetValue("OwnCertificate", ownCert ? 1 : 0, RegistryValueKind.DWord);
                    k.Close();
                }
            string ipport = "0.0.0.0:" + port.ToString(CultureInfo.InvariantCulture), url = "https://+:" + port.ToString(CultureInfo.InvariantCulture) + "/";
            Run("sc.exe", "stop " + CureServer.ServiceName); Run("sc.exe", "delete " + CureServer.ServiceName);
            int code = Run("sc.exe", "create " + CureServer.ServiceName + " binPath= " + BinPath(exe) + " start= auto obj= " + Q(ServiceAccount) + " DisplayName= " + Q("Cure Server (Elixar for Business)"));
            if (code != 0) { res.Error = "Windows didn't make the Cure Server service (code " + code + ")."; return res; }
            if (RunOverride == null) using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) LockKey(b, CureServer.SettingsKey, ServiceAccount);
            Run("sc.exe", "description " + CureServer.ServiceName + " " + Q("Receives Cure Agent reports and shows them in the Cure Console. Reports only."));
            Run("sc.exe", "failure " + CureServer.ServiceName + " reset= 86400 actions= restart/60000/restart/300000//");
            Run("netsh.exe", "http delete sslcert ipport=" + ipport);
            if (Run("netsh.exe", "http add sslcert ipport=" + ipport + " certhash=" + cert + " appid=" + AppId) != 0) { res.Error = "Windows didn't bind the certificate to port " + port + "."; return res; }
            Run("netsh.exe", "http delete urlacl url=" + url);
            if (Run("netsh.exe", "http add urlacl url=" + url + " user=" + Q(ServiceAccount)) != 0) { res.Error = "Windows didn't reserve the address for the service."; return res; }
            Run("netsh.exe", "advfirewall firewall delete rule name=" + Q(FirewallRule));
            Run("netsh.exe", "advfirewall firewall add rule name=" + Q(FirewallRule) + " dir=in action=allow protocol=TCP localport=" + port.ToString(CultureInfo.InvariantCulture) + " profile=domain");
            // the data folder: administrators, SYSTEM and the service only
            string data = CureServer.DefaultDataDir;
            if (RunOverride == null)
            {
                Directory.CreateDirectory(data);
                if (!Locked.Lock(data, false)) { res.Error = "Could not protect the Cure Server's data folder."; return res; }
                var ds = Directory.GetAccessControl(data);
                ds.AddAccessRule(new FileSystemAccessRule(new NTAccount(ServiceAccount), FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(data, ds);
            }
            Run("sc.exe", "start " + CureServer.ServiceName);
            res.Token = token; res.Thumbprint = cert; res.Url = "https://" + fqdn + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";
            res.RegFile = Path.Combine(data, "Cure Agent settings.reg");
            if (RunOverride == null) SafeFile.Save(res.RegFile, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(AgentReg(res.Url, token, cert))).ToArray());
            using (ChangeLog.In("Cure Server")) ChangeLog.Add("Settings", "Installed Cure Server", res.Url, "", "service CureServer (" + ServiceAccount + "), certificate " + cert.Substring(0, 8) + "...", "Reports only", false);
            return res;
        }

        // the settings for every PC (Group Policy Preferences / Intune / regedit)
        public static string AgentReg(string url, string token, string thumbprint)
        {
            Func<string, string> e = s => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "Windows Registry Editor Version 5.00\r\n\r\n; Cure Agent settings for every PC. Keep this file private: it holds the enrollment token.\r\n" +
                   "[HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Elixar\\Cure]\r\n\"Server\"=\"" + e(url) + "\"\r\n\"EnrollToken\"=\"" + e(token) + "\"\r\n\"Thumbprint\"=\"" + e(thumbprint) + "\"\r\n\"Interval\"=dword:0000000f\r\n";
        }

        public static void StopServer() { Run("sc.exe", "stop " + CureServer.ServiceName); System.Threading.Thread.Sleep(RunOverride == null ? 3000 : 0); }
        public static void StartServer() { Run("sc.exe", "start " + CureServer.ServiceName); }

        public static string NewServerToken()
        {
            string token = CureServer.NewToken();
            if (RunOverride == null)
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.OpenSubKey(CureServer.SettingsKey, true))
                {
                    if (k == null) return null;
                    k.SetValue("TokenHash", CureServer.HashToken(token));
                }
            Run("sc.exe", "stop " + CureServer.ServiceName); Run("sc.exe", "start " + CureServer.ServiceName);
            using (ChangeLog.In("Cure Server")) ChangeLog.Add("Settings", "New Cure enrollment token", "Cure Server", "", "", "PCs already enrolled keep reporting; new PCs need the new token", false);
            return token;
        }

        public static void UninstallServer(bool purge)
        {
            var s = RunOverride == null ? CureServer.FromRegistry() : new CureServer();
            string cert = ""; bool own = false;
            if (RunOverride == null)
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.OpenSubKey(CureServer.SettingsKey))
                    if (k != null) { cert = k.GetValue("Certificate") as string ?? ""; own = (k.GetValue("OwnCertificate") as int? ?? 0) == 1; }
            string p = s.Port.ToString(CultureInfo.InvariantCulture);
            Run("sc.exe", "stop " + CureServer.ServiceName); Run("sc.exe", "delete " + CureServer.ServiceName);
            Run("netsh.exe", "http delete sslcert ipport=0.0.0.0:" + p);
            Run("netsh.exe", "http delete urlacl url=https://+:" + p + "/");
            Run("netsh.exe", "advfirewall firewall delete rule name=" + Q(FirewallRule));
            if (RunOverride == null)
            {
                if (own && cert.Length == 40 && cert.All(Uri.IsHexDigit)) StoreData.RunPs("Remove-Item -LiteralPath " + Sys.PsQuote("Cert:\\LocalMachine\\My\\" + cert) + " -ErrorAction SilentlyContinue", 60000);
                try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) b.DeleteSubKeyTree(CureServer.SettingsKey, false); } catch { }
                if (purge) try { if (Directory.Exists(CureServer.DefaultDataDir)) SafeDelete.Delete(CureServer.DefaultDataDir); } catch { }
            }
            using (ChangeLog.In("Cure Server")) ChangeLog.Add("Settings", "Removed Cure Server", "service CureServer", "", purge ? "data deleted" : "data kept", "", false);
        }
    }
}
#endif
