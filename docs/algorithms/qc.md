# Quality control

Automatic quality checks of picks, timing, geometry and models. Sources:

* `src/RefractionStudio.Analysis/Quality/QualityControl.cs` — flags (`QcFlag`, `QcLevel`), reciprocal pairs,
  line checks (`CheckLine`) and trace checks (`CheckTraces`);
* `src/RefractionStudio.Analysis/RefractionAnalyzer.cs` — where the checks run, the QC cards and the
  overall verdict (`Summarize`);
* `src/RefractionStudio.Domain/Interpretation.cs` — `QcThresholds`;
* `src/RefractionStudio.App/ViewModels/QcAndVelocityViewModels.cs` — the QC page (`QcPageViewModel`);
* `src/RefractionStudio.Reporting/ReportTables.cs`, `ReportGenerator.cs` — QC in the report.

QC findings are advisory. They never change picks, models or results. The verdict is "an aid to review;
it does not imply compliance with any standard" (this sentence is always appended to the verdict reasons).

Related documents: [picking.md](picking.md) (SNR and confidence of picks),
[layer-assignment.md](layer-assignment.md) (segment fits), [layered-methods.md](layered-methods.md)
(layered warnings), [tomography.md](tomography.md) (residuals, staleness), [engineering.md](engineering.md)
(Vs30).

---

## 1. Levels

`QcLevel`:

| Level | Display text (`ReportTables.LevelText`) | Icon (QC page) | Meaning |
|---|---|---|---|
| `Ok` | OK | check | acceptable |
| `Review` | Review | warning | should be reviewed |
| `Problem` | Likely problem | error | likely problem |

Every finding is shown with colour, icon and text.

## 2. Thresholds

`QcThresholds` (one set per analysis scenario, `ScenarioSettings.Qc`). The application has no editor for
these values; the defaults below apply unless a project file carries other values.

| Parameter | Default | Used by |
|---|---|---|
| `ReciprocalToleranceAbsolute` | 0.002 s | `reciprocity` |
| `ReciprocalToleranceRelative` | 0.03 (of the mean reciprocal time) | `reciprocity` |
| `LowSnr` | 3.0 (amplitude ratio) | `low-snr` |
| `MaxResidualSeconds` | 0.003 s | `segment-residual`, `tomography-residual`, "Model RMS error" card |
| `AirWaveVelocityMin` | 320 m/s | `air-wave` |
| `AirWaveVelocityMax` | 360 m/s | `air-wave` |
| `TriggerInterceptTolerance` | 0.002 s | `trigger` |
| `TimeReversalTolerance` | 0.001 s | `time-reversal` |

Some limits are fixed in code: the weak-onset factor score (0.3), the air-wave minimum offset (5 m), the
tomography χ² limit (3), the segment outlier factor (3 robust standard deviations) and the escalation
factor 2 for `Problem` level.

## 3. When the checks run

`RefractionAnalyzer.AnalyzeLine` builds the observations of each line, fits the travel-time segments
(`SegmentAssigner.FitAll` with the layered regression mode), runs the selected layered method, attaches
the stored tomography result (with its staleness), finds reciprocal pairs and calls
`QualityControl.CheckLine`. This happens on every analysis update. All times used are corrected times
(raw + shot-time correction + time shift), see `src/RefractionStudio.Analysis/TravelTimes/Observations.cs`.

Location text used in messages (`Where`): `shot <label>, channel <n> (x = <receiver x> m)`.

### Reciprocal pairs

`QualityControl.ReciprocalPairs(obs, tolerance)` with
`tolerance = LayeredSettings.CoincidenceTolerance ?? typical geophone spacing / 4`. For every pair of included
gathers A, B whose shot positions differ by more than the tolerance, the included pick of A at the receiver
nearest to B's shot position (within the tolerance) and the included pick of B nearest to A's shot
position are paired. Pairs are also plotted on the travel-time graph.

---

## 4. Line checks (`QualityControl.CheckLine`)

In the table, *t* is the corrected time, *|x|* the absolute offset. Messages are quoted with
placeholders in angle brackets.

| Code | Level | Condition | Title / message |
|---|---|---|---|
| `no-picks` | Problem | the line has no observations at all (no further checks run) | "No picks" — "No first arrivals have been picked on this line." |
| `shot-time` | Problem | the shot-time correction of a gather is unavailable (reference-geophone method without a reference pick) | "Shot time not corrected" — the reason, e.g. "Shot <label>: no reference-geophone pick, so its shot time cannot be corrected." |
| `low-snr` | Review | included pick with `Snr` < `LowSnr` | "Low signal-to-noise" — "<where>: SNR <snr> is below <LowSnr>. Check the pick or apply filtering." |
| `low-confidence` | Review | included pick of origin `Automatic` whose confidence level is `Low` (score < 0.4) | "Low-confidence automatic pick" — "<where>: confidence <score>. <reasons>" |
| `weak-onset` | Review | included `Automatic` pick whose "Onset sharpness" confidence factor scores < 0.3 | "Weak onset" — "<where>: the onset is emergent; the pick may be late." |
| `air-wave` | Review | **P lines only**; included pick with *|x|* > 5 m, *t* > 0 and `AirWaveVelocityMin` ≤ *|x|/t* ≤ `AirWaveVelocityMax` | "Possible air wave" — "<where>: the pick implies <v> m/s from the shot, the speed of sound in air. Make sure the first ground arrival was picked." |
| `non-positive-time` | Problem | included pick with *t* ≤ 0 and *|x|* > 0 | "Arrival at or before the shot" — "<where>: corrected time <t> ms is not positive. Check the trigger delay and the shot-time correction." |
| `time-reversal` | Review | per gather and shot side, picks sorted by *|x|*: a pick earlier than the next-nearer pick by more than `TimeReversalTolerance` | "Arrival time decreases with offset" — "<where> arrives <Δt> ms earlier than the nearer receiver at x = <x> m. Review the picks or the geometry." |
| `segment-residual` | Review; Problem if \|r\| > 2·`MaxResidualSeconds` | segment fit (gather, side, layer) with ≥ 4 picks; residual r of a member pick from the straight-line fit satisfies \|r\| > `MaxResidualSeconds` **and** \|r\| > 3·σ_robust of the segment's residuals (σ_robust = 1.4826·MAD) | "Inconsistent with its segment" — "<where> is <r> ms from the straight-line fit of layer <n> arrivals. It may be mis-picked or assigned to the wrong layer." |
| `trigger` | Review | layer-1 segment fit with ≥ 3 picks, of a surface shot (shot depth ≤ 0), whose zero-offset intercept satisfies \|t₀\| > `TriggerInterceptTolerance` | "Possible trigger timing problem" — "Direct arrivals of shot <label> (towards decreasing/increasing x) extrapolate to <t₀> ms at zero offset. This can indicate a trigger delay; a shot-time correction may be needed (not applied automatically)." |
| `reciprocity` | Review; Problem if \|Δ\| > 2·tol | reciprocal pair with \|Δ\| > tol, where Δ = t(A at B) − t(B at A) and tol = max(`ReciprocalToleranceAbsolute`, `ReciprocalToleranceRelative` · mean of the two times) | "Reciprocal times disagree" — "Shots <A> and <B> should have equal reciprocal times but differ by <\|Δ\|> ms (tolerance <tol> ms). Check those picks and the shot-time corrections." |
| `sh-unconfirmed` | Review | **SH lines only**; included gather that is not combined by `Stack` from members including both `ShLeft` and `ShRight` impacts | "SH arrival not confirmed by opposite impacts" — "Shot <label> is not combined from left and right impacts, so the shear arrival is not confirmed by a polarity reversal." |
| `missing-picks` | Review | included gather: receiver channels of its spread that have no pick (included or excluded) and whose trace is not excluded | "Unpicked traces" — "Shot <label>: channel(s) <list> have no pick and are not excluded." |
| `layered` | Problem | the layered interpretation is unavailable | "Layered interpretation not available" — the method's reason (e.g. "No included picks.") |
| `layered-warning` | Review | one flag per warning of the layered model | "Layered interpretation note" — the warning text (see [layered-methods.md](layered-methods.md)) |
| `tomography-stale` | Review | a stored tomogram exists and is stale | "Tomography is out of date" — "Picks, geometry or settings changed after the tomogram was computed. Run the inversion again before reporting." |
| `tomography-misfit` | Review | stored tomogram with final χ² > 3 | "Tomography does not fit the picks" — "Final χ² = <χ²> (RMS <rms> ms). Review outliers or the pick uncertainty settings." |
| `tomography-residual` | Review; Problem if \|r\| > 2·`MaxResidualSeconds` | tomogram residual (observed − modelled) with \|r\| > `MaxResidualSeconds` | "Large model residual" — "<where>: observed − modelled = <r> ms in the tomogram." |

Notes:

* Picks of origin `Manual`, `AcceptedSuggestion` or `Correlation` are not tested for confidence or onset
  sharpness; only `Automatic` picks are.
* The segment check uses the regression mode of the layered settings (ordinary or robust); only picks with
  non-zero offset are used, and segments are formed from the current layer assignment.
* The `trigger` flag only suggests a correction; nothing is applied automatically.
* Air-wave detection is disabled on SH lines because shear-wave velocities overlap the speed of sound.
* Tomography staleness: see [tomography.md](tomography.md#7-outputs-storage-and-staleness).

### Trace checks (`QualityControl.CheckTraces`)

`CheckTraces(line, gather, pickTimes)` evaluates waveforms of the non-excluded traces of a gather:

| Code | Level | Condition | Title / message |
|---|---|---|---|
| `dead-trace` | Problem | trace RMS = 0 | "Dead trace" — "Channel <n> is flat. Exclude it (suggestion)." |
| `noisy-trace` | Review | trace RMS > 10 × median trace RMS | "Possible bad trace" — "Channel <n> is <k>× stronger than the median trace; it may be noisy or clipped." |
| `polarity` | Review | for an interior trace with picks on it and both neighbours: Pearson correlation over a 10 ms window starting at the picks is < −0.5 with both neighbours, while the two neighbours correlate > 0.5 with each other (window must be ≥ 4 samples) | "Possible polarity reversal" — "Channel <n> is anti-correlated with both neighbours (<c₁>, <c₂>) after its first break. The geophone may be wired in reverse; reverse its polarity if confirmed." |

`RefractionAnalyzer.AnalyzeLine` runs these checks for every included gather with a finite shot position
when a waveform source is set (`RefractionAnalyzer.Traces`, an `ITraceDataSource`). The workspace sets it
to `WorkspaceTraceSource` (`src/RefractionStudio.Core/Data/WorkspaceTraceSource.cs`, assigned in
`ProjectWorkspace`), which supplies the **processed** gather from the shared `GatherProvider` cache and the
gather's picks converted to the processed (display) time base. If a gather's raw data cannot be read, the
source returns nothing for it and the other checks still run. The resulting flags are appended to the
line's findings, so they appear on the QC page, in the report and as trace markers in gather plots
(`PlotModels.Gather` in `src/RefractionStudio.Reporting/PlotModels.cs`). Without a waveform source (for
example `new RefractionAnalyzer()` used directly) the trace checks are skipped. Flat (dead) traces are
also reported at import time by the import inspection (`dead-trace` issue in
`src/RefractionStudio.Formats/ImportInspection.cs`; see the file-format documents).

---

## 5. QC cards

`RefractionAnalyzer.Summarize` builds the dashboard cards in this order:

| Card | Value | Level | Detail |
|---|---|---|---|
| Imported records | number of records | Problem if none, else OK | "<n> file(s), <m> traces." |
| Shot gathers | included gathers over all lines | Problem if 0, else OK | "<n> excluded." |
| Missing measurements | number of `missing-picks` flags (shots with unpicked traces) | OK if 0, else Review | "Every receiver trace is picked or excluded." / "<n> shot(s) have unpicked traces." |
| P picks accepted / SH picks accepted (one per wave type that has a line) | "<accepted> / <included>", accepted = included picks of origin `Manual` or `AcceptedSuggestion` | Problem if the wave type has no picks; Review if any included pick is not accepted (origin `Automatic` or `Correlation`); else OK | "No picks." / "<n> automatic pick(s) not yet reviewed." |
| Low-confidence picks | included `Automatic` picks with level Low | OK if 0, else Review | "Automatic picks with low confidence still included." |
| Trigger corrections | gathers with a non-zero correction from a method other than None | Problem if any gather's correction is unavailable, else OK | "<n> shot(s) could not be corrected." / "Shot-time corrections applied." |
| Excluded measurements | picks not included | always OK | "Excluded picks remain visible and auditable." |
| Depth coverage | maximum depth of investigation of all tomograms; if none, deepest layered refractor point; else "—" | OK if a value exists, else Review | "Deepest ray-covered cell of the tomograms." / "Deepest interpreted refractor." |
| Vs30 availability | "<available> / <positions>" or "Not available" | Review if not available or any value is EXTRAPOLATED, else OK | the reason / "Some values are EXTRAPOLATED." / "Computed from measured depth." |
| Model RMS error | largest per-line RMS (tomogram final RMS; otherwise the first refractor RMS of the layered model) | Review if no value or above `MaxResidualSeconds`, else OK | "Largest RMS travel-time misfit of the current models." |

## 6. Verdict

`QualityVerdict` is decided in this order:

1. **Incomplete** — no survey line ("No survey line has been set up.") or no observations ("No first arrivals
   have been picked.").
2. **Incomplete** — no line has a layered model or a tomogram ("No velocity model has been computed yet.").
3. **Review recommended** — at least one `Problem` flag, or more `Review` flags than
   `max(3, N/10)` where N is the number of observations (all lines, integer division). Reasons:
   "<p> likely problem(s) were found." and/or "<r> item(s) should be reviewed."
4. **Good** — otherwise; reason "No quality issues were detected." or "Only <r> minor item(s) to review."

The reason "This summary is an aid to review; it does not imply compliance with any standard." is always
appended. Display texts (`ReportGenerator.VerdictText`): "Good", "Review recommended", "Incomplete".

## 7. Presentation

* **QC page** (`QcPageViewModel`): verdict with icon and reasons, the cards, and the findings list ordered by
  level (Problem first), limited to 1500 rows, with the filters "All findings", "Likely problems", "To
  review". Double-clicking a finding, or the **Show in the data** button
  (`src/RefractionStudio.App/Views/QcPageView.axaml(.cs)`), selects its line, gather and channel and
  navigates to: the P or SH picks page
  for pick-specific findings; the Velocity model page for `tomography*` and `layered*` codes; the Processing
  page for `shot-time`; otherwise the Waveforms page.
* **Report** (`ReportGenerator`): "Analysis quality" callout with the reasons, a "Quality summary" table of the
  cards (Item, Value, Status, Detail) and a "Quality control findings" table (Status, Finding, Line,
  Details). See `docs/REPORTING.md`.
* **Gather plot**: trace markers take the worst level of trace-level findings (codes `dead-trace`,
  `noisy-trace`, `polarity`) of that channel; see the note in Section 4.
