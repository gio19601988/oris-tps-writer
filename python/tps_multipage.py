#!/usr/bin/env python3
"""
Multi-page TPS builder — დიდი datasets-ისთვის.

ARN.TPS-ის ანალიზიდან:
- ყოველ data page-ში ~19 record (uncompressed ~0x571 bytes)
- pages 0x100-aligned, sequential
- რამდენიმე data page → ერთი block (ან რამდენიმე block)
- index page(s) data pages-ს შორის ან ბოლოს
- definitions ცალკე page-ში, უფრო მაღალ address-ზე

ჩვენი მიდგომა:
- RECORDS_PER_PAGE record თითო page-ში (conservative 16)
- ყველა data page ერთ block-ში (consecutive)
- definitions ცალკე block-ში, ბოლოს
- blocks იწყება index [2]-დან (გამარჯვებული ფორმულა)
"""

import sys
import struct
from io import BytesIO

sys.path.insert(0, '.')
from tps_writer_v2 import (
    Field, Index, build_record, build_page, build_table_definition_body,
    u32, u32be, u16, u8,
    TYPE_TABLE_NAME, TYPE_TABLE_DEF, TYPE_DATA,
    HEADER_SIZE, TPS_MAGIC,
)

# ერთ page-ში რამდენი data record ჩაიდოს (conservative, real ARN uses ~19)
RECORDS_PER_PAGE = 16


def _align_up(value, boundary=0x100):
    """value-ის დამრგვალება boundary-ის ზევით."""
    if value % boundary != 0:
        return ((value // boundary) + 1) * boundary
    return value


def build_tps_multipage(table_name, fields, record_length, data_rows,
                        indexes=None, records_per_page=RECORDS_PER_PAGE,
                        table_number=1, include_index_page=True):
    """
    მრავალ-page TPS ფაილის builder.
    
    table_name:      ცხრილის სახელი
    fields:          list of Field instances (with sequential index attribute)
    record_length:   ერთი row-ის ფიზიკური ზომა
    data_rows:       list of (record_number, raw_bytes)
    indexes:         list of Index instances
    records_per_page: data records per page
    include_index_page: შევქმნათ თუ არა index/key page
    
    Returns: ფაილის bytes
    """
    if indexes is None:
        indexes = []
    
    # === 1. Build all data records ===
    all_data_recs = []
    for rec_num, raw in data_rows:
        all_data_recs.append((rec_num, raw))
    
    # === 2. Split into pages ===
    data_pages = []   # list of list-of-built-records
    for i in range(0, len(all_data_recs), records_per_page):
        chunk = all_data_recs[i:i + records_per_page]
        built = []
        for j, (rec_num, raw) in enumerate(chunk):
            rec = build_record(
                TYPE_DATA, payload=raw, table_number=table_number,
                record_number=rec_num, is_first_on_page=(j == 0),
            )
            built.append(rec)
        data_pages.append(built)
    
    # === 3. Build definition records ===
    table_name_rec = build_record(
        TYPE_TABLE_NAME, payload=u32be(table_number),
        table_name_str=table_name, is_first_on_page=True,
    )
    table_def_payload = build_table_definition_body(
        fields, record_length, driver_version=1, indexes=indexes
    )
    table_def_rec = build_record(
        TYPE_TABLE_DEF, payload=table_def_payload,
        table_number=table_number, is_first_on_page=False,
    )
    
    # === 4. Build index records (key page) ===
    index_page_recs = []
    if include_index_page and indexes and data_rows:
        # Use the first index. Key data = full row bytes (matches real ARN).
        # Sort by record bytes for B-tree-ish ordering (Clarion expects sorted).
        sorted_rows = sorted(data_rows, key=lambda r: r[1])
        for j, (rec_num, raw) in enumerate(sorted_rows):
            # Index record: header = table(BE) + indexNum(1) + keyData(record_length)
            #               payload = recordNumber (BE)
            key_data = raw[:record_length]
            header = u32be(table_number) + u8(0x00) + key_data
            payload = u32be(rec_num)
            full_data = header + payload
            wrapper = BytesIO()
            wrapper.write(u8(0xC0))
            wrapper.write(u16(len(full_data)))
            wrapper.write(u16(len(header)))
            wrapper.write(full_data)
            index_page_recs.append(wrapper.getvalue())
    
    # === 5. Layout pages at 0x100-aligned offsets ===
    layout = []   # list of (page_bytes, start_offset, end_offset, kind)
    cursor = HEADER_SIZE
    
    # 5a. Data pages
    for page_records in data_pages:
        page_bytes = build_page(page_records, cursor)
        end_raw = cursor + len(page_bytes)
        end = _align_up(end_raw)
        page_bytes = page_bytes + b'\x00' * (end - end_raw)
        layout.append((page_bytes, cursor, end, 'data'))
        cursor = end
    
    # 5b. Index page(s) — split into multiple pages like data
    if index_page_recs:
        for k in range(0, len(index_page_recs), records_per_page):
            chunk = index_page_recs[k:k + records_per_page]
            # Rebuild chunk records with correct is_first_on_page flag
            # (index records were built with 0xC0 already; first-on-page is fine
            #  since they all use full headers)
            page_bytes = build_page(chunk, cursor)
            end_raw = cursor + len(page_bytes)
            end = _align_up(end_raw)
            page_bytes = page_bytes + b'\x00' * (end - end_raw)
            layout.append((page_bytes, cursor, end, 'index'))
            cursor = end
    
    # 5c. Definitions page
    page_bytes = build_page([table_name_rec, table_def_rec], cursor)
    end_raw = cursor + len(page_bytes)
    end = _align_up(end_raw)
    page_bytes = page_bytes + b'\x00' * (end - end_raw)
    layout.append((page_bytes, cursor, end, 'defs'))
    cursor = end
    
    file_length = cursor
    
    # === 6. Build blocks ===
    # *** Block consolidation (A) ***
    # რეალური ARN.TPS-ში ერთ block-ში ბევრი page ერთიანდება (Block 2 = 24 page).
    # block არის უბრალოდ continuous range of pages.
    # 
    # ჩვენ ვაჯგუფებთ consecutive pages ერთ block-ად kind-ის მიხედვით:
    #   - ყველა data page → ერთი block (consecutive)
    #   - index page → ცალკე block
    #   - definitions page → ცალკე block
    # ეს ხსნის 60-block ლიმიტს — ერთ block-ში ათასობით page ეტევა.
    
    blocks = []
    cb_start = None
    cb_end = None
    cb_kind = None
    
    for (_, start, end, kind) in layout:
        if cb_kind is None:
            cb_start, cb_end, cb_kind = start, end, kind
        elif kind == cb_kind and start == cb_end:
            cb_end = end   # extend current block
        else:
            blocks.append((cb_start, cb_end))
            cb_start, cb_end, cb_kind = start, end, kind
    
    if cb_start is not None:
        blocks.append((cb_start, cb_end))
    
    # === 7. Build header ===
    def offset_to_ref(off):
        if off < 0x200:
            return 0
        ref = (off - 0x200) >> 8
        if ((ref << 8) + 0x200) != off:
            raise ValueError(f"Offset 0x{off:x} not on 0x100 boundary")
        return ref
    
    BLOCK_START_INDEX = 2   # გამარჯვებული ფორმულა
    
    buf = BytesIO()
    buf.write(u32(0))                          # +0x00 addr
    buf.write(u16(HEADER_SIZE))                # +0x04 hdrSize
    buf.write(u32(file_length))                # +0x06 fileLength1
    buf.write(u32(file_length))                # +0x0A fileLength2
    buf.write(TPS_MAGIC)                       # +0x0E "tOpS"
    buf.write(u16(0))                          # +0x12 zeros
    buf.write(u32be(len(data_rows)))           # +0x14 lastIssuedRow (BE)
    buf.write(u32(len(data_rows) + 1))         # +0x18 changes
    buf.write(u32(0))                          # +0x1C managementPageRef
    
    starts = [0] * BLOCK_START_INDEX + [offset_to_ref(b[0]) for b in blocks]
    ends = [0] * BLOCK_START_INDEX + [offset_to_ref(b[1]) for b in blocks]
    while len(starts) < 60: starts.append(0)
    while len(ends) < 60: ends.append(0)
    for r in starts[:60]: buf.write(u32(r))
    for r in ends[:60]: buf.write(u32(r))
    
    header_bytes = buf.getvalue()
    assert len(header_bytes) == HEADER_SIZE
    
    # === 8. Assemble ===
    result = header_bytes
    for page_bytes, _, _, _ in layout:
        result += page_bytes
    
    return result


if __name__ == '__main__':
    # Test: large dataset (50 records → multiple pages)
    sys.path.insert(0, '.')
    from oris_encoding import to_oris_field
    
    fields = [
        Field("EMP:NAME", 0x12, offset=0, length=50, index=0),
        Field("EMP:DEPT", 0x12, offset=50, length=20, index=1),
    ]
    indexes = [Index("EMP:K1", fields_in_key=1, key_fields=[(0, 0)], flags=6)]
    record_length = 70
    
    # Generate 50 fake Georgian names
    first_names = ["გიორგი", "ნინო", "დავით", "მარიამ", "ლევან", "თამარ", "ზურაბ", "ანა"]
    last_names = ["ბერიძე", "მაისურაძე", "კაპანაძე", "გელაშვილი", "წიკლაური", "ჯავახიშვილი"]
    
    data_rows = []
    for i in range(50):
        fn = first_names[i % len(first_names)]
        ln = last_names[i % len(last_names)]
        name = f"{ln} {fn}"
        dept = f"განყ.{(i % 5) + 1}"
        raw = to_oris_field(name, 50) + to_oris_field(dept, 20)
        data_rows.append((i + 1, raw))
    
    tps = build_tps_multipage(
        "EMPLOYEES", fields, record_length, data_rows, indexes=indexes,
    )
    
    with open('test_multipage.tps', 'wb') as f:
        f.write(tps)
    
    n_data_pages = (len(data_rows) + RECORDS_PER_PAGE - 1) // RECORDS_PER_PAGE
    print(f"✓ test_multipage.tps: {len(tps)} bytes")
    print(f"  Records: {len(data_rows)}")
    print(f"  Records per page: {RECORDS_PER_PAGE}")
    print(f"  Data pages: {n_data_pages}")
    print(f"  + 1 index page + 1 definitions page")
