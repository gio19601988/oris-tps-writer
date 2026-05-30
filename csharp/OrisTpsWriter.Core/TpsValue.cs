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

        // ── Clarion "standard date" (LONG = days since 1800-12-28) ──
        // ORIS stores some dates as a LONG field rather than the 0x04 DATE
        // type (e.g. DOCS:DATE). Day 0 = 1800-12-28; useful range guards
        // against treating ordinary integers as dates.
        private static readonly DateTime ClarionEpoch = new(1800, 12, 28);
        private const long ClarionDateMin = 1;       // 1800-12-29
        private const long ClarionDateMax = 109207;  // ~2099-12-31

        public static bool IsClarionDateInRange(long days) =>
            days >= ClarionDateMin && days <= ClarionDateMax;

        public static string DecodeClarionDate(long days)
        {
            if (days == 0) return "";
            if (!IsClarionDateInRange(days)) return days.ToString(CultureInfo.InvariantCulture);
            return ClarionEpoch.AddDays(days).ToString(DateFormat, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parse a value back to a Clarion standard-date LONG. Accepts a date
        /// string (yyyy-MM-dd etc.) or a raw integer day count. Empty → 0.
        /// </summary>
        public static long EncodeClarionDate(object value)
        {
            if (value == null) return 0;
            string s = value.ToString().Trim();
            if (s.Length == 0) return 0;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
                return raw; // already a day count
            if (TryParseDate(s, out DateTime dt))
                return (long)(dt.Date - ClarionEpoch).TotalDays;
            return 0;
        }

        // ── BCD (Clarion packed decimal, type 0x0A) ──────────────
        // Stored as packed nibbles: high nibble of byte 0 is the sign
        // (non-zero = negative), every remaining nibble is one decimal digit.
        // `digits` = number of fractional digits (decimal places).
        public static string DecodeBcd(byte[] b, int digits)
        {
            if (b == null || b.Length == 0) return "";

            int signNibble = (b[0] >> 4) & 0x0F;
            bool negative = signNibble != 0 && signNibble != 0x0F ? false : signNibble == 0x0F;
            // Clarion convention: a 0xF in the top nibble marks negative; 0 marks positive.
            negative = signNibble == 0x0F;

            var sb = new System.Text.StringBuilder(b.Length * 2);
            // first byte: only low nibble is a digit (high nibble is sign)
            sb.Append((char)('0' + (b[0] & 0x0F)));
            for (int i = 1; i < b.Length; i++)
            {
                sb.Append((char)('0' + ((b[i] >> 4) & 0x0F)));
                sb.Append((char)('0' + (b[i] & 0x0F)));
            }

            string allDigits = sb.ToString().TrimStart('0');
            if (allDigits.Length == 0) allDigits = "0";

            string text;
            if (digits <= 0)
            {
                text = allDigits;
            }
            else
            {
                if (allDigits.Length <= digits)
                    allDigits = allDigits.PadLeft(digits + 1, '0');
                int split = allDigits.Length - digits;
                text = allDigits.Substring(0, split) + "." + allDigits.Substring(split);
            }

            if (text == "0" || (digits > 0 && IsAllZero(text))) negative = false;
            return negative ? "-" + text : text;
        }

        private static bool IsAllZero(string numeric)
        {
            foreach (char c in numeric)
                if (c != '0' && c != '.') return false;
            return true;
        }

        /// <summary>Encode a decimal string into a `byteLen`-byte BCD with `digits` decimals.</summary>
        public static byte[] EncodeBcd(object value, int byteLen, int digits)
        {
            var result = new byte[byteLen];
            if (byteLen == 0) return result;

            string s = value?.ToString()?.Trim() ?? "";
            if (s.Length == 0) return result;

            bool negative = s.StartsWith("-");
            if (negative) s = s.Substring(1);

            int dot = s.IndexOf('.');
            string intPart = dot < 0 ? s : s.Substring(0, dot);
            string fracPart = dot < 0 ? "" : s.Substring(dot + 1);

            // normalise fractional digits to `digits`
            if (digits > 0)
                fracPart = (fracPart.Length >= digits)
                    ? fracPart.Substring(0, digits)
                    : fracPart.PadRight(digits, '0');
            else
                fracPart = "";

            string all = (intPart + fracPart);
            foreach (char c in all) if (c < '0' || c > '9') return result; // invalid → zero
            all = all.TrimStart('0');
            if (all.Length == 0) all = "0";

            // total nibbles available: 2*byteLen, minus 1 for the sign nibble
            int maxDigits = byteLen * 2 - 1;
            if (all.Length > maxDigits) all = all.Substring(all.Length - maxDigits); // overflow guard
            all = all.PadLeft(maxDigits, '0');

            // pack: nibble 0 = sign, then digits
            int idx = 0;
            // high nibble of byte 0 = sign
            int signNibble = negative && !IsAllZero(all) ? 0x0F : 0x00;
            result[0] = (byte)((signNibble << 4) | (all[idx++] - '0'));
            for (int i = 1; i < byteLen; i++)
            {
                int hi = all[idx++] - '0';
                int lo = all[idx++] - '0';
                result[i] = (byte)((hi << 4) | lo);
            }
            return result;
        }
    }
}
