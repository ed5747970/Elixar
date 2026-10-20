// Email providers besides Gmail:
//  - Outlook / Hotmail / Live / MSN: "Sign in with Microsoft" (no password; send-only unless you turn on Email cleaning).
//  - Any other provider (Yahoo, iCloud, AOL, GMX, Zoho...): IMAP and SMTP with an app password, always over TLS.
// MailAccount is the one place the rest of Elixar talks to, whichever provider you use.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CDriveCleaner
{
    // stored encrypted for your Windows account, in the folder only administrators can open
    static class Vault
    {
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Elixar mail account v2");
        public static string PathOf(string name) { var d = GoogleAuth.Dir; return d == null ? null : Path.Combine(d, name); }
        public static void Save(string name, Dictionary<string, string> values)
        {
            string f = PathOf(name);
            if (f == null) throw new IOException("Could not prepare Elixar's protected mail folder.");
            var b = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(values));
            File.WriteAllBytes(f, SecretBox.Protect(b, Entropy));
            if (Lx.On) Lx.Private(f, false);
        }
        public static Dictionary<string, object> Load(string name)
        {
            try
            {
                string f = PathOf(name);
                if (f == null || !File.Exists(f)) return null;
                var b = File.ReadAllBytes(f);
                var raw = b;
                b = SecretBox.Unprotect(b, Entropy);
                var json = GoogleAuth.Json(Encoding.UTF8.GetString(b));
                if (Lx.On && json != null && !SecretBox.IsSealed(raw)) SecretBox.Upgrade(f, b, Entropy);
                return json;
            }
            catch { return null; }
        }
        public static void Delete(string name) { try { string f = PathOf(name); if (f != null && File.Exists(f)) File.Delete(f); } catch { } }
        public static string S(Dictionary<string, object> d, string k) { return GoogleAuth.Str(d, k); }
    }

    // ============================ Outlook / Hotmail: Sign in with Microsoft ============================
    public static class MicrosoftAuth
    {
        public static string AuthUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
        public static string TokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";   // changed only by tests
        public static string Graph = "https://graph.microsoft.com/v1.0/";
        public const string SendScopes = "openid email offline_access User.Read Mail.Send";
        public const string CleanScope = "Mail.ReadWrite";   // only after you turn on Email cleaning

        public static string ClientId
        {
            get
            {
                try { string f = Vault.PathOf("microsoft-client.txt"); if (f != null && File.Exists(f)) { string id = File.ReadAllText(f).Trim(); if (ValidId(id)) return id; } } catch { }
                string b = Updates.ConfigValue("ms-id");
                return ValidId(b) ? b : null;
            }
        }
        public static bool BuiltIn { get { return ValidId(Updates.ConfigValue("ms-id")); } }
        public static bool ValidId(string id) { Guid g; return !string.IsNullOrEmpty(id) && Guid.TryParse(id, out g); }
        public static string SaveClientId(string id)
        {
            id = (id ?? "").Trim();
            if (!ValidId(id)) return "That isn't an Application (client) ID. It looks like 00000000-0000-0000-0000-000000000000.";
            string f = Vault.PathOf("microsoft-client.txt");
            if (f == null) return "Could not prepare Elixar's protected mail folder.";
            File.WriteAllText(f, id);
            return null;
        }

        public class Account { public string Email = "", Refresh = "", Scopes = ""; public bool Clean { get { return (" " + Scopes.ToLowerInvariant() + " ").Contains("mail.readwrite"); } } }
        public static Account Load()
        {
            var o = Vault.Load("microsoft-account.dat");
            if (o == null) return null;
            var a = new Account { Email = Vault.S(o, "email"), Refresh = Vault.S(o, "refresh"), Scopes = Vault.S(o, "scopes") };
            return a.Email.Length > 0 && a.Refresh.Length > 0 ? a : null;
        }
        static void Save(Account a) { Vault.Save("microsoft-account.dat", new Dictionary<string, string> { { "email", a.Email }, { "refresh", a.Refresh }, { "scopes", a.Scopes } }); }
        public static void SignOut() { Vault.Delete("microsoft-account.dat"); token = null; }

        public static string AuthorizeUrl(string clientId, string redirect, string state, string challenge, bool clean)
        {
            Func<string, string> E = Uri.EscapeDataString;
            return AuthUrl + "?client_id=" + E(clientId) + "&response_type=code&response_mode=query&redirect_uri=" + E(redirect) +
                   "&scope=" + E(clean ? SendScopes + " " + CleanScope : SendScopes) + "&state=" + state + "&code_challenge=" + challenge + "&code_challenge_method=S256&prompt=select_account";
        }

        // the same careful browser sign-in as Google's: PKCE, a one-time check value, answered on this PC only
        public static string SignIn(Action<string> status, CancellationToken cancel, bool clean)
        {
            string cid = ClientId;
            if (cid == null) return "Set up Microsoft sign-in first (one time).";
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            TcpListener v6 = null;
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                try { v6 = new TcpListener(IPAddress.IPv6Loopback, port); v6.Start(); } catch { v6 = null; }   // "localhost" may mean ::1
                string redirect = "http://localhost:" + port + "/", state = GoogleAuth.Random(24), verifier = GoogleAuth.Random(48);
                if (status != null) status("Waiting for you to sign in to Microsoft in your browser...");
                GoogleAuth.OpenBrowser(AuthorizeUrl(cid, redirect, state, GoogleAuth.Challenge(verifier), clean));
                string code = null, err = null;
                DateTime until = DateTime.Now.AddMinutes(5);
                while (code == null && err == null)
                {
                    if (cancel.IsCancellationRequested) return "Sign-in was cancelled.";
                    if (DateTime.Now > until) return "Sign-in timed out. Try again.";
                    TcpListener ready = listener.Pending() ? listener : v6 != null && v6.Pending() ? v6 : null;
                    if (ready == null) { Thread.Sleep(150); continue; }
                    try
                    {
                        using (var client = ready.AcceptTcpClient())
                        {
                            client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                            var stream = client.GetStream();
                            var q = GoogleAuth.ParseQuery(GoogleAuth.ReadLine(stream));
                            string got, page;
                            if (!q.TryGetValue("state", out got) || got != state) page = GoogleAuth.Page("This page isn't for Elixar's sign-in.", false);
                            else if (q.ContainsKey("error")) { err = q["error"] == "access_denied" ? "You didn't allow Elixar's permissions." : "Microsoft said: " + q["error"]; page = GoogleAuth.Page("Sign-in didn't finish. You can close this tab.", false); }
                            else if (q.ContainsKey("code")) { code = q["code"]; page = GoogleAuth.Page("Microsoft sign-in done. You can close this tab and go back to Elixar.", true); }
                            else page = GoogleAuth.Page("Nothing to do here.", false);
                            var resp = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: " +
                                                              Encoding.UTF8.GetByteCount(page) + "\r\n\r\n" + page);
                            try { stream.Write(resp, 0, resp.Length); } catch { }
                        }
                    }
                    catch (IOException) { }        // a connection that stalls or breaks: wait for the next one
                    catch (System.Net.Sockets.SocketException) { }
                }
                if (err != null) return err;
                if (status != null) status("Finishing sign-in...");
                var tok = GoogleAuth.PostForm(TokenUrl, new Dictionary<string, string> { { "client_id", cid }, { "code", code }, { "redirect_uri", redirect },
                                                                                       { "grant_type", "authorization_code" }, { "code_verifier", verifier },
                                                                                       { "scope", clean ? SendScopes + " " + CleanScope : SendScopes } });
                string refresh = GoogleAuth.Str(tok, "refresh_token"), scope = GoogleAuth.Str(tok, "scope"), at = GoogleAuth.Str(tok, "access_token");
                if (refresh.Length == 0 || at.Length == 0) return "Microsoft didn't give Elixar a sign-in key: " + (GoogleAuth.Str(tok, "error_description") + " " + GoogleAuth.Str(tok, "error")).Trim();
                if (scope.IndexOf("Mail.Send", StringComparison.OrdinalIgnoreCase) < 0) return "Microsoft sign-in worked, but the \"send email\" permission wasn't given.";
                if (clean && scope.IndexOf("Mail.ReadWrite", StringComparison.OrdinalIgnoreCase) < 0) return "Microsoft sign-in worked, but the permission to see and tidy your emails wasn't given, so Email cleaning stays off.";
                token = at; tokenUntil = DateTime.Now.AddSeconds(Math.Max(60, ToInt(GoogleAuth.Str(tok, "expires_in"))) - 60);
                var me = GoogleAuth.Json(Get(Graph + "me?$select=mail,userPrincipalName"));
                string email = GoogleAuth.Str(me, "mail");
                if (email.Length == 0) email = GoogleAuth.Str(me, "userPrincipalName");
                if (!Regex.IsMatch(email, @"^[^@\s<>,;""]+@[^@\s<>,;""]+\.[^@\s<>,;""]+$")) return "Microsoft didn't share your email address.";
                Save(new Account { Email = email, Refresh = refresh, Scopes = scope });
                MailStatus.Reset();
                return null;
            }
            catch (Exception e) { return "Sign-in failed: " + e.Message; }
            finally { listener.Stop(); if (v6 != null) v6.Stop(); }
        }

        static int ToInt(string s) { int v; return int.TryParse(s, out v) ? v : 3600; }
        static string token; static DateTime tokenUntil;
        static readonly object gate = new object();
        public static void UseTestToken(string t) { token = t; tokenUntil = DateTime.Now.AddHours(1); }
        public const string SignedOutText = "Microsoft signed Elixar out (the sign-in was removed or expired). Sign in again in Settings.";

        public static string AccessToken(out string error)
        {
            lock (gate)
            {
                error = null;
                if (token != null && DateTime.Now < tokenUntil) return token;
                var a = Load();
                if (a == null) { error = "Not signed in."; return null; }
                try
                {
                    var tok = GoogleAuth.PostForm(TokenUrl, new Dictionary<string, string> { { "client_id", ClientId ?? "" }, { "grant_type", "refresh_token" },
                                                                                           { "refresh_token", a.Refresh }, { "scope", a.Clean ? SendScopes + " " + CleanScope : SendScopes } });
                    string at = GoogleAuth.Str(tok, "access_token");
                    if (at.Length == 0)
                    {
                        string e = GoogleAuth.Str(tok, "error");
                        if (e == "invalid_grant" || e == "interaction_required") { error = SignedOutText; MailStatus.Failed(error, true); }
                        else error = "Microsoft refused: " + (e + " " + GoogleAuth.Str(tok, "error_description")).Trim();
                        return null;
                    }
                    string nr = GoogleAuth.Str(tok, "refresh_token");
                    if (nr.Length > 0 && nr != a.Refresh) { a.Refresh = nr; Save(a); }   // Microsoft hands out a new one now and then
                    token = at; tokenUntil = DateTime.Now.AddSeconds(Math.Max(60, ToInt(GoogleAuth.Str(tok, "expires_in"))) - 60);
                    return at;
                }
                catch (Exception e) { error = "Could not reach Microsoft: " + e.Message; return null; }
            }
        }

        public static string Call(string method, string url, string body)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string err; string t = AccessToken(out err);
                if (t == null) throw new IOException(err);
                Sys.SecureTls();
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method; req.Timeout = 60000; req.UserAgent = "Elixar/" + Updates.Current;
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + t;
                try
                {
                    if (body != null) { req.ContentType = "application/json"; var b = Encoding.UTF8.GetBytes(body); using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length); }
                    else if (method == "POST") req.ContentLength = 0;
                    using (var resp = (HttpWebResponse)req.GetResponse()) using (var r = new StreamReader(resp.GetResponseStream())) return r.ReadToEnd();
                }
                catch (WebException we)
                {
                    var r = we.Response as HttpWebResponse;
                    int st = r == null ? 0 : (int)r.StatusCode;
                    if (st == 401 && attempt == 0) { token = null; continue; }
                    if (st == 429 || st >= 500) { Thread.Sleep(1500 * (attempt + 1)); continue; }
                    if (st == 403) throw new IOException("Microsoft didn't allow this: Email cleaning needs your permission (Settings > Email account).");
                    throw new IOException("Outlook answered " + (r == null ? we.Message : st + " " + r.StatusDescription));
                }
            }
            throw new IOException("Outlook is busy. Try again in a few minutes.");
        }
        static string Get(string url) { return Call("GET", url, null); }

        // sends to the signed-in address only; pictures go inside the email as inline attachments
        public static string Send(string subject, string text, string html, List<Mailer.Inline> images)
        {
            var a = Load();
            if (a == null) return "Not signed in.";
            try
            {
                var atts = (images ?? new List<Mailer.Inline>()).Select(im => (object)new Dictionary<string, object> {
                    { "@odata.type", "#microsoft.graph.fileAttachment" }, { "name", im.Id.Split('@')[0] + ".png" }, { "contentType", "image/png" },
                    { "contentBytes", Convert.ToBase64String(im.Png) }, { "isInline", true }, { "contentId", im.Id } }).ToList();
                var msg = new Dictionary<string, object> {
                    { "message", new Dictionary<string, object> {
                        { "subject", subject.Replace("\r", " ").Replace("\n", " ") }, { "body", new Dictionary<string, object> { { "contentType", "HTML" }, { "content", html } } },
                        { "toRecipients", new object[] { new Dictionary<string, object> { { "emailAddress", new Dictionary<string, object> { { "address", a.Email } } } } } },
                        { "attachments", atts } } },
                    { "saveToSentItems", false } };
                Call("POST", Graph + "me/sendMail", new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.Serialize(msg));
                MailStatus.Sent(subject);
                return null;
            }
            catch (Exception e) { string err = e.Message; if (err != SignedOutText) MailStatus.Failed(err, false); return err; }
        }
    }

    public static class Outlook
    {
        public static List<MailCategory> Categories()
        {
            return new List<MailCategory>
            {
                new MailCategory { Key = "junk", Name = "Junk Email", Query = "mailFolders/junkemail/messages", Tier = Tier.Safe, DefaultOn = true, SpamFolder = true,
                                   About = "Outlook's Junk Email folder: messages Outlook already decided are junk." },
                new MailCategory { Key = "other", Name = "\"Other\" inbox emails older than 2 months", Query = "mailFolders/inbox/messages|other|60", Tier = Tier.Safe, DefaultOn = true,
                                   About = "Outlook's \"Other\" tab: newsletters, ads and automatic notifications Outlook keeps out of Focused. Flagged and High importance emails are left out." },
                new MailCategory { Key = "otherold", Name = "\"Focused\" inbox emails older than 2 years", Query = "mailFolders/inbox/messages|focused|730", Tier = Tier.Review,
                                   About = "Old emails in your Focused inbox. Your decision: these can be personal. Flagged and High importance emails are left out." },
            };
        }

        // counts the emails (leaving out flagged and High importance ones) and looks at a sample for size and senders
        public static void Scan(MailCategory c, CancellationToken cancel)
        {
            c.Ids.Clear(); c.Count = 0; c.More = false; c.Bytes = -1; c.Sampled = 0; c.Senders.Clear(); c.Error = null;
            try
            {
                var p = c.Query.Split('|');
                string url = MicrosoftAuth.Graph + "me/" + p[0] + "?$top=500&$select=id,flag,importance,from";
                if (p.Length == 3)
                    url += "&$filter=" + Uri.EscapeDataString("inferenceClassification eq '" + p[1] + "' and receivedDateTime lt " +
                                                             DateTime.UtcNow.AddDays(-int.Parse(p[2])).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                var senders = new Dictionary<string, int>();
                while (url != null && !cancel.IsCancellationRequested)
                {
                    var o = GoogleAuth.Json(MicrosoftAuth.Call("GET", url, null));
                    var vals = o.ContainsKey("value") ? o["value"] as System.Collections.ArrayList : null;
                    if (vals != null)
                        foreach (Dictionary<string, object> m in vals)
                        {
                            var flag = m.ContainsKey("flag") ? m["flag"] as Dictionary<string, object> : null;
                            if (flag != null && GoogleAuth.Str(flag, "flagStatus") == "flagged") continue;
                            if (GoogleAuth.Str(m, "importance") == "high") continue;
                            c.Ids.Add(GoogleAuth.Str(m, "id"));
                            var from = m.ContainsKey("from") ? m["from"] as Dictionary<string, object> : null;
                            var ea = from != null && from.ContainsKey("emailAddress") ? from["emailAddress"] as Dictionary<string, object> : null;
                            if (ea != null && c.Ids.Count <= 2000)
                            {
                                string who = GoogleAuth.Str(ea, "name"); if (who.Length == 0) who = Gmail.SenderName(GoogleAuth.Str(ea, "address"));
                                int n; senders.TryGetValue(who, out n); senders[who] = n + 1;
                            }
                        }
                    url = o.ContainsKey("@odata.nextLink") ? o["@odata.nextLink"] as string : null;
                    if (url != null && !url.StartsWith(MicrosoftAuth.Graph, StringComparison.OrdinalIgnoreCase)) url = null;   // only ever follow Microsoft's own address
                    if (c.Ids.Count >= Gmail.Cap) { c.More = url != null; break; }
                }
                c.Count = c.Ids.Count;
                c.Senders = senders.OrderByDescending(kv => kv.Value).Take(8).ToList();
                c.Sampled = Math.Min(c.Count, 2000);
                if (c.Count == 0) { c.Bytes = 0; return; }
                // size from a spread-out sample (Outlook keeps it in the message's PR_MESSAGE_SIZE property)
                var pick = new List<string>();
                int step = Math.Max(1, c.Count / 30);
                for (int i = 0; i < c.Count && pick.Count < 30; i += step) pick.Add(c.Ids[i]);
                long sum = 0; int got = 0;
                Parallel.ForEach(pick, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, id =>
                {
                    try
                    {
                        var m = GoogleAuth.Json(MicrosoftAuth.Call("GET", MicrosoftAuth.Graph + "me/messages/" + Uri.EscapeDataString(id) +
                                                                   "?$select=id&$expand=" + Uri.EscapeDataString("singleValueExtendedProperties($filter=id eq 'Integer 0x0E08')"), null));
                        var props = m.ContainsKey("singleValueExtendedProperties") ? m["singleValueExtendedProperties"] as System.Collections.ArrayList : null;
                        if (props != null && props.Count > 0) { long v; if (long.TryParse(GoogleAuth.Str((Dictionary<string, object>)props[0], "value"), out v)) lock (pick) { sum += v; got++; } }
                    }
                    catch { }
                });
                c.Bytes = got == 0 ? -1 : (long)(sum / (double)got * c.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { c.Error = e.Message; }
        }

        // moves emails to Deleted Items (they can be restored from there)
        public static int Trash(List<string> ids, Action<int> progress, CancellationToken cancel)
        {
            int done = 0, failed = 0;
            try
            {
                Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, (id, state) =>
                {
                    try
                    {
                        MicrosoftAuth.Call("POST", MicrosoftAuth.Graph + "me/messages/" + Uri.EscapeDataString(id) + "/move", "{\"destinationId\":\"deleteditems\"}");
                        int n = Interlocked.Increment(ref done);
                        if (progress != null && n % 25 == 0) progress(n);
                    }
                    catch { if (Interlocked.Increment(ref failed) > 50) state.Stop(); }
                });
            }
            catch (OperationCanceledException) { }
            return done;
        }
    }

    // ============================ any other provider: IMAP (reading) and SMTP (sending) with an app password ============================
    public static class OtherMail
    {
        public class Settings_ { public string Email = "", User = "", Password = "", Imap = "", Smtp = ""; public int ImapPort = 993, SmtpPort = 587; public bool Clean; }

        public class Preset { public string Name = "", Imap = "", Smtp = "", Help = ""; }
        // well-known providers: servers filled in for you, and where to make an app password
        public static Preset PresetFor(string email)
        {
            string d = (email ?? "").Split('@').Last().Trim().ToLowerInvariant();
            // Gmail with an app password: for copies of Elixar that don't have Google's sign-in built in
            if (d == "gmail.com" || d == "googlemail.com")
                return new Preset { Name = "Gmail", Imap = "imap.gmail.com", Smtp = "smtp.gmail.com", Help = "Gmail: turn on 2-Step Verification (myaccount.google.com > Security), then open myaccount.google.com/apppasswords, make one named Elixar and paste its 16 letters here." };
            if (d.StartsWith("yahoo.") || d == "ymail.com" || d == "rocketmail.com")
                return new Preset { Name = "Yahoo Mail", Imap = "imap.mail.yahoo.com", Smtp = "smtp.mail.yahoo.com", Help = "Yahoo: login.yahoo.com > Account info > Account security > Generate app password." };
            if (d == "icloud.com" || d == "me.com" || d == "mac.com")
                return new Preset { Name = "iCloud Mail", Imap = "imap.mail.me.com", Smtp = "smtp.mail.me.com", Help = "iCloud: account.apple.com > Sign-In and Security > App-Specific Passwords." };
            if (d == "aol.com" || d == "aim.com")
                return new Preset { Name = "AOL Mail", Imap = "imap.aol.com", Smtp = "smtp.aol.com", Help = "AOL: login.aol.com > Account security > Generate app password." };
            if (d == "gmx.com" || d == "gmx.us")
                return new Preset { Name = "GMX", Imap = "imap.gmx.com", Smtp = "mail.gmx.com", Help = "GMX: turn on IMAP in Settings > POP3 & IMAP, then use your password or an app password." };
            if (d == "gmx.net" || d == "gmx.de" || d == "gmx.at" || d == "gmx.ch")
                return new Preset { Name = "GMX", Imap = "imap.gmx.net", Smtp = "mail.gmx.net", Help = "GMX: turn on IMAP in Settings > POP3 & IMAP, then use your password or an app password." };
            if (d == "zoho.com" || d == "zohomail.com")
                return new Preset { Name = "Zoho Mail", Imap = "imap.zoho.com", Smtp = "smtp.zoho.com", Help = "Zoho: accounts.zoho.com > Security > App Passwords." };
            if (d == "mail.com" || d == "email.com")
                return new Preset { Name = "mail.com", Imap = "imap.mail.com", Smtp = "smtp.mail.com", Help = "mail.com: turn on IMAP access in Settings, then use your password." };
            if (d.StartsWith("yandex.") || d == "ya.ru")
                return new Preset { Name = "Yandex Mail", Imap = "imap.yandex.com", Smtp = "smtp.yandex.com", Help = "Yandex: id.yandex.com > Security > App passwords (Mail)." };
            if (d.Length == 0) return new Preset();
            return new Preset { Name = d, Imap = "imap." + d, Smtp = "smtp." + d, Help = "Your provider's website explains how to make an app password (or turn on IMAP). Check the server names with your provider." };
        }

        // the address belongs to Gmail or Microsoft: their own safer sign-in is used instead
        // the safer sign-in for this address, when this copy of Elixar can do it. Gmail: Sign in with Google when it is
        // set up, otherwise an app password works too. Outlook.com no longer accepts app passwords, so it always needs
        // Sign in with Microsoft.
        public static string BetterChoice(string email) { return BetterChoice(email, GoogleAuth.HasClient); }
        public static string BetterChoice(string email, bool googleSignIn)
        {
            string d = (email ?? "").Split('@').Last().Trim().ToLowerInvariant();
            if (d == "gmail.com" || d == "googlemail.com") return googleSignIn ? "google" : null;
            if (d == "outlook.com" || d == "hotmail.com" || d == "live.com" || d == "msn.com" || d.StartsWith("hotmail.") || d.StartsWith("outlook.") || d.StartsWith("live.")) return "microsoft";
            return null;
        }

        public static Settings_ Load()
        {
            var o = Vault.Load("other-account.dat");
            if (o == null) return null;
            var s = new Settings_ { Email = Vault.S(o, "email"), User = Vault.S(o, "user"), Password = Vault.S(o, "pass"), Imap = Vault.S(o, "imap"), Smtp = Vault.S(o, "smtp"), Clean = Vault.S(o, "clean") == "1" };
            int p; if (int.TryParse(Vault.S(o, "imapPort"), out p)) s.ImapPort = p; if (int.TryParse(Vault.S(o, "smtpPort"), out p)) s.SmtpPort = p;
            return s.Email.Length > 0 && s.Password.Length > 0 ? s : null;
        }
        public static void Save(Settings_ s)
        {
            Vault.Save("other-account.dat", new Dictionary<string, string> { { "email", s.Email }, { "user", s.User }, { "pass", s.Password }, { "imap", s.Imap }, { "smtp", s.Smtp },
                { "imapPort", s.ImapPort.ToString(CultureInfo.InvariantCulture) }, { "smtpPort", s.SmtpPort.ToString(CultureInfo.InvariantCulture) }, { "clean", s.Clean ? "1" : "0" } });
        }
        public static void Forget() { Vault.Delete("other-account.dat"); }

        static readonly Regex Host = new Regex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62}\.)+[A-Za-z]{2,63}$");
        public static string Check(Settings_ s)
        {
            if (!Regex.IsMatch(s.Email ?? "", @"^[^@\s<>,;""]+@[^@\s<>,;""]+\.[^@\s<>,;""]+$")) return "That doesn't look like an email address.";
            if (string.IsNullOrEmpty(s.Password)) return "Enter the app password.";
            if (!Host.IsMatch(s.Imap ?? "") || !Host.IsMatch(s.Smtp ?? "")) return "Check the server names (like imap.example.com).";
            if (s.ImapPort != 993) return "Elixar only connects to IMAP over a secure connection (port 993).";
            if (s.SmtpPort != 587) return "Elixar only sends over a secure connection on port 587 (STARTTLS).";
            return null;
        }

        // tries the settings: signs in to IMAP (nothing is changed) and finds the Spam and Trash folders
        public static string Connect(Settings_ s)
        {
            string e = Check(s);
            if (e != null) return e;
            try
            {
                using (var im = Imap.Open(s)) { im.Folders(); }
                Save(s);
                MailStatus.Reset();
                return null;
            }
            catch (Exception ex) { return "Couldn't sign in to " + s.Imap + ": " + ex.Message; }
        }

        // the email itself: to and from your own address only, text and HTML, pictures inside it
        public static MailMessage Message(string email, string subject, string text, string html, List<Mailer.Inline> images)
        {
            var msg = new MailMessage(new MailAddress(email, "Elixar"), new MailAddress(email));
            msg.Subject = subject.Replace("\r", " ").Replace("\n", " ");
            msg.SubjectEncoding = Encoding.UTF8;
            msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, Encoding.UTF8, "text/plain"));
            var hv = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html");
            foreach (var im in images ?? new List<Mailer.Inline>())
                hv.LinkedResources.Add(new LinkedResource(new MemoryStream(im.Png), "image/png") { ContentId = im.Id });
            msg.AlternateViews.Add(hv);
            return msg;
        }

        public static string Send(string subject, string text, string html, List<Mailer.Inline> images)
        {
            var s = Load();
            if (s == null) return "Not connected.";
            try
            {
                Sys.SecureTls();
                using (var msg = Message(s.Email, subject, text, html, images))
                using (var smtp = new SmtpClient(s.Smtp, s.SmtpPort) { EnableSsl = true, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 60000,
                                                                        UseDefaultCredentials = false, Credentials = new NetworkCredential(s.User.Length > 0 ? s.User : s.Email, s.Password) })
                    smtp.Send(msg);
                MailStatus.Sent(subject);
                return null;
            }
            catch (Exception e)
            {
                string err = "Couldn't send through " + s.Smtp + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message);
                bool auth = e is SmtpException && e.Message.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0;
                MailStatus.Failed(auth ? "Your email provider didn't accept the app password any more. Connect again in Settings." : err, auth);
                return err;
            }
        }

        static string Imap4Date(DateTime d) { return d.ToString("d-MMM-yyyy", CultureInfo.InvariantCulture); }

        public static List<MailCategory> Categories()
        {
            DateTime now = DateTime.Today;
            return new List<MailCategory>
            {
                new MailCategory { Key = "spam", Name = "Spam / Junk folder", Query = "\\Junk|ALL", Tier = Tier.Safe, DefaultOn = true, SpamFolder = true,
                                   About = "The folder your provider already filled with spam." },
                new MailCategory { Key = "newsletters", Name = "Newsletters and ads older than a month", Query = "INBOX|UNFLAGGED BEFORE " + Imap4Date(now.AddDays(-30)) + " HEADER List-Unsubscribe \"\"", Tier = Tier.Safe, DefaultOn = true,
                                   About = "Emails in your inbox that carry an \"unsubscribe\" link (newsletters, shops, notifications). Flagged emails are left out." },
                new MailCategory { Key = "large", Name = "Large emails (over 10 MB) older than a year", Query = "INBOX|UNFLAGGED BEFORE " + Imap4Date(now.AddYears(-1)) + " LARGER 10000000", Tier = Tier.Review,
                                   About = "Emails with big attachments, often photos or videos. Your decision: save the attachments first if you need them." },
            };
        }

        public static void Scan(MailCategory c, CancellationToken cancel)
        {
            c.Ids.Clear(); c.Count = 0; c.More = false; c.Bytes = -1; c.Sampled = 0; c.Senders.Clear(); c.Error = null; c.Folder = null;
            var s = Load();
            if (s == null) { c.Error = "Not connected."; return; }
            try
            {
                using (var im = Imap.Open(s))
                {
                    var p = c.Query.Split(new[] { '|' }, 2);
                    string folder = p[0].StartsWith("\\") ? im.FolderWith(p[0], new[] { "Spam", "Junk", "Bulk Mail", "Junk E-mail", "Junk Email" }) : p[0];
                    if (folder == null) { c.Bytes = 0; return; }   // no spam folder: nothing to do
                    c.Folder = folder;
                    im.Examine(folder);   // read-only: nothing changes, not even "read" marks
                    c.UidValidity = im.LastValidity;
                    var uids = im.UidSearch(p[1]);
                    if (uids.Count > Gmail.Cap) { c.More = true; uids = uids.Skip(uids.Count - Gmail.Cap).ToList(); }
                    c.Ids.AddRange(uids.Select(u => u.ToString(CultureInfo.InvariantCulture)));
                    c.Count = c.Ids.Count;
                    if (c.Count == 0) { c.Bytes = 0; return; }
                    var pick = new List<long>();
                    int step = Math.Max(1, uids.Count / Gmail.Sample);
                    for (int i = 0; i < uids.Count && pick.Count < Gmail.Sample; i += step) pick.Add(uids[i]);
                    var info = im.SizesAndSenders(pick);
                    c.Sampled = info.Count;
                    c.Bytes = info.Count == 0 ? -1 : (long)(info.Average(x => (double)x.Key) * c.Count);
                    c.Senders = info.GroupBy(x => Gmail.SenderName(x.Value)).Select(g => new KeyValuePair<string, int>(g.Key, g.Count())).OrderByDescending(kv => kv.Value).Take(8).ToList();
                }
            }
            catch (Exception e) { c.Error = e.Message; }
        }

        public static int Trash(List<MailCategory> cats, Action<int> progress, CancellationToken cancel)
        {
            var s = Load();
            if (s == null) return 0;
            int done = 0;
            using (var im = Imap.Open(s))
            {
                string trash = im.FolderWith("\\Trash", new[] { "Trash", "Deleted Messages", "Deleted Items", "Deleted" });
                if (trash == null) throw new IOException("Couldn't find your Trash folder, so nothing was moved.");
                foreach (var c in cats.Where(x => x.Folder != null && x.Ids.Count > 0))
                {
                    if (string.Equals(c.Folder, trash, StringComparison.OrdinalIgnoreCase)) continue;
                    im.Select(c.Folder);
                    // the folder was renumbered since the check: these numbers may now be other emails, so nothing is moved
                    if (c.UidValidity != null && im.LastValidity != c.UidValidity) throw new IOException("Your mailbox changed since it was checked. Check it again, then clean.");
                    foreach (var chunk in c.Ids.Select((id, i) => new { id, i }).GroupBy(x => x.i / 200).Select(g => g.Select(x => long.Parse(x.id, CultureInfo.InvariantCulture)).ToList()))
                    {
                        if (cancel.IsCancellationRequested) return done;
                        im.Move(chunk, trash);
                        done += chunk.Count;
                        if (progress != null) progress(done);
                    }
                }
            }
            return done;
        }
    }

    // ============================ a small IMAP client: TLS only, the few commands Elixar needs ============================
    public class Imap : IDisposable
    {
        public static Func<string, int, Stream> OpenOverride;   // tests only: a fake server on this PC
        readonly Stream io;
        readonly TcpClient tcp;
        int tag;
        public HashSet<string> Caps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<KeyValuePair<string, string>> folders;   // name -> attributes

        Imap(Stream s, TcpClient t) { io = s; tcp = t; }

        public static Imap Open(OtherMail.Settings_ s)
        {
            Imap im;
            if (OpenOverride != null) im = new Imap(OpenOverride(s.Imap, s.ImapPort), null);
            else
            {
                var t = new TcpClient();
                var ar = t.BeginConnect(s.Imap, s.ImapPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(20000)) { t.Close(); throw new IOException("the server didn't answer"); }
                t.EndConnect(ar);
                t.ReceiveTimeout = 60000; t.SendTimeout = 60000;
                var ssl = new SslStream(t.GetStream(), false);   // the server's certificate is checked by Windows as usual
                ssl.AuthenticateAsClient(s.Imap, null, (SslProtocols)3072, true);   // TLS 1.2 only: 1.0 and 1.1 are broken
                im = new Imap(ssl, t);
            }
            string hello = im.ReadLine();
            if (!hello.StartsWith("* OK", StringComparison.OrdinalIgnoreCase)) throw new IOException("unexpected greeting");
            im.Command("CAPABILITY");
            im.Command("LOGIN " + Q(s.User.Length > 0 ? s.User : s.Email) + " " + Q(s.Password), "the email provider didn't accept the address or app password");
            im.Command("CAPABILITY");
            return im;
        }

        // a quoted IMAP string; anything that could break the command is refused
        public static string Q(string v)
        {
            if (v == null || v.Any(ch => ch < 32 || ch > 126)) throw new IOException("the address or password has characters Elixar can't send safely");
            return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        string ReadLine()
        {
            var b = new List<byte>();
            int c;
            while ((c = io.ReadByte()) >= 0) { if (c == '\n') break; if (c != '\r') b.Add((byte)c); if (b.Count > 1 << 20) throw new IOException("line too long"); }
            if (c < 0 && b.Count == 0) throw new IOException("the connection closed");
            return Encoding.UTF8.GetString(b.ToArray());
        }

        // runs one command; returns the untagged lines (with any {n} literals joined in)
        public List<string> Command(string cmd, string failText = null)
        {
            string t = "E" + (++tag).ToString(CultureInfo.InvariantCulture);
            var bytes = Encoding.UTF8.GetBytes(t + " " + cmd + "\r\n");
            io.Write(bytes, 0, bytes.Length); io.Flush();
            var lines = new List<string>();
            long total = 0;   // a server that never stops answering can't fill the memory
            while (true)
            {
                string line = ReadLine();
                total += line.Length;
                if (total > 64L << 20) throw new IOException("reply too big");
                var lit = Regex.Match(line, @"\{(\d+)\}$");
                while (lit.Success)
                {
                    int n;
                    if (!int.TryParse(lit.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n > 4 << 20) throw new IOException("reply too big");
                    total += n;
                    if (total > 64L << 20) throw new IOException("reply too big");
                    var buf = new byte[n]; int got = 0;
                    while (got < n) { int r = io.Read(buf, got, n - got); if (r <= 0) throw new IOException("the connection closed"); got += r; }
                    line = line + "\n" + Encoding.UTF8.GetString(buf) + ReadLine();
                    lit = Regex.Match(line, @"\{(\d+)\}$");
                }
                if (line.StartsWith(t + " ", StringComparison.Ordinal))
                {
                    string rest = line.Substring(t.Length + 1);
                    if (!rest.StartsWith("OK", StringComparison.OrdinalIgnoreCase)) throw new IOException(failText ?? ("the server said: " + rest));
                    break;
                }
                if (line.StartsWith("* CAPABILITY ", StringComparison.OrdinalIgnoreCase)) foreach (var cap in line.Substring(13).Split(' ')) Caps.Add(cap.Trim());
                lines.Add(line);
            }
            return lines;
        }

        public List<KeyValuePair<string, string>> Folders()
        {
            if (folders != null) return folders;
            folders = new List<KeyValuePair<string, string>>();
            foreach (var l in Command("LIST \"\" \"*\""))
            {
                var m = Regex.Match(l, "^\\* LIST \\(([^)]*)\\) (?:\"[^\"]*\"|NIL) (.+)$", RegexOptions.Singleline);
                if (!m.Success) continue;
                string name = m.Groups[2].Value.Trim();
                if (name.StartsWith("\"") && name.EndsWith("\"")) name = name.Substring(1, name.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
                else if (name.Contains("\n")) name = name.Substring(name.IndexOf('\n') + 1);
                folders.Add(new KeyValuePair<string, string>(name, m.Groups[1].Value));
            }
            return folders;
        }

        // the folder marked \Junk or \Trash by the server, or the usual names
        public string FolderWith(string attr, string[] names)
        {
            var f = Folders();
            var hit = f.FirstOrDefault(x => (" " + x.Value + " ").IndexOf(" " + attr + " ", StringComparison.OrdinalIgnoreCase) >= 0);
            if (hit.Key != null) return hit.Key;
            foreach (var n in names)
            {
                var byName = f.FirstOrDefault(x => string.Equals(x.Key, n, StringComparison.OrdinalIgnoreCase) || x.Key.EndsWith("/" + n, StringComparison.OrdinalIgnoreCase) || x.Key.EndsWith("." + n, StringComparison.OrdinalIgnoreCase));
                if (byName.Key != null) return byName.Key;
            }
            return null;
        }

        public void Examine(string folder) { LastValidity = Validity(Command("EXAMINE " + Q(folder))); }
        public void Select(string folder) { LastValidity = Validity(Command("SELECT " + Q(folder))); }
        public string LastValidity;   // UIDVALIDITY: if it changes, the same numbers can mean other emails
        static string Validity(List<string> lines)
        {
            foreach (var l in lines) { var m = Regex.Match(l, @"\[UIDVALIDITY (\d+)\]", RegexOptions.IgnoreCase); if (m.Success) return m.Groups[1].Value; }
            return null;
        }

        public List<long> UidSearch(string criteria)
        {
            var uids = new List<long>();
            foreach (var l in Command("UID SEARCH " + criteria))
                if (l.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase))
                    foreach (var p in l.Substring(8).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)) { long u; if (long.TryParse(p, out u)) uids.Add(u); }
            uids.Sort();
            return uids;
        }

        // size and From of a few emails (read-only: BODY.PEEK leaves them unread)
        public List<KeyValuePair<long, string>> SizesAndSenders(List<long> uids)
        {
            var res = new List<KeyValuePair<long, string>>();
            if (uids.Count == 0) return res;
            foreach (var l in Command("UID FETCH " + string.Join(",", uids) + " (RFC822.SIZE BODY.PEEK[HEADER.FIELDS (FROM)])"))
            {
                var sz = Regex.Match(l, @"RFC822\.SIZE (\d+)");
                if (!sz.Success) continue;
                var from = Regex.Match(l, @"(?im)^From:\s*(.+)$");
                res.Add(new KeyValuePair<long, string>(long.Parse(sz.Groups[1].Value, CultureInfo.InvariantCulture), from.Success ? from.Groups[1].Value.Trim() : ""));
            }
            return res;
        }

        // to the Trash folder: MOVE when the server has it, otherwise copy and mark only these emails as deleted
        public void Move(List<long> uids, string trash)
        {
            string set = string.Join(",", uids);
            if (Caps.Contains("MOVE")) { Command("UID MOVE " + set + " " + Q(trash)); return; }
            Command("UID COPY " + set + " " + Q(trash));
            Command("UID STORE " + set + " +FLAGS.SILENT (\\Deleted)");
            if (Caps.Contains("UIDPLUS")) Command("UID EXPUNGE " + set);   // never a plain EXPUNGE: it could remove other emails marked deleted
        }

        public void Dispose()
        {
            try { Command("LOGOUT"); } catch { }
            try { io.Dispose(); } catch { }
            if (tcp != null) try { tcp.Close(); } catch { }
        }
    }

    // ============================ the one place the rest of Elixar talks to ============================
    public static class MailAccount
    {
        static string ActiveFile { get { return Vault.PathOf("active.txt"); } }
        public static string Kind
        {
            get
            {
                try { string f = ActiveFile; if (f != null && File.Exists(f)) { string k = File.ReadAllText(f).Trim(); if (k == "google" || k == "microsoft" || k == "other") return k; } } catch { }
                return GoogleAuth.Load() != null ? "google" : null;
            }
        }
        public static void SetKind(string k) { try { string f = ActiveFile; if (f != null) File.WriteAllText(f, k); } catch { } }

        public static string Email
        {
            get
            {
                string k = Kind;
                if (k == "google") return GoogleAuth.SignedInEmail;
                if (k == "microsoft") { var a = MicrosoftAuth.Load(); return a == null ? null : a.Email; }
                if (k == "other") { var s = OtherMail.Load(); return s == null ? null : s.Email; }
                return null;
            }
        }
        public static bool Cleaning
        {
            get
            {
                string k = Kind;
                if (k == "google") return GoogleAuth.MailboxAccess;
                if (k == "microsoft") { var a = MicrosoftAuth.Load(); return a != null && a.Clean; }
                if (k == "other") { var s = OtherMail.Load(); return s != null && s.Clean; }
                return false;
            }
        }
        public static string ProviderName
        {
            get
            {
                string k = Kind;
                if (k == "google") return "Gmail";
                if (k == "microsoft") return "Outlook";
                if (k == "other") { var s = OtherMail.Load(); return s == null ? "email" : OtherMail.PresetFor(s.Email).Name; }
                return "Gmail";
            }
        }
        public static string TrashName { get { string k = Kind; return k == "microsoft" ? "Deleted Items" : k == "other" ? "Trash" : "Gmail's Trash"; } }

        public static string Send(string subject, string text, string html, List<Mailer.Inline> images)
        {
            string k = Kind;
            if (k == "microsoft") return MicrosoftAuth.Send(subject, text, html, images);
            if (k == "other") return OtherMail.Send(subject, text, html, images);
            return Mailer.Send(subject, text, html, images);
        }

        // for the daily check: is the sign-in still good?
        public static string CheckSignIn(out bool signedOut)
        {
            signedOut = false;
            string k = Kind, err = null;
            if (k == "google") { if (GoogleAuth.AccessToken(out err) == null) signedOut = err == GoogleAuth.SignedOutText; }
            else if (k == "microsoft") { if (MicrosoftAuth.AccessToken(out err) == null) signedOut = err == MicrosoftAuth.SignedOutText; }
            else if (k == "other")
            {
                var s = OtherMail.Load();
                try { using (Imap.Open(s)) { } }
                catch (Exception e) { err = "Couldn't sign in to " + s.Imap + ": " + e.Message; signedOut = e.Message.Contains("didn't accept"); if (signedOut) MailStatus.Failed(err, true); }
            }
            return err;
        }

        public static List<MailCategory> Categories()
        {
            string k = Kind;
            return k == "microsoft" ? Outlook.Categories() : k == "other" ? OtherMail.Categories() : Gmail.Categories();
        }
        public static void Scan(MailCategory c, CancellationToken t)
        {
            string k = Kind;
            if (k == "microsoft") Outlook.Scan(c, t); else if (k == "other") OtherMail.Scan(c, t); else Gmail.Scan(c, t);
        }
        public static int Trash(List<MailCategory> cats, Action<int> progress, CancellationToken t)
        {
            string k = Kind;
            if (k == "other") return OtherMail.Trash(cats, progress, t);
            var ids = cats.SelectMany(c => c.Ids).Distinct().ToList();
            return k == "microsoft" ? Outlook.Trash(ids, progress, t) : Gmail.Trash(ids, progress, t);
        }

        // removes the connection from this PC (and cancels it at Google)
        public static void Disconnect()
        {
            string k = Kind;
            if (k == "google") GoogleAuth.SignOut();
            else if (k == "microsoft") MicrosoftAuth.SignOut();
            else if (k == "other") OtherMail.Forget();
            MailStatus.Reset();
            try { string f = ActiveFile; if (f != null && File.Exists(f)) File.Delete(f); } catch { }
        }
    }
}
