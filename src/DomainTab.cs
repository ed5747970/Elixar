// Elixar for Business: the Domain PCs tab. This PC is the Cure Server: every PC's Cure Agent reports here, and this tab shows
// them all - the ones at risk first. Pick a PC by its name to see its disks, what fills C:, what can be removed and what to do.
// It only reads the reports (Cure stage 1): nothing is started on another PC from here.

#if BUSINESS
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    // a small card: caption, big value, a note under it
    public class KpiCard : Control
    {
        public string Cap = "", Val = "", Note = "";
        public Color ValColor = Color.Empty;
        public float Scale_ = 1f;
        public KpiCard() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(r, (int)(10 * Scale_)))
            {
                using (var b = new SolidBrush(Theme.Panel)) g.FillPath(b, path);
                using (var p = new Pen(Theme.Grid)) g.DrawPath(p, path);
            }
            int x = (int)(12 * Scale_), y = (int)(9 * Scale_);
            using (var f = new Font("Segoe UI", 8f)) TextRenderer.DrawText(g, Cap.ToUpperInvariant(), f, new Point(x, y), Theme.Quiet);
            using (var f = new Font("Segoe UI Semibold", 17f)) TextRenderer.DrawText(g, Val, f, new Point(x - 2, y + (int)(15 * Scale_)), ValColor.IsEmpty ? Theme.Ink : ValColor);
            using (var f = new Font("Segoe UI", 8.5f))
                TextRenderer.DrawText(g, Note, f, new Rectangle(x, y + (int)(48 * Scale_), Width - 2 * x, Height - y - (int)(48 * Scale_)), Theme.Quiet, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }
        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    // what fills C:, as one bar and a legend
    public class FillBar : Control
    {
        public List<Tuple<string, long, Color>> Parts = new List<Tuple<string, long, Color>>();
        public long Total;
        public float Scale_ = 1f;
        public FillBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Parent != null ? Parent.BackColor : Theme.Panel);
            int h = (int)(14 * Scale_), w = Width - 2;
            if (Total <= 0) { TextRenderer.DrawText(g, Lang.T("Measured a few hours after Cure Agent starts."), Font, new Point(0, 0), Theme.Quiet); return; }
            float x = 0;
            using (var bg = new SolidBrush(Theme.Grid)) g.FillRectangle(bg, 0, 0, w, h);
            foreach (var p in Parts)
            {
                float pw = Math.Max(0, (float)(w * (double)p.Item2 / Total));
                using (var b = new SolidBrush(p.Item3)) g.FillRectangle(b, x, 0, pw, h);
                x += pw;
            }
            int lx = 0, ly = h + (int)(8 * Scale_);
            foreach (var p in Parts)
            {
                string t = p.Item1 + "  " + Fs.Fmt(Math.Max(0, p.Item2));
                var sz = TextRenderer.MeasureText(g, t, Font);
                int need = (int)(16 * Scale_) + sz.Width + (int)(18 * Scale_);
                if (lx > 0 && lx + need > Width) { lx = 0; ly += sz.Height + (int)(4 * Scale_); }
                using (var b = new SolidBrush(p.Item3)) g.FillEllipse(b, lx, ly + sz.Height / 2 - (int)(5 * Scale_), (int)(10 * Scale_), (int)(10 * Scale_));
                TextRenderer.DrawText(g, t, Font, new Point(lx + (int)(14 * Scale_), ly), Theme.Ink);
                lx += need;
            }
        }
    }

    public partial class MainForm
    {
        TabPage pageDomain;
        Panel domSetup, domMain, domDetail;
        FlowLayoutPanel domKpis, domPcKpis;
        ListBox domList;
        TextBox domFind;
        ComboBox domFilter;
        Label domStatus, domName, domState, domSub, domBanner, domStartup;
        FillBar domFill;
        TableLayoutPanel domTips;
        ListView domItems, domPlaces, domApps, domDisks;
        Button domRefresh, domWeb, domRemove, domSetupBtn;
        TextBox domSetupOut, domGroup;
        NumericUpDown domPort;
        Timer domTimer;
        public static string DomainDataOverride;
        static Color Mix(Color a, Color b, double t) { return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t)); }   // self-test and previews: a Cure Server data folder

        Button domAlerts;
        public bool AlertsButtonShown { get { return domAlerts != null; } }
        public class DomPc { public string Id, Name, Seen; public CureReport R; public CureRisk Risk; public List<long[]> History; }
        List<DomPc> domPcs = new List<DomPc>();
        string domPicked, domBannerText = "";
        bool domSetupOn;

        static Color RiskColor(CureRisk k)
        {
            return Theme.MapFore(k >= CureRisk.Full && k != CureRisk.Silent ? CriticalColor : k >= CureRisk.Watch && k != CureRisk.Silent ? CautionColor : k == CureRisk.Old ? CautionColor : k == CureRisk.Silent ? Color.Gray : SafeColor);
        }

        void BuildDomainTab()
        {
            pageDomain = new TabPage("Domain PCs") { BackColor = Color.FromArgb(246, 248, 251) };
            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(16), S(12), S(16), S(10)) };
            pageDomain.Controls.Add(root);

            // ---- no Cure Server on this PC yet ----
            domSetup = new Panel { Dock = DockStyle.Fill, Visible = false, AutoScroll = true };
            var st = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top };
            st.Controls.Add(Title("Domain PCs"));
            st.Controls.Add(Note("Every PC with Cure Agent sends its status to a Cure Server, and this tab shows them all: which PC is running out of space, how its disks are, " +
                                 "and what can be cleaned or removed there. Make this PC the Cure Server (a Windows server or an IT PC that is always on). Reports only: nothing can be started on another PC from here."));
            var r1 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(4), 0, S(4)) };
            domPort = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = CureServer.DefaultPort, Width = S(80) };
            domGroup = new TextBox { Width = S(300), Text = "" };
            Func<string, Label> lbl = t => new Label { AutoSize = true, Text = t, Margin = new Padding(0, S(6), S(6), 0) };
            r1.Controls.AddRange(new Control[] { lbl("Port"), domPort });
            st.Controls.Add(r1);
            // on its own line: next to the port it went past the right edge of a narrow window
            var r1b = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, S(4)) };
            r1b.Controls.AddRange(new Control[] { lbl("Who may see the web console (a domain group, optional)"), domGroup });
            st.Controls.Add(r1b);
            domSetupBtn = MakeButton("Make this PC the Cure Server...");
            domSetupBtn.Font = new Font("Segoe UI Semibold", 9.5f);
            domSetupBtn.Click += (o, e) => DomainSetup();
            st.Controls.Add(domSetupBtn);
            domSetupOut = new TextBox { Multiline = true, ReadOnly = true, Width = S(860), Height = S(180), Visible = false, Font = new Font("Consolas", 9.5f), ScrollBars = ScrollBars.Vertical, Margin = new Padding(0, S(10), 0, 0) };
            st.Controls.Add(domSetupOut);
            st.Controls.Add(Note("Then on every PC: Group Policy (or Intune) sets the Cure Agent settings the server saves for you, and the Elixar for Business MSI installs Cure Agent " +
                                 "(msiexec /i Elixar-Business.msi /qn CUREAGENT=1). The User Guide > Cure has every step."));
            domSetup.Controls.Add(st);
            root.Controls.Add(domSetup);

            // ---- the PCs ----
            domMain = new Panel { Dock = DockStyle.Fill };
            var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };   // the buttons go to a second line in a narrow window
            head.Controls.Add(Title("Domain PCs"));
            domStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(14), S(10), S(10), 0) };
            domRefresh = MakeButton("Refresh"); domRefresh.Click += (o, e) => DomainLoad();
            domWeb = MakeButton("Open the web console"); domWeb.Click += (o, e) => { try { Shell.Open("https://localhost:" + DomainPort() + "/"); } catch { } };
            var exp = MakeButton("Export (CSV)..."); exp.Click += (o, e) => DomainExport();
            domAlerts = MakeButton("Email alerts..."); domAlerts.Click += (o, e) => OpenAlerts();
            head.Controls.AddRange(new Control[] { domStatus, domRefresh, domWeb, exp, domAlerts });
            domKpis = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MinimumSize = new Size(0, S(98)), WrapContents = true, Padding = new Padding(0, S(4), 0, S(4)) };   // the tiles wrap (they were cut off)

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = S(8), Orientation = Orientation.Vertical };
            split.HandleCreated += (o, e) => { try { split.SplitterDistance = S(300); } catch { } };
            // left: the PCs by name, the ones at risk first
            var left = split.Panel1; left.Padding = new Padding(0, 0, S(4), 0);
            domFind = new TextBox { Dock = DockStyle.Top };
            domFind.HandleCreated += (o, e) => { if (Win.IsWindows) try { SendMessage(domFind.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, Lang.T("Find a PC by name...")); } catch { } };
            domFind.TextChanged += (o, e) => DomainFillList();
            domFilter = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            domFilter.Items.AddRange(new object[] { "All PCs", "At risk (space or disk)", "Almost full", "Disk warnings", "Not seen for 7+ days" });
            domFilter.SelectedIndex = 0; domFilter.SelectedIndexChanged += (o, e) => DomainFillList();
            domList = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(46), IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
            domList.DrawItem += DomainDrawItem;
            domList.SelectedIndexChanged += (o, e) => { var p = domList.SelectedItem as DomPc; if (p != null && p.Id != domPicked) { domPicked = p.Id; DomainShow(p); } };
            left.Controls.Add(domList); left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(6) }); left.Controls.Add(domFilter); left.Controls.Add(new Panel { Dock = DockStyle.Top, Height = S(4) }); left.Controls.Add(domFind);

            // right: one PC, like Elixar's own Dashboard
            domDetail = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(10), 0, S(6), S(10)) };
            var col = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            Action<Control> add = c => { col.RowStyles.Add(new RowStyle(SizeType.AutoSize)); col.Controls.Add(c, 0, col.RowCount++); };
            var nameRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            domName = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 17f), UseMnemonic = false };
            domState = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(S(10), S(12), 0, 0), UseMnemonic = false };
            nameRow.Controls.AddRange(new Control[] { domName, domState });
            add(nameRow);
            domSub = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(S(1000), 0), UseMnemonic = false, Margin = new Padding(S(2), 0, 0, S(6)) };
            add(domSub);
            domBanner = new Label { AutoSize = true, Padding = new Padding(S(10), S(7), S(10), S(7)), Font = new Font("Segoe UI Semibold", 10f), MaximumSize = new Size(S(1000), 0), UseMnemonic = false, Margin = new Padding(0, 0, 0, S(8)) };
            add(domBanner);
            domPcKpis = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, S(6)) };
            add(domPcKpis);
            add(Head2("What fills C:"));
            domFill = new FillBar { Height = S(70), Width = S(900), Scale_ = s, Font = new Font("Segoe UI", 9f) };
            add(domFill);
            add(Head2("What to do on this PC"));
            domTips = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, S(6)) };
            add(domTips);
            add(Head2("What can be removed (the Cleaner's items, all accounts together)"));
            domItems = DomList(new[] { "Item", "Level", "Size", "Weekly clean" }, new[] { 330, 130, 100, 110 }); add(domItems);
            add(Head2("What fills C: in detail"));
            domPlaces = DomList(new[] { "Place", "Level", "Size", "What to do" }, new[] { 280, 120, 100, 420 }); add(domPlaces);
            add(Head2("Biggest apps"));
            domApps = DomList(new[] { "App", "Publisher", "Size", "Last used" }, new[] { 300, 220, 100, 120 }); add(domApps);
            add(Head2("Disks and drives"));
            domDisks = DomList(new[] { "Disk / drive", "Type", "Health", "Details" }, new[] { 300, 120, 120, 340 }); add(domDisks);
            add(Head2("Starts with Windows"));
            domStartup = new Label { AutoSize = true, MaximumSize = new Size(S(950), 0), UseMnemonic = false, ForeColor = Color.DimGray, Margin = new Padding(S(2), 0, 0, S(10)) };
            add(domStartup);
            domRemove = MakeButton("Remove this PC from the list...");
            domRemove.Click += (o, e) => DomainRemove();
            add(domRemove);
            domDetail.Controls.Add(col);
            // the bar and the lists are as wide as the column (they were at least 500 wide, so in a narrow window their right side was cut off)
            col.SizeChanged += (o, e) => { int w = Math.Max(S(260), col.ClientSize.Width - S(8)); if (domFill.Width != w) domFill.Width = w; foreach (var lv in new[] { domItems, domPlaces, domApps, domDisks }) if (lv.Width != w) lv.Width = w; };
            split.Panel2.Controls.Add(domDetail);

            domMain.Controls.Add(split); domMain.Controls.Add(domKpis); domMain.Controls.Add(head);
            root.Controls.Add(domMain);

            // refresh every minute while the tab is open; the timer goes with the window
            domTimer = new Timer { Interval = 60000 };
            domTimer.Tick += (o, e) => { if (IsDisposed || Disposing) { domTimer.Stop(); return; } if (tabs.SelectedTab == pageDomain && domMain.Visible) DomainLoad(); };
            Disposed += (o, e) => { domTimer.Stop(); domTimer.Dispose(); };
            domTimer.Start();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        Label Head2(string t) { return new Label { AutoSize = true, Text = t, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Theme.MapFore(Theme.Accent), Margin = new Padding(0, S(10), 0, S(4)) }; }

        ListView DomList(string[] cols, int[] widths)
        {
            var lv = new ListView { View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Width = S(900), Height = S(60), Margin = new Padding(0, 0, 0, S(4)) };
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], S(widths[i]), cols[i] == "Size" ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            return lv;
        }

        void DomFit(ListView lv)
        {
            int rows = Math.Max(1, Math.Min(lv.Items.Count, 14));
            lv.Height = S(28) + rows * S(21);
        }

        string DomainDataDir { get { return DomainDataOverride ?? CureServer.DefaultDataDir; } }
        int DomainPort() { try { return CureServer.FromRegistry().Port; } catch { return CureServer.DefaultPort; } }

        void OnDomainTab()
        {
            bool here = DomainDataOverride != null || CureServer.InstalledHere();
            domSetup.Visible = !here; domMain.Visible = here; domSetupOn = !here;
            if (here) DomainLoad();
        }

        // read every PC's latest report (the service keeps writing them; this only reads)
        public void DomainLoad()
        {
            var srv = new CureServer { DataDir = DomainDataDir };
            var list = new List<DomPc>();
            try
            {
                var devs = srv.Devices().Where(d => !d.Revoked).ToList();
                foreach (var d in devs)
                {
                    var r = srv.Latest(d.Id);
                    list.Add(new DomPc { Id = d.Id, Name = d.Computer, Seen = d.LastSeen, R = r, History = srv.History(d.Id) });
                }
            }
            catch { }
            string newest = list.Where(p => p.R != null).Select(p => { Version v; return Version.TryParse(p.R.Elixar, out v) ? v : null; }).Where(v => v != null).DefaultIfEmpty(new Version(0, 0)).Max().ToString();
            foreach (var p in list)
            {
                DateTime seen; bool ok = DateTime.TryParse(p.Seen ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out seen);
                p.Risk = CureAdvice.Risk(p.R, ok ? seen : DateTime.MinValue, newest, p.History);
            }
            domPcs = list.OrderByDescending(p => (int)p.Risk == 7 ? 3 : (int)p.Risk).ThenBy(p => CureAdvice.FreePercent(p.R) < 0 ? 999 : CureAdvice.FreePercent(p.R)).ThenBy(p => p.Name).ToList();
            domStatus.Text = Lang.T(domPcs.Count == 1 ? "1 PC" : domPcs.Count + " PCs") + "  ·  " + Lang.T("updated") + " " + DateTime.Now.ToString("HH:mm");
            DomainKpis();
            DomainFillList();
        }

        void DomainKpis()
        {
            domKpis.Controls.Clear();
            Action<string, string, string, Color> k = (cap, val, note, c) => domKpis.Controls.Add(new KpiCard { Cap = Lang.T(cap), Val = val, Note = Lang.T(note), ValColor = c, Scale_ = s, Size = new Size(S(190), S(88)), Margin = new Padding(0, 0, S(10), 0) });
            int seen = domPcs.Count(p => p.Risk != CureRisk.Silent), full = domPcs.Count(p => p.Risk == CureRisk.Full), low = domPcs.Count(p => p.Risk == CureRisk.Low);
            int disk = domPcs.Count(p => p.Risk == CureRisk.Disk || p.Risk == CureRisk.Worn);
            long free = domPcs.Where(p => p.R != null).Sum(p => CureAdvice.CouldFree(p.R)), saved = domPcs.Where(p => p.R != null).Sum(p => Math.Max(0, p.R.Saved));
            k("PCs reporting", seen + " / " + domPcs.Count, (domPcs.Count - seen) + " not seen for 7+ days", Color.Empty);
            k("At risk: no space", full.ToString(), low + " more getting full", full > 0 ? Theme.MapFore(CriticalColor) : Theme.MapFore(SafeColor));
            k("Disk warnings", disk.ToString(), "failing or worn", disk > 0 ? Theme.MapFore(CriticalColor) : Theme.MapFore(SafeColor));
            k("Can be freed", Fs.Fmt(free), "Safe items and big leftovers", Color.Empty);
            k("Freed so far", Fs.Fmt(saved), "by Elixar on these PCs", Theme.MapFore(SafeColor));
        }

        void DomainFillList()
        {
            if (domList == null) return;
            string q = (domFind.Text ?? "").Trim();
            int f = domFilter.SelectedIndex;
            var shown = domPcs.Where(p => (q.Length == 0 || p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) &&
                                          (f == 0 || f == 1 && p.Risk >= CureRisk.Low && p.Risk != CureRisk.Silent || f == 2 && p.Risk == CureRisk.Full ||
                                           f == 3 && (p.Risk == CureRisk.Disk || p.Risk == CureRisk.Worn) || f == 4 && p.Risk == CureRisk.Silent)).ToList();
            domList.BeginUpdate();
            domList.Items.Clear();
            foreach (var p in shown) domList.Items.Add(p);
            domList.EndUpdate();
            var pick = shown.FirstOrDefault(p => p.Id == domPicked) ?? shown.FirstOrDefault();
            domPicked = null;
            if (pick != null) domList.SelectedItem = pick;
            else { domName.Text = domPcs.Count == 0 ? Lang.T("No PC has reported yet") : Lang.T("No PC matches"); domState.Text = ""; domSub.Text = domPcs.Count == 0 ? Lang.T("Install Cure Agent on the PCs (User Guide > Cure). They show here within a few minutes.") : ""; domBanner.Visible = false; }
        }

        void DomainDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var p = (DomPc)domList.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(sel ? Mix(Theme.Panel, Theme.Accent, Theme.Dark ? 0.35 : 0.15) : Theme.Panel)) g.FillRectangle(b, e.Bounds);
            var c = RiskColor(p.Risk);
            int x = e.Bounds.X + S(10), y = e.Bounds.Y;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y + S(10), S(10), S(10));
            using (var f = new Font("Segoe UI Semibold", 10f)) TextRenderer.DrawText(g, p.Name, f, new Point(x + S(16), y + S(4)), Theme.Ink);
            int fp = CureAdvice.FreePercent(p.R);
            string line = (p.R != null && p.R.Free >= 0 ? Fs.Fmt(p.R.Free) + " " + Lang.T("free") + " (" + fp + "%)" : "?") + "  ·  " + Lang.T(CureAdvice.RiskText(p.Risk));
            using (var f = new Font("Segoe UI", 8.5f)) TextRenderer.DrawText(g, line, f, new Point(x + S(16), y + S(23)), Theme.Quiet);
            // a thin bar: how full C: is
            int bw = e.Bounds.Width - S(36), by = y + S(40);
            using (var b = new SolidBrush(Theme.Grid)) g.FillRectangle(b, x + S(16), by, bw - S(16), S(3));
            if (fp >= 0) using (var b = new SolidBrush(c)) g.FillRectangle(b, x + S(16), by, (bw - S(16)) * (100 - fp) / 100, S(3));
        }

        void DomainShow(DomPc p)
        {
            var r = p.R ?? new CureReport();
            domName.Text = p.Name;
            domState.Text = "●  " + Lang.T(CureAdvice.RiskText(p.Risk)); domState.ForeColor = RiskColor(p.Risk);
            DateTime seen; string ago = DateTime.TryParse(p.Seen ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out seen) ? Ago(seen) : "?";
            domSub.Text = string.Join("  ·  ", new[] { r.Windows, r.Model, r.Ou, Lang.T("last report") + " " + ago, r.Accounts > 0 ? r.Accounts + " " + Lang.T(r.Accounts == 1 ? "account" : "accounts") : null }.Where(x => !string.IsNullOrEmpty(x)));
            // the banner: why it's at risk, in one line
            int fp = CureAdvice.FreePercent(r), dtf = CureAdvice.DaysToFull(r, p.History);
            string banner = p.Risk == CureRisk.Disk ? "The disk is failing. Back up this PC's files now." :
                            p.Risk == CureRisk.Full ? "At risk: C: has " + Fs.Fmt(r.Free) + " free (" + fp + "%)" + (dtf >= 0 ? " and fills up in about " + dtf + " days." : ".") + " " + Fs.Fmt(CureAdvice.CouldFree(r)) + " can be freed." :
                            p.Risk == CureRisk.Low ? "Getting full: " + Fs.Fmt(r.Free) + " free (" + fp + "%)" + (dtf >= 0 ? ", full in about " + dtf + " days." : ".") :
                            p.Risk == CureRisk.Silent ? "This PC hasn't reported for more than 7 days (turned off, away, or Cure Agent stopped)." : null;
            domBanner.Visible = banner != null; domBannerText = banner != null ? Lang.T(banner) : "";
            if (banner != null)
            {
                domBanner.Text = Lang.T(banner);
                var c = RiskColor(p.Risk);
                domBanner.ForeColor = Theme.Dark ? Color.White : c;
                domBanner.BackColor = Mix(Theme.Panel, c, Theme.Dark ? 0.35 : 0.12);
            }
            // the cards
            domPcKpis.Controls.Clear();
            Action<string, string, string, Color> k = (cap, val, note, c) => domPcKpis.Controls.Add(new KpiCard { Cap = Lang.T(cap), Val = val, Note = note, ValColor = c, Scale_ = s, Size = new Size(S(150), S(88)), Margin = new Padding(0, 0, S(8), S(8)) });
            k("C: free", r.Free >= 0 ? Fs.Fmt(r.Free) : "?", r.Total > 0 ? fp + "% " + Lang.T("of") + " " + Fs.Fmt(r.Total) : "", fp >= 0 && fp < CureAdvice.FullPercent ? Theme.MapFore(CriticalColor) : fp >= 0 && fp < CureAdvice.LowPercent ? Theme.MapFore(CautionColor) : Color.Empty);
            k("Can be freed", Fs.Fmt(CureAdvice.CouldFree(r)), Lang.T("Safe now:") + " " + Fs.Fmt(Math.Max(0, r.CleanSafe)), Color.Empty);
            var worst = (r.Disks ?? new CureDisk[0]).OrderByDescending(d => d.Level == "Failing" ? 3 : d.Level == "Warning" ? 2 : 1).FirstOrDefault();
            k("Disk health", r.DiskLevel == "Healthy" ? Lang.T("Good") : string.IsNullOrEmpty(r.DiskLevel) ? "?" : Lang.T(r.DiskLevel), worst != null ? (worst.Wear >= 0 ? Lang.T("wear") + " " + worst.Wear + "%" : "") + (worst.Temp > 0 ? "  " + worst.Temp + " °C" : "") : "",
              r.DiskLevel == "Failing" ? Theme.MapFore(CriticalColor) : r.DiskLevel == "Warning" ? Theme.MapFore(CautionColor) : Theme.MapFore(SafeColor));
            string lc = string.IsNullOrEmpty(r.LastClean) ? Lang.T("never") : DaysText(r.LastClean);
            k("Last clean", lc, (r.LastCleanBytes > 0 ? Lang.T("freed") + " " + Fs.Fmt(r.LastCleanBytes) : "") + (r.WeeklyClean ? "  ·  " + Lang.T("weekly clean on") : "  ·  " + Lang.T("weekly clean off")), Color.Empty);
            k("App updates", r.AppUpdates < 0 ? "?" : r.AppUpdates.ToString(), Lang.T("from winget / Store"), r.AppUpdates > 5 ? Theme.MapFore(CautionColor) : Color.Empty);
            k("Startup programs", r.StartupCount < 0 ? "?" : r.StartupCount.ToString(), Lang.T("start with Windows"), r.StartupCount > 15 ? Theme.MapFore(CautionColor) : Color.Empty);
            // what fills C:
            domFill.Parts = new List<Tuple<string, long, Color>>();
            if (r.FillCritical >= 0)
            {
                domFill.Parts.Add(Tuple.Create(Lang.T("Safe to clean"), r.FillSafe, Theme.MapFore(SafeColor)));
                domFill.Parts.Add(Tuple.Create(Lang.T("Your files"), r.FillReview, Theme.MapFore(ReviewColor)));
                domFill.Parts.Add(Tuple.Create(Lang.T("Leftovers and system stores"), r.FillCaution, Theme.MapFore(CautionColor)));
                domFill.Parts.Add(Tuple.Create(Lang.T("Windows and apps"), r.FillCritical, Theme.MapFore(CriticalColor)));
                domFill.Parts.Add(Tuple.Create(Lang.T("Free"), Math.Max(0, r.Free), Theme.Dark ? Color.FromArgb(150, 150, 170) : Color.FromArgb(160, 168, 182)));
                domFill.Total = r.Total;
            }
            else domFill.Total = 0;
            domFill.Invalidate();
            // what to do
            domTips.SuspendLayout(); domTips.Controls.Clear(); domTips.RowStyles.Clear(); domTips.RowCount = 0;
            var tips = CureAdvice.Tips(r, p.History);
            if (tips.Count == 0) tips.Add(new CureTip { Level = -1, Text = "Nothing to do: this PC is in good shape." });
            foreach (var t in tips)
            {
                var c = t.Level >= 2 ? Theme.MapFore(CriticalColor) : t.Level == 1 ? Theme.MapFore(CautionColor) : t.Level == 0 ? Theme.MapFore(ReviewColor) : Theme.MapFore(SafeColor);
                domTips.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                domTips.Controls.Add(new Label { Text = "●", AutoSize = true, ForeColor = c, Margin = new Padding(0, S(2), S(6), S(4)) }, 0, domTips.RowCount);
                domTips.Controls.Add(new Label { Text = Lang.T(t.Text), AutoSize = true, MaximumSize = new Size(S(900), 0), UseMnemonic = false, Margin = new Padding(0, S(2), 0, S(4)),
                                                 Font = t.Level >= 2 ? new Font("Segoe UI Semibold", 9.5f) : new Font("Segoe UI", 9.5f) }, 1, domTips.RowCount);
                domTips.RowCount++;
            }
            domTips.ResumeLayout();
            // the lists
            Func<string, string> lvl = tier => Lang.T(tier == "safe" ? "Safe" : tier == "review" ? "Check first" : tier == "caution" ? "Can't be undone" : tier == "critical" ? "Never delete" : tier);
            Action<ListView, IEnumerable<string[]>, Func<string[], Color>> fill = (lv, rows, color) =>
            {
                lv.BeginUpdate(); lv.Items.Clear();
                foreach (var row in rows) { var it = new ListViewItem(row); if (color != null) { var cc = color(row); if (!cc.IsEmpty) { it.UseItemStyleForSubItems = false; it.SubItems[1].ForeColor = cc; } } lv.Items.Add(it); }
                if (lv.Items.Count == 0) lv.Items.Add(new ListViewItem(new[] { Lang.T("Not measured yet"), "", "", "" }));
                lv.EndUpdate(); DomFit(lv);
            };
            Func<string[], Color> tierColor = row => row[1] == lvl("safe") ? Theme.MapFore(SafeColor) : row[1] == lvl("review") ? Theme.MapFore(ReviewColor) : row[1] == lvl("caution") ? Theme.MapFore(CautionColor) : row[1] == lvl("critical") ? Theme.MapFore(CriticalColor) : Color.Empty;
            fill(domItems, (r.Items ?? new CureItem[0]).Where(i => i.Bytes > 0).OrderByDescending(i => i.Bytes).Select(i => new[] { Lang.T(i.Name), lvl(i.Tier), Fs.Fmt(i.Bytes), Lang.T(i.Tier == "safe" && i.Auto ? "yes" : "no") }), tierColor);
            fill(domPlaces, (r.Places ?? new CurePlace[0]).Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).Select(x => new[] { Lang.T(x.Name), lvl(x.Tier), Fs.Fmt(x.Bytes), Lang.T(x.Action) }), tierColor);
            fill(domApps, (r.Apps ?? new CureApp[0]).OrderByDescending(a => a.Bytes).Select(a => new[] { a.Name, a.Publisher, Fs.Fmt(a.Bytes), string.IsNullOrEmpty(a.LastUsed) ? "?" : DaysText(a.LastUsed) }), null);
            fill(domDisks, (r.Disks ?? new CureDisk[0]).Select(d => new[] { d.Name + (d.Letters.Length > 0 ? "  (" + d.Letters + ":)" : ""), d.Kind, d.Level == "Healthy" ? Lang.T("Good") : Lang.T(d.Level),
                                                                        string.Join("  ", new[] { d.Wear >= 0 ? Lang.T("wear") + " " + d.Wear + "%" : null, d.Temp > 0 ? d.Temp + " °C" : null }.Concat(d.Reasons).Where(x => !string.IsNullOrEmpty(x))) })
                             .Concat((r.Drives ?? new CureDrive[0]).Select(d => new[] { d.Letter + ":", Lang.T("drive"), d.Total > 0 && d.Free * 100 < d.Total * 10 ? Lang.T("Almost full") : "", Fs.Fmt(d.Free) + " " + Lang.T("free of") + " " + Fs.Fmt(d.Total) })),
                 row => row[2] == Lang.T("Failing") || row[2] == Lang.T("Almost full") ? Theme.MapFore(CriticalColor) : row[2] == Lang.T("Warning") ? Theme.MapFore(CautionColor) : Color.Empty);
            domDisks.Items.Cast<ListViewItem>().ToList().ForEach(it => { if (it.SubItems.Count > 2 && !it.UseItemStyleForSubItems) { it.SubItems[2].ForeColor = it.SubItems[1].ForeColor; it.SubItems[1].ForeColor = Theme.Ink; } });
            domStartup.Text = (r.Startup ?? new string[0]).Length == 0 ? Lang.T("Not reported yet.") : string.Join(",  ", r.Startup);
            domRemove.Tag = p;
            domDetail.AutoScrollPosition = new Point(0, 0);
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            return d.TotalMinutes < 1 ? Lang.T("just now") : d.TotalMinutes < 60 ? (int)d.TotalMinutes + " " + Lang.T("min ago") : d.TotalHours < 24 ? (int)d.TotalHours + " " + Lang.T("h ago") : (int)d.TotalDays + " " + Lang.T("days ago");
        }
        static string DaysText(string iso)
        {
            DateTime t; if (!DateTime.TryParse(iso ?? "", CultureInfo.InvariantCulture, DateTimeStyles.None, out t)) return "?";
            int d = (int)Math.Floor((DateTime.Now - t).TotalDays);
            return Lang.T(d <= 0 ? "today" : d == 1 ? "yesterday" : d + " days ago");
        }

        void DomainExport()
        {
            using (var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "Domain PCs " + DateTime.Today.ToString("yyyy-MM-dd") + ".csv", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            {
                if (testMode || d.ShowDialog(this) != DialogResult.OK) return;
                try { SafeFile.Save(d.FileName, new CureServer { DataDir = DomainDataDir }.Csv()); Log(Lang.T("Exported: ") + d.FileName); }
                catch (Exception ex) { Lang.Box(this, ex.Message, "Elixar"); }
            }
        }

        void DomainRemove()
        {
            var p = domRemove.Tag as DomPc;
            if (p == null || testMode) return;
            if (Lang.Box(this, Lang.T("Remove " + p.Name + " from the list? Its reports are refused from now on. If Cure Agent still runs there, it joins again with the enrollment token."),
                         "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            CureSetup.StopServer();
            try { new CureServer { DataDir = DomainDataDir }.Remove(p.Id); } finally { CureSetup.StartServer(); }
            using (ChangeLog.In("Cure Server")) ChangeLog.Add("Settings", "Removed a PC from the Cure Server", p.Name, "", "", "", false);
            DomainLoad();
        }

        void DomainSetup()
        {
            if (testMode) return;
            string exe = Path.Combine(Install.Dir, "CureServer.exe");
            if (!File.Exists(exe)) { Lang.Box(this, Lang.T("Install Elixar for Business with its MSI on this PC first (it puts CureServer.exe in " + Install.Dir + ")."), "Elixar"); return; }
            if (Lang.Box(this, Lang.T("Make this PC the Cure Server? It adds a Windows service (Cure Server, its own low-rights account), a certificate for this PC and opens port " + domPort.Value + " on the domain network only. Reports only: nothing can be started on the PCs."),
                         "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            domSetupBtn.Enabled = false;
            int port = (int)domPort.Value; string group = domGroup.Text.Trim();
            Task.Factory.StartNew(() => CureSetup.InstallServer(exe, port, null, group.Length > 0 ? group : null)).ContinueWith(t => UI(() =>
            {
                domSetupBtn.Enabled = true;
                var r = t.Exception == null ? t.Result : new CureSetup.ServerResult { Error = t.Exception.InnerException.Message };
                domSetupOut.Visible = true;
                domSetupOut.Text = r.Error != null ? Lang.T("Not done: ") + r.Error :
                    Lang.T("Cure Server is running on this PC.") + "\r\n\r\n" + Lang.T("Web console:") + "       " + r.Url + "\r\n" + Lang.T("Certificate:") + "       " + r.Thumbprint + "\r\n" +
                    Lang.T("Enrollment token:") + "  " + r.Token + "   (" + Lang.T("shown once; keep it private") + ")\r\n\r\n" + Lang.T("Settings for every PC (Group Policy / Intune):") + "\r\n" + r.RegFile;
                if (r.Error == null) { Log(Lang.T("Cure Server set up on this PC.")); System.Threading.Thread.Sleep(0); }
            }));
        }

        // ---- self-test ----
        public List<string> DomainPcNames { get { return domList.Items.Cast<DomPc>().Select(p => p.Name).ToList(); } }
        public void DomainPick(string name) { var p = domList.Items.Cast<DomPc>().FirstOrDefault(x => x.Name == name); if (p != null) domList.SelectedItem = p; }
        public string DomainHeader { get { return domName.Text + " | " + domState.Text + " | " + domBannerText; } }
        public List<string> DomainTipTexts { get { return domTips.Controls.OfType<Label>().Where(l => l.Text != "●").Select(l => l.Text).ToList(); } }
        public int DomainItemRows { get { return domItems.Items.Count; } }
        public bool DomainSetupShown { get { return domSetupOn; } }
        public void ShowDomain() { tabs.SelectedTab = pageDomain; OnDomainTab(); }
        public void DomainFilter(int i) { domFilter.SelectedIndex = i; }
        public void DomainScroll(int y) { domDetail.AutoScrollPosition = new Point(0, S(y)); }
    }
}
#endif
