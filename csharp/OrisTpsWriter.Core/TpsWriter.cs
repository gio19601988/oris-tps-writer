using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace OrisTpsWriter.Core
{
    /// <summary>
    /// TPS ფაილის low-level writer engine.
    ///
    /// დადასტურებული discovery-ები (რეალური ARN.TPS-ის ანალიზიდან):
    ///   1. pageStart/pageEnd = page references: fileOffset = (ref &lt;&lt; 8) + 0x200
    ///   2. blocks იწყება index [2]-დან (პირველი ორი slot რეზერვირებული)
    ///   3. driverVersion = 1
    ///   4. tableNumber/recordNumber/lastIssuedRow = BIG-endian
    ///   5. data block + index block + definitions block ცალკე
    ///   6. RLE-wrapped pages
    ///   7. consecutive same-kind pages → ერთი block (consolidation)
    /// </summary>
    public static class TpsWriter
    {
        public const int HeaderSize = 0x200;
        public const int PageAlign = 0x100;
        public const int BlockStartIndex = 2; // გამარჯვებული ფორმულა
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("tOpS");

        // Record types
        public const byte TypeData      = 0xF3;
        public const byte TypeTableDef  = 0xFA;
        public const byte TypeTableName = 0xFE;

        public const int DefaultRecordsPerPage = 16;

        // -------------------------------------------------------------------
        // Little/Big-endian helpers
        // -------------------------------------------------------------------
        private static byte[] U16(int v) => new[] { (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF) };
        private static byte[] U32(long v) => new[]
        {
            (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF),
            (byte)((v >> 16) & 0xFF), (byte)((v >> 24) & 0xFF)
        };
        private static byte[] U32Be(long v) => new[]
        {
            (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
            (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)
        };

        private static int AlignUp(int value, int boundary = PageAlign)
            => value % boundary != 0 ? ((value / boundary) + 1) * boundary : value;

        // -------------------------------------------------------------------
        // Record builders
        // -------------------------------------------------------------------

        /// <summary>
        /// TableName record: header = 0xFE + name; payload = tableNumber (BE).
        /// </summary>
        private static byte[] BuildTableNameRecord(string tableName, int tableNumber)
        {
            using var hdr = new MemoryStream();
            hdr.WriteByte(TypeTableName);
            byte[] nameBytes = Encoding.ASCII.GetBytes(tableName);
            hdr.Write(nameBytes, 0, nameBytes.Length);
            byte[] header = hdr.ToArray();

            byte[] payload = U32Be(tableNumber);
            return WrapRecord(header, payload);
        }

        /// <summary>
        /// TableDef record: header = tableNumber(BE) + 0xFA + blockIndex(uint16);
        /// payload = TableDefinitionRecord body.
        /// </summary>
        private static byte[] BuildTableDefRecord(int tableNumber, byte[] body)
        {
            using var hdr = new MemoryStream();
            hdr.Write(U32Be(tableNumber), 0, 4);
            hdr.WriteByte(TypeTableDef);
            hdr.Write(U16(0), 0, 2); // block index
            byte[] header = hdr.ToArray();
            return WrapRecord(header, body);
        }

        /// <summary>
        /// Data record: header = tableNumber(BE) + 0xF3 + recordNumber(BE);
        /// payload = row bytes.
        /// </summary>
        private static byte[] BuildDataRecord(int tableNumber, int recordNumber, byte[] row)
        {
            using var hdr = new MemoryStream();
            hdr.Write(U32Be(tableNumber), 0, 4);
            hdr.WriteByte(TypeData);
            hdr.Write(U32Be(recordNumber), 0, 4);
            byte[] header = hdr.ToArray();
            return WrapRecord(header, row);
        }

        /// <summary>
        /// Index record: header = tableNumber(BE) + indexNum(1) + keyData;
        /// payload = recordNumber (BE).
        /// </summary>
        private static byte[] BuildIndexRecord(int tableNumber, int indexNum,
                                               byte[] keyData, int recordNumber)
        {
            using var hdr = new MemoryStream();
            hdr.Write(U32Be(tableNumber), 0, 4);
            hdr.WriteByte((byte)indexNum);
            hdr.Write(keyData, 0, keyData.Length);
            byte[] header = hdr.ToArray();
            return WrapRecord(header, U32Be(recordNumber));
        }

        /// <summary>
        /// TpsRecord wrapper: flags(0xC0) + recordLength(2) + headerLength(2) + data.
        /// ჩვენ ყოველთვის full header-ს ვწერთ (0xC0, no header reuse compression).
        /// </summary>
        private static byte[] WrapRecord(byte[] header, byte[] payload)
        {
            byte[] data = new byte[header.Length + payload.Length];
            Array.Copy(header, 0, data, 0, header.Length);
            Array.Copy(payload, 0, data, header.Length, payload.Length);

            using var ms = new MemoryStream();
            ms.WriteByte(0xC0);                       // full header marker
            ms.Write(U16(data.Length), 0, 2);         // recordLength
            ms.Write(U16(header.Length), 0, 2);       // headerLength
            ms.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        // -------------------------------------------------------------------
        // Page builder
        // -------------------------------------------------------------------

        /// <summary>
        /// TpsPage: addr(4) + pageSize(2) + uncompSize(2) + uncompNoHdr(2)
        ///          + recordCount(2) + flags(1) + RLE-wrapped data.
        /// </summary>
        private static byte[] BuildPage(List<byte[]> records, int pageAddr)
        {
            using var payloadMs = new MemoryStream();
            foreach (var rec in records)
                payloadMs.Write(rec, 0, rec.Length);
            byte[] payload = payloadMs.ToArray();
            int uncompressedSize = payload.Length;

            byte[] rlePayload = TpsRle.Wrap(payload);
            int pageSize = 13 + rlePayload.Length;
            int pageSizeUncompressed = 13 + uncompressedSize;

            using var ms = new MemoryStream();
            ms.Write(U32(pageAddr), 0, 4);                 // addr
            ms.Write(U16(pageSize), 0, 2);                 // pageSize (physical)
            ms.Write(U16(pageSizeUncompressed), 0, 2);     // pageSizeUncompressed
            ms.Write(U16(uncompressedSize), 0, 2);         // pageSizeUncompressedWithoutHeader
            ms.Write(U16(records.Count), 0, 2);            // recordCount
            ms.WriteByte(0x00);                            // flags
            ms.Write(rlePayload, 0, rlePayload.Length);
            return ms.ToArray();
        }

        // -------------------------------------------------------------------
        // Main file builder (multi-page + block consolidation)
        // -------------------------------------------------------------------

        /// <summary>
        /// მრავალ-page TPS ფაილის builder.
        /// </summary>
        /// <param name="tableName">ცხრილის სახელი</param>
        /// <param name="fields">ფილდები (sequential Index attribute)</param>
        /// <param name="recordLength">ერთი row-ის ფიზიკური ზომა</param>
        /// <param name="dataRows">(recordNumber, rowBytes) წყვილები</param>
        /// <param name="indexes">ინდექსები</param>
        /// <param name="recordsPerPage">data records per page</param>
        /// <param name="tableNumber">ცხრილის ნომერი</param>
        /// <param name="includeIndexPage">შევქმნათ თუ არა index page</param>
        public static byte[] Build(
            string tableName,
            List<TpsField> fields,
            int recordLength,
            List<(int RecordNumber, byte[] Row)> dataRows,
            List<TpsIndex> indexes = null,
            int recordsPerPage = DefaultRecordsPerPage,
            int tableNumber = 1,
            bool includeIndexPage = true,
            long lastIssuedRow = -1)
        {
            indexes ??= new List<TpsIndex>();

            // lastIssuedRow: max record number (not count) — INSERT-safe.
            // -1 → auto (max record number).
            if (lastIssuedRow < 0)
                lastIssuedRow = dataRows.Count > 0 ? dataRows.Max(r => r.RecordNumber) : 0;

            // --- Build TableDef body ---
            byte[] tableDefBody = BuildTableDefBody(fields, recordLength, indexes, driverVersion: 1);

            byte[] tableNameRec = BuildTableNameRecord(tableName, tableNumber);
            byte[] tableDefRec = BuildTableDefRecord(tableNumber, tableDefBody);

            // --- Layout: list of (pageBytes, start, end, kind) ---
            var layout = new List<(byte[] Bytes, int Start, int End, string Kind)>();
            int cursor = HeaderSize;

            // Data pages
            for (int i = 0; i < dataRows.Count; i += recordsPerPage)
            {
                var chunk = dataRows.GetRange(i, Math.Min(recordsPerPage, dataRows.Count - i));
                var recs = new List<byte[]>();
                foreach (var (rn, row) in chunk)
                    recs.Add(BuildDataRecord(tableNumber, rn, row));

                byte[] page = BuildPage(recs, cursor);
                int endRaw = cursor + page.Length;
                int end = AlignUp(endRaw);
                page = Pad(page, end - endRaw);
                layout.Add((page, cursor, end, "data"));
                cursor = end;
            }

            // Index pages
            if (includeIndexPage && indexes.Count > 0 && dataRows.Count > 0)
            {
                // Sort by row bytes (B-tree-ish order)
                var sortedRows = new List<(int RecordNumber, byte[] Row)>(dataRows);
                sortedRows.Sort((a, b) => CompareBytes(a.Row, b.Row));

                var indexRecs = new List<byte[]>();
                foreach (var (rn, row) in sortedRows)
                {
                    byte[] keyData = new byte[recordLength];
                    Array.Copy(row, keyData, Math.Min(row.Length, recordLength));
                    indexRecs.Add(BuildIndexRecord(tableNumber, 0, keyData, rn));
                }

                for (int i = 0; i < indexRecs.Count; i += recordsPerPage)
                {
                    var chunk = indexRecs.GetRange(i, Math.Min(recordsPerPage, indexRecs.Count - i));
                    byte[] page = BuildPage(chunk, cursor);
                    int endRaw = cursor + page.Length;
                    int end = AlignUp(endRaw);
                    page = Pad(page, end - endRaw);
                    layout.Add((page, cursor, end, "index"));
                    cursor = end;
                }
            }

            // Definitions page
            {
                byte[] page = BuildPage(new List<byte[]> { tableNameRec, tableDefRec }, cursor);
                int endRaw = cursor + page.Length;
                int end = AlignUp(endRaw);
                page = Pad(page, end - endRaw);
                layout.Add((page, cursor, end, "defs"));
                cursor = end;
            }

            int fileLength = cursor;

            // --- Block consolidation: consecutive same-kind pages → one block ---
            var blocks = new List<(int Start, int End)>();
            int? cbStart = null, cbEnd = null;
            string cbKind = null;
            foreach (var (_, start, end, kind) in layout)
            {
                if (cbKind == null)
                {
                    cbStart = start; cbEnd = end; cbKind = kind;
                }
                else if (kind == cbKind && start == cbEnd)
                {
                    cbEnd = end;
                }
                else
                {
                    blocks.Add((cbStart.Value, cbEnd.Value));
                    cbStart = start; cbEnd = end; cbKind = kind;
                }
            }
            if (cbStart.HasValue)
                blocks.Add((cbStart.Value, cbEnd.Value));

            // --- Header ---
            byte[] header = BuildHeader(blocks, fileLength, lastIssuedRow, dataRows.Count);

            // --- Assemble ---
            using var outMs = new MemoryStream();
            outMs.Write(header, 0, header.Length);
            foreach (var (bytes, _, _, _) in layout)
                outMs.Write(bytes, 0, bytes.Length);
            return outMs.ToArray();
        }

        // -------------------------------------------------------------------
        // TableDef body
        // -------------------------------------------------------------------
        private static byte[] BuildTableDefBody(List<TpsField> fields, int recordLength,
                                                List<TpsIndex> indexes, int driverVersion)
        {
            using var ms = new MemoryStream();
            ms.Write(U16(driverVersion), 0, 2);     // driverVersion
            ms.Write(U16(recordLength), 0, 2);      // recordLength
            ms.Write(U16(fields.Count), 0, 2);      // nrOfFields
            ms.Write(U16(0), 0, 2);                 // nrOfMemos
            ms.Write(U16(indexes.Count), 0, 2);     // nrOfIndexes
            foreach (var f in fields)
            {
                byte[] fb = f.Serialize();
                ms.Write(fb, 0, fb.Length);
            }
            foreach (var idx in indexes)
            {
                byte[] ib = idx.Serialize();
                ms.Write(ib, 0, ib.Length);
            }
            return ms.ToArray();
        }

        // -------------------------------------------------------------------
        // File header (0x200 bytes)
        // -------------------------------------------------------------------
        private static byte[] BuildHeader(List<(int Start, int End)> blocks,
                                          int fileLength, long lastIssuedRow, int recordCount)
        {
            int OffsetToRef(int off)
            {
                if (off < 0x200) return 0;
                int r = (off - 0x200) >> 8;
                if (((r << 8) + 0x200) != off)
                    throw new InvalidOperationException($"Offset 0x{off:X} not on 0x100 boundary");
                return r;
            }

            using var ms = new MemoryStream();
            ms.Write(U32(0), 0, 4);                       // +0x00 addr (must be 0)
            ms.Write(U16(HeaderSize), 0, 2);              // +0x04 hdrSize
            ms.Write(U32(fileLength), 0, 4);              // +0x06 fileLength1
            ms.Write(U32(fileLength), 0, 4);              // +0x0A fileLength2
            ms.Write(Magic, 0, 4);                        // +0x0E "tOpS"
            ms.Write(U16(0), 0, 2);                       // +0x12 zeros
            ms.Write(U32Be(lastIssuedRow), 0, 4);         // +0x14 lastIssuedRow (BE)
            ms.Write(U32(recordCount + 1), 0, 4);         // +0x18 changes
            ms.Write(U32(0), 0, 4);                       // +0x1C managementPageRef

            // pageStart[60] — leading reserved slots, then blocks
            var starts = new List<int>();
            for (int i = 0; i < BlockStartIndex; i++) starts.Add(0);
            foreach (var b in blocks) starts.Add(OffsetToRef(b.Start));
            while (starts.Count < 60) starts.Add(0);
            for (int i = 0; i < 60; i++) ms.Write(U32(starts[i]), 0, 4);

            // pageEnd[60]
            var ends = new List<int>();
            for (int i = 0; i < BlockStartIndex; i++) ends.Add(0);
            foreach (var b in blocks) ends.Add(OffsetToRef(b.End));
            while (ends.Count < 60) ends.Add(0);
            for (int i = 0; i < 60; i++) ms.Write(U32(ends[i]), 0, 4);

            byte[] result = ms.ToArray();
            if (result.Length != HeaderSize)
                throw new InvalidOperationException($"Header size {result.Length} != {HeaderSize}");
            return result;
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------
        private static byte[] Pad(byte[] data, int padCount)
        {
            if (padCount <= 0) return data;
            byte[] result = new byte[data.Length + padCount];
            Array.Copy(data, result, data.Length);
            return result;
        }

        private static int CompareBytes(byte[] a, byte[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int cmp = a[i].CompareTo(b[i]);
                if (cmp != 0) return cmp;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
