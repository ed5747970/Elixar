// Interface language: English (built in) or Hebrew (right to left). Text not in the table stays in English.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static partial class Lang
    {
        public static bool Hebrew;
        public static void Init(string lang) { Hebrew = lang == "he"; }

        static Dictionary<string, string> he;
        static Dictionary<string, string> Table { get { return he ?? (he = BuildHebrew()); } }

        // whole text, or a known start ("Remove 3 (1.2 GB)..." keeps its numbers)
        public static string T(string s)
        {
            if (!Hebrew || string.IsNullOrEmpty(s)) return s;
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

        static readonly List<Tuple<Regex, Func<Match, string>>> Patterns = new List<Tuple<Regex, Func<Match, string>>>();
        static void Pat(string re, Func<Match, string> f) { Patterns.Add(Tuple.Create(new Regex("^" + re + "$", RegexOptions.Compiled), f)); }

        public static void Apply(Control root)
        {
            if (!Hebrew) return;
            if (root is Form) { var f = (Form)root; f.RightToLeft = RightToLeft.Yes; f.RightToLeftLayout = true; f.Text = T(f.Text); }
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
                lv.RightToLeftLayout = true;
            }
            else if (c is TabControl) { var tc = (TabControl)c; tc.RightToLeftLayout = true; foreach (TabPage p in tc.TabPages) p.Text = T(p.Text); }
            else if (c is ComboBox) { var cb = (ComboBox)c; for (int i = 0; i < cb.Items.Count; i++) if (cb.Items[i] is string) cb.Items[i] = T((string)cb.Items[i]); }
            if (c.ContextMenuStrip != null) foreach (ToolStripItem it in c.ContextMenuStrip.Items) it.Text = T(it.Text);
            foreach (Control k in c.Controls) Walk(k);
        }

        public static void Menu(ContextMenuStrip m)
        {
            if (!Hebrew) return;
            m.RightToLeft = RightToLeft.Yes;
            foreach (ToolStripItem it in m.Items) it.Text = T(it.Text);
        }

        public static DialogResult Box(IWin32Window owner, string text, string caption, MessageBoxButtons b) { return Box(owner, text, caption, b, MessageBoxIcon.None); }

        // a warning with Yes / No where No is the button Enter presses
        public static DialogResult BoxNo(IWin32Window owner, string text, string caption)
        {
            var opt = Hebrew ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;
            return MessageBox.Show(owner, T(text), T(caption), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, opt);
        }
        public static DialogResult Box(IWin32Window owner, string text, string caption) { return Box(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }

        // MessageBox with the right direction and the text translated where known
        public static DialogResult Box(IWin32Window owner, string text, string caption, MessageBoxButtons b, MessageBoxIcon i)
        {
            if (!Hebrew) return MessageBox.Show(owner, text, caption, b, i);
            return MessageBox.Show(owner, T(text), T(caption), b, i, MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        }
    }
}
