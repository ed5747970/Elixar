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
            if (Win.IsWindows)
            {
                sb.AppendLine("Running program: " + (SelfImage.Verified ? "the same as this file, which is held open so nothing can replace it."
                                                                        : "WARNING: different from this file. It may have been replaced; download Elixar again."));
                sb.AppendLine("Download mark: " + (SafeFile.HasDownloadMark(exe) ? "yes - Windows shows its SmartScreen box for this copy. Use the installed copy from the Start menu instead."
                                                                                : "none - Windows doesn't show its SmartScreen box for this copy."));
            }
            if (Lx.On)
            {
                sb.AppendLine();
                sb.AppendLine("Installed package (.deb):");
                if (!exe.StartsWith("/usr/lib/elixar/")) sb.AppendLine("   This copy runs from a folder, not from the package. Install the .deb so only root can change Elixar.");
                else
                {
                    string o; int c = Lx.Run("dpkg", "--verify elixar", 30000, out o);
                    sb.AppendLine(c == 0 && o.Trim().Length == 0 ? "   Every file matches the package (dpkg --verify)." : "   WARNING: some files differ from the package. Reinstall it:\n   " + o.Replace("\n", "\n   "));
                }
                sb.AppendLine("System helper: " + (Lx.HelperTrusted(Lx.Helper) ? Lx.Helper + " - owned by root, nobody else can change it." :
                                                   File.Exists(Lx.Helper) ? "WARNING: " + Lx.Helper + " can be changed by others, so Elixar doesn't use it." :
                                                   "not installed (comes with the .deb); system items run Elixar's fixed commands by full path."));
                sb.AppendLine("Sign-ins: " + (SecretBox.KeyringReady ? "encrypted with a key kept in your desktop keyring, in a folder only your account can open."
                                                                     : "in a folder only your account can open (no desktop keyring found to encrypt them)."));
            }
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
                sb.AppendLine("   - starts Windows tools only from the Windows folder, and Docker only from Program Files");
                sb.AppendLine("   - copies, moves and saves files only after checking where they really are");
                sb.AppendLine("   - restarts and updates only through the protected copy");
            }
            return sb.ToString();
        }
    }
}
