# Clarion TopSpeed `.tps` format — writing notes

This document captures what was needed to **write** valid `.tps` files that the
real Clarion engine accepts. The reading format is well covered by
[tps-parse](https://github.com/ctrl-alt-dev/tps-parse); the notes below focus on
the details that matter for *writing* and that are not documented elsewhere.

All multi-byte integers are little-endian **unless noted as BE** (big-endian).

## File layout

```
+-----------------------------+ 0x000
| File header (0x200 bytes)   |
+-----------------------------+ 0x200
| Page (0x100-aligned)        |
| Page                        |
| ...                         |
+-----------------------------+
```

Pages are grouped into *blocks*. The header holds a block table; each block is a
contiguous run of pages.

## File header (offset 0x000, length 0x200)

| Offset | Size | Field | Notes |
|-------:|-----:|-------|-------|
| 0x00 | 4 | addr | must be `0x00000000` (also the "not encrypted" signal) |
| 0x04 | 2 | hdrSize | `0x0200` |
| 0x06 | 4 | fileLength1 | total file length |
| 0x0A | 4 | fileLength2 | same as fileLength1 |
| 0x0E | 4 | magic | `"tOpS"` |
| 0x12 | 2 | zeros | `0x0000` |
| 0x14 | 4 | lastIssuedRow | **big-endian** |
| 0x18 | 4 | changes | change counter |
| 0x1C | 4 | managementPageRef | may be 0 |
| 0x20 | 240 | pageStart[60] | page **references**, not offsets (see below) |
| 0x110 | 240 | pageEnd[60] | page references |

### Discovery 1 — page references, not offsets

The `pageStart` / `pageEnd` arrays do **not** hold absolute file offsets. They
hold *page references* that convert via:

```
fileOffset = (pageRef << 8) + 0x200
pageRef    = (fileOffset - 0x200) >> 8
```

Consequence: every block boundary must sit on a `0x100` boundary.

### Discovery 2 — the first two block slots are reserved

This was the single most important discovery for getting record data to display.
Real data blocks must start at **index `[2]`** of the block table. Slots `[0]`
and `[1]` are reserved; if you place data blocks there, the Clarion engine reports
`Record Count = 0` even though the table structure parses correctly.

### Discovery 3 — driver version is 1

`driverVersion` in the table-definition body is `1`, not `0x0200` as some sources
suggest.

## Page (0x100-aligned)

| Offset | Size | Field | Notes |
|-------:|-----:|-------|-------|
| +0x00 | 4 | addr | equals this page's own file offset |
| +0x04 | 2 | pageSize | physical (post-RLE) size |
| +0x06 | 2 | pageSizeUncompressed | |
| +0x08 | 2 | pageSizeUncompressedWithoutHeader | |
| +0x0A | 2 | recordCount | |
| +0x0C | 1 | flags | `0x00` for normal pages |
| +0x0D | … | data | RLE-wrapped records |

If `pageSize != pageSizeUncompressed` and `flags == 0`, the reader runs RLE
decompression. This project wraps record data as a single RLE "skip" block
(no real compression, but a valid RLE stream the engine accepts).

## Record wrapper

```
+0x00  1   flags          (0xC0 = full header, no header-reuse compression)
+0x01  2   recordLength   (length of `data`)
+0x03  2   headerLength
+0x05  …   data           (first `headerLength` bytes are the record header)
```

### Record headers by type

- **Data** (`0xF3`): `tableNumber` (BE, 4) + `0xF3` + `recordNumber` (BE, 4) → 9 bytes
- **TableName** (`0xFE`): `0xFE` + name; payload = `tableNumber` (BE, 4)
- **TableDefinition** (`0xFA`): `tableNumber` (BE, 4) + `0xFA` + blockIndex (2) → 7 bytes
- **Index**: `tableNumber` (BE, 4) + indexNumber (1) + keyData; payload = `recordNumber` (BE, 4)

### Discovery 4 — big-endian table/record numbers

`tableNumber`, `recordNumber`, and the header `lastIssuedRow` are big-endian,
unlike the rest of the format.

## Table-definition body

```
2  driverVersion      (= 1)
2  recordLength
2  nrOfFields
2  nrOfMemos
2  nrOfIndexes
   fields...
   indexes...
```

### Field definition

```
1   type
2   offset
z   name (zero-terminated)
2   elements
2   length
2   flags
2   index        ← display sequence number (0,1,2,...), NOT key participation
   type-specific extras:
     STRING/CSTRING/PSTRING: 2 stringLength, then empty stringMask (two 0x00)
     BCD: 1 digitsAfterDecimal, 1 lengthOfElement
```

### Discovery 5 — `index` is the display order

The field's `index` attribute is the display sequence number. The Scalabium
viewer orders columns by it. Setting it to anything other than the field's
position scrambles the displayed column order.

### Index definition

```
z   externalFile (zero-terminated)
1   marker 0x01   (only when externalFile is empty)
z   name
1   flags
2   fieldsInKey
2*n keyField[]       ← all field indices first
2*n keyFieldFlag[]   ← then all flags
```

### Discovery 6 — index uses separate arrays

tps-parse reads the key fields as interleaved `(field, flag)` pairs, but real
Clarion files store them as two separate arrays: all `keyField` values, then all
`keyFieldFlag` values. Writing the interleaved layout makes the Scalabium viewer
fail to recognise the data.

## RLE (TopSpeed run-length encoding)

```
[skip]          1 byte; if > 0x7F, a 2-byte form follows
[skip bytes]    `skip` raw bytes
[repeat]        1 byte; repeats the last copied byte (2-byte form if > 0x7F)
```

The 2-byte expansion is:
`value = ((msb << 7) & 0xFF00) + (firstByte & 0x7F) + 0x80 * (msb & 1)`

## ORIS Georgian encoding

ORIS stores Georgian text in a fixed single-byte codepage. Bytes `0xC0`–`0xE4`
map to the 33 Georgian letters in alphabetical order (with a few gaps). The same
bytes render as Cyrillic with a Russian font or as Latin-1 accented characters
with a Western font — the bytes on disk are identical.

| Byte | Letter | Byte | Letter | Byte | Letter |
|------|--------|------|--------|------|--------|
| C0 | ა | CA | კ | D6 | უ |
| C1 | ბ | CB | ლ | D7 | ფ |
| C2 | გ | CC | მ | D8 | ქ |
| C3 | დ | CD | ნ | D9 | ღ |
| C4 | ე | CF | ო | DA | ყ |
| C5 | ვ | D0 | პ | DB | შ |
| C6 | ზ | D1 | ჟ | DC | ჩ |
| C8 | თ | D2 | რ | DD | ც |
| C9 | ი | D3 | ს | DE | ძ |
| | | D4 | ტ | DF | წ |
| | | | | E0 | ჭ |
| | | | | E1 | ხ |
| | | | | E3 | ჯ |
| | | | | E4 | ჰ |

ASCII (`0x00`–`0x7F`) passes through unchanged.

## Multi-page and block consolidation

Each page holds a fixed number of records (this project uses 16, conservatively;
real ORIS files use ~19). To support large tables, consecutive pages of the same
kind (data / index / definitions) are merged into a single block — matching how
real ORIS files pack ~24 pages into one block. This removes the 60-slot block
limit as a practical record cap; tables of 10,000+ records have been tested.
