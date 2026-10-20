// The icon next to the clock: how much is free on C:, and (if you choose) Elixar minimizes there.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public partial class MainForm
    {
        NotifyIcon tray;
        CheckBox chkTray;
        public string TrayText { get { return tray == null ? "" : tray.Text; } }   // self-test
        static bool TrayMinimize { get { return Settings.Get("TrayMinimize", "0") == "1"; } }

        void BuildTray()
        {
            if (LinuxUi || testMode) return;
            var menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("Open Elixar"), null, (o, e) => RestoreFromTray());
            menu.Items.Add(Lang.T("Exit"), null, (o, e) => { tray.Visible = false; Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "Elixar", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (o, e) => RestoreFromTray();
            Resize += (o, e) => { if (WindowState == FormWindowState.Minimized && TrayMinimize) { Hide(); } };
            FormClosed += (o, e) => { try { tray.Visible = false; tray.Dispose(); } catch { } };
            UpdateTray();
        }

        // from the icon next to the clock: anyone at the PC could click it, so the PIN is asked again when one is set
        void RestoreFromTray()
        {
            if (!Visible && !testMode && !PinLock.Unlock(null)) return;
            Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate();
        }

        // "Elixar - C: 29.4 GB free" (Windows allows 63 characters)
        public static string TrayLine(long free, long total)
        {
            string t = "Elixar - C: " + Fs.Fmt(free) + " free" + (total > 0 ? " (" + Math.Round(free * 100.0 / total) + "%)" : "");
            return t.Length > 63 ? t.Substring(0, 63) : t;
        }

        void UpdateTray()
        {
            if (tray == null) return;
            try { tray.Text = TrayLine(Sys.Free(), Sys.Total()); } catch { }
        }

        CheckBox TrayOption()
        {
            chkTray = new CheckBox { AutoSize = true, Text = "Minimize Elixar to the icon next to the clock", Checked = TrayMinimize, Margin = new Padding(S(4), S(10), 0, 0), Visible = !LinuxUi };
            chkTray.CheckedChanged += (o, e) => { if (!testMode) try { Settings.Set("TrayMinimize", chkTray.Checked ? "1" : "0"); } catch { } };
            return chkTray;
        }
    }
}
