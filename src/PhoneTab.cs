// Phone tab: clean an Android phone over USB, or move big photos and videos off an iPhone.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        TabPage pagePhone, pagePhClean, pagePhApps, pagePhBig;
        TabControl phoneTabs;
        Panel phHelp, phBody;
        Label lblPhone, lblPhNote, lblPhCleanNote, lblPhTools, lblPhApps, lblPhBig, lblPhDest;
        DriveBar phBar; RowStyle phBarRow;
        Button btnPhFind, btnPhTools, btnPhClean, btnPhSafe, btnPhMeasure, btnPhAppRemove, btnPhAppRestore, btnPhAppSuggest, btnPhBigFind, btnPhCopy, btnPhMove, btnPhDelete, btnPhDest;
        ListView phList, phTips, phAppList, phBigList;
        CheckBox chkPhBackup;
        ComboBox cmbPhMin;
        PhoneDevice phone; IPhone.Found iphone;
        List<PhoneItem> phItems = new List<PhoneItem>();
        List<StorageSplit> phSplit = new List<StorageSplit>();
        string phDest;
        bool phBusy, phLoaded;
        public event Action PhoneDone;
        public int PhoneRows { get { return phList.Items.Count + phAppList.Items.Count; } }

        static readonly Color[] SplitColors =
        {
            Color.FromArgb(91, 76, 196), Color.FromArgb(140, 128, 220), Color.FromArgb(0, 125, 70), Color.FromArgb(232, 140, 30),
            Color.FromArgb(214, 69, 65), Color.FromArgb(40, 90, 200), Color.FromArgb(120, 170, 220), Color.FromArgb(150, 160, 175), Color.FromArgb(190, 105, 0)
        };

        void BuildPhone()
        {
            pagePhone = new TabPage("Phone") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(S(12), S(8), S(12), S(6)) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(phBarRow = new RowStyle(SizeType.Absolute, S(80))); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var head = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            head.Controls.Add(Title("Phone"));
            lblPhone = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), ForeColor = Color.FromArgb(46, 38, 92), Margin = new Padding(S(14), S(9), 0, 0), Text = "Looking for a phone..." };
            head.Controls.Add(lblPhone);
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(4)) };
            btnPhFind = MakeButton("Look for phone again");
            btnPhTools = MakeButton("Get Android tools (Google, 7 MB)");
            lblPhNote = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(8), S(8), 0, 0), MaximumSize = new Size(S(760), 0) };
            row.Controls.AddRange(new Control[] { btnPhFind, btnPhTools, lblPhNote });
            phBar = new DriveBar { Dock = DockStyle.Fill, BarHeight = S(20), BackColor = pagePhone.BackColor, Of = "the phone" };

            phBody = new Panel { Dock = DockStyle.Fill };
            phoneTabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(S(12), S(4)) };
            pagePhClean = new TabPage("Clean") { BackColor = pagePhone.BackColor };
            pagePhApps = new TabPage("Apps") { BackColor = pagePhone.BackColor };
            pagePhBig = new TabPage("Big files") { BackColor = pagePhone.BackColor };
            phoneTabs.TabPages.AddRange(new[] { pagePhClean, pagePhApps, pagePhBig });
            phHelp = BuildPhoneHelp();
            phBody.Controls.Add(phoneTabs); phBody.Controls.Add(phHelp);
            BuildPhClean(); BuildPhApps(); BuildPhBig();

            t.Controls.Add(head, 0, 0); t.Controls.Add(row, 0, 1); t.Controls.Add(phBar, 0, 2); t.Controls.Add(phBody, 0, 3);
            pagePhone.Controls.Add(t);

            btnPhFind.Click += (o, e) => DetectPhone();
            btnPhTools.Click += (o, e) => GetAndroidTools();
            phoneTabs.SelectedIndexChanged += (o, e) => { if (phoneTabs.SelectedTab == pagePhBig && phBigList.Items.Count == 0 && !phBusy && (phone != null || iphone != null)) FindBigFiles(); };
            ShowPhoneHelp(true);
        }

        Panel BuildPhoneHelp()
        {
            var p = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, Padding = new Padding(S(18), S(14), S(18), S(14)) };
            var text = new Label
            {
                AutoSize = true, Location = new Point(S(18), S(14)), MaximumSize = new Size(S(1000), 0), Font = new Font("Segoe UI", 10f),
                Text =
                    "ANDROID  (Samsung, Xiaomi, Pixel, OnePlus...)\n" +
                    "   1.  On the phone open Settings > About phone and tap \"Build number\" 7 times.\n" +
                    "        (Samsung: About phone > Software information > Build number.  Xiaomi: About phone > OS version.)\n" +
                    "   2.  Open Settings > Developer options and turn on \"USB debugging\".\n" +
                    "   3.  Plug the phone into this PC with a USB cable, unlock it and tap \"Allow\" when it asks about USB debugging.\n" +
                    "   Elixar talks to Android with Google's free Android tools (adb). If they are missing, click \"Get Android tools\" above.\n" +
                    "   You can turn USB debugging off again when you are done.\n\n" +
                    "iPHONE\n" +
                    "   1.  Install \"Apple Devices\" from the Microsoft Store (or iTunes), so Windows can talk to the iPhone.\n" +
                    "   2.  Plug the iPhone in, unlock it, tap \"Trust\" and enter your passcode.\n" +
                    "   Apple doesn't let a PC clean other apps on an iPhone. Elixar shows the storage, finds your biggest photos and\n" +
                    "   videos so you can move them to this PC, and lists what to clean on the iPhone itself.\n\n" +
                    "Then click \"Look for phone again\"."
            };
            p.Controls.Add(text);
            return p;
        }

        void ShowPhoneHelp(bool help)
        {
            phHelp.Visible = help; phoneTabs.Visible = !help; phBar.Visible = !help; phBarRow.Height = help ? 0 : S(80);
            if (help) phHelp.BringToFront(); else phoneTabs.BringToFront();
        }

        // ---------------- Clean ----------------
        const string AndroidCleanNote = "Safe items come back on their own. Review items are your files: Elixar copies them to this PC first, then removes them from the phone.";

        void BuildPhClean()
        {
            var t = Page(4);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var note = lblPhCleanNote = Note(AndroidCleanNote);
            phList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            phList.Columns.Add("Item", S(300)); phList.Columns.Add("Size", S(90), HorizontalAlignment.Right); phList.Columns.Add("Files", S(70), HorizontalAlignment.Right);
            phList.Columns.Add("Kind", S(80)); phList.Columns.Add("What it is", S(560));
            phList.ItemChecked += (o, e) => UpdatePhClean();
            phTips = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, Visible = false };
            phTips.Columns.Add("On the iPhone", S(420)); phTips.Columns.Add("What it frees", S(640));
            var holder = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            holder.Controls.Add(phList); holder.Controls.Add(phTips);
            chkPhBackup = new CheckBox { AutoSize = true, Checked = true, Text = "Copy Review items to this PC before removing them (recommended)", Margin = new Padding(S(2), S(8), 0, 0) };
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnPhClean = MakeButton("Clean selected"); btnPhClean.Font = new Font("Segoe UI Semibold", 9f); btnPhClean.Enabled = false;
            btnPhSafe = MakeButton("Select safe items"); btnPhMeasure = MakeButton("Measure again");
            row.Controls.AddRange(new Control[] { btnPhClean, btnPhSafe, btnPhMeasure });
            t.Controls.Add(note, 0, 0); t.Controls.Add(holder, 0, 1); t.Controls.Add(chkPhBackup, 0, 2); t.Controls.Add(row, 0, 3);
            pagePhClean.Controls.Add(t);
            btnPhSafe.Click += (o, e) => { foreach (ListViewItem r in phList.Items) { var it = (PhoneItem)r.Tag; r.Checked = it.Tier == "safe" && it.Bytes != 0; } };
            btnPhMeasure.Click += (o, e) => { if (phone != null) LoadAndroid(); };
            btnPhClean.Click += (o, e) => CleanPhone();
        }

        void UpdatePhClean()
        {
            var sel = phList.CheckedItems.Cast<ListViewItem>().Select(r => (PhoneItem)r.Tag).ToList();
            btnPhClean.Enabled = sel.Count > 0 && !phBusy && !testMode && phone != null;
            btnPhClean.Text = sel.Count > 0 ? "Clean " + sel.Count + " (" + Fs.Fmt(sel.Sum(i => Math.Max(0, i.Bytes))) + ")" : "Clean selected";
        }

        // ---------------- Apps ----------------
        void BuildPhApps()
        {
            var t = Page(3);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            lblPhApps = Note("Pre-installed apps are removed for you only and can be put back any time. Your own apps are uninstalled with their data.");
            phAppList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            phAppList.Columns.Add("App", S(260)); phAppList.Columns.Add("Total", S(80), HorizontalAlignment.Right); phAppList.Columns.Add("Data", S(80), HorizontalAlignment.Right);
            phAppList.Columns.Add("Cache", S(75), HorizontalAlignment.Right); phAppList.Columns.Add("Last used", S(100)); phAppList.Columns.Add("Notes", S(480));
            phAppList.ItemChecked += (o, e) => UpdatePhApps();
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            btnPhAppRemove = MakeButton("Remove selected..."); btnPhAppRemove.Font = new Font("Segoe UI Semibold", 9f); btnPhAppRemove.Enabled = false;
            btnPhAppRestore = MakeButton("Put back selected"); btnPhAppRestore.Enabled = false;
            btnPhAppSuggest = MakeButton("Select suggested pre-installed apps");
            row.Controls.AddRange(new Control[] { btnPhAppRemove, btnPhAppRestore, btnPhAppSuggest });
            t.Controls.Add(lblPhApps, 0, 0); t.Controls.Add(phAppList, 0, 1); t.Controls.Add(row, 0, 2);
            pagePhApps.Controls.Add(t);
            btnPhAppSuggest.Click += (o, e) => { foreach (ListViewItem r in phAppList.Items) { var a = (PhoneApp)r.Tag; r.Checked = !a.Removed && a.Tier == "optional"; } };
            btnPhAppRemove.Click += (o, e) => PhoneApps(false);
            btnPhAppRestore.Click += (o, e) => PhoneApps(true);
        }

        void UpdatePhApps()
        {
            var sel = phAppList.CheckedItems.Cast<ListViewItem>().Select(r => (PhoneApp)r.Tag).ToList();
            bool can = !phBusy && !testMode && phone != null;
            btnPhAppRemove.Enabled = can && sel.Any(a => !a.Removed);
            btnPhAppRestore.Enabled = can && sel.Any(a => a.Removed);
            int n = sel.Count(a => !a.Removed);
            btnPhAppRemove.Text = n > 0 ? "Remove " + n + " (" + Fs.Fmt(sel.Where(a => !a.Removed).Sum(a => a.System ? Math.Max(0, a.DataBytes) + Math.Max(0, a.CacheBytes) : a.Total)) + ")..." : "Remove selected...";
        }

        // ---------------- Big files ----------------
        void BuildPhBig()
        {
            var t = Page(4);
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var top = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, S(4)) };
            top.Controls.Add(new Label { AutoSize = true, Text = "Show files bigger than", Margin = new Padding(0, S(7), S(4), 0) });
            cmbPhMin = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(90) };
            cmbPhMin.Items.AddRange(new object[] { "20 MB", "50 MB", "100 MB", "500 MB" }); cmbPhMin.SelectedIndex = 1;
            btnPhBigFind = MakeButton("Find");
            lblPhBig = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(8), S(7), 0, 0) };
            top.Controls.AddRange(new Control[] { cmbPhMin, btnPhBigFind, lblPhBig });
            phBigList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, Dock = DockStyle.Fill, ShowItemToolTips = true };
            phBigList.Columns.Add("File", S(330)); phBigList.Columns.Add("Size", S(85), HorizontalAlignment.Right); phBigList.Columns.Add("Date", S(95));
            phBigList.Columns.Add("Type", S(130)); phBigList.Columns.Add("Folder", S(440));
            phBigList.ItemChecked += (o, e) => UpdatePhBig();
            var dest = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(6), 0, 0) };
            lblPhDest = new Label { AutoSize = true, Margin = new Padding(0, S(7), S(6), 0) };
            btnPhDest = MakeButton("Change...");
            var btnOpen = MakeButton("Open folder");
            dest.Controls.AddRange(new Control[] { lblPhDest, btnPhDest, btnOpen });
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(2), 0, 0) };
            btnPhMove = MakeButton("Move to PC"); btnPhMove.Font = new Font("Segoe UI Semibold", 9f);
            btnPhCopy = MakeButton("Copy to PC"); btnPhDelete = MakeButton("Delete from phone...");
            row.Controls.AddRange(new Control[] { btnPhMove, btnPhCopy, btnPhDelete });
            t.Controls.Add(top, 0, 0); t.Controls.Add(phBigList, 0, 1); t.Controls.Add(dest, 0, 2); t.Controls.Add(row, 0, 3);
            pagePhBig.Controls.Add(t);
            btnPhBigFind.Click += (o, e) => FindBigFiles();
            btnPhMove.Click += (o, e) => BigFilesToPc(true);
            btnPhCopy.Click += (o, e) => BigFilesToPc(false);
            btnPhDelete.Click += (o, e) => DeleteBigFiles();
            btnPhDest.Click += (o, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Where should copies from the phone go?", SelectedPath = PhoneDestRoot() })
                    if (d.ShowDialog(this) == DialogResult.OK) { phDest = d.SelectedPath; UpdatePhBig(); }
            };
            btnOpen.Click += (o, e) => { try { Directory.CreateDirectory(PhoneDestRoot()); Process.Start("explorer.exe", "\"" + PhoneDestRoot() + "\""); } catch { } };
            UpdatePhBig();
        }

        string PhoneDestRoot()
        {
            if (phDest != null) return phDest;
            return PhoneBackup.Root(1L << 30);
        }

        string PhoneDestFor()
        {
            string who = phone != null ? phone.Title : iphone != null ? iphone.Name : "phone";
            return Path.Combine(PhoneDestRoot(), PhoneBackup.Safe(who));
        }

        void UpdatePhBig()
        {
            var sel = phBigList.CheckedItems.Cast<ListViewItem>().Select(r => (PhoneFile)r.Tag).ToList();
            bool can = sel.Count > 0 && !phBusy && (phone != null || iphone != null);
            string size = sel.Count > 0 ? " " + sel.Count + " (" + Fs.Fmt(sel.Sum(f => f.Bytes)) + ")" : "";
            btnPhMove.Text = "Move" + size + " to PC"; btnPhCopy.Text = "Copy" + size + " to PC";
            btnPhMove.Enabled = can && !testMode; btnPhCopy.Enabled = can; btnPhDelete.Enabled = can && !testMode;
            lblPhDest.Text = "Copies go to:  " + PhoneDestFor();
        }

        // ---------------- finding the phone ----------------
        public void ShowPhone() { tabs.SelectedTab = pagePhone; OnPhoneTab(); }

        void OnPhoneTab() { if (!phLoaded) DetectPhone(); }

        void SetPhBusy(bool busy)
        {
            phBusy = busy;
            foreach (var b in new[] { btnPhFind, btnPhTools, btnPhSafe, btnPhMeasure, btnPhAppSuggest, btnPhBigFind, btnPhDest }) b.Enabled = !busy;
            UpdatePhClean(); UpdatePhApps(); UpdatePhBig();
            pagePhone.UseWaitCursor = busy;
        }

        public void DetectPhone()
        {
            if (phBusy) return;
            phLoaded = true; SetPhBusy(true);
            lblPhone.Text = "Looking for a phone..."; lblPhNote.Text = "";
            Task.Factory.StartNew(() =>
            {
                string err;
                var devs = Adb.Devices(out err);
                IPhone.Found ip = null;
                if (devs.Count == 0) { try { ip = IPhone.Find(); } catch { } }
                return Tuple.Create(devs, err, ip);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                SetPhBusy(false);
                phone = null; iphone = null;
                var devs = t.IsFaulted ? new List<PhoneDevice>() : t.Result.Item1;
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result.Item2;
                btnPhTools.Visible = err == "notools";
                var ready = devs.FirstOrDefault(d => d.State == "device");
                if (ready != null) { phone = ready; LoadAndroid(); return; }
                var waiting = devs.FirstOrDefault();
                if (waiting != null)
                {
                    lblPhone.Text = "Android phone found, waiting for permission";
                    lblPhNote.Text = waiting.State == "unauthorized" ? "Unlock the phone and tap \"Allow\" on \"Allow USB debugging?\", then click \"Look for phone again\"."
                                                                       : "The phone is " + waiting.State + ". Unplug it, plug it in again and unlock it.";
                    ShowPhoneHelp(true); SetPhoneBar(null, 0, 0); FirePhoneDone(); return;
                }
                if (!t.IsFaulted && t.Result.Item3 != null) { ShowIPhone(t.Result.Item3, null); return; }
                lblPhone.Text = "No phone connected";
                lblPhNote.Text = err == "notools" ? "Android tools are not on this PC yet; the button gets them from Google." : "";
                ShowPhoneHelp(true); SetPhoneBar(null, 0, 0); FirePhoneDone();
            }));
        }

        void FirePhoneDone() { if (PhoneDone != null) PhoneDone(); }

        void GetAndroidTools()
        {
            if (phBusy) return;
            SetPhBusy(true); lblPhNote.Text = "Downloading Google's Android tools...";
            Task.Factory.StartNew(() => Adb.Download(Log)).ContinueWith(t => UI(() =>
            {
                SetPhBusy(false);
                string err = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                if (err != null) { lblPhNote.Text = err; return; }
                btnPhTools.Visible = false;
                DetectPhone();
            }));
        }

        void SetPhoneBar(List<StorageSplit> split, long total, long free)
        {
            phBar.Total = total;
            phBar.Segments = new List<DriveBar.Seg>();
            if (total > 0)
            {
                int i = 0;
                long known = 0;
                foreach (var s in (split ?? new List<StorageSplit>()).Where(x => x.Bytes > 0))
                {
                    phBar.Segments.Add(new DriveBar.Seg { Label = s.Label, Bytes = s.Bytes, Color = SplitColors[i++ % SplitColors.Length] });
                    known += s.Bytes;
                }
                long used = total - free;
                if (known == 0) phBar.Segments.Add(new DriveBar.Seg { Label = "Used", Bytes = used, Color = SplitColors[0] });
                else if (used > known) phBar.Segments.Add(new DriveBar.Seg { Label = "Not split by Android", Bytes = used - known, Color = Color.FromArgb(190, 196, 205) });
                phBar.Segments.Add(new DriveBar.Seg { Label = "Free", Bytes = free, Color = Color.FromArgb(236, 239, 244), Outline = true });
            }
            phBar.Invalidate();
        }

        // ---------------- Android ----------------
        void LoadAndroid()
        {
            if (phBusy || phone == null) return;
            SetPhBusy(true);
            ShowPhoneHelp(false);
            phList.Visible = true; phTips.Visible = false; chkPhBackup.Visible = true; lblPhCleanNote.Text = AndroidCleanNote;
            btnPhClean.Visible = btnPhSafe.Visible = btnPhMeasure.Visible = true;
            phAppList.Visible = true; btnPhAppRemove.Visible = btnPhAppRestore.Visible = btnPhAppSuggest.Visible = true;
            lblPhApps.Text = "Pre-installed apps are removed for you only and can be put back any time. Your own apps are uninstalled with their data.";
            lblPhone.Text = "Reading the phone..."; lblPhNote.Text = "";
            var dev = phone;
            Task.Factory.StartNew(() =>
            {
                Android.ParseInfo(dev, Adb.Shell(dev.Serial, Android.InfoScript, 60000));
                List<StorageSplit> split;
                var apps = Android.ParseApps(Adb.Shell(dev.Serial, Android.AppsScript, 180000), out split);
                long cache = split.Where(s => s.Label == "App Cache").Sum(s => s.Bytes);
                var items = Android.Items();
                foreach (var it in items) Android.Measure(dev.Serial, it, cache);
                return Tuple.Create(split, apps, items);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                SetPhBusy(false);
                if (t.IsFaulted) { lblPhone.Text = "Could not read the phone"; lblPhNote.Text = t.Exception.GetBaseException().Message; FirePhoneDone(); return; }
                ShowAndroid(phone, t.Result.Item1, t.Result.Item2, t.Result.Item3);
                FirePhoneDone();
            }));
        }

        void ShowAndroid(PhoneDevice dev, List<StorageSplit> split, List<PhoneApp> apps, List<PhoneItem> items)
        {
            phone = dev; iphone = null; phSplit = split; phItems = items;
            ShowPhoneHelp(false);
            lblPhone.Text = dev.Title + (dev.Version.Length > 0 ? "   ·   Android " + dev.Version : "") +
                            (dev.Total > 0 ? "   ·   " + Fs.Fmt(dev.Total - dev.Free) + " used of " + Fs.Fmt(dev.Total) + ", " + Fs.Fmt(dev.Free) + " free" : "");
            lblPhNote.Text = split.Count > 0 ? "Storage split comes from Android, which updates it about once a day." : "";
            SetPhoneBar(split, dev.Total, dev.Free);

            phList.BeginUpdate(); phList.Items.Clear();
            foreach (var it in items.Where(i => i.AllCaches || i.Bytes > 0).OrderBy(i => i.Tier == "safe" ? 0 : 1).ThenByDescending(i => i.Bytes))
            {
                var r = new ListViewItem(it.Name) { Tag = it, UseItemStyleForSubItems = false, ToolTipText = string.Join("\n", it.Found) };
                r.SubItems.Add(it.Bytes >= 0 ? Fs.Fmt(it.Bytes) : "?");
                r.SubItems.Add(it.AllCaches ? "" : it.Files.ToString("N0"));
                r.SubItems.Add(it.Tier == "safe" ? "Safe" : "Review").ForeColor = it.Tier == "safe" ? SafeColor : ReviewColor;
                r.SubItems.Add(it.Info);
                r.Checked = it.DefaultOn && it.Bytes != 0;
                phList.Items.Add(r);
            }
            phList.EndUpdate();
            UpdatePhClean();
            ShowPhoneApps(apps);
        }

        void ShowPhoneApps(List<PhoneApp> apps)
        {
            phAppList.BeginUpdate(); phAppList.Items.Clear(); phAppList.Groups.Clear();
            var gPre = new ListViewGroup("Pre-installed apps you can remove (removed for you only - can be put back)", HorizontalAlignment.Left);
            var gYours = new ListViewGroup("Your apps, biggest first", HorizontalAlignment.Left);
            var gGone = new ListViewGroup("Removed earlier - tick and click \"Put back\"", HorizontalAlignment.Left);
            phAppList.Groups.AddRange(new[] { gPre, gYours, gGone });
            Func<long, string> F = b => b < 0 ? "" : Fs.Fmt(b);
            Action<PhoneApp, ListViewGroup, string, Color> add = (a, g, notes, color) =>
            {
                var r = new ListViewItem(a.Name) { Tag = a, Group = g, UseItemStyleForSubItems = false, ToolTipText = a.Package };
                r.SubItems.Add(a.Total > 0 ? Fs.Fmt(a.Total) : ""); r.SubItems.Add(F(a.DataBytes)); r.SubItems.Add(F(a.CacheBytes));
                r.SubItems.Add(a.LastUsed > DateTime.MinValue ? (DateTime.Now - a.LastUsed).TotalDays < 1 ? "today" : ((int)(DateTime.Now - a.LastUsed).TotalDays) + " days ago" : "");
                r.SubItems.Add(notes).ForeColor = color;
                phAppList.Items.Add(r);
            };
            foreach (var a in apps.Where(x => x.System && !x.Removed && x.Tier.Length > 0).OrderBy(x => x.Tier == "optional" ? 0 : 1).ThenByDescending(x => x.Total))
                add(a, gPre, (a.Tier == "optional" ? "suggested - " : "your decision - ") + a.Why, a.Tier == "optional" ? SafeColor : ReviewColor);
            foreach (var a in apps.Where(x => !x.System && !x.Removed).OrderByDescending(x => x.Total))
            {
                bool idle = a.LastUsed > DateTime.MinValue && (DateTime.Now - a.LastUsed).TotalDays > 60;
                add(a, gYours, idle ? "not used for " + (int)(DateTime.Now - a.LastUsed).TotalDays + " days" : "", idle ? CautionColor : Color.DimGray);
            }
            foreach (var a in apps.Where(x => x.Removed).OrderBy(x => x.Name))
                add(a, gGone, "removed for you; still on the phone's system part", Color.DimGray);
            phAppList.EndUpdate();
            UpdatePhApps();
        }

        void CleanPhone()
        {
            var sel = phList.CheckedItems.Cast<ListViewItem>().Select(r => (PhoneItem)r.Tag).ToList();
            if (sel.Count == 0 || phone == null || testMode) return;
            bool backup = chkPhBackup.Checked;
            string root = Path.Combine(PhoneDestFor(), "Cleaned " + DateTime.Now.ToString("yyyy-MM-dd HHmm"));
            var review = sel.Where(i => i.Tier != "safe").ToList();
            string msg = "Clean " + sel.Count + " items (" + Fs.Fmt(sel.Sum(i => Math.Max(0, i.Bytes))) + ") on " + phone.Title + "?" +
                         (review.Count > 0 ? "\n\n" + (backup ? "Review items are copied to:\n" + root + "\nfirst. Only files copied in full are removed from the phone."
                                                              : "Review items are NOT copied to this PC first, so they can't be brought back.") : "");
            if (Lang.Box(this, msg, "Elixar", MessageBoxButtons.OKCancel, review.Count > 0 && !backup ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;
            SetPhBusy(true);
            var dev = phone;
            Task.Factory.StartNew(() =>
            {
                long freed = 0;
                foreach (var it in sel)
                {
                    Log("Phone: " + it.Name + "...");
                    UI(() => lblPhNote.Text = "Cleaning: " + it.Name + "...");
                    freed += Android.Clean(dev.Serial, it, it.Tier != "safe" && backup ? root : null, Log);
                }
                return freed;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                SetPhBusy(false);
                string done = t.IsFaulted ? "Stopped: " + t.Exception.GetBaseException().Message : "Phone cleaned: about " + Fs.Fmt(t.Result) + " freed.";
                Log(done);
                Lang.Box(this, done, "Elixar", MessageBoxButtons.OK, t.IsFaulted ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                LoadAndroid();
            }));
        }

        void PhoneApps(bool restore)
        {
            var sel = phAppList.CheckedItems.Cast<ListViewItem>().Select(r => (PhoneApp)r.Tag).Where(a => a.Removed == restore).ToList();
            if (sel.Count == 0 || phone == null || testMode) return;
            if (!restore)
            {
                var own = sel.Where(a => !a.System).ToList();
                string msg = "Remove " + sel.Count + " apps from " + phone.Title + "?\n\n" +
                             (sel.Count(a => a.System) > 0 ? "  - " + sel.Count(a => a.System) + " pre-installed: removed for you only; \"Put back\" restores them.\n" : "") +
                             (own.Count > 0 ? "  - " + own.Count + " of your own apps: uninstalled together with their data (" + string.Join(", ", own.Take(6).Select(a => a.Name)) + (own.Count > 6 ? "..." : "") + ").\n    You can install them again from the Play Store.\n" : "");
                if (Lang.Box(this, msg, "Elixar", MessageBoxButtons.OKCancel, own.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK) return;
            }
            SetPhBusy(true);
            var dev = phone;
            Task.Factory.StartNew(() =>
            {
                int ok = 0;
                foreach (var a in sel)
                {
                    string o = Adb.Shell(dev.Serial, restore ? Android.AppRestoreCmd(a) : Android.AppRemoveCmd(a), 120000);
                    bool good = Android.Ok(o);
                    if (good) ok++;
                    Log("Phone: " + (restore ? "put back " : "removed ") + a.Name + " (" + a.Package + ")" + (good ? "" : " - failed: " + o.Trim()));
                }
                return ok;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                SetPhBusy(false);
                int ok = t.IsFaulted ? 0 : t.Result;
                Lang.Box(this, (restore ? "Put back " : "Removed ") + ok + " of " + sel.Count + " apps." + (ok < sel.Count ? "\nThe log on the Cleaner tab says why." : ""),
                    "Elixar", MessageBoxButtons.OK, ok == sel.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                LoadAndroid();
            }));
        }

        // ---------------- Big files ----------------
        long PhMin() { return new[] { 20L, 50L, 100L, 500L }[Math.Max(0, cmbPhMin.SelectedIndex)] << 20; }

        void FindBigFiles()
        {
            if (phBusy || (phone == null && iphone == null)) return;
            SetPhBusy(true);
            lblPhBig.Text = "Looking through the phone (can take a minute)...";
            long min = PhMin();
            var dev = phone; var ip = iphone;
            Task.Factory.StartNew(() => dev != null ? Android.BigFiles(dev.Serial, min)
                                                    : IPhone.ListMedia(ip.DcimPath, min, n => UI(() => lblPhBig.Text = n + " found so far...")).OrderByDescending(f => f.Bytes).ToList(),
                TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                SetPhBusy(false);
                if (t.IsFaulted) { lblPhBig.Text = "Could not list files: " + t.Exception.GetBaseException().Message; return; }
                ShowBigFiles(t.Result);
            }));
        }

        void ShowBigFiles(List<PhoneFile> files)
        {
            phBigList.BeginUpdate(); phBigList.Items.Clear();
            foreach (var f in files)
            {
                var r = new ListViewItem(f.Name) { Tag = f, ToolTipText = f.Path };
                r.SubItems.Add(Fs.Fmt(f.Bytes));
                r.SubItems.Add(f.Date > DateTime.MinValue ? f.Date.ToString("yyyy-MM-dd") : "");
                r.SubItems.Add(f.Kind);
                r.SubItems.Add(f.Folder.StartsWith(Android.Sd) ? f.Folder.Substring(Android.Sd.Length).TrimStart('/') : f.Folder);
                phBigList.Items.Add(r);
            }
            phBigList.EndUpdate();
            lblPhBig.Text = files.Count + " files, " + Fs.Fmt(files.Sum(f => f.Bytes)) + (phone != null ? " (app files under Android/data are left out)" : "");
            UpdatePhBig();
        }

        void BigFilesToPc(bool move)
        {
            var sel = phBigList.CheckedItems.Cast<ListViewItem>().Select(r => (PhoneFile)r.Tag).ToList();
            if (sel.Count == 0 || (move && testMode)) return;
            string root = PhoneDestFor();
            if (move && Lang.Box(this, "Move " + sel.Count + " files (" + Fs.Fmt(sel.Sum(f => f.Bytes)) + ") to:\n" + root +
                    "\n\nEach file is copied, checked to be the same size, and only then deleted from the phone.", "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            SetPhBusy(true);
            var dev = phone;
            Task.Factory.StartNew(() =>
            {
                int ok = 0; long bytes = 0; var gone = new List<string>();
                foreach (var f in sel)
                {
                    UI(() => lblPhBig.Text = (move ? "Moving " : "Copying ") + f.Name + " (" + (ok + 1) + " of " + sel.Count + ")...");
                    string dir = dev != null ? PhoneBackup.LocalFor(root, f.Folder) : Path.Combine(root, f.Folder.Replace('/', '\\'));
                    bool copied; string o = "";
                    if (dev != null) copied = Android.PullOne(dev.Serial, f, dir, out o);
                    else copied = IPhone.CopyTo(f.Path, f.Name, f.Bytes, dir);
                    if (!copied) { Log("Phone: could not copy " + f.Name + " " + o.Trim()); continue; }
                    ok++; bytes += f.Bytes;
                    if (move)
                    {
                        if (dev != null) gone.Add(f.Path);
                        else if (!IPhone.Delete(f.Path) || IPhone.Exists(f.Path)) Log("Phone: copied " + f.Name + " but the iPhone did not allow deleting it.");
                    }
                }
                if (dev != null && gone.Count > 0) Android.RemoveFiles(dev.Serial, gone);
                return Tuple.Create(ok, bytes);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                SetPhBusy(false);
                int ok = t.IsFaulted ? 0 : t.Result.Item1;
                string done = (move ? "Moved " : "Copied ") + ok + " of " + sel.Count + " files (" + Fs.Fmt(t.IsFaulted ? 0 : t.Result.Item2) + ") to " + root + "." +
                              (move && iphone != null ? "\n\nOn the iPhone, also empty Photos > Albums > Recently Deleted to free the space." : "");
                Log("Phone: " + done.Replace("\n", " "));
                Lang.Box(this, done, "Elixar", MessageBoxButtons.OK, ok == sel.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (move) FindBigFiles(); else lblPhBig.Text = done.Split('\n')[0];
            }));
        }

        void DeleteBigFiles()
        {
            var sel = phBigList.CheckedItems.Cast<ListViewItem>().Select(r => (PhoneFile)r.Tag).ToList();
            if (sel.Count == 0 || testMode) return;
            if (Lang.Box(this, "Delete " + sel.Count + " files (" + Fs.Fmt(sel.Sum(f => f.Bytes)) + ") from the phone for good?\n\nThey are NOT copied anywhere. Use \"Move to PC\" to keep a copy.",
                    "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            SetPhBusy(true);
            var dev = phone;
            Task.Factory.StartNew(() =>
            {
                if (dev != null) Android.RemoveFiles(dev.Serial, sel.Select(f => f.Path).ToList());
                else foreach (var f in sel) IPhone.Delete(f.Path);
                Log("Phone: deleted " + sel.Count + " files (" + Fs.Fmt(sel.Sum(f => f.Bytes)) + ").");
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() => { SetPhBusy(false); FindBigFiles(); }));
        }

        // ---------------- iPhone ----------------
        void ShowIPhone(IPhone.Found f, List<PhoneFile> files)
        {
            iphone = f; phone = null;
            ShowPhoneHelp(false);
            phList.Visible = false; phTips.Visible = true; chkPhBackup.Visible = false;
            lblPhCleanNote.Text = "Apple doesn't let a PC clean an iPhone's apps. These are the places that free the most space, done on the iPhone itself:";
            btnPhClean.Visible = btnPhSafe.Visible = btnPhMeasure.Visible = false;
            phAppList.Visible = false; btnPhAppRemove.Visible = btnPhAppRestore.Visible = btnPhAppSuggest.Visible = false;
            lblPhApps.Text = "Apple doesn't let a PC see or remove iPhone apps.\n\nOn the iPhone: Settings > General > iPhone Storage lists apps by size.\n" +
                             "  •  \"Offload App\" removes the app but keeps its documents and data; tap it again to reinstall.\n" +
                             "  •  \"Offload Unused Apps\" (same screen) does this by itself for apps you don't open.";
            if (f.Error == "locked")
            {
                lblPhone.Text = f.Name + " found, but it is locked";
                lblPhNote.Text = "Unlock the iPhone, tap \"Trust\" and enter your passcode, then click \"Look for phone again\".";
                SetPhoneBar(null, 0, 0);
            }
            else
            {
                lblPhone.Text = f.Name + (f.Total > 0 ? "   ·   " + Fs.Fmt(f.Total - f.Free) + " used of " + Fs.Fmt(f.Total) + ", " + Fs.Fmt(f.Free) + " free" : "");
                lblPhNote.Text = "iPhone: Elixar can move photos and videos to this PC. Everything else is cleaned on the iPhone itself (Clean tab).";
                SetPhoneBar(f.Total > 0 ? new List<StorageSplit> { new StorageSplit { Label = "Used", Bytes = f.Total - f.Free } } : null, f.Total, f.Free);
            }
            phTips.BeginUpdate(); phTips.Items.Clear();
            foreach (var tip in new[]
            {
                new[] { "Photos > Albums > Recently Deleted > Select > Delete All", "Photos you deleted stay here 30 days and still take space" },
                new[] { "Settings > General > iPhone Storage > Offload Unused Apps", "Removes apps you don't use, keeps their data" },
                new[] { "Settings > Messages > Keep Messages > 1 Year", "Old texts with photos and videos; iPhone Storage > Messages shows big attachments" },
                new[] { "WhatsApp > Settings > Storage and data > Manage storage", "Big chats and forwarded videos, biggest first" },
                new[] { "Telegram > Settings > Data and Storage > Storage Usage > Clear", "Telegram's cache; everything stays in its cloud" },
                new[] { "Settings > Apps > Safari > Clear History and Website Data", "Browser cache" },
                new[] { "Settings > Photos > Optimize iPhone Storage (with iCloud Photos)", "Keeps small copies on the phone, full photos in iCloud" },
                new[] { "Spotify, Netflix, YouTube, Podcasts: remove downloads", "Offline music and videos are often several GB" },
                new[] { "Settings > General > iPhone Storage > iOS update file > Delete", "A downloaded update you haven't installed yet" },
            })
                phTips.Items.Add(new ListViewItem(tip));
            phTips.EndUpdate();
            if (files != null) ShowBigFiles(files);
            else if (f.Error == null) { phoneTabs.SelectedTab = pagePhBig; FindBigFiles(); }
            UpdatePhBig();
            FirePhoneDone();
        }
    }
}
