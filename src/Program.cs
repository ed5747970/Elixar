// Start-up: command-line modes, administrator rights, crash reporting.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            SelfImage.Hold();          // first: nobody can rename or replace this Elixar.exe while it runs
            Install.HardenDllSearch();
            bool scheduled = args.Contains("--auto-clean") || args.Contains("--daily-tidy") || args.Contains("--mail-daily") || args.Contains("--check-space") || args.Contains("--fake-adb");
            if (Win.IsWindows)
            {
                // a .config, .local or .manifest put next to Elixar.exe could make it load someone else's code
                var planted = EnvGuard.PlantedFiles(Application.ExecutablePath);
                if (planted.Count > 0)
                {
                    if (!scheduled)
                        MessageBox.Show("Elixar did not start, because something was put next to Elixar.exe:\n\n" + string.Join("\n", planted) +
                            "\n\nElixar never uses these, and Windows would obey them: they could make Elixar run someone else's code with administrator rights. " +
                            "Delete them, or download Elixar again into a new folder.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
            }
            // running as administrator: the programs Elixar starts get Windows' own Program Files, Path and ComSpec, and no .NET switches
            var droppedEnv = Win.IsWindows && Sys.IsAdmin() ? EnvGuard.Sanitize() : new List<string>();
            Theme.Init(Settings.ThemeMode);
            Lang.Init(Settings.Language);
            // the pretend phone used by the self-test only (never answers outside a self-test run)
            if (args.Length > 0 && args[0] == "--fake-adb" && Environment.GetEnvironmentVariable("ELIXAR_SELFTEST") == "1") return FakeAdb.Run(args.Skip(1).ToArray());
            if (args.Contains("--selftest"))
            {
                Sys.TryDpiAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return SelfTest.Run(!args.Contains("--quiet"));
            }
            int ci = Array.IndexOf(args, "--check-space");
            if (ci >= 0)
            {
                int pct = 15;
                if (ci + 1 < args.Length) int.TryParse(args[ci + 1], out pct);
                Application.EnableVisualStyles();
                return Automation.CheckSpace(Math.Max(1, Math.Min(99, pct)));
            }
            if (args.Contains("--test-alert"))
            {
                Application.EnableVisualStyles();
                Automation.Notify("Test alert from Elixar",
                    "This is how the low-space alert looks. " + Fs.Fmt(Sys.Free()) + " is free on C: right now.", ToolTipIcon.Info, false);
                return 0;
            }
            if (args.Contains("--mail-daily") || args.Contains("--auto-clean") || args.Contains("--daily-tidy")) Install.HideLauncherWindow();
            if (args.Contains("--daily-tidy"))
            {
                Application.EnableVisualStyles();
                return Tidy.Daily();
            }
            if (args.Contains("--mail-daily"))
            {
                Application.EnableVisualStyles();
                return MailReports.Daily();
            }
            if (args.Contains("--auto-clean"))
            {
                Application.EnableVisualStyles();
                return Automation.AutoClean(true);
            }

            bool testUi = args.Contains("--test");
            bool noElevate = testUi || args.Contains("--no-elevate") || !Win.IsWindows;
            if (Lx.IsRoot && !testUi)
            {
                // a window running as root could be told to do anything; Elixar asks for your password only for its fixed system commands
                Application.EnableVisualStyles();
                MessageBox.Show("Please start Elixar without sudo.\n\nIt runs as you and asks for your password only when a system clean needs it.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            if (!noElevate && !Sys.IsAdmin())
            {
                // before asking for administrator rights: .NET switches set for your account would load code into Elixar
                var inAccount = EnvGuard.InAccount();
                if (inAccount.Count > 0)
                {
                    if (Lang.Box(null, "Before Elixar asks for administrator rights:\n\nThese settings for your Windows account make every .NET program load extra code:\n\n" +
                            string.Join("\n", inAccount) + "\n\nWith administrator rights, that code would get them too. Developer tools sometimes add these settings, and so does malware.\n\n" +
                            "Remove them and continue? (No closes Elixar.)", "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return 0;
                    EnvGuard.RemoveFromAccount(inAccount);
                }
                try
                {
                    // the protected copy is preferred when it is the same version or newer: only administrators can change it,
                    // and it carries no "downloaded" mark, so Windows doesn't show its SmartScreen box for it
                    string self = Application.ExecutablePath;
                    try
                    {
                        if (Install.IsInstalled && !Install.RunningInstalled && Locked.IsLocked(Install.Exe) &&
                            Updates.Compare(new Version(FileVersionInfo.GetVersionInfo(Install.Exe).FileVersion), new Version(FileVersionInfo.GetVersionInfo(self).FileVersion)) >= 0)
                            self = Install.Exe;
                    }
                    catch { }
                    // keep --install / --uninstall when asking for admin rights
                    string pass = string.Join(" ", args.Where(x => x == "--install" || x == "--uninstall" || x == "--quiet"));
                    Process.Start(new ProcessStartInfo(self, pass) { UseShellExecute = true, Verb = "runas" });
                }
                catch (Win32Exception)
                {
                    Lang.Box(null, "Elixar needs administrator rights to clean Windows folders.\n\nStart it again and click Yes on the Windows prompt.",
                        "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return 0;
            }

            if (Win.IsWindows && !testUi && droppedEnv.Count > 0)
            {
                // started as administrator with .NET switches already in place: their code may be running inside Elixar
                var inAccount = EnvGuard.InAccount();
                if (Lang.Box(null, "Elixar was started with settings that make .NET load extra code:\n\n" + string.Join("\n", droppedEnv) +
                        "\n\nThat code may already be running inside Elixar with administrator rights, so Elixar closes now." +
                        (inAccount.Count > 0 ? "\n\nRemove these settings from your Windows account? Then start Elixar again." : "\n\nCheck how Elixar was started, then start it again normally."),
                        "Elixar", inAccount.Count > 0 ? MessageBoxButtons.YesNo : MessageBoxButtons.OK, MessageBoxIcon.Warning) == DialogResult.Yes)
                    EnvGuard.RemoveFromAccount(inAccount);
                return 1;
            }

            int au = Array.IndexOf(args, "--apply-update");
            if (au >= 0 && au + 2 < args.Length) { int pid; int.TryParse(args[au + 2], out pid); return Updates.Apply(args[au + 1], pid); }
            if (args.Contains("--uninstall")) { Application.EnableVisualStyles(); return Setup.Uninstall(args.Contains("--quiet")); }
            if (args.Contains("--install"))
            {
                Application.EnableVisualStyles();
                string err = Setup.InstallHere(null);
                Lang.Box(null, err == null ? "Elixar is installed: find it in the Start menu. It also appears in Windows' installed apps list." : "Could not install Elixar: " + err, "Elixar",
                    MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                if (err == null) try { Process.Start(new ProcessStartInfo(Install.Exe) { UseShellExecute = false }); } catch { }
                return err == null ? 0 : 1;
            }

            Sys.TryDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (o, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (o, e) => ShowCrash(e.ExceptionObject as Exception);

            // the PIN, when one is set (Elixar restarting itself a moment ago doesn't ask again)
            if (!testUi && !PinLock.TakeTicket() && !PinLock.Unlock(null)) return 0;
            if (!testUi) OfferInstalledUpdate();
            Application.Run(new MainForm(testUi));
            return 0;
        }

        // An older Elixar is installed on this PC (in Program Files, used by the Start menu and the schedules) and this newer
        // download was started: offer to update that copy in place. Nothing is uninstalled, so your settings, schedules and
        // email sign-in stay as they are; the schedules simply run the new version from now on.
        static void OfferInstalledUpdate()
        {
            try
            {
                if (!Win.IsWindows || !Sys.IsAdmin() || !Install.IsInstalled || Install.RunningInstalled || Install.Matches) return;
                var installed = new Version(FileVersionInfo.GetVersionInfo(Install.Exe).FileVersion);
                if (!Updates.IsNewer(Updates.Current, installed)) return;
                string was = installed.ToString(3), now = Updates.Current.ToString(3);
                if (Lang.Box(null, Lang.T("Update the installed Elixar to " + now + "?") + "\n\n" +
                        Lang.T("Elixar " + was + " is installed on this PC (Start menu and schedules). This version replaces it in place: your settings, schedules and email sign-in stay as they are.") ,
                        "Elixar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string err = Install.Ensure();
                if (err == null) { try { Automation.UpgradeElevatedTasks(); } catch { } try { Automation.Log("Updated the installed Elixar from " + was + " to " + now + "."); } catch { } }
                Lang.Box(null, Lang.T(err == null ? "Done: the installed Elixar is now " + now + "." : "The installed Elixar could not be updated: " + err), "Elixar",
                         MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch { }
        }

        static void ShowCrash(Exception ex)
        {
            string text = ex == null ? "Unknown error" : ex.ToString();
            // as administrator, only into Elixar's locked data folder (a file next to a downloaded Elixar.exe could be a link)
            string logAt = Sys.IsAdmin() || !Win.IsWindows ? Paths.FileIn("cleaner-log.txt") : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cleaner-log.txt");   // Linux: your private folder
            try { File.AppendAllText(logAt, DateTime.Now.ToString("s") + " CRASH " + text + Environment.NewLine); } catch { }
            MessageBox.Show("Something went wrong:\n\n" + (ex == null ? text : ex.Message) + "\n\nDetails were saved to " + logAt + ".",
                "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
