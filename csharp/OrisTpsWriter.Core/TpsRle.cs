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
        /// Minimum run length worth compressing. The repeat marker costs 1-2
        /// bytes, so only runs longer than this are collapsed; shorter runs are
        /// emitted as literals.
        /// </summary>
        private const int RunThreshold = 4;

        /// <summary>
        /// Largest repeat count a single repeat marker can encode (same 2-byte
        /// limit as a skip count). Longer runs are split across units.
        /// </summary>
        private const int MaxRepeat = MaxSkipRun;

        /// <summary>
        /// RLE-encode raw data using the TopSpeed grammar:
        ///   [skip count][skip literal bytes][repeat count]
        /// where the repeat count repeats the LAST literal byte that many extra
        /// times. Runs of identical bytes (very common in space/zero-padded TPS
        /// records) are collapsed, which shrinks data and index pages a lot. The
        /// output round-trips through Unwrap — the same decoder the app and the
        /// real Clarion engine use — so it stays format-faithful.
        /// </summary>
        public static byte[] Wrap(byte[] raw)
        {
            int n = raw.Length;
            var outBytes = new List<byte>(n / 2 + 16);

            int pos = 0;
            while (pos < n)
            {
                // Find the next run (>= RunThreshold identical bytes) at or after pos.
                int runStart = -1, runByte = -1, runLen = 0;
                int scan = pos;
                while (scan < n)
                {
                    int len = 1;
                    while (scan + len < n && raw[scan + len] == raw[scan]) len++;
                    if (len >= RunThreshold)
                    {
                        runStart = scan; runByte = raw[scan]; runLen = len;
                        break;
                    }
                    scan += len; // treat this short run as part of the literals
                }

                int literalEnd; // exclusive
                int repeat;
                int nextPos;
                if (runStart < 0)
                {
                    // No more runs — everything left is literal.
                    literalEnd = n;
                    repeat = 0;
                    nextPos = n;
                }
                else
                {
                    // Literals run up to and INCLUDING the first run byte (which
                    // becomes the byte the repeat marker multiplies).
                    literalEnd = runStart + 1;
                    int rep = runLen - 1;
                    if (rep > MaxRepeat) rep = MaxRepeat; // cap; remainder handled next loop
                    repeat = rep;
                    nextPos = runStart + 1 + rep;
                }

                // Emit literal bytes [pos, literalEnd) in skip blocks. Only the
                // final block carries the real repeat; earlier splits use 0.
                int litLen = literalEnd - pos;
                int lp = pos;
                while (true)
                {
                    int chunk = Math.Min(MaxSkipRun, literalEnd - lp);
                    bool isLast = (lp + chunk) >= literalEnd;
                    AppendSkip(outBytes, chunk);
                    for (int i = 0; i < chunk; i++) outBytes.Add(raw[lp + i]);
                    lp += chunk;
                    AppendRepeat(outBytes, isLast ? repeat : 0);
                    if (isLast) break;
                }

                pos = nextPos;
            }

            // End sentinel: the decoder only consumes a repeat marker when at
            // least two bytes remain (`pos < n-1`). A trailing run would
            // otherwise leave its repeat count as the final byte and be skipped.
            // A single 0x00 — which Unwrap accepts as end-of-stream padding —
            // guarantees every real repeat marker is read.
            outBytes.Add(0x00);

            return outBytes.ToArray();
        }

        /// <summary>Append a repeat count in 1-byte or 2-byte form.</summary>
        private static void AppendRepeat(List<byte> outBytes, int count) =>
            AppendSkip(outBytes, count); // identical varint encoding to skip

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
