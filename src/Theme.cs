// Settings (language, theme) and dark mode.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
            // coloured text: lift dark shades so they stay readable on a dark background
            return Lum(c) < 0.45f ? ControlPaint.Light(c, 0.6f) : c;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

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
                if (Win.IsWindows) try { SetWindowTheme(lv.Handle, "DarkMode_Explorer", null); } catch { }
                FixItems(lv);
            }
            else if (c is TextBox || c is ComboBox || c is NumericUpDown || c is ListBox) { c.BackColor = Panel; c.ForeColor = Ink; }
            else if (c is Button)
            {
                var b = (Button)c;
                b.FlatStyle = FlatStyle.Flat; b.BackColor = ButtonBack; b.ForeColor = Ink;
                b.FlatAppearance.BorderColor = Color.FromArgb(96, 96, 114);
            }
            else if (c is LinkLabel) { var l = (LinkLabel)c; l.LinkColor = Accent; l.ActiveLinkColor = Color.White; }
            else if (c is TabControl) OwnerDrawTabs((TabControl)c);
            else if (c is TabPage) { c.BackColor = Back; }
            foreach (Control k in c.Controls) Walk(k);
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

    public class SettingsForm : Form
    {
        public bool NeedsRestart;
        public Action CheckUpdates;
        public SettingsForm(float scale)
        {
            Func<int, int> S = px => (int)Math.Round(px * scale);
            Text = Lang.T("Settings"); Icon = Brand.Load(32); StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false; MaximizeBox = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new Padding(S(14)); Font = new Font("Segoe UI", 9.5f);
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
            var lang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(220) };
            lang.Items.AddRange(new object[] { "English", "עברית (Hebrew)" });
            lang.SelectedIndex = Settings.Language == "he" ? 1 : 0;
            var theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(220) };
            theme.Items.AddRange(new object[] { Lang.T("Follow Windows"), Lang.T("Light"), Lang.T("Dark") });
            theme.SelectedIndex = Settings.ThemeMode == "light" ? 1 : Settings.ThemeMode == "dark" ? 2 : 0;
            t.Controls.Add(new Label { AutoSize = true, Text = Lang.T("Language"), Margin = new Padding(0, S(7), S(12), S(6)) }, 0, 0); t.Controls.Add(lang, 1, 0);
            t.Controls.Add(new Label { AutoSize = true, Text = Lang.T("Theme"), Margin = new Padding(0, S(7), S(12), S(6)) }, 0, 1); t.Controls.Add(theme, 1, 1);
            var btnInstall = new Button { Text = Lang.T(Setup.IsRegistered() ? "Repair the installation" : "Install Elixar on this PC"), AutoSize = true };
            var btnUpdate = new Button { Text = Lang.T("Check for updates"), AutoSize = true };
            var info = new Label { AutoSize = true, MaximumSize = new Size(S(420), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, S(6), 0, 0),
                                   Text = Lang.T("Installing adds Elixar to the Start menu and to Windows' installed apps list (with Uninstall), in a folder only administrators can change.") };
            t.Controls.Add(new Label { AutoSize = true, Text = Lang.T("This PC"), Margin = new Padding(0, S(12), S(12), 0) }, 0, 2);
            var tools = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(6), 0, 0) }; tools.Controls.Add(btnInstall); tools.Controls.Add(btnUpdate);
            t.Controls.Add(tools, 1, 2); t.Controls.Add(info, 1, 3);
            btnInstall.Click += (o, e) =>
            {
                string err = Setup.InstallHere(null);
                Lang.Box(this, err == null ? "Elixar is installed: find it in the Start menu. It also appears in Windows' installed apps list." : "Could not install Elixar: " + err, "Elixar",
                    MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            };
            btnUpdate.Click += (o, e) => { if (CheckUpdates != null) CheckUpdates(); };
            var ok = new Button { Text = Lang.T("Save"), AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = Lang.T("Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(12), 0, 0) }; row.Controls.Add(ok); row.Controls.Add(cancel);
            t.Controls.Add(row, 1, 4);
            Controls.Add(t); AcceptButton = ok; CancelButton = cancel;
            ok.Click += (o, e) =>
            {
                string l = lang.SelectedIndex == 1 ? "he" : "en", th = theme.SelectedIndex == 1 ? "light" : theme.SelectedIndex == 2 ? "dark" : "auto";
                NeedsRestart = l != Settings.Language || th != Settings.ThemeMode;
                Settings.Language = l; Settings.ThemeMode = th;
            };
            Shown += (o, e) => { Theme.Apply(this); Lang.Apply(this); };
        }
    }
}
