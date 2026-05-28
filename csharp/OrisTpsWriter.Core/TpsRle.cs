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
        /// RLE-encode raw data as a single 'skip' block (no actual compression).
        /// </summary>
        public static byte[] Wrap(byte[] raw)
        {
            int n = raw.Length;
            var outBytes = new List<byte>(n + 4);

            if (n <= 0x7F)
            {
                // 1-byte skip
                outBytes.Add((byte)n);
                outBytes.AddRange(raw);
            }
            else
            {
                // 2-byte skip — find msb such that:
                //   ((msb << 7) & 0xFF00) + (n & 0x7F) + 0x80 * (msb & 1) == n
                int lsb = n & 0x7F;
                int target = n - lsb;
                bool found = false;
                for (int msb = 0; msb < 256; msb++)
                {
                    int computed = ((msb << 7) & 0x00FF00) + 0x80 * (msb & 0x01);
                    if (computed == target)
                    {
                        outBytes.Add((byte)(0x80 | lsb));
                        outBytes.Add((byte)msb);
                        outBytes.AddRange(raw);
                        found = true;
                        break;
                    }
                }
                if (!found)
                    throw new InvalidOperationException(
                        $"Cannot RLE-encode data of size {n} as a single skip block. " +
                        "გამოიყენე multi-page split.");
            }

            return outBytes.ToArray();
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
