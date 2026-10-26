// Tools > Repair Windows: Windows' own repair tools, one after the other - DISM checks the component store (the copies Windows
// repairs itself from) and replaces damaged parts from Windows Update, then SFC checks every protected system file.
// Personal files and apps are not touched. Both tools are started from the Windows folder by their full path.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public enum RepairOutcome { Healthy, Repaired, PartlyRepaired, Failed, RestartFirst, Stopped }

    public class RepairStep
    {
        public string Title = "", Tool = "", Args = "";
        public RepairOutcome Outcome; public string Text = ""; public int Code;
    }

    public static class WinRepair
    {
        public static RepairStep[] Steps()
        {
            return new[]
            {
                new RepairStep { Title = "Check the Windows component store", Tool = "dism.exe", Args = "/Online /Cleanup-Image /RestoreHealth" },
                new RepairStep { Title = "Check every Windows system file", Tool = "sfc.exe", Args = "/scannow" },
            };
        }

        // self-test: (tool, args, progress) -> (exit code, output)
        public static Func<string, string, Action<int>, CancellationToken, Tuple<int, string>> RunOverride;

        // the percentage in a progress line: DISM "[====  45.2%   ]", SFC "Verification 45% complete."
        static readonly Regex Pct = new Regex(@"(\d{1,3})(?:[.,]\d)?\s*%", RegexOptions.Compiled);
        public static int Percent(string line)
        {
            var m = Pct.Match(line ?? ""); int p;
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p <= 100 ? p : -1;
        }

        // what a finished step means, in plain words (English output; other Windows languages fall back to the exit code)
        public static void Judge(RepairStep s, int code, string output)
        {
            string o = (output ?? "").Replace("\0", "");
            s.Code = code;
            if (s.Tool == "dism.exe")
            {
                if (code == 0)
                {
                    bool fixedSome = Regex.IsMatch(o, @"repaired|corruption was repaired", RegexOptions.IgnoreCase) && !Regex.IsMatch(o, @"No component store corruption", RegexOptions.IgnoreCase);
                    s.Outcome = fixedSome ? RepairOutcome.Repaired : RepairOutcome.Healthy;
                    s.Text = fixedSome ? "Damaged parts of the component store were replaced from Windows Update." : "The component store is healthy.";
                }
                else if (code == unchecked((int)0x800f081f) || o.IndexOf("0x800f081f", StringComparison.OrdinalIgnoreCase) >= 0)
                { s.Outcome = RepairOutcome.Failed; s.Text = "Windows couldn't download the repair files. Check the internet connection and that Windows Update works, then try again."; }
                else if (code == 3010 || o.IndexOf("restart", StringComparison.OrdinalIgnoreCase) >= 0 && code != 0)
                { s.Outcome = RepairOutcome.RestartFirst; s.Text = "Windows needs a restart to finish other work first. Restart, then run Repair Windows again."; }
                else { s.Outcome = RepairOutcome.Failed; s.Text = "DISM stopped with error " + Hex(code) + ". " + LastLine(o); }
                return;
            }
            // SFC: the result is in its last message
            if (Regex.IsMatch(o, @"did not find any integrity violations", RegexOptions.IgnoreCase))
            { s.Outcome = RepairOutcome.Healthy; s.Text = "Every Windows system file is fine."; }
            else if (Regex.IsMatch(o, @"found corrupt files and successfully repaired", RegexOptions.IgnoreCase))
            { s.Outcome = RepairOutcome.Repaired; s.Text = "Damaged Windows files were found and repaired. Restart the PC to finish."; }
            else if (Regex.IsMatch(o, @"unable to fix some", RegexOptions.IgnoreCase))
            { s.Outcome = RepairOutcome.PartlyRepaired; s.Text = "Some damaged Windows files couldn't be repaired. Restart, run Repair Windows once more; if it stays, Windows' \"Reset this PC\" (keep my files) fixes it."; }
            else if (Regex.IsMatch(o, @"repair pending|requires reboot|pending.*restart", RegexOptions.IgnoreCase))
            { s.Outcome = RepairOutcome.RestartFirst; s.Text = "A repair is waiting for a restart. Restart, then run Repair Windows again."; }
            else if (code == 0 && o.Trim().Length > 0)
            { s.Outcome = RepairOutcome.Healthy; s.Text = "Finished. " + LastLine(o); }
            else { s.Outcome = RepairOutcome.Failed; s.Text = "SFC couldn't finish (" + Hex(code) + "). " + LastLine(o); }
        }

        static string Hex(int code) { return code >= 0 && code < 100000 ? code.ToString(CultureInfo.InvariantCulture) : "0x" + code.ToString("x8"); }
        static string LastLine(string o)
        {
            string l = o.Replace("\r", "\n").Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 3 && Percent(x) < 0) ?? "";
            return l.Length > 200 ? l.Substring(0, 200) : l;
        }

        // runs one step and reports its progress (0-100); returns when the tool ends or is stopped
        public static void Run(RepairStep s, Action<int> progress, CancellationToken tok)
        {
            Tuple<int, string> r;
            if (RunOverride != null) r = RunOverride(s.Tool, s.Args, progress, tok);
            else r = RunTool(Win.SystemExe(s.Tool), s.Args, progress, tok);
            if (tok.IsCancellationRequested) { s.Outcome = RepairOutcome.Stopped; s.Text = "Stopped. Nothing is left half done: you can run it again any time."; s.Code = -1; }
            else Judge(s, r.Item1, r.Item2);
            ChangeLog.Command(s.Tool, s.Args, s.Text, s.Code);
        }

        static Encoding Oem() { try { return Encoding.GetEncoding(437); } catch { return Encoding.Default; } }

        static Tuple<int, string> RunTool(string exe, string args, Action<int> progress, CancellationToken tok)
        {
            // SFC writes UTF-16 to a pipe and both tools update their progress with \r, so the output is read
            // character by character, NUL characters dropped and lines split on \r as well as \n
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Oem() };
            var all = new StringBuilder();
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                var line = new StringBuilder(); int last = -1;
                var reader = new Thread(() =>
                {
                    try
                    {
                        int c;
                        while ((c = p.StandardOutput.Read()) >= 0)
                        {
                            if (c == 0) continue;
                            if (c == '\r' || c == '\n')
                            {
                                if (line.Length > 0)
                                {
                                    string l = line.ToString(); line.Clear();
                                    lock (all) all.AppendLine(l);
                                    int pc = Percent(l);
                                    if (pc >= 0 && pc != last) { last = pc; if (progress != null) progress(pc); }
                                }
                                continue;
                            }
                            line.Append((char)c);
                        }
                    }
                    catch { }
                }) { IsBackground = true };
                reader.Start();
                while (!p.WaitForExit(500))
                    if (tok.IsCancellationRequested) { try { p.Kill(); } catch { } p.WaitForExit(10000); break; }
                reader.Join(5000);
                if (line.Length > 0) lock (all) all.AppendLine(line.ToString());
                int code = -1; try { code = p.ExitCode; } catch { }
                string err = ""; try { err = errTask.Result; } catch { }
                lock (all) return Tuple.Create(code, all.ToString() + err);
            }
        }
    }

    public partial class MainForm
    {
        TabPage pageRepair;
        Label[] repNum, repTitle, repText; ProgressBar[] repBar;
        Button btnRepStart, btnRepStop; Label repStatus; CancellationTokenSource repCts;
        public event Action RepairDone;
        public string RepairSummary { get { return repStatus == null ? "" : repStatus.Text; } }
        public string RepairStepText(int i) { return repText[i].Text; }

        void BuildRepair()
        {
            pageRepair = new TabPage("Repair Windows") { BackColor = Color.FromArgb(246, 248, 251) };
            var t = Page(6);
            for (int i = 0; i < 6; i++) t.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
            var steps = WinRepair.Steps();
            repNum = new Label[steps.Length]; repTitle = new Label[steps.Length]; repText = new Label[steps.Length]; repBar = new ProgressBar[steps.Length];
            var list = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = steps.Length * 3, Dock = DockStyle.Top, Margin = new Padding(0, S(6), 0, S(6)) };
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(36))); list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < steps.Length; i++)
            {
                repNum[i] = new Label { AutoSize = false, Size = new Size(S(26), S(26)), TextAlign = ContentAlignment.MiddleCenter, Text = (i + 1).ToString(), Font = new Font("Segoe UI Semibold", 10f),
                                        BackColor = Color.FromArgb(91, 76, 196), ForeColor = Color.White, Margin = new Padding(0, S(4), 0, 0) };
                repTitle[i] = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Text = steps[i].Title + "   (" + steps[i].Tool.Replace(".exe", "").ToUpperInvariant() + " " + steps[i].Args.Split(' ').Last() + ")", Margin = new Padding(0, S(6), 0, 0) };
                repBar[i] = new ProgressBar { Width = S(520), Height = S(10), Maximum = 100, Visible = false, Margin = new Padding(S(2), S(4), 0, 0) };
                repText[i] = new Label { AutoSize = true, MaximumSize = new Size(S(900), 0), ForeColor = Color.DimGray, Text = "Not run yet.", Margin = new Padding(S(1), S(2), 0, S(8)) };
                list.Controls.Add(repNum[i], 0, i * 3); list.SetRowSpan(repNum[i], 3);
                list.Controls.Add(repTitle[i], 1, i * 3); list.Controls.Add(repBar[i], 1, i * 3 + 1); list.Controls.Add(repText[i], 1, i * 3 + 2);
            }
            btnRepStart = MakeButton("Repair Windows"); btnRepStart.Font = new Font("Segoe UI Semibold", 9f);
            btnRepStop = MakeButton("Stop"); btnRepStop.Enabled = false;
            var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, S(4), 0, 0) };
            row.Controls.AddRange(new Control[] { btnRepStart, btnRepStop });
            repStatus = new Label { AutoSize = true, MaximumSize = new Size(S(1000), 0), Margin = new Padding(S(1), S(10), 0, 0), Text = "" };
            t.Controls.Add(Title("Repair Windows"), 0, 0);
            t.Controls.Add(Note("Uses Windows' own repair tools to find and fix damaged system files: DISM checks the copies Windows repairs itself from " +
                                "(and downloads fresh ones from Windows Update), then SFC checks every Windows system file. Your files, settings and apps are not touched. " +
                                "Takes 15-30 minutes and needs the internet; you can keep working meanwhile. Use it when Windows misbehaves: updates fail, " +
                                "built-in apps or settings don't open, or errors about missing system files."), 0, 1);
            t.Controls.Add(list, 0, 2); t.Controls.Add(row, 0, 3); t.Controls.Add(repStatus, 0, 5);
            pageRepair.Controls.Add(t);
            btnRepStart.Click += (o, e) => StartRepair();
            btnRepStop.Click += (o, e) => { if (repCts != null) { repCts.Cancel(); btnRepStop.Enabled = false; repStatus.Text = Lang.T("Stopping..."); } };
        }

        public void StartRepair()
        {
            if (repCts != null) return;
            if (!testMode && Win.IsWindows && !Sys.IsAdmin()) { Lang.Box(this, "Repairing Windows needs administrator rights.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            repCts = new CancellationTokenSource(); var tok = repCts.Token;
            btnRepStart.Enabled = false; btnRepStop.Enabled = true; repStatus.Text = "";
            var steps = WinRepair.Steps();
            for (int i = 0; i < steps.Length; i++) { repNum[i].BackColor = Color.FromArgb(91, 76, 196); repNum[i].Text = (i + 1).ToString(); repText[i].Text = Lang.T("Waiting..."); repText[i].ForeColor = Color.DimGray; repBar[i].Value = 0; repBar[i].Visible = false; }
            Log("Repair Windows started.");
            Task.Factory.StartNew(() =>
            {
                using (ChangeLog.In("Repair Windows"))
                    for (int i = 0; i < steps.Length && !tok.IsCancellationRequested; i++)
                    {
                        int k = i;
                        UI(() => { repBar[k].Visible = true; repText[k].Text = Lang.T("Running... (this is the long part)"); });
                        WinRepair.Run(steps[k], p => UI(() => { repBar[k].Value = Math.Max(0, Math.Min(100, p)); repText[k].Text = p + "%"; }), tok);
                        UI(() => ShowRepairStep(k, steps[k]));
                        Log("  " + steps[k].Title + ": " + steps[k].Text);
                        if (steps[k].Outcome == RepairOutcome.RestartFirst || steps[k].Outcome == RepairOutcome.Stopped) break;
                    }
                return steps;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                repCts = null; btnRepStart.Enabled = true; btnRepStop.Enabled = false;
                var done = t.IsFaulted ? null : t.Result;
                repStatus.Text = Lang.T(done == null ? "Repair stopped: " + t.Exception.GetBaseException().Message : RepairVerdict(done));
                Activity.Add("repair", repStatus.Text, 0);
                if (RepairDone != null) RepairDone();
            }));
        }

        void ShowRepairStep(int i, RepairStep s)
        {
            repBar[i].Visible = false;
            bool good = s.Outcome == RepairOutcome.Healthy || s.Outcome == RepairOutcome.Repaired;
            repNum[i].Text = good ? "✓" : s.Outcome == RepairOutcome.Stopped ? "–" : "!";
            repNum[i].BackColor = good ? Color.FromArgb(46, 125, 50) : s.Outcome == RepairOutcome.Stopped ? Color.Gray : Color.FromArgb(198, 40, 40);
            repText[i].ForeColor = good ? Color.FromArgb(46, 125, 50) : s.Outcome == RepairOutcome.Stopped ? Color.DimGray : Color.FromArgb(198, 40, 40);
            repText[i].Text = Lang.T(s.Text);
        }

        public static string RepairVerdict(IList<RepairStep> steps)
        {
            var ran = steps.Where(s => s.Code != 0 || s.Text.Length > 0).ToList();
            if (ran.Any(s => s.Outcome == RepairOutcome.Stopped)) return "Stopped. Run it again whenever you like.";
            if (ran.Any(s => s.Outcome == RepairOutcome.RestartFirst)) return "Restart the PC, then run Repair Windows again.";
            if (ran.Any(s => s.Outcome == RepairOutcome.Failed || s.Outcome == RepairOutcome.PartlyRepaired)) return "Not everything could be repaired: see the steps above.";
            if (ran.Any(s => s.Outcome == RepairOutcome.Repaired)) return "Windows was repaired. Restart the PC to finish.";
            return "Windows is healthy: nothing needed repairing.";
        }
    }
}
