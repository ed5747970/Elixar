// Cleaner > Sites to keep (cookies): choose the sites whose cookies the "Cookies" items keep (CookieKeep).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class CookieKeepForm : Form
    {
        readonly ListView list; readonly TextBox search; readonly Label status; readonly Button btnSave;
        List<CookieSite> sites = new List<CookieSite>();
        readonly HashSet<string> ticked;
        bool filling;
        public event Action Loaded;   // self-test
        public bool SavedForTest, LoadedForTest;

        public CookieKeepForm(float scale)
        {
            WindowFit.Watch(this);
            Func<int, int> S = px => (int)Math.Round(px * scale);
            Text = Lang.T("Sites to keep"); Icon = Brand.Load(32); StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; Font = new Font("Segoe UI", 9.5f); Size = new Size(S(760), S(560)); MinimumSize = new Size(S(520), S(380)); Padding = new Padding(S(12));
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 100 : 0));
            var note = new Label { AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(S(720), 0), Margin = new Padding(0, 0, 0, S(8)), UseMnemonic = false,
                                   Text = Lang.T("When you clean the Cookies items, the cookies of these sites stay, so you stay signed in there. Every other site's cookies go (trackers, sites you visited once). " +
                                                 "Ticked first: sites where you look signed in. The list works for every browser.") };
            status = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, S(6)), Text = Lang.T("Reading the browsers' cookies...") };
            search = new TextBox { Width = S(300), Margin = new Padding(0, 0, 0, S(6)) };
            list = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            list.Columns.Add(Lang.T("Site"), S(240)); list.Columns.Add(Lang.T("Browsers"), S(170)); list.Columns.Add(Lang.T("Cookies"), S(70), HorizontalAlignment.Right); list.Columns.Add(Lang.T("Why suggested"), S(160));
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, S(8), 0, 0) };
            var btnNone = new Button { AutoSize = true, Text = Lang.T("Untick all") };
            btnSave = new Button { AutoSize = true, Text = Lang.T("Save"), Font = new Font("Segoe UI Semibold", 9.5f), Enabled = false };
            var btnCancel = new Button { AutoSize = true, Text = Lang.T("Cancel"), DialogResult = DialogResult.Cancel };
            row.Controls.AddRange(new Control[] { btnSave, btnNone, btnCancel });
            t.Controls.Add(note, 0, 0); t.Controls.Add(status, 0, 1); t.Controls.Add(search, 0, 2); t.Controls.Add(list, 0, 3); t.Controls.Add(row, 0, 4);
            Controls.Add(t); CancelButton = btnCancel;

            var saved = CookieKeep.Load();
            ticked = new HashSet<string>(saved);
            bool suggest = saved.Count == 0;
            search.TextChanged += (o, e) => Fill();
            list.ItemChecked += (o, e) =>
            {
                if (filling) return;
                string s = (string)e.Item.Tag;
                if (e.Item.Checked) ticked.Add(s); else ticked.Remove(s);
                Count();
            };
            btnNone.Click += (o, e) => { ticked.Clear(); Fill(); };
            btnSave.Click += (o, e) =>
            {
                try { CookieKeep.Save(ticked); SavedForTest = true; DialogResult = DialogResult.OK; Close(); }
                catch (Exception ex) { status.Text = Lang.T("Not saved: ") + ex.Message; }
            };
            Shown += (o, e) =>
            {
                Task.Factory.StartNew(() => { List<string> problems; var l = CookieKeep.Sites(out problems); return Tuple.Create(l, problems); }).ContinueWith(r => BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    if (r.IsFaulted) { status.Text = r.Exception.GetBaseException().Message; return; }
                    sites = r.Result.Item1;
                    if (suggest) foreach (var s in sites.Where(x => x.SignedIn && !x.Tracker)) ticked.Add(s.Site);
                    btnSave.Enabled = true;
                    Fill();
                    if (r.Result.Item2.Count > 0) status.Text += "  " + Lang.T("Not read: ") + string.Join("; ", r.Result.Item2) + " " + Lang.T("(close that browser and open this again)");
                    LoadedForTest = true;
                    if (Loaded != null) Loaded();
                })));
            };
        }

        void Fill()
        {
            filling = true;
            list.BeginUpdate(); list.Items.Clear();
            string q = (search.Text ?? "").Trim().ToLowerInvariant();
            // the ones you keep that have no cookies right now still show, so they can be unticked
            var all = sites.Concat(ticked.Where(k => !sites.Any(s => s.Site == k)).Select(k => new CookieSite { Site = k })).ToList();
            foreach (var s in all.Where(x => q.Length == 0 || x.Site.Contains(q)))
            {
                var r = new ListViewItem(s.Site) { Tag = s.Site, Checked = ticked.Contains(s.Site) };
                r.SubItems.Add(string.Join(", ", s.Browsers.OrderBy(b => b)));
                r.SubItems.Add(s.Count.ToString());
                r.SubItems.Add(Lang.T(s.Tracker ? "tracker" : s.SignedIn ? "signed in" : s.Count == 0 ? "on your list" : ""));
                if (s.Tracker) r.ForeColor = Color.Gray;
                list.Items.Add(r);
            }
            list.EndUpdate();
            filling = false;
            Count();
        }

        void Count()
        {
            status.Text = Lang.Hebrew ? sites.Count + " אתרים עם עוגיות; " + ticked.Count + " יישמרו." : sites.Count + " sites have cookies; " + ticked.Count + " will be kept.";
            btnSave.Text = Lang.Hebrew ? "שמירה (" + ticked.Count + ")" : "Save (" + ticked.Count + (ticked.Count == 1 ? " site)" : " sites)");
        }

        // self-test
        public int RowsForTest { get { return list.Items.Count; } }
        public List<string> TickedForTest { get { return ticked.OrderBy(x => x).ToList(); } }
        public void SaveForTest() { btnSave.PerformClick(); }
    }
}
