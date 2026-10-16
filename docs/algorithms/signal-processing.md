# Signal processing

This document describes the trace-processing operations of Refraction Studio: the processing
pipeline, each step, how filters are designed and applied, the advice the application gives about
filters and first breaks, and the amplitude spectrum with its band-pass suggestion.

Sources:

| Topic | File |
|---|---|
| Step definitions (parameters, display names, JSON discriminators) | `src/RefractionStudio.Domain/Processing.cs` |
| Pipeline execution, validation and advice | `src/RefractionStudio.Core/Data/PipelineRunner.cs` |
| Gather assembly (stacking, polarity, pipeline, per-trace adjustments, cache) | `src/RefractionStudio.Core/Data/GatherProvider.cs` |
| Butterworth and notch design, causal and zero-phase filtering | `src/RefractionStudio.SignalProcessing/Iir.cs` |
| Demean, detrend, taper, normalise, balance, AGC, shift, resample, envelope, interpolation | `src/RefractionStudio.SignalProcessing/TraceOps.cs` |
| Spectrum and band-pass suggestion | `src/RefractionStudio.SignalProcessing/Spectrum.cs` |
| FFT | `src/RefractionStudio.SignalProcessing/Fft.cs` |
| Cross-correlation, statistics | `src/RefractionStudio.SignalProcessing/Correlation.cs`, `Statistics.cs` |
| Processing page (templates, defaults, spectrum panel) | `src/RefractionStudio.App/ViewModels/WaveformsAndProcessingViewModels.cs` |
| Spectrum plot and filter-response curve | `src/RefractionStudio.Reporting/PlotModels.cs` (`Spectrum`, `FilterResponse`) |
| Tests | `tests/RefractionStudio.SignalProcessing.Tests/FilterTests.cs`, `tests/RefractionStudio.Core.Tests/WorkspaceTests.cs` |

---

## 1. Principles

* **Non-destructive.** Raw samples are decoded from the embedded original files and never written
  back. Every operation in `TraceOps` returns a new array; `PipelineRunner.Run` copies its input.
* **Per line.** Each line of each analysis scenario has its own `ProcessingPipeline`
  (`LineInterpretation.Pipeline`, `src/RefractionStudio.Domain/Interpretation.cs`). Steps run in list
  order; disabled steps are skipped. Every change to the pipeline is an undoable edit.
* **Record time is preserved.** Picks are stored in record time. Steps that move samples
  (`TimeShiftStep`, and the per-trace time shift) are accounted for when converting between display
  time and record time (section 5).
* **Invalid steps are skipped, not fatal.** Each enabled step is validated against the sample
  interval in effect at that point of the pipeline. A step that fails validation is skipped and the
  reason (suffixed "Step skipped.") is reported in the gather's problem list
  (`PipelineRunner.Run`; covered by `Invalid_processing_steps_are_skipped_and_reported` in
  `tests/RefractionStudio.Core.Tests/WorkspaceTests.cs`).

### 1.1 Default pipelines

* `ProcessingPipeline.Recommended` is a single enabled `RemoveMeanStep`. `LineInterpretation.Create()`
  uses it for every new line interpretation.
* When the import page creates a new line, it replaces the pipeline with the user's
  `Settings.DefaultPipeline` (`ImportPageViewModel.cs`). By default that is also `Recommended`. In
  Settings, the option "default causal high-pass" makes it *demean + causal Butterworth high-pass,
  order 2, at the configured corner* (default 10 Hz) (`SettingsViewModels.cs`, `Apply`).
* "Copy processing to all lines" (Processing page) replaces the pipeline of every other line with a
  copy of the current one, after a confirmation, as one undoable gesture.

### 1.2 Step templates in the Processing page

New steps are created with these parameters (`ProcessingPageViewModel` constructor):

| Template | Step | Initial parameters |
|---|---|---|
| Remove DC offset (demean) | `RemoveMeanStep` | – |
| Linear detrend | `DetrendStep` | – |
| Taper | `TaperStep` | 5 % each end, Hann |
| High-pass (Butterworth) | `HighPassStep` | 10 Hz, order 2, causal (default selection) |
| Low-pass (Butterworth) | `LowPassStep` | 250 Hz, order 4, zero-phase |
| Band-pass (Butterworth) | `BandPassStep` | 10–250 Hz, order 2, causal; if a suggested band exists, the suggestion is used instead (section 7.3) |
| Notch (power line) | `NotchStep` | 50 Hz, Q = 30, 3 harmonics |
| Normalize | `NormalizeStep` | Peak |
| Trace balance | `BalanceStep` | window 0–100 ms |
| AGC (display aid) | `AgcStep` | window 50 ms |
| Polarity reversal | `PolarityStep` | – |
| Time shift | `TimeShiftStep` | 0 ms |
| Resample | `ResampleStep` | 0.25 ms (4 kHz) |

Parameters are edited as text and parsed with the invariant culture (decimal point). Times are
entered in milliseconds and the taper length in percent; they are stored in seconds and as a
fraction. Before a change is applied, the edited step is validated with `PipelineRunner.Validate`
against the original sample interval of the displayed gather; a failing step is not applied and a
warning dialog lists the problems.

---

## 2. Order of operations for a displayed gather

`GatherProvider.Build` produces each `ProcessedTrace` as follows:

1. **Member combination.** A gather has one or more member records (`ShotGather.Members`). With
   `CombineMode.Single` only the first member is used. Otherwise each member trace is multiplied by
   `member polarity × channel geometry sign` (−1 when the spread channel has `InvertPolarity`) and
   the members are averaged: `combined[i] = Σ_m sign_m · raw_m[i] / M`. For an SH pair (two or more
   members of different source kinds, at least one with polarity −1) this is the polarity-corrected
   difference `(left − right)/2`. Members that differ in sample count, sample interval (tolerance
   1e-12 s) or delay (1e-9 s) are not combined; the channel is not shown and a problem is reported.
   Channels with role `Unused` or `Auxiliary` are skipped.
2. **Pipeline.** `PipelineRunner.Run(combined, dt, delay, pipeline)`.
3. **Per-trace gain and polarity** (`TraceAdjustment.Gain`, `InvertPolarity`): multiplication by
   `Gain · (±1)`.
4. **Per-trace time shift** (`TraceAdjustment.TimeShift`): `TraceOps.Shift` (section 4.10).

For SH pairs, each member is additionally processed separately (steps 2–4) so that the
polarity-divergence picker can compare the two impact directions.

Processed gathers are cached in a least-recently-used cache of 64 gathers. The cache key is a text
fingerprint of everything that affects the result: gather members and combination mode, shot
position, spread layout and channel roles/polarities/overrides, `ProcessingPipeline.Fingerprint()`
(type name and sorted parameters of every enabled step), the trace adjustments of the gather, and
the shot-time correction. Decoded raw files are cached separately in `TraceStore` (bounded at
40 000 000 samples).

The automatic pickers work on the **processed** traces (`ProcessedGather.ToGatherData()` uses
`Processed`), and their picks are converted back to record time
(`ProjectWorkspace.AutoPickAsync`). This is why the choice of filters matters for first breaks
(section 6).

---

## 3. Butterworth filters

### 3.1 Design (`IirDesign.Butterworth`)

* Low-pass or high-pass, order N = 1…10. The corner must satisfy `0 < fc < fs/2`; otherwise
  `ArgumentOutOfRangeException` (in the pipeline, validation catches this first).
* Analog prototype poles `p_k = exp(iθ_k)`, `θ_k = π/2 + π(2k+1)/(2N)`, k = 0…⌊N/2⌋−1, grouped in
  conjugate pairs, each giving a second-order section with denominator `s² − 2 Re(p_k) s + 1`.
  An odd order adds one first-order section.
* Bilinear transform with frequency pre-warping, using the `s = (z−1)/(z+1)` convention:

  ```
  W = tan(π · fc / fs)

  low-pass  2nd order:  H(s) = W² / (s² + c1·W·s + W²),   c1 = −2 Re(p_k)
  high-pass 2nd order:  H(s) = s² / (s² + c1·W·s + W²)
  low-pass  1st order:  H(s) = W / (s + W)
  high-pass 1st order:  H(s) = s / (s + W)
  ```

  Each section is normalised to `a0 = 1` and stored as a `Biquad(B0, B1, B2, A1, A2)`; the filter is
  the cascade (`SosFilter`) of these second-order sections (SOS).
* The −3 dB point of the causal filter is exactly at `fc` (pre-warping). The magnitude response is
  verified against the closed-form bilinear Butterworth response for low- and high-pass of several
  orders (`Magnitude_matches_the_Butterworth_response`); unit DC gain of the low-pass and DC
  rejection of the high-pass are tested (`Lowpass_has_unit_dc_gain_and_highpass_blocks_dc`).

### 3.2 Band-pass (`IirDesign.BandPass`)

A cascade of a Butterworth high-pass at `lowHz` and a low-pass at `highHz`, **each of the stated
order**. `lowHz < highHz` is required. The pipeline requires `0 < low < high < Nyquist`.

### 3.3 Causal filtering (`SosFilter.Filter`)

Each section is run in transposed direct form II with zero initial state, sections in cascade:

```
y[i]  = B0·x[i] + z1
z1    = B1·x[i] − A1·y[i] + z2
z2    = B2·x[i] − A2·y[i]
```

Causal filtering cannot place energy before the true onset, but it shifts arrivals later
(group delay, section 6.2). Verified against a direct difference-equation implementation
(`Causal_filter_matches_direct_difference_equation`).

### 3.4 Zero-phase filtering (`SosFilter.FiltFilt`)

Forward–backward filtering:

1. The trace (length n) is extended at both ends by **odd reflection** about the end samples,
   `pad = min(n − 1, 3·(2·S + 1))` samples per side, where S is the number of second-order sections:
   `ext[i] = 2·x[0] − x[pad − i]` at the start and `2·x[n−1] − x[n−2−i]` at the end.
2. The extended signal is filtered forward, reversed, filtered again and reversed back. Before each
   pass every section's state is initialised to the **steady state for a constant input equal to
   its first input value** (`z1 = (G − B0)·x0`, `z2 = (B2 − A2·G)·x0`, G = section DC gain). This
   is the Gustafsson (1996) approach also used by common scientific libraries and suppresses
   start-up transients (`Filtfilt_of_a_constant_has_no_startup_transient`).
3. The padding is removed.

The result has zero phase and magnitude `|H(f)|²`: the effective order is doubled and the response
at the corner frequency is −6 dB instead of −3 dB (`Filtfilt_preserves_phase_and_squares_magnitude`).

A zero-phase filter is acausal: its impulse response is symmetric in time, so filtered energy
appears **before** the true onset. See section 6.1.

---

## 4. Pipeline steps

All parameters below are the fields of the step records in `Processing.cs`. Validation rules are
from `PipelineRunner.Validate`; the Nyquist frequency is `0.5 / dt` with `dt` the sample interval in
effect at that point (it changes after a `ResampleStep`).

### 4.1 Remove DC offset (`RemoveMeanStep`, JSON `removeMean`)

`y[i] = x[i] − mean(x)` over the whole trace.

### 4.2 Linear detrend (`DetrendStep`, `detrend`)

Removes the least-squares straight line `a + b·(i − m)` with the centred abscissa `m = (n−1)/2`,
which makes the normal equations diagonal: `a = Σx/n`, `b = Σ(i−m)·x / Σ(i−m)²`. Traces with fewer
than 2 samples are only demeaned.

### 4.3 Taper (`TaperStep`, `taper`; `Fraction`, `Shape`)

`m = ⌊Fraction · n⌋` samples at each end are multiplied by a weight `w(r)`, `r = i/m`, i = 0…m−1
(mirrored at the end):

* `Hann`: `w = 0.5·(1 − cos(π r))`
* `Linear`: `w = r`

The first and last samples become zero. Nothing happens when `m < 1`. Validation: `0 ≤ Fraction ≤
0.5` ("the taper fraction must be 0–50 %").

### 4.4 High-pass / low-pass / band-pass (`highPass`, `lowPass`, `bandPass`)

Parameters: `CornerHz` (or `LowHz`, `HighHz`), `Order`, `ZeroPhase`. Design and application as in
section 3. Validation:

* High-/low-pass: `0 < corner < Nyquist`.
* Band-pass: `0 < low < high < Nyquist`.
* Order 1–10 ("the filter order must be 1–10"; the order is entered as a number and truncated to an
  integer).

### 4.5 Notch (`NotchStep`, `notch`; `FrequencyHz`, `Q`, `Harmonics`)

Second-order notch from the bilinear transform of `H(s) = (s² + ω0²)/(s² + (ω0/Q)s + ω0²)`
(R. Bristow-Johnson, "Cookbook formulae for audio EQ biquad filter coefficients"):

```
w0 = 2π f0 / fs,  α = sin(w0)/(2Q)
b = [1, −2cos w0, 1] / (1+α),   a = [1, −2cos w0, 1−α] / (1+α)
```

The −3 dB bandwidth of a single pass is `f0/Q`. The step notches `f0·h` for h = 1…`Harmonics`. Each
notch is **always applied zero-phase** (`FiltFilt`), regardless of any setting, so the attenuation
profile is squared. If a harmonic reaches or exceeds Nyquist, it and all higher harmonics are skipped
and the problem "Notch harmonic h (f Hz) is above Nyquist and was skipped." is reported.
Validation: `0 < f0 < Nyquist`, `Q > 0`, `Harmonics ≥ 1`.

Note: because the notch is zero-phase, a strong notch can also place a small amount of filtered
energy ahead of the onset; the pipeline advice (section 6.1) warns about this for every enabled notch
step.

### 4.6 Normalize (`NormalizeStep`, `normalize`; `Mode`)

Divides the trace by its peak absolute value (`Peak`) or its RMS (`Rms`). An all-zero trace is
returned unchanged.

### 4.7 Trace balance (`BalanceStep`, `balance`; `WindowStart`, `WindowEnd` in record seconds)

Scales the whole trace by `1 / RMS(window)`. The window is converted to sample indices with the
record time of sample 0 (the trace delay): `[round((start − t0)/dt), round((end − t0)/dt))`, clamped
to the trace. Nothing happens for an empty window or zero RMS. Validation: `end > start`.

### 4.8 AGC (`AgcStep`, `agc`; `WindowSeconds`)

Automatic gain control with a centred sliding RMS window of `W = max(1, round(window/dt))` samples:

```
half   = ⌊W/2⌋
rms[i] = sqrt( mean( x[j]², j = max(0, i−half) … min(n−1, i+half) ) )
y[i]   = x[i] / max(rms[i], 1e-6 · RMS(trace))
```

The window is truncated at the trace ends. The floor (10⁻⁶ of the trace RMS) stabilises silent
zones. `AgcStep.IsAmplitudeDisplayAid` is true: AGC distorts relative amplitudes. Validation: window
longer than one sample.

### 4.9 Polarity reversal (`PolarityStep`, `polarity`)

Multiplies every sample by −1. (Per-trace polarity reversal is a separate `TraceAdjustment`, and
channel polarity is set in the spread geometry.)

### 4.10 Time shift (`TimeShiftStep`, `timeShift`; `Seconds`, positive = later)

`TraceOps.Shift` on the same time axis:

* If the shift is an integer number of samples (within 1e-9 samples) samples are moved exactly.
* Otherwise each output sample is interpolated at position `i − shift/dt` with the Kaiser-windowed
  sinc interpolator (section 4.12).
* Samples shifted in from outside the trace are zero.

`TimeShiftStep.ShiftsTime` is true and `ProcessingPipeline.TotalTimeShift` sums all enabled time
shifts; this is used to map picks back to record time (section 5). The per-trace
`TraceAdjustment.TimeShift` uses the same function.

### 4.11 Resample (`ResampleStep`, `resample`; `NewSampleInterval` in seconds)

`TraceOps.Resample(x, dtOld, dtNew)`:

1. **Anti-alias filter when decimating** (`dtNew > dtOld·(1 + 1e-12)`): zero-phase (`FiltFilt`)
   8th-order Butterworth low-pass at **80 % of the new Nyquist frequency**, `0.8 · 0.5/dtNew`. Being
   zero-phase, the effective magnitude is that of a 16th-order filter, −6 dB at the corner. No
   filter is applied when the interval decreases (upsampling).
2. **Interpolation**: output sample i is the Kaiser-windowed sinc interpolation at fractional input
   position `i · dtNew/dtOld`.
3. **Length**: the duration `(n−1)·dtOld` is kept: `count = ⌊duration/dtNew + 1e-9⌋ + 1`. The start
   time (delay) is unchanged.

After the step, the pipeline continues with `dt = NewSampleInterval`, and the processed trace
reports the new `SampleInterval`; the original trace keeps its own interval
(`ProcessedTrace.OriginalSampleInterval`). Validation: `NewSampleInterval > 0`.

The anti-alias pre-filter is zero-phase and therefore acausal; the pipeline advice warns about it
(section 6.1).

### 4.12 Band-limited interpolation (`Interpolation`)

Used by time shifts and resampling:

```
Sinc(x, t)  = Σ_k x[k] · K(t − k),   k = ⌊t⌋−15 … ⌊t⌋+16   (samples outside the trace count as 0)
K(d)        = sin(πd)/(πd) · I0(β·sqrt(1 − (d/16)²)) / I0(β),   |d| < 16;  K(0) = 1;  0 otherwise
```

with half-width 16 samples and Kaiser β = 8 (about 80 dB side-lobe attenuation). `I0` is evaluated
by its power series. An integer position returns the sample itself. Positions within 16 samples of
the trace ends see zeros beyond the ends. Accuracy is tested for fractional shifts and resampling of
band-limited sines (`Fractional_shift_of_a_band_limited_signal_is_accurate`,
`Resampling_preserves_a_band_limited_sine`).

### 4.13 Other utilities (not pipeline steps)

* `TraceOps.Envelope`: magnitude of the analytic signal via an FFT Hilbert transform (DC and Nyquist
  kept, positive frequencies doubled, negative zeroed). Tested in
  `Envelope_of_a_modulated_carrier_recovers_the_modulation`; it is not used by the pipeline.
* `Correlation.Normalized`: Pearson-normalised cross-correlation over lags −L…+L with parabolic
  sub-sample refinement of the peak (optionally on |r| to detect reversed polarity). Used by the
  cross-correlation pick propagation (`AutoPicker.cs`).
* `Correlation.Pearson`: zero-lag coefficient. Used by QC (`QualityControl.cs`) and by the import
  SH pairing (`ImportService.cs`).
* `Statistics`: mean, RMS, max-abs, variance, median, MAD, robust sigma `1.4826·MAD`, linear
  percentiles.
* `Fft`: iterative radix-2 Cooley–Tukey; lengths must be powers of two; the inverse is scaled by
  1/N.

---

## 5. Display time and record time

Each `ProcessedTrace` has `TotalShift = pipeline.TotalTimeShift + adjustment.TimeShift`:

```
display time = record time + TotalShift
record time  = display time − TotalShift
```

Picks made or computed on the processed display are converted to record time before they are
stored, and drawn at `record time + TotalShift`. Other steps (filters, AGC, normalisation,
resampling) do not change the time axis. Filters can still move the *apparent* onset (section 6);
that is not compensated.

---

## 6. Filters and first breaks: the advice

`PipelineRunner.Advice` produces non-blocking advice that the Processing page lists with a "▲"
marker (validation errors are listed with "✖"). The texts are:

### 6.1 Zero-phase filters (picks become early)

Shown when any enabled high-, low- or band-pass step has `ZeroPhase = true`. Enabled notch steps and
resampling steps (whose anti-alias filter is zero-phase) also produce a warning that the filter is
zero-phase and can place energy ahead of the onset. The general text is:

> Zero-phase filtering spreads energy ahead of the true onset and can bias first-break picks early.
> Use causal filters while picking, or verify picks on unfiltered traces.

Reason: forward–backward filtering has a symmetric (two-sided) impulse response. A first break is a
step-like change from noise to signal; after zero-phase filtering, part of the filtered wavelet
precedes the onset, so a picker (or a person) sees the arrival earlier than it is. The code comment
in `PipelineRunner.Advice` puts the bias at up to about a quarter period of the low corner; the
advice text itself does not quantify it.

### 6.2 Causal low-pass group delay (picks become late)

For every enabled **causal** low-pass step, and every **causal** band-pass step (using its high
corner and order), the advice is:

> «step name» is causal: its low-pass delays arrivals by about τ ms (group delay well below fc Hz),
> so first breaks picked on it will be late by that amount.

τ is the low-frequency group delay of the analog Butterworth low-pass prototype of order N and
corner fc (`PipelineRunner.LowPassGroupDelay`):

```
τ(0) = (1 / (2π fc)) · Σ_{k=0}^{N−1} sin( π(2k+1) / (2N) )
```

Examples: order 2 → τ = 1.414/(2π fc); order 4 → τ = 2.613/(2π fc). For a causal 4th-order 250 Hz
low-pass, τ ≈ 1.7 ms; for a causal order-2 band-pass with a 250 Hz high corner, τ ≈ 0.9 ms.

This is an approximation: it is the analog value at zero frequency (the digital filter is the
bilinear transform of that prototype, close to it when fc is well below Nyquist), and for a
band-pass it ignores the phase of the high-pass part. Causal high-pass steps produce no advice.

The application recommends (Processing page guided text and the advice above) removing the DC offset
and, if surface waves obscure the first breaks, adding a **causal high-pass**, and inspecting the
spectrum before choosing filters.

### 6.3 AGC

When an AGC step is enabled:

> AGC changes relative amplitudes; use it as a display aid only. Signal-to-noise values are measured
> on the processed traces.

---

## 7. Spectrum and band-pass suggestion

### 7.1 Single-trace spectrum (`SpectrumAnalysis.Compute`)

1. At least 4 samples are required.
2. The mean is removed and a Hann window `w_i = 0.5·(1 − cos(2πi/(n−1)))` applied.
3. FFT length `M = 2 · nextPow2(n)` (zero-padded); bin spacing `Δf = 1/(M·dt)`; bins 0…M/2.
4. One-sided amplitude: `A_k = |X_k| · c_k / Σw`, `c_k = 1` at DC and Nyquist, 2 otherwise
   (window-corrected amplitude in input units).
5. **Dominant frequency**: the largest `A_k` for k ≥ 1.
6. **Band**: the spectrum is smoothed with a 5-bin moving average (±2 bins, truncated at the
   ends). Starting from the smoothed peak, the band extends while adjacent smoothed bins remain
   ≥ peak · 10^(−L/20), with L = 20 dB by default. `BandLow`/`BandHigh` are the frequencies of the
   last bins inside; the band never extends below bin 1.
7. **Spectral centroid**: `Σ f_k A_k / Σ A_k` over k ≥ 1 (unsmoothed).

### 7.2 Gather spectrum (`SpectrumAnalysis.Average`)

Each trace with at least 4 samples is first normalised to unit RMS (so strong near-shot traces do
not dominate), its spectrum computed as above, and the amplitude spectra are averaged bin by bin
(over the shortest bin count). Dominant frequency, band and centroid are then recomputed on the
averaged spectrum with the same rules, **except that no 5-bin smoothing is applied** to the
averaged spectrum before measuring the band.

The application uses the non-excluded traces with at least 8 samples:

* **Processing page**: spectrum of the *original* (unprocessed, combined) traces, with the response
  of the enabled filters overlaid, and the text "Dominant f Hz; −20 dB band a–b Hz. Suggested
  band-pass: l–h Hz."
* **Waveforms page**: spectrum of the processed or original traces, following the gather display's
  "processed" setting.

The plot (`PlotModels.Spectrum`) shows the averaged spectrum normalised to its maximum (DC
excluded), markers at the dominant frequency and the band edges, and the x-axis up to
`min(Nyquist, max(3·BandHigh, 1.5·suggested high))`.

### 7.3 Filter-response overlay (`PlotModels.FilterResponse`)

The product of the magnitude responses `|H(f)|` of all enabled high-, low-, band-pass and notch
steps at the original sample rate; zero-phase steps (and all notches) contribute `|H(f)|²`. Steps
whose design fails are omitted (they are reported by validation). Other steps (taper, AGC,
resampling, …) are not represented.

### 7.4 Band-pass suggestion (`SpectrumAnalysis.SuggestBandPass`)

From the −20 dB band of the averaged spectrum:

```
low  = max(1 Hz, BandLow / √2)                 (half an octave below)
high = min(0.8 · Nyquist, BandHigh · √2)       (half an octave above)
if high ≤ 1.5 · low:  high = min(0.8 · Nyquist, 4 · low)
```

The suggestion is never applied automatically. "Use suggested band" adds a band-pass step with the
low corner rounded to 0.1 Hz and the high corner rounded to 1 Hz, **order 2, causal**. Adding the
band-pass template also uses the suggestion when one is available. Tested in
`Band_suggestion_brackets_the_signal_band` and `Spectrum_finds_the_dominant_frequency`.

---

## 8. Validation messages (reference)

From `PipelineRunner.Validate` (the step's display name precedes each message):

| Step | Condition | Message |
|---|---|---|
| High-/low-pass | not `0 < corner < Nyquist` | the corner must be between 0 and N Hz (Nyquist). |
| Band-pass | not `0 < low < high < Nyquist` | corners must satisfy 0 < low < high < N Hz. |
| High-/low-/band-pass | order < 1 or > 10 | the filter order must be 1–10. |
| Notch | not (`0 < f < Nyquist`, `Q > 0`, `Harmonics ≥ 1`) | frequency must be below Nyquist and Q positive. |
| Taper | fraction < 0 or > 0.5 | the taper fraction must be 0–50 %. |
| AGC | window ≤ dt | the window must be longer than one sample. |
| Balance | end ≤ start | the window end must follow its start. |
| Resample | interval ≤ 0 | the sample interval must be positive. |

Only the first matching condition is reported per step (a filter with an invalid corner *and* an
invalid order reports the corner).
