#!/usr/bin/env python3
"""
TpsReader — სრული .tps ფაილის წამკითხავი.

წაიკითხავს ნებისმიერ .tps ფაილს (real ORIS ჩათვლით):
  - header, blocks, pages
  - RLE decompression
  - header-reuse compression (record-level)
  - table definition (fields + indexes)
  - ყველა data record

ეს არის INSERT-ის საფუძველი: read-modify-rewrite workflow-სთვის
ჯერ წავიკითხავთ მთელ არსებულ ფაილს, შემდეგ regenerate ახალი row-ებით.
"""

import struct
from io import BytesIO


def _u16(data, off): return struct.unpack_from('<H', data, off)[0]
def _u32(data, off): return struct.unpack_from('<I', data, off)[0]
def _u32be(data, off): return struct.unpack_from('>I', data, off)[0]


def decompress_rle(compressed):
    """TopSpeed RLE decompression (tps_rle.py-ის იდენტური)."""
    src = compressed
    out = bytearray()
    pos = 0
    n = len(src)
    while pos < n:
        skip = src[pos]; pos += 1
        if skip == 0:
            if pos == n: break
            raise ValueError(f"Bad RLE skip 0x00 at {pos-1}")
        if skip > 0x7F:
            if pos >= n: raise ValueError("Incomplete 2-byte skip")
            msb = src[pos]; pos += 1
            lsb = skip & 0x7F
            skip = ((msb << 7) & 0x00FF00) + lsb + 0x80 * (msb & 0x01)
        if pos + skip > n:
            raise ValueError(f"Skip overflow at {pos}")
        out.extend(src[pos:pos+skip]); pos += skip
        if pos < n - 1:
            to_repeat = out[-1]
            rep = src[pos]; pos += 1
            if rep > 0x7F:
                if pos >= n: raise ValueError("Incomplete 2-byte repeat")
                msb = src[pos]; pos += 1
                lsb = rep & 0x7F
                rep = ((msb << 7) & 0x00FF00) + lsb + 0x80 * (msb & 0x01)
            out.extend(bytes([to_repeat]) * rep)
    return bytes(out)


class TpsField:
    def __init__(self, ftype, offset, name, elements, length, flags, index):
        self.type = ftype
        self.offset = offset
        self.name = name
        self.elements = elements
        self.length = length
        self.flags = flags
        self.index = index

    def __repr__(self):
        return f"Field({self.name}, type=0x{self.type:02x}, ofs={self.offset}, len={self.length})"


class TpsReader:
    def __init__(self, path):
        with open(path, 'rb') as f:
            self.data = f.read()
        self.header = {}
        self.blocks = []          # list of (start, end)
        self.fields = []
        self.indexes_raw = []     # raw index def bytes (we preserve as-is)
        self.record_length = 0
        self.driver_version = 0
        self.table_name = None
        self.table_number = 1
        self.data_records = {}    # record_number → row bytes
        self.last_issued_row = 0
        self._parse()

    # ----------------------------------------------------------------
    def _parse(self):
        d = self.data
        if _u32(d, 0) != 0:
            raise ValueError("File doesn't start with 0x00000000 (encrypted or not TPS)")
        magic = d[0x0E:0x12]
        if magic != b'tOpS':
            raise ValueError(f"Bad magic: {magic!r}")

        self.last_issued_row = _u32be(d, 0x14)
        self.header['changes'] = _u32(d, 0x18)
        self.header['mgmt_page_ref'] = _u32(d, 0x1C)

        # blocks
        for i in range(60):
            ps = _u32(d, 0x20 + i*4)
            pe = _u32(d, 0x110 + i*4)
            if ps == 0 and pe == 0:
                continue
            so = (ps << 8) + 0x200
            eo = (pe << 8) + 0x200
            if so >= eo:
                continue
            self.blocks.append((so, eo))

        # walk all pages
        for (so, eo) in self.blocks:
            self._parse_block(so, eo)

    def _parse_block(self, start, end):
        d = self.data
        pos = start
        while pos < end:
            if pos + 13 > len(d):
                break
            page_addr = _u32(d, pos)
            if page_addr != pos:
                pos += 0x100
                continue
            page_size = _u16(d, pos + 4)
            page_size_uc = _u16(d, pos + 6)
            rec_count = _u16(d, pos + 0x0A)
            flags = d[pos + 0x0C]

            comp = d[pos + 13: pos + page_size]
            try:
                if page_size != page_size_uc and flags == 0:
                    dec = decompress_rle(comp)
                else:
                    dec = comp
                self._parse_page_records(dec, rec_count)
            except Exception:
                pass  # management/index pages we can't parse — skip safely

            pos = (pos + page_size + 0xFF) & ~0xFF

    def _parse_page_records(self, dec, rec_count):
        pos = 0
        prev_data = b''
        prev_rec_len = 0
        prev_hdr_len = 0
        for _ in range(rec_count):
            if pos >= len(dec):
                break
            fb = dec[pos]; pos += 1
            if fb & 0x80:
                rec_len = _u16(dec, pos); pos += 2
            else:
                rec_len = prev_rec_len
            if fb & 0x40:
                hdr_len = _u16(dec, pos); pos += 2
            else:
                hdr_len = prev_hdr_len
            copy = fb & 0x3F
            new_count = rec_len - copy
            if new_count < 0 or pos + new_count > len(dec):
                break
            new_bytes = dec[pos: pos + new_count]
            pos += new_count
            rec_data = prev_data[:copy] + new_bytes

            self._classify_record(rec_data, hdr_len)

            prev_data = rec_data
            prev_rec_len = rec_len
            prev_hdr_len = hdr_len

    def _classify_record(self, rec_data, hdr_len):
        if len(rec_data) < 5:
            return
        # TableName: first byte 0xFE
        if rec_data[0] == 0xFE:
            name = rec_data[1:hdr_len].decode('ascii', errors='replace')
            self.table_name = name
            return
        type_byte = rec_data[4]
        table_num = _u32be(rec_data, 0)
        if type_byte == 0xFA:  # TableDefinition
            self.table_number = table_num
            self._parse_table_def(rec_data[hdr_len:])
        elif type_byte == 0xF3:  # Data
            if hdr_len >= 9:
                rec_num = _u32be(rec_data, 5)
                row = rec_data[hdr_len:]
                self.data_records[rec_num] = row
        # index/memo records: ignored (we regenerate indexes)

    def _parse_table_def(self, body):
        if len(body) < 10:
            return
        self.driver_version = _u16(body, 0)
        self.record_length = _u16(body, 2)
        n_fields = _u16(body, 4)
        n_memos = _u16(body, 6)
        n_indexes = _u16(body, 8)
        pos = 10
        for _ in range(n_fields):
            ftype = body[pos]; pos += 1
            offset = _u16(body, pos); pos += 2
            zi = body.index(b'\x00', pos)
            name = body[pos:zi].decode('ascii', errors='replace')
            pos = zi + 1
            elements = _u16(body, pos); pos += 2
            length = _u16(body, pos); pos += 2
            flags = _u16(body, pos); pos += 2
            index = _u16(body, pos); pos += 2
            if ftype == 0x0A:  # BCD
                pos += 2
            elif ftype in (0x12, 0x13, 0x14):  # string types
                pos += 2  # stringLength
                # stringMask: zero-terminated; when empty it's a single 0x00
                # followed by one extra 0x00 (observed in real ARN.TPS as "00 00").
                zi2 = body.index(b'\x00', pos)
                if zi2 == pos:
                    # empty mask: 0x00 + extra 0x00
                    pos += 2
                else:
                    pos = zi2 + 1
            self.fields.append(TpsField(ftype, offset, name, elements, length, flags, index))
        # indexes: we don't need to parse deeply; regeneration rebuilds them

    # ----------------------------------------------------------------
    def summary(self):
        lines = []
        lines.append(f"Table name:     {self.table_name}")
        lines.append(f"Table number:   {self.table_number}")
        lines.append(f"Driver version: {self.driver_version}")
        lines.append(f"Record length:  {self.record_length}")
        lines.append(f"Last issued:    {self.last_issued_row}")
        lines.append(f"Fields:         {len(self.fields)}")
        for f in self.fields:
            lines.append(f"   {f}")
        lines.append(f"Data records:   {len(self.data_records)}")
        if self.data_records:
            nums = sorted(self.data_records.keys())
            lines.append(f"   Record # range: {nums[0]}..{nums[-1]}")
        return "\n".join(lines)


if __name__ == '__main__':
    import sys
    path = sys.argv[1] if len(sys.argv) > 1 else 'ARN.TPS'
    r = TpsReader(path)
    print(r.summary())
