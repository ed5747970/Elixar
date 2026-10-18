// The tab bar: the main tabs on the left, Settings on the right. A tab that belongs to the other system
// (Linux on Windows; Apps and Phone on Linux) stays visible but greyed, with a note saying where it works.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class NavBar : PaintedCard
    {
        public class Item
        {
            public string Text = "", Glyph, Tip = "";
            public TabPage Page;
            public bool Enabled = true, Right;
            public Rectangle Rect;
        }
        public List<Item> Items = new List<Item>();
        public Item Selected;
        public event Action<Item> Picked;
        Item hover;
        Font bold;
        readonly ToolTip tip = new ToolTip();
        string lastTip = "";

        public NavBar() { TabStop = false; }

        static Color Bar { get { return Theme.Dark ? Color.FromArgb(36, 36, 44) : Color.White; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Grid)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            int pad = S(10), gap = S(4), x = pad, xr = Width - pad;
            foreach (var it in Items)
            {
                string text = Lang.T(it.Text);
                int w = W(g, text, Font) + S(28) + (it.Glyph != null ? S(22) : 0);
                var r = it.Right ? new Rectangle(xr - w, S(6), w, Height - S(12)) : new Rectangle(x, S(6), w, Height - S(12));
                if (it.Right) xr -= w + gap; else x += w + gap;
                it.Rect = M(r);
                bool sel = it == Selected, hov = it == hover && it.Enabled && !sel;
                if (sel || hov)
                    using (var path = Round(it.Rect, S(8)))
                    using (var b = new SolidBrush(sel ? Mix(Bar, Theme.Accent, Theme.Dark ? 0.22 : 0.10) : Mix(Bar, Theme.Ink, 0.05)))
                        g.FillPath(b, path);
                Color ink = !it.Enabled ? Mix(Theme.Quiet, Bar, 0.45) : sel ? Theme.Accent : Theme.Ink;
                var tr = it.Rect;
                if (it.Glyph != null)
                {
                    int gs = S(16);
                    var gr = M(new Rectangle(r.X + S(12), r.Y + (r.Height - gs) / 2, gs, gs));
                    Gear(g, gr, ink, Math.Max(1.5f, S(2) * .8f));
                    tr = M(new Rectangle(r.X + S(20), r.Y, r.Width - S(20), r.Height));
                }
                if (bold == null || bold.Size != Font.Size) bold = new Font(Font, FontStyle.Bold);
                TextRenderer.DrawText(g, text, sel ? bold : Font, tr, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | (Rtl ? TextFormatFlags.RightToLeft : 0));
                if (sel)
                    using (var b = new SolidBrush(Theme.Accent))
                    using (var path = Round(new RectangleF(it.Rect.X + S(12), Height - S(4), it.Rect.Width - S(24), S(3)), S(1)))
                        g.FillPath(b, path);
            }
        }

        static void Gear(Graphics g, Rectangle r, Color c, float w)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, R = r.Width * .42f, ri = r.Width * .2f;
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI / 4 * i;
                    g.DrawLine(p, cx + (float)Math.Cos(a) * R * .72f, cy + (float)Math.Sin(a) * R * .72f, cx + (float)Math.Cos(a) * R, cy + (float)Math.Sin(a) * R);
                }
                g.DrawEllipse(p, cx - R * .7f, cy - R * .7f, R * 1.4f, R * 1.4f);
                g.DrawEllipse(p, cx - ri, cy - ri, ri * 2, ri * 2);
            }
        }

        Item Hit(Point p) { return Items.FirstOrDefault(i => i.Rect.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = Hit(e.Location);
            Cursor = h != null && h.Enabled ? Cursors.Hand : Cursors.Default;
            string t = h != null && !h.Enabled ? Lang.T(h.Tip) : "";
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t); }
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var h = Hit(e.Location);
            if (h == null || e.Button != MouseButtons.Left) return;
            if (!h.Enabled) { tip.Show(Lang.T(h.Tip), this, e.Location.X, Height, 2500); return; }
            Selected = h; Invalidate();
            if (Picked != null) Picked(h);
        }

        public void Select(TabPage p)
        {
            var it = Items.FirstOrDefault(i => i.Page == p);
            if (it != null && it != Selected) { Selected = it; Invalidate(); }
        }
    }

    public partial class MainForm
    {
        NavBar nav;
        Panel tabHost;
        public List<string> NavEnabled { get { return nav.Items.Where(i => i.Enabled).Select(i => i.Text).ToList(); } }
        public List<string> NavGreyed { get { return nav.Items.Where(i => !i.Enabled).Select(i => i.Text).ToList(); } }
        public string NavRight { get { var r = nav.Items.FirstOrDefault(i => i.Right); return r == null ? "" : r.Text; } }

        // the pages stay in the (hidden-header) tab control, so every "show this tab" keeps working
        void BuildNav()
        {
            nav = new NavBar { Dock = DockStyle.Top, Height = S(46), Scale_ = s, Font = new Font("Segoe UI", 10f) };
            Func<string, TabPage, bool, string, NavBar.Item> item = (t, p, on, why) => new NavBar.Item { Text = t, Page = p, Enabled = on, Tip = why };
            bool lx = LinuxUi;
            nav.Items.Add(item("Dashboard", pageDash, true, ""));
            nav.Items.Add(item("Apps", pageApps, !lx, "Only on Windows: on Linux, remove apps with the App Center or apt."));
            nav.Items.Add(item("Cleaner", pageClean, true, ""));
            nav.Items.Add(item("Tools", pageTools, true, ""));
            nav.Items.Add(item("Phone", pagePhone, !lx, "Only on Windows for now."));
            nav.Items.Add(item("Email", pageEmail, true, ""));
            nav.Items.Add(item("Automation", pageAuto, true, ""));
            nav.Items.Add(item("Linux", pageLinux, lx, "Only on Linux: this PC runs " + OsInfo.Current.Name + "."));
            nav.Items.Add(new NavBar.Item { Text = "Settings", Page = pageSettings, Right = true, Glyph = "gear" });
            nav.Selected = nav.Items[0];
            nav.Picked += it => tabs.SelectedTab = it.Page;
            tabs.SelectedIndexChanged += (o, e) => nav.Select(tabs.SelectedTab);

            tabHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            tabs.Dock = DockStyle.None;
            tabHost.Controls.Add(tabs);
            EventHandler fit = (o, e) =>
            {
                int top = Math.Max(0, tabs.DisplayRectangle.Top);
                tabs.SetBounds(-2, -top, tabHost.ClientSize.Width + 4, tabHost.ClientSize.Height + top + 2);
            };
            tabHost.Resize += fit;
            tabs.HandleCreated += fit;
        }
    }
}
