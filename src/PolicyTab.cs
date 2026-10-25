// Policy manager (next to Logs): Default or Custom rules for what Elixar may change, presets, export / import, a .reg file for IT,
// and whether this PC follows its policy. Every change to the policy is written to Logs.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pagePolicy;
        RadioButton rbPolDefault, rbPolCustom;
        Label polStatus, polOrg, polCheck;
        ComboBox polPreset;
        Button btnPolPreset, btnPolSave, btnPolDiscard, btnPolExport, btnPolImport, btnPolReg, btnPolFix;
        TableLayoutPanel polGrid;
        TabControl polTabs;
        readonly Dictionary<string, ComboBox> polCombos = new Dictionary<string, ComboBox>();
        readonly Dictionary<string, Label> polMarks = new Dictionary<string, Label>();
        Dictionary<string, string> polEdit = Policy.Defaults();
        bool polCustom, polFilling;
        Label sysprepBlocked;
        List<string> polSchedDiff = new List<string>();   // where Task Scheduler differs from the policy's times
        public static Func<string, DialogResult> PolicyConfirmHook;   // self-test: answers the questions

        // for the self-test and previews
        public bool PolicyDirty { get { return btnPolSave != null && btnPolSave.Enabled; } }
        public string PolicyStatusText { get { return polStatus.Text; } }
        public void PolicySetMode(bool custom) { (custom ? rbPolCustom : rbPolDefault).Checked = true; }
        public void PolicySet(string key, string value) { var cb = polCombos[key]; var r = Policy.Rule(key); cb.SelectedIndex = Array.IndexOf(r.Values, value); }
        public void PolicyUsePreset(string name) { polPreset.SelectedIndex = Array.IndexOf(Policy.PresetNames, name); PolicyPreset(); }
        public string PolicySave() { return PolicySaveNow(); }
        public bool PolicyComboEnabled(string key) { return polCombos[key].Enabled; }
        public bool MailConnectEnabled { get { return pnlChoose != null && pnlChoose.Enabled; } }
        public string MailStatusText { get { return mailStatus == null ? null : mailStatus.Text; } }
        public string PolicyOrgText { get { return polOrg == null ? null : polOrg.Text; } }
        // the Sysprep tools' "turned off" note, or null when they can be used
        public string SysprepOffText { get { return pageSysprep != null && sysprepBlocked != null && pageSysprep.Controls.Cast<Control>().Any(c => c != sysprepBlocked && !c.Enabled) ? sysprepBlocked.Text : null; } }
        public void ShowPolicy() { tabs.SelectedTab = pagePolicy; }
        public void PolicyScrollTo(string key) { var sp = polGrid.Parent as ScrollableControl; if (sp != null) sp.ScrollControlIntoView(polCombos[key]); }
        public void ShowAutomationTab() { tabs.SelectedTab = pageAuto; }

        DialogResult PolAsk(string text, bool warn)
        {
            if (PolicyConfirmHook != null) return PolicyConfirmHook(text);
            return warn ? Lang.BoxNo(this, text, "Elixar") : Lang.Box(this, text, "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        }

        void BuildPolicyTab()
        {
            pagePolicy = new TabPage("Policy manager") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(7);
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Title("Policy manager"), 0, 0);
            t.Controls.Add(Note("A policy decides what Elixar may change on this PC and how. Default is Elixar's own rules: exactly how Elixar works without a policy. " +
                                "Custom is your own copy: change any rule, and switch back to Default any time - your custom rules are kept. Every change to the policy is written to Logs."), 0, 1);

            // Default / Custom, like a switch
            var top = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, S(6)) };
            Func<string, RadioButton> seg = s => new RadioButton { Text = s, Appearance = Appearance.Button, AutoSize = false, Size = new Size(S(120), S(32)), TextAlign = ContentAlignment.MiddleCenter,
                                                                    FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 0, 0, 0) };
            rbPolDefault = seg("Default"); rbPolCustom = seg("Custom");
            rbPolDefault.FlatAppearance.BorderColor = rbPolCustom.FlatAppearance.BorderColor = Theme.Grid;
            polStatus = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(S(16), S(7), 0, 0), UseMnemonic = false };
            top.Controls.AddRange(new Control[] { rbPolDefault, rbPolCustom, polStatus });
            t.Controls.Add(top, 0, 2);

            var presetRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, S(4)) };
            polPreset = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(240), Margin = new Padding(0, S(2), S(8), 0) };
            polPreset.Items.AddRange(Policy.PresetNames); polPreset.SelectedIndex = 0;
            btnPolPreset = MakeButton("Use this preset");
            polOrg = new Label { AutoSize = true, ForeColor = Theme.MapFore(Color.FromArgb(150, 90, 0)), Margin = new Padding(S(16), S(6), 0, 0), UseMnemonic = false, MaximumSize = new Size(S(560), 0) };
            presetRow.Controls.AddRange(new Control[] { new Label { AutoSize = true, Text = "Start from a preset:", Margin = new Padding(S(2), S(6), S(6), 0) }, polPreset, btnPolPreset, polOrg });
            t.Controls.Add(presetRow, 0, 3);

            // the rules, grouped
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, S(4), 0, S(4)), BackColor = Theme.Panel };
            polGrid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, Location = new Point(0, 0), Padding = new Padding(S(10), S(6), S(10), S(6)), BackColor = Theme.Panel };
            polGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(380))); polGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(270)));
            polGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(240))); polGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;
            Action<Control, int> add = (c, col) => polGrid.Controls.Add(c, col, row);
            polGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            add(new Label { AutoSize = true, Text = "Rule", Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.Quiet, Margin = new Padding(0, S(2), 0, S(4)) }, 0);
            add(new Label { AutoSize = true, Text = "In force", Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.Quiet, Margin = new Padding(0, S(2), 0, S(4)) }, 1);
            add(new Label { AutoSize = true, Text = "Default", Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.Quiet, Margin = new Padding(S(10), S(2), 0, S(4)) }, 2);
            row++;
            string group = null;
            foreach (var r in Policy.Rules)
            {
                if (r.Group != group)
                {
                    group = r.Group;
                    polGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    var gh = new Label { AutoSize = true, Text = group, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.MapFore(Theme.Accent), Margin = new Padding(0, S(10), 0, S(4)) };
                    add(gh, 0); polGrid.SetColumnSpan(gh, 4); row++;
                }
                polGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var name = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, S(2), S(10), S(6)), BackColor = Theme.Panel };
                name.Controls.Add(new Label { AutoSize = true, Text = r.Name, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0) });
                name.Controls.Add(new Label { AutoSize = true, Text = r.Help, ForeColor = Theme.Quiet, MaximumSize = new Size(S(370), 0), Margin = new Padding(0, S(1), 0, 0) });
                add(name, 0);
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(255), Margin = new Padding(0, S(4), S(6), 0), Tag = r.Key };
                cb.Items.AddRange(r.Labels.Cast<object>().ToArray());
                cb.SelectedIndexChanged += (o, e) => { if (!polFilling) { var k = (string)((ComboBox)o).Tag; polEdit[k] = Policy.Rule(k).Values[Math.Max(0, ((ComboBox)o).SelectedIndex)]; PolicyRefresh(); } };
                polCombos[r.Key] = cb; add(cb, 1);
                add(new Label { AutoSize = true, Text = r.Label(r.Default), ForeColor = Theme.Quiet, Margin = new Padding(S(10), S(7), S(6), 0) }, 2);
                var mark = new Label { AutoSize = true, Margin = new Padding(S(4), S(7), 0, 0), Font = new Font("Segoe UI Semibold", 9f), UseMnemonic = false };
                polMarks[r.Key] = mark; add(mark, 3);
                row++;
            }
            scroll.Controls.Add(polGrid);
            // two pages: the rules, and what to do with each cleaning item
            polTabs = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, S(4)) };
            var pRules = new TabPage("Rules") { BackColor = Theme.Panel }; scroll.Margin = new Padding(0); pRules.Controls.Add(scroll);
            var pItems = new TabPage("Cleaning items") { BackColor = Theme.Panel };
            var scroll2 = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel };
            var itemsGrid = BuildPolicyItems(); itemsGrid.Location = new Point(0, 0);
            scroll2.Controls.Add(itemsGrid); pItems.Controls.Add(scroll2);
            var pFolders = new TabPage("Folders") { BackColor = Theme.Panel };
            var scroll3 = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Panel };
            var foldersGrid = BuildPolicyFolders(); foldersGrid.Location = new Point(0, 0);
            scroll3.Controls.Add(foldersGrid); pFolders.Controls.Add(scroll3);
            polTabs.TabPages.Add(pRules); polTabs.TabPages.Add(pFolders); polTabs.TabPages.Add(pItems); polTabs.TabPages.Add(BuildPolicyFleet());
            t.Controls.Add(polTabs, 0, 4);

            var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, S(2), 0, S(4)) };
            polCheck = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(S(2), S(7), S(10), 0), UseMnemonic = false, MaximumSize = new Size(S(900), 0) };
            btnPolFix = MakeButton("Set a PIN or password now...");
            checkRow.Controls.AddRange(new Control[] { polCheck, btnPolFix });
            t.Controls.Add(checkRow, 0, 5);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
            btnPolSave = MakeButton("Save the policy"); btnPolSave.Font = new Font("Segoe UI Semibold", 9f);
            btnPolDiscard = MakeButton("Discard changes");
            btnPolExport = MakeButton("Export...");
            btnPolImport = MakeButton("Import...");
            btnPolReg = MakeButton("Export for IT (.reg or Group Policy)...");
            buttons.Controls.AddRange(new Control[] { btnPolSave, btnPolDiscard, btnPolExport, btnPolImport, btnPolReg });
            t.Controls.Add(buttons, 0, 6);
            pagePolicy.Controls.Add(t);

            rbPolDefault.CheckedChanged += (o, e) => { PolSegColors(); if (!polFilling && rbPolDefault.Checked) { polCustom = false; PolicyRefresh(); } };
            rbPolCustom.CheckedChanged += (o, e) => { PolSegColors(); if (!polFilling && rbPolCustom.Checked) { polCustom = true; PolicyRefresh(); } };
            pagePolicy.VisibleChanged += (o, e) => PolSegColors();   // after the theme has painted everything
            btnPolPreset.Click += (o, e) => PolicyPreset();
            btnPolSave.Click += (o, e) => { string err = PolicySaveNow(); if (err != null && err != "") Lang.Box(this, Lang.T("The policy was not saved: ") + Lang.T(err), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning); };
            btnPolDiscard.Click += (o, e) => PolicyLoadUi();
            btnPolExport.Click += (o, e) => PolicyExport(false);
            btnPolReg.Click += (o, e) => PolicyExport(true);
            btnPolImport.Click += (o, e) => PolicyImport();
            btnPolFix.Click += (o, e) =>
            {
                if (Policy.Check().Count > 0) { if (!testMode && PinForm.SetNew(this)) { Log(Lang.T("A PIN or password now protects Elixar.")); UpdatePinUi(); } PolicyRefresh(); return; }
                PolicyApplySchedules();
            };
            PolicyLoadUi();
        }

        void OnPolicyTab() { Policy.Load(); PolicyLoadUi(); PolSegColors(); PolicyCheckSchedules(); }

        // compares Task Scheduler with the policy's times, in the background
        void PolicyCheckSchedules()
        {
            if (testMode || !(Win.IsWindows || Lx.On)) return;
            System.Threading.Tasks.Task.Factory.StartNew(() =>
            {
                var tidy = TidyPrefs.Load(); var mp = MailPrefs.Load();
                var plan = Automation.Plan(Install.Exe, AlertPercent(), tidy.AnyDaily, MailAccount.Email != null && (mp.Alerts || mp.Summary) && Policy.Allowed("email"));
                return Automation.ScheduleDifferences(plan, Automation.Query);
            }).ContinueWith(t => UI(() => { polSchedDiff = t.IsFaulted ? new List<string>() : t.Result; PolicyRefresh(); }));
        }

        void PolicyApplySchedules()
        {
            if (testMode) return;
            btnPolFix.Enabled = false;
            System.Threading.Tasks.Task.Factory.StartNew(() => ChangeLog.Within("Policy", () => ApplySchedules(Log))).ContinueWith(t => UI(() =>
            {
                btnPolFix.Enabled = true;
                var errs = t.IsFaulted ? new List<string> { t.Exception.GetBaseException().Message } : t.Result;
                if (errs.Count > 0) Lang.Box(this, Lang.T("Some schedules could not be set:") + "\n\n" + string.Join("\n", errs), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowScheduleText(); ShowMailWhen(); if (alertPercent > 0) LoadAutomationState();
                PolicyCheckSchedules();
            }));
        }

        // the chosen side of the Default / Custom switch is filled with the accent colour
        void PolSegColors()
        {
            foreach (var rb in new[] { rbPolDefault, rbPolCustom })
            {
                if (rb == null) continue;
                rb.BackColor = rb.Checked ? Theme.Accent : Theme.Panel;
                rb.ForeColor = rb.Checked ? Color.White : Theme.Ink;
                rb.FlatAppearance.CheckedBackColor = Theme.Accent;
                rb.FlatAppearance.BorderColor = rb.Checked ? Theme.Accent : Theme.Grid;
            }
        }

        void PolicyLoadUi()
        {
            polCustom = Policy.UseCustom;
            polEdit = new Dictionary<string, string>(Policy.Custom);
            polFilling = true;
            (polCustom ? rbPolCustom : rbPolDefault).Checked = true;
            polFilling = false;
            PolicyRefresh();
        }

        // what the screen shows: in Default the combos show the default rules (locked); an organization's rules are always locked
        void PolicyRefresh()
        {
            if (polGrid == null) return;
            var org = Policy.Org();
            polFilling = true;
            foreach (var r in Policy.Rules)
            {
                var cb = polCombos[r.Key]; var mark = polMarks[r.Key];
                string v = org.ContainsKey(r.Key) ? org[r.Key] : polCustom ? polEdit[r.Key] : r.Default;
                cb.SelectedIndex = Math.Max(0, Array.IndexOf(r.Values, v));
                cb.Enabled = polCustom && !org.ContainsKey(r.Key);
                if (org.ContainsKey(r.Key)) { mark.Text = "🔒 " + Lang.T(Policy.ByCompany(r.Key) ? "Off on company PCs" : "Set by your organization"); mark.ForeColor = Theme.MapFore(Color.FromArgb(150, 90, 0)); }
                else if (v != r.Default) { mark.Text = "● " + Lang.T("Changed"); mark.ForeColor = Theme.MapFore(Theme.Accent); }
                else mark.Text = "";
            }
            polFilling = false;
            int diff = polCustom ? Policy.Differences(polEdit) : 0;
            polStatus.Text = Lang.T(polCustom ? "Custom policy" : "Default policy") + "  ·  " +
                             Lang.T(!polCustom ? "Elixar's own rules" : diff == 0 ? "same as Default" : diff == 1 ? "1 rule differs from Default" : diff + " rules differ from Default");
            int byCo = Policy.CompanyBlocks.Count(Policy.ByCompany), bySet = org.Count - byCo;
            polOrg.Text = (byCo == 0 ? "" : string.Format(Lang.T("This PC is joined to {0}: Phone, Sysprep and Email are off."), Policy.Company()) + (bySet > 0 ? "  " : "")) +
                          (bySet == 0 ? "" : Lang.T(bySet == 1 ? "1 rule is set by your organization and can't be changed here." : bySet + " rules are set by your organization and can't be changed here."));
            polPreset.Enabled = btnPolPreset.Enabled = polCustom;
            PolicyRefreshItems();
            bool dirty = polCustom != Policy.UseCustom || Policy.Rules.Any(r => polEdit[r.Key] != (Policy.Custom.ContainsKey(r.Key) ? Policy.Custom[r.Key] : r.Default)) ||
                         polEdit.Keys.Concat(Policy.Custom.Keys).Where(Policy.IsItemKey).Distinct().Any(k => ItemOf(polEdit, k) != ItemOf(Policy.Custom, k));
            btnPolSave.Enabled = btnPolDiscard.Enabled = dirty;
            var pinProbs = Policy.Check();
            var probs = pinProbs.Concat(polSchedDiff).ToList();
            btnPolFix.Text = Lang.T(pinProbs.Count > 0 ? "Set a PIN or password now..." : "Apply the schedules now");
            polCheck.Text = dirty ? Lang.T("Not saved yet: click Save the policy.") : probs.Count == 0 ? "✔ " + Lang.T("This PC follows its policy.") : "⚠ " + string.Join("  ", probs.Select(Lang.T));
            polCheck.ForeColor = Theme.MapFore(dirty ? Color.FromArgb(196, 120, 0) : probs.Count == 0 ? Color.FromArgb(0, 125, 70) : Color.FromArgb(190, 40, 40));
            btnPolFix.Visible = !dirty && probs.Count > 0;
        }

        void PolicyPreset()
        {
            if (!polCustom) return;
            polEdit = Policy.Preset(Policy.PresetNames[Math.Max(0, polPreset.SelectedIndex)]);
            PolicyRefresh();
        }

        // returns null when saved, "" when you stopped it, or why it didn't save
        string PolicySaveNow()
        {
            var target = polCustom ? polEdit : Policy.Defaults();
            bool loosens = Policy.Rules.Any(r => Strictness(r, target[r.Key]) < Strictness(r, Policy.Get(r.Key))) || (Policy.UseCustom && !polCustom);
            // a PIN is needed to change the policy (the rule in force now decides)
            if (Policy.Get("pin.policy") == "on" && PinLock.IsSet && PolicyConfirmHook == null && !testMode)
                if (!PinLock.Unlock(this)) return "";
            var risky = items.Where(it => it.Tier != Tier.Safe && polCustom && ItemRule.Parse(ItemOf(target, Policy.ItemKey(it.Name))).Do == "auto" && Policy.GetItem(it.Name).Do != "auto").ToList();
            if (risky.Count > 0 && PolAsk(Lang.T("These items will be cleaned by themselves, without asking each time:") + "\n\n" + string.Join("\n", risky.Select(it => "  - " + Lang.T(it.Name) + "  (" + Lang.T(it.Tier == Tier.Review ? "Check first" : "Can't be undone") + ")")) +
                                          "\n\n" + Lang.T("They are not Safe items. Save the policy anyway?"), true) != DialogResult.Yes) return "";
            if (loosens && PolAsk(Lang.T("This makes the rules less strict. Save it?") + "\n\n" + Lang.T("Elixar writes every change to the policy in Logs."), true) != DialogResult.Yes) return "";
            var schedBefore = Policy.Rules.Where(r => Policy.IsSchedule(r.Key)).Select(r => Policy.Get(r.Key)).Concat(new[] { Policy.AnyAuto().ToString() }).ToList();
            string err = Policy.Save(polCustom, polEdit);
            if (err != null) return err;
            Log(Lang.T(polCustom ? "Policy saved: Custom." : "Policy saved: Default."));
            ApplyPolicyUi();
            PolicyLoadUi();
            // new times: Task Scheduler follows right away
            if (!schedBefore.SequenceEqual(Policy.Rules.Where(r => Policy.IsSchedule(r.Key)).Select(r => Policy.Get(r.Key)).Concat(new[] { Policy.AnyAuto().ToString() }))) { ShowScheduleText(); ShowMailWhen(); PolicyApplySchedules(); }
            // the Windows tweaks the policy now fixes are set right away
            if (!testMode && !LinuxUi && Tweaks.ApplyPolicy(Log) > 0) RefreshTweaks();
            else if (pageTweaks != null) RefreshTweaks();
            // a rule now asks for a PIN that isn't set
            if (!testMode && PolicyConfirmHook == null && Policy.Check().Count > 0 && !PinLock.IsSet &&
                PolAsk(Lang.T("The policy needs a PIN or password. Set one now?"), false) == DialogResult.Yes && PinForm.SetNew(this)) { UpdatePinUi(); PolicyRefresh(); }
            return null;
        }

        // higher = stricter, to know when a change makes the rules looser
        static int Strictness(PolicyRule r, string v)
        {
            switch (r.Key)
            {
                case "clean.level": return v == "safe" ? 2 : v == "review" ? 1 : 0;
                case "clean.restore": return v == "always" ? 1 : 0;
                case "pin.open": return v == "required" ? 1 : 0;
                case "pin.policy": return v == "on" ? 1 : 0;
                default:
                    if (r.Key.StartsWith("tweak.")) return v == "choice" ? 0 : 1; return v == "block" ? 1 : 0;
            }
        }

        void PolicyExport(bool reg)
        {
            if (testMode) return;
            var rules = Policy.Rules.ToDictionary(r => r.Key, r => Policy.Get(r.Key));
            using (var d = new SaveFileDialog { Filter = reg ? Lang.T("Registry file with this policy (*.reg)") + "|*.reg|" + Lang.T("Group Policy and Intune template (*.admx)") + "|*.admx" : "Elixar policy (*.elixarpolicy)|*.elixarpolicy",
                                                FileName = "Elixar policy " + DateTime.Today.ToString("yyyy-MM-dd") + (reg ? ".reg" : ".elixarpolicy"),
                                                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), OverwritePrompt = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (reg && (d.FilterIndex == 2 || d.FileName.EndsWith(".admx", StringComparison.OrdinalIgnoreCase)))
                {
                    List<string> files;
                    try { files = Policy.SaveAdmx(Path.GetDirectoryName(d.FileName)); }
                    catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); return; }
                    using (ChangeLog.In("Policy")) ChangeLog.Add("Settings", "Policy: Group Policy template saved", files[0], "", "", "", false);
                    Log(Lang.T("Policy exported: ") + files[0]);
                    Lang.Box(this, Lang.T("Saved Elixar.admx with its texts (en-US, he-IL) in this folder:") + "\n" + Path.GetDirectoryName(files[0]) + "\n\n" +
                                   Lang.T("Copy them to C:\\Windows\\PolicyDefinitions (or your domain's Central Store), or import Elixar.admx in Intune. The rules then show in the Group Policy editor under Computer Configuration > Administrative Templates > Elixar. The per-item cleaning rules are only in the .reg file."),
                             "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    if (reg) SafeFile.Save(d.FileName, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Policy.RegText(rules))).ToArray());   // regedit reads UTF-16
                    else SafeFile.Save(d.FileName, Policy.ExportText(rules));
                }
                catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); return; }
                using (ChangeLog.In("Policy")) ChangeLog.Add("Settings", reg ? "Policy: exported for IT (.reg)" : "Policy: exported", d.FileName, "", "", "", false);
                Log(Lang.T("Policy exported: ") + d.FileName);
                if (reg) Lang.Box(this, Lang.T("Saved. Import it on each PC (regedit, Group Policy Preferences or Intune) to set these rules for the whole organization. Elixar then shows them as set by your organization."),
                                  "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        void PolicyImport()
        {
            if (testMode) return;
            string text;
            using (var d = new OpenFileDialog { Filter = "Elixar policy (*.elixarpolicy)|*.elixarpolicy|Text (*.txt)|*.txt", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { var fi = new FileInfo(d.FileName); if (fi.Length > 64 * 1024) { Lang.Box(this, "Not an Elixar policy file.", "Elixar"); return; } text = File.ReadAllText(d.FileName); }
                catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); return; }
            }
            PolicyImportText(text);
        }

        // shows what the file sets, then takes it as the Custom policy (saved only when you click Save the policy)
        public bool PolicyImportText(string text)
        {
            var res = Policy.Import(text);
            if (res.Problems.Count > 0 && res.Problems[0] == "Not an Elixar policy file.") { if (PolicyConfirmHook == null) Lang.Box(this, Lang.T("Not an Elixar policy file."), "Elixar"); return false; }
            var diff = Policy.Rules.Where(r => res.Rules[r.Key] != r.Default).Select(r => "  - " + Lang.T(r.Name) + ": " + Lang.T(r.Label(res.Rules[r.Key]))).ToList();
            string ask = Lang.T("Use the rules in this file as your Custom policy?") + "\n\n" +
                         (diff.Count == 0 ? Lang.T("It sets the same rules as Default.") : string.Join("\n", diff)) + "\n\n" +
                         (!res.HadFingerprint ? Lang.T("The file has no fingerprint: it may have been written by hand.") + "\n\n" :
                          !res.FingerprintOk ? Lang.T("Careful: the fingerprint doesn't match - the file was changed after it was exported.") + "\n\n" : "") +
                         (res.Problems.Count > 0 ? Lang.T("Left out:") + "\n" + string.Join("\n", res.Problems.Take(8).Select(p => "  - " + p)) + "\n\n" : "") +
                         Lang.T("Check the rules, then click Save the policy.");
            if (PolAsk(ask, !res.FingerprintOk) != DialogResult.Yes) return false;
            polCustom = true; polEdit = res.Rules;
            polFilling = true; rbPolCustom.Checked = true; polFilling = false;
            PolicyRefresh();
            return true;
        }

        // ================= the rules at work across the window =================
        public void ApplyPolicyUi()
        {
            if (nav == null) return;
            Action<TabPage, string> navOn = (page, key) =>
            {
                var it = nav.Items.FirstOrDefault(i => i.Page == page); if (it == null) return;
                if (it.Tag == null) it.Tag = Tuple.Create(it.Enabled, it.Tip);   // how it was before any policy
                var was = (Tuple<bool, string>)it.Tag;
                bool ok = Policy.Allowed(key);
                it.Enabled = was.Item1 && ok;
                it.Tip = ok ? was.Item2 : Policy.ByCompany(key) ? Lang.T("Turned off on company PCs. Ask your IT department.") :
                         Policy.Source(key) == "organization" ? Lang.T("Turned off by your organization's policy. Ask your IT department.") : Lang.T("Turned off by the policy (Policy manager).");
            };
            navOn(pagePhone, "phone");
            navOn(pageEmail, "email");
            UpdateMailUi();
            nav.Invalidate();
            if (tabs.SelectedTab != null && nav.Items.Any(i => i.Page == tabs.SelectedTab && !i.Enabled)) tabs.SelectedTab = pageDash;
            // restore point before a clean
            string rp = Policy.Get("clean.restore");
            if (chkRestore != null && !LinuxUi)
            {
                if (rp != "ask") chkRestore.Checked = rp == "always";
                chkRestore.Enabled = rp == "ask" && !cleaningNow;
            }
            // Tools > Sysprep
            if (pageSysprep != null)
            {
                bool ok = Policy.Allowed("sysprep");
                foreach (Control c in pageSysprep.Controls) if (c != sysprepBlocked) c.Enabled = ok;   // Windows greys out every button inside
                if (!ok && sysprepBlocked == null)
                {
                    sysprepBlocked = new Label { Dock = DockStyle.Top, AutoSize = false, Height = S(34), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(S(12), 0, 0, 0),
                                                 Font = new Font("Segoe UI Semibold", 9.5f),
                                                 BackColor = Theme.Dark ? Color.FromArgb(70, 55, 20) : Color.FromArgb(255, 243, 205), ForeColor = Theme.Dark ? Color.FromArgb(255, 220, 150) : Color.FromArgb(110, 75, 0) };
                    pageSysprep.Controls.Add(sysprepBlocked);
                }
                if (sysprepBlocked != null)
                {
                    sysprepBlocked.Visible = !ok;
                    sysprepBlocked.Text = Policy.ByCompany("sysprep") ? string.Format(Lang.T("Sysprep and golden image tools are turned off on company PCs (this PC is joined to {0}). Ask your IT department."), Policy.Company())
                                                                      : Lang.T("Sysprep and golden image tools are turned off by the policy (Policy manager).");
                }
            }
            // Automation: the rules the policy fixes can't be changed there
            if (cmbUndoDays != null)
            {
                cmbUndoDays.Enabled = !Policy.Fixes("undo.days");
                chkBin.Enabled = !Policy.Fixes("bin.days"); if (Policy.Fixes("bin.days")) cmbBinDays.Enabled = false; else cmbBinDays.Enabled = chkBin.Checked;
                chkLowClean.Enabled = !Policy.Fixes("low.clean");
            }
            // Settings: a required PIN can't be switched off
            if (PinSwitch != null && !testMode) PinSwitch.Enabled = !(Policy.Get("pin.open") == "required" && PinLock.IsSet);
        }

        // only the items the policy allows are cleaned; the others are named
        List<CleanItem> PolicyFilter(List<CleanItem> sel)
        {
            var max = Policy.MaxTier;
            var never = sel.Where(Policy.Never).ToList();
            if (never.Count > 0)
            {
                Log(Lang.T("Not cleaned (the policy says never touch them): ") + string.Join(", ", never.Select(i => Lang.T(i.Name))));
                if (!testMode) Lang.Box(this, Lang.T("The policy says never touch these, so they are left out:") + "\n\n" + string.Join("\n", never.Select(i => "  - " + Lang.T(i.Name))), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                sel = sel.Except(never).ToList();
            }
            var no = sel.Where(i => i.Tier > max).ToList();
            if (no.Count == 0) return sel;
            Log(Lang.T("Not cleaned (the policy doesn't allow them): ") + string.Join(", ", no.Select(i => Lang.T(i.Name))));
            if (!testMode)
                Lang.Box(this, Lang.T("The policy allows only " + (max == Tier.Safe ? "Safe items" : "Safe and Check first items") + ", so these are left out:") + "\n\n" +
                               string.Join("\n", no.Select(i => "  - " + Lang.T(i.Name))), "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return sel.Except(no).ToList();
        }

        // the policy asks for a PIN that isn't set yet: asked once each time Elixar opens
        void PolicyPinCheck()
        {
            if (testMode || Policy.Get("pin.open") != "required" || PinLock.IsSet) return;
            var r = NiceBox.ShowWith(this, Lang.T("The policy on this PC needs a PIN or password to open Elixar.") + "\n\n" + Lang.T("Choose a 4, 6 or 8 digit PIN, or a password."),
                                     Lang.T("Protect Elixar with a PIN or password"), MessageBoxButtons.YesNo, MessageBoxIcon.Information, 0, new[] { Lang.T("Set it up..."), Lang.T("Later") });
            if (r == DialogResult.Yes && PinForm.SetNew(this)) Log(Lang.T("A PIN or password now protects Elixar."));
            UpdatePinUi(); ApplyPolicyUi();
        }

        // ================= Cleaning items: what to do with each one, when, and how old its files must be =================
        class PolItemRow { public CleanItem Item; public ComboBox Do, When, Time, Age; public Label Mark; public string[] DoValues = ItemRule.Dos; public int[] AgeValues = ItemRule.Ages; }
        static readonly string[] HalfHours = Enumerable.Range(0, 48).Select(i => (i / 2).ToString("00") + ":" + (i % 2 == 0 ? "00" : "30")).ToArray();

        // the four choices for one item, in columns 1 to 5 of a grid row (the same item can be on both pages: they stay in step)
        PolItemRow MakeItemRow(TableLayoutPanel g, int row, CleanItem it)
        {
            bool dl = it.Name == Policy.DownloadsItem;
            var r = new PolItemRow { Item = it };
            if (dl) { r.DoValues = new[] { "ask", "auto" }; r.AgeValues = Policy.DownloadAges; }
            Func<int, object[], ComboBox> combo = (w, opts) => { var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(w), Margin = new Padding(0, S(3), S(6), S(2)) }; cb.Items.AddRange(opts); return cb; };
            r.Do = combo(180, dl ? new object[] { Lang.T("Leave it to me"), Lang.T("Move old files to Undo by itself") } : ItemRule.DoLabels.Select(Lang.T).Cast<object>().ToArray());
            r.When = combo(200, ItemRule.WhenLabels.Select(Lang.T).Cast<object>().ToArray());
            r.Time = combo(80, HalfHours.Cast<object>().ToArray());
            r.Age = combo(160, r.AgeValues.Select(a => (object)Lang.T(ItemRule.AgeLabels[Array.IndexOf(ItemRule.Ages, a)])).ToArray());
            r.Mark = new Label { AutoSize = true, Margin = new Padding(S(4), S(7), 0, 0), Font = new Font("Segoe UI Semibold", 9f), UseMnemonic = false };
            g.Controls.Add(r.Do, 1, row); g.Controls.Add(r.When, 2, row); g.Controls.Add(r.Time, 3, row); g.Controls.Add(r.Age, 4, row); g.Controls.Add(r.Mark, 5, row);
            EventHandler changed = (o, e) =>
            {
                if (polFilling) return;
                var ir = new ItemRule { Do = r.DoValues[Math.Max(0, r.Do.SelectedIndex)], When = ItemRule.Whens[Math.Max(0, r.When.SelectedIndex)],
                                        Time = HalfHours[Math.Max(0, r.Time.SelectedIndex)], Age = r.AgeValues[Math.Max(0, r.Age.SelectedIndex)] };
                string k = Policy.ItemKey(it.Name);
                if (ir.ToString() == ItemRule.DefaultText) polEdit.Remove(k); else polEdit[k] = ir.ToString();
                PolicyRefresh();
            };
            r.Do.SelectedIndexChanged += changed; r.When.SelectedIndexChanged += changed; r.Time.SelectedIndexChanged += changed; r.Age.SelectedIndexChanged += changed;
            polItems.Add(r);
            return r;
        }

        // ================= Folders: the Dashboard's places, by their warning sign (Critical, Caution, Review, Safe) =================
        Control BuildPolicyFolders()
        {
            var g = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 6, Padding = new Padding(S(10), S(14), S(10), S(10)), BackColor = Theme.Panel, Margin = new Padding(0) };
            foreach (int w in new[] { 330, 190, 210, 90, 170 }) g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(w)));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;
            Action<Control, int> add = (c, col) => g.Controls.Add(c, col, row);
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var head = new Label { AutoSize = true, Text = "Folders: what to do with each one, by its warning sign", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.MapFore(Theme.Accent), Margin = new Padding(0, S(4), 0, S(2)) };
            add(head, 0); g.SetColumnSpan(head, 6); row++;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var note = new Label { AutoSize = true, MaximumSize = new Size(S(1000), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(6)),
                                   Text = "The places on the Dashboard's map of C:. Critical folders are never deleted. Caution, Review and Safe folders are cleaned by the Cleaner item named under them, so a rule here is the same rule as on Cleaning items. Downloads holds your own files: old ones can only go to Undo, never straight away, and never newer than 7 days." };
            add(note, 0); g.SetColumnSpan(note, 6); row++;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heads = new[] { "Folder", "What to do", "When", "Time", "Only files" };
            for (int i = 0; i < heads.Length; i++) add(new Label { AutoSize = true, Text = heads[i], Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.Quiet, Margin = new Padding(0, S(2), 0, S(4)) }, i);
            row++;
            var extra = Policy.ExtraItems();
            var locs = DashData.Build().Where(l => DashData.TierOrder.Contains(l.Tier)).OrderBy(l => Array.IndexOf(DashData.TierOrder, l.Tier)).ToList();
            string group = null;
            foreach (var l in locs)
            {
                if (l.Tier != group)
                {
                    group = l.Tier;
                    g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    string title = l.Tier == "critical" ? "\u26D4 Critical" : l.Tier == "caution" ? "\u26A0 Caution" : l.Tier == "review" ? "\u25CF Review" : "\u2714 Safe";
                    var gh = new Label { AutoSize = true, Text = Lang.T(title.Substring(2)), Font = new Font("Segoe UI Semibold", 10f), ForeColor = Theme.MapFore(TierColor(l.Tier)), Margin = new Padding(0, S(10), 0, S(4)) };
                    gh.Text = title.Substring(0, 2) + gh.Text;
                    add(gh, 0); g.SetColumnSpan(gh, 6); row++;
                }
                g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                string itemName; Policy.FolderItems.TryGetValue(l.Id, out itemName);
                var item = itemName == null ? null : items.Concat(extra).FirstOrDefault(i => i.Name == itemName);
                var name = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, S(4), S(8), S(4)), BackColor = Theme.Panel };
                name.Controls.Add(new Label { AutoSize = true, Text = Lang.T(l.Name), MaximumSize = new Size(S(320), 0), UseMnemonic = false, Margin = new Padding(0) });
                name.Controls.Add(new Label { AutoSize = true, Text = item == null ? l.Path : (item.Name == Policy.DownloadsItem ? l.Path : Lang.T("Cleaned by: ") + Lang.T(item.Name)), ForeColor = Theme.Quiet, MaximumSize = new Size(S(320), 0), UseMnemonic = false, Margin = new Padding(0, S(1), 0, 0) });
                add(name, 0);
                if (item != null) MakeItemRow(g, row, item);
                else
                {
                    var fixedL = new Label { AutoSize = true, Text = "\uD83D\uDD12 " + Lang.T(Policy.FolderFixed(l.Id, l.Tier)), ForeColor = Theme.Quiet, Margin = new Padding(0, S(7), 0, 0), UseMnemonic = false };
                    add(fixedL, 1); g.SetColumnSpan(fixedL, 4);
                }
                polFolderRows++;
                row++;
            }
            return g;
        }
        int polFolderRows;
        readonly List<PolItemRow> polItems = new List<PolItemRow>();

        static string ItemOf(Dictionary<string, string> d, string key) { string v; return d.TryGetValue(key, out v) ? v : ItemRule.DefaultText; }

        Control BuildPolicyItems()
        {
            var g = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 6, Padding = new Padding(S(10), S(14), S(10), S(10)), BackColor = Theme.Panel, Margin = new Padding(0) };
            foreach (int w in new[] { 330, 190, 210, 90, 170 }) g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(w)));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;
            Action<Control, int> add = (c, col) => g.Controls.Add(c, col, row);
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var head = new Label { AutoSize = true, Text = "Cleaning items: what to do with each one", Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.MapFore(Theme.Accent), Margin = new Padding(0, S(4), 0, S(2)) };
            add(head, 0); g.SetColumnSpan(head, 6); row++;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var note = new Label { AutoSize = true, MaximumSize = new Size(S(1000), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(6)),
                                   Text = "Every item in the Cleaner, by its warning sign. Ask me: it waits for you, as now. Clean it by itself: Elixar cleans it on its own at the time you choose (it is checked every half hour, also after the PC was off). Never touch it: it can't be ticked or cleaned. Only files older than: newer files stay, also when you clean by hand (for items made of files)." };
            add(note, 0); g.SetColumnSpan(note, 6); row++;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foreach (var h in new[] { "Item", "What to do", "When", "Time", "Only files" })
                add(new Label { AutoSize = true, Text = h, Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.Quiet, Margin = new Padding(0, S(2), 0, S(4)) }, Array.IndexOf(new[] { "Item", "What to do", "When", "Time", "Only files" }, h));
            row++;
            Func<CleanItem, int> order = it => it.Drive != null ? 4 : it.Privacy ? 3 : (int)it.Tier;
            string group = null;
            foreach (var it in items.OrderBy(order).ThenBy(i => Lang.T(i.Name)))
            {
                string grp = it.Drive != null ? "Drive " + it.Drive : it.Privacy ? "Privacy" : it.Tier == Tier.Safe ? "Safe" : it.Tier == Tier.Review ? "Check first" : "Can't be undone";
                if (grp != group)
                {
                    group = grp;
                    g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    Color c = it.Privacy || it.Drive != null ? Theme.Ink : it.Tier == Tier.Safe ? SafeColor : it.Tier == Tier.Review ? ReviewColor : CautionColor;
                    string sign = it.Drive != null ? "\u25A3 " : it.Privacy ? "\u25CF " : it.Tier == Tier.Safe ? "\u2714 " : it.Tier == Tier.Review ? "\u26A0 " : "\u26D4 ";
                    var gh = new Label { AutoSize = true, Text = sign + Lang.T(grp), Font = new Font("Segoe UI Semibold", 10f), ForeColor = Theme.MapFore(c), Margin = new Padding(0, S(10), 0, S(4)) };
                    add(gh, 0); g.SetColumnSpan(gh, 6); row++;
                }
                g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                add(new Label { AutoSize = true, Text = Lang.T(it.Name), MaximumSize = new Size(S(320), 0), UseMnemonic = false, Margin = new Padding(0, S(6), S(8), S(4)) }, 0);
                MakeItemRow(g, row, it);
                row++;
            }
            return g;
        }

        void PolicyRefreshItems()
        {
            if (polItems.Count == 0) return;
            var org = Policy.Org();
            bool was = polFilling; polFilling = true;
            string[] times = Enumerable.Range(0, 48).Select(i => (i / 2).ToString("00") + ":" + (i % 2 == 0 ? "00" : "30")).ToArray();
            foreach (var r in polItems)
            {
                string k = Policy.ItemKey(r.Item.Name);
                bool byOrg = org.ContainsKey(k);
                var ir = ItemRule.Parse(byOrg ? org[k] : polCustom ? ItemOf(polEdit, k) : ItemRule.DefaultText);
                r.Do.SelectedIndex = Math.Max(0, Array.IndexOf(r.DoValues, ir.Do));
                r.When.SelectedIndex = Array.IndexOf(ItemRule.Whens, ir.When);
                r.Time.SelectedIndex = Math.Max(0, Array.IndexOf(times, ir.Time));
                r.Age.SelectedIndex = Math.Max(0, Array.IndexOf(r.AgeValues, ir.Age));
                bool edit = polCustom && !byOrg;
                r.Age.Enabled = edit && ir.Do != "never";
                r.Do.Enabled = edit;
                r.When.Enabled = r.Time.Enabled = edit && ir.Do == "auto";
                if (byOrg) { r.Mark.Text = "\uD83D\uDD12 " + Lang.T("Set by your organization"); r.Mark.ForeColor = Theme.MapFore(Color.FromArgb(150, 90, 0)); }
                else if (ir.ToString() != ItemRule.DefaultText) { r.Mark.Text = "\u25CF " + Lang.T("Changed"); r.Mark.ForeColor = Theme.MapFore(Theme.Accent); }
                else r.Mark.Text = "";
            }
            polFilling = was;
        }

        // for the self-test and previews
        public void PolicySetItem(string name, string rule) { var r = polItems.First(x => x.Item.Name == name); var ir = ItemRule.Parse(rule);
            r.Do.SelectedIndex = Array.IndexOf(r.DoValues, ir.Do); r.When.SelectedIndex = Array.IndexOf(ItemRule.Whens, ir.When);
            r.Time.SelectedIndex = Array.IndexOf(HalfHours, ir.Time); r.Age.SelectedIndex = Array.IndexOf(r.AgeValues, ir.Age); }
        public int PolicyFolderRows { get { return polFolderRows; } }
        public List<string> PolicyItemValues(string name) { return polItems.Where(x => x.Item.Name == name).Select(x => x.Do.SelectedIndex + "/" + x.When.SelectedIndex + "/" + x.Time.SelectedIndex + "/" + x.Age.SelectedIndex).ToList(); }
        public void PolicyShowFolders() { polTabs.SelectedIndex = 1; }
        public void PolicyScrollFolders(string itemName)
        {
            var r = polItems.FirstOrDefault(x => x.Item.Name == itemName && x.Do.Parent != null && x.Do.Parent.Parent != null && x.Do.Parent.Parent.Parent == polTabs.TabPages[1]);
            if (r != null) ((ScrollableControl)r.Do.Parent.Parent).ScrollControlIntoView(r.Do);
        }
        public int PolicyItemRows { get { return polItems.Count; } }
        public bool PolicyItemWhenEnabled(string name) { return polItems.First(x => x.Item.Name == name).When.Enabled; }
        public void PolicyShowItems() { polTabs.SelectedIndex = 2; }
        public List<string> CleanerRowTexts { get { return list.Items.Cast<ListViewItem>().Select(i => i.Text + (i.Checked ? " [x]" : "")).ToList(); } }
    }
}
