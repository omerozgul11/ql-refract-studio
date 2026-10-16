# Scientific validation

Refraction Studio's numerical results are checked in two ways:

1. **The validation suite** (`tools/RefractionStudio.Cli/Validation.cs`), run with the CLI, compares
   each high-risk calculation with an independently known answer — a hand calculation, a closed-form
   solution, or a synthetic earth model with known parameters — and writes a Markdown report.
2. **Unit tests** in `tests/` cover the same calculations plus edge cases and failure modes
   ([TESTING.md](TESTING.md)).

This document summarises the suite, the most recent run and the known limitations.

## 1. Running the suite

```bash
source .tools/env.sh
dotnet run --project tools/RefractionStudio.Cli -- validate --out docs/validation
```

The command writes `docs/validation/validation-report.md`, prints `<passed>/<total> validation cases
pass` and one `FAIL …` line per failing case, and exits with **0** when every case passes and **3**
otherwise. A case passes when the result is finite and |actual − expected| ≤ tolerance.

## 2. Latest run

Run on 2026-10-11 on the build host, version 1.0.0. Full table: [validation/validation-report.md](validation/validation-report.md).

**Result: 37 of 37 cases pass (exit code 0).** An earlier run with a 0.3 % tolerance on the
homogeneous-medium ray-tracing case reported that case as failing (measured error 0.307 %); the
tolerance was set to 0.5 % after the worst direction-dependent error of the solver was measured
(0.344 %, §4).

| Group | Cases | Pass |
|---|---:|---:|
| Engineering formulas (hand calculations) | 12 | 12 |
| Ray tracing (closed-form travel times) | 3 | 3 |
| Layered methods (synthetic models with known answers) | 17 | 17 |
| Tomography and Vs30 (synthetic surveys) | 5 | 5 |

## 3. The cases

### 3.1 Engineering formulas

Inputs Vp = 1800 m/s, Vs = 400 m/s, ρ = 1900 kg/m³ through `EngineeringPropertyCalculator`
(`src/RefractionStudio.Analysis/Engineering/ElasticProperties.cs`); details in
[algorithms/engineering.md](algorithms/engineering.md).

| Case | Expected | Actual | Tolerance |
|---|---:|---:|---:|
| Poisson's ratio ν = (Vp² − 2Vs²) / (2(Vp² − Vs²)) | 0.47403 | 0.47403 (diff 0) | 1e-12 |
| Shear modulus G = ρVs² | 3.04e8 Pa | 3.04e8 Pa (diff 0) | 0.001 Pa |
| Young's modulus E = 2G(1 + ν) | 8.9621e8 Pa | 8.9621e8 Pa (diff 0) | 0.001 Pa |
| Bulk modulus K = ρ(Vp² − 4Vs²/3) | 5.7507e9 Pa | 5.7507e9 Pa (diff 0) | 0.001 Pa |
| Cross-check E = 9KG / (3K + G) | 8.9621e8 Pa | diff 1.19e-7 Pa | 0.001 Pa |
| Vs30 of 200 m/s (0–5 m), 400 m/s (5–20 m), 800 m/s below: 30 / Σ(h/V) = 30 / 0.075 | 400 m/s | 400 m/s (diff 0) | 1e-9 m/s |
| Gardner density at Vp = 2000 m/s (310·Vp^0.25) | 2073.09495 kg/m³ | diff 0 | 1e-9 |
| Brocher (2005) eq. 1 density at Vp = 2000 m/s | 1905.392 kg/m³ | diff 2.3e-13 | 1e-6 |
| 1 ft → m | 0.3048 | diff 0 | 1e-15 |
| 1 pcf → kg/m³ | 16.018463… | diff 0 | 1e-9 |
| 1 ksf → Pa | 47880.25898… | diff −7.3e-12 | 1e-6 |
| Straight-ray source–receiver distance, x = 30 m, Δz = 40 m | 50 m | diff 0 | 1e-12 |

The Vs30 calculator is called with explicit extrapolation disabled (`Calculate(profile, 0, 30, false)`).
The unit conversions use the NIST SP 811 factors.

### 3.2 Ray tracing

`ShortestPathSolver` (`src/RefractionStudio.Analysis/Tomography/ShortestPathSolver.cs`) on flat
models (`Topography.Flat`) against closed-form travel times; method in
[algorithms/tomography.md](algorithms/tomography.md).

| Case | Grid / secondary nodes | Expected | Actual | Difference | Tolerance | Result |
|---|---|---:|---:|---:|---:|---|
| Homogeneous 1500 m/s, source (0, 0), receiver at x = 80 m, 20 m below the surface; t = R/V | 1 m × 1 m, 5 | 0.054975 s | 0.055144 s | +1.689e-4 s (+0.307 %) | ±0.5 % (2.749e-4 s); 0.3 % in the recorded run | PASS at 0.5 % (FAIL at 0.3 % in the recorded run) |
| Head wave, 500 over 2000 m/s, h = 5 m, x = 100 m; t = x/V₂ + 2h√(1/V₁² − 1/V₂²) | 0.5 m × 0.5 m, 4 | 0.06936 s | 0.06940 s | +3.12e-5 s | ±1e-4 s | PASS |
| Linear gradient v = 400 + 40·z, x = 80 m; t = (2/g)·asinh(gx / 2v₀) | 0.5 m × 0.5 m, 4 | 0.10474 s | 0.10487 s | +1.38e-4 s | ±1 % (1.05e-3 s) | PASS |

All three errors are positive: a graph (network) path can only be equal to or longer than the true
ray path, so shortest-path travel times are biased slightly late.

### 3.3 Layered methods

Picks are generated from exact analytic first-arrival times of the synthetic models in
`src/RefractionStudio.Synthetics/SyntheticModels.cs` (no noise). Layer assignment uses
`SegmentAssigner.Suggest`; the methods are described in
[algorithms/layer-assignment.md](algorithms/layer-assignment.md) and
[algorithms/layered-methods.md](algorithms/layered-methods.md).

**Two layers** (`HorizontalLayersModel`: 500 m/s, 5 m, over 2000 m/s): 24 receivers at 2 m from
x = 0, shots at −24, −1, 23, 47 and 70 m; the forward/reverse pair for the reciprocal methods is −1 m
and 47 m.

| Method | V₁ (expected 500 m/s, ±0.5) | V₂ (expected 2000 m/s, ±2) | Depth positions | Max depth error (expected 5 m, ±0.01) |
|---|---|---|---:|---|
| Intercept-time | 500 (diff 0) | 2000 (diff 4.5e-13) | 2 | 8.9e-16 m |
| Plus–minus | 500 (diff 0) | 2000 (diff −1.8e-12) | 12 | 1.8e-15 m |
| GRM, XY = 2 m | 500 (diff 0) | 2000 (diff −1.8e-12) | 12 | 1.8e-15 m |
| Delay-time | 500 (diff 0) | 2000 (diff −1.1e-9) | 29 | −6.8e-12 m |

V₁ is the pooled direct-wave slope.

**Dipping refractor** (`DippingLayerModel`: 600 over 2500 m/s, vertical depth 4 m at x = 0, dip 5°,
interface deepening towards +x), shots at −1 m and 47 m, intercept-time method:

| Quantity | Expected | Actual difference | Tolerance |
|---|---:|---:|---:|
| V₂ | 2500 m/s | 0 | ±0.01 m/s |
| Vertical depth at x = −1 m | 3.91251 m | 1.3e-15 m | ±0.001 m |
| Vertical depth at x = 47 m | 8.11197 m | −1.8e-15 m | ±0.001 m |

**Three layers** (400 m/s × 3 m, 1200 m/s × 8 m, 3000 m/s half-space), 48 receivers at 2 m, shots at
−48, −1, 47 and 95 m, delay-time method with layer stripping:

| Quantity | Expected | Actual difference | Tolerance |
|---|---:|---:|---:|
| V₃ | 3000 m/s | −3.3e-9 m/s | ±3 m/s |
| Mean depth of refractor 2 (all positions) | 11 m | −1.4e-9 m | ±0.1 m |

### 3.4 Tomography and Vs30

**Gradient model** (`GradientModel` v = 400 + 40·depth m/s): exact analytic picks for 24 receivers at
2 m and shots at −24, −1, 11, 23, 35, 47 and 70 m, inverted by `TomographyEngine` with cell 1 m × 1 m,
maximum depth 16 m, at most 10 iterations, smoothing 10, 4 secondary nodes, pick error 0.1 ms + 0.2 %.
The velocity error is evaluated in cells with centre 6 m ≤ x ≤ 40 m, depth between 1 m and 0.8 × the
depth of investigation of the column, and at least 5 ray hits.

| Case | Expected | Actual | Tolerance | Result |
|---|---:|---:|---:|---|
| Median \|v/v_true − 1\| over 244 well-covered cells | 0 | 0.01207 (1.2 %) | 0.05 | PASS |
| Final RMS travel-time residual | 0 s | 2.93e-4 s (0.29 ms) | 5e-4 s | PASS |

**Sample site Vs30.** The sample project is rebuilt from scratch (`SampleProjectBuilder`: synthetic
SEG-2 records with noise, automatic SH picking, SH tomography) and the Vs30 values the analysis reports
are compared with the true Vs30 of the synthetic Vs(x, depth) model, computed by numerical integration
(30 / Σ 0.01 m / Vs at 1 cm steps). Tolerance ±5 % of the true value.

| Position | True Vs30 | Recovered Vs30 | Difference | Tolerance | Result |
|---|---:|---:|---:|---:|---|
| x = 30 m | 552.6 m/s | 571.9 m/s | +19.3 m/s (+3.5 %) | ±27.6 m/s | PASS |
| x = 70.5 m | 581.3 m/s | 599.5 m/s | +18.2 m/s (+3.1 %) | ±29.1 m/s | PASS |
| x = 110 m | 659.0 m/s | 675.7 m/s | +16.7 m/s (+2.5 %) | ±33.0 m/s | PASS |

## 4. Known limitations

* **Direction-dependent ray-tracing error.** The shortest-path network with 5 secondary nodes per cell
  edge restricts ray directions, so its error depends on the angle of the ray to the cell edges. The
  unit test `ShortestPathSolverTests.Error_over_all_ray_directions_stays_below_half_a_percent_with_five_secondary_nodes`
  sweeps receivers 80 m from the source over 0–89° and measures a worst case of 0.344 % (1 m cells);
  the validation tolerance for the homogeneous case is therefore 0.5 %. Network travel times are never
  shorter than the true ones; when observed times are inverted, the inversion compensates the late
  modelled times with slightly higher velocities (one contributor to the high Vs30 bias below).
* **Shortest-path times are biased late.** All ray-tracing differences are positive (network paths
  are never shorter than true rays); the bias falls with more secondary nodes and smaller cells.
* **Sample-site Vs30 is biased high.** At all three positions the recovered Vs30 is 2.5–3.5 % above
  the true value (within the ±5 % tolerance). The suite does not separate the contributions of
  automatic picking on noisy records, the late bias of shortest-path forward modelling, and
  regularisation (smoothing) of the tomogram.
* **Not fully independent synthetic data for the sample site.** The sample's synthetic records are
  generated with `GridSyntheticModel`, whose first-arrival times come from the same
  `ShortestPathSolver` (6 secondary nodes, 1 m cells in the sample) that the tomography uses. The
  closed-form ray-tracing cases (§3.2) are the independent check of that solver. The true Vs30, by
  contrast, is computed directly from the velocity function.
* **Layered-method cases are noise-free and geometrically simple.** Picks are exact analytic times on
  flat ground (or one planar dipping interface), so the near-zero errors show that the formulas are
  implemented correctly, not how the methods behave with noisy picks, lateral velocity changes,
  hidden layers, velocity inversions or topography. Those situations are covered qualitatively by unit
  tests (for example `Velocity_inversion_is_reported_not_hidden`,
  `End_shots_only_cannot_separate_delays_and_say_so` in `tests/RefractionStudio.Analysis.Tests`).
* **Tomography is checked on one smooth model.** The gradient case evaluates only well-covered cells
  (≥ 5 hits, above 0.8 × the depth of investigation, away from the line ends). Sharp interfaces are
  smoothed by regularisation; resolution below the depth of investigation is not assessed.
* **Elastic properties and density** are checked against their formulas only; the empirical
  Gardner and Brocher relations are not validated against measured densities (the application labels
  such densities as estimated — see [algorithms/engineering.md](algorithms/engineering.md)).
* **Picking accuracy** is not part of this suite except implicitly through the sample-site Vs30. It is
  tested in `tests/RefractionStudio.Analysis.Tests/PickingTests.cs` and
  `tests/RefractionStudio.Core.Tests/WorkspaceTests.cs` (automatic picks against truth).

## 5. Where the checks live

| What | Where |
|---|---|
| Validation suite (cases, inputs, tolerances, report writer) | `tools/RefractionStudio.Cli/Validation.cs` |
| Synthetic models with known answers | `src/RefractionStudio.Synthetics/SyntheticModels.cs`, `SurveyGenerator.cs`, `SampleProjectBuilder.cs` |
| Ray-tracing unit tests | `tests/RefractionStudio.Analysis.Tests/ShortestPathSolverTests.cs` |
| Layered-method and regression tests | `tests/RefractionStudio.Analysis.Tests/LayeredMethodTests.cs` |
| Tomography tests | `tests/RefractionStudio.Analysis.Tests/TomographyTests.cs` |
| Vs30, elastic properties, density | `tests/RefractionStudio.Analysis.Tests/EngineeringTests.cs` |
| Picking and shot-time correction | `tests/RefractionStudio.Analysis.Tests/PickingTests.cs` |
| Unit conversion | `tests/RefractionStudio.Core.Tests/WorkspaceTests.cs` (`UnitConversionTests`) |
| DSP (filters, FFT, resampling, correlation) | `tests/RefractionStudio.SignalProcessing.Tests/FilterTests.cs` |
| Latest report | `docs/validation/validation-report.md` |
