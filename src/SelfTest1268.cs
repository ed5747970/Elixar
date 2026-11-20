// Self-test for 1.2.68: the growing-folders check that keeps what it finished, the space-drop alert, SecureAPlus's stuck
// uploads, your choice for a fast-growing folder (Elixar for Private), and Elixar's own sounds.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class SelfTest1268
    {
        static void Pump(Func<bool> done, int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (!done() && DateTime.Now < end) { Application.DoEvents(); Thread.Sleep(15); }
        }

        static void Run(Action<bool, string> check, string name, Func<string> test)
        {
            string why;
            try { why = test(); }
            catch (Exception ex)
            {
                var at = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Contains("CDriveCleaner.")).Take(4);
                why = "ERROR " + ex.GetType().Name + ": " + ex.Message + " @ " + string.Join(" < ", at);
            }
            check(why == null, name + (why == null ? "" : "  (" + why + ")"));
        }

        const long KB = 1024, MB = 1L << 20, GB = 1L << 30;

        static void Files(string dir, string prefix, string ext, int n, long size, DateTime oldest)
        {
            Directory.CreateDirectory(dir);
            for (int i = 0; i < n; i++)
            {
                string f = Path.Combine(dir, prefix + i.ToString("D2") + ext);
                File.WriteAllBytes(f, new byte[size]);
                File.SetLastWriteTime(f, oldest.AddHours(i));
            }
        }

        public static void All(string root, Action<bool, string> check)
        {
            string R = Path.Combine(root, "t1268"); Directory.CreateDirectory(R);

            // ---------- the growing-folders check keeps what it finished ----------
            Run(check, "Growing fast, the daily reading: ProgramData and Windows first; a place that runs out of time is left out (its folders never look new), the rest is kept; 2 days in a row is told to you and shown in PC health", () =>
            {
                var roots = Win.IsWindows ? FolderGrowth.Roots() : null;
                if (roots != null)
                {
                    string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    if (roots.Count == 0 || !string.Equals(roots[0][0], pd, StringComparison.OrdinalIgnoreCase)) return "ProgramData isn't measured first: " + (roots.Count > 0 ? roots[0][0] : "none");
                    if (!string.Equals(roots.Last()[0], UserDirs.Home, StringComparison.OrdinalIgnoreCase)) return "your profile isn't last";
                }
                string a = Path.Combine(R, "placeA"), b = Path.Combine(R, "placeB"), od = Path.Combine(R, "growdata");
                Directory.CreateDirectory(od);
                Files(Path.Combine(a, "AppOne", "logs"), "x", ".log", 3, 300 * KB, DateTime.Now.AddDays(-1));
                Files(Path.Combine(b, "Big"), "y", ".bin", 2, 300 * KB, DateTime.Now.AddDays(-1));
                FolderGrowth.OverrideDir = od;
                FolderGrowth.RootsOverride = () => new List<string[]> { new[] { a, "4" }, new[] { b, "4" } };
                long keepMin = 0;
                try
                {
                    FolderGrowth.SlowRootForTest = r => r == b;
                    var rd = FolderGrowth.MeasureAll(TimeSpan.FromMinutes(1));
                    if (rd.Sizes == null || rd.Done.Count != 1 || rd.Done[0] != a || rd.Missed.Count != 1 || rd.Missed[0] != b) return "partial reading: done " + rd.Done.Count + ", missed " + rd.Missed.Count;
                    if (rd.Sizes.Keys.Any(k => k.StartsWith(b))) return "a place that ran out of time was kept";
                    FolderGrowth.SlowRootForTest = r => true;
                    if (FolderGrowth.MeasureAll(TimeSpan.FromMinutes(1)).Sizes != null) return "nothing measured, yet a reading";

                    // a week ago only A was measured; today both: B's folder isn't "new", A's folder that grew is found
                    string aBig = Path.Combine(a, "AppOne"), bBig = Path.Combine(b, "Big");
                    var d0 = DateTime.Today.AddDays(-7);
                    FolderGrowth.SaveSnap(d0, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { { aBig, 1 * GB } }, new List<string> { a });
                    FolderGrowth.SaveSnap(DateTime.Today, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { { aBig, 3 * GB }, { bBig, 5 * GB } }, null);
                    var snaps = FolderGrowth.ReadSnaps(); var done = FolderGrowth.ReadDone();
                    if (snaps[d0].Count != 1 || !done.ContainsKey(d0) || done.ContainsKey(DateTime.Today)) return "marks: " + snaps[d0].Count + " / " + done.Count;
                    var fast = FolderGrowth.Detect(snaps, DateTime.Today, done);
                    if (fast.Count != 1 || fast[0].Path != aBig) return "found: " + string.Join(", ", fast.Select(x => x.Path));
                    if (!FolderGrowth.Detect(snaps, DateTime.Today).Any(x => x.Path == bBig)) return "without the marks B would look new (the test is wrong)";

                    // the daily run: 2 days in a row that couldn't finish B -> told, and in PC health
                    File.Delete(Path.Combine(od, "readings-" + (Win.IsWindows && Sys.IsAdmin() ? "admin" : "user") + ".txt"));
                    FolderGrowth.SlowRootForTest = r => r == b;
                    var r1 = FolderGrowth.DailyRun(DateTime.Today.AddDays(-1), null, null);
                    if (r1.Missed.Count != 1 || r1.CheckProblem != null) return "day 1: " + r1.Missed.Count + " / " + r1.CheckProblem;
                    var r2 = FolderGrowth.DailyRun(DateTime.Today, null, null);
                    if (r2.CheckProblem == null || !r2.CheckProblem.Contains("2 days in a row")) return "day 2: " + r2.CheckProblem;
                    if (!FolderGrowth.CheckAlertDue(DateTime.Today) || FolderGrowth.CheckAlertDue(DateTime.Today)) return "told once a week";
                    if (FolderGrowth.ReadDone().Count != 2) return "the finished place wasn't kept: " + FolderGrowth.ReadDone().Count;
                    var f = new HealthFacts { Free = 50 * GB, Total = 200 * GB, GrowthCheckStuck = true };
                    var h = Health.Evaluate(f, DateTime.Now, new string[0]).Items.FirstOrDefault(i => i.Key == "growcheck");
                    if (h == null || h.Act != HAct.Growing || h.Penalty != 0) return "PC health";
                    // a day that finishes ends it
                    FolderGrowth.SlowRootForTest = null;
                    var r3 = FolderGrowth.DailyRun(DateTime.Today.AddDays(1), null, null);
                    if (r3.CheckProblem != null || r3.Missed.Count != 0) return "a day that finished: " + r3.CheckProblem;
                    if (FolderGrowth.Budget < TimeSpan.FromMinutes(10)) return "the reading's time: " + FolderGrowth.Budget;
                    keepMin = 1;
                }
                finally { FolderGrowth.SlowRootForTest = null; FolderGrowth.RootsOverride = null; FolderGrowth.OverrideDir = null; }
                return keepMin == 1 ? null : "not finished";
            });

            // ---------- C: lost a lot in a few days ----------
            Run(check, "Space-drop alert: 10 GB or more lost in 2 days or a week is told at once (at most every 3 days), naming the folder that took it", () =>
            {
                var today = DateTime.Today;
                Func<int, long, SpaceHistory.Point> pt = (ago, free) => new SpaceHistory.Point { Day = today.AddDays(-ago), Free = free, Total = 500 * GB };
                int days;
                if (SpaceHistory.Drop(new List<SpaceHistory.Point> { pt(7, 100 * GB), pt(0, 85 * GB) }, today, out days) != 15 * GB || days != 7) return "a week: " + days;
                if (SpaceHistory.Drop(new List<SpaceHistory.Point> { pt(7, 480 * GB), pt(0, 460 * GB) }, today, out days) != 0) return "lots still free and years to go: told anyway";
                if (SpaceHistory.Drop(new List<SpaceHistory.Point> { pt(2, 60 * GB), pt(0, 49 * GB) }, today, out days) != 11 * GB || days != 2) return "2 days";
                if (SpaceHistory.Drop(new List<SpaceHistory.Point> { pt(7, 100 * GB), pt(0, 95 * GB) }, today, out days) != 0) return "5 GB isn't fast";
                var other = pt(7, 100 * GB); other.Total = 900 * GB;
                if (SpaceHistory.Drop(new List<SpaceHistory.Point> { other, pt(0, 50 * GB) }, today, out days) != 0) return "another drive size (a new disk) counted";
                if (SpaceHistory.Drop(new List<SpaceHistory.Point> { pt(7, 100 * GB) }, today, out days) != 0) return "no reading today";
                string hf = Path.Combine(R, "space-1268.txt");
                File.WriteAllText(hf, today.AddDays(-7).ToString("yyyy-MM-dd") + "\t" + (100 * GB) + "\t" + (500 * GB) + "\n" + today.ToString("yyyy-MM-dd") + "\t" + (46 * GB) + "\t" + (500 * GB) + "\n");
                SpaceHistory.OverrideFile = hf;
                try
                {
                    string n = SpaceHistory.DropNotice(today, @"Whitelist\to be transferred", 54 * GB);
                    if (n == null || !n.Contains("lost 54") || !n.Contains("Most of it: Whitelist") || !n.Contains("in 7 days")) return "notice: " + n;
                    File.Delete(hf + ".drop-alerted");
                    string n2 = SpaceHistory.DropNotice(today, "Zoom\\logs", 2 * GB);
                    if (n2 == null || n2.Contains("Most of it") || !n2.Contains("grew most")) return "a small folder called most of it: " + n2;
                    if (SpaceHistory.DropNotice(today.AddDays(1), null, 0) != null) return "told again the next day";
                    if (SpaceHistory.DropNotice(today.AddDays(3), null, 0) == null && SpaceHistory.Drop(SpaceHistory.Read(), today.AddDays(3), out days) > 0) return "not told after 3 days";
                }
                finally { SpaceHistory.OverrideFile = null; }
                return null;
            });

            // ---------- SecureAPlus's stuck uploads ----------
            Run(check, "SecureAPlus's stuck uploads (ProgramData\\SecureAge Technology\\...\\to be transferred): explained, never called unusual, and not deleted unless you choose it", () =>
            {
                string sap = Path.Combine(R, "ProgramData", "SecureAge Technology", "SecureAge", "Whitelist", "to be transferred");
                Files(sap, "20261114101018_zbFJzp6i-446710_whitelistmissing", ".txt", 6, 200 * KB, DateTime.Now.AddDays(-6));
                var names = new List<string> { "SecureAPlus", "SecureAge Technology" };
                var s = FolderGrowth.Study(sap, names, DateTime.Now);
                if (s.Kind != GrowKind.Look || s.Known != "secureaplus" || !s.Verdict.Contains("stuck") || !s.Owner.StartsWith("SecureAPlus")) return s.Kind + " / " + s.Known + " / " + s.Verdict;
                var e = Explain.For(@"C:\ProgramData\SecureAge Technology\SecureAge\Whitelist\to be transferred", null, null);
                if (e == null || e.Title != "SecureAPlus uploads" || e.Tier != "caution") return "the space map: " + (e == null ? "none" : e.Title + " / " + e.Tier);
                if (Directory.GetFiles(sap).Length != 6) return "something was deleted";
                return null;
            });

            // ---------- your choice for a fast-growing folder ----------
            string place = Path.Combine(R, "ProgramData");
            Func<string, string> placeOk = p => p.StartsWith(place + Path.DirectorySeparatorChar) ? null : "Elixar only deletes on a schedule in an app's folder (in ProgramData or AppData).";
#if !BUSINESS
            Run(check, "Your choice (Elixar for Private): only for an app's folder in ProgramData or AppData; never your files, Windows' folders or an unusual folder; a cap keeps the newest file and never deletes programs", () =>
            {
                string od = Path.Combine(R, "rules1"); Directory.CreateDirectory(od);
                FolderGrowth.OverrideDir = od; FolderGrowth.ChoicePlaceOverride = placeOk;
                try
                {
                    string app = Path.Combine(place, "SomeApp", "uploads");
                    Files(app, "u", ".dat", 10, 100 * KB, DateTime.Now.AddDays(-20));
                    var look = new FolderStudy { Path = app, Kind = GrowKind.Look };
                    if (FolderGrowth.ChoiceProblem(look) != null) return "an app's folder: " + FolderGrowth.ChoiceProblem(look);
                    foreach (var k in new[] { GrowKind.Unusual, GrowKind.Windows, GrowKind.Yours })
                        if (FolderGrowth.ChoiceProblem(new FolderStudy { Path = app, Kind = k }) == null) return "offered for " + k;
                    if (FolderGrowth.ChoiceProblem(new FolderStudy { Path = Path.Combine(R, "elsewhere", "x"), Kind = GrowKind.Look }) == null) return "offered outside ProgramData and AppData";
                    FolderGrowth.ChoicePlaceOverride = null;
                    if (FolderGrowth.ChoiceProblem(new FolderStudy { Path = app + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "uploads", Kind = GrowKind.Look }) == null) return "a path with .. was allowed";
                    FolderGrowth.ChoicePlaceOverride = placeOk;
                    var places = new[] { place, Path.Combine(R, "Local") };
                    if (FolderGrowth.PlaceProblem(Path.Combine(place, "App", "logs"), places) != null) return "an app's folder";
                    foreach (var bad in new[] { place, Path.Combine(R, "Local", "Microsoft", "Protect"), Path.Combine(place, "microsoft", "Windows Defender"), Path.Combine(R, "Local", "Elixar", "growing"), Path.Combine(R, "Localx", "App") })
                        if (FolderGrowth.PlaceProblem(bad, places) == null) return "allowed: " + bad;
                    // your own kinds of files stay even inside an app's folder
                    string mix = Path.Combine(place, "MailApp", "store"); Files(mix, "m", ".pst", 2, 100 * KB, DateTime.Now.AddDays(-60)); Files(mix, "k", ".kdbx", 1, 100 * KB, DateTime.Now.AddDays(-60)); Files(mix, "t", ".tmp", 3, 100 * KB, DateTime.Now.AddDays(-60));
                    long tt; var md = FolderGrowth.CapFiles(new KeepRule { Path = mix, Mode = "cap", Cap = 400 * KB, Down = 0 }, out tt);
                    if (md.Count != 2 || md.Any(x => !x.EndsWith(".tmp"))) return "your files in an app's folder: " + string.Join(",", md.Select(Path.GetFileName));
                    if (FolderGrowth.RuleFiles(new KeepRule { Path = mix, Mode = "all", Days = 3 }, DateTime.Now).Any(x => !x.EndsWith(".tmp"))) return "older than: your files";
                    if (FolderGrowth.AddChoice(new FolderStudy { Path = app, Kind = GrowKind.Unusual }, "cap", 14, 2 * GB, 1 * GB) == null) return "a rule for an unusual folder";
                    if (FolderGrowth.AddChoice(look, "cap", 14, 512 * MB, 0) == null) return "a cap under 1 GB";
                    if (FolderGrowth.AddChoice(look, "cap", 14, 2 * GB, 2 * GB) == null) return "back down to the limit itself";
                    if (FolderGrowth.AddChoice(look, "cap", 14, 20 * GB, 2 * GB) != null) return "a cap";
                    var r = FolderGrowth.Rules().Single();
                    if (r.Mode != "cap" || r.Cap != 20 * GB || r.Down != 2 * GB || !FolderGrowth.RuleText(r).Contains("20")) return "saved: " + r.Mode + " " + r.Cap;
                    // a line someone changed to a tiny cap deletes nothing
                    File.AppendAllText(Path.Combine(od, "keep-in-check.txt"), "14\t0\t2026-11-20\t" + Path.Combine(place, "Other") + "\tcap\t10\t0\t-\n");
                    if (FolderGrowth.Rules().Count != 1) return "a tiny cap was read";

                    // what a cap deletes: the oldest, never the newest file, never a program
                    File.WriteAllBytes(Path.Combine(app, "helper.exe"), new byte[500 * KB]); File.SetLastWriteTime(Path.Combine(app, "helper.exe"), DateTime.Now.AddDays(-90));
                    long total;
                    var del = FolderGrowth.CapFiles(new KeepRule { Path = app, Mode = "cap", Cap = 600 * KB, Down = 300 * KB }, out total);
                    if (total != 1500 * KB || del.Count != 9 || del.Any(x => x.EndsWith(".exe") || x.EndsWith("u09.dat"))) return "with a program: " + del.Count + " of " + total;
                    File.Delete(Path.Combine(app, "helper.exe"));
                    // drivers, shortcuts and scripts aren't deleted either
                    foreach (var x in new[] { "drv.sys", "go.lnk", "set.reg", "mod.psm1" }) { File.WriteAllBytes(Path.Combine(app, x), new byte[10]); File.SetLastWriteTime(Path.Combine(app, x), DateTime.Now.AddDays(-99)); }
                    if (FolderGrowth.CapFiles(new KeepRule { Path = app, Mode = "cap", Cap = 1, Down = 0 }, out total).Any(x => x.EndsWith(".sys") || x.EndsWith(".lnk") || x.EndsWith(".reg") || x.EndsWith(".psm1"))) return "a driver, shortcut or script would be deleted";
                    foreach (var x in new[] { "drv.sys", "go.lnk", "set.reg", "mod.psm1" }) File.Delete(Path.Combine(app, x));
                    del = FolderGrowth.CapFiles(new KeepRule { Path = app, Mode = "cap", Cap = 600 * KB, Down = 300 * KB }, out total);
                    if (del.Count != 8 || !del[0].EndsWith("u00.dat") || del.Any(x => x.EndsWith("u08.dat") || x.EndsWith("u09.dat"))) return "oldest first, down to the limit: " + string.Join(",", del.Select(Path.GetFileName));
                    if (FolderGrowth.CapFiles(new KeepRule { Path = app, Mode = "cap", Cap = 2 * MB, Down = 300 * KB }, out total).Count != 0) return "under the limit";
                    // over the limit only because of your own files: nothing deleted
                    string pics = Path.Combine(place, "PhotoApp", "store"); Files(pics, "p", ".jpg", 3, 400 * KB, DateTime.Now.AddDays(-30)); Files(pics, "c", ".cache", 3, 100 * KB, DateTime.Now.AddDays(-30));
                    if (FolderGrowth.CapFiles(new KeepRule { Path = pics, Mode = "cap", Cap = 1 * MB, Down = 0 }, out total).Count != 0) return "deleted because your own files are big";

                    // the daily run: deleted for good, in Logs, and you're told
                    FolderGrowth.RemoveRule(app);
                    File.WriteAllText(Path.Combine(od, "keep-in-check.txt"), "14\t0\t2026-11-20\t" + app + "\tcap\t" + (600 * KB) + "\t" + (300 * KB) + "\t-\n");
                    var raw = FolderGrowth.Rules();
                    if (raw.Count != 0) return "a cap under 1 GB from the file";
                    // (the run itself, with a cap the file can't hold: called directly)
                    FolderGrowth.RunNotes.Clear();
                    long freed;
                    using (ChangeLog.In("Keep in check")) freed = FolderGrowth.RunRule(new KeepRule { Path = app, Mode = "cap", Cap = 600 * KB, Down = 300 * KB }, DateTime.Now, null);
                    var left = Directory.GetFiles(app).Select(Path.GetFileName).OrderBy(x => x).ToList();
                    if (freed != 800 * KB || left.Count != 2 || left[1] != "u09.dat") return "after the run: " + freed / KB + " KB, left " + string.Join(",", left);
                    if (!ChangeLog.Read().Any(x => x.Action == "Capped a fast-growing folder (your choice)")) return "not in Logs";
                    if (FolderGrowth.RunNotes.Count != 1 || !FolderGrowth.RunNotes[0][0].StartsWith("Your rule freed")) return "not told: " + FolderGrowth.RunNotes.Count;
                    // a choice rule outside an app's folder is skipped, even when the file says so
                    FolderGrowth.RunNotes.Clear();
                    string mine = Path.Combine(R, "elsewhere", "docs"); Files(mine, "d", ".docx", 5, 100 * KB, DateTime.Now.AddDays(-50));
                    if (FolderGrowth.RunRule(new KeepRule { Path = mine, Mode = "cap", Cap = 100 * KB, Down = 0 }, DateTime.Now, null) != 0 || Directory.GetFiles(mine).Length != 5) return "deleted outside an app's folder";
                    if (FolderGrowth.RunRule(new KeepRule { Path = mine, Mode = "all", Days = 3 }, DateTime.Now, null) != 0 || Directory.GetFiles(mine).Length != 5) return "\"older than\" outside an app's folder";

                    // "older than N days" for an app's folder: every kind of file except programs, the first times to Undo
                    string old = Path.Combine(place, "OtherApp", "data");
                    Files(old, "o", ".dat", 4, 10 * KB, DateTime.Now.AddDays(-40)); Files(old, "n", ".dat", 2, 10 * KB, DateTime.Now.AddHours(-3));
                    File.WriteAllBytes(Path.Combine(old, "tool.dll"), new byte[10]); File.SetLastWriteTime(Path.Combine(old, "tool.dll"), DateTime.Now.AddDays(-90));
                    if (FolderGrowth.AddChoice(new FolderStudy { Path = old, Kind = GrowKind.Look }, "age", 14, 0, 0) != null) return "older than";
                    var ar = FolderGrowth.Rules().First(x => x.Path == old);
                    var files = FolderGrowth.RuleFiles(ar, DateTime.Now);
                    if (ar.Mode != "all" || files.Count != 4 || files.Any(x => x.EndsWith(".dll"))) return "older than: " + ar.Mode + " " + files.Count;
                    // the same choice for a "safe" folder stays the log, cache and temp kind
                    if (FolderGrowth.AddChoice(new FolderStudy { Path = old, Kind = GrowKind.Safe }, "age", 14, 0, 0) != null || FolderGrowth.Rules().First(x => x.Path == old).Mode != "age") return "safe folder";
                    return null;
                }
                finally { FolderGrowth.OverrideDir = null; FolderGrowth.ChoicePlaceOverride = null; }
            });

            Run(check, "Growing fast window (Elixar for Private): \"What should Elixar do?\" for an app's folder, asks before saving, a cap is done once at once; nothing to choose for an unusual folder", () =>
            {
                string od = Path.Combine(R, "rules2"); Directory.CreateDirectory(od);
                FolderGrowth.OverrideDir = od; FolderGrowth.ChoicePlaceOverride = placeOk;
                try
                {
                    string app = Path.Combine(place, "WinApp", "cache");
                    Files(app, "c", ".bin", 3, 100 * KB, DateTime.Now.AddDays(-3));
                    string odd = Path.Combine(place, "x7f3k9q");
                    var look = new FolderStudy { Path = app, Kind = GrowKind.Look, Owner = "WinApp", Inside = "3 files", Verdict = "Look", Bytes = 3 * GB };
                    var weird = new FolderStudy { Path = odd, Kind = GrowKind.Unusual, Owner = "No installed app owns it", Inside = "-", Verdict = "Scan" };
                    using (var gf = new GrowingForm(true, s => s, null, null, null))
                    {
                        var handle = gf.Handle;   // so the window's "done" can be posted to it
                        string asked = null; bool answer = false;
                        gf.ConfirmForTest = q => { asked = q; return answer; };
                        gf.ShowStudies(new List<Tuple<FastFolder, FolderStudy>> { Tuple.Create(new FastFolder { Path = app, Now = 3 * GB, Grew = 2 * GB, Days = 7 }, look),
                                                                                   Tuple.Create(new FastFolder { Path = odd, Now = 2 * GB, Grew = 2 * GB, Days = 2 }, weird) });
                        if (!gf.ChoiceBoxes.ContainsKey(app) || gf.ChoiceBoxes.ContainsKey(odd)) return "choices: " + gf.ChoiceBoxes.Count;
                        var cb = gf.ChoiceBoxes[app];
                        if (!cb.Tell.Checked) return "\"Just tell me\" isn't the start";
                        cb.Cap.Checked = true; cb.CapGb.Value = 2; cb.DownGb.Value = 1;
                        if (cb.Tell.Checked || !cb.Cap.Checked) return "the choices aren't one group";
                        gf.SaveChoice(look, cb, new Panel());
                        if (asked == null || !asked.Contains("Cap this folder at 2") || !asked.Contains("nothing is deleted today") || !asked.Contains("may lose data") || FolderGrowth.Rules().Count != 0) return "asked: " + asked;
                        answer = true; bool capDone = false; gf.CapDone += () => capDone = true;
                        gf.SaveChoice(look, cb, new Panel());
                        Pump(() => capDone, 10000);
                        var r = FolderGrowth.Rules().SingleOrDefault();
                        if (r == null || r.Mode != "cap" || r.Cap != 2 * GB || r.Down != 1 * GB || !cb.Saved.Text.StartsWith("Saved") || !capDone) return "saved: " + (r == null ? "none" : r.Mode) + " / " + cb.Saved.Text + " / " + capDone;
                        cb.DownGb.Value = 2;
                        gf.SaveChoice(look, cb, new Panel());
                        if (!gf.StatusText.Contains("smaller than the limit")) return "down to the limit: " + gf.StatusText;
                        cb.Tell.Checked = true; gf.SaveChoice(look, cb, new Panel());
                        if (FolderGrowth.Rules().Count != 0) return "\"just tell me\" kept the rule";
                        var card = new Panel(); cb.Ignore.Checked = true; gf.SaveChoice(look, cb, card);
                        if (card.Visible || !FolderGrowth.Ignored().Contains(app)) return "ignore";
                        if (Directory.GetFiles(app).Length != 3) return "files went";
                        // the rules can't be saved (not an administrator, say): a message, never a crash
                        string blockedDir = Path.Combine(R, "not-a-folder"); File.WriteAllText(blockedDir, "x");
                        FolderGrowth.OverrideDir = blockedDir;
                        cb.Cap.Checked = true; cb.CapGb.Value = 5; cb.DownGb.Value = 1;
                        gf.SaveChoice(look, cb, new Panel());
                        if (!gf.StatusText.StartsWith("Not saved")) return "a save that failed: " + gf.StatusText;
                        FolderGrowth.OverrideDir = od;
                        // SecureAPlus's folder starts at its own numbers: 20 GB, back down to 2 GB
                        var sapS = new FolderStudy { Path = Path.Combine(place, "SecureAge Technology", "SecureAge", "Whitelist", "to be transferred"), Kind = GrowKind.Look, Known = "secureaplus", Owner = "SecureAPlus", Inside = "-", Verdict = "stuck", Bytes = 54 * GB };
                        Directory.CreateDirectory(sapS.Path);
                        gf.ShowStudies(new List<Tuple<FastFolder, FolderStudy>> { Tuple.Create(new FastFolder { Path = sapS.Path, Now = 54 * GB, Grew = 54 * GB, Days = 7 }, sapS) });
                        var sb = gf.ChoiceBoxes[sapS.Path];
                        if (sb.CapGb.Value != 20 || sb.DownGb.Value != 2) return "SecureAPlus numbers: " + sb.CapGb.Value + "/" + sb.DownGb.Value;
                    }
                    return null;
                }
                finally { FolderGrowth.OverrideDir = null; FolderGrowth.ChoicePlaceOverride = null; }
            });
#else
            Run(check, "Elixar for Business: no delete choices for growing folders (the company sets rules in Policy manager); a choice rule in the file is never read or run", () =>
            {
                string od = Path.Combine(R, "rules3"); Directory.CreateDirectory(od);
                FolderGrowth.OverrideDir = od; FolderGrowth.ChoicePlaceOverride = placeOk;
                try
                {
                    string app = Path.Combine(place, "BizApp", "uploads");
                    Files(app, "u", ".dat", 4, 100 * KB, DateTime.Now.AddDays(-20));
                    var look = new FolderStudy { Path = app, Kind = GrowKind.Look, Owner = "BizApp", Inside = "-", Verdict = "-" };
                    if (FolderGrowth.ChoiceProblem(look) == null) return "choices offered";
                    File.WriteAllText(Path.Combine(od, "keep-in-check.txt"), "14\t5\t2026-11-20\t" + app + "\tcap\t" + (2 * GB) + "\t0\t-\n14\t5\t2026-11-20\t" + app + "\tall\t0\t0\t-\n");
                    if (FolderGrowth.Rules().Count != 0) return "a choice rule was read";
                    if (FolderGrowth.RunRule(new KeepRule { Path = app, Mode = "cap", Cap = 100 * KB, Down = 0 }, DateTime.Now, null) != 0 || Directory.GetFiles(app).Length != 4) return "a cap ran";
                    using (var gf = new GrowingForm(true, s => s, null, null, null))
                    {
                        gf.ShowStudies(new List<Tuple<FastFolder, FolderStudy>> { Tuple.Create(new FastFolder { Path = app, Now = 3 * GB, Grew = 2 * GB, Days = 7 }, look) });
                        if (gf.Studies.Count != 1) return "window";
                    }
                    return null;
                }
                finally { FolderGrowth.OverrideDir = null; FolderGrowth.ChoicePlaceOverride = null; }
            });
#endif

            // ---------- Elixar's own sounds ----------
            Run(check, "Sounds: 13 Potion sounds built into Elixar.exe (short WAV files), Settings > Sounds saves Potion / Windows' own / none at once, and \"none\" plays nothing", () =>
            {
                foreach (Snd s in Enum.GetValues(typeof(Snd)))
                {
                    var b = Sounds.Data(s);
                    if (b == null) return "missing: " + s;
                    if (b.Length < 44 || System.Text.Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return "not a WAV file: " + s;
                    int rate = BitConverter.ToInt32(b, 28);   // bytes a second
                    double secs = (b.Length - 44) / (double)Math.Max(1, rate);
                    if (secs > 1.7 || b.Length > 120 * KB) return s + " is too long: " + secs.ToString("0.00") + " s";
                }
                string keep = Sounds.Choice; bool keepOpen = Sounds.OnOpen;
                try
                {
                    Sounds.Choice = Sounds.Off; Sounds.Played.Clear();
                    Sounds.Play(Snd.Done);
                    if (Sounds.Played.Count != 0) return "\"No sounds\" played one";
                    Sounds.Choice = "something else";
                    if (Sounds.Choice != Sounds.Potion) return "an unknown setting isn't Potion";
                    Sounds.Play(Snd.Done);
                    if (Sounds.Played.LastOrDefault() != Snd.Done) return "not played";
                    bool keepUi = MainForm.ForceWindowsUi; MainForm.ForceWindowsUi = true;
                    try
                    {
                        using (var f = new MainForm(true))
                        {
                            if (f.SoundPotion == null || !f.SoundPotion.Checked) return "Settings > Sounds";
                            f.SoundOff.Checked = true;
                            if (Sounds.Choice != Sounds.Off || f.SoundOnOpen.Enabled) return "saving none: " + Sounds.Choice;
                            f.SoundWindows.Checked = true;
                            if (Sounds.Choice != Sounds.WindowsOwn) return "saving Windows' own";
                            f.SoundPotion.Checked = true; f.SoundOnOpen.Checked = true;
                            if (Sounds.Choice != Sounds.Potion || !Sounds.OnOpen) return "saving Potion and the cork pop";
                        }
                    }
                    finally { MainForm.ForceWindowsUi = keepUi; }
                    // the sound when a check you started finishes
                    var before = new HealthReport { Total = 70 };
                    var sec = new HealthReport { Total = 90 }; sec.Items.Add(new HFind { Area = "Security", Level = HLevel.Warn });
                    var better = new HealthReport { Total = 80 }; better.Items.Add(new HFind { Area = "Speed", Level = HLevel.Warn });
                    if (HealthForm.CheckSound(before, sec) != Snd.Alert || HealthForm.CheckSound(before, better) != Snd.Better || HealthForm.CheckSound(better, before) != Snd.Scan) return "health check sounds";
                    var ign = new HealthReport { Total = 60 }; ign.Items.Add(new HFind { Area = "Security", Level = HLevel.Bad, Ignored = true });
                    if (HealthForm.CheckSound(before, ign) != Snd.Scan) return "an ignored finding made the alert sound";
                }
                finally { Sounds.Choice = keep; Sounds.OnOpen = keepOpen; }
                return null;
            });

            // ---------- translated ----------
            Run(check, "1.2.68 in Hebrew, Arabic, Russian and Spanish: the choices, the alerts and Settings > Sounds", () =>
            {
                var words = new List<string> { "What should Elixar do with this folder?", "Just tell me", "Cap its size: when it passes", "GB, delete the oldest files until it's under",
                    "Delete files older than", "days, daily", "Ignore this folder", "Save my choice...", "Sounds", "Elixar's own (Potion)", "Windows' own sounds", "No sounds",
                    "Play a cork pop when Elixar opens", "C: is filling up fast", "The growing-folders check couldn't finish", "The growing-folders check can't finish", "Your rules (allowed by you)",
                    "SecureAPlus's uploads to SecureAge are stuck: it keeps making new ones and never removes the old ones. Update or repair SecureAPlus. Deleting old ones doesn't turn off your protection." };
                string keep = Lang.Code;
                try
                {
                    foreach (var lang in new[] { "he", "ar", "ru", "es" })
                    {
                        Lang.Init(lang);
                        foreach (var w in words) if (Lang.T(w) == w) return "not in " + lang + ": " + w;
                        foreach (var w in new[] { "over 20 GB: oldest files deleted until under 2 GB, daily", "Your rule freed 52 GB", "Cap this folder at 20 GB?", "C: lost 54 GB in 7 days. Most of it: Whitelist\\to be transferred (+54 GB). Click to open Elixar.",
                                                  "Every day, when it's over 20 GB, Elixar deletes its oldest files until it's under 2 GB. The newest file always stays, and programs are never deleted.", "Right now this deletes 40 files (52 GB). They can't be put back.", "C: lost 12 GB in 7 days. The folder that grew most: Zoom\\logs (+2 GB). Click to open Elixar." })
                            if (Lang.T(w) == w && lang == "he") return "pattern not in Hebrew: " + w;
                    }
                }
                finally { Lang.Init(keep); }
                return null;
            });
        }
    }
}
