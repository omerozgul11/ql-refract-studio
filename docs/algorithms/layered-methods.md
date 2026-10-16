# Layered interpretation methods

Refraction Studio offers four layered (refractor) interpretation methods: intercept-time, plus–minus,
the generalized reciprocal method (GRM) and least-squares delay-time (time-term) inversion. All work on
the corrected first-arrival times of one line and on the layer assignment of those arrivals
([layer-assignment.md](layer-assignment.md)). Tomography is described separately in
[tomography.md](tomography.md).

Source files:

| File | Content |
|---|---|
| `src/RefractionStudio.Analysis/Layered/LayeredModel.cs` | Result types, pooled velocity, layer stripping, shared helpers |
| `src/RefractionStudio.Analysis/Layered/InterceptTimeMethod.cs` | Slope–intercept method |
| `src/RefractionStudio.Analysis/Layered/GrmMethod.cs` | GRM, XY analysis, plus–minus (`PlusMinusMethod`) |
| `src/RefractionStudio.Analysis/Layered/DelayTimeMethod.cs` | Delay-time least squares |
| `src/RefractionStudio.Analysis/TravelTimes/Observations.cs` | Observations, shot-time correction |
| `src/RefractionStudio.Analysis/TravelTimes/Segments.cs` | Segment fits |
| `src/RefractionStudio.Analysis/Numerics/LinearAlgebra.cs` | `DenseSolver.LeastSquares` (normal equations, Cholesky) |
| `src/RefractionStudio.Analysis/RefractionAnalyzer.cs` | Method selection per line |
| `src/RefractionStudio.Domain/Interpretation.cs` | `LayeredMethod`, `LayeredSettings` |
| `tests/RefractionStudio.Analysis.Tests/LayeredMethodTests.cs` | Synthetic tests of all methods |

---

## 1. Input data

### 1.1 Observations

`ObservationBuilder.Build` (`src/RefractionStudio.Analysis/TravelTimes/Observations.cs`) produces one
`Observation` per pick with:

* shot x, source elevation (ground elevation at the shot, or the shot's elevation override, minus the
  shot depth), ground elevation at the shot, shot depth;
* receiver x (from the spread layout) and receiver elevation (channel elevation override, otherwise the
  line topography at x);
* raw (record) time, shot-time correction, timing shift (processing pipeline + trace adjustment), and

  ```
  corrected time = raw time + shot-time correction + timing shift
  ```

* the layer from the layer assignment;
* whether it is included and, if not, why. An observation is excluded when the channel is not in the
  spread layout or is not a receiver channel, when the shot, trace or pick is excluded, or when the
  shot-time correction of its gather is unavailable.

Only included observations are used by the methods. If a line has no included observation, the layered
result is "No included picks." (`RefractionAnalyzer.AnalyzeLine`).

### 1.2 Shot-time correction

The correction per gather (none, manual value, or from the reference-geophone pick) is described in
[picking.md](picking.md#10-reference-geophone-picking-and-shot-time-correction). Raw times are never
modified; the observation keeps raw time, correction and corrected time side by side.

### 1.3 Settings

`LayeredSettings` (`src/RefractionStudio.Domain/Interpretation.cs`), per line:

| Setting | Default | Used by |
|---|---|---|
| `Method` | `DelayTime` | Selects the method |
| `ForwardShot`, `ReverseShot` | none | Reversed pair: intercept-time (optional), plus–minus and GRM (required). Forward = lower x |
| `GrmXy` | 0 m | GRM XY distance |
| `RefractorVelocitySegments` | 1 | Delay-time: number of lateral refractor-velocity segments (the UI clamps it to 1–20) |
| `VelocitySmoothing` | 1.0 | Delay-time: weight tying adjacent segment slownesses |
| `CoincidenceTolerance` | null = ¼ of the typical geophone spacing | Delay-time: distance within which a shot and a receiver share a delay node; also the reciprocal-pair tolerance of QC |
| `FixedVelocities` | none | User-fixed layer velocities (index 0 = layer 1); null entries are estimated. Entered in the UI as e.g. `450, -, -` |
| `MinusTimeWindow` | 5 | Number of positions in the plus–minus/GRM sliding regression of t_V (section 4.4) |
| `RegressionMode` | `Ordinary` | Segment line fits: OLS or Huber robust |

The typical geophone spacing is the median spacing of the line's spreads, or 1 m if none is positive
(`LineObservations.TypicalSpacing`).

The number of layers is `max(2, SegmentAssignment.LayerCount)`.

---

## 2. Common elements

### 2.1 Top-layer velocity

`LayeredCommon.TopLayerVelocity`: the fixed velocity of layer 1 if given; otherwise the **pooled
slope** of the layer-1 (direct) arrivals. If neither is available, the method fails with "The velocity of
the top layer cannot be estimated: at least three direct arrivals (layer 1) on one side of a shot are
needed, or fix the velocity."

### 2.2 Pooled velocity

`LayeredCommon.PooledVelocity(layer)` groups the arrivals of a layer (|offset| > 0) by (shot, side),
keeps groups with at least 2 arrivals and fits one common slope with a separate intercept per group
(within-group regression). Separate intercepts absorb shot-time errors and near-source delays.

```
slope = Σ_groups Σ (x − x̄_g)(t − t̄_g) / Σ_groups Σ (x − x̄_g)²
V = 1 / slope
SE(slope) = √( SSE / (n − groups − 1) / Sxx ),   SE(V) = SE(slope) / slope²
```

At least 3 arrivals in total and a positive slope are required.

### 2.3 Layer stripping

For refractor k (the top of layer k + 1), with layer velocities V_1 … V_{k+1}, the vertical slowness of
layer j for a ray critically refracted at refractor k is

```
q_{j,k+1} = √( 1/V_j² − 1/V_{k+1}² )
```

and the delay (time-depth) of the refractor is

```
δ_k = Σ_{j ≤ k} h_j · q_{j,k+1}
```

`LayeredCommon.ThicknessFromDelay` solves this for h_k given the thicknesses h_1 … h_{k−1} above:

```
h_k = ( δ_k − Σ_{j < k} h_j · q_{j,k+1} ) / q_{k,k+1}
```

For planar layers the intercept time equals 2·δ_k. For the GRM and delay-time methods, the upper
thicknesses at a position are taken from the refractors already computed
(`LayeredCommon.UpperThicknesses`): depths are interpolated along each upper refractor and, outside its
range, held constant at its end value (which raises the warning "shallower layer thicknesses were held
constant beyond their measured range").

### 2.4 Checks applied by every method

* **Velocity inversion** (`LayeredCommon.VelocityInversion`): velocities must strictly increase with
  depth. Otherwise the interpretation is **not produced**; the reason states which layer is not faster
  than the one above and asks to check the layer assignment. (Tested by
  `Velocity_inversion_is_reported_not_hidden`.)
* **Negative thickness**: a computed thickness that is negative (or NaN) is set to 0, the interface point
  is marked `Questionable`, and a warning names the position and the value.

### 2.5 Results

`LayeredModel` contains, per layer, the velocity, its standard error when estimable and the basis in
words; per refractor, the representative velocity, optional lateral velocity segments, the interface
points (x, depth below ground, elevation = ground elevation − depth, depth standard error when
estimable, delay time, number of constraining arrivals, questionable flag), the RMS residual of the
method's own fit where applicable and a basis text; model residuals; and warnings.

`LayeredModel.VelocityAt(x, depth)` returns the velocity at a point (null where a refractor depth is not
defined at x); it is used for 1-D profiles ([engineering.md](engineering.md)).

---

## 3. Intercept-time method

`InterceptTimeMethod` (slope–intercept analysis; Redpath 1973, Haeni 1988). Planar layers.

### 3.1 Velocities

* Layer 1: section 2.1.
* Fixed velocities are used where given.
* **Without a reversed pair**: each deeper layer's velocity is the pooled apparent velocity of its
  arrivals (section 2.2). Dip is not resolved. A warning says so: "No reversed shot pair: refractor
  velocities are apparent velocities and dip is not resolved (horizontal layers assumed)." If a forward
  shot was selected but the pair is incomplete or not ordered forward (low x) → reverse (high x), a
  further warning is added.
* **With a reversed pair** (forward shot at lower x than reverse shot): the segment fits of the forward
  shot towards increasing x (V_d) and of the reverse shot towards decreasing x (V_u) are used. Both need
  a fit with at least two arrivals.
  * Refractor 1, exact dipping-layer solution:

    ```
    a = asin(V_1 / V_d),  b = asin(V_1 / V_u)
    θ_c = (a + b) / 2,    α = (a − b) / 2     (dip)
    V_2 = V_1 / sin θ_c
    ```

    Requires V_1 < V_d and V_1 < V_u.
  * Deeper refractors: harmonic mean `2 V_d V_u / (V_d + V_u)` (small-dip approximation).

### 3.2 Depths

Depths are computed **beneath shot points** — the two shots of the reversed pair, or every included
shot otherwise. For each shot and refractor k:

1. Intercept times are taken from the segment fits of layer k + 1 of that shot — with a reversed pair,
   only the side facing the other shot; otherwise both sides, averaged.
2. Delay `δ = mean intercept / 2`.
3. Refractor 1 with a reversed pair: perpendicular depth `h⊥ = V_1 · t_i / (2 cos θ_c)` and vertical
   depth below the shot `h⊥ / cos α`. Otherwise: layer stripping (section 2.3).
4. The depth is the sum of the thicknesses; elevation = ground elevation at the shot − depth.
5. If a shot has no intercept for refractor k, deeper refractors are not computed for that shot.

Warnings: negative thickness (set to 0); "Some shots are buried; intercept depths beneath them are
relative to the source depth and were not corrected."; "No shot has a measurable intercept for
refractor k."

### 3.3 Residuals

Residuals are the differences between each included arrival and its own segment fit.

---

## 4. Generalized reciprocal method (GRM) and plus–minus

`GrmMethod` (Palmer 1980). `PlusMinusMethod` (Hagedoorn 1959) is the same code with XY fixed at 0.

A reversed pair is required: forward shot A at lower x, reverse shot B at higher x. Missing or
mis-ordered shots make the method unavailable with an explanation.

### 4.1 Reciprocal time

For refractor k (layer k + 1), the forward shot's arrivals towards increasing x and the reverse shot's
arrivals towards decreasing x are used; each needs at least 2.

The reciprocal time t_AB is the refractor arrival of each shot at the position of the other shot,
obtained by linear interpolation between arrivals or, beyond the last arrival, by linear extension of
the end segment by **at most one geophone interval** (`LayeredCommon.ArrivalAt`):

* both estimates available → their mean; a note is added if they differ by more than
  `max(2 ms, 3 % of t_AB)`; another if either was extended;
* one estimate → used, with a note that it is measured on one shot only;
* none → the refractor cannot be evaluated.

### 4.2 Velocity analysis and time-depths

Positions G are the distinct receiver positions of the two shots' layer arrivals strictly between the
shots. With X = G − XY/2 and Y = G + XY/2, t_AY and t_BX are interpolated (no extrapolation) from the
forward and reverse arrivals; positions where either is missing are skipped. At least 2 positions are
needed.

```
velocity-analysis function   t_V(G) = (t_AY − t_BX + t_AB) / 2
refractor velocity           V′ = 1 / slope of the line fitted to t_V(G)
time-depth                   t_G = [ t_AY + t_BX − (t_AB + XY / V′) ] / 2
```

If a velocity is fixed for that layer it is used instead of the slope. A non-positive slope makes the
refractor unavailable ("the velocity-analysis function does not increase with position").

The velocity standard error is the slope standard error divided by slope². The RMS deviation of t_V
from its regression line is reported as the velocity-analysis RMS and used as an indicative time-depth
uncertainty; the depth standard error is that value divided by q_{k,k+1}.

Depths at each G follow by layer stripping (section 2.3) using the time-depth t_G. Each point records
the number of arrivals within one geophone spacing of Y (forward) and X (reverse).

### 4.3 Fallback

If the GRM cannot evaluate a refractor and the intercept-time method has points for it, the
intercept-time result beneath the shots is used for that refractor with a warning; otherwise the
interpretation is unavailable.

### 4.4 Lateral velocity

When enough positions exist and the velocity is not fixed, a sliding regression over
`LayeredSettings.MinusTimeWindow` consecutive positions of t_V (default 5) gives local refractor
velocities (`LateralVelocity`). The Velocity model page does not offer a field for this window, so
projects edited in the application use the default of 5.

### 4.5 XY analysis

`GrmMethod.AnalyzeXy` evaluates the velocity-analysis function for XY = 0, ½·spacing, 1·spacing, … up
to a maximum, for one refractor. The Velocity model page calls it for refractor 1 with a maximum of
4 × the typical spacing (`AnalyzeXy`, `src/RefractionStudio.App/ViewModels/QcAndVelocityViewModels.cs`)
and plots the result.

The suggested XY is the value with the **minimum RMS deviation of the velocity-analysis function** from
its regression line, among evaluations with at least 3 positions (the "simplest" velocity-analysis
function). It is a suggestion; the UI copies it into the XY field.

Helper formulae (Palmer 1980), single overburden layer:

```
average overburden velocity  V̄ = √( V′² · XY / (XY + 2 V′ t_G) )
optimum XY                   XY = 2 h tan i,  sin i = V_1 / V_2
```

### 4.6 Residuals

The GRM and plus–minus methods do not produce model residuals (the residual list is empty); the
velocity-analysis RMS is reported per refractor.

---

## 5. Delay-time least squares

`DelayTimeMethod` (time-term method; Scheidegger & Willmore 1957, Willmore & Bancroft 1960, Barry 1967).
It uses all shots.

### 5.1 Model

For each head-wave arrival of refractor k (layer k + 1, |offset| > 0):

```
t = a_s + a_r + Σ_m s_m · L_m
```

* a_s, a_r — delay times at the source and receiver;
* s_m — refractor slowness in lateral segment m, with the line between the extreme shot and receiver
  positions divided into `RefractorVelocitySegments` equal segments;
* L_m — the length of the source–receiver interval (horizontal) inside segment m.

With a fixed velocity V there is one implicit segment and the term (|Δx| / V) is moved to the
right-hand side.

At least 4 arrivals are required.

### 5.2 Delay nodes

* Receivers are clustered by position: a receiver within `CoincidenceTolerance` of an existing surface
  node shares it.
* A surface shot (shot depth ≤ 0) shares a node with a receiver within the tolerance.
* A **buried** shot gets its own node, which is not converted to depth; a note says so.
* An arrival whose source and receiver share a node (zero-length path) is skipped.

### 5.3 Additional equations

All equations are solved together by weighted least squares through the normal equations with a
Cholesky factorisation (`DenseSolver.LeastSquares`, `src/RefractionStudio.Analysis/Numerics/LinearAlgebra.cs`);
a row with weight w contributes w² to the normal equations.

| Equations | Weight |
|---|---|
| Arrival times (above) | 1 |
| Source tie: a surface shot that has no coincident receiver node, with receivers on both sides, is tied to the linearly interpolated delay of its two neighbouring receiver nodes: `a_s − (1 − w) a_left − w a_right = 0` | 10 |
| Slowness smoothing (more than one segment, velocity not fixed): `width · (s_m − s_{m+1}) = 0` | `VelocitySmoothing` |
| Damping of every delay node: `a = 0` | 1e-6 |

The source ties separate source and receiver delays. If no shot shares a receiver node and none can be
tied, a note warns that "source and receiver delays cannot be separated reliably; depths beneath
receivers are uncertain". If there are fewer than half as many arrival equations as unknowns, a note
says the delays are poorly constrained. A singular system makes the refractor unavailable.

### 5.4 Uncertainties

From the arrival equations only:

```
σ² = SSE / (n_data − n_unknowns)
SE(a_i) = √(σ² · [(AᵀW²A)⁻¹]_ii)
SE(V_m) = SE(s_m) / s_m²
```

If there are no redundant arrivals (n_data ≤ n_unknowns), no uncertainties are given and a note says so.
Depth standard errors are SE(delay) / q_{k,k+1}.

### 5.5 Results

* Every segment slowness must be positive; otherwise the refractor is unavailable.
* Representative velocity: harmonic mean of the segment velocities (equal widths),
  `segments / Σ (1 / V_m)`; its standard error is given only for a single segment.
* Lateral velocity segments are reported when there is more than one.
* Interface points are computed at every surface node by layer stripping of the node's delay.
* Residuals: observed minus predicted time for every arrival used; the refractor's RMS residual is
  √(SSE / n_data).

---

## 6. Topography and geometry

* All four methods work with **horizontal** positions and offsets (|x_r − x_s|); no elevation or
  datum static correction is applied to the times.
* Depths are vertical depths below the ground surface at each position (below the shot for the
  intercept-time method). Interface elevations are the ground elevation from the line topography
  (linear interpolation between surveyed points, end values held beyond them, 0 when no topography is
  given) minus the depth.
* Buried sources are not corrected by the intercept-time method (warning) and have their own delay node
  in the delay-time method (note); the GRM uses the shot positions as given.
* The **forward-model check** on the Velocity model page (`ForwardModelCheck`,
  `src/RefractionStudio.App/ViewModels/QcAndVelocityViewModels.cs`) does honour topography: it
  rasterises the layered model onto a grid that follows the topography (cell size half the typical
  spacing, depth `max(5 m, 1.5 × deepest interface)`), with interfaces held flat beyond their range,
  traces rays from each source elevation to each receiver elevation with the shortest-path solver
  (4 secondary nodes) and reports the RMS of observed minus computed times and the three largest
  residuals (`TomographyEngine.Rasterize`, `TomographyEngine.ForwardModel`; see
  [tomography.md](tomography.md)).

---

## 7. Warnings and quality control

Warnings of a successful interpretation are reported as `layered-warning` QC flags (level Review), and
the reason of an unavailable interpretation as a `layered` flag (level Problem)
(`src/RefractionStudio.Analysis/Quality/QualityControl.cs`; see [qc.md](qc.md)).

Messages produced by the methods:

| Method | Condition | Result |
|---|---|---|
| All | Top-layer velocity not estimable | Unavailable |
| All | Velocities not increasing with depth | Unavailable |
| All | Negative thickness | Warning; thickness 0; point questionable |
| GRM, delay-time | Upper thicknesses held constant beyond their range | Warning |
| Intercept-time | No reversed pair / incomplete pair | Warning |
| Intercept-time | Reversed pair lacks layer arrivals on either shot | Unavailable |
| Intercept-time | Apparent velocity not above V_1 (reversed, refractor 1) | Unavailable |
| Intercept-time | Too few arrivals for a pooled velocity | Unavailable |
| Intercept-time | Buried shots; no intercept for a refractor | Warning |
| GRM / plus–minus | Pair missing or not ordered | Unavailable |
| GRM / plus–minus | Reciprocal-time estimates differ; extended; one shot only | Warning |
| GRM / plus–minus | Fewer than 2 covered positions; no reciprocal time; non-increasing t_V | Intercept-time fallback (warning) or unavailable |
| Delay-time | Fewer than 4 arrivals; singular system; non-positive slowness | Unavailable |
| Delay-time | Buried shot; poorly constrained delays; source/receiver delays not separable; no redundancy | Warning |

---

## 8. Tests

`tests/RefractionStudio.Analysis.Tests/LayeredMethodTests.cs` checks, on synthetic data: exact OLS and
its standard errors; Huber resistance to an outlier; crossover recovery by the segment assigner; exact
pooled direct-wave velocity; unreversed intercept-time velocity and depth; identical and exact
plus–minus and GRM (XY = 0) results for flat layers; exact GRM at the optimum XY and its suggestion by
the XY analysis; exact delay-time inversion for flat layers; three-layer recovery by layer stripping; a
dipping refractor resolved by a reversed pair; velocity inversions reported; and a missing reciprocal
pair explained. See [../SCIENTIFIC-VALIDATION.md](../SCIENTIFIC-VALIDATION.md) for the validation runs.

---

## References

As cited in the source code:

* Barry (1967) — delay-time method (`DelayTimeMethod.cs`).
* Hagedoorn, J.G. (1959). Geophysical Prospecting 7, 158–182 — plus–minus method (`GrmMethod.cs`).
* Haeni (1988), USGS TWRI 2-D2 — slope–intercept interpretation (`InterceptTimeMethod.cs`).
* Palmer, D. (1980). The Generalized Reciprocal Method of Seismic Refraction Interpretation. SEG (`GrmMethod.cs`).
* Redpath (1973) — slope–intercept interpretation (`InterceptTimeMethod.cs`).
* Scheidegger & Willmore (1957); Willmore & Bancroft (1960) — time-term method (`DelayTimeMethod.cs`).
