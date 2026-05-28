#!/usr/bin/env python3
"""
TPS Writer v2 — sourced directly from tps-parse Java source code.

ფორმატის რეფერენსი:
- TpsHeader.java:    file header (0x200 bytes)
- TpsBlock.java:     block container
- TpsPage.java:      page structure (13-byte header + records)
- TpsRecord.java:    record wrapper with header-reuse compression
- AbstractHeader.java: tableNumber (BIG-endian!) + type byte
- TableNameHeader.java: type 0xFE, NO tableNumber, just name
- DataHeader.java:   tableNumber + 0xF3 + recordNumber (BE uint32)
- TableDefinitionHeader.java: tableNumber + 0xFA + ...
- TableDefinitionRecord.java: driverVer + recLen + nFields + nMemos + nIdx + fields...
- FieldDefinitionRecord.java: type + offset + name(z) + elements + len + flags + idx + (extras)

შემოწმდა Java source-ის წინააღმდეგ. დაუშიფრავი .tps წერს მხოლოდ.
"""

import struct
from io import BytesIO


# ===================================================================
# კონსტანტები
# ===================================================================

TPS_MAGIC = b'tOpS'
HEADER_SIZE = 0x200
PAGE_ALIGN = 0x100

# Record-ის ტიპები (data[4]-ში — table number BE-ის შემდეგ)
TYPE_DATA = 0xF3
TYPE_METADATA = 0xF6
TYPE_TABLE_DEF = 0xFA
TYPE_MEMO = 0xFC
TYPE_TABLE_NAME = 0xFE   # სპეციალური — data[0]-ში

# ფილდის ტიპები (FieldDefinitionRecord.java-ის switch-ის მიხედვით)
F_BYTE = 0x01
F_SHORT = 0x02
F_USHORT = 0x03
F_DATE = 0x04
F_TIME = 0x05
F_LONG = 0x06
F_ULONG = 0x07
F_FLOAT = 0x08
F_DOUBLE = 0x09
F_BCD = 0x0A
F_STRING = 0x12       # fixed-length
F_CSTRING = 0x13      # zero-terminated
F_PSTRING = 0x14      # pascal
F_GROUP = 0x16


# ===================================================================
# დაბალი დონის helpers
# ===================================================================

def u8(v): return struct.pack('<B', v & 0xFF)
def u16(v): return struct.pack('<H', v & 0xFFFF)
def u32(v): return struct.pack('<I', v & 0xFFFFFFFF)
def u32be(v): return struct.pack('>I', v & 0xFFFFFFFF)   # BIG-endian


# ===================================================================
# Record body builders
# ===================================================================

class Field:
    """ცხრილის ერთი ფილდის აღწერა."""
    def __init__(self, name, field_type, offset, length, elements=1, flags=0, index=0):
        self.name = name           # "TBL:FIELDNAME" ფორმატით
        self.field_type = field_type
        self.offset = offset
        self.length = length
        self.elements = elements
        self.flags = flags         # field flags (0 default, 1 = indexed?)
        self.index = index         # index field number
    
    def serialize(self):
        """FieldDefinitionRecord ფორმატი."""
        buf = BytesIO()
        buf.write(u8(self.field_type))
        buf.write(u16(self.offset))
        # zero-terminated name
        buf.write(self.name.encode('ascii') + b'\x00')
        buf.write(u16(self.elements))
        buf.write(u16(self.length))
        buf.write(u16(self.flags))    # flags
        buf.write(u16(self.index))    # index
        
        # ტიპის სპეციფიკური დანამატები
        if self.field_type == F_BCD:
            buf.write(u8(0))  # bcdDigitsAfterDecimalPoint
            buf.write(u8(0))  # bcdLengthOfElement
        elif self.field_type in (F_STRING, F_CSTRING, F_PSTRING):
            buf.write(u16(self.length))   # stringLength
            buf.write(b'\x00')             # empty stringMask
            buf.write(b'\x00')             # extra byte when mask empty
        
        return buf.getvalue()


class Index:
    """ცხრილის ერთი ინდექსის აღწერა."""
    def __init__(self, name, fields_in_key, key_fields=None, flags=6, external_file=""):
        self.name = name                # "TBL:KEYNAME"
        self.external_file = external_file
        self.flags = flags              # ჩვეულებრივ 6
        self.fields_in_key = fields_in_key
        # key_fields = list of (field_index, field_flag) tuples
        if key_fields is None:
            key_fields = [(i, 0) for i in range(fields_in_key)]
        self.key_fields = key_fields
    
    def serialize(self):
        """IndexDefinitionRecord ფორმატი.
        
        რეალური Clarion format (verified against ARN.TPS):
            externalFile (zero-terminated string)
            if externalFile == "": marker byte 0x01
            name (zero-terminated string)
            flags (uint8)
            fieldsInKey (uint16)
            keyField[fieldsInKey] (uint16 each)    ← SEPARATE arrays!
            keyFieldFlag[fieldsInKey] (uint16 each)
        
        შენიშვნა: tps-parse Java კოდი ცდილობს interleaved-ად კითხვას
        (field, flag, field, flag), მაგრამ რეალური Clarion ფაილებში
        ეს არის ცალკე arrays. ჩვენ ვწერთ რეალური ფორმატით.
        """
        buf = BytesIO()
        # externalFile (zero-terminated)
        buf.write(self.external_file.encode('ascii') + b'\x00')
        # If externalFile empty, marker byte 0x01
        if len(self.external_file) == 0:
            buf.write(u8(0x01))
        # name (zero-terminated)
        buf.write(self.name.encode('ascii') + b'\x00')
        # flags (uint8)
        buf.write(u8(self.flags))
        # fieldsInKey (uint16)
        buf.write(u16(self.fields_in_key))
        # SEPARATE arrays: all keyField, then all keyFieldFlag
        for field_idx, _ in self.key_fields:
            buf.write(u16(field_idx))
        for _, field_flag in self.key_fields:
            buf.write(u16(field_flag))
        return buf.getvalue()


def build_table_definition_body(fields, record_length, driver_version=1, indexes=None):
    """TableDefinitionRecord-ის body (header-ის გარეშე).
    
    Driver version: რეალური ORIS ფაილებში არის 1 (არა 0x200 როგორც გვეგონა).
    """
    if indexes is None:
        indexes = []
    
    buf = BytesIO()
    buf.write(u16(driver_version))     # driverVersion (რეალურად = 1)
    buf.write(u16(record_length))      # recordLength
    buf.write(u16(len(fields)))        # nrOfFields
    buf.write(u16(0))                  # nrOfMemos
    buf.write(u16(len(indexes)))       # nrOfIndexes
    for f in fields:
        buf.write(f.serialize())
    # memos go here (we have none)
    # indexes
    for idx in indexes:
        buf.write(idx.serialize())
    return buf.getvalue()


def build_record_header(record_type, table_number=0, record_number=0,
                        table_name_str=""):
    """
    AbstractHeader-ი:
        tableNumber (BE uint32) + type (uint8) + type-specific...
    
    გამონაკლისი: TableName — არ აქვს tableNumber-ი. მთლიანი header == 0xFE + name.
    Payload ცარიელია.
    """
    buf = BytesIO()
    
    if record_type == TYPE_TABLE_NAME:
        # სპეციალური: მთლიანი header == 0xFE + name
        buf.write(u8(TYPE_TABLE_NAME))
        buf.write(table_name_str.encode('ascii'))
    else:
        buf.write(u32be(table_number))     # tableNumber (BIG endian!)
        buf.write(u8(record_type))
        
        if record_type == TYPE_DATA:
            buf.write(u32be(record_number))   # recordNumber (BE uint32)
        elif record_type == TYPE_TABLE_DEF:
            # TableDefinitionHeader-ს აქვს block ნომერი (uint16)
            buf.write(u16(0))   # block index (0 = first/only chunk)
    
    return buf.getvalue()


def build_record(record_type, payload, table_number=0, record_number=0,
                 is_first_on_page=True, table_name_str=""):
    """
    TpsRecord-ის სრული binary სტრუქტურა.
    
    Layout (TpsRecord.java-ის მიხედვით):
        +0  uint8  flags (0xC0 if first on page = full header)
        +1  uint16 recordLength  (data-ის სრული სიგრძე)
        +3  uint16 headerLength
        +5  ...    data of size recordLength, first headerLength bytes = header
    """
    header_bytes = build_record_header(record_type, table_number, record_number,
                                        table_name_str=table_name_str)
    data = header_bytes + payload
    
    wrapper = BytesIO()
    if is_first_on_page:
        # full header marker
        wrapper.write(u8(0xC0))
    else:
        # for now we always write full headers (no compression of records)
        wrapper.write(u8(0xC0))
    
    wrapper.write(u16(len(data)))         # recordLength
    wrapper.write(u16(len(header_bytes))) # headerLength
    wrapper.write(data)
    return wrapper.getvalue()


# ===================================================================
# Page builder
# ===================================================================

def _rle_wrap(raw: bytes) -> bytes:
    """
    RLE-encode raw data as a single 'skip' block (no actual compression).
    This produces minimum-overhead RLE-format data that the Clarion engine 
    expects, while round-tripping perfectly with tps-parse's deRle().
    """
    n = len(raw)
    out = bytearray()
    
    if n <= 0x7F:
        # 1-byte skip
        out.append(n)
        out.extend(raw)
    else:
        # 2-byte skip — need to find msb such that:
        #   ((msb << 7) & 0xFF00) + (n & 0x7F) + 0x80 * (msb & 1) == n
        lsb = n & 0x7F
        target = n - lsb
        found = False
        for msb in range(256):
            computed = ((msb << 7) & 0x00FF00) + 0x80 * (msb & 0x01)
            if computed == target:
                out.append(0x80 | lsb)
                out.append(msb)
                out.extend(raw)
                found = True
                break
        if not found:
            raise ValueError(f"Cannot RLE-encode data of size {n}")
    
    return bytes(out)


def build_page(records, page_addr, use_rle=True):
    """
    TpsPage-ის bytes-ი.
    
    Layout (TpsPage.java-ის მიხედვით):
        +0   uint32 addr (= page_addr offset!)
        +4   uint16 pageSize (compressed = total page size)
        +6   uint16 pageSizeUncompressed
        +8   uint16 pageSizeUncompressedWithoutHeader
        +10  uint16 recordCount
        +12  uint8  flags
        +13  ...    records data
    
    გვერდის სრული ფიზიკური ზომა: 13 + len(data_bytes)
    
    RLE მოდი (use_rle=True): ჩვენ ვწერთ RLE-encoded data-ს ერთი მთლიანი
    'skip' block-ად. pageSize განსხვავდება pageSizeUncompressed-ისგან,
    რის გამოც Java tps-parse-ი deRle-ს გაუშვებს. ეს არის Clarion-ის
    რეალური engine-ისთვის რეკომენდირებული ფორმატი.
    """
    payload = b''.join(records)
    uncompressed_size = len(payload)   # data-ის სიგრძე header-ის გარეშე
    
    if use_rle:
        rle_payload = _rle_wrap(payload)
        page_size = 13 + len(rle_payload)
        page_size_uncompressed = 13 + uncompressed_size
        data_to_write = rle_payload
    else:
        page_size = 13 + uncompressed_size
        page_size_uncompressed = page_size
        data_to_write = payload
    
    buf = BytesIO()
    buf.write(u32(page_addr))                        # addr
    buf.write(u16(page_size))                        # pageSize (physical)
    buf.write(u16(page_size_uncompressed))           # pageSizeUncompressed
    buf.write(u16(uncompressed_size))                # pageSizeUncompressedWithoutHeader
    buf.write(u16(len(records)))                     # recordCount
    buf.write(u8(0x00))                              # flags
    buf.write(data_to_write)
    
    return buf.getvalue()


# ===================================================================
# File header builder
# ===================================================================

def build_file_header(pages_info, file_length, last_issued_row=0, changes=1,
                      block_start_index=2):
    """
    TpsHeader (0x200 bytes), TpsHeader.java-ის ფორმატით.
    
    *** კრიტიკული DISCOVERY-ები ***
    
    1. pageStart/pageEnd ცხრილში არ ვწერთ absolute file offset-ებს, არამედ
       "page references" რომლებიც გადათარგმნება ფორმულით:
           fileOffset = (pageRef << 8) + 0x200
       ანუ:
           pageRef = (fileOffset - 0x200) >> 8
       ეს ნიშნავს, რომ block-ის end უნდა იყოს 0x100 (256) boundary-ზე.
    
    2. *** გამარჯვებული DISCOVERY (v11) ***
       blocks ცხრილში პირველი ორი slot ([0], [1]) Clarion-ისთვის
       რეზერვირებულია! რეალური მონაცემთა blocks უნდა დაიწყოს index [2]-დან.
       თუ blocks [0]-დან ვწერთ, Clarion Viewer-ი Record Count = 0-ს აჩვენებს
       (data records-ს ვერ პოულობს). ამიტომ block_start_index=2 default-ია.
    
    Layout:
        +0x00  uint32 addr            (= 0x00000000)
        +0x04  uint16 hdrSize         (= 0x0200)
        +0x06  uint32 fileLength1
        +0x0A  uint32 fileLength2
        +0x0E  4 bytes "tOpS"
        +0x12  uint16 zeros
        +0x14  uint32 lastIssuedRow   (BIG endian!)
        +0x18  uint32 changes
        +0x1C  uint32 managementPageRef
        +0x20  uint32[60] pageStart   (page references, NOT offsets!)
        +0x110 uint32[60] pageEnd     (page references, NOT offsets!)
    
    pages_info:        list of (start_offset, end_offset) tuples — absolute file offsets
    block_start_index: ცხრილში რომელი slot-იდან დაიწყოს blocks (default 2)
    """
    def offset_to_ref(off):
        if off < 0x200:
            return 0
        ref = (off - 0x200) >> 8
        if ((ref << 8) + 0x200) != off:
            raise ValueError(f"Offset 0x{off:x} is not on 0x100 boundary — cannot encode as page ref")
        return ref
    
    buf = BytesIO()
    
    buf.write(u32(0))                    # +0x00 — must be zero
    buf.write(u16(HEADER_SIZE))          # +0x04 — hdrSize uint16!
    buf.write(u32(file_length))          # +0x06 — fileLength1
    buf.write(u32(file_length))          # +0x0A — fileLength2
    buf.write(TPS_MAGIC)                 # +0x0E — "tOpS"
    buf.write(u16(0))                    # +0x12 — zeros
    buf.write(u32be(last_issued_row))    # +0x14 — lastIssuedRow BIG endian!
    buf.write(u32(changes))              # +0x18 — changes
    buf.write(u32(0))                    # +0x1C — managementPageRef
    
    # pageStart[60] — leading reserved slots empty, blocks at block_start_index
    refs_start = [0] * block_start_index + [offset_to_ref(info[0]) for info in pages_info]
    while len(refs_start) < 60:
        refs_start.append(0)
    for ref in refs_start[:60]:
        buf.write(u32(ref))
    
    # pageEnd[60] — same offset
    refs_end = [0] * block_start_index + [offset_to_ref(info[1]) for info in pages_info]
    while len(refs_end) < 60:
        refs_end.append(0)
    for ref in refs_end[:60]:
        buf.write(u32(ref))
    
    result = buf.getvalue()
    assert len(result) == HEADER_SIZE, f"Header size mismatch: {len(result)} != {HEADER_SIZE}"
    return result


# ===================================================================
# მთლიანი ფაილის შემოწყობა
# ===================================================================

def build_minimal_tps(table_name="TEST", fields=None, data_rows=None, indexes=None):
    """
    შექმნის მინიმალურ .tps ფაილს.
    
    table_name: ცხრილის სახელი (ASCII)
    fields:     list of Field instances
    data_rows:  list of (record_number, raw_bytes) — წინასწარ pack-ული row-ები
    indexes:    list of Index instances (optional; default = one index on first field)
    """
    if fields is None:
        fields = [
            Field("TEST:ID", F_ULONG, offset=0, length=4),
            Field("TEST:NAME", F_STRING, offset=4, length=30),
        ]
    
    if data_rows is None:
        data_rows = []
    
    if indexes is None:
        # Default: ერთი index რომელიც პირველ ფილდს ფარავს
        # (Clarion ცარიელი Key-ის გარეშე Record Count-ს ცარიელად აჩვენებს)
        indexes = [Index("TBL:K1", fields_in_key=1, key_fields=[(0, 0)])]
    
    table_number = 1
    record_length = sum(f.length for f in fields)  # მარტივი sum, no padding
    
    # === Records ===
    
    # TableName record:
    #   Header: 0xFE + name string
    #   Payload: tableNumber as BE uint32
    table_name_rec = build_record(
        TYPE_TABLE_NAME,
        payload=u32be(table_number),     # ← BE-encoded table number
        table_name_str=table_name,
        is_first_on_page=True,
    )
    
    # TableDefinition record (driverVersion=1, with indexes)
    table_def_payload = build_table_definition_body(
        fields, record_length, driver_version=1, indexes=indexes
    )
    table_def_rec = build_record(
        TYPE_TABLE_DEF,
        payload=table_def_payload,
        table_number=table_number,
        is_first_on_page=False,
    )
    
    # Data records
    data_recs = []
    for rec_num, raw in data_rows:
        rec = build_record(
            TYPE_DATA,
            payload=raw,
            table_number=table_number,
            record_number=rec_num,
            is_first_on_page=(len(data_recs) == 0),
        )
        data_recs.append(rec)
    
    # ARN.TPS-ის სტრუქტურის ანალიზიდან:
    # - Block 0 (lower): data records 
    # - Block 1 (higher): TableName + TableDefinition
    # 
    # Scalabium Clarion Viewer-ი ეძებს definitions higher block-ში
    # (v2-მა Field Count=2 აჩვენა ამ წყობით; v3+ definitions-first წყობით 
    #  ცარიელად აჩვენებდა)
    
    # Page #1: data records (lower address)
    page_data_addr = HEADER_SIZE
    if data_recs:
        page_data = build_page(data_recs, page_data_addr)
        page_data_end_raw = page_data_addr + len(page_data)
        if page_data_end_raw % 0x100 != 0:
            page_data_end = ((page_data_end_raw // 0x100) + 1) * 0x100
            page_data = page_data + b'\x00' * (page_data_end - page_data_end_raw)
        else:
            page_data_end = page_data_end_raw
    else:
        page_data = b''
        page_data_end = page_data_addr
    
    # Page #2: definitions (higher address)
    page_def_addr = page_data_end
    page_def = build_page([table_name_rec, table_def_rec], page_def_addr)
    page_def_end_raw = page_def_addr + len(page_def)
    if page_def_end_raw % 0x100 != 0:
        page_def_end = ((page_def_end_raw // 0x100) + 1) * 0x100
        page_def = page_def + b'\x00' * (page_def_end - page_def_end_raw)
    else:
        page_def_end = page_def_end_raw
    
    # === Blocks ===
    
    if data_recs:
        blocks = [
            (page_data_addr, page_data_end),   # Block 0: data (LOWER address)
            (page_def_addr, page_def_end),     # Block 1: definitions (HIGHER address)
        ]
    else:
        blocks = [
            (page_def_addr, page_def_end),
        ]
    
    file_length = page_def_end
    last_issued_row = len(data_rows)
    
    # === Header ===
    
    header_bytes = build_file_header(
        pages_info=blocks,
        file_length=file_length,
        last_issued_row=last_issued_row,
        changes=len(data_rows) + 1,
    )
    
    # Assemble file: header + page_data + page_def (data first, definitions last)
    if data_recs:
        return header_bytes + page_data + page_def
    return header_bytes + page_def


def pack_test_row(record_id, name_text):
    """ჩვენი ცხრილის row-ის raw bytes (ID + NAME)."""
    buf = BytesIO()
    buf.write(u32(record_id))
    name_bytes = name_text.encode('ascii', errors='replace')
    name_padded = name_bytes.ljust(30, b' ')[:30]
    buf.write(name_padded)
    return buf.getvalue()


def hex_dump(data, offset=0, length=None):
    if length is None:
        length = len(data) - offset
    lines = []
    for i in range(0, length, 16):
        addr = offset + i
        chunk = data[offset + i: offset + i + 16]
        if not chunk:
            break
        hex_part = ' '.join(f'{b:02x}' for b in chunk)
        ascii_part = ''.join(chr(b) if 32 <= b < 127 else '.' for b in chunk)
        lines.append(f'{addr:04x}: {hex_part:<48} {ascii_part}')
    return '\n'.join(lines)


# ===================================================================
# Main
# ===================================================================

if __name__ == '__main__':
    data_rows = [
        (1, pack_test_row(1, "First row")),
        (2, pack_test_row(2, "Second row")),
        (3, pack_test_row(3, "Third row")),
    ]
    
    tps = build_minimal_tps(data_rows=data_rows)
    
    with open('test_v2.tps', 'wb') as f:
        f.write(tps)
    
    print(f"✓ ფაილი: test_v2.tps")
    print(f"✓ ზომა: {len(tps)} bytes (0x{len(tps):04x})")
    print()
    print("=== Header (0x000 - 0x040) ===")
    print(hex_dump(tps, 0, 64))
    print()
    print("=== Page #1 (0x200+) ===")
    print(hex_dump(tps, 0x200, min(256, len(tps) - 0x200)))
