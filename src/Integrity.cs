// "Verify this copy": Elixar's fingerprint, signature and the state of its protected copy and Undo folder.

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class IntegrityForm : Form
    {
        readonly TextBox box;
        readonly Button btnInstall, btnCopy;
        string hash = "";

        public IntegrityForm(float scale)
        {
            Func<int, int> S = px => (int)Math.Round(px * scale);
            Text = "Verify Elixar"; Icon = Brand.Load(32); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5f); Size = new Size(S(820), S(500)); MinimizeBox = false; MaximizeBox = false;
            box = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), BackColor = Color.White, ScrollBars = ScrollBars.Vertical, Text = "Checking..." };
            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(S(8)) };
            btnCopy = new Button { Text = "Copy fingerprint", AutoSize = true };
            btnInstall = new Button { Text = "Install / update protected copy", AutoSize = true, Enabled = false };
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            row.Controls.AddRange(new Control[] { btnCopy, btnInstall, close });
            Controls.Add(box); Controls.Add(row);
            AcceptButton = close;
            btnCopy.Click += (o, e) => { if (hash.Length > 0) try { Clipboard.SetText(hash); } catch { } };
            btnInstall.Click += (o, e) =>
            {
                string err = Install.Ensure();
                Lang.Box(this, err == null ? "The protected copy in " + Install.Dir + " is up to date." : "Could not update it: " + err, "Elixar",
                    MessageBoxButtons.OK, err == null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                Fill();
            };
            Shown += (o, e) => Fill();
        }

        void Fill()
        {
            Task.Factory.StartNew(() => Report()).ContinueWith(t => { if (!IsDisposed) BeginInvoke((Action)(() =>
            {
                box.Text = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                btnInstall.Enabled = Win.IsWindows && Sys.IsAdmin() && !Install.RunningInstalled;
            })); });
        }

        public string Report()
        {
            string exe = Install.Current;
            hash = Trust.Sha256(exe);
            var ver = typeof(IntegrityForm).Assembly.GetName().Version;
            bool signed = Trust.HasValidSignature(exe);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Elixar " + ver.Major + "." + ver.Minor + "." + ver.Build + " - made by Elisar");
            sb.AppendLine();
            sb.AppendLine("This file:   " + exe);
            sb.AppendLine("SHA-256:     " + hash);
            sb.AppendLine("Signature:   " + (signed ? "valid, signed by " + Trust.Signer(exe) : "not signed (Elixar is a personal project)"));
            sb.AppendLine("             Compare it with SHA256.txt from your download. If they differ, delete this copy.");
            sb.AppendLine();
            if (Win.IsWindows)
            {
                sb.AppendLine("Protected copy (used by the schedules):");
                if (Install.RunningInstalled) sb.AppendLine("   You are running it: " + Install.Exe + (Locked.IsLocked(Install.Exe) ? " - only administrators can change it." : " - WARNING: others can change it."));
                else if (!Install.IsInstalled) sb.AppendLine("   Not installed. It is put in " + Install.Dir + " when you save a schedule, or with the button below.");
                else sb.AppendLine("   " + Install.Exe + (Install.Matches ? " - same as this file." : " - DIFFERENT from this file (older or newer version). Use the button below to update it."));
                sb.AppendLine();
                string undo = Quarantine.RootFor(0);
                sb.AppendLine("Undo folder: " + undo + (Locked.IsLocked(undo) ? " - locked to administrators." : " - WARNING: not locked."));
                sb.AppendLine("Data folder: " + Paths.Data + (Locked.IsLocked(Paths.Data) ? " - only administrators can change it." : ""));
                sb.AppendLine();
                sb.AppendLine("Also protected:");
                sb.AppendLine("   - never deletes inside Windows' own folders");
                sb.AppendLine("   - never follows links planted in folders it cleans");
                sb.AppendLine("   - runs Android tools only after checking Google's signature");
                sb.AppendLine("   - runs uninstallers for your account without admin rights");
            }
            return sb.ToString();
        }
    }
}
