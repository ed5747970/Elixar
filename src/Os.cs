// Which system Elixar runs on: "Windows 11 Pro 24H2 (build 26100)" or "Ubuntu 24.04.5 LTS". Shown on the Dashboard,
// in Settings and in the self-test report, and used to pick the right cleaning tools (apt, snap, flatpak...).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class OsInfo
    {
        public string Name = "", Family = "", Id = "", Version = "";
        public int Build;

        // "debian" for Ubuntu, Mint, Debian, Pop!_OS...; "fedora", "arch", "suse"; "windows"; "other"
        public bool Supported { get { return Family == "windows" || Family == "debian"; } }

        static OsInfo current;
        public static OsInfo Current { get { return current ?? (current = Win.IsWindows ? ReadWindows() : ReadLinux(File.Exists("/etc/os-release") ? SafeLines("/etc/os-release") : SafeLines("/usr/lib/os-release"))); } }

        static string[] SafeLines(string f) { try { return File.ReadAllLines(f); } catch { return new string[0]; } }

        // /etc/os-release: KEY=value lines, values sometimes in quotes
        public static OsInfo ReadLinux(IEnumerable<string> lines)
        {
            var kv = new Dictionary<string, string>();
            foreach (var raw in lines)
            {
                int i = raw.IndexOf('=');
                if (i <= 0 || raw.TrimStart().StartsWith("#")) continue;
                string v = raw.Substring(i + 1).Trim();
                if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[v.Length - 1] == v[0]) v = v.Substring(1, v.Length - 2);
                kv[raw.Substring(0, i).Trim()] = new string(v.Where(c => !char.IsControl(c)).ToArray());
            }
            Func<string, string> get = k => { string v; return kv.TryGetValue(k, out v) ? v : ""; };
            var o = new OsInfo { Id = get("ID").ToLowerInvariant(), Version = get("VERSION_ID") };
            o.Name = get("PRETTY_NAME").Length > 0 ? get("PRETTY_NAME") : (get("NAME") + " " + o.Version).Trim();
            if (o.Name.Length == 0) o.Name = "Linux";
            if (o.Name.Length > 60) o.Name = o.Name.Substring(0, 60);
            var like = (" " + o.Id + " " + get("ID_LIKE").ToLowerInvariant() + " ").Replace('"', ' ');
            o.Family = like.Contains(" debian ") || like.Contains(" ubuntu ") ? "debian"
                     : like.Contains(" fedora ") || like.Contains(" rhel ") || like.Contains(" centos ") ? "fedora"
                     : like.Contains(" arch ") ? "arch"
                     : like.Contains(" suse ") || like.Contains(" opensuse ") ? "suse" : "other";
            return o;
        }

        // ProductName still says "Windows 10" on Windows 11, so the build number decides (11 starts at 22000)
        public static OsInfo FromWindows(string product, string display, int build, int ubr)
        {
            string name = (product ?? "Windows").Trim();
            if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
            var o = new OsInfo { Family = "windows", Id = "windows", Build = build, Version = display ?? "" };
            o.Name = name + (string.IsNullOrEmpty(display) ? "" : " " + display) + (build > 0 ? " (build " + build + (ubr > 0 ? "." + ubr : "") + ")" : "");
            return o;
        }

        static OsInfo ReadWindows()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k == null) return FromWindows("Windows", "", Environment.OSVersion.Version.Build, 0);
                    int build, ubr = 0;
                    if (!int.TryParse(k.GetValue("CurrentBuild") as string ?? "", out build)) build = Environment.OSVersion.Version.Build;
                    object u = k.GetValue("UBR"); if (u is int) ubr = (int)u;
                    string disp = k.GetValue("DisplayVersion") as string ?? k.GetValue("ReleaseId") as string ?? "";
                    return FromWindows(k.GetValue("ProductName") as string, disp, build, ubr);
                }
            }
            catch { return FromWindows("Windows", "", Environment.OSVersion.Version.Build, 0); }
        }

        // what Elixar can do on this system, in one line
        public string Support
        {
            get
            {
                if (Family == "windows") return "Fully supported.";
                if (Family == "debian") return "Supported: apt, Snap, Flatpak, the system journal and your caches.";
                return "Your caches, Trash and app data are supported. System packages (" + (Family == "fedora" ? "dnf" : Family == "arch" ? "pacman" : Family == "suse" ? "zypper" : "your package manager") +
                       ") aren't cleaned yet: Elixar is made for Ubuntu, Debian and Mint first.";
            }
        }
    }
}
