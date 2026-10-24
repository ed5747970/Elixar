// Interface language: English (built in), Hebrew and Arabic (right to left), Russian and Spanish. Text not in a table stays in English.
// Hebrew covers every screen; Arabic, Russian and Spanish cover the main screens (LangMore.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static partial class Lang
    {
        public static readonly string[] Codes = { "en", "he", "ar", "ru", "es" };
        public static string Code = "en";
        // Hebrew: the full translation (and Hebrew-only texts such as email reports); Rtl: the window runs right to left
        public static bool Hebrew { get { return Code == "he"; } set { Code = value ? "he" : Code == "he" ? "en" : Code; } }
        public static bool Rtl { get { return Code == "he" || Code == "ar"; } }
        public static bool On { get { return Code != "en"; } }
        public static void Init(string lang) { Code = Codes.Contains(lang) ? lang : "en"; }

        static readonly Dictionary<string, Dictionary<string, string>> tables = new Dictionary<string, Dictionary<string, string>>();
        static readonly Dictionary<string, List<Tuple<Regex, Func<Match, string>>>> patterns = new Dictionary<string, List<Tuple<Regex, Func<Match, string>>>>();
        static List<Tuple<Regex, Func<Match, string>>> building;
        static readonly object tableGate = new object();
        static Dictionary<string, string> Table
        {
            get
            {
                string c = Code;
                lock (tableGate)
                {
                    Dictionary<string, string> t;
                    if (tables.TryGetValue(c, out t)) return t;
                    building = new List<Tuple<Regex, Func<Match, string>>>();
                    t = c == "he" ? BuildHebrew() : BuildMore(c);
                    patterns[c] = building; tables[c] = t;
                    return t;
                }
            }
        }
        static List<Tuple<Regex, Func<Match, string>>> Patterns { get { var t = Table; lock (tableGate) return patterns[Code]; } }

        // whole text, or a known start ("Remove 3 (1.2 GB)..." keeps its numbers)
        public static string T(string s)
        {
            if (!On || string.IsNullOrEmpty(s)) return s;
            string v;
            if (Table.TryGetValue(s, out v)) return v;
            string trimmed = s.Trim();
            if (trimmed != s && Table.TryGetValue(trimmed, out v)) return v;
            foreach (var p in Patterns)
            {
                var m = p.Item1.Match(s);
                if (m.Success) return p.Item2(m);
            }
            return s;
        }

        // emails are written in Hebrew or English only (the other languages cover the window's main screens)
        public static string MailT(string s) { return Hebrew ? T(s) : s; }

        static void Pat(string re, Func<Match, string> f) { building.Add(Tuple.Create(new Regex("^" + re + "$", RegexOptions.Compiled), f)); }

        public static void Apply(Control root)
        {
            if (!On) return;
            if (root is Form) { var f = (Form)root; if (Rtl) { f.RightToLeft = RightToLeft.Yes; f.RightToLeftLayout = true; } f.Text = T(f.Text); }
            Walk(root);
        }

        static void Walk(Control c)
        {
            if (!(c is TextBox) && !(c is ComboBox) && !(c is NumericUpDown) && !(c is Form)) c.Text = T(c.Text);
            if (c is ListView)
            {
                var lv = (ListView)c;
                foreach (ColumnHeader h in lv.Columns) h.Text = T(h.Text);
                foreach (ListViewGroup g in lv.Groups) g.Header = T(g.Header);
                lv.RightToLeftLayout = Rtl;
            }
            else if (c is TabControl) { var tc = (TabControl)c; tc.RightToLeftLayout = Rtl; foreach (TabPage p in tc.TabPages) p.Text = T(p.Text); }
            else if (c is ComboBox) { var cb = (ComboBox)c; for (int i = 0; i < cb.Items.Count; i++) if (cb.Items[i] is string) cb.Items[i] = T((string)cb.Items[i]); }
            if (c.ContextMenuStrip != null) foreach (ToolStripItem it in c.ContextMenuStrip.Items) it.Text = T(it.Text);
            foreach (Control k in c.Controls) Walk(k);
        }

        public static void Menu(ContextMenuStrip m)
        {
            if (!On) return;
            if (Rtl) m.RightToLeft = RightToLeft.Yes;
            foreach (ToolStripItem it in m.Items) it.Text = T(it.Text);
        }

        public static DialogResult Box(IWin32Window owner, string text, string caption, MessageBoxButtons b) { return Box(owner, text, caption, b, MessageBoxIcon.None); }

        // a warning with Yes / No where No is the button Enter presses
        public static DialogResult BoxNo(IWin32Window owner, string text, string caption)
        {
            return NiceBox.Show(owner, T(text), T(caption), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, 1);
        }
        public static DialogResult Box(IWin32Window owner, string text, string caption) { return Box(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }

        // Elixar's own pop-up (in the app's look and direction), with the text translated where known
        public static DialogResult Box(IWin32Window owner, string text, string caption, MessageBoxButtons b, MessageBoxIcon i)
        {
            return NiceBox.Show(owner, On ? T(text) : text, On ? T(caption) : caption, b, i, 0);
        }
    }
}
