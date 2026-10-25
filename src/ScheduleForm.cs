// Elixar for Private: when each automatic action runs (in Elixar for Business, Policy manager sets the times).
// The times are kept with the policy, so Task Scheduler, Automation and email reports all read the same ones.
// A time an organization set by Group Policy is shown locked.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        public static Func<Form, DialogResult> ScheduleHook;   // self-test and previews

        public void ChangeTimesForTest() { ChangeTimes(); }
        void ChangeTimes()
        {
            if (Edition.Business) { ShowPolicy(); return; }
            var keys = Policy.Rules.Where(r => Policy.IsSchedule(r.Key)).ToList();
            var org = Policy.Org();
            using (var f = new Form { Text = Lang.T("When things run"), Icon = Icon, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                      StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Font = Font, Padding = new Padding(S(14)) })
            {
                var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
                t.Controls.Add(new Label { Text = Lang.T("When each automatic action runs. Elixar updates Windows' Task Scheduler right away."), AutoSize = true, MaximumSize = new Size(S(480), 0), Margin = new Padding(0, 0, 0, S(10)) }, 0, 0);
                t.SetColumnSpan(t.GetControlFromPosition(0, 0), 2);
                var combos = new Dictionary<string, ComboBox>();
                int row = 1;
                foreach (var r in keys)
                {
                    var lbl = new Label { Text = Lang.T(r.Name), AutoSize = true, Margin = new Padding(0, S(7), S(12), 0) };
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(220) };
                    foreach (var v in r.Values) cb.Items.Add(Lang.T(r.Label(v)));
                    cb.SelectedIndex = Math.Max(0, Array.IndexOf(r.Values, Policy.Get(r.Key)));
                    bool locked = org.ContainsKey(r.Key);
                    cb.Enabled = !locked;
                    if (locked) lbl.Text += "  🔒";
                    new ToolTip().SetToolTip(cb, Lang.T(locked ? "Set by your organization" : r.Help));
                    t.Controls.Add(lbl, 0, row); t.Controls.Add(cb, 1, row); combos[r.Key] = cb; row++;
                }
                var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(12), 0, 0) };
                var ok = MakeButton("Save"); ok.DialogResult = DialogResult.OK;
                var cancel = MakeButton("Cancel"); cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.AddRange(new Control[] { cancel, ok });
                t.Controls.Add(buttons, 0, row); t.SetColumnSpan(buttons, 2);
                f.Controls.Add(t); f.AcceptButton = ok; f.CancelButton = cancel;
                f.Shown += (o, e) => { Theme.Apply(f); Lang.Apply(f); };
                var res = ScheduleHook != null ? ScheduleHook(f) : f.ShowDialog(this);
                if (res != DialogResult.OK) return;
                var custom = new Dictionary<string, string>(Policy.Custom);
                foreach (var r in keys) if (!org.ContainsKey(r.Key)) custom[r.Key] = r.Values[Math.Max(0, combos[r.Key].SelectedIndex)];
                var before = keys.Select(r => Policy.Get(r.Key)).ToList();
                string err = Policy.Save(true, custom);
                if (err != null) { Lang.Box(this, err, "Elixar"); return; }
                if (before.SequenceEqual(keys.Select(r => Policy.Get(r.Key)))) return;
                Log(Lang.T("Schedule saved."));
                ShowScheduleText(); ShowMailWhen(); PolicyApplySchedules();
            }
        }
    }
}
