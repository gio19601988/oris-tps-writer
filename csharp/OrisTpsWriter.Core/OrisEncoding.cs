using System;
using System.Collections.Generic;
using System.Text;

namespace OrisTpsWriter.Core
{
    /// <summary>
    /// ORIS Georgian Encoding Codec.
    ///
    /// ORIS იყენებს single-byte custom codepage-ს, რომელიც ბაიტებს 0xC0-0xE4
    /// ქართულ ანბანს უსაბამებს. რენდერდება როგორც "А Б В" რუსული ფონტით
    /// ან "À Á Â" ლათინური ფონტით — ფაილში ბაიტი ერთი და იგივეა.
    ///
    /// წყარო: Converter_რეალური_სიმბოლოებით.xlsx (33 ქართული ასო).
    /// დადასტურებულია რეალური ARN.TPS ფაილით (ოქრიაშვილი, გოგიჩაძე და ა.შ.).
    /// </summary>
    public static class OrisEncoding
    {
        // Georgian Unicode char → ORIS byte
        private static readonly Dictionary<char, byte> GeoToByte = new()
        {
            {'\u10D0', 0xC0}, // ა
            {'\u10D1', 0xC1}, // ბ
            {'\u10D2', 0xC2}, // გ
            {'\u10D3', 0xC3}, // დ
            {'\u10D4', 0xC4}, // ე
            {'\u10D5', 0xC5}, // ვ
            {'\u10D6', 0xC6}, // ზ
            {'\u10D7', 0xC8}, // თ
            {'\u10D8', 0xC9}, // ი
            {'\u10D9', 0xCA}, // კ
            {'\u10DA', 0xCB}, // ლ
            {'\u10DB', 0xCC}, // მ
            {'\u10DC', 0xCD}, // ნ
            {'\u10DD', 0xCF}, // ო
            {'\u10DE', 0xD0}, // პ
            {'\u10DF', 0xD1}, // ჟ
            {'\u10E0', 0xD2}, // რ
            {'\u10E1', 0xD3}, // ს
            {'\u10E2', 0xD4}, // ტ
            {'\u10E3', 0xD6}, // უ
            {'\u10E4', 0xD7}, // ფ
            {'\u10E5', 0xD8}, // ქ
            {'\u10E6', 0xD9}, // ღ
            {'\u10E7', 0xDA}, // ყ
            {'\u10E8', 0xDB}, // შ
            {'\u10E9', 0xDC}, // ჩ
            {'\u10EA', 0xDD}, // ც
            {'\u10EB', 0xDE}, // ძ
            {'\u10EC', 0xDF}, // წ
            {'\u10ED', 0xE0}, // ჭ
            {'\u10EE', 0xE1}, // ხ
            {'\u10EF', 0xE3}, // ჯ
            {'\u10F0', 0xE4}, // ჰ
        };

        // ORIS byte → Georgian Unicode char (reverse map)
        private static readonly Dictionary<byte, char> ByteToGeo;

        static OrisEncoding()
        {
            ByteToGeo = new Dictionary<byte, char>();
            foreach (var kvp in GeoToByte)
                ByteToGeo[kvp.Value] = kvp.Key;
        }

        /// <summary>
        /// ქართული/ASCII string → ORIS bytes.
        /// ASCII (0x00-0x7F) გადადის as-is. Non-mapped chars → '?'.
        /// </summary>
        public static byte[] Encode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<byte>();

            var outBytes = new List<byte>(text.Length);
            foreach (char ch in text)
            {
                if (GeoToByte.TryGetValue(ch, out byte b))
                    outBytes.Add(b);
                else if (ch < 0x80)
                    outBytes.Add((byte)ch);
                else
                    outBytes.Add((byte)'?'); // non-mapped
            }
            return outBytes.ToArray();
        }

        /// <summary>
        /// ORIS bytes → ქართული/ASCII string.
        /// </summary>
        public static string Decode(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            var sb = new StringBuilder(data.Length);
            foreach (byte b in data)
            {
                if (ByteToGeo.TryGetValue(b, out char ch))
                    sb.Append(ch);
                else if (b < 0x80)
                    sb.Append((char)b);
                else
                    sb.Append('?');
            }
            return sb.ToString();
        }

        /// <summary>
        /// String → fixed-length ORIS field (Clarion STRING type).
        /// უფრო მოკლე → space-padded (0x20). უფრო გრძელი → truncated.
        /// </summary>
        public static byte[] ToFixedField(string text, int length, byte padByte = 0x20)
        {
            byte[] encoded = Encode(text ?? string.Empty);
            byte[] result = new byte[length];

            // Fill with pad byte first
            for (int i = 0; i < length; i++)
                result[i] = padByte;

            // Copy encoded bytes (truncate if too long)
            int copyLen = Math.Min(encoded.Length, length);
            Array.Copy(encoded, result, copyLen);

            return result;
        }

        /// <summary>
        /// Fixed-length ORIS field → trimmed string.
        /// </summary>
        public static string FromFixedField(byte[] data)
        {
            return Decode(data).TrimEnd();
        }
    }
}
