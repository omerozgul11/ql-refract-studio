# Refraction Studio — User Guide

Refraction Studio processes, picks, interprets and reports seismic refraction surveys (P-wave and
SH-wave). This guide walks through the workflow, explains each step and ends with a worked example
using the sample project that ships with the application.

> Refraction Studio is a tool for qualified users. Results depend on the field data, the geometry,
> the picks and the interpretation choices. The software does not replace professional judgement and
> does not certify compliance with any standard.

---

## 1. The window

| Area | Purpose |
|---|---|
| **Header** (top, dark band) | Left: the QuakeLogic logo and product name (click to return to the start page), then New, Open, Save, Undo, Redo, Import, Export (opens the Results page with the export buttons) and Report (opens the Report page). Right: **Search commands** (the command palette), the **Guided / Expert** selector, Settings and Help. |
| **Sidebar** (left) | The project name and its save status, then the workflow: the steps of an interpretation. Each step shows its status: ✓ complete, ● in progress, ! needs attention, ○ not started. Click any step at any time; nothing is enforced. |
| **Workspace** (centre) | Plots, tables and settings of the current step. |
| **Trace inspector** (right, Waveforms and Picks pages) | Properties of the selected trace (display gain, polarity, exclusion, static time shift, note) and its pick (accept, delete, use / exclude, restore the automatic suggestion, propagate to neighbours). The command palette entry *Toggle inspector panel* hides or shows it. |
| **Status bar** (bottom) | Messages, progress of long operations (with **Cancel** for operations that can be cancelled), and the application version. |

When you hover over a gather, the line under the plot shows the trace's channel, position, amplitude,
time, pick and QC findings.

**Command palette — Ctrl+K** (or **Search commands** in the header). Type part of a command name and press
Enter. The palette lists the file and edit commands (New, Open, Save, Save as, Import, Undo, Redo),
*Generate report* (opens the Report page), Settings, Help, *Explore sample project*, the
Guided / Expert switch, *Toggle inspector panel*, *Go to* any workflow page, and every help topic
(*Help: …*). It shows up to 14 matches. Settings and exports are not listed; use Settings and the
Results page.

**Guided / Expert mode** (selector in the header). Guided mode shows a short guidance box on each page that
explains the step; Expert mode hides it. All parameters are available in both modes, and the project
is the same in both.

### Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S | New / Open / Save / Save As |
| Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) | Undo / Redo (everything: picks, filters, geometry, layers, exclusions) |
| Ctrl+I | Import |
| Ctrl+R | Open the Report page |
| Ctrl+K | Command palette |
| F1 | Help for the current page |
| Esc | Close the palette, help or settings |

In a shot-gather plot (click the plot first so that it has the keyboard focus):

| Keys | Action |
|---|---|
| ↑ / ↓ | Select the previous / next trace |
| Page Up / Page Down | Previous / next shot |
| ← / → | Move the selected pick by one sample (Shift: ten samples) — P picks and S picks pages |
| Enter | Accept the automatic pick of the selected trace — P picks and S picks pages |
| Delete or Backspace | Delete the selected pick — P picks and S picks pages |

Mouse in plots: the wheel zooms (time axis in gathers; Ctrl: the other axis; Shift: both axes); drag to pan (on the
picks pages a left click on a trace places a pick, so pan there with the right or middle button);
**Fit all** in the plot's right-click menu resets the view. Double-clicking empty plot space also fits
the view; in a gather, double-clicking a trace selects it instead.

---

## 2. Projects

A project (`.rfsx`) contains the original data files byte-for-byte (with their SHA-256 checksums),
the geometry, the processing, picks, interpretations, results and the complete history of changes.
Raw data are never modified.

* **Save / Save As** write the whole project atomically: a failure never leaves a damaged file.
* **Recovery checkpoints** are written at the interval set in Settings → General (default 3 minutes);
  your project file changes only when you save. After an unexpected exit, Refraction Studio offers to
  **Recover**, **Discard** or decide **Later**.
* **Snapshots** (Project page) store named versions of the project state inside the project; **Restore**
  returns to one (undoable).
* **Scenarios** (Project page) are alternative interpretations of the same data — for example manual
  picks versus automatic picks. Create one by duplicating the active scenario, switch with **Activate**,
  and compare them on the Results page.
* **Duplicate project…** saves an independent copy under a new name.
* **Archive project…** writes a ZIP with the project file as last saved, a machine-readable audit
  record (`audit.json`) and a `README.txt`. Save the project first: unsaved changes are not in the
  archived project file.
* The **processing history** on the Project page lists every change with its parameters, time, user
  and software version.

---

## 3. Import

Drag files or a folder onto the Import page, or use **Import** (Ctrl+I) and **Choose files…** /
**Choose folder…**. SEG-2 (`.sg2`, `.seg2`, `.dat`) and SEG-Y (`.sgy`, `.segy`) are supported.

The list shows, before anything is added to the project, each file's format, number of records and
traces, source position and recording date. **Validation** lists findings as *Error*, *Warning* or
*Information*: unreadable files, duplicate files or records, different sample rates or record
lengths, missing or duplicate channels, flat (dead) or clipped traces, suspicious delays, repeated
source positions and unexpected acquisition order.

Nothing is repaired silently. Some findings offer a correction (for example *Skip this file* or *Do not
import this trace*); tick the corrections you approve. Skipped traces are imported but marked as
excluded, so they stay visible.

Choose the target (a new survey line or an existing line), the wave type (P or SH), an optional spread
profile, the geophone spacing used when the headers give no receiver positions, and how records at
the same shot position are combined:

* **Separate** — one shot gather per record;
* **Stack** — records at the same position are averaged (vertical stack);
* **Opposite SH impacts** — left and right impacts are identified by the sign of their correlation and
  combined as (left − right)/2, which keeps shear energy and cancels P energy. This is a suggestion:
  check it in Geometry.

A new line receives your default processing pipeline (Settings → Processing and picking). New P lines
use the default picker from the same settings; new SH lines use the SH polarity-divergence picker.

---

## 4. Geometry

Each line has its own geometry (P and SH lines are independent).

* **Spread and channels**: first geophone position, geophone spacing and channel order (**Apply
  layout**). Positions recorded in the file headers are used automatically. Per channel you can set the
  role, an x override, an elevation and polarity reversal.
* **Channel roles**: receiver, reference (shot / trigger geophone), auxiliary or unused. Save the
  configuration as a **spread profile** to reuse it (**Apply channel roles**).
* **Shots**: position along the line, elevation (empty = from the ground profile), source depth below
  ground, and whether the shot is used. For shots with several records, **Stack / single** switches
  between combining the records and using the first one only, and **Flip last record** reverses the
  polarity of the last record before combining (SH left / right pairs). Press **Apply** after editing
  a row.
* **Ground profile**: enter or paste surveyed points (x, elevation), or **Import CSV…**. Elevations of
  geophones and shots are interpolated linearly; beyond the ends the end elevation is held constant.
  Leave the list empty for flat ground at elevation 0.

The line diagram shows the ground profile, geophones and shots. Changes update every result.

---

## 5. Waveforms and processing

The gather view shows receiver position across and time downwards.

* Display: wiggle, variable area, wiggle + variable area, or image; per-trace or common scaling; gain
  and clip; a **reduction velocity** displays t − |offset| / V to flatten refracted arrivals. Picks are
  always stored in true time.
* **Original / Processed / Overlay** — the label above the plot states which one you see.
* Hover over a trace for its channel, position, amplitude, time, pick and QC findings; click it to
  select it and edit its properties (display gain, polarity, exclusion, static time shift, note) in the
  trace inspector.
* **Show the amplitude spectrum of this shot** opens the spectrum panel.

**Processing** (Processing page) is a list of steps applied in order (enable, edit, reorder, remove —
all undoable): demean, detrend, taper, high/low/band-pass Butterworth (order; causal or zero-phase),
notch, normalise, trace balance, AGC, polarity reversal, time shift and resampling. **Copy this
pipeline to all other lines…** reuses it. The **Spectrum** shows the amplitude spectrum, dominant
frequency and −20 dB band, the response of your filters, and suggests band-pass corners (**Add
suggested band-pass**).

> **Filtering and first breaks.** Zero-phase filters spread energy ahead of the true onset (picks can
> become early); causal low-pass filters delay arrivals (picks become late). The **Advice** box warns
> about both: for a causal low-pass or band-pass it states the approximate delay in milliseconds; for
> zero-phase filters, notch filters and the anti-alias filter of resampling (both zero-phase) it warns
> about possible precursors. For picking, prefer a causal high-pass to remove surface waves and little
> else.

---

## 6. Shot-time (trigger) correction

If the trigger time varies between shots, correct it on the **Shot time (trigger) correction** tab of
the Processing page:

* **Reference geophone** — the onset on the channel with the role *Reference* (Geometry) defines time
  zero. **Pick reference onsets automatically** picks it on the raw, unfiltered reference trace of
  every shot; you can also pick it manually. If you enter the velocity between shot and reference
  geophone **and** the reference channel has an x position set in Geometry (x override), the expected
  travel time from the shot to the reference geophone is taken into account; otherwise the reference
  onset is taken as time zero. A shot without a reference pick cannot be corrected and is excluded,
  with the reason shown.
* **Manual** — enter a correction per shot.

Raw times, corrections and corrected times are all kept and reported.

---

## 7. Picking first arrivals

The **P picks** and **S picks** pages have the same layout: gather, trace inspector, pick table and
travel-time plot.

* **Pick automatically** runs the selected picker on the whole line, or only on the shot shown when
  **Current shot only** is ticked (off by default; the option also limits acceptance and the pick
  table). Pickers: AIC, STA/LTA, modified energy ratio, and the SH polarity-divergence picker for
  opposite-impact SH gathers. The search window follows the minimum / maximum apparent velocities and
  the window margin in **Picker settings**; **Guide by neighbouring picks** uses neighbouring picks to
  steer the window. **Save as line defaults** stores the settings for the line.
* Every automatic pick has a **confidence** (High / Medium / Low) with its reasons:
  signal-to-noise, onset sharpness, consistency with neighbouring traces and agreement between pickers
  (or of the two SH impacts).
* **Accept high-confidence** marks high-confidence automatic picks as reviewed; **Accept this shot**
  accepts all picks of the shot; **Delete shot picks…** removes them.
* **Manual picking**: click a trace to place a pick (Shift-click, or **Snap clicks to the onset**, moves
  it to the nearest onset found by the picker), drag a pick to move it (a magnifier appears), ← → to
  nudge, Enter to accept, Delete to remove. Manual picks are never replaced by automatic picking; the
  automatic suggestion is kept and shown as a thin dashed mark, and **Restore automatic** in the
  inspector brings it back.
* **Propagate to neighbours** (inspector) tracks a reviewed pick to the neighbouring traces by
  cross-correlation while the waveforms correlate.
* **Pick table**: shot, channel, position, offset, pick time, shot correction, corrected time, layer,
  origin, confidence, SNR, used, status and comment. Selecting a row selects the trace; the pick time,
  *Used* and the comment can be edited in the table. Clicking a point in the travel-time plot shows its
  trace.

---

## 8. Quality control

The QC page summarises the analysis with cards (imported records, shot gathers, missing measurements,
P / SH picks accepted, low-confidence picks, trigger corrections, excluded measurements, depth
coverage, Vs30 availability, model RMS error) and an overall **Analysis quality** (Good, Review
recommended, Incomplete) with its reasons.

Findings include low SNR, weak onsets, travel times decreasing with offset, picks inconsistent with
their segment, possible air waves (P lines), possible trigger problems, reciprocal-time differences,
unconfirmed SH arrivals, unpicked traces, dead traces, possibly noisy or clipped traces, possible
polarity reversals, model residuals and out-of-date models. Each finding has a colour **and** an icon
and text. Clicking a finding selects it and shows what to check; double-click it, or press **Show in
the data**, to open the page, shot and trace it refers to.

The thresholds used by quality control are fixed defaults; they cannot be changed in the application.

---

## 9. Velocity model

### Layer assignment

On the travel-time plot, arrivals are assigned to layers (1 = direct wave). **Suggest layer
assignment** fits straight segments to each shot for the chosen number of layers (crossovers you moved
are kept); **Reset…** discards your changes. Choose a shot to show its crossovers as draggable
markers. To change a single arrival, click its point and use **Assign to layer: 1 / 2 / 3 / 4**,
**Automatic** (back to the automatic assignment), **Use / exclude** or **Show trace**. The page lists
the layer velocities; the apparent velocity, intercept and fit of each segment are in the exports
(XLSX / CSV) and the PDF report.

### Layered methods

* **Intercept-time** — planar layers; exact two-layer dip with a reversed pair; depths beneath shots.
* **Plus–minus** — reversed pair; refractor velocity and depth at the geophones between the two shots
  that both shots' arrivals cover.
* **GRM** — like plus–minus with an XY distance; **Analyse XY** suggests the optimum.
* **Delay-time** — least squares over all shots; lateral refractor-velocity segments; uncertainties.

The forward and reverse shots for plus–minus and GRM are chosen automatically unless you select them.
Depths are converted by layer stripping. Results include layer velocities, refractor depths and
elevations, and warnings (for example negative thicknesses). If the velocities decrease with depth
(velocity inversion) the layered result is not computed and the reason is shown instead. **Check by
forward modelling** computes travel times through the layered model by ray tracing and compares them
with the picks.

### Tomography

**Run tomography** computes a smooth velocity section from all picks (shortest-path ray tracing,
regularised Gauss–Newton). Settings: cell size and model depth (or **Choose the grid automatically**),
starting model, smoothing and its horizontal / vertical ratio, maximum iterations (up to 50), pick
uncertainty, velocity limits and **Down-weight outlying picks (robust)**. The result shows the
velocity section with contours, ray coverage (**Show rays**) and the convergence; the modelled travel
times are drawn on the travel-time plot. **Hatched areas are not constrained by the data and must not
be interpreted**: cells below the depth of investigation of their column, and columns that no ray
crosses. The settings are stored when you run the tomography. Changing picks or geometry after a run
(or the layered model, when it is the starting model) marks the tomogram *out of date* until it is run
again.

---

## 10. Engineering results

* **Velocity profiles** at chosen positions (Vp, Vs) with the depth constrained by data. Choose the SH
  and P lines and the model used (tomography or layered).
* **Time-averaged shear-wave velocity** — Vs30, or VsZ for another averaging depth (the label follows
  the depth, e.g. Vs20), and additional depth intervals. A value is reported only where the SH model is
  constrained to the averaging depth; otherwise the reason is shown. Extrapolation can be enabled
  deliberately (**Allow extrapolation below the model**); extrapolated values are labelled
  **EXTRAPOLATED** everywhere.
* **Density** — measured / assumed values by depth interval, or an explicitly selected empirical
  relation (Gardner 1974, valid 1500–6100 m/s; Brocher 2005, valid 1500–8500 m/s), labelled
  **ESTIMATED**.
* **Engineering parameters** — Vp/Vs, Poisson's ratio, G, E and K per depth, with flags for physically
  inconsistent combinations; property sections where tomographic models of a P and an SH line overlap.

Units are chosen in Settings (SI or US customary). Calculations always use SI internally.

---

## 11. Report and exports

**Report** (header, Ctrl+R) opens the Report page. Fill in the report details, choose the sections,
units and paper size, and press **Generate PDF report…**. The report contains: cover, summary,
reproducibility, project and survey information, geometry, processing and picking methodology, shot
gathers, travel-time curves, layered model, tomography, velocity profiles, Vs30, engineering
parameters, quality control, notes, warnings and limitations, conclusions, and appendices (pick tables
per shot, processing history). The report never writes conclusions for you.

Branding (company, logo, address, engineer, registration, footer) is entered in Settings → Reporting;
**Use my company branding** on the Report page applies it to the project. A **template** on the Report
page stores the branding and the selected sections for reuse in other projects.

**Export** (header) opens the Results page, where separate buttons write all tables to an Excel
workbook (XLSX) or CSV files, the audit record (JSON) and figures (travel-time curves as PNG and SVG;
layered and tomography sections as PNG and PDF). Every plot can also be exported to PNG, SVG or PDF
(size, resolution, background and theme) or copied to the clipboard from its right-click menu.

**Compare scenarios** on the Results page analyses every scenario and shows the differences in your
display units.

---

## 12. Licence

Refraction Studio 1.0 is the full version on every installation: every feature is available, with no
trial, no licence key and no activation, and no internet connection is needed. The only network
access is the optional update check (Settings → Updates), which downloads a signed release manifest
from the public Refraction Studio GitHub repository.

Use of the software is governed by the end-user licence agreement shown by the installer (also in the
client package under *Licence and notices*).

---

## 13. Example workflow — the sample project

On the start screen choose **Explore the sample** (command palette: *Explore sample project*). It is
generated from a synthetic earth model (no real site): soil (Vp 450 / Vs 210 m/s) over a saturated
weathered layer (Vp 1650 / Vs 420 m/s) over bedrock whose velocity increases with depth, with
undulating interfaces and topography; a 48-channel spread at 3 m and seven shots, recorded with P and
SH sources.

1. **Import** — already done: seven P records with a shot geophone on channel 49; fourteen SH records
   (left and right impacts) combined into seven gathers.
2. **Geometry** — positions came from the file headers; the surveyed ground profile was entered.
3. **Processing** — demean and a causal 15 Hz (P) / 6 Hz (SH) high-pass to suppress surface waves.
4. **Shot time** — corrected from the shot geophone: shots S2, S4 and S7 had trigger errors of
   2.5, −1.5 and 2 ms, which the correction removes (see the table on the shot-time tab of
   Processing).
5. **Picking** — automatic picks; high-confidence picks were accepted, the rest are left for review.
   Picks that quality control marked as inconsistent with their refractor segment were excluded after
   review; they remain in the pick table with a comment. Open **QC** to see the dead channel 30 on
   shot S3, the reversed geophone on channel 17 and the picks still awaiting review.
6. **Velocity model** — three layers assigned automatically; delay-time interpretation; tomography of
   both lines. Compare the layered refractors (dashed) with the tomogram contours.
7. **Engineering** — Vs30 at 30, 70.5 and 110 m (true values of the synthetic model: 553, 581 and
   659 m/s; the sample's tomography gives 572, 600 and 676 m/s); illustrative densities give Poisson's
   ratio and moduli.
8. **Report and Results** — generate the PDF on the Report page; export the XLSX tables from the
   Results page.

Try changing a pick, a filter or a crossover and watch results update; use Undo to go back.
