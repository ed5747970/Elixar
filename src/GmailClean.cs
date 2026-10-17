// Email cleaning: finds old ads, social notifications, spam and large old emails in your Gmail and moves the ones you pick
// to Gmail's Trash.
//
// Safety:
// - Off until you turn it on. Elixar first asks you in its own words, then Google asks for the permission.
// - Elixar only reads each email's sender, date and size (format=metadata, From header only). It never opens what an email
//   says, and nothing about your emails is saved on the PC or sent anywhere.
// - The searches are fixed in Elixar; Starred and Important emails, and chats, are always left out.
// - Emails are only MOVED to Gmail's Trash, after you confirm (Gmail keeps them there 30 days, so they can be restored).
//   Elixar never deletes an email permanently.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CDriveCleaner
{
    public class MailCategory
    {
        public string Key = "", Name = "", Query = "", About = "";
        public Tier Tier;
        public bool DefaultOn, SpamFolder;
        public int Count;
        public bool More;                       // more than Elixar counts (the cap)
        public long Bytes = -1;                 // estimated from a sample
        public int Sampled;
        public List<string> Ids = new List<string>();
        public List<KeyValuePair<string, int>> Senders = new List<KeyValuePair<string, int>>();
        public string Error;
    }

    public static class Gmail
    {
        public const string Keep = " -is:starred -is:important -in:chats";
        public const int Cap = 20000, Sample = 60;
        public static string Api = "https://gmail.googleapis.com/gmail/v1/users/me/";   // changed only by tests (a fake Gmail on this PC)

        // the searches are fixed here: nothing typed by anyone is put into them
        public static List<MailCategory> Categories()
        {
            return new List<MailCategory>
            {
                new MailCategory { Key = "promotions", Name = "Ads and offers older than a month", Query = "category:promotions older_than:30d" + Keep, Tier = Tier.Safe, DefaultOn = true,
                                   About = "Gmail's Promotions tab: shops, newsletters and deals you have already seen." },
                new MailCategory { Key = "social", Name = "Social network notifications older than a month", Query = "category:social older_than:30d" + Keep, Tier = Tier.Safe, DefaultOn = true,
                                   About = "Gmail's Social tab: likes, comments and friend requests from Facebook, LinkedIn, Instagram and others." },
                new MailCategory { Key = "forums", Name = "Group and mailing-list emails older than 3 months", Query = "category:forums older_than:90d" + Keep, Tier = Tier.Safe, DefaultOn = true,
                                   About = "Gmail's Forums tab: mailing lists and discussion groups." },
                new MailCategory { Key = "spam", Name = "Spam", Query = "in:spam", Tier = Tier.Safe, DefaultOn = true, SpamFolder = true,
                                   About = "Gmail removes spam by itself after 30 days; moving it to the Trash just clears it sooner." },
                new MailCategory { Key = "updates", Name = "Automatic updates older than 6 months", Query = "category:updates older_than:180d" + Keep, Tier = Tier.Review,
                                   About = "Gmail's Updates tab: shipping notices, account alerts, statements and receipts. Check the senders first: you may want to keep receipts." },
                new MailCategory { Key = "large", Name = "Large emails (over 10 MB) older than a year", Query = "larger:10M older_than:1y" + Keep, Tier = Tier.Review,
                                   About = "Emails with big attachments, often photos or videos. Your decision: save the attachments first if you need them." },
            };
        }

        static Dictionary<string, object> Json(string s) { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(s); }

        static string Call(string method, string url, string body, out int status)
        {
            status = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string err;
                string token = GoogleAuth.AccessToken(out err);
                if (token == null) throw new IOException(err);
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method; req.Timeout = 60000; req.UserAgent = "Elixar/" + Updates.Current;
                req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
                try
                {
                    if (body != null)
                    {
                        req.ContentType = "application/json";
                        var b = Encoding.UTF8.GetBytes(body);
                        using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                    }
                    else if (method == "POST") req.ContentLength = 0;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var r = new StreamReader(resp.GetResponseStream()))
                    { status = (int)resp.StatusCode; return r.ReadToEnd(); }
                }
                catch (WebException we)
                {
                    var r = we.Response as HttpWebResponse;
                    status = r == null ? 0 : (int)r.StatusCode;
                    if (status == 401 && attempt == 0) { GoogleAuth.Forget(); continue; }
                    if (status == 429 || status >= 500) { Thread.Sleep(1000 * (attempt + 1)); continue; }   // busy: wait and try again
                    if (status == 403) throw new IOException("Google didn't allow this: Email cleaning needs your permission (Email tab > Allow).");
                    throw new IOException("Gmail answered " + (r == null ? we.Message : status + " " + r.StatusDescription));
                }
            }
            throw new IOException("Gmail is busy. Try again in a few minutes.");
        }

        // "Amazon.com <store-news@amazon.com>" -> "Amazon.com"
        public static string SenderName(string from)
        {
            from = (from ?? "").Trim();
            int lt = from.IndexOf('<');
            string name = lt > 0 ? from.Substring(0, lt).Trim().Trim('"').Trim() : "";
            if (name.Length > 0) return name.Length > 40 ? name.Substring(0, 40) : name;
            string addr = lt >= 0 ? from.Substring(lt + 1).TrimEnd('>') : from;
            int at = addr.IndexOf('@');
            return at >= 0 ? addr.Substring(at + 1) : (addr.Length > 0 ? addr : "(unknown)");
        }

        // counts the emails in a category and looks at a sample of them for their size and senders
        public static void Scan(MailCategory c, CancellationToken cancel)
        {
            c.Ids.Clear(); c.Count = 0; c.More = false; c.Bytes = -1; c.Sampled = 0; c.Senders.Clear(); c.Error = null;
            try
            {
                string page = null;
                int st;
                do
                {
                    if (cancel.IsCancellationRequested) return;
                    string url = Api + "messages?maxResults=500&fields=messages/id,nextPageToken&q=" + Uri.EscapeDataString(c.Query) +
                                 (c.SpamFolder ? "&includeSpamTrash=true" : "") + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
                    var o = Json(Call("GET", url, null, out st));
                    var msgs = o.ContainsKey("messages") ? o["messages"] as System.Collections.ArrayList : null;
                    if (msgs != null) foreach (Dictionary<string, object> m in msgs) c.Ids.Add((string)m["id"]);
                    page = o.ContainsKey("nextPageToken") ? o["nextPageToken"] as string : null;
                    if (c.Ids.Count >= Cap) { c.More = page != null; break; }
                } while (page != null);
                c.Count = c.Ids.Count;
                if (c.Count == 0) { c.Bytes = 0; return; }
                // a spread-out sample: sizes and senders only
                var pick = new List<string>();
                int step = Math.Max(1, c.Count / Sample);
                for (int i = 0; i < c.Count && pick.Count < Sample; i += step) pick.Add(c.Ids[i]);
                long sum = 0; int got = 0;
                var senders = new Dictionary<string, int>();
                Parallel.ForEach(pick, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancel }, id =>
                {
                    try
                    {
                        int s2;
                        var m = Json(Call("GET", Api + "messages/" + Uri.EscapeDataString(id) + "?format=metadata&metadataHeaders=From&fields=sizeEstimate,payload/headers", null, out s2));
                        long size = Convert.ToInt64(m.ContainsKey("sizeEstimate") ? m["sizeEstimate"] : 0, CultureInfo.InvariantCulture);
                        string from = "";
                        var pl = m.ContainsKey("payload") ? m["payload"] as Dictionary<string, object> : null;
                        var hs = pl != null && pl.ContainsKey("headers") ? pl["headers"] as System.Collections.ArrayList : null;
                        if (hs != null) foreach (Dictionary<string, object> h in hs) if (string.Equals(h["name"] as string, "From", StringComparison.OrdinalIgnoreCase)) from = h["value"] as string;
                        string who = SenderName(from);
                        lock (senders) { sum += size; got++; int n; senders.TryGetValue(who, out n); senders[who] = n + 1; }
                    }
                    catch { }
                });
                c.Sampled = got;
                c.Bytes = got == 0 ? -1 : (long)(sum / (double)got * c.Count);
                c.Senders = senders.OrderByDescending(kv => kv.Value).Take(8).ToList();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { c.Error = e.Message; }
        }

        // moves emails to Gmail's Trash (restorable there for 30 days); returns how many were moved
        public static int Trash(List<string> ids, Action<int> progress, CancellationToken cancel)
        {
            int done = 0, failed = 0;
            try
            {
            Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancel }, (id, state) =>
            {
                try
                {
                    int st;
                    Call("POST", Api + "messages/" + Uri.EscapeDataString(id) + "/trash?fields=id", null, out st);
                    int n = Interlocked.Increment(ref done);
                    if (progress != null && n % 25 == 0) progress(n);
                }
                catch { if (Interlocked.Increment(ref failed) > 50) state.Stop(); }   // something is wrong (offline, permission): stop
            });
            }
            catch (OperationCanceledException) { }
            return done;
        }
    }
}
