// Built-in and Store apps (Copilot, Xbox, News, ...): list, classify, and remove for the current user.
// Parts of Windows, frameworks, codecs, the Store itself, language packs and driver panels are locked.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class StoreApp
    {
        public string Name = "", FullName = "", Version = "", Publisher = "", Location = "", Signature = "";
        public bool Framework, NonRemovable;
        public string Friendly = "", Note = "";
        public string Level = "review";   // optional | review | keep
        public long Bytes = -1;
    }

    public static class StoreData
    {
        // name (exact, or prefix when it ends with '*') -> friendly name, level, note
        static readonly string[][] Known =
        {
            new[] { "Microsoft.Copilot", "Copilot", "optional", "AI assistant app. Windows works without it. Copilot in Edge and on the web keeps working; reinstall from the Store anytime." },
            new[] { "Microsoft.Windows.Ai.Copilot.Provider", "Copilot (older version)", "optional", "Older Copilot app part. Safe to remove." },
            new[] { "Microsoft.BingNews", "News", "optional", "News app. Safe to remove." },
            new[] { "Microsoft.BingWeather", "Weather", "optional", "Weather app. Safe to remove." },
            new[] { "Microsoft.BingSearch", "Bing Search", "optional", "Bing search app. Windows search keeps working." },
            new[] { "Microsoft.GetHelp", "Get Help", "optional", "Microsoft support app. Safe to remove." },
            new[] { "Microsoft.Getstarted", "Tips", "optional", "Windows tips app. Safe to remove." },
            new[] { "Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection", "optional", "Card games with ads. Safe to remove." },
            new[] { "Microsoft.People", "People", "optional", "Old contacts app. Safe to remove." },
            new[] { "Microsoft.WindowsFeedbackHub", "Feedback Hub", "optional", "Sends feedback to Microsoft. Safe to remove." },
            new[] { "Microsoft.ZuneVideo", "Movies & TV", "optional", "Old video app; Media Player plays videos too." },
            new[] { "Clipchamp.Clipchamp", "Clipchamp", "optional", "Video editor. Safe to remove if you don't edit videos." },
            new[] { "Microsoft.Todos", "Microsoft To Do", "optional", "Task list app. Your lists stay in your Microsoft account." },
            new[] { "Microsoft.PowerAutomateDesktop", "Power Automate", "optional", "Automation tool. Safe to remove if you don't use it." },
            new[] { "Microsoft.WindowsMaps", "Maps", "optional", "Maps app. Safe to remove." },
            new[] { "Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office hub)", "optional", "Launcher for Office. Installed Office apps keep working." },
            new[] { "Microsoft.549981C3F5F10", "Cortana", "optional", "Retired assistant. Safe to remove." },
            new[] { "Microsoft.Windows.DevHome", "Dev Home", "optional", "Developer dashboard. Safe to remove." },
            new[] { "Microsoft.MixedReality.Portal", "Mixed Reality Portal", "optional", "Only for VR headsets." },
            new[] { "Microsoft.Microsoft3DViewer", "3D Viewer", "optional", "3D model viewer. Safe to remove." },
            new[] { "Microsoft.SkypeApp", "Skype", "optional", "Skype has been retired. Safe to remove." },
            new[] { "Microsoft.MicrosoftJournal", "Journal", "optional", "Pen note-taking app. Safe to remove." },
            new[] { "Microsoft.Wallet", "Wallet", "optional", "Old payments app. Safe to remove." },
            new[] { "microsoft.windowscommunicationsapps", "Mail and Calendar (old)", "optional", "Retired Mail and Calendar. Use Outlook or the web instead." },
            new[] { "Microsoft.XboxSpeechToTextOverlay", "Xbox speech-to-text", "optional", "Speech-to-text for Game Bar chat." },
            new[] { "Microsoft.GamingApp", "Xbox", "review", "Needed for PC Game Pass and Xbox PC games. Remove only if you don't use them." },
            new[] { "Microsoft.XboxApp", "Xbox Console Companion", "optional", "Old Xbox app. Safe to remove." },
            new[] { "Microsoft.XboxGamingOverlay", "Xbox Game Bar", "review", "Win+G overlay and game recording. Remove if you don't record games." },
            new[] { "Microsoft.XboxGameOverlay", "Xbox Game Bar (part)", "review", "Older Game Bar part. Remove together with Game Bar." },
            new[] { "Microsoft.XboxIdentityProvider", "Xbox sign-in", "review", "Xbox sign-in used by some PC games (Minecraft, Forza, Sea of Thieves). Keep if you play those." },
            new[] { "Microsoft.Xbox.TCUI", "Xbox friends and invites", "review", "Xbox friends and invites inside some games. Keep if you play Xbox-connected games." },
            new[] { "Microsoft.YourPhone", "Phone Link", "review", "Connects your phone to Windows. Remove if you don't use it." },
            new[] { "Microsoft.ZuneMusic", "Media Player", "review", "Windows' music and video player. Remove only if you use another player." },
            new[] { "Microsoft.WindowsSoundRecorder", "Sound Recorder", "review", "Voice recorder app." },
            new[] { "Microsoft.WindowsAlarms", "Clock", "review", "Alarms, timers and Focus sessions." },
            new[] { "Microsoft.MicrosoftStickyNotes", "Sticky Notes", "review", "Your notes are kept in your Microsoft account if you are signed in." },
            new[] { "MicrosoftCorporationII.QuickAssist", "Quick Assist", "review", "Remote help from another person. Remove if nobody helps you remotely." },
            new[] { "Microsoft.OutlookForWindows", "Outlook (new)", "review", "New Outlook mail app. Remove if you use another mail app." },
            new[] { "MSTeams", "Microsoft Teams", "review", "Teams. Remove if you don't use it for work or school." },
            new[] { "MicrosoftTeams", "Microsoft Teams (personal)", "review", "Teams personal chat. Remove if you don't use it." },
            new[] { "SpotifyAB.SpotifyMusic", "Spotify", "review", "Remove if you don't use Spotify." },
            new[] { "Disney.*", "Disney+", "optional", "Preinstalled streaming app. Safe to remove if you don't use it." },
            new[] { "AmazonVideo.PrimeVideo", "Prime Video", "optional", "Preinstalled streaming app. Safe to remove if you don't use it." },
            new[] { "4DF9E0F8.Netflix", "Netflix", "review", "Remove if you don't use Netflix." },
            new[] { "king.com.*", "Candy Crush (King games)", "optional", "Preinstalled game with ads. Safe to remove." },
            new[] { "Facebook.*", "Facebook / Instagram", "review", "Remove if you don't use it." },
            new[] { "BytedancePte.Ltd.TikTok", "TikTok", "review", "Remove if you don't use it." },
        };

        // never removable here: the Store, security, codecs, frameworks, language packs, Windows parts and driver panels
        static readonly string[] KeepExact =
        {
            "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
            "Microsoft.Windows.Photos", "Microsoft.Paint", "Microsoft.WindowsNotepad", "Microsoft.ScreenSketch", "Microsoft.WindowsCalculator",
            "Microsoft.WindowsTerminal", "Microsoft.HEIFImageExtension", "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions",
            "Microsoft.WebMediaExtensions", "Microsoft.WebpImageExtension", "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension",
            "Microsoft.MPEG2VideoExtension", "Microsoft.AVCEncoderVideoExtension", "Microsoft.Winget.Source", "Microsoft.ApplicationCompatibilityEnhancements",
            "Microsoft.OneDriveSync", "Microsoft.WidgetsPlatformRuntime"
        };
        static readonly string[] KeepPrefix =
        {
            "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.", "Microsoft.WindowsAppRuntime", "Microsoft.Services.Store",
            "Microsoft.LanguageExperiencePack", "Microsoft.MicrosoftEdge", "Microsoft.Edge", "Microsoft.WebView2", "Microsoft.Windows.",
            "MicrosoftWindows.", "Windows.", "windows.", "Microsoft.AAD.", "Microsoft.AccountsControl", "Microsoft.CredDialogHost",
            "Microsoft.ECApp", "Microsoft.LockApp", "Microsoft.Win32WebViewHost", "Microsoft.AsyncTextService", "Microsoft.BioEnrollment",
            "NVIDIACorp.", "RealtekSemiconductorCorp.", "AppUp.Intel", "AdvancedMicroDevicesInc", "DolbyLaboratories.", "WavesAudio.",
            "SynapticsIncorporated.", "ELANMicroelectronicsCorpo.", "IntelCorp.", "AD2F1837.", "E046963F.", "DellInc.", "Lenovo", "HP", "ASUS", "Acer"
        };

        static bool Matches(string pattern, string name)
        {
            return pattern.EndsWith("*")
                ? name.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }

        public static void Classify(StoreApp a)
        {
            var k = Known.FirstOrDefault(x => Matches(x[0], a.Name));
            if (k != null && !a.Framework && !a.NonRemovable && !a.Signature.Equals("System", StringComparison.OrdinalIgnoreCase))
            {
                a.Friendly = k[1]; a.Level = k[2]; a.Note = k[3];
                return;
            }
            a.Friendly = Pretty(a.Name);
            if (a.Framework) { a.Level = "keep"; a.Note = "Shared component other apps need."; return; }
            if (a.NonRemovable || a.Signature.Equals("System", StringComparison.OrdinalIgnoreCase)) { a.Level = "keep"; a.Note = "Part of Windows."; return; }
            if (KeepExact.Any(x => x.Equals(a.Name, StringComparison.OrdinalIgnoreCase)) ||
                KeepPrefix.Any(p => a.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                a.Level = "keep";
                a.Note = a.Name.IndexOf("LanguageExperiencePack", StringComparison.OrdinalIgnoreCase) >= 0 ? "Windows display language. Keep." :
                         a.Name.IndexOf("Extension", StringComparison.OrdinalIgnoreCase) >= 0 ? "Picture or video format support. Keep." :
                         a.Name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) || a.Name.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || a.Name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
                            ? "Part of Windows or needed by it. Keep." : "Driver control panel for your hardware. Keep.";
                return;
            }
            a.Level = "review";
            a.Note = "Store app. Remove if you don't use it; you can reinstall it from the Microsoft Store.";
        }

        public static string Pretty(string name)
        {
            string n = name;
            int dot = n.IndexOf('.');
            if (dot > 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
            return n.Replace('.', ' ');
        }

        // tab-separated: Name, PackageFullName, Version, Publisher, IsFramework, NonRemovable, SignatureKind, InstallLocation
        public const string ListScript =
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
            "Get-AppxPackage | ForEach-Object { @($_.Name, $_.PackageFullName, $_.Version, $_.Publisher, $_.IsFramework, $_.NonRemovable, $_.SignatureKind, $_.InstallLocation) -join \"`t\" }";

        public static List<StoreApp> ParseList(string output)
        {
            var list = new List<StoreApp>();
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var p = raw.TrimEnd('\r').Split('\t');
                if (p.Length < 8 || p[0].Length == 0) continue;
                var a = new StoreApp
                {
                    Name = p[0], FullName = p[1], Version = p[2], Publisher = p[3],
                    Framework = p[4].Equals("True", StringComparison.OrdinalIgnoreCase),
                    NonRemovable = p[5].Equals("True", StringComparison.OrdinalIgnoreCase),
                    Signature = p[6], Location = p[7].Trim()
                };
                Classify(a);
                list.Add(a);
            }
            // one row per app name (a package can be installed for several architectures)
            return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        }

        public static string RemoveScript(string fullName)
        {
            // a package full name is letters, digits, dots, dashes, underscores and tildes; anything else is refused
            if (!System.Text.RegularExpressions.Regex.IsMatch(fullName ?? "", "^[A-Za-z0-9._~-]{1,200}$")) return "'ERROR: not a valid package name'";
            return "$ErrorActionPreference = 'Stop'; try { Remove-AppxPackage -Package " + Sys.PsQuote(fullName) + "; 'OK' } catch { 'ERROR: ' + $_.Exception.Message }";
        }

        public static string RunPsScriptFor(string script) { return Sys.PsSafe(script); }

        public static string RunPs(string script, int timeoutMs)
        {
            var psi = new ProcessStartInfo(Win.SystemExe("powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(RunPsScriptFor(script))))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } ChangeLog.PowerShell(script, "timed out"); return "ERROR: timed out"; }
                string err = e.Result.Trim();
                string r = o.Result + (err.Length > 0 ? "\n" + err : "");
                ChangeLog.PowerShell(script, r);
                return r;
            }
        }

        public static List<StoreApp> ReadInstalled(bool measure)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return new List<StoreApp>();
            var apps = ParseList(RunPs(ListScript, 120000));
            if (measure)
                foreach (var a in apps.Where(x => x.Level != "keep"))
                {
                    try { if (a.Location.Length > 3 && Directory.Exists(a.Location)) a.Bytes = Fs.SizeOf(a.Location); } catch { }
                }
            return apps;
        }
    }

    public class StoreSorter : System.Collections.IComparer
    {
        public int Compare(object x, object y)
        {
            var a = (StoreApp)((ListViewItem)x).Tag;
            var b = (StoreApp)((ListViewItem)y).Tag;
            int r = b.Bytes.CompareTo(a.Bytes);
            return r != 0 ? r : string.Compare(a.Friendly, b.Friendly, StringComparison.OrdinalIgnoreCase);
        }
    }

    public partial class MainForm
    {
        public event Action StoreDone;
        TabControl appsTabs;
        TabPage pagePrograms, pageStore;
        ListView storeList;
        Label storeHeader, storeStatus;
        Button btnStoreRefresh, btnStoreRemove, btnStoreOptional, btnStoreNone, btnStoreShop;
        CheckBox chkStoreRestore;
        bool storeBusy, storeLoaded;
        readonly Dictionary<string, ListViewGroup> storeGroups = new Dictionary<string, ListViewGroup>();
        List<StoreApp> storeCache;
        public int StoreCount { get { return storeList == null ? 0 : storeList.Items.Count; } }

        static string StoreLevelTitle(string l)
        {
            return l == "optional" ? "Optional - safe to remove" : l == "review" ? "Your decision - read the note" : "Keep - needed by Windows (locked)";
        }

        void BuildStoreApps()
        {
            pageStore = new TabPage("Built-in and Store apps") { BackColor = Color.FromArgb(246, 248, 251) };
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(S(12), S(10), S(12), S(10)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var head = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            storeHeader = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Text = "Built-in and Store apps", Margin = new Padding(0, S(2), 0, 0) };
            btnStoreRefresh = MakeButton("Refresh");
            head.Controls.Add(storeHeader, 0, 0);
            head.Controls.Add(btnStoreRefresh, 1, 0);

            storeStatus = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(S(1), 0, 0, S(8)), MaximumSize = new Size(S(1000), 0),
                Text = "Apps that came with Windows or from the Microsoft Store, like Copilot and Xbox. Removed apps can be reinstalled from the Microsoft Store. Nothing is ticked for you."
            };

            storeList = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill, ShowItemToolTips = true };
            storeList.Columns.Add("App", S(260));
            storeList.Columns.Add("Size", S(85), HorizontalAlignment.Right);
            storeList.Columns.Add("What happens if you remove it", S(560));
            storeList.Columns.Add("Package", S(300));
            foreach (var l in new[] { "optional", "review", "keep" })
            {
                var g = new ListViewGroup(StoreLevelTitle(l), HorizontalAlignment.Left);
                storeGroups[l] = g;
                storeList.Groups.Add(g);
            }
            storeList.ListViewItemSorter = new StoreSorter();
            // locked rows can never be ticked
            storeList.ItemCheck += (o, e) =>
            {
                var a = (StoreApp)storeList.Items[e.Index].Tag;
                if (a.Level == "keep" && e.NewValue == CheckState.Checked) e.NewValue = CheckState.Unchecked;
            };
            storeList.ItemChecked += (o, e) => UpdateStoreButton();
            storeList.SelectedIndexChanged += (o, e) =>
            {
                if (storeList.SelectedItems.Count == 0) return;
                var a = (StoreApp)storeList.SelectedItems[0].Tag;
                storeStatus.Text = a.Friendly + ": " + a.Note;
            };

            chkStoreRestore = new CheckBox { AutoSize = true, Checked = true, Text = "Create a restore point first (recommended)", Margin = new Padding(S(2), S(8), 0, 0) };

            var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
            btnStoreRemove = MakeButton("Remove selected...");
            btnStoreRemove.Font = new Font("Segoe UI Semibold", 9f);
            btnStoreOptional = MakeButton("Select all Optional");
            btnStoreNone = MakeButton("Select none");
            btnStoreShop = MakeButton("Open Microsoft Store");
            btnRow.Controls.AddRange(new Control[] { btnStoreRemove, btnStoreOptional, btnStoreNone, btnStoreShop });
            btnStoreRemove.Enabled = false;

            root.Controls.Add(head, 0, 0);
            root.Controls.Add(storeStatus, 0, 1);
            root.Controls.Add(storeList, 0, 2);
            root.Controls.Add(chkStoreRestore, 0, 3);
            root.Controls.Add(btnRow, 0, 4);
            pageStore.Controls.Add(root);

            btnStoreRefresh.Click += (o, e) => RefreshStoreApps();
            btnStoreOptional.Click += (o, e) => { foreach (ListViewItem r in storeList.Items) r.Checked = ((StoreApp)r.Tag).Level == "optional"; };
            btnStoreNone.Click += (o, e) => { foreach (ListViewItem r in storeList.Items) r.Checked = false; };
            btnStoreRemove.Click += (o, e) => RemoveStoreApps(storeList.CheckedItems.Cast<ListViewItem>().Select(r => (StoreApp)r.Tag).ToList());
            btnStoreShop.Click += (o, e) =>
            {
                try { Shell.Open("ms-windows-store://home"); }
                catch (Exception ex) { Log("Could not open the Microsoft Store: " + ex.Message); }
            };
        }

        void UpdateStoreButton()
        {
            int n = storeList.CheckedItems.Count;
            btnStoreRemove.Enabled = n > 0 && !testMode && !storeBusy;
            btnStoreRemove.Text = n > 0 ? "Remove " + n + " selected..." : "Remove selected...";
        }

        public void RefreshStoreApps()
        {
            if (storeBusy) return;
            storeBusy = true;
            storeLoaded = true;
            btnStoreRefresh.Enabled = false;
            storeList.Items.Clear();
            storeStatus.Text = "Reading built-in and Store apps...";
            var watch = Stopwatch.StartNew();
            Task.Factory.StartNew(() => StoreData.ReadInstalled(true), TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                storeBusy = false;
                btnStoreRefresh.Enabled = true;
                if (t.IsFaulted) storeStatus.Text = "Could not read the apps: " + t.Exception.GetBaseException().Message;
                else
                {
                    storeCache = t.Result;
                    ShowStoreApps(t.Result);
                    int opt = t.Result.Count(a => a.Level == "optional"), rev = t.Result.Count(a => a.Level == "review");
                    storeHeader.Text = "Built-in and Store apps   " + opt + " optional, " + rev + " your decision";
                    storeStatus.Text = "Updated " + DateTime.Now.ToString("HH:mm") + " (took " + (int)Math.Round(watch.Elapsed.TotalSeconds) + " s). " +
                        "Click an app to read what happens if you remove it. Locked apps (gray) are needed by Windows.";
                }
                UpdateStoreButton();
                if (StoreDone != null) StoreDone();
            }));
        }

        void ShowStoreApps(List<StoreApp> apps)
        {
            storeList.BeginUpdate();
            storeList.Items.Clear();
            foreach (var a in apps.Where(x => !(x.Level == "keep" && x.Framework)))   // frameworks are hidden: dozens of them, never removable
            {
                var lvi = new ListViewItem(a.Friendly) { Tag = a, UseItemStyleForSubItems = false, Group = storeGroups[a.Level], ToolTipText = a.Note };
                lvi.SubItems.Add(a.Bytes > 0 ? Fs.Fmt(a.Bytes) : a.Level == "keep" ? "" : "?");
                var note = lvi.SubItems.Add(a.Note);
                var pkg = lvi.SubItems.Add(a.Name);
                pkg.ForeColor = Color.DimGray;
                if (a.Level == "keep") { lvi.ForeColor = Color.Gray; note.ForeColor = Color.Gray; }
                else note.ForeColor = a.Level == "optional" ? SafeColor : ReviewColor;
                storeList.Items.Add(lvi);
            }
            storeList.Sort();
            storeList.EndUpdate();
            foreach (var l in storeGroups.Keys.ToList())
            {
                int n = storeList.Items.Cast<ListViewItem>().Count(r => ((StoreApp)r.Tag).Level == l);
                storeGroups[l].Header = StoreLevelTitle(l) + "   (" + n + ")";
            }
        }

        public void RemoveStoreApps(List<StoreApp> apps)
        {
            apps = apps.Where(a => a.Level != "keep").ToList();
            if (apps.Count == 0 || testMode || Policy.Blocked(this, "apps.remove")) return;
            string list = string.Join("\n", apps.Select(a => "  - " + a.Friendly + (a.Level == "review" ? "   (" + a.Note + ")" : "")));
            if (Lang.Box(this, "Remove these " + apps.Count + " apps for your account?\n\n" + list +
                    "\n\nYou can reinstall any of them from the Microsoft Store.", "Elixar",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            bool restore = chkStoreRestore.Checked;
            storeBusy = true;
            UpdateStoreButton();
            btnStoreRefresh.Enabled = false;
            Task.Factory.StartNew(() =>
            {
                if (restore)
                {
                    Log("Creating a restore point before removing apps...");
                    string r;
                    try
                    {
                        r = Sys.RunPowerShell("try { Checkpoint-Computer -Description 'Before Elixar removed apps' -RestorePointType APPLICATION_UNINSTALL -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue; " +
                            "if ($w) { 'Not created: ' + ($w -join ' ') } else { 'Restore point created.' } } catch { 'Not created: ' + $_.Exception.Message }", 5 * 60 * 1000);
                    }
                    catch (Exception ex) { r = "Not created: " + ex.Message; }
                    Log("  " + r);
                }
                long start = Sys.Free();
                int ok = 0;
                foreach (var a in apps)
                {
                    var app = a;
                    UI(() => storeStatus.Text = "Removing " + app.Friendly + "...");
                    string res;
                    try { res = StoreData.RunPs(StoreData.RemoveScript(app.FullName), 5 * 60 * 1000).Trim(); }
                    catch (Exception ex) { res = "ERROR: " + ex.Message; }
                    if (res.StartsWith("OK")) { ok++; Log("Removed " + app.Friendly + "."); }
                    else Log("Could not remove " + app.Friendly + ": " + res.Replace("ERROR: ", ""));
                }
                Log("Apps removed: " + ok + " of " + apps.Count + ". Freed " + Fs.Fmt(Math.Max(0, Sys.Free() - start)) + ".");
                return ok;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                storeBusy = false;
                int ok = t.IsFaulted ? 0 : t.Result;
                RefreshStoreApps();
                Lang.Box(this, "Removed " + ok + " of " + apps.Count + " apps." + (ok < apps.Count ? "\nThe log at the bottom of the Cleaner tab says why some could not be removed." : ""),
                    "Elixar", MessageBoxButtons.OK, ok == apps.Count ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }));
        }

        // ---------- which app owns a file or folder (used by Largest files and the drill-down) ----------
        List<AppEntry> programsIndex;

        public void LoadAppIndex()
        {
            Task.Factory.StartNew(() =>
            {
                var progs = AppsData.ReadInstalled();
                var store = storeCache ?? StoreData.ReadInstalled(false);
                return Tuple.Create(progs, store);
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                if (t.IsFaulted) return;
                programsIndex = t.Result.Item1;
                if (storeCache == null) storeCache = t.Result.Item2;
            }));
        }

        public static object FindOwner(string path, IEnumerable<AppEntry> programs, IEnumerable<StoreApp> store)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Func<string, bool> under = loc =>
            {
                if (string.IsNullOrEmpty(loc) || loc.Length <= 3) return false;
                string l = loc.TrimEnd('\\'), p = path.TrimEnd('\\');
                return p.Equals(l, StringComparison.OrdinalIgnoreCase) || p.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase);
            };
            // the deepest (most specific) folder wins
            var prog = (programs ?? new AppEntry[0]).Where(a => AppsData.SafeToMeasure(a.Location) && under(a.Location)).OrderByDescending(a => a.Location.Length).FirstOrDefault();
            var st = (store ?? new StoreApp[0]).Where(a => under(a.Location)).OrderByDescending(a => a.Location.Length).FirstOrDefault();
            if (prog != null && st != null) return prog.Location.Length >= st.Location.Length ? (object)prog : st;
            return (object)prog ?? st;
        }

        public string OwnerName(string path)
        {
            var o = FindOwner(path, programsIndex, storeCache);
            if (o is AppEntry) return ((AppEntry)o).Name;
            if (o is StoreApp) return ((StoreApp)o).Level == "keep" ? null : ((StoreApp)o).Friendly;
            return null;
        }

        public void UninstallOwner(string path)
        {
            var o = FindOwner(path, programsIndex, storeCache);
            if (o is AppEntry) UninstallProgram((AppEntry)o);
            else if (o is StoreApp) RemoveStoreApps(new List<StoreApp> { (StoreApp)o });
        }

        public void ShowStoreTab()
        {
            tabs.SelectedTab = pageApps;
            appsTabs.SelectedTab = pageStore;
            if (!storeLoaded) RefreshStoreApps();
        }
    }
}
