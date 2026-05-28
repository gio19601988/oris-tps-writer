#!/usr/bin/env python3
"""
ORIS TPS Writer — მაღალი დონის API
====================================

გამარჯვებული ფორმულა (დადასტურებული Clarion Viewer-ით):
- Driver version = 1
- blocks იწყება index [2]-დან (პირველი ორი slot რეზერვირებული)
- data block ცალკე, definitions block ცალკე
- ქართული ტექსტი ORIS single-byte encoding-ით
- RLE-wrapped pages

გამოყენება:
    from oris_tps import OrisTable, StringField, LongField
    
    table = OrisTable("UNNAMED")
    table.add_field(StringField("ARN:KADR", length=50))
    table.add_field(StringField("ARN:SECT", length=20))
    table.add_key("ARN:K1", ["ARN:SECT", "ARN:KADR"])
    
    table.add_row({"ARN:KADR": "ოქრიაშვილი გ.", "ARN:SECT": ""})
    table.add_row({"ARN:KADR": "გოგიჩაძე ა.", "ARN:SECT": ""})
    
    table.save("output.tps")
"""

import sys
import struct
from io import BytesIO

sys.path.insert(0, '.')
from tps_writer_v2 import (
    Field, Index, build_minimal_tps,
    F_BYTE, F_SHORT, F_USHORT, F_DATE, F_TIME, F_LONG, F_ULONG,
    F_FLOAT, F_DOUBLE, F_BCD, F_STRING, F_CSTRING, F_PSTRING,
    u32,
)
from oris_encoding import to_oris_field, encode_oris


# ===================================================================
# Field type wrappers (მაღალი დონის)
# ===================================================================

class StringField:
    """ფიქსირებული სიგრძის ქართული/ASCII string ფილდი (Clarion STRING)."""
    def __init__(self, name, length):
        self.name = name
        self.length = length
        self.field_type = F_STRING
    
    def pack(self, value):
        """value → fixed-length ORIS-encoded bytes."""
        return to_oris_field(str(value or ""), self.length)


class LongField:
    """32-bit signed integer (Clarion LONG)."""
    def __init__(self, name):
        self.name = name
        self.length = 4
        self.field_type = F_LONG
    
    def pack(self, value):
        return struct.pack('<i', int(value or 0))


class ULongField:
    """32-bit unsigned integer (Clarion ULONG)."""
    def __init__(self, name):
        self.name = name
        self.length = 4
        self.field_type = F_ULONG
    
    def pack(self, value):
        return struct.pack('<I', int(value or 0))


class ShortField:
    """16-bit signed integer (Clarion SHORT)."""
    def __init__(self, name):
        self.name = name
        self.length = 2
        self.field_type = F_SHORT
    
    def pack(self, value):
        return struct.pack('<h', int(value or 0))


# ===================================================================
# მაღალი დონის ცხრილი
# ===================================================================

class OrisTable:
    """ORIS-compatible TPS ცხრილის builder."""
    
    def __init__(self, name="UNNAMED"):
        self.name = name
        self.fields = []          # list of field wrappers
        self.keys = []            # list of (key_name, [field_names])
        self.rows = []            # list of dicts
    
    def add_field(self, field):
        """დაამატე ფილდი (StringField, LongField, etc.)."""
        self.fields.append(field)
        return self
    
    def add_key(self, key_name, field_names, flags=6):
        """დაამატე index/key.
        
        key_name:    "TBL:K1" ფორმატით
        field_names: list of field names key-ში (sorting order)
        """
        self.keys.append((key_name, field_names, flags))
        return self
    
    def add_row(self, row_dict):
        """დაამატე ჩანაწერი dict-ით {field_name: value}."""
        self.rows.append(row_dict)
        return self
    
    def _compute_offsets(self):
        """ფილდების offset-ების ავტომატური გამოთვლა."""
        offset = 0
        offsets = {}
        for f in self.fields:
            offsets[f.name] = offset
            offset += f.length
        return offsets, offset
    
    def _build_low_level(self):
        """გადათარგმნა low-level tps_writer_v2 ობიექტებად."""
        offsets, record_length = self._compute_offsets()
        
        # Field definitions
        # field "index" attribute = რომელ key-ში მონაწილეობს (1-based, 0 = none)
        field_in_key = {}
        for ki, (kname, kfields, kflags) in enumerate(self.keys, start=1):
            for fn in kfields:
                # ფილდი key-ში მონაწილეობს
                field_in_key[fn] = ki
        
        # Field "index" attribute = display sequence number (0, 1, 2, ...).
        # ეს NOT key participation! Scalabium Clarion Viewer ფილდებს ალაგებს
        # ამ attribute-ით. რეალურ ARN.TPS-ში KADR=#0, SECT=#1 (definition order).
        ll_fields = []
        for fi, f in enumerate(self.fields):
            ll_fields.append(Field(
                f.name, f.field_type,
                offset=offsets[f.name],
                length=f.length,
                flags=0,
                index=fi,    # sequential display order
            ))
        
        # Index definitions
        field_name_to_idx = {f.name: i for i, f in enumerate(self.fields)}
        ll_indexes = []
        for kname, kfields, kflags in self.keys:
            key_fields = [(field_name_to_idx[fn], 0) for fn in kfields]
            ll_indexes.append(Index(
                kname,
                fields_in_key=len(kfields),
                key_fields=key_fields,
                flags=kflags,
            ))
        
        # Data rows: pack each row
        data_rows = []
        for i, row in enumerate(self.rows, start=1):
            buf = BytesIO()
            for f in self.fields:
                value = row.get(f.name, "")
                buf.write(f.pack(value))
            data_rows.append((i, buf.getvalue()))
        
        return ll_fields, ll_indexes, data_rows
    
    def build(self):
        """დააბრუნე ფაილის bytes."""
        ll_fields, ll_indexes, data_rows = self._build_low_level()
        
        return build_minimal_tps(
            table_name=self.name,
            fields=ll_fields,
            data_rows=data_rows,
            indexes=ll_indexes if ll_indexes else None,
        )
    
    def save(self, path):
        """შეინახე .tps ფაილი."""
        tps = self.build()
        with open(path, 'wb') as f:
            f.write(tps)
        return len(tps)


# ===================================================================
# Demo
# ===================================================================

if __name__ == '__main__':
    print("=== ORIS TPS Writer — High-level API demo ===\n")
    
    # ARN.TPS-ის ანალოგი
    table = OrisTable("UNNAMED")
    table.add_field(StringField("ARN:KADR", length=50))
    table.add_field(StringField("ARN:SECT", length=20))
    table.add_key("ARN:K1", ["ARN:SECT", "ARN:KADR"])
    
    georgian_names = [
        "ოქრიაშვილი გ. .",
        "გოგიჩაძე ა. .",
        "კაპანაძე ე. .",
        "კიკვაძე დ. .",
        "ახვლედიანი ზ. .",
        "ბერიძე ნ. .",
        "მჭედლიშვილი თ. .",
    ]
    
    for name in georgian_names:
        table.add_row({"ARN:KADR": name, "ARN:SECT": ""})
    
    size = table.save("test_highlevel.tps")
    print(f"✓ test_highlevel.tps: {size} bytes")
    print(f"  ცხრილი: {table.name}")
    print(f"  ფილდები: {len(table.fields)}")
    print(f"  Keys: {len(table.keys)}")
    print(f"  ჩანაწერები: {len(table.rows)}")
    print()
    print("ქართული გვარები ჩაწერილია ORIS encoding-ით:")
    for name in georgian_names:
        encoded = encode_oris(name)
        print(f"  {name!r:25} → {encoded[:15].hex(' ')}...")
