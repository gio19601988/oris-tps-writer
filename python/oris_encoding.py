#!/usr/bin/env python3
"""
ORIS Georgian Encoding Codec
============================

ORIS იყენებს single-byte custom codepage-ს, რომელიც bit-by-bit
შეესაბამება CP1251/CP1252-ის bytes 0xC0-0xE4 დიაპაზონს, მაგრამ
ეს ბაიტები რეალურად ქართულ ანბანს წარმოადგენენ.

Source: Converter_რეალური_სიმბოლოებით.xlsx (33 ქართული letter)

ASCII (0x00-0x7F) გადადის as-is.
ციფრები, სიმბოლოები, space — ჩვეულებრივი ASCII.

ეს encoding ცნობილია სხვადასხვა სახელით:
  - ORIS Unicode (პრაქტიკულად არ არის Unicode)
  - "ცრუ კირილიცა" / fake Cyrillic
  - GeorgianTransliterated

გამოყენება:
    from oris_encoding import encode_oris, decode_oris
    
    raw_bytes = encode_oris("შპს ალფა")     # → b'\\xc7\\xd0\\xd1 \\xc0\\xcb\\xce\\xc0'
    text = decode_oris(raw_bytes)            # → "შპს ალფა"
"""

# Forward mapping: Georgian Unicode char → ORIS byte
# (ანბანის თანმიმდევრობა Converter Excel-დან)
_GEO_TO_BYTE = {
    'ა': 0xC0, 'ბ': 0xC1, 'გ': 0xC2, 'დ': 0xC3, 'ე': 0xC4,
    'ვ': 0xC5, 'ზ': 0xC6, 'თ': 0xC8, 'ი': 0xC9, 'კ': 0xCA,
    'ლ': 0xCB, 'მ': 0xCC, 'ნ': 0xCD, 'ო': 0xCF, 'პ': 0xD0,
    'ჟ': 0xD1, 'რ': 0xD2, 'ს': 0xD3, 'ტ': 0xD4, 'უ': 0xD6,
    'ფ': 0xD7, 'ქ': 0xD8, 'ღ': 0xD9, 'ყ': 0xDA, 'შ': 0xDB,
    'ჩ': 0xDC, 'ც': 0xDD, 'ძ': 0xDE, 'წ': 0xDF, 'ჭ': 0xE0,
    'ხ': 0xE1, 'ჯ': 0xE3, 'ჰ': 0xE4,
}

# Reverse mapping: ORIS byte → Georgian Unicode char
_BYTE_TO_GEO = {v: k for k, v in _GEO_TO_BYTE.items()}


def encode_oris(text: str, errors: str = 'replace') -> bytes:
    """
    ქართული Unicode ტექსტი → ORIS bytes.
    
    Args:
        text:   Python string (UTF-8 internal)
        errors: 'replace' (default) — non-mapped chars become '?'
                'strict'             — raise ValueError
                'ignore'             — skip non-mapped chars
    
    Returns:
        bytes — ORIS-compatible single-byte sequence
    """
    out = bytearray()
    for ch in text:
        if ch in _GEO_TO_BYTE:
            out.append(_GEO_TO_BYTE[ch])
        elif ord(ch) < 0x80:
            # ASCII pass-through
            out.append(ord(ch))
        else:
            if errors == 'strict':
                raise ValueError(f"Char {ch!r} (U+{ord(ch):04X}) not in ORIS encoding")
            elif errors == 'replace':
                out.append(ord('?'))
            # 'ignore' — skip
    return bytes(out)


def decode_oris(data: bytes, errors: str = 'replace') -> str:
    """
    ORIS bytes → ქართული Unicode ტექსტი.
    """
    out = []
    for b in data:
        if b in _BYTE_TO_GEO:
            out.append(_BYTE_TO_GEO[b])
        elif b < 0x80:
            out.append(chr(b))
        else:
            if errors == 'strict':
                raise ValueError(f"Byte 0x{b:02X} not in ORIS encoding")
            elif errors == 'replace':
                out.append('?')
            # 'ignore' — skip
    return ''.join(out)


def to_oris_field(text: str, length: int, pad_byte: int = 0x20) -> bytes:
    """
    String → fixed-length ORIS field (Clarion STRING type).
    
    შემთხვევები:
    - text უფრო მოკლე: pad-ი space-ით (0x20)
    - text უფრო გრძელი: truncate-ი length-ზე
    
    გამოყენება Clarion's FIELD_STRING (type 0x12)-ისთვის.
    """
    encoded = encode_oris(text)
    if len(encoded) >= length:
        return encoded[:length]
    return encoded + bytes([pad_byte]) * (length - len(encoded))


def from_oris_field(data: bytes) -> str:
    """
    Fixed-length ORIS field → trimmed Unicode string.
    """
    return decode_oris(data).rstrip()


# ===================================================================
# Self-test
# ===================================================================

if __name__ == '__main__':
    print("=== ORIS Encoding Codec Test ===\n")
    
    # ტესტი 1: encode/decode round-trip
    samples = [
        "შპს ალფა",
        "ნედლეული მასალები",
        "გაყიდვები",
        "ABC 123 ქართული",
        "ვალდებულებანი მიმწოდებლების მიმართ",
    ]
    
    for s in samples:
        encoded = encode_oris(s)
        decoded = decode_oris(encoded)
        status = "✓" if decoded == s else "✗"
        hex_str = ' '.join(f'{b:02x}' for b in encoded[:30])
        print(f"  {status} {s!r}")
        print(f"     bytes ({len(encoded)}): {hex_str}{'...' if len(encoded) > 30 else ''}")
        print(f"     decoded: {decoded!r}")
        print()
    
    # ტესტი 2: fixed-length field
    print("=== Fixed-length field test ===")
    name = "შპს ალფა"
    field = to_oris_field(name, 30)
    print(f"  Input:    {name!r}")
    print(f"  Field:    {field!r}")
    print(f"  Length:   {len(field)} bytes")
    print(f"  Restored: {from_oris_field(field)!r}")
    print()
    
    # ტესტი 3: სრულდება როგორ ჩანდა ORIS-ში თუ ფაილს ჩავწერთ ბაიტურად
    # და გავხსნით CP1251 viewer-ით
    print("=== How it appears in CP1251 viewer ===")
    s = "შპს ალფა"
    raw = encode_oris(s)
    print(f"  Unicode source: {s!r}")
    print(f"  Raw bytes:      {raw.hex(' ')}")
    print(f"  CP1251 render:  {raw.decode('cp1251', errors='replace')!r}")
    print(f"  CP1252 render:  {raw.decode('cp1252', errors='replace')!r}")
    print()
    print("  ↑ ეს არის ის, რასაც ხედავ ORIS-ში 'რუსული ფონტით'.")
