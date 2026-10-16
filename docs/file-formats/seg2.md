# SEG-2

Refraction Studio reads SEG-2 field records (Pullan, S.E., 1990, "Recommended standard for seismic
(/radar) data files in the personal computer environment", *Geophysics* 55(9), 1260–1271) and can
write them for synthetic data, the sample project and tests.

Sources:

| Topic | File |
|---|---|
| Reader | `src/RefractionStudio.Formats/Seg2Reader.cs` |
| Writer | `src/RefractionStudio.Formats/Seg2Writer.cs` |
| Reader interface, registry, exceptions | `src/RefractionStudio.Formats/Abstractions.cs` |
| Import validation (issues and fixes) | `src/RefractionStudio.Formats/ImportInspection.cs` |
| Turning decoded records into lines, spreads and gathers | `src/RefractionStudio.Core/Import/ImportService.cs` |
| Golden files and their independent generator | `tests/RefractionStudio.Formats.Tests/Golden/`, `make_golden.py` |
| Tests | `tests/RefractionStudio.Formats.Tests/GoldenFileTests.cs`, `ImportInspectionTests.cs` |

---

## 1. Detection

* Reader id `seg2`, name "SEG-2", extensions `.sg2`, `.seg2`, `.dat`.
* `CanRead` looks only at the first two bytes: `55 3A` (little-endian file descriptor identifier
  0x3A55) or `3A 55` (big-endian). The extension is not required for detection; the
  `FormatRegistry` tries SEG-2 before SEG-Y (`FormatRegistry.CreateDefault`). The test
  `Registry_detects_formats_by_content` detects a SEG-2 file named `anything.dat`.
* The whole file is read into memory and parsed (`Seg2Reader.Parse`). A metadata-only read
  (`ReadMetadataAsync`) parses all headers but returns empty sample arrays.

---

## 2. Byte order

The byte order is taken from the file descriptor identifier: `55 3A` → little-endian, `3A 55` →
big-endian. All multi-byte integers, the string-list length fields and the sample data of the file
are then read in that order. Both byte orders are supported for every data format code.

---

## 3. File descriptor block

Offsets are 0-based bytes from the start of the file.

| Offset | Size | Field | Use |
|---|---|---|---|
| 0 | 2 | File descriptor block id (0x3A55) | Byte order and identification. Anything else: "not a SEG-2 file". |
| 2 | 2 | Revision number | A value other than 1 produces the notice "SEG-2 revision number is N; the file was read with the revision 1 rules." |
| 4 | 2 | M: size of the trace pointer sub-block (bytes) | Must be ≥ 4·N. |
| 6 | 2 | N: number of traces | Must be > 0. |
| 8 | 1 | String terminator size | Must be 1 or 2. |
| 9 | 1–2 | String terminator character(s) | Used to cut each string. |
| 11 | 1 | Line terminator size | Must be 0, 1 or 2. |
| 12 | 0–2 | Line terminator character(s) | Replaced by a line feed inside strings. |
| 14–31 | | Reserved | Not read. |
| 32 | M | Trace pointers: N unsigned 32-bit offsets | Offset of each trace descriptor block. |
| 32+M | | File descriptor strings | Up to the smallest trace pointer. |

The file must be at least 32 bytes long, and the pointer table must lie inside the file.

---

## 4. Trace descriptor block

Offsets are relative to the trace pointer.

| Offset | Size | Field | Use |
|---|---|---|---|
| 0 | 2 | Trace descriptor block id (0x4422) | Anything else: "does not start with a SEG-2 trace descriptor". |
| 2 | 2 | Block size (bytes) | Must be ≥ 32 and inside the file. Samples start at pointer + block size. |
| 4 | 4 | Data block size (bytes) | Must hold the declared samples. |
| 8 | 4 | Number of samples | |
| 12 | 1 | Data format code | 1–5 supported (section 5). |
| 13–31 | | Reserved | Not read. |
| 32 | block size − 32 | Trace descriptor strings | Section 6. |

The pointer must be ≥ 32 and the 32-byte fixed part must lie inside the file.

---

## 5. Data formats

| Code | Encoding | Bytes per sample | Reported `SampleFormat` |
|---|---|---|---|
| 1 | 16-bit two's-complement integer | 2 | `int16` |
| 2 | 32-bit two's-complement integer | 4 | `int32` |
| 3 | 20-bit packed floating point | 2.5 | `20-bit float` |
| 4 | IEEE 754 single precision | 4 | `float32` |
| 5 | IEEE 754 double precision | 8 | `float64` |

Any other code is rejected: "… uses an unsupported sample encoding (data format code N)." with the
detail "SEG-2 defines codes 1–5."

### 5.1 20-bit packing (code 3)

Samples are stored in groups of four in 10 bytes:

```
bytes 0–1   16-bit exponent word: 4-bit exponent per sample,
            sample 0 in the least-significant nibble, sample 3 in the most-significant nibble
bytes 2–9   four 16-bit mantissas, one's complement
```

Each value is decoded as

```
m = signed 16-bit mantissa;  if m < 0: m = m + 1     (one's complement → two's complement)
e = (exponentWord >> (4·k)) & 0xF
value = m · 2^e
```

The sample count must be a multiple of 4; otherwise the trace is rejected ("uses the 20-bit encoding
with a sample count (N) that is not a multiple of 4"). The number of bytes needed is
`ceil(N · 2.5)`.

### 5.2 Scaling

When the trace has a `DESCALING_FACTOR` keyword whose numeric value is not 0 and not 1, every sample
is multiplied by it. No other scaling is applied. Decoded samples that are not finite (NaN or
infinity) cause the error "… contains invalid (non-finite) sample values."

---

## 6. Strings and keywords

### 6.1 String list parsing (`Seg2Reader.ParseStrings`)

Both the file descriptor strings and each trace descriptor's strings are a list of entries:

```
[2-byte offset to the next entry, counted from the start of this entry] [text] [string terminator] …
```

A zero offset ends the list. An offset smaller than 2 or running past the end of the block stops
parsing (the strings read so far are kept; no error is raised). For each entry:

* the text is cut at the first string terminator and decoded as Latin-1;
* line terminators are replaced by `\n`; leading and trailing NUL, space, tab, CR and LF are removed;
* the keyword is the text up to the first space or tab, **upper-cased**; the value is the rest,
  trimmed;
* a repeated keyword is kept, with its values joined by a line feed.

All keywords are stored with the record or trace header (`DecodedRecord.Keywords`,
`TraceHeader.Keywords`) and are available in the application's trace and record details.

### 6.2 Numeric values

`FirstNumber` and `Numbers` split a value at spaces, tabs, commas, semicolons and line feeds and parse
leading tokens as invariant-culture floating-point numbers, stopping at the first token that is not
a finite number. Integer keywords (`TryInt`) must hold an integral value.

### 6.3 Keywords used

File descriptor (record level):

| Keyword | Use |
|---|---|
| `ACQUISITION_DATE` | Acquisition date. Accepted formats: `d/MMM/yyyy`, `dd/MMM/yyyy`, `d/M/yyyy`, `dd/MM/yyyy`, `yyyy-MM-dd`, `d-MMM-yyyy`, `MM/dd/yyyy` (invariant culture). If none matches, the record has no acquisition time. |
| `ACQUISITION_TIME` | Time of day, parsed as a .NET `TimeSpan` (e.g. `14:05:30`); used only if within 0–24 h. The resulting time is stored with a UTC offset of zero (no time-zone information is inferred). |
| `SHOT_SEQUENCE_NUMBER` | Field record number of the record. |
| `SOURCE_LOCATION` | Fallback source position (first number) when the first trace has none. |

Trace descriptor:

| Keyword | Use |
|---|---|
| `SAMPLE_INTERVAL` | **Required.** Sample interval in seconds; must be positive and finite. Missing: "… has no SAMPLE_INTERVAL; the time axis cannot be established." |
| `DELAY` | Record time of the first sample, seconds (default 0). May be negative (pre-trigger). |
| `CHANNEL_NUMBER` | Channel number (default: 1-based position of the trace in the file). |
| `DESCALING_FACTOR` | Sample multiplier (section 5.2). |
| `STACK` | Number of stacked impacts, stored as `TraceHeader.Stack`. |
| `RECEIVER_LOCATION` | First number = receiver x (m); third number, if present, = receiver elevation. |
| `SOURCE_LOCATION` | First number = source x (m). The first trace's value becomes the record's source position. |

Other keywords (for example `INSTRUMENT`, `NOTE`) are kept but not interpreted.

### 6.4 Resulting record

A SEG-2 file always gives exactly one record (`DecodedFile.Records` has one element) with the format
description `SEG-2 rev R, little-endian` or `… big-endian`.

---

## 7. Errors

All failures raise `SeismicFormatException` with a message written for users and, where useful, a
technical detail (byte offsets, field values) for "Show details". Reader errors:

| Condition | Message (abbreviated) |
|---|---|
| File shorter than 32 bytes | is too short to be a SEG-2 file |
| First two bytes not `55 3A`/`3A 55` | is not a SEG-2 file (the file descriptor block identifier is missing) |
| String terminator size not 1 or 2 | has an invalid SEG-2 header (string terminator size N) |
| Line terminator size > 2 | has an invalid SEG-2 header (line terminator size N) |
| N = 0 | contains no traces |
| 4·N > M | inconsistent SEG-2 header: N traces but room for only M/4 trace pointers |
| Pointer table beyond end of file | is truncated: the trace pointer table extends beyond the end of the file |
| Trace pointer outside the file | The file is truncated or damaged: trace k … points outside the file |
| Trace descriptor id ≠ 0x4422 | does not start with a SEG-2 trace descriptor |
| Descriptor size < 32 or beyond the file | the descriptor of trace k has an invalid size |
| Unsupported data format code | uses an unsupported sample encoding (data format code N) |
| Code 3 with sample count not a multiple of 4 | uses the 20-bit encoding with a sample count … |
| Declared samples exceed the data block size | declares N samples but its data block holds only B bytes |
| Samples beyond the end of the file | The file is truncated: the samples of trace k extend beyond the end of the file |
| `SAMPLE_INTERVAL` missing / not positive or finite | has no SAMPLE_INTERVAL … / has an invalid sample interval |
| Non-finite samples | contains invalid (non-finite) sample values |

The golden files `truncated.sg2`, `bad_descriptor.sg2`, `unsupported_code.sg2` (code 9),
`no_sample_interval.sg2` and `not_seg2.sg2` exercise these paths
(`Malformed_files_fail_gracefully_with_a_user_message`).

---

## 8. Import validation and fixes

Before anything is added to a project, the import preview reads and hashes every file
(`ImportInspector.ReadFilesAsync`, SHA-256 of the file bytes) and runs `ImportInspector.Validate`.
These checks apply to SEG-2 and SEG-Y alike. A fix is only a proposal; nothing is applied unless the
user approves it.

| Code | Severity | Condition | Proposed fix |
|---|---|---|---|
| `unreadable` | Error | The file could not be opened, its format was not recognised, or the reader raised an error | Skip this file |
| `duplicate-file` | Warning | Byte-identical (same SHA-256) to another file in the batch | Skip the duplicate |
| `already-imported` | Warning | Same SHA-256 as a file already in the project | Skip this file |
| `duplicate-record` | Warning | A record has exactly the same samples (SHA-256 of all trace samples) as a record in a different file | Skip this file |
| `mixed-sample-interval` | Warning | One file contains traces with different sample intervals | – |
| `sample-interval-mismatch` | Warning | The file's sample interval differs from the majority interval of the batch (tolerance 1e-12 s); the message gives both rates, e.g. "(2 kHz) from the other records (4 kHz)" | – |
| `sample-count-mismatch` | Information | The file has record lengths other than the majority sample count | – |
| `duplicate-channel` | Error | A channel number appears more than once in a record | – |
| `missing-channels` | Warning | Channel numbers between 1 and the highest channel are absent | – |
| `fewer-traces` | Warning | No gaps, but fewer traces than the majority of records | – |
| `no-source-location` | Information | The record has no source position | – |
| `dead-trace` | Warning | All samples zero or all equal | Do not import this trace |
| `clipped-trace` | Warning | At least max(5, n/200) samples within 1e-9 (relative) of the maximum absolute amplitude | – |
| `suspicious-delay` | Warning | \|delay\| longer than the record (sample count × interval) | – |
| `acquisition-order` | Information | A record was acquired before the preceding one in the file list (reported once) | – |
| `repeated-source` | Information | Several records share a source position (rounded to 1 mm); they can be stacked or paired as SH impacts | – |

An approved "Skip this file" removes the file from the import. An approved "Do not import this
trace" imports the trace but marks it **excluded** (a `TraceAdjustment` with `Excluded = true` and
the note "Skipped at import (approved correction)"), so the decision is visible and reversible
(`ImportService.Plan`). Files already in the project and duplicate hashes are never imported twice.

### 8.1 How header values are used at import

`ImportService.Plan`:

* **Records**: one `SeismicRecord` per decoded record, labelled with the file name (multi-record
  files: file name plus `#` and the field record number).
* **Spreads**: if every receiver channel has a receiver x, and the spacing between consecutive
  channels is constant (within 0.1 % of the median spacing), a regular spread is created from the
  first position, the median spacing and its direction. Irregular recorded positions are kept as
  per-channel position overrides. Without positions, a regular spread from 0 m with the profile or
  default spacing (1 m if none) is assumed, and a note asks the user to check it in Geometry. One
  spread is created per distinct layout (roll-along).
* **Topography**: if the line has none and at least two receivers with different elevations are
  recorded, topography is created from the receiver elevations.
* **Shot position**: `SourceX` of the record. Records without it become gathers that are excluded
  until a position is entered in Geometry.
* **Grouping**: separate gathers, stacking of records at the same position, or SH opposite-impact
  pairing by the sign of the mean zero-lag correlation (a labelled suggestion).

---

## 9. Writing SEG-2 (`Seg2Writer`)

`Seg2Writer.Write(fileKeywords, traces, formatCode = 4, littleEndian = true)` returns the file as a
byte array. It is used by the synthetic survey generator (`src/RefractionStudio.Synthetics/SurveyGenerator.cs`),
the sample project builder (`SampleProjectBuilder.cs`) and the tests. The desktop application does
not offer a SEG-2 export command.

Layout written:

* **File descriptor**: id 0x3A55, revision 1, M = 4·N (no spare pointers), N = number of traces
  (1–65535), string terminator size 1 with NUL, line terminator size 1 with LF, bytes 14–31 zero.
* **Trace pointer table**, followed by the **file strings**, padded with zeros to a multiple of
  4 bytes.
* **Per trace**: a descriptor block (id 0x4422, block size = 32 + strings, padded to a multiple of
  4; data block size = number of bytes of the encoded samples; sample count; format code; bytes
  13–31 zero; strings), then the samples, then zero padding to a multiple of 4 bytes.
* **Strings**: each entry is `KEY value` (or `KEY` alone if the value is empty), Latin-1, followed by
  one NUL; the 2-byte entry length includes the length field and the terminator. Entries are sorted
  by keyword (ordinal), and each list ends with two zero bytes.
* `CHANNEL_NUMBER` is added (1-based trace index) when missing. `SAMPLE_INTERVAL` is required; a
  trace without it raises `ArgumentException`.

Sample encoding:

| Code | Rule |
|---|---|
| 1, 2 | Round to nearest, clamp to the integer range. |
| 3 | For each sample, the smallest exponent e ≤ 15 such that \|round(v)\|/2^e ≤ 32767; mantissa `round(v/2^e)` clamped to ±32767; a negative mantissa is stored in one's complement (m − 1). The exponent is chosen **per sample**. Requires a multiple of 4 samples. |
| 4 | Cast to single precision. |
| 5 | Double precision. |

Values are written as given: for integer codes the caller must already have scaled them to the
integer range and supply `DESCALING_FACTOR`. The synthetic generator scales each trace so that its
peak is 30000 (code 1) or 1e7 (codes 2 and 3) and records `DESCALING_FACTOR = 1/scale`.

Round trips are tested in `Seg2WriterRoundTripTests.Writer_output_is_read_back_exactly` for codes
1, 2 and 3 in both byte orders, code 4 little-endian and code 5 big-endian (codes 1, 2, 4, 5 exact;
code 3 within a relative quantisation of 2⁻¹⁵).

---

## 10. Golden files

`tests/RefractionStudio.Formats.Tests/Golden/make_golden.py` builds every byte of the golden files
from the published layouts with Python's `struct` module, independently of the C# writer, so the
reader is tested against a second implementation. Run `python3 make_golden.py` in that folder to
regenerate them. The files are copied to the test output directory by the test project.

| File | Content | Assertions (`GoldenFileTests.cs`) |
|---|---|---|
| `le_int16.sg2` | Little-endian, code 1, 3 traces, samples `[0, 1, −1, 1000, −1000, 32767, −32768, 12345]`; file strings `ACQUISITION_DATE 10/OCT/2026`, `ACQUISITION_TIME 14:05:30`, `INSTRUMENT GOLDEN TEST 24`, `SHOT_SEQUENCE_NUMBER 7`; trace strings `SAMPLE_INTERVAL 0.00025`, `DELAY -0.005`, `RECEIVER_LOCATION` 0/2/4, `SOURCE_LOCATION -1.0`, `STACK 3` | Channels, sample interval, delay, positions, stack, format name, samples, acquisition time 2026-10-10 14:05:30 UTC, field record 7, end time |
| `be_int32.sg2` | Big-endian, code 2, `DESCALING_FACTOR 0.5`, samples `[0, 100000, −100000, 2000000, −2000000, 7, −7, 65536]` | Samples × 0.5 |
| `le_float32.sg2` | Little-endian, code 4 | `[0.5, −0.25, (float)1.5e-3, 3.0]` |
| `be_float64.sg2` | Big-endian, code 5 | `[1e-9, −2.5, 3.25, 0.0]` |
| `le_20bit.sg2` | Little-endian, code 3, same values as `be_int32.sg2` | Exact values, format name `20-bit float` |
| `truncated.sg2` | Valid file with the last 10 bytes removed | "truncated" |
| `bad_descriptor.sg2` | Trace descriptor id 0x1234 | "trace descriptor" |
| `unsupported_code.sg2` | Data format code 9 | "data format code 9" |
| `no_sample_interval.sg2` | Trace without `SAMPLE_INTERVAL` | "SAMPLE_INTERVAL" |
| `not_seg2.sg2` | A text file | "not a SEG-2 file"; not detected by the registry |

A metadata-only read of `le_int16.sg2` returns headers with empty sample arrays
(`Metadata_read_returns_headers_without_samples`).

---

## 11. Limitations

* Only the first value of `SOURCE_LOCATION` and the first and third values of `RECEIVER_LOCATION`
  are used; y coordinates and source elevation are ignored.
* Keywords such as `RECEIVER`, `SOURCE`, `TRIGGER`, `RAW_RECORD`, `CDP_*`, `LINE_ID`,
  `NOTCH_FREQUENCY`, `LOW_CUT_FILTER`, `HIGH_CUT_FILTER`, `POLARITY` and `UNITS` are stored but not
  interpreted.
* Acquisition times are stored as UTC with no time-zone conversion.
* The whole file is held in memory while it is decoded.
