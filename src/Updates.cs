// Updates from the GitHub releases of Elixar. A new version is installed only when its manifest is signed with
// Elisar's private key (the public half is built into Elixar) and every file matches its SHA-256 in that manifest.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CDriveCleaner
{
    public static class Updates
    {
        // filled in by the GitHub build (build.ps1 writes update-config.txt); empty in a local build
        static Dictionary<string, string> config;
        static Dictionary<string, string> Config
        {
            get
            {
                if (config != null) return config;
                config = new Dictionary<string, string>();
                try
                {
                    using (var st = typeof(Updates).Assembly.GetManifestResourceStream("Elixar.update-config.txt"))
                        if (st != null)
                            using (var r = new StreamReader(st))
                                foreach (var line in r.ReadToEnd().Split('\n'))
                                {
                                    int i = line.IndexOf('=');
                                    if (i > 0) config[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                                }
                }
                catch { }
                return config;
            }
        }
        public static string Repo { get { string v; return Config.TryGetValue("repo", out v) ? v : ""; } }

        // the Sponsor button: GitHub Sponsors of the repository's owner, or a "sponsor=" link in the build's config (only these sites)
        public static string SponsorUrl { get { string v; return SponsorFrom(Repo, Config.TryGetValue("sponsor", out v) ? v : null); } }
        public static string SponsorFrom(string repo, string sponsor)
        {
            if (sponsor != null)
            {
                string v = sponsor.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(v, @"^https://(github\.com/sponsors|ko-fi\.com|www\.buymeacoffee\.com)/[A-Za-z0-9_-]{1,64}/?$")) return v;
            }
            var m = System.Text.RegularExpressions.Regex.Match(repo ?? "", @"^([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/[A-Za-z0-9._-]{1,100}$");
            return m.Success ? "https://github.com/sponsors/" + m.Groups[1].Value : null;
        }
        public static string ConfigValue(string key) { string v; return Config.TryGetValue(key, out v) ? v : ""; }
        public static string PublicKey { get { string v; return Config.TryGetValue("key", out v) ? v : ""; } }
        public static bool Configured { get { return Repo.Contains("/") && PublicKey.Contains("<Modulus>"); } }
        public static string ReleasesPage { get { return Repo.Length > 0 ? "https://github.com/" + Repo + "/releases" : ""; } }

        public static Version Current { get { return typeof(Updates).Assembly.GetName().Version; } }

        // Version numbers started again at 1.2.27 (the release after 2.2.7). Builds from before carry a 0 as the last part
        // of their version (2.2.7.0, 1.9.1.0); the new numbering carries 1 (1.2.27.1), and release tags have no last part
        // (v1.2.28). So any new-numbering version counts as newer than any old one, whatever the digits.
        static int Era(Version v) { return v.Revision == 0 ? 0 : 1; }
        public static int Compare(Version a, Version b)
        {
            if (a == null || b == null) return a == null ? (b == null ? 0 : -1) : 1;
            if (Era(a) != Era(b)) return Era(a).CompareTo(Era(b));
            return new Version(a.Major, a.Minor, Math.Max(0, a.Build)).CompareTo(new Version(b.Major, b.Minor, Math.Max(0, b.Build)));
        }
        public static bool IsNewer(Version a, Version than) { return Compare(a, than) > 0; }

        public class Release { public Version Version; public string Tag = "", ZipUrl = "", ManifestUrl = "", SigUrl = "", Page = "", Notes = ""; }
        public class Manifest { public Version Version; public string ZipSha256 = "", ExeSha256 = "", Edition = "private"; }   // no edition line: Elixar for Private

        public static Version ParseTag(string tag)
        {
            Version v;
            return Version.TryParse((tag ?? "").TrimStart('v', 'V'), out v) ? v : null;
        }

        // GitHub's "latest release" answer -> the files Elixar needs
        public static Release ParseRelease(string json)
        {
            var o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var r = new Release { Tag = (o.ContainsKey("tag_name") ? o["tag_name"] : "") as string ?? "", Page = (o.ContainsKey("html_url") ? o["html_url"] : "") as string ?? "",
                                  Notes = (o.ContainsKey("body") ? o["body"] : "") as string ?? "" };
            r.Version = ParseTag(r.Tag);
            var assets = o.ContainsKey("assets") ? o["assets"] as System.Collections.ArrayList : null;
            if (assets != null)
                foreach (Dictionary<string, object> a in assets)
                {
                    string name = a["name"] as string ?? "", url = a["browser_download_url"] as string ?? "";
                    // only this edition's files (Elixar for Private and Elixar for Business are published side by side)
                    if (name.Equals(Edition.ZipAsset, StringComparison.OrdinalIgnoreCase)) r.ZipUrl = url;
                    else if (name.Equals(Edition.ManifestAsset + ".txt", StringComparison.OrdinalIgnoreCase)) r.ManifestUrl = url;
                    else if (name.Equals(Edition.ManifestAsset + ".sig", StringComparison.OrdinalIgnoreCase)) r.SigUrl = url;
                }
            return r;
        }

        public static Manifest ParseManifest(string text)
        {
            var m = new Manifest();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var p = raw.Trim().Split(new[] { '=' }, 2);
                if (p.Length != 2) continue;
                if (p[0] == "version") Version.TryParse(p[1], out m.Version);
                else if (p[0] == "zip-sha256") m.ZipSha256 = p[1].ToUpperInvariant();
                else if (p[0] == "exe-sha256") m.ExeSha256 = p[1].ToUpperInvariant();
                else if (p[0] == "edition") m.Edition = p[1].Trim().ToLowerInvariant();
            }
            return m;
        }

        public static bool VerifySignature(byte[] data, byte[] signature, string publicKeyXml)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))   // PROV_RSA_AES: supports SHA-256
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), signature);
                }
            }
            catch { return false; }
        }

        static WebClient Client()
        {
            Sys.SecureTls();   // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Elixar/" + Current;
            return wc;
        }

        public const int MaxSmall = 64 * 1024;            // the signed manifest and its signature
        public const long MaxZip = 200L * 1024 * 1024;    // a release is about 1 MB

        // downloads at most max bytes; a bigger answer is refused (it can't be a genuine Elixar release)
        public static long Download(string url, Stream to, long max)
        {
            Sys.SecureTls();
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "Elixar/" + Current; rq.Timeout = 60000; rq.ReadWriteTimeout = 60000; rq.MaximumAutomaticRedirections = 5;
            using (var rs = rq.GetResponse())
            {
                if (rs.ContentLength > max) throw new IOException("The download is bigger than an Elixar release can be.");
                return CopyLimited(rs.GetResponseStream(), to, max);
            }
        }

        public static long CopyLimited(Stream from, Stream to, long max)
        {
            var buf = new byte[81920]; long total = 0; int n;
            while ((n = from.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > max) throw new IOException("The download is bigger than an Elixar release can be.");
                to.Write(buf, 0, n);
            }
            return total;
        }

        static byte[] Small(string url) { using (var ms = new MemoryStream()) { Download(url, ms, MaxSmall); return ms.ToArray(); } }

        static bool FromGitHub(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == "https" &&
                   (u.Host == "github.com" || u.Host == "api.github.com" || u.Host.EndsWith(".githubusercontent.com"));
        }

        public static Release Latest(out string error)
        {
            error = null;
            if (!Repo.Contains("/")) { error = "Updates start once Elixar is published on GitHub."; return null; }
            try
            {
                using (var wc = Client())
                {
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    return ParseRelease(wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest"));
                }
            }
            catch (Exception e) { error = "Could not check for updates: " + e.Message; return null; }
        }

        // downloads and checks a release; returns the path of the new Elixar.exe in a folder only administrators can change
        public static string Prepare(Release r, Action<string> log, out string error)
        {
            error = null;
            if (!Configured) { error = "This copy of Elixar has no update key, so it can't check that an update is genuine. Download it from " + ReleasesPage; return null; }
            if (new[] { r.ZipUrl, r.ManifestUrl, r.SigUrl }.Any(u => !FromGitHub(u))) { error = "The release is missing its signed files."; return null; }
            string dir = Path.Combine(Path.Combine(Paths.Data, "update"), Guid.NewGuid().ToString("N"));
            try
            {
                if (!Locked.Lock(Path.Combine(Paths.Data, "update"))) { error = "Could not prepare a protected folder for the update."; return null; }
                Directory.CreateDirectory(dir);
                byte[] manifest, sig;
                manifest = Small(r.ManifestUrl); sig = Small(r.SigUrl);
                byte[] sigBytes;
                try { sigBytes = Convert.FromBase64String(Encoding.ASCII.GetString(sig).Trim()); } catch { sigBytes = sig; }
                if (!VerifySignature(manifest, sigBytes, PublicKey)) { error = "The update is not signed by Elisar's key, so Elixar did not install it."; return null; }
                var m = ParseManifest(Encoding.UTF8.GetString(manifest));
                if (m.Version == null || !IsNewer(m.Version, Current)) { error = "No newer version."; return null; }
                if (m.Edition != Edition.Id) { error = "The update is for another Elixar edition, so Elixar did not install it."; return null; }
                if (log != null) log("Downloading Elixar " + m.Version + "...");
                string zip = Path.Combine(dir, "Elixar.zip");
                using (var fs = new FileStream(zip, FileMode.CreateNew, FileAccess.Write)) Download(r.ZipUrl, fs, MaxZip);
                if (Trust.Sha256(zip) != m.ZipSha256) { error = "The download does not match its signed fingerprint."; return null; }
                string extract = Path.Combine(dir, "x");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, extract);
                string exe = Directory.GetFiles(extract, "Elixar.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe == null || Trust.Sha256(exe) != m.ExeSha256) { error = "Elixar.exe in the download does not match its signed fingerprint."; return null; }
                if (log != null) log("Elixar " + m.Version + " is downloaded and checked (signature and fingerprint).");
                return exe;
            }
            catch (Exception e) { error = "Update failed: " + e.Message; return null; }
        }

        // the new version waits for this one to close, puts itself in place and starts
        public static void Launch(string newExe)
        {
            PinLock.WriteTicket();   // the new version opens without asking for the PIN again
            Process.Start(new ProcessStartInfo(newExe, "--apply-update \"" + Install.Current + "\" " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
        }

        // The new version puts itself into Program Files (only administrators can change it) and starts from there.
        // It never writes over, or starts, a copy in a folder other programs can change (like Downloads): a link or a swapped
        // file there could turn an administrator's write or start into someone else's. An older copy left in Downloads hands
        // over to the newer protected copy when it starts.
        public static int Apply(string target, int pid)
        {
            try { var p = Process.GetProcessById(pid); p.WaitForExit(60000); } catch { }
            string err = "not tried";
            for (int i = 0; i < 20 && err != null; i++) { err = Install.Ensure(); if (err != null) Thread.Sleep(500); }
            if (err == null) { try { Process.Start(new ProcessStartInfo(Install.Exe) { UseShellExecute = false }); } catch { } return 0; }
            Lang.Box(null, "The update could not be installed: " + err, "Elixar");
            return 1;
        }
    }
}
