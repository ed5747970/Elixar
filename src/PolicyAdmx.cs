// Group Policy template for IT: Elixar.admx + en-US\Elixar.adml + he-IL\Elixar.adml, made from the same rules as Policy manager.
// Copy them to C:\Windows\PolicyDefinitions (or the domain's Central Store) and the rules show in the Group Policy editor under
// Computer Configuration > Administrative Templates > Elixar; Intune can import the same files (Devices > Configuration > Import ADMX).
// Each setting writes one text value to HKLM\SOFTWARE\Policies\Elixar, exactly what the .reg export writes.
// The per-item cleaning rules (Policy manager > Cleaning items) are not in the template: use the .reg export for those.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace CDriveCleaner
{
    public static partial class Policy
    {
        public const string AdmxNamespace = "Elisar.Policies.Elixar";

        // a name Group Policy accepts: letters, digits and _ only
        public static string AdmxId(string key)
        {
            var sb = new StringBuilder();
            foreach (char c in key) sb.Append(c < 128 && char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }
        static string X(string s) { return SecurityElement.Escape(s ?? ""); }

        static string Group(PolicyRule r) { return "cat_" + AdmxId(r.Group); }

        public static string AdmxText()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append("<!-- Elixar ").Append(Updates.Current).Append(" - made by Elisar. Settings go to HKLM\\SOFTWARE\\Policies\\Elixar. -->\r\n");
            sb.Append("<policyDefinitions xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions\">\r\n");
            sb.Append("  <policyNamespaces>\r\n    <target prefix=\"elixar\" namespace=\"").Append(AdmxNamespace).Append("\" />\r\n  </policyNamespaces>\r\n");
            sb.Append("  <resources minRequiredRevision=\"1.0\" />\r\n");
            sb.Append("  <supportedOn>\r\n    <definitions>\r\n      <definition name=\"SUPPORTED_Elixar\" displayName=\"$(string.SUPPORTED_Elixar)\" />\r\n    </definitions>\r\n  </supportedOn>\r\n");
            sb.Append("  <categories>\r\n    <category name=\"Elixar\" displayName=\"$(string.Elixar)\" />\r\n");
            foreach (var g in Rules.Select(Group).Distinct())
                sb.Append("    <category name=\"").Append(g).Append("\" displayName=\"$(string.").Append(g).Append(")\">\r\n      <parentCategory ref=\"Elixar\" />\r\n    </category>\r\n");
            sb.Append("  </categories>\r\n  <policies>\r\n");
            foreach (var r in Rules)
            {
                string id = AdmxId(r.Key);
                sb.Append("    <policy name=\"").Append(id).Append("\" class=\"Machine\" displayName=\"$(string.").Append(id).Append(")\" explainText=\"$(string.").Append(id)
                  .Append("_Help)\" presentation=\"$(presentation.").Append(id).Append(")\" key=\"SOFTWARE\\Policies\\Elixar\">\r\n");
                sb.Append("      <parentCategory ref=\"").Append(Group(r)).Append("\" />\r\n      <supportedOn ref=\"SUPPORTED_Elixar\" />\r\n");
                sb.Append("      <elements>\r\n        <enum id=\"").Append(id).Append("\" valueName=\"").Append(X(r.Key)).Append("\" required=\"true\">\r\n");
                for (int i = 0; i < r.Values.Length; i++)
                    sb.Append("          <item displayName=\"$(string.").Append(id).Append("_").Append(i).Append(")\"><value><string>").Append(X(r.Values[i])).Append("</string></value></item>\r\n");
                sb.Append("        </enum>\r\n      </elements>\r\n    </policy>\r\n");
            }
            sb.Append("  </policies>\r\n</policyDefinitions>\r\n");
            return sb.ToString();
        }

        // the texts in the language Elixar shows now (Lang.Code)
        public static string AdmlText()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
            sb.Append("<policyDefinitionResources xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" revision=\"1.0\" schemaVersion=\"1.0\" xmlns=\"http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions\">\r\n");
            sb.Append("  <displayName>Elixar</displayName>\r\n  <description>").Append(X(Lang.T("Elixar's rules for every PC (Policy manager).")))
              .Append("</description>\r\n  <resources>\r\n    <stringTable>\r\n");
            Action<string, string> s = (id, text) => sb.Append("      <string id=\"").Append(id).Append("\">").Append(X(text)).Append("</string>\r\n");
            s("Elixar", "Elixar");
            s("SUPPORTED_Elixar", Lang.T("Elixar 1.2.53 or later"));
            foreach (var g in Rules.GroupBy(Group)) s(g.Key, Lang.T(g.First().Group));
            foreach (var r in Rules)
            {
                string id = AdmxId(r.Key);
                s(id, Lang.T(r.Name));
                string help = Lang.T(r.Help) + "\n\n" + Lang.T("Default (when not configured):") + " " + Lang.T(r.Label(r.Default)) + "\n" +
                              Lang.T("Registry:") + " HKLM\\SOFTWARE\\Policies\\Elixar, " + r.Key + "\n" +
                              Lang.T("Elixar shows this rule as set by your organization, and it can't be changed on the PC.");
                if (CompanyBlocks.Contains(r.Key))
                    help += "\n\n" + Lang.T("On a PC joined to a domain or Microsoft Entra ID this is Not allowed when it isn't configured. Choose Allowed here to allow it on the PCs this applies to.");
                s(id + "_Help", help);
                for (int i = 0; i < r.Values.Length; i++) s(id + "_" + i, Lang.T(r.Labels[i]));
            }
            sb.Append("    </stringTable>\r\n    <presentationTable>\r\n");
            foreach (var r in Rules)
            {
                string id = AdmxId(r.Key);
                sb.Append("      <presentation id=\"").Append(id).Append("\">\r\n        <dropdownList refId=\"").Append(id).Append("\" noSort=\"true\" defaultItem=\"")
                  .Append(Math.Max(0, Array.IndexOf(r.Values, r.Default))).Append("\">").Append(X(Lang.T(r.Name))).Append("</dropdownList>\r\n      </presentation>\r\n");
            }
            sb.Append("    </presentationTable>\r\n  </resources>\r\n</policyDefinitionResources>\r\n");
            return sb.ToString();
        }

        // writes Elixar.admx in folder and the texts in en-US and he-IL beside it; returns the files written
        public static List<string> SaveAdmx(string folder)
        {
            var files = new List<string>();
            Directory.CreateDirectory(folder);
            string admx = Path.Combine(folder, "Elixar.admx");
            SafeFile.Save(admx, AdmxText()); files.Add(admx);
            string keep = Lang.Code;
            try
            {
                foreach (var loc in new[] { new[] { "en", "en-US" }, new[] { "he", "he-IL" } })
                {
                    Lang.Code = loc[0];
                    string dir = Path.Combine(folder, loc[1]); Directory.CreateDirectory(dir);
                    string adml = Path.Combine(dir, "Elixar.adml");
                    SafeFile.Save(adml, AdmlText()); files.Add(adml);
                }
            }
            finally { Lang.Code = keep; }
            return files;
        }
    }
}
