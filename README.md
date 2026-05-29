# ORIS TPS Writer

**The first open-source writer for Clarion TopSpeed (`.tps`) files** — with built-in support for the Georgian character encoding used by the ORIS accounting software.

Most existing open-source tools (tps-parse, TpsParser, tpsread) can only *read* `.tps` files. This project *writes* them — and the output is verified to load correctly in the real Clarion engine (Scalabium Clarion Viewer), not just in third-party parsers.

> Available in **Python** and **C# / .NET 8** (with a WPF desktop app).

---

## Why this exists

ORIS is a Georgian accounting application that stores its data in Clarion TopSpeed `.tps` files. The official way to write these files is SoftVelocity's commercial ODBC driver. This project provides a free, open alternative built entirely from reverse-engineering the format against real ORIS files.

Two hard problems are solved here:

1. **Writing valid `.tps` files from scratch** — header, blocks, pages, records, RLE compression, and B-tree-style key pages, all in the exact layout the Clarion engine expects.
2. **The ORIS Georgian encoding** — ORIS does not store Georgian text as UTF-8. It uses a fixed single-byte codepage (bytes `0xC0`–`0xE4` mapped to the Georgian alphabet) that renders as Cyrillic or Latin-1 glyphs depending on the font. This project includes the full, verified mapping.

---

## Quick start

### Python

```python
from oris_tps import OrisTable, StringField, ULongField, LongField

table = OrisTable("PARTNERS")
table.add_field(ULongField("PRT:ID"))
table.add_field(StringField("PRT:NAME", 60))
table.add_field(LongField("PRT:BALANCE"))
table.add_key("PRT:K1", ["PRT:ID"])

table.add_row({"PRT:ID": 1, "PRT:NAME": "შპს ალფა", "PRT:BALANCE": 5000000})
table.add_row({"PRT:ID": 2, "PRT:NAME": "ნედლეული მასალები", "PRT:BALANCE": -250000})

table.save("partners.tps")
```

### C# / .NET 8

```csharp
using OrisTpsWriter.Core;

var table = new OrisTable("PARTNERS");
table.AddField(new ULongField("PRT:ID"));
table.AddField(new StringField("PRT:NAME", 60));
table.AddField(new LongField("PRT:BALANCE"));
table.AddKey("PRT:K1", new[] { "PRT:ID" });

table.AddRow(new() { ["PRT:ID"] = 1, ["PRT:NAME"] = "შპს ალფა", ["PRT:BALANCE"] = 5000000 });
table.Save(@"C:\ORIS\Data\partners.tps");
```

---

## Repository layout

```
oris-tps-writer/
├── python/                 # Python implementation
│   ├── oris_tps.py         #   high-level API (start here)
│   ├── tps_writer_v2.py    #   low-level writer engine
│   ├── tps_multipage.py    #   multi-page support for large tables
│   ├── tps_reader.py       #   reader for existing files
│   ├── tps_insert.py       #   INSERT / UPDATE / DELETE
│   ├── oris_encoding.py    #   Georgian single-byte codec
│   ├── tps_rle.py          #   TopSpeed RLE codec
│   └── tps_verifier.py     #   reader/verifier
├── csharp/                 # C# / .NET 8 implementation
│   ├── OrisTpsWriter.Core/ #   class library (cross-platform)
│   │                       #     incl. TpsReader.cs, TpsTable.cs (editing)
│   ├── OrisTpsWriter/      #   WPF desktop app (Windows)
│   └── OrisTpsWriter.sln
├── samples/                # Example .tps output files
└── docs/                   # Format documentation
```

---

## Features

- ✅ Write `.tps` files from scratch — verified in the real Clarion engine
- ✅ **Edit existing `.tps` files** — INSERT / UPDATE / DELETE on real ORIS files
- ✅ Georgian text via the ORIS single-byte encoding
- ✅ Multiple field types: `STRING`, `LONG`, `ULONG`, `SHORT`
- ✅ Indexes / keys (required for records to display)
- ✅ Multi-page support — tested with 10,000+ records
- ✅ Block consolidation — no practical record limit
- ✅ Python and C# implementations with identical output
- ✅ WPF desktop app for non-programmers

## Editing existing files

INSERT / UPDATE / DELETE use a safe **read-modify-rewrite** strategy: the whole
file is parsed, modified in memory, and regenerated with a consistent index —
rather than risky in-place B-tree surgery. Existing records (numbers and
content) are fully preserved, and `lastIssuedRow` is carried over so new records
never collide with deleted ones.

```python
from tps_insert import TpsTable

t = TpsTable.open("data.tps")
rn = t.insert({"ARN:KADR": "ახალი გვარი", "ARN:SECT": ""})
t.update(rn, {"ARN:SECT": "A1"})
t.delete(42)
t.save("data.tps", backup=True)   # writes data.tps.bak_<timestamp> first
```

```csharp
using OrisTpsWriter.Core;

var t = TpsTable.Open(@"C:\ORIS\Data\data.tps");
int rn = t.Insert(new() { ["ARN:KADR"] = "ახალი გვარი", ["ARN:SECT"] = "" });
t.Save(@"C:\ORIS\Data\data.tps", backup: true);
```

> ⚠️ Always back up before editing production accounting data. Make sure ORIS is
> closed (no open file lock) while writing.

## Limitations

- Editing uses full regeneration (read-modify-rewrite), not in-place B-tree
  updates. This is safe and correct, but rewrites the entire file on each save.
- The Georgian mapping covers the 33 standard letters. Other custom ORIS glyphs
  may need to be added to the codec.
- Memo/BLOB fields are not yet supported.

---

## How it was built

The format was reverse-engineered against a real ORIS file (`ARN.TPS`) using [tps-parse](https://github.com/ctrl-alt-dev/tps-parse) as a reading reference and the Scalabium Clarion Viewer as ground truth. See [`docs/FORMAT.md`](docs/FORMAT.md) for the full list of discoveries — including several details that are not documented anywhere else (the page-reference encoding, the reserved block slots, and the index-definition byte layout).

## ქართულად

ეს არის პირველი ღია წყაროს writer Clarion TopSpeed `.tps` ფაილებისთვის, ORIS-ის ქართული კოდირების მხარდაჭერით. არსებული ბიბლიოთეკები მხოლოდ კითხვას აკეთებენ — ეს წერს, და შედეგი დადასტურებულია ნამდვილ Clarion engine-ში.

გამოყენება: იხ. ზემოთ Python/C# მაგალითები. დეტალური ფორმატის დოკუმენტაცია — [`docs/FORMAT.md`](docs/FORMAT.md).

---

## License

Apache License 2.0 — see [LICENSE](LICENSE).

Built on knowledge from [tps-parse](https://github.com/ctrl-alt-dev/tps-parse) (also Apache 2.0).

## Disclaimer

This software is provided as-is. Always back up your data before writing to any production accounting system. The authors are not responsible for data loss. This is an independent project and is not affiliated with ORIS or SoftVelocity.
