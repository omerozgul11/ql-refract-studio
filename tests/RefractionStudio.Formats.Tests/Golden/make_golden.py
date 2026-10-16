#!/usr/bin/env python3
"""Generates the golden SEG-2 / SEG-Y test files.

This generator is deliberately independent of the C# writer: it builds every byte from the
published layouts (SEG-2: Pullan 1990; SEG-Y rev 1) so the reader is tested against a second
implementation. Expected sample values are documented in GoldenFileTests.cs.

Run:  python3 make_golden.py   (writes files next to this script)
"""
import os
import struct

HERE = os.path.dirname(os.path.abspath(__file__))


def seg2_strings(pairs, e):
    out = b""
    for key, value in pairs:
        text = (key + (" " + value if value else "")).encode("latin-1") + b"\x00"
        out += struct.pack(e + "H", 2 + len(text)) + text
    return out + b"\x00\x00"


def pad4(b):
    return b + b"\x00" * ((4 - len(b) % 4) % 4)


def encode(samples, code, e):
    if code == 1:
        return b"".join(struct.pack(e + "h", s) for s in samples)
    if code == 2:
        return b"".join(struct.pack(e + "i", s) for s in samples)
    if code == 4:
        return b"".join(struct.pack(e + "f", s) for s in samples)
    if code == 5:
        return b"".join(struct.pack(e + "d", s) for s in samples)
    if code == 3:
        out = b""
        for g in range(0, len(samples), 4):
            exps, mants = 0, []
            for k, v in enumerate(samples[g:g + 4]):
                ex = 0
                while abs(v) >> ex > 32767:
                    ex += 1
                m = v >> ex if v >= 0 else -((-v) >> ex)
                if m < 0:
                    m -= 1  # one's complement
                exps |= ex << (4 * k)
                mants.append(m)
            out += struct.pack(e + "H", exps) + b"".join(struct.pack(e + "h", m) for m in mants)
        return out
    raise ValueError(code)


def seg2(traces, code, little, file_strings, descriptor_id=0x4422, override_code=None):
    e = "<" if little else ">"
    n = len(traces)
    m = 4 * n
    header = struct.pack(e + "HHHH", 0x3A55, 1, m, n) + bytes([1, 0, 0, 1, 0x0A, 0]) + b"\x00" * 18
    assert len(header) == 32
    body = pad4(seg2_strings(file_strings, e))
    offset = 32 + m + len(body)
    blocks = b""
    pointers = []
    for strings, samples in traces:
        pointers.append(offset + len(blocks))
        s = seg2_strings(strings, e)
        size = 32 + len(s)
        size += (4 - size % 4) % 4
        data = encode(samples, code, e)
        desc = struct.pack(e + "HHII", descriptor_id, size, len(data), len(samples)) + bytes([override_code or code]) + b"\x00" * 19
        desc = (desc + s).ljust(size, b"\x00")
        blocks += pad4(desc + data)
    ptr = b"".join(struct.pack(e + "I", p) for p in pointers)
    return header + ptr + body + blocks


FILE_STRINGS = [("ACQUISITION_DATE", "10/OCT/2026"), ("ACQUISITION_TIME", "14:05:30"),
                ("INSTRUMENT", "GOLDEN TEST 24"), ("SHOT_SEQUENCE_NUMBER", "7")]


def trace_strings(ch, dt="0.00025", delay="-0.005", extra=None):
    s = [("CHANNEL_NUMBER", str(ch)), ("SAMPLE_INTERVAL", dt), ("DELAY", delay),
         ("RECEIVER_LOCATION", f"{(ch - 1) * 2.0:.1f}"), ("SOURCE_LOCATION", "-1.0"), ("STACK", "3")]
    if extra:
        s += extra
    return s


INT_SAMPLES = [0, 1, -1, 1000, -1000, 32767, -32768, 12345]
BIG_SAMPLES = [0, 100000, -100000, 2000000, -2000000, 7, -7, 65536]


def write(name, data):
    with open(os.path.join(HERE, name), "wb") as f:
        f.write(data)


def main():
    # Valid files.
    write("le_int16.sg2", seg2([(trace_strings(c), INT_SAMPLES) for c in (1, 2, 3)], 1, True, FILE_STRINGS))
    write("be_int32.sg2", seg2([(trace_strings(c, extra=[("DESCALING_FACTOR", "0.5")]), BIG_SAMPLES) for c in (1, 2)], 2, False, FILE_STRINGS))
    write("le_float32.sg2", seg2([(trace_strings(c), [0.5, -0.25, 1.5e-3, 3.0]) for c in (1, 2)], 4, True, FILE_STRINGS))
    write("be_float64.sg2", seg2([(trace_strings(c), [1e-9, -2.5, 3.25, 0.0]) for c in (1,)], 5, False, FILE_STRINGS))
    write("le_20bit.sg2", seg2([(trace_strings(c), BIG_SAMPLES) for c in (1, 2)], 3, True, FILE_STRINGS))
    # Malformed files.
    good = seg2([(trace_strings(c), INT_SAMPLES) for c in (1, 2)], 1, True, FILE_STRINGS)
    write("truncated.sg2", good[: len(good) - 10])
    write("bad_descriptor.sg2", seg2([(trace_strings(1), INT_SAMPLES)], 1, True, FILE_STRINGS, descriptor_id=0x1234))
    write("unsupported_code.sg2", seg2([(trace_strings(1), INT_SAMPLES)], 1, True, FILE_STRINGS, override_code=9))
    write("no_sample_interval.sg2", seg2([([("CHANNEL_NUMBER", "1")], INT_SAMPLES)], 1, True, FILE_STRINGS))
    write("not_seg2.sg2", b"This is a text file, not seismic data." + b"\x00" * 40)
    write("segy_ibm_be.sgy", segy())


def ibm(v):
    if v == 0:
        return 0
    sign = 0x80000000 if v < 0 else 0
    v = abs(v)
    e = 64
    while v >= 1:
        v /= 16.0
        e += 1
    while v < 1.0 / 16:
        v *= 16.0
        e -= 1
    return sign | (e << 24) | int(round(v * (1 << 24)))


def segy():
    text = ("C 1 GOLDEN SEG-Y TEST FILE".ljust(80) * 40).encode("ascii")
    binary = bytearray(400)
    struct.pack_into(">h", binary, 16, 500)   # 500 us
    struct.pack_into(">h", binary, 20, 4)     # 4 samples
    struct.pack_into(">h", binary, 24, 1)     # IBM float
    struct.pack_into(">H", binary, 300, 0x0100)
    struct.pack_into(">h", binary, 302, 1)    # fixed length
    out = text + bytes(binary)
    samples = [1.0, -2.5, 0.15625, 100.0]
    for ffid, src in ((101, -2.0), (102, 50.0)):
        for ch in (1, 2, 3):
            th = bytearray(240)
            struct.pack_into(">i", th, 8, ffid)
            struct.pack_into(">i", th, 12, ch)
            struct.pack_into(">h", th, 30, 2)          # vertically summed
            struct.pack_into(">h", th, 70, -100)       # coordinates in cm
            struct.pack_into(">i", th, 72, int(src * 100))
            struct.pack_into(">i", th, 80, int((ch - 1) * 250))  # 2.5 m spacing
            struct.pack_into(">h", th, 108, 10)        # 10 ms delay
            struct.pack_into(">H", th, 114, 4)
            struct.pack_into(">H", th, 116, 500)
            out += bytes(th) + b"".join(struct.pack(">I", ibm(s * ch)) for s in samples)
    return out


if __name__ == "__main__":
    main()
