// Tools > Sysprep > Answer file: create, load, check and save unattend.xml, and run Sysprep with it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class UnattendForm : Form
    {
        readonly float sc; readonly bool test;
        int S(int v) { return (int)Math.Round(v * sc); }
        string path;
        UnattendSpec loaded;
        TextBox tName, tOwner, tOrg, tKey, tUi, tSys, tUser, tInput, tAccount, tPassword, tAdminPw, tCommands;
        ComboBox cZone;
        CheckBox kEula, kOem, kOnline, kWireless, kPrivacy, kAccount, kAuto, kAdmin, kCopy, kDrivers, kRearm, kDelete, kVm;
        Label lPath, lProblems;
        public Action<string> Log;
        public static Func<UnattendForm, DialogResult> TestHook;   // previews and the self-test
        public string ProblemsText { get { return lProblems.Text; } }

        public UnattendForm(bool testMode)
        {
            test = testMode;
            using (var g = CreateGraphics()) sc = Math.Max(1f, g.DpiX / 96f);
            Text = Lang.T("Answer file (unattend.xml)");
            Font = new Font("Segoe UI", 9.5f); BackColor = Theme.Back; ForeColor = Theme.Ink;
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowInTaskbar = false; Icon = Brand.Load(32);
            Size = new Size(S(880), S(860)); MinimumSize = new Size(S(760), S(560));
            if (Lang.Hebrew) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }

            var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(S(16), S(12), S(16), S(8)) };
            Func<string, Label> head = t => new Label { AutoSize = true, Text = Lang.T(t), Font = new Font("Segoe UI Semibold", 11f), Margin = new Padding(0, S(10), 0, S(4)) };
            Func<string, Label> note = t => new Label { AutoSize = true, MaximumSize = new Size(S(800), 0), Text = Lang.T(t), ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(4)) };
            Func<string, TextBox, int, Control> field = (label, box, w) =>
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(2), 0, S(2)) };
                row.Controls.Add(new Label { Text = Lang.T(label), Width = S(200), Margin = new Padding(0, S(6), S(6), 0), UseMnemonic = false });
                box.Width = S(w); row.Controls.Add(box); return row;
            };
            Func<string, CheckBox> chk = t => new CheckBox { AutoSize = true, Text = Lang.T(t), Margin = new Padding(0, S(3), 0, S(1)) };

            body.Controls.Add(new Label { AutoSize = true, Text = Lang.T("Sysprep answer file"), Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, S(2)) });
            body.Controls.Add(note("An answer file sets up each PC made from your image by itself: its name, language, time zone, the setup screens to skip, an account and first sign-in commands. Sysprep reads it from its own folder."));
            lPath = new Label { AutoSize = true, MaximumSize = new Size(S(800), 0), Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, S(2), 0, S(4)) };
            body.Controls.Add(lPath);

            body.Controls.Add(head("This PC"));
            tName = new TextBox(); tOwner = new TextBox(); tOrg = new TextBox(); tKey = new TextBox();
            cZone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(360) };
            try { foreach (var z in TimeZoneInfo.GetSystemTimeZones()) cZone.Items.Add(z.Id); } catch { }
            body.Controls.Add(field("Computer name (* = random)", tName, 200));
            body.Controls.Add(field("Owner", tOwner, 300));
            body.Controls.Add(field("Organization", tOrg, 300));
            body.Controls.Add(field("Product key (optional)", tKey, 300));
            var zrow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(2), 0, S(2)) };
            zrow.Controls.Add(new Label { Text = Lang.T("Time zone"), Width = S(200), Margin = new Padding(0, S(6), S(6), 0) }); zrow.Controls.Add(cZone);
            body.Controls.Add(zrow);

            body.Controls.Add(head("Language"));
            tUi = new TextBox(); tSys = new TextBox(); tUser = new TextBox(); tInput = new TextBox();
            body.Controls.Add(field("Windows language", tUi, 120));
            body.Controls.Add(field("System locale (older apps)", tSys, 120));
            body.Controls.Add(field("Formats (dates, numbers)", tUser, 120));
            body.Controls.Add(field("Keyboards (separate with ;)", tInput, 240));
            body.Controls.Add(note("Use language names like en-US or he-IL."));

            body.Controls.Add(head("Setup screens"));
            kEula = chk("Skip the license page"); kOem = chk("Skip the manufacturer registration page"); kOnline = chk("Skip the Microsoft account screens (use a local account)");
            kWireless = chk("Skip the Wi-Fi screen"); kPrivacy = chk("Skip the privacy questions (Windows' recommended settings)");
            body.Controls.AddRange(new Control[] { kEula, kOem, kOnline, kWireless, kPrivacy });

            body.Controls.Add(head("Account"));
            kAccount = chk("Create a local administrator account"); tAccount = new TextBox(); tPassword = new TextBox { UseSystemPasswordChar = true };
            kAuto = chk("Sign in to it automatically once (for first sign-in commands)");
            body.Controls.Add(kAccount); body.Controls.Add(field("Account name", tAccount, 220)); body.Controls.Add(field("Password", tPassword, 220)); body.Controls.Add(kAuto);

            body.Controls.Add(head("Built-in Administrator"));
            body.Controls.Add(note("Sysprep turns the built-in Administrator off. Elixar can give it a password and turn it on again at the first sign-in."));
            kAdmin = chk("Turn on the built-in Administrator after setup"); tAdminPw = new TextBox { UseSystemPasswordChar = true };
            body.Controls.Add(kAdmin); body.Controls.Add(field("Administrator password", tAdminPw, 220));
            body.Controls.Add(note("A password in an answer file is only encoded, not encrypted. Elixar doesn't keep it anywhere else."));

            body.Controls.Add(head("Image"));
            kCopy = chk("Copy this account's settings to every new account (CopyProfile)");
            kDrivers = chk("Keep the installed drivers (faster first start on the same hardware)");
            kRearm = chk("Skip the activation rearm (SkipRearm)");
            body.Controls.AddRange(new Control[] { kCopy, kDrivers, kRearm });

            body.Controls.Add(head("First sign-in"));
            body.Controls.Add(note("Commands that run once, at the first sign-in (one per line), for example installing an app."));
            tCommands = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = S(780), Height = S(70), Font = new Font("Consolas", 9.5f), RightToLeft = RightToLeft.No };
            body.Controls.Add(tCommands);
            kDelete = chk("Delete the answer file after setup (recommended: it can contain a password or a product key)");
            body.Controls.Add(kDelete);

            body.Controls.Add(head("Check"));
            lProblems = new Label { AutoSize = true, MaximumSize = new Size(S(800), 0), Margin = new Padding(0, 0, 0, S(8)), UseMnemonic = false };
            body.Controls.Add(lProblems);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(S(12), S(10), S(12), S(10)), BackColor = Theme.Dark ? Color.FromArgb(36, 36, 44) : Theme.Panel, WrapContents = true };
            Func<string, Button> btn = t => new Button { AutoSize = true, Text = Lang.T(t), Margin = new Padding(0, 0, S(8), 0), Padding = new Padding(S(6), S(2), S(6), S(2)), Enabled = !test || t == "Check" || t == "Close" };
            var bNew = btn("New"); var bLoad = btn("Load..."); var bCheck = btn("Check"); var bSave = btn("Save to Sysprep's folder"); var bSaveAs = btn("Save as...");
            var bRun = btn("Run Sysprep with it..."); kVm = new CheckBox { AutoSize = true, Text = Lang.T("Virtual machine (/mode:vm)"), Margin = new Padding(S(4), S(6), S(8), 0) };
            var bClose = btn("Close"); bClose.DialogResult = DialogResult.Cancel; CancelButton = bClose;
            bSave.Font = new Font("Segoe UI Semibold", 9.5f);
            bar.Controls.AddRange(new Control[] { bNew, bLoad, bCheck, bSave, bSaveAs, bRun, kVm, bClose });
            Controls.Add(body); Controls.Add(bar);

            bNew.Click += (o, e) => { path = null; loaded = null; Fill(Unattend.Defaults()); };
            bLoad.Click += (o, e) => LoadFile();
            bCheck.Click += (o, e) => CheckNow();
            bSave.Click += (o, e) => Save(Unattend.SysprepFile);
            bSaveAs.Click += (o, e) =>
            {
                using (var d = new SaveFileDialog { Filter = "Answer file (*.xml)|*.xml", FileName = "unattend.xml", Title = Lang.T("Save the answer file") })
                    if (d.ShowDialog(this) == DialogResult.OK) Save(d.FileName);
            };
            bRun.Click += (o, e) => RunSysprep();
            kAccount.CheckedChanged += (o, e) => { tAccount.Enabled = tPassword.Enabled = kAuto.Enabled = kAccount.Checked; };
            foreach (Control c in new Control[] { tName, tOwner, tOrg, tKey, tUi, tSys, tUser, tInput, tAccount, tPassword, tAdminPw, tCommands })
                c.TextChanged += (o, e) => lProblems.Text = Lang.T("Click Check to check the answer file.");

            // start with the file in Sysprep's folder when there is one, else with this PC's settings
            UnattendSpec start = null;
            try { if (File.Exists(Unattend.SysprepFile)) { start = Unattend.Read(File.ReadAllText(Unattend.SysprepFile)); path = Unattend.SysprepFile; loaded = start; } } catch { }
            Fill(start ?? Unattend.Defaults());
            Shown += (o, e) => { Theme.Apply(this); Theme.DarkTitle(this); CheckNow(); };
        }

        public static void ShowFor(IWin32Window owner, bool testMode, Action<string> log)
        {
            using (var f = new UnattendForm(testMode) { Log = log })
            {
                if (TestHook != null) { TestHook(f); return; }
                f.ShowDialog(owner);
            }
        }

        void Fill(UnattendSpec s)
        {
            tName.Text = s.ComputerName; tOwner.Text = s.Owner; tOrg.Text = s.Organization; tKey.Text = s.ProductKey;
            int zi = cZone.Items.IndexOf(s.TimeZone); if (zi < 0 && s.TimeZone.Length > 0) zi = cZone.Items.Add(s.TimeZone); cZone.SelectedIndex = zi;
            tUi.Text = s.UILanguage; tSys.Text = s.SystemLocale; tUser.Text = s.UserLocale; tInput.Text = s.InputLocale;
            kEula.Checked = s.HideEula; kOem.Checked = s.HideOem; kOnline.Checked = s.HideOnline; kWireless.Checked = s.HideWireless; kPrivacy.Checked = s.SkipPrivacy;
            kAdmin.Checked = s.EnableAdmin; tAdminPw.Text = s.AdminPassword;
            kAccount.Checked = s.CreateAccount; tAccount.Text = s.AccountName; tPassword.Text = s.AccountPassword; kAuto.Checked = s.AutoLogon;
            tAccount.Enabled = tPassword.Enabled = kAuto.Enabled = s.CreateAccount;
            kCopy.Checked = s.CopyProfile; kDrivers.Checked = s.PersistDrivers; kRearm.Checked = s.SkipRearm;
            tCommands.Text = string.Join("\r\n", s.FirstLogon); kDelete.Checked = s.DeleteAfter;
            lPath.Text = path == null ? Lang.T("New answer file (not saved yet).") : Lang.T("File: " + path);
        }

        public UnattendSpec Spec()
        {
            var s = new UnattendSpec
            {
                Arch = loaded != null ? loaded.Arch : Unattend.ThisArch(),
                ComputerName = tName.Text.Trim(), Owner = tOwner.Text.Trim(), Organization = tOrg.Text.Trim(), ProductKey = tKey.Text.Trim(),
                TimeZone = cZone.SelectedItem as string ?? "", UILanguage = tUi.Text.Trim(), SystemLocale = tSys.Text.Trim(), UserLocale = tUser.Text.Trim(), InputLocale = tInput.Text.Trim(),
                HideEula = kEula.Checked, HideOem = kOem.Checked, HideOnline = kOnline.Checked, HideWireless = kWireless.Checked, SkipPrivacy = kPrivacy.Checked,
                CreateAccount = kAccount.Checked, AccountName = tAccount.Text.Trim(), AccountPassword = tPassword.Text, AutoLogon = kAccount.Checked && kAuto.Checked,
                EnableAdmin = kAdmin.Checked, AdminPassword = tAdminPw.Text,
                CopyProfile = kCopy.Checked, PersistDrivers = kDrivers.Checked, SkipRearm = kRearm.Checked, DeleteAfter = kDelete.Checked
            };
            s.FirstLogon.AddRange(tCommands.Lines.Select(l => l.Trim()).Where(l => l.Length > 0));
            if (loaded != null) s.Unknown.AddRange(loaded.Unknown);
            return s;
        }

        List<UnattendProblem> CheckNow()
        {
            var probs = Unattend.Check(Spec());
            try { probs = Unattend.CheckXml(Unattend.Build(Spec()), Unattend.ThisArch()).Concat(probs.Where(p => p.Level <= 1)).GroupBy(p => p.Text).Select(g => g.First()).OrderByDescending(p => p.Level).ToList(); } catch { }
            int bad = probs.Count(p => p.Level == 3), warn = probs.Count(p => p.Level == 2);
            var sb = new StringBuilder();
            sb.Append(Lang.T(bad == 0 && warn == 0 ? "Ready: Windows Setup can use this answer file." : bad > 0 ? bad + " problem(s) would stop Windows Setup." : warn + " thing(s) to look at."));
            foreach (var p in probs) sb.Append("\n" + (p.Level == 3 ? "✖  " : p.Level == 2 ? "⚠  " : "•  ") + Lang.T(p.Text));
            lProblems.Text = sb.ToString();
            lProblems.ForeColor = Theme.MapFore(bad > 0 ? Color.FromArgb(199, 54, 47) : warn > 0 ? Color.FromArgb(196, 120, 0) : Color.FromArgb(0, 125, 70));
            return probs;
        }

        void LoadFile()
        {
            using (var d = new OpenFileDialog { Filter = "Answer file (*.xml)|*.xml", Title = Lang.T("Load an answer file") })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    var fi = new FileInfo(d.FileName);
                    if (fi.Length > 4 * 1024 * 1024) throw new IOException("The file is too big for an answer file.");
                    string xml = File.ReadAllText(d.FileName);
                    var probs = Unattend.CheckXml(xml, Unattend.ThisArch());
                    var bad = probs.FirstOrDefault(p => p.Text.StartsWith("The file is not valid XML") || p.Text.StartsWith("This is not a Windows answer file"));
                    if (bad != null) { Lang.Box(this, Lang.T(bad.Text), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    loaded = Unattend.Read(xml); path = d.FileName; Fill(loaded); CheckNow();
                }
                catch (Exception ex) { Lang.Box(this, Lang.T("The answer file could not be read: ") + ex.Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        bool Save(string target)
        {
            if (test) return false;
            var probs = CheckNow();
            if (probs.Any(p => p.Level == 3)) { Lang.Box(this, Lang.T("Fix the problems marked ✖ first: Windows Setup would stop on them."), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            var s = Spec();
            if (s.Unknown.Count > 0 && string.Equals(target, path, StringComparison.OrdinalIgnoreCase) &&
                Lang.BoxNo(this, Lang.T("Save over the loaded file?") + "\n\n" + Lang.T(s.Unknown.Count + " setting(s) in it are not shown here and would be left out.") + "\n\n" + Lang.T("Use Save as... to keep the original."), "Elixar") != DialogResult.Yes) return false;
            try
            {
                string xml = Unattend.Build(s);
                bool existed = File.Exists(target);
                File.WriteAllText(target, xml, new UTF8Encoding(false));
                ChangeLog.Add("Files", existed ? "Saved over an answer file" : "Saved an answer file", target, "", "", "Sysprep", false);
                path = target; loaded = Unattend.Read(xml); lPath.Text = Lang.T("File: " + path);
                if (Log != null) Log(Lang.T("Sysprep: answer file saved: " + target));
                Lang.Box(this, Lang.T("Saved: " + target), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            catch (Exception ex) { Lang.Box(this, Lang.T("The answer file could not be saved: ") + ex.Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
        }

        void RunSysprep()
        {
            if (test) return;
            if (!Save(Unattend.SysprepFile)) return;
            string exe = Sysprep.Exe;
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess) exe = exe.Replace(@"\System32\", @"\Sysnative\");
            string args = "/generalize /oobe /shutdown" + (kVm.Checked ? " /mode:vm" : "") + " /unattend:\"" + Unattend.SysprepFile + "\"";
            var checks = Sysprep.Assess(Sysprep.Read(null));
            int block = checks.Count(c => c.Level == 3);
            if (Lang.BoxNo(this, Lang.T("Run Sysprep now?") + "\n\n" + Lang.T("Windows is generalized and the PC shuts down. Capture the image after that; the next start runs setup with your answer file.") +
                    (block > 0 ? "\n\n" + Lang.T(block == 1 ? "1 thing still stops Sysprep." : block + " things still stop Sysprep.") : "") + "\n\n  - sysprep.exe " + args, "Elixar") != DialogResult.Yes) return;
            try
            {
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
                if (Log != null) Log("Sysprep: started with " + args);
                ChangeLog.Command(exe, args, "", 0);
                Close();
            }
            catch (Exception ex) { Lang.Box(this, Lang.T("Sysprep could not be started: ") + ex.Message, "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
