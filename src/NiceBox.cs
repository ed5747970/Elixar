// Elixar's own pop-up, in the app's look (light or dark, English or Hebrew), used instead of Windows' grey message box.
//
// The text is laid out by meaning, so every existing message looks right without rewriting it:
//   - the first short paragraph is the title (bold)
//   - lines starting with "- " or "• " become a list in a soft card; "Thumbnail cache (30 MB)" shows the size on the right
//   - a short "Label: value" paragraph ("Estimated space freed: 50 MB") shows the value in bold
//   - a short warning ("This cannot be undone.", "...deleted for good") is shown in red
// Buttons: the main one is filled (red when the action deletes or warns), the other one is outlined. Enter presses the
// default button, Esc cancels, Ctrl+C copies the whole text (like Windows' own box).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class NiceBox : Form
    {
        public static new bool Enabled = true;                // self-test can switch back to Windows' box
        public static Func<NiceBox, DialogResult> TestHook;   // previews and the self-test: look at the box instead of showing it

        readonly string fullText;
        readonly float sc;
        public string TitleText { get; private set; }
        public List<string> Bullets = new List<string>();
        public List<Button> ButtonsShown = new List<Button>();
        public bool DangerStyle { get; private set; }

        static readonly Regex Bullet = new Regex(@"^\s*(?:[-•*]\s+)(.+)$");
        static readonly Regex Sized = new Regex(@"^(.+?)\s+\(([\d.,]+\s?(?:B|KB|MB|GB|TB|bytes)|varies|משתנה)\)$");
        static readonly Regex LabelValue = new Regex(@"^([^:\n]{3,48}):\s+([^\n]{1,40})$");
        static readonly Regex Final = new Regex(@"(cannot|can't|can not) be undone|deleted for good|for good\.$|לא ניתן לבטל|אי אפשר לבטל|לצמיתות", RegexOptions.IgnoreCase);

        int S(int v) { return (int)Math.Round(v * sc); }

        // the same box with its own button names (for example "Connect email...", "Not now", "Don't ask again")
        [ThreadStatic] static string[] nextLabels;
        public string[] Labels { get; private set; }
        public static DialogResult ShowWith(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, int defaultButton, string[] labels)
        {
            if (!Enabled) return Show(owner, text, caption, buttons, icon, defaultButton);
            nextLabels = labels;
            try { return Show(owner, text, caption, buttons, icon, defaultButton); }
            finally { nextLabels = null; }
        }

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, int defaultButton)
        {
            if (!Enabled) return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton == 1 ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1,
                                                 Lang.Rtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
            using (var f = new NiceBox(text ?? "", string.IsNullOrEmpty(caption) ? "Elixar" : caption, buttons, icon, defaultButton))
            {
                if (TestHook != null) return TestHook(f);
                try
                {
                    if (icon == MessageBoxIcon.Warning) SystemSounds.Exclamation.Play();
                    else if (icon == MessageBoxIcon.Error) SystemSounds.Hand.Play();
                    else if (icon == MessageBoxIcon.Question) SystemSounds.Question.Play();
                    else if (icon == MessageBoxIcon.Information) SystemSounds.Asterisk.Play();
                }
                catch { }
                return owner == null ? f.ShowDialog() : f.ShowDialog(owner);
            }
        }

        NiceBox(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, int defaultButton)
        {
            fullText = text;
            using (var g = CreateGraphics()) sc = Math.Max(1f, g.DpiX / 96f);
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Theme.Panel; ForeColor = Theme.Ink;
            KeyPreview = true;
            if (Lang.Rtl) { RightToLeft = RightToLeft.Yes; RightToLeftLayout = true; }

            Color tone = icon == MessageBoxIcon.Error ? Theme.Danger : icon == MessageBoxIcon.Warning ? Theme.MapFore(Color.FromArgb(196, 120, 0)) : Theme.Accent;
            DangerStyle = (icon == MessageBoxIcon.Warning || icon == MessageBoxIcon.Error) && buttons != MessageBoxButtons.OK;

            int width = S(460), pad = S(22), iconSize = S(40);
            var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0), Padding = new Padding(0), BackColor = Theme.Panel };
            int textW = width - pad * 2 - iconSize - S(14);

            // paragraphs -> title, text, lists, label/value rows, final warning
            var paras = text.Replace("\r\n", "\n").Split(new[] { "\n\n" }, StringSplitOptions.None).Select(p => p.Trim('\n')).Where(p => p.Trim().Length > 0).ToList();
            int start = 0;
            if (paras.Count > 0 && paras[0].Length <= 150 && !paras[0].Split('\n').Any(l => Bullet.IsMatch(l)))
            {
                TitleText = paras[0];
                body.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(textW, 0), Text = paras[0], Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Ink, Margin = new Padding(0, S(2), 0, S(8)), UseMnemonic = false });
                start = 1;
            }
            for (int i = start; i < paras.Count; i++)
            {
                var lines = paras[i].Split('\n');
                var plain = new List<string>();
                Action flush = () =>
                {
                    if (plain.Count == 0) return;
                    string t = string.Join("\n", plain); plain.Clear();
                    Match lv = LabelValue.Match(t);
                    if (lv.Success && !t.Contains("\n"))
                    {
                        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, S(8)), BackColor = Theme.Panel };
                        row.Controls.Add(new Label { AutoSize = true, Text = lv.Groups[1].Value + ":", ForeColor = Theme.Quiet, Margin = new Padding(0, S(1), S(4), 0), UseMnemonic = false });
                        row.Controls.Add(new Label { AutoSize = true, Text = lv.Groups[2].Value, Font = new Font("Segoe UI Semibold", 9.75f), ForeColor = Theme.Ink, Margin = new Padding(0), UseMnemonic = false });
                        body.Controls.Add(row);
                    }
                    else if (t.Length <= 90 && Final.IsMatch(t))
                        body.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(textW, 0), Text = "●  " + t, Font = new Font("Segoe UI Semibold", 9f), ForeColor = Theme.Danger, Margin = new Padding(0, S(2), 0, S(8)), UseMnemonic = false });
                    else
                        body.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(textW, 0), Text = t, ForeColor = TitleText == null && body.Controls.Count == 0 ? Theme.Ink : Theme.Quiet, Margin = new Padding(0, 0, 0, S(8)), UseMnemonic = false });
                };
                var items = new List<string>();
                Action flushItems = () => { if (items.Count == 0) return; body.Controls.Add(ListCard(items.ToList(), textW)); Bullets.AddRange(items); items.Clear(); };
                foreach (var l in lines)
                {
                    var m = Bullet.Match(l);
                    if (m.Success) { flush(); items.Add(m.Groups[1].Value.Trim()); }
                    else { flushItems(); plain.Add(l); }
                }
                flush(); flushItems();
            }

            // the icon: a soft circle in the message's colour
            var pic = new PictureBox { Size = new Size(iconSize, iconSize), Margin = new Padding(0, S(2), S(14), 0), BackColor = Theme.Panel };
            var bmp = new Bitmap(iconSize, iconSize);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 70 : 38, tone))) g.FillEllipse(b, 0, 0, iconSize - 1, iconSize - 1);
                if (icon == MessageBoxIcon.None)
                {
                    try { using (var ic = Brand.Load(32)) g.DrawIcon(ic, new Rectangle(S(8), S(8), iconSize - S(16), iconSize - S(16))); } catch { }
                }
                else
                {
                    string glyph = icon == MessageBoxIcon.Error ? "×" : icon == MessageBoxIcon.Warning ? "!" : icon == MessageBoxIcon.Question ? "?" : "i";
                    using (var gf = new Font("Segoe UI", icon == MessageBoxIcon.Error ? 20f : 16f, FontStyle.Bold, GraphicsUnit.Point))
                    using (var b = new SolidBrush(tone))
                    using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(glyph, gf, b, new RectangleF(0, 0, iconSize, iconSize + S(1)), fmt);
                }
            }
            pic.Image = bmp;

            var top = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(pad, pad, pad, S(10)), Margin = new Padding(0), BackColor = Theme.Panel, Dock = DockStyle.Top };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(pic, 0, 0); top.Controls.Add(body, 1, 0);

            // a body taller than most of the screen scrolls
            var scroll = new Panel { AutoScroll = true, Dock = DockStyle.Fill, BackColor = Theme.Panel };
            scroll.Controls.Add(top);

            // the button bar
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(pad, S(12), pad - S(6), S(12)), BackColor = Theme.Dark ? Color.FromArgb(36, 36, 44) : Theme.Back, AutoSize = true, WrapContents = false };
            var defs = ButtonsFor(buttons);
            Labels = nextLabels; nextLabels = null;
            if (Labels != null)
                for (int i = 0; i < defs.Count && i < Labels.Length; i++)
                    if (!string.IsNullOrEmpty(Labels[i])) defs[i] = Tuple.Create(Labels[i], defs[i].Item2);
            int def = Math.Max(0, Math.Min(defs.Count - 1, defaultButton));
            Button cancel = null;
            for (int k = defs.Count - 1; k >= 0; k--)
            {
                bool primary = k == 0;
                var b = new Button
                {
                    Text = Lang.T(defs[k].Item1), DialogResult = defs[k].Item2, AutoSize = true, MinimumSize = new Size(S(96), S(34)), Margin = new Padding(S(6), 0, 0, 0),
                    FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 9.5f), Cursor = Cursors.Hand, UseMnemonic = false, Padding = new Padding(S(10), 0, S(10), 0)
                };
                if (primary)
                {
                    var fill = DangerStyle ? (Theme.Dark ? Color.FromArgb(214, 72, 64) : Color.FromArgb(199, 54, 47)) : Theme.Dark ? Color.FromArgb(118, 102, 230) : Color.FromArgb(91, 76, 196);
                    b.BackColor = fill; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0;
                    b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(fill, 0.15f); b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(fill, 0.05f);
                }
                else
                {
                    b.BackColor = Theme.Panel; b.ForeColor = Theme.Ink; b.FlatAppearance.BorderColor = Theme.Dark ? Color.FromArgb(90, 90, 108) : Color.FromArgb(206, 211, 222);
                    b.FlatAppearance.MouseOverBackColor = Theme.Dark ? Color.FromArgb(56, 56, 68) : Color.FromArgb(243, 241, 255);
                }
                bar.Controls.Add(b);
                ButtonsShown.Insert(0, b);
                if (defs[k].Item2 == DialogResult.Cancel || defs[k].Item2 == DialogResult.No) cancel = b;
            }
            if (cancel == null && ButtonsShown.Count == 1) cancel = ButtonsShown[0];
            AcceptButton = ButtonsShown[def]; CancelButton = cancel;
            if (cancel == null) ControlBox = false;   // like Windows: Abort/Retry boxes have no X

            Controls.Add(scroll); Controls.Add(bar);
            top.PerformLayout(); bar.PerformLayout();
            var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
            int h = Math.Min(top.PreferredSize.Height, (int)(screen.Height * 0.7)) + bar.PreferredSize.Height;
            ClientSize = new Size(Math.Max(width, Math.Min(screen.Width - S(80), top.PreferredSize.Width + SystemInformation.VerticalScrollBarWidth)), h);
            Shown += (o, e) => { ButtonsShown[def].Focus(); Theme.DarkTitle(this); };
            KeyDown += (o, e) => { if (e.Control && e.KeyCode == Keys.C) { try { Clipboard.SetText(Text + "\n\n" + fullText); } catch { } e.Handled = true; } };
        }

        // a soft card with one row per item; a size in brackets goes to the right
        Control ListCard(List<string> items, int w)
        {
            var card = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, S(2), 0, S(10)),
                                              Padding = new Padding(S(12), S(8), S(12), S(8)), BackColor = Theme.Dark ? Color.FromArgb(52, 52, 62) : Theme.Back, MinimumSize = new Size(w, 0), MaximumSize = new Size(w, 0) };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bool sizes = items.Take(40).Any(i => Sized.IsMatch(i));   // no sizes: the text gets the whole width
            foreach (var it in items.Take(40))
            {
                var m = Sized.Match(it);
                string name = m.Success ? m.Groups[1].Value : it, size = m.Success ? m.Groups[2].Value : "";
                card.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(w - S(sizes ? 110 : 30), 0), Text = "•  " + name, ForeColor = Theme.Ink, Margin = new Padding(0, S(3), S(8), S(3)), UseMnemonic = false });
                card.Controls.Add(new Label { AutoSize = true, Text = size, ForeColor = Theme.Quiet, Font = new Font("Segoe UI Semibold", 9.25f), Margin = new Padding(0, S(3), 0, S(3)), Anchor = AnchorStyles.Right, UseMnemonic = false });
            }
            if (items.Count > 40) card.Controls.Add(new Label { AutoSize = true, Text = "+ " + (items.Count - 40), ForeColor = Theme.Quiet, Margin = new Padding(0, S(3), 0, 0) });
            card.Paint += (o, e) =>
            {
                using (var p = new Pen(Theme.Grid)) e.Graphics.DrawRectangle(p, 0, 0, card.Width - 1, card.Height - 1);
            };
            return card;
        }

        static List<Tuple<string, DialogResult>> ButtonsFor(MessageBoxButtons b)
        {
            var l = new List<Tuple<string, DialogResult>>();
            Action<string, DialogResult> add = (t, r) => l.Add(Tuple.Create(t, r));
            switch (b)
            {
                case MessageBoxButtons.OKCancel: add("OK", DialogResult.OK); add("Cancel", DialogResult.Cancel); break;
                case MessageBoxButtons.YesNo: add("Yes", DialogResult.Yes); add("No", DialogResult.No); break;
                case MessageBoxButtons.YesNoCancel: add("Yes", DialogResult.Yes); add("No", DialogResult.No); add("Cancel", DialogResult.Cancel); break;
                case MessageBoxButtons.RetryCancel: add("Retry", DialogResult.Retry); add("Cancel", DialogResult.Cancel); break;
                case MessageBoxButtons.AbortRetryIgnore: add("Abort", DialogResult.Abort); add("Retry", DialogResult.Retry); add("Ignore", DialogResult.Ignore); break;
                default: add("OK", DialogResult.OK); break;
            }
            return l;
        }
    }
}
