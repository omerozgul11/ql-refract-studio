# Project file (`.rfsx`)

A Refraction Studio project is a single `.rfsx` file: a ZIP container holding the project state as
JSON, the audit history, named snapshots, serialised tomography results and byte-for-byte copies of
every imported raw data file. This document describes the container, how it is saved and verified,
schema versioning, and the crash-recovery checkpoints kept outside the project.

Sources:

| Topic | File |
|---|---|
| Container read/write, manifest, migrations | `src/RefractionStudio.Persistence/ProjectContainer.cs` |
| Crash-recovery checkpoints and staged raw files | `src/RefractionStudio.Persistence/RecoveryStore.cs` |
| Per-user catalogue (recent projects, profiles, templates) | `src/RefractionStudio.Persistence/AppCatalog.cs` |
| Tomography result store and binary format | `src/RefractionStudio.Core/Services/ResultStore.cs` |
| Save, open, checkpoint, recover, archive | `src/RefractionStudio.Core/ProjectWorkspace.cs` |
| Raw file sources (staged, embedded, in memory) | `src/RefractionStudio.Core/Data/TraceStore.cs` (`RawFileRepository`) |
| JSON conventions | `src/RefractionStudio.Domain/DomainJson.cs`, `Ids.cs` |
| Project state model | `src/RefractionStudio.Domain/Project.cs`, `Survey.cs`, `Interpretation.cs` |
| Per-user folders | `src/RefractionStudio.Infrastructure/AppEnvironment.cs` (`AppPaths`) |
| Autosave timer | `src/RefractionStudio.App/Services/Services.cs` (`AutosaveService`) |
| Tests | `tests/RefractionStudio.Core.Tests/WorkspaceTests.cs` |

---

## 1. Container layout

```
manifest.json                 container manifest (section 2)
project.json                  ProjectState (section 3)
history.json                  audit history: array of HistoryEntry
snapshots/<snapshotId>.json   ProjectState of each retained snapshot
raw/<sha256>/<file name>      original imported files, byte-identical
results/<resultId>.bin        serialised tomography results (section 5)
```

* Identifiers in entry names are GUIDs written as 32 lower-case hex digits without hyphens
  (`Guid.ToString("N")`).
* `<sha256>` is the lower-case hex SHA-256 of the raw file; `<file name>` is its original name
  (`SourceFile.ContainerPath`).
* Compression: raw files are **stored** (no compression); JSON entries use `Optimal`; result blobs
  use `Fastest`.
* The format id is `quakelogic.refraction-studio.project` (`ProjectContainer.FormatId`); the
  extension is `.rfsx` (`ProjectContainer.Extension`).

Each raw file is written once per SHA-256, even if several `SourceFile` entries were to share it
(the importer already refuses duplicate content).

---

## 2. Manifest (`manifest.json`)

`ContainerManifest`, serialised with the project JSON settings (section 3.1):

| Property | Content |
|---|---|
| `format` | `quakelogic.refraction-studio.project` |
| `schemaVersion` | Schema version of `project.json` (from `ProjectState.SchemaVersion`) |
| `appVersion` | Version of the application that wrote the file |
| `createdAt` | Project creation time (`ProjectMetadata.CreatedAt`) |
| `savedAt` | Time of this save (UTC) |
| `projectSha256` | Lower-case hex SHA-256 of the exact bytes of `project.json` |
| `rawFiles` | Object mapping each raw file's SHA-256 to its container path |

### 2.1 Checksums

* **`project.json`** is verified on every load against `projectSha256`. A mismatch stops loading:
  "The project file is damaged: project.json does not match its checksum." (tested in
  `Damaged_and_future_files_are_refused_with_explanations`).
* **Raw files** are hashed while they are written; if a source no longer matches the SHA-256
  recorded at import, the save is aborted: "The raw file “…” changed since it was imported; the
  project was not saved." When a raw file is read from an opened container
  (`LoadedProject.Open`), its SHA-256 is recomputed and compared with `SourceFile.Sha256`; a
  mismatch raises "The embedded raw file “…” is damaged (its checksum does not match)."
* At load time, every `SourceFile` of the state must have its `raw/…` entry; otherwise "The project
  is incomplete: the raw file “…” is missing."
* `history.json`, snapshots and result blobs are not covered by a checksum. Result blobs carry a
  magic number and version (section 5).

---

## 3. Project state (`project.json`)

`project.json` is the serialised `ProjectState`:

| Property | Content |
|---|---|
| `schemaVersion` | Currently 1 (`ProjectState.CurrentSchemaVersion`) |
| `metadata` | Project name and descriptive fields (project number, client, site, survey date, …), creation time, creating application version |
| `survey` | Source files (name, SHA-256, length, format id, import time, original path), records with their trace headers and keywords as read from the files, survey lines (wave type, topography, spreads, shot gathers) |
| `scenarios` | Analysis scenarios; each holds per-line interpretations (processing pipeline, trace adjustments, picks, shot-time settings and entries, picker settings, segment assignment, layered settings, tomography settings and a reference to a stored tomography result, display settings) and scenario settings (engineering, density, Vs30, velocity classes, isolines) |
| `activeScenarioId` | The active scenario |
| `report` | Report settings |
| `snapshots` | Snapshot headers (id, name, creation time, note) |

Raw samples are never stored in JSON; they are decoded on demand from the embedded raw files.
Derived analysis results (observations, layered models, QC, Vs30, engineering values) are not stored
either: they are recomputed from the state. Only tomography results are persisted, because they are
expensive to compute (section 5).

### 3.1 JSON conventions (`DomainJson`)

* camelCase property names; dictionary keys are written unchanged;
* enums as camelCase strings (integer enum values are rejected when reading);
* strongly typed ids (`LineId`, `GatherId`, …) as 32-hex-digit GUID strings, also as dictionary keys;
  pick keys as `<gatherId>:<channel>`;
* properties whose value is null are omitted;
* NaN and ±Infinity are written as the named literals `"NaN"`, `"Infinity"`, `"-Infinity"`;
* `ImmutableArray` values are always written as arrays (`[]` when uninitialised), and `null` reads
  back as an empty array;
* indented output in the project file (`DomainJson.Options`), compact output in recovery checkpoints
  (`DomainJson.Compact`); no comments or trailing commas are accepted.

### 3.2 Schema versions and migrations

`ProjectMigrations.Upgrade` runs on `project.json` and on every snapshot when a project is loaded:

* `schemaVersion` greater than the version this build writes → refused: "This project was created by
  a newer version of Refraction Studio (schema N). Update the application to open it."
* `schemaVersion` missing or < 1 → refused: "This project uses an unknown schema version."
* Otherwise the JSON is deserialised and **normalised** (`ProjectMigrations.Normalize`): missing or
  null arrays in the survey (source files, records, lines, spreads, channels, gathers, members,
  topography points), the scenarios (engineering profile positions, density intervals, Vs30 positions
  and custom intervals, velocity classes, isoline velocities, pipeline steps, fixed layer
  velocities), the snapshot list and the report sections become empty arrays.

Schema 1 is the only schema so far; no version-to-version transformation exists yet. The method is
the place where future transformations are chained (the code comment shows the intended pattern), and
the notes it returns are exposed as `LoadedProject.MigrationNotes`.

---

## 4. Saving

### 4.1 Atomic save (`ProjectContainer.Save`)

1. A temporary file `.<target name>.<8 hex digits>.tmp` is created in the **same directory** as the
   target (`FileMode.CreateNew`, no sharing).
2. The complete container is written to it: raw files first (streamed from their current source and
   hashed), then `manifest.json`, `project.json`, `history.json`, snapshots and results.
3. The file stream is flushed to disk (`Flush(flushToDisk: true)`).
4. The temporary file replaces the target (`File.Move(temp, target, overwrite: true)`).

On any failure the temporary file is deleted and the previous project file is left untouched. I/O
and permission errors are reported as "The project could not be saved to “…”: <reason>". The test
`Save_and_load_round_trip_preserves_state_raw_bytes_and_history` checks that no `.tmp` file
remains, that the state round-trips exactly, that the history survives and that every embedded raw
file is byte-identical to the original.

### 4.2 What a save contains (`ProjectWorkspace.Document`, `Save`)

* the current state and the full history (a "Save project" history entry is recorded first);
* the snapshot states whose headers are still listed in the state;
* the serialised tomography results referenced by the current state or by any retained snapshot
  (`ResultStore.Export`);
* every raw file, read from the session's staging area, the previously opened container, or memory
  (`RawFileRepository`).

After saving, the workspace re-opens the new container so that raw files are served from the saved
copy, marks the session clean and clears the recovery checkpoint.

"Duplicate project…" on the Project page (dialog "Save a copy of the project") writes the same document to another path with
`ProjectContainer.Save` and keeps working on the original file.

### 4.3 Raw files are kept unchanged

Imported files are hashed (SHA-256) when they are read for the import preview. On import they are
copied unchanged into the session's recovery folder (section 6.2) and later into the container. No
step of the application modifies them: processing, trace adjustments, exclusions and geometry are
stored as interpretation data in `project.json`, and samples are always decoded from the original
bytes. Trace headers are stored as read (`TraceHeader` is never edited).

---

## 5. Results store (`results/<id>.bin`)

`ResultStore` keeps computed tomography results in memory by GUID. A line interpretation refers to
its result through `TomographyResultRef` (result id, input fingerprint, creation time, application
version, iterations, RMS, χ²). On load, blobs are kept as bytes and deserialised on first use; a
blob that cannot be decoded (wrong magic, unsupported version, truncated) yields "no result".

Binary layout (`TomographyResultSerializer`, .NET `BinaryWriter`: little-endian, strings with a
7-bit-encoded length prefix and UTF-8):

| Field | Type |
|---|---|
| Magic `0x52534D54` ("RSMT") | int32 |
| Version (1) | int32 |
| Grid: x0, dx, nx, zTop, dz, nz | double, double, int32, double, double, int32 |
| Active-cell mask (nx·nz) | bool each |
| Ground elevation per column (nx) | double each |
| Model velocity, starting-model velocity, coverage | length-prefixed double arrays |
| Hit counts | int32 count + int32 values |
| Depth of investigation | length-prefixed double array |
| Iterations: iteration, λ, RMS (s), χ², step length | count + (int32, 4 × double) |
| Residuals: pick key, observed, predicted | count + (string, double, double) |
| Rays: pick key, x[], z[] | count + (string, int32 n, n × double, n × double) |
| Settings | `TomographySettings` as compact JSON string |
| Input fingerprint | string |
| Notes | count + strings |
| Stop reason | string |
| Elapsed time | int64 ticks |

The input fingerprint lets the analysis detect that a stored result is stale after picks or
settings changed (`Tomography_result_survives_save_and_load_and_detects_staleness`).

---

## 6. Recovery checkpoints (outside the project)

### 6.1 Location and content

Per-user folder (`AppPaths`):
`%LOCALAPPDATA%\QuakeLogic\RefractionStudio\Recovery\<sessionId>\` (on other platforms the local
application-data folder of the user). Each editing session has its own folder:

```
state.json     ProjectState (compact JSON)
history.json   audit history
info.json      RecoveryInfo: session id, project name, saved project path (or null),
               checkpoint time, application version, process id
raw/<sha256>/<file name>   staged copies of imported raw files
```

Each JSON file is written atomically (`<name>.tmp`, then moved over the old file). Recovery data
never leaves the computer.

### 6.2 When checkpoints are written

* **Autosave**: a timer (`AutosaveService`) calls `ProjectWorkspace.Checkpoint()` every
  `AutosaveMinutes` (default 3; the Settings page clamps it to 1–60 minutes). A checkpoint is written
  only when the project has unsaved changes.
* **Crash**: the global crash handler (`src/RefractionStudio.Infrastructure/Diagnostics.cs`,
  `CrashHandler`) tries to write a checkpoint and records in the crash report whether it succeeded.
* **Import**: `PrepareImport` copies each new raw file into the session's `raw/` folder immediately
  (`RecoveryStore.StageRawFile`, idempotent, written via a `.tmp` file), so a project that was
  never saved can still be recovered with its data.

### 6.3 Clean-up

* After a successful **save**, only `state.json`, `history.json` and `info.json` are deleted; staged
  raw files are kept for the rest of the session.
* **Closing** the project deletes the whole session folder.
* Choosing **Discard** at start-up deletes that session's folder.

### 6.4 Recovery at start-up

At start-up (`MainWindowViewModel.StartupAsync`) every session folder with both `info.json` and
`state.json` whose process is no longer running (or is the current process) is offered, newest
first: "Refraction Studio recovered an unsaved project: “name” (last checkpoint …)." with the choices
**Recover**, **Discard** and **Later**. "Later" keeps the folder for the next start.

`ProjectWorkspace.Recover`:

1. loads and normalises the checkpoint state and history;
2. if the checkpoint refers to a saved project file that still exists, attaches it as a source of
   raw files and tomography results;
3. registers staged raw files from the session folder;
4. refuses recovery if a never-saved project refers to raw files that are not staged ("The recovered
   project refers to N raw file(s) that are no longer available.");
5. clears references to tomography results that are not available (results computed after the last
   save are not part of a checkpoint);
6. opens the state as a dirty session and records "Recovered unsaved changes after an unexpected
   exit" in the history.

The recovered project must be saved to keep the changes (`Never_saved_project_is_recovered_with_its_staged_raw_data`).

**Snapshots** are kept through recovery: the recovered session has the snapshot states as well as
their headers, so they can be restored after recovery and are written again (`snapshots/*.json`) on
the next save.

---

## 7. Archive

The Project page's **Archive project…** command (`ProjectWorkspace.Archive`) requires a saved project and
writes a ZIP (via a `.tmp` file) containing:

* the **saved** `.rfsx` file as it is on disk (unsaved changes are not included);
* `audit.json`: the machine-readable audit export of the current state, analysis and history
  (`AuditExport.Create`, `src/RefractionStudio.Reporting/Exports.cs`);
* `README.txt`: project name, creation time, application version and user name.

---

## 8. Per-user catalogue (not part of projects)

`catalog.db` in the per-user folder is a SQLite database (`AppCatalog`) with tables
`recent_projects` (up to 20 unpinned entries plus pinned ones), `spread_profiles`,
`report_templates` and `schema_info`. It stores no project data; deleting it loses only
conveniences. Two spread profiles are built in ("QuakeLogic 24-ch spread + shot geophone": channels
1–24 receivers at 2 m and channel 25 reference; "48-ch spread": 48 receivers at 2 m). User settings
are stored in `settings.json` in the same folder.
