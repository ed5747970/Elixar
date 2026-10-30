// Cure Agent (Elixar for Business): a Windows service (CureAgent.exe, next to Elixar.exe in Program Files) that sends this
// PC's status to the company's Cure Server. See CureProtocol.cs for how it proves who it is and what it never sends.
// Settings, the first found wins:
//   HKLM\SOFTWARE\Policies\Elixar\Cure   (Group Policy / Intune: Server, EnrollToken, Thumbprint, Interval)
//   HKLM\SOFTWARE\Elixar\Cure            (CureAgent.exe --install)
// Stage 1 is reports only: the server's answer can only change how often the agent reports.

#if BUSINESS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class CureConfig
    {
        public string Server = "", Token = "", Thumbprint = "", From = "";
        public int Interval = CureProtocol.DefaultInterval;
        public bool Ok { get { return Error == null; } }
        public string Error
        {
            get
            {
                if (Server.Length == 0) return "No Cure Server is set (Group Policy: HKLM\\SOFTWARE\\Policies\\Elixar\\Cure, value Server).";
                Uri u;
                if (!Uri.TryCreate(Server, UriKind.Absolute, out u) || u.UserInfo.Length > 0 || u.Query.Length > 0) return "The Cure Server address isn't a plain web address: " + Server;
                if (u.Scheme != "https" && !(CureAgent.AllowHttpLoopback && u.Scheme == "http" && u.IsLoopback)) return "The Cure Server address must start with https://";
                if (Thumbprint.Length > 0 && !(Thumbprint.Length == 40 || Thumbprint.Length == 64)) return "The certificate fingerprint must be 40 (SHA-1) or 64 (SHA-256) letters and digits.";
                return null;
            }
        }
        public string BaseUrl { get { return Server.TrimEnd('/') + "/"; } }
    }

    public static class CureAgent
    {
        public const string ServiceName = "CureAgent", PolicyKey = @"SOFTWARE\Policies\Elixar\Cure", OwnKey = @"SOFTWARE\Elixar\Cure";
        public static bool AllowHttpLoopback;                // self-test only: a plain http server on this PC
        public static Func<CureConfig> ConfigOverride;       // self-test
        public static Func<CureReport> CollectOverride;      // self-test
        public static string DeviceFileOverride;             // self-test

        // ---------- settings ----------
        public static CureConfig Config()
        {
            if (ConfigOverride != null) return ConfigOverride();
            var c = new CureConfig();
            if (!Win.IsWindows) return c;
            foreach (var path in new[] { PolicyKey, OwnKey })
                try
                {
                    using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var k = b.OpenSubKey(path))
                    {
                        if (k == null || string.IsNullOrWhiteSpace(k.GetValue("Server") as string)) continue;
                        c.Server = ((string)k.GetValue("Server")).Trim();
                        c.Token = ((k.GetValue("EnrollToken") as string) ?? "").Trim();
                        c.Thumbprint = new string(((k.GetValue("Thumbprint") as string) ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
                        int iv; object v = k.GetValue("Interval");
                        if (v is int) c.Interval = (int)v; else if (v is string && int.TryParse((string)v, out iv)) c.Interval = iv;
                        c.Interval = Math.Max(CureProtocol.MinInterval, Math.Min(CureProtocol.MaxInterval, c.Interval));
                        c.From = path == PolicyKey ? "Group Policy" : "this PC";
                        return c;
                    }
                }
                catch { }
            return c;
        }

        // ---------- this PC's id and key (machine-encrypted, admins and SYSTEM only) ----------
        static string DeviceFile { get { return DeviceFileOverride ?? Path.Combine(Path.Combine(Paths.Data, "cure"), "device.dat"); } }
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Elixar Cure Agent device key v1");

        public class Device { public string Id = "", Server = ""; public byte[] Key; }

        public static Device LoadDevice()
        {
            try
            {
                if (!File.Exists(DeviceFile)) return null;
                byte[] raw = File.ReadAllBytes(DeviceFile);
                byte[] plain = Win.IsWindows ? ProtectedData.Unprotect(raw, Entropy, DataProtectionScope.LocalMachine) : raw;
                var p = Encoding.UTF8.GetString(plain).Split('\n');
                if (p.Length < 3) return null;
                return new Device { Id = p[0], Server = p[1], Key = Convert.FromBase64String(p[2]) };
            }
            catch { return null; }
        }

        static void SaveDevice(Device d)
        {
            string dir = Path.GetDirectoryName(DeviceFile);
            Directory.CreateDirectory(dir);
            if (DeviceFileOverride == null && Win.IsWindows && !Locked.Lock(dir, false)) throw new IOException("Could not protect Elixar's Cure folder.");
            byte[] plain = Encoding.UTF8.GetBytes(d.Id + "\n" + d.Server + "\n" + Convert.ToBase64String(d.Key));
            SafeFile.Save(DeviceFile, Win.IsWindows ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine) : plain);
        }

        public static void ForgetDevice() { try { if (File.Exists(DeviceFile)) File.Delete(DeviceFile); } catch { } }

        // ---------- what this PC reports ----------
        static List<CureItem> measured; static DateTime measuredAt = DateTime.MinValue;
        static DiskReport disks; static DateTime disksAt = DateTime.MinValue;
        static int appUpdates = -1; static DateTime updatesAt = DateTime.MinValue;

        public static CureReport Collect()
        {
            if (CollectOverride != null) return CollectOverride();
            var r = new CureReport { Agent = CureProtocol.Version, Edition = Edition.Id, Time = DateTime.Now.ToString("s", CultureInfo.InvariantCulture) };
            var errors = new List<string>();
            Action<string, Action> tryIt = (what, a) => { try { a(); } catch (Exception e) { errors.Add(what + ": " + e.Message); } };
            tryIt("status", () =>
            {
                var s = Fleet.Collect(false);
                r.Computer = s.Computer; r.Elixar = s.Elixar; r.Total = s.Total; r.Free = s.Free; r.Saved = s.Saved; r.SavedThisMonth = s.SavedThisMonth;
                r.UndoBytes = s.UndoBytes; r.LastClean = s.LastClean; r.LastCleanBytes = s.LastCleanBytes; r.PolicyMode = s.PolicyMode; r.PolicyFingerprint = s.PolicyFingerprint;
            });
            if (string.IsNullOrEmpty(r.Computer)) r.Computer = Environment.MachineName;
            tryIt("windows", () => r.Windows = OsName());
            tryIt("boot", () => r.BootTime = DateTime.Now.AddMilliseconds(-UptimeMs()).ToString("s", CultureInfo.InvariantCulture));
            tryIt("restart", () => r.PendingReboot = RestartPending());
            tryIt("company", () => { r.Company = Policy.Company() ?? ""; r.Ou = Ou(); r.Model = Model(); });
            tryIt("drives", () => r.Drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                                                       .Select(d => new CureDrive { Letter = d.Name.Substring(0, 1), Total = d.TotalSize, Free = d.AvailableFreeSpace }).ToArray());
            // the slow ones are measured in the background now and then; every report sends what is ready, so a busy PC
            // (or a step that takes long as SYSTEM) never stops the reports - it says what is still being measured
            var slow = StartSlow();
            if (slow != null) slow.Join(SlowWaitMs);
            lock (slowGate)
            {
                if (measured != null)
                {
                    Func<string, long> sum = t => measured.Where(i => i.Tier == t && i.Bytes > 0).Sum(i => i.Bytes);
                    r.CleanSafe = sum("safe"); r.CleanReview = sum("review"); r.CleanCaution = sum("caution");
                    r.Items = measured.Where(i => i.Bytes > 0).OrderByDescending(i => i.Bytes).Take(80).ToArray();
                    r.MeasuredAt = measuredAt.ToString("s", CultureInfo.InvariantCulture);
                }
                if (places != null)
                {
                    Func<string, long> sum = t => places.Where(x => x.Tier == t && x.Bytes > 0).Sum(x => x.Bytes);
                    r.FillSafe = sum("safe"); r.FillReview = sum("review"); r.FillCaution = sum("caution"); r.FillCritical = sum("critical");
                    r.Places = places.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).Take(40).ToArray();
                    r.PlacesAt = placesAt.ToString("s", CultureInfo.InvariantCulture);
                }
                if (apps != null) r.Apps = apps.ToArray();
                if (disks != null)
                {
                    r.DiskLevel = disks.Worst.ToString();
                    r.Disks = disks.Disks.Select(d => new CureDisk { Name = d.Name, Kind = d.Kind, Level = d.Level.ToString(), Letters = d.Letters, Wear = d.Wear, Temp = d.Temp, Reasons = d.Reasons.ToArray() }).ToArray();
                }
                r.AppUpdates = appUpdates;
                errors.AddRange(slowErrors);
                if (slowNow != null) errors.Add("still measuring: " + slowNow + " (" + (int)(DateTime.Now - slowSince).TotalMinutes + " min)");
            }
            tryIt("weekly clean", () => r.WeeklyClean = Policy.Get("sched.clean.day") != "off");
            r.Accounts = Profiles().Count;
            tryIt("startup", () =>
            {
                var on = StartupApps.Read(false).Where(e => e.Enabled && !e.Missing).ToList();
                r.StartupCount = on.Count; r.Startup = on.Select(e => e.Name).Distinct().Take(60).ToArray();
            });
            r.Locks = Policy.CompanyBlocks.Where(Policy.ByCompany).ToArray();
            r.EventLog = Policy.Get("eventlog") != "off";
            r.Errors = errors.Take(10).Select(CureProtocol.NoPaths).ToArray();
            return r;
        }

        static List<CurePlace> places; static DateTime placesAt = DateTime.MinValue;
        static List<CureApp> apps; static DateTime appsAt = DateTime.MinValue;

        // ---------- the slow measurements (one background thread) ----------
        public static int SlowWaitMs = 60000;   // a report waits this long for them; --show and --send wait longer
        static readonly object slowGate = new object();
        static Thread slowWorker;
        static string slowNow; static DateTime slowSince;
        static List<string> slowErrors = new List<string>();

        static bool SlowDue()
        {
            var n = DateTime.Now;
            return measured == null || n - measuredAt > TimeSpan.FromHours(1) || places == null || n - placesAt > TimeSpan.FromHours(6) ||
                   apps == null || n - appsAt > TimeSpan.FromHours(24) || disks == null || n - disksAt > TimeSpan.FromHours(6) || n - updatesAt > TimeSpan.FromHours(24);
        }

        // starts the measuring when something is due and it isn't already running; returns the running thread (or null)
        static Thread StartSlow()
        {
            lock (slowGate)
            {
                if (slowWorker != null && slowWorker.IsAlive) return slowWorker;
                if (!SlowDue()) return null;
                slowNow = "starting"; slowSince = DateTime.Now;   // set before the thread runs, so a report right now already says so
                slowWorker = new Thread(MeasureSlow) { IsBackground = true, Name = "Cure Agent measuring", Priority = ThreadPriority.BelowNormal };
                slowWorker.Start();
                return slowWorker;
            }
        }

        static void MeasureSlow()
        {
            var errs = new List<string>();
            Action<string, Func<bool>, Action> step = (what, due, a) =>
            {
                if (!due()) return;
                lock (slowGate) { slowNow = what; slowSince = DateTime.Now; }
                try { a(); } catch (Exception e) { errs.Add(what + ": " + e.Message); }
            };
            var now = DateTime.Now;
            step("disks", () => disks == null || now - disksAt > TimeSpan.FromHours(6), () => { var d = DiskHealth.Read(); lock (slowGate) { disks = d; disksAt = DateTime.Now; } });
            step("what can be cleaned", () => measured == null || now - measuredAt > TimeSpan.FromHours(1), () => { var m = MeasureAllAccounts(); lock (slowGate) { measured = m; measuredAt = DateTime.Now; } });
            step("apps", () => apps == null || now - appsAt > TimeSpan.FromHours(24), () => { var a = BigApps(); lock (slowGate) { apps = a; appsAt = DateTime.Now; } });
            step("what fills C:", () => places == null || now - placesAt > TimeSpan.FromHours(6), () => { var p = PlacesAllAccounts(); lock (slowGate) { places = p; placesAt = DateTime.Now; } });
            step("app updates", () => now - updatesAt > TimeSpan.FromHours(24), () =>
            {
                int n = -1; if (Policy.Allowed("apps.update") && WinGet.Available) { string e; var l = WinGet.List(out e); if (e == null) n = l.Count; }
                lock (slowGate) { appUpdates = n; updatesAt = DateTime.Now; }
            });
            lock (slowGate) { slowNow = null; slowErrors = errs; }
        }
        public static Func<List<string>> ProfilesOverride;   // self-test

        // the real people's profiles on this PC (not the service accounts)
        public static List<string> Profiles()
        {
            if (ProfilesOverride != null) return ProfilesOverride();
            var l = new List<string>();
            if (!Win.IsWindows) return l;
            try
            {
                string users = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)), "Users");
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList"))
                    if (k != null)
                        foreach (var sid in k.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-") && !s.EndsWith(".bak")))
                            using (var p = k.OpenSubKey(sid))
                            {
                                string path = p == null ? null : p.GetValue("ProfileImagePath") as string;
                                if (string.IsNullOrEmpty(path)) continue;
                                path = Environment.ExpandEnvironmentVariables(path).TrimEnd('\\');
                                if (path.StartsWith(users + "\\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(path) && SafeDelete.NoLinks(path)) l.Add(path);
                            }
            }
            catch { }
            return l;
        }

        // Each account's own items (temp files, browser caches...) are measured in that account and added up;
        // the PC's items (Windows Update, Recycle Bin...) once. Only sizes leave the PC, never whose they are.
        public static List<CureItem> MeasureAllAccounts()
        {
            var total = new Dictionary<string, CureItem>();
            var profiles = Profiles();
            string keep = UserDirs.ProfileOverride;
            try
            {
                bool first = true;
                foreach (var prof in profiles.Count > 0 ? profiles.Cast<string>() : new string[] { null })
                {
                    UserDirs.ProfileOverride = prof;
                    foreach (var it in Catalog.Build().Where(i => !i.Privacy))
                    {
                        bool own = prof != null && (it.Where ?? "").IndexOf(prof, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!own && !first) continue;   // the PC's items: measured once
                        long b; try { b = it.Measure(); } catch { b = -1; }
                        CureItem c;
                        if (!total.TryGetValue(it.Name, out c)) total[it.Name] = c = new CureItem { Name = it.Name, Tier = it.Tier.ToString().ToLowerInvariant(), Auto = it.DefaultOn, Bytes = 0 };
                        if (b > 0) c.Bytes += b;
                    }
                    first = false;
                }
            }
            finally { UserDirs.ProfileOverride = keep; }
            return total.Values.ToList();
        }

        public static List<CurePlace> PlacesAllAccounts()
        {
            var total = new Dictionary<string, CurePlace>();
            var profiles = Profiles();
            string keep = UserDirs.ProfileOverride;
            var t = System.Threading.Thread.CurrentThread; var pr = t.Priority; t.Priority = ThreadPriority.Lowest;   // walking C: is slow: never in the way
            try
            {
                bool first = true;
                foreach (var prof in profiles.Count > 0 ? profiles.Cast<string>() : new string[] { null })
                {
                    UserDirs.ProfileOverride = prof;
                    var locs = DashData.Build();
                    foreach (var l in locs) DashData.Measure(l);
                    DashData.ComputeOwn(locs);
                    foreach (var l in locs.Where(x => x.Bytes.HasValue))
                    {
                        bool own = prof != null && l.Path.StartsWith(prof + "\\", StringComparison.OrdinalIgnoreCase);
                        if (!own && !first) continue;
                        CurePlace c;
                        if (!total.TryGetValue(l.Name, out c)) total[l.Name] = c = new CurePlace { Name = l.Name, Tier = l.Tier, Action = l.Action, Bytes = 0 };
                        c.Bytes += l.Own;
                    }
                    first = false;
                }
            }
            finally { UserDirs.ProfileOverride = keep; t.Priority = pr; }
            return total.Values.ToList();
        }

        // the biggest installed apps, with when they were last used (Windows' Prefetch)
        static List<CureApp> BigApps()
        {
            var list = AppsData.ReadInstalled();
            var pf = AppsData.ReadPrefetch();
            foreach (var a in list.OrderByDescending(x => x.ReportedBytes).Take(60)) { try { AppsData.FillDetails(a, pf); } catch { } }
            return list.Select(a => new { a, b = Math.Max(a.ReportedBytes, a.MeasuredBytes) }).Where(x => x.b > 0).OrderByDescending(x => x.b).Take(30)
                       .Select(x => new CureApp { Name = x.a.Name ?? "", Publisher = x.a.Publisher ?? "", Bytes = x.b,
                                                  LastUsed = x.a.LastUsed.HasValue ? x.a.LastUsed.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                                                  Installed = x.a.Installed.HasValue ? x.a.Installed.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "" }).ToList();
        }

        static string OsName()
        {
            using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (k == null) return Environment.OSVersion.VersionString;
                string name = k.GetValue("ProductName") as string ?? "Windows", disp = k.GetValue("DisplayVersion") as string ?? "", build = k.GetValue("CurrentBuild") as string ?? "";
                object ubr = k.GetValue("UBR");
                int b2; if (int.TryParse(build, out b2) && b2 >= 22000) name = name.Replace("Windows 10", "Windows 11");   // Windows 11 still says 10 here
                return (name + " " + disp + " (" + build + (ubr != null ? "." + ubr : "") + ")").Trim();
            }
        }

        static bool RestartPending()
        {
            using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var a = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            using (var c = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                return a != null || c != null;
        }

        static string Ou()
        {
            using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine"))
                return k == null ? "" : (k.GetValue("Distinguished-Name") as string ?? "");
        }

        static string Model()
        {
            using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = b.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                return k == null ? "" : ((k.GetValue("SystemManufacturer") as string ?? "") + " " + (k.GetValue("SystemProductName") as string ?? "")).Trim();
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        static long UptimeMs() { try { return (long)GetTickCount64(); } catch { return (uint)Environment.TickCount; } }

        // ---------- talking to the server ----------
        // only this server's certificate (when a fingerprint is set), else a certificate Windows trusts for that name
        public static bool CertOk(X509Certificate cert, SslPolicyErrors errors, string thumbprint)
        {
            if (cert == null) return false;
            if (thumbprint.Length == 0) return errors == SslPolicyErrors.None;
            var c2 = new X509Certificate2(cert);
            string sha1 = c2.Thumbprint.ToLowerInvariant(), sha256;
            using (var sha = SHA256.Create()) sha256 = CureProtocol.Hex(sha.ComputeHash(c2.RawData));
            return CureProtocol.Same(thumbprint, thumbprint.Length == 40 ? sha1 : sha256);
        }

        static string Post(CureConfig c, string path, byte[] body, Dictionary<string, string> headers, out int status)
        {
            Sys.SecureTls();
            var rq = (HttpWebRequest)WebRequest.Create(c.BaseUrl + path);
            rq.Method = "POST"; rq.ContentType = "application/json"; rq.UserAgent = "CureAgent/" + CureProtocol.Version;
            rq.Timeout = 30000; rq.ReadWriteTimeout = 30000; rq.AllowAutoRedirect = false;
            if (rq.RequestUri.IsLoopback) rq.Proxy = null;
            string tp = c.Thumbprint;
            rq.ServerCertificateValidationCallback = (s, cert, chain, err) => CertOk(cert, err, tp);
            foreach (var h in headers) rq.Headers[h.Key] = h.Value;
            rq.ContentLength = body.Length;
            using (var s = rq.GetRequestStream()) s.Write(body, 0, body.Length);
            try
            {
                using (var rs = (HttpWebResponse)rq.GetResponse()) { status = (int)rs.StatusCode; return ReadSmall(rs.GetResponseStream()); }
            }
            catch (WebException we)
            {
                var rs = we.Response as HttpWebResponse; if (rs == null) throw;
                status = (int)rs.StatusCode; using (rs) return ReadSmall(rs.GetResponseStream());
            }
        }

        static string ReadSmall(Stream s) { using (var ms = new MemoryStream()) { Updates.CopyLimited(s, ms, 8192); return Encoding.UTF8.GetString(ms.ToArray()); } }

        public class Result { public bool Sent, NoSettings; public string Error; public int NextMinutes; }

        // one report now (enrolls first when this PC has no id yet for this server)
        public static Result SendOnce()
        {
            var res = new Result();
            var c = Config();
            if (!c.Ok) { res.Error = c.Error; res.NoSettings = true; return res; }
            res.NextMinutes = c.Interval;
            try
            {
                var dev = LoadDevice();
                if (dev == null || dev.Server != c.BaseUrl)
                {
                    if (c.Token.Length == 0) { res.Error = "This PC isn't enrolled yet and no enrollment token is set (value EnrollToken)."; return res; }
                    var ask = CureProtocol.Json().Serialize(new Dictionary<string, string> { { "token", c.Token }, { "computer", Environment.MachineName } });
                    int st; string ans = Post(c, "api/v1/enroll", Encoding.UTF8.GetBytes(ask), new Dictionary<string, string>(), out st);
                    if (st != 200) { res.Error = "The Cure Server didn't enroll this PC (" + st + "): " + Short(ans); return res; }
                    var o = CureProtocol.Json().Deserialize<Dictionary<string, object>>(ans);
                    string id = o.ContainsKey("id") ? o["id"] as string : null, key = o.ContainsKey("key") ? o["key"] as string : null;
                    byte[] kb; try { kb = Convert.FromBase64String(key ?? ""); } catch { kb = null; }
                    if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(Uri.IsHexDigit) || kb == null || kb.Length != 32) { res.Error = "The Cure Server's enrollment answer isn't valid."; return res; }
                    dev = new Device { Id = id, Server = c.BaseUrl, Key = kb };
                    SaveDevice(dev);
                    using (ChangeLog.In("Cure Agent")) ChangeLog.Add("Settings", "This PC joined the Cure Server", c.BaseUrl, "", "device " + id, "Reports only: the server can't start anything on this PC", false);
                }
                byte[] body = Encoding.UTF8.GetBytes(CureProtocol.Json().Serialize(Collect()));
                if (body.Length > CureProtocol.MaxBody) { res.Error = "The report is too big."; return res; }
                string time = CureProtocol.UnixNow(), nonce = CureProtocol.Nonce();
                int status; string reply = Post(c, "api/v1/report", body, new Dictionary<string, string> {
                    { "X-Cure-Device", dev.Id }, { "X-Cure-Time", time }, { "X-Cure-Nonce", nonce }, { "X-Cure-Sign", CureProtocol.Sign(dev.Key, time, nonce, body) } }, out status);
                if (status == 401 || status == 410) { ForgetDevice(); res.Error = "The Cure Server no longer knows this PC; it will enroll again."; return res; }
                if (status != 200) { res.Error = "The Cure Server refused the report (" + status + "): " + Short(reply); return res; }
                res.Sent = true;
                // the only thing the server can say: when to report next
                try
                {
                    var o = CureProtocol.Json().Deserialize<Dictionary<string, object>>(reply);
                    if (o != null && o.ContainsKey("next") && o["next"] is int) res.NextMinutes = Math.Max(CureProtocol.MinInterval, Math.Min(CureProtocol.MaxInterval, (int)o["next"]));
                }
                catch { }
            }
            catch (Exception e) { res.Error = "Couldn't reach the Cure Server: " + e.Message; }
            return res;
        }

        // the last attempt, for CureAgent.exe --status (in the agent's admin-only folder)
        public static string StatusFile { get { return Path.Combine(Path.GetDirectoryName(DeviceFile), "status.txt"); } }
        public static void WriteStatus(Result r)
        {
            try { SafeFile.Save(StatusFile, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "\t" + (r.Sent ? "sent; next in " + r.NextMinutes + " min" : "not sent: " + r.Error)); } catch { }
        }

        static string Short(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length > 200 ? s.Substring(0, 200) : s; }

        // ---------- the service ----------
        public class Service : ServiceBase
        {
            readonly ManualResetEvent stop = new ManualResetEvent(false);
            Thread worker;
            public Service() { ServiceName = CureAgent.ServiceName; CanStop = true; CanShutdown = true; }
            protected override void OnStart(string[] args) { worker = new Thread(Loop) { IsBackground = true, Name = "Cure Agent" }; worker.Start(); }
            protected override void OnStop() { stop.Set(); if (worker != null) worker.Join(15000); }
            protected override void OnShutdown() { OnStop(); }
            void Loop()
            {
                var rnd = new Random();
                if (stop.WaitOne(TimeSpan.FromSeconds(30 + rnd.Next(90)))) return;   // let Windows finish starting, and not every PC at once
                int fails = 0;
                while (true)
                {
                    var r = SendOnce();
                    // no settings yet (waiting for Group Policy): look again every 5 minutes; that only reads the registry
                    int minutes = r.Sent ? r.NextMinutes : r.NoSettings ? CureProtocol.MinInterval : Math.Min(60, CureProtocol.MinInterval << Math.Min(fails, 4));
                    fails = r.Sent || r.NoSettings ? 0 : fails + 1;
                    WriteStatus(r);
                    double jitter = minutes * 60 * (0.9 + rnd.NextDouble() * 0.2);
                    if (stop.WaitOne(TimeSpan.FromSeconds(jitter))) return;
                }
            }
        }
    }
}
#endif
