// Leftovers of removed apps: folders in AppData and ProgramData that no installed app, service or running program
// belongs to, and that nothing has changed for 3 months. These are guesses, so nothing is ticked for you, and what you
// pick goes to Elixar's Undo for 7 days (Tools > Undo puts it back) before it is deleted.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class Leftover { public string Path = "", Where = ""; public long Bytes; public DateTime LastChange; public string Name { get { return System.IO.Path.GetFileName(Path); } } }

    public static class Leftovers
    {
        public const int QuietDays = 90;

        // folders of Windows itself and of the big platforms: never offered, whatever their age
        static readonly HashSet<string> Never = new HashSet<string>(new[] {
            "microsoft", "packages", "temp", "tmp", "programs", "publishers", "comms", "connecteddevicesplatform", "d3dscache", "crashdumps", "virtualstore",
            "elevateddiagnostics", "history", "fontcache", "iconcache", "peerdistrepub", "placeholdertilelogofolder", "windows", "microsoftcorporation", "microsoftedge",
            "packagecache", "usoprivate", "usoshared", "softwaredistribution", "windowsdefender", "windowsholographicdevices", "regid19910 6commicrosoft",
            "nvidia", "nvidiacorporation", "amd", "ati", "intel", "realtek", "google", "mozilla", "apple", "applecomputer", "adobe", "sun", "oracle", "java",
            "elixar", "elixarundo", "desktop", "documents", "startmenu", "templates", "recent", "sendto", "networkshortcuts", "printershortcuts", "libraries",
            "onedrive", "teams", "msteams", "squirreltemp", "cache", "caches", "ssh", "gnupg", "identities", "credentials", "crypto", "protect", "systemcertificates",
            "vault", "spoon", "dbg", "symbols", "pip", "npm", "nuget", "nugetpackages", "dotnet", "powershell", "windowspowershell", "wsl", "lxss", "docker",
            "dockerdesktop", "steam", "epicgameslauncher", "battlenet", "blizzardentertainment", "riotgames", "origin", "eadesktop", "ubisoftgamelauncher",
            "gog", "goggalaxy", "discord", "spotify", "whatsapp", "telegram desktop", "zoom", "slack", "dropbox", "box", "icloud", "chocolatey", "scoop",
            "jetbrains", "visualstudio", "vscode", "code", "github", "githubdesktop", "git", "python", "anaconda", "conda", "mathworks", "autodesk", "unity",
            "unitytechnologies", "epicgames", "ssms", "sqlserver", "iis", "inetpub", "regid", "lenovo", "dell", "hp", "hewlettpackard", "asus", "acer", "msi",
            "samsung", "logitech", "logishrd", "razer", "corsair", "steelseries", "synaptics", "elantech", "dolby", "waves", "conexant", "killer", "qualcomm",
            "broadcom", "mediatek", "vmware", "virtualbox", "oraclevirtualbox", "hyperv", "kaspersky", "eset", "avast", "avg", "norton", "symantec", "mcafee",
            "bitdefender", "malwarebytes", "trendmicro", "sophos", "comodo", "panda", "webex", "cisco", "citrix", "anydesk", "teamviewer", "parsec"
        }.Select(N));

        public static string N(string s) { return Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }

        public static Func<List<string>> NamesOverride;   // self-test

        // names that tell Elixar something is still installed: apps, their publishers and folders, services, running programs
        public static List<string> InUseNames()
        {
            if (NamesOverride != null) return NamesOverride();
            var names = new List<string>();
            if (!Win.IsWindows) return names;
            try
            {
                foreach (var a in AppsData.ReadInstalled())
                {
                    names.Add(a.Name); names.Add(a.Publisher);
                    foreach (var p in new[] { a.Location, a.Uninstall })
                        foreach (Match m in Regex.Matches(p ?? "", @"\\([^\\""]+)")) names.Add(m.Groups[1].Value);
                }
            }
            catch { }
            try
            {
                using (var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var sv = hk.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                    if (sv != null)
                        foreach (var n in sv.GetSubKeyNames())
                            using (var k = sv.OpenSubKey(n))
                            {
                                names.Add(n);
                                string img = k == null ? null : k.GetValue("ImagePath") as string;
                                foreach (Match m in Regex.Matches(img ?? "", @"\\([^\\""]+)")) names.Add(m.Groups[1].Value);
                            }
            }
            catch { }
            // programs that start with Windows, from either registry view
            try
            {
                foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                        using (var hk = RegistryKey.OpenBaseKey(hive, view))
                            foreach (var sub in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
                                using (var k = hk.OpenSubKey(sub))
                                    if (k != null)
                                        foreach (var v in k.GetValueNames())
                                        {
                                            names.Add(v);
                                            foreach (Match m in Regex.Matches(k.GetValue(v) as string ?? "", @"\\([^\\""]+)")) names.Add(m.Groups[1].Value);
                                        }
            }
            catch { }
            try { foreach (var p in Process.GetProcesses()) { try { names.Add(p.ProcessName); } catch { } finally { p.Dispose(); } } } catch { }
            return names.Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        // a folder belongs to something installed when its name matches an app, publisher, folder, service or program
        public static bool Belongs(string folderName, HashSet<string> exact, List<string> long_)
        {
            string f = N(folderName);
            if (f.Length < 3 || Never.Contains(f)) return true;   // too short to judge, or a known platform: leave it
            if (Regex.IsMatch(folderName ?? "", @"^\{?[0-9A-Fa-f]{8}-") || Regex.IsMatch(f, @"^[0-9a-f]{16,}$|^\d+$")) return true;   // a code, not a name: can't tell whose it is
            if (exact.Contains(f)) return true;
            foreach (var n in long_) if (n.Contains(f) || (n.Length >= 5 && f.Contains(n))) return true;
            return false;
        }

        static DateTime Newest(string dir, int max)
        {
            DateTime newest = DateTime.MinValue; int seen = 0;
            try { newest = Directory.GetLastWriteTime(dir); } catch { }
            var st = new Stack<string>(); st.Push(dir);
            while (st.Count > 0 && seen < max)
            {
                string d = st.Pop();
                try
                {
                    foreach (var f in new DirectoryInfo(d).GetFileSystemInfos())
                    {
                        if (++seen > max) return DateTime.Now;   // very big: treat it as in use
                        if (f.LastWriteTime > newest) newest = f.LastWriteTime;
                        if (f is DirectoryInfo && (f.Attributes & FileAttributes.ReparsePoint) == 0) st.Push(f.FullName);
                    }
                }
                catch { }
            }
            return newest;
        }

        // ---------------- right after an uninstall: what that one app left behind ----------------
        public static string StripVersion(string name)
        {
            string s = Regex.Replace(name ?? "", @"\s*\((x64|x86|64-bit|32-bit|[a-z]{2}-[a-z]{2}|remove only)\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+v?\d+(\.\d+)+.*$", "");
            s = Regex.Replace(s, @"\s+(x64|x86|64-bit|32-bit)$", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        public static List<string> NamesOf(AppEntry a)
        {
            var l = new List<string> { N(a.Name), N(StripVersion(a.Name)) };
            try { if (!string.IsNullOrEmpty(a.Location)) l.Add(N(Path.GetFileName(a.Location.TrimEnd('\\', '/')))); } catch { }
            return l.Where(x => x.Length >= 4 && !Never.Contains(x)).Distinct().ToList();
        }

        public static Func<List<string[]>> ProgramRootsOverride;   // self-test
        static List<string[]> ProgramRoots()
        {
            if (ProgramRootsOverride != null) return ProgramRootsOverride();
            var r = new List<string[]>();
            if (!Win.IsWindows) return r;
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string p = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(p) && !r.Any(x => string.Equals(x[0], p, StringComparison.OrdinalIgnoreCase))) r.Add(new[] { p, Path.GetFileName(p) });
            }
            return r;
        }

        // folders named after the app (or after its publisher, then the app inside it) that nothing still installed uses
        public static List<Leftover> ForApp(AppEntry a, List<string> stillInUse)
        {
            var names = NamesOf(a);
            var found = new List<Leftover>();
            if (names.Count == 0) return found;
            var others = new HashSet<string>(stillInUse.Select(N).Where(x => x.Length >= 3));
            string pub = N(a.Publisher);
            Action<string, string> consider = (d, where) =>
            {
                try
                {
                    if (found.Any(f => string.Equals(f.Path, d, StringComparison.OrdinalIgnoreCase) || DashData.Inside(d, f.Path))) return;
                    var di = new DirectoryInfo(d);
                    if (!di.Exists || (di.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0 || Protect.IsProtected(d)) return;
                    if (others.Contains(N(di.Name))) return;   // another app, service or running program still has this name
                    found.Add(new Leftover { Path = di.FullName, Where = where, Bytes = Fs.SizeOf(di.FullName), LastChange = Directory.GetLastWriteTime(di.FullName) });
                }
                catch { }
            };
            // where the app said it was installed
            if (!string.IsNullOrEmpty(a.Location))
            {
                string loc = a.Location.Trim().Trim('"').TrimEnd('\\', '/');
                bool deepEnough = loc.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length >= 3;   // never a drive or Program Files itself
                if (deepEnough && names.Contains(N(Path.GetFileName(loc)))) consider(loc, "Install folder");
            }
            foreach (var root in ProgramRoots().Concat(Roots()))
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(root[0]); } catch { continue; }
                foreach (var d in dirs)
                {
                    string n = N(Path.GetFileName(d));
                    if (names.Contains(n)) consider(d, root[1]);
                    else if (pub.Length >= 3 && n == pub && !Never.Contains(n))
                        try { foreach (var inner in Directory.GetDirectories(d)) if (names.Contains(N(Path.GetFileName(inner)))) consider(inner, root[1] + "\\" + Path.GetFileName(d)); } catch { }
                }
            }
            return found.OrderByDescending(f => f.Bytes).ToList();
        }

        public static Func<List<string[]>> RootsOverride;   // previews
        public static List<string[]> Roots()
        {
            if (RootsOverride != null) return RootsOverride();
            var r = new List<string[]>();
            if (!Win.IsWindows) return r;
            r.Add(new[] { UserDirs.Local, "AppData\\Local" });
            r.Add(new[] { UserDirs.Local + "Low", "AppData\\LocalLow" });
            r.Add(new[] { UserDirs.Roaming, "AppData\\Roaming" });
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(pd)) r.Add(new[] { pd, "ProgramData" });
            return r;
        }

        public static List<Leftover> Find(List<string[]> roots, List<string> inUse, Action<string> status, CancellationToken ct)
        {
            var exact = new HashSet<string>(inUse.Select(N).Where(x => x.Length >= 3));
            var long_ = exact.Where(x => x.Length >= 4).ToList();
            var found = new List<Leftover>();
            foreach (var root in roots)
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(root[0]); } catch { continue; }
                foreach (var d in dirs)
                {
                    if (ct.IsCancellationRequested) return found;
                    try
                    {
                        var di = new DirectoryInfo(d);
                        if ((di.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0 || Protect.IsProtected(d)) continue;
                        if (Belongs(di.Name, exact, long_)) continue;
                        // a publisher folder: if any folder inside belongs to an installed app, leave the whole folder
                        if (Directory.GetDirectories(d).Any(s => N(Path.GetFileName(s)).Length >= 5 && !Never.Contains(N(Path.GetFileName(s))) && Belongs(Path.GetFileName(s), exact, long_))) continue;
                        if (status != null) status("Checking " + d);
                        var last = Newest(d, 20000);
                        if ((DateTime.Now - last).TotalDays < QuietDays) continue;
                        long size = Fs.SizeOf(d);
                        if (size < (1L << 20)) continue;
                        found.Add(new Leftover { Path = d, Where = root[1], Bytes = size, LastChange = last });
                    }
                    catch { }
                }
            }
            return found.OrderByDescending(f => f.Bytes).ToList();
        }
    }
}
