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
