// Saved settings (language, theme) and dark mode. The Settings tab is in SettingsTab.cs.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public static class Settings
    {
        const string Key = @"Software\Elixar";
        public static string Get(string name, string def)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) { var v = k == null ? null : k.GetValue(name) as string; return string.IsNullOrEmpty(v) ? def : v; } }
            catch { return def; }
        }
        public static void Set(string name, string value)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, value, RegistryValueKind.String); } catch { }
        }
        public static string ThemeMode { get { return Get("Theme", "auto"); } set { Set("Theme", value); } }        // auto, light, dark
        public static string Language { get { return Get("Language", "en"); } set { Set("Language", value); } }     // en, he
    }

    public static class Theme
    {
        public static bool Dark;

        public static bool WindowsUsesDark()
        {
            if (Lx.On) return Lx.PrefersDark();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && k.GetValue("AppsUseLightTheme") is int && (int)k.GetValue("AppsUseLightTheme") == 0;
            }
            catch { return false; }
        }

        public static void Init(string mode)
        {
            Dark = mode == "dark" || (mode == "auto" && WindowsUsesDark());
        }

        public static Color Back { get { return Dark ? Color.FromArgb(30, 30, 36) : Color.FromArgb(246, 248, 251); } }
        public static Color Panel { get { return Dark ? Color.FromArgb(42, 42, 50) : Color.White; } }
        public static Color Ink { get { return Dark ? Color.FromArgb(232, 232, 240) : Color.FromArgb(24, 32, 43); } }
        public static Color Quiet { get { return Dark ? Color.FromArgb(165, 165, 182) : Color.DimGray; } }
        public static Color Grid { get { return Dark ? Color.FromArgb(70, 70, 86) : Color.FromArgb(222, 226, 232); } }
        public static Color Accent { get { return Dark ? Color.FromArgb(157, 144, 255) : Color.FromArgb(91, 76, 196); } }
        public static Color Danger { get { return Dark ? Color.FromArgb(255, 123, 114) : Color.FromArgb(199, 54, 47); } }
        static Color ButtonBack { get { return Color.FromArgb(56, 56, 68); } }

        static float Lum(Color c) { return (0.299f * c.R + 0.587f * c.G + 0.114f * c.B) / 255f; }
        static bool Grayish(Color c) { return Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 30; }

        // a light colour picked for the light theme -> its dark-theme counterpart
        public static Color MapBack(Color c)
        {
            if (!Dark || c.A == 0 || !Grayish(c)) return c;
            float l = Lum(c);
            return l > 0.99f ? Panel : l > 0.8f ? Back : c;
        }
        public static Color MapFore(Color c)
        {
            if (!Dark) return c;
            if (Grayish(c)) { float l = Lum(c); return l < 0.3f ? Ink : l < 0.7f ? Quiet : c; }
            // coloured text: lift dark shades towards white so they stay readable on a dark background
            float lum = Lum(c);
            return lum < 0.45f ? Blend(c, Color.White, 0.55f) : lum < 0.6f ? Blend(c, Color.White, 0.3f) : c;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        static Color HeaderBack { get { return Color.FromArgb(48, 48, 58); } }
        static Color DisabledBack { get { return Color.FromArgb(40, 40, 48); } }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);

        public static void Apply(Control root)
        {
            if (!Dark) return;
            if (root is Form)
            {
                var f = (Form)root;
                if (Win.IsWindows) { try { int on = 1; if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4); } catch { } }
                f.BackColor = Back; f.ForeColor = Ink;
            }
            Walk(root);
        }

        static void Walk(Control c)
        {
            if (c is BrandBar) return;
            c.BackColor = MapBack(c.BackColor);
            c.ForeColor = MapFore(c.ForeColor);
            if (c is ListView)
            {
                var lv = (ListView)c;
                lv.BackColor = Panel; lv.ForeColor = Ink;
                if (lv.BorderStyle == BorderStyle.Fixed3D) lv.BorderStyle = BorderStyle.FixedSingle;
                if (Win.IsWindows) try { SetWindowTheme(lv.Handle, "DarkMode_Explorer", null); } catch { }
                DarkHeaders(lv);
                FixItems(lv);
            }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.BackColor = Panel; cb.ForeColor = Ink;
                // Windows 10/11 draw a dark drop-down with their own dark theme; elsewhere a flat one takes the colours
                if (Win.IsWindows) try { SetWindowTheme(cb.Handle, "DarkMode_CFD", null); } catch { cb.FlatStyle = FlatStyle.Flat; }
                else cb.FlatStyle = FlatStyle.Flat;
            }
            else if (c is TextBox || c is NumericUpDown || c is ListBox)
            {
                c.BackColor = Panel; c.ForeColor = Ink;
                if (Win.IsWindows && (c is ListBox || (c is TextBox && ((TextBox)c).Multiline))) try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
            }
            else if (c is Button)
            {
                var b = (Button)c;
                b.FlatStyle = FlatStyle.Flat; b.BackColor = ButtonBack; b.ForeColor = Ink;
                b.FlatAppearance.BorderColor = Color.FromArgb(96, 96, 114);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(68, 66, 86);
                DarkDisabled(b);
            }
            else if (c is LinkLabel) { var l = (LinkLabel)c; l.LinkColor = Accent; l.ActiveLinkColor = Color.White; }
            else if (c is TabControl) DarkTabs((TabControl)c);
            else if (c is TabPage) { c.BackColor = Back; }
            else if (c is GroupBox) DarkGroup((GroupBox)c);
            else if (c is ScrollableControl && ((ScrollableControl)c).AutoScroll && Win.IsWindows) { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }
            foreach (Control k in c.Controls) Walk(k);
        }

        // Windows draws some parts of its controls itself, in light colours whatever the theme: the frame around tab pages
        // and their tab strip, group box borders, list headers and disabled buttons. In the dark theme Elixar paints those.
        static readonly HashSet<Control> painted = new HashSet<Control>();
        static readonly MethodInfo SetStyleM = typeof(Control).GetMethod("SetStyle", BindingFlags.NonPublic | BindingFlags.Instance);
        static void SelfPainted(Control c)
        {
            try { SetStyleM.Invoke(c, new object[] { ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true }); } catch { }
        }
        public static bool IsSelfPainted(Control c) { return painted.Contains(c); }   // self-test

        static void DarkTabs(TabControl tc)
        {
            if (!painted.Add(tc)) return;
            SelfPainted(tc);
            tc.Paint += (o, e) =>
            {
                var g = e.Graphics;
                using (var b = new SolidBrush(Back)) g.FillRectangle(b, tc.ClientRectangle);
                bool strip = false;
                for (int i = 0; i < tc.TabCount; i++)
                {
                    Rectangle r;
                    try { r = tc.GetTabRect(i); } catch { continue; }
                    if (r.Height <= 6 || r.Width <= 0) continue;
                    strip = true;
                    bool sel = i == tc.SelectedIndex;
                    using (var b = new SolidBrush(sel ? Panel : Back)) g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, tc.TabPages[i].Text, tc.Font, r, sel ? Ink : Quiet,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    if (sel) using (var p = new Pen(Accent, 2)) g.DrawLine(p, r.Left + 4, r.Bottom - 2, r.Right - 4, r.Bottom - 2);
                }
                if (strip) { var d = tc.DisplayRectangle; using (var p = new Pen(Grid)) g.DrawLine(p, 0, d.Top - 2, tc.Width, d.Top - 2); }
            };
            tc.SelectedIndexChanged += (o, e) => tc.Invalidate();
        }

        static void DarkGroup(GroupBox gb)
        {
            if (!painted.Add(gb)) return;
            SelfPainted(gb);
            gb.Paint += (o, e) =>
            {
                var g = e.Graphics;
                using (var b = new SolidBrush(gb.BackColor)) g.FillRectangle(b, gb.ClientRectangle);
                var ts = TextRenderer.MeasureText(g, gb.Text, gb.Font);
                int y = ts.Height / 2;
                using (var p = new Pen(Grid)) g.DrawRectangle(p, 0, y, gb.Width - 1, gb.Height - y - 1);
                if (gb.Text.Length > 0)
                {
                    bool rtl = gb.RightToLeft == RightToLeft.Yes;
                    var tr = new Rectangle(rtl ? gb.Width - ts.Width - 10 : 8, 0, ts.Width + 2, ts.Height);
                    using (var b = new SolidBrush(gb.BackColor)) g.FillRectangle(b, tr);
                    TextRenderer.DrawText(g, gb.Text, gb.Font, tr, Ink, TextFormatFlags.NoPrefix | TextFormatFlags.Left);
                }
            };
        }

        static void DarkHeaders(ListView lv)
        {
            if (!painted.Add(lv) || lv.View != View.Details) return;
            if (Win.IsWindows)
                try { IntPtr h = SendMessage(lv.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero); if (h != IntPtr.Zero) SetWindowTheme(h, "DarkMode_ItemsView", null); } catch { }
            lv.OwnerDraw = true;
            lv.DrawItem += (o, e) =>
            {
                // items added after the theme was applied: their colours are made readable on the dark background here
                var it = e.Item;
                var f = MapFore(it.ForeColor == Color.Empty ? Ink : it.ForeColor); if (it.ForeColor != f) it.ForeColor = f;
                foreach (ListViewItem.ListViewSubItem si in it.SubItems) { var sf = MapFore(si.ForeColor); if (si.ForeColor != sf) si.ForeColor = sf; }
                e.DrawDefault = true;
            };
            lv.DrawSubItem += (o, e) => e.DrawDefault = true;
            lv.DrawColumnHeader += (o, e) =>
            {
                using (var b = new SolidBrush(HeaderBack)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Grid))
                {
                    e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
                var r = Rectangle.Inflate(e.Bounds, -6, 0);
                var align = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : e.Header.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, r, Ink, align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }

        static void DarkDisabled(Button b)
        {
            if (!painted.Add(b)) return;
            b.Paint += (o, e) =>
            {
                if (b.Enabled) return;
                var g = e.Graphics;
                using (var br = new SolidBrush(DisabledBack)) g.FillRectangle(br, b.ClientRectangle);
                using (var p = new Pen(Grid)) g.DrawRectangle(p, 0, 0, b.Width - 1, b.Height - 1);
                TextRenderer.DrawText(g, b.Text, b.Font, b.ClientRectangle, Color.FromArgb(118, 118, 134),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
            };
        }

        // items added later keep their light-theme colours: remap them when a list changes
        static readonly Dictionary<ListView, int> seen = new Dictionary<ListView, int>();
        public static void FixItems(ListView lv)
        {
            if (!Dark) return;
            foreach (ListViewItem it in lv.Items)
            {
                it.ForeColor = MapFore(it.ForeColor == Color.Empty ? Ink : it.ForeColor);
                if (it.BackColor != Color.Empty) it.BackColor = MapBack(it.BackColor);
                foreach (ListViewItem.ListViewSubItem s in it.SubItems) s.ForeColor = MapFore(s.ForeColor);
            }
            seen[lv] = lv.Items.Count;
        }

        // checks open lists now and then (cheap: only when their item count changed)
        public static void Watch(Form f)
        {
            if (!Dark) return;
            var lists = new List<ListView>();
            Action<Control> find = null;
            find = c => { if (c is ListView) lists.Add((ListView)c); foreach (Control k in c.Controls) find(k); };
            find(f);
            var t = new Timer { Interval = 700 };
            t.Tick += (o, e) =>
            {
                foreach (var lv in lists)
                {
                    int n; if (!lv.IsDisposed && (!seen.TryGetValue(lv, out n) || n != lv.Items.Count)) { lv.BeginUpdate(); FixItems(lv); lv.EndUpdate(); }
                }
            };
            t.Start();
            f.FormClosed += (o, e) => t.Dispose();
        }

        static void OwnerDrawTabs(TabControl tc)
        {
            if (tc.DrawMode == TabDrawMode.OwnerDrawFixed) return;
            tc.DrawMode = TabDrawMode.OwnerDrawFixed;
            tc.DrawItem += (o, e) =>
            {
                var page = tc.TabPages[e.Index];
                bool sel = e.Index == tc.SelectedIndex;
                using (var b = new SolidBrush(sel ? Panel : Back)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, page.Text, tc.Font, e.Bounds, sel ? Ink : Quiet, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (sel) using (var p = new Pen(Accent, 2)) e.Graphics.DrawLine(p, e.Bounds.Left + 4, e.Bounds.Bottom - 2, e.Bounds.Right - 4, e.Bounds.Bottom - 2);
            };
        }
    }
}
