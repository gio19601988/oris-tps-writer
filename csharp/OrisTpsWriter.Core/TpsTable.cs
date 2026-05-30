using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OrisTpsWriter.Core
{
    /// <summary>
    /// არსებული .tps ფაილის in-memory რედაქტირებადი წარმოდგენა.
    ///
    /// მიდგომა: read-modify-rewrite (უსაფრთხო) — წავიკითხავთ მთელ ფაილს,
    /// შევცვლით records-ს, შემდეგ regenerate ვაკეთებთ.
    ///
    /// გამოყენება:
    ///   var t = TpsTable.Open("data.tps");
    ///   t.Insert(new() { ["ARN:KADR"] = "ახალი", ["ARN:SECT"] = "" });
    ///   t.Save("data.tps", backup: true);
    ///
    /// *** ყოველთვის backup-ი აიღე ცვლილებამდე (backup: true default-ია). ***
    /// </summary>
    public class TpsTable
    {
        public string TableName { get; }
        public int TableNumber { get; }
        public int RecordLength { get; }
        public long LastIssuedRow { get; private set; }
        public List<TpsField> Fields { get; }

        // recordNumber → raw row bytes
        private readonly SortedDictionary<int, byte[]> _records;

        private static readonly Dictionary<byte, int> NumericSizes = new()
        {
            { FieldType.Byte, 1 }, { FieldType.Short, 2 }, { FieldType.UShort, 2 },
            { FieldType.Long, 4 }, { FieldType.ULong, 4 },
            { FieldType.Float, 4 }, { FieldType.Double, 8 },
        };

        private TpsTable(TpsReader reader)
        {
            TableName = reader.TableName ?? "UNNAMED";
            TableNumber = reader.TableNumber;
            RecordLength = reader.RecordLength;
            Fields = reader.Fields;
            LastIssuedRow = reader.LastIssuedRow;
            _records = new SortedDictionary<int, byte[]>();
            foreach (var kvp in reader.DataRecords)
                _records[kvp.Key] = kvp.Value;
        }

        public static TpsTable Open(string path) => new(new TpsReader(path));
        public static TpsTable Open(byte[] data) => new(new TpsReader(data));

        public int Count => _records.Count;

        // ----------------------------------------------------------------
        private byte[] PackRow(Dictionary<string, object> values)
        {
            using var ms = new MemoryStream();
            foreach (var f in Fields.OrderBy(f => f.Offset))
            {
                values.TryGetValue(f.Name, out object val);
                if (f.Type == FieldType.Date)
                {
                    ms.Write(TpsValue.EncodeDate(val), 0, 4);
                }
                else if (f.Type == FieldType.Time)
                {
                    ms.Write(TpsValue.EncodeTime(val), 0, 4);
                }
                else if (NumericSizes.TryGetValue(f.Type, out int size))
                {
                    long n = val == null || (val is string s && s == "")
                        ? 0 : Convert.ToInt64(val);
                    var bytes = BitConverter.GetBytes(n); // little-endian
                    ms.Write(bytes, 0, size);
                }
                else
                {
                    string str = val?.ToString() ?? "";
                    ms.Write(OrisEncoding.ToFixedField(str, f.Length), 0, f.Length);
                }
            }
            return ms.ToArray();
        }

        private Dictionary<string, object> UnpackRow(byte[] row)
        {
            var outDict = new Dictionary<string, object>();
            foreach (var f in Fields)
            {
                var chunk = new byte[f.Length];
                Array.Copy(row, f.Offset, chunk, 0, Math.Min(f.Length, row.Length - f.Offset));
                if (f.Type == FieldType.Date)
                {
                    outDict[f.Name] = TpsValue.DecodeDate(chunk);
                }
                else if (f.Type == FieldType.Time)
                {
                    outDict[f.Name] = TpsValue.DecodeTime(chunk);
                }
                else if (NumericSizes.TryGetValue(f.Type, out int size))
                {
                    long n = 0;
                    for (int i = 0; i < size; i++) n |= (long)chunk[i] << (8 * i);
                    outDict[f.Name] = n;
                }
                else
                {
                    outDict[f.Name] = OrisEncoding.FromFixedField(chunk);
                }
            }
            return outDict;
        }

        // ----------------------------------------------------------------
        // CRUD
        // ----------------------------------------------------------------
        public int Insert(Dictionary<string, object> values)
        {
            LastIssuedRow++;
            int rn = (int)LastIssuedRow;
            _records[rn] = PackRow(values);
            return rn;
        }

        public List<int> InsertMany(IEnumerable<Dictionary<string, object>> rows)
            => rows.Select(Insert).ToList();

        public void Update(int recordNumber, Dictionary<string, object> values)
        {
            if (!_records.ContainsKey(recordNumber))
                throw new KeyNotFoundException($"record #{recordNumber} არ არსებობს");
            var existing = UnpackRow(_records[recordNumber]);
            foreach (var kvp in values) existing[kvp.Key] = kvp.Value;
            _records[recordNumber] = PackRow(existing);
        }

        public void Delete(int recordNumber) => _records.Remove(recordNumber);

        public Dictionary<string, object> Get(int recordNumber)
            => UnpackRow(_records[recordNumber]);

        public IEnumerable<(int RecordNumber, Dictionary<string, object> Values)> AllRows()
        {
            foreach (var kvp in _records)
                yield return (kvp.Key, UnpackRow(kvp.Value));
        }

        // ----------------------------------------------------------------
        public int Save(string path, bool backup = true)
        {
            if (backup && File.Exists(path))
            {
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(path, $"{path}.bak_{ts}", overwrite: false);
            }

            var ordered = Fields.OrderBy(f => f.Offset).ToList();
            // sequential display index
            for (int i = 0; i < ordered.Count; i++) ordered[i].Index = i;

            var indexes = new List<TpsIndex>
            {
                new($"{TableName}:K1", new List<(int, int)> { (0, 0) }, flags: 6)
            };

            var dataRows = _records
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();

            byte[] tps = TpsWriter.Build(
                TableName, ordered, RecordLength, dataRows,
                indexes: indexes, tableNumber: TableNumber,
                lastIssuedRow: LastIssuedRow);

            File.WriteAllBytes(path, tps);
            return tps.Length;
        }
    }
}
