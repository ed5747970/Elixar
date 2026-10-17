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
        public static string PublicKey { get { string v; return Config.TryGetValue("key", out v) ? v : ""; } }
        public static bool Configured { get { return Repo.Contains("/") && PublicKey.Contains("<Modulus>"); } }
        public static string ReleasesPage { get { return Repo.Length > 0 ? "https://github.com/" + Repo + "/releases" : ""; } }

        public static Version Current { get { return typeof(Updates).Assembly.GetName().Version; } }

        public class Release { public Version Version; public string Tag = "", ZipUrl = "", ManifestUrl = "", SigUrl = "", Page = "", Notes = ""; }
        public class Manifest { public Version Version; public string ZipSha256 = "", ExeSha256 = ""; }

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
                    if (name.Equals("Elixar.zip", StringComparison.OrdinalIgnoreCase)) r.ZipUrl = url;
                    else if (name.Equals("elixar-manifest.txt", StringComparison.OrdinalIgnoreCase)) r.ManifestUrl = url;
                    else if (name.Equals("elixar-manifest.sig", StringComparison.OrdinalIgnoreCase)) r.SigUrl = url;
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
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "Elixar/" + Current;
            return wc;
        }

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
                using (var wc = Client()) { manifest = wc.DownloadData(r.ManifestUrl); sig = wc.DownloadData(r.SigUrl); }
                byte[] sigBytes;
                try { sigBytes = Convert.FromBase64String(Encoding.ASCII.GetString(sig).Trim()); } catch { sigBytes = sig; }
                if (!VerifySignature(manifest, sigBytes, PublicKey)) { error = "The update is not signed by Elisar's key, so Elixar did not install it."; return null; }
                var m = ParseManifest(Encoding.UTF8.GetString(manifest));
                if (m.Version == null || m.Version <= Current) { error = "No newer version."; return null; }
                if (log != null) log("Downloading Elixar " + m.Version + "...");
                string zip = Path.Combine(dir, "Elixar.zip");
                using (var wc = Client()) wc.DownloadFile(r.ZipUrl, zip);
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
            System.Windows.Forms.MessageBox.Show("The update could not be installed: " + err, "Elixar");
            return 1;
        }
    }
}
