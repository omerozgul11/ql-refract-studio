# First-arrival travel-time tomography

This document describes the tomographic inversion implemented in
`src/RefractionStudio.Analysis/Tomography/`:

| File | Content |
|---|---|
| `ModelGrid.cs` | `ModelGrid` (regular cell grid with topography) and `VelocityModel` |
| `ShortestPathSolver.cs` | `ShortestPathSolver` (network ray tracing), `Ray`, `TraceResult`, `ITravelTimeSolver` |
| `TomographyEngine.cs` | `TomographyEngine` (Gauss–Newton inversion), `TomographySettings` usage, `TomographyResult`, forward modelling and rasterisation helpers |

The settings record `TomographySettings` is defined in `src/RefractionStudio.Domain/Interpretation.cs`.
The linear solver (`Lsqr`, `SparseMatrix`) is in `src/RefractionStudio.Analysis/Numerics/LinearAlgebra.cs`.

Tomography is not run as part of `RefractionAnalyzer.Analyze`; it runs only on request
(`ProjectWorkspace.RunTomographyAsync` in `src/RefractionStudio.Core/ProjectWorkspace.cs`, called from the
**Run tomography** button on the Velocity model page). The stored result is attached to later analyses and
checked for staleness (see [Outputs, storage and staleness](#outputs-storage-and-staleness)).

Related documents: [layer-assignment.md](layer-assignment.md) (segment fits used for the gradient
starting model), [layered-methods.md](layered-methods.md) (layered starting model, forward-model
check), [qc.md](qc.md) (tomography QC flags), [engineering.md](engineering.md) (profiles and property
sections derived from tomograms).

---

## 1. Input data

The engine uses the *included* observations of one line (`LineObservations.Included`, see
`src/RefractionStudio.Analysis/TravelTimes/Observations.cs`). Every observation carries the corrected
travel time

```
t_corrected = t_raw + shot-time correction + time shift (processing + trace static)
```

and the source and receiver coordinates (x, elevation). Observations with zero offset *and* zero
elevation difference between source and receiver are dropped.

Preconditions (`TomographyEngine.Run`), each returning an "unavailable" result with the quoted reason:

| Condition | Reason text |
|---|---|
| fewer than 10 usable picks | "Tomography needs at least 10 included first-arrival picks; this line has N." |
| `MinVelocity <= 0` or `MaxVelocity <= MinVelocity` | "The velocity bounds are invalid (minimum must be positive and below the maximum)." |
| any receiver unreachable in the starting model | "N receivers could not be reached by rays; check the line geometry and topography." |
| starting model cannot be built | see [Starting models](#4-starting-models) |

### Data uncertainty

Each pick i has a standard deviation

```
σ_i = max( PickErrorAbsolute + PickErrorRelative · |t_i| , 1e-6 s )
```

Defaults (`TomographySettings`): `PickErrorAbsolute` = 0.0005 s (0.5 ms), `PickErrorRelative` = 0.02 (2 %).

---

## 2. Model grid

`ModelGrid.Create(topography, xMin, xMax, dx, dz, maxDepth)`:

* **Cells.** A regular grid of `Nx × Nz` rectangular cells, row 0 at the top. Cell index =
  `row · Nx + column`. Elevation `z` is positive up.
* **Lateral extent.** `xMin`/`xMax` are the outermost shot or receiver positions of the used data.
  `Nx = ceil((xMax − xMin)/dx)` and the grid is centred on the data:
  `X0 = (xMin + xMax)/2 − Nx·dx/2`.
* **Vertical extent.** The top `ZTop` is the highest ground elevation sampled at the cell edges; the
  bottom lies `maxDepth` below the *lowest* ground elevation sampled at the cell centres:
  `Nz = ceil((ZTop − (minGround − maxDepth))/dz)`.
* **Air cells.** A cell is *active* when its centre is at or below the ground elevation at the column
  centre. Inactive (air) cells carry no velocity (NaN) and are not parameters. Every column is given
  at least one active cell (the bottom one) if none qualifies.
* **Topography outside the surveyed points** is held at the end elevations
  (`Topography.ElevationAt`); the engine adds a note when the model extends beyond the topography
  points.

Cell size and depth (`TomographyEngine.Run`):

| Quantity | Rule |
|---|---|
| cell width `dx` | `CellWidth`, or (default `null`) half the typical geophone spacing |
| cell height `dz` | `CellHeight`, or (default `null`) equal to `dx` |
| `maxDepth` | `MaxDepth`, or (default `null`) `max(maxOffset/3, 3·dz)` where `maxOffset` is the largest absolute offset of the used data |

The typical geophone spacing (`LineObservations.TypicalSpacing`) is the median of the spread spacings of
the line, or 1 m if none is positive.

**Cell-count limit.** `TomographyEngine.MaxCells = 60000`. While the grid has more cells, `dx` and `dz`
are both multiplied by 1.25 and the grid is rebuilt; a note records the enlarged size.

**Non-square cells.** If `dz > 1.5·dx` or `dx > 1.5·dz` a note warns that strongly non-square cells reduce
ray-tracing accuracy for steep or shallow ray segments. (Thin cells cannot represent the steep
critical-angle legs of head waves; the ray-tracer test
`tests/RefractionStudio.Analysis.Tests/ShortestPathSolverTests.cs` therefore uses square cells for the
two-layer case.)

In the application (`VelocityModelPageViewModel.TomographySettingsFromForm` in
`src/RefractionStudio.App/ViewModels/QcAndVelocityViewModels.cs`), **Choose the grid automatically**
passes `null` for cell width, cell height and depth. Otherwise one **Cell size** value (minimum 0.1 m)
is used for both width and height, and **Model depth** (minimum 1 m) is passed as `MaxDepth`.

---

## 3. Forward problem: shortest-path ray tracing

`ShortestPathSolver` implements network (shortest-path) ray tracing after Moser (1991, Geophysics 56,
59–67).

### Graph

* **Nodes**: all cell corners plus `SecondaryNodes` evenly spaced nodes on every cell edge
  (horizontal and vertical). Valid range 0–12; the constructor default is 3, the
  `TomographySettings.SecondaryNodes` default is 5 (the application always uses 5 for inversion).
* **Edges**: every pair of nodes on the boundary of the same *active* cell with finite slowness is
  connected by a straight segment with travel time `length × slowness(cell)`. A segment along an edge
  shared by two cells is evaluated in both cells, so the faster cell is used; this lets the network
  represent head waves travelling along a velocity contrast.
* The node layout is cached per grid instance.

### Source and receivers

* The source is attached to every active cell whose closed rectangle contains it (tolerance 1e-7 of
  the cell size). A point above the active surface of its column(s) is first moved down to the highest
  active cell top. If the point falls in an inactive cell below a topographic step, the top active
  cell of its column is used. Initial node times are straight-line times from the source to every
  node of those cells.
* Dijkstra's algorithm (binary-heap priority queue with lazy deletion) gives the first-arrival time
  at every node.
* A receiver is attached the same way. Its time is the minimum over the nodes `v` of its cells of
  `T(v) + |v − receiver| · s(cell)`; if source and receiver share a cell, the direct straight segment is
  also considered.
* **Rays** follow the predecessor chain from the receiver back to the source. Each segment records
  the cell it crosses; ray length per cell is therefore exact for the network path.

Network paths are restricted to the node set, so computed times are never shorter than the true
continuous-medium times; accuracy improves with the number of secondary nodes. The unit tests
(`tests/RefractionStudio.Analysis.Tests/ShortestPathSolverTests.cs`) check:

| Test | Configuration | Tolerance asserted |
|---|---|---|
| homogeneous medium, straight rays | 1 × 1 m cells, 3 secondary nodes | relative error in [0, +1 %] |
| homogeneous medium, straight rays | 1 × 1 m cells, 5 secondary nodes | relative error in [0, +0.3 %] |
| two-layer head wave vs intercept formula (500/2000 m/s, h = 5 m) | 0.5 × 0.5 m cells, 4 nodes | 0 to +0.1 ms |
| linear gradient v = 400 + 40·z vs `t = (2/g)·asinh(g·x/(2·v0))` | 0.5 × 0.25 m cells, 3 nodes | relative error ≤ 1 % |
| topography | 1 × 1 m cells | rays cross active cells only; time ≥ straight chord time |

Shots are traced in parallel (`Parallel.ForEach`, one task per shot gather).

---

## 4. Starting models

`TomographySettings.StartingModel` (`StartingModelKind`, default `Gradient`):

### Gradient (default)

```
v(cell) = clamp( v_top + (v_bottom − v_top) · min(1, d / H) , MinVelocity, MaxVelocity )
```

where `d = max(0, depth of the cell centre below the local ground)` and `H = ZTop − ZBottom` is the total
grid height.

* `v_top` = `StartTopVelocity`, or (default `null`) the **near-offset velocity**
  (`TomographyEngine.NearOffsetVelocity`): the median of `offset / t_corrected` over picks with
  `0 < |offset| ≤ 2.5 × typical spacing` and positive time. If there are none, the median of the five
  smallest apparent velocities of all picks is used. This estimate does not depend on the layer
  assignment.
* `v_bottom` = `StartBottomVelocity`, or (default `null`) the highest positive apparent velocity of the
  ordinary least-squares segment fits (`SegmentAssigner.FitAll`, see [layer-assignment.md](layer-assignment.md));
  if no fit has one, the largest `offset / t_corrected` of the picks. `v_bottom` is raised to `v_top` if
  smaller.
* If `v_top` cannot be derived the run stops with "A starting velocity could not be derived from the
  picks; enter the starting velocities."

### Constant

`v = v_top` everywhere (same `v_top` rule as above), clamped to the bounds.

### Layered

The current layered interpretation (see [layered-methods.md](layered-methods.md)) is rasterised onto the
grid: each active cell receives the velocity of the layer containing its centre depth
(`LayeredModel.VelocityAt`). Where a refractor depth is undefined at the cell's x (beyond the positions
where the method determined it), the nearest end depth of that refractor is held
(`VelocityWithHeldInterfaces`) and a note is added. Values are clamped to the bounds.
`ProjectWorkspace.RunTomographyAsync` computes the layered model with the line's current layered method
and settings; if it is unavailable the run stops with "The layered starting model is not available: …".

In the application the starting top/bottom velocities are not exposed; they are always estimated
(`StartTopVelocity` and `StartBottomVelocity` are passed as `null`).

---

## 5. Inverse problem

### Parameterisation

The parameters are the natural logarithms of the slowness of the active cells, `m_p = ln s_p`. Bounds are
applied in log space:

```
ln(1/MaxVelocity) ≤ m_p ≤ ln(1/MinVelocity)
```

Defaults: `MinVelocity` = 100 m/s, `MaxVelocity` = 7000 m/s. The starting model is clamped to these bounds.

### Linearised system

At each iteration rays are traced in the current model and the following problem is solved for the
update Δm:

```
min  ‖ W (d − g(m) − J Δm) ‖²  +  λ² ‖ R (m + Δm) ‖²
```

* `d − g(m)`: observed minus computed travel times.
* `J = ∂t/∂ln s`: for pick i and cell p, `J_ip = L_ip · s_p`, where `L_ip` is the length of ray i in cell p.
* `W`: diagonal, `W_ii = √w_i / σ_i`, with `w_i` the robust weight (1 without robust weighting).
* `R`: first differences between neighbouring active cells. For each active cell, one row against its
  right neighbour, weighted by `√HorizontalSmoothingRatio` (ratio floored at 1e-6), and one row against
  the cell below, weight 1. Inactive neighbours are skipped.

Because the regularisation acts on `m + Δm` (not on Δm), the stacked system is

```
[ W J  ]        [ W (d − g(m)) ]
[ λ R  ] Δm  =  [ −λ R m       ]
```

which is solved without forming normal equations by LSQR (Paige & Saunders 1982), with at most 400
LSQR iterations and tolerance 1e-6 (`Lsqr.Solve`).

Defaults: `Smoothing` (initial λ) = 30, `HorizontalSmoothingRatio` = 2 (values above 1 favour laterally
smooth, layer-like models).

### Robust (Huber) data weighting

With `RobustDataWeighting = true` (default), every iteration re-weights each data row from the current
normalised residual `u_i = |r_i| / σ_i`:

```
w_i = 1              if u_i ≤ k
w_i = k / u_i        if u_i > k          with k = HuberThreshold = 2.0
```

The misfit used for the acceptance test and the target is the Huber objective

```
Φ = (1/N) Σ ρ(u_i),   ρ(u) = u²            (u ≤ k)
                      ρ(u) = 2k·u − k²     (u > k)
```

so the misfit is quadratic for normal residuals and linear for outliers. Without robust weighting
`Φ = χ²`. After the run, if any pick has `w_i < 0.5` (i.e. `u_i > 4`), a note states how many picks with
residuals above 4σ were down-weighted and asks to review them in QC. The picks are not removed; they
remain in the data and are reported by QC if their residual exceeds the QC threshold.

### Reported misfit

For every accepted model the engine records (`TomographyIteration`):

```
RMS  = sqrt( Σ r_i² / N )             (seconds)
χ²   = Σ (r_i/σ_i)² / N
```

together with λ and the accepted step fraction. Iteration 0 is the starting model (λ = NaN).

### Line search

The update is tried with step fractions α = 1, 0.5, 0.25. For each, the trial model `m + α·Δm` is clamped
to the bounds and rays are re-traced. A trial is rejected if any receiver becomes unreachable. The first
trial whose objective Φ is lower than the current Φ is accepted.

### λ schedule and stopping rules

Let `λ₀ = Smoothing`, `λ_floor = 0.1 · λ₀`, `decay = SmoothingDecay` (default 0.8).

For iteration `it = 1 … MaxIterations` (default 10):

1. If `Φ ≤ TargetChi2` (default 1.0): stop — "Target misfit reached (…)". With robust weighting the
   target is compared with the Huber objective; the message reports both.
2. Build and solve the system with the current λ; perform the line search.
3. If a step was accepted and the relative improvement `(Φ_old − Φ_new)/Φ_old ≥ 1 %`:
   `λ ← max(λ_floor, decay · λ)`.
4. Otherwise (no step accepted, or improvement < 1 %):
   * if λ is above the floor, relax it faster, `λ ← max(λ_floor, decay² · λ)`, and continue (the
     regularisation may be too strong);
   * if λ is already at the floor, stop with "Misfit stagnated (improvement x % < 1 % at the smallest
     regularisation)." (step accepted) or "No further reduction of the misfit along the model update."
     (no step accepted).
5. If the loop ends, the stop reason is "Maximum number of iterations reached."

An accepted step with less than 1 % improvement is kept (the model is updated) before λ is relaxed.
Rejected iterations are not recorded in the convergence history, so the number of recorded iterations
can be smaller than the number of loop passes. With `SmoothingDecay = 1` λ never reaches the floor, so
the stagnation stop does not trigger and the run continues to the maximum number of iterations or the
target. The application always passes `SmoothingDecay = 0.8`.

The run can be cancelled; progress is reported per stage ("Tracing rays in the starting model",
"Solving the linearised system", "Tracing rays (step α)", "Iteration complete").

---

## 6. Coverage, depth of investigation and the constrained mask

From the final rays (`TomographyEngine.Run`):

* `Coverage[cell]`: total ray length (m) in the cell.
* `Hits[cell]`: number of rays crossing the cell (each ray counted once per cell).
* `InvestigationDepth[column]` (DOI): depth below the column's ground elevation of the **bottom edge** of
  the deepest active cell with `Hits > 0` and `Coverage > CoverageThreshold`; NaN if no such cell.
  `CoverageThreshold` defaults to 0 m (any ray), and the application passes 0.

Two predicates are exposed on `TomographyResult`:

* `IsConstrained(cell)`: the cell is active and its centre depth is at or above the DOI of its column.
  Interior cells that individual network rays happen to miss are treated as constrained, because they are
  tied to their neighbours by the regularisation.
* `IsCrossedByRays(cell)`: `Hits > 0` and `Coverage > CoverageThreshold`.

**Display** (`PlotModels.Section` in `src/RefractionStudio.Reporting/PlotModels.cs`,
`SectionRenderer` in `src/RefractionStudio.Visualization/SectionRenderer.cs`): cells for which
`IsConstrained` is false (below the DOI, or in columns without rays) are not coloured and are drawn
with a light hatch. The colour range and the default contour levels are computed from constrained cells
only, and contours are not drawn through unconstrained cells. The starting-model view shows all cells.
The caption on the Velocity model page states the same rule: hatched areas are below the depth of
investigation of their column, or in columns that no ray crosses.
The ray-coverage view plots `log₁₀(Coverage)` for active cells with coverage > 0.

The DOI also determines the constrained depth of 1-D profiles taken from the tomogram
(`ProfileExtractor.FromTomography`, see [engineering.md](engineering.md)), and the
`ConstrainedVelocityAt(x, depth)` lookup used by property sections returns a value only for constrained
cells.

---

## 7. Outputs, storage and staleness

`TomographyResult` contains:

| Field | Content |
|---|---|
| `Model`, `StartingModel` | final and starting `VelocityModel` (velocity per cell, NaN in air cells) |
| `Coverage`, `Hits` | per-cell ray length (m) and ray count |
| `InvestigationDepth` | per-column DOI (m below ground), NaN where no rays |
| `Iterations` | convergence history (`Iteration`, `Lambda`, `RmsSeconds`, `Chi2`, `StepLength`) |
| `Residuals` | `ModelResidual` per used pick: observed, predicted, `Residual = observed − predicted` |
| `Rays` | final ray paths (vertex x, z) per pick |
| `Settings` | the settings used |
| `InputFingerprint` | SHA-256 (hex) of the inputs, see below |
| `Notes` | decisions taken (cell enlargement, non-square cells, topography extension, starting model, down-weighted picks) |
| `StopReason`, `Elapsed` | why iteration stopped; run time |

`FinalRms` and `FinalChi2` are taken from the last recorded iteration.

**Fingerprint** (`TomographyEngine.Fingerprint`): SHA-256 over, in invariant culture with round-trip
formatting, every included observation (key, corrected time, shot x, source elevation, receiver x,
receiver elevation, ordered by key), all topography points, `settings.ToString()`, and — only when the
starting model is `Layered` — the layered model's layer velocities and refractor points.

**Storage.** `ProjectWorkspace.RunTomographyAsync` adds the result to the `ResultStore`
(`src/RefractionStudio.Core/Services/ResultStore.cs`, serialised into the project's results folder) and
records a `TomographyResultRef` (result id, fingerprint, creation time, application version, number of
iterations = recorded iterations − 1, final RMS and χ²) on the line with one undoable edit. See
`docs/file-formats/project-file.md`.

**Staleness** (`RefractionAnalyzer.AnalyzeLine` in `src/RefractionStudio.Analysis/RefractionAnalyzer.cs`):
the stored result is *stale* when the fingerprint recomputed from the current picks, geometry and the
stored settings differs from the reference, or when the stored settings differ from the line's current
tomography settings. A stale result is still shown, marked "OUT OF DATE — run again." on the Velocity
model page, triggers the QC flag `tomography-stale`, and profiles taken from it carry the note "The
tomogram is out of date with respect to the current picks or settings."

---

## 8. Forward modelling of layered models

Two helpers reuse the ray tracer:

* `TomographyEngine.Rasterize(LayeredModel, ModelGrid)`: layer velocity at each active cell centre, with
  refractor depths held at their nearest determined value beyond their range.
* `TomographyEngine.ForwardModel(VelocityModel, observations, secondaryNodes = 3)`: first-arrival times
  through a gridded model, returned as residuals (unreachable receivers are omitted).

The **Forward-model check** button of the layered method
(`VelocityModelPageViewModel.ForwardModelCheck`) rasterises the current layered model on a grid with
square cells of half the typical spacing and depth `max(5 m, 1.5 × deepest refractor point)`, traces with
4 secondary nodes, and reports the RMS residual and the three largest residuals.

---

## 9. Defaults and application limits

| Setting (`TomographySettings`) | Default | Application form |
|---|---|---|
| `CellWidth`, `CellHeight` | null (½ spacing; height = width) | one "Cell size" value, ≥ 0.1 m, or automatic |
| `MaxDepth` | null (`max(maxOffset/3, 3·dz)`) | "Model depth", ≥ 1 m, or automatic |
| `StartingModel` | Gradient | Gradient / Layered / Constant |
| `StartTopVelocity`, `StartBottomVelocity` | null (estimated) | not exposed |
| `Smoothing` | 30 | "Smoothing (regularisation weight)", ≥ 0 |
| `SmoothingDecay` | 0.8 | fixed 0.8 |
| `HorizontalSmoothingRatio` | 2 | "Horizontal / vertical smoothing ratio", ≥ 0.1 |
| `MaxIterations` | 10 | "Maximum iterations", clamped to 1–50 |
| `MinVelocity` / `MaxVelocity` | 100 / 7000 m/s | "Velocity limits"; min ≥ 10, max ≥ min + 10 |
| `SecondaryNodes` | 5 | fixed 5 |
| `PickErrorAbsolute` | 0.0005 s | "Pick uncertainty" absolute in ms, ≥ 0.01 ms |
| `PickErrorRelative` | 0.02 | relative in %, ≥ 0 |
| `CoverageThreshold` | 0 m | fixed 0 |
| `TargetChi2` | 1.0 | fixed 1 |
| `RobustDataWeighting` | true | "Down-weight outlying picks (robust)" |

(The "Maximum iterations" field accepts 1 to 50.)

---

## 10. Limitations

* First arrivals only; no amplitudes, later arrivals or anisotropy.
* The network ray tracer overestimates times slightly (paths are restricted to nodes); the error
  depends on cell size and the number of secondary nodes (Section 3).
* Velocity inversions (low-velocity layers beneath faster layers) produce no first arrivals and cannot be
  resolved; the regularised model will smooth across them.
* Regions below the DOI are not constrained by data and are masked; regions inside the DOI that no ray
  crosses are determined by the regularisation.
* The final model depends on the starting model, λ, the smoothing ratio and the pick uncertainties;
  comparing runs (and with the layered interpretation) is part of the interpretation.

### Validation

`tests/RefractionStudio.Analysis.Tests/TomographyTests.cs`:

* gradient model (v = 400 + 40·depth m/s, 7 shots, 24 channels at 2 m, exact synthetic times): final RMS
  < 0.5 ms; χ² decreases; cells between x = 6 and 40 m, deeper than 1 m, shallower than 0.8 × DOI and hit
  by ≥ 5 rays are within ±10 % of the true velocity (more than 50 cells checked); DOI within 1–16 m;
* two-layer model (500/2000 m/s, 5 m): RMS < 1 ms; velocity at 1.5 m depth within 400–650 m/s; maximum
  velocity between 6 and 9 m depth > 1500 m/s; profile constrained depth equals the DOI; fingerprint is
  reproducible;
* fewer than 10 picks is refused with a reason;
* forward modelling the delay-time interpretation of the two-layer model reproduces the picks within
  ±0.2 ms.

See also `docs/SCIENTIFIC-VALIDATION.md`.
