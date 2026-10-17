// Settings tab: language (English or Hebrew), theme (follow Windows, light, dark), install and updates.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pageSettings;
        RadioButton rbEnglish, rbHebrew, rbThemeAuto, rbThemeLight, rbThemeDark;
        Button btnApplySettings;
        Panel themePreview;

        string ChosenLanguage { get { return rbHebrew.Checked ? "he" : "en"; } }
        string ChosenTheme { get { return rbThemeDark.Checked ? "dark" : rbThemeLight.Checked ? "light" : "auto"; } }

        // for the self-test: the tab shows the saved choices
        public bool SettingsTabReady
        {
            get
            {
                return pageSettings != null && tabs.TabPages.Contains(pageSettings) && ChosenLanguage == Settings.Language &&
                       ChosenTheme == (Settings.ThemeMode == "dark" || Settings.ThemeMode == "light" ? Settings.ThemeMode : "auto") && !btnApplySettings.Enabled;
            }
        }

        public void ShowSettings() { tabs.SelectedTab = pageSettings; }

        void BuildSettingsTab()
        {
            pageSettings = new TabPage("Settings") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(12);
            for (int i = 0; i < 11; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.AutoScroll = true;
            Func<string, Label> head = text => new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), Text = text, Margin = new Padding(0, S(10), 0, S(4)) };
            Func<string, RadioButton> radio = text => new RadioButton { Text = text, AutoSize = true, Margin = new Padding(S(4), S(4), S(22), S(4)) };

            // language: each name is written in its own language, so it can be found whichever one is showing
            var langRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            rbEnglish = radio("English"); rbHebrew = radio("עברית (Hebrew)");
            langRow.Controls.Add(rbEnglish); langRow.Controls.Add(rbHebrew);

            // theme, with a small preview of the chosen colours
            var themeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            rbThemeAuto = radio("Follow Windows"); rbThemeLight = radio("Light"); rbThemeDark = radio("Dark");
            themePreview = new Panel { Size = new Size(S(220), S(120)), Margin = new Padding(S(10), 0, 0, 0) };
            themePreview.Paint += (o, e) => PaintThemePreview(e.Graphics, themePreview.ClientRectangle);
            var themeRadios = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            themeRadios.Controls.Add(rbThemeAuto); themeRadios.Controls.Add(rbThemeLight); themeRadios.Controls.Add(rbThemeDark);
            themeRow.Controls.Add(themeRadios); themeRow.Controls.Add(themePreview);

            btnApplySettings = MakeButton("Apply and restart Elixar"); btnApplySettings.Font = new Font("Segoe UI Semibold", 9f);
            btnApplySettings.Margin = new Padding(S(2), S(14), 0, S(6));

            // this PC: install and updates
            var pcButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            var btnInstall = MakeButton(Setup.IsRegistered() ? "Repair the installation" : "Install Elixar on this PC");
            var btnUpdate = MakeButton("Check for updates");
            pcButtons.Controls.Add(btnInstall); pcButtons.Controls.Add(btnUpdate);
            var pcInfo = Note("Installing adds Elixar to the Start menu and to Windows' installed apps list (with Uninstall), in a folder only administrators can change. Start Elixar from the Start menu afterwards: Windows no longer shows its blue \"Windows protected your PC\" box for it.");
            pcInfo.Margin = new Padding(S(2), S(6), 0, 0);

            t.Controls.Add(Title("Settings"), 0, 0);
            t.Controls.Add(Note("Choose the language and how Elixar looks. To apply a change, Elixar restarts for a moment; nothing else changes."), 0, 1);
            t.Controls.Add(head("Language"), 0, 2); t.Controls.Add(langRow, 0, 3);
            t.Controls.Add(head("Theme"), 0, 4); t.Controls.Add(themeRow, 0, 5);
            t.Controls.Add(btnApplySettings, 0, 6);
            t.Controls.Add(head("Email reports"), 0, 7); t.Controls.Add(BuildEmailBox(), 0, 8);
            t.Controls.Add(head("This PC"), 0, 9); t.Controls.Add(pcButtons, 0, 10);
            var last = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) }; last.Controls.Add(pcInfo); t.Controls.Add(last, 0, 11);
            pageSettings.Controls.Add(t);

            // the saved choices
            (Settings.Language == "he" ? rbHebrew : rbEnglish).Checked = true;
            (Settings.ThemeMode == "dark" ? rbThemeDark : Settings.ThemeMode == "light" ? rbThemeLight : rbThemeAuto).Checked = true;

            EventHandler changed = (o, e) => { UpdateApplySettings(); themePreview.Invalidate(); };
            foreach (var rb in new[] { rbEnglish, rbHebrew, rbThemeAuto, rbThemeLight, rbThemeDark }) rb.CheckedChanged += changed;
            UpdateApplySettings();

            btnApplySettings.Click += (o, e) => ApplySettings();
            btnInstall.Click += (o, e) =>
            {
                string err = Setup.InstallHere(null);
                Lang.Box(this, err == null ? "Elixar is installed: find it in the Start menu. It also appears in Windows' installed apps list." : "Could not install Elixar: " + err, "Elixar",
                    MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (err == null) btnInstall.Text = Lang.T("Repair the installation");
            };
            btnUpdate.Click += (o, e) => CheckForUpdates(true);
        }

        void UpdateApplySettings()
        {
            string savedTheme = Settings.ThemeMode == "dark" || Settings.ThemeMode == "light" ? Settings.ThemeMode : "auto";
            btnApplySettings.Enabled = ChosenLanguage != Settings.Language || ChosenTheme != savedTheme;
        }

        void ApplySettings()
        {
            if (!list.Enabled)   // cleaning or measuring right now
            {
                Lang.Box(this, "Wait until Elixar finishes, then apply the change.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Settings.Language = ChosenLanguage;
            Settings.ThemeMode = ChosenTheme;
            UpdateApplySettings();
            if (testMode) return;
            // starts Elixar again (the protected copy in Program Files when possible), then closes this window
            try { Install.Relaunch(null); Close(); } catch { }
        }

        // a little Elixar window in the chosen colours
        void PaintThemePreview(Graphics g, Rectangle r)
        {
            bool dark = ChosenTheme == "dark" || (ChosenTheme == "auto" && Theme.WindowsUsesDark());
            Color back = dark ? Color.FromArgb(30, 30, 36) : Color.FromArgb(246, 248, 251);
            Color panel = dark ? Color.FromArgb(42, 42, 50) : Color.White;
            Color ink = dark ? Color.FromArgb(232, 232, 240) : Color.FromArgb(24, 32, 43);
            Color quiet = dark ? Color.FromArgb(165, 165, 182) : Color.FromArgb(150, 150, 160);
            Color grid = dark ? Color.FromArgb(70, 70, 86) : Color.FromArgb(222, 226, 232);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var outer = new Rectangle(r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, outer);
            var bar = new Rectangle(outer.X, outer.Y, outer.Width, Math.Max(12, outer.Height / 7));
            using (var lg = new LinearGradientBrush(bar, Color.FromArgb(38, 30, 92), Color.FromArgb(20, 70, 90), 0f)) g.FillRectangle(lg, bar);
            int pad = Math.Max(5, outer.Width / 28), top = bar.Bottom + pad, w = (outer.Width - pad * 5) / 4;
            Color[] tiers = { Color.FromArgb(199, 54, 47), Color.FromArgb(190, 105, 0), Color.FromArgb(40, 90, 200), Color.FromArgb(0, 125, 70) };
            for (int i = 0; i < 4; i++)
            {
                var c = new Rectangle(outer.X + pad + i * (w + pad), top, w, outer.Height / 4);
                using (var b = new SolidBrush(panel)) g.FillRectangle(b, c);
                using (var p = new Pen(grid)) g.DrawRectangle(p, c);
                using (var b = new SolidBrush(tiers[i])) g.FillRectangle(b, c.X, c.Y, c.Width, 3);
                using (var b = new SolidBrush(ink)) g.FillRectangle(b, c.X + 3, c.Y + 8, c.Width / 2, 4);
            }
            int ly = top + outer.Height / 4 + pad;
            var listBox = new Rectangle(outer.X + pad, ly, outer.Width - pad * 2, outer.Bottom - ly - pad);
            using (var b = new SolidBrush(panel)) g.FillRectangle(b, listBox);
            using (var p = new Pen(grid)) g.DrawRectangle(p, listBox);
            for (int y = listBox.Y + 5, i = 0; y + 3 < listBox.Bottom; y += 8, i++)
                using (var b = new SolidBrush(i == 0 ? ink : quiet)) g.FillRectangle(b, listBox.X + 5, y, (listBox.Width - 10) * (i % 3 == 0 ? 6 : i % 3 == 1 ? 4 : 5) / 7, 3);
            using (var p = new Pen(grid)) g.DrawRectangle(p, outer);
        }
    }
}
