# Testing

All automated tests use xUnit v3 (`xunit.v3` 3.2.2, `Microsoft.NET.Test.Sdk` 18.10.1). Each test
project is an executable (`OutputType` `Exe`), as xUnit v3 requires, and runs with `dotnet test`.
The scientific validation report (`validate` CLI command) complements the unit tests; see
[SCIENTIFIC-VALIDATION.md](SCIENTIFIC-VALIDATION.md).

## 1. Test projects

| Project | Under test | Needs | Tests (count on the build host) |
|---|---|---|---|
| `tests/RefractionStudio.SignalProcessing.Tests` | `src/RefractionStudio.SignalProcessing` | — | 32 |
| `tests/RefractionStudio.Formats.Tests` | `src/RefractionStudio.Formats` | golden files | 25 |
| `tests/RefractionStudio.Analysis.Tests` | `src/RefractionStudio.Analysis` (+ `Synthetics`) | — | 49 |
| `tests/RefractionStudio.Core.Tests` | `src/RefractionStudio.Core` (+ `Synthetics`) | — | 28 |
| `tests/RefractionStudio.Licensing.Tests` | `src/RefractionStudio.Licensing` | — | 72 |
| `tests/RefractionStudio.Reporting.Tests` | `src/RefractionStudio.Reporting` (+ `Core`) | `samples/Refraction-Studio-Sample.rfsx` | 7 |
| `tests/RefractionStudio.App.Tests` | `src/RefractionStudio.App` (headless) | `samples/Refraction-Studio-Sample.rfsx` | 7 (6 when last run; `ThemeTests` was added afterwards) |
| `tests/RefractionStudio.Server.Tests` | `server/*` | **Docker** | not run on the build host |

The counts are from runs on the build host on 2026-10-11 (all passed). The test tree was being
extended at the time of writing, so the numbers are indicative.

There are no dedicated test projects for `Domain`, `Persistence`, `Visualization`, `Infrastructure`
or `Synthetics`; they are exercised through the projects above (for example, `.rfsx` save/load and
recovery in `Core.Tests`, renderers through the report and UI tests).

### SignalProcessing.Tests (`FilterTests.cs`)

* `FftTests` — impulse gives a flat spectrum, forward/inverse round trip, a sine lands in its bin,
  non-power-of-two lengths are rejected.
* `ButterworthTests` — magnitude matches the analytic Butterworth response for each filter type and
  order; low-pass has unit DC gain and high-pass blocks DC; a corner above Nyquist is rejected;
  `filtfilt` preserves phase and squares the magnitude and has no start-up transient on a constant;
  the causal filter equals a direct difference-equation implementation; the notch removes its
  frequency.
* `TraceOpsTests` — detrend, demean, taper, AGC, integer and fractional shifts, resampling of a
  band-limited sine, envelope.
* `CorrelationAndSpectrumTests` — sub-sample correlation lag, polarity detection with absolute
  correlation, dominant frequency, band-pass suggestion, robust statistics.

### Formats.Tests

* `GoldenFileTests` — reads the golden SEG-2 files (little-endian int16 with full metadata,
  big-endian int32 with `DESCALING_FACTOR`, float32/float64, packed 20-bit), metadata-only reads,
  malformed files (`truncated.sg2`, `bad_descriptor.sg2`, `unsupported_code.sg2`,
  `no_sample_interval.sg2`, `not_seg2.sg2`) failing with a user-readable `SeismicFormatException`,
  a big-endian IBM-float SEG-Y file grouped by field record, and format detection by content.
* `Seg2WriterRoundTripTests` — `Seg2Writer` output for format codes 1–5 in both byte orders is read
  back exactly; IBM-float conversion round trips.
* `ImportInspectionTests` — import inspection of a clean batch, a different sample rate, duplicates /
  dead / unreadable files (reported with fixes that are not applied automatically) and missing
  channels.

**Golden files and the independent generator.** `tests/RefractionStudio.Formats.Tests/Golden/*.sg2`
and `segy_ibm_be.sgy` are produced by `Golden/make_golden.py`, a byte-level Python implementation
written from the published layouts (SEG-2: Pullan 1990; SEG-Y rev 1). It deliberately shares no code
with the C# writer, so the reader is tested against a second implementation. The expected sample
values are in `GoldenFileTests.cs`. To regenerate (only when a new case is added):

```bash
python3 tests/RefractionStudio.Formats.Tests/Golden/make_golden.py   # writes next to the script
```

The files are copied to the test output folder by the project file (`Golden/*.sg2;Golden/*.sgy`).
Details of the formats: [file-formats/seg2.md](file-formats/seg2.md), [file-formats/segy.md](file-formats/segy.md).

### Analysis.Tests

* `PickingTests` — every picker algorithm on clean synthetic data within a few samples; confidence
  lower for noisy data with reasons; dead traces not picked and flagged; reversed-polarity channel
  suggested; SH polarity-divergence picker finds the shear onset rather than the P wave;
  cross-correlation transfers a pick to the neighbouring trace.
* `ShotTimeAndQcTests` — reference-geophone correction removes trigger errors and keeps raw times; a
  missing reference pick excludes the shot with a reason; trigger errors and reciprocity violations
  are flagged; a clean line has no problems; air-wave picks are suspected.
* `RegressionTests` — OLS exactness and standard errors against a hand calculation; Huber regression
  resists an outlier.
* `LayeredMethodTests` — segment assigner crossover; pooled direct-wave velocity; intercept-time
  (unreversed); plus–minus and GRM with XY = 0 identical and exact; GRM optimum XY and XY analysis;
  delay-time inversion; three-layer stripping; dipping refractor; velocity inversion reported;
  missing reciprocal pair explained.
* `ShortestPathSolverTests` — homogeneous medium against straight rays (3 secondary nodes: ≤ 1 %,
  5 secondary nodes: ≤ 0.3 %, never shorter than the true path), two-layer head wave, linear
  gradient against the analytic solution, topography (air cells inactive, rays stay in the ground).
* `TomographyTests` — gradient model recovered within 10 % where rays constrain it; two-layer
  tomogram and Vs30 profile; too few picks refused with a reason; forward model of the true layered
  model reproduces the picks. `DelayTimeLimitationTests` — end shots only cannot separate delays and
  the result says so.
* `Vs30Tests`, `ElasticPropertyTests`, `DensityTests` (`EngineeringTests.cs`) — harmonic Vs30,
  refusal without explicit extrapolation and labelling when extrapolated, custom interval, moduli
  against hand calculation and equivalent formulas, flags for Vs ≥ Vp and negative K/ν, missing
  density, Gardner/Brocher values, density never inferred unless estimation is selected.

Shared synthetic lines are built in `TestLines.cs` from `src/RefractionStudio.Synthetics`.

### Core.Tests

* `SessionTests` — execute/undo/redo and history, redo cleared by a new edit, no-op edits ignored,
  gestures as one undo step, dirty tracking, bounded undo history, snapshots.
* `EditTests` — manual pick keeps the automatic suggestion, pipeline step edits, trace exclusion and
  layer overrides undoable, scenarios share raw data.
* `WorkspaceTests` — import derives spread and shot positions from headers; SH records paired as
  opposite impacts with the difference cancelling P; save/load round trip preserving state, raw bytes
  and history; damaged and future-version files refused with explanations; recovery of a never-saved
  project with its staged raw data; automatic picks against truth and trigger-error correction;
  invalid processing steps skipped and reported; workflow status; tomography result surviving
  save/load and staleness detection.
* `UnitConversionTests` — exact factors, display round trips, display-only rounding.

### Licensing.Tests

* `CryptoTests` (licence keys) — key format, uniqueness over 10 000 keys, custom prefix,
  normalisation (case, spaces, missing prefix, confusable letters), check-character detection of
  single-character typos and adjacent transpositions (except the 0/Z pair the tests name), keyed
  HMAC hash and display hint.
* `TokenTests` — ES256 token issue/verify, tampering, wrong key, key rotation, wrong token type,
  malformed tokens, `alg: none`, unverified peek, key id, base64url.
* `ManifestSigningTests` — update manifests verify only with the release key; tampering is rejected.
* `DeviceFingerprintTests` and `FileLicenseStoreTests` (`DeviceAndStoreTests.cs`) — fingerprint
  stability, salt dependence, per-component hashes, normalisation, optional machine name, raw
  identifiers never appear in the device description; encrypted store round trip, tamper and copy-to-another-computer detection.
* `LicenseManagerTests` — the licence state machine against a fake API and an adjustable clock:
  trial lifecycle (14 days, expiring, expired, 3-day offline limit), activation, revocation,
  suspension, expiry, offline grace, clock rollback (> 24 h requires validation, ≤ 24 h tolerated),
  activation limit, deactivation, foreign/untrusted tokens, copied store.

See [SECURITY.md](SECURITY.md) for the behaviour these tests protect.

### Reporting.Tests

`ReportTests` (with `SampleFixture`, which opens a private copy of the sample project once):
complete PDF for the sample (`%PDF-` header, `%%EOF`, page count between 20 and 400), US-customary
and Letter reports, deselected sections shorten the report, a project without data still produces a
report, the XLSX workbook holds every table at full precision, CSV escaping, and the audit JSON with
raw-file hashes. See [REPORTING.md](REPORTING.md).

### App.Tests (headless UI)

The real application runs in Avalonia's headless platform with Skia rendering
(`UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })`), so frames can
be captured (`tests/RefractionStudio.App.Tests/TestApp.cs`). Each run uses an isolated data folder
(`REFRACTIONSTUDIO_DATA_DIR` set to a temporary folder `rs-ui-tests-<pid>`) and an in-process licence
server (`FakeLicenseApi`) that issues trial tokens signed with a key generated for the run; activation
with a key is refused and validation reports "offline".

* `MainWindowTests` — start page without a project; every navigation page renders the sample project
  with no Avalonia binding/layout warnings (`CollectingLogSink`); settings sections, help, licence
  and command-palette overlays open, render and close; editing requires a licence (a trial is started)
  and is undoable/redoable.
* `InteractionTests` — clicking a trace places a manual pick on the clicked channel near the clicked
  time, the arrow keys nudge it, and undo removes it; SEG-2 files written by the application's own
  writer from a synthetic survey (`SyntheticFiles.cs`) are inspected and imported as a new line.
* `ThemeTests` — with the dark theme, the Waveforms, Velocity model, Quality control and Engineering
  pages render the sample without Avalonia problems.

**Screenshots.** The tests save PNG frames to `screenshots/` in the test output folder, e.g.
`tests/RefractionStudio.App.Tests/bin/Debug/net10.0/screenshots/` (`UiHarness.ScreenshotDirectory`):
`00-start.png`, one `NN-<Page>.png` per navigation page (`01-Project` … `12-Report`), one
`settings-<Section>.png` per settings section, `help-topic.png`, `help-guide.png`, `license.png`,
`palette.png`, `import-inspected.png`, `import-done.png`, and `dark-<Page>.png` from the theme test. They are for visual review; the tests do
not compare them with reference images.

### Server.Tests (Docker required)

`ServerFixture` starts one PostgreSQL container (`postgres:17-alpine`, Testcontainers) for the whole
assembly, applies the EF Core migrations once, and generates ephemeral token and release signing keys,
a key pepper and a Stripe webhook secret. The licence server and admin portal run in-process through
`WebApplicationFactory`. Tests create their own customers, licences and devices and run in parallel
(`xunit.runner.json`: `parallelizeTestCollections: true`, `maxParallelThreads: 4`).

* `ActivationTests` — activation, offline window bounded by licence expiry, invalid/revoked/
  suspended/expired/foreign-product keys, seat and transfer limits, admin reset, validation refresh,
  forged tokens, blocked devices, malformed requests, raw keys never stored, append-only tables
  enforced by the database, and the desktop `LicenseManager` against the real server.
* `TrialTests`, `RateLimitTests`, `StripeWebhookTests`, `AdminPortalTests`, `UpdateFeedAndHealthTests`
  (signed update manifests, 204 when up to date, beta channel, health endpoint, HTTPS redirection,
  SemVer precedence).

Without a reachable Docker daemon the fixture cannot start the container and these tests fail. They
were not run on the build host for this document. Server details: [licensing/LICENSE-SERVER.md](licensing/LICENSE-SERVER.md).

## 2. Running the tests

```bash
source .tools/env.sh
dotnet build RefractionStudio.sln

# one project at a time (recommended; fast feedback)
dotnet test tests/RefractionStudio.SignalProcessing.Tests --no-build
dotnet test tests/RefractionStudio.Formats.Tests --no-build
dotnet test tests/RefractionStudio.Analysis.Tests --no-build
dotnet test tests/RefractionStudio.Core.Tests --no-build
dotnet test tests/RefractionStudio.Licensing.Tests --no-build
dotnet test tests/RefractionStudio.Reporting.Tests --no-build
dotnet test tests/RefractionStudio.App.Tests --no-build

# server tests (Docker daemon must be running)
dotnet test tests/RefractionStudio.Server.Tests

# everything in the solution (includes the server tests, so Docker is needed)
dotnet test RefractionStudio.sln
```

Approximate wall-clock times on the build host (`--no-build`): SignalProcessing, Formats and
Licensing about 4–5 s each; Analysis about 8 s; Core about 10 s; Reporting about 13 s; App.Tests
about 30 s.

To run a single test or class, use a filter, for example:

```bash
dotnet test tests/RefractionStudio.Analysis.Tests --filter "FullyQualifiedName~ShortestPathSolverTests"
```

The Reporting and App tests copy `samples/Refraction-Studio-Sample.rfsx` into their output folder;
after a change that affects the project format or the sample, regenerate the sample first
([BUILD.md §7](BUILD.md#7-regenerating-the-sample-project)).

## 3. Scientific validation

```bash
dotnet run --project tools/RefractionStudio.Cli -- validate --out docs/validation
```

writes `docs/validation/validation-report.md` and exits with 0 when all cases pass, 3 otherwise. The
cases, tolerances and the latest results are discussed in
[SCIENTIFIC-VALIDATION.md](SCIENTIFIC-VALIDATION.md).
