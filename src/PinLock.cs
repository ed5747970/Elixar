// An optional PIN for opening Elixar. It stops other people who use this PC from opening Elixar and removing things;
// it doesn't encrypt anything. Kept in Elixar's folder that only administrators can open, as a slow salted hash
// (PBKDF2, 200,000 rounds) - never the PIN itself. Wrong PINs make you wait longer each time, also after a restart.
// A recovery code, shown once when the PIN is set, removes the PIN if it is forgotten. Scheduled runs (weekly clean,
// daily tidy, emails) never show a window, so they keep working without it.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public static class PinLock
    {
        public static string OverrideDir;   // self-test
        static string FileOf(string n) { return OverrideDir != null ? Path.Combine(OverrideDir, n) : Paths.FileIn(n); }
        static string PinFile { get { return FileOf("pin.dat"); } }
        static string TriesFile { get { return FileOf("pin-tries.txt"); } }
        static string TicketFile { get { return FileOf("unlock.tmp"); } }
        public const int Rounds = 200000, FreeTries = 5;
        public static Func<DateTime> NowOverride;
        static DateTime Now { get { return NowOverride != null ? NowOverride() : DateTime.UtcNow; } }

        public static bool IsSet { get { try { return File.Exists(PinFile) && Read() != null; } catch { return false; } } }
        public static bool ValidPin(string pin) { return pin != null && pin.Length >= 4 && pin.Length <= 8 && pin.All(c => c >= '0' && c <= '9'); }

        // what protects Elixar: a PIN of 4, 6 or 8 digits, or a password (8 to 64 characters, any letters, digits or signs)
        public enum Kind { Pin4, Pin6, Pin8, Password, PinAny /* set by an earlier build: 4 to 8 digits */ }
        public static int Digits(Kind k) { return k == Kind.Pin4 ? 4 : k == Kind.Pin6 ? 6 : k == Kind.Pin8 ? 8 : 0; }
        public static bool IsPin(Kind k) { return k != Kind.Password; }
        public static bool Valid(Kind k, string s)
        {
            if (s == null) return false;
            if (k == Kind.Password) return s.Length >= 8 && s.Length <= 64 && s.Trim().Length > 0;
            if (k == Kind.PinAny) return ValidPin(s);
            return s.Length == Digits(k) && s.All(c => c >= '0' && c <= '9');
        }
        static string Name(Kind k) { return k == Kind.Pin4 ? "pin4" : k == Kind.Pin6 ? "pin6" : k == Kind.Pin8 ? "pin8" : k == Kind.Password ? "password" : "pin"; }
        static Kind? Parse(string n)
        {
            switch (n) { case "pin4": return Kind.Pin4; case "pin6": return Kind.Pin6; case "pin8": return Kind.Pin8; case "password": return Kind.Password; case "pin": return Kind.PinAny; default: return null; }
        }
        // what is set now (null: nothing)
        public static Kind? Current { get { try { var s = File.Exists(PinFile) ? Read() : null; return s == null ? (Kind?)null : s.Kind; } catch { return null; } } }

        static byte[] Hash(string secret, byte[] salt) { using (var k = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(secret), salt, Rounds)) return k.GetBytes(32); }
        static byte[] Salt() { var b = new byte[16]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        static bool Same(byte[] a, byte[] b) { if (a == null || b == null || a.Length != b.Length) return false; int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i]; return d == 0; }

        // a code that is easy to read and type: no 0/O, 1/I/L
        const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        public static string NewRecoveryCode()
        {
            var b = new byte[16]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++) { if (i > 0 && i % 4 == 0) sb.Append('-'); sb.Append(Alphabet[b[i] % Alphabet.Length]); }
            return sb.ToString();
        }
        public static string NormalizeCode(string c) { return new string((c ?? "").ToUpperInvariant().Where(ch => Alphabet.IndexOf(ch) >= 0).ToArray()); }

        class Stored { public Kind Kind; public byte[] Salt, Pin, CodeSalt, Code; }
        static Stored Read()
        {
            var p = File.ReadAllText(PinFile).Trim().Split('|');
            string rounds = Rounds.ToString(CultureInfo.InvariantCulture);
            // v2|kind|rounds|salt|hash|code salt|code hash   (v1, from the first build with a PIN, had no kind)
            if (p.Length == 7 && p[0] == "v2" && p[2] == rounds && Parse(p[1]).HasValue)
                return new Stored { Kind = Parse(p[1]).Value, Salt = Convert.FromBase64String(p[3]), Pin = Convert.FromBase64String(p[4]), CodeSalt = Convert.FromBase64String(p[5]), Code = Convert.FromBase64String(p[6]) };
            if (p.Length == 6 && p[0] == "v1" && p[1] == rounds)
                return new Stored { Kind = Kind.PinAny, Salt = Convert.FromBase64String(p[2]), Pin = Convert.FromBase64String(p[3]), CodeSalt = Convert.FromBase64String(p[4]), Code = Convert.FromBase64String(p[5]) };
            return null;
        }

        // a PIN of 4, 6 or 8 digits (the kind follows from its length)
        public static string Set(string pin)
        {
            Kind k = pin == null ? Kind.Pin6 : pin.Length == 4 ? Kind.Pin4 : pin.Length == 8 ? Kind.Pin8 : Kind.Pin6;
            return Set(k, pin);
        }

        // sets (or replaces) the PIN or password; returns the new recovery code, to show once
        public static string Set(Kind kind, string secret)
        {
            if (kind == Kind.PinAny) kind = Kind.Pin6;
            if (!Valid(kind, secret)) throw new ArgumentException(kind == Kind.Password ? "The password must be 8 to 64 characters." : "The PIN must be " + Digits(kind) + " digits.");
            string code = NewRecoveryCode();
            byte[] s1 = Salt(), s2 = Salt();
            string line = "v2|" + Name(kind) + "|" + Rounds + "|" + Convert.ToBase64String(s1) + "|" + Convert.ToBase64String(Hash(secret, s1)) + "|" + Convert.ToBase64String(s2) + "|" + Convert.ToBase64String(Hash(NormalizeCode(code), s2));
            string tmp = PinFile + ".new";
            File.WriteAllText(tmp, line);
            bool had = File.Exists(PinFile);
            if (had) File.Delete(PinFile);
            File.Move(tmp, PinFile);
            ResetTries();
            ChangeLog.Add("Security", had ? "Changed the PIN or password" : "Turned on the PIN or password", "Elixar", "", kind == Kind.Password ? "a password" : "a " + Digits(kind) + "-digit PIN", "The PIN, the password and the recovery code are never written here", false);
            return code;
        }

        public static void Remove()
        {
            bool had = false;
            try { had = File.Exists(PinFile); File.Delete(PinFile); } catch { }
            ResetTries();
            if (had) ChangeLog.Add("Security", "Turned off the PIN or password", "Elixar", "", "", "", false);
        }

        public enum Result { Ok, Wrong, Wait }
        // seconds to wait before the next try: none for the first 5 wrong PINs, then 30 s, 1, 2, 4... up to 15 minutes
        public static int WaitFor(int wrong) { return wrong < FreeTries ? 0 : (int)Math.Min(900, 30 * Math.Pow(2, wrong - FreeTries)); }

        static void Tries(out int wrong, out DateTime last)
        {
            wrong = 0; last = DateTime.MinValue;
            try
            {
                var p = File.ReadAllText(TriesFile).Trim().Split('|'); long t;
                if (p.Length == 2 && int.TryParse(p[0], out wrong) && long.TryParse(p[1], out t)) last = new DateTime(t, DateTimeKind.Utc);
            }
            catch { }
        }
        static void ResetTries() { try { if (File.Exists(TriesFile)) File.Delete(TriesFile); } catch { } }

        public static int SecondsLeft()
        {
            int wrong; DateTime last; Tries(out wrong, out last);
            int w = WaitFor(wrong); if (w == 0) return 0;
            return Math.Max(0, (int)Math.Ceiling((last.AddSeconds(w) - Now).TotalSeconds));
        }
        public static int TriesLeft() { int wrong; DateTime last; Tries(out wrong, out last); return Math.Max(0, FreeTries - wrong); }

        static Result Try(Func<Stored, bool> ok)
        {
            if (SecondsLeft() > 0) return Result.Wait;
            Stored s; try { s = Read(); } catch { s = null; }
            if (s == null) return Result.Ok;   // no PIN set
            if (ok(s)) { ResetTries(); return Result.Ok; }
            int wrong; DateTime last; Tries(out wrong, out last);
            try { File.WriteAllText(TriesFile, (wrong + 1).ToString(CultureInfo.InvariantCulture) + "|" + Now.Ticks.ToString(CultureInfo.InvariantCulture)); } catch { }
            return Result.Wrong;
        }
        public static Result Check(string secret) { return Try(s => Valid(s.Kind, secret) && Same(Hash(secret, s.Salt), s.Pin)); }
        // the recovery code removes the PIN (a new one can be set right after)
        public static Result Recover(string code)
        {
            var r = Try(s => Same(Hash(NormalizeCode(code), s.CodeSalt), s.Code));
            if (r == Result.Ok) Remove();
            return r;
        }

        // Elixar restarting itself (language, theme, update) doesn't ask again: a one-time ticket, valid for two minutes
        public static void WriteTicket() { try { File.WriteAllText(TicketFile, Now.Ticks.ToString(CultureInfo.InvariantCulture)); } catch { } }
        public static bool TakeTicket()
        {
            try
            {
                if (!File.Exists(TicketFile)) return false;
                long t; bool ok = long.TryParse(File.ReadAllText(TicketFile).Trim(), out t) && Math.Abs((Now - new DateTime(t, DateTimeKind.Utc)).TotalSeconds) < 120;
                File.Delete(TicketFile);
                return ok;
            }
            catch { return false; }
        }

        // asks for the PIN when one is set; false = don't open
        public static bool Unlock(IWin32Window owner)
        {
            if (!IsSet) return true;
            using (var f = new PinForm(PinForm.Mode.Enter)) return PinForm.Ask(owner, f) == DialogResult.OK;
        }
    }

    // one window for entering, recovering and setting the PIN, in Elixar's look
    public class PinForm : Form
    {
        public enum Mode { Enter, Recover, Set }
        Mode mode; readonly float sc;
        int S(int v) { return (int)Math.Round(v * sc); }
        Label lTitle, lText, lStatus; TextBox t1, t2; Button bOk, bCancel; LinkLabel lnk; Timer wait;
        FlowLayoutPanel body, kinds; RadioButton[] kindRadios;
        PinLock.Kind kind = PinLock.Kind.Pin6;   // what is being set, or what is asked for
        bool Pin { get { return PinLock.IsPin(kind); } }
        public string RecoveryCode { get; private set; }
        public static Func<PinForm, DialogResult> TestHook;

        public PinForm(Mode m)
        {
            WindowFit.Watch(this);   // fits the screen (a tall window is made smaller; its page scrolls)
            mode = m;
            using (var g = CreateGraphics()) sc = Math.Max(1f, g.DpiX / 96f);
            Text = "Elixar"; Icon = Brand.Load(32); Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Panel; ForeColor = Theme.Ink;
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }
            body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(S(28), S(22), S(28), S(10)), BackColor = Theme.Panel };
            var logo = new PictureBox { Image = Brand.Load(64).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(S(48), S(48)), Margin = new Padding(0, 0, 0, S(8)) };
            lTitle = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Margin = new Padding(0, 0, 0, S(4)) };
            lText = new Label { AutoSize = true, MaximumSize = new Size(S(340), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(12)) };
            t1 = new TextBox { Width = S(240), Font = new Font("Segoe UI", 16f), UseSystemPasswordChar = true, MaxLength = 8, TextAlign = HorizontalAlignment.Center, RightToLeft = RightToLeft.No, Margin = new Padding(0, 0, 0, S(8)) };
            t2 = new TextBox { Width = S(240), Font = new Font("Segoe UI", 16f), UseSystemPasswordChar = true, MaxLength = 8, TextAlign = HorizontalAlignment.Center, RightToLeft = RightToLeft.No, Margin = new Padding(0, 0, 0, S(8)) };
            foreach (var t in new[] { t1, t2 }) { t.BorderStyle = BorderStyle.FixedSingle; t.BackColor = Theme.Dark ? Color.FromArgb(52, 52, 62) : Color.White; t.ForeColor = Theme.Ink; }
            lStatus = new Label { AutoSize = true, MaximumSize = new Size(S(340), 0), ForeColor = Theme.MapFore(Color.FromArgb(199, 54, 47)), Margin = new Padding(0, 0, 0, S(6)), Text = "" };
            lnk = new LinkLabel { AutoSize = true, Margin = new Padding(0, S(2), 0, S(6)), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent };
            // setting up: a 4, 6 or 8 digit PIN, or a password
            kinds = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(S(350), 0), Margin = new Padding(0, 0, 0, S(10)) };
            var kindList = new[] { PinLock.Kind.Pin4, PinLock.Kind.Pin6, PinLock.Kind.Pin8, PinLock.Kind.Password };
            var kindText = new[] { "4-digit PIN", "6-digit PIN", "8-digit PIN", "Password" };
            kindRadios = new RadioButton[4];
            for (int i = 0; i < 4; i++)
            {
                var k = kindList[i];
                kindRadios[i] = new RadioButton { AutoSize = true, Text = Lang.T(kindText[i]), Checked = k == kind, Margin = new Padding(0, 0, S(12), S(2)), ForeColor = Theme.Ink };
                kindRadios[i].CheckedChanged += (o, e) => { if (((RadioButton)o).Checked) { kind = k; Switch(Mode.Set); } };
                kinds.Controls.Add(kindRadios[i]);
            }
            body.Controls.AddRange(new Control[] { logo, lTitle, lText, kinds, t1, t2, lStatus, lnk });
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(20), S(10), S(20), S(12)), BackColor = Theme.Dark ? Color.FromArgb(36, 36, 44) : Theme.Back };
            bOk = new Button { AutoSize = true, MinimumSize = new Size(S(110), S(36)), FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10f), ForeColor = Color.White, BackColor = Theme.Dark ? Color.FromArgb(118, 102, 230) : Color.FromArgb(91, 76, 196), Margin = new Padding(S(6), 0, 0, 0) };
            bOk.FlatAppearance.BorderSize = 0;
            bCancel = new Button { AutoSize = true, MinimumSize = new Size(S(96), S(36)), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel, BackColor = Theme.Panel, ForeColor = Theme.Ink, Margin = new Padding(S(6), 0, 0, 0) };
            bCancel.FlatAppearance.BorderColor = Theme.Dark ? Color.FromArgb(90, 90, 108) : Color.FromArgb(206, 211, 222);
            bar.Controls.Add(bOk); bar.Controls.Add(bCancel);
            Controls.Add(body); Controls.Add(bar);
            AcceptButton = bOk; CancelButton = bCancel;
            KeyPreview = true;
            // a PIN takes digits only; a password and the recovery code take anything
            foreach (var t in new[] { t1, t2 }) t.KeyPress += (o, e) => { if (mode != Mode.Recover && Pin && !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            bOk.Click += (o, e) => Go();
            lnk.LinkClicked += (o, e) => Switch(mode == Mode.Enter ? Mode.Recover : Mode.Enter);
            wait = new Timer { Interval = 1000 }; wait.Tick += (o, e) => ShowWait();
            FormClosed += (o, e) => wait.Dispose();
            Shown += (o, e) => { Theme.DarkTitle(this); t1.Focus(); ShowWait(); };
            Switch(m);
        }

        public static DialogResult Ask(IWin32Window owner, PinForm f)
        {
            if (TestHook != null) return TestHook(f);
            return owner == null ? f.ShowDialog() : f.ShowDialog(owner);
        }

        void Switch(Mode m)
        {
            mode = m; lStatus.Text = "";
            if (m != Mode.Set) { var cur = PinLock.Current; if (cur.HasValue) kind = cur.Value; }
            t1.Text = ""; t2.Text = "";
            int len = PinLock.Digits(kind);
            t1.MaxLength = t2.MaxLength = m == Mode.Recover ? 19 : kind == PinLock.Kind.Password ? 64 : len > 0 ? len : 8;
            t1.UseSystemPasswordChar = m != Mode.Recover;
            float size = m == Mode.Recover ? 13f : Pin ? 16f : 12f;
            t1.Font = new Font("Segoe UI", size); t2.Font = new Font("Segoe UI", size);
            t2.Visible = m == Mode.Set;
            kinds.Visible = m == Mode.Set;
            lnk.Visible = m != Mode.Set;
            bool pw = kind == PinLock.Kind.Password;
            switch (m)
            {
                case Mode.Enter:
                    lTitle.Text = Lang.T(pw ? "Enter your password" : "Enter your PIN"); lText.Text = Lang.T(pw ? "Elixar is protected with a password." : "Elixar is protected with a PIN.");
                    bOk.Text = Lang.T("Unlock"); bCancel.Text = Lang.T("Close"); lnk.Text = Lang.T(pw ? "Forgot your password?" : "Forgot your PIN?"); break;
                case Mode.Recover:
                    lTitle.Text = Lang.T(pw ? "Forgot your password?" : "Forgot your PIN?");
                    lText.Text = Lang.T("Type the recovery code you got when you set it up (like ABCD-EFGH-JKMN-PQRS). It removes the PIN or password; you can set a new one in Settings > Security and protection.");
                    bOk.Text = Lang.T(pw ? "Remove the password" : "Remove the PIN"); bCancel.Text = Lang.T("Close"); lnk.Text = Lang.T(pw ? "Back to the password" : "Back to the PIN"); break;
                case Mode.Set:
                    lTitle.Text = Lang.T("Choose a PIN or password");
                    lText.Text = Lang.T(pw ? "8 to 64 characters: letters, digits and signs. Elixar asks for it every time it opens. Type it twice."
                                           : len + " digits. Elixar asks for it every time it opens, so other people using this PC can't open it. Type it twice.");
                    bOk.Text = Lang.T(pw ? "Set the password" : "Set the PIN"); bCancel.Text = Lang.T("Cancel"); break;
            }
            PerformLayout(); body.PerformLayout();
            ClientSize = new Size(S(400), body.PreferredSize.Height + S(70));
            if (Visible) t1.Focus();
        }

        void ShowWait()
        {
            if (mode == Mode.Set) { wait.Stop(); return; }
            int s = PinLock.SecondsLeft();
            if (s > 0)
            {
                lStatus.Text = Lang.T("Too many wrong tries. Try again in " + (s >= 60 ? (s / 60) + " min " + (s % 60) + " s" : s + " s") + ".");
                bOk.Enabled = t1.Enabled = false; if (!wait.Enabled) wait.Start();
            }
            else if (!bOk.Enabled) { lStatus.Text = ""; bOk.Enabled = t1.Enabled = true; wait.Stop(); t1.Focus(); }
        }

        void Go()
        {
            if (mode == Mode.Set)
            {
                if (!PinLock.Valid(kind, t1.Text)) { lStatus.Text = Lang.T(Pin ? "The PIN must be " + PinLock.Digits(kind) + " digits." : "The password must be 8 to 64 characters."); t1.Focus(); return; }
                if (t1.Text != t2.Text) { lStatus.Text = Lang.T("The two entries are different. Type it again."); t2.Text = ""; t2.Focus(); return; }
                try { RecoveryCode = PinLock.Set(kind, t1.Text); } catch (Exception ex) { lStatus.Text = ex.Message; return; }
                DialogResult = DialogResult.OK; return;
            }
            UseWaitCursor = true; Application.DoEvents();
            var r = mode == Mode.Enter ? PinLock.Check(t1.Text) : PinLock.Recover(t1.Text);
            UseWaitCursor = false;
            if (r == PinLock.Result.Ok)
            {
                if (mode == Mode.Recover) Lang.Box(this, "Removed. You can set a new PIN or password in Settings > Security and protection.", "Elixar", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK; return;
            }
            t1.Text = "";
            if (r == PinLock.Result.Wait) { ShowWait(); return; }
            int left = PinLock.TriesLeft();
            lStatus.Text = Lang.T(mode == Mode.Enter ? (Pin ? "Wrong PIN." : "Wrong password.") : "That recovery code isn't right.") + (left > 0 ? " " + Lang.T(left == 1 ? "1 try left before a short wait." : left + " tries left before a short wait.") : "");
            ShowWait(); t1.Focus();
        }

        // sets a PIN, then shows the recovery code once
        public static bool SetNew(IWin32Window owner)
        {
            string code;
            using (var f = new PinForm(Mode.Set)) { if (Ask(owner, f) != DialogResult.OK) return false; code = f.RecoveryCode; }
            ShowCode(owner, code);
            return true;
        }

        public static void ShowCode(IWin32Window owner, string code)
        {
            using (var f = new Form { Text = "Elixar", Icon = Brand.Load(32), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent,
                                      BackColor = Theme.Panel, ForeColor = Theme.Ink, Font = new Font("Segoe UI", 10f) })
            {
                float s = 1f; using (var g = f.CreateGraphics()) s = Math.Max(1f, g.DpiX / 96f);
                Func<int, int> S = v => (int)Math.Round(v * s);
                if (Lang.Rtl) { f.RightToLeft = RightToLeft.Yes; f.RightToLeftLayout = true; }
                var p = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(S(28), S(22), S(28), S(10)) };
                p.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f), Text = Lang.T("Done. Keep your recovery code"), Margin = new Padding(0, 0, 0, S(6)) });
                p.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(S(380), 0), ForeColor = Theme.Quiet, Margin = new Padding(0, 0, 0, S(12)),
                                           Text = Lang.T("If you forget the PIN or password, this code is the only way back in. Write it down or keep it in your password manager. Elixar shows it only now.") });
                var box = new TextBox { ReadOnly = true, Text = code, Font = new Font("Consolas", 18f), Width = S(360), TextAlign = HorizontalAlignment.Center, RightToLeft = RightToLeft.No, BackColor = Theme.Back, ForeColor = Theme.Ink, Margin = new Padding(0, 0, 0, S(10)) };
                p.Controls.Add(box);
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(20), S(10), S(20), S(12)), BackColor = Theme.Dark ? Color.FromArgb(36, 36, 44) : Theme.Back };
                var ok = new Button { AutoSize = true, MinimumSize = new Size(S(140), S(36)), Text = Lang.T("I saved it"), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                                      BackColor = Theme.Dark ? Color.FromArgb(118, 102, 230) : Color.FromArgb(91, 76, 196), Font = new Font("Segoe UI Semibold", 10f) };
                ok.FlatAppearance.BorderSize = 0;
                var copy = new Button { AutoSize = true, MinimumSize = new Size(S(96), S(36)), Text = Lang.T("Copy"), FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Ink, Margin = new Padding(S(6), 0, 0, 0) };
                copy.Click += (o, e) => { try { Clipboard.SetText(code); copy.Text = Lang.T("Copied"); } catch { } };
                bar.Controls.Add(ok); bar.Controls.Add(copy);
                f.Controls.Add(p); f.Controls.Add(bar); f.AcceptButton = ok;
                p.PerformLayout(); f.ClientSize = new Size(S(440), p.PreferredSize.Height + S(70));
                f.Shown += (o, e) => { Theme.DarkTitle(f); ok.Focus(); };
                if (TestHook != null) { f.Show(); TestHookCode(f); f.Close(); return; }
                if (owner == null) f.ShowDialog(); else f.ShowDialog(owner);
            }
        }
        public static Action<Form> TestHookCode = f => { };
    }
}
