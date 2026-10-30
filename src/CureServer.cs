// Cure Server (Elixar for Business): a Windows service on the company's own server (CureServer.exe, Program Files\Elixar).
// It receives Cure Agent reports and shows them in the Cure Console, a web page on the same port.
//   - HTTPS only (http.sys with the server's certificate); it runs as its own low-rights account (NT SERVICE\CureServer).
//   - Agents: enroll with the company token (only its SHA-256 is kept), then every report must be signed with the PC's key.
//   - Console: Windows sign-in (Kerberos/NTLM); only the allowed group (and the server's administrators) can see it.
//   - Stage 1 is reports only: there is no way to send anything to a PC. The console only reads.
// Data: C:\ProgramData\Elixar\CureServer (administrators, SYSTEM and the service only).

#if BUSINESS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class CureDevice
    {
        public string Id = "", Computer = "", Key = "", Enrolled = "", LastSeen = "", Ip = "";
        public bool Revoked;
    }

    public class CureServer
    {
        public const string ServiceName = "CureServer", SettingsKey = @"SOFTWARE\Elixar\CureServer";
        public const int DefaultPort = 8443, MinSecondsBetweenReports = 60, MaxEnrollPerHour = 200;

        // ---------- settings (registry, set by --install) ----------
        public int Port = DefaultPort;
        public string Group = "", TokenHash = "", DataDir = "";
        public bool Https = true;
        public Func<HttpListenerContext, string> UserOverride;   // self-test: the signed-in console user (null = not signed in)
        public static Func<string, string, bool> InGroupOverride; // self-test

        public static CureServer FromRegistry()
        {
            var s = new CureServer { DataDir = DefaultDataDir };
            using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var k = b.OpenSubKey(SettingsKey))
                if (k != null)
                {
                    object p = k.GetValue("Port"); if (p is int) s.Port = (int)p;
                    s.Group = (k.GetValue("Group") as string ?? "").Trim();
                    s.TokenHash = (k.GetValue("TokenHash") as string ?? "").Trim().ToLowerInvariant();
                }
            return s;
        }
        public static string DefaultDataDir { get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Elixar"), "CureServer"); } }
        public static string HashToken(string token) { using (var sha = SHA256.Create()) return CureProtocol.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? ""))); }
        public static string NewToken() { return Convert.ToBase64String(CureProtocol.NewKey()).Replace('+', '-').Replace('/', '_').TrimEnd('='); }

        // ---------- the device list ----------
        readonly object gate = new object();
        List<CureDevice> devices;
        readonly Dictionary<string, Dictionary<string, DateTime>> nonces = new Dictionary<string, Dictionary<string, DateTime>>();
        readonly Dictionary<string, DateTime> lastReport = new Dictionary<string, DateTime>();
        readonly Queue<DateTime> enrolls = new Queue<DateTime>();                                       // successful enrollments
        readonly Dictionary<string, Queue<DateTime>> badEnrolls = new Dictionary<string, Queue<DateTime>>();   // wrong tokens, per address
        public const int MaxBadEnrollPerHour = 20;
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Elixar Cure Server device keys v1");

        string DevicesFile { get { return Path.Combine(DataDir, "devices.json"); } }
        string ReportFile(string id) { return Path.Combine(Path.Combine(DataDir, "reports"), id + ".json"); }
        string HistoryFile(string id) { return Path.Combine(Path.Combine(DataDir, "history"), id + ".csv"); }

        public List<CureDevice> Devices()
        {
            lock (gate)
            {
                if (devices == null)
                {
                    try { devices = File.Exists(DevicesFile) ? CureProtocol.Json().Deserialize<List<CureDevice>>(File.ReadAllText(DevicesFile)) ?? new List<CureDevice>() : new List<CureDevice>(); }
                    catch { devices = new List<CureDevice>(); }
                }
                return devices;
            }
        }
        void SaveDevices() { lock (gate) { Directory.CreateDirectory(DataDir); var j = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }; WriteAtomic(DevicesFile, j.Serialize(devices)); } }

        static void WriteAtomic(string file, string text)
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
        }

        byte[] Protect(byte[] k) { return Win.IsWindows ? ProtectedData.Protect(k, Entropy, DataProtectionScope.LocalMachine) : k; }
        byte[] Unprotect(byte[] k) { return Win.IsWindows ? ProtectedData.Unprotect(k, Entropy, DataProtectionScope.LocalMachine) : k; }

        // ---------- agents ----------
        public class Answer { public int Status; public string Body = ""; public string Type = "application/json; charset=utf-8"; }
        static Answer A(int status, object o) { return new Answer { Status = status, Body = CureProtocol.Json().Serialize(o) }; }
        static Answer Err(int status, string msg) { return A(status, new Dictionary<string, string> { { "error", msg } }); }

        public Answer Enroll(byte[] body, string ip)
        {
            if (TokenHash.Length != 64) return Err(503, "This Cure Server has no enrollment token yet.");
            // wrong tokens are limited per address, so guessing (or flooding) from one place can't stop real PCs from enrolling
            string from = ip ?? "";
            lock (gate)
            {
                Queue<DateTime> bad;
                if (badEnrolls.TryGetValue(from, out bad)) { while (bad.Count > 0 && DateTime.UtcNow - bad.Peek() > TimeSpan.FromHours(1)) bad.Dequeue(); if (bad.Count >= MaxBadEnrollPerHour) return Err(429, "Too many wrong enrollment tokens from this address."); }
                while (enrolls.Count > 0 && DateTime.UtcNow - enrolls.Peek() > TimeSpan.FromHours(1)) enrolls.Dequeue();
                if (enrolls.Count >= MaxEnrollPerHour) return Err(429, "Too many new PCs this hour.");
                if (badEnrolls.Count > 10000) badEnrolls.Clear();   // memory stays bounded
            }
            Dictionary<string, object> o;
            try { o = CureProtocol.Json().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body)); } catch { return Err(400, "Not JSON."); }
            string token = o != null && o.ContainsKey("token") ? o["token"] as string : null, computer = o != null && o.ContainsKey("computer") ? o["computer"] as string : null;
            if (!CureProtocol.Same(HashToken(token), TokenHash))
            {
                lock (gate) { Queue<DateTime> bad; if (!badEnrolls.TryGetValue(from, out bad)) badEnrolls[from] = bad = new Queue<DateTime>(); bad.Enqueue(DateTime.UtcNow); }
                return Err(403, "Wrong enrollment token.");
            }
            if (!CureProtocol.GoodName(computer)) return Err(400, "Not a computer name.");
            lock (gate) enrolls.Enqueue(DateTime.UtcNow);   // only real enrollments count toward the hourly limit
            byte[] key = CureProtocol.NewKey();
            var d = new CureDevice { Id = CureProtocol.Hex(CureProtocol.NewKey()).Substring(0, 32), Computer = computer.ToUpperInvariant(), Key = Convert.ToBase64String(Protect(key)),
                                     Enrolled = DateTime.Now.ToString("s", CultureInfo.InvariantCulture), Ip = ip ?? "" };
            lock (gate) { Devices().Add(d); SaveDevices(); }
            return A(200, new Dictionary<string, string> { { "id", d.Id }, { "key", Convert.ToBase64String(key) } });
        }

        public Answer Report(byte[] body, string id, string time, string nonce, string sign, string ip)
        {
            CureDevice d;
            lock (gate) d = Devices().FirstOrDefault(x => x.Id == id);
            if (d == null || string.IsNullOrEmpty(id)) return Err(401, "Unknown PC.");
            if (d.Revoked) return Err(410, "This PC was removed from the Cure Server.");
            long t; if (!long.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) return Err(400, "No time.");
            long now = long.Parse(CureProtocol.UnixNow(), CultureInfo.InvariantCulture);
            if (Math.Abs(now - t) > CureProtocol.MaxSkewSeconds) return Err(400, "The PC's clock is more than 5 minutes off.");
            if (string.IsNullOrEmpty(nonce) || nonce.Length > 64 || !nonce.All(Uri.IsHexDigit)) return Err(400, "No nonce.");
            byte[] key; try { key = Unprotect(Convert.FromBase64String(d.Key)); } catch { return Err(500, "This PC's key can't be read on this server."); }
            if (!CureProtocol.Same(CureProtocol.Sign(key, time, nonce, body), (sign ?? "").ToLowerInvariant())) return Err(401, "Wrong signature.");
            lock (gate)
            {
                Dictionary<string, DateTime> seen;
                if (!nonces.TryGetValue(id, out seen)) nonces[id] = seen = new Dictionary<string, DateTime>();
                foreach (var old in seen.Where(kv => DateTime.UtcNow - kv.Value > TimeSpan.FromMinutes(15)).Select(kv => kv.Key).ToList()) seen.Remove(old);
                if (seen.ContainsKey(nonce)) return Err(409, "This report was already received.");
                seen[nonce] = DateTime.UtcNow;   // a signed request is used up even when it's refused for coming too soon
                DateTime last;
                if (lastReport.TryGetValue(id, out last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(MinSecondsBetweenReports)) return Err(429, "Too many reports.");
                lastReport[id] = DateTime.UtcNow;
            }
            CureReport r;
            try { r = CureProtocol.Clean(CureProtocol.Json().Deserialize<CureReport>(Encoding.UTF8.GetString(body))); } catch { return Err(400, "Not a report."); }
            if (r == null) return Err(400, "Not a report.");
            r.Computer = d.Computer;   // the name it enrolled with, whatever the report says
            Directory.CreateDirectory(Path.GetDirectoryName(ReportFile(id))); Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile(id)));
            WriteAtomic(ReportFile(id), CureProtocol.Json().Serialize(r));
            AddHistory(id, r);
            lock (gate) { d.LastSeen = DateTime.Now.ToString("s", CultureInfo.InvariantCulture); d.Ip = ip ?? ""; SaveDevices(); }
            return A(200, new Dictionary<string, object> { { "next", CureProtocol.DefaultInterval } });
        }

        // one line a day per PC (the last report of the day wins), at most 400 days
        void AddHistory(string id, CureReport r)
        {
            string f = HistoryFile(id), day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var lines = File.Exists(f) ? File.ReadAllLines(f).Where(l => !l.StartsWith(day + ",")).ToList() : new List<string>();
            lines.Add(string.Join(",", new[] { day, r.Free.ToString(CultureInfo.InvariantCulture), r.Total.ToString(CultureInfo.InvariantCulture), r.CleanSafe.ToString(CultureInfo.InvariantCulture) }));
            WriteAtomic(f, string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 400))) + "\n");
        }

        public bool Remove(string nameOrId)
        {
            lock (gate)
            {
                var hit = Devices().Where(d => d.Id == nameOrId || d.Computer.Equals(nameOrId, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var d in hit) { d.Revoked = true; d.Key = ""; }
                if (hit.Count > 0) SaveDevices();
                return hit.Count > 0;
            }
        }

        // ---------- the console's data ----------
        public CureReport Latest(string id)
        {
            try { return File.Exists(ReportFile(id)) ? CureProtocol.Json().Deserialize<CureReport>(File.ReadAllText(ReportFile(id))) : null; } catch { return null; }
        }

        public static string State(CureReport r, DateTime lastSeen, string newest)
        {
            if (lastSeen != DateTime.MinValue && DateTime.Now - lastSeen > TimeSpan.FromDays(7)) return "off";
            if (r == null) return "off";
            if (r.DiskLevel == "Failing") return "disk";
            if (r.Total > 0 && r.Free >= 0 && r.Free * 100 < r.Total * 10) return "full";
            if (r.DiskLevel == "Warning") return "worn";
            Version a, b;
            if (Version.TryParse(r.Elixar, out a) && Version.TryParse(newest ?? "", out b) && a < b) return "old";
            return "ok";
        }

        public List<Dictionary<string, object>> Pcs()
        {
            var list = new List<Dictionary<string, object>>();
            var reps = Devices().Where(d => !d.Revoked).Select(d => Tuple.Create(d, Latest(d.Id))).ToList();
            // two live enrollments with one name: one of them may not be that PC (a copied enrollment token) - shown, never merged
            var twice = new HashSet<string>(reps.GroupBy(x => x.Item1.Computer, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            string newest = reps.Where(x => x.Item2 != null).Select(x => { Version v; return Version.TryParse(x.Item2.Elixar, out v) ? v : null; }).Where(v => v != null).DefaultIfEmpty(new Version(0, 0)).Max().ToString();
            foreach (var x in reps)
            {
                var d = x.Item1; var r = x.Item2;
                DateTime seen; DateTime.TryParse(d.LastSeen, CultureInfo.InvariantCulture, DateTimeStyles.None, out seen);
                list.Add(new Dictionary<string, object> {
                    { "id", d.Id }, { "pc", d.Computer }, { "state", State(r, seen == default(DateTime) ? DateTime.MinValue : seen, newest) }, { "seen", d.LastSeen },
                    { "free", r != null ? r.Free : -1 }, { "total", r != null ? r.Total : -1 }, { "clean", r != null ? r.CleanSafe : -1 }, { "disk", r != null ? r.DiskLevel : "" },
                    { "lastClean", r != null ? r.LastClean : "" }, { "updates", r != null ? r.AppUpdates : -1 }, { "elixar", r != null ? r.Elixar : "" }, { "saved", r != null ? r.Saved : -1 },
                    { "month", r != null ? r.SavedThisMonth : -1 }, { "twice", twice.Contains(d.Computer) }, { "enrolled", d.Enrolled }, { "ip", d.Ip } });
            }
            return list;
        }

        public Dictionary<string, object> Pc(string id)
        {
            var d = Devices().FirstOrDefault(x => x.Id == id && !x.Revoked);
            if (d == null) return null;
            var hist = File.Exists(HistoryFile(id)) ? File.ReadAllLines(HistoryFile(id)).Select(l => l.Split(',')).Where(p => p.Length >= 4)
                          .Select(p => new object[] { p[0], Long(p[1]), Long(p[2]), Long(p[3]) }).ToList() : new List<object[]>();
            bool twice = Devices().Count(x => !x.Revoked && x.Computer.Equals(d.Computer, StringComparison.OrdinalIgnoreCase)) > 1;
            return new Dictionary<string, object> { { "id", d.Id }, { "pc", d.Computer }, { "seen", d.LastSeen }, { "enrolled", d.Enrolled }, { "ip", d.Ip }, { "twice", twice }, { "report", Latest(id) }, { "history", hist } };
        }
        // one point a day: { 0, free, total, safe to clean }
        public List<long[]> History(string id)
        {
            try
            {
                return File.Exists(HistoryFile(id)) ? File.ReadAllLines(HistoryFile(id)).Select(l => l.Split(',')).Where(p => p.Length >= 4)
                                                          .Select(p => new[] { 0L, Long(p[1]), Long(p[2]), Long(p[3]) }).ToList() : new List<long[]>();
            }
            catch { return new List<long[]>(); }
        }

        // the Domain PCs tab (Elixar for Business on the server itself) reads the same files the service writes
        public static bool InstalledHere()
        {
            if (!Win.IsWindows) return false;
            try { using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) using (var k = b.OpenSubKey(SettingsKey)) return k != null; }
            catch { return false; }
        }

        static long Long(string s) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; }

        public string Csv()
        {
            var sb = new StringBuilder("PC,State,Free bytes,Total bytes,Safe to clean bytes,Disk,Last clean,App updates,Elixar,Last report\r\n");
            Func<object, string> c = v =>
            {
                string s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                if (s.Length > 0 && "=+-@\t\r".IndexOf(s[0]) >= 0 && !(s[0] == '-' && s.Skip(1).All(char.IsDigit))) s = "'" + s;   // no formulas in Excel
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            };
            foreach (var p in Pcs()) sb.Append(string.Join(",", new[] { "pc", "state", "free", "total", "clean", "disk", "lastClean", "updates", "elixar", "seen" }.Select(k => c(p[k])))).Append("\r\n");
            return sb.ToString();
        }

        // ---------- HTTP ----------
        HttpListener listener;
        Thread loop;
        volatile bool stopping;
        public string Prefix { get { return (Https ? "https://+:" : "http://127.0.0.1:") + Port + "/"; } }

        public void Start()
        {
            Directory.CreateDirectory(DataDir);
            listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            if (UserOverride == null)
                listener.AuthenticationSchemeSelectorDelegate = rq => IsAgentPath(rq.Url.AbsolutePath) ? AuthenticationSchemes.Anonymous : AuthenticationSchemes.IntegratedWindowsAuthentication;
            listener.Start();
            loop = new Thread(() =>
            {
                while (!stopping)
                {
                    HttpListenerContext ctx;
                    try { ctx = listener.GetContext(); } catch { if (stopping) return; continue; }
                    ThreadPool.QueueUserWorkItem(_ => { try { Handle(ctx); } catch { try { ctx.Response.Abort(); } catch { } } });
                }
            }) { IsBackground = true, Name = "Cure Server" };
            loop.Start();
        }

        public void Stop() { stopping = true; try { listener.Stop(); listener.Close(); } catch { } }

        static bool IsAgentPath(string p) { return p == "/api/v1/enroll" || p == "/api/v1/report"; }

        void Handle(HttpListenerContext ctx)
        {
            var rq = ctx.Request; var rs = ctx.Response;
            string path = rq.Url.AbsolutePath, ip = rq.RemoteEndPoint != null ? rq.RemoteEndPoint.Address.ToString() : "";
            Answer a;
            if (IsAgentPath(path))
            {
                if (rq.HttpMethod != "POST") a = Err(405, "POST only.");
                else
                {
                    byte[] body = ReadBody(rq);
                    if (body == null) a = Err(413, "Too big.");
                    else if (path == "/api/v1/enroll") a = Enroll(body, ip);
                    else a = Report(body, rq.Headers["X-Cure-Device"], rq.Headers["X-Cure-Time"], rq.Headers["X-Cure-Nonce"], rq.Headers["X-Cure-Sign"], ip);
                }
            }
            else
            {
                string user = UserOverride != null ? UserOverride(ctx) : UserOf(ctx);
                if (user == null) a = Err(401, "Sign in with your Windows account.");
                else if (!Allowed(ctx, user)) a = Err(403, "Your account isn't allowed to see the Cure Console. Ask for the group " + (Group.Length > 0 ? Group : "Administrators") + ".");
                else if (rq.HttpMethod != "GET") a = Err(405, "The Cure Console only reads.");
                else a = Console(path, rq.QueryString["id"], user);
            }
            Send(rs, a, path);
        }

        static string UserOf(HttpListenerContext ctx)
        {
            var id = ctx.User != null ? ctx.User.Identity as WindowsIdentity : null;
            return id != null && id.IsAuthenticated && !id.IsAnonymous ? id.Name : null;
        }

        bool Allowed(HttpListenerContext ctx, string user)
        {
            if (InGroupOverride != null) return InGroupOverride(user, Group);
            var id = ctx.User != null ? ctx.User.Identity as WindowsIdentity : null;
            if (id == null) return false;
            var p = new WindowsPrincipal(id);
            if (p.IsInRole(WindowsBuiltInRole.Administrator)) return true;
            try { return Group.Length > 0 && p.IsInRole(Group); } catch { return false; }
        }

        static byte[] ReadBody(HttpListenerRequest rq)
        {
            if (rq.ContentLength64 > CureProtocol.MaxBody) return null;
            using (var ms = new MemoryStream())
            {
                var buf = new byte[8192]; int n; long total = 0;
                while ((n = rq.InputStream.Read(buf, 0, buf.Length)) > 0) { total += n; if (total > CureProtocol.MaxBody) return null; ms.Write(buf, 0, n); }
                return ms.ToArray();
            }
        }

        Answer Console(string path, string id, string user)
        {
            switch (path)
            {
                case "/": case "/index.html": return new Answer { Status = 200, Body = Resource("cure.console.html"), Type = "text/html; charset=utf-8" };
                case "/console.js": return new Answer { Status = 200, Body = Resource("cure.console.js"), Type = "text/javascript; charset=utf-8" };
                case "/console.css": return new Answer { Status = 200, Body = Resource("cure.console.css"), Type = "text/css; charset=utf-8" };
                case "/api/v1/pcs": return A(200, new Dictionary<string, object> { { "user", user }, { "pcs", Pcs() } });
                case "/api/v1/pc": { var p = Pc(id ?? ""); return p == null ? Err(404, "No such PC.") : A(200, p); }
                case "/api/v1/export.csv": return new Answer { Status = 200, Body = Csv(), Type = "text/csv; charset=utf-8" };
                default: return Err(404, "Not found.");
            }
        }

        public static Func<string, string> ResourceOverride;   // self-test
        static string Resource(string name)
        {
            if (ResourceOverride != null) return ResourceOverride(name);
            using (var s = typeof(CureServer).Assembly.GetManifestResourceStream("Elixar." + name))
                if (s != null) using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
            return "";
        }

        // every answer: no caching, no framing, no guessing of types, and the page may only load its own files
        public static readonly Dictionary<string, string> SecurityHeaders = new Dictionary<string, string>
        {
            { "Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'" },
            { "X-Content-Type-Options", "nosniff" }, { "X-Frame-Options", "DENY" }, { "Referrer-Policy", "no-referrer" }, { "Cache-Control", "no-store" },
        };

        void Send(HttpListenerResponse rs, Answer a, string path)
        {
            try
            {
                rs.StatusCode = a.Status; rs.ContentType = a.Type;
                foreach (var h in SecurityHeaders) rs.Headers[h.Key] = h.Value;
                if (Https) rs.Headers["Strict-Transport-Security"] = "max-age=31536000";
                if (path == "/api/v1/export.csv") rs.Headers["Content-Disposition"] = "attachment; filename=\"cure-pcs.csv\"";
                var b = Encoding.UTF8.GetBytes(a.Body ?? "");
                rs.ContentLength64 = b.Length;
                rs.OutputStream.Write(b, 0, b.Length);
            }
            finally { try { rs.Close(); } catch { } }
        }

        // ---------- the service ----------
        public class Service : ServiceBase
        {
            CureServer server;
            public Service() { ServiceName = CureServer.ServiceName; CanStop = true; }
            protected override void OnStart(string[] args) { server = FromRegistry(); server.Start(); }
            protected override void OnStop() { if (server != null) server.Stop(); }
        }
    }
}
#endif
