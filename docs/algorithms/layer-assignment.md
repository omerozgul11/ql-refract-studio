# Layer assignment

Every first arrival used by the layered methods is assigned to a layer: layer 1 is the direct wave in
the top layer, layer n (n ≥ 2) is the head wave of refractor n − 1 (the top of layer n). The assignment
is stored as crossover offsets per shot and side, plus optional per-pick overrides. The automatic
assignment is a suggestion that the user confirms or edits.

Source files:

| File | Content |
|---|---|
| `src/RefractionStudio.Domain/Interpretation.cs` | `GatherSegments`, `SegmentAssignment` and `LayerOf` |
| `src/RefractionStudio.Analysis/TravelTimes/Segments.cs` | `SegmentAssigner` (automatic suggestion) and `FitAll` (segment fits) |
| `src/RefractionStudio.Analysis/TravelTimes/Observations.cs` | `ObservationBuilder` — applies the assignment to every observation |
| `src/RefractionStudio.Analysis/Numerics/Regression.cs` | Ordinary and Huber robust line fits |
| `src/RefractionStudio.Core/Session/Edits.cs` | `SetSegments`, `SetGatherCrossovers`, `SetPickLayer` |
| `src/RefractionStudio.App/ViewModels/QcAndVelocityViewModels.cs` | Suggest / Reset commands, crossover dragging, per-pick assignment |
| `tests/RefractionStudio.Analysis.Tests/LayeredMethodTests.cs` | `Segment_assigner_recovers_the_crossover_of_a_two_layer_model` |

---

## 1. Data model

`SegmentAssignment(LayerCount, Gathers, Overrides)`:

* `LayerCount` — number of layers of the interpretation (default 2). The analysis uses
  `max(2, LayerCount)` (`RefractionAnalyzer.AnalyzeLine`, `src/RefractionStudio.Analysis/RefractionAnalyzer.cs`).
* `Gathers` — per shot, a `GatherSegments(Left, Right, UserEdited)`:
  * `Left` — crossover offsets on the decreasing-x side, as absolute offsets in increasing order;
  * `Right` — crossover offsets on the increasing-x side;
  * `UserEdited` — false while the values are an automatic suggestion.
* `Overrides` — explicit layer of individual picks (`PickKey` → layer).

### Layer of an arrival (`SegmentAssignment.LayerOf`)

For a pick with signed offset `x_r − x_s`:

1. If the pick has an override, that layer is returned.
2. If the shot has no `GatherSegments`, the layer is 1.
3. Otherwise the side is chosen by the sign of the offset (negative → `Left`, zero or positive →
   `Right`), and

   ```
   layer = 1 + number of crossovers c on that side with |offset| ≥ c
   ```

   capped at `LayerCount`. A crossover of 0 means that the shallow layer is absent on that side (all
   arrivals start at layer 2 or deeper, for example on an off-end shot).

`ObservationBuilder.Build` stores the layer in every `Observation`; excluded picks also receive a layer
but are not used.

---

## 2. Automatic suggestion (`SegmentAssigner.Suggest`)

The suggestion is computed for every shot whose segments the user has **not** edited. It has three
stages: per-side segmentation, reference slownesses for the line, and a monotone mapping of each side's
segments onto the reference layers.

### 2.1 Per-side segmentation with BIC

For each shot and each side, the included observations with |offset| > 0 are sorted by |offset| and
corrected time t is regressed against |offset| (`Segment`):

* candidate segmentations have between 1 and `min(LayerCount − 1, ⌊n/2⌋ − 1)` breaks
  (n = number of points), so up to `LayerCount` segments;
* every segment has at least 2 points;
* every segment slope must be positive, and slopes must **decrease** with offset (velocities increase
  with depth): each segment's slope must be lower than the previous one;
* each segment is fitted by ordinary least squares (computed from prefix sums);
* candidates are scored by the Bayesian information criterion with a Gaussian likelihood and two
  parameters per segment:

  ```
  BIC = n · ln( max(SSE / n, (1e-5 s)²) ) + 2 · segments · ln n
  ```

  The misfit is floored at a timing precision of 10 µs (`TimingFloor`) so that rounding noise in exact
  synthetic data cannot justify extra segments;
* the single-segment fit is a candidate only if its slope is positive; all break combinations are
  searched exhaustively, and the lowest BIC wins.

The result is the list of break indices and the slope and intercept of each segment.

### 2.2 Reference slownesses (`InitialSlownesses`)

Reference slownesses s_1 > s_2 > … for the line's layers are estimated from all segmented sides with
at least 2 points:

1. **Direct-wave sides**: sides whose first segment has a positive slope and passes near the origin —
   `|intercept| < max(0.002 s, 0.25 · first arrival time)`.
2. For each layer k, the reference slowness is the median of the k-th segment slope over the
   direct-wave sides that have at least k segments.
3. Layers that remain undetermined (or all layers, if no side starts with a direct wave) are filled
   from a 1-D k-means of all positive segment slopes in log-slowness (k = `LayerCount`, centres
   initialised evenly between the largest and smallest value, at most 50 iterations, returned in
   decreasing slowness). With no slopes at all, the fill values are 1/(500 · k) s/m.
4. Monotonicity is enforced: `s_k = min(s_k, 0.98 · s_{k−1})`.

### 2.3 Monotone mapping of segments onto layers (`MapSegments`)

The m segments of one side are mapped to layers L_1 ≤ L_2 ≤ … ≤ L_m (non-decreasing) by dynamic
programming, minimising

```
Σ_i | ln( slope_i / s_{L_i} ) |
```

(a segment with a non-positive slope costs 10). Because the sequence is only non-decreasing:

* consecutive segments may share a layer — a curved branch split by the segmentation;
* shallow layers may be absent — the first segment of an off-end shot can map directly to layer 2 or 3.

If no reference slownesses exist, segments are numbered 1, 2, … in order. A side with fewer than two
points gets a single segment of layer 1.

### 2.4 Crossovers (`Crossovers`)

The layer sequence of a side's points is converted to crossover offsets:

* if the first point's layer is L > 1, L − 1 crossovers of 0 are added (layers absent at small offsets);
* between consecutive points i − 1 and i whose layers differ, one crossover per layer step is placed at
  the midpoint `(|offset_{i−1}| + |offset_i|) / 2`;
* at most `LayerCount − 1` crossovers are kept.

The suggestion is returned as `GatherSegments(Left, Right, UserEdited = false)` for each processed shot;
user-edited shots and all per-pick overrides are carried over unchanged.

`SuggestCrossovers` (the interface method for a single side) returns the midpoints of the BIC breaks
only, without the reference mapping.

---

## 3. User control

All changes are undoable edits (`src/RefractionStudio.Core/Session/Edits.cs`) and are recorded in the
project history.

| Action (Velocity model page) | Effect | Code |
|---|---|---|
| **Suggest** | Re-suggests every shot that is not user-edited for the chosen number of layers; user-edited shots and per-pick overrides are kept | `SuggestLayers` → `Edits.SetSegments` |
| **Reset** (asks for confirmation) | Discards all crossovers and overrides of the line, then suggests a new assignment | `ResetLayersAsync` → `Edits.SetSegments` |
| Drag a crossover handle | Sets the crossover to the dragged absolute offset (minimum 0.01 m); the other crossovers of that side are pushed so that the list stays ordered; the shot is marked `UserEdited` | `MoveCrossover` → `Edits.SetGatherCrossovers` |
| Assign the selected pick to a layer | Adds an override for that pick | `AssignSelectedToLayer` → `Edits.SetPickLayer` |
| Clear layer override | Removes the override | `ClearLayerOverride` → `Edits.SetPickLayer(…, null)` |
| Change the number of layers | Updates `LayerCount` (applied with the layered-method settings) | `ApplyMethod` |

Dragging is one gesture ("Move crossover"); a licence that does not allow editing blocks it
(`Context.License.Status.CanEdit`).

---

## 4. Segment fits

`SegmentAssigner.FitAll` fits a straight line t = a + s·|offset| to the included arrivals of every
(shot, side, layer) group with |offset| > 0. The fit uses the line's `LayeredSettings.RegressionMode`:

* `Ordinary` (default) — ordinary least squares, with standard errors of slope and intercept for
  n > 2;
* `Robust` — Huber M-estimate by iteratively reweighted least squares: tuning constant k = 1.345, scale
  1.4826 × the median absolute residual, at most 50 iterations, convergence at a relative change of
  1e-12 in slope and intercept; with fewer than 3 points the OLS fit is returned
  (`src/RefractionStudio.Analysis/Numerics/Regression.cs`).

A group with a single arrival gets no fit. Each `SegmentFit` reports the apparent velocity 1/slope
(NaN unless the slope is positive), the zero-offset intercept time, the point count and the offset
range. These fits feed the intercept-time method ([layered-methods.md](layered-methods.md)) and the QC
checks for segment residuals and trigger timing ([qc.md](qc.md)).
