using System;
using System.Collections.Generic;
using System.IO;

namespace OrisTpsWriter.Core
{
    // -----------------------------------------------------------------------
    // მაღალი დონის ფილდის wrapper-ები
    // -----------------------------------------------------------------------

    /// <summary>ფილდის base interface.</summary>
    public interface IOrisField
    {
        string Name { get; }
        int Length { get; }
        byte FieldType { get; }
        byte[] Pack(object value);
    }

    /// <summary>ფიქსირებული სიგრძის ქართული/ASCII string (Clarion STRING).</summary>
    public class StringField : IOrisField
    {
        public string Name { get; }
        public int Length { get; }
        public byte FieldType => Core.FieldType.String;

        public StringField(string name, int length)
        {
            Name = name;
            Length = length;
        }

        public byte[] Pack(object value)
        {
            string s = value?.ToString() ?? string.Empty;
            return OrisEncoding.ToFixedField(s, Length);
        }
    }

    /// <summary>32-bit signed integer (Clarion LONG).</summary>
    public class LongField : IOrisField
    {
        public string Name { get; }
        public int Length => 4;
        public byte FieldType => Core.FieldType.Long;

        public LongField(string name) { Name = name; }

        public byte[] Pack(object value)
        {
            int v = value == null ? 0 : Convert.ToInt32(value);
            return BitConverter.GetBytes(v); // little-endian on x86/x64
        }
    }

    /// <summary>32-bit unsigned integer (Clarion ULONG).</summary>
    public class ULongField : IOrisField
    {
        public string Name { get; }
        public int Length => 4;
        public byte FieldType => Core.FieldType.ULong;

        public ULongField(string name) { Name = name; }

        public byte[] Pack(object value)
        {
            uint v = value == null ? 0 : Convert.ToUInt32(value);
            return BitConverter.GetBytes(v);
        }
    }

    /// <summary>16-bit signed integer (Clarion SHORT).</summary>
    public class ShortField : IOrisField
    {
        public string Name { get; }
        public int Length => 2;
        public byte FieldType => Core.FieldType.Short;

        public ShortField(string name) { Name = name; }

        public byte[] Pack(object value)
        {
            short v = value == null ? (short)0 : Convert.ToInt16(value);
            return BitConverter.GetBytes(v);
        }
    }

    // -----------------------------------------------------------------------
    // მაღალი დონის ცხრილი
    // -----------------------------------------------------------------------

    /// <summary>
    /// ORIS-compatible TPS ცხრილის builder (high-level API).
    ///
    /// გამოყენება:
    ///   var table = new OrisTable("PARTNERS");
    ///   table.AddField(new ULongField("PRT:ID"));
    ///   table.AddField(new StringField("PRT:NAME", 60));
    ///   table.AddKey("PRT:K1", new[] { "PRT:ID" });
    ///   table.AddRow(new() { ["PRT:ID"] = 1, ["PRT:NAME"] = "შპს ალფა" });
    ///   table.Save("output.tps");
    /// </summary>
    public class OrisTable
    {
        public string Name { get; }
        private readonly List<IOrisField> _fields = new();
        private readonly List<(string KeyName, string[] FieldNames, int Flags)> _keys = new();
        private readonly List<Dictionary<string, object>> _rows = new();

        public int RecordsPerPage { get; set; } = TpsWriter.DefaultRecordsPerPage;

        public OrisTable(string name = "UNNAMED")
        {
            Name = name;
        }

        public OrisTable AddField(IOrisField field)
        {
            _fields.Add(field);
            return this;
        }

        public OrisTable AddKey(string keyName, string[] fieldNames, int flags = 6)
        {
            _keys.Add((keyName, fieldNames, flags));
            return this;
        }

        public OrisTable AddRow(Dictionary<string, object> row)
        {
            _rows.Add(row);
            return this;
        }

        public int RecordCount => _rows.Count;
        public int FieldCount => _fields.Count;

        /// <summary>გენერირებს ფაილის bytes-ს.</summary>
        public byte[] Build()
        {
            // Compute offsets
            var offsets = new Dictionary<string, int>();
            int offset = 0;
            foreach (var f in _fields)
            {
                offsets[f.Name] = offset;
                offset += f.Length;
            }
            int recordLength = offset;

            // Low-level fields (sequential display index)
            var llFields = new List<TpsField>();
            for (int i = 0; i < _fields.Count; i++)
            {
                var f = _fields[i];
                llFields.Add(new TpsField(
                    f.Name, f.FieldType,
                    offset: offsets[f.Name],
                    length: f.Length,
                    flags: 0,
                    index: i // sequential display order
                ));
            }

            // Low-level indexes
            var fieldNameToIdx = new Dictionary<string, int>();
            for (int i = 0; i < _fields.Count; i++)
                fieldNameToIdx[_fields[i].Name] = i;

            var llIndexes = new List<TpsIndex>();
            foreach (var (keyName, fieldNames, flags) in _keys)
            {
                var keyFields = new List<(int, int)>();
                foreach (var fn in fieldNames)
                    keyFields.Add((fieldNameToIdx[fn], 0));
                llIndexes.Add(new TpsIndex(keyName, keyFields, flags));
            }

            // Pack data rows
            var dataRows = new List<(int, byte[])>();
            for (int i = 0; i < _rows.Count; i++)
            {
                using var ms = new MemoryStream();
                foreach (var f in _fields)
                {
                    object value = _rows[i].TryGetValue(f.Name, out var v) ? v : null;
                    byte[] packed = f.Pack(value);
                    ms.Write(packed, 0, packed.Length);
                }
                dataRows.Add((i + 1, ms.ToArray()));
            }

            return TpsWriter.Build(
                Name, llFields, recordLength, dataRows,
                indexes: llIndexes.Count > 0 ? llIndexes : null,
                recordsPerPage: RecordsPerPage
            );
        }

        /// <summary>ინახავს .tps ფაილს და აბრუნებს ბაიტების რაოდენობას.</summary>
        public int Save(string path)
        {
            byte[] tps = Build();
            File.WriteAllBytes(path, tps);
            return tps.Length;
        }
    }
}
