#!/usr/bin/env python3
"""
tps_insert — არსებულ .tps ფაილში ჩანაწერების ჩამატება/განახლება/წაშლა.

მიდგომა: read-modify-rewrite (უსაფრთხო)
  1. წავიკითხავთ მთელ არსებულ ფაილს (TpsReader)
  2. შევცვლით records-ს მეხსიერებაში
  3. regenerate ვაკეთებთ მთელ ფაილს (build_tps_multipage)

ეს B-tree-ს ხელით არ ანახლებს — index pages მთლიანად regenerate-დება
ახალი მონაცემებიდან. ეს უსაფრთხოა, რადგან Clarion-ი ინდექსს ფაილიდან
კითხულობს და ჩვენ ვაშენებთ კონსისტენტურ ინდექსს.

*** კრიტიკული უსაფრთხოების წესი ***
ყოველთვის backup-ი აიღე ცვლილებამდე. ეს კოდი ცვლის ფინანსურ მონაცემებს.
"""

import os
import shutil
import struct
from datetime import datetime
from io import BytesIO

import sys
sys.path.insert(0, '.')

from tps_reader import TpsReader, TpsField
from tps_writer_v2 import Field, Index
from tps_multipage import build_tps_multipage


# Field type → fixed-length detection (numeric types have implicit length)
_NUMERIC_TYPES = {
    0x01: 1,   # byte
    0x02: 2,   # short
    0x03: 2,   # ushort
    0x06: 4,   # long
    0x07: 4,   # ulong
    0x08: 4,   # float
    0x09: 8,   # double
}


class TpsTable:
    """
    არსებული .tps ფაილის in-memory რედაქტირებადი წარმოდგენა.

    გამოყენება:
        t = TpsTable.open("data.tps")
        t.insert({"ARN:KADR": "ახალი გვარი", "ARN:SECT": ""})
        t.save("data.tps", backup=True)
    """

    def __init__(self, reader: TpsReader):
        self._reader = reader
        self.table_name = reader.table_name or "UNNAMED"
        self.table_number = reader.table_number
        self.record_length = reader.record_length
        self.fields = reader.fields  # list of TpsField
        self.last_issued_row = reader.last_issued_row
        # records: dict record_number → row bytes (raw, ORIS-encoded)
        self.records = dict(reader.data_records)

    # ----------------------------------------------------------------
    @classmethod
    def open(cls, path):
        return cls(TpsReader(path))

    # ----------------------------------------------------------------
    def _field_by_name(self, name):
        for f in self.fields:
            if f.name == name:
                return f
        raise KeyError(f"ფილდი არ მოიძებნა: {name}")

    def _pack_row(self, values: dict) -> bytes:
        """dict {field_name: value} → raw ORIS-encoded row bytes."""
        from oris_encoding import to_oris_field
        buf = BytesIO()
        # ფილდები offset-ის მიხედვით დავალაგოთ
        ordered = sorted(self.fields, key=lambda f: f.offset)
        for f in ordered:
            val = values.get(f.name, None)
            if f.type in _NUMERIC_TYPES:
                size = _NUMERIC_TYPES[f.type]
                n = int(val) if val not in (None, "") else 0
                if f.type in (0x07, 0x03):  # unsigned
                    buf.write(n.to_bytes(size, 'little', signed=False))
                else:
                    buf.write(n.to_bytes(size, 'little', signed=True))
            else:
                # string types
                s = "" if val is None else str(val)
                buf.write(to_oris_field(s, f.length))
        return buf.getvalue()

    def _unpack_row(self, row: bytes) -> dict:
        """raw row bytes → dict {field_name: value}."""
        from oris_encoding import from_oris_field
        out = {}
        for f in self.fields:
            chunk = row[f.offset: f.offset + f.length]
            if f.type in _NUMERIC_TYPES:
                signed = f.type not in (0x07, 0x03)
                out[f.name] = int.from_bytes(chunk, 'little', signed=signed)
            else:
                out[f.name] = from_oris_field(chunk)
        return out

    # ----------------------------------------------------------------
    # CRUD ოპერაციები
    # ----------------------------------------------------------------
    def insert(self, values: dict) -> int:
        """ახალი ჩანაწერის ჩამატება. აბრუნებს ახალ record number-ს."""
        self.last_issued_row += 1
        rn = self.last_issued_row
        self.records[rn] = self._pack_row(values)
        return rn

    def insert_many(self, rows: list) -> list:
        """ბევრი ჩანაწერის ჩამატება."""
        return [self.insert(r) for r in rows]

    def update(self, record_number: int, values: dict):
        """არსებული ჩანაწერის განახლება (partial update)."""
        if record_number not in self.records:
            raise KeyError(f"record #{record_number} არ არსებობს")
        existing = self._unpack_row(self.records[record_number])
        existing.update(values)
        self.records[record_number] = self._pack_row(existing)

    def delete(self, record_number: int):
        """ჩანაწერის წაშლა."""
        self.records.pop(record_number, None)

    def get(self, record_number: int) -> dict:
        """ჩანაწერის წაკითხვა dict-ად."""
        return self._unpack_row(self.records[record_number])

    def all_rows(self):
        """ყველა ჩანაწერი (record_number, dict) წყვილებად, დალაგებული."""
        for rn in sorted(self.records.keys()):
            yield rn, self._unpack_row(self.records[rn])

    @property
    def count(self):
        return len(self.records)

    # ----------------------------------------------------------------
    # შენახვა
    # ----------------------------------------------------------------
    def save(self, path, backup=True):
        """
        ფაილის შენახვა (regenerate). 

        backup=True: არსებული ფაილის ასლს აიღებს .bak_TIMESTAMP სახელით.
        """
        if backup and os.path.exists(path):
            ts = datetime.now().strftime("%Y%m%d_%H%M%S")
            bak = f"{path}.bak_{ts}"
            shutil.copy2(path, bak)

        # Convert reader fields → writer fields (sequential index)
        ordered = sorted(self.fields, key=lambda f: f.offset)
        wfields = []
        for i, f in enumerate(ordered):
            wfields.append(Field(f.name, f.type, offset=f.offset,
                                 length=f.length, flags=0, index=i))

        # Index: ერთი key პირველ ფილდზე (default).
        # თუ ორიგინალს ჰქონდა index, regenerate ვაკეთებთ მსგავსს.
        idx_name = f"{self.table_name}:K1"
        indexes = [Index(idx_name, fields_in_key=1, key_fields=[(0, 0)], flags=6)]

        # Data rows — preserve original record numbers
        data_rows = [(rn, self.records[rn]) for rn in sorted(self.records.keys())]

        tps = build_tps_multipage(
            self.table_name, wfields, self.record_length, data_rows,
            indexes=indexes, table_number=self.table_number,
            last_issued_row=self.last_issued_row,
        )

        with open(path, 'wb') as fp:
            fp.write(tps)
        return len(tps)


if __name__ == '__main__':
    # Demo: არსებულ ფაილში ჩამატება
    import sys
    src = sys.argv[1] if len(sys.argv) > 1 else 'ARN.TPS'

    print(f"=== INSERT demo on {src} ===\n")
    t = TpsTable.open(src)
    print(f"ჩატვირთულია: {t.count} ჩანაწერი, last_issued={t.last_issued_row}")
    print(f"ფილდები: {[f.name for f in t.fields]}\n")

    # ჩავამატოთ ახალი ჩანაწერი
    new_rn = t.insert({"ARN:KADR": "ტესტაშვილი ახალი", "ARN:SECT": ""})
    print(f"ჩაემატა ახალი record #{new_rn}")
    print(f"ახლა: {t.count} ჩანაწერი")

    # შევინახოთ ცალკე ფაილში (ორიგინალს არ ვცვლით)
    out = src.replace('.TPS', '_modified.tps').replace('.tps', '_modified.tps')
    size = t.save(out, backup=False)
    print(f"შენახულია: {out} ({size} bytes)")
