// Policy manager > Cleaning items: for every item in the Cleaner, what to do with it (ask me, clean it by itself on a schedule,
// or never touch it), when, and how old its files must be before Elixar deletes them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CDriveCleaner
{
    public class ItemRule
    {
        public string Do = "ask", When = "daily", Time = "03:00";
        public int Age;   // days; 0 = any age

        public static readonly string[] Dos = { "ask", "auto", "never" };
        public static readonly string[] DoLabels = { "Ask me (as now)", "Clean it by itself", "Never touch it" };
        public static readonly string[] Whens = { "daily", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "monthly" };
        public static readonly string[] WhenLabels = { "Every day", "Every Monday", "Every Tuesday", "Every Wednesday", "Every Thursday", "Every Friday", "Every Saturday", "Every Sunday", "Every month (on the 1st)" };
        public static readonly int[] Ages = { 0, 1, 7, 14, 30, 90 };
        public static readonly string[] AgeLabels = { "Any age", "Older than 1 day", "Older than 7 days", "Older than 14 days", "Older than 30 days", "Older than 90 days" };
        public const string DefaultText = "ask|daily|03:00|0";

        public override string ToString() { return Do + "|" + When + "|" + Time + "|" + Age.ToString(CultureInfo.InvariantCulture); }

        // "auto|Sunday|20:00|7"; null when anything in it isn't allowed
        public static ItemRule TryParse(string v)
        {
            var p = (v ?? "").Trim().Split('|');
            if (p.Length != 4) return null;
            string d = Dos.FirstOrDefault(x => string.Equals(x, p[0].Trim(), StringComparison.OrdinalIgnoreCase));
            string w = Whens.FirstOrDefault(x => string.Equals(x, p[1].Trim(), StringComparison.OrdinalIgnoreCase));
            string t = p[2].Trim(); int a;
            if (d == null || w == null || !int.TryParse(p[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out a) || !Ages.Contains(a)) return null;
            if (t.Length == 4) t = "0" + t;
            if (t.Length != 5 || t[2] != ':' || !char.IsDigit(t[0]) || !char.IsDigit(t[1]) || !char.IsDigit(t[3]) || !char.IsDigit(t[4])) return null;
            int h = int.Parse(t.Substring(0, 2), CultureInfo.InvariantCulture), m = int.Parse(t.Substring(3), CultureInfo.InvariantCulture);
            if (h > 23 || (m != 0 && m != 30)) return null;
            return new ItemRule { Do = d, When = w, Time = t, Age = a };
        }
        public static ItemRule Parse(string v) { return TryParse(v) ?? new ItemRule(); }

        // in plain words, for Logs and the policy
        public string Words()
        {
            string age = Age > 0 ? ", only files older than " + Age + (Age == 1 ? " day" : " days") : "";
            if (Do == "never") return "Never touch it";
            if (Do == "ask") return "Ask me" + age;
            return "Clean it by itself: " + WhenLabels[Array.IndexOf(Whens, When)].ToLowerInvariant() + " at " + Time + age;
        }

        // the last moment it was due, at or before now
        public DateTime LastDue(DateTime now)
        {
            var t = TimeSpan.ParseExact(Time, "hh\\:mm", CultureInfo.InvariantCulture);
            if (When == "daily") { var d = now.Date + t; return d <= now ? d : d.AddDays(-1); }
            if (When == "monthly") { var d = new DateTime(now.Year, now.Month, 1) + t; return d <= now ? d : new DateTime(now.Year, now.Month, 1).AddMonths(-1) + t; }
            var day = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), When);
            var w = now.Date.AddDays(-(((int)now.DayOfWeek - (int)day + 7) % 7)) + t;
            return w <= now ? w : w.AddDays(-7);
        }
    }

    public static partial class Policy
    {
        public const string ItemPrefix = "item:";
        public static bool IsItemKey(string k) { return k != null && k.StartsWith(ItemPrefix); }
        public static string ItemKey(string itemName) { return ItemPrefix + itemName; }

        // an item rule as Elixar writes it, or null; the item's name is plain text of reasonable length
        static string CanonItem(string key, string value)
        {
            string name = (key ?? "").Length > ItemPrefix.Length ? key.Substring(ItemPrefix.Length) : "";
            if (name.Length == 0 || name.Length > 120 || name.Any(c => char.IsControl(c) || c == '=')) return null;
            var r = ItemRule.TryParse(value);
            return r == null ? null : r.ToString();
        }

        public static ItemRule GetItem(string itemName) { return ItemRule.Parse(Get(ItemKey(itemName))); }

        // the policy at work on the Cleaner's items: an age limit wraps how the item measures and cleans
        public static List<CleanItem> Wrap(List<CleanItem> items)
        {
            foreach (var it in items)
            {
                ItemRule r;
                try { r = GetItem(it.Name); } catch { continue; }
                if (r.Age <= 0) continue;
                int age = r.Age;
                var measure = it.Measure; var clean = it.Clean;
                if (it.Name == "Recycle Bin" && it.Drive == null)
                {
                    // the Recycle Bin knows when each item was deleted: only what has been in it longer than the limit goes
                    it.Measure = () => { long total, old; RecycleAging.Stats(age, out total, out old); return old; };
                    it.Clean = log => { int n; RecycleAging.EmptyOld(age, log, out n); };
                    it.Info = (it.Info ?? "") + " Policy: only items deleted more than " + age + (age == 1 ? " day ago." : " days ago.");
                    continue;
                }
                if (measure != null) it.Measure = () => Fs.WithMinAge(age, measure);
                if (clean != null) it.Clean = log => Fs.WithMinAge(age, () => { clean(log); return 0L; });
                it.Info = (it.Info ?? "") + " Policy: only files older than " + age + (age == 1 ? " day." : " days.");
            }
            return items;
        }

        public static bool Never(CleanItem it) { try { return GetItem(it.Name).Do == "never"; } catch { return false; } }

        // ---------------- the Dashboard's folders, by their warning sign ----------------
        // which Cleaner item cleans each folder; null = Elixar never deletes it (Windows needs it, or they are your own files)
        public static readonly Dictionary<string, string> FolderItems = new Dictionary<string, string>
        {
            { "winold", "Previous Windows installation (Windows.old)" }, { "winbt", "Previous Windows installation (Windows.old)" },
            { "getcurrent", "Windows upgrade media ($GetCurrent)" }, { "winreagent", "Recovery update leftovers ($WinREAgent)" },
            { "adobearm", "Adobe Reader old updates" }, { "winsxs", "Windows component cleanup (DISM)" }, { "logs", "Old Windows servicing logs" },
            { "downloads", DownloadsItem }, { "recycle", "Recycle Bin" },
            { "nvdx", "NVIDIA shader cache" }, { "stremio", "Stremio cache" }, { "usertemp", "Your temp files" }, { "wintemp", "Windows temp files" },
            { "wudl", "Windows Update downloads" }, { "dosvc", "Delivery Optimization cache" }, { "wer", "Error reports" }, { "minidump", "Crash dumps" },
            { "memdump", "Crash dumps" }, { "thumbs", "Thumbnail cache" }, { "d3d", "DirectX shader cache" }, { "chrome", "Chrome cache" }, { "edge", "Edge cache" },
        };

        // why a folder has no rule
        public static string FolderFixed(string id, string tier)
        {
            if (tier == "critical") return "Never deleted: Windows and your apps need it";
            if (id == "hiberfil") return "Not a schedule: shrink or turn it off in Tools > System space";
            if (id == "videos" || id == "desktop") return "Your own files: Elixar never deletes them";
            return "Elixar doesn't clean this folder";
        }

        // Downloads: your own files, so only "move what is older than N days to Undo" (never deleted straight away, never newer files)
        public const string DownloadsItem = "Downloads folder (files older than the limit, to Undo)";
        public static readonly int[] DownloadAges = { 7, 14, 30, 90 };

        public static List<CleanItem> ExtraItems()
        {
            var l = new List<CleanItem>();
            string dl = null;
            try { dl = Path.Combine(UserDirs.Home, "Downloads"); } catch { }
            if (dl == null) return l;
            var r = GetItem(DownloadsItem);
            int age = Math.Max(7, r.Age);
            l.Add(new CleanItem
            {
                Name = DownloadsItem, Tier = Tier.Review, Where = dl,
                Info = "Moves what has been in Downloads longer than the limit to Undo. Newer files stay.",
                Measure = () => Fs.WithMinAge(age, () => Fs.MeasureIn(dl)),
                Clean = log => Fs.WithMinAge(age, () => { var keep = TidyPrefs.Load().UndoDays; Quarantine.MoveToUndo("Downloads older than " + age + " days", Fs.Children(dl, "*"), log, keep); return 0L; })
            });
            return l;
        }

        // ---------------- items that clean by themselves ----------------
        public static string RunsFile { get { return Paths.FileIn("policy-cleaning.txt"); } }
        public static string RunsOverride;   // self-test

        public static Dictionary<string, DateTime> ReadRuns()
        {
            var d = new Dictionary<string, DateTime>();
            try
            {
                string f = RunsOverride ?? RunsFile;
                if (!File.Exists(f)) return d;
                foreach (var line in File.ReadAllLines(f))
                {
                    int i = line.LastIndexOf('\t'); if (i <= 0) continue;
                    DateTime t;
                    if (DateTime.TryParse(line.Substring(i + 1), CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) d[line.Substring(0, i)] = t;
                }
            }
            catch { }
            return d;
        }
        static void WriteRuns(Dictionary<string, DateTime> d)
        {
            try { File.WriteAllLines(RunsOverride ?? RunsFile, d.Select(kv => kv.Key.Replace('\t', ' ') + "\t" + kv.Value.ToString("s", CultureInfo.InvariantCulture))); } catch { }
        }

        // which items are due now; an item seen for the first time waits for its next time (it doesn't run the moment you set it)
        public static List<CleanItem> Due(List<CleanItem> items, DateTime now, Dictionary<string, DateTime> runs, out bool firstSeen)
        {
            firstSeen = false;
            var due = new List<CleanItem>();
            foreach (var it in items)
            {
                var r = GetItem(it.Name);
                if (r.Do != "auto" || it.RootScript != null || it.Tier > MaxTier) continue;
                DateTime last;
                if (!runs.TryGetValue(it.Name, out last)) { runs[it.Name] = now; firstSeen = true; continue; }
                if (last < r.LastDue(now)) due.Add(it);
            }
            return due;
        }

        public static bool AnyAuto()
        {
            Ensure();
            var keys = (UseCustom ? Custom.Keys : Enumerable.Empty<string>()).Concat(Org().Keys).Where(IsItemKey).Distinct();
            return keys.Any(k => ItemRule.Parse(Get(k)).Do == "auto");
        }

        // the scheduled run (every half hour, as administrator): cleans the items that are due, skips any whose app is open
        public static int RunDue(Action<string> log)
        {
            if (Win.IsWindows && !Sys.IsAdmin()) { if (log != null) log("Policy cleaning skipped: it needs administrator rights."); return 1; }
            var now = DateTime.Now;
            var runs = ReadRuns();
            bool first;
            var due = Due(Catalog.Build().Concat(ExtraItems()).ToList(), now, runs, out first);
            long total = 0; int done = 0;
            using (ChangeLog.In("Policy cleaning"))
                foreach (var it in due)
                {
                    if (it.Procs.Length > 0 && Sys.FindProcesses(it.Procs).Count > 0) { if (log != null) log("  Skipped " + it.Name + " (the app is open; it is never closed automatically). It runs next time."); continue; }
                    if (log != null) log("Policy cleaning: " + it.Name + "...");
                    long before = Sys.Free(); string problem = null;
                    try { it.Clean(m => { if (log != null) log(m); }); } catch (Exception e) { problem = e.Message; if (log != null) log("  Problem: " + e.Message); }
                    long got = Math.Max(0, Sys.Free() - before); total += got; done++;
                    ChangeLog.Cleaned(it, got, problem, "Cleaned by the policy (" + GetItem(it.Name).Words() + ")");
                    runs[it.Name] = now;
                }
            if (done > 0 || first) WriteRuns(runs);
            if (done > 0) using (ChangeLog.In("Policy cleaning")) { try { Activity.Add("clean", "Policy cleaning: " + done + (done == 1 ? " item, freed " : " items, freed ") + Fs.Fmt(total), total); } catch { } }
            return 0;
        }
    }
}
