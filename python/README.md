# ORIS TPS Writer — Python

Pure-Python implementation. No external dependencies (standard library only).
Requires Python 3.7+.

## Files

| File | Purpose |
|------|---------|
| `oris_tps.py` | High-level API — **start here** |
| `tps_writer_v2.py` | Low-level writer engine |
| `tps_multipage.py` | Multi-page support for large tables |
| `tps_reader.py` | Reader for existing `.tps` files |
| `tps_insert.py` | INSERT / UPDATE / DELETE on existing files |
| `oris_encoding.py` | Georgian single-byte codec |
| `tps_rle.py` | TopSpeed RLE codec |
| `tps_verifier.py` | Reader / verifier for testing output |

## Editing an existing file

```python
from tps_insert import TpsTable

t = TpsTable.open("data.tps")
print(f"{t.count} records, last issued #{t.last_issued_row}")

rn = t.insert({"ARN:KADR": "ახალი გვარი", "ARN:SECT": ""})
t.update(rn, {"ARN:SECT": "A1"})
t.delete(42)

t.save("data.tps", backup=True)   # creates data.tps.bak_<timestamp>
```

## Example

```python
from oris_tps import OrisTable, StringField, ULongField, LongField

table = OrisTable("PARTNERS")
table.add_field(ULongField("PRT:ID"))
table.add_field(StringField("PRT:NAME", 60))
table.add_field(LongField("PRT:BALANCE"))
table.add_key("PRT:K1", ["PRT:ID"])

table.add_row({"PRT:ID": 1, "PRT:NAME": "შპს ალფა", "PRT:BALANCE": 5000000})
table.save("partners.tps")
```

## Large tables (multi-page)

For thousands of records, use `tps_multipage.build_tps_multipage` directly, or
just keep adding rows to `OrisTable` — paging is handled automatically by the
low-level engine.

## Verifying output

```bash
python tps_verifier.py partners.tps
```

Or open the generated file in the Scalabium Clarion Viewer.
