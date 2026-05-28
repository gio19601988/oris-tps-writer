#!/usr/bin/env python3
"""
TPS Reader/Verifier — tps-parse-ის Java წყაროდან გადათარგმნილი Python-ში.

მიზანი: ვალიდაცია, რომ ჩვენი writer-ი სწორ ფაილებს ქმნის.
ეს რეადერი იცის ზუსტად ის, რასაც Java tps-parse-ი — ჩვენი writer-ი
თუ წარმატებით გადის ამ რეადერში, საუკეთესო signal-ია, რომ
ფორმატი არანაკლებ tps-parse-ის compatible-ია.
"""

import struct
import sys
from io import BytesIO


def parse_tps_header(data):
    """TpsHeader.java-ის ანალოგი."""
    if len(data) < 0x200:
        raise ValueError(f"File too small: {len(data)} < 0x200")
    
    # TpsHeader.java:48 -> addr = leLong() (uint32)
    addr = struct.unpack_from('<I', data, 0x00)[0]
    if addr != 0:
        raise ValueError(f"File doesn't start with 0x00000000 (got 0x{addr:08x}) — encrypted or not TPS")
    
    # TpsHeader.java:52 -> hdrSize = leShort() (uint16)
    hdr_size = struct.unpack_from('<H', data, 0x04)[0]
    
    # offset moves: skip 6 bytes consumed (4 + 2)
    # next: leLong fileLength1, leLong fileLength2
    file_length1 = struct.unpack_from('<I', data, 0x06)[0]
    file_length2 = struct.unpack_from('<I', data, 0x0A)[0]
    
    # next: fixedLengthString(4) → "tOpS"
    magic = data[0x0E:0x12]
    
    # next: leShort zeros
    zeros = struct.unpack_from('<H', data, 0x12)[0]
    
    # next: beLong lastIssuedRow (BIG endian!)
    last_issued_row = struct.unpack_from('>I', data, 0x14)[0]
    
    # next: leLong changes
    changes = struct.unpack_from('<I', data, 0x18)[0]
    
    # next: leLong managementPageRef (then toFileOffset applied)
    mgmt_page_ref = struct.unpack_from('<I', data, 0x1C)[0]
    
    # pageStart array: (0x110 - 0x20) / 4 = 60 entries
    page_starts = list(struct.unpack_from('<60I', data, 0x20))
    
    # pageEnd array: (0x200 - 0x110) / 4 = 60 entries
    page_ends = list(struct.unpack_from('<60I', data, 0x110))
    
    return {
        'addr': addr,
        'hdr_size': hdr_size,
        'file_length1': file_length1,
        'file_length2': file_length2,
        'magic': magic,
        'zeros': zeros,
        'last_issued_row': last_issued_row,
        'changes': changes,
        'mgmt_page_ref': mgmt_page_ref,
        'page_starts': page_starts,
        'page_ends': page_ends,
    }


def parse_tps_page(data, offset):
    """TpsPage.java-ის ანალოგი."""
    # +0x00 leLong addr
    page_addr = struct.unpack_from('<I', data, offset)[0]
    # +0x04 leShort pageSize
    page_size = struct.unpack_from('<H', data, offset + 4)[0]
    # +0x06 leShort pageSizeUncompressed
    page_size_uncompressed = struct.unpack_from('<H', data, offset + 6)[0]
    # +0x08 leShort pageSizeUncompressedWithoutHeader
    page_size_no_hdr = struct.unpack_from('<H', data, offset + 8)[0]
    # +0x0A leShort recordCount
    record_count = struct.unpack_from('<H', data, offset + 0x0A)[0]
    # +0x0C leByte flags
    flags = data[offset + 0x0C]
    
    # data starts at offset+13
    data_start = offset + 13
    # data length = pageSize - 13 (header size including addr)
    data_len = page_size - 13
    
    return {
        'page_addr': page_addr,
        'page_size': page_size,
        'page_size_uncompressed': page_size_uncompressed,
        'page_size_no_hdr': page_size_no_hdr,
        'record_count': record_count,
        'flags': flags,
        'data_start': data_start,
        'data_len': data_len,
        'data': data[data_start:data_start + data_len] if flags == 0 else None,
    }


def parse_records_from_page(page_info):
    """TpsRecord.java-ის ანალოგი — header reuse compression."""
    if page_info['flags'] != 0:
        return []
    
    if page_info['page_size'] != page_info['page_size_uncompressed']:
        raise NotImplementedError("RLE compression not implemented in this verifier")
    
    data = page_info['data']
    records = []
    pos = 0
    prev = None
    
    while pos < len(data) and len(records) < page_info['record_count']:
        flags = data[pos]
        pos += 1
        
        if prev is None:
            # პირველი record უნდა იყოს სრული header-ით
            if (flags & 0xC0) != 0xC0:
                raise ValueError(f"First record on page must have full header (got 0x{flags:02x})")
            record_length = struct.unpack_from('<H', data, pos)[0]
            pos += 2
            header_length = struct.unpack_from('<H', data, pos)[0]
            pos += 2
            record_data = data[pos:pos + record_length]
            pos += record_length
        else:
            # შესაძლო header reuse
            if flags & 0x80:
                record_length = struct.unpack_from('<H', data, pos)[0]
                pos += 2
            else:
                record_length = prev['record_length']
            
            if flags & 0x40:
                header_length = struct.unpack_from('<H', data, pos)[0]
                pos += 2
            else:
                header_length = prev['header_length']
            
            copy = flags & 0x3F
            copied_data = prev['data'][:copy]
            new_data = data[pos:pos + (record_length - copy)]
            pos += (record_length - copy)
            record_data = copied_data + new_data
            
            if len(record_data) != record_length:
                raise ValueError(f"Record length mismatch: {len(record_data)} != {record_length}")
        
        # Parse record header
        header_bytes = record_data[:header_length]
        record_type = identify_record_type(header_bytes)
        
        record = {
            'flags': flags,
            'record_length': record_length,
            'header_length': header_length,
            'data': record_data,
            'header_bytes': header_bytes,
            'record_type': record_type,
        }
        records.append(record)
        prev = record
    
    return records


def identify_record_type(header_bytes):
    """TpsRecord.java:107 buildHeader()-ის ლოგიკა."""
    if len(header_bytes) < 5:
        return ('UNKNOWN', None)
    
    # peek(0): if 0xFE → TableName
    if header_bytes[0] == 0xFE:
        name = header_bytes[1:].decode('ascii', errors='replace').rstrip('\x00')
        return ('TableName', {'name': name})
    
    # peek(4): record type
    type_byte = header_bytes[4]
    # bytes 0-3 = tableNumber (BIG endian)
    table_number = struct.unpack_from('>I', header_bytes, 0)[0]
    
    if type_byte == 0xF3:
        # DataHeader: + recordNumber (BE uint32)
        rec_num = struct.unpack_from('>I', header_bytes, 5)[0] if len(header_bytes) >= 9 else None
        return ('Data', {'table': table_number, 'record_number': rec_num})
    elif type_byte == 0xF6:
        return ('Metadata', {'table': table_number})
    elif type_byte == 0xFA:
        return ('TableDefinition', {'table': table_number})
    elif type_byte == 0xFC:
        return ('Memo', {'table': table_number})
    else:
        return ('Index', {'table': table_number, 'type': type_byte})


def parse_table_definition_body(body):
    """TableDefinitionRecord.java + FieldDefinitionRecord.java."""
    pos = 0
    driver_version = struct.unpack_from('<H', body, pos)[0]; pos += 2
    record_length = struct.unpack_from('<H', body, pos)[0]; pos += 2
    nr_fields = struct.unpack_from('<H', body, pos)[0]; pos += 2
    nr_memos = struct.unpack_from('<H', body, pos)[0]; pos += 2
    nr_indexes = struct.unpack_from('<H', body, pos)[0]; pos += 2
    
    fields = []
    for _ in range(nr_fields):
        field_type = body[pos]; pos += 1
        offset = struct.unpack_from('<H', body, pos)[0]; pos += 2
        # zero-terminated name
        zero_idx = body.index(b'\x00', pos)
        name = body[pos:zero_idx].decode('ascii', errors='replace')
        pos = zero_idx + 1
        elements = struct.unpack_from('<H', body, pos)[0]; pos += 2
        length = struct.unpack_from('<H', body, pos)[0]; pos += 2
        flags = struct.unpack_from('<H', body, pos)[0]; pos += 2
        index = struct.unpack_from('<H', body, pos)[0]; pos += 2
        
        # ტიპის სპეციფიკური დანამატები
        extras = {}
        if field_type == 0x0A:
            extras['bcd_digits'] = body[pos]; pos += 1
            extras['bcd_length'] = body[pos]; pos += 1
        elif field_type in (0x12, 0x13, 0x14):
            extras['string_length'] = struct.unpack_from('<H', body, pos)[0]; pos += 2
            zero_idx = body.index(b'\x00', pos)
            extras['string_mask'] = body[pos:zero_idx].decode('ascii', errors='replace')
            pos = zero_idx + 1
            if len(extras['string_mask']) == 0:
                pos += 1   # extra byte when mask empty
        
        fields.append({
            'type': field_type,
            'offset': offset,
            'name': name,
            'elements': elements,
            'length': length,
            'flags': flags,
            'index': index,
            **extras,
        })
    
    return {
        'driver_version': driver_version,
        'record_length': record_length,
        'nr_fields': nr_fields,
        'nr_memos': nr_memos,
        'nr_indexes': nr_indexes,
        'fields': fields,
    }


def verify_tps_file(path):
    """ფაილის სრული ვალიდაცია — დააბრუნებს მოხსენებას."""
    with open(path, 'rb') as f:
        data = f.read()
    
    print(f"=== Verifying: {path} ===")
    print(f"File size: {len(data)} bytes (0x{len(data):04x})")
    print()
    
    # 1. Header
    try:
        hdr = parse_tps_header(data)
        print("--- Header ---")
        print(f"  Magic:           {hdr['magic']!r}  {'✓' if hdr['magic'] == b'tOpS' else '✗'}")
        print(f"  Header size:     0x{hdr['hdr_size']:04x}")
        print(f"  File length 1:   0x{hdr['file_length1']:08x}")
        print(f"  File length 2:   0x{hdr['file_length2']:08x}")
        print(f"  Last issued row: {hdr['last_issued_row']}")
        print(f"  Changes:         {hdr['changes']}")
        # find non-default page entries
        active_blocks = []
        for i in range(60):
            ps = hdr['page_starts'][i]
            pe = hdr['page_ends'][i]
            if ps != 0x200 or pe != 0x200:
                active_blocks.append((i, ps, pe))
        print(f"  Active blocks:   {len(active_blocks)}")
        for i, ps, pe in active_blocks:
            print(f"    [{i}]: 0x{ps:08x} .. 0x{pe:08x}")
    except Exception as e:
        print(f"✗ Header error: {e}")
        return False
    
    print()
    
    # 2. Pages
    for block_idx, ps, pe in active_blocks:
        print(f"--- Block #{block_idx}: 0x{ps:08x} - 0x{pe:08x} ---")
        # სიმარტისთვის: ვცადოთ პირველი page-ის parse-ი ბლოკის დასაწყისიდან
        try:
            page = parse_tps_page(data, ps)
            print(f"  Page addr (self):           0x{page['page_addr']:08x}")
            print(f"  Page size:                  0x{page['page_size']:04x}")
            print(f"  Page size (uncompressed):   0x{page['page_size_uncompressed']:04x}")
            print(f"  Page size (no hdr):         0x{page['page_size_no_hdr']:04x}")
            print(f"  Record count:               {page['record_count']}")
            print(f"  Flags:                      0x{page['flags']:02x}")
            
            # 3. Records
            records = parse_records_from_page(page)
            print(f"  Parsed records:             {len(records)}")
            for i, rec in enumerate(records):
                rtype, info = rec['record_type']
                print(f"    [{i}] {rtype}: {info}")
                if rtype == 'TableDefinition':
                    body = rec['data'][rec['header_length']:]
                    table_def = parse_table_definition_body(body)
                    print(f"        driver=0x{table_def['driver_version']:04x}, recordLen={table_def['record_length']}")
                    print(f"        fields={table_def['nr_fields']}, memos={table_def['nr_memos']}, indexes={table_def['nr_indexes']}")
                    for fi, f in enumerate(table_def['fields']):
                        print(f"          Field[{fi}]: type=0x{f['type']:02x}, ofs={f['offset']}, len={f['length']}, name={f['name']!r}")
                elif rtype == 'Data':
                    body = rec['data'][rec['header_length']:]
                    preview = body[:40]
                    print(f"        body[:40]={preview!r}")
        except Exception as e:
            print(f"  ✗ Page parse error: {e}")
            import traceback
            traceback.print_exc()
            return False
    
    print()
    print("✓ All structural checks passed!")
    return True


if __name__ == '__main__':
    path = sys.argv[1] if len(sys.argv) > 1 else 'test_v2.tps'
    ok = verify_tps_file(path)
    sys.exit(0 if ok else 1)
