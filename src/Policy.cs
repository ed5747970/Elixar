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
        // the value as Elixar writes it, whatever the letter case in a file or the registry ("sunday" -> "Sunday"); null if not allowed
        public string Canon(string v) { v = (v ?? "").Trim(); return Values.FirstOrDefault(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)); }
    }

    public static partial class Policy
    {
        static PolicyRule R(string key, string group, string name, string help, string def, params string[] valuesAndLabels)
        {
            var r = new PolicyRule { Key = key, Group = group, Name = name, Help = help, Default = def };
            r.Values = valuesAndLabels.Where((x, i) => i % 2 == 0).ToArray();
            r.Labels = valuesAndLabels.Where((x, i) => i % 2 == 1).ToArray();
            return r;
        }

        static readonly string[] Allow = { "allow", "Allowed", "block", "Not allowed" };

        // every hour of the day as value / label pairs, after an optional first choice ("Off")
        static string[] Times(string firstValue, string firstLabel, string labelPrefix)
        {
            var l = new List<string>();
            if (firstValue != null) { l.Add(firstValue); l.Add(firstLabel); }
            for (int m = 0; m < 24 * 60; m += 30) { string t = (m / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (m % 60).ToString("00", CultureInfo.InvariantCulture); l.Add(t); l.Add(labelPrefix + t); }
            return l.ToArray();
        }

        public static bool IsSchedule(string key) { return key.StartsWith("sched."); }

        // the Default values are exactly how Elixar works without a policy, so nothing changes until you choose a stricter rule
        public static readonly PolicyRule[] Rules = new[]
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
            R("apps.update", "What Elixar may change", "Update apps", "Apps > Updates: newer versions through Windows' package manager (winget).", "allow", Allow),
            R("startup.change", "What Elixar may change", "Change startup programs", "Tools > Startup: turn programs on or off, remove leftovers.", "allow", Allow),
            R("drivers.remove", "What Elixar may change", "Remove drivers", "Tools > Drivers: old and unused driver packages.", "allow", Allow),
            R("phone", "What Elixar may change", "Phone tools", "The Phone tab: clean, move files and remove apps on a phone.", "allow", Allow),
            R("sysprep", "What Elixar may change", "Sysprep and golden image tools", "Tools > Sysprep: fixes, answer files, image cleanup.", "allow", Allow),
            R("email", "What Elixar may change", "Email", "Connect an email account, reports and Gmail cleaning.", "allow", Allow),
            R("pin.open", "Access", "PIN or password to open Elixar", "Required: Elixar asks for one to be set and it can't be turned off.", "choice",
              "choice", "Your choice (Settings)", "required", "Required"),
            R("pin.policy", "Access", "PIN or password to change this policy", "Needed: changing the policy asks for the PIN or password first.", "off",
              "off", "Not needed", "on", "Needed"),
            R("sched.check", "Schedule: when each action runs", "Low-space check", "Warns when C: drops under the limit in Automation.", "off", Times("off", "Off", "Every day at ")),
            R("sched.logon", "Schedule: when each action runs", "Low-space check after sign-in", "A few minutes after you sign in to Windows.", "off", "off", "Off", "on", "On"),
            R("sched.clean.day", "Schedule: when each action runs", "Weekly clean", "The Safe items, ticked by default; open apps are skipped.", "off",
              "off", "Off", "Monday", "Every Monday", "Tuesday", "Every Tuesday", "Wednesday", "Every Wednesday", "Thursday", "Every Thursday", "Friday", "Every Friday", "Saturday", "Every Saturday", "Sunday", "Every Sunday"),
            R("sched.clean.time", "Schedule: when each action runs", "Weekly clean time", "When the weekly clean starts.", "20:00", Times(null, null, "")),
            R("sched.tidy", "Schedule: when each action runs", "Daily tidy time", "Old Recycle Bin items, expired Undo and the clean when C: is low (when they are on).", "12:00", Times(null, null, "")),
            R("sched.mail.time", "Schedule: when each action runs", "Email reports time", "Alerts are checked and reports sent at this time (when email reports are on).", "09:00", Times(null, null, "")),
            R("sched.mail.day", "Schedule: when each action runs", "Weekly email summary day", "The day the weekly summary is sent.", "Sunday",
              "Monday", "Monday", "Tuesday", "Tuesday", "Wednesday", "Wednesday", "Thursday", "Thursday", "Friday", "Friday", "Saturday", "Saturday", "Sunday", "Sunday"),
        }.Concat(Tweaks.All.Select(t => R(Tweaks.PolicyKey(t), "Windows tweaks", t.Name, t.Help, "choice",
              "choice", "Your choice (Tools > Windows tweaks)", "on", "Always on", "off", "Always Windows' default"))).ToArray();

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
                    foreach (var t in Tweaks.All) s(Tweaks.PolicyKey(t), "on");
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
        public static bool SchedulesMoved;   // the schedules set in Automation before they moved here were taken over once
        public static bool Moved { get { Ensure(); return SchedulesMoved; } }
        public static Dictionary<string, string> Custom = Defaults();
        static bool loaded;
        public static event Action Changed;

        public static void Load()
        {
            UseCustom = false; Custom = Defaults(); orgCache = null; SchedulesMoved = false;
            try
            {
                string f = File_;
                if (File.Exists(f))
                    foreach (var line in File.ReadAllLines(f))
                    {
                        int i = line.IndexOf('='); if (i <= 0) continue;
                        string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                        if (k == "mode") UseCustom = v == "custom";
                        else if (k == "schedules") SchedulesMoved = v == "moved";
                        else if (k.StartsWith("custom." + ItemPrefix)) { string ik = k.Substring(7), cv = CanonItem(ik, v); if (cv != null) Custom[ik] = cv; }
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
                    {
                        foreach (var r in Rules) { var v = r.Canon(k.GetValue(r.Key) as string); if (v != null) d[r.Key] = v; }
                        foreach (var n in k.GetValueNames().Where(x => x.StartsWith(ItemPrefix))) { var v = CanonItem(n, k.GetValue(n) as string); if (v != null) d[n] = v; }
                    }
            }
            catch { }
            return d;
        }

        public static string Get(string key)
        {
            Ensure();
            string v;
            if (IsItemKey(key))
            {
                if (Org().TryGetValue(key, out v)) return v;
                if (UseCustom && Custom.TryGetValue(key, out v)) return v;
                return ItemRule.DefaultText;
            }
            var r = Rule(key); if (r == null) return null;
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
            foreach (var kv in custom ?? new Dictionary<string, string>())
            {
                if (IsItemKey(kv.Key)) { string cv = CanonItem(kv.Key, kv.Value); if (cv != null && cv != ItemRule.DefaultText) clean[kv.Key] = cv; continue; }
                var r = Rule(kv.Key); if (r != null && r.Valid(kv.Value)) clean[kv.Key] = kv.Value;
            }
            var itemKeys = Custom.Keys.Concat(clean.Keys).Concat(Org().Keys).Where(IsItemKey).Distinct().ToList();
            var before = Rules.ToDictionary(r => r.Key, r => Get(r.Key));
            foreach (var ik in itemKeys) before[ik] = Get(ik);
            bool wasCustom = UseCustom;
            var sb = new StringBuilder("# Elixar policy (Policy manager). Only administrators can change this file.\n");
            sb.Append("mode=").Append(useCustom ? "custom" : "default").Append('\n');
            if (SchedulesMoved) sb.Append("schedules=moved\n");
            foreach (var r in Rules) sb.Append("custom.").Append(r.Key).Append('=').Append(clean[r.Key]).Append('\n');
            foreach (var kv in clean.Where(x => IsItemKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal)) sb.Append("custom.").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
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
                foreach (var ik in itemKeys)
                {
                    string now = Get(ik);
                    if (before[ik] != now) ChangeLog.Add("Settings", "Policy: " + ik.Substring(ItemPrefix.Length), "Policy manager", ItemRule.Parse(before[ik]).Words(), ItemRule.Parse(now).Words(), "Cleaning item", false);
                }
            }
            if (Changed != null) try { Changed(); } catch { }
            return null;
        }

        // ---------------- export / import ----------------
        static string Lines(Dictionary<string, string> rules)
        {
            var l = Rules.Select(r => r.Key + "=" + (rules.ContainsKey(r.Key) ? rules[r.Key] : r.Default)).ToList();
            l.AddRange(rules.Where(kv => IsItemKey(kv.Key) && kv.Value != ItemRule.DefaultText).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            return string.Join("\n", l);
        }
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
                string k = line.Substring(0, i).Trim(), rawV = line.Substring(i + 1).Trim(), v = rawV.ToLowerInvariant();
                if (k == "fingerprint") { fp = line.Substring(i + 1).Trim(); continue; }
                if (IsItemKey(k))
                {
                    string iv = CanonItem(k, rawV);
                    if (iv == null) { res.Problems.Add(Cut(k.Substring(ItemPrefix.Length)) + ": \"" + Cut(rawV) + "\" is not allowed, kept the default"); continue; }
                    res.Rules[k] = iv; known++; continue;
                }
                var r = Rule(k);
                if (r == null) { res.Problems.Add("Unknown rule: " + Cut(k)); continue; }
                string cv = r.Canon(rawV);
                if (cv == null) { res.Problems.Add(r.Name + ": \"" + Cut(v) + "\" is not allowed, kept the default"); continue; }
                res.Rules[k] = cv; known++;
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
            foreach (var kv in rules.Where(x => IsItemKey(x.Key) && x.Value != ItemRule.DefaultText).OrderBy(x => x.Key, StringComparer.Ordinal))
                sb.Append('"').Append(kv.Key.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"=\"").Append(kv.Value).Append("\"\r\n");
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

        // a time Windows' Task Scheduler gives back, as one of the policy's times (down to the half hour)
        public static string HalfHour(TimeSpan t) { int m = (int)t.TotalMinutes / 30 * 30; return (m / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (m % 60).ToString("00", CultureInfo.InvariantCulture); }

        // once, when this version first runs as administrator: the schedules set in Automation become the policy's schedule,
        // so nothing runs at another time than before (if they differ from Default, they go into Custom)
        public static bool MoveSchedules(Func<string, TaskState> query, string mailDay)
        {
            Ensure();
            if (SchedulesMoved) return false;
            Func<TaskState, string> at = t => t.Start.HasValue ? HalfHour(t.Start.Value.TimeOfDay) : null;
            var check = query(Automation.CheckTask); var logon = query(Automation.LogonTask); var clean = query(Automation.CleanTask);
            var tidy = query(Automation.TidyTask); var mail = query(Automation.MailTask);
            var found = new Dictionary<string, string>();
            if (check.Exists && at(check) != null) found["sched.check"] = at(check);
            if (logon.Exists) found["sched.logon"] = "on";
            if (clean.Exists && Automation.Days.Contains(clean.Day) && at(clean) != null) { found["sched.clean.day"] = clean.Day; found["sched.clean.time"] = at(clean); }
            if (tidy.Exists && at(tidy) != null) found["sched.tidy"] = at(tidy);
            if (mail.Exists && at(mail) != null) found["sched.mail.time"] = at(mail);
            if (Automation.Days.Contains(mailDay ?? "")) found["sched.mail.day"] = mailDay;
            var differ = found.Where(kv => Rule(kv.Key).Valid(kv.Value) && kv.Value != Rule(kv.Key).Default).ToList();
            SchedulesMoved = true;
            if (differ.Count == 0) { Save(UseCustom, Custom); return false; }
            var c = UseCustom ? new Dictionary<string, string>(Custom) : Defaults();
            foreach (var kv in differ) c[kv.Key] = kv.Value;
            using (ChangeLog.In("Policy"))
                ChangeLog.Add("Settings", "Policy: the schedules from Automation moved here", "Policy manager", "", string.Join(", ", differ.Select(kv => Rule(kv.Key).Name + " " + Rule(kv.Key).Label(kv.Value))),
                              "Times are now set in Policy manager. They are the same as before, so nothing runs at another time.", false);
            Save(true, c);
            return true;
        }

        public static int Differences(Dictionary<string, string> rules)
        {
            return Rules.Count(r => rules.ContainsKey(r.Key) && rules[r.Key] != r.Default) + rules.Count(kv => IsItemKey(kv.Key) && kv.Value != ItemRule.DefaultText);
        }
    }
}
