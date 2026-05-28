#!/usr/bin/env python3
"""
TopSpeed RLE (Run-Length Encoding) codec.

ფორმატი reverse-engineered tps-parse-ის RandomAccess.deRle()-დან.
არსი:
    [skip_count][skip_bytes...][repeat_count_minus_1]
    
    skip_bytes-ის ბოლო ბაიტი მერე გაიმეორება (repeat_count) ჯერ.

ნიუანსები:
- skip_count არ შეიძლება იყოს 0x00 (error)
- თუ skip > 0x7F: ეს არის 2-byte encoding
- იგივე ლოგიკა repeat-count-ისთვის
"""

from io import BytesIO


def decompress_rle(compressed: bytes) -> bytes:
    """TopSpeed RLE decompression."""
    src = compressed
    out = bytearray()
    pos = 0
    n = len(src)
    
    while pos < n:
        skip = src[pos]
        pos += 1
        
        if skip == 0:
            # Trailing zero byte = end of stream (page padding)
            # Java's loop has `while (!cmp.isAtEnd())` but real ORIS pages
            # sometimes end with 0x00 — we tolerate it.
            if pos == n:
                break
            raise ValueError(f"Bad RLE Skip (0x00) at position {pos-1}, {n-pos} bytes remaining")
        
        if skip > 0x7F:
            if pos >= n:
                raise ValueError("Incomplete 2-byte skip")
            msb = src[pos]
            pos += 1
            lsb = skip & 0x7F
            shift = 0x80 * (msb & 0x01)
            skip = ((msb << 7) & 0x00FF00) + lsb + shift
        
        # Copy `skip` raw bytes
        if pos + skip > n:
            raise ValueError(f"Skip overflow at pos {pos}: skip={skip}, remaining={n-pos}")
        out.extend(src[pos:pos + skip])
        pos += skip
        
        # Check if there's a repeat byte (more than 1 byte remaining)
        if pos < n - 1:
            # The byte to repeat is the last byte we copied (already in out[-1])
            # Java reads it from input (same thing due to jumpRel(-1))
            to_repeat = out[-1]
            repeats_minus_one = src[pos]
            pos += 1
            
            if repeats_minus_one > 0x7F:
                if pos >= n:
                    raise ValueError("Incomplete 2-byte repeat count")
                msb = src[pos]
                pos += 1
                lsb = repeats_minus_one & 0x7F
                shift = 0x80 * (msb & 0x01)
                repeats_minus_one = ((msb << 7) & 0x00FF00) + lsb + shift
            
            # Append repeated bytes
            out.extend(bytes([to_repeat]) * repeats_minus_one)
    
    return bytes(out)


def compress_rle(raw: bytes) -> bytes:
    """
    TopSpeed RLE compression — inverse of decompress_rle.
    
    ეს უფრო რთულია — ალგორითმს ვაშენებთ "ბაიტი-by-ბაიტი" greedy მოდელით:
    - ვცდილობთ ვიპოვოთ "skip section" — ბაიტები, რომლებიც განსხვავდება
    - შემდეგ "run" — ერთი და იგივე ბაიტი
    """
    out = bytearray()
    n = len(raw)
    pos = 0
    
    while pos < n:
        # Find skip section: consecutive non-repeated bytes
        # We need at least 1 byte to skip.
        # Strategy: find next run of >=2 same bytes after pos
        skip_end = pos + 1   # we always include at least 1 byte
        
        # Extend skip until we find a 2+ run (cheaper to encode as run)
        # OR until we hit max skip length
        MAX_SKIP_1BYTE = 0x7F
        
        while skip_end < n and (skip_end - pos) < 0x3F00:   # rough max for 2-byte skip
            # Check if at skip_end starts a run of 2+ same bytes
            if skip_end + 1 < n and raw[skip_end] == raw[skip_end + 1]:
                # Yes — stop the skip here
                break
            skip_end += 1
        
        skip_len = skip_end - pos
        
        # Encode skip length
        if skip_len <= MAX_SKIP_1BYTE:
            out.append(skip_len)
        else:
            # 2-byte encoding
            # skip = ((msb << 7) & 0xFF00) + lsb + (0x80 * (msb & 0x01))
            # We need to encode skip_len back to msb/lsb
            lsb = (skip_len & 0x7F) | 0x80   # set high bit on first byte
            msb_val = (skip_len >> 7)
            # Note: there's a "shift" trick — when msb has low bit, +0x80 added
            # For simplicity, we encode msb directly
            # Validate: ((msb << 7) & 0xFF00) + (lsb & 0x7F) + 0x80*(msb&1) == skip_len
            out.append(lsb)
            out.append(msb_val)
        
        # Copy raw bytes
        out.extend(raw[pos:skip_end])
        pos = skip_end
        
        # Now encode run (if there are bytes left and they form a run)
        if pos < n:
            run_byte = raw[pos]
            run_end = pos
            while run_end < n and raw[run_end] == run_byte:
                run_end += 1
            run_len = run_end - pos
            
            if run_len == 0:
                break
            
            # The run "replays" the LAST byte we wrote — but our last byte
            # was raw[pos-1] which may differ from run_byte!
            # 
            # Reality: tps-parse's algorithm REPLAYS the last byte of skip.
            # So for our compressor to work, the last byte of skip MUST equal run_byte.
            # 
            # If they differ, we need to extend the skip to include 1 byte of the run,
            # so that the last skip byte == run_byte.
            
            if pos > 0 and raw[pos - 1] != run_byte:
                # extend skip by 1
                # Re-emit: drop the previous skip encoding, re-encode with +1
                # For simplicity: just include 1 byte of the run in skip, reduce run by 1
                # This requires rewinding — handle by NOT processing this run if it would cause issues
                # Simpler: emit one extra byte as a "skip 1" segment
                out.append(1)
                out.append(run_byte)
                pos += 1
                run_len -= 1
                if run_len == 0:
                    continue
            
            # Encode run length
            if run_len <= MAX_SKIP_1BYTE:
                out.append(run_len)
            else:
                lsb = (run_len & 0x7F) | 0x80
                msb_val = (run_len >> 7)
                out.append(lsb)
                out.append(msb_val)
            
            pos += run_len
    
    return bytes(out)


# ===================================================================
# Self-test
# ===================================================================

if __name__ == '__main__':
    print("=== TopSpeed RLE codec self-test ===\n")
    
    test_cases = [
        b'ABC',
        b'AAAA',
        b'ABCAAAA',
        b'Hello World!',
        b'\x00' * 50 + b'Some text' + b'\x20' * 30,
        b'Field1: Test Data\x00\x00\x00\x00Field2: More\x20\x20\x20\x20',
    ]
    
    for raw in test_cases:
        try:
            compressed = compress_rle(raw)
            decompressed = decompress_rle(compressed)
            match = decompressed == raw
            print(f"  {'✓' if match else '✗'} {raw[:40]!r}{'...' if len(raw) > 40 else ''}")
            print(f"     raw size: {len(raw)}, compressed: {len(compressed)} ({100*len(compressed)/len(raw):.0f}%)")
            if not match:
                print(f"     EXPECTED: {raw!r}")
                print(f"     GOT:      {decompressed!r}")
        except Exception as e:
            print(f"  ✗ {raw[:40]!r}: ERROR {e}")
    
    # Real-world test: try to decompress data from ARN.TPS
    print()
    print("=== Decompressing real ORIS page ===")
    
    data = open('ARN.TPS', 'rb').read()
    # Block [2]: file offset 0x300, page size 0x1b7, uncompressed 0x571
    page_start = 0x300
    page_size = 0x1b7
    page_size_uc = 0x571
    
    compressed_data = data[page_start + 13: page_start + page_size]
    print(f"Compressed data size: {len(compressed_data)} (expected {page_size - 13})")
    print(f"Expected uncompressed: {page_size_uc - 13} bytes")
    
    try:
        decompressed = decompress_rle(compressed_data)
        print(f"Decompressed size: {len(decompressed)}")
        print(f"First 100 bytes hex: {decompressed[:100].hex(' ')}")
        # Try to decode as ORIS
        import sys; sys.path.insert(0, '.')
        from oris_encoding import decode_oris
        # Look for Georgian text
        readable = decode_oris(decompressed[:200], errors='replace')
        print(f"As ORIS text (first 200 bytes):")
        print(f"  {readable!r}")
    except Exception as e:
        print(f"Decompression error: {e}")
        import traceback
        traceback.print_exc()
