// Email reports: "Sign in with Google" and emails from your own Gmail to yourself.
//
// Security:
// - Elixar asks Google only for permission to SEND mail (gmail.send). It can't read, change or delete anything in the mailbox.
// - Sign-in happens in your browser on Google's own page; Elixar never sees your password. It uses the standard way for desktop
//   apps (OAuth with PKCE and a one-time check value, answered on a port that only this PC can reach).
// - The sign-in key Google returns is encrypted for your Windows account (DPAPI) and kept in a folder only administrators can open.
// - Mail only ever goes to the address you signed in with, so Elixar can't be used to send mail to anyone else.
// - Emails contain categories and sizes, never file names or folder paths, and never a link: Elixar never asks you to click or sign
//   in from an email, so a look-alike email asking for that is not from Elixar.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CDriveCleaner
{
    public static class GoogleAuth
    {
        public const string Scope = "openid email https://www.googleapis.com/auth/gmail.send";
        // only when you turn on Email cleaning, after Elixar asks you: see senders, dates and sizes, and move emails to Gmail's Trash
        public const string ModifyScope = "https://www.googleapis.com/auth/gmail.modify";
        const string AuthUrl = "https://accounts.google.com/o/oauth2/v2/auth", TokenUrl = "https://oauth2.googleapis.com/token", RevokeUrl = "https://oauth2.googleapis.com/revoke";
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Elixar mail sign-in v1");

        public class Client { public string Id = "", Secret = ""; }
        public class Account { public string Email = "", Refresh = "", ClientId = "", ClientSecret = "", Scopes = ""; public DateTime Since;
                               public bool Mailbox { get { return (" " + Scopes + " ").Contains(" " + ModifyScope + " "); } } }

        public static string OverrideDir;   // self-test only
        public static string Dir
        {
            get
            {
                if (OverrideDir != null) return OverrideDir;
                string d = Path.Combine(Paths.Data, "mail");
                return Locked.Lock(d, false) ? d : null;   // administrators only: not even readable by normal programs
            }
        }
        static string ClientFile { get { var d = Dir; return d == null ? null : Path.Combine(d, "google-client.json"); } }
        static string AccountFile { get { var d = Dir; return d == null ? null : Path.Combine(d, "account.dat"); } }

        static Dictionary<string, object> Json(string s) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(s); }
        static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }

        // ---------- the Google "app keys" (the client) ----------
        // From the file Google lets you download for a "Desktop app" client: {"installed":{"client_id":...,"client_secret":...}}
        public static Client ParseClientFile(string json, out string error)
        {
            error = null;
            try
            {
                var o = Json(json);
                var inst = (o.ContainsKey("installed") ? o["installed"] : null) as Dictionary<string, object>;
                if (inst == null) { error = o.ContainsKey("web") ? "This is a \"Web application\" client. In Google Cloud, create a \"Desktop app\" client instead." : "This isn't a Google client file."; return null; }
                var c = new Client { Id = Str(inst, "client_id"), Secret = Str(inst, "client_secret") };
                if (!c.Id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || c.Id.IndexOfAny(new[] { ' ', '&', '?', '#', '/' }) >= 0 || c.Secret.Length == 0 || c.Secret.Length > 200)
                { error = "The file doesn't contain a valid Google client ID and secret."; return null; }
                return c;
            }
            catch { error = "The file couldn't be read as a Google client file."; return null; }
        }

        public static string ImportClient(string path)
        {
            if (new FileInfo(path).Length > 64 * 1024) return "That file is too big to be a Google client file.";
            string err; string text;
            using (var s = SafeFile.OpenRead(path)) using (var r = new StreamReader(s)) text = r.ReadToEnd();
            var c = ParseClientFile(text, out err);
            if (c == null) return err;
            string f = ClientFile;
            if (f == null) return "Could not prepare Elixar's protected mail folder.";
            File.WriteAllText(f, new JavaScriptSerializer().Serialize(new Dictionary<string, string> { { "id", c.Id }, { "secret", c.Secret } }));
            return null;
        }

        // one imported on this PC first, then the one built into Elixar by the GitHub build (so people there need no setup)
        public static Client LoadClient()
        {
            try
            {
                string f = ClientFile;
                if (f != null && File.Exists(f)) { var o = Json(File.ReadAllText(f)); var c = new Client { Id = Str(o, "id"), Secret = Str(o, "secret") }; if (c.Id.Length > 0) return c; }
            }
            catch { }
            return BuiltInClient;
        }

        public static Client BuiltInClient
        {
            get
            {
                var c = new Client { Id = Updates.ConfigValue("google-id"), Secret = Updates.ConfigValue("google-secret") };
                return c.Id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) && c.Secret.Length > 0 ? c : null;
            }
        }

        public static bool HasClient { get { return LoadClient() != null; } }

        // ---------- the signed-in account ----------
        static byte[] Protect(byte[] b) { return Win.IsWindows ? ProtectedData.Protect(b, Entropy, DataProtectionScope.CurrentUser) : b; }
        static byte[] Unprotect(byte[] b) { return Win.IsWindows ? ProtectedData.Unprotect(b, Entropy, DataProtectionScope.CurrentUser) : b; }

        public static void Save(Account a)
        {
            string f = AccountFile;
            if (f == null) throw new IOException("Could not prepare Elixar's protected mail folder.");
            var json = new JavaScriptSerializer().Serialize(new Dictionary<string, string> {
                { "email", a.Email }, { "refresh", a.Refresh }, { "cid", a.ClientId }, { "csec", a.ClientSecret }, { "scopes", a.Scopes }, { "since", a.Since.ToString("o", CultureInfo.InvariantCulture) } });
            File.WriteAllBytes(f, Protect(Encoding.UTF8.GetBytes(json)));
        }

        public static Account Load()
        {
            try
            {
                string f = AccountFile;
                if (f == null || !File.Exists(f)) return null;
                var o = Json(Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(f))));
                var a = new Account { Email = Str(o, "email"), Refresh = Str(o, "refresh"), ClientId = Str(o, "cid"), ClientSecret = Str(o, "csec"), Scopes = Str(o, "scopes") };
                DateTime.TryParse(Str(o, "since"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out a.Since);
                return a.Email.Length > 0 && a.Refresh.Length > 0 ? a : null;
            }
            catch { return null; }
        }

        public static string SignedInEmail { get { var a = Load(); return a == null ? null : a.Email; } }
        public static bool MailboxAccess { get { var a = Load(); return a != null && a.Mailbox; } }

        // ---------- sign-in in the browser ----------
        public static string B64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        public static byte[] FromB64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }
        static string Random(int bytes) { var b = new byte[bytes]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return B64Url(b); }
        public static string Challenge(string verifier) { using (var h = SHA256.Create()) return B64Url(h.ComputeHash(Encoding.ASCII.GetBytes(verifier))); }

        public static string AuthorizeUrl(Client c, string redirect, string state, string challenge) { return AuthorizeUrl(c, redirect, state, challenge, false); }

        public static string AuthorizeUrl(Client c, string redirect, string state, string challenge, bool mailbox)
        {
            Func<string, string> E = Uri.EscapeDataString;
            return AuthUrl + "?client_id=" + E(c.Id) + "&redirect_uri=" + E(redirect) + "&response_type=code&scope=" + E(mailbox ? Scope + " " + ModifyScope : Scope) +
                   "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state + "&access_type=offline&prompt=consent";
        }

        // "GET /?state=..&code=.. HTTP/1.1" -> the query values
        public static Dictionary<string, string> ParseQuery(string requestLine)
        {
            var d = new Dictionary<string, string>();
            var parts = (requestLine ?? "").Split(' ');
            if (parts.Length < 2 || parts[0] != "GET") return d;
            int q = parts[1].IndexOf('?');
            if (q < 0) return d;
            foreach (var kv in parts[1].Substring(q + 1).Split('&'))
            {
                int i = kv.IndexOf('=');
                if (i <= 0) continue;
                try { d[Uri.UnescapeDataString(kv.Substring(0, i).Replace('+', ' '))] = Uri.UnescapeDataString(kv.Substring(i + 1).Replace('+', ' ')); } catch { }
            }
            return d;
        }

        // the email address in Google's sign-in answer (the id_token came straight from Google over HTTPS)
        public static string EmailFromIdToken(string idToken, string clientId, out bool verified)
        {
            verified = false;
            try
            {
                var p = (idToken ?? "").Split('.');
                if (p.Length != 3) return null;
                var o = Json(Encoding.UTF8.GetString(FromB64Url(p[1])));
                string iss = Str(o, "iss"), aud = Str(o, "aud");
                if ((iss != "https://accounts.google.com" && iss != "accounts.google.com") || aud != clientId) return null;
                verified = Str(o, "email_verified").Equals("True", StringComparison.OrdinalIgnoreCase);
                string email = Str(o, "email");
                return email.Contains("@") && email.IndexOfAny(new[] { '\r', '\n', '<', '>', ',', ';', '"' }) < 0 ? email : null;
            }
            catch { return null; }
        }

        static Dictionary<string, object> PostForm(string url, Dictionary<string, string> form)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            string body = string.Join("&", form.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST"; req.ContentType = "application/x-www-form-urlencoded"; req.UserAgent = "Elixar/" + Updates.Current; req.Timeout = 30000;
            var b = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse()) using (var r = new StreamReader(resp.GetResponseStream())) return Json(r.ReadToEnd());
            }
            catch (WebException we)
            {
                if (we.Response == null) throw;
                using (var r = new StreamReader(we.Response.GetResponseStream())) { var o = Json(r.ReadToEnd()); o["_status"] = (int)((HttpWebResponse)we.Response).StatusCode; return o; }
            }
        }

        // Opens Google's sign-in page in the browser and waits (up to 5 minutes) for the answer. Returns an error or null.
        public static string SignIn(Action<string> status, CancellationToken cancel) { return SignIn(status, cancel, false); }

        // mailbox: also ask for Email cleaning (only after the person agreed in Elixar's own question first)
        public static string SignIn(Action<string> status, CancellationToken cancel, bool mailbox)
        {
            var c = LoadClient();
            if (c == null) return "Set up Google sign-in first (one time).";
            var listener = new TcpListener(IPAddress.Loopback, 0);   // only this PC can reach it
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = "http://127.0.0.1:" + port + "/", state = Random(24), verifier = Random(48);
                string url = AuthorizeUrl(c, redirect, state, Challenge(verifier), mailbox);
                if (status != null) status("Waiting for you to sign in to Google in your browser...");
                OpenBrowser(url);
                string code = null, err = null;
                DateTime until = DateTime.Now.AddMinutes(5);
                while (code == null && err == null)
                {
                    if (cancel.IsCancellationRequested) return "Sign-in was cancelled.";
                    if (DateTime.Now > until) return "Sign-in timed out. Try again.";
                    if (!listener.Pending()) { Thread.Sleep(150); continue; }
                    using (var client = listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                        var stream = client.GetStream();
                        string line = ReadLine(stream);
                        var q = ParseQuery(line);
                        string page;
                        string gotState;
                        if (!q.TryGetValue("state", out gotState) || gotState != state) page = Page("This page isn't for Elixar's sign-in.", false);   // a stray request (e.g. favicon)
                        else if (q.ContainsKey("error")) { err = q["error"] == "access_denied" ? "You didn't allow Elixar to send email." : "Google said: " + q["error"]; page = Page("Sign-in didn't finish. You can close this tab.", false); }
                        else if (q.ContainsKey("code")) { code = q["code"]; page = Page("Google sign-in done. You can close this tab and go back to Elixar.", true); }
                        else page = Page("Nothing to do here.", false);
                        var resp = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " +
                                                          Encoding.UTF8.GetByteCount(page) + "\r\n\r\n" + page);
                        try { stream.Write(resp, 0, resp.Length); } catch { }
                    }
                }
                if (err != null) return err;
                if (status != null) status("Finishing sign-in...");
                var tok = PostForm(TokenUrl, new Dictionary<string, string> { { "code", code }, { "client_id", c.Id }, { "client_secret", c.Secret },
                                                                            { "redirect_uri", redirect }, { "grant_type", "authorization_code" }, { "code_verifier", verifier } });
                string refresh = Str(tok, "refresh_token"), scope = Str(tok, "scope");
                if (refresh.Length == 0) return "Google didn't give Elixar a sign-in key: " + (Str(tok, "error_description") + " " + Str(tok, "error")).Trim();
                if (scope.IndexOf("gmail.send", StringComparison.Ordinal) < 0) return "Google sign-in worked, but the \"send email\" permission wasn't ticked. Sign in again and allow it.";
                if (mailbox && (" " + scope + " ").IndexOf(" " + ModifyScope + " ", StringComparison.Ordinal) < 0)
                    return "Google sign-in worked, but the permission to see and tidy your emails wasn't ticked, so Email cleaning stays off.";
                bool verified;
                string email = EmailFromIdToken(Str(tok, "id_token"), c.Id, out verified);
                if (email == null || !verified) return "Google didn't confirm your email address.";
                Save(new Account { Email = email, Refresh = refresh, ClientId = c.Id, ClientSecret = c.Secret, Since = DateTime.Now, Scopes = scope });
                MailStatus.Reset();
                cachedToken = Str(tok, "access_token"); cachedUntil = DateTime.Now.AddSeconds(Math.Max(60, ToInt(Str(tok, "expires_in"))) - 60);
                return null;
            }
            catch (Exception e) { return "Sign-in failed: " + e.Message; }
            finally { listener.Stop(); }
        }

        static int ToInt(string s) { int v; return int.TryParse(s, out v) ? v : 3600; }

        static string ReadLine(NetworkStream s)
        {
            var sb = new StringBuilder();
            int b;
            while (sb.Length < 8192 && (b = s.ReadByte()) >= 0) { if (b == '\n') break; if (b != '\r') sb.Append((char)b); }
            return sb.ToString();
        }

        static string Page(string text, bool ok)
        {
            return "<!doctype html><html><head><meta charset=\"utf-8\"><title>Elixar</title></head><body style=\"font-family:Segoe UI,sans-serif;background:#f6f8fb;color:#18202b;" +
                   "display:flex;align-items:center;justify-content:center;height:90vh\"><div style=\"background:#fff;border-radius:12px;padding:32px 40px;box-shadow:0 2px 12px #0001\">" +
                   "<div style=\"font-size:22px;font-weight:600;color:" + (ok ? "#007d46" : "#5b4cc4") + "\">Elixar</div><p style=\"font-size:16px\">" + WebUtility.HtmlEncode(text) + "</p></div></body></html>";
        }

        public static Action<string> BrowserOverride;   // tests only

        static void OpenBrowser(string url)
        {
            if (BrowserOverride != null) { BrowserOverride(url); return; }
            if (Win.IsWindows) System.Diagnostics.Process.Start(Win.SystemExe("explorer.exe"), "\"" + url + "\"");
            else System.Diagnostics.Process.Start("xdg-open", url);
        }

        // ---------- a short-lived key for sending ----------
        static string cachedToken; static DateTime cachedUntil;
        public static string LastError;
        public static void Forget() { cachedToken = null; }
        public static void UseTestToken(string t) { cachedToken = t; cachedUntil = DateTime.Now.AddHours(1); }   // tests only
        public const string SignedOutText = "Google signed Elixar out (the sign-in was removed or expired). Sign in again in Settings.";

        static readonly object tokenLock = new object();

        public static string AccessToken(out string error)
        {
            lock (tokenLock) return AccessTokenLocked(out error);   // Email cleaning asks from several threads at once
        }

        static string AccessTokenLocked(out string error)
        {
            error = null;
            if (cachedToken != null && DateTime.Now < cachedUntil) return cachedToken;
            var a = Load();
            if (a == null) { error = "Not signed in."; return null; }
            try
            {
                var tok = PostForm(TokenUrl, new Dictionary<string, string> { { "client_id", a.ClientId }, { "client_secret", a.ClientSecret },
                                                                            { "refresh_token", a.Refresh }, { "grant_type", "refresh_token" } });
                string at = Str(tok, "access_token");
                if (at.Length == 0)
                {
                    string e = Str(tok, "error");
                    if (e == "invalid_grant") { error = SignedOutText; MailStatus.Failed(error, true); }
                    else error = "Google refused: " + (e + " " + Str(tok, "error_description")).Trim();
                    return null;
                }
                cachedToken = at; cachedUntil = DateTime.Now.AddSeconds(Math.Max(60, ToInt(Str(tok, "expires_in"))) - 60);
                return at;
            }
            catch (Exception e) { error = "Could not reach Google: " + e.Message; return null; }
        }

        // removes the sign-in from this PC and asks Google to cancel it
        public static void SignOut()
        {
            var a = Load();
            if (a != null) { try { PostForm(RevokeUrl, new Dictionary<string, string> { { "token", a.Refresh } }); } catch { } }
            try { string f = AccountFile; if (f != null && File.Exists(f)) File.Delete(f); } catch { }
            MailStatus.Reset();
            cachedToken = null;
        }
    }

    // ============================ sending ============================
    public static class Mailer
    {
        static string B64Lines(byte[] b)
        {
            string s = Convert.ToBase64String(b);
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i += 76) sb.Append(s, i, Math.Min(76, s.Length - i)).Append("\r\n");
            return sb.ToString();
        }

        static string Header(string s) { return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Replace("\r", " ").Replace("\n", " "))) + "?="; }

        public class Inline { public string Id; public byte[] Png; }

        public static string Mime(string to, string subject, string text, string html) { return Mime(to, subject, text, html, null); }

        // text and HTML versions; pictures are placed inside the HTML (referenced as cid:<id>), never as links to the internet
        public static string Mime(string to, string subject, string text, string html, List<Inline> images)
        {
            string b = "elixar-" + Guid.NewGuid().ToString("N"), rb = "elixar-r-" + Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            sb.Append("From: ").Append(Header("Elixar")).Append(" <").Append(to).Append(">\r\n");
            sb.Append("To: <").Append(to).Append(">\r\n");
            sb.Append("Subject: ").Append(Header(subject)).Append("\r\n");
            sb.Append("Date: ").Append(DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("MIME-Version: 1.0\r\nX-Mailer: Elixar\r\n");
            sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(b).Append("\"\r\n\r\n");
            sb.Append("--").Append(b).Append("\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(B64Lines(Encoding.UTF8.GetBytes(text)));
            if (images == null || images.Count == 0)
                sb.Append("--").Append(b).Append("\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(B64Lines(Encoding.UTF8.GetBytes(html)));
            else
            {
                sb.Append("--").Append(b).Append("\r\nContent-Type: multipart/related; boundary=\"").Append(rb).Append("\"\r\n\r\n");
                sb.Append("--").Append(rb).Append("\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(B64Lines(Encoding.UTF8.GetBytes(html)));
                foreach (var im in images)
                    sb.Append("--").Append(rb).Append("\r\nContent-Type: image/png\r\nContent-Transfer-Encoding: base64\r\nContent-ID: <").Append(im.Id)
                      .Append(">\r\nContent-Disposition: inline; filename=\"").Append(im.Id.Split('@')[0]).Append(".png\"\r\n\r\n").Append(B64Lines(im.Png));
                sb.Append("--").Append(rb).Append("--\r\n");
            }
            sb.Append("--").Append(b).Append("--\r\n");
            return sb.ToString();
        }

        // sends to the signed-in address only; returns an error or null
        public static string Send(string subject, string text, string html) { return Send(subject, text, html, null); }

        public static string Send(string subject, string text, string html, List<Inline> images)
        {
            string to = GoogleAuth.SignedInEmail;
            if (to == null) return "Not signed in.";
            string err;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = GoogleAuth.AccessToken(out err);
                if (token == null) { GoogleAuth.LastError = err; if (err != GoogleAuth.SignedOutText) MailStatus.Failed(err, false); return err; }
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    var req = (HttpWebRequest)WebRequest.Create("https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
                    req.Method = "POST"; req.ContentType = "application/json"; req.Timeout = 60000; req.UserAgent = "Elixar/" + Updates.Current;
                    req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                    var body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, string> { { "raw", GoogleAuth.B64Url(Encoding.UTF8.GetBytes(Mime(to, subject, text, html, images))) } }));
                    using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                    using (var resp = (HttpWebResponse)req.GetResponse()) { }
                    GoogleAuth.LastError = null;
                    MailStatus.Sent(subject);
                    return null;
                }
                catch (WebException we)
                {
                    var r = we.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { ForgetToken(); continue; }
                    err = "Gmail didn't accept the email: " + (r != null ? (int)r.StatusCode + " " + r.StatusDescription : we.Message);
                    GoogleAuth.LastError = err;
                    MailStatus.Failed(err, false);
                    return err;
                }
            }
            return "Gmail didn't accept the email.";
        }

        static void ForgetToken() { GoogleAuth.Forget(); }
    }
}
