using System;
using System.Collections.Generic;
using System.IO;

namespace OrisTpsWriter.Core
{
    /// <summary>
    /// არსებული .tps ფაილის წამკითხავი (real ORIS ფაილების ჩათვლით).
    ///
    /// მხარს უჭერს: RLE decompression, header-reuse compression,
    /// table definition (fields), ყველა data record.
    /// INSERT/UPDATE/DELETE-ის საფუძველი.
    /// </summary>
    public class TpsReader
    {
        public string TableName { get; private set; }
        public int TableNumber { get; private set; } = 1;
        public int DriverVersion { get; private set; }
        public int RecordLength { get; private set; }
        public long LastIssuedRow { get; private set; }
        public List<TpsField> Fields { get; } = new();
        /// <summary>recordNumber → raw row bytes</summary>
        public Dictionary<int, byte[]> DataRecords { get; } = new();

        private readonly byte[] _data;

        public TpsReader(string path)
        {
            _data = File.ReadAllBytes(path);
            Parse();
        }

        public TpsReader(byte[] data)
        {
            _data = data;
            Parse();
        }

        private static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
        private static uint U32(byte[] d, int o) =>
            (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        private static uint U32Be(byte[] d, int o) =>
            (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        private void Parse()
        {
            var d = _data;
            if (U32(d, 0) != 0)
                throw new InvalidDataException("File doesn't start with 0x00000000 (encrypted or not TPS).");
            if (d[0x0E] != (byte)'t' || d[0x0F] != (byte)'O' || d[0x10] != (byte)'p' || d[0x11] != (byte)'S')
                throw new InvalidDataException("Bad magic — not a TPS file.");

            LastIssuedRow = U32Be(d, 0x14);

            // blocks
            var blocks = new List<(int, int)>();
            for (int i = 0; i < 60; i++)
            {
                uint ps = U32(d, 0x20 + i * 4);
                uint pe = U32(d, 0x110 + i * 4);
                if (ps == 0 && pe == 0) continue;
                int so = (int)((ps << 8) + 0x200);
                int eo = (int)((pe << 8) + 0x200);
                if (so >= eo) continue;
                blocks.Add((so, eo));
            }

            foreach (var (so, eo) in blocks)
                ParseBlock(so, eo);

            // The table definition can be split across several 0xFA records
            // (each carries a 2-byte little-endian sequence number in its header).
            // Concatenate the fragments in order, then parse the whole thing once.
            if (_tableDefFragments.Count > 0)
            {
                int totalLen = 0;
                foreach (var kvp in _tableDefFragments) totalLen += kvp.Value.Length;
                var body = new byte[totalLen];
                int p = 0;
                foreach (var seq in _tableDefFragments.Keys) // SortedDictionary → ascending
                {
                    var frag = _tableDefFragments[seq];
                    Array.Copy(frag, 0, body, p, frag.Length);
                    p += frag.Length;
                }
                Fields.Clear();
                ParseTableDef(body);
            }
        }

        // sequence number → table-definition body fragment
        private readonly SortedDictionary<int, byte[]> _tableDefFragments = new();

        private void ParseBlock(int start, int end)
        {
            var d = _data;
            int pos = start;
            while (pos < end)
            {
                if (pos + 13 > d.Length) break;
                uint pageAddr = U32(d, pos);
                if (pageAddr != (uint)pos) { pos += 0x100; continue; }
                int pageSize = U16(d, pos + 4);
                int pageSizeUc = U16(d, pos + 6);
                int recCount = U16(d, pos + 0x0A);
                byte flags = d[pos + 0x0C];

                int compLen = pageSize - 13;
                if (compLen > 0 && pos + 13 + compLen <= d.Length)
                {
                    var comp = new byte[compLen];
                    Array.Copy(d, pos + 13, comp, 0, compLen);
                    try
                    {
                        byte[] dec = (pageSize != pageSizeUc && flags == 0)
                            ? TpsRle.Unwrap(comp) : comp;
                        ParsePageRecords(dec, recCount);
                    }
                    catch { /* index/management pages may not parse — skip */ }
                }

                pos = (pos + pageSize + 0xFF) & ~0xFF;
            }
        }

        private void ParsePageRecords(byte[] dec, int recCount)
        {
            int pos = 0;
            byte[] prevData = Array.Empty<byte>();
            int prevRecLen = 0, prevHdrLen = 0;

            for (int r = 0; r < recCount; r++)
            {
                if (pos >= dec.Length) break;
                byte fb = dec[pos]; pos++;

                int recLen = (fb & 0x80) != 0 ? U16(dec, pos) : prevRecLen;
                if ((fb & 0x80) != 0) pos += 2;
                int hdrLen = (fb & 0x40) != 0 ? U16(dec, pos) : prevHdrLen;
                if ((fb & 0x40) != 0) pos += 2;

                int copy = fb & 0x3F;
                int newCount = recLen - copy;
                if (newCount < 0 || pos + newCount > dec.Length) break;

                var recData = new byte[recLen];
                Array.Copy(prevData, 0, recData, 0, Math.Min(copy, prevData.Length));
                Array.Copy(dec, pos, recData, copy, newCount);
                pos += newCount;

                Classify(recData, hdrLen);

                prevData = recData;
                prevRecLen = recLen;
                prevHdrLen = hdrLen;
            }
        }

        private void Classify(byte[] recData, int hdrLen)
        {
            if (recData.Length < 5) return;

            if (recData[0] == 0xFE) // TableName
            {
                int len = Math.Min(hdrLen, recData.Length) - 1;
                if (len > 0)
                    TableName = System.Text.Encoding.ASCII.GetString(recData, 1, len);
                return;
            }

            byte typeByte = recData[4];
            uint tableNum = U32Be(recData, 0);

            if (typeByte == 0xFA) // TableDefinition (possibly fragmented)
            {
                TableNumber = (int)tableNum;
                int bodyLen = recData.Length - hdrLen;
                if (bodyLen > 0)
                {
                    var body = new byte[bodyLen];
                    Array.Copy(recData, hdrLen, body, 0, bodyLen);
                    // header layout: tableNum(4 BE) + 0xFA(1) + sequence(2 LE)
                    int seq = hdrLen >= 7 ? U16(recData, 5) : _tableDefFragments.Count;
                    _tableDefFragments[seq] = body; // collected; parsed after all blocks
                }
            }
            else if (typeByte == 0xF3) // Data
            {
                if (hdrLen >= 9)
                {
                    int recNum = (int)U32Be(recData, 5);
                    int rowLen = recData.Length - hdrLen;
                    var row = new byte[rowLen];
                    Array.Copy(recData, hdrLen, row, 0, rowLen);
                    DataRecords[recNum] = row;
                }
            }
        }

        private static bool IsKnownFieldType(byte t) =>
            t is 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
              or 0x08 or 0x09 or 0x0A or 0x12 or 0x13 or 0x14 or 0x16;

        private void ParseTableDef(byte[] body)
        {
            if (body.Length < 10) return;
            DriverVersion = U16(body, 0);
            RecordLength = U16(body, 2);
            int nFields = U16(body, 4);
            // memos @6, indexes @8
            int pos = 10;
            for (int i = 0; i < nFields; i++)
            {
                // Bounds: type(1)+offset(2)+name(≥1)+elements(2)+length(2)+flags(2)+index(2)
                if (pos + 1 > body.Length) break;
                byte ftype = body[pos]; pos++;

                if (pos + 2 > body.Length) break;
                int offset = U16(body, pos); pos += 2;

                int zi = Array.IndexOf(body, (byte)0, pos);
                if (zi < 0) break;
                string name = System.Text.Encoding.ASCII.GetString(body, pos, zi - pos);
                pos = zi + 1;

                if (pos + 8 > body.Length) break;
                int elements = U16(body, pos); pos += 2;
                int length   = U16(body, pos); pos += 2;
                int flags    = U16(body, pos); pos += 2;
                int index    = U16(body, pos); pos += 2;

                // type-specific extras
                int bcdDigits = 0, bcdLength = 0;
                if (ftype == 0x0A) // BCD: digitsAfterDecimal(1) + lengthOfElement(1)
                {
                    if (pos + 2 <= body.Length)
                    {
                        bcdDigits = body[pos];
                        bcdLength = body[pos + 1];
                    }
                    pos += 2;
                }
                else if (ftype == 0x12 || ftype == 0x13 || ftype == 0x14) // STRING/CSTRING/PSTRING
                {
                    pos += 2; // stringLength
                    // stringMask: zero-terminated. Consume up to and incl. the 0x00.
                    int zi2 = Array.IndexOf(body, (byte)0, pos);
                    pos = zi2 < 0 ? body.Length : zi2 + 1;
                }

                Fields.Add(new TpsField(name, ftype, offset, length, elements, flags, index)
                {
                    BcdDigits = bcdDigits,
                    BcdLength = bcdLength,
                });

                // Self-correct string-mask padding drift: some writers pad an empty
                // string-mask with an extra 0x00 (e.g. ARN.TPS) while standard Clarion
                // uses a single terminator. Skip up to 2 trailing 0x00 padding bytes if
                // doing so aligns the next field on a valid type byte.
                if (i < nFields - 1 && pos < body.Length && !IsKnownFieldType(body[pos]))
                {
                    int probe = pos, skipped = 0;
                    while (probe < body.Length && skipped < 2 && body[probe] == 0x00)
                    { probe++; skipped++; }
                    if (probe < body.Length && IsKnownFieldType(body[probe]))
                        pos = probe;
                }
            }
        }
    }
}
