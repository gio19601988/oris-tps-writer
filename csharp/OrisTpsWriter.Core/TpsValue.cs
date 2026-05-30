using System;
using System.Globalization;

namespace OrisTpsWriter.Core
{
    /// <summary>
    /// Clarion TopSpeed DATE (0x04) and TIME (0x05) value codecs.
    ///
    /// DATE — 4 bytes, little-endian: [day, month, yearLo, yearHi]
    ///        i.e. uint32 = day | (month &lt;&lt; 8) | (year &lt;&lt; 16).
    ///        All-zero means "no date" → empty string.
    /// TIME — 4 bytes: [centiseconds, second, minute, hour].
    ///
    /// Decode → display/export string; Encode parses common string forms back.
    /// </summary>
    public static class TpsValue
    {
        public const string DateFormat = "yyyy-MM-dd";
        public const string TimeFormat = "HH:mm:ss";

        // ── DATE ─────────────────────────────────────────────────
        public static string DecodeDate(byte[] b)
        {
            if (b == null || b.Length < 4) return "";
            int day   = b[0];
            int month = b[1];
            int year  = b[2] | (b[3] << 8);
            if (day == 0 && month == 0 && year == 0) return "";
            if (year < 1 || year > 9999 || month < 1 || month > 12 ||
                day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999),
                                                       Math.Clamp(month, 1, 12)))
                return ""; // not a valid calendar date — show blank rather than garbage
            return new DateTime(year, month, day).ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        public static byte[] EncodeDate(object value)
        {
            var result = new byte[4]; // all-zero = empty
            if (value == null) return result;
            string s = value.ToString().Trim();
            if (s.Length == 0) return result;
            if (!TryParseDate(s, out DateTime dt)) return result;
            result[0] = (byte)dt.Day;
            result[1] = (byte)dt.Month;
            result[2] = (byte)(dt.Year & 0xFF);
            result[3] = (byte)((dt.Year >> 8) & 0xFF);
            return result;
        }

        private static bool TryParseDate(string s, out DateTime dt)
        {
            string[] fmts =
            {
                "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy", "dd/MM/yyyy", "dd-MM-yyyy",
                "MM/dd/yyyy", "yyyy-MM-dd HH:mm:ss", "d.M.yyyy", "d/M/yyyy"
            };
            if (DateTime.TryParseExact(s, fmts, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out dt))
                return true;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                     DateTimeStyles.None, out dt);
        }

        // ── TIME ─────────────────────────────────────────────────
        public static string DecodeTime(byte[] b)
        {
            if (b == null || b.Length < 4) return "";
            int centi = b[0];
            int sec   = b[1];
            int min   = b[2];
            int hour  = b[3];
            if (centi == 0 && sec == 0 && min == 0 && hour == 0) return "";
            if (hour > 23 || min > 59 || sec > 59) return "";
            return $"{hour:D2}:{min:D2}:{sec:D2}";
        }

        public static byte[] EncodeTime(object value)
        {
            var result = new byte[4]; // all-zero = empty
            if (value == null) return result;
            string s = value.ToString().Trim();
            if (s.Length == 0) return result;

            int hour = 0, min = 0, sec = 0, centi = 0;
            if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts))
            {
                hour = ts.Hours; min = ts.Minutes; sec = ts.Seconds;
                centi = ts.Milliseconds / 10;
            }
            else if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out var dt))
            {
                hour = dt.Hour; min = dt.Minute; sec = dt.Second;
                centi = dt.Millisecond / 10;
            }
            else return result;

            result[0] = (byte)centi;
            result[1] = (byte)sec;
            result[2] = (byte)min;
            result[3] = (byte)hour;
            return result;
        }
    }
}
