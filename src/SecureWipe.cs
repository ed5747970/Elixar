// Tools > Secure wipe: makes deleted files impossible to recover.
//  - Wipe free space: fills the drive's free space with zeros (or random, random, zeros) in files that Windows deletes by
//    itself when they are closed - even if Elixar is stopped halfway, nothing is left behind. Only free space is written.
//    On an SSD overwriting doesn't reach the old data (the SSD writes elsewhere), so Windows' TRIM runs instead.
//  - Shred files: overwrites the files you pick, then renames and deletes them. Never Windows', apps' or Elixar's own files,
//    never a link, and only after the file is held open and its real location checked.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class WipeDrive
    {
        public string Root = "", Name = "", Media = "";   // Media: "SSD", "HDD" or "" (not known: a USB stick, a card, a virtual disk)
        public long Free, Total; public bool System;
        public bool Ssd { get { return Media == "SSD"; } }
        public override string ToString()
        {
            return Root.TrimEnd('\\') + " " + Name + "  (" + (Media.Length > 0 ? Media : "drive") + ")  \u00b7  " + Fs.Fmt(Free) + " free";
        }
    }

    public class WipeResult { public long Written; public int Passes; public bool Stopped, Trimmed; public string Error; }

    public static class SecureWipe
    {
        public const string FolderName = "Elixar wipe (deletes itself)";
        public const long SystemReserve = 512L << 20;   // the Windows drive keeps this much free during a wipe
        public static long ChunkBytes = 1L << 30;       // one file per GB (FAT32 allows up to 4 GB)
        public static Func<string, string> MediaOverride;   // self-test: root -> "SSD" / "HDD" / ""
        public static Func<string, long> FreeOverride;      // self-test: root -> free bytes
        public static Func<string, string, int> RunOverride;  // self-test: (exe, args) -> exit code

        // ---------- drives ----------
        public static Func<List<WipeDrive>> DrivesOverride;   // previews
        public static List<WipeDrive> Drives()
        {
            if (DrivesOverride != null) return DrivesOverride();
            var media = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (MediaOverride == null && Win.IsWindows)
                try
                {
                    foreach (var d in DiskHealth.Read().Disks)
                        foreach (var l in (d.Letters ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length == 1))
                            media[l] = d.Solid ? "SSD" : d.Spinning ? "HDD" : "";
                }
                catch { }
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows), sys = string.IsNullOrEmpty(win) ? "/" : Path.GetPathRoot(win);
            var list = new List<WipeDrive>();
            foreach (var di in DriveInfo.GetDrives())
            {
                try
                {
                    if (!di.IsReady || (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable)) continue;
                    string root = di.RootDirectory.FullName, m;
                    if (MediaOverride != null) m = MediaOverride(root); else if (!media.TryGetValue(root.Substring(0, 1), out m)) m = "";
                    list.Add(new WipeDrive { Root = root, Name = di.VolumeLabel ?? "", Media = m ?? "", Free = di.AvailableFreeSpace, Total = di.TotalSize,
                                             System = string.Equals(root, sys, StringComparison.OrdinalIgnoreCase) });
                }
                catch { }
            }
            return list;
        }

        static long FreeOf(string root) { return FreeOverride != null ? FreeOverride(root) : new DriveInfo(root).AvailableFreeSpace; }

        static int Run(string exe, string args)
        {
            if (RunOverride != null) return RunOverride(exe, args);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Win.SystemExe(exe), args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(60 * 60 * 1000)) { try { p.Kill(); } catch { } ChangeLog.Command(exe, args, "timed out", -1); return -1; }
                    ChangeLog.Command(exe, args, (o.Result + " " + e.Result).Trim(), p.ExitCode);
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { ChangeLog.Command(exe, args, ex.Message, -1); return -1; }
        }

        // TRIM: Windows tells the SSD which blocks are free, and the SSD erases them
        public static bool Trim(string root)
        {
            string letter = root.Substring(0, 1).ToUpperInvariant();
            if (!char.IsLetter(letter[0])) return false;
            return Run("defrag.exe", letter + ": /L") == 0;
        }

        // ---------- wipe free space ----------
        // passes: 1 = zeros; 3 = random, random, zeros
        public static WipeResult WipeFree(WipeDrive d, int passes, Action<long, long, int> progress, CancellationToken tok)
        {
            var r = new WipeResult { Passes = passes };
            if (!Policy.Allowed("wipe")) { r.Error = OffText; return r; }   // checked here too, whoever asks
            if (d.Ssd)
            {
                r.Trimmed = Trim(d.Root);
                using (ChangeLog.In("Secure wipe"))
                    ChangeLog.Add("Settings", "Ran TRIM on " + d.Root.TrimEnd('\\') + " (SSD)", d.Root, "", "", "On an SSD, TRIM makes the SSD erase deleted data", !r.Trimmed);
                return r;
            }
            long reserve = d.System ? SystemReserve : 0;
            for (int pass = 1; pass <= passes && !tok.IsCancellationRequested && r.Error == null; pass++)
            {
                bool random = passes > 1 && pass < passes;
                int p = pass;
                r.Written += Fill(d.Root, random, reserve, (w, total) => { if (progress != null) progress(w, total, p); }, tok, out r.Error);
            }
            r.Stopped = tok.IsCancellationRequested;
            using (ChangeLog.In("Secure wipe"))
                ChangeLog.Add("Settings", (r.Stopped ? "Stopped wiping " : "Wiped the free space of ") + d.Root.TrimEnd('\\'),
                              d.Root, "", Fs.Fmt(r.Written) + " overwritten", (passes == 1 ? "once with zeros" : passes + " times") + (r.Error != null ? " - " + r.Error : ""), r.Error != null);
            return r;
        }

        // fills the free space with files Windows deletes when they are closed; returns the bytes written
        static long Fill(string root, bool random, long reserve, Action<long, long> progress, CancellationToken tok, out string error)
        {
            error = null;
            string dir = Path.Combine(root, FolderName);
            var open = new List<FileStream>();
            long written = 0, total = Math.Max(1, FreeOf(root) - reserve);
            var buf = new byte[4 << 20];
            try
            {
                Directory.CreateDirectory(dir);
                try { new DirectoryInfo(dir).Attributes |= FileAttributes.Hidden; } catch { }
                bool full = false;
                for (int n = 0; !full && !tok.IsCancellationRequested; n++)
                {
                    if (FreeOf(root) - reserve < buf.Length) break;
                    if (random) using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf);
                    FileStream fs;
                    try { fs = new FileStream(Path.Combine(dir, n + ".tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough | FileOptions.DeleteOnClose); }
                    catch (IOException) { break; }
                    open.Add(fs);
                    long inFile = 0;
                    while (inFile < ChunkBytes && !tok.IsCancellationRequested)
                    {
                        if ((written & ((64L << 20) - 1)) == 0 && FreeOf(root) - reserve < buf.Length) { full = true; break; }
                        try { fs.Write(buf, 0, buf.Length); }
                        catch (IOException) { full = true; break; }   // the drive is full: done
                        inFile += buf.Length; written += buf.Length;
                        if (progress != null && (written & ((64L << 20) - 1)) == 0) progress(written, total);
                    }
                    try { fs.Flush(true); } catch (IOException) { full = true; }
                }
                if (progress != null) progress(Math.Min(written, total), total);
            }
            catch (Exception e) { error = e.Message; }
            finally
            {
                foreach (var fs in open) try { fs.Dispose(); } catch { }   // closing deletes each file
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            }
            return written;
        }

        // a wipe folder left by a power cut: its files are already gone (deleted on close); the empty folder goes too
        public static void CleanupLeftovers()
        {
            foreach (var di in DriveInfo.GetDrives())
                try
                {
                    if (!di.IsReady || (di.DriveType != DriveType.Fixed && di.DriveType != DriveType.Removable)) continue;
                    string dir = Path.Combine(di.RootDirectory.FullName, FolderName);
                    if (Directory.Exists(dir) && SafeDelete.RealPathMatches(dir)) SafeDelete.Delete(dir);
                }
                catch { }
        }

        // ---------- shred files ----------
        static IEnumerable<string> Forbidden()
        {
            Func<Environment.SpecialFolder, string> F = f => { try { return Environment.GetFolderPath(f); } catch { return ""; } };
            foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                                      Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                string p = F(f); if (!string.IsNullOrEmpty(p)) yield return p;
            }
            if (Win.IsWindows) { yield return Paths.Data; yield return Install.Dir; }
        }

        static bool Under(string path, string folder)
        {
            string a = Path.GetFullPath(path).TrimEnd('\\', '/'), b = Path.GetFullPath(folder).TrimEnd('\\', '/');
            var cmp = Win.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return a.Equals(b, cmp) || a.StartsWith(b + Path.DirectorySeparatorChar, cmp);
        }

        // null when the file may be shredded, otherwise why not
        public static string OffText { get { return Policy.ByCompany("wipe") ? "Secure wipe is turned off on company PCs. Ask your IT department." : "Secure wipe is turned off by the policy (Policy manager)."; } }

        public static string CanShred(string path)
        {
            if (!Policy.Allowed("wipe")) return OffText;
            try
            {
                string full = Path.GetFullPath(path);
                if (Protect.IsProtected(full)) return "one of Windows' or your main folders";
                foreach (var f in Forbidden()) if (Under(full, f)) return "Windows', an app's or Elixar's own file";
                if (!File.Exists(full)) return Directory.Exists(full) ? "a folder (pick the files inside)" : "not found";
                var a = File.GetAttributes(full);
                if ((a & FileAttributes.ReparsePoint) != 0) return "a link";
                if ((a & FileAttributes.System) != 0) return "a system file";
                if (!SafeDelete.RealPathMatches(full)) return "a link is in the way";
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        public static bool Shred(string path, int passes, out string error)
        {
            error = CanShred(path);
            if (error != null) return false;
            string full = Path.GetFullPath(path), dir = Path.GetDirectoryName(full);
            long size = 0;
            try
            {
                if ((File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) File.SetAttributes(full, File.GetAttributes(full) & ~FileAttributes.ReadOnly);
                string expected = Win.IsWindows ? SafeDelete.RealPath(full) : full;
                using (var fs = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                {
                    // held open with no sharing: check it is still the same file in the same place
                    if (Win.IsWindows && !SafeDelete.Same(SafeDelete.Final(fs.SafeFileHandle), expected)) { error = "a link is in the way"; return false; }
                    size = fs.Length;
                    var buf = new byte[1 << 20];
                    for (int pass = 1; pass <= passes; pass++)
                    {
                        if (passes > 1 && pass < passes) using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buf); else Array.Clear(buf, 0, buf.Length);
                        fs.Position = 0;
                        for (long left = size; left > 0; left -= buf.Length) fs.Write(buf, 0, (int)Math.Min(buf.Length, left));
                        fs.Flush(true);
                    }
                    fs.SetLength(0); fs.Flush(true);
                }
                // a new name, so not even the name is left behind, then deleted the link-safe way
                string hiddenName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp", hidden = Path.Combine(dir, hiddenName);
                // renamed through a handle that is checked for where the file really is (a link put in the way meanwhile is refused)
                if (Win.IsWindows) SafeFile.MoveInto(full, dir, hiddenName); else File.Move(full, hidden);
                bool gone = Win.IsWindows ? SafeDelete.Delete(hidden) : TryDelete(hidden);
                if (!gone) { error = "overwritten, but Windows didn't delete it: " + Path.GetFileName(hidden); return false; }
                using (ChangeLog.In("Secure wipe"))
                    ChangeLog.Add("Cleaning", "Shredded a file", full, Fs.Fmt(size), "", (passes == 1 ? "overwritten once" : "overwritten " + passes + " times") + ", then deleted (can't be undone)", false);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        static bool TryDelete(string p) { try { File.Delete(p); return true; } catch { return false; } }
    }

    // ============================ the Tools page ============================
    public partial class MainForm
    {
        TabPage pageWipe;
        ComboBox wipeDrive; RadioButton wipeOnce, wipeThree; Label wipeNote, wipeStatus; ProgressBar wipeBar; Button btnWipe, btnWipeStop, btnShredAdd, btnShred;
        ListView shredList; CancellationTokenSource wipeCts;
        public event Action WipeDone;
        public string WipeStatusText { get { return wipeStatus == null ? "" : wipeStatus.Text; } }

        void BuildWipe()
        {
            pageWipe = new TabPage("Secure wipe") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(5);
            for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 100 : 0));
            t.Controls.Add(Title("Secure wipe"), 0, 0);
            t.Controls.Add(Note("For when you sell or give away a PC or a disk, or deleted something private: makes deleted files impossible to recover. " +
                                "Only free space and the files you pick are overwritten. Shredding can't be undone."), 0, 1);

            // wipe free space
            var g1 = new GroupBox { Text = "Wipe free space", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(S(10), S(6), S(10), S(8)), Margin = new Padding(0, 0, 0, S(10)) };
            var f1 = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var dr = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            wipeDrive = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(420), Margin = new Padding(S(4), S(2), S(8), 0) };
            var refresh = MakeButton("Refresh");
            dr.Controls.AddRange(new Control[] { new Label { AutoSize = true, Text = "Drive", Margin = new Padding(0, S(6), 0, 0) }, wipeDrive, refresh });
            wipeOnce = new RadioButton { AutoSize = true, Checked = true, Text = "Wipe once (zeros) - recommended: enough for today's disks", Margin = new Padding(S(2), S(6), 0, 0) };
            wipeThree = new RadioButton { AutoSize = true, Text = "Wipe 3 times (random, random, zeros) - for older or very sensitive disks; takes 3 times as long", Margin = new Padding(S(2), S(2), 0, 0) };
            wipeNote = new Label { AutoSize = true, MaximumSize = new Size(S(900), 0), ForeColor = Color.FromArgb(150, 90, 0), Margin = new Padding(S(2), S(6), 0, 0), Text = "" };
            wipeBar = new ProgressBar { Width = S(520), Height = S(12), Maximum = 1000, Visible = false, Margin = new Padding(S(2), S(8), 0, 0) };
            var r1 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
            btnWipe = MakeButton("Wipe free space..."); btnWipe.Font = new Font("Segoe UI Semibold", 9f);
            btnWipeStop = MakeButton("Stop"); btnWipeStop.Enabled = false;
            r1.Controls.AddRange(new Control[] { btnWipe, btnWipeStop });
            f1.Controls.AddRange(new Control[] { dr, wipeOnce, wipeThree, wipeNote, wipeBar, r1 });
            g1.Controls.Add(f1);

            // shred files
            var g2 = new GroupBox { Text = "Shred files", Dock = DockStyle.Fill, Padding = new Padding(S(10), S(6), S(10), S(8)) };
            var t2 = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            t2.RowStyles.Add(new RowStyle(SizeType.AutoSize)); t2.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); t2.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t2.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(900), 0), ForeColor = Color.DimGray, Margin = new Padding(S(1), S(2), 0, S(6)),
                                        Text = "Pick files (or drag them here): each is overwritten, renamed and deleted. Windows', apps' and Elixar's own files are refused. " +
                                               "On an SSD the file is overwritten and deleted, and TRIM runs after." }, 0, 0);
            shredList = new ListView { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, AllowDrop = true, MinimumSize = new Size(0, S(90)) };
            shredList.Columns.Add("File", S(560)); shredList.Columns.Add("Size", S(90), HorizontalAlignment.Right); shredList.Columns.Add("", S(300));
            var r2 = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
            btnShredAdd = MakeButton("Add files..."); var btnShredClear = MakeButton("Clear list");
            btnShred = MakeButton("Shred..."); btnShred.Font = new Font("Segoe UI Semibold", 9f); btnShred.Enabled = false;
            r2.Controls.AddRange(new Control[] { btnShredAdd, btnShredClear, btnShred });
            t2.Controls.Add(shredList, 0, 1); t2.Controls.Add(r2, 0, 2);
            g2.Controls.Add(t2);

            wipeStatus = new Label { AutoSize = true, MaximumSize = new Size(S(1000), 0), Margin = new Padding(S(1), S(6), 0, 0), Text = "" };
            t.Controls.Add(g1, 0, 2); t.Controls.Add(g2, 0, 3); t.Controls.Add(wipeStatus, 0, 4);
            pageWipe.Controls.Add(t);

            refresh.Click += (o, e) => LoadWipeDrives();
            wipeDrive.SelectedIndexChanged += (o, e) => WipeDriveNote();
            btnWipe.Click += (o, e) => StartWipe();
            btnWipeStop.Click += (o, e) => { if (wipeCts != null) { wipeCts.Cancel(); btnWipeStop.Enabled = false; wipeStatus.Text = Lang.T("Stopping... (the wipe files delete themselves)"); } };
            btnShredAdd.Click += (o, e) =>
            {
                using (var d = new OpenFileDialog { Multiselect = true, Title = Lang.T("Files to shred") })
                    if (d.ShowDialog(this) == DialogResult.OK) AddShred(d.FileNames);
            };
            btnShredClear.Click += (o, e) => { shredList.Items.Clear(); ShredSel(); };
            shredList.DragEnter += (o, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            shredList.DragDrop += (o, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) AddShred(f); };
            btnShred.Click += (o, e) => StartShred();
        }

        bool wipeLoaded;
        void OnWipeTab() { if (!wipeLoaded) LoadWipeDrives(); }

        void LoadWipeDrives()
        {
            wipeLoaded = true; wipeDrive.Items.Clear(); wipeNote.Text = Lang.T("Reading the drives...");
            Task.Factory.StartNew(() => SecureWipe.Drives()).ContinueWith(t => UI(() =>
            {
                wipeDrive.Items.Clear();
                if (!t.IsFaulted) foreach (var d in t.Result) wipeDrive.Items.Add(d);
                // a data drive first: wiping the Windows drive is rarely what's needed
                int pick = 0; for (int i = 0; i < wipeDrive.Items.Count; i++) if (!((WipeDrive)wipeDrive.Items[i]).System) { pick = i; break; }
                if (wipeDrive.Items.Count > 0) wipeDrive.SelectedIndex = pick; else wipeNote.Text = Lang.T("No drive found.");
            }));
        }

        WipeDrive CurWipe { get { return wipeDrive.SelectedItem as WipeDrive; } }

        void WipeDriveNote()
        {
            var d = CurWipe; if (d == null) return;
            wipeOnce.Enabled = wipeThree.Enabled = !d.Ssd;
            btnWipe.Text = Lang.T(d.Ssd ? "Run TRIM on " + d.Root.TrimEnd('\\') : "Wipe free space of " + d.Root.TrimEnd('\\') + "...");
            wipeNote.Text = Lang.T(d.Ssd
                ? d.Root.TrimEnd('\\') + " is an SSD. On an SSD, overwriting doesn't reach the old data (the SSD writes somewhere else), so Elixar runs Windows' TRIM instead: it tells the SSD to erase what was deleted. To give an SSD away, use its maker's \"Secure Erase\" or Windows' Reset this PC > Remove everything > Clean the drive."
                : (d.Media == "" ? "Elixar can't tell what kind of drive this is. On USB sticks and memory cards, overwriting may not reach every old copy. " : "") +
                  "About " + Math.Max(1, (int)Math.Round(d.Free / (100.0 * (1 << 20)) / 60)) + " minutes per pass for " + Fs.Fmt(d.Free) + "." +
                  (d.System ? " This is the Windows drive: " + Fs.Fmt(SecureWipe.SystemReserve) + " stays free so Windows keeps working." : ""));
        }

        public void StartWipe()
        {
            var d = CurWipe; if (d == null || wipeCts != null || Policy.Blocked(this, "wipe")) return;
            int passes = wipeThree.Checked ? 3 : 1;
            if (!testMode && !d.Ssd && Lang.Box(this, "Overwrite all free space on " + d.Root.TrimEnd('\\') + " " + (passes == 1 ? "once" : "3 times") + "?\n\nYour files are not touched. " +
                         "The drive looks full while it runs; the wipe files delete themselves when it ends (also if it's stopped).", "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            wipeCts = new CancellationTokenSource(); var tok = wipeCts.Token;
            btnWipe.Enabled = false; btnWipeStop.Enabled = !d.Ssd; wipeBar.Visible = !d.Ssd; wipeBar.Value = 0;
            wipeStatus.Text = Lang.T(d.Ssd ? "Running TRIM..." : "Wiping... you can keep working.");
            Task.Factory.StartNew(() => SecureWipe.WipeFree(d, passes, (w, total, pass) => UI(() =>
            {
                wipeBar.Value = (int)Math.Max(0, Math.Min(1000, ((pass - 1) + (double)w / total) * 1000 / passes));
                wipeStatus.Text = Lang.T("Pass " + pass + " of " + passes + ": " + Fs.Fmt(w) + " of " + Fs.Fmt(total));
            }), tok), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                wipeCts = null; btnWipe.Enabled = true; btnWipeStop.Enabled = false; wipeBar.Visible = false;
                var r = t.IsFaulted ? new WipeResult { Error = t.Exception.GetBaseException().Message } : t.Result;
                wipeStatus.Text = Lang.T(d.Ssd ? (r.Trimmed ? "TRIM ran on " + d.Root.TrimEnd('\\') + ": the SSD erases what was deleted." : "TRIM didn't run: " + (r.Error ?? "Windows refused it.")) :
                                         r.Error != null ? "The wipe stopped: " + r.Error :
                                         r.Stopped ? "Stopped after " + Fs.Fmt(r.Written) + ". The wipe files are gone." :
                                         "Done: the free space of " + d.Root.TrimEnd('\\') + " was overwritten" + (passes == 1 ? "" : " " + passes + " times") + " (" + Fs.Fmt(r.Written) + "). Deleted files there can't be recovered.");
                Activity.Add("wipe", wipeStatus.Text, 0);
                LoadWipeDrives();
                if (WipeDone != null) WipeDone();
            }));
        }

        public void AddShred(IEnumerable<string> files)
        {
            foreach (var f in files)
            {
                if (shredList.Items.Cast<ListViewItem>().Any(i => string.Equals((string)i.Tag, f, StringComparison.OrdinalIgnoreCase))) continue;
                string why = SecureWipe.CanShred(f);
                long size = 0; try { size = new FileInfo(f).Length; } catch { }
                var it = new ListViewItem(f) { Tag = f, UseItemStyleForSubItems = false };
                it.SubItems.Add(why == null ? Fs.Fmt(size) : "");
                var s = it.SubItems.Add(why == null ? Lang.T("ready") : Lang.T("refused: " + why));
                s.ForeColor = why == null ? Color.FromArgb(46, 125, 50) : Color.FromArgb(198, 40, 40);
                shredList.Items.Add(it);
            }
            ShredSel();
        }

        public int ShredReady { get { return shredList.Items.Cast<ListViewItem>().Count(i => SecureWipe.CanShred((string)i.Tag) == null); } }

        void ShredSel()
        {
            int n = ShredReady;
            btnShred.Enabled = n > 0 && wipeCts == null;
            btnShred.Text = Lang.T(n > 0 ? "Shred " + n + " file" + (n == 1 ? "" : "s") + "..." : "Shred...");
        }

        public void StartShred()
        {
            if (Policy.Blocked(this, "wipe")) return;
            var files = shredList.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).Where(f => SecureWipe.CanShred(f) == null).ToList();
            if (files.Count == 0) return;
            int passes = wipeThree.Checked ? 3 : 1;
            if (!testMode && Lang.Box(this, "Shred " + files.Count + " file" + (files.Count == 1 ? "" : "s") + "?\n\nThey are overwritten and deleted. This can't be undone: they don't go to the Recycle Bin or Undo.",
                                      "Elixar", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            btnShred.Enabled = false; btnShredAdd.Enabled = false; wipeStatus.Text = Lang.T("Shredding...");
            Task.Factory.StartNew(() =>
            {
                int ok = 0; var errors = new List<string>(); var ssdRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var media = SecureWipe.Drives().GroupBy(x => x.Root, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Ssd, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    string err;
                    if (SecureWipe.Shred(f, passes, out err)) { ok++; bool ssd; string root = Path.GetPathRoot(Path.GetFullPath(f)) ?? ""; if (media.TryGetValue(root, out ssd) && ssd) ssdRoots.Add(root); }
                    else errors.Add(Path.GetFileName(f) + ": " + err);
                }
                foreach (var r in ssdRoots) SecureWipe.Trim(r);
                return Tuple.Create(ok, errors, ssdRoots.Count > 0);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                btnShredAdd.Enabled = true;
                if (t.IsFaulted) wipeStatus.Text = Lang.T("Shredding stopped: " + t.Exception.GetBaseException().Message);
                else
                {
                    var r = t.Result;
                    wipeStatus.Text = Lang.T("Shredded " + r.Item1 + " of " + files.Count + " file" + (files.Count == 1 ? "" : "s") + "." + (r.Item3 ? " TRIM ran on the SSD." : "") +
                                             (r.Item2.Count > 0 ? " Not shredded: " + string.Join("; ", r.Item2.Take(5)) : ""));
                    foreach (var it in shredList.Items.Cast<ListViewItem>().ToList()) if (!File.Exists((string)it.Tag)) shredList.Items.Remove(it);
                    Activity.Add("wipe", wipeStatus.Text, 0);
                }
                ShredSel();
                if (WipeDone != null) WipeDone();
            }));
        }
    }
}
