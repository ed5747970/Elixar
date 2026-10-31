// Dashboard > Watch: the folders that keep growing, how fast, what to do, and how often to look at them.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class WatchForm : Form
    {
        readonly float sc; readonly bool test;
        int S(int v) { return (int)Math.Round(v * sc); }
        readonly Dictionary<string, DashLoc> locs;
        readonly Action<string> openFolder;
        FlowLayoutPanel list; Label lHead;
        public static Func<WatchForm, DialogResult> TestHook;   // previews and the self-test
        public static Func<DateTime> TodayOverride;
        DateTime Today { get { return TodayOverride != null ? TodayOverride() : DateTime.Today; } }
        public List<Growth.Watch> Shown_ = new List<Growth.Watch>();
        public bool Changed;

        public WatchForm(bool testMode, IEnumerable<DashLoc> places, Action<string> open)
        {
            WindowFit.Watch(this);   // fits the screen (a tall window is made smaller; its page scrolls)
            test = testMode; openFolder = open;
            locs = (places ?? new DashLoc[0]).Where(p => !string.IsNullOrEmpty(p.Id)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
            using (var g = CreateGraphics()) sc = Math.Max(1f, g.DpiX / 96f);
            Text = Lang.T("Folders to watch");
            Font = new Font("Segoe UI", 9.5f); BackColor = Theme.Back; ForeColor = Theme.Ink;
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowInTaskbar = false; Icon = Brand.Load(32);
            Size = new Size(S(780), S(640)); MinimumSize = new Size(S(600), S(420));
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(S(16), S(12), S(16), S(8)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.Controls.Add(new Label { AutoSize = true, Text = Lang.T("Folders to watch"), Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, S(2)) }, 0, 0);
            t.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(720), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(6)),
                                       Text = Lang.T("Elixar writes down the size of each place on the Dashboard once a day (sizes only, never file names). These keep growing: look at them as often as shown, then click \"I checked it\" and Elixar reminds you when it's time again.") }, 0, 1);
            lHead = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 0, 0, S(8)) };
            t.Controls.Add(lHead, 0, 2);
            list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            t.Controls.Add(list, 0, 3);
            list.Resize += (o, e) => { foreach (Control c in list.Controls) c.Width = Math.Max(S(480), list.ClientSize.Width - S(24)); };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(S(12), S(10), S(12), S(10)), BackColor = Theme.Dark ? Color.FromArgb(36, 36, 44) : Theme.Panel };
            var bClose = new Button { AutoSize = true, Text = Lang.T("Close"), Padding = new Padding(S(6), S(2), S(6), S(2)), DialogResult = DialogResult.Cancel };
            CancelButton = bClose; bar.Controls.Add(bClose);
            Controls.Add(t); Controls.Add(bar);
            Shown += (o, e) => { Theme.Apply(this); Theme.DarkTitle(this); };
            Fill();
        }

        public static void ShowFor(IWin32Window owner, bool testMode, IEnumerable<DashLoc> places, Action<string> open, Action changed)
        {
            using (var f = new WatchForm(testMode, places, open))
            {
                if (TestHook != null) TestHook(f); else f.ShowDialog(owner);
                if (f.Changed && changed != null) changed();
            }
        }

        public void Fill()
        {
            Shown_ = Growth.Watchlist(Today);
            list.SuspendLayout();
            foreach (Control c in list.Controls.Cast<Control>().ToList()) { list.Controls.Remove(c); c.Dispose(); }
            if (!Growth.HasHistory(Today))
                lHead.Text = Lang.T("Collecting data: after two weeks of readings, the folders that keep growing show up here.");
            else if (Shown_.Count == 0)
                lHead.Text = Lang.T("No folder keeps growing. Elixar keeps watching.");
            else
            {
                int due = Shown_.Count(w => w.Due);
                lHead.Text = Lang.T(due == 0 ? "Nothing to look at today." : due == 1 ? "1 folder to look at now." : due + " folders to look at now.");
            }
            foreach (var w in Shown_) { var card = Card(w); list.Controls.Add(card); Theme.Apply(card); }
            list.ResumeLayout();
            lHead.ForeColor = Theme.MapFore(Shown_.Any(w => w.Due) ? Color.FromArgb(196, 120, 0) : Shown_.Count == 0 ? Color.FromArgb(0, 125, 70) : Theme.Ink);
        }

        Control Card(Growth.Watch w)
        {
            DashLoc loc; locs.TryGetValue(w.Id, out loc);
            Color tone = Theme.MapFore(w.Due ? Color.FromArgb(196, 120, 0) : Theme.Accent);
            var card = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Panel,
                                              Padding = new Padding(S(14), S(10), S(12), S(10)), Margin = new Padding(0, 0, 0, S(8)), Width = Math.Max(S(480), list.ClientSize.Width - S(24)) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), BackColor = Theme.Panel };
            int tw = S(520);
            text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(tw, 0), UseMnemonic = false, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Theme.Ink, Margin = new Padding(0, 0, 0, S(2)),
                                          Text = Lang.T(w.Name) + "  ·  " + Fs.Fmt(w.Now) });
            string how = (w.Steady ? Lang.T("Always grows: in " + w.GrewWeeks + " of the last " + w.Weeks + " weeks") : Lang.T("Grows fast")) +
                         "  ·  " + Lang.T("+" + Fs.Fmt(Math.Max(0, w.PerMonth)) + " a month");
            text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(tw, 0), UseMnemonic = false, ForeColor = tone, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, 0, 0, S(3)), Text = how });
            if (loc != null && !string.IsNullOrEmpty(loc.Action))
                text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(tw, 0), UseMnemonic = false, ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(3)), Text = Lang.T(loc.Action) });
            string when = Lang.T("Check it " + Growth.EveryText(w.EveryDays)) + "  ·  " +
                          (w.Checked.HasValue ? Lang.T("last checked " + w.Checked.Value.ToString(Lang.On ? "d.M" : "d MMM")) + "  ·  " : "") +
                          (w.Due ? Lang.T("due now") : Lang.T("next on " + w.NextCheck.ToString(Lang.On ? "d.M" : "d MMM")));
            text.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(tw, 0), UseMnemonic = false, ForeColor = Theme.Ink, Margin = new Padding(0), Text = when });
            card.Controls.Add(text, 0, 0);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(S(8), 0, 0, 0), BackColor = Theme.Panel };
            var bLook = new Button { AutoSize = true, Text = Lang.T("Show what's inside"), Margin = new Padding(0, 0, 0, S(6)), Padding = new Padding(S(4), S(1), S(4), S(1)), Enabled = loc != null && openFolder != null };
            var bDone = new Button { AutoSize = true, Text = Lang.T("I checked it"), Margin = new Padding(0), Padding = new Padding(S(4), S(1), S(4), S(1)), Enabled = !test };
            bLook.Click += (o, e) => { if (loc != null && openFolder != null) openFolder(loc.Path); };
            bDone.Click += (o, e) => { Growth.MarkChecked(w.Id, Today); Changed = true; Fill(); };
            buttons.Controls.Add(bLook); buttons.Controls.Add(bDone);
            card.Controls.Add(buttons, 1, 0);
            card.Paint += (o, e) =>
            {
                using (var p = new Pen(Theme.Grid)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
                using (var br = new SolidBrush(tone)) e.Graphics.FillRectangle(br, Lang.Rtl ? card.Width - S(4) : 0, 0, S(4), card.Height);
            };
            return card;
        }
    }
}
