# Refraction Studio — Architecture

QuakeLogic **Refraction Studio** is a Windows desktop application for processing, picking,
interpreting, quality-checking and reporting **seismic refraction** surveys (P-wave and SH-wave),
plus a licensing back end (licence server + admin portal). This document is the contract between
the layers; read it before changing a project boundary.

It was specified from the *Downhole Studio* product brief, translated to the refraction method.
Section 12 lists how each requirement of that brief maps onto refraction, including the few that do
not apply and why.

## 1. Principles

1. **Raw data is immutable.** Imported files are embedded byte-for-byte in the project together
   with their SHA-256 and are re-decoded on open. Everything else — stacking, SH polarity
   combination, filters, gain, trace polarity, time shifts, shot-time (trigger) corrections, picks,
   layer assignments, models — is a *description* applied on top of raw samples.
2. **The scientific engine has no UI.** `Domain`, `SignalProcessing`, `Analysis`, `Formats`,
   `Persistence`, `Core`, `Visualization` and `Reporting` never reference Avalonia. Anything the UI
   shows can be computed from a loaded project in a console process
   (`RefractionAnalyzer.Analyze(project, traces)`), which is what the CLI and the scientific
   validation suite do.
3. **No silent science.** Nothing is extrapolated, interpolated, repaired, stacked, estimated or
   inverted unless the user asked for it, and every such operation is labelled in the UI, the
   project history and the report. When a value cannot be computed reliably the engine returns a
   *reason*, not a number (`Computed<T>`).
4. **One state, one undo stack.** The editable project is a single immutable `ProjectState`. Every
   edit is a pure function `ProjectState → ProjectState` executed through `ProjectSession`, which
   provides application-wide undo/redo and an append-only audit history.
5. **One rendering path.** Plots are drawn by `Visualization` onto a SkiaSharp canvas. The same
   renderer paints the screen (through an Avalonia custom draw operation), PNG, SVG, the clipboard
   and the PDF report.
6. **Licensing never touches project data.** A licence problem can at most switch the application
   into a read-only viewer; it never blocks opening or viewing a project and never modifies one.

## 2. Repository layout

```
RefractionStudio.sln
src/
  RefractionStudio.Domain           immutable records, ids, units/conversion, enums. No deps.
  RefractionStudio.SignalProcessing DSP on double[]: FFT, Butterworth SOS, filtfilt, notch, taper,
                                    detrend, AGC, balance, resampling, correlation, statistics
  RefractionStudio.Formats          ISeismicDataReader plug-ins: SEG-2 reader, SEG-Y reader (rev 0/1;
                                    rev 2 byte-order constant); SEG-2 writer used for synthetic data,
                                    the sample project and tests (the app has no SEG-2 export);
                                    import inspection and validation
  RefractionStudio.Analysis         first-break pickers + confidence, SH polarity tools, cross-
                                    correlation, shot-time correction, travel-time geometry & QC,
                                    segment (layer) assignment, intercept-time, plus–minus, GRM,
                                    delay-time (time-term) inversion, shortest-path ray tracing,
                                    regularised travel-time tomography, Vs30/VsZ, engineering
                                    properties, density estimation, QC verdict, RefractionAnalyzer
  RefractionStudio.Persistence      .rfsx container, versioned JSON + migrations, SQLite catalogue
                                    (recent projects, spread/instrument profiles, report templates),
                                    autosave/recovery store
  RefractionStudio.Core             ProjectSession (state, undo/redo, gestures, history), edits,
                                    import service, gather/trace provider + processed-trace cache,
                                    workflow status, scenarios, analysis service
  RefractionStudio.Visualization    SkiaSharp renderers: shot gather (wiggle / variable area /
                                    image), travel-time plot, velocity & property sections, ray
                                    coverage, layer model, spread diagram, spectrum, 1-D profiles,
                                    convergence; PNG/SVG/PDF export
  RefractionStudio.Reporting        PDF report (Skia PDF back end), XLSX writer, CSV, audit export
  RefractionStudio.Licensing        product-agnostic client: fingerprint, ES256 signed tokens,
                                    protected local store, clock-rollback guard, state machine,
                                    API client; shared DTO contracts also used by the server
  RefractionStudio.Infrastructure   logging, user settings, crash handling, diagnostics package,
                                    updater (signed manifests), help content
  RefractionStudio.Synthetics       deterministic synthetic refraction surveys with known answers
  RefractionStudio.App              Avalonia 12 MVVM desktop shell
server/
  RefractionStudio.Server.Data      EF Core model + migrations (PostgreSQL), key generation, token
                                    signing, licence domain services, payment + email abstractions
  RefractionStudio.LicenseServer    ASP.NET Core API: trials, activation, validation, deactivation,
                                    transfer, update feed, Stripe webhook, email outbox worker
  RefractionStudio.AdminPortal      ASP.NET Core Razor Pages internal admin portal
tools/
  RefractionStudio.Cli              batch analysis, sample project, validation report, signing keys,
                                    release-manifest signing
tests/                              xUnit v3: SignalProcessing, Formats, Analysis, Core, Licensing,
                                    Reporting, App (headless UI) and Server tests (docs/TESTING.md)
packaging/                          package_windows.py, windows-toolchain.sh; windows/: Inno Setup
                                    script, EULA, third-party notices (signing: docs/DEPLOYMENT.md)
docs/                               architecture, algorithms, file formats, validation, licensing,
                                    deployment, user guide
```

Dependency direction (arrows point at dependencies):

```
App ─► Core, Visualization, Reporting, Licensing, Infrastructure, Synthetics (sample project)
Reporting ─► Core, Visualization            Infrastructure ─► Persistence, Licensing
Core ─► Analysis, Formats, Persistence      Visualization ─► Domain (+ SkiaSharp)
Analysis ─► SignalProcessing, Domain        Formats, Persistence ─► Domain
Synthetics ─► Analysis, Formats             LicenseServer, AdminPortal ─► Server.Data ─► Licensing
```

`Licensing`, `Infrastructure`, `SignalProcessing`, the plot primitives of `Visualization` and the
layout engine of `Reporting` contain nothing refraction-specific in their public contracts, so the
other QuakeLogic studios (MASW, HVSR, Downhole, Crosshole) can share them later.

## 3. Domain model (SI internally)

Internal units: metre, second, m/s, kg/m³, pascal, hertz. Values are `double`; rounding is
display-only (`UnitFormatter`, SI or US customary). Geometry is 2-D along a line: `x` is the
in-line coordinate (m), `elevation` is positive up (m). Depths are positive down below the local
ground surface.

* `ProjectState` — `Metadata`, `Survey`, `Scenarios`, `ActiveScenarioId`, `Report`, `Snapshots`.
* `Survey` — `SourceFiles` (embedded raw files and their hashes), `Records` (one per field record:
  trace headers, sample interval, delay, keywords), `Lines`.
* `SurveyLine` — one profile for one wave type (`P` or `SH`): topography, spreads (receiver
  layout + channel map), `ShotGathers`. P and SH lines have independent geometry.
* `ShotGather` — the unit that is picked: shot position (x, elevation override, depth of a buried
  source), spread, and member records with a combine mode: single, vertical stack, or SH
  difference ((left − right)/2) with each member's polarity. Picks are keyed by
  `PickKey(gatherId, channel)`.
* `AnalysisScenario` — everything interpretive: processing pipelines, per-trace adjustments,
  shot-time correction, picks, layer (segment) assignment, layered-method settings, tomography
  settings and the cached tomography result reference, density model, Vs30 settings, velocity
  classes. Scenarios share raw data.
* `ArrivalPick` — time (record time, s), origin (manual / automatic / accepted suggestion /
  correlation), algorithm, confidence (score, level, per-factor breakdown, human-readable reasons),
  SNR, included flag, comment, and the automatic suggestion that is retained beside a manual pick.

Raw samples live outside the state in an `ITraceStore` keyed by record and channel; undo snapshots
never copy seismic data.

## 4. Editing model, undo and history

`ProjectSession.Execute(ProjectEdit)` where `ProjectEdit` = description + history entry +
`Func<ProjectState, ProjectState>`. The session pushes the previous state onto the undo stack,
raises `StateChanged`, marks the project dirty and appends an entry to the audit history (undo and
redo are themselves audited). Continuous gestures (dragging a pick, a crossover point, a GRM XY
slider) use `BeginGesture/EndGesture` so a drag is one undo step. Immutable records with
structural sharing make an undo entry a handful of references.

## 5. Processing

A `ProcessingPipeline` is an ordered list of polymorphic `ProcessingStep`s (JSON `kind`
discriminator): remove mean, linear detrend, taper, high/low/band-pass Butterworth (order,
causal or zero-phase), notch, normalise, trace balance, AGC, polarity reversal, time shift and
resample (anti-aliased, explicit). Each step can be enabled, edited, reordered and removed, all
through undoable edits. Decoded raw files are cached by `TraceStore` (bounded by a total sample
count); `GatherProvider` builds processed gathers lazily and keeps them in an LRU cache of whole
gathers keyed by a gather-level fingerprint (records, combination, pipeline, trace adjustments).
Processed samples are never written back to the raw data.

Picks are always made on, and stored in, *record time*. Display-only operations (AGC, linear
move-out reduction, clipping) never change pick times. A time-shift step shifts the displayed
samples and the pick is mapped back to record time by the shift.

## 6. Analysis engine

`RefractionAnalyzer.Analyze(ProjectState, ITomographyResultSource?, CancellationToken)` returns an
immutable `AnalysisResult` with, per line: observations (raw time, shot-time correction, corrected
time, offsets, layer index), segment fits, the layered interpretation by the selected method
(intercept-time, plus–minus, GRM, delay-time), the stored tomography result with its staleness,
reciprocal pairs and QC flags — travel-time checks (reciprocity, travel times decreasing with offset,
segment residuals, air-wave suspicion by velocity band, trigger problems, model residuals) and, when a
waveform source is set (`RefractionAnalyzer.Traces`, an `ITraceDataSource`), trace-level checks (dead,
noisy and possibly reversed traces). Across lines it adds Vs30/VsZ (or the reason it cannot be
computed), engineering properties with validity flags, the QC cards and an overall quality verdict.
Tomography runs only on request (it takes seconds) through `ITomographyEngine` with progress and
cancellation; the forward-model check of a layered interpretation is also run on request.

Algorithms sit behind interfaces: `IFirstBreakPicker`, `IAutoPicker`, `ICrossCorrelationAnalyzer`,
`IShotTimeCorrector`, `ISegmentAssigner`, `ILayeredMethod`, `ITravelTimeSolver`, `ITomographyEngine`,
`IVs30Calculator`, `IEngineeringPropertyCalculator`, `IDensityEstimator`. The SH polarity-divergence
picker is the static class `ShPolarityPicker`. Formulas, assumptions and references:
`docs/algorithms/`.

## 7. Persistence

`.rfsx` is a ZIP container written atomically (temp file in the same directory, flush, replace):

```
manifest.json         format id, schema version, app version, timestamps, SHA-256 of project.json
                      and of each raw file
project.json          ProjectState (schema version 1; ProjectMigrations refuses newer or unknown
                      versions and normalises missing arrays — the place for future migrations)
history.json          append-only audit history
snapshots/<id>.json   named snapshots (state only)
raw/<sha256>/<name>   original imported files, byte-identical (stored, not compressed)
results/<id>.bin      cached derived results (tomography grids) with input fingerprint
```

Unsaved imports are staged in the recovery directory immediately, so crash recovery can rebuild a
project that was never saved; a recovered project keeps its snapshots. User preferences (`settings.json`) and the SQLite catalogue live in
the per-user application-data folder, never inside projects.

## 8. Visualization and UI

`Visualization` exposes renderers with the shape `Render(SKCanvas, SKRect, Viewport, Model,
Style)` plus hit-testing helpers. The App wraps each renderer in a `SkiaPlotView` control that owns
the viewport (zoom / pan / fit), mouse and keyboard interaction and the export context menu.
ViewModels hold presentation state only and call `Core` services; there is no science in a
ViewModel.

Layout: a near-black QuakeLogic header (logo and product name returning to the start page;
new/open/save, undo/redo, import, export, report; command search, Guided/Expert selector, licence
state, settings and help on the right) with a thin orange rule; a left sidebar with the project name,
save state and the workflow navigation with step status; centre workspace; a trace inspector on the
right of the Waveforms and Picks pages (no application-wide inspector); status bar with messages,
progress and the version. Guided and Expert modes are presentation modes over the same project
(Expert hides the guidance box; every parameter is available in both). Themes: light, dark, system. Visual style follows the QuakeLogic house style
(`Styles/AppStyles.axaml`, tokens in `App.axaml`): Barlow text, Oswald uppercase labels, white surfaces
with hairline dividers on a light neutral background, near-black header, orange (#ED6A23) used only as
an accent (selection, focus, key marks).

## 9. Licensing

Per-computer seats. The desktop computes a privacy-preserving fingerprint (salted SHA-256 of
normalised machine identifiers, plus per-component hashes for tolerant matching) and talks to the
licence server, which issues ES256-signed tokens (trial or licence). The desktop ships only the
public key. Trials are recorded server-side per fingerprint, so reinstalling does not reset them.
Offline windows are set by the server in the signed token (30 days by default for licences, 3 days
for trials). A last-seen time kept in the encrypted, authenticated local licence store (AES-GCM, key
derived from the fingerprint) detects clock rollback, which only asks for online validation. Details:
`docs/SECURITY.md`, `docs/licensing/`.

## 10. Security

TLS only; per-IP and per-key rate limiting; licence keys stored as HMAC-SHA-256 hashes (with the
last 4 characters kept for display); secrets from environment / secret store only; Stripe
webhooks verified by signature and timestamp; admin portal with hashed passwords, lockout,
antiforgery and a full audit log. Details: `docs/SECURITY.md` (desktop) and
`docs/licensing/LICENSE-SERVER.md` (server).

## 11. Testing

xUnit v3. Numerical tests compare against hand-calculated values; algorithm tests run on
synthetic surveys with known answers; SEG-2 golden files cover encodings and malformed inputs;
server tests run against a real PostgreSQL (Testcontainers); headless Avalonia tests drive the
critical UI flows. `refraction-studio-cli validate --out docs/validation` produces the scientific
validation report (`docs/TESTING.md`, `docs/SCIENTIFIC-VALIDATION.md`).

## 12. Mapping of the product brief onto seismic refraction

| Brief requirement (downhole) | Refraction Studio |
|---|---|
| Survey geometry: borehole, source offset, receiver depths, top-down/bottom-up | Line geometry: topography, spreads (first receiver, spacing, channel direction, per-channel override), shot positions incl. off-end and buried sources, roll-along; channel-direction reversal |
| Channel mapping (vertical, H1, H2, reference) | Channel roles: seismic receiver, reference/trigger (shot) geophone, auxiliary, unused; per-channel polarity; reusable spread profiles |
| P and S geometry independent | P and SH lines are independent `SurveyLine`s |
| Waveform viewer (depth vs time) | Shot-gather viewer (receiver position vs time) with wiggle, variable area, image, overlay of original and processed traces, reduction velocity (LMO) |
| Reference geophone / trigger correction | Per-shot time correction from a reference geophone pick or a manual value; QC flags shots whose direct-wave intercept suggests a trigger problem (no correction is applied from it); raw, correction and corrected times all kept |
| P picking (AIC, STA/LTA, energy) | First-break picking on every trace: AIC, STA/LTA, modified energy ratio; confidence; manual tools |
| S-wave: opposite impacts, polarity overlay, stacking, cross-correlation | SH gathers from left/right impacts combined as the difference trace ((left − right)/2, with a flip option for the last record); original/processed overlay; polarity-divergence onset picker; cross-correlation pick transfer |
| Horizontal component rotation / hodogram | **Not applicable**: refraction spreads record one component per station (vertical for P, transverse horizontal for SH). Rotation would need 3-C stations, which the import model does not assume. Not exposed in the UI. |
| Pick table | Pick table per line: shot x, receiver x, offset, raw / correction / corrected time, layer, confidence, origin, included, comment |
| Travel-time graph (depth vs time) | Travel-time plot (position vs time) for all shots with layer assignment, segment fits, reciprocal pairs, model residuals |
| Interval / pseudo-interval / layer regression velocity | Segment regression (OLS + robust Huber), intercept-time, plus–minus, GRM, delay-time inversion, tomography |
| Layer model | Layered interpretation along the line: refractor depths per station, interfaces, layer velocities, choice of 2–4 layers, crossover editing, auto-suggested crossovers |
| Velocity profile | Velocity section (tomogram / layered), 1-D profile extracted at any position |
| Vs30 | Vs30 / VsZ at positions chosen by the user from the SH model, with depth-of-investigation checks and explicit, labelled extrapolation |
| Engineering parameters | ν, G, E, K, Vp/Vs where both P and SH models exist at the same position and density is supplied or explicitly estimated |
| ASTM D7400 | ASTM D5777 (refraction guide) is the engineering reference; no text is reproduced and no compliance is claimed |
