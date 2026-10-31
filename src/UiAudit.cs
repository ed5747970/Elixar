// Finds the layout mistakes that make part of a window unusable (self-test and previews):
//   - a control that sticks out of a container that can't scroll (so it can't be seen or reached, like the bottom of
//     Settings when nothing scrolled)
//   - a button whose text doesn't fit on it
// Only reads the layout; changes nothing.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class UiAudit
    {
        static bool ScrollsItself(Control c) { return c is ListView || c is TreeView || c is TextBoxBase || c is ListBox || c is ComboBox || c is DataGridView || c is WebBrowser || c is NumericUpDown; }

        // every problem in the window as it is laid out now (where = the window's title, for the report)
        public static List<string> Find(Form f, string where)
        {
            var found = new List<string>();
            f.PerformLayout();
            Walk(f, f, where ?? f.Text, found);
            return found.Distinct().ToList();
        }

        static void Walk(Control c, Form f, string where, List<string> found)
        {
            foreach (Control k in c.Controls)
            {
                if (!k.Visible || k.Width <= 0 || k.Height <= 0) continue;
                // a box that only holds other controls is judged by what it holds (its own size may be bigger than what shows)
                bool holder = k.Controls.Count > 0 && (k is Panel || k is TableLayoutPanel || k is FlowLayoutPanel || k is TabControl || k is TabPage || k is SplitContainer || k is UserControl || k is GroupBox && false);
                string why = holder ? null : Unreachable(k);
                if (why != null) { found.Add(where + ": " + Name(k) + " " + why); continue; }
                var b = k as ButtonBase;
                if (b != null && !b.AutoSize && b.Text.Length > 0 && !(b is CheckBox) && !(b is RadioButton))
                {
                    var need = TextRenderer.MeasureText(b.Text, b.Font);
                    if (need.Width > b.Width - 4 && b.Width > 0) found.Add(where + ": button \"" + Short(b.Text) + "\" is too narrow for its text (" + b.Width + " < " + need.Width + ")");
                }
                if (!ScrollsItself(k)) Walk(k, f, where, found);   // a list or text box scrolls its own lines
            }
        }

        // null when the control can be seen whole, or reached by scrolling; otherwise which container cuts it off
        public static string Unreachable(Control k)
        {
            var r = k.Bounds;   // in the parent's client coordinates (scrolling already counted in)
            for (var p = k.Parent; p != null; p = p.Parent)
            {
                var sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll) { r = p.Bounds; if (p.Parent == null) return null; continue; }   // scrolls: anything inside can be reached
                // a tab control: only its page area counts (Elixar hides the tab headers above the panel on purpose)
                if (p is TabControl) { if (p.Parent == null) return null; r = new Rectangle(r.X + p.Left, r.Y + p.Top, r.Width, r.Height); continue; }
                var client = p.ClientRectangle;
                const int slack = 3;
                if (r.Right > client.Right + slack || r.Bottom > client.Bottom + slack || r.Left < client.Left - slack || r.Top < client.Top - slack)
                {
                    int outBy = Math.Max(Math.Max(r.Right - client.Right, r.Bottom - client.Bottom), Math.Max(client.Left - r.Left, client.Top - r.Top));
                    if (r.IntersectsWith(client) || outBy > 0)
                        return "is cut off by " + Name(p) + " (" + outBy + " px outside, and it can't scroll) [" + r + " in " + client + "]";
                }
                if (p is Form) return null;
                var pc = p.Parent == null ? Point.Empty : p.Location;
                r = new Rectangle(r.X + pc.X, r.Y + pc.Y, r.Width, r.Height);
                r.Intersect(p.Bounds);   // only the part the parent shows travels up
                if (r.Width <= 0 || r.Height <= 0) return null;
            }
            return null;
        }

        static string Short(string s) { s = (s ?? "").Replace("\n", " ").Replace("&", ""); return s.Length > 40 ? s.Substring(0, 40) + "..." : s; }
        static string Name(Control c)
        {
            string t = c is Form ? "the window" : c is TabPage ? "the page \"" + Short(c.Text) + "\"" : (c.Text ?? "").Length > 0 && !(c is Panel) ? c.GetType().Name + " \"" + Short(c.Text) + "\"" : c.GetType().Name;
            return t;
        }
    }
}

namespace CDriveCleaner
{
    public partial class MainForm
    {
        // every tab and every Tools page, one after the other, as they look in this window's size now
        // long texts wrap to the width that shows (after a resize and when another page or Tools page shows)
        bool refitQueued;
        public void RefitText()
        {
            if (refitQueued || !IsHandleCreated) return;
            refitQueued = true;
            BeginInvoke((Action)(() => { refitQueued = false; try { TextFit.Apply(tabs.SelectedTab == pageTools ? (Control)toolsTabs.SelectedTab : tabs.SelectedTab); } catch { } }));
        }

        public List<string> AuditLayout()
        {
            var found = new List<string>();
            var keepTab = tabs.SelectedTab; var keepTool = toolsTabs.SelectedTab;
            foreach (var x in nav.Problems()) found.Add(Size.Width + "x" + Size.Height + " top bar: " + x);
            foreach (TabPage p in tabs.TabPages)
            {
                if (!p.Enabled) continue;
                tabs.SelectedTab = p; Application.DoEvents(); TextFit.Apply(p); Application.DoEvents();
                if (p == pageTools)
                    foreach (TabPage tp in toolsTabs.TabPages) { toolsTabs.SelectedTab = tp; Application.DoEvents(); TextFit.Apply(tp); Application.DoEvents(); found.AddRange(UiAudit.Find(this, Size.Width + "x" + Size.Height + " " + p.Text + " > " + tp.Text)); }
                else found.AddRange(UiAudit.Find(this, Size.Width + "x" + Size.Height + " " + p.Text));
            }
            tabs.SelectedTab = keepTab; toolsTabs.SelectedTab = keepTool;
            return found.Distinct().ToList();
        }
    }
}
