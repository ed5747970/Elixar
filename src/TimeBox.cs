// A time-of-day box (every 15 minutes) that follows the dark theme. Windows draws its own time picker white in any theme.

using System;
using System.Globalization;
using System.Windows.Forms;

namespace CDriveCleaner
{
    public class TimeBox : ComboBox
    {
        public TimeBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            MaxDropDownItems = 16;
            for (int m = 0; m < 24 * 60; m += 15) Items.Add(Text_(m));
            Value = DateTime.Today.AddHours(12);
        }

        static string Text_(int minutes) { return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00"); }

        public static int Minutes(string hhmm)
        {
            DateTime d;
            return DateTime.TryParseExact(hhmm ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.Hour * 60 + d.Minute : -1;
        }

        // today at the chosen time (a time that isn't on the list, e.g. from an older schedule, is added to it)
        public DateTime Value
        {
            get { int m = Minutes(SelectedItem as string); return DateTime.Today.AddMinutes(m < 0 ? 12 * 60 : m); }
            set
            {
                string t = Text_(value.Hour * 60 + value.Minute);
                int i = Items.IndexOf(t);
                if (i < 0)
                {
                    int pos = 0;
                    while (pos < Items.Count && string.CompareOrdinal((string)Items[pos], t) < 0) pos++;
                    Items.Insert(pos, t); i = pos;
                }
                SelectedIndex = i;
            }
        }
    }
}
