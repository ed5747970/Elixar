// Tools > Sysprep > Image cleanup: removes what a golden image doesn't need, just before Sysprep.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class ImageCleanForm : Form
    {
        readonly float sc; readonly bool test;
        int S(int v) { return (int)Math.Round(v * sc); }
        readonly List<CleanItem> items;
        ListView list; Label lInfo, lTotal; TextBox tLog; Button bClean, bClose;
        bool busy;
        public Action<string> Log;
        public static Func<ImageCleanForm, DialogResult> TestHook;   // previews and the self-test
        public static Func<CleanItem, long> MeasureOverride;         // previews
        public List<CleanItem> Items { get { return items; } }
        public string TotalText { get { return lTotal.Text; } }

        public ImageCleanForm(bool testMode)
        {
            test = testMode;
            using (var g = CreateGraphics()) sc = Math.Max(1f, g.DpiX / 96f);
            Text = Lang.T("Image cleanup");
            Font = new Font("Segoe UI", 9.5f); BackColor = Theme.Back; ForeColor = Theme.Ink;
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; ShowInTaskbar = false; Icon = Brand.Load(32);
            Size = new Size(S(860), S(680)); MinimumSize = new Size(S(700), S(520));
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }

            items = GoldenImage.ImageItems();
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(S(16), S(12), S(16), S(8)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 62)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
            t.Controls.Add(new Label { AutoSize = true, Text = Lang.T("Image cleanup"), Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, S(2)) }, 0, 0);
            t.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(800), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(8)),
                                       Text = Lang.T("Removes what a golden image doesn't need, so it is smaller and every PC starts clean. Run it last, just before Sysprep: it also deletes restore points. Nothing here can be undone.") }, 0, 1);
            list = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false, MultiSelect = false };
            list.Columns.Add(Lang.T("What"), S(400)); list.Columns.Add(Lang.T("Size"), S(110), HorizontalAlignment.Right); list.Columns.Add(Lang.T("Kind"), S(130));
            foreach (var it in items)
            {
                string kind = it.Tier == Tier.Safe ? "Safe" : it.Tier == Tier.Review ? "Check first" : "Can't be undone";
                var r = new ListViewItem(new[] { Lang.T(it.Name), Lang.T("measuring..."), Lang.T(kind) }) { Checked = it.DefaultOn, Tag = it, UseItemStyleForSubItems = false };
                list.Items.Add(r);
            }
            t.Controls.Add(list, 0, 2);
            lInfo = new Label { AutoSize = true, MaximumSize = new Size(S(800), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, S(6), 0, S(4)), UseMnemonic = false, Text = Lang.T("Select a line to see what it does.") };
            t.Controls.Add(lInfo, 0, 3);
            lTotal = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, S(2), 0, S(6)) };
            t.Controls.Add(lTotal, 0, 4);
            tLog = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), RightToLeft = RightToLeft.No };
            t.Controls.Add(tLog, 0, 5);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(S(12), S(10), S(12), S(10)), BackColor = Theme.Dark ? Color.FromArgb(36, 36, 44) : Theme.Panel };
            bClean = new Button { AutoSize = true, Text = Lang.T("Clean selected..."), Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, 0, S(8), 0), Padding = new Padding(S(6), S(2), S(6), S(2)), Enabled = false };
            bClose = new Button { AutoSize = true, Text = Lang.T("Close"), Margin = new Padding(0, 0, S(8), 0), Padding = new Padding(S(6), S(2), S(6), S(2)), DialogResult = DialogResult.Cancel };
            CancelButton = bClose;
            bar.Controls.AddRange(new Control[] { bClean, bClose });
            Controls.Add(t); Controls.Add(bar);

            list.SelectedIndexChanged += (o, e) =>
            {
                if (list.SelectedItems.Count == 0) return;
                var it = (CleanItem)list.SelectedItems[0].Tag;
                lInfo.Text = Lang.T(it.Info ?? "") + (string.IsNullOrEmpty(it.Where) ? "" : "\n" + it.Where);
            };
            list.ItemChecked += (o, e) => Total();
            bClean.Click += (o, e) => Clean();
            FormClosing += (o, e) => { if (busy) e.Cancel = true; };
            Shown += (o, e) => { Theme.Apply(this); Theme.DarkTitle(this); Measure(); };
        }

        public static void ShowFor(IWin32Window owner, bool testMode, Action<string> log)
        {
            if (Policy.Blocked(owner, "sysprep")) return;   // turned off (policy or company PC)
            using (var f = new ImageCleanForm(testMode) { Log = log })
            {
                if (TestHook != null) { TestHook(f); return; }
                f.ShowDialog(owner);
            }
        }

        void UI(Action a) { if (IsDisposed || !IsHandleCreated) return; try { BeginInvoke(a); } catch { } }
        void Write(string m) { UI(() => { tLog.AppendText(m + Environment.NewLine); }); if (Log != null) Log(m.Trim()); }

        public void Measure()
        {
            busy = true; bClean.Enabled = false;
            lTotal.Text = Lang.T("Measuring...");
            Task.Factory.StartNew(() =>
            {
                foreach (var it in items)
                {
                    long n;
                    try { n = MeasureOverride != null ? MeasureOverride(it) : it.Measure(); } catch { n = 0; }
                    it.SizeUnknown = n < 0; it.Size = Math.Max(0, n);
                    var cur = it;
                    UI(() =>
                    {
                        foreach (ListViewItem r in list.Items)
                            if (r.Tag == cur)
                            {
                                bool nothing = !cur.SizeUnknown && cur.Size == 0;
                                r.SubItems[1].Text = cur.SizeUnknown ? Lang.T("after it runs") : nothing ? Lang.T("nothing to clean") : Fs.Fmt(cur.Size);
                                if (nothing) { r.Checked = false; r.ForeColor = Theme.Quiet; r.SubItems[1].ForeColor = Theme.Quiet; r.SubItems[2].ForeColor = Theme.Quiet; }
                            }
                    });
                }
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() => { busy = false; bClean.Enabled = !test; Total(); }));
        }

        void Total()
        {
            var sel = Selected();
            long sum = sel.Sum(i => i.Size);
            lTotal.Text = Lang.T(sel.Count == 0 ? "Nothing selected." : "Selected: " + sel.Count + " - about " + Fs.Fmt(sum) + (sel.Any(i => i.SizeUnknown) ? " + more" : ""));
        }

        List<CleanItem> Selected() { return list.Items.Cast<ListViewItem>().Where(r => r.Checked).Select(r => (CleanItem)r.Tag).ToList(); }

        void Clean()
        {
            if (test || busy) return;
            var sel = Selected();
            if (sel.Count == 0) return;
            string ask = Lang.T("Clean these for the image?") + "\n\n" + string.Join("\n", sel.Select(i => "  - " + Lang.T(i.Name) + (i.SizeUnknown ? "" : "  (" + Fs.Fmt(i.Size) + ")"))) + "\n\n" +
                         Lang.T("This can't be undone. Close your other programs first.");
            if (Lang.BoxNo(this, ask, "Elixar") != DialogResult.Yes) return;
            busy = true; bClean.Enabled = false; list.Enabled = false; bClose.Enabled = false;
            // restore points last: everything before it can still be undone with one
            var order = sel.Where(i => i.Name != "Restore points").Concat(sel.Where(i => i.Name == "Restore points")).ToList();
            Task.Factory.StartNew(() => ChangeLog.Within("Sysprep", () =>
            {
                Write(Lang.T("Image cleanup started."));
                foreach (var it in order)
                {
                    Write(Lang.T(it.Name) + "...");
                    string problem = null;
                    try { it.Clean(m => Write(m)); } catch (Exception e) { Write("  " + Lang.T("Skipped: ") + e.Message); problem = e.Message; }
                    ChangeLog.Cleaned(it, 0, problem, "Golden image cleanup");
                }
                Write(Lang.T("Image cleanup finished."));
            }), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                busy = false; list.Enabled = true; bClose.Enabled = true;
                Measure();
            }));
        }
    }
}
