// Uninstall + leftovers in one step: after an app's own uninstaller has finished, Elixar looks for the folders that app left
// behind and offers to move them to Undo (they can be put back from Tools > Undo). Nothing is deleted without asking.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        public static Func<List<AppEntry>> InstalledOverride;             // self-test: what is installed now
        public static Func<AppEntry, List<Leftover>, bool> LeftoversAskHook;  // self-test: answer the question
        public event Action<AppEntry, List<Leftover>> UninstallFinished;
        public static int UninstallPollMs = 4000;
        readonly HashSet<string> watching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool StillInstalled(AppEntry a, List<AppEntry> now)
        {
            return now.Any(x => (!string.IsNullOrEmpty(a.KeyPath) && string.Equals(x.KeyPath, a.KeyPath, StringComparison.OrdinalIgnoreCase)) ||
                                (string.IsNullOrEmpty(a.KeyPath) && string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase)));
        }

        // waits (up to 30 minutes) until Windows no longer lists the app, then looks for what it left behind
        public void WatchUninstall(AppEntry a)
        {
            if (a == null || !watching.Add(a.KeyPath ?? a.Name)) return;
            var started = DateTime.Now;
            Task.Factory.StartNew(() =>
            {
                while ((DateTime.Now - started).TotalMinutes < 30 && !IsDisposed)
                {
                    Thread.Sleep(UninstallPollMs);
                    List<AppEntry> now;
                    try { now = InstalledOverride != null ? InstalledOverride() : AppsData.ReadInstalled(); } catch { continue; }
                    if (StillInstalled(a, now)) continue;
                    Thread.Sleep(Math.Min(UninstallPollMs, 3000));   // let the uninstaller finish removing its own files
                    var inUse = Leftovers.InUseNames();
                    return Leftovers.ForApp(a, inUse);
                }
                return null;
            }, TaskCreationOptions.LongRunning).ContinueWith(t => UI(() =>
            {
                watching.Remove(a.KeyPath ?? a.Name);
                var found = t.IsFaulted ? null : t.Result;
                if (found == null) return;   // still installed after 30 minutes, or cancelled in the uninstaller
                Log(Lang.T(a.Name + " was uninstalled.") + " " + Lang.T(found.Count == 0 ? "It left nothing behind." : "It left " + found.Count + " folders behind (" + Fs.Fmt(found.Sum(f => f.Bytes)) + ")."));
                if (found.Count > 0) OfferLeftovers(a, found);
                if (UninstallFinished != null) UninstallFinished(a, found);
                if (pageApps != null && !appsBusy && !testMode) RefreshApps();
            }));
        }

        void OfferLeftovers(AppEntry a, List<Leftover> found)
        {
            int keep = TidyPrefs.Load().UndoDays;
            bool yes;
            if (LeftoversAskHook != null) yes = LeftoversAskHook(a, found);
            else
            {
                string list = string.Join("\n", found.Take(12).Select(f => "  - " + f.Path + "  (" + Fs.Fmt(f.Bytes) + ")")) + (found.Count > 12 ? "\n  ..." : "");
                yes = Lang.BoxNo(this, Lang.T(a.Name + " is uninstalled, but it left these folders behind:") + "\n\n" + list + "\n\n" +
                                       Lang.T("Move them to Undo? They are kept for " + keep + " days in Tools > Undo, so you can put them back."), "Elixar") == DialogResult.Yes;
            }
            if (!yes) { Log(Lang.T("Leftovers of " + a.Name + " were left as they are. Tools > Leftovers can find them later.")); return; }
            Task.Factory.StartNew(() => ChangeLog.Within("Leftovers", () => Quarantine.MoveToUndo("Leftovers of " + a.Name, found.Select(f => f.Path), m => UI(() => Log(m)), keep)))
                .ContinueWith(t => UI(() => Log(Lang.T(t.IsFaulted ? "Could not move the leftovers: " + t.Exception.GetBaseException().Message : "Moved " + Fs.Fmt(t.Result) + " of leftovers of " + a.Name + " to Undo."))));
        }
    }
}
