// Two things every Elixar window needs on Windows:
//   1. it fits the screen it is on: never wider or taller than the screen (minus the taskbar), title bar on screen -
//      otherwise the scroll bar of a long page (Settings) ends up off-screen and the bottom can't be reached
//   2. the mouse wheel scrolls the page under the mouse pointer, not only the control that has the keyboard focus
//      (after clicking a tab the focus is on the tab bar, so the wheel did nothing on the page)

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class WindowFit
    {
        // the window's bounds moved and shrunk to fit inside the work area (the screen without the taskbar)
        public static Rectangle Fit(Rectangle window, Rectangle work)
        {
            int w = Math.Min(window.Width, work.Width), h = Math.Min(window.Height, work.Height);
            int x = Math.Max(work.Left, Math.Min(window.Left, work.Right - w));
            int y = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        public static void Keep(Form f)
        {
            if (f.WindowState != FormWindowState.Normal) return;
            var work = Screen.FromControl(f).WorkingArea;
            // a window that sizes itself to what it shows: taller (or wider) than the screen, it gets the screen's size and scrolls
            if (f.AutoSize && (f.Height > work.Height || f.Width > work.Width)) { f.AutoSize = false; f.AutoScroll = true; }
            if (f.MinimumSize.Width > work.Width || f.MinimumSize.Height > work.Height)
                f.MinimumSize = new Size(Math.Min(f.MinimumSize.Width, work.Width), Math.Min(f.MinimumSize.Height, work.Height));
            var b = Fit(f.Bounds, work);
            if (b != f.Bounds) f.Bounds = b;
        }

        // fits now, and again after the window is resized, moved to another screen, or Windows changes its size
        public static void Watch(Form f)
        {
            f.Shown += (o, e) => Keep(f);
            f.ResizeEnd += (o, e) => Keep(f);
            f.Resize += (o, e) => { if (f.WindowState == FormWindowState.Normal && !resizing) { resizing = true; try { Keep(f); } finally { resizing = false; } } };
            WheelRouter.Install();
        }
        [ThreadStatic] static bool resizing;
    }

    // WM_MOUSEWHEEL goes to the scrolling page under the pointer. Controls that scroll themselves (lists, text boxes,
    // drop-downs) keep the wheel; anything else passes it to the nearest page that can scroll.
    public class WheelRouter : IMessageFilter
    {
        const int WM_MOUSEWHEEL = 0x020A;
        static bool installed;
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public static void Install()
        {
            if (installed || !Win.IsWindows) return;
            installed = true;
            Application.AddMessageFilter(new WheelRouter());
        }

        // the page that should scroll for a wheel turn over this control, or null when the control scrolls itself
        public static ScrollableControl Target(Control under)
        {
            for (var c = under; c != null; c = c.Parent)
            {
                if (c is ListView || c is TreeView || c is TextBoxBase || c is ListBox || c is ComboBox || c is DataGridView || c is NumericUpDown || c is TrackBar || c is WebBrowser) return null;
                var sc = c as ScrollableControl;
                if (sc != null && sc.AutoScroll && sc.VerticalScroll.Visible) return sc;
            }
            return null;
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;
            try
            {
                var under = Control.FromChildHandle(WindowFromPoint(Cursor.Position));
                var to = Target(under);
                if (to == null || to.Handle == m.HWnd) return false;   // nothing to route, or it's already going there
                SendMessage(to.Handle, m.Msg, m.WParam, m.LParam);
                return true;
            }
            catch { return false; }
        }
    }
}

namespace CDriveCleaner
{
    // Long texts wrap to the width that is really visible: a label's MaximumSize (or no limit) can be wider than a narrow
    // window, and then the end of the text was cut off. Runs after the window or a page changes size.
    public static class TextFit
    {
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Label, object> orig = new System.Runtime.CompilerServices.ConditionalWeakTable<Label, object>();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Label, object> skip = new System.Runtime.CompilerServices.ConditionalWeakTable<Label, object>();

        // a label whose width its page sets itself (in a table column); TextFit leaves it alone
        public static void Skip(Label l) { skip.GetValue(l, x => (object)true); }

        public static void Apply(Control root)
        {
            if (root == null || !root.IsHandleCreated || !root.Visible) return;
            foreach (Control c in root.Controls) Walk(c);
        }

        static void Walk(Control c)
        {
            if (!c.Visible) return;
            var l = c as Label;
            object no;
            if (l != null && l.AutoSize && !(l is LinkLabel && l.Text.Length < 40) && !skip.TryGetValue(l, out no)) Fit(l);
            foreach (Control k in c.Controls) Walk(k);
        }

        static Control Viewport(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
                if (p is TabPage || p is Form || (p is ScrollableControl && ((ScrollableControl)p).AutoScroll)) return p;
            return null;
        }

        static void Fit(Label l)
        {
            var v = Viewport(l); if (v == null || l.Parent == null) return;
            object o = orig.GetValue(l, x => (object)x.MaximumSize.Width);
            int max = (int)o;
            int left; try { left = v.PointToClient(l.Parent.PointToScreen(l.Location)).X; } catch { return; }
            int avail = v.ClientSize.Width - left - l.Margin.Right - SystemInformation.VerticalScrollBarWidth - 4;
            if (avail < 120) return;
            int want = max == 0 ? (l.PreferredWidth > avail ? avail : 0) : Math.Min(max, avail);
            if (l.MaximumSize.Width == want) return;
            l.MaximumSize = new Size(want, 0);
            // the boxes around it grow or shrink with the new number of lines (a label under it could end up outside)
            for (var p = l.Parent; p != null && p != v; p = p.Parent) p.PerformLayout();
            v.PerformLayout();
        }
    }
}
