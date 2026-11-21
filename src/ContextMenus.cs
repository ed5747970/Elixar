// Tools > Right-click menu: what apps added to the right-click menu of files, folders, drives and the desktop.
// Flags entries whose program is gone (they slow the menu down or break it) and lets you turn an entry off and on again,
// the way Windows itself supports: a handler goes on Windows' "blocked shell extensions" list, a command gets
// "LegacyDisable". Nothing is deleted, so "Turn on" always puts it back. Windows' own entries are never changed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class MenuEntry
    {
        public string Name = "", Where = "", Kind = "", Hive = "", Key = "", Clsid = "", Program = "", Why = "";
        public bool Missing, Off, WindowsOwn;
        public CheckLevel Level;
    }

    public static class ContextMenus
    {
        // self-test: the "Classes" keys to read ({ "HKLM"/"HKCU", key }) and the key that holds the blocked list
        public static Func<List<Tuple<string, RegistryKey>>> ClassesOverride;
        public static Func<RegistryKey> BlockedOverride;
        public static Func<string, bool> ExistsOverride;

        static readonly string[][] Places = {
            new[] { "*", "Files" }, new[] { "AllFilesystemObjects", "Files and folders" }, new[] { "Directory", "Folders" }, new[] { "Folder", "Folders" },
            new[] { @"Directory\Background", "Inside a folder" }, new[] { "Drive", "Drives" }, new[] { "DesktopBackground", "Desktop" } };
        const string BlockedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

        static List<Tuple<string, RegistryKey>> Classes()
        {
            if (ClassesOverride != null) return ClassesOverride();
            var l = new List<Tuple<string, RegistryKey>>();
            if (!Win.IsWindows) return l;
            try { l.Add(Tuple.Create("HKLM", Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Classes"))); } catch { }
            try { l.Add(Tuple.Create("HKCU", Registry.CurrentUser.OpenSubKey(@"Software\Classes"))); } catch { }
            return l.Where(x => x.Item2 != null).ToList();
        }

        static RegistryKey Blocked(bool write)
        {
            if (BlockedOverride != null) return BlockedOverride();
            return write ? Registry.LocalMachine.CreateSubKey(BlockedPath) : Registry.LocalMachine.OpenSubKey(BlockedPath);
        }

        static bool Exists(string p) { return ExistsOverride != null ? ExistsOverride(p) : File.Exists(p); }

        static readonly Regex Guid = new Regex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$");

        // the program a command line starts
        public static string ProgramOf(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables((cmd ?? "").Trim());
            if (cmd.StartsWith("\"")) { int e = cmd.IndexOf('"', 1); return e > 1 ? cmd.Substring(1, e - 1) : ""; }
            var m = Regex.Match(cmd, @"^(.+?\.(exe|com|bat|cmd|dll))(\s|$)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : cmd.Split(' ')[0];
        }

        static string Expand(string p) { return Environment.ExpandEnvironmentVariables((p ?? "").Trim().Trim('"')); }

        static bool Rooted(string p) { return Regex.IsMatch(p ?? "", @"^([A-Za-z]:[\\/]|\\\\)"); }   // a Windows path with its drive

        static bool Windows(string program)
        {
            if (string.IsNullOrEmpty(program)) return true;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!Rooted(program)) return true;   // "rundll32.exe", "explorer.exe": Windows' own, found through its folders
            return !string.IsNullOrEmpty(win) && program.StartsWith(win.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static List<MenuEntry> Read()
        {
            var l = new List<MenuEntry>();
            var classes = Classes();
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { using (var b = Blocked(false)) if (b != null) foreach (var v in b.GetValueNames()) blocked.Add(v); } catch { }
            try
            {
                foreach (var c in classes)
                    foreach (var place in Places)
                    {
                        // handlers (shell extensions)
                        using (var hs = c.Item2.OpenSubKey(place[0] + @"\shellex\ContextMenuHandlers"))
                            if (hs != null)
                                foreach (var name in hs.GetSubKeyNames())
                                    using (var h = hs.OpenSubKey(name))
                                    {
                                        string clsid = (h == null ? "" : h.GetValue("") as string ?? "").Trim();
                                        if (!Guid.IsMatch(clsid) && Guid.IsMatch(name)) clsid = name;
                                        if (!Guid.IsMatch(clsid)) continue;
                                        var e = new MenuEntry { Name = name.TrimStart('-'), Where = place[1], Kind = "Handler", Hive = c.Item1, Key = place[0] + @"\shellex\ContextMenuHandlers\" + name, Clsid = clsid };
                                        e.Program = Server(classes, clsid);
                                        string friendly = ClsidName(classes, clsid); if (Guid.IsMatch(e.Name) && friendly.Length > 0) e.Name = friendly;
                                        e.Off = blocked.Contains(clsid);
                                        l.Add(e);
                                    }
                        // commands (verbs)
                        using (var vs = c.Item2.OpenSubKey(place[0] + @"\shell"))
                            if (vs != null)
                                foreach (var verb in vs.GetSubKeyNames())
                                    using (var v = vs.OpenSubKey(verb))
                                    using (var cmd = v == null ? null : v.OpenSubKey("command"))
                                    {
                                        if (v == null) continue;
                                        string text = (v.GetValue("MUIVerb") as string) ?? (v.GetValue("") as string) ?? verb;
                                        if (text.StartsWith("@")) text = verb;
                                        var e = new MenuEntry { Name = text.Replace("&", ""), Where = place[1], Kind = "Command", Hive = c.Item1, Key = place[0] + @"\shell\" + verb };
                                        string line = cmd == null ? "" : cmd.GetValue("") as string ?? "";
                                        e.Program = line.Length > 0 ? ProgramOf(line) : "";
                                        e.Off = v.GetValueNames().Contains("LegacyDisable", StringComparer.OrdinalIgnoreCase);
                                        if (e.Program.Length == 0 && v.GetValue("ExplorerCommandHandler") == null && v.OpenSubKey("DropTarget") == null && cmd == null) continue;   // a menu folder
                                        l.Add(e);
                                    }
                    }
            }
            finally { if (ClassesOverride == null) foreach (var c in classes) c.Item2.Dispose(); }
            foreach (var e in l) Judge(e);
            // the same entry under Directory and Folder: one row
            return l.GroupBy(e => e.Kind + "|" + e.Hive + "|" + (e.Kind == "Handler" ? e.Clsid : e.Key.Split('\\').Last()) + "|" + e.Program, StringComparer.OrdinalIgnoreCase)
                    .Select(g => { var f = g.First(); f.Where = string.Join(", ", g.Select(x => x.Where).Distinct()); return f; })
                    .OrderByDescending(e => e.Level).ThenBy(e => e.WindowsOwn).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static string Server(List<Tuple<string, RegistryKey>> classes, string clsid)
        {
            foreach (var c in classes.OrderBy(x => x.Item1 == "HKCU" ? 0 : 1))
                try { using (var k = c.Item2.OpenSubKey(@"CLSID\" + clsid + @"\InprocServer32")) { string p = k == null ? null : k.GetValue("") as string; if (!string.IsNullOrEmpty(p)) return Expand(p); } } catch { }
            return "";
        }
        static string ClsidName(List<Tuple<string, RegistryKey>> classes, string clsid)
        {
            foreach (var c in classes)
                try { using (var k = c.Item2.OpenSubKey(@"CLSID\" + clsid)) { string p = k == null ? null : k.GetValue("") as string; if (!string.IsNullOrEmpty(p)) return p; } } catch { }
            return "";
        }

        public static void Judge(MenuEntry e)
        {
            e.WindowsOwn = Windows(e.Program);
            e.Missing = !e.WindowsOwn && e.Program.Length > 0 && Rooted(e.Program) && !Exists(e.Program);
            if (e.Missing && !e.Off) { e.Level = CheckLevel.Warn; e.Why = "Broken: its program is gone. Turn it off."; }
            else if (e.Off) { e.Level = CheckLevel.Ok; e.Why = "Turned off."; }
            else if (e.WindowsOwn) { e.Level = CheckLevel.Ok; e.Why = "Windows' own."; }
            else { e.Level = CheckLevel.Info; e.Why = "Added by an app."; }
        }

        // turns an entry off (or on again). Windows' own entries are never changed. Needs administrator rights.
        public static string SetOff(MenuEntry e, bool off)
        {
            if (e.WindowsOwn) return "That's Windows' own entry: Elixar doesn't change it.";
            if (!Policy.Allowed("menu.change")) return "Your organization's policy doesn't allow this.";
            try
            {
                if (e.Kind == "Handler")
                {
                    if (!Guid.IsMatch(e.Clsid)) return "not a handler";
                    using (var b = Blocked(true))
                    {
                        if (off) b.SetValue(e.Clsid, e.Name, RegistryValueKind.String);
                        else if (b.GetValueNames().Contains(e.Clsid, StringComparer.OrdinalIgnoreCase)) b.DeleteValue(e.Clsid, false);
                    }
                }
                else
                {
                    if (!e.Key.Contains(@"\shell\") || e.Key.Contains("..")) return "not a command";
                    var classes = Classes();
                    try
                    {
                        var c = classes.FirstOrDefault(x => x.Item1 == e.Hive);
                        if (c == null) return "not found";
                        string root = c.Item1 == "HKLM" ? @"SOFTWARE\Classes\" : @"Software\Classes\";
                        using (var k = ClassesOverride != null ? c.Item2.OpenSubKey(e.Key, true) : (c.Item1 == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(root + e.Key, true))
                        {
                            if (k == null) return "The entry is gone.";
                            if (off) k.SetValue("LegacyDisable", "", RegistryValueKind.String); else k.DeleteValue("LegacyDisable", false);
                        }
                    }
                    finally { if (ClassesOverride == null) foreach (var c in classes) c.Item2.Dispose(); }
                }
            }
            catch (UnauthorizedAccessException) { return "Elixar needs administrator rights for that."; }
            catch (Exception ex) { return ex.Message; }
            e.Off = off; Judge(e);
            ChangeLog.Add("Settings", off ? "Right-click menu: turned an entry off" : "Right-click menu: turned an entry on", e.Name, e.Where, e.Program, e.Kind == "Handler" ? "Blocked list: " + e.Clsid : "LegacyDisable: " + e.Key, false);
            return null;
        }
    }
}
