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
        }

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

            if (typeByte == 0xFA) // TableDefinition
            {
                TableNumber = (int)tableNum;
                int bodyLen = recData.Length - hdrLen;
                if (bodyLen > 0)
                {
                    var body = new byte[bodyLen];
                    Array.Copy(recData, hdrLen, body, 0, bodyLen);
                    ParseTableDef(body);
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

        private void ParseTableDef(byte[] body)
        {
            if (body.Length < 10) return;
            DriverVersion = U16(body, 0);
            RecordLength = U16(body, 2);
            int nFields = U16(body, 4);
            // memos @6, indexes @8
            int pos = 10;
            for (int i = 0; i < nFields && pos < body.Length; i++)
            {
                byte ftype = body[pos]; pos++;
                int offset = U16(body, pos); pos += 2;
                int zi = Array.IndexOf(body, (byte)0, pos);
                string name = System.Text.Encoding.ASCII.GetString(body, pos, zi - pos);
                pos = zi + 1;
                int elements = U16(body, pos); pos += 2;
                int length = U16(body, pos); pos += 2;
                int flags = U16(body, pos); pos += 2;
                int index = U16(body, pos); pos += 2;

                if (ftype == 0x0A) pos += 2; // BCD
                else if (ftype == 0x12 || ftype == 0x13 || ftype == 0x14)
                {
                    pos += 2; // stringLength
                    int zi2 = Array.IndexOf(body, (byte)0, pos);
                    if (zi2 == pos) pos += 2;       // empty mask: 0x00 + extra
                    else pos = zi2 + 1;
                }

                Fields.Add(new TpsField(name, ftype, offset, length, elements, flags, index));
            }
        }
    }
}
