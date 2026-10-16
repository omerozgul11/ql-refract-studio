# SEG-Y

Refraction Studio reads SEG-Y files (SEG Technical Standards Committee; revision 0, revision 1
(2002) and revision 2.0 (2017) layouts) as one or more field records. It does not write SEG-Y.

Sources:

| Topic | File |
|---|---|
| Reader | `src/RefractionStudio.Formats/SegYReader.cs` |
| Reader interface, registry, exceptions | `src/RefractionStudio.Formats/Abstractions.cs` |
| Import validation | `src/RefractionStudio.Formats/ImportInspection.cs` (see [seg2.md, section 8](seg2.md#8-import-validation-and-fixes); the checks are format-independent) |
| Golden file and generator | `tests/RefractionStudio.Formats.Tests/Golden/segy_ibm_be.sgy`, `make_golden.py` (`segy()`) |
| Tests | `tests/RefractionStudio.Formats.Tests/GoldenFileTests.cs` |

Byte positions below are given both as 1-based byte numbers (as in the SEG-Y standard) and as
0-based offsets.

---

## 1. Detection

* Reader id `segy`, name "SEG-Y", extensions `.sgy`, `.segy`.
* Detection is by content: `CanRead` accepts a file whose 3600-byte head contains a plausible binary
  header (section 3), whatever its extension. The extensions are used for file dialogs and the drop
  text.
* The registry tries SEG-2 first (by its 2-byte identifier), then SEG-Y.
* The whole file is read into memory. A metadata-only read returns empty sample arrays.

---

## 2. Textual header (bytes 1–3200)

The 3200-byte textual header is decoded to text and stored as the record keyword `TEXT_HEADER`
(40 lines of 80 characters, trailing spaces trimmed):

* If the first byte is `0xC3` (EBCDIC "C"), the header is treated as **EBCDIC** and converted with a
  built-in table covering the printable subset of code page 037: letters, digits, space and
  `. < ( + & $ * ) ; - / , % _ > ? : # @ ' = "`. Other bytes become spaces.
* Otherwise it is treated as **ASCII**; bytes outside 32–126 become spaces.

Extended textual headers (count in binary header bytes 3505–3506) are skipped (3200 bytes each) with
the notice "N extended textual header(s) skipped." A negative count (rev 2 "variable number") is
treated as 0.

---

## 3. Binary header (bytes 3201–3600)

| Bytes | Offset in binary header | Field | Use |
|---|---|---|---|
| 3217–3218 | 16 | Sample interval (µs), read as signed 16-bit | Must be > 0. Default for traces whose own interval is 0. |
| 3221–3222 | 20 | Samples per trace, read as unsigned 16-bit (0–65535) | Used for fixed-length files and for traces whose own count is 0. |
| 3225–3226 | 24 | Data sample format code | Must be 1, 2, 3, 5, 6 or 8. |
| 3297–3300 | 96 | Byte-order constant (rev 2) | `0x01020304` → big-endian, `0x04030201` → little-endian (read as big-endian). |
| 3501–3502 | 300 | Format revision | Major revision = high byte (e.g. `0x0100` → 1). Shown in the format description only. |
| 3503–3504 | 302 | Fixed-length trace flag | 1 → every trace uses the binary-header sample count. |
| 3505–3506 | 304 | Number of extended textual headers | Skipped (section 2). |

### 3.1 Byte order

If the rev 2 byte-order constant is present, it decides the byte order. Otherwise the reader tries
**big-endian first, then little-endian**, and accepts the first order in which the format code is
one of {1, 2, 3, 5, 6, 8}, and the sample interval is > 0. If neither
order is plausible the file is rejected: "does not have a valid SEG-Y binary header (unsupported
sample format or zero sample interval)." The chosen order applies to all trace headers and samples.

---

## 4. Trace headers (240 bytes each)

| Bytes | Offset | Field | Use |
|---|---|---|---|
| 1–4 | 0 | Trace sequence number within line | Channel number if bytes 13–16 are ≤ 0. |
| 9–12 | 8 | Original field record number (FFID) | Groups traces into records; keyword `FIELD_RECORD`. |
| 13–16 | 12 | Trace number within the field record | Channel number; keyword `TRACE_IN_RECORD`. |
| 31–32 | 30 | Number of vertically summed traces | `TraceHeader.Stack` if > 0. |
| 41–44 | 40 | Receiver group elevation | Receiver elevation, scaled by bytes 69–70. |
| 69–70 | 68 | Elevation scalar | Applied to bytes 41–44. |
| 71–72 | 70 | Coordinate scalar | Applied to bytes 73–76 and 81–84. |
| 73–76 | 72 | Source coordinate X | Source position; keyword `SOURCE_X`. |
| 81–84 | 80 | Group coordinate X | Receiver position; keyword `RECEIVER_X`. |
| 109–110 | 108 | Delay recording time (ms, signed 16-bit) | Record time of the first sample = value × 0.001 s, scaled by the time scalar (bytes 215–216) when it is present. |
| 215–216 | 214 | Time scalar (rev 1) | Applied to the delay with the scalar rule below; 0 or absent = unchanged. |
| 115–116 | 114 | Number of samples (unsigned 16-bit) | Used unless the fixed-length flag is set or the value is 0 (then the binary header value). |
| 117–118 | 116 | Sample interval (µs, unsigned 16-bit) | 0 → binary header value. |
| 157–158 | 156 | Year | Acquisition time of the record (first trace of each record). |
| 159–160 | 158 | Day of year | |
| 161–162 | 160 | Hour | |
| 163–164 | 162 | Minute | |
| 165–166 | 164 | Second | |

**Scalars** (`Scale`): 0 or 1 → value unchanged; positive → multiply; negative → divide by the
absolute value. A receiver elevation of 0 with an elevation scalar of 0 is treated as "not recorded".

**Acquisition time**: requires year > 0 and day 1–366; two-digit years are mapped to 2000–2069
(< 70) or 1970–1999; hour, minute and second are clamped to 0–23/0–59/0–59. The time is stored as
UTC; the time basis code (bytes 167–168) is not read.

**Keywords** stored per trace: `FIELD_RECORD`, `TRACE_IN_RECORD`, `SOURCE_X`, `RECEIVER_X` (the
scaled values).

---

## 5. Data sample formats

| Code | Encoding | Bytes | Reported `SampleFormat` |
|---|---|---|---|
| 1 | IBM System/360 single-precision float | 4 | `IBM float32` |
| 2 | 32-bit two's-complement integer | 4 | `int32` |
| 3 | 16-bit two's-complement integer | 2 | `int16` |
| 5 | IEEE 754 single precision | 4 | `float32` |
| 6 | IEEE 754 double precision | 8 | `float64` |
| 8 | 8-bit two's-complement integer | 1 | `int8` |

Codes 4 (fixed point with gain), 7 (24-bit integer), 9–12 and 15–16 (64-bit and unsigned integers,
rev 2) and any others are not supported; such files fail the binary-header check.

**IBM float** (`SegYReader.IbmToDouble`):

```
value = (−1)^s · 0.F · 16^(E − 64)
s = bit 31,  E = bits 30–24 (7 bits),  F = bits 23–0 (24-bit fraction / 2^24)
```

A zero fraction gives 0. The textbook example `0x4276A000` = 118.625 is tested
(`Ibm_float_conversion_round_trips`). `SegYReader.DoubleToIbm` exists for building test data only.

No amplitude scaling is applied (the trace-weighting factor, bytes 169–170, is not read). Samples that
are not finite cause the error "Trace k of … contains invalid (non-finite) sample values."

---

## 6. Records

Traces are read sequentially after the textual and binary headers (and any extended textual
headers). Consecutive traces with the same FFID (bytes 9–12) form one record, in file order; a
change of FFID starts a new record (an FFID that reappears later starts another record). For each
record:

* `FieldRecordNumber` = FFID;
* `SourceX` = source X of its first trace;
* `AcquiredAt` = time of its first trace;
* `Keywords` = `TEXT_HEADER`.

The format description is `SEG-Y rev R, big-endian|little-endian, format code F`.

---

## 7. Errors and notices

| Condition | Result |
|---|---|
| File shorter than 3600 bytes | Error: is too short to be a SEG-Y file |
| No plausible binary header in either byte order | Error: does not have a valid SEG-Y binary header … (detail: bytes 3217–3218 and 3225–3226 were checked in both byte orders) |
| A trace's samples extend beyond the end of the file | Error: is truncated: trace k extends beyond the end of the file |
| No complete trace header after the headers | Error: contains no traces |
| Non-finite samples | Error (section 5) |
| Bytes left after the last complete trace | Notice: N trailing byte(s) after the last trace were ignored. |
| Extended textual headers | Notice (section 2) |

Import validation (duplicates, sample-interval and channel checks, dead and clipped traces, delays,
source positions) is the same as for SEG-2: see [seg2.md, section 8](seg2.md#8-import-validation-and-fixes).
Multi-record SEG-Y files give one project record per FFID group, labelled `<file name> #<FFID>`.

---

## 8. Golden file

`segy_ibm_be.sgy` is generated by `make_golden.py` (function `segy()`), independently of the reader:

* ASCII textual header ("C 1 GOLDEN SEG-Y TEST FILE" on every line);
* binary header: sample interval 500 µs, 4 samples, format 1 (IBM float), revision `0x0100`,
  fixed-length flag 1, big-endian, no byte-order constant;
* two records, FFID 101 (source X −2.0 m) and 102 (source X 50.0 m), 3 traces each;
* coordinate scalar −100 (values in cm), group X = (channel − 1) × 2.5 m, delay 10 ms, 2 vertically
  summed traces, samples `[1.0, −2.5, 0.15625, 100.0] × channel`.

`Segy_ibm_big_endian_groups_traces_by_field_record` checks two records, FFID 101, both source
positions, and for channel 3 of the second record: receiver x 5.0 m, interval 0.0005 s, delay
0.010 s, stack 2 and samples `[3.0, −7.5, 0.46875, 300.0]`. `Registry_detects_formats_by_content`
checks detection of the file as SEG-Y.

---

## 9. Limitations

* Sample counts are limited to 65535 per trace (16-bit fields; the rev 2 extended sample-count field
  is not read).
* The delay comes from bytes 109–110 (with the time scalar); lag times A/B (bytes 105–108) are
  ignored.
* Only X coordinates are used. Source/receiver Y, source elevation, coordinate units (bytes 89–90)
  and the trace identification code (bytes 29–30, e.g. dead or auxiliary traces) are not read.
* Rev 2 extensions beyond the byte-order constant are not supported: additional trace header blocks,
  extended sample count and interval fields in the binary header, data trailers and the additional
  sample formats.
* The textual header's EBCDIC conversion covers only the printable subset of code page 037.
