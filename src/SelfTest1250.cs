// Self-test for the 1.2.50 features: app updates, space map, "Is this safe to delete?", Windows tweaks, uninstall + leftovers,
// startup impact, the command line, the fleet report, the savings counter, the new languages and the Sponsor button.
// Everything runs in a throwaway folder and a throwaway registry key; nothing on this PC is changed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class SelfTest1250
    {
        static void Pump(Func<bool> done, int ms)
        {
            var end = DateTime.Now.AddMilliseconds(ms);
            while (!done() && DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
        }

        static void Run(Action<bool, string> check, string name, Func<string> test)
        {
            string why;
            try { why = test(); } catch (Exception ex) { why = "ERROR " + ex.GetType().Name + ": " + ex.Message; }
            check(why == null, name + (why == null ? "" : "  (" + why + ")"));
        }

        public static void All(string root, Action<bool, string> check)
        {
            string R = Path.Combine(root, "t1250"); Directory.CreateDirectory(R);

            // ---------- app updates ----------
            Run(check, "App updates: winget's list is read (an id that isn't a plain package id is refused) and the Updates tab shows it", () =>
            {
                Func<string, string, string, string, string, string> row = (n, id, v, a, s) => n.PadRight(20) + id.PadRight(22) + v.PadRight(10) + a.PadRight(11) + s;
                string sample = "   - \r  \\ \r" + row("Name", "Id", "Version", "Available", "Source") + "\r\n" + new string('-', 70) + "\r\n" +
                                row("7-Zip 23.01 (x64)", "7zip.7zip", "23.01", "24.08", "winget") + "\r\n" + row("Evil", "evil;calc", "1.0", "2.0", "winget") + "\r\n" +
                                row("Notepad++", "Notepad++.Notepad++", "8.6", "8.7.1", "winget") + "\r\n2 upgrades available.\r\n";
                var l = WinGet.Parse(sample);
                if (l.Count != 2 || l[0].Id != "7zip.7zip" || l[0].Available != "24.08" || l[1].Name != "Notepad++") return "parsed " + l.Count + ": " + string.Join(",", l.Select(x => x.Id + "=" + x.Available));
                if (WinGet.ValidId("a b") || WinGet.ValidId("x&y") || !WinGet.ValidId("Microsoft.VisualStudioCode")) return "ValidId";
                if (!WinGet.UpgradeArgs("7zip.7zip").Contains("--id 7zip.7zip --exact --silent")) return "args";
                var keep = WinGet.RunOverride; WinGet.RunOverride = a => sample;
                try
                {
                    using (var f = new MainForm(true))
                    {
                        var h = f.Handle; bool done = false; f.UpdatesDone += () => done = true;
                        f.CheckAppUpdates(); Pump(() => done, 20000);
                        if (f.UpdateRows != 2) return "tab rows " + f.UpdateRows;
                    }
                }
                finally { WinGet.RunOverride = keep; }
                return null;
            });

            // ---------- space map ----------
            Run(check, "Space map: a drive is read once into folders and their sizes (links are not followed), boxes fill the map without overlapping, and each box gets the Dashboard's warning sign", () =>
            {
                string m = Path.Combine(R, "map"); Directory.CreateDirectory(Path.Combine(m, "big", "inner")); Directory.CreateDirectory(Path.Combine(m, "small"));
                File.WriteAllBytes(Path.Combine(m, "big", "inner", "a.bin"), new byte[3 << 20]); File.WriteAllBytes(Path.Combine(m, "small", "b.bin"), new byte[20000]); File.WriteAllBytes(Path.Combine(m, "c.txt"), new byte[1000]);
                string outside = Path.Combine(R, "outside"); Directory.CreateDirectory(outside); File.WriteAllBytes(Path.Combine(outside, "x.bin"), new byte[1 << 20]);
                try { if (Win.IsWindows) Sys.Run("cmd.exe", "/c mklink /J \"" + Path.Combine(m, "link") + "\" \"" + outside + "\"", 15000); else Sys.Run("ln", "-s \"" + outside + "\" \"" + Path.Combine(m, "link") + "\"", 15000); } catch { }
                var top = SpaceScan.Scan(m, null, System.Threading.CancellationToken.None);
                long want = (3 << 20) + 20000 + 1000;
                if (top.Size != want) return "size " + top.Size + " want " + want;
                if (top.Children.Count != 3 || top.Children[0].Name != "big" || !top.Children.Any(c => c.IsFiles && c.Size == 1000)) return "children " + string.Join(",", top.Children.Select(c => c.Name + "=" + c.Size));
                var small = top.Children.First(c => c.Name == "small");
                if (small.Children.Count != 0 || top.Children[0].Children.Count != 1) return "prune";
                var area = new RectangleF(0, 0, 300, 200);
                SpaceScan.Layout(top.Children, area);
                foreach (var a in top.Children)
                {
                    if (a.Box.Left < -0.01 || a.Box.Top < -0.01 || a.Box.Right > 300.01 || a.Box.Bottom > 200.01) return "outside the map";
                    if (Math.Abs(a.Box.Width * a.Box.Height - a.Size * 60000.0 / want) > 2) return "area of " + a.Name;
                    foreach (var b in top.Children) if (b != a) { var x = RectangleF.Intersect(a.Box, b.Box); if (x.Width * x.Height > 0.5f) return "overlap"; }
                }
                var places = new List<DashLoc> { new DashLoc { Path = Path.Combine(m, "big"), Tier = "safe" }, new DashLoc { Path = m, Tier = "review" } };
                if (SpaceScan.TierOf(Path.Combine(m, "big", "inner"), places) != "safe" || SpaceScan.TierOf(Path.Combine(m, "small"), places) != "review" || SpaceScan.TierOf(outside, places) != "") return "tiers";
                MainForm.MapRootOverride = m;
                try
                {
                    using (var f = new MainForm(true))
                    {
                        var h = f.Handle; bool done = false; f.MapDone += () => done = true;
                        f.MapScan(); Pump(() => done, 20000);
                        if (f.MapRoot == null || f.MapRoot.Size != want) return "tab";
                    }
                }
                finally { MainForm.MapRootOverride = null; }
                return null;
            });

            // ---------- is this safe to delete? ----------
            Run(check, "\"Is this safe to delete?\": Windows' own folders say never, caches say yes, your files say it's your decision, an unknown folder says find out first; a web search sends only the folder's name", () =>
            {
                Func<string, string> tier = p => Explain.For(p, null, null).Tier;
                if (tier(@"C:\Windows\System32\drivers") != "critical" || tier(@"C:\Windows\WinSxS") != "caution" || tier(@"C:\Users\Dana\AppData\Local\Temp\x") != "safe" ||
                    tier(@"C:\Users\Dana\AppData\Local\Google\Chrome\User Data\Default\Cache") != "safe" || tier(@"C:\Users\Dana\AppData\Local\Google\Chrome\User Data") != "review" ||
                    tier(@"D:\code\site\node_modules") != "review" || tier(@"C:\Program Files\Foo") != "critical" || tier(@"C:\pagefile.sys") != "critical" || tier(@"C:\hiberfil.sys") != "caution")
                    return "tiers";
                var unknown = Explain.For(@"C:\Weird\Thing", null, null);
                if (unknown.Tier != "" || !unknown.Verdict.StartsWith("Not sure")) return "unknown: " + unknown.Tier + "/" + unknown.Verdict;
                var owned = Explain.For(@"C:\Weird\Thing", null, p => "Foo App");
                if (!owned.What.Contains("Foo App")) return "owner";
                if (Explain.SafeName(@"C:\Users\Dana\Secret\") != "Secret" || Explain.SearchUrl(@"C:\Users\Dana\Secret").Contains("Dana") || Explain.SearchUrl(@"C:\Users\Dana").Contains("Dana")) return "web search shows the path";
                ExplainForm seen = null; MainForm.ExplainHook = ef => seen = ef;
                try { using (var f = new MainForm(true)) { f.ExplainPath(@"C:\Windows\Temp"); } }
                finally { MainForm.ExplainHook = null; }
                if (seen == null || seen.E.Tier != "safe") return "window";
                return null;
            });

            // ---------- Windows tweaks ----------
            Run(check, "Windows tweaks: each switch writes its values through Logs (every one can be undone there), \"Windows' default\" puts Windows' own values back, and the policy can fix a tweak on", () =>
            {
                string prefix = "TweakTest-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                Tweaks.TestPrefix = prefix;
                string keepCl = ChangeLog.OverrideFile; ChangeLog.OverrideFile = Path.Combine(R, "tweaks-changes.jsonl");
                try
                {
                    var ads = Tweaks.Get("ads");
                    if (Tweaks.State(ads) != TweakState.Off) return "start state";
                    if (Tweaks.Set(ads, true, null).Count > 0 || Tweaks.State(ads) != TweakState.On) return "on";
                    var regs = ChangeLog.Read().Where(e => e.Kind == "Registry").ToList();
                    if (regs.Count != ads.Values.Count || !regs.All(e => e.CanUndo && e.Section == "Windows tweaks" && e.Detail.StartsWith("Windows tweak"))) return "log " + regs.Count + " " + string.Join(",", regs.Select(e => e.Section + "/" + e.CanUndo));
                    if (RegLog.Undo(regs[0]) != null || Tweaks.State(ads) != TweakState.Partly) return "undo";
                    if (Tweaks.Set(ads, false, null).Count > 0 || Tweaks.State(ads) != TweakState.Off) return "default";
                    // Windows' value 1 is written back where Windows has one; the value Windows doesn't have is removed
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Elixar\" + prefix + @"\HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"))
                        if (k == null || !(k.GetValue("SystemPaneSuggestionsEnabled") is int) || (int)k.GetValue("SystemPaneSuggestionsEnabled") != 1) return "default value";
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Elixar\" + prefix + @"\HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                        if (k != null && k.GetValue("Start_IrisRecommendations") != null) return "removed value";
                    if (!RegLog.UndoAllowed("HKCU", @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager") || RegLog.UndoAllowed("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System")) return "undo keys";
                    if (Policy.Rules.Count(r => r.Key.StartsWith("tweak.")) != Tweaks.All.Length || Policy.Preset("Golden image (IT)")["tweak.copilot"] != "on" || Policy.Preset("Balanced (same as Default)")["tweak.copilot"] != "choice") return "policy rules";
                    var custom = Policy.Defaults(); custom["tweak.bing"] = "on";
                    if (Policy.Save(true, custom) != null) return "save";
                    if (Tweaks.ApplyPolicy(null) != 1 || Tweaks.State(Tweaks.Get("bing")) != TweakState.On || Tweaks.ApplyPolicy(null) != 0) return "policy apply";
                    using (var f = new MainForm(true))
                    {
                        f.ShowTweaks(); f.RefreshTweaks();
                        int bi = Array.IndexOf(Tweaks.All, Tweaks.Get("bing"));
                        if (f.TweakBoxEnabled(bi) || !f.TweakBoxEnabled(0) || !f.TweakStateText(bi).Contains(Lang.T("set by the policy"))) return "tab";
                    }
                    Policy.Save(false, Policy.Defaults());
                    return null;
                }
                finally
                {
                    Tweaks.TestPrefix = null; ChangeLog.OverrideFile = keepCl;
                    try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Elixar\" + prefix, false); } catch { }
                }
            });

            // ---------- uninstall + leftovers ----------
            Run(check, "Uninstall + leftovers: when Windows no longer lists the app, Elixar finds the folders named after it (and inside its publisher's folder), never another app's, and asks before moving them to Undo", () =>
            {
                string L = Path.Combine(R, "Local"), P = Path.Combine(R, "ProgramFiles");
                foreach (var d in new[] { Path.Combine(L, "Foo App"), Path.Combine(L, "FooCorp", "Foo App"), Path.Combine(L, "Bar"), Path.Combine(P, "Foo App") })
                { Directory.CreateDirectory(d); File.WriteAllBytes(Path.Combine(d, "x.dat"), new byte[5000]); }
                var keepR = Leftovers.RootsOverride; var keepP = Leftovers.ProgramRootsOverride; var keepN = Leftovers.NamesOverride;
                Leftovers.RootsOverride = () => new List<string[]> { new[] { L, "AppData\\Local" } };
                Leftovers.ProgramRootsOverride = () => new List<string[]> { new[] { P, "Program Files" } };
                Leftovers.NamesOverride = () => new List<string> { "Bar" };
                var keepI = MainForm.InstalledOverride; var keepAsk = MainForm.LeftoversAskHook; int keepPoll = MainForm.UninstallPollMs;
                try
                {
                    if (Leftovers.StripVersion("7-Zip 23.01 (x64)") != "7-Zip" || Leftovers.StripVersion("Foo App 2.1") != "Foo App") return "names";
                    var app = new AppEntry { Name = "Foo App 2.1 (x64)", Publisher = "FooCorp", Location = Path.Combine(P, "Foo App"), KeyPath = "HKLM\\X\\FooApp" };
                    var found = Leftovers.ForApp(app, new List<string> { "Bar" });
                    var paths = found.Select(x => x.Path).OrderBy(x => x).ToList();
                    if (found.Count != 3 || paths.Any(p => p.EndsWith("Bar")) || found.Sum(x => x.Bytes) != 15000) return "found " + string.Join(", ", paths);
                    if (Leftovers.ForApp(app, new List<string> { "Foo App" }).Any(x => Path.GetFileName(x.Path) == "Foo App")) return "an app still using the name";
                    int calls = 0; List<Leftover> asked = null;
                    MainForm.InstalledOverride = () => ++calls < 3 ? new List<AppEntry> { app } : new List<AppEntry>();
                    MainForm.LeftoversAskHook = (a, l) => { asked = l; return false; };
                    MainForm.UninstallPollMs = 30;
                    using (var f = new MainForm(true))
                    {
                        var h = f.Handle; bool done = false; f.UninstallFinished += (a, l) => done = true;
                        f.WatchUninstall(app); Pump(() => done, 20000);
                        if (!done || asked == null || asked.Count != 3 || calls < 3) return "watch: done " + done + ", asked " + (asked == null ? -1 : asked.Count) + ", calls " + calls;
                    }
                    if (!Directory.Exists(Path.Combine(L, "Foo App"))) return "moved without a yes";
                    return null;
                }
                finally
                {
                    Leftovers.RootsOverride = keepR; Leftovers.ProgramRootsOverride = keepP; Leftovers.NamesOverride = keepN;
                    MainForm.InstalledOverride = keepI; MainForm.LeftoversAskHook = keepAsk; MainForm.UninstallPollMs = keepPoll;
                }
            });

            // ---------- startup impact ----------
            Run(check, "Startup impact: Windows' own startup measurements are read (how long each startup took, and how much each program slowed it)", () =>
            {
                string raw = "BOOT|2026-10-20T08:00:00|52000|31000|21000\nBOOT|2026-10-21T08:00:00|40000|25000|15000\nBOOT|bad|1|2|3\n" +
                             "APP|2026-10-20T08:00:00|OneDrive.exe|Microsoft OneDrive|3000\nAPP|2026-10-21T08:00:00|OneDrive.exe|Microsoft OneDrive|5000\n" +
                             "APP|2026-10-21T08:00:00|Teams.exe|Teams|800\nAPP|2026-10-21T08:00:00||x|5\nAPP|2026-10-21T08:00:00|evil.exe|x|-4\ngarbage\n";
                var d = StartupImpact.Parse(raw);
                if (d.Boots.Count != 2 || d.Boots[0].Ms != 40000 || d.Apps.Count != 2) return "boots " + d.Boots.Count + ", apps " + d.Apps.Count;
                var od = d.For(@"C:\Users\x\AppData\Local\Microsoft\OneDrive\OneDrive.exe");
                if (od == null || od.Times != 2 || od.AvgMs != 4000 || StartupImpact.Words(od) != "+4.0 s  (2 times)" || StartupImpact.Words(null) != "-") return "app";
                if (!StartupImpact.Summary(d).StartsWith("Last startup took 40 s")) return StartupImpact.Summary(d);
                return null;
            });

            // ---------- savings counter ----------
            Run(check, "Savings: everything Elixar frees is added up, and the Dashboard's Last clean tile shows the total", () =>
            {
                string keep = Savings.OverrideFile; Savings.OverrideFile = Path.Combine(R, "savings.txt");
                string keepCl = ChangeLog.OverrideFile; ChangeLog.OverrideFile = Path.Combine(R, "savings-changes.jsonl");
                try
                {
                    Savings.Add(1000, "a"); Savings.Add(-5, "bad"); Savings.Add(0, "none"); Savings.Add(2000, "b\tc");
                    ChangeLog.Cleaned(Catalog.Build().First(), 500, null, "test");
                    ChangeLog.Cleaned(Catalog.Build().First(), 700, "a problem", "test");   // a clean that went wrong isn't counted
                    var t = Savings.Read();
                    if (t.Bytes != 3500 || t.Cleans != 3 || t.ThisMonth != 3500 || !t.Since.HasValue) return t.Bytes + "/" + t.Cleans;
                    if (!Savings.Words(t).StartsWith("Elixar has freed")) return "words";
                    return null;
                }
                finally { Savings.OverrideFile = keep; ChangeLog.OverrideFile = keepCl; }
            });

            // ---------- fleet report ----------
            Run(check, "Fleet report: each PC's status file holds numbers and its name only, other files in the folder are skipped, and Policy manager > Fleet report lists them", () =>
            {
                string dir = Path.Combine(R, "fleet"); Directory.CreateDirectory(dir);
                var s = Fleet.Collect(false);
                string json = Fleet.Json(s);
                if (json.Contains(Environment.UserName) && Environment.UserName.Length > 3 && !Environment.MachineName.Contains(Environment.UserName)) return "user name in the report";
                var back = Fleet.Parse(json);
                if (back == null || back.Computer != Environment.MachineName || back.Total != s.Total || back.Elixar != s.Elixar) return "round trip";
                Fleet.Write(dir, s);
                var other = new FleetStatus { Computer = "=cmd|' /C calc'!A0", Total = 100, Free = 5, FreePercent = 5, Time = "2026-01-01T00:00:00" };
                Fleet.Write(Path.Combine(dir, "other.json"), other);
                File.WriteAllText(Path.Combine(dir, "junk.json"), "{\"tool\":\"something else\"}");
                File.WriteAllText(Path.Combine(dir, "bad.json"), "not json");
                int bad; var list = Fleet.ReadFolder(dir, out bad);
                if (list.Count != 2 || bad != 2 || list[0].FreePercent != 5) return "folder " + list.Count + "/" + bad;
                if (!Fleet.Csv(list).Contains("\"'=cmd")) return "csv formula";
                using (var f = new MainForm(true))
                {
                    var h = f.Handle; bool done = false; f.FleetDone += () => done = true;
                    f.FleetSetFolder(dir); f.FleetLoad(); Pump(() => done, 20000);
                    if (f.FleetRowCount != 2 || !f.FleetRowText(0).Contains("5%")) return "tab " + f.FleetRowCount;
                }
                return null;
            });

            // ---------- command line ----------
            Run(check, "Command line: --help, --status, --report and --policy work without a window, a wrong command or a blocked action gives its exit code, and nothing asks a question", () =>
            {
                var said = new List<string>();
                var keepOut = Cli.Out; Cli.Out = x => said.Add(x); Cli.AssumeAdmin = true; Cli.NoSchedules = true;
                string prefix = "CliTest-" + Guid.NewGuid().ToString("N").Substring(0, 6); Tweaks.TestPrefix = prefix;
                try
                {
                    if (!Cli.IsCli(new[] { "--status" }) || Cli.IsCli(new[] { "--test" }) || Cli.IsCli(new string[0])) return "IsCli";
                    if (Cli.Run(new[] { "--help" }) != 0 || !string.Join("\n", said).Contains("--report")) return "help";
                    if (Cli.Run(new[] { "--status" }) != 0 || !said.Any(x => x.StartsWith("Elixar ") && x.Contains(Environment.MachineName))) return "status";
                    string rep = Path.Combine(R, "cli-report.json");
                    if (Cli.Run(new[] { "--report", rep }) != 0 || Fleet.Parse(File.ReadAllText(rep)) == null) return "report";
                    if (Cli.Run(new[] { "--clean-item", "No such item" }) != 4 || Cli.Run(new[] { "--tweaks", "maybe" }) != 4 || Cli.Run(new[] { "--report" }) != 4) return "wrong commands";
                    string pf = Path.Combine(R, "careful.elixarpolicy"); File.WriteAllText(pf, Policy.ExportText(Policy.Preset("Careful")));
                    if (Cli.Run(new[] { "--policy", pf }) != 0 || Policy.Get("clean.level") != "review" || !Policy.UseCustom) return "policy";
                    var thumbs = Catalog.Build().First(i => i.Tier == Tier.Caution);
                    if (Cli.Run(new[] { "--clean-item", thumbs.Name }) != 2) return "blocked item";   // Careful: Check-first items at most
                    string changed = File.ReadAllText(pf).Replace("clean.level=review", "clean.level=everything");
                    File.WriteAllText(pf, changed);
                    if (Cli.Run(new[] { "--policy", pf }) != 1 || Policy.Get("clean.level") != "review") return "a changed policy file was taken";
                    Policy.Save(false, Policy.Defaults());
                    return null;
                }
                finally
                {
                    Cli.Out = keepOut; Cli.AssumeAdmin = false; Cli.NoSchedules = false; Tweaks.TestPrefix = null;
                    try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Elixar\" + prefix, false); } catch { }
                }
            });

            // ---------- languages ----------
            Run(check, "Languages: Arabic (right to left), Russian and Spanish translate the main screens, keep numbers in their texts, and leave the rest in English; Hebrew is unchanged", () =>
            {
                string keep = Lang.Code;
                try
                {
                    if (!Lang.MoreTableOk || Lang.MoreCount < 150) return "table";
                    Lang.Init("ru");
                    if (Lang.T("Cleaner") != "Очистка" || !Lang.T("Free 1.2 GB safely").StartsWith("Освободить 1.2 GB") || Lang.Rtl || Lang.Hebrew || Lang.T("Some text nobody translated") != "Some text nobody translated") return "ru";
                    if (Lang.MailT("Cleaner") != "Cleaner") return "emails stay English";
                    Lang.Init("ar");
                    if (!Lang.Rtl || Lang.T("Dashboard") != "لوحة التحكم" || Lang.T("Clean 3 (1.2 GB)") != "تنظيف 3 (1.2 GB)") return "ar";
                    Lang.Init("es");
                    if (Lang.T("Settings") != "Configuración" || Lang.T("Freed 2 GB · 40 GB in all") != "Liberado 2 GB · 40 GB en total") return "es: " + Lang.T("Freed 2 GB · 40 GB in all");
                    Lang.Init("he");
                    if (Lang.T("Cleaner") != "ניקוי" || !Lang.Rtl) return "he";
                    Lang.Init("xx");
                    if (Lang.Code != "en" || Lang.T("Cleaner") != "Cleaner") return "unknown language";
                    return null;
                }
                finally { Lang.Code = keep; }
            });

            // ---------- sponsor ----------
            Run(check, "Sponsor button: shows only when the build knows Elixar's GitHub address, and opens only GitHub Sponsors, Ko-fi or Buy Me a Coffee", () =>
            {
                if (Updates.SponsorFrom("elisar/elixar", null) != "https://github.com/sponsors/elisar") return "repo";
                if (Updates.SponsorFrom("", null) != null || Updates.SponsorFrom("not a repo", null) != null) return "no repo";
                if (Updates.SponsorFrom("a/b", "https://evil.example/x") != "https://github.com/sponsors/a") return "other site";
                if (Updates.SponsorFrom("", "https://ko-fi.com/elisar") != "https://ko-fi.com/elisar") return "ko-fi";
                if (Updates.SponsorFrom("", "https://github.com/sponsors/x/../../evil") != null) return "path";
                return null;
            });
        }
    }
}
