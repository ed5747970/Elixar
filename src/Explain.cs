// "Is this safe to delete?": a plain answer for any folder or file - what it is, what happens if it goes, and the right way
// to remove it. Built-in knowledge first (nothing leaves the PC). "Search the web" sends only the folder's name, never the path
// (paths hold your user name).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class Explanation
    {
        public string Path, Title, Tier, What, IfDeleted, How, Source;
        public string Verdict
        {
            get
            {
                return Tier == "critical" ? "No - never delete it" : Tier == "caution" ? "Only with the right tool" :
                       Tier == "review" ? "Your decision - look inside first" : Tier == "safe" ? "Yes - it is rebuilt automatically" : "Not sure - find out what made it first";
            }
        }
    }

    public static class Explain
    {
        class Known { public Regex Re; public string Title, Tier, What, IfDeleted, How; }

        static Known K(string re, string title, string tier, string what, string ifDeleted, string how)
        {
            return new Known { Re = new Regex(re, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), Title = title, Tier = tier, What = what, IfDeleted = ifDeleted, How = how };
        }

        // the most specific first; paths are matched with / and \ both allowed
        static readonly Known[] Base =
        {
            K(@"^[a-z]:[\\/]windows[\\/](system32|syswow64)([\\/]|$)", "Windows system files", "critical",
              "The programs and drivers Windows itself is made of.", "Windows stops starting or breaks in odd ways. Usually needs a reinstall.", "Never delete anything here. To free space use Cleaner and Tools > System space."),
            K(@"^[a-z]:[\\/]windows[\\/]winsxs([\\/]|$)", "Windows component store (WinSxS)", "caution",
              "Every version of every Windows component, kept so updates can be removed. Most of it is shared with System32 (hard links), so it looks much bigger than it is.", "Updates, repairs and new features stop working; Windows can fail to start.", "Only with DISM: Cleaner > Windows component cleanup (DISM)."),
            K(@"^[a-z]:[\\/]windows[\\/]installer([\\/]|$)", "Windows Installer cache", "critical",
              "Copies of the setup files of installed programs.", "Those programs can't be repaired, updated or uninstalled any more.", "Leave it. It shrinks when you uninstall programs in the Apps tab."),
            K(@"^[a-z]:[\\/]windows[\\/]softwaredistribution[\\/]download([\\/]|$)", "Windows Update downloads", "safe",
              "Updates Windows downloaded. After they are installed they aren't needed.", "Nothing, once updates are installed. An update that is half way may download again.", "Cleaner > Windows Update downloads."),
            K(@"^[a-z]:[\\/]windows[\\/]temp([\\/]|$)", "Windows temp files", "safe", "Scratch files programs and Windows leave behind.", "Nothing. Files still in use are skipped.", "Cleaner > Windows temp files."),
            K(@"[\\/]appdata[\\/]local[\\/]temp([\\/]|$)", "Your temp files", "safe", "Scratch files your programs leave behind.", "Nothing. Files still in use are skipped.", "Cleaner > Your temp files."),
            K(@"^[a-z]:[\\/]windows[\\/]prefetch([\\/]|$)", "Prefetch", "safe", "Notes Windows keeps to start your programs faster. Small.", "Programs start a little slower for a few days, then it is rebuilt.", "Leave it - it saves almost nothing."),
            K(@"^[a-z]:[\\/]windows[\\/](minidump|livekernelreports)([\\/]|$)|^[a-z]:[\\/]windows[\\/]memory\.dmp$", "Crash dumps", "safe", "Saved memory from a crash, for finding its cause.", "Nothing, unless someone is investigating that crash.", "Cleaner > Crash dumps."),
            K(@"^[a-z]:[\\/]windows[\\/]logs([\\/]|$)", "Windows logs", "caution", "Records of updates and setup.", "Old ones: nothing. Current ones are in use.", "Cleaner > Old Windows servicing logs."),
            K(@"^[a-z]:[\\/]windows\.old([\\/]|$)", "Previous Windows installation", "caution", "Your old Windows, kept after an upgrade so you can go back.", "You can't go back to the previous Windows version.", "Cleaner > Previous Windows installation (Windows.old). Deleting it by hand fails on permissions."),
            K(@"^[a-z]:[\\/]\$windows\.~(bt|ws)([\\/]|$)", "Windows upgrade leftovers", "caution", "Files from a Windows upgrade.", "Nothing once the upgrade has finished.", "Cleaner, together with Windows.old."),
            K(@"^[a-z]:[\\/]\$recycle\.bin([\\/]|$)", "Recycle Bin", "review", "Files you deleted, kept so you can get them back.", "They are gone for good.", "Look in the Recycle Bin first, then empty it (or let Policy manager empty old files)."),
            K(@"^[a-z]:[\\/]windows([\\/]|$)", "Windows", "critical", "Windows itself.", "Windows breaks.", "Never delete by hand. Use Cleaner and Tools > System space."),
            K(@"^[a-z]:[\\/](pagefile|swapfile)\.sys$", "Page file", "critical", "Extra memory on the drive, used when RAM is full.", "Windows makes it again; with it off, programs can crash when memory runs out.", "Leave it to Windows."),
            K(@"^[a-z]:[\\/]hiberfil\.sys$", "Hibernation file", "caution", "Holds your memory while the PC hibernates, and makes Fast startup work.", "Hibernate and Fast startup stop working.", "Tools > System space > Hibernation file (shrink or turn off)."),
            K(@"^[a-z]:[\\/]program files( \(x86\))?[\\/]windowsapps([\\/]|$)", "Store apps", "critical", "Apps from the Microsoft Store.", "Those apps break and Windows can't update them.", "Uninstall them in the Apps tab."),
            K(@"^[a-z]:[\\/]program files( \(x86\))?([\\/]|$)", "Installed programs", "critical", "Your installed programs.", "The program breaks and can't be uninstalled properly.", "Uninstall it in the Apps tab, then Tools > Leftovers."),
            K(@"^[a-z]:[\\/]programdata[\\/]package cache([\\/]|$)", "Package Cache", "critical", "Setup files Visual Studio and other installers need to repair or uninstall.", "Those programs can't be repaired, updated or removed.", "Leave it. Uninstall the programs you don't use."),
            K(@"^[a-z]:[\\/]programdata[\\/]microsoft[\\/]windows[\\/]wer([\\/]|$)", "Error reports", "safe", "Reports about programs that crashed.", "Nothing.", "Cleaner > Error reports."),
            K(@"^[a-z]:[\\/]programdata([\\/]|$)", "Shared app data", "critical", "Settings and data programs share between all users.", "Programs lose their settings, licences or break.", "Leave it. It shrinks when you uninstall programs."),
            K(@"[\\/]google[\\/]chrome[\\/]user data[\\/][^\\/]+[\\/](cache|code cache|gpucache)([\\/]|$)", "Chrome cache", "safe", "Copies of web pages and pictures Chrome downloaded.", "Pages load a little slower the first time. You stay signed in.", "Cleaner > Chrome cache (with Chrome closed)."),
            K(@"[\\/]microsoft[\\/]edge[\\/]user data[\\/][^\\/]+[\\/](cache|code cache|gpucache)([\\/]|$)", "Edge cache", "safe", "Copies of web pages and pictures Edge downloaded.", "Pages load a little slower the first time. You stay signed in.", "Cleaner > Edge cache (with Edge closed)."),
            K(@"[\\/]mozilla[\\/]firefox[\\/]profiles[\\/][^\\/]+[\\/]cache2([\\/]|$)", "Firefox cache", "safe", "Copies of web pages Firefox downloaded.", "Pages load a little slower the first time.", "Close Firefox, then clear it in Firefox's settings or Cleaner."),
            K(@"[\\/](google[\\/]chrome|microsoft[\\/]edge|bravesoftware[\\/]brave-browser)[\\/]user data([\\/]|$)", "Browser profile", "review", "Your bookmarks, passwords, history and extensions.", "You lose them unless the browser syncs them.", "Only clear the cache inside it (Cleaner). Leave the rest."),
            K(@"[\\/]nvidia[\\/](dxcache|glcache)([\\/]|$)|[\\/]d3dscache([\\/]|$)|[\\/]amd[\\/]dxcache([\\/]|$)", "Graphics shader cache", "safe", "Shaders your graphics card compiled for games.", "Games stutter a little the first time, then it is rebuilt.", "Cleaner (close games first)."),
            K(@"[\\/]microsoft[\\/]windows[\\/]explorer[\\/]thumbcache", "Thumbnail cache", "safe", "Small pictures for folders in Explorer.", "Thumbnails appear a moment later the first time.", "Cleaner > Thumbnail cache."),
            K(@"[\\/]microsoft[\\/]windows[\\/]inetcache([\\/]|$)", "Internet cache", "safe", "Web files saved by Windows and old Internet Explorer.", "Nothing important.", "Cleaner."),
            K(@"[\\/]deliveryoptimization([\\/]|$)", "Delivery Optimization cache", "safe", "Update pieces Windows shares with other PCs.", "Nothing.", "Cleaner > Delivery Optimization cache."),
            K(@"[\\/]node_modules([\\/]|$)", "Node.js packages", "review", "Packages a programming project downloaded.", "The project needs \"npm install\" before it runs again.", "Delete it in projects you no longer work on."),
            K(@"[\\/](\.nuget[\\/]packages|\.gradle[\\/]caches|\.m2[\\/]repository|npm-cache|pip[\\/]cache|yarn[\\/]cache)([\\/]|$)", "Developer package cache", "safe", "Copies of programming packages, kept to build faster.", "They download again when a project needs them.", "Smart scan finds them, or the tool's own clean command."),
            K(@"[\\/]steamapps[\\/]common[\\/]", "Steam game", "review", "A game installed by Steam.", "The game is gone; your saves are usually in the cloud.", "Uninstall it in Steam, so Steam knows."),
            K(@"[\\/]steamapps[\\/](shadercache|downloading|temp)([\\/]|$)", "Steam cache", "safe", "Shaders and half-finished downloads from Steam.", "Rebuilt or downloaded again.", "Close Steam first."),
            K(@"[\\/]apple computer[\\/]mobilesync[\\/]backup([\\/]|$)|[\\/]apple[\\/]mobilesync[\\/]backup([\\/]|$)", "iPhone backups", "review", "Backups of an iPhone or iPad made by iTunes.", "You can't restore that phone from this backup.", "Delete old backups in iTunes/Apple Devices: Preferences > Devices."),
            K(@"[\\/]packages[\\/]canonicalgroup|[\\/]ext4\.vhdx$|[\\/]docker[\\/]wsl([\\/]|$)", "WSL or Docker disk", "review", "The disk of a Linux system (WSL) or Docker.", "Everything inside that Linux or Docker is gone.", "Remove it from WSL or Docker itself (wsl --unregister, Docker settings)."),
            K(@"[\\/]onedrive([^\\/]*)([\\/]|$)", "OneDrive", "review", "Files synced with OneDrive.", "Deleting here deletes them in the cloud too.", "Use OneDrive > Free up space (Elixar's OneDrive tab) to keep them online only."),
            K(@"[\\/]downloads([\\/]|$)", "Downloads", "review", "Things you downloaded: installers, archives, documents.", "Only what you delete. Old installers are usually not needed.", "Sort by size and delete what you don't need, or let Policy manager move old files to Undo."),
            K(@"[\\/](documents|pictures|videos|music|desktop)([\\/]|$)", "Your files", "review", "Your own files.", "They are gone (from Undo after its days).", "Only delete what you know. Tools > Move off C: can move big folders to another drive."),
            K(@"[\\/]appdata[\\/](local|roaming|locallow)[\\/][^\\/]+[\\/]?$", "App data", "review", "Settings and data of one program.", "That program loses its settings, sign-in or saved data.", "Uninstall the program first, then Tools > Leftovers removes what stays behind."),
            K(@"\.(tmp|temp|old|bak)$", "Temporary or backup file", "review", "A temporary or backup copy.", "Usually nothing - check that the program that made it is closed.", "Delete it or move it to Undo."),
            K(@"\.(log|etl)$", "Log file", "safe", "A record a program writes as it works.", "Nothing, unless you are troubleshooting.", "Delete it."),
            K(@"\.(dmp|mdmp|hdmp)$", "Crash dump", "safe", "Memory saved from a crash.", "Nothing, unless someone is investigating it.", "Delete it."),
            K(@"\.(iso|img|zip|7z|rar|msi|exe)$", "Installer or archive", "review", "A setup file or packed archive, often already installed or unpacked.", "You'd need to download it again.", "Delete it if you already installed or unpacked it."),
            K(@"^[a-z]:[\\/]users[\\/][^\\/]+[\\/]?$", "A user's folder", "critical", "Everything of one Windows account.", "That person's files and settings are gone.", "Remove accounts in Windows Settings > Accounts."),
            K(@"^[a-z]:[\\/]?$", "The whole drive", "critical", "The drive itself.", "Everything.", "Open a folder in the space map to see what's inside."),
        };

        public static Explanation For(string path, List<DashLoc> places, Func<string, string> owner)
        {
            path = (path ?? "").Trim();
            var e = new Explanation { Path = path };
            // 1. the knowledge base
            var k = Base.FirstOrDefault(x => x.Re.IsMatch(path));
            // 2. the Dashboard's places (they know this PC's real paths and Elixar's own tool for each)
            DashLoc best = null;
            if (places != null)
                foreach (var l in places)
                    if (!string.IsNullOrEmpty(l.Path) && (string.Equals(path.TrimEnd('\\', '/'), l.Path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) || DashData.Inside(path, l.Path)))
                        if (best == null || l.Path.Length > best.Path.Length) best = l;
            if (k != null)
            {
                e.Title = k.Title; e.Tier = k.Tier; e.What = k.What; e.IfDeleted = k.IfDeleted; e.How = k.How; e.Source = "Elixar's built-in list";
                // a place on the Dashboard deeper than the match is more exact (for example Chrome's cache inside AppData)
                if (best != null && best.Tier != k.Tier && Specific(best, k, path)) { e.Title = best.Name; e.Tier = best.Tier; e.How = best.Action; }
            }
            else if (best != null)
            {
                e.Title = best.Name; e.Tier = best.Tier; e.How = best.Action; e.Source = "The Dashboard's map";
                e.What = "Part of \"" + best.Name + "\".";
                e.IfDeleted = best.Tier == "safe" ? "Nothing important; it is rebuilt." : best.Tier == "critical" ? "Windows or your programs can break." : "Depends on what's inside.";
            }
            else
            {
                string tier, reason; Scanner.Classify(path, out tier, out reason);
                e.Title = SafeName(path); e.Tier = tier == "caution" && reason.StartsWith("Unrecognised") ? "" : tier; e.What = reason; e.Source = "Elixar's file rules";
                e.IfDeleted = "Elixar doesn't know this one. If a program made it, that program may lose data or stop working.";
                e.How = "Find out which program made it (the folder above usually tells), or move it to Undo first so you can put it back.";
            }
            string app = null;
            try { if (owner != null) app = owner(path); } catch { }
            if (!string.IsNullOrEmpty(app)) e.What += " It belongs to " + app + ".";
            return e;
        }

        static bool Specific(DashLoc d, Known k, string path)
        {
            // the Dashboard place wins when it is a deeper folder than the folder the rule is about
            var m = k.Re.Match(path);
            return m.Success && d.Path.Length > m.Index + m.Length;
        }

        // only the last part of a path: never the user name
        public static string SafeName(string path)
        {
            string p = (path ?? "").TrimEnd('\\', '/');
            string n = p.Substring(p.LastIndexOfAny(new[] { '\\', '/' }) + 1);
            return string.IsNullOrEmpty(n) ? p : n;
        }

        public static string SearchUrl(string path)
        {
            // a user's own folder is named after the user: search for what it is, not for the name
            string name = Regex.IsMatch((path ?? "").TrimEnd('\\', '/'), @"^([a-z]:)?[\\/](users|home)[\\/][^\\/]+$", RegexOptions.IgnoreCase) ? "user profile" : SafeName(path);
            string q = "what is the \"" + name + "\" folder and is it safe to delete";
            return "https://www.bing.com/search?q=" + Uri.EscapeDataString(q);
        }

        public static string Text(Explanation e)
        {
            return Lang.T("Is this safe to delete?") + "\n" + e.Path + "\n\n" + Lang.T(e.Title) + ": " + Lang.T(e.Verdict) + "\n\n" +
                   Lang.T("What it is") + ": " + Lang.T(e.What) + "\n" + Lang.T("If it is deleted") + ": " + Lang.T(e.IfDeleted) + "\n" + Lang.T("The right way") + ": " + Lang.T(e.How);
        }
    }

    public class ExplainForm : Form
    {
        public readonly Explanation E;
        public ExplainForm(Explanation e, Color tierColor, float scale)
        {
            WindowFit.Watch(this);   // fits the screen (a tall window is made smaller; its page scrolls)
            E = e;
            Func<int, int> S = v => (int)Math.Round(v * scale);
            Text = Lang.T("Is this safe to delete?");
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f); BackColor = Color.White; ClientSize = new Size(S(560), S(360)); AutoScaleMode = AutoScaleMode.None;
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, AutoScroll = true, Padding = new Padding(S(16), S(12), S(16), S(10)) };   // a long explanation scrolls
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 6; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var band = new Label { AutoSize = false, Dock = DockStyle.Top, Height = S(40), BackColor = tierColor, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 12f),
                                   TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(10), 0, S(10), 0), UseMnemonic = false,
                                   Text = Lang.T(e.Verdict), Margin = new Padding(0, 0, 0, S(8)) };
            var title = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Text = Lang.T(e.Title), UseMnemonic = false, MaximumSize = new Size(S(520), 0) };
            var path = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "‎" + e.Path, UseMnemonic = false, MaximumSize = new Size(S(520), 0), Margin = new Padding(S(1), 0, 0, S(8)) };
            t.Controls.Add(band, 0, 0); t.Controls.Add(title, 0, 1); t.Controls.Add(path, 0, 2);
            t.Controls.Add(Part("What it is", e.What, S), 0, 3);
            t.Controls.Add(Part("If it is deleted", e.IfDeleted, S), 0, 4);
            t.Controls.Add(Part("The right way", e.How, S), 0, 5);
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(48), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(12), S(8), S(12), S(8)), BackColor = Color.FromArgb(244, 246, 250) };
            var ok = new Button { Text = Lang.T("Close"), AutoSize = true, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.System, Margin = new Padding(S(6), 0, 0, 0) };
            var web = new Button { Text = Lang.T("Search the web for it"), AutoSize = true, FlatStyle = FlatStyle.System, Margin = new Padding(S(6), 0, 0, 0) };
            var copy = new Button { Text = Lang.T("Copy"), AutoSize = true, FlatStyle = FlatStyle.System, Margin = new Padding(S(6), 0, 0, 0) };
            web.Click += (o, a) => { try { Shell.Open(Explain.SearchUrl(e.Path)); } catch { } };
            copy.Click += (o, a) => { try { Clipboard.SetText(Explain.Text(e)); } catch { } };
            row.Controls.AddRange(new Control[] { ok, copy, web });
            var tip = new ToolTip(); tip.SetToolTip(web, Lang.T("Opens a web search with only the folder's name (\"" + Explain.SafeName(e.Path) + "\"), never the full path."));
            var note = new Label { AutoSize = true, ForeColor = Color.Gray, Text = Lang.T("From " + e.Source + "."), Margin = new Padding(S(1), S(4), 0, 0) };
            t.Controls.Add(note, 0, 6);
            Controls.Add(t); Controls.Add(row);
            AcceptButton = ok; CancelButton = ok;
            Shown += (o, a) =>
            {
                int h = t.GetPreferredSize(new Size(ClientSize.Width, 0)).Height + row.Height;
                ClientSize = new Size(ClientSize.Width, Math.Min(S(680), Math.Max(S(320), h + S(6))));
            };
        }

        static Control Part(string head, string body, Func<int, int> S)
        {
            var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0, 0, 0, S(6)), WrapContents = false };
            p.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Text = Lang.T(head), Margin = new Padding(0) });
            p.Controls.Add(new Label { AutoSize = true, Text = Lang.T(body ?? ""), UseMnemonic = false, MaximumSize = new Size(S(520), 0), Margin = new Padding(0) });
            return p;
        }
    }

    public partial class MainForm
    {
        public static Action<ExplainForm> ExplainHook;   // self-test: see the window instead of showing it

        public Explanation ExplainOf(string path)
        {
            return Explain.For(path, dashLocs ?? DashData.Build(), p => { try { return OwnerName(p); } catch { return null; } });
        }

        public void ExplainPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            var e = ExplainOf(path);
            using (var f = new ExplainForm(e, MapColor(e.Tier), (float)s))
            {
                if (ExplainHook != null) { ExplainHook(f); return; }
                f.ShowDialog(this);
            }
        }
    }
}
