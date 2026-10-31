// Tools > Windows tweaks: switch off ads, tips, Bing in Start, Copilot, extra tracking and background apps.
// Every value is written through RegLog, so Logs shows the old and new value and can put each one back.
// "Put back Windows' default" writes Windows' own value again (or removes the value when Windows has none).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public class TweakValue
    {
        public string Hive, Key, Name;
        public int On;          // the value the tweak writes (DWORD)
        public int? Default;    // Windows' own value; null = Windows has no value there
    }

    public class Tweak
    {
        public string Id, Name, Help;
        public bool Admin;      // writes to HKLM (Elixar runs as administrator)
        public List<TweakValue> Values = new List<TweakValue>();
    }

    public enum TweakState { Off, On, Partly }

    public static class Tweaks
    {
        public static string TestPrefix;   // self-test: every key goes under HKCU\Software\Elixar\<prefix>\...

        const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        const string ADV = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        static TweakValue V(string hive, string key, string name, int on, int? def) { return new TweakValue { Hive = hive, Key = key, Name = name, On = on, Default = def }; }

        public static readonly Tweak[] All =
        {
            new Tweak { Id = "ads", Name = "Turn off ads and suggested apps",
                        Help = "Suggested apps in Start, apps Windows installs by itself, ads on the lock screen and \"suggested content\" in Settings.",
                        Values = { V("HKCU", CDM, "SystemPaneSuggestionsEnabled", 0, 1), V("HKCU", CDM, "SubscribedContent-338388Enabled", 0, 1),
                                   V("HKCU", CDM, "SilentInstalledAppsEnabled", 0, 1), V("HKCU", CDM, "SubscribedContent-338393Enabled", 0, 1),
                                   V("HKCU", CDM, "SubscribedContent-353694Enabled", 0, 1), V("HKCU", CDM, "SubscribedContent-353696Enabled", 0, 1),
                                   V("HKCU", CDM, "RotatingLockScreenOverlayEnabled", 0, 1), V("HKCU", CDM, "SubscribedContent-338387Enabled", 0, 1),
                                   V("HKCU", ADV, "Start_IrisRecommendations", 0, null) } },
            new Tweak { Id = "tips", Name = "Turn off tips and \"finish setting up\" screens",
                        Help = "Tips and tricks, the welcome screens after updates, and \"Get even more out of Windows\".",
                        Values = { V("HKCU", CDM, "SubscribedContent-338389Enabled", 0, 1), V("HKCU", CDM, "SoftLandingEnabled", 0, 1),
                                   V("HKCU", CDM, "SubscribedContent-310093Enabled", 0, 1),
                                   V("HKCU", @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, null) } },
            new Tweak { Id = "bing", Name = "Search only this PC from Start (no Bing)",
                        Help = "Start's search box stops showing web results from Bing. Restart or sign out to see it.",
                        Values = { V("HKCU", @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
                                   V("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, null) } },
            new Tweak { Id = "copilot", Name = "Turn off Copilot in Windows",
                        Help = "Hides the Copilot button and turns off Windows Copilot. A Copilot app from the Store can be removed in the Apps tab.",
                        Values = { V("HKCU", @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                                   V("HKCU", ADV, "ShowCopilotButton", 0, null) } },
            new Tweak { Id = "telemetry", Name = "Send Microsoft as little data as possible", Admin = true,
                        Help = "Diagnostic data set to \"Required\" only, no ads based on your data, and no advertising ID.",
                        Values = { V("HKLM", @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1, null),
                                   V("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, null),
                                   V("HKCU", @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, null) } },
            new Tweak { Id = "background", Name = "Stop Store apps running in the background",
                        Help = "Store apps only run while open. Mail and calendar notifications may arrive later; apps you pin in Settings still work.",
                        Values = { V("HKCU", @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, null),
                                   V("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0, null) } },
        };

        public static Tweak Get(string id) { return All.FirstOrDefault(t => t.Id == id); }

        // the keys Logs may write back to (RegLog.UndoKeys)
        public static IEnumerable<string> Keys { get { return All.SelectMany(t => t.Values).Select(v => v.Key).Distinct(StringComparer.OrdinalIgnoreCase); } }

        static RegistryKey Open(TweakValue v, bool write)
        {
            string key = TestPrefix != null ? @"Software\Elixar\" + TestPrefix + @"\" + v.Hive + @"\" + v.Key : v.Key;
            var root = TestPrefix != null || v.Hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            return write ? root.CreateSubKey(key) : root.OpenSubKey(key, false);
        }

        static int? Read(TweakValue v)
        {
            try
            {
                using (var k = Open(v, false))
                {
                    if (k == null) return null;
                    var o = k.GetValue(v.Name);
                    if (o is int) return (int)o;
                    int n; if (o != null && int.TryParse(o.ToString(), out n)) return n;
                    return null;
                }
            }
            catch { return null; }
        }

        static bool IsOn(TweakValue v) { return Read(v) == v.On; }

        public static TweakState State(Tweak t)
        {
            int on = t.Values.Count(IsOn);
            return on == 0 ? TweakState.Off : on == t.Values.Count ? TweakState.On : TweakState.Partly;
        }

        // on = the tweak's values; off = Windows' own. Returns what went wrong (empty = all fine).
        public static List<string> Set(Tweak t, bool on, Action<string> log)
        {
            var errs = new List<string>();
            ChangeLog.Within("Windows tweaks", () =>
            {
                foreach (var v in t.Values)
                {
                    try
                    {
                        using (var k = Open(v, true))
                        {
                            if (k == null) throw new UnauthorizedAccessException("no access");
                            string why = "Windows tweak: " + t.Name + (on ? "" : " (Windows' default put back)");
                            if (on) RegLog.Set(k, v.Name, v.On, RegistryValueKind.DWord, why);
                            else if (v.Default.HasValue) RegLog.Set(k, v.Name, v.Default.Value, RegistryValueKind.DWord, why);
                            else RegLog.Delete(k, v.Name, why);
                        }
                    }
                    catch (Exception ex) { errs.Add(v.Name + ": " + ex.Message); }
                }
            });
            if (log != null) log(Lang.T((on ? "Windows tweak on: " : "Windows tweak off: ") + t.Name) + (errs.Count > 0 ? " (" + string.Join("; ", errs) + ")" : ""));
            return errs;
        }

        // the Policy manager decides: "on" = always on, "off" = always Windows' default, "choice" = left to you
        public static string PolicyKey(Tweak t) { return "tweak." + t.Id; }

        public static int ApplyPolicy(Action<string> log)
        {
            int n = 0;
            foreach (var t in All)
            {
                string p = Policy.Get(PolicyKey(t));
                if (p == "on" && State(t) != TweakState.On) { Set(t, true, log); n++; }
                else if (p == "off" && State(t) != TweakState.Off) { Set(t, false, log); n++; }
            }
            return n;
        }
    }

    public partial class MainForm
    {
        TabPage pageTweaks;
        readonly List<CheckBox> tweakBoxes = new List<CheckBox>();
        readonly List<Label> tweakStates = new List<Label>();
        Label tweakStatus;
        Button btnTweakApply, btnTweakDefault;
        bool tweakLoading;

        void BuildTweaks()
        {
            pageTweaks = new TabPage("Windows tweaks") { BackColor = pageTools.BackColor };
            var t = Page(4);
            t.AutoScroll = true;
            for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Title("Windows tweaks"), 0, 0);
            tweakStatus = Note("Switch off what Windows shows or sends that you didn't ask for. Every change is written in Logs with the old value, so each one can be put back.");
            t.Controls.Add(tweakStatus, 0, 1);
            // the text column takes what the state column leaves (it was a fixed 640, so in a narrow window the state was cut off)
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 0, 0, S(6)) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var helps = new List<Label>();
            int row = 0;
            foreach (var tw in Tweaks.All)
            {
                var box = new CheckBox { AutoSize = true, Text = tw.Name + (tw.Admin ? "  (for everyone on this PC)" : ""), Font = new Font("Segoe UI Semibold", 9.75f), Margin = new Padding(0, S(6), 0, 0), Tag = tw };
                var help = new Label { AutoSize = true, ForeColor = Color.DimGray, Text = tw.Help, MaximumSize = new Size(S(620), 0), Margin = new Padding(S(20), 0, 0, S(4)) };
                TextFit.Skip(help); helps.Add(help);
                var st = new Label { AutoSize = true, Margin = new Padding(S(10), S(8), 0, 0) };
                var left = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
                left.Controls.Add(box); left.Controls.Add(help);
                grid.Controls.Add(left, 0, row); grid.Controls.Add(st, 1, row); row++;
                tweakBoxes.Add(box); tweakStates.Add(st);
                box.CheckedChanged += (o, e) => { if (!tweakLoading) UpdateTweakButtons(); };
            }
            t.Controls.Add(grid, 0, 2);
            grid.Layout += (o, e) =>
            {
                int stateW = 0;
                foreach (var s in tweakStates) stateW = Math.Max(stateW, s.PreferredWidth + s.Margin.Horizontal);
                int w = Math.Max(S(240), Math.Min(S(620), grid.ClientSize.Width - stateW - S(20) - S(8)));
                foreach (var h in helps) if (h.MaximumSize.Width != w) h.MaximumSize = new Size(w, 0);
            };
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(4), 0, 0) };
            btnTweakApply = MakeButton("Apply"); btnTweakApply.Font = new Font("Segoe UI Semibold", 9f);
            btnTweakDefault = MakeButton("Put all back to Windows' default");
            var openLogs = MakeButton("See the changes in Logs");
            buttons.Controls.AddRange(new Control[] { btnTweakApply, btnTweakDefault, openLogs });
            t.Controls.Add(buttons, 0, 3);
            pageTweaks.Controls.Add(t);
            btnTweakApply.Click += (o, e) => ApplyTweaks(false);
            btnTweakDefault.Click += (o, e) => ApplyTweaks(true);
            openLogs.Click += (o, e) => { ShowLogs(); LogsPickSection("Windows tweaks"); };
        }

        public void RefreshTweaks()
        {
            tweakLoading = true;
            for (int i = 0; i < Tweaks.All.Length; i++)
            {
                var tw = Tweaks.All[i]; var st = Tweaks.State(tw);
                string pol = Policy.Get(Tweaks.PolicyKey(tw));
                tweakBoxes[i].Checked = st == TweakState.On;
                tweakBoxes[i].Enabled = pol == "choice";
                tweakStates[i].Text = Lang.T(st == TweakState.On ? "On" : st == TweakState.Partly ? "Partly on" : "Windows' default") + (pol != "choice" ? "  ·  " + Lang.T("set by the policy") : "");
                tweakStates[i].ForeColor = st == TweakState.On ? Color.FromArgb(0, 125, 70) : st == TweakState.Partly ? Color.FromArgb(190, 105, 0) : Color.DimGray;
            }
            tweakLoading = false;
            UpdateTweakButtons();
        }

        void UpdateTweakButtons()
        {
            int changes = 0;
            for (int i = 0; i < Tweaks.All.Length; i++)
            {
                var st = Tweaks.State(Tweaks.All[i]);
                if (tweakBoxes[i].Enabled && tweakBoxes[i].Checked != (st == TweakState.On)) changes++;
            }
            btnTweakApply.Enabled = changes > 0 && (!testMode || TweakHook != null);
            btnTweakApply.Text = Lang.T(changes > 0 ? "Apply " + changes + " change" + (changes == 1 ? "" : "s") : "Apply");
            btnTweakDefault.Enabled = (!testMode || TweakHook != null) && Tweaks.All.Where((tw, i) => tweakBoxes[i].Enabled).Any(tw => Tweaks.State(tw) != TweakState.Off);
        }

        // the switches as they are now (false = what is ticked), or everything back to Windows' default (true)
        public List<string> ApplyTweaks(bool allDefault)
        {
            var errs = new List<string>();
            if (testMode && TweakHook == null) return errs;
            if (allDefault && TweakHook == null && Lang.BoxNo(this, Lang.T("Put every tweak back to Windows' default?") + "\n\n" + Lang.T("Ads, tips and the other things come back as Windows had them."), "Elixar") != DialogResult.Yes) return errs;
            for (int i = 0; i < Tweaks.All.Length; i++)
            {
                if (!tweakBoxes[i].Enabled && TweakHook == null) continue;
                var tw = Tweaks.All[i]; var st = Tweaks.State(tw);
                bool want = !allDefault && tweakBoxes[i].Checked;
                if (want && st != TweakState.On) errs.AddRange(Tweaks.Set(tw, true, Log));
                else if (!want && st != TweakState.Off) errs.AddRange(Tweaks.Set(tw, false, Log));
            }
            if (errs.Count > 0) Log(Lang.T("Some tweaks could not be changed: ") + string.Join("; ", errs));
            else Log(Lang.T("Windows tweaks saved. Some show after you sign out or restart."));
            RefreshTweaks();
            return errs;
        }
        public static object TweakHook;   // self-test: apply without asking

        public void TweaksTick(int i, bool on) { tweakBoxes[i].Checked = on; }
        public bool TweakBoxEnabled(int i) { return tweakBoxes[i].Enabled; }
        public string TweakStateText(int i) { return tweakStates[i].Text; }
        public void ShowTweaks() { ShowTools(pageTweaks); }
    }
}
