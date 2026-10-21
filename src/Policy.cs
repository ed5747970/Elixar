// Policy manager: the rules for what Elixar may change on this PC and how.
// Default = Elixar's own rules (exactly how Elixar works without a policy). Custom = your copy; switching back keeps it.
// An organization can set rules for every PC through Group Policy / Intune (HKLM\SOFTWARE\Policies\Elixar): those win and are locked.
// The policy file is kept in Elixar's data folder, which only administrators can change.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class PolicyRule
    {
        public string Key = "", Group = "", Name = "", Help = "", Default = "";
        public string[] Values = new string[0], Labels = new string[0];
        public string Label(string v) { int i = Array.IndexOf(Values, v); return i < 0 ? v : Labels[i]; }
        public bool Valid(string v) { return v != null && Values.Contains(v); }
    }

    public static class Policy
    {
        static PolicyRule R(string key, string group, string name, string help, string def, params string[] valuesAndLabels)
        {
            var r = new PolicyRule { Key = key, Group = group, Name = name, Help = help, Default = def };
            r.Values = valuesAndLabels.Where((x, i) => i % 2 == 0).ToArray();
            r.Labels = valuesAndLabels.Where((x, i) => i % 2 == 1).ToArray();
            return r;
        }

        static readonly string[] Allow = { "allow", "Allowed", "block", "Not allowed" };

        // the Default values are exactly how Elixar works without a policy, so nothing changes until you choose a stricter rule
        public static readonly PolicyRule[] Rules =
        {
            R("clean.level", "Cleaning", "What the Cleaner may delete", "Safe items are always fine; Check first and Can't be undone items need a look first.", "everything",
              "everything", "Everything (each clean asks first)", "review", "Safe and Check first", "safe", "Safe only"),
            R("clean.restore", "Cleaning", "Restore point before a clean", "A restore point lets Windows go back to before the clean.", "ask",
              "ask", "You choose each time", "always", "Always create one", "never", "Never"),
            R("undo.days", "Undo and Recycle Bin", "Keep removed files in Undo for", "Files from Smart scan and Duplicates; Tools > Undo puts them back.", "choice",
              "choice", "Your choice (Automation)", "7", "7 days", "30", "30 days", "90", "90 days"),
            R("bin.days", "Undo and Recycle Bin", "Empty old Recycle Bin items after", "The daily tidy, with administrator rights.", "choice",
              "choice", "Your choice (Automation)", "off", "Never", "14", "14 days", "30", "30 days", "60", "60 days", "90", "90 days"),
            R("low.clean", "Undo and Recycle Bin", "Safe clean by itself when C: is low", "At most once a day, when C: drops under the alert limit.", "choice",
              "choice", "Your choice (Automation)", "on", "Always on", "off", "Always off"),
            R("apps.remove", "What Elixar may change", "Remove apps", "Apps tab: uninstallers and built-in or Store apps.", "allow", Allow),
            R("startup.change", "What Elixar may change", "Change startup programs", "Tools > Startup: turn programs on or off, remove leftovers.", "allow", Allow),
            R("drivers.remove", "What Elixar may change", "Remove drivers", "Tools > Drivers: old and unused driver packages.", "allow", Allow),
            R("phone", "What Elixar may change", "Phone tools", "The Phone tab: clean, move files and remove apps on a phone.", "allow", Allow),
            R("sysprep", "What Elixar may change", "Sysprep and golden image tools", "Tools > Sysprep: fixes, answer files, image cleanup.", "allow", Allow),
            R("email", "What Elixar may change", "Email", "Connect an email account, reports and Gmail cleaning.", "allow", Allow),
            R("pin.open", "Access", "PIN or password to open Elixar", "Required: Elixar asks for one to be set and it can't be turned off.", "choice",
              "choice", "Your choice (Settings)", "required", "Required"),
            R("pin.policy", "Access", "PIN or password to change this policy", "Needed: changing the policy asks for the PIN or password first.", "off",
              "off", "Not needed", "on", "Needed"),
        };

        public static PolicyRule Rule(string key) { return Rules.FirstOrDefault(r => r.Key == key); }
        public static Dictionary<string, string> Defaults() { return Rules.ToDictionary(r => r.Key, r => r.Default); }

        // ready-made starting points for Custom
        public static readonly string[] PresetNames = { "Balanced (same as Default)", "Careful", "Deep clean", "Golden image (IT)", "Locked down (shared PC)" };
        public static Dictionary<string, string> Preset(string name)
        {
            var p = Defaults();
            Action<string, string> s = (k, v) => p[k] = v;
            switch (name)
            {
                case "Careful":
                    s("clean.level", "review"); s("clean.restore", "always"); s("undo.days", "90"); s("drivers.remove", "block"); s("sysprep", "block"); break;
                case "Deep clean":
                    s("clean.restore", "always"); s("undo.days", "7"); s("bin.days", "30"); s("low.clean", "on"); break;
                case "Golden image (IT)":
                    s("clean.restore", "never"); s("undo.days", "7"); s("bin.days", "off"); s("low.clean", "off"); s("phone", "block"); s("email", "block"); s("pin.policy", "on"); break;
                case "Locked down (shared PC)":
                    s("clean.level", "safe"); s("clean.restore", "always"); s("apps.remove", "block"); s("startup.change", "block"); s("drivers.remove", "block");
                    s("sysprep", "block"); s("phone", "block"); s("pin.open", "required"); s("pin.policy", "on"); break;
            }
            return p;
        }

        // ---------------- what is in force ----------------
        public static string OverrideFile;                          // self-test and previews
        public static Func<Dictionary<string, string>> OrgOverride; // self-test and previews: what an organization set
        static string File_ { get { return OverrideFile ?? Paths.FileIn("policy.txt"); } }

        public static bool UseCustom;
        public static Dictionary<string, string> Custom = Defaults();
        static bool loaded;
        public static event Action Changed;

        public static void Load()
        {
            UseCustom = false; Custom = Defaults(); orgCache = null;
            try
            {
                string f = File_;
                if (File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                    {
                        int i = line.IndexOf('='); if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                        if (k == "mode") UseCustom = v == "custom";
                        else if (k.StartsWith("custom.")) { var r = Rule(k.Substring(7)); if (r != null && r.Valid(v)) Custom[r.Key] = v; }
                    }
            }
            catch { }
            loaded = true;
        }
        static void Ensure() { if (!loaded) Load(); }

        // what an organization set (Group Policy / Intune): only known rules with allowed values count
        static Dictionary<string, string> orgCache;
        public static Dictionary<string, string> Org()
        {
            if (OrgOverride != null) return OrgOverride() ?? new Dictionary<string, string>();
            if (orgCache == null) orgCache = ReadOrg();
            return orgCache;
        }
        static Dictionary<string, string> ReadOrg()
        {
            var d = new Dictionary<string, string>();
            if (!Win.IsWindows) return d;
            try
            {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.OpenSubKey(@"SOFTWARE\Policies\Elixar"))
                    if (k != null)
                        foreach (var r in Rules) { var v = k.GetValue(r.Key) as string; if (v != null && r.Valid(v.Trim().ToLowerInvariant())) d[r.Key] = v.Trim().ToLowerInvariant(); }
            }
            catch { }
            return d;
        }

        public static string Get(string key)
        {
            Ensure();
            var r = Rule(key); if (r == null) return null;
            string v;
            if (Org().TryGetValue(key, out v)) return v;
            if (UseCustom && Custom.TryGetValue(key, out v) && r.Valid(v)) return v;
            return r.Default;
        }

        // where the rule in force comes from: organization, custom or default
        public static string Source(string key)
        {
            Ensure();
            if (Org().ContainsKey(key)) return "organization";
            return UseCustom ? "custom" : "default";
        }

        public static bool Allowed(string key) { return Get(key) != "block"; }

        public static Tier MaxTier { get { string v = Get("clean.level"); return v == "safe" ? Tier.Safe : v == "review" ? Tier.Review : Tier.Caution; } }

        // shown when something is not allowed; true = stop
        public static bool Blocked(IWin32Window owner, string key)
        {
            if (Allowed(key)) return false;
            var r = Rule(key);
            string who = Source(key) == "organization" ? Lang.T("your organization's policy") : Lang.T("the policy on this PC");
            Lang.Box(owner, Lang.T(r.Name) + ": " + Lang.T("not allowed by") + " " + who + ".\n\n" +
                     Lang.T(Source(key) == "organization" ? "Ask your IT department to change it." : "Change it in Policy manager (next to Logs)."),
                     "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        // the policy's word on the daily tidy and Undo settings (everything that reads them goes through this)
        public static void ApplyTo(TidyPrefs p)
        {
            if (p == null) return;
            int n;
            string u = Get("undo.days"); if (int.TryParse(u, out n)) p.UndoDays = n;
            string b = Get("bin.days"); if (b == "off") p.BinDays = 0; else if (int.TryParse(b, out n)) { p.BinDays = n; p.BinChoice = n; }
            string l = Get("low.clean"); if (l == "on") p.LowClean = true; else if (l == "off") p.LowClean = false;
        }
        public static bool Fixes(string key) { var v = Get(key); return v != null && v != "choice"; }

        // ---------------- saving (written down in Logs) ----------------
        public static string Save(bool useCustom, Dictionary<string, string> custom)
        {
            Ensure();
            var clean = Defaults();
            foreach (var kv in custom ?? new Dictionary<string, string>()) { var r = Rule(kv.Key); if (r != null && r.Valid(kv.Value)) clean[kv.Key] = kv.Value; }
            var before = Rules.ToDictionary(r => r.Key, r => Get(r.Key));
            bool wasCustom = UseCustom;
            var sb = new StringBuilder("# Elixar policy (Policy manager). Only administrators can change this file.\n");
            sb.Append("mode=").Append(useCustom ? "custom" : "default").Append('\n');
            foreach (var r in Rules) sb.Append("custom.").Append(r.Key).Append('=').Append(clean[r.Key]).Append('\n');
            try { File.WriteAllText(File_, sb.ToString()); } catch (Exception e) { return e.Message; }
            UseCustom = useCustom; Custom = clean; loaded = true;
            using (ChangeLog.In("Policy"))
            {
                if (wasCustom != useCustom) ChangeLog.Add("Settings", "Policy: switched to " + (useCustom ? "Custom" : "Default"), "Policy manager", wasCustom ? "Custom" : "Default", useCustom ? "Custom" : "Default", "", false);
                foreach (var r in Rules)
                {
                    string now = Get(r.Key);
                    if (before[r.Key] != now) ChangeLog.Add("Settings", "Policy: " + r.Name, "Policy manager", r.Label(before[r.Key]), r.Label(now), Source(r.Key) == "organization" ? "Set by your organization" : "", false);
                }
            }
            if (Changed != null) try { Changed(); } catch { }
            return null;
        }

        // ---------------- export / import ----------------
        static string Lines(Dictionary<string, string> rules) { return string.Join("\n", Rules.Select(r => r.Key + "=" + (rules.ContainsKey(r.Key) ? rules[r.Key] : r.Default))); }
        public static string Fingerprint(Dictionary<string, string> rules)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(Lines(rules)))).Replace("-", "").Substring(0, 16);
        }

        public static string ExportText(Dictionary<string, string> rules)
        {
            return "# Elixar policy - made with Elixar " + Updates.Current + " on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\r\n" +
                   "# Import it in Policy manager on another PC. Editing it by hand changes the fingerprint, and Elixar says so.\r\n" +
                   Lines(rules).Replace("\n", "\r\n") + "\r\nfingerprint=" + Fingerprint(rules) + "\r\n";
        }

        public class ImportResult { public Dictionary<string, string> Rules = Defaults(); public List<string> Problems = new List<string>(); public bool FingerprintOk, HadFingerprint; }

        // only known rules with allowed values are taken; anything else is listed and left out
        public static ImportResult Import(string text)
        {
            var res = new ImportResult();
            if (text == null || text.Length > 64 * 1024) { res.Problems.Add("Not an Elixar policy file."); return res; }
            string fp = null; int known = 0;
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int i = line.IndexOf('='); if (i <= 0) { res.Problems.Add("Not understood: " + Cut(line)); continue; }
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim().ToLowerInvariant();
                if (k == "fingerprint") { fp = line.Substring(i + 1).Trim(); continue; }
                var r = Rule(k);
                if (r == null) { res.Problems.Add("Unknown rule: " + Cut(k)); continue; }
                if (!r.Valid(v)) { res.Problems.Add(r.Name + ": \"" + Cut(v) + "\" is not allowed, kept the default"); continue; }
                res.Rules[k] = v; known++;
            }
            if (known == 0) res.Problems.Insert(0, "Not an Elixar policy file.");
            res.HadFingerprint = fp != null;
            res.FingerprintOk = fp != null && fp == Fingerprint(res.Rules) && res.Problems.Count == 0;
            return res;
        }
        static string Cut(string s) { return s.Length > 60 ? s.Substring(0, 60) + "..." : s; }

        // for IT: the same rules as a .reg file for HKLM\SOFTWARE\Policies\Elixar (Group Policy Preferences, Intune or regedit)
        public static string RegText(Dictionary<string, string> rules)
        {
            var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n; Elixar policy for every PC. Elixar shows these rules as \"Set by your organization\".\r\n[HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Elixar]\r\n");
            foreach (var r in Rules) sb.Append('"').Append(r.Key).Append("\"=\"").Append(rules.ContainsKey(r.Key) ? rules[r.Key] : r.Default).Append("\"\r\n");
            return sb.ToString();
        }

        // ---------------- does this PC follow it ----------------
        public static List<string> Check()
        {
            var l = new List<string>();
            if (Get("pin.open") == "required" && !PinLock.IsSet) l.Add("A PIN or password is required to open Elixar, but none is set.");
            if (Get("pin.policy") == "on" && !PinLock.IsSet) l.Add("A PIN or password is needed to change the policy, but none is set.");
            return l;
        }

        public static int Differences(Dictionary<string, string> rules) { return Rules.Count(r => rules.ContainsKey(r.Key) && rules[r.Key] != r.Default); }
    }
}
