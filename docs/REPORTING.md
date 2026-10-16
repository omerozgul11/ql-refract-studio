# Reporting and exports

This document covers what Refraction Studio produces when a user generates a PDF report or exports
results:

- the report sections, with their tables and figures;
- how units are handled;
- branding and report templates;
- the XLSX, CSV, figure and audit-JSON exports.

Source files:

| Area | File |
|---|---|
| PDF report content | `src/RefractionStudio.Reporting/ReportGenerator.cs` |
| PDF page layout (pagination, tables, figures, callouts) | `src/RefractionStudio.Reporting/PdfLayout.cs` |
| Result tables (shared by the UI, report, XLSX and CSV) | `src/RefractionStudio.Reporting/ReportTables.cs` |
| Table model, CSV writer, XLSX writer | `src/RefractionStudio.Reporting/Tables.cs` |
| Audit record, table set, figure helper | `src/RefractionStudio.Reporting/Exports.cs` |
| Plot models used by report figures | `src/RefractionStudio.Reporting/PlotModels.cs` |
| Figure rendering to PNG/SVG/PDF | `src/RefractionStudio.Visualization/FigureExport.cs` |
| Report, Results and export commands in the app | `src/RefractionStudio.App/ViewModels/EngineeringResultsReportViewModels.cs` |
| Per-plot export dialog | `src/RefractionStudio.App/Views/ExportFigureDialog.cs`, `src/RefractionStudio.App/Controls/PlotView.cs` |
| Report settings stored in the project | `src/RefractionStudio.Domain/Project.cs` (`ReportSettings`, `CompanyBranding`, `ReportSection`) |
| Report templates | `src/RefractionStudio.Persistence/AppCatalog.cs` (`ReportTemplate`) |
| Units and conversions | `src/RefractionStudio.Domain/Units/Units.cs` |
| Command-line report and export | `tools/RefractionStudio.Cli/Commands.cs` (`ReportAsync`, `ExportAsync`) |

---

## 1. PDF report

### 1.1 How it is produced

`ReportGenerator.Generate(ReportInput, ReportOptions)` returns the PDF as a byte array. The PDF is
written through `PdfLayout` using the SkiaSharp PDF back end (`SKDocument.CreatePdf`). `PdfLayout`
lays out all content before drawing it, so every header and footer can print "Page n of N".

PDF metadata:

- Title: the report title.
- Author: the "Prepared by" value, or the user name if that is empty.
- Subject: the project name.
- Creator: "Refraction Studio".
- Producer: "Refraction Studio (SkiaSharp PDF)".
- Raster DPI: 300.

In the application, the Report page runs `ReportPageViewModel.GenerateAsync`:

1. It stores the report form in the project (`ApplySettings`, an undoable edit).
2. The user picks a file name. The default is the report number, or the project name, with `.pdf`.
3. It re-runs the analysis on the current project state and lays out the PDF on a background thread.
   A progress dialog is shown and the run can be cancelled.
4. It adds a `report.generate` entry with the file name to the project history.

From the command line:

```bash
dotnet run --project tools/RefractionStudio.Cli -- report <project.rfsx> --out <report.pdf> [--units si|us] [--paper a4|letter]
```

With `--units us`, moduli are shown in ksf. If `--paper` is omitted, US units select Letter and SI
units select A4 (`Commands.ReportAsync`).

### 1.2 Options

`ReportOptions` (`ReportGenerator.cs`):

| Option | Meaning |
|---|---|
| `Paper` | `A4` (595.28 × 841.89 pt) or `Letter` (612 × 792 pt). Margins are 54 pt; the header and footer bands are 26 pt each (`PdfLayout`). |
| `Units` | `SI` or `USCustomary` display units. |
| `ModulusUnit` | MPa, GPa, psi or ksf. If it does not belong to the chosen unit system, `UnitConversion.EffectiveModulusUnit` replaces it. |
| `AppVersion`, `UserName`, `GeneratedAt` | Printed for reproducibility. |
| `MaxAppendixPicks` | Maximum pick rows printed in Appendix A (default 2000); all rows are in the XLSX/CSV export. |

In the app:

- The paper size comes from the Report page, which starts from the Settings → Reporting value.
- The units come from the Report page, which starts from the project's `UnitsOverride`, or the user
  setting if there is no override.
- The modulus unit comes from Settings.

### 1.3 Page decoration

Every page except the cover has:

- a header with the company name on the left, the report title and report number on the right, and
  a rule below;
- a footer with the branding footer text on the left ("Generated with Refraction Studio {version}"
  if no footer is set) and "Page n of N" on the right.

Layout rules (`PdfLayout.Table`):

- Each level-1 heading starts a new page.
- Long tables continue across pages and repeat their header row.
- Numbers are right-aligned and never wrapped.
- A shortened table ends with "N further row(s) are in the accompanying spreadsheet."

### 1.4 Sections

The section list is the `ReportSection` enum in `src/RefractionStudio.Domain/Project.cs`. The user
chooses sections on the Report page; if the stored list is empty, every section is included.
"Summary" and "Reproducibility" are always printed. Sections marked "— line" are repeated for each
line in the analysis. Sections appear in this order:

| Section | Toggle | Content (from `ReportGenerator.Generate`) |
|---|---|---|
| Cover | `Cover` | Accent band; logo if set (56 pt high, at most 220 pt wide). Company name, address, phone, e-mail and website. Title and subtitle. Project, Site, Client, Report number, Survey date, Report date, Prepared by (or the branding engineer name), Checked by. The line "Prepared with Refraction Studio {version}". The cover has no header or footer. |
| Summary | always | A callout "Analysis quality: Good / Review recommended / Incomplete" with the QC reasons. One bullet per line: included shots, first arrivals used, layered method and layer velocities, the depth range of each refractor, and the tomography RMS (marked "(out of date)" if stale). One bullet per Vs30 position: the value and qualifier, or the reason no value is reported. |
| Reproducibility | always | Software version; when (UTC) and by whom the report was generated; when the analysis was computed; active scenario; a units statement ("calculations in SI"); number of history entries. |
| Project information | `ProjectInformation` | Project, project number, client, client contact, site, site address, survey date, field crew, conditions, prepared by, checked by, engineer / geophysicist, registration, and the project description. Empty values are left out. |
| Survey configuration | `SurveyConfiguration` | Instrument, geophones and source. Table "Survey lines": line, wave type, included shots, receivers, minimum spacing, line length, and topography ("Flat (none supplied)" or the number of surveyed points). Table "Raw data files" (first 60 rows). |
| Geometry — line | `Geometry` | Spread figure showing the ground profile at 5× vertical exaggeration, geophones and shots. Table "Shot points": shot, x, ground elevation, source depth, records (combined or single), included, comment. |
| Processing and picking — line | `ProcessingMethodology` | A statement that raw records are not modified. A numbered list of the enabled processing steps with parameters. A warning callout for each item from `PipelineRunner.Advice`. The shot-time correction used (none, manual, or a reference geophone with its reference velocity if set), plus the shot-time table when a correction is applied. Pick counts by origin (manual, accepted automatic, automatic not reviewed, cross-correlation, excluded), the picker algorithm, and a sentence on what the confidence score combines. |
| Shot gathers — line | `ShotGathers` | Gather figures with picks, segment fits and the tomography prediction. Unless "include all gathers" is set, three representative gathers are shown (first, middle and last by shot position) and the report says how many are shown. If a gather cannot be read, a "Gather not available" callout replaces it and the report still completes. |
| Travel-time curves — line | `TravelTimes` | Travel-time figure with corrected times, layer markers, excluded picks, QC markers, segment fits, tomography prediction and reciprocal-time bars. Table "Travel-time segments" (first 80 rows). |
| Layered interpretation — line | `LayeredModel` | Method name and layer count. Table "Layer velocities". Layered section figure at 2× vertical exaggeration. Table "Refractor depths" (first 120 rows). Up to 25 interpretation warnings as callouts. If there is no layered model, a callout gives the reason. |
| Travel-time tomography — line | `Tomography` (only when a tomogram exists) | Method; grid (Nx × Nz cells of Dx × Dz m); starting model; λ and its decay; horizontal/vertical smoothing ratio; pick uncertainty (absolute ms plus relative %); iterations; final RMS; χ²; stop reason. An "Out of date" callout if the tomogram is stale, plus any notes. Figures: the tomogram (unconstrained cells hatched, refractors overlaid), ray coverage, and the convergence plot. |
| Velocity profiles | `VelocityProfiles` | 1-D Vp/Vs profile figures (35 m depth axis) at the engineering profile positions, or at the Vs30 positions if none are set, using the P and SH lines chosen in the engineering settings. Where a profile is unavailable, the reason is given. |
| Vs30 and time-averaged shear-wave velocity | `Vs30` | The definition and table "Time-averaged shear-wave velocity". An "EXTRAPOLATED values" callout if any Vs30 value includes extrapolation, and an "Insufficient depth" callout if any position has no value. |
| Engineering parameters | `EngineeringParameters` | How density was obtained (measured intervals, or ESTIMATED with the estimator name). Table "Engineering properties" (first 200 rows). A Poisson's-ratio section figure when both a P and an SH tomogram exist. |
| Quality control | `QualityControl` | Verdict callout, the "Quality summary" card table, and the "Quality control findings" table (first 150 rows). |
| Processing notes | `ProcessingNotes` | Free text from the Report page; printed only if not empty. |
| Warnings and limitations | `Warnings` | Bulleted warnings from `ReportGenerator.CollectWarnings`, then the fixed `Limitations` statement (§1.6). |
| Conclusions | `Conclusions` | Free text, or "No conclusions were entered." |
| Appendix A — Pick tables | `PickTableAppendix` | A legend of the origin codes and the corrected-time formula, then per line one compact table per shot (in shot order): see §1.5. At most `MaxAppendixPicks` rows in total; further picks are replaced by a note pointing to the XLSX/CSV export. |
| Appendix B — Processing history | `HistoryAppendix` | The full project history table. |

### 1.5 Tables

All tables are built in `ReportTables.cs`. The XLSX and CSV exports (§4) use the same table objects.
Units are shown in the column headers.

| Table | Columns |
|---|---|
| Shot picks (report Appendix A; `ReportTables.ShotPicks`) | Title "Shot <label> — shot correction <value> ms" (or "not applied"). Columns: Ch, x, Offset, Raw (ms), Corrected (ms), Layer, Origin (short code: Auto, Accepted, Manual, Correlation), Confidence (level and score), SNR, Used (Yes/No), Note (trace shift, exclusion reason and comment). Rows sorted by receiver x. |
| Picks — line (XLSX/CSV export) | Shot, Shot x, Channel, Receiver x, Offset, Raw pick (ms), Shot correction (ms), Timing shift (ms), Corrected time (ms), Layer, Origin, Algorithm, Confidence, Confidence score, SNR, Included, Reason excluded, Comment. Corrected time = raw + shot correction + timing shift. Rows are sorted by shot x, then by receiver x. |
| Travel-time segments — line | Shot, Direction (decreasing or increasing x), Layer, Arrivals, Apparent velocity, Intercept (ms), R², RMS (ms), Min offset, Max offset. |
| Shot-time corrections — line | Shot, Method, Reference pick (ms), Expected (ms), Correction (ms) or "n/a", Note. |
| Layer velocities — line (method) | Layer, Velocity, Std. error, Basis. |
| Refractor depths — line | Refractor, x, Depth, Elevation, Depth std. error, Delay time (ms), Arrivals, Flag ("Review" for questionable points). |
| Time-averaged shear-wave velocity | x, Quantity (Vs30 — or VsZ for another averaging depth, e.g. Vs20 — or a custom interval), Velocity, Vertical travel time (ms), Constrained depth, Status (qualifier), Model. Where no value can be computed, the row shows "Not available" and the reason. |
| Engineering properties | x, Depth, Vp, Vs, Vp/Vs, Poisson's ratio, Density, G, E, K, Flags (property flags plus "ESTIMATED density"). The table note gives ν = (Vp² − 2Vs²)/[2(Vp² − Vs²)], G = ρVs², E = 2G(1+ν), K = ρ(Vp² − 4Vs²/3). |
| Quality control findings | Status (OK / Review / Likely problem), Finding, Line, Details. Most severe first. |
| Raw data files | File, Format, Bytes, SHA-256, Imported. |
| Processing history | Time (UTC), Category, Change, User, Version, Parameters (`key=value`, sorted by key). |

Printed labels:

- Pick origins (`ReportTables.OriginText`): "Manual", "Automatic (not reviewed)", "Automatic,
  accepted", "Cross-correlation".
- Layered methods (`ReportTables.MethodName`): "Intercept-time", "Plus–minus (Hagedoorn)",
  "Generalized reciprocal method (Palmer)", "Delay-time (time-term) least squares".
- Missing or non-finite numbers are printed as "—".

### 1.6 Warnings and the limitations statement

`ReportGenerator.CollectWarnings` lists, for each line:

- an unavailable layered model, with the reason;
- every layered-model warning;
- a stale tomogram, and any tomography notes;
- processing advice from `PipelineRunner.Advice`;
- how many included automatic picks were not individually reviewed;
- a note if the user never edited the layer assignment;

and for the whole project:

- density estimated from Vp;
- Vs30 extrapolation turned on by the user;
- up to 20 QC findings of level "Problem" that are not tied to a single pick.

The `ReportGenerator.Limitations` text is printed whenever the Warnings section is included. It
states that results depend on data quality, geometry, picks and interpretation choices; that the
software does not replace professional judgement; that the refraction method assumes velocity
increases with depth and cannot detect low-velocity or hidden layers; that layered interpretations
assume laterally continuous layers and tomograms are smooth, non-unique models; and that compliance
with standards such as ASTM D5777 is the user's responsibility.

### 1.7 Figures in the report

Report figures use `PlotTheme.Print` and the same renderers as the screen: `GatherRenderer`,
`TravelTimeRenderer`, `SectionRenderer`, `SpreadRenderer` and `XyRenderer`. They are drawn into the
PDF as vector content, each with an italic caption. Figure data comes from `PlotModels` (`Gather`,
`TravelTimes`, `Section`, `Spread`, `Profiles`, `Convergence`, `PropertySection`). Sections are
drawn at 2× vertical exaggeration and the geometry figure at 5×.

---

## 2. Units

All calculations are done in SI. Values are converted only for display
(`src/RefractionStudio.Domain/Units/Units.cs`, `UnitFormatter`). The conversion factors are exact:
international foot 0.3048 m, avoirdupois pound 0.45359237 kg, standard gravity 9.80665 m/s².

| Quantity | SI | US customary |
|---|---|---|
| Length, depth, elevation | m | ft |
| Velocity | m/s | ft/s |
| Time | ms | ms |
| Density | kg/m³ | pcf |
| Modulus | MPa or GPa | psi or ksf |
| Slowness | ms/m | ms/ft |

If the modulus unit does not belong to the selected system, it is replaced: MPa for SI, ksf for US
customary (`UnitConversion.EffectiveModulusUnit`).

Labels in the report and the exports — including the tomography grid description, custom-interval
names, profile-figure axes and section legends — use the display units. The Vs30 definition itself
(30 m) does not depend on the display units; the quantity label follows the chosen averaging depth
(Vs30, Vs20, …).

---

## 3. Branding and templates

### 3.1 Branding

`CompanyBranding` (`src/RefractionStudio.Domain/Project.cs`) holds the company name, address, phone,
e-mail, website, footer, engineer name, registration, and a logo stored as base64 PNG.

User branding:

- The user enters it in Settings → Reporting. It is stored in the user settings
  (`UserSettings.Branding`, `src/RefractionStudio.Infrastructure/AppEnvironment.cs`).
- The logo can be a PNG or JPEG of up to 2 MB. It is decoded and saved as PNG
  (`SettingsViewModel.ChooseLogoAsync`).

Project branding:

- Each project has its own copy in `ReportSettings.Branding`.
- If the project's company name is empty when the report form is applied, the user branding is copied
  in (`ReportPageViewModel.Build`).
- "Use my company branding" on the Report page replaces the project branding with the user branding.

If the logo cannot be decoded, the cover leaves it out and still prints the company name.

### 3.2 Report settings stored in the project

`ReportSettings` holds the branding; the title (default "Seismic Refraction Survey Report") and
subtitle; the report number, prepared by and checked by; processing notes and conclusions; the
selected sections and the "include all gathers" option; and an optional units override. These
settings are saved in the project file; changes are undoable and recorded in the history.

### 3.3 Templates

A report template (`ReportTemplate`, `src/RefractionStudio.Persistence/AppCatalog.cs`) stores a name,
the branding and the selected sections. It does not store the title, report number, notes or
conclusions.

- Templates are saved in the user's catalogue database (`catalog.db`, table `report_templates`, in
  the application data folder), so they are available in all of that user's projects.
- "Save as template" uses the name the user typed, or "{company} report" if none was typed.
- "Apply" sets the section toggles and the project branding from the template.

---

## 4. Exports

### 4.1 Result tables: XLSX and CSV

`ExportTables.All` (`src/RefractionStudio.Reporting/Exports.cs`) collects these tables, with all rows
and in the user's display units (contents as in §1.5):

- For each line: Picks, Travel-time segments and Shot-time corrections; Layer velocities and Refractor
  depths when a layered model exists.
- When available: Time-averaged shear-wave velocity and Engineering properties.
- Always: Quality control findings, Raw data files and Processing history.

XLSX (`XlsxWriter`, `src/RefractionStudio.Reporting/Tables.cs`) is a small built-in SpreadsheetML
writer with no external dependencies:

- One worksheet per table. Sheet names are the table titles with `[ ] : * ? / \` removed, cut to
  31 characters, and made unique ("name (2)", …).
- The header row is bold, shaded and frozen. Column widths follow the content (8–60 characters).
- Numbers are stored at full precision (round-trip `"R"` format). The display format is applied as a
  cell number format, so rounding affects display only.
- The document creator is set to the user name.

CSV (`CsvWriter`):

- One file per table into a folder the user chooses.
- Quoting follows RFC 4180: a field containing a comma, quote or line break is quoted, and quotes
  inside it are doubled.
- Invariant culture, numbers in round-trip format, UTF-8 with a BOM.
- File names are the table titles with non-alphanumeric characters replaced by `_`, made unique with
  `_2`, `_3`, ….

### 4.2 Audit record (JSON)

`AuditExport.Create` writes a machine-readable record of how the results were produced:

- generation time, user, and the software name and version;
- the project (name, number, client, site, schema version);
- the active scenario (name, description, creation time);
- the raw files: name, SHA-256, length, format and import time;
- for each line: shots (label, x, depth, included, combine mode, record count, comment); enabled
  processing steps and their parameters; excluded traces with notes, and trace adjustments that change
  something (gain, polarity, time shift, note); shot-time method and reference velocity; picker
  settings; pick counts by origin and the excluded picks with reasons; a layer-assignment summary
  (layer count, number of user-edited gathers, number of overrides); the layered settings, whether a
  model is available (and why not), and the layer velocities; tomography settings, input fingerprint,
  stale flag, iterations, stop reason, notes, and the application version and time of the computation;
- engineering, density and Vs30 settings;
- the QC verdict, reasons, and the number of "Problem" and "Review" findings;
- the full history: time, kind, operation, description, parameters, user and version.

The audit record is written by Results → Export audit record, by the CLI `export` command, and by
Archive on the Project page (`ProjectWorkspace.Archive` writes a ZIP with the last saved project
file, `audit.json` and a `README.txt`; unsaved changes are not in the archived project file).

### 4.3 Figures

Any plot: the plot's context menu (`PlotView`) has two export items.

- **Copy image** copies a 2× PNG in the print theme to the clipboard.
- **Export figure…** opens `ExportFigureDialog`, which offers: format PNG (raster), SVG (vector) or
  PDF (vector); width and height from 200 to 8000 (points for vector formats, logical pixels for PNG;
  defaults are the on-screen size, at least 800 × 500); resolution 1×, 2× or 3× (PNG only); a
  transparent background (PNG only); and a dark theme (the default is the white print theme). The
  figure is drawn at the current zoom.

Results → Export figures writes, for each line, in the print theme:

- `<line>_traveltimes.png` and `.svg` (1200 × 700; PNG at 2×);
- for the layered and the tomography section, when available, `<line>_layered.png` and
  `<line>_tomography.png` (1400 × 620 at 2×) plus matching `.pdf` files (1000 × 440 pt).

The CLI `export` command writes into `<out>/figures/`: `<line>_traveltimes.png` and `.svg`;
`<line>_tomogram.png` (1400 × 600) and `.pdf` (1000 × 430) when a tomogram exists; and a PNG of the
first included gather.

All figure output goes through `FigureExport.Render` (`src/RefractionStudio.Visualization/FigureExport.cs`):
PNG is drawn on a Skia raster surface scaled by the resolution factor, SVG uses `SKSvgCanvas`, and
PDF is a one-page Skia PDF document.

### 4.4 Command-line export

```bash
dotnet run --project tools/RefractionStudio.Cli -- export <project.rfsx> --out <folder> [--units si|us]
```

writes `results.xlsx`, `csv/*.csv`, `audit.json` and `figures/` into the folder
(`Commands.ExportAsync`).

### 4.5 Scenario comparison

Results → Compare scenarios analyses every scenario in the project (`ScenarioComparer.Compare`,
`src/RefractionStudio.Core/Services/WorkflowStatus.cs`). For each line it shows layer velocities,
mean refractor depths, the layered RMS residual, the tomography RMS and the Vs30 values, in the
display units. The comparison is shown on screen only and is not part of the
XLSX/CSV export.

---

## 5. Licence and exports

Report generation and the exports do not check the licence. If the licence does not allow editing,
saving changed report settings into the project is refused with a message (all project edits go
through `StudioContext.Edit`), and the PDF is still generated with the report settings already
stored in the project. See [SECURITY.md](SECURITY.md).

---

## 6. Tests

`tests/RefractionStudio.Reporting.Tests/ReportTests.cs` checks that:

- the sample project produces a complete PDF (header `%PDF-`, trailer `%%EOF`, 20–400 pages);
- reports in US customary units on Letter paper are generated;
- deselecting sections makes the report shorter;
- a project with no data still produces a report;
- the XLSX has one uniquely named sheet per table, with numbers at full precision;
- CSV escaping is correct;
- the audit JSON is valid and carries the raw-file SHA-256 hashes.

See [TESTING.md](TESTING.md).
