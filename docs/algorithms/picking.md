# First-break picking

This document describes how Refraction Studio picks first arrivals automatically, how it scores the
confidence of each automatic pick, how picks are transferred between traces by cross-correlation and
how the reference (shot) geophone is picked for shot-time correction.

Source files:

| File | Content |
|---|---|
| `src/RefractionStudio.Analysis/Picking/Pickers.cs` | Single-trace pickers (AIC, STA/LTA, modified energy ratio), the onset pre-window (`OnsetWindow`), signal-to-noise measurement |
| `src/RefractionStudio.Analysis/Picking/AutoPicker.cs` | Gather-level picking, search window, neighbour guidance, confidence scoring, SH polarity-divergence picker, cross-correlation transfer |
| `src/RefractionStudio.Domain/Interpretation.cs` | `PickerAlgorithm`, `PickerSettings` (defaults), `ShotTimeSettings` |
| `src/RefractionStudio.Domain/Picks.cs` | `ArrivalPick`, `ArrivalOrigin`, `PickConfidence` and its level thresholds |
| `src/RefractionStudio.Core/ProjectWorkspace.cs` | `AutoPickAsync`, `PropagatePick`, `AutoPickReferences` |
| `src/RefractionStudio.Core/Session/Edits.cs` | How automatic and correlation picks are stored (`ApplyAutomaticPicks`, `ApplyCorrelationPicks`) |
| `src/RefractionStudio.SignalProcessing/Correlation.cs` | Normalised cross-correlation |
| `tests/RefractionStudio.Analysis.Tests/PickingTests.cs` | Synthetic tests of the pickers, confidence, SH picker, transfer and shot-time correction |

---

## 1. Time axes

Picks are stored in **record time**: time relative to the trigger, before shot-time correction
(`ArrivalPick.Time`, `src/RefractionStudio.Domain/Picks.cs`). Three time axes are involved:

* *record time* — the time axis of the raw samples (`TraceHeader.Delay` is the record time of sample 0);
* *display time* — record time plus the timing shift of the processing pipeline and of the trace
  adjustment (`ProcessedTrace.TotalShift`, `src/RefractionStudio.Core/Data/GatherProvider.cs`);
* *corrected time* — record time + shot-time correction + timing shift. Every analysis method uses the
  corrected time (`Observation.CorrectedTime`, `src/RefractionStudio.Analysis/TravelTimes/Observations.cs`).

The pickers run on the *processed* traces (pipeline and trace adjustments applied) on the display time
axis. Each `GatherTrace` carries a `TimeOffset` equal to the shot-time correction of its gather, so that
corrected time = display time + `TimeOffset`. After picking, `AutoPickAsync` converts each pick back to
record time with `ProcessedTrace.ToRecordTime` (display time − `TotalShift`) before storing it.

See [signal-processing.md](signal-processing.md) for the processing steps and for why zero-phase filters
can move energy ahead of the true onset.

---

## 2. Picker settings and defaults

`PickerSettings` (`src/RefractionStudio.Domain/Interpretation.cs`) is stored per line:

| Setting | Default | Meaning |
|---|---|---|
| `Algorithm` | `Aic` (record default) | `Aic`, `StaLta`, `EnergyRatio` or `ShPolarityDivergence`. On import, a new P line takes the algorithm chosen in Settings → Processing and picking ("Default picker for new lines"); a new SH line uses `ShPolarityDivergence`. |
| `ShortWindow` | 0.002 s | STA window; energy window of the MER picker is derived from it |
| `LongWindow` | 0.02 s | LTA window |
| `MinApparentVelocity` | 150 m/s | Search window ends at offset / this velocity + margin; null = end of trace |
| `MaxApparentVelocity` | 8000 m/s | Search window starts at offset / this velocity − margin; null = time zero |
| `WindowMargin` | 0.01 s | Margin added to both ends of the velocity window |
| `UseNeighbourGuidance` | true | Re-pick inconsistent picks near the trend of consistent neighbours |

In the Picks page the values are entered in milliseconds and m/s; the view model enforces a short window
of at least 0.2 ms, a long window of at least twice the short window, a non-negative margin, and maps a
velocity ≤ 0 to "no limit" (`CurrentSettings`, `src/RefractionStudio.App/ViewModels/PicksPageViewModel.cs`).

Window lengths in samples (with Δt the sample interval of the processed trace):

```
ns = max(2, round(ShortWindow / Δt))
nl = max(ns + 1, round(LongWindow / Δt))
```

---

## 3. Search window from apparent velocities

`AutoPicker.Window` (`src/RefractionStudio.Analysis/Picking/AutoPicker.cs`) converts the apparent-velocity
limits into a sample range for each trace. With offset `|x_r − x_s|`, in corrected time:

```
t_min = offset / MaxApparentVelocity − WindowMargin     (−∞ if no maximum velocity)
t_max = offset / MinApparentVelocity + WindowMargin     (+∞ if no minimum velocity)
```

Both are converted to display time by subtracting the trace's `TimeOffset`. The window never starts
earlier than two samples before corrected time zero, and is clamped to the trace. Traces whose window
is shorter than 8 samples, traces that are flat (all samples equal) inside the window, and traces the
user has excluded are not picked.

---

## 4. Onset pre-window and sustained triggering (`OnsetWindow`)

AIC and the energy ratio respond to the largest change in their window, so a strong later arrival
(for example a surface wave) can capture the pick. Both are therefore run inside a narrow window around
the *first* energy onset, located with an STA/LTA trigger (two-step approach after Sleeman & van Eck
1999). The implementation is `OnsetWindow` in `src/RefractionStudio.Analysis/Picking/Pickers.cs`.

### 4.1 STA/LTA ratio

The ratio is computed in non-overlapping form from prefix sums. For sample i:

* long-term window: samples `[i − ns − nl + 1, i − ns]` (clipped at 0) — gives the background mean `m`
  and variance `σ²`;
* short-term window: samples `[i − ns + 1, i]` — gives the mean squared deviation from `m`;
* `ratio(i) = STA / max(σ², floor)` with `floor = 1e-12 · mean(x²) + 1e-300`;
* if the long window holds fewer than 4 samples, the ratio is 0.

The ratio is about 1 on stationary noise, rises at an onset and stays high while the long window is
still before the onset. Because the short-term energy is measured about the long-term mean, DC offsets
cancel.

### 4.2 Trigger threshold and sustain

`OnsetWindow.Trigger`:

1. The ratio is evaluated from `max(start, 1)` to the end of the window; fewer than 8 samples → no trigger.
2. `r_max` is the maximum ratio in the window. If `r_max ≤ 1.05` there is no trigger.
3. Threshold:
   * if `r_max < 8` (twice the lower bound): `0.5 · r_max` (weak records);
   * otherwise `clamp(0.1 · r_max, 4, 50)` (`TriggerThreshold = 4.0`, `MaxTriggerThreshold = 50.0`).
4. **Sustained triggering**: the trigger is the first sample at which the ratio reaches the threshold
   and stays at or above it for `max(2, ns)` consecutive samples (or until the end of the window).
   Brief noise bursts therefore do not trigger.

### 4.3 Narrow window

`OnsetWindow.Around` returns `[max(start, trigger − nl), min(end, trigger + 2·ns)]`. If no trigger is
found, the full search window is used unchanged.

---

## 5. Single-trace pickers

All pickers return a fractional sample index and a 0–1 **sharpness** value with a short description;
sharpness enters the confidence score (section 7).

### 5.1 AIC (default)

`AicPicker` — Akaike information criterion picker after Maeda (1985).

1. Flat window → no pick.
2. The window is narrowed by `OnsetWindow.Around`.
3. Inside the narrowed window of N samples (N ≥ 8 required), for k = 2 … N − 3:

   ```
   AIC(k) = k · ln var(x[0 .. k−1]) + (N − k − 1) · ln var(x[k .. N−1])
   ```

   (indices relative to the window start; variances floored at 1e-300).
4. The onset is the index of the minimum.
5. Sharpness = `(mean AIC − min AIC) / (max AIC − min AIC)`, clamped to 0–1. If the AIC curve is flat
   (max = min) there is no pick.

`AicPicker.PickInWindow` evaluates the same criterion on an exact window without pre-windowing; it is
used by neighbour guidance (section 6) and to refine the SH picker (section 5.4).

### 5.2 STA/LTA

`StaLtaPicker` — after Allen (1978), on the variance-based ratio of section 4.1.

1. The trigger of section 4.2 is computed on the whole search window (no pre-window).
2. From the trigger the onset walks back one sample at a time while the preceding ratio is still
   above 1.5 and lower than the current one (the ratio is still rising).
3. Sharpness = `1 − 1 / r_max`, clamped to 0–1.

STA/LTA is a detector: the unit test allows it 2 ms of onset error on an emergent 80 Hz wavelet,
against 4 samples for AIC (`Clean_data_is_picked_within_a_few_samples`,
`tests/RefractionStudio.Analysis.Tests/PickingTests.cs`).

### 5.3 Modified energy ratio (MER)

`EnergyRatioPicker` — after Wong et al. (2009).

1. Flat window → no pick. The window is narrowed by `OnsetWindow.Around`.
2. Energy window `L = max(3, round(2 · ShortWindow / Δt))` samples; the search is restricted to
   `[L, length − L]`, and needs at least 4 samples.
3. For each sample i, with `m_b` the mean of the L samples before i:

   ```
   before(i) = variance of x[i−L .. i−1]
   after(i)  = mean of (x[j] − m_b)² for j = i .. i+L−1
   ER(i)     = after(i) / max(before(i), eps)          eps = 1e-12 · mean(x²) + 1e-300
   MER(i)    = (ER(i) · |x_i − m_b|)³
   ```

   Energies are taken about the mean of the preceding window, which makes the picker insensitive to DC
   offsets. (The class comment writes `|x_i|`; the code uses `|x_i − m_b|`.)
4. The MER maximum tends to lie on the first peak, so the onset steps back from it while the preceding
   sample has the same sign relative to `m_b` and a smaller magnitude, i.e. to the start of that lobe.
5. Sharpness = `clamp(log10(max(ER, 1)) / 2, 0, 1)` — 1 at ER ≥ 100.

### 5.4 SH polarity-divergence picker

`ShPolarityPicker` (`src/RefractionStudio.Analysis/Picking/AutoPicker.cs`) is used for SH gathers built
from opposite impacts. A gather is treated as an SH pair when it combines at least two member records of
at least two different kinds and at least one member has negative polarity
(`GatherProvider.Build`, `src/RefractionStudio.Core/Data/GatherProvider.cs`). For each receiver channel
the provider then also supplies each member's processed trace multiplied by its polarity weight, so that
the S wave is aligned (same sign) in both. The picker uses the first two members, `a` and `b`:

```
D = (a + b) / 2    shear energy (reverses with the impact, so it is aligned after weighting)
C = (a − b) / 2    energy that does not reverse with the impact (P, noise)
```

With `w = max(2, round(ShortWindow / Δt))`, a characteristic function is computed over a centred window
of about w samples:

```
CF(i) = E_D / (E_D + E_C + floor · n)       floor = 1e-6 · (ΣD² + ΣC²) / N
```

Search loop, starting at the window start:

1. Run the STA/LTA trigger of section 4.2 on D. No trigger → no pick.
2. Average CF over the 2w samples from the trigger. If the average is at least **0.6**, the energy
   reverses with the impact direction: refine the onset with AIC on D in
   `[max(searchFrom, trigger − nl), min(end, trigger + 2w)]` (the trigger itself if AIC fails) and stop.
3. Otherwise continue the search from `trigger + w`, while the search start is before `end − w`.

Sharpness = mean CF over the 4w samples after the onset ("shear energy fraction"). Neighbour guidance
is not applied with this picker.

---

## 6. Neighbour guidance

When `UseNeighbourGuidance` is on, at least 5 traces were picked and the picker is not the SH picker,
`AutoPicker.PickGather` re-examines each pick (traces sorted by x):

1. **Neighbourhood**: up to two picked traces on each side of trace i in the sorted list, keeping only
   those on the same side of the shot. At least 3 are required. A straight line t(x) is fitted to them;
   the prediction at x_i and the RMS scatter of the neighbours about the line are computed. The original
   (first-pass) picks are used for all neighbourhoods.
2. **Tolerance** of a trace: `tol = max(3 Δt, 0.03 · |corrected time|)`.
3. If the neighbours' scatter exceeds the tolerance, the neighbours do not agree and the pick is left
   alone.
4. If the pick is within `4 · tol` of the prediction it is left alone.
5. Otherwise AIC (`PickInWindow`) is re-run in a window centred on the predicted time: from
   `center − 2·half` to `center + half` samples, with `half = ceil(3 · tol / Δt) + 4`.
6. The re-pick replaces the original only if it lies within `2 · tol` of the prediction. A reason is
   recorded: "Re-picked on the trend of consistent neighbouring traces (first estimate was … ms from it)".
   Otherwise the original pick is kept, and the neighbour-consistency factor of its confidence will be
   low.

---

## 7. Confidence score

Each automatic pick carries a `PickConfidence` (`src/RefractionStudio.Domain/Picks.cs`): a combined
score, a level, the contributing factors (name, 0–1 score, weight, explanation) and plain-language
reasons for reduced confidence. The combined score is the weighted mean of the factors that are present:

```
score = Σ (factor score · weight) / Σ weight
```

| Factor | Weight | Score | Reason added when |
|---|---|---|---|
| Signal-to-noise | 0.35 | `clamp(log10(max(SNR, 1)) / log10(30), 0, 1)` — 1 at SNR ≥ 30. If SNR cannot be measured: 0.5 | score < 0.5 (SNR below about 5.5): "Low signal-to-noise ratio"; or SNR not measurable |
| Onset sharpness | 0.20 | The picker's sharpness (section 5) | sharpness < 0.4: "The onset is weak or emergent." |
| Neighbour consistency | 0.25 | `exp(−½ (dev / tol)²)`, dev = pick − prediction from a local line through up to two picks on each side (same side of the shot, at least 2). Too few neighbours: 0.5 | score < 0.5 |
| Picker agreement (AIC/STA-LTA/MER) | 0.20 | The other two pickers are run in the same search window; mean of `exp(−½ ((t_other − t) / (2 tol))²)`. Omitted if neither returns a pick | score < 0.5 |
| Opposite-impact agreement (SH picker) | 0.20 | `clamp((r + 1) / 2, 0, 1)`, r = zero-lag Pearson correlation of the two polarity-weighted members over `max(4, round(4 · ShortWindow / Δt))` samples from the pick | r < 0.5: "The two opposite impacts do not confirm a shear arrival" |

`tol` is the tolerance of section 6. For neighbour consistency the prediction is made from the picks
after neighbour guidance.

**Signal-to-noise** (`SignalToNoise.Measure`, `src/RefractionStudio.Analysis/Picking/Pickers.cs`) is the
amplitude ratio RMS(signal) / RMS(noise):

* signal window: from the pick, `max(5 Δt, 3 · ShortWindow)` long (at least 5 samples);
* noise window: `LongWindow` long (at least 5 samples) ending just before the pick;
* not measurable (null) when fewer than 5 noise samples exist (arrival near the start of the record);
* if the noise RMS is 0 the ratio is 1e6 (or null when the signal is also 0).

**Levels** (`PickConfidence.LevelFor`):

| Level | Score |
|---|---|
| High | ≥ 0.7 (`HighThreshold`) |
| Medium | ≥ 0.4 (`MediumThreshold`) and < 0.7 |
| Low | < 0.4 |

The QC page applies its own low-SNR threshold (`QcThresholds.LowSnr`, default 3.0) to picks; see
[qc.md](qc.md).

---

## 8. Running automatic picking and storing the results

`ProjectWorkspace.AutoPickAsync` (`src/RefractionStudio.Core/ProjectWorkspace.cs`):

* picks all included gathers of a line, or a given subset (the Picks page has a "Current shot only"
  option, off by default, which limits picking, acceptance and the pick table to the shot shown);
* gathers are processed in parallel (`ProcessorCount − 1` workers, at least 1);
* times are converted to record time and applied as **one undo step** by `Edits.ApplyAutomaticPicks`.

`Edits.ApplyAutomaticPicks` (`src/RefractionStudio.Core/Session/Edits.cs`):

| Existing pick | Result |
|---|---|
| Manual or accepted suggestion (`IsManual`) | Time unchanged; the new automatic result is stored as its `Suggestion` |
| Correlation pick | Time unchanged; `Suggestion` updated |
| Automatic pick or none | Replaced by a new automatic pick (origin `Automatic`, algorithm, confidence, SNR); the existing `Included` flag and comment are kept; the suggestion is also stored |

The picker settings used are saved to the line, and the history entry records algorithm, count and the
STA/LTA windows.

---

## 9. Cross-correlation propagation

`ProjectWorkspace.PropagatePick` transfers a reviewed pick from one trace to its neighbours (Gather
viewer command `PropagateFromSelected`, `src/RefractionStudio.App/ViewModels/GatherViewerViewModel.cs`).
Defaults:

| Parameter | Default |
|---|---|
| `minCoefficient` | 0.7 |
| `windowBefore` | 0.002 s before the pick |
| `windowAfter` | 0.008 s after the pick |
| `maxLag` | 0.004 s |

Procedure:

1. The non-excluded processed traces of the gather are sorted by x; the source trace must have a pick.
2. In each direction (decreasing and increasing x), step to the next trace and correlate a window of the
   current trace (from `windowBefore` before to `windowAfter` after its pick) with the next trace over
   ±`maxLag` (`CrossCorrelationAnalyzer.Transfer`, `src/RefractionStudio.Analysis/Picking/AutoPicker.cs`).
3. The target trace is first aligned to the source time axis (start-time difference rounded to whole
   samples). Both traces must have the same sample interval.
4. The coefficient at each lag is the Pearson-normalised correlation of the two windows (each with its
   own mean removed); lags whose window leaves the target trace score 0. The best lag is refined to
   sub-sample precision by parabolic interpolation (`Correlation.Normalized`,
   `src/RefractionStudio.SignalProcessing/Correlation.cs`).
5. The transferred time is the source time plus the best lag. Propagation continues from the newly
   picked trace (a chain), and stops in that direction at the first trace whose coefficient is below
   `minCoefficient` or where the window does not fit.

Storage (`Edits.ApplyCorrelationPicks`): manual and accepted picks are never overwritten. Other traces
receive a pick with origin `Correlation`, algorithm "Cross-correlation", and a confidence with a single
factor "Cross-correlation" whose score is the coefficient clamped to 0–1 (level from the thresholds in
section 7; reason "Weak correlation with the reference trace." below 0.7). Existing SNR, inclusion,
comment and suggestion are retained. The transfer is one undo step.

---

## 10. Reference-geophone picking and shot-time correction

### 10.1 Picking the reference geophone

`ProjectWorkspace.AutoPickReferences` picks the trigger pulse on the reference (shot/spy) geophone of
each included gather:

* the first channel with role `Reference` in the gather's spread is used;
* the pick is made on the **unprocessed** trace (`ProcessedTrace.Original`: members combined and
  geometry polarity applied, no processing) at the original sample interval, because filtering —
  zero-phase filtering in particular — would move energy ahead of the true onset;
* the search window is record time −5 ms to +50 ms;
* the AIC picker is used (including its STA/LTA pre-window, with the line's picker settings);
* gathers whose existing reference pick is manual are skipped;
* results are stored as automatic picks (algorithm "AIC") in the per-gather `ShotTimeEntry`, as one
  undo step.

### 10.2 Correction

`ShotTimeCorrector` (`src/RefractionStudio.Analysis/TravelTimes/Observations.cs`) computes one correction
per gather; raw pick times are never modified.

| Method | Correction added to every pick of the gather |
|---|---|
| `None` (default) | 0 |
| `Manual` | The value entered for the shot; 0 with a note if none was entered |
| `ReferenceGeophone` | `expected − t_ref`, where `t_ref` is the reference pick (record time) |

For `ReferenceGeophone`, `expected = √(Δx² + Δz²) / ReferenceVelocity` when a reference velocity is set
and the reference channel has an explicit position (`XOverride`); Δz uses the channel's elevation
override (or the topography at its position) and the source elevation (ground elevation minus shot
depth). Otherwise the reference geophone is treated as being at the source and `expected = 0`.

If a gather has no reference pick, its correction is unavailable and all its observations are excluded
from analysis with the reason "no reference-geophone pick, so its shot time cannot be corrected"
(tested by `Missing_reference_pick_excludes_the_shot_with_a_reason`). Corrected time =
raw time + correction + timing shift (section 1). The QC check that flags direct arrivals extrapolating
away from zero time (a possible trigger problem) is described in [qc.md](qc.md); it does not apply a
correction.

---

## References

As cited in the source code:

* Maeda, N. (1985). J. Seismol. Soc. Japan 38, 365–379 — AIC picker.
* Allen, R. (1978). BSSA 68, 1521–1532 — STA/LTA.
* Wong, J., Han, L., Bancroft, J., Stewart, R. (2009). CREWES Report 21 — modified energy ratio.
* Sleeman & van Eck (1999). PEPI 113 — two-step (trigger, then onset) picking.
