// Sysprep answer files (unattend.xml): build one from simple choices, read an existing one, check it against the rules
// Windows Setup uses, and save it where Sysprep looks for it (C:\Windows\System32\Sysprep\unattend.xml).
//
// Passes used with Sysprep: generalize (drivers, rearm), specialize (computer name, time zone, owner, key, CopyProfile),
// oobeSystem (languages, setup screens, a local account, auto sign-in, first sign-in commands). windowsPE is ignored by
// Sysprep. A password in an answer file is only encoded (Base64), not encrypted: Elixar says so, never keeps it itself,
// and can add a first sign-in command that deletes the answer files once setup is done.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CDriveCleaner
{
    public class UnattendSpec
    {
        public string Arch = "amd64";
        public string ComputerName = "*", Owner = "", Organization = "", ProductKey = "", TimeZone = "";
        public string UILanguage = "", SystemLocale = "", UserLocale = "", InputLocale = "";
        public bool HideEula = true, HideOem = true, HideOnline = true, HideWireless = false, SkipPrivacy = true;
        public bool CreateAccount, AutoLogon;
        public string AccountName = "", AccountPassword = "";
        public bool EnableAdmin;                  // turn the built-in Administrator on again after Sysprep (it turns it off)
        public string AdminPassword = "";        // its password (UserAccounts/AdministratorPassword)
        public bool CopyProfile, PersistDrivers = true, SkipRearm;
        public List<string> FirstLogon = new List<string>();
        public bool DeleteAfter = true;
        public List<string> Unknown = new List<string>();   // settings in a loaded file that this editor doesn't show (kept out when saving)
    }

    public class UnattendProblem { public int Level; public string Text = ""; }   // 3 stops setup, 2 should fix, 1 good to know

    public static class Unattend
    {
        public const string Ns = "urn:schemas-microsoft-com:unattend", Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State", Token = "31bf3856ad364e35";
        public static string SysprepFile { get { return Path.Combine(Path.GetDirectoryName(Sysprep.Exe), "unattend.xml"); } }
        static readonly string[] Passes = { "windowsPE", "offlineServicing", "generalize", "specialize", "auditSystem", "auditUser", "oobeSystem" };

        public static string ThisArch()
        {
            // Windows' own record (a program can change the environment variable of the same name)
            string a = "";
            try
            {
                using (var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? Microsoft.Win32.RegistryView.Registry64 : Microsoft.Win32.RegistryView.Default))
                using (var k = hk.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                    a = k == null ? "" : k.GetValue("PROCESSOR_ARCHITECTURE") as string ?? "";
            }
            catch { }
            a = a.ToUpperInvariant();
            return a == "ARM64" ? "arm64" : a == "X86" && !Environment.Is64BitOperatingSystem ? "x86" : "amd64";
        }

        public static UnattendSpec Defaults()
        {
            var s = new UnattendSpec { Arch = ThisArch() };
            try { s.TimeZone = TimeZoneInfo.Local.Id; } catch { }
            string ui = CultureInfo.InstalledUICulture.Name, user = CultureInfo.CurrentCulture.Name;
            s.UILanguage = string.IsNullOrEmpty(ui) ? "en-US" : ui;
            s.SystemLocale = s.UserLocale = string.IsNullOrEmpty(user) ? s.UILanguage : user;
            s.InputLocale = s.UILanguage == s.UserLocale ? s.UILanguage : s.UILanguage + ";" + s.UserLocale;
            return s;
        }

        // the "hidden" form Windows uses in answer files: Base64 of the password followed by the element name (UTF-16)
        public static string Encode(string password, string suffix) { return Convert.ToBase64String(Encoding.Unicode.GetBytes((password ?? "") + suffix)); }
        public static string Decode(string value, string suffix)
        {
            try { string t = Encoding.Unicode.GetString(Convert.FromBase64String(value ?? "")); return t.EndsWith(suffix) ? t.Substring(0, t.Length - suffix.Length) : t; }
            catch { return value ?? ""; }
        }

        // turns the built-in Administrator on at the first sign-in. It is found by its ID (the SID ending in -500), not by its
        // name, so this works in every Windows language and after the account was renamed. Microsoft: Sysprep's generalize
        // turns the account off, and setting AdministratorPassword alone doesn't turn it on.
        public static string EnableAdminCommand { get { return "powershell -NoProfile -Command \"Get-LocalUser | Where-Object { $_.SID.Value -like 'S-1-5-21-*-500' } | Enable-LocalUser\""; } }
        static bool IsEnableAdmin(string line)
        {
            if (line == EnableAdminCommand) return true;
            return Regex.IsMatch(line, @"^(cmd(\.exe)? /[cd] )*net(\.exe)? user administrator /active:yes$", RegexOptions.IgnoreCase);
        }

        public static string DeleteCommand { get { return "cmd /c del /f /q \"%WINDIR%\\System32\\Sysprep\\unattend.xml\" \"%WINDIR%\\Panther\\unattend.xml\""; } }

        // ---------- writing ----------
        public static string Build(UnattendSpec s)
        {
            var sb = new StringBuilder();
            var ws = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
            using (var w = XmlWriter.Create(new StringWriterUtf8(sb), ws))
            {
                w.WriteStartDocument();
                w.WriteStartElement("unattend", Ns);
                w.WriteAttributeString("xmlns", "wcm", null, Wcm);

                Action<string> comp = name =>
                {
                    w.WriteStartElement("component", Ns);
                    w.WriteAttributeString("name", name); w.WriteAttributeString("processorArchitecture", s.Arch); w.WriteAttributeString("publicKeyToken", Token);
                    w.WriteAttributeString("language", "neutral"); w.WriteAttributeString("versionScope", "nonSxS");
                };
                Action<string, string> el = (n, v) => { if (!string.IsNullOrEmpty(v)) w.WriteElementString(n, Ns, v); };

                // generalize
                if (s.PersistDrivers || s.SkipRearm)
                {
                    w.WriteStartElement("settings", Ns); w.WriteAttributeString("pass", "generalize");
                    if (s.PersistDrivers) { comp("Microsoft-Windows-PnpSysprep"); el("PersistAllDeviceInstalls", "true"); w.WriteEndElement(); }
                    if (s.SkipRearm) { comp("Microsoft-Windows-Security-SPP"); el("SkipRearm", "1"); w.WriteEndElement(); }
                    w.WriteEndElement();
                }
                // specialize
                w.WriteStartElement("settings", Ns); w.WriteAttributeString("pass", "specialize");
                comp("Microsoft-Windows-Shell-Setup");
                el("ComputerName", string.IsNullOrEmpty(s.ComputerName) ? "*" : s.ComputerName);
                if (s.CopyProfile) el("CopyProfile", "true");
                el("ProductKey", s.ProductKey.Trim().ToUpperInvariant());
                el("RegisteredOrganization", s.Organization); el("RegisteredOwner", s.Owner);
                el("TimeZone", s.TimeZone);
                w.WriteEndElement(); w.WriteEndElement();
                // oobeSystem
                w.WriteStartElement("settings", Ns); w.WriteAttributeString("pass", "oobeSystem");
                comp("Microsoft-Windows-International-Core");
                el("InputLocale", s.InputLocale); el("SystemLocale", s.SystemLocale); el("UILanguage", s.UILanguage); el("UserLocale", s.UserLocale);
                w.WriteEndElement();
                comp("Microsoft-Windows-Shell-Setup");
                if (s.CreateAccount && s.AutoLogon && s.AccountName.Length > 0)
                {
                    w.WriteStartElement("AutoLogon", Ns);
                    w.WriteStartElement("Password", Ns); el("Value", Encode(s.AccountPassword, "Password")); el("PlainText", "false"); w.WriteEndElement();
                    el("Enabled", "true"); el("LogonCount", "1"); el("Username", s.AccountName);
                    w.WriteEndElement();
                }
                var cmds = s.FirstLogon.Where(c => c.Trim().Length > 0).ToList();
                if (s.EnableAdmin) cmds.Insert(0, EnableAdminCommand);
                if (s.DeleteAfter) cmds.Add(DeleteCommand);
                if (cmds.Count > 0)
                {
                    w.WriteStartElement("FirstLogonCommands", Ns);
                    for (int i = 0; i < cmds.Count; i++)
                    {
                        w.WriteStartElement("SynchronousCommand", Ns); w.WriteAttributeString("wcm", "action", Wcm, "add");
                        el("CommandLine", cmds[i].Trim()); el("Description", i == cmds.Count - 1 && s.DeleteAfter ? "Delete the answer files" : i == 0 && s.EnableAdmin ? "Turn on the built-in Administrator" : "Command " + (i + 1)); el("Order", (i + 1).ToString(CultureInfo.InvariantCulture));
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteStartElement("OOBE", Ns);
                el("HideEULAPage", s.HideEula ? "true" : "false");
                if (s.CreateAccount) el("HideLocalAccountScreen", "true");
                el("HideOEMRegistrationScreen", s.HideOem ? "true" : "false");
                el("HideOnlineAccountScreens", s.HideOnline ? "true" : "false");
                el("HideWirelessSetupInOOBE", s.HideWireless ? "true" : "false");
                if (s.SkipPrivacy) el("ProtectYourPC", "3");
                w.WriteEndElement();
                bool local = s.CreateAccount && s.AccountName.Length > 0, admin = !string.IsNullOrEmpty(s.AdminPassword);
                if (local || admin)
                {
                    w.WriteStartElement("UserAccounts", Ns);
                    if (admin) { w.WriteStartElement("AdministratorPassword", Ns); el("Value", Encode(s.AdminPassword, "AdministratorPassword")); el("PlainText", "false"); w.WriteEndElement(); }
                    if (local)
                    {
                        w.WriteStartElement("LocalAccounts", Ns);
                        w.WriteStartElement("LocalAccount", Ns); w.WriteAttributeString("wcm", "action", Wcm, "add");
                        w.WriteStartElement("Password", Ns); el("Value", Encode(s.AccountPassword, "Password")); el("PlainText", "false"); w.WriteEndElement();
                        el("DisplayName", s.AccountName); el("Group", "Administrators"); el("Name", s.AccountName);
                        w.WriteEndElement(); w.WriteEndElement();
                    }
                    w.WriteEndElement();
                }
                w.WriteEndElement(); w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndDocument();
            }
            return sb.ToString();
        }

        class StringWriterUtf8 : StringWriter { public StringWriterUtf8(StringBuilder sb) : base(sb, CultureInfo.InvariantCulture) { } public override Encoding Encoding { get { return new UTF8Encoding(false); } } }

        // ---------- reading (no DTDs, no outside files: an answer file is only data) ----------
        public static XmlDocument Load(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
            var doc = new XmlDocument { XmlResolver = null };
            using (var r = XmlReader.Create(new StringReader(xml), settings)) doc.Load(r);
            return doc;
        }

        static XmlNamespaceManager Nsm(XmlDocument d) { var m = new XmlNamespaceManager(d.NameTable); m.AddNamespace("u", Ns); m.AddNamespace("wcm", Wcm); return m; }

        static readonly HashSet<string> Known = new HashSet<string>(new[] {
            "generalize/Microsoft-Windows-PnpSysprep/PersistAllDeviceInstalls", "generalize/Microsoft-Windows-Security-SPP/SkipRearm",
            "specialize/Microsoft-Windows-Shell-Setup/ComputerName", "specialize/Microsoft-Windows-Shell-Setup/CopyProfile", "specialize/Microsoft-Windows-Shell-Setup/ProductKey",
            "specialize/Microsoft-Windows-Shell-Setup/RegisteredOrganization", "specialize/Microsoft-Windows-Shell-Setup/RegisteredOwner", "specialize/Microsoft-Windows-Shell-Setup/TimeZone",
            "oobeSystem/Microsoft-Windows-International-Core/InputLocale", "oobeSystem/Microsoft-Windows-International-Core/SystemLocale",
            "oobeSystem/Microsoft-Windows-International-Core/UILanguage", "oobeSystem/Microsoft-Windows-International-Core/UserLocale",
            "oobeSystem/Microsoft-Windows-Shell-Setup/AutoLogon", "oobeSystem/Microsoft-Windows-Shell-Setup/FirstLogonCommands",
            "oobeSystem/Microsoft-Windows-Shell-Setup/OOBE", "oobeSystem/Microsoft-Windows-Shell-Setup/UserAccounts" });

        public static UnattendSpec Read(string xml)
        {
            var d = Load(xml); var m = Nsm(d);
            var s = new UnattendSpec { CreateAccount = false, DeleteAfter = false, PersistDrivers = false, HideEula = false, HideOem = false, HideOnline = false, SkipPrivacy = false };
            Func<string, string, string, string> get = (pass, comp, path) =>
            {
                var n = d.SelectSingleNode("/u:unattend/u:settings[@pass='" + pass + "']/u:component[@name='" + comp + "']/" + path, m);
                return n == null ? null : n.InnerText.Trim();
            };
            var anyComp = d.SelectSingleNode("//u:component", m) as XmlElement;
            if (anyComp != null && anyComp.GetAttribute("processorArchitecture").Length > 0) s.Arch = anyComp.GetAttribute("processorArchitecture");
            s.PersistDrivers = get("generalize", "Microsoft-Windows-PnpSysprep", "u:PersistAllDeviceInstalls") == "true";
            s.SkipRearm = get("generalize", "Microsoft-Windows-Security-SPP", "u:SkipRearm") == "1";
            const string sh = "Microsoft-Windows-Shell-Setup", ic = "Microsoft-Windows-International-Core";
            s.ComputerName = get("specialize", sh, "u:ComputerName") ?? "*";
            s.CopyProfile = (get("specialize", sh, "u:CopyProfile") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
            s.ProductKey = get("specialize", sh, "u:ProductKey") ?? get("specialize", sh, "u:ProductKey/u:Key") ?? "";
            s.Organization = get("specialize", sh, "u:RegisteredOrganization") ?? get("oobeSystem", sh, "u:RegisteredOrganization") ?? "";
            s.Owner = get("specialize", sh, "u:RegisteredOwner") ?? get("oobeSystem", sh, "u:RegisteredOwner") ?? "";
            s.TimeZone = get("specialize", sh, "u:TimeZone") ?? get("oobeSystem", sh, "u:TimeZone") ?? "";
            s.InputLocale = get("oobeSystem", ic, "u:InputLocale") ?? ""; s.SystemLocale = get("oobeSystem", ic, "u:SystemLocale") ?? "";
            s.UILanguage = get("oobeSystem", ic, "u:UILanguage") ?? ""; s.UserLocale = get("oobeSystem", ic, "u:UserLocale") ?? "";
            Func<string, bool> t = x => (get("oobeSystem", sh, "u:OOBE/u:" + x) ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
            s.HideEula = t("HideEULAPage"); s.HideOem = t("HideOEMRegistrationScreen"); s.HideOnline = t("HideOnlineAccountScreens"); s.HideWireless = t("HideWirelessSetupInOOBE");
            s.SkipPrivacy = get("oobeSystem", sh, "u:OOBE/u:ProtectYourPC") == "3";
            var acc = d.SelectSingleNode("/u:unattend/u:settings[@pass='oobeSystem']/u:component[@name='" + sh + "']/u:UserAccounts/u:LocalAccounts/u:LocalAccount", m);
            if (acc != null)
            {
                s.CreateAccount = true;
                var nm = acc.SelectSingleNode("u:Name", m); s.AccountName = nm == null ? "" : nm.InnerText.Trim();
                var pv = acc.SelectSingleNode("u:Password/u:Value", m); var pt = acc.SelectSingleNode("u:Password/u:PlainText", m);
                s.AccountPassword = pv == null ? "" : pt != null && pt.InnerText.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? pv.InnerText : Decode(pv.InnerText, "Password");
            }
            var apv = d.SelectSingleNode("/u:unattend/u:settings[@pass='oobeSystem']/u:component[@name='" + sh + "']/u:UserAccounts/u:AdministratorPassword/u:Value", m);
            var apt = d.SelectSingleNode("/u:unattend/u:settings[@pass='oobeSystem']/u:component[@name='" + sh + "']/u:UserAccounts/u:AdministratorPassword/u:PlainText", m);
            if (apv != null) s.AdminPassword = apt != null && apt.InnerText.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ? apv.InnerText : Decode(apv.InnerText, "AdministratorPassword");
            s.AutoLogon = (get("oobeSystem", sh, "u:AutoLogon/u:Enabled") ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);
            foreach (XmlNode c in d.SelectNodes("/u:unattend/u:settings[@pass='oobeSystem']/u:component[@name='" + sh + "']/u:FirstLogonCommands/u:SynchronousCommand/u:CommandLine", m))
            {
                string line = c.InnerText.Trim();
                if (line == DeleteCommand) s.DeleteAfter = true; else if (IsEnableAdmin(line)) s.EnableAdmin = true; else if (line.Length > 0) s.FirstLogon.Add(line);
            }
            // what this editor doesn't show: listed, so saving over the file never drops them silently
            foreach (XmlElement set in d.SelectNodes("/u:unattend/u:settings", m))
                foreach (XmlElement comp in set.SelectNodes("u:component", m))
                    foreach (XmlNode child in comp.ChildNodes)
                    {
                        if (child is XmlElement && !Known.Contains(set.GetAttribute("pass") + "/" + comp.GetAttribute("name") + "/" + child.LocalName))
                            s.Unknown.Add(set.GetAttribute("pass") + ": " + comp.GetAttribute("name") + " / " + child.LocalName);
                        else if (child is XmlElement && child.LocalName == "UserAccounts")   // only these two parts are shown (not domain accounts)
                            foreach (XmlNode g in child.ChildNodes)
                                if (g is XmlElement && g.LocalName != "LocalAccounts" && g.LocalName != "AdministratorPassword")
                                    s.Unknown.Add(set.GetAttribute("pass") + ": " + comp.GetAttribute("name") + " / UserAccounts / " + g.LocalName);
                    }
            return s;
        }

        // ---------- checking ----------
        static readonly Regex KeyFmt = new Regex(@"^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$"), NameFmt = new Regex(@"^[A-Za-z0-9-]{1,15}$");

        public static bool ValidLocale(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            if (Regex.IsMatch(v, @"^[0-9A-Fa-f]{4}:[0-9A-Fa-f]{8}$")) return true;
            try { var c = CultureInfo.GetCultureInfo(v); return c != null && !c.Equals(CultureInfo.InvariantCulture) && c.Name.Equals(v, StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        public static List<UnattendProblem> Check(UnattendSpec s)
        {
            var p = new List<UnattendProblem>();
            Action<int, string> add = (l, t) => p.Add(new UnattendProblem { Level = l, Text = t });
            string n = s.ComputerName ?? "";
            if (n != "*" && (!NameFmt.IsMatch(n) || n.All(char.IsDigit) || n.StartsWith("-") || n.EndsWith("-")))
                add(3, "Computer name: up to 15 letters, digits or hyphens, not only digits (or * for a random name).");
            if (n == "*") add(1, "Computer name: Windows picks a random name (DESKTOP-...). Every PC from this image gets its own.");
            else add(1, "Computer name: every PC set up from this image gets the same name. Use * unless the image is for one PC.");
            if (s.ProductKey.Trim().Length > 0 && !KeyFmt.IsMatch(s.ProductKey.Trim().ToUpperInvariant())) add(3, "Product key: 5 groups of 5 letters or digits (XXXXX-XXXXX-XXXXX-XXXXX-XXXXX).");
            if (s.TimeZone.Length > 0 && Win.IsWindows)
            {
                bool ok = false; try { TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone); ok = true; } catch { }
                if (!ok) add(3, "Time zone: \"" + s.TimeZone + "\" is not a Windows time zone name (for example \"Israel Standard Time\").");
            }
            foreach (var kv in new[] { Tuple.Create("Windows language", s.UILanguage), Tuple.Create("System locale", s.SystemLocale), Tuple.Create("User locale", s.UserLocale) })
                if (kv.Item2.Length > 0 && !ValidLocale(kv.Item2)) add(3, kv.Item1 + ": \"" + kv.Item2 + "\" is not a language name (for example en-US or he-IL).");
            foreach (var k in (s.InputLocale ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (!ValidLocale(k.Trim())) add(3, "Keyboards: \"" + k.Trim() + "\" is not a language or keyboard code (for example en-US;he-IL).");
            if (s.CreateAccount)
            {
                if (!Regex.IsMatch(s.AccountName ?? "", @"^[^""/\\\[\]:;|=,+*?<>@]{1,20}$") || s.AccountName.Trim().Length == 0 || s.AccountName.EndsWith("."))
                    add(3, "Account name: 1 to 20 characters, without \" / \\ [ ] : ; | = , + * ? < > @ and not ending with a dot.");
                if (new[] { "administrator", "guest", "defaultaccount", "wdagutilityaccount" }.Contains((s.AccountName ?? "").Trim().ToLowerInvariant()))
                    add(3, "Account name: \"" + s.AccountName + "\" is a built-in Windows account. Choose another name (for the built-in Administrator, use \"Turn on the built-in Administrator\").");
                if (string.IsNullOrEmpty(s.AccountPassword)) add(2, "The account has no password: anyone can sign in to the new PC. Set one, or ask users to set one at first sign-in.");
            }
            else if (s.HideOnline) add(1, "No account is created and the online-account screens are hidden: Windows asks for a local account during setup.");
            if (s.EnableAdmin && string.IsNullOrEmpty(s.AdminPassword)) add(3, "The built-in Administrator would be turned on without a password. Set its password.");
            if (!string.IsNullOrEmpty(s.AdminPassword) && s.AdminPassword.Length < 8) add(2, "The Administrator password is short. Everyone knows the name Administrator: use at least 8 characters, better 12 or more.");
            if (s.EnableAdmin && !s.DeleteAfter) add(3, "The Administrator password would stay on the new PC in the answer file. Turn on \"Delete the answer file after setup\".");
            if (s.EnableAdmin) add(1, "The built-in Administrator is turned on at the first sign-in (found by its ID, so it works in every Windows language). Turn it off when you no longer need it.");
            else if (!string.IsNullOrEmpty(s.AdminPassword)) add(1, "The built-in Administrator gets this password but stays off (Windows' default after Sysprep).");
            if ((s.CreateAccount && !string.IsNullOrEmpty(s.AccountPassword)) || !string.IsNullOrEmpty(s.AdminPassword))
                add(2, "The password is only encoded in the answer file (Base64), not encrypted: anyone who can read the file can read it. Keep \"Delete the answer file after setup\" on.");
            if (s.AutoLogon && !s.CreateAccount) add(2, "Sign in automatically needs the account above.");
            if (s.CopyProfile) add(1, "CopyProfile copies the settings of the account you are signed in with now into every new account. Customize that account before running Sysprep.");
            if (s.SkipRearm) add(1, "SkipRearm keeps the activation timer as it is. Use it when Sysprep stops because it was run too many times.");
            if (!s.DeleteAfter && !s.EnableAdmin && (s.CreateAccount || s.ProductKey.Length > 0 || s.AdminPassword.Length > 0)) add(2, "The answer file stays on the new PC with the account or product key in it. Turn on \"Delete the answer file after setup\".");
            if (s.Unknown.Count > 0) add(2, s.Unknown.Count + " setting(s) in the loaded file are not shown here and are not saved by this editor: " + string.Join("; ", s.Unknown.Take(6)) + (s.Unknown.Count > 6 ? "; ..." : "") + ". Save under a new name to keep the original.");
            return p.OrderByDescending(x => x.Level).ToList();
        }

        // checks of a file as Windows Setup reads it (structure, passes, architecture), on top of Check
        public static List<UnattendProblem> CheckXml(string xml, string thisArch)
        {
            var p = new List<UnattendProblem>();
            XmlDocument d;
            try { d = Load(xml); } catch (Exception e) { p.Add(new UnattendProblem { Level = 3, Text = "The file is not valid XML: " + e.Message }); return p; }
            if (d.DocumentElement == null || d.DocumentElement.LocalName != "unattend" || d.DocumentElement.NamespaceURI != Ns)
            { p.Add(new UnattendProblem { Level = 3, Text = "This is not a Windows answer file (the root must be <unattend xmlns=\"" + Ns + "\">)." }); return p; }
            var m = Nsm(d);
            var seen = new HashSet<string>();
            foreach (XmlElement set in d.SelectNodes("/u:unattend/u:settings", m))
            {
                string pass = set.GetAttribute("pass");
                if (!Passes.Contains(pass)) p.Add(new UnattendProblem { Level = 3, Text = "Unknown setup pass \"" + pass + "\"." });
                if (pass == "windowsPE") p.Add(new UnattendProblem { Level = 1, Text = "The windowsPE part is only used when installing from media; Sysprep ignores it." });
                foreach (XmlElement c in set.SelectNodes("u:component", m))
                {
                    string name = c.GetAttribute("name"), arch = c.GetAttribute("processorArchitecture");
                    if (!seen.Add(pass + "|" + name + "|" + arch)) p.Add(new UnattendProblem { Level = 3, Text = name + " appears twice in " + pass + "." });
                    if (arch.Length > 0 && !arch.Equals(thisArch, StringComparison.OrdinalIgnoreCase) && arch != "wow64")
                        p.Add(new UnattendProblem { Level = 3, Text = name + " (" + pass + ") is for " + arch + ", but this Windows is " + thisArch + ": Setup ignores it." });
                    if (c.GetAttribute("publicKeyToken") != Token) p.Add(new UnattendProblem { Level = 3, Text = name + " (" + pass + ") has a wrong publicKeyToken." });
                    if (pass == "oobeSystem" && name == "Microsoft-Windows-Shell-Setup" && c.SelectSingleNode(".//u:CopyProfile", m) != null)
                        p.Add(new UnattendProblem { Level = 3, Text = "CopyProfile must be in the specialize pass, not oobeSystem." });
                }
            }
            foreach (XmlNode pt in d.SelectNodes("//u:PlainText", m))
                if (pt.InnerText.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) { p.Add(new UnattendProblem { Level = 2, Text = "A password is stored as plain text. Elixar saves passwords encoded (still readable by anyone who has the file)." }); break; }
            try { p.AddRange(Check(Read(xml))); } catch { }
            return p.OrderByDescending(x => x.Level).ToList();
        }
    }
}
