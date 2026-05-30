using System;
using System.Collections.Generic;

namespace OrisTpsWriter.Core
{
    /// <summary>
    /// TopSpeed RLE (Run-Length Encoding) codec.
    ///
    /// Clarion-ის pages RLE-compressed-ია. ჩვენ ვწერთ minimum-overhead RLE
    /// wrap-ს (ფაქტობრივი compression არ ხდება, მაგრამ ფორმატი RLE-compatible-ია,
    /// რასაც Clarion-ის engine ელის).
    ///
    /// დადასტურებულია რეალური ARN.TPS pages-ის decompression-ით.
    /// </summary>
    public static class TpsRle
    {
        /// <summary>
        /// Largest run a single skip block can represent. The 2-byte skip form
        /// encodes at most ((255&lt;&lt;7)&amp;0xFF00) + 0x7F + 0x80 = 32767 bytes.
        /// We cap chunks a little lower at a clean boundary for headroom.
        /// </summary>
        public const int MaxSkipRun = 0x7F00; // 32512

        /// <summary>
        /// RLE-encode raw data with no actual compression. Data larger than a
        /// single skip block is emitted as several skip blocks chained with a
        /// zero-length repeat (0x00) between them — which Unwrap (and the real
        /// Clarion engine) treats as "repeat last byte 0 times", a no-op. This
        /// removes the old single-block size limit so wide/large pages encode.
        /// </summary>
        public static byte[] Wrap(byte[] raw)
        {
            int n = raw.Length;
            var outBytes = new List<byte>(n + 8);

            int pos = 0;
            bool first = true;
            do
            {
                int chunk = Math.Min(MaxSkipRun, n - pos);

                // separator repeat (0 times) between consecutive skip blocks
                if (!first) outBytes.Add(0x00);
                first = false;

                AppendSkip(outBytes, chunk);
                for (int i = 0; i < chunk; i++) outBytes.Add(raw[pos + i]);
                pos += chunk;
            }
            while (pos < n);

            return outBytes.ToArray();
        }

        /// <summary>Append a skip count in 1-byte or 2-byte form.</summary>
        private static void AppendSkip(List<byte> outBytes, int count)
        {
            if (count <= 0x7F)
            {
                outBytes.Add((byte)count);
                return;
            }
            int lsb = count & 0x7F;
            int target = count - lsb;
            for (int msb = 0; msb < 256; msb++)
            {
                int computed = ((msb << 7) & 0x00FF00) + 0x80 * (msb & 0x01);
                if (computed == target)
                {
                    outBytes.Add((byte)(0x80 | lsb));
                    outBytes.Add((byte)msb);
                    return;
                }
            }
            // Unreachable: chunks are capped at MaxSkipRun, always encodable.
            throw new InvalidOperationException($"Skip count {count} not encodable.");
        }

        /// <summary>
        /// TopSpeed RLE decompression (verification/testing).
        /// </summary>
        public static byte[] Unwrap(byte[] compressed)
        {
            var outBytes = new List<byte>();
            int pos = 0;
            int n = compressed.Length;

            while (pos < n)
            {
                int skip = compressed[pos];
                pos++;

                if (skip == 0)
                {
                    // Trailing zero = page padding, end of stream
                    if (pos == n) break;
                    throw new InvalidOperationException(
                        $"Bad RLE Skip (0x00) at position {pos - 1}");
                }

                if (skip > 0x7F)
                {
                    if (pos >= n) throw new InvalidOperationException("Incomplete 2-byte skip");
                    int msb = compressed[pos];
                    pos++;
                    int lsb = skip & 0x7F;
                    int shift = 0x80 * (msb & 0x01);
                    skip = ((msb << 7) & 0x00FF00) + lsb + shift;
                }

                if (pos + skip > n)
                    throw new InvalidOperationException($"Skip overflow at {pos}");

                for (int i = 0; i < skip; i++)
                    outBytes.Add(compressed[pos + i]);
                pos += skip;

                // Repeat byte (if more than 1 byte remains)
                if (pos < n - 1)
                {
                    byte toRepeat = outBytes[outBytes.Count - 1];
                    int repeats = compressed[pos];
                    pos++;

                    if (repeats > 0x7F)
                    {
                        if (pos >= n) throw new InvalidOperationException("Incomplete 2-byte repeat");
                        int msb = compressed[pos];
                        pos++;
                        int lsb = repeats & 0x7F;
                        int shift = 0x80 * (msb & 0x01);
                        repeats = ((msb << 7) & 0x00FF00) + lsb + shift;
                    }

                    for (int i = 0; i < repeats; i++)
                        outBytes.Add(toRepeat);
                }
            }

            return outBytes.ToArray();
        }
    }
}
