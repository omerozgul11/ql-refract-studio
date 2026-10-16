# Engineering results: profiles, Vs30, elastic parameters, density

Sources:

| File | Content |
|---|---|
| `src/RefractionStudio.Analysis/Engineering/Profiles.cs` | `VelocityProfile`, `ProfileInterval`, `ProfileExtractor` |
| `src/RefractionStudio.Analysis/Engineering/Vs30.cs` | `EquivalentVelocity`, `Vs30Calculator` |
| `src/RefractionStudio.Analysis/Engineering/ElasticProperties.cs` | `DensityProvider`, `EngineeringPropertyCalculator`, `ElasticProperties` |
| `src/RefractionStudio.Analysis/Engineering/Sections.cs` | `PropertySectionCalculator`, `PropertySections` |
| `src/RefractionStudio.Analysis/RefractionAnalyzer.cs` | `LineAnalysis.ProfileAt`, `RefractionAnalyzer.Engineering` (orchestration) |
| `src/RefractionStudio.Domain/Interpretation.cs` | `Vs30Settings`, `EngineeringSettings`, `DensityModel`, `DensityInterval`, `DensitySource`, `DensityEstimator` |
| `src/RefractionStudio.App/ViewModels/EngineeringResultsReportViewModels.cs` | Engineering page (`EngineeringPageViewModel`) |
| `src/RefractionStudio.Reporting/ReportTables.cs` | Vs30 and engineering tables |

All calculations use SI units internally (m, s, m/s, kg/m³, Pa). Display units (SI or US customary; moduli
in MPa, GPa, psi or ksf) are applied only for display and export (`src/RefractionStudio.Domain/Units/Units.cs`).

Every result is a `Computed<T>` (`src/RefractionStudio.Domain/Computed.cs`): either a value, optionally with
qualifying notes such as "EXTRAPOLATED" or "ESTIMATED density", or the reason it could not be computed.
No substitute number is produced for a missing one.

Related documents: [tomography.md](tomography.md) (depth of investigation),
[layered-methods.md](layered-methods.md) (refractor depths), [qc.md](qc.md) ("Vs30 availability" card).

---

## 1. Velocity profiles

A `VelocityProfile` is a list of constant-velocity depth intervals below a position x, from the surface
down, plus a **constrained depth** (the depth to which the data constrain the profile) and a source label.
`VelocityAt(depth)` returns the velocity of the interval with `Top ≤ depth < Bottom`, or null.

`LineAnalysis.ProfileAt(source, x)` selects the model (`VelocityModelSource`: `Tomography` or `Layered`,
default `Tomography` in `ScenarioSettings.Default`).

### From a tomogram (`ProfileExtractor.FromTomography`)

* Unavailable if x is outside the model ("x = … m is outside the tomography model (…–… m).") or the
  column's depth of investigation is not positive ("No rays cross the model at x = … m.").
* The column containing x is used (nearest column, no lateral interpolation). Each active cell gives one
  interval from `max(0, ground − cell top)` to `ground − cell bottom` with the cell velocity, down to the
  bottom of the grid.
* **Constrained depth** = the column's depth of investigation (DOI): the bottom of the deepest cell crossed
  by rays (see [tomography.md](tomography.md#6-coverage-depth-of-investigation-and-the-constrained-mask)).
* Source label: "Tomography". If the tomogram is stale, the profile carries the note "The tomogram is out of
  date with respect to the current picks or settings."
* If the line has no tomogram: "No tomography result exists for <line>; run the inversion first."

### From a layered model (`ProfileExtractor.FromLayered`)

* For each refractor, its depth at x is linearly interpolated between its interface points
  (`RefractorResult.DepthAt`); outside the range where the refractor was determined the profile is
  unavailable ("Refractor k is not determined at x = … m.").
* Intervals: layer 1 from 0 to the first refractor, …, and the last layer as a half-space to +∞.
  Zero-thickness intervals are skipped.
* **Constrained depth** = the deepest refractor depth at x. The half-space below it has no measured
  thickness.
* Source label: "Layered (<method>)". If the layered model is unavailable, its reason is returned.

---

## 2. Vs30 and time-averaged velocity over a depth interval

`Vs30Calculator.Calculate(profile, top, bottom, allowExtrapolation)` computes the time-averaged
(harmonic, travel-time-weighted) velocity

```
Vs(top–bottom) = (bottom − top) / Σ (h_i / V_i)
```

where `h_i` is the thickness of profile interval i inside [top, bottom]. Vs30 is the case top = 0,
bottom = 30 m. The vertical one-way travel time `Σ h_i/V_i` is also returned (`TravelTime`).

### Constrained depth and extrapolation

Let `D` be the profile's constrained depth.

* If `D ≥ bottom` (within 1e-9 m): the value is computed from the profile only and labelled **Measured**.
* If `D < bottom` and extrapolation is **not** enabled (default `Vs30Settings.AllowExtrapolation = false`), the
  value is not computed. Reason: "Insufficient measured depth for direct Vs30 calculation: the model at
  x = … m is constrained only to … m." (For an interval not starting at 0 the text reads "VsZ"; for top = 0
  the number is the bottom depth, e.g. "Vs20".)
* If `D < bottom` and extrapolation **is** enabled:
  * if `top ≥ D` the value is not computed ("The interval starts below the constrained depth (… m).");
  * otherwise the travel time is summed from `top` to `D` and the remaining depth `bottom − D` is assigned the
    velocity of the deepest profile interval used above `D`, held constant. The result carries
    `Extrapolated = true`, the extrapolation velocity, the computed note "EXTRAPOLATED", and the label
    (`EquivalentVelocity.Qualifier`)

    ```
    EXTRAPOLATED below <D> m at constant <V> m/s
    ```

    For a tomographic profile `V` is the velocity of the deepest ray-constrained cell of the column. For a
    layered profile `D` is the deepest refractor depth, so `V` is the velocity of the layer *above* the
    deepest refractor (the half-space velocity below it is not used).

Other refusals: invalid interval (`bottom ≤ top` or `top < 0`): "The depth interval is invalid."; a
non-positive velocity in the profile: "Invalid velocity in the profile at …–… m."; no interval inside the
range: "The profile has no velocities in the interval."; incomplete coverage of `[top, min(bottom, D)]`
(tolerance 1e-6 m): "The profile has gaps in the interval."

The application asks for confirmation before enabling extrapolation ("Enable Vs30 extrapolation?" — "…
Such values are estimates, not measurements, and are labelled EXTRAPOLATED in the application and the
report."). The qualifier is shown with every value on the Engineering page, in the Results summary and in
the report table "Time-averaged shear-wave velocity" (column "Status").

### Orchestration (`RefractionAnalyzer.Engineering`)

* The SH line is `EngineeringSettings.ShLine`. Unavailable reasons: no SH line selected ("Select an SH-wave
  line for Vs30 in the engineering settings."), the selected line is a P line ("<line> is a P-wave line;
  Vs30 requires shear-wave velocities."), or no positions ("Choose the positions along the line where Vs30
  is evaluated.").
* Positions: `Vs30Settings.Positions`, or `EngineeringSettings.ProfilePositions` if the former is empty.
  Vs30 is evaluated only at these discrete positions. The application writes the same position list to both
  settings.
* At each position, the SH profile from the selected model source gives Vs over `[0, TargetDepth]`
  (default 30 m) and over each custom interval (`Vs30Settings.CustomIntervals`), all with the same
  extrapolation setting. If the profile itself is unavailable, the entry carries the profile's reason.
* A stale-tomogram note on the profile is not propagated to the Vs value; staleness is reported by the QC
  flag `tomography-stale` (see [qc.md](qc.md)).

Application inputs (Engineering page): positions as a comma/semicolon/space separated list in metres;
target depth clamped to 1–200 m; custom intervals as `top-bottom` pairs separated by commas (each must
satisfy `bottom > top ≥ 0`).

Labels follow the averaging depth: with the default 30 m the quantity is called Vs30; with another depth
Z it is called VsZ (for example Vs20) on the Engineering page, in the report table "Time-averaged
shear-wave velocity" and in the exports. Depths in labels are shown in the display units.

### Custom intervals

Each custom interval `[Top, Bottom]` is computed by the same function and rules (including the constrained
depth test and extrapolation). Report label: "Vs <top>–<bottom>" with the depths in the display units.

---

## 3. Engineering profiles (Vp, Vs and elastic parameters by depth)

`RefractionAnalyzer.Engineering` builds an `EngineeringProfile` at each of
`EngineeringSettings.ProfilePositions`:

* Requires both a P line and an SH line ("Engineering properties need a P-wave line and an SH-wave line
  covering the same ground; select both in the engineering settings.") and positions ("Choose the positions
  where engineering profiles are reported.").
* At each x, the P and SH profiles are taken from the selected model source. If either is unavailable, the
  profile has no rows and notes "P model: <reason>" / "SH model: <reason>".
* Depth limit = `min(D_P, D_SH)` (the smaller constrained depth). Rows are evaluated at
  `d = step/2, 3·step/2, …` while `d < limit`, with `step = DepthStep` (default 1 m; the application clamps it to
  0.1–10 m). No row is produced below the constrained depth of either model; there is no extrapolation.
* Per row: Vp and Vs from the profiles (unavailable as "No Vp at this depth." / "No Vs at this depth."),
  density from the density model at depth d (Section 5), and the elastic properties (Section 4) when both
  velocities exist.
* If no row is produced: note "The P and SH models do not overlap in constrained depth at x = … m."
* Notes of the profiles (e.g. stale tomogram) are kept, prefixed "P model:" / "SH model:".

---

## 4. Elastic parameters

`EngineeringPropertyCalculator.Calculate(vp, vs, density)` uses isotropic linear elasticity:

```
Vp/Vs
ν = (Vp² − 2·Vs²) / [2·(Vp² − Vs²)]          Poisson's ratio
G = ρ·Vs²                                     shear modulus (Pa)
E = 2·G·(1 + ν)                               Young's modulus (Pa)
K = ρ·(Vp² − 4·Vs²/3)                         bulk modulus (Pa)
```

Physically inconsistent inputs are flagged and the affected quantity is left unavailable; inputs are never
corrected. Flags (strings in `ElasticProperties.Flags`, shown in the "Flags" column of the engineering table):

| Condition | Effect | Flag text |
|---|---|---|
| Vp ≤ 0 or Vs ≤ 0 | all quantities unavailable | "Vp and Vs must both be positive." |
| Vs ≥ Vp | ν unavailable (so E unavailable) | "Vs ≥ Vp is physically inconsistent for an elastic material; check the P and S models at this position." |
| Vp² − Vs² < 1e-6·Vp² (`DegenerateFraction`) | ν unavailable | "Vp² − Vs² is close to zero; Poisson's ratio is undefined." |
| ν < 0 (Vp/Vs < √2) | ν reported | "Negative Poisson's ratio (<ν>, Vp/Vs < √2) is unusual for soils and rocks; review the velocities." |
| ν > 0.48 | ν reported | "Poisson's ratio <ν> is typical of saturated soils, where Vp mainly reflects the pore water." |
| K < 0 (Vp/Vs < 2/√3) | K unavailable | "Negative bulk modulus (Vp/Vs = <r> < 2/√3); the velocity pair is inconsistent." |

* Vp/Vs and ν do not need density. G, E and K need density; if density is unavailable they are unavailable
  with the density reason (e.g. "Missing density: …").
* When density is estimated, G, E and K carry the note "ESTIMATED density", and the engineering table's
  "Flags" column contains "ESTIMATED density".

The unit tests (`tests/RefractionStudio.Analysis.Tests/EngineeringTests.cs`) compare the moduli with hand
calculations and with equivalent formulas, and check the flags above.

---

## 5. Density

Density is never inferred unless the user selects an estimate (`DensityModel`, default
`DensityModel.None` = source `Measured` with no intervals).

### Measured / assumed values (`DensitySource.Measured`)

`DensityProvider.DensityAt` returns the value of the first `DensityInterval` with
`TopDepth ≤ depth < BottomDepth`:

* value ≤ 0: unavailable ("The density entered for …–… m is not positive.");
* no interval covers the depth: unavailable ("Missing density: no value has been entered for … m depth.").

The basis is the interval note, or "Entered for …–… m". The application validates each row
(top ≥ 0, bottom > top, density > 0 kg/m³). A newly added row in the form is pre-filled with the previous
row's bottom as top, a 10 m thickness and the previous density (1900 kg/m³ for the first row); these are form
defaults that the user is expected to overwrite.

### Estimated from Vp (`DensitySource.Estimated`)

An estimator must be chosen explicitly (`DensityEstimator`); otherwise: "Estimated density was selected
without choosing an estimation method." Vp at the depth must be positive ("Density cannot be estimated
without Vp at this depth."). Results carry the note "ESTIMATED".

| Estimator | Formula (as implemented) | Applied for Vp | Outside the range |
|---|---|---|---|
| `Gardner1974` — Gardner, Gardner & Gregory (1974), Geophysics 39, 770–780 | ρ [kg/m³] = 310 · Vp[m/s]^0.25 | 1500–6100 m/s (`GardnerMinVp`, `GardnerMaxVp`) | unavailable: "Gardner's relation is applied only for Vp 1500–6100 m/s (sedimentary rocks); Vp here is … m/s." |
| `Brocher2005` — Brocher (2005), BSSA 95(6), eq. 1 (Nafe–Drake fit) | ρ [g/cm³] = 1.6612·Vp − 0.4721·Vp² + 0.0671·Vp³ − 0.0043·Vp⁴ + 0.000106·Vp⁵, Vp in km/s; returned × 1000 in kg/m³ | 1500–8500 m/s (`BrocherMinVp`, `BrocherMaxVp`) | unavailable: "Brocher's (2005) Nafe–Drake fit is valid for Vp 1500–8500 m/s; Vp here is … m/s." |

Reference values from the unit tests: Gardner(2000 m/s) = 2073.095 kg/m³; Brocher(2000 m/s) = 1905.392 kg/m³.

Before switching to an estimate the application asks for confirmation ("Use estimated density?" — "Density
will be estimated from Vp with <estimator>. Empirical relations were calibrated for rocks and are not valid
for most soils; values outside their range are not computed. All results will be labelled ESTIMATED.").

---

## 6. Property sections

`PropertySectionCalculator.Compute(pTomogram, shTomogram, density)` evaluates the elastic parameters on the
cells of the **P-wave tomogram grid**:

* only active P cells that are ray-constrained (`TomographyResult.IsConstrained`, i.e. within the P column's
  DOI) are used;
* Vs is sampled from the SH tomogram at the P cell centre (x, depth below ground), nearest SH cell, and only
  if that SH cell is ray-constrained (`ConstrainedVelocityAt`);
* density is taken from the density model at the cell depth (with the cell's Vp for estimates);
* arrays of Vp, Vs, Vp/Vs, ν, G, E, K and density are returned, NaN where not available; `InvalidCells`
  counts cells where ν was unavailable; `DensityEstimated` is set if any estimated density was used and the
  result carries the note "ESTIMATED density".

If no cell is constrained in both tomograms: "The P and SH tomograms do not overlap in ray-constrained
cells." Property sections are computed only from tomograms (both lines need a stored tomography result),
independent of the model-source setting.

The Engineering page shows one property at a time ("Poisson", "VpVs", "G", "E", "K", "Density"); for G, E, K
and Density the title is suffixed "— ESTIMATED density" when applicable (`PlotModels.PropertySection` in
`src/RefractionStudio.Reporting/PlotModels.cs`). The report also includes property sections
(`ReportGenerator`, see `docs/REPORTING.md`).

---

## 7. Limitations

* Profiles are 1-D at discrete positions; the tomographic profile uses the nearest grid column.
* Vs30 and engineering rows are restricted to the constrained depth; extrapolation is only available for
  Vs30/VsZ, only when enabled, and is labelled.
* The elastic relations assume isotropic, linear elastic, small-strain behaviour. P and SH models must
  describe the same ground; the software pairs them by position and depth below ground only.
* Empirical density relations were calibrated for rocks and sediments, not for most soils; measured
  densities should be preferred.
