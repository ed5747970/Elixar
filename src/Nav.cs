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
            public object Tag;
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

        // where every item goes. Too narrow for all of them (a small window, a long language): first Settings, Logs and
        // Policy manager show only their icons, then the tabs get closer together, then the last tabs move into "More"
        // (they used to be drawn on top of each other)
        public readonly Item More = new Item { Text = "More" };
        public readonly List<Item> Overflow = new List<Item>();
        public bool IconsOnly { get; private set; }

        int FullW(Graphics g, Item it, int side) { return W(g, Lang.T(it.Text), Font) + side + (it.Glyph != null ? S(22) : 0); }
        int IconW() { return S(16) + S(24); }

        public void Arrange(Graphics g)
        {
            int pad = S(10), gap = S(4), middle = S(12), room = Width - 2 * pad;
            var left = Items.Where(i => !i.Right).ToList(); var right = Items.Where(i => i.Right).ToList();
            Func<int, bool, int> total = (side, icons) =>
                left.Sum(i => FullW(g, i, side) + gap) + right.Sum(i => (icons ? IconW() : FullW(g, i, S(28))) + gap) + middle;
            int side2 = S(28); IconsOnly = false;
            if (total(side2, false) > room) { IconsOnly = true; if (total(side2, true) > room) side2 = S(14); }
            Overflow.Clear(); More.Rect = Rectangle.Empty;
            int rightW = right.Sum(i => (IconsOnly ? IconW() : FullW(g, i, S(28))) + gap);
            int leftRoom = room - rightW - middle, used = 0;
            int moreW = W(g, Lang.T(More.Text) + "  \u25BE", Font) + side2;
            if (left.Sum(i => FullW(g, i, side2) + gap) > leftRoom)
                foreach (var it in left)
                {
                    int w = FullW(g, it, side2) + gap;
                    if (Overflow.Count > 0 || used + w > leftRoom - moreW - gap) Overflow.Add(it); else used += w;
                }
            int x = pad, xr = Width - pad;
            foreach (var it in Items)
            {
                if (Overflow.Contains(it)) { it.Rect = Rectangle.Empty; continue; }
                int w = it.Right ? (IconsOnly ? IconW() : FullW(g, it, S(28))) : FullW(g, it, side2);
                var r = it.Right ? new Rectangle(xr - w, S(6), w, Height - S(12)) : new Rectangle(x, S(6), w, Height - S(12));
                if (it.Right) xr -= w + gap; else x += w + gap;
                it.Rect = M(r);
            }
            if (Overflow.Count > 0) More.Rect = M(new Rectangle(x, S(6), moreW, Height - S(12)));
        }

        // for the self-test: items drawn on top of each other or past the edge
        public List<string> Problems()
        {
            using (var g = CreateGraphics()) Arrange(g);
            var shown = Items.Where(i => !Overflow.Contains(i)).ToList(); if (Overflow.Count > 0) shown.Add(More);
            var bad = new List<string>();
            foreach (var a in shown)
            {
                if (a.Rect.Left < 0 || a.Rect.Right > Width) bad.Add("the tab \"" + a.Text + "\" goes past the edge of the bar");
                foreach (var b in shown) if (a != b && string.CompareOrdinal(a.Text, b.Text) < 0 && a.Rect.IntersectsWith(b.Rect)) bad.Add("the tabs \"" + a.Text + "\" and \"" + b.Text + "\" are drawn on top of each other");
            }
            return bad;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bar);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Grid)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            Arrange(g);
            var all = Items.Where(i => !Overflow.Contains(i)).ToList(); if (Overflow.Count > 0) all.Add(More);
            foreach (var it in all)
            {
                string text = Lang.T(it.Text) + (it == More ? "  \u25BE" : "");
                bool icon = it.Right && IconsOnly;
                bool sel = it == Selected || it == More && Overflow.Contains(Selected), hov = it == hover && it.Enabled && !sel;
                if (sel || hov)
                    using (var path = Round(it.Rect, S(8)))
                    using (var b = new SolidBrush(sel ? Mix(Bar, Theme.Accent, Theme.Dark ? 0.22 : 0.10) : Mix(Bar, Theme.Ink, 0.05)))
                        g.FillPath(b, path);
                Color ink = !it.Enabled ? Mix(Theme.Quiet, Bar, 0.45) : sel ? Theme.Accent : Theme.Ink;
                var tr = it.Rect;
                if (it.Glyph != null)
                {
                    int gs = S(16);
                    var gr = new Rectangle(icon ? it.Rect.X + (it.Rect.Width - gs) / 2 : Rtl ? it.Rect.Right - S(12) - gs : it.Rect.X + S(12), it.Rect.Y + (it.Rect.Height - gs) / 2, gs, gs);
                    if (it.Glyph == "list") ListGlyph(g, gr, ink, Math.Max(1.5f, S(2) * .8f));
                    else if (it.Glyph == "shield") ShieldGlyph(g, gr, ink, Math.Max(1.5f, S(2) * .8f));
                    else Gear(g, gr, ink, Math.Max(1.5f, S(2) * .8f));
                    tr = Rtl ? new Rectangle(it.Rect.X, it.Rect.Y, it.Rect.Width - S(20), it.Rect.Height) : new Rectangle(it.Rect.X + S(20), it.Rect.Y, it.Rect.Width - S(20), it.Rect.Height);
                }
                if (bold == null || bold.Size != Font.Size) bold = new Font(Font, FontStyle.Bold);
                if (!icon)
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

        // a shield with a tick: the policy
        static void ShieldGlyph(Graphics g, Rectangle r, Color c, float w)
        {
            float x = r.X, y = r.Y, W = r.Width, H = r.Height;
            using (var p = new Pen(c, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var path = new GraphicsPath())
            {
                path.AddLine(x + W * .5f, y + H * .04f, x + W * .9f, y + H * .2f);
                path.AddBezier(x + W * .9f, y + H * .2f, x + W * .9f, y + H * .62f, x + W * .7f, y + H * .82f, x + W * .5f, y + H * .96f);
                path.AddBezier(x + W * .5f, y + H * .96f, x + W * .3f, y + H * .82f, x + W * .1f, y + H * .62f, x + W * .1f, y + H * .2f);
                path.CloseFigure();
                g.DrawPath(p, path);
                g.DrawLines(p, new[] { new PointF(x + W * .32f, y + H * .5f), new PointF(x + W * .46f, y + H * .64f), new PointF(x + W * .7f, y + H * .36f) });
            }
        }

        // a page with lines: the change log
        static void ListGlyph(Graphics g, Rectangle r, Color c, float w)
        {
            using (var p = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float x = r.X + r.Width * .14f, y = r.Y + r.Height * .06f, pw = r.Width * .72f, ph = r.Height * .88f;
                g.DrawRectangle(p, x, y, pw, ph);
                for (int i = 0; i < 3; i++)
                {
                    float ly = y + ph * (.3f + .22f * i);
                    g.DrawLine(p, x + pw * .25f, ly, x + pw * .75f, ly);
                }
            }
        }

        Item Hit(Point p) { if (More.Rect.Contains(p)) return More; return Items.FirstOrDefault(i => !Overflow.Contains(i) && i.Rect.Contains(p)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = Hit(e.Location);
            Cursor = h != null && h.Enabled ? Cursors.Hand : Cursors.Default;
            string t = h != null && !h.Enabled ? Lang.T(h.Tip) : h != null && h.Right && IconsOnly ? Lang.T(h.Text) : "";
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t); }
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            var h = Hit(e.Location);
            if (h == null || e.Button != MouseButtons.Left) return;
            if (h == More) { ShowMore(); return; }
            if (!h.Enabled) { tip.Show(Lang.T(h.Tip), this, e.Location.X, Height, 2500); return; }
            Selected = h; Invalidate();
            if (Picked != null) Picked(h);
        }

        // the tabs that didn't fit, as a menu under "More"
        void ShowMore()
        {
            var menu = new ContextMenuStrip { RightToLeft = RightToLeft };
            foreach (var it in Overflow)
            {
                var x = it;
                var mi = new ToolStripMenuItem(Lang.T(it.Text)) { Enabled = it.Enabled, Checked = it == Selected, ToolTipText = it.Enabled ? "" : Lang.T(it.Tip) };
                mi.Click += (o, e) => { Selected = x; Invalidate(); if (Picked != null) Picked(x); };
                menu.Items.Add(mi);
            }
            menu.Closed += (o, e) => BeginInvoke((Action)(() => menu.Dispose()));
            menu.Show(this, new Point(Rtl ? More.Rect.Right : More.Rect.Left, Height));
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
        public List<string> NavRightItems { get { return nav.Items.Where(i => i.Right).Select(i => i.Text).ToList(); } }
        public string NavRight { get { var r = nav.Items.FirstOrDefault(i => i.Right); return r == null ? "" : r.Text; } }

        // the pages stay in the (hidden-header) tab control, so every "show this tab" keeps working
        void BuildNav()
        {
            nav = new NavBar { Dock = DockStyle.Top, Height = S(46), Scale_ = s, Font = new Font("Segoe UI", 10f) };
            Func<string, TabPage, bool, string, NavBar.Item> item = (t, p, on, why) => new NavBar.Item { Text = t, Page = p, Enabled = on, Tip = why };
            bool lx = LinuxUi;
            nav.Items.Add(item("Dashboard", pageDash, true, ""));
#if BUSINESS
            nav.Items.Add(item("Domain PCs", pageDomain, true, ""));
#endif
            nav.Items.Add(item("Apps", pageApps, !lx, "Only on Windows: on Linux, remove apps with the App Center or apt."));
            nav.Items.Add(item("Cleaner", pageClean, true, ""));
            nav.Items.Add(item("Tools", pageTools, true, ""));
            nav.Items.Add(item("Phone", pagePhone, !lx, "Only on Windows for now."));
            nav.Items.Add(item("Email", pageEmail, true, ""));
            if (OneDriveShown) nav.Items.Add(item("OneDrive", pageOneDrive, true, ""));
            nav.Items.Add(item("Automation", pageAuto, true, ""));
            nav.Items.Add(item("Linux", pageLinux, lx, "Only on Linux: this PC runs " + OsInfo.Current.Name + "."));
            nav.Items.Add(new NavBar.Item { Text = "Settings", Page = pageSettings, Right = true, Glyph = "gear" });
            nav.Items.Add(new NavBar.Item { Text = "Logs", Page = pageLogs, Right = true, Glyph = "list" });
            nav.Items.Add(new NavBar.Item { Text = "Policy manager", Page = pagePolicy, Right = true, Glyph = "shield" });
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
