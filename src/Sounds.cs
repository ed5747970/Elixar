// Elixar's own sounds ("Potion": wood notes with bubbles, a cork pop, fizz and swooshes), made for Elixar (tools/sounds).
// Settings > Sounds: Potion (default), Windows' own sounds, or no sounds.
//   - built into Elixar.exe: no sound file on the disk is ever played, so nothing can be swapped in
//   - quiet (about half volume) and short; silent when Windows' sound scheme is "No Sounds"
//   - only for something you see in the window: scheduled runs and automatic cleans make no sound of their own

using System;
using System.Collections.Generic;
using System.IO;
using System.Media;
using Microsoft.Win32;

namespace CDriveCleaner
{
    public enum Snd { Question, Warning, Error, Info, Done, Better, Scan, Undo, Mail, Update, Alert, Pc, Open }

    public static class Sounds
    {
        public const string Potion = "potion", WindowsOwn = "windows", Off = "off";
        public static string Choice
        {
            get { string v = Settings.Get("Sounds", Potion); return v == WindowsOwn || v == Off ? v : Potion; }
            set { Settings.Set("Sounds", value == WindowsOwn || value == Off ? value : Potion); }
        }
        // the cork pop when Elixar opens: off unless you turn it on
        public static bool OnOpen { get { return Settings.Get("SoundOnOpen", "0") == "1"; } set { Settings.Set("SoundOnOpen", value ? "1" : "0"); } }

        public static readonly List<Snd> Played = new List<Snd>();   // self-test: what would have played
        public static bool Quiet;                                     // self-test and scheduled runs: record, never play

        static readonly Dictionary<Snd, byte[]> cache = new Dictionary<Snd, byte[]>();
        static SoundPlayer current;

        public static string ResourceName(Snd s) { return "Elixar.sounds.potion-" + s.ToString().ToLowerInvariant() + ".wav"; }

        // the sound built into Elixar.exe (null if this build has none)
        public static byte[] Data(Snd s)
        {
            lock (cache)
            {
                byte[] b;
                if (cache.TryGetValue(s, out b)) return b;
                try
                {
                    using (var st = typeof(Sounds).Assembly.GetManifestResourceStream(ResourceName(s)))
                    {
                        if (st == null) b = null;
                        else { var ms = new MemoryStream(); st.CopyTo(ms); b = ms.ToArray(); }
                    }
                }
                catch { b = null; }
                cache[s] = b;
                return b;
            }
        }

        // Windows' sound scheme set to "No Sounds" (Control Panel > Sound > Sounds)
        public static bool WindowsSilent()
        {
            if (!Win.IsWindows) return false;
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes")) return k != null && string.Equals(k.GetValue("") as string, ".None", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static void Play(Snd s) { Play(s, Choice); }

        public static void Play(Snd s, string choice)
        {
            if (choice == Off) return;
            lock (Played) { Played.Add(s); if (Played.Count > 200) Played.RemoveAt(0); }
            if (Quiet || !Win.IsWindows || WindowsSilent()) return;
            try
            {
                if (choice == WindowsOwn)
                {
                    // what Elixar did before it had its own sounds: Windows' sounds for its message boxes only
                    if (s == Snd.Warning) SystemSounds.Exclamation.Play();
                    else if (s == Snd.Error) SystemSounds.Hand.Play();
                    else if (s == Snd.Question) SystemSounds.Question.Play();
                    else if (s == Snd.Info) SystemSounds.Asterisk.Play();
                    return;
                }
                var b = Data(s);
                if (b == null) return;
                var p = new SoundPlayer(new MemoryStream(b, false));
                p.Load();
                lock (cache) current = p;   // kept while it plays
                p.Play();   // in the background: never holds up the window
            }
            catch { }
        }
    }
}
